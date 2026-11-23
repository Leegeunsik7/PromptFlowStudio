using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🌍 Step 39 — 전 언어 일괄 분기/패키지 요청(한국어 마스터 대본 · 활성 렌더 기준).</summary>
/// <param name="BaseClipId">마스터 렌더 클립 ID(변형 ID 를 주면 마스터로 환원).</param>
/// <param name="KoreanCues">표시 자막(한국어 원문 · 장식 포함) — null/빈 → 번역 · 적합 생략, 패키지만.</param>
public sealed record PlutoMultiLocaleBatchRequest(string BaseClipId, IReadOnlyList<SubtitleCue>? KoreanCues)
{
    /// <summary>번역 트랙 맥락 키(Workspace <c>LocaleTrackContext()</c> 와 같은 값 → 렌더가 즉시 같은 트랙 사용).</summary>
    public string ContextKey { get; init; } = "montage";
    /// <summary>표시 자막의 시리즈 장식(편 · 총 편수) — 비시리즈 0.</summary>
    public int SubtitleEpisode { get; init; }
    public int SubtitleTotal { get; init; }
    public IReadOnlyList<PlutoGlossaryEntry>? Glossary { get; init; }
    /// <summary>패키지 주제(null → 카탈로그 제목에서 편 토큰 제거).</summary>
    public string? Theme { get; init; }
    public string? CatalogTitle { get; init; }
    /// <summary>패키지 · 카탈로그 제목의 편 표기(렌더 모드 기준).</summary>
    public int EpisodeNumber { get; init; }
    public int TotalEpisodes { get; init; }
    public string? HookStyle { get; init; }
    public IReadOnlyList<string>? StyleTags { get; init; }
    public bool IsShorts { get; init; } = true;
    /// <summary>렌더된 마스터 영상 경로 — 있으면 영상 옆에 로캘 사이드카 묶음 원자 저장.</summary>
    public string? VideoPath { get; init; }
    /// <summary>대상 로캘(null → <see cref="PlutoMultiLocaleBatchBundle.DefaultBatchLocales"/>).</summary>
    public IReadOnlyList<string>? Locales { get; init; }
}

/// <summary>🌍 일괄 파이프라인 협력 서비스(미연결 단계는 경고와 함께 건너뜀).</summary>
public sealed record PlutoMultiLocaleBatchServices(IPlutoSubtitleLocalizationService? Localization, IPlutoRenderCatalogService? Catalog, IPlutoYouTubePublishPackService? PublishPack);

/// <summary>🌍 로캘별 결과 상태.</summary>
public enum PlutoLocaleBatchStatus
{
    /// <summary>분기 · 패키지 생성(사이드카 요청 없음).</summary>
    Packaged,
    /// <summary>패키지 + 영상 옆 사이드카 저장.</summary>
    SidecarSaved,
    /// <summary>🔒 이미 YouTube 에 연결(게시)된 로캘 — 제목 동결 · 사이드카 재생성 안 함(중복 업로드 방지).</summary>
    AlreadyPublished,
    /// <summary>패키지는 만들었지만 사이드카 묶음이 커밋되지 않음(다른 언어 실패 → 전부 아니면 전무).</summary>
    SidecarNotCommitted,
    /// <summary>이 로캘만 실패(형제 로캘은 계속).</summary>
    Failed,
}

