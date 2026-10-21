using PromptFlowStudio.Services.LiveShowroom;
using PromptFlowStudio.Services.MotionTypography;
using PromptFlowStudio.Services.RhythmAction;

namespace PromptFlowStudio.Services.SmartPublishing;

public enum ViralSfxKind : byte { Whoosh, Pop, Impact, Chaching }

/// <summary>v17.76.0 효과음 큐 1 건(값 형식 · 출력 타임라인 ms).</summary>
public struct SfxCue
{
    public double TimeMs;
    public ViralSfxKind Kind;
    public float Gain;
}

/// <summary>
/// [PromptFlow Studio v17.76.0] 🔊 절차적 바이럴 사운드 FX 합성 &amp; 오디오 믹서 엔진(외부 에셋 0 · <see cref="Render"/> · <see cref="Mix"/> 무할당).
///   ▸ 합성(48 kHz · 모노 float · 결정적 xorshift):
///     Whoosh = 백색 잡음 → 상태변수 대역통과(중심 300 Hz → 4 kHz 지수 스윕 · Q 2.2) · 사인 반창 엔벨로프 0.42 s ·
///     Pop = 공진 팝(1.2 kHz → 420 Hz 피치 하강 사인 + 2 ms 클릭) · 지수 감쇠 τ 18 ms · 0.09 s ·
///     Impact = FM 서브 킥(반송 55 Hz · 피치 엔벨로프 ×3 → ×1 · 변조비 1.5 · 지수 감쇠) + 저역 잡음 버스트 · 0.6 s ·
///     Chaching = 금속 FM 벨 2 타(반송 2.1 kHz / 3.2 kHz · 변조비 3.5 · 지수 감쇠) + 고역 잡음 '차' 15 ms · 0.55 s.
///   ▸ 큐 수집: v17.52 자막 단어 등장(<see cref="TypoWord.StartMs"/> → Pop) · v17.54 컷 전환(<see cref="RhythmCutEvent.PresentSec"/> → Whoosh · 드롭 컷 = Impact) ·
///     v17.68 구매(<see cref="PurchaseEvent.AtTick"/> → Chaching · 틱 → ms 변환) · 감정 피크(Impact) — 큐 링 256 · 시각 정렬 삽입 · 같은 종류 최소 간격(쿨다운).
///   ▸ 믹싱: 블록 단위(임의 길이 · 스테레오/모노 float 또는 16-bit 인터리브) 샘플 정밀(10 ms 요구 → 오차 &lt; 1 샘플) 오버레이 · 뱅크 버퍼 직접 합산 ·
///     덕킹 없는 가산 후 소프트 리미터(tanh 근사 · −1 dBFS) — 기존 BGM/보컬은 이득 1 그대로.
/// </summary>
public sealed class ProceduralViralSoundFxSynthesizer
{
    public const int Fs = 48000, Kinds = 4, CueCap = 256;
    public static readonly int[] LengthSamples = { (int)(0.42 * Fs), (int)(0.09 * Fs), (int)(0.60 * Fs), (int)(0.55 * Fs) };
    /// <summary>같은 종류 큐 최소 간격(ms) — 단어 팝 도배 · 연속 컷 휘쉬 겹침 방지.</summary>
    public static readonly float[] CooldownMs = { 180f, 90f, 400f, 250f };

    private readonly float[][] _bank = { new float[LengthSamples[0]], new float[LengthSamples[1]], new float[LengthSamples[2]], new float[LengthSamples[3]] };
    private readonly SfxCue[] _cues = new SfxCue[CueCap];
    private int _n;

    public ProceduralViralSoundFxSynthesizer(uint seed = 0xF00Du)
    {
        Seed = seed == 0 ? 1u : seed;
        for (var k = 0; k < Kinds; k++) Render((ViralSfxKind)k, _bank[k], Seed + (uint)k * 7919u);
    }

    public uint Seed { get; }
    public float MasterGain { get; set; } = 0.8f;
    public float LimiterCeiling { get; set; } = 0.891f;   // −1 dBFS
    public int CueCount => _n;
    public ref readonly SfxCue Cue(int i) => ref _cues[i];
    public ReadOnlySpan<float> Bank(ViralSfxKind k) => _bank[(int)k];
    public long Dropped { get; private set; }
    public long Suppressed { get; private set; }
    public long Mixes { get; private set; }
    public long LimitedSamples { get; private set; }
    public double LastMixUs { get; private set; }

