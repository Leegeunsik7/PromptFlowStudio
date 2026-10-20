using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;      // ProcessRunner / CliPathResolver
using PromptFlowStudio.Services.Storyboard;   // StoryboardProject

namespace PromptFlowStudio.Services.Wan;

/// <summary>완 모델 티어 — 파라미터/정밀도/로컬무료 여부/최소 VRAM.</summary>
public sealed record WanModelTier(string Id, string DisplayName, double ParamsB, string Precision, bool LocalFree, int MinVramMb, string Note);

/// <summary>하이브리드 렌더 계획 — 모드/모델/화질보완 체인/fps/예상비용(원)/요약.</summary>
public sealed record HybridRenderPlan(string Mode, WanModelTier Model, string EnhanceChain, int Fps, int CostKrw, string Summary);

/// <summary>
/// [PromptFlowStudio] v3.8.0 알라바바 완(Alibaba Wan) 로컬/클라우드 무료 하이브리드 엔진(Task 1 · 순수 BCL + FFmpeg CLI).
///   ▸ <b>VRAM 탐지</b>(nvidia-smi)로 로컬 완 1.3B(FP8/GGUF)를 <b>0원 무료 1순위</b> 렌더로 선택(6GB=FP8, 8GB+=GGUF, 16GB+=14B).
///   ▸ 로컬 6GB 화질 한계를 <b>FFmpeg 후처리</b>(<c>minterpolate=fps=60</c> 프레임 보간 + <c>cas</c> 샤픈 + <c>eq</c> 고대비 그레이딩 + <c>unsharp</c>)로 보완해
///     <b>Wan 3.0급 디테일</b>을 무료로 완성.
///   ▸ 클라우드 업스케일(Wan 3.0)은 선택지로 제공(원격 · 정책상 유료 가능). nvidia-smi/ffmpeg 부재는 Graceful.
/// </summary>
public sealed class AlibabaWanHybridEngine
{
    /// <summary>완 모델 티어(로컬 무료 우선 + 클라우드 프리미엄).</summary>
    public static readonly IReadOnlyList<WanModelTier> Tiers = new[]
    {
        new WanModelTier("wan1.3b_fp8",  "완 1.3B · FP8 (로컬 · 0원)",   1.3, "FP8",     true, 4000,  "RTX 3060 6GB 최적 · 무료 무제한"),
        new WanModelTier("wan1.3b_gguf", "완 1.3B · GGUF-Q8 (로컬 · 0원)", 1.3, "GGUF-Q8", true, 8000,  "8GB+ 고화질 로컬 · 무료"),
        new WanModelTier("wan14b_local", "완 14B · FP16 (로컬 · 0원)",     14,  "FP16",    true, 16000, "16GB+ 로컬 프리미엄 · 무료"),
        new WanModelTier("wan30_cloud",  "완 3.0 · 클라우드 업스케일",     30,  "CLOUD",   false, 0,    "원격 프리미엄(정책상 유료 가능)"),
    };

    /// <summary>ID로 티어 조회(없으면 null).</summary>
    public static WanModelTier? TierFor(string? id) => Tiers.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    // ══════════════════════ VRAM 탐지 ══════════════════════

