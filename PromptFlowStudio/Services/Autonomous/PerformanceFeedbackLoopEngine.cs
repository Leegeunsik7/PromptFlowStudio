using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Interactive;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Services.Autonomous;

/// <summary>v16.28.0 프롬프트 특성 축(밴딧 팔 그룹).</summary>
public enum PromptAxis : byte { Camera, Lighting, Format }

/// <summary>v16.28.0 영상 1편 성과(게시 후 수집).</summary>
public sealed record VideoPerformance(
    string VideoId, string Theme, string Prompt, string Platform,
    long Impressions, long Views, long Likes, double Ctr, double EarlyDropRate, double AvgRetention, DateTimeOffset At, string? SlotId = null)
{
    public double LikeRate => Views > 0 ? Math.Min(1, (double)Likes / Views) : 0;
}

/// <summary>v16.28.0 저성과 진단.</summary>
public sealed record PerformanceDiagnosis(string VideoId, double Reward, bool LowPerformer, string? WeakCamera, string? WeakLighting, string? BestCamera, string? BestLighting, IReadOnlyList<string> Findings);

/// <summary>v16.28.0 프롬프트 자가 정제 이력 1세대.</summary>
public sealed record PromptRevision(int Generation, string Theme, string Before, string After, double RewardBefore, double PredictedReward, bool Ai, string Rationale, DateTimeOffset At);

/// <summary>v16.28.0 성과 목표(정규화 기준).</summary>
public sealed record PerformanceTargets(double Ctr = 0.06, double LikeRate = 0.04, double MaxEarlyDrop = 0.35);

/// <summary>
/// [PromptFlowStudio] v16.28.0 🔁 실시간 성과 피드백 루프 &amp; 프롬프트 자가 개선 엔진 — 성과 수집 → 보상 → 특성 밴딧 → 저성과 진단 → Gemini 자가 정제 → 차기 생성 큐.
///   ▸ 수집: (a) v16.17.0 게시 결과 videoId 의 YouTube Data API statistics(조회/좋아요 · <c>PFS_YOUTUBE_API_KEY</c> · 8초 · 지수 백오프 · 스키마 검증)
///     (b) YouTube Studio/TikTok 성과 CSV 가져오기(노출·CTR·초반 이탈) (c) v16.26.0 <see cref="InteractiveConversionAnalytics"/> 핫스팟 CTR 보정.
///   ▸ 보상: 0.40·CTR/목표 + 0.35·(1−초반이탈/한도) + 0.25·좋아요율/목표 (각 0~1.5 클램프 · <see cref="Reward"/> 무할당).
///   ▸ 특성: 프롬프트에서 카메라 무빙/조명/포맷 토큰을 Span 매칭(<see cref="DetectFeature"/> · 무할당 · 한/영 사전) → 팔별 보상 누적 → UCB1 선택(<see cref="Ucb1"/>).
///   ▸ 자가 정제: 저성과(보상 &lt; 테마 평균×0.8 또는 &lt; 0.5) 프롬프트를 상위 패턴(최고 보상 프롬프트 3개 + 최적 카메라/조명 팔)으로
///     Gemini Strict JSON 재작성(10초 데드라인 · 스키마/길이/금칙 검증) → 실패 1초 내 결정적 폴백(약한 토큰 → 최적 토큰 치환).
///     결과는 <see cref="TunedQueue"/>(상한 64) 로 디렉터가 소비 · 이력 <see cref="History"/>(상한 200)로 변천사 그래프.
/// </summary>
public sealed class PerformanceFeedbackLoopEngine
{
    public const int MaxRecords = 5000;
    public const int MaxHistory = 200;
    public const int MaxQueue = 64;
    public const int MaxPromptLength = 800;
    public static readonly TimeSpan AiDeadline = TimeSpan.FromSeconds(10);

    /// <summary>카메라 무빙 사전(정규 라벨, 동의어…) — 첫 원소가 라벨이자 치환 토큰.</summary>
    public static readonly string[][] CameraLexicon =
    {
        new[] { "slow dolly-in", "dolly in", "dolly-in", "push in", "돌리 인", "줌인", "zoom in" },
        new[] { "smooth tracking shot", "tracking shot", "tracking", "follow shot", "트래킹", "팔로우" },
        new[] { "handheld close-up", "handheld", "핸드헬드" },
        new[] { "aerial drone shot", "drone", "aerial", "드론", "항공" },
        new[] { "orbit shot", "orbit", "360", "오빗", "회전 촬영" },
        new[] { "static wide shot", "static", "locked-off", "wide shot", "고정", "와이드" },
        new[] { "macro close-up", "macro", "close-up", "closeup", "클로즈업", "접사" },
    };

