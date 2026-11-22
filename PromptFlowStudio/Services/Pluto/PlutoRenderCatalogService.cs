using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🎞️ 렌더 완료 영상 메타 — 성과 피드백 귀속(훅 · 룩 · BGM · 태그) + 학습 반영 여부.</summary>
public sealed record PlutoRenderedClipRecord(string VideoId, string Title, string RenderMode, string HookStyle, string ColorLookId, string BgmMood,
    IReadOnlyList<string> Tags, string OutputPath, DateTimeOffset RenderedAtUtc, bool TelemetryLogged)
{
    /// <summary>📺 Step 22 — 게시된 YouTube 동영상 ID(11자 · 미연결 → null). 한 번 연결되면 다음 내보내기부터 O(1) 매칭.</summary>
    public string? YouTubeVideoId { get; init; }

    /// <summary>📈 Step 22 — 마지막으로 학습에 반영한 YouTube 스냅샷(성숙도 증분 학습 기준 · 미반영 → null).</summary>
    public PlutoYouTubeIngestWatermark? YouTubeWatermark { get; init; }

    /// <summary>🅰️🅱️ Step 25 — 크리에이터가 복사한 업로드 제목 변형(<c>"A"</c> · <c>"B"</c> · 미기록 → null).</summary>
    public string? SelectedTitleVariant { get; init; }

    /// <summary>🅰️🅱️ 선택된 제목의 프레이밍(<see cref="PlutoTitleFramings"/>).</summary>
    public string? PublishedTitleFraming { get; init; }

    /// <summary>🅰️🅱️ 선택된 제목 원문.</summary>
    public string? PublishedTitleText { get; init; }

    /// <summary>🅰️🅱️ 전체 패키지 복사 시 아직 확정되지 않은 반대 변형의 프레이밍(실제 게시 제목으로 판정 · 판정 후 null).</summary>
    public string? AlternateTitleFraming { get; init; }

    /// <summary>🅰️🅱️ 반대 변형 제목 원문(판정 대기).</summary>
    public string? AlternateTitleText { get; init; }

    /// <summary>🅰️🅱️ 귀속 상태 — <see cref="PlutoTitleAttributionStates"/>(Copied → Confirmed | Diverged).</summary>
    public string? TitleAttribution { get; init; }

    /// <summary>🎯 Step 27 — [기획 즉시 적용]으로 만든 클립의 기획안 ID(미적용 · 레거시 → null).</summary>
    public string? AppliedBriefId { get; init; }

    /// <summary>🎯 적용 기획안 형식(<c>단편</c> · <c>시리즈 3부작</c>).</summary>
    public string? AppliedBriefFormat { get; init; }

    /// <summary>🎯 기획 시점 기대 합성 지표(3초·완주·CTR 합성 0~100 · <see cref="PlutoBriefOutcomeAttributionService.Composite"/>).</summary>
    public double? AppliedBriefExpectedRetention { get; init; }

    /// <summary>🎯 기획안 제목 프레이밍.</summary>
    public string? AppliedBriefTitleFraming { get; init; }

    /// <summary>🎯 실측 채점 완료(클립당 정확히 1회 · 멀티 PC 병합 시 OR 유지).</summary>
    public bool AppliedBriefEvaluated { get; init; }

    /// <summary>🎯 기획안 종류(<see cref="PlutoTopicBriefKinds"/> — 활용/탐색 적중률 버킷).</summary>
    public string? AppliedBriefKind { get; init; }

    /// <summary>🎯 기획안 훅 스타일 라벨(사후 분석 조합 키 — 클립 HookStyle 은 자막 원문일 수 있음).</summary>
    public string? AppliedBriefHookStyle { get; init; }

    /// <summary>🎯 기획안 룩(사후 분석 조합 키).</summary>
    public string? AppliedBriefColorLookId { get; init; }

    /// <summary>🎯 기획안 BGM 무드.</summary>
    public string? AppliedBriefBgmMood { get; init; }

    /// <summary>🎯 기획안 테마(도메인 분류 · 사후 분석 사유).</summary>
    public string? AppliedBriefTheme { get; init; }

    /// <summary>🎯 적용 시각 — (기획안 ID, 적용 시각) = 기획 결정 1건(시리즈 여러 편은 1건으로 집계).</summary>
    public DateTimeOffset? AppliedBriefAppliedAtUtc { get; init; }

    /// <summary>🎯 채점 결과 SurpriseDelta = 실측 합성 − 기대(채점 전 → null).</summary>
    public double? AppliedBriefSurpriseDelta { get; init; }

    /// <summary>🎯 적용 기획안 추적 중.</summary>
    public bool HasAppliedBrief => !string.IsNullOrEmpty(AppliedBriefId);
}

