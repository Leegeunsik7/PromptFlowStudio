using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Runtime.InteropServices;

namespace PromptFlowStudio.Services.Hardware;

/// <summary>타일 렌더 작업(프레임 공통 시각 · 프레임/타일 사각형).</summary>
public readonly record struct RenderTileJob(long FrameSeq, double Time, int FrameWidth, int FrameHeight, int TileIndex, int X0, int Y0, int W, int H);

/// <summary>타일 렌더러 — dst 는 타일 원점(행 간격 stride 바이트 · RGB24). 동일 입력 → 동일 픽셀(결정적 · 스레드 안전) 계약.</summary>
public interface ITileRenderer
{
    string Name { get; }
    void Render(in RenderTileJob job, Span<byte> dst, int stride);
}

/// <summary>
/// 내장 레이트레이스 스튜디오 장면(구 3 · 체커 바닥 · 그림자 · 반사 1회 · 서브샘플 Quality²) — 픽셀 독립 결정적(타일 분할과 무관하게 비트 동일).
/// </summary>
public sealed class RayTracedStudioScene : ITileRenderer
{
    public string Name => "RT 스튜디오";
    public int Quality { get; init; } = 1;
    /// <summary>인위 비용 배율(노드 이질성 시뮬 · 1 = 정상).</summary>
    public int CostMultiplier { get; init; } = 1;

    public void Render(in RenderTileJob job, Span<byte> dst, int stride)
    {
        var t = (float)job.Time;
        var az = t * 0.35f;
        var eye = new Vector3(MathF.Sin(az) * 6f, 2.2f, MathF.Cos(az) * 6f);
        var fwd = Vector3.Normalize(new Vector3(0, 0.6f, 0) - eye);
        var right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitY));
        var up = Vector3.Cross(right, fwd);
        var f = 1f / MathF.Tan(0.5f * 55f * MathF.PI / 180f);
        var aspect = job.FrameWidth / (float)job.FrameHeight;
        Span<Vector4> sph = stackalloc Vector4[3];
        Span<Vector3> col = stackalloc Vector3[3];
        sph[0] = new Vector4(MathF.Sin(t) * 1.6f, 0.8f + MathF.Abs(MathF.Sin(t * 2.1f)) * 0.8f, 0, 0.8f); col[0] = new Vector3(0.95f, 0.3f, 0.25f);
        sph[1] = new Vector4(-1.9f, 0.6f, MathF.Cos(t * 0.7f) * 1.4f, 0.6f); col[1] = new Vector3(0.25f, 0.7f, 0.95f);
        sph[2] = new Vector4(1.8f, 0.5f, -1.2f + MathF.Sin(t * 1.3f) * 0.6f, 0.5f); col[2] = new Vector3(0.95f, 0.85f, 0.3f);
        var light = Vector3.Normalize(new Vector3(0.6f, 1f, 0.4f));
        var q = Math.Clamp(Quality, 1, 4);
        var inv = 1f / (q * q);
        var reps = Math.Clamp(CostMultiplier, 1, 64);
        for (var y = 0; y < job.H; y++)
        {
            var row = dst.Slice(y * stride, job.W * 3);
            for (var x = 0; x < job.W; x++)
            {
                var acc = Vector3.Zero;
                for (var r = 0; r < reps; r++)
                {
                    acc = Vector3.Zero;
                    for (var sy = 0; sy < q; sy++)
                        for (var sx = 0; sx < q; sx++)
                        {
                            var u = ((job.X0 + x + (sx + 0.5f) / q) / job.FrameWidth * 2f - 1f) * aspect;
                            var v = 1f - (job.Y0 + y + (sy + 0.5f) / q) / job.FrameHeight * 2f;
                            var dir = Vector3.Normalize(fwd * f + right * u + up * v);
                            acc += Trace(eye, dir, sph, col, light, 1);
                        }
                }
                acc *= inv;
                row[x * 3] = (byte)Math.Clamp((int)(MathF.Sqrt(acc.X) * 255f + 0.5f), 0, 255);
                row[x * 3 + 1] = (byte)Math.Clamp((int)(MathF.Sqrt(acc.Y) * 255f + 0.5f), 0, 255);
                row[x * 3 + 2] = (byte)Math.Clamp((int)(MathF.Sqrt(acc.Z) * 255f + 0.5f), 0, 255);
            }
        }
    }

    private static Vector3 Trace(Vector3 o, Vector3 d, ReadOnlySpan<Vector4> sph, ReadOnlySpan<Vector3> col, Vector3 light, int depth)
    {
        var best = float.MaxValue; var hit = -1;
        for (var i = 0; i < sph.Length; i++)
        {
            var t = HitSphere(o, d, sph[i]);
            if (t > 1e-3f && t < best) { best = t; hit = i; }
        }
        var tp = d.Y < -1e-4f ? -o.Y / d.Y : float.MaxValue;
        if (tp > 1e-3f && tp < best) { best = tp; hit = 3; }
        if (hit < 0)
        {
            var k = MathF.Max(0f, d.Y);
            return Vector3.Lerp(new Vector3(0.18f, 0.2f, 0.26f), new Vector3(0.05f, 0.09f, 0.22f), k);
        }
        var p = o + d * best;
        Vector3 n, albedo;
        if (hit == 3)
        {
            n = Vector3.UnitY;
            var c = ((int)MathF.Floor(p.X) + (int)MathF.Floor(p.Z)) & 1;
            albedo = c == 0 ? new Vector3(0.75f) : new Vector3(0.22f);
            var fade = MathF.Exp(-0.02f * (p.X * p.X + p.Z * p.Z));
            albedo *= fade;
        }
        else
        {
            var s = sph[hit];
            n = Vector3.Normalize(p - new Vector3(s.X, s.Y, s.Z));
            albedo = col[hit];
        }
        var ndl = MathF.Max(0f, Vector3.Dot(n, light));
        if (ndl > 0f)
            for (var i = 0; i < sph.Length; i++)
                if (i != hit && HitSphere(p + n * 1e-3f, light, sph[i]) > 0f) { ndl *= 0.25f; break; }
        var c0 = albedo * (0.12f + 0.88f * ndl);
        if (depth > 0 && hit != 3)
        {
            var rd = d - 2f * Vector3.Dot(d, n) * n;
            c0 = c0 * 0.8f + Trace(p + n * 1e-3f, rd, sph, col, light, depth - 1) * 0.2f;
        }
        return c0;
    }

    private static float HitSphere(Vector3 o, Vector3 d, Vector4 s)
    {
        var oc = o - new Vector3(s.X, s.Y, s.Z);
        var b = Vector3.Dot(oc, d);
        var c = oc.LengthSquared() - s.W * s.W;
        var disc = b * b - c;
        if (disc < 0f) return -1f;
        var sq = MathF.Sqrt(disc);
        var t = -b - sq;
        return t > 1e-3f ? t : -b + sq;
    }
}

