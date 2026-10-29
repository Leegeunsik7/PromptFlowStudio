using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Configuration;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Services.Render;

/// <summary>엔진 헬스 스냅샷(불변) — 상태가 바뀔 때만 새 인스턴스.</summary>
public sealed record EngineHealthSnapshot(bool ComfyOnline, string ComfyUrl, bool CloudConfigured, string? CloudEngineName, DateTimeOffset CheckedAt)
{
    public static readonly EngineHealthSnapshot Unknown = new(false, EngineHealthCheckService.DefaultComfyUrl, false, null, DateTimeOffset.MinValue);

    public bool IsChecked => CheckedAt != DateTimeOffset.MinValue;

    /// <summary><c>ComfyUI: 🟢 Online (8188)</c> / <c>ComfyUI: 🔴 Offline (시뮬레이션 모드로 동작)</c>.</summary>
    public string ComfyLabel => !IsChecked ? "ComfyUI: ⚪ 점검 중…"
        : ComfyOnline ? "ComfyUI: 🟢 Online (" + EngineHealthCheckService.PortOf(ComfyUrl) + ")"
        : "ComfyUI: 🔴 Offline (시뮬레이션 모드로 동작)";

    public string CloudLabel => !IsChecked ? "Cloud: ⚪ 점검 중…"
        : CloudConfigured ? "Cloud: 🟢 API 키 구성됨" + (CloudEngineName is null ? "" : " (" + CloudEngineName + ")")
        : "Cloud: 🔴 API 키 미설정";
}

/// <summary>
/// [PromptFlowStudio] 렌더 엔진 사전 헬스체크(Pre-flight Ping) — 5초 주기 백그라운드 경량 점검.
///   ▸ ComfyUI: <c>{PFS_COMFY_URL|http://127.0.0.1:8188}/system_stats</c> GET(헤더까지 · 타임아웃 1초) — 2xx 면 Online.
///   ▸ Cloud: <see cref="VideoEngineSelector"/> 의 비-ComfyUI 엔진 중 <c>Enabled</c>(API 키/엔드포인트 구성) 존재 여부.
///   ▸ 구독 참조 카운트(<see cref="Start"/>/<see cref="Stop"/>) — 화면이 없으면 폴링 0. 상태 변화 시에만 <see cref="Changed"/>(예외 격리).
///   ▸ 틱 중첩 차단(Interlocked) · 핑/콜백 예외는 Offline 판정으로 격리.
///   ▸ 핫리로드: <see cref="AttachSettings"/> 로 <see cref="AppSettingsManager.OnSettingsChanged"/> 구독 → 타깃 URL 즉시 교체 + 백그라운드 재핑.
/// </summary>
public sealed class EngineHealthCheckService : IDisposable
{
    public const string DefaultComfyUrl = "http://127.0.0.1:8188";
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(1);
    private static readonly HttpClient s_http = new() { Timeout = PingTimeout };

    private readonly VideoEngineSelector? _selector;
    private readonly Func<string, CancellationToken, Task<bool>> _ping;
    private volatile string _comfyUrl;
    private AppSettingsManager? _settings;
    private readonly object _gate = new();
    private System.Threading.Timer? _timer;
    private CancellationTokenSource? _cts;
    private int _refs;
    private int _ticking;
    private bool _disposed;
    private EngineHealthSnapshot _latest = EngineHealthSnapshot.Unknown;

    public EngineHealthCheckService(VideoEngineSelector? selector) : this(selector, null, null) { }

    /// <param name="ping">ComfyUI 핑(베이스 URL · 1초 타임아웃 토큰) — null 이면 HTTP GET <c>/system_stats</c>.</param>
    /// <param name="comfyUrl">null 이면 <c>PFS_COMFY_URL</c> → <see cref="DefaultComfyUrl"/>.</param>
    public EngineHealthCheckService(VideoEngineSelector? selector, Func<string, CancellationToken, Task<bool>>? ping, string? comfyUrl)
    {
        _selector = selector;
        _ping = ping ?? DefaultPingAsync;
        _comfyUrl = (comfyUrl ?? (Environment.GetEnvironmentVariable("PFS_COMFY_URL") is { Length: > 0 } u ? u : DefaultComfyUrl)).TrimEnd('/');
        _latest = EngineHealthSnapshot.Unknown with { ComfyUrl = _comfyUrl };
    }

    public EngineHealthSnapshot Latest { get { lock (_gate) return _latest; } }

    /// <summary>현재 핑 대상 ComfyUI 베이스 URL.</summary>
    public string ComfyUrl => _comfyUrl;

