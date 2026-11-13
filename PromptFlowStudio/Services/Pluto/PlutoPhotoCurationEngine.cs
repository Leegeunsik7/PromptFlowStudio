using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🧹 큐레이션 제외 사유(선택 컷은 <see cref="None"/>).</summary>
public enum PlutoCurationRejectionReason
{
    None = 0,
    BlurryOutOfFocus = 1,
    PoorExposure = 2,
    DuplicateBurstShot = 3,
    UnreadableFile = 4,
    LowerRankCut = 5,
}

/// <summary>📸 사진 1장 평가 결과 — 선택 시 <see cref="NarrativeActOrder"/> = 몽타주 순번(1부터) · 미선택 0.</summary>
public sealed record PlutoCuratedPhotoCandidate(
    string ImagePath, int OriginalIndex, bool IsSelected, int NarrativeActOrder, string NarrativeRoleLabel,
    double CompositeScore, double SharpnessScore, double ExposureScore, double SaliencyScore,
    ulong PerceptualDHash, (double X, double Y) FocalPoint, PlutoCurationRejectionReason RejectionReason);

/// <summary>📦 배치 큐레이션 결과 — <see cref="SelectedCuts"/> 는 3막 서사 순서 · <see cref="AllEvaluated"/> 는 입력 순서.</summary>
public sealed record PlutoBatchCurationResult(
    IReadOnlyList<PlutoCuratedPhotoCandidate> SelectedCuts, IReadOnlyList<PlutoCuratedPhotoCandidate> AllEvaluated,
    int TotalInputCount, int BlurryRejectedCount, int ExposureRejectedCount, int DuplicateRejectedCount);

/// <summary>✨ 연사 배치 → 흔들림/노출/유사 연사 제거 → 베스트 K컷 → 3막 서사 배열.</summary>
public interface IPlutoPhotoCurationEngine
{
    Task<PlutoBatchCurationResult> CurateBatchAsync(IReadOnlyList<string> imagePaths, int targetCutCount = 4, PlutoVisionStorySynthesizer? visionSynthesizer = null, CancellationToken ct = default);

    /// <summary>64비트 dHash(9×8 Rec.709 · bit(y·8+x) = L[x,y] &gt; L[x+1,y]) — 판독 불가 → 경로 해시.</summary>
    ulong ComputePerceptualDHash(string imagePath);

    /// <summary>초점 가중(반경 0.35 · 2배) 라플라시안 분산 → [0,1] — 판독 불가 → 0.65.</summary>
    double ComputeLaplacianSharpness(string imagePath, (double X, double Y) focalPoint);
}

/// <summary>
/// ✨ Pluto 배치 사진 큐레이터 — ≤128×128 썸네일 1회 디코드로 선명도(초점 가중 ∇²Y 분산) · 돌출도 · dHash 산출,
/// <see cref="IPlutoImageColorAnalyzer"/> 노출 게이트 · <see cref="IPlutoImageFocalDetector"/> 초점을 결합.
///   ▸ Composite = 0.45·Sharp + 0.30·Saliency + 0.25·Exposure · 탐욕 NMS(Hamming ≤ 8) 연사 중복 제거.
///   ▸ 각 게이트는 최소 컷 수 구제([부분 구제]) — 전량 흐림/중복이어도 가능한 한 K컷 보존.
///   ▸ Vision 설정 시 상위 후보 표정 보정(동시 3 · 사진당 4초 · 실패 → 로컬 유지).
/// </summary>
public sealed class PlutoPhotoCurationEngine : IPlutoPhotoCurationEngine
{
    public static readonly PlutoPhotoCurationEngine Shared = new();

