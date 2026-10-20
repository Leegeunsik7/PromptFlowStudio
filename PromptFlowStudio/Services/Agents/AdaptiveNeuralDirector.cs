using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PromptFlowStudio.Services.Agents;

/// <summary>샷 사이즈(구도) — 원경 → 극단 클로즈업.</summary>
public enum NeuralShotSize { ExtremeWide, Wide, Medium, CloseUp, ExtremeCloseUp }

/// <summary>카메라 워킹.</summary>
public enum NeuralCameraMove { Static, Push, Dolly, Pan, Orbit, Crane, Handheld }

/// <summary>컷 진입 트랜지션.</summary>
public enum NeuralTransition { Cut, Dissolve, Whip, Match }

/// <summary>프롬프트 맥락 감정 — 밸런스(-1~1)·각성도(0~1).</summary>
public sealed record NeuralMood(double Valence, double Arousal, string Label, IReadOnlyList<string> Cues);

/// <summary>단일 3D 샷 — 시간 구간 + 구도 + 카메라 포즈(요·피치·거리·FOV).</summary>
public sealed record NeuralShot(
    int Index, double StartSec, double DurationSec,
    NeuralShotSize Size, NeuralCameraMove Move, NeuralTransition In,
    double Yaw, double Pitch, double Distance, double Fov, double Energy);

/// <summary>연출 플랜 — 동일 입력이면 동일 지문(멱등).</summary>
public sealed record NeuralDirectorPlan(
    long Revision, NeuralMood Mood, double TempoBpm, double EffectiveArousal, double DurationSec,
    IReadOnlyList<NeuralShot> Shots, int Onsets, string Canonical, string Digest, double ElapsedMs);

/// <summary>
/// v9.5.1 적응형 뉴럴 디렉터 — 타임라인 오디오 스펙트럼(Goertzel 3밴드 플럭스·RMS)과
/// 프롬프트 감정 맥락을 결합해 컷 전환 시점·샷 사이즈·카메라 워킹·3D 포즈를 자율 오케스트레이션한다.
/// 오디오 부재 시 감정 기반 합성 엔벨로프로 Graceful 폴백. 순수 BCL.
/// </summary>
public sealed class AdaptiveNeuralDirector
{
    public const string PromptMarker = "[ND-SHOT]";
    private const int FramesPerSecond = 20;
    private const int MaxShots = 24;
    private const double SnapSec = 0.5;
    private const double MaxShotSec = 6.0;

    private static readonly string[] PositiveCues = { "행복", "기쁨", "밝", "희망", "사랑", "축제", "승리", "happy", "joy", "bright", "hope", "love", "festival", "victory" };
    private static readonly string[] NegativeCues = { "슬픔", "어둠", "비극", "공포", "분노", "이별", "절망", "sad", "dark", "tragic", "fear", "anger", "horror", "grief" };
    private static readonly string[] HighCues = { "액션", "추격", "폭발", "전투", "빠른", "긴박", "역동", "action", "chase", "explosion", "battle", "fast", "intense", "epic" };
    private static readonly string[] LowCues = { "잔잔", "고요", "명상", "느린", "평화", "서정", "calm", "quiet", "slow", "serene", "peaceful", "ambient" };
    private static readonly string[] OrbitCues = { "3d", "orbit", "회전", "공간", "입체", "360" };

    private readonly object _gate = new();
    private NeuralDirectorPlan? _last;
    private long _revision;

    public event Action? OnChange;

    public NeuralDirectorPlan? LastPlan { get { lock (_gate) return _last; } }
    public long PlanCount => Interlocked.Read(ref _revision);
    public string? LastAction { get; private set; }

    /// <summary>프롬프트 어휘 기반 감정 분석(한/영 렉시콘).</summary>
    public static NeuralMood AnalyzeMood(string? prompt)
    {
        var text = (prompt ?? string.Empty).ToLowerInvariant();
        var cues = new List<string>();
        int pos = Count(text, PositiveCues, cues), neg = Count(text, NegativeCues, cues);
        int hi = Count(text, HighCues, cues), lo = Count(text, LowCues, cues);
        var valence = (pos - neg) / (double)(pos + neg + 1);
        var arousal = Math.Clamp(0.5 + (hi - lo) * 0.2, 0, 1);
        var label = (valence >= 0, arousal >= 0.5) switch
        {
            (true, true) => "고조·환희",
            (false, true) => "긴장·위협",
            (true, false) => "서정·평온",
            _ => "침잠·비애",
        };
        return new NeuralMood(Math.Round(valence, 3), Math.Round(arousal, 3), label, cues);
    }

