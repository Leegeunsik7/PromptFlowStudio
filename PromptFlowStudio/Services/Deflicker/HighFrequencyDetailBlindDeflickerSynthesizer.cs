namespace PromptFlowStudio.Services.Deflicker;

/// <summary>
/// [PromptFlow Studio v17.07.0] 생성형 영상 시뮬레이터 — 레이스 모자(3 px 주기 레이스 · 체크 밴드 · 챙) 객체가 서브픽셀로 이동하고 배경이 패닝되며
/// 조명이 천천히 변하는 장면. 아티팩트: 프레임마다 텍스처 재생성 위상 떨림 · 고주파 이득 펌핑 · 잡음 · 주기적 모핑(챙 윤곽 돌출/눈 모양 변형).
/// </summary>
public sealed class GenerativeClipSimulator
{
    public const int MorphPeriod = 90, MorphStart = 45;
    private ulong _rng = 0x9E3779B97F4A7C15UL;

    public GenerativeClipSimulator(int width = 160, int height = 96) { Width = width; Height = height; }

    public int Width { get; }
    public int Height { get; }
    public float Artifacts { get; set; } = 1f;
    public float MorphAmp { get; set; } = 14f;
    public int MorphFrames { get; set; } = 3;
    public bool AutoMorph { get; set; } = true;
    public long ForcedMorphUntil { get; set; } = -1;
    public float PhaseU { get; private set; }
    public float PhaseV { get; private set; }
    public float Gain { get; private set; } = 1f;
    public float Light(long t) => 0.78f + 0.18f * MathF.Sin(t * 0.012f);
    public float ObjX(long t) => Width * 0.5f + Width * 0.16f * MathF.Sin(t * 0.021f);
    public float ObjY(long t) => Height * 0.5f + Height * 0.07f * MathF.Sin(t * 0.033f + 0.7f);
    public float Pan(long t) => t * 0.23f;
    public bool IsMorph(long t) => t < ForcedMorphUntil || AutoMorph && t >= MorphStart && (t - MorphStart) % MorphPeriod < MorphFrames;

    private float Next() { _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17; return (_rng >> 40) / 16777216f * 2f - 1f; }

    private static float Smooth(float d) => Math.Clamp(0.5f - d, 0f, 1f);

