using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;
using PromptFlowStudio.Services.Storyboard;

namespace PromptFlowStudio.Services.Media;

/// <summary>v16.23.0 씬 1건의 리텐션 입력 특징(값 형식 · 무할당).</summary>
public readonly record struct RetentionSceneInput(double Seconds, int PromptWords, int MotionHits, int VoiceChars, bool HasQuestion);

/// <summary>v16.23.0 이탈 위험 구간 — 씬 인덱스 · 구간(초) · 초당 이탈률 · 램핑 배속(1.2~1.5x) · 펀치-인 줌 배율.</summary>
public readonly record struct RetentionRiskZone(int Scene, double Start, double End, double DropPerSecond, double Speed, double Zoom);

/// <summary>v16.23.0 예측 결과(UI 물질화) — 원본/램핑 후 곡선(0~1) · 위험 구간 · 평균/최종 유지율.</summary>
public sealed record RetentionPrediction(float[] Curve, float[] RampedCurve, double BinSeconds, double TotalSeconds,
    RetentionRiskZone[] Zones, double AverageRetention, double FinalRetention, double RampedAverageRetention, double RampedTotalSeconds);

/// <summary>
/// [PromptFlowStudio] v16.23.0 📈 AI 리텐션 예측 &amp; 스피드 램핑 엔진 — 씬별 전개 속도·프롬프트 밀도 → 예상 시청자 유지율 곡선 → 이탈 위험 구간 자동 램핑.
///   ▸ 모델: 0.5초 빈 생존 곡선 R(t+dt)=R(t)·exp(−h·dt). 위험률 h = 기본 0.012 + 지루함(1−참여도)·0.11·min(1, 씬 경과/3초)
///     + 첫 3초 후킹 이탈 0.14·(1−후킹점수). 컷 전환 시 지루함 리셋(숏폼 컷 리듬). 참여도 = 프롬프트 밀도·모션 어휘·나레이션 밀도·질문.
///   ▸ 위험 구간: 첫 3초 이후 씬 중 초당 이탈률 ≥ <see cref="RiskDropPerSecond"/> 또는 저참여(&lt;0.45)·장시간(≥2.5초) → 심각도 비례 1.2~1.5x · 1.08~1.15x 펀치-인.
///   ▸ 핫패스(<see cref="Predict"/>·<see cref="DetectRiskZones"/>·<see cref="BuildInputs"/>)는 Span 입출력 · LINQ/박싱/문자열 0(UI 렌더마다 재계산).
///   ▸ FFmpeg 인젝션: trim 세그먼트 → 램핑 구간 setpts=PTS/s + atempo=s + zoompan 12프레임 펀치-인 → concat. 오디오 실패 시 무음 재시도([부분 구제]).
/// </summary>
public sealed class RetentionHeatmapPredictorEngine
{
    public const double DefaultCutSeconds = 5.0;
    public const double DefaultBinSeconds = 0.5;
    public const int MaxBins = 480;
    public const int MaxScenes = 64;
    public const int MaxZones = 32;
    public const double RiskDropPerSecond = 0.05;
    public const double MinSpeed = 1.2;
    public const double MaxSpeed = 1.5;
    public const int PunchInFrames = 12;

    private static readonly string[] MotionWords = { "run", "jump", "fly", "spin", "dash", "explode", "fast", "zoom", "chase", "dance",
        "달리", "뛰", "날아", "회전", "폭발", "빠르", "질주", "춤", "쫓" };

    private readonly Func<string?> _ffmpeg;
    private int _busy;
    private int _applied;

    public RetentionHeatmapPredictorEngine(IVideoMergeService merge) : this(() => merge.ResolvedFfmpegPath) { }

    /// <param name="ffmpegPath">ffmpeg 경로 공급자(테스트 주입용).</param>
    public RetentionHeatmapPredictorEngine(Func<string?> ffmpegPath) => _ffmpeg = ffmpegPath;

    public bool IsAvailable => _ffmpeg() is not null;
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public int AppliedCount => Volatile.Read(ref _applied);
    public RetentionPrediction? LastPrediction { get; private set; }
    public string? LastAction { get; private set; }

    public event Action? OnChange;

    // ── 핫패스(무할당) ───────────────────────────────────────────────────────

