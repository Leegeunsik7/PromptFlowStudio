using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🎬 Step 40 — 로캘 1개 렌더 작업(마스터 시각 타임라인 재사용 · 로캘 자막/TTS 투영만 교체).</summary>
/// <param name="StagingPath">렌더러가 써야 할 스테이징 MP4(같은 폴더 · 검증 통과 후에만 <paramref name="FinalPath"/> 로 원자 교체).</param>
/// <param name="FinalPath">최종 <c>{base}_{locale}.mp4</c>(렌더러는 직접 쓰지 않는다).</param>
/// <param name="TimelineSeconds">마스터 타임라인 길이(오디오/영상 길이 동등성 기준 · 0 = 미상).</param>
public sealed record PlutoLocaleRenderJob(string BaseClipId, string Locale, string MasterVideoPath, string StagingPath, string FinalPath,
    IReadOnlyList<SubtitleCue> Cues, string VoiceId, double SpeakingRate, double TimelineSeconds)
{
    public PlutoLocalizedSubtitleProjection? Projection { get; init; }
    public bool IsShorts { get; init; } = true;
}

/// <summary>🎬 렌더러 결과 — 길이(초)는 측정 가능할 때만(미측정 null → 동등성 검사 생략).</summary>
public sealed record PlutoLocaleRenderOutcome(bool Ok, string? Message = null)
{
    public double? VideoDurationSeconds { get; init; }
    public double? AudioDurationSeconds { get; init; }
}

/// <summary>🎬 로캘 렌더 대리자(테스트 → 결정론 페이크 · 앱 → FFmpeg 몽타주 내보내기). <see cref="PlutoLocaleRenderJob.StagingPath"/> 에만 쓴다.</summary>
public delegate Task<PlutoLocaleRenderOutcome> PlutoLocaleRenderDelegate(PlutoLocaleRenderJob job, CancellationToken ct);

/// <summary>🎙️ TTS 1.30x 초과 로캘 처리 정책.</summary>
public enum PlutoTtsOverflowRenderPolicy
{
    /// <summary>기본 — 렌더 보류(기존 MP4 · 사이드카 그대로) · 형제 로캘은 계속.</summary>
    Skip,
    /// <summary>경고와 함께 렌더(길이 동등성 가드는 그대로 적용 → 낭독이 타임라인을 넘으면 커밋 거부).</summary>
    RenderFlagged,
}

/// <summary>🎬 Step 40 — 전 언어 일괄 렌더 요청.</summary>
/// <param name="Batch">사전 분기/패키지 요청(<see cref="PlutoMultiLocaleBatchRequest.VideoPath"/> = 마스터 영상 · 필수).</param>
public sealed record PlutoMultiLocaleRenderRequest(PlutoMultiLocaleBatchRequest Batch)
{
    /// <summary>출력 폴더(null → 마스터 영상 폴더).</summary>
    public string? OutputDirectory { get; init; }
    public PlutoTtsOverflowRenderPolicy TtsOverflowPolicy { get; init; } = PlutoTtsOverflowRenderPolicy.Skip;
    /// <summary>마스터 타임라인 길이(null → 자막 마지막 종료 시각).</summary>
    public double? MasterDurationSeconds { get; init; }
    /// <summary>오디오/영상 길이 허용 오차(초).</summary>
    public double DurationToleranceSeconds { get; init; } = 0.25;
}

/// <summary>🎬 로캘별 렌더 상태.</summary>
public enum PlutoLocaleRenderStatus
{
    /// <summary><c>{base}_{locale}.mp4</c> + 사이드카 커밋 완료.</summary>
    Rendered,
    /// <summary>🔒 이미 YouTube 에 연결된 로캘 — MP4 · 사이드카 무변경.</summary>
    AlreadyPublished,
    /// <summary>🎙️ TTS 1.30x 초과 — 정책상 렌더 보류(기존 파일 무변경).</summary>
    TtsOverflowBlocked,
    /// <summary>사전 분기/패키지 · 경로 검증 실패.</summary>
    PreflightFailed,
    /// <summary>렌더러 실패 · 예외 · 커밋 실패(이전 MP4 복원).</summary>
    RenderFailed,
    /// <summary>렌더 산출물 검증 실패(빈/손상 MP4 · 오디오/영상 길이 불일치) — 커밋 거부.</summary>
    OutputRejected,
    /// <summary>💽 디스크 여유 공간 부족 — 렌더 시작 안 함.</summary>
    InsufficientDisk,
    /// <summary>취소 — 이 로캘은 커밋되지 않음(이미 커밋된 형제 로캘은 보존).</summary>
    Cancelled,
}

