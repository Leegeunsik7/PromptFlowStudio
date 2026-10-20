using System.Diagnostics;
using System.Globalization;
using System.Text;
using PromptFlowStudio.Services.Icvfx;
using PromptFlowStudio.Services.MediaPipeline;
using PromptFlowStudio.Services.Sound;

namespace PromptFlowStudio.Services.Synergy;

public enum ScreenTransition : byte { HardCut, MatchCut, SmashCut, Dissolve, FadeToBlack, JCut }
public enum ScreenSide : byte { Center, Left, Right }
public enum ConsistencyKind : byte { TimeOfDayMismatch, PaletteJump, WardrobeChange, CutTooShort, EmotionWhiplash }

/// <summary>캐릭터 연속성 고정 — 이름 해시 시드(결정적) · 최초 의상 룩 태그 · 등장 통계.</summary>
public sealed record DirectorCharacter(int Id, string Name, uint Seed, string? Wardrobe, int FirstShot, int Lines, int Shots)
{
    public string LookTag => "char:" + Name + "#" + Seed.ToString("x8", CultureInfo.InvariantCulture) + (Wardrobe is null ? "" : " · " + Wardrobe);
}

/// <summary>디렉팅된 숏 — 감정선(긴장/정서) · 숏 크기/렌즈 · 전환 · 출연 마스크 · 화면 방향(180° 규칙) · 색온도.</summary>
public readonly record struct DirectorShot(int Index, int Scene, double Start, double Duration, BeatKind Kind, ShotSize Size, int LensMm, ScreenTransition Transition,
    float Tension, float Valence, ulong Cast, int Speaker, ScreenSide Side, float Kelvin, TimeOfDay Time, bool Exterior, string Text);

/// <summary>OST 큐 — 시작(s) · 무드 · BPM · 단조 여부 · 강도 · 기점 숏.</summary>
public readonly record struct DirectorMusicCue(double Start, string Mood, int Bpm, bool Minor, float Intensity, int Shot);

public readonly record struct ContinuityIssue(int Shot, ConsistencyKind Kind, bool AutoFixed, string Detail);

public sealed record ScriptDirection(IReadOnlyList<DirectorShot> Shots, IReadOnlyList<DirectorCharacter> Cast, IReadOnlyList<DirectorMusicCue> Music, IReadOnlyList<FoleyEvent> Foley,
    IReadOnlyList<ContinuityIssue> Issues, int Scenes, double Duration, float ConsistencyScore, int SyncedFoley, double Millis)
{
    public int Count(ConsistencyKind k) { var n = 0; for (var i = 0; i < Issues.Count; i++) if (Issues[i].Kind == k) n++; return n; }
}

/// <summary>
/// [PromptFlow Studio v16.62.0] 🎞 지능형 자율 연출 &amp; 씬 조립 디렉터 강화기(Script-to-Screen).
///   ▸ 대본(INT/EXT/내부/외부/씬 헤딩 · "이름: 대사" · 지문) 2패스 → 캐릭터 레지스트리(FNV 시드 · 최초 의상 고정 룩 태그).
///   ▸ 감정선: v16.57 컨텍스트 튜너 에너지/정서가 + 비트 종류 바이어스 → 연속 아크 평활(급변 완화 보정) → 긴장 기반 컷 길이 · 숏 크기 · 렌즈.
///   ▸ 전환: 씬 경계(디졸브/페이드/스매시) · 긴장 급상승 스매시 · 급하강 디졸브 · 대사 교차 하드컷 · 출연 공유 크기 변화 매치컷 · 지문→대사 J컷.
///   ▸ 180° 규칙: 씬별 화자 좌/우 고정. OST: 긴장 대역(히스테리시스) 전환점 큐 · BPM/무드/조성 → 컷 길이를 박자 정수배로 스냅(컷 온 비트).
///   ▸ 폴리: v16.33 키워드 사전 → <see cref="FoleyEvent"/>(발자국 케이던스 · 충격 온 컷 · 번개 지연 천둥 · 환경음 베드 · 스매시 휙) — 반박자 그리드 동기.
///   ▸ 비주얼 일관성 검수: 시간대 불일치(씬 고정) · 색온도 점프(동기 없는 ±1200K 초과 클램프) · 씬 내 의상 변경 · 대사 판독 시간 부족(연장) · 감정 급변.
///   ▸ 재생 헤드 <see cref="Advance"/>/<see cref="CurrentTension"/> 무할당 — 시너지 버스(대본 긴장 소스) 공급.
/// </summary>
public sealed class EnhancedScriptToScreenDirector
{
    public const int MaxCast = 64;
    public const double FoleyGridTolerance = 0.001;

