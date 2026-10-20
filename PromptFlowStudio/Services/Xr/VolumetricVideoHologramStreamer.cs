using System.Buffers.Binary;
using System.Buffers.Text;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PromptFlowStudio.Services.DigitalTwin;
using PromptFlowStudio.Services.VTuber;

namespace PromptFlowStudio.Services.Xr;

public enum HoloSource : byte { Mocap, Twin, Rgbd }

public sealed record HoloStreamOptions(HoloSource Source = HoloSource.Mocap, int TargetFps = 30, int Port = VolumetricVideoHologramStreamer.DefaultPort,
    string? RtpHost = null, int RtpPort = 0, int KeyInterval = 30, int Density = 24);

/// <summary>볼류메트릭 포인트 클라우드 프레임(사전 할당 · 재사용) — 월드 좌표(m · Y 상향) + 0xRRGGBB.</summary>
public sealed class PointCloudFrame
{
    public const int MaxPoints = 8192;
    public readonly float[] Xyz = new float[MaxPoints * 3];
    public readonly uint[] Rgb = new uint[MaxPoints];
    public int Count;
    public long Seq;

    public void Clear() => Count = 0;

    public bool Add(float x, float y, float z, uint rgb)
    {
        if (Count >= MaxPoints) return false;
        var i = Count * 3;
        Xyz[i] = x; Xyz[i + 1] = y; Xyz[i + 2] = z;
        Rgb[Count++] = rgb;
        return true;
    }
}

/// <summary>
/// 무할당 볼류메트릭 코덱 — 16bit 격자 양자화(±Extent m) · 키프레임(점간 공간 델타) / 인터프레임(동일 인덱스 시간 델타) · ZigZag varint · RGB565(변경 시에만).
/// 헤더 12B: "PFVH" · ver · flags(1=Key · 2=Color) · count u16 · seq u32.
/// </summary>
public sealed class VolumetricCodec
{
    public const float Extent = 2.5f;
    public const int HeaderSize = 12, MaxPointBytes = 11;
    public const byte Version = 1, FlagKey = 1, FlagColor = 2;
    public static int MaxEncodedSize(int points) => HeaderSize + points * MaxPointBytes;

    private readonly ushort[] _q = new ushort[PointCloudFrame.MaxPoints * 3];
    private readonly ushort[] _prevQ = new ushort[PointCloudFrame.MaxPoints * 3];
    private readonly ushort[] _prevC = new ushort[PointCloudFrame.MaxPoints];
    private int _prevCount = -1, _sinceKey;

    public int KeyInterval { get; set; } = 30;

    public static ushort Quantize(float v) => (ushort)Math.Clamp((int)MathF.Round((v + Extent) / (2f * Extent) * 65535f), 0, 65535);
    public static float Dequantize(ushort q) => q / 65535f * (2f * Extent) - Extent;
    public static ushort ToRgb565(uint rgb) => (ushort)(((rgb >> 8) & 0xF800) | ((rgb >> 5) & 0x07E0) | ((rgb >> 3) & 0x001F));
    public static uint FromRgb565(ushort c) { uint r = (uint)(c >> 11) & 31, g = (uint)(c >> 5) & 63, b = (uint)c & 31; return ((r * 255 / 31) << 16) | ((g * 255 / 63) << 8) | (b * 255 / 31); }

    public void Reset() { _prevCount = -1; _sinceKey = 0; }