    public const int MaxSampleSize = 128;
    public const int MaxBatchSize = 30;
    public const int DefaultCutCount = 4;
    public const double FallbackSharpness = 0.65, FallbackSaliency = 0.5;
    public const double SharpnessNormalizer = 0.045, BlurThreshold = 0.18;
    public const double FocalRadius = 0.35, FocalWeight = 2.0;
    public const double MinLuminance = 0.14, MaxLuminance = 0.92;
    public const double SharpWeight = 0.45, SaliencyWeight = 0.30, ExposureWeight = 0.25;
    public const int DuplicateHammingThreshold = 8;
    public const int VisionConcurrency = 3;
    public static readonly TimeSpan VisionTimeout = TimeSpan.FromSeconds(4);

    public const string Act1Label = "Act 1 · 첫 1.5초 후킹";
    public const string Act2Label = "Act 2 · 리듬 전개";
    public const string Act3Label = "Act 3 · 감동 클라이맥스";

    private readonly IPlutoImageColorAnalyzer _color;
    private readonly IPlutoImageFocalDetector _focal;

    public PlutoPhotoCurationEngine(IPlutoImageColorAnalyzer? colorAnalyzer = null, IPlutoImageFocalDetector? focalDetector = null)
    {
        _color = colorAnalyzer ?? PlutoImageColorAnalyzer.Shared;
        _focal = focalDetector ?? PlutoImageFocalDetector.Shared;
    }

    /// <summary>🏷 사유 → 한국어 라벨(UI).</summary>
    public static string ReasonLabel(PlutoCurationRejectionReason r) => r switch
    {
        PlutoCurationRejectionReason.BlurryOutOfFocus => "흔들림/초점 흐림",
        PlutoCurationRejectionReason.PoorExposure => "노출 불량",
        PlutoCurationRejectionReason.DuplicateBurstShot => "유사 연사 중복",
        PlutoCurationRejectionReason.UnreadableFile => "판독 불가",
        PlutoCurationRejectionReason.LowerRankCut => "순위 밖",
        _ => "선택",
    };

    // ── 픽셀 로드 ──

    /// <summary>≤128×128 ARGB 썸네일(판독 불가 · 비 Windows → null).</summary>
    private static int[]? TryLoadPixels(string imagePath, out int width, out int height)
    {
        width = height = 0;
#if WINDOWS
        if (string.IsNullOrWhiteSpace(imagePath) || !System.IO.File.Exists(imagePath)) return null;
        try
        {
            using var src = new System.Drawing.Bitmap(imagePath);
            int w = Math.Max(1, src.Width), h = Math.Max(1, src.Height);
            var scale = Math.Min(1.0, (double)MaxSampleSize / Math.Max(w, h));
            int sw = Math.Max(1, (int)Math.Round(w * scale)), sh = Math.Max(1, (int)Math.Round(h * scale));
            using var thumb = new System.Drawing.Bitmap(sw, sh, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(thumb))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                g.DrawImage(src, 0, 0, sw, sh);
            }
            var data = thumb.LockBits(new System.Drawing.Rectangle(0, 0, sw, sh), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var px = new int[sw * sh];
                for (var y = 0; y < sh; y++)
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, px, y * sw, sw);
                width = sw;
                height = sh;
                return px;
            }
            finally { thumb.UnlockBits(data); }
        }
        catch (Exception ex) when (ex is ArgumentException or System.IO.IOException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.Curation] " + ex.Message);   // [부분 구제] 중립 폴백
            return null;
        }
#else
        _ = imagePath;
        return null;