/// <summary>🅰️🅱️ Step 25 — 제목 귀속 상태 상수.</summary>
public static class PlutoTitleAttributionStates
{
    /// <summary>복사만 됨(게시 미확인).</summary>
    public const string Copied = "Copied";
    /// <summary>실제 YouTube 제목과 일치 확인 → CTR 귀속 대상.</summary>
    public const string Confirmed = "Confirmed";
    /// <summary>게시 전 크리에이터가 제목을 크게 고침 → 귀속 제외(오귀속 방지).</summary>
    public const string Diverged = "Diverged";

    public static bool IsKnown(string? s) => s is Copied or Confirmed or Diverged;
}

/// <summary>🅰️🅱️ Step 25 — 업로드 제목 선택 기록 요청.</summary>
/// <param name="Variant"><c>"A"</c> 또는 <c>"B"</c>.</param>
/// <param name="Framing">선택 제목 프레이밍.</param>
/// <param name="Text">선택 제목 원문.</param>
/// <param name="AlternateFraming">전체 패키지 복사 시 반대 변형 프레이밍(단일 제목 복사 → null).</param>
/// <param name="AlternateText">전체 패키지 복사 시 반대 변형 원문.</param>
public sealed record PlutoPublishedTitleSelection(string Variant, string Framing, string Text, string? AlternateFraming = null, string? AlternateText = null);

/// <summary>📈 YouTube 스냅샷 반영 워터마크 — 반영 시점 조회수 · 성숙도 단계(0 = 최초 반영) · 반영 지표.</summary>
public sealed record PlutoYouTubeIngestWatermark(long Views, int Stage, double HookPct, double CompletionPct, double CtrPct, DateTimeOffset UpdatedUtc);

/// <summary>📺 YouTube ID 연결 결과.</summary>
public enum PlutoYouTubeBindResult
{
    /// <summary>새로 연결.</summary>
    Bound,
    /// <summary>이미 같은 ID로 연결됨.</summary>
    AlreadyBound,
    /// <summary>클립이 다른 YouTube ID에 이미 연결됨(덮어쓰기 거부).</summary>
    ClipBoundElsewhere,
    /// <summary>YouTube ID가 다른 클립에 이미 연결됨(1:1 위반 거부).</summary>
    IdTakenByOtherClip,
    /// <summary>클립 없음 · 잘못된 ID.</summary>
    NotFound,
}

/// <summary>🎞️ 영속 렌더 카탈로그 — 시리즈 편별 · 재시작 후에도 최근 렌더 영상을 성과 피드백에 다시 선택.</summary>
public interface IPlutoRenderCatalogService
{
    /// <summary>렌더 기록(같은 VideoId → 최신 메타로 교체 후 맨 앞 · 상한 <see cref="PlutoRenderCatalogService.Capacity"/>건).</summary>
    Task RecordRenderedClipAsync(PlutoRenderedClipRecord record, CancellationToken ct = default);

    /// <summary>최신순 최대 <paramref name="maxCount"/>건.</summary>
    Task<IReadOnlyList<PlutoRenderedClipRecord>> GetRecentClipsAsync(int maxCount = 30, CancellationToken ct = default);

    /// <summary>VideoId(대소문자 무시) → 정규화 제목 → 정규화 VideoId 순 조회(없음 → null).</summary>
    Task<PlutoRenderedClipRecord?> FindByVideoIdOrTitleAsync(string videoIdOrTitle, CancellationToken ct = default);

    /// <summary>학습 반영 완료 표시(미추적 → 무시).</summary>
    Task MarkTelemetryLoggedAsync(string videoId, CancellationToken ct = default);

    /// <summary>카탈로그 변경 통지(백그라운드 스레드 가능).</summary>
    event Action? Changed;

    /// <summary>📺 YouTube 동영상 ID로 조회(Tier 1 · 없음 → null).</summary>
    Task<PlutoRenderedClipRecord?> FindByYouTubeVideoIdAsync(string youTubeVideoId, CancellationToken ct = default);

    /// <summary>📺 클립 ↔ YouTube ID 1:1 연결을 게이트 안에서 원자적으로 기록(기존 다른 연결은 덮어쓰지 않음).</summary>
    Task<PlutoYouTubeBindResult> TryBindYouTubeVideoIdAsync(string clipVideoId, string youTubeVideoId, CancellationToken ct = default);

    /// <summary>📈 반영 워터마크 갱신 — 조회수/단계가 기존보다 앞설 때만(단조 · 되감기 거부). 반영됨 → true.</summary>
    Task<bool> UpdateYouTubeWatermarkAsync(string clipVideoId, PlutoYouTubeIngestWatermark watermark, CancellationToken ct = default);

