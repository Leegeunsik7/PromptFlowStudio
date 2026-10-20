using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Media;

namespace PromptFlowStudio.Services.Wan;

/// <summary>컷 구간 선정 전략 — 사용자가 "어떤 식으로 변환할지" 직접 고를 수 있는 실질 옵션(각기 다른 산식으로 매핑).</summary>
public enum CutdownStrategy
{
    /// <summary>AI 추천(기본) — 초과분의 25% 만 도입부에서 컷 + 75%는 엔딩(클라이맥스) 앞에서 컷.</summary>
    Balanced,
    /// <summary>훅 우선 — 도입부(0초)부터 목표 길이만큼 유지(엔딩을 통째로 컷).</summary>
    HookPriority,
    /// <summary>클라이맥스 우선 — 엔딩까지 목표 길이만큼 유지(도입부를 통째로 컷).</summary>
    ClimaxPriority,
    /// <summary>수동 지정 — 사용자가 지정한 시작 지점부터 목표 길이만큼 유지.</summary>
    Manual,
}

/// <summary>출력 인코딩 품질 프리셋 — QualityGuardrailPreset(Wan 렌더)과 동일한 3단 톤.</summary>
public enum CutdownQuality { Fast, Balanced, High }

/// <summary>15초 컷다운 계획 — 원본 길이/목표 길이/선정된 컷 구간(초)/선정 근거(전략별로 문구가 다름).</summary>
public sealed record CutdownPlan(double SourceDurationSec, double TargetDurationSec, double StartSec, double EndSec, string Strategy);

/// <summary>컷다운 실행 결과 — Ok=false 라도 Plan 은 채워져 사유 확인 가능(Graceful).</summary>
public sealed record CutdownResult(bool Ok, string? OutputPath, string? PlaybackUrl, CutdownPlan? Plan, string Note);

/// <summary>
/// [PromptFlow Studio v5.8.3] AI 숏폼 컷다운 — 15~30초 원본을 15초 숏폼 1편으로 자동 재단(데스크톱 전용).
///   ▸ 로컬 경로/HTTP URL 수신(URL 이면 미디어 폴더로 스트리밍 다운로드) → ffmpeg stderr 의 <c>Duration:</c> 라인 파싱으로
///     실길이 프로브(별도 ffprobe 불요) → <see cref="CutdownStrategy"/> 4종 중 사용자가 고른 방식으로 단일 15초 구간을
///     선정(목표 이하면 무손실 파일 복사) → <see cref="CutdownQuality"/> 프리셋에 따라 실컷(libx264 재인코딩).
///   ▸ ffmpeg 미탑재/실패 시 계획(Plan)만 반환하고 Ok=false — LongToShortsConverter/FfmpegFrameExtractor 와 동일한
///     스텁 정합·Graceful 폴백 기조(빈 화면/예외 전파 없음).
/// </summary>
public sealed class AiShortsCutdownEngine(ILocalMediaService media)
{
    public const double TargetDurationSec = 15.0;

    private static readonly HttpClient _shared = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly Regex _durationRx = new(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);

    private long _runs, _fallbacks;
    public long Runs => Interlocked.Read(ref _runs);
    public long Fallbacks => Interlocked.Read(ref _fallbacks);
    public event Action? OnChange;
    public string? LastAction { get; private set; }

    /// <summary>전략별 컷 구간 산정 — Manual 이 아니면 <paramref name="manualStartSec"/> 는 무시.</summary>
    public static CutdownPlan PlanCutdown(double sourceDurationSec, double targetSec = TargetDurationSec,
        CutdownStrategy strategy = CutdownStrategy.Balanced, double manualStartSec = 0)
    {
        var src = Math.Max(sourceDurationSec, 0);
        if (src <= targetSec || src <= 0)
            return new CutdownPlan(Math.Round(src, 2), targetSec, 0, Math.Round(src, 2), "이미 목표 길이 이하 — 원본 그대로 사용");

        var trimTotal = src - targetSec;
        double start;
        string label;
        switch (strategy)
        {
            case CutdownStrategy.HookPriority:
                start = 0;
                label = "훅 우선 — 도입부(0s)부터 목표 길이만큼 유지, 엔딩은 컷";
                break;
            case CutdownStrategy.ClimaxPriority:
                start = trimTotal;
                label = "클라이맥스 우선 — 엔딩까지 목표 길이만큼 유지, 도입부는 컷";
                break;
            case CutdownStrategy.Manual:
                start = Math.Clamp(manualStartSec, 0, trimTotal);
                label = $"수동 지정 — {start:F1}s 지점부터 목표 길이만큼 유지";
                break;
            default:
                start = Math.Round(trimTotal * 0.25, 2);   // 초과분의 25% 만 도입부에서 컷(훅 보존)
                label = $"AI 추천(균형) — 도입부 {start:F1}s 컷 + 클라이맥스 유지 {targetSec:F0}s 구간";
                break;
        }
        var end = Math.Round(start + targetSec, 2);
        return new CutdownPlan(Math.Round(src, 2), targetSec, Math.Round(start, 2), end, label);
    }

    private static (int Crf, string Preset) QualityParams(CutdownQuality q) => q switch
    {
        CutdownQuality.Fast => (26, "veryfast"),
        CutdownQuality.High => (16, "slow"),
        _ => (20, "veryfast"),
    };

    /// <summary>ffmpeg 를 출력 없이 1회 실행해 stderr 의 Duration 라인만 파싱(ffprobe 불요). 실패 시 0.</summary>
    public async Task<double> ProbeDurationSecAsync(string videoPath, string? customFfmpegPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath)) return 0;
