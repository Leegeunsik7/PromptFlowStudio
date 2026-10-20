using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PromptFlowStudio.Services.Engines;
using PromptFlowStudio.Services.Media;
using PromptFlowStudio.Services.Sound;

namespace PromptFlowStudio.Services.Music;

[JsonConverter(typeof(JsonStringEnumConverter<MusicGenre>))]
public enum MusicGenre { KPop, HipHop, Edm, Ballad, Lofi, Rock }

public enum SectionKind { Intro, Verse, PreChorus, Chorus, Drop, Outro }

/// <summary>곡 구간(마디 단위) — 에너지 0~1.</summary>
public sealed record MusicSection(SectionKind Kind, int StartBar, int Bars, float Energy);

/// <summary>가사 큐(줄 단위 · 첫/끝 음절 시각 · 대표 음높이).</summary>
public sealed record LyricCue(string Text, double StartSec, double EndSec, int Midi);

/// <summary>음악 생성 요청 — 장르 · 가사 · 무드 · 길이 · BPM(0 = 장르 기본) · 시드 · 원격 허용 · 샘플레이트 · 보컬.</summary>
public sealed record MusicRequest(MusicGenre Genre, string? Lyrics, string? Mood, double Seconds = 30, int Bpm = 0, int Seed = 0, bool AllowRemote = true, int Rate = 44100, bool Vocals = true);

/// <summary>생성 트랙(스테레오 float · 구간 · 가사 큐 · 소스).</summary>
public sealed class MusicTrack
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..10];
    public MusicGenre Genre { get; init; }
    public string Mood { get; init; } = "";
    public int Bpm { get; init; }
    public string KeyName { get; init; } = "";
    public bool Minor { get; init; }
    public int Rate { get; init; }
    public float[] Left { get; init; } = Array.Empty<float>();
    public float[] Right { get; init; } = Array.Empty<float>();
    public IReadOnlyList<MusicSection> Sections { get; init; } = Array.Empty<MusicSection>();
    public IReadOnlyList<LyricCue> Lyrics { get; init; } = Array.Empty<LyricCue>();
    public int VocalNotes { get; init; }
    public string Source { get; init; } = "";
    public string Prompt { get; init; } = "";
    public string? Path { get; set; }
    public double Lufs { get; set; }
    public float PeakDb { get; set; }
    public double Seconds => Rate == 0 ? 0 : Left.Length / (double)Rate;
}

/// <summary>원격 생성형 음악 API(Suno/Udio 호환 게이트웨이) — 환경변수 · 시크릿 하드코딩 금지.</summary>
public sealed record MusicApiConfig(string? Endpoint, string? ApiKey, string Provider)
{
    public const string EndpointVar = "PFS_MUSIC_ENDPOINT", KeyVar = "PFS_MUSIC_API_KEY", ProviderVar = "PFS_MUSIC_PROVIDER";

    public bool IsConfigured => Uri.TryCreate(Endpoint, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.IsLoopback);

    public static MusicApiConfig FromEnvironment()
    {
        var p = Environment.GetEnvironmentVariable(ProviderVar);
        return new MusicApiConfig(Environment.GetEnvironmentVariable(EndpointVar), Environment.GetEnvironmentVariable(KeyVar),
            string.Equals(p, "udio", StringComparison.OrdinalIgnoreCase) ? "udio" : "suno");
    }
}

/// <summary>
/// [PromptFlow Studio v16.37.0] 🎵 AI 제너레이티브 뮤직 &amp; 보컬 합성 엔진.
///   ▸ 원격: Suno/Udio 호환 게이트웨이(POST /generate → GET /status/{id} 폴링 → 오디오 다운로드) · 타임아웃 · 지수 백오프 · 응답 스키마 즉시 검증 ·
///     WAV 직접 디코드(그 외 포맷은 ffmpeg 변환) · 실패 시 즉시 BCL 폴백.
///   ▸ BCL 절차적 신디사이저(결정적 · 시드): 장르 프로필(BPM · 장/단조 · 코드 진행 · 16스텝 드럼 · 스윙) + 무드 보정 → 구간 편곡(인트로/벌스/프리/코러스/드랍/아웃트로) →
///     킥/스네어/하이햇 · 베이스(서브) · 패드 · 아르페지오 · 라이저(드랍 직전 무음 갭) · 포먼트 보컬(한글 중성/라틴 모음 → F1~F3 · 비브라토 · 자음 버스트) → 리미터.
///   ▸ 가사 → 음절 → 코드톤 멜로디 · 줄 단위 큐 · float32 WAV(미디어 루트 → WebView 재생).
/// </summary>
public sealed class AiGenerativeMusicEngine : IDisposable
{
    public static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(60);
    public const double MinSeconds = 6, MaxSeconds = 240;

    private readonly HttpClient _http;
    private readonly Func<MusicApiConfig> _config;
    private readonly Func<string?> _ffmpeg;
    private readonly string _folder;
    private readonly TimeSpan _poll, _deadline;
    private readonly object _gate = new();
    private readonly List<MusicTrack> _tracks = new();
    private int _busy;

