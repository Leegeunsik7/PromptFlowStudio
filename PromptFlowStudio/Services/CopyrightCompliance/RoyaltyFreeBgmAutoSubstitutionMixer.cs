using PromptFlowStudio.Services.SmartPublishing;
using PromptFlowStudio.Services.Sound;

namespace PromptFlowStudio.Services.CopyrightCompliance;

public enum BgmStyle : byte { LoFi, Synthwave, HipHop }

/// <summary>
/// [PromptFlow Studio v17.91.0] 🎼 로열티 프리 BGM 자동 치환 믹서(<see cref="Substitute"/> · <see cref="Normalize"/> · <see cref="MeasureLufs"/> 무할당 · 제자리 처리).
///   ▸ 소거: 분석기 정렬 오프셋 → 상호상관 샘플 미세 정렬 → 레퍼런스 마스터 LS 이득 역위상 감산(보컬 무손상).
///   ▸ 제거: 침해 구간 ±120 ms 크로스페이드 영역에서 소거 잔여를 보컬 대역(HP 200 Hz · LP 4 kHz)과 잔여로 분리 →
///     보컬 대역엔 분석기 랜드마크 상위 주파수 노치(밴드스톱 Q 10 · 최대 48 = 지문 랜드마크 자체를 제거) · 보컬 엔벨로프 사이드체인(무음 시 −30 dB) / 잔여(베이스 · 고역)는 −30 dB 고정 감쇄.
///   ▸ 치환: v17.76 <see cref="ProceduralViralSoundFxSynthesizer"/> 뱅크(임팩트 = 킥 · 팝 = 스네어/림 · 휘쉬 = 바 전환 스윕) + 절차적 패드/베이스로
///     생성자에서 만든 2 마디 루프(Lo-Fi 82 · Synthwave 100 · Hip-Hop 90 BPM)를 빈자리에 등전력 크로스페이드로 타일링.
///   ▸ 마스터: ITU-R BS.1770 K-가중(쉘프 + RLB HP) 400 ms 블록/75 % 겹침 · 절대(−70) · 상대(−10 LU) 게이트 적분 라우드니스 → −14 LUFS 이득 ·
///     즉시 어택 피크 리미터(−1 dBFS 천장 · 80 ms 릴리스) 최대 3 회 보정 → 0 dBFS 초과 0 건.
/// </summary>
public sealed class RoyaltyFreeBgmAutoSubstitutionMixer
{
    public const int Fs = 48000, Styles = 3, MaxNotches = 48, CrossfadeMs = 120, MaxSubBlocks = 6000;
    public const double NotchQ = 10;
    /// <summary>보컬 사이드체인 키 임계(대역 엔벨로프 · ≈ −42 dBFS).</summary>
    public const float VoiceKey = 0.008f;
    public const float TargetLufs = -14f, DuckDb = -30f, Ceiling = 0.891f;
    public static readonly int[] Bpm = { 82, 100, 90 };
    public static readonly string[] StyleNames = { "☕ Lo-Fi", "🌆 Synthwave", "🎤 Hip-Hop" };

    private static readonly float Duck = MathF.Pow(10f, DuckDb / 20f);
    private readonly float[][] _loops = new float[Styles][];
    private readonly double[] _sub = new double[MaxSubBlocks];
    private readonly Biquad[] _notch = new Biquad[MaxNotches];
    private readonly float[] _notchHz = new float[MaxNotches];
    private readonly float[] _ref = new float[AcousticFingerprintContentIdAnalyzerEngine.RefSeconds * Fs];

    public RoyaltyFreeBgmAutoSubstitutionMixer(ProceduralViralSoundFxSynthesizer? sfx = null)
    {
        var bank = sfx ?? new ProceduralViralSoundFxSynthesizer();
        for (var s = 0; s < Styles; s++) _loops[s] = RenderLoop((BgmStyle)s, bank);
    }

    public BgmStyle Style { get; set; } = BgmStyle.LoFi;
    public float BgmGain { get; set; } = 0.55f;
    public ReadOnlySpan<float> Loop(BgmStyle s) => _loops[(int)s];
    public int NotchCount { get; private set; }
    public float NotchHz(int i) => _notchHz[i];
    public double PreLufs { get; private set; }
    public double PostLufs { get; private set; }
    public float PeakDbfs { get; private set; }
    public int ClippedSamples { get; private set; }
    public long LimitedSamples { get; private set; }
    public long SubstitutedSamples { get; private set; }
    public double VoiceOpenRatio { get; private set; }
    public long Runs { get; private set; }
    /// <summary>레퍼런스 마스터 소거 — 미세 정렬 지연(샘플) · LS 이득 · 소거량(dB).</summary>
    public int LastAlignLag { get; private set; }
    public float LastCancelGain { get; private set; }
    public float LastCancelDb { get; private set; }
    public bool ReferenceCancel { get; set; } = true;

