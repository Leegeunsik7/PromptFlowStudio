using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Account;   // LicenseService.DataAreaRoot()
using PromptFlowStudio.Services.Engines;   // ProcessRunner / CliPathResolver

namespace PromptFlowStudio.Services.Mobile;

/// <summary>런타임 네이티브 권한 종류.</summary>
public enum MobilePermission { Camera, Microphone, Storage }

/// <summary>캡처 종류(사진/영상/오디오).</summary>
public enum CaptureKind { Photo, Video, Audio }

/// <summary>권한 상태.</summary>
public sealed record PermissionState(MobilePermission Permission, bool Granted);

/// <summary>캡처 결과 — 성공/종류/로컬 경로/메시지.</summary>
public sealed record CaptureResult(bool Ok, CaptureKind Kind, string? Path, string Message);

/// <summary>플랫폼 정보 — 이름/모바일 여부/카메라·FFmpegKit 가용성/ffmpeg 백엔드.</summary>
public sealed record PlatformInfo(string Platform, bool IsMobile, bool HasCamera, bool HasFFmpegKit, string FfmpegBackend);

/// <summary>
/// [PromptFlowStudio] v4.0.0 모바일 네이티브 브릿지 시임(Task 2) — 플랫폼 독립 인터페이스(외부 유료 SDK 0).
///   ▸ 데스크톱(net8.0-windows)은 <see cref="DesktopPlatformBridge"/>(ProcessRunner+ffmpeg.exe), 안드로이드(MAUI)는
///     동일 인터페이스를 <c>MediaPicker/Permissions/FFmpegKit</c>로 구현(모바일 프로젝트) → <b>UI/서비스 로직 무변경 재사용</b>.
///   ▸ FFmpeg 명령 체인은 <see cref="MobileFfmpegCommand"/>가 <b>플랫폼 공통 인자</b>로 조립 → 데스크톱 CLI 와 FFmpegKit 이 동일 구동.
/// </summary>
public interface IMobilePlatformBridge
{
    /// <summary>현재 플랫폼 정보.</summary>
    PlatformInfo Info { get; }
    /// <summary>런타임 권한을 요청/확인한다(데스크톱은 항상 true).</summary>
    Task<bool> EnsurePermissionAsync(MobilePermission permission, CancellationToken ct = default);
    /// <summary>카메라/마이크/갤러리에서 캡처한다(데스크톱은 미지원 · Graceful).</summary>
    Task<CaptureResult> CaptureAsync(CaptureKind kind, CancellationToken ct = default);
    /// <summary>FFmpeg 명령을 실행한다(데스크톱=ffmpeg.exe · 모바일=FFmpegKit). onProgress 는 0~1.</summary>
    Task<bool> RunFfmpegAsync(IReadOnlyList<string> args, double totalSeconds = 0, Action<double>? onProgress = null, CancellationToken ct = default);
    /// <summary>플랫폼 저장 루트 기준 절대 경로.</summary>
    string ResolveStoragePath(string relative);
}

/// <summary>
/// [PromptFlowStudio] FFmpegKit/CLI 공통 명령 빌더(순수 BCL · 테스트 가능).
///   데스크톱은 인자 배열을 그대로 ffmpeg.exe 에 전달하고, FFmpegKit 은 <see cref="ToCommandLine"/>로 단일 문자열을 execute 한다.
/// </summary>
public static class MobileFfmpegCommand
{
    /// <summary>기본 트랜스코드(H.264 · yuv420p · faststart).</summary>
    public static IReadOnlyList<string> BuildTranscode(string input, string output, int crf = 20) => new[]
    {
        "-y", "-i", input, "-c:v", "libx264", "-preset", "veryfast", "-crf",
        Math.Clamp(crf, 0, 51).ToString(CultureInfo.InvariantCulture),
        "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", output,
    };

    /// <summary>캡처 원본을 지정 길이 클립으로 트림.</summary>
    public static IReadOnlyList<string> BuildCaptureToClip(string input, string output, int seconds) => new[]
    {
        "-y", "-i", input, "-t", Math.Max(1, seconds).ToString(CultureInfo.InvariantCulture),
        "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p", "-movflags", "+faststart", output,
    };

    /// <summary>인자 배열 → FFmpegKit.execute 용 단일 명령 문자열(공백 포함 인자는 따옴표).</summary>
    public static string ToCommandLine(IReadOnlyList<string> args)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < args.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            var a = args[i] ?? "";
            if (a.Length == 0 || a.IndexOf(' ') >= 0) sb.Append('"').Append(a.Replace("\"", "\\\"")).Append('"');
            else sb.Append(a);
        }
        return sb.ToString();
    }

    /// <summary>FFmpeg 진행 라인("time=HH:MM:SS.xx")에서 초를 파싱한다(없으면 -1).</summary>
    public static double ParseFfmpegTimeSeconds(string? line)
    {
        if (string.IsNullOrEmpty(line)) return -1;
        var idx = line.IndexOf("time=", StringComparison.Ordinal);
        if (idx < 0) return -1;
        var tail = line.Substring(idx + 5).TrimStart();
        var tok = new string(tail.TakeWhile(c => c == ':' || c == '.' || char.IsDigit(c)).ToArray());
        var parts = tok.Split(':');
        try
        {
            if (parts.Length == 3
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)
                && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
                return h * 3600 + m * 60 + s;
        }
        catch { }
        return -1;
    }
}

/// <summary>
/// [PromptFlowStudio] 데스크톱(net8.0-windows) 플랫폼 브릿지 — ffmpeg.exe(ProcessRunner) 사용.
///   카메라/권한은 데스크톱 폴백(파일 선택 안내). 모바일에서는 MAUI 프로젝트의 Android 브릿지가 이 인터페이스를 대체 구현한다.
/// </summary>
public sealed class DesktopPlatformBridge : IMobilePlatformBridge
{
    public PlatformInfo Info { get; } = new("Windows Desktop", IsMobile: false, HasCamera: false, HasFFmpegKit: false, FfmpegBackend: "ffmpeg.exe (CLI)");

    public Task<bool> EnsurePermissionAsync(MobilePermission permission, CancellationToken ct = default) => Task.FromResult(true);

    public Task<CaptureResult> CaptureAsync(CaptureKind kind, CancellationToken ct = default)
        => Task.FromResult(new CaptureResult(false, kind, null, "데스크톱에서는 카메라 캡처 대신 파일 선택을 사용하세요(모바일 전용)."));

    public async Task<bool> RunFfmpegAsync(IReadOnlyList<string> args, double totalSeconds = 0, Action<double>? onProgress = null, CancellationToken ct = default)
    {
        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null || args is null || args.Count == 0) return false;
        try
        {
            void OnLine(bool _, string line)
            {
                if (onProgress is null || totalSeconds <= 0) return;
                var sec = MobileFfmpegCommand.ParseFfmpegTimeSeconds(line);
                if (sec >= 0) onProgress(Math.Clamp(sec / totalSeconds, 0, 1));
            }
            var res = await ProcessRunner.RunAsync(ffmpeg, args, workingDir: null, onLine: OnLine, ct: ct).ConfigureAwait(false);
            return res.Ok;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { Program.Log("DesktopBridge.RunFfmpeg", ex); return false; }
    }

    public string ResolveStoragePath(string relative)
    {
        try
        {
            var root = LicenseService.DataAreaRoot();
            var full = Path.Combine(root, relative ?? "");
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            return full;
        }
        catch { return relative ?? ""; }
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
