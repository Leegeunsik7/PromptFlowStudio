using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;   // Permissions
using Microsoft.Maui.Media;              // MediaPicker
using Microsoft.Maui.Storage;            // FileSystem
using PromptFlowStudio.Services.Mobile;  // IMobilePlatformBridge / MobileFfmpegCommand (기존 프로젝트 재사용)

namespace PromptFlowStudio.Mobile.Services;

/// <summary>
/// [PromptFlow Studio v4.0.0] 안드로이드 플랫폼 브릿지 — 데스크톱 시임의 모바일 구현(Task 2).
///   ▸ 카메라/마이크/스토리지 권한을 MAUI <see cref="Permissions"/>/<see cref="MediaPicker"/>로 연결.
///   ▸ FFmpeg 명령 체인은 <see cref="MobileFfmpegCommand"/>가 조립한 공통 인자를 <b>FFmpegKit</b>(네이티브 ARM64)로 실행.
///   기존 .razor/서비스는 <see cref="IMobilePlatformBridge"/>만 통해 플랫폼에 접근하므로 로직 무변경 재사용.
/// </summary>
public sealed class AndroidPlatformBridge : IMobilePlatformBridge
{
    public PlatformInfo Info { get; } = new("Android (Galaxy S26 Ultra)", IsMobile: true, HasCamera: true, HasFFmpegKit: false, FfmpegBackend: "FFmpeg.AutoGen (bindings · CLI exec 미구현)");

    public async Task<bool> EnsurePermissionAsync(MobilePermission permission, CancellationToken ct = default) => permission switch
    {
        MobilePermission.Camera => await Ensure<Permissions.Camera>(),
        MobilePermission.Microphone => await Ensure<Permissions.Microphone>(),
        _ => await Ensure<Permissions.StorageWrite>(),
    };

    private static async Task<bool> Ensure<T>() where T : Permissions.BasePermission, new()
    {
        var status = await Permissions.CheckStatusAsync<T>();
        if (status != PermissionStatus.Granted) status = await Permissions.RequestAsync<T>();
        return status == PermissionStatus.Granted;
    }

    public async Task<CaptureResult> CaptureAsync(CaptureKind kind, CancellationToken ct = default)
    {
        try
        {
            switch (kind)
            {
                case CaptureKind.Photo:
                    if (!await EnsurePermissionAsync(MobilePermission.Camera, ct)) return new(false, kind, null, "카메라 권한 거부");
                    if (!MediaPicker.Default.IsCaptureSupported) return new(false, kind, null, "카메라 미지원");
                    var photo = await MediaPicker.Default.CapturePhotoAsync();
                    return photo is null ? new(false, kind, null, "취소됨") : new(true, kind, await CopyIn(photo, ct), "촬영 완료");

                case CaptureKind.Video:
                    if (!await EnsurePermissionAsync(MobilePermission.Camera, ct)) return new(false, kind, null, "카메라 권한 거부");
                    var video = await MediaPicker.Default.CaptureVideoAsync();
                    return video is null ? new(false, kind, null, "취소됨") : new(true, kind, await CopyIn(video, ct), "영상 캡처 완료");

                default: // Audio — Voice AI Director 오프라인 NLU 용 마이크 확보
                    return new(await EnsurePermissionAsync(MobilePermission.Microphone, ct), kind, null, "마이크 준비 완료");
            }
        }
        catch (Exception ex) { return new(false, kind, null, ex.Message); }
    }

    private async Task<string> CopyIn(FileResult file, CancellationToken ct)
    {
        var dest = ResolveStoragePath($"capture/{file.FileName}");
        using var src = await file.OpenReadAsync();
        using var fs = File.Create(dest);
        await src.CopyToAsync(fs, ct);
        return dest;
    }

    public Task<bool> RunFfmpegAsync(IReadOnlyList<string> args, double totalSeconds = 0, Action<double>? onProgress = null, CancellationToken ct = default)
    {
        // v5.1.1: FFmpegKit(Laerdal.FFmpeg) 제거 — 커맨드라인 실행 백엔드 없음.
        //   FFmpeg.AutoGen(libav* P/Invoke)로 프로그래매틱 트랜스코드를 구현하기 전까지 Graceful 비활성(false).
        //   인자 체인은 MobileFfmpegCommand 로 조립 가능하나 실행기는 별도 필요(캡처/센서/AR/NFC 는 정상).
        _ = MobileFfmpegCommand.ToCommandLine(args);
        return Task.FromResult(false);
    }

    public string ResolveStoragePath(string relative)
    {
        var full = Path.Combine(FileSystem.AppDataDirectory, relative);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        return full;
    }
}
