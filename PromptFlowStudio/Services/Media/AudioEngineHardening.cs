using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;   // ProcessRunner / CliPathResolver

namespace PromptFlowStudio.Services.Media;

/// <summary>공간 음향 적용 결과 — 출력 영상 생성 여부 · 음향 필터 실제 적용 여부 · 사유.</summary>
public sealed record SpatialApplyResult(bool OutputProduced, bool FilterApplied, string Message);

/// <summary>오디오 파일 재생/믹싱 준비 상태 — 준비 여부 · 바이트 · 사유.</summary>
public sealed record AudioReadyResult(bool Ready, long Bytes, string Message);

/// <summary>
/// [PromptFlowStudio] v4.4.3 Task3 · 3D 감정 보이스 &amp; 서라운드 음향 FFmpeg 고립 차단 하드닝 — 순수 BCL + FFmpeg CLI(외부 NuGet 0).
///   ▸ <b>필터 비정상 종료 흡수</b>: 바이노럴 <c>pan</c>/<c>sofalizer</c> 실행이 잘못된 파라미터/SOFA 로 Exit Code 1 로 죽어도
///     예외/오류를 흡수하고 <b>음향 필터만 스킵</b>한 뒤 <b>원음 패스스루</b>로 출력 영상을 <b>반드시 완성</b>한다(<see cref="ApplySpatialOrPassthroughAsync"/>).
///   ▸ <b>sofalizer 안전 강등</b>: HRIR SOFA 파일이 없거나 0바이트면 sofalizer 를 아예 만들지 않고(<see cref="BuildSofalizerOrNull"/>) 등파워 <c>pan</c> 으로 강등,
///     그마저 실패하면 원음으로 스킵 — 3단 폴백.
///   ▸ <b>TTS I/O 무한대기 차단</b>: 나레이션 WAV/MP3 가 스트림 생성 중 잠기거나(FileLock) 0바이트일 때, <see cref="TimeSpan"/> 예산 안에서만
///     공유 읽기로 열어 실제 1바이트를 검증하고(<see cref="EnsureAudioReadyAsync"/>) 초과 시 즉시 반환 — 절대 무한 대기하지 않는다.
/// </summary>
public sealed class AudioEngineHardening
{
    /// <summary>FFmpeg 필터/패스스루 프로세스 상한 — 이 시간 안에 끝나지 않으면 트리 종료(ProcessRunner 취소).</summary>
    public TimeSpan ProcessTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>TTS 오디오 준비 검증 기본 예산.</summary>
    public static readonly TimeSpan DefaultReadyTimeout = TimeSpan.FromSeconds(10);

    // ══════════════════════ 공간 음향 적용(실패 시 원음 패스스루) ══════════════════════

