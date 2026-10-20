using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PromptFlowStudio.Services.CognitiveCinematography;

/// <summary>보케 프리셋: 6 엽 육각 · 6 엽 30° 회전 · 아나모픽 2× 세로 타원 육각.</summary>
public enum BokehPreset : byte { Hexagon, HexRotated, Anamorphic }

/// <summary>뷰포트 표시: 최종 합성 · 산란광만 · 깊이 · CoC 히트맵.</summary>
public enum CineView : byte { Final, Scatter, Depth, Coc }

/// <summary>
/// [PromptFlow Studio v17.21.0] 🌫️ 광선 추적 볼류메트릭 산란 &amp; 시네마틱 피사계 심도 합성기(무할당 · 결정적).
///   ▸ 1차 광선: 256×144 해석적 교차(구 · 캡슐 · 박스 · 바닥 · 뒷벽) · 창 쿠키 × 섀도 맵 햇빛 · 뒷벽 스트링 전구(HDR 발광 = 보케 하이라이트) · 투명화 장애물 알파 합성.
///   ▸ 레이 마칭: ½ 해상도 128×72 · 24 스텝 · 4×4 Bayer 지터(5 탭 박스 해소) · 32×16×32 주기 노이즈 밀도(바람 이류 · 높이 감쇠) · HG 위상(g = 0.55) ·
///     Beer–Lambert 투과 → 쌍선형 업샘플 합성 = 블라인드 사이 갓레이.
///   ▸ CoC: 얇은 렌즈 r = f²/(N(s − f))·|z − s|/z → px(1024 px 기준 · 최대 8) — Vector256 커널(스칼라와 비트 동일).
///   ▸ 보케: 육각 = 마름모 3 개 합(DICE 분리형) — 1 패스 수직/좌하 · 2 패스 좌하/우하 방향 1D 게더 4 회 · 조리개 블레이드 회전/아나모픽 압축 · 샘플 CoC 누설 가드.
/// </summary>
public sealed class VolumetricRayMarchingCinematicDoFSynthesizer
{
    public const int W = 256, H = 144, HW = W / 2, HH = H / 2, N = W * H, HN = HW * HH, Steps = 24, MaxTaps = 9, GX = 32, GY = 16, GZ = 32;
    public const float MaxCoc = 8f, PreviewGain = 4f, FarZ = 40f, GridCell = 0.35f;

    private static readonly float[] Bayer = { 0f, 8f, 2f, 10f, 12f, 4f, 14f, 6f, 3f, 11f, 1f, 9f, 15f, 7f, 13f, 5f };

    private readonly float[] _r = new float[N], _g = new float[N], _b = new float[N], _z = new float[N], _coc = new float[N];
    private readonly float[] _vr = new float[N], _vg = new float[N], _vb = new float[N], _sr = new float[N], _sg = new float[N], _sb = new float[N];
    private readonly float[] _or = new float[N], _og = new float[N], _ob = new float[N];
    private readonly float[] _scat = new float[HN], _trans = new float[HN], _tmp = new float[HN];
    private readonly float[] _grid = new float[GX * GY * GZ];
    private readonly int[] _dx = new int[3 * MaxTaps], _dy = new int[3 * MaxTaps];
    private BokehPreset _preset = (BokehPreset)255;

    public VolumetricRayMarchingCinematicDoFSynthesizer()
    {
        var rng = 0x9E3779B9u;
        for (var i = 0; i < _grid.Length; i++)
        {
            rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
            _grid[i] = (rng & 0xFFFF) / 65535f;
        }
        // 3 회 박스 평활 → 저주파 덩어리(먼지 구름)
        Span<float> tmp = stackalloc float[GX];
        for (var pass = 0; pass < 2; pass++)
        for (var z = 0; z < GZ; z++)
        for (var y = 0; y < GY; y++)
        {
            var row = z * GX * GY + y * GX;
            for (var x = 0; x < GX; x++) tmp[x] = (_grid[row + (x + GX - 1) % GX] + _grid[row + x] + _grid[row + (x + 1) % GX]) / 3f;
            for (var x = 0; x < GX; x++) _grid[row + x] = tmp[x];
        }
        SetPreset(BokehPreset.Hexagon);
    }

