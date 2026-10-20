using System.Numerics;
namespace PromptFlowStudio.Services.GenlockFrustum;

/// <summary>6DoF 카메라 트래커 포즈 + 렌즈 화각(수평 FoV 는 아나모픽 스퀴즈 반영).</summary>
public struct TrackedCameraPose
{
    public Vector3 Position, Forward, Right, Up;
    public float HFovDeg, VFovDeg, FocalMm, TStop, FocusM;
}

/// <summary>
/// [PromptFlow Studio v17.44.0] 🖥️ 실시간 오프액시스 비대칭 투영 &amp; LED 볼륨 워핑 엔진(<see cref="Compute"/> 60 Hz 무할당).
/// 반경 6 m · 240° 곡면 LED 월(24 열 × 4 행 평면 패싯)의 패널마다 일반화 원근 투영(Kooima)으로
/// 비대칭 절두체 l/r/b/t 와 전단 계수((r+l)/(r−l), (t+b)/(t−b))를 Vector&lt;T&gt; SoA 커널(스칼라 비트 동일)로 산출하고
/// 패널별 투영 행렬(평행이동 × 패널 기저 × 오프센터 원근)을 갱신한다. 이어서 카메라 화각 경계 광선을 원통에 교차시켜
/// 인너 프러스텀 영역(θ/높이)을 구하고, LED 피치의 센서 투영 간격(px) · 초점 이탈 CoC 로 모아레 위험/프리블러 맵과
/// 시야각 감쇠 게인 · 패싯 경계(베젤) 밀도 단차를 계산한다.
/// </summary>
public sealed class RealtimeOffAxisFrustumProjectionEngine
{
    public const int Cols = 24, Rows = 4, Panels = Cols * Rows, BoundarySamples = 32;
    public const float RadiusM = 6f, ArcDeg = 240f, PanelHeightM = 1.5f, WallHeightM = Rows * PanelHeightM;
    public const float PitchMm = 2.6f, NearM = 0.05f, FarM = 500f, SensorPxW = 4096f, MaxBlurPx = 1.5f, MaxGain = 2f, FalloffGamma = 0.6f, MoireSigma = 0.45f;
    public static readonly float ColStepRad = ArcDeg / Cols * MathF.PI / 180f, HalfArcRad = 0.5f * ArcDeg * MathF.PI / 180f;
    public static readonly float ChordM = 2f * RadiusM * MathF.Sin(0.5f * ColStepRad);

    private readonly float[] _pax = new float[Panels], _pay = new float[Panels], _paz = new float[Panels];
    private readonly float[] _pbx = new float[Panels], _pby = new float[Panels], _pbz = new float[Panels];
    private readonly float[] _pcx = new float[Panels], _pcy = new float[Panels], _pcz = new float[Panels];
    private readonly float[] _vrx = new float[Panels], _vry = new float[Panels], _vrz = new float[Panels];
    private readonly float[] _vux = new float[Panels], _vuy = new float[Panels], _vuz = new float[Panels];
    private readonly float[] _vnx = new float[Panels], _vny = new float[Panels], _vnz = new float[Panels];
    private readonly float[] _mx = new float[Panels], _my = new float[Panels], _mz = new float[Panels];
    private readonly float[] _l = new float[Panels], _r = new float[Panels], _b = new float[Panels], _t = new float[Panels];
    private readonly float[] _shx = new float[Panels], _shy = new float[Panels], _dist = new float[Panels], _cos = new float[Panels];
    private readonly float[] _q = new float[Panels], _coc = new float[Panels], _risk = new float[Panels], _blur = new float[Panels], _gain = new float[Panels];
    private readonly float[] _seam = new float[Cols - 1];
    private readonly byte[] _inner = new byte[Panels];
    private readonly Matrix4x4[] _proj = new Matrix4x4[Panels];
    private TrackedCameraPose _pose;

    public RealtimeOffAxisFrustumProjectionEngine()
    {
        for (var row = 0; row < Rows; row++)
        for (var c = 0; c < Cols; c++)
        {
            var i = row * Cols + c;
            float t0 = -HalfArcRad + c * ColStepRad, t1 = t0 + ColStepRad, y0 = row * PanelHeightM, y1 = y0 + PanelHeightM;
            var pa = WallPoint(t0, y0); var pb = WallPoint(t1, y0); var pc = WallPoint(t0, y1);
            var vr = Vector3.Normalize(pb - pa); var vu = Vector3.Normalize(pc - pa); var vn = Vector3.Normalize(Vector3.Cross(vr, vu));
            var m = 0.5f * (pb + pc);
            _pax[i] = pa.X; _pay[i] = pa.Y; _paz[i] = pa.Z; _pbx[i] = pb.X; _pby[i] = pb.Y; _pbz[i] = pb.Z; _pcx[i] = pc.X; _pcy[i] = pc.Y; _pcz[i] = pc.Z;
            _vrx[i] = vr.X; _vry[i] = vr.Y; _vrz[i] = vr.Z; _vux[i] = vu.X; _vuy[i] = vu.Y; _vuz[i] = vu.Z; _vnx[i] = vn.X; _vny[i] = vn.Y; _vnz[i] = vn.Z;
            _mx[i] = m.X; _my[i] = m.Y; _mz[i] = m.Z;
        }
    }

