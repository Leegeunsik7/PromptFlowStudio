using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.OpenUsd;

/// <summary>지면 높이장(지형 · 음향 흡음률). <see cref="GenerativeWorldProceduralEngine"/> 가 구현.</summary>
public interface IHeightField
{
    float HeightAt(float x, float z);
    float AbsorptionAt(float x, float z);
}

/// <summary>
/// 학습 잔차 보정 결선점 — 해석적 솔버 속도장에 신경망 잔차(Δv)를 더함. 미결선 시 순수 해석 솔버.
/// 구현은 무할당이어야 함(물리 틱 핫패스에서 호출).
/// </summary>
public interface IPhysicsResidualModel
{
    void Correct(Span<float> vx, Span<float> vy, Span<float> vz, int count, float dt);
}

public enum ImpactKind : byte { Ground = 0, Body, Cloth, Fluid }

public readonly record struct ImpactEvent(float Time, float X, float Y, float Z, float Energy, ImpactKind Kind);

/// <summary>초기 반사 탭(지연 ms · 선형 이득 · 반사면 인덱스).</summary>
public readonly record struct ReverbTap(float DelayMs, float Gain, int Patch);

/// <summary>
/// [PromptFlow Studio v16.72.0] ⚛ 강체/변형체 물리 &amp; 공간 충돌 시뮬레이터.
///   ▸ 강체: 구 SoA 256 · 반암시적 오일러(SIMD <see cref="Vector{T}"/> — AVX2 시 8레인) · 균일 해시 그리드 광역 충돌 · 충격량 반발/마찰 · 높이장 지면 접촉 · 위치 보정.
///   ▸ 천: 16×16 Verlet(SIMD) · 구조/전단 거리 제약 6회 · 고정 핀 · 구/지면 충돌.
///   ▸ 유체: PBF(Position Based Fluids) 216 입자 · 단방향 밀도 제약(poly6/spiky · 정지 밀도 자동 보정) · 해시 그리드 이웃 · 용기 경계.
///   ▸ 음향: 충돌 기하(지면 패치 · 외피 벽 · 강체 · 천 · 수면) → 반사 메시 → 이미지 소스 근사 초기 반사 탭 + Sabine RT60 → 임펄스 응답 · 충돌 임팩트 이벤트 링.
///   ▸ 신경망: <see cref="IPhysicsResidualModel"/> 결선점(학습 잔차 Δv) — 미결선 시 해석적 솔버만 사용.
///   스레드 모델: 단일 소유자(러너 잠금). 모든 Step/Build 무할당.
/// </summary>
public sealed class NeuralPhysicsCollisionSimulator
{
    public const int MaxBodies = 256, ClothN = 16, ClothCount = ClothN * ClothN, FluidSide = 6, FluidCount = FluidSide * FluidSide * FluidSide;
    public const int MaxPatches = 128, MaxTaps = 64, MaxImpacts = 64;
    public const float SpeedOfSound = 343f;
    private const int GridBuckets = 4096;

    // 강체
    private readonly float[] _px = new float[MaxBodies], _py = new float[MaxBodies], _pz = new float[MaxBodies];
    private readonly float[] _vx = new float[MaxBodies], _vy = new float[MaxBodies], _vz = new float[MaxBodies];
    private readonly float[] _r = new float[MaxBodies], _inv = new float[MaxBodies], _rest = new float[MaxBodies];
    private int _n;
    private readonly int[] _head = new int[GridBuckets], _next = new int[Math.Max(MaxBodies, FluidCount)];

    // 천
    private readonly float[] _cx = new float[ClothCount], _cy = new float[ClothCount], _cz = new float[ClothCount];
    private readonly float[] _ox = new float[ClothCount], _oy = new float[ClothCount], _oz = new float[ClothCount];
    private readonly float[] _tmp = new float[ClothCount];
    private readonly int[] _ca = new int[ClothCount * 4], _cb = new int[ClothCount * 4];
    private readonly float[] _cl = new float[ClothCount * 4];
    private int _cons;
    private Vector3 _pinA, _pinB;

    // 유체
    private readonly float[] _fx = new float[FluidCount], _fy = new float[FluidCount], _fz = new float[FluidCount];
    private readonly float[] _qx = new float[FluidCount], _qy = new float[FluidCount], _qz = new float[FluidCount];
    private readonly float[] _fvx = new float[FluidCount], _fvy = new float[FluidCount], _fvz = new float[FluidCount];
    private readonly float[] _lambda = new float[FluidCount], _rho = new float[FluidCount];
    private Vector3 _boxMin, _boxMax;
    private float _rho0 = 1;
    private const float H = 0.1f;
    private static readonly float Poly6 = 315f / (64f * MathF.PI * MathF.Pow(H, 9));
    private static readonly float SpikyGrad = -45f / (MathF.PI * MathF.Pow(H, 6));

