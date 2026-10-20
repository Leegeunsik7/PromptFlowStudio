using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Neural3d;

namespace PromptFlowStudio.Services.Hardware;

public enum DirectPathFormat : int { Rgb24 = 0, Bgra32 = 1, Nv12 = 2 }
public enum DirectPathBacking : byte { NativeAligned, SharedMemory }
internal enum SlotState : int { Free = 0, Writing = 1, Ready = 2, Reading = 3 }

/// <summary>프레임 메타데이터(공유 메모리 직렬 · blittable) — 3DGS 카메라 포즈/내부 파라미터 · ICVFX 렌즈/클립 · 타일 마스크.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct DirectPathMeta
{
    public long FrameSeq, TimestampTicks, RenderMicros;
    public int Width, Height, Stride;
    public DirectPathFormat Format;
    public Vector3 CameraPosition;
    public Quaternion CameraRotation;
    public float Fx, Fy, Cx, Cy;
    public float FocalMm, SensorWidthMm, NearClip, FarClip;
    public int SplatCount, NodeMask;
    public ulong TileMask;

    public static int FrameBytes(DirectPathFormat f, int w, int h) => f switch { DirectPathFormat.Bgra32 => w * h * 4, DirectPathFormat.Nv12 => w * h + (w + 1) / 2 * 2 * ((h + 1) / 2), _ => w * h * 3 };
}

/// <summary>GPU 외부 메모리 결선점 — 호스트 고정 메모리 등록(cudaHostRegister · D3D12 OpenExistingHeapFromAddress · Vulkan VK_EXT_external_memory_host).</summary>
public interface IGpuHostMemoryInterop
{
    string Name { get; }
    bool Register(IntPtr address, long bytes);
    void Unregister(IntPtr address);
}

/// <summary>
/// [PromptFlow Studio v16.63.0] ⚡ CPU-GPU Zero-Copy DirectPath 메모리 아레나.
///   ▸ 백킹: 4KB 정렬 NativeMemory(프로세스 내) 또는 MemoryMappedFile 공유 메모리(Windows 이름 지정 → 타 프로세스 <see cref="OpenExisting"/>).
///   ▸ 페이지 잠금: Windows VirtualLock(P/Invoke · 최선 노력) → DMA/GPU 호스트 등록 가능한 상주 메모리 · <see cref="IGpuHostMemoryInterop"/> 결선점.
///   ▸ 슬롯 링: 헤더 페이지(슬롯 제어 256B: 상태 · 길이 · 시퀀스 · <see cref="DirectPathMeta"/>) + 4KB 정렬 페이로드. 상태 전이는 공유 메모리 위 Interlocked CAS(락 없음 · 프로세스 간 동작).
///   ▸ 생산자는 <see cref="Payload"/> Span 에 직접 렌더(복사 0) → <see cref="Publish"/> · 소비자는 같은 Span 을 직접 읽고 <see cref="Release"/>. 소비 지연 시 가장 오래된 Ready 슬롯 회수(최신 우선).
/// </summary>
public sealed unsafe class DirectPathArena : IDisposable
{
    public const uint Magic = 0x50444650; // "PFDP"
    public const int PageSize = 4096, ControlSize = 256, HeatPageBytes = 64 * 1024, MaxSlots = 15;
    private const int HdrSlotCount = 8, HdrSlotBytes = 16, HdrWriteSeq = 24;

    private readonly MemoryMappedFile? _mmf;
    private readonly MemoryMappedViewAccessor? _view;
    private byte* _base;
    private readonly long _total;
    private readonly bool _owner;
    private readonly int[] _heat;
    private int _cursor, _disposed;
    private long _published, _consumed, _reclaimed, _zeroCopyBytes, _copiedBytes, _acquireFails;