/// <summary>🌍 로캘 1개의 일괄 결과.</summary>
public sealed record PlutoLocaleBatchItem(string Locale, PlutoLocaleBatchStatus Status)
{
    public PlutoTranslationStatus? Translation { get; init; }
    public double SpeakingRate { get; init; } = 1.0;
    public int TtsOverflowCueCount { get; init; }
    public int TtsExcessChars { get; init; }
    public int UntranslatedCueCount { get; init; }
    public string? VariantClipId { get; init; }
    public string? YouTubeVideoId { get; init; }
    public PlutoYouTubePublishPack? Pack { get; init; }
    public string? SidecarPath { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public bool HasTtsOverflow => TtsOverflowCueCount > 0;
    public string ShortLabel => Locale.Length >= 2 ? Locale[..2].ToUpperInvariant() : Locale;
}

/// <summary>🌍 일괄 결과.</summary>
/// <param name="Busy">같은 마스터의 일괄 작업이 이미 진행 중 → 이번 호출은 아무것도 하지 않음.</param>
/// <param name="Bundle">사이드카 묶음 결과(영상 경로 없음 · 저장 대상 0 → null).</param>
public sealed record PlutoMultiLocaleBatchResult(string BaseClipId, IReadOnlyList<PlutoLocaleBatchItem> Items, bool Busy, PlutoSidecarBundleResult? Bundle)
{
    public static PlutoMultiLocaleBatchResult BusyFor(string baseClipId) => new(baseClipId, Array.Empty<PlutoLocaleBatchItem>(), true, null);

    public int Count(PlutoLocaleBatchStatus status) { var n = 0; foreach (var i in Items) if (i.Status == status) n++; return n; }

    public PlutoLocaleBatchItem? Find(string locale)
    {
        foreach (var i in Items) if (string.Equals(i.Locale, locale, StringComparison.OrdinalIgnoreCase)) return i;
        return null;
    }

    /// <summary>토스트 1줄 — <c>🌍 KO ✅ · EN 🎙️+12자 · JA ✅ · ES 🔒게시됨 · 💾 3개</c>.</summary>
    public string SummaryKorean
    {
        get
        {
            if (Busy) return "⏳ 같은 렌더의 전 언어 일괄 작업이 이미 진행 중입니다";
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(128).Append("🌍 전 언어 일괄");
            for (var k = 0; k < Items.Count; k++)
            {
                var i = Items[k];
                sb.Append(k == 0 ? " — " : " · ").Append(i.ShortLabel).Append(' ');
                switch (i.Status)
                {
                    case PlutoLocaleBatchStatus.Failed: sb.Append("❌"); break;
                    case PlutoLocaleBatchStatus.AlreadyPublished: sb.Append("🔒게시됨"); break;
                    case PlutoLocaleBatchStatus.SidecarNotCommitted: sb.Append("⏸"); break;
                    default:
                        if (i.HasTtsOverflow) sb.Append("🎙️+").Append(i.TtsExcessChars.ToString(inv)).Append('자');
                        else if (i.UntranslatedCueCount > 0) sb.Append("🌐한글 ").Append(i.UntranslatedCueCount.ToString(inv)).Append("컷");
                        else sb.Append('✅');
                        break;
                }
            }
            var saved = Count(PlutoLocaleBatchStatus.SidecarSaved);
            if (saved > 0) sb.Append(" · 💾 ").Append(saved.ToString(inv)).Append("개");
            if (Bundle is { Committed: false } b) sb.Append(" · ").Append(b.MessageKorean);
            return sb.ToString();
        }
    }
}

/// <summary>
/// 🌍 Step 39 — 원클릭 전 언어(KO · EN · JA · ES) 분기/패키지 오케스트레이터(순수 BCL · 결정론 순서).
///   로캘마다 ① 한국어 대본 → 용어집 · 고유명사 보존 번역(비파괴 트랙 · 오프라인 폴백) ② 자막 투영 · TTS 0.85x–1.30x 적합 경고 수집
///   ③ <c>{BaseClipId}::{Locale}</c> 멱등 분기(연결된 YouTube ID 불변) ④ 로캘 패키지 → 영상 옆 사이드카 묶음 원자 커밋.
///   ▸ 한 로캘의 실패 · 경고는 형제 로캘을 멈추지 않는다(취소만 전파).
///   ▸ 🔒 이미 게시(연결)된 로캘은 패키지/사이드카를 다시 만들지 않는다 — 게시 제목과 다른 새 제목이 옆에 놓여 중복 업로드 · 오연결되는 것을 차단.
///   ▸ 🧷 같은 마스터에 대한 동시 실행(연타 · 두 창)은 프로세스 단일 비행 — 두 번째 호출은 <see cref="PlutoMultiLocaleBatchResult.Busy"/>(사이드카 백업/복원 교차 방지).
/// </summary>
public static class PlutoMultiLocaleBatchBundle
{
    public static readonly IReadOnlyList<string> DefaultBatchLocales = new[] { "ko-KR", "en-US", "ja-JP", "es-ES" };