    /// <summary>🅰️🅱️ Step 25 — 복사된 업로드 제목 변형 기록(실측 판정 완료 클립은 동결 · 기록됨 → true). 기본 구현 = 미지원(false).</summary>
    Task<bool> RecordPublishedTitleAsync(string clipVideoId, PlutoPublishedTitleSelection selection, CancellationToken ct = default) => Task.FromResult(false);

    /// <summary>🅰️🅱️ Step 25 — 연결된 실제 YouTube 제목으로 변형 귀속 판정(Confirmed · Diverged · 변형 교정). 변경됨 → true. 기본 구현 = 미지원(false).</summary>
    Task<bool> ResolvePublishedTitleAsync(string clipVideoId, string? realizedTitle, CancellationToken ct = default) => Task.FromResult(false);

    /// <summary>🎯 Step 27 — 적용 기획안 채점 결과를 게이트 안에서 원자적으로 1회 기록(미추적 · 이미 채점 → false = 정확히 1회 CAS). 기본 구현 = 미지원(false).</summary>
    Task<bool> TryRecordBriefOutcomeAsync(string clipVideoId, double surpriseDelta, CancellationToken ct = default) => Task.FromResult(false);
}

/// <summary>
/// 🎞️ <see cref="IPlutoRenderCatalogService"/> 구현 — <c>pluto_render_catalog.json</c>(☁️ Drive 볼트 · 로컬 폴백 · 원자 기록).
/// 적재 불가(잠금·권한) 세션은 기존 파일 덮어쓰기 금지 · 영속 실패 → [부분 구제] 메모리 유지.
/// </summary>
public sealed class PlutoRenderCatalogService : IPlutoRenderCatalogService, IPlutoHotReloadable, IDisposable
{
    public const string FileName = "pluto_render_catalog.json";
    public const int Capacity = 100;
    public const int MaxFieldLength = 300;
    public const int MaxTags = 12;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly string _path;
    private readonly IPlutoStorageManager? _storage;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<PlutoRenderedClipRecord> _clips = new();   // 최신순
    private volatile bool _loaded;
    private volatile bool _readOnly;
    private volatile bool _hasUnappliedDiskUpdate;   // 🚧 Step 21 — 잠긴 재적재 이후 기록 펜스(_gate 안에서만 전환)
    private int _disposed;

