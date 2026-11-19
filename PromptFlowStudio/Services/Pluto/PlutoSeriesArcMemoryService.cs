using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>📺 편 간 이탈이 일어난 축 — 3초 훅 유지율 · 완주율 · 둘 다.</summary>
public enum PlutoSeriesDropAxis { None = 0, HookRetention = 1, Completion = 2, Both = 3 }

/// <summary>📺 시리즈 1편의 구성(훅 · 룩 · BGM) + 실측 성과(퍼센트 0~100 · 미측정 → <see cref="IsMeasured"/> false).</summary>
public sealed record PlutoSeriesEpisodeStat(int EpisodeNumber, string VideoId, string HookStyle, string ColorLookId, string BgmMood, bool IsMeasured,
    double HookRetentionPct, double CompletionPct, double CtrPct)
{
    /// <summary>유지 지수(0~100) = (3초 유지율 + 완주율) / 2 · 미측정 → 0.</summary>
    public double RetentionIndex => IsMeasured ? PlutoSeriesArcMemoryService.RetentionIndexOf(HookRetentionPct, CompletionPct) : 0.0;
}

/// <summary>📺 연속 측정 편 사이의 유지 지수 변화(퍼센트포인트 · 음수 = 이탈).</summary>
public sealed record PlutoEpisodeRetentionDelta(int FromEpisode, int ToEpisode, double DeltaPts, double HookDeltaPts, double CompletionDeltaPts);

/// <summary>
/// 📺 시리즈 아크 분석 — 편별 구성/성과 · 편 간 변화(<see cref="EpisodeRetentionDeltas"/>) · 기울기(p/편) · 이탈 판정 · 에스컬레이션 단계(0~3) · 앵커 모티프.
/// </summary>
public sealed record PlutoSeriesArcReport(string SeriesKey, string SeriesTitle, int TotalEpisodes, IReadOnlyList<PlutoSeriesEpisodeStat> Episodes,
    IReadOnlyList<PlutoEpisodeRetentionDelta> EpisodeRetentionDeltas, double RetentionSlope, double EpisodeRetentionDelta, int MeasuredCount, bool HasDropOff,
    PlutoSeriesDropAxis DropAxis, int EscalationLevel, bool HookOverheated, string AnchorColorLookId, string AnchorBgmMood, DateTimeOffset UpdatedAtUtc)
{
    public bool HasAnchor => AnchorColorLookId.Length > 0 || AnchorBgmMood.Length > 0;
}

/// <summary>
/// 📺 다음 편 연속성 지시 — 앞선 편의 앵커 룩/BGM 유지 + (이탈 감지 시) 궁금증 유발 오프닝 훅 · 클리프행어 아웃트로.
/// 근거 없음 → <see cref="None"/>(아무것도 바꾸지 않음).
/// </summary>
public sealed record PlutoSeriesContinuityDirective(string SeriesKey, int NextEpisodeNumber, int TotalEpisodes, string AnchorColorLookId, string AnchorBgmMood,
    int EscalationLevel, PlutoSeriesDropAxis DropAxis, string RecommendedHookLine, string CliffhangerOutroLine, double EpisodeRetentionDelta, string ReasonKorean)
{
    public static readonly PlutoSeriesContinuityDirective None = new(string.Empty, 0, 0, string.Empty, string.Empty, 0, PlutoSeriesDropAxis.None, string.Empty, string.Empty, 0.0, string.Empty);
    public bool HasAnchor => AnchorColorLookId.Length > 0 || AnchorBgmMood.Length > 0;
    /// <summary>편 간 이탈이 감지되어 훅/클리프행어 강화를 권고.</summary>
    public bool EscalateHook => EscalationLevel > 0;
    public bool HasGuidance => HasAnchor || EscalateHook;
}

/// <summary>📺 두뇌 리포트용 시리즈 아크 현황 — 추적 시리즈 · 측정 편 · 이탈 시리즈 수 · 대표 아크(최근 갱신 · 없음 → null).</summary>
public sealed record PlutoSeriesArcStatus(int TrackedSeriesCount, int MeasuredEpisodeCount, int DropOffSeriesCount, PlutoSeriesArcReport? FocusArc)
{
    public static readonly PlutoSeriesArcStatus Empty = new(0, 0, 0, null);
}

/// <summary>📺 Step 16 시리즈 아크 · 편 간 유지율 연속성 메모리.</summary>
public interface IPlutoSeriesArcMemoryService
{
    /// <summary>아크 메모리 선적재(멱등).</summary>
    Task EnsureLoadedAsync(CancellationToken ct = default);

    /// <summary>렌더 카탈로그의 시리즈 편(<c>시리즈 n/N편</c>)을 아크 메모리에 반영(구성 · 앵커) — 변경 수 반환 · 카탈로그 미연결 → 0.</summary>
    Task<int> SyncFromCatalogAsync(CancellationToken ct = default);

    /// <summary>편 성과 귀속 — 카탈로그(또는 텔레메트리 RenderMode)로 시리즈 편임이 확인될 때만 기록(true) · 비시리즈/비유한 지표 → false.</summary>
    Task<bool> RecordEpisodePerformanceAsync(PlutoVideoPerformanceTelemetry telemetry, CancellationToken ct = default);

