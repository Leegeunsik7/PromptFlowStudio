using System.Diagnostics;
using System.Numerics;

namespace PromptFlowStudio.Services.MassiveCrowd;

public enum CrowdLod : byte { Near, Mid, Far }

/// <summary>[v17.04.0] 군중 SIMD 커널 — Vector&lt;float&gt; 경로와 스칼라 경로가 원소별 비트 동일(축약 FMA 없음 · 비교/선택 클램프).</summary>
public static class CrowdSimd
{
    /// <summary>p += v·dt.</summary>
    public static void Integrate(Span<float> p, ReadOnlySpan<float> v, float dt)
    {
        var n = p.Length; var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var vd = new Vector<float>(dt);
            for (var w = Vector<float>.Count; i <= n - w; i += w) (new Vector<float>(p.Slice(i)) + new Vector<float>(v.Slice(i)) * vd).CopyTo(p.Slice(i));
        }
        for (; i < n; i++) p[i] += v[i] * dt;
    }

    public static void IntegrateScalar(Span<float> p, ReadOnlySpan<float> v, float dt)
    {
        for (var i = 0; i < p.Length; i++) p[i] += v[i] * dt;
    }

    /// <summary>x = clamp(x + (t − x)·r, 0, 1) — 감정 이완(전파 목표로).</summary>
    public static void Relax(Span<float> x, ReadOnlySpan<float> t, float r)
    {
        var n = x.Length; var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var vr = new Vector<float>(r); var one = Vector<float>.One;
            for (var w = Vector<float>.Count; i <= n - w; i += w)
            {
                var vx = new Vector<float>(x.Slice(i));
                var y = vx + (new Vector<float>(t.Slice(i)) - vx) * vr;
                y = Vector.ConditionalSelect(Vector.LessThan(y, Vector<float>.Zero), Vector<float>.Zero, y);
                y = Vector.ConditionalSelect(Vector.GreaterThan(y, one), one, y);
                y.CopyTo(x.Slice(i));
            }
        }
        for (; i < n; i++) { var y = x[i] + (t[i] - x[i]) * r; y = y < 0f ? 0f : y; x[i] = y > 1f ? 1f : y; }
    }

    public static void RelaxScalar(Span<float> x, ReadOnlySpan<float> t, float r)
    {
        for (var i = 0; i < x.Length; i++) { var y = x[i] + (t[i] - x[i]) * r; y = y < 0f ? 0f : y; x[i] = y > 1f ? 1f : y; }
    }

    /// <summary>d = a + b + c (3-탭 상자 합).</summary>
    public static void Add3(ReadOnlySpan<float> a, ReadOnlySpan<float> b, ReadOnlySpan<float> c, Span<float> d)
    {
        var n = d.Length; var i = 0;
        if (Vector.IsHardwareAccelerated)
            for (var w = Vector<float>.Count; i <= n - w; i += w) (new Vector<float>(a.Slice(i)) + new Vector<float>(b.Slice(i)) + new Vector<float>(c.Slice(i))).CopyTo(d.Slice(i));
        for (; i < n; i++) d[i] = a[i] + b[i] + c[i];
    }

    public static void Add3Scalar(ReadOnlySpan<float> a, ReadOnlySpan<float> b, ReadOnlySpan<float> c, Span<float> d)
    {
        for (var i = 0; i < d.Length; i++) d[i] = a[i] + b[i] + c[i];
    }

    /// <summary>d = (a − b)·k (중앙 차분 기울기).</summary>
    public static void SubScale(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> d, float k)
    {
        var n = d.Length; var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var vk = new Vector<float>(k);
            for (var w = Vector<float>.Count; i <= n - w; i += w) ((new Vector<float>(a.Slice(i)) - new Vector<float>(b.Slice(i))) * vk).CopyTo(d.Slice(i));
        }
        for (; i < n; i++) d[i] = (a[i] - b[i]) * k;
    }

    public static void SubScaleScalar(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> d, float k)
    {
        for (var i = 0; i < d.Length; i++) d[i] = (a[i] - b[i]) * k;
    }

    /// <summary>d += s·g (오디오 믹스).</summary>
    public static void ScaleAdd(Span<float> d, ReadOnlySpan<float> s, float g)
    {
        var n = d.Length; var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var vg = new Vector<float>(g);
            for (var w = Vector<float>.Count; i <= n - w; i += w) (new Vector<float>(d.Slice(i)) + new Vector<float>(s.Slice(i)) * vg).CopyTo(d.Slice(i));
        }
        for (; i < n; i++) d[i] += s[i] * g;
    }

    public static void ScaleAddScalar(Span<float> d, ReadOnlySpan<float> s, float g)
    {
        for (var i = 0; i < d.Length; i++) d[i] += s[i] * g;
    }
}