    public static readonly string[][] LightingLexicon =
    {
        new[] { "golden hour light", "golden hour", "sunset light", "골든아워", "노을" },
        new[] { "soft diffused light", "soft light", "diffused", "부드러운 조명", "소프트" },
        new[] { "neon rim light", "neon", "cyberpunk", "네온" },
        new[] { "high-key bright light", "high-key", "bright", "밝은 조명", "하이키" },
        new[] { "low-key dramatic light", "low-key", "dramatic", "chiaroscuro", "로우키", "드라마틱" },
        new[] { "natural window light", "window light", "natural light", "자연광", "창가" },
    };

    public static readonly string[][] FormatLexicon =
    {
        new[] { "pov", "pov", "1인칭" },
        new[] { "asmr", "asmr" },
        new[] { "tutorial", "tutorial", "how to", "튜토리얼", "방법" },
        new[] { "challenge", "challenge", "챌린지" },
        new[] { "vlog", "vlog", "브이로그", "일상" },
        new[] { "before-after", "before after", "before-after", "전후", "비포애프터" },
        new[] { "story", "story", "스토리", "드라마" },
    };

    private sealed class Arm
    {
        public int N;
        public double Sum;
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly Func<string, CancellationToken, Task<GeminiResult>>? _generate;
    private readonly Func<bool> _aiEnabled;
    private readonly Func<string?> _ytKey;
    private readonly InteractiveConversionAnalytics? _interactive;
    private readonly object _gate = new();
    private readonly List<VideoPerformance> _records = new();
    private readonly List<PromptRevision> _history = new();
    private readonly Queue<(string Theme, string Prompt)> _queue = new();
    private readonly Arm[][] _arms;
    private int _generation, _aiTunes, _fallbackTunes, _busy;

    public PerformanceFeedbackLoopEngine(GeminiClient? gemini = null, InteractiveConversionAnalytics? interactive = null)
        : this(gemini is null ? null : (p, ct) => gemini.GenerateAsync(p, null, 0.6, ct), () => gemini?.Enabled == true,
               () => Environment.GetEnvironmentVariable("PFS_YOUTUBE_API_KEY"), interactive) { }

    public PerformanceFeedbackLoopEngine(Func<string, CancellationToken, Task<GeminiResult>>? generate, Func<bool> aiEnabled, Func<string?> ytKey, InteractiveConversionAnalytics? interactive)
    {
        _generate = generate;
        _aiEnabled = aiEnabled;
        _ytKey = ytKey;
        _interactive = interactive;
        _arms = new Arm[3][];
        _arms[0] = NewArms(CameraLexicon.Length);
        _arms[1] = NewArms(LightingLexicon.Length);
        _arms[2] = NewArms(FormatLexicon.Length);
    }

    private static Arm[] NewArms(int n)
    {
        var a = new Arm[n];
        for (var i = 0; i < n; i++) a[i] = new Arm();
        return a;
    }

    public event Action? OnChange;

    public PerformanceTargets Targets { get; set; } = new();
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public bool AiAvailable => _generate is not null && _aiEnabled();
    public bool YouTubeConfigured => !string.IsNullOrWhiteSpace(_ytKey());
    public int Generation => Volatile.Read(ref _generation);
    public int AiTunes => Volatile.Read(ref _aiTunes);
    public int FallbackTunes => Volatile.Read(ref _fallbackTunes);
    public string? LastAction { get; private set; }
    public int RecordCount { get { lock (_gate) return _records.Count; } }
    public int QueueDepth { get { lock (_gate) return _queue.Count; } }
    public IReadOnlyList<VideoPerformance> Records { get { lock (_gate) return _records.ToArray(); } }
    public IReadOnlyList<PromptRevision> History { get { lock (_gate) return _history.ToArray(); } }

    // ══════════════════════ 순수 헬퍼(무할당 · 테스트 대상) ══════════════════════

