using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Inference;

/// <summary>텐서 상주 계층 — Mapped(MMF 공유 아레나) / Pooled(ArrayPool 스필).</summary>
public enum NeuralMemoryTier { Mapped, Pooled }

public sealed record NeuralTensorView(string Name, string Shape, NeuralMemoryTier Tier, long Offset, int Bytes, int Pages, long Version, long Streams, bool Verified);

public sealed record NeuralStreamReport(int Tensors, long Bytes, int Chunks, int ChecksumFailures, double ElapsedMs, double ThroughputMBps, int Mapped, int Pooled);

/// <summary>
/// v9.6.1 자율 뉴럴 메모리 스트리머 — 익명 MemoryMappedFile 페이지 아레나(64KB 페이지 first-fit)에 텐서 가중치를 상주시키고,
/// 스트리밍 시 페이로드는 아레나에 둔 채 (텐서·오프셋·길이) 디스크립터만 <see cref="Channel{T}"/> 로 중계한다(제로카피 핸드오프).
/// 아레나 포화 시 이미 스트리밍된 콜드 텐서를 LRU 로 Pooled 계층 강등 → 재할당, 그래도 부족하면 ArrayPool 스필(동적 버퍼 할당).
/// 청크 체인 체크섬으로 비트 플립/변조를 검출. MMF 불가 플랫폼은 전량 Pooled 로 Graceful. 순수 BCL.
/// </summary>
public sealed class AutonomousNeuralMemoryStreamer : IDisposable
{
    public const int PageBytes = 64 * 1024;
    public const int ChunkBytes = 64 * 1024;
    private const ulong Seed = 14695981039346656037UL;
    private const ulong Prime = 0x100000001B3UL;

    private sealed class Tensor
    {
        public string Name = "";
        public int[] Shape = Array.Empty<int>();
        public NeuralMemoryTier Tier;
        public int FirstPage = -1;
        public int Pages;
        public int Bytes;
        public byte[]? Pooled;
        public long Version;
        public ulong Checksum;
        public long Streams;
        public long LastTouch;
        public bool Verified;
        public int Pinned;
    }

    private readonly record struct Chunk(Tensor Tensor, int Offset, int Length, bool Last);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _streamGate = new(1, 1);
    private readonly Dictionary<string, Tensor> _tensors = new(StringComparer.Ordinal);
    private readonly MemoryMappedFile? _mmf;
    private readonly MemoryMappedViewAccessor? _view;
    private readonly bool[] _pageUsed;
    private readonly int _pageCount;
    private long _clock, _version, _evictions, _spills, _streamedBytes, _chunks, _failures;
    private NeuralStreamReport? _last;
    private bool _disposed;

    public event Action? OnChange;