    // 음향
    private readonly float[] _apx = new float[MaxPatches], _apy = new float[MaxPatches], _apz = new float[MaxPatches];
    private readonly float[] _anx = new float[MaxPatches], _any = new float[MaxPatches], _anz = new float[MaxPatches];
    private readonly float[] _aarea = new float[MaxPatches], _aabs = new float[MaxPatches];
    private readonly byte[] _akind = new byte[MaxPatches];
    private int _patches;
    private readonly ReverbTap[] _taps = new ReverbTap[MaxTaps];
    private int _tapCount;
    private readonly ImpactEvent[] _impacts = new ImpactEvent[MaxImpacts];
    private long _impactSeq;

    private float _time, _maxPen;
    private long _steps, _contacts, _lastStepTicks;

    public NeuralPhysicsCollisionSimulator()
    {
        ResetCloth(new Vector3(-0.75f, 2.5f, 0), 0.1f);
        ResetFluid(new Vector3(2f, 0f, -0.5f), new Vector3(2.6f, 1.2f, 0.1f));
    }

    public IHeightField? Field { get; set; }
    public IPhysicsResidualModel? Residual { get; set; }
    public float GroundY { get; set; }
    public float GravityY { get; set; } = -9.81f;
    public float Friction { get; set; } = 0.3f;
    public int Substeps { get; set; } = 4;
    public bool RigidEnabled { get; set; } = true;
    public bool ClothEnabled { get; set; } = true;
    public bool FluidEnabled { get; set; } = true;
    /// <summary>음향 외피(실내) — null 이면 야외(지면 + 오브젝트만).</summary>
    public (Vector3 Min, Vector3 Max, float Absorption)? Enclosure { get; set; }

    public int BodyCount => _n;
    public float Time => _time;
    public long Steps => Interlocked.Read(ref _steps);
    public long Contacts => Interlocked.Read(ref _contacts);
    public float MaxPenetration => _maxPen;
    public double LastStepMs => _lastStepTicks * 1000.0 / Stopwatch.Frequency;
    public static int SimdLanes => Vector<float>.Count;
    public static bool SimdAccelerated => Vector.IsHardwareAccelerated;
    public static bool Avx2 => System.Runtime.Intrinsics.X86.Avx2.IsSupported;
    public float RestDensity => _rho0;
    public int PatchCount => _patches;
    public int TapCount => _tapCount;
    public float Rt60 { get; private set; }
    public long ImpactCount => Interlocked.Read(ref _impactSeq);

    public Vector3 BodyPosition(int i) => new(_px[i], _py[i], _pz[i]);
    public Vector3 BodyVelocity(int i) => new(_vx[i], _vy[i], _vz[i]);
    public float BodyRadius(int i) => _r[i];
    public Vector3 ClothPoint(int i) => new(_cx[i], _cy[i], _cz[i]);
    public Vector3 FluidPoint(int i) => new(_fx[i], _fy[i], _fz[i]);
    public float FluidDensityRatio(int i) => _rho[i] / _rho0;
    public (Vector3 Min, Vector3 Max) FluidBox => (_boxMin, _boxMax);
    public ReverbTap Tap(int i) => _taps[i];

    // ══════════════════════ 구성 ══════════════════════

    public void ClearBodies() { _n = 0; _maxPen = 0; }

    public int AddSphere(Vector3 p, float radius, float mass, float restitution = 0.5f, Vector3 velocity = default)
    {
        if (_n >= MaxBodies) return -1;
        var i = _n++;
        _px[i] = p.X; _py[i] = p.Y; _pz[i] = p.Z;
        _vx[i] = velocity.X; _vy[i] = velocity.Y; _vz[i] = velocity.Z;
        _r[i] = MathF.Max(0.01f, radius); _inv[i] = mass > 0 ? 1f / mass : 0; _rest[i] = Math.Clamp(restitution, 0f, 1f);
        return i;
    }

    public void SetBodyPosition(int i, Vector3 p) { _px[i] = p.X; _py[i] = p.Y; _pz[i] = p.Z; }

    /// <summary>천 초기화: 핀(왼/오 위 모서리)에서 수평(XZ) 격자 · spacing 간격 · 구조+전단 제약.</summary>
    public void ResetCloth(Vector3 origin, float spacing)
    {
        for (var j = 0; j < ClothN; j++)
            for (var i = 0; i < ClothN; i++)
            {
                var k = j * ClothN + i;
                _cx[k] = _ox[k] = origin.X + i * spacing;
                _cy[k] = _oy[k] = origin.Y;
                _cz[k] = _oz[k] = origin.Z + j * spacing;
            }
        _pinA = new Vector3(_cx[0], _cy[0], _cz[0]);
        _pinB = new Vector3(_cx[ClothN - 1], _cy[ClothN - 1], _cz[ClothN - 1]);
        _cons = 0;
        var diag = spacing * MathF.Sqrt(2);
        for (var j = 0; j < ClothN; j++)
            for (var i = 0; i < ClothN; i++)
            {
                var k = j * ClothN + i;
                if (i + 1 < ClothN) AddCon(k, k + 1, spacing);
                if (j + 1 < ClothN) AddCon(k, k + ClothN, spacing);
                if (i + 1 < ClothN && j + 1 < ClothN) AddCon(k, k + ClothN + 1, diag);
                if (i > 0 && j + 1 < ClothN) AddCon(k, k + ClothN - 1, diag);
            }
    }

