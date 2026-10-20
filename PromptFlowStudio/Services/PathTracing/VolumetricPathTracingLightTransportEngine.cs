using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Neural3d;
using PromptFlowStudio.Services.OpenUsd;

namespace PromptFlowStudio.Services.PathTracing;

public enum PtPrimKind : byte { None = 0, Sphere, Box, Ground }

/// <summary>
/// [PromptFlow Studio v16.74.0] ✨ 볼류메트릭 패스 트레이싱 광원 전송 엔진(CPU · 무할당).
///   ▸ 장면: OpenUSD 합성 스테이지(v16.72 Sphere/Cube · 월드 변환 · 재질 바인딩) + 3DGS(v16.58) 가우시안 → 32³ 이질 밀도/색 그리드 + 무한 바닥.
///   ▸ 교차: 구 SoA 를 <see cref="Vector{T}"/> 레인 단위 일괄 교차(SIMD) · 박스 슬랩 · 바닥 평면.
///   ▸ 광원 전송: 유니/양방향 없는 단방향 경로 추적 — 태양 NEE(그림자 광선 = 비율 추적 투과율) · 하늘 환경광 · 발광 재질 · 러시안 룰렛.
///   ▸ 참여 매질: 높이·AABB 제한 균질 안개(σt · 알베도 · Henyey–Greenstein g) + 3DGS 그리드 — 결합 다수결(majorant) 델타 추적 · 위상 함수 샘플링.
///   ▸ 서브서피스: 재질 Subsurface 확률로 프리미티브 내부 랜덤워크(σt = 1/평균자유행정 · 산란 알베도 = SubsurfaceColor) → 출구 램버트 방출.
///   ▸ AOV: 컬러(SPP 평균) · 휘도 2차 모멘트 · 1차 히트 알베도/법선/깊이 · 이전 카메라 재투영 모션 벡터(픽셀) · 정적 시 점진 누적 참조.
/// </summary>
public sealed class VolumetricPathTracingLightTransportEngine
{
    public const int MaxSpheres = 128, MaxBoxes = 64, Grid = 32;
    private const float Eps = 1e-3f;

    private readonly object _gate = new();
    private readonly MaterialXBsdfShaderSynthesizer _mtl;
    // 구 SoA(SIMD 패딩: 미사용 r² = −1)
    private readonly float[] _sx = new float[MaxSpheres], _sy = new float[MaxSpheres], _sz = new float[MaxSpheres], _sr2 = new float[MaxSpheres], _sr = new float[MaxSpheres];
    private readonly int[] _smat = new int[MaxSpheres];
    private int _ns;
    private readonly Vector3[] _bmin = new Vector3[MaxBoxes], _bmax = new Vector3[MaxBoxes];
    private readonly int[] _bmat = new int[MaxBoxes];
    private int _nb;
    // 3DGS 그리드
    private readonly float[] _gd = new float[Grid * Grid * Grid], _gr = new float[Grid * Grid * Grid], _gg = new float[Grid * Grid * Grid], _gb = new float[Grid * Grid * Grid];
    private Vector3 _gMin, _gMax, _gCell;
    private float _gMaxD;
    private bool _hasGrid;
    // 프레임 버퍼
    private int _w, _h;
    private float[] _color = Array.Empty<float>(), _m2 = Array.Empty<float>(), _albedo = Array.Empty<float>(), _normal = Array.Empty<float>(), _depth = Array.Empty<float>(),
        _mvx = Array.Empty<float>(), _mvy = Array.Empty<float>(), _accum = Array.Empty<float>();
    private int _accumN;
    // 카메라
    private Vector3 _eye = new(0, 1.5f, 6), _fwd, _right, _up;
    private float _tanH, _fov = 45f;
    private Vector3 _pEye, _pFwd, _pRight, _pUp;
    private float _pTanH;
    private bool _hasPrev, _dirty = true;
    private long _frames, _rays, _renderTicks;

    public VolumetricPathTracingLightTransportEngine(MaterialXBsdfShaderSynthesizer materials)
    {
        _mtl = materials;
        Array.Fill(_sr2, -1f);
        SetResolution(160, 90);
        SetCamera(new Vector3(0, 1.5f, 6), new Vector3(0, 0.8f, 0), 45f);
        _pEye = _eye; _pFwd = _fwd; _pRight = _right; _pUp = _up; _pTanH = _tanH;
    }