public enum RenderNodeKind : byte { Local, Remote }
internal enum TileOutcome : byte { Committed, Lost, Failed }

/// <summary>노드 상태 스냅샷(UI).</summary>
public readonly record struct RenderNodeStatus(int Id, string Name, RenderNodeKind Kind, bool Online, long Tiles, long Stolen, double TileMicrosEwma, double LastTileMicros, float Share, string? Endpoint);

/// <summary>프레임 조립 결과.</summary>
public readonly record struct FarmFrameResult(long FrameSeq, bool Complete, int Tiles, int Reissued, double LatencyMs, double MaxTileMs, int NodesUsed);

/// <summary>
/// 원격 렌더 노드 서버(RFT1/TCP) — 요청(48B) → 타일 렌더 → 응답(28B + RGB). 클라이언트별 전용 스레드 · 동기 소켓(무할당).
/// </summary>
public sealed class RenderNodeServer : IDisposable
{
    public const uint Magic = 0x31544652; // "RFT1"
    public const int RequestBytes = 48, ResponseHeader = 28, MaxTilePixels = 512 * 512;
    private readonly Socket _listen;
    private readonly ITileRenderer _renderer;
    private readonly List<Socket> _clients = new();
    private readonly Thread _accept;
    private volatile bool _stop;
    private long _served;

    public RenderNodeServer(ITileRenderer renderer, int port = 0, IPAddress? bind = null)
    {
        _renderer = renderer;
        _listen = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listen.Bind(new IPEndPoint(bind ?? IPAddress.Loopback, port));
        _listen.Listen(8);
        Port = ((IPEndPoint)_listen.LocalEndPoint!).Port;
        _accept = new Thread(AcceptLoop) { IsBackground = true, Name = "RFT1.Accept" };
        _accept.Start();
    }

