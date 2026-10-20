using System.Numerics;

namespace PromptFlowStudio.Services.DexterousManipulation;

/// <summary>
/// [PromptFlow Studio v17.36.0] 🧲 마찰 원뿔 슬립 이탈 &amp; 손가락 관통 방어 가드(<see cref="Process"/> 무할당).
///   ▸ 접촉 하중: 손 몫 하중 F(= 소유 비율 × m(a − g) + 외력 충격) ÷ 접촉 수 → 접촉별 법선 성분 fₙ = f·nᵢ(안쪽) · 접선 |fₜ| ·
///     쿨롱 조건 |fₜ| ≤ μ(N + fₙ) — N = 파지(압착) 법선력.
///   ▸ 파지력 증폭: 기저 N₀ = |정적 하중| ÷ (μ·접촉 수) × 1.05 · 필요 N* = |fₜ| ÷ (μ/1.3) − fₙ → 같은 프레임 즉시 상향(상한 60 N/접촉) · 반감기 0.35 s 기저 복귀(가드 OFF = N₀ 고정).
///   ▸ 미끄럼: 원뿔 초과분 Σ(|fₜ| − μ(N + fₙ))⁺ → 미끄럼 가속 ÷ m · 초과 소멸 시 정지 마찰 복귀 · 누적 변위 &gt; 3 cm = 이탈(낙하).
///   ▸ 관통 방어: 압착 굴곡(컴플라이언스 0.35°/N × 손가락 파지력)이 만드는 지골 관통 → 손가락별 후퇴각 가상 스프링-댐퍼(ω 60 rad/s · 임계 감쇠 · 4 서브스텝) ·
///     잔여 &gt; 패드 1 mm 시 이분 탐색 하드 투영 · 손가락 끝 스윕(전 프레임 → 현 프레임 6 서브스텝)이 강체 내부(SDF &lt; −반경)를 통과하면 터널링 차단.
/// </summary>
public sealed class FrictionConeSlipPreventionGuard
{
    public const int MaxSlots = RealtimeDexterousGraspSynthesisEngine.MaxSlots, MaxC = RealtimeDexterousGraspSynthesisEngine.MaxContacts;
    public const float G = 9.81f, Safety = 1.3f, MaxGrip = 60f, DecayHalfLife = 0.35f, Compliance = 0.35f, PadM = 0.001f, Omega = 60f, Lever = 0.05f, EjectDist = 0.03f;

    private readonly float[] _grip = new float[MaxSlots * MaxC], _ret = new float[MaxSlots * 5], _retV = new float[MaxSlots * 5], _fingerN = new float[MaxSlots * 5];
    private readonly float[] _slipD = new float[MaxSlots], _excess = new float[MaxSlots], _base = new float[MaxSlots];
    private readonly Vector3[] _slipV = new Vector3[MaxSlots], _slip = new Vector3[MaxSlots], _tipPrev = new Vector3[MaxSlots * 5];
    private readonly bool[] _tipInit = new bool[MaxSlots * 5], _slipping = new bool[MaxSlots];

    public bool SlipGuard { get; set; } = true;
    public bool PenetrationGuard { get; set; } = true;
    public float Grip(int slot, int contact) => _grip[slot * MaxC + contact];
    public float FingerForce(int slot, int finger) => _fingerN[slot * 5 + finger];
    public float BaseGrip(int slot) => _base[slot];
    public float SlipMm(int slot) => _slipD[slot] * 1000f;
    public Vector3 SlipWorld(int slot) => _slip[slot];
    public bool Slipping(int slot) => _slipping[slot];
    public bool Ejected(int slot) => _slipD[slot] > EjectDist;
    public float ExcessN(int slot) => _excess[slot];
    public float RetreatDeg(int slot, int finger) => _ret[slot * 5 + finger];
    public long SlipEvents { get; private set; }
    public long Amplifications { get; private set; }
    public float MaxGripN { get; private set; }
    public float MaxSlipMm { get; private set; }
    public float RawPenetrationMm { get; private set; }
    public float MaxRawPenetrationMm { get; private set; }
    public float ResidualPenetrationMm { get; private set; }
    public float MaxResidualPenetrationMm { get; private set; }
    public long HardProjections { get; private set; }
    public long TunnelsBlocked { get; private set; }
    public double LastUs { get; private set; }

    public void ResetSlot(int slot)
    {
        for (var i = 0; i < MaxC; i++) _grip[slot * MaxC + i] = 0f;
        for (var f = 0; f < 5; f++) { _ret[slot * 5 + f] = _retV[slot * 5 + f] = _fingerN[slot * 5 + f] = 0f; _tipInit[slot * 5 + f] = false; }
        _slipD[slot] = 0f; _slipV[slot] = _slip[slot] = Vector3.Zero; _slipping[slot] = false; _excess[slot] = 0f;
    }

    public void ResetMetrics()
    {
        SlipEvents = Amplifications = HardProjections = TunnelsBlocked = 0;
        MaxGripN = MaxSlipMm = MaxRawPenetrationMm = MaxResidualPenetrationMm = 0f;
    }

