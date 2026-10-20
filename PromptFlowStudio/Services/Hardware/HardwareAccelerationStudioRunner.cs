using System.Diagnostics;
using System.Globalization;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.Hardware;

/// <summary>GPU 실측(nvidia-smi) — 미탐지 시 Name=null.</summary>
public readonly record struct GpuSample(string? Name, int MemUsedMb, int MemTotalMb, int UtilGpu, int UtilEncoder, DateTime At);

/// <summary>
/// [PromptFlow Studio v16.63.0] ⚡ 하드웨어 가속 &amp; 분산 렌더링 스튜디오 러너 —
/// 분산 팜(로컬 + 루프백 RFT1 원격) → DirectPath 공유 슬롯 제자리 조립 → 프리뷰 다운샘플 → 하드웨어 인코더 슬롯 핸드오프(복사 0).
/// 30Hz PeriodicTimer · <see cref="TickOnce"/> 무할당(인코더 미가동/Null 기준).
/// </summary>
public sealed class HardwareAccelerationStudioRunner : IDisposable
{
    public const int PreviewW = 192, PreviewH = 108, TickHz = 30;

    private readonly object _gate = new();
    private readonly HardwareZeroCopyDirectPathPipeline _dp;
    private readonly DistributedRenderFarmSyncEngine _farm;
    private readonly HardwareVideoEncoderOffloader _enc;
    private readonly byte[] _preview = new byte[PreviewW * PreviewH * 3];
    private readonly byte[] _owners = new byte[DistributedRenderFarmSyncEngine.MaxTiles];
    private CancellationTokenSource? _cts;
    private double _t;
    private long _frameSeq, _lastSeq;
    private int _w = 640, _h = 360;
    private bool _farmReady;
    private GpuSample _gpu;

    public HardwareAccelerationStudioRunner(IServiceProvider sp)
        : this((HardwareZeroCopyDirectPathPipeline?)sp.GetService(typeof(HardwareZeroCopyDirectPathPipeline)) ?? new HardwareZeroCopyDirectPathPipeline(),
               (DistributedRenderFarmSyncEngine?)sp.GetService(typeof(DistributedRenderFarmSyncEngine)) ?? new DistributedRenderFarmSyncEngine(),
               (HardwareVideoEncoderOffloader?)sp.GetService(typeof(HardwareVideoEncoderOffloader)) ?? new HardwareVideoEncoderOffloader(() => null)) { }

    public HardwareAccelerationStudioRunner(HardwareZeroCopyDirectPathPipeline dp, DistributedRenderFarmSyncEngine farm, HardwareVideoEncoderOffloader enc)
    {
        _dp = dp; _farm = farm; _enc = enc;
    }

    public event Action? OnChange;

    public HardwareZeroCopyDirectPathPipeline DirectPath => _dp;
    public DistributedRenderFarmSyncEngine Farm => _farm;
    public HardwareVideoEncoderOffloader Encoder => _enc;
    public bool IsRunning => _cts is not null;
    public long FrameSeq => Interlocked.Read(ref _frameSeq);
    public int Width => _w;
    public int Height => _h;
    public double TimeSec => _t;
    public double DeadlineMs { get; set; } = 1000.0 / TickHz;
    public GpuSample Gpu => _gpu;
    public string? LastAction { get; private set; }

    /// <summary>기본 팜(로컬 2 + 루프백 원격 1) · DirectPath 아레나 준비(최초 1회).</summary>
    public void EnsureReady()
    {
        lock (_gate)
        {
            if (_dp.Arena is null || _dp.Width != _w || _dp.Height != _h) _dp.Configure(_w, _h, DirectPathFormat.Rgb24, 4);
            if (_farmReady) return;
            _farmReady = true;
            if (_farm.NodeCount == 0)
            {
                var r = new RayTracedStudioScene();
                var local = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
                for (var i = 0; i < local; i++) _farm.AddLocal(r, "로컬-GPU" + (i + 1).ToString(CultureInfo.InvariantCulture));
                _farm.AddLoopbackRemote(r, "원격-루프백");
            }
        }
    }

    public void SetResolution(int w, int h)
    {
        var running = IsRunning;
        Stop();
        lock (_gate) { _w = Math.Clamp(w & ~1, 64, 7680); _h = Math.Clamp(h & ~1, 36, 4320); _dp.Configure(_w, _h, DirectPathFormat.Rgb24, 4); }
        LastAction = "📐 렌더 " + _w.ToString(CultureInfo.InvariantCulture) + "×" + _h.ToString(CultureInfo.InvariantCulture);
        if (running) Start(); else Raise();
    }

