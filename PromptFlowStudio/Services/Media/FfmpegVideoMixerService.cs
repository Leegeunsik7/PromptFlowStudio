using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;

namespace PromptFlowStudio.Services.Media;

/// <summary>v16.12.0 컷 경계 트랜지션.</summary>
public enum PostTransition { Cut, Crossfade, FadeInOut }

/// <summary>
/// v16.12.0 영상 병합 계획 — 입력 MP4(순서) · 컷별 길이(초) · 출력 해상도/fps · 트랜지션.
///   <see cref="ClipSeconds"/> 는 입력과 1:1(누락/0 이하는 <see cref="FfmpegVideoMixerService.DefaultClipSeconds"/>).
/// </summary>
public sealed record VideoMixPlan(
    IReadOnlyList<string> Inputs, string OutputPath, int Width, int Height, int Fps = 30,
    PostTransition Transition = PostTransition.Crossfade, double TransitionSeconds = 0.5,
    IReadOnlyList<double>? ClipSeconds = null);

/// <summary>v16.12.0 병합 타임라인 — 컷별 시작 시각(초, 트랜지션 겹침 반영) + 총 길이. 오디오 믹서가 SFX 배치에 사용.</summary>
public sealed record MixTimeline(IReadOnlyList<double> CutStarts, double TotalSeconds);

/// <summary>v16.12.0 믹서 실행 결과.</summary>
public sealed record MixResult(bool Ok, string? OutputPath, string Message);

/// <summary>
/// [PromptFlowStudio] v16.12.0 🎞️ 비디오 트랜지션·심리스 병합 엔진 — ffmpeg filter_complex 래퍼(순수 BCL · 외부 NuGet 0).
///   ▸ 정규화: 입력마다 scale(비율 유지)+pad(레터박스)+setsar=1+fps+yuv420p+settb → 해상도/프레임레이트/타임베이스 통일(xfade 전제).
///   ▸ Crossfade: xfade 체인(offset = 누적 길이 − 겹침) · FadeInOut: 컷별 fade in/out(dip-to-black) 후 concat · Cut: 순수 concat.
///   ▸ 출력은 영상 전용(-an) 중간 산출물 — 오디오(BGM/SFX)는 <see cref="FfmpegAudioMixerService"/> 가 먹싱.
///   ▸ 인자는 리스트로 생성(ArgumentList → 공백 경로 안전) · <see cref="BuildArgs"/> 는 순수 함수(파일 접근 0)라 가상 경로로 검증 가능.
/// </summary>
public sealed class FfmpegVideoMixerService
{
    /// <summary>Wan 컷 기본 길이(초).</summary>
    public const double DefaultClipSeconds = 5.0;

    /// <summary>트랜지션 길이 상한 비율(컷 길이 대비).</summary>
    private const double MaxTransitionRatio = 0.45;

    private readonly Func<string?> _ffmpeg;

    public FfmpegVideoMixerService(IVideoMergeService merge) : this(() => merge.ResolvedFfmpegPath) { }

    /// <param name="ffmpegPath">ffmpeg 경로 공급자(테스트 주입용).</param>
    public FfmpegVideoMixerService(Func<string?> ffmpegPath) => _ffmpeg = ffmpegPath;

    /// <summary>ffmpeg 사용 가능 여부.</summary>
    public bool IsAvailable => _ffmpeg() is not null;

    /// <summary>컷 길이(초) — 계획값 우선, 없으면 기본 5초.</summary>
    public static double ClipLength(VideoMixPlan plan, int i)
        => plan.ClipSeconds is { } c && i < c.Count && c[i] > 0.05 ? c[i] : DefaultClipSeconds;

    /// <summary>실효 트랜지션 길이 — 가장 짧은 컷의 45% 이하로 클램프(Cut 은 0).</summary>
    public static double EffectiveTransition(VideoMixPlan plan)
    {
        if (plan.Transition == PostTransition.Cut || plan.Inputs.Count == 0) return 0;
        var shortest = double.MaxValue;
        for (var i = 0; i < plan.Inputs.Count; i++) shortest = Math.Min(shortest, ClipLength(plan, i));
        return Math.Round(Math.Clamp(plan.TransitionSeconds, 0.1, shortest * MaxTransitionRatio), 3);
    }

    /// <summary>컷 시작 시각·총 길이 — Crossfade 는 컷마다 겹침만큼 앞당김, 그 외는 단순 누적.</summary>
    public static MixTimeline Timeline(VideoMixPlan plan)
    {
        var n = plan.Inputs.Count;
        var starts = new double[n];
        var overlap = plan.Transition == PostTransition.Crossfade ? EffectiveTransition(plan) : 0;
        double t = 0;
        for (var i = 0; i < n; i++)
        {
            starts[i] = Math.Round(t, 3);
            t += ClipLength(plan, i) - (i < n - 1 ? overlap : 0);
        }
        return new MixTimeline(starts, Math.Round(t, 3));
    }