    public static string KindName(ViralSfxKind k) => k switch { ViralSfxKind.Whoosh => "휘쉬", ViralSfxKind.Pop => "팝", ViralSfxKind.Impact => "임팩트", _ => "차칭" };
    public static string KindIcon(ViralSfxKind k) => k switch { ViralSfxKind.Whoosh => "💨", ViralSfxKind.Pop => "🫧", ViralSfxKind.Impact => "💥", _ => "💰" };

    // ══════════════════════ 합성(무할당 · 결정적) ══════════════════════
    /// <summary>효과음 1 종 → dst(길이 = 렌더 샘플 수 · 초과분 0). 피크 정규화 0.9.</summary>
    public static void Render(ViralSfxKind kind, Span<float> dst, uint seed)
    {
        dst.Clear();
        var rng = seed == 0 ? 1u : seed;
        var n = Math.Min(dst.Length, LengthSamples[(int)kind]);
        const float Dt = 1f / Fs, TwoPi = 2f * MathF.PI;
        switch (kind)
        {
            case ViralSfxKind.Whoosh:
            {
                float lp = 0, bp = 0;
                for (var i = 0; i < n; i++)
                {
                    var x = (float)i / n;
                    var fc = 300f * MathF.Pow(4000f / 300f, x);
                    var f = 2f * MathF.Sin(MathF.PI * MathF.Min(fc, Fs / 6f) / Fs); const float Q = 1f / 2.2f;
                    var hp = Noise(ref rng) - lp - Q * bp; bp += f * hp; lp += f * bp;
                    dst[i] = bp * MathF.Sin(MathF.PI * x) * MathF.Sin(MathF.PI * x);
                }
                break;
            }
            case ViralSfxKind.Pop:
            {
                float ph = 0;
                for (var i = 0; i < n; i++)
                {
                    var t = i * Dt;
                    var f = 420f + (1200f - 420f) * MathF.Exp(-t / 0.012f);
                    ph += TwoPi * f * Dt;
                    var click = i < 96 ? Noise(ref rng) * (1f - i / 96f) * 0.6f : 0f;
                    dst[i] = (MathF.Sin(ph) + click) * MathF.Exp(-t / 0.018f) * MathF.Min(1f, i / 24f);
                }
                break;
            }
            case ViralSfxKind.Impact:
            {
                float ph = 0, mph = 0, lp = 0;
                for (var i = 0; i < n; i++)
                {
                    var t = i * Dt;
                    var fc = 55f * (1f + 2f * MathF.Exp(-t / 0.035f));
                    mph += TwoPi * fc * 1.5f * Dt;
                    ph += TwoPi * fc * Dt;
                    var idx = 2.5f * MathF.Exp(-t / 0.05f);
                    var body = MathF.Sin(ph + idx * MathF.Sin(mph)) * MathF.Exp(-t / 0.22f);
                    lp += 0.06f * (Noise(ref rng) - lp);
                    var burst = lp * 3f * MathF.Exp(-t / 0.04f);
                    dst[i] = (body + burst) * MathF.Min(1f, i / 48f);
                }
                break;
            }
            default:
            {
                float p1 = 0, m1 = 0, p2 = 0, m2 = 0, hpPrev = 0, hpOut = 0;
                var second = (int)(0.11f * Fs);
                for (var i = 0; i < n; i++)
                {
                    var t = i * Dt;
                    m1 += TwoPi * 2100f * 3.5f * Dt; p1 += TwoPi * 2100f * Dt;
                    var bell1 = MathF.Sin(p1 + 1.8f * MathF.Exp(-t / 0.08f) * MathF.Sin(m1)) * MathF.Exp(-t / 0.15f);
                    var bell2 = 0f;
                    if (i >= second)
                    {
                        var t2 = (i - second) * Dt;
                        m2 += TwoPi * 3200f * 3.5f * Dt; p2 += TwoPi * 3200f * Dt;
                        bell2 = MathF.Sin(p2 + 1.6f * MathF.Exp(-t2 / 0.08f) * MathF.Sin(m2)) * MathF.Exp(-t2 / 0.2f);
                    }
                    var w = Noise(ref rng); hpOut = 0.9f * (hpOut + w - hpPrev); hpPrev = w;
                    var cha = i < 720 ? hpOut * (1f - i / 720f) * 0.8f : 0f;
                    dst[i] = 0.6f * bell1 + 0.7f * bell2 + cha;
                }
                break;
            }
        }
        float peak = 0; for (var i = 0; i < n; i++) peak = MathF.Max(peak, MathF.Abs(dst[i]));
        if (peak > 1e-6f) { var g = 0.9f / peak; for (var i = 0; i < n; i++) dst[i] *= g; }
    }