    public AutonomousNeuralMemoryStreamer(int pages = 192)
    {
        _pageCount = Math.Clamp(pages, 16, 16384);
        _pageUsed = new bool[_pageCount];
        try
        {
            // 익명 매핑(null 이름) — Windows/Linux/Android/iOS 공통 지원
            _mmf = MemoryMappedFile.CreateNew(null, (long)_pageCount * PageBytes, MemoryMappedFileAccess.ReadWrite);
            _view = _mmf.CreateViewAccessor(0, (long)_pageCount * PageBytes, MemoryMappedFileAccess.ReadWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
            _view?.Dispose();
            _mmf?.Dispose();
            _view = null;
            _mmf = null;
            LastError = "MMF 불가 — ArrayPool 전용 강등: " + ex.Message;
        }
    }

    public bool IsMapped => _view is not null;
    public long CapacityBytes => (long)_pageCount * PageBytes;
    public string? LastAction { get; private set; }
    public string? LastError { get; private set; }
    public long Evictions => Interlocked.Read(ref _evictions);
    public long Spills => Interlocked.Read(ref _spills);
    public long StreamedBytes => Interlocked.Read(ref _streamedBytes);
    public long ChunksStreamed => Interlocked.Read(ref _chunks);
    public long ChecksumFailures => Interlocked.Read(ref _failures);
    public NeuralStreamReport? LastReport { get { lock (_gate) return _last; } }

    public long MappedBytes
    {
        get
        {
            lock (_gate)
            {
                var used = 0;
                foreach (var u in _pageUsed) if (u) used++;
                return (long)used * PageBytes;
            }
        }
    }

    public long PooledBytes
    {
        get
        {
            lock (_gate)
            {
                long sum = 0;
                foreach (var t in _tensors.Values) if (t.Tier == NeuralMemoryTier.Pooled) sum += t.Bytes;
                return sum;
            }
        }
    }

    public IReadOnlyList<NeuralTensorView> Tensors
    {
        get
        {
            lock (_gate)
            {
                var list = new List<NeuralTensorView>(_tensors.Count);
                foreach (var t in _tensors.Values)
                    list.Add(new NeuralTensorView(t.Name, string.Join('×', t.Shape), t.Tier,
                        t.Tier == NeuralMemoryTier.Mapped ? (long)t.FirstPage * PageBytes : -1, t.Bytes, t.Pages, t.Version, t.Streams, t.Verified));
                list.Sort((a, b) => a.Version.CompareTo(b.Version));
                return list;
            }
        }
    }

    /// <summary>텐서 가중치 적재 — Mapped 우선, 포화 시 콜드 강등 후 재시도, 최종 ArrayPool 스필.</summary>
    public NeuralTensorView Load(string name, int[] shape, ReadOnlySpan<float> weights)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var bytes = MemoryMarshal.AsBytes(weights);
        var t = new Tensor { Name = name, Shape = shape, Bytes = bytes.Length, Checksum = Mix(Seed, bytes) };
        lock (_gate)
        {
            if (_tensors.Remove(name, out var old)) FreeLocked(old);
            var pages = Math.Max(1, (bytes.Length + PageBytes - 1) / PageBytes);
            var first = _view is null ? -1 : AllocLocked(pages);
            if (first < 0 && _view is not null && DemoteColdLocked(pages)) first = AllocLocked(pages);
            if (first >= 0)
            {
                t.Tier = NeuralMemoryTier.Mapped;
                t.FirstPage = first;
                t.Pages = pages;
                _view!.SafeMemoryMappedViewHandle.WriteSpan((ulong)(_view.PointerOffset + (long)first * PageBytes), bytes);
            }
            else
            {
                t.Tier = NeuralMemoryTier.Pooled;
                t.Pooled = ArrayPool<byte>.Shared.Rent(bytes.Length);
                bytes.CopyTo(t.Pooled);
                _spills++;
            }
            t.Version = ++_version;
            t.LastTouch = ++_clock;
            _tensors[name] = t;
            LastAction = $"{name} 적재 · {(t.Tier == NeuralMemoryTier.Mapped ? "MMF" : "Pool 스필")} · {bytes.Length / 1024}KB";
        }
        OnChange?.Invoke();
        return new NeuralTensorView(t.Name, string.Join('×', t.Shape), t.Tier, t.FirstPage * (long)PageBytes, t.Bytes, t.Pages, t.Version, 0, false);
    }

