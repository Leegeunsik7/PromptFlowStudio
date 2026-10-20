using System.Numerics;

namespace PromptFlowStudio.Services.ContinuumPhysics;

/// <summary>
/// [PromptFlow Studio v17.26.0] 🛡 비침투 SDF 충돌 &amp; 에너지 발산 방어 가드(하위 단계 · 프레임 경로 무할당).
///   ▸ 동적 SDF: v17.11 골격 팔뚝 캡슐 · v17.13 검지 캡슐 · v17.24 햅틱 프록시 구 · v17.25 원격 참가자 해머 구 — 하위 단계마다 자세/점 속도 갱신.
///   ▸ 격자 충돌(항상): 콜라이더 내부 노드의 상대 속도 법선 성분 제거 + 쿨롱 마찰(μ 0.4) · 주입 일(½m(|v′|²−|v|²)⁺) 집계.
///   ▸ 가드 ON: ① CFL Δt = 0.5·Δx / max(P파 속도, 입자 최대 속도, 콜라이더 속도) 적응 하위 단계(최대 <see cref="MaxSubsteps"/> · 초과 시 시간 확장)
///     ② 하위 단계당 이동 ≤ 1 셀 속도 클램프(터널링 차단) ③ 이류 후 입자 SDF &lt; 0 이면 표면으로 투영 + 법선 유입 속도 제거
///     ④ 해밀토니안 클램프: H = KE + PE + EE 가 (직전 H + 콜라이더 주입 + 허용 오차) 를 넘으면 초과분만큼 운동 에너지를 축소.
///   ▸ 가드 OFF: 고정 <see cref="OffSubsteps"/> 하위 단계(명시적 적분 불안정 노출) · 격자 충돌만.
/// </summary>
public sealed class NonPenetrationEnergyConservationPhysicsGuard
{
    public const int Colliders = 4, OffSubsteps = 8, MaxSubsteps = 40;
    public const float Cfl = 0.5f, ColliderFriction = 0.4f, EnergyTol = 0.02f, PenetrationTol = 0.1f * RealtimeMpmContinuumPhysicsEngine.Dx, Period = 4f;
    public static readonly string[] Names = { "원격 해머(v17.25)", "팔뚝 골격(v17.11)", "검지(v17.13)", "햅틱 프록시(v17.24)" };

    private readonly Vector3[] _a = new Vector3[Colliders], _b = new Vector3[Colliders], _va = new Vector3[Colliders], _vb = new Vector3[Colliders];
    private readonly float[] _r = { 0.05f, 0.035f, 0.025f, 0.03f };
    private ContinuumMaterial _mat;
    private double _injected;
    private float _href;
    private bool _init;

    public bool Enabled { get; set; } = true;
    public bool ParticleProjection { get; set; } = true;
    public bool EnergyClamp { get; set; } = true;
    public float HammerSpeed { get; set; } = 4f;
    public long Projections { get; private set; }
    public long VelocityClamps { get; private set; }
    public long EnergyClamps { get; private set; }
    public long DilatedFrames { get; private set; }
    public int Penetrations { get; private set; }
    public int MaxPenetrations { get; private set; }
    public float MaxPenetrationMm { get; private set; }
    public double InjectedJ { get; private set; }
    public double RemovedJ { get; private set; }
    /// <summary>수치 적분이 만든 허위 에너지 누적(클램프 전 측정).</summary>
    public double SpuriousJ { get; private set; }
    public float LastExcess { get; private set; }
    public float EnergyBudget => _href;
    public float MaxEnergy { get; private set; }
    public Vector3 ColliderA(int c) => _a[c];
    public Vector3 ColliderB(int c) => _b[c];
    public float ColliderRadius(int c) => _r[c];
    public Vector3 ColliderVelocity(int c) => 0.5f * (_va[c] + _vb[c]);
    public static float BlockTop => RealtimeMpmContinuumPhysicsEngine.BlockMax.Y;

    public void OnReset(RealtimeMpmContinuumPhysicsEngine e)
    {
        _mat = e.Material;
        _init = false;
        _injected = 0;
        Pose(0.0, _a, _b);
        Array.Clear(_va); Array.Clear(_vb);
    }

    public void ResetMetrics()
    {
        Projections = VelocityClamps = EnergyClamps = DilatedFrames = 0;
        MaxPenetrations = 0; MaxPenetrationMm = 0f; InjectedJ = RemovedJ = SpuriousJ = 0; MaxEnergy = 0f;
    }

