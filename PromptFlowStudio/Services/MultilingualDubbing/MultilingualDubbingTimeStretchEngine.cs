using PromptFlowStudio.Services.GlobalDubbing;
using PromptFlowStudio.Services.LiveDubbing;
using PromptFlowStudio.Services.Localization.Dubbing;
using PromptFlowStudio.Services.ViralVoiceFx;

namespace PromptFlowStudio.Services.MultilingualDubbing;

/// <summary>원본 화자 음색 — 기본 주파수(Hz) · 포먼트 배율(중립 성도 대비).</summary>
public readonly record struct SpeakerTimbre(float F0Hz, float Formant);

/// <summary>v17.82.0 더빙 트랙 1개 결과(합성 길이 → WSOLA 배율 → 컷 길이 정합).</summary>
public struct DubTrackInfo
{
    public DubLanguage Lang;
    public int Phonemes, RawLength, Length;
    public float RequiredRatio, Ratio;
    public bool Clamped;
    public double ResidualMs, SynthUs, StretchUs;
}

/// <summary>
/// [PromptFlow Studio v17.82.0] 🗣️ 다국어 보이스 더빙 &amp; 무할당 타임-스트레치 엔진(<see cref="RenderTrack"/> · <see cref="Stretch"/> 무할당).
///   ▸ v17.64 번역 음소(<see cref="PhonemeEvent"/>) → 언어별 운율(속도 · 억양 · 하강) 성문 펄스(Rosenberg) + 2-포먼트 공진기/마찰 잡음 절차 합성(중립 성도 · 120 Hz).
///   ▸ v17.80 <see cref="RealtimeVoiceEmotionShifterEngine"/> SOLA 피치/포먼트로 원본 화자 음색(<see cref="SpeakerTimbre"/>) 이식 — 지연 2400 샘플 보상.
///   ▸ WSOLA: 1024 Hann 프레임 · 합성 홉 512 · 분석 홉 512/r · ±256 탐색 정규화 상호상관(2×4 데시메이션)으로 파형 동기 중첩 가산 — 배율 0.8~1.25×.
///   ▸ 원본 컷 길이에 정합: 필요 배율이 범위 밖이면 클램프 후 잔여분 무음 패딩/10 ms 페이드 절단 · 잔차 ms 보고.
/// </summary>
public sealed class MultilingualDubbingTimeStretchEngine
{
    public const int Fs = 48_000, MaxSec = 12, MaxSamples = Fs * MaxSec, FrameN = 1024, HopS = FrameN / 2, Tolerance = 256, MaxPhonemes = 1024;
    public const float MinRatio = 0.8f, MaxRatio = 1.25f, NeutralF0 = 120f;
    private const int FadeOut = Fs / 100, TimbreHop = 4800, TimbreWin = 512, MinLag = 80, MaxLag = 600, TimbreCap = 256;
    private const float Peak = 0.5f;

    public static readonly DubLanguage[] Targets = { DubLanguage.En, DubLanguage.Es, DubLanguage.Ja };
    private static readonly float[] Hann = BuildHann();

    private readonly RealtimeVoiceEmotionShifterEngine _shifter = new();
    private readonly float[] _raw = new float[MaxSamples];
    private readonly float[] _blk = new float[RealtimeVoiceEmotionShifterEngine.BlockMax];
    private readonly float[] _f0s = new float[TimbreCap];
    private readonly float _neutralRmsHz;
    private uint _noise = 0x2545F491u;

    public MultilingualDubbingTimeStretchEngine()
    {
        // 중립 성도 기준 RMS 주파수(포먼트 배율 추정 분모) — 생성자 1회
        Span<PhonemeEvent> ref5 = stackalloc PhonemeEvent[5];
        var vs = new[] { LipPhoneme.A, LipPhoneme.E, LipPhoneme.I, LipPhoneme.O, LipPhoneme.U };
        for (var i = 0; i < 5; i++) { ref5[i].Ph = vs[i]; ref5[i].DurMs = 160f; }
        var n = Synthesize(ref5, DubLanguage.Ko, _raw);
        _neutralRmsHz = RmsFrequency(_raw.AsSpan(0, n));
    }

    public long Tracks { get; private set; }
    public long Clamps { get; private set; }
    public long Frames { get; private set; }
    public double LastStretchUs { get; private set; }
    public RealtimeVoiceEmotionShifterEngine Shifter => _shifter;