    private static float Noise(ref uint s) { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return (s & 0xFFFFFF) / 8388608f - 1f; }

    // ══════════════════════ 큐(무할당) ══════════════════════
    public void ClearCues() { _n = 0; }

    /// <summary>시각 정렬 삽입 · 같은 종류 쿨다운 이내 = 억제 · 링 가득 = 드롭. 반환 = 채택 여부.</summary>
    public bool AddCue(double timeMs, ViralSfxKind kind, float gain = 1f)
    {
        if (!double.IsFinite(timeMs) || timeMs < 0) { Dropped++; return false; }
        for (var i = 0; i < _n; i++) if (_cues[i].Kind == kind && Math.Abs(_cues[i].TimeMs - timeMs) < CooldownMs[(int)kind]) { Suppressed++; return false; }
        if (_n == CueCap) { Dropped++; return false; }
        var j = _n;
        while (j > 0 && _cues[j - 1].TimeMs > timeMs) { _cues[j] = _cues[j - 1]; j--; }
        _cues[j] = new SfxCue { TimeMs = timeMs, Kind = kind, Gain = Math.Clamp(gain, 0f, 2f) };
        _n++;
        return true;
    }

    /// <summary>v17.52 자막 단어 등장 → 팝(문장 첫 단어 = 1.0 · 이후 0.55). offsetMs = 클립 타임라인 원점 보정.</summary>
    public int CollectCaptions(RealtimeWordByWordMotionTypographyEngine typo, double offsetMs = 0)
    {
        var added = 0;
        for (var i = 0; i < typo.WordCount; i++)
        {
            ref readonly var w = ref typo.Word(i);
            if (AddCue(w.StartMs + offsetMs, ViralSfxKind.Pop, i == 0 || w.Line != typo.Word(i - 1).Line ? 1f : 0.55f)) added++;
        }
        return added;
    }

    /// <summary>v17.54 컷 이벤트 열 → 휘쉬(전환 120 ms 전 시작 = 스윕 정점이 컷에 정렬) · 드롭 컷 = 임팩트(컷 시각).</summary>
    public int CollectCuts(ReadOnlySpan<RhythmCutEvent> cuts, double offsetMs = 0)
    {
        var added = 0;
        for (var i = 0; i < cuts.Length; i++)
        {
            var t = cuts[i].PresentSec * 1000 + offsetMs;
            if (cuts[i].Reason == RhythmCutReason.Drop) { if (AddCue(t, ViralSfxKind.Impact, 1f)) added++; }
            else if (AddCue(Math.Max(0, t - WhooshLeadMs), ViralSfxKind.Whoosh, cuts[i].Reason == RhythmCutReason.Phrase ? 0.9f : 0.6f)) added++;
        }
        return added;
    }

    /// <summary>v17.54 시퀀서 컷 링 직접 수집(최신 → 과거).</summary>
    public int CollectCuts(RealtimeRhythmDropCameraSequencerEngine seq, double offsetMs = 0)
    {
        var added = 0;
        for (var i = 0; i < seq.CutCount; i++)
        {
            ref readonly var c = ref seq.Cut(i);
            var t = c.PresentSec * 1000 + offsetMs;
            if (c.Reason == RhythmCutReason.Drop) { if (AddCue(t, ViralSfxKind.Impact, 1f)) added++; }
            else if (AddCue(Math.Max(0, t - WhooshLeadMs), ViralSfxKind.Whoosh, c.Reason == RhythmCutReason.Phrase ? 0.9f : 0.6f)) added++;
        }
        return added;
    }

    /// <summary>휘쉬 선행(ms) — 0.42 s 스윕의 진폭 정점(≈ 중간 이후)이 컷 프레임에 걸리도록.</summary>
    public const double WhooshLeadMs = 120;

