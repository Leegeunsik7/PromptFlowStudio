using System.Numerics;

namespace PromptFlowStudio.Services.ContinuumPhysics;

/// <summary>파편 인스턴스(링버퍼 슬롯).</summary>
public struct FragmentInstance
{
    public long Id;
    public int Event, Count;
    public long BirthFrame;
    public Vector3 Seed, Centroid, Velocity, Min, Max;
    public bool Live;
}

/// <summary>
/// [PromptFlow Studio v17.26.0] 💥 절차적 보로노이 충격 파괴 &amp; 소성 변형 합성기(<see cref="AfterFrame"/> 무할당).
///   ▸ 파괴(유리): 손상되지 않은 입자 중 최대 변형 에너지 밀도 ψ 가 임계값을 넘으면 그 지점을 충격 중심으로 3D 보로노이 씨앗 12개(근거리 6 · 반경 내 6 · 결정적 xorshift) 생성 →
///     반경 내 입자를 최근접 씨앗 셀로 재분류 · 두 번째 셀과의 이등분면 거리 &lt; 균열 폭이면 손상 d = 1(응력 0 → 균열면 분리).
///   ▸ 파편 메쉬: 고정 64 슬롯 링버퍼에 즉시 인스턴싱(가장 오래된 슬롯 재사용 시 소속 입자 본체로 회수) · 프레임마다 무게중심/속도/AABB 갱신.
///   ▸ 소성 변형: 블록 표면 메쉬(6면 × 9×9 정점)를 MPM 격자 속도로 이류 → 정지 법선 기준 안쪽 변위(찌그러짐 깊이) · 하중 제거 후 잔류 = 비가역 소성(점토 · 금속).
/// </summary>
public sealed class ProceduralFracturePlasticDeformationSynthesizer
{
    public const int Ring = 64, SeedsPerEvent = 12, FaceRes = 9, Vertices = 6 * FaceRes * FaceRes, Cooldown = 6, MaxEvents = 4;
    public const float FractureRadius = 0.12f, CrackWidth = 0.3f * RealtimeMpmContinuumPhysicsEngine.Dx;

    private readonly FragmentInstance[] _ring = new FragmentInstance[Ring];
    private readonly short[] _frag = new short[RealtimeMpmContinuumPhysicsEngine.Capacity];
    private readonly float[] _vx = new float[Vertices], _vy = new float[Vertices], _vz = new float[Vertices];
    private readonly Vector3[] _rest = new Vector3[Vertices], _normal = new Vector3[Vertices];
    private readonly Vector3[] _seeds = new Vector3[SeedsPerEvent];
    private readonly short[] _seedSlot = new short[SeedsPerEvent];
    private readonly double[] _sum = new double[Ring * 7];
    private readonly float[] _mn = new float[Ring * 3], _mx = new float[Ring * 3];
    private int _next = 1;
    private long _frame, _lastEvent = long.MinValue / 2, _nextId = 1;
    private uint _rng = 0xBB67AE85u;