/// <summary>
/// [PromptFlow Studio v17.04.0] 👥 실시간 대규모 에이전틱 군중 시뮬레이션 엔진(최대 131,072 에이전트 · 무할당).
/// 400 m 스타디움(필드 타원 · 스탠드 고리 · 출구 8) → 2 m 셀 200×200 공간 해시(계수 정렬 · 셀 합계 동시 집계) → 밀도 · 3×3 상자 블러 감정장(SIMD) · 밀도 기울기(SIMD) →
/// 감정 전파: 흥분(기본 트리거 + 이웃 평균) · 공포(임계 용량-반응 증폭 전선 + 회복 감쇠) SIMD 이완 · 골/환호/패닉/파도타기 이벤트 →
/// 선호 속도(좌석 목표 · 도착 감속 · 패닉 시 출구/위협 반대 · 파도 기립 정지) → LOD별 속도: 근거리 = RVO2 ORCA(최근접 10 이웃 · 2D 선형계획 + 3D 폴백 · 비근거리 이웃은 전책임),
/// 중거리 = 밀도 기울기 분리 + 셀 평균 정렬 플로킹, 원거리 = 밀도장 유동(FarStride 프레임 분산 갱신) → SIMD 적분 → 경계/필드 장벽.
/// </summary>
public sealed class MassiveAgenticCrowdSimulationEngine
{
    public const int MaxAgents = 131072, MaxNeighbors = 10, Sectors = 16, Exits = 8, Grid = 200, Cells = Grid * Grid;
    public const float World = 400f, Cell = World / Grid, Radius = 0.3f, NeighborDist = 2.5f, TimeHorizon = 2f;
    public const float Cx = 200f, Cz = 200f, FieldRx = 70f, FieldRz = 45f, StandInner = 1.15f, StandOuter = 2.6f;
    private const float Eps = 1e-5f;

    private readonly float[] _px, _pz, _vx, _vz;
    private readonly float[] _nvx, _nvz, _prefX, _prefZ;
    private readonly float[] _gx, _gz, _speed;
    private readonly float[] _arousal, _fear, _stand, _ang;
    private readonly float[] _tA, _tF;
    private readonly byte[] _team, _sector;
    private readonly CrowdLod[] _lod;
    private readonly int[] _cellOf, _sorted;
    private readonly int[] _cellCount = new int[Cells], _cellStart = new int[Cells + 1], _cellFill = new int[Cells];
    private readonly float[] _cnt = new float[Cells], _sumA = new float[Cells], _sumF = new float[Cells], _sumVx = new float[Cells], _sumVz = new float[Cells];
    private readonly float[] _bCnt = new float[Cells], _bA = new float[Cells], _bF = new float[Cells], _tmp = new float[Cells];
    private readonly float[] _dens = new float[Cells], _gradX = new float[Cells], _gradZ = new float[Cells];
    private readonly float[] _secN = new float[Sectors], _secA = new float[Sectors], _secF = new float[Sectors], _secStand = new float[Sectors], _secHome = new float[Sectors], _secX = new float[Sectors], _secZ = new float[Sectors];
    private readonly float[] _exitX = new float[Exits], _exitZ = new float[Exits];
    // ORCA 스크래치
    private readonly float[] _lpx = new float[MaxNeighbors], _lpz = new float[MaxNeighbors], _ldx = new float[MaxNeighbors], _ldz = new float[MaxNeighbors];
    private readonly float[] _qpx = new float[MaxNeighbors], _qpz = new float[MaxNeighbors], _qdx = new float[MaxNeighbors], _qdz = new float[MaxNeighbors];
    private readonly int[] _nbIdx = new int[MaxNeighbors];
    private readonly float[] _nbD = new float[MaxNeighbors];
    private ulong _rng = 0x5851F42D4C957F2DUL;
    private int _count;
    private float _fearX, _fearZ, _fearR, _fearT, _waveAng, _waveT;

    /// <param name="capacity">에이전트 버퍼 용량(기본 = 최대 131,072 · 소규모 시나리오는 작게 → LOH/GC 압력 절감)</param>
    public MassiveAgenticCrowdSimulationEngine(int count = 100_000, int capacity = MaxAgents)
    {
        capacity = Math.Clamp(Math.Max(capacity, count), 16, MaxAgents);
        Capacity = capacity;
        _px = new float[capacity];
        _pz = new float[capacity];
        _vx = new float[capacity];
        _vz = new float[capacity];
        _nvx = new float[capacity];
        _nvz = new float[capacity];
        _prefX = new float[capacity];
        _prefZ = new float[capacity];
        _gx = new float[capacity];
        _gz = new float[capacity];
        _speed = new float[capacity];
        _arousal = new float[capacity];
        _fear = new float[capacity];
        _stand = new float[capacity];
        _ang = new float[capacity];
        _tA = new float[capacity];
        _tF = new float[capacity];
        _team = new byte[capacity];
        _sector = new byte[capacity];
        _lod = new CrowdLod[capacity];
        _cellOf = new int[capacity];
        _sorted = new int[capacity];
        for (var k = 0; k < Exits; k++)
        {
            var a = k * MathF.Tau / Exits;
            _exitX[k] = Cx + FieldRx * (StandOuter + 0.12f) * MathF.Cos(a);
            _exitZ[k] = Cz + FieldRz * (StandOuter + 0.12f) * MathF.Sin(a);
        }
        Populate(count);
    }