    private static readonly HashSet<string> s_inFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>테스트 · 진단용 — 해당 마스터 일괄 작업 진행 중.</summary>
    public static bool IsRunning(string baseClipId)
    {
        var key = MasterKey(baseClipId);
        lock (s_inFlight) return key.Length > 0 && s_inFlight.Contains(key);
    }

    internal static string MasterKey(string? clipId)
        => string.IsNullOrWhiteSpace(clipId) ? string.Empty : PlutoMultiLanguageChannelService.ParseVariantClipId(clipId.Trim()).BaseClipId;

    /// <summary>
    /// 🎬 Step 40 — 같은 마스터 단일 비행 슬롯 선점(일괄 패키지 · 일괄 렌더 공용). 두 작업 모두 같은 <c>{base}_{locale}_publish_pack.txt</c> 를 쓰므로
    /// 렌더 도중 패키지 일괄(또는 그 반대)이 끼어들어 사이드카를 교차 덮어쓰는 것을 프로세스 전역에서 차단한다.
    /// </summary>
    internal static bool TryEnter(string baseId)
    {
        if (string.IsNullOrEmpty(baseId)) return false;
        lock (s_inFlight) return s_inFlight.Add(baseId);
    }

    internal static void Exit(string baseId)
    {
        lock (s_inFlight) s_inFlight.Remove(baseId);
    }

    /// <summary>🎬 Step 40 — 슬롯을 이미 쥔 호출자(일괄 렌더)용 사전 분기/패키지(재진입 Busy 없이 같은 파이프라인).</summary>
    internal static Task<PlutoMultiLocaleBatchResult> RunHeldAsync(IPlutoMultiLanguageChannelService channels, string baseId, PlutoMultiLocaleBatchRequest request, PlutoMultiLocaleBatchServices services, CancellationToken ct)
        => RunCoreAsync(channels, baseId, request, services, ct);

