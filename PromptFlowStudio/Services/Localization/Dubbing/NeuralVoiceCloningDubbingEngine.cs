using System.Numerics;
using System.Runtime.InteropServices;

namespace PromptFlowStudio.Services.Localization.Dubbing;

public enum Ph : byte { Sil, A, E, I, O, U, P, B, T, D, K, G, F, V, S, Z, Sh, H, M, N, Ng, L, R, W, Y }

/// <summary>음색 파라미터(복제 대상): 기본 F0 · 억양 폭(반음 σ) · 성도 길이 배율(포먼트 스케일) · 기식 · 지터 · 시머.</summary>
public struct VoiceTimbre
{
    public float F0, RangeSt, Vtf, Breathiness, Jitter, Shimmer;
    public static VoiceTimbre Actor => new() { F0 = 118f, RangeSt = 2.6f, Vtf = 1.0f, Breathiness = 0.18f, Jitter = 0.008f, Shimmer = 0.05f };
    public static VoiceTimbre Actress => new() { F0 = 205f, RangeSt = 3.2f, Vtf = 1.17f, Breathiness = 0.3f, Jitter = 0.006f, Shimmer = 0.04f };
}

/// <summary>발화 계획(선할당 · 음소/음절 타임라인 · 억양/에너지 윤곽 64점).</summary>
public sealed class DubPlan
{
    public const int MaxPhones = 192, MaxSyllables = 72, ContourPoints = 64;
    public readonly Ph[] Phone = new Ph[MaxPhones];
    public readonly float[] Start = new float[MaxPhones], Dur = new float[MaxPhones];
    public readonly short[] Syllable = new short[MaxPhones];
    public readonly float[] SylStart = new float[MaxSyllables], SylDur = new float[MaxSyllables];
    public readonly byte[] Tone = new byte[MaxSyllables];
    public readonly bool[] Stressed = new bool[MaxSyllables];
    public readonly float[] Pitch = new float[ContourPoints], Energy = new float[ContourPoints];
    public int Count, Syllables;
    public float Duration, NaturalDuration, RateScale = 1f;
    public bool Question;
    public DubLanguage Language;

    public void Clear() { Count = 0; Syllables = 0; Duration = 0f; Array.Clear(Pitch); Array.Fill(Energy, 1f); }

    /// <summary>시각 t(s)의 음소 인덱스(이진 탐색 · 휴지/범위 밖 = −1).</summary>
    public int PhoneAt(float t)
    {
        var k = PhoneBefore(t);
        return k >= 0 && t < Start[k] + Dur[k] ? k : -1;
    }

    /// <summary>Start ≤ t 인 마지막 음소(휴지 포함 · 없으면 −1).</summary>
    public int PhoneBefore(float t)
    {
        if (Count == 0 || t < Start[0]) return -1;
        int lo = 0, hi = Count - 1;
        while (lo < hi) { var m = (lo + hi + 1) >> 1; if (Start[m] <= t) lo = m; else hi = m - 1; }
        return lo;
    }

    public float ContourAt(float[] c, float t)
    {
        if (Duration <= 0f) return c[0];
        var x = Math.Clamp(t / Duration, 0f, 1f) * (ContourPoints - 1);
        var i = Math.Min((int)x, ContourPoints - 2); var f = x - i;
        return c[i] + (c[i + 1] - c[i]) * f;
    }
}

/// <summary>더빙 SIMD 커널(Vector&lt;float&gt; · 꼬리 스칼라 — 스칼라 판과 비트 일치(레인 누적 순서 재현)).</summary>
public static class DubSimd
{
    private static int Body(int n) => Vector.IsHardwareAccelerated && n >= Vector<float>.Count ? n - n % Vector<float>.Count : 0;

    /// <summary>내적(레인별 누적 → 레인 순서 합 → 꼬리).</summary>
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var n = a.Length; var m = Body(n); var acc = Vector<float>.Zero;
        if (m > 0)
        {
            var va = MemoryMarshal.Cast<float, Vector<float>>(a[..m]); var vb = MemoryMarshal.Cast<float, Vector<float>>(b[..m]);
            for (var k = 0; k < va.Length; k++) acc += va[k] * vb[k];
        }
        var s = 0f;
        for (var l = 0; l < Vector<float>.Count; l++) s += acc[l];
        for (var i = m; i < n; i++) s += a[i] * b[i];
        return s;
    }

    public static float DotScalar(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var n = a.Length; var m = Body(n); var w = Vector<float>.Count;
        Span<float> lanes = stackalloc float[w];
        lanes.Clear();
        for (var i = 0; i < m; i++) lanes[i % w] += a[i] * b[i];
        var s = 0f;
        for (var l = 0; l < w; l++) s += lanes[l];
        for (var i = m; i < n; i++) s += a[i] * b[i];
        return s;
    }

    /// <summary>dst = a·ga + b·gb(성대음 + 기식 혼합).</summary>
    public static void Mix(Span<float> dst, ReadOnlySpan<float> a, ReadOnlySpan<float> ga, ReadOnlySpan<float> b, ReadOnlySpan<float> gb)
    {
        var n = dst.Length; var m = Body(n);
        if (m > 0)
        {
            var d = MemoryMarshal.Cast<float, Vector<float>>(dst[..m]);
            var va = MemoryMarshal.Cast<float, Vector<float>>(a[..m]); var vga = MemoryMarshal.Cast<float, Vector<float>>(ga[..m]);
            var vb = MemoryMarshal.Cast<float, Vector<float>>(b[..m]); var vgb = MemoryMarshal.Cast<float, Vector<float>>(gb[..m]);
            for (var k = 0; k < d.Length; k++) d[k] = va[k] * vga[k] + vb[k] * vgb[k];
        }
        for (var i = m; i < n; i++) dst[i] = a[i] * ga[i] + b[i] * gb[i];
    }

    public static void MixScalar(Span<float> dst, ReadOnlySpan<float> a, ReadOnlySpan<float> ga, ReadOnlySpan<float> b, ReadOnlySpan<float> gb)
    {
        for (var i = 0; i < dst.Length; i++) dst[i] = a[i] * ga[i] + b[i] * gb[i];
    }
}

