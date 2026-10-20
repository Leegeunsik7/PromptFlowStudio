using System.Numerics;
using PromptFlowStudio.Services.AnatomicalTissue;
using PromptFlowStudio.Services.StrandHair;

namespace PromptFlowStudio.Services.Garment;

/// <summary>
/// [PromptFlow Studio v17.16.0] 🛡️ 다층 의상 자가 교차 &amp; 신체·헤어 충돌 접촉 가드(무할당 · <see cref="IGarmentCollider"/>).
///   ▸ 피부 암시 표면: v17.14 근육 팽창 피부 링(중심 · 접선 · 둘레 기준 e0/e1 · 둘레 16 방사 반경) → 최근접 링 ± 2 구간 축에 투영(축 미끄럼 추종) ·
///     각도 보간 피부 반경 R(θ) → 반경 &lt; R + 레이어 여유이면 방사 밀어내기(근육 벌지 추종) + 접선 마찰.
///   ▸ 레이어 간 자가 교차: 같은 (튜브 · 링 · 둘레) 안쪽 레이어 입자 반경 + ½(두께 합) + 1 mm 이상 유지(바깥 레이어가 안쪽을 뚫지 않음) · 위반 계수 전/후
///     (팔꿈치/무릎 관절 링 ± 4 · 겨드랑이/어깨 몸통↔상완 피부 겹침 압착 구간은 잔여 계측에서 분리 집계).
///   ▸ 교차 튜브(우선순위 낮음 · 먼저 적용): 가슴 의상 ↔ 상완 피부 · 소매 ↔ 가슴 피부(+6 mm) · 코트 스커트 ↔ 양다리(팬츠 여유) —
///     튜브 AABB · 최근접 링 도달 반경 조기 기각 → 최근접 ± 2 링 구간 투영. 이후 자기 튜브 피부 여유 + 레이어 간 두께를 단일 투영으로 최종 보장.
///   ▸ 고정: 목/소매산/허리 링 = 피부 정점 + 법선 × 여유(서브스텝 보간 · 근육 팽창 추종).
///   ▸ 최대 거리 테더(Chaos/PhysX MaxDistance 방식): 전 입자의 피부 부착 목표(스커트 = 골반 링 외삽 A 라인)에서 레이어별 반경(6 / 20 / 45 mm · 스커트 +30 mm/링) 이내 유지.
///   ▸ 헤어(v17.15): 가이드 입자가 몸통/팔 최외곽 의상 표면 안이면 밖으로 투영 · 반작용으로 해당 의상 입자를 질량비(0.15)만큼 안쪽으로.
/// </summary>
public sealed class MultiLayerGarmentContactSelfIntersectionGuard : IGarmentCollider
{
    private const int Segs = NeuralAnatomicalMuscleFasciaEngine.Segs, Rings = NeuralAnatomicalMuscleFasciaEngine.Rings, Tubes = NeuralAnatomicalMuscleFasciaEngine.Tubes, RC = Tubes * Rings;
    public const float LayerGap = 0.001f, HairRadius = 0.0015f, HairMassRatio = 0.15f;

    private readonly Vector3[] _c = new Vector3[RC], _t = new Vector3[RC], _e0 = new Vector3[RC], _e1 = new Vector3[RC];
    private readonly float[] _rad = new float[RC * Segs];
    private readonly Vector3[] _bmin = new Vector3[Tubes], _bmax = new Vector3[Tubes];
    private readonly float[] _ringReach = new float[Tubes];
    private readonly int[] _jointRing = new int[Tubes];
    private Vector3[] _pinPrev = Array.Empty<Vector3>(), _pinCur = Array.Empty<Vector3>();

    public MultiLayerGarmentContactSelfIntersectionGuard(MultiLayerGarmentPbdDynamicsEngine e)
    {
        Engine = e;
        _pinPrev = new Vector3[e.P]; _pinCur = new Vector3[e.P];
        var sk = e.Skin;
        for (var t = 1; t < Tubes; t++)
        {
            var ch = NeuralAnatomicalMuscleFasciaEngine.Chains[t];
            var ja = Vector3.Distance(sk.RestJoint(ch[0]), sk.RestJoint(ch[1]));
            var best = 0; var bd = float.MaxValue;
            for (var i = 0; i < Rings; i++) { var d = MathF.Abs(sk.RingArc(t * Rings + i) - ja); if (d < bd) { bd = d; best = i; } }
            _jointRing[t] = best;
        }
        UpdateBody(true);
    }