    public MaterialXBsdfShaderSynthesizer Materials => _mtl;
    public int Width => _w;
    public int Height => _h;
    public int Spp { get; set; } = 1;
    public int MaxBounces { get; set; } = 6;
    public bool Ground { get; set; } = true;
    public int GroundMaterial { get; set; }
    public Vector3 SkyZenith { get; set; } = new(0.35f, 0.55f, 0.95f);
    public Vector3 SkyHorizon { get; set; } = new(0.85f, 0.88f, 0.95f);
    public Vector3 SunDirection { get; set; } = Vector3.Normalize(new Vector3(0.4f, 0.8f, 0.3f));
    public Vector3 SunRadiance { get; set; } = new(3.2f, 3.0f, 2.7f);
    public float FogDensity { get; set; }
    public Vector3 FogAlbedo { get; set; } = new(0.9f);
    public float FogAnisotropy { get; set; } = 0.3f;
    public Vector3 FogMin { get; set; } = new(-40, -1, -40);
    public Vector3 FogMax { get; set; } = new(40, 8, 40);
    public float GridDensityScale { get; set; } = 1f;
    public float MaxDistance { get; set; } = 200f;
    public int SphereCount => _ns;
    public int BoxCount => _nb;
    public bool HasGrid => _hasGrid;
    public float GridMaxDensity => _gMaxD;
    public long Frames => Interlocked.Read(ref _frames);
    public long RaysTraced => Interlocked.Read(ref _rays);
    public double LastRenderMs => _renderTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    public int AccumulatedFrames => _accumN;
    public Vector3 Eye => _eye;
    public static int SimdLanes => Vector<float>.Count;

    public ReadOnlySpan<float> Color => _color;
    public ReadOnlySpan<float> SecondMoment => _m2;
    public ReadOnlySpan<float> Albedo => _albedo;
    public ReadOnlySpan<float> Normal => _normal;
    public ReadOnlySpan<float> Depth => _depth;
    public ReadOnlySpan<float> MotionX => _mvx;
    public ReadOnlySpan<float> MotionY => _mvy;
    public ReadOnlySpan<float> Accumulated => _accum;

    /// <summary>설정 변경 후 점진 누적 초기화.</summary>
    public void Invalidate() => _dirty = true;

    // ══════════════════════ 구성(제어 평면) ══════════════════════

    public void SetResolution(int w, int h)
    {
        lock (_gate)
        {
            _w = Math.Clamp(w, 8, 3840); _h = Math.Clamp(h, 8, 2160);
            var n = _w * _h;
            _color = new float[n * 3]; _m2 = new float[n]; _albedo = new float[n * 3]; _normal = new float[n * 3]; _depth = new float[n];
            _mvx = new float[n]; _mvy = new float[n]; _accum = new float[n * 3];
            _accumN = 0; _hasPrev = false; _dirty = true;
        }
    }

    public void SetCamera(Vector3 eye, Vector3 target, float fovDeg)
    {
        lock (_gate)
        {
            _eye = eye; _fov = Math.Clamp(fovDeg, 5f, 120f);
            _fwd = Vector3.Normalize(target - eye);
            _right = Vector3.Normalize(Vector3.Cross(_fwd, Vector3.UnitY));
            _up = Vector3.Cross(_right, _fwd);
            _tanH = MathF.Tan(_fov * MathF.PI / 360f);
        }
    }

    public void ClearScene()
    {
        lock (_gate)
        {
            _ns = 0; _nb = 0; _hasGrid = false; _gMaxD = 0;
            Array.Fill(_sr2, -1f);
            _dirty = true;
        }
    }

    public int AddSphere(Vector3 c, float r, int material)
    {
        lock (_gate)
        {
            if (_ns >= MaxSpheres) return -1;
            var i = _ns++;
            _sx[i] = c.X; _sy[i] = c.Y; _sz[i] = c.Z; _sr[i] = MathF.Max(1e-3f, r); _sr2[i] = _sr[i] * _sr[i]; _smat[i] = material;
            _dirty = true;
            return i;
        }
    }

    public int AddBox(Vector3 min, Vector3 max, int material)
    {
        lock (_gate)
        {
            if (_nb >= MaxBoxes) return -1;
            var i = _nb++;
            _bmin[i] = Vector3.Min(min, max); _bmax[i] = Vector3.Max(min, max); _bmat[i] = material;
            _dirty = true;
            return i;
        }
    }

