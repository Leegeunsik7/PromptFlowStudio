using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.LowSpec;

/// <summary>아레나 대여 핸들(값 형식) — 크기 등급 · 슬롯 · 오프셋 · 요청 길이. Length 0 = 미스(호출자 stackalloc 폴백).</summary>
public readonly record struct SqueezeLease(int Class, int Slot, int Offset, int Length)
{
    public bool IsValid => Length > 0;
}

/// <summary>고정 용량 미세 객체 재활용 풀 — 생성 시 선할당, 소진 시 null(신규 할당 0).</summary>
public sealed class ObjectRecycler<T> where T : class, new()
{
    private readonly T[] _items;
    private int _top;
    private long _misses;

    public ObjectRecycler(int capacity)
    {
        _items = new T[Math.Max(1, capacity)];
        for (var i = 0; i < _items.Length; i++) _items[i] = new T();
        _top = _items.Length;
    }

    public int Capacity => _items.Length;
    public int Available { get { lock (_items) return _top; } }
    public long Misses => Interlocked.Read(ref _misses);

    public T? Rent()
    {
        lock (_items)
        {
            if (_top == 0) { Interlocked.Increment(ref _misses); return null; }
            var t = _items[--_top];
            _items[_top] = null!;
            return t;
        }
    }

    public void Return(T item)
    {
        lock (_items) if (_top < _items.Length) _items[_top++] = item;
    }
}

/// <summary>
/// [PromptFlow Studio v16.65.0] ⚡ Zero-Alloc 메모리 압착 &amp; GC 억제 가드.
/// 단일 선할당 아레나(RAM ≤4GB: 2MB · ≤8GB: 4MB · 그 외 8MB)를 6개 크기 등급(64B~64KB) 슬롯으로 분할 — 대여/반납은 정수 프리 스택(무할당 · 이중 반납 차단).
/// 프레임 링 버퍼(래핑 덮어쓰기) · <see cref="ObjectRecycler{T}"/> 미세 객체 재활용 · 세션 동안 SustainedLowLatency + Gen0/1/2 수집 증분 감시 ·
/// <see cref="Squeeze"/>: 프리 스택 오프셋 정렬(저주소 우선 재사용 → 작업 집합 압착) + 피크 대비 회수 가능 바이트 산출.
/// </summary>
public sealed class ZeroAllocMemorySqueezerGuard : IDisposable
{
    public static readonly int[] ClassSizes = { 64, 256, 1024, 4096, 16384, 65536 };
    public const int RingBytes = 256 * 1024;

    private readonly object _gate = new();
    private readonly byte[] _arena;
    private readonly byte[] _ring = new byte[RingBytes];
    private readonly int[] _base = new int[6], _slots = new int[6], _top = new int[6], _peak = new int[6];
    private readonly int[][] _free = new int[6][];
    private readonly bool[][] _inUse = new bool[6][];
    private readonly ZeroGcCounters _counters;
    private int _ringHead;
    private long _requested, _committed, _rents, _returns, _misses, _doubleReturns, _ringWrites, _ringWraps, _ticks, _squeezes;
    private int _g0, _g1, _g2;
    private bool _session;

    public ZeroAllocMemorySqueezerGuard() : this(ArenaBytesFor(HardwareAdaptiveLowSpecProfiler.Probe())) { }

    public ZeroAllocMemorySqueezerGuard(long arenaBytes, ZeroGcCounters? counters = null)
    {
        _counters = counters ?? new ZeroGcCounters();
        _arena = new byte[Math.Clamp(arenaBytes, 512 * 1024, 64L * 1024 * 1024)];
        // 등급별 균등 바이트 몫 — 작은 등급이 슬롯을 더 많이 가짐
        var share = _arena.Length / ClassSizes.Length;
        var off = 0;
        for (var c = 0; c < ClassSizes.Length; c++)
        {
            var n = Math.Max(1, share / ClassSizes[c]);
            _base[c] = off; _slots[c] = n;
            _free[c] = new int[n]; _inUse[c] = new bool[n];
            for (var i = 0; i < n; i++) _free[c][i] = n - 1 - i; // 스택 꼭대기 = 슬롯 0(저주소)
            _top[c] = n;
            off += n * ClassSizes[c];
        }
    }

