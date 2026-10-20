using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using PromptFlowStudio.Services.Atmosphere;
using PromptFlowStudio.Services.GenerativeVfx;
using PromptFlowStudio.Services.HairCloth;

namespace PromptFlowStudio.Services.DigitalTwinWorld;

/// <summary>물리 서브시스템(부하 스펙트럼 인덱스).</summary>
public enum PhysicsStage : byte { Weather, Rigid, Cloth, Fluid, Coupling }

/// <summary>
/// [PromptFlow Studio v16.99.0] 🌐 통합 멀티피직스 &amp; 제너레이티브 월드 다이내믹스 합성기.
/// · 강체(구 SoA · 최대 128): 반암시 오일러 Vector&lt;float&gt; 적분 · 지면/평면 · 구-구 충돌(반발/마찰 임펄스 · 위치 보정) · 수면 아래 부력/항력.
/// · 결합: v16.84 기상 → 바람(강체 공력 항력 · v16.83 천 Wind · 유체 기체상 추종) · 기온 → 유체 점성/기체 부력 ·
///   강체 ↔ 천(정점 구 밖 투영 + 법선 속도 제거 · 반작용 임펄스 = 정점 질량 × 속도 변화) · 강체 ↔ 유체(입자 구 밖 투영 + 반사 · 반작용 운동량 → 강체).
/// · 단계별 µs 실측 = 부하 스펙트럼 · 운동량/에너지 회계 · 무할당(서브시스템 내부 포함 사전 할당).
/// </summary>
public sealed class UnifiedMultiPhysicsWorldDynamicsSynthesizer
{
    public const int MaxBodies = 128, Stages = 5;
    public const float AvatarLift = 1.68f;
    public static readonly string[] StageNames = { "기상(v16.84)", "강체", "천(v16.83)", "유체(v16.81)", "결합" };
    private static readonly int Pad = (MaxBodies + Vector<float>.Count - 1) / Vector<float>.Count * Vector<float>.Count;
    private static readonly double TickUs = 1e6 / Stopwatch.Frequency;

    private readonly object _gate = new();
    private readonly float[] _x = new float[Pad], _y = new float[Pad], _z = new float[Pad], _vx = new float[Pad], _vy = new float[Pad], _vz = new float[Pad];
    private readonly float[] _r = new float[Pad], _invM = new float[Pad], _fx = new float[Pad], _fy = new float[Pad], _fz = new float[Pad];
    private readonly bool[] _kinematic = new bool[MaxBodies];
    private readonly double[] _stageUs = new double[Stages];
    private readonly float[] _clothMass;
    private int _bodies;
    private long _steps, _rigidContacts, _clothContacts, _fluidContacts;

    public UnifiedMultiPhysicsWorldDynamicsSynthesizer(DynamicMicroWeatherSimulatorEngine? weather = null, GenerativeGarmentClothCollisionSynthesizer? cloth = null, NeuralParticleFluidDynamicsEngine? fluid = null)
    {
        Weather = weather ?? new DynamicMicroWeatherSimulatorEngine();
        Cloth = cloth ?? new GenerativeGarmentClothCollisionSynthesizer();
        // v16.83 리그는 머리 원점(발 −1.675 m) → 월드 지면(y = 0)에 세움
        Cloth.Body.SetPose(0f, new Vector3(0f, AvatarLift, 0f), 0f, 0f, 0f);
        Cloth.Configure(Cloth.Garment, Cloth.Fabric);
        Fluid = fluid ?? new NeuralParticleFluidDynamicsEngine(4096);
        _clothMass = new float[GenerativeGarmentClothCollisionSynthesizer.MaxVerts];
        Array.Fill(_clothMass, 0.004f);
    }

    public DynamicMicroWeatherSimulatorEngine Weather { get; }
    public GenerativeGarmentClothCollisionSynthesizer Cloth { get; }
    public NeuralParticleFluidDynamicsEngine Fluid { get; }