    /// <summary>추적 중인 시리즈 아크(최근 갱신순 · 최대 <paramref name="maxCount"/>개).</summary>
    IReadOnlyList<PlutoSeriesArcReport> GetSeriesArcs(int maxCount = 8);

    /// <summary>다음 편 연속성 지시(메모리 기준 · 동기) — 미추적 시리즈 → <see cref="PlutoSeriesContinuityDirective.None"/>.</summary>
    PlutoSeriesContinuityDirective GetContinuityDirective(string? seriesThemeOrTitle, int totalEpisodes, int nextEpisodeNumber);

    /// <summary>적재 + 카탈로그 동기화 후 연속성 지시(실패 → [부분 구제] 메모리 기준 · 취소만 전파).</summary>
    Task<PlutoSeriesContinuityDirective> GetContinuityDirectiveAsync(string? seriesThemeOrTitle, int totalEpisodes, int nextEpisodeNumber, CancellationToken ct = default);

    /// <summary>두뇌 리포트용 현황.</summary>
    PlutoSeriesArcStatus GetStatus();

    /// <summary>아크 변경 통지(백그라운드 스레드 가능).</summary>
    event Action? Changed;
}

/// <summary>
/// 📺 <see cref="IPlutoSeriesArcMemoryService"/> 구현 — <c>pluto_series_arc.json</c>(☁️ Drive 볼트 · 로컬 폴백 · 원자 기록).
///   ▸ 렌더 카탈로그는 최근 100건만 보존하고 성과 수치를 담지 않는다 → 편별 구성 + 실측 성과를 별도 영속(카탈로그에서 밀려난 편도 아크는 유지).
///   ▸ 시리즈 식별 = 정규화(제목에서 "n/N편" 앞의 테마) + "#N" — 스키마 변경 없이 기존 카탈로그 항목(Step 9~15)에서 그대로 유도.
///   ▸ 유지 지수 = (3초 유지율 + 완주율) / 2 · <see cref="PlutoEpisodeRetentionDelta"/> = 연속 측정 편 간 변화(p) · 기울기 = 편 번호에 대한 최소제곱(p/편).
///   ▸ 이탈 = 최근 변화 ≤ −<see cref="DropThresholdPts"/> 또는 (측정 3편 이상 ∧ 기울기 ≤ −<see cref="DropThresholdPts"/>) →
///     단계 1~3(낙폭 10p · 20p 경계 · 연속 이탈 +1)의 궁금증 유발 오프닝 훅 + 클리프행어 아웃트로 권고.
///     단, 직전 편이 이미 클릭베이트(3초 ≥ 65% ∧ 완주 &lt; 20%)면 오프닝 훅은 더 올리지 않는다(과열 방지 — 아웃트로만).
///   ▸ 앵커 = 앞선 편들의 최빈 룩 / BGM 무드(동률 → 가장 앞선 편) — 다음 편 조립 시 그대로 유지해 시리즈 정체성을 잇는다.
///   ▸ 적재 불가(잠금·권한) 세션은 기존 파일 덮어쓰기 금지 · 영속 실패 → [부분 구제] 메모리 유지.
/// </summary>
public sealed class PlutoSeriesArcMemoryService : IPlutoSeriesArcMemoryService, IDisposable
{
    public const string FileName = "pluto_series_arc.json";
    public const int MaxSeries = 50;
    public const int MaxEpisodesPerSeries = 99;
    /// <summary>이탈 판정 임계(유지 지수 퍼센트포인트).</summary>
    public const double DropThresholdPts = 5.0;
    public const double Level2DropPts = 10.0, Level3DropPts = 20.0;
    public const int MaxEscalationLevel = 3;
    public const string OutroJoiner = " — ";

    /// <summary>궁금증 유발(curiosity gap) 오프닝 훅 — 단계 1~3.</summary>
    public static readonly IReadOnlyList<string> CuriosityGapHooks = new[]
    {
        "지난 편에서 못다 한 이야기, 지금 이어져요",
        "지난 편 마지막 장면, 그다음이 궁금하셨죠?",
        "이번 편을 놓치면 이야기의 절반을 놓쳐요",
    };

    /// <summary>클리프행어 아웃트로 — 단계 1~3.</summary>
    public static readonly IReadOnlyList<string> CliffhangerOutros = new[]
    {
        "다음 편에서 이야기가 이어져요",
        "진짜 이야기는 다음 편에서 시작돼요",
        "다음 편 첫 장면에서 답이 공개돼요",
    };

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly string _path;
    private readonly IPlutoStorageManager? _storage;
    private readonly IPlutoRenderCatalogService? _catalog;
    private readonly IPlutoMistakeNotebookService? _notebook;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<string, SeriesDto> _series = new(StringComparer.Ordinal);
    private volatile bool _loaded;
    private volatile bool _readOnly;
    private int _disposed;

