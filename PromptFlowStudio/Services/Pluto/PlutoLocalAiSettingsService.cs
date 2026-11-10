using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>📡 로컬 AI 게이트웨이 진단 Ping 결과 — 온라인 · 감지 프로토콜 · 지연 · 사용자용 팁.</summary>
public readonly record struct PlutoGatewayPingResult(bool IsOnline, string? Protocol, long LatencyMs, string Message)
{
    public const string OpenAiProtocol = "OpenAI 호환";
    public const string OllamaProtocol = "Ollama";
    public const string InvalidUrlTip = "URL 형식 오류(http/https 절대 주소) — 원격 주소는 'LAN 엔드포인트 허용'이 필요해요";
    public const string RefusedTip = "연결 거부 — 로컬 AI 서버(llama.cpp · Ollama · LM Studio)가 실행 중인지, 포트가 맞는지 확인하세요";
    public const string TimeoutTip = "응답 시간 초과 — 서버 기동/모델 로딩 중이거나 방화벽이 막고 있어요";
    public const string AuthTip = "인증 거부(401/403) — PFS_LOCAL_AI_API_KEY 환경변수를 확인하세요";
    public const string NoEndpointTip = "서버는 응답했지만 /v1/models · /api/tags 모두 실패 — OpenAI/Ollama 호환 경로인지 확인하세요";

    public static PlutoGatewayPingResult Online(string protocol, long latencyMs) => new(true, protocol, Math.Max(0, latencyMs), "연결 성공");
    public static PlutoGatewayPingResult Offline(string tip) => new(false, null, 0, tip);
}

/// <summary>
/// 🦙 Pluto 로컬 AI 설정 서비스 — GUI 설정(<c>pluto_local_ai_settings.json</c>)을 실행 중 게이트웨이 <see cref="PlutoLocalGatewayOptions"/>에 즉시 반영 · 원자적 영속.
///   ▸ 우선순위: 저장 파일 &gt; 환경변수(PFS_LOCAL_AI_*) &gt; 기본값. API 키는 환경변수 전용(직렬화 제외 · 저장 시 보존).
///   ▸ <see cref="TestConnectionAsync"/>는 저장 전 초안을 임시 게이트웨이로 Ping(실행 중 상태 불변).
/// </summary>
public sealed class PlutoLocalAiSettingsService : IDisposable
{
    public const string FileName = "pluto_local_ai_settings.json";
    public const int MaxModelNameLength = 128;
    public const int MaxBaseUrlLength = 512;

    private readonly string _path;
    private readonly HttpMessageHandler? _probeHandler;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    public PlutoCustomLocalGateway Gateway { get; }
    public string StoragePath => _path;

    /// <summary>설정 적용 통지(백그라운드 스레드 가능 — 구독자는 InvokeAsync(StateHasChanged)).</summary>
    public event Action? Changed;

    /// <param name="probeHandler">연결 테스트용 핸들러(테스트/프록시 · 소유권은 호출자).</param>
    public PlutoLocalAiSettingsService(PlutoCustomLocalGateway gateway, string? storagePath = null, HttpMessageHandler? probeHandler = null)
    {
        Gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _path = string.IsNullOrWhiteSpace(storagePath) ? DefaultPath : storagePath;
        _probeHandler = probeHandler;
    }

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", FileName);

    /// <summary>현재 실행 중 설정 스냅샷(폼 초기화용 · API 키 제외).</summary>
    public PlutoLocalGatewayOptions Snapshot()
    {
        var o = Gateway.Options;
        return new PlutoLocalGatewayOptions { IsEnabled = o.IsEnabled, BaseUrl = o.BaseUrl, ModelName = o.ModelName, TimeoutMs = o.TimeoutMs, AllowRemote = o.AllowRemote };
    }

