using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Media;     // IVideoMergeService
using PromptFlowStudio.Services.Spatial;   // ReadExactAsync

namespace PromptFlowStudio.Services.Virtual;

/// <summary>OSC 1.0 코덱(빅엔디언 · 4바이트 정렬 · 무할당). 숫자 인자(f/i/d/h/T/F)만 float 로 추출, 문자열/블롭은 건너뜀.</summary>
public static class OscCodec
{
    public static int Pad4(int n) => (n + 3) & ~3;

    /// <summary>메시지 파싱 — address 는 패킷 슬라이스(복사 0) · args 용량 초과 인자는 버림.</summary>
    public static bool TryParseMessage(ReadOnlySpan<byte> msg, out ReadOnlySpan<byte> address, Span<float> args, out int argc)
    {
        address = default; argc = 0;
        if (msg.Length < 8 || msg[0] != (byte)'/') return false;
        var an = msg.IndexOf((byte)0);
        if (an <= 0) return false;
        address = msg.Slice(0, an);
        var pos = Pad4(an + 1);
        if (pos >= msg.Length || msg[pos] != (byte)',') return false;
        var tagSpan = msg.Slice(pos);
        var tn = tagSpan.IndexOf((byte)0);
        if (tn <= 0) return false;
        var tags = tagSpan.Slice(1, tn - 1);
        pos += Pad4(tn + 1);
        for (var k = 0; k < tags.Length; k++)
        {
            float v; var has = true;
            switch (tags[k])
            {
                case (byte)'f': if (pos + 4 > msg.Length) return false; v = BinaryPrimitives.ReadSingleBigEndian(msg.Slice(pos)); pos += 4; break;
                case (byte)'i': if (pos + 4 > msg.Length) return false; v = BinaryPrimitives.ReadInt32BigEndian(msg.Slice(pos)); pos += 4; break;
                case (byte)'d': if (pos + 8 > msg.Length) return false; v = (float)BinaryPrimitives.ReadDoubleBigEndian(msg.Slice(pos)); pos += 8; break;
                case (byte)'h': if (pos + 8 > msg.Length) return false; v = BinaryPrimitives.ReadInt64BigEndian(msg.Slice(pos)); pos += 8; break;
                case (byte)'T': v = 1; break;
                case (byte)'F': v = 0; break;
                case (byte)'N': case (byte)'I': v = 0; has = false; break;
                case (byte)'s': case (byte)'S':
                {
                    if (pos >= msg.Length) return false;
                    var z = msg.Slice(pos).IndexOf((byte)0);
                    if (z < 0) return false;
                    pos += Pad4(z + 1); v = 0; has = false;
                    break;
                }
                case (byte)'b':
                {
                    if (pos + 4 > msg.Length) return false;
                    var size = BinaryPrimitives.ReadInt32BigEndian(msg.Slice(pos));
                    if (size < 0 || pos + 4 + size > msg.Length) return false;
                    pos += 4 + Pad4(size); v = 0; has = false;
                    break;
                }
                default: return false;
            }
            if (pos > msg.Length) return false;
            if (has && argc < args.Length) args[argc++] = v;
        }
        return true;
    }

    /// <summary>float 인자 메시지 기록 → 길이(버퍼 부족 -1).</summary>
    public static int WriteMessage(Span<byte> dst, ReadOnlySpan<byte> address, ReadOnlySpan<float> args)
    {
        var aLen = Pad4(address.Length + 1);
        var tLen = Pad4(args.Length + 2);
        var total = aLen + tLen + args.Length * 4;
        if (address.IsEmpty || address[0] != (byte)'/' || dst.Length < total) return -1;
        dst.Slice(0, total).Clear();
        address.CopyTo(dst);
        dst[aLen] = (byte)',';
        for (var i = 0; i < args.Length; i++) dst[aLen + 1 + i] = (byte)'f';
        var pos = aLen + tLen;
        for (var i = 0; i < args.Length; i++, pos += 4) BinaryPrimitives.WriteSingleBigEndian(dst.Slice(pos), args[i]);
        return total;
    }