    public AiGenerativeMusicEngine(IServiceProvider sp)
        : this(MusicApiConfig.FromEnvironment,
               sp.GetService(typeof(IVideoMergeService)) is IVideoMergeService m ? () => m.ResolvedFfmpegPath : () => null,
               sp.GetService(typeof(ILocalMediaService)) is ILocalMediaService media ? System.IO.Path.Combine(media.MediaRoot, "music") : null) { }

    public AiGenerativeMusicEngine(Func<MusicApiConfig> config, Func<string?> ffmpeg, string? folder = null, HttpMessageHandler? handler = null, TimeSpan? poll = null, TimeSpan? deadline = null)
    {
        _config = config;
        _ffmpeg = ffmpeg;
        _folder = folder ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", "Media", "music");
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = HttpTimeout;
        _poll = poll ?? TimeSpan.FromSeconds(3);
        _deadline = deadline ?? TimeSpan.FromMinutes(4);
    }

    public event Action? OnChange;

    public string Folder => _folder;
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public bool RemoteConfigured { get { try { return _config().IsConfigured; } catch (Exception ex) { Program.Log("Music.Config", ex); return false; } } }
    public string Provider { get { try { return _config().Provider; } catch { return "suno"; } } }
    public MusicTrack? Current { get; private set; }
    public string? LastAction { get; private set; }
    public long RemoteOk { get; private set; }
    public long Fallbacks { get; private set; }
    public IReadOnlyList<MusicTrack> Tracks { get { lock (_gate) return _tracks.ToArray(); } }

    // ── 장르 프로필 ───────────────────────────────────────────────────────────

    private readonly record struct GenreProfile(int Bpm, bool Minor, int[] Prog, string Kick, string Snare, string Hat, float Swing, bool Drop, string Tags);

    private static readonly int[] MajorScale = { 0, 2, 4, 5, 7, 9, 11 }, MinorScale = { 0, 2, 3, 5, 7, 8, 10 };
    private static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    private static GenreProfile ProfileOf(MusicGenre g) => g switch
    {
        MusicGenre.KPop => new(120, false, new[] { 0, 4, 5, 3 }, "x...x...x...x...", "....x.......x...", "x.x.x.x.x.x.x.x.", 0, true, "k-pop, catchy hook, polished pop production, bright synths"),
        MusicGenre.HipHop => new(90, true, new[] { 0, 5, 2, 6 }, "x......x..x.....", "....x.......x...", "xxxxxxxxxxxxxxxx", 0.18f, false, "hip-hop, boom bap groove, deep 808 bass, swung hats"),
        MusicGenre.Edm => new(128, true, new[] { 0, 5, 2, 6 }, "x...x...x...x...", "....x.......x...", "..x...x...x...x.", 0, true, "edm, festival big room, supersaw lead, heavy drop"),
        MusicGenre.Ballad => new(72, false, new[] { 0, 4, 5, 3 }, "x.......x.......", "....x.......x...", "x...x...x...x...", 0, false, "emotional ballad, warm piano pads, soft drums"),
        MusicGenre.Lofi => new(82, false, new[] { 0, 5, 3, 4 }, "x.....x...x.....", "....x.......x...", "x.x.x.x.x.x.x.x.", 0.22f, false, "lo-fi chill hop, dusty drums, mellow chords"),
        _ => new(140, false, new[] { 0, 3, 4, 0 }, "x.x.....x.x.....", "....x.......x...", "x.x.x.x.x.x.x.x.", 0, false, "rock, driving drums, distorted power chords"),
    };

    public static string GenreLabel(MusicGenre g) => g switch
    {
        MusicGenre.KPop => "K-Pop", MusicGenre.HipHop => "힙합", MusicGenre.Edm => "EDM", MusicGenre.Ballad => "발라드", MusicGenre.Lofi => "로파이", _ => "록",
    };

    public static int DefaultBpm(MusicGenre g) => ProfileOf(g).Bpm;

    /// <summary>텍스트 → 장르(미검출 null).</summary>
    public static MusicGenre? DetectGenre(string? text)
    {
        var t = (text ?? "").ToLowerInvariant();
        if (t.Contains("k-pop") || t.Contains("kpop") || t.Contains("케이팝") || t.Contains("아이돌")) return MusicGenre.KPop;
        if (t.Contains("hip") || t.Contains("힙합") || t.Contains("랩") || t.Contains("rap")) return MusicGenre.HipHop;
        if (t.Contains("edm") || t.Contains("일렉") || t.Contains("house") || t.Contains("드랍")) return MusicGenre.Edm;
        if (t.Contains("ballad") || t.Contains("발라드")) return MusicGenre.Ballad;
        if (t.Contains("lofi") || t.Contains("lo-fi") || t.Contains("로파이")) return MusicGenre.Lofi;
        if (t.Contains("rock") || t.Contains("록") || t.Contains("밴드")) return MusicGenre.Rock;
        return null;
    }