    /// <summary>프로젝트 → 씬 입력(완료 여부 무관 · 빈 컷 제외). 반환 = 기록 수(≤ dst.Length).</summary>
    public static int BuildInputs(StoryboardProject project, Span<RetentionSceneInput> dst, double cutSeconds = DefaultCutSeconds)
    {
        var n = 0;
        var sec = Math.Max(0.5, cutSeconds);
        var scenes = project.Scenes;
        for (var i = 0; i < scenes.Count && n < dst.Length; i++)
        {
            var s = scenes[i];
            var prompt = s.Prompt.AsSpan();
            var voice = s.VoiceText.AsSpan();
            if (prompt.IsWhiteSpace() && voice.IsWhiteSpace()) continue;
            var voiceChars = voice.Trim().Length;
            dst[n++] = new RetentionSceneInput(sec, CountWords(prompt), CountMotion(prompt), voiceChars,
                voice.IndexOf('?') >= 0 || prompt.IndexOf('?') >= 0);
        }
        return n;
    }

    /// <summary>참여도 0~1(프롬프트 밀도 · 모션 · 나레이션 밀도 · 질문).</summary>
    public static double EngagementOf(in RetentionSceneInput s)
    {
        var sec = Math.Max(0.25, s.Seconds);
        var e = 0.30
                + Math.Min(0.25, s.PromptWords / sec * 0.04)
                + Math.Min(0.20, s.MotionHits * 0.10)
                + Math.Min(0.15, s.VoiceChars / sec * 0.02)
                + (s.HasQuestion ? 0.05 : 0);
        return Math.Clamp(e, 0, 1);
    }

    /// <summary>유지율 곡선 예측(0.5초 빈 · curve[0] = 1) · 반환 = 기록 빈 수. hookScore01 = 첫 3초 후킹 점수 0~1.</summary>
    public static int Predict(ReadOnlySpan<RetentionSceneInput> scenes, Span<float> curve, double binSeconds = DefaultBinSeconds, double hookScore01 = 0.5)
    {
        if (scenes.IsEmpty || curve.IsEmpty) return 0;
        var dt = Math.Max(0.05, binSeconds);
        var total = TotalSeconds(scenes);
        var bins = Math.Min(curve.Length, (int)Math.Ceiling(total / dt) + 1);
        var hook = Math.Clamp(hookScore01, 0, 1);
        var r = 1.0;
        var si = 0;
        var sceneStart = 0.0;
        var engagement = EngagementOf(scenes[0]);
        curve[0] = 1f;
        for (var b = 1; b < bins; b++)
        {
            var t = (b - 0.5) * dt;   // 구간 중앙
            while (si < scenes.Length - 1 && t >= sceneStart + scenes[si].Seconds)
            {
                sceneStart += scenes[si].Seconds;
                si++;
                engagement = EngagementOf(scenes[si]);
            }
            var inScene = t - sceneStart;
            var h = 0.012 + (1 - engagement) * 0.11 * Math.Min(1, inScene / 3.0);
            if (t < ViralHookVariantGeneratorService.HookSeconds) h += 0.14 * (1 - hook);
            r *= Math.Exp(-h * dt);
            curve[b] = (float)r;
        }
        return bins;
    }

    /// <summary>이탈 위험 구간 검출(첫 3초 후킹 구간 제외) · 반환 = 기록 수(≤ zones.Length).</summary>
    public static int DetectRiskZones(ReadOnlySpan<RetentionSceneInput> scenes, ReadOnlySpan<float> curve, Span<RetentionRiskZone> zones,
        double binSeconds = DefaultBinSeconds)
    {
        if (scenes.IsEmpty || curve.IsEmpty || zones.IsEmpty) return 0;
        var dt = Math.Max(0.05, binSeconds);
        var n = 0;
        var start = 0.0;
        for (var i = 0; i < scenes.Length && n < zones.Length; i++)
        {
            var sec = scenes[i].Seconds;
            var end = start + sec;
            var zs = Math.Max(start, ViralHookVariantGeneratorService.HookSeconds);
            if (end - zs >= 1.0)
            {
                // 후킹 구간 이후 시작 컷: 곡선 실측(컷 전체) · 후킹 구간에 걸친 컷: 후킹 이탈을 뺀 컷 평균 위험률(곡선 꼬리 편향 방지)
                double drop;
                if (start >= ViralHookVariantGeneratorService.HookSeconds)
                {
                    var r0 = Sample(curve, start, dt);
                    var r1 = Sample(curve, end, dt);
                    drop = r0 > 0 ? (r0 - r1) / r0 / sec : 0;
                }
                else drop = SceneHazard(scenes[i]);
                var e = EngagementOf(scenes[i]);
                var boring = e < 0.45 && sec >= 2.5;
                if (drop >= RiskDropPerSecond || boring)
                {
                    var sev = Math.Clamp(Math.Max((drop - RiskDropPerSecond) / RiskDropPerSecond, (0.45 - e) / 0.25), 0, 1);
                    var speed = Math.Round((MinSpeed + (MaxSpeed - MinSpeed) * sev) * 20) / 20;   // 0.05 단위
                    var zoom = Math.Round((1.08 + 0.07 * sev) * 100) / 100;
                    zones[n++] = new RetentionRiskZone(i, zs, end, drop, Math.Clamp(speed, MinSpeed, MaxSpeed), zoom);
                }
            }
            start = end;
        }
        return n;
    }

