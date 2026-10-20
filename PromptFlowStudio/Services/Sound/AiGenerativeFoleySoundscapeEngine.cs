using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Interactive;   // ShoppableVideoOverlayEngine.ProbeAsync
using PromptFlowStudio.Services.Media;         // IVideoMergeService
using PromptFlowStudio.Services.Spatial;       // ReadExactAsync
using PromptFlowStudio.Services.Storyboard;

namespace PromptFlowStudio.Services.Sound;

public enum FoleyKind : byte { Footstep, DoorSlam, Wind, Laughter, Rain, Whoosh, Impact, Crowd }

[Flags]
public enum FoleyMask
{
    None = 0, Footstep = 1 << FoleyKind.Footstep, DoorSlam = 1 << FoleyKind.DoorSlam, Wind = 1 << FoleyKind.Wind, Laughter = 1 << FoleyKind.Laughter,
    Rain = 1 << FoleyKind.Rain, Whoosh = 1 << FoleyKind.Whoosh, Impact = 1 << FoleyKind.Impact, Crowd = 1 << FoleyKind.Crowd,
}

/// <summary>프레임 모션 샘플 — 에너지(0~1 · 평균 절대 차) · 모션 무게중심(정규화 u,v).</summary>
public readonly record struct MotionSample(float Energy, float CenterX, float CenterY);

/// <summary>Foley 이벤트(타임코드 초 · 길이 · 선형 게인 · 팬 -1~1 · 강도).</summary>
public readonly record struct FoleyEvent(FoleyKind Kind, double Start, double Duration, float Gain, float Pan, float Intensity);

public sealed record FoleyPlan(IReadOnlyList<FoleyEvent> Events, float[] Left, float[] Right, int Rate, double Duration, float[] MotionEnergy, double MotionFps, FoleyMask Keywords);

/// <summary>
/// [PromptFlow Studio v16.33.0] AI 시각 연동 절차적 Foley 사운드스케이프 생성기(순수 BCL 합성 · 샘플 라이브러리 0).
///   ▸ 모션: ffmpeg gray 96폭·10fps → 프레임 차분 에너지 + 모션 무게중심(무할당).
///   ▸ 대본/프롬프트 키워드(한/영) → Foley 마스크(발자국·문·바람·웃음·비·휙·충격·군중).
///   ▸ 이벤트: 적응 임계(평균+2.5σ) 국소 최대 온셋 → 문 닫힘/충격 · 지속 모션 구간 → 발자국 케이던스 · 급격한 무게중심 이동 → 휙 · 환경음 베드.
///   ▸ 합성: 사인 썸프·필터 노이즈·스윕 밴드패스·포먼트 펄스(웃음)·물방울 임펄스(비) — 시드 결정적 · 팬=모션 무게중심.
///   ▸ 인젝션: 원본 오디오와 amix(오디오 없음 → SFX 단독 매핑) · 영상 스트림 copy.
/// </summary>
public sealed class AiGenerativeFoleySoundscapeEngine
{
    public const int Rate = 48_000;
    public const int MotionWidth = 96;
    public const double MotionFps = 10;
    public const int MaxEvents = 256;
    public const double MaxSeconds = 300;

    private static readonly (FoleyKind Kind, string[] Words)[] Lexicon =
    {
        (FoleyKind.Footstep, new[] { "walk", "footstep", "step", "stairs", "run", "걷", "발자국", "계단", "달리", "뛰" }),
        (FoleyKind.DoorSlam, new[] { "door", "slam", "문을", "문이", "문 ", "닫" }),
        (FoleyKind.Wind, new[] { "wind", "storm", "breeze", "바람", "폭풍", "태풍" }),
        (FoleyKind.Laughter, new[] { "laugh", "giggle", "웃", "하하", "ㅋㅋ" }),
        (FoleyKind.Rain, new[] { "rain", "drizzle", "비가", "비 오", "비오", "비 내", "빗", "폭우", "장마" }),
        (FoleyKind.Whoosh, new[] { "whoosh", "swing", "dash", "휙", "빠르게", "스윙" }),
        (FoleyKind.Impact, new[] { "punch", "hit", "crash", "explosion", "fall", "충돌", "때리", "폭발", "넘어", "쾅" }),
        (FoleyKind.Crowd, new[] { "crowd", "cafe", "party", "market", "audience", "군중", "관중", "카페", "파티", "시장" }),
    };