    /// <summary>프레임 t 렌더(clean = 아티팩트 없음 · 같은 장면).</summary>
    public void Render(long t, bool clean, Span<float> r, Span<float> g, Span<float> b)
    {
        float pu = 0, pv = 0, gain = 1f, noise = 0f;
        var morph = !clean && IsMorph(t);
        if (!clean && Artifacts > 0)
        {
            pu = Next() * 0.9f * Artifacts; pv = Next() * 0.9f * Artifacts;
            gain = 1f + Next() * 0.3f * Artifacts; noise = 0.012f * Artifacts;
            PhaseU = pu; PhaseV = pv; Gain = gain;
        }
        float L = Light(t), cx = ObjX(t), cy = ObjY(t), pan = Pan(t);
        const float crownRx = 22f, crownRy = 17f, brimRx = 34f, brimRy = 6f;
        float crownY = cy - 5f, brimY = cy + 9f;
        var bump = morph ? MorphAmp : 0f;
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                float bx = x + pan;
                float br = 0.30f + 0.10f * MathF.Sin(bx * 0.071f) * MathF.Cos(y * 0.093f) + 0.05f * MathF.Sin((bx + y) * 0.031f);
                float bgc = 0.28f + 0.08f * MathF.Cos(bx * 0.043f + y * 0.05f);
                float R = br, G = bgc + 0.05f, B = 0.42f + 0.06f * MathF.Sin(bx * 0.05f);
                // 챙(모핑 시 오른쪽에 돌출).
                float dxb = x - cx, dyb = y - brimY;
                var ang = MathF.Atan2(dyb, dxb);
                var rx = brimRx + bump * MathF.Max(0f, MathF.Cos(ang)) * MathF.Max(0f, MathF.Cos(ang));
                var brimD = (MathF.Sqrt(dxb * dxb / (rx * rx) + dyb * dyb / (brimRy * brimRy)) - 1f) * MathF.Min(rx, brimRy);
                float dxc = x - cx, dyc = y - crownY;
                // 모핑: 크라운 윤곽이 우상단으로 돌출(고대비 형태 환각).
                var cbump = bump > 0 ? bump * MathF.Pow(MathF.Max(0f, MathF.Cos(MathF.Atan2(dyc, dxc) + 0.8f)), 2f) : 0f;
                float crx = crownRx + cbump, cry = crownRy + cbump;
                var crownD = (MathF.Sqrt(dxc * dxc / (crx * crx) + dyc * dyc / (cry * cry)) - 1f) * MathF.Min(crx, cry);
                if (dyc > 9f) crownD = MathF.Max(crownD, dyc - 9f);
                var cov = MathF.Max(Smooth(brimD), Smooth(crownD));
                if (cov > 0)
                {
                    float u = x - cx + pu, v = y - cy + pv;
                    var lace = 0.5f + 0.5f * MathF.Sin(u * 2.0944f) * MathF.Sin(v * 2.0944f);
                    lace = 0.5f + (lace - 0.5f) * gain;
                    // 체크 밴드 = 형태(고정) · 레이스 = 위상/대비 재생성 떨림.
                    var bandCov = Smooth(MathF.Abs(dyc + 3f) - 2.5f);
                    var sq = Math.Clamp(0.5f + 1.6f * MathF.Sin((x - cx) * 1.0472f), 0f, 1f);   // 안티앨리어싱 사각파(6 px 주기)
                    float tr = 0.86f, tg = 0.80f, tb = 0.93f;
                    var k = 0.45f + 0.55f * lace;
                    k += (0.25f + 0.7f * sq - k) * bandCov;
                    if (brimD < 0.5f && crownD > 0.5f) { tr = 0.55f; tg = 0.42f; tb = 0.62f; k = 0.7f + 0.3f * lace; }
                    // 눈 장식 2개(모핑 시 왼쪽 눈 세로로 늘어남).
                    float ex1 = x - (cx - 7f), ex2 = x - (cx + 7f), ey = y - (crownY + 1f), eyH = morph ? 4.5f : 2.2f;
                    var eye = MathF.Max(Smooth((MathF.Sqrt(ex1 * ex1 / 6.25f + ey * ey / (eyH * eyH)) - 1f) * 2.2f), Smooth((MathF.Sqrt(ex2 * ex2 / 6.25f + ey * ey / 4.84f) - 1f) * 2.2f));
                    if (eye > 0) { tr += (0.08f - tr) * eye; tg += (0.08f - tg) * eye; tb += (0.08f - tb) * eye; k += (1f - k) * eye; }
                    R += (tr * k - R) * cov; G += (tg * k - G) * cov; B += (tb * k - B) * cov;
                }
                var i = y * Width + x;
                var nz = noise > 0 ? Next() * noise : 0f;
                r[i] = Math.Clamp(R * L + nz, 0f, 1f); g[i] = Math.Clamp(G * L + nz, 0f, 1f); b[i] = Math.Clamp(B * L + nz, 0f, 1f);
            }
    }
}

/// <summary>
/// [PromptFlow Studio v17.07.0] ✨ 고주파 디테일 분리 &amp; 블라인드 시공간 디플리커 합성기.
/// 채널별 라플라시안 피라미드(G0 → G1 → G2 · L0 = G0 − ↑G1 · L1 = G1 − ↑G2)로 조명(G2 기저)과 질감(L0/L1)을 분리하고,
/// 질감을 국소 조명으로 정규화(D = L / max(↑G, ε))해 조명 변화와 무관하게 이전 출력 질감을 흐름으로 끌어와 피닝(SIMD) — 질감 떨림/플리커만 제거,
/// 조명(기저)은 그대로 통과(선택: 전역 조명 펌핑만 EMA로 블라인드 평탄화). 모핑 가드 마스크 영역은 흐름 보상 직전 출력으로 롤백.
/// 지표: 질감 시간 불안정도(입력/출력) · 픽셀 일관성(%) · 조명 추종. 전 경로 무할당.
/// </summary>
public sealed class HighFrequencyDetailBlindDeflickerSynthesizer
{
    public const float Eps = 0.03f;
    public const float ConsistencyLevel = 3f / 255f;

