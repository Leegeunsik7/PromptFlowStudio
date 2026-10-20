using System.Numerics;
using PromptFlowStudio.Services.Mocap;

namespace PromptFlowStudio.Services.MarkerlessMocap;

/// <summary>
/// [PromptFlow Studio v17.11.0] 🦴 기구학적 관절 가동 범위 &amp; 자가 관통 방지 가드(<see cref="Process"/> 무할당).
///   ① 가림/튐 게이트: 예측 p̂ = pₜ₋₁ + vₜ₋₁·dt 대비 편차 &gt; vmax·dt + 여유 이거나 삼각측량 실패 → 예측 유지(속도 감쇠) · 연속 HoldFrames 초과 시 재획득.
///   ② 떨림 억제: 관절별 One Euro 필터(fc = fmin + β|ẋ|) — 슬라이더 0..1 → fmin 8 Hz … 0.4 Hz.
///   ③ ROM 락: 3D → 전역 회전(최소 스윙) → 로컬 → v16.75 생체역학 한계(볼 = 스윙 원뿔 + 비틀림 · 힌지 = 무릎/팔꿈치 축 [−3°, 150°]) 클램프 →
///     실측 뼈 길이 스케일 FK 재구성(뼈 길이 불변).
///   ④ 자가 관통 차단: 몸통 캡슐(척추1–목 · r 0.12 s) vs 팔 세그먼트 3종 · 좌/우 허벅지·정강이 캡슐 상호 — 선분-선분 최근접 → 법선 방향 밀어내기 · 뼈 길이 재정규화(수렴까지 ≤ 6 패스).
/// </summary>
public sealed class KinematicJointLimitBonePenetrationGuard
{
    private const int J = MocapSkeleton.Body;
    public const float TorsoRadius = 0.12f, ArmRadius = 0.035f, LegRadius = 0.055f;

    private readonly Vector3[] _prev = new Vector3[J], _vel = new Vector3[J], _dHat = new Vector3[J];
    private readonly Vector3[] _in1 = new Vector3[J], _in2 = new Vector3[J], _o1 = new Vector3[J], _o2 = new Vector3[J];
    private readonly Vector3[] _filt = new Vector3[J], _out = new Vector3[J], _offsets = new Vector3[J];
    private readonly Quaternion[] _global = new Quaternion[J], _local = new Quaternion[J], _fkGlobal = new Quaternion[J];
    private readonly int[] _hold = new int[J];
    private readonly bool[] _heldNow = new bool[J], _clampedNow = new bool[J];
    private bool _init;
    private int _hist;

    public bool SpikeGate { get; set; } = true;
    public bool RomGuard { get; set; } = true;
    public bool PenetrationGuard { get; set; } = true;
    /// <summary>떨림 억제 강도 0..1.</summary>
    public float Smoothing { get; set; } = 0.5f;
    public float MaxSpeed { get; set; } = 8f;
    public int HoldFrames { get; set; } = 8;
    public float Beta { get; set; } = 3f;
    /// <summary>실측 골격 스케일(기준 1.70 m 대비 · EMA).</summary>
    public float Scale { get; private set; } = 1f;

    public ReadOnlySpan<Vector3> Output => _out;
    public Vector3 Out(int j) => _out[j];
    /// <summary>튐 게이트 + One Euro 후(ROM/관통 전) 위치.</summary>
    public Vector3 Filtered(int j) => _filt[j];
    public bool Held(int j) => _heldNow[j];
    public bool Clamped(int j) => _clampedNow[j];
    public int HeldJoints { get; private set; }
    public int RomClamped { get; private set; }
    public int PenetrationsFixed { get; private set; }
    public float MaxPenetrationMm { get; private set; }
    public float ResidualPenetrationMm { get; private set; }
    public float JitterInMm { get; private set; }
    public float JitterOutMm { get; private set; }
    public long SpikesRejected { get; private set; }
    public long RomClampTotal { get; private set; }
    public long PenetrationTotal { get; private set; }
    public double LastGuardUs { get; private set; }
    public float MinCutoffHz => 8f * MathF.Pow(0.05f, Math.Clamp(Smoothing, 0f, 1f));

    public void Reset()
    {
        _init = false; _hist = 0;
        Array.Clear(_vel); Array.Clear(_dHat); Array.Clear(_hold);
        SpikesRejected = RomClampTotal = PenetrationTotal = 0;
        Scale = 1f;
    }