    /// <summary>환경설정 저장 이벤트 구독(1회) — 저장 즉시 <see cref="AppSettingsManager.EffectiveComfyUiUrl"/> 로 동기화. Dispose 시 해제.</summary>
    public void AttachSettings(AppSettingsManager settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            if (_disposed || _settings is not null) return;
            _settings = settings;
        }
        settings.OnSettingsChanged += OnSettingsChanged;
    }

    /// <summary>
    /// 타깃 URL 교체 — 바뀌면 스냅샷을 '점검 중'으로 되돌려 <see cref="Changed"/> 후 즉시 백그라운드 핑(재시작 불필요).
    /// 반환: 실제 변경 여부.
    /// </summary>
    public bool UpdateComfyUrl(string? url)
    {
        var next = AppSettingsManager.NormalizeUrl(url);
        lock (_gate)
        {
            if (_disposed || string.Equals(next, _comfyUrl, StringComparison.Ordinal)) return false;
            _comfyUrl = next;
            _latest = EngineHealthSnapshot.Unknown with { ComfyUrl = next };
        }
        try { Changed?.Invoke(); } catch (Exception ex) { Program.Log("EngineHealth.Changed", ex); }
        _ = RecheckAsync();
        return true;
    }

    private void OnSettingsChanged(UserSettings _)
    {
        var s = _settings;
        if (s is not null) UpdateComfyUrl(s.EffectiveComfyUiUrl);
    }

    private async Task RecheckAsync()
    {
        try { await CheckNowAsync().ConfigureAwait(false); }
        catch (Exception ex) { Program.Log("EngineHealth.Recheck", ex); }
    }

    /// <summary>상태 변화 알림 — 타이머 스레드에서 발생(Blazor 는 InvokeAsync 마샬링).</summary>
    public event Action? Changed;

    /// <summary>폴링 구독 시작(참조 카운트) — 첫 구독 시 즉시 1회 + 5초 주기.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _refs++ > 0) return;
            _cts = new CancellationTokenSource();
            _timer = new System.Threading.Timer(OnTick, null, TimeSpan.Zero, PollInterval);
        }
    }

    /// <summary>폴링 구독 해제 — 마지막 해제 시 타이머·CTS 정리.</summary>
    public void Stop()
    {
        System.Threading.Timer? timer;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_refs == 0 || --_refs > 0) return;
            timer = _timer; cts = _cts;
            _timer = null; _cts = null;
        }
        timer?.Dispose();
        if (cts is not null) { try { cts.Cancel(); } catch (ObjectDisposedException) { } cts.Dispose(); }
    }

    /// <summary>즉시 1회 점검 — 상태가 바뀌면 <see cref="Changed"/>.</summary>
    public async Task<EngineHealthSnapshot> CheckNowAsync(CancellationToken ct = default)
    {
        var url = _comfyUrl;
        bool online;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(PingTimeout);
            try { online = await _ping(url, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { online = false; }   // 연결 거부 · 타임아웃 · DNS → Offline
        }
        var (cloud, cloudName) = ProbeCloud();

        EngineHealthSnapshot snap;
        bool changed;
        lock (_gate)
        {
            if (!string.Equals(url, _comfyUrl, StringComparison.Ordinal)) return _latest;   // 점검 중 URL 교체 → 낡은 결과 폐기
            var prev = _latest;
            changed = !prev.IsChecked || prev.ComfyOnline != online || prev.CloudConfigured != cloud || !string.Equals(prev.CloudEngineName, cloudName, StringComparison.Ordinal);
            if (changed) _latest = new EngineHealthSnapshot(online, url, cloud, cloudName, DateTimeOffset.Now);
            snap = _latest;
        }
        if (changed) { try { Changed?.Invoke(); } catch (Exception ex) { Program.Log("EngineHealth.Changed", ex); } }
        return snap;
    }

    /// <summary>URL 포트 문자열(명시 없으면 스킴 기본) — 파싱 실패 시 "?".</summary>
    public static string PortOf(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?";

    private (bool Configured, string? Name) ProbeCloud()
    {
        var sel = _selector;
        if (sel is null) return (false, null);
        try
        {
            var all = sel.All;
            for (var i = 0; i < all.Count; i++)
                if (all[i] is not ComfyUiVideoEngine && all[i].Enabled) return (true, all[i].DisplayName);
        }
        catch (Exception ex) { Program.Log("EngineHealth.Cloud", ex); }
        return (false, null);
    }

    private void OnTick(object? state)
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        CancellationToken token;
        lock (_gate)
        {
            if (_cts is null) { Volatile.Write(ref _ticking, 0); return; }
            token = _cts.Token;
        }
        _ = RunTickAsync(token);
    }

    private async Task RunTickAsync(CancellationToken token)
    {
        try { await CheckNowAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Program.Log("EngineHealth.Tick", ex); }
        finally { Volatile.Write(ref _ticking, 0); }
    }

    private static async Task<bool> DefaultPingAsync(string baseUrl, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/system_stats");
        using var res = await s_http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        return res.IsSuccessStatusCode;
    }

    public void Dispose()
    {
        System.Threading.Timer? timer;
        CancellationTokenSource? cts;
        AppSettingsManager? settings;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            timer = _timer; cts = _cts;
            _timer = null; _cts = null; _refs = 0;
            settings = _settings; _settings = null;
        }
        if (settings is not null) settings.OnSettingsChanged -= OnSettingsChanged;
        timer?.Dispose();
        if (cts is not null) { try { cts.Cancel(); } catch (ObjectDisposedException) { } cts.Dispose(); }
        Changed = null;
    }
}