    /// <summary>프롬프트 감정 템포로 데모 오디오(킥·하이햇·패드)를 합성해 연출한다.</summary>
    public NeuralDirectorPlan DirectDemo(string? prompt, double seconds = 16)
    {
        seconds = Math.Clamp(seconds, 4, 60);
        const int sr = 16000;
        var n = (int)(seconds * sr);
        var buf = ArrayPool<float>.Shared.Rent(n);
        try
        {
            SynthesizeDemo(buf.AsSpan(0, n), sr, AnalyzeMood(prompt), StableSeed(prompt));
            return Direct(prompt, buf.AsSpan(0, n), sr, seconds);
        }
        finally { ArrayPool<float>.Shared.Return(buf); }
    }

    /// <summary>타임라인 PCM(모노 float) + 프롬프트 → 샷 플랜.</summary>
    public NeuralDirectorPlan Direct(string? prompt, ReadOnlySpan<float> pcm, int sampleRate, double durationSec = 0)
    {
        var sw = Stopwatch.StartNew();
        var mood = AnalyzeMood(prompt);
        var seed = StableSeed(prompt);
        var hop = sampleRate > 0 ? sampleRate / FramesPerSecond : 0;
        var frames = hop > 0 ? pcm.Length / hop : 0;
        var fromAudio = frames >= 8;
        if (fromAudio) durationSec = (double)frames / FramesPerSecond;
        else
        {
            // Graceful: 오디오 부재 → 감정 기반 합성 엔벨로프
            durationSec = Math.Clamp(durationSec <= 0 ? 12 : durationSec, 1, 600);
            frames = (int)(durationSec * FramesPerSecond);
        }

        var rmsBuf = ArrayPool<double>.Shared.Rent(frames);
        var fluxBuf = ArrayPool<double>.Shared.Rent(frames);
        try
        {
            var rms = rmsBuf.AsSpan(0, frames);
            var flux = fluxBuf.AsSpan(0, frames);
            if (fromAudio) AnalyzeAudio(pcm, sampleRate, hop, rms, flux);
            else SynthesizeEnvelope(mood, seed, rms, flux);

            double mean = 0, sq = 0;
            for (var i = 0; i < frames; i++) mean += flux[i];
            mean /= frames;
            for (var i = 0; i < frames; i++) { var d = flux[i] - mean; sq += d * d; }
            var threshold = mean + 1.2 * Math.Sqrt(sq / frames);

            var onsets = 0;
            for (var f = 1; f < frames - 1; f++)
                if (IsOnset(flux, f, threshold)) onsets++;
            var density = onsets / Math.Max(1, durationSec);
            var arousal = Math.Round(Math.Clamp(mood.Arousal * 0.6 + Math.Min(1, density / 2.5) * 0.4, 0, 1), 3);
            var tempo = EstimateTempo(flux, mood.Arousal);

            var minShot = Math.Clamp(Math.Max(4.0 - 2.5 * arousal, 2 * 60.0 / tempo), 1.5, 4.5);
            var cuts = new List<double>(MaxShots) { 0 };
            var last = 0.0;
            for (var f = 1; f < frames && cuts.Count < MaxShots; f++)
            {
                var t = (double)f / FramesPerSecond;
                var since = t - last;
                if (!((IsOnset(flux, f, threshold) && since >= minShot) || since >= MaxShotSec)) continue;
                var snapped = Math.Round(t / SnapSec) * SnapSec;
                if (snapped - last < 1.0 || durationSec - snapped < 1.0) continue;
                cuts.Add(snapped);
                last = snapped;
            }

            var shotEnergy = new double[cuts.Count];
            var maxShotEnergy = 1e-9;
            for (var i = 0; i < cuts.Count; i++)
            {
                var end = i + 1 < cuts.Count ? cuts[i + 1] : durationSec;
                int f0 = (int)(cuts[i] * FramesPerSecond), f1 = Math.Min(frames, Math.Max(f0 + 1, (int)(end * FramesPerSecond)));
                double acc = 0;
                for (var f = f0; f < f1; f++) acc += rms[f];
                shotEnergy[i] = acc / (f1 - f0);
                if (shotEnergy[i] > maxShotEnergy) maxShotEnergy = shotEnergy[i];
            }

            var orbit = ContainsAny((prompt ?? string.Empty).ToLowerInvariant(), OrbitCues);
            var shots = new List<NeuralShot>(cuts.Count);
            NeuralShotSize? prevSize = null;
            var prevEnergy = 0.0;
            for (var i = 0; i < cuts.Count; i++)
            {
                var start = cuts[i];
                var end = i + 1 < cuts.Count ? cuts[i + 1] : durationSec;
                var energy = Math.Round(Math.Clamp(shotEnergy[i] / maxShotEnergy, 0, 1), 3);

                var size = PickSize(i, cuts.Count, energy, arousal, prevSize);
                var move = PickMove(i, cuts.Count, energy, arousal, orbit);
                var trans = i == 0 ? NeuralTransition.Cut
                    : energy - prevEnergy > 0.35 ? NeuralTransition.Whip
                    : mood.Valence < -0.25 && energy < 0.45 ? NeuralTransition.Dissolve
                    : Family(size) == Family(prevSize!.Value) && Math.Abs(energy - prevEnergy) < 0.1 ? NeuralTransition.Match
                    : NeuralTransition.Cut;

                var jitter = ((seed >> (i * 5 % 58)) & 0x3F) / 63.0 - 0.5;
                var yaw = Math.Round(jitter * (move == NeuralCameraMove.Orbit ? 120 : 40), 1);
                var pitch = move == NeuralCameraMove.Crane ? -18 : Math.Round(-6 + energy * 8 - (size == NeuralShotSize.ExtremeWide ? 6 : 0), 1);
                var (dist, fov) = size switch
                {
                    NeuralShotSize.ExtremeWide => (12.0, 60.0),
                    NeuralShotSize.Wide => (7.0, 45.0),
                    NeuralShotSize.Medium => (3.5, 35.0),
                    NeuralShotSize.CloseUp => (1.6, 28.0),
                    _ => (0.8, 20.0),
                };
                shots.Add(new NeuralShot(i, start, Math.Round(end - start, 2), size, move, trans, yaw, pitch, dist, fov, energy));
                prevSize = size;
                prevEnergy = energy;
            }

            var canonical = BuildCanonical(mood, tempo, arousal, durationSec, onsets, shots);
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
            var plan = new NeuralDirectorPlan(Interlocked.Increment(ref _revision), mood, tempo, arousal, Math.Round(durationSec, 2),
                shots, onsets, canonical, digest, Math.Round(sw.Elapsed.TotalMilliseconds, 2));
            lock (_gate) _last = plan;
            LastAction = $"{mood.Label} · {tempo.ToString("0", CultureInfo.InvariantCulture)}BPM · {shots.Count}샷 · 온셋 {onsets}";
            OnChange?.Invoke();
            return plan;
        }
        finally
        {
            ArrayPool<double>.Shared.Return(rmsBuf);
            ArrayPool<double>.Shared.Return(fluxBuf);
        }
    }

