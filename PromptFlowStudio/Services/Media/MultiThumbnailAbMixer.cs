using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;      // ProcessRunner / CliPathResolver
using PromptFlowStudio.Services.Storyboard;   // StoryboardProject

namespace PromptFlowStudio.Services.Media;

/// <summary>썸네일 변종 — 키·이름·출력 경로·성공·메시지.</summary>
public sealed record ThumbnailVariant(string Key, string Name, string? OutputPath, bool Ok, string Message);

/// <summary>썸네일 A/B 결과 — 성공·변종 목록·피크 타임코드·메시지.</summary>
public sealed record ThumbAbResult(bool Ok, IReadOnlyList<ThumbnailVariant> Variants, int PeakTimecodeMs, string Message);

/// <summary>
/// [PromptFlowStudio] 지능형 멀티 썸네일 A/B 믹서(v3.5.0) — FFmpeg 프레임 포착/합성(외부 라이브러리 0).
///   ▸ 컷별 긴장도(<see cref="StoryboardScene.PresetTension"/>) 최고점 프레임을 자동 포착(<see cref="PeakTimecodeMs"/>) → A/B 테스트용 3종 썸네일 동시 추출.
///   ▸ 3구도: <b>A 클린 피크</b> · <b>B 로고 각인</b>(우측 하단 브랜드 로고) · <b>C 타이틀 임팩트</b>(대형 제목 오버레이).
///   ▸ 커버 스케일/크롭으로 규격 맞춤. 로고/제목 부재는 해당 변종 스킵(Graceful).
/// </summary>
public sealed class MultiThumbnailAbMixer
{
    /// <summary>긴장도 최고 컷의 타임코드(ms) — argmax(PresetTension) × 컷 길이(순수 · 테스트 대상).</summary>
    public static int PeakTimecodeMs(StoryboardProject project, int clipMs = 4600)
    {
        var scenes = project?.Scenes;
        if (scenes is null || scenes.Count == 0) return 0;
        var peakIdx = 0; var peak = int.MinValue;
        for (var i = 0; i < scenes.Count; i++)
        {
            var t = scenes[i].PresetTension ?? 0;
            if (t > peak) { peak = t; peakIdx = i; }
        }
        return peakIdx * clipMs + clipMs / 2;   // 컷 중앙 프레임
    }

    /// <summary>비디오에서 3종 A/B 썸네일을 동시 추출한다.</summary>
    public async Task<ThumbAbResult> GenerateAsync(StoryboardProject project, string videoPath, string outDir, int w = 1080, int h = 1920, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            return new(false, Array.Empty<ThumbnailVariant>(), 0, "영상을 찾을 수 없습니다.");
        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null) return new(false, Array.Empty<ThumbnailVariant>(), 0, "ffmpeg 없음");