    private void AddCon(int a, int b, float len) { _ca[_cons] = a; _cb[_cons] = b; _cl[_cons] = len; _cons++; }

    public int ClothConstraintCount => _cons;

    /// <summary>최대 거리 제약 오차(상대 · 신장 측정).</summary>
    public float ClothMaxStretch()
    {
        var m = 0f;
        for (var c = 0; c < _cons; c++)
        {
            int a = _ca[c], b = _cb[c];
            var d = MathF.Sqrt(Sq(_cx[a] - _cx[b]) + Sq(_cy[a] - _cy[b]) + Sq(_cz[a] - _cz[b]));
            m = MathF.Max(m, MathF.Abs(d - _cl[c]) / _cl[c]);
        }
        return m;
    }

    /// <summary>유체 블록 초기화: 용기(min..max) 바닥 모서리에 6³ 격자(간격 H/2) · 정지 밀도 = 격자 중심 입자 밀도로 자동 보정.</summary>
    public void ResetFluid(Vector3 boxMin, Vector3 boxMax)
    {
        _boxMin = boxMin; _boxMax = boxMax;
        const float s = H * 0.5f;
        for (var k = 0; k < FluidSide; k++)
            for (var j = 0; j < FluidSide; j++)
                for (var i = 0; i < FluidSide; i++)
                {
                    var p = (k * FluidSide + j) * FluidSide + i;
                    _fx[p] = boxMin.X + s * (i + 0.5f); _fy[p] = boxMin.Y + s * (j + 0.5f); _fz[p] = boxMin.Z + s * (k + 0.5f);
                    _fvx[p] = _fvy[p] = _fvz[p] = 0;
                }
        _fx.AsSpan().CopyTo(_qx); _fy.AsSpan().CopyTo(_qy); _fz.AsSpan().CopyTo(_qz);
        BuildGrid(_qx, _qy, _qz, FluidCount, H);
        var center = (FluidSide / 2 * FluidSide + FluidSide / 2) * FluidSide + FluidSide / 2;
        _rho0 = 1;
        _rho0 = Density(center);
        for (var p = 0; p < FluidCount; p++) _rho[p] = _rho0;
    }

    // ══════════════════════ 스텝 ══════════════════════

    /// <summary>dt 초 진행(서브스텝 분할 · 무할당).</summary>
    public void Step(float dt)
    {
        using var guard = ZeroGcRegionScope.Enter("Physics.Step");
        var t0 = Stopwatch.GetTimestamp();
        var sub = Math.Clamp(Substeps, 1, 16);
        var h = dt / sub;
        for (var s = 0; s < sub; s++)
        {
            if (RigidEnabled && _n > 0) StepRigid(h);
            if (ClothEnabled) StepCloth(h);
            if (FluidEnabled) StepFluid(h);
            _time += h;
        }
        _lastStepTicks = Stopwatch.GetTimestamp() - t0;
        Interlocked.Increment(ref _steps);
    }

    private float Ground(float x, float z) => Field?.HeightAt(x, z) ?? GroundY;

    private Vector3 GroundNormal(float x, float z)
    {
        if (Field is null) return Vector3.UnitY;
        const float e = 0.2f;
        var hx = Field.HeightAt(x + e, z) - Field.HeightAt(x - e, z);
        var hz = Field.HeightAt(x, z + e) - Field.HeightAt(x, z - e);
        return Vector3.Normalize(new Vector3(-hx, 2 * e, -hz));
    }