    public bool WeatherOn { get; set; } = true;
    public bool RigidOn { get; set; } = true;
    public bool ClothOn { get; set; } = true;
    public bool FluidOn { get; set; } = true;
    public bool CouplingOn { get; set; } = true;
    public float Gravity { get; set; } = -9.81f;
    public float Restitution { get; set; } = 0.35f;
    public float Friction { get; set; } = 0.4f;
    public float AirDensity { get; set; } = 1.2f;
    public float DragCoefficient { get; set; } = 0.47f;
    /// <summary>바람 → 천/강체 결합 배율(실내 트윈 = 실외 기상의 일부만 유입).</summary>
    public float WindCoupling { get; set; } = 0.5f;
    public float FluidParticleMass { get; set; } = 0.002f;
    public float WaterLevel { get; set; } = -1f;

    public int BodyCount => _bodies;
    public long Steps => Interlocked.Read(ref _steps);
    public long RigidContacts => Interlocked.Read(ref _rigidContacts);
    public long ClothContacts => Interlocked.Read(ref _clothContacts);
    public long FluidContacts => Interlocked.Read(ref _fluidContacts);
    public double StageUs(PhysicsStage s) => _stageUs[(int)s];
    public double TotalUs { get; private set; }
    public Vector3 LastWind { get; private set; }
    public float LastTemperature { get; private set; }
    public Vector3 ClothImpulse { get; private set; }
    public Vector3 FluidImpulse { get; private set; }

    public Vector3 BodyPosition(int i) => new(_x[i], _y[i], _z[i]);
    public Vector3 BodyVelocity(int i) => new(_vx[i], _vy[i], _vz[i]);
    public float BodyRadius(int i) => _r[i];
    public bool IsKinematic(int i) => _kinematic[i];

    public int AddBody(Vector3 p, float radius, float mass, Vector3 v = default, bool kinematic = false)
    {
        lock (_gate)
        {
            if (_bodies >= MaxBodies) return -1;
            var i = _bodies++;
            _x[i] = p.X; _y[i] = p.Y; _z[i] = p.Z; _vx[i] = v.X; _vy[i] = v.Y; _vz[i] = v.Z;
            _r[i] = radius; _invM[i] = kinematic || mass <= 0 ? 0 : 1f / mass; _kinematic[i] = kinematic;
            return i;
        }
    }

    /// <summary>운동학 강체 이동(디지털 트윈 동기화 대상 · 속도 = 이동/dt).</summary>
    public void MoveKinematic(int i, Vector3 p, float dt)
    {
        lock (_gate)
        {
            if (dt > 0) { _vx[i] = (p.X - _x[i]) / dt; _vy[i] = (p.Y - _y[i]) / dt; _vz[i] = (p.Z - _z[i]) / dt; }
            _x[i] = p.X; _y[i] = p.Y; _z[i] = p.Z;
        }
    }

    /// <summary>앵커 락 복귀: 강체가 락 위치에서 벗어났으면 위치 고정 + 속도 제거(물리 드리프트 차단).</summary>
    public bool MoveKinematicIfDrifted(int i, Vector3 locked, float tolerance = 1e-4f)
    {
        lock (_gate)
        {
            if ((BodyPosition(i) - locked).LengthSquared() <= tolerance * tolerance) return false;
            _x[i] = locked.X; _y[i] = locked.Y; _z[i] = locked.Z; _vx[i] = _vy[i] = _vz[i] = 0;
            return true;
        }
    }

    public void ClearBodies() { lock (_gate) { _bodies = 0; Array.Clear(_r); Array.Clear(_invM); } }

    /// <summary>총 강체 운동량(동역학 · kg·m/s).</summary>
    public Vector3 RigidMomentum()
    {
        var m = Vector3.Zero;
        for (var i = 0; i < _bodies; i++) if (_invM[i] > 0) m += BodyVelocity(i) / _invM[i];
        return m;
    }

