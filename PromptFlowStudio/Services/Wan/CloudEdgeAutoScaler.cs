using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Mobile;   // RemoteRenderClient(URL 검증·결합 재사용)

namespace PromptFlowStudio.Services.Wan;

/// <summary>클라우드 프로비저닝 API 요청/응답(불변 · System.Text.Json).</summary>
public sealed record CloudProvisionRequest(int Slots, string Region, string Purpose = "promptflow-render");
public sealed record CloudProvisionResponse(string? BaseUrl, string? InstanceId, int? Slots);

/// <summary>오토스케일러가 띄운 클라우드 인스턴스 1대(그리드 노드와 1:1 · 스텁 여부 표시).</summary>
public sealed record CloudInstance(string NodeId, string Name, string Url, bool Stub, DateTimeOffset ProvisionedAt);

/// <summary>
/// [PromptFlow Studio v8.2.0] 클라우드 엣지 오토스케일러(관찰 가능 싱글턴 · 순수 BCL · 외부 클라우드 SDK 0 · IDisposable).
///   ▸ <see cref="DistributedRenderGrid"/> 의 대기 백로그가 가용 슬롯을 초과하면(부하 압력) 클라우드 워커 API 에 인스턴스 동적 프로비저닝을 요청해 노드를 추가한다(Scale-out).
///   ▸ 렌더가 멎고 유휴가 지속되면(하트비트 무부하) 오토스케일러가 띄운 노드만 골라 자동 축소한다(Scale-in · 사용자 온프렘 노드는 불가침).
///   ▸ <see cref="System.Threading.Timer"/> 10초 주기 평가 + 수동 트리거. 재진입은 <see cref="Interlocked"/> 가드로 차단.
///   ▸ Graceful: 프로비저닝 API 미구성/예외 시 <b>BCL 스텁 노드</b>로 폴백 등록 — 그리드 하트비트/페일오버가 도달성을 알아서 판정하므로 렌더 파이프라인 무중단(우아한 저하).
///   ⚠ <see cref="OnChange"/> 는 타이머/HTTP(백그라운드) 스레드에서 온다 — 컴포넌트는 InvokeAsync(StateHasChanged)로 마샬링(제3헌장).
/// </summary>
public sealed class CloudEdgeAutoScaler : IDisposable
{
    private static readonly TimeSpan EvalInterval = TimeSpan.FromSeconds(10);
    private const int IdleTicksToScaleIn = 2;   // 이 횟수만큼 연속 유휴 평가 시 1대 축소
    private readonly DistributedRenderGrid _grid;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, CloudInstance> _instances = new();   // 그리드 NodeId → 인스턴스
    private int _evalBusy;      // 평가 재진입 방지(0/1)
    private int _idleTicks;
    private int _seq;

    // ── 구성 ──
    private string _endpoint = "";     // 클라우드 프로비저닝 API 베이스(빈 값이면 스텁 폴백 경로)
    private string _apiKey = "";
    private string _region = "auto";
    private int _minNodes;
    private int _maxNodes = 4;
    private int _slotsPerNode = 2;

    // ── 카운터(HUD) ──
    private long _provisioned, _terminated, _scaleOut, _scaleIn, _stub;

    public CloudEdgeAutoScaler(DistributedRenderGrid grid)
    {
        _grid = grid;
        _timer = new System.Threading.Timer(_ => _ = EvaluateAsync(), null, EvalInterval, EvalInterval);
    }

    /// <summary>상태 변경 방송(구독: 컴포넌트 OnInitialized · 해지: Dispose).</summary>
    public event Action? OnChange;
    private void Notify() => OnChange?.Invoke();

    // ── 상태 ──
    public bool Enabled { get; private set; } = true;
    public int ActiveInstances => _instances.Count;
    public int MaxNodes => _maxNodes;
    public int MinNodes => _minNodes;
    public bool ProviderConfigured => RemoteRenderClient.IsValidWorkerUrl(_endpoint);
    public long ProvisionedTotal => Interlocked.Read(ref _provisioned);
    public long TerminatedTotal => Interlocked.Read(ref _terminated);
    public long ScaleOutEvents => Interlocked.Read(ref _scaleOut);
    public long ScaleInEvents => Interlocked.Read(ref _scaleIn);
    public long StubProvisions => Interlocked.Read(ref _stub);
    public string? LastAction { get; private set; }
    public DateTimeOffset? LastEvalAt { get; private set; }
    public IReadOnlyList<CloudInstance> Instances => _instances.Values.OrderBy(i => i.ProvisionedAt).ToList();