    private void StepRigid(float dt)
    {
        var n = _n;
        // SIMD 적분: v += g·dt(동적 바디만 — inv>0 마스크) · p += v·dt
        AddMasked(_vy.AsSpan(0, n), _inv.AsSpan(0, n), GravityY * dt);
        Residual?.Correct(_vx.AsSpan(0, n), _vy.AsSpan(0, n), _vz.AsSpan(0, n), n, dt);
        Axpy(_px.AsSpan(0, n), _vx.AsSpan(0, n), dt);
        Axpy(_py.AsSpan(0, n), _vy.AsSpan(0, n), dt);
        Axpy(_pz.AsSpan(0, n), _vz.AsSpan(0, n), dt);

        // 광역: 해시 그리드(셀 = 최대 지름)
        var maxR = 0.05f;
        for (var i = 0; i < n; i++) maxR = MathF.Max(maxR, _r[i]);
        var cell = 2 * maxR;
        BuildGrid(_px, _py, _pz, n, cell);
        var inv = 1f / cell;
        for (var i = 0; i < n; i++)
        {
            int ix = (int)MathF.Floor(_px[i] * inv), iy = (int)MathF.Floor(_py[i] * inv), iz = (int)MathF.Floor(_pz[i] * inv);
            for (var dz = -1; dz <= 1; dz++)
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                        for (var j = _head[Bucket(ix + dx, iy + dy, iz + dz)]; j >= 0; j = _next[j])
                            if (j > i) Collide(i, j);
        }

        // 지면(높이장) 접촉
        for (var i = 0; i < n; i++)
        {
            if (_inv[i] == 0) continue;
            var g = Ground(_px[i], _pz[i]);
            var pen = g + _r[i] - _py[i];
            if (pen <= 0) continue;
            var nrm = GroundNormal(_px[i], _pz[i]);
            _px[i] += nrm.X * pen; _py[i] += nrm.Y * pen; _pz[i] += nrm.Z * pen;
            var vn = _vx[i] * nrm.X + _vy[i] * nrm.Y + _vz[i] * nrm.Z;
            if (vn < 0)
            {
                var e = vn < -0.6f ? _rest[i] : 0f;   // 저속 접촉은 비탄성(정지 안정)
                var jn = -(1 + e) * vn;
                _vx[i] += jn * nrm.X; _vy[i] += jn * nrm.Y; _vz[i] += jn * nrm.Z;
                // 쿨롱 마찰: 접선 속도 감쇠(|Δvt| ≤ μ·jn)
                float tx = _vx[i] - (vn + jn) * nrm.X, ty = _vy[i] - (vn + jn) * nrm.Y, tz = _vz[i] - (vn + jn) * nrm.Z;
                var vt = MathF.Sqrt(tx * tx + ty * ty + tz * tz);
                if (vt > 1e-6f)
                {
                    var k = MathF.Min(1f, Friction * jn / vt);
                    _vx[i] -= tx * k; _vy[i] -= ty * k; _vz[i] -= tz * k;
                }
                if (-vn > 0.8f) Impact(_px[i], _py[i] - _r[i], _pz[i], 0.5f * vn * vn / MathF.Max(_inv[i], 1e-6f), ImpactKind.Ground);
            }
            _maxPen = MathF.Max(_maxPen, pen);
            Interlocked.Increment(ref _contacts);
        }
    }

    private void Collide(int i, int j)
    {
        float dx = _px[j] - _px[i], dy = _py[j] - _py[i], dz = _pz[j] - _pz[i];
        var rr = _r[i] + _r[j];
        var d2 = dx * dx + dy * dy + dz * dz;
        if (d2 >= rr * rr || d2 < 1e-12f) return;
        var wsum = _inv[i] + _inv[j];
        if (wsum == 0) return;
        var d = MathF.Sqrt(d2);
        float nx = dx / d, ny = dy / d, nz = dz / d;
        var pen = rr - d;
        _maxPen = MathF.Max(_maxPen, pen);
        // 위치 보정(역질량 비례)
        var ci = pen * _inv[i] / wsum; var cj = pen * _inv[j] / wsum;
        _px[i] -= nx * ci; _py[i] -= ny * ci; _pz[i] -= nz * ci;
        _px[j] += nx * cj; _py[j] += ny * cj; _pz[j] += nz * cj;
        var vrel = (_vx[j] - _vx[i]) * nx + (_vy[j] - _vy[i]) * ny + (_vz[j] - _vz[i]) * nz;
        if (vrel >= 0) return;
        var e = MathF.Min(_rest[i], _rest[j]);
        var jn = -(1 + e) * vrel / wsum;
        _vx[i] -= jn * _inv[i] * nx; _vy[i] -= jn * _inv[i] * ny; _vz[i] -= jn * _inv[i] * nz;
        _vx[j] += jn * _inv[j] * nx; _vy[j] += jn * _inv[j] * ny; _vz[j] += jn * _inv[j] * nz;
        Interlocked.Increment(ref _contacts);
        if (-vrel > 0.5f) Impact(_px[i] + nx * _r[i], _py[i] + ny * _r[i], _pz[i] + nz * _r[i], 0.5f * jn * -vrel, ImpactKind.Body);
    }

