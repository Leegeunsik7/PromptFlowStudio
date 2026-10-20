using System.Numerics;

namespace PromptFlowStudio.Services.ArGraphics;

/// <summary>[PromptFlow Studio v17.06.0] FreeD D1 카메라 자세(각도 ° · 위치 m · 월드 Y-up: FreeD X → x · FreeD Y → z · FreeD Z(높이) → y).</summary>
public struct FreeDPose
{
    public byte CameraId;
    public float Pan, Tilt, Roll;
    public float X, Y, Z;
    public int Zoom, Focus, Spare;
}

/// <summary>
/// [PromptFlow Studio v17.06.0] FreeD 프로토콜(패킷 D1 · 29 바이트) — 0xD1 · 카메라 ID · 팬/틸트/롤(부호 24비트 · 1/32768°) ·
/// X/Y/Z(부호 24비트 · 1/64 mm) · 줌/포커스(무부호 24비트 엔코더) · 예비 2 · 체크섬(0x40 − Σ바이트 0..27).
/// </summary>
public static class FreeD
{
    public const int PacketBytes = 29;
    public const byte TypeD1 = 0xD1;
    public const float AngleScale = 1f / 32768f, PosScale = 1f / 64000f;

    private static int S24(ReadOnlySpan<byte> p, int o) => (sbyte)p[o] << 16 | p[o + 1] << 8 | p[o + 2];
    private static int U24(ReadOnlySpan<byte> p, int o) => p[o] << 16 | p[o + 1] << 8 | p[o + 2];
    private static void W24(Span<byte> p, int o, int v) { p[o] = (byte)(v >> 16); p[o + 1] = (byte)(v >> 8); p[o + 2] = (byte)v; }

    public static byte Checksum(ReadOnlySpan<byte> p)
    {
        var s = 0x40;
        for (var i = 0; i < PacketBytes - 1; i++) s -= p[i];
        return (byte)s;
    }

    public static bool Valid(ReadOnlySpan<byte> p) => p.Length >= PacketBytes && p[0] == TypeD1 && Checksum(p) == p[PacketBytes - 1];

    public static void Encode(in FreeDPose c, Span<byte> p)
    {
        p[0] = TypeD1; p[1] = c.CameraId;
        W24(p, 2, Math.Clamp((int)MathF.Round(c.Pan * 32768f), -8388608, 8388607));
        W24(p, 5, Math.Clamp((int)MathF.Round(c.Tilt * 32768f), -8388608, 8388607));
        W24(p, 8, Math.Clamp((int)MathF.Round(c.Roll * 32768f), -8388608, 8388607));
        W24(p, 11, Math.Clamp((int)MathF.Round(c.X * 64000f), -8388608, 8388607));
        W24(p, 14, Math.Clamp((int)MathF.Round(c.Y * 64000f), -8388608, 8388607));
        W24(p, 17, Math.Clamp((int)MathF.Round(c.Z * 64000f), -8388608, 8388607));
        W24(p, 20, Math.Clamp(c.Zoom, 0, 0xFFFFFF));
        W24(p, 23, Math.Clamp(c.Focus, 0, 0xFFFFFF));
        p[26] = (byte)(c.Spare >> 8); p[27] = (byte)c.Spare;
        p[28] = Checksum(p);
    }

    /// <summary>단일 패킷 역직렬화(무할당) — 형식/체크섬 오류 시 false.</summary>
    public static bool Decode(ReadOnlySpan<byte> p, out FreeDPose c)
    {
        c = default;
        if (!Valid(p)) return false;
        c.CameraId = p[1];
        c.Pan = S24(p, 2) * AngleScale; c.Tilt = S24(p, 5) * AngleScale; c.Roll = S24(p, 8) * AngleScale;
        c.X = S24(p, 11) * PosScale; c.Y = S24(p, 14) * PosScale; c.Z = S24(p, 17) * PosScale;
        c.Zoom = U24(p, 20); c.Focus = U24(p, 23); c.Spare = p[26] << 8 | p[27];
        return true;
    }

