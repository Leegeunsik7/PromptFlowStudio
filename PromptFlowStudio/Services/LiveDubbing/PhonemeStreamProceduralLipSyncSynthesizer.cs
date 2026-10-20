using PromptFlowStudio.Services.DigitalHuman;

namespace PromptFlowStudio.Services.LiveDubbing;

/// <summary>
/// [PromptFlow Studio v17.32.0] 👄 음소 스트림 연동 60 fps 절차적 안면 립싱크 합성기(레인 1개당 1 인스턴스 · 무할당 링버퍼).
///   ▸ 10 ms 슬라이딩 윈도우(가드 출력 홉): RMS(적응 피크 정규화 포락) · 영교차율 · 고역 차분비 + 엔진 음소 타임라인(입력 위치 역매핑 · 동시조음 혼합)
///     → 16 음소 가중치(모음 개구 = 0.3 + 0.7·포락 · 무음 게이트 · 마찰 ZCR 부스트).
///   ▸ 음소 × 입모양 18 행렬 → v17.12 ARKit 52 블렌드셰이프 중 jawOpen · mouthPucker · mouthFunnel · mouthSmile(lipCornerPuller) · stretch · close · press ·
///     roll · upperUp · lowerDown · shrugUpper · tongueOut 키프레임(링 128 = 1.28 s).
///   ▸ 60 fps 평가: 가드 ON = <see cref="AudioVisualSyncJitterCompensationGuard.LipLagMs"/> 룩어헤드 3차 Hermite · OFF = 최근접 홀드 → 52 배열 입 영역만 오버라이드.
/// </summary>
public sealed class PhonemeStreamProceduralLipSyncSynthesizer
{
    public const int Hop = RealtimeNeuralVoiceCloningDubbingEngine.Hop, Fs = RealtimeNeuralVoiceCloningDubbingEngine.Fs, KeyRing = 128;
    public const int Phonemes = RealtimeNeuralVoiceCloningDubbingEngine.PhonemeCount, Shapes = NeuralFacialMuscleSimulator.ShapeCount;

    public static readonly ArkitBlendshape[] Mouth =
    {
        ArkitBlendshape.JawOpen, ArkitBlendshape.MouthPucker, ArkitBlendshape.MouthFunnel, ArkitBlendshape.MouthSmileLeft, ArkitBlendshape.MouthSmileRight,
        ArkitBlendshape.MouthStretchLeft, ArkitBlendshape.MouthStretchRight, ArkitBlendshape.MouthClose, ArkitBlendshape.MouthPressLeft, ArkitBlendshape.MouthPressRight,
        ArkitBlendshape.MouthRollLower, ArkitBlendshape.MouthRollUpper, ArkitBlendshape.MouthUpperUpLeft, ArkitBlendshape.MouthUpperUpRight,
        ArkitBlendshape.MouthLowerDownLeft, ArkitBlendshape.MouthLowerDownRight, ArkitBlendshape.MouthShrugUpper, ArkitBlendshape.TongueOut,
    };
    public const int MouthCount = 18;

