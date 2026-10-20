using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Account;   // LicenseService.DataAreaRoot()
using PromptFlowStudio.Services.Media;     // ILocalMediaService

namespace PromptFlowStudio.Services.Platform;

/// <summary>임시 파일/프로세스 자원 정소 계약.</summary>
public interface IResourceCleanupService
{
    /// <summary>앱 시작 시 24시간 이상 지난 임시/캐시 파편을 백그라운드로 정소(총 삭제 수 반환).</summary>
    Task<int> CleanupOnStartupAsync();
    /// <summary>프로젝트 종료 시 해당 프로젝트의 중간 산출물(up_/lipsync_ 등)을 정리.</summary>
    int CleanupProject(string projectId);
}

/// <summary>
/// [PromptFlowStudio] 자원 정소 — 임시 렌더링 파편/캐시 누수 차단(외부 NuGet 0, 순수 BCL).
///   ▸ 대상: <c>%LocalAppData%\PromptFlowStudio\Temp\</c>, <c>Media/_tts</c>(TTS 캐시), 프로젝트 폴더의 중간본(up_/lipsync_/*_4k),
///     시스템 <c>%Temp%\promptflow-*</c>(subs/cover/upscale/lipsync)·업데이트 임시. <b>24시간 이상</b> 지난 파일만 삭제(진행 중 작업 보호).
///   ▸ Graceful: 파일별 try/catch — 잠금/권한 파일은 건너뛰고 계속. 앱 시작을 막지 않도록 백그라운드 실행.
/// </summary>
public sealed class ResourceCleanupService(ILocalMediaService media) : IResourceCleanupService
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private string DataArea => LicenseService.DataAreaRoot();
    private string MediaRoot => media.MediaRoot;

    /// <inheritdoc/>
    public Task<int> CleanupOnStartupAsync() => Task.Run(() =>
    {
        var n = 0;
        try
        {
            // ① 데이터영역 Temp
            n += SweepDir(Path.Combine(DataArea, "Temp"), MaxAge, recurse: true);

            // ② TTS 캐시(_tts) — 오래된 mp3/wav/정규화본
            n += SweepDir(Path.Combine(MediaRoot, "_tts"), MaxAge, recurse: false);

            // ③ 프로젝트 폴더의 중간 렌더 산출물(up_/lipsync_/*_4k) — final.mp4·cover 는 보존
            n += SweepMediaIntermediates(MaxAge);

            // ④ 시스템 임시 폴더(%Temp%\promptflow-*, 업데이트 임시)
            foreach (var d in SystemTempDirs())
                n += SweepDir(d, MaxAge, recurse: true, removeEmpty: true);
        }
        catch (Exception ex) { Program.Log("ResourceCleanup.Startup", ex); }
        return n;
    });

    /// <inheritdoc/>
    public int CleanupProject(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return 0;
        var n = 0;
        try
        {
            var dir = Path.Combine(MediaRoot, projectId);
            if (Directory.Exists(dir))
                foreach (var f in SafeFiles(dir))
                {
                    var name = Path.GetFileName(f);
                    // 중간 산출물만 제거(최종본 final.mp4·final_4k.mp4·cover.jpg 는 보존).
                    if (name.StartsWith("up_", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("lipsync_", StringComparison.OrdinalIgnoreCase))
                        n += TryDelete(f);
                }
        }
        catch (Exception ex) { Program.Log("ResourceCleanup.Project", ex); }
        return n;
    }

    // ── 내부 ──
    private int SweepMediaIntermediates(TimeSpan age)
    {
        var n = 0;
        try
        {
            if (!Directory.Exists(MediaRoot)) return 0;
            foreach (var dir in SafeDirs(MediaRoot))
                foreach (var f in SafeFiles(dir))
                {
                    var name = Path.GetFileName(f);
                    var isIntermediate = name.StartsWith("up_", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("lipsync_", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith("_4k.mp4", StringComparison.OrdinalIgnoreCase);
                    if (isIntermediate && IsOld(f, age)) n += TryDelete(f);
                }
        }
        catch { /* 무시 */ }
        return n;
    }

    private static int SweepDir(string dir, TimeSpan age, bool recurse, bool removeEmpty = false)
    {
        var n = 0;
        try
        {
            if (!Directory.Exists(dir)) return 0;
            foreach (var f in SafeFiles(dir))
                if (IsOld(f, age)) n += TryDelete(f);

            if (recurse)
                foreach (var sub in SafeDirs(dir))
                {
                    n += SweepDir(sub, age, recurse: true, removeEmpty);
                    if (removeEmpty) TryRemoveEmptyDir(sub);
                }
        }
        catch { /* 무시 */ }
        return n;
    }

    private IEnumerable<string> SystemTempDirs()
    {
        var t = Path.GetTempPath();
        yield return Path.Combine(t, "promptflow-subs");
        yield return Path.Combine(t, "promptflow-cover");
        yield return Path.Combine(t, "promptflow-upscale");
        yield return Path.Combine(t, "promptflow-lipsync");
        yield return Path.Combine(t, "PromptFlowStudio-Update");
    }

    private static bool IsOld(string file, TimeSpan age)
    { try { return DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > age; } catch { return false; } }

    private static int TryDelete(string file)
    { try { File.Delete(file); return 1; } catch { return 0; } }

    private static void TryRemoveEmptyDir(string dir)
    { try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { } }

    private static IEnumerable<string> SafeFiles(string dir)
    { try { return Directory.EnumerateFiles(dir); } catch { return Array.Empty<string>(); } }

    private static IEnumerable<string> SafeDirs(string dir)
    { try { return Directory.EnumerateDirectories(dir); } catch { return Array.Empty<string>(); } }
}
