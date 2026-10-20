using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Neural3d;

namespace PromptFlowStudio.Services.GenerativeVfx;

public enum FluidPhase : byte { Liquid = 0, Gas = 1 }

/// <summary>
/// [PromptFlow Studio v16.81.0] 🌊 실시간 신경망 파티클 유체 역학 엔진(SoA · 무할당 · Vector&lt;float&gt; SIMD).
/// 이중 밀도 완화(Clavet 2005 · 야코비 대칭형): 중력/부력 · 예측 [SIMD] → 균일 격자 카운팅 정렬 + SoA 공간 재배열(캐시 지역성) →
/// 이웃 리스트 1회 수집(밀도 · 근밀도 동시) → 압력/근압력 변위 + 표면장력 응집 → 속도 복원 [SIMD] →
/// 점성 임펄스(같은 이웃 리스트 재사용 · 선형 σ + 이차 β · 신경망 에디 점성 2-4-1 보정) → 도메인 벽 · 외부 충돌기.
/// 액체(중력) · 가스(부력 · 수명)는 같은 상끼리만 상호작용. 3DGS 씬으로 스플랫 내보내기(컴포지팅).
/// </summary>
public sealed class NeuralParticleFluidDynamicsEngine
{
    public const int MaxCapacity = 1 << 20;

    // ── 입자 SoA(공개 · 충돌 가드 직접 접근) ──
    public float[] X = Array.Empty<float>(), Y = Array.Empty<float>(), Z = Array.Empty<float>();
    public float[] VX = Array.Empty<float>(), VY = Array.Empty<float>(), VZ = Array.Empty<float>();
    public float[] Life = Array.Empty<float>(), GScale = Array.Empty<float>(), Rho = Array.Empty<float>();
    public byte[] Phase = Array.Empty<byte>();
    private float[] _px = Array.Empty<float>(), _py = Array.Empty<float>(), _pz = Array.Empty<float>();
    private float[] _dx = Array.Empty<float>(), _dy = Array.Empty<float>(), _dz = Array.Empty<float>();
    private float[] _rhoN = Array.Empty<float>(), _tmp = Array.Empty<float>();
    private byte[] _tmpB = Array.Empty<byte>();
    private int[] _nbr = Array.Empty<int>(), _nbrCount = Array.Empty<int>(), _tmpI = Array.Empty<int>();
    private int[] _cell = Array.Empty<int>(), _sorted = Array.Empty<int>(), _cellStart = Array.Empty<int>(), _cellCount = Array.Empty<int>();
    private int _count, _nx, _ny, _nz;
    /// <summary>입자당 이웃 리스트 상한(초과분은 절단 · <see cref="NeighborOverflow"/>).</summary>
    public const int MaxNeighbors = 64;
    public const float ReferenceDt = 1f / 120f;
    private float _invH;
    private ulong _rng = 0xF1_0816_81UL;

    public int Capacity => Life.Length;
    public int Count => _count;
    public Vector3 DomainMin { get; private set; } = new(-2f, 0f, -2f);
    public Vector3 DomainMax { get; private set; } = new(2f, 3f, 2f);
    public float H { get; private set; } = 0.12f;

    // ── 물성(제어 평면) ──
    public float Gravity { get; set; } = -9.81f;
    public float RestDensity { get; set; } = 2.6f;
    public float Stiffness { get; set; } = 60f;
    public float NearStiffness { get; set; } = 240f;
    /// <summary>선형 점성 σ(0..).</summary>
    public float Viscosity { get; set; } = 0.6f;
    /// <summary>이차 점성 β.</summary>
    public float ViscosityQuad { get; set; } = 0.25f;
    /// <summary>표면장력(응집) 계수.</summary>
    public float SurfaceTension { get; set; } = 1.2f;
    public float GasBuoyancy { get; set; } = 0.25f;
    public float WallRestitution { get; set; } = 0.25f;
    public bool NeuralClosure { get; set; } = true;
    public int Substeps { get; set; } = 2;
    public InteractiveVfxCollisionSplashGuard? Collider { get; set; }