    /// <summary>샷 지시를 프롬프트에 마커 기반 멱등 주입(기존 지시는 교체).</summary>
    public static string ApplyToPrompt(string? prompt, NeuralShot shot)
    {
        var text = prompt ?? string.Empty;
        var idx = text.IndexOf(PromptMarker, StringComparison.Ordinal);
        var core = (idx >= 0 ? text[..idx] : text).TrimEnd();
        var size = shot.Size switch
        {
            NeuralShotSize.ExtremeWide => "extreme wide establishing shot",
            NeuralShotSize.Wide => "wide shot",
            NeuralShotSize.Medium => "medium shot",
            NeuralShotSize.CloseUp => "close-up",
            _ => "extreme close-up",
        };
        var move = shot.Move switch
        {
            NeuralCameraMove.Push => "slow push-in",
            NeuralCameraMove.Dolly => "dolly tracking",
            NeuralCameraMove.Pan => "smooth pan",
            NeuralCameraMove.Orbit => "orbiting 3D camera",
            NeuralCameraMove.Crane => "crane descent",
            NeuralCameraMove.Handheld => "handheld kinetic camera",
            _ => "locked-off static camera",
        };
        var fov = shot.Fov.ToString("0", CultureInfo.InvariantCulture);
        return (core.Length > 0 ? core + " " : string.Empty) + $"{PromptMarker} {size}, {move}, {fov}° FOV";
    }

