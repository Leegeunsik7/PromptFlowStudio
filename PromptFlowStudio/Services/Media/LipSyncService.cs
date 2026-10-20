using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;

namespace PromptFlowStudio.Services.Media;

/// <summary>립싱크 결과. 실패 시 Ok=false + Message.</summary>
public sealed record LipSyncResult(bool Ok, string? OutputPath, string Message);

/// <summary>인물 입모양 동기화(립싱크) 계약 — LatentSync CLI 로 영상 속 입모양을 나레이션 오디오에 맞춘다.</summary>
public interface ILipSyncService
{
    bool IsAvailable { get; }
    /// <summary><paramref name="videoPath"/> 의 인물 입모양을 <paramref name="audioPath"/>(나레이션)에 맞춰 립싱크한 새 영상을 만든다. 실패는 Graceful.</summary>
    Task<LipSyncResult> ApplyAsync(string videoPath, string audioPath, string outputPath, IProgress<string>? status = null, CancellationToken ct = default);
}

/// <summary>
/// [PromptFlowStudio] 립싱크 — LatentSync(Apache 2.0) CLI 연동(외부 NuGet 0, 순수 BCL <see cref="ProcessRunner"/>).
///   ▸ 설정 <c>Engines:LatentSync:Exe</c>(보통 python.exe) + Args 템플릿(inference 스크립트/체크포인트 · {video}{audio}{out} 치환).
///   ▸ 딥러닝 추론이라 수 분 소요될 수 있음 — 표준출력/에러 비동기 드레인(데드락 차단), 취소 시 프로세스 트리 kill.
///   ▸ CLI/모델 부재·실패는 예외 없이 결과 record 로 원인 반환(Graceful) — 호출부는 원본 클립을 그대로 사용.
/// </summary>
public sealed class LipSyncService(EngineConfigService engines, IVideoMergeService merge) : ILipSyncService
{
    private const int TargetFps = 30;
    private string? Exe => engines.Resolve(EngineKeys.LatentSync);

    /// <inheritdoc/>
    public bool IsAvailable => Exe is not null;

    /// <inheritdoc/>
    public async Task<LipSyncResult> ApplyAsync(string videoPath, string audioPath, string outputPath, IProgress<string>? status = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath)) return new(false, null, "입력 영상을 찾을 수 없습니다.");
        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath)) return new(false, null, "나레이션 오디오를 찾을 수 없습니다.");
        var exe = Exe;
        if (exe is null) return new(false, null, "LatentSync CLI 가 설정되지 않았습니다(관리자 설정에서 경로 지정).");

        string? fpsTemp = null;
        try
        {
            var outDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

            // ── FPS 정규화 샌드박스 ── Wan 2.1 의 가변 프레임레이트(VFR)를 고정 30fps(CFR)로 전처리 → 립싱크 프레임 정렬 안정.
            status?.Report("FPS 정규화 중… (30fps)");
            fpsTemp = await NormalizeFpsAsync(videoPath, ct).ConfigureAwait(false);
            var srcVideo = fpsTemp ?? videoPath;   // 정규화 실패 시 원본으로 진행(Graceful)

            status?.Report("립싱크 처리 중… (LatentSync)");
            var template = engines.Args(EngineKeys.LatentSync);
            var args = ProcessRunner.Substitute(template, new Dictionary<string, string>
            {
                ["video"] = srcVideo, ["audio"] = audioPath, ["out"] = outputPath,
            });

            // 추론 스크립트는 보통 자체 작업 디렉터리(체크포인트 상대경로) 기준 → exe 가 파이썬이면 스크립트 폴더를 WD 로.
            var wd = Path.GetDirectoryName(exe);
            var r = await ProcessRunner.RunAsync(exe, args, workingDir: wd, ct: ct).ConfigureAwait(false);
            if (!r.Started) return new(false, null, "LatentSync 실행 실패.");
            return r.Ok && File.Exists(outputPath)
                ? new(true, outputPath, "립싱크 완료")
                : new(false, null, $"립싱크 실패: {Tail(r.StdErr)}");
        }
        catch (OperationCanceledException) { return new(false, null, "립싱크가 취소되었습니다."); }
        catch (Exception ex) { Program.Log("LipSync.Apply", ex); return new(false, null, "립싱크 실패: " + ex.Message); }
        finally { try { if (fpsTemp is not null && File.Exists(fpsTemp)) File.Delete(fpsTemp); } catch { /* 무시 */ } }
    }

    /// <summary>ffmpeg 로 입력 영상을 고정 30fps(CFR)로 재인코딩해 임시파일 경로 반환. ffmpeg 부재/실패는 null(원본 사용 폴백).</summary>
    private async Task<string?> NormalizeFpsAsync(string videoPath, CancellationToken ct)
    {
        var ffmpeg = merge.ResolvedFfmpegPath;
        if (ffmpeg is null) return null;
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "promptflow-lipsync");
            Directory.CreateDirectory(dir);
            var outp = Path.Combine(dir, $"cfr30_{Guid.NewGuid():N}.mp4");
            var r = await ProcessRunner.RunAsync(ffmpeg, new[]
            {
                "-y", "-i", videoPath,
                "-r", TargetFps.ToString(System.Globalization.CultureInfo.InvariantCulture),   // 고정 30 FPS(CFR)
                "-vsync", "cfr",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-preset", "veryfast", "-crf", "18",
                "-c:a", "aac", "-b:a", "192k",
                outp,
            }, ct: ct).ConfigureAwait(false);
            return r.Ok && File.Exists(outp) ? outp : null;
        }
        catch (Exception ex) { Program.Log("LipSync.NormalizeFps", ex); return null; }
    }

    private static string Tail(string s, int n = 400) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s.Trim() : "…" + s[^n..].Trim());
}