    /// <summary>프레임 인코딩(무할당). 반환 = 기록 바이트(버퍼 부족 시 0).</summary>
    public int Encode(PointCloudFrame f, Span<byte> dst)
    {
        var n = f.Count;
        if (dst.Length < MaxEncodedSize(n)) return 0;
        var key = n != _prevCount || _sinceKey >= KeyInterval || _prevCount < 0;
        for (var i = 0; i < n * 3; i++) _q[i] = Quantize(f.Xyz[i]);
        var color = key;
        if (!key) for (var i = 0; i < n; i++) if (ToRgb565(f.Rgb[i]) != _prevC[i]) { color = true; break; }

        "PFVH"u8.CopyTo(dst);
        dst[4] = Version;
        dst[5] = (byte)((key ? FlagKey : 0) | (color ? FlagColor : 0));
        BinaryPrimitives.WriteUInt16LittleEndian(dst[6..], (ushort)n);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[8..], (uint)f.Seq);
        var p = HeaderSize;
        for (var i = 0; i < n; i++)
        {
            for (var a = 0; a < 3; a++)
            {
                var k = i * 3 + a;
                int reference = key ? (i == 0 ? 32768 : _q[k - 3]) : _prevQ[k];
                p += WriteVar(dst[p..], ZigZag(_q[k] - reference));
            }
            if (color)
            {
                var c = ToRgb565(f.Rgb[i]);
                BinaryPrimitives.WriteUInt16LittleEndian(dst[p..], c);
                p += 2;
                _prevC[i] = c;
            }
        }
        _q.AsSpan(0, n * 3).CopyTo(_prevQ);
        _prevCount = n;
        _sinceKey = key ? 1 : _sinceKey + 1;
        return p;
    }

    /// <summary>디코딩(무할당 · 인코더와 동일 상태 머신). 실패 시 false(키프레임 대기).</summary>
    public bool Decode(ReadOnlySpan<byte> src, PointCloudFrame dst)
    {
        if (src.Length < HeaderSize || !src[..4].SequenceEqual("PFVH"u8) || src[4] != Version) return false;
        var flags = src[5];
        var n = BinaryPrimitives.ReadUInt16LittleEndian(src[6..]);
        if (n > PointCloudFrame.MaxPoints) return false;
        var key = (flags & FlagKey) != 0;
        var color = (flags & FlagColor) != 0;
        if (!key && n != _prevCount) return false;
        var p = HeaderSize;
        for (var i = 0; i < n; i++)
        {
            for (var a = 0; a < 3; a++)
            {
                var k = i * 3 + a;
                if (!TryReadVar(src, ref p, out var z)) return false;
                int reference = key ? (i == 0 ? 32768 : _q[k - 3]) : _prevQ[k];
                _q[k] = (ushort)(reference + UnZigZag(z));
            }
            if (color)
            {
                if (p + 2 > src.Length) return false;
                _prevC[i] = BinaryPrimitives.ReadUInt16LittleEndian(src[p..]);
                p += 2;
            }
        }
        for (var i = 0; i < n * 3; i++) dst.Xyz[i] = Dequantize(_q[i]);
        for (var i = 0; i < n; i++) dst.Rgb[i] = FromRgb565(_prevC[i]);
        dst.Count = n;
        dst.Seq = BinaryPrimitives.ReadUInt32LittleEndian(src[8..]);
        _q.AsSpan(0, n * 3).CopyTo(_prevQ);
        _prevCount = n;
        return true;
    }

    private static uint ZigZag(int v) => (uint)((v << 1) ^ (v >> 31));
    private static int UnZigZag(uint v) => (int)(v >> 1) ^ -(int)(v & 1);

    private static int WriteVar(Span<byte> d, uint v)
    {
        var i = 0;
        while (v >= 0x80) { d[i++] = (byte)(v | 0x80); v >>= 7; }
        d[i++] = (byte)v;
        return i;
    }

    private static bool TryReadVar(ReadOnlySpan<byte> s, ref int p, out uint v)
    {
        v = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            if (p >= s.Length) return false;
            var b = s[p++];
            v |= (uint)(b & 0x7F) << shift;
            if (b < 0x80) return true;
        }
        return false;
    }
}

/// <summary>RTP(RFC 3550) 패킷화 — 동적 PT 96 · 90kHz 타임스탬프 · MTU 1200 분할 · 마지막 조각 마커.</summary>
public static class HoloRtp
{
    public const int HeaderSize = 12, MaxPayload = 1188, PayloadType = 96;

    public static void WriteHeader(Span<byte> d, bool marker, ushort seq, uint ts, uint ssrc)
    {
        d[0] = 0x80;
        d[1] = (byte)((marker ? 0x80 : 0) | PayloadType);
        BinaryPrimitives.WriteUInt16BigEndian(d[2..], seq);
        BinaryPrimitives.WriteUInt32BigEndian(d[4..], ts);
        BinaryPrimitives.WriteUInt32BigEndian(d[8..], ssrc);
    }

    public static string BuildSdp(string host, int port, uint ssrc)
    {
        var inv = CultureInfo.InvariantCulture;
        return "v=0\r\no=- " + ssrc.ToString(inv) + " 1 IN IP4 " + host + "\r\ns=PromptFlow Volumetric Hologram\r\nc=IN IP4 " + host + "\r\nt=0 0\r\n"
             + "m=application " + port.ToString(inv) + " RTP/AVP 96\r\na=rtpmap:96 x-pfs-volumetric/90000\r\na=fmtp:96 codec=PFVH;ver=1;extent=2.5\r\na=sendonly\r\n";
    }
}

