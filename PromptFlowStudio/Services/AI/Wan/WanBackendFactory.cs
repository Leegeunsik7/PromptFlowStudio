using System;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.AI.Wan;

/// <summary>현재 선택된 백엔드 스냅샷 — HUD 표시/캐시 판단용.</summary>
public sealed record WanBackendStatus(WanBackendKind Active, bool ComfyUiAvailable, DateTimeOffset CheckedAt);

/// <summary>
/// v10.5.0-F2 Wan 백엔드 팩토리 — <see cref="ComfyUiWanConnector"/> 헬스체크(TTL 5초 캐시로 과다 프로브 방지) 결과에
/// 따라 <see cref="IWanBackendConnector"/>를 자율 선택한다: 가용하면 원격 ComfyUI, 미가동이면 즉시(무조건 가용한)
/// <see cref="DirectMlWanConnector"/> 인프로세스 폴백으로 전환 — 상위(호출자)는 반환된 인터페이스만 사용하므로
/// 백엔드 교체를 인지할 필요가 없다. <see cref="DirectMlWanConnector"/> 의존이 전이돼 데스크톱 전용(Mobile 미등록).
/// </summary>
public sealed class WanBackendFactory
{
    private static readonly TimeSpan ProbeTtl = TimeSpan.FromSeconds(5);

    private readonly ComfyUiWanConnector _comfy;
    private readonly DirectMlWanConnector _directMl;
    private readonly object _gate = new();
    private WanBackendStatus? _cached;
    private long _comfySelections, _directMlSelections, _probeCount;

    public event Action? OnChange;
    public string? LastAction { get; private set; }
    public long ComfySelections => Interlocked.Read(ref _comfySelections);
    public long DirectMlSelections => Interlocked.Read(ref _directMlSelections);
    public long ProbeCount => Interlocked.Read(ref _probeCount);
    public WanBackendStatus? LastStatus { get { lock (_gate) return _cached; } }

    public WanBackendFactory(ComfyUiWanConnector comfy, DirectMlWanConnector directMl)
    {
        _comfy = comfy;
        _directMl = directMl;
    }

    /// <summary>가용성에 따라 활성 커넥터를 반환(TTL 캐시 우선 — 매 호출마다 재프로브하지 않음).</summary>
    public async Task<IWanBackendConnector> GetActiveConnectorAsync(CancellationToken ct = default)
    {
        var status = await GetStatusAsync(ct).ConfigureAwait(false);
        if (status.Active == WanBackendKind.ComfyUi) { Interlocked.Increment(ref _comfySelections); return _comfy; }
        Interlocked.Increment(ref _directMlSelections);
        return _directMl;
    }

    /// <summary>현재 상태 조회(TTL 만료 시에만 실제 헬스체크 재실행).</summary>
    public async Task<WanBackendStatus> GetStatusAsync(CancellationToken ct = default)
    {
        lock (_gate) { if (_cached is { } c && DateTimeOffset.UtcNow - c.CheckedAt < ProbeTtl) return c; }

        Interlocked.Increment(ref _probeCount);
        var available = await _comfy.IsAvailableAsync(ct).ConfigureAwait(false);
        var status = new WanBackendStatus(available ? WanBackendKind.ComfyUi : WanBackendKind.DirectMlInProcess, available, DateTimeOffset.UtcNow);

        lock (_gate)
        {
            _cached = status;
            LastAction = available ? "ComfyUI 가용 — 원격 백엔드 선택" : "ComfyUI 미가동 — DirectML 인프로세스로 자율 폴백";
        }
        OnChange?.Invoke();
        return status;
    }

    /// <summary>다음 호출에서 캐시를 무시하고 강제 재프로브(HUD "지금 재확인" 버튼용).</summary>
    public void ForceRecheck()
    {
        lock (_gate) _cached = null;
        OnChange?.Invoke();
    }
}