    /// <summary>보상(0~1.5) — CTR · 초반 이탈 · 좋아요율 가중 합.</summary>
    public static double Reward(double ctr, double earlyDrop, double likeRate, PerformanceTargets t)
    {
        var c = Math.Clamp(ctr / Math.Max(1e-6, t.Ctr), 0, 1.5);
        var d = Math.Clamp(1 - earlyDrop / Math.Max(1e-6, t.MaxEarlyDrop * 2), 0, 1.5);
        var l = Math.Clamp(likeRate / Math.Max(1e-6, t.LikeRate), 0, 1.5);
        return 0.40 * c + 0.35 * d + 0.25 * l;
    }

    /// <summary>프롬프트 → 사전 인덱스(대소문자 무시 Span 검색 · 무할당 · 미검출 -1).</summary>
    public static int DetectFeature(ReadOnlySpan<char> prompt, string[][] lexicon)
    {
        for (var i = 0; i < lexicon.Length; i++)
        {
            var syn = lexicon[i];
            for (var k = 1; k < syn.Length; k++)
                if (prompt.IndexOf(syn[k].AsSpan(), StringComparison.OrdinalIgnoreCase) >= 0) return i;
        }
        return -1;
    }

    public static string[][] LexiconOf(PromptAxis a) => a switch { PromptAxis.Camera => CameraLexicon, PromptAxis.Lighting => LightingLexicon, _ => FormatLexicon };

    /// <summary>UCB1 팔 선택(무할당) — 미시도 팔 우선, 이후 평균 + c·√(ln N / n).</summary>
    public static int Ucb1(ReadOnlySpan<int> counts, ReadOnlySpan<double> sums, double c = 0.6)
    {
        var total = 0;
        for (var i = 0; i < counts.Length; i++) total += counts[i];
        var best = -1; var bestScore = double.MinValue;
        for (var i = 0; i < counts.Length; i++)
        {
            if (counts[i] == 0) return i;
            var score = sums[i] / counts[i] + c * Math.Sqrt(Math.Log(Math.Max(1, total)) / counts[i]);
            if (score > bestScore) { bestScore = score; best = i; }
        }
        return best;
    }

    /// <summary>약한 토큰 → 강한 토큰 치환(동의어 대소문자 무시 · 없으면 쉼표 뒤 추가).</summary>
    public static string ReplaceFeature(string prompt, string[][] lexicon, int from, int to)
    {
        if ((uint)to >= (uint)lexicon.Length) return prompt;
        var target = lexicon[to][0];
        if ((uint)from < (uint)lexicon.Length)
        {
            var syn = lexicon[from];
            for (var k = 1; k < syn.Length; k++)
            {
                var i = prompt.IndexOf(syn[k], StringComparison.OrdinalIgnoreCase);
                if (i >= 0) return prompt[..i] + target + prompt[(i + syn[k].Length)..];
            }
        }
        if (prompt.Contains(target, StringComparison.OrdinalIgnoreCase)) return prompt;
        return prompt.TrimEnd().TrimEnd(',', '.') + ", " + target;
    }