    public static string LangName(DubLanguage l) => l switch
    {
        DubLanguage.Ko => "한국어(원본)", DubLanguage.En => "English", DubLanguage.Es => "Español", DubLanguage.Ja => "日本語", _ => l.ToString(),
    };

    public static string LangFlag(DubLanguage l) => l switch
    {
        DubLanguage.Ko => "🇰🇷", DubLanguage.En => "🇺🇸", DubLanguage.Es => "🇪🇸", DubLanguage.Ja => "🇯🇵", _ => "🏳️",
    };

    /// <summary>언어 운율 — 음소 길이 배율 · 억양 배율 · 문장 하강 폭.</summary>
    public static void Prosody(DubLanguage l, out float dur, out float f0, out float decl)
    {
        switch (l)
        {
            case DubLanguage.En: dur = 1.00f; f0 = 1.00f; decl = 0.20f; break;
            case DubLanguage.Es: dur = 0.94f; f0 = 1.04f; decl = 0.14f; break;
            case DubLanguage.Ja: dur = 0.96f; f0 = 1.06f; decl = 0.10f; break;
            default: dur = 1.00f; f0 = 1.00f; decl = 0.16f; break;
        }
    }

    // ══════════════════════ 한국어 원본 음소(비핫패스) ══════════════════════
    private static readonly LipPhoneme[] Cho =
    {
        LipPhoneme.Kg, LipPhoneme.Kg, LipPhoneme.N, LipPhoneme.Td, LipPhoneme.Td, LipPhoneme.L, LipPhoneme.Pbm, LipPhoneme.Pbm, LipPhoneme.Pbm, LipPhoneme.Sz,
        LipPhoneme.Sz, LipPhoneme.Rest, LipPhoneme.Sz, LipPhoneme.Sz, LipPhoneme.Sz, LipPhoneme.Kg, LipPhoneme.Td, LipPhoneme.Pbm, LipPhoneme.Rest,
    };

    private static readonly LipPhoneme[] Jung =
    {
        LipPhoneme.A, LipPhoneme.E, LipPhoneme.A, LipPhoneme.E, LipPhoneme.O, LipPhoneme.E, LipPhoneme.O, LipPhoneme.E, LipPhoneme.O, LipPhoneme.A, LipPhoneme.E,
        LipPhoneme.E, LipPhoneme.O, LipPhoneme.U, LipPhoneme.O, LipPhoneme.E, LipPhoneme.I, LipPhoneme.U, LipPhoneme.I, LipPhoneme.I, LipPhoneme.I,
    };

    /// <summary>한글 음절 분해(초 55 · 중 120 · 종 60 · 띄어쓰기 110 · 구두점 220 ms) → 음소 수.</summary>
    public static int KoreanPhonemes(ReadOnlySpan<char> text, Span<PhonemeEvent> dst)
    {
        var n = 0; double t = 0;
        foreach (var c in text)
        {
            if (n + 3 > dst.Length) break;
            if (c is >= '가' and <= '힣')
            {
                var code = c - 0xAC00; var cho = code / 588; var jung = code % 588 / 28; var jong = code % 28;
                if (Cho[cho] != LipPhoneme.Rest) Put(dst, ref n, ref t, Cho[cho], 55f);
                Put(dst, ref n, ref t, Jung[jung], 120f);
                if (jong > 0) Put(dst, ref n, ref t, jong switch { 4 or 5 or 6 or 21 => LipPhoneme.N, 16 or 17 or 18 => LipPhoneme.Pbm, >= 8 and <= 15 => LipPhoneme.L, _ => LipPhoneme.Td }, 60f);
            }
            else if (c == ' ') Put(dst, ref n, ref t, LipPhoneme.Rest, 110f);
            else if (c is '.' or ',' or '!' or '?') Put(dst, ref n, ref t, LipPhoneme.Rest, 220f);
        }
        return n;
    }

    private static void Put(Span<PhonemeEvent> dst, ref int n, ref double t, LipPhoneme p, float ms)
    {
        dst[n].Ph = p; dst[n].DurMs = ms; dst[n].StartMs = t; dst[n].Chunk = 0;
        t += ms; n++;
    }

