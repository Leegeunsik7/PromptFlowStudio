using System;
using System.Text.Json;

namespace PromptFlowStudio.Services.Mobile;

/// <summary>
/// [PromptFlowStudio] v5.2.1 Task1 · 비전 AI 이상 → 오프라인 큐 적재 &amp; 원격 동기화 결선 — 순수 BCL(외부 NuGet 0).
///   ▸ <see cref="IMobileVisionAiEngine.OnAnomaly"/>(모션/색상 이상) → <see cref="IMobileOfflineStore.Enqueue"/>(AES-GCM 암호화 로컬 큐) 자동 적재.
///   ▸ <see cref="OfflineSyncPipeline.Configure"/>에 스튜디오 백엔드 API URL 바인딩 → 온라인 복구 시 자동 원자 플러시.
///   ▸ 오프라인/백엔드 미도달은 Graceful(큐 유지). null 방어 · 멱등 <see cref="IDisposable"/>.
/// </summary>
public sealed class TelemetryPipelineWiring : IDisposable
{
    /// <summary>기본 스튜디오 백엔드 베이스 URL(placeholder · 운영 시 <see cref="Configure"/> 로 override). 미도달 시 큐 유지.</summary>
    public const string DefaultApiBaseUrl = "https://studio.angtec.com";

    private readonly IMobileVisionAiEngine _vision;
    private readonly IMobileOfflineStore _store;
    private readonly OfflineSyncPipeline _sync;
    private bool _disposed;

    public TelemetryPipelineWiring(IMobileVisionAiEngine vision, IMobileOfflineStore store, OfflineSyncPipeline sync)
    {
        _vision = vision ?? throw new ArgumentNullException(nameof(vision));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sync = sync ?? throw new ArgumentNullException(nameof(sync));

        _vision.OnAnomaly += OnAnomaly;         // 이상 감지 → 암호화 로컬 큐 적재
        _sync.Configure(DefaultApiBaseUrl);      // 백엔드 바인딩(host 가 Configure 로 실 URL override)
    }

    /// <summary>스튜디오 백엔드 API URL/암호 패스프레이즈 재설정(appsettings/관리자 설정에서 주입).</summary>
    public void Configure(string apiBaseUrl, string? passphrase = null) => _sync.Configure(apiBaseUrl, passphrase);

    private void OnAnomaly(VisionAnomaly a)
    {
        if (_disposed || a is null || !a.Detected) return;
        try
        {
            var json = JsonSerializer.Serialize(new AnomalyRecord(
                "vision-anomaly", a.MotionScore, a.ColorShiftScore, a.Message ?? "", a.TimestampMs), PromptFlowJsonContext.Default.TelemetryAnomalyRecord);
            _store.Enqueue(SyncItemKind.Telemetry, json);   // 온라인이면 자동 플러시 · 오프라인이면 암호화 보관
        }
        catch (Exception ex) { Program.Log("TelemetryWiring.OnAnomaly", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;   // 멱등
        _disposed = true;
        try { _vision.OnAnomaly -= OnAnomaly; } catch { /* 무시 */ }
    }

    /// <summary>이상 텔레메트리 적재 레코드(백엔드 동기화 페이로드).</summary>
    internal sealed record AnomalyRecord(string Type, double Motion, double ColorShift, string Message, long TimestampMs);
}
