using System.Globalization;
using System.Text;
using PromptFlowStudio.Services.Screenplay;
using PromptFlowStudio.Services.Storyboard;
using PromptFlowStudio.Services.Virtual;

namespace PromptFlowStudio.Services.Cinema;

public enum ShotType : byte { Establishing, Master, TwoShot, Medium, OverTheShoulder, CloseUp, ExtremeCloseUp, Insert }

public enum SceneMood : byte { Neutral, Warm, Tense, Melancholy, Action, Mystery }

/// <summary>3점 조명(키 방위각 · 키:필 비 · 백라이트 · 색온도 K · 로우키 여부).</summary>
public readonly record struct ThreePointLight(float KeyAzimuth, float KeyFillRatio, float BackIntensity, int ColorTempK, bool LowKey);

/// <summary>블로킹 마크(스테이지 m · x 우 / z 전방).</summary>
public readonly record struct BlockingMark(string Character, float X, float Z);

public sealed record CinematicShot(
    int Index, int Scene, ShotType Type, int LensMm, double HorizontalFov, string Subject, string? Partner,
    float CamX, float CamZ, float CamYaw, CameraRigPreset Movement, ThreePointLight Light, double Seconds,
    IReadOnlyList<BlockingMark> Marks, string Prompt);

public sealed record ShotPlan(string Title, IReadOnlyList<CinematicShot> Shots, int AxisViolations, double TotalSeconds);

/// <summary>
/// [PromptFlow Studio v16.41.0] 🎬 AI 자율 감독 씬 블로킹 &amp; 시네마틱 샷 리스트 오케스트레이터.
///   ▸ 씬(헤딩 · 지문 · 대사 · 긴장도) → 분위기 판정 → 샷 문법: 첫 씬/EXT 설정샷(24mm) → 마스터(35mm) → 대화 OTS/리버스(50mm) → 감정 정점 클로즈업(85mm) · 클라이맥스 익스트림 CU(135mm) · 물건 인서트.
///   ▸ 블로킹: 인물 축선(180° 규칙) 배치 · 접근/이탈 동사 → 거리 변화 · 카메라는 항상 축선 한쪽(+z) — 축 위반 자동 검사.
///   ▸ 렌즈 → Super35(24.89mm) 수평 화각 · 분위기/긴장 → 3점 조명(로우키 8:1 ~ 하이키 2:1 · 색온도) · 카메라 무브(CameraRigPreset) · 영문 생성 프롬프트 · 스토리보드 컷 적용.
/// </summary>
public sealed class AutonomousSceneBlockingEngine
{
    public const double SensorWidthMm = 24.89;
    public const float AxisHalfGap = 0.8f;

    public event Action? OnChange;
    public ShotPlan? Current { get; private set; }
    public string? LastAction { get; private set; }

    /// <summary>수평 화각(도) = 2·atan(센서폭 / 2f).</summary>
    public static double HorizontalFov(int lensMm) => 2 * Math.Atan(SensorWidthMm / (2.0 * Math.Max(1, lensMm))) * 180 / Math.PI;

    public static int LensFor(ShotType t) => t switch
    {
        ShotType.Establishing => 24, ShotType.Master => 35, ShotType.TwoShot => 35, ShotType.Medium => 50, ShotType.OverTheShoulder => 50,
        ShotType.CloseUp => 85, ShotType.ExtremeCloseUp => 135, _ => 100,
    };

    public static string TypeLabel(ShotType t) => t switch
    {
        ShotType.Establishing => "설정샷", ShotType.Master => "마스터", ShotType.TwoShot => "투샷", ShotType.Medium => "미디엄",
        ShotType.OverTheShoulder => "OTS", ShotType.CloseUp => "클로즈업", ShotType.ExtremeCloseUp => "익스트림 CU", _ => "인서트",
    };

    private static readonly (string Word, SceneMood Mood)[] MoodWords =
    {
        ("싸운", SceneMood.Action), ("질주", SceneMood.Action), ("폭발", SceneMood.Action), ("fight", SceneMood.Action), ("chase", SceneMood.Action),
        ("숨을 거", SceneMood.Melancholy), ("눈물", SceneMood.Melancholy), ("이별", SceneMood.Melancholy), ("tears", SceneMood.Melancholy),
        ("배신", SceneMood.Tense), ("위협", SceneMood.Tense), ("총", SceneMood.Tense), ("threat", SceneMood.Tense),
        ("비밀", SceneMood.Mystery), ("그림자", SceneMood.Mystery), ("어둠", SceneMood.Mystery), ("shadow", SceneMood.Mystery),
        ("웃", SceneMood.Warm), ("포옹", SceneMood.Warm), ("사랑", SceneMood.Warm), ("smile", SceneMood.Warm),
    };