    /// <summary>☁️ <see cref="IPlutoStorageManager"/> 경유 — Drive 우선 · 로컬 폴백.</summary>
    public PlutoRenderCatalogService(IPlutoStorageManager storage) : this((string?)null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    public PlutoRenderCatalogService(string? storagePath)
    {
        _path = string.IsNullOrWhiteSpace(storagePath)
            ? Path.Combine(global::PromptFlowStudio.Services.Platform.AppDataPaths.LocalAppData, "PromptFlowStudio", FileName)
            : storagePath;
    }

    public string StoragePath => _storage?.ResolveDataPath(FileName) ?? _path;

    public event Action? Changed;

    /// <summary>직렬화 DTO.</summary>
    public sealed class ClipDto
    {
        public string VideoId { get; set; } = string.Empty;
        public string? Title { get; set; }
        public string? RenderMode { get; set; }
        public string? HookStyle { get; set; }
        public string? ColorLookId { get; set; }
        public string? BgmMood { get; set; }
        public string[]? Tags { get; set; }
        public string? OutputPath { get; set; }
        public DateTimeOffset RenderedAtUtc { get; set; }
        public bool TelemetryLogged { get; set; }
        public string? YouTubeVideoId { get; set; }
        public WatermarkDto? YouTubeWatermark { get; set; }
        public string? SelectedTitleVariant { get; set; }
        public string? PublishedTitleFraming { get; set; }
        public string? PublishedTitleText { get; set; }
        public string? AlternateTitleFraming { get; set; }
        public string? AlternateTitleText { get; set; }
        public string? TitleAttribution { get; set; }
        public string? AppliedBriefId { get; set; }
        public string? AppliedBriefFormat { get; set; }
        public double? AppliedBriefExpectedRetention { get; set; }
        public string? AppliedBriefTitleFraming { get; set; }
        public bool AppliedBriefEvaluated { get; set; }
        public string? AppliedBriefKind { get; set; }
        public string? AppliedBriefHookStyle { get; set; }
        public string? AppliedBriefColorLookId { get; set; }
        public string? AppliedBriefBgmMood { get; set; }
        public string? AppliedBriefTheme { get; set; }
        public DateTimeOffset? AppliedBriefAppliedAtUtc { get; set; }
        public double? AppliedBriefSurpriseDelta { get; set; }
    }

    /// <summary>워터마크 직렬화 DTO.</summary>
    public sealed class WatermarkDto
    {
        public long Views { get; set; }
        public int Stage { get; set; }
        public double HookPct { get; set; }
        public double CompletionPct { get; set; }
        public double CtrPct { get; set; }
        public DateTimeOffset UpdatedUtc { get; set; }
    }

    /// <summary>영속 스냅샷(버전 필드로 향후 스키마 확장).</summary>
    public sealed class Snapshot
    {
        public int Version { get; set; } = 1;
        public List<ClipDto>? Clips { get; set; }
    }

    /// <summary>매칭 키 정규화 — 확장자 제거 · 소문자 · 문자/숫자만(공백·구두점·이모지 제거).</summary>
    public static string NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var s = value.Trim();
        var ext = Path.GetExtension(s);
        if (ext.Length is > 1 and <= 5 && (ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || ext.Equals(".mov", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webm", StringComparison.OrdinalIgnoreCase) || ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase)))
            s = s[..^ext.Length];
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    private static string Clip(string? s) => string.IsNullOrWhiteSpace(s) ? string.Empty : s.Trim() is { Length: > MaxFieldLength } t ? t[..MaxFieldLength] : s.Trim();

    private static PlutoRenderedClipRecord Sanitize(PlutoRenderedClipRecord r)
    {
        var tags = new List<string>(Math.Min(r.Tags?.Count ?? 0, MaxTags));
        if (r.Tags is not null)
            foreach (var t in r.Tags)
            {
                if (tags.Count >= MaxTags) break;
                var c = Clip(t);
                if (c.Length > 0) tags.Add(c);
            }
        var clean = new PlutoRenderedClipRecord(Clip(r.VideoId), Clip(r.Title), Clip(r.RenderMode), Clip(r.HookStyle), Clip(r.ColorLookId), Clip(r.BgmMood),
            tags.ToArray(), r.OutputPath?.Trim() ?? string.Empty, r.RenderedAtUtc, r.TelemetryLogged)
        {
            YouTubeVideoId = YouTubeStudioAnalyticsNormalizer.IsYouTubeVideoId(r.YouTubeVideoId?.Trim()) ? r.YouTubeVideoId!.Trim() : null,
            YouTubeWatermark = r.YouTubeWatermark is { Views: >= 0, Stage: >= 0 } w ? w : null,
        };
        return SanitizeAppliedBrief(SanitizeTitleAttribution(clean, r), r);
    }

    private static string? OptField(string? v) { var c = Clip(v); return c.Length > 0 ? c : null; }

    /// <summary>🎯 Step 27 — 기획 출처 정화: ID 없음 · 기대치 비유한 → 전부 해제(부분 상태 금지) · 채점 표식은 결과값과 함께만 유지.</summary>
    private static PlutoRenderedClipRecord SanitizeAppliedBrief(PlutoRenderedClipRecord clean, PlutoRenderedClipRecord r)
    {
        var id = Clip(r.AppliedBriefId);
        if (id.Length == 0 || r.AppliedBriefExpectedRetention is not { } exp || !double.IsFinite(exp))
            return clean with
            {
                AppliedBriefId = null, AppliedBriefFormat = null, AppliedBriefExpectedRetention = null, AppliedBriefTitleFraming = null, AppliedBriefEvaluated = false,
                AppliedBriefKind = null, AppliedBriefHookStyle = null, AppliedBriefColorLookId = null, AppliedBriefBgmMood = null, AppliedBriefTheme = null,
                AppliedBriefAppliedAtUtc = null, AppliedBriefSurpriseDelta = null,
            };
        var delta = r.AppliedBriefSurpriseDelta is { } d && double.IsFinite(d) ? Math.Clamp(d, -100.0, 100.0) : (double?)null;
        var evaluated = r.AppliedBriefEvaluated && delta is not null;
        return clean with
        {
            AppliedBriefId = id, AppliedBriefFormat = OptField(r.AppliedBriefFormat), AppliedBriefExpectedRetention = Math.Clamp(exp, 0.0, 100.0),
            AppliedBriefTitleFraming = OptField(r.AppliedBriefTitleFraming), AppliedBriefEvaluated = evaluated, AppliedBriefKind = OptField(r.AppliedBriefKind),
            AppliedBriefHookStyle = OptField(r.AppliedBriefHookStyle), AppliedBriefColorLookId = OptField(r.AppliedBriefColorLookId), AppliedBriefBgmMood = OptField(r.AppliedBriefBgmMood),
            AppliedBriefTheme = OptField(r.AppliedBriefTheme), AppliedBriefAppliedAtUtc = r.AppliedBriefAppliedAtUtc, AppliedBriefSurpriseDelta = evaluated ? delta : null,
        };
    }

    /// <summary>🅰️🅱️ 제목 귀속 필드 정화 — 변형 ∉ {A,B} 또는 원문 없음 → 전부 해제(부분 상태 금지).</summary>
    private static PlutoRenderedClipRecord SanitizeTitleAttribution(PlutoRenderedClipRecord clean, PlutoRenderedClipRecord r)
    {
        var variant = r.SelectedTitleVariant?.Trim();
        var text = Clip(r.PublishedTitleText);
        if (variant is not ("A" or "B") || text.Length == 0)
            return clean with { SelectedTitleVariant = null, PublishedTitleFraming = null, PublishedTitleText = null, AlternateTitleFraming = null, AlternateTitleText = null, TitleAttribution = null };
        var alt = Clip(r.AlternateTitleText);
        var framing = Clip(r.PublishedTitleFraming);
        var altFraming = Clip(r.AlternateTitleFraming);
        return clean with
        {
            SelectedTitleVariant = variant,
            PublishedTitleFraming = framing.Length > 0 ? framing : PlutoTitleFramings.Unknown,
            PublishedTitleText = text,
            AlternateTitleText = alt.Length > 0 ? alt : null,
            AlternateTitleFraming = alt.Length > 0 ? (altFraming.Length > 0 ? altFraming : PlutoTitleFramings.Unknown) : null,
            TitleAttribution = PlutoTitleAttributionStates.IsKnown(r.TitleAttribution) ? r.TitleAttribution : PlutoTitleAttributionStates.Copied,
        };
    }

    private static PlutoRenderedClipRecord FromDto(ClipDto d) => new(d.VideoId, d.Title ?? "", d.RenderMode ?? "", d.HookStyle ?? "", d.ColorLookId ?? "",
        d.BgmMood ?? "", d.Tags ?? Array.Empty<string>(), d.OutputPath ?? "", d.RenderedAtUtc, d.TelemetryLogged)
    {
        YouTubeVideoId = d.YouTubeVideoId,
        YouTubeWatermark = d.YouTubeWatermark is { } w ? new PlutoYouTubeIngestWatermark(w.Views, w.Stage, w.HookPct, w.CompletionPct, w.CtrPct, w.UpdatedUtc) : null,
        SelectedTitleVariant = d.SelectedTitleVariant, PublishedTitleFraming = d.PublishedTitleFraming, PublishedTitleText = d.PublishedTitleText,
        AlternateTitleFraming = d.AlternateTitleFraming, AlternateTitleText = d.AlternateTitleText, TitleAttribution = d.TitleAttribution,
        AppliedBriefId = d.AppliedBriefId, AppliedBriefFormat = d.AppliedBriefFormat, AppliedBriefExpectedRetention = d.AppliedBriefExpectedRetention,
        AppliedBriefTitleFraming = d.AppliedBriefTitleFraming, AppliedBriefEvaluated = d.AppliedBriefEvaluated, AppliedBriefKind = d.AppliedBriefKind,
        AppliedBriefHookStyle = d.AppliedBriefHookStyle, AppliedBriefColorLookId = d.AppliedBriefColorLookId, AppliedBriefBgmMood = d.AppliedBriefBgmMood,
        AppliedBriefTheme = d.AppliedBriefTheme, AppliedBriefAppliedAtUtc = d.AppliedBriefAppliedAtUtc, AppliedBriefSurpriseDelta = d.AppliedBriefSurpriseDelta,
    };

    private static ClipDto ToDto(PlutoRenderedClipRecord r) => new()
    {
        VideoId = r.VideoId, Title = r.Title, RenderMode = r.RenderMode, HookStyle = r.HookStyle, ColorLookId = r.ColorLookId, BgmMood = r.BgmMood,
        Tags = r.Tags is string[] arr ? arr : new List<string>(r.Tags).ToArray(), OutputPath = r.OutputPath, RenderedAtUtc = r.RenderedAtUtc, TelemetryLogged = r.TelemetryLogged,
        YouTubeVideoId = r.YouTubeVideoId,
        YouTubeWatermark = r.YouTubeWatermark is { } w ? new WatermarkDto { Views = w.Views, Stage = w.Stage, HookPct = w.HookPct, CompletionPct = w.CompletionPct, CtrPct = w.CtrPct, UpdatedUtc = w.UpdatedUtc } : null,
        SelectedTitleVariant = r.SelectedTitleVariant, PublishedTitleFraming = r.PublishedTitleFraming, PublishedTitleText = r.PublishedTitleText,
        AlternateTitleFraming = r.AlternateTitleFraming, AlternateTitleText = r.AlternateTitleText, TitleAttribution = r.TitleAttribution,
        AppliedBriefId = r.AppliedBriefId, AppliedBriefFormat = r.AppliedBriefFormat, AppliedBriefExpectedRetention = r.AppliedBriefExpectedRetention,
        AppliedBriefTitleFraming = r.AppliedBriefTitleFraming, AppliedBriefEvaluated = r.AppliedBriefEvaluated, AppliedBriefKind = r.AppliedBriefKind,
        AppliedBriefHookStyle = r.AppliedBriefHookStyle, AppliedBriefColorLookId = r.AppliedBriefColorLookId, AppliedBriefBgmMood = r.AppliedBriefBgmMood,
        AppliedBriefTheme = r.AppliedBriefTheme, AppliedBriefAppliedAtUtc = r.AppliedBriefAppliedAtUtc, AppliedBriefSurpriseDelta = r.AppliedBriefSurpriseDelta,
    };

    private async Task EnsureLoadedLockedAsync(CancellationToken ct)
    {
        if (_hasUnappliedDiskUpdate) { await ReloadCoreAsync(ct).ConfigureAwait(false); if (_loaded) return; }   // 🚧 변경 적용 전 병합본 재적재
        if (_loaded) return;
        var (data, status) = await PlutoJsonStore.TryLoadAsync<Snapshot>(StoragePath, "Pluto.RenderCatalog", ct).ConfigureAwait(false);
        _readOnly = status == PlutoLoadStatus.Unreadable;
        _clips = BuildList(data);
        _loaded = true;
    }

    private static List<PlutoRenderedClipRecord> BuildList(Snapshot? data)
    {
        var list = new List<PlutoRenderedClipRecord>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (data?.Clips is { } clips)
            foreach (var c in clips)
            {
                if (c is null || string.IsNullOrWhiteSpace(c.VideoId) || !seen.Add(c.VideoId.Trim())) continue;
                list.Add(Sanitize(FromDto(c)));
                if (list.Count >= Capacity) break;
            }
        return list;
    }

    // ── ♻️ Step 20 — 실행 중 재적재 ──

    public string HubFileName => FileName;

    /// <summary>♻️ 게이트 안에서 대기 해결 승격 → 파싱 → 원자 교체(손상 · 부재 · 잠김 · 읽기 전용 세션 → 라이브 카탈로그 유지 + false).</summary>
    public async Task<bool> ReloadFromDiskAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0) return false;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_readOnly) return false;
            PlutoHotReload.PromotePending(_storage, FileName);
            if (!await ReloadCoreAsync(ct).ConfigureAwait(false)) return false;
        }
        finally { _gate.Release(); }
        RaiseChanged();
        return true;
    }

