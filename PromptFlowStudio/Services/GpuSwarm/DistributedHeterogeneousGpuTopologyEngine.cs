using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace PromptFlowStudio.Services.GpuSwarm;

/// <summary>노드 연산 백엔드.</summary>
public enum GpuBackend : byte { CpuSimd, Dx12, Vulkan, Metal }

/// <summary>노드 연결 계층.</summary>
public enum NodeLink : byte { Local, Lan, Cloud }

/// <summary>
/// [PromptFlow Studio v16.96.0] ⚡ 실시간 이기종 GPU 클러스터 토폴로지 &amp; VRAM 프로빙 엔진.
/// · 노드 SoA(최대 16): 백엔드 · 링크 · 실측 처리율(Mpx/s) · 링크 대역폭(MB/s) · RTT · VRAM 총량/예약 · 하트비트 · 건강.
/// · 핫패스 보고(ReportTile · Heartbeat · Reserve/ReleaseVram) = Interlocked 누적만(무잠금 · 무할당).
/// · Probe(): 구간 델타 → 순간 처리율/대역폭 → Vector&lt;float&gt; SIMD EMA(전 노드 일괄) → 건강 판정 → 정규화 연산 가중치(SIMD 합) · 토폴로지 버전 증가.
/// · 로컬 하드웨어: GPU API 런타임 존재 검출(d3d12 · vulkan · Metal 프레임워크, NativeLibrary.TryLoad) + CPU SIMD FMA GFLOPS/메모리 대역폭 실측.
///   Pure BCL 제약상 GPU 커널 디스패치/VRAM 조회는 불가 → 연산은 CPU SIMD, 로컬 "VRAM" = 관리 메모리 가용량 기반 예산.
/// </summary>
public sealed class DistributedHeterogeneousGpuTopologyEngine
{
    public const int MaxNodes = 16;
    public static readonly string[] BackendNames = { "CPU SIMD", "DirectX 12", "Vulkan", "Metal" };
    public static readonly string[] LinkNames = { "로컬", "LAN", "클라우드" };
    private static readonly double TickMs = 1000.0 / Stopwatch.Frequency;
    private static readonly int Pad = (MaxNodes + Vector<float>.Count - 1) / Vector<float>.Count * Vector<float>.Count;

    private readonly object _gate = new();
    private readonly string[] _name = new string[MaxNodes];
    private readonly GpuBackend[] _backend = new GpuBackend[MaxNodes];
    private readonly NodeLink[] _link = new NodeLink[MaxNodes];
    private readonly long[] _vramTotal = new long[MaxNodes], _vramUsed = new long[MaxNodes], _vramPeak = new long[MaxNodes];
    private readonly long[] _pixels = new long[MaxNodes], _computeTicks = new long[MaxNodes], _bytes = new long[MaxNodes], _xferTicks = new long[MaxNodes], _tiles = new long[MaxNodes];
    private readonly long[] _lastPixels = new long[MaxNodes], _lastCompute = new long[MaxNodes], _lastBytes = new long[MaxNodes], _lastXfer = new long[MaxNodes];
    private readonly long[] _heartbeatTs = new long[MaxNodes];
    private readonly int[] _connected = new int[MaxNodes], _rttUs = new int[MaxNodes];
    private readonly bool[] _healthy = new bool[MaxNodes];
    private readonly int[] _lanes = new int[MaxNodes];
    // SIMD EMA 레인(패딩)
    private readonly float[] _mpx = new float[Pad], _mpxInst = new float[Pad], _bw = new float[Pad], _bwInst = new float[Pad], _rtt = new float[Pad], _rttInst = new float[Pad];
    private readonly float[] _weight = new float[Pad], _load = new float[Pad];
    private int _count;
    private long _probes, _version;

    public DistributedHeterogeneousGpuTopologyEngine() => DetectLocal();

    public float EmaAlpha { get; set; } = 0.3f;
    public double HeartbeatTimeoutMs { get; set; } = 60;
    public int Count => Volatile.Read(ref _count);
    public long Probes => Interlocked.Read(ref _probes);
    public long Version => Interlocked.Read(ref _version);
    public double LastProbeUs { get; private set; }

    // ── 로컬 하드웨어 ──
    public bool HasDx12 { get; private set; }
    public bool HasVulkan { get; private set; }
    public bool HasMetal { get; private set; }
    public double CpuGflops { get; private set; }
    public double CpuBandwidthGBs { get; private set; }
    public GpuBackend BestLocalGpu => HasDx12 ? GpuBackend.Dx12 : HasVulkan ? GpuBackend.Vulkan : HasMetal ? GpuBackend.Metal : GpuBackend.CpuSimd;