    /// <summary>램핑 반영(위험 씬 길이 ÷ 배속 · 펀치-인은 모션 신호 +1) — 제자리 갱신.</summary>
    public static void ApplyRamp(Span<RetentionSceneInput> scenes, ReadOnlySpan<RetentionRiskZone> zones)
    {
        foreach (var z in zones)
        {
            if ((uint)z.Scene >= (uint)scenes.Length) continue;
            var s = scenes[z.Scene];
            var keep = Math.Max(0, z.Start - SceneStart(scenes, z.Scene));
            scenes[z.Scene] = s with { Seconds = keep + (s.Seconds - keep) / Math.Max(1, z.Speed), MotionHits = s.MotionHits + 1 };
        }
    }

    public static double TotalSeconds(ReadOnlySpan<RetentionSceneInput> scenes)
    {
        var t = 0.0;
        foreach (var s in scenes) t += s.Seconds;
        return t;
    }

    public static double Average(ReadOnlySpan<float> curve)
    {
        if (curve.IsEmpty) return 0;
        var sum = 0.0;
        foreach (var v in curve) sum += v;
        return sum / curve.Length;
    }

    // ── UI 물질화 ────────────────────────────────────────────────────────────

    /// <summary>프로젝트 예측(곡선·위험 구간·램핑 후 비교 · 결과 보관 + OnChange).</summary>
    public RetentionPrediction Analyze(StoryboardProject project, double hookScore01 = 0.5, double cutSeconds = DefaultCutSeconds)
    {
        var p = Compute(project, hookScore01, cutSeconds);
        LastPrediction = p;
        Raise();
        return p;
    }

    /// <summary>순수 예측(상태 미기록).</summary>
    public static RetentionPrediction Compute(StoryboardProject project, double hookScore01 = 0.5, double cutSeconds = DefaultCutSeconds)
    {
        ArgumentNullException.ThrowIfNull(project);
        Span<RetentionSceneInput> scenes = stackalloc RetentionSceneInput[MaxScenes];
        Span<float> curve = stackalloc float[MaxBins];
        Span<RetentionRiskZone> zones = stackalloc RetentionRiskZone[MaxZones];
        var n = BuildInputs(project, scenes, cutSeconds);
        var input = scenes[..n];
        var bins = Predict(input, curve, DefaultBinSeconds, hookScore01);
        var zn = DetectRiskZones(input, curve[..bins], zones, DefaultBinSeconds);
        var total = TotalSeconds(input);
        var c0 = curve[..bins].ToArray();
        var z = zones[..zn].ToArray();

        ApplyRamp(input, zones[..zn]);
        var rbins = Predict(input, curve, DefaultBinSeconds, hookScore01);
        var c1 = curve[..rbins].ToArray();
        return new RetentionPrediction(c0, c1, DefaultBinSeconds, total, z, Average(c0), c0.Length > 0 ? c0[^1] : 0,
            Average(c1), TotalSeconds(input));
    }

    // ── FFmpeg 인젝션 ────────────────────────────────────────────────────────

