using System.Numerics;
namespace PromptFlowStudio.Services.GaussianClothHair;

/// <summary>
/// [PromptFlow Studio v17.59.0] 🛡 신장 변형률 한계 클램프 &amp; 초고속 침투 방어 가드(<see cref="Apply"/> 무할당).
/// ① 비유한 입자 → 직전 안전 위치 복구 ② 신장률 클램프: 헤어 = 뿌리→끝 FTL(부모 확정 · 자식만 이동 → 정확히 ≤ 110 %) · 직물 구조 = 역질량 분배 GS 수렴 반복(≤ 110 %)
/// ③ 스윕 구 CCD: 직전 안전 위치 → 현재 위치 · 캡슐 이전 → 현재 끝점 선형 보간(상대 이동량 / 0.4r 하위 단계 · 상대 이동 &lt; r 생략) 첫 진입 시각의 진입 법선 · 축 매개변수 기록 →
/// 끝 상태가 반대편(법선 내적 &lt; 0)이면 터널링 → 진입 쪽 캡슐 표면으로 복귀 + 캡슐 표면 법선 속도만 전달(접선 끌림 없음) · 클램프 고정점 ④ 이산 밀어내기 · 클램프 · 터널 재배치 교대 마감(보정 변위의 25 % 만 속도화) ⑤ 잔여 계측(과신장 · 터널 반대편 · 캡슐 내부 침투) → 안전 위치 갱신.
/// </summary>
public sealed class StrainLimitPenetrationGuard
{
    public const float MaxStrain = 1.10f, StrainTol = 1e-3f, PenTol = 0.001f, ContactBand = 0.002f, VelocityShare = 0.25f;
    public const int ClampPasses = 24, FinishRounds = 8, MaxSub = 24;
    private const int P = RealtimeGaussianClothHairPhysicsEngine.Particles;
    private readonly float[] _sx = new float[P], _sy = new float[P], _sz = new float[P], _gx = new float[P], _gy = new float[P], _gz = new float[P];
    private readonly int[] _tunI = new int[P], _tunC = new int[P];
    private readonly Vector3[] _tunN = new Vector3[P];
    private readonly byte[] _lock = new byte[P], _contact = new byte[P];
    private bool _hasSafe;
    private int _tunN0;

    public bool StrainEnabled { get; set; } = true;
    public bool CcdEnabled { get; set; } = true;
    public int FrameOverStretch { get; private set; }
    public long OverStretchFixes { get; private set; }
    public float FramePeakStrain { get; private set; } = 1f;
    public float PeakStrain { get; private set; } = 1f;
    public float FrameMaxStrain { get; private set; } = 1f;
    public int StrainResidual { get; private set; }
    /// <summary>충돌 우선 구간(접촉 · 터널 복귀 입자 포함 구속)의 110 % 초과 수 — 관통 0 을 우선해 신장 허용.</summary>
    public int ContactStretch { get; private set; }
    public int FrameTunnels { get; private set; }
    public long Tunnels { get; private set; }
    public int TunnelResidual { get; private set; }
    public int Penetrations { get; private set; }
    public long NanFixes { get; private set; }
    public int CcdSweeps { get; private set; }
    public double LastGuardUs { get; private set; }
    public int Defects => StrainResidual + TunnelResidual + Penetrations;
    public float PenetrationRate => Penetrations / (float)P;
    public int TunnelParticle(int k) => _tunI[k];
    public bool Locked(int i) => _lock[i] != 0;
    public int TunnelCapsule(int k) => _tunC[k];

    public void ResetMetrics() { OverStretchFixes = 0; Tunnels = 0; NanFixes = 0; PeakStrain = 1f; }

    /// <summary>안전 위치 무효화(배치/리셋 직후 첫 프레임은 CCD 생략).</summary>
    public void Reset() => _hasSafe = false;

