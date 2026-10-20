using System.Diagnostics;

namespace PromptFlowStudio.Services.VirtualArena;

public enum ArenaEntityKind : byte { Avatar, Performer, Prop }

/// <summary>
/// [PromptFlow Studio v17.03.0] 🗺 대규모 공간 관심 영역(AOI) 관리 합성기(최대 32,768 엔티티 · 8 시청자 · 무할당).
/// 256 m 아레나 → 64×64 공간 해시 그리드(4 m 셀 · 계수 정렬 재구축) → 시청자별 반경 경계 상자 셀 순회 + 셀 외접원 원뿔 보수 컬링(FoV) →
/// 엔티티 판정(반경 · FoV 원뿔 · 4 m 주변 인지) → 거리/종류 가중 우선순위 누적기(장기 미송신 엔티티 기아 방지) → 내림차순 정렬 → 시청자 바이트 예산까지 송신(근 14 B · 중 9 B · 원 5 B 양자화).
/// 엔티티 이동은 SIMD 적분 · 관객 배회/플래시몹 · 연기자 무대 순환. 브루트포스 판정과 가시 집합 수 일치(테스트).
/// </summary>
public sealed class MassiveSpatialInterestManagementSynthesizer
{
    public const int MaxEntities = 32768, MaxViewers = 8, Grid = 64, Cells = Grid * Grid, NearBytes = 14, MidBytes = 9, FarBytes = 5;
    public const float ArenaSize = 256f, CellSize = ArenaSize / Grid, NearDist = 20f, MidDist = 60f, PeripheralDist = 4f, Center = ArenaSize / 2f, StageRadius = 16f;
    public static readonly string[] KindNames = { "관객 아바타", "연기자", "소품" };
    public static readonly string[] ViewerNames = { "로컬 플레이어", "중계 카메라 N", "중계 카메라 E", "중계 카메라 S", "중계 카메라 W", "VR 관객 A", "VR 관객 B", "드론 캠" };

    private readonly float[] _x = new float[MaxEntities], _z = new float[MaxEntities], _vx = new float[MaxEntities], _vz = new float[MaxEntities];
    private readonly ArenaEntityKind[] _kind = new ArenaEntityKind[MaxEntities];
    private readonly int[] _cellCount = new int[Cells], _cellStart = new int[Cells + 1], _cellFill = new int[Cells], _sorted = new int[MaxEntities], _cellOf = new int[MaxEntities];
    private readonly float[] _vpx = new float[MaxViewers], _vpz = new float[MaxViewers], _vyaw = new float[MaxViewers], _vfov = new float[MaxViewers], _vrad = new float[MaxViewers];
    private readonly int[] _budget = new int[MaxViewers];
    private readonly float[] _acc = new float[MaxViewers * MaxEntities];
    private readonly float[] _key = new float[MaxEntities];
    private readonly int[] _idx = new int[MaxEntities];
    private readonly int[] _visible = new int[MaxViewers], _sent = new int[MaxViewers], _bytes = new int[MaxViewers], _near = new int[MaxViewers], _mid = new int[MaxViewers], _far = new int[MaxViewers];
    private readonly int[] _visited = new int[MaxViewers], _culled = new int[MaxViewers];
    private ulong _rng = 0xA0761D6478BD642FUL;
    private int _count = 20000;
    private float _rushT, _rushX, _rushZ;

    public MassiveSpatialInterestManagementSynthesizer()
    {
        Populate();
        for (var v = 1; v < MaxViewers; v++)
        {
            float ang, rad, fov, range;
            if (v <= 4) { ang = (v - 1) * MathF.PI / 2f; rad = 95f; fov = 70f; range = 110f; }
            else if (v <= 6) { ang = 0.4f + v * 1.7f; rad = 60f; fov = 360f; range = 28f; }
            else { ang = 2.2f; rad = 40f; fov = 100f; range = 90f; }
            var px = Center + rad * MathF.Cos(ang); var pz = Center + rad * MathF.Sin(ang);
            SetViewer(v, px, pz, MathF.Atan2(Center - pz, Center - px), fov, range);
            _budget[v] = 1400;
        }
        SetViewer(0, Center - 30f, Center, 0f, 90f, 80f);
        _budget[0] = 1400;
        Rebuild();
    }