/// <summary>🎬 로캘 1개의 렌더 결과.</summary>
public sealed record PlutoLocaleRenderItem(string Locale, PlutoLocaleRenderStatus Status)
{
    public string? VariantClipId { get; init; }
    public string? YouTubeVideoId { get; init; }
    public string? VideoPath { get; init; }
    public string? SidecarPath { get; init; }
    /// <summary>카탈로그 변형 레코드의 출력 경로가 이 MP4 로 결선됨(마스터 로캘은 마스터 경로 유지 → false).</summary>
    public bool CatalogBound { get; init; }
    public string? VoiceId { get; init; }
    public double SpeakingRate { get; init; } = 1.0;
    public int TtsOverflowCueCount { get; init; }
    public int TtsExcessChars { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public bool HasTtsOverflow => TtsOverflowCueCount > 0;
    public string ShortLabel => Locale.Length >= 2 ? Locale[..2].ToUpperInvariant() : Locale;
}

/// <summary>🎬 전 언어 일괄 렌더 결과.</summary>
public sealed record PlutoMultiLocaleRenderResult(string BaseClipId, IReadOnlyList<PlutoLocaleRenderItem> Items, bool Busy, bool Cancelled, PlutoMultiLocaleBatchResult? Preflight)
{
    public static PlutoMultiLocaleRenderResult BusyFor(string baseClipId) => new(baseClipId, Array.Empty<PlutoLocaleRenderItem>(), true, false, null);

    public int Count(PlutoLocaleRenderStatus status) { var n = 0; foreach (var i in Items) if (i.Status == status) n++; return n; }

    public PlutoLocaleRenderItem? Find(string locale)
    {
        foreach (var i in Items) if (string.Equals(i.Locale, locale, StringComparison.OrdinalIgnoreCase)) return i;
        return null;
    }

    /// <summary>토스트 1줄 — <c>🎬 전 언어 일괄 렌더 — KO ✅ · EN 🔒게시됨 · JA 🎙️보류 · ES ❌ · 🎞️ 2개</c>.</summary>
    public string SummaryKorean
    {
        get
        {
            if (Busy) return "⏳ 같은 렌더의 전 언어 일괄 작업이 이미 진행 중입니다";
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(128).Append("🎬 전 언어 일괄 렌더");
            for (var k = 0; k < Items.Count; k++)
            {
                var i = Items[k];
                sb.Append(k == 0 ? " — " : " · ").Append(i.ShortLabel).Append(' ');
                sb.Append(i.Status switch
                {
                    PlutoLocaleRenderStatus.Rendered => i.HasTtsOverflow ? "✅🎙️" : "✅",
                    PlutoLocaleRenderStatus.AlreadyPublished => "🔒게시됨",
                    PlutoLocaleRenderStatus.TtsOverflowBlocked => "🎙️보류+" + i.TtsExcessChars.ToString(inv) + "자",
                    PlutoLocaleRenderStatus.OutputRejected => "🚫검증",
                    PlutoLocaleRenderStatus.InsufficientDisk => "💽부족",
                    PlutoLocaleRenderStatus.Cancelled => "⏹",
                    _ => "❌",
                });
            }
            var n = Count(PlutoLocaleRenderStatus.Rendered);
            sb.Append(" · 🎞️ ").Append(n.ToString(inv)).Append("개 · 💾 ").Append(n.ToString(inv)).Append("개");
            if (Cancelled) sb.Append(" · ⏹ 취소(완료된 언어는 보존)");
            return sb.ToString();
        }
    }
}

/// <summary>🎬 Step 40 — 원클릭 전 언어 일괄 렌더/패키지.</summary>
public interface IPlutoMultiLocaleBatchRenderService
{
    /// <summary>같은 마스터의 일괄(패키지 · 렌더) 작업 진행 중.</summary>
    bool IsRunning(string baseClipId);