    public const string DemoScript =
        "EXT. 사막 협곡 - 골든아워\n" +
        "하린이 빨간 코트를 입고 협곡 가장자리로 천천히 걷는다. 바람이 분다.\n" +
        "하린: 여기가 마지막 신호가 잡힌 곳이야.\n" +
        "도윤: 조심해, 뭔가 이상해.\n" +
        "숨죽인 정적 속, 무언가 다가온다.\n" +
        "폭풍이 몰아치고 번개가 하늘을 가른다. 비가 쏟아진다.\n" +
        "거대한 탑이 폭발하며 붕괴한다! 하린이 빠르게 달리며 피한다.\n" +
        "하린이 파란 코트 차림으로 돌아본다.\n" +
        "INT. 기지 - 밤\n" +
        "문이 쾅 닫힌다. 도윤이 웃으며 숨을 고른다.\n" +
        "도윤: 살았다. 정말 살았어.\n" +
        "하린: 아직 끝난 게 아니야.\n" +
        "한낮의 태양이 창으로 쏟아진다.\n";

    private static readonly string[] Colors = { "빨간", "붉은", "파란", "푸른", "검은", "검정", "하얀", "흰", "노란", "초록", "회색", "red", "blue", "black", "white", "green", "grey", "gray" };
    private static readonly string[] Garments = { "코트", "셔츠", "드레스", "재킷", "자켓", "모자", "정장", "망토", "갑옷", "후드", "coat", "shirt", "dress", "jacket", "hat", "suit", "cloak", "hoodie" };

    private readonly object _gate = new();
    private readonly AutoPresetContextTuner _tuner;
    private double[] _start = Array.Empty<double>();
    private float[] _tension = Array.Empty<float>(), _valence = Array.Empty<float>();
    private double _duration, _playhead;
    private int _cur;
    private string _script = "";

    public EnhancedScriptToScreenDirector() : this((AutoPresetContextTuner?)null) { }
    public EnhancedScriptToScreenDirector(IServiceProvider sp) : this(sp.GetService(typeof(AutoPresetContextTuner)) as AutoPresetContextTuner) { }
    public EnhancedScriptToScreenDirector(AutoPresetContextTuner? tuner) => _tuner = tuner ?? new AutoPresetContextTuner();

    public event Action? OnChange;

    public ScriptDirection? Last { get; private set; }
    public string Script => _script;
    public string? LastAction { get; private set; }
    public int ShotCount => _start.Length;
    public int CurrentShot => _cur;
    public double Playhead => _playhead;
    public double Duration => _duration;
    public float CurrentTension => (uint)_cur < (uint)_tension.Length ? _tension[_cur] : 0.3f;
    public float CurrentValence => (uint)_cur < (uint)_valence.Length ? _valence[_cur] : 0.5f;

    public static string Label(ScreenTransition t) => t switch
    {
        ScreenTransition.HardCut => "컷", ScreenTransition.MatchCut => "매치컷", ScreenTransition.SmashCut => "스매시컷", ScreenTransition.Dissolve => "디졸브",
        ScreenTransition.FadeToBlack => "페이드", _ => "J컷",
    };

    public static string Label(ConsistencyKind k) => k switch
    {
        ConsistencyKind.TimeOfDayMismatch => "시간대 불일치", ConsistencyKind.PaletteJump => "색온도 점프", ConsistencyKind.WardrobeChange => "의상 변경",
        ConsistencyKind.CutTooShort => "대사 판독 부족", _ => "감정 급변",
    };

    /// <summary>FNV-1a(UTF-16) — 이름 → 결정적 캐릭터 시드.</summary>
    public static uint SeedOf(string name)
    {
        var h = 2166136261u;
        foreach (var ch in name) { h ^= ch; h *= 16777619u; }
        return h;
    }

