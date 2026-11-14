using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using PromptFlowStudio.Services.Account;   // LicenseService.DataAreaRoot()
using PromptFlowStudio.Services.Render;    // RenderEngineKind
using PromptFlowStudio.Services.Security;  // LocalCipher(SNS 자격 봉인)

namespace PromptFlowStudio.Services.Configuration;

/// <summary>사용자 환경설정 영속본(<c>user_settings.json</c>) — 비밀이 아닌 값만. API 키는 Wan/Gemini 설정 서비스가 머신바운드 암호화로 보관.</summary>
public sealed record UserSettings(string ComfyUiUrl, string RenderMode);

/// <summary>
/// SNS 채널 자격 — YouTube(OAuth Client/RefreshToken) · Instagram(Graph UserId/AccessToken · 로컬 웹훅 브릿지 URL).
/// 디스크에는 필드별 LocalCipher(AES-256-GCM · 머신바운드) 봉인본만 기록. <see cref="ToString"/> 은 비밀 마스킹.
///   ▸ <see cref="YouTubeChannelName"/>: 브라우저 OAuth 연동 시 조회한 채널 표시명 · <see cref="InstagramTokenSavedUtc"/>: 60일 장기 토큰 저장 시각(ISO-8601 UTC).
/// </summary>
public sealed record SnsCredentials(
    string YouTubeClientId, string YouTubeClientSecret, string YouTubeRefreshToken,
    string InstagramUserId, string InstagramAccessToken, string InstagramWebhookUrl,
    string YouTubeChannelName = "", string InstagramTokenSavedUtc = "")
{
    public static readonly SnsCredentials Empty = new("", "", "", "", "", "");
    public bool YouTubeConfigured => YouTubeClientId.Length > 0 && YouTubeClientSecret.Length > 0 && YouTubeRefreshToken.Length > 0;
    public bool InstagramConfigured => InstagramUserId.Length > 0 && InstagramAccessToken.Length > 0;
    public bool InstagramBridgeConfigured => InstagramWebhookUrl.Length > 0;

    public override string ToString() =>
        "SnsCredentials { YouTube = " + (YouTubeConfigured ? "***" : "-") + ", Instagram = " + (InstagramConfigured ? "***" : "-") + " }";
}