/// <summary>
/// [PromptFlow Studio v16.40.0] 🥽 3D 볼류메트릭 비디오 &amp; 홀로그램 스트리밍 트랜스코더.
///   ▸ 소스: v16.36.0 모캡(33 관절 → 본 캡슐 볼륨 샘플링) · v16.36.0 디지털 트윈 메시(턴테이블) · RGB-D/영상 프레임(휘도 의사 깊이).
///   ▸ 무할당 코덱(VolumetricCodec) → ① 루프백 WebXR 뷰어(SSE · base64 · WebGL 포인트 · immersive-vr) ② RTP/UDP(+SDP · RTSP 서버 인제스트용).
///   ▸ 전용 스레드 데드라인 루프 · 모캡 싱크는 락 내 프레임 복사만 · 무대/관객 상태 JSON 동일 SSE 채널 다중화.
/// </summary>
public sealed class VolumetricVideoHologramStreamer : IDisposable
{
    public const int DefaultPort = 17862;
    private static readonly float[] Ring = BuildRing(8);

    private readonly RealtimeWebcamMotionCaptureTracker? _tracker;
    private readonly AiDigitalTwinGeneratorEngine? _twins;
    private readonly OverlaySseServer _server;
    private readonly VolumetricCodec _codec = new();
    private readonly PointCloudFrame _frame = new();
    private readonly MocapFrame _latest = new(), _work = new();
    private readonly object _gate = new(), _pubGate = new();
    private readonly byte[] _enc = new byte[VolumetricCodec.MaxEncodedSize(PointCloudFrame.MaxPoints)];
    private readonly byte[] _sse = new byte[8 + (VolumetricCodec.MaxEncodedSize(PointCloudFrame.MaxPoints) + 2) / 3 * 4 + 4];
    private readonly byte[] _state = new byte[16 * 1024];
    private readonly byte[] _rtp = new byte[HoloRtp.HeaderSize + HoloRtp.MaxPayload];
    private readonly Action<MocapFrame> _sink;
    private Thread? _thread;
    private volatile bool _run;
    private Socket? _udp;
    private HoloStreamOptions _opt = new();
    private long _frames, _bytesOut, _rawBytes, _rtpPackets, _mocapSeen;
    private ushort _rtpSeq;
    private readonly uint _ssrc = (uint)Environment.TickCount ^ 0x5046_5648u;
    private double _fps, _encodeMs;
    private int _lastPoints, _lastBytes;
    private long _lastRaise;
    private byte[]? _rgbd;
    private int _rgbdW, _rgbdH;

    public VolumetricVideoHologramStreamer(IServiceProvider sp)
        : this(sp.GetService(typeof(RealtimeWebcamMotionCaptureTracker)) as RealtimeWebcamMotionCaptureTracker,
               sp.GetService(typeof(AiDigitalTwinGeneratorEngine)) as AiDigitalTwinGeneratorEngine) { }

    public VolumetricVideoHologramStreamer(RealtimeWebcamMotionCaptureTracker? tracker, AiDigitalTwinGeneratorEngine? twins)
    {
        _tracker = tracker;
        _twins = twins;
        _server = new OverlaySseServer(Path.Combine(AppContext.BaseDirectory, "overlay_media"), XrViewerHtml);
        _sink = OnMocap;
    }

    public event Action? OnChange;

    public bool IsRunning => _thread is not null;
    public HoloSource Source => _opt.Source;
    public string Url => _server.Url;
    public int Clients => _server.Clients;
    public long Frames => Interlocked.Read(ref _frames);
    public long BytesOut => Interlocked.Read(ref _bytesOut);
    public long RtpPackets => Interlocked.Read(ref _rtpPackets);
    public double Fps => Volatile.Read(ref _fps);
    public double EncodeMs => Volatile.Read(ref _encodeMs);
    public int LastPoints => Volatile.Read(ref _lastPoints);
    public int LastBytes => Volatile.Read(ref _lastBytes);
    public double CompressionRatio { get { var b = Interlocked.Read(ref _bytesOut); return b == 0 ? 0 : Interlocked.Read(ref _rawBytes) / (double)b; } }
    public bool RtpActive => _udp is not null;
    public string? Sdp { get; private set; }
    public string? LastAction { get; private set; }
    public PointCloudFrame Frame => _frame;