    // ── 계측 ──
    public long Steps { get; private set; }
    public double LastStepMs { get; private set; }
    public double MaxStepMs { get; private set; }
    public float MeanDensity { get; private set; }
    public float MaxSpeed { get; private set; }
    public float KineticEnergy { get; private set; }
    public float MeanNeighbors { get; private set; }
    public int LiquidCount { get; private set; }
    public int GasCount { get; private set; }
    public long Emitted { get; private set; }
    public long NeighborOverflow { get; private set; }
    public long Expired { get; private set; }
    public string? LastAction { get; private set; }

    public NeuralParticleFluidDynamicsEngine(int capacity = 32768) => Configure(capacity, DomainMin, DomainMax, H);

    /// <summary>제어 평면: 용량 · 도메인 · 평활 반경(버퍼/격자 1회 할당).</summary>
    public void Configure(int capacity, Vector3 min, Vector3 max, float h)
    {
        capacity = Math.Clamp(capacity, 16, MaxCapacity);
        if (h <= 0f || max.X <= min.X || max.Y <= min.Y || max.Z <= min.Z) throw new ArgumentOutOfRangeException(nameof(h));
        DomainMin = min; DomainMax = max; H = h; _invH = 1f / h;
        _nx = Math.Max(1, (int)MathF.Ceiling((max.X - min.X) * _invH)); _ny = Math.Max(1, (int)MathF.Ceiling((max.Y - min.Y) * _invH)); _nz = Math.Max(1, (int)MathF.Ceiling((max.Z - min.Z) * _invH));
        if (Capacity != capacity)
        {
            X = new float[capacity]; Y = new float[capacity]; Z = new float[capacity];
            VX = new float[capacity]; VY = new float[capacity]; VZ = new float[capacity];
            Life = new float[capacity]; GScale = new float[capacity]; Rho = new float[capacity]; Phase = new byte[capacity];
            _px = new float[capacity]; _py = new float[capacity]; _pz = new float[capacity];
            _dx = new float[capacity]; _dy = new float[capacity]; _dz = new float[capacity]; _rhoN = new float[capacity];
            _cell = new int[capacity]; _sorted = new int[capacity];
            _tmp = new float[capacity]; _tmpB = new byte[capacity]; _tmpI = new int[capacity];
            _nbr = new int[capacity * MaxNeighbors]; _nbrCount = new int[capacity];
        }
        var cells = _nx * _ny * _nz;
        if (_cellStart.Length != cells) { _cellStart = new int[cells]; _cellCount = new int[cells]; }
        _count = 0;
        LastAction = "🌊 용량 " + capacity.ToString(System.Globalization.CultureInfo.InvariantCulture) + " · 격자 " + cells.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Clear() { _count = 0; Emitted = Expired = 0; Steps = 0; MaxStepMs = 0; }

    /// <summary>입자 1개 방출(가득 차면 false). 가스 수명 유한 · 액체 = ∞.</summary>
    public bool Emit(Vector3 p, Vector3 v, FluidPhase phase, float life = float.PositiveInfinity)
    {
        if (_count >= Capacity) return false;
        var i = _count++;
        X[i] = p.X; Y[i] = p.Y; Z[i] = p.Z; VX[i] = v.X; VY[i] = v.Y; VZ[i] = v.Z;
        Phase[i] = (byte)phase; Life[i] = phase == FluidPhase.Gas ? life : float.PositiveInfinity;
        GScale[i] = phase == FluidPhase.Liquid ? 1f : -GasBuoyancy;
        Rho[i] = RestDensity;
        Emitted++;
        return true;
    }

    /// <summary>격자 블록 방출(간격 0.5h · 지터). 반환 = 방출 수.</summary>
    public int EmitBlock(Vector3 min, Vector3 max, Vector3 v, FluidPhase phase, float life = float.PositiveInfinity)
    {
        var s = H * 0.5f; var n = 0;
        for (var z = min.Z; z < max.Z; z += s)
            for (var y = min.Y; y < max.Y; y += s)
                for (var x = min.X; x < max.X; x += s)
                {
                    var j = new Vector3(Rand() - 0.5f, Rand() - 0.5f, Rand() - 0.5f) * (s * 0.1f);
                    if (!Emit(new Vector3(x, y, z) + j, v, phase, life)) return n;
                    n++;
                }
        return n;
    }

    /// <summary>원판 노즐 방출(초당 rate · 이번 프레임 dt). 무할당.</summary>
    public int EmitNozzle(Vector3 center, Vector3 dir, float radius, float speed, int count, FluidPhase phase, float life = float.PositiveInfinity)
    {
        dir = Vector3.Normalize(dir);
        var t = Vector3.Normalize(Vector3.Cross(dir, MathF.Abs(dir.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY)); var b = Vector3.Cross(dir, t);
        var n = 0;
        for (var k = 0; k < count; k++)
        {
            var a = Rand() * MathF.Tau; var r = radius * MathF.Sqrt(Rand());
            if (!Emit(center + t * (r * MathF.Cos(a)) + b * (r * MathF.Sin(a)), dir * speed, phase, life)) break;
            n++;
        }
        return n;
    }

    public Vector3 Position(int i) => new(X[i], Y[i], Z[i]);
    public Vector3 Velocity(int i) => new(VX[i], VY[i], VZ[i]);

    /// <summary>신경망 에디 점성 클로저(2-4-1 · tanh · 고정 가중치): (ρ/ρ₀−1, |v|/4) → 점성 배율 0.5..2.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float EddyViscosity(float densityRatio, float speed)
    {
        var a = Math.Clamp(densityRatio - 1f, -1f, 1f); var s = MathF.Min(speed * 0.25f, 2f);
        var h0 = MathF.Tanh(1.6f * s - 0.8f); var h1 = MathF.Tanh(-2.0f * a + 0.1f);
        var h2 = MathF.Tanh(1.2f * s + 1.1f * a - 0.4f); var h3 = MathF.Tanh(0.7f * a - 0.9f * s);
        var o = MathF.Tanh(0.65f * h0 + 0.35f * h1 + 0.45f * h2 - 0.3f * h3 - 0.1f);
        return MathF.Pow(2f, o); // 0.5..2
    }

    /// <summary>핫패스: 1 프레임(서브스텝 포함 · 무할당).</summary>
    public void Step(float frameDt)
    {
        using var guard = ZeroGcRegionScope.Enter("Fluid.Step");
        var t0 = Stopwatch.GetTimestamp();
        var sub = Math.Clamp(Substeps, 1, 8);
        var dt = frameDt / sub;
        for (var s = 0; s < sub; s++) SubStep(dt);
        Stats();
        Steps++;
        LastStepMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        if (LastStepMs > MaxStepMs) MaxStepMs = LastStepMs;
    }

    private void SubStep(float dt)
    {
        Expire(dt);
        var n = _count;
        if (n == 0) return;
        AddScaled(VY.AsSpan(0, n), GScale.AsSpan(0, n), Gravity * dt);
        Advance(X.AsSpan(0, n), VX.AsSpan(0, n), _px.AsSpan(0, n), dt);
        Advance(Y.AsSpan(0, n), VY.AsSpan(0, n), _py.AsSpan(0, n), dt);
        Advance(Z.AsSpan(0, n), VZ.AsSpan(0, n), _pz.AsSpan(0, n), dt);
        BuildGrid();
        Reorder();
        Gather();
        Relax(dt);
        var inv = 1f / dt;
        VelocityFrom(VX.AsSpan(0, n), X.AsSpan(0, n), _px.AsSpan(0, n), inv);
        VelocityFrom(VY.AsSpan(0, n), Y.AsSpan(0, n), _py.AsSpan(0, n), inv);
        VelocityFrom(VZ.AsSpan(0, n), Z.AsSpan(0, n), _pz.AsSpan(0, n), inv);
        Viscosity_(dt);
        Walls();
        Collider?.Resolve(this, dt);
    }

    private void Expire(float dt)
    {
        var w = 0;
        for (var i = 0; i < _count; i++)
        {
            var life = Life[i] - dt;
            if (life <= 0f) { Expired++; continue; }
            if (w != i)
            {
                X[w] = X[i]; Y[w] = Y[i]; Z[w] = Z[i]; VX[w] = VX[i]; VY[w] = VY[i]; VZ[w] = VZ[i];
                GScale[w] = GScale[i]; Rho[w] = Rho[i]; Phase[w] = Phase[i];
            }
            Life[w] = life;
            w++;
        }
        _count = w;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CellOf(float x, float y, float z)
    {
        var cx = Math.Clamp((int)((x - DomainMin.X) * _invH), 0, _nx - 1);
        var cy = Math.Clamp((int)((y - DomainMin.Y) * _invH), 0, _ny - 1);
        var cz = Math.Clamp((int)((z - DomainMin.Z) * _invH), 0, _nz - 1);
        return (cz * _ny + cy) * _nx + cx;
    }

    /// <summary>균일 격자 카운팅 정렬(무할당).</summary>
    private void BuildGrid()
    {
        var n = _count;
        Array.Clear(_cellCount);
        for (var i = 0; i < n; i++) { var c = CellOf(X[i], Y[i], Z[i]); _cell[i] = c; _cellCount[c]++; }
        var acc = 0;
        for (var c = 0; c < _cellStart.Length; c++) { _cellStart[c] = acc; acc += _cellCount[c]; _cellCount[c] = 0; }
        for (var i = 0; i < n; i++) { var c = _cell[i]; _sorted[_cellStart[c] + _cellCount[c]++] = i; }
    }

    /// <summary>SoA 를 격자 순서로 재배열(배열 참조 교환 · 무할당) → 이후 i == 정렬 위치, 셀 범위 = 연속 인덱스.</summary>
    private void Reorder()
    {
        var n = _count;
        Permute(ref X, n); Permute(ref Y, n); Permute(ref Z, n);
        Permute(ref VX, n); Permute(ref VY, n); Permute(ref VZ, n);
        Permute(ref _px, n); Permute(ref _py, n); Permute(ref _pz, n);
        Permute(ref Life, n); Permute(ref GScale, n); Permute(ref Rho, n); Permute(ref _rhoN, n);
        for (var k = 0; k < n; k++) _tmpB[k] = Phase[_sorted[k]];
        (Phase, _tmpB) = (_tmpB, Phase);
        for (var k = 0; k < n; k++) _tmpI[k] = _cell[_sorted[k]];
        (_cell, _tmpI) = (_tmpI, _cell);
    }

    private void Permute(ref float[] a, int n)
    {
        var t = _tmp; var idx = _sorted;
        for (var k = 0; k < n; k++) t[k] = a[idx[k]];
        _tmp = a; a = t;
    }

    /// <summary>이웃 리스트 수집 + 밀도 · 근밀도(정렬 후 셀 범위 = 연속 인덱스).</summary>
    private void Gather()
    {
        var n = _count; var h = H; var h2 = h * h; var invH = _invH;
        long neighbors = 0;
        for (var i = 0; i < n; i++)
        {
            float xi = X[i], yi = Y[i], zi = Z[i]; var ph = Phase[i];
            float rho = 0, rhoN = 0; var cnt = 0; var bas = i * MaxNeighbors;
            var ci = _cell[i]; var cx = ci % _nx; var cy = ci / _nx % _ny; var cz = ci / (_nx * _ny);
            for (var gz = Math.Max(cz - 1, 0); gz <= Math.Min(cz + 1, _nz - 1); gz++)
                for (var gy = Math.Max(cy - 1, 0); gy <= Math.Min(cy + 1, _ny - 1); gy++)
                {
                    var row = (gz * _ny + gy) * _nx;
                    var s0 = _cellStart[row + Math.Max(cx - 1, 0)]; var c1 = row + Math.Min(cx + 1, _nx - 1); var s1 = _cellStart[c1] + _cellCount[c1];
                    for (var j = s0; j < s1; j++)
                    {
                        if (j == i || Phase[j] != ph) continue;
                        float rx = X[j] - xi, ry = Y[j] - yi, rz = Z[j] - zi;
                        var r2 = rx * rx + ry * ry + rz * rz;
                        if (r2 >= h2) continue;
                        var q = 1f - MathF.Sqrt(r2) * invH;
                        rho += q * q; rhoN += q * q * q;
                        if (cnt < MaxNeighbors) _nbr[bas + cnt++] = j; else NeighborOverflow++;
                    }
                }
            Rho[i] = rho; _rhoN[i] = rhoN; _nbrCount[i] = cnt; neighbors += cnt;
        }
        MeanNeighbors = n > 0 ? neighbors / (float)n : 0f;
    }

    /// <summary>압력 변위 + 응집(야코비 대칭 · 이웃 리스트).</summary>
    private void Relax(float dt)
    {
        var n = _count; var invH = _invH; var k = Stiffness; var kn = NearStiffness; var rho0 = RestDensity; var st = SurfaceTension;
        // 강성은 기준 스텝(1/120 s)으로 정규화 — 서브스텝 수와 무관한 변위 규모
        var dt2 = ReferenceDt * ReferenceDt; var cap = 0.25f * H;
        for (var i = 0; i < n; i++)
        {
            float xi = X[i], yi = Y[i], zi = Z[i];
            var pi = k * (Rho[i] - rho0); var pni = kn * _rhoN[i];
            float ax = 0, ay = 0, az = 0;
            var bas = i * MaxNeighbors; var cnt = _nbrCount[i];
            for (var m = 0; m < cnt; m++)
            {
                var j = _nbr[bas + m];
                float rx = X[j] - xi, ry = Y[j] - yi, rz = Z[j] - zi;
                var r2 = rx * rx + ry * ry + rz * rz;
                float r, inv;
                if (r2 < 1e-12f) { r = 0f; inv = 1f; rx = ((i - j) & 1) == 0 ? 1f : -1f; ry = 0; rz = 0; } // 완전 겹침: 결정적 분리
                else { r = MathF.Sqrt(r2); inv = 1f / r; }
                rx *= inv; ry *= inv; rz *= inv;
                var q = 1f - r * invH;
                var pj = k * (Rho[j] - rho0); var pnj = kn * _rhoN[j];
                // 압력(반발 +) · 응집(인력 −)
                var d = 0.5f * dt2 * ((pi + pj) * q + (pni + pnj) * q * q) - dt2 * st * q * (1f - q);
                ax -= d * rx; ay -= d * ry; az -= d * rz;
            }
            _dx[i] = Math.Clamp(ax, -cap, cap); _dy[i] = Math.Clamp(ay, -cap, cap); _dz[i] = Math.Clamp(az, -cap, cap);
        }
        AddScaled(X.AsSpan(0, n), _dx.AsSpan(0, n), 1f);
        AddScaled(Y.AsSpan(0, n), _dy.AsSpan(0, n), 1f);
        AddScaled(Z.AsSpan(0, n), _dz.AsSpan(0, n), 1f);
    }

    /// <summary>점성 임펄스(야코비 · 대칭 · 이웃 리스트 재사용) → 속도.</summary>
    private void Viscosity_(float dt)
    {
        var n = _count; var h = H; var h2 = h * h; var invH = _invH; var sigma = Viscosity; var beta = ViscosityQuad; var rho0 = RestDensity;
        if (sigma <= 0f && beta <= 0f) return;
        for (var i = 0; i < n; i++)
        {
            float xi = X[i], yi = Y[i], zi = Z[i], vxi = VX[i], vyi = VY[i], vzi = VZ[i];
            var mul = NeuralClosure ? EddyViscosity(Rho[i] / rho0, MathF.Sqrt(vxi * vxi + vyi * vyi + vzi * vzi)) : 1f;
            var sg = sigma * mul;
            float ax = 0, ay = 0, az = 0;
            var bas = i * MaxNeighbors; var cnt = _nbrCount[i];
            for (var m = 0; m < cnt; m++)
            {
                var j = _nbr[bas + m];
                float rx = X[j] - xi, ry = Y[j] - yi, rz = Z[j] - zi;
                var r2 = rx * rx + ry * ry + rz * rz;
                if (r2 >= h2 || r2 < 1e-12f) continue;
                var r = MathF.Sqrt(r2); var inv = 1f / r;
                rx *= inv; ry *= inv; rz *= inv;
                var q = 1f - r * invH;
                var u = (vxi - VX[j]) * rx + (vyi - VY[j]) * ry + (vzi - VZ[j]) * rz;
                if (u <= 0f) continue;
                var imp = 0.5f * dt * q * (sg * u + beta * u * u);
                ax -= imp * rx; ay -= imp * ry; az -= imp * rz;
            }
            _dx[i] = ax; _dy[i] = ay; _dz[i] = az;
        }
        var cap = 0.5f * h / dt;
        for (var i = 0; i < n; i++)
        {
            VX[i] += Math.Clamp(_dx[i], -cap, cap); VY[i] += Math.Clamp(_dy[i], -cap, cap); VZ[i] += Math.Clamp(_dz[i], -cap, cap);
        }
    }

    private void Walls()
    {
        var mn = DomainMin; var mx = DomainMax; var e = WallRestitution; var eps = H * 0.01f;
        for (var i = 0; i < _count; i++)
        {
            if (X[i] < mn.X) { X[i] = mn.X + eps; if (VX[i] < 0) VX[i] *= -e; } else if (X[i] > mx.X) { X[i] = mx.X - eps; if (VX[i] > 0) VX[i] *= -e; }
            if (Y[i] < mn.Y) { Y[i] = mn.Y + eps; if (VY[i] < 0) VY[i] *= -e; } else if (Y[i] > mx.Y) { Y[i] = mx.Y - eps; if (VY[i] > 0) VY[i] *= -e; }
            if (Z[i] < mn.Z) { Z[i] = mn.Z + eps; if (VZ[i] < 0) VZ[i] *= -e; } else if (Z[i] > mx.Z) { Z[i] = mx.Z - eps; if (VZ[i] > 0) VZ[i] *= -e; }
        }
    }

    private void Stats()
    {
        var n = _count;
        double rho = 0, ke = 0; var mx = 0f; int liq = 0;
        for (var i = 0; i < n; i++)
        {
            var s2 = VX[i] * VX[i] + VY[i] * VY[i] + VZ[i] * VZ[i];
            ke += s2; if (s2 > mx) mx = s2;
            if (Phase[i] == (byte)FluidPhase.Liquid) { liq++; rho += Rho[i]; }
        }
        LiquidCount = liq; GasCount = n - liq;
        MeanDensity = liq > 0 ? (float)(rho / liq) : 0f;
        MaxSpeed = MathF.Sqrt(mx);
        KineticEnergy = n > 0 ? (float)(0.5 * ke / n) : 0f;
    }

    /// <summary>3DGS 컴포지팅: 입자 → 가우시안(stride 샘플 · 속도 색). 용량 확보는 첫 호출만.</summary>
    public int WriteSplats(GaussianScene scene, int max)
    {
        var n = _count; if (n == 0 || max <= 0) { scene.Count = 0; scene.Version++; return 0; }
        var stride = Math.Max(1, (n + max - 1) / max);
        var m = (n + stride - 1) / stride;
        scene.EnsureCapacity(m);
        var r = H * 0.35f * MathF.Sqrt(stride);
        var w = 0;
        for (var i = 0; i < n; i += stride, w++)
        {
            scene.Pos[w * 3] = X[i]; scene.Pos[w * 3 + 1] = Y[i]; scene.Pos[w * 3 + 2] = Z[i];
            scene.Rot[w * 4] = 1f; scene.Rot[w * 4 + 1] = 0f; scene.Rot[w * 4 + 2] = 0f; scene.Rot[w * 4 + 3] = 0f;
            scene.Scale[w * 3] = r; scene.Scale[w * 3 + 1] = r; scene.Scale[w * 3 + 2] = r;
            var gas = Phase[i] == (byte)FluidPhase.Gas;
            scene.Opacity[w] = gas ? 0.25f : 0.8f;
            var sp = MathF.Min(1f, MathF.Sqrt(VX[i] * VX[i] + VY[i] * VY[i] + VZ[i] * VZ[i]) * 0.25f);
            var o = w * GaussianScene.ShStride;
            scene.Sh.AsSpan(o, GaussianScene.ShStride).Clear();
            scene.Sh[o] = GaussianScene.ToDc(gas ? 0.7f : 0.15f + 0.6f * sp);
            scene.Sh[o + 1] = GaussianScene.ToDc(gas ? 0.7f : 0.45f + 0.45f * sp);
            scene.Sh[o + 2] = GaussianScene.ToDc(gas ? 0.72f : 0.85f + 0.15f * sp);
        }
        scene.Count = w; scene.Version++;
        return w;
    }

    private float Rand()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17;
        return (_rng >> 40) / (float)(1 << 24);
    }

    // ── SIMD 커널(Vector<float> · 꼬리 스칼라) — 스칼라 레퍼런스와 연산 순서 동일(비트 일치) ──

    /// <summary>v += s · k.</summary>
    public static void AddScaled(Span<float> v, ReadOnlySpan<float> s, float k)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated && v.Length >= Vector<float>.Count)
        {
            var w = Vector<float>.Count; var kv = new Vector<float>(k);
            ref var pv = ref MemoryMarshal.GetReference(v); ref var ps = ref MemoryMarshal.GetReference(s);
            for (; i <= v.Length - w; i += w)
            {
                ref var at = ref Unsafe.As<float, byte>(ref Unsafe.Add(ref pv, i));
                var a = Unsafe.ReadUnaligned<Vector<float>>(ref at);
                var b = Unsafe.ReadUnaligned<Vector<float>>(ref Unsafe.As<float, byte>(ref Unsafe.Add(ref ps, i)));
                Unsafe.WriteUnaligned(ref at, a + b * kv);
            }
        }
        AddScaledScalar(v[i..], s[i..], k);
    }