    /// <summary>전 텐서 스트리밍 — 생산자는 디스크립터만 발행, 소비자가 청크 체인 체크섬 검증.</summary>
    public async Task<NeuralStreamReport> StreamAllAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _streamGate.WaitAsync(ct).ConfigureAwait(false);
        var pinned = new List<Tensor>();
        try
        {
            lock (_gate)
            {
                pinned.AddRange(_tensors.Values);
                pinned.Sort((a, b) => a.Version.CompareTo(b.Version));
                foreach (var t in pinned) t.Pinned++;
            }
            var channel = Channel.CreateBounded<Chunk>(new BoundedChannelOptions(16) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
            var sw = Stopwatch.StartNew();
            var consumer = Task.Run(() => ConsumeAsync(channel.Reader, ct), ct);
            long total = 0;
            try
            {
                foreach (var t in pinned)
                {
                    for (var off = 0; off < t.Bytes; off += ChunkBytes)
                    {
                        var len = Math.Min(ChunkBytes, t.Bytes - off);
                        await channel.Writer.WriteAsync(new Chunk(t, off, len, off + len >= t.Bytes), ct).ConfigureAwait(false);
                    }
                    total += t.Bytes;
                }
            }
            finally { channel.Writer.TryComplete(); }
            var (chunks, failures) = await consumer.ConfigureAwait(false);
            var ms = Math.Max(0.001, sw.Elapsed.TotalMilliseconds);
            int mapped = 0, pooled = 0;
            lock (_gate)
                foreach (var t in pinned) { if (t.Tier == NeuralMemoryTier.Mapped) mapped++; else pooled++; }
            var report = new NeuralStreamReport(pinned.Count, total, chunks, failures, Math.Round(ms, 2),
                Math.Round(total / 1048576.0 / (ms / 1000.0), 1), mapped, pooled);
            lock (_gate) _last = report;
            Interlocked.Add(ref _streamedBytes, total);
            Interlocked.Add(ref _chunks, chunks);
            Interlocked.Add(ref _failures, failures);
            LastAction = $"스트리밍 {pinned.Count}텐서 · {report.ThroughputMBps}MB/s" + (failures > 0 ? $" · 체크섬 실패 {failures}" : " · 무결");
            OnChange?.Invoke();
            return report;
        }
        finally
        {
            lock (_gate) foreach (var t in pinned) t.Pinned--;
            _streamGate.Release();
        }
    }