    public MultiLayerGarmentPbdDynamicsEngine Engine { get; }
    public bool BodyCollision { get; set; } = true;
    public bool LayerGuard { get; set; } = true;
    public bool CrossTube { get; set; } = true;
    public bool HairContactEnabled { get; set; } = true;
    /// <summary>교차 튜브 피부 여유(m).</summary>
    public float CrossMargin { get; set; } = 0.006f;
    /// <summary>교차 튜브 호출당 최대 밀어내기(m · 폭발적 보정 차단 · 반복으로 수렴).</summary>
    public float MaxPush { get; set; } = 0.008f;
    /// <summary>안쪽 레이어 추종 상한(피부 여유 대비 m).</summary>
    public float LayerFollowMax { get; set; } = 0.025f;
    public bool Tether { get; set; } = true;
    /// <summary>레이어별 피부 부착 목표 대비 최대 거리(m · 이너 밀착 → 코트 여유).</summary>
    public float[] TetherMax { get; } = { 0.006f, 0.02f, 0.045f };
    public Vector3 Target(int i) => _pinCur[i];
    public float[] LayerThickness { get; } = { 0.0008f, 0.001f, 0.002f };
    public float[] LayerFriction { get; } = { 0.3f, 0.35f, 0.4f };
    public float BodyPenetrationBeforeMm { get; private set; }
    public float BodyPenetrationResidualMm { get; private set; }
    public int LayerViolationsBefore { get; private set; }
    public int LayerViolationsAfter { get; private set; }
    public int CreaseParticles { get; private set; }
    public int WorstParticle { get; private set; } = -1;
    public float LayerViolationMaxMm { get; private set; }
    public int CrossContacts { get; private set; }
    public int BodyContacts { get; private set; }
    public int HairInsideBefore { get; private set; }
    public int HairInsideAfter { get; private set; }
    public float HairMaxDepthMm { get; private set; }
    public double LastCollideUs { get; private set; }
    public double LastHairUs { get; private set; }

    // ── 피부 링 프레임 ──────────────────────────────────────

    /// <summary>근육 피부(v17.14) → 링 중심/접선/기준 축/방사 반경 · 튜브 AABB · 고정 목표(직전 ↔ 현재).</summary>
    public void UpdateBody(bool resetPins = false)
    {
        var sk = Engine.Skin;
        for (var t = 0; t < Tubes; t++) { _bmin[t] = new Vector3(float.MaxValue); _bmax[t] = new Vector3(float.MinValue); }
        for (var r = 0; r < RC; r++)
        {
            var c = sk.Center[r];
            int ia = r % Rings == Rings - 1 ? r - 1 : r, ib = ia + 1;
            var tg = sk.Center[ib] - sk.Center[ia];
            var tl = tg.Length();
            tg = tl > 1e-6f ? tg / tl : Vector3.UnitY;
            var v0 = sk.Position(r * Segs) - c; v0 -= tg * Vector3.Dot(v0, tg);
            var e0 = v0.LengthSquared() > 1e-10f ? Vector3.Normalize(v0) : Vector3.UnitZ;
            _c[r] = c; _t[r] = tg; _e0[r] = e0; _e1[r] = Vector3.Cross(tg, e0);
            var maxR = 0f;
            for (var s = 0; s < Segs; s++)
            {
                var q = sk.Position(r * Segs + s) - c; q -= tg * Vector3.Dot(q, tg);
                var l = q.Length();
                _rad[r * Segs + s] = l;
                if (l > maxR) maxR = l;
            }
            var tube = r / Rings;
            if (r % Rings == 0) _ringReach[tube] = 0f;
            var seg = Vector3.Distance(sk.Center[ia], sk.Center[ib]);
            _ringReach[tube] = MathF.Max(_ringReach[tube], MathF.Sqrt(maxR * maxR + seg * seg) + 0.06f);
            _bmin[tube] = Vector3.Min(_bmin[tube], c - new Vector3(maxR + 0.06f));
            _bmax[tube] = Vector3.Max(_bmax[tube], c + new Vector3(maxR + 0.06f));
        }
        var e = Engine;
        int r0 = 0, r1 = 1;
        var skirtT = sk.Center[r1] - sk.Center[r0];
        var spacing = skirtT.Length() * 1.1f;
        skirtT = spacing > 1e-6f ? skirtT / (spacing / 1.1f) : Vector3.UnitY;
        for (var i = 0; i < e.P; i++)
        {
            Vector3 target;
            if (e.RingOf[i] >= 0)
            {
                var v = e.SkinVertex[i];
                var c = sk.Center[v / Segs];
                var q = sk.Position(v) - c; q -= _t[v / Segs] * Vector3.Dot(q, _t[v / Segs]);
                var dir = q.LengthSquared() > 1e-10f ? Vector3.Normalize(q) : sk.Normal(v);
                target = sk.Position(v) + dir * e.LayerClearance[e.LayerOf[i]];
            }
            else
            {
                // 스커트: 골반 링 프레임 아래로 외삽(A 라인 플레어)
                int k = -e.RingOf[i], seg = e.SegOf[i];
                var v = NeuralAnatomicalMuscleFasciaEngine.Vertex(0, 0, seg);
                var q = sk.Position(v) - sk.Center[0]; q -= skirtT * Vector3.Dot(q, skirtT);
                var rl = q.Length();
                var dir = rl > 1e-6f ? q / rl : Vector3.UnitZ;
                target = sk.Center[0] - skirtT * (spacing * k) + dir * (rl + e.LayerClearance[e.LayerOf[i]] + 0.014f * k);
            }
            _pinPrev[i] = resetPins ? target : _pinCur[i];
            _pinCur[i] = target;
        }
    }