    /// <summary>
    /// 연속 패킷 스트림 일괄 역직렬화: 1단계 바이트 → 정수 SoA(9 필드 · 체크섬 검증), 2단계 SIMD 정수 → 실수 스케일(스칼라와 비트 동일).
    /// fields: 필드 f의 i번째 = fields[f * cap + i]. 반환 = 유효 패킷 수(무효 패킷은 건너뜀).
    /// </summary>
    public static int DecodeBatch(ReadOnlySpan<byte> stream, Span<int> raw, Span<float> fields, Span<int> zoomFocus, int cap, bool simd = true)
    {
        var n = 0;
        for (var o = 0; o + PacketBytes <= stream.Length && n < cap; o += PacketBytes)
        {
            var p = stream.Slice(o, PacketBytes);
            if (!Valid(p)) continue;
            for (var f = 0; f < 6; f++) raw[f * cap + n] = S24(p, 2 + f * 3);
            zoomFocus[n] = U24(p, 20); zoomFocus[cap + n] = U24(p, 23);
            n++;
        }
        for (var f = 0; f < 6; f++)
        {
            var s = f < 3 ? AngleScale : PosScale;
            var src = raw.Slice(f * cap, n); var dst = fields.Slice(f * cap, n);
            if (simd) FreeDSimd.Scale(src, dst, s); else FreeDSimd.ScaleScalar(src, dst, s);
        }
        return n;
    }
}

/// <summary>[PromptFlow Studio v17.06.0] FreeD/렌즈 SIMD 커널(스칼라 쌍둥이와 비트 동일 · FMA 축약 없음).</summary>
public static class FreeDSimd
{
    public static void Scale(ReadOnlySpan<int> src, Span<float> dst, float s)
    {
        int i = 0, n = src.Length;
        if (Vector.IsHardwareAccelerated && n >= Vector<int>.Count)
        {
            var vs = new Vector<float>(s);
            for (; i <= n - Vector<int>.Count; i += Vector<int>.Count)
                (Vector.ConvertToSingle(new Vector<int>(src.Slice(i))) * vs).CopyTo(dst.Slice(i));
        }
        for (; i < n; i++) dst[i] = (float)src[i] * s;
    }

    public static void ScaleScalar(ReadOnlySpan<int> src, Span<float> dst, float s)
    {
        for (var i = 0; i < src.Length; i++) dst[i] = (float)src[i] * s;
    }

    /// <summary>방사 왜곡 역변환(고정 반복): xu ← xd / (1 + K1·r² + K2·r⁴), r² = xu² + yu².</summary>
    public static void Undistort(ReadOnlySpan<float> xd, ReadOnlySpan<float> yd, Span<float> xu, Span<float> yu, float k1, float k2, int iterations)
    {
        int i = 0, n = xd.Length;
        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            Vector<float> one = Vector<float>.One, vk1 = new(k1), vk2 = new(k2);
            for (; i <= n - Vector<float>.Count; i += Vector<float>.Count)
            {
                var x0 = new Vector<float>(xd.Slice(i)); var y0 = new Vector<float>(yd.Slice(i));
                Vector<float> x = x0, y = y0;
                for (var it = 0; it < iterations; it++)
                {
                    var r2 = x * x + y * y;
                    var s = one + vk1 * r2 + vk2 * (r2 * r2);
                    x = x0 / s; y = y0 / s;
                }
                x.CopyTo(xu.Slice(i)); y.CopyTo(yu.Slice(i));
            }
        }
        for (; i < n; i++)
        {
            float x0 = xd[i], y0 = yd[i], x = x0, y = y0;
            for (var it = 0; it < iterations; it++) { var r2 = x * x + y * y; var s = 1f + k1 * r2 + k2 * (r2 * r2); x = x0 / s; y = y0 / s; }
            xu[i] = x; yu[i] = y;
        }
    }

    public static void UndistortScalar(ReadOnlySpan<float> xd, ReadOnlySpan<float> yd, Span<float> xu, Span<float> yu, float k1, float k2, int iterations)
    {
        for (var i = 0; i < xd.Length; i++)
        {
            float x0 = xd[i], y0 = yd[i], x = x0, y = y0;
            for (var it = 0; it < iterations; it++) { var r2 = x * x + y * y; var s = 1f + k1 * r2 + k2 * (r2 * r2); x = x0 / s; y = y0 / s; }
            xu[i] = x; yu[i] = y;
        }
    }
}

