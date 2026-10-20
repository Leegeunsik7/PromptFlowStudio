using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;   // ProcessRunner / CliPathResolver

namespace PromptFlowStudio.Services.Wan;

/// <summary>
/// 무료 결함 보정 옵션 — 각 필터 단계의 on/off 와 모션 보간 목표 FPS. 전부 안전 기본값(원클릭 Auto-Pilot).
/// </summary>
/// <param name="Deflicker">프레임 깜빡임 제거(deflicker) + 시간축 디노이즈(hqdn3d).</param>
/// <param name="Sharpen">언샤프 마스크 + Lanczos 리샘플로 선명도 복원.</param>
/// <param name="Interpolate">모션 보상 프레임 보간(minterpolate) — 무거우므로 기본 off.</param>
/// <param name="TargetFps">보간 목표 FPS(12~60 클램프). 기본 30.</param>
/// <param name="TemporalSr">시간적 슈퍼 레졸루션 — 인접 프레임 가중 평균(tmix)으로 시간축 노이즈를 저감해 프로급 매끄러움을 얻는다. 기본 true.</param>
/// <param name="Deband">디밴딩 색상 안정화 — 하늘/그라데이션의 계단식 컬러 밴딩(deband)을 억제해 계조를 부드럽게 만든다. 기본 true.</param>
public sealed record WanPostOptions(
    bool Deflicker = true, bool Sharpen = true, bool Interpolate = false, int TargetFps = 30,
    bool TemporalSr = true, bool Deband = true);

/// <summary>
/// 후처리 결과. <see cref="Applied"/>=false 면 보정 없이 원본을 그대로 쓰라는 뜻(ffmpeg 부재/적용필터 없음 · Graceful).
/// </summary>
public sealed record WanPostResult(bool Ok, bool Applied, string? OutputPath, string FilterChain, string Message);

/// <summary>
/// [PromptFlowStudio] Wan 2.1 무료 생성물 자동 디플리커 &amp; 프레임 모션 보정기 — 순수 FFmpeg CLI(외부 NuGet 0 · <see cref="ProcessRunner"/> 공유).
///   ▸ 무료 모델 특유의 <b>깜빡임(Flicker)</b>을 <c>deflicker</c> 로 제거하고, <c>hqdn3d</c> 시간축 디노이즈 + <c>unsharp</c> 선명화 + <c>scale=…:flags=lanczos</c> 고품질 리샘플로 화질을 복원한다.
///   ▸ <b>시간적 슈퍼 레졸루션</b>(<c>tmix</c> 중앙 가중 3프레임 평균)으로 시간축 노이즈를, <b>디밴딩</b>(<c>deband</c>)으로 하늘/그라데이션의 컬러 밴딩을 억제해 유료(Pro) 모델급 매끄러움을 얻는다.
///   ▸ 옵션으로 <c>minterpolate</c>(모션 보상 프레임 보간)를 켜 저 FPS 뭉개짐을 부드럽게 만든다(무거워 기본 off).
///   ▸ <b>병합 단계 조건부 자동 실행</b> 설계 — <see cref="Storyboard.StoryboardJobManager"/> 가 업스케일과 번인 사이(각 컷 단위)에서 호출한다.
///   ▸ Graceful: ffmpeg 부재/실패/취소 시 예외 없이 <see cref="WanPostResult.Applied"/>=false 로 원본을 유지한다.
///   ⚠ 필터 문자열은 <b>내부 상수 조합</b>만 사용(외부 입력 미개입) — 필터그래프 인젝션 표면이 없다.
/// </summary>
public sealed class WanVideoPostEngine
{
    private const double ClipSeconds = 5.0;   // Wan 컷 길이(고정) — 진행률 추정 분모

    /// <summary>ffmpeg 실행 파일을 찾을 수 있는지(exe 옆 → Tools 번들/로컬앱데이터 → PATH).</summary>
    public bool IsAvailable => ResolveFfmpeg() is not null;

    /// <summary>
    /// 입력 영상에 무료 결함 보정 파이프라인을 적용해 <paramref name="outputPath"/> 로 저장한다.
    ///   적용할 필터가 없거나 ffmpeg 가 없으면 <see cref="WanPostResult.Applied"/>=false 로 원본을 유지(호출부는 입력 경로를 계속 사용).
    /// </summary>
    public async Task<WanPostResult> StabilizeAsync(
        string inputPath, string outputPath, WanPostOptions? options = null,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var opt = Normalize(options);
        var chain = BuildFilterChain(opt);

        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
            return new(false, false, inputPath, chain, "입력 영상을 찾을 수 없습니다.");
        if (chain.Length == 0)
            return new(true, false, inputPath, chain, "적용할 보정 항목이 없습니다(원본 유지).");

        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null)
            return new(true, false, inputPath, chain, "ffmpeg 미탐지 — 보정을 건너뜁니다(원본 유지).");