/// <summary>
/// [PromptFlow Studio v16.89.0] 🎙 실시간 음성 복제 &amp; 다국어 더빙 생성 엔진(24 kHz · 블록 400 = 60 Hz · 스트리밍 무할당).
///   ▸ 합성(학습 모델 미사용 · 소스-필터 보코더): 성문 펄스(sin² 개방 위상 · 방사 미분) + 지터/시머 + 기식 노이즈 → 포먼트 직렬 공진기 4(F1~F3 음소 목표 · 30% 전이 · ×성도 배율) ·
///     마찰(대역 공진 노이즈) · 파열(폐쇄 + 버스트) · 비음(약화 머머) · 혼합/게인 SIMD.
///   ▸ G2P: 로마자 근사 발음(공백 = 음절 · 쉼표 = 휴지 · 숫자 = 성조) → 25 음소 · 리듬(음절/강세/모라) · 강세 음절 1.35× · 등시성(원본 길이 맞춤 · 속도 0.75~1.35×).
///   ▸ 복제: 원본 음성 분석 — 자기상관 피치(12 kHz · 10 ms) → F0 중앙값/반음 σ · 정합 모음 중심 LPC(12차) 포락선 F1/F2 → 성도 배율 · HNR → 기식.
///   ▸ 운율 전이: 원본 z-반음 억양 + 에너지 윤곽(정규 시간 64점) → 대상 음절 타임라인 · 복제 F0/폭 · 성조 언어 = 윤곽 0.5× + 사전 성조 · 의문문 상승.
///   ▸ 지표: 재분석 음색 유사도 · 억양 상관(Pearson) · 등시성 오차.
/// </summary>
public sealed class NeuralVoiceCloningDubbingEngine
{
    public const int Fs = 24000, Block = 400, MaxSamples = Fs * 8, Frame = 240, MaxFrames = MaxSamples / Frame, LpcOrder = 12;

    private readonly struct PhSpec
    {
        public readonly float F1, F2, F3, Amp, Noise, NoiseHz, Closure;
        public readonly bool Voiced;
        public PhSpec(float f1, float f2, float f3, float amp, bool voiced, float noise = 0f, float noiseHz = 0f, float closure = 0f)
        { F1 = f1; F2 = f2; F3 = f3; Amp = amp; Voiced = voiced; Noise = noise; NoiseHz = noiseHz; Closure = closure; }
    }

    private static readonly PhSpec[] Specs =
    {
        new(500, 1500, 2500, 0f, false),                                          // Sil
        new(730, 1090, 2440, 1f, true), new(530, 1840, 2480, 0.9f, true), new(300, 2250, 3000, 0.8f, true), new(570, 840, 2410, 0.9f, true), new(320, 870, 2240, 0.8f, true),
        new(300, 1100, 2300, 0f, false, 0.5f, 1500, 0.65f), new(300, 1100, 2300, 0.15f, true, 0.35f, 1500, 0.65f),   // P B
        new(350, 1700, 2600, 0f, false, 0.5f, 4200, 0.6f), new(350, 1700, 2600, 0.15f, true, 0.35f, 4200, 0.6f),     // T D
        new(330, 2000, 2600, 0f, false, 0.5f, 2300, 0.6f), new(330, 2000, 2600, 0.15f, true, 0.35f, 2300, 0.6f),     // K G
        new(400, 1500, 2500, 0f, false, 0.3f, 6500), new(400, 1500, 2500, 0.3f, true, 0.25f, 6500),                   // F V
        new(380, 1700, 2600, 0f, false, 0.45f, 7500), new(380, 1700, 2600, 0.3f, true, 0.35f, 7500),                  // S Z
        new(380, 1800, 2400, 0f, false, 0.45f, 3200),                                                               // Sh
        new(600, 1500, 2500, 0f, false, 0.22f, 1500),                                                               // H
        new(280, 1100, 2300, 0.35f, true), new(280, 1700, 2500, 0.35f, true), new(280, 2000, 2600, 0.35f, true),     // M N Ng
        new(360, 1300, 2700, 0.55f, true), new(420, 1300, 1700, 0.5f, true), new(300, 700, 2200, 0.6f, true), new(280, 2200, 2900, 0.6f, true),
    };