    public CineSimdPath Path { get; set; } = CineSimdPath.V256;
    public CineView View { get; set; } = CineView.Final;
    public bool Volumetric { get; set; } = true;
    public bool DepthOfFieldOn { get; set; } = true;
    public float FogDensity { get; set; } = 0.1f;
    public float Anisotropy { get; set; } = 0.55f;
    public float SunIntensity { get; set; } = 9f;
    public float Exposure { get; set; } = 1.1f;
    public BokehPreset Preset => _preset;
    public float BladeRotationDeg { get; private set; }
    public float Squeeze { get; private set; } = 1f;
    public float Time { get; private set; }
    public double LastPrimaryUs { get; private set; }
    public double LastMarchUs { get; private set; }
    public double LastCocUs { get; private set; }
    public double LastBokehUs { get; private set; }
    public double LastTotalUs { get; private set; }
    public float MeanScatter { get; private set; }
    public float MeanTransmit { get; private set; }
    public float BlurredFraction { get; private set; }
    public float MaxCocPx { get; private set; }
    public long MarchSamples { get; private set; }
    public long BokehTaps { get; private set; }
    public long Frames { get; private set; }
    public ReadOnlySpan<float> Depth => _z;
    public ReadOnlySpan<float> CocBuffer => _coc;
    public ReadOnlySpan<float> R => _or;
    public ReadOnlySpan<float> G => _og;
    public ReadOnlySpan<float> B => _ob;
    public ReadOnlySpan<float> ScatterBuffer => _scat;

    public static string PresetName(BokehPreset p) => p switch { BokehPreset.HexRotated => "6 엽 30° 회전", BokehPreset.Anamorphic => "아나모픽 2×", _ => "6 엽 육각" };

    /// <summary>보케 프리셋 → 3 방향 정수 탭 오프셋 표(프리셋 변경 시만 재계산).</summary>
    public void SetPreset(BokehPreset p)
    {
        if (p == _preset) return;
        _preset = p;
        BladeRotationDeg = p == BokehPreset.HexRotated ? 30f : 0f;
        Squeeze = p == BokehPreset.Anamorphic ? 2f : 1f;
        var rot = BladeRotationDeg * (MathF.PI / 180f);
        // 화면 y 아래 방향: 수직(위) · 좌하(210°) · 우하(330°)
        ReadOnlySpan<float> ang = stackalloc float[3] { -MathF.PI / 2f, MathF.PI * 5f / 6f, MathF.PI / 6f };
        for (var d = 0; d < 3; d++)
        {
            float ux = MathF.Cos(ang[d] + rot), uy = MathF.Sin(ang[d] + rot) * Squeeze;
            for (var k = 0; k < MaxTaps; k++)
            {
                _dx[d * MaxTaps + k] = (int)MathF.Round(k * ux);
                _dy[d * MaxTaps + k] = (int)MathF.Round(k * uy);
            }
        }
    }

    public int TapX(int dir, int k) => _dx[dir * MaxTaps + k];
    public int TapY(int dir, int k) => _dy[dir * MaxTaps + k];

    /// <summary>얇은 렌즈 CoC 반경(px).</summary>
    public static float CocRadiusPx(float focalMm, float fStop, float focusM, float z) => MathF.Min(MathF.Abs(z - focusM) / z * CocScale(focalMm, fStop, focusM), MaxCoc);

    /// <summary>CoC 스케일 K: r = |z − s|/z·K (px) — 1024 px 출력 기준 착란원(프리뷰 256 px 에 ×4 강조).</summary>
    public static float CocScale(float focalMm, float fStop, float focusM)
    {
        var f = focalMm / 1000f;
        var cocM = f * f / (fStop * MathF.Max(1e-3f, focusM - f));
        return cocM / (CognitiveCameraFramingBlockingEngine.SensorW / 1000f) * W * 0.5f * PreviewGain;
    }

    /// <summary>CoC 버퍼 산출(스칼라).</summary>
    public void CocScalar(float focusM, float k)
    {
        for (var i = 0; i < N; i++) _coc[i] = MathF.Min(MathF.Abs(_z[i] - focusM) / _z[i] * k, MaxCoc);
    }