    public static void AddScaledScalar(Span<float> v, ReadOnlySpan<float> s, float k)
    {
        for (var i = 0; i < v.Length; i++) v[i] = v[i] + s[i] * k;
    }

    /// <summary>prev = x · x += v · dt.</summary>
    public static void Advance(Span<float> x, ReadOnlySpan<float> v, Span<float> prev, float dt)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated && x.Length >= Vector<float>.Count)
        {
            var w = Vector<float>.Count; var dv = new Vector<float>(dt);
            ref var px = ref MemoryMarshal.GetReference(x); ref var pv = ref MemoryMarshal.GetReference(v); ref var pp = ref MemoryMarshal.GetReference(prev);
            for (; i <= x.Length - w; i += w)
            {
                ref var at = ref Unsafe.As<float, byte>(ref Unsafe.Add(ref px, i));
                var a = Unsafe.ReadUnaligned<Vector<float>>(ref at);
                Unsafe.WriteUnaligned(ref Unsafe.As<float, byte>(ref Unsafe.Add(ref pp, i)), a);
                Unsafe.WriteUnaligned(ref at, a + Unsafe.ReadUnaligned<Vector<float>>(ref Unsafe.As<float, byte>(ref Unsafe.Add(ref pv, i))) * dv);
            }
        }
        AdvanceScalar(x[i..], v[i..], prev[i..], dt);
    }

    public static void AdvanceScalar(Span<float> x, ReadOnlySpan<float> v, Span<float> prev, float dt)
    {
        for (var i = 0; i < x.Length; i++) { var a = x[i]; prev[i] = a; x[i] = a + v[i] * dt; }
    }

    /// <summary>v = (x − prev) · invDt.</summary>
    public static void VelocityFrom(Span<float> v, ReadOnlySpan<float> x, ReadOnlySpan<float> prev, float invDt)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated && v.Length >= Vector<float>.Count)
        {
            var w = Vector<float>.Count; var iv = new Vector<float>(invDt);
            ref var pv = ref MemoryMarshal.GetReference(v); ref var px = ref MemoryMarshal.GetReference(x); ref var pp = ref MemoryMarshal.GetReference(prev);
            for (; i <= v.Length - w; i += w)
            {
                var a = Unsafe.ReadUnaligned<Vector<float>>(ref Unsafe.As<float, byte>(ref Unsafe.Add(ref px, i)));
                var b = Unsafe.ReadUnaligned<Vector<float>>(ref Unsafe.As<float, byte>(ref Unsafe.Add(ref pp, i)));
                Unsafe.WriteUnaligned(ref Unsafe.As<float, byte>(ref Unsafe.Add(ref pv, i)), (a - b) * iv);
            }
        }
        VelocityFromScalar(v[i..], x[i..], prev[i..], invDt);
    }

    public static void VelocityFromScalar(Span<float> v, ReadOnlySpan<float> x, ReadOnlySpan<float> prev, float invDt)
    {
        for (var i = 0; i < v.Length; i++) v[i] = (x[i] - prev[i]) * invDt;
    }
}
