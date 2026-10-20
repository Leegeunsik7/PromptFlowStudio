using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace PromptFlowStudio.Services.Integration;

/// <summary>마스터 모듈 계층 — 시동 순서이자 관제 그룹(Core → Distributed → Security → Intelligence → Edge).</summary>
public enum MasterTier { Core, Distributed, Security, Intelligence, Edge }

/// <summary>마스터 모듈 상태(HUD 신호등).</summary>
public enum MasterModuleState { Down, Starting, Ready, Degraded, Stopped }

/// <summary>전체 시스템 상태 롤업.</summary>
public enum MasterSystemState { Idle, Booting, Operational, Degraded, Critical }

/// <summary>프로브 1회 결과(불변 struct · 상태 갱신 시 힙 할당 0).</summary>
public readonly record struct MasterModuleProbe(MasterModuleState State, string Metric, string? Note = null);

/// <summary>모듈 스냅샷(불변 · HUD 표출).</summary>
public sealed record MasterModuleView(string Name, MasterTier Tier, MasterModuleState State, string Metric, string? Note);

/// <summary>일괄 시동 리포트(불변).</summary>
public sealed record MasterBootReport(DateTimeOffset At, int Total, int Ready, int Degraded, int Down, double DurationMs)
{
    public double ReadinessRatio => Total <= 0 ? 0d : (double)Ready / Total;
    public bool AllReady => Total > 0 && Ready == Total;
}