    /// <summary>CoC 버퍼 산출(Vector256 · 꼬리 스칼라 · 스칼라와 비트 동일).</summary>
    public void CocV256(float focusM, float k)
    {
        ref var z0 = ref MemoryMarshal.GetArrayDataReference(_z);
        ref var c0 = ref MemoryMarshal.GetArrayDataReference(_coc);
        var vs = Vector256.Create(focusM); var vk = Vector256.Create(k); var vm = Vector256.Create(MaxCoc);
        var i = 0;
        for (; i <= N - Vector256<float>.Count; i += Vector256<float>.Count)
        {
            var z = Vector256.LoadUnsafe(ref z0, (nuint)i);
            Vector256.Min(Vector256.Abs(z - vs) / z * vk, vm).StoreUnsafe(ref c0, (nuint)i);
        }
        for (; i < N; i++) _coc[i] = MathF.Min(MathF.Abs(_z[i] - focusM) / _z[i] * k, MaxCoc);
    }

    /// <summary>프레임 합성(무할당): 1차 광선 → 레이 마칭 → CoC → 육각 보케.</summary>
    public void Render(CinematicBlockingSet set, CognitiveCameraFramingBlockingEngine cam, float focusM, float fStop, float dt)
    {
        var freq = (double)System.Diagnostics.Stopwatch.Frequency;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Time += dt;
        Primary(set, cam);
        var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (Volumetric) { March(set, cam); Composite(set); }
        else { Array.Fill(_scat, 0f); Array.Fill(_trans, 1f); MeanScatter = 0f; MeanTransmit = 1f; }
        var t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        var k = CocScale(cam.FocalMm, fStop, focusM);
        if (Path == CineSimdPath.V256 && Vector256.IsHardwareAccelerated) CocV256(focusM, k); else CocScalar(focusM, k);
        var t3 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (DepthOfFieldOn) Bokeh();
        else { _r.AsSpan().CopyTo(_or); _g.AsSpan().CopyTo(_og); _b.AsSpan().CopyTo(_ob); BlurredFraction = 0f; }
        var t4 = System.Diagnostics.Stopwatch.GetTimestamp();
        LastPrimaryUs = (t1 - t0) * 1e6 / freq; LastMarchUs = (t2 - t1) * 1e6 / freq; LastCocUs = (t3 - t2) * 1e6 / freq; LastBokehUs = (t4 - t3) * 1e6 / freq; LastTotalUs = (t4 - t0) * 1e6 / freq;
        Frames++;
    }

    private Vector3 Shade(CinematicBlockingSet set, int hit, Vector3 p, Vector3 rd)
    {
        Vector3 n, alb;
        if (hit == -2)
        {
            n = Vector3.UnitY;
            var chk = (((int)MathF.Floor(p.X) + (int)MathF.Floor(p.Z)) & 1) == 0;
            alb = chk ? new Vector3(0.34f, 0.32f, 0.3f) : new Vector3(0.24f, 0.23f, 0.22f);
        }
        else if (hit == -3)
        {
            n = Vector3.UnitZ;
            alb = new Vector3(0.2f, 0.2f, 0.24f);
            // 스트링 전구(HDR 발광) — 보케 하이라이트
            var bx = p.X * 1.4f; var fx = bx - MathF.Floor(bx) - 0.5f;
            var by = p.Y - (2.9f + 0.25f * MathF.Sin(MathF.Floor(bx) * 1.7f));
            if (fx * fx + by * by * 2f < 0.0025f) return new Vector3(9f, 6.2f, 3f);
        }
        else { n = set.Normal(hit, p); alb = set.Color(hit); }
        var ndl = MathF.Max(0f, -Vector3.Dot(n, set.Light));
        var vis = ndl > 0f ? set.LightVisibility(p + n * 0.02f) : 0f;
        var spec = MathF.Pow(MathF.Max(0f, Vector3.Dot(Vector3.Reflect(set.Light, n), -rd)), 24f) * vis * 0.5f;
        return alb * (set.SunColor * (ndl * vis * 2.2f) + set.Ambient * (2.4f + 1.2f * n.Y)) + set.SunColor * spec;
    }