#endif
    }

    private static double Luma(int p) => (0.2126 * ((p >> 16) & 0xFF) + 0.7152 * ((p >> 8) & 0xFF) + 0.0722 * (p & 0xFF)) / 255.0;

    /// <summary>결정론 경로 해시(FNV-1a 64) — 판독 불가 사진끼리 중복 판정되지 않도록 고유값.</summary>
    public static ulong PathHash(string? path)
    {
        var h = 14695981039346656037UL;
        if (path is null) return h;
        for (var i = 0; i < path.Length; i++)
        {
            h ^= path[i];
            h *= 1099511628211UL;
        }
        return h;
    }

    // ── 공개 헬퍼 ──

    public ulong ComputePerceptualDHash(string imagePath)
    {
        var px = TryLoadPixels(imagePath, out var w, out var h);
        return px is null ? PathHash(imagePath) : DHashFromPixels(px, w, h);
    }

    public double ComputeLaplacianSharpness(string imagePath, (double X, double Y) focalPoint)
    {
        var px = TryLoadPixels(imagePath, out var w, out var h);
        return px is null ? FallbackSharpness : SharpnessFromPixels(px, w, h, focalPoint);
    }

    /// <summary>🧮 ∇²Y = Y(x−1)+Y(x+1)+Y(y−1)+Y(y+1)−4Y 내부 픽셀 가중 분산(초점 반경 0.35 → 2배) / 0.045 → [0,1].</summary>
    public static double SharpnessFromPixels(ReadOnlySpan<int> argb, int width, int height, (double X, double Y) focal)
    {
        if (width < 3 || height < 3 || argb.Length < width * height) return FallbackSharpness;
        double sw = 0, s1 = 0, s2 = 0;
        double invW = 1.0 / (width - 1), invH = 1.0 / (height - 1);
        const double r2 = FocalRadius * FocalRadius;
        for (var y = 1; y < height - 1; y++)
        {
            var row = y * width;
            var dy = y * invH - focal.Y;
            for (var x = 1; x < width - 1; x++)
            {
                var lap = Luma(argb[row + x - 1]) + Luma(argb[row + x + 1]) + Luma(argb[row - width + x]) + Luma(argb[row + width + x]) - 4.0 * Luma(argb[row + x]);
                var dx = x * invW - focal.X;
                var wt = dx * dx + dy * dy <= r2 ? FocalWeight : 1.0;
                sw += wt;
                s1 += wt * lap;
                s2 += wt * lap * lap;
            }
        }
        if (!(sw > 0)) return FallbackSharpness;
        var mean = s1 / sw;
        var variance = Math.Max(0, s2 / sw - mean * mean);
        return Math.Clamp(variance / SharpnessNormalizer, 0.0, 1.0);
    }

    /// <summary>🧮 9×8 박스 평균 Rec.709 휘도 → 64비트 수평 차분 해시(무할당 · stackalloc).</summary>
    public static ulong DHashFromPixels(ReadOnlySpan<int> argb, int width, int height)
    {
        if (width <= 0 || height <= 0 || argb.Length < width * height) return 0;
        Span<double> sum = stackalloc double[72];
        Span<int> cnt = stackalloc int[72];
        sum.Clear();
        cnt.Clear();
        for (var y = 0; y < height; y++)
        {
            var gy = Math.Min(7, y * 8 / height);
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var gx = Math.Min(8, x * 9 / width);
                var k = gy * 9 + gx;
                sum[k] += Luma(argb[row + x]);
                cnt[k]++;
            }
        }
        // 9 열보다 좁은 썸네일 → 빈 셀은 좌측 이웃 값으로 채움.
        Span<double> lum = stackalloc double[72];
        for (var gy = 0; gy < 8; gy++)
        {
            var last = 0.0;
            for (var gx = 0; gx < 9; gx++)
            {
                var k = gy * 9 + gx;
                if (cnt[k] > 0) last = sum[k] / cnt[k];
                lum[k] = last;
            }
        }
        ulong hash = 0;
        for (var gy = 0; gy < 8; gy++)
            for (var gx = 0; gx < 8; gx++)
                if (lum[gy * 9 + gx] > lum[gy * 9 + gx + 1]) hash |= 1UL << (gy * 8 + gx);
        return hash;
    }

    /// <summary>🧮 피사체 돌출도 = 0.40·피부톤 비율 + 0.35·초점 영역 그래디언트 대비 + 0.25·중앙성 → [0,1].</summary>
    public static double SaliencyFromPixels(ReadOnlySpan<int> argb, int width, int height, (double X, double Y) focal)
    {
        if (width < 3 || height < 3 || argb.Length < width * height) return FallbackSaliency;
        int skin = 0, nearCount = 0;
        double gradAll = 0, gradNear = 0;
        double invW = 1.0 / (width - 1), invH = 1.0 / (height - 1);
        const double r2 = FocalRadius * FocalRadius;
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            var dy = y * invH - focal.Y;
            for (var x = 0; x < width; x++)
            {
                var p = argb[row + x];
                int r = (p >> 16) & 0xFF, g = (p >> 8) & 0xFF, b = p & 0xFF;
                if (r > 95 && g > 40 && b > 20 && r > g && r > b && Math.Abs(r - g) > 15) skin++;
                var gx = x < width - 1 ? Math.Abs(Luma(argb[row + x + 1]) - Luma(p)) : 0.0;
                var gy = y < height - 1 ? Math.Abs(Luma(argb[row + width + x]) - Luma(p)) : 0.0;
                var gr = gx + gy;
                gradAll += gr;
                var dx = x * invW - focal.X;
                if (dx * dx + dy * dy <= r2)
                {
                    gradNear += gr;
                    nearCount++;
                }
            }
        }
        var n = (double)(width * height);
        var skinScore = Math.Clamp(skin / n / 0.15, 0.0, 1.0);
        var meanAll = gradAll / n;
        var meanNear = nearCount > 0 ? gradNear / nearCount : meanAll;
        var focusScore = meanAll > 1e-9 ? Math.Clamp((meanNear / meanAll - 0.6) / 1.0, 0.0, 1.0) : 0.0;
        double cdx = focal.X - 0.5, cdy = focal.Y - 0.45;
        var centrality = 1.0 - Math.Clamp(Math.Sqrt(cdx * cdx + cdy * cdy) / 0.45, 0.0, 1.0);
        return Math.Clamp(0.40 * skinScore + 0.35 * focusScore + 0.25 * centrality, 0.0, 1.0);
    }

    /// <summary>🧮 노출 점수 = 0.7·(목표 휘도 0.54 근접) + 0.3·(대비 0.22 근접).</summary>
    public static double ExposureScoreFrom(PlutoImageColorMetrics m)
    {
        var lum = 1.0 - Math.Clamp(Math.Abs(m.MeanLuminance - PlutoImageColorAnalyzer.TargetLuminance) / PlutoImageColorAnalyzer.TargetLuminance, 0.0, 1.0);
        var contrast = 1.0 - Math.Clamp(Math.Abs(m.ContrastStdDev - PlutoImageColorAnalyzer.TargetContrast) / PlutoImageColorAnalyzer.TargetContrast, 0.0, 1.0);
        return Math.Clamp(0.7 * lum + 0.3 * contrast, 0.0, 1.0);
    }

    public static double Composite(double sharp, double saliency, double exposure)
        => SharpWeight * sharp + SaliencyWeight * saliency + ExposureWeight * exposure;

    // ── 배치 큐레이션 ──

    private sealed class Eval
    {
        public string Path = "";
        public int Index;
        public bool Readable = true;
        public double Sharp, Exposure, Saliency, Luminance;
        public ulong Hash;
        public (double X, double Y) Focal;
        public PlutoCurationRejectionReason Reason;
        public double Score => Composite(Sharp, Saliency, Exposure);
        public double HookScore => 0.6 * Saliency + 0.4 * Sharp;
    }

    private Eval Evaluate(string path, int index)
    {
        var e = new Eval { Path = path, Index = index };
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            e.Readable = false;
            e.Reason = PlutoCurationRejectionReason.UnreadableFile;
            e.Focal = PlutoImageFocalDetector.Fallback;
            e.Hash = PathHash(path);
            e.Sharp = 0;
            e.Saliency = 0;
            e.Exposure = 0;
            return e;
        }
        try { e.Focal = PlutoImageFocalDetector.ClampFocal(_focal.DetectLocalFocalPoint(path)); }
        catch (Exception ex) when (ex is not OperationCanceledException) { System.Diagnostics.Debug.WriteLine("[Pluto.Curation.Focal] " + ex.Message); e.Focal = PlutoImageFocalDetector.Fallback; }
        PlutoImageColorMetrics m;
        try { m = _color.AnalyzeImage(path) ?? PlutoImageColorMetrics.Neutral; }
        catch (Exception ex) when (ex is not OperationCanceledException) { System.Diagnostics.Debug.WriteLine("[Pluto.Curation.Color] " + ex.Message); m = PlutoImageColorMetrics.Neutral; }
        e.Luminance = m.MeanLuminance;
        e.Exposure = ExposureScoreFrom(m);
        var px = TryLoadPixels(path, out var w, out var h);
        if (px is null)
        {
            // 디코드 불가(예: GDI+ 미지원 포맷) — 렌더 경로는 FFmpeg 로 가능하므로 중립값으로 후보 유지.
            e.Sharp = FallbackSharpness;
            e.Saliency = FallbackSaliency;
            e.Hash = PathHash(path);
        }
        else
        {
            e.Sharp = SharpnessFromPixels(px, w, h, e.Focal);
            e.Saliency = SaliencyFromPixels(px, w, h, e.Focal);
            e.Hash = DHashFromPixels(px, w, h);
        }
        return e;
    }

    public async Task<PlutoBatchCurationResult> CurateBatchAsync(IReadOnlyList<string> imagePaths, int targetCutCount = DefaultCutCount, PlutoVisionStorySynthesizer? visionSynthesizer = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var total = imagePaths?.Count ?? 0;
        if (imagePaths is null || total == 0)
            return new PlutoBatchCurationResult(Array.Empty<PlutoCuratedPhotoCandidate>(), Array.Empty<PlutoCuratedPhotoCandidate>(), 0, 0, 0, 0);
        var k = Math.Clamp(targetCutCount, PlutoMontageCompiler.MinClips, PlutoMontageCompiler.MaxClips);
        var paths = new string[total];
        for (var i = 0; i < total; i++) paths[i] = imagePaths[i] ?? "";

        // ① ⚡ 로컬 평가(백그라운드)
        var evals = await Task.Run(() =>
        {
            var r = new Eval[paths.Length];
            for (var i = 0; i < r.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                r[i] = Evaluate(paths[i], i);
            }
            return r;
        }, ct).ConfigureAwait(false);

        var readable = 0;
        for (var i = 0; i < evals.Length; i++) if (evals[i].Readable) readable++;
        var need = Math.Min(k, readable);

        // ② 흔들림 게이트(+ 최소 컷 구제: 선명도 높은 순)
        var alive = 0;
        for (var i = 0; i < evals.Length; i++)
        {
            var e = evals[i];
            if (!e.Readable) continue;
            if (e.Sharp < BlurThreshold) e.Reason = PlutoCurationRejectionReason.BlurryOutOfFocus;
            else alive++;
        }
        alive = Rescue(evals, PlutoCurationRejectionReason.BlurryOutOfFocus, alive, need, static e => e.Sharp);

        // ③ 노출 게이트(+ 구제: 노출 점수 높은 순)
        for (var i = 0; i < evals.Length; i++)
        {
            var e = evals[i];
            if (!e.Readable || e.Reason != PlutoCurationRejectionReason.None) continue;
            if (e.Luminance < MinLuminance || e.Luminance > MaxLuminance)
            {
                e.Reason = PlutoCurationRejectionReason.PoorExposure;
                alive--;
            }
        }
        alive = Rescue(evals, PlutoCurationRejectionReason.PoorExposure, alive, need, static e => e.Exposure);

        // ④ 👁 Vision 표정 보정(상위 2K 생존 후보 · 설정 시만)
        if (visionSynthesizer is not null && visionSynthesizer.IsVisionConfigured)
        {
            var top = SortedSurvivors(evals);
            var n = Math.Min(top.Count, 2 * k);
            using var gate = new SemaphoreSlim(VisionConcurrency, VisionConcurrency);
            var tasks = new Task[n];
            for (var i = 0; i < n; i++) tasks[i] = RefineExpressionAsync(visionSynthesizer, gate, top[i], ct);
            await Task.WhenAll(tasks).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }

        // ⑤ dHash 탐욕 NMS(Composite 내림차순) + 구제
        var ranked = SortedSurvivors(evals);
        var accepted = new List<Eval>(ranked.Count);
        var dups = new List<Eval>();
        for (var i = 0; i < ranked.Count; i++)
        {
            var c = ranked[i];
            var dup = false;
            for (var j = 0; j < accepted.Count; j++)
                if (BitOperations.PopCount(c.Hash ^ accepted[j].Hash) <= DuplicateHammingThreshold) { dup = true; break; }
            if (dup) { c.Reason = PlutoCurationRejectionReason.DuplicateBurstShot; dups.Add(c); }
            else accepted.Add(c);
        }
        for (var i = 0; i < dups.Count && accepted.Count < need; i++)   // dups 는 이미 Composite 내림차순
        {
            dups[i].Reason = PlutoCurationRejectionReason.None;
            accepted.Add(dups[i]);
        }
        accepted.Sort(static (a, b) => b.Score.CompareTo(a.Score));

        // ⑥ 상위 K → 순위 밖 표기 → 3막 배열
        var picks = new List<Eval>(Math.Min(k, accepted.Count));
        for (var i = 0; i < accepted.Count; i++)
        {
            if (picks.Count < k) picks.Add(accepted[i]);
            else accepted[i].Reason = PlutoCurationRejectionReason.LowerRankCut;
        }
        var ordered = SequenceThreeActs(picks);

        var order = new int[evals.Length];
        var labels = new string[evals.Length];
        for (var i = 0; i < ordered.Length; i++)
        {
            order[ordered[i].Index] = i + 1;
            labels[ordered[i].Index] = ActLabel(i, ordered.Length);
        }
        var all = new PlutoCuratedPhotoCandidate[evals.Length];
        int blurry = 0, exposure = 0, duplicate = 0;
        for (var i = 0; i < evals.Length; i++)
        {
            var e = evals[i];
            switch (e.Reason)
            {
                case PlutoCurationRejectionReason.BlurryOutOfFocus: blurry++; break;
                case PlutoCurationRejectionReason.PoorExposure: exposure++; break;
                case PlutoCurationRejectionReason.DuplicateBurstShot: duplicate++; break;
            }
            all[i] = ToCandidate(e, order[i], labels[i] ?? "");
        }
        var selected = new PlutoCuratedPhotoCandidate[ordered.Length];
        for (var i = 0; i < selected.Length; i++) selected[i] = all[ordered[i].Index];
        return new PlutoBatchCurationResult(selected, all, total, blurry, exposure, duplicate);
    }

    private static PlutoCuratedPhotoCandidate ToCandidate(Eval e, int order, string label)
        => new(e.Path, e.Index, order > 0, order, label, e.Score, e.Sharp, e.Exposure, e.Saliency, e.Hash, e.Focal, order > 0 ? PlutoCurationRejectionReason.None : e.Reason);

    /// <summary>[부분 구제] 생존 수 &lt; need → 해당 사유 탈락 후보를 key 내림차순으로 복원.</summary>
    private static int Rescue(Eval[] evals, PlutoCurationRejectionReason reason, int alive, int need, Func<Eval, double> key)
    {
        while (alive < need)
        {
            Eval? best = null;
            for (var i = 0; i < evals.Length; i++)
            {
                var e = evals[i];
                if (e.Reason == reason && (best is null || key(e) > key(best))) best = e;
            }
            if (best is null) break;
            best.Reason = PlutoCurationRejectionReason.None;
            alive++;
        }
        return alive;
    }

    private static List<Eval> SortedSurvivors(Eval[] evals)
    {
        var list = new List<Eval>(evals.Length);
        for (var i = 0; i < evals.Length; i++)
            if (evals[i].Readable && evals[i].Reason == PlutoCurationRejectionReason.None) list.Add(evals[i]);
        list.Sort(static (a, b) =>
        {
            var c = b.Score.CompareTo(a.Score);
            return c != 0 ? c : a.Index.CompareTo(b.Index);
        });
        return list;
    }

    private static string ActLabel(int position, int count)
        => position == 0 ? Act1Label : position == count - 1 ? Act3Label : Act2Label;

    /// <summary>🎬 Act 1 = max(0.6·Sal + 0.4·Sharp) · Act 3 = 잔여 max Composite · Act 2 = Σ|ΔX| 최대 순열(≤3장 전수).</summary>
    private static Eval[] SequenceThreeActs(List<Eval> picks)
    {
        var n = picks.Count;
        if (n == 0) return Array.Empty<Eval>();
        var pool = new List<Eval>(picks);
        var hook = 0;
        for (var i = 1; i < pool.Count; i++) if (pool[i].HookScore > pool[hook].HookScore) hook = i;
        var act1 = pool[hook];
        pool.RemoveAt(hook);
        if (pool.Count == 0) return new[] { act1 };
        var climax = 0;
        for (var i = 1; i < pool.Count; i++) if (pool[i].Score > pool[climax].Score) climax = i;
        var act3 = pool[climax];
        pool.RemoveAt(climax);

        var mid = pool.ToArray();
        var best = (Eval[])mid.Clone();
        var bestCost = -1.0;
        Permute(mid, 0, act1, act3, ref best, ref bestCost);
        var result = new Eval[n];
        result[0] = act1;
        for (var i = 0; i < best.Length; i++) result[i + 1] = best[i];
        result[n - 1] = act3;
        return result;
    }

    private static void Permute(Eval[] a, int start, Eval first, Eval last, ref Eval[] best, ref double bestCost)
    {
        if (start >= a.Length)
        {
            var prev = first.Focal.X;
            var cost = 0.0;
            for (var i = 0; i < a.Length; i++) { cost += Math.Abs(a[i].Focal.X - prev); prev = a[i].Focal.X; }
            cost += Math.Abs(last.Focal.X - prev);
            if (cost > bestCost + 1e-12) { bestCost = cost; best = (Eval[])a.Clone(); }
            return;
        }
        for (var i = start; i < a.Length; i++)
        {
            (a[start], a[i]) = (a[i], a[start]);
            Permute(a, start + 1, first, last, ref best, ref bestCost);
            (a[start], a[i]) = (a[i], a[start]);
        }
    }

    /// <summary>👁 Vision 표정 보정 — 웃음/축하/놀이 +0.15 · 수면 +0.05 · 표정 판독 +0.05(실패/시간 초과 → 로컬 유지).</summary>
    private static async Task RefineExpressionAsync(PlutoVisionStorySynthesizer synth, SemaphoreSlim gate, Eval e, CancellationToken ct)
    {
        try { await gate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(VisionTimeout);
            var plan = await synth.SynthesizeFromImageAsync(e.Path, null, cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
            if (plan.Source != PlutoVisionSource.Vision) return;
            var a = plan.Attributes;
            var bonus = a.Kind switch
            {
                PlutoVisionMoodKind.Smile or PlutoVisionMoodKind.Celebration or PlutoVisionMoodKind.Play => 0.15,
                PlutoVisionMoodKind.Sleep => 0.05,
                _ => 0.0,
            };
            if (!string.IsNullOrWhiteSpace(a.Expression)) bonus += 0.05;
            e.Saliency = Math.Clamp(e.Saliency + bonus, 0.0, 1.0);
            e.Focal = PlutoImageFocalDetector.ClampFocal(plan.DetectedFocalPoint);
        }
        catch (OperationCanceledException) { /* 시간 초과/취소 → 로컬 유지 */ }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.Curation.Vision] " + ex.Message);   // [부분 구제] 로컬 유지
        }
        finally { gate.Release(); }
    }
}