    public int SlotCount { get; }
    public long SlotBytes { get; }
    public long TotalBytes => _total;
    public DirectPathBacking Backing { get; }
    public string? SharedName { get; }
    public bool PageLocked { get; private set; }
    public IntPtr BaseAddress => (IntPtr)_base;
    public long Published => Interlocked.Read(ref _published);
    public long Consumed => Interlocked.Read(ref _consumed);
    public long Reclaimed => Interlocked.Read(ref _reclaimed);
    public long ZeroCopyBytes => Interlocked.Read(ref _zeroCopyBytes);
    public long CopiedBytes => Interlocked.Read(ref _copiedBytes);
    public long AcquireFails => Interlocked.Read(ref _acquireFails);
    public ReadOnlySpan<int> PageHeat => _heat;
    public int HeatPages => _heat.Length;

    private DirectPathArena(int slots, long slotBytes, DirectPathBacking backing, string? name, MemoryMappedFile? mmf, MemoryMappedViewAccessor? view, byte* basePtr, bool owner)
    {
        SlotCount = slots; SlotBytes = slotBytes; Backing = backing; SharedName = name;
        _mmf = mmf; _view = view; _base = basePtr; _owner = owner;
        _total = PageSize + slots * slotBytes;
        _heat = new int[(int)((_total + HeatPageBytes - 1) / HeatPageBytes)];
    }

    private static long RoundPage(long n) => (n + PageSize - 1) / PageSize * PageSize;

    /// <summary>아레나 생성 — sharedName 지정 시(Windows) 이름 있는 공유 메모리, 아니면 4KB 정렬 네이티브 메모리.</summary>
    public static DirectPathArena Create(int slots, long maxFrameBytes, string? sharedName = null, bool lockPages = true)
    {
        slots = Math.Clamp(slots, 2, MaxSlots);
        var sb = RoundPage(Math.Max(PageSize, maxFrameBytes));
        var total = PageSize + slots * sb;
        DirectPathArena a;
        if (sharedName is not null && OperatingSystem.IsWindows())
        {
            var mmf = MemoryMappedFile.CreateNew(sharedName, total, MemoryMappedFileAccess.ReadWrite);
            var view = mmf.CreateViewAccessor(0, total, MemoryMappedFileAccess.ReadWrite);
            byte* p = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            p += view.PointerOffset;
            a = new DirectPathArena(slots, sb, DirectPathBacking.SharedMemory, sharedName, mmf, view, p, true);
        }
        else
        {
            var p = (byte*)NativeMemory.AlignedAlloc((nuint)total, PageSize);
            a = new DirectPathArena(slots, sb, DirectPathBacking.NativeAligned, null, null, null, p, true);
        }
        new Span<byte>(a._base, PageSize).Clear();
        *(uint*)a._base = Magic;
        *(int*)(a._base + 4) = 1;
        *(int*)(a._base + HdrSlotCount) = slots;
        *(long*)(a._base + HdrSlotBytes) = sb;
        if (lockPages) a.PageLocked = NativeLock.TryLock((IntPtr)a._base, total);
        return a;
    }

