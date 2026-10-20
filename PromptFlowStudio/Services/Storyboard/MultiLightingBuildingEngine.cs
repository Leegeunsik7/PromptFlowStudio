using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;   // ProcessRunner / CliPathResolver

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>사옥 조명 그레이드 1종 — 키·폴더·표시명·아이콘 + FFmpeg 컬러 그레이드 <c>-vf</c>.</summary>
public sealed record LightingGrade(string Key, string Folder, string Label, string Icon, string Vf);

/// <summary>조명 1종 렌더링 결과.</summary>
public sealed record LightingVariant(string Key, string Label, string Icon, bool Ok, string? OutputPath, string Message);

/// <summary>3색 조명 멀티배치 결과(성공 수/변형 목록/출력 루트).</summary>
public sealed record MultiLightingResult(bool Ok, IReadOnlyList<LightingVariant> Variants, string OutputRoot, string Message);

/// <summary>
/// [PromptFlowStudio] 사옥 3색 조명 멀티배치 렌더 엔진(v2.5.0) — 순수 FFmpeg CLI 컬러 그레이딩(외부 NuGet 0).
///   ▸ <see cref="CorporateBuildingThemeEngine"/>가 만든 사옥 홍보 <b>마스터 1개</b>를 주간(청명)·노을(골든아워)·야경(네온) <b>3색 조명</b>으로
///     <see cref="MultiPlatformRenderEngine"/>와 동일한 <b>세마포어 병렬 패턴</b>으로 일괄 그레이딩해 배출한다 — 한 번의 클릭, 3개 무드.
///   ▸ 결과물은 <c>{projectDir}/lighting/{조명폴더}/final.mp4</c> 로 <b>조명별 자동 분류 저장</b>.
///   ▸ 그레이드는 <c>eq</c>(밝기·대비·채도) + <c>colorbalance</c>(색온도) + <c>curves</c> 조합. GPU 불필요 · libx264 안정 인코딩. 실패 조명은 건너뛰고 계속(Graceful).
/// </summary>
public sealed class MultiLightingBuildingEngine
{
    /// <summary>3색 조명 그레이드 프리셋(주간·노을·야경).</summary>
    public static readonly IReadOnlyList<LightingGrade> Grades = new[]
    {
        new LightingGrade("day", "01_Day_ClearSky", "주간 · 청명", "☀️",
            "eq=brightness=0.04:contrast=1.08:saturation=1.12:gamma=1.02,colorbalance=rs=-0.04:gs=0.0:bs=0.06,curves=b='0/0.02 1/1'"),
        new LightingGrade("golden", "02_GoldenHour_Sunset", "노을 · 골든아워", "🌇",
            "eq=brightness=0.02:contrast=1.10:saturation=1.22:gamma=0.98,colorbalance=rs=0.12:gs=0.03:bs=-0.10:rm=0.06,curves=r='0/0 0.5/0.57 1/1':b='0/0 1/0.92'"),
        new LightingGrade("night", "03_Neon_Night", "야경 · 네온", "🌃",
            "eq=brightness=-0.06:contrast=1.22:saturation=1.28:gamma=0.94,colorbalance=rs=-0.06:gs=-0.02:bs=0.14:bm=0.06,curves=b='0/0.05 1/1':r='0/0 1/0.95'"),
    };

    /// <summary>그레이드 키로 조회(없으면 null).</summary>
    public static LightingGrade? GradeFor(string? key) => Grades.FirstOrDefault(g => string.Equals(g.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>키로 컬러 그레이드 <c>-vf</c> 문자열을 만든다(테스트/미리보기용).</summary>
    public static string BuildGradeVf(string? key) => GradeFor(key)?.Vf ?? "";

    /// <summary>
    /// 사옥 마스터를 3색 조명으로 병렬 그레이딩해 조명별 폴더에 저장한다. 실패 조명은 건너뛰고 계속(Graceful).
    /// </summary>
    public async Task<MultiLightingResult> RenderThreeAsync(
        string masterVideo, string projectDir, int maxParallel = 2,
        IProgress<(string Key, double Pct)>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(masterVideo) || !File.Exists(masterVideo))
            return new(false, Array.Empty<LightingVariant>(), "", "사옥 마스터 영상을 찾을 수 없습니다.");

        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null)
            return new(false, Array.Empty<LightingVariant>(), "", "ffmpeg 실행 파일을 찾을 수 없습니다.");

        var outRoot = Path.Combine(projectDir, "lighting");
        Directory.CreateDirectory(outRoot);

        var variants = new LightingVariant[Grades.Count];
        using var gate = new SemaphoreSlim(Math.Max(1, maxParallel));

        var tasks = Grades.Select(async (grade, idx) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var dir = Path.Combine(outRoot, grade.Folder);
                Directory.CreateDirectory(dir);
                var finalPath = Path.Combine(dir, "final.mp4");

                var args = new List<string>
                {
                    "-y", "-i", masterVideo, "-vf", grade.Vf,
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
                    "-c:a", "copy", "-movflags", "+faststart", finalPath,
                };
                var res = await ProcessRunner.RunAsync(ffmpeg, args, workingDir: null, onLine: null, ct: ct).ConfigureAwait(false);
                var ok = res.Ok && File.Exists(finalPath);
                progress?.Report((grade.Key, ok ? 1.0 : 0.0));
                variants[idx] = new(grade.Key, grade.Label, grade.Icon, ok, ok ? finalPath : null, ok ? "완료" : "인코딩 실패");
            }
            catch (OperationCanceledException) { variants[idx] = new(grade.Key, grade.Label, grade.Icon, false, null, "취소됨"); }
            catch (Exception ex) { variants[idx] = new(grade.Key, grade.Label, grade.Icon, false, null, ex.Message); }
            finally { gate.Release(); }
        });

        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* 부분 완료분 반환 */ }

        var list = variants.Where(v => v is not null).ToList();
        var okCount = list.Count(v => v.Ok);
        return new(okCount > 0, list, outRoot, $"{okCount}/{Grades.Count} 조명 배치 완료");
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