    public int Port { get; }
    public long Served => Interlocked.Read(ref _served);
    /// <summary>타일당 인위 지연(ms · 스트래글러 시뮬).</summary>
    public int ExtraDelayMs { get; set; }
    /// <summary>true = 응답 중단(장애 시뮬 · 연결 유지).</summary>
    public bool Stall { get; set; }

    private void AcceptLoop()
    {
        while (!_stop)
        {
            Socket c;
            try { c = _listen.Accept(); }
            catch (SocketException) { if (_stop) return; continue; }
            catch (ObjectDisposedException) { return; }
            c.NoDelay = true;
            lock (_clients) _clients.Add(c);
            new Thread(() => Serve(c)) { IsBackground = true, Name = "RFT1.Serve" }.Start();
        }
    }

    private void Serve(Socket c)
    {
        var req = new byte[RequestBytes];
        var buf = new byte[ResponseHeader + MaxTilePixels * 3];
        try
        {
            while (!_stop)
            {
                if (!RemoteRenderNode.ReceiveExact(c, req)) return;
                if (BinaryPrimitives.ReadUInt32LittleEndian(req) != Magic) return;
                var seq = BinaryPrimitives.ReadInt64LittleEndian(req.AsSpan(4));
                var tile = BinaryPrimitives.ReadInt32LittleEndian(req.AsSpan(12));
                int x0 = BinaryPrimitives.ReadInt32LittleEndian(req.AsSpan(16)), y0 = BinaryPrimitives.ReadInt32LittleEndian(req.AsSpan(20));
                int w = BinaryPrimitives.ReadInt32LittleEndian(req.AsSpan(24)), h = BinaryPrimitives.ReadInt32LittleEndian(req.AsSpan(28));
                int fw = BinaryPrimitives.ReadInt32LittleEndian(req.AsSpan(32)), fh = BinaryPrimitives.ReadInt32LittleEndian(req.AsSpan(36));
                var time = BinaryPrimitives.ReadDoubleLittleEndian(req.AsSpan(40));
                if (w <= 0 || h <= 0 || w * h > MaxTilePixels) return;
                var t0 = Stopwatch.GetTimestamp();
                _renderer.Render(new RenderTileJob(seq, time, fw, fh, tile, x0, y0, w, h), buf.AsSpan(ResponseHeader, w * h * 3), w * 3);
                if (ExtraDelayMs > 0) Thread.Sleep(ExtraDelayMs);
                while (Stall && !_stop) Thread.Sleep(5);
                var us = (int)Math.Min(int.MaxValue, (Stopwatch.GetTimestamp() - t0) * 1_000_000 / Stopwatch.Frequency);
                BinaryPrimitives.WriteUInt32LittleEndian(buf, Magic);
                BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(4), seq);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(12), tile);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(16), w);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(20), h);
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(24), us);
                var total = ResponseHeader + w * h * 3;
                var sent = 0;
                while (sent < total) sent += c.Send(buf.AsSpan(sent, total - sent));
                Interlocked.Increment(ref _served);
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
        finally { try { c.Dispose(); } catch (ObjectDisposedException) { } }
    }

    public void Dispose()
    {
        _stop = true;
        try { _listen.Dispose(); } catch (ObjectDisposedException) { }
        lock (_clients) { foreach (var c in _clients) try { c.Dispose(); } catch (ObjectDisposedException) { } _clients.Clear(); }
    }
}

/// <summary>렌더 노드 기반(전용 워커 스레드 · 타일 풀링).</summary>
public abstract class RenderNode : IDisposable
{
    private double _ewma, _last;
    internal long TilesDone, Stolen;
    internal volatile bool Online = true;
    internal Thread? Worker;
    internal readonly SemaphoreSlim Go = new(0, int.MaxValue);
    internal volatile bool Quit;

    protected RenderNode(string name) => Name = name;

    public int Id { get; internal set; }
    public string Name { get; }
    public abstract RenderNodeKind Kind { get; }
    public virtual string? Endpoint => null;
    public bool IsOnline => Online;
    public double TileMicrosEwma => Volatile.Read(ref _ewma);
    public double LastTileMicros => Volatile.Read(ref _last);

    internal void Observe(double us)
    {
        Volatile.Write(ref _last, us);
        Volatile.Write(ref _ewma, _ewma <= 0 ? us : _ewma * 0.8 + us * 0.2);
    }