    /// <summary>v17.68 구매 이벤트 → 차칭(수량 많을수록 크게). tickHz = 구매 틱 시계(MCR 60 Hz) · startTick = 클립 0 ms 의 틱.</summary>
    public int CollectPurchases(ReadOnlySpan<PurchaseEvent> ev, long startTick, double tickHz, double offsetMs = 0)
    {
        var added = 0;
        for (var i = 0; i < ev.Length; i++)
        {
            if (ev[i].AtTick < startTick) continue;
            var t = (ev[i].AtTick - startTick) * 1000.0 / tickHz + offsetMs;
            if (AddCue(t, ViralSfxKind.Chaching, Math.Clamp(0.7f + 0.1f * ev[i].Qty, 0.7f, 1.2f))) added++;
        }
        return added;
    }

    /// <summary>v17.68 세일즈 FSM 구매 피드 직접 수집.</summary>
    public int CollectPurchases(LiveSalesPitchReactionSynthesizer sales, long startTick, double tickHz, double offsetMs = 0)
    {
        var added = 0;
        for (var i = 0; i < sales.FeedCount; i++)
        {
            ref readonly var e = ref sales.Feed(i);
            if (e.AtTick < startTick) continue;
            if (AddCue((e.AtTick - startTick) * 1000.0 / tickHz + offsetMs, ViralSfxKind.Chaching, Math.Clamp(0.7f + 0.1f * e.Qty, 0.7f, 1.2f))) added++;
        }
        return added;
    }

    // ══════════════════════ 믹싱(무할당 · 샘플 정밀) ══════════════════════
    /// <summary>
    /// 블록 [blockStart, blockStart + frames) 에 걸치는 큐를 dst 에 가산(인터리브 channels · float) → 소프트 리미터. blockStart = 타임라인 절대 샘플.
    /// 큐 시작 샘플 = round(TimeMs × 48) — 블록 경계를 넘는 큐는 다음 블록에서 이어짐.
    /// </summary>
    public void Mix(Span<float> dst, int channels, long blockStart)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        channels = Math.Max(1, channels);
        var frames = dst.Length / channels; var blockEnd = blockStart + frames;
        for (var c = 0; c < _n; c++)
        {
            ref readonly var q = ref _cues[c];
            var s0 = StartSample(q.TimeMs);
            if (s0 >= blockEnd) break;   // 정렬 = 이후 큐 전부 미래
            var bank = _bank[(int)q.Kind];
            var s1 = s0 + bank.Length;
            if (s1 <= blockStart) continue;
            var a = (int)Math.Max(0, s0 - blockStart); var b = (int)Math.Min(frames, s1 - blockStart);
            var g = q.Gain * MasterGain;
            var off = (int)(blockStart + a - s0);
            for (var i = a; i < b; i++)
            {
                var v = bank[off + i - a] * g;
                var o = i * channels;
                for (var ch = 0; ch < channels; ch++) dst[o + ch] += v;
            }
        }
        Limit(dst);
        Mixes++;
        LastMixUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>16-bit 인터리브 PCM 제자리 믹싱(스크래치 = 호출자 float 버퍼 ≥ pcm.Length).</summary>
    public void Mix(Span<short> pcm, int channels, long blockStart, Span<float> scratch)
    {
        var n = pcm.Length; var s = scratch[..n];
        for (var i = 0; i < n; i++) s[i] = pcm[i] * (1f / 32768f);
        Mix(s, channels, blockStart);
        for (var i = 0; i < n; i++) pcm[i] = (short)Math.Clamp(MathF.Round(s[i] * 32767f), -32768f, 32767f);
    }

    public static long StartSample(double timeMs) => (long)Math.Round(timeMs * (Fs / 1000.0));

    /// <summary>천장 초과 시 소프트 니(tanh 근사) — 천장 미만 샘플은 비트 그대로.</summary>
    private void Limit(Span<float> x)
    {
        var c = LimiterCeiling;
        for (var i = 0; i < x.Length; i++)
        {
            var v = x[i]; var a = MathF.Abs(v);
            if (a <= c) continue;
            var over = (a - c) / (1f - c + 1e-6f);
            var y = c + (1f - c) * (over / (1f + over));   // 1 로 점근
            x[i] = v < 0 ? -y : y;
            LimitedSamples++;
        }
    }

    /// <summary>타임라인 전체 길이(ms · 마지막 큐 종료).</summary>
    public double EndMs()
    {
        double e = 0;
        for (var i = 0; i < _n; i++) e = Math.Max(e, _cues[i].TimeMs + LengthSamples[(int)_cues[i].Kind] * 1000.0 / Fs);
        return e;
    }
}