    /// <summary>무드 보정 — (단조 강제 여부, BPM 가감, 에너지 배율).</summary>
    public static (bool? Minor, int BpmDelta, float Energy) MoodOf(string? mood)
    {
        var m = (mood ?? "").ToLowerInvariant();
        if (m.Contains("sad") || m.Contains("슬") || m.Contains("melanch") || m.Contains("이별") || m.Contains("그리")) return (true, -8, 0.8f);
        if (m.Contains("dark") || m.Contains("어두") || m.Contains("긴장") || m.Contains("tense")) return (true, 0, 1f);
        if (m.Contains("chill") || m.Contains("잔잔") || m.Contains("calm") || m.Contains("편안")) return (null, -10, 0.75f);
        if (m.Contains("happy") || m.Contains("신나") || m.Contains("energ") || m.Contains("밝") || m.Contains("행복")) return (false, 6, 1.1f);
        return (null, 0, 1f);
    }

    public static string BuildPrompt(MusicRequest r)
    {
        var p = ProfileOf(r.Genre);
        var bpm = r.Bpm > 0 ? r.Bpm : p.Bpm + MoodOf(r.Mood).BpmDelta;
        return p.Tags + ", " + (string.IsNullOrWhiteSpace(r.Mood) ? "" : r.Mood!.Trim() + " mood, ") + bpm.ToString(CultureInfo.InvariantCulture) + " bpm" + (r.Vocals ? ", expressive lead vocal" : ", instrumental");
    }

    // ── 편곡 ────────────────────────────────────────────────────────────────

    /// <summary>총 마디 → 구간(인트로 2 · [벌스 4 · 프리 2 · 코러스 4 · 드랍 4]* · 아웃트로 2 · 잔여는 마지막 본문 구간 연장).</summary>
    public static List<MusicSection> Arrange(int totalBars, bool drop, float energy = 1f)
    {
        var list = new List<MusicSection>();
        totalBars = Math.Max(4, totalBars);
        var bar = 0;
        void Add(SectionKind k, int bars, float e)
        {
            bars = Math.Min(bars, totalBars - bar);
            if (bars <= 0) return;
            list.Add(new MusicSection(k, bar, bars, Math.Clamp(e * energy, 0.1f, 1f)));
            bar += bars;
        }
        var outro = totalBars >= 12 ? 2 : 0;
        Add(SectionKind.Intro, totalBars >= 8 ? 2 : 1, 0.4f);
        while (bar < totalBars - outro)
        {
            var before = bar;
            Add(SectionKind.Verse, Math.Min(4, totalBars - outro - bar), 0.6f);
            Add(SectionKind.PreChorus, Math.Min(2, totalBars - outro - bar), 0.72f);
            Add(SectionKind.Chorus, Math.Min(4, totalBars - outro - bar), 0.88f);
            if (drop) Add(SectionKind.Drop, Math.Min(4, totalBars - outro - bar), 1f);
            if (bar == before) break;
        }
        Add(SectionKind.Outro, totalBars - bar, 0.4f);
        return list;
    }

    // ── 가사 → 음절 ─────────────────────────────────────────────────────────

    /// <summary>음절(줄 · 텍스트 · 모음 0~6[a e i o u ʌ ɯ] · 초성 자음 여부).</summary>
    public readonly record struct Syllable(int Line, string Text, int Vowel, bool Consonant);

    private static readonly int[] JungToVowel = { 0, 1, 0, 1, 5, 1, 5, 1, 3, 0, 1, 1, 3, 4, 5, 1, 2, 4, 6, 2, 2 };

    /// <summary>모음 포먼트(F1 · F2 · F3 Hz).</summary>
    public static readonly (float F1, float F2, float F3)[] Formants =
    {
        (800, 1200, 2500), (500, 1900, 2600), (300, 2300, 3000), (450, 800, 2600), (325, 700, 2500), (600, 1000, 2500), (350, 1400, 2500),
    };

