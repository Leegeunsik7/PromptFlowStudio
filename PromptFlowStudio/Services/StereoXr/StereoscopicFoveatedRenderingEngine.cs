using System.Numerics;
using PromptFlowStudio.Services.CognitiveCinematography;

namespace PromptFlowStudio.Services.StereoXr;

/// <summary>프리뷰 모드: 양안 분할(SBS) · 적청 애너글리프 · VRS 셰이딩 레이트 맵.</summary>
public enum StereoView : byte { SideBySide, Anaglyph, RateMap }

/// <summary>
/// 눈 하나의 비대칭(off-axis) 프러스텀 카메라. 수평 tan 범위 [TanL, TanR] 은 수렴 거리에서 시차 0 이 되도록 IPD/2 ÷ 수렴 거리만큼 이동한다.
/// </summary>
public readonly struct XrEyeCamera
{
    public readonly Vector3 Position, Forward, Right, Up;
    public readonly Quaternion Orientation;
    public readonly float TanL, TanR, TanV;

    public XrEyeCamera(Vector3 position, Quaternion q, float tanL, float tanR, float tanV)
    {
        Position = position; Orientation = q;
        Forward = Vector3.Transform(Vector3.UnitZ, q); Right = Vector3.Transform(Vector3.UnitX, q); Up = Vector3.Transform(Vector3.UnitY, q);
        TanL = tanL; TanR = tanR; TanV = tanV;
    }

    /// <summary>화면 좌표(0..1, 좌상단 원점) → 월드 광선 방향(정규화).</summary>
    public Vector3 Ray(float sx, float sy) => Vector3.Normalize(Forward + Right * (TanL + sx * (TanR - TanL)) + Up * ((1f - 2f * sy) * TanV));

    /// <summary>월드 방향(무한원점) → 화면 좌표. 카메라 뒤면 false.</summary>
    public bool ProjectDir(Vector3 d, out float sx, out float sy)
    {
        var z = Vector3.Dot(d, Forward);
        if (z <= 1e-6f) { sx = sy = -1f; return false; }
        sx = (Vector3.Dot(d, Right) / z - TanL) / (TanR - TanL);
        sy = 0.5f - Vector3.Dot(d, Up) / z / (2f * TanV);
        return true;
    }

    /// <summary>월드 점 → 화면 좌표 · 시선 깊이 z.</summary>
    public bool Project(Vector3 p, out float sx, out float sy, out float z)
    {
        var d = p - Position;
        z = Vector3.Dot(d, Forward);
        if (z <= 1e-4f) { sx = sy = -1f; return false; }
        sx = (Vector3.Dot(d, Right) / z - TanL) / (TanR - TanL);
        sy = 0.5f - Vector3.Dot(d, Up) / z / (2f * TanV);
        return true;
    }
}

/// <summary>
/// [PromptFlow Studio v17.22.0] 🥽 실시간 양안(Stereoscopic) · 포비티드(Foveated) 렌더링 엔진(무할당 · 결정적).
///   ▸ IPD(mm) 로 머리 중심에서 좌/우 눈을 ±IPD/2 분리하고, 수렴 거리에서 시차 0 이 되는 비대칭 프러스텀을 구성한다.
///   ▸ 단일 패스 인스턴싱: 8×8 타일 격자를 한 번 순회하며 타일마다 눈 인스턴스 0/1 을 연속 셰이딩(v17.21 블로킹 세트 해석적 교차 공유).
///   ▸ VRS 소프트웨어 근사: HMD 시선 추적 좌표와 타일 중심 광선의 이심률(°)로 1×1(중심와) · 2×2(근주변) · 4×4(주변) 셰이딩 레이트를 정하고
///     블록당 1 샘플을 셰이딩해 블록 전체를 채운다(레이트 1 타일은 VRS OFF 와 비트 동일).
/// </summary>
public sealed class StereoscopicFoveatedRenderingEngine
{
    public const int EW = 128, EH = 72, EN = EW * EH, Tile = 8, TX = EW / Tile, TY = EH / Tile, Tiles = TX * TY, SbsW = EW * 2;
    public const float FarZ = 40f, MinIpd = 54f, MaxIpd = 74f;

    private readonly uint[] _px = new uint[2 * EN];
    private readonly float[] _z = new float[2 * EN];
    private readonly byte[] _rate = new byte[2 * Tiles];
    private XrEyeCamera _l, _r;

    public StereoscopicFoveatedRenderingEngine() => SetPose(new Vector3(0.6f, 1.6f, 4f), HeadRotation(MathF.PI, 0f, 0f));

