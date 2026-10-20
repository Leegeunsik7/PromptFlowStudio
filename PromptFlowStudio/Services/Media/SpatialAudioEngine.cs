using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;      // ProcessRunner / CliPathResolver
using PromptFlowStudio.Services.Storyboard;   // StoryboardProject / StoryboardScene

namespace PromptFlowStudio.Services.Media;

/// <summary>카메라 모션 큐(오르빗 좌/우 · 근접 푸시인 · 원거리 풀아웃 · 고정).</summary>
public enum CameraCue { OrbitLeft, OrbitRight, PushIn, PullOut, Static }

/// <summary>컷 1개의 3D 공간 음향 좌표 — 팬(-1..1)·깊이(0근접~1원거리)·방위각(deg)·좌/우 등파워 게인.</summary>
public sealed record SpatialPoint(int Index, CameraCue Cue, double Pan, double Depth, double Azimuth, double LGain, double RGain);

/// <summary>
/// [PromptFlowStudio] v3.8.0 3D 바이노럴 카메라 연동 입체 음향 엔진(Task 3 · 순수 BCL → FFmpeg <c>pan</c>).
///   ▸ 스토리보드 컷의 <b>카메라 오르빗/패닝</b>(좌➔우, 근접➔원거리)을 프롬프트/전환에서 추론해 <b>오디오 좌표</b>로 환산.
///   ▸ <b>등파워(constant-power) 팬</b>(cos/sin)으로 좌우 정위, <b>원근 볼륨 감쇠</b>로 거리감 → 헤드폰/스피커 3D 공간감.
///   ▸ 아기 웃음·자동차 엔진음이 화면 움직임을 따라 흐르도록 FFmpeg <c>pan=stereo</c> + <c>volume</c> 체인을 동적 합성.
/// </summary>
public sealed class SpatialAudioEngine
{
    private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    // ══════════════════════ 카메라 큐 추론 ══════════════════════

    /// <summary>프롬프트/전환(xfade)에서 카메라 큐를 추론한다(좌우 정위 우선 → 원근).</summary>
    public static CameraCue DetectCameraCue(string? prompt, string? xfade)
    {
        var p = (prompt ?? "").ToLowerInvariant();
        var x = (xfade ?? "").ToLowerInvariant();
        bool Has(params string[] ks) { foreach (var k in ks) if (p.Contains(k)) return true; return false; }

        if (x.Contains("slideleft") || Has("orbit left", "pan left", "arc left", "left orbit", "왼")) return CameraCue.OrbitLeft;
        if (x.Contains("slideright") || Has("orbit right", "pan right", "arc right", "whip-pan", "whip pan", "오른")) return CameraCue.OrbitRight;
        if (Has("push-in", "push in", "dolly-in", "dolly in", "zoom in", "creeping dolly", "macro push")) return CameraCue.PushIn;
        if (Has("pull-out", "pull out", "pan-out", "pan out", "pull back", "pulls back", "crane bloom", "pull-back")) return CameraCue.PullOut;
        return CameraCue.Static;
    }

    /// <summary>큐 + 컷 위치 → 팬(-1 좌 ~ +1 우). 오르빗은 진행에 따라 좌우로 스윕.</summary>
    public static double PanForCue(CameraCue cue, int index, int total)
    {
        var sweep = total > 1 ? (index / (double)(total - 1) - 0.5) * 0.25 : 0.0;
        return cue switch
        {
            CameraCue.OrbitLeft => Math.Clamp(-0.6 - Math.Abs(sweep), -1.0, 1.0),
            CameraCue.OrbitRight => Math.Clamp(0.6 + Math.Abs(sweep), -1.0, 1.0),
            _ => 0.0,
        };
    }

    /// <summary>큐 → 깊이(0 근접 ~ 1 원거리).</summary>
    public static double DepthForCue(CameraCue cue) => cue switch
    {
        CameraCue.PushIn => 0.2,
        CameraCue.PullOut => 0.85,
        _ => 0.5,
    };

    // ══════════════════════ 등파워 팬 + 원근 볼륨 ══════════════════════

    /// <summary>팬(-1..1) → 좌/우 등파워 게인(L²+R²=1).</summary>
    public static (double L, double R) ConstantPowerGains(double pan)
    {
        var t = (Math.Clamp(pan, -1.0, 1.0) + 1.0) / 2.0;   // 0(좌)~1(우)
        return (Math.Cos(t * Math.PI / 2.0), Math.Sin(t * Math.PI / 2.0));
    }

    /// <summary>바이노럴 스테레오 정위 <c>pan</c> 필터.</summary>
    public static string BuildBinauralPan(double pan)
    {
        var (l, r) = ConstantPowerGains(pan);
        return $"pan=stereo|c0={F(l)}*c0|c1={F(r)}*c1";
    }

    /// <summary>원근 볼륨 감쇠(원거리일수록 작게).</summary>
    public static string BuildDepthVolume(double depth)
        => $"volume={F(1.0 - Math.Clamp(depth, 0.0, 1.0) * 0.5)}";

    /// <summary>컷 공간 좌표 → 정위 + 원근 결합 <c>-af</c> 체인.</summary>
    public static string BuildSpatialChain(SpatialPoint point)
        => BuildBinauralPan(point.Pan) + "," + BuildDepthVolume(point.Depth);

    // ══════════════════════ 프로젝트 분석 ══════════════════════

    /// <summary>컷 → 공간 좌표.</summary>
    public SpatialPoint PointFor(StoryboardScene scene, int index, int total)
    {
        var cue = DetectCameraCue(scene?.Prompt, scene?.EnterTransition?.XfadeName);
        var pan = PanForCue(cue, index, total);
        var depth = DepthForCue(cue);
        var (l, r) = ConstantPowerGains(pan);
        return new SpatialPoint(index, cue, pan, depth, pan * 90.0, l, r);
    }

    /// <summary>프로젝트 전 컷의 공간 음향 좌표(UI 캔버스/스펙 시트/클립 처리용).</summary>
    public IReadOnlyList<SpatialPoint> AnalyzeProject(StoryboardProject project)
    {
        var list = new List<SpatialPoint>();
        if (project is null) return list;
        var n = project.Scenes.Count;
        for (var i = 0; i < n; i++) list.Add(PointFor(project.Scenes[i], i, n));
        return list;
    }

    /// <summary>단일 클립(병합 전)에 공간 정위+원근을 적용한다. ffmpeg 부재/실패는 false(원음 유지 · Graceful).</summary>
    public async Task<bool> ApplySpatialClipAsync(string clip, string outPath, SpatialPoint point, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clip) || !File.Exists(clip)) return false;
        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null) return false;
        try
        {
            var dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var args = new List<string>
            {
                "-y", "-i", clip, "-af", BuildSpatialChain(point),
                "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", outPath,
            };
            var res = await ProcessRunner.RunAsync(ffmpeg, args, workingDir: null, onLine: null, ct: ct).ConfigureAwait(false);
            return res.Ok && File.Exists(outPath);
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { Program.Log("SpatialAudio.ApplyClip", ex); return false; }
    }

    private static string? ResolveFfmpeg()
    {
        try
        {
            var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(local)) return local;
            return CliPathResolver.GetExecutablePath("ffmpeg", "ffmpeg.exe");
        }
        catch { return null; }
    }
}
