using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>📋 Step 24 — 업로드 패키지 생성 요청(활성 렌더 기준).</summary>
/// <param name="Theme">시리즈 테마 또는 영상 주제.</param>
/// <param name="CatalogTitle">렌더 카탈로그에 기록된 제목(게시 후 자동 연결 기준 · null → <paramref name="Theme"/>).</param>
/// <param name="EpisodeNumber">시리즈 편 번호(비시리즈 → 0).</param>
/// <param name="TotalEpisodes">시리즈 총 편수(비시리즈 → 0).</param>
/// <param name="HookStyle">렌더 훅 스타일 · 첫 자막.</param>
/// <param name="StyleTags">렌더 스타일 태그.</param>
public sealed record PlutoPublishPackRequest(string Theme, string? CatalogTitle, int EpisodeNumber, int TotalEpisodes, string? HookStyle, IReadOnlyList<string>? StyleTags)
{
    /// <summary>Shorts(<c>#shorts</c> 포함 · 기본) / 일반 영상.</summary>
    public bool IsShorts { get; init; } = true;
    public bool IsSeries => EpisodeNumber >= 1 && TotalEpisodes >= 2 && EpisodeNumber <= TotalEpisodes;

    /// <summary>🌐 Step 31 — 지역 채널 로캘(<c>ko-KR</c> · <c>en-US</c> · <c>ja-JP</c> · <c>es-ES</c>). null → 레거시(한글 감지 한/영) 출력.</summary>
    public string? TargetLocale { get; init; }
}

/// <summary>📋 A/B 제목 후보 1개 — 카탈로그 제목과의 연결 유사도(게시 후 자동 연결 예측) 포함.</summary>
public sealed record PlutoPublishTitleCandidate(string Variant, string Title, double LinkabilityScore)
{
    /// <summary>🅰️🅱️ Step 25 — 제목 프레이밍(<see cref="PlutoTitleFramings"/> · 연결성 안전 형태로 되돌림 → <see cref="PlutoTitleFramings.CatalogSafe"/>).</summary>
    public string Framing { get; init; } = PlutoTitleFramings.Unknown;

    /// <summary>게시 후 YouTube Studio 내보내기가 Tier 2로 자동 연결될 만큼 유사.</summary>
    public bool AutoLinkable => LinkabilityScore >= PlutoYouTubeCatalogLinker.MinFuzzyScore;
}

