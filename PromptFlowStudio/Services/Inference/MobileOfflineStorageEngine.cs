using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Inference;

/// <summary>[v9.1.3] 오프라인 보관 대상 시네마틱 프롬프트 1건.</summary>
public sealed record OfflinePromptRecord(
    string Id,
    string Scope,
    string Prompt,
    int Revision,
    DateTimeOffset UpdatedAt);

/// <summary>[v9.1.3] 타임라인 변경 델타 1건(클립 추가/이동/삭제 등 · 재접속 시 순서대로 재생).</summary>
public sealed record OfflineTimelineDelta(
    long Sequence,
    string ClipId,
    int TrackIndex,
    double StartSeconds,
    double DurationSeconds,
    string Kind,
    string Payload,
    DateTimeOffset At);

/// <summary>
/// [PromptFlow Studio v9.1.3] 모바일 오프라인 스토리지 엔진.
///   ▸ 시네마틱 프롬프트와 타임라인 델타를 인메모리에 유지하고, 순수 BCL 바이너리 파일로 로컬 영속화한다.
///   ▸ 외부 DB/NuGet 0 — <c>BinaryWriter</c>/<c>BinaryReader</c> + SHA-256 꼬리 지문으로 손상 파일을 걸러낸다.
///   ▸ 저장 경로는 <c>LocalApplicationData</c>(Android/iOS 샌드박스 포함) 이며, 쓰기는 임시파일 → 원자적 교체로 수행한다.
///   ▸ 제5헌장: 디스크 I/O 실패는 예외로 전파하지 않고 <see cref="LastError"/> 로 보고한 뒤 인메모리 상태를 보존한다.
/// </summary>
public sealed class MobileOfflineStorageEngine : IDisposable
{
    private const string Magic = "PFOS";
    private const int FormatVersion = 1;
    private const int MaxPrompts = 2048;
    private const int MaxDeltas = 4096;
    private const int AutoFlushMs = 5_000;

    private readonly ConcurrentDictionary<string, OfflinePromptRecord> _prompts = new(StringComparer.Ordinal);
    private readonly List<OfflineTimelineDelta> _deltas = new(64);
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly object _deltaSync = new();
    // CS0104 회피: WinForms 헤드에서 System.Windows.Forms.Timer 와 충돌하므로 완전 한정명 고정.
    private readonly System.Threading.Timer _autoFlush;

    private long _sequence;
    private int _flushCount;
    private int _loadCount;
    private int _trimmedDeltas;
    private volatile bool _dirty;
    private bool _disposed;

    public MobileOfflineStorageEngine(string? storagePath = null)
    {
        StoragePath = string.IsNullOrWhiteSpace(storagePath) ? ResolveDefaultPath() : storagePath!;
        _autoFlush = new System.Threading.Timer(_ => AutoFlushTick(), null, AutoFlushMs, AutoFlushMs);
    }

    /// <summary>상태 변경 브로드캐스트(구독자는 <c>InvokeAsync(StateHasChanged)</c> 로 마셜링할 것).</summary>
    public event Action? OnChange;

    /// <summary>바이너리 저장 파일 절대 경로.</summary>
    public string StoragePath { get; }

    /// <summary>미저장 변경 존재 여부.</summary>
    public bool IsDirty => _dirty;

    /// <summary>디스크 I/O 진행 중.</summary>
    public bool IsBusy { get; private set; }

    /// <summary>보관 중인 프롬프트 수.</summary>
    public int PromptCount => _prompts.Count;

    /// <summary>대기 중인 타임라인 델타 수.</summary>
    public int DeltaCount { get { lock (_deltaSync) { return _deltas.Count; } } }

    /// <summary>상한 초과로 잘려나간 델타 누적 수.</summary>
    public int TrimmedDeltaCount => Volatile.Read(ref _trimmedDeltas);

    /// <summary>플러시 누적 횟수.</summary>
    public int FlushCount => Volatile.Read(ref _flushCount);

    /// <summary>로드 누적 횟수.</summary>
    public int LoadCount => Volatile.Read(ref _loadCount);

    /// <summary>마지막 플러시 시각.</summary>
    public DateTimeOffset? LastFlushAt { get; private set; }

    /// <summary>마지막 로드 시각.</summary>
    public DateTimeOffset? LastLoadedAt { get; private set; }