    /// <summary>UI 프리뷰용 투영 샘플(최대 max 점 · x,y,rgb). UI 스레드 전용 · 락 복사.</summary>
    public int CopyPreview(Span<float> xyz, Span<uint> rgb)
    {
        lock (_gate)
        {
            var n = _frame.Count;
            if (n == 0) return 0;
            var max = Math.Min(rgb.Length, xyz.Length / 3);
            var step = Math.Max(1, n / Math.Max(1, max));
            var c = 0;
            for (var i = 0; i < n && c < max; i += step, c++)
            {
                xyz[c * 3] = _frame.Xyz[i * 3]; xyz[c * 3 + 1] = _frame.Xyz[i * 3 + 1]; xyz[c * 3 + 2] = _frame.Xyz[i * 3 + 2];
                rgb[c] = _frame.Rgb[i];
            }
            return c;
        }
    }

    // ══════════════════════ 생성(무할당) ══════════════════════

    private static float[] BuildRing(int n)
    {
        var r = new float[n * 2];
        for (var i = 0; i < n; i++) { r[i * 2] = MathF.Cos(i * MathF.Tau / n); r[i * 2 + 1] = MathF.Sin(i * MathF.Tau / n); }
        return r;
    }

    private static uint HoloColor(float y)
    {
        var t = Math.Clamp(y / 2f, 0f, 1f);
        var r = (uint)(40 + 80 * t); var g = (uint)(200 + 40 * t); var b = 255u;
        return (r << 16) | (g << 8) | b;
    }

    /// <summary>모캡 → 볼륨(본별 캡슐 링 샘플 + 머리 구). density = 본당 축 샘플 수.</summary>
    public static int GenerateFromMocap(MocapFrame f, PointCloudFrame dst, int density = 24)
    {
        dst.Clear();
        var bones = MocapTopology.PoseBones;
        var pose = f.Pose;
        var ringN = Ring.Length / 2;
        density = Math.Clamp(density, 4, 64);
        for (var b = 0; b + 1 < bones.Length; b += 2)
        {
            int ja = bones[b] * MocapFrame.PoseStride, jb = bones[b + 1] * MocapFrame.PoseStride;
            if (pose[ja + 3] < 0.3f || pose[jb + 3] < 0.3f) continue;
            float ax = (pose[ja] - 0.5f) * 2f, ay = (1f - pose[ja + 1]) * 2f, az = -pose[ja + 2];
            float bx = (pose[jb] - 0.5f) * 2f, by = (1f - pose[jb + 1]) * 2f, bz = -pose[jb + 2];
            var radius = bones[b] >= 23 ? 0.06f : bones[b] >= 11 ? 0.045f : 0.02f;
            for (var s = 0; s < density; s++)
            {
                var t = s / (float)(density - 1);
                float cx = ax + (bx - ax) * t, cy = ay + (by - ay) * t, cz = az + (bz - az) * t;
                var phase = s % ringN;
                for (var r = 0; r < ringN; r += 2)
                {
                    var k = ((r + phase) % ringN) * 2;
                    if (!dst.Add(cx + Ring[k] * radius, cy, cz + Ring[k + 1] * radius, HoloColor(cy))) return dst.Count;
                }
            }
        }
        if (pose[3] >= 0.3f)
        {
            float hx = (pose[0] - 0.5f) * 2f, hy = (1f - pose[1]) * 2f, hz = -pose[2];
            for (var lat = 1; lat < 8; lat++)
            {
                var el = lat / 8f * MathF.PI;
                float sy = MathF.Cos(el) * 0.11f, rr = MathF.Sin(el) * 0.09f;
                for (var r = 0; r < ringN; r++)
                    if (!dst.Add(hx + Ring[r * 2] * rr, hy + sy, hz + Ring[r * 2 + 1] * rr, HoloColor(hy + sy))) return dst.Count;
            }
        }
        return dst.Count;
    }

    /// <summary>디지털 트윈 메시 → 볼륨(턴테이블 회전 · 피부색 셰이딩).</summary>
    public static int GenerateFromTwin(TwinMesh mesh, uint skin, float yaw, PointCloudFrame dst)
    {
        dst.Clear();
        var v = mesh.Vertices;
        var n = v.Length / 3;
        if (n == 0) return 0;
        var step = Math.Max(1, (n + PointCloudFrame.MaxPoints - 1) / PointCloudFrame.MaxPoints);
        float cs = MathF.Cos(yaw), sn = MathF.Sin(yaw);
        uint sr = (skin >> 16) & 255, sg = (skin >> 8) & 255, sb = skin & 255;
        for (var i = 0; i < n; i += step)
        {
            float x = v[i * 3], y = v[i * 3 + 1], z = v[i * 3 + 2];
            var shade = Math.Clamp(0.55f + z * 0.9f, 0.3f, 1f);
            if (!dst.Add(x * cs + z * sn, 1.55f + y * 0.6f, -x * sn + z * cs,
                    ((uint)(sr * shade) << 16) | ((uint)(sg * shade) << 8) | (uint)(sb * shade))) break;
        }
        return dst.Count;
    }

