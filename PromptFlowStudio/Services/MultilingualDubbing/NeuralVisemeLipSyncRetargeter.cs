using PromptFlowStudio.Services.DigitalHuman;
using PromptFlowStudio.Services.GaussianFaceHand;

namespace PromptFlowStudio.Services.MultilingualDubbing;

/// <summary>v17.82.0 14종 비젬(모음 5 · 양순 3 · 순치 2 · 치조 2 · 유음 2).</summary>
public enum DubViseme : byte { AA, EE, IH, OH, OU, M, B, P, F, V, S, T, L, R }

/// <summary>
/// [PromptFlow Studio v17.82.0] 👄 3DGS 뉴럴 비젬 립싱크 리타게팅 합성기(<see cref="Analyze"/> · <see cref="Retarget"/> 무할당 · 60 fps).
///   ▸ 48 kHz PCM 800 샘플(16.7 ms) 프레임 → RMS · 영교차율 · Goertzel 24 밴드(200~3000 Hz 로그 22 + 4.5/7 kHz) 포먼트 스펙트럼 → F1/F2 피크.
///   ▸ 룰베이스 분류: 무음 전이 → M/B/P 폐쇄 · 온셋 버스트 → P/B/T · 고역 마찰 → S/F/V · (F1, F2) 가우시안 프로토타입 → AA/EE/IH/OH/OU/L/R 소프트 가중치.
///   ▸ 14 비젬 × ARKit 52 사상 행렬 → 목표 가중치 → 어택/릴리스 보간(계수 × <c>rate</c> — v17.82 드리프트 가드 미세 가감속) → v17.58 <see cref="RealtimeGaussianFacialBlendshapeEngine"/> 적용.
/// </summary>
public sealed class NeuralVisemeLipSyncRetargeter
{
    public const int Fs = 48_000, FrameSamples = 800, VisemeCount = 14, K = 52, Bands = 24, LowBands = 22;
    public const float FrameMs = 1000f * FrameSamples / Fs, Attack = 0.6f, Release = 0.35f;
    private const float SilenceRms = 0.012f;

    private static readonly float[] BandHz = BuildBands();
    private static readonly float[] Coef = BuildCoef();
    private static readonly float[] Map = BuildMap();
    private static readonly float[] Win = BuildWindow();   // Hann — 메인 로브 ±120 Hz 로 인접 배음 포착
    private static readonly float[,] Proto = { { 750, 1200 }, { 300, 2300 }, { 470, 1850 }, { 500, 880 }, { 330, 780 }, { 360, 1100 }, { 420, 1300 } };
    private static readonly DubViseme[] ProtoViseme = { DubViseme.AA, DubViseme.EE, DubViseme.IH, DubViseme.OH, DubViseme.OU, DubViseme.L, DubViseme.R };

    private readonly float[] _vis = new float[VisemeCount];
    private readonly float[] _target = new float[K];
    private readonly float[] _blend = new float[K];
    private readonly float[] _band = new float[Bands];
    private float _prevRms;

    public ReadOnlySpan<float> Visemes => _vis;
    public ReadOnlySpan<float> Blend => _blend;
    public ReadOnlySpan<float> BandPower => _band;
    public float Weight(ArkitBlendshape k) => _blend[(int)k];
    public float Viseme(DubViseme v) => _vis[(int)v];
    public DubViseme Dominant { get; private set; }
    public float Rms { get; private set; }
    public float Zcr { get; private set; }
    public float F1 { get; private set; }
    public float F2 { get; private set; }
    public float HfRatio { get; private set; }
    public long Frames { get; private set; }
    public double LastUs { get; private set; }
    public double MaxUs { get; private set; }

    public static string Name(DubViseme v) => v.ToString();

    public void Reset()
    {
        Array.Clear(_vis); Array.Clear(_target); Array.Clear(_blend); Array.Clear(_band);
        _prevRms = 0; Rms = Zcr = F1 = F2 = HfRatio = 0; Dominant = DubViseme.M; Frames = 0; LastUs = MaxUs = 0;
    }