    // ══════════════════════ 치환(무할당 · 제자리) ══════════════════════

    /// <summary>침해 구간 원음 제거 + 로열티 프리 루프 합성. notchHz = 분석기 랜드마크 주파수(빈 스팬이면 노치 생략).</summary>
    public void Substitute(Span<float> audio, ReadOnlySpan<InfringementSegment> segments, ReadOnlySpan<float> notchHz)
    {
        NotchCount = Math.Min(MaxNotches, notchHz.Length);
        for (var k = 0; k < NotchCount; k++)
        {
            _notchHz[k] = notchHz[k];
            _notch[k] = Notch(notchHz[k], NotchQ);
        }
        var loop = _loops[(int)Style];
        var fade = CrossfadeMs * Fs / 1000;
        long open = 0, total = 0;
        SubstitutedSamples = 0;
        foreach (var seg in segments)
        {
            var s0 = (int)Math.Clamp((long)seg.StartMs * Fs / 1000, 0, audio.Length);
            var s1 = (int)Math.Clamp((long)seg.EndMs * Fs / 1000, s0, audio.Length);
            var a = Math.Max(0, s0 - fade);
            var b = Math.Min(audio.Length, s1 + fade);
            var hp = Biquad.HighPass(200, 0.707, Fs);
            var lp = Biquad.LowPass(4000, 0.707, Fs);
            for (var k = 0; k < NotchCount; k++) _notch[k].Reset();
            if (ReferenceCancel) CancelReference(audio, a, b, seg.Track, seg.RefOffsetSamples);
            float env = 0, gate = Duck;
            const float Att = 1f / (0.005f * Fs), Rel = 1f / (0.12f * Fs), GateAtt = 1f / (0.01f * Fs), GateRel = 1f / (0.05f * Fs);
            for (var n = a; n < b; n++)
            {
                var x = audio[n];
                var band = lp.Process(hp.Process(x));
                var voc = band;
                for (var k = 0; k < NotchCount; k++) voc = _notch[k].Process(voc);
                var av = MathF.Abs(voc);
                env += (av > env ? Att : Rel) * (av - env);
                var want = env > VoiceKey ? 1f : Duck;
                gate += (want > gate ? GateAtt : GateRel) * (want - gate);
                if (want >= 1f) open++;
                total++;
                var processed = voc * gate + (x - band) * Duck;
                var w = n < s0 ? (n - a) / (float)Math.Max(1, s0 - a) : n >= s1 ? (b - n) / (float)Math.Max(1, b - s1) : 1f;
                var bed = loop[(n - a) % loop.Length] * BgmGain * MathF.Sin(w * MathF.PI * 0.5f);
                audio[n] = x * (1f - w) + processed * w + bed;
            }
            SubstitutedSamples += b - a;
        }
        VoiceOpenRatio = total == 0 ? 0 : open / (double)total;
        Runs++;
    }

    /// <summary>
    /// 레퍼런스 마스터 정렬 소거(무할당) — 지문 오프셋(프레임 해상도) 주변 ±2 홉을 4 샘플 → 1 샘플 2 단 상호상관으로 미세 정렬,
    /// 최소자승 이득 g = ⟨x, r⟩ / ⟨r, r⟩ 로 [a, b) 구간에서 g·r 을 감산. (모의 DB 원본 — 실환경은 권리자 레퍼런스 파일)
    /// </summary>
    private void CancelReference(Span<float> audio, int a, int b, int track, int offset)
    {
        AcousticFingerprintContentIdAnalyzerEngine.SynthesizeReference(track, _ref, 0);
        const int Win = 8192, Range = 2 * AcousticFingerprintContentIdAnalyzerEngine.Hop;
        var mid = Math.Clamp((a + b) / 2 - Win / 2, 0, Math.Max(0, audio.Length - Win));
        int bestLag = 0; var best = double.NegativeInfinity;
        for (var pass = 0; pass < 2; pass++)
        {
            int from = pass == 0 ? -Range : bestLag - 4, to = pass == 0 ? Range : bestLag + 4, step = pass == 0 ? 4 : 1;
            for (var lag = from; lag <= to; lag += step)
            {
                double acc = 0;
                var r0 = mid + offset + lag;
                for (var i = 0; i < Win; i += pass == 0 ? 2 : 1)
                {
                    var ri = r0 + i;
                    if ((uint)ri < (uint)_ref.Length && mid + i < audio.Length) acc += audio[mid + i] * _ref[ri];
                }
                if (acc > best) { best = acc; bestLag = lag; }
            }
        }
        var shift = offset + bestLag;
        double xr = 0, rr = 0, before = 0;
        for (var n = a; n < b; n++)
        {
            var ri = n + shift;
            if ((uint)ri >= (uint)_ref.Length) continue;
            xr += audio[n] * _ref[ri]; rr += _ref[ri] * _ref[ri]; before += audio[n] * audio[n];
        }
        var g = rr > 1e-9 ? (float)(xr / rr) : 0f;
        double after = 0;
        if (g > 0)
            for (var n = a; n < b; n++)
            {
                var ri = n + shift;
                if ((uint)ri >= (uint)_ref.Length) { after += audio[n] * audio[n]; continue; }
                audio[n] -= g * _ref[ri];
                after += audio[n] * audio[n];
            }
        else after = before;
        LastAlignLag = shift;
        LastCancelGain = g;
        LastCancelDb = before <= 0 ? 0 : (float)(10 * Math.Log10(Math.Max(after, 1e-12) / before));
    }