    /// <summary>스피드 램핑 + 펀치-인 필터 그래프(출력 [v] · audio=true 면 [a]). 구간은 [0,total] 로 클램프 · 겹침/역순 무시(순수 함수).</summary>
    public static string BuildRampFilter(ReadOnlySpan<RetentionRiskZone> zones, double totalSeconds, int width, int height, bool audio)
    {
        var w = Even(width);
        var h = Even(height);
        var total = Math.Max(0.1, totalSeconds);
        var sb = new StringBuilder(256 + zones.Length * 512);
        var seg = 0;
        var cursor = 0.0;
        foreach (var z in zones)
        {
            var s = Math.Clamp(z.Start, 0, total);
            var e = Math.Clamp(z.End, 0, total);
            if (s < cursor || e - s < 0.2) continue;
            if (s - cursor >= 0.05) AppendSegment(sb, seg++, cursor, s, 1, 1, w, h, audio);
            AppendSegment(sb, seg++, s, e, Math.Clamp(z.Speed, 1, 2), Math.Clamp(z.Zoom, 1, 1.5), w, h, audio);
            cursor = e;
        }
        if (total - cursor >= 0.05 || seg == 0) AppendSegment(sb, seg++, cursor, total, 1, 1, w, h, audio);
        for (var i = 0; i < seg; i++)
        {
            sb.Append("[v").Append(i).Append(']');
            if (audio) sb.Append("[a").Append(i).Append(']');
        }
        sb.Append("concat=n=").Append(seg).Append(":v=1:a=").Append(audio ? 1 : 0).Append("[v]");
        if (audio) sb.Append("[a]");
        return sb.ToString();
    }

    private static void AppendSegment(StringBuilder sb, int i, double start, double end, double speed, double zoom, int w, int h, bool audio)
    {
        sb.Append("[0:v]trim=start=").Append(Inv(start)).Append(":end=").Append(Inv(end)).Append(",setpts=PTS-STARTPTS");
        if (speed > 1.001) sb.Append(",setpts=PTS/").Append(Inv(speed));
        sb.Append(",scale=").Append(w).Append(':').Append(h).Append(":force_original_aspect_ratio=increase,crop=").Append(w).Append(':').Append(h)
          .Append(",setsar=1,fps=30");
        if (zoom > 1.001)
            sb.Append(",zoompan=z='min(1+").Append(Inv(zoom - 1)).Append("*on/").Append(PunchInFrames).Append(',').Append(Inv(zoom))
              .Append(")':x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':d=1:s=").Append(w).Append('x').Append(h).Append(":fps=30");
        sb.Append(",format=yuv420p[v").Append(i).Append("];");
        if (!audio) return;
        sb.Append("[0:a]atrim=start=").Append(Inv(start)).Append(":end=").Append(Inv(end)).Append(",asetpts=PTS-STARTPTS");
        if (speed > 1.001) sb.Append(",atempo=").Append(Inv(speed));
        sb.Append(",aresample=48000[a").Append(i).Append("];");
    }

    /// <summary>ffmpeg 인자(순수 함수).</summary>
    public static List<string> BuildRampArgs(string input, string output, ReadOnlySpan<RetentionRiskZone> zones, double totalSeconds,
        int width, int height, bool audio)
    {
        if (string.IsNullOrWhiteSpace(input)) throw new ArgumentException("입력 영상이 비었습니다.", nameof(input));
        var args = new List<string>(32)
        {
            "-y", "-hide_banner", "-nostdin", "-i", input,
            "-filter_complex", BuildRampFilter(zones, totalSeconds, width, height, audio),
            "-map", "[v]",
        };
        if (audio) { args.Add("-map"); args.Add("[a]"); args.Add("-c:a"); args.Add("aac"); args.Add("-b:a"); args.Add("192k"); }
        args.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-progress", "pipe:1", output });
        return args;
    }