    // ══════════════════════ 절차 합성(무할당) ══════════════════════
    private static void Formants(LipPhoneme p, out float f1, out float f2, out float voice, out float noise, out float amp)
    {
        voice = 1f; noise = 0f; amp = 1f;
        switch (p)
        {
            case LipPhoneme.A: f1 = 750; f2 = 1200; break;
            case LipPhoneme.E: f1 = 480; f2 = 1850; break;
            case LipPhoneme.I: f1 = 300; f2 = 2300; break;
            case LipPhoneme.O: f1 = 500; f2 = 880; break;
            case LipPhoneme.U: f1 = 330; f2 = 780; break;
            case LipPhoneme.N: f1 = 260; f2 = 1500; amp = 0.35f; break;
            case LipPhoneme.L: f1 = 360; f2 = 1100; amp = 0.6f; break;
            case LipPhoneme.R: f1 = 420; f2 = 1300; amp = 0.6f; break;
            case LipPhoneme.W: f1 = 320; f2 = 700; amp = 0.6f; break;
            case LipPhoneme.Pbm: f1 = 250; f2 = 900; amp = 0.25f; break;
            case LipPhoneme.Fv: f1 = 1800; f2 = 4200; voice = 0.15f; noise = 0.45f; amp = 0.5f; break;
            case LipPhoneme.Sz: f1 = 5200; f2 = 7600; voice = 0f; noise = 0.9f; amp = 0.55f; break;
            case LipPhoneme.Th: f1 = 3000; f2 = 5500; voice = 0f; noise = 0.6f; amp = 0.4f; break;
            case LipPhoneme.Td: f1 = 3400; f2 = 4600; voice = 0.1f; noise = 0.8f; amp = 0.5f; break;
            case LipPhoneme.Kg: f1 = 1800; f2 = 2600; voice = 0.1f; noise = 0.8f; amp = 0.5f; break;
            default: f1 = 500; f2 = 1500; voice = 0f; amp = 0f; break;
        }
    }

    /// <summary>음소 → 중립 성도 PCM(피크 0.5 정규화) · 반환 샘플 수.</summary>
    public int Synthesize(ReadOnlySpan<PhonemeEvent> ph, DubLanguage lang, Span<float> dst)
    {
        Prosody(lang, out var durK, out var f0K, out var decl);
        long total = 0;
        for (var i = 0; i < ph.Length; i++) total += (long)(ph[i].DurMs * durK * Fs / 1000f);
        if (total <= 0) return 0;
        var n = 0; var phase = 0f; var gPrev = 0f; var lp = 0f;
        float a1 = 0, a2 = 0, b1 = 0, b2 = 0, g1 = 0, g2 = 0, y11 = 0, y12 = 0, y21 = 0, y22 = 0;
        for (var i = 0; i < ph.Length && n < dst.Length; i++)
        {
            var len = Math.Min((int)(ph[i].DurMs * durK * Fs / 1000f), dst.Length - n);
            Formants(ph[i].Ph, out var f1, out var f2, out var voice, out var noise, out var amp);
            Resonator(f1, f1 < 1000 ? 90f : 400f, out a1, out a2, out g1);
            Resonator(f2, f2 < 3000 ? 140f : 900f, out b1, out b2, out g2);
            var closure = ph[i].Ph == LipPhoneme.Pbm ? len * 6 / 10 : ph[i].Ph is LipPhoneme.Td or LipPhoneme.Kg ? len / 2 : 0;
            var att = Math.Max(1, Math.Min(len / 4, Fs / 200));
            for (var s = 0; s < len; s++, n++)
            {
                var prog = (float)n / total;
                var f0 = NeutralF0 * f0K * (1f + decl * (0.5f - prog)) * (1f + 0.012f * MathF.Sin(n * (2f * MathF.PI * 5.2f / Fs)));
                phase += f0 / Fs; if (phase >= 1f) phase -= 1f;
                var g = phase < 0.4f ? 0.5f * (1f - MathF.Cos(MathF.PI * phase / 0.4f)) : phase < 0.56f ? MathF.Cos(MathF.PI * (phase - 0.4f) / 0.32f) : 0f;
                var glot = g - gPrev; gPrev = g;
                _noise = _noise * 1664525u + 1013904223u;
                var wn = ((_noise >> 9) * (1f / 8388608f)) - 1f;
                var x = glot * 18f * voice + wn * noise;
                var y1 = g1 * x + a1 * y11 + a2 * y12; y12 = y11; y11 = y1;
                var y2 = g2 * x + b1 * y21 + b2 * y22; y22 = y21; y21 = y2;
                var env = Math.Min(1f, Math.Min((float)s / att, (float)(len - s) / att));
                if (s < closure) env *= ph[i].Ph == LipPhoneme.Pbm ? 0.15f : 0.05f;
                lp += 0.6f * (y1 + 0.6f * y2 - lp);
                dst[n] = lp * env * amp;
            }
        }
        Normalize(dst[..n]);
        return n;
    }

