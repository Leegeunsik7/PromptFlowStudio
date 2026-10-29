using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using PromptFlowStudio.Services.Account;   // LicenseService.DataAreaRoot()
using PromptFlowStudio.Services.Render;    // RenderEngineKind

namespace PromptFlowStudio.Services.Configuration;

/// <summary>사용자 환경설정 영속본(<c>user_settings.json</c>) — 비밀이 아닌 값만. API 키는 Wan/Gemini 설정 서비스가 머신바운드 암호화로 보관.</summary>
public sealed record UserSettings(string ComfyUiUrl, string RenderMode);

/// <summary>
/// [PromptFlowStudio] 앱 환경설정 관리자 — 순수 BCL(System.Text.Json · 외부 NuGet 0).
///   ▸ 우선순위: <c>user_settings.json</c>(사용자 저장본) → 번들 <c>appsettings.json</c>(<c>ComfyUI:BaseUrl</c> · <c>Render:DefaultMode</c>) → 내장 기본값.
///   ▸ 저장은 .tmp 기록 후 교체(원자적) · 인메모리 스냅샷 참조 교체(읽기 lock-free) · 저장 성공 시 <see cref="OnChanged"/>.
///   ▸ 환경변수 <c>PFS_COMFY_URL</c> 은 기존 동작대로 최우선(<see cref="EffectiveComfyUiUrl"/>).
/// </summary>
public sealed class AppSettingsManager
{
    public const string DefaultComfyUiUrl = "http://127.0.0.1:8188";
    public const string FileName = "user_settings.json";
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(2);
    private static readonly HttpClient s_http = new() { Timeout = PingTimeout };
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly UserSettings _bundled;
    private volatile UserSettings _s;

    public event Action? OnChanged;
    public string FilePath { get; }
    /// <summary>사용자 저장본에서 로드했는지(false = 번들 기본값).</summary>
    public bool LoadedFromUser { get; private set; }

    public AppSettingsManager(IConfiguration cfg) : this(cfg, Path.Combine(LicenseService.DataAreaRoot(), FileName)) { }

    /// <param name="filePath">user_settings.json 경로(테스트 주입).</param>
    public AppSettingsManager(IConfiguration? cfg, string filePath)
    {
        FilePath = filePath;
        _bundled = new UserSettings(NormalizeUrl(cfg?["ComfyUI:BaseUrl"]), NormalizeMode(cfg?["Render:DefaultMode"]));
        _s = LoadInitial();
    }

    public UserSettings Current => _s;
    public UserSettings Bundled => _bundled;
    public string ComfyUiUrl => _s.ComfyUiUrl;
    public string RenderMode => _s.RenderMode;
    public bool IsMockMode => string.Equals(_s.RenderMode, RenderEngineKind.Mock, StringComparison.Ordinal);

    /// <summary>실사용 ComfyUI 주소 — <c>PFS_COMFY_URL</c> → 저장값.</summary>
    public string EffectiveComfyUiUrl
        => Environment.GetEnvironmentVariable("PFS_COMFY_URL") is { Length: > 0 } u ? u.TrimEnd('/') : _s.ComfyUiUrl;

    /// <summary>원자적 저장 — 디스크 성공 후에만 스냅샷 교체. 실패 시 false(상태 불변).</summary>
    public bool Save(string? comfyUiUrl, string? renderMode)
    {
        var next = new UserSettings(NormalizeUrl(comfyUiUrl), NormalizeMode(renderMode));
        lock (_gate)
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(next, JsonOpts));
                File.Move(tmp, FilePath, overwrite: true);
                _s = next;
                LoadedFromUser = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                Program.Log("AppSettingsManager.Save", ex);
                return false;
            }
        }
        try { OnChanged?.Invoke(); } catch (Exception ex) { Program.Log("AppSettingsManager.OnChanged", ex); }
        return true;
    }

    /// <summary>ComfyUI 연결 테스트 — <c>GET {url}/system_stats</c> 2xx 면 true(2초 타임아웃 · 예외 격리).</summary>
    public static async Task<bool> PingComfyUiAsync(string? url, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(PingTimeout);
        try
        {
            using var res = await s_http.GetAsync(NormalizeUrl(url) + "/system_stats", HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException or UriFormatException) { return false; }
    }

    /// <summary>URL 정규화 — 공백/끝 '/' 제거, 비면 기본값.</summary>
    public static string NormalizeUrl(string? url)
        => string.IsNullOrWhiteSpace(url) ? DefaultComfyUiUrl : url.Trim().TrimEnd('/');

    /// <summary>렌더 모드 정규화 — ComfyUI / Cloud / Mock(대소문자 무시), 그 외 ComfyUI.</summary>
    public static string NormalizeMode(string? mode)
    {
        var m = mode?.Trim();
        if (string.Equals(m, RenderEngineKind.Cloud, StringComparison.OrdinalIgnoreCase)) return RenderEngineKind.Cloud;
        if (string.Equals(m, RenderEngineKind.Mock, StringComparison.OrdinalIgnoreCase)) return RenderEngineKind.Mock;
        return RenderEngineKind.ComfyUi;
    }

    private UserSettings LoadInitial()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var u = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath));
                if (u is not null)
                {
                    LoadedFromUser = true;
                    return new UserSettings(
                        string.IsNullOrWhiteSpace(u.ComfyUiUrl) ? _bundled.ComfyUiUrl : NormalizeUrl(u.ComfyUiUrl),
                        string.IsNullOrWhiteSpace(u.RenderMode) ? _bundled.RenderMode : NormalizeMode(u.RenderMode));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Program.Log("AppSettingsManager.Load", ex);   // 손상 → 번들 기본값(Graceful)
        }
        return _bundled;
    }
}
