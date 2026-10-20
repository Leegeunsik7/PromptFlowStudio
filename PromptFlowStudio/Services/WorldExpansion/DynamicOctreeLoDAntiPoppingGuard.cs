using System.Numerics;

namespace PromptFlowStudio.Services.WorldExpansion;

/// <summary>시네마틱 카메라 상태(v17.21) — 등속 선회 예측 모델 입력.</summary>
public readonly record struct WorldCamera(Vector3 Position, float Yaw, float Pitch, float Speed, float YawRate, float FovDeg)
{
    public Vector3 Forward => new(MathF.Sin(Yaw) * MathF.Cos(Pitch), MathF.Sin(Pitch), MathF.Cos(Yaw) * MathF.Cos(Pitch));

    /// <summary>등속 선회(CTRV) 예측 위치 t 초 후.</summary>
    public Vector3 Predict(float t)
    {
        if (MathF.Abs(YawRate) < 1e-4f) return Position + new Vector3(MathF.Sin(Yaw), 0f, MathF.Cos(Yaw)) * Speed * t;
        var y1 = Yaw + YawRate * t;
        var k = Speed / YawRate;
        return Position + new Vector3(k * (MathF.Cos(Yaw) - MathF.Cos(y1)), 0f, k * (MathF.Sin(y1) - MathF.Sin(Yaw)));
    }

    public bool InFrustum(Vector3 p, float margin)
    {
        var d = p - Position;
        d.Y = 0f;
        var l = d.Length();
        if (l < margin) return true;
        var f = new Vector3(MathF.Sin(Yaw), 0f, MathF.Cos(Yaw));
        return Vector3.Dot(d, f) / l > MathF.Cos((FovDeg * 0.5f) * (MathF.PI / 180f)) - margin / l;
    }
}

/// <summary>
/// [PromptFlow Studio v17.27.0] 🛡 동적 옥트리 LoD 메모리 버짓 &amp; 팝핑 방어 가드(<see cref="Update"/> 무할당).
///   ▸ 고정 풀: <see cref="GaussianWorldPool"/> 슬랩(4 가우시안) 자유 스택 — 런타임 확장 없음 · 타일 레코드 4096 · 키 → 타일 개방 주소 맵(매 프레임 재구축).
///   ▸ 예측 확장: CTRV 카메라 궤적(0 · 0.5 · 1 · 1.5 s) 반경 안 타일을 요구 집합으로 → 프러스텀 진입 전에 생성(우선순위 = 예측 경로 거리).
///   ▸ 옥트리 LoD: 거리 임계 16/32/56 m ÷ 바이어스 — 요구 슬랩이 예산(용량 85 %)을 넘으면 바이어스(≤ 2.5)를 키워 거칠게 · 그래도 넘으면 우선순위 끝(원거리)부터 반경 축소.
///   ▸ 안티 팝핑: 신규/교체 타일은 스무스스텝 알파-디졸브 페이드인(기본 24 프레임) · 이전 LoD 는 같은 속도로 페이드아웃 후 슬랩 반환(교차 디졸브).
///   ▸ 가드 OFF: 페이드 0 · 바이어스 없음 → 풀 고갈 시 타일 누락(구멍) · 시야 내 즉시 출현(팝).
/// </summary>
public sealed class DynamicOctreeLoDAntiPoppingGuard
{
    public const int MaxTiles = 4096, MapSize = 16384, MaxSlabsPerSet = 64, MaxCraters = 8, MaxCandidates = 4096;
    public const float MaxBias = 2.5f, BudgetFraction = 0.85f, PopThreshold = 0.2f, LateFraction = 0.6f;
    public static readonly float[] LodDistance = { 16f, 32f, 56f };

    private struct Tile
    {
        public int Tx, Tz, Epoch, SlabCount, OldSlabCount;
        public sbyte Lod, OldLod;
        public float Fade, OldFade, PrevAlpha;
        public bool Live, Leaving, Desired, InView;
        public sbyte WantLod;
        public float Dist;
    }

    private readonly Tile[] _t = new Tile[MaxTiles];
    private readonly int[] _slabs = new int[MaxTiles * MaxSlabsPerSet], _oldSlabs = new int[MaxTiles * MaxSlabsPerSet];
    private readonly int[] _freeTiles = new int[MaxTiles];
    private readonly int[] _map = new int[MapSize];
    private readonly int[] _free;
    private readonly short[] _slabTile;
    private readonly byte[] _slabOld;
    private readonly float[] _candKey = new float[MaxCandidates];
    private readonly int[] _candIdx = new int[MaxCandidates], _candTx = new int[MaxCandidates], _candTz = new int[MaxCandidates];
    private readonly sbyte[] _candLod = new sbyte[MaxCandidates];
    private readonly Vector4[] _craters = new Vector4[MaxCraters];
    private readonly Vector3[] _pred = new Vector3[4];
    private int _freeTop, _freeTileTop, _craterCount, _craterHead;
    private long _frame;

