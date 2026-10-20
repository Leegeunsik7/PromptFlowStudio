using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Wan;   // CudaZeroCopyEngine

namespace PromptFlowStudio.Services.Media;

/// <summary>플랫폼별 출력 규격 — 대상 플랫폼·폴더명·해상도·화면비.</summary>
public sealed record PlatformRenderSpec(SafePlatform Platform, string Key, string Folder, int Width, int Height, string AspectLabel, string Icon);

/// <summary>플랫폼 1건 렌더링 결과.</summary>
public sealed record PlatformVariant(SafePlatform Platform, string Label, bool Ok, bool GpuUsed, string? OutputPath, string Backend, string Message);

/// <summary>멀티 플랫폼 일괄 렌더링 결과(성공 수/변형 목록/출력 루트).</summary>
public sealed record MultiRenderResult(bool Ok, IReadOnlyList<PlatformVariant> Variants, string OutputRoot, string Message);

/// <summary>
/// [PromptFlowStudio] 멀티 플랫폼 일괄 동시 렌더링 & OSMU 패키징 엔진 — 순수 FFmpeg CLI(외부 NuGet 0).
///   ▸ 최종 마스터 영상을 유저가 선택한 플랫폼 규격(유튜브/인스타/틱톡/네이버/당근/치지직 9:16 · 핀터레스트 2:3 · 샤오홍슈 3:4)으로
///     <b>병렬 동시 리타겟</b>한다. 동일 화면비는 <see cref="CudaZeroCopyEngine.ScaleZeroCopyAsync"/>(풀 VRAM 제로카피), 다른 화면비는 세이프존 Y-앵커 편향 커버 크롭.
///   ▸ 결과물은 <c>{projectDir}/platforms/{플랫폼폴더}/final.mp4</c> 로 <b>플랫폼별 자동 분류 저장</b>.
///   ▸ <see cref="MultiPlatformSafeZoneService"/> Y-앵커로 크롭 창을 편향해 자막/피사체 보존. GPU→CPU→원본 3단 Graceful.
/// </summary>
public sealed class MultiPlatformRenderEngine(CudaZeroCopyEngine cuda, DynamicPlatformSubtitleService subs)
{
    /// <summary>8대 플랫폼 출력 규격(세로 숏폼 · 세이프존 Y-앵커 확장 플랫폼).</summary>
    public static readonly IReadOnlyList<PlatformRenderSpec> Specs = new[]
    {
        new PlatformRenderSpec(SafePlatform.YouTubeShorts,  "youtube",    "YouTube_Shorts_9x16",  1080, 1920, "9:16", "▶️"),
        new PlatformRenderSpec(SafePlatform.InstagramReels, "instagram",  "Instagram_Reels_9x16", 1080, 1920, "9:16", "📸"),
        new PlatformRenderSpec(SafePlatform.TikTok,         "tiktok",     "TikTok_9x16",          1080, 1920, "9:16", "🎵"),
        new PlatformRenderSpec(SafePlatform.NaverClip,      "naverclip",  "NaverClip_9x16",       1080, 1920, "9:16", "🟢"),
        new PlatformRenderSpec(SafePlatform.KarrotStory,    "karrot",     "Karrot_Story_9x16",    1080, 1920, "9:16", "🥕"),
        new PlatformRenderSpec(SafePlatform.Chzzk,          "chzzk",      "Chzzk_9x16",           1080, 1920, "9:16", "🎮"),
        new PlatformRenderSpec(SafePlatform.Pinterest,      "pinterest",  "Pinterest_2x3",        1080, 1620, "2:3",  "📌"),
        new PlatformRenderSpec(SafePlatform.Xiaohongshu,    "xiaohongshu","Xiaohongshu_3x4",      1080, 1440, "3:4",  "📕"),
    };

    /// <summary>플랫폼의 출력 규격(없으면 null).</summary>
    public static PlatformRenderSpec? SpecFor(SafePlatform p) => Specs.FirstOrDefault(s => s.Platform == p);

    /// <summary>
    /// 마스터 영상을 선택 플랫폼들로 병렬 리타겟해 플랫폼별 폴더에 저장한다. 실패 플랫폼은 건너뛰고 계속(Graceful).
    /// </summary>
    public async Task<MultiRenderResult> RenderAllAsync(
        string masterVideo, string projectDir, int masterW, int masterH,
        IReadOnlyList<SafePlatform> platforms, int maxParallel = 2, string? platformCaption = null,
        IProgress<(SafePlatform Platform, double Pct)>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(masterVideo) || !File.Exists(masterVideo))
            return new(false, Array.Empty<PlatformVariant>(), "", "마스터 영상을 찾을 수 없습니다.");