    /// <summary>타일 렌더 → 프레임 기록 · 완료 CAS. Failed = 노드 오프라인 처리.</summary>
    internal abstract TileOutcome RenderTile(in RenderTileJob job, DistributedRenderFarmSyncEngine farm);

    public virtual void Dispose() { Quit = true; Go.Release(); Go.Dispose(); }
}

/// <summary>로컬 노드 — 프레임 버퍼(DirectPath 슬롯)에 제자리 렌더(복사 0).</summary>
public sealed class LocalRenderNode : RenderNode
{
    private readonly ITileRenderer _r;
    public LocalRenderNode(string name, ITileRenderer renderer) : base(name) => _r = renderer;
    public override RenderNodeKind Kind => RenderNodeKind.Local;
    internal override TileOutcome RenderTile(in RenderTileJob job, DistributedRenderFarmSyncEngine farm)
    {
        if (!farm.BeginWrite(job.FrameSeq)) return TileOutcome.Lost;
        try
        {
            _r.Render(job, farm.TileTarget(job), farm.Stride);
            return farm.TryComplete(job.TileIndex, Id) ? TileOutcome.Committed : TileOutcome.Lost;
        }
        finally { farm.EndWrite(); }
    }
}

/// <summary>원격 노드 — RFT1 TCP(동기 · 수신 타임아웃) → 승리 시 프레임에 행 단위 기록.</summary>
public sealed class RemoteRenderNode : RenderNode
{
    private readonly IPEndPoint _ep;
    private readonly byte[] _req = new byte[RenderNodeServer.RequestBytes];
    private readonly byte[] _resp = new byte[RenderNodeServer.ResponseHeader + RenderNodeServer.MaxTilePixels * 3];
    private Socket? _sock;

    public RemoteRenderNode(string name, IPEndPoint endpoint, int timeoutMs = 400) : base(name) { _ep = endpoint; TimeoutMs = timeoutMs; }

    public override RenderNodeKind Kind => RenderNodeKind.Remote;
    public override string? Endpoint => _ep.ToString();
    public int TimeoutMs { get; }

    internal static bool ReceiveExact(Socket s, Span<byte> dst)
    {
        var got = 0;
        while (got < dst.Length)
        {
            var n = s.Receive(dst[got..]);
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }

    internal override TileOutcome RenderTile(in RenderTileJob job, DistributedRenderFarmSyncEngine farm)
    {
        try
        {
            if (_sock is null)
            {
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true, ReceiveTimeout = TimeoutMs, SendTimeout = TimeoutMs };
                s.Connect(_ep);
                _sock = s;
            }
            var r = _req.AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(r, RenderNodeServer.Magic);
            BinaryPrimitives.WriteInt64LittleEndian(r[4..], job.FrameSeq);
            BinaryPrimitives.WriteInt32LittleEndian(r[12..], job.TileIndex);
            BinaryPrimitives.WriteInt32LittleEndian(r[16..], job.X0); BinaryPrimitives.WriteInt32LittleEndian(r[20..], job.Y0);
            BinaryPrimitives.WriteInt32LittleEndian(r[24..], job.W); BinaryPrimitives.WriteInt32LittleEndian(r[28..], job.H);
            BinaryPrimitives.WriteInt32LittleEndian(r[32..], job.FrameWidth); BinaryPrimitives.WriteInt32LittleEndian(r[36..], job.FrameHeight);
            BinaryPrimitives.WriteDoubleLittleEndian(r[40..], job.Time);
            var sent = 0;
            while (sent < r.Length) sent += _sock.Send(r[sent..]);
            var len = RenderNodeServer.ResponseHeader + job.W * job.H * 3;
            if (!ReceiveExact(_sock, _resp.AsSpan(0, len))) throw new SocketException((int)SocketError.ConnectionReset);
            if (BinaryPrimitives.ReadUInt32LittleEndian(_resp) != RenderNodeServer.Magic || BinaryPrimitives.ReadInt64LittleEndian(_resp.AsSpan(4)) != job.FrameSeq
                || BinaryPrimitives.ReadInt32LittleEndian(_resp.AsSpan(12)) != job.TileIndex) throw new SocketException((int)SocketError.ProtocolType);
            if (!farm.BeginWrite(job.FrameSeq)) return TileOutcome.Lost;   // 지난 프레임 응답 폐기
            try
            {
                if (farm.IsDone(job.TileIndex)) return TileOutcome.Lost;
                var dst = farm.TileTarget(job);
                var rowBytes = job.W * 3;
                for (var y = 0; y < job.H; y++) _resp.AsSpan(RenderNodeServer.ResponseHeader + y * rowBytes, rowBytes).CopyTo(dst.Slice(y * farm.Stride, rowBytes));
                return farm.TryComplete(job.TileIndex, Id) ? TileOutcome.Committed : TileOutcome.Lost;
            }
            finally { farm.EndWrite(); }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            try { _sock?.Dispose(); } catch (ObjectDisposedException) { }
            _sock = null;
            return TileOutcome.Failed;
        }
    }