        try
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var args = new List<string>
            {
                "-y",
                "-i", inputPath,
                "-vf", chain,
                "-c:v", "libx264",
                "-pix_fmt", "yuv420p",
                "-preset", "veryfast",
                "-crf", "18",                       // 보정 단계는 고화질 유지(병합에서 최종 재인코딩)
                "-c:a", "copy",                     // 오디오는 무변경(원본 트랙 보존)
                "-movflags", "+faststart",
                "-progress", "pipe:1",
                outputPath,
            };

            var lastPct = -1;
            void OnLine(bool isErr, string line)
            {
                if (progress is null || line is null) return;
                // -progress stdout: out_time_us=microsec
                var idx = line.IndexOf("out_time_us=", StringComparison.Ordinal);
                if (idx < 0) return;
                var val = line[(idx + "out_time_us=".Length)..].Trim();
                if (!long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) || us < 0) return;
                var pct = (int)Math.Clamp(us / 1_000_000.0 / ClipSeconds * 100.0, 0, 99);
                if (pct != lastPct) { lastPct = pct; progress.Report(pct); }
            }

            var res = await ProcessRunner.RunAsync(ffmpeg, args, workingDir: null, onLine: OnLine, ct: ct).ConfigureAwait(false);

            if (res.ExitCode == -2)   // 취소
                return new(false, false, inputPath, chain, "보정이 취소되었습니다(원본 유지).");
            if (!res.Ok || !File.Exists(outputPath))
                return new(false, false, inputPath, chain, "ffmpeg 보정 실패 — 원본을 유지합니다: " + Tail(res.StdErr));

            progress?.Report(100);
            return new(true, true, outputPath, chain, "무료 결함 보정 완료");
        }
        catch (OperationCanceledException)
        {
            return new(false, false, inputPath, chain, "보정이 취소되었습니다(원본 유지).");
        }
        catch (Exception ex)
        {
            Program.Log("WanVideoPostEngine.Stabilize", ex);
            return new(false, false, inputPath, chain, "보정 실패 — 원본을 유지합니다: " + ex.Message);
        }
    }

    /// <summary>
    /// 옵션에 따라 <c>-vf</c> 필터 체인을 구성한다(내부 상수만 · 순서: 디노이즈→디플리커→보간→샤픈→Lanczos 짝수 정렬).
    ///   전부 off 면 빈 문자열(=보정 없음).
    /// </summary>
    public static string BuildFilterChain(WanPostOptions? options)
    {
        var opt = Normalize(options);
        var parts = new List<string>();

        if (opt.Deflicker)
        {
            parts.Add("hqdn3d=1.5:1.2:6:6");             // 시간축 디노이즈(깜빡임 유발 노이즈 완화)
            parts.Add("deflicker=mode=pm:size=5");       // 프레임 깜빡임 제거(5프레임 창 평균)
        }
        if (opt.TemporalSr)
            parts.Add("tmix=frames=3:weights=1 2 1");    // 시간적 슈퍼 레졸루션(중앙 가중 3프레임 평균 — 잔상 최소·시간축 노이즈 저감)
        if (opt.Deband)
            parts.Add("deband=1thr=0.02:2thr=0.02:3thr=0.02:4thr=0.02:range=16:blur=1");   // 컬러 밴딩 억제(계조 안정화)
        if (opt.Interpolate)
            parts.Add($"minterpolate=fps={opt.TargetFps.ToString(CultureInfo.InvariantCulture)}:mi_mode=mci:mc_mode=aobmc:me_mode=bidir:vsbmc=1");
        if (opt.Sharpen)
            parts.Add("unsharp=5:5:0.8:5:5:0.0");        // 언샤프 마스크(윤곽 선명화)

        if (parts.Count == 0) return "";

        // 항상 짝수 해상도 보장 + Lanczos 고품질 리샘플(H.264 요건 · 화질 손실 최소화).
        parts.Add("scale=trunc(iw/2)*2:trunc(ih/2)*2:flags=lanczos");
        return string.Join(",", parts);
    }

    /// <summary>옵션 정규화(null → 기본값, FPS 12~60 클램프).</summary>
    private static WanPostOptions Normalize(WanPostOptions? o)
    {
        o ??= new WanPostOptions();
        var fps = Math.Clamp(o.TargetFps <= 0 ? 30 : o.TargetFps, 12, 60);
        return o with { TargetFps = fps };
    }

    /// <summary>ffmpeg 위치 결정 — exe 옆(배포 관례) 우선 → CliPathResolver 3단계(번들/로컬앱데이터/PATH). 못 찾으면 null.</summary>
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

    /// <summary>stderr 꼬리(오류 메시지용).</summary>
    private static string Tail(string? s, int n = 400) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s.Trim() : "…" + s[^n..].Trim());
}