    public float IpdMm { get; set; } = 63f;
    public float FovHDeg { get; set; } = 96f;
    public float ConvergenceM { get; set; } = 4f;
    public bool VrsEnabled { get; set; } = true;
    public float FoveaDeg { get; set; } = 12f;
    public float MidDeg { get; set; } = 26f;
    public Vector2 GazeLeft { get; private set; } = new(0.5f, 0.5f);
    public Vector2 GazeRight { get; private set; } = new(0.5f, 0.5f);
    public Vector3 HeadPosition { get; private set; }
    public Quaternion HeadOrientation { get; private set; } = Quaternion.Identity;
    public XrEyeCamera Left => _l;
    public XrEyeCamera Right => _r;
    public XrEyeCamera Eye(int e) => e == 0 ? _l : _r;
    public ReadOnlySpan<uint> Pixels => _px;
    public ReadOnlySpan<float> Depth => _z;
    public ReadOnlySpan<byte> Rates => _rate;
    public byte TileRate(int eye, int tile) => _rate[eye * Tiles + tile];
    public long ShadedSamples { get; private set; }
    public float ShadingRatio { get; private set; } = 1f;
    public int FoveaTiles { get; private set; }
    public double LastRenderUs { get; private set; }
    public long Frames { get; private set; }
    public float PixelsPerDegree => EW / FovHDeg;

    /// <summary>요(+Y 축) · 위로 피치 · 롤(라디안) → 머리 회전(정면 = +Z).</summary>
    public static Quaternion HeadRotation(float yaw, float pitchUp, float roll) => Quaternion.CreateFromYawPitchRoll(yaw, -pitchUp, roll);

    /// <summary>머리 포즈 → 좌/우 눈 카메라(IPD · 화각 · 수렴 거리 반영).</summary>
    public void SetPose(Vector3 headPos, Quaternion headQ)
    {
        HeadPosition = headPos; HeadOrientation = headQ;
        ComputeEyes(headPos, headQ, IpdMm, FovHDeg, ConvergenceM, out _l, out _r);
    }

    public static void ComputeEyes(Vector3 headPos, Quaternion headQ, float ipdMm, float fovHDeg, float convergenceM, out XrEyeCamera left, out XrEyeCamera right)
    {
        var half = Math.Clamp(ipdMm, MinIpd, MaxIpd) * 0.0005f;
        var tanH = MathF.Tan(fovHDeg * (MathF.PI / 360f));
        var tanV = tanH * EH / EW;
        var shift = half / MathF.Max(0.25f, convergenceM);
        var rightAxis = Vector3.Transform(Vector3.UnitX, headQ);
        left = new XrEyeCamera(headPos - rightAxis * half, headQ, shift - tanH, shift + tanH, tanV);
        right = new XrEyeCamera(headPos + rightAxis * half, headQ, -shift - tanH, -shift + tanH, tanV);
    }

    /// <summary>눈별 시선 추적 좌표(0..1).</summary>
    public void SetGaze(Vector2 left, Vector2 right)
    {
        GazeLeft = Vector2.Clamp(left, Vector2.Zero, Vector2.One);
        GazeRight = Vector2.Clamp(right, Vector2.Zero, Vector2.One);
    }

    /// <summary>이심률(°) → 셰이딩 레이트(1 · 2 · 4).</summary>
    public static byte RateFor(float eccDeg, float foveaDeg, float midDeg) => eccDeg <= foveaDeg ? (byte)1 : eccDeg <= midDeg ? (byte)2 : (byte)4;

    // ── 단일 패스 인스턴스 렌더 ─────────────────────────────