    /// <summary>
    /// 완성 영상에 위험 구간 램핑 적용(예외 0). 실제 길이를 ffmpeg 로 측정해 예측 구간을 비례 매핑(크로스페이드 등 길이 차 보정).
    /// 오디오 포함 실패 시 무음 재시도([부분 구제]).
    /// </summary>
    public async Task<MixResult> ApplySpeedRampAsync(string input, string output, RetentionPrediction prediction,
        int width = ViralHookVariantGeneratorService.Width, int height = ViralHookVariantGeneratorService.Height,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prediction);
        if (Interlocked.Exchange(ref _busy, 1) != 0) return new(false, null, "이미 스피드 램핑이 진행 중입니다.");
        try
        {
            var exe = _ffmpeg();
            if (exe is null) return Done(new(false, null, "ffmpeg.exe 를 찾을 수 없어 스피드 램핑을 적용할 수 없습니다."));
            if (string.IsNullOrWhiteSpace(input) || !File.Exists(input)) return Done(new(false, null, "원본 완성 영상이 없습니다."));
            if (prediction.Zones.Length == 0) return Done(new(false, null, "이탈 위험 구간이 없어 램핑이 필요하지 않습니다."));

            var probe = await ProcessRunner.RunAsync(exe, new List<string> { "-hide_banner", "-nostdin", "-i", input }, ct: ct).ConfigureAwait(false);
            var actual = AiAudioDrivenLipSyncService.ParseDuration(probe.StdErr);
            var hasAudio = probe.StdErr.Contains("Audio:", StringComparison.Ordinal);
            var zones = ScaleZones(prediction.Zones, prediction.TotalSeconds, actual);
            var total = actual > 0 ? actual : prediction.TotalSeconds;

            var r = await FfmpegVideoMixerService.RunAsync(exe, BuildRampArgs(input, output, zones, total, width, height, hasAudio), output, total, progress, ct).ConfigureAwait(false);
            if (!r.Ok && hasAudio && !ct.IsCancellationRequested)
                r = await FfmpegVideoMixerService.RunAsync(exe, BuildRampArgs(input, output, zones, total, width, height, false), output, total, progress, ct).ConfigureAwait(false);
            if (r.Ok)
            {
                Interlocked.Increment(ref _applied);
                r = r with { Message = zones.Length.ToString(CultureInfo.InvariantCulture) + "구간 램핑·펀치-인 적용" };
            }
            return Done(r);
        }
        catch (OperationCanceledException) { return Done(new(false, null, "취소됨")); }
        catch (Exception ex) { Program.Log("Retention.ApplyRamp", ex); return Done(new(false, null, "스피드 램핑 실패: " + ex.Message)); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    /// <summary>예측 시간축 → 실제 영상 길이 비례 매핑(실제 길이 모름 → 그대로).</summary>
    public static RetentionRiskZone[] ScaleZones(RetentionRiskZone[] zones, double predictedTotal, double actualTotal)
    {
        var copy = (RetentionRiskZone[])zones.Clone();
        if (actualTotal <= 0 || predictedTotal <= 0) return copy;
        var k = actualTotal / predictedTotal;
        for (var i = 0; i < copy.Length; i++) copy[i] = copy[i] with { Start = copy[i].Start * k, End = Math.Min(actualTotal, copy[i].End * k) };
        return copy;
    }

    /// <summary>유지율 → 히트맵 색(초록 ≥0.7 · 노랑 ≥0.45 · 빨강).</summary>
    public static string HeatColorOf(double retention) => retention >= 0.7 ? "#22c55e" : retention >= 0.45 ? "#eab308" : "#ef4444";

    private MixResult Done(MixResult r)
    {
        LastAction = (r.Ok ? "✅ " : "⚠ ") + r.Message;
        Raise();
        return r;
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("Retention.OnChange", ex); }
    }

    // Predict 와 같은 모델의 컷 평균 위험률(후킹 이탈 제외) — 지루함 min(1, t/3) 의 [0, sec] 평균
    private static double SceneHazard(in RetentionSceneInput s)
    {
        var sec = Math.Max(0.25, s.Seconds);
        var boredom = sec <= 3 ? sec / 6 : (1.5 + (sec - 3)) / sec;
        return 0.012 + (1 - EngagementOf(s)) * 0.11 * boredom;
    }

    private static double SceneStart(ReadOnlySpan<RetentionSceneInput> scenes, int index)
    {
        var t = 0.0;
        for (var i = 0; i < index && i < scenes.Length; i++) t += scenes[i].Seconds;
        return t;
    }

    private static float Sample(ReadOnlySpan<float> curve, double t, double dt)
    {
        var i = (int)Math.Round(t / dt);
        return curve[Math.Clamp(i, 0, curve.Length - 1)];
    }

    private static int CountWords(ReadOnlySpan<char> s)
    {
        var n = 0;
        var inWord = false;
        foreach (var c in s)
        {
            var sep = char.IsWhiteSpace(c) || c == ',' || c == '.';
            if (!sep && !inWord) n++;
            inWord = !sep;
        }
        return n;
    }

    private static int CountMotion(ReadOnlySpan<char> s)
    {
        var n = 0;
        foreach (var w in MotionWords) if (s.IndexOf(w.AsSpan(), StringComparison.OrdinalIgnoreCase) >= 0) n++;
        return n;
    }

    private static int Even(int v) => Math.Max(2, v & ~1);
    private static string Inv(double v) => FfmpegVideoMixerService.Inv(v);
}