    private void StepCloth(float dt)
    {
        // SIMD Verlet: next = c + (c − o)·k + a·dt² (y축만 중력)
        const float damp = 0.99f;
        VerletAxis(_cx, _ox, 0, damp);
        VerletAxis(_cy, _oy, GravityY * dt * dt, damp);
        VerletAxis(_cz, _oz, 0, damp);
        for (var it = 0; it < 6; it++)
        {
            for (var c = 0; c < _cons; c++)
            {
                int a = _ca[c], b = _cb[c];
                float dx = _cx[b] - _cx[a], dy = _cy[b] - _cy[a], dz = _cz[b] - _cz[a];
                var d = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                if (d < 1e-7f) continue;
                var k = 0.5f * (d - _cl[c]) / d;
                _cx[a] += dx * k; _cy[a] += dy * k; _cz[a] += dz * k;
                _cx[b] -= dx * k; _cy[b] -= dy * k; _cz[b] -= dz * k;
            }
            Pin(0, _pinA); Pin(ClothN - 1, _pinB);
        }
        // 충돌: 강체 구(밀어냄) · 지면
        for (var p = 0; p < ClothCount; p++)
        {
            for (var b = 0; b < _n; b++)
            {
                float dx = _cx[p] - _px[b], dy = _cy[p] - _py[b], dz = _cz[p] - _pz[b];
                var rr = _r[b] + 0.01f;
                var d2 = dx * dx + dy * dy + dz * dz;
                if (d2 >= rr * rr || d2 < 1e-12f) continue;
                var d = MathF.Sqrt(d2);
                var s = rr / d;
                _cx[p] = _px[b] + dx * s; _cy[p] = _py[b] + dy * s; _cz[p] = _pz[b] + dz * s;
            }
            var g = Ground(_cx[p], _cz[p]) + 0.005f;
            if (_cy[p] < g) { _cy[p] = g; _oy[p] = g; }
        }
        Pin(0, _pinA); Pin(ClothN - 1, _pinB);
    }

    private void Pin(int k, Vector3 p) { _cx[k] = _ox[k] = p.X; _cy[k] = _oy[k] = p.Y; _cz[k] = _oz[k] = p.Z; }