    /// <summary>
    /// 성과 CSV 파싱 — 헤더 필수 열: video_id, theme, prompt, views · 선택: impressions, likes, ctr, early_drop, retention, platform.
    /// 비율은 0~1 또는 % 허용. 불량 행 건너뜀. 반환 (records, skipped).
    /// </summary>
    public static (List<VideoPerformance> Rows, int Skipped) ParseCsv(string? csv)
    {
        var rows = new List<VideoPerformance>();
        var skipped = 0;
        if (string.IsNullOrWhiteSpace(csv)) return (rows, 0);
        var lines = csv.Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart('﻿').Split('\n');
        if (lines.Length < 2) return (rows, 0);
        var head = SplitCsv(lines[0]);
        int Col(string n) { for (var i = 0; i < head.Count; i++) if (string.Equals(head[i].Trim(), n, StringComparison.OrdinalIgnoreCase)) return i; return -1; }
        int cId = Col("video_id"), cTheme = Col("theme"), cPrompt = Col("prompt"), cViews = Col("views"), cImp = Col("impressions"), cLikes = Col("likes"),
            cCtr = Col("ctr"), cDrop = Col("early_drop"), cRet = Col("retention"), cPlat = Col("platform");
        if (cId < 0 || cTheme < 0 || cPrompt < 0 || cViews < 0) return (rows, lines.Length - 1);
        for (var li = 1; li < lines.Length && rows.Count < MaxRecords; li++)
        {
            if (string.IsNullOrWhiteSpace(lines[li])) continue;
            var f = SplitCsv(lines[li]);
            string Get(int c) => c >= 0 && c < f.Count ? f[c].Trim() : "";
            if (!long.TryParse(Get(cViews), NumberStyles.Integer, CultureInfo.InvariantCulture, out var views) || views < 0 || Get(cId).Length is 0 or > 64) { skipped++; continue; }
            long.TryParse(Get(cImp), NumberStyles.Integer, CultureInfo.InvariantCulture, out var imp);
            long.TryParse(Get(cLikes), NumberStyles.Integer, CultureInfo.InvariantCulture, out var likes);
            var prompt = Get(cPrompt);
            rows.Add(new VideoPerformance(Get(cId), Get(cTheme).Length == 0 ? "general" : Get(cTheme), prompt.Length > MaxPromptLength ? prompt[..MaxPromptLength] : prompt,
                Get(cPlat).Length == 0 ? "youtube" : Get(cPlat), Math.Max(0, imp), views, Math.Max(0, likes), Ratio(Get(cCtr)), Ratio(Get(cDrop)), Ratio(Get(cRet)), DateTimeOffset.Now));
        }
        return (rows, skipped);

        static double Ratio(string s)
        {
            var pct = s.EndsWith('%');
            if (!double.TryParse(pct ? s[..^1] : s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v) || v < 0) return 0;
            return Math.Clamp(pct || v > 1 ? v / 100.0 : v, 0, 1);
        }
    }

    private static List<string> SplitCsv(string line)
    {
        var res = new List<string>(12);
        var sb = new StringBuilder();
        var q = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (q)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') q = false;
                else sb.Append(ch);
            }
            else if (ch == '"') q = true;
            else if (ch == ',') { res.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        res.Add(sb.ToString());
        return res;
    }

    /// <summary>Gemini 정제 응답 검증 — <c>{"prompt":"...","rationale":"..."}</c> · 20~800자 · 원문 반복/URL/코드펜스 거부.</summary>
    public static bool TryParseTuned(string? text, string before, out string prompt, out string rationale)
    {
        prompt = rationale = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            using var doc = JsonDocument.Parse(GeminiClient.StripFence(text));
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("prompt", out var p) || p.ValueKind != JsonValueKind.String) return false;
            prompt = (p.GetString() ?? "").Trim();
            rationale = r.TryGetProperty("rationale", out var ra) && ra.ValueKind == JsonValueKind.String ? (ra.GetString() ?? "").Trim() : "";
            if (rationale.Length > 300) rationale = rationale[..300];
            if (prompt.Length is < 20 or > MaxPromptLength || string.Equals(prompt, before.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
            if (prompt.Contains("http", StringComparison.OrdinalIgnoreCase) || prompt.Contains("```", StringComparison.Ordinal)) return false;
            return true;
        }
        catch (JsonException) { prompt = rationale = ""; return false; }
    }

    // ══════════════════════ 수집 ══════════════════════

    /// <summary>성과 기록(동일 videoId 는 최신으로 교체) → 팔 통계 갱신. 반환: 보상.</summary>
    public double Ingest(VideoPerformance v)
    {
        var reward = Reward(v.Ctr, v.EarlyDropRate, v.LikeRate, Targets);
        lock (_gate)
        {
            for (var i = 0; i < _records.Count; i++)
            {
                if (_records[i].VideoId != v.VideoId) continue;
                Untrack(_records[i]);
                _records.RemoveAt(i);
                break;
            }
            _records.Add(v);
            if (_records.Count > MaxRecords) { Untrack(_records[0]); _records.RemoveAt(0); }
            Track(v, reward, 1);
        }
        Raise();
        return reward;
    }

    public (int Accepted, int Skipped) ImportCsv(string? csv)
    {
        var (rows, skipped) = ParseCsv(csv);
        foreach (var r in rows) Ingest(r);
        LastAction = "📥 성과 " + rows.Count.ToString(CultureInfo.InvariantCulture) + "건 반영";
        Raise();
        return (rows.Count, skipped);
    }