    private void Primary(CinematicBlockingSet set, CognitiveCameraFramingBlockingEngine cam)
    {
        var ro = cam.Position;
        var f = cam.Forward;
        for (var y = 0; y < H; y++)
        {
            var sy = (y + 0.5f) / H;
            for (var x = 0; x < W; x++)
            {
                var rd = cam.Ray((x + 0.5f) / W, sy);
                var t = set.Trace(ro, rd, out var hit, out var gt, out var ghost);
                Vector3 col;
                float z;
                if (hit == -1) { col = new Vector3(0.015f, 0.017f, 0.025f); z = FarZ; }
                else { col = Shade(set, hit, ro + rd * t, rd); z = MathF.Min(FarZ, t * Vector3.Dot(rd, f)); }
                if (ghost >= 0)
                {
                    var a = set.Alpha(ghost);
                    col = Shade(set, ghost, ro + rd * gt, rd) * a + col * (1f - a);
                }
                var i = y * W + x;
                _r[i] = col.X; _g[i] = col.Y; _b[i] = col.Z; _z[i] = MathF.Max(0.05f, z);
            }
        }
    }

    /// <summary>주기 노이즈 밀도(삼선형 · 바람 이류 · 높이 감쇠).</summary>
    public float Density(Vector3 p)
    {
        if (p.Y < 0f || p.Y > 5f) return 0f;
        float gx = p.X / GridCell + Time * 0.6f, gy = p.Y / GridCell + Time * 0.15f, gz = p.Z / GridCell + Time * 0.25f;
        float fx = MathF.Floor(gx), fy = MathF.Floor(gy), fz = MathF.Floor(gz);
        float ax = gx - fx, ay = gy - fy, az = gz - fz;
        int x0 = ((int)fx % GX + GX) % GX, y0 = ((int)fy % GY + GY) % GY, z0 = ((int)fz % GZ + GZ) % GZ;
        int x1 = (x0 + 1) % GX, y1 = (y0 + 1) % GY, z1 = (z0 + 1) % GZ;
        var g = _grid;
        float c00 = g[(z0 * GY + y0) * GX + x0] + (g[(z0 * GY + y0) * GX + x1] - g[(z0 * GY + y0) * GX + x0]) * ax;
        float c10 = g[(z0 * GY + y1) * GX + x0] + (g[(z0 * GY + y1) * GX + x1] - g[(z0 * GY + y1) * GX + x0]) * ax;
        float c01 = g[(z1 * GY + y0) * GX + x0] + (g[(z1 * GY + y0) * GX + x1] - g[(z1 * GY + y0) * GX + x0]) * ax;
        float c11 = g[(z1 * GY + y1) * GX + x0] + (g[(z1 * GY + y1) * GX + x1] - g[(z1 * GY + y1) * GX + x0]) * ax;
        var c0 = c00 + (c10 - c00) * ay; var c1 = c01 + (c11 - c01) * ay;
        var n = c0 + (c1 - c0) * az;
        return MathF.Exp(-p.Y * 0.35f) * (0.15f + 1.6f * n * n);
    }

    /// <summary>헤니–그린스타인 위상.</summary>
    public static float Phase(float cosTheta, float g)
    {
        var d = 1f + g * g - 2f * g * cosTheta;
        return (1f - g * g) / (4f * MathF.PI * d * MathF.Sqrt(d));
    }