/// <summary>📋 Step 24 — 붙여넣기용 YouTube 업로드 메타데이터 패키지.</summary>
public sealed record PlutoYouTubePublishPack(PlutoPublishTitleCandidate TitleA, PlutoPublishTitleCandidate TitleB, string Description, string PinnedComment,
    IReadOnlyList<string> Hashtags, IReadOnlyList<string> Tags, IReadOnlyList<string> BlockedRemoved, string HookLine, bool IsKorean)
{
    /// <summary>🌐 Step 31 — 생성 로캘(레거시 → 빈 문자열). 사이드카 이름 <c>{base}_{locale}_publish_pack.txt</c> 에 쓰인다.</summary>
    public string Locale { get; init; } = string.Empty;

    /// <summary>클립보드 1회 복사용 전체 텍스트.</summary>
    public string ToClipboardText()
    {
        var sb = new StringBuilder(512);
        sb.Append("[제목 A] ").Append(TitleA.Title).Append('\n');
        sb.Append("[제목 B] ").Append(TitleB.Title).Append("\n\n");
        sb.Append("[설명]\n").Append(Description).Append("\n\n");
        sb.Append("[고정 댓글]\n").Append(PinnedComment).Append("\n\n");
        sb.Append("[태그] ").Append(string.Join(", ", Tags));
        return sb.ToString();
    }

    /// <summary>💾 Step 25 — 영상 옆 <c>{base}_publish_pack.txt</c> 본문(제목 A/B · 설명 · 고정 댓글 · 해시태그 · 태그).</summary>
    public string ToSidecarText(string videoFileName, string? clipVideoId, DateTimeOffset generatedUtc)
    {
        var sb = new StringBuilder(768);
        sb.Append("# Pluto 업로드 패키지 — ").Append(videoFileName).Append('\n');
        sb.Append("# 생성(UTC): ").Append(generatedUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
        if (!string.IsNullOrWhiteSpace(clipVideoId)) sb.Append("# 카탈로그 ID: ").Append(clipVideoId.Trim()).Append('\n');
        if (Locale.Length > 0) sb.Append("# 로캘: ").Append(Locale).Append('\n');
        sb.Append('\n');
        sb.Append("[제목 A] (").Append(TitleA.Framing).Append(")\n").Append(TitleA.Title).Append("\n\n");
        sb.Append("[제목 B] (").Append(TitleB.Framing).Append(")\n").Append(TitleB.Title).Append("\n\n");
        sb.Append("[설명]\n").Append(Description).Append("\n\n");
        sb.Append("[고정 댓글]\n").Append(PinnedComment).Append("\n\n");
        sb.Append("[해시태그]\n").Append(string.Join(" ", Hashtags)).Append("\n\n");
        sb.Append("[태그]\n").Append(string.Join(", ", Tags)).Append('\n');
        return sb.ToString().ReplaceLineEndings("\r\n");   // 메모장 · 모든 편집기에서 줄바꿈 보존
    }

    /// <summary>🅰️🅱️ 변형 문자 → 후보(<c>"B"</c> → B · 그 밖 → A).</summary>
    public PlutoPublishTitleCandidate Candidate(string variant) => string.Equals(variant, "B", StringComparison.OrdinalIgnoreCase) ? TitleB : TitleA;

    /// <summary>UI 1줄 요약.</summary>
    public string SummaryKorean => "📋 A: " + TitleA.Title + " · B: " + TitleB.Title + " · " + string.Join(" ", Hashtags)
        + (BlockedRemoved.Count > 0 ? " · 🛡️ 오답 노트 제외 " + BlockedRemoved.Count.ToString(CultureInfo.InvariantCulture) + "개" : "");
}

/// <summary>📋 Step 24 — 게시 전 YouTube 업로드 메타데이터(A/B 제목 · 훅 정렬 설명 · 고정 댓글 CTA · 파레토 해시태그) 생성기.</summary>
public interface IPlutoYouTubePublishPackService
{
    /// <summary>메모리 기준 동기 생성(결정론).</summary>
    PlutoYouTubePublishPack Generate(PlutoPublishPackRequest request);

    /// <summary>랭커 · 오답 노트 · 시리즈 아크 선적재 후 생성(적재 실패 → [부분 구제] 메모리 기준 · 취소만 전파).</summary>
    Task<PlutoYouTubePublishPack> GenerateAsync(PlutoPublishPackRequest request, CancellationToken ct = default);

    /// <summary>🅰️🅱️ Step 25 — 현재 프레이밍 순위(실측 CTR 축소 추정 · 근거 부족 → 기본 순서).</summary>
    IReadOnlyList<PlutoTitleFramingStat> GetFramingRanking();

    /// <summary>🅰️🅱️ Step 25 — 카탈로그(실측 귀속 확정 클립의 워터마크 CTR · 조회수)로 프레이밍 근거 재계산 · 반환 = 귀속 표본 수.</summary>
    int RefreshFramingEvidence(IReadOnlyList<PlutoRenderedClipRecord> clips);

    /// <summary>🅰️🅱️ Step 25 — 크리에이터가 복사한 변형(<c>"A"</c> · <c>"B"</c> · <c>"AB"</c> = 전체 패키지)을 카탈로그에 기록.</summary>
    Task<bool> RecordSelectionAsync(string clipVideoId, PlutoYouTubePublishPack pack, string variant, CancellationToken ct = default);

    /// <summary>💾 Step 25 — 렌더된 영상 옆에 <c>{base}_publish_pack.txt</c> 원자 기록(경로 검증 실패 → 무기록).</summary>
    Task<PlutoSidecarWriteResult> WriteSidecarAsync(string videoPath, PlutoYouTubePublishPack pack, string? clipVideoId = null, CancellationToken ct = default);
}

/// <summary>🅰️🅱️ Step 25 — 업로드 제목 프레이밍 상수(학습 키 · 카탈로그 기록값).</summary>
public static class PlutoTitleFramings
{
    /// <summary>정보/결과 증명형 — <c>{제목} {n/N편} · {훅}</c>(Step 24 기본 A).</summary>
    public const string OutcomeProof = "outcome-proof";
    /// <summary>호기심/질문형 — <c>{훅} | {제목} {n/N편}</c>(Step 24 기본 B).</summary>
    public const string CuriosityQuestion = "curiosity-question";
    /// <summary>결말 유도형 — <c>{제목} — 결말 예상? {n/N편}</c>.</summary>
    public const string EndingPrompt = "ending-prompt";
    /// <summary>연결성 자가 검사로 되돌린 카탈로그 안전 형태 — <c>{제목} {n/N편}</c>.</summary>
    public const string CatalogSafe = "catalog-safe";
    /// <summary>미상(구버전 기록).</summary>
    public const string Unknown = "unknown";

    /// <summary>근거 부족 시 기본 순서(Step 24 A/B 출력과 동일).</summary>
    public static readonly IReadOnlyList<string> DefaultOrder = new[] { OutcomeProof, CuriosityQuestion, EndingPrompt };

    public static bool IsComposable(string? f) => f is OutcomeProof or CuriosityQuestion or EndingPrompt;
}

/// <summary>🅰️🅱️ 프레이밍 1개의 실측 근거 — 표본 수 · 누적 조회수 · 평균 CTR · 경험적 베이즈 축소 CTR(순위 키).</summary>
public sealed record PlutoTitleFramingStat(string Framing, int Samples, long Views, double MeanCtrPct, double ShrunkCtrPct);

/// <summary>💾 사이드카 기록 결과.</summary>
public sealed record PlutoSidecarWriteResult(bool Ok, string? Path, string MessageKorean);

/// <summary>
/// 📋 <see cref="IPlutoYouTubePublishPackService"/> 순수 BCL 구현.
///   ▸ 제목 A(정보형) · B(호기심형) — 둘 다 <c>n/N편</c> 토큰 보존(링커 편 가드 · 시리즈 아크 키) · 100자 상한(토큰은 절대 자르지 않음).
///   ▸ 🔗 연결성 자가 검사: 후보 제목이 카탈로그 제목과의 <see cref="PlutoYouTubeCatalogLinker.TitleSimilarity"/> 하한 미만이면
///     카탈로그 제목 중심 안전 형태로 되돌려 게시 후 내보내기가 미매칭 대기열로 빠지지 않게 한다.
///   ▸ 훅 · 태그는 파레토/학습 랭킹 순 · 오답 노트 차단 토큰은 제외(제외 목록 보고). 해시태그 ≤ <see cref="MaxHashtags"/>개(YouTube 15개 초과 시 전부 무시됨).
/// </summary>
public sealed class PlutoYouTubePublishPackService : IPlutoYouTubePublishPackService
{
    public const int MaxTitleLength = 100;
    public const int MaxHashtags = 8;
    public const int MaxTagsTotalChars = 450;   // YouTube 태그 필드 500자 여유
    public const int MaxHookPhraseLength = 40;

    // ── 🅰️🅱️ Step 25 — 프레이밍 귀속 ──
    /// <summary>귀속 표본 최소 조회수(그 미만은 CTR 잡음).</summary>
    public const long MinViewsForAttribution = 50;
    /// <summary>이 조회수부터 표본 가중치 1(그 아래는 조회수 비례 감쇠).</summary>
    public const long FullWeightViews = 1000;
    /// <summary>경험적 베이즈 사전 강도(가상 표본 수) — 행운의 1편이 순위를 뒤집지 못하게.</summary>
    public const double FramingPriorStrength = 2.0;
    /// <summary>순위 재정렬에 필요한 최소 귀속 표본(전체).</summary>
    public const int MinAttributedSamples = 2;
    /// <summary>실제 게시 제목 ↔ 복사 제목 확정 유사도 하한(정규화 완전 일치는 즉시 확정).</summary>
    public const double RealizedTitleConfirmScore = 0.8;
    /// <summary>💾 사이드카 접미.</summary>
    public const string SidecarSuffix = "_publish_pack.txt";

    private static readonly string[] SidecarVideoExtensions = { ".mp4", ".mov", ".webm", ".mkv" };

    private static readonly System.Text.RegularExpressions.Regex LocalizedEpisodeTokenRx = new(@"第\s*\d{1,3}\s*/\s*\d{1,3}\s*話|\bEp\.\s?\d{1,3}\s*/\s*\d{1,3}",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    private readonly IPlutoAdaptivePatternRanker? _ranker;
    private readonly IPlutoSeriesArcMemoryService? _arc;
    private readonly IPlutoMistakeNotebookService? _notebook;
    private readonly IPlutoRenderCatalogService? _catalog;
    private readonly Func<DateTimeOffset> _clock;
    private PlutoTitleFramingStat[] _framingStats = Array.Empty<PlutoTitleFramingStat>();   // 교체식 갱신 → 락 없는 스냅샷 읽기

    public PlutoYouTubePublishPackService(IPlutoAdaptivePatternRanker? ranker = null, IPlutoSeriesArcMemoryService? arc = null, IPlutoMistakeNotebookService? notebook = null,
        IPlutoRenderCatalogService? catalog = null, Func<DateTimeOffset>? clock = null)
    {
        _ranker = ranker;
        _arc = arc;
        _notebook = notebook;
        _catalog = catalog;
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
    }

    public async Task<PlutoYouTubePublishPack> GenerateAsync(PlutoPublishPackRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            if (_notebook is not null) await _notebook.EnsureLoadedAsync(ct).ConfigureAwait(false);
            if (_ranker is not null) await _ranker.EnsureLoadedAsync(ct).ConfigureAwait(false);
            if (_arc is not null) await _arc.EnsureLoadedAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.PublishPack] preload ([부분 구제]): " + ex.Message); }
        try
        {
            // 🅰️🅱️ 실측 CTR 귀속(카탈로그 = 멀티 PC 병합 허브)으로 프레이밍 순위 갱신
            if (_catalog is not null) RefreshFramingEvidence(await _catalog.GetRecentClipsAsync(PlutoRenderCatalogService.Capacity, ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.PublishPack] framing evidence ([부분 구제]): " + ex.Message); }
        return Generate(request);
    }

    // ── 🅰️🅱️ 프레이밍 귀속 ──

    public IReadOnlyList<PlutoTitleFramingStat> GetFramingRanking()
    {
        var stats = Volatile.Read(ref _framingStats);
        var order = FramingOrder(stats);
        var result = new List<PlutoTitleFramingStat>(order.Count);
        foreach (var f in order)
        {
            var s = FindStat(stats, f);
            result.Add(s ?? new PlutoTitleFramingStat(f, 0, 0, 0, 0));
        }
        return result;
    }

    public int RefreshFramingEvidence(IReadOnlyList<PlutoRenderedClipRecord> clips)
    {
        ArgumentNullException.ThrowIfNull(clips);
        var stats = ComputeFramingStats(clips);
        Volatile.Write(ref _framingStats, stats);
        var n = 0;
        foreach (var s in stats) n += s.Samples;
        return n;
    }

    /// <summary>
    /// 🅰️🅱️ 순수 집계 — 실측 귀속 확정(<see cref="PlutoTitleAttributionStates.Confirmed"/>) 클립의 마지막 반영 워터마크(CTR · 조회수)만 사용.
    /// 클립당 1표본(재수집 · 성숙 증분 · 멀티 PC 중복이 이중 집계되지 않음) · 조회수 가중 · 전역 평균으로 경험적 베이즈 축소.
    /// </summary>
    public static PlutoTitleFramingStat[] ComputeFramingStats(IReadOnlyList<PlutoRenderedClipRecord> clips)
    {
        ArgumentNullException.ThrowIfNull(clips);
        var keys = new List<string>(4);
        var count = new List<int>(4);
        var views = new List<long>(4);
        var wSum = new List<double>(4);
        var wCtr = new List<double>(4);
        var seenYt = new HashSet<string>(StringComparer.Ordinal);
        double gW = 0, gCtr = 0;
        foreach (var c in clips)
        {
            if (c is null || c.TitleAttribution != PlutoTitleAttributionStates.Confirmed || c.YouTubeWatermark is not { } wm) continue;
            var f = c.PublishedTitleFraming;
            if (string.IsNullOrWhiteSpace(f) || f == PlutoTitleFramings.Unknown) continue;
            if (wm.Views < MinViewsForAttribution || !double.IsFinite(wm.CtrPct) || wm.CtrPct <= 0 || wm.CtrPct > 100) continue;
            if (c.YouTubeVideoId is { Length: > 0 } yt && !seenYt.Add(yt)) continue;
            var w = Math.Min(1.0, (double)wm.Views / FullWeightViews);
            var i = keys.IndexOf(f);
            if (i < 0) { keys.Add(f); count.Add(0); views.Add(0); wSum.Add(0); wCtr.Add(0); i = keys.Count - 1; }
            count[i]++;
            views[i] += wm.Views;
            wSum[i] += w;
            wCtr[i] += w * wm.CtrPct;
            gW += w;
            gCtr += w * wm.CtrPct;
        }
        foreach (var d in PlutoTitleFramings.DefaultOrder)
            if (!keys.Contains(d)) { keys.Add(d); count.Add(0); views.Add(0); wSum.Add(0); wCtr.Add(0); }
        var mu = gW > 0 ? gCtr / gW : 0;
        var result = new PlutoTitleFramingStat[keys.Count];
        for (var i = 0; i < keys.Count; i++)
        {
            var mean = wSum[i] > 0 ? wCtr[i] / wSum[i] : 0;
            var shrunk = (wCtr[i] + FramingPriorStrength * mu) / (wSum[i] + FramingPriorStrength);
            result[i] = new PlutoTitleFramingStat(keys[i], count[i], views[i], mean, shrunk);
        }
        return result;
    }

    private static PlutoTitleFramingStat? FindStat(PlutoTitleFramingStat[] stats, string framing)
    {
        foreach (var s in stats) if (string.Equals(s.Framing, framing, StringComparison.Ordinal)) return s;
        return null;
    }

    /// <summary>합성 가능한 프레이밍 순서 — 귀속 표본 ≥ <see cref="MinAttributedSamples"/> 이면 축소 CTR 내림차순(동률 → 기본 순서) · 그 밖 기본 순서.</summary>
    private static List<string> FramingOrder(PlutoTitleFramingStat[] stats)
    {
        var order = new List<string>(PlutoTitleFramings.DefaultOrder);
        var total = 0;
        foreach (var s in stats) if (PlutoTitleFramings.IsComposable(s.Framing)) total += s.Samples;
        if (total < MinAttributedSamples) return order;
        order.Sort((x, y) =>
        {
            var sx = FindStat(stats, x)?.ShrunkCtrPct ?? 0;
            var sy = FindStat(stats, y)?.ShrunkCtrPct ?? 0;
            var c = sy.CompareTo(sx);
            return c != 0 ? c : IndexOfDefault(x).CompareTo(IndexOfDefault(y));
        });
        return order;
    }

    private static int IndexOfDefault(string f)
    {
        for (var i = 0; i < PlutoTitleFramings.DefaultOrder.Count; i++) if (PlutoTitleFramings.DefaultOrder[i] == f) return i;
        return int.MaxValue;
    }

    public async Task<bool> RecordSelectionAsync(string clipVideoId, PlutoYouTubePublishPack pack, string variant, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (_catalog is null || string.IsNullOrWhiteSpace(clipVideoId)) return false;
        var v = variant?.Trim().ToUpperInvariant() ?? string.Empty;
        var selection = v switch
        {
            "A" => new PlutoPublishedTitleSelection("A", pack.TitleA.Framing, pack.TitleA.Title),
            "B" => new PlutoPublishedTitleSelection("B", pack.TitleB.Framing, pack.TitleB.Title),
            "AB" => new PlutoPublishedTitleSelection("A", pack.TitleA.Framing, pack.TitleA.Title, pack.TitleB.Framing, pack.TitleB.Title),   // 전체 패키지 → 실제 게시 제목으로 판정
            _ => null,
        };
        if (selection is null) return false;
        return await _catalog.RecordPublishedTitleAsync(clipVideoId, selection, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 🅰️🅱️ 순수 — 연결된 실제 YouTube 제목으로 변형 귀속 판정. 정규화 완전 일치 우선 → 유사도 ≥ <see cref="RealizedTitleConfirmScore"/> 이고
    /// 반대 변형보다 <see cref="PlutoYouTubeCatalogLinker.AmbiguityMargin"/> 이상 앞설 때 확정(필요 시 A↔B 교정) · 그 밖 → <see cref="PlutoTitleAttributionStates.Diverged"/>(귀속 제외).
    /// 이미 판정된 클립 · 제목 미기록 → 그대로.
    /// </summary>
    public static PlutoRenderedClipRecord ResolveRealizedTitle(PlutoRenderedClipRecord clip, string? realizedTitle)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (string.IsNullOrWhiteSpace(realizedTitle) || string.IsNullOrWhiteSpace(clip.PublishedTitleText) || clip.SelectedTitleVariant is not ("A" or "B")) return clip;
        if (clip.TitleAttribution is PlutoTitleAttributionStates.Confirmed or PlutoTitleAttributionStates.Diverged) return clip;
        var real = PlutoRenderCatalogService.NormalizeKey(realizedTitle);
        var sel = clip.PublishedTitleText!;
        var alt = clip.AlternateTitleText;
        var exactSel = real.Length > 0 && real == PlutoRenderCatalogService.NormalizeKey(sel);
        var exactAlt = real.Length > 0 && !string.IsNullOrWhiteSpace(alt) && real == PlutoRenderCatalogService.NormalizeKey(alt);
        bool useAlt;
        if (exactSel != exactAlt) useAlt = exactAlt;
        else
        {
            var simSel = PlutoYouTubeCatalogLinker.TitleSimilarity(realizedTitle, sel);
            var simAlt = string.IsNullOrWhiteSpace(alt) ? -1.0 : PlutoYouTubeCatalogLinker.TitleSimilarity(realizedTitle, alt);
            var m = PlutoYouTubeCatalogLinker.AmbiguityMargin;
            if (exactSel && exactAlt) simSel = simAlt = 1.0;   // A = B 정규형(차별화 실패) → 모호
            if (simSel >= RealizedTitleConfirmScore && simSel >= simAlt + m) useAlt = false;
            else if (simAlt >= RealizedTitleConfirmScore && simAlt >= simSel + m) useAlt = true;
            else return clip with { AlternateTitleFraming = null, AlternateTitleText = null, TitleAttribution = PlutoTitleAttributionStates.Diverged };
        }
        if (!useAlt) return clip with { AlternateTitleFraming = null, AlternateTitleText = null, TitleAttribution = PlutoTitleAttributionStates.Confirmed };
        return clip with
        {
            SelectedTitleVariant = clip.SelectedTitleVariant == "A" ? "B" : "A",
            PublishedTitleFraming = string.IsNullOrWhiteSpace(clip.AlternateTitleFraming) ? PlutoTitleFramings.Unknown : clip.AlternateTitleFraming,
            PublishedTitleText = alt,
            AlternateTitleFraming = null,
            AlternateTitleText = null,
            TitleAttribution = PlutoTitleAttributionStates.Confirmed,
        };
    }

    // ── 💾 사이드카 ──

    /// <summary>
    /// 💾 순수 경로 검증 — 완전 한정 · <c>..</c> 세그먼트 없음 · 장치/확장 접두(<c>\\?\</c> · <c>\\.\</c>) 금지 · 영상 확장자(mp4/mov/webm/mkv) →
    /// <c>{dir}\{base}_publish_pack.txt</c>. 실패 → null + 사유.
    /// </summary>
    public static string? TryGetSidecarPath(string? videoPath, out string reason) => TryGetSidecarPath(videoPath, null, out reason);

    /// <summary>
    /// 🌐 Step 31 — 로캘 사이드카 경로 <c>{dir}\{base}_{locale}_publish_pack.txt</c>(로캘 null/빈 → 레거시 이름).
    /// 로캘은 <see cref="PlutoMultiLanguageChannelService.NormalizeLocale"/> 통과 값만(문자/숫자/하이픈 · 경로 문자 주입 차단).
    /// </summary>
    public static string? TryGetSidecarPath(string? videoPath, string? locale, out string reason)
    {
        var loc = string.Empty;
        if (!string.IsNullOrWhiteSpace(locale))
        {
            if (PlutoMultiLanguageChannelService.NormalizeLocale(locale) is not { } n) { reason = "로캘 형식이 올바르지 않습니다"; return null; }
            loc = "_" + n;
        }
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(videoPath)) { reason = "렌더된 영상 경로가 없습니다"; return null; }
        var p = videoPath.Trim();
        if (p.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || p.IndexOf('*') >= 0 || p.IndexOf('?') >= 0) { reason = "경로에 허용되지 않는 문자가 있습니다"; return null; }
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal)) { reason = "장치 경로는 허용되지 않습니다"; return null; }
        if (!Path.IsPathFullyQualified(p)) { reason = "절대 경로가 아닙니다"; return null; }
        foreach (var seg in p.Split('\\', '/'))
            if (seg == "..") { reason = "상위 폴더 이동(..)이 포함된 경로입니다"; return null; }
        var ext = Path.GetExtension(p);
        var okExt = false;
        foreach (var e in SidecarVideoExtensions) if (string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)) { okExt = true; break; }
        if (!okExt) { reason = "영상 파일(.mp4/.mov/.webm/.mkv)이 아닙니다"; return null; }
        var dir = Path.GetDirectoryName(p);
        var stem = Path.GetFileNameWithoutExtension(p);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrWhiteSpace(stem)) { reason = "영상 파일 이름이 비어 있습니다"; return null; }
        var name = stem + loc + SidecarSuffix;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { reason = "파일 이름에 허용되지 않는 문자가 있습니다"; return null; }
        return Path.Combine(dir, name);
    }

    public async Task<PlutoSidecarWriteResult> WriteSidecarAsync(string videoPath, PlutoYouTubePublishPack pack, string? clipVideoId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (TryGetSidecarPath(videoPath, pack.Locale, out var reason) is not { } sidecar) return new PlutoSidecarWriteResult(false, null, "⚠ " + reason);
        var video = videoPath.Trim();
        string? tmp = null;
        try
        {
            if (!File.Exists(video)) return new PlutoSidecarWriteResult(false, null, "⚠ 렌더된 영상 파일을 찾지 못했습니다");
            if (Directory.Exists(sidecar)) return new PlutoSidecarWriteResult(false, null, "⚠ 같은 이름의 폴더가 있어 저장하지 않았습니다");
            // 🔗 심볼릭 링크/정션이 심어진 자리는 따라가지 않음(링크 대상 파일 덮어쓰기 방지)
            if (File.Exists(sidecar) && (File.GetAttributes(sidecar) & FileAttributes.ReparsePoint) != 0)
                return new PlutoSidecarWriteResult(false, null, "⚠ 대상이 링크 파일이라 저장하지 않았습니다");
            var text = pack.ToSidecarText(Path.GetFileName(video), clipVideoId, _clock());
            tmp = sidecar + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(tmp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), ct).ConfigureAwait(false);
            File.Move(tmp, sidecar, overwrite: true);   // ⚛️ 원자 교체 — 업로드 중 반쪽 파일 노출 없음
            tmp = null;
            return new PlutoSidecarWriteResult(true, sidecar, "💾 영상 옆에 저장: " + Path.GetFileName(sidecar));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.PublishPack] sidecar: " + ex.Message);
            return new PlutoSidecarWriteResult(false, null, "⚠ 패키지 저장 실패: " + ex.Message);
        }
        finally
        {
            if (tmp is not null)
                try { File.Delete(tmp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { System.Diagnostics.Debug.WriteLine("[Pluto.PublishPack] tmp: " + ex.Message); }
        }
    }

    public PlutoYouTubePublishPack Generate(PlutoPublishPackRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var theme = Clean(request.Theme);
        var catalogTitle = Clean(request.CatalogTitle);
        if (theme.Length == 0) theme = catalogTitle.Length > 0 ? PlutoSeriesArcMemoryService.SeriesThemeOf(catalogTitle) : "Pluto";
        if (catalogTitle.Length == 0) catalogTitle = theme;
        // 🌐 Step 31 — 로캘 지정 → 프로필(편 토큰 · 훅 스타일 · 기본 해시태그 · 설명/CTA 문구) · 미지정 → 레거시(한글 감지)
        var locale = PlutoMultiLanguageChannelService.NormalizeLocale(request.TargetLocale);
        var channels = Channels ?? SharedChannels;
        PlutoChannelLocaleProfile? profile = null;
        if (locale is not null && channels.TryGetProfile(locale, out var lp)) profile = lp;
        else locale = null;
        var korean = profile is not null ? string.Equals(profile.LocaleCode, "ko-KR", StringComparison.Ordinal) : ContainsHangul(theme) || ContainsHangul(catalogTitle);
        var legacyCopy = profile is null || profile.LocaleCode is "ko-KR" or "en-US";
        var suppressed = SafeSuppressed();
        var blocked = new List<string>();

        // ── 훅: 요청 훅 → (차단 시) 파레토/학습 1위 훅 → 기본 문구 ──
        var hook = Clean(request.HookStyle);
        if (hook.Length > 0 && IsBlocked(hook, suppressed)) { blocked.Add(hook); hook = string.Empty; }
        if (hook.Length == 0 || IsGenericHookKey(hook)) hook = TopRankedHook(suppressed) ?? string.Empty;
        var directive = SafeDirective(theme, request);
        if (directive.EscalateHook && directive.RecommendedHookLine.Length > 0 && !IsBlocked(directive.RecommendedHookLine, suppressed))
            hook = directive.RecommendedHookLine;   // 📺 편 간 이탈 → 아크 메모리 강화 훅 우선
        var hookPhrase = HookPhrase(hook, korean);
        if (profile is not null)
        {
            // 키 형태/일반 훅을 영어로 매핑한 문구는 ja/es 채널에 맞지 않음 → 로캘 기본 훅 · 그 밖은 로캘 스타일로 각색
            var raw = Clean(hook);
            var keyLike = raw.Length == 0 || IsGenericHookKey(raw) || (raw.IndexOf(' ') < 0 && (raw.IndexOf('-') > 0 || raw.IndexOf('_') > 0));
            hookPhrase = channels.AdaptHook(profile.LocaleCode, keyLike && !legacyCopy ? string.Empty : hookPhrase);
        }

        // ── 제목 A/B ──
        var token = !request.IsSeries ? string.Empty
            : profile is not null ? channels.FormatEpisodeToken(profile.LocaleCode, request.EpisodeNumber, request.TotalEpisodes)
            : EpisodeToken(request.EpisodeNumber, request.TotalEpisodes);
        var endingSuffix = profile is not null ? profile.Copy.EndingPromptSuffix : korean ? " — 결말 예상?" : " — guess the ending?";
        var core = StripEpisodeToken(catalogTitle);
        if (core.Length == 0) core = StripEpisodeToken(theme);
        // 🅰️🅱️ Step 25 — 실측 CTR 순위 1위 프레이밍 = A · A와 다른 다음 순위 = B(근거 부족 → Step 24 기본: 정보형 A · 호기심형 B)
        PlutoPublishTitleCandidate? a = null, b = null;
        foreach (var framing in FramingOrder(Volatile.Read(ref _framingStats)))
        {
            var cand = EnsureLinkable(a is null ? "A" : "B", ComposeFramed(framing, core, token, hookPhrase, endingSuffix), catalogTitle, core, token, framing);
            if (a is null) { a = cand; continue; }
            if (!string.Equals(cand.Title, a.Title, StringComparison.Ordinal)) { b = cand; break; }   // A/B가 같아지면 실험 불가 → 다음 프레이밍
        }
        a ??= EnsureLinkable("A", FitTitle(core, token), catalogTitle, core, token, PlutoTitleFramings.CatalogSafe);
        b ??= EnsureLinkable("B", ComposeFramed(PlutoTitleFramings.EndingPrompt, core, token, hookPhrase, endingSuffix), catalogTitle, core, token, PlutoTitleFramings.EndingPrompt);

        // ── 해시태그 · 태그 ──
        var tagCandidates = new List<string>();
        if (request.StyleTags is { } st) foreach (var t in st) tagCandidates.Add(t);
        foreach (var lt in ParetoTagLeaders()) tagCandidates.Add(lt);
        foreach (var t in PlutoYouTubeCatalogLinker.Tokenize(theme)) if (t.Length >= 2) tagCandidates.Add(t);
        var ranked = RankTagsSafe(tagCandidates);
        var hashtags = new List<string>(MaxHashtags);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (request.IsShorts) { hashtags.Add("#shorts"); seen.Add("shorts"); }
        if (request.IsShorts && profile is not null)   // 🌐 로캘 기본 해시태그(#쇼츠 · #ショート · #cortos)
            foreach (var dh in profile.DefaultHashtags)
            {
                var body = HashtagBody(dh);
                if (body.Length >= 2 && hashtags.Count < MaxHashtags && seen.Add(body)) hashtags.Add("#" + body);
            }
        var seriesTag = request.IsSeries ? HashtagBody(StripEpisodeToken(theme)) : string.Empty;
        if (seriesTag.Length > 0 && seen.Add(seriesTag)) hashtags.Add("#" + seriesTag);
        foreach (var raw in ranked)
        {
            if (hashtags.Count >= MaxHashtags) break;
            if (IsBlocked(raw, suppressed)) { if (!ContainsIgnoreCase(blocked, raw)) blocked.Add(raw); continue; }
            var body = HashtagBody(raw);
            if (body.Length < 2 || !seen.Add(body)) continue;
            hashtags.Add("#" + body);
        }
        var tags = new List<string>();
        var total = 0;
        foreach (var h in hashtags)
        {
            var t = h.TrimStart('#');
            if (total + t.Length + 1 > MaxTagsTotalChars) break;
            tags.Add(t);
            total += t.Length + 1;
        }
        if (core.Length > 0 && total + core.Length + 1 <= MaxTagsTotalChars && !ContainsIgnoreCase(tags, core)) tags.Insert(0, core);

        var description = legacyCopy ? BuildDescription(request, a.Title, hookPhrase, directive, hashtags, korean) : BuildLocalizedDescription(request, a.Title, hookPhrase, directive, hashtags, profile!.Copy);
        var pinned = legacyCopy ? BuildPinnedComment(request, korean) : BuildLocalizedPinnedComment(request, profile!.Copy);
        return new PlutoYouTubePublishPack(a, b, description, pinned, hashtags, tags, blocked, hookPhrase, korean) { Locale = locale ?? string.Empty };
    }

    /// <summary>🌐 Step 31 — 공유 기본 로캘 레지스트리(주입 없을 때 · 내장 4종).</summary>
    private static readonly PlutoMultiLanguageChannelService SharedChannels = new();

    /// <summary>🌐 Step 31 — 로캘 레지스트리(사용자 정의 로캘 포함 · null → 내장 4종).</summary>
    public IPlutoMultiLanguageChannelService? Channels { get; init; }

    private static string BuildLocalizedDescription(PlutoPublishPackRequest r, string title, string hookPhrase, PlutoSeriesContinuityDirective d, List<string> hashtags, PlutoLocaleCopy copy)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(400);
        sb.Append(hookPhrase).Append('\n');   // 첫 줄 = 로캘 훅
        sb.Append(title).Append("\n\n");
        if (r.IsSeries)
        {
            sb.Append(string.Format(inv, copy.SeriesLineTemplate, r.EpisodeNumber, r.TotalEpisodes));
            if (r.EpisodeNumber > 1 && copy.ContinuesFromTemplate.Length > 0) sb.Append(string.Format(inv, copy.ContinuesFromTemplate, r.EpisodeNumber - 1));
            sb.Append('\n');
            if (r.EpisodeNumber < r.TotalEpisodes)
                sb.Append(string.Format(inv, copy.NextEpisodeTemplate, r.EpisodeNumber + 1)).Append(d.CliffhangerOutroLine).Append('\n');
            else sb.Append(copy.SeriesFinaleLine).Append('\n');
            sb.Append('\n');
        }
        sb.Append(copy.SubscribeLine).Append("\n\n");
        sb.Append(string.Join(" ", hashtags));
        return sb.ToString();
    }

    private static string BuildLocalizedPinnedComment(PlutoPublishPackRequest r, PlutoLocaleCopy copy)
    {
        if (r.IsSeries && r.EpisodeNumber < r.TotalEpisodes) return string.Format(CultureInfo.InvariantCulture, copy.PinnedNextTemplate, r.EpisodeNumber + 1);
        return r.IsSeries ? copy.PinnedSeriesCompleteLine : copy.PinnedStandaloneLine;
    }

    // ── 순수 함수 ──

    /// <summary><c>n/N편</c> 토큰(링커 정규식 · 아크 메모리 파서 공통 형식).</summary>
    public static string EpisodeToken(int n, int total) => n.ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture) + "편";

    public static bool ContainsHangul(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var ch in s) if (ch is >= '가' and <= '힣') return true;
        return false;
    }

    private static string Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var ch in s.Trim())
        {
            if (char.IsControl(ch) || ch is '<' or '>') continue;   // YouTube 제목/설명 금지 문자
            if (char.IsWhiteSpace(ch)) { if (!space) sb.Append(' '); space = true; continue; }
            space = false;
            sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    /// <summary>편 표기(<c>2/3편</c> · <c>[2/3편]</c>) 제거 후 정리.</summary>
    public static string StripEpisodeToken(string? title)
    {
        var s = Clean(title);
        if (s.Length == 0) return s;
        // 🌐 Step 31 — 로캘 편 토큰(第1/3話 · Ep.1/3 · Ep. 1/3) 제거 — 안 지우면 core 에 남아 로캘 토큰이 제목에 두 번 들어감
        try
        {
            var stripped = LocalizedEpisodeTokenRx.Replace(s, " ");
            if (!string.Equals(stripped, s, StringComparison.Ordinal)) s = Clean(stripped).Trim('[', ']', '(', ')', '·', '-', '—').Trim();
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { System.Diagnostics.Debug.WriteLine("[Pluto.PublishPack] token strip timeout"); }
        if (s.Length == 0) return s;
        var theme = PlutoSeriesArcMemoryService.SeriesThemeOf(s);
        if (!string.Equals(theme, s, StringComparison.Ordinal)) return theme;
        // 테마 추출이 실패한 경우에도 남은 "n/N편"을 직접 제거
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var j = i;
            while (j < s.Length && char.IsAsciiDigit(s[j])) j++;
            if (j > i && j < s.Length && s[j] == '/')
            {
                var k = j + 1;
                while (k < s.Length && char.IsAsciiDigit(s[k])) k++;
                if (k > j + 1 && k < s.Length && s[k] == '편') { i = k; continue; }
            }
            sb.Append(s[i]);
        }
        return sb.ToString().Trim().Trim('[', ']', '(', ')', '·', '-', '—').Trim();
    }

    private static bool IsGenericHookKey(string hook)
        => hook.Equals("montage-hook", StringComparison.OrdinalIgnoreCase) || hook.Equals("훅", StringComparison.Ordinal) || hook.Equals("hook", StringComparison.OrdinalIgnoreCase);

    /// <summary>훅 키/자막 → 제목 문구(최대 <see cref="MaxHookPhraseLength"/>자 · 키 형태는 로캘 문구로 매핑).</summary>
    public static string HookPhrase(string? hook, bool korean)
    {
        var h = Clean(hook);
        if (h.Length == 0 || IsGenericHookKey(h)) return korean ? "끝까지 보면 반전" : "Wait for the ending";
        if (h.IndexOf(' ') < 0 && (h.IndexOf('-') > 0 || h.IndexOf('_') > 0))
        {
            var k = h.ToLowerInvariant();
            if (k.Contains("question", StringComparison.Ordinal)) return korean ? "이거 알고 있었나요?" : "Did you know this?";
            if (k.Contains("countdown", StringComparison.Ordinal)) return korean ? "3초 뒤 놀라운 장면" : "In 3 seconds…";
            if (k.Contains("reveal", StringComparison.Ordinal) || k.Contains("twist", StringComparison.Ordinal)) return korean ? "마지막 장면 주목" : "Watch the last scene";
            if (k.Contains("pov", StringComparison.Ordinal)) return "POV";
            h = h.Replace('-', ' ').Replace('_', ' ');
        }
        return h.Length > MaxHookPhraseLength ? h[..MaxHookPhraseLength].TrimEnd() + "…" : h;
    }

    private static string ComposeTitle(string core, string token, string hookPhrase, bool curiosity)
    {
        // A(정보형): "{core} {n/N편} · {hook}"  ·  B(호기심형): "{hook} | {core} {n/N편}"
        var main = token.Length > 0 ? core + " " + token : core;
        var full = curiosity ? hookPhrase + " | " + main : main + " · " + hookPhrase;
        if (full.Length <= MaxTitleLength) return full;
        return FitTitle(core, token);   // 길면 훅 문구를 버리고 연결 핵심(제목 + 편 토큰)만 유지
    }

    /// <summary>제목 + 편 토큰을 100자에 맞춤 — 토큰은 절대 자르지 않고 제목을 줄인다.</summary>
    public static string FitTitle(string core, string token)
    {
        var suffix = token.Length > 0 ? " " + token : string.Empty;
        var room = MaxTitleLength - suffix.Length;
        var c = core.Length > room ? core[..Math.Max(0, room - 1)].TrimEnd() + "…" : core;
        return (c + suffix).Trim();
    }

    /// <summary>프레이밍별 제목 합성(알 수 없는 프레이밍 → 정보형).</summary>
    private static string ComposeFramed(string framing, string core, string token, string hookPhrase, string endingSuffix) => framing switch
    {
        PlutoTitleFramings.CuriosityQuestion => ComposeTitle(core, token, hookPhrase, curiosity: true),
        PlutoTitleFramings.EndingPrompt => FitTitle(core + endingSuffix, token),
        _ => ComposeTitle(core, token, hookPhrase, curiosity: false),
    };

    private static PlutoPublishTitleCandidate EnsureLinkable(string variant, string title, string catalogTitle, string core, string token, string framing)
    {
        var score = PlutoYouTubeCatalogLinker.TitleSimilarity(title, catalogTitle);
        if (score >= PlutoYouTubeCatalogLinker.MinFuzzyScore) return new PlutoPublishTitleCandidate(variant, title, score) { Framing = framing };
        // 🔗 연결성 미달 → 카탈로그 제목 중심 안전 형태(훅 문구가 Dice 점수를 희석해 자동 연결이 끊기는 것을 방지)
        var safe = FitTitle(core, token);
        var safeScore = PlutoYouTubeCatalogLinker.TitleSimilarity(safe, catalogTitle);
        return safeScore > score
            ? new PlutoPublishTitleCandidate(variant, safe, safeScore) { Framing = PlutoTitleFramings.CatalogSafe }
            : new PlutoPublishTitleCandidate(variant, title, score) { Framing = framing };
    }

    /// <summary>해시태그 본문 — 문자/숫자/밑줄만(공백 · 기호 제거) · 최대 30자.</summary>
    public static string HashtagBody(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = raw.Trim();
        var colon = s.LastIndexOf(':');
        if (colon >= 0 && colon < s.Length - 1) s = s[(colon + 1)..];   // "style:xxx" 학습 키 접두 제거
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_') sb.Append(char.IsUpper(ch) ? char.ToLowerInvariant(ch) : ch);
            if (sb.Length >= 30) break;
        }
        return sb.ToString();
    }

    private static bool ContainsIgnoreCase(List<string> list, string v)
    {
        foreach (var x in list) if (string.Equals(x, v, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>오답 노트 차단 판정 — 원문 또는 해시태그 정규형이 차단 토큰과 일치.</summary>
    private static bool IsBlocked(string token, IReadOnlySet<string> suppressed)
    {
        if (suppressed.Count == 0) return false;
        if (suppressed.Contains(token.Trim())) return true;
        var body = HashtagBody(token);
        if (body.Length == 0) return false;
        foreach (var s in suppressed) if (string.Equals(HashtagBody(s), body, StringComparison.Ordinal)) return true;
        return false;
    }

    private IReadOnlySet<string> SafeSuppressed()
    {
        try
        {
            if (_notebook?.GetSuppressedTokens() is { } set)
            {
                var copy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in set) if (!string.IsNullOrWhiteSpace(s)) copy.Add(s.Trim());
                return copy;
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.PublishPack] notebook: " + ex.Message); }
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private string? TopRankedHook(IReadOnlySet<string> suppressed)
    {
        try
        {
            if (_ranker is null) return null;
            foreach (var m in _ranker.GetParetoModalityStatus())
                if (m.Kind == PlutoPatternKind.Hook && m.Leader is { } l && !IsBlocked(l.Label, suppressed)) return l.Label;
            foreach (var o in _ranker.RankOptions(PlutoPatternKind.Hook))
                if (!o.IsPenalized && !IsBlocked(o.DisplayLabel, suppressed)) return o.DisplayLabel;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.PublishPack] hook rank: " + ex.Message); }
        return null;
    }

    private List<string> ParetoTagLeaders()
    {
        var list = new List<string>(1);
        try
        {
            if (_ranker is null) return list;
            foreach (var m in _ranker.GetParetoModalityStatus())
                if (m.Kind == PlutoPatternKind.StyleTag && m.Leader is { } l && !l.Label.Contains(' ', StringComparison.Ordinal)) list.Add(l.Label);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.PublishPack] pareto: " + ex.Message); }
        return list;
    }

    private IReadOnlyList<string> RankTagsSafe(List<string> candidates)
    {
        var dedup = new List<string>(candidates.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
        {
            var t = Clean(c);
            if (t.Length > 0 && seen.Add(HashtagBody(t))) dedup.Add(t);
        }
        try
        {
            if (_ranker is not null)
            {
                var ranked = _ranker.RankTags(dedup, dedup.Count);
                if (ranked.Count > 0)
                {
                    // 랭커는 학습 라벨로 돌려줄 수 있음 → 순위만 취하고 누락 후보는 원래 순서로 뒤에 붙임
                    var result = new List<string>(dedup.Count);
                    var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var r in ranked) if (used.Add(HashtagBody(r))) result.Add(r);
                    foreach (var d in dedup) if (used.Add(HashtagBody(d))) result.Add(d);
                    return result;
                }
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.PublishPack] tag rank: " + ex.Message); }
        return dedup;
    }

    private PlutoSeriesContinuityDirective SafeDirective(string theme, PlutoPublishPackRequest request)
    {
        if (_arc is null || !request.IsSeries) return PlutoSeriesContinuityDirective.None;
        try { return _arc.GetContinuityDirective(theme, request.TotalEpisodes, request.EpisodeNumber) ?? PlutoSeriesContinuityDirective.None; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.PublishPack] arc: " + ex.Message); return PlutoSeriesContinuityDirective.None; }
    }

    private static string BuildDescription(PlutoPublishPackRequest r, string title, string hookPhrase, PlutoSeriesContinuityDirective d, List<string> hashtags, bool korean)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(400);
        sb.Append(hookPhrase).Append('\n');   // 첫 줄 = 훅(검색 · 피드 미리보기 노출)
        sb.Append(title).Append("\n\n");
        if (r.IsSeries)
        {
            if (korean)
            {
                sb.Append("📺 시리즈 ").Append(r.EpisodeNumber.ToString(inv)).Append('/').Append(r.TotalEpisodes.ToString(inv)).Append("편");
                if (r.EpisodeNumber > 1) sb.Append(" — ").Append((r.EpisodeNumber - 1).ToString(inv)).Append("편에서 이어집니다");
                sb.Append('\n');
                if (r.EpisodeNumber < r.TotalEpisodes)
                    sb.Append("▶ 다음 ").Append((r.EpisodeNumber + 1).ToString(inv)).Append("편: ").Append(d.CliffhangerOutroLine.Length > 0 ? d.CliffhangerOutroLine : "이야기는 계속됩니다").Append('\n');
                else sb.Append("🏁 시리즈 완결편\n");
            }
            else
            {
                sb.Append("📺 Series part ").Append(r.EpisodeNumber.ToString(inv)).Append(" of ").Append(r.TotalEpisodes.ToString(inv)).Append('\n');
                if (r.EpisodeNumber < r.TotalEpisodes)
                    sb.Append("▶ Next: part ").Append((r.EpisodeNumber + 1).ToString(inv)).Append(d.CliffhangerOutroLine.Length > 0 ? " — " + d.CliffhangerOutroLine : "").Append('\n');
                else sb.Append("🏁 Series finale\n");
            }
            sb.Append('\n');
        }
        sb.Append(korean ? "👍 좋아요 · 🔔 구독으로 다음 영상을 받아보세요" : "👍 Like & 🔔 subscribe for the next one").Append("\n\n");
        sb.Append(string.Join(" ", hashtags));
        return sb.ToString();
    }

    private static string BuildPinnedComment(PlutoPublishPackRequest r, bool korean)
    {
        var inv = CultureInfo.InvariantCulture;
        if (r.IsSeries && r.EpisodeNumber < r.TotalEpisodes)
            return korean
                ? "📌 " + (r.EpisodeNumber + 1).ToString(inv) + "편이 곧 올라옵니다 — 구독 + 🔔 알림 켜고 놓치지 마세요! 이번 편 몇 점인가요? 👇"
                : "📌 Part " + (r.EpisodeNumber + 1).ToString(inv) + " drops soon — subscribe + 🔔 so you don't miss it! Rate this part 👇";
        if (r.IsSeries)
            return korean ? "📌 시리즈 완결! 다음 시리즈 주제를 댓글로 추천해 주세요 👇" : "📌 Series complete! Suggest the next series in the comments 👇";
        return korean ? "📌 끝까지 본 분들, 가장 좋았던 장면을 댓글로 알려주세요 👇" : "📌 Made it to the end? Tell me your favorite moment 👇";
    }
}