    private void Untrack(VideoPerformance v) => Track(v, Reward(v.Ctr, v.EarlyDropRate, v.LikeRate, Targets), -1);

    private void Track(VideoPerformance v, double reward, int sign)
    {
        for (var a = 0; a < 3; a++)
        {
            var idx = DetectFeature(v.Prompt, LexiconOf((PromptAxis)a));
            if (idx < 0) continue;
            var arm = _arms[a][idx];
            arm.N = Math.Max(0, arm.N + sign);
            arm.Sum += sign * reward;
            if (arm.N == 0) arm.Sum = 0;
        }
    }

    /// <summary>
    /// YouTube 통계 갱신 — 게시된 videoId(최대 50) 의 viewCount/likeCount 를 기존 기록에 반영(CTR/이탈은 Studio CSV 로 보강).
    /// 키 없음/실패는 Graceful(0 반환).
    /// </summary>
    public async Task<int> RefreshYouTubeStatsAsync(IReadOnlyList<string> videoIds, CancellationToken ct = default)
    {
        var key = _ytKey();
        if (string.IsNullOrWhiteSpace(key) || videoIds.Count == 0) return 0;
        var ids = new StringBuilder();
        for (var i = 0; i < videoIds.Count && i < 50; i++)
        {
            if (!PromptFlowStudio.Services.Enterprise.TeamCollaborationSessionBridge.IsSafeId(videoIds[i])) continue;
            if (ids.Length > 0) ids.Append(',');
            ids.Append(videoIds[i]);
        }
        if (ids.Length == 0) return 0;
        var url = "https://www.googleapis.com/youtube/v3/videos?part=statistics&id=" + Uri.EscapeDataString(ids.ToString()) + "&key=" + Uri.EscapeDataString(key);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
                d.CancelAfter(TimeSpan.FromSeconds(8));
                using var resp = await Http.GetAsync(url, d.Token).ConfigureAwait(false);
                if ((int)resp.StatusCode is 429 or >= 500) { await Task.Delay(TimeSpan.FromSeconds(1 << attempt), ct).ConfigureAwait(false); continue; }
                if (!resp.IsSuccessStatusCode) return 0;
                var n = ApplyYouTubeStats(await resp.Content.ReadAsStringAsync(d.Token).ConfigureAwait(false));
                LastAction = "📈 YouTube 통계 " + n.ToString(CultureInfo.InvariantCulture) + "편 갱신";
                Raise();
                return n;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Program.Log("Feedback.YouTube", ex); }
        }
        return 0;
    }

    /// <summary>videos.list 응답 반영(스키마 검증 · 미등록 videoId 무시). 반환: 갱신 수.</summary>
    public int ApplyYouTubeStats(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        var updates = new List<VideoPerformance>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return 0;
            foreach (var it in items.EnumerateArray())
            {
                if (!it.TryGetProperty("id", out var ide) || ide.ValueKind != JsonValueKind.String || !it.TryGetProperty("statistics", out var st) || st.ValueKind != JsonValueKind.Object) continue;
                var id = ide.GetString()!;
                long views = 0, likes = 0;
                if (st.TryGetProperty("viewCount", out var vc) && vc.ValueKind == JsonValueKind.String) long.TryParse(vc.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out views);
                if (st.TryGetProperty("likeCount", out var lc) && lc.ValueKind == JsonValueKind.String) long.TryParse(lc.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out likes);
                VideoPerformance? cur = null;
                lock (_gate) foreach (var r in _records) if (r.VideoId == id) { cur = r; break; }
                if (cur is not null) updates.Add(cur with { Views = Math.Max(cur.Views, views), Likes = Math.Max(cur.Likes, likes), At = DateTimeOffset.Now });
            }
        }
        catch (JsonException) { return 0; }
        foreach (var u in updates) Ingest(u);
        return updates.Count;
    }

    // ══════════════════════ 분석 ══════════════════════

    /// <summary>축별 팔 통계 스냅샷(라벨 · 시도 · 평균 보상).</summary>
    public List<(string Label, int N, double Mean)> ArmStats(PromptAxis axis)
    {
        var lex = LexiconOf(axis);
        var list = new List<(string, int, double)>(lex.Length);
        lock (_gate)
            for (var i = 0; i < lex.Length; i++)
            {
                var a = _arms[(int)axis][i];
                list.Add((lex[i][0], a.N, a.N > 0 ? a.Sum / a.N : 0));
            }
        return list;
    }

    /// <summary>축별 다음 실험 팔(UCB1 · 시도된 팔이 하나도 없으면 -1).</summary>
    public int NextArm(PromptAxis axis)
    {
        var arms = _arms[(int)axis];
        Span<int> n = stackalloc int[arms.Length];
        Span<double> s = stackalloc double[arms.Length];
        var any = false;
        lock (_gate)
            for (var i = 0; i < arms.Length; i++) { n[i] = arms[i].N; s[i] = arms[i].Sum; any |= n[i] > 0; }
        return any ? Ucb1(n, s) : -1;
    }

    /// <summary>축별 최고 평균 팔(최소 표본 2 · 없으면 -1).</summary>
    public int BestArm(PromptAxis axis)
    {
        var best = -1; var bestMean = double.MinValue;
        lock (_gate)
        {
            var arms = _arms[(int)axis];
            for (var i = 0; i < arms.Length; i++)
            {
                if (arms[i].N < 2) continue;
                var m = arms[i].Sum / arms[i].N;
                if (m > bestMean) { bestMean = m; best = i; }
            }
        }
        return best;
    }

    public double ThemeMeanReward(string theme)
    {
        double s = 0; var n = 0;
        lock (_gate) foreach (var r in _records) if (r.Theme == theme) { s += Reward(r.Ctr, r.EarlyDropRate, r.LikeRate, Targets); n++; }
        return n == 0 ? 0 : s / n;
    }

    public double OverallMeanReward()
    {
        double s = 0; var n = 0;
        lock (_gate) foreach (var r in _records) { s += Reward(r.Ctr, r.EarlyDropRate, r.LikeRate, Targets); n++; }
        return n == 0 ? 0 : s / n;
    }

    /// <summary>저성과 진단 — 테마 평균 대비 · 목표 대비 · 약한 카메라/조명 팔 → 최적 팔 제안.</summary>
    public PerformanceDiagnosis Diagnose(VideoPerformance v)
    {
        var t = Targets;
        var reward = Reward(v.Ctr, v.EarlyDropRate, v.LikeRate, t);
        var themeMean = ThemeMeanReward(v.Theme);
        var low = reward < 0.5 || (themeMean > 0 && reward < themeMean * 0.8);
        var findings = new List<string>(4);
        if (v.Ctr < t.Ctr) findings.Add("CTR " + (v.Ctr * 100).ToString("0.#", CultureInfo.InvariantCulture) + "% < 목표 " + (t.Ctr * 100).ToString("0.#", CultureInfo.InvariantCulture) + "% — 썸네일/첫 프레임 훅 약함");
        if (v.EarlyDropRate > t.MaxEarlyDrop) findings.Add("초반 이탈 " + (v.EarlyDropRate * 100).ToString("0", CultureInfo.InvariantCulture) + "% — 첫 3초 움직임/자극 부족");
        if (v.LikeRate < t.LikeRate) findings.Add("좋아요율 낮음 — 감정 고조/결말 보강");
        var cam = DetectFeature(v.Prompt, CameraLexicon); var lit = DetectFeature(v.Prompt, LightingLexicon);
        var bestCam = BestArm(PromptAxis.Camera); var bestLit = BestArm(PromptAxis.Lighting);
        string? weakCam = null, weakLit = null;
        if (cam < 0) findings.Add("카메라 무빙 지시 없음");
        else if (bestCam >= 0 && bestCam != cam) weakCam = CameraLexicon[cam][0];
        if (lit < 0) findings.Add("조명 지시 없음");
        else if (bestLit >= 0 && bestLit != lit) weakLit = LightingLexicon[lit][0];
        return new PerformanceDiagnosis(v.VideoId, reward, low, weakCam, weakLit,
            bestCam >= 0 ? CameraLexicon[bestCam][0] : null, bestLit >= 0 ? LightingLexicon[bestLit][0] : null, findings);
    }

    /// <summary>테마별 최고 보상 프롬프트(최대 n).</summary>
    public List<VideoPerformance> TopPerformers(string? theme, int n)
    {
        List<VideoPerformance> all;
        lock (_gate) all = new List<VideoPerformance>(_records);
        if (theme is not null) all.RemoveAll(r => r.Theme != theme);
        var t = Targets;
        all.Sort((a, b) => Reward(b.Ctr, b.EarlyDropRate, b.LikeRate, t).CompareTo(Reward(a.Ctr, a.EarlyDropRate, a.LikeRate, t)));
        if (all.Count > n) all.RemoveRange(n, all.Count - n);
        return all;
    }

    // ══════════════════════ 자가 정제 ══════════════════════

    /// <summary>
    /// 저성과 영상 자가 정제 1회 — Gemini(상위 패턴 few-shot) → 실패 시 결정적 폴백. 결과는 차기 생성 큐 + 이력.
    /// 저성과가 아니면 null.
    /// </summary>
    public async Task<PromptRevision?> TuneAsync(VideoPerformance v, CancellationToken ct = default)
    {
        var dx = Diagnose(v);
        if (!dx.LowPerformer) return null;
        Interlocked.Increment(ref _busy);
        try
        {
            var top = TopPerformers(v.Theme, 3);
            if (top.Count == 0) top = TopPerformers(null, 3);
            string after = "", rationale = "";
            var ai = false;
            if (AiAvailable)
            {
                try
                {
                    using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    d.CancelAfter(AiDeadline);
                    var r = await _generate!(BuildTuningInstruction(v, dx, top), d.Token).ConfigureAwait(false);
                    ai = r.Ok && TryParseTuned(r.Text, v.Prompt, out after, out rationale);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { ai = false; }
                catch (Exception ex) when (ex is not OperationCanceledException) { Program.Log("Feedback.Tune", ex); ai = false; }
            }
            if (!ai) (after, rationale) = FallbackTune(v.Prompt, dx);
            var predicted = Math.Max(dx.Reward, PredictReward(after));
            PromptRevision rev;
            lock (_gate)
            {
                rev = new PromptRevision(++_generation, v.Theme, v.Prompt, after, dx.Reward, predicted, ai, rationale, DateTimeOffset.Now);
                _history.Add(rev);
                if (_history.Count > MaxHistory) _history.RemoveAt(0);
                _queue.Enqueue((v.Theme, after));
                while (_queue.Count > MaxQueue) _queue.Dequeue();
            }
            Interlocked.Increment(ref ai ? ref _aiTunes : ref _fallbackTunes);
            LastAction = "🧬 G" + rev.Generation.ToString(CultureInfo.InvariantCulture) + " 자가 정제 · " + v.Theme + (ai ? " (Gemini)" : " (폴백)");
            Raise();
            return rev;
        }
        finally { Interlocked.Decrement(ref _busy); }
    }

    /// <summary>전체 저성과 일괄 정제(최대 n · 순차).</summary>
    public async Task<int> TuneLowPerformersAsync(int max = 5, CancellationToken ct = default)
    {
        var all = Records;
        var n = 0;
        foreach (var v in all)
        {
            if (n >= max) break;
            if (await TuneAsync(v, ct).ConfigureAwait(false) is not null) n++;
        }
        return n;
    }

    /// <summary>결정적 폴백 — 약한/누락 카메라·조명을 최적 팔(없으면 UCB 실험 팔)로 치환 + 훅 문구.</summary>
    public (string Prompt, string Rationale) FallbackTune(string prompt, PerformanceDiagnosis dx)
    {
        var p = prompt;
        var why = new StringBuilder("규칙 기반: ");
        var cam = DetectFeature(p, CameraLexicon);
        var camTo = BestArm(PromptAxis.Camera); if (camTo < 0) camTo = NextArm(PromptAxis.Camera); if (camTo < 0) camTo = 0;
        if (cam != camTo) { p = ReplaceFeature(p, CameraLexicon, cam, camTo); why.Append("카메라→").Append(CameraLexicon[camTo][0]).Append(' '); }
        var lit = DetectFeature(p, LightingLexicon);
        var litTo = BestArm(PromptAxis.Lighting); if (litTo < 0) litTo = NextArm(PromptAxis.Lighting); if (litTo < 0) litTo = 0;
        if (lit != litTo) { p = ReplaceFeature(p, LightingLexicon, lit, litTo); why.Append("조명→").Append(LightingLexicon[litTo][0]).Append(' '); }
        if ((dx.Findings.Count > 0 && dx.Findings[0].StartsWith("CTR", StringComparison.Ordinal)) || HasDrop(dx))
        {
            if (!p.Contains("eye-catching opening", StringComparison.OrdinalIgnoreCase)) { p = "eye-catching opening motion in first second, " + p; why.Append("첫 1초 훅 추가"); }
        }
        if (p.Length > MaxPromptLength) p = p[..MaxPromptLength];
        return (p, why.ToString().Trim());

        static bool HasDrop(PerformanceDiagnosis d) { foreach (var f in d.Findings) if (f.StartsWith("초반", StringComparison.Ordinal)) return true; return false; }
    }

    /// <summary>프롬프트 예측 보상 = 검출된 팔 평균 보상의 평균(표본 없으면 전체 평균).</summary>
    public double PredictReward(string prompt)
    {
        double s = 0; var n = 0;
        lock (_gate)
            for (var a = 0; a < 3; a++)
            {
                var idx = DetectFeature(prompt, LexiconOf((PromptAxis)a));
                if (idx < 0) continue;
                var arm = _arms[a][idx];
                if (arm.N == 0) continue;
                s += arm.Sum / arm.N; n++;
            }
        return n == 0 ? OverallMeanReward() : s / n;
    }

    private string BuildTuningInstruction(VideoPerformance v, PerformanceDiagnosis dx, List<VideoPerformance> top)
    {
        var sb = new StringBuilder(1024);
        sb.Append("너는 숏폼 AI 영상 프롬프트 최적화 전문가다. 아래 저성과 Wan 비디오 프롬프트를 고성과 패턴을 참고해 재작성하라.\n");
        sb.Append("규칙: 영어 프롬프트 1문단(20~600자), 피사체·행동 유지, 카메라 무빙과 조명을 명시, 첫 1초 시선 훅 포함, URL/브랜드 상표/실존 인물 금지.\n");
        sb.Append("출력은 JSON 한 개만: {\"prompt\":\"...\",\"rationale\":\"한국어 1문장\"}\n\n[저성과] 테마=").Append(v.Theme)
          .Append(" 보상=").Append(dx.Reward.ToString("0.00", CultureInfo.InvariantCulture)).Append('\n').Append(v.Prompt).Append("\n[진단]\n");
        foreach (var f in dx.Findings) sb.Append("- ").Append(f).Append('\n');
        if (dx.BestCamera is not null) sb.Append("- 고성과 카메라: ").Append(dx.BestCamera).Append('\n');
        if (dx.BestLighting is not null) sb.Append("- 고성과 조명: ").Append(dx.BestLighting).Append('\n');
        sb.Append("[고성과 예시]\n");
        foreach (var t in top) sb.Append("- (보상 ").Append(Reward(t.Ctr, t.EarlyDropRate, t.LikeRate, Targets).ToString("0.00", CultureInfo.InvariantCulture)).Append(") ").Append(t.Prompt).Append('\n');
        return sb.ToString();
    }

    /// <summary>차기 생성 큐에서 정제 프롬프트 1개 꺼내기(테마 일치 우선 · 없으면 null).</summary>
    public string? DequeueTuned(string? theme)
    {
        string? hit = null;
        lock (_gate)
        {
            if (_queue.Count == 0) return null;
            var n = _queue.Count;
            for (var i = 0; i < n; i++)
            {
                var x = _queue.Dequeue();
                if (hit is null && (theme is null || x.Theme == theme)) hit = x.Prompt;
                else _queue.Enqueue(x);
            }
        }
        if (hit is not null) Raise();
        return hit;
    }

    public IReadOnlyList<(string Theme, string Prompt)> TunedQueue { get { lock (_gate) return _queue.ToArray(); } }

    /// <summary>v16.26.0 인터랙티브 핫스팟 CTR(있으면) — 디렉터 KPI 참고값.</summary>
    public double InteractiveCtr => _interactive?.Totals().Ctr ?? 0;

    public void Reset()
    {
        lock (_gate)
        {
            _records.Clear(); _history.Clear(); _queue.Clear();
            foreach (var g in _arms) foreach (var a in g) { a.N = 0; a.Sum = 0; }
            _generation = 0;
        }
        LastAction = "학습 초기화";
        Raise();
    }

    private void Raise()
    {
        var h = OnChange;
        if (h is null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action)d)(); } catch (Exception ex) { Program.Log("Feedback.OnChange", ex); }
        }
    }
}