    private readonly int _w, _h, _w1, _h1, _w2, _h2;
    private readonly float[][] _in = new float[3][], _out = new float[3][], _prevOut = new float[3][], _anchorD0 = new float[3][], _anchorD1 = new float[3][];
    private readonly float[] _g1, _g2, _u1, _u2, _l0, _l1, _d0, _d1, _p0, _p1, _g1o, _u1o, _tmp, _warpFull, _warpHalf;
    private readonly byte[] _mismatch;
    private readonly float[] _rigX, _rigY, _rigN, _rigW;
    private readonly float[] _anchorHf, _pinHf, _eIn, _ePin, _struct1, _structFull, _wFull, _wHalf, _rbFull, _rigFx, _rigFy;
    private readonly float[] _lumaIn1, _lumaPrevIn1, _lumaOut1, _lumaWarp1, _lumaOut0, _lumaOutPrev0, _hfIn, _hfInPrev, _hfOut, _hfOutPrev, _scratch, _scratch2;
    private bool _hasPrev;
    private float _ema = -1f;

    public HighFrequencyDetailBlindDeflickerSynthesizer(int width = 160, int height = 96)
    {
        _w = width; _h = height; _w1 = width / 2; _h1 = height / 2; _w2 = width / 4; _h2 = height / 4;
        int n0 = width * height, n1 = _w1 * _h1, n2 = _w2 * _h2;
        for (var c = 0; c < 3; c++) { _in[c] = new float[n0]; _out[c] = new float[n0]; _prevOut[c] = new float[n0]; _anchorD0[c] = new float[n0]; _anchorD1[c] = new float[n1]; }
        _g1 = new float[n1]; _g2 = new float[n2]; _u1 = new float[n0]; _u2 = new float[n1]; _l0 = new float[n0]; _l1 = new float[n1];
        _d0 = new float[n0]; _d1 = new float[n1]; _p0 = new float[n0]; _p1 = new float[n1]; _g1o = new float[n1]; _u1o = new float[n0];
        _tmp = new float[n0]; _warpFull = new float[n0]; _warpHalf = new float[n1];
        _lumaIn1 = new float[n1]; _lumaPrevIn1 = new float[n1]; _lumaOut1 = new float[n1]; _lumaWarp1 = new float[n1];
        _lumaOut0 = new float[n0]; _lumaOutPrev0 = new float[n0]; _hfIn = new float[n0]; _hfInPrev = new float[n0]; _hfOut = new float[n0]; _hfOutPrev = new float[n0];
        _scratch = new float[n0]; _scratch2 = new float[n0];
        _rigX = new float[n1]; _rigY = new float[n1]; _rigN = new float[n1]; _rigW = new float[n1];
        _mismatch = new byte[n0]; _anchorHf = new float[n0]; _pinHf = new float[n0]; _eIn = new float[n0]; _ePin = new float[n0];
        _struct1 = new float[n1]; _structFull = new float[n0]; _wFull = new float[n0]; _wHalf = new float[n1]; _rbFull = new float[n0]; _rigFx = new float[n0]; _rigFy = new float[n0];
    }

