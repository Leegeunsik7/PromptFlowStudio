using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Mobile;

/// <summary>동기화 항목 종류 — PLC 텔레메트리 수집 / 스토리보드 수정안 / Wan 3.0 렌더 요청(v5.4.1 · enum 값 말미 추가 → 구 JSON 역호환).</summary>
public enum SyncItemKind { Telemetry, StoryboardEdit, WanRender }

/// <summary>오프라인 큐 항목 — 식별자·종류·페이로드(JSON)·적재시각(ms).</summary>
public sealed record SyncItem(string Id, SyncItemKind Kind, string PayloadJson, long QueuedAtMs);

/// <summary>동기화 결과 — 성공 여부·전송 건수·잔여 건수·사유.</summary>
public sealed record SyncFlushResult(bool Ok, int Sent, int Remaining, string Message)
{
    public static readonly SyncFlushResult Empty = new(true, 0, 0, "동기화할 항목 없음");
}

/// <summary>
/// [PromptFlowStudio] v5.2 Task2 · 오프라인-퍼스트 로컬 동기화 큐 시임 — 순수 BCL(외부 NuGet 0).
///   ▸ 네트워크 단선에도 텔레메트리/스토리보드 수정안이 유실되지 않도록 <b>AES-GCM 암호화 JSON</b>으로 로컬 영속(Thread-Safe).
///   ▸ 온라인 복구 시 백엔드로 <b>원자적 자동 동기화</b> — 업로더가 반환한 <b>성공 ID 만</b> 제거(부분 성공 안전 · 그 사이 새 항목 보존).
///   ▸ 데스크톱은 상시 온라인 가정, 모바일은 <c>Connectivity</c> 변경 이벤트를 <c>#if ANDROID||IOS</c> 가드에서 구독해 <see cref="SetOnline"/> 구동.
/// </summary>
public interface IMobileOfflineStore : IDisposable
{
    /// <summary>현재 온라인 여부.</summary>
    bool IsOnline { get; }
    /// <summary>미전송 큐 건수.</summary>
    int PendingCount { get; }
    /// <summary>큐 건수 변경 방송(UI 뱃지).</summary>
    event Action<int>? OnPendingChanged;
    /// <summary>동기화 완료 방송.</summary>
    event Action<SyncFlushResult>? OnFlushed;
    /// <summary>오프라인→온라인 복구 방송(자동 동기화 직전 · 파이프라인이 토스트/로그).</summary>
    event Action? OnNetworkRestored;
    /// <summary>백엔드 업로더 등록 — 배치를 받아 <b>실제 업로드에 성공한 항목 ID 집합</b>을 반환(부분 성공 지원 · 원자적 제거).</summary>
    void ConfigureUploader(Func<IReadOnlyList<SyncItem>, CancellationToken, Task<IReadOnlyCollection<string>>>? uploader);
    /// <summary>항목을 큐에 적재(즉시 암호화 영속). 온라인이면 자동 동기화 시도.</summary>
    void Enqueue(SyncItemKind kind, string payloadJson);
    /// <summary>온라인 상태 설정 — 오프라인→온라인 전이 시 자동 동기화 트리거(플랫폼 Connectivity 가 호출).</summary>
    void SetOnline(bool online);
    /// <summary>큐를 백엔드로 원자적 동기화(단일 진입 · 성공 ID 만 제거).</summary>
    Task<SyncFlushResult> FlushAsync(CancellationToken ct = default);
}

/// <summary>순수 BCL 오프라인 큐 구현 — 데스크톱/모바일 공용(모바일은 ctor 에서 Connectivity 자동 구독).</summary>
public sealed class MobileOfflineStore : IMobileOfflineStore
{
    private const string FileName = "offline_queue.enc";
    private static readonly byte[] Salt = Encoding.ASCII.GetBytes("PromptFlowStudio.v5.2.offline.salt");