    public void Apply(RealtimeGaussianClothHairPhysicsEngine e, AerodynamicWindBodyCollisionSynthesizer body)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        RecoverNonFinite(e);
        for (var i = 0; i < P; i++) { _gx[i] = e.X[i]; _gy[i] = e.Y[i]; _gz[i] = e.Z[i]; }
        FramePeakStrain = MeasureStrain(e, out var over);
        FrameOverStretch = over;
        if (FramePeakStrain > PeakStrain) PeakStrain = FramePeakStrain;
        if (StrainEnabled && over > 0) { ClampStrain(e); OverStretchFixes += over; }
        _tunN0 = 0;
        Array.Clear(_lock);
        if (_hasSafe) Sweep(e, body);
        FrameTunnels = _tunN0; Tunnels += _tunN0;
        if (StrainEnabled || CcdEnabled)
            for (var r = 0; r < FinishRounds; r++)
            {
                var moved = CcdEnabled ? Push(e, body) : 0;
                var changed = StrainEnabled && ClampStrain(e);
                if (CcdEnabled) Refix(e, body);
                if (moved == 0 && !changed) break;
            }
        if (CcdEnabled) Push(e, body);
        // 가드 보정 변위의 25 % 만 속도로(이전 위치 동반 이동) — 대변위 클램프/밀어내기의 에너지 주입 억제(CCD 복귀 입자는 자체 속도 지정)
        for (var i = 0; i < P; i++)
        {
            if (_lock[i] != 0 || e.W[i] <= 0f) continue;
            float dx = e.X[i] - _gx[i], dy = e.Y[i] - _gy[i], dz = e.Z[i] - _gz[i];
            if (dx == 0f && dy == 0f && dz == 0f) continue;
            const float K = 1f - VelocityShare;
            e.PX[i] += dx * K; e.PY[i] += dy * K; e.PZ[i] += dz * K;
        }
        // 잔여 계측(충돌 우선: 신체 접촉/터널 복귀 입자 구속은 접촉 신장으로 분리)
        Penetrations = CountPenetrations(e, body);
        FrameMaxStrain = MeasureResidual(e, out var res, out var cst);
        StrainResidual = res; ContactStretch = cst;
        var tr = 0;
        for (var k = 0; k < _tunN0; k++)
        {
            var i = _tunI[k]; var c = _tunC[k];
            var x = e.Position(i);
            var q = RealtimeGaussianClothHairPhysicsEngine.ClosestOnSeg(body.CapsuleA(c), body.CapsuleB(c), x, out _);
            if (!CcdEnabled || Vector3.Dot(x - q, _tunN[k]) < 0f) tr++;
        }
        TunnelResidual = tr;
        for (var i = 0; i < P; i++) { _sx[i] = e.X[i]; _sy[i] = e.Y[i]; _sz[i] = e.Z[i]; }
        _hasSafe = true;
        LastGuardUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private void RecoverNonFinite(RealtimeGaussianClothHairPhysicsEngine e)
    {
        for (var i = 0; i < P; i++)
        {
            if (float.IsFinite(e.X[i] + e.Y[i] + e.Z[i] + e.PX[i] + e.PY[i] + e.PZ[i])) continue;
            var s = _hasSafe ? new Vector3(_sx[i], _sy[i], _sz[i]) : Vector3.Zero;
            e.SetPosition(i, s); e.SetPrevious(i, s);
            NanFixes++;
        }
    }

    /// <summary>구조/헤어 신장 구속 최대 신장률 · 한계 초과 수.</summary>
    public static float MeasureStrain(RealtimeGaussianClothHairPhysicsEngine e, out int over)
    {
        over = 0; var peak = 1f;
        for (var pass = 0; pass < 2; pass++)
        {
            int lo = pass == 0 ? 0 : e.HairStretchStart, hi = pass == 0 ? e.StructuralCount : e.HairStretchEnd;
            for (var c = lo; c < hi; c++)
            {
                var r = Vector3.Distance(e.Position(e.ConstraintA(c)), e.Position(e.ConstraintB(c))) / e.RestLength(c);
                if (r > peak) peak = r;
                if (r > MaxStrain * (1f + StrainTol)) over++;
            }
        }
        return peak;
    }

    /// <summary>신장 클램프 GS — 사슬(헤어 · 직물 세로 위 → 아래)은 부모 확정 FTL · 가로는 역질량 분배 · 터널 복귀 입자는 고정점(이웃이 끌려옴).</summary>
    private bool ClampStrain(RealtimeGaussianClothHairPhysicsEngine e)
    {
        for (var pass = 0; pass < ClampPasses; pass++)
        {
            var worst = MathF.Max(ClampRange(e, e.HairStretchStart, e.HairStretchEnd, true), ClampRange(e, 0, e.StructuralCount, false));
            if (worst <= MaxStrain * (1f + StrainTol * 0.5f)) return pass > 0 || worst > MaxStrain;
        }
        return true;
    }

    private float ClampRange(RealtimeGaussianClothHairPhysicsEngine e, int lo, int hi, bool chain)
    {
        const int U = RealtimeGaussianClothHairPhysicsEngine.SkirtU;
        var w = e.W; var worst = 1f;
        for (var c = lo; c < hi; c++)
        {
            int a = e.ConstraintA(c), b = e.ConstraintB(c);
            float wa = _lock[a] != 0 ? 0f : w[a], wb = _lock[b] != 0 ? 0f : w[b];
            if ((chain || b - a == U) && wb > 0f) wa = 0f;
            var ws = wa + wb;
            if (ws <= 0f) continue;
            Vector3 pa = e.Position(a), pb = e.Position(b), d = pb - pa;
            var l = d.Length(); var lim = e.RestLength(c) * MaxStrain;
            if (l <= lim) continue;
            worst = MathF.Max(worst, l / e.RestLength(c));
            var corr = d * ((l - lim) / (l * ws));
            if (wa > 0f) e.SetPosition(a, pa + corr * wa);
            e.SetPosition(b, pb - corr * wb);
        }
        return worst;
    }

