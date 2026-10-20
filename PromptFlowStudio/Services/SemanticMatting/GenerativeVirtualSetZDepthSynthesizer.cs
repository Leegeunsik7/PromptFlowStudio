using System.Numerics;
using PromptFlowStudio.Services.OpenUsd;
using PromptFlowStudio.Services.SpatialSlam;

namespace PromptFlowStudio.Services.SemanticMatting;

/// <summary>
/// [PromptFlow Studio v17.08.0] 🏛 생성형 가상 세트 Z-Depth 융합 합성기(무할당 · 프레임 지연 0).
/// 카메라 프레임(원점 · x 오른쪽 · y 위 · z 전방) 핀홀 레이캐스트로 v16.71 OpenUSD 레이어(Cube/Sphere · translate/scale/displayColor · visibility)와
/// v16.98 SLAM RANSAC 바닥 평면(프림 y = 바닥 기준 높이) · LED 볼륨 후면 벽(HDRI 표시)을 래스터 → 색 + z 깊이 버퍼.
/// 같은 프레임의 매트에서 발 접지점(최하단 α 행) 광선 ∩ SLAM 바닥 = 피사체 앵커 깊이 → 픽셀별 z 테스트(소프트 폭 ZSoft) 오클루전 합성.
/// </summary>
public sealed class GenerativeVirtualSetZDepthSynthesizer
{
    public const string DefaultUsda = """
#usda 1.0
(
    defaultPrim = "VirtualSet"
    upAxis = "Y"
    metersPerUnit = 1
)

def Xform "VirtualSet"
{
    def Cube "Desk"
    {
        double size = 1
        double3 xformOp:scale = (1.6, 0.8, 0.5)
        color3f[] primvars:displayColor = [(0.55, 0.36, 0.22)]
        double3 xformOp:translate = (0.95, 0.4, 2.2)
    }
    def Cube "PillarL"
    {
        double size = 1
        double3 xformOp:scale = (0.5, 3, 0.5)
        color3f[] primvars:displayColor = [(0.7, 0.72, 0.78)]
        double3 xformOp:translate = (-2.2, 1.5, 4.6)
    }
    def Cube "PillarR"
    {
        double size = 1
        double3 xformOp:scale = (0.5, 3, 0.5)
        color3f[] primvars:displayColor = [(0.7, 0.72, 0.78)]
        double3 xformOp:translate = (2.4, 1.5, 5.2)
    }
    def Sphere "Globe"
    {
        double radius = 0.32
        color3f[] primvars:displayColor = [(0.2, 0.55, 0.95)]
        double3 xformOp:translate = (-0.95, 0.32, 2.1)
    }
    def Sphere "Orb"
    {
        double radius = 0.2
        color3f[] primvars:displayColor = [(1, 0.82, 0.25)]
        double3 xformOp:translate = (0, 1.2, 3)
    }
    def DistantLight "Key"
    {
        float inputs:intensity = 2.2
        color3f inputs:color = (1, 0.96, 0.9)
    }
}
""";

    public const int MaxObjects = 32;
    public const float WallZ = 7.5f;
    private readonly int _w, _h, _n;
    private readonly float[] _sr, _sg, _sb, _sz, _or, _og, _ob, _vis;
    private readonly sbyte[] _id;
    private readonly Vector3[] _c = new Vector3[MaxObjects], _e = new Vector3[MaxObjects], _col = new Vector3[MaxObjects];
    private readonly float[] _rad = new float[MaxObjects];
    private readonly bool[] _sphere = new bool[MaxObjects], _visible = new bool[MaxObjects];
    private readonly int[] _prim = new int[MaxObjects];
    private int _count;

    public GenerativeVirtualSetZDepthSynthesizer(int width = RealtimeSemanticAlphaMattingEngine.DefaultWidth, int height = RealtimeSemanticAlphaMattingEngine.DefaultHeight)
    {
        _w = width; _h = height; _n = width * height;
        _sr = new float[_n]; _sg = new float[_n]; _sb = new float[_n]; _sz = new float[_n];
        _or = new float[_n]; _og = new float[_n]; _ob = new float[_n]; _vis = new float[_n];
        _id = new sbyte[_n];
        Layer = new UsdLayer("virtual_set.usda");
        LoadUsda(System.Text.Encoding.UTF8.GetBytes(DefaultUsda));
    }

