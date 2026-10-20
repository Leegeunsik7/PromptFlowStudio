using System.Numerics;
using PromptFlowStudio.Services.Mocap;

namespace PromptFlowStudio.Services.AnatomicalTissue;

/// <summary>
/// [PromptFlow Studio v17.14.0] 🫧 연조직 관성 지글 &amp; 동적 압축 주름 합성기(무할당).
///   ▸ 관성 지글(<see cref="ApplyInertia"/>): 연조직 영역 5(복부 · 대퇴 좌우 · 상완 후면 좌우)마다 뼈 부착 앵커 위치 링버퍼(8) → 2차 차분 가속 →
///     3D 감쇠 질량-스프링 ẍ = −ω²x − 2ζωẋ − a_anchor(관성 의사력) · 반암시 오일러 4 서브스텝 · |x| ≤ 3 cm · 정점 변위 = Σᵣ 가중[r][v]·xᵣ(SIMD 누적).
///   ▸ 동적 주름(<see cref="Wrinkles"/>): 축 방향 링 간 간선 압축 변형률 ε = max(0, 1 − l/l₀)(3점 평활) → 높이장 h = A·ε·½(1 + cos(2π·호/λ + φ(θ))) ·
///     법선 섭동 n′ = normalize(n − ∂h/∂s·t) · 튜브 전개 탄젠트 공간 법선 맵(둘레 64 × 축 96 · 쌍선형 변형률).
/// </summary>
public sealed class SoftTissueInertialDynamicsWrinkleSynthesizer
{
    public const int Regions = 5, Hist = 8, MapW = 64, MapH = 96;
    public const float MaxOffset = 0.03f, Lambda = 0.012f;
    private const int V = NeuralAnatomicalMuscleFasciaEngine.V, Segs = NeuralAnatomicalMuscleFasciaEngine.Segs, Rings = NeuralAnatomicalMuscleFasciaEngine.Rings;
    public static readonly string[] RegionNames = { "복부", "대퇴(좌)", "대퇴(우)", "상완 후면(좌)", "상완 후면(우)" };
    private static readonly int[] RegionBone = { MocapSkeleton.Pelvis, MocapSkeleton.LHip, MocapSkeleton.RHip, MocapSkeleton.LShoulder, MocapSkeleton.RShoulder };
    private static readonly float[] RegionMass = { 1f, 0.8f, 0.8f, 0.55f, 0.55f };

    private readonly float[] _w = new float[Regions * V];
    private readonly float[] _cx = new float[Regions], _cy = new float[Regions], _cz = new float[Regions];
    private readonly float[] _jx = new float[V], _jy = new float[V], _jz = new float[V];
    private readonly Vector3[] _anchorRest = new Vector3[Regions], _x = new Vector3[Regions], _v = new Vector3[Regions];
    private readonly Vector3[] _hist = new Vector3[Regions * Hist];
    private readonly float[] _strain = new float[V], _wrinkle = new float[V];
    private readonly float[] _restEdge = new float[V];
    private readonly Vector3[] _wn = new Vector3[V];
    private readonly float[] _map = new float[MapW * MapH * 3];
    private int _histHead, _histCount;

    public SoftTissueInertialDynamicsWrinkleSynthesizer(NeuralAnatomicalMuscleFasciaEngine engine)
    {
        Engine = engine;
        for (var r = 0; r < Regions; r++)
        {
            int tube; float s0, s1, th, width;
            switch (r)
            {
                case 0: tube = 0; s0 = 0.05f; s1 = 0.5f; th = 0f; width = 1.7f; break;
                case 1: case 2: tube = r == 1 ? 3 : 4; s0 = 0.03f; s1 = 0.48f; th = MathF.PI; width = 2.4f; break;
                default: tube = r == 3 ? 1 : 2; s0 = 0.08f; s1 = 0.48f; th = MathF.PI * 0.5f; width = 1.5f; break;
            }
            var sum = Vector3.Zero; var wsum = 0f;
            for (var v = 0; v < V; v++)
            {
                if (v / (Rings * Segs) != tube) continue;
                var s = engine.RingS(v / Segs);
                var t = MathF.Tau * (v % Segs) / Segs;
                var prof = s <= s0 || s >= s1 ? 0f : MathF.Pow(MathF.Sin(MathF.PI * (s - s0) / (s1 - s0)), 2f);
                var dth = MathF.Abs(MathF.IEEERemainder(t - th, MathF.Tau));
                var ang = dth >= width ? 0f : MathF.Pow(MathF.Cos(dth / width * MathF.PI * 0.5f), 2f);
                var w = prof * ang;
                _w[r * V + v] = w;
                sum += w * engine.RestVertex(v); wsum += w;
            }
            _anchorRest[r] = wsum > 0 ? sum / wsum : engine.RestJoint(RegionBone[r]);
        }
        for (var v = 0; v < V; v++)
        {
            var ring = (v / Segs) % Rings;
            _restEdge[v] = ring < Rings - 1 ? Vector3.Distance(engine.RestVertex(v), engine.RestVertex(v + Segs)) : 1f;
        }
        Path = engine.Path;
    }

