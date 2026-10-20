using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;   // EngineConfigService / ProcessRunner / EngineKeys
using PromptFlowStudio.Services.Media;     // IVideoMergeService / ILocalMediaService
using PromptFlowStudio.Services.Wan;       // GeminiClient / GeminiMedia

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>얼굴 크롭 결과. Ok=false 면 실패. Method="Gemini Vision" | "Heuristic". FaceCenterYPct=적용된 얼굴 세로 중심(%).</summary>
public sealed record FaceCropResult(bool Ok, string? Path, string Method, bool Upscaled, double FaceCenterYPct, string Message);

/// <summary>
/// [PromptFlowStudio] LLM Vision 무모델 얼굴 오토 크롭기 — 외부 ML(YOLO/OpenCV) 없이 <b>Gemini Vision</b> 추론으로 아기 얼굴 위치를 얻어 4K 썸네일을 정밀 크롭.
///   ▸ 렌더 완료 컷의 표정 피크 프레임을 캡처 → Gemini 에 "얼굴 중심 X/Y 비율·너비"를 <c>JSON</c>으로 질의 → <c>ffmpeg crop</c> 파라미터로 변환.
///   ▸ 얼굴 중심에 맞춘 <b>1.5배 매크로 크롭</b> 후 Real-ESRGAN 4K(<see cref="EngineKeys.RealEsrgan"/>) 업스케일 → thumbnail.png.
///   ▸ <b>오프라인 폴백</b>: Gemini 키 없음/실패/저신뢰(confidence&lt;0.3)면 휴리스틱(중앙-상단 43%)으로 크롭 — 항상 산출 시도(Graceful).
///   ▸ 크롭 중심 비율은 C# 에서 [1/3, 2/3]로 클램프해 크롭 박스가 항상 프레임 안에 들어오게 함(ffmpeg min/max·콤마 이스케이프 불필요).
/// </summary>
public sealed class LlmVisionCropService(IVideoMergeService merge, EngineConfigService engines, ILocalMediaService media, GeminiClient gemini)
{
    private const double CaptureSec = 2.2;       // 표정 피크 근사 시점(소스 기준)
    private const double Zoom = 1.5;             // 매크로 줌 배수
    private const double HeuristicFaceY = 43.0;  // 폴백 얼굴 세로 중심(%) — 상단-중앙(y_offset≈0.10·ih)