    public static List<Syllable> Syllabify(string? lyrics, out List<string> lines)
    {
        lines = new List<string>();
        var syl = new List<Syllable>();
        if (string.IsNullOrWhiteSpace(lyrics)) return syl;
        foreach (var raw in lyrics.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('[')) continue;   // [Chorus] 등 메타 태그 제외
            var li = lines.Count;
            lines.Add(line);
            var i = 0;
            while (i < line.Length)
            {
                var c = line[i];
                if (c is >= '가' and <= '힣')
                {
                    var idx = c - 0xAC00;
                    syl.Add(new Syllable(li, c.ToString(), JungToVowel[idx % 588 / 28], idx / 588 != 11));
                    i++;
                    continue;
                }
                if (char.IsLetter(c))
                {
                    var start = i;
                    while (i < line.Length && char.IsLetter(line[i]) && line[i] < 'ᄀ') i++;
                    var word = line[start..i];
                    var groups = 0; var prevV = false; var firstV = -1;
                    for (var k = 0; k < word.Length; k++)
                    {
                        var v = LatinVowel(word[k]);
                        if (v >= 0 && !prevV) { groups++; if (firstV < 0) firstV = v; }
                        prevV = v >= 0;
                    }
                    groups = Math.Max(1, groups);
                    for (var g = 0; g < groups; g++) syl.Add(new Syllable(li, g == 0 ? word : "", firstV < 0 ? 6 : firstV, g == 0 && LatinVowel(word[0]) < 0));
                    continue;
                }
                i++;
            }
        }
        return syl;
    }

    private static int LatinVowel(char c) => char.ToLowerInvariant(c) switch { 'a' => 0, 'e' => 1, 'i' or 'y' => 2, 'o' => 3, 'u' => 4, _ => -1 };

    // ── 신디사이저(결정적 · 순수) ────────────────────────────────────────────

    /// <summary>요청 → 트랙(파일 미기록 · 결정적).</summary>
    public static MusicTrack Synthesize(MusicRequest r)
    {
        var prof = ProfileOf(r.Genre);
        var (moodMinor, bpmDelta, moodEnergy) = MoodOf(r.Mood);
        var minor = moodMinor ?? prof.Minor;
        var bpm = Math.Clamp(r.Bpm > 0 ? r.Bpm : prof.Bpm + bpmDelta, 50, 200);
        var rate = Math.Clamp(r.Rate, 8000, 96000);
        var seconds = Math.Clamp(r.Seconds, MinSeconds, MaxSeconds);
        var seed = r.Seed != 0 ? r.Seed : StableHash((r.Lyrics ?? "") + "|" + r.Mood + "|" + r.Genre);
        var rng = new XorShift32(seed);
        var root = (minor ? 57 : 60) + (int)(rng.Next01() * 5) - 2;     // A3/C4 ± 2
        var scale = minor ? MinorScale : MajorScale;
        var spb = 60.0 / bpm;
        var barSec = spb * 4;
        var totalBars = Math.Max(4, (int)Math.Round(seconds / barSec));
        var sections = Arrange(totalBars, prof.Drop, moodEnergy);
        var n = (int)(totalBars * barSec * rate) + rate / 2;
        var l = new float[n];
        var rr = new float[n];
        var syl = Syllabify(r.Lyrics, out var lines);
        var cues = new List<LyricCue>();
        var sylIdx = 0; var vocalNotes = 0;
        int cueLine = -1; double cueStart = 0, cueEnd = 0; var cueMidi = 0;
        var melody = 2;   // 코드톤 인덱스(0~5)

        foreach (var sec in sections)
        {
            for (var b = sec.StartBar; b < sec.StartBar + sec.Bars; b++)
            {
                var t0 = b * barSec;
                var e = sec.Energy;
                var degree = prof.Prog[b % prof.Prog.Length];
                var nextIsDrop = b == sec.StartBar + sec.Bars - 1 && NextKind(sections, sec) == SectionKind.Drop;
                // 패드(드랍 = 얇게)
                for (var k = 0; k < 3; k++)
                {
                    var midi = NoteOf(root, scale, degree + k * 2);
                    Pad(l, rr, rate, t0, barSec, Hz(midi), (sec.Kind == SectionKind.Drop ? 0.03f : 0.05f) * e, (k - 1) * 0.35f, ref rng);
                }
                // 베이스
                if (sec.Kind != SectionKind.Intro)
                {
                    var bassMidi = NoteOf(root - 12, scale, degree);
                    var eighth = sec.Kind is SectionKind.Chorus or SectionKind.Drop;
                    var steps = eighth ? 8 : 4;
                    for (var s = 0; s < steps; s++)
                    {
                        if (nextIsDrop && s >= steps * 3 / 4) break;
                        Bass(l, rr, rate, t0 + s * barSec / steps, barSec / steps * 0.9, Hz(bassMidi), 0.22f * e, sec.Kind == SectionKind.Drop);
                    }
                }
                // 드럼(16스텝 · 스윙 · 드랍 직전 마지막 박 무음)
                for (var st = 0; st < 16; st++)
                {
                    if (nextIsDrop && st >= 12) break;
                    var t = t0 + st * spb / 4 + ((st & 1) == 1 ? prof.Swing * spb / 4 : 0);
                    var kick = sec.Kind == SectionKind.Drop ? st % 4 == 0 : prof.Kick[st] == 'x';
                    if (kick && sec.Kind is not (SectionKind.Intro or SectionKind.Outro)) Kick(l, rr, rate, t, 0.95f * Math.Max(0.7f, e));
                    if (prof.Snare[st] == 'x' && sec.Kind is not (SectionKind.Intro or SectionKind.Outro)) Snare(l, rr, rate, t, 0.32f * e, ref rng);
                    if (prof.Hat[st] == 'x') Hat(l, rr, rate, t, (sec.Kind == SectionKind.Intro ? 0.05f : 0.1f) * e, (st & 2) == 0 ? -0.3f : 0.3f, ref rng);
                }
                if (nextIsDrop) Riser(l, rr, rate, t0, barSec * 0.75, 0.16f, ref rng);
                // 아르페지오(코러스 · 드랍)
                if (sec.Kind is SectionKind.Chorus or SectionKind.Drop)
                    for (var s = 0; s < 16; s++)
                    {
                        if (nextIsDrop && s >= 12) break;
                        var midi = NoteOf(root + 12, scale, degree + (s % 3) * 2 + (s % 6 >= 3 ? 7 : 0));
                        Pluck(l, rr, rate, t0 + s * spb / 4, spb / 4, Hz(midi), (sec.Kind == SectionKind.Drop ? 0.07f : 0.045f) * e, (s & 1) == 0 ? -0.25f : 0.25f);
                    }
                // 보컬(벌스 · 프리 · 코러스)
                if (r.Vocals && syl.Count > 0 && sec.Kind is SectionKind.Verse or SectionKind.PreChorus or SectionKind.Chorus)
                {
                    var slots = r.Genre == MusicGenre.Ballad ? 4 : 8;
                    var restP = sec.Kind == SectionKind.Chorus ? 0.12f : 0.28f;
                    for (var s = 0; s < slots; s++)
                    {
                        if (s == slots - 1 && (b - sec.StartBar) % 2 == 1) continue;   // 2마디마다 호흡
                        if (rng.Next01() < restP) continue;
                        var sy = syl[sylIdx % syl.Count];
                        sylIdx++;
                        melody = Math.Clamp(melody + (int)MathF.Round(rng.Next() * 1.4f), 0, 5);
                        var midi = NoteOf(root, scale, degree + (melody % 3) * 2 + (melody >= 3 ? 7 : 0)) + (sec.Kind == SectionKind.Chorus ? 12 : 0);
                        var ts = t0 + s * barSec / slots;
                        var dur = barSec / slots * 0.92;
                        Vocal(l, rr, rate, ts, dur, Hz(midi), sy.Vowel, sy.Consonant, 0.2f * Math.Max(0.75f, e), ref rng);
                        vocalNotes++;
                        if (sy.Line != cueLine)
                        {
                            if (cueLine >= 0 && cues.Count < 400) cues.Add(new LyricCue(lines[cueLine], Math.Round(cueStart, 3), Math.Round(cueEnd, 3), cueMidi));
                            cueLine = sy.Line; cueStart = ts; cueMidi = midi;
                        }
                        cueEnd = ts + dur;
                    }
                }
            }
        }
        if (cueLine >= 0 && cues.Count < 400) cues.Add(new LyricCue(lines[cueLine], Math.Round(cueStart, 3), Math.Round(cueEnd, 3), cueMidi));

        // 마스터: −3 dBFS 정규화 → −1 dB 리미터
        var peak = 1e-6f;
        for (var i = 0; i < n; i++) peak = MathF.Max(peak, MathF.Max(MathF.Abs(l[i]), MathF.Abs(rr[i])));
        var g = 0.708f / peak;
        for (var i = 0; i < n; i++) { l[i] *= g; rr[i] *= g; }
        SoundDsp.Limit(l, rr, rate, -1f);
        return new MusicTrack
        {
            Genre = r.Genre, Mood = r.Mood ?? "", Bpm = bpm, Minor = minor, KeyName = NoteNames[((root % 12) + 12) % 12] + (minor ? " minor" : " major"),
            Rate = rate, Left = l, Right = rr, Sections = sections, Lyrics = cues, VocalNotes = vocalNotes,
            Source = "BCL 신디사이저", Prompt = BuildPrompt(r with { Bpm = bpm }),
            Lufs = Math.Round(SoundDsp.MeasureLufs(l, rr, rate), 2), PeakDb = MathF.Round(Math.Max(SoundDsp.PeakDb(l), SoundDsp.PeakDb(rr)), 2),
        };
    }

    private static SectionKind? NextKind(List<MusicSection> s, MusicSection cur)
    {
        var i = s.IndexOf(cur);
        return i >= 0 && i + 1 < s.Count ? s[i + 1].Kind : null;
    }

    private static int StableHash(string s)
    {
        unchecked
        {
            var h = (int)2166136261;
            foreach (var c in s) h = (h ^ c) * 16777619;
            return h == 0 ? 1 : h;
        }
    }

    /// <summary>스케일 도수(옥타브 랩) → MIDI.</summary>
    public static int NoteOf(int root, int[] scale, int degree)
    {
        var oct = (int)Math.Floor(degree / 7.0);
        var d = degree - oct * 7;
        return root + oct * 12 + scale[d];
    }

    public static float Hz(int midi) => 440f * MathF.Pow(2f, (midi - 69) / 12f);

    private static (int Start, int End) Range(int len, int rate, double t, double dur)
    {
        var s = (int)(t * rate);
        return (Math.Max(0, s), Math.Min(len, s + (int)(dur * rate)));
    }

    private static void Kick(float[] l, float[] r, int rate, double t, float gain)
    {
        var (s, e) = Range(l.Length, rate, t, 0.35);
        double ph = 0;
        for (var i = s; i < e; i++)
        {
            var tt = (i - s) / (float)rate;
            ph += (45 + 105 * MathF.Exp(-tt * 30)) / rate;
            var v = MathF.Sin((float)(ph * 2 * Math.PI)) * MathF.Exp(-tt * 7) * gain;
            l[i] += v; r[i] += v;
        }
    }

    private static void Snare(float[] l, float[] r, int rate, double t, float gain, ref XorShift32 rng)
    {
        var (s, e) = Range(l.Length, rate, t, 0.22);
        var bp = Biquad.BandPass(2000, 0.8, rate);
        for (var i = s; i < e; i++)
        {
            var tt = (i - s) / (float)rate;
            var v = (bp.Process(rng.Next()) * 1.6f + 0.4f * MathF.Sin(2 * MathF.PI * 185 * tt)) * MathF.Exp(-tt * 18) * gain;
            l[i] += v; r[i] += v;
        }
    }

    private static void Hat(float[] l, float[] r, int rate, double t, float gain, float pan, ref XorShift32 rng)
    {
        var (s, e) = Range(l.Length, rate, t, 0.06);
        var hp = Biquad.HighPass(7000, 0.7, rate);
        var (gl, gr) = SoundDsp.Pan(pan);
        for (var i = s; i < e; i++)
        {
            var tt = (i - s) / (float)rate;
            var v = hp.Process(rng.Next()) * MathF.Exp(-tt * 60) * gain;
            l[i] += v * gl; r[i] += v * gr;
        }
    }

    private static void Bass(float[] l, float[] r, int rate, double t, double dur, float hz, float gain, bool sub)
    {
        var (s, e) = Range(l.Length, rate, t, dur);
        var lp = Biquad.LowPass(500, 0.9, rate);
        double ph = 0;
        var len = Math.Max(1, e - s);
        for (var i = s; i < e; i++)
        {
            var k = i - s;
            var env = MathF.Min(1, k / (rate * 0.005f)) * MathF.Min(1, (len - k) / (rate * 0.02f));
            ph += hz / rate; ph -= Math.Floor(ph);
            var saw = (float)(ph * 2 - 1);
            var v = lp.Process(saw) * env * gain;
            if (sub) v += MathF.Sin((float)(ph * 2 * Math.PI)) * env * gain * 0.8f;
            l[i] += v; r[i] += v;
        }
    }

    private static void Pad(float[] l, float[] r, int rate, double t, double dur, float hz, float gain, float pan, ref XorShift32 rng)
    {
        var (s, e) = Range(l.Length, rate, t, dur);
        var lp = Biquad.LowPass(1400, 0.7, rate);
        var (gl, gr) = SoundDsp.Pan(pan);
        double p1 = rng.Next01(), p2 = rng.Next01();
        var len = Math.Max(1, e - s);
        for (var i = s; i < e; i++)
        {
            var k = i - s;
            var env = MathF.Min(1, k / (len * 0.15f)) * MathF.Min(1, (len - k) / (len * 0.2f));
            p1 += hz * 1.004 / rate; p1 -= Math.Floor(p1);
            p2 += hz * 0.996 / rate; p2 -= Math.Floor(p2);
            var v = lp.Process((float)(p1 + p2 - 1)) * env * gain;
            l[i] += v * gl; r[i] += v * gr;
        }
    }

    private static void Pluck(float[] l, float[] r, int rate, double t, double dur, float hz, float gain, float pan)
    {
        var (s, e) = Range(l.Length, rate, t, dur);
        var lp = Biquad.LowPass(3000, 0.7, rate);
        var (gl, gr) = SoundDsp.Pan(pan);
        double ph = 0;
        for (var i = s; i < e; i++)
        {
            var tt = (i - s) / (float)rate;
            ph += hz / rate; ph -= Math.Floor(ph);
            var v = lp.Process(ph < 0.5 ? 1f : -1f) * MathF.Exp(-tt * 12) * gain;
            l[i] += v * gl; r[i] += v * gr;
        }
    }

    private static void Riser(float[] l, float[] r, int rate, double t, double dur, float gain, ref XorShift32 rng)
    {
        var (s, e) = Range(l.Length, rate, t, dur);
        var len = Math.Max(1, e - s);
        var bp = Biquad.BandPass(300, 1.2, rate);
        for (var i = s; i < e; i++)
        {
            var k = i - s;
            var x = k / (float)len;
            if ((k & 63) == 0)
            {
                var nb = Biquad.BandPass(300 * MathF.Pow(20, x), 1.2, rate);
                nb.CopyStateFrom(bp);
                bp = nb;
            }
            var v = bp.Process(rng.Next()) * x * x * gain * 2;
            l[i] += v; r[i] += v;
        }
    }

    private static void Vocal(float[] l, float[] r, int rate, double t, double dur, float hz, int vowel, bool consonant, float gain, ref XorShift32 rng)
    {
        var (s, e) = Range(l.Length, rate, t, dur);
        var (f1, f2, f3) = Formants[Math.Clamp(vowel, 0, Formants.Length - 1)];
        var b1 = Biquad.BandPass(f1, f1 / 80.0, rate);
        var b2 = Biquad.BandPass(f2, f2 / 100.0, rate);
        var b3 = Biquad.BandPass(f3, f3 / 120.0, rate);
        var hp = Biquad.HighPass(3000, 0.7, rate);
        var len = Math.Max(1, e - s);
        double ph = 0;
        var cons = consonant ? (int)(rate * 0.015f) : 0;
        for (var i = s; i < e; i++)
        {
            var k = i - s;
            var tt = k / (float)rate;
            var vib = tt > 0.12f ? 1 + 0.005f * MathF.Sin(2 * MathF.PI * 5.5f * tt) : 1;
            ph += hz * vib / rate; ph -= Math.Floor(ph);
            var src = (float)(ph * 2 - 1) + rng.Next() * 0.03f;
            var env = MathF.Min(1, k / (rate * 0.025f)) * MathF.Min(1, (len - k) / (rate * 0.06f));
            var v = (b1.Process(src) + 0.6f * b2.Process(src) + 0.3f * b3.Process(src)) * env * gain * 2.2f;
            if (k < cons) v += hp.Process(rng.Next()) * (1 - k / (float)cons) * gain * 0.8f;
            l[i] += v; r[i] += v;
        }
    }

    // ── 실행(원격 → 폴백 · 저장) ─────────────────────────────────────────────

    /// <summary>생성(원격 허용·구성 시 원격 시도 → 실패 즉시 BCL 폴백) · WAV 저장 · 현재 등록. 동시 실행 차단(null).</summary>
    public async Task<MusicTrack?> GenerateAsync(MusicRequest r, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0) return null;
        Raise();
        try
        {
            MusicTrack? track = null;
            string? note = null;
            if (r.AllowRemote && RemoteConfigured)
            {
                try { track = await TryRemoteAsync(r, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { Program.Log("Music.Remote", ex); note = ex.Message; }
                if (track is null) { Fallbacks++; note ??= "원격 응답 무효"; }
                else RemoteOk++;
            }
            track ??= await Task.Run(() => Synthesize(r), ct).ConfigureAwait(false);
            Save(track);
            lock (_gate)
            {
                _tracks.Insert(0, track);
                if (_tracks.Count > 8) _tracks.RemoveAt(_tracks.Count - 1);
            }
            Current = track;
            LastAction = "🎵 " + GenreLabel(track.Genre) + " · " + track.Bpm.ToString(CultureInfo.InvariantCulture) + " BPM · " + track.Seconds.ToString("0", CultureInfo.InvariantCulture) + "초 · " + track.Source
                         + (note is null ? "" : " (폴백: " + note + ")");
            return track;
        }
        catch (OperationCanceledException) { LastAction = "⚠ 취소됨"; return null; }
        catch (Exception ex) { Program.Log("Music.Generate", ex); LastAction = "⚠ 생성 실패: " + ex.Message; return null; }
        finally { Volatile.Write(ref _busy, 0); Raise(); }
    }

    private void Save(MusicTrack t)
    {
        Directory.CreateDirectory(_folder);
        var path = System.IO.Path.Combine(_folder, "mv_" + t.Genre.ToString().ToLowerInvariant() + "_" + t.Id + ".wav");
        SoundDsp.WriteStereoWav(path, t.Left, t.Right, t.Rate);
        t.Path = path;
    }

    public void Select(string id)
    {
        lock (_gate) foreach (var t in _tracks) if (t.Id == id) { Current = t; break; }
        Raise();
    }

    /// <summary>원격 생성 — 제출 → 폴링(데드라인) → 다운로드 → 디코드. 스키마 위반 = 예외.</summary>
    private async Task<MusicTrack?> TryRemoteAsync(MusicRequest r, CancellationToken ct)
    {
        var cfg = _config();
        var baseUri = cfg.Endpoint!.TrimEnd('/');
        var body = new JsonObject
        {
            ["provider"] = cfg.Provider,
            ["prompt"] = BuildPrompt(r),
            ["lyrics"] = r.Lyrics ?? "",
            ["instrumental"] = !r.Vocals,
            ["duration_seconds"] = Math.Clamp(r.Seconds, MinSeconds, MaxSeconds),
            ["bpm"] = r.Bpm > 0 ? r.Bpm : DefaultBpm(r.Genre),
        }.ToJsonString();
        var submit = await SendAsync(() => Req(HttpMethod.Post, baseUri + "/generate", cfg, body), ct).ConfigureAwait(false);
        var id = JsonNode.Parse(submit)?["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || !IsSafeId(id)) throw new InvalidDataException("응답 스키마 위반: id");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_deadline);
        string? audioUrl = null;
        while (audioUrl is null)
        {
            var st = JsonNode.Parse(await SendAsync(() => Req(HttpMethod.Get, baseUri + "/status/" + Uri.EscapeDataString(id), cfg, null), deadline.Token).ConfigureAwait(false));
            var status = st?["status"]?.GetValue<string>();
            switch (status)
            {
                case "complete" or "completed" or "succeeded":
                    audioUrl = st?["audio_url"]?.GetValue<string>();
                    if (!Uri.TryCreate(audioUrl, UriKind.Absolute, out var au) || !(au.Scheme == Uri.UriSchemeHttps || au.IsLoopback)) throw new InvalidDataException("응답 스키마 위반: audio_url");
                    break;
                case "queued" or "running" or "processing" or "pending":
                    await Task.Delay(_poll, deadline.Token).ConfigureAwait(false);
                    break;
                case "failed" or "error":
                    throw new InvalidOperationException("원격 생성 실패: " + (st?["error"]?.ToString() ?? status));
                default:
                    throw new InvalidDataException("응답 스키마 위반: status");
            }
        }
        var bytes = await SendBytesAsync(audioUrl, deadline.Token).ConfigureAwait(false);
        if (!SoundDsp.TryReadWav(bytes, out var l, out var rr, out var rate, out _))
        {
            var dec = await DecodeWithFfmpegAsync(bytes, deadline.Token).ConfigureAwait(false);
            if (dec is null) throw new InvalidDataException("오디오 디코드 실패(WAV 아님 · ffmpeg 없음)");
            (l, rr, rate) = dec.Value;
        }
        if (l.Length < rate) throw new InvalidDataException("오디오 길이 1초 미만");
        if (ReferenceEquals(l, rr)) rr = (float[])l.Clone();
        return new MusicTrack
        {
            Genre = r.Genre, Mood = r.Mood ?? "", Bpm = r.Bpm, Rate = rate, Left = l, Right = rr, Source = cfg.Provider == "udio" ? "Udio API" : "Suno API", Prompt = BuildPrompt(r),
            Lufs = Math.Round(SoundDsp.MeasureLufs(l, rr, rate), 2), PeakDb = MathF.Round(Math.Max(SoundDsp.PeakDb(l), SoundDsp.PeakDb(rr)), 2),
        };
    }

    private static bool IsSafeId(string id)
    {
        foreach (var c in id) if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return false;
        return true;
    }

    private static HttpRequestMessage Req(HttpMethod m, string url, MusicApiConfig cfg, string? json)
    {
        var req = new HttpRequestMessage(m, url);
        if (!string.IsNullOrEmpty(cfg.ApiKey)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return req;
    }

    private static bool IsTransient(HttpStatusCode c) => c is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int)c >= 500;

    /// <summary>지수 백오프(3회 · 200/400ms) — 일시 오류만 재시도 · 총 지연 &lt;1초.</summary>
    private async Task<string> SendAsync(Func<HttpRequestMessage> make, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var req = make();
            HttpResponseMessage? res = null;
            try { res = await _http.SendAsync(req, ct).ConfigureAwait(false); }
            catch (HttpRequestException) when (attempt < 2) { }
            if (res is not null)
            {
                using (res)
                {
                    if (res.IsSuccessStatusCode) return await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (!IsTransient(res.StatusCode) || attempt >= 2) throw new HttpRequestException("HTTP " + ((int)res.StatusCode).ToString(CultureInfo.InvariantCulture));
                }
            }
            await Task.Delay(200 << attempt, ct).ConfigureAwait(false);
        }
    }

    private async Task<byte[]> SendBytesAsync(string url, CancellationToken ct)
    {
        using var res = await _http.GetAsync(url, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        var len = res.Content.Headers.ContentLength;
        if (len > 512L * 1024 * 1024) throw new InvalidDataException("오디오 512MB 초과");
        return await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private async Task<(float[] L, float[] R, int Rate)?> DecodeWithFfmpegAsync(byte[] bytes, CancellationToken ct)
    {
        var exe = _ffmpeg();
        if (exe is null) return null;
        var src = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pfs_mus_" + Guid.NewGuid().ToString("N") + ".bin");
        var dst = System.IO.Path.ChangeExtension(src, ".wav");
        try
        {
            await File.WriteAllBytesAsync(src, bytes, ct).ConfigureAwait(false);
            var res = await ProcessRunner.RunAsync(exe, new[] { "-y", "-hide_banner", "-nostdin", "-i", src, "-ac", "2", "-ar", "44100", "-c:a", "pcm_f32le", dst }, ct: ct).ConfigureAwait(false);
            if (!res.Ok || !File.Exists(dst)) return null;
            return SoundDsp.TryReadWav(await File.ReadAllBytesAsync(dst, ct).ConfigureAwait(false), out var l, out var r, out var rate, out _) ? (l, r, rate) : null;
        }
        finally
        {
            try { File.Delete(src); } catch { /* 무시 */ }
            try { File.Delete(dst); } catch { /* 무시 */ }
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("Music.OnChange", ex); }
    }

    public void Dispose() => _http.Dispose();
}