    public int Width => _w;
    public int Height => _h;
    public bool Pinning { get; set; } = true;
    public float Strength { get; set; } = 0.97f;
    /// <summary>L1(중간 대역) 피닝 비율(L0 대비).</summary>
    public float MidStrength { get; set; } = 0.25f;
    public bool LightDeflicker { get; set; }
    /// <summary>모핑 가드 기준 보상용 흐름 평활 반경(반해상도 px).</summary>
    public int RigidRadius { get; set; } = 8;
    public float SceneCutConfidence { get; set; } = 0.25f;
    public long Frames { get; private set; }
    public long SceneCuts { get; private set; }
    public double LastUs { get; private set; }
    public double DecomposeUs { get; private set; }
    public float FlickerIn { get; private set; }
    public float FlickerOut { get; private set; }
    public float Consistency { get; private set; } = 1f;
    public float MeanIn { get; private set; }
    public float MeanOut { get; private set; }
    public float LightGain { get; private set; } = 1f;
    public bool LastSceneCut { get; private set; }
    public ReadOnlySpan<float> In(int c) => _in[c];
    public ReadOnlySpan<float> Out(int c) => _out[c];
    public Span<float> InputPlane(int c) => _in[c];
    public ReadOnlySpan<float> DetailIn => _hfIn;
    public ReadOnlySpan<float> DetailOut => _hfOut;
    public ReadOnlySpan<float> LumaHalfIn => _lumaIn1;

    public void Reset() { _hasPrev = false; _ema = -1f; _rigValid = false; }

    private static float Luma(float r, float g, float b) => 0.2126f * r + 0.7152f * g + 0.0722f * b;