    public bool UseSimd { get; set; } = true;
    public ref readonly TrackedCameraPose Pose => ref _pose;
    /// <summary>인너 프러스텀 벽면 영역(오버스캔 + 페더 포함) — θ(rad, 중앙 0) · 높이(m).</summary>
    public float InnerTheta0 { get; private set; }
    public float InnerTheta1 { get; private set; }
    public float InnerY0 { get; private set; }
    public float InnerY1 { get; private set; }
    public bool InnerValid { get; private set; }
    public int InnerPanels { get; private set; }
    public float MaxRisk { get; private set; }
    public float MaxBlur { get; private set; }
    public float SeamMax { get; private set; }
    public float MaxShear { get; private set; }
    public float MinGain { get; private set; }
    public float MaxGainUsed { get; private set; }
    public double LastComputeUs { get; private set; }
    public long Frames { get; private set; }

    public float Left(int i) => _l[i];
    public float RightE(int i) => _r[i];
    public float Bottom(int i) => _b[i];
    public float Top(int i) => _t[i];
    public float ShearX(int i) => _shx[i];
    public float ShearY(int i) => _shy[i];
    public float Distance(int i) => _dist[i];
    public float Cosine(int i) => _cos[i];
    public float PitchPx(int i) => _q[i];
    public float CocPx(int i) => _coc[i];
    public float Risk(int i) => _risk[i];
    public float Blur(int i) => _blur[i];
    public float Gain(int i) => _gain[i];
    public float Seam(int c) => _seam[c];
    public bool IsInner(int i) => _inner[i] != 0;
    public ref readonly Matrix4x4 Projection(int i) => ref _proj[i];
    public ReadOnlySpan<float> BlurMap => _blur;
    public ReadOnlySpan<float> ShearXs => _shx;
    public ReadOnlySpan<float> ShearYs => _shy;
    public ReadOnlySpan<float> Lefts => _l;
    public ReadOnlySpan<float> Tops => _t;
    public Vector3 CornerA(int i) => new(_pax[i], _pay[i], _paz[i]);
    public Vector3 CornerB(int i) => new(_pbx[i], _pby[i], _pbz[i]);
    public Vector3 CornerC(int i) => new(_pcx[i], _pcy[i], _pcz[i]);
    public Vector3 Center(int i) => new(_mx[i], _my[i], _mz[i]);

    /// <summary>원통 벽면 좌표 θ(중앙 0 = −Z) · 높이 → 월드.</summary>
    public static Vector3 WallPoint(float theta, float y) => new(RadiusM * MathF.Sin(theta), y, -RadiusM * MathF.Cos(theta));

    /// <summary>LED 피치 투영 간격 q(카메라 px) 기준 모아레 위험 — q≈1 에서 최대, CoC 가 격자 주기를 덮으면 감쇠.</summary>
    public static float MoireRisk(float q, float cocPx)
    {
        if (!(q > 1e-6f)) return 0f;
        var lq = MathF.Log(q);
        var att = cocPx / q;
        return MathF.Exp(-lq * lq / (2f * MoireSigma * MoireSigma)) * MathF.Exp(-att * att);
    }

    // ══════════════════════ 프레임(무할당) ══════════════════════
    public void Compute(in TrackedCameraPose pose, float overscan, float feather)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _pose = pose;
        var pe = pose.Position;
        var i = UseSimd && Vector.IsHardwareAccelerated ? KernelSimd(pe) : 0;
        for (; i < Panels; i++) KernelScalar(i, pe);