    /// <summary>재접속 허용(오프라인 → 온라인 복귀 시도).</summary>
    public void Revive() { Online = true; }

    public override void Dispose()
    {
        base.Dispose();
        try { _sock?.Dispose(); } catch (ObjectDisposedException) { }
    }
}

/// <summary>
/// [PromptFlow Studio v16.63.0] 🛰 초저지연 분산 렌더 노드 팜 동기화 엔진.
///   ▸ 프레임 → 타일 그리드(TilesX×TilesY ≤ 256) · 전 노드 동일 프레임 시각(시간 일관) · 동적 풀링(빠른 노드가 더 많이 가져감 = 자동 부하 분산).
///   ▸ 스트래글러 대응: 대기 타일 소진 후 유휴 노드가 클레임 경과 &gt; max(2×EWMA, 하한) 타일을 투기 재발행 — 최초 완료 CAS 승자만 커밋(중복 무해 · 결정적 렌더).
///   ▸ 장애: 원격 노드 송수신 실패/타임아웃 → 오프라인 · 해당 타일 즉시 대기열 복귀. 데드라인 초과 시 미완 타일 수 보고(부분 프레임 · 쓰기 펜스 후 반환).
///   ▸ 조립: 로컬 노드는 대상 프레임(DirectPath 공유 슬롯)에 제자리 렌더 · 원격은 승리 시 행 복사 1회. 타일 소유 맵 · 노드별 몫/지연 텔레메트리.
/// </summary>
public sealed unsafe class DistributedRenderFarmSyncEngine : IDisposable
{
    public const int MaxNodes = 16, MaxTiles = 256;
    private const int Pending = 0, Claimed = 1, Done = 2;

    private readonly object _gate = new();
    private readonly RenderNode?[] _nodes = new RenderNode?[MaxNodes];
    private readonly int[] _state = new int[MaxTiles];
    private readonly int[] _owner = new int[MaxTiles], _claimer = new int[MaxTiles];
    private readonly long[] _claimAt = new long[MaxTiles];
    private readonly int[] _dup = new int[MaxTiles];
    private readonly long[] _nodeTilesFrame = new long[MaxNodes];
    private readonly ManualResetEventSlim _done = new(false);
    private readonly List<RenderNodeServer> _servers = new();
    private int _nodeCount, _tiles, _remaining, _reissued, _writers, _abort, _nextId;
    private int _fw, _fh, _tw, _th;
    private byte* _frame;
    private long _frameSeq, _frames, _incomplete, _totalReissued;
    private double _time, _lastLatency, _ewmaLatency, _maxTileMs;
    private volatile bool _active;

    public DistributedRenderFarmSyncEngine() { }

    public event Action? OnChange;

    public int TilesX { get; private set; } = 8;
    public int TilesY { get; private set; } = 6;
    public int Stride { get; private set; }
    public int NodeCount => _nodeCount;
    public long Frames => Interlocked.Read(ref _frames);
    public long IncompleteFrames => Interlocked.Read(ref _incomplete);
    public long TotalReissued => Interlocked.Read(ref _totalReissued);
    public double LastLatencyMs => Volatile.Read(ref _lastLatency);
    public double LatencyEwmaMs => Volatile.Read(ref _ewmaLatency);
    public double LastMaxTileMs => Volatile.Read(ref _maxTileMs);
    /// <summary>투기 재발행 하한(ms).</summary>
    public double StragglerFloorMs { get; set; } = 4;
    public FarmFrameResult Last { get; private set; }
    public string? LastAction { get; private set; }
    public int TileCount => TilesX * TilesY;
    /// <summary>타일 소유 노드 Id(-1 = 미완) — 마지막 프레임.</summary>
    public int OwnerOf(int tile) => (uint)tile < (uint)MaxTiles ? Volatile.Read(ref _owner[tile]) : -1;