    private void March(CinematicBlockingSet set, CognitiveCameraFramingBlockingEngine cam)
    {
        var ro = cam.Position;
        var f = cam.Forward;
        var sigma = FogDensity;
        double sumS = 0, sumT = 0;
        long samples = 0;
        for (var hy = 0; hy < HH; hy++)
        for (var hx = 0; hx < HW; hx++)
        {
            int px = hx * 2 + 1, py = hy * 2 + 1;
            var rd = cam.Ray((px + 0.5f) / W, (py + 0.5f) / H);
            var maxT = MathF.Min(_z[py * W + px] / MathF.Max(0.05f, Vector3.Dot(rd, f)), FarZ);
            var step = maxT / Steps;
            var t = step * (Bayer[(hx & 3) + (hy & 3) * 4] + 0.5f) / 16f;
            var ph = Phase(-Vector3.Dot(set.Light, rd), Anisotropy) * 4f * MathF.PI;
            float s = 0f, tr = 1f;
            for (var k = 0; k < Steps; k++, t += step)
            {
                var p = ro + rd * t;
                var d = Density(p);
                if (d <= 0f) continue;
                samples++;
                var ext = d * sigma * step;
                var vis = set.LightVisibility(p);
                s += tr * ext * (vis * ph * SunIntensity + 0.02f);
                tr *= MathF.Exp(-ext);
                if (tr < 0.01f) break;
            }
            var j = hy * HW + hx;
            _scat[j] = s; _trans[j] = tr;
            sumS += s; sumT += tr;
        }
        // Bayer 지터 해소: ½ 해상도 5 탭 분리형 박스(지터 주기 4 를 덮음)
        Box5(_scat); Box5(_trans);
        MarchSamples = samples;
        MeanScatter = (float)(sumS / HN); MeanTransmit = (float)(sumT / HN);
    }

    private void Box5(float[] a)
    {
        for (var y = 0; y < HH; y++)
        {
            var row = y * HW;
            for (var x = 0; x < HW; x++)
            {
                float sum = 0f;
                for (var k = -2; k <= 2; k++) sum += a[row + Math.Clamp(x + k, 0, HW - 1)];
                _tmp[row + x] = sum * 0.2f;
            }
        }
        for (var y = 0; y < HH; y++)
        for (var x = 0; x < HW; x++)
        {
            float sum = 0f;
            for (var k = -2; k <= 2; k++) sum += _tmp[Math.Clamp(y + k, 0, HH - 1) * HW + x];
            a[y * HW + x] = sum * 0.2f;
        }
    }

    private void Composite(CinematicBlockingSet set)
    {
        var sun = set.SunColor;
        for (var y = 0; y < H; y++)
        {
            var fy = Math.Clamp((y - 0.5f) * 0.5f, 0f, HH - 1.001f);
            int y0 = (int)fy; var ay = fy - y0;
            for (var x = 0; x < W; x++)
            {
                var fx = Math.Clamp((x - 0.5f) * 0.5f, 0f, HW - 1.001f);
                int x0 = (int)fx; var ax = fx - x0;
                int a = y0 * HW + x0, b = a + 1, c = a + HW, d = c + 1;
                var s = (_scat[a] * (1 - ax) + _scat[b] * ax) * (1 - ay) + (_scat[c] * (1 - ax) + _scat[d] * ax) * ay;
                var tr = (_trans[a] * (1 - ax) + _trans[b] * ax) * (1 - ay) + (_trans[c] * (1 - ax) + _trans[d] * ax) * ay;
                var i = y * W + x;
                _r[i] = _r[i] * tr + sun.X * s; _g[i] = _g[i] * tr + sun.Y * s; _b[i] = _b[i] * tr + sun.Z * s;
            }
        }
    }