        var specs = (platforms ?? Array.Empty<SafePlatform>())
            .Select(SpecFor).Where(s => s is not null).Select(s => s!).Distinct().ToList();
        if (specs.Count == 0)
            return new(false, Array.Empty<PlatformVariant>(), "", "선택된 플랫폼이 없습니다.");

        var outRoot = Path.Combine(projectDir, "platforms");
        Directory.CreateDirectory(outRoot);

        var srcAspect = masterH > 0 ? (double)masterW / masterH : 9.0 / 16.0;
        var variants = new PlatformVariant[specs.Count];
        using var gate = new SemaphoreSlim(Math.Max(1, maxParallel));

        var tasks = specs.Select(async (spec, idx) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var dir = Path.Combine(outRoot, spec.Folder);
                Directory.CreateDirectory(dir);
                var finalPath = Path.Combine(dir, "final.mp4");
                var hasCaption = !string.IsNullOrWhiteSpace(platformCaption);
                var cropOut = hasCaption ? Path.Combine(dir, "_base.mp4") : finalPath;   // 자막 번인 시 중간 파일
                var prog = new Progress<double>(p => progress?.Report((spec.Platform, p)));

                var tgtAspect = (double)spec.Width / spec.Height;
                var sameAspect = Math.Abs(srcAspect - tgtAspect) < 0.02;

                ZeroCopyResult r;
                if (sameAspect)
                {
                    // 동일 화면비 → 풀 VRAM 제로카피 스케일(초고속).
                    r = await cuda.ScaleZeroCopyAsync(masterVideo, cropOut, spec.Width, spec.Height, 0, prog, ct).ConfigureAwait(false);
                }
                else
                {
                    // 다른 화면비 → 세이프존 Y-앵커 편향 커버 크롭.
                    var (top, bottom) = SafeBand(spec.Platform);
                    var yBias = Math.Clamp((top + bottom) / 2.0, 0.2, 0.6);
                    r = await cuda.CropRetargetAsync(masterVideo, cropOut, spec.Width, spec.Height, 0, yBias, prog, ct).ConfigureAwait(false);
                }

                var ok = r.Ok && r.Applied && r.OutputPath is not null;
                var producedPath = ok ? r.OutputPath : null;

                // 플랫폼 규격별 동적 자막 재-번인(세이프존 Y-앵커·폰트 크기 재계산 · 캡션 지정 시에만).
                if (ok && hasCaption)
                {
                    var burned = await subs.BurnAsync(producedPath!, finalPath, platformCaption, spec.Platform, spec.Width, spec.Height, ct).ConfigureAwait(false);
                    if (burned) { producedPath = finalPath; try { if (File.Exists(cropOut)) File.Delete(cropOut); } catch { } }
                    // 번인 실패 시 자막 없는 규격본(cropOut) 유지(Graceful).
                }

                variants[idx] = new(spec.Platform, Label(spec), ok, r.GpuUsed, producedPath, r.Backend, r.Message);
            }
            catch (OperationCanceledException) { variants[idx] = new(spec.Platform, Label(spec), false, false, null, "none", "취소됨"); }
            catch (Exception ex) { variants[idx] = new(spec.Platform, Label(spec), false, false, null, "none", ex.Message); }
            finally { gate.Release(); }
        });

        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* 부분 완료분 반환 */ }

        var list = variants.Where(v => v is not null).ToList();
        var okCount = list.Count(v => v.Ok);
        return new(okCount > 0, list, outRoot, $"{okCount}/{specs.Count} 플랫폼 렌더링 완료");
    }

    /// <summary>세이프존 Y-앵커(상단~하단 비율) — <see cref="MultiPlatformSafeZoneService"/> Zones 조회.</summary>
    private static (double Top, double Bottom) SafeBand(SafePlatform p)
    {
        var z = new MultiPlatformSafeZoneService().Get(p);
        return (z.TopPct, z.BottomPct);
    }

    private static string Label(PlatformRenderSpec s) => $"{s.Icon} {s.Platform} ({s.AspectLabel})";
}
