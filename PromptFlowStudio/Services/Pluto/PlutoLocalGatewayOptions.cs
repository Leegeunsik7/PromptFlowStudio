using System;
using System.Globalization;
using System.Text.Json.Serialization;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>
/// 🔌 사내 승인 로컬 AI 마이크로서비스 게이트웨이 설정 — OpenAI 호환(<c>/v1/chat/completions</c>) · Ollama 호환(<c>/api/chat</c>).
/// 환경변수: PFS_LOCAL_AI_ENABLED(=1) / PFS_LOCAL_AI_URL / PFS_LOCAL_AI_MODEL / PFS_LOCAL_AI_TIMEOUT_MS / PFS_LOCAL_AI_ALLOW_REMOTE(=1) / PFS_LOCAL_AI_API_KEY.
/// 🔒 프라이버시 게이트: 루프백이 아닌 엔드포인트는 <see cref="AllowRemote"/> 없이는 호출하지 않음(아이 프로필 앵커 외부 유출 차단).
/// </summary>
public sealed class PlutoLocalGatewayOptions
{
    public const string DefaultBaseUrl = "http://localhost:8080";
    public const string DefaultModelName = "local-slm";
    public const int DefaultTimeoutMs = 3500;
    public const int MinTimeoutMs = 100;
    public const int MaxTimeoutMs = 60_000;

    public string BaseUrl { get; set; } = DefaultBaseUrl;
    public string ModelName { get; set; } = DefaultModelName;
    public bool IsEnabled { get; set; }
    public int TimeoutMs { get; set; } = DefaultTimeoutMs;

    /// <summary>비루프백(LAN 등) 엔드포인트 허용 — 명시적 동의 시에만.</summary>
    public bool AllowRemote { get; set; }

    /// <summary>Bearer 키(선택) — 환경변수 전용 · 직렬화 제외 · 하드코딩 금지.</summary>
    [JsonIgnore] public string? ApiKey { get; set; }

    /// <summary>[<see cref="MinTimeoutMs"/>, <see cref="MaxTimeoutMs"/>] 클램프된 실효 타임아웃.</summary>
    [JsonIgnore] public TimeSpan EffectiveTimeout => TimeSpan.FromMilliseconds(Math.Clamp(TimeoutMs <= 0 ? DefaultTimeoutMs : TimeoutMs, MinTimeoutMs, MaxTimeoutMs));

    public static PlutoLocalGatewayOptions FromEnvironment()
    {
        var url = Environment.GetEnvironmentVariable("PFS_LOCAL_AI_URL");
        var model = Environment.GetEnvironmentVariable("PFS_LOCAL_AI_MODEL");
        var key = Environment.GetEnvironmentVariable("PFS_LOCAL_AI_API_KEY");
        return new PlutoLocalGatewayOptions
        {
            BaseUrl = string.IsNullOrWhiteSpace(url) ? DefaultBaseUrl : url.Trim(),
            ModelName = string.IsNullOrWhiteSpace(model) ? DefaultModelName : model.Trim(),
            IsEnabled = Environment.GetEnvironmentVariable("PFS_LOCAL_AI_ENABLED") == "1",
            TimeoutMs = int.TryParse(Environment.GetEnvironmentVariable("PFS_LOCAL_AI_TIMEOUT_MS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) && t > 0 ? t : DefaultTimeoutMs,
            AllowRemote = Environment.GetEnvironmentVariable("PFS_LOCAL_AI_ALLOW_REMOTE") == "1",
            ApiKey = string.IsNullOrWhiteSpace(key) ? null : key.Trim(),
        };
    }

    /// <summary>BaseUrl 검증 — 절대 http/https · 쿼리/프래그먼트 제거 · 비루프백은 <see cref="AllowRemote"/> 필요.</summary>
    public bool TryResolveBase(out Uri baseUri)
    {
        baseUri = null!;
        var raw = BaseUrl;
        if (string.IsNullOrWhiteSpace(raw) || !Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;
        if (!u.IsLoopback && !AllowRemote) return false;
        var path = u.AbsolutePath.TrimEnd('/');
        baseUri = new Uri(u.GetLeftPart(UriPartial.Authority) + path + "/", UriKind.Absolute);
        return true;
    }
}
