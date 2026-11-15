using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>📚 시리즈 1편 — 컷(3~5 · 연대순 3막) · 몽타주 플랜 · "n/N편" 연속 3막 자막 · 편별 SNS 키트.</summary>
public sealed record PlutoSeriesEpisodePlan(int EpisodeNumber, int TotalEpisodes, string EpisodeTitle, IReadOnlyList<PlutoCuratedPhotoCandidate> Cuts, PlutoMontagePlan MontagePlan,
    IReadOnlyList<SubtitleCue> Subtitles, PlutoSnsPublishingKit PublishingKit);

/// <summary>📚 시리즈 분할 결과 — 편 간 <c>OriginalIndex</c> 중복 0.</summary>
public sealed record PlutoSeriesBatchResult(string SeriesTheme, IReadOnlyList<PlutoSeriesEpisodePlan> Episodes, int TotalInputPhotos, int TotalUsedUniquePhotos);

/// <summary>📚 Pluto 4.4 멀티 에피소드 시리즈 분할기.</summary>
public interface IPlutoSeriesBatchPlanner
{
    /// <summary>
    /// 10~30장 → 고품질 비중복 컷을 2~3편(편당 3~5컷)의 서로소 마이크로 에피소드로 분할.
    /// 고유 컷 부족 시 편 수 축소(최소 2편) · 2편도 불가 → <see cref="InvalidOperationException"/>. 자막/키트 실패 → 템플릿 · 취소만 전파.
    /// </summary>
    Task<PlutoSeriesBatchResult> BuildSeriesAsync(IReadOnlyList<string> photoPaths, string seriesTheme, int desiredEpisodes = 3, CancellationToken ct = default);
}

/// <summary>
/// 📚 <see cref="IPlutoSeriesBatchPlanner"/> 구현.
///   ▸ <see cref="IPlutoPhotoCurationEngine"/> 1회 평가 → 생존 풀(선택 + 순위 밖) Composite 내림차순 → 부족 시 흐림/노출 탈락 구제(연사 중복·판독 불가는 제외).
///   ▸ 상위 ≤ 5·N 컷을 <b>촬영(입력) 순서</b>로 정렬 → 연속 구간 분할(편 간 서사 연속) · 초점 [0.20, 0.80]² 클램프.
///   ▸ 편별 몽타주 컴파일 → 3막 자막 → 첫 큐 "n/N편 · " 접두 · 마지막 큐 "다음 편에 계속"/"완결" → SNS 키트 제목 "[n/N편]" 접두.
/// </summary>
public sealed class PlutoSeriesBatchPlanner : IPlutoSeriesBatchPlanner
{
    public const int MinEpisodes = 2, MaxEpisodes = 3;
    public const int MinCutsPerEpisode = 3, MaxCutsPerEpisode = 5;
    /// <summary>UI 노출 권장 최소 입력(장).</summary>
    public const int RecommendedMinPhotos = 10;
    public const int MaxInputPhotos = PlutoPhotoCurationEngine.MaxBatchSize;
    public const string ContinueSuffix = " (다음 편에 계속)";
    public const string FinaleSuffix = " (완결)";

    private readonly IPlutoPhotoCurationEngine _curation;
    private readonly IPlutoMontageCompiler _compiler;
    private readonly IPlutoMontageStoryGenerator? _story;
    private readonly IPlutoMultiFormatPackager? _packager;
    private readonly IPlutoMistakeNotebookService? _notebook;
    private readonly IPlutoAdaptivePatternRanker? _ranker;

    public PlutoSeriesBatchPlanner(IPlutoPhotoCurationEngine? curation = null, IPlutoMontageCompiler? compiler = null, IPlutoMontageStoryGenerator? story = null,
        IPlutoMultiFormatPackager? packager = null, IPlutoMistakeNotebookService? notebook = null, IPlutoAdaptivePatternRanker? ranker = null)
    {
        _curation = curation ?? PlutoPhotoCurationEngine.Shared;
        _compiler = compiler ?? PlutoMontageCompiler.Shared;
        _story = story;
        _packager = packager;
        _notebook = notebook;
        _ranker = ranker;
    }

