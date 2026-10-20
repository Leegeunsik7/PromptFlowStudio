using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Forms;   // IBrowserFile
using PromptFlowStudio.Services.Media;         // ILocalMediaService
using PromptFlowStudio.Services.Wan;           // IWanApiService(OSS 업로드)

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>앵커 이미지 등록 결과(저장 경로 + 가상호스트 썸네일 URL). 실패 시 Ok=false.</summary>
public sealed record AnchorResult(bool Ok, string? Path, string? ThumbUrl, string Message);

/// <summary>메인 캐릭터/스타일 앵커 계약 — 앵커 이미지를 등록하고, 컷 생성 시 사용할 I2V 참조 이미지를 결정한다.</summary>
public interface ICharacterAnchorService
{
    /// <summary>앵커 이미지를 미디어 폴더에 저장하고 경로/썸네일을 반환. 실패는 Graceful.</summary>
    Task<AnchorResult> RegisterAsync(IBrowserFile file, CancellationToken ct = default);

    /// <summary>이 컷 생성에 쓸 I2V 참조 이미지 경로 — 컷 자체 이미지 우선 → 프로젝트 앵커 → 없으면 null.</summary>
    string? ResolveReferenceImage(StoryboardScene scene, StoryboardProject project);

    /// <summary>프로젝트에 유효한 앵커 이미지가 등록돼 있는지(파일 존재).</summary>
    bool HasAnchor(StoryboardProject project);

    /// <summary>등록된 앵커의 가상호스트 썸네일 URL(없으면 null).</summary>
    string? ThumbUrl(StoryboardProject project);

    /// <summary>앵커 이미지의 OSS 업로드 URL(oss://)을 프로젝트 단위로 캐싱해 반환 — 첫 컷에서 1회 업로드 후 2~5번 컷은 재사용.
    ///   앵커 없음/업로드 실패는 null(호출부가 로컬 경로 업로드로 폴백). thread-safe(동시 컷 생성 시 1회만 업로드).</summary>
    Task<string?> GetOrUploadAnchorUrlAsync(StoryboardProject project, CancellationToken ct = default);
}

/// <summary>
/// [PromptFlowStudio] 캐릭터/스타일 앵커 — Wan 2.1 I2V 로 전 컷의 등장인물 외모·미술 스타일을 유지(일관성). 외부 NuGet 0.
///   ▸ 등록: 업로드 이미지를 <c>MediaRoot/_uploads/anchor</c> 에 저장(가상호스트 썸네일). <see cref="StoryboardProject.AnchorImagePath"/> 에 경로 보관.
///   ▸ 바인딩: <see cref="ResolveReferenceImage"/> 가 컷 생성 시 참조 이미지를 결정 — 컷 자체 이미지가 있으면 그것, 없으면 앵커.
///     StoryboardJobManager 가 이 값을 WanVideoRequest.ImagePath 로 넣어 모든 컷을 앵커 기반 I2V 로 호출한다(컷 이미지 없는 T2V 컷도 앵커로 i2v 전환).
///   ▸ 파일 부재/손상은 null 로 폴백(앵커 없이 기존 t2v/i2v 흐름).
/// </summary>
public sealed class CharacterAnchorService(ILocalMediaService media, IWanApiService wan) : ICharacterAnchorService
{
    private const long MaxSize = PromptFlowStudio.Services.MediaPipeline.UnlimitedMediaStreamBufferPipeline.DefaultMaxBytes;   // v16.57.0: 12MB 상한 해제(스트리밍 · 업로드 한도 초과 시 프록시)
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(12);   // DashScope 임시 OSS 만료 예방(12h TTL)

    // 프로젝트+이미지경로 → OSS 업로드 URL 캐시(재업로드 방지). 앵커가 바뀌면 키가 달라져 자동 무효화 + 12h 경과 시 재업로드.
    private readonly Dictionary<string, AnchorCacheEntry> _ossCache = new(StringComparer.Ordinal);
    private readonly object _cacheLock = new();
    private readonly SemaphoreSlim _uploadGate = new(1, 1);   // 동시 컷 생성 시 업로드 1회로 직렬화

    /// <summary>OSS 업로드 캐시 항목 — URL + 업로드 시각(UTC). 12h 경과 시 만료로 간주해 재업로드.</summary>
    private sealed record AnchorCacheEntry(string OssUrl, DateTime CachedAt);