    public void SetGrid(int tx, int ty)
    {
        lock (_gate) { TilesX = Math.Clamp(tx, 1, 16); TilesY = Math.Clamp(ty, 1, 16); }
        Raise();
    }

    public RenderNodeStatus[] Snapshot()
    {
        lock (_gate)
        {
            var arr = new RenderNodeStatus[_nodeCount];
            long total = 0;
            for (var i = 0; i < _nodeCount; i++) total += _nodeTilesFrame[i];
            for (var i = 0; i < _nodeCount; i++)
            {
                var n = _nodes[i]!;
                arr[i] = new RenderNodeStatus(n.Id, n.Name, n.Kind, n.Online, Interlocked.Read(ref n.TilesDone), Interlocked.Read(ref n.Stolen), n.TileMicrosEwma, n.LastTileMicros,
                    total > 0 ? _nodeTilesFrame[i] / (float)total : 0f, n.Endpoint);
            }
            return arr;
        }
    }

    public RenderNode AddLocal(ITileRenderer renderer, string? name = null)
    {
        var n = new LocalRenderNode(name ?? "로컬-" + (_nextId + 1), renderer);
        Attach(n);
        return n;
    }

    public RemoteRenderNode AddRemote(IPEndPoint ep, string? name = null, int timeoutMs = 400)
    {
        var n = new RemoteRenderNode(name ?? "원격-" + ep, ep, timeoutMs);
        Attach(n);
        return n;
    }

    /// <summary>인-프로세스 루프백 원격 노드(RFT1 서버 기동 + 연결) — 데모/테스트.</summary>
    public (RemoteRenderNode Node, RenderNodeServer Server) AddLoopbackRemote(ITileRenderer renderer, string? name = null, int timeoutMs = 400)
    {
        var srv = new RenderNodeServer(renderer);
        lock (_gate) _servers.Add(srv);
        var n = AddRemote(new IPEndPoint(IPAddress.Loopback, srv.Port), name ?? "원격-:" + srv.Port, timeoutMs);
        return (n, srv);
    }

    private void Attach(RenderNode n)
    {
        lock (_gate)
        {
            if (_nodeCount >= MaxNodes) throw new InvalidOperationException("노드 상한 " + MaxNodes);
            n.Id = _nextId++;
            _nodes[_nodeCount++] = n;
            var w = new Thread(() => WorkerLoop(n)) { IsBackground = true, Name = "Farm." + n.Name };
            n.Worker = w;
            w.Start();
        }
        LastAction = "➕ 노드 " + n.Name;
        Raise();
    }

    public bool RemoveNode(int id)
    {
        RenderNode? victim = null;
        lock (_gate)
        {
            for (var i = 0; i < _nodeCount; i++)
            {
                if (_nodes[i]!.Id != id) continue;
                victim = _nodes[i];
                for (var k = i; k < _nodeCount - 1; k++) { _nodes[k] = _nodes[k + 1]; _nodeTilesFrame[k] = _nodeTilesFrame[k + 1]; }
                _nodes[--_nodeCount] = null;
                break;
            }
        }
        if (victim is null) return false;
        victim.Dispose();
        LastAction = "➖ 노드 " + victim.Name;
        Raise();
        return true;
    }

    public void ReviveAll()
    {
        lock (_gate) for (var i = 0; i < _nodeCount; i++) _nodes[i]!.Online = true;
        Raise();
    }

    // ── 워커 측 API(노드 → 팜) ──

    internal Span<byte> TileTarget(in RenderTileJob job) => new(_frame + job.Y0 * Stride + job.X0 * 3, (job.H - 1) * Stride + job.W * 3);

    /// <summary>쓰기 펜스 진입 — 현재 프레임 · 미중단일 때만(코디네이터는 펜스 해제까지 프레임을 넘기지 않음).</summary>
    internal bool BeginWrite(long frameSeq)
    {
        Interlocked.Increment(ref _writers);
        if (Volatile.Read(ref _abort) == 0 && Interlocked.Read(ref _frameSeq) == frameSeq) return true;
        Interlocked.Decrement(ref _writers);
        return false;
    }

    internal void EndWrite() => Interlocked.Decrement(ref _writers);

    internal bool IsDone(int tile) => Volatile.Read(ref _state[tile]) == Done;