    private readonly string _path;
    private readonly byte[] _key;
    private readonly object _lock = new();
    private readonly List<SyncItem> _queue = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private Func<IReadOnlyList<SyncItem>, CancellationToken, Task<IReadOnlyCollection<string>>>? _uploader;
    private volatile bool _online;
    private bool _disposed;

    public bool IsOnline => _online;
    public int PendingCount { get { lock (_lock) return _queue.Count; } }
    public event Action<int>? OnPendingChanged;
    public event Action<SyncFlushResult>? OnFlushed;
    public event Action? OnNetworkRestored;

    public MobileOfflineStore(string? storageDir = null, string passphrase = "pfs-offline-default")
    {
        var dir = string.IsNullOrWhiteSpace(storageDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", "sync")
            : storageDir!;
        try { Directory.CreateDirectory(dir); } catch { /* 생성 실패는 Persist 에서 재시도 */ }
        _path = Path.Combine(dir, FileName);
        // PBKDF2 로 32바이트 키 파생(디바이스 패스프레이즈 — 운영 시 키스토어 연동 권장).
        _key = Rfc2898DeriveBytes.Pbkdf2(passphrase ?? "", Salt, 100_000, HashAlgorithmName.SHA256, 32);
        LoadFromDisk();
        AttachPlatformConnectivity();
    }

    public void ConfigureUploader(Func<IReadOnlyList<SyncItem>, CancellationToken, Task<IReadOnlyCollection<string>>>? uploader)
        => _uploader = uploader;

    public void Enqueue(SyncItemKind kind, string payloadJson)
    {
        if (_disposed) return;
        var item = new SyncItem(Guid.NewGuid().ToString("N"), kind, payloadJson ?? "", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        int count;
        lock (_lock) { _queue.Add(item); count = _queue.Count; }
        Persist();
        OnPendingChanged?.Invoke(count);
        if (_online) _ = TryAutoFlush();   // 온라인이면 즉시 동기화 시도(fire-and-forget · 예외 흡수)
    }

    public void SetOnline(bool online)
    {
        var was = _online;
        _online = online;
        if (online && !was)
        {
            OnNetworkRestored?.Invoke();   // 복구 통지(파이프라인이 토스트/로그)
            _ = TryAutoFlush();            // 자동 원자 동기화
        }
    }

    private async Task TryAutoFlush()
    {
        try { await FlushAsync().ConfigureAwait(false); }
        catch (Exception ex) { Program.Log("OfflineStore.AutoFlush", ex); }
    }

    public async Task<SyncFlushResult> FlushAsync(CancellationToken ct = default)
    {
        if (_disposed) return SyncFlushResult.Empty;
        var uploader = _uploader;
        if (uploader is null) return new(false, 0, PendingCount, "업로더 미구성");
        if (!_online) return new(false, 0, PendingCount, "오프라인 — 큐 보관 중");

        if (!await _flushGate.WaitAsync(0, ct).ConfigureAwait(false))
            return new(false, 0, PendingCount, "이미 동기화 진행 중");   // 단일 진입(중복 방지)
        try
        {
            List<SyncItem> batch;
            lock (_lock) batch = _queue.ToList();
            if (batch.Count == 0) return SyncFlushResult.Empty;

            IReadOnlyCollection<string> succeeded;
            try { succeeded = await uploader(batch, ct).ConfigureAwait(false) ?? Array.Empty<string>(); }
            catch (OperationCanceledException) { return new(false, 0, batch.Count, "취소됨"); }
            catch (Exception ex) { Program.Log("OfflineStore.Upload", ex); succeeded = Array.Empty<string>(); }   // 5xx/타임아웃 → 큐 유지

            if (succeeded.Count == 0) return new(false, 0, PendingCount, "업로드 실패/보류 — 큐 유지(재시도 대기)");

            // 원자적: 실제 성공한 ID 만 제거(부분 성공 안전 · 업로드 도중 새로 들어온 항목 보존).
            int remaining;
            lock (_lock)
            {
                var sent = succeeded.ToHashSet(StringComparer.Ordinal);
                _queue.RemoveAll(q => sent.Contains(q.Id));
                remaining = _queue.Count;
            }
            Persist();
            OnPendingChanged?.Invoke(remaining);
            var result = new SyncFlushResult(true, succeeded.Count, remaining,
                succeeded.Count == batch.Count ? $"{succeeded.Count}건 동기화 완료" : $"{succeeded.Count}/{batch.Count}건 동기화(잔여 재시도)");
            OnFlushed?.Invoke(result);
            return result;
        }
        finally { _flushGate.Release(); }
    }

    // ── AES-GCM 암호화 영속(원자적 파일 교체) ──

    private void Persist()
    {
        try
        {
            List<SyncItem> snapshot;
            lock (_lock) snapshot = _queue.ToList();
            var json = JsonSerializer.SerializeToUtf8Bytes(snapshot, PromptFlowJsonContext.Default.SyncItemList);

            var nonce = RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16];
            var cipher = new byte[json.Length];
            using (var gcm = new AesGcmCompat(_key, 16)) gcm.Encrypt(nonce, json, cipher, tag);

            var blob = new byte[12 + 16 + cipher.Length];
            Buffer.BlockCopy(nonce, 0, blob, 0, 12);
            Buffer.BlockCopy(tag, 0, blob, 12, 16);
            Buffer.BlockCopy(cipher, 0, blob, 28, cipher.Length);

            var tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, blob);
            File.Move(tmp, _path, overwrite: true);   // 원자적 교체(부분 기록 방지)
        }
        catch (Exception ex) { Program.Log("OfflineStore.Persist", ex); }
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var blob = File.ReadAllBytes(_path);
            if (blob.Length < 28) return;   // 손상 — 무시(빈 큐)
            var nonce = blob.AsSpan(0, 12).ToArray();
            var tag = blob.AsSpan(12, 16).ToArray();
            var cipher = blob.AsSpan(28).ToArray();
            var plain = new byte[cipher.Length];
            using (var gcm = new AesGcmCompat(_key, 16)) gcm.Decrypt(nonce, cipher, tag, plain);
            var items = JsonSerializer.Deserialize(plain, PromptFlowJsonContext.Default.SyncItemList);
            if (items is not null) lock (_lock) { _queue.Clear(); _queue.AddRange(items); }
        }
        catch (Exception ex) { Program.Log("OfflineStore.Load", ex); /* 복호화 실패 → 빈 큐 Graceful */ }
    }

    // ── 플랫폼 Connectivity(모바일 전용 · 데스크톱은 상시 온라인) ──

    private void AttachPlatformConnectivity()
    {
#if ANDROID || IOS
        try
        {
            _online = Microsoft.Maui.Networking.Connectivity.Current.NetworkAccess == Microsoft.Maui.Networking.NetworkAccess.Internet;
            Microsoft.Maui.Networking.Connectivity.Current.ConnectivityChanged += OnConnectivityChanged;
        }
        catch { _online = true; /* Connectivity 초기화 전 — 온라인 가정(Graceful) */ }
#else
        _online = true;   // 데스크톱: 상시 온라인
#endif
    }

#if ANDROID || IOS
    private void OnConnectivityChanged(object? sender, Microsoft.Maui.Networking.ConnectivityChangedEventArgs e)
        => SetOnline(e.NetworkAccess == Microsoft.Maui.Networking.NetworkAccess.Internet);
#endif

    private void DetachPlatformConnectivity()
    {
#if ANDROID || IOS
        try { Microsoft.Maui.Networking.Connectivity.Current.ConnectivityChanged -= OnConnectivityChanged; } catch { /* 무시 */ }
#endif
    }

    public void Dispose()
    {
        if (_disposed) return;   // 멱등
        _disposed = true;
        DetachPlatformConnectivity();
        try { _flushGate.Dispose(); } catch { }
        OnPendingChanged = null;
        OnFlushed = null;
        OnNetworkRestored = null;
    }
}