        var hf = Math.Clamp(pose.HFovDeg, 1f, 170f) * (MathF.PI / 360f);
        var tanH = MathF.Tan(hf);
        var tanV = MathF.Tan(Math.Clamp(pose.VFovDeg, 1f, 170f) * (MathF.PI / 360f));
        var fpx = SensorPxW / (2f * tanH);
        var f = Math.Clamp(pose.FocalMm, 8f, 400f);
        var pxMm = 2f * f * tanH / SensorPxW;
        var focus = Math.Clamp(pose.FocusM, 0.3f, 1000f);
        var cocK = f * f / (Math.Clamp(pose.TStop, 1f, 32f) * MathF.Max(focus * 1000f - f, 1f));
        float maxRisk = 0f, maxBlur = 0f, maxShear = 0f, minG = float.MaxValue, maxG = 0f;
        for (var p = 0; p < Panels; p++)
        {
            var cs = MathF.Max(_cos[p], 0.05f);
            var q = PitchMm * 1e-3f * cs / _dist[p] * fpx;
            var coc = cocK * MathF.Abs(_dist[p] - focus) / _dist[p] / pxMm;
            var risk = MoireRisk(q, coc);
            _q[p] = q; _coc[p] = coc; _risk[p] = risk; _blur[p] = MaxBlurPx * risk;
            var g = 1f / MathF.Max(MathF.Pow(cs, FalloffGamma), 1f / MaxGain);
            _gain[p] = g;
            if (risk > maxRisk) maxRisk = risk;
            if (_blur[p] > maxBlur) maxBlur = _blur[p];
            var sh = MathF.Max(MathF.Abs(_shx[p]), MathF.Abs(_shy[p]));
            if (sh > maxShear) maxShear = sh;
            if (g < minG) minG = g;
            if (g > maxG) maxG = g;
            var vr = new Vector3(_vrx[p], _vry[p], _vrz[p]); var vu = new Vector3(_vux[p], _vuy[p], _vuz[p]); var vn = new Vector3(_vnx[p], _vny[p], _vnz[p]);
            var rot = new Matrix4x4(vr.X, vu.X, vn.X, 0f, vr.Y, vu.Y, vn.Y, 0f, vr.Z, vu.Z, vn.Z, 0f, 0f, 0f, 0f, 1f);
            _proj[p] = Matrix4x4.CreateTranslation(-pe) * rot * Matrix4x4.CreatePerspectiveOffCenter(_l[p], _r[p], _b[p], _t[p], NearM, FarM);
        }
        var seamMax = 0f;
        for (var c = 0; c < Cols - 1; c++)
        {
            var s = 0f;
            for (var row = 0; row < Rows; row++)
            {
                int a = row * Cols + c, b = a + 1;
                var da = MathF.Max(_cos[a], 0.05f) / _dist[a]; var db = MathF.Max(_cos[b], 0.05f) / _dist[b];
                s += MathF.Abs(MathF.Log(da / db));
            }
            _seam[c] = s / Rows;
            if (_seam[c] > seamMax) seamMax = _seam[c];
        }
        MaxRisk = maxRisk; MaxBlur = maxBlur; MaxShear = maxShear; MinGain = minG; MaxGainUsed = maxG; SeamMax = seamMax;
        InnerRegion(in pose, tanH, tanV, 1f + MathF.Max(overscan, 0f) + MathF.Max(feather, 0f));
        Frames++;
        LastComputeUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private int KernelSimd(Vector3 pe)
    {
        var w = Vector<float>.Count;
        var ex = new Vector<float>(pe.X); var ey = new Vector<float>(pe.Y); var ez = new Vector<float>(pe.Z);
        var near = new Vector<float>(NearM); var eps = new Vector<float>(1e-4f);
        var i = 0;
        for (; i <= Panels - w; i += w)
        {
            var ax = new Vector<float>(_pax, i) - ex; var ay = new Vector<float>(_pay, i) - ey; var az = new Vector<float>(_paz, i) - ez;
            var bx = new Vector<float>(_pbx, i) - ex; var by = new Vector<float>(_pby, i) - ey; var bz = new Vector<float>(_pbz, i) - ez;
            var cx = new Vector<float>(_pcx, i) - ex; var cy = new Vector<float>(_pcy, i) - ey; var cz = new Vector<float>(_pcz, i) - ez;
            var rx = new Vector<float>(_vrx, i); var ry = new Vector<float>(_vry, i); var rz = new Vector<float>(_vrz, i);
            var ux = new Vector<float>(_vux, i); var uy = new Vector<float>(_vuy, i); var uz = new Vector<float>(_vuz, i);
            var nx = new Vector<float>(_vnx, i); var ny = new Vector<float>(_vny, i); var nz = new Vector<float>(_vnz, i);
            var d = Vector.Max(-(nx * ax + ny * ay + nz * az), eps);
            var k = near / d;
            var l = (rx * ax + ry * ay + rz * az) * k;
            var r = (rx * bx + ry * by + rz * bz) * k;
            var b = (ux * ax + uy * ay + uz * az) * k;
            var t = (ux * cx + uy * cy + uz * cz) * k;
            l.CopyTo(_l, i); r.CopyTo(_r, i); b.CopyTo(_b, i); t.CopyTo(_t, i);
            ((r + l) / (r - l)).CopyTo(_shx, i);
            ((t + b) / (t - b)).CopyTo(_shy, i);
            var vx = ex - new Vector<float>(_mx, i); var vy = ey - new Vector<float>(_my, i); var vz = ez - new Vector<float>(_mz, i);
            var dist = Vector.SquareRoot(vx * vx + vy * vy + vz * vz);
            dist.CopyTo(_dist, i);
            ((nx * vx + ny * vy + nz * vz) / dist).CopyTo(_cos, i);
        }
        return i;
    }