    public DynamicOctreeLoDAntiPoppingGuard(int capacity = 32768)
    {
        Pool = new GaussianWorldPool(capacity);
        _free = new int[Pool.Slabs];
        _slabTile = new short[Pool.Slabs];
        _slabOld = new byte[Pool.Slabs];
        Reset();
    }

    public GaussianWorldPool Pool { get; }
    public bool Enabled { get; set; } = true;
    public bool Prediction { get; set; } = true;
    public int FadeFrames { get; set; } = 24;
    public float Radius { get; set; } = 80f;
    public int GenBudget { get; set; } = 12;
    public float LodBias { get; private set; } = 1f;
    public int LiveTiles { get; private set; }
    public int UsedSlabs => Pool.Slabs - _freeTop;
    public float Occupancy => UsedSlabs / (float)Pool.Slabs;
    public float PeakOccupancy { get; private set; }
    public int LiveGaussians { get; private set; }
    public int RequiredSlabs { get; private set; }
    public int PendingTiles { get; private set; }
    public int GeneratedThisFrame { get; private set; }
    public long Generated { get; private set; }
    public long Dropped { get; private set; }
    public long PopsInView { get; private set; }
    public long BornInView { get; private set; }
    /// <summary>반경의 <see cref="LateFraction"/> 이내 시야에서 뒤늦게 생성된 타일(예측 지연).</summary>
    public long LateInView { get; private set; }
    public long Crossfades { get; private set; }
    public float MaxAlphaStep { get; private set; }
    public float FrameAlphaStep { get; private set; }
    public int CraterCount => _craterCount;
    public Vector4 Crater(int i) => _craters[i];
    public int LodTiles(int lod) { var n = 0; for (var i = 0; i < MaxTiles; i++) if (_t[i].Live && !_t[i].Leaving && _t[i].Lod == lod) n++; return n; }
    public bool TileLive(int i) => _t[i].Live;
    public int TileX(int i) => _t[i].Tx;
    public int TileZ(int i) => _t[i].Tz;
    public int TileLod(int i) => _t[i].Lod;
    public bool TileLeaving(int i) => _t[i].Leaving;
    public float TileAlpha(int i) => Alpha(i);
    /// <summary>월드 점 p 가 속한 타일이 상주하는지(직전 Update 맵 기준).</summary>
    public bool Resident(Vector3 p) => Find((int)MathF.Floor(p.X / GenerativeNeuralWorldExpansionEngine.TileSize), (int)MathF.Floor(p.Z / GenerativeNeuralWorldExpansionEngine.TileSize)) >= 0;
    /// <summary>슬랩 렌더 알파(소속 타일 · 신규/이전 세트별 디졸브) — 비할당 슬랩은 0.</summary>
    public float SlabAlpha(int slab)
    {
        var t = _slabTile[slab];
        if (t < 0) return 0f;
        ref readonly var tl = ref _t[t];
        return _slabOld[slab] != 0 ? Smooth(tl.OldFade) : Smooth(tl.Fade);
    }

    public void Reset()
    {
        for (var i = 0; i < Pool.Slabs; i++) { _free[i] = Pool.Slabs - 1 - i; _slabTile[i] = -1; _slabOld[i] = 0; }
        _freeTop = Pool.Slabs;
        for (var i = 0; i < MaxTiles; i++) { _t[i] = default; _freeTiles[i] = MaxTiles - 1 - i; }
        _freeTileTop = MaxTiles;
        Array.Clear(Pool.Opacity);
        _craterCount = _craterHead = 0;
        LodBias = 1f; LiveTiles = 0; LiveGaussians = 0; PeakOccupancy = 0f;
        ResetMetrics();
    }

    public void ResetMetrics()
    {
        Generated = Dropped = PopsInView = BornInView = LateInView = Crossfades = 0;
        MaxAlphaStep = 0f;
        PeakOccupancy = Occupancy;
    }