    public event Action? OnChange;

    public static long ArenaBytesFor(in HardwareSnapshot hw) =>
        hw.TotalRamMb <= 4096 ? 2L * 1024 * 1024 : hw.TotalRamMb <= 8192 ? 4L * 1024 * 1024 : 8L * 1024 * 1024;

    public long ArenaBytes => _arena.Length;
    public long RequestedBytes => Interlocked.Read(ref _requested);
    public long CommittedBytes => Interlocked.Read(ref _committed);
    public long Rents => Interlocked.Read(ref _rents);
    public long Returns => Interlocked.Read(ref _returns);
    public long Misses => Interlocked.Read(ref _misses);
    public long DoubleReturns => Interlocked.Read(ref _doubleReturns);
    public long RingWrites => Interlocked.Read(ref _ringWrites);
    public long RingWraps => Interlocked.Read(ref _ringWraps);
    public long Ticks => Interlocked.Read(ref _ticks);
    public long Squeezes => Interlocked.Read(ref _squeezes);
    public long GuardViolations => _counters.Violations;
    public bool SessionActive { get { lock (_gate) return _session; } }
    public string? LastAction { get; private set; }

    /// <summary>패킹 효율(요청/커밋 · 1에 가까울수록 압착).</summary>
    public float PackingRatio { get { var c = CommittedBytes; return c > 0 ? (float)RequestedBytes / c : 1f; } }

    /// <summary>힙 대비 압축률 — 동일 수명 대여를 매번 new 했을 때 누적 할당 대비 아레나 고정 점유 절감(0..1).</summary>
    public float CompressionRate
    {
        get
        {
            var naive = (double)Rents * 128 + RingWrites * 64; // 대여/링 기록 1회당 보수적 평균 객체 비용
            return naive > _arena.Length ? (float)(1 - _arena.Length / naive) : 0f;
        }
    }

    /// <summary>세션 시작 이후 Gen0/1/2 수집 증분.</summary>
    public (int Gen0, int Gen1, int Gen2) CollectionsSinceSession
    {
        get
        {
            lock (_gate)
                return _session ? (GC.CollectionCount(0) - _g0, GC.CollectionCount(1) - _g1, GC.CollectionCount(2) - _g2) : (0, 0, 0);
        }
    }

    public int ClassOf(int bytes)
    {
        for (var c = 0; c < ClassSizes.Length; c++) if (bytes <= ClassSizes[c]) return c;
        return -1;
    }

    public int FreeSlots(int cls) { lock (_gate) return (uint)cls < (uint)_top.Length ? _top[cls] : 0; }
    public int SlotCount(int cls) => (uint)cls < (uint)_slots.Length ? _slots[cls] : 0;
    public int PeakSlots(int cls) { lock (_gate) return (uint)cls < (uint)_peak.Length ? _peak[cls] : 0; }

    /// <summary>최소 적합 등급 대여 — 소진 시 상위 등급 승격 · 전부 소진 시 미스(무할당).</summary>
    public SqueezeLease Rent(int bytes)
    {
        if (bytes <= 0) return default;
        lock (_gate)
        {
            for (var c = ClassOf(bytes); c >= 0 && c < ClassSizes.Length; c++)
            {
                if (_top[c] == 0) continue;
                var slot = _free[c][--_top[c]];
                _inUse[c][slot] = true;
                var used = _slots[c] - _top[c];
                if (used > _peak[c]) _peak[c] = used;
                _requested += bytes; _committed += ClassSizes[c]; _rents++;
                return new SqueezeLease(c, slot, _base[c] + slot * ClassSizes[c], bytes);
            }
            _misses++;
            return default;
        }
    }

    public Span<byte> Span(in SqueezeLease lease) => lease.IsValid ? _arena.AsSpan(lease.Offset, lease.Length) : Span<byte>.Empty;