    /// <summary>
    /// USD 합성 스테이지 → 프리미티브(정의 · 가시 Sphere/Cube). 재질 = 프림 이름과 같은 라이브러리 재질, 없으면 프림 색 플라스틱("usd:이름") 자동 등록. 반환 = 프리미티브 수.
    /// </summary>
    public int BuildFromUsd(OpenUsdSpatialAssetCompositorEngine comp, bool clear = true)
    {
        if (clear) ClearScene();
        var st = comp.Stage;
        var n = 0;
        for (var i = 0; i < st.Count; i++)
        {
            var s = comp.State(i);
            if (!s.Defined || !s.Visible || s.Type is not (UsdPrimType.Sphere or UsdPrimType.Cube)) continue;
            var name = new string(st.NameOf(i));
            var mat = _mtl.Find(name);
            if (mat < 0)
            {
                var m = PbrMaterial.Default;
                var (r, g, b) = st.Color(i);
                m.BaseColor = new Vector3(r, g, b);
                mat = _mtl.Register("usd:" + name, m);
                if (mat < 0) mat = 0;
            }
            var c = new Vector3(s.X, s.Y, s.Z);
            if (s.Type == UsdPrimType.Sphere) { if (AddSphere(c, st.RadiusOf(i) * s.Scale, mat) >= 0) n++; }
            else
            {
                var h = new Vector3(st.SizeOf(i) * 0.5f * s.Scale);
                if (AddBox(c - h, c + h, mat) >= 0) n++;
            }
        }
        return n;
    }

    /// <summary>3DGS → 32³ 밀도/색 그리드(가우시안당 3σ 영역 커널 누적 · 등방 σ = 스케일 평균). 무할당.</summary>
    public void BindGaussians(GaussianScene gs, float densityPerOpacity = 8f)
    {
        lock (_gate)
        {
            Array.Clear(_gd); Array.Clear(_gr); Array.Clear(_gg); Array.Clear(_gb);
            _hasGrid = false; _gMaxD = 0; _dirty = true;
            if (gs.Count == 0) return;
            Vector3 mn = new(float.MaxValue), mx = new(float.MinValue);
            for (var i = 0; i < gs.Count; i++)
            {
                var sg = gs.ScaleOf(i); var s3 = 3f * (sg.X + sg.Y + sg.Z) / 3f;
                var p = gs.Position(i);
                mn = Vector3.Min(mn, p - new Vector3(s3)); mx = Vector3.Max(mx, p + new Vector3(s3));
            }
            _gMin = mn; _gMax = mx; _gCell = (mx - mn) / Grid;
            for (var i = 0; i < gs.Count; i++)
            {
                var sg = gs.ScaleOf(i); var sigma = MathF.Max(1e-3f, (sg.X + sg.Y + sg.Z) / 3f);
                var p = gs.Position(i);
                var col = gs.BaseColor(i);
                var dens = gs.Opacity[i] * densityPerOpacity;
                int x0 = Cell(p.X - 3 * sigma, 0), x1 = Cell(p.X + 3 * sigma, 0), y0 = Cell(p.Y - 3 * sigma, 1), y1 = Cell(p.Y + 3 * sigma, 1), z0 = Cell(p.Z - 3 * sigma, 2), z1 = Cell(p.Z + 3 * sigma, 2);
                var inv2s2 = 1f / (2 * sigma * sigma);
                for (var z = z0; z <= z1; z++)
                    for (var y = y0; y <= y1; y++)
                        for (var x = x0; x <= x1; x++)
                        {
                            var c = _gMin + new Vector3((x + 0.5f) * _gCell.X, (y + 0.5f) * _gCell.Y, (z + 0.5f) * _gCell.Z);
                            var w = dens * MathF.Exp(-(c - p).LengthSquared() * inv2s2);
                            var k = (z * Grid + y) * Grid + x;
                            _gd[k] += w; _gr[k] += w * col.X; _gg[k] += w * col.Y; _gb[k] += w * col.Z;
                        }
            }
            for (var k = 0; k < _gd.Length; k++)
            {
                var d = _gd[k];
                if (d <= 1e-6f) { _gd[k] = 0; continue; }
                _gr[k] = Math.Clamp(_gr[k] / d, 0f, 1f); _gg[k] = Math.Clamp(_gg[k] / d, 0f, 1f); _gb[k] = Math.Clamp(_gb[k] / d, 0f, 1f);
                if (d > _gMaxD) _gMaxD = d;
            }
            _hasGrid = _gMaxD > 0;
        }
    }

    private int Cell(float v, int axis)
    {
        var mn = axis == 0 ? _gMin.X : axis == 1 ? _gMin.Y : _gMin.Z;
        var c = axis == 0 ? _gCell.X : axis == 1 ? _gCell.Y : _gCell.Z;
        return Math.Clamp((int)MathF.Floor((v - mn) / MathF.Max(c, 1e-6f)), 0, Grid - 1);
    }

    // ══════════════════════ 교차 ══════════════════════