    private void VerletAxis(float[] c, float[] o, float acc, float k)
    {
        var cs = c.AsSpan(0, ClothCount); var os = o.AsSpan(0, ClothCount); var ts = _tmp.AsSpan(0, ClothCount);
        var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var vc = MemoryMarshal.Cast<float, Vector<float>>(cs);
            var vo = MemoryMarshal.Cast<float, Vector<float>>(os);
            var vt = MemoryMarshal.Cast<float, Vector<float>>(ts);
            var vk = new Vector<float>(k); var va = new Vector<float>(acc);
            for (var v = 0; v < vc.Length; v++)
            {
                vt[v] = vc[v] + (vc[v] - vo[v]) * vk + va;
                vo[v] = vc[v];
                vc[v] = vt[v];
            }
            i = vc.Length * Vector<float>.Count;
        }
        for (; i < cs.Length; i++)
        {
            var nx = cs[i] + (cs[i] - os[i]) * k + acc;
            os[i] = cs[i]; cs[i] = nx;
        }
    }

    private void StepFluid(float dt)
    {
        // 예측: v += g·dt · q = p + v·dt
        for (var p = 0; p < FluidCount; p++)
        {
            _fvy[p] += GravityY * dt;
            _qx[p] = _fx[p] + _fvx[p] * dt; _qy[p] = _fy[p] + _fvy[p] * dt; _qz[p] = _fz[p] + _fvz[p] * dt;
            ClampBox(p);
        }
        BuildGrid(_qx, _qy, _qz, FluidCount, H);
        const float eps = 40f;
        for (var it = 0; it < 3; it++)
        {
            for (var p = 0; p < FluidCount; p++)
            {
                var rho = Density(p);
                _rho[p] = rho;
                var c = rho / _rho0 - 1f;
                if (c <= 0) { _lambda[p] = 0; continue; }   // 단방향(압축만 교정 · 군집 방지)
                Vector3 gi = default; var sum = 0f;
                ForNeighbors(p, out _, ref gi, ref sum);
                sum += gi.LengthSquared();
                _lambda[p] = -c / (sum + eps);
            }
            for (var p = 0; p < FluidCount; p++)
            {
                Vector3 dp = default;
                var ix = (int)MathF.Floor(_qx[p] / H); var iy = (int)MathF.Floor(_qy[p] / H); var iz = (int)MathF.Floor(_qz[p] / H);
                for (var dz = -1; dz <= 1; dz++)
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++)
                            for (var q = _head[Bucket(ix + dx, iy + dy, iz + dz)]; q >= 0; q = _next[q])
                            {
                                if (q == p) continue;
                                float rx = _qx[p] - _qx[q], ry = _qy[p] - _qy[q], rz = _qz[p] - _qz[q];
                                var r = MathF.Sqrt(rx * rx + ry * ry + rz * rz);
                                if (r >= H || r < 1e-6f) continue;
                                var g = SpikyGrad * (H - r) * (H - r) / r * (_lambda[p] + _lambda[q]) / _rho0;
                                dp += new Vector3(rx * g, ry * g, rz * g);
                            }
                var len = dp.Length();
                if (len > H * 0.25f) dp *= H * 0.25f / len;
                _qx[p] += dp.X; _qy[p] += dp.Y; _qz[p] += dp.Z;
                ClampBox(p);
            }
        }
        var invDt = 1f / dt;
        for (var p = 0; p < FluidCount; p++)
        {
            _fvx[p] = (_qx[p] - _fx[p]) * invDt * 0.999f; _fvy[p] = (_qy[p] - _fy[p]) * invDt * 0.999f; _fvz[p] = (_qz[p] - _fz[p]) * invDt * 0.999f;
            _fx[p] = _qx[p]; _fy[p] = _qy[p]; _fz[p] = _qz[p];
        }
    }

    private float Density(int p)
    {
        var rho = 0f;
        var ix = (int)MathF.Floor(_qx[p] / H); var iy = (int)MathF.Floor(_qy[p] / H); var iz = (int)MathF.Floor(_qz[p] / H);
        for (var dz = -1; dz <= 1; dz++)
            for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                    for (var q = _head[Bucket(ix + dx, iy + dy, iz + dz)]; q >= 0; q = _next[q])
                    {
                        var r2 = Sq(_qx[p] - _qx[q]) + Sq(_qy[p] - _qy[q]) + Sq(_qz[p] - _qz[q]);
                        if (r2 < H * H) { var w = H * H - r2; rho += Poly6 * w * w * w; }
                    }
        return rho;
    }

    /// <summary>∇C 누적: gi = Σ∇W/ρ0(자기 기울기) · sum = Σ|∇W/ρ0|²(이웃 기울기).</summary>
    private void ForNeighbors(int p, out int count, ref Vector3 gi, ref float sum)
    {
        count = 0;
        var ix = (int)MathF.Floor(_qx[p] / H); var iy = (int)MathF.Floor(_qy[p] / H); var iz = (int)MathF.Floor(_qz[p] / H);
        for (var dz = -1; dz <= 1; dz++)
            for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                    for (var q = _head[Bucket(ix + dx, iy + dy, iz + dz)]; q >= 0; q = _next[q])
                    {
                        if (q == p) continue;
                        float rx = _qx[p] - _qx[q], ry = _qy[p] - _qy[q], rz = _qz[p] - _qz[q];
                        var r = MathF.Sqrt(rx * rx + ry * ry + rz * rz);
                        if (r >= H || r < 1e-6f) continue;
                        var g = SpikyGrad * (H - r) * (H - r) / r / _rho0;
                        var gv = new Vector3(rx * g, ry * g, rz * g);
                        gi += gv;
                        sum += gv.LengthSquared();
                        count++;
                    }
    }

    private void ClampBox(int p)
    {
        var floor = MathF.Max(_boxMin.Y, Ground(_qx[p], _qz[p]));
        _qx[p] = Math.Clamp(_qx[p], _boxMin.X, _boxMax.X);
        _qy[p] = Math.Clamp(_qy[p], floor, MathF.Max(floor, _boxMax.Y));
        _qz[p] = Math.Clamp(_qz[p], _boxMin.Z, _boxMax.Z);
    }

    private void BuildGrid(float[] x, float[] y, float[] z, int n, float cell)
    {
        _head.AsSpan().Fill(-1);
        var inv = 1f / cell;
        for (var i = 0; i < n; i++)
        {
            var b = Bucket((int)MathF.Floor(x[i] * inv), (int)MathF.Floor(y[i] * inv), (int)MathF.Floor(z[i] * inv));
            _next[i] = _head[b]; _head[b] = i;
        }
    }

    private static int Bucket(int x, int y, int z) => (int)(((uint)x * 73856093u ^ (uint)y * 19349663u ^ (uint)z * 83492791u) & (GridBuckets - 1));

    private static float Sq(float v) => v * v;

    // ══════════════════════ SIMD 커널 ══════════════════════

    /// <summary>y += a·x (Vector&lt;float&gt; 본체 + 스칼라 꼬리).</summary>
    public static void Axpy(Span<float> y, ReadOnlySpan<float> x, float a)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated && y.Length >= Vector<float>.Count)
        {
            var vy = MemoryMarshal.Cast<float, Vector<float>>(y);
            var vx = MemoryMarshal.Cast<float, Vector<float>>(x);
            var va = new Vector<float>(a);
            for (var v = 0; v < vy.Length; v++) vy[v] += vx[v] * va;
            i = vy.Length * Vector<float>.Count;
        }
        for (; i < y.Length; i++) y[i] += a * x[i];
    }

    /// <summary>y += a (mask[i] &gt; 0 인 원소만 — 정적 바디 제외).</summary>
    private static void AddMasked(Span<float> y, ReadOnlySpan<float> mask, float a)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated && y.Length >= Vector<float>.Count)
        {
            var vy = MemoryMarshal.Cast<float, Vector<float>>(y);
            var vm = MemoryMarshal.Cast<float, Vector<float>>(mask);
            var va = new Vector<float>(a);
            for (var v = 0; v < vy.Length; v++) vy[v] += Vector.ConditionalSelect(Vector.GreaterThan(vm[v], Vector<float>.Zero), va, Vector<float>.Zero);
            i = vy.Length * Vector<float>.Count;
        }
        for (; i < y.Length; i++) if (mask[i] > 0) y[i] += a;
    }

    public float KineticEnergy()
    {
        var e = 0f;
        for (var i = 0; i < _n; i++) if (_inv[i] > 0) e += 0.5f / _inv[i] * (_vx[i] * _vx[i] + _vy[i] * _vy[i] + _vz[i] * _vz[i]);
        return e;
    }

    // ══════════════════════ 음향 반사 메시 ══════════════════════

    private void Impact(float x, float y, float z, float energy, ImpactKind kind)
    {
        var seq = Interlocked.Increment(ref _impactSeq) - 1;
        _impacts[seq % MaxImpacts] = new ImpactEvent(_time, x, y, z, energy, kind);
    }

    /// <summary>최근 임팩트(최신 우선) 복사. 반환 = 복사 수.</summary>
    public int CopyImpacts(Span<ImpactEvent> dst)
    {
        var total = Interlocked.Read(ref _impactSeq);
        var n = (int)Math.Min(Math.Min(total, MaxImpacts), dst.Length);
        for (var k = 0; k < n; k++) dst[k] = _impacts[(total - 1 - k) % MaxImpacts];
        return n;
    }

    private void AddPatch(Vector3 p, Vector3 n, float area, float absorb, ImpactKind kind)
    {
        if (_patches >= MaxPatches || area <= 0) return;
        var i = _patches++;
        _apx[i] = p.X; _apy[i] = p.Y; _apz[i] = p.Z;
        _anx[i] = n.X; _any[i] = n.Y; _anz[i] = n.Z;
        _aarea[i] = area; _aabs[i] = Math.Clamp(absorb, 0f, 1f); _akind[i] = (byte)kind;
    }

    public (Vector3 P, Vector3 N, float Area, float Absorption) Patch(int i) =>
        (new Vector3(_apx[i], _apy[i], _apz[i]), new Vector3(_anx[i], _any[i], _anz[i]), _aarea[i], _aabs[i]);

    /// <summary>
    /// 현재 충돌 기하 → 반사 메시(지면 3×3 패치 · 외피 6면 · 강체 · 천 4분면 · 수면) → 초기 반사 탭(지연 오름차순) + Sabine RT60. 무할당. 반환 = 탭 수.
    /// </summary>
    public int BuildAcousticMesh(Vector3 source, Vector3 listener)
    {
        using var guard = ZeroGcRegionScope.Enter("Physics.Acoustic");
        _patches = 0;
        var mid = (source + listener) * 0.5f;
        const float tile = 4f;
        for (var j = -1; j <= 1; j++)
            for (var i = -1; i <= 1; i++)
            {
                float x = mid.X + i * tile, z = mid.Z + j * tile;
                AddPatch(new Vector3(x, Ground(x, z), z), GroundNormal(x, z), tile * tile, Field?.AbsorptionAt(x, z) ?? 0.3f, ImpactKind.Ground);
            }
        if (Enclosure is { } enc)
        {
            var c = (enc.Min + enc.Max) * 0.5f; var s = enc.Max - enc.Min;
            AddPatch(new Vector3(c.X, enc.Max.Y, c.Z), -Vector3.UnitY, s.X * s.Z, enc.Absorption, ImpactKind.Ground);
            AddPatch(new Vector3(enc.Min.X, c.Y, c.Z), Vector3.UnitX, s.Y * s.Z, enc.Absorption, ImpactKind.Ground);
            AddPatch(new Vector3(enc.Max.X, c.Y, c.Z), -Vector3.UnitX, s.Y * s.Z, enc.Absorption, ImpactKind.Ground);
            AddPatch(new Vector3(c.X, c.Y, enc.Min.Z), Vector3.UnitZ, s.X * s.Y, enc.Absorption, ImpactKind.Ground);
            AddPatch(new Vector3(c.X, c.Y, enc.Max.Z), -Vector3.UnitZ, s.X * s.Y, enc.Absorption, ImpactKind.Ground);
        }
        for (var b = 0; b < _n && _patches < MaxPatches - 6; b++)
        {
            var center = new Vector3(_px[b], _py[b], _pz[b]);
            var toMid = mid - center;
            var len = toMid.Length();
            if (len < 1e-4f) continue;
            var nrm = toMid / len;
            AddPatch(center + nrm * _r[b], nrm, MathF.PI * _r[b] * _r[b], 0.1f, ImpactKind.Body);
        }
        if (ClothEnabled)
        {
            const int Q = ClothN / 2;
            for (var qj = 0; qj < 2; qj++)
                for (var qi = 0; qi < 2; qi++)
                {
                    Vector3 sum = default, nsum = default; var area = 0f; var cnt = 0;
                    for (var j = qj * Q; j < qj * Q + Q - (qj == 1 ? 1 : 0); j++)
                        for (var i = qi * Q; i < qi * Q + Q - (qi == 1 ? 1 : 0); i++)
                        {
                            var k = j * ClothN + i;
                            var a = ClothPoint(k); var bx = ClothPoint(k + 1); var c = ClothPoint(k + ClothN);
                            var cr = Vector3.Cross(bx - a, c - a);
                            nsum += cr; area += cr.Length(); sum += a; cnt++;
                        }
                    if (cnt == 0 || nsum.LengthSquared() < 1e-12f) continue;
                    var nrm = Vector3.Normalize(nsum);
                    var p = sum / cnt;
                    if (Vector3.Dot(nrm, mid - p) < 0) nrm = -nrm;   // 양면 천: 청취 측 면
                    AddPatch(p, nrm, area, 0.5f, ImpactKind.Cloth);
                }
        }
        if (FluidEnabled)
        {
            var top = float.MinValue;
            for (var p = 0; p < FluidCount; p++) top = MathF.Max(top, _fy[p]);
            var s = _boxMax - _boxMin;
            AddPatch(new Vector3((_boxMin.X + _boxMax.X) * 0.5f, top, (_boxMin.Z + _boxMax.Z) * 0.5f), Vector3.UnitY, s.X * s.Z, 0.02f, ImpactKind.Fluid);
        }

        // 초기 반사(패치 중심 경유 근사 이미지 소스) — 양쪽이 패치 앞면일 때만
        _tapCount = 0;
        var direct = Vector3.Distance(source, listener);
        for (var i = 0; i < _patches && _tapCount < MaxTaps; i++)
        {
            var p = new Vector3(_apx[i], _apy[i], _apz[i]);
            var nrm = new Vector3(_anx[i], _any[i], _anz[i]);
            var a = source - p; var b = listener - p;
            float da = a.Length(), db = b.Length();
            if (da < 1e-3f || db < 1e-3f) continue;
            var ca = Vector3.Dot(a, nrm) / da; var cb = Vector3.Dot(b, nrm) / db;
            if (ca <= 0 || cb <= 0) continue;
            var gain = (1f - _aabs[i]) * _aarea[i] * 0.5f * (ca + cb) / (4f * MathF.PI * da * db);
            gain = MathF.Min(gain, 1f) * MathF.Max(direct, 1f);   // 직접음(1/direct) 대비 상대 이득
            var delay = (da + db - direct) / SpeedOfSound * 1000f;
            if (delay < 0) delay = 0;
            var t = _tapCount++;
            while (t > 0 && _taps[t - 1].DelayMs > delay) { _taps[t] = _taps[t - 1]; t--; }
            _taps[t] = new ReverbTap(delay, MathF.Min(gain, 1f), i);
        }

        // Sabine RT60 = 0.161·V / A (V = 외피 또는 메시 경계 상자 · A = Σ면적·흡음 + 개방면)
        float vol, absorb = 0;
        for (var i = 0; i < _patches; i++) absorb += _aarea[i] * _aabs[i];
        if (Enclosure is { } e2) { var s = e2.Max - e2.Min; vol = MathF.Abs(s.X * s.Y * s.Z); }
        else
        {
            Vector3 mn = new(float.MaxValue), mx = new(float.MinValue);
            for (var i = 0; i < _patches; i++) { var p = new Vector3(_apx[i], _apy[i], _apz[i]); mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
            var s = Vector3.Max(mx - mn, new Vector3(1f));
            vol = s.X * MathF.Max(s.Y, 3f) * s.Z;
            absorb += s.X * s.Z + 2 * MathF.Max(s.Y, 3f) * (s.X + s.Z);   // 야외: 하늘·측면 개방면 = 완전 흡음
        }
        Rt60 = 0.161f * vol / MathF.Max(absorb, 1e-3f);
        return _tapCount;
    }

    /// <summary>임펄스 응답 렌더: 직접음 + 초기 반사 탭 + RT60 지수 감쇠 확산 꼬리(결정적 잡음). 무할당.</summary>
    public void RenderImpulseResponse(Span<float> ir, int sampleRate)
    {
        ir.Clear();
        if (ir.IsEmpty) return;
        ir[0] = 1f;
        var lastTap = 0;
        var meanGain = 0f;
        for (var t = 0; t < _tapCount; t++)
        {
            var s = (int)(_taps[t].DelayMs * 0.001f * sampleRate);
            if ((uint)s < (uint)ir.Length) ir[s] += _taps[t].Gain;
            lastTap = Math.Max(lastTap, s);
            meanGain += _taps[t].Gain;
        }
        if (_tapCount > 0) meanGain /= _tapCount;
        var decay = -6.9078f / MathF.Max(Rt60, 0.01f) / sampleRate;   // ln(10⁻³)
        ulong rng = 0x1672;
        for (var s = Math.Max(1, lastTap); s < ir.Length; s++)
        {
            rng ^= rng << 13; rng ^= rng >> 7; rng ^= rng << 17;
            var noise = ((rng >> 40) * (1f / (1 << 24)) - 0.5f) * 2f;
            ir[s] += noise * meanGain * 0.5f * MathF.Exp(decay * (s - lastTap));
        }
    }
}