    /// <inheritdoc/>
    public async Task<AnchorResult> RegisterAsync(IBrowserFile file, CancellationToken ct = default)
    {
        if (file is null) return new(false, null, null, "파일이 없습니다.");
        if (file.Size > MaxSize) return new(false, null, null, "이미지가 수신 상한을 초과합니다.");

        try
        {
            var dir = Path.Combine(media.MediaRoot, "_uploads", "anchor");
            Directory.CreateDirectory(dir);
            var ext = Path.GetExtension(file.Name);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".png";
            var savePath = Path.Combine(dir, $"anchor_{Guid.NewGuid():N}{ext}");

            await using (var src = file.OpenReadStream(MaxSize, ct))
            await using (var dst = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            savePath = PromptFlowStudio.Services.MediaPipeline.UnlimitedMediaStreamBufferPipeline.EnsureUploadProxy(savePath);   // 32MB/4K 초과 → 장변 2048 프록시

            return new(true, savePath, media.ToPlaybackUrl(savePath), "앵커 이미지가 등록되었습니다.");
        }
        catch (Exception ex) { Program.Log("CharacterAnchor.Register", ex); return new(false, null, null, "앵커 등록 실패: " + ex.Message); }
    }

    /// <inheritdoc/>
    public string? ResolveReferenceImage(StoryboardScene scene, StoryboardProject project)
    {
        // 컷 자체 참조 이미지가 최우선(개별 오버라이드).
        if (!string.IsNullOrEmpty(scene.ImagePath) && SafeExists(scene.ImagePath)) return scene.ImagePath;
        // 없으면 프로젝트 앵커(모든 컷 일관성).
        var a = project.AnchorImagePath;
        return !string.IsNullOrEmpty(a) && SafeExists(a) ? a : null;
    }

    /// <inheritdoc/>
    public bool HasAnchor(StoryboardProject project)
        => !string.IsNullOrEmpty(project.AnchorImagePath) && SafeExists(project.AnchorImagePath);

    /// <inheritdoc/>
    public string? ThumbUrl(StoryboardProject project)
        => HasAnchor(project) ? media.ToPlaybackUrl(project.AnchorImagePath!) : null;

    /// <inheritdoc/>
    public async Task<string?> GetOrUploadAnchorUrlAsync(StoryboardProject project, CancellationToken ct = default)
    {
        if (!HasAnchor(project)) return null;
        var key = project.Id + "|" + project.AnchorImagePath;   // 이미지 바뀌면 키가 달라져 자동 재업로드

        if (TryGetFresh(key) is { } hit) return hit;            // 빠른 경로: 캐시 히트 + TTL 유효

        await _uploadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (TryGetFresh(key) is { } hit2) return hit2;      // 락 획득 후 재확인(동시성)

            // 만료됐거나 없으면 재업로드(장시간 세션 URL 만료 예방).
            var up = await wan.UploadImageAsync(project.AnchorImagePath!, null, ct).ConfigureAwait(false);
            if (!up.Ok || string.IsNullOrEmpty(up.OssUrl))
            {
                Program.Log("CharacterAnchor.Upload", new Exception("앵커 OSS 업로드 실패: " + up.Message));
                return null;   // 폴백: 호출부가 로컬 경로로 개별 업로드
            }
            lock (_cacheLock) { _ossCache[key] = new AnchorCacheEntry(up.OssUrl!, DateTime.UtcNow); }
            return up.OssUrl;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { Program.Log("CharacterAnchor.GetOrUpload", ex); return null; }
        finally { _uploadGate.Release(); }
    }

    /// <summary>캐시에 있고 12h 이내면 URL 반환. 만료됐으면 항목 제거 후 null(→ 재업로드 유도).</summary>
    private string? TryGetFresh(string key)
    {
        lock (_cacheLock)
        {
            if (!_ossCache.TryGetValue(key, out var e)) return null;
            if (DateTime.UtcNow - e.CachedAt < CacheTtl) return e.OssUrl;
            _ossCache.Remove(key);   // 만료 → 무효화
            return null;
        }
    }

    private static bool SafeExists(string? p)
    { try { return !string.IsNullOrEmpty(p) && File.Exists(p); } catch { return false; } }
}