    public int Count
    {
        get => _count;
        set { var n = Math.Clamp(value, 100, MaxEntities); if (n > _count) Spawn(_count, n); _count = n; }
    }

    public float TickHz { get; set; } = 20f;
    public long Updates { get; private set; }
    public double LastUpdateUs { get; private set; }
    public double LastGridUs { get; private set; }
    public double LastFilterUs { get; private set; }
    public int MaxCellCount { get; private set; }
    public bool RushActive => _rushT > 0f;
    public int CellCount(int c) => _cellCount[c];
    public int CellCountAt(int cx, int cz) => _cellCount[cz * Grid + cx];
    public float EntityX(int e) => _x[e];
    public float EntityZ(int e) => _z[e];
    public ArenaEntityKind Kind(int e) => _kind[e];
    public float ViewerX(int v) => _vpx[v];
    public float ViewerZ(int v) => _vpz[v];
    public float ViewerYaw(int v) => _vyaw[v];
    public float ViewerFovDeg(int v) => _vfov[v] * 180f / MathF.PI;
    public float ViewerRadius(int v) => _vrad[v];
    public int Budget(int v) => _budget[v];
    public void SetBudget(int v, int bytes) => _budget[v] = Math.Clamp(bytes, 64, 65536);
    public int Visible(int v) => _visible[v];
    public int Sent(int v) => _sent[v];
    public int Bytes(int v) => _bytes[v];
    public int NearSent(int v) => _near[v];
    public int MidSent(int v) => _mid[v];
    public int FarSent(int v) => _far[v];
    public int CellsVisited(int v) => _visited[v];
    public int CellsCulled(int v) => _culled[v];
    public float Accumulator(int v, int e) => _acc[v * MaxEntities + e];

    public int TotalBytesPerTick { get { var s = 0; for (var v = 0; v < MaxViewers; v++) s += _bytes[v]; return s; } }
    public int TotalVisible { get { var s = 0; for (var v = 0; v < MaxViewers; v++) s += _visible[v]; return s; } }
    /// <summary>필터 없는 전량 브로드캐스트(전 엔티티 × 전 시청자 × 근거리 포맷).</summary>
    public long NaiveBytesPerTick => (long)_count * MaxViewers * NearBytes;
    public float Savings => 1f - TotalBytesPerTick / (float)Math.Max(1, NaiveBytesPerTick);
    public double TotalKbps => TotalBytesPerTick * 8.0 * TickHz / 1000.0;

    public void SetViewer(int v, float x, float z, float yawRad, float fovDeg, float radius)
    {
        _vpx[v] = Math.Clamp(x, 0f, ArenaSize); _vpz[v] = Math.Clamp(z, 0f, ArenaSize); _vyaw[v] = yawRad;
        _vfov[v] = Math.Clamp(fovDeg, 10f, 360f) * MathF.PI / 180f; _vrad[v] = Math.Clamp(radius, 5f, 200f);
    }

    public void Rush(float x, float z, float seconds = 8f) { _rushX = x; _rushZ = z; _rushT = seconds; }