    private async Task<(int Chunks, int Failures)> ConsumeAsync(ChannelReader<Chunk> reader, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        var running = Seed;
        int chunks = 0, failures = 0;
        try
        {
            await foreach (var c in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                running = Absorb(c, buffer, running);
                chunks++;
                if (!c.Last) continue;
                var ok = running == c.Tensor.Checksum;
                if (!ok) failures++;
                lock (_gate)
                {
                    c.Tensor.Verified = ok;
                    c.Tensor.Streams++;
                    c.Tensor.LastTouch = ++_clock;
                }
                running = Seed;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        return (chunks, failures);
    }

    private ulong Absorb(in Chunk c, byte[] buffer, ulong running)
    {
        var t = c.Tensor;
        if (t.Tier == NeuralMemoryTier.Pooled && t.Pooled is { } pooled)
            return Mix(running, pooled.AsSpan(c.Offset, c.Length)); // Pooled: 슬라이스 직접 참조(복사 0)
        var dest = buffer.AsSpan(0, c.Length);
        _view!.SafeMemoryMappedViewHandle.ReadSpan((ulong)(_view.PointerOffset + (long)t.FirstPage * PageBytes + c.Offset), dest);
        return Mix(running, dest);
    }

    /// <summary>데모: 12 레이어(1MB) + 임베딩(4MB) 적재 → 스트리밍. 아레나 12MB 초과분은 자율 강등/스필.</summary>
    public async Task<NeuralStreamReport> RunDemoAsync(CancellationToken ct = default)
    {
        var weights = ArrayPool<float>.Shared.Rent(1024 * 1024);
        try
        {
            for (var i = 0; i < 12; i++)
            {
                Fill(weights.AsSpan(0, 256 * 1024), (uint)(i + 1));
                Load($"blk.{i:00}.attn", new[] { 256, 1024 }, weights.AsSpan(0, 256 * 1024));
            }
            Fill(weights.AsSpan(0, 1024 * 1024), 0xE3BDu);
            Load("tok_embd", new[] { 1024, 1024 }, weights.AsSpan(0, 1024 * 1024));
        }
        finally { ArrayPool<float>.Shared.Return(weights); }
        return await StreamAllAsync(ct).ConfigureAwait(false);
    }

    /// <summary>데모 위변조: MMF 상주 텐서 1바이트 비트 플립(메모리 오염 시뮬) — 다음 스트리밍에서 검출.</summary>
    public string? InjectBitFlip()
    {
        string? name = null;
        lock (_gate)
        {
            if (_view is not null)
                foreach (var t in _tensors.Values)
                {
                    if (t.Tier != NeuralMemoryTier.Mapped) continue;
                    var pos = (long)t.FirstPage * PageBytes + t.Bytes / 2;
                    _view.Write(pos, (byte)(_view.ReadByte(pos) ^ 0x10));
                    name = t.Name;
                    break;
                }
            LastAction = name is null ? "MMF 상주 텐서 없음" : $"{name} 비트 플립 주입";
        }
        OnChange?.Invoke();
        return name;
    }

    public void ReleaseAll()
    {
        lock (_gate)
        {
            foreach (var t in _tensors.Values) FreeLocked(t);
            _tensors.Clear();
            LastAction = "전 텐서 해제";
        }
        OnChange?.Invoke();
    }

    private int AllocLocked(int pages)
    {
        var run = 0;
        for (var i = 0; i < _pageCount; i++)
        {
            run = _pageUsed[i] ? 0 : run + 1;
            if (run < pages) continue;
            var first = i - pages + 1;
            for (var p = first; p <= i; p++) _pageUsed[p] = true;
            return first;
        }
        return -1;
    }

    /// <summary>스트리밍 완료·비고정 콜드 텐서를 LRU 순 Pooled 로 강등해 연속 페이지 확보.</summary>
    private bool DemoteColdLocked(int pagesNeeded)
    {
        var cold = new List<Tensor>();
        foreach (var t in _tensors.Values)
            if (t.Tier == NeuralMemoryTier.Mapped && t.Streams > 0 && t.Pinned == 0) cold.Add(t);
        cold.Sort((a, b) => a.LastTouch.CompareTo(b.LastTouch));
        foreach (var t in cold)
        {
            var pooled = ArrayPool<byte>.Shared.Rent(t.Bytes);
            _view!.SafeMemoryMappedViewHandle.ReadSpan((ulong)(_view.PointerOffset + (long)t.FirstPage * PageBytes), pooled.AsSpan(0, t.Bytes));
            for (var p = t.FirstPage; p < t.FirstPage + t.Pages; p++) _pageUsed[p] = false;
            t.Tier = NeuralMemoryTier.Pooled;
            t.Pooled = pooled;
            t.FirstPage = -1;
            t.Pages = 0;
            _evictions++;
            if (HasRunLocked(pagesNeeded)) return true;
        }
        return false;
    }

    private bool HasRunLocked(int pages)
    {
        var run = 0;
        foreach (var u in _pageUsed)
        {
            run = u ? 0 : run + 1;
            if (run >= pages) return true;
        }
        return false;
    }

    private void FreeLocked(Tensor t)
    {
        if (t.Tier == NeuralMemoryTier.Mapped && t.FirstPage >= 0)
            for (var p = t.FirstPage; p < t.FirstPage + t.Pages; p++) _pageUsed[p] = false;
        // 스트리밍 중(고정) 버퍼는 풀 반환 보류 — 소비자 읽기 경합 차단, GC 회수
        if (t.Pooled is { } pooled && t.Pinned == 0) ArrayPool<byte>.Shared.Return(pooled);
        t.Pooled = null;
        t.FirstPage = -1;
    }

    private static void Fill(Span<float> dest, uint seed)
    {
        var x = seed * 2654435761u | 1u;
        for (var i = 0; i < dest.Length; i++)
        {
            x ^= x << 13; x ^= x >> 17; x ^= x << 5;
            dest[i] = (x & 0xFFFF) / 32768f - 1f;
        }
    }

    /// <summary>청크 체인 체크섬 — 8바이트 정렬 청크 연쇄 시 전체 1회 계산과 동일.</summary>
    private static ulong Mix(ulong h, ReadOnlySpan<byte> data)
    {
        var words = MemoryMarshal.Cast<byte, ulong>(data);
        foreach (var w in words) { h = (h ^ w) * Prime; h ^= h >> 29; }
        foreach (var b in data[(words.Length * 8)..]) h = (h ^ b) * Prime;
        return h;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            foreach (var t in _tensors.Values) FreeLocked(t);
            _tensors.Clear();
        }
        _view?.Dispose();
        _mmf?.Dispose();
    }
}