    /// <summary>v17.26 파괴 크레이터 등록 → 해당 구 내부 가우시안 불투명도 0(이후 생성도 제외).</summary>
    public void AddCrater(Vector3 c, float r)
    {
        _craters[_craterHead] = new Vector4(c, r);
        _craterHead = (_craterHead + 1) % MaxCraters;
        if (_craterCount < MaxCraters) _craterCount++;
        var r2 = r * r;
        var p = Pool;
        for (var s = 0; s < Pool.Slabs; s++)
        {
            if (_slabTile[s] < 0) continue;
            for (var k = s * GaussianWorldPool.SlabSize; k < (s + 1) * GaussianWorldPool.SlabSize; k++)
            {
                var dx = p.X[k] - c.X; var dy = p.Y[k] - c.Y; var dz = p.Z[k] - c.Z;
                if (dx * dx + dy * dy + dz * dz < r2) p.Opacity[k] = 0f;
            }
        }
    }

    public static int SlabsFor(int lod) => (GenerativeNeuralWorldExpansionEngine.PointCount(lod) + GaussianWorldPool.SlabSize - 1) / GaussianWorldPool.SlabSize;

    public int LodFor(float d)
    {
        var b = Enabled ? LodBias : 1f;
        for (var l = 0; l < LodDistance.Length; l++) if (d < LodDistance[l] / b) return l;
        return GenerativeNeuralWorldExpansionEngine.MaxLod;
    }

    /// <summary>1 프레임: 요구 집합 · 예산 바이어스 → 이탈 페이드 → 우선순위 생성(예산 N) → 페이드/팝 측정.</summary>
    public void Update(GenerativeNeuralWorldExpansionEngine engine, in WorldCamera cam)
    {
        _frame++;
        RebuildMap();
        _pred[0] = cam.Position;
        var np = 1;
        if (Prediction) { _pred[1] = cam.Predict(0.5f); _pred[2] = cam.Predict(1f); _pred[3] = cam.Predict(1.5f); np = 4; }
        float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
        for (var i = 0; i < np; i++) { x0 = MathF.Min(x0, _pred[i].X); x1 = MathF.Max(x1, _pred[i].X); z0 = MathF.Min(z0, _pred[i].Z); z1 = MathF.Max(z1, _pred[i].Z); }
        const float ts = GenerativeNeuralWorldExpansionEngine.TileSize;
        int tx0 = (int)MathF.Floor((x0 - Radius) / ts), tx1 = (int)MathF.Floor((x1 + Radius) / ts);
        int tz0 = (int)MathF.Floor((z0 - Radius) / ts), tz1 = (int)MathF.Floor((z1 + Radius) / ts);
        for (var i = 0; i < MaxTiles; i++) _t[i].Desired = false;

        // 1) 요구 집합 · 예산 바이어스(가드 ON) — 후보 목록 작성
        var budget = (int)(Pool.Slabs * BudgetFraction);
        var cand = 0;
        for (var pass = 0; pass < 8; pass++)
        {
            cand = 0;
            var need = 0;
            for (var tz = tz0; tz <= tz1; tz++)
                for (var tx = tx0; tx <= tx1; tx++)
                {
                    var c = new Vector3((tx + 0.5f) * ts, 0f, (tz + 0.5f) * ts);
                    var dCur = Dist2(c, cam.Position);
                    var dMin = dCur;
                    for (var i = 1; i < np; i++) dMin = MathF.Min(dMin, Dist2(c, _pred[i]));
                    if (dMin > Radius || cand >= MaxCandidates) continue;
                    var lod = LodFor(dCur);
                    need += SlabsFor(lod);
                    _candTx[cand] = tx; _candTz[cand] = tz; _candLod[cand] = (sbyte)lod; _candKey[cand] = dMin; _candIdx[cand] = cand;
                    cand++;
                }
            RequiredSlabs = need;
            if (!Enabled || need <= budget || LodBias >= MaxBias) break;
            LodBias = MathF.Min(MaxBias, LodBias * 1.25f);
        }
        if (Enabled && RequiredSlabs < budget / 2 && LodBias > 1f) LodBias = MathF.Max(1f, LodBias / 1.05f);   // 여유 시 서서히 정밀 복귀
        // 예산 최종 단: 우선순위 순으로 누적해 초과분 탈락(반경 축소)
        _candKey.AsSpan(0, cand).Sort(_candIdx.AsSpan(0, cand));
        var acc = 0;
        var keep = cand;
        if (Enabled)
            for (var k = 0; k < cand; k++)
            {
                acc += SlabsFor(_candLod[_candIdx[k]]);
                if (acc > budget) { keep = k; break; }
            }
        // 2) 요구 표시 · 생성 대기
        var pending = 0;
        var gen = 0;
        for (var k = 0; k < keep; k++)
        {
            var ci = _candIdx[k];
            var ti = Find(_candTx[ci], _candTz[ci]);
            var lod = _candLod[ci];
            if (ti >= 0)
            {
                ref var t = ref _t[ti];
                t.Desired = true;
                if (t.Leaving) { t.Leaving = false; }
                if (t.Lod == lod && t.Epoch == engine.Epoch) continue;
                if (t.OldSlabCount > 0 && Enabled) { pending++; continue; }   // 교차 디졸브 완료 전 재교체 보류(이전 세트 즉시 소멸 = 팝)
            }
            pending++;
            if (gen >= GenBudget) continue;
            if (Spawn(engine, ti, _candTx[ci], _candTz[ci], lod, cam)) gen++;
        }
        PendingTiles = pending - gen;
        GeneratedThisFrame = gen;
        Generated += gen;

        // 3) 이탈 · 페이드 진행 · 팝 측정
        var step = Enabled && FadeFrames > 0 ? 1f / FadeFrames : 1f;
        var frameMax = 0f;
        var live = 0;
        var gs = 0;
        for (var i = 0; i < MaxTiles; i++)
        {
            ref var t = ref _t[i];
            if (!t.Live) continue;
            if (!t.Desired && !t.Leaving) t.Leaving = true;
            if (t.Leaving) t.Fade = MathF.Max(0f, t.Fade - step);
            else t.Fade = MathF.Min(1f, t.Fade + step);
            if (t.OldSlabCount > 0)
            {
                t.OldFade = MathF.Max(0f, t.OldFade - step);
                if (t.OldFade <= 0f) ReleaseOld(i);
            }
            if (t.Leaving && t.Fade <= 0f) { Release(i); continue; }
            var c = new Vector3((t.Tx + 0.5f) * ts, cam.Position.Y, (t.Tz + 0.5f) * ts);
            t.InView = cam.InFrustum(c, ts);
            var a = Alpha(i);
            if (t.InView)
            {
                var d = MathF.Abs(a - t.PrevAlpha);
                if (d > frameMax) frameMax = d;
                if (d > PopThreshold) PopsInView++;
            }
            t.PrevAlpha = a;
            live++;
            gs += GenerativeNeuralWorldExpansionEngine.PointCount(t.Lod);
        }
        LiveTiles = live;
        LiveGaussians = gs;
        FrameAlphaStep = frameMax;
        if (frameMax > MaxAlphaStep) MaxAlphaStep = frameMax;
        if (Occupancy > PeakOccupancy) PeakOccupancy = Occupancy;
    }