    /// <summary>타 프로세스/인스턴스가 생성한 공유 아레나 열기(Windows) — 동일 물리 페이지 매핑(복사 0).</summary>
    public static DirectPathArena OpenExisting(string sharedName)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("이름 있는 공유 메모리는 Windows 전용");
        var mmf = MemoryMappedFile.OpenExisting(sharedName, MemoryMappedFileRights.ReadWrite);
        var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
        byte* p = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        p += view.PointerOffset;
        if (*(uint*)p != Magic) { view.SafeMemoryMappedViewHandle.ReleasePointer(); view.Dispose(); mmf.Dispose(); throw new InvalidDataException("DirectPath 매직 불일치"); }
        return new DirectPathArena(*(int*)(p + HdrSlotCount), *(long*)(p + HdrSlotBytes), DirectPathBacking.SharedMemory, sharedName, mmf, view, p, false);
    }

    private ref int StateRef(int slot) => ref *(int*)(_base + 64 + slot * ControlSize);
    private ref int LengthRef(int slot) => ref *(int*)(_base + 64 + slot * ControlSize + 4);
    private ref long SeqRef(int slot) => ref *(long*)(_base + 64 + slot * ControlSize + 8);
    private ref long WriteSeqRef => ref *(long*)(_base + HdrWriteSeq);

    /// <summary>슬롯 메타데이터(공유 메모리 직접 참조).</summary>
    public ref DirectPathMeta Meta(int slot) => ref *(DirectPathMeta*)(_base + 64 + slot * ControlSize + 16);

    /// <summary>슬롯 페이로드 Span(공유 메모리 직접 · 복사 0).</summary>
    public Span<byte> Payload(int slot) => new(_base + PageSize + slot * SlotBytes, (int)SlotBytes);

    public IntPtr PayloadAddress(int slot) => (IntPtr)(_base + PageSize + slot * SlotBytes);

    public long SequenceOf(int slot) => (uint)slot < (uint)SlotCount ? Volatile.Read(ref SeqRef(slot)) : -1;
    public int LengthOf(int slot) => (uint)slot < (uint)SlotCount ? Volatile.Read(ref LengthRef(slot)) : 0;

    /// <summary>쓰기 슬롯 확보(Free 우선 → 없으면 가장 오래된 Ready 회수) — 실패 -1.</summary>
    public int AcquireWrite()
    {
        var n = SlotCount;
        var start = Interlocked.Increment(ref _cursor);
        for (var k = 0; k < n; k++)
        {
            var s = (int)((uint)(start + k) % (uint)n);
            if (Interlocked.CompareExchange(ref StateRef(s), (int)SlotState.Writing, (int)SlotState.Free) == (int)SlotState.Free) return s;
        }
        int oldest = -1; long os = long.MaxValue;
        for (var s = 0; s < n; s++)
            if (Volatile.Read(ref StateRef(s)) == (int)SlotState.Ready && SeqRef(s) < os) { os = SeqRef(s); oldest = s; }
        if (oldest >= 0 && Interlocked.CompareExchange(ref StateRef(oldest), (int)SlotState.Writing, (int)SlotState.Ready) == (int)SlotState.Ready)
        {
            Interlocked.Increment(ref _reclaimed);
            return oldest;
        }
        Interlocked.Increment(ref _acquireFails);
        return -1;
    }

    /// <summary>제자리 렌더 완료 게시(시퀀스 부여 · 페이지 히트 집계).</summary>
    public long Publish(int slot, int length)
    {
        if ((uint)slot >= (uint)SlotCount) return -1;
        length = (int)Math.Clamp(length, 0, SlotBytes);
        LengthRef(slot) = length;
        var seq = Interlocked.Increment(ref WriteSeqRef);
        Volatile.Write(ref SeqRef(slot), seq);
        Meta(slot).FrameSeq = seq;
        Volatile.Write(ref StateRef(slot), (int)SlotState.Ready);
        Interlocked.Increment(ref _published);
        Interlocked.Add(ref _zeroCopyBytes, length);
        Heat(PageSize + slot * SlotBytes, length);
        return seq;
    }

    /// <summary>레거시 경로(관리 버퍼 → 공유 메모리 복사 게시) — 비교/호환용 · 복사 바이트 집계.</summary>
    public long PublishCopy(ReadOnlySpan<byte> frame, in DirectPathMeta meta)
    {
        var s = AcquireWrite();
        if (s < 0) return -1;
        var len = (int)Math.Min(frame.Length, SlotBytes);
        frame[..len].CopyTo(Payload(s));
        Meta(s) = meta;
        Interlocked.Add(ref _copiedBytes, len);
        var seq = Publish(s, len);
        Interlocked.Add(ref _zeroCopyBytes, -len);
        return seq;
    }

    /// <summary>쓰기 취소(Free 복귀).</summary>
    public void Abort(int slot) { if ((uint)slot < (uint)SlotCount) Volatile.Write(ref StateRef(slot), (int)SlotState.Free); }

    /// <summary>읽기 슬롯 확보 — newest=true 최신 프레임(프리뷰) · false 가장 오래된 프레임(인코더 FIFO). 없으면 -1.</summary>
    public int AcquireRead(bool newest = false, long afterSeq = 0)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            int pick = -1; long ps = newest ? long.MinValue : long.MaxValue;
            for (var s = 0; s < SlotCount; s++)
            {
                if (Volatile.Read(ref StateRef(s)) != (int)SlotState.Ready) continue;
                var q = SeqRef(s);
                if (q <= afterSeq) continue;
                if (newest ? q > ps : q < ps) { ps = q; pick = s; }
            }
            if (pick < 0) return -1;
            if (Interlocked.CompareExchange(ref StateRef(pick), (int)SlotState.Reading, (int)SlotState.Ready) == (int)SlotState.Ready) return pick;
        }
        return -1;
    }

    /// <summary>읽기 반납(Free).</summary>
    public void Release(int slot)
    {
        if ((uint)slot >= (uint)SlotCount) return;
        if (Interlocked.CompareExchange(ref StateRef(slot), (int)SlotState.Free, (int)SlotState.Reading) == (int)SlotState.Reading) Interlocked.Increment(ref _consumed);
    }

    /// <summary>복사 바이트 수동 집계(소비자가 관리 버퍼로 복사한 경우).</summary>
    public void CountCopy(long bytes) => Interlocked.Add(ref _copiedBytes, bytes);

    private void Heat(long offset, int length)
    {
        if (length <= 0) return;
        var p0 = (int)(offset / HeatPageBytes);
        var p1 = (int)Math.Min(_heat.Length - 1, (offset + length - 1) / HeatPageBytes);
        for (var p = p0; p <= p1; p++) Interlocked.Increment(ref _heat[p]);
    }

    /// <summary>슬롯 상태 스냅샷(0 Free · 1 Writing · 2 Ready · 3 Reading).</summary>
    public int StateOf(int slot) => (uint)slot < (uint)SlotCount ? Volatile.Read(ref StateRef(slot)) : -1;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (PageLocked) NativeLock.Unlock((IntPtr)_base, _total);
        if (_view is not null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
            _mmf!.Dispose();
        }
        else if (_owner) NativeMemory.AlignedFree(_base);
        _base = null;
    }
}