    public void Pin(MultiLayerGarmentPbdDynamicsEngine e, float alpha)
    {
        for (var i = 0; i < e.P; i++)
        {
            if (e.W[i] != 0f) continue;
            var p = Vector3.Lerp(_pinPrev[i], _pinCur[i], alpha);
            e.X[i] = e.PX[i] = p.X; e.Y[i] = e.PY[i] = p.Y; e.Z[i] = e.PZ[i] = p.Z;
        }
    }

    /// <summary>튜브 링 구간 [r0, r1] 축 투영 → 중심 · 방사 방향 · 반경 · 피부 반경(각도 보간). 축 범위 밖이면 false.</summary>
    public bool Query(int tube, int r0, int r1, Vector3 x, out Vector3 center, out Vector3 dir, out float radial, out float skinR)
    {
        center = x; dir = Vector3.UnitX; radial = 0f; skinR = 0f;
        r0 = Math.Max(0, r0); r1 = Math.Min(Rings - 1, r1);
        var best = -1; var bt = 0f; var bd = float.MaxValue;
        for (var i = r0; i < r1; i++)
        {
            int a = tube * Rings + i, b = a + 1;
            var ab = _c[b] - _c[a];
            var t = Math.Clamp(Vector3.Dot(x - _c[a], ab) / MathF.Max(1e-10f, ab.LengthSquared()), 0f, 1f);
            var d = Vector3.DistanceSquared(x, _c[a] + ab * t);
            if (d < bd) { bd = d; best = i; bt = t; }
        }
        if (best < 0 || (best == r0 && bt <= 0f && r0 == 0) || (best == r1 - 1 && bt >= 1f && r1 == Rings - 1)) return false;
        int ia = tube * Rings + best, ib = ia + 1;
        center = Vector3.Lerp(_c[ia], _c[ib], bt);
        var tg = Vector3.Normalize(Vector3.Lerp(_t[ia], _t[ib], bt));
        var q = x - center;
        var axial = tg * Vector3.Dot(q, tg);
        var perp = q - axial;
        center += axial;
        radial = perp.Length();
        dir = radial > 1e-9f ? perp / radial : Vector3.Normalize(Vector3.Lerp(_e0[ia], _e0[ib], bt));
        var e0 = Vector3.Lerp(_e0[ia], _e0[ib], bt); var e1 = Vector3.Lerp(_e1[ia], _e1[ib], bt);
        var th = MathF.Atan2(Vector3.Dot(dir, e1), Vector3.Dot(dir, e0));
        if (th < 0) th += MathF.Tau;
        var f = th / MathF.Tau * Segs;
        var s0 = (int)f % Segs; var s1 = (s0 + 1) % Segs; var fs = f - (int)f;
        float ra = _rad[ia * Segs + s0] + (_rad[ia * Segs + s1] - _rad[ia * Segs + s0]) * fs, rb = _rad[ib * Segs + s0] + (_rad[ib * Segs + s1] - _rad[ib * Segs + s0]) * fs;
        skinR = ra + (rb - ra) * bt;
        return true;
    }