    // 음소별 입모양(jaw · pucker · funnel · smileL/R · stretchL/R · close · pressL/R · rollLo/Up · upperUpL/R · lowerDownL/R · shrugUp · tongue)
    private static readonly float[] Table =
    {
        0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f,                         // Rest
        0.7f, 0f, 0f, 0.05f, 0.05f, 0.05f, 0.05f, 0f, 0f, 0f, 0f, 0f, 0.15f, 0.15f, 0.3f, 0.3f, 0f, 0f,  // A
        0.4f, 0f, 0f, 0.2f, 0.2f, 0.4f, 0.4f, 0f, 0f, 0f, 0f, 0f, 0.2f, 0.2f, 0.2f, 0.2f, 0f, 0f,        // E
        0.2f, 0f, 0f, 0.5f, 0.5f, 0.5f, 0.5f, 0f, 0f, 0f, 0f, 0f, 0.1f, 0.1f, 0.05f, 0.05f, 0f, 0f,      // I
        0.5f, 0.3f, 0.6f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0.1f, 0.1f, 0f, 0f,                // O
        0.2f, 0.8f, 0.3f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0.1f, 0f,                  // U
        0f, 0.1f, 0f, 0f, 0f, 0f, 0f, 0.8f, 0.6f, 0.6f, 0.2f, 0.2f, 0f, 0f, 0f, 0f, 0.2f, 0f,            // P/B/M
        0.1f, 0f, 0f, 0f, 0f, 0.1f, 0.1f, 0f, 0f, 0f, 0.7f, 0f, 0.3f, 0.3f, 0f, 0f, 0f, 0f,              // F/V
        0.2f, 0f, 0f, 0f, 0f, 0.1f, 0.1f, 0f, 0f, 0f, 0f, 0f, 0.1f, 0.1f, 0.1f, 0.1f, 0f, 0.6f,          // Th
        0.3f, 0f, 0f, 0.1f, 0.1f, 0.1f, 0.1f, 0f, 0f, 0f, 0f, 0f, 0.1f, 0.1f, 0.1f, 0.1f, 0f, 0.2f,      // L
        0.1f, 0f, 0f, 0.2f, 0.2f, 0.3f, 0.3f, 0.2f, 0f, 0f, 0f, 0f, 0.1f, 0.1f, 0f, 0f, 0f, 0f,          // S/Z
        0.35f, 0f, 0f, 0f, 0f, 0.1f, 0.1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0.1f, 0.1f, 0f, 0f,               // K/G
        0.25f, 0f, 0f, 0.05f, 0.05f, 0.2f, 0.2f, 0.1f, 0f, 0f, 0f, 0f, 0.05f, 0.05f, 0.05f, 0.05f, 0f, 0.1f, // T/D
        0.25f, 0.4f, 0.3f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0.1f, 0f,                 // R
        0.1f, 0.9f, 0.2f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0.15f, 0f,                 // W
        0.15f, 0f, 0f, 0.05f, 0.05f, 0.1f, 0.1f, 0.3f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0.1f,         // N
    };

    private readonly float[] _keys = new float[KeyRing * MouthCount];
    private readonly float[] _win = new float[6 * MouthCount];
    private readonly float[] _p = new float[Phonemes], _frame = new float[MouthCount], _prevFrame = new float[MouthCount];
    private long _hops;
    private float _peak = 1e-3f;

    public long Hops => _hops;
    public float Envelope { get; private set; }
    public float Zcr { get; private set; }
    public LipPhoneme Dominant { get; private set; }
    public float PhonemeWeight(int p) => _p[p];
    public float MouthWeight(int s) => _frame[s];
    public float KeyWeight(int back, int s) => _keys[(int)((_hops - 1 - back + KeyRing * 4L) % KeyRing) * MouthCount + s];
    public double LastHopUs { get; private set; }
    public double MaxHopUs { get; private set; }

    public static float TableWeight(LipPhoneme p, int s) => Table[(int)p * MouthCount + s];
    public static string MouthName(int s) => NeuralFacialMuscleSimulator.Name(Mouth[s]);

    public void Reset()
    {
        Array.Clear(_keys); Array.Clear(_p); Array.Clear(_frame); Array.Clear(_prevFrame);
        _hops = 0; _peak = 1e-3f; Envelope = Zcr = 0f; Dominant = LipPhoneme.Rest; MaxHopUs = 0;
    }