    /// <summary>표정 피크 프레임에서 Gemini Vision 으로 얼굴을 찾아 중심 크롭 + 4K 업스케일한 thumbnail.png 를 만든다.</summary>
    public async Task<FaceCropResult> CropFaceThumbnailAsync(StoryboardProject project, IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (project is null) return new(false, null, "Heuristic", false, HeuristicFaceY, "프로젝트가 없습니다.");
        var ffmpeg = merge.ResolvedFfmpegPath;
        if (ffmpeg is null) return new(false, null, "Heuristic", false, HeuristicFaceY, "ffmpeg 를 찾을 수 없습니다.");

        var peak = PickPeakScene(project);
        if (peak?.LocalPath is null) return new(false, null, "Heuristic", false, HeuristicFaceY, "썸네일을 만들 완료된 컷(로컬 파일)이 없습니다.");

        try
        {
            var dir = Path.Combine(media.MediaRoot, project.Id);
            Directory.CreateDirectory(dir);
            var probeJpg = Path.Combine(dir, "_face_probe.jpg");
            var cropPng = Path.Combine(dir, "_face_crop.png");
            var outPng = Path.Combine(dir, "thumbnail.png");

            // ── ① 프로브 프레임 캡처(축소 720w — Gemini 인라인 상한/속도) ──
            log?.Report("표정 피크 프레임 캡처…");
            if (!await RunFfmpegAsync(ffmpeg, new[] { "-y", "-ss", Inv(CaptureSec), "-i", peak.LocalPath!, "-vf", "scale=720:-2", "-frames:v", "1", "-q:v", "3", probeJpg }, ct).ConfigureAwait(false)
                || !File.Exists(probeJpg))
                return new(false, null, "Heuristic", false, HeuristicFaceY, "프레임 캡처에 실패했습니다.");

            // ── ② Gemini Vision 으로 얼굴 중심 추론(키 없음/실패/저신뢰면 휴리스틱) ──
            var (fxPct, fyPct, method) = await InferFaceCenterAsync(probeJpg, log, ct).ConfigureAwait(false);

            // ── ③ 얼굴 중심 1.5배 매크로 크롭 + 720w ──
            log?.Report($"{method} 기준 얼굴 매크로 크롭…");
            var vf = BuildCropVf(fxPct, fyPct);
            if (!await RunFfmpegAsync(ffmpeg, new[] { "-y", "-i", probeJpg, "-vf", vf, "-frames:v", "1", "-q:v", "2", cropPng }, ct).ConfigureAwait(false)
                || !File.Exists(cropPng))
                return new(false, null, method, false, fyPct, "얼굴 크롭에 실패했습니다.");
            TryDelete(probeJpg);

            // ── ④ Real-ESRGAN 4K → 없으면 ffmpeg lanczos 폴백 ──
            var esrgan = engines.Resolve(EngineKeys.RealEsrgan);
            if (esrgan is not null)
            {
                log?.Report("Real-ESRGAN 4K 업스케일 중…");
                var model = engines.Extra(EngineKeys.RealEsrgan, "Model", "realesrgan-x4plus") ?? "realesrgan-x4plus";
                var args = ProcessRunner.Substitute(engines.Args(EngineKeys.RealEsrgan), new System.Collections.Generic.Dictionary<string, string>
                { ["in"] = cropPng, ["out"] = outPng, ["scale"] = "4", ["model"] = model });
                var r = await ProcessRunner.RunAsync(esrgan, args, null, null, ct).ConfigureAwait(false);
                if (r.Started && r.ExitCode == 0 && File.Exists(outPng))
                {
                    TryDelete(cropPng);
                    return new(true, outPng, method, true, fyPct, $"{method} 얼굴 크롭 + Real-ESRGAN 4K 완료.");
                }
                log?.Report("Real-ESRGAN 실패 → ffmpeg 고화질 스케일 폴백.");
            }

            if (await RunFfmpegAsync(ffmpeg, new[] { "-y", "-i", cropPng, "-vf", "scale=2160:-2:flags=lanczos", "-frames:v", "1", "-q:v", "2", outPng }, ct).ConfigureAwait(false)
                && File.Exists(outPng))
            {
                TryDelete(cropPng);
                return new(true, outPng, method, false, fyPct, $"{method} 얼굴 크롭 + ffmpeg 고화질 스케일 완료.");
            }

            if (File.Exists(cropPng)) { try { File.Copy(cropPng, outPng, true); TryDelete(cropPng); } catch { } }
            return File.Exists(outPng)
                ? new(true, outPng, method, false, fyPct, $"{method} 얼굴 크롭 완료(크롭본).")
                : new(false, null, method, false, fyPct, "썸네일 산출에 실패했습니다.");
        }
        catch (OperationCanceledException) { return new(false, null, "Heuristic", false, HeuristicFaceY, "취소되었습니다."); }
        catch (Exception ex) { Program.Log("LlmVisionCrop.Crop", ex); return new(false, null, "Heuristic", false, HeuristicFaceY, "크롭 실패: " + ex.Message); }
    }

    /// <summary>썸네일 재생 URL(가상호스트).</summary>
    public string? PlaybackUrl(string? path) => string.IsNullOrEmpty(path) || !File.Exists(path) ? null : media.ToPlaybackUrl(path);

    // ── Gemini Vision 얼굴 중심 추론(폴백 포함) ──
    private async Task<(double FxPct, double FyPct, string Method)> InferFaceCenterAsync(string probeJpg, IProgress<string>? log, CancellationToken ct)
    {
        if (!gemini.Enabled) return (50.0, HeuristicFaceY, "Heuristic");
        try
        {
            var bytes = await File.ReadAllBytesAsync(probeJpg, ct).ConfigureAwait(false);
            if (bytes.LongLength == 0 || bytes.LongLength > GeminiClient.MaxInlineBytes) return (50.0, HeuristicFaceY, "Heuristic");

            log?.Report("Gemini Vision 얼굴 위치 추론 중…");
            const string instruction =
                "You are a precise vision assistant. The image is one frame from a baby short-form video. " +
                "Locate the baby's face. Respond ONLY with strict JSON, no prose: " +
                "{\"faceCenterXPct\": <number 0-100, horizontal center of the face as percent of image width>, " +
                "\"faceCenterYPct\": <number 0-100, vertical center of the face as percent of image height>, " +
                "\"faceWidthPct\": <number 0-100, face width as percent of image width>, " +
                "\"confidence\": <number 0-1>}. If no face is visible, set confidence to 0.";

            var res = await gemini.GenerateAsync(instruction, new[] { new GeminiMedia("image/jpeg", bytes) }, 0.2, ct).ConfigureAwait(false);
            if (!res.Ok || res.Text is null) return (50.0, HeuristicFaceY, "Heuristic");

            if (TryParseFace(res.Text, out var fx, out var fy, out var conf) && conf >= 0.3)
                return (fx, fy, "Gemini Vision");
            return (50.0, HeuristicFaceY, "Heuristic");
        }
        catch (Exception ex) { Program.Log("LlmVisionCrop.Infer", ex); return (50.0, HeuristicFaceY, "Heuristic"); }
    }