    public float RigidKineticEnergy()
    {
        float e = 0;
        for (var i = 0; i < _bodies; i++) if (_invM[i] > 0) e += 0.5f * BodyVelocity(i).LengthSquared() / _invM[i];
        return e;
    }

    /// <summary>1프레임 통합 스텝(무할당 · 단계 µs 기록).</summary>
    public void Step(float dt)
    {
        lock (_gate)
        {
            var t0 = Stopwatch.GetTimestamp();
            var ts = t0;
            // 1) 기상 → 바람/기온
            if (WeatherOn) Weather.Step(dt);
            var st = Weather.State;
            LastTemperature = st.Temperature;
            LastWind = WeatherOn ? Weather.WindAt(0f, 1.5f, 0f) * WindCoupling : Vector3.Zero; // 기상 OFF = 무풍
            Lap(PhysicsStage.Weather, ref ts);
            // 2) 강체
            if (RigidOn) StepRigid(dt);
            Lap(PhysicsStage.Rigid, ref ts);
            // 3) 천(바람 결합)
            if (ClothOn)
            {
                Cloth.Wind = CouplingOn ? LastWind : Vector3.Zero;
                Cloth.Step(dt);
            }
            Lap(PhysicsStage.Cloth, ref ts);
            // 4) 유체(기온 → 점성/부력)
            if (FluidOn)
            {
                if (CouplingOn)
                {
                    var warm = Math.Clamp((LastTemperature - 5f) / 30f, 0f, 1f);
                    Fluid.Viscosity = 0.9f - 0.5f * warm;
                    Fluid.GasBuoyancy = 0.1f + 0.4f * warm;
                }
                Fluid.Step(dt);
            }
            Lap(PhysicsStage.Fluid, ref ts);
            // 5) 결합(강체 ↔ 천 · 강체 ↔ 유체 · 바람 → 유체 기체상)
            if (CouplingOn)
            {
                ClothImpulse = RigidOn && ClothOn ? CoupleCloth() : Vector3.Zero;
                FluidImpulse = RigidOn && FluidOn ? CoupleFluid() : Vector3.Zero;
                if (FluidOn) WindOnGas(dt);
            }
            Lap(PhysicsStage.Coupling, ref ts);
            TotalUs = (Stopwatch.GetTimestamp() - t0) * TickUs;
            Interlocked.Increment(ref _steps);
        }
    }

    private void Lap(PhysicsStage s, ref long ts)
    {
        var now = Stopwatch.GetTimestamp();
        _stageUs[(int)s] = (now - ts) * TickUs;
        ts = now;
    }