    private readonly object _gate = new();
    private readonly float[] _src = new float[MaxSamples], _dub = new float[MaxSamples];
    private readonly float[] _dec = new float[MaxSamples / 2];
    private readonly float[] _srcF0 = new float[MaxFrames], _srcEnergy = new float[MaxFrames], _dubF0 = new float[MaxFrames];
    private readonly float[] _win = new float[Frame * 3 / 2], _r = new float[LpcOrder + 1], _lpc = new float[LpcOrder + 1], _tmpLpc = new float[LpcOrder + 1];
    private readonly float[] _voice = new float[Block], _noise = new float[Block], _gv = new float[Block], _gn = new float[Block], _mix = new float[Block];
    private readonly DubPlan _srcPlan = new(), _plan = new();
    private int _srcLen, _dubLen, _srcFrames, _dubFrames;
    private long _clock;
    // 보코더 상태(스트리밍)
    private float _ph, _prevG, _y1a, _y2a, _y1b, _y2b, _y1c, _y2c, _y1d, _y2d, _ny1, _ny2, _jit, _shim;
    private float _f1, _f2, _f3, _nHz = -1f, _nNorm = 1f;
    private const float OutputGain = 0.28f;
    private uint _rng = 0x13579BDFu;

    public NeuralVoiceCloningDubbingEngine() { }

    public VoiceTimbre SourceSpeaker { get; set; } = VoiceTimbre.Actor;
    public VoiceTimbre Clone { get; private set; } = VoiceTimbre.Actor;
    public VoiceTimbre DubMeasured { get; private set; }
    public VoiceTimbre SourceMeasured { get; private set; }
    public float TimbreSimilarity { get; private set; }
    public float ProsodyCorrelation { get; private set; }
    public float IsochronyError { get; private set; }
    public float SourceDuration => _srcPlan.Duration;
    public bool CloneTimbre { get; set; } = true;
    public bool TransferProsody { get; set; } = true;
    public float EmotionGain { get; set; } = 1f;
    public DubPlan Plan => _plan;
    public DubPlan SourcePlan => _srcPlan;
    public ReadOnlySpan<float> SourceAudio => _src.AsSpan(0, _srcLen);
    public ReadOnlySpan<float> DubAudio => _dub.AsSpan(0, _dubLen);
    public ReadOnlySpan<float> SourceF0 => _srcF0.AsSpan(0, _srcFrames);
    public ReadOnlySpan<float> DubF0 => _dubF0.AsSpan(0, _dubFrames);
    public float PlaybackTime => _clock / (float)Fs;
    public bool Finished => _clock >= _dubLen;
    public double LastBlockMs { get; private set; }
    public double LastPrepareMs { get; private set; }
    public float BlockRms { get; private set; }

    public static bool IsVowel(Ph p) => p is >= Ph.A and <= Ph.U;
    public static string PhoneName(Ph p) => p.ToString();

    // ══════════════════════ G2P ══════════════════════

    /// <summary>로마자 근사 발음 → 음소/음절 계획(기본 리듬 길이 · 무할당 · 길이 맞춤 전).</summary>
    public static void G2P(ReadOnlySpan<char> roman, in LanguageProfile lang, DubPlan plan)
    {
        plan.Clear(); plan.Language = lang.Id;
        var t = 0.08f;
        var baseSyl = 1f / lang.SyllablesPerSec;
        var i = 0; var sylInPhrase = 0;
        while (i < roman.Length && plan.Syllables < DubPlan.MaxSyllables)
        {
            var c = roman[i];
            if (c == ' ') { i++; continue; }
            if (c is ',' or '.' or '!' or '?') { t += 0.2f; sylInPhrase = 0; i++; continue; }
            var j = i;
            while (j < roman.Length && roman[j] is not (' ' or ',' or '.' or '!' or '?')) j++;
            var syl = roman[i..j]; i = j;
            byte tone = 0;
            if (syl.Length > 1 && char.IsAsciiDigit(syl[^1])) { tone = (byte)(syl[^1] - '0'); syl = syl[..^1]; }
            var s = plan.Syllables++;
            var stressed = lang.Rhythm == Rhythm.Stress && (sylInPhrase & 1) == 0;
            sylInPhrase++;
            var dur = baseSyl * (lang.Rhythm == Rhythm.Stress ? (stressed ? 1.35f : 0.75f) : 1f);
            var p0 = plan.Count;
            ParseSyllable(syl, plan);
            var n = plan.Count - p0;
            if (n == 0) { plan.Syllables--; continue; }
            // 음소 길이: 모음 핵 55% · 자음 나머지 균등
            int vowels = 0, cons = 0;
            for (var k = p0; k < plan.Count; k++) if (IsVowel(plan.Phone[k])) vowels++; else cons++;
            float vShare = vowels > 0 ? (cons > 0 ? 0.55f : 1f) : 0f, cShare = 1f - vShare;
            var tt = t;
            for (var k = p0; k < plan.Count; k++)
            {
                var d = IsVowel(plan.Phone[k]) ? dur * vShare / vowels : dur * cShare / Math.Max(cons, 1);
                plan.Start[k] = tt; plan.Dur[k] = d; plan.Syllable[k] = (short)s; tt += d;
            }
            plan.SylStart[s] = t; plan.SylDur[s] = dur; plan.Tone[s] = tone; plan.Stressed[s] = stressed;
            t = tt;
        }
        plan.Duration = t + 0.12f;
        plan.NaturalDuration = plan.Duration;
    }

    private static void Push(DubPlan p, Ph ph) { if (p.Count < DubPlan.MaxPhones) p.Phone[p.Count++] = ph; }