    /// <summary>
    /// 입력 클립에 3D 공간 음향(sofalizer 우선 → 등파워 pan)을 적용한다. 어떤 실패(Exit 1·예외·ffmpeg 부재)에도
    /// <paramref name="outPath"/> 는 <b>원음 패스스루로 반드시 생성</b>되어 영상 렌더가 중단되지 않는다.
    /// </summary>
    /// <param name="sofaPath">HRIR SOFA 파일 경로(있고 유효할 때만 sofalizer 사용). null/부재/0바이트면 pan 으로 강등.</param>
    public async Task<SpatialApplyResult> ApplySpatialOrPassthroughAsync(
        string inputClip, string outPath, SpatialPoint point, string? sofaPath = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(inputClip) || !File.Exists(inputClip))
            return new SpatialApplyResult(false, false, "입력 클립을 찾을 수 없습니다: " + inputClip);

        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null)
            return await PassthroughAsync(inputClip, outPath, "ffmpeg 미설치 — 음향 필터 스킵(영상 유지)", ct).ConfigureAwait(false);

        // 1순위: 유효한 SOFA 있으면 sofalizer, 없으면 등파워 pan.
        var sofalizer = BuildSofalizerOrNull(sofaPath, point);
        var primary = sofalizer ?? SpatialAudioEngine.BuildSpatialChain(point);

        if (await TryFilterAsync(ffmpeg, inputClip, outPath, primary, ct).ConfigureAwait(false))
            return new SpatialApplyResult(true, true, sofalizer is null ? "등파워 pan 3D 음향 적용" : "sofalizer 바이노럴 적용");

        if (ct.IsCancellationRequested)
            return new SpatialApplyResult(File.Exists(outPath), false, "취소됨");

        // 2순위: sofalizer 가 죽었으면(Exit 1 등) 등파워 pan 으로 1회 강등 재시도.
        if (sofalizer is not null)
        {
            var pan = SpatialAudioEngine.BuildSpatialChain(point);
            if (await TryFilterAsync(ffmpeg, inputClip, outPath, pan, ct).ConfigureAwait(false))
                return new SpatialApplyResult(true, true, "sofalizer 실패 → 등파워 pan 으로 적용");
            if (ct.IsCancellationRequested)
                return new SpatialApplyResult(File.Exists(outPath), false, "취소됨");
        }

        // 3순위: 필터 전부 실패 → 원음 패스스루(영상은 반드시 완성).
        return await PassthroughAsync(inputClip, outPath, "음향 필터 실패 — 원음으로 스킵(영상 렌더 완수)", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 유효한 HRIR SOFA 파일이 있을 때만 <c>sofalizer</c> 필터 문자열을 만든다. 경로가 비었거나 파일이 없거나 0바이트면
    /// <c>null</c> 을 반환해 호출부가 <c>pan</c> 으로 강등하게 한다(순수 · 테스트 대상).
    /// </summary>
    public static string? BuildSofalizerOrNull(string? sofaPath, SpatialPoint point)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sofaPath)) return null;
            var fi = new FileInfo(sofaPath);
            if (!fi.Exists || fi.Length == 0) return null;   // 부재/0바이트 → sofalizer 미사용
            // ffmpeg 필터 문법상 경로의 백슬래시/콜론을 이스케이프한다.
            var esc = sofaPath.Replace("\\", "/").Replace(":", "\\:");
            var az = ((point.Azimuth % 360) + 360) % 360;    // 0..360 방위각 정규화
            return $"sofalizer=sofa='{esc}':type=freq:radius=0.15:rotation={F(az)}";
        }
        catch { return null; }
    }

    private async Task<bool> TryFilterAsync(string ffmpeg, string inp, string outPath, string filter, CancellationToken ct)
    {
        try
        {
            var dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var args = new List<string>
            {
                "-y", "-i", inp, "-af", filter,
                "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", outPath,
            };

            using var opCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            opCts.CancelAfter(ProcessTimeout);   // ⏱ 필터가 무응답이어도 상한에서 트리 종료.
            var res = await ProcessRunner.RunAsync(ffmpeg, args, workingDir: null, onLine: null, ct: opCts.Token).ConfigureAwait(false);

            // Exit Code 1(잘못된 필터/파라미터) 등 비정상 종료·0바이트 산출물은 실패로 흡수하고 잔여물 제거.
            if (res.Ok && File.Exists(outPath) && new FileInfo(outPath).Length > 0) return true;
            SafeDelete(outPath);
            return false;
        }
        catch (Exception ex)
        {
            Program.Log("AudioEngineHardening.Filter", ex);
            SafeDelete(outPath);
            return false;
        }
    }

    private async Task<SpatialApplyResult> PassthroughAsync(string inp, string outPath, string why, CancellationToken ct)
    {
        try
        {
            var dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // ① ffmpeg 있으면 무필터 재먹싱(-c copy) — 컨테이너/faststart 정상 보존.
            var ffmpeg = ResolveFfmpeg();
            if (ffmpeg is not null)
            {
                var args = new List<string> { "-y", "-i", inp, "-c", "copy", "-movflags", "+faststart", outPath };
                using var opCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                opCts.CancelAfter(ProcessTimeout);
                var res = await ProcessRunner.RunAsync(ffmpeg, args, workingDir: null, onLine: null, ct: opCts.Token).ConfigureAwait(false);
                if (res.Ok && File.Exists(outPath) && new FileInfo(outPath).Length > 0)
                    return new SpatialApplyResult(true, false, why);
                SafeDelete(outPath);
            }

            // ② 최후 폴백: 원본 바이트 복사 — ffmpeg 부재/재먹싱 실패에도 outPath 는 반드시 생성.
            var samePath = string.Equals(Path.GetFullPath(inp), Path.GetFullPath(outPath), StringComparison.OrdinalIgnoreCase);
            if (!samePath) File.Copy(inp, outPath, overwrite: true);
            return new SpatialApplyResult(File.Exists(outPath), false, why);
        }
        catch (Exception ex)
        {
            Program.Log("AudioEngineHardening.Passthrough", ex);
            // 복사조차 실패 — 입력 원본은 그대로 남아 있어 호출부가 계속 사용 가능(파이프라인 중단 없음).
            return new SpatialApplyResult(false, false, why + " · 패스스루 실패: " + ex.Message);
        }
    }

    // ══════════════════════ TTS WAV/MP3 I/O 무한대기 가드 ══════════════════════

    /// <summary>
    /// 나레이션 오디오가 재생/믹싱 가능한 상태인지 검증한다 — 존재·0바이트 아님·잠금 해제·실제 1바이트 읽힘.
    /// <paramref name="timeout"/>(기본 <see cref="DefaultReadyTimeout"/>) 예산 안에서만 재시도하며, 초과하면 즉시 실패로 반환한다(무한 대기 금지).
    /// </summary>
    public async Task<AudioReadyResult> EnsureAudioReadyAsync(string? path, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return new AudioReadyResult(false, 0, "경로가 비어 있습니다.");
        var budget = timeout is { } t && t > TimeSpan.Zero ? t : DefaultReadyTimeout;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(budget);   // ⏱ 예산 초과 시 강제 취소 — 잠금이 안 풀려도 여기서 끝난다.

        while (true)
        {
            try
            {
                if (File.Exists(path))
                {
                    var len = new FileInfo(path).Length;
                    if (len > 0)
                    {
                        // 공유 읽기로 열어 실제 1바이트를 읽는다 — 다른 프로세스가 쓰기 잠금 중이면 IOException → 재시도.
                        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
                        var probe = new byte[1];
                        var read = await fs.ReadAsync(probe.AsMemory(0, 1), cts.Token).ConfigureAwait(false);
                        if (read > 0) return new AudioReadyResult(true, len, "재생 가능");
                    }
                    // len == 0: 스트림이 아직 쓰는 중일 수 있음 → 재시도.
                }
                // 파일 미생성: 생성 대기 → 재시도.
            }
            catch (OperationCanceledException)
            {
                return new AudioReadyResult(false, SafeLen(path), ct.IsCancellationRequested ? "취소됨" : "타임아웃 — 잠금/0바이트가 예산 내 해소되지 않음");
            }
            catch (IOException) { /* 잠금/공유 위반 → 짧게 대기 후 재시도 */ }
            catch (UnauthorizedAccessException) { /* 권한/공유 위반 → 재시도 */ }
            catch (Exception ex)
            {
                Program.Log("AudioEngineHardening.EnsureReady", ex);
                return new AudioReadyResult(false, SafeLen(path), "검증 실패: " + ex.Message);
            }

            try { await Task.Delay(150, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                return new AudioReadyResult(false, SafeLen(path), ct.IsCancellationRequested ? "취소됨" : "타임아웃 — 잠금/0바이트가 예산 내 해소되지 않음");
            }
        }
    }

    // ══════════════════════ 헬퍼 ══════════════════════

    private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    private static long SafeLen(string? path)
    {
        try { return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 무시 */ }
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