    /// <summary>
    /// 사전 분기/패키지(<see cref="PlutoMultiLocaleBatchBundle"/>) → 로캘마다 자막/TTS 투영 렌더 → 검증 → <c>{base}_{locale}.mp4</c> + <c>{base}_{locale}_publish_pack.txt</c> 짝 커밋
    /// → 카탈로그 <c>{BaseClipId}::{Locale}</c> 출력 경로 결선. 로캘별 실패는 형제를 멈추지 않는다. <paramref name="render"/> null → 생성자 기본 렌더러.
    /// </summary>
    Task<PlutoMultiLocaleRenderResult> RenderAllLocalesAsync(PlutoMultiLocaleRenderRequest request, PlutoMultiLocaleBatchServices services, PlutoLocaleRenderDelegate? render = null, CancellationToken ct = default);
}

/// <summary>
/// 🎬 Step 40 — 전 언어 일괄 렌더 오케스트레이터(순수 BCL · 결정론 순서 · 핫패스 아님).
///   ▸ 🔒 게시(연결)된 로캘은 렌더 · 사이드카 무변경 — 커밋 직전 카탈로그를 다시 읽어 렌더 도중 연결된 로캘도 덮어쓰지 않음(TOCTOU).
///   ▸ 🎙️ TTS 1.30x 초과 로캘은 정책(<see cref="PlutoTtsOverflowRenderPolicy"/>)대로 보류/경고 · 산출물 오디오가 마스터 타임라인을 넘으면 커밋 거부(A/V 싱크 보호).
///   ▸ ⚛️ 렌더러는 스테이징 파일에만 쓴다 → MP4 서명(<c>ftyp</c>) · 길이 검증 → 기존 MP4 백업 → 교체 → 사이드카 원자 기록 → 실패 시 이전 MP4 복원(영상/사이드카 짝 불일치 0).
///   ▸ 💽 로캘마다 디스크 여유 공간 사전 점검(마스터 크기 기반) — 부족하면 남은 로캘 시작 안 함(반쪽 MP4 · ENOSPC 방지).
///   ▸ 🧷 같은 마스터의 일괄 패키지(Step 39)와 단일 비행 슬롯 공유 — 같은 사이드카 파일 교차 덮어쓰기 차단.
/// </summary>
public sealed class PlutoMultiLocaleBatchRenderService : IPlutoMultiLocaleBatchRenderService
{
    /// <summary>스테이징 파일 표식(취소 · 실패 시 삭제 · 잔류 검사).</summary>
    public const string StagingMarker = ".rendering";
    /// <summary>여유 공간 최소 여분(바이트).</summary>
    public const long MinFreeHeadroomBytes = 32L * 1024 * 1024;

    /// <summary>⚠ 마스터 ↔ 현재 작업 공간 불일치 경고 접두(토스트 · 테스트 공용).</summary>
    public const string WorkspaceMismatchPrefix = "⚠ 마스터 불일치";

    /// <summary>
    /// 🔍 작업 공간 지문(사진 경로 순서 + 자막 시각 · 원문) — FNV-1a 64 · 순서 민감 · 문자열 보간 없음.
    /// 마스터를 렌더한 시점의 지문과 일괄 렌더 시점의 지문을 비교해 자막/미디어가 바뀐 채 게시하는 실수를 드러낸다.
    /// </summary>
    public static ulong ComputeWorkspaceFingerprint(IReadOnlyList<string>? photos, IReadOnlyList<SubtitleCue>? cues)
    {
        const ulong prime = 1099511628211UL;
        var h = 14695981039346656037UL;
        static ulong Mix(ulong h, string? text)
        {
            if (text is not null) foreach (var ch in text) { h ^= ch; h *= prime; }
            h ^= 0x1F; h *= prime;   // 필드 경계(이어 붙임 충돌 방지)
            return h;
        }
        h = Mix(h, photos is null ? "0" : photos.Count.ToString(CultureInfo.InvariantCulture));
        if (photos is not null) for (var i = 0; i < photos.Count; i++) h = Mix(h, photos[i]);
        h = Mix(h, cues is null ? "0" : cues.Count.ToString(CultureInfo.InvariantCulture));
        if (cues is not null)
            for (var i = 0; i < cues.Count; i++)
            {
                var c = cues[i];
                h = Mix(h, c.StartSec.ToString(CultureInfo.InvariantCulture));
                h = Mix(h, c.EndSec.ToString(CultureInfo.InvariantCulture));
                h = Mix(h, c.Text);
                h = Mix(h, c.SecondaryText);
            }
        return h;
    }

    /// <summary>
    /// ⚠ 카탈로그에서 고른 마스터가 현재 작업 공간과 다르면 경고 1줄(일치 → null).
    /// <paramref name="renderedVideoId"/>/<paramref name="renderedFingerprint"/> = 이 작업 공간에서 마지막으로 렌더한 마스터와 그때의 지문(없음 → null).
    /// </summary>
    public static string? DescribeWorkspaceMismatch(string masterVideoId, string? renderedVideoId, ulong? renderedFingerprint, ulong currentFingerprint)
    {
        if (string.IsNullOrWhiteSpace(renderedVideoId) || renderedFingerprint is null
            || !string.Equals(masterVideoId, renderedVideoId, StringComparison.OrdinalIgnoreCase))
            return WorkspaceMismatchPrefix + " — 선택한 카탈로그 마스터(" + masterVideoId + ")는 현재 작업 공간에서 렌더한 영상이 아닙니다 · 자막/미디어가 다를 수 있으니 게시 전 확인하세요";
        if (renderedFingerprint.Value != currentFingerprint)
            return WorkspaceMismatchPrefix + " — 마스터(" + masterVideoId + ") 렌더 이후 현재 작업 공간의 자막/미디어가 바뀌었습니다 · 게시 전 확인하세요";
        return null;
    }