    /// <summary>하위 단계 수 · Δt 결정(가드 ON: CFL 적응 · OFF: 고정).</summary>
    public int PlanSubsteps(RealtimeMpmContinuumPhysicsEngine e, float frameSim, out float dt)
    {
        if (!Enabled) { dt = frameSim / OffSubsteps; return OffSubsteps; }
        var speed = MathF.Max(ContinuumMaterials.WaveSpeed(_mat), e.MaxSpeed);
        for (var c = 0; c < Colliders; c++) speed = MathF.Max(speed, MathF.Max(_va[c].Length(), _vb[c].Length()));
        var dtc = Cfl * RealtimeMpmContinuumPhysicsEngine.Dx / speed;
        var n = (int)MathF.Ceiling(frameSim / dtc);
        if (n > MaxSubsteps) { DilatedFrames++; dt = dtc; return MaxSubsteps; }
        n = Math.Max(n, 1);
        dt = frameSim / n;
        return n;
    }

    /// <summary>시나리오 시계 t(초)의 콜라이더 자세 · 점 속도(중앙 차분).</summary>
    public void UpdateColliders(double t, float dt)
    {
        Span<Vector3> a0 = stackalloc Vector3[Colliders];
        Span<Vector3> b0 = stackalloc Vector3[Colliders];
        Pose(t - dt, a0, b0);
        Pose(t, _a, _b);
        var inv = 1f / dt;
        for (var c = 0; c < Colliders; c++) { _va[c] = (_a[c] - a0[c]) * inv; _vb[c] = (_b[c] - b0[c]) * inv; }
    }

    /// <summary>결정적 시나리오(주기 4 s): 검지 누름 0.5–1.5 s · 해머 낙하 1.8 s~ · 햅틱 프록시 측면 밀기 2.6–3.4 s.</summary>
    public void Pose(double time, Span<Vector3> a, Span<Vector3> b)
    {
        var t = (float)(time % Period);
        var top = BlockTop;
        // 해머: 1.8 s 부터 HammerSpeed 로 낙하(중심 = 블록 상면 → 반지름 5 cm 압입) → 0.5 m/s 로 복귀
        const float start = 1.8f, rest = 0.6f;
        var low = top;
        var tHit = (rest - low) / HammerSpeed;
        float hy;
        if (t < start) hy = rest;
        else if (t < start + tHit) hy = rest - HammerSpeed * (t - start);
        else hy = MathF.Min(rest, low + 0.5f * (t - start - tHit));
        a[0] = b[0] = new Vector3(0.56f, hy, 0.5f);
        // 팔뚝(팔꿈치 → 손목) · 검지(손목 → 손끝)
        var s = t > 0.5f && t < 1.5f ? MathF.Sin(MathF.PI * (t - 0.5f)) : 0f;
        s *= s;
        var wrist = new Vector3(0.34f, top + 0.13f - 0.04f * s, 0.45f);
        a[1] = new Vector3(0.12f, top + 0.2f, 0.45f); b[1] = wrist;
        a[2] = wrist; b[2] = new Vector3(0.44f, top + 0.06f - 0.09f * s, 0.45f);
        // 햅틱 프록시: +z 측면에서 밀기
        var h = t > 2.6f && t < 3.4f ? MathF.Sin(MathF.PI * (t - 2.6f) / 0.8f) : 0f;
        a[3] = b[3] = new Vector3(0.45f, RealtimeMpmContinuumPhysicsEngine.Floor + 0.07f, 0.68f - 0.08f * h);
    }

    /// <summary>캡슐 SDF 최솟값 · 외향 법선 · 표면점 속도.</summary>
    public float Sdf(Vector3 p, out Vector3 n, out Vector3 vel)
    {
        var best = float.MaxValue;
        n = Vector3.UnitY; vel = Vector3.Zero;
        for (var c = 0; c < Colliders; c++)
        {
            var d = Capsule(c, p, out var nc, out var u);
            if (d >= best) continue;
            best = d; n = nc; vel = _va[c] + (_vb[c] - _va[c]) * u;
        }
        return best;
    }

    private float Capsule(int c, Vector3 p, out Vector3 n, out float u)
    {
        var ab = _b[c] - _a[c];
        var l2 = ab.LengthSquared();
        u = l2 > 1e-12f ? Math.Clamp(Vector3.Dot(p - _a[c], ab) / l2, 0f, 1f) : 0f;
        var q = p - (_a[c] + ab * u);
        var len = q.Length();
        n = len > 1e-9f ? q / len : Vector3.UnitY;
        return len - _r[c];
    }