    private static void AnalyzeAudio(ReadOnlySpan<float> pcm, int sr, int hop, Span<double> rms, Span<double> flux)
    {
        double pl = 0, pm = 0, ph = 0, pr = 0;
        var hiF = Math.Min(6000, sr * 0.45);
        for (var f = 0; f < rms.Length; f++)
        {
            var win = pcm.Slice(f * hop, hop);
            double sum = 0;
            foreach (var v in win) sum += v * v;
            var r = Math.Sqrt(sum / hop);
            double lo = Goertzel(win, 60, sr), mid = Goertzel(win, 1200, sr), hi = Goertzel(win, hiF, sr);
            rms[f] = r;
            flux[f] = f == 0 ? 0 : Math.Max(0, lo - pl) + Math.Max(0, mid - pm) + Math.Max(0, hi - ph) + Math.Max(0, r - pr);
            pl = lo; pm = mid; ph = hi; pr = r;
        }
    }

    private static double Goertzel(ReadOnlySpan<float> x, double freq, int sr)
    {
        var c = 2 * Math.Cos(2 * Math.PI * freq / sr);
        double s1 = 0, s2 = 0;
        foreach (var v in x) { var s0 = v + c * s1 - s2; s2 = s1; s1 = s0; }
        return Math.Sqrt(Math.Max(0, s1 * s1 + s2 * s2 - c * s1 * s2)) / x.Length;
    }

    private static void SynthesizeEnvelope(NeuralMood mood, ulong seed, Span<double> rms, Span<double> flux)
    {
        var beat = 60.0 / (80 + mood.Arousal * 80);
        for (var f = 0; f < rms.Length; f++)
        {
            var t = (double)f / FramesPerSecond;
            var section = 0.35 + ((seed >> ((int)(t / 4) * 4 % 60)) & 0xF) / 15.0 * 0.65;
            rms[f] = section * (0.6 + 0.4 * Math.Exp(-(t % beat) / beat * 6));
            flux[f] = f == 0 ? 0 : Math.Max(0, rms[f] - rms[f - 1]);
        }
    }

    private static void SynthesizeDemo(Span<float> pcm, int sr, NeuralMood mood, ulong seed)
    {
        var beat = 60.0 / (80 + mood.Arousal * 80);
        var rng = new Random(unchecked((int)seed));
        Span<double> sections = stackalloc double[16];
        for (var i = 0; i < sections.Length; i++) sections[i] = 0.35 + rng.NextDouble() * 0.65;
        for (var n = 0; n < pcm.Length; n++)
        {
            var t = (double)n / sr;
            var sec = sections[(int)(t / 4) % 16];
            var bt = t % beat;
            var kick = Math.Exp(-bt * 18) * Math.Sin(2 * Math.PI * 60 * bt);
            var hat = mood.Arousal > 0.5 ? Math.Exp(-((t + beat / 2) % beat) * 60) * (rng.NextDouble() * 2 - 1) * 0.3 : 0;
            var pad = 0.08 * Math.Sin(2 * Math.PI * 220 * t) * (0.5 + 0.5 * Math.Sin(2 * Math.PI * t / 8));
            pcm[n] = (float)(sec * (0.7 * kick + hat) + pad);
        }
    }

    private static double EstimateTempo(ReadOnlySpan<double> flux, double arousal)
    {
        var fallback = Math.Round(80 + arousal * 80);
        if (flux.Length < 40) return fallback;
        Span<double> corr = stackalloc double[21];
        double best = 0;
        var bestLag = 0;
        for (var lag = 7; lag <= 20; lag++)
        {
            double acc = 0;
            for (var i = lag; i < flux.Length; i++) acc += flux[i] * flux[i - lag];
            corr[lag] = acc / (flux.Length - lag);
            if (corr[lag] > best) { best = corr[lag]; bestLag = lag; }
        }
        if (bestLag == 0 || best <= 1e-12) return fallback;
        // 옥타브 오류 보정: 절반 주기(2배 템포)도 강한 상관이면 채택
        int lo = bestLag / 2, hi = (bestLag + 1) / 2;
        if (lo >= 7 && Math.Max(corr[lo], corr[hi]) >= best * 0.5) return Math.Round(120.0 * FramesPerSecond / bestLag);
        return Math.Round(60.0 * FramesPerSecond / bestLag);
    }