/// <summary>
/// [PromptFlow Studio v9.0.1] 엔터프라이즈 마스터 오케스트레이터(관찰 가능 싱글턴 · 순수 BCL · 외부 NuGet 0 · IDisposable).
///   ▸ v8.0~v8.9 전 모듈(스웜 · 오토스케일러 · CRDT 협업 · SSO · Raft 합의 · AI 자가진단 · 지식 그래프 · MCP · 무할당 봉인 등)을
///     <see cref="MasterTier"/> 계층별 디스크립터로 등록하고 <see cref="StartAllAsync"/> 로 계층 순차 시동 → <see cref="StopAll"/> 로 역순 정지한다.
///   ▸ 각 모듈은 <see cref="MasterModuleProbe"/> 델리게이트로만 관찰한다 — 오케스트레이터가 모듈 내부 상태를 소유/변조하지 않아 기존 실 로직은 무변경(제1헌장).
///   ▸ 5초 <see cref="System.Threading.Timer"/> 로 전 모듈을 재프로브해 <see cref="SystemState"/>(Operational/Degraded/Critical)를 롤업.
///   ▸ 미등록(미주입) 모듈은 조용히 건너뛴다 — 데스크톱/모바일 프로파일 차이로 일부 서비스가 없어도 시동은 완주한다(제5헌장).
///   ⚠ <see cref="OnChange"/> 는 Timer(백그라운드) 스레드에서 온다 — 컴포넌트는 InvokeAsync(StateHasChanged)로 마샬링(제3헌장).
/// </summary>
public sealed class EnterpriseMasterOrchestrator : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private const int EdgeDefaultPort = 8787;
    private const int CollabDefaultPort = 8788;

    private readonly List<ModuleDescriptor> _modules = new();
    private readonly object _gate = new();
    private readonly System.Threading.Timer _timer;
    private readonly DateTimeOffset _createdAt = DateTimeOffset.Now;
    private bool _disposed;

    public EnterpriseMasterOrchestrator(IServiceProvider? services = null)
    {
        if (services is not null) WireCoreModules(services);
        _timer = new System.Threading.Timer(_ => SafeRefresh(), null, TimeSpan.FromSeconds(6), RefreshInterval);
    }

    /// <summary>상태 변경 방송(구독: 컴포넌트 OnInitialized · 해지: Dispose).</summary>
    public event Action? OnChange;
    private void Notify() => OnChange?.Invoke();

    // ── 관측 지표(HUD) ──
    public int ModuleCount { get { lock (_gate) return _modules.Count; } }
    public int ReadyCount { get; private set; }
    public int DegradedCount { get; private set; }
    public int DownCount { get; private set; }
    public bool IsBooting { get; private set; }
    public string? CurrentModule { get; private set; }
    public MasterSystemState SystemState { get; private set; } = MasterSystemState.Idle;
    public MasterBootReport? LastBootReport { get; private set; }
    public string? LastAction { get; private set; }
    public DateTimeOffset? LastBootAt { get; private set; }
    public TimeSpan Uptime => DateTimeOffset.Now - _createdAt;

    /// <summary>준비 비율(Ready / 전체) — 관제 게이지.</summary>
    public double ReadinessRatio { get { var n = ModuleCount; return n <= 0 ? 0d : (double)ReadyCount / n; } }

    /// <summary>모듈 스냅샷(계층 → 이름 순 · 스냅샷 시점에만 할당).</summary>
    public IReadOnlyList<MasterModuleView> Modules
    {
        get
        {
            lock (_gate)
            {
                var list = new List<MasterModuleView>(_modules.Count);
                foreach (var m in _modules) list.Add(new MasterModuleView(m.Name, m.Tier, m.State, m.Metric, m.Note));
                list.Sort(static (x, y) => x.Tier != y.Tier ? x.Tier.CompareTo(y.Tier) : string.CompareOrdinal(x.Name, y.Name));
                return list;
            }
        }
    }

    /// <summary>계층별 (준비/전체) 집계 — HUD 계층 칩.</summary>
    public IReadOnlyList<(MasterTier Tier, int Ready, int Total)> TierRollup
    {
        get
        {
            lock (_gate)
            {
                var result = new List<(MasterTier, int, int)>(5);
                foreach (MasterTier tier in Enum.GetValues<MasterTier>())
                {
                    var total = 0;
                    var ready = 0;
                    foreach (var m in _modules)
                    {
                        if (m.Tier != tier) continue;
                        total++;
                        if (m.State == MasterModuleState.Ready) ready++;
                    }
                    if (total > 0) result.Add((tier, ready, total));
                }
                return result;
            }
        }
    }

    // ── 등록 ──

    /// <summary>모듈 디스크립터 등록(동일 이름 재등록 시 교체 — 멱등).</summary>
    public void RegisterModule(string name, MasterTier tier, Func<MasterModuleProbe> probe,
        Func<CancellationToken, Task>? startAsync = null, Action? stop = null)
    {
        if (string.IsNullOrWhiteSpace(name) || probe is null) return;
        lock (_gate)
        {
            var descriptor = new ModuleDescriptor(name, tier, probe, startAsync, stop);
            var i = _modules.FindIndex(m => string.Equals(m.Name, name, StringComparison.Ordinal));
            if (i >= 0) _modules[i] = descriptor; else _modules.Add(descriptor);
        }
    }

    /// <summary>주입 가능한 v8 서비스만 골라 디스크립터로 등록(미주입 타입은 건너뜀).</summary>
    private void WireCoreModules(IServiceProvider sp)
    {
        // ── Core: 관제 · 게이트웨이 · REST Edge ──
        if (sp.GetService<TelemetryAggregator>() is { } tel)
            RegisterModule("텔레메트리 집계", MasterTier.Core, () => new MasterModuleProbe(
                MasterModuleState.Ready, $"{tel.CurrentRps} RPS · {tel.AvgLatencyMs.ToString("0", CultureInfo.InvariantCulture)}ms"));

        if (sp.GetService<ApiGatewayEngine>() is { } gw)
            RegisterModule("API 게이트웨이", MasterTier.Core, () => new MasterModuleProbe(
                gw.Denied429Count > 0 ? MasterModuleState.Degraded : MasterModuleState.Ready,
                $"{gw.TotalRequests}건 · 거부 {gw.Denied401Count + gw.Denied429Count}",
                gw.Denied429Count > 0 ? "레이트 리밋 포화" : null));

        if (sp.GetService<EmbeddedHttpServer>() is { } edge)
            RegisterModule("REST Edge 서버", MasterTier.Core,
                () => new MasterModuleProbe(edge.IsListening ? MasterModuleState.Ready : MasterModuleState.Stopped,
                    edge.IsListening ? $"{edge.RequestCount}건 수신" : "중지됨", edge.LastError),
                _ => { edge.Start(EdgeDefaultPort); return Task.CompletedTask; },
                edge.Stop);

        if (sp.GetService<WebhookDispatcher>() is { } hook)
            RegisterModule("Webhook 디스패처", MasterTier.Core, () => new MasterModuleProbe(
                hook.QueuedCount > 64 ? MasterModuleState.Degraded : MasterModuleState.Ready, $"대기 {hook.QueuedCount}"));

        // ── Distributed: 그리드 · 합의 · 리전 · 연합 · 협업 ──
        if (sp.GetService<Wan.DistributedRenderGrid>() is { } grid)
            RegisterModule("분산 렌더 그리드", MasterTier.Distributed, () => new MasterModuleProbe(
                grid.Nodes.Any() ? MasterModuleState.Ready : MasterModuleState.Down, $"노드 {grid.Nodes.Count()}"));

        if (sp.GetService<Wan.CloudEdgeAutoScaler>() is { } scaler)
            RegisterModule("클라우드 오토스케일러", MasterTier.Distributed, () => new MasterModuleProbe(
                scaler.Enabled ? MasterModuleState.Ready : MasterModuleState.Stopped,
                $"{scaler.ActiveInstances}/{scaler.MaxNodes} 인스턴스", scaler.ProviderConfigured ? null : "프로바이더 미구성(스텁)"));

        if (sp.GetService<DistributedConsensusEngine>() is { } raft)
            RegisterModule("Raft 합의 엔진", MasterTier.Distributed, () => new MasterModuleProbe(
                raft.HasLeader ? MasterModuleState.Ready : MasterModuleState.Degraded,
                $"T{raft.CurrentTerm} · 커밋 {raft.CommitIndex} · 생존 {raft.AliveCount}/{raft.NodeCount}",
                raft.HasLeader ? null : "리더 부재(선출 중)"));

        if (sp.GetService<MultiRegionFailoverEngine>() is { } region)
            RegisterModule("멀티 리전 페일오버", MasterTier.Distributed, () => new MasterModuleProbe(
                region.QuorumOk ? MasterModuleState.Ready : MasterModuleState.Degraded,
                $"{region.ActiveRegionId} · 정상 {region.HealthyCount}/{region.RegionCount}", region.QuorumOk ? null : "쿼럼 미달"));

        if (sp.GetService<Agents.EdgeSwarmFederation>() is { } fed)
            RegisterModule("엣지 스웜 연합", MasterTier.Distributed, () => new MasterModuleProbe(
                fed.PeerCount == 0 ? MasterModuleState.Stopped : fed.IsConverged ? MasterModuleState.Ready : MasterModuleState.Degraded,
                $"피어 {fed.LinkedCount}/{fed.PeerCount} · 합의 {(fed.AgreementRatio * 100).ToString("0", CultureInfo.InvariantCulture)}%"));

        if (sp.GetService<LiveCollaborationHub>() is { } collab)
            RegisterModule("CRDT 실시간 협업", MasterTier.Distributed,
                () => new MasterModuleProbe(collab.IsListening ? MasterModuleState.Ready : MasterModuleState.Stopped,
                    collab.IsListening ? $"피어 {collab.ConnectedPeers} · 델타 {collab.DeltasApplied}" : "중지됨", collab.LastError),
                _ => { collab.Start(CollabDefaultPort); return Task.CompletedTask; },
                collab.Stop);

        // ── Security: C2PA · 원장 · SSO · ZTNA · 정책 · 감사 · 재해복구 ──
        if (sp.GetService<C2PaWatermarkEngine>() is { } c2pa)
            RegisterModule("C2PA 워터마크", MasterTier.Security, () => new MasterModuleProbe(
                c2pa.TamperCount == 0 ? MasterModuleState.Ready : MasterModuleState.Degraded,
                $"서명 {c2pa.SignedCount} · 검증 {c2pa.VerifiedOk}", c2pa.TamperCount > 0 ? c2pa.LastAlert : null));

        if (sp.GetService<Inference.QuantumResistantLedgerSigner>() is { } ledger)
            RegisterModule("PQC WORM 원장", MasterTier.Security, () => new MasterModuleProbe(
                ledger.TamperCount == 0 ? MasterModuleState.Ready : MasterModuleState.Degraded,
                $"{ledger.EntryCount}건 · 서명 {ledger.SignedCount}", ledger.TamperCount > 0 ? "체인 변조 감지" : null));

        if (sp.GetService<EnterpriseSsoEngine>() is { } sso)
            RegisterModule("엔터프라이즈 SSO", MasterTier.Security, () => new MasterModuleProbe(
                sso.RsaConfigured || sso.HmacConfigured ? MasterModuleState.Ready : MasterModuleState.Down,
                $"통과 {sso.ValidatedOk} · 거부 {sso.Rejected} · 세션 {sso.ActiveSessions}",
                sso.RsaConfigured || sso.HmacConfigured ? null : "서명 키 미구성"));

        if (sp.GetService<ZeroTrustMeshProxy>() is { } mesh)
            RegisterModule("제로 트러스트 메쉬", MasterTier.Security, () => new MasterModuleProbe(
                mesh.TripCount == 0 ? MasterModuleState.Ready : MasterModuleState.Degraded,
                $"허용 {mesh.Allowed} · 차단 {mesh.Denied401 + mesh.DeniedOpen} · 피어 {mesh.PeerCount}", mesh.TripCount > 0 ? $"서킷 트립 {mesh.TripCount}회" : null));

        if (sp.GetService<TenantPolicyEngine>() is { } policy)
            RegisterModule("테넌트 정책 RBAC", MasterTier.Security, () => new MasterModuleProbe(
                MasterModuleState.Ready, $"테넌트 {policy.TenantCount} · 평가 {policy.Evaluated}", policy.LastViolation));

        if (sp.GetService<ZeroTrustAuditExporter>() is { } audit)
            RegisterModule("WORM 감사 익스포터", MasterTier.Security, () => new MasterModuleProbe(
                audit.VerifyChain() == 0 ? MasterModuleState.Ready : MasterModuleState.Degraded, $"{audit.EntryCount}건 해시체인"));

        if (sp.GetService<AirGappedDisasterRecoveryEngine>() is { } recovery)
            RegisterModule("폐쇄망 재해 복구", MasterTier.Security, () => new MasterModuleProbe(
                recovery.SnapshotCount > 0 ? MasterModuleState.Ready : MasterModuleState.Stopped,
                $"스냅샷 {recovery.SnapshotCount} · 복구 {recovery.RestoreCount}", recovery.LastError));

        // ── Intelligence: 스웜 · 플릿 · 자가진단 · 예측 · 추론 · 지식 그래프 · MCP · 봉인 ──
        if (sp.GetService<Agents.AgentSwarmOrchestrator>() is { } swarm)
            RegisterModule("에이전트 스웜", MasterTier.Intelligence, () => new MasterModuleProbe(
                MasterModuleState.Ready, $"라운드 {swarm.TotalRounds} · 교정 {swarm.DefectsResolved}/{swarm.DefectsFound}",
                swarm.IsRunning ? "수렴 실행 중" : null));

        if (sp.GetService<Agents.AiFleetOrchestrator>() is { } fleet)
            RegisterModule("AI 플릿 오케스트레이터", MasterTier.Intelligence, () => new MasterModuleProbe(
                fleet.QuarantinedWorkers > 0 ? MasterModuleState.Degraded : MasterModuleState.Ready,
                $"활성 {fleet.ActiveWorkers}/{fleet.WorkerCount} · 배정 {fleet.TotalAssigned}",
                fleet.QuarantinedWorkers > 0 ? $"격리 {fleet.QuarantinedWorkers}" : null));

        if (sp.GetService<Agents.AiSelfDiagnosticEngine>() is { } diag)
            RegisterModule("AI 자가진단", MasterTier.Intelligence, () => new MasterModuleProbe(
                MasterModuleState.Ready, $"표본 {diag.SamplesInspected} · 이상 {diag.AnomaliesDetected}", diag.LastDirective));

        if (sp.GetService<Agents.PredictiveTelemetryEngine>() is { } predictive)
            RegisterModule("장애 예측 엔진", MasterTier.Intelligence, () => new MasterModuleProbe(
                predictive.Risk == Agents.ForecastRisk.Critical ? MasterModuleState.Degraded : MasterModuleState.Ready,
                $"위험도 {predictive.Risk} · 경고 {predictive.Warnings}", predictive.LastWarning));

        if (sp.GetService<Inference.WebGpuInferenceEngine>() is { } infer)
            RegisterModule("온디바이스 추론", MasterTier.Intelligence, () => new MasterModuleProbe(
                MasterModuleState.Ready, infer.BackendLabel, infer.NativeReady ? null : "BCL 폴백 경로"));

        if (sp.GetService<Inference.MultiModalNeuralCache>() is { } cache)
            RegisterModule("멀티모달 뉴럴 캐시", MasterTier.Intelligence, () => new MasterModuleProbe(
                MasterModuleState.Ready, $"엔트리 {cache.Entries} · 히트 {(cache.HitRatio * 100).ToString("0", CultureInfo.InvariantCulture)}%"));

        if (sp.GetService<Inference.NativePluginSandbox>() is { } sandbox)
            RegisterModule("플러그인 샌드박스", MasterTier.Intelligence, () => new MasterModuleProbe(
                sandbox.Rejected > 0 ? MasterModuleState.Degraded : MasterModuleState.Ready,
                $"플러그인 {sandbox.LoadedPlugins} · 실행 {sandbox.Executions}", sandbox.Rejected > 0 ? $"임계 위반 {sandbox.Rejected}" : null));

        if (sp.GetService<GraphifyQueryEngine>() is { } graph)
            RegisterModule("Graphify 지식 그래프", MasterTier.Intelligence,
                () => new MasterModuleProbe(graph.IsLoaded ? MasterModuleState.Ready : MasterModuleState.Stopped,
                    graph.IsLoaded ? $"노드 {graph.NodeCount} · 간선 {graph.EdgeCount}" : "미로드", graph.LastError),
                async ct => await graph.LoadAsync(null, ct).ConfigureAwait(false));

        if (sp.GetService<McpTestAutomationBridge>() is { } mcp)
            RegisterModule("MCP 검증 브리지", MasterTier.Intelligence,
                () => new MasterModuleProbe(mcp.ConnectedCount > 0 ? MasterModuleState.Ready : MasterModuleState.Stopped,
                    $"접속 {mcp.ConnectedCount}/{mcp.ServerCount} · 스텁 {mcp.StubFallbacks}", mcp.ConnectedCount > 0 ? null : "스텁 폴백 경로"),
                async ct => await mcp.ProbeAllAsync(ct).ConfigureAwait(false));

        if (sp.GetService<ZeroAllocPipelineSealer>() is { } sealer)
            RegisterModule("Zero-Alloc 봉인기", MasterTier.Intelligence,
                () => new MasterModuleProbe(sealer.IsPipelineSealed ? MasterModuleState.Ready : MasterModuleState.Stopped,
                    sealer.Certificate is null ? $"핫패스 {sealer.PathCount}개 대기" : $"{sealer.SealedPaths}/{sealer.Certificate.Paths} 경로 0.0B",
                    sealer.ViolationCount > 0 ? $"위반 {sealer.ViolationCount}건" : null),
                async ct => await sealer.SealAsync(ct).ConfigureAwait(false));

        // ── Edge: 메모리 · 모바일 · 공간 3D · 하이브리드 스케줄러 · 스트레스 ──
        if (sp.GetService<AutonomousMemoryCompactor>() is { } compactor)
            RegisterModule("자율 메모리 압축", MasterTier.Edge, () => new MasterModuleProbe(
                compactor.UnderPressure ? MasterModuleState.Degraded : MasterModuleState.Ready,
                $"힙 {compactor.HeapMb}MB · 파편화 {(compactor.FragmentationRatio * 100).ToString("0", CultureInfo.InvariantCulture)}%",
                compactor.UnderPressure ? "압축 임계 초과" : null));

        if (sp.GetService<MauiMobileBridgeAdapter>() is { } bridge)
            RegisterModule("MAUI 모바일 브리지", MasterTier.Edge, () => new MasterModuleProbe(
                MasterModuleState.Ready, $"{bridge.PlatformLabel} · 보류 {bridge.PendingCount}", bridge.IsNativeBound ? null : "네이티브 훅 미결선(스텁)"));

        if (sp.GetService<Media.Spatial3DVideoEngine>() is { } spatial)
            RegisterModule("공간 3D 변환", MasterTier.Edge, () => new MasterModuleProbe(
                MasterModuleState.Ready, $"변환 {spatial.TranscodedClips}클립"));

        if (sp.GetService<Inference.HybridNodeMeshScheduler>() is { } hybrid)
            RegisterModule("하이브리드 스케줄러", MasterTier.Edge, () => new MasterModuleProbe(
                MasterModuleState.Ready, $"로컬 {hybrid.LocalDispatched} · 원격 {hybrid.RemoteDispatched}"));

        if (sp.GetService<SystemStressAuditor>() is { } stress)
            RegisterModule("동시성 스트레스 오디터", MasterTier.Edge, () => new MasterModuleProbe(
                MasterModuleState.Ready, stress.IsRunning ? "오디트 실행 중" : "대기"));
    }

    // ── 라이프사이클 ──

    /// <summary>
    /// 계층 순차 시동(Core → Distributed → Security → Intelligence → Edge). 각 모듈의 시동 훅 실패는 Degraded 로 기록하고 다음 모듈로 진행한다(제5헌장).
    /// </summary>
    public async Task<MasterBootReport> StartAllAsync(CancellationToken ct = default)
    {
        IsBooting = true;
        SystemState = MasterSystemState.Booting;
        LastAction = "마스터 시동 시작…";
        Notify();
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            foreach (MasterTier tier in Enum.GetValues<MasterTier>())
            {
                ModuleDescriptor[] batch;
                lock (_gate) batch = _modules.Where(m => m.Tier == tier).ToArray();
                foreach (var m in batch)
                {
                    ct.ThrowIfCancellationRequested();
                    CurrentModule = m.Name;
                    m.State = MasterModuleState.Starting;
                    Notify();
                    if (m.StartAsync is not null)
                    {
                        try { await m.StartAsync(ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { m.Note = $"시동 실패 — {ex.GetType().Name}"; }
                    }
                    ProbeOne(m);
                    Notify();
                }
            }
            LastAction = "마스터 시동 완료";
        }
        catch (OperationCanceledException) { LastAction = "시동 취소 — 완료분 보존"; }
        finally
        {
            IsBooting = false;
            CurrentModule = null;
            Rollup();
        }

        var report = new MasterBootReport(DateTimeOffset.Now, ModuleCount, ReadyCount, DegradedCount, DownCount,
            (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);
        LastBootReport = report;
        LastBootAt = report.At;
        Notify();
        return report;
    }

    /// <summary>역순(Edge → Core) 정지 — 정지 훅이 없는 모듈은 상태만 재프로브.</summary>
    public void StopAll()
    {
        ModuleDescriptor[] all;
        lock (_gate) all = _modules.ToArray();
        foreach (var m in all.OrderByDescending(m => m.Tier))
        {
            try { m.Stop?.Invoke(); }
            catch (Exception ex) { m.Note = $"정지 실패 — {ex.GetType().Name}"; }
            ProbeOne(m);
        }
        LastAction = "마스터 정지 완료";
        Rollup();
        Notify();
    }

    /// <summary>전 모듈 즉시 재프로브(수동 새로고침).</summary>
    public void Refresh()
    {
        SafeRefresh();
        LastAction = $"관제 갱신 — 준비 {ReadyCount}/{ModuleCount}";
        Notify();
    }

    private void SafeRefresh()
    {
        if (_disposed) return;
        ModuleDescriptor[] all;
        lock (_gate) all = _modules.ToArray();
        foreach (var m in all) ProbeOne(m);
        Rollup();
        Notify();
    }

    private static void ProbeOne(ModuleDescriptor m)
    {
        try
        {
            var p = m.Probe();
            m.State = p.State;
            m.Metric = p.Metric;
            m.Note = p.Note;
        }
        catch (Exception ex)
        {
            m.State = MasterModuleState.Down;
            m.Metric = "프로브 실패";
            m.Note = ex.GetType().Name;
        }
    }

    /// <summary>모듈 상태 → 전체 시스템 상태 롤업(Down 존재=Critical · Degraded 존재=Degraded).</summary>
    private void Rollup()
    {
        int ready = 0, degraded = 0, down = 0;
        lock (_gate)
        {
            foreach (var m in _modules)
            {
                switch (m.State)
                {
                    case MasterModuleState.Ready: ready++; break;
                    case MasterModuleState.Degraded: degraded++; break;
                    case MasterModuleState.Down: down++; break;
                }
            }
        }
        ReadyCount = ready;
        DegradedCount = degraded;
        DownCount = down;
        SystemState = IsBooting ? MasterSystemState.Booting
            : ModuleCount == 0 ? MasterSystemState.Idle
            : down > 0 ? MasterSystemState.Critical
            : degraded > 0 ? MasterSystemState.Degraded
            : MasterSystemState.Operational;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
        lock (_gate) _modules.Clear();
        OnChange = null;
    }

    /// <summary>모듈 디스크립터(가변 상태는 프로브 결과만 보유 — 모듈 내부는 소유하지 않음).</summary>
    private sealed class ModuleDescriptor
    {
        public ModuleDescriptor(string name, MasterTier tier, Func<MasterModuleProbe> probe,
            Func<CancellationToken, Task>? startAsync, Action? stop)
        {
            Name = name;
            Tier = tier;
            Probe = probe;
            StartAsync = startAsync;
            Stop = stop;
        }

        public string Name { get; }
        public MasterTier Tier { get; }
        public Func<MasterModuleProbe> Probe { get; }
        public Func<CancellationToken, Task>? StartAsync { get; }
        public Action? Stop { get; }
        public MasterModuleState State { get; set; } = MasterModuleState.Down;
        public string Metric { get; set; } = "미프로브";
        public string? Note { get; set; }
    }
}