    /// <summary>최근접 링(구간 [lo, hi] 중심 거리) → 그 ± 2 구간 질의(축 미끄럼에도 올바른 링 프레임).</summary>
    public bool QueryNear(int tube, int lo, int hi, Vector3 x, out Vector3 center, out Vector3 dir, out float radial, out float skinR)
    {
        var best = lo; var bd = float.MaxValue;
        for (var r = lo; r <= hi; r++) { var d = Vector3.DistanceSquared(x, _c[tube * Rings + r]); if (d < bd) { bd = d; best = r; } }
        return Query(tube, Math.Max(lo, best - 2), Math.Min(hi, best + 2), x, out center, out dir, out radial, out skinR);
    }

    private bool InBox(int tube, Vector3 x) => x.X >= _bmin[tube].X && x.Y >= _bmin[tube].Y && x.Z >= _bmin[tube].Z && x.X <= _bmax[tube].X && x.Y <= _bmax[tube].Y && x.Z <= _bmax[tube].Z;

    /// <summary>바깥 레이어 여유(해당 튜브 링에 존재하는 최외곽 의상 기준).</summary>
    public float OuterClearance(int tube, int ring)
    {
        var e = Engine;
        for (var l = MultiLayerGarmentPbdDynamicsEngine.Layers - 1; l >= 0; l--)
            if (e.Map(l, tube, ring, 0) >= 0) return e.LayerClearance[l] + LayerThickness[l] * 0.5f;
        return 0f;
    }

    /// <summary>교차 튜브 밀어내기: 최근접 링 ± 2 구간만 · 링 경계 구 조기 기각 · 피부 + 여유.</summary>
    private bool CrossPush(ref Vector3 x, int tube, int ringLo, int ringHi, float clearance)
    {
        if (!InBox(tube, x)) return false;
        var best = -1; var bd = float.MaxValue;
        for (var r = ringLo; r <= ringHi; r++) { var d = Vector3.DistanceSquared(x, _c[tube * Rings + r]); if (d < bd) { bd = d; best = r; } }
        if (best < 0 || bd > _ringReach[tube] * _ringReach[tube]) return false;
        if (!Query(tube, Math.Max(ringLo, best - 2), Math.Min(ringHi, best + 2), x, out var c, out var dir, out var radial, out var skinR)) return false;
        var req = skinR + clearance;
        if (radial >= req) return false;
        x = c + dir * MathF.Min(req, radial + MaxPush);
        return true;
    }