    // ══════════════════════ 분석(무할당) ══════════════════════
    /// <summary>16.7 ms 프레임 → 14 비젬 가중치(합 ≤ 1).</summary>
    public void Analyze(ReadOnlySpan<float> frame)
    {
        var n = Math.Min(frame.Length, FrameSamples);
        double e = 0; var zc = 0;
        for (var i = 0; i < n; i++) { e += frame[i] * frame[i]; if (i > 0 && (frame[i] >= 0) != (frame[i - 1] >= 0)) zc++; }
        var rms = n > 0 ? (float)Math.Sqrt(e / n) : 0f;
        Zcr = n > 1 ? (float)zc / (n - 1) : 0f;
        float total = 1e-12f, low = 0, mid = 0;
        for (var b = 0; b < Bands; b++)
        {
            float s1 = 0, s2 = 0; var c = Coef[b];
            for (var i = 0; i < n; i++) { var s = frame[i] * Win[i] + c * s1 - s2; s2 = s1; s1 = s; }
            var p = Math.Max(0f, s1 * s1 + s2 * s2 - c * s1 * s2);
            _band[b] = p; total += p;
            if (BandHz[b] < 900) low += p; else if (b < LowBands) mid += p;
        }
        var hf = _band[LowBands] + _band[LowBands + 1];
        HfRatio = hf / total;
        Array.Clear(_vis);
        var onset = rms > SilenceRms && rms > _prevRms * 3.5f;
        if (rms < SilenceRms)
        {
            // 무음: 직전 발화 → 양순 폐쇄(M) · 장기 무음 → 휴지(가중 0)
            if (_prevRms >= SilenceRms) _vis[(int)DubViseme.M] = 0.8f;
            F1 = F2 = 0;
        }
        else if (HfRatio > 0.45f || (Zcr > 0.22f && HfRatio > 0.25f))
        {
            if (onset) _vis[(int)DubViseme.T] = 0.9f;
            else if (HfRatio > 0.45f || _band[LowBands + 1] > _band[LowBands]) _vis[(int)DubViseme.S] = 1f;   // 치찰음 = 고역 우세
            else if (low / total > 0.2f) _vis[(int)DubViseme.V] = 1f;
            else _vis[(int)DubViseme.F] = 1f;
        }
        else if (onset && _prevRms < SilenceRms)
        {
            if (HfRatio > 0.15f) _vis[(int)DubViseme.P] = 0.9f; else _vis[(int)DubViseme.B] = 0.9f;
        }
        else if (low / total > 0.9f && rms < 0.06f)
        {
            _vis[(int)DubViseme.M] = 0.9f;
        }
        else
        {
            Peaks();
            var voicing = Math.Clamp(1f - HfRatio * 2f, 0f, 1f);
            float sum = 0;
            for (var p = 0; p < ProtoViseme.Length; p++)
            {
                var d1 = (F1 - Proto[p, 0]) / 160f; var d2 = (F2 - Proto[p, 1]) / 380f;
                var w = MathF.Exp(-(d1 * d1 + d2 * d2));
                _vis[(int)ProtoViseme[p]] = w; sum += w;
            }
            var k = sum > 1e-6f ? voicing * Math.Min(1f, rms * 12f) / sum : 0f;
            for (var p = 0; p < ProtoViseme.Length; p++) _vis[(int)ProtoViseme[p]] *= k;
        }
        _prevRms = rms; Rms = rms;
        var best = 0;
        for (var v = 1; v < VisemeCount; v++) if (_vis[v] > _vis[best]) best = v;
        Dominant = (DubViseme)best;
    }

    private void Peaks()
    {
        int b1 = -1, b2 = -1;
        for (var b = 0; b < LowBands; b++)
        {
            if (BandHz[b] < 900) { if (b1 < 0 || _band[b] > _band[b1]) b1 = b; }
            else if (b2 < 0 || _band[b] > _band[b2]) b2 = b;
        }
        F1 = b1 < 0 ? 0 : Interp(b1);
        F2 = b2 < 0 ? 0 : Interp(b2);
    }