    /// <summary>
    /// 🚦 [전 언어 일괄 렌더] 시작 전 사전 조건 점검 — 막히면 사용자에게 보일 한국어 안내 1줄, 통과 → null(무응답 조기 종료 방지).
    /// </summary>
    public static string? DescribeRenderBlocker(bool batchBusy, bool otherJobBusy, int photoCount, bool channelsReady, bool servicesReady)
    {
        if (batchBusy) return "⏳ 전 언어 일괄 작업이 이미 진행 중입니다 · 끝난 뒤 다시 시도하세요";
        if (otherJobBusy) return "⏳ 다른 렌더/패키지 작업이 진행 중입니다 · 끝난 뒤 다시 시도하세요";
        if (photoCount < 2) return "⚠ 전 언어 일괄 렌더에는 몽타주 사진이 2장 이상 필요합니다(현재 " + photoCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + "장)";
        if (!channelsReady) return "⚠ 다국어 채널 서비스가 준비되지 않아 전 언어 일괄 렌더를 시작할 수 없습니다";
        if (!servicesReady) return "⚠ 게시 패키지/일괄 렌더 서비스가 등록되지 않아 전 언어 일괄 렌더를 시작할 수 없습니다";
        return null;
    }

    private readonly IPlutoMultiLanguageChannelService _channels;
    private readonly PlutoLocaleRenderDelegate? _defaultRender;
    private readonly IPlutoAutonomousActivityFeedService? _feed;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<string, long?> _freeSpace;

    public PlutoMultiLocaleBatchRenderService(IPlutoMultiLanguageChannelService channels, PlutoLocaleRenderDelegate? defaultRender = null,
        IPlutoAutonomousActivityFeedService? activityFeed = null, Func<DateTimeOffset>? clock = null, Func<string, long?>? freeSpaceProbe = null)
    {
        _channels = channels ?? throw new ArgumentNullException(nameof(channels));
        _defaultRender = defaultRender;
        _feed = activityFeed;
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
        _freeSpace = freeSpaceProbe ?? DefaultFreeSpace;
    }

    public bool IsRunning(string baseClipId) => PlutoMultiLocaleBatchBundle.IsRunning(baseClipId);

    public async Task<PlutoMultiLocaleRenderResult> RenderAllLocalesAsync(PlutoMultiLocaleRenderRequest request, PlutoMultiLocaleBatchServices services, PlutoLocaleRenderDelegate? render = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Batch);
        ArgumentNullException.ThrowIfNull(services);
        var baseId = PlutoMultiLocaleBatchBundle.MasterKey(request.Batch.BaseClipId);
        if (baseId.Length == 0) throw new ArgumentException("마스터 클립 ID 가 비어 있습니다", nameof(request));
        if (!PlutoMultiLocaleBatchBundle.TryEnter(baseId)) return PlutoMultiLocaleRenderResult.BusyFor(baseId);
        PlutoMultiLocaleRenderResult result;
        try
        {
            result = await RenderCoreAsync(baseId, request, services, render ?? _defaultRender, ct).ConfigureAwait(false);
        }
        finally { PlutoMultiLocaleBatchBundle.Exit(baseId); }
        try { _feed?.Record(PlutoActivityKind.LocaleRender, result.SummaryKorean); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.LocaleRender] feed: " + ex.Message); }   // 피드 실패 격리
        return result;
    }