    /// <summary>현재 그리드 부하 압력(대기 백로그 − 가용 슬롯) — 양수면 Scale-out 후보.</summary>
    public int Pressure
    {
        get
        {
            var (free, queued, _) = SnapshotLoad();
            return queued - free;
        }
    }

    // ── 구성 API ──

    /// <summary>클라우드 프로비저닝 공급자 설정(엔드포인트 비면 스텁 폴백 모드로 동작).</summary>
    public void ConfigureProvider(string? endpoint, string? apiKey, int minNodes, int maxNodes, int slotsPerNode, string? region = null)
    {
        lock (_gate)
        {
            _endpoint = (endpoint ?? "").Trim();
            _apiKey = (apiKey ?? "").Trim();
            _minNodes = Math.Clamp(minNodes, 0, 64);
            _maxNodes = Math.Clamp(maxNodes, Math.Max(1, _minNodes), 64);
            _slotsPerNode = Math.Clamp(slotsPerNode, 1, 16);
            if (!string.IsNullOrWhiteSpace(region)) _region = region!.Trim();
        }
        Notify();
    }

    /// <summary>오토스케일 활성/비활성(비활성 시 평가 루프는 돌되 아무 조치도 하지 않음).</summary>
    public void SetEnabled(bool enabled) { Enabled = enabled; Notify(); }

    // ── 평가 루프 ──

    private async Task EvaluateAsync()
    {
        if (Interlocked.Exchange(ref _evalBusy, 1) == 1) return;
        try
        {
            LastEvalAt = DateTimeOffset.Now;
            if (!Enabled) return;

            var (free, queued, running) = SnapshotLoad();

            // Scale-out: 대기 백로그가 가용 슬롯을 초과(또는 슬롯이 꽉 찬 채 렌더 진행) → 부족분만큼 노드 추가.
            var deficit = queued - free;
            if (deficit > 0 || (running && free <= 0))
            {
                _idleTicks = 0;
                var want = Math.Max(1, (int)Math.Ceiling(Math.Max(deficit, 1) / (double)_slotsPerNode));
                var room = _maxNodes - _instances.Count;
                var add = Math.Clamp(want, 0, Math.Max(0, room));
                if (add > 0) { await ScaleOutAsync(add).ConfigureAwait(false); return; }
            }

            // Scale-in: 유휴(무부하·무진행)가 연속 지속되면 스케일러 노드만 1대씩 축소(min 하한).
            if (!running && queued == 0 && free >= 0)
            {
                if (++_idleTicks >= IdleTicksToScaleIn && _instances.Count > _minNodes)
                {
                    _idleTicks = 0;
                    ScaleIn(1);
                }
            }
            else _idleTicks = 0;
        }
        catch (Exception ex) { Program.Log("CloudScaler.Evaluate", ex); }
        finally { Interlocked.Exchange(ref _evalBusy, 0); }
    }

    /// <summary>가용 슬롯/대기 백로그/진행 여부 스냅샷(가용=Usable 노드 슬롯 합 − InFlight).</summary>
    private (int Free, int Queued, bool Running) SnapshotLoad()
    {
        var totalSlots = _grid.Nodes.Where(n => n.IsUsable).Sum(n => Math.Max(1, n.MaxConcurrency));
        var free = Math.Max(0, totalSlots - _grid.TotalInFlight);
        var queued = _grid.LastProgress?.Queued ?? 0;
        return (free, queued, _grid.IsRunning);
    }

    // ── Scale-out / Scale-in ──

    /// <summary>클라우드 노드 <paramref name="count"/>대 프로비저닝(수동 트리거 가능). 반환=실제 추가 수.</summary>
    public async Task<int> RequestScaleOutAsync(int count = 1)
    {
        var room = _maxNodes - _instances.Count;
        var add = Math.Clamp(count, 0, Math.Max(0, room));
        return add > 0 ? await ScaleOutAsync(add).ConfigureAwait(false) : 0;
    }

    private async Task<int> ScaleOutAsync(int count)
    {
        var added = 0;
        for (var i = 0; i < count; i++)
        {
            var inst = await ProvisionOneAsync().ConfigureAwait(false);
            if (inst is null) continue;
            _instances[inst.NodeId] = inst;
            Interlocked.Increment(ref _provisioned);
            if (inst.Stub) Interlocked.Increment(ref _stub);
            added++;
        }
        if (added > 0)
        {
            Interlocked.Increment(ref _scaleOut);
            LastAction = $"☁ Scale-out +{added} (활성 {_instances.Count}/{_maxNodes})";
            Notify();
        }
        return added;
    }