    public static float KelvinFor(TimeOfDay t, bool exterior) => !exterior ? 3400f : t switch
    {
        TimeOfDay.Dawn => 4300f, TimeOfDay.GoldenHour => 3300f, TimeOfDay.Night => 7500f, TimeOfDay.Indoor => 3400f, _ => 5600f,
    };

    private static bool Has(string s, params string[] keys)
    {
        foreach (var k in keys) if (s.Contains(k, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsHeading(string line)
    {
        var u = line.ToUpperInvariant();
        return u.StartsWith("INT", StringComparison.Ordinal) || u.StartsWith("EXT", StringComparison.Ordinal) || line.StartsWith("S#", StringComparison.Ordinal)
            || line.StartsWith("내부", StringComparison.Ordinal) || line.StartsWith("외부", StringComparison.Ordinal) || line.StartsWith("씬", StringComparison.Ordinal);
    }

    private static string? Speaker(string line)
    {
        var i = line.IndexOf(':');
        if (i <= 0 || i >= 16) return null;
        var n = line[..i].Trim();
        return n.Length == 0 || n.Contains(' ') && n.Length > 12 ? null : n;
    }

    private static string? Wardrobe(string line)
    {
        foreach (var c in Colors)
        {
            var i = line.IndexOf(c, StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;
            foreach (var g in Garments)
            {
                var j = line.IndexOf(g, i + c.Length, StringComparison.OrdinalIgnoreCase);
                if (j >= 0 && j - i <= 12) return c + " " + g;
            }
        }
        return null;
    }

    private static int Band(float t, int prev) => prev switch
    {
        0 => t > 0.40f ? (t > 0.70f ? 2 : 1) : 0,
        2 => t < 0.60f ? (t < 0.30f ? 0 : 1) : 2,
        _ => t > 0.70f ? 2 : t < 0.30f ? 0 : 1,
    };

    private static string Mood(int band, float valence) => band switch
    {
        2 => valence < 0.45f ? "비장/위협" : "고양/승리",
        1 => valence < 0.45f ? "서스펜스" : "추진",
        _ => valence < 0.45f ? "애수" : "평온",
    };

    /// <summary>대본 분석 → 숏 리스트 · 캐스트 · OST · 폴리 · 일관성 리포트(재생 헤드 재설정).</summary>
    public ScriptDirection Analyze(string script)
    {
        var sw = Stopwatch.StartNew();
        var lines = (script ?? "").Replace("\r", "").Split('\n');
        // 1패스: 화자 등록
        var names = new List<string>();
        foreach (var raw in lines)
        {
            var l = raw.Trim();
            if (l.Length == 0 || IsHeading(l)) continue;
            if (Speaker(l) is { } sp && !names.Contains(sp) && names.Count < MaxCast) names.Add(sp);
        }
        var wardrobe = new string?[names.Count];
        var sceneWardrobe = new string?[names.Count];
        var firstShot = new int[names.Count]; Array.Fill(firstShot, -1);
        var lineCount = new int[names.Count];
        var shotCount = new int[names.Count];
        var sides = new ScreenSide[names.Count];

        var shots = new List<DirectorShot>();
        var music = new List<DirectorMusicCue>();
        var foley = new List<FoleyEvent>();
        var issues = new List<ContinuityIssue>();
        int scene = -1, band = -1;
        var exterior = true; var sceneTime = TimeOfDay.Unspecified;
        var t = 0.0; var arc = 0.3f; var beat = 60.0 / 100; var cueStart = 0.0;
        var newScene = true;
        DirectorShot? prev = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            string text;
            BeatKind kind;
            float energy, valence;
            TimeOfDay lineTime;
            if (IsHeading(line))
            {
                scene++; newScene = true;
                var u = line.ToUpperInvariant();
                exterior = u.StartsWith("EXT", StringComparison.Ordinal) || line.StartsWith("외부", StringComparison.Ordinal) || Has(line, "외부", "EXT");
                var hp = _tuner.Tune(line);
                sceneTime = hp.Time != TimeOfDay.Unspecified && hp.Time != TimeOfDay.Indoor ? hp.Time : Has(line, "NIGHT", "밤") ? TimeOfDay.Night : Has(line, "DAY", "낮") ? TimeOfDay.Day : TimeOfDay.Unspecified;
                Array.Clear(sceneWardrobe); Array.Clear(sides);
                text = line; kind = BeatKind.Establishing; energy = 0.25f; valence = 0.5f; lineTime = sceneTime;
            }
            else
            {
                if (scene < 0) { scene = 0; newScene = true; }
                var p = _tuner.Tune(line);
                text = line; energy = p.Energy; valence = p.Valence; lineTime = p.Time;
                var spk = Speaker(line);
                kind = Has(line, "폭발", "explod", "climax", "클라이맥스", "붕괴", "collapse") ? BeatKind.Climax
                    : Has(line, "드러나", "공개", "reveal", "나타난다", "모습을 드러") ? BeatKind.Reveal
                    : Has(line, "긴장", "숨죽", "다가온다", "tension", "creep", "정적 속", "이상해") ? BeatKind.Tension
                    : spk is not null ? BeatKind.Dialogue
                    : Has(line, "평온", "고요", "잔잔", "calm", "peaceful", "휴식", "숨을 고") ? BeatKind.Calm
                    : energy > 0.55f || Has(line, "달려", "뛰", "폭풍", "번개") ? BeatKind.Action : BeatKind.Calm;
            }
            var idx = shots.Count;
            var speaker = -1;
            var sp2 = kind == BeatKind.Dialogue || line.IndexOf(':') is > 0 and < 16 ? Speaker(line) : null;
            if (sp2 is not null) speaker = names.IndexOf(sp2);

            // 출연 마스크 · 의상 연속성
            ulong cast = 0;
            var look = kind == BeatKind.Establishing ? null : Wardrobe(line);
            for (var c = 0; c < names.Count; c++)
            {
                if (c != speaker && !line.Contains(names[c], StringComparison.Ordinal)) continue;
                cast |= 1UL << c;
                shotCount[c]++;
                if (firstShot[c] < 0) firstShot[c] = idx;
                if (c == speaker) lineCount[c]++;
                if (look is null || c == speaker) continue;
                if (sceneWardrobe[c] is { } w0 && !string.Equals(w0, look, StringComparison.Ordinal))
                    issues.Add(new ContinuityIssue(idx, ConsistencyKind.WardrobeChange, false, names[c] + " · " + w0 + " → " + look + "(씬 " + (scene + 1).ToString(CultureInfo.InvariantCulture) + ")"));
                sceneWardrobe[c] ??= look;
                wardrobe[c] ??= look;
            }

            // 감정선(긴장 아크 · 급변 완화)
            var bias = kind switch { BeatKind.Climax => 0.45f, BeatKind.Tension => 0.35f, BeatKind.Reveal => 0.25f, BeatKind.Action => 0.2f, BeatKind.Calm => -0.2f, BeatKind.Establishing => -0.1f, _ => 0f };
            if (Has(line, "공포", "비명", "위험", "조심", "fear", "scream")) bias += 0.15f;
            var raw0 = Math.Clamp(0.55f * energy + bias, 0f, 1f);
            var motivated = kind is BeatKind.Climax or BeatKind.Reveal || newScene;
            if (!motivated && MathF.Abs(raw0 - arc) > 0.5f) issues.Add(new ContinuityIssue(idx, ConsistencyKind.EmotionWhiplash, true, "긴장 " + arc.ToString("0.00", CultureInfo.InvariantCulture) + " → " + raw0.ToString("0.00", CultureInfo.InvariantCulture) + " 평활"));
            var tension = motivated ? raw0 : arc + (raw0 - arc) * 0.65f;
            if (kind != BeatKind.Establishing) arc = tension;

            // 시간대 · 색온도
            var time = sceneTime;
            if (kind != BeatKind.Establishing && lineTime != TimeOfDay.Unspecified && lineTime != TimeOfDay.Indoor && sceneTime != TimeOfDay.Unspecified && lineTime != sceneTime)
                issues.Add(new ContinuityIssue(idx, ConsistencyKind.TimeOfDayMismatch, true, "씬 " + sceneTime + " ↔ 지문 " + lineTime + " — 씬 시간대로 고정"));
            var kelvin = KelvinFor(time, exterior) - (valence - 0.5f) * 800f;
            var lightning = Has(line, "번개", "천둥", "lightning", "thunder", "폭발", "explod");
            if (lightning) kelvin = Math.Max(kelvin, 7000f);
            if (!newScene && prev is { } pv0 && !lightning && kind != BeatKind.Climax && MathF.Abs(kelvin - pv0.Kelvin) > 1200f)
            {
                var fixedK = pv0.Kelvin + MathF.CopySign(1200f, kelvin - pv0.Kelvin);
                issues.Add(new ContinuityIssue(idx, ConsistencyKind.PaletteJump, true, pv0.Kelvin.ToString("0", CultureInfo.InvariantCulture) + "K → " + kelvin.ToString("0", CultureInfo.InvariantCulture) + "K · " + fixedK.ToString("0", CultureInfo.InvariantCulture) + "K로 클램프"));
                kelvin = fixedK;
            }

            // OST 큐(긴장 대역 · 씬 경계)
            var nb = Band(tension, band < 0 ? 1 : band);
            if (newScene || nb != band || music.Count == 0)
            {
                band = nb;
                var bpm = 2 * (int)MathF.Round((70f + tension * 80f) / 2f);
                music.Add(new DirectorMusicCue(t, Mood(band, valence), bpm, valence < 0.45f, tension, idx));
                beat = 60.0 / bpm; cueStart = t;
            }

            // 컷 길이(긴장 ↑ 짧게 · 대사 판독 하한 · 박자 정수배)
            var dur = Math.Clamp((2.2 + text.Length / 22.0) * (1.35 - 0.7 * tension), 1.2, 9.0);
            if (kind == BeatKind.Establishing) dur = Math.Max(dur, 3.0);
            if (speaker >= 0)
            {
                var spoken = line.Length - line.IndexOf(':') - 1;
                var need = spoken / 9.0 + 0.6;
                if (dur < need) { issues.Add(new ContinuityIssue(idx, ConsistencyKind.CutTooShort, true, dur.ToString("0.0", CultureInfo.InvariantCulture) + "s → " + need.ToString("0.0", CultureInfo.InvariantCulture) + "s 연장")); dur = need; }
            }
            dur = Math.Max(1, Math.Ceiling(dur / beat - 1e-9)) * beat;

            // 숏 크기 · 렌즈 · 180° 화면 방향
            var size = kind switch
            {
                BeatKind.Establishing or BeatKind.Reveal or BeatKind.Calm => ShotSize.Wide,
                BeatKind.Climax => tension > 0.8f ? ShotSize.Close : ShotSize.Medium,
                BeatKind.Tension => ShotSize.Close,
                BeatKind.Dialogue => tension > 0.55f || prev is { Kind: BeatKind.Dialogue } ? ShotSize.Close : ShotSize.Medium,
                _ => ShotSize.Medium,
            };
            var lens = (size switch { ShotSize.Wide => 24, ShotSize.Medium => 35, _ => 85 }) + (int)(tension * 15f);
            var side = ScreenSide.Center;
            if (speaker >= 0)
            {
                if (sides[speaker] == ScreenSide.Center)
                {
                    var leftTaken = false; var rightTaken = false;
                    for (var c = 0; c < sides.Length; c++) { leftTaken |= sides[c] == ScreenSide.Left; rightTaken |= sides[c] == ScreenSide.Right; }
                    sides[speaker] = !leftTaken ? ScreenSide.Left : !rightTaken ? ScreenSide.Right : ScreenSide.Center;
                }
                side = sides[speaker];
            }

            // 전환
            ScreenTransition tr;
            if (prev is not { } pv) tr = ScreenTransition.FadeToBlack;
            else if (newScene) tr = (pv.Time == TimeOfDay.Night) != (time == TimeOfDay.Night) ? ScreenTransition.FadeToBlack : tension - pv.Tension > 0.35f ? ScreenTransition.SmashCut : ScreenTransition.Dissolve;
            else if (tension - pv.Tension > 0.3f) tr = ScreenTransition.SmashCut;
            else if (pv.Tension - tension > 0.3f) tr = ScreenTransition.Dissolve;
            else if (speaker >= 0 && pv.Speaker >= 0 && pv.Speaker != speaker) tr = ScreenTransition.HardCut;
            else if (speaker >= 0 && pv.Speaker < 0 && (pv.Cast & (1UL << speaker)) != 0) tr = ScreenTransition.JCut;
            else if ((pv.Cast & cast) != 0 && pv.Size != size) tr = ScreenTransition.MatchCut;
            else tr = ScreenTransition.HardCut;

            var shot = new DirectorShot(idx, Math.Max(0, scene), t, dur, kind, size, lens, tr, tension, valence, cast, speaker, side, kelvin, time, exterior, text);
            shots.Add(shot);

            // 폴리(반박자 그리드 스냅)
            var half = beat * 0.5;
            double Snap(double x) => cueStart + Math.Round((x - cueStart) / half) * half;
            var mask = AiGenerativeFoleySoundscapeEngine.DetectKeywords(line);
            var pan = side == ScreenSide.Left ? -0.4f : side == ScreenSide.Right ? 0.4f : 0f;
            for (var k = 0; k < 8; k++)
            {
                if (((int)mask & (1 << k)) == 0) continue;
                var fk = (FoleyKind)k;
                switch (fk)
                {
                    case FoleyKind.Footstep:
                        var run = Has(line, "달려", "달리", "뛰", "빠르게", "run");
                        var cadence = Math.Max(half, Math.Round((run ? 0.33 : 0.55) / half) * half);
                        for (var s = 0; s < 8 && s * cadence < dur - 0.05; s++) foley.Add(new FoleyEvent(fk, Snap(t + s * cadence), 0.12, run ? 0.8f : 0.55f, pan, run ? 0.9f : 0.5f));
                        break;
                    case FoleyKind.Impact:
                        foley.Add(new FoleyEvent(fk, t, 0.6, 0.9f, pan, Math.Max(0.5f, tension)));
                        break;
                    case FoleyKind.Wind or FoleyKind.Rain or FoleyKind.Crowd:
                        foley.Add(new FoleyEvent(fk, t, dur, 0.45f, 0f, 0.4f + tension * 0.5f));
                        break;
                    case FoleyKind.Whoosh:
                        foley.Add(new FoleyEvent(fk, Snap(t + Math.Max(0, dur - 0.25)), 0.35, 0.6f, pan, 0.7f));
                        break;
                    default:
                        foley.Add(new FoleyEvent(fk, Snap(t + 0.3), 0.4, 0.7f, pan, 0.6f));
                        break;
                }
            }
            if (Has(line, "번개", "천둥", "lightning", "thunder")) foley.Add(new FoleyEvent(FoleyKind.Impact, Snap(t + 0.4), 1.2, 0.8f, 0f, 0.8f));
            if (tr == ScreenTransition.SmashCut && idx > 0) foley.Add(new FoleyEvent(FoleyKind.Whoosh, Math.Max(0, t - half), 0.3, 0.5f, 0f, 0.6f));

            prev = shot;
            newScene = false;
            t += dur;
        }

        var synced = 0;
        for (var i = 0; i < foley.Count; i++)
        {
            var f = foley[i];
            var ci = 0;
            for (var m = 0; m < music.Count; m++) if (music[m].Start <= f.Start + 1e-9) ci = m;
            var half = 30.0 / Math.Max(1, music.Count > 0 ? music[ci].Bpm : 100);
            var rel = (f.Start - (music.Count > 0 ? music[ci].Start : 0)) / half;
            if (Math.Abs(rel - Math.Round(rel)) * half <= FoleyGridTolerance) synced++;
        }
        var cast2 = new List<DirectorCharacter>(names.Count);
        for (var c = 0; c < names.Count; c++) cast2.Add(new DirectorCharacter(c, names[c], SeedOf(names[c]), wardrobe[c], firstShot[c], lineCount[c], shotCount[c]));
        float penalty = 0;
        foreach (var i in issues) penalty += i.AutoFixed ? 2f : 8f;
        var dir = new ScriptDirection(shots, cast2, music, foley, issues, Math.Max(0, scene + 1), t, Math.Clamp(100f - penalty, 0f, 100f), synced, sw.Elapsed.TotalMilliseconds);

        var st = new double[shots.Count]; var te = new float[shots.Count]; var va = new float[shots.Count];
        for (var i = 0; i < shots.Count; i++) { st[i] = shots[i].Start; te[i] = shots[i].Tension; va[i] = shots[i].Valence; }
        lock (_gate)
        {
            _start = st; _tension = te; _valence = va; _duration = t; _playhead = 0; _cur = 0;
            _script = script ?? "";
            Last = dir;
        }
        LastAction = "🎞 " + shots.Count.ToString(CultureInfo.InvariantCulture) + "숏 · 캐스트 " + cast2.Count.ToString(CultureInfo.InvariantCulture) + " · OST " + music.Count.ToString(CultureInfo.InvariantCulture)
            + " · 폴리 " + foley.Count.ToString(CultureInfo.InvariantCulture) + " · 일관성 " + dir.ConsistencyScore.ToString("0", CultureInfo.InvariantCulture) + "점";
        Raise();
        return dir;
    }

    /// <summary>재생 헤드 진행(무할당 · 루프) — 현재 숏 인덱스 갱신.</summary>
    public void Advance(double dt)
    {
        lock (_gate)
        {
            if (_start.Length == 0 || _duration <= 0) return;
            _playhead += dt;
            if (_playhead >= _duration) { _playhead %= _duration; _cur = 0; }
            while (_cur + 1 < _start.Length && _start[_cur + 1] <= _playhead) _cur++;
        }
    }

    public void Seek(double t)
    {
        lock (_gate)
        {
            _playhead = _duration > 0 ? Math.Clamp(t, 0, _duration - 1e-6) : 0;
            _cur = 0;
            while (_cur + 1 < _start.Length && _start[_cur + 1] <= _playhead) _cur++;
        }
    }

    /// <summary>분석한 대본을 ICVFX 에이전틱 디렉터에 동일 적재(카메라/조명/VFX 연동).</summary>
    public int ApplyToIcvfx(AgenticAutonomousVirtualProductionDirector icvfx)
    {
        var n = icvfx.LoadScript(_script);
        LastAction = "🎬 ICVFX 디렉터 적재 · " + n.ToString(CultureInfo.InvariantCulture) + "비트";
        Raise();
        return n;
    }

    /// <summary>폴리 큐 → 스테레오 PCM(v16.33 절차 합성 재사용).</summary>
    public (float[] L, float[] R) RenderFoley(int rate = AiGenerativeFoleySoundscapeEngine.Rate)
    {
        var d = Last;
        if (d is null || d.Foley.Count == 0) return (Array.Empty<float>(), Array.Empty<float>());
        return AiGenerativeFoleySoundscapeEngine.RenderTrack(d.Foley, Math.Min(d.Duration, AiGenerativeFoleySoundscapeEngine.MaxSeconds), rate);
    }

    /// <summary>숏 리스트 내보내기(TSV).</summary>
    public string ExportShotList()
    {
        var d = Last;
        if (d is null) return "";
        var sb = new StringBuilder("#\t씬\t시작\t길이\t크기\t렌즈\t전환\t긴장\t색온도\t화면\t대본\n");
        foreach (var s in d.Shots)
        {
            sb.Append((s.Index + 1).ToString(CultureInfo.InvariantCulture)).Append('\t').Append((s.Scene + 1).ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(s.Start.ToString("0.00", CultureInfo.InvariantCulture)).Append('\t').Append(s.Duration.ToString("0.00", CultureInfo.InvariantCulture)).Append('\t')
              .Append(s.Size).Append('\t').Append(s.LensMm.ToString(CultureInfo.InvariantCulture)).Append("mm\t").Append(Label(s.Transition)).Append('\t')
              .Append(s.Tension.ToString("0.00", CultureInfo.InvariantCulture)).Append('\t').Append(s.Kelvin.ToString("0", CultureInfo.InvariantCulture)).Append("K\t")
              .Append(s.Side).Append('\t').Append(s.Text.Replace('\t', ' ')).Append('\n');
        }
        return sb.ToString();
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Synergy.Director.OnChange", ex); }
    }
}