    /// <summary>10 ms 출력 홉 분석 → 음소 가중치 → 입모양 키프레임(무할당).</summary>
    public void PushHop(ReadOnlySpan<float> hop, RealtimeNeuralVoiceCloningDubbingEngine eng, int lane, long inputPos)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        float sq = 0f, diff = 0f; var zc = 0;
        var prevS = hop[0];
        for (var i = 0; i < hop.Length; i++)
        {
            var s = hop[i];
            sq += s * s;
            if (i > 0) { var d = s - prevS; diff += d * d; if ((s >= 0f) != (prevS >= 0f)) zc++; }
            prevS = s;
        }
        var rms = MathF.Sqrt(sq / hop.Length);
        _peak = MathF.Max(rms, _peak * 0.997f);
        var env = Math.Clamp(rms / (0.6f * _peak + 1e-6f), 0f, 1f);
        var zcr = (float)zc / hop.Length;
        var hi = sq > 1e-9f ? diff / (4f * sq) : 0f;
        Envelope = env; Zcr = zcr;

        var ph = eng.PhonemeAt(lane, inputPos, out var prev, out var blend, out _);
        Array.Clear(_p);
        _p[(int)ph] += blend;
        _p[(int)prev] += 1f - blend;
        var open = 0.3f + 0.7f * env;
        for (var p = 1; p <= (int)LipPhoneme.U; p++) _p[p] *= open;
        if (zcr > 0.18f && hi > 0.2f) { var k = MathF.Min(1f, (zcr - 0.18f) * 3f); _p[(int)LipPhoneme.Sz] += 0.3f * k * (1f - _p[(int)LipPhoneme.Sz]); }
        if (env < 0.06f && ph != LipPhoneme.Pbm)
            for (var p = 0; p < Phonemes; p++) _p[p] *= env / 0.06f;
        var best = 0f; Dominant = LipPhoneme.Rest;
        for (var p = 1; p < Phonemes; p++) if (_p[p] > best) { best = _p[p]; Dominant = (LipPhoneme)p; }

        var row = (int)(_hops % KeyRing) * MouthCount;
        for (var s = 0; s < MouthCount; s++)
        {
            var v = 0f;
            for (var p = 1; p < Phonemes; p++) { var w = _p[p]; if (w != 0f) v += w * Table[p * MouthCount + s]; }
            _keys[row + s] = MathF.Min(1f, v);
        }
        _hops++;
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        LastHopUs = us;
        if (us > MaxHopUs) MaxHopUs = us;
    }

    /// <summary>
    /// 60 fps 프레임 평가(출력 시계 ms) → w52 입 영역 오버라이드. 키프레임 h 의 시각 = (h + 0.5) × 10 ms.
    /// </summary>
    public void Evaluate(double tMs, Span<float> w52, AudioVisualSyncJitterCompensationGuard guard)
    {
        var hopMs = 1000.0 * Hop / Fs;
        if (_hops >= 6 && guard.Enabled)
        {
            var te = tMs - guard.LipEvalLagMs;
            var x = te / hopMs - 0.5;
            var k1 = (long)Math.Floor(x);
            k1 = Math.Clamp(k1, _hops - KeyRing + 3, _hops - 4);
            var u = (float)Math.Clamp(x - k1, 0.0, 1.0);
            for (var j = 0; j < 6; j++) Key(k1 - 2 + j).CopyTo(_win.AsSpan(j * MouthCount, MouthCount));
            guard.Hermite(_win, u, _prevFrame, _frame);
        }
        else if (_hops > 0)
        {
            var k = (long)Math.Floor(tMs / hopMs - 0.5);
            k = Math.Clamp(k, Math.Max(0, _hops - KeyRing + 1), _hops - 1);
            Key(k).CopyTo(_frame);
        }
        else Array.Clear(_frame);
        guard.MeasureFlicker(_frame);
        _frame.AsSpan().CopyTo(_prevFrame);
        for (var s = 0; s < MouthCount; s++) w52[(int)Mouth[s]] = _frame[s];
    }

    private ReadOnlySpan<float> Key(long h) => _keys.AsSpan((int)(((h % KeyRing) + KeyRing) % KeyRing) * MouthCount, MouthCount);
}