    public static SceneMood MoodOf(ScriptScene s)
    {
        Span<int> score = stackalloc int[6];
        foreach (var e in s.Elements)
        {
            var t = e.Text ?? "";
            foreach (var (w, m) in MoodWords) if (t.Contains(w, StringComparison.OrdinalIgnoreCase)) score[(int)m]++;
        }
        var best = 0;
        for (var i = 1; i < score.Length; i++) if (score[i] > score[best]) best = i;
        if (score[best] == 0) return s.Tension >= 70 ? SceneMood.Tense : SceneMood.Neutral;
        return (SceneMood)best;
    }

    /// <summary>분위기 · 긴장 → 3점 조명(무할당).</summary>
    public static ThreePointLight LightingFor(SceneMood mood, int tension, bool night)
    {
        var ratio = mood switch { SceneMood.Warm => 2f, SceneMood.Neutral => 3f, SceneMood.Action => 4f, SceneMood.Melancholy => 6f, SceneMood.Mystery => 8f, _ => 6f };
        ratio *= 1f + Math.Clamp(tension, 0, 100) / 200f;
        var temp = mood switch { SceneMood.Warm => 3200, SceneMood.Melancholy => 6500, SceneMood.Mystery => 7000, SceneMood.Tense => 5000, _ => 5600 };
        if (night) temp = Math.Max(temp, 6000);
        var az = mood is SceneMood.Mystery or SceneMood.Tense ? 75f : 45f;
        return new ThreePointLight(az, MathF.Round(ratio, 1), mood is SceneMood.Mystery or SceneMood.Action ? 0.9f : 0.6f, temp, ratio >= 6f);
    }

    private static CameraRigPreset MoveFor(ShotType t, SceneMood m, int tension) => t switch
    {
        ShotType.Establishing => m == SceneMood.Mystery ? CameraRigPreset.PanavisionReveal : CameraRigPreset.DroneRise,
        ShotType.Master => m == SceneMood.Action ? CameraRigPreset.GimbalFollow : CameraRigPreset.StaticLockOff,
        ShotType.CloseUp or ShotType.ExtremeCloseUp => tension >= 70 ? CameraRigPreset.DollyIn : CameraRigPreset.StaticLockOff,
        ShotType.Medium => m == SceneMood.Action ? CameraRigPreset.Handheld : CameraRigPreset.TruckRight,
        ShotType.Insert => CameraRigPreset.StaticLockOff,
        _ => m == SceneMood.Tense ? CameraRigPreset.Handheld : CameraRigPreset.StaticLockOff,
    };

    private static string MovePhrase(CameraRigPreset p) => p switch
    {
        CameraRigPreset.DollyIn => "slow dolly in", CameraRigPreset.DollyOut => "dolly out", CameraRigPreset.DroneRise => "aerial drone rise",
        CameraRigPreset.PanavisionReveal => "anamorphic reveal pan", CameraRigPreset.GimbalFollow => "gimbal follow", CameraRigPreset.Handheld => "handheld",
        CameraRigPreset.TruckRight => "lateral truck right", _ => "locked-off",
    };

    private static string ShotPhrase(ShotType t) => t switch
    {
        ShotType.Establishing => "wide establishing shot", ShotType.Master => "master shot", ShotType.TwoShot => "two-shot", ShotType.Medium => "medium shot",
        ShotType.OverTheShoulder => "over-the-shoulder shot", ShotType.CloseUp => "close-up", ShotType.ExtremeCloseUp => "extreme close-up", _ => "insert detail shot",
    };

    private static readonly string[] ApproachWords = { "다가간", "다가온", "approaches", "steps closer" };
    private static readonly string[] ExitWords = { "떠난", "나간다", "돌아선", "leaves", "exits" };