    /// <summary>−14 LUFS 정규화 + 피크 리미터 — 0 dBFS 초과 0 건 보장. 반환 = 최종 LUFS.</summary>
    public double Normalize(Span<float> audio)
    {
        PreLufs = MeasureLufs(audio);
        var lufs = PreLufs;
        for (var pass = 0; pass < 3 && !double.IsNegativeInfinity(lufs) && Math.Abs(lufs - TargetLufs) > 0.2; pass++)
        {
            var g = (float)Math.Pow(10, (TargetLufs - lufs) / 20);
            Limit(audio, g);
            lufs = MeasureLufs(audio);
        }
        PostLufs = lufs;
        float peak = 0; var clipped = 0;
        foreach (var v in audio) { var a = MathF.Abs(v); if (a > peak) peak = a; if (a > 1f) clipped++; }
        PeakDbfs = peak <= 0 ? -120f : 20f * MathF.Log10(peak);
        ClippedSamples = clipped;
        return lufs;
    }

    private void Limit(Span<float> audio, float gain)
    {
        var gl = 1f;
        const float Release = 1f / (0.08f * Fs);
        for (var n = 0; n < audio.Length; n++)
        {
            var y = audio[n] * gain;
            var ay = MathF.Abs(y);
            var target = ay > Ceiling ? Ceiling / ay : 1f;
            if (target < gl) { gl = target; LimitedSamples++; }
            else gl = MathF.Min(target, gl + (1f - gl) * Release * 8f);
            audio[n] = y * gl;
        }
    }

    /// <summary>BS.1770 적분 라우드니스(모노 · 무할당).</summary>
    public double MeasureLufs(ReadOnlySpan<float> audio)
    {
        var shelf = new Biquad { B0 = 1.53512485958697f, B1 = -2.69169618940638f, B2 = 1.19839281085285f, A1 = -1.69065929318241f, A2 = 0.73248077421585f };
        var rlb = new Biquad { B0 = 1f, B1 = -2f, B2 = 1f, A1 = -1.99004745483398f, A2 = 0.99007225036621f };
        const int SubLen = Fs / 10;
        var subs = Math.Min(MaxSubBlocks, audio.Length / SubLen);
        for (var s = 0; s < subs; s++)
        {
            double acc = 0;
            var o = s * SubLen;
            for (var i = 0; i < SubLen; i++) { var k = rlb.Process(shelf.Process(audio[o + i])); acc += (double)k * k; }
            _sub[s] = acc / SubLen;
        }
        var blocks = subs - 3;
        if (blocks <= 0) return double.NegativeInfinity;
        double sumAbs = 0; var nAbs = 0;
        for (var j = 0; j < blocks; j++)
        {
            var z = (_sub[j] + _sub[j + 1] + _sub[j + 2] + _sub[j + 3]) * 0.25;
            if (Loud(z) > -70) { sumAbs += z; nAbs++; }
        }
        if (nAbs == 0) return double.NegativeInfinity;
        var rel = Loud(sumAbs / nAbs) - 10;
        double sum = 0; var n = 0;
        for (var j = 0; j < blocks; j++)
        {
            var z = (_sub[j] + _sub[j + 1] + _sub[j + 2] + _sub[j + 3]) * 0.25;
            var l = Loud(z);
            if (l > -70 && l > rel) { sum += z; n++; }
        }
        return n == 0 ? double.NegativeInfinity : Loud(sum / n);
    }