    /// <summary>최근접 교차(SIMD 구 + 박스 + 바닥).</summary>
    public bool TraceClosest(Vector3 o, Vector3 d, float tMax, out float t, out PtPrimKind kind, out int index)
    {
        t = tMax; kind = PtPrimKind.None; index = -1;
        var vc = Vector<float>.Count;
        var padded = (_ns + vc - 1) / vc * vc;
        if (padded > 0)
        {
            Vector<float> ox = new(o.X), oy = new(o.Y), oz = new(o.Z), dx = new(d.X), dy = new(d.Y), dz = new(d.Z), eps = new(Eps);
            for (var i = 0; i < padded; i += vc)
            {
                var ocx = ox - new Vector<float>(_sx, i); var ocy = oy - new Vector<float>(_sy, i); var ocz = oz - new Vector<float>(_sz, i);
                var b = ocx * dx + ocy * dy + ocz * dz;
                var c = ocx * ocx + ocy * ocy + ocz * ocz - new Vector<float>(_sr2, i);
                var disc = b * b - c;
                var hitMask = Vector.GreaterThan(disc, Vector<float>.Zero);
                if (Vector.EqualsAll(hitMask, Vector<int>.Zero)) continue;
                var sq = Vector.SquareRoot(Vector.Max(disc, Vector<float>.Zero));
                var t0 = -b - sq; var t1 = -b + sq;
                var tt = Vector.ConditionalSelect(Vector.GreaterThan(t0, eps), t0, t1);
                var valid = Vector.BitwiseAnd(hitMask, Vector.BitwiseAnd(Vector.GreaterThan(tt, eps), Vector.LessThan(tt, new Vector<float>(t))));
                if (Vector.EqualsAll(valid, Vector<int>.Zero)) continue;
                for (var l = 0; l < vc; l++)
                    if (valid[l] != 0 && tt[l] < t) { t = tt[l]; kind = PtPrimKind.Sphere; index = i + l; }
            }
        }
        for (var i = 0; i < _nb; i++)
            if (SlabEnter(_bmin[i], _bmax[i], o, d, t, out var tb)) { t = tb; kind = PtPrimKind.Box; index = i; }
        if (Ground && d.Y < -1e-6f)
        {
            var tg = -o.Y / d.Y;
            if (tg > Eps && tg < t) { t = tg; kind = PtPrimKind.Ground; index = -1; }
        }
        return kind != PtPrimKind.None;
    }

    private static bool SlabEnter(Vector3 mn, Vector3 mx, Vector3 o, Vector3 d, float tMax, out float t)
    {
        var inv = new Vector3(1f / d.X, 1f / d.Y, 1f / d.Z);
        var ta = (mn - o) * inv; var tb = (mx - o) * inv;
        var tmin = Vector3.Min(ta, tb); var tmaxv = Vector3.Max(ta, tb);
        var t0 = MathF.Max(MathF.Max(tmin.X, tmin.Y), tmin.Z);
        var t1 = MathF.Min(MathF.Min(tmaxv.X, tmaxv.Y), tmaxv.Z);
        t = t0 > Eps ? t0 : t1;
        return t1 >= t0 && t > Eps && t < tMax;
    }

    private static bool SlabRange(Vector3 mn, Vector3 mx, Vector3 o, Vector3 d, out float t0, out float t1)
    {
        var inv = new Vector3(1f / d.X, 1f / d.Y, 1f / d.Z);
        var ta = (mn - o) * inv; var tb = (mx - o) * inv;
        var tmin = Vector3.Min(ta, tb); var tmaxv = Vector3.Max(ta, tb);
        t0 = MathF.Max(MathF.Max(tmin.X, tmin.Y), MathF.Max(tmin.Z, 0));
        t1 = MathF.Min(MathF.Min(tmaxv.X, tmaxv.Y), tmaxv.Z);
        return t1 > t0;
    }

    private Vector3 NormalAt(PtPrimKind kind, int i, Vector3 p)
    {
        if (kind == PtPrimKind.Sphere) return Vector3.Normalize(p - new Vector3(_sx[i], _sy[i], _sz[i]));
        if (kind == PtPrimKind.Ground) return Vector3.UnitY;
        var c = (_bmin[i] + _bmax[i]) * 0.5f; var h = (_bmax[i] - _bmin[i]) * 0.5f;
        var q = (p - c) / Vector3.Max(h, new Vector3(1e-6f));
        var a = Vector3.Abs(q);
        return a.X >= a.Y && a.X >= a.Z ? new Vector3(MathF.Sign(q.X), 0, 0) : a.Y >= a.Z ? new Vector3(0, MathF.Sign(q.Y), 0) : new Vector3(0, 0, MathF.Sign(q.Z));
    }