    /// <summary>대본 → 샷 리스트(씬별 블로킹 · 렌즈 · 조명 · 무브 · 축선 검증).</summary>
    public ShotPlan Plan(ScreenplayDoc doc, int maxShotsPerScene = 8)
    {
        var shots = new List<CinematicShot>(doc.Scenes.Count * 5);
        var axisViolations = 0;
        for (var si = 0; si < doc.Scenes.Count; si++)
        {
            var sc = doc.Scenes[si];
            var mood = MoodOf(sc);
            var night = sc.Heading.Contains("밤", StringComparison.Ordinal) || sc.Heading.Contains("NIGHT", StringComparison.OrdinalIgnoreCase);
            var light = LightingFor(mood, sc.Tension, night);
            var cast = new List<string>(4);
            foreach (var e in sc.Elements) if (e.Kind == ScriptElementKind.Dialogue && !string.IsNullOrWhiteSpace(e.Character) && !cast.Contains(e.Character!)) cast.Add(e.Character!);
            // 블로킹: 축선(x) 위 좌우 배치 · 접근/이탈 지문 반영
            var gap = AxisHalfGap;
            foreach (var e in sc.Elements)
            {
                if (e.Kind != ScriptElementKind.Action) continue;
                foreach (var w in ApproachWords) if (e.Text.Contains(w, StringComparison.OrdinalIgnoreCase)) gap = Math.Max(0.35f, gap - 0.3f);
                foreach (var w in ExitWords) if (e.Text.Contains(w, StringComparison.OrdinalIgnoreCase)) gap += 0.9f;
            }
            var marks = new List<BlockingMark>(cast.Count);
            for (var c = 0; c < cast.Count; c++)
            {
                var side = c % 2 == 0 ? -1f : 1f;
                marks.Add(new BlockingMark(cast[c], side * (gap + c / 2 * 0.6f), c / 2 * -0.8f));
            }
            var sceneShots = new List<(ShotType T, string Subject, string? Partner, string Text)>();
            var location = CinematicUniverseTimelineEngine.LocationOf(sc.Heading);
            if (si == 0 || sc.Heading.StartsWith("EXT", StringComparison.OrdinalIgnoreCase) || sc.Heading.StartsWith("외부", StringComparison.Ordinal))
                sceneShots.Add((ShotType.Establishing, location, null, location));
            sceneShots.Add((cast.Count >= 2 ? ShotType.Master : cast.Count == 1 ? ShotType.Medium : ShotType.Master, cast.Count > 0 ? cast[0] : location, cast.Count > 1 ? cast[1] : null, Summary(sc)));
            string? prevSpeaker = null;
            foreach (var e in sc.Elements)
            {
                if (sceneShots.Count >= maxShotsPerScene) break;
                if (e.Kind == ScriptElementKind.Action && e.Text.Contains("[[GAIN", StringComparison.Ordinal) | e.Text.Contains("[[USE", StringComparison.Ordinal))
                {
                    sceneShots.Add((ShotType.Insert, ItemOf(e.Text), null, e.Text));
                    continue;
                }
                if (e.Kind != ScriptElementKind.Dialogue || e.Character is null) continue;
                var partner = PartnerOf(cast, e.Character);
                var peak = sc.Tension >= 85 && e.Text.Contains('!');
                var emotional = peak || sc.Tension >= 70 || e.Text.Contains('!') || !string.IsNullOrEmpty(e.Parenthetical);
                ShotType type;
                if (peak) type = ShotType.ExtremeCloseUp;
                else if (emotional) type = ShotType.CloseUp;
                else if (partner is not null && prevSpeaker != e.Character) type = ShotType.OverTheShoulder;
                else type = ShotType.Medium;
                sceneShots.Add((type, e.Character, partner, e.Text));
                prevSpeaker = e.Character;
            }
            // 시간 배분(대사 길이 가중 · 설정/마스터 최소 2초)
            var sceneSec = sc.Seconds > 0 ? sc.Seconds : Math.Max(4, sceneShots.Count * 3);
            double weight = 0;
            foreach (var x in sceneShots) weight += x.T is ShotType.Establishing or ShotType.Master ? 3 : Math.Max(1.5, x.Text.Length / 12.0);
            var lastCamSide = 0;
            foreach (var x in sceneShots)
            {
                var w = x.T is ShotType.Establishing or ShotType.Master ? 3 : Math.Max(1.5, x.Text.Length / 12.0);
                var lens = LensFor(x.T);
                var (cx, cz, yaw) = CameraFor(x.T, x.Subject, x.Partner, marks);
                var camSide = cz >= 0 ? 1 : -1;
                if (lastCamSide != 0 && camSide != lastCamSide) axisViolations++;
                lastCamSide = camSide;
                var move = MoveFor(x.T, mood, sc.Tension);
                var prompt = BuildPrompt(x.T, lens, move, light, mood, x.Subject, x.Partner, location, night);
                shots.Add(new CinematicShot(shots.Count + 1, sc.Number, x.T, lens, Math.Round(HorizontalFov(lens), 1), x.Subject, x.Partner, cx, cz, yaw, move, light,
                    Math.Round(sceneSec * w / weight, 2), marks, prompt));
            }
        }
        double total = 0; foreach (var s in shots) total += s.Seconds;
        var plan = new ShotPlan(doc.Title, shots, axisViolations, Math.Round(total, 2));
        Current = plan;
        LastAction = "🎬 샷 리스트 " + shots.Count.ToString(CultureInfo.InvariantCulture) + "컷 · 축선 위반 " + axisViolations.ToString(CultureInfo.InvariantCulture);
        Raise();
        return plan;
    }

