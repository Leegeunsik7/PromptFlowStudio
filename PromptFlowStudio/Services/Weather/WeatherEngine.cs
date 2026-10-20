using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;   // ProcessRunner / CliPathResolver
using PromptFlowStudio.Services.Media;     // SpatialAudioEngine (pan 연동)

namespace PromptFlowStudio.Services.Weather;

/// <summary>날씨 앰비언스 적용 결과 — 적용 여부 · 사용 필터 · 사유.</summary>
public sealed record WeatherAmbienceResult(bool Applied, string Filter, string Message);

/// <summary>기후 앰비언스 노이즈 스펙 — anoisesrc 색·진폭 + 셰이핑 체인(순수 산출물).</summary>
public sealed record AmbienceSpec(string Color, double Amplitude, string Shape);

/// <summary>
/// [PromptFlowStudio] v4.5.0 Task2 · 날씨 프롬프트 &amp; 입체 음향 합성 엔진 — 순수 BCL + FFmpeg CLI(외부 NuGet 0).
///   ▸ <b>시네마틱 프롬프트 합성</b>(<see cref="SynthesizePrompt"/>): 기후×시간대×강도 → Wan 1.3B/3.0 최적 영문 프롬프트("torrential rainstorm with lightning", "volumetric dense fog" 등).
///   ▸ <b>기후 앰비언스 믹싱</b>(<see cref="ApplyAmbienceAsync"/>): <c>anoisesrc</c>(빗소리=white·바람=brown)를 EQ/트레몰로로 셰이핑해 원음과 <c>amix</c>,
///     기존 <see cref="SpatialAudioEngine"/>의 등파워 <c>pan</c> 과 한 패스로 연동. ffmpeg 부재/실패는 원음 패스스루로 Graceful(영상 유지).
/// </summary>
public sealed class WeatherEngine
{
    /// <summary>FFmpeg 프로세스 상한 — 무응답이어도 이 시간 안에 종료(하드닝 기조 계승).</summary>
    public TimeSpan ProcessTimeout { get; init; } = TimeSpan.FromSeconds(180);

    // ══════════════════════ 프롬프트 합성(순수 · 테스트 대상) ══════════════════════