    /// <summary>
    /// 손 slot 의 파지력 · 미끄럼 · 관통 처리(합성 후 · <see cref="RealtimeDexterousGraspSynthesisEngine.Finalize"/> 전). load = 손이 소품에 가할 힘(월드 N) · staticLoad = 정지 시 하중 크기(N).
    /// </summary>
    public void Process(RealtimeDexterousGraspSynthesisEngine eng, int slot, DexProp prop, Vector3 load, float staticLoad, float dt)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var n = eng.ContactCount(slot);
        var hold = eng.CloseBlend(slot) > 0.98f && n > 0;
        if (!hold)
        {
            for (var f = 0; f < 5; f++) { eng.SetSqueeze(slot, f, 0f); _fingerN[slot * 5 + f] = 0f; }
            _slipV[slot] = Vector3.Zero; _slipping[slot] = false; _excess[slot] = 0f;
            Penetration(eng, slot, prop, dt);
            return;
        }
        var mu = prop.Friction;
        var n0 = staticLoad / (mu * n) * 1.05f;
        _base[slot] = n0;
        var decay = MathF.Exp(-dt * 0.6931472f / DecayHalfLife);
        var f1 = load / n;
        var exc = Vector3.Zero; var excN = 0f;
        for (var i = 0; i < n; i++)
        {
            var ni = -eng.ContactNormal(slot, i);
            var fn = Vector3.Dot(f1, ni);
            var ft = f1 - fn * ni;
            var tl = ft.Length();
            ref var g = ref _grip[slot * MaxC + i];
            if (SlipGuard)
            {
                var need = tl / (mu / Safety) - fn;
                var decayed = n0 + (MathF.Max(g, n0) - n0) * decay;
                var target = MathF.Min(MathF.Max(n0, need), MaxGrip);
                if (target > decayed + 0.01f) Amplifications++;
                g = MathF.Max(decayed, target);
            }
            else g = n0;
            if (g > MaxGripN) MaxGripN = g;
            var e = tl - mu * MathF.Max(0f, g + fn);
            if (e > 0f && tl > 1e-6f) { exc += ft / tl * e; excN += e; }
        }
        // 미끄럼(원뿔 초과분 → 가속 · 정지 마찰 복귀)
        _excess[slot] = excN;
        if (excN > 1e-4f)
        {
            if (!_slipping[slot]) SlipEvents++;
            _slipping[slot] = true;
            _slipV[slot] += exc / prop.Mass * dt;
        }
        else { _slipping[slot] = false; _slipV[slot] = Vector3.Zero; }
        _slip[slot] += _slipV[slot] * dt;
        _slipD[slot] = _slip[slot].Length();
        if (_slipD[slot] * 1000f > MaxSlipMm) MaxSlipMm = _slipD[slot] * 1000f;
        eng.SetSlipOffset(slot, -prop.DirToLocal(_slip[slot]));
        // 손가락 파지력 → 압착 굴곡
        for (var f = 0; f < 5; f++) _fingerN[slot * 5 + f] = 0f;
        for (var i = 0; i < n; i++) { var fi = eng.ContactFinger(slot, i); if (fi < 5) _fingerN[slot * 5 + fi] += _grip[slot * MaxC + i]; }
        for (var f = 0; f < 5; f++) eng.SetSqueeze(slot, f, _fingerN[slot * 5 + f] * Compliance);
        Penetration(eng, slot, prop, dt);
        LastUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private void Penetration(RealtimeDexterousGraspSynthesisEngine eng, int slot, DexProp prop, float dt)
    {
        var raw = 0f; var res = 0f;
        for (var f = 0; f < 5; f++)
        {
            var k = slot * 5 + f;
            // 원시(가드 없음) 관통
            eng.SetRetreat(slot, f, 0f);
            var d0 = eng.FingerPenetration(slot, prop, f);
            raw = MathF.Max(raw, d0);
            if (!PenetrationGuard) { _ret[k] = _retV[k] = 0f; res = MathF.Max(res, d0); _tipInit[k] = false; continue; }
            // 가상 스프링-댐퍼(후퇴각)
            var h = dt / 4f;
            for (var sub = 0; sub < 4; sub++)
            {
                eng.SetRetreat(slot, f, _ret[k]);
                var d = eng.FingerPenetration(slot, prop, f);
                var target = _ret[k] + d / Lever * 57.29578f;
                if (d <= 0f) target = MathF.Max(0f, _ret[k] - 0.5f);
                var a = Omega * Omega * (target - _ret[k]) - 2f * Omega * _retV[k];
                _retV[k] += a * h; _ret[k] = MathF.Max(0f, _ret[k] + _retV[k] * h);
            }
            eng.SetRetreat(slot, f, _ret[k]);
            var dr = eng.FingerPenetration(slot, prop, f);
            // 터널링: 손끝 스윕이 강체 내부 통과
            var tip = eng.TipLocal(slot, f);
            var tunnel = false;
            if (_tipInit[k])
            {
                var a0 = _tipPrev[k];
                for (var s = 1; s < 6; s++) if (prop.Sdf(a0 + (tip - a0) * (s / 6f)) < -RealtimeDexterousGraspSynthesisEngine.FingerRadius) { tunnel = true; break; }
            }
            if (dr > PadM || tunnel)
            {
                if (tunnel) TunnelsBlocked++;
                HardProjections++;
                float lo = _ret[k], hi = _ret[k] + 120f;
                for (var it = 0; it < 16; it++)
                {
                    var m = 0.5f * (lo + hi);
                    eng.SetRetreat(slot, f, m);
                    if (eng.FingerPenetration(slot, prop, f) > PadM * 0.5f) lo = m; else hi = m;
                }
                _ret[k] = hi; _retV[k] = 0f;
                eng.SetRetreat(slot, f, hi);
                dr = eng.FingerPenetration(slot, prop, f);
                tip = eng.TipLocal(slot, f);
            }
            _tipPrev[k] = tip; _tipInit[k] = true;
            res = MathF.Max(res, dr);
        }
        RawPenetrationMm = raw * 1000f; ResidualPenetrationMm = res * 1000f;
        if (RawPenetrationMm > MaxRawPenetrationMm) MaxRawPenetrationMm = RawPenetrationMm;
        if (ResidualPenetrationMm > MaxResidualPenetrationMm) MaxResidualPenetrationMm = ResidualPenetrationMm;
    }
}