    public int Width => _w;
    public int Height => _h;
    public UsdLayer Layer { get; }
    public float HFovDeg { get; set; } = 60f;
    public float Fx => _w * 0.5f / MathF.Tan(HFovDeg * MathF.PI / 360f);
    /// <summary>SLAM 바닥 평면(n·p + d = 0 · n.Y &gt; 0).</summary>
    public Vector3 FloorN { get; private set; } = Vector3.UnitY;
    public float FloorD { get; private set; } = 0.9f;
    public bool FloorFromSlam { get; private set; }
    /// <summary>z 테스트 소프트 폭(m) — 경계 앨리어싱 방지.</summary>
    public float ZSoft { get; set; } = 0.08f;
    /// <summary>운영자 깊이 오프셋(m · 앵커 깊이에 가산).</summary>
    public float DepthOffset { get; set; }
    public float Exposure { get; set; } = 1f;
    public int ObjectCount => _count;
    public bool SubjectFound { get; private set; }
    public Vector3 Anchor { get; private set; }
    public float SubjectDepth { get; private set; } = float.PositiveInfinity;
    public float SubjectHeightM { get; private set; }
    public int OccludedPixels { get; private set; }
    public int FrontPixels { get; private set; }
    public long SetFrame { get; private set; }
    public long MatteFrame { get; private set; }
    public long LatencyFrames => SetFrame - MatteFrame;
    public double RenderUs { get; private set; }
    public double CompositeUs { get; private set; }
    public UsdError LastUsdError { get; private set; }

    public ReadOnlySpan<float> SetR => _sr;
    public ReadOnlySpan<float> SetG => _sg;
    public ReadOnlySpan<float> SetB => _sb;
    public ReadOnlySpan<float> SetZ => _sz;
    public ReadOnlySpan<sbyte> ObjectId => _id;
    public Span<float> OutR => _or;
    public Span<float> OutG => _og;
    public Span<float> OutB => _ob;
    public ReadOnlySpan<float> Visibility => _vis;
    public string ObjectName(int k) => k < 0 || k >= _count ? "" : Layer.NameOf(_prim[k]).ToString();
    public bool ObjectVisible(int k) => _visible[k];
    public Vector3 ObjectCenter(int k) => _c[k];

    /// <summary>USDA 파싱 → 렌더 프림 표(Cube → AABB · Sphere · DistantLight는 HDRI 키 세기로 소비).</summary>
    public bool LoadUsda(ReadOnlySpan<byte> usda)
    {
        Layer.Clear();
        var ok = UsdaReader.Parse(usda, Layer);
        LastUsdError = Layer.LastError;
        Rebuild();
        return ok;
    }

    public float KeyIntensityFromUsd()
    {
        for (var i = 0; i < Layer.Count; i++)
            if (Layer.TypeOf(i) == UsdPrimType.DistantLight && Layer.Has(i, UsdField.Intensity)) return Layer.Intensity[i];
        return float.NaN;
    }

    /// <summary>SLAM 평면 적용(법선 위쪽 정렬).</summary>
    public void SetFloor(Vector3 n, float d)
    {
        if (n.Y < 0) { n = -n; d = -d; }
        FloorN = n; FloorD = d; FloorFromSlam = true;
        Rebuild();
    }

    public float FloorY(float x, float z) => -(FloorN.X * x + FloorN.Z * z + FloorD) / FloorN.Y;

    /// <summary>프림 표 재구성(USD 변경 · 바닥 변경 시).</summary>
    public void Rebuild()
    {
        _count = 0;
        for (var i = 0; i < Layer.Count && _count < MaxObjects; i++)
        {
            var t = Layer.TypeOf(i);
            if (t != UsdPrimType.Cube && t != UsdPrimType.Sphere) continue;
            float tx = 0, ty = 0, tz = 0;
            for (var p = i; p >= 0; p = Layer.ParentOf(p))
                if (Layer.Has(p, UsdField.Translate)) { var (x, y, z) = Layer.Translate(p); tx += x; ty += y; tz += z; }
            var k = _count++;
            _prim[k] = i;
            _c[k] = new Vector3(tx, ty + FloorY(tx, tz), tz);
            _sphere[k] = t == UsdPrimType.Sphere;
            var (sx, sy, sz) = Layer.Has(i, UsdField.Scale) ? Layer.ScaleOf(i) : (1f, 1f, 1f);
            if (_sphere[k]) _rad[k] = (Layer.Has(i, UsdField.Radius) ? Layer.RadiusOf(i) : 1f) * MathF.Max(sx, MathF.Max(sy, sz));
            else { var h = (Layer.Has(i, UsdField.Size) ? Layer.SizeOf(i) : 2f) * 0.5f; _e[k] = new Vector3(h * sx, h * sy, h * sz); }
            var (cr, cg, cb) = Layer.Has(i, UsdField.Color) ? Layer.Color(i) : (0.7f, 0.7f, 0.7f);
            _col[k] = new Vector3(cr, cg, cb);
            _visible[k] = !Layer.IsInvisible(i);
        }
    }

