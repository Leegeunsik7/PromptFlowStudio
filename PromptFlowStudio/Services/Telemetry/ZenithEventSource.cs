using System;
using System.Diagnostics.Tracing;
using System.Threading;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Services.Telemetry;

/// <summary>
/// [PromptFlowStudio] v15.17.0 LTS Zenith ETW 관제 소스(<c>PromptFlowStudio-Zenith</c>).
///   ▸ 무중단 키 로테이션 횟수 · SIMD 토큰 스캔 소요 Tick · Lock-free Vault 상태 변경을 ETW 로 방출.
///   ▸ 페이로드는 정수만(프로바이더/신호 종류 = 정수 코드) — 원시 <c>WriteEvent(int, long, long)</c>/<c>(int, int, int, int)</c> 오버로드(params 배열·박싱 0).
///   ▸ 리스너 미구독 시 <see cref="EventSource.IsEnabled()"/> 1회 판정으로 조기 반환(0B) — 핫패스에서 상시 호출 가능.
///   ▸ 로테이션 누계는 구독 여부와 무관하게 <see cref="Interlocked"/> 로 집계(HUD/테스트 관측용).
/// 수집: <c>dotnet-trace collect --providers PromptFlowStudio-Zenith</c> 또는 PerfView/WPR.
/// </summary>
[EventSource(Name = SourceName)]
public sealed class ZenithEventSource : EventSource
{
    public const string SourceName = "PromptFlowStudio-Zenith";

    public const int KeyRotatedId = 1;
    public const int SimdScanId = 2;
    public const int VaultStateId = 3;

    public static class Keywords
    {
        public const EventKeywords Rotation = (EventKeywords)0x1;
        public const EventKeywords Simd = (EventKeywords)0x2;
        public const EventKeywords Vault = (EventKeywords)0x4;
    }

    /// <summary>프로세스 단일 인스턴스.</summary>
    public static readonly ZenithEventSource Log = new();

    private long _rotations;
    private long _simdScans;
    private long _vaultChanges;

    private ZenithEventSource() { }

    public long RotationCount => Interlocked.Read(ref _rotations);
    public long SimdScanCount => Interlocked.Read(ref _simdScans);
    public long VaultChangeCount => Interlocked.Read(ref _vaultChanges);

    // ══ 이벤트(매니페스트 정의) ═════════════════════════════════════════
    [Event(KeyRotatedId, Level = EventLevel.Informational, Keywords = Keywords.Rotation)]
    public void KeyRotated(long provider, long totalRotations)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Rotation)) WriteEvent(KeyRotatedId, provider, totalRotations);
    }

    [Event(SimdScanId, Level = EventLevel.Verbose, Keywords = Keywords.Simd)]
    public void SimdScan(long elapsedTicks, long textLength)
    {
        if (IsEnabled(EventLevel.Verbose, Keywords.Simd)) WriteEvent(SimdScanId, elapsedTicks, textLength);
    }

    [Event(VaultStateId, Level = EventLevel.Informational, Keywords = Keywords.Vault)]
    public void VaultStateChanged(int kind, int provider, int ok)
    {
        if (IsEnabled(EventLevel.Informational, Keywords.Vault)) WriteEvent(VaultStateId, kind, provider, ok);
    }

    // ══ 결선 헬퍼(비이벤트 · 0B) ═══════════════════════════════════════
    /// <summary>WanApiService 무중단 로테이션 1회 성공.</summary>
    [NonEvent]
    public void OnKeyRotated(string? provider)
    {
        var total = Interlocked.Increment(ref _rotations);
        if (IsEnabled()) KeyRotated(ProviderCode(provider), total);
    }

    /// <summary>SIMD 스캔 계측 활성 여부 — 비활성이면 호출부는 타임스탬프조차 읽지 않는다.</summary>
    public bool SimdTracing => IsEnabled(EventLevel.Verbose, Keywords.Simd);

    /// <summary>SIMD 토큰 스캔 1회 소요(<see cref="System.Diagnostics.Stopwatch"/> Tick).</summary>
    [NonEvent]
    public void OnSimdScan(long elapsedTicks, int textLength)
    {
        Interlocked.Increment(ref _simdScans);
        SimdScan(elapsedTicks, textLength);
    }

    /// <summary>Lock-free Vault 신호(키 변경/영구 저장 결과).</summary>
    [NonEvent]
    public void OnVaultSignal(VaultSignalKind kind, string? provider, bool ok)
    {
        Interlocked.Increment(ref _vaultChanges);
        if (IsEnabled()) VaultStateChanged((int)kind, ProviderCode(provider), ok ? 1 : 0);
    }

    /// <summary>프로바이더 정수 코드(0=없음 · 1=DashScope · 2=SiliconFlow · 3=Custom · 99=기타) — 문자열 페이로드 회피.</summary>
    public static int ProviderCode(string? provider)
    {
        if (provider is null) return 0;
        if (string.Equals(provider, WanTokenVault.DashScope, StringComparison.Ordinal)) return 1;
        if (string.Equals(provider, WanTokenVault.SiliconFlow, StringComparison.Ordinal)) return 2;
        if (string.Equals(provider, WanTokenVault.Custom, StringComparison.Ordinal)) return 3;
        return 99;
    }
}