    public static async Task<PlutoMultiLocaleBatchResult> RunAsync(IPlutoMultiLanguageChannelService channels, PlutoMultiLocaleBatchRequest request, PlutoMultiLocaleBatchServices services, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(services);
        var baseId = MasterKey(request.BaseClipId);
        if (baseId.Length == 0) throw new ArgumentException("마스터 클립 ID 가 비어 있습니다", nameof(request));
        lock (s_inFlight)
        {
            if (!s_inFlight.Add(baseId)) return PlutoMultiLocaleBatchResult.BusyFor(baseId);
        }
        try
        {
            return await RunCoreAsync(channels, baseId, request, services, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (s_inFlight) s_inFlight.Remove(baseId);
        }
    }

    /// <summary>요청 로캘 정규화 · 중복 제거 · 미등록 제외(입력 순서 유지).</summary>
    public static List<string> ResolveLocales(IPlutoMultiLanguageChannelService channels, IReadOnlyList<string>? requested)
    {
        var src = requested ?? DefaultBatchLocales;
        var list = new List<string>(src.Count);
        foreach (var raw in src)
        {
            if (!channels.TryGetProfile(raw, out var p) || PlutoMultiLanguageChannelService.NormalizeLocale(raw) is null) continue;
            var dup = false;
            foreach (var l in list) if (string.Equals(l, p.LocaleCode, StringComparison.Ordinal)) { dup = true; break; }
            if (!dup) list.Add(p.LocaleCode);
        }
        return list;
    }

    private static async Task<PlutoMultiLocaleBatchResult> RunCoreAsync(IPlutoMultiLanguageChannelService channels, string baseId, PlutoMultiLocaleBatchRequest request,
        PlutoMultiLocaleBatchServices services, CancellationToken ct)
    {
        // 📸 원문 스냅샷 — 일괄 도중 편집이 들어와도 모든 로캘이 같은 원문 기준(로캘 간 불일치 금지)
        SubtitleCue[] cues;
        if (request.KoreanCues is { Count: > 0 } src) { cues = new SubtitleCue[src.Count]; for (var i = 0; i < cues.Length; i++) cues[i] = src[i]; }
        else cues = Array.Empty<SubtitleCue>();
        var ctxKey = string.IsNullOrWhiteSpace(request.ContextKey) ? "montage" : request.ContextKey.Trim();
        int sEp = request.SubtitleEpisode, sTot = request.SubtitleTotal;
        int n = request.EpisodeNumber, total = request.TotalEpisodes;
        var baseTitle = !string.IsNullOrWhiteSpace(request.CatalogTitle) ? request.CatalogTitle!.Trim() : !string.IsNullOrWhiteSpace(request.Theme) ? request.Theme!.Trim() : baseId;
        var tags = request.StyleTags ?? Array.Empty<string>();
        var wantSidecar = !string.IsNullOrWhiteSpace(request.VideoPath);

        var locales = ResolveLocales(channels, request.Locales);
        var items = new List<PlutoLocaleBatchItem>(locales.Count);
        var staged = new List<(int Item, PlutoSidecarBundleEntry Entry)>(locales.Count);
        foreach (var loc in locales)
        {
            ct.ThrowIfCancellationRequested();
            var warnings = new List<string>(2);
            var item = new PlutoLocaleBatchItem(loc, PlutoLocaleBatchStatus.Failed);
            try
            {
                // ① 번역(기본 로캘 제외) — 실패 · 미연결은 경고만(원문 투영으로 계속)
                PlutoLocaleSubtitleTrack? track = null;
                var isDefault = string.Equals(loc, PlutoMultiLanguageChannelService.DefaultLocale, StringComparison.Ordinal);
                if (!isDefault && cues.Length > 0)
                {
                    if (services.Localization is { } l10n)
                    {
                        try
                        {
                            var tr = await l10n.TranslateAsync(ctxKey, cues, loc, request.Glossary, sEp, sTot, ct).ConfigureAwait(false);
                            item = item with { Translation = tr.Status };
                            track = tr.Track;
                            if (tr.Status is PlutoTranslationStatus.Partial or PlutoTranslationStatus.OfflineFallback) warnings.Add(tr.MessageKorean);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception ex) { warnings.Add("🌐 번역 실패(원문 유지): " + PlutoAutonomousActivityFeedService.RedactSecrets(ex.Message)); }
                    }
                    else warnings.Add("🌐 번역 서비스 미연결 — 원문 유지");
                }

                // ② 자막 투영 · TTS 적합(경고만 · 형제 로캘 계속)
                if (cues.Length > 0)
                {
                    var proj = PlutoLocaleSubtitleFitter.Project(cues, channels, loc, sEp, sTot, track);
                    item = item with
                    {
                        SpeakingRate = proj.SpeakingRate, TtsOverflowCueCount = proj.TtsOverflowCueCount, TtsExcessChars = proj.TotalExcessChars,
                        UntranslatedCueCount = proj.UntranslatedCueCount,
                    };
                    if (proj.WarningKorean.Length > 0) warnings.Add(proj.WarningKorean);
                }

                // ③ {BaseClipId}::{Locale} 멱등 분기(원본 로캘 → 마스터 그대로 · 연결 ID 불변)
                PlutoRenderedClipRecord? clip = null;
                if (services.Catalog is { } cat)
                {
                    var token = n > 0 && total > 0 ? channels.FormatEpisodeToken(loc, n, total) : string.Empty;
                    var locTitle = PlutoYouTubePublishPackService.FitTitle(PlutoYouTubePublishPackService.StripEpisodeToken(baseTitle), token);
                    clip = await cat.BranchLocaleVariantAsync(baseId, loc, locTitle, null, ct).ConfigureAwait(false);
                    if (clip is null) { items.Add(item with { Error = "마스터 렌더를 카탈로그에서 찾지 못했습니다", Warnings = warnings }); continue; }
                    item = item with { VariantClipId = clip.VideoId, YouTubeVideoId = clip.YouTubeVideoId };
                    if (!string.IsNullOrWhiteSpace(clip.YouTubeVideoId))
                    {
                        // 🔒 게시된 로캘 — 패키지 · 사이드카 재생성 금지(제목 동결 · 중복 업로드 방지)
                        items.Add(item with { Status = PlutoLocaleBatchStatus.AlreadyPublished, Warnings = warnings });
                        continue;
                    }
                }

                // ④ 로캘 패키지
                if (services.PublishPack is not { } pp) { items.Add(item with { Error = "업로드 패키지 서비스 미연결", Warnings = warnings }); continue; }
                var packTitle = clip is not null && !string.IsNullOrWhiteSpace(clip.Title) ? clip.Title : baseTitle;
                var theme = clip is { IsLocaleVariant: true } || string.IsNullOrWhiteSpace(request.Theme)
                    ? PlutoYouTubePublishPackService.StripEpisodeToken(packTitle) : request.Theme!.Trim();
                var req = new PlutoPublishPackRequest(theme, packTitle, n, total, request.HookStyle, tags) { IsShorts = request.IsShorts, TargetLocale = loc };
                var pack = await pp.GenerateAsync(req, ct).ConfigureAwait(false);
                item = item with { Status = PlutoLocaleBatchStatus.Packaged, Pack = pack };
                if (wantSidecar) staged.Add((items.Count, new PlutoSidecarBundleEntry(pack, clip?.VideoId)));
                items.Add(item with { Warnings = warnings });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                items.Add(item with { Status = PlutoLocaleBatchStatus.Failed, Error = PlutoAutonomousActivityFeedService.RedactSecrets(ex.Message), Warnings = warnings });
            }
        }

        // ⑤ 사이드카 묶음 — 전부 아니면 전무(이전 묶음 보존 · 커밋 실패 시 복원)
        PlutoSidecarBundleResult? bundle = null;
        if (staged.Count > 0 && services.PublishPack is { } writer)
        {
            var entries = new PlutoSidecarBundleEntry[staged.Count];
            for (var i = 0; i < entries.Length; i++) entries[i] = staged[i].Entry;
            try
            {
                bundle = await writer.WriteSidecarBundleAsync(request.VideoPath!, entries, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                var r = new PlutoSidecarWriteResult[entries.Length];
                for (var i = 0; i < r.Length; i++) r[i] = new PlutoSidecarWriteResult(false, null, "⚠ 패키지 저장 실패: " + PlutoAutonomousActivityFeedService.RedactSecrets(ex.Message));
                bundle = new PlutoSidecarBundleResult(false, false, r, "⚠ 사이드카 묶음 저장 실패");
            }
            for (var i = 0; i < staged.Count; i++)
            {
                var idx = staged[i].Item;
                var w = i < bundle.Results.Count ? bundle.Results[i] : null;
                items[idx] = w is { Ok: true }
                    ? items[idx] with { Status = PlutoLocaleBatchStatus.SidecarSaved, SidecarPath = w.Path }
                    : items[idx] with { Status = PlutoLocaleBatchStatus.SidecarNotCommitted, Error = w?.MessageKorean ?? bundle.MessageKorean };
            }
        }
        return new PlutoMultiLocaleBatchResult(baseId, items, false, bundle);
    }
}