    public ProceduralFracturePlasticDeformationSynthesizer()
    {
        var mn = RealtimeMpmContinuumPhysicsEngine.BlockMin;
        var mx = RealtimeMpmContinuumPhysicsEngine.BlockMax;
        var v = 0;
        for (var f = 0; f < 6; f++)
        {
            var axis = f >> 1;
            var hi = (f & 1) == 1;
            var nrm = Vector3.Zero;
            for (var a = 0; a < FaceRes; a++)
                for (var b = 0; b < FaceRes; b++)
                {
                    float u = a / (FaceRes - 1f), w = b / (FaceRes - 1f);
                    Vector3 p;
                    if (axis == 0) { p = new Vector3(hi ? mx.X : mn.X, Lerp(mn.Y, mx.Y, u), Lerp(mn.Z, mx.Z, w)); nrm = new Vector3(hi ? 1 : -1, 0, 0); }
                    else if (axis == 1) { p = new Vector3(Lerp(mn.X, mx.X, u), hi ? mx.Y : mn.Y, Lerp(mn.Z, mx.Z, w)); nrm = new Vector3(0, hi ? 1 : -1, 0); }
                    else { p = new Vector3(Lerp(mn.X, mx.X, u), Lerp(mn.Y, mx.Y, w), hi ? mx.Z : mn.Z); nrm = new Vector3(0, 0, hi ? 1 : -1); }
                    _rest[v] = p; _normal[v] = nrm; v++;
                }
        }
        ResetSurface();
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    public bool Enabled { get; set; } = true;
    public float CriticalEnergy { get; set; } = 15f;   // 검지 누름 ψ ≈ 5 · 해머 충격 ψ ≈ 60
    public int Events { get; private set; }
    public int CrackParticles { get; private set; }
    public int LiveFragments { get; private set; }
    public long Recycled { get; private set; }
    public float PeakEnergy { get; private set; }
    public float MaxStrainEnergy { get; private set; }
    public Vector3 LastImpact { get; private set; }
    public float DentMm { get; private set; }
    public float MaxDentMm { get; private set; }
    public float MeanDisplacementMm { get; private set; }
    public ref readonly FragmentInstance Fragment(int slot) => ref _ring[slot];
    public int FragmentOf(int particle) => _frag[particle];
    public Vector3 Vertex(int v) => new(_vx[v], _vy[v], _vz[v]);
    public Vector3 RestVertex(int v) => _rest[v];
    public Vector3 RestNormal(int v) => _normal[v];
    /// <summary>정점 v 의 안쪽 변위(m, 음수 = 바깥으로 부풂).</summary>
    public float Inward(int v) => -Vector3.Dot(Vertex(v) - _rest[v], _normal[v]);

    /// <summary>엔진 재생성 직후 호출: 파편 · 표면 메쉬 초기화 · 추적점 등록.</summary>
    public void Attach(RealtimeMpmContinuumPhysicsEngine e)
    {
        Array.Clear(_ring); Array.Clear(_frag);
        _next = 1; _frame = 0; _lastEvent = long.MinValue / 2; _rng = 0xBB67AE85u;
        Events = CrackParticles = LiveFragments = 0; PeakEnergy = MaxStrainEnergy = 0f; DentMm = MaxDentMm = MeanDisplacementMm = 0f;
        _ring[0] = new FragmentInstance { Id = 0, Live = true, Seed = (RealtimeMpmContinuumPhysicsEngine.BlockMin + RealtimeMpmContinuumPhysicsEngine.BlockMax) * 0.5f };
        ResetSurface();
        e.RegisterTracers(_vx, _vy, _vz, Vertices);
    }

    private void ResetSurface()
    {
        for (var v = 0; v < Vertices; v++) { _vx[v] = _rest[v].X; _vy[v] = _rest[v].Y; _vz[v] = _rest[v].Z; }
    }

    /// <summary>프레임 후처리: 파괴 판정 · 파편 통계 · 표면 찌그러짐 측정.</summary>
    public void AfterFrame(RealtimeMpmContinuumPhysicsEngine e)
    {
        _frame++;
        var n = e.Count;
        var best = -1;
        var peak = 0f;
        for (var i = 0; i < n; i++)
        {
            if (e.Damage(i) >= 1f) continue;
            var psi = e.StrainEnergy(i);
            if (psi > peak) { peak = psi; best = i; }
        }
        MaxStrainEnergy = peak;
        if (peak > PeakEnergy) PeakEnergy = peak;
        if (Enabled && best >= 0 && ContinuumMaterials.IsBrittle(e.Material) && peak > CriticalEnergy && _frame - _lastEvent >= Cooldown && Events < MaxEvents)
            Fracture(e, e.Position(best));
        UpdateFragments(e);
        double sum = 0;
        var dent = 0f;
        for (var v = 0; v < Vertices; v++)
        {
            var d = Vertex(v) - _rest[v];
            sum += d.Length();
            var inw = -Vector3.Dot(d, _normal[v]);
            if (_normal[v].Y > -0.5f && inw > dent) dent = inw;   // 바닥면 제외
        }
        MeanDisplacementMm = (float)(sum / Vertices) * 1000f;
        DentMm = dent * 1000f;
        if (DentMm > MaxDentMm) MaxDentMm = DentMm;
    }

    /// <summary>충격 중심 c 에서 보로노이 분할.</summary>
    public void Fracture(RealtimeMpmContinuumPhysicsEngine e, Vector3 c)
    {
        Events++;
        _lastEvent = _frame;
        LastImpact = c;
        for (var s = 0; s < SeedsPerEvent; s++)
        {
            var r = s < SeedsPerEvent / 2 ? 0.035f : FractureRadius;
            Vector3 d;
            do d = new Vector3(Rand() * 2f - 1f, Rand() * 2f - 1f, Rand() * 2f - 1f); while (d.LengthSquared() > 1f);
            _seeds[s] = c + d * r;
            var slot = (short)_next;
            _next = _next + 1 >= Ring ? 1 : _next + 1;
            if (_ring[slot].Live)
            {
                Recycled++;
                for (var i = 0; i < e.Count; i++) if (_frag[i] == slot) _frag[i] = 0;   // 가장 오래된 파편 본체로 회수
            }
            _seedSlot[s] = slot;
            _ring[slot] = new FragmentInstance { Id = _nextId++, Event = Events, BirthFrame = _frame, Seed = _seeds[s], Live = true };
        }
        var rr = FractureRadius * 1.2f;
        var cracks = 0;
        for (var i = 0; i < e.Count; i++)
        {
            var p = e.Position(i);
            if (Vector3.DistanceSquared(p, c) > rr * rr) continue;
            int s1 = 0, s2 = 1;
            float d1 = float.MaxValue, d2 = float.MaxValue;
            for (var s = 0; s < SeedsPerEvent; s++)
            {
                var d = Vector3.DistanceSquared(p, _seeds[s]);
                if (d < d1) { d2 = d1; s2 = s1; d1 = d; s1 = s; }
                else if (d < d2) { d2 = d; s2 = s; }
            }
            _frag[i] = _seedSlot[s1];
            // 이등분면까지 거리 = (d2² − d1²) / (2|s1 − s2|)
            var gap = (d2 - d1) / (2f * MathF.Max(Vector3.Distance(_seeds[s1], _seeds[s2]), 1e-6f));
            if (gap < CrackWidth && e.Damage(i) < 1f) { e.SetDamage(i, 1f); cracks++; }
        }
        CrackParticles += cracks;
    }

    private void UpdateFragments(RealtimeMpmContinuumPhysicsEngine e)
    {
        Array.Clear(_sum);
        Array.Fill(_mn, float.MaxValue);
        Array.Fill(_mx, float.MinValue);
        for (var i = 0; i < e.Count; i++)
        {
            if (e.Damage(i) >= 1f) continue;
            var s = _frag[i];
            var p = e.Position(i);
            var v = e.Velocity(i);
            var o = 7 * s;
            _sum[o] += 1; _sum[o + 1] += p.X; _sum[o + 2] += p.Y; _sum[o + 3] += p.Z; _sum[o + 4] += v.X; _sum[o + 5] += v.Y; _sum[o + 6] += v.Z;
            var b = 3 * s;
            if (p.X < _mn[b]) _mn[b] = p.X; if (p.Y < _mn[b + 1]) _mn[b + 1] = p.Y; if (p.Z < _mn[b + 2]) _mn[b + 2] = p.Z;
            if (p.X > _mx[b]) _mx[b] = p.X; if (p.Y > _mx[b + 1]) _mx[b + 1] = p.Y; if (p.Z > _mx[b + 2]) _mx[b + 2] = p.Z;
        }
        var live = 0;
        for (var s = 0; s < Ring; s++)
        {
            ref var f = ref _ring[s];
            if (!f.Live) continue;
            var o = 7 * s;
            var cnt = (int)_sum[o];
            f.Count = cnt;
            if (cnt == 0) { if (s > 0) f.Live = false; continue; }
            var inv = 1.0 / cnt;
            f.Centroid = new Vector3((float)(_sum[o + 1] * inv), (float)(_sum[o + 2] * inv), (float)(_sum[o + 3] * inv));
            f.Velocity = new Vector3((float)(_sum[o + 4] * inv), (float)(_sum[o + 5] * inv), (float)(_sum[o + 6] * inv));
            f.Min = new Vector3(_mn[3 * s], _mn[3 * s + 1], _mn[3 * s + 2]);
            f.Max = new Vector3(_mx[3 * s], _mx[3 * s + 1], _mx[3 * s + 2]);
            live++;
        }
        LiveFragments = live;
    }

    private float Rand()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
        return (_rng >> 8) * (1f / 16777216f);
    }
}