    private int MaterialOf(PtPrimKind kind, int i) => kind switch { PtPrimKind.Sphere => _smat[i], PtPrimKind.Box => _bmat[i], _ => GroundMaterial };

    /// <summary>프리미티브 내부 점에서 경계까지 거리(서브서피스 랜덤워크).</summary>
    private float ExitDistance(PtPrimKind kind, int i, Vector3 p, Vector3 d)
    {
        if (kind == PtPrimKind.Sphere)
        {
            var oc = p - new Vector3(_sx[i], _sy[i], _sz[i]);
            var b = Vector3.Dot(oc, d);
            var c = oc.LengthSquared() - _sr2[i];
            return MathF.Max(0, -b + MathF.Sqrt(MathF.Max(0, b * b - c)));
        }
        var inv = new Vector3(1f / d.X, 1f / d.Y, 1f / d.Z);
        var tb = Vector3.Max((_bmin[i] - p) * inv, (_bmax[i] - p) * inv);
        return MathF.Max(0, MathF.Min(MathF.Min(tb.X, tb.Y), tb.Z));
    }

    // ══════════════════════ 매질 ══════════════════════

    private float Majorant => FogDensity + (_hasGrid ? _gMaxD * GridDensityScale : 0);

    private float Sigma(Vector3 x, out bool grid, out Vector3 gridAlbedo)
    {
        grid = false; gridAlbedo = default;
        var s = 0f;
        var fmn = FogMin; var fmx = FogMax;
        if (FogDensity > 0 && x.X >= fmn.X && x.Y >= fmn.Y && x.Z >= fmn.Z && x.X <= fmx.X && x.Y <= fmx.Y && x.Z <= fmx.Z) s = FogDensity;
        if (_hasGrid && x.X >= _gMin.X && x.Y >= _gMin.Y && x.Z >= _gMin.Z && x.X < _gMax.X && x.Y < _gMax.Y && x.Z < _gMax.Z)
        {
            var k = (Cell(x.Z, 2) * Grid + Cell(x.Y, 1)) * Grid + Cell(x.X, 0);
            var g = _gd[k] * GridDensityScale;
            if (g > 0) { grid = true; gridAlbedo = new Vector3(_gr[k], _gg[k], _gb[k]); s += g; }
        }
        return s;
    }

    /// <summary>매질 존재 구간 [t0, t1](안개 AABB ∪ 그리드 AABB) 교차.</summary>
    private bool MediumRange(Vector3 o, Vector3 d, float tMax, out float t0, out float t1)
    {
        t0 = float.MaxValue; t1 = 0;
        if (FogDensity > 0 && SlabRange(FogMin, FogMax, o, d, out var a0, out var a1)) { t0 = MathF.Min(t0, a0); t1 = MathF.Max(t1, a1); }
        if (_hasGrid && SlabRange(_gMin, _gMax, o, d, out var b0, out var b1)) { t0 = MathF.Min(t0, b0); t1 = MathF.Max(t1, b1); }
        t1 = MathF.Min(t1, tMax);
        return t1 > t0;
    }

    /// <summary>델타 추적 — 실 충돌 시 거리·산란 알베도.</summary>
    private bool SampleMedium(Vector3 o, Vector3 d, float tMax, ref Rng rng, out float t, out Vector3 albedo)
    {
        t = 0; albedo = default;
        var maj = Majorant;
        if (maj <= 0 || !MediumRange(o, d, tMax, out var t0, out var t1)) return false;
        t = t0;
        while (true)
        {
            t -= MathF.Log(1 - rng.Next()) / maj;
            if (t >= t1) return false;
            var x = o + d * t;
            var s = Sigma(x, out _, out var ga);
            var u = rng.Next() * maj;
            if (u >= s) continue;   // 가상 충돌
            var sf = FogDensity > 0 && InFog(x) ? FogDensity : 0;
            albedo = u < sf ? FogAlbedo : ga;   // 성분별 소멸 계수 비례 선택
            return true;
        }
    }

    private bool InFog(Vector3 x)
    {
        var mn = FogMin; var mx = FogMax;
        return x.X >= mn.X && x.Y >= mn.Y && x.Z >= mn.Z && x.X <= mx.X && x.Y <= mx.Y && x.Z <= mx.Z;
    }