    private static double Loud(double z) => z <= 0 ? double.NegativeInfinity : -0.691 + 10 * Math.Log10(z);

    private static Biquad Notch(double fc, double q)
    {
        var w = 2 * Math.PI * Math.Clamp(fc, 20, Fs * 0.45) / Fs;
        var c = Math.Cos(w); var a = Math.Sin(w) / (2 * q);
        var a0 = 1 + a;
        return new Biquad { B0 = (float)(1 / a0), B1 = (float)(-2 * c / a0), B2 = (float)(1 / a0), A1 = (float)(-2 * c / a0), A2 = (float)((1 - a) / a0) };
    }

    // ══════════════════════ 로열티 프리 루프(생성자 1 회) ══════════════════════

    private static float[] RenderLoop(BgmStyle style, ProceduralViralSoundFxSynthesizer sfx)
    {
        var bpm = Bpm[(int)style];
        var beat = Fs * 60 / bpm;
        var len = beat * 8;   // 2 마디
        var dst = new float[len];
        var kick = sfx.Bank(ViralSfxKind.Impact);
        var snare = sfx.Bank(ViralSfxKind.Pop);
        var sweep = sfx.Bank(ViralSfxKind.Whoosh);
        foreach (var k in KickSteps[(int)style]) Overlay(dst, kick, k * beat / 4, 0.55f);
        for (var b = 1; b < 8; b += 2) Overlay(dst, snare, b * beat, style == BgmStyle.LoFi ? 0.30f : 0.42f);
        Overlay(dst, sweep, len - sweep.Length, 0.18f);
        // 패드/베이스(코드 진행 2 마디: I → vi)
        var rng = 0x9E3779B9u;
        for (var n = 0; n < len; n++)
        {
            var second = n >= len / 2;
            var ch = second ? ChordB : ChordA;
            var t = n / (float)Fs;
            float pad = 0;
            for (var v = 0; v < 4; v++)
            {
                var f = ch[v] * (style == BgmStyle.HipHop ? 0.5f : 1f);
                pad += style == BgmStyle.Synthwave ? Saw(f * t) * 0.5f + Saw(f * 1.004f * t) * 0.5f : MathF.Sin(2f * MathF.PI * f * t);
            }
            var trem = style == BgmStyle.LoFi ? 0.75f + 0.25f * MathF.Sin(2f * MathF.PI * 0.5f * t) : 1f;
            var bassF = ch[0] * 0.25f;
            var inBeat = n % beat;
            var bass = MathF.Sin(2f * MathF.PI * bassF * t) * MathF.Exp(-inBeat / (float)beat * (style == BgmStyle.HipHop ? 1.5f : 3f));
            rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
            var hatEnv = (n % (beat / 2)) < 900 ? 1f - (n % (beat / 2)) / 900f : 0f;
            var hat = ((rng & 0xFFFF) / 32768f - 1f) * hatEnv * (style == BgmStyle.LoFi ? 0.05f : 0.09f);
            var crackle = style == BgmStyle.LoFi && (rng >> 24) == 0 ? 0.08f : 0f;
            dst[n] += pad * 0.07f * trem + bass * 0.32f + hat + crackle;
        }
        float peak = 0;
        foreach (var v in dst) peak = MathF.Max(peak, MathF.Abs(v));
        if (peak > 0) for (var n = 0; n < len; n++) dst[n] *= 0.8f / peak;
        return dst;
    }

    /// <summary>16 분음표 스텝(2 마디 = 32) 킥 배치 — Lo-Fi 하프타임 · Synthwave 4-on-the-floor · Hip-Hop 붐뱁.</summary>
    private static readonly int[][] KickSteps = { new[] { 0, 10, 16, 26 }, new[] { 0, 4, 8, 12, 16, 20, 24, 28 }, new[] { 0, 6, 10, 16, 22, 26 } };
    private static readonly float[] ChordA = { 261.63f, 329.63f, 392.00f, 493.88f }, ChordB = { 220.00f, 261.63f, 329.63f, 392.00f };

    private static float Saw(float phase) => 2f * (phase - MathF.Floor(phase + 0.5f));

    private static void Overlay(float[] dst, ReadOnlySpan<float> src, int at, float gain)
    {
        for (var i = 0; i < src.Length && at + i < dst.Length; i++) if (at + i >= 0) dst[at + i] += src[i] * gain;
    }
}