/// <summary>
/// [PromptFlow Studio v17.06.0] 렌즈 왜곡 핀홀 카메라 — 자세(팬/틸트/롤 · 위치) + 수평 FOV + 방사 왜곡(K1/K2).
/// 투영 = 핀홀 → 왜곡(1 + K1 r² + K2 r⁴) → 픽셀. 역투영 = ST 맵 격자(33×19 · 왜곡 → 무왜곡 정규 좌표 · SIMD 반복 역변환) 쌍선형 보간 →
/// 광선 → 평면 교차. 렌즈 값이 바뀔 때만 격자 재계산.
/// </summary>
public sealed class LensCamera
{
    public const int GridW = 33, GridH = 19, UndistortIterations = 10;
    private static readonly int GridPad = (GridW * GridH + 15) / 16 * 16;

    private readonly float[] _gxd = new float[GridPad], _gyd = new float[GridPad], _gxu = new float[GridPad], _gyu = new float[GridPad];
    private float _gridFov = -1, _gridK1 = float.NaN, _gridK2 = float.NaN;

    public LensCamera(int width, int height)
    {
        Width = width; Height = height; Cx = (width - 1) * 0.5f; Cy = (height - 1) * 0.5f;
        Set(0, 0, 0, Vector3.Zero, 45f, 0f, 0f);
    }

    public int Width { get; }
    public int Height { get; }
    public float Cx { get; }
    public float Cy { get; }
    public Vector3 Position { get; private set; }
    public Vector3 Forward { get; private set; } = Vector3.UnitZ;
    public Vector3 Right { get; private set; } = Vector3.UnitX;
    public Vector3 Up { get; private set; } = Vector3.UnitY;
    public float HFovDeg { get; private set; } = 45;
    public float K1 { get; private set; }
    public float K2 { get; private set; }
    public float Fx { get; private set; }
    public long GridRebuilds { get; private set; }
    public double LastGridUs { get; private set; }
    /// <summary>모서리 왜곡 변위(px) — 무왜곡 대비.</summary>
    public float CornerShiftPx { get; private set; }
    public float GridXu(int k) => _gxu[k];
    public float GridYu(int k) => _gyu[k];
    public float GridXd(int k) => _gxd[k];
    public float GridYd(int k) => _gyd[k];

    public void Set(float panDeg, float tiltDeg, float rollDeg, Vector3 pos, float hfovDeg, float k1, float k2, bool grid = true)
    {
        float p = panDeg * MathF.PI / 180f, t = tiltDeg * MathF.PI / 180f, r = rollDeg * MathF.PI / 180f;
        var f = new Vector3(MathF.Sin(p) * MathF.Cos(t), MathF.Sin(t), MathF.Cos(p) * MathF.Cos(t));
        var r0 = new Vector3(MathF.Cos(p), 0, -MathF.Sin(p));
        var u0 = Vector3.Cross(f, r0);
        Forward = f; Right = r0 * MathF.Cos(r) + u0 * MathF.Sin(r); Up = u0 * MathF.Cos(r) - r0 * MathF.Sin(r);
        Position = pos; HFovDeg = hfovDeg; K1 = k1; K2 = k2;
        Fx = Cx / MathF.Tan(hfovDeg * MathF.PI / 360f);
        if (grid && (hfovDeg != _gridFov || k1 != _gridK1 || k2 != _gridK2)) RebuildGrid();
    }