    private void StepRigid(float dt)
    {
        var n = _bodies;
        // 외력: 중력 + 공력 항력(상대 바람) + 수중 부력/항력
        var w = CouplingOn ? LastWind : Vector3.Zero;
        for (var i = 0; i < n; i++)
        {
            if (_invM[i] == 0) { _fx[i] = _fy[i] = _fz[i] = 0; continue; }
            var m = 1f / _invM[i];
            var rel = w - BodyVelocity(i);
            var area = MathF.PI * _r[i] * _r[i];
            var drag = 0.5f * AirDensity * DragCoefficient * area * rel.Length() * rel;
            var f = new Vector3(0, m * Gravity, 0) + drag;
            if (_y[i] - _r[i] < WaterLevel)
            {
                var sub = Math.Clamp((WaterLevel - (_y[i] - _r[i])) / (2 * _r[i]), 0f, 1f);
                var vol = 4f / 3f * MathF.PI * _r[i] * _r[i] * _r[i] * sub;
                f += new Vector3(0, 1000f * 9.81f * vol, 0) - BodyVelocity(i) * (2f * m * sub);
            }
            _fx[i] = f.X; _fy[i] = f.Y; _fz[i] = f.Z;
        }
        // 반암시 오일러(SIMD): v += F·invM·dt · x += v·dt
        var vx = MemoryMarshal.Cast<float, Vector<float>>(_vx.AsSpan()); var vy = MemoryMarshal.Cast<float, Vector<float>>(_vy.AsSpan()); var vz = MemoryMarshal.Cast<float, Vector<float>>(_vz.AsSpan());
        var px = MemoryMarshal.Cast<float, Vector<float>>(_x.AsSpan()); var py = MemoryMarshal.Cast<float, Vector<float>>(_y.AsSpan()); var pz = MemoryMarshal.Cast<float, Vector<float>>(_z.AsSpan());
        var fx = MemoryMarshal.Cast<float, Vector<float>>(_fx.AsSpan()); var fy = MemoryMarshal.Cast<float, Vector<float>>(_fy.AsSpan()); var fz = MemoryMarshal.Cast<float, Vector<float>>(_fz.AsSpan());
        var im = MemoryMarshal.Cast<float, Vector<float>>(_invM.AsSpan());
        var vdt = new Vector<float>(dt);
        var blocks = (n + Vector<float>.Count - 1) / Vector<float>.Count;
        for (var k = 0; k < blocks; k++)
        {
            var a = im[k] * vdt;
            vx[k] += fx[k] * a; vy[k] += fy[k] * a; vz[k] += fz[k] * a;
            var dyn = Vector.GreaterThan(im[k], Vector<float>.Zero);
            px[k] += Vector.ConditionalSelect(dyn, vx[k] * vdt, Vector<float>.Zero);
            py[k] += Vector.ConditionalSelect(dyn, vy[k] * vdt, Vector<float>.Zero);
            pz[k] += Vector.ConditionalSelect(dyn, vz[k] * vdt, Vector<float>.Zero);
        }
        // 지면
        for (var i = 0; i < n; i++)
        {
            if (_invM[i] == 0 || _y[i] >= _r[i]) continue;
            _y[i] = _r[i];
            if (_vy[i] < 0)
            {
                var vn = -_vy[i];
                _vy[i] = vn * Restitution;
                if (_vy[i] < 0.05f) _vy[i] = 0;
                var damp = Math.Max(0f, 1f - Friction * vn * dt * 20f);
                _vx[i] *= damp; _vz[i] *= damp;
                _rigidContacts++;
            }
        }
        // 구-구(임펄스 · 2회 반복)
        for (var it = 0; it < 2; it++)
            for (var i = 0; i < n; i++)
                for (var j = i + 1; j < n; j++)
                {
                    var wi = _invM[i]; var wj = _invM[j];
                    if (wi + wj == 0) continue;
                    var d = BodyPosition(j) - BodyPosition(i);
                    var rr = _r[i] + _r[j];
                    var d2 = d.LengthSquared();
                    if (d2 >= rr * rr || d2 < 1e-12f) continue;
                    var dist = MathF.Sqrt(d2); var nrm = d / dist;
                    var pen = rr - dist;
                    var corr = nrm * (pen / (wi + wj) * 0.8f);
                    _x[i] -= corr.X * wi; _y[i] -= corr.Y * wi; _z[i] -= corr.Z * wi;
                    _x[j] += corr.X * wj; _y[j] += corr.Y * wj; _z[j] += corr.Z * wj;
                    var rv = BodyVelocity(j) - BodyVelocity(i);
                    var vn = Vector3.Dot(rv, nrm);
                    if (vn >= 0) continue;
                    var jn = -(1 + Restitution) * vn / (wi + wj);
                    var tang = rv - nrm * vn;
                    var tl = tang.Length();
                    var jt = tl > 1e-6f ? -Math.Min(Friction * jn, tl / (wi + wj)) * (tang / tl) : Vector3.Zero;
                    var imp = nrm * jn + jt;
                    _vx[i] -= imp.X * wi; _vy[i] -= imp.Y * wi; _vz[i] -= imp.Z * wi;
                    _vx[j] += imp.X * wj; _vy[j] += imp.Y * wj; _vz[j] += imp.Z * wj;
                    _rigidContacts++;
                }
    }