    public int Capacity { get; }
    public int Count => _count;
    public long Frame { get; private set; }
    public float BaseExcitement { get; set; } = 0.25f;
    public bool AutoGoals { get; set; } = true;
    public bool FieldBarrier { get; set; } = true;
    public int FarStride { get; set; } = 2;
    public float WaveSpeed { get; set; } = 0.55f;
    public bool WaveActive => _waveT > 0f;
    public float WaveAngle => _waveAng;
    public bool PanicActive => _fearT > 0f;
    public float PanicX => _fearX;
    public float PanicZ => _fearZ;
    public float PanicRadius => _fearR;

    // ── 이벤트 펄스(음향 합성기가 감지) ──
    public long GoalEvents { get; private set; }
    public int LastGoalTeam { get; private set; } = -1;
    public long HitEvents { get; private set; }
    public long CheerEvents { get; private set; }

    // ── 통계 ──
    public long OrcaSolves { get; private set; }
    public long Lp3Fallbacks { get; private set; }
    public long NearCollisions { get; private set; }
    public int LastNear { get; private set; }
    public int LastMid { get; private set; }
    public int LastFarUpdated { get; private set; }
    public float MeanArousal { get; private set; }
    public float MeanFear { get; private set; }
    public float MeanSpeed { get; private set; }
    public float MaxDensity { get; private set; }
    public double LastStepUs { get; private set; }
    public double GridUs { get; private set; }
    public double FieldUs { get; private set; }
    public double EmotionUs { get; private set; }
    public double OrcaUs { get; private set; }
    public double FlockUs { get; private set; }

    public Span<CrowdLod> Lods => _lod.AsSpan(0, _count);
    public float X(int i) => _px[i];
    public float Z(int i) => _pz[i];
    public float Vx(int i) => _vx[i];
    public float Vz(int i) => _vz[i];
    public float Arousal(int i) => _arousal[i];
    public float Fear(int i) => _fear[i];
    public float Standing(int i) => _stand[i];
    public int Team(int i) => _team[i];
    public int SectorOf(int i) => _sector[i];
    public CrowdLod Lod(int i) => _lod[i];
    public ReadOnlySpan<float> Xs => _px.AsSpan(0, _count);
    public ReadOnlySpan<float> Zs => _pz.AsSpan(0, _count);
    public ReadOnlySpan<byte> SectorIndex => _sector.AsSpan(0, _count);
    public float CellDensity(int c) => _dens[c];
    public float CellArousal(int c) => _bCnt[c] > 0f ? _bA[c] / _bCnt[c] : 0f;
    public float CellFear(int c) => _bCnt[c] > 0f ? _bF[c] / _bCnt[c] : 0f;
    public int CellPopulation(int c) => _cellCount[c];
    public float SectorCount(int s) => _secN[s];
    public float SectorArousal(int s) => _secN[s] > 0f ? _secA[s] / _secN[s] : 0f;
    public float SectorFear(int s) => _secN[s] > 0f ? _secF[s] / _secN[s] : 0f;
    public float SectorStanding(int s) => _secN[s] > 0f ? _secStand[s] / _secN[s] : 0f;
    public float SectorHomeShare(int s) => _secN[s] > 0f ? _secHome[s] / _secN[s] : 0.5f;
    public float SectorX(int s) => _secN[s] > 0f ? _secX[s] / _secN[s] : Cx;
    public float SectorZ(int s) => _secN[s] > 0f ? _secZ[s] / _secN[s] : Cz;
    public float ExitX(int k) => _exitX[k];
    public float ExitZ(int k) => _exitZ[k];