    /// <summary>최초 완료 CAS(Claimed → Done) · 소유 기록 · 잔여 감소(펜스 내부 호출).</summary>
    internal bool TryComplete(int tile, int nodeId)
    {
        if (Interlocked.CompareExchange(ref _state[tile], Done, Claimed) != Claimed) return false;
        Volatile.Write(ref _owner[tile], nodeId);
        if (Interlocked.Decrement(ref _remaining) == 0) _done.Set();
        return true;
    }

    private void TileRect(int tile, out int x0, out int y0, out int w, out int h)
    {
        int tx = tile % TilesX, ty = tile / TilesX;
        x0 = _tw * tx; y0 = _th * ty;
        w = tx == TilesX - 1 ? _fw - x0 : _tw;
        h = ty == TilesY - 1 ? _fh - y0 : _th;
    }

    private int Claim(int slot, out bool stolen)
    {
        stolen = false;
        for (var t = 0; t < _tiles; t++)
            if (Volatile.Read(ref _state[t]) == Pending && Interlocked.CompareExchange(ref _state[t], Claimed, Pending) == Pending)
            {
                Volatile.Write(ref _claimAt[t], Stopwatch.GetTimestamp());
                return t;
            }
        // 투기 재발행: 가장 오래 걸리는 클레임 타일(1회)
        var now = Stopwatch.GetTimestamp();
        double ewma = 0; var cnt = 0;
        for (var i = 0; i < _nodeCount; i++) { var e = _nodes[i]!.TileMicrosEwma; if (e > 0) { ewma += e; cnt++; } }
        var thresholdTicks = (long)(Math.Max(StragglerFloorMs * 1000.0, cnt > 0 ? 2.0 * ewma / cnt : 0) * Stopwatch.Frequency / 1_000_000.0);
        int pick = -1; long oldest = long.MaxValue;
        for (var t = 0; t < _tiles; t++)
        {
            if (Volatile.Read(ref _state[t]) != Claimed || Volatile.Read(ref _dup[t]) != 0) continue;
            if (Volatile.Read(ref _claimer[t]) == slot) continue;
            var at = Volatile.Read(ref _claimAt[t]);
            if (now - at > thresholdTicks && at < oldest) { oldest = at; pick = t; }
        }
        if (pick >= 0 && Interlocked.CompareExchange(ref _dup[pick], 1, 0) == 0) { stolen = true; Interlocked.Increment(ref _reissued); return pick; }
        return -1;
    }

    private void WorkerLoop(RenderNode n)
    {
        while (!n.Quit)
        {
            try { n.Go.Wait(); } catch (ObjectDisposedException) { return; }
            if (n.Quit) return;
            var spin = new SpinWait();
            while (_active && Volatile.Read(ref _abort) == 0 && Volatile.Read(ref _remaining) > 0 && n.Online)
            {
                var slot = IndexOf(n);
                if (slot < 0) break;
                var tile = Claim(n.Id, out var stolen);
                if (tile < 0) { spin.SpinOnce(sleep1Threshold: 30); continue; }
                spin.Reset();
                if (!stolen) Volatile.Write(ref _claimer[tile], n.Id);
                TileRect(tile, out var x0, out var y0, out var w, out var h);
                var job = new RenderTileJob(_frameSeq, _time, _fw, _fh, tile, x0, y0, w, h);
                var t0 = Stopwatch.GetTimestamp();
                var outcome = n.RenderTile(job, this);
                var us = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
                if (outcome == TileOutcome.Failed)
                {
                    n.Online = false;
                    if (job.FrameSeq == Interlocked.Read(ref _frameSeq))
                    {
                        if (!stolen) Interlocked.CompareExchange(ref _state[tile], Pending, Claimed);
                        else Volatile.Write(ref _dup[tile], 0);
                    }
                    break;
                }
                n.Observe(us);
                if (outcome == TileOutcome.Committed) Credit(slot, n, stolen, us);
            }
        }
    }

    private void Credit(int slot, RenderNode n, bool stolen, double us)
    {
        Interlocked.Increment(ref n.TilesDone);
        Interlocked.Increment(ref _nodeTilesFrame[slot]);
        if (stolen) Interlocked.Increment(ref n.Stolen);
        var ms = us / 1000.0;
        if (ms > _maxTileMs) Volatile.Write(ref _maxTileMs, ms);
    }

