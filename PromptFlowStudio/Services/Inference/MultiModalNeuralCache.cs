using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PromptFlowStudio.Services.Inference;

/// <summary>캐시 엔트리 인덱스(불변) — MMF 오프셋·길이·최근 접근 틱.</summary>
public sealed record NeuralCacheEntry(string Key, long Offset, int Length, long AccessTick);

/// <summary>
/// [PromptFlow Studio v8.5.2] 멀티모달 뉴럴 추론 캐시(관찰 가능 싱글턴 · 순수 BCL · 외부 NuGet 0 · IDisposable).
///   ▸ 동일 시나리오/프롬프트의 하드웨어 추론 결과(텐서/임베딩 바이트)를 <see cref="MemoryMappedFile"/> 링 버퍼에 <b>Zero-Copy</b> 상주.
///   ▸ 읽기/쓰기 전송 버퍼는 <see cref="ArrayPool{T}"/> 대여로 재사용(핫패스 힙 할당 0) · 인덱스는 <see cref="Dictionary{TKey,TValue}"/>(락 보호).
///   ▸ 링 랩어라운드 시 겹치는 구 엔트리를 자동 무효화(덮어쓰기 일관성) → 용량 상한 내 LRU 근사 상주.
///   ▸ 키는 (모달리티·시드) SHA-256 → 충돌 안전. 실패/미스는 예외 없이 우아하게 처리(Graceful).
/// </summary>
public sealed class MultiModalNeuralCache : IDisposable
{
    private readonly long _capacity;
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly object _gate = new();
    private readonly Dictionary<string, NeuralCacheEntry> _index = new();
    private long _writePos;
    private long _accessSeq;
    private bool _disposed;

    private long _hits, _misses, _evictions, _bytesStored;

    /// <summary>기본 16MB 인메모리 MMF(디스크 파일 없음 · 프로세스 전용).</summary>
    public MultiModalNeuralCache(long capacityBytes = 16L * 1024 * 1024)
    {
        _capacity = Math.Max(64 * 1024, capacityBytes);
        _mmf = MemoryMappedFile.CreateNew(null, _capacity);        // mapName=null → 익명(비영속) 매핑
        _view = _mmf.CreateViewAccessor(0, _capacity, MemoryMappedFileAccess.ReadWrite);
    }

    public event Action? OnChange;
    private void Notify() => OnChange?.Invoke();

    // ── 관측 지표(HUD) ──
    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);
    public long Evictions => Interlocked.Read(ref _evictions);
    public long BytesResident => Interlocked.Read(ref _bytesStored);
    public int Entries { get { lock (_gate) return _index.Count; } }
    public long CapacityBytes => _capacity;
    public double HitRatio { get { var h = Hits; var t = h + Misses; return t == 0 ? 0 : (double)h / t; } }
    public double FillRatio => _capacity <= 0 ? 0 : Math.Clamp((double)BytesResident / _capacity, 0, 1);

    /// <summary>(모달리티·시드) → 캐시 키(SHA-256 hex).</summary>
    public static string KeyOf(string modality, string seed)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{modality}{seed}"))).ToLowerInvariant();

    /// <summary>
    /// 추론 결과 바이트를 캐시에 상주(링 배치 · 겹침 무효화). 용량 초과 데이터는 거부(false).
    /// </summary>
    public bool Store(string key, ReadOnlySpan<byte> data)
    {
        if (_disposed || string.IsNullOrEmpty(key)) return false;
        var len = data.Length;
        if (len <= 0 || len > _capacity) return false;

        var buffer = ArrayPool<byte>.Shared.Rent(len);   // 전송 버퍼 풀 대여(핫패스 무할당)
        try
        {
            data.CopyTo(buffer);
            lock (_gate)
            {
                if (_writePos + len > _capacity) _writePos = 0;   // 링 랩어라운드
                var start = _writePos;
                InvalidateOverlaps(start, len);                   // 덮어쓰기 구간의 구 엔트리 제거
                _view.WriteArray(start, buffer, 0, len);
                _index[key] = new NeuralCacheEntry(key, start, len, Interlocked.Increment(ref _accessSeq));
                _writePos += len;
                RecomputeResident();
            }
        }
        catch (Exception ex) { Program.Log("NeuralCache.Store", ex); return false; }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        Notify();
        return true;
    }

    /// <summary>
    /// 캐시 조회 — 히트 시 <paramref name="destination"/> 에 복사하고 바이트 수 반환. 미스/용량부족=0.
    /// </summary>
    public int TryGet(string key, Span<byte> destination)
    {
        if (_disposed || string.IsNullOrEmpty(key)) { Interlocked.Increment(ref _misses); return 0; }
        NeuralCacheEntry? e;
        lock (_gate)
        {
            if (!_index.TryGetValue(key, out var found)) { Interlocked.Increment(ref _misses); return 0; }
            if (destination.Length < found.Length) { Interlocked.Increment(ref _misses); return 0; }
            _index[key] = found with { AccessTick = Interlocked.Increment(ref _accessSeq) };   // LRU 갱신
            e = found;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(e.Length);
        try
        {
            _view.ReadArray(e.Offset, buffer, 0, e.Length);
            buffer.AsSpan(0, e.Length).CopyTo(destination);
            Interlocked.Increment(ref _hits);
            return e.Length;
        }
        catch (Exception ex) { Program.Log("NeuralCache.TryGet", ex); Interlocked.Increment(ref _misses); return 0; }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    /// <summary>존재 여부만 확인(통계 미반영).</summary>
    public bool Contains(string key) { lock (_gate) return _index.ContainsKey(key); }

    /// <summary>엔트리 길이(없으면 0) — 조회 전 버퍼 크기 산정용.</summary>
    public int LengthOf(string key) { lock (_gate) return _index.TryGetValue(key, out var e) ? e.Length : 0; }

    public void Clear()
    {
        lock (_gate) { _index.Clear(); _writePos = 0; RecomputeResident(); }
        Notify();
    }

    // ── 내부(락 아래 호출) ──
    private void InvalidateOverlaps(long start, int len)
    {
        var end = start + len;
        var dead = _index.Where(kv => kv.Value.Offset < end && kv.Value.Offset + kv.Value.Length > start)
                         .Select(kv => kv.Key).ToList();
        foreach (var k in dead) { _index.Remove(k); Interlocked.Increment(ref _evictions); }
    }

    private void RecomputeResident() => Interlocked.Exchange(ref _bytesStored, _index.Values.Sum(v => (long)v.Length));

    public void Dispose()
    {
        _disposed = true;
        try { _view.Dispose(); } catch { /* 무시 */ }
        try { _mmf.Dispose(); } catch { /* 무시 */ }
    }
}