    /// <summary>마지막 오류(정상 시 null).</summary>
    public string? LastError { get; private set; }

    /// <summary>디스크 상 파일 크기(바이트 · 없으면 0).</summary>
    public long BytesOnDisk
    {
        get
        {
            try { return File.Exists(StoragePath) ? new FileInfo(StoragePath).Length : 0L; }
            catch { return 0L; }
        }
    }

    /// <summary>HUD 표기용 크기 문자열(InvariantCulture 고정).</summary>
    public string SizeLabel => (BytesOnDisk / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + "KB";

    /// <summary>프롬프트 스냅샷(갱신 시각 내림차순).</summary>
    public IReadOnlyList<OfflinePromptRecord> Prompts
    {
        get
        {
            var arr = new List<OfflinePromptRecord>(_prompts.Count);
            foreach (var kv in _prompts) arr.Add(kv.Value);
            arr.Sort(static (a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
            return arr;
        }
    }

    /// <summary>델타 스냅샷(시퀀스 오름차순 = 재생 순서).</summary>
    public IReadOnlyList<OfflineTimelineDelta> Deltas
    {
        get { lock (_deltaSync) { return _deltas.ToArray(); } }
    }

    /// <summary>프롬프트 저장/갱신(같은 Id 재저장 시 Revision 증가 · 내용이 같으면 멱등 무변경).</summary>
    public OfflinePromptRecord SavePrompt(string id, string scope, string prompt)
    {
        if (_disposed) return new OfflinePromptRecord(id ?? string.Empty, scope ?? string.Empty, prompt ?? string.Empty, 0, DateTimeOffset.UtcNow);

        var key = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id.Trim();
        var text = prompt ?? string.Empty;
        var sc = string.IsNullOrWhiteSpace(scope) ? "default" : scope.Trim();

        if (_prompts.TryGetValue(key, out var existing)
            && string.Equals(existing.Prompt, text, StringComparison.Ordinal)
            && string.Equals(existing.Scope, sc, StringComparison.Ordinal))
        {
            return existing;   // 멱등: 동일 내용 재저장은 리비전을 올리지 않는다
        }

        var record = new OfflinePromptRecord(key, sc, text, (existing?.Revision ?? 0) + 1, DateTimeOffset.UtcNow);
        _prompts[key] = record;

        TrimPromptsIfNeeded();
        MarkDirty();
        return record;
    }

    /// <summary>프롬프트 조회(없으면 null).</summary>
    public OfflinePromptRecord? GetPrompt(string id)
        => string.IsNullOrWhiteSpace(id) ? null : (_prompts.TryGetValue(id.Trim(), out var r) ? r : null);

    /// <summary>프롬프트 삭제.</summary>
    public bool RemovePrompt(string id)
    {
        if (_disposed || string.IsNullOrWhiteSpace(id)) return false;
        if (!_prompts.TryRemove(id.Trim(), out _)) return false;

        MarkDirty();
        return true;
    }

    /// <summary>타임라인 델타 추가(상한 초과 시 가장 오래된 건부터 폐기).</summary>
    public OfflineTimelineDelta AppendDelta(
        string clipId, int trackIndex, double startSeconds, double durationSeconds, string kind, string? payload = null)
    {
        var delta = new OfflineTimelineDelta(
            Interlocked.Increment(ref _sequence),
            string.IsNullOrWhiteSpace(clipId) ? "clip" : clipId.Trim(),
            Math.Max(0, trackIndex),
            Sanitize(startSeconds),
            Math.Max(0d, Sanitize(durationSeconds)),
            string.IsNullOrWhiteSpace(kind) ? "update" : kind.Trim(),
            payload ?? string.Empty,
            DateTimeOffset.UtcNow);

        if (_disposed) return delta;

        lock (_deltaSync)
        {
            _deltas.Add(delta);
            if (_deltas.Count > MaxDeltas)
            {
                var over = _deltas.Count - MaxDeltas;
                _deltas.RemoveRange(0, over);
                Interlocked.Add(ref _trimmedDeltas, over);
            }
        }

        MarkDirty();
        return delta;
    }

    /// <summary>델타 소비(서버 동기화 성공 후 호출). 지정 시퀀스 이하를 제거한다.</summary>
    public int AcknowledgeDeltas(long uptoSequence)
    {
        if (_disposed) return 0;

        int removed;
        lock (_deltaSync)
        {
            removed = _deltas.RemoveAll(d => d.Sequence <= uptoSequence);
        }

        if (removed > 0) MarkDirty();
        return removed;
    }

    /// <summary>인메모리 상태를 디스크에 영속화(원자적 교체).</summary>
    public async Task<bool> FlushAsync(CancellationToken ct = default)
    {
        if (_disposed) return false;

        await _ioGate.WaitAsync(ct).ConfigureAwait(false);
        IsBusy = true;
        Notify();

        try
        {
            var payload = Serialize();
            var dir = Path.GetDirectoryName(StoragePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var temp = StoragePath + ".tmp";
            await File.WriteAllBytesAsync(temp, payload, ct).ConfigureAwait(false);
            File.Move(temp, StoragePath, overwrite: true);

            Interlocked.Increment(ref _flushCount);
            LastFlushAt = DateTimeOffset.UtcNow;
            LastError = null;
            _dirty = false;
            return true;
        }
        catch (OperationCanceledException)
        {
            LastError = "플러시 취소됨";
            return false;
        }
        catch (Exception ex)
        {
            LastError = "플러시 실패: " + ex.Message;   // 인메모리 상태는 그대로 보존
            return false;
        }
        finally
        {
            IsBusy = false;
            _ioGate.Release();
            Notify();
        }
    }

    /// <summary>디스크에서 복원. 파일 없음/손상은 실패가 아니라 빈 상태 유지로 처리한다.</summary>
    public async Task<bool> LoadAsync(CancellationToken ct = default)
    {
        if (_disposed) return false;

        await _ioGate.WaitAsync(ct).ConfigureAwait(false);
        IsBusy = true;
        Notify();

        try
        {
            if (!File.Exists(StoragePath))
            {
                LastError = null;
                return false;
            }

            var bytes = await File.ReadAllBytesAsync(StoragePath, ct).ConfigureAwait(false);
            if (!TryDeserialize(bytes, out var error))
            {
                LastError = error;
                return false;
            }

            Interlocked.Increment(ref _loadCount);
            LastLoadedAt = DateTimeOffset.UtcNow;
            LastError = null;
            _dirty = false;
            return true;
        }
        catch (OperationCanceledException)
        {
            LastError = "로드 취소됨";
            return false;
        }
        catch (Exception ex)
        {
            LastError = "로드 실패: " + ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
            _ioGate.Release();
            Notify();
        }
    }

    /// <summary>전체 비우기(메모리 + 디스크).</summary>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        if (_disposed) return;

        _prompts.Clear();
        lock (_deltaSync) { _deltas.Clear(); }

        await _ioGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(StoragePath)) File.Delete(StoragePath);
            LastError = null;
            _dirty = false;
        }
        catch (Exception ex)
        {
            LastError = "삭제 실패: " + ex.Message;
        }
        finally
        {
            _ioGate.Release();
            Notify();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _autoFlush.Dispose();
        _ioGate.Dispose();
        OnChange = null;
    }

    // ── 직렬화(순수 BCL 바이너리 + SHA-256 꼬리 지문) ──

    private byte[] Serialize()
    {
        var prompts = Prompts;
        var deltas = Deltas;

        using var ms = new MemoryStream(4096);
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Encoding.ASCII.GetBytes(Magic));
            w.Write(FormatVersion);
            w.Write(Volatile.Read(ref _sequence));

            w.Write(prompts.Count);
            foreach (var p in prompts)
            {
                w.Write(p.Id);
                w.Write(p.Scope);
                w.Write(p.Prompt);
                w.Write(p.Revision);
                w.Write(p.UpdatedAt.ToUnixTimeMilliseconds());
            }

            w.Write(deltas.Count);
            foreach (var d in deltas)
            {
                w.Write(d.Sequence);
                w.Write(d.ClipId);
                w.Write(d.TrackIndex);
                w.Write(d.StartSeconds);
                w.Write(d.DurationSeconds);
                w.Write(d.Kind);
                w.Write(d.Payload);
                w.Write(d.At.ToUnixTimeMilliseconds());
            }
        }

        var body = ms.ToArray();
        var digest = SHA256.HashData(body);

        var result = new byte[body.Length + digest.Length];
        Buffer.BlockCopy(body, 0, result, 0, body.Length);
        Buffer.BlockCopy(digest, 0, result, body.Length, digest.Length);
        return result;
    }