    /// <summary>🚧 Step 21 재적재 대기 기록 펜스(잠긴 재적재 이후 낡은 메모리 덮어쓰기 차단).</summary>
    public bool HasUnappliedDiskUpdate => _hasUnappliedDiskUpdate;

    /// <summary>♻️ 재적재 본체(호출자가 <see cref="_gate"/> 보유) — 잠김 → 펜스 설정 · 디스크 읽힘(정상/손상/부재) → 펜스 해제.</summary>
    private async Task<bool> ReloadCoreAsync(CancellationToken ct)
    {
        var (data, status) = await PlutoJsonStore.TryLoadAsync<Snapshot>(StoragePath, "Pluto.RenderCatalog.Reload", ct).ConfigureAwait(false);
        _hasUnappliedDiskUpdate = status == PlutoLoadStatus.Unreadable;
        if (status != PlutoLoadStatus.Ok || data is null) return false;
        _clips = BuildList(data);
        _loaded = true;
        return true;
    }

    public async Task RecordRenderedClipAsync(PlutoRenderedClipRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(record.VideoId)) throw new ArgumentException("VideoId required", nameof(record));
        var clean = Sanitize(record);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedLockedAsync(ct).ConfigureAwait(false);
            for (var i = _clips.Count - 1; i >= 0; i--)
                if (string.Equals(_clips[i].VideoId, clean.VideoId, StringComparison.OrdinalIgnoreCase)) _clips.RemoveAt(i);
            _clips.Insert(0, clean);
            if (_clips.Count > Capacity) _clips.RemoveRange(Capacity, _clips.Count - Capacity);
            await PersistLockedAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        RaiseChanged();
    }

    public async Task<IReadOnlyList<PlutoRenderedClipRecord>> GetRecentClipsAsync(int maxCount = 30, CancellationToken ct = default)
    {
        if (maxCount <= 0) return Array.Empty<PlutoRenderedClipRecord>();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedLockedAsync(ct).ConfigureAwait(false);
            var n = Math.Min(maxCount, _clips.Count);
            var result = new PlutoRenderedClipRecord[n];
            _clips.CopyTo(0, result, 0, n);
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task<PlutoRenderedClipRecord?> FindByVideoIdOrTitleAsync(string videoIdOrTitle, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoIdOrTitle)) return null;
        var raw = videoIdOrTitle.Trim();
        var norm = NormalizeKey(raw);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedLockedAsync(ct).ConfigureAwait(false);
            foreach (var c in _clips)
                if (string.Equals(c.VideoId, raw, StringComparison.OrdinalIgnoreCase)) return c;
            if (norm.Length == 0) return null;
            foreach (var c in _clips)
                if (string.Equals(NormalizeKey(c.Title), norm, StringComparison.Ordinal)) return c;
            foreach (var c in _clips)
                if (string.Equals(NormalizeKey(c.VideoId), norm, StringComparison.Ordinal)) return c;
            return null;
        }
        finally { _gate.Release(); }
    }

    public async Task MarkTelemetryLoggedAsync(string videoId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoId)) return;
        var id = videoId.Trim();
        var changed = false;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedLockedAsync(ct).ConfigureAwait(false);
            for (var i = 0; i < _clips.Count; i++)
            {
                var c = _clips[i];
                if (!c.TelemetryLogged && string.Equals(c.VideoId, id, StringComparison.OrdinalIgnoreCase))
                {
                    _clips[i] = c with { TelemetryLogged = true };
                    changed = true;
                }
            }
            if (changed) await PersistLockedAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        if (changed) RaiseChanged();
    }

    public async Task<PlutoRenderedClipRecord?> FindByYouTubeVideoIdAsync(string youTubeVideoId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(youTubeVideoId)) return null;
        var yt = youTubeVideoId.Trim();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedLockedAsync(ct).ConfigureAwait(false);
            foreach (var c in _clips)
                if (string.Equals(c.YouTubeVideoId, yt, StringComparison.Ordinal)) return c;   // YouTube ID 는 대소문자 구분
            return null;
        }
        finally { _gate.Release(); }
    }

    public async Task<PlutoYouTubeBindResult> TryBindYouTubeVideoIdAsync(string clipVideoId, string youTubeVideoId, CancellationToken ct = default)
    {
        var yt = youTubeVideoId?.Trim();
        if (string.IsNullOrWhiteSpace(clipVideoId) || yt is null || !YouTubeStudioAnalyticsNormalizer.IsYouTubeVideoId(yt)) return PlutoYouTubeBindResult.NotFound;
        var id = clipVideoId.Trim();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedLockedAsync(ct).ConfigureAwait(false);
            var idx = -1;
            for (var i = 0; i < _clips.Count; i++)
            {
                var c = _clips[i];
                if (string.Equals(c.VideoId, id, StringComparison.OrdinalIgnoreCase)) { if (idx < 0) idx = i; continue; }
                if (string.Equals(c.YouTubeVideoId, yt, StringComparison.Ordinal)) return PlutoYouTubeBindResult.IdTakenByOtherClip;   // 🛡️ 1:1
            }
            if (idx < 0) return PlutoYouTubeBindResult.NotFound;
            var cur = _clips[idx];
            if (cur.YouTubeVideoId is { } have)
                return string.Equals(have, yt, StringComparison.Ordinal) ? PlutoYouTubeBindResult.AlreadyBound : PlutoYouTubeBindResult.ClipBoundElsewhere;
            _clips[idx] = cur with { YouTubeVideoId = yt };
            await PersistLockedAsync(ct).ConfigureAwait(false);   // 영속 실패 → [부분 구제] 메모리 연결 유지
        }
        finally { _gate.Release(); }
        RaiseChanged();
        return PlutoYouTubeBindResult.Bound;
    }

    /// <summary>워터마크 우열 — 조회수 우선, 같으면 단계(멀티 PC 병합과 같은 규칙).</summary>
    public static bool WatermarkSupersedes(PlutoYouTubeIngestWatermark candidate, PlutoYouTubeIngestWatermark? existing)
        => existing is null || candidate.Views > existing.Views || (candidate.Views == existing.Views && candidate.Stage > existing.Stage);

    public async Task<bool> UpdateYouTubeWatermarkAsync(string clipVideoId, PlutoYouTubeIngestWatermark watermark, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(watermark);
        if (string.IsNullOrWhiteSpace(clipVideoId) || watermark.Views < 0 || watermark.Stage < 0) return false;
        var id = clipVideoId.Trim();
        var changed = false;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedLockedAsync(ct).ConfigureAwait(false);
            for (var i = 0; i < _clips.Count; i++)
            {
                var c = _clips[i];
                if (!string.Equals(c.VideoId, id, StringComparison.OrdinalIgnoreCase)) continue;
                if (!WatermarkSupersedes(watermark, c.YouTubeWatermark)) break;   // 🛡️ 되감기 거부
                _clips[i] = c with { YouTubeWatermark = watermark };
                changed = true;
                break;
            }
            if (changed) await PersistLockedAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        if (changed) RaiseChanged();
        return changed;
    }

    public async Task<bool> RecordPublishedTitleAsync(string clipVideoId, PlutoPublishedTitleSelection selection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (string.IsNullOrWhiteSpace(clipVideoId) || selection.Variant is not ("A" or "B") || string.IsNullOrWhiteSpace(selection.Text)) return false;
        return await MutateClipAsync(clipVideoId.Trim(), c =>
        {
            // 🧊 실제 게시 제목으로 이미 판정된 클립은 동결(게시 후 재복사가 실측 귀속을 덮지 않음)
            if (c.TitleAttribution is PlutoTitleAttributionStates.Confirmed or PlutoTitleAttributionStates.Diverged) return c;
            return SanitizeTitleAttribution(c, c with
            {
                SelectedTitleVariant = selection.Variant, PublishedTitleFraming = selection.Framing, PublishedTitleText = selection.Text,
                AlternateTitleFraming = selection.AlternateFraming, AlternateTitleText = selection.AlternateText, TitleAttribution = PlutoTitleAttributionStates.Copied,
            });
        }, ct).ConfigureAwait(false);
    }

    public async Task<bool> ResolvePublishedTitleAsync(string clipVideoId, string? realizedTitle, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clipVideoId) || string.IsNullOrWhiteSpace(realizedTitle)) return false;
        return await MutateClipAsync(clipVideoId.Trim(), c => PlutoYouTubePublishPackService.ResolveRealizedTitle(c, realizedTitle), ct).ConfigureAwait(false);
    }

    public async Task<bool> TryRecordBriefOutcomeAsync(string clipVideoId, double surpriseDelta, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clipVideoId) || !double.IsFinite(surpriseDelta)) return false;
        var delta = Math.Clamp(surpriseDelta, -100.0, 100.0);
        return await MutateClipAsync(clipVideoId.Trim(),
            c => !c.HasAppliedBrief || c.AppliedBriefEvaluated ? c : c with { AppliedBriefEvaluated = true, AppliedBriefSurpriseDelta = delta }, ct).ConfigureAwait(false);
    }

    /// <summary>클립 1건을 게이트 안에서 변환(동일 → 무기록) · 변경 → 영속 + 통지.</summary>
    private async Task<bool> MutateClipAsync(string id, Func<PlutoRenderedClipRecord, PlutoRenderedClipRecord> mutate, CancellationToken ct)
    {
        var changed = false;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedLockedAsync(ct).ConfigureAwait(false);
            for (var i = 0; i < _clips.Count; i++)
            {
                var c = _clips[i];
                if (!string.Equals(c.VideoId, id, StringComparison.OrdinalIgnoreCase)) continue;
                var next = mutate(c);
                if (!next.Equals(c)) { _clips[i] = next; changed = true; }
                break;
            }
            if (changed) await PersistLockedAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        if (changed) RaiseChanged();
        return changed;
    }

    private async Task<bool> PersistLockedAsync(CancellationToken ct)
    {
        if (_readOnly) return false;   // 🛡️ 적재 불가 파일 보호
        if (_hasUnappliedDiskUpdate)   // 🚧 펜스 — 디스크 병합본 재적재 먼저 · 여전히 잠김 → 덮어쓰기 포기
        {
            if (await ReloadCoreAsync(ct).ConfigureAwait(false)) return true;   // 메모리 = 디스크 병합본(재기록 불필요)
            if (_hasUnappliedDiskUpdate) { System.Diagnostics.Debug.WriteLine("[Pluto.RenderCatalog] save aborted — write fence"); return false; }
        }
        try
        {
            var dtos = new List<ClipDto>(_clips.Count);
            foreach (var c in _clips) dtos.Add(ToDto(c));
            var json = JsonSerializer.Serialize(new Snapshot { Clips = dtos }, JsonOpts);
            return _storage is not null
                ? await _storage.WriteAllTextAtomicAsync(FileName, json, ct).ConfigureAwait(false)
                : await PlutoJsonStore.WriteAtomicAsync(_path, json, "Pluto.RenderCatalog", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.RenderCatalog] persist: " + ex.Message); return false; }   // [부분 구제]
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.RenderCatalog] Changed handler: " + ex.Message); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Changed = null;
        _gate.Dispose();
    }
}
