using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;   // ProcessRunner / CliPathResolver

namespace PromptFlowStudio.Services.Media;

/// <summary>3D 버추얼 무대 배경 템플릿 — 키·이름·이모지·기본 크로마색·절차적 배경 lavfi.</summary>
public sealed record StudioBackground(string Key, string Name, string Emoji, string ChromaDefault, string BgLavfi);

/// <summary>합성 결과.</summary>
public sealed record CompositeResult(bool Ok, string? OutputPath, string Message);

/// <summary>
/// [PromptFlowStudio] 3D 버추얼 스튜디오 크로마키 &amp; 배경 합성 엔진(v3.0.0) — FFmpeg chromakey/despill/overlay(외부 라이브러리 0).
///   ▸ 피사체 녹색/청색 배경을 <c>chromakey</c> + <c>despill</c>(경계 색번짐 제거)로 깔끔히 분리.
///   ▸ B2B 사이버 쇼룸 · 방송 주조종실 · 숲속 마법 무대 등 <b>3D 배경 템플릿</b>(절차적 lavfi 그라디언트 · 무에셋 오프라인)과 레이어 합성.
///   ▸ 배경 미디어(사옥 전경 등)를 직접 지정하면 그 위에 합성. 실패/부재는 Graceful(원본 유지).
/// </summary>
public sealed class VirtualStudioCompositor
{
    /// <summary>버추얼 무대 템플릿(절차적 배경 · 무에셋).</summary>
    public static readonly IReadOnlyList<StudioBackground> Backgrounds = new[]
    {
        new StudioBackground("cyber_showroom", "사이버 쇼룸", "🛰️", "green", "gradients=s={w}x{h}:c0=0x0b0d14:c1=0x0ea5e9:nb_colors=2:speed=0.008"),
        new StudioBackground("control_room", "방송 주조종실", "🎛️", "green", "gradients=s={w}x{h}:c0=0x0b0d14:c1=0xef4444:nb_colors=2:speed=0.006"),
        new StudioBackground("forest_magic", "숲속 마법 무대", "🌲", "green", "gradients=s={w}x{h}:c0=0x052e16:c1=0x22d3ee:nb_colors=2:speed=0.01"),
        new StudioBackground("brand_void", "브랜드 보이드", "🖤", "green", "gradients=s={w}x{h}:c0=0x000000:c1=0x8b5cf6:nb_colors=2:speed=0.005"),
    };

    /// <summary>템플릿 키로 조회(없으면 첫 템플릿).</summary>
    public static StudioBackground Resolve(string? key) => Backgrounds.FirstOrDefault(b => string.Equals(b.Key, key, StringComparison.OrdinalIgnoreCase)) ?? Backgrounds[0];

    /// <summary>크로마색 → (hex, despill type).</summary>
    public static (string Hex, string Type) ChromaInfo(string? color)
        => (color ?? "").Trim().ToLowerInvariant() == "blue" ? ("0x0000FF", "blue") : ("0x00FF00", "green");

    /// <summary>크로마키 + despill 필터 조각(테스트/단독 사용).</summary>
    public static string BuildChromaFilter(string chromaColor, double similarity = 0.10, double blend = 0.08)
    {
        var (hex, type) = ChromaInfo(chromaColor);
        var s = similarity.ToString("0.##", CultureInfo.InvariantCulture);
        var b = blend.ToString("0.##", CultureInfo.InvariantCulture);
        return $"chromakey={hex}:{s}:{b},despill=type={type}:mix=0.5:expand=0";
    }

    /// <summary>
    /// 합성 filter_complex — 입력0=전경(크로마 피사체), 입력1=배경. 배경 스케일 → 전경 키잉/despill/스케일 → overlay.
    /// </summary>
    public static string BuildCompositeFilterComplex(string chromaColor, int w, int h, string outLabel)
    {
        var key = BuildChromaFilter(chromaColor);
        return $"[1:v]scale={w}:{h},setsar=1[bg];[0:v]{key},scale={w}:{h}[fg];[bg][fg]overlay=shortest=1:format=auto[{outLabel}]";
    }

    /// <summary>
    /// 전경(크로마) 영상을 버추얼 무대(템플릿 절차 배경, 또는 지정 배경 미디어)에 합성한다.
    /// </summary>
    public async Task<CompositeResult> CompositeAsync(string fgVideo, string outPath, string templateKey, string chromaColor, int w, int h,
        string? bgMediaPath = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fgVideo) || !File.Exists(fgVideo)) return new(false, null, "전경 영상을 찾을 수 없습니다.");
        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null) return new(false, null, "ffmpeg 실행 파일을 찾을 수 없습니다.");

        try
        {
            var dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var fc = BuildCompositeFilterComplex(chromaColor, w, h, "vout");

            var args = new List<string> { "-y", "-i", fgVideo };
            if (!string.IsNullOrWhiteSpace(bgMediaPath) && File.Exists(bgMediaPath))
                args.AddRange(new[] { "-stream_loop", "-1", "-i", bgMediaPath });   // 지정 배경 미디어(사옥 전경 등)
            else
            {
                var bg = Resolve(templateKey);
                var lavfi = bg.BgLavfi.Replace("{w}", w.ToString(CultureInfo.InvariantCulture)).Replace("{h}", h.ToString(CultureInfo.InvariantCulture));
                args.AddRange(new[] { "-f", "lavfi", "-i", $"{lavfi}:r=30" });      // 절차적 3D 무대(무에셋)
            }
            args.AddRange(new[]
            {
                "-filter_complex", fc, "-map", "[vout]", "-map", "0:a?",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
                "-c:a", "copy", "-shortest", "-movflags", "+faststart", outPath,
            });
            var res = await ProcessRunner.RunAsync(ffmpeg, args, workingDir: null, onLine: null, ct: ct).ConfigureAwait(false);
            return res.Ok && File.Exists(outPath)
                ? new(true, outPath, $"버추얼 무대 합성 완료 · {Resolve(templateKey).Name}")
                : new(false, null, "합성 실패(크로마 배경/ffmpeg 확인)");
        }
        catch (OperationCanceledException) { return new(false, null, "취소됨"); }
        catch (Exception ex) { Program.Log("VirtualStudio.Composite", ex); return new(false, null, "합성 실패: " + ex.Message); }
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
