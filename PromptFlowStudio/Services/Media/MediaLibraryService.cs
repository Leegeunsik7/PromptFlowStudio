using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace PromptFlowStudio.Services.Media;

/// <summary>로컬에 자산화된 미디어 1건(갤러리/퀵갤러리 표시용). PosterUrl 은 추출된 썸네일(.jpg)이 있을 때만.</summary>
public sealed record MediaItem(string Path, string PlaybackUrl, string? PosterUrl, string Name, string Folder, long Bytes, DateTime Created);

/// <summary>
/// [PromptFlowStudio] 로컬 미디어 탐색기 — <see cref="ILocalMediaService.MediaRoot"/> 하위의 *.mp4 를 열거/삭제/탐색기표시.
///   순수 BCL(System.IO/Diagnostics). 재생 URL 은 가상호스트(<see cref="ILocalMediaService.ToPlaybackUrl"/>)로 변환.
/// </summary>
public interface IMediaLibraryService
{
    string MediaRoot { get; }
    /// <summary>최신순 미디어 목록(최대 <paramref name="max"/>건). 실패해도 빈 목록.</summary>
    IReadOnlyList<MediaItem> List(int max = 300);
    /// <summary>파일 삭제(성공 여부).</summary>
    bool Delete(string path);
    /// <summary>Windows 탐색기에서 파일 위치 열기(/select).</summary>
    void Reveal(string path);
    /// <summary>미디어 드라이브 여유 공간(바이트). 실패 시 0.</summary>
    long FreeSpaceBytes();
}

/// <inheritdoc cref="IMediaLibraryService"/>
public sealed class MediaLibraryService(ILocalMediaService media) : IMediaLibraryService
{
    public string MediaRoot => media.MediaRoot;

    public IReadOnlyList<MediaItem> List(int max = 300)
    {
        var root = media.MediaRoot;
        var list = new List<MediaItem>();
        try
        {
            if (!Directory.Exists(root)) return list;
            foreach (var f in Directory.EnumerateFiles(root, "*.mp4", SearchOption.AllDirectories))
            {
                try
                {
                    var fi = new FileInfo(f);
                    var folder = Path.GetFileName(Path.GetDirectoryName(f) ?? "");
                    var poster = f + ".jpg";
                    var posterUrl = File.Exists(poster) ? media.ToPlaybackUrl(poster) : null;
                    list.Add(new MediaItem(f, media.ToPlaybackUrl(f), posterUrl, Path.GetFileName(f), folder, fi.Length, fi.CreationTime));
                }
                catch { /* 개별 파일 접근 실패는 건너뜀 */ }
            }
        }
        catch (Exception ex) { Program.Log("MediaLibrary.List", ex); }

        return list.OrderByDescending(i => i.Created).Take(max).ToList();
    }

    public bool Delete(string path)
    {
        try
        {
            if (File.Exists(path)) { File.Delete(path); return true; }
        }
        catch (Exception ex) { Program.Log("MediaLibrary.Delete", ex); }
        return false;
    }

    public void Reveal(string path)
    {
        try
        {
            // 탐색기에서 해당 파일을 선택한 채로 연다.
            ProcessCompat.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { Program.Log("MediaLibrary.Reveal", ex); }
    }

    public long FreeSpaceBytes()
    {
        try
        {
            var rootPath = Path.GetPathRoot(Path.GetFullPath(media.MediaRoot));
            if (string.IsNullOrEmpty(rootPath)) return 0;
            return new DriveInfo(rootPath).AvailableFreeSpace;
        }
        catch { return 0; }
    }

    /// <summary>바이트를 사람이 읽는 단위로(B/KB/MB/GB/TB).</summary>
    public static string FormatBytes(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double b = bytes; int i = 0;
        while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        return $"{b:0.#} {u[i]}";
    }
}