/// <summary>
/// [PromptFlowStudio] 앱 환경설정 관리자 — 순수 BCL(System.Text.Json · 외부 NuGet 0).
///   ▸ 우선순위: <c>user_settings.json</c>(사용자 저장본) → 번들 <c>appsettings.json</c>(<c>ComfyUI:BaseUrl</c> · <c>Render:DefaultMode</c>) → 내장 기본값.
///   ▸ 저장은 .tmp 기록 후 교체(원자적) · 인메모리 스냅샷 참조 교체(읽기 lock-free) · 저장 성공 시 <see cref="OnChanged"/> · <see cref="OnSettingsChanged"/>(구독자별 예외 격리).
///   ▸ 환경변수 <c>PFS_COMFY_URL</c> 은 기존 동작대로 최우선(<see cref="EffectiveComfyUiUrl"/>).
///   ▸ Google Drive 미러(<see cref="MemberRegistryService"/> 동일 규약): 로컬 부재 + 미러 존재 시 시작 시 자동 복원, 저장 성공 시 미러로 원자 복제(실패는 로컬 동작 무영향).
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
    /// <summary>저장 성공 시 새 스냅샷 전달 — 헬스체크/렌더 엔진 URL 핫리로드(재시작 불필요).</summary>
    public event Action<UserSettings>? OnSettingsChanged;
    public string FilePath { get; }
    /// <summary>사용자 저장본에서 로드했는지(false = 번들 기본값).</summary>
    public bool LoadedFromUser { get; private set; }

    /// <summary>미러(백업) 디렉터리 — null 이면 미러 비활성.</summary>
    public string? MirrorDir { get; }
    /// <summary>최근 미러 실패 사유(진단용).</summary>
    public string? LastMirrorError { get; private set; }
    /// <summary>시작 시 미러에서 user_settings.json 을 복원했는지.</summary>
    public bool RestoredFromMirror { get; private set; }

    public AppSettingsManager(IConfiguration cfg)
        : this(cfg, Path.Combine(LicenseService.DataAreaRoot(), FileName), MemberRegistryService.ResolveMirrorDir()) { }

    /// <param name="filePath">user_settings.json 경로(테스트 주입).</param>
    /// <param name="mirrorDir">미러 디렉터리(Google Drive) — null/공백이면 미러 비활성(테스트 기본).</param>
    public AppSettingsManager(IConfiguration? cfg, string filePath, string? mirrorDir = null)
    {
        FilePath = filePath;
        MirrorDir = string.IsNullOrWhiteSpace(mirrorDir) ? null : mirrorDir;
        TryRestoreFromMirror();
        _bundled = new UserSettings(NormalizeUrl(cfg?["ComfyUI:BaseUrl"]), NormalizeMode(cfg?["Render:DefaultMode"]));
        _s = LoadInitial();
        SnsFilePath = Path.Combine(Path.GetDirectoryName(filePath) ?? "", SnsFileName);
        _sns = LoadSns();
    }

    // ── SNS 채널 자격(암호화 저장소) ──
    public const string SnsFileName = "sns_credentials.json";
    private const string SnsPurpose = "MS2E::PromptFlowStudio::SNS::v1";
    private volatile SnsCredentials _sns;

    /// <summary>SNS 자격 저장 성공 시 새 스냅샷 전달(퍼블리셔 핫리로드).</summary>
    public event Action<SnsCredentials>? OnSnsChanged;
    public string SnsFilePath { get; }
    public SnsCredentials Sns => _sns;

    /// <summary>SNS 자격 원자적 저장 — 필드별 LocalCipher 봉인 후 .tmp → 교체. 실패 시 false(상태 불변).</summary>
    public bool SaveSns(SnsCredentials? creds)
    {
        var c = Normalize(creds ?? SnsCredentials.Empty);
        lock (_gate)
        {
            try
            {
                var dir = Path.GetDirectoryName(SnsFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var sealedDto = new SnsCredentials(Seal(c.YouTubeClientId), Seal(c.YouTubeClientSecret), Seal(c.YouTubeRefreshToken),
                    Seal(c.InstagramUserId), Seal(c.InstagramAccessToken), Seal(c.InstagramWebhookUrl),
                    Seal(c.YouTubeChannelName), Seal(c.InstagramTokenSavedUtc));
                var tmp = SnsFilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(sealedDto, JsonOpts));
                File.Move(tmp, SnsFilePath, overwrite: true);
                _sns = c;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.Cryptography.CryptographicException)
            {
                Program.Log("AppSettingsManager.SaveSns", ex);
                return false;
            }
        }
        var handlers = OnSnsChanged;
        if (handlers is not null)
            foreach (var d in handlers.GetInvocationList())
            {
                try { ((Action<SnsCredentials>)d)(c); } catch (Exception ex) { Program.Log("AppSettingsManager.OnSnsChanged", ex); }
            }
        return true;
    }

    private static string Seal(string v) => v.Length == 0 ? "" : LocalCipher.Protect(v, SnsPurpose);
    private static string Open(string? v) => string.IsNullOrEmpty(v) ? "" : LocalCipher.Unprotect(v, SnsPurpose);

    private static SnsCredentials Normalize(SnsCredentials c) => new(
        (c.YouTubeClientId ?? "").Trim(), (c.YouTubeClientSecret ?? "").Trim(), (c.YouTubeRefreshToken ?? "").Trim(),
        (c.InstagramUserId ?? "").Trim(), (c.InstagramAccessToken ?? "").Trim(), (c.InstagramWebhookUrl ?? "").Trim(),
        (c.YouTubeChannelName ?? "").Trim(), (c.InstagramTokenSavedUtc ?? "").Trim());

    private SnsCredentials LoadSns()
    {
        try
        {
            if (File.Exists(SnsFilePath) && JsonSerializer.Deserialize<SnsCredentials>(File.ReadAllText(SnsFilePath)) is { } s)
                return new SnsCredentials(Open(s.YouTubeClientId), Open(s.YouTubeClientSecret), Open(s.YouTubeRefreshToken),
                    Open(s.InstagramUserId), Open(s.InstagramAccessToken), Open(s.InstagramWebhookUrl),
                    Open(s.YouTubeChannelName), Open(s.InstagramTokenSavedUtc));   // 타 PC/손상 → 빈 값(Graceful) · 구버전 파일은 신규 필드 ""
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Program.Log("AppSettingsManager.LoadSns", ex);
        }
        return SnsCredentials.Empty;
    }

    public UserSettings Current => _s;
    public UserSettings Bundled => _bundled;
    public string ComfyUiUrl => _s.ComfyUiUrl;
    public string RenderMode => _s.RenderMode;
    public bool IsMockMode => string.Equals(_s.RenderMode, RenderEngineKind.Mock, StringComparison.OrdinalIgnoreCase);

    /// <summary>렌더 모드만 즉시 영구 저장(저장된 ComfyUI 주소 유지) — 환경설정 칩 클릭 시 호출.</summary>
    public bool SaveRenderMode(string? renderMode) => Save(_s.ComfyUiUrl, renderMode);

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
            MirrorToBackup();
        }
        try { OnChanged?.Invoke(); } catch (Exception ex) { Program.Log("AppSettingsManager.OnChanged", ex); }
        var snap = _s;
        var handlers = OnSettingsChanged;
        if (handlers is not null)
            foreach (var d in handlers.GetInvocationList())
            {
                try { ((Action<UserSettings>)d)(snap); } catch (Exception ex) { Program.Log("AppSettingsManager.OnSettingsChanged", ex); }
            }
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

    /// <summary>렌더 모드 정규화 — 대소문자 무시 + 별칭(cloud/dashscope/wan/wan21 → Cloud · mock/simulation → Mock), 그 외 ComfyUI.</summary>
    public static string NormalizeMode(string? mode)
    {
        var m = mode?.Trim();
        if (string.IsNullOrEmpty(m)) return RenderEngineKind.ComfyUi;
        if (string.Equals(m, RenderEngineKind.Cloud, StringComparison.OrdinalIgnoreCase)
            || string.Equals(m, "dashscope", StringComparison.OrdinalIgnoreCase)
            || string.Equals(m, "wan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(m, "wan21", StringComparison.OrdinalIgnoreCase)
            || string.Equals(m, "wan2.1", StringComparison.OrdinalIgnoreCase)) return RenderEngineKind.Cloud;
        if (string.Equals(m, RenderEngineKind.Mock, StringComparison.OrdinalIgnoreCase)
            || string.Equals(m, "simulation", StringComparison.OrdinalIgnoreCase)) return RenderEngineKind.Mock;
        return RenderEngineKind.ComfyUi;
    }

    /// <summary>즉시 미러 플러시 — 로컬 user_settings.json 부재 시 현재 스냅샷 저장(미러 포함), 존재 시 미러만 복제. 실패 사유는 <see cref="LastMirrorError"/>.</summary>
    public bool FlushMirror()
    {
        if (MirrorDir is null) return false;
        if (!File.Exists(FilePath))
        {
            var cur = _s;
            return Save(cur.ComfyUiUrl, cur.RenderMode) && LastMirrorError is null;
        }
        lock (_gate) MirrorToBackup();
        return LastMirrorError is null;
    }

    private string? MirrorPath => MirrorDir is null ? null : Path.Combine(MirrorDir, FileName);

    /// <summary>로컬 user_settings.json 이 없고 미러에 유효한 설정이 있으면 복원. 기존 로컬 파일은 절대 덮어쓰지 않음.</summary>
    private void TryRestoreFromMirror()
    {
        var src = MirrorPath;
        if (src is null) return;
        try
        {
            if (File.Exists(FilePath) || !File.Exists(src)) return;
            if (JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(src)) is null) return;   // 스키마 검증 실패 시 복원 안 함
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = FilePath + ".restore.tmp";
            File.Copy(src, tmp, overwrite: true);
            File.Move(tmp, FilePath, overwrite: false);
            RestoredFromMirror = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            LastMirrorError = ex.Message;
        }
    }

    /// <summary>로컬 user_settings.json 을 미러로 원자 복제(tmp 기록 후 교체). 실패해도 로컬 저장 결과에 영향 없음.</summary>
    private void MirrorToBackup()
    {
        var dst = MirrorPath;
        if (dst is null) return;
        try
        {
            if (!File.Exists(FilePath)) return;
            Directory.CreateDirectory(MirrorDir!);
            var tmp = dst + ".tmp";
            File.Copy(FilePath, tmp, overwrite: true);
            if (File.Exists(dst)) File.Replace(tmp, dst, null, ignoreMetadataErrors: true);
            else File.Move(tmp, dst);
            LastMirrorError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LastMirrorError = ex.Message;
        }
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