    /// <summary>인접 밴드 파워 가중 중심(로그 격자 보간).</summary>
    private float Interp(int b)
    {
        float w = _band[b], f = BandHz[b] * w;
        if (b > 0) { w += _band[b - 1] * 0.5f; f += BandHz[b - 1] * _band[b - 1] * 0.5f; }
        if (b + 1 < LowBands) { w += _band[b + 1] * 0.5f; f += BandHz[b + 1] * _band[b + 1] * 0.5f; }
        return w > 0 ? f / w : BandHz[b];
    }

    // ══════════════════════ 리타게팅(무할당) ══════════════════════
    /// <summary>비젬 → ARKit 목표 → 어택/릴리스 보간(<paramref name="rate"/> = 드리프트 가드 보간 계수 배율).</summary>
    public void Retarget(float rate = 1f)
    {
        rate = Math.Clamp(rate, 0f, 3f);
        Array.Clear(_target);
        for (var v = 0; v < VisemeCount; v++)
        {
            var w = _vis[v];
            if (w <= 0f) continue;
            var row = v * K;
            for (var k = 0; k < K; k++) _target[k] += w * Map[row + k];
        }
        for (var k = 0; k < K; k++)
        {
            var t = Math.Min(1f, _target[k]);
            var a = Math.Min(1f, (t > _blend[k] ? Attack : Release) * rate);
            _blend[k] += a * (t - _blend[k]);
        }
        Frames++;
    }