    private static string Summary(ScriptScene sc)
    {
        foreach (var e in sc.Elements) if (e.Kind == ScriptElementKind.Action && e.Text.Length > 0) return e.Text;
        return sc.Heading;
    }

    private static string ItemOf(string text)
    {
        var p = text.IndexOf(':', text.IndexOf("[[", StringComparison.Ordinal) + 2);
        var e = p < 0 ? -1 : text.IndexOf("]]", p, StringComparison.Ordinal);
        return p >= 0 && e > p ? text[(p + 1)..e].Trim() : "prop";
    }

    private static string? PartnerOf(List<string> cast, string who)
    {
        foreach (var c in cast) if (c != who) return c;
        return null;
    }

    /// <summary>샷 타입 · 피사체/상대 마크 → 카메라 위치(항상 축선 +z 측 · 180° 규칙).</summary>
    public static (float X, float Z, float Yaw) CameraFor(ShotType t, string subject, string? partner, IReadOnlyList<BlockingMark> marks)
    {
        BlockingMark? s = null, p = null;
        foreach (var m in marks) { if (m.Character == subject) s = m; else if (m.Character == partner) p = m; }
        var sx = s?.X ?? 0f; var sz = s?.Z ?? 0f;
        var dist = t switch { ShotType.Establishing => 9f, ShotType.Master or ShotType.TwoShot => 5f, ShotType.Medium => 2.6f, ShotType.OverTheShoulder => 2.2f, ShotType.CloseUp => 1.3f, ShotType.ExtremeCloseUp => 0.8f, _ => 0.6f };
        if (t == ShotType.OverTheShoulder && p is { } pm)
        {
            // 상대 어깨 너머(상대 쪽 뒤 · +z 측 유지) → 피사체를 바라봄
            var cx = pm.X + (pm.X - sx) * 0.35f;
            var cz = MathF.Max(0.4f, pm.Z + 0.6f);
            return (cx, cz, MathF.Atan2(sx - cx, cz - sz) * 180f / MathF.PI);
        }
        if (t is ShotType.Establishing or ShotType.Master or ShotType.TwoShot) return (0f, dist, 0f);
        var ox = p is { } q ? (q.X - sx) * 0.25f : 0f;
        return (sx + ox, sz + dist, MathF.Atan2(-ox, dist) * 180f / MathF.PI);
    }

    public static string BuildPrompt(ShotType t, int lens, CameraRigPreset move, ThreePointLight l, SceneMood mood, string subject, string? partner, string location, bool night)
    {
        var sb = new StringBuilder(220);
        sb.Append("Cinematic ").Append(ShotPhrase(t)).Append(", ").Append(lens.ToString(CultureInfo.InvariantCulture)).Append("mm lens, ").Append(MovePhrase(move)).Append(" camera, ");
        if (t == ShotType.OverTheShoulder && partner is not null) sb.Append("over ").Append(partner).Append("'s shoulder onto ").Append(subject);
        else if (t == ShotType.Establishing) sb.Append(location);
        else sb.Append(subject).Append(partner is not null && t is ShotType.Master or ShotType.TwoShot ? " and " + partner : "");
        sb.Append(" in ").Append(location).Append(night ? " at night" : "").Append(", ");
        sb.Append(l.LowKey ? "low-key " : "high-key ").Append("three-point lighting ").Append(l.KeyFillRatio.ToString("0.#", CultureInfo.InvariantCulture)).Append(":1 key-to-fill, ")
          .Append(l.ColorTempK.ToString(CultureInfo.InvariantCulture)).Append("K, ").Append(mood.ToString().ToLowerInvariant()).Append(" mood, shallow depth of field, film grain");
        return sb.ToString();
    }

    /// <summary>샷 리스트 → 스토리보드 컷(프롬프트 · 대사 보이스 · 긴장도). 반환 = 추가 컷 수.</summary>
    public int ApplyToStoryboard(StoryboardProject project, ShotPlan plan, bool replace = true)
    {
        if (replace) project.Scenes.Clear();
        foreach (var s in plan.Shots)
            project.Scenes.Add(new StoryboardScene { Prompt = s.Prompt, PresetTension = (int)Math.Round(s.Light.KeyFillRatio * 10), StatusText = "대기" });
        LastAction = "📋 스토리보드 컷 " + plan.Shots.Count.ToString(CultureInfo.InvariantCulture) + "개 적용";
        Raise();
        return plan.Shots.Count;
    }

    private void Raise() { try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("Blocking.OnChange", ex); } }
}