    /// <summary>ST 맵 격자 재계산(무할당 · SIMD 역왜곡).</summary>
    private void RebuildGrid()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var j = 0; j < GridH; j++)
            for (var i = 0; i < GridW; i++)
            {
                var k = j * GridW + i;
                float u = i * (Width - 1f) / (GridW - 1), v = j * (Height - 1f) / (GridH - 1);
                _gxd[k] = (u - Cx) / Fx; _gyd[k] = (Cy - v) / Fx;
            }
        FreeDSimd.Undistort(_gxd, _gyd, _gxu, _gyu, K1, K2, UndistortIterations);
        _gridFov = HFovDeg; _gridK1 = K1; _gridK2 = K2;
        CornerShiftPx = MathF.Sqrt((_gxu[0] - _gxd[0]) * (_gxu[0] - _gxd[0]) + (_gyu[0] - _gyd[0]) * (_gyu[0] - _gyd[0])) * Fx;
        GridRebuilds++;
        LastGridUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>월드 → 왜곡 픽셀(카메라 앞 0.1 m 이상만).</summary>
    public bool Project(Vector3 w, out float u, out float v)
    {
        var d = w - Position;
        var zc = Vector3.Dot(d, Forward);
        if (zc < 0.1f) { u = v = 0; return false; }
        float x = Vector3.Dot(d, Right) / zc, y = Vector3.Dot(d, Up) / zc;
        var r2 = x * x + y * y;
        if (r2 > 4f) { u = v = 0; return false; }
        var s = 1f + K1 * r2 + K2 * (r2 * r2);
        u = Cx + Fx * x * s; v = Cy - Fx * y * s;
        return true;
    }

    /// <summary>무왜곡 핀홀 투영(왜곡 비교용).</summary>
    public bool ProjectIdeal(Vector3 w, out float u, out float v)
    {
        var d = w - Position;
        var zc = Vector3.Dot(d, Forward);
        if (zc < 0.1f) { u = v = 0; return false; }
        u = Cx + Fx * Vector3.Dot(d, Right) / zc; v = Cy - Fx * Vector3.Dot(d, Up) / zc;
        return true;
    }

    /// <summary>깊이(카메라 전방 거리).</summary>
    public float Depth(Vector3 w) => Vector3.Dot(w - Position, Forward);

    /// <summary>픽셀 → 무왜곡 정규 좌표(ST 맵 쌍선형).</summary>
    public void GridRay(float u, float v, out float x, out float y)
    {
        var gi = Math.Clamp(u * (GridW - 1) / (Width - 1f), 0f, GridW - 1.0001f);
        var gj = Math.Clamp(v * (GridH - 1) / (Height - 1f), 0f, GridH - 1.0001f);
        int i = (int)gi, j = (int)gj; float fi = gi - i, fj = gj - j;
        int k = j * GridW + i;
        x = (_gxu[k] * (1 - fi) + _gxu[k + 1] * fi) * (1 - fj) + (_gxu[k + GridW] * (1 - fi) + _gxu[k + GridW + 1] * fi) * fj;
        y = (_gyu[k] * (1 - fi) + _gyu[k + 1] * fi) * (1 - fj) + (_gyu[k + GridW] * (1 - fi) + _gyu[k + GridW + 1] * fi) * fj;
    }

    /// <summary>픽셀 → 정확 역왜곡(반복) 광선.</summary>
    public Vector3 Ray(float u, float v)
    {
        float x0 = (u - Cx) / Fx, y0 = (Cy - v) / Fx, x = x0, y = y0;
        for (var it = 0; it < UndistortIterations; it++) { var r2 = x * x + y * y; var s = 1f + K1 * r2 + K2 * (r2 * r2); x = x0 / s; y = y0 / s; }
        return Forward + Right * x + Up * y;
    }

    /// <summary>광선 ∩ 평면(n·p + d = 0).</summary>
    public bool Intersect(Vector3 dir, Vector3 n, float d, out Vector3 hit)
    {
        var den = Vector3.Dot(n, dir);
        hit = default;
        if (MathF.Abs(den) < 1e-6f) return false;
        var t = -(Vector3.Dot(n, Position) + d) / den;
        if (t <= 0) return false;
        hit = Position + dir * t;
        return true;
    }
}