    /// <summary>인스턴스 1대 프로비저닝 — 실 API 성공 시 실 노드, 실패/미구성 시 BCL 스텁 노드로 그리드에 등록.</summary>
    private async Task<CloudInstance?> ProvisionOneAsync()
    {
        var n = Interlocked.Increment(ref _seq);
        string? url = null; var stub = true;

        if (ProviderConfigured)
        {
            try
            {
                var reqBody = JsonSerializer.Serialize(new CloudProvisionRequest(_slotsPerNode, _region), PromptFlowWireJsonContext.Default.CloudProvisionRequest);
                var endpoint = RemoteRenderClient.BuildEndpoint(_endpoint, "api/v1/instances");
                using var content = new StringContent(reqBody, Encoding.UTF8, "application/json");
                using var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
                if (!string.IsNullOrWhiteSpace(_apiKey)) req.Headers.TryAddWithoutValidation("X-API-Key", _apiKey);
                using var linked = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                using var resp = await _http.SendAsync(req, linked.Token).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var parsed = TryParse(await resp.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false));
                    if (RemoteRenderClient.IsValidWorkerUrl(parsed?.BaseUrl)) { url = parsed!.BaseUrl!.Trim(); stub = false; }
                }
            }
            catch (Exception ex) { Program.Log("CloudScaler.Provision", ex); }   // → 스텁 폴백(우아한 저하)
        }

        // 실 URL 미확보 시 BCL 스텁 — 그리드 하트비트가 Offline 로 판정해 배정 제외(무해). 실 워커가 이 주소를 응답하면 자가 승격.
        url ??= $"http://cloud-stub-{n}.promptflow.local:8799";
        var name = stub ? $"☁ Cloud-{n} · 스텁" : $"☁ Cloud-{n}";
        var node = _grid.RegisterNode(name, url, _slotsPerNode);
        _ = _grid.ProbeNodeAsync(node.Id);   // 즉시 1회 프로브(실 노드면 Online 승격)
        return new CloudInstance(node.Id, name, url, stub, DateTimeOffset.Now);
    }

    /// <summary>오토스케일러가 띄운 노드만 <paramref name="count"/>대 축소(온프렘 노드 불가침). 반환=실제 축소 수.</summary>
    public int ScaleIn(int count = 1)
    {
        var removed = 0;
        // 최근 프로비저닝 순(LIFO)으로 회수 — 오래된 안정 노드를 남긴다.
        foreach (var inst in _instances.Values.OrderByDescending(i => i.ProvisionedAt).ToList())
        {
            if (removed >= count || _instances.Count <= _minNodes) break;
            if (!_instances.TryRemove(inst.NodeId, out _)) continue;
            _ = TerminateRemoteAsync(inst);   // best-effort 원격 해제
            _grid.RemoveNode(inst.NodeId);
            Interlocked.Increment(ref _terminated);
            removed++;
        }
        if (removed > 0)
        {
            Interlocked.Increment(ref _scaleIn);
            LastAction = $"☁ Scale-in −{removed} (활성 {_instances.Count}/{_maxNodes})";
            Notify();
        }
        return removed;
    }

    private async Task TerminateRemoteAsync(CloudInstance inst)
    {
        if (inst.Stub || !ProviderConfigured) return;
        try
        {
            var endpoint = RemoteRenderClient.BuildEndpoint(_endpoint, "api/v1/instances/" + Uri.EscapeDataString(inst.NodeId));
            using var req = new HttpRequestMessage(HttpMethod.Delete, endpoint);
            if (!string.IsNullOrWhiteSpace(_apiKey)) req.Headers.TryAddWithoutValidation("X-API-Key", _apiKey);
            using var linked = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var _ = await _http.SendAsync(req, linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { Program.Log("CloudScaler.Terminate", ex); }   // 해제 실패는 무해(로컬은 이미 제거)
    }

    private static CloudProvisionResponse? TryParse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try { return JsonSerializer.Deserialize(body!, PromptFlowWireJsonContext.Default.CloudProvisionResponse); }
        catch { return null; }
    }

    public void Dispose()
    {
        try { _timer.Dispose(); } catch { /* 무시 */ }
        foreach (var inst in _instances.Values) { try { _grid.RemoveNode(inst.NodeId); } catch { /* 무시 */ } }
        try { _http.Dispose(); } catch { /* 무시 */ }
    }
}