    public int FindObject(ReadOnlySpan<char> name)
    {
        for (var k = 0; k < _count; k++) if (Layer.NameOf(_prim[k]).SequenceEqual(name)) return k;
        return -1;
    }

    /// <summary>프림 이동(바닥 기준 높이 · 레이어에도 기록).</summary>
    public void MoveObject(int k, float x, float yAboveFloor, float z)
    {
        if ((uint)k >= (uint)_count) return;
        Layer.SetTranslate(_prim[k], x, yAboveFloor, z);
        _c[k] = new Vector3(x, yAboveFloor + FloorY(x, z), z);
    }

    public void SetObjectVisible(int k, bool on)
    {
        if ((uint)k >= (uint)_count) return;
        Layer.SetVisibility(_prim[k], on);
        _visible[k] = on;
    }

    public Vector3 Ray(float u, float v)
    {
        var f = Fx;
        return Vector3.Normalize(new Vector3((u - _w * 0.5f) / f, -(v - _h * 0.5f) / f, 1f));
    }

    public bool Project(Vector3 p, out float u, out float v)
    {
        var f = Fx;
        u = p.X / p.Z * f + _w * 0.5f; v = -p.Y / p.Z * f + _h * 0.5f;
        return p.Z > 0.05f;
    }

    /// <summary>가상 세트 래스터(색 + z 깊이 + 객체 id).</summary>
    public void RenderSet(HdriEnvironment env, long frame)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Span<Vector3> sh = stackalloc Vector3[9];
        for (var k = 0; k < 9; k++) sh[k] = env.Sh(k);
        var ld = env.DominantDir; var lc = env.DominantColor;
        for (var y = 0; y < _h; y++)
            for (var x = 0; x < _w; x++)
            {
                var i = y * _w + x;
                var d = Ray(x + 0.5f, y + 0.5f);
                var best = d.Z > 1e-4f ? WallZ / d.Z : 1e9f;
                var hit = -2;                               // −2 벽 · −1 바닥 · k 객체
                var nrm = new Vector3(0, 0, -1);
                var nd = Vector3.Dot(FloorN, d);
                if (nd < -1e-5f) { var t = -FloorD / nd; if (t > 0 && t < best) { best = t; hit = -1; nrm = FloorN; } }
                for (var k = 0; k < _count; k++)
                {
                    if (!_visible[k]) continue;
                    if (_sphere[k])
                    {
                        var b = Vector3.Dot(d, _c[k]);
                        var c = _c[k].LengthSquared() - _rad[k] * _rad[k];
                        var disc = b * b - c;
                        if (disc < 0) continue;
                        var t = b - MathF.Sqrt(disc);
                        if (t > 0.01f && t < best) { best = t; hit = k; nrm = Vector3.Normalize(d * t - _c[k]); }
                    }
                    else if (Slab(d, _c[k] - _e[k], _c[k] + _e[k], out var t, out var n) && t < best) { best = t; hit = k; nrm = n; }
                }
                var p = d * best;
                Vector3 albedo;
                if (hit == -2)
                {   // LED 볼륨 벽: HDRI 방향 표본 표시 + 패널 그리드.
                    var ex = (int)((MathF.Atan2(p.X, WallZ * 1.4f) / MathF.PI * 0.5f + 0.5f) * HdriEnvironment.W) % HdriEnvironment.W;
                    var ey = Math.Clamp((int)((0.5f - MathF.Atan2(p.Y, WallZ) / MathF.PI) * HdriEnvironment.H), 0, HdriEnvironment.H - 1);
                    var ti = ey * HdriEnvironment.W + Math.Max(0, ex);
                    var grid = (MathF.Abs(p.X * 2f - MathF.Round(p.X * 2f)) < 0.03f || MathF.Abs(p.Y * 2f - MathF.Round(p.Y * 2f)) < 0.03f) ? 0.6f : 1f;
                    var em = new Vector3(env.Texel(0, ti), env.Texel(1, ti), env.Texel(2, ti)) * (0.55f * grid);
                    _sr[i] = MathF.Min(1f, em.X * Exposure); _sg[i] = MathF.Min(1f, em.Y * Exposure); _sb[i] = MathF.Min(1f, em.Z * Exposure);
                    _sz[i] = p.Z; _id[i] = -2;
                    continue;
                }
                if (hit == -1)
                {
                    var chk = (((int)MathF.Floor(p.X * 2f) + (int)MathF.Floor(p.Z * 2f)) & 1) == 0;
                    albedo = chk ? new Vector3(0.42f, 0.42f, 0.45f) : new Vector3(0.3f, 0.3f, 0.33f);
                }
                else albedo = _col[hit];
                var e = HdriEnvironment.Irradiance(sh, nrm);
                var hv = Vector3.Normalize(ld - d);
                var spec = MathF.Pow(MathF.Max(0f, Vector3.Dot(nrm, hv)), 32f) * 0.15f;
                var col = (albedo * e + lc * spec) * Exposure;
                _sr[i] = MathF.Min(1f, col.X); _sg[i] = MathF.Min(1f, col.Y); _sb[i] = MathF.Min(1f, col.Z);
                _sz[i] = p.Z; _id[i] = (sbyte)hit;
            }
        SetFrame = frame;
        RenderUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private static bool Slab(Vector3 d, Vector3 lo, Vector3 hi, out float t, out Vector3 n)
    {
        float t0 = 0f, t1 = 1e9f; var axis = -1; var sgn = 1f;
        for (var a = 0; a < 3; a++)
        {
            float da = a == 0 ? d.X : a == 1 ? d.Y : d.Z, l = a == 0 ? lo.X : a == 1 ? lo.Y : lo.Z, h = a == 0 ? hi.X : a == 1 ? hi.Y : hi.Z;
            if (MathF.Abs(da) < 1e-7f) { if (l > 0 || h < 0) { t = 0; n = default; return false; } continue; }
            float ta = l / da, tb = h / da, s = -1f;
            if (ta > tb) { (ta, tb) = (tb, ta); s = 1f; }
            if (ta > t0) { t0 = ta; axis = a; sgn = s; }
            if (tb < t1) t1 = tb;
            if (t0 > t1) { t = 0; n = default; return false; }
        }
        t = t0;
        n = axis == 0 ? new Vector3(sgn, 0, 0) : axis == 1 ? new Vector3(0, sgn, 0) : new Vector3(0, 0, sgn);
        return axis >= 0 && t0 > 0.01f;
    }