    private int IndexOf(RenderNode n)
    {
        for (var i = 0; i < _nodeCount; i++) if (ReferenceEquals(_nodes[i], n)) return i;
        return -1;
    }

    /// <summary>분산 렌더 → 대상 프레임(RGB24 · stride=width×3) 조립(무할당 · 동기). deadlineMs 초과 시 부분 프레임(쓰기 펜스 후 반환).</summary>
    public FarmFrameResult RenderFrame(double time, IntPtr frame, int width, int height, double deadlineMs = 100)
    {
        lock (_gate)
        {
            if (_nodeCount == 0 || frame == IntPtr.Zero) return default;
            var t0 = Stopwatch.GetTimestamp();
            _fw = width; _fh = height; Stride = width * 3;
            _tiles = TilesX * TilesY;
            _tw = width / TilesX; _th = height / TilesY;
            _frame = (byte*)frame;
            _time = time;
            Interlocked.Increment(ref _frameSeq);
            for (var t = 0; t < _tiles; t++) { _state[t] = Pending; _owner[t] = -1; _claimer[t] = -1; _dup[t] = 0; }
            Array.Clear(_nodeTilesFrame);
            _maxTileMs = 0;
            _reissued = 0;
            _remaining = _tiles;
            Volatile.Write(ref _abort, 0);
            _done.Reset();
            _active = true;
            var used = 0;
            for (var i = 0; i < _nodeCount; i++) if (_nodes[i]!.Online) { _nodes[i]!.Go.Release(); used++; }
            var complete = used > 0 && _done.Wait(TimeSpan.FromMilliseconds(Math.Max(1, deadlineMs)));
            Volatile.Write(ref _abort, 1);   // 쓰기 펜스: 지연 응답/중단 타일이 다음 프레임·해제 버퍼에 기록하지 못하게
            var sw = new SpinWait();
            while (Volatile.Read(ref _writers) != 0) sw.SpinOnce();
            _active = false;
            _frame = null;
            var lat = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            Volatile.Write(ref _lastLatency, lat);
            Volatile.Write(ref _ewmaLatency, _ewmaLatency <= 0 ? lat : _ewmaLatency * 0.9 + lat * 0.1);
            Interlocked.Increment(ref _frames);
            if (!complete) Interlocked.Increment(ref _incomplete);
            Interlocked.Add(ref _totalReissued, _reissued);
            var res = new FarmFrameResult(_frameSeq, complete, _tiles, _reissued, lat, _maxTileMs, used);
            Last = res;
            return res;
        }
    }

    /// <summary>관리 배열 대상(고정 후 동기 렌더).</summary>
    public FarmFrameResult RenderFrame(double time, byte[] frame, int width, int height, double deadlineMs = 100)
    {
        if (frame.Length < width * height * 3) throw new ArgumentException("frame 크기 부족");
        fixed (byte* p = frame) return RenderFrame(time, (IntPtr)p, width, height, deadlineMs);
    }

    /// <summary>DirectPath 슬롯 제자리 조립 → 게시(시퀀스 · -1 = 슬롯 없음).</summary>
    public long RenderInto(DirectPathArena arena, double time, int width, int height, double deadlineMs = 100)
    {
        var s = arena.AcquireWrite();
        if (s < 0) return -1;
        var r = RenderFrame(time, arena.PayloadAddress(s), width, height, deadlineMs);
        ref var m = ref arena.Meta(s);
        m.Width = width; m.Height = height; m.Stride = width * 3; m.Format = DirectPathFormat.Rgb24;
        m.TimestampTicks = DateTime.UtcNow.Ticks;
        m.RenderMicros = (long)(r.LatencyMs * 1000);
        m.NodeMask = r.NodesUsed;
        ulong mask = 0;
        for (var t = 0; t < Math.Min(64, r.Tiles); t++) if (Volatile.Read(ref _state[t]) == Done) mask |= 1UL << t;
        m.TileMask = mask;
        return arena.Publish(s, width * height * 3);
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Farm.OnChange", ex); }
    }

    public void Dispose()
    {
        RenderNode?[] nodes;
        lock (_gate) { nodes = (RenderNode?[])_nodes.Clone(); Array.Clear(_nodes); _nodeCount = 0; }
        foreach (var n in nodes) n?.Dispose();
        lock (_gate) { foreach (var s in _servers) s.Dispose(); _servers.Clear(); }
        _done.Dispose();
    }
}