    private static void Resonator(float f, float bw, out float a1, out float a2, out float g)
    {
        var r = MathF.Exp(-MathF.PI * bw / Fs);
        a1 = 2f * r * MathF.Cos(2f * MathF.PI * Math.Min(f, Fs * 0.45f) / Fs); a2 = -r * r; g = 1f - r;
    }

    private static void Normalize(Span<float> x)
    {
        var m = 0f;
        for (var i = 0; i < x.Length; i++) { var a = MathF.Abs(x[i]); if (a > m) m = a; }
        if (m < 1e-9f) return;
        var k = Peak / m;
        for (var i = 0; i < x.Length; i++) x[i] *= k;
    }

    /// <summary>v17.80 SOLA 엔진으로 화자 음색 이식(제자리 · 고정 지연 보상).</summary>
    public void ApplyTimbre(Span<float> buf, SpeakerTimbre timbre)
    {
        const int L = RealtimeVoiceEmotionShifterEngine.Latency;
        _shifter.Reset();
        _shifter.SetTarget(new VoiceEmotionProfile(timbre.F0Hz / NeutralF0, timbre.Formant, 0f, 1f));
        var len = buf.Length; var total = len + L;
        for (var off = 0; off < total; off += _blk.Length)
        {
            var m = Math.Min(_blk.Length, total - off);
            var b = _blk.AsSpan(0, m);
            for (var i = 0; i < m; i++) b[i] = off + i < len ? buf[off + i] : 0f;
            _shifter.Process(b, b);
            for (var i = 0; i < m; i++) { var o = off + i - L; if ((uint)o < (uint)len) buf[o] = b[i]; }
        }
    }

