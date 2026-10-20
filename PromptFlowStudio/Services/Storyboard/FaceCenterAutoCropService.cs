using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;   // ProcessRunner / CliPathResolver

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>1:1 오토 크롭 결과. Cropped=false 면 원본 그대로 사용(ffmpeg 부재/실패 · Graceful).</summary>
public sealed record FaceCenterCropResult(bool Ok, bool Cropped, string? Path, int Size, string Message);

/// <summary>
/// [PromptFlowStudio] 오프라인 아기 얼굴 스마트 정중앙 크롭 엔진 — 순수 FFmpeg CLI(외부 ML/NuGet 0 · <see cref="ProcessRunner"/> 공유).
///   ▸ I2V 레퍼런스 업로드 시 <b>정사각(1:1) 세이프존 중심</b>으로 크롭해 안면/피사체를 프레임 중앙에 맞춘다 → Wan I2V 인체/안면 왜곡 최소화.
///   ▸ <b>무모델 휴리스틱</b>: 가로/세로 중 짧은 변으로 정사각 커버 크롭 · X 중앙 · Y 상단 편향(아기 얼굴은 상단-중앙 경향, 기본 0.35) → size×size 리샘플.
///   ▸ Graceful: ffmpeg 부재/실패/취소 시 <see cref="FaceCenterCropResult.Cropped"/>=false 로 원본 경로를 그대로 돌려준다(T2V/원본 I2V 흐름 유지).
///   ⚠ 필터그래프 내부 콤마(<c>min(iw,ih)</c>)는 <c>\,</c> 로 이스케이프(필터 구분자 오인 방지).
/// </summary>
public sealed class FaceCenterAutoCropService(LumaFaceDetector detector)
{
    private const int DefaultSize = 768;
    private const double DefaultYBias = 0.35;   // 얼굴 탐지 실패 시 폴백(상단-중앙 편향)

    /// <summary>
    /// 이미지를 1:1 정중앙 크롭해 <c>{name}_1x1.png</c> 로 저장한다. <paramref name="yBias"/>=null 이면 <see cref="LumaFaceDetector"/>로
    ///   피부톤/휘도 무게중심 Y를 <b>동적 추론</b>해 얼굴을 정중앙에 맞춘다(어떤 위치의 사진이든). 탐지 실패는 0.35 폴백. 크롭 실패/부재는 원본 유지(Graceful).
    /// </summary>
    public async Task<FaceCenterCropResult> CropSquareAsync(string imagePath, int size = DefaultSize, double? yBias = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            return new(false, false, imagePath, size, "이미지를 찾을 수 없습니다.");

        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null) return new(true, false, imagePath, size, "ffmpeg 미탐지 — 원본을 그대로 사용합니다(크롭 생략).");

        try
        {
            var (effectiveYBias, method) = yBias is double fixedBias
                ? (Math.Clamp(fixedBias, 0, 1), "fixed")
                : await ResolveYBiasAsync(imagePath, ct).ConfigureAwait(false);   // 가변 얼굴 탐지

            var dir = Path.GetDirectoryName(imagePath) ?? Path.GetTempPath();
            var outPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(imagePath) + "_1x1.png");
            var res = await ProcessRunner.RunAsync(ffmpeg, BuildCropArgs(imagePath, outPath, size, effectiveYBias), workingDir: null, onLine: null, ct: ct).ConfigureAwait(false);

            if (res.Ok && File.Exists(outPath))
                return new(true, true, outPath, Math.Clamp(size, 128, 2048), $"1:1 정중앙 오토 크롭 완료(얼굴 탐지: {method})");
            return new(true, false, imagePath, size, "크롭 실패 — 원본을 사용합니다: " + Tail(res.StdErr));
        }
        catch (OperationCanceledException) { return new(true, false, imagePath, size, "취소 — 원본을 사용합니다."); }
        catch (Exception ex) { Program.Log("FaceCenterAutoCrop", ex); return new(true, false, imagePath, size, "크롭 실패 — 원본을 사용합니다: " + ex.Message); }
    }

    /// <summary>피부톤/휘도 무게중심으로 동적 Y-Bias 를 산출([0.15,0.6] 클램프). 탐지 실패는 0.35 폴백.</summary>
    private async Task<(double YBias, string Method)> ResolveYBiasAsync(string imagePath, CancellationToken ct)
    {
        try
        {
            var d = await detector.DetectAsync(imagePath, ct).ConfigureAwait(false);
            if (d.Ok) return (Math.Clamp(d.CenterYNorm, 0.15, 0.6), d.Method);
        }
        catch (Exception ex) { Program.Log("FaceCenterAutoCrop.Detect", ex); }
        return (DefaultYBias, "fallback");
    }

    /// <summary>정사각 커버 크롭(X 중앙 · Y 편향) + size 리샘플 인자. ⚠ 필터 내부 콤마는 <c>\,</c> 이스케이프.</summary>
    public static IReadOnlyList<string> BuildCropArgs(string input, string output, int size, double yBias)
    {
        var s = Math.Clamp(size <= 0 ? DefaultSize : size, 128, 2048);
        var yb = Math.Clamp(yBias, 0.0, 1.0).ToString("0.###", CultureInfo.InvariantCulture);
        var vf = $"crop=min(iw\\,ih):min(iw\\,ih):(iw-min(iw\\,ih))/2:(ih-min(iw\\,ih))*{yb},scale={s}:{s}:flags=lanczos";
        return new List<string> { "-y", "-i", input, "-vf", vf, "-frames:v", "1", output };
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

    private static string Tail(string? s, int n = 300) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s.Trim() : "…" + s[^n..].Trim());
}