    /// <summary>비율 추적 투과율(그림자 광선 · 낮은 투과율은 러시안 룰렛 — 비편향).</summary>
    private float Transmittance(Vector3 o, Vector3 d, float tMax, ref Rng rng)
    {
        var maj = Majorant;
        if (maj <= 0 || !MediumRange(o, d, tMax, out var t0, out var t1)) return 1f;
        var tr = 1f; var t = t0;
        while (true)
        {
            t -= MathF.Log(1 - rng.Next()) / maj;
            if (t >= t1) return tr;
            tr *= 1 - Sigma(o + d * t, out _, out _) / maj;
            if (tr < 0.1f)
            {
                if (rng.Next() >= 0.5f) return 0;
                tr *= 2;
            }
        }
    }

    private float SunVisibility(Vector3 p, ref Rng rng)
    {
        var sd = SunDirection;
        if (TraceClosest(p, sd, MaxDistance, out _, out _, out _)) return 0;
        return Transmittance(p, sd, MaxDistance, ref rng);
    }

    private static float HgPhase(float cos, float g)
    {
        var d = 1 + g * g - 2 * g * cos;
        return (1 - g * g) / (4 * MathF.PI * d * MathF.Sqrt(MathF.Max(d, 1e-8f)));
    }

    private static Vector3 HgSample(Vector3 dir, float g, float u1, float u2)
    {
        float cos;
        if (MathF.Abs(g) < 1e-3f) cos = 1 - 2 * u1;
        else { var s = (1 - g * g) / (1 - g + 2 * g * u1); cos = (1 + g * g - s * s) / (2 * g); }
        cos = Math.Clamp(cos, -1f, 1f);
        var sin = MathF.Sqrt(MathF.Max(0, 1 - cos * cos)); var phi = MathF.Tau * u2;
        MaterialXBsdfShaderSynthesizer.Basis(dir, out var t, out var b);
        return Vector3.Normalize(t * (sin * MathF.Cos(phi)) + b * (sin * MathF.Sin(phi)) + dir * cos);
    }

    public Vector3 Sky(Vector3 d) => Vector3.Lerp(SkyHorizon, SkyZenith, Math.Clamp(d.Y, 0f, 1f));

    // ══════════════════════ 적분기 ══════════════════════

    internal struct Rng
    {
        private ulong _s;
        public Rng(ulong s) => _s = s;
        public float Next()
        {
            _s += 0x9E3779B97F4A7C15UL;
            var z = _s;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL; z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL; z ^= z >> 31;
            return (z >> 40) * (1f / (1 << 24));
        }
    }

    private Vector3 TracePath(Vector3 o, Vector3 d, ref Rng rng)
    {
        var L = Vector3.Zero; var T = Vector3.One;
        var mats = _mtl.Items;
        var sun = SunRadiance; var sd = SunDirection;
        var hasSun = sun.X + sun.Y + sun.Z > 0;
        var maxB = Math.Max(1, MaxBounces);
        for (var bounce = 0; bounce < maxB; bounce++)
        {
            _rays++;
            var hit = TraceClosest(o, d, MaxDistance, out var ts, out var kind, out var idx);
            if (SampleMedium(o, d, hit ? ts : MaxDistance, ref rng, out var tm, out var alb))
            {
                var x = o + d * tm;
                T *= alb;
                if (hasSun) L += T * sun * (HgPhase(Vector3.Dot(d, sd), FogAnisotropy) * SunVisibility(x, ref rng));
                d = HgSample(d, FogAnisotropy, rng.Next(), rng.Next());
                o = x;
                if (!RussianRoulette(ref T, bounce, ref rng)) break;
                continue;
            }
            if (!hit) { L += T * Sky(d); break; }
            var p = o + d * ts;
            ref readonly var m = ref mats[MaterialOf(kind, idx)];
            L += T * m.Emission;
            var n = NormalAt(kind, idx, p);
            var wo = -d;
            if (Vector3.Dot(n, wo) < 0) n = -n;
            if (m.Subsurface > 0 && kind != PtPrimKind.Ground && rng.Next() < m.Subsurface)
            {
                // 내부 랜덤워크
                var sigma = 1f / m.SubsurfaceRadius;
                var pos = p - n * Eps;
                var dir = MaterialXBsdfShaderSynthesizer.CosineSample(-n, rng.Next(), rng.Next());
                var exited = false;
                for (var step = 0; step < 256; step++)
                {
                    var dist = -MathF.Log(1 - rng.Next()) / sigma;
                    var exitT = ExitDistance(kind, idx, pos, dir);
                    if (dist >= exitT) { pos += dir * exitT; exited = true; break; }
                    pos += dir * dist;
                    T *= m.SubsurfaceColor;
                    var u = 1 - 2 * rng.Next(); var ph = MathF.Tau * rng.Next(); var r = MathF.Sqrt(MathF.Max(0, 1 - u * u));
                    dir = new Vector3(r * MathF.Cos(ph), r * MathF.Sin(ph), u);
                }
                if (!exited) break;
                var nOut = NormalAt(kind, idx, pos);
                p = pos + nOut * Eps;
                if (hasSun)
                {
                    var cs = Vector3.Dot(nOut, sd);
                    if (cs > 0) L += T * sun * (cs / MathF.PI * SunVisibility(p, ref rng));
                }
                d = MaterialXBsdfShaderSynthesizer.CosineSample(nOut, rng.Next(), rng.Next());
                o = p;
                if (!RussianRoulette(ref T, bounce, ref rng)) break;
                continue;
            }
            var po = p + n * Eps;
            if (hasSun && Vector3.Dot(n, sd) > 0)
            {
                var f = MaterialXBsdfShaderSynthesizer.Eval(m, n, wo, sd, out _);
                if (f.X + f.Y + f.Z > 0) L += T * f * sun * SunVisibility(po, ref rng);
            }
            if (!MaterialXBsdfShaderSynthesizer.Sample(m, n, wo, rng.Next(), rng.Next(), rng.Next(), out var wi, out var w, out _, out _)) break;
            T *= w;
            o = po; d = wi;
            if (!RussianRoulette(ref T, bounce, ref rng)) break;
        }
        return L;
    }