    /// <summary>☁️ <see cref="IPlutoStorageManager"/> 경유 — Drive 우선 · 로컬 폴백.</summary>
    public PlutoSeriesArcMemoryService(IPlutoStorageManager storage, IPlutoRenderCatalogService? catalog = null, IPlutoMistakeNotebookService? notebook = null)
        : this((string?)null, catalog, notebook)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    public PlutoSeriesArcMemoryService(string? storagePath, IPlutoRenderCatalogService? catalog = null, IPlutoMistakeNotebookService? notebook = null)
    {
        _path = string.IsNullOrWhiteSpace(storagePath)
            ? Path.Combine(global::PromptFlowStudio.Services.Platform.AppDataPaths.LocalAppData, "PromptFlowStudio", FileName)
            : storagePath;
        _catalog = catalog;
        _notebook = notebook;
    }

    public string StoragePath => _storage?.ResolveDataPath(FileName) ?? _path;

    /// <summary>현재 시각(테스트 주입용).</summary>
    public Func<DateTimeOffset>? UtcNowProvider { get; init; }

    /// <summary>적재 불가 세션(디스크 보호 중).</summary>
    public bool IsReadOnlySession => _readOnly;

    public event Action? Changed;

    // ── 직렬화 DTO(선택 필드는 누락 허용 → 향후 스키마 확장과 양방향 호환) ──

    public sealed class EpisodeDto
    {
        public int N { get; set; }
        public string? VideoId { get; set; }
        public string? Hook { get; set; }
        public string? Look { get; set; }
        public string? Bgm { get; set; }
        public bool Measured { get; set; }
        public double HookPct { get; set; }
        public double CompletionPct { get; set; }
        public double CtrPct { get; set; }
        public DateTimeOffset RenderedUtc { get; set; }
        public DateTimeOffset MeasuredUtc { get; set; }
    }

    public sealed class SeriesDto
    {
        public string Key { get; set; } = string.Empty;
        public string? Title { get; set; }
        public int Total { get; set; }
        public DateTimeOffset UpdatedUtc { get; set; }
        public List<EpisodeDto>? Episodes { get; set; }
    }

    public sealed class Snapshot
    {
        public int Version { get; set; } = 1;
        public List<SeriesDto>? Series { get; set; }
    }

    // ── 순수 함수 ──

    /// <summary>유지 지수(0~100) = (3초 유지율 + 완주율) / 2 — 비유한/범위 밖 → 클램프.</summary>
    public static double RetentionIndexOf(double hookPct, double completionPct) => (Pct(hookPct) + Pct(completionPct)) / 2.0;

    private static double Pct(double v) => double.IsFinite(v) ? Math.Clamp(v, 0.0, 100.0) : 0.0;