    private void KernelScalar(int i, Vector3 pe)
    {
        float ax = _pax[i] - pe.X, ay = _pay[i] - pe.Y, az = _paz[i] - pe.Z;
        float bx = _pbx[i] - pe.X, by = _pby[i] - pe.Y, bz = _pbz[i] - pe.Z;
        float cx = _pcx[i] - pe.X, cy = _pcy[i] - pe.Y, cz = _pcz[i] - pe.Z;
        float rx = _vrx[i], ry = _vry[i], rz = _vrz[i], ux = _vux[i], uy = _vuy[i], uz = _vuz[i], nx = _vnx[i], ny = _vny[i], nz = _vnz[i];
        var d = MathF.Max(-(nx * ax + ny * ay + nz * az), 1e-4f);
        var k = NearM / d;
        var l = (rx * ax + ry * ay + rz * az) * k;
        var r = (rx * bx + ry * by + rz * bz) * k;
        var b = (ux * ax + uy * ay + uz * az) * k;
        var t = (ux * cx + uy * cy + uz * cz) * k;
        _l[i] = l; _r[i] = r; _b[i] = b; _t[i] = t;
        _shx[i] = (r + l) / (r - l);
        _shy[i] = (t + b) / (t - b);
        float vx = pe.X - _mx[i], vy = pe.Y - _my[i], vz = pe.Z - _mz[i];
        var dist = MathF.Sqrt(vx * vx + vy * vy + vz * vz);
        _dist[i] = dist;
        _cos[i] = (nx * vx + ny * vy + nz * vz) / dist;
    }

    /// <summary>화각 경계(오버스캔·페더 확장) 광선 32 개 + 중심 → 원통 교차 → 벽면 θ/높이 경계 → 패널 분류.</summary>
    private void InnerRegion(in TrackedCameraPose pose, float tanH, float tanV, float ext)
    {
        float th0 = float.MaxValue, th1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
        var hits = 0;
        for (var k = 0; k <= BoundarySamples; k++)
        {
            float nx, ny;
            if (k == BoundarySamples) { nx = 0f; ny = 0f; }
            else
            {
                var s = 4f * k / BoundarySamples;
                var e = (int)s; var u = 2f * (s - e) - 1f;
                (nx, ny) = e switch { 0 => (u, -1f), 1 => (1f, u), 2 => (-u, 1f), _ => (-1f, -u) };
                nx *= ext; ny *= ext;
            }
            var dir = pose.Forward + pose.Right * (nx * tanH) + pose.Up * (ny * tanV);
            if (!TryHitWall(pose.Position, dir, out var th, out var y)) continue;
            hits++;
            if (th < th0) th0 = th;
            if (th > th1) th1 = th;
            if (y < y0) y0 = y;
            if (y > y1) y1 = y;
        }
        InnerValid = hits > 0;
        var n = 0;
        if (!InnerValid) { Array.Clear(_inner); InnerPanels = 0; return; }
        InnerTheta0 = th0; InnerTheta1 = th1; InnerY0 = y0; InnerY1 = y1;
        for (var row = 0; row < Rows; row++)
        for (var c = 0; c < Cols; c++)
        {
            float a0 = -HalfArcRad + c * ColStepRad, a1 = a0 + ColStepRad, b0 = row * PanelHeightM, b1 = b0 + PanelHeightM;
            var hit = a1 > th0 && a0 < th1 && b1 > y0 && b0 < y1;
            _inner[row * Cols + c] = hit ? (byte)1 : (byte)0;
            if (hit) n++;
        }
        InnerPanels = n;
    }

    /// <summary>볼륨 내부 원점 광선 → 원통(축 Y) 원방향 교차 · θ/높이를 월 범위로 클램프.</summary>
    public static bool TryHitWall(Vector3 o, Vector3 d, out float theta, out float y)
    {
        theta = 0f; y = 0f;
        var a = d.X * d.X + d.Z * d.Z;
        if (!(a > 1e-8f)) return false;
        var b = 2f * (o.X * d.X + o.Z * d.Z);
        var c = o.X * o.X + o.Z * o.Z - RadiusM * RadiusM;
        var disc = b * b - 4f * a * c;
        if (!(disc >= 0f)) return false;
        var t = (-b + MathF.Sqrt(disc)) / (2f * a);
        if (!(t > 0f)) return false;
        var p = o + d * t;
        theta = Math.Clamp(MathF.Atan2(p.X, -p.Z), -HalfArcRad, HalfArcRad);
        y = Math.Clamp(p.Y, 0f, WallHeightM);
        return true;
    }
}
