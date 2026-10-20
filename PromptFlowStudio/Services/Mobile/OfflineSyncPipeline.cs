using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Mobile;

/// <summary>
/// [PromptFlowStudio] v5.2.1 Task2 · 오프라인 큐 ➔ 원격 동기화 업로더 결선 — 순수 BCL(외부 NuGet 0).
///   ▸ <see cref="IMobileOfflineStore"/> 의 업로더로 바인딩되어, 네트워크 복구 시 큐를 백엔드로 전송한다.
///   ▸ 전송은 <see cref="RemoteRenderClient"/> 헬퍼로 <b>AES-256 암호화</b>(앱 표준 전송 규약 재사용) 후 HTTP POST.
///   ▸ HTTP 5xx/타임아웃/예외 시 해당 항목을 <b>큐에 유지</b>하고, <b>성공한 ID 만</b> 반환해 스토어가 원자적으로 제거(안전 재시도).
/// </summary>
public sealed class OfflineSyncPipeline : IDisposable
{
    private readonly IMobileOfflineStore _store;
    private readonly HttpClient _http;
    private byte[] _key;
    private string? _backendUrl;
    private bool _bound;
    private bool _disposed;

    /// <summary>네트워크 복구 릴레이(UI 토스트).</summary>
    public event Action? OnNetworkRestored;
    /// <summary>동기화 완료 릴레이.</summary>
    public event Action<SyncFlushResult>? OnFlushed;

    public OfflineSyncPipeline(IMobileOfflineStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _key = RemoteRenderClient.DeriveKey("pfs-sync-default");
        Bind();
    }

    /// <summary>백엔드 주소/암호 패스프레이즈 구성(미구성 시 전송 보류 · 큐 유지).</summary>
    public void Configure(string? backendUrl, string? passphrase = null)
    {
        _backendUrl = string.IsNullOrWhiteSpace(backendUrl) ? null : backendUrl!.Trim();
        if (!string.IsNullOrWhiteSpace(passphrase)) _key = RemoteRenderClient.DeriveKey(passphrase!);
    }

    private void Bind()
    {
        if (_bound) return;
        _bound = true;
        _store.ConfigureUploader(UploadBatchAsync);   // 스토어 업로더로 결선
        _store.OnNetworkRestored += HandleRestored;
        _store.OnFlushed += HandleFlushed;
    }

    private void HandleRestored() => OnNetworkRestored?.Invoke();
    private void HandleFlushed(SyncFlushResult r) => OnFlushed?.Invoke(r);

    // 배치 업로드 — 성공 ID 만 반환(순서 보존 · 첫 실패에서 중단 → 재시도 안전).
    private async Task<IReadOnlyCollection<string>> UploadBatchAsync(IReadOnlyList<SyncItem> batch, CancellationToken ct)
    {
        var ok = new List<string>();
        if (batch is null) return ok;
        foreach (var item in batch)
        {
            if (ct.IsCancellationRequested) break;
            bool sent;
            try { sent = await SendOneAsync(item, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Program.Log("OfflineSync.Send", ex); sent = false; }   // HttpRequestException/타임아웃
            if (sent) ok.Add(item.Id);
            else break;   // 5xx/단선 → 이후 항목은 다음 재시도로(순서 보존 · 큐 유지)
        }
        return ok;
    }

    private async Task<bool> SendOneAsync(SyncItem item, CancellationToken ct)
    {
        if (_backendUrl is null) return false;   // 백엔드 미구성 → 보류(큐 유지)

        // RemoteRenderClient 헬퍼로 AES-256 암호화(앱 표준 전송 규약 재사용).
        var plain = Encoding.UTF8.GetBytes(item.PayloadJson ?? "");
        var cipher = RemoteRenderClient.Encrypt(plain, _key, out var iv);
        var envelope = new SyncEnvelope(
            item.Id, item.Kind.ToString(), item.QueuedAtMs,
            Convert.ToBase64String(iv), Convert.ToBase64String(cipher),
            RemoteRenderClient.Sha256Hex(plain));

        var path = item.Kind switch
        {
            SyncItemKind.Telemetry => "api/telemetry/ingest",
            SyncItemKind.WanRender => "api/wan/render",       // v5.4.1 Wan 3.0 렌더 큐 → 복구 시 자동 재전송
            _ => "api/storyboard/sync",
        };
        var url = RemoteRenderClient.BuildEndpoint(_backendUrl, path);
        using var body = new StringContent(JsonSerializer.Serialize(envelope, PromptFlowJsonContext.Default.OfflineSyncEnvelope), Encoding.UTF8, "application/json");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));   // 요청 1건 상한(무응답 방어)
        using var resp = await _http.PostAsync(url, body, timeoutCts.Token).ConfigureAwait(false);
        return resp.IsSuccessStatusCode;   // 5xx → false → 큐 유지(재시도)
    }

    public void Dispose()
    {
        if (_disposed) return;   // 멱등
        _disposed = true;
        try
        {
            _store.OnNetworkRestored -= HandleRestored;
            _store.OnFlushed -= HandleFlushed;
            _store.ConfigureUploader(null);
        }
        catch { /* 무시 */ }
        _http.Dispose();
        OnNetworkRestored = null;
        OnFlushed = null;
    }

    /// <summary>암호화 동기화 전송 본문 — 항목 메타 + Base64 IV/Cipher + 체크섬.</summary>
    internal sealed record SyncEnvelope(string Id, string Kind, long QueuedAtMs, string Iv, string Cipher, string Checksum);
}