    /// <summary>1틱(무할당) — 팜 조립 → 슬롯 게시 → 프리뷰 → 인코더 핸드오프/반납.</summary>
    public FarmFrameResult TickOnce(double dt)
    {
        EnsureReady();
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("HwAccel.Runner.Tick");
            _t += dt;
            var a = _dp.Arena!;
            var seq = _farm.RenderInto(a, _t, _w, _h, DeadlineMs);
            var r = _farm.Last;
            if (seq < 0) return r;
            var s = a.AcquireRead(newest: true, afterSeq: _lastSeq);
            if (s >= 0)
            {
                _lastSeq = a.SequenceOf(s);
                Downsample(a.Payload(s), _w, _h, _preview);
                if (_enc.IsRunning) _enc.SubmitSlot(a, s);
                else a.Release(s);
            }
            for (var t = 0; t < Math.Min(_owners.Length, _farm.TileCount); t++) _owners[t] = (byte)Math.Clamp(_farm.OwnerOf(t) + 1, 0, 255);
            Interlocked.Increment(ref _frameSeq);
            return r;
        }
    }

    private static void Downsample(ReadOnlySpan<byte> src, int w, int h, Span<byte> dst)
    {
        for (var y = 0; y < PreviewH; y++)
        {
            var sy = y * h / PreviewH;
            for (var x = 0; x < PreviewW; x++)
            {
                var sx = x * w / PreviewW;
                var si = (sy * w + sx) * 3; var di = (y * PreviewW + x) * 3;
                dst[di] = src[si]; dst[di + 1] = src[si + 1]; dst[di + 2] = src[si + 2];
            }
        }
    }

    public void CopyPreview(Span<byte> dst) { lock (_gate) _preview.AsSpan().CopyTo(dst); }

    /// <summary>타일 소유 노드(Id+1 · 0 = 미완) 복사.</summary>
    public int CopyOwners(Span<byte> dst) { lock (_gate) { var n = Math.Min(dst.Length, _farm.TileCount); _owners.AsSpan(0, n).CopyTo(dst); return n; } }

    /// <summary>nvidia-smi 실측(2s 타임아웃 · 미탐지 시 Name=null).</summary>
    public async Task<GpuSample> RefreshGpuAsync(CancellationToken ct = default)
    {
        var smi = OperatingSystem.IsWindows() ? PromptFlowStudio.Services.Engines.CliPathResolver.GetExecutablePath("nvidia-smi", "nvidia-smi.exe") : null;
        if (smi is null) { _gpu = new GpuSample(null, 0, 0, 0, 0, DateTime.Now); return _gpu; }
        using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
        to.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var r = await PromptFlowStudio.Services.Engines.ProcessRunner.RunAsync(smi, new[] { "--query-gpu=name,memory.used,memory.total,utilization.gpu,utilization.encoder", "--format=csv,noheader,nounits" }, null, null, to.Token).ConfigureAwait(false);
            var line = r.StdOut.Split('\n')[0].Split(',');
            if (r.Ok && line.Length >= 5)
            {
                static int I(string s) => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
                _gpu = new GpuSample(line[0].Trim(), I(line[1]), I(line[2]), I(line[3]), I(line[4]), DateTime.Now);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (System.ComponentModel.Win32Exception) { }
        return _gpu;
    }

    public void Start()
    {
        if (_cts is not null) return;
        EnsureReady();
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(() => LoopAsync(cts.Token));
        LastAction = "▶ 분산 렌더 · DirectPath 라이브";
        Raise();
    }

    public void Stop()
    {
        var c = Interlocked.Exchange(ref _cts, null);
        if (c is null) return;
        c.Cancel(); c.Dispose();
        LastAction = "⏸ 정지";
        Raise();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / TickHz));
        var last = Stopwatch.GetTimestamp();
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var now = Stopwatch.GetTimestamp();
                var dt = Math.Min(0.2, (now - last) / (double)Stopwatch.Frequency);
                last = now;
                try { TickOnce(dt); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("HwAccel.Tick", ex); }
                if ((FrameSeq & 3) == 0) Raise();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("HwAccel.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