    private float Next01()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17;
        return (_rng >> 40) / 16777216f;
    }

    private void Populate() => Spawn(0, _count);

    private void Spawn(int from, int to)
    {
        for (var e = from; e < to; e++)
        {
            var u = Next01();
            var k = u < 0.004f ? ArenaEntityKind.Performer : u < 0.12f ? ArenaEntityKind.Prop : ArenaEntityKind.Avatar;
            _kind[e] = k;
            float r, a = Next01() * MathF.Tau;
            r = k == ArenaEntityKind.Performer ? StageRadius * Next01() : k == ArenaEntityKind.Prop ? 8f + 115f * Next01() : 28f + 92f * MathF.Sqrt(Next01());
            _x[e] = Center + r * MathF.Cos(a); _z[e] = Center + r * MathF.Sin(a);
            _vx[e] = _vz[e] = 0f;
            for (var v = 0; v < MaxViewers; v++) _acc[v * MaxEntities + e] = 0f;
        }
    }

    /// <summary>1 AOI 틱(무할당): 이동 → 그리드 재구축 → 시청자별 필터/예산 송신.</summary>
    public void Update(float dt)
    {
        var t0 = Stopwatch.GetTimestamp();
        var n = _count;
        if (_rushT > 0f) _rushT -= dt;
        for (var e = 0; e < n; e++)
        {
            switch (_kind[e])
            {
                case ArenaEntityKind.Performer:
                {
                    var dx = _x[e] - Center; var dz = _z[e] - Center;
                    var d = MathF.Max(0.5f, MathF.Sqrt(dx * dx + dz * dz));
                    var pull = d > StageRadius ? -0.8f : 0f;
                    _vx[e] = -dz / d * 2.2f + dx / d * pull; _vz[e] = dx / d * 2.2f + dz / d * pull;
                    break;
                }
                case ArenaEntityKind.Avatar:
                    if (_rushT > 0f)
                    {
                        var dx = _rushX - _x[e]; var dz = _rushZ - _z[e]; var d2 = dx * dx + dz * dz;
                        if (d2 < 3600f && d2 > 4f) { var d = MathF.Sqrt(d2); _vx[e] = dx / d * 3f; _vz[e] = dz / d * 3f; break; }
                    }
                    if ((e + Updates) % 37 == 0)
                    {
                        var a = Next01() * MathF.Tau; var s = Next01() < 0.6f ? 0f : 0.4f + 1.2f * Next01();
                        _vx[e] = s * MathF.Cos(a); _vz[e] = s * MathF.Sin(a);
                    }
                    break;
                default:
                    _vx[e] = _vz[e] = 0f;
                    break;
            }
        }
        ArenaSimd.Integrate(_x.AsSpan(0, n), _vx.AsSpan(0, n), dt);
        ArenaSimd.Integrate(_z.AsSpan(0, n), _vz.AsSpan(0, n), dt);
        for (var e = 0; e < n; e++)
        {
            if (_x[e] < 0.5f) { _x[e] = 0.5f; _vx[e] = -_vx[e]; } else if (_x[e] > ArenaSize - 0.5f) { _x[e] = ArenaSize - 0.5f; _vx[e] = -_vx[e]; }
            if (_z[e] < 0.5f) { _z[e] = 0.5f; _vz[e] = -_vz[e]; } else if (_z[e] > ArenaSize - 0.5f) { _z[e] = ArenaSize - 0.5f; _vz[e] = -_vz[e]; }
        }
        var t1 = Stopwatch.GetTimestamp();
        Rebuild();
        var t2 = Stopwatch.GetTimestamp();
        for (var v = 0; v < MaxViewers; v++) Filter(v);
        var t3 = Stopwatch.GetTimestamp();
        Updates++;
        LastGridUs = (t2 - t1) * 1e6 / Stopwatch.Frequency;
        LastFilterUs = (t3 - t2) * 1e6 / Stopwatch.Frequency;
        LastUpdateUs = (t3 - t0) * 1e6 / Stopwatch.Frequency;
    }

    private static int CellIndex(float x) => Math.Clamp((int)(x / CellSize), 0, Grid - 1);

    /// <summary>계수 정렬 공간 해시 재구축(셀별 연속 엔티티 인덱스).</summary>
    private void Rebuild()
    {
        var n = _count;
        Array.Clear(_cellCount);
        for (var e = 0; e < n; e++)
        {
            var c = CellIndex(_z[e]) * Grid + CellIndex(_x[e]);
            _cellOf[e] = c; _cellCount[c]++;
        }
        _cellStart[0] = 0;
        var mx = 0;
        for (var c = 0; c < Cells; c++) { _cellStart[c + 1] = _cellStart[c] + _cellCount[c]; if (_cellCount[c] > mx) mx = _cellCount[c]; }
        MaxCellCount = mx;
        Array.Copy(_cellStart, _cellFill, Cells);
        for (var e = 0; e < n; e++) _sorted[_cellFill[_cellOf[e]]++] = e;
    }

    /// <summary>가시 판정(그리드 경로와 브루트포스 공용).</summary>
    private static bool InView(float dx, float dz, float fx, float fz, float cosHalf, float r, bool full)
    {
        var d2 = dx * dx + dz * dz;
        if (d2 > r * r) return false;
        if (full || d2 <= PeripheralDist * PeripheralDist) return true;
        return dx * fx + dz * fz >= cosHalf * MathF.Sqrt(d2);
    }

    private void Filter(int v)
    {
        float px = _vpx[v], pz = _vpz[v], r = _vrad[v], half = _vfov[v] * 0.5f;
        float fx = MathF.Cos(_vyaw[v]), fz = MathF.Sin(_vyaw[v]), cosHalf = MathF.Cos(half);
        var full = half >= MathF.PI - 1e-4f;
        int cx0 = CellIndex(px - r), cx1 = CellIndex(px + r), cz0 = CellIndex(pz - r), cz1 = CellIndex(pz + r);
        const float cellR = CellSize * 0.70710678f + 1e-3f;
        int n = 0, visited = 0, culled = 0;
        var accBase = v * MaxEntities;
        for (var cz = cz0; cz <= cz1; cz++)
            for (var cx = cx0; cx <= cx1; cx++)
            {
                var c = cz * Grid + cx;
                if (_cellCount[c] == 0) continue;
                float dcx = (cx + 0.5f) * CellSize - px, dcz = (cz + 0.5f) * CellSize - pz;
                var dc = MathF.Sqrt(dcx * dcx + dcz * dcz);
                if (dc - cellR > r) { culled++; continue; }
                if (!full && dc > cellR && dc - cellR > PeripheralDist)
                {
                    var ang = MathF.Acos(Math.Clamp((dcx * fx + dcz * fz) / dc, -1f, 1f));
                    if (ang - MathF.Asin(cellR / dc) > half + 1e-3f) { culled++; continue; }
                }
                visited++;
                for (var k = _cellStart[c]; k < _cellStart[c + 1]; k++)
                {
                    var e = _sorted[k];
                    float dx = _x[e] - px, dz = _z[e] - pz;
                    if (!InView(dx, dz, fx, fz, cosHalf, r, full)) continue;
                    var d = MathF.Sqrt(dx * dx + dz * dz);
                    var w = _kind[e] == ArenaEntityKind.Performer ? 4f : _kind[e] == ArenaEntityKind.Prop ? 0.5f : 1f;
                    ref var a = ref _acc[accBase + e];
                    a += w * (1f + 30f / (d + 5f));
                    _key[n] = -a; _idx[n] = e; n++;
                }
            }
        _visible[v] = n; _visited[v] = visited; _culled[v] = culled;
        Array.Sort(_key, _idx, 0, n);
        int bytes = 0, sent = 0, near = 0, mid = 0, far = 0;
        var budget = _budget[v];
        for (var i = 0; i < n; i++)
        {
            var e = _idx[i];
            float dx = _x[e] - px, dz = _z[e] - pz;
            var d2 = dx * dx + dz * dz;
            var size = d2 < NearDist * NearDist ? NearBytes : d2 < MidDist * MidDist ? MidBytes : FarBytes;
            if (bytes + size > budget) break;
            bytes += size; sent++;
            if (size == NearBytes) near++; else if (size == MidBytes) mid++; else far++;
            _acc[accBase + e] = 0f;
        }
        _bytes[v] = bytes; _sent[v] = sent; _near[v] = near; _mid[v] = mid; _far[v] = far;
    }

    /// <summary>검증용 전수 판정(그리드 미사용).</summary>
    public int BruteForceVisible(int v)
    {
        float px = _vpx[v], pz = _vpz[v], r = _vrad[v], half = _vfov[v] * 0.5f;
        float fx = MathF.Cos(_vyaw[v]), fz = MathF.Sin(_vyaw[v]), cosHalf = MathF.Cos(half);
        var full = half >= MathF.PI - 1e-4f;
        var n = 0;
        for (var e = 0; e < _count; e++) if (InView(_x[e] - px, _z[e] - pz, fx, fz, cosHalf, r, full)) n++;
        return n;
    }
}