    private void Sweep(RealtimeGaussianClothHairPhysicsEngine e, AerodynamicWindBodyCollisionSynthesizer body)
    {
        var sweeps = 0;
        for (var i = 0; i < P; i++)
        {
            if (e.W[i] <= 0f) continue;
            var p0 = new Vector3(_sx[i], _sy[i], _sz[i]);
            var p1 = e.Position(i);
            var dp = p1 - p0; var lp = dp.Length();
            var th = e.Thickness(i);
            var recorded = false;
            for (var c = 0; c < AerodynamicWindBodyCollisionSynthesizer.Capsules; c++)
            {
                Vector3 A0 = body.PrevCapsuleA(c), B0 = body.PrevCapsuleB(c), A1 = body.CapsuleA(c), B1 = body.CapsuleB(c);
                if (!AerodynamicWindBodyCollisionSynthesizer.Collides(i, c)) continue;
                var r = body.Radius(c);
                var rel = lp + MathF.Max(Vector3.Distance(A0, A1), Vector3.Distance(B0, B1));
                if (rel < r) continue;
                // 광역: 두 시점 경계 구 · 입자 경로 경계 구
                var c0 = (A0 + B0) * 0.5f; var c1 = (A1 + B1) * 0.5f; var br = body.BoundRadius(c) + th;
                var mc = (c0 + c1) * 0.5f; var mp = (p0 + p1) * 0.5f;
                var reach = br + Vector3.Distance(c0, c1) * 0.5f + lp * 0.5f;
                if (Vector3.DistanceSquared(mc, mp) > reach * reach) continue;
                var q0 = RealtimeGaussianClothHairPhysicsEngine.ClosestOnSeg(A0, B0, p0, out var tPrev);
                var nPrev = p0 - q0; var d0 = nPrev.Length();
                if (d0 < r) continue;
                nPrev /= d0;
                sweeps++;
                var steps = Math.Clamp((int)MathF.Ceiling(rel / (0.4f * r)), 2, MaxSub);
                var hit = false;
                for (var s = 1; s <= steps; s++)
                {
                    var tau = s / (float)steps;
                    var A = Vector3.Lerp(A0, A1, tau); var B = Vector3.Lerp(B0, B1, tau); var x = Vector3.Lerp(p0, p1, tau);
                    var q = RealtimeGaussianClothHairPhysicsEngine.ClosestOnSeg(A, B, x, out var tq);
                    var dv = x - q; var d = dv.Length();
                    if (d < r) { hit = true; break; }
                    nPrev = dv / d; tPrev = tq;
                }
                if (!hit) continue;
                var qe = RealtimeGaussianClothHairPhysicsEngine.ClosestOnSeg(A1, B1, p1, out _);
                var de = p1 - qe; var dl = de.Length();
                if (dl > 1e-7f && Vector3.Dot(de / dl, nPrev) >= 0f) continue;   // 같은 쪽 = 정상 접촉(이산 투영이 처리)
                // 터널링: 진입 법선(현재 축 직교 투영) 쪽 표면으로 복귀
                var ax = B1 - A1; var al = ax.Length();
                var ne = nPrev;
                if (al > 1e-6f && tPrev > 0f && tPrev < 1f) { ax /= al; ne -= ax * Vector3.Dot(ne, ax); var nl = ne.Length(); ne = nl > 1e-6f ? ne / nl : nPrev; }
                if (!recorded) { _tunI[_tunN0] = i; _tunC[_tunN0] = c; _tunN[_tunN0] = ne; _tunN0++; recorded = true; }
                if (!CcdEnabled) continue;
                var fix = A1 + (B1 - A1) * tPrev + ne * (r + th);
                var dc = (A1 - A0) * (1f - tPrev) + (B1 - B0) * tPrev;
                e.SetPosition(i, fix); e.SetPrevious(i, fix - ne * MathF.Max(Vector3.Dot(dc, ne), 0f)); _lock[i] = 1;
                p1 = fix; dp = p1 - p0; lp = dp.Length();
            }
        }
        CcdSweeps = sweeps;
    }