    /// <summary>🚀 시작 시 동기 적재 — 환경변수 기반 옵션 위에 저장 파일을 덮어씀(손상/부재/IO 실패 → 환경변수 그대로). DI 팩토리 전용.</summary>
    public static PlutoLocalGatewayOptions LoadEffectiveOptions(string? storagePath = null)
    {
        var env = PlutoLocalGatewayOptions.FromEnvironment();
        var path = string.IsNullOrWhiteSpace(storagePath) ? DefaultPath : storagePath;
        try
        {
            if (!File.Exists(path)) return env;
            var saved = JsonSerializer.Deserialize<PlutoLocalGatewayOptions>(File.ReadAllText(path));
            if (saved is not null && TryNormalize(saved, out var norm, out _)) CopyInto(env, norm);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.LocalAi] settings load fallback: " + ex.Message);
        }
        return env;
    }

    /// <summary>입력 검증 · 정규화 — URL은 http/https 절대 주소, 모델명 비면 기본값, 타임아웃 클램프. 실패 시 사용자용 오류.</summary>
    public static bool TryNormalize(PlutoLocalGatewayOptions input, out PlutoLocalGatewayOptions normalized, out string? error)
    {
        normalized = null!;
        error = null;
        if (input is null) { error = "설정이 비어 있어요"; return false; }
        var url = (input.BaseUrl ?? "").Trim();
        if (url.Length == 0) url = PlutoLocalGatewayOptions.DefaultBaseUrl;
        if (url.Length > MaxBaseUrlLength || !Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
        {
            error = "엔드포인트 URL은 http:// 또는 https:// 로 시작하는 절대 주소여야 해요";
            return false;
        }
        var model = (input.ModelName ?? "").Trim();
        if (model.Length == 0) model = PlutoLocalGatewayOptions.DefaultModelName;
        if (model.Length > MaxModelNameLength) { error = "모델 식별자가 너무 길어요(최대 128자)"; return false; }
        var timeout = input.TimeoutMs <= 0 ? PlutoLocalGatewayOptions.DefaultTimeoutMs : Math.Clamp(input.TimeoutMs, PlutoLocalGatewayOptions.MinTimeoutMs, PlutoLocalGatewayOptions.MaxTimeoutMs);
        normalized = new PlutoLocalGatewayOptions { IsEnabled = input.IsEnabled, BaseUrl = url, ModelName = model, TimeoutMs = timeout, AllowRemote = input.AllowRemote };
        return true;
    }

    /// <summary>💾 저장 — 검증 → 실행 중 게이트웨이에 즉시 적용(서킷 초기화) → 원자적 영속. 디스크 실패 시 [부분 구제]로 런타임 적용은 유지.</summary>
    public async Task<(bool Applied, bool Persisted, string? Error)> SaveAsync(PlutoLocalGatewayOptions input, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0) return (false, false, "설정 서비스가 종료되었어요");
        if (!TryNormalize(input, out var norm, out var error)) return (false, false, error);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        bool persisted;
        try
        {
            CopyInto(Gateway.Options, norm);
            Gateway.ResetState();
            var json = JsonSerializer.Serialize(norm, new JsonSerializerOptions { WriteIndented = true });
            persisted = await PlutoJsonStore.WriteAtomicAsync(_path, json, "Pluto.LocalAi", ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        RaiseChanged();
        return (true, persisted, persisted ? null : "디스크 저장 실패 — 이번 실행 동안만 적용돼요");
    }

    /// <summary>🔌 연결 테스트 — 초안 설정을 임시 게이트웨이로 Ping(실행 중 게이트웨이 · 서킷 불변). 예외 없음(호출자 취소 제외).</summary>
    public async Task<PlutoGatewayPingResult> TestConnectionAsync(PlutoLocalGatewayOptions draft, CancellationToken ct = default)
    {
        if (!TryNormalize(draft, out var norm, out var error)) return PlutoGatewayPingResult.Offline(error ?? PlutoGatewayPingResult.InvalidUrlTip);
        norm.ApiKey = Gateway.Options.ApiKey;   // 🔑 환경변수 키 재사용(UI 비노출)
        using var probe = new PlutoCustomLocalGateway(norm, _probeHandler);
        return await probe.PingDetailedAsync(ct).ConfigureAwait(false);
    }

    private static void CopyInto(PlutoLocalGatewayOptions target, PlutoLocalGatewayOptions src)
    {
        target.BaseUrl = src.BaseUrl;
        target.ModelName = src.ModelName;
        target.TimeoutMs = src.TimeoutMs;
        target.AllowRemote = src.AllowRemote;
        target.IsEnabled = src.IsEnabled;   // 마지막 — 활성 시점에 URL/모델이 이미 반영되도록
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.LocalAi] Changed handler: " + ex.Message); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Changed = null;
        _gate.Dispose();
    }
}