    /// <summary>RGBA 영상 → 볼륨(휘도 의사 깊이 · 어두운 배경 키아웃 · step 픽셀 간격).</summary>
    public static int GenerateFromRgbd(ReadOnlySpan<byte> rgba, int w, int h, int stride, ReadOnlySpan<float> depth, int step, PointCloudFrame dst)
    {
        dst.Clear();
        step = Math.Max(1, step);
        var aspect = h / (float)Math.Max(1, w);
        for (var y = 0; y < h; y += step)
        {
            for (var x = 0; x < w; x += step)
            {
                var o = y * stride + x * 4;
                uint r = rgba[o], g = rgba[o + 1], b = rgba[o + 2];
                var lum = (0.299f * r + 0.587f * g + 0.114f * b) / 255f;
                if (lum < 0.08f) continue;
                var d = depth.IsEmpty ? 0.6f - lum * 0.6f : depth[y * w + x];
                if (!dst.Add((x / (float)w - 0.5f) * 2f, (1f - y / (float)h) * 2f * aspect, -d, (r << 16) | (g << 8) | b)) return dst.Count;
            }
        }
        return dst.Count;
    }

    /// <summary>합성 RGBA 소스 설정(Rgbd 모드 · null 해제).</summary>
    public void SetRgbdFrame(byte[]? rgba, int w, int h) { lock (_gate) { _rgbd = rgba; _rgbdW = w; _rgbdH = h; } }

    // ══════════════════════ 수명 ══════════════════════

    public void Start(HoloStreamOptions? options = null)
    {
        if (_thread is not null) return;
        _opt = options ?? new HoloStreamOptions();
        _codec.KeyInterval = Math.Clamp(_opt.KeyInterval, 1, 600);
        _codec.Reset();
        _server.Start(_opt.Port);
        if (_opt.RtpHost is { Length: > 0 } host && _opt.RtpPort > 0) OpenRtp(host, _opt.RtpPort);
        _tracker?.AddSink(_sink);
        _run = true;
        var t = new Thread(Loop) { IsBackground = true, Name = "PFS.Hologram", Priority = ThreadPriority.AboveNormal };
        _thread = t;
        t.Start();
        LastAction = "🥽 홀로그램 스트림 ON · " + _server.Url + (RtpActive ? " · RTP " + _opt.RtpHost + ":" + _opt.RtpPort.ToString(CultureInfo.InvariantCulture) : "");
        Raise(true);
    }

    public void Stop()
    {
        var t = Interlocked.Exchange(ref _thread, null);
        if (t is null) return;
        _run = false;
        _tracker?.RemoveSink(_sink);
        t.Join(1000);
        _server.Dispose();
        var u = Interlocked.Exchange(ref _udp, null);
        u?.Dispose();
        Volatile.Write(ref _fps, 0);
        LastAction = "⏹ 홀로그램 스트림 OFF";
        Raise(true);
    }