    /// <summary>입력 평면(InputPlane 0..2에 기록됨) 1 프레임 처리.</summary>
    public void Process(SubPixelOpticalFlowTexturePinningEngine flow, GenerativeMorphingSuppressionGuard guard)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Frames++;
        int n0 = _w * _h, n1 = _w1 * _h1;
        // 1) 반해상도 저역 휘도(흐름 · 모핑 판정용).
        for (var i = 0; i < n0; i++) _scratch[i] = Luma(_in[0][i], _in[1][i], _in[2][i]);
        FlowPlane.Down(_scratch, _w, _h, _tmp, _lumaIn1);
        FlowPlane.Smooth121(_lumaIn1, _w1, _h1, _tmp); FlowPlane.Smooth121(_lumaIn1, _w1, _h1, _tmp);
        LastSceneCut = false;
        if (_hasPrev)
        {
            flow.Estimate(_lumaIn1, _lumaPrevIn1);
            if (flow.MeanConfidence < SceneCutConfidence) { LastSceneCut = true; SceneCuts++; }
            else flow.AdvanceTracks();
        }
        var pin = Pinning && _hasPrev && !LastSceneCut;
        // 앵커 재고정: 첫 프레임 · 장면 전환 · 피닝 꺼짐 · 트랙 신뢰 소진.
        var reanchor = !pin || flow.NeedsAnchor;
        Detail(_scratch, _hfIn);
        if (pin && !reanchor) { Structure(); Occlusion(flow); Weights(flow); }
        // 2) 모핑 판정: 흐름 보상 직전 출력 저역 휘도 vs 현재 입력.
        if (_hasPrev)
        {
            // 가드 기준 = 직전 프레임의 평활 흐름(등속 예측)으로 보상 — 현재 조밀 흐름은 국소 형태 변형(모핑)까지 따라가 환각을 기준에 복사하므로.
            if (!_rigValid) PredictRigid(flow);
            FlowPlane.Warp(_lumaOut1, _w1, _h1, _rigX, _rigY, _lumaWarp1);
            guard.Analyze(_lumaIn1, _lumaWarp1, flow.Confidence, LastSceneCut);
            // 모핑/장면 전환이 아닌 프레임의 흐름만 다음 예측에 반영(환각이 예측을 끌고 가지 않게).
            if (guard.LastMorphTiles == 0 || LastSceneCut) PredictRigid(flow);
        }
        if (pin && guard.Suppression > 0) RollbackWeights(flow, guard);
        var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        // 3) 채널별 분리 → 정규화 디테일 피닝 → 재합성.
        for (var c = 0; c < 3; c++)
        {
            var g0 = _in[c];
            FlowPlane.Down(g0, _w, _h, _tmp, _g1);
            FlowPlane.Down(_g1, _w1, _h1, _tmp, _g2);
            FlowPlane.Up(_g1, _w1, _h1, _u1);
            FlowPlane.Up(_g2, _w2, _h2, _u2);
            for (var i = 0; i < n0; i++) _l0[i] = g0[i] - _u1[i];
            for (var i = 0; i < n1; i++) _l1[i] = _g1[i] - _u2[i];
            PinSimd.Normalize(_l0, _u1, Eps, _d0);
            PinSimd.Normalize(_l1, _u2, Eps, _d1);
            if (pin)
            {
                flow.PinPlane(_d0, _anchorD0[c], true, Strength, _wFull, _warpFull, _p0);
                flow.PinPlane(_d1, _anchorD1[c], false, Strength * MidStrength, _wHalf, _warpHalf, _p1);
            }
            else { _d0.AsSpan().CopyTo(_p0); _d1.AsSpan().CopyTo(_p1); }
            if (reanchor) { _p0.AsSpan().CopyTo(_anchorD0[c]); _p1.AsSpan().CopyTo(_anchorD1[c]); }
            PinSimd.Compose(_u2, _p1, _u2, Eps, _g1o);
            FlowPlane.Up(_g1o, _w1, _h1, _u1o);
            PinSimd.Compose(_u1o, _p0, _u1, Eps, _out[c]);
            // 4) 모핑 롤백: 기저까지 흐름 보상 직전 출력으로.
            if (pin && guard.Suppression > 0)
            {
                FlowPlane.WarpCubic(_prevOut[c], _w, _h, _rigFx, _rigFy, _warpFull);
                PinSimd.Pin(_out[c], _warpFull, _rbFull, 1f, _out[c]);
            }
        }
        if (reanchor) flow.Reanchor();
        DecomposeUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t1) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        // 5) 블라인드 전역 조명 플리커(선택): 평균 휘도를 EMA 추세로.
        double mi = 0, mo = 0;
        for (var i = 0; i < n0; i++) { mi += _scratch[i]; mo += Luma(_out[0][i], _out[1][i], _out[2][i]); }
        MeanIn = (float)(mi / n0);
        var meanOut = (float)(mo / n0);
        LightGain = 1f;
        if (LightDeflicker && !LastSceneCut)
        {
            _ema = _ema < 0 ? meanOut : _ema + (meanOut - _ema) * 0.25f;
            LightGain = Math.Clamp(_ema / MathF.Max(1e-3f, meanOut), 0.8f, 1.25f);
            for (var c = 0; c < 3; c++) for (var i = 0; i < n0; i++) _out[c][i] *= LightGain;
            meanOut *= LightGain;
        }
        else _ema = meanOut;
        for (var c = 0; c < 3; c++) for (var i = 0; i < n0; i++) _out[c][i] = Math.Clamp(_out[c][i], 0f, 1f);
        MeanOut = meanOut;
        // 6) 지표: 질감(L0 휘도) 시간 불안정도 · 픽셀 일관성.
        for (var i = 0; i < n0; i++) _lumaOut0[i] = Luma(_out[0][i], _out[1][i], _out[2][i]);
        Detail(_lumaOut0, _hfOut);
        if (reanchor) _hfOut.AsSpan().CopyTo(_anchorHf);
        if (_hasPrev && !LastSceneCut)
        {
            FlowPlane.WarpCubic(_hfInPrev, _w, _h, flow.FullFlowX, flow.FullFlowY, _scratch2);
            double fi = 0, fo = 0; int cnt = 0, ok = 0;
            var conf = flow.FullConfidence;
            for (var i = 0; i < n0; i++)
            {
                if (conf[i] < 0.9f || _structFull[i] >= 0.5f) continue;   // 질감 층(형태 윤곽 제외)만 계측
                fi += MathF.Abs(_hfIn[i] - _scratch2[i]); cnt++;
            }
            FlowPlane.WarpCubic(_hfOutPrev, _w, _h, flow.FullFlowX, flow.FullFlowY, _scratch2);
            for (var i = 0; i < n0; i++) { if (conf[i] >= 0.9f && _structFull[i] < 0.5f) fo += MathF.Abs(_hfOut[i] - _scratch2[i]); }
            FlowPlane.WarpCubic(_lumaOutPrev0, _w, _h, flow.FullFlowX, flow.FullFlowY, _scratch2);
            var drift = MeanOut - _prevMeanOut;
            for (var i = 0; i < n0; i++) { if (conf[i] >= 0.9f && _structFull[i] < 0.5f && MathF.Abs(_lumaOut0[i] - _scratch2[i] - drift) < ConsistencyLevel) ok++; }
            if (cnt > 0) { FlickerIn = (float)(fi / cnt); FlickerOut = (float)(fo / cnt); Consistency = ok / (float)cnt; }
        }
        _prevMeanOut = MeanOut;
        // 7) 다음 프레임 상태.
        _lumaIn1.AsSpan().CopyTo(_lumaPrevIn1);
        FlowPlane.Down(_lumaOut0, _w, _h, _tmp, _lumaOut1);
        FlowPlane.Smooth121(_lumaOut1, _w1, _h1, _tmp); FlowPlane.Smooth121(_lumaOut1, _w1, _h1, _tmp);
        _lumaOut0.AsSpan().CopyTo(_lumaOutPrev0);
        _hfIn.AsSpan().CopyTo(_hfInPrev); _hfOut.AsSpan().CopyTo(_hfOutPrev);
        for (var c = 0; c < 3; c++) _out[c].AsSpan().CopyTo(_prevOut[c]);
        _hasPrev = true;
        LastUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private float _prevMeanOut;

    public long InvalidatedPixels { get; private set; }

    private bool _rigValid;

    /// <summary>현재 흐름 → 강한 평활(반경 RigidRadius) 등속 예측 흐름.</summary>
    private void PredictRigid(SubPixelOpticalFlowTexturePinningEngine flow)
    {
        var n = _w1 * _h1;
        FlowPlane.BoxSum(flow.FlowX, _w1, _h1, RigidRadius, _tmp, _rigX);
        FlowPlane.BoxSum(flow.FlowY, _w1, _h1, RigidRadius, _tmp, _rigY);
        Array.Fill(_rigN, 1f);
        FlowPlane.BoxSum(_rigN, _w1, _h1, RigidRadius, _tmp, _rigW);
        for (var i = 0; i < n; i++) { _rigX[i] /= _rigW[i]; _rigY[i] /= _rigW[i]; }
        _rigValid = true;
    }

    /// <summary>롤백 가중 = 가드 마스크 × 흐름 신뢰 × 화면 내 출처(유입 가장자리 복제 줄무늬 방지).</summary>
    private void RollbackWeights(SubPixelOpticalFlowTexturePinningEngine flow, GenerativeMorphingSuppressionGuard guard)
    {
        // 롤백 흐름 = 등속 예측 강체 흐름(전해상도 ×2) — 조밀 흐름/신뢰도는 모핑 영역에서 환각을 따라가거나 0이 되므로 사용 안 함.
        FlowPlane.Up(_rigX, _w1, _h1, _rigFx); FlowPlane.Up(_rigY, _w1, _h1, _rigFy);
        for (var i = 0; i < _rigFx.Length; i++) { _rigFx[i] *= 2f; _rigFy[i] *= 2f; }
        ReadOnlySpan<float> mf = guard.MaskFull;
        for (var y = 0; y < _h; y++)
            for (var x = 0; x < _w; x++)
            {
                var i = y * _w + x; float sx = x + _rigFx[i], sy = y + _rigFy[i];
                _rbFull[i] = sx < 0 || sy < 0 || sx > _w - 1 || sy > _h - 1 ? 0f : mf[i];
            }
    }
    /// <summary>구조 윤곽 기준 기울기(저역 휘도 · 반해상도 px당) — 이 이상은 형태로 보고 피닝 제외.</summary>
    public float StructureGradient { get; set; } = 0.02f;
    public float StructureShare { get; private set; }
    public ReadOnlySpan<float> PinWeight => _wFull;

    /// <summary>피닝 가중 = 트랙 신뢰 × (1 − 구조): 저역 기울기(외곽 · 눈 · 밴드 경계)는 재생성 떨림이 아닌 형태이므로 현재 값 유지.</summary>
    private void Structure()
    {
        double acc = 0;
        for (var y = 0; y < _h1; y++)
            for (var x = 0; x < _w1; x++)
            {
                var i = y * _w1 + x;
                var gx = _lumaIn1[y * _w1 + Math.Min(_w1 - 1, x + 1)] - _lumaIn1[y * _w1 + Math.Max(0, x - 1)];
                var gy = _lumaIn1[Math.Min(_h1 - 1, y + 1) * _w1 + x] - _lumaIn1[Math.Max(0, y - 1) * _w1 + x];
                _tmp[i] = Math.Clamp(MathF.Sqrt(gx * gx + gy * gy) * 0.5f / StructureGradient - 0.5f, 0f, 1f);
            }
        // 3×3 최대(윤곽 양옆 보호) → 반해상도 구조 맵.
        for (var y = 0; y < _h1; y++)
            for (var x = 0; x < _w1; x++)
            {
                float m = 0;
                for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++) m = MathF.Max(m, _tmp[Math.Clamp(y + dy, 0, _h1 - 1) * _w1 + Math.Clamp(x + dx, 0, _w1 - 1)]);
                _struct1[y * _w1 + x] = m; acc += m;
            }
        StructureShare = (float)(acc / _struct1.Length);
        FlowPlane.Up(_struct1, _w1, _h1, _structFull);
    }

    private void Weights(SubPixelOpticalFlowTexturePinningEngine flow)
    {
        var tf = flow.TrackFull.Trust; var th = flow.TrackHalf.Trust;
        for (var i = 0; i < _wFull.Length; i++) _wFull[i] = tf[i] * (1f - _structFull[i]);
        for (var i = 0; i < _wHalf.Length; i++) _wHalf[i] = th[i] * (1f - _struct1[i]);
    }
    /// <summary>국소 디테일 에너지 비 허용치(재생성 대비 펌핑 ±30% → 에너지 ×3.4까지 정상).</summary>
    public float EnergyRatio { get; set; } = 6f;

    /// <summary>
    /// 가림 해제/텍스처 유입 판정: 앵커 휘도 디테일을 트랙 좌표로 끌어와 국소(5×5) 디테일 에너지를 현재 입력과 비교 —
    /// 위상 떨림과 무관(에너지)하게 다른 질감(배경 ↔ 레이스)이 붙은 트랙만 무효화.
    /// </summary>
    private void Occlusion(SubPixelOpticalFlowTexturePinningEngine flow)
    {
        flow.TrackFull.Fetch(_anchorHf, _pinHf);
        for (var i = 0; i < _pinHf.Length; i++) { _scratch2[i] = _hfIn[i] * _hfIn[i]; _l0[i] = _pinHf[i] * _pinHf[i]; }
        FlowPlane.BoxSum(_scratch2, _w, _h, 2, _tmp, _eIn);
        FlowPlane.BoxSum(_l0, _w, _h, 2, _tmp, _ePin);
        long bad = 0;
        for (var i = 0; i < _mismatch.Length; i++)
        {
            float a = _eIn[i] + 2e-3f, b = _ePin[i] + 2e-3f;
            var m = (a > EnergyRatio * b || b > EnergyRatio * a) && _structFull[i] < 0.5f;   // 형태 윤곽은 피닝 제외 대상 → 트랙 유지
            _mismatch[i] = (byte)(m ? 1 : 0);
            if (m) bad++;
        }
        InvalidatedPixels = bad;
        flow.TrackFull.Invalidate(_mismatch, _w, 1);
        flow.TrackHalf.Invalidate(_mismatch, _w, 2);
    }

    /// <summary>휘도 L0 대역(G0 − ↑↓G0).</summary>
    private void Detail(ReadOnlySpan<float> l, Span<float> hf)
    {
        FlowPlane.Down(l, _w, _h, _tmp, _g1);
        FlowPlane.Up(_g1, _w1, _h1, _u1o);
        for (var i = 0; i < hf.Length; i++) hf[i] = l[i] - _u1o[i];
    }
}