    /// <summary>"n/N편" 표기.</summary>
    public static string PartTag(int n, int total) => n.ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture) + "편";

    /// <summary>고유 컷 수 → 실제 편 수(2~3 · 불가 → 0).</summary>
    public static int ResolveEpisodeCount(int uniqueCuts, int desired)
    {
        var n = Math.Clamp(desired, MinEpisodes, MaxEpisodes);
        while (n > MinEpisodes && uniqueCuts < n * MinCutsPerEpisode) n--;
        return uniqueCuts >= n * MinCutsPerEpisode ? n : 0;
    }

    /// <summary>편별 컷 수(앞 편부터 +1 배분 · 각 3~5).</summary>
    public static int[] DistributeCuts(int usable, int episodes)
    {
        var sizes = new int[episodes];
        var total = Math.Min(usable, episodes * MaxCutsPerEpisode);
        for (var i = 0; i < episodes; i++) sizes[i] = total / episodes + (i < total % episodes ? 1 : 0);
        return sizes;
    }

    public async Task<PlutoSeriesBatchResult> BuildSeriesAsync(IReadOnlyList<string> photoPaths, string seriesTheme, int desiredEpisodes = 3, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(photoPaths);
        ct.ThrowIfCancellationRequested();
        var theme = string.IsNullOrWhiteSpace(seriesTheme) ? "우리 가족 이야기" : seriesTheme.Trim();
        var count = Math.Min(photoPaths.Count, MaxInputPhotos);
        if (count < MinEpisodes * MinCutsPerEpisode)
            throw new InvalidOperationException("시리즈 분할에는 사진 " + (MinEpisodes * MinCutsPerEpisode).ToString(CultureInfo.InvariantCulture) + "장 이상이 필요합니다.");
        var paths = new string[count];
        for (var i = 0; i < count; i++) paths[i] = photoPaths[i] ?? "";

        // ① 1회 큐레이션 평가(흐림 · 노출 · dHash 연사)
        var curated = await _curation.CurateBatchAsync(paths, MaxCutsPerEpisode, null, ct).ConfigureAwait(false);
        var pool = BuildPool(curated.AllEvaluated, ResolveEpisodeCountTarget(desiredEpisodes) * MinCutsPerEpisode);
        var episodes = ResolveEpisodeCount(pool.Count, desiredEpisodes);
        if (episodes == 0)
            throw new InvalidOperationException("고유 컷이 부족합니다(" + pool.Count.ToString(CultureInfo.InvariantCulture) + "장 · 2편 분할에 6장 필요).");

        // ② 상위 컷 → 촬영 순서 → 연속 구간 분할
        var sizes = DistributeCuts(pool.Count, episodes);
        var used = 0;
        for (var i = 0; i < sizes.Length; i++) used += sizes[i];
        var picks = pool.GetRange(0, used);
        picks.Sort(static (a, b) => a.OriginalIndex.CompareTo(b.OriginalIndex));

        var suppressed = SafeSuppressed();
        var packager = _packager ?? new PlutoMultiFormatPackager(null, _notebook, null, _ranker);
        var result = new PlutoSeriesEpisodePlan[episodes];
        var offset = 0;
        for (var e = 0; e < episodes; e++)
        {
            ct.ThrowIfCancellationRequested();
            var cuts = new PlutoCuratedPhotoCandidate[sizes[e]];
            var cutPaths = new string[cuts.Length];
            var focals = new (double X, double Y)[cuts.Length];
            for (var i = 0; i < cuts.Length; i++)
            {
                var src = picks[offset + i];
                var focal = PlutoImageFocalDetector.ClampFocal(src.FocalPoint);
                cuts[i] = src with
                {
                    IsSelected = true, NarrativeActOrder = i + 1, NarrativeRoleLabel = ActLabel(i, cuts.Length),
                    FocalPoint = focal, RejectionReason = PlutoCurationRejectionReason.None,
                };
                cutPaths[i] = src.ImagePath;
                focals[i] = focal;
            }
            offset += cuts.Length;

            var plan = _compiler.CompileMontagePlan(cutPaths, focals, null, null);
            var number = e + 1;
            var subtitles = DecorateSubtitles(await BuildSubtitlesAsync(plan, cuts, theme, suppressed, ct).ConfigureAwait(false), number, episodes);
            var kit = await BuildKitAsync(packager, theme, number, episodes, subtitles, ct).ConfigureAwait(false);
            result[e] = new PlutoSeriesEpisodePlan(number, episodes, theme + " " + PartTag(number, episodes) + " — " + EpisodeArcName(number, episodes), cuts, plan, subtitles, kit);
        }
        return new PlutoSeriesBatchResult(theme, result, photoPaths.Count, used);
    }

    // ── 풀 ──

    private static int ResolveEpisodeCountTarget(int desired) => Math.Clamp(desired, MinEpisodes, MaxEpisodes);

    /// <summary>생존(선택 + 순위 밖) Composite 내림차순 → 부족분만 흐림/노출 탈락 구제 · 연사 중복 · 판독 불가 제외.</summary>
    internal static List<PlutoCuratedPhotoCandidate> BuildPool(IReadOnlyList<PlutoCuratedPhotoCandidate> all, int minimum)
    {
        var pool = new List<PlutoCuratedPhotoCandidate>(all.Count);
        var rescue = new List<PlutoCuratedPhotoCandidate>();
        var seen = new HashSet<int>();
        for (var i = 0; i < all.Count; i++)
        {
            var c = all[i];
            if (!seen.Add(c.OriginalIndex)) continue;
            if (c.IsSelected || c.RejectionReason is PlutoCurationRejectionReason.None or PlutoCurationRejectionReason.LowerRankCut) pool.Add(c);
            else if (c.RejectionReason is PlutoCurationRejectionReason.BlurryOutOfFocus or PlutoCurationRejectionReason.PoorExposure) rescue.Add(c);
        }
        pool.Sort(ByScore);
        if (pool.Count < minimum && rescue.Count > 0)   // [부분 구제]
        {
            rescue.Sort(ByScore);
            for (var i = 0; i < rescue.Count && pool.Count < minimum; i++) pool.Add(rescue[i]);
        }
        return pool;
    }

    private static int ByScore(PlutoCuratedPhotoCandidate a, PlutoCuratedPhotoCandidate b)
    {
        var c = b.CompositeScore.CompareTo(a.CompositeScore);
        return c != 0 ? c : a.OriginalIndex.CompareTo(b.OriginalIndex);
    }

    private static string ActLabel(int position, int count)
        => position == 0 ? PlutoPhotoCurationEngine.Act1Label : position == count - 1 ? PlutoPhotoCurationEngine.Act3Label : PlutoPhotoCurationEngine.Act2Label;

    private static string EpisodeArcName(int n, int total)
        => n == 1 ? "시작" : n == total ? "완결" : "전개";

    // ── 자막 · 키트 ──

    private async Task<IReadOnlyList<SubtitleCue>> BuildSubtitlesAsync(PlutoMontagePlan plan, IReadOnlyList<PlutoCuratedPhotoCandidate> cuts, string theme,
        IReadOnlySet<string>? suppressed, CancellationToken ct)
    {
        if (_story is not null)
        {
            try
            {
                var cues = await _story.GenerateThreeActSubtitlesAsync(plan, cuts, theme, ct).ConfigureAwait(false);
                if (cues is { Count: > 0 }) return cues;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Series] story fallback: " + ex.Message); }
        }
        return PlutoMontageStoryGenerator.GenerateLocal(plan, theme, suppressed, _ranker);
    }

    /// <summary>첫 큐 "n/N편 · " 접두(EN "Part n/N · ") · 마지막 큐 계속/완결 접미 — 구간 불변.</summary>
    public static IReadOnlyList<SubtitleCue> DecorateSubtitles(IReadOnlyList<SubtitleCue> cues, int n, int total)
    {
        if (cues.Count == 0) return cues;
        var arr = new SubtitleCue[cues.Count];
        for (var i = 0; i < arr.Length; i++) arr[i] = cues[i];
        var tag = PartTag(n, total);
        var partEn = "Part " + n.ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture);
        arr[0] = arr[0] with
        {
            Text = tag + " · " + arr[0].Text,
            SecondaryText = string.IsNullOrEmpty(arr[0].SecondaryText) ? arr[0].SecondaryText : partEn + " · " + arr[0].SecondaryText,
        };
        var last = arr.Length - 1;
        arr[last] = arr[last] with
        {
            Text = arr[last].Text + (n < total ? ContinueSuffix : FinaleSuffix),
            SecondaryText = string.IsNullOrEmpty(arr[last].SecondaryText) ? arr[last].SecondaryText : arr[last].SecondaryText + (n < total ? " (to be continued)" : " (finale)"),
        };
        return arr;
    }

    private static async Task<PlutoSnsPublishingKit> BuildKitAsync(IPlutoMultiFormatPackager packager, string theme, int n, int total, IReadOnlyList<SubtitleCue> subtitles,
        CancellationToken ct)
    {
        var tag = PartTag(n, total);
        PlutoSnsPublishingKit kit;
        try { kit = await packager.GeneratePublishingKitAsync(theme + " " + tag, new[] { theme, "시리즈", tag }, subtitles, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.Series] kit fallback: " + ex.Message);   // [부분 구제] 최소 키트
            kit = new PlutoSnsPublishingKit(new[] { theme }, theme, "", new[] { "#시리즈" }, "19:00–21:00");
        }
        var titles = new string[kit.Titles.Count];
        for (var i = 0; i < titles.Length; i++)
            titles[i] = kit.Titles[i].Contains(tag, StringComparison.Ordinal) ? kit.Titles[i] : "[" + tag + "] " + kit.Titles[i];
        var next = n < total ? "\n\n▶ 다음 편: " + PartTag(n + 1, total) : "\n\n🎬 시리즈 완결 — 1편부터 정주행!";
        return kit with { Titles = titles, CaptionKo = kit.CaptionKo + next };
    }

    private IReadOnlySet<string>? SafeSuppressed()
    {
        try { return _notebook?.GetSuppressedTokens(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Series] suppressed: " + ex.Message); return null; }
    }
}