    /// <summary>양안 1 프레임(무할당): 타일 순회 × 눈 인스턴스 0/1.</summary>
    public void Render(CinematicBlockingSet set)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        long shaded = 0;
        var fovea = 0;
        var gl = _l.Ray(GazeLeft.X, GazeLeft.Y);
        var gr = _r.Ray(GazeRight.X, GazeRight.Y);
        for (var ty = 0; ty < TY; ty++)
        for (var tx = 0; tx < TX; tx++)
        {
            var tile = ty * TX + tx;
            for (var e = 0; e < 2; e++)
            {
                var cam = e == 0 ? _l : _r;
                byte rate = 1;
                if (VrsEnabled)
                {
                    var c = cam.Ray((tx + 0.5f) * Tile / EW, (ty + 0.5f) * Tile / EH);
                    var ecc = MathF.Acos(Math.Clamp(Vector3.Dot(c, e == 0 ? gl : gr), -1f, 1f)) * (180f / MathF.PI);
                    rate = RateFor(ecc, FoveaDeg, MidDeg);
                }
                _rate[e * Tiles + tile] = rate;
                if (rate == 1) fovea++;
                var o = e * EN;
                for (var by = ty * Tile; by < ty * Tile + Tile; by += rate)
                for (var bx = tx * Tile; bx < tx * Tile + Tile; bx += rate)
                {
                    var rd = cam.Ray((bx + rate * 0.5f) / EW, (by + rate * 0.5f) / EH);
                    var t = set.Trace(cam.Position, rd, out var hit, out var gt, out var ghost);
                    Vector3 col;
                    float z;
                    if (hit == -1) { col = new Vector3(0.015f, 0.017f, 0.025f); z = FarZ; }
                    else { col = Shade(set, hit, cam.Position + rd * t, rd); z = MathF.Min(FarZ, t * Vector3.Dot(rd, cam.Forward)); }
                    if (ghost >= 0)
                    {
                        var a = set.Alpha(ghost);
                        col = Shade(set, ghost, cam.Position + rd * gt, rd) * a + col * (1f - a);
                    }
                    var p = Pack(col);
                    shaded++;
                    for (var y = by; y < by + rate; y++)
                    {
                        var row = o + y * EW;
                        for (var x = bx; x < bx + rate; x++) { _px[row + x] = p; _z[row + x] = z; }
                    }
                }
            }
        }
        ShadedSamples = shaded;
        ShadingRatio = (float)shaded / (2 * EN);
        FoveaTiles = fovea;
        LastRenderUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        Frames++;
    }

    private static Vector3 Shade(CinematicBlockingSet set, int hit, Vector3 p, Vector3 rd)
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

    private static byte Tone(float v)
    {
        v *= 1.1f;
        v = v / (1f + v) * 1.15f;
        return (byte)Math.Clamp((int)(MathF.Sqrt(MathF.Max(0f, v)) * 255f + 0.5f), 0, 255);
    }

    /// <summary>선형 RGB → 톤 매핑 0xAARRGGBB.</summary>
    public static uint Pack(Vector3 c) => 0xFF000000u | (uint)Tone(c.X) << 16 | (uint)Tone(c.Y) << 8 | Tone(c.Z);

    // ── 프리뷰 BMP ─────────────────────────────────────────

    /// <summary>프리뷰 BMP 최대 크기(SBS 256×72 · 24 비트).</summary>
    public const int BmpMax = 54 + SbsW * 3 * EH;

    /// <summary>양안 픽셀(좌 EN · 우 EN) → 24 비트 BMP. 반환 = 바이트 수.</summary>
    public static int WriteBmp(ReadOnlySpan<uint> px, ReadOnlySpan<byte> rates, StereoView view, Span<byte> bmp)
    {
        var w = view == StereoView.Anaglyph ? EW : SbsW;
        var row = w * 3;
        var size = 54 + row * EH;
        bmp[..54].Clear();
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BitConverter.TryWriteBytes(bmp[2..], size);
        BitConverter.TryWriteBytes(bmp[10..], 54);
        BitConverter.TryWriteBytes(bmp[14..], 40);
        BitConverter.TryWriteBytes(bmp[18..], w);
        BitConverter.TryWriteBytes(bmp[22..], EH);
        BitConverter.TryWriteBytes(bmp[26..], (short)1);
        BitConverter.TryWriteBytes(bmp[28..], (short)24);
        BitConverter.TryWriteBytes(bmp[34..], row * EH);
        for (var y = 0; y < EH; y++)
        {
            var o = 54 + (EH - 1 - y) * row;
            for (var x = 0; x < w; x++)
            {
                uint r, g, b;
                if (view == StereoView.Anaglyph)
                {
                    // 하프 컬러 애너글리프: R = 좌안 휘도 · G/B = 우안
                    var pl = px[y * EW + x]; var pr = px[EN + y * EW + x];
                    r = ((pl >> 16 & 0xFF) * 77 + (pl >> 8 & 0xFF) * 150 + (pl & 0xFF) * 29) >> 8;
                    g = pr >> 8 & 0xFF; b = pr & 0xFF;
                }
                else
                {
                    var e = x >= EW ? 1 : 0; var ex = x - e * EW;
                    var p = px[e * EN + y * EW + ex];
                    r = p >> 16 & 0xFF; g = p >> 8 & 0xFF; b = p & 0xFF;
                    if (view == StereoView.RateMap)
                    {
                        var rt = rates[e * Tiles + (y / Tile) * TX + ex / Tile];
                        // 1×1 = 녹 · 2×2 = 황 · 4×4 = 적 틴트(50%)
                        uint tr = rt == 1 ? 40u : 240u, tg = rt == 4 ? 60u : 220u, tb = 40u;
                        r = (r + tr) >> 1; g = (g + tg) >> 1; b = (b + tb) >> 1;
                        if (ex % Tile == 0 || y % Tile == 0) { r >>= 1; g >>= 1; b >>= 1; }
                    }
                }
                bmp[o + x * 3] = (byte)b; bmp[o + x * 3 + 1] = (byte)g; bmp[o + x * 3 + 2] = (byte)r;
            }
        }
        return size;
    }
}