    private float Next01()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17;
        return (_rng >> 40) / 16777216f;
    }

    /// <summary>타원 정규화 다이아몬드 각 [0, 4) — 삼각함수 없는 단조 방위(섹터 · 파도).</summary>
    public static float DiamondAngle(float dx, float dz)
    {
        float x = dx / FieldRx, z = dz / FieldRz;
        if (x == 0f && z == 0f) return 0f;
        if (z >= 0f) return x >= 0f ? z / (x + z) : 1f - x / (-x + z);
        return x < 0f ? 2f - z / (-x - z) : 3f + x / (x - z);
    }

    private void SeatGoal(int i, float aroundAngle, float spread)
    {
        var a = aroundAngle + (Next01() - 0.5f) * spread;
        var s = StandInner + 0.05f + (StandOuter - StandInner - 0.1f) * Next01();
        _gx[i] = Cx + FieldRx * s * MathF.Cos(a); _gz[i] = Cz + FieldRz * s * MathF.Sin(a);
    }

    /// <summary>
    /// 스탠드 고리에 n명 배치 — 지터 격자 좌석(간격 √(면적/n) · ±4% 지터 → 초기 겹침 없음) 중 선택 표집(Knuth S)으로 정확히 n석 균등 선택.
    /// 목표 = 자기 좌석(착석) · 팀 = 반구 · 보행 속도 1.0~1.6 m/s.
    /// </summary>
    public void Populate(int n)
    {
        _count = 0;
        n = Math.Clamp(n, 0, Capacity);
        if (n > 0)
        {
            const float sIn = StandInner + 0.05f, sOut = StandOuter - 0.05f;
            var area = MathF.PI * FieldRx * FieldRz * (sOut * sOut - sIn * sIn);
            var h = MathF.Sqrt(area / n) * 0.98f;
            float x0 = Cx - FieldRx * sOut, x1 = Cx + FieldRx * sOut, z0 = Cz - FieldRz * sOut, z1 = Cz + FieldRz * sOut;
            long total = 0;
            for (var z = z0; z <= z1; z += h)
                for (var x = x0; x <= x1; x += h) if (InStands(x, z, sIn, sOut)) total++;
            var need = (long)n;
            for (var z = z0; z <= z1 && need > 0; z += h)
                for (var x = x0; x <= x1 && need > 0; x += h)
                {
                    if (!InStands(x, z, sIn, sOut)) continue;
                    if (Next01() * total < need)
                    {
                        float jx = x + (Next01() - 0.5f) * 0.08f * h, jz = z + (Next01() - 0.5f) * 0.08f * h;
                        var i = AddAgent(jx, jz, jx, jz);
                        _team[i] = (byte)(jx >= Cx ? 0 : 1);
                        need--;
                    }
                    total--;
                }
        }
        RebuildGrid();
    }

    private static bool InStands(float x, float z, float sIn, float sOut)
    {
        float ex = (x - Cx) / FieldRx, ez = (z - Cz) / FieldRz;
        var e = ex * ex + ez * ez;
        return e >= sIn * sIn && e <= sOut * sOut;
    }

    public void Clear() { _count = 0; RebuildGrid(); }

    public int AddAgent(float x, float z, float goalX, float goalZ)
    {
        if (_count >= Capacity) return -1;
        var i = _count++;
        _px[i] = x; _pz[i] = z; _gx[i] = goalX; _gz[i] = goalZ;
        _vx[i] = _vz[i] = _nvx[i] = _nvz[i] = 0f;
        _speed[i] = 1.0f + 0.6f * Next01();
        _arousal[i] = BaseExcitement; _fear[i] = 0f; _stand[i] = 0f;
        _team[i] = 0; _lod[i] = CrowdLod.Far;
        return i;
    }

    public void SetGoal(int i, float x, float z) { _gx[i] = x; _gz[i] = z; }
    public void SetLod(int i, CrowdLod l) => _lod[i] = l;

    // ── 이벤트 ──
    public void Goal(int team)
    {
        for (var i = 0; i < _count; i++)
        {
            if (_team[i] == team) _arousal[i] = MathF.Min(1f, _arousal[i] + 0.65f);
            else _arousal[i] = MathF.Max(0f, _arousal[i] - 0.15f);
        }
        GoalEvents++; LastGoalTeam = team;
    }

    public void Cheer() { for (var i = 0; i < _count; i++) _arousal[i] = MathF.Min(1f, _arousal[i] + 0.3f); CheerEvents++; }

    /// <summary>피격/위협(공포 원천 3 s · 반경 내 즉시 공포 1 → 전파).</summary>
    public void Panic(float x, float z, float radius = 12f) { _fearX = x; _fearZ = z; _fearR = radius; _fearT = 3f; HitEvents++; }

    public void StartWave() { _waveT = 4f * MathF.Tau / WaveSpeed / 4f + 2f; _waveAng = 0f; }

    // ── 1 프레임 ──

    public void Step(float dt)
    {
        var t0 = Stopwatch.GetTimestamp();
        RebuildGrid();
        var t1 = Stopwatch.GetTimestamp();
        Fields();
        var t2 = Stopwatch.GetTimestamp();
        Emotion(dt);
        Preferred();
        var t3 = Stopwatch.GetTimestamp();
        int near = 0, mid = 0, far = 0;
        for (var i = 0; i < _count; i++)
        {
            if (_lod[i] != CrowdLod.Near) continue;
            Orca(i, dt);
            near++;
        }
        var t4 = Stopwatch.GetTimestamp();
        var stride = Math.Max(1, FarStride);
        var phase = (int)(Frame % stride);
        for (var i = 0; i < _count; i++)
        {
            var l = _lod[i];
            if (l == CrowdLod.Near) continue;
            var c = _cellOf[i];
            if (l == CrowdLod.Mid)
            {
                var n = MathF.Max(1f, _cnt[c]);
                _nvx[i] = _prefX[i] - 0.35f * _gradX[c] + 0.25f * (_sumVx[c] / n - _vx[i]);
                _nvz[i] = _prefZ[i] - 0.35f * _gradZ[c] + 0.25f * (_sumVz[c] / n - _vz[i]);
                mid++;
            }
            else if (i % stride == phase)
            {
                _nvx[i] = _prefX[i] - 0.5f * _gradX[c];
                _nvz[i] = _prefZ[i] - 0.5f * _gradZ[c];
                far++;
            }
            else { _nvx[i] = _vx[i]; _nvz[i] = _vz[i]; }
            var sp2 = _nvx[i] * _nvx[i] + _nvz[i] * _nvz[i];
            var lim = _speed[i] * (1f + _fear[i]);
            if (sp2 > lim * lim) { var k = lim / MathF.Sqrt(sp2); _nvx[i] *= k; _nvz[i] *= k; }
        }
        var n0 = _count;
        _nvx.AsSpan(0, n0).CopyTo(_vx); _nvz.AsSpan(0, n0).CopyTo(_vz);
        CrowdSimd.Integrate(_px.AsSpan(0, n0), _vx.AsSpan(0, n0), dt);
        CrowdSimd.Integrate(_pz.AsSpan(0, n0), _vz.AsSpan(0, n0), dt);
        Constrain();
        var t5 = Stopwatch.GetTimestamp();
        if (_fearT > 0f) _fearT -= dt;
        if (_waveT > 0f) { _waveT -= dt; _waveAng += WaveSpeed * 4f / MathF.Tau * dt; if (_waveAng >= 4f) _waveAng -= 4f; }
        LastNear = near; LastMid = mid; LastFarUpdated = far;
        Frame++;
        var f = 1e6 / Stopwatch.Frequency;
        GridUs = (t1 - t0) * f; FieldUs = (t2 - t1) * f; EmotionUs = (t3 - t2) * f; OrcaUs = (t4 - t3) * f; FlockUs = (t5 - t4) * f;
        LastStepUs = (t5 - t0) * f;
    }

    private static int CellIndex(float v) => Math.Clamp((int)(v / Cell), 0, Grid - 1);

    /// <summary>계수 정렬 재구축 + 셀 합계(인원 · 감정 · 속도) + 섹터 집계(다이아몬드 각).</summary>
    private void RebuildGrid()
    {
        var n = _count;
        Array.Clear(_cellCount); Array.Clear(_cnt); Array.Clear(_sumA); Array.Clear(_sumF); Array.Clear(_sumVx); Array.Clear(_sumVz);
        Array.Clear(_secN); Array.Clear(_secA); Array.Clear(_secF); Array.Clear(_secStand); Array.Clear(_secHome); Array.Clear(_secX); Array.Clear(_secZ);
        double sa = 0, sf = 0, ss = 0;
        for (var i = 0; i < n; i++)
        {
            var c = CellIndex(_pz[i]) * Grid + CellIndex(_px[i]);
            _cellOf[i] = c; _cellCount[c]++;
            _cnt[c] += 1f; _sumA[c] += _arousal[i]; _sumF[c] += _fear[i]; _sumVx[c] += _vx[i]; _sumVz[c] += _vz[i];
            var da = DiamondAngle(_px[i] - Cx, _pz[i] - Cz);
            _ang[i] = da;
            var s = Math.Min(Sectors - 1, (int)(da * (Sectors / 4f)));
            _sector[i] = (byte)s;
            _secN[s] += 1f; _secA[s] += _arousal[i]; _secF[s] += _fear[i]; _secStand[s] += _stand[i]; _secX[s] += _px[i]; _secZ[s] += _pz[i];
            if (_team[i] == 0) _secHome[s] += 1f;
            sa += _arousal[i]; sf += _fear[i]; ss += MathF.Sqrt(_vx[i] * _vx[i] + _vz[i] * _vz[i]);
        }
        _cellStart[0] = 0;
        for (var c = 0; c < Cells; c++) _cellStart[c + 1] = _cellStart[c] + _cellCount[c];
        Array.Copy(_cellStart, _cellFill, Cells);
        for (var i = 0; i < n; i++) _sorted[_cellFill[_cellOf[i]]++] = i;
        MeanArousal = n == 0 ? 0f : (float)(sa / n); MeanFear = n == 0 ? 0f : (float)(sf / n); MeanSpeed = n == 0 ? 0f : (float)(ss / n);
    }

    /// <summary>밀도(인/m²) · 3×3 상자 블러 감정장 · 밀도 중앙 차분 기울기(SIMD 행 연산).</summary>
    private void Fields()
    {
        Blur(_cnt, _bCnt); Blur(_sumA, _bA); Blur(_sumF, _bF);
        const float inv = 1f / (Cell * Cell * 9f);
        var mx = 0f;
        for (var c = 0; c < Cells; c++) { var d = _bCnt[c] * inv; _dens[c] = d; if (d > mx) mx = d; }
        MaxDensity = mx;
        Array.Clear(_gradX); Array.Clear(_gradZ);
        const float k = 0.5f / Cell;
        for (var z = 0; z < Grid; z++)
        {
            var r = z * Grid;
            CrowdSimd.SubScale(_dens.AsSpan(r + 2, Grid - 2), _dens.AsSpan(r, Grid - 2), _gradX.AsSpan(r + 1, Grid - 2), k);
        }
        for (var z = 1; z < Grid - 1; z++)
            CrowdSimd.SubScale(_dens.AsSpan((z + 1) * Grid, Grid), _dens.AsSpan((z - 1) * Grid, Grid), _gradZ.AsSpan(z * Grid, Grid), k);
    }

    private void Blur(float[] src, float[] dst)
    {
        for (var z = 0; z < Grid; z++)
        {
            var r = z * Grid;
            CrowdSimd.Add3(src.AsSpan(r, Grid - 2), src.AsSpan(r + 1, Grid - 2), src.AsSpan(r + 2, Grid - 2), _tmp.AsSpan(r + 1, Grid - 2));
            _tmp[r] = src[r] + src[r + 1];
            _tmp[r + Grid - 1] = src[r + Grid - 2] + src[r + Grid - 1];
        }
        _tmp.AsSpan(0, Grid).CopyTo(dst);
        for (var x = 0; x < Grid; x++) dst[x] = _tmp[x] + _tmp[Grid + x];
        for (var z = 1; z < Grid - 1; z++)
            CrowdSimd.Add3(_tmp.AsSpan((z - 1) * Grid, Grid), _tmp.AsSpan(z * Grid, Grid), _tmp.AsSpan((z + 1) * Grid, Grid), dst.AsSpan(z * Grid, Grid));
        var lr = (Grid - 1) * Grid;
        for (var x = 0; x < Grid; x++) dst[lr + x] = _tmp[lr - Grid + x] + _tmp[lr + x];
    }

    /// <summary>감정 전파: 이웃 평균(블러 장) 수집 → SIMD 이완 · 공포 원천 · 파도 기립.</summary>
    private void Emotion(float dt)
    {
        var n = _count;
        var b = BaseExcitement;
        // 공포 = 임계 용량-반응(SIR형): 이웃 공포 ≥ 0.15 노출 시 위협 지속 중 증폭(전선 확산) · 위협 종료 후 회복 감쇠
        var amp = _fearT > 0f ? 1.6f : 0.85f;
        for (var i = 0; i < n; i++)
        {
            var c = _cellOf[i];
            var inv = 1f / MathF.Max(1f, _bCnt[c]);
            _tA[i] = 0.55f * b + 0.45f * (_bA[c] * inv);
            var nf = _bF[c] * inv;
            _tF[i] = nf >= 0.15f ? MathF.Min(1f, nf * amp) : nf * 0.85f;
        }
        CrowdSimd.Relax(_arousal.AsSpan(0, n), _tA.AsSpan(0, n), MathF.Min(1f, 0.6f * dt));
        CrowdSimd.Relax(_fear.AsSpan(0, n), _tF.AsSpan(0, n), MathF.Min(1f, 2.5f * dt));
        if (_fearT > 0f)
        {
            int cx0 = CellIndex(_fearX - _fearR), cx1 = CellIndex(_fearX + _fearR), cz0 = CellIndex(_fearZ - _fearR), cz1 = CellIndex(_fearZ + _fearR);
            var r2 = _fearR * _fearR;
            for (var cz = cz0; cz <= cz1; cz++)
                for (var cx = cx0; cx <= cx1; cx++)
                {
                    var c = cz * Grid + cx;
                    for (var k = _cellStart[c]; k < _cellStart[c + 1]; k++)
                    {
                        var i = _sorted[k];
                        float dx = _px[i] - _fearX, dz = _pz[i] - _fearZ;
                        if (dx * dx + dz * dz <= r2) { _fear[i] = 1f; _arousal[i] = 1f; }
                    }
                }
        }
        if (_waveT > 0f)
        {
            for (var i = 0; i < n; i++)
            {
                var d = MathF.Abs(_ang[i] - _waveAng);
                if (d > 2f) d = 4f - d;
                var s = Math.Clamp(1f - d / 0.18f, 0f, 1f);
                _stand[i] = MathF.Max(s, _stand[i] - 2f * dt);
                if (s > 0.5f) _arousal[i] = MathF.Max(_arousal[i], 0.75f);
            }
        }
        else for (var i = 0; i < n; i++) _stand[i] = MathF.Max(0f, _stand[i] - 2f * dt);
    }

    /// <summary>선호 속도: 좌석 목표(도착 감속 · 배회 재목표) · 공포 = 위협 반대/최근접 출구 · 파도 기립 = 정지.</summary>
    private void Preferred()
    {
        for (var i = 0; i < _count; i++)
        {
            float tx = _gx[i], tz = _gz[i];
            var speed = _speed[i];
            if (_fear[i] > 0.5f)
            {
                speed *= 1f + _fear[i];
                float ax = _px[i] - _fearX, az = _pz[i] - _fearZ;
                if (_fearT > 0f && ax * ax + az * az < 4f * _fearR * _fearR)
                {
                    var d = MathF.Max(0.1f, MathF.Sqrt(ax * ax + az * az));
                    tx = _px[i] + ax / d * 10f; tz = _pz[i] + az / d * 10f;
                }
                else
                {
                    var k = (int)MathF.Round(_ang[i] * 2f) % Exits;
                    tx = _exitX[k]; tz = _exitZ[k];
                }
            }
            float dx = tx - _px[i], dz = tz - _pz[i];
            var dist = MathF.Sqrt(dx * dx + dz * dz);
            if (dist < 0.4f)
            {
                _prefX[i] = _prefZ[i] = 0f;
                if (AutoGoals && Next01() < 0.002f) SeatGoal(i, MathF.Atan2((_gz[i] - Cz) / FieldRz, (_gx[i] - Cx) / FieldRx), 0.06f);
                continue;
            }
            var s = speed * MathF.Min(1f, dist / 1.2f) * (1f - _stand[i]) / dist;
            _prefX[i] = dx * s; _prefZ[i] = dz * s;
        }
    }

    private void Constrain()
    {
        for (var i = 0; i < _count; i++)
        {
            ref var x = ref _px[i]; ref var z = ref _pz[i];
            if (float.IsNaN(x) || float.IsNaN(z)) { x = _gx[i]; z = _gz[i]; _vx[i] = _vz[i] = 0f; }
            x = Math.Clamp(x, 0.5f, World - 0.5f); z = Math.Clamp(z, 0.5f, World - 0.5f);
            if (!FieldBarrier) continue;
            float ex = (x - Cx) / FieldRx, ez = (z - Cz) / FieldRz;
            var e2 = ex * ex + ez * ez;
            if (e2 < 1f)
            {
                var k = 1f / MathF.Max(1e-3f, MathF.Sqrt(e2));
                x = Cx + (x - Cx) * k; z = Cz + (z - Cz) * k;
            }
        }
    }

    // ═════════════════════════ RVO2 ORCA ═════════════════════════

    private static float Det(float ax, float az, float bx, float bz) => ax * bz - az * bx;

    private void Orca(int i, float dt)
    {
        OrcaSolves++;
        float px = _px[i], pz = _pz[i], vx = _vx[i], vz = _vz[i];
        // 최근접 이웃(3×3 셀 · 거리 정렬 삽입)
        var nb = 0;
        int cx = CellIndex(px), cz = CellIndex(pz);
        const float r2max = NeighborDist * NeighborDist;
        for (var z = Math.Max(0, cz - 1); z <= Math.Min(Grid - 1, cz + 1); z++)
            for (var x = Math.Max(0, cx - 1); x <= Math.Min(Grid - 1, cx + 1); x++)
            {
                var c = z * Grid + x;
                for (var k = _cellStart[c]; k < _cellStart[c + 1]; k++)
                {
                    var j = _sorted[k];
                    if (j == i) continue;
                    float dx = _px[j] - px, dz = _pz[j] - pz;
                    var d2 = dx * dx + dz * dz;
                    if (d2 >= r2max || (nb == MaxNeighbors && d2 >= _nbD[nb - 1])) continue;
                    var p = nb < MaxNeighbors ? nb++ : nb - 1;
                    while (p > 0 && _nbD[p - 1] > d2) { _nbD[p] = _nbD[p - 1]; _nbIdx[p] = _nbIdx[p - 1]; p--; }
                    _nbD[p] = d2; _nbIdx[p] = j;
                }
            }
        // ORCA 반평면
        const float invTh = 1f / TimeHorizon, combined = 2f * Radius, combinedSq = combined * combined;
        var invDt = 1f / dt;
        for (var q = 0; q < nb; q++)
        {
            var j = _nbIdx[q];
            float rpx = _px[j] - px, rpz = _pz[j] - pz, rvx = vx - _vx[j], rvz = vz - _vz[j];
            var distSq = rpx * rpx + rpz * rpz;
            float dirX, dirZ, ux, uz;
            if (distSq > combinedSq)
            {
                float wx = rvx - invTh * rpx, wz = rvz - invTh * rpz;
                var wLenSq = wx * wx + wz * wz;
                var dot1 = wx * rpx + wz * rpz;
                if (dot1 < 0f && dot1 * dot1 > combinedSq * wLenSq)
                {
                    var wLen = MathF.Sqrt(wLenSq);
                    float uwx = wx / wLen, uwz = wz / wLen;
                    dirX = uwz; dirZ = -uwx;
                    var m = combined * invTh - wLen;
                    ux = m * uwx; uz = m * uwz;
                }
                else
                {
                    var leg = MathF.Sqrt(distSq - combinedSq);
                    if (Det(rpx, rpz, wx, wz) > 0f) { dirX = (rpx * leg - rpz * combined) / distSq; dirZ = (rpx * combined + rpz * leg) / distSq; }
                    else { dirX = -(rpx * leg + rpz * combined) / distSq; dirZ = -(-rpx * combined + rpz * leg) / distSq; }
                    var dot2 = rvx * dirX + rvz * dirZ;
                    ux = dot2 * dirX - rvx; uz = dot2 * dirZ - rvz;
                }
            }
            else
            {
                if (j > i && _lod[j] == CrowdLod.Near) NearCollisions++;   // ORCA↔ORCA 쌍만(중/원거리 근사는 제외)
                float wx = rvx - invDt * rpx, wz = rvz - invDt * rpz;
                var wLen = MathF.Max(Eps, MathF.Sqrt(wx * wx + wz * wz));
                float uwx = wx / wLen, uwz = wz / wLen;
                dirX = uwz; dirZ = -uwx;
                var m = combined * invDt - wLen;
                ux = m * uwx; uz = m * uwz;
            }
            var share = _lod[j] == CrowdLod.Near ? 0.5f : 1f;   // 비근거리 이웃은 회피 협력 없음 → 전책임
            _lpx[q] = vx + share * ux; _lpz[q] = vz + share * uz; _ldx[q] = dirX; _ldz[q] = dirZ;
        }
        var maxSpeed = _speed[i] * (1f + _fear[i]);
        float rx, rz;
        var fail = Lp2(_lpx, _lpz, _ldx, _ldz, nb, maxSpeed, _prefX[i], _prefZ[i], false, out rx, out rz);
        if (fail < nb) { Lp3(nb, fail, maxSpeed, ref rx, ref rz); Lp3Fallbacks++; }
        _nvx[i] = rx; _nvz[i] = rz;
    }

    private static bool Lp1(float[] lpx, float[] lpz, float[] ldx, float[] ldz, int lineNo, float radius, float optX, float optZ, bool dirOpt, ref float rx, ref float rz)
    {
        var dot = lpx[lineNo] * ldx[lineNo] + lpz[lineNo] * ldz[lineNo];
        var disc = dot * dot + radius * radius - (lpx[lineNo] * lpx[lineNo] + lpz[lineNo] * lpz[lineNo]);
        if (disc < 0f) return false;
        var sq = MathF.Sqrt(disc);
        float tLeft = -dot - sq, tRight = -dot + sq;
        for (var i = 0; i < lineNo; i++)
        {
            var den = Det(ldx[lineNo], ldz[lineNo], ldx[i], ldz[i]);
            var num = Det(ldx[i], ldz[i], lpx[lineNo] - lpx[i], lpz[lineNo] - lpz[i]);
            if (MathF.Abs(den) <= Eps) { if (num < 0f) return false; continue; }
            var t = num / den;
            if (den >= 0f) tRight = MathF.Min(tRight, t); else tLeft = MathF.Max(tLeft, t);
            if (tLeft > tRight) return false;
        }
        if (dirOpt)
        {
            var tt = optX * ldx[lineNo] + optZ * ldz[lineNo] > 0f ? tRight : tLeft;
            rx = lpx[lineNo] + tt * ldx[lineNo]; rz = lpz[lineNo] + tt * ldz[lineNo];
        }
        else
        {
            var t = Math.Clamp(ldx[lineNo] * (optX - lpx[lineNo]) + ldz[lineNo] * (optZ - lpz[lineNo]), tLeft, tRight);
            rx = lpx[lineNo] + t * ldx[lineNo]; rz = lpz[lineNo] + t * ldz[lineNo];
        }
        return true;
    }

    private static int Lp2(float[] lpx, float[] lpz, float[] ldx, float[] ldz, int n, float radius, float optX, float optZ, bool dirOpt, out float rx, out float rz)
    {
        if (dirOpt) { rx = optX * radius; rz = optZ * radius; }
        else if (optX * optX + optZ * optZ > radius * radius) { var l = MathF.Sqrt(optX * optX + optZ * optZ); rx = optX / l * radius; rz = optZ / l * radius; }
        else { rx = optX; rz = optZ; }
        for (var i = 0; i < n; i++)
        {
            if (Det(ldx[i], ldz[i], lpx[i] - rx, lpz[i] - rz) > 0f)
            {
                float sx = rx, sz = rz;
                if (!Lp1(lpx, lpz, ldx, ldz, i, radius, optX, optZ, dirOpt, ref rx, ref rz)) { rx = sx; rz = sz; return i; }
            }
        }
        return n;
    }

    private void Lp3(int n, int begin, float radius, ref float rx, ref float rz)
    {
        var distance = 0f;
        for (var i = begin; i < n; i++)
        {
            if (Det(_ldx[i], _ldz[i], _lpx[i] - rx, _lpz[i] - rz) <= distance) continue;
            var m = 0;
            for (var j = 0; j < i; j++)
            {
                var det = Det(_ldx[i], _ldz[i], _ldx[j], _ldz[j]);
                float px, pz;
                if (MathF.Abs(det) <= Eps)
                {
                    if (_ldx[i] * _ldx[j] + _ldz[i] * _ldz[j] > 0f) continue;
                    px = 0.5f * (_lpx[i] + _lpx[j]); pz = 0.5f * (_lpz[i] + _lpz[j]);
                }
                else
                {
                    var t = Det(_ldx[j], _ldz[j], _lpx[i] - _lpx[j], _lpz[i] - _lpz[j]) / det;
                    px = _lpx[i] + t * _ldx[i]; pz = _lpz[i] + t * _ldz[i];
                }
                float dx = _ldx[j] - _ldx[i], dz = _ldz[j] - _ldz[i];
                var l = MathF.Max(Eps, MathF.Sqrt(dx * dx + dz * dz));
                _qpx[m] = px; _qpz[m] = pz; _qdx[m] = dx / l; _qdz[m] = dz / l; m++;
            }
            float sx = rx, sz = rz;
            if (Lp2(_qpx, _qpz, _qdx, _qdz, m, radius, -_ldz[i], _ldx[i], true, out rx, out rz) < m) { rx = sx; rz = sz; }
            distance = Det(_ldx[i], _ldz[i], _lpx[i] - rx, _lpz[i] - rz);
        }
    }
}