/// <summary>페이지 잠금(Windows VirtualLock/VirtualUnlock · 비-Windows 무동작).</summary>
internal static class NativeLock
{
#if WINDOWS
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualLock(IntPtr address, UIntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualUnlock(IntPtr address, UIntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessWorkingSetSize(IntPtr process, out UIntPtr min, out UIntPtr max);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetProcessWorkingSetSize(IntPtr process, UIntPtr min, UIntPtr max);
    private static readonly object Gate = new();

    public static bool TryLock(IntPtr p, long bytes)
    {
        try
        {
            if (VirtualLock(p, (UIntPtr)(ulong)bytes)) return true;
            lock (Gate)
            {
                var proc = GetCurrentProcess();
                if (!GetProcessWorkingSetSize(proc, out var mn, out var mx)) return false;
                var add = (ulong)bytes + 1024 * 1024;
                if (!SetProcessWorkingSetSize(proc, (UIntPtr)((ulong)mn + add), (UIntPtr)Math.Max((ulong)mx, (ulong)mn + add * 2))) return false;
            }
            return VirtualLock(p, (UIntPtr)(ulong)bytes);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    public static void Unlock(IntPtr p, long bytes)
    {
        try { VirtualUnlock(p, (UIntPtr)(ulong)bytes); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }
#else
    public static bool TryLock(IntPtr p, long bytes) => false;
    public static void Unlock(IntPtr p, long bytes) { }
#endif
}

/// <summary>DirectPath 벤치(복사 경로 vs 제자리 경로).</summary>
public readonly record struct DirectPathBench(int Frames, int FrameBytes, double CopyPathMs, double ZeroCopyPathMs, long CopyPathBytesCopied, long ZeroCopyBytesCopied, long ZeroCopyAllocated);

/// <summary>
/// [PromptFlow Studio v16.63.0] ⚡ 하드웨어 Zero-Copy DirectPath 파이프라인 — 렌더러(3DGS 라스터라이저 · 분산 팜) → 공유 아레나 슬롯 제자리 렌더 →
/// 소비자(하드웨어 인코더 · 프리뷰 · 타 프로세스)가 동일 페이지를 직접 소비. 3DGS 카메라 · ICVFX 렌즈 메타데이터 동봉.
///   ▸ GPU: D3D12(DirectML) 가용성 P/Invoke 프로브 · <see cref="Interop"/>(호스트 메모리 등록) 결선 시 아레나 전체를 GPU 가시 메모리로 등록.
///   ▸ 레거시 대비: <see cref="Benchmark"/> 로 관리 스테이징 → 복사 게시 → 소비자 복사 경로와 바이트/시간 비교.
/// </summary>
public sealed class HardwareZeroCopyDirectPathPipeline : IDisposable
{
    public const int DefaultSlots = 4;
    private readonly object _gate = new();
    private DirectPathArena? _arena;
    private IGpuHostMemoryInterop? _interop;
    private bool _registered;
    private byte[] _staging = Array.Empty<byte>(), _consumerCopy = Array.Empty<byte>();
    private float[] _depth = Array.Empty<float>();
    private int _w, _h;
    private DirectPathFormat _fmt;

    public HardwareZeroCopyDirectPathPipeline() { }

    public event Action? OnChange;

    public DirectPathArena? Arena => _arena;
    public int Width => _w;
    public int Height => _h;
    public DirectPathFormat Format => _fmt;
    public bool GpuD3d12Capable { get; } = PromptFlowStudio.Services.Inference.DirectMlNativeAdapter.IsDirectMlCapable();
    public bool GpuRegistered => _registered;
    public DirectPathBench? LastBench { get; private set; }
    public string? LastAction { get; private set; }

    /// <summary>GPU 호스트 메모리 등록 결선점(설정 시 현재 아레나 즉시 등록 시도).</summary>
    public IGpuHostMemoryInterop? Interop
    {
        get => _interop;
        set { lock (_gate) { Unregister(); _interop = value; RegisterGpu(); } Raise(); }
    }

    /// <summary>해상도/포맷 구성(아레나 재생성 · 구성 시에만 할당).</summary>
    public DirectPathArena Configure(int width, int height, DirectPathFormat format = DirectPathFormat.Rgb24, int slots = DefaultSlots, string? sharedName = null)
    {
        lock (_gate)
        {
            Unregister();
            _arena?.Dispose();
            _w = Math.Clamp(width, 16, 7680); _h = Math.Clamp(height, 16, 4320); _fmt = format;
            _arena = DirectPathArena.Create(slots, DirectPathMeta.FrameBytes(format, _w, _h), sharedName);
            if (_depth.Length < _w * _h) _depth = new float[_w * _h];
            RegisterGpu();
            LastAction = "🧩 DirectPath " + _w + "×" + _h + " " + format + " · 슬롯 " + _arena.SlotCount + " · " + (_arena.TotalBytes >> 20) + "MB · "
                + (_arena.Backing == DirectPathBacking.SharedMemory ? "공유 메모리" : "정렬 네이티브") + (_arena.PageLocked ? " · 페이지 잠금" : "");
            Raise();
            return _arena;
        }
    }

    private void RegisterGpu()
    {
        if (_interop is null || _arena is null) return;
        try { _registered = _interop.Register(_arena.BaseAddress, _arena.TotalBytes); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException) { _registered = false; PromptFlowStudio.Program.Log("DirectPath.Gpu", ex); }
    }

    private void Unregister()
    {
        if (!_registered || _interop is null || _arena is null) return;
        try { _interop.Unregister(_arena.BaseAddress); } catch (InvalidOperationException) { }
        _registered = false;
    }

    /// <summary>3DGS 라스터라이즈를 공유 슬롯에 제자리 수행(복사 0) — 카메라/ICVFX 메타 기록 · 게시 시퀀스 반환(-1 = 슬롯 없음).</summary>
    public long RenderSplats(AiGaussianSplattingRasterizerEngine ras, GaussianScene scene, in SplatCamera cam, in SplatRenderOptions opt, float focalMm = 35f, float sensorMm = 36f)
    {
        var a = _arena;
        if (a is null || _fmt != DirectPathFormat.Rgb24 || cam.Width != _w || cam.Height != _h) return -1;
        using var guard = ZeroGcRegionScope.Enter("DirectPath.RenderSplats");
        var s = a.AcquireWrite();
        if (s < 0) return -1;
        var t0 = Stopwatch.GetTimestamp();
        var n = ras.Render(scene, cam, opt, a.Payload(s)[..(_w * _h * 3)], _depth.AsSpan(0, _w * _h));
        ref var m = ref a.Meta(s);
        m.TimestampTicks = DateTime.UtcNow.Ticks;
        m.RenderMicros = (Stopwatch.GetTimestamp() - t0) * 1_000_000 / Stopwatch.Frequency;
        m.Width = _w; m.Height = _h; m.Stride = _w * 3; m.Format = DirectPathFormat.Rgb24;
        m.CameraPosition = cam.Position;
        m.CameraRotation = Quaternion.CreateFromRotationMatrix(new Matrix4x4(cam.Right.X, cam.Right.Y, cam.Right.Z, 0, cam.Up.X, cam.Up.Y, cam.Up.Z, 0, -cam.Forward.X, -cam.Forward.Y, -cam.Forward.Z, 0, 0, 0, 0, 1));
        m.Fx = cam.Fx; m.Fy = cam.Fy; m.Cx = cam.Cx; m.Cy = cam.Cy;
        m.FocalMm = focalMm; m.SensorWidthMm = sensorMm; m.NearClip = opt.Near; m.FarClip = opt.Far;
        m.SplatCount = n; m.NodeMask = 1; m.TileMask = ulong.MaxValue;
        return a.Publish(s, _w * _h * 3);
    }

    /// <summary>레거시(복사) vs DirectPath(제자리) — 동일 라스터 작업 frames 회 · 복사 바이트 · 시간 · 제자리 경로 할당.</summary>
    public DirectPathBench Benchmark(AiGaussianSplattingRasterizerEngine ras, GaussianScene scene, in SplatCamera cam, int frames = 8)
    {
        lock (_gate)
        {
            var a = _arena ?? throw new InvalidOperationException("Configure 먼저 호출");
            var bytes = _w * _h * 3;
            if (_staging.Length < bytes) { _staging = new byte[bytes]; _consumerCopy = new byte[bytes]; }
            var opt = new SplatRenderOptions(Near: 0.05f, Far: 50f);
            long copied0 = a.CopiedBytes;
            var t = Stopwatch.GetTimestamp();
            for (var i = 0; i < frames; i++)
            {
                ras.Render(scene, cam, opt, _staging.AsSpan(0, bytes), _depth.AsSpan(0, _w * _h));
                var meta = new DirectPathMeta { Width = _w, Height = _h, Stride = _w * 3 };
                a.PublishCopy(_staging.AsSpan(0, bytes), meta);
                var r = a.AcquireRead();
                if (r >= 0) { a.Payload(r)[..bytes].CopyTo(_consumerCopy); a.CountCopy(bytes); a.Release(r); }
            }
            var copyMs = (Stopwatch.GetTimestamp() - t) * 1000.0 / Stopwatch.Frequency;
            var copyBytes = a.CopiedBytes - copied0;
            RenderSplats(ras, scene, cam, opt); var w0 = a.AcquireRead(); if (w0 >= 0) a.Release(w0);
            copied0 = a.CopiedBytes;
            var alloc0 = GC.GetAllocatedBytesForCurrentThread();
            t = Stopwatch.GetTimestamp();
            long checksum = 0;
            for (var i = 0; i < frames; i++)
            {
                RenderSplats(ras, scene, cam, opt);
                var r = a.AcquireRead();
                if (r >= 0) { checksum += a.Payload(r)[bytes / 2]; a.Release(r); }
            }
            var zcMs = (Stopwatch.GetTimestamp() - t) * 1000.0 / Stopwatch.Frequency;
            var alloc = GC.GetAllocatedBytesForCurrentThread() - alloc0;
            GC.KeepAlive(checksum);
            var b = new DirectPathBench(frames, bytes, copyMs, zcMs, copyBytes, a.CopiedBytes - copied0, alloc);
            LastBench = b;
            LastAction = "🏁 DirectPath 벤치 · 복사 " + (copyBytes >> 20) + "MB → 0B · " + copyMs.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "ms → "
                + zcMs.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "ms";
            Raise();
            return b;
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("DirectPath.OnChange", ex); }
    }

    public void Dispose()
    {
        lock (_gate) { Unregister(); _arena?.Dispose(); _arena = null; }
    }
}