    /// <summary>PCM 위치(샘플)의 16.7 ms 프레임 분석 + 리타게팅(범위 밖 = 무음).</summary>
    public void Process(ReadOnlySpan<float> pcm, int pos, float rate)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (pos < 0 || pos >= pcm.Length) Analyze(ReadOnlySpan<float>.Empty);
        else Analyze(pcm.Slice(pos, Math.Min(FrameSamples, pcm.Length - pos)));
        Retarget(rate);
        LastUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        if (LastUs > MaxUs) MaxUs = LastUs;
    }

    /// <summary>v17.58 3DGS 얼굴 엔진에 입/턱 가중치 반영(타 영역 유지).</summary>
    public void Apply(RealtimeGaussianFacialBlendshapeEngine face)
    {
        for (var k = (int)ArkitBlendshape.JawForward; k <= (int)ArkitBlendshape.MouthUpperUpRight; k++) face.SetWeight((ArkitBlendshape)k, _blend[k]);
        face.SetWeight(ArkitBlendshape.CheekPuff, _blend[(int)ArkitBlendshape.CheekPuff]);
        face.SetWeight(ArkitBlendshape.TongueOut, _blend[(int)ArkitBlendshape.TongueOut]);
    }

    // ══════════════════════ 정적 테이블 ══════════════════════
    private static float[] BuildBands()
    {
        var b = new float[Bands];
        for (var i = 0; i < LowBands; i++) b[i] = 200f * MathF.Pow(3000f / 200f, i / (LowBands - 1f));
        b[LowBands] = 4500f; b[LowBands + 1] = 7000f;
        return b;
    }

    private static float[] BuildCoef()
    {
        var c = new float[Bands];
        for (var i = 0; i < Bands; i++) c[i] = 2f * MathF.Cos(2f * MathF.PI * BandHz[i] / Fs);
        return c;
    }

    private static float[] BuildWindow()
    {
        var w = new float[FrameSamples];
        for (var i = 0; i < FrameSamples; i++) w[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / (FrameSamples - 1));
        return w;
    }

    private static float[] BuildMap()
    {
        var m = new float[VisemeCount * K];
        void S(DubViseme v, ArkitBlendshape k, float w) => m[(int)v * K + (int)k] = w;
        void P(DubViseme v, ArkitBlendshape l, ArkitBlendshape r, float w) { S(v, l, w); S(v, r, w); }
        S(DubViseme.AA, ArkitBlendshape.JawOpen, 0.75f); P(DubViseme.AA, ArkitBlendshape.MouthLowerDownLeft, ArkitBlendshape.MouthLowerDownRight, 0.4f); P(DubViseme.AA, ArkitBlendshape.MouthStretchLeft, ArkitBlendshape.MouthStretchRight, 0.1f);
        S(DubViseme.EE, ArkitBlendshape.JawOpen, 0.18f); P(DubViseme.EE, ArkitBlendshape.MouthSmileLeft, ArkitBlendshape.MouthSmileRight, 0.6f); P(DubViseme.EE, ArkitBlendshape.MouthStretchLeft, ArkitBlendshape.MouthStretchRight, 0.35f);
        S(DubViseme.IH, ArkitBlendshape.JawOpen, 0.35f); P(DubViseme.IH, ArkitBlendshape.MouthSmileLeft, ArkitBlendshape.MouthSmileRight, 0.3f); P(DubViseme.IH, ArkitBlendshape.MouthStretchLeft, ArkitBlendshape.MouthStretchRight, 0.2f);
        S(DubViseme.OH, ArkitBlendshape.JawOpen, 0.5f); S(DubViseme.OH, ArkitBlendshape.MouthFunnel, 0.55f); S(DubViseme.OH, ArkitBlendshape.MouthPucker, 0.2f);
        S(DubViseme.OU, ArkitBlendshape.JawOpen, 0.15f); S(DubViseme.OU, ArkitBlendshape.MouthPucker, 0.8f); S(DubViseme.OU, ArkitBlendshape.MouthFunnel, 0.35f);
        S(DubViseme.M, ArkitBlendshape.MouthClose, 0.8f); P(DubViseme.M, ArkitBlendshape.MouthPressLeft, ArkitBlendshape.MouthPressRight, 0.5f);
        S(DubViseme.B, ArkitBlendshape.MouthClose, 0.7f); P(DubViseme.B, ArkitBlendshape.MouthPressLeft, ArkitBlendshape.MouthPressRight, 0.3f); S(DubViseme.B, ArkitBlendshape.MouthRollLower, 0.2f);
        S(DubViseme.P, ArkitBlendshape.MouthClose, 0.6f); P(DubViseme.P, ArkitBlendshape.MouthPressLeft, ArkitBlendshape.MouthPressRight, 0.7f); S(DubViseme.P, ArkitBlendshape.CheekPuff, 0.2f);
        S(DubViseme.F, ArkitBlendshape.MouthRollLower, 0.6f); P(DubViseme.F, ArkitBlendshape.MouthUpperUpLeft, ArkitBlendshape.MouthUpperUpRight, 0.3f); S(DubViseme.F, ArkitBlendshape.JawOpen, 0.1f);
        S(DubViseme.V, ArkitBlendshape.MouthRollLower, 0.5f); P(DubViseme.V, ArkitBlendshape.MouthUpperUpLeft, ArkitBlendshape.MouthUpperUpRight, 0.25f); S(DubViseme.V, ArkitBlendshape.JawOpen, 0.15f);
        P(DubViseme.S, ArkitBlendshape.MouthStretchLeft, ArkitBlendshape.MouthStretchRight, 0.5f); P(DubViseme.S, ArkitBlendshape.MouthSmileLeft, ArkitBlendshape.MouthSmileRight, 0.2f); S(DubViseme.S, ArkitBlendshape.JawOpen, 0.08f);
        S(DubViseme.T, ArkitBlendshape.JawOpen, 0.2f); S(DubViseme.T, ArkitBlendshape.TongueOut, 0.15f); S(DubViseme.T, ArkitBlendshape.MouthShrugUpper, 0.2f);
        S(DubViseme.L, ArkitBlendshape.JawOpen, 0.3f); S(DubViseme.L, ArkitBlendshape.TongueOut, 0.3f);
        S(DubViseme.R, ArkitBlendshape.MouthPucker, 0.35f); S(DubViseme.R, ArkitBlendshape.MouthFunnel, 0.3f); S(DubViseme.R, ArkitBlendshape.JawOpen, 0.2f);
        return m;
    }
}