    private static bool IsOnset(ReadOnlySpan<double> flux, int f, double threshold) =>
        flux[f] > threshold && flux[f] >= flux[f - 1] && (f + 1 >= flux.Length || flux[f] >= flux[f + 1]);

    private static NeuralShotSize PickSize(int i, int n, double energy, double arousal, NeuralShotSize? prev)
    {
        NeuralShotSize size;
        if (i == 0) size = arousal > 0.75 ? NeuralShotSize.Wide : NeuralShotSize.ExtremeWide;
        else if (i == n - 1 && n > 2) size = arousal > 0.7 ? NeuralShotSize.CloseUp : NeuralShotSize.Wide;
        else
        {
            var k = energy * 0.7 + arousal * 0.3;
            size = k > 0.8 ? NeuralShotSize.ExtremeCloseUp : k > 0.6 ? NeuralShotSize.CloseUp : k > 0.4 ? NeuralShotSize.Medium : NeuralShotSize.Wide;
        }
        // 동일 사이즈 연속 = 점프컷 → 한 단계 조정
        if (prev == size) size = size == NeuralShotSize.ExtremeCloseUp ? NeuralShotSize.CloseUp : size + 1;
        return size;
    }

    private static NeuralCameraMove PickMove(int i, int n, double energy, double arousal, bool orbit)
    {
        if (orbit && energy > 0.45 && i % 3 == 1) return NeuralCameraMove.Orbit;
        if (energy > 0.75) return arousal > 0.6 ? NeuralCameraMove.Handheld : NeuralCameraMove.Push;
        if (energy > 0.45) return i % 2 == 0 ? NeuralCameraMove.Dolly : NeuralCameraMove.Pan;
        return i == 0 || i == n - 1 ? NeuralCameraMove.Crane : NeuralCameraMove.Static;
    }

    private static int Family(NeuralShotSize s) => s switch
    {
        NeuralShotSize.ExtremeWide or NeuralShotSize.Wide => 0,
        NeuralShotSize.Medium => 1,
        _ => 2,
    };

    private static string BuildCanonical(NeuralMood mood, double tempo, double arousal, double duration, int onsets, List<NeuralShot> shots)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(64 + shots.Count * 48);
        sb.Append("ND|v").Append(mood.Valence.ToString("0.000", inv))
          .Append("|a").Append(arousal.ToString("0.000", inv))
          .Append("|bpm").Append(tempo.ToString("0", inv))
          .Append("|d").Append(duration.ToString("0.00", inv))
          .Append("|o").Append(onsets)
          .Append("|n").Append(shots.Count).Append('|');
        foreach (var s in shots)
            sb.Append(s.Index).Append(':').Append(s.StartSec.ToString("0.00", inv)).Append(':').Append(s.DurationSec.ToString("0.00", inv))
              .Append(':').Append((int)s.Size).Append(':').Append((int)s.Move).Append(':').Append((int)s.In)
              .Append(':').Append(s.Yaw.ToString("0.0", inv)).Append(':').Append(s.Pitch.ToString("0.0", inv))
              .Append(':').Append(s.Energy.ToString("0.000", inv)).Append(';');
        return sb.ToString();
    }

    private static int Count(string text, string[] lexicon, List<string> cues)
    {
        var n = 0;
        foreach (var w in lexicon)
            if (text.Contains(w, StringComparison.Ordinal)) { n++; cues.Add(w); }
        return n;
    }

    private static bool ContainsAny(string text, string[] lexicon)
    {
        foreach (var w in lexicon)
            if (text.Contains(w, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>프로세스 독립 안정 시드(FNV-1a 64) — string.GetHashCode 무작위화 회피.</summary>
    private static ulong StableSeed(string? text)
    {
        var h = 14695981039346656037UL;
        foreach (var ch in text ?? string.Empty) { h ^= ch; h *= 1099511628211UL; }
        return h;
    }
}