    private bool TryDeserialize(byte[] bytes, out string? error)
    {
        error = null;

        if (bytes.Length <= 32 + 12)
        {
            error = "파일이 너무 짧음(손상)";
            return false;
        }

        var bodyLength = bytes.Length - 32;
        var expected = new byte[32];
        Buffer.BlockCopy(bytes, bodyLength, expected, 0, 32);

        var body = new byte[bodyLength];
        Buffer.BlockCopy(bytes, 0, body, 0, bodyLength);

        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(body), expected))
        {
            error = "무결성 지문 불일치(손상/위변조)";
            return false;
        }

        using var ms = new MemoryStream(body, writable: false);
        using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        var magic = Encoding.ASCII.GetString(r.ReadBytes(4));
        if (!string.Equals(magic, Magic, StringComparison.Ordinal))
        {
            error = "매직 헤더 불일치";
            return false;
        }

        var version = r.ReadInt32();
        if (version != FormatVersion)
        {
            error = "포맷 버전 불일치(" + version.ToString(CultureInfo.InvariantCulture) + ")";
            return false;
        }

        var seq = r.ReadInt64();

        var promptCount = r.ReadInt32();
        var loadedPrompts = new List<OfflinePromptRecord>(Math.Max(0, promptCount));
        for (var i = 0; i < promptCount; i++)
        {
            var id = r.ReadString();
            var scope = r.ReadString();
            var text = r.ReadString();
            var rev = r.ReadInt32();
            var at = DateTimeOffset.FromUnixTimeMilliseconds(r.ReadInt64());
            loadedPrompts.Add(new OfflinePromptRecord(id, scope, text, rev, at));
        }

        var deltaCount = r.ReadInt32();
        var loadedDeltas = new List<OfflineTimelineDelta>(Math.Max(0, deltaCount));
        for (var i = 0; i < deltaCount; i++)
        {
            var sequence = r.ReadInt64();
            var clipId = r.ReadString();
            var track = r.ReadInt32();
            var start = r.ReadDouble();
            var dur = r.ReadDouble();
            var kind = r.ReadString();
            var payload = r.ReadString();
            var at = DateTimeOffset.FromUnixTimeMilliseconds(r.ReadInt64());
            loadedDeltas.Add(new OfflineTimelineDelta(sequence, clipId, track, start, dur, kind, payload, at));
        }

        _prompts.Clear();
        foreach (var p in loadedPrompts) _prompts[p.Id] = p;

        lock (_deltaSync)
        {
            _deltas.Clear();
            _deltas.AddRange(loadedDeltas);
        }

        Interlocked.Exchange(ref _sequence, Math.Max(seq, loadedDeltas.Count == 0 ? 0L : loadedDeltas[^1].Sequence));
        return true;
    }

    // ── 내부 유틸 ──

    private void AutoFlushTick()
    {
        if (_disposed || !_dirty || IsBusy) return;
        _ = FlushAsync();   // 백그라운드 자동 저장 — 실패는 LastError 로만 보고
    }

    private void MarkDirty()
    {
        _dirty = true;
        Notify();
    }

    private void Notify() => OnChange?.Invoke();

    private void TrimPromptsIfNeeded()
    {
        if (_prompts.Count <= MaxPrompts) return;

        var ordered = Prompts;   // 최신순
        for (var i = MaxPrompts; i < ordered.Count; i++)
            _prompts.TryRemove(ordered[i].Id, out _);
    }

    private static double Sanitize(double v)
        => double.IsNaN(v) || double.IsInfinity(v) ? 0d : v;

    /// <summary>기본 저장 경로 — 샌드박스 접근이 막히면 실행 디렉터리로 폴백한다.</summary>
    private static string ResolveDefaultPath()
    {
        try
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root)) root = AppContext.BaseDirectory;
            return Path.Combine(root, "PromptFlowStudio", "offline-store.bin");
        }
        catch
        {
            return Path.Combine(AppContext.BaseDirectory, "offline-store.bin");
        }
    }
}
