using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PromptFlowStudio.Services.Media;        // ILocalMediaService
using PromptFlowStudio.Services.Storyboard;   // StoryboardProject · GalleryAsset · GalleryAssetKind

namespace PromptFlowStudio.Services.Ui;

/// <summary>
/// [PromptFlow Studio v5.6 · Task2] 갤러리 자동 자산화 서비스 — 순수 BCL(외부 SDK 0).
///   ▸ Concat 롱폼 병합 완료 · 📸 키프레임 추출 시 자산을 <see cref="StoryboardProject.Gallery"/> + 세션 목록에 등록.
///   ▸ data URL(스텁 샘플/추출 프레임)은 프로젝트 미디어 폴더(<c>{MediaRoot}/{pid}/gallery</c>)에 파일로 영속(side effect).
///   ▸ <see cref="OnChanged"/> 방송으로 갤러리 탭이 실시간 갱신(표시는 세션 목록의 원본 URL 사용 → WebView 즉시 렌더).
/// </summary>
public sealed class GalleryAutoSaveService
{
    private readonly ILocalMediaService _media;
    private readonly object _lock = new();
    private readonly List<GalleryAsset> _assets = new();

    public GalleryAutoSaveService(ILocalMediaService media) => _media = media;

    /// <summary>세션 내 자산화된 갤러리 항목(최신순 스냅샷).</summary>
    public IReadOnlyList<GalleryAsset> Assets { get { lock (_lock) return _assets.ToList(); } }

    /// <summary>목록 변경 방송(갤러리 탭 실시간 갱신).</summary>
    public event Action? OnChanged;

    /// <summary>자산 등록 — 중복(URL 동일) 무시. data URL 은 미디어 폴더에 파일 영속(best-effort).</summary>
    public GalleryAsset? Register(StoryboardProject? project, GalleryAssetKind kind, string? url, string label)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        // data URL 은 프로젝트 미디어 폴더에 파일로 영속(자산 보존 · 표시는 원본 URL 사용).
        if (url!.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            try { PersistDataUrl(project, url!, kind); } catch (Exception ex) { Program.Log("GalleryAutoSave.Persist", ex); }

        var asset = new GalleryAsset
        {
            Url = url!,
            Kind = kind,
            Label = label ?? "",
            CreatedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        lock (_lock)
        {
            if (_assets.Any(a => a.Url == asset.Url)) return null;   // 중복 방지
            _assets.Insert(0, asset);
        }
        try { project?.Gallery.Add(asset); } catch { /* 프로젝트 임베드 실패는 무시 */ }
        OnChanged?.Invoke();
        return asset;
    }

    private void PersistDataUrl(StoryboardProject? project, string dataUrl, GalleryAssetKind kind)
    {
        var comma = dataUrl.IndexOf("base64,", StringComparison.Ordinal);
        if (comma < 5) return;
        var meta = dataUrl.Substring(5, comma - 5);   // 예: "image/svg+xml;"
        var ext = meta.Contains("svg") ? ".svg" : meta.Contains("png") ? ".png" : meta.Contains("mp4") ? ".mp4" : meta.Contains("webp") ? ".webp" : ".jpg";
        var bytes = Convert.FromBase64String(dataUrl[(comma + 7)..]);
        var pid = string.IsNullOrWhiteSpace(project?.Id) ? "flow" : project!.Id;
        var dir = Path.Combine(_media.MediaRoot, pid, "gallery");
        Directory.CreateDirectory(dir);
        var prefix = kind == GalleryAssetKind.Video ? "clip" : "frame";
        File.WriteAllBytes(Path.Combine(dir, $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss_fff}{ext}"), bytes);
    }
}