    /// <summary>"n/N편" 표기 위치 탐색(첫 일치) — 없음 → false.</summary>
    private static bool TryFindPartTag(string s, out int index, out int length, out int n, out int total)
    {
        index = length = n = total = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (!IsAsciiDigit(s[i]) || (i > 0 && IsAsciiDigit(s[i - 1]))) continue;
            var j = i;
            var a = 0;
            while (j < s.Length && IsAsciiDigit(s[j]) && j - i < 3) { a = a * 10 + (s[j] - '0'); j++; }
            if (j >= s.Length || s[j] != '/') continue;
            var k = j + 1;
            var b = 0;
            while (k < s.Length && IsAsciiDigit(s[k]) && k - j - 1 < 3) { b = b * 10 + (s[k] - '0'); k++; }
            if (k == j + 1 || k >= s.Length || s[k] != '편') continue;
            if (a < 1 || b < 2 || a > b || b > MaxEpisodesPerSeries) continue;
            index = i; length = k + 1 - i; n = a; total = b;
            return true;
        }
        return false;
    }

    private static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';

    /// <summary>카탈로그 RenderMode(<c>시리즈 2/3편</c>) → (편 번호, 총 편수) — 시리즈가 아니면 false.</summary>
    public static bool TryParseEpisodeMode(string? renderMode, out int episodeNumber, out int totalEpisodes)
    {
        episodeNumber = totalEpisodes = 0;
        return !string.IsNullOrWhiteSpace(renderMode) && TryFindPartTag(renderMode, out _, out _, out episodeNumber, out totalEpisodes);
    }

    /// <summary>편 제목/테마 → 시리즈 테마("n/N편" 표기 앞부분 · 표기가 맨 앞이면 뒷부분 · 없음 → 전체).</summary>
    public static string SeriesThemeOf(string? titleOrTheme)
    {
        if (string.IsNullOrWhiteSpace(titleOrTheme)) return string.Empty;
        var s = titleOrTheme.Trim();
        if (!TryFindPartTag(s, out var idx, out var len, out _, out _)) return s;
        var head = s[..idx].Trim().TrimEnd('[', '(', '·', '-', '—').Trim();
        if (head.Length > 0) return head;
        return s[(idx + len)..].Trim().TrimStart(']', ')', '·', '-', '—').Trim();
    }

    /// <summary>시리즈 키 = 정규화 테마 + "#총편수" — 같은 테마라도 편수가 다르면 다른 시리즈.</summary>
    public static string SeriesKeyFor(string? titleOrTheme, int totalEpisodes)
    {
        var norm = PlutoRenderCatalogService.NormalizeKey(SeriesThemeOf(titleOrTheme));
        return (norm.Length == 0 ? "series" : norm) + "#" + Math.Max(0, totalEpisodes).ToString(Inv);
    }

    /// <summary>앞선 편(<paramref name="beforeEpisode"/> 미만 · ≤ 0 → 전체)의 최빈값(동률 → 가장 앞선 편) — BGM 은 <c>none</c>/<c>custom</c> 제외.</summary>
    public static string AnchorOf(IReadOnlyList<PlutoSeriesEpisodeStat> episodes, int beforeEpisode, bool bgm)
    {
        string best = string.Empty;
        int bestCount = 0, bestFirst = int.MaxValue;
        for (var i = 0; i < episodes.Count; i++)
        {
            var v = Motif(episodes[i], bgm);
            if (v.Length == 0 || (beforeEpisode > 0 && episodes[i].EpisodeNumber >= beforeEpisode)) continue;
            int count = 0, first = int.MaxValue;
            for (var k = 0; k < episodes.Count; k++)
            {
                if (beforeEpisode > 0 && episodes[k].EpisodeNumber >= beforeEpisode) continue;
                if (!string.Equals(Motif(episodes[k], bgm), v, StringComparison.OrdinalIgnoreCase)) continue;
                count++;
                if (episodes[k].EpisodeNumber < first) first = episodes[k].EpisodeNumber;
            }
            if (count > bestCount || (count == bestCount && first < bestFirst)) { best = v; bestCount = count; bestFirst = first; }
        }
        return best;
    }

    private static string Motif(PlutoSeriesEpisodeStat e, bool bgm)
    {
        var v = (bgm ? e.BgmMood : e.ColorLookId) ?? string.Empty;
        if (bgm && (v.Equals("none", StringComparison.OrdinalIgnoreCase) || v.Equals("custom", StringComparison.OrdinalIgnoreCase))) return string.Empty;
        return v;
    }

    /// <summary>
    /// 📺 아크 분석(순수 함수 · 결정론) — <paramref name="episodes"/> 는 순서 무관(편 번호로 정렬).
    /// <paramref name="beforeEpisode"/> &gt; 0 → 그 편 '이전' 편만으로 추세 · 앵커 계산(다음 편 지시용).
    /// </summary>
    public static PlutoSeriesArcReport Analyze(string seriesKey, string seriesTitle, int totalEpisodes, IReadOnlyList<PlutoSeriesEpisodeStat> episodes,
        DateTimeOffset updatedAtUtc = default, int beforeEpisode = 0)
    {
        ArgumentNullException.ThrowIfNull(episodes);
        var sorted = new List<PlutoSeriesEpisodeStat>(episodes.Count);
        for (var i = 0; i < episodes.Count; i++) if (episodes[i] is not null) sorted.Add(episodes[i]);
        sorted.Sort(static (a, b) => a.EpisodeNumber.CompareTo(b.EpisodeNumber));

        var measured = new List<PlutoSeriesEpisodeStat>(sorted.Count);
        for (var i = 0; i < sorted.Count; i++)
            if (sorted[i].IsMeasured && (beforeEpisode <= 0 || sorted[i].EpisodeNumber < beforeEpisode)) measured.Add(sorted[i]);

        var deltas = new PlutoEpisodeRetentionDelta[Math.Max(0, measured.Count - 1)];
        for (var i = 1; i < measured.Count; i++)
        {
            var p = measured[i - 1];
            var c = measured[i];
            deltas[i - 1] = new PlutoEpisodeRetentionDelta(p.EpisodeNumber, c.EpisodeNumber, c.RetentionIndex - p.RetentionIndex,
                Pct(c.HookRetentionPct) - Pct(p.HookRetentionPct), Pct(c.CompletionPct) - Pct(p.CompletionPct));
        }

        // 최소제곱 기울기(유지 지수 p / 편)
        var slope = 0.0;
        if (measured.Count >= 2)
        {
            double sx = 0, sy = 0;
            for (var i = 0; i < measured.Count; i++) { sx += measured[i].EpisodeNumber; sy += measured[i].RetentionIndex; }
            double mx = sx / measured.Count, my = sy / measured.Count, num = 0, den = 0;
            for (var i = 0; i < measured.Count; i++)
            {
                var dx = measured[i].EpisodeNumber - mx;
                num += dx * (measured[i].RetentionIndex - my);
                den += dx * dx;
            }
            slope = den > 0 ? num / den : 0.0;
        }

        var latest = deltas.Length > 0 ? deltas[^1].DeltaPts : 0.0;
        var drop = deltas.Length > 0 && (latest <= -DropThresholdPts || (measured.Count >= 3 && slope <= -DropThresholdPts));
        var axis = PlutoSeriesDropAxis.None;
        var level = 0;
        if (drop)
        {
            var last = deltas[^1];
            bool hookFell = last.HookDeltaPts <= -DropThresholdPts, completionFell = last.CompletionDeltaPts <= -DropThresholdPts;
            axis = hookFell && completionFell ? PlutoSeriesDropAxis.Both
                : hookFell ? PlutoSeriesDropAxis.HookRetention
                : completionFell ? PlutoSeriesDropAxis.Completion
                : last.HookDeltaPts <= last.CompletionDeltaPts ? PlutoSeriesDropAxis.HookRetention : PlutoSeriesDropAxis.Completion;
            var magnitude = Math.Max(-latest, -slope);
            level = magnitude >= Level3DropPts ? 3 : magnitude >= Level2DropPts ? 2 : 1;
            if (deltas.Length >= 2 && deltas[^1].DeltaPts <= -DropThresholdPts && deltas[^2].DeltaPts <= -DropThresholdPts) level++;   // 연속 이탈
            level = Math.Min(MaxEscalationLevel, level);
        }
        var overheated = measured.Count > 0
            && Pct(measured[^1].HookRetentionPct) >= PlutoParetoFrontier.ClickbaitHook * 100.0
            && Pct(measured[^1].CompletionPct) < PlutoParetoFrontier.ClickbaitCompletion * 100.0;

        return new PlutoSeriesArcReport(seriesKey ?? string.Empty, seriesTitle ?? string.Empty, totalEpisodes, sorted, deltas, slope, latest, measured.Count, drop, axis, level, overheated,
            AnchorOf(sorted, beforeEpisode, bgm: false), AnchorOf(sorted, beforeEpisode, bgm: true), updatedAtUtc);
    }

    /// <summary>
    /// 📺 분석 → 다음 편 지시(순수 함수) — 앵커는 항상 · 훅/아웃트로는 이탈 감지 시에만.
    /// <paramref name="suppressed"/>(오답 노트 차단 토큰)에 걸린 문구는 낮은 단계로 대체(전부 차단 → 생략).
    /// </summary>
    public static PlutoSeriesContinuityDirective BuildDirective(PlutoSeriesArcReport arc, int nextEpisodeNumber, IReadOnlySet<string>? suppressed = null)
    {
        ArgumentNullException.ThrowIfNull(arc);
        if (!arc.HasAnchor && !arc.HasDropOff) return PlutoSeriesContinuityDirective.None;
        string hook = string.Empty, outro = string.Empty, reason;
        if (arc.HasDropOff)
        {
            if (!arc.HookOverheated) hook = PickLine(CuriosityGapHooks, arc.EscalationLevel, suppressed);
            if (arc.TotalEpisodes <= 0 || nextEpisodeNumber < arc.TotalEpisodes) outro = PickLine(CliffhangerOutros, arc.EscalationLevel, suppressed);   // 완결편은 다음 편 예고 없음
            var d = arc.EpisodeRetentionDeltas[^1];
            reason = "📉 " + d.FromEpisode.ToString(Inv) + "편→" + d.ToEpisode.ToString(Inv) + "편 유지 지수 " + Signed(d.DeltaPts) + "p(3초 " + Signed(d.HookDeltaPts) + " · 완주 " + Signed(d.CompletionDeltaPts)
                + ") → " + (arc.HookOverheated ? "훅 과열 — 클리프행어 아웃트로만" : "궁금증 훅 + 클리프행어") + " Lv" + arc.EscalationLevel.ToString(Inv);
        }
        else reason = arc.MeasuredCount >= 2 ? "✅ 편 간 유지 양호(" + Signed(arc.EpisodeRetentionDelta) + "p) — 앵커 모티프 유지" : "앵커 모티프 유지(편 성과 2편부터 이탈 추적)";
        return new PlutoSeriesContinuityDirective(arc.SeriesKey, nextEpisodeNumber, arc.TotalEpisodes, arc.AnchorColorLookId, arc.AnchorBgmMood,
            arc.HasDropOff ? arc.EscalationLevel : 0, arc.DropAxis, hook, outro, arc.EpisodeRetentionDelta, reason);
    }

    private static string PickLine(IReadOnlyList<string> lines, int level, IReadOnlySet<string>? suppressed)
    {
        for (var i = Math.Clamp(level, 1, lines.Count) - 1; i >= 0; i--)
            if (suppressed is null || !suppressed.Contains(lines[i])) return lines[i];
        return string.Empty;
    }

    private static string Signed(double v) => (v >= 0 ? "+" : "−") + Math.Abs(v).ToString("0.#", Inv);

    /// <summary>
    /// 💬 지시를 3막 자막에 반영(순수 함수 · 구간 불변 · 멱등) — 첫 큐 = 궁금증 오프닝 훅 · 마지막 큐 뒤에 클리프행어 아웃트로.
    /// "n/N편" 장식(<see cref="PlutoSeriesBatchPlanner.DecorateSubtitles"/>) '이전'에 호출한다. 권고 없음 → 원본 그대로.
    /// </summary>
    public static IReadOnlyList<SubtitleCue> ApplyDirective(IReadOnlyList<SubtitleCue> cues, PlutoSeriesContinuityDirective? directive)
    {
        ArgumentNullException.ThrowIfNull(cues);
        if (directive is null || !directive.EscalateHook || cues.Count == 0) return cues;
        var hook = directive.RecommendedHookLine;
        var outro = directive.CliffhangerOutroLine;
        if (hook.Length == 0 && outro.Length == 0) return cues;
        var arr = new SubtitleCue[cues.Count];
        for (var i = 0; i < arr.Length; i++) arr[i] = cues[i];
        if (hook.Length > 0 && !(arr[0].Text ?? string.Empty).Contains(hook, StringComparison.Ordinal)) arr[0] = arr[0] with { Text = hook };
        var last = arr.Length - 1;
        if (outro.Length > 0 && !(arr[last].Text ?? string.Empty).Contains(outro, StringComparison.Ordinal))
            arr[last] = arr[last] with { Text = string.IsNullOrWhiteSpace(arr[last].Text) ? outro : arr[last].Text + OutroJoiner + outro };
        return arr;
    }

    /// <summary>📺 두뇌 리포트 1줄(결정론 한국어).</summary>
    public static string BuildArcLabel(PlutoSeriesArcStatus? status)
    {
        if (status?.FocusArc is not { } arc) return "기록 없음 · 시리즈 2편 이상 성과 입력부터 편 간 유지율 추적";
        var sb = new StringBuilder(128);
        sb.Append('\'').Append(arc.SeriesTitle.Length > 0 ? arc.SeriesTitle : "시리즈").Append("' ").Append(arc.MeasuredCount.ToString(Inv)).Append('/').Append(arc.TotalEpisodes.ToString(Inv)).Append("편 측정");
        if (arc.EpisodeRetentionDeltas.Count > 0)
        {
            var d = arc.EpisodeRetentionDeltas[^1];
            sb.Append(" · ").Append(d.FromEpisode.ToString(Inv)).Append('→').Append(d.ToEpisode.ToString(Inv)).Append("편 ").Append(Signed(d.DeltaPts)).Append("p · 기울기 ").Append(Signed(arc.RetentionSlope)).Append("p/편");
            sb.Append(arc.HasDropOff
                ? " · 📉 이탈 → " + (arc.HookOverheated ? "아웃트로" : "훅") + " 에스컬레이션 Lv" + arc.EscalationLevel.ToString(Inv)
                : " · ✅ 유지");
        }
        else sb.Append(" · 편 간 비교 대기");
        if (arc.HasAnchor) sb.Append(" · 앵커 ").Append(arc.AnchorColorLookId.Length > 0 ? arc.AnchorColorLookId : "—").Append(" / ").Append(arc.AnchorBgmMood.Length > 0 ? arc.AnchorBgmMood : "—");
        if (status.TrackedSeriesCount > 1) sb.Append(" · 추적 ").Append(status.TrackedSeriesCount.ToString(Inv)).Append("개");
        if (status.DropOffSeriesCount > 1) sb.Append("(이탈 ").Append(status.DropOffSeriesCount.ToString(Inv)).Append(')');
        return sb.ToString();
    }

    // ── 적재 · 영속 ──

    private DateTimeOffset Now() => UtcNowProvider?.Invoke() ?? DateTimeOffset.UtcNow;

    private static string Clip(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var t = s.Trim();
        return t.Length > PlutoRenderCatalogService.MaxFieldLength ? t[..PlutoRenderCatalogService.MaxFieldLength] : t;
    }

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loaded) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loaded) return;
            var (data, status) = await PlutoJsonStore.TryLoadAsync<Snapshot>(StoragePath, "Pluto.SeriesArc", ct).ConfigureAwait(false);
            _readOnly = status == PlutoLoadStatus.Unreadable;
            lock (_sync)
            {
                _series.Clear();
                if (data?.Series is { } list)
                    foreach (var s in list)
                    {
                        if (s is null || string.IsNullOrWhiteSpace(s.Key) || _series.Count >= MaxSeries) continue;
                        var clean = new SeriesDto { Key = s.Key.Trim(), Title = Clip(s.Title), Total = Math.Clamp(s.Total, 0, MaxEpisodesPerSeries), UpdatedUtc = s.UpdatedUtc, Episodes = new List<EpisodeDto>() };
                        if (s.Episodes is not null)
                            foreach (var e in s.Episodes)
                            {
                                if (e is null || e.N < 1 || e.N > MaxEpisodesPerSeries || FindEpisode(clean, e.N) is not null) continue;
                                var finite = double.IsFinite(e.HookPct) && double.IsFinite(e.CompletionPct) && double.IsFinite(e.CtrPct);
                                clean.Episodes.Add(new EpisodeDto
                                {
                                    N = e.N, VideoId = Clip(e.VideoId), Hook = Clip(e.Hook), Look = Clip(e.Look), Bgm = Clip(e.Bgm), Measured = e.Measured && finite,
                                    HookPct = Pct(e.HookPct), CompletionPct = Pct(e.CompletionPct), CtrPct = Pct(e.CtrPct), RenderedUtc = e.RenderedUtc, MeasuredUtc = e.MeasuredUtc,
                                });
                            }
                        _series[clean.Key] = clean;
                    }
            }
            _loaded = true;
        }
        finally { _gate.Release(); }
    }

    private static EpisodeDto? FindEpisode(SeriesDto s, int n)
    {
        if (s.Episodes is null) return null;
        for (var i = 0; i < s.Episodes.Count; i++) if (s.Episodes[i].N == n) return s.Episodes[i];
        return null;
    }

    /// <summary>시리즈 조회/생성(잠금 안) — 상한 초과 시 가장 오래된 시리즈 축출.</summary>
    private SeriesDto GetOrAddLocked(string key, string title, int total, DateTimeOffset now)
    {
        if (_series.TryGetValue(key, out var s)) return s;
        if (_series.Count >= MaxSeries)
        {
            string? oldest = null;
            var oldestUtc = DateTimeOffset.MaxValue;
            foreach (var kv in _series)
                if (kv.Value.UpdatedUtc < oldestUtc || (kv.Value.UpdatedUtc == oldestUtc && string.CompareOrdinal(kv.Key, oldest) < 0)) { oldest = kv.Key; oldestUtc = kv.Value.UpdatedUtc; }
            if (oldest is not null) _series.Remove(oldest);
        }
        s = new SeriesDto { Key = key, Title = title, Total = total, UpdatedUtc = now, Episodes = new List<EpisodeDto>(Math.Min(total, 8)) };
        _series[key] = s;
        return s;
    }

    private async Task<bool> PersistAsync(CancellationToken ct)
    {
        if (_readOnly) return false;   // 🛡️ 적재 불가 파일 보호
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string json;
            lock (_sync)
            {
                var list = new List<SeriesDto>(_series.Count);
                foreach (var kv in _series) list.Add(kv.Value);
                list.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));   // 결정론 순서
                json = JsonSerializer.Serialize(new Snapshot { Series = list }, JsonOpts);
            }
            return _storage is not null
                ? await _storage.WriteAllTextAtomicAsync(FileName, json, ct).ConfigureAwait(false)
                : await PlutoJsonStore.WriteAtomicAsync(_path, json, "Pluto.SeriesArc", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.SeriesArc] persist: " + ex.Message); return false; }   // [부분 구제]
        finally { _gate.Release(); }
    }

    // ── 카탈로그 동기화 · 성과 귀속 ──

    public async Task<int> SyncFromCatalogAsync(CancellationToken ct = default)
    {
        if (_catalog is null) return 0;
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        var clips = await _catalog.GetRecentClipsAsync(PlutoRenderCatalogService.Capacity, ct).ConfigureAwait(false);
        var changes = 0;
        lock (_sync)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);   // 카탈로그는 최신순 → 편마다 가장 최근 렌더만 채택
            for (var i = 0; i < clips.Count; i++)
            {
                var c = clips[i];
                if (c is null || !TryParseEpisodeMode(c.RenderMode, out var n, out var total)) continue;
                var key = SeriesKeyFor(c.Title, total);
                if (!seen.Add(key + "|" + n.ToString(Inv))) continue;
                var s = GetOrAddLocked(key, SeriesThemeOf(c.Title), total, c.RenderedAtUtc);
                var e = FindEpisode(s, n);
                if (e is null)
                {
                    (s.Episodes ??= new List<EpisodeDto>()).Add(new EpisodeDto { N = n, VideoId = c.VideoId, Hook = c.HookStyle, Look = c.ColorLookId, Bgm = c.BgmMood, RenderedUtc = c.RenderedAtUtc });
                    if (c.RenderedAtUtc > s.UpdatedUtc) s.UpdatedUtc = c.RenderedAtUtc;
                    changes++;
                    continue;
                }
                // 측정된 편은 단순 재렌더로 덮지 않는다(실측 성과는 그 영상의 것) — 같은 영상이거나 미측정일 때만 구성 갱신
                var same = string.Equals(e.VideoId, c.VideoId, StringComparison.OrdinalIgnoreCase);
                if (e.Measured && !same) continue;
                if (same && string.Equals(e.Hook, c.HookStyle, StringComparison.Ordinal) && string.Equals(e.Look, c.ColorLookId, StringComparison.Ordinal)
                    && string.Equals(e.Bgm, c.BgmMood, StringComparison.Ordinal)) continue;
                e.VideoId = c.VideoId; e.Hook = c.HookStyle; e.Look = c.ColorLookId; e.Bgm = c.BgmMood; e.RenderedUtc = c.RenderedAtUtc;
                if (c.RenderedAtUtc > s.UpdatedUtc) s.UpdatedUtc = c.RenderedAtUtc;
                changes++;
            }
        }
        if (changes == 0) return 0;
        await PersistAsync(ct).ConfigureAwait(false);
        RaiseChanged();
        return changes;
    }

    public async Task<bool> RecordEpisodePerformanceAsync(PlutoVideoPerformanceTelemetry telemetry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        if (string.IsNullOrWhiteSpace(telemetry.VideoId)) return false;
        if (!double.IsFinite(telemetry.ThreeSecondHookRetentionPct) || !double.IsFinite(telemetry.AverageCompletionPct) || !double.IsFinite(telemetry.ClickThroughRatePct)) return false;
        await EnsureLoadedAsync(ct).ConfigureAwait(false);

        PlutoRenderedClipRecord? clip = null;
        if (_catalog is not null)
        {
            try { clip = await _catalog.FindByVideoIdOrTitleAsync(telemetry.VideoId, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.SeriesArc] catalog lookup: " + ex.Message); }   // [부분 구제] 텔레메트리 RenderMode 로 판정
        }
        int n, total;
        string titleSource;
        if (clip is not null && TryParseEpisodeMode(clip.RenderMode, out n, out total)) titleSource = clip.Title;
        else if (TryParseEpisodeMode(telemetry.RenderMode, out n, out total)) titleSource = clip?.Title is { Length: > 0 } t ? t : telemetry.VideoId;
        else return false;   // 시리즈 편이 아님

        var videoId = Clip(clip?.VideoId is { Length: > 0 } cid ? cid : telemetry.VideoId);
        var now = Now();
        lock (_sync)
        {
            var s = GetOrAddLocked(SeriesKeyFor(titleSource, total), SeriesThemeOf(titleSource), total, now);
            var e = FindEpisode(s, n);
            if (e is null) { e = new EpisodeDto { N = n, RenderedUtc = clip?.RenderedAtUtc ?? default }; (s.Episodes ??= new List<EpisodeDto>()).Add(e); }
            e.VideoId = videoId;
            e.Hook = FirstNonEmpty(telemetry.HookStyle, clip?.HookStyle, e.Hook);
            e.Look = FirstNonEmpty(telemetry.ColorLookId, clip?.ColorLookId, e.Look);
            e.Bgm = FirstNonEmpty(telemetry.BgmMood, clip?.BgmMood, e.Bgm);
            e.Measured = true;
            e.HookPct = Pct(telemetry.ThreeSecondHookRetentionPct);
            e.CompletionPct = Pct(telemetry.AverageCompletionPct);
            e.CtrPct = Pct(telemetry.ClickThroughRatePct);
            e.MeasuredUtc = now;
            s.UpdatedUtc = now;
        }
        await PersistAsync(ct).ConfigureAwait(false);
        RaiseChanged();
        return true;
    }

    private static string FirstNonEmpty(string? a, string? b, string? c)
        => !string.IsNullOrWhiteSpace(a) ? Clip(a) : !string.IsNullOrWhiteSpace(b) ? Clip(b) : Clip(c);

    // ── 조회 ──

    private static PlutoSeriesArcReport ReportLocked(SeriesDto s, int beforeEpisode)
    {
        var eps = new PlutoSeriesEpisodeStat[s.Episodes?.Count ?? 0];
        for (var i = 0; i < eps.Length; i++)
        {
            var e = s.Episodes![i];
            eps[i] = new PlutoSeriesEpisodeStat(e.N, e.VideoId ?? string.Empty, e.Hook ?? string.Empty, e.Look ?? string.Empty, e.Bgm ?? string.Empty, e.Measured, e.HookPct, e.CompletionPct, e.CtrPct);
        }
        return Analyze(s.Key, s.Title ?? string.Empty, s.Total, eps, s.UpdatedUtc, beforeEpisode);
    }

    public IReadOnlyList<PlutoSeriesArcReport> GetSeriesArcs(int maxCount = 8)
    {
        if (maxCount <= 0) return Array.Empty<PlutoSeriesArcReport>();
        var list = new List<PlutoSeriesArcReport>();
        lock (_sync) foreach (var kv in _series) list.Add(ReportLocked(kv.Value, 0));
        list.Sort(static (a, b) =>
        {
            var c = b.UpdatedAtUtc.CompareTo(a.UpdatedAtUtc);
            return c != 0 ? c : string.CompareOrdinal(a.SeriesKey, b.SeriesKey);
        });
        if (list.Count > maxCount) list.RemoveRange(maxCount, list.Count - maxCount);
        return list;
    }

    public PlutoSeriesContinuityDirective GetContinuityDirective(string? seriesThemeOrTitle, int totalEpisodes, int nextEpisodeNumber)
    {
        if (nextEpisodeNumber <= 1) return PlutoSeriesContinuityDirective.None;   // 1편은 이어받을 앞선 편이 없다
        PlutoSeriesArcReport arc;
        lock (_sync)
        {
            if (!_series.TryGetValue(SeriesKeyFor(seriesThemeOrTitle, totalEpisodes), out var s)) return PlutoSeriesContinuityDirective.None;
            arc = ReportLocked(s, nextEpisodeNumber);
        }
        IReadOnlySet<string>? suppressed = null;
        try { suppressed = _notebook?.GetSuppressedTokens(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.SeriesArc] suppressed: " + ex.Message); }
        return BuildDirective(arc, nextEpisodeNumber, suppressed);
    }

    public async Task<PlutoSeriesContinuityDirective> GetContinuityDirectiveAsync(string? seriesThemeOrTitle, int totalEpisodes, int nextEpisodeNumber, CancellationToken ct = default)
    {
        try
        {
            await EnsureLoadedAsync(ct).ConfigureAwait(false);
            await SyncFromCatalogAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.SeriesArc] sync: " + ex.Message); }   // [부분 구제] 메모리 기준
        return GetContinuityDirective(seriesThemeOrTitle, totalEpisodes, nextEpisodeNumber);
    }

    public PlutoSeriesArcStatus GetStatus()
    {
        var arcs = GetSeriesArcs(MaxSeries);
        if (arcs.Count == 0) return PlutoSeriesArcStatus.Empty;
        int measured = 0, drops = 0;
        PlutoSeriesArcReport? focus = null;
        for (var i = 0; i < arcs.Count; i++)
        {
            measured += arcs[i].MeasuredCount;
            if (arcs[i].HasDropOff) drops++;
            if (focus is null && arcs[i].MeasuredCount >= 2) focus = arcs[i];   // 최근 갱신순 → 편 간 비교가 가능한 첫 시리즈
        }
        return new PlutoSeriesArcStatus(arcs.Count, measured, drops, focus ?? arcs[0]);
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.SeriesArc] Changed handler: " + ex.Message); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Changed = null;
        _gate.Dispose();
    }
}