    private static float Dist2(Vector3 a, Vector3 b) { var dx = a.X - b.X; var dz = a.Z - b.Z; return MathF.Sqrt(dx * dx + dz * dz); }

    private float Alpha(int i)
    {
        ref readonly var t = ref _t[i];
        var a = Smooth(t.Fade);
        if (t.OldSlabCount > 0) a = MathF.Max(a, Smooth(t.OldFade));   // 교차 디졸브 중 가시 커버리지
        return a;
    }

    public static float Smooth(float x) { x = Math.Clamp(x, 0f, 1f); return x * x * (3f - 2f * x); }

    private bool Spawn(GenerativeNeuralWorldExpansionEngine engine, int ti, int tx, int tz, int lod, in WorldCamera cam)
    {
        var need = SlabsFor(lod);
        if (_freeTop < need)
        {
            if (Enabled) ReclaimLeaving(need);
            if (_freeTop < need) { Dropped++; return false; }
        }
        var fresh = ti < 0;
        if (fresh)
        {
            if (_freeTileTop == 0) { Dropped++; return false; }
            ti = _freeTiles[--_freeTileTop];
            _t[ti] = new Tile { Tx = tx, Tz = tz, Live = true, Desired = true, Lod = -1 };
            Insert(tx, tz, ti);
            var c = new Vector3((tx + 0.5f) * GenerativeNeuralWorldExpansionEngine.TileSize, cam.Position.Y, (tz + 0.5f) * GenerativeNeuralWorldExpansionEngine.TileSize);
            if (cam.InFrustum(c, GenerativeNeuralWorldExpansionEngine.TileSize))
            {
                BornInView++;
                if (Vector3.Distance(c, cam.Position) < LateFraction * Radius) LateInView++;   // 예측 실패: 이미 가까운 시야 안에서 뒤늦게 생성
            }
        }
        ref var t = ref _t[ti];
        if (!fresh && t.SlabCount > 0)
        {
            // 이전 세트 → 페이드아웃 슬롯(이미 교차 중이면 즉시 반환)
            if (t.OldSlabCount > 0) ReleaseOld(ti);
            if (Enabled && FadeFrames > 0)
            {
                Array.Copy(_slabs, ti * MaxSlabsPerSet, _oldSlabs, ti * MaxSlabsPerSet, t.SlabCount);
                for (var k = 0; k < t.SlabCount; k++) _slabOld[_oldSlabs[ti * MaxSlabsPerSet + k]] = 1;
                t.OldSlabCount = t.SlabCount; t.OldLod = t.Lod; t.OldFade = t.Fade;
                Crossfades++;
            }
            else FreeSlabs(_slabs, ti * MaxSlabsPerSet, t.SlabCount);
            t.SlabCount = 0;
        }
        for (var k = 0; k < need; k++)
        {
            var s = _free[--_freeTop];
            _slabs[ti * MaxSlabsPerSet + k] = s;
            _slabTile[s] = (short)ti;
            _slabOld[s] = 0;
        }
        t.SlabCount = need;
        t.Lod = (sbyte)lod;
        t.Epoch = engine.Epoch;
        t.Fade = 0f;
        engine.GenerateTile(tx, tz, lod, Pool, _slabs.AsSpan(ti * MaxSlabsPerSet, need), _craters.AsSpan(0, _craterCount));
        // 페이드 OFF: 즉시 완전 불투명
        if (!Enabled || FadeFrames <= 0) t.Fade = 1f;
        return true;
    }