    public static bool IsBundle(ReadOnlySpan<byte> p) => p.Length >= 16 && p.Slice(0, 8).SequenceEqual("#bundle\0"u8);
}

/// <summary>카메라 트래킹 샘플(스테이지 좌표 m · 각도 도 · FOV 도(0=미전송) · 수신 단조 ms).</summary>
public readonly record struct CameraTrackingSample(float X, float Y, float Z, float Pan, float Tilt, float Roll, float Fov, long ReceivedMs);

public sealed record StagingPackageResult(bool Ok, string? Folder, int Files, string? KeyedVideo, string Message);

/// <summary>
/// [PromptFlow Studio v16.32.0] 언리얼 엔진 5 실시간 Live Link/OSC · NDI/RTSP 스테이징 브릿지(순수 BCL 소켓 + ffmpeg).
///   ▸ 수신: UDP OSC(번들 재귀 4단) <c>/pfs/camera</c>·<c>/camera/transform</c> (x,y,z cm · pitch,yaw,roll · fov) → UE(X 전방·Y 우·Z 상) → 스테이지 좌표 링버퍼(256)
///     · 패킷 간격 EMA/지터 · 기본 루프백 바인드(원격 허용 opt-in) · SIO_UDP_CONNRESET off · 수신 예외 백오프 continue.
///   ▸ 송신: AI 카메라 궤적을 <c>/pfs/camera/set</c> 로 UE 뷰포트에 60Hz 스트리밍(PeriodicTimer · stackalloc 인코드 · 무할당).
///   ▸ 프레임: NDI(ndi://소스 · ffmpeg libndi_newtek 빌드 필요) / RTSP(TCP) / SRT / UDP / RTMP / HTTP → rawvideo 더블 버퍼(최신 프레임 · 시퀀스).
///   ▸ 헬스: ComfyUI <c>/system_stats</c> · UE Remote Control <c>/remote/info</c> (3초 타임아웃).
///   ▸ 1-Click 스테이징 패키지: UE 카메라 JSON/CSV + 크로마키 합성 영상 + manifest(SHA-256) · 합성 실패 시 [부분 구제].
/// </summary>
public sealed class UnrealEngine5LiveStagingBridge : IDisposable
{
    public const int DefaultListenPort = 8000;
    public const int DefaultUePort = 8001;
    public const int RingSize = 256;
    public const int MaxPacket = 4096;
    public const long LiveWindowMs = 2000;
    public static ReadOnlySpan<byte> CameraAddress => "/pfs/camera"u8;
    public static ReadOnlySpan<byte> CameraAliasAddress => "/camera/transform"u8;
    public static ReadOnlySpan<byte> CameraSetAddress => "/pfs/camera/set"u8;

    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(3) };

    private readonly Func<string?> _ffmpeg;
    private readonly CinematicCameraMotionControlEngine _camera;
    private readonly AiChromaKeyingAndRelightingEngine _chroma;
    private readonly HttpClient _http;
    private readonly object _gate = new();
    private readonly CameraTrackingSample[] _ring = new CameraTrackingSample[RingSize];
    private Socket? _rx, _tx;
    private CancellationTokenSource? _rxCts, _ingestCts, _playCts;
    private byte[]? _front, _back;
    private int _frameW, _frameH, _head, _count, _playing, _disposed;
    private long _packets, _messages, _unknown, _rxErrors, _sent, _frameSeq, _lastRxMs, _lastRaiseMs;
    private double _emaInterval, _emaJitter, _ingestFps, _playProgress;
    private CameraTrackingSample _latest;

    public UnrealEngine5LiveStagingBridge(IVideoMergeService merge, CinematicCameraMotionControlEngine camera, AiChromaKeyingAndRelightingEngine chroma)
        : this(() => merge.ResolvedFfmpegPath, camera, chroma, null) { }

    public UnrealEngine5LiveStagingBridge(Func<string?> ffmpeg, CinematicCameraMotionControlEngine camera, AiChromaKeyingAndRelightingEngine chroma, HttpClient? http)
    {
        _ffmpeg = ffmpeg;
        _camera = camera;
        _chroma = chroma;
        _http = http ?? s_http;
    }

    public event Action? OnChange;

    public CinematicCameraMotionControlEngine Camera => _camera;
    public AiChromaKeyingAndRelightingEngine Chroma => _chroma;
    public string ComfyUrl { get; set; } = Environment.GetEnvironmentVariable("PFS_COMFY_URL") is { Length: > 0 } c ? c : "http://127.0.0.1:8188";
    public string UeRemoteUrl { get; set; } = Environment.GetEnvironmentVariable("PFS_UE_REMOTE_URL") is { Length: > 0 } u ? u : "http://127.0.0.1:30010";
    public int ListenPort { get; private set; }
    public string? TargetEndpoint { get; private set; }
    public string? IngestSource { get; private set; }
    public bool IsListening => Volatile.Read(ref _rx) is not null;
    public bool IngestRunning => Volatile.Read(ref _ingestCts) is not null;
    public bool IsPlayingPath => Volatile.Read(ref _playing) != 0;
    public double PlayProgress => Volatile.Read(ref _playProgress);
    public bool? ComfyOnline { get; private set; }
    public bool? UeRemoteOnline { get; private set; }
    public long Packets => Interlocked.Read(ref _packets);
    public long Messages => Interlocked.Read(ref _messages);
    public long UnknownMessages => Interlocked.Read(ref _unknown);
    public long ReceiveErrors => Interlocked.Read(ref _rxErrors);
    public long SentPackets => Interlocked.Read(ref _sent);
    public long FrameSeq => Interlocked.Read(ref _frameSeq);
    public double IngestFps => Volatile.Read(ref _ingestFps);
    public double PacketIntervalMs { get { lock (_gate) return _emaInterval; } }
    public double JitterMs { get { lock (_gate) return _emaJitter; } }
    public int SampleCount { get { lock (_gate) return _count; } }
    public CameraTrackingSample Latest { get { lock (_gate) return _latest; } }
    public string? LastAction { get; private set; }

    public bool UeOscLive(long nowMs) { var l = Interlocked.Read(ref _lastRxMs); return l > 0 && nowMs - l < LiveWindowMs; }
    public bool UeOscLiveNow => UeOscLive(Environment.TickCount64);

    /// <summary>최근 샘플 n개(최신 우선) → dst 복사 · 반환 = 복사 개수(무할당).</summary>
    public int CopyRecent(Span<CameraTrackingSample> dst)
    {
        lock (_gate)
        {
            var n = Math.Min(dst.Length, _count);
            for (var i = 0; i < n; i++) dst[i] = _ring[(_head - 1 - i + RingSize * 2) % RingSize];
            return n;
        }
    }

    // ══════════════════════ OSC 수신 ══════════════════════

    /// <summary>패킷 1개 처리(번들 재귀 · 무할당). 반환 = 처리된 카메라 메시지 수.</summary>
    public int HandlePacket(ReadOnlySpan<byte> packet, long nowMs) => HandlePacket(packet, nowMs, 0, true);

    private int HandlePacket(ReadOnlySpan<byte> packet, long nowMs, int depth, bool top)
    {
        if (top) Interlocked.Increment(ref _packets);
        if (OscCodec.IsBundle(packet))
        {
            if (depth >= 4) return 0;
            var handled = 0;
            var pos = 16;
            while (pos + 4 <= packet.Length)
            {
                var size = BinaryPrimitives.ReadInt32BigEndian(packet.Slice(pos));
                pos += 4;
                if (size <= 0 || pos + size > packet.Length) break;
                handled += HandlePacket(packet.Slice(pos, size), nowMs, depth + 1, false);
                pos += size;
            }
            return handled;
        }
        Span<float> args = stackalloc float[8];
        if (!OscCodec.TryParseMessage(packet, out var addr, args, out var argc)) { Interlocked.Increment(ref _unknown); return 0; }
        Interlocked.Increment(ref _messages);
        if (!(addr.SequenceEqual(CameraAddress) || addr.SequenceEqual(CameraAliasAddress)) || argc < 6) { Interlocked.Increment(ref _unknown); return 0; }
        // UE(cm · X 전방 · Y 우 · Z 상) → 스테이지(m · x 우 · y 상 · z 전방)
        var s = new CameraTrackingSample(args[1] * 0.01f, args[2] * 0.01f, args[0] * 0.01f, args[4], args[3], args[5], argc >= 7 ? args[6] : 0f, nowMs);
        var raise = false;
        lock (_gate)
        {
            var prev = Interlocked.Read(ref _lastRxMs);
            if (prev > 0)
            {
                var dt = Math.Max(0, nowMs - prev);
                if (_emaInterval <= 0) _emaInterval = dt;
                else
                {
                    _emaJitter = _emaJitter * 0.9 + Math.Abs(dt - _emaInterval) * 0.1;
                    _emaInterval = _emaInterval * 0.9 + dt * 0.1;
                }
            }
            _ring[_head] = s;
            _head = (_head + 1) % RingSize;
            if (_count < RingSize) _count++;
            _latest = s;
            Interlocked.Exchange(ref _lastRxMs, nowMs);
            if (nowMs - _lastRaiseMs >= 100) { _lastRaiseMs = nowMs; raise = true; }
        }
        if (raise) Raise();
        return 1;
    }

    /// <summary>UDP OSC 수신 시작(기본 127.0.0.1 · allowRemote=true 시 전체 인터페이스). 멱등.</summary>
    public bool StartListening(int port = DefaultListenPort, bool allowRemote = false)
    {
        if (IsListening) return true;
        Socket? s = null;
        try
        {
            s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            if (OperatingSystem.IsWindows())
            {
                try { s.IOControl(unchecked((int)0x9800000C), new byte[4], null); } catch (Exception ex) { Program.Log("UE5.ConnReset", ex); }
            }
            s.Bind(new IPEndPoint(allowRemote ? IPAddress.Any : IPAddress.Loopback, Math.Clamp(port, 0, 65535)));
            var cts = new CancellationTokenSource();
            if (Interlocked.CompareExchange(ref _rx, s, null) is not null) { s.Dispose(); cts.Dispose(); return true; }
            Interlocked.Exchange(ref _rxCts, cts)?.Dispose();
            ListenPort = ((IPEndPoint)s.LocalEndPoint!).Port;
            _ = Task.Run(() => ReceiveLoopAsync(s, cts.Token));
            LastAction = "📡 OSC 수신 · UDP " + ListenPort.ToString(CultureInfo.InvariantCulture) + (allowRemote ? " (원격 허용)" : " (루프백)");
            Raise();
            return true;
        }
        catch (Exception ex)
        {
            s?.Dispose();
            Program.Log("UE5.Listen", ex);
            LastAction = "⚠ OSC 포트 바인드 실패: " + ex.Message;
            Raise();
            return false;
        }
    }

    public void StopListening()
    {
        var cts = Interlocked.Exchange(ref _rxCts, null);
        var s = Interlocked.Exchange(ref _rx, null);
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        s?.Dispose();
        cts?.Dispose();
        if (s is not null) { LastAction = "📡 OSC 수신 중지"; Raise(); }
    }

    private async Task ReceiveLoopAsync(Socket s, CancellationToken ct)
    {
        var buf = new byte[MaxPacket];
        var backoff = 50;
        while (!ct.IsCancellationRequested)
        {
            int n;
            try { n = await s.ReceiveAsync(buf.AsMemory(), SocketFlags.None, ct).ConfigureAwait(false); backoff = 50; }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                Interlocked.Increment(ref _rxErrors);
                Program.Log("UE5.Receive", ex);
                try { await Task.Delay(backoff, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                backoff = Math.Min(backoff * 2, 2000);
                continue;
            }
            try { HandlePacket(buf.AsSpan(0, n), Environment.TickCount64); }
            catch (Exception ex) { Program.Log("UE5.Packet", ex); }
        }
    }

    // ══════════════════════ OSC 송신(UE 뷰포트 카메라) ══════════════════════

    /// <summary>UE OSC 서버(기본 127.0.0.1:8001) 대상 설정 — IP 리터럴 또는 호스트명(IPv4).</summary>
    public bool ConfigureTarget(string host = "127.0.0.1", int port = DefaultUePort)
    {
        try
        {
            if (!IPAddress.TryParse(host, out var ip))
            {
                ip = null;
                foreach (var a in Dns.GetHostAddresses(host)) if (a.AddressFamily == AddressFamily.InterNetwork) { ip = a; break; }
                if (ip is null) return false;
            }
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(new IPEndPoint(ip, Math.Clamp(port, 1, 65535)));
            Interlocked.Exchange(ref _tx, s)?.Dispose();
            TargetEndpoint = ip + ":" + port.ToString(CultureInfo.InvariantCulture);
            LastAction = "🎯 UE OSC 대상 · " + TargetEndpoint;
            Raise();
            return true;
        }
        catch (Exception ex) { Program.Log("UE5.Target", ex); LastAction = "⚠ UE 대상 설정 실패: " + ex.Message; Raise(); return false; }
    }

    /// <summary>카메라 키 1개 → <c>/pfs/camera/set</c>(x,y,z cm · pitch,yaw,roll · focal mm · focus cm). 무할당.</summary>
    public bool SendCamera(in CameraKeyframe k)
    {
        var s = Volatile.Read(ref _tx);
        if (s is null) return false;
        Span<byte> pkt = stackalloc byte[96];
        var len = EncodeCamera(k, pkt);
        try
        {
            s.Send(pkt.Slice(0, len));
            Interlocked.Increment(ref _sent);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { Interlocked.Increment(ref _rxErrors); return false; }
    }

    public static int EncodeCamera(in CameraKeyframe k, Span<byte> dst)
    {
        var (ux, uy, uz) = CinematicCameraMotionControlEngine.StageToUnreal(k.X, k.Y, k.Z);
        Span<float> a = stackalloc float[8];
        a[0] = ux; a[1] = uy; a[2] = uz; a[3] = k.Tilt; a[4] = k.Pan; a[5] = k.Roll; a[6] = k.FocalMm; a[7] = k.FocusM * 100f;
        return OscCodec.WriteMessage(dst, CameraSetAddress, a);
    }

    /// <summary>카메라 궤적을 fps 로 실시간 스트리밍(동시 1개 · 취소 시 즉시 종료). 반환 = 전송 프레임 수(대상 없음 -1).</summary>
    public async Task<int> PlayCameraPathAsync(CameraRigPlan plan, double fps = 60, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _tx) is null) return -1;
        if (Interlocked.Exchange(ref _playing, 1) == 1) return 0;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Interlocked.Exchange(ref _playCts, cts)?.Dispose();
        var frames = 0;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / Math.Clamp(fps, 1, 240)));
            var sw = Stopwatch.StartNew();
            LastAction = "▶ UE 카메라 스트리밍 · " + CinematicCameraMotionControlEngine.Label(plan.Preset);
            Raise();
            while (true)
            {
                var t = sw.Elapsed.TotalSeconds;
                SendCamera(CinematicCameraMotionControlEngine.Sample(plan.Keyframes, Math.Min(t, plan.DurationSec)));
                frames++;
                Volatile.Write(ref _playProgress, Math.Min(1, t / plan.DurationSec));
                if (t >= plan.DurationSec) break;
                if (!await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false)) break;
            }
            LastAction = "■ UE 카메라 스트리밍 완료 · " + frames.ToString(CultureInfo.InvariantCulture) + "프레임";
            return frames;
        }
        catch (OperationCanceledException) { LastAction = "■ UE 카메라 스트리밍 중단"; return frames; }
        finally
        {
            if (Interlocked.CompareExchange(ref _playCts, null, cts) == cts) cts.Dispose();
            Volatile.Write(ref _playing, 0);
            Raise();
        }
    }

    public void StopCameraPath() { try { Volatile.Read(ref _playCts)?.Cancel(); } catch (ObjectDisposedException) { } }

    // ══════════════════════ NDI/RTSP 프레임 수신 ══════════════════════

    /// <summary>소스 → ffmpeg 입력 인자(허용: ndi:// rtsp:// rtsps:// srt:// udp:// rtmp(s):// http(s):// · 로컬 파일). 거부 시 null.</summary>
    public static List<string>? BuildIngestArgs(string? source, int w, int h, double fps)
    {
        var src = source?.Trim();
        if (string.IsNullOrEmpty(src) || src[0] == '-' || src.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) return null;
        var ci = CultureInfo.InvariantCulture;
        var a = new List<string> { "-hide_banner", "-v", "error", "-fflags", "nobuffer", "-flags", "low_delay" };
        if (src.StartsWith("ndi://", StringComparison.OrdinalIgnoreCase))
        {
            var name = src.Substring(6);
            if (name.Length == 0) return null;
            a.AddRange(new[] { "-f", "libndi_newtek", "-i", name });
        }
        else if (Uri.TryCreate(src, UriKind.Absolute, out var u) && !u.IsFile)
        {
            switch (u.Scheme.ToLowerInvariant())
            {
                case "rtsp": case "rtsps": a.AddRange(new[] { "-rtsp_transport", "tcp", "-i", src }); break;
                case "srt": case "udp": case "rtmp": case "rtmps": case "http": case "https": a.AddRange(new[] { "-i", src }); break;
                default: return null;
            }
        }
        else if (File.Exists(src)) a.AddRange(new[] { "-re", "-stream_loop", "-1", "-i", src });
        else return null;
        a.AddRange(new[] { "-an", "-vf", "fps=" + fps.ToString("0.###", ci) + ",scale=" + w.ToString(ci) + ":" + h.ToString(ci) + ":force_original_aspect_ratio=decrease,pad=" + w.ToString(ci) + ":" + h.ToString(ci) + ":(ow-iw)/2:(oh-ih)/2",
            "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1" });
        return a;
    }

    /// <summary>실시간 프레임 수신 시작(16:9 · 더블 버퍼). ffmpeg 부재/소스 거부 false.</summary>
    public bool StartIngest(string source, int width = 640, double fps = 30)
    {
        var ffmpeg = _ffmpeg();
        var w = Math.Clamp(width, 64, 1920) & ~1;
        var h = (w * 9 / 16) & ~1;
        var args = BuildIngestArgs(source, w, h, fps);
        if (ffmpeg is null || args is null) { LastAction = ffmpeg is null ? "⚠ ffmpeg 없음 — 프레임 수신 불가" : "⚠ 허용되지 않은 소스"; Raise(); return false; }
        StopIngest();
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _ingestCts, cts, null) is not null) { cts.Dispose(); return false; }
        lock (_gate) { _front = new byte[w * h * 3]; _back = new byte[w * h * 3]; _frameW = w; _frameH = h; }
        IngestSource = source.Trim();
        _ = Task.Run(() => IngestLoopAsync(ffmpeg, args, w, h, cts));
        LastAction = "📺 프레임 수신 · " + IngestSource;
        Raise();
        return true;
    }

    public void StopIngest()
    {
        var cts = Interlocked.Exchange(ref _ingestCts, null);
        if (cts is null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async Task IngestLoopAsync(string ffmpeg, List<string> args, int w, int h, CancellationTokenSource cts)
    {
        var psi = new ProcessStartInfo { FileName = ffmpeg, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var n = w * h * 3;
        try
        {
            using var p = new Process { StartInfo = psi };
            if (!p.Start()) return;
            _ = p.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                var stream = p.StandardOutput.BaseStream;
                var sw = Stopwatch.StartNew();
                var window = 0;
                while (!cts.IsCancellationRequested)
                {
                    byte[] back;
                    lock (_gate) back = _back!;
                    if (!await Spatial3dGaussianSplattingEngine.ReadExactAsync(stream, back, n, cts.Token).ConfigureAwait(false)) break;
                    lock (_gate) { (_front, _back) = (_back, _front); }
                    Interlocked.Increment(ref _frameSeq);
                    window++;
                    if (sw.ElapsedMilliseconds >= 1000)
                    {
                        Volatile.Write(ref _ingestFps, window * 1000.0 / sw.ElapsedMilliseconds);
                        window = 0; sw.Restart();
                        Raise();
                    }
                }
            }
            finally { try { if (!p.HasExited) p.Kill(true); } catch { } }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Program.Log("UE5.Ingest", ex); LastAction = "⚠ 프레임 수신 실패: " + ex.Message; }
        finally
        {
            Interlocked.CompareExchange(ref _ingestCts, null, cts);
            cts.Dispose();
            Volatile.Write(ref _ingestFps, 0);
            Raise();
        }
    }

    /// <summary>최신 프레임 복사(dst ≥ w·h·3) → 시퀀스(프레임 없음 0). 무할당.</summary>
    public long TryCopyLatestFrame(Span<byte> dst, out int w, out int h)
    {
        lock (_gate)
        {
            w = _frameW; h = _frameH;
            var n = w * h * 3;
            if (_front is null || n == 0 || dst.Length < n || Interlocked.Read(ref _frameSeq) == 0) return 0;
            _front.AsSpan(0, n).CopyTo(dst);
            return Interlocked.Read(ref _frameSeq);
        }
    }

    // ══════════════════════ 헬스 프로브 ══════════════════════

    public static bool IsHttpUrl(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    public async Task<bool> ProbeComfyAsync(CancellationToken ct = default)
    {
        ComfyOnline = await ProbeAsync(ComfyUrl, "/system_stats", "system", ct).ConfigureAwait(false);
        Raise();
        return ComfyOnline == true;
    }

    public async Task<bool> ProbeUnrealRemoteAsync(CancellationToken ct = default)
    {
        UeRemoteOnline = await ProbeAsync(UeRemoteUrl, "/remote/info", null, ct).ConfigureAwait(false);
        Raise();
        return UeRemoteOnline == true;
    }

    private async Task<bool> ProbeAsync(string baseUrl, string path, string? mustContain, CancellationToken ct)
    {
        if (!IsHttpUrl(baseUrl)) return false;
        try
        {
            using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
            d.CancelAfter(TimeSpan.FromSeconds(3));
            using var res = await _http.GetAsync(baseUrl.TrimEnd('/') + path, d.Token).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return false;
            if (mustContain is null) return true;
            var body = await res.Content.ReadAsStringAsync(d.Token).ConfigureAwait(false);
            return body.Contains(mustContain, StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
    }

    // ══════════════════════ 1-Click 라이브 스테이징 · 패키지 ══════════════════════

    /// <summary>OSC 수신 + UE 대상 + (선택) 프레임 수신 + 카메라 궤적 스트리밍을 한 번에.</summary>
    public async Task<string> StartLiveStagingAsync(CameraRigPlan plan, string ueHost, int uePort, string? ingestSource, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.Append(StartListening(ListenPort > 0 ? ListenPort : DefaultListenPort) ? "OSC 수신 ✓" : "OSC 수신 ✗");
        if (!ConfigureTarget(ueHost, uePort)) return sb.Append(" · UE 대상 ✗").ToString();
        if (!string.IsNullOrWhiteSpace(ingestSource)) sb.Append(StartIngest(ingestSource) ? " · 프레임 ✓" : " · 프레임 ✗");
        var frames = await PlayCameraPathAsync(plan, 60, ct).ConfigureAwait(false);
        sb.Append(" · 카메라 ").Append(frames.ToString(CultureInfo.InvariantCulture)).Append("프레임 전송");
        LastAction = "🎬 " + sb;
        Raise();
        return sb.ToString();
    }

    /// <summary>{folder}/{name}_vp/ — camera_path.json · camera_path.csv · (전경+배경 시) keyed.mp4 · manifest.json(SHA-256).</summary>
    public async Task<StagingPackageResult> ExportStagingPackageAsync(string folder, string name, CameraRigPlan plan, string? fgPath, string? bgPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folder)) return new(false, null, 0, null, "출력 폴더를 지정하세요.");
        var dir = Path.Combine(folder, MvHevcSpatialVideoTranscoder.SafeName(name) + "_vp");
        try
        {
            Directory.CreateDirectory(dir);
            var files = new List<string>();
            var utf8 = new UTF8Encoding(false);
            var json = Path.Combine(dir, "camera_path.json");
            await File.WriteAllTextAsync(json, CinematicCameraMotionControlEngine.ToUnrealJson(plan), utf8, ct).ConfigureAwait(false);
            files.Add(json);
            var csv = Path.Combine(dir, "camera_path.csv");
            await File.WriteAllTextAsync(csv, CinematicCameraMotionControlEngine.ToUnrealCsv(plan, 24), utf8, ct).ConfigureAwait(false);
            files.Add(csv);
            string? keyed = null; var note = "";
            if (!string.IsNullOrWhiteSpace(fgPath) && !string.IsNullOrWhiteSpace(bgPath))
            {
                var r = await _chroma.KeyVideoAsync(fgPath, bgPath, Path.Combine(dir, "keyed.mp4"), 1280, 30, ct).ConfigureAwait(false);
                if (r.Ok && r.OutputPath is not null) { keyed = r.OutputPath; files.Add(keyed); }
                else note = "[부분 구제] 크로마키 합성 실패(" + r.Message + ") — 카메라 데이터는 보존. ";
            }
            var entries = new List<Dictionary<string, object>>(files.Count);
            foreach (var f in files)
            {
                await using var fs = File.OpenRead(f);
                var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
                entries.Add(new() { ["name"] = Path.GetFileName(f), ["bytes"] = fs.Length, ["sha256"] = Convert.ToHexString(hash).ToLowerInvariant() });
            }
            var ks = _chroma.Settings;
            var manifest = new Dictionary<string, object>
            {
                ["generator"] = "PromptFlow Studio", ["version"] = "16.32.0", ["preset"] = plan.Preset.ToString(), ["rationale"] = plan.Rationale,
                ["ueTarget"] = TargetEndpoint ?? "", ["oscListenPort"] = ListenPort, ["oscCameraAddress"] = "/pfs/camera", ["oscCameraSetAddress"] = "/pfs/camera/set",
                ["comfyUrl"] = ComfyUrl, ["keying"] = new Dictionary<string, object> { ["similarity"] = ks.Similarity, ["smoothness"] = ks.Smoothness, ["spill"] = ks.Spill, ["relight"] = ks.Relight, ["lightWrap"] = ks.LightWrap },
                ["createdUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), ["files"] = entries,
            };
            await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), utf8, ct).ConfigureAwait(false);
            LastAction = "📦 버추얼 스테이징 패키지 · 파일 " + (files.Count + 1).ToString(CultureInfo.InvariantCulture);
            Raise();
            return new(true, dir, files.Count + 1, keyed, (note + "패키지 완료").Trim());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new(false, dir, 0, null, "취소됨"); }
        catch (Exception ex) { Program.Log("UE5.Package", ex); return new(false, dir, 0, null, "패키지 실패: " + ex.Message); }
    }

    private void Raise()
    {
        var h = OnChange;
        if (h is null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action)d)(); } catch (Exception ex) { Program.Log("UE5.OnChange", ex); }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        StopListening();
        StopIngest();
        StopCameraPath();
        Interlocked.Exchange(ref _tx, null)?.Dispose();
    }
}