    public NeuralAnatomicalMuscleFasciaEngine Engine { get; }
    public TissueSimdPath Path { get; set; }
    public bool JiggleEnabled { get; set; } = true;
    public bool WrinkleEnabled { get; set; } = true;
    /// <summary>연조직 고유 진동수(Hz).</summary>
    public float FrequencyHz { get; set; } = 3.2f;
    /// <summary>감쇠비 ζ.</summary>
    public float Damping { get; set; } = 0.18f;
    public float JiggleGain { get; set; } = 0.3f;
    /// <summary>주름 진폭(m).</summary>
    public float WrinkleAmplitude { get; set; } = 0.0025f;
    public Vector3 Offset(int r) => _x[r];
    public float OffsetMm(int r) => _x[r].Length() * 1000f;
    public float Weight(int r, int v) => _w[r * V + v];
    public Vector3 AnchorAccel(int r) { Accel(r, out var a); return a; }
    public float Strain(int v) => _strain[v];
    public float Wrinkle(int v) => _wrinkle[v];
    public Vector3 WrinkleNormal(int v) => _wn[v];
    public float MaxJiggleMm { get; private set; }
    public float MaxStrain { get; private set; }
    public int WrinkledVertices { get; private set; }
    public double LastJiggleUs { get; private set; }
    public double LastWrinkleUs { get; private set; }
    public float MapNormal(int x, int y, int c) => _map[(y * MapW + x) * 3 + c];

    public void Reset()
    {
        Array.Clear(_x); Array.Clear(_v); _histCount = 0; _histHead = 0;
    }

    private Vector3 Anchor(int r)
    {
        var b = RegionBone[r];
        var e = Engine;
        // 앵커 = 영역 레스트 중심의 뼈 강체 변환(회전 시 접선/구심 가속 포함)
        return e.Joint(b) + Vector3.Transform(_anchorRest[r] - e.RestJoint(b), e.JointRotation(b));
    }

    private bool Accel(int r, out Vector3 a)
    {
        a = Vector3.Zero;
        if (_histCount < 3 || _lastDt <= 0) return false;
        int i0 = (_histHead - 1 + Hist) % Hist, i1 = (_histHead - 2 + Hist) % Hist, i2 = (_histHead - 3 + Hist) % Hist;
        a = (_hist[r * Hist + i0] - 2f * _hist[r * Hist + i1] + _hist[r * Hist + i2]) / (_lastDt * _lastDt);
        return true;
    }

    private float _lastDt;

    /// <summary>앵커 가속 → 감쇠 진동 → 정점 지글 변위 가산(엔진 Px/Py/Pz). 무할당.</summary>
    public void ApplyInertia(float dt)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _lastDt = dt;
        for (var r = 0; r < Regions; r++) _hist[r * Hist + _histHead] = Anchor(r);
        _histHead = (_histHead + 1) % Hist;
        if (_histCount < Hist) _histCount++;
        var maxMm = 0f;
        var om = MathF.Tau * FrequencyHz;
        for (var r = 0; r < Regions; r++)
        {
            if (!JiggleEnabled || dt <= 0) { _x[r] = Vector3.Zero; _v[r] = Vector3.Zero; }
            else
            {
                Accel(r, out var a);
                var amag = a.Length();
                if (amag > 400f) a *= 400f / amag;   // 수치 튐 차단
                var h = dt / 4f;
                for (var s = 0; s < 4; s++)
                {
                    _v[r] += (-om * om * _x[r] - 2f * Damping * om * _v[r] - JiggleGain * RegionMass[r] * a) * h;
                    _x[r] += _v[r] * h;
                }
                var l = _x[r].Length();
                if (l > MaxOffset) { _x[r] *= MaxOffset / l; _v[r] *= 0.5f; }
            }
            _cx[r] = _x[r].X; _cy[r] = _x[r].Y; _cz[r] = _x[r].Z;
            maxMm = MathF.Max(maxMm, _x[r].Length() * 1000f);
        }
        MaxJiggleMm = maxMm;
        NeuralAnatomicalMuscleFasciaEngine.Accumulate(Path, _w, _cx, Regions, V, _jx);
        NeuralAnatomicalMuscleFasciaEngine.Accumulate(Path, _w, _cy, Regions, V, _jy);
        NeuralAnatomicalMuscleFasciaEngine.Accumulate(Path, _w, _cz, Regions, V, _jz);
        var e = Engine;
        for (var v = 0; v < V; v++) { e.Px[v] += _jx[v]; e.Py[v] += _jy[v]; e.Pz[v] += _jz[v]; }
        LastJiggleUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>단일 영역 진동자 단계(테스트 · 해석 검증용 · 앵커 가속 직접 주입).</summary>
    public Vector3 StepOscillator(int r, Vector3 anchorAccel, float dt)
    {
        var om = MathF.Tau * FrequencyHz;
        var h = dt / 4f;
        for (var s = 0; s < 4; s++)
        {
            _v[r] += (-om * om * _x[r] - 2f * Damping * om * _v[r] - JiggleGain * RegionMass[r] * anchorAccel) * h;
            _x[r] += _v[r] * h;
        }
        return _x[r];
    }