    private void DetectLocal()
    {
        if (OperatingSystem.IsWindows()) { HasDx12 = Probe("d3d12.dll"); HasVulkan = Probe("vulkan-1.dll"); }
        else if (OperatingSystem.IsAndroid() || OperatingSystem.IsLinux()) HasVulkan = Probe("libvulkan.so") || Probe("libvulkan.so.1");
        else if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst() || OperatingSystem.IsIOS()) HasMetal = Probe("/System/Library/Frameworks/Metal.framework/Metal");
    }

    private static bool Probe(string lib)
    {
        try
        {
            if (!NativeLibrary.TryLoad(lib, out var h)) return false;
            NativeLibrary.Free(h);
            return true;
        }
        catch (Exception ex) when (ex is BadImageFormatException or DllNotFoundException or ArgumentException) { return false; }
    }

    /// <summary>CPU SIMD 실측(콜드): 단일 코어 Vector FMA 체인 GFLOPS · 메모리 복사 대역폭.</summary>
    public void CalibrateCpu()
    {
        var acc = new Vector<float>(1f); var m = new Vector<float>(0.999f); var a = new Vector<float>(0.001f);
        const int iters = 2_000_000;
        var t0 = Stopwatch.GetTimestamp();
        for (var i = 0; i < iters; i++) acc = acc * m + a;
        var sec = (Stopwatch.GetTimestamp() - t0) * TickMs / 1000.0;
        CpuGflops = acc[0] > -1 ? 2.0 * iters * Vector<float>.Count / Math.Max(1e-9, sec) / 1e9 : 0; // 단일 코어 의존 FMA 체인(하한)
        var src = new byte[8 << 20]; var dst = new byte[8 << 20];
        src.AsSpan().CopyTo(dst);
        t0 = Stopwatch.GetTimestamp();
        for (var k = 0; k < 4; k++) src.AsSpan().CopyTo(dst);
        sec = (Stopwatch.GetTimestamp() - t0) * TickMs / 1000.0;
        CpuBandwidthGBs = 2.0 * 4 * src.Length / Math.Max(1e-9, sec) / 1e9;
    }

    // ── 노드 등록 ──
    /// <summary>노드 등록 — prior = 실측 전 선언값(첫 프로브부터 실측 EMA로 대체) · lanes = 병렬 워커 수.</summary>
    public int AddNode(string name, GpuBackend backend, NodeLink link, long vramBytes, int lanes = 1, float priorMpx = 1f, float priorBwMBs = 0f, float priorRttMs = 0f)
    {
        lock (_gate)
        {
            for (var i = 0; i < _count; i++) if (_name[i] == name) return i; // alreadyRegistered
            if (_count >= MaxNodes) throw new InvalidOperationException("노드 상한 " + MaxNodes);
            var n = _count;
            _name[n] = name; _backend[n] = backend; _link[n] = link; _vramTotal[n] = Math.Max(1, vramBytes);
            _connected[n] = 1; _heartbeatTs[n] = Stopwatch.GetTimestamp();
            _lanes[n] = Math.Max(1, lanes); _mpx[n] = priorMpx; _bw[n] = priorBwMBs; _rtt[n] = priorRttMs; _rttUs[n] = (int)(priorRttMs * 1000); _healthy[n] = true;
            Volatile.Write(ref _count, n + 1);
            Interlocked.Increment(ref _version);
            return n;
        }
    }

    public string Name(int n) => _name[n];
    public GpuBackend Backend(int n) => _backend[n];
    public NodeLink Link(int n) => _link[n];
    public bool IsLocal(int n) => _link[n] == NodeLink.Local;
    public int Lanes(int n) => _lanes[n];
    public long VramTotal(int n) => _vramTotal[n];
    public long VramUsed(int n) => Interlocked.Read(ref _vramUsed[n]);
    public long VramPeak(int n) => Interlocked.Read(ref _vramPeak[n]);
    public long VramFree(int n) => _vramTotal[n] - VramUsed(n);
    public long Tiles(int n) => Interlocked.Read(ref _tiles[n]);
    public long PixelsDone(int n) => Interlocked.Read(ref _pixels[n]);
    public bool Connected(int n) => Volatile.Read(ref _connected[n]) != 0;
    public bool Healthy(int n) => _healthy[n];
    public float Mpx(int n) => _mpx[n];
    public float BandwidthMBs(int n) => _bw[n];
    public float RttMs(int n) => _rtt[n];
    public float Weight(int n) => _weight[n];
    public float Load(int n) => _load[n];
    public double HeartbeatAgeMs(int n) => (Stopwatch.GetTimestamp() - Volatile.Read(ref _heartbeatTs[n])) * TickMs;

    public void SetConnected(int n, bool on) { Volatile.Write(ref _connected[n], on ? 1 : 0); Interlocked.Increment(ref _version); }

    // ── 핫패스 보고 ──
    public void Heartbeat(int n, double rttMs)
    {
        Volatile.Write(ref _heartbeatTs[n], Stopwatch.GetTimestamp());
        Volatile.Write(ref _rttUs[n], (int)Math.Min(int.MaxValue, rttMs * 1000));
    }

    public void ReportTile(int n, int pixels, long computeTicks, int bytes, long xferTicks)
    {
        Interlocked.Add(ref _pixels[n], pixels);
        Interlocked.Add(ref _computeTicks[n], computeTicks);
        Interlocked.Add(ref _bytes[n], bytes);
        Interlocked.Add(ref _xferTicks[n], xferTicks);
        Interlocked.Increment(ref _tiles[n]);
        Volatile.Write(ref _heartbeatTs[n], Stopwatch.GetTimestamp());
    }

    /// <summary>VRAM 예약(잔량 부족 = false · 무잠금 CAS).</summary>
    public bool ReserveVram(int n, long bytes)
    {
        while (true)
        {
            var u = Interlocked.Read(ref _vramUsed[n]);
            if (u + bytes > _vramTotal[n]) return false;
            if (Interlocked.CompareExchange(ref _vramUsed[n], u + bytes, u) != u) continue;
            var nu = u + bytes;
            long pk;
            while (nu > (pk = Interlocked.Read(ref _vramPeak[n])) && Interlocked.CompareExchange(ref _vramPeak[n], nu, pk) != pk) { }
            return true;
        }
    }

    public void ReleaseVram(int n, long bytes) => Interlocked.Add(ref _vramUsed[n], -bytes);

    /// <summary>
    /// 주기 프로빙(무할당): 구간 델타 → 순간값 → SIMD EMA → 건강 → 가중치(건강 노드 처리율 정규화) · 부하(VRAM 점유율).
    /// </summary>
    public void Probe()
    {
        lock (_gate)
        {
            var t0 = Stopwatch.GetTimestamp();
            var n = _count;
            var now = t0;
            for (var i = 0; i < n; i++)
            {
                var px = Interlocked.Read(ref _pixels[i]); var ct = Interlocked.Read(ref _computeTicks[i]);
                var by = Interlocked.Read(ref _bytes[i]); var xt = Interlocked.Read(ref _xferTicks[i]);
                var dPx = px - _lastPixels[i]; var dCt = ct - _lastCompute[i]; var dBy = by - _lastBytes[i]; var dXt = xt - _lastXfer[i];
                _lastPixels[i] = px; _lastCompute[i] = ct; _lastBytes[i] = by; _lastXfer[i] = xt;
                // 표본 없는 구간은 이전 EMA 유지(순간값 = EMA)
                _mpxInst[i] = dCt > 0 ? (float)(dPx / (dCt * TickMs * 1000.0)) : _mpx[i];
                _bwInst[i] = dXt > 0 && dBy > 0 ? (float)(dBy / (dXt * TickMs / 1000.0) / (1024 * 1024)) : _bw[i];
                _rttInst[i] = Volatile.Read(ref _rttUs[i]) / 1000f;
                _healthy[i] = Volatile.Read(ref _connected[i]) != 0 && (now - Volatile.Read(ref _heartbeatTs[i])) * TickMs <= HeartbeatTimeoutMs;
                _load[i] = (float)((double)Interlocked.Read(ref _vramUsed[i]) / _vramTotal[i]);
            }
            Ema(_mpx, _mpxInst, n); Ema(_bw, _bwInst, n); Ema(_rtt, _rttInst, n);
            // 가중치: 건강 노드 처리율 정규화(SIMD 합)
            for (var i = 0; i < Pad; i++) _weight[i] = i < n && _healthy[i] ? Math.Max(_mpx[i], 1e-3f) * _lanes[i] : 0f;
            var sum = SimdSum(_weight);
            if (sum > 0)
            {
                var inv = new Vector<float>(1f / sum);
                var w = MemoryMarshal.Cast<float, Vector<float>>(_weight.AsSpan());
                for (var k = 0; k < w.Length; k++) w[k] *= inv;
            }
            Interlocked.Increment(ref _probes);
            Interlocked.Increment(ref _version);
            LastProbeUs = (Stopwatch.GetTimestamp() - t0) * TickMs * 1000.0;
        }
    }

    private void Ema(float[] ema, float[] inst, int n)
    {
        var a = Math.Clamp(EmaAlpha, 0.01f, 1f);
        for (var i = 0; i < n; i++) if (ema[i] == 0) ema[i] = inst[i]; // 첫 표본 = 즉시 채택
        var ve = MemoryMarshal.Cast<float, Vector<float>>(ema.AsSpan());
        var vi = MemoryMarshal.Cast<float, Vector<float>>(inst.AsSpan());
        var va = new Vector<float>(a);
        for (var k = 0; k < ve.Length; k++) ve[k] += (vi[k] - ve[k]) * va;
    }

    public static float SimdSum(ReadOnlySpan<float> x)
    {
        var acc = Vector<float>.Zero; var i = 0;
        var v = MemoryMarshal.Cast<float, Vector<float>>(x);
        for (; i < v.Length; i++) acc += v[i];
        var s = Vector.Dot(acc, Vector<float>.One);
        for (var k = v.Length * Vector<float>.Count; k < x.Length; k++) s += x[k];
        return s;
    }

    /// <summary>건강 노드 수.</summary>
    public int HealthyCount { get { var c = 0; for (var i = 0; i < Count; i++) if (_healthy[i]) c++; return c; } }

    /// <summary>첫 건강 로컬 노드(페일백 대상 · 없으면 0).</summary>
    public int LocalFallbackNode
    {
        get
        {
            for (var i = 0; i < Count; i++) if (_link[i] == NodeLink.Local && Volatile.Read(ref _connected[i]) != 0) return i;
            return 0;
        }
    }
}