    /// <summary>격자 노드 충돌(콜라이더 AABB 내 노드만 순회).</summary>
    public void ApplyGridColliders(float[] gm, float[] gx, float[] gy, float[] gz, float dt)
    {
        const int N = RealtimeMpmContinuumPhysicsEngine.N;
        const float dx = RealtimeMpmContinuumPhysicsEngine.Dx;
        double inj = 0;
        for (var c = 0; c < Colliders; c++)
        {
            var mn = Vector3.Min(_a[c], _b[c]) - new Vector3(_r[c]);
            var mx = Vector3.Max(_a[c], _b[c]) + new Vector3(_r[c]);
            int i0 = Math.Max(0, (int)(mn.X / dx)), i1 = Math.Min(N - 1, (int)(mx.X / dx) + 1);
            int j0 = Math.Max(0, (int)(mn.Y / dx)), j1 = Math.Min(N - 1, (int)(mx.Y / dx) + 1);
            int k0 = Math.Max(0, (int)(mn.Z / dx)), k1 = Math.Min(N - 1, (int)(mx.Z / dx) + 1);
            for (var i = i0; i <= i1; i++)
                for (var j = j0; j <= j1; j++)
                    for (var k = k0; k <= k1; k++)
                    {
                        var g = (i * N + j) * N + k;
                        var m = gm[g];
                        if (m <= 0f) continue;
                        var d = Capsule(c, new Vector3(i * dx, j * dx, k * dx), out var n, out var u);
                        if (d >= 0f) continue;
                        var vc = _va[c] + (_vb[c] - _va[c]) * u;
                        var v = new Vector3(gx[g], gy[g], gz[g]);
                        var rel = v - vc;
                        var vn = Vector3.Dot(rel, n);
                        if (vn >= 0f) continue;
                        var vt = rel - vn * n;
                        var lt = vt.Length();
                        vt = lt > -ColliderFriction * vn ? vt * (1f + ColliderFriction * vn / lt) : Vector3.Zero;
                        var nv = vc + vt;
                        var de = 0.5 * m * (nv.LengthSquared() - v.LengthSquared());
                        if (de > 0) inj += de;
                        gx[g] = nv.X; gy[g] = nv.Y; gz[g] = nv.Z;
                    }
        }
        _injected += inj;
    }

    /// <summary>하위 단계당 이동 ≤ 1 셀(터널링 차단).</summary>
    public void LimitVelocity(ref float vx, ref float vy, ref float vz, float dt)
    {
        if (!Enabled) return;
        var vmax = RealtimeMpmContinuumPhysicsEngine.Dx / dt;
        var s2 = vx * vx + vy * vy + vz * vz;
        if (s2 <= vmax * vmax) return;
        var k = vmax / MathF.Sqrt(s2);
        vx *= k; vy *= k; vz *= k;
        VelocityClamps++;
    }

    /// <summary>이류 후 입자 비침투 투영(가드 ON).</summary>
    public void ProjectParticle(ref float x, ref float y, ref float z, ref float vx, ref float vy, ref float vz)
    {
        if (!Enabled || !ParticleProjection) return;
        var p = new Vector3(x, y, z);
        var d = Sdf(p, out var n, out var vc);
        if (d >= 0f) return;
        p -= d * n;
        x = p.X; y = p.Y; z = p.Z;
        var v = new Vector3(vx, vy, vz);
        var vn = Vector3.Dot(v - vc, n);
        if (vn < 0f) { v -= vn * n; vx = v.X; vy = v.Y; vz = v.Z; }
        Projections++;
    }

    /// <summary>프레임 종료: 침투 측정 · 허위 에너지 측정 · 해밀토니안 클램프.</summary>
    public void EndFrame(RealtimeMpmContinuumPhysicsEngine e)
    {
        var pen = 0;
        var deep = 0f;
        for (var i = 0; i < e.Count; i++)
        {
            var d = Sdf(e.Position(i), out _, out _);
            if (d < -PenetrationTol) pen++;
            if (-d > deep) deep = -d;
        }
        Penetrations = pen;
        if (pen > MaxPenetrations) MaxPenetrations = pen;
        if (deep * 1000f > MaxPenetrationMm) MaxPenetrationMm = deep * 1000f;

        var h = e.TotalEnergy;
        if (!float.IsFinite(h)) h = float.MaxValue;
        var inj = (float)_injected;
        InjectedJ += _injected;
        _injected = 0;
        if (!_init) { _href = h; _init = true; }
        var allowed = _href + inj + EnergyTol * (e.KineticEnergy + e.ElasticEnergy) + 1e-7f;
        var excess = h - allowed;
        LastExcess = MathF.Max(0f, excess);
        if (excess > 0f) SpuriousJ += excess;
        if (Enabled && EnergyClamp && excess > 0f)
        {
            var ke = e.KineticEnergy;
            var s = ke > 0f && float.IsFinite(ke) ? MathF.Sqrt(MathF.Max(0f, ke - excess) / ke) : 0f;
            e.ScaleVelocities(s);
            RemovedJ += ke - e.KineticEnergy;
            EnergyClamps++;
            h = e.TotalEnergy;
        }
        _href = h;
        if (h > MaxEnergy) MaxEnergy = h;
    }
}