    /// <summary>최종 위치 기준 압축 변형률 → 주름 강도 · 섭동 법선 · 법선 맵(무할당).</summary>
    public void Wrinkles(int mapTube)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var e = Engine;
        var maxS = 0f; var n = 0;
        for (var v = 0; v < V; v++)
        {
            var ring = (v / Segs) % Rings;
            if (ring >= Rings - 1) { _strain[v] = 0f; continue; }
            var l = MathF.Sqrt((e.Px[v + Segs] - e.Px[v]) * (e.Px[v + Segs] - e.Px[v]) + (e.Py[v + Segs] - e.Py[v]) * (e.Py[v + Segs] - e.Py[v]) + (e.Pz[v + Segs] - e.Pz[v]) * (e.Pz[v + Segs] - e.Pz[v]));
            _strain[v] = MathF.Max(0f, 1f - l / _restEdge[v]);
        }
        for (var v = 0; v < V; v++)
        {
            var ring = (v / Segs) % Rings;
            float a = ring > 0 ? _strain[v - Segs] : _strain[v], c = ring < Rings - 2 ? _strain[v + Segs] : _strain[v];
            var s = WrinkleEnabled ? 0.25f * a + 0.5f * _strain[v] + 0.25f * c : 0f;
            var th = MathF.Tau * (v % Segs) / Segs;
            var arc = e.RingArc(v / Segs);
            var ph = MathF.Tau * arc / Lambda + 0.6f * MathF.Sin(3f * th);
            var amp = WrinkleAmplitude * MathF.Min(1f, s * 4f);
            _wrinkle[v] = amp * 0.5f * (1f + MathF.Cos(ph));
            var dh = -amp * 0.5f * MathF.Sin(ph) * MathF.Tau / Lambda;
            var nn = e.Normal(v) - dh * e.Tangent(v);
            _wn[v] = Vector3.Normalize(nn);
            if (s > maxS) maxS = s;
            if (amp > WrinkleAmplitude * 0.25f) n++;
        }
        MaxStrain = maxS; WrinkledVertices = n;
        BuildMap(mapTube);
        LastWrinkleUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>튜브 전개 탄젠트 공간 법선 맵(x = 둘레 · y = 축 · RGB = (n_u, n_s, n_n)).</summary>
    private void BuildMap(int tube)
    {
        var e = Engine;
        var b = tube * Rings * Segs;
        var len = e.RingArc(tube * Rings + Rings - 1);
        for (var y = 0; y < MapH; y++)
        {
            var fr = y * (Rings - 1f) / (MapH - 1);
            var r0 = Math.Min((int)fr, Rings - 2); var fy = fr - r0;
            var arc = len * y / (MapH - 1);
            for (var x = 0; x < MapW; x++)
            {
                var fs = x * (float)Segs / MapW;
                var s0 = (int)fs % Segs; var s1 = (s0 + 1) % Segs; var fx = fs - (int)fs;
                float a00 = _strain[b + r0 * Segs + s0], a01 = _strain[b + r0 * Segs + s1], a10 = _strain[b + (r0 + 1) * Segs + s0], a11 = _strain[b + (r0 + 1) * Segs + s1];
                var st = WrinkleEnabled ? (a00 * (1 - fx) + a01 * fx) * (1 - fy) + (a10 * (1 - fx) + a11 * fx) * fy : 0f;
                var th = MathF.Tau * x / MapW;
                var ph = MathF.Tau * arc / Lambda + 0.6f * MathF.Sin(3f * th);
                var amp = MathF.Min(1f, st * 4f);
                var ds = -amp * 0.5f * MathF.Sin(ph) * MathF.Tau * 0.0025f / Lambda * 4f;
                var du = -amp * 0.5f * MathF.Cos(ph) * 0.6f * 3f * MathF.Cos(3f * th) * 0.08f;
                var nrm = Vector3.Normalize(new Vector3(-du, -ds, 1f));
                var o = (y * MapW + x) * 3;
                _map[o] = nrm.X; _map[o + 1] = nrm.Y; _map[o + 2] = nrm.Z;
            }
        }
    }
}