    public void Collide(MultiLayerGarmentPbdDynamicsEngine e, int iteration, int substep)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var measure = iteration == 0;
        if (iteration == 0 && substep == 0) { BodyPenetrationBeforeMm = 0; LayerViolationsBefore = 0; LastCollideUs = 0; }
        var depth = BodyPenetrationBeforeMm * 0.001f;
        int viol = 0, cross = 0, contacts = 0;
        for (var i = 0; i < e.P; i++)
        {
            if (e.W[i] == 0f) continue;
            int l = e.LayerOf[i], tube = e.TubeOf[i], ring = e.RingOf[i];
            var x = new Vector3(e.X[i], e.Y[i], e.Z[i]);
            var x0 = x;
            var clear = e.LayerClearance[l] + LayerThickness[l] * 0.5f;
            // ⓪ 최대 거리 테더(피부 부착 목표 · 느슨함 = 레이어/스커트 링) — 빠른 사지 운동 추종 · 깊은 이탈 차단
            if (Tether)
            {
                var tg = Vector3.Lerp(_pinPrev[i], _pinCur[i], (substep + 1f) / e.Substeps);
                var md = ring >= 0 ? TetherMax[l] : TetherMax[l] + 0.03f * -ring;
                var dv = x - tg; var dl = dv.Length();
                if (dl > md) x = tg + dv * (md / dl);
            }
            // ① 교차 튜브(피부 + 소폭 여유 · 우선순위 낮음)
            if (CrossTube)
            {
                var tt = (TissueTube)tube;
                var cm = CrossMargin + LayerThickness[l];
                if (tt == TissueTube.Torso)
                {
                    if (ring >= 12) { if (CrossPush(ref x, 1, 2, 12, cm)) cross++; if (CrossPush(ref x, 2, 2, 12, cm)) cross++; }
                    else { if (CrossPush(ref x, 3, 0, Rings - 1, e.LayerClearance[1] + cm)) cross++; if (CrossPush(ref x, 4, 0, Rings - 1, e.LayerClearance[1] + cm)) cross++; }
                }
                else if ((tt is TissueTube.ArmL or TissueTube.ArmR) && ring >= 2) { if (CrossPush(ref x, 0, 12, Rings - 1, cm)) cross++; }
            }
            // ② 자기 튜브: 피부 여유 + 레이어 간 두께(최우선 · 단일 투영)
            var hit = false;
            if (ring >= 0 && (BodyCollision || LayerGuard) && QueryNear(tube, 0, Rings - 1, x, out var c, out var dir, out var radial, out var skinR))
            {
                var req = BodyCollision ? skinR + clear : 0f;
                if (BodyCollision && measure && req - radial > depth) depth = req - radial;
                if (LayerGuard)
                    for (var li = l - 1; li >= 0; li--)
                    {
                        var ip = e.Map(li, tube, ring, e.SegOf[i]);
                        if (ip < 0) continue;
                        var rl = Vector3.Dot(new Vector3(e.X[ip], e.Y[ip], e.Z[ip]) - c, dir) + 0.5f * (LayerThickness[li] + LayerThickness[l]) + LayerGap;
                        rl = MathF.Min(rl, skinR + clear + LayerFollowMax);   // 안쪽 레이어 이탈 연쇄 증폭 차단
                        if (radial < rl && measure && substep == 0) viol++;
                        if (rl > req) req = rl;
                        break;
                    }
                if (radial < req)
                {
                    x = c + dir * req;
                    hit = true;
                    // 접선 마찰(표면 법선 = 방사 방향) → 재투영(자기 튜브 = 최종 권위 · 상한 없음)
                    var p = new Vector3(e.PX[i], e.PY[i], e.PZ[i]);
                    var mv = x - p;
                    x -= (mv - dir * Vector3.Dot(mv, dir)) * LayerFriction[l];
                    if (QueryNear(tube, 0, Rings - 1, x, out var c2, out var d2, out var r2, out var s2) && r2 < s2 + clear) x = c2 + d2 * MathF.Max(s2 + clear, r2);
                }
            }
            if (x == x0) continue;
            if (hit) contacts++;
            e.X[i] = x.X; e.Y[i] = x.Y; e.Z[i] = x.Z;
        }
        BodyPenetrationBeforeMm = depth * 1000f;
        if (measure && substep == 0) LayerViolationsBefore = viol;
        if (iteration == e.Iterations - 1) { CrossContacts = cross; BodyContacts = contacts; }
        LastCollideUs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>팔꿈치/무릎 주름 영역(관절 링 ± 4) — 굴곡 시 피부끼리 맞닿는 압착 구간.</summary>
    public bool InCrease(int tube, int ring) => tube > 0 && ring >= 0 && Math.Abs(ring - _jointRing[tube]) <= 4;
    public int JointRing(int tube) => _jointRing[tube];

    /// <summary>다른 튜브(몸통 ↔ 상완) 또는 같은 사지의 반대편 분절 피부 + 여유 안 = 겨드랑이/어깨 · 굴곡 접힘 압착 구간.</summary>
    private bool Pinched(int tube, int ring, Vector3 x)
    {
        if (tube == 0 && ring >= 12) return Inside(1, 2, 12, x) || Inside(2, 2, 12, x);
        if (tube is 1 or 2 && Inside(0, 12, Rings - 1, x)) return true;
        if (tube > 0)
        {
            // 굴곡 접힘: 상완 ↔ 전완(대퇴 ↔ 하퇴) 반대편 분절 피부 안
            var jr = _jointRing[tube];
            return ring < jr ? Inside(tube, jr + 1, Rings - 1, x) : Inside(tube, 0, jr - 1, x);
        }
        return false;
    }

    private bool Inside(int tube, int lo, int hi, Vector3 x)
    {
        if (!InBox(tube, x)) return false;
        return QueryNear(tube, lo, hi, x, out _, out _, out var radial, out var skinR) && radial < skinR + CrossMargin + 0.004f;
    }