    /// <summary>천 정점 × 강체 구: 밖으로 투영 · 정점 법선 속도 제거 · 반작용 임펄스 → 강체.</summary>
    private Vector3 CoupleCloth()
    {
        var c = Cloth;
        var total = Vector3.Zero;
        for (var b = 0; b < _bodies; b++)
        {
            var center = BodyPosition(b); var r = _r[b] + 0.01f; var r2 = r * r;
            var vb = BodyVelocity(b);
            var acc = Vector3.Zero;
            for (var v = 0; v < GenerativeGarmentClothCollisionSynthesizer.MaxVerts; v++)
            {
                if (c.InvMass[v] == 0) continue;
                var d = new Vector3(c.X[v] - center.X, c.Y[v] - center.Y, c.Z[v] - center.Z);
                var d2 = d.LengthSquared();
                if (d2 >= r2 || d2 < 1e-12f) continue;
                var dist = MathF.Sqrt(d2); var nrm = d / dist;
                var p = center + nrm * r;
                c.X[v] = p.X; c.Y[v] = p.Y; c.Z[v] = p.Z;
                var vv = new Vector3(c.VX[v], c.VY[v], c.VZ[v]);
                var rel = vv - vb;
                var vn = Vector3.Dot(rel, nrm);
                if (vn < 0)
                {
                    var dv = -vn * nrm;
                    c.VX[v] += dv.X; c.VY[v] += dv.Y; c.VZ[v] += dv.Z;
                    acc -= dv * _clothMass[v];
                }
                _clothContacts++;
            }
            if (_invM[b] > 0) { _vx[b] += acc.X * _invM[b]; _vy[b] += acc.Y * _invM[b]; _vz[b] += acc.Z * _invM[b]; }
            total += acc;
        }
        return total;
    }

    /// <summary>유체 입자 × 강체 구: 밖으로 투영 + 반사 · 반작용 운동량 → 강체.</summary>
    private Vector3 CoupleFluid()
    {
        var f = Fluid;
        var n = f.Count;
        var total = Vector3.Zero;
        for (var b = 0; b < _bodies; b++)
        {
            var center = BodyPosition(b); var r = _r[b] + f.H * 0.25f; var r2 = r * r;
            var vb = BodyVelocity(b);
            var acc = Vector3.Zero;
            for (var i = 0; i < n; i++)
            {
                var dx = f.X[i] - center.X; var dy = f.Y[i] - center.Y; var dz = f.Z[i] - center.Z;
                var d2 = dx * dx + dy * dy + dz * dz;
                if (d2 >= r2 || d2 < 1e-12f) continue;
                var dist = MathF.Sqrt(d2);
                var nrm = new Vector3(dx, dy, dz) / dist;
                var p = center + nrm * r;
                f.X[i] = p.X; f.Y[i] = p.Y; f.Z[i] = p.Z;
                var vv = new Vector3(f.VX[i], f.VY[i], f.VZ[i]);
                var vn = Vector3.Dot(vv - vb, nrm);
                if (vn < 0)
                {
                    var dv = -(1f + 0.2f) * vn * nrm;
                    f.VX[i] += dv.X; f.VY[i] += dv.Y; f.VZ[i] += dv.Z;
                    acc -= dv * FluidParticleMass;
                }
                _fluidContacts++;
            }
            if (_invM[b] > 0) { _vx[b] += acc.X * _invM[b]; _vy[b] += acc.Y * _invM[b]; _vz[b] += acc.Z * _invM[b]; }
            total += acc;
        }
        return total;
    }

    /// <summary>바람 → 유체 기체상 이완 추종(τ = 0.5 s).</summary>
    private void WindOnGas(float dt)
    {
        var f = Fluid; var w = LastWind; var k = Math.Clamp(dt / 0.5f, 0f, 1f);
        for (var i = 0; i < f.Count; i++)
        {
            if (f.Phase[i] != (byte)FluidPhase.Gas) continue;
            f.VX[i] += (w.X - f.VX[i]) * k; f.VZ[i] += (w.Z - f.VZ[i]) * k;
        }
    }
}