    // ══════════════════════ WSOLA(무할당 핫패스) ══════════════════════
    /// <summary>파형 유사도 중첩 가산 타임 스케일(출력 길이 = 입력 × r · r ∈ [0.8, 1.25]) · 반환 출력 샘플 수.</summary>
    public int Stretch(ReadOnlySpan<float> src, Span<float> dst, float ratio)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        ratio = Math.Clamp(ratio, MinRatio, MaxRatio);
        var outLen = Math.Min(dst.Length, (int)(src.Length * ratio));
        dst[..outLen].Clear();
        var ha = HopS / ratio; var prev = 0;
        for (var k = 0; ; k++)
        {
            var outPos = k * HopS;
            if (outPos >= outLen) break;
            var best = Math.Min((int)(k * ha), Math.Max(0, src.Length - 1));
            if (k > 0) best = Search(src, prev + HopS, best);
            var lim = Math.Min(FrameN, outLen - outPos);
            for (var i = 0; i < lim; i++)
            {
                var s = best + i;
                if (s >= src.Length) break;
                dst[outPos + i] += src[s] * Hann[i];
            }
            prev = best;
            Frames++;
        }
        LastStretchUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        return outLen;
    }

    /// <summary>자연 연속 구간(nat) 과 후보(nominal ± 256) 정규화 상호상관 최대 위치.</summary>
    private static int Search(ReadOnlySpan<float> src, int nat, int nominal)
    {
        if (nat + HopS > src.Length) return nominal;
        var lo = Math.Max(0, nominal - Tolerance); var hi = Math.Min(src.Length - HopS, nominal + Tolerance);
        var best = nominal; var bestScore = float.NegativeInfinity;
        for (var c = lo; c <= hi; c += 2)
        {
            float xy = 0, yy = 1e-9f;
            for (var i = 0; i < HopS; i += 4) { var y = src[c + i]; xy += src[nat + i] * y; yy += y * y; }
            var score = xy / MathF.Sqrt(yy);
            if (score > bestScore) { bestScore = score; best = c; }
        }
        return best;
    }

    /// <summary>원본 컷 길이(<paramref name="targetLen"/>) 정합 — 필요 배율 클램프 → WSOLA → 패딩/페이드 절단.</summary>
    public void Fit(ReadOnlySpan<float> src, Span<float> dst, int targetLen, ref DubTrackInfo info)
    {
        targetLen = Math.Min(targetLen, dst.Length);
        info.RawLength = src.Length;
        info.RequiredRatio = src.Length == 0 ? 1f : (float)targetLen / src.Length;
        info.Ratio = Math.Clamp(info.RequiredRatio, MinRatio, MaxRatio);
        info.Clamped = info.Ratio != info.RequiredRatio;
        if (info.Clamped) Clamps++;
        var n = Stretch(src, dst[..targetLen], info.Ratio);
        var natural = (int)(src.Length * info.Ratio);
        if (n < targetLen) dst[n..targetLen].Clear();
        else if (natural > targetLen)
            for (var i = 0; i < FadeOut && i < targetLen; i++) dst[targetLen - 1 - i] *= (float)i / FadeOut;
        info.Length = targetLen;
        info.ResidualMs = (natural - targetLen) * 1000.0 / Fs;
        info.StretchUs = LastStretchUs;
    }

    /// <summary>음소 → 합성 → 음색 이식 → 컷 길이 WSOLA 정합(무할당).</summary>
    public void RenderTrack(ReadOnlySpan<PhonemeEvent> ph, DubLanguage lang, SpeakerTimbre timbre, int targetLen, Span<float> dst, out DubTrackInfo info)
    {
        info = default;
        info.Lang = lang; info.Phonemes = ph.Length;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var n = Synthesize(ph, lang, _raw);
        ApplyTimbre(_raw.AsSpan(0, n), timbre);
        info.SynthUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        Fit(_raw.AsSpan(0, n), dst, targetLen, ref info);
        Tracks++;
    }

    // ══════════════════════ 음색 분석(비핫패스 · 무할당) ══════════════════════
    /// <summary>원본 음성 화자 음색 추정 — 100 ms 간격 NCCF F0 중앙값 · RMS 주파수 / 중립 기준 → 포먼트 배율.</summary>
    public SpeakerTimbre AnalyzeTimbre(ReadOnlySpan<float> pcm)
    {
        var cnt = 0;
        for (var p = 0; p + TimbreWin + MaxLag < pcm.Length && cnt < TimbreCap; p += TimbreHop)
        {
            var w = pcm.Slice(p, TimbreWin + MaxLag + 1);
            float e0 = 0; for (var i = 0; i < TimbreWin; i++) e0 += w[i] * w[i];
            if (e0 < TimbreWin * 1e-4f) continue;
            var bestLag = 0; var bestR = 0f;
            for (var lag = MinLag; lag <= MaxLag; lag++)
            {
                float xy = 0, yy = 1e-9f;
                for (var i = 0; i < TimbreWin; i++) { var y = w[i + lag]; xy += w[i] * y; yy += y * y; }
                var r = xy / MathF.Sqrt(e0 * yy);
                if (r > bestR) { bestR = r; bestLag = lag; }
            }
            if (bestR > 0.6f) _f0s[cnt++] = (float)Fs / bestLag;
        }
        var f0 = NeutralF0;
        if (cnt > 0) { var s = _f0s.AsSpan(0, cnt); s.Sort(); f0 = s[cnt / 2]; }
        var formant = _neutralRmsHz > 0 ? Math.Clamp(RmsFrequency(pcm) / _neutralRmsHz, RealtimeVoiceEmotionShifterEngine.MinRatio, RealtimeVoiceEmotionShifterEngine.MaxRatio) : 1f;
        return new SpeakerTimbre(f0, formant);
    }

    /// <summary>RMS 주파수 = Fs/2π · √(Σ Δx² / Σ x²).</summary>
    private static float RmsFrequency(ReadOnlySpan<float> x)
    {
        double e = 0, d = 0;
        for (var i = 1; i < x.Length; i++) { e += x[i] * x[i]; var dx = x[i] - x[i - 1]; d += dx * dx; }
        return e < 1e-12 ? 0f : (float)(Fs / (2 * Math.PI) * Math.Sqrt(d / e));
    }

    private static float[] BuildHann()
    {
        var h = new float[FrameN];
        for (var i = 0; i < FrameN; i++) h[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / FrameN);   // 주기형 — 50 % 중첩 합 = 1
        return h;
    }
}