    private void OpenRtp(string host, int port)
    {
        try
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            if (OperatingSystem.IsWindows()) { try { s.IOControl(unchecked((int)0x9800000C), new byte[4], null); } catch (Exception ex) { Program.Log("Holo.ConnReset", ex); } }
            var ip = IPAddress.TryParse(host, out var a) ? a : IPAddress.Loopback;
            s.Connect(new IPEndPoint(ip, port));
            _udp = s;
            Sdp = HoloRtp.BuildSdp(ip.ToString(), port, _ssrc);
        }
        catch (Exception ex) { Program.Log("Holo.Rtp", ex); LastAction = "⚠ RTP 열기 실패 · SSE 단독 송출"; }
    }

    private void OnMocap(MocapFrame f)
    {
        lock (_gate) f.CopyTo(_latest);
        Interlocked.Increment(ref _mocapSeen);
    }

    private void Loop()
    {
        var sw = Stopwatch.StartNew();
        var period = 1000.0 / Math.Clamp(_opt.TargetFps, 1, 90);
        var next = 0.0;
        var winStart = 0.0;
        var winFrames = 0;
        while (_run)
        {
            var now = sw.Elapsed.TotalMilliseconds;
            try { ProcessOnce(now); }
            catch (Exception ex) { Program.Log("Holo.Tick", ex); }
            winFrames++;
            if (now - winStart >= 1000)
            {
                Volatile.Write(ref _fps, winFrames * 1000.0 / Math.Max(1, now - winStart));
                winStart = now; winFrames = 0;
            }
            next += period;
            var wait = next - sw.Elapsed.TotalMilliseconds;
            if (wait < -period * 4) next = sw.Elapsed.TotalMilliseconds;
            else if (wait > 1) Thread.Sleep((int)wait);
        }
    }

    /// <summary>1 프레임: 생성 → 인코딩 → SSE(base64) · RTP 송출. 워밍업 후 무할당. 반환 = 인코딩 바이트.</summary>
    public int ProcessOnce(double nowMs)
    {
        var t0 = Stopwatch.GetTimestamp();
        int bytes;
        lock (_gate)
        {
            switch (_opt.Source)
            {
                case HoloSource.Twin when _twins?.Current is { Mesh: { } mesh } twin:
                    GenerateFromTwin(mesh, ParseHex(twin.SkinHex), (float)(nowMs * 0.0006), _frame);
                    break;
                case HoloSource.Rgbd when _rgbd is { } px:
                    GenerateFromRgbd(px, _rgbdW, _rgbdH, _rgbdW * 4, ReadOnlySpan<float>.Empty, 4, _frame);
                    break;
                default:
                    if (Interlocked.Read(ref _mocapSeen) > 0) _latest.CopyTo(_work);
                    else SynthesizeIdlePose(_work, nowMs);
                    GenerateFromMocap(_work, _frame, _opt.Density);
                    break;
            }
            _frame.Seq++;
            bytes = _codec.Encode(_frame, _enc);
            Volatile.Write(ref _lastPoints, _frame.Count);
        }
        if (bytes <= 0) return 0;
        Interlocked.Add(ref _rawBytes, (long)_frame.Count * 15);
        Interlocked.Add(ref _bytesOut, bytes);
        Interlocked.Increment(ref _frames);
        Volatile.Write(ref _lastBytes, bytes);

        if (_server.IsRunning)
        {
            "data: V"u8.CopyTo(_sse);
            Base64.EncodeToUtf8(_enc.AsSpan(0, bytes), _sse.AsSpan(7), out _, out var w);
            _sse[7 + w] = (byte)'\n'; _sse[8 + w] = (byte)'\n';
            lock (_pubGate) _server.BroadcastRaw(_sse.AsSpan(0, 9 + w));
        }
        if (_udp is { } u) SendRtp(u, bytes, nowMs);
        Volatile.Write(ref _encodeMs, Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        Raise(false);
        return bytes;
    }

    private void SendRtp(Socket u, int bytes, double nowMs)
    {
        var ts = (uint)(nowMs * 90.0);
        for (var off = 0; off < bytes; off += HoloRtp.MaxPayload)
        {
            var len = Math.Min(HoloRtp.MaxPayload, bytes - off);
            HoloRtp.WriteHeader(_rtp, off + len >= bytes, _rtpSeq++, ts, _ssrc);
            _enc.AsSpan(off, len).CopyTo(_rtp.AsSpan(HoloRtp.HeaderSize));
            try { u.Send(_rtp.AsSpan(0, HoloRtp.HeaderSize + len)); Interlocked.Increment(ref _rtpPackets); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { return; }
        }
    }

    /// <summary>무대/관객 상태 JSON(UTF-8)을 동일 SSE 채널로 다중화 송출(무할당).</summary>
    public int PublishState(ReadOnlySpan<byte> json)
    {
        if (!_server.IsRunning || json.Length + 10 > _state.Length) return 0;
        lock (_pubGate)
        {
            "data: "u8.CopyTo(_state);
            json.CopyTo(_state.AsSpan(6));
            _state[6 + json.Length] = (byte)'\n'; _state[7 + json.Length] = (byte)'\n';
            return _server.BroadcastRaw(_state.AsSpan(0, 8 + json.Length));
        }
    }

    /// <summary>모캡 미수신 시 합성 아이들 포즈(호흡 · 팔 스윙) — 무할당.</summary>
    public static void SynthesizeIdlePose(MocapFrame f, double nowMs)
    {
        var t = (float)(nowMs / 1000.0);
        var sw = MathF.Sin(t * 2.2f) * 0.06f;
        PoseBuilder.Write(f.Pose, 0.5f, 0.16f + MathF.Sin(t * 1.3f) * 0.005f, 0.1f, 0.36f + sw, 0.52f - sw, 0.64f - sw, 0.52f + sw, MathF.Sin(t * 4.4f) * 0.01f, 1f);
        f.Confidence = 1f;
        f.TimestampMs = nowMs;
    }

    private static uint ParseHex(string? hex)
    {
        if (hex is { Length: 7 } && hex[0] == '#' && uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return v;
        return 0xC8A07F;
    }

    private void Raise(bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now - Interlocked.Read(ref _lastRaise) < 250) return;
        Interlocked.Exchange(ref _lastRaise, now);
        try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("Holo.OnChange", ex); }
    }

    public void Dispose() { Stop(); _server.Dispose(); }

    // ══════════════════════ WebXR 뷰어 ══════════════════════

    private const string XrViewerHtml = """
<!doctype html><html><head><meta charset="utf-8"><title>PromptFlow XR Hologram Stage</title>
<style>html,body{margin:0;height:100%;background:#03040a;overflow:hidden;font-family:sans-serif;color:#cfe}#c{width:100%;height:100%;display:block}
#ui{position:fixed;left:12px;top:10px;font-size:12px;opacity:.8}#vr{position:fixed;right:12px;top:10px;padding:8px 14px;border-radius:8px;border:1px solid #5cf;background:#0a1830;color:#bdf}
#fx{position:fixed;left:0;right:0;bottom:16px;text-align:center;font-size:42px;pointer-events:none}</style></head>
<body><canvas id="c"></canvas><div id="ui">PFS Volumetric · <span id="st">connecting…</span></div><button id="vr" hidden>🥽 Enter VR</button><div id="fx"></div>
<script>
const E=2.5,cv=document.getElementById('c'),gl=cv.getContext('webgl',{xrCompatible:true,antialias:true});
const vs=`attribute vec3 p;attribute vec3 c;uniform mat4 P,V;uniform float s;varying vec3 vc;void main(){gl_Position=P*V*vec4(p,1.);gl_PointSize=s/gl_Position.w;vc=c;}`;
const fs=`precision mediump float;varying vec3 vc;uniform vec3 tint;void main(){vec2 d=gl_PointCoord-.5;if(dot(d,d)>.25)discard;gl_FragColor=vec4(vc*tint,1.);}`;
function sh(t,s){const o=gl.createShader(t);gl.shaderSource(o,s);gl.compileShader(o);return o}
const pr=gl.createProgram();gl.attachShader(pr,sh(gl.VERTEX_SHADER,vs));gl.attachShader(pr,sh(gl.FRAGMENT_SHADER,fs));gl.linkProgram(pr);gl.useProgram(pr);
const bp=gl.createBuffer(),bc=gl.createBuffer(),L={p:gl.getAttribLocation(pr,'p'),c:gl.getAttribLocation(pr,'c'),P:gl.getUniformLocation(pr,'P'),V:gl.getUniformLocation(pr,'V'),s:gl.getUniformLocation(pr,'s'),t:gl.getUniformLocation(pr,'tint')};
let q=new Uint16Array(0),pc=new Uint16Array(0),pos=new Float32Array(0),col=new Float32Array(0),n=0,prevN=-1,tint=[1,1,1],stage=null;
function rv(b,o){let v=0,s=0,x;do{x=b[o.i++];v|=(x&127)<<s;s+=7}while(x&128);return v>>>0}
function dec(b){if(b[0]!=80||b[1]!=70||b[2]!=86||b[3]!=72)return;const f=b[5],cnt=b[6]|b[7]<<8,key=f&1,colr=f&2;if(!key&&cnt!=prevN)return;
 if(q.length<cnt*3){q=new Uint16Array(cnt*3);pc=new Uint16Array(cnt);pos=new Float32Array(cnt*3);col=new Float32Array(cnt*3)}
 const o={i:12},pq=q.slice(0,cnt*3);for(let i=0;i<cnt;i++){for(let a=0;a<3;a++){const k=i*3+a,z=rv(b,o),d=(z>>>1)^-(z&1),r=key?(i==0?32768:q[k-3]):pq[k];q[k]=(r+d)&65535;pos[k]=q[k]/65535*2*E-E}
 if(colr){pc[i]=b[o.i]|b[o.i+1]<<8;o.i+=2}const c=pc[i];col[i*3]=(c>>11)/31;col[i*3+1]=((c>>5)&63)/63;col[i*3+2]=(c&31)/31}n=cnt;prevN=cnt;
 gl.bindBuffer(gl.ARRAY_BUFFER,bp);gl.bufferData(gl.ARRAY_BUFFER,pos.subarray(0,n*3),gl.DYNAMIC_DRAW);gl.bindBuffer(gl.ARRAY_BUFFER,bc);gl.bufferData(gl.ARRAY_BUFFER,col.subarray(0,n*3),gl.DYNAMIC_DRAW)}
function hsl(h,l){const k=x=>(x+h*12)%12,f=x=>l-.5*Math.min(l,1-l)*Math.max(-1,Math.min(k(x)-3,9-k(x),1));return[f(0),f(8),f(4)]}
const es=new EventSource('/events');es.onopen=()=>st.textContent='live';es.onerror=()=>st.textContent='reconnecting…';
es.onmessage=e=>{const d=e.data;if(d[0]=='V'){const s=atob(d.slice(1)),b=new Uint8Array(s.length);for(let i=0;i<s.length;i++)b[i]=s.charCodeAt(i);dec(b);return}
 try{const j=JSON.parse(d);if(j.type=='stage'){stage=j;const w=j.wash||0;tint=w>0?hsl(j.hue||0,.5+w*.25).map(x=>.6+x*.6):[1,1,1]}else if(j.type=='crowd'&&j.burst){fx.textContent=j.burst;setTimeout(()=>fx.textContent='',900)}}catch(_){}};
function persp(f,a){const t=1/Math.tan(f/2);return new Float32Array([t/a,0,0,0,0,t,0,0,0,0,-1.002,-1,0,0,-.2002,0])}
function look(t){const c=Math.cos(t),s=Math.sin(t),r=3.2;return new Float32Array([c,0,s,0,0,1,0,0,-s,0,c,0,0,-1.1,-r,1])}
function draw(P,V,sz){gl.uniformMatrix4fv(L.P,false,P);gl.uniformMatrix4fv(L.V,false,V);gl.uniform1f(L.s,sz);gl.uniform3fv(L.t,tint);
 gl.bindBuffer(gl.ARRAY_BUFFER,bp);gl.enableVertexAttribArray(L.p);gl.vertexAttribPointer(L.p,3,gl.FLOAT,false,0,0);gl.bindBuffer(gl.ARRAY_BUFFER,bc);gl.enableVertexAttribArray(L.c);gl.vertexAttribPointer(L.c,3,gl.FLOAT,false,0,0);if(n)gl.drawArrays(gl.POINTS,0,n)}
function frame(t){cv.width=innerWidth;cv.height=innerHeight;gl.viewport(0,0,cv.width,cv.height);const s=stage&&stage.strobe>0.5?.25:0;gl.clearColor(.01+s,.015+s,.04+s,1);gl.clear(gl.COLOR_BUFFER_BIT|gl.DEPTH_BUFFER_BIT);gl.enable(gl.DEPTH_TEST);
 draw(persp(1,cv.width/cv.height),look(t*.0002),cv.height*.012);requestAnimationFrame(frame)}requestAnimationFrame(frame);
if(navigator.xr)navigator.xr.isSessionSupported('immersive-vr').then(ok=>{if(!ok)return;vr.hidden=false;vr.onclick=async()=>{const se=await navigator.xr.requestSession('immersive-vr');await gl.makeXRCompatible();
 se.updateRenderState({baseLayer:new XRWebGLLayer(se,gl)});const ref=await se.requestReferenceSpace('local-floor');const xf=(t,fr)=>{se.requestAnimationFrame(xf);const ps=fr.getViewerPose(ref);if(!ps)return;const ly=se.renderState.baseLayer;
 gl.bindFramebuffer(gl.FRAMEBUFFER,ly.framebuffer);gl.clearColor(0,0,0,1);gl.clear(gl.COLOR_BUFFER_BIT|gl.DEPTH_BUFFER_BIT);for(const v of ps.views){const vp=ly.getViewport(v);gl.viewport(vp.x,vp.y,vp.width,vp.height);
 const m=new DOMMatrix(Array.from(v.transform.inverse.matrix)).translate(0,0,-2.2);draw(v.projectionMatrix,m.toFloat32Array(),vp.height*.012)}};se.requestAnimationFrame(xf)}});
</script></body></html>
""";
}