        Directory.CreateDirectory(outDir);
        var peakMs = PeakTimecodeMs(project);
        var sec = (peakMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
        var logo = project.WatermarkImagePath ?? project.BuyerEmblemPath ?? "";
        var title = string.IsNullOrWhiteSpace(project.CoverTitle) ? project.Title : project.CoverTitle;
        var variants = new List<ThumbnailVariant>();
        string? tmpTxt = null;

        try
        {
            // A: 클린 피크
            var aPath = Path.Combine(outDir, "thumb_A_clean.png");
            variants.Add(await RunAsync(ffmpeg, BuildExtractArgs(videoPath, sec, aPath, w, h), "clean", "A 클린 피크", aPath, ct).ConfigureAwait(false));

            // B: 로고 각인
            if (!string.IsNullOrWhiteSpace(logo) && File.Exists(logo))
            {
                var bPath = Path.Combine(outDir, "thumb_B_logo.png");
                variants.Add(await RunAsync(ffmpeg, BuildLogoArgs(videoPath, sec, logo, bPath, w, h), "logo", "B 로고 각인", bPath, ct).ConfigureAwait(false));
            }
            else variants.Add(new ThumbnailVariant("logo", "B 로고 각인", null, false, "로고/엠블럼 미등록 — 스킵"));

            // C: 타이틀 임팩트
            if (!string.IsNullOrWhiteSpace(title))
            {
                var font = FontResolver.FindKoreanFont(bold: true);
                var esc = font is not null ? FontResolver.EscapeDrawPath(font) : null;
                var tmpDir = Path.Combine(Path.GetTempPath(), "promptflow-thumb");
                Directory.CreateDirectory(tmpDir);
                tmpTxt = Path.Combine(tmpDir, $"t_{Guid.NewGuid():N}.txt");
                File.WriteAllText(tmpTxt, title!.Trim(), new UTF8Encoding(false));
                var cPath = Path.Combine(outDir, "thumb_C_title.png");
                variants.Add(await RunAsync(ffmpeg, BuildTitleArgs(videoPath, sec, tmpTxt, cPath, w, h, esc), "title", "C 타이틀 임팩트", cPath, ct).ConfigureAwait(false));
            }
            else variants.Add(new ThumbnailVariant("title", "C 타이틀 임팩트", null, false, "제목 없음 — 스킵"));

            var ok = variants.Count(v => v.Ok);
            return new(ok > 0, variants, peakMs, $"{ok}/3 썸네일 추출 · 피크 {sec}s");
        }
        catch (OperationCanceledException) { return new(false, variants, peakMs, "취소됨"); }
        catch (Exception ex) { Program.Log("ThumbAb.Generate", ex); return new(false, variants, peakMs, ex.Message); }
        finally { try { if (tmpTxt is not null && File.Exists(tmpTxt)) File.Delete(tmpTxt); } catch { } }
    }

    // ── 인자 빌더(테스트 대상) ──
    private const string Cover = "scale={0}:{1}:force_original_aspect_ratio=increase,crop={0}:{1}";

    /// <summary>피크 프레임 추출(커버 스케일/크롭).</summary>
    public static IReadOnlyList<string> BuildExtractArgs(string video, string sec, string outPng, int w, int h)
        => new[] { "-y", "-ss", sec, "-i", video, "-frames:v", "1", "-vf", string.Format(CultureInfo.InvariantCulture, Cover, w, h), outPng };

    /// <summary>피크 프레임 + 로고 우측 하단 오버레이.</summary>
    public static IReadOnlyList<string> BuildLogoArgs(string video, string sec, string logo, string outPng, int w, int h)
        => new[]
        {
            "-y", "-ss", sec, "-i", video, "-i", logo, "-frames:v", "1",
            "-filter_complex", $"[0:v]{string.Format(CultureInfo.InvariantCulture, Cover, w, h)}[bg];[1:v]scale={w / 6}:-1[lg];[bg][lg]overlay=W-w-28:H-h-28[o]",
            "-map", "[o]", outPng,
        };

    /// <summary>피크 프레임 + 대형 타이틀 텍스트(하단).</summary>
    public static IReadOnlyList<string> BuildTitleArgs(string video, string sec, string titleFile, string outPng, int w, int h, string? escFont)
    {
        var fontHead = escFont is not null ? $"fontfile={escFont}:" : "";
        var fs = Math.Max(40, h / 14);
        var vf = string.Format(CultureInfo.InvariantCulture, Cover, w, h) +
                 $",drawtext={fontHead}textfile={EscTextPath(titleFile)}:fontcolor=white:fontsize={fs}:borderw=4:bordercolor=black@0.85:x=(w-text_w)/2:y=h-text_h-{h / 8}";
        return new[] { "-y", "-ss", sec, "-i", video, "-frames:v", "1", "-vf", vf, outPng };
    }

    private static string EscTextPath(string p) => (p ?? "").Replace('\\', '/').Replace(":", "\\:");

    private static async Task<ThumbnailVariant> RunAsync(string ffmpeg, IReadOnlyList<string> args, string key, string name, string outPng, CancellationToken ct)
    {
        try
        {
            var res = await ProcessRunner.RunAsync(ffmpeg, args, workingDir: null, onLine: null, ct: ct).ConfigureAwait(false);
            return res.Ok && File.Exists(outPng) ? new(key, name, outPng, true, "완료") : new(key, name, null, false, "추출 실패");
        }
        catch (Exception ex) { Program.Log("ThumbAb.Run", ex); return new(key, name, null, false, ex.Message); }
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