/// <summary>렌즈 캘리브레이션 표 1점(줌 엔코더 → 수평 FOV · K1 · K2).</summary>
public readonly record struct LensPoint(int Zoom, float HFovDeg, float K1, float K2);

/// <summary>
/// [PromptFlow Studio v17.06.0] 📐 실시간 FreeD 트래킹 &amp; 렌즈 왜곡 보정 엔진.
/// FreeD D1 스트림 수신(체크섬 검증 · 일괄 SIMD 역직렬화) → 자세 링(64) → 비디오 지연 정합(TrackingDelayFrames) →
/// 마운트 오프셋(팬/틸트) 및 FOV 스케일 보정 → 줌/포커스 엔코더 → 렌즈 표 보간(FOV · K1 · K2 · 포커스 브리딩) → 왜곡 카메라 · ST 맵 실시간 갱신.
/// 랜드마크 재투영 가우스-뉴턴(LM)으로 팬/틸트 오프셋 · FOV 스케일 캘리브레이션. 전 경로 무할당.
/// </summary>
public sealed class FreeDTrackingProtocolLensDistortionEngine
{
    public const int Ring = 64, MaxBatch = 64, MaxZoom = 65535;
    public static readonly LensPoint[] DefaultLens =
    {
        new(0, 62f, -0.115f, 0.018f), new(8000, 44f, -0.070f, 0.010f), new(18000, 30f, -0.035f, 0.004f),
        new(30000, 19f, -0.012f, 0.001f), new(45000, 11f, 0.004f, 0f), new(65535, 5.5f, 0.012f, 0f),
    };

    private readonly FreeDPose[] _ring = new FreeDPose[Ring];
    private readonly long[] _ringFrame = new long[Ring];
    private readonly byte[] _last = new byte[FreeD.PacketBytes];
    private readonly int[] _raw = new int[6 * MaxBatch], _zf = new int[2 * MaxBatch];
    private readonly float[] _fields = new float[6 * MaxBatch];
    private readonly LensCamera _cal;
    private long _latest = -1;

    public FreeDTrackingProtocolLensDistortionEngine(int width = 256, int height = 144)
    {
        Camera = new LensCamera(width, height);
        _cal = new LensCamera(width, height);
        Array.Fill(_ringFrame, -1L);
    }

    public LensCamera Camera { get; }
    public LensPoint[] Lens { get; set; } = DefaultLens;
    public int TrackingDelayFrames { get; set; } = 3;
    public float PanOffset { get; set; }
    public float TiltOffset { get; set; }
    public float FovScale { get; set; } = 1f;
    public float FocusBreathing { get; set; } = 0.015f;
    public long Packets { get; private set; }
    public long BadPackets { get; private set; }
    public long Applied { get; private set; }
    public long MissingPoses { get; private set; }
    public FreeDPose Current { get; private set; }
    public double LastDecodeUs { get; private set; }
    public float CalibrationRmsPx { get; private set; } = float.NaN;
    public int CalibrationPoints { get; private set; }
    public long Calibrations { get; private set; }
    public ReadOnlySpan<byte> LastPacket => _last;