    private readonly Func<string?> _ffmpeg;
    private int _busy;
    private long _generated;

    public AiGenerativeFoleySoundscapeEngine(IVideoMergeService merge) : this(() => merge.ResolvedFfmpegPath) { }

    public AiGenerativeFoleySoundscapeEngine(Func<string?> ffmpeg) => _ffmpeg = ffmpeg;

    public event Action? OnChange;

    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public bool FfmpegAvailable => _ffmpeg() is not null;
    public long GeneratedEvents => Interlocked.Read(ref _generated);
    public FoleyPlan? LastPlan { get; private set; }
    public string? LastAction { get; private set; }

    public static string Label(FoleyKind k) => k switch
    {
        FoleyKind.Footstep => "발자국", FoleyKind.DoorSlam => "문 닫힘", FoleyKind.Wind => "바람", FoleyKind.Laughter => "웃음",
        FoleyKind.Rain => "비", FoleyKind.Whoosh => "휙", FoleyKind.Impact => "충격", FoleyKind.Crowd => "군중", _ => k.ToString(),
    };

    public static bool IsAmbience(FoleyKind k) => k is FoleyKind.Wind or FoleyKind.Rain or FoleyKind.Crowd;

    // ══════════════════════ 키워드 ══════════════════════

    public static FoleyMask DetectKeywords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return FoleyMask.None;
        var t = text.ToLowerInvariant();
        var m = FoleyMask.None;
        foreach (var (kind, words) in Lexicon)
            foreach (var w in words)
                if (t.Contains(w, StringComparison.Ordinal)) { m |= (FoleyMask)(1 << (int)kind); break; }
        return m;
    }

    public static string SceneText(StoryboardProject? project)
    {
        if (project is null) return "";
        var sb = new StringBuilder();
        foreach (var s in project.Scenes) sb.Append(s.Prompt).Append(' ').Append(s.VoiceText).Append(' ');
        return sb.ToString();
    }

    // ══════════════════════ 모션 분석 ══════════════════════

    /// <summary>연속 gray 프레임 차분 → 에너지/무게중심(노이즈 임계 12 · 무할당).</summary>
    public static MotionSample AnalyzeMotion(ReadOnlySpan<byte> prev, ReadOnlySpan<byte> cur, int w, int h)
    {
        var n = w * h;
        if (n <= 0 || prev.Length < n || cur.Length < n) return default;
        long sum = 0, sx = 0, sy = 0;
        for (var y = 0; y < h; y++)
        {
            var row = y * w;
            for (var x = 0; x < w; x++)
            {
                var d = Math.Abs(cur[row + x] - prev[row + x]);
                if (d <= 12) continue;
                sum += d; sx += (long)d * x; sy += (long)d * y;
            }
        }
        if (sum == 0) return new(0, 0.5f, 0.5f);
        return new(sum / (255f * n), (float)sx / sum / Math.Max(1, w - 1), (float)sy / sum / Math.Max(1, h - 1));
    }

    // ══════════════════════ 이벤트 계획 ══════════════════════

    public static List<FoleyEvent> PlanEvents(ReadOnlySpan<MotionSample> m, double fps, FoleyMask mask, double duration, int seed)
    {
        var ev = new List<FoleyEvent>(32);
        duration = Math.Clamp(duration, 0, MaxSeconds);
        if (duration <= 0) return ev;
        if ((mask & FoleyMask.Wind) != 0) ev.Add(new(FoleyKind.Wind, 0, duration, 0.25f, 0, 1));
        if ((mask & FoleyMask.Rain) != 0) ev.Add(new(FoleyKind.Rain, 0, duration, 0.22f, 0, 1));
        if ((mask & FoleyMask.Crowd) != 0) ev.Add(new(FoleyKind.Crowd, 0, duration, 0.18f, 0, 1));
        double mean = 0, var = 0;
        for (var i = 0; i < m.Length; i++) mean += m[i].Energy;
        mean = m.Length > 0 ? mean / m.Length : 0;
        for (var i = 0; i < m.Length; i++) { var d = m[i].Energy - mean; var += d * d; }
        var std = m.Length > 1 ? Math.Sqrt(var / m.Length) : 0;
        var rng = new XorShift32(seed);
        double lastOnset = -1, lastWhoosh = -1, lastLaugh = -10, nextStep = 0;
        var stepSide = 1f;
        for (var i = 0; i < m.Length && ev.Count < MaxEvents; i++)
        {
            var t = i / fps;
            if (t >= duration) break;
            var e = m[i].Energy;
            var pan = Math.Clamp((m[i].CenterX - 0.5f) * 2f, -1f, 1f);
            var prevE = i > 0 ? m[i - 1].Energy : 0f;
            var nextE = i + 1 < m.Length ? m[i + 1].Energy : 0f;
            // 온셋 → 문/충격/웃음
            if (e > mean + 2.5 * std && e > 0.02f && e >= prevE && e >= nextE && t - lastOnset >= 0.3)
            {
                lastOnset = t;
                var strong = e > mean + 4 * std;
                var inten = (float)Math.Clamp(0.6 + (e - mean) / (std * 6 + 1e-6), 0.6, 1.6);
                if ((mask & FoleyMask.DoorSlam) != 0) ev.Add(new(FoleyKind.DoorSlam, t, 0.8, 0.8f, pan, inten));
                else if ((mask & FoleyMask.Impact) != 0 || strong) ev.Add(new(FoleyKind.Impact, t, 0.5, 0.75f, pan, inten));
                if ((mask & FoleyMask.Laughter) != 0 && t - lastLaugh >= 2.0) { ev.Add(new(FoleyKind.Laughter, t + 0.1, 1.2, 0.5f, pan, 1)); lastLaugh = t; }
            }
            // 급격한 수평 이동 → 휙
            if (i > 0 && e > mean + std && e > 0.01f && Math.Abs(m[i].CenterX - m[i - 1].CenterX) * fps > 1.5 && t - lastWhoosh >= 0.6)
            {
                ev.Add(new(FoleyKind.Whoosh, Math.Max(0, t - 0.2), 0.5, 0.55f, pan, 1));
                lastWhoosh = t;
            }
            // 지속 모션 → 발자국 케이던스
            if ((mask & FoleyMask.Footstep) != 0 && e > Math.Max(mean * 0.8, 0.005) && t >= nextStep)
            {
                ev.Add(new(FoleyKind.Footstep, t, 0.25, 0.6f, Math.Clamp(pan + 0.1f * stepSide, -1f, 1f), 0.8f + 0.2f * rng.Next01()));
                stepSide = -stepSide;
                nextStep = t + Math.Clamp(0.6 - e * 2.5, 0.35, 0.6);
            }
        }
        if ((mask & FoleyMask.Laughter) != 0 && lastLaugh < 0 && ev.Count < MaxEvents) ev.Add(new(FoleyKind.Laughter, duration * 0.3, 1.2, 0.5f, 0, 1));
        ev.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        return ev;
    }

    // ══════════════════════ 절차적 합성 ══════════════════════

    /// <summary>Foley 1건 합성(mono · dst 길이 = 이벤트 길이 · 시드 결정적 · 무할당).</summary>
    public static void Synthesize(FoleyKind kind, Span<float> dst, int rate, float intensity, int seed)
    {
        dst.Clear();
        var n = dst.Length;
        if (n == 0 || rate <= 0) return;
        var I = Math.Clamp(intensity, 0.1f, 2f);
        var rng = new XorShift32(seed);
        var dt = 1f / rate;
        var fade = Math.Max(1, (int)(0.004f * rate));
        switch (kind)
        {
            case FoleyKind.Footstep:
            {
                var lp = Biquad.LowPass(1800, 0.7, rate);
                for (var i = 0; i < n; i++)
                {
                    var t = i * dt;
                    var thump = MathF.Sin(2 * MathF.PI * 70 * t) * MathF.Exp(-t / 0.035f);
                    var scuff = lp.Process(rng.Next()) * MathF.Exp(-t / 0.018f);
                    dst[i] = (0.7f * thump + 0.6f * scuff) * 0.8f * I;
                }
                break;
            }
            case FoleyKind.DoorSlam:
            {
                var bp = Biquad.BandPass(2500, 2, rate); var lp = Biquad.LowPass(400, 0.7, rate);
                for (var i = 0; i < n; i++)
                {
                    var t = i * dt; var w = rng.Next();
                    dst[i] = (MathF.Sin(2 * MathF.PI * 55 * t) * MathF.Exp(-t / 0.12f) * 0.9f + bp.Process(w) * MathF.Exp(-t / 0.008f) * 1.6f
                              + lp.Process(w) * MathF.Exp(-t / 0.25f) * 0.4f) * I;
                }
                break;
            }
            case FoleyKind.Impact:
            {
                var bp = Biquad.BandPass(1200, 1.2, rate);
                for (var i = 0; i < n; i++)
                {
                    var t = i * dt;
                    dst[i] = (MathF.Sin(2 * MathF.PI * 90 * t) * MathF.Exp(-t / 0.06f) + bp.Process(rng.Next()) * MathF.Exp(-t / 0.02f) * 1.4f) * 0.8f * I;
                }
                break;
            }
            case FoleyKind.Whoosh:
            {
                var bp = Biquad.BandPass(400, 1.5, rate);
                for (var i = 0; i < n; i++)
                {
                    var u = (float)i / n;
                    if ((i & 63) == 0)
                    {
                        var keep = bp;
                        bp = Biquad.BandPass(300 + 2700 * MathF.Sin(MathF.PI * u), 1.5, rate);
                        bp.CopyStateFrom(keep);
                    }
                    dst[i] = bp.Process(rng.Next()) * MathF.Sin(MathF.PI * u) * 1.2f * I;
                }
                break;
            }
            case FoleyKind.Wind:
            {
                var lp = Biquad.LowPass(500, 0.9, rate);
                for (var i = 0; i < n; i++)
                {
                    var t = i * dt;
                    if ((i & 255) == 0) { var keep = lp; lp = Biquad.LowPass(450 + 300 * MathF.Sin(2 * MathF.PI * 0.11f * t), 0.9, rate); lp.CopyStateFrom(keep); }
                    var gust = 0.55f + 0.45f * MathF.Sin(2 * MathF.PI * 0.15f * t + 0.7f * MathF.Sin(2 * MathF.PI * 0.05f * t));
                    dst[i] = lp.Process(rng.Next()) * gust * 1.4f * I;
                }
                fade = Math.Max(fade, Math.Min(n / 2, rate / 2));
                break;
            }
            case FoleyKind.Rain:
            {
                var hp = Biquad.HighPass(2500, 0.7, rate); var drop = Biquad.BandPass(4200, 3, rate);
                var p = 450f / rate;
                for (var i = 0; i < n; i++)
                {
                    var imp = rng.Next01() < p ? rng.Next01() * 3f : 0f;
                    dst[i] = (hp.Process(rng.Next()) * 0.25f + drop.Process(imp)) * I;
                }
                fade = Math.Max(fade, Math.Min(n / 2, rate / 2));
                break;
            }
            case FoleyKind.Laughter:
            {
                var f1 = Biquad.BandPass(800, 4, rate); var f2 = Biquad.BandPass(1250, 5, rate);
                float phase = 0;
                for (var i = 0; i < n; i++)
                {
                    var t = i * dt;
                    var syl = t * 5f - MathF.Floor(t * 5f);                                     // 5Hz "하"
                    var env = syl < 0.6f ? MathF.Pow(MathF.Sin(MathF.PI * syl / 0.6f), 2) : 0f;
                    var f0 = 230f * (1 + 0.06f * MathF.Sin(2 * MathF.PI * 6 * t)) * (1 - 0.15f * t / (n * dt));
                    phase += f0 * dt; phase -= MathF.Floor(phase);
                    var src = (2 * phase - 1) * 0.6f + rng.Next() * 0.35f;                     // 톱니 + 숨소리
                    dst[i] = (f1.Process(src) + 0.7f * f2.Process(src)) * env * MathF.Exp(-t / 1.5f) * 2.2f * I;
                }
                break;
            }
            case FoleyKind.Crowd:
            {
                Span<Biquad> v = stackalloc Biquad[6];
                for (var k = 0; k < 6; k++) v[k] = Biquad.BandPass(450 + 220 * k, 3, rate);
                for (var i = 0; i < n; i++)
                {
                    var t = i * dt; var s = 0f;
                    for (var k = 0; k < 6; k++) s += v[k].Process(rng.Next()) * (0.5f + 0.5f * MathF.Sin(2 * MathF.PI * (2.7f + 0.9f * k) * t + k));
                    dst[i] = s * 0.6f * I;
                }
                fade = Math.Max(fade, Math.Min(n / 2, rate / 2));
                break;
            }
        }
        for (var i = 0; i < fade && i < n; i++)
        {
            var g = (float)i / fade;
            if (IsAmbience(kind)) dst[i] *= g;
            dst[n - 1 - i] *= g;
        }
    }

    /// <summary>이벤트 → 스테레오 트랙(등전력 팬 · 길이 duration).</summary>
    public static (float[] L, float[] R) RenderTrack(IReadOnlyList<FoleyEvent> events, double duration, int rate, int seed = 1)
    {
        var total = (int)(Math.Clamp(duration, 0, MaxSeconds) * rate);
        var l = new float[total]; var r = new float[total];
        for (var k = 0; k < events.Count; k++)
        {
            var e = events[k];
            var start = (int)(e.Start * rate);
            if (start >= total) continue;
            var len = Math.Min(total - start, (int)(e.Duration * rate));
            if (len <= 0) continue;
            var buf = ArrayPool<float>.Shared.Rent(len);
            try
            {
                Synthesize(e.Kind, buf.AsSpan(0, len), rate, e.Intensity, seed * 7919 + k);
                var (gl, gr) = SoundDsp.Pan(e.Pan);
                gl *= e.Gain; gr *= e.Gain;
                for (var i = 0; i < len; i++) { l[start + i] += buf[i] * gl; r[start + i] += buf[i] * gr; }
            }
            finally { ArrayPool<float>.Shared.Return(buf); }
        }
        return (l, r);
    }

    /// <summary>특정 종류 이벤트만 모노 렌더(Atmos 오브젝트용).</summary>
    public static float[] RenderKindMono(IReadOnlyList<FoleyEvent> events, FoleyKind kind, int totalSamples, int rate, int seed = 1)
    {
        var y = new float[Math.Max(0, totalSamples)];
        for (var k = 0; k < events.Count; k++)
        {
            var e = events[k];
            if (e.Kind != kind) continue;
            var start = (int)(e.Start * rate);
            var len = Math.Min(y.Length - start, (int)(e.Duration * rate));
            if (start >= y.Length || len <= 0) continue;
            var buf = ArrayPool<float>.Shared.Rent(len);
            try
            {
                Synthesize(e.Kind, buf.AsSpan(0, len), rate, e.Intensity, seed * 7919 + k);
                for (var i = 0; i < len; i++) y[start + i] += buf[i] * e.Gain;
            }
            finally { ArrayPool<float>.Shared.Return(buf); }
        }
        return y;
    }

    /// <summary>모션 + 텍스트 → 계획/렌더(동기 · 테스트/외부 모션 주입).</summary>
    public FoleyPlan Generate(ReadOnlySpan<MotionSample> motion, double fps, double duration, string? text, int seed = 1)
    {
        var mask = DetectKeywords(text);
        var events = PlanEvents(motion, fps, mask, duration, seed);
        var (l, r) = RenderTrack(events, duration, Rate, seed);
        var energy = new float[motion.Length];
        for (var i = 0; i < motion.Length; i++) energy[i] = motion[i].Energy;
        var plan = new FoleyPlan(events, l, r, Rate, Math.Clamp(duration, 0, MaxSeconds), energy, fps, mask);
        LastPlan = plan;
        Interlocked.Add(ref _generated, events.Count);
        LastAction = "🔊 Foley " + events.Count.ToString(CultureInfo.InvariantCulture) + "건 · " + MaskLabel(mask);
        Raise();
        return plan;
    }

    public static string MaskLabel(FoleyMask m)
    {
        if (m == FoleyMask.None) return "키워드 없음(모션 전용)";
        var sb = new StringBuilder();
        for (var k = 0; k < 8; k++) if (((int)m & (1 << k)) != 0) { if (sb.Length > 0) sb.Append('·'); sb.Append(Label((FoleyKind)k)); }
        return sb.ToString();
    }

    // ══════════════════════ 영상 분석 · 인젝션 ══════════════════════

    /// <summary>영상 → 모션 분석 → Foley 계획. ffmpeg 부재/동시 실행 Graceful(null).</summary>
    public async Task<FoleyPlan?> GenerateAsync(string videoPath, string? sceneText, CancellationToken ct = default)
    {
        var ffmpeg = _ffmpeg();
        if (ffmpeg is null || string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath)) { LastAction = "⚠ Foley 분석 불가(ffmpeg/파일 확인)"; Raise(); return null; }
        if (Interlocked.Exchange(ref _busy, 1) == 1) return null;
        LastAction = "🔍 모션 분석 중 · " + Path.GetFileName(videoPath);
        Raise();
        byte[]? a = null, b = null;
        try
        {
            var probe = await ShoppableVideoOverlayEngine.ProbeAsync(ffmpeg, videoPath, ct).ConfigureAwait(false);
            if (probe is null) return null;
            var w = MotionWidth;
            var h = Math.Max(8, (int)Math.Round(MotionWidth * (double)probe.Value.H / probe.Value.W) & ~1);
            var ci = CultureInfo.InvariantCulture;
            var psi = new ProcessStartInfo { FileName = ffmpeg, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var x in new[] { "-hide_banner", "-v", "error", "-t", MaxSeconds.ToString(ci), "-i", videoPath, "-vf", "fps=" + MotionFps.ToString(ci) + ",scale=" + w.ToString(ci) + ":" + h.ToString(ci),
                         "-pix_fmt", "gray", "-f", "rawvideo", "pipe:1" })
                psi.ArgumentList.Add(x);
            using var p = new Process { StartInfo = psi };
            if (!p.Start()) return null;
            using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
            d.CancelAfter(TimeSpan.FromMinutes(5));
            _ = p.StandardError.ReadToEndAsync(d.Token);
            var n = w * h;
            a = ArrayPool<byte>.Shared.Rent(n); b = ArrayPool<byte>.Shared.Rent(n);
            var motion = new List<MotionSample>(1024);
            var first = true;
            try
            {
                while (motion.Count < MaxSeconds * MotionFps && await Spatial3dGaussianSplattingEngine.ReadExactAsync(p.StandardOutput.BaseStream, b, n, d.Token).ConfigureAwait(false))
                {
                    motion.Add(first ? default : AnalyzeMotion(a.AsSpan(0, n), b.AsSpan(0, n), w, h));
                    first = false;
                    (a, b) = (b, a);
                }
            }
            finally { try { if (!p.HasExited) p.Kill(true); } catch { } }
            var dur = probe.Value.Duration > 0 ? probe.Value.Duration : motion.Count / MotionFps;
            return Generate(motion.ToArray(), MotionFps, dur, sceneText, CinematicSeed(videoPath));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex) { Program.Log("Foley.Generate", ex); LastAction = "⚠ Foley 실패: " + ex.Message; return null; }
        finally
        {
            if (a is not null) ArrayPool<byte>.Shared.Return(a);
            if (b is not null) ArrayPool<byte>.Shared.Return(b);
            Volatile.Write(ref _busy, 0);
            Raise();
        }
    }

    private static int CinematicSeed(string s) { unchecked { var h = 17; foreach (var c in Path.GetFileName(s)) h = h * 31 + c; return h; } }

    public static List<string> BuildInjectArgs(string video, string audio, string output, bool videoHasAudio) => videoHasAudio
        ? new List<string> { "-hide_banner", "-v", "error", "-y", "-i", video, "-i", audio, "-filter_complex", "[0:a][1:a]amix=inputs=2:duration=first:normalize=0[a]",
            "-map", "0:v:0", "-map", "[a]", "-c:v", "copy", "-c:a", "aac", "-b:a", "256k", "-movflags", "+faststart", output }
        : new List<string> { "-hide_banner", "-v", "error", "-y", "-i", video, "-i", audio, "-map", "0:v:0", "-map", "1:a:0", "-c:v", "copy", "-c:a", "aac", "-b:a", "256k",
            "-shortest", "-movflags", "+faststart", output };

    /// <summary>오디오를 영상에 인젝션(원본 오디오 amix → 실패 시 단독 매핑 폴백). 성공 시 출력 경로.</summary>
    public async Task<string?> InjectAsync(string video, string audioWav, string output, CancellationToken ct = default)
    {
        var ffmpeg = _ffmpeg();
        if (ffmpeg is null || !File.Exists(video) || !File.Exists(audioWav)) return null;
        foreach (var withAudio in new[] { true, false })
        {
            if (await RunAsync(ffmpeg, BuildInjectArgs(video, audioWav, output, withAudio), ct).ConfigureAwait(false) == 0 && File.Exists(output))
            {
                LastAction = "💉 오디오 인젝션 · " + Path.GetFileName(output);
                Raise();
                return output;
            }
        }
        try { if (File.Exists(output)) File.Delete(output); } catch (Exception ex) { Program.Log("Foley.Cleanup", ex); }
        return null;
    }

    internal static async Task<int> RunAsync(string exe, List<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo { FileName = exe, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        try
        {
            if (!p.Start()) return -1;
            using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
            d.CancelAfter(TimeSpan.FromMinutes(10));
            _ = p.StandardOutput.ReadToEndAsync(d.Token);
            _ = p.StandardError.ReadToEndAsync(d.Token);
            await p.WaitForExitAsync(d.Token).ConfigureAwait(false);
            return p.ExitCode;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return -2; }
        catch (System.ComponentModel.Win32Exception ex) { Program.Log("Sound.Run", ex); return -3; }
        finally { try { if (!p.HasExited) p.Kill(true); } catch { } }
    }

    private void Raise()
    {
        var h = OnChange;
        if (h is null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action)d)(); } catch (Exception ex) { Program.Log("Foley.OnChange", ex); }
        }
    }
}