    /// <summary>반납(이중 반납 · 위조 핸들 차단).</summary>
    public bool Return(in SqueezeLease lease)
    {
        if (!lease.IsValid || (uint)lease.Class >= (uint)ClassSizes.Length) return false;
        lock (_gate)
        {
            var c = lease.Class;
            if ((uint)lease.Slot >= (uint)_slots[c] || !_inUse[c][lease.Slot] || lease.Offset != _base[c] + lease.Slot * ClassSizes[c])
            { _doubleReturns++; return false; }
            _inUse[c][lease.Slot] = false;
            _free[c][_top[c]++] = lease.Slot;
            _requested -= lease.Length; _committed -= ClassSizes[c]; _returns++;
            return true;
        }
    }

    /// <summary>링 버퍼 기록(래핑 덮어쓰기) — 기록 시작 오프셋 반환. 링보다 큰 입력은 꼬리만 보존.</summary>
    public int RingWrite(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (data.Length > RingBytes) data = data[^RingBytes..];
            var start = _ringHead;
            var first = Math.Min(data.Length, RingBytes - start);
            data[..first].CopyTo(_ring.AsSpan(start));
            if (first < data.Length) { data[first..].CopyTo(_ring); _ringWraps++; }
            _ringHead = (start + data.Length) % RingBytes;
            if (_ringHead == 0 && data.Length > 0) _ringWraps++;
            _ringWrites++;
            return start;
        }
    }

    public ReadOnlySpan<byte> RingAt(int offset, int length) => offset + length <= RingBytes ? _ring.AsSpan(offset, length) : ReadOnlySpan<byte>.Empty;

    /// <summary>GC 억제 세션 — SustainedLowLatency 참조 획득 · 수집 기준선 기록.</summary>
    public void BeginSession()
    {
        lock (_gate)
        {
            if (_session) return;
            _session = true;
            GcLatencySession.Acquire();
            _g0 = GC.CollectionCount(0); _g1 = GC.CollectionCount(1); _g2 = GC.CollectionCount(2);
        }
        LastAction = "🛡 GC 억제 세션 시작";
        Raise();
    }

    public void EndSession()
    {
        lock (_gate)
        {
            if (!_session) return;
            _session = false;
            GcLatencySession.Release();
        }
        LastAction = "⏹ GC 억제 세션 종료";
        Raise();
    }

    /// <summary>1프레임 압착 파이프라인(무할당 가드): 미세 버퍼 대여 → 링 매핑 → 반납.</summary>
    public int Tick(int frameBytes)
    {
        using var guard = ZeroGcRegionScope.Enter("LowSpec.Squeezer.Tick", _counters);
        var a = Rent(Math.Clamp(frameBytes, 1, 65536));
        var b = Rent(200);
        var c = Rent(48);
        var span = Span(a);
        if (!span.IsEmpty) { span[0] = (byte)_ticks; RingWrite(span[..Math.Min(span.Length, 4096)]); }
        Return(c); Return(b); Return(a);
        Interlocked.Increment(ref _ticks);
        return span.Length;
    }

    /// <summary>압착: 프리 스택을 오프셋 역순 정렬(꼭대기 = 저주소) — 이후 대여가 저주소에 밀집. 회수 가능 바이트(피크 이후 미사용) 반환.</summary>
    public long Squeeze()
    {
        long trimmable = 0;
        lock (_gate)
        {
            for (var c = 0; c < ClassSizes.Length; c++)
            {
                Array.Sort(_free[c], 0, _top[c]);
                Array.Reverse(_free[c], 0, _top[c]);
                trimmable += (long)(_slots[c] - _peak[c]) * ClassSizes[c];
                _peak[c] = _slots[c] - _top[c];
            }
            _squeezes++;
        }
        LastAction = "🗜 압착 · 회수 가능 " + (trimmable / 1024).ToString(System.Globalization.CultureInfo.InvariantCulture) + "KB";
        Raise();
        return trimmable;
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("LowSpec.Squeezer.OnChange", ex); }
    }

    public void Dispose() => EndSession();
}