    /// <summary>nvidia-smi 출력(memory.total)에서 첫 정수 MB 를 파싱한다(없으면 0).</summary>
    public static int ParseNvidiaSmiVram(string? smiOutput)
    {
        if (string.IsNullOrWhiteSpace(smiOutput)) return 0;
        foreach (var raw in smiOutput.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var num = new string(line.TakeWhile(char.IsDigit).ToArray());
            if (num.Length > 0 && int.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb) && mb > 0) return mb;
        }
        return 0;
    }

    /// <summary>nvidia-smi 로 총 VRAM(MB)을 탐지한다(부재/실패 시 0 · Graceful).</summary>
    public async Task<int> DetectVramMbAsync(CancellationToken ct = default)
    {
        try
        {
            var exe = ResolveExe("nvidia-smi", "nvidia-smi.exe");
            if (exe is null) return 0;
            var args = new[] { "--query-gpu=memory.total", "--format=csv,noheader,nounits" };
            var res = await ProcessRunner.RunAsync(exe, args, workingDir: null, onLine: null, ct: ct).ConfigureAwait(false);
            return res.Ok ? ParseNvidiaSmiVram(res.StdOut) : 0;
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception ex) { Program.Log("WanHybrid.DetectVram", ex); return 0; }
    }

    /// <summary>VRAM(MB) → 권장 로컬 무료 티어(6GB=FP8 · 8GB+=GGUF · 16GB+=14B). 미탐지(0)도 FP8 로 오프로딩 시도.</summary>
    public static WanModelTier RecommendLocalTier(int vramMb)
    {
        if (vramMb >= 16000) return Tiers[2];
        if (vramMb >= 8000) return Tiers[1];
        return Tiers[0];   // 6GB/미탐지 → FP8(0원)
    }

    // ══════════════════════ Wan 3.0급 무료 화질 보완 체인 ══════════════════════

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// 로컬 저VRAM 결과를 Wan 3.0급으로 끌어올리는 FFmpeg 후처리 vf 체인.
    ///   minterpolate(모션보상 60fps 보간) → cas(콘텐츠 적응 샤픈) → eq(고대비/채도/감마) → unsharp(디테일 복원).
    /// </summary>
    public static string BuildEnhanceChain(int fps = 60, double sharp = 0.5, double contrast = 1.08, double saturation = 1.12, double gamma = 0.98)
    {
        var f = Math.Clamp(fps, 24, 120);
        var s = Math.Clamp(sharp, 0.0, 1.0);
        var c = Math.Clamp(contrast, 0.5, 2.0);
        var sat = Math.Clamp(saturation, 0.0, 3.0);
        var g = Math.Clamp(gamma, 0.1, 3.0);
        return $"minterpolate=fps={f}:mi_mode=mci:mc_mode=aobmc:me_mode=bidir:vsbmc=1," +
               $"cas=strength={F(s)}," +
               $"eq=contrast={F(c)}:saturation={F(sat)}:gamma={F(g)}," +
               $"unsharp=5:5:0.8:5:5:0.0";
    }

    /// <summary>모드별 예상 비용(원) — 로컬은 0원(무료), 클라우드는 컷당 근사 추정.</summary>
    public static int EstimateCostKrw(string mode, int cuts)
        => string.Equals(mode, "cloud_upscale", StringComparison.OrdinalIgnoreCase) ? Math.Max(0, cuts) * 320 : 0;

    /// <summary>프로젝트 + VRAM → 하이브리드 렌더 계획(로컬 0원 우선 · 화질 보완 체인 포함).</summary>
    public HybridRenderPlan BuildPlan(StoryboardProject project, int vramMb, bool preferCloud)
    {
        var mode = preferCloud ? "cloud_upscale" : "local";
        var model = preferCloud ? Tiers[3] : RecommendLocalTier(vramMb);
        // 로컬은 항상 화질 보완, 클라우드는 프로젝트 토글에 따름.
        var boost = !preferCloud || project.WanQualityBoost;
        var chain = boost ? BuildEnhanceChain(project.WanEnhanceFps) : "";
        var cost = EstimateCostKrw(mode, project.Scenes.Count);
        var summary = preferCloud
            ? $"클라우드 완 3.0 업스케일 · {project.Scenes.Count}컷 · 예상 {cost:N0}원"
            : $"로컬 {model.DisplayName} · {(boost ? $"완 3.0급 보완({project.WanEnhanceFps}fps)" : "원본")} · 0원 무료";
        return new HybridRenderPlan(mode, model, chain, project.WanEnhanceFps, cost, summary);
    }

    /// <summary>화질 보완 체인을 영상에 적용한다(체인 빈 값/ffmpeg 부재/실패는 false · 원본 유지 · Graceful).</summary>
    public async Task<bool> ApplyEnhanceAsync(string video, string outPath, string enhanceChain, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(video) || !File.Exists(video) || string.IsNullOrWhiteSpace(enhanceChain)) return false;
        var ffmpeg = ResolveExe("ffmpeg", "ffmpeg.exe");
        if (ffmpeg is null) return false;
        try
        {
            var dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var args = new List<string>
            {
                "-y", "-i", video, "-vf", enhanceChain,
                "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p",
                "-c:a", "copy", "-movflags", "+faststart", outPath,
            };
            var res = await ProcessRunner.RunAsync(ffmpeg, args, workingDir: null, onLine: null, ct: ct).ConfigureAwait(false);
            return res.Ok && File.Exists(outPath);
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { Program.Log("WanHybrid.ApplyEnhance", ex); return false; }
    }

    private static string? ResolveExe(string cmd, string exe)
    {
        try
        {
            var local = Path.Combine(AppContext.BaseDirectory, exe);
            if (File.Exists(local)) return local;
            return CliPathResolver.GetExecutablePath(cmd, exe);
        }
        catch { return null; }
    }
}