    /// <summary>방향 dir 의 가변 반경 1D 게더(누설 가드) — src → dst.</summary>
    private long Gather(int dir, float[] sr, float[] sg, float[] sb, float[] dr, float[] dg, float[] db)
    {
        long taps = 0;
        var o = dir * MaxTaps;
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var i = y * W + x;
            var r = _coc[i];
            if (r < 0.5f) { dr[i] = sr[i]; dg[i] = sg[i]; db[i] = sb[i]; continue; }
            float ar = sr[i], ag = sg[i], ab = sb[i], wsum = 1f;
            var n = Math.Min(MaxTaps - 1, (int)r + 1);
            for (var k = 1; k <= n; k++)
            {
                int qx = Math.Clamp(x + _dx[o + k], 0, W - 1), qy = Math.Clamp(y + _dy[o + k], 0, H - 1);
                var q = qy * W + qx;
                var w = Math.Clamp(r - k + 1f, 0f, 1f) * Math.Clamp(_coc[q] - k + 1.5f, 0f, 1f);
                ar += sr[q] * w; ag += sg[q] * w; ab += sb[q] * w; wsum += w;
            }
            taps += n;
            var inv = 1f / wsum;
            dr[i] = ar * inv; dg[i] = ag * inv; db[i] = ab * inv;
        }
        return taps;
    }

    private void Bokeh()
    {
        long taps = 0;
        // 1 패스: V = 수직 · D = 좌하 → S = V + D
        taps += Gather(0, _r, _g, _b, _vr, _vg, _vb);
        taps += Gather(1, _r, _g, _b, _sr, _sg, _sb);
        for (var i = 0; i < N; i++) { _sr[i] += _vr[i]; _sg[i] += _vg[i]; _sb[i] += _vb[i]; }
        // 2 패스: 마름모(V·좌하) + 마름모(V·우하) + 마름모(D·우하) → /3
        taps += Gather(1, _vr, _vg, _vb, _or, _og, _ob);
        taps += Gather(2, _sr, _sg, _sb, _vr, _vg, _vb);
        var blurred = 0; var maxc = 0f;
        for (var i = 0; i < N; i++)
        {
            _or[i] = (_or[i] + _vr[i]) * (1f / 3f); _og[i] = (_og[i] + _vg[i]) * (1f / 3f); _ob[i] = (_ob[i] + _vb[i]) * (1f / 3f);
            if (_coc[i] >= 0.5f) blurred++;
            if (_coc[i] > maxc) maxc = _coc[i];
        }
        BokehTaps = taps;
        BlurredFraction = blurred / (float)N;
        MaxCocPx = maxc;
    }

    private static byte Tone(float v, float exposure)
    {
        v *= exposure;
        v = v / (1f + v) * 1.15f;
        return (byte)Math.Clamp((int)(MathF.Sqrt(MathF.Max(0f, v)) * 255f + 0.5f), 0, 255);
    }

    /// <summary>뷰포트 24 비트 BMP(하향 행 순서 · BGR).</summary>
    public void WriteBmp(Span<byte> bmp)
    {
        const int row = W * 3, size = 54 + row * H;
        bmp[..54].Clear();
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BitConverter.TryWriteBytes(bmp[2..], size);
        BitConverter.TryWriteBytes(bmp[10..], 54);
        BitConverter.TryWriteBytes(bmp[14..], 40);
        BitConverter.TryWriteBytes(bmp[18..], W);
        BitConverter.TryWriteBytes(bmp[22..], H);
        BitConverter.TryWriteBytes(bmp[26..], (short)1);
        BitConverter.TryWriteBytes(bmp[28..], (short)24);
        BitConverter.TryWriteBytes(bmp[34..], row * H);
        for (var y = 0; y < H; y++)
        {
            var o = 54 + (H - 1 - y) * row;
            for (var x = 0; x < W; x++)
            {
                var i = y * W + x;
                byte r, g, b;
                switch (View)
                {
                    case CineView.Scatter:
                    {
                        var s = _scat[Math.Min(HH - 1, y >> 1) * HW + Math.Min(HW - 1, x >> 1)];
                        r = Tone(s * 1f, 2.5f); g = Tone(s * 0.86f, 2.5f); b = Tone(s * 0.64f, 2.5f);
                        break;
                    }
                    case CineView.Depth:
                    {
                        var d = (byte)Math.Clamp((int)(255f - _z[i] * 22f), 0, 255);
                        r = d; g = d; b = d;
                        break;
                    }
                    case CineView.Coc:
                    {
                        var c = _coc[i] / MaxCoc;
                        r = (byte)(255f * MathF.Min(1f, c * 2f)); g = (byte)(255f * MathF.Max(0f, 1f - MathF.Abs(c - 0.5f) * 2f)); b = (byte)(255f * MathF.Max(0f, 1f - c * 3f));
                        break;
                    }
                    default:
                        r = Tone(_or[i], Exposure); g = Tone(_og[i], Exposure); b = Tone(_ob[i], Exposure);
                        break;
                }
                bmp[o + x * 3] = b; bmp[o + x * 3 + 1] = g; bmp[o + x * 3 + 2] = r;
            }
        }
    }
}