    private void ReclaimLeaving(int need)
    {
        // ① 교차 디졸브 중인 이전 LoD 세트 → ② 페이드아웃이 가장 많이 진행된 이탈 타일 순으로 강제 반환
        for (var i = 0; i < MaxTiles && _freeTop < need; i++)
            if (_t[i].Live && _t[i].OldSlabCount > 0) ReleaseOld(i);
        while (_freeTop < need)
        {
            var best = -1;
            var bf = float.MaxValue;
            for (var i = 0; i < MaxTiles; i++)
                if (_t[i].Live && _t[i].Leaving && _t[i].Fade < bf) { bf = _t[i].Fade; best = i; }
            if (best < 0) return;
            Release(best);
        }
    }

    private void Release(int i)
    {
        ref var t = ref _t[i];
        FreeSlabs(_slabs, i * MaxSlabsPerSet, t.SlabCount);
        if (t.OldSlabCount > 0) FreeSlabs(_oldSlabs, i * MaxSlabsPerSet, t.OldSlabCount);
        t = default;
        _freeTiles[_freeTileTop++] = i;
    }

    private void ReleaseOld(int i)
    {
        ref var t = ref _t[i];
        FreeSlabs(_oldSlabs, i * MaxSlabsPerSet, t.OldSlabCount);
        t.OldSlabCount = 0; t.OldFade = 0f;
    }

    private void FreeSlabs(int[] arr, int start, int count)
    {
        for (var k = 0; k < count; k++)
        {
            var s = arr[start + k];
            _slabTile[s] = -1; _slabOld[s] = 0;
            var o = s * GaussianWorldPool.SlabSize;
            Array.Clear(Pool.Opacity, o, GaussianWorldPool.SlabSize);
            _free[_freeTop++] = s;
        }
    }

    // 타일 키 → 인덱스 개방 주소 맵(매 프레임 재구축 · 삭제 비석 불필요)
    private void RebuildMap()
    {
        Array.Fill(_map, -1);
        for (var i = 0; i < MaxTiles; i++) if (_t[i].Live) Insert(_t[i].Tx, _t[i].Tz, i);
    }

    private static int Slot(int tx, int tz) => (int)(((uint)tx * 73856093u ^ (uint)tz * 19349663u) & (MapSize - 1));

    private void Insert(int tx, int tz, int ti)
    {
        var s = Slot(tx, tz);
        while (_map[s] >= 0) s = (s + 1) & (MapSize - 1);
        _map[s] = ti;
    }

    private int Find(int tx, int tz)
    {
        var s = Slot(tx, tz);
        while (_map[s] >= 0)
        {
            var i = _map[s];
            if (_t[i].Live && _t[i].Tx == tx && _t[i].Tz == tz) return i;
            s = (s + 1) & (MapSize - 1);
        }
        return -1;
    }
}