    /// <summary>ffmpeg 인자 생성(순수 함수). 입력 0개면 예외.</summary>
    public static List<string> BuildArgs(VideoMixPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var n = plan.Inputs.Count;
        if (n == 0) throw new ArgumentException("입력 영상이 없습니다.", nameof(plan));
        int w = Even(plan.Width), h = Even(plan.Height), fps = Math.Clamp(plan.Fps, 12, 60);
        var td = EffectiveTransition(plan);

        var args = new List<string>(8 + n * 2 + 16) { "-y", "-hide_banner", "-nostdin" };
        for (var i = 0; i < n; i++) { args.Add("-i"); args.Add(plan.Inputs[i]); }

        var fc = new StringBuilder(256 + n * 220);
        for (var i = 0; i < n; i++)
        {
            fc.Append('[').Append(i).Append(":v]scale=").Append(w).Append(':').Append(h)
              .Append(":force_original_aspect_ratio=decrease,pad=").Append(w).Append(':').Append(h)
              .Append(":(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps=").Append(fps).Append(",format=yuv420p,");
            var len = ClipLength(plan, i);
            fc.Append("trim=duration=").Append(Inv(len)).Append(",setpts=PTS-STARTPTS");
            if (plan.Transition == PostTransition.FadeInOut && n > 1)
            {
                if (i > 0) fc.Append(",fade=t=in:st=0:d=").Append(Inv(td));
                if (i < n - 1) fc.Append(",fade=t=out:st=").Append(Inv(Math.Max(0, len - td))).Append(":d=").Append(Inv(td));
            }
            fc.Append(",settb=AVTB[v").Append(i).Append("];");
        }

        string outLabel;
        if (n == 1) outLabel = "v0";
        else if (plan.Transition == PostTransition.Crossfade)
        {
            var tl = Timeline(plan);
            var prev = "v0";
            for (var i = 1; i < n; i++)
            {
                var o = i == n - 1 ? "vout" : "x" + i.ToString(CultureInfo.InvariantCulture);
                fc.Append('[').Append(prev).Append("][v").Append(i).Append("]xfade=transition=fade:duration=").Append(Inv(td))
                  .Append(":offset=").Append(Inv(tl.CutStarts[i])).Append('[').Append(o).Append("];");
                prev = o;
            }
            outLabel = "vout";
        }
        else
        {
            for (var i = 0; i < n; i++) fc.Append("[v").Append(i).Append(']');
            fc.Append("concat=n=").Append(n).Append(":v=1:a=0[vout];");
            outLabel = "vout";
        }
        if (fc[^1] == ';') fc.Length--;

        args.Add("-filter_complex"); args.Add(fc.ToString());
        args.Add("-map"); args.Add("[" + outLabel + "]");
        args.Add("-an");
        args.Add("-c:v"); args.Add("libx264");
        args.Add("-preset"); args.Add("veryfast");
        args.Add("-crf"); args.Add("20");
        args.Add("-pix_fmt"); args.Add("yuv420p");
        args.Add("-r"); args.Add(fps.ToString(CultureInfo.InvariantCulture));
        args.Add("-movflags"); args.Add("+faststart");
        args.Add("-progress"); args.Add("pipe:1");
        args.Add(plan.OutputPath);
        return args;
    }

    /// <summary>병합 실행 — 입력 존재 검증 · 진행률(0~1, out_time 기반) · Graceful 결과.</summary>
    public async Task<MixResult> MixAsync(VideoMixPlan plan, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var exe = _ffmpeg();
        if (exe is null) return new(false, null, "ffmpeg.exe 를 찾을 수 없습니다(exe 옆 또는 Tools\\ffmpeg · PATH).");
        if (plan.Inputs.Count == 0) return new(false, null, "병합할 컷이 없습니다.");
        for (var i = 0; i < plan.Inputs.Count; i++)
            if (!File.Exists(plan.Inputs[i])) return new(false, null, "컷 " + (i + 1).ToString(CultureInfo.InvariantCulture) + " 파일이 없습니다.");
        return await RunAsync(exe, BuildArgs(plan), plan.OutputPath, Timeline(plan).TotalSeconds, progress, ct).ConfigureAwait(false);
    }

    /// <summary>ffmpeg 실행 공통부(오디오 믹서 공유) — <c>-progress pipe:1</c> 의 out_time_us 로 진행률 산출.</summary>
    internal static async Task<MixResult> RunAsync(string exe, List<string> args, string output, double totalSeconds,
        IProgress<double>? progress, CancellationToken ct)
    {
        try
        {
            var dir = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var total = Math.Max(0.1, totalSeconds);
            var r = await ProcessRunner.RunAsync(exe, args, onLine: (isErr, line) =>
            {
                if (isErr || progress is null || !line.StartsWith("out_time_us=", StringComparison.Ordinal)) return;
                if (long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us > 0)
                    progress.Report(Math.Clamp(us / 1_000_000.0 / total, 0, 1));
            }, ct: ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return new(false, null, "취소됨");
            if (!r.Started) return new(false, null, "ffmpeg 실행 실패");
            if (r.ExitCode != 0 || !File.Exists(output))
                return new(false, null, "ffmpeg 오류(" + r.ExitCode.ToString(CultureInfo.InvariantCulture) + "): " + Tail(r.StdErr));
            progress?.Report(1);
            return new(true, output, "완료");
        }
        catch (Exception ex) { return new(false, null, "ffmpeg 실행 예외: " + ex.Message); }
    }

    internal static string Inv(double v) => Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);

    private static int Even(int v) => Math.Max(2, v & ~1);

    private static string Tail(string s) => string.IsNullOrEmpty(s) ? "" : s.Length <= 400 ? s.Trim() : "…" + s[^400..].Trim();
}