    private static void ParseSyllable(ReadOnlySpan<char> s, DubPlan p)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var c = char.ToLowerInvariant(s[i]); var nx = i + 1 < s.Length ? char.ToLowerInvariant(s[i + 1]) : '\0';
            switch (c)
            {
                case 'a': Push(p, Ph.A); break;
                case 'e': Push(p, nx == 'o' || nx == 'u' ? Ph.O : Ph.E); if (nx is 'o' or 'u') i++; break;
                case 'i': Push(p, Ph.I); break;
                case 'o': Push(p, Ph.O); break;
                case 'u': Push(p, Ph.U); break;
                case 'y': Push(p, i + 1 < s.Length && nx is 'a' or 'e' or 'o' or 'u' ? Ph.Y : Ph.I); break;
                case 'w': Push(p, Ph.W); break;
                case 'p': Push(p, Ph.P); if (nx == 'p') i++; break;
                case 'b': Push(p, Ph.B); break;
                case 't': if (nx == 'h') { Push(p, Ph.S); i++; } else if (nx == 's') { Push(p, Ph.T); Push(p, Ph.S); i++; } else { Push(p, Ph.T); if (nx == 't') i++; } break;
                case 'd': Push(p, Ph.D); if (nx == 'h') i++; break;
                case 'k': if (nx == 'h') { Push(p, Ph.H); i++; } else Push(p, Ph.K); break;
                case 'q': case 'c' when nx != 'h': Push(p, Ph.K); break;
                case 'c': Push(p, Ph.T); Push(p, Ph.Sh); i++; break;
                case 'g': if (nx == 'h') { Push(p, Ph.G); i++; } else Push(p, Ph.G); break;
                case 'j': Push(p, Ph.D); Push(p, Ph.Sh); if (nx == 'j') i++; break;
                case 'f': Push(p, Ph.F); break;
                case 'v': Push(p, Ph.V); break;
                case 's': if (nx == 'h') { Push(p, Ph.Sh); i++; } else if (nx == 'c' && i + 2 < s.Length && char.ToLowerInvariant(s[i + 2]) == 'h') { Push(p, Ph.Sh); i += 2; } else { Push(p, Ph.S); if (nx == 's') i++; } break;
                case 'z': if (nx == 'h') { Push(p, Ph.D); Push(p, Ph.Sh); i++; } else Push(p, Ph.Z); break;
                case 'x': Push(p, Ph.Sh); break;
                case 'h': Push(p, Ph.H); break;
                case 'm': Push(p, Ph.M); break;
                case 'n': if (nx == 'g') { Push(p, Ph.Ng); i++; } else Push(p, Ph.N); break;
                case 'l': Push(p, Ph.L); break;
                case 'r': Push(p, Ph.R); break;
                default: break;
            }
        }
    }

    // ══════════════════════ 운율 ══════════════════════

    /// <summary>감정 → 원본 억양 템플릿(음절 반음 · 변화) · 에너지.</summary>
    private static void EmotionContour(LineEmotion e, bool question, DubPlan p, float gain)
    {
        var n = DubPlan.ContourPoints;
        for (var k = 0; k < n; k++)
        {
            var u = k / (float)(n - 1);
            float st = -2.5f * u, en = 1f;
            switch (e)
            {
                case LineEmotion.Joy: st = 3.5f * MathF.Sin(MathF.PI * MathF.Min(u * 1.4f, 1f)) - 1.5f * u + 1f; en = 1.15f; break;
                case LineEmotion.SadQuestion: st = -1.2f * MathF.Sin(MathF.PI * u) - 0.5f; en = 0.85f; break;
                case LineEmotion.Determined: st = 2.5f - 5f * u + 1.2f * MathF.Sin(6f * u); en = 1.2f; break;
                case LineEmotion.Humor: st = 2.5f * MathF.Sin(MathF.Tau * 1.5f * u) - u; en = 1.05f; break;
                case LineEmotion.Gratitude: st = 1.5f * MathF.Sin(MathF.PI * u) - 1.5f * u; en = 0.95f; break;
                default: st = -1.5f * u + 0.6f * MathF.Sin(MathF.PI * 2f * u); en = 0.9f; break;
            }
            if (question && u > 0.82f) st += 6f * (u - 0.82f) / 0.18f;
            p.Pitch[k] = st * gain; p.Energy[k] = en * (0.7f + 0.3f * MathF.Sin(MathF.PI * MathF.Min(u * 1.2f, 1f)));
        }
    }

    /// <summary>성조(반음 · 음절 내 위치 v 0..1) — 표준 중국어 1~5 · 베트남어 1~6 근사.</summary>
    public static float ToneSt(DubLanguage l, byte tone, float v) => l switch
    {
        DubLanguage.Zh => tone switch { 1 => 3f, 2 => -1f + 4f * v, 3 => -2f - 3f * MathF.Sin(MathF.PI * v) + 2f * v, 4 => 4f - 7f * v, _ => 0f },
        DubLanguage.Vi => tone switch { 2 => -1f - 3f * v, 3 => -1f + 5f * v, 4 => -1f - 2f * MathF.Sin(MathF.PI * v) + 3f * v, 5 => -2f - 2f * v, 6 => -3f * v, _ => 1f },
        _ => 0f,
    };

    // ══════════════════════ 보코더 ══════════════════════

    private float Noise()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
        return (_rng * (1f / uint.MaxValue)) * 2f - 1f;
    }

    private static float Res(float x, float f, float bw, ref float y1, ref float y2)
    {
        var r = MathF.Exp(-MathF.PI * bw / Fs);
        var b = 2f * r * MathF.Cos(MathF.Tau * f / Fs); var c = -r * r; var a = 1f - b - c;
        var y = a * x + b * y1 + c * y2; y2 = y1; y1 = y;
        return y;
    }

    /// <summary>2극 공진기 피크 이득(|H(e^jω)| @ f) — 마찰 대역 정규화.</summary>
    private static float PeakGain(float f, float bw)
    {
        var r = MathF.Exp(-MathF.PI * bw / Fs);
        var b = 2f * r * MathF.Cos(MathF.Tau * f / Fs); var c = -r * r; var a = 1f - b - c;
        var w = MathF.Tau * f / Fs;
        float re = 1f - b * MathF.Cos(w) - c * MathF.Cos(2f * w), im = b * MathF.Sin(w) + c * MathF.Sin(2f * w);
        return MathF.Max(MathF.Abs(a) / MathF.Sqrt(re * re + im * im), 1e-4f);
    }

    private void ResetVoice() { _ph = 0f; _prevG = 0f; _y1a = _y2a = _y1b = _y2b = _y1c = _y2c = _y1d = _y2d = _ny1 = _ny2 = 0f; _jit = 0f; _shim = 0f; _f1 = 500f; _f2 = 1500f; _f3 = 2500f; }

    /// <summary>계획 구간 [s0, s0+n) 렌더(무할당 · 블록 단위 SIMD 혼합).</summary>
    private void RenderSpan(DubPlan plan, in VoiceTimbre v, long s0, Span<float> dst)
    {
        var n = dst.Length;
        for (var off = 0; off < n; off += Block)
        {
            var m = Math.Min(Block, n - off);
            for (var i = 0; i < m; i++)
            {
                var t = (s0 + off + i) / (float)Fs;
                var k = plan.PhoneAt(t);
                if (k < 0) { _voice[i] = 0f; _noise[i] = 0f; _gv[i] = 0f; _gn[i] = 0f; continue; }
                var ph = plan.Phone[k]; ref readonly var sp = ref Specs[(int)ph];
                var u = (t - plan.Start[k]) / plan.Dur[k];
                // 포먼트 전이(음소 앞 30% · 지수 접근)
                var tf = sp.F1 * v.Vtf; var tf2 = sp.F2 * v.Vtf; var tf3 = sp.F3 * v.Vtf;
                var kf = u < 0.3f ? 0.004f : 0.0015f;
                _f1 += (tf - _f1) * kf; _f2 += (tf2 - _f2) * kf; _f3 += (tf3 - _f3) * kf;
                // F0 = 복제 F0 · 2^(윤곽 + 성조) · 지터
                var syl = plan.Syllable[k];
                var tone = plan.Tone[syl] != 0 ? ToneSt(plan.Language, plan.Tone[syl], Math.Clamp((t - plan.SylStart[syl]) / plan.SylDur[syl], 0f, 1f)) : 0f;
                var st = plan.ContourAt(plan.Pitch, t) * v.RangeSt / 2.6f + tone;
                var f0 = v.F0 * MathF.Pow(2f, st / 12f) * (1f + _jit);
                // 성문 펄스(sin² 개방 0.6) → 방사 미분
                _ph += f0 / Fs;
                if (_ph >= 1f) { _ph -= 1f; _jit = v.Jitter * Noise(); _shim = v.Shimmer * Noise(); }
                var g = _ph < 0.6f ? MathF.Sin(MathF.PI * _ph / 0.6f) : 0f; g *= g;
                var dg = (g - _prevG) * 12f; _prevG = g;
                var open = _ph < 0.6f ? 1f : 0.3f;
                // 파열: 폐쇄 구간 무음/보이스바 → 버스트 15 ms
                float voicedAmp = sp.Amp, noiseAmp = sp.Noise;
                if (sp.Closure > 0f)
                {
                    if (u < sp.Closure) { noiseAmp = 0f; voicedAmp = sp.Voiced ? 0.08f : 0f; }
                    else { var bt = (u - sp.Closure) * plan.Dur[k]; noiseAmp *= MathF.Exp(-bt / 0.015f) * 2f; voicedAmp = sp.Voiced ? 0.3f : 0f; }
                }
                if (ph == Ph.H) { _f1 += (Specs[(int)Ph.A].F1 * v.Vtf - _f1) * 0.002f; }
                var en = plan.ContourAt(plan.Energy, t);
                var edge = Math.Clamp(MathF.Min(u, 1f - u) * plan.Dur[k] / 0.012f + 0.35f, 0f, 1f);
                _voice[i] = dg + v.Breathiness * open * Noise() * 0.6f;
                _noise[i] = Noise();
                _gv[i] = voicedAmp * en * edge * (1f + _shim);
                _gn[i] = noiseAmp * en * edge;
            }
            // 공진 전 혼합은 분리: 성대음(기식 포함)만 포먼트 · 마찰 노이즈는 대역 공진
            var vs = _voice.AsSpan(0, m);
            for (var i = 0; i < m; i++)
            {
                var x = vs[i];
                x = Res(x, _f1, 80f, ref _y1a, ref _y2a);
                x = Res(x, _f2, 110f, ref _y1b, ref _y2b);
                x = Res(x, _f3, 160f, ref _y1c, ref _y2c);
                x = Res(x, 3500f * MathF.Max(0.9f, _f3 / 2500f), 250f, ref _y1d, ref _y2d);
                vs[i] = x;
                var t = (s0 + off + i) / (float)Fs; var k = plan.PhoneAt(t);
                var hz = k >= 0 ? Specs[(int)plan.Phone[k]].NoiseHz : 1000f;
                var nf = MathF.Max(hz, 500f) * MathF.Min(1.1f, v.Vtf);
                if (nf != _nHz) { _nHz = nf; _nNorm = 1f / PeakGain(nf, nf * 0.35f); }
                _noise[i] = Res(_noise[i], nf, nf * 0.35f, ref _ny1, ref _ny2) * _nNorm * 0.3f;
                vs[i] *= OutputGain;
            }
            DubSimd.Mix(dst.Slice(off, m), vs, _gv.AsSpan(0, m), _noise.AsSpan(0, m), _gn.AsSpan(0, m));
        }
    }

    // ══════════════════════ 분석(제어 평면 · 선할당 스크래치) ══════════════════════

    private int Decimate(ReadOnlySpan<float> x)
    {
        var n = x.Length / 2;
        for (var i = 0; i < n; i++) _dec[i] = 0.5f * (x[2 * i] + x[2 * i + 1]);
        return n;
    }

    /// <summary>자기상관 피치(12 kHz · 10 ms 홉 · 30 ms 창 · 60~400 Hz · 유성 임계 0.45) → frames · F0(0 = 무성) · 에너지.</summary>
    private int TrackPitch(ReadOnlySpan<float> x, Span<float> f0, Span<float> energy, out float meanHnr)
    {
        var n = Decimate(x);
        const int hop = Frame / 2, win = Frame * 3 / 2 / 2 * 2; // 120 · 360 @12 kHz
        const int lagMin = 12000 / 400, lagMax = 12000 / 60;
        var frames = Math.Min((n - win - lagMax) / hop, f0.Length);
        double hnrSum = 0; var hnrN = 0;
        for (var fI = 0; fI < Math.Max(frames, 0); fI++)
        {
            var seg = _dec.AsSpan(fI * hop, win + lagMax);
            var a = seg[..win];
            var e0 = DubSimd.Dot(a, a);
            energy[fI] = MathF.Sqrt(e0 / win);
            if (e0 < 1e-5f) { f0[fI] = 0f; continue; }
            float best = 0f; var bl = 0;
            for (var lag = lagMin; lag <= lagMax; lag++)
            {
                var b = seg.Slice(lag, win);
                var r = DubSimd.Dot(a, b) / MathF.Sqrt(e0 * DubSimd.Dot(b, b) + 1e-12f);
                if (r > best) { best = r; bl = lag; }
            }
            if (best < 0.45f) { f0[fI] = 0f; continue; }
            // 옥타브 오류 억제: 절반 지연에서도 강하면 채택
            var half = bl / 2;
            if (half >= lagMin)
            {
                var b = seg.Slice(half, win);
                var r2 = DubSimd.Dot(a, b) / MathF.Sqrt(e0 * DubSimd.Dot(b, b) + 1e-12f);
                if (r2 > 0.9f * best) bl = half;
            }
            f0[fI] = 12000f / bl;
            var rr = MathF.Min(best, 0.999f);
            hnrSum += 10.0 * Math.Log10(rr / (1f - rr)); hnrN++;
        }
        meanHnr = hnrN > 0 ? (float)(hnrSum / hnrN) : 0f;
        return Math.Max(frames, 0);
    }

    /// <summary>LPC(자기상관 · Levinson-Durbin) 포락선 첫 두 피크(F1, F2) @ 12 kHz 표본 중심 c.</summary>
    private (float F1, float F2) Formants(int center)
    {
        const int w = Frame * 3 / 2 / 2 * 2 / 2; // 180 @12 kHz = 15 ms
        var start = Math.Clamp(center - w / 2, 1, _dec.Length - w - 1);
        for (var i = 0; i < w; i++)
        {
            var x = _dec[start + i] - 0.9f * _dec[start + i - 1];
            _win[i] = x * (0.54f - 0.46f * MathF.Cos(MathF.Tau * i / (w - 1)));
        }
        var ws = _win.AsSpan(0, w);
        for (var k = 0; k <= LpcOrder; k++) _r[k] = DubSimd.Dot(ws[..(w - k)], ws[k..w]);
        if (_r[0] <= 1e-9f) return (0f, 0f);
        _r[0] *= 1.0001f;
        Array.Clear(_lpc); _lpc[0] = 1f; var err = _r[0];
        for (var i = 1; i <= LpcOrder; i++)
        {
            var acc = _r[i];
            for (var j = 1; j < i; j++) acc += _lpc[j] * _r[i - j];
            var k = -acc / err;
            Array.Copy(_lpc, _tmpLpc, LpcOrder + 1);
            for (var j = 1; j < i; j++) _lpc[j] = _tmpLpc[j] + k * _tmpLpc[i - j];
            _lpc[i] = k; err *= 1f - k * k;
            if (err <= 0f) break;
        }
        float f1 = 0f, f2 = 0f, pPrev = 0f, pPrev2 = 0f;
        for (var f = 150f; f <= 3600f; f += 20f)
        {
            float re = 0f, im = 0f;
            for (var j = 0; j <= LpcOrder; j++) { var ang = -MathF.Tau * f * j / 12000f; re += _lpc[j] * MathF.Cos(ang); im += _lpc[j] * MathF.Sin(ang); }
            var pw = 1f / (re * re + im * im + 1e-12f);
            if (pPrev > pPrev2 && pPrev > pw && f > 190f)
            {
                if (f1 == 0f) f1 = f - 20f; else if (f2 == 0f && f - 20f - f1 > 250f) { f2 = f - 20f; break; }
            }
            pPrev2 = pPrev; pPrev = pw;
        }
        return (f1, f2);
    }

    /// <summary>음색 추정(정합 계획 모음 중심 · 피치 통계 · HNR).</summary>
    private VoiceTimbre Estimate(ReadOnlySpan<float> audio, DubPlan aligned, Span<float> f0, Span<float> energy, out int frames)
    {
        frames = TrackPitch(audio, f0, energy, out var hnr);
        // F0 중앙값(유성 표본 ≤ 256 균등 추출 · 스택 정렬)
        Span<float> vals = stackalloc float[256];
        var nv = 0;
        for (var i = 0; i < frames && nv < vals.Length; i += Math.Max(1, frames / 256)) if (f0[i] > 0f) vals[nv++] = f0[i];
        if (nv == 0) return VoiceTimbre.Actor;
        var vs = vals[..nv]; vs.Sort();
        var med = vs[nv / 2];
        double s = 0; for (var i = 0; i < nv; i++) { var d = 12.0 * Math.Log2(vs[i] / med); s += d * d; }
        var range = (float)Math.Sqrt(s / nv);
        // 성도 배율: 모음 중심 F1/F2 ÷ 표준값 중앙값
        Span<float> ratios = stackalloc float[128]; var nr = 0;
        for (var k = 0; k < aligned.Count && nr < ratios.Length - 1; k++)
        {
            var ph = aligned.Phone[k];
            if (!IsVowel(ph) || aligned.Dur[k] < 0.05f) continue;
            var c = (int)((aligned.Start[k] + aligned.Dur[k] * 0.55f) * 12000f);
            var (m1, m2) = Formants(c);
            ref readonly var sp = ref Specs[(int)ph];
            if (m1 > 0f && MathF.Abs(m1 / sp.F1 - 1.08f) < 0.6f) ratios[nr++] = m1 / sp.F1;
            if (m2 > 0f && MathF.Abs(m2 / sp.F2 - 1.08f) < 0.6f) ratios[nr++] = m2 / sp.F2;
        }
        var vtf = 1f;
        if (nr > 0) { var rs = ratios[..nr]; rs.Sort(); vtf = rs[nr / 2]; }
        var breath = Math.Clamp(0.55f - 0.035f * hnr, 0.02f, 0.6f);
        return new VoiceTimbre { F0 = med, RangeSt = range, Vtf = vtf, Breathiness = breath, Jitter = 0.008f, Shimmer = 0.05f };
    }

    // ══════════════════════ 제어 평면 API ══════════════════════

    /// <summary>원본 대사 합성(원본 배우 음색 · 감정 억양) → 분석(복제 음색 + z-억양/에너지 윤곽).</summary>
    public void PrepareSource(string roman, LineEmotion emotion, bool question)
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            G2P(roman, GlobalLanguageCatalog.Profile(DubLanguage.Ko), _srcPlan);
            _srcPlan.Question = question;
            EmotionContour(emotion, question, _srcPlan, EmotionGain);
            _srcLen = Math.Min((int)(_srcPlan.Duration * Fs), MaxSamples);
            ResetVoice();
            RenderSpan(_srcPlan, SourceSpeaker, 0, _src.AsSpan(0, _srcLen));
            var est = Estimate(_src.AsSpan(0, _srcLen), _srcPlan, _srcF0, _srcEnergy, out _srcFrames);
            SourceMeasured = est;
            Clone = CloneTimbre ? est : VoiceTimbre.Actress;
            LastPrepareMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
    }

    /// <summary>원본 피치/에너지 → 정규 시간 64점 z-반음 · 상대 에너지.</summary>
    private void ExtractContour(DubPlan dst)
    {
        var n = DubPlan.ContourPoints; var med = Clone.F0; var rng = MathF.Max(Clone.RangeSt, 0.5f);
        var frameSec = (Frame / 2) / 12000f;
        var dur = _srcPlan.Duration;
        float lastZ = 0f; double eSum = 0; var eN = 0;
        for (var i = 0; i < _srcFrames; i++) if (_srcEnergy[i] > 1e-4f) { eSum += _srcEnergy[i]; eN++; }
        var eMean = eN > 0 ? (float)(eSum / eN) : 1f;
        for (var k = 0; k < n; k++)
        {
            var t = k / (float)(n - 1) * dur;
            var fi = Math.Clamp((int)(t / frameSec), 0, Math.Max(_srcFrames - 1, 0));
            // 인근 유성 표본(±5 프레임) 탐색
            var z = lastZ;
            for (var d = 0; d <= 5; d++)
            {
                if (fi - d >= 0 && fi - d < _srcFrames && _srcF0[fi - d] > 0f) { z = 12f * MathF.Log2(_srcF0[fi - d] / med) / rng; break; }
                if (fi + d < _srcFrames && _srcF0[fi + d] > 0f) { z = 12f * MathF.Log2(_srcF0[fi + d] / med) / rng; break; }
            }
            lastZ = z;
            dst.Pitch[k] = z * 2.6f;
            dst.Energy[k] = _srcFrames > 0 ? Math.Clamp(_srcEnergy[fi] / eMean, 0.3f, 1.6f) : 1f;
        }
        // 3점 평활
        for (var k = 1; k < n - 1; k++) dst.Pitch[k] = 0.25f * dst.Pitch[k - 1] + 0.5f * dst.Pitch[k] + 0.25f * dst.Pitch[k + 1];
    }

    /// <summary>더빙 준비: G2P → 등시성 길이 맞춤 → 운율 전이 → 오프라인 렌더(지표용) → 재분석 · 재생 위치 0.</summary>
    public void PrepareDub(string roman, DubLanguage lang, bool question)
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var prof = GlobalLanguageCatalog.Profile(lang);
            G2P(roman, prof, _plan);
            _plan.Question = question;
            // 등시성: 원본 길이에 맞춤(속도 0.75~1.35×)
            var target = _srcPlan.Duration > 0f ? _srcPlan.Duration : _plan.Duration;
            var scale = Math.Clamp(target / _plan.Duration, 0.75f, 1.35f);
            _plan.RateScale = 1f / scale;
            for (var k = 0; k < _plan.Count; k++) { _plan.Start[k] *= scale; _plan.Dur[k] *= scale; }
            for (var s = 0; s < _plan.Syllables; s++) { _plan.SylStart[s] *= scale; _plan.SylDur[s] *= scale; }
            _plan.Duration *= scale;
            IsochronyError = target > 0f ? MathF.Abs(_plan.Duration - target) / target : 0f;
            // 운율
            if (TransferProsody && _srcFrames > 0) ExtractContour(_plan);
            else EmotionContour(LineEmotion.Calm, question, _plan, 1f);
            if (prof.Tonal) for (var k = 0; k < DubPlan.ContourPoints; k++) _plan.Pitch[k] *= 0.5f;
            if (question)
                for (var k = 0; k < DubPlan.ContourPoints; k++)
                {
                    var u = k / (float)(DubPlan.ContourPoints - 1);
                    if (u > 0.85f) _plan.Pitch[k] = MathF.Max(_plan.Pitch[k], prof.QuestionRise * 2f * (u - 0.85f) / 0.15f);
                }
            _dubLen = Math.Min((int)(_plan.Duration * Fs), MaxSamples);
            ResetVoice();
            RenderSpan(_plan, Clone, 0, _dub.AsSpan(0, _dubLen));
            // 재분석 → 음색 유사도 · 억양 상관
            DubMeasured = Estimate(_dub.AsSpan(0, _dubLen), _plan, _dubF0, _noiseScratch, out _dubFrames);
            TimbreSimilarity = Similarity(SourceMeasured, DubMeasured);
            ProsodyCorrelation = Correlate(_srcF0.AsSpan(0, _srcFrames), _srcPlan.Duration, _dubF0.AsSpan(0, _dubFrames), _plan.Duration);
            _clock = 0; ResetVoice();
            LastPrepareMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
    }

    private readonly float[] _noiseScratch = new float[MaxFrames];

    /// <summary>음색 유사도 0..1: F0(반음 차) · 성도 배율 · 기식 · 억양 폭 가중.</summary>
    public static float Similarity(in VoiceTimbre a, in VoiceTimbre b)
    {
        var df = MathF.Abs(12f * MathF.Log2(MathF.Max(b.F0, 1f) / MathF.Max(a.F0, 1f)));
        var dv = MathF.Abs(b.Vtf / MathF.Max(a.Vtf, 0.1f) - 1f);
        var db = MathF.Abs(b.Breathiness - a.Breathiness);
        return Math.Clamp(1f - 0.06f * df - 1.2f * dv - 0.6f * db, 0f, 1f);
    }

    /// <summary>정규 시간 축 억양 상관(Pearson · 양쪽 유성 64점).</summary>
    public static float Correlate(ReadOnlySpan<float> a, float durA, ReadOnlySpan<float> b, float durB)
    {
        if (a.Length == 0 || b.Length == 0) return 0f;
        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0; var n = 0;
        for (var k = 0; k < 64; k++)
        {
            var u = (k + 0.5f) / 64f;
            var ia = Math.Min((int)(u * a.Length), a.Length - 1); var ib = Math.Min((int)(u * b.Length), b.Length - 1);
            if (a[ia] <= 0f || b[ib] <= 0f) continue;
            double x = Math.Log2(a[ia]), y = Math.Log2(b[ib]);
            sx += x; sy += y; sxx += x * x; syy += y * y; sxy += x * y; n++;
        }
        if (n < 4) return 0f;
        var cov = sxy - sx * sy / n; var vx = sxx - sx * sx / n; var vy = syy - sy * sy / n;
        return vx > 1e-9 && vy > 1e-9 ? (float)(cov / Math.Sqrt(vx * vy)) : 0f;
    }

    /// <summary>스트리밍 1블록(무할당 · 오프라인 렌더 결과 재생 = 결정적 · 재생 시계 진행).</summary>
    public int RenderBlock(Span<float> dst)
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var n = Math.Min(dst.Length, Block);
            var avail = (int)Math.Max(0, Math.Min(n, _dubLen - _clock));
            if (avail > 0) _dub.AsSpan((int)_clock, avail).CopyTo(dst);
            if (avail < n) dst[avail..n].Clear();
            _clock += n;
            BlockRms = MathF.Sqrt(DubSimd.Dot(dst[..n], dst[..n]) / n);
            LastBlockMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            return avail;
        }
    }

    /// <summary>실시간 보코더 렌더(계획 → 블록 · 무할당 · 오프라인 버퍼 미사용 경로).</summary>
    public void SynthesizeBlock(long startSample, Span<float> dst)
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (startSample == 0) ResetVoice();
            RenderSpan(_plan, Clone, startSample, dst);
            LastBlockMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
    }

    public void Rewind() { lock (_gate) _clock = 0; }
}