    /// <summary>패킷 1개 수신(프레임 번호 = 수신 시각의 비디오 프레임).</summary>
    public bool Ingest(ReadOnlySpan<byte> packet, long frame)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!FreeD.Decode(packet, out var pose)) { BadPackets++; return false; }
        packet.Slice(0, FreeD.PacketBytes).CopyTo(_last);
        var s = (int)(frame % Ring);
        _ring[s] = pose; _ringFrame[s] = frame;
        if (frame > _latest) _latest = frame;
        Packets++;
        LastDecodeUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        return true;
    }

    /// <summary>다중 패킷 스트림 일괄 수신(SIMD 스케일) — 마지막 유효 패킷을 프레임에 기록.</summary>
    public int IngestBatch(ReadOnlySpan<byte> stream, long frame)
    {
        var n = FreeD.DecodeBatch(stream, _raw, _fields, _zf, MaxBatch);
        BadPackets += stream.Length / FreeD.PacketBytes - n;
        if (n == 0) return 0;
        var k = n - 1;
        var pose = new FreeDPose
        {
            Pan = _fields[k], Tilt = _fields[MaxBatch + k], Roll = _fields[2 * MaxBatch + k],
            X = _fields[3 * MaxBatch + k], Y = _fields[4 * MaxBatch + k], Z = _fields[5 * MaxBatch + k],
            Zoom = _zf[k], Focus = _zf[MaxBatch + k],
        };
        var s = (int)(frame % Ring);
        _ring[s] = pose; _ringFrame[s] = frame;
        if (frame > _latest) _latest = frame;
        Packets += n;
        return n;
    }

    public float BatchField(int field, int i) => _fields[field * MaxBatch + i];

    /// <summary>비디오 프레임에 맞는 자세 선택(frame − 지연) → 카메라 갱신. 결손 시 가장 가까운 과거 자세.</summary>
    public bool Apply(long frame)
    {
        var want = frame - TrackingDelayFrames;
        for (var back = 0; back < 8; back++)
        {
            var f = want - back;
            if (f < 0) break;
            var s = (int)(f % Ring);
            if (_ringFrame[s] != f) continue;
            if (back > 0) MissingPoses++;
            Current = _ring[s];
            Configure(Camera, _ring[s], PanOffset, TiltOffset, FovScale, true);
            Applied++;
            return true;
        }
        MissingPoses++;
        return false;
    }

    /// <summary>엔코더 → 렌즈 표 선형 보간 + 포커스 브리딩.</summary>
    public void LensAt(int zoom, int focus, out float hfov, out float k1, out float k2)
    {
        var t = Lens;
        if (zoom <= t[0].Zoom) { hfov = t[0].HFovDeg; k1 = t[0].K1; k2 = t[0].K2; }
        else if (zoom >= t[^1].Zoom) { hfov = t[^1].HFovDeg; k1 = t[^1].K1; k2 = t[^1].K2; }
        else
        {
            var i = 1;
            while (t[i].Zoom < zoom) i++;
            var a = (zoom - t[i - 1].Zoom) / (float)(t[i].Zoom - t[i - 1].Zoom);
            hfov = t[i - 1].HFovDeg + (t[i].HFovDeg - t[i - 1].HFovDeg) * a;
            k1 = t[i - 1].K1 + (t[i].K1 - t[i - 1].K1) * a;
            k2 = t[i - 1].K2 + (t[i].K2 - t[i - 1].K2) * a;
        }
        hfov *= 1f - FocusBreathing * Math.Clamp(focus / (float)MaxZoom, 0f, 1f);
    }

    /// <summary>원하는 FOV → 줌 엔코더(단조 이분 탐색).</summary>
    public int ZoomForFov(float hfov)
    {
        int lo = 0, hi = MaxZoom;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            LensAt(mid, 0, out var f, out _, out _);
            if (f > hfov) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    public void Configure(LensCamera cam, in FreeDPose p, float panOff, float tiltOff, float fovScale, bool grid)
    {
        LensAt(p.Zoom, p.Focus, out var hfov, out var k1, out var k2);
        cam.Set(p.Pan + panOff, p.Tilt + tiltOff, p.Roll, new Vector3(p.X, p.Z, p.Y), hfov * fovScale, k1, k2, grid);
    }

    /// <summary>
    /// 랜드마크 재투영 캘리브레이션(LM 가우스-뉴턴 · 수치 야코비안): 미지수 = 팬/틸트 마운트 오프셋 · FOV 스케일.
    /// 화면 안 랜드마크 ≥ 4 필요. 반환 = 보정 후 RMS(px) · 실패 시 NaN.
    /// </summary>
    public float Calibrate(in FreeDPose raw, ReadOnlySpan<Vector3> world, ReadOnlySpan<Vector2> observed, ReadOnlySpan<bool> seen)
    {
        double p0 = PanOffset, p1 = TiltOffset, p2 = FovScale;
        var count = 0;
        for (var i = 0; i < world.Length; i++) if (seen[i]) count++;
        CalibrationPoints = count;
        if (count < 4) return float.NaN;
        Span<double> jt = stackalloc double[3];
        for (var iter = 0; iter < 15; iter++)
        {
            double a00 = 0, a01 = 0, a02 = 0, a11 = 0, a12 = 0, a22 = 0, b0 = 0, b1 = 0, b2 = 0;
            for (var i = 0; i < world.Length; i++)
            {
                if (!seen[i]) continue;
                Configure(_cal, raw, (float)p0, (float)p1, (float)p2, false);
                if (!_cal.Project(world[i], out var u, out var v)) continue;
                double ru = observed[i].X - u, rv = observed[i].Y - v;
                for (var axis = 0; axis < 2; axis++)
                {
                    for (var k = 0; k < 3; k++)
                    {
                        var e = k == 2 ? 1e-4 : 1e-3;
                        Configure(_cal, raw, (float)(p0 + (k == 0 ? e : 0)), (float)(p1 + (k == 1 ? e : 0)), (float)(p2 + (k == 2 ? e : 0)), false);
                        _cal.Project(world[i], out var u2, out var v2);
                        jt[k] = ((axis == 0 ? u2 : v2) - (axis == 0 ? u : v)) / e;
                    }
                    var r = axis == 0 ? ru : rv;
                    a00 += jt[0] * jt[0]; a01 += jt[0] * jt[1]; a02 += jt[0] * jt[2]; a11 += jt[1] * jt[1]; a12 += jt[1] * jt[2]; a22 += jt[2] * jt[2];
                    b0 += jt[0] * r; b1 += jt[1] * r; b2 += jt[2] * r;
                }
            }
            var lm = 1e-6 * (a00 + a11 + a22);
            a00 += lm; a11 += lm; a22 += lm;
            var det = a00 * (a11 * a22 - a12 * a12) - a01 * (a01 * a22 - a12 * a02) + a02 * (a01 * a12 - a11 * a02);
            if (Math.Abs(det) < 1e-18) break;
            var d0 = (b0 * (a11 * a22 - a12 * a12) - a01 * (b1 * a22 - a12 * b2) + a02 * (b1 * a12 - a11 * b2)) / det;
            var d1 = (a00 * (b1 * a22 - a12 * b2) - b0 * (a01 * a22 - a12 * a02) + a02 * (a01 * b2 - b1 * a02)) / det;
            var d2 = (a00 * (a11 * b2 - b1 * a12) - a01 * (a01 * b2 - b1 * a02) + b0 * (a01 * a12 - a11 * a02)) / det;
            p0 += d0; p1 += d1; p2 += d2;
            if (Math.Abs(d0) < 1e-6 && Math.Abs(d1) < 1e-6 && Math.Abs(d2) < 1e-8) break;
        }
        PanOffset = (float)p0; TiltOffset = (float)p1; FovScale = (float)p2;
        Configure(_cal, raw, PanOffset, TiltOffset, FovScale, false);
        double se = 0; var m = 0;
        for (var i = 0; i < world.Length; i++)
        {
            if (!seen[i] || !_cal.Project(world[i], out var u, out var v)) continue;
            se += (observed[i].X - u) * (observed[i].X - u) + (observed[i].Y - v) * (observed[i].Y - v); m++;
        }
        Calibrations++;
        CalibrationRmsPx = m == 0 ? float.NaN : (float)Math.Sqrt(se / m);
        return CalibrationRmsPx;
    }
}