    private async Task<PlutoMultiLocaleRenderResult> RenderCoreAsync(string baseId, PlutoMultiLocaleRenderRequest request, PlutoMultiLocaleBatchServices services,
        PlutoLocaleRenderDelegate? render, CancellationToken ct)
    {
        var batch = request.Batch;
        var master = batch.VideoPath?.Trim() ?? string.Empty;
        if (PlutoYouTubePublishPackService.TryGetSidecarPath(master, null, out var masterWhy) is null)
            return Fail(baseId, ResolveTargets(batch), "마스터 영상 경로 오류: " + masterWhy);
        var outDir = string.IsNullOrWhiteSpace(request.OutputDirectory) ? Path.GetDirectoryName(master)! : request.OutputDirectory.Trim();
        if (!Path.IsPathFullyQualified(outDir) || outDir.Split('\\', '/').AsSpan().IndexOf("..") >= 0)
            return Fail(baseId, ResolveTargets(batch), "출력 폴더가 절대 경로가 아닙니다");
        try { Directory.CreateDirectory(outDir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return Fail(baseId, ResolveTargets(batch), "출력 폴더를 만들 수 없습니다: " + PlutoAutonomousActivityFeedService.RedactSecrets(ex.Message));
        }

        // ① 사전 분기/패키지(번역 · 적합 · 변형 분기 · 로캘 패키지) — 마스터 옆 사이드카는 만들지 않는다(사이드카는 로캘 MP4 와 짝으로만)
        var pre = await PlutoMultiLocaleBatchBundle.RunHeldAsync(_channels, baseId, batch with { VideoPath = null }, services, ct).ConfigureAwait(false);

        // 📸 원문 스냅샷 · 타임라인 길이
        SubtitleCue[] cues;
        if (batch.KoreanCues is { Count: > 0 } src) { cues = new SubtitleCue[src.Count]; for (var i = 0; i < cues.Length; i++) cues[i] = src[i]; }
        else cues = Array.Empty<SubtitleCue>();
        var timeline = request.MasterDurationSeconds is { } md && double.IsFinite(md) && md > 0 ? md : CueTimelineEnd(cues);
        var tol = double.IsFinite(request.DurationToleranceSeconds) && request.DurationToleranceSeconds >= 0 ? request.DurationToleranceSeconds : 0.25;
        var ctxKey = string.IsNullOrWhiteSpace(batch.ContextKey) ? "montage" : batch.ContextKey.Trim();
        var stem = MasterStem(master);
        long masterBytes = 0;
        try { if (File.Exists(master)) masterBytes = new FileInfo(master).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { masterBytes = 0; }

        var items = new List<PlutoLocaleRenderItem>(pre.Items.Count);
        var cancelled = false;
        var diskShort = false;
        for (var k = 0; k < pre.Items.Count; k++)
        {
            var p = pre.Items[k];
            var item = new PlutoLocaleRenderItem(p.Locale, PlutoLocaleRenderStatus.PreflightFailed)
            {
                VariantClipId = p.VariantClipId, YouTubeVideoId = p.YouTubeVideoId, SpeakingRate = p.SpeakingRate,
                TtsOverflowCueCount = p.TtsOverflowCueCount, TtsExcessChars = p.TtsExcessChars, Warnings = p.Warnings,
            };
            if (cancelled || ct.IsCancellationRequested) { cancelled = true; items.Add(item with { Status = PlutoLocaleRenderStatus.Cancelled }); continue; }
            if (p.Status == PlutoLocaleBatchStatus.AlreadyPublished) { items.Add(item with { Status = PlutoLocaleRenderStatus.AlreadyPublished }); continue; }
            if (p.Status == PlutoLocaleBatchStatus.Failed || p.Pack is null) { items.Add(item with { Error = p.Error ?? "업로드 패키지를 만들지 못했습니다" }); continue; }
            if (diskShort) { items.Add(item with { Status = PlutoLocaleRenderStatus.InsufficientDisk, Error = "💽 디스크 여유 공간 부족 — 시작 안 함" }); continue; }
            try
            {
                var r = await RenderOneAsync(baseId, master, outDir, stem, masterBytes, timeline, tol, ctxKey, request, services, render, p, item, ct).ConfigureAwait(false);
                if (r.Status == PlutoLocaleRenderStatus.InsufficientDisk) diskShort = true;
                items.Add(r);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                cancelled = true;
                items.Add(item with { Status = PlutoLocaleRenderStatus.Cancelled });
            }
            catch (Exception ex)
            {
                items.Add(item with { Status = PlutoLocaleRenderStatus.RenderFailed, Error = PlutoAutonomousActivityFeedService.RedactSecrets(ex.Message) });
            }
        }
        return new PlutoMultiLocaleRenderResult(baseId, items, false, cancelled, pre);
    }

    private async Task<PlutoLocaleRenderItem> RenderOneAsync(string baseId, string master, string outDir, string stem, long masterBytes, double timeline, double tol, string ctxKey,
        PlutoMultiLocaleRenderRequest request, PlutoMultiLocaleBatchServices services, PlutoLocaleRenderDelegate? render, PlutoLocaleBatchItem p, PlutoLocaleRenderItem item, CancellationToken ct)
    {
        var batch = request.Batch;
        var loc = p.Locale;
        var warnings = new List<string>(p.Warnings.Count + 2);
        warnings.AddRange(p.Warnings);
        item = item with { Warnings = warnings };

        // ② 로캘 자막/TTS 투영(마스터 시각 타임라인 그대로 · 번역 트랙은 사전 단계가 방금 갱신)
        var cues = batch.KoreanCues ?? Array.Empty<SubtitleCue>();
        PlutoLocalizedSubtitleProjection proj;
        if (cues.Count > 0)
        {
            PlutoLocaleSubtitleTrack? track = null;
            try { track = services.Localization?.GetTrack(ctxKey, loc); }
            catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add("🌐 번역 트랙 조회 실패(원문 투영): " + PlutoAutonomousActivityFeedService.RedactSecrets(ex.Message)); }
            proj = PlutoLocaleSubtitleFitter.Project(cues, _channels, loc, batch.SubtitleEpisode, batch.SubtitleTotal, track);
        }
        else
        {
            var prof = _channels.GetProfileOrDefault(loc);
            proj = new PlutoLocalizedSubtitleProjection(loc, Array.Empty<SubtitleCue>(), Array.Empty<PlutoLocalizedCueFit>(), prof.DefaultTtsVoiceId, prof.BaseSpeakingRate);
        }
        item = item with
        {
            VoiceId = proj.VoiceId, SpeakingRate = proj.IsIdentity ? item.SpeakingRate : proj.SpeakingRate,
            TtsOverflowCueCount = proj.IsIdentity ? item.TtsOverflowCueCount : proj.TtsOverflowCueCount,
            TtsExcessChars = proj.IsIdentity ? item.TtsExcessChars : proj.TotalExcessChars,
        };

        // 🎙️ TTS 초과 게이트(정책)
        if (item.HasTtsOverflow)
        {
            if (request.TtsOverflowPolicy == PlutoTtsOverflowRenderPolicy.Skip)
                return item with { Status = PlutoLocaleRenderStatus.TtsOverflowBlocked, Error = "🎙️ TTS 1.30x 초과 " + item.TtsExcessChars.ToString(CultureInfo.InvariantCulture) + "자 — 대본 축약 후 다시 렌더(기존 파일 유지)" };
            warnings.Add("🎙️ TTS 1.30x 초과 상태로 렌더 — 길이 동등성 가드 적용");
        }

        // ③ 경로 — {stem}_{locale}.mp4 · 사이드카 {stem}_{locale}_publish_pack.txt(로캘 MP4 의 레거시 이름 = Step 39 로캘 사이드카 이름과 동일)
        var final = Path.Combine(outDir, stem + "_" + loc + ".mp4");
        if (PlutoYouTubePublishPackService.TryGetSidecarPath(final, null, out var why) is not { } sidecar) return item with { Error = "출력 경로 오류: " + why };
        if (string.Equals(Path.GetFullPath(final), Path.GetFullPath(master), StringComparison.OrdinalIgnoreCase))
            return item with { Error = "출력 경로가 마스터 영상과 같습니다 — 마스터 보호를 위해 건너뜀" };
        if (BlockReason(final) is { } fb) return item with { Error = fb };
        if (BlockReason(sidecar) is { } sb) return item with { Error = sb };

        // 💽 여유 공간 사전 점검(스테이징 1개 = 마스터 크기 근사 + 여분)
        var need = checked(masterBytes + masterBytes / 4 + MinFreeHeadroomBytes);
        long? free = null;
        try { free = _freeSpace(outDir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { free = null; }
        if (free is { } f && f < need)
            return item with { Status = PlutoLocaleRenderStatus.InsufficientDisk, Error = "💽 디스크 여유 공간 부족(필요 ≈ " + (need / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MB)" };

        if (render is null) return item with { Status = PlutoLocaleRenderStatus.RenderFailed, Error = "렌더러 미연결" };

        // ④ 스테이징 렌더
        var staging = Path.Combine(outDir, stem + "_" + loc + "." + Guid.NewGuid().ToString("N")[..12] + StagingMarker + ".mp4");
        try
        {
            ct.ThrowIfCancellationRequested();
            var job = new PlutoLocaleRenderJob(baseId, loc, master, staging, final, proj.Cues, proj.VoiceId, item.SpeakingRate, timeline) { Projection = proj, IsShorts = batch.IsShorts };
            PlutoLocaleRenderOutcome outcome;
            try { outcome = await render(job, ct).ConfigureAwait(false) ?? new PlutoLocaleRenderOutcome(false, "렌더러 결과 없음"); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return item with { Status = PlutoLocaleRenderStatus.RenderFailed, Error = "렌더 실패: " + PlutoAutonomousActivityFeedService.RedactSecrets(ex.Message) }; }
            ct.ThrowIfCancellationRequested();
            if (!outcome.Ok) return item with { Status = PlutoLocaleRenderStatus.RenderFailed, Error = "렌더 실패: " + PlutoAutonomousActivityFeedService.RedactSecrets(outcome.Message ?? "알 수 없음") };

            // ⑤ 산출물 검증 — MP4 서명 · 오디오/영상 길이 동등성(낭독이 타임라인을 넘으면 마지막 자막 뒤로 음성이 밀림)
            if (ValidateStaged(staging) is { } bad) return item with { Status = PlutoLocaleRenderStatus.OutputRejected, Error = bad };
            if (DurationMismatch(outcome, timeline, request.MasterDurationSeconds is > 0, tol) is { } drift) return item with { Status = PlutoLocaleRenderStatus.OutputRejected, Error = drift };

            // 🔒 커밋 직전 게시 재확인(렌더 도중 자동 폴러 · 다른 PC 가 연결했을 수 있음)
            if (services.Catalog is { } cat0 && p.VariantClipId is { Length: > 0 } vid0)
            {
                var cur = await cat0.FindByVideoIdOrTitleAsync(vid0, ct).ConfigureAwait(false);
                if (cur is not null && string.Equals(cur.VideoId, vid0, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(cur.YouTubeVideoId))
                    return item with { Status = PlutoLocaleRenderStatus.AlreadyPublished, YouTubeVideoId = cur.YouTubeVideoId };
            }

            // ⑥ 짝 커밋(영상 → 사이드카) — 이후는 취소 불가 구간
            var text = p.Pack!.ToSidecarText(Path.GetFileName(final), p.VariantClipId, _clock());
            if (CommitPair(staging, final, sidecar, text) is { } commitErr) return item with { Status = PlutoLocaleRenderStatus.RenderFailed, Error = commitErr };
            staging = null;
        }
        finally
        {
            if (staging is not null) DeleteQuietly(staging);
        }

        // ⑦ 카탈로그 출력 경로 결선({BaseClipId}::{Locale} · 마스터 로캘은 마스터 레코드 경로 유지) — 커밋 뒤라 취소 토큰 미전파
        var bound = false;
        if (services.Catalog is { } cat && p.VariantClipId is { Length: > 0 } vid && !string.Equals(vid, baseId, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var rec = await cat.BranchLocaleVariantAsync(baseId, loc, null, final, CancellationToken.None).ConfigureAwait(false);
                bound = rec is not null && string.Equals(rec.OutputPath, final, StringComparison.OrdinalIgnoreCase);
                if (!bound) warnings.Add("🗂️ 카탈로그 출력 경로 결선 안 됨(게시 동결 · 마스터 없음)");
            }
            catch (Exception ex) { warnings.Add("🗂️ 카탈로그 결선 실패(파일은 저장됨): " + PlutoAutonomousActivityFeedService.RedactSecrets(ex.Message)); }
        }
        return item with { Status = PlutoLocaleRenderStatus.Rendered, VideoPath = final, SidecarPath = sidecar, CatalogBound = bound };
    }

    /// <summary>⚛️ 영상 · 사이드카 짝 커밋 — 기존 MP4 백업 → 교체 → 사이드카 원자 기록 → 실패 시 이전 MP4 복원. 성공 → null.</summary>
    internal static string? CommitPair(string staging, string final, string sidecar, string sidecarText)
    {
        string? bak = null, tmp = null;
        var swapped = false;
        try
        {
            if (File.Exists(final)) { bak = final + "." + Guid.NewGuid().ToString("N")[..12] + ".bak"; File.Move(final, bak); }
            File.Move(staging, final);
            swapped = true;
            tmp = sidecar + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, sidecarText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            File.Move(tmp, sidecar, overwrite: true);
            tmp = null;
            if (bak is not null) { DeleteQuietly(bak); bak = null; }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            var restored = true;
            try
            {
                if (swapped && File.Exists(final)) File.Delete(final);   // 새 영상 회수(사이드카와 짝이 안 맞는 영상 노출 금지)
                if (bak is not null) { File.Move(bak, final); bak = null; }
            }
            catch (Exception rex) when (rex is IOException or UnauthorizedAccessException) { restored = false; System.Diagnostics.Debug.WriteLine("[Pluto.LocaleRender] restore: " + rex.Message); }
            return (restored ? "⚠ 커밋 실패 → 이전 영상 복원: " : "⚠ 커밋 실패(백업 .bak 보존): ") + PlutoAutonomousActivityFeedService.RedactSecrets(ex.Message);
        }
        finally
        {
            if (tmp is not null) DeleteQuietly(tmp);
        }
    }

    /// <summary>🧪 스테이징 MP4 검증 — 존재 · 링크 아님 · ISO BMFF <c>ftyp</c> 서명. 통과 → null.</summary>
    internal static string? ValidateStaged(string path)
    {
        try
        {
            if (!File.Exists(path)) return "🚫 렌더 산출물이 없습니다";
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return "🚫 렌더 산출물이 링크 파일입니다";
            Span<byte> head = stackalloc byte[8];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length < 8) return "🚫 렌더 산출물이 비었거나 잘렸습니다(" + fs.Length.ToString(CultureInfo.InvariantCulture) + " B)";
            fs.ReadExactly(head);
            if (head[4] != (byte)'f' || head[5] != (byte)'t' || head[6] != (byte)'y' || head[7] != (byte)'p') return "🚫 MP4 서명(ftyp)이 없는 손상된 산출물";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "🚫 렌더 산출물을 읽지 못했습니다: " + PlutoAutonomousActivityFeedService.RedactSecrets(ex.Message);
        }
    }

    /// <summary>🎙️ 오디오/영상 길이 동등성 — 오디오가 타임라인 + 허용 오차를 넘으면 거부 · 영상은 명시 마스터 길이일 때만 양방향 비교. 통과 → null.</summary>
    internal static string? DurationMismatch(PlutoLocaleRenderOutcome o, double timeline, bool explicitMaster, double tol)
    {
        if (!(timeline > 0)) return null;
        var inv = CultureInfo.InvariantCulture;
        if (o.AudioDurationSeconds is { } a && double.IsFinite(a) && a > timeline + tol)
            return "🚫 낭독 오디오 " + a.ToString("0.00", inv) + "s 가 타임라인 " + timeline.ToString("0.00", inv) + "s 를 넘음 — A/V 싱크 보호로 커밋 거부";
        if (explicitMaster && o.VideoDurationSeconds is { } v && double.IsFinite(v) && Math.Abs(v - timeline) > tol)
            return "🚫 영상 길이 " + v.ToString("0.00", inv) + "s ≠ 마스터 " + timeline.ToString("0.00", inv) + "s — 커밋 거부";
        return null;
    }

    /// <summary>마스터 파일 이름 줄기 — 끝의 <c>_{로캘}</c> 은 걷어냄(로캘 MP4 를 마스터로 주어도 <c>x_en-US_ja-JP.mp4</c> 누적 금지).</summary>
    internal string MasterStem(string master)
    {
        var stem = Path.GetFileNameWithoutExtension(master);
        var us = stem.LastIndexOf('_');
        if (us > 0 && us < stem.Length - 1)
        {
            var tail = stem[(us + 1)..];
            if (PlutoMultiLanguageChannelService.NormalizeLocale(tail) is { } n && string.Equals(n, tail, StringComparison.OrdinalIgnoreCase) && _channels.TryGetProfile(n, out _))
                return stem[..us];
        }
        return stem;
    }

    private static string? BlockReason(string target)
    {
        try
        {
            if (Directory.Exists(target)) return "같은 이름의 폴더가 있어 저장하지 않았습니다: " + Path.GetFileName(target);
            if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) return "대상이 링크 파일이라 저장하지 않았습니다: " + Path.GetFileName(target);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "대상 확인 실패: " + ex.Message; }
    }

    private static double CueTimelineEnd(SubtitleCue[] cues)
    {
        var end = 0.0;
        foreach (var c in cues) if (double.IsFinite(c.EffectiveEndSec) && c.EffectiveEndSec > end) end = c.EffectiveEndSec;
        return end;
    }

    private List<string> ResolveTargets(PlutoMultiLocaleBatchRequest batch) => PlutoMultiLocaleBatchBundle.ResolveLocales(_channels, batch.Locales);

    private static PlutoMultiLocaleRenderResult Fail(string baseId, List<string> locales, string error)
    {
        var items = new PlutoLocaleRenderItem[locales.Count];
        for (var i = 0; i < items.Length; i++) items[i] = new PlutoLocaleRenderItem(locales[i], PlutoLocaleRenderStatus.PreflightFailed) { Error = error };
        return new PlutoMultiLocaleRenderResult(baseId, items, false, false, null);
    }

    private static long? DefaultFreeSpace(string dir)
    {
        try
        {
            var root = Path.GetPathRoot(dir);
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return null;   // UNC → 미측정(렌더러가 판단)
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private static void DeleteQuietly(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { System.Diagnostics.Debug.WriteLine("[Pluto.LocaleRender] cleanup: " + ex.Message); }
    }
}