    /// <summary>Gemini JSON 에서 얼굴 중심(X/Y%)·confidence 파싱(펜스 제거·숫자/문자 모두 허용). 실패 시 false.</summary>
    public static bool TryParseFace(string raw, out double fxPct, out double fyPct, out double confidence)
    {
        fxPct = 50; fyPct = HeuristicFaceY; confidence = 0;
        try
        {
            using var doc = JsonDocument.Parse(GeminiClient.StripFence(raw));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            fxPct = ReadNum(root, "faceCenterXPct", 50);
            fyPct = ReadNum(root, "faceCenterYPct", HeuristicFaceY);
            confidence = ReadNum(root, "confidence", 0);
            fxPct = Math.Clamp(fxPct, 0, 100);
            fyPct = Math.Clamp(fyPct, 0, 100);
            confidence = Math.Clamp(confidence, 0, 1);
            return true;
        }
        catch { return false; }
    }

    /// <summary>얼굴 중심(X/Y%)으로 1.5배 매크로 크롭 ffmpeg vf 문자열을 만든다. 중심은 [1/3,2/3]로 클램프해 크롭 박스가 항상 프레임 내부.</summary>
    public static string BuildCropVf(double fxPct, double fyPct, int scaleW = 720)
    {
        // 크롭 폭/높이 = iw/1.5, ih/1.5 → 반폭=iw/3. 중심 비율을 [1/3, 2/3]로 클램프하면 offset ∈ [0, iw/3](프레임 내부).
        var fx = Math.Clamp(fxPct / 100.0, 1.0 / 3.0, 2.0 / 3.0);
        var fy = Math.Clamp(fyPct / 100.0, 1.0 / 3.0, 2.0 / 3.0);
        // x_offset = iw*fx - iw/3, y_offset = ih*fy - ih/3 (min/max·콤마 이스케이프 없이 안전).
        return $"crop=iw/1.5:ih/1.5:iw*{F(fx)}-iw/3:ih*{F(fy)}-ih/3,scale={scaleW}:-2";
    }

    // ── 표정 피크 컷 선정(AutoThumbnail 과 동일 규칙) ──
    private static StoryboardScene? PickPeakScene(StoryboardProject project)
    {
        StoryboardScene? peak = null; var best = -1;
        var scenes = project.Scenes;
        for (var i = 0; i < scenes.Count; i++)
        {
            var s = scenes[i];
            if (string.IsNullOrEmpty(s.LocalPath) || !File.Exists(s.LocalPath)) continue;
            var t = s.PresetTension ?? s.EmotionTension ?? (i is 2 or 3 ? 72 : 40);
            if (t > best) { best = t; peak = s; }
        }
        return peak;
    }

    private static double ReadNum(JsonElement o, string name, double fallback)
    {
        if (!o.TryGetProperty(name, out var v)) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String => double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback,
            _ => fallback,
        };
    }

    private static string F(double d) => d.ToString("0.####", CultureInfo.InvariantCulture);
    private static string Inv(double d) => d.ToString(CultureInfo.InvariantCulture);
    private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

    private static async Task<bool> RunFfmpegAsync(string ffmpeg, string[] args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = ffmpeg, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var proc = new Process { StartInfo = psi };
            proc.ErrorDataReceived += (_, __) => { };
            proc.OutputDataReceived += (_, __) => { };
            if (!proc.Start()) return false;
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();
            try { await proc.WaitForExitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { } return false; }
            return proc.ExitCode == 0;
        }
        catch (Exception ex) { Program.Log("LlmVisionCrop.Ffmpeg", ex); return false; }
    }
}