    private static bool RussianRoulette(ref Vector3 T, int bounce, ref Rng rng)
    {
        if (bounce < 3) return true;
        var q = MathF.Max(T.X, MathF.Max(T.Y, T.Z));
        if (q >= 1) return true;
        if (q <= 0 || rng.Next() > q) return false;
        T /= q;
        return true;
    }

    private Vector3 PixelDir(float px, float py, Vector3 fwd, Vector3 right, Vector3 up, float tanH)
    {
        var aspect = (float)_w / _h;
        var nx = (px / _w * 2 - 1) * tanH * aspect;
        var ny = (1 - py / _h * 2) * tanH;
        return Vector3.Normalize(fwd + right * nx + up * ny);
    }

    private bool ProjectPrev(Vector3 p, bool direction, out float px, out float py)
    {
        var rel = direction ? p : p - _pEye;
        var z = Vector3.Dot(rel, _pFwd);
        px = py = 0;
        if (z <= 1e-5f) return false;
        var aspect = (float)_w / _h;
        px = (Vector3.Dot(rel, _pRight) / (z * _pTanH * aspect) + 1) * 0.5f * _w;
        py = (1 - Vector3.Dot(rel, _pUp) / (z * _pTanH)) * 0.5f * _h;
        return true;
    }

    /// <summary>
    /// 1프레임 렌더(SPP · 결정적 시드 = frameIndex) → AOV 갱신 → 카메라/장면 정적이면 점진 누적. 무할당.
    /// </summary>
    public void RenderFrame(long frameIndex)
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("PathTrace.Frame");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var camMoved = !_hasPrev || _eye != _pEye || _fwd != _pFwd || _tanH != _pTanH;
            if (!_hasPrev) { _pEye = _eye; _pFwd = _fwd; _pRight = _right; _pUp = _up; _pTanH = _tanH; }
            var mats = _mtl.Items;
            var spp = Math.Clamp(Spp, 1, 4096);
            var inv = 1f / spp;
            for (var y = 0; y < _h; y++)
                for (var x = 0; x < _w; x++)
                {
                    var p = y * _w + x;
                    var acc = Vector3.Zero; var m2 = 0f;
                    for (var s = 0; s < spp; s++)
                    {
                        var rng = new Rng(Hash((ulong)frameIndex, (ulong)p, (ulong)s));
                        var dir = PixelDir(x + rng.Next(), y + rng.Next(), _fwd, _right, _up, _tanH);
                        var L = TracePath(_eye, dir, ref rng);
                        if (!float.IsFinite(L.X + L.Y + L.Z)) L = Vector3.Zero;
                        acc += L;
                        var lum = 0.2126f * L.X + 0.7152f * L.Y + 0.0722f * L.Z;
                        m2 += lum * lum;
                    }
                    acc *= inv;
                    _color[p * 3] = acc.X; _color[p * 3 + 1] = acc.Y; _color[p * 3 + 2] = acc.Z;
                    _m2[p] = m2 * inv;
                    // AOV(픽셀 중심 1차 히트)
                    var cd = PixelDir(x + 0.5f, y + 0.5f, _fwd, _right, _up, _tanH);
                    if (TraceClosest(_eye, cd, MaxDistance, out var th, out var kind, out var idx))
                    {
                        var hp = _eye + cd * th;
                        ref readonly var mm = ref mats[MaterialOf(kind, idx)];
                        var a = mm.BaseColor + mm.Emission;
                        var nrm = NormalAt(kind, idx, hp);
                        if (Vector3.Dot(nrm, cd) > 0) nrm = -nrm;
                        _albedo[p * 3] = a.X; _albedo[p * 3 + 1] = a.Y; _albedo[p * 3 + 2] = a.Z;
                        _normal[p * 3] = nrm.X; _normal[p * 3 + 1] = nrm.Y; _normal[p * 3 + 2] = nrm.Z;
                        _depth[p] = th;
                        if (ProjectPrev(hp, false, out var qx, out var qy)) { _mvx[p] = qx - (x + 0.5f); _mvy[p] = qy - (y + 0.5f); }
                        else { _mvx[p] = 0; _mvy[p] = 0; }
                    }
                    else
                    {
                        _albedo[p * 3] = _albedo[p * 3 + 1] = _albedo[p * 3 + 2] = 1f;
                        _normal[p * 3] = -cd.X; _normal[p * 3 + 1] = -cd.Y; _normal[p * 3 + 2] = -cd.Z;
                        _depth[p] = 0;
                        if (ProjectPrev(cd, true, out var qx, out var qy)) { _mvx[p] = qx - (x + 0.5f); _mvy[p] = qy - (y + 0.5f); }
                        else { _mvx[p] = 0; _mvy[p] = 0; }
                    }
                }
            // 점진 누적(SIMD)
            if (camMoved || _dirty) { Array.Clear(_accum); _accumN = 0; _dirty = false; }
            _accumN++;
            var wNew = 1f / _accumN;
            var va = System.Runtime.InteropServices.MemoryMarshal.Cast<float, Vector<float>>(_accum.AsSpan());
            var vcur = System.Runtime.InteropServices.MemoryMarshal.Cast<float, Vector<float>>(_color.AsSpan());
            var vw = new Vector<float>(wNew);
            for (var i = 0; i < va.Length; i++) va[i] += (vcur[i] - va[i]) * vw;
            for (var i = va.Length * Vector<float>.Count; i < _accum.Length; i++) _accum[i] += (_color[i] - _accum[i]) * wNew;
            _pEye = _eye; _pFwd = _fwd; _pRight = _right; _pUp = _up; _pTanH = _tanH; _hasPrev = true;
            _frames++;
            _renderTicks = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        }
    }

    /// <summary>
    /// v16.96.0 분산 타일 렌더(무잠금 · 무할당): 픽셀 시드 = RenderFrame과 동일(frameIndex, 전체 프레임 픽셀 인덱스, s) → 타일 분할과 무관하게 Color와 비트 일치.
    /// dst = 타일 원점 기준 RGB(행 간격 dstStride floats). 장면/카메라/해상도 변경과 동시 호출 금지(스웜 프레임 경계에서만 구성). 광선 카운터는 근사.
    /// </summary>
    public void RenderTile(long frameIndex, int x0, int y0, int tw, int th, Span<float> dst, int dstStride)
    {
        var spp = Math.Clamp(Spp, 1, 4096);
        var inv = 1f / spp;
        for (var y = y0; y < y0 + th; y++)
        {
            var row = dst.Slice((y - y0) * dstStride, tw * 3);
            for (var x = x0; x < x0 + tw; x++)
            {
                var p = y * _w + x;
                var acc = Vector3.Zero;
                for (var s = 0; s < spp; s++)
                {
                    var rng = new Rng(Hash((ulong)frameIndex, (ulong)p, (ulong)s));
                    var dir = PixelDir(x + rng.Next(), y + rng.Next(), _fwd, _right, _up, _tanH);
                    var L = TracePath(_eye, dir, ref rng);
                    if (!float.IsFinite(L.X + L.Y + L.Z)) L = Vector3.Zero;
                    acc += L;
                }
                acc *= inv;
                var o = (x - x0) * 3;
                row[o] = acc.X; row[o + 1] = acc.Y; row[o + 2] = acc.Z;
            }
        }
    }

    /// <summary>단일 광선 방사휘도(검증·프로브용 · 결정적).</summary>
    public Vector3 Radiance(Vector3 o, Vector3 d, ulong seed)
    {
        lock (_gate)
        {
            var rng = new Rng(seed);
            return TracePath(o, Vector3.Normalize(d), ref rng);
        }
    }

    private static ulong Hash(ulong a, ulong b, ulong c)
    {
        var z = a * 0x9E3779B97F4A7C15UL ^ b * 0xC2B2AE3D27D4EB4FUL ^ c * 0x165667B19E3779F9UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL; z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