    /// <summary>같은 프레임 매트 → 발 접지 행(최하단 α ≥ 0.5 픽셀 ≥ 3) → 광선 ∩ 바닥 = 앵커.</summary>
    public void PlaceSubject(ReadOnlySpan<float> alpha, long matteFrame)
    {
        MatteFrame = matteFrame;
        int footRow = -1, topRow = -1; float footU = 0;
        for (var y = _h - 1; y >= 0 && footRow < 0; y--)
        {
            int c = 0; float su = 0;
            for (var x = 0; x < _w; x++) if (alpha[y * _w + x] >= 0.5f) { c++; su += x + 0.5f; }
            if (c >= 3) { footRow = y; footU = su / c; }
        }
        for (var y = 0; y < _h && topRow < 0; y++)
            for (var x = 0; x < _w; x++) if (alpha[y * _w + x] >= 0.5f) { topRow = y; break; }
        SubjectFound = false;
        if (footRow < 0) { SubjectDepth = float.PositiveInfinity; return; }
        var d = Ray(footU, footRow + 1f);
        var nd = Vector3.Dot(FloorN, d);
        if (nd > -1e-5f) { SubjectDepth = float.PositiveInfinity; return; }
        var t = -FloorD / nd;
        Anchor = d * t;
        SubjectDepth = Anchor.Z + DepthOffset;
        SubjectHeightM = (footRow + 1 - topRow) / Fx * Anchor.Z;
        SubjectFound = true;
    }

    /// <summary>z 테스트 합성(제자리 출력): v = α · clamp((z_set − z_subj)/ZSoft + ½) · out = v·F + (1−v)·set · v = 0 픽셀은 set 그대로.</summary>
    public void Composite(ReadOnlySpan<float> fr, ReadOnlySpan<float> fg, ReadOnlySpan<float> fb, ReadOnlySpan<float> alpha)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int occ = 0, front = 0;
        var zs = SubjectDepth;
        var inv = 1f / MathF.Max(1e-4f, ZSoft);
        for (var i = 0; i < _n; i++)
        {
            var a = alpha[i];
            var wf = SubjectFound ? Math.Clamp((_sz[i] - zs) * inv + 0.5f, 0f, 1f) : 0f;
            var v = a * wf;
            if (a >= 0.5f) { if (wf < 0.5f) occ++; else front++; }
            _vis[i] = v;
            if (v <= 0f) { _or[i] = _sr[i]; _og[i] = _sg[i]; _ob[i] = _sb[i]; continue; }
            var u = 1f - v;
            _or[i] = v * fr[i] + u * _sr[i]; _og[i] = v * fg[i] + u * _sg[i]; _ob[i] = v * fb[i] + u * _sb[i];
        }
        OccludedPixels = occ; FrontPixels = front;
        CompositeUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }
}