    /// <summary>클램프가 터널 복귀 입자를 다시 반대편으로 끌어간 경우 진입 쪽 표면으로 재배치.</summary>
    private void Refix(RealtimeGaussianClothHairPhysicsEngine e, AerodynamicWindBodyCollisionSynthesizer body)
    {
        for (var k = 0; k < _tunN0; k++)
        {
            int i = _tunI[k], c = _tunC[k];
            var x = e.Position(i);
            var q = RealtimeGaussianClothHairPhysicsEngine.ClosestOnSeg(body.CapsuleA(c), body.CapsuleB(c), x, out _);
            var R = body.Radius(c) + e.Thickness(i);
            var dv = x - q; var s = Vector3.Dot(dv, _tunN[k]);
            if (s >= 0f && dv.LengthSquared() >= R * R) continue;
            var lat = dv - _tunN[k] * s;
            var ll = lat.Length(); if (ll > R * 0.9f) lat *= R * 0.9f / ll;
            var h = MathF.Sqrt(MathF.Max(R * R - lat.LengthSquared(), 0f));
            e.SetPosition(i, q + lat + _tunN[k] * h);
        }
    }

    private static int Push(RealtimeGaussianClothHairPhysicsEngine e, AerodynamicWindBodyCollisionSynthesizer body)
    {
        var n = 0;
        for (var i = 0; i < P; i++)
        {
            if (e.W[i] <= 0f) continue;
            var x = e.Position(i); var th = e.Thickness(i);
            var moved = false;
            for (var c = 0; c < AerodynamicWindBodyCollisionSynthesizer.Capsules; c++)
            {
                var R = body.Radius(c) + th; var br = body.BoundRadius(c) + th;
                if (Vector3.DistanceSquared(x, body.BoundCenter(c)) > br * br || !AerodynamicWindBodyCollisionSynthesizer.Collides(i, c)) continue;
                var q = RealtimeGaussianClothHairPhysicsEngine.ClosestOnSeg(body.CapsuleA(c), body.CapsuleB(c), x, out _);
                var dv = x - q; var d2 = dv.LengthSquared();
                if (d2 >= R * R || d2 < 1e-14f) continue;
                x = q + dv * (R / MathF.Sqrt(d2)); moved = true;
            }
            if (moved) { e.SetPosition(i, x); n++; }
        }
        return n;
    }

    /// <summary>캡슐 내부 침투 입자 수(표면 − 1 mm) · 접촉 띠(표면 + 두께 + 2 mm) 플래그 기록.</summary>
    private int CountPenetrations(RealtimeGaussianClothHairPhysicsEngine e, AerodynamicWindBodyCollisionSynthesizer body)
    {
        var n = 0;
        for (var i = 0; i < P; i++)
        {
            _contact[i] = 0;
            if (e.W[i] <= 0f) continue;
            var x = e.Position(i); var th = e.Thickness(i);
            var inside = false;
            for (var c = 0; c < AerodynamicWindBodyCollisionSynthesizer.Capsules; c++)
            {
                var r = body.Radius(c); var band = r + th + ContactBand; var br = body.BoundRadius(c) + th + ContactBand;
                if (Vector3.DistanceSquared(x, body.BoundCenter(c)) > br * br || !AerodynamicWindBodyCollisionSynthesizer.Collides(i, c)) continue;
                var q = RealtimeGaussianClothHairPhysicsEngine.ClosestOnSeg(body.CapsuleA(c), body.CapsuleB(c), x, out _);
                var d2 = Vector3.DistanceSquared(x, q);
                if (d2 < band * band) _contact[i] = 1;
                if (d2 < (r - PenTol) * (r - PenTol)) inside = true;
            }
            if (inside) n++;
        }
        return n;
    }

    /// <summary>잔여 신장 — 자유 구속(양 끝 비접촉 · 비복귀) 110 % 초과 수 / 충돌 우선 구간 초과 수 · 전체 최대 신장률.</summary>
    private float MeasureResidual(RealtimeGaussianClothHairPhysicsEngine e, out int free, out int contact)
    {
        free = 0; contact = 0; var peak = 1f; var lim = MaxStrain * (1f + StrainTol);
        for (var pass = 0; pass < 2; pass++)
        {
            int lo = pass == 0 ? 0 : e.HairStretchStart, hi = pass == 0 ? e.StructuralCount : e.HairStretchEnd;
            for (var c = lo; c < hi; c++)
            {
                int a = e.ConstraintA(c), b = e.ConstraintB(c);
                var r = Vector3.Distance(e.Position(a), e.Position(b)) / e.RestLength(c);
                if (r > peak) peak = r;
                if (r <= lim) continue;
                if ((_contact[a] | _contact[b] | _lock[a] | _lock[b]) != 0) contact++; else free++;
            }
        }
        return peak;
    }
}