    /// <summary>날씨 구성 → Wan 최적 영문 시네마틱 프롬프트. <paramref name="subject"/>(선택)가 있으면 앞에 배치.</summary>
    public string SynthesizePrompt(WeatherThemeConfig? cfg, string? subject = null)
    {
        cfg ??= new WeatherThemeConfig();   // null 방어
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(subject)) parts.Add(subject!.Trim());
        parts.Add(ClimatePhrase(cfg.Climate, cfg.Intensity));
        parts.Add(TimePhrase(cfg.TimeOfDay));
        parts.Add("cinematic, volumetric lighting, atmospheric depth, ultra-detailed, sharp focus, 4k, film grain, professional color grading");
        return string.Join(", ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    /// <summary>안정화 네거티브 프롬프트(Wan 결함 억제 · 기존 방침과 정렬).</summary>
    public string BuildNegativePrompt(WeatherThemeConfig? _ = null)
        => "blurry, low quality, overexposed, washed out, banding, artifacts, distorted, watermark, text, jpeg noise";

    private static string ClimatePhrase(WeatherClimate c, int intensity)
    {
        var tier = intensity <= 33 ? 0 : intensity <= 66 ? 1 : 2;   // 약/보통/강
        return c switch
        {
            WeatherClimate.Clear => tier switch
            {
                0 => "clear calm sky, gentle weather",
                1 => "bright clear blue sky with soft clouds",
                _ => "brilliant crystal-clear sky, vivid sunlight, crisp fresh air",
            },
            WeatherClimate.Rain => tier switch
            {
                0 => "light drizzle, gentle rain, subtle wet surfaces",
                1 => "steady rainfall, wet reflective streets, rain droplets on surfaces",
                _ => "torrential rainstorm with lightning, heavy downpour, dramatic storm clouds, splashing water",
            },
            WeatherClimate.Snow => tier switch
            {
                0 => "gentle light snowfall, soft drifting snowflakes",
                1 => "steady snowfall, snow-covered ground, calm winter scene",
                _ => "heavy blizzard, thick falling snow, whiteout conditions, blowing snowdrifts",
            },
            WeatherClimate.Fog => tier switch
            {
                0 => "thin drifting mist, light morning haze",
                1 => "rolling fog, soft atmospheric haze, muted distance",
                _ => "volumetric dense fog, thick low-lying mist, near-zero visibility, moody haze",
            },
            _ => "atmospheric weather",
        };
    }

    private static string TimePhrase(WeatherTimeOfDay t) => t switch
    {
        WeatherTimeOfDay.Dawn => "at dawn, golden sunrise glow, soft warm rim light",
        WeatherTimeOfDay.Noon => "at midday, bright natural daylight",
        WeatherTimeOfDay.Sunset => "during golden hour, warm orange sunset light, long shadows",
        WeatherTimeOfDay.Night => "at night, moonlit, deep blue nocturnal tones, ambient lights",
        _ => "",
    };

    // ══════════════════════ 앰비언스 스펙(순수 · 테스트 대상) ══════════════════════

    /// <summary>기후 → anoisesrc 색/진폭 + 셰이핑 체인. 강도(1~100)에 비례해 진폭 상승(클램프).</summary>
    public static AmbienceSpec BuildAmbienceSpec(WeatherThemeConfig cfg)
    {
        cfg ??= new WeatherThemeConfig();
        var i = Math.Clamp(cfg.Intensity, 1, 100) / 100.0;
        return cfg.Climate switch
        {
            // 빗소리 — 화이트 노이즈를 밴드패스(하이패스+로우패스)로 다듬음. 강할수록 진폭↑.
            WeatherClimate.Rain => new AmbienceSpec("white", Clamp(0.05 + i * 0.50, 0.05, 0.55), "highpass=f=300,lowpass=f=9500"),
            // 눈 — 실제로 매우 조용. 핑크 노이즈를 로우패스로 뭉갠 미세한 히스.
            WeatherClimate.Snow => new AmbienceSpec("pink", Clamp(0.02 + i * 0.12, 0.02, 0.14), "lowpass=f=2500"),
            // 안개 — 브라운 노이즈 저역 럼블 + 느린 트레몰로(바람 결).
            WeatherClimate.Fog => new AmbienceSpec("brown", Clamp(0.03 + i * 0.22, 0.03, 0.25), "lowpass=f=520,tremolo=f=0.15:d=0.85"),
            // 맑음 — 산들바람 정도의 아주 약한 브라운 노이즈.
            _ => new AmbienceSpec("brown", Clamp(0.02 + i * 0.12, 0.02, 0.14), "lowpass=f=900,tremolo=f=0.1:d=0.5"),
        };
    }

    private static double Clamp(double v, double lo, double hi) => Math.Min(hi, Math.Max(lo, v));

    // ══════════════════════ 앰비언스 적용(원음 + 기후음 amix, 선택적 pan) ══════════════════════

    /// <summary>
    /// 입력 클립의 원음에 기후 앰비언스를 <c>amix</c> 로 합성한다. <paramref name="pan"/>(-1..1) 지정 시 <see cref="SpatialAudioEngine"/>
    /// 등파워 팬을 같은 패스에 결합. 비활성/앰비언스 off 는 no-op, ffmpeg 부재/실패는 원음 패스스루(영상 반드시 유지 · Graceful).
    /// </summary>
    public async Task<WeatherAmbienceResult> ApplyAmbienceAsync(
        string inputClip, string outPath, WeatherThemeConfig? cfg, double? pan = null, CancellationToken ct = default)
    {
        cfg ??= new WeatherThemeConfig();
        if (string.IsNullOrWhiteSpace(inputClip) || !File.Exists(inputClip))
            return new WeatherAmbienceResult(false, "", "입력 클립을 찾을 수 없습니다: " + inputClip);

        if (!cfg.Enabled || !cfg.Ambience)
            return new WeatherAmbienceResult(false, "", "날씨 앰비언스 비활성 — 원음 유지(no-op).");

        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null)
            return await PassthroughAsync(inputClip, outPath, "ffmpeg 미설치 — 앰비언스 스킵(원음 유지).", ct).ConfigureAwait(false);

        var spec = BuildAmbienceSpec(cfg);
        var ci = CultureInfo.InvariantCulture;

        // filter_complex: [1]노이즈 셰이핑 → [amb], 원음[0:a] 와 amix → (선택)pan → [mixed]
        var fc = $"[1:a]{spec.Shape}[amb];[0:a][amb]amix=inputs=2:duration=first:dropout_transition=0";
        if (pan is double pv)
            fc += $"[mx];[mx]{SpatialAudioEngine.BuildBinauralPan(pv)}[mixed]";
        else
            fc += "[mixed]";

        try
        {
            var dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var noise = $"anoisesrc=color={spec.Color}:amplitude={spec.Amplitude.ToString("0.###", ci)}:r=48000";
            var args = new List<string>
            {
                "-y", "-i", inputClip,
                "-f", "lavfi", "-i", noise,
                "-filter_complex", fc,
                "-map", "0:v:0", "-map", "[mixed]",
                "-c:v", "copy", "-c:a", "aac", "-b:a", "192k",
                "-shortest", "-movflags", "+faststart", outPath,
            };

            using var opCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            opCts.CancelAfter(ProcessTimeout);   // ⏱ 무응답 방어(하드닝 기조)
            var res = await ProcessRunner.RunAsync(ffmpeg, args, workingDir: null, onLine: null, ct: opCts.Token).ConfigureAwait(false);

            if (res.Ok && File.Exists(outPath) && new FileInfo(outPath).Length > 0)
                return new WeatherAmbienceResult(true, fc, $"{WeatherThemeCatalog.ClimateLabel(cfg.Climate)} 앰비언스 합성 완료");

            // 실패(원음 트랙 없음 등) → 원음 패스스루로 영상 완수.
            SafeDelete(outPath);
            if (ct.IsCancellationRequested) return new WeatherAmbienceResult(false, fc, "취소됨");
            return await PassthroughAsync(inputClip, outPath, "앰비언스 합성 실패 — 원음으로 스킵(영상 유지).", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Program.Log("WeatherEngine.ApplyAmbience", ex);
            SafeDelete(outPath);
            return await PassthroughAsync(inputClip, outPath, "앰비언스 예외 — 원음으로 스킵: " + ex.Message, ct).ConfigureAwait(false);
        }
    }

    private static Task<WeatherAmbienceResult> PassthroughAsync(string inp, string outPath, string why, CancellationToken ct)
    {
        try
        {
            var dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var same = string.Equals(Path.GetFullPath(inp), Path.GetFullPath(outPath), StringComparison.OrdinalIgnoreCase);
            if (!same) File.Copy(inp, outPath, overwrite: true);
            return Task.FromResult(new WeatherAmbienceResult(false, "", why));
        }
        catch (Exception ex)
        {
            Program.Log("WeatherEngine.Passthrough", ex);
            return Task.FromResult(new WeatherAmbienceResult(false, "", why + " · 패스스루 실패: " + ex.Message));
        }
    }

    private static void SafeDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { /* 무시 */ } }

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