    private static float Alpha(float fc, float dt)
    {
        var tau = 1f / (MathF.Tau * fc);
        return 1f / (1f + tau / dt);
    }

    /// <summary>원시 삼각측량 → 가드 출력(무할당).</summary>
    public void Process(ReadOnlySpan<Vector3> raw, ReadOnlySpan<bool> valid, float dt)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!_init)
        {
            var n = 0;
            for (var j = 0; j < J; j++) if (valid[j]) n++;
            if (n < J - 2) { LastGuardUs = 0; return; }
            for (var j = 0; j < J; j++) { _prev[j] = _filt[j] = _out[j] = _in1[j] = _in2[j] = _o1[j] = _o2[j] = raw[j]; _vel[j] = _dHat[j] = Vector3.Zero; _hold[j] = 0; }
            _init = true;
        }

        // ① 가림/튐 게이트 → ② One Euro
        var held = 0;
        var gate = MaxSpeed * dt + 0.04f;
        float fmin = MinCutoffHz, aD = Alpha(1f, dt);
        for (var j = 0; j < J; j++)
        {
            var pred = _prev[j] + _vel[j] * dt;
            var x = raw[j];
            var reject = !valid[j] || (SpikeGate && Vector3.Distance(x, pred) > gate);
            if (reject && _hold[j] < HoldFrames)
            {
                _hold[j]++; held++;
                if (valid[j]) SpikesRejected++;
                _heldNow[j] = true;
                _vel[j] *= 0.8f;
                x = pred;
            }
            else
            {
                if (!valid[j]) x = pred;
                _heldNow[j] = false;
                if (_hold[j] > 0) { _vel[j] = Vector3.Zero; _prev[j] = x; }   // 재획득: 예측 상태 리셋(속도 폭주 방지)
                _hold[j] = 0;
            }
            var dRaw = (x - _filt[j]) / dt;
            _dHat[j] = Vector3.Lerp(_dHat[j], dRaw, aD);
            var a = Alpha(fmin + Beta * _dHat[j].Length(), dt);
            var f = Vector3.Lerp(_filt[j], x, a);
            if (!_heldNow[j]) _vel[j] = Vector3.Lerp(_vel[j], (x - _prev[j]) / dt, 0.5f);
            _prev[j] = x;
            _filt[j] = f;
        }
        HeldJoints = held;

        // 실측 스케일(사지 뼈 길이 합 / 레스트 합)
        float meas = 0, rest = 0;
        for (var j = 1; j < J; j++)
        {
            meas += Vector3.Distance(_filt[j], _filt[MocapSkeleton.Parent[j]]);
            rest += MocapSkeleton.RestOffset[j].Length();
        }
        if (rest > 0 && held == 0) Scale += 0.05f * (meas / rest - Scale);

        // ③ ROM 락
        var clamped = 0;
        if (RomGuard)
        {
            AutoRiggingAndRetargetingSynthesizer.SolveGlobalRotations(_filt, _global);
            for (var j = 0; j < J; j++)
            {
                var p = MocapSkeleton.Parent[j];
                var l = p < 0 ? _global[j] : Quaternion.Normalize(Quaternion.Inverse(_global[p]) * _global[j]);
                var c = false;
                if (p >= 0) l = AutoRiggingAndRetargetingSynthesizer.ClampLocal(j, l, out c);
                _clampedNow[j] = c;
                if (c) clamped++;
                _local[j] = l;
                _offsets[j] = MocapSkeleton.RestOffset[j] * Scale;
            }
            MocapSkeleton.ForwardKinematics(_offsets, _local, _filt[MocapSkeleton.Pelvis], _out, _fkGlobal);
        }
        else
        {
            _filt.AsSpan().CopyTo(_out);
            Array.Clear(_clampedNow);
        }
        RomClamped = clamped;
        RomClampTotal += clamped;

        // ④ 자가 관통
        var depth0 = Penetration(_out);
        var fixedN = 0;
        if (PenetrationGuard && depth0 > 0)
        {
            for (var pass = 0; pass < 6; pass++) { var r = Resolve(_out); if (r == 0) break; fixedN += r; }
            PenetrationTotal += fixedN > 0 ? 1 : 0;
        }
        PenetrationsFixed = fixedN;
        MaxPenetrationMm = depth0 * 1000f;
        ResidualPenetrationMm = Penetration(_out) * 1000f;

        // 떨림(2차 차분 평균 · mm)
        if (_hist >= 2)
        {
            float ji = 0, jo = 0;
            for (var j = 0; j < J; j++)
            {
                ji += (raw[j] - 2 * _in1[j] + _in2[j]).Length();
                jo += (_out[j] - 2 * _o1[j] + _o2[j]).Length();
            }
            JitterInMm += 0.1f * (ji * 1000f / J - JitterInMm);
            JitterOutMm += 0.1f * (jo * 1000f / J - JitterOutMm);
        }
        _hist++;
        for (var j = 0; j < J; j++)
        {
            _in2[j] = _in1[j]; _in1[j] = valid[j] ? raw[j] : _in1[j];
            _o2[j] = _o1[j]; _o1[j] = _out[j];
        }
        LastGuardUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    // ── 캡슐 기하 ───────────────────────────────────────────

    /// <summary>선분-선분 최근접(제곱 거리 · s,t ∈ [0,1]).</summary>
    public static float SegmentSegmentClosest(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out float s, out float t)
    {
        Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
        float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
        if (a <= 1e-12f && e <= 1e-12f) { s = t = 0; return Vector3.Dot(r, r); }
        if (a <= 1e-12f) { s = 0; t = Math.Clamp(f / e, 0f, 1f); }
        else
        {
            var c = Vector3.Dot(d1, r);
            if (e <= 1e-12f) { t = 0; s = Math.Clamp(-c / a, 0f, 1f); }
            else
            {
                var b = Vector3.Dot(d1, d2);
                var den = a * e - b * b;
                s = den > 1e-12f ? Math.Clamp((b * f - c * e) / den, 0f, 1f) : 0f;
                t = (b * s + f) / e;
                if (t < 0) { t = 0; s = Math.Clamp(-c / a, 0f, 1f); }
                else if (t > 1) { t = 1; s = Math.Clamp((b - c) / a, 0f, 1f); }
            }
        }
        var d = p1 + d1 * s - (p2 + d2 * t);
        return Vector3.Dot(d, d);
    }

    // 검사 쌍: (근위, 원위, 상대 근위, 상대 원위, 반경 합, 종류 0 = 팔↔몸통 · 1 = 다리↔다리)
    private static readonly int[] Pairs =
    {
        MocapSkeleton.LShoulder, MocapSkeleton.LElbow, MocapSkeleton.LElbow, MocapSkeleton.LWrist, MocapSkeleton.LWrist, MocapSkeleton.LHand,
        MocapSkeleton.RShoulder, MocapSkeleton.RElbow, MocapSkeleton.RElbow, MocapSkeleton.RWrist, MocapSkeleton.RWrist, MocapSkeleton.RHand,
    };

    private float TorsoR => (TorsoRadius + ArmRadius) * Scale;
    private float LegR => 2f * LegRadius * Scale;

    /// <summary>최대 관통 깊이(m). 어깨→팔꿈치 근위 끝은 몸통 접합부이므로 원위 70% 만 검사.</summary>
    private float Penetration(Vector3[] p)
    {
        var max = 0f;
        Vector3 ta = p[MocapSkeleton.Spine1], tb = p[MocapSkeleton.Neck];
        for (var k = 0; k < Pairs.Length; k += 2)
        {
            var a = p[Pairs[k]];
            if (k % 6 == 0) a = Vector3.Lerp(a, p[Pairs[k + 1]], 0.3f);
            var d = MathF.Sqrt(SegmentSegmentClosest(a, p[Pairs[k + 1]], ta, tb, out _, out _));
            max = MathF.Max(max, TorsoR - d);
        }
        max = MathF.Max(max, LegR - MathF.Sqrt(SegmentSegmentClosest(p[MocapSkeleton.LKnee], p[MocapSkeleton.LAnkle], p[MocapSkeleton.RKnee], p[MocapSkeleton.RAnkle], out _, out _)));
        max = MathF.Max(max, LegR - MathF.Sqrt(SegmentSegmentClosest(Vector3.Lerp(p[MocapSkeleton.LHip], p[MocapSkeleton.LKnee], 0.3f), p[MocapSkeleton.LKnee], Vector3.Lerp(p[MocapSkeleton.RHip], p[MocapSkeleton.RKnee], 0.3f), p[MocapSkeleton.RKnee], out _, out _)));
        return MathF.Max(0f, max);
    }

    /// <summary>원위 관절과 그 자손을 평행 이동(무할당 · 체인 구조 기반).</summary>
    private static void Translate(Vector3[] p, int joint, Vector3 delta)
    {
        p[joint] += delta;
        for (var j = joint + 1; j < J; j++)
        {
            var q = MocapSkeleton.Parent[j];
            while (q > joint) q = MocapSkeleton.Parent[q];
            if (q == joint) p[j] += delta;
        }
    }

    /// <summary>뼈 길이 재정규화: child = parent + dir · len(자손 동반 이동).</summary>
    private static void Renormalize(Vector3[] p, int parent, int child, float len)
    {
        var d = p[child] - p[parent];
        var l = d.Length();
        if (l < 1e-6f) return;
        Translate(p, child, p[parent] + d * (len / l) - p[child]);
    }

    private int Resolve(Vector3[] p)
    {
        var n = 0;
        Vector3 ta = p[MocapSkeleton.Spine1], tb = p[MocapSkeleton.Neck];
        for (var k = 0; k < Pairs.Length; k += 2)
        {
            int pa = Pairs[k], pb = Pairs[k + 1];
            var a = p[pa];
            if (k % 6 == 0) a = Vector3.Lerp(a, p[pb], 0.3f);
            var d2 = SegmentSegmentClosest(a, p[pb], ta, tb, out var s, out var t);
            var d = MathF.Sqrt(d2);
            if (d >= TorsoR) continue;
            var axisPt = ta + (tb - ta) * t;
            var cp = a + (p[pb] - a) * s;
            var nrm = cp - axisPt;
            if (nrm.LengthSquared() < 1e-10f) nrm = new Vector3(pa == MocapSkeleton.LShoulder || pa == MocapSkeleton.LElbow || pa == MocapSkeleton.LWrist ? 1 : -1, 0, 0.3f);
            nrm = Vector3.Normalize(nrm);
            var len = Vector3.Distance(p[pa], p[pb]);
            Translate(p, pb, nrm * ((TorsoR - d + 0.002f) / MathF.Max(0.3f, s)));
            Renormalize(p, pa, pb, len);
            n++;
        }
        n += SeparateLegs(p, MocapSkeleton.LKnee, MocapSkeleton.LAnkle, MocapSkeleton.RKnee, MocapSkeleton.RAnkle, MocapSkeleton.LKnee, MocapSkeleton.RKnee);
        n += SeparateLegs(p, MocapSkeleton.LHip, MocapSkeleton.LKnee, MocapSkeleton.RHip, MocapSkeleton.RKnee, MocapSkeleton.LHip, MocapSkeleton.RHip);
        return n;
    }

    private int SeparateLegs(Vector3[] p, int la, int lb, int ra, int rb, int lRoot, int rRoot)
    {
        Vector3 a0 = la == lRoot && la == MocapSkeleton.LHip ? Vector3.Lerp(p[la], p[lb], 0.3f) : p[la];
        Vector3 b0 = ra == rRoot && ra == MocapSkeleton.RHip ? Vector3.Lerp(p[ra], p[rb], 0.3f) : p[ra];
        var d = MathF.Sqrt(SegmentSegmentClosest(a0, p[lb], b0, p[rb], out var s, out var t));
        if (d >= LegR) return 0;
        var sep = a0 + (p[lb] - a0) * s - (b0 + (p[rb] - b0) * t);
        // 좌측 다리 = +X(캐릭터 왼쪽) 기본 분리 방향
        var right = p[MocapSkeleton.LHip] - p[MocapSkeleton.RHip];
        if (sep.LengthSquared() < 1e-10f || Vector3.Dot(sep, right) < 0) sep = right;
        sep.Y = 0;
        if (sep.LengthSquared() < 1e-10f) sep = Vector3.UnitX;
        sep = Vector3.Normalize(sep);
        var push = (LegR - d + 0.002f) * 0.5f;
        float ll = Vector3.Distance(p[la], p[lb]), rl = Vector3.Distance(p[ra], p[rb]);
        Translate(p, lb, sep * push); Renormalize(p, la, lb, ll);
        Translate(p, rb, -sep * push); Renormalize(p, ra, rb, rl);
        return 1;
    }
}