#if WINDOWS
        try
        {
            var ffmpegExe = !string.IsNullOrWhiteSpace(customFfmpegPath) && File.Exists(customFfmpegPath) ? customFfmpegPath! : "ffmpeg";
            using var proc = new System.Diagnostics.Process();
            proc.StartInfo.FileName = ffmpegExe;
            proc.StartInfo.Arguments = $"-i \"{videoPath}\"";
            proc.StartInfo.CreateNoWindow = true;
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.RedirectStandardError = true;
            proc.Start();
            var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var m = _durationRx.Match(stderr);
            if (m.Success)
            {
                var h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var mi = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                var s = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                return h * 3600 + mi * 60 + s;
            }
        }
        catch (Exception ex) { Program.Log("AiShortsCutdown.Probe", ex); }
#else
        await Task.CompletedTask; // 모바일: ffmpeg 프로세스 미지원 → 0 폴백
#endif
        return 0;
    }

    /// <summary>원본(경로/URL) → 15초 숏폼 1편. 이미 15초 이하면 무손실 복사, 초과분은 ffmpeg 실컷(재인코딩).</summary>
    public async Task<CutdownResult> ExecuteCutdownAsync(string sourceUrlOrPath, string? customFfmpegPath, double targetSec = TargetDurationSec,
        CutdownStrategy strategy = CutdownStrategy.Balanced, double manualStartSec = 0, CutdownQuality quality = CutdownQuality.Balanced, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceUrlOrPath))
            return new CutdownResult(false, null, null, null, "입력 경로/URL 이 비어 있습니다.");

        // 1) URL 이면 미디어 폴더로 스트리밍 다운로드(LongToShortsConverter 와 동일 전략).
        var localPath = sourceUrlOrPath;
        var isUrl = sourceUrlOrPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || sourceUrlOrPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (isUrl)
        {
            try
            {
                var importDir = Path.Combine(media.MediaRoot, "ai-cutdown-import");
                Directory.CreateDirectory(importDir);
                localPath = Path.Combine(importDir, $"import_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
                using var resp = await _shared.GetAsync(sourceUrlOrPath, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(localPath);
                await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _fallbacks);
                LastAction = "원본 다운로드 실패 — " + ex.Message;
                OnChange?.Invoke();
                return new CutdownResult(false, null, null, null, "원본 다운로드 실패: " + ex.Message);
            }
        }

        if (!File.Exists(localPath))
            return new CutdownResult(false, null, null, null, "원본 파일을 찾을 수 없습니다: " + localPath);

        // 2) 실길이 프로브 → 사용자가 고른 전략으로 컷 구간 계획.
        var dur = await ProbeDurationSecAsync(localPath, customFfmpegPath, ct).ConfigureAwait(false);
        if (dur <= 0)
        {
            Interlocked.Increment(ref _fallbacks);
            LastAction = "ffmpeg 길이 프로브 실패 — 미탑재이거나 손상된 파일";
            OnChange?.Invoke();
            return new CutdownResult(false, null, null, null, "ffmpeg 로 원본 길이를 확인할 수 없습니다(ffmpeg 미탑재이거나 손상된 파일).");
        }
        var plan = PlanCutdown(dur, targetSec, strategy, manualStartSec);

        try
        {
            var outDir = Path.Combine(media.MediaRoot, "ai-cutdown-out");
            Directory.CreateDirectory(outDir);
            var outputPath = Path.Combine(outDir, $"cutdown15_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

            if (plan.StartSec <= 0.05 && plan.EndSec >= plan.SourceDurationSec - 0.05)
            {
                // 이미 목표 길이 이하 — ffmpeg 불요, 무손실 파일 복사만으로 완결(ffmpeg 미탑재 환경에서도 동작).
                File.Copy(localPath, outputPath, overwrite: true);
            }
            else
            {
#if WINDOWS
                var (crf, preset) = QualityParams(quality);
                var ffmpegExe = !string.IsNullOrWhiteSpace(customFfmpegPath) && File.Exists(customFfmpegPath) ? customFfmpegPath! : "ffmpeg";
                using var proc = new System.Diagnostics.Process();
                proc.StartInfo.FileName = ffmpegExe;
                proc.StartInfo.Arguments =
                    $"-y -ss {plan.StartSec.ToString("F2", CultureInfo.InvariantCulture)} -i \"{localPath}\" " +
                    $"-t {plan.TargetDurationSec.ToString("F2", CultureInfo.InvariantCulture)} -c:v libx264 -preset {preset} -crf {crf.ToString(CultureInfo.InvariantCulture)} -c:a aac -movflags +faststart \"{outputPath}\"";
                proc.StartInfo.CreateNoWindow = true;
                proc.StartInfo.UseShellExecute = false;
                proc.Start();
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
#endif
                if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                {
                    Interlocked.Increment(ref _fallbacks);
                    LastAction = "ffmpeg 실행 실패 — 계획만 산출(Graceful)";
                    OnChange?.Invoke();
                    return new CutdownResult(false, null, null, plan, "ffmpeg 실행 실패 — 계획은 산출됐으나 실제 컷은 수행되지 않았습니다.");
                }
            }

            Interlocked.Increment(ref _runs);
            LastAction = $"컷다운 완료 · {plan.SourceDurationSec:F1}s → {plan.TargetDurationSec:F0}s · {strategy}/{quality}";
            OnChange?.Invoke();
            return new CutdownResult(true, outputPath, media.ToPlaybackUrl(outputPath), plan, "컷다운 완료");
        }
        catch (Exception ex)
        {
            Program.Log("AiShortsCutdown.Execute", ex);
            Interlocked.Increment(ref _fallbacks);
            LastAction = "실행 예외 — " + ex.Message;
            OnChange?.Invoke();
            return new CutdownResult(false, null, null, plan, "실행 중 예외: " + ex.Message);
        }
    }
}
