using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Media;
using Microsoft.Maui.Storage;
using PromptFlowStudio.Services.Mobile;   // IMobilePlatformBridge / MobileFfmpegCommand (기존 재사용)

namespace PromptFlowStudio.Mobile.Services;

/// <summary>
/// [PromptFlow Studio v4.2] 애플 iOS(iPhone/iPad) 플랫폼 브릿지 — 카메라/마이크(MediaPicker·Permissions) + FFmpegKit(iOS).
/// </summary>
public sealed class IosPlatformBridge : IMobilePlatformBridge
{
    public PlatformInfo Info { get; } = new("iOS (iPhone/iPad)", IsMobile: true, HasCamera: true, HasFFmpegKit: false, FfmpegBackend: "FFmpeg.AutoGen (bindings · CLI exec 미구현)");

    public async Task<bool> EnsurePermissionAsync(MobilePermission permission, CancellationToken ct = default) => permission switch
    {
        MobilePermission.Camera => await Ensure<Permissions.Camera>(),
        MobilePermission.Microphone => await Ensure<Permissions.Microphone>(),
        _ => await Ensure<Permissions.Photos>(),
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
                    var photo = await MediaPicker.Default.CapturePhotoAsync();
                    return photo is null ? new(false, kind, null, "취소됨") : new(true, kind, await CopyIn(photo, ct), "촬영 완료");
                case CaptureKind.Video:
                    if (!await EnsurePermissionAsync(MobilePermission.Camera, ct)) return new(false, kind, null, "카메라 권한 거부");
                    var video = await MediaPicker.Default.CaptureVideoAsync();
                    return video is null ? new(false, kind, null, "취소됨") : new(true, kind, await CopyIn(video, ct), "영상 캡처 완료");
                default:
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
        // v5.1.1: FFmpegKit(Laerdal.FFmpeg.iOS) 제거 — FFmpeg.AutoGen 프로그래매틱 경로 구현 전까지 Graceful 비활성(false).
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