    /// <summary>스텝 후 잔여: 피부 관통 최대 깊이 · 레이어 위반 수/최대(주름 압착 구간은 별도 집계).</summary>
    public void MeasureResidual()
    {
        var e = Engine;
        float res = 0, lv = 0; int viol = 0, crease = 0;
        for (var i = 0; i < e.P; i++)
        {
            int l = e.LayerOf[i], tube = e.TubeOf[i], ring = e.RingOf[i];
            if (ring < 0 || e.W[i] == 0f) continue;
            var x = e.Pos(i);
            if (InCrease(tube, ring) || Pinched(tube, ring, x)) { crease++; continue; }
            if (!QueryNear(tube, 0, Rings - 1, x, out var c, out var dir, out var radial, out var skinR)) continue;
            var d = skinR + e.LayerClearance[l] * 0.5f - radial;   // 피부 실관통(여유 절반 이상 침범)
            if (d > res) { res = d; WorstParticle = i; }
            for (var li = l - 1; li >= 0; li--)
            {
                var ip = e.Map(li, tube, ring, e.SegOf[i]);
                if (ip < 0) continue;
                var ri = Vector3.Dot(e.Pos(ip) - c, dir);
                var v = ri + 0.5f * (LayerThickness[li] + LayerThickness[l]) - radial;
                if (v > 1e-4f) { viol++; if (v > lv) lv = v; }
                break;
            }
        }
        BodyPenetrationResidualMm = res * 1000f;
        CreaseParticles = crease;
        LayerViolationsAfter = viol; LayerViolationMaxMm = lv * 1000f;
    }

    // ── 헤어 상호 충돌 ──────────────────────────────────────

    /// <summary>v17.15 가이드 입자 ↔ 몸통/팔 최외곽 의상 표면(양방향 · 무할당): 투영 패스 → 검증 패스.</summary>
    public void HairContact(StrandHairPbdDynamicsEngine hair)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        HairInsideBefore = HairPass(hair, HairContactEnabled, out var maxD);
        HairMaxDepthMm = maxD * 1000f;
        HairInsideAfter = HairPass(hair, false, out _);
        LastHairUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private int HairPass(StrandHairPbdDynamicsEngine hair, bool push, out float maxD)
    {
        var e = Engine;
        int G = hair.G, N = StrandHairPbdDynamicsEngine.N;
        var count = 0; maxD = 0f;
        for (var k = 2 * G; k < N * G; k++)
        {
            var x = new Vector3(hair.X[k], hair.Y[k], hair.Z[k]);
            var inside = false;
            for (var tube = 0; tube < 3; tube++)
            {
                if (!InBox(tube, x)) continue;
                if (!Query(tube, tube == 0 ? 6 : 0, Rings - 1, x, out var c, out var dir, out var radial, out var skinR)) continue;
                var ring = NearestRing(tube, x);
                int rr = tube * Rings + ring;
                var th = MathF.Atan2(Vector3.Dot(dir, _e1[rr]), Vector3.Dot(dir, _e0[rr]));
                if (th < 0) th += MathF.Tau;
                var seg = (int)MathF.Round(th / MathF.Tau * Segs) % Segs;
                var outer = -1;
                for (var l = MultiLayerGarmentPbdDynamicsEngine.Layers - 1; l >= 0 && outer < 0; l--) outer = e.Map(l, tube, ring, seg);
                if (outer < 0) continue;
                var surface = MathF.Max(skinR + e.LayerClearance[e.LayerOf[outer]], Vector3.Dot(e.Pos(outer) - c, dir)) + LayerThickness[e.LayerOf[outer]] * 0.5f + HairRadius;
                if (radial >= surface - 1e-5f) continue;
                inside = true;
                var d = surface - radial;
                if (d > maxD) maxD = d;
                if (!push) continue;
                x = c + dir * (surface + 1e-5f);
                if (e.W[outer] > 0f)
                {
                    var p = dir * (-d * HairMassRatio);
                    e.X[outer] += p.X; e.Y[outer] += p.Y; e.Z[outer] += p.Z;
                }
            }
            if (inside) count++;
            if (push) { hair.X[k] = x.X; hair.Y[k] = x.Y; hair.Z[k] = x.Z; }
        }
        return count;
    }

    private int NearestRing(int tube, Vector3 x)
    {
        var best = 0; var bd = float.MaxValue;
        for (var i = 0; i < Rings; i++) { var d = Vector3.DistanceSquared(x, _c[tube * Rings + i]); if (d < bd) { bd = d; best = i; } }
        return best;
    }
}
