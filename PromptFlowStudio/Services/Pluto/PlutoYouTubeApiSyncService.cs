using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;   // #if WINDOWS 구간 ProtectedData(DPAPI · WindowsDesktop 공유 프레임워크 · 외부 NuGet 0)
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Platform;
using PromptFlowStudio.Services.Security;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🔄 Step 33 — 동기화 대상 채널 1개(<c>UC…</c> 24자 · 지역 채널 로캘 → 로캘 변형 연결의 약한 신호).</summary>
public sealed record PlutoYouTubeApiChannel(string ChannelId, string? Locale = null);

/// <summary>
/// 🔐 Step 33 — YouTube Data API v3 자격 증명(로컬 전용 · 봉인 저장). 읽기 전용 API 키 또는 OAuth(클라이언트 ID/시크릿 + 리프레시 토큰) 중 하나 이상.
/// OAuth 가 있으면 우선(Bearer) · 없으면 API 키(<c>X-Goog-Api-Key</c> 헤더 — URL 에 키를 싣지 않음).
/// </summary>
public sealed record PlutoYouTubeApiCredentials
{
    public string? ApiKey { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string? RefreshToken { get; init; }
    public string? AccessToken { get; init; }
    public DateTimeOffset? AccessTokenExpiresUtc { get; init; }
    public IReadOnlyList<PlutoYouTubeApiChannel> Channels { get; init; } = Array.Empty<PlutoYouTubeApiChannel>();

    public bool HasOAuth => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret) && !string.IsNullOrWhiteSpace(RefreshToken);
    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);
    public bool IsConfigured => (HasOAuth || HasApiKey) && Channels.Count > 0;

    /// <summary>로그 · 진단 · UI 노출용 — 비밀값 미포함.</summary>
    public override string ToString() => "PlutoYouTubeApiCredentials { Auth = " + (HasOAuth ? "OAuth" : HasApiKey ? "ApiKey" : "None") + ", Channels = " + Channels.Count.ToString(CultureInfo.InvariantCulture) + " }";
}

/// <summary>🔄 동기화 결과 상태.</summary>
public enum PlutoYouTubeApiSyncState
{
    Synced,
    /// <summary>[부분 구제] — 일부 채널/배치만 처리(예산 · 쿼터 · 오류) · 받은 행은 반영됨.</summary>
    Partial,
    NotConfigured,
    /// <summary>네트워크 금지(<c>PROMPTFLOW_DISABLE_YOUTUBE_API=1</c> · 테스트 하네스 <c>PROMPTFLOW_PLUTO_DRIVE=off</c>).</summary>
    Disabled,
    /// <summary>이미 동기화 중(더블 클릭 · 동시 호출).</summary>
    Busy,
    /// <summary><c>403 quotaExceeded</c> 이후 태평양 자정까지 냉각.</summary>
    QuotaCooldown,
    /// <summary>자체 일일 예산(<see cref="PlutoYouTubeApiSyncService.DefaultDailyUnitBudget"/>) 소진.</summary>
    BudgetExhausted,
    AuthFailed,
    Failed,
}

/// <summary>🔄 동기화/연결 테스트 결과.</summary>
public sealed record PlutoYouTubeApiSyncResult(PlutoYouTubeApiSyncState State, string SummaryKorean)
{
    public int ChannelsSynced { get; init; }
    public int VideosFetched { get; init; }
    public int HttpRequests { get; init; }
    public int QuotaUnitsUsed { get; init; }
    public int Retries { get; init; }
    public PlutoTelemetryBatchImportResult? Import { get; init; }
    public bool Succeeded => State is PlutoYouTubeApiSyncState.Synced or PlutoYouTubeApiSyncState.Partial;
}

/// <summary>🔄 설정 카드 표시용 상태(비밀값 없음).</summary>
public sealed record PlutoYouTubeApiStatusSnapshot(bool IsConfigured, bool IsNetworkAllowed, string AuthMode, string MaskedCredential, int ChannelCount,
    DateTimeOffset? LastSyncUtc, string? LastSummaryKorean, int QuotaUnitsUsedToday, int DailyUnitBudget, DateTimeOffset? CooldownUntilUtc, string CredentialStorePath);

/// <summary>🔐 자격 증명 저장 결과.</summary>
public sealed record PlutoYouTubeApiSaveResult(bool Ok, string MessageKorean);

/// <summary>🔄 Step 33 — YouTube Data API v3 다채널 실시간 텔레메트리 동기화.</summary>
public interface IPlutoYouTubeApiSyncService
{
    bool IsConfigured { get; }
    /// <summary>실 네트워크 호출 허용(미설정 · 킬 스위치 · 테스트 하네스 → false).</summary>
    bool IsNetworkAllowed { get; }
    event Action? Changed;
    PlutoYouTubeApiStatusSnapshot GetStatus();
    /// <summary>입력 즉시 스키마 검증(채널 ID 형식 · 로캘) → 봉인 저장(로컬 전용 · Drive 경로 거부). 빈 필드 · 빈 채널 목록 = 기존 저장값 유지(비밀값을 UI 로 되읽지 않기 위함).</summary>
    Task<PlutoYouTubeApiSaveResult> SaveCredentialsAsync(PlutoYouTubeApiCredentials credentials, CancellationToken ct = default);
    /// <summary><c>channels.list</c> 1회(1 unit)로 채널 · 업로드 재생목록 확인.</summary>
    Task<PlutoYouTubeApiSyncResult> TestConnectionAsync(CancellationToken ct = default);
    /// <summary><c>channels.list</c> → <c>playlistItems.list</c> → 50개 묶음 <c>videos.list</c> → 카탈로그 연결 · 관측 반영.</summary>
    Task<PlutoYouTubeApiSyncResult> SyncNowAsync(CancellationToken ct = default);
    /// <summary>연결 해제 — 봉인 파일 덮어쓰기 후 삭제 · 메모리 비밀 폐기 · (가능하면) 리프레시 토큰 폐기 요청.</summary>
    Task DisconnectAsync(CancellationToken ct = default);
}

/// <summary>
/// 🔄 <see cref="IPlutoYouTubeApiSyncService"/> 구현(순수 BCL).
///   ▸ 🔐 자격 증명: <c>%LOCALAPPDATA%\PromptFlowStudio\PlutoLocalOnly\</c> 에 DPAPI(CurrentUser · Windows) / AES-GCM 머신 바운드(그 외) 봉인 — Drive · 클라우드 동기화 경로는 거부.
///   ▸ 💸 쿼터: 요청 1건 = 1 unit 로 보수 집계 · 태평양 자정 기준 일일 자체 예산 · <c>quotaExceeded</c> → 자정까지 냉각(영속) · 채널 50개 · 영상 50개 묶음.
///   ▸ ⏳ 백오프: 429 · 5xx · <c>rateLimitExceeded</c> → <c>Retry-After</c>(상한 30초) 또는 지수 1·2·4초 · 401 → 토큰 1회 갱신 후 재시도.
///   ▸ 🧪 격리: 기본 전송(실 네트워크)은 <c>PROMPTFLOW_PLUTO_DRIVE=off</c> 에서 금지 · <c>PROMPTFLOW_DISABLE_YOUTUBE_API=1</c> 은 주입 전송까지 전부 차단.
/// </summary>
public sealed class PlutoYouTubeApiSyncService : IPlutoYouTubeApiSyncService, IDisposable
{
    public const string DisableEnvVar = "PROMPTFLOW_DISABLE_YOUTUBE_API";
    public const string CredentialFileName = "pluto_youtube_api_credentials.sealed";
    public const string StateFileName = "pluto_youtube_api_state.json";
    public const string LocalOnlyFolderName = "PlutoLocalOnly";
    public const string ApiBase = "https://www.googleapis.com/youtube/v3/";
    public const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    public const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";
    public const int MaxIdsPerRequest = 50;
    public const int MaxChannels = 8;
    public const int MaxRecentUploadsPerChannel = 50;
    public const int DefaultDailyUnitBudget = 1500;
    public const int MaxAttempts = 3;
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    /// <summary>만료 이만큼 전이면 미리 갱신.</summary>
    public static readonly TimeSpan TokenRefreshSkew = TimeSpan.FromSeconds(120);

    private static readonly byte[] Magic = "PFYT"u8.ToArray();
    private const byte SchemeDpapi = 1, SchemeLocalCipher = 2;
    private static readonly byte[] Entropy = "pfs-pluto-youtube-api-v1"u8.ToArray();
    private const string CipherPurpose = "pfs-pluto-youtube-api";

    private readonly IPlutoTelemetryBatchImporter? _importer;
    private readonly HttpMessageHandler? _injectedHandler;
    private readonly string _dir;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly object _sync = new();
    private HttpClient? _http;
    private PlutoYouTubeApiCredentials? _creds;
    private bool _credsLoaded;
    private StateDto _state = new();
    private bool _stateLoaded;
    private long _networkAttempts;
    private bool _disposed;

    public event Action? Changed;

    /// <summary>시계(테스트 주입).</summary>
    public Func<DateTimeOffset> UtcNow { get; init; } = static () => DateTimeOffset.UtcNow;

    /// <summary>백오프 대기(테스트는 기록만 하고 즉시 반환).</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = static (d, ct) => Task.Delay(d, ct);

    /// <summary>자체 일일 쿼터 예산(Google 기본 10,000 unit 중 다른 도구 몫을 남김).</summary>
    public int DailyUnitBudget { get; init; } = DefaultDailyUnitBudget;

    public string PlatformName { get; init; } = "YouTube Shorts";

    /// <summary>실제 HTTP 송신 시도 수(토큰 엔드포인트 포함 · 격리 검증용).</summary>
    public long NetworkAttemptCount => Interlocked.Read(ref _networkAttempts);

    /// <summary>봉인 파일 경로(Drive 경로였다면 로컬 기본 폴더로 대체된 결과).</summary>
    public string CredentialStorePath => Path.Combine(_dir, CredentialFileName);

    /// <summary>요청한 저장 폴더가 클라우드 동기화 경로라 로컬 기본 폴더로 대체됨.</summary>
    public bool StoreDirectoryRejected { get; }

    /// <param name="importer">카탈로그 연결 · 관측 반영(null → 받기만 하고 반영 생략).</param>
    /// <param name="handler">주입 전송(오프라인 테스트) — 소유권은 호출자. null → 실 네트워크(<see cref="SocketsHttpHandler"/>).</param>
    /// <param name="storeDirectory">봉인 · 상태 파일 폴더(null → 로컬 앱 데이터). 클라우드 동기화 경로는 거부.</param>
    public PlutoYouTubeApiSyncService(IPlutoTelemetryBatchImporter? importer = null, HttpMessageHandler? handler = null, string? storeDirectory = null)
    {
        _importer = importer;
        _injectedHandler = handler;
        var fallback = Path.Combine(AppDataPaths.LocalAppData, "PromptFlowStudio", LocalOnlyFolderName);
        if (string.IsNullOrWhiteSpace(storeDirectory)) _dir = fallback;
        else if (IsCloudSyncedPath(storeDirectory)) { _dir = fallback; StoreDirectoryRejected = true; }
        else _dir = storeDirectory.Trim();
    }

    // ── 🛡️ 경로 · 정책 ──

    /// <summary>
    /// ☁️ 비밀 저장 금지 경로 — Pluto Drive 볼트(<see cref="PlutoDriveStorageManager.DriveVaultRoot"/> · 현재 Primary) 및
    /// 흔한 클라우드 동기화 폴더(<c>내 드라이브</c> · <c>My Drive</c> · <c>Google Drive</c> · <c>OneDrive</c> · <c>Dropbox</c> · <c>iCloud</c>).
    /// </summary>
    public static bool IsCloudSyncedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string full;
        try { full = Path.GetFullPath(path.Trim()); }
        catch (Exception) { return true; }   // 해석 불가 경로는 거부(보수)
        var norm = full.Replace('/', '\\');
        if (StartsWithDir(norm, PlutoDriveStorageManager.DriveVaultRoot)) return true;
        string? primary = null;
        try { primary = PlutoDriveStorageManager.DefaultPrimaryRoot(); } catch (Exception) { /* 정책 조회 실패 → 이름 규칙만 */ }
        if (!string.IsNullOrEmpty(primary) && StartsWithDir(norm, primary)) return true;
        foreach (var marker in CloudMarkers)
            if (norm.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static readonly string[] CloudMarkers = { "\\내 드라이브", "\\My Drive", "\\Google Drive", "\\GoogleDrive", "\\OneDrive", "\\Dropbox", "\\iCloudDrive", "\\iCloud Drive" };

    private static bool StartsWithDir(string path, string root)
    {
        var r = root.Replace('/', '\\').TrimEnd('\\');
        return r.Length > 0 && (path.Equals(r, StringComparison.OrdinalIgnoreCase) || path.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase));
    }

    private static bool KillSwitchOn()
    {
        var v = Environment.GetEnvironmentVariable(DisableEnvVar)?.Trim();
        return v is "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "on", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HarnessIsolated()
        => string.Equals(Environment.GetEnvironmentVariable(PlutoDriveStorageManager.DriveEnvVar)?.Trim(), "off", StringComparison.OrdinalIgnoreCase);

    /// <summary>전송 정책 — 킬 스위치는 무조건 차단 · 실 네트워크(주입 전송 없음)는 하네스 격리 시 차단.</summary>
    public bool TransportAllowed => !KillSwitchOn() && (_injectedHandler is not null || !HarnessIsolated());

    public bool IsConfigured => LoadCredentials()?.IsConfigured == true;

    public bool IsNetworkAllowed => TransportAllowed && IsConfigured;

    // ── 🔐 봉인 저장 ──

    /// <summary>채널 ID 형식(<c>UC</c> + 22자 base64url).</summary>
    public static bool IsChannelId(string? s)
    {
        if (s is null || s.Length != 24 || !s.StartsWith("UC", StringComparison.Ordinal)) return false;
        foreach (var ch in s)
            if (!(ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')) return false;
        return true;
    }

    /// <summary>
    /// 설정 입력(줄/콤마 구분 · <c>UC…=ja-JP</c> 로 로캘 지정) → 채널 목록. 형식 위반 항목은 <paramref name="invalid"/> 에 담고 제외 · 중복 제거 · 최대 <see cref="MaxChannels"/>.
    /// </summary>
    public static List<PlutoYouTubeApiChannel> ParseChannelList(string? text, List<string> invalid)
    {
        ArgumentNullException.ThrowIfNull(invalid);
        var list = new List<PlutoYouTubeApiChannel>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in text.Split(new[] { '\n', '\r', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = raw.IndexOf('=');
            var id = (eq < 0 ? raw : raw[..eq]).Trim();
            string? locale = null;
            if (eq >= 0)
            {
                locale = PlutoMultiLanguageChannelService.NormalizeLocale(raw[(eq + 1)..].Trim());
                if (locale is null) { invalid.Add(raw + " (로캘 형식)"); continue; }
            }
            if (!IsChannelId(id)) { invalid.Add(raw + " (채널 ID 형식)"); continue; }
            if (!seen.Add(id)) continue;
            if (list.Count >= MaxChannels) { invalid.Add(raw + " (채널 상한 " + MaxChannels.ToString(CultureInfo.InvariantCulture) + ")"); continue; }
            list.Add(new PlutoYouTubeApiChannel(id, locale));
        }
        return list;
    }

    private sealed class CredDto
    {
        public string? ApiKey { get; set; }
        public string? ClientId { get; set; }
        public string? ClientSecret { get; set; }
        public string? RefreshToken { get; set; }
        public string? AccessToken { get; set; }
        public DateTimeOffset? AccessTokenExpiresUtc { get; set; }
        public List<ChannelDto>? Channels { get; set; }
    }

    private sealed class ChannelDto
    {
        public string? Id { get; set; }
        public string? Locale { get; set; }
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>봉인(DPAPI CurrentUser → 실패/비 Windows 시 머신 · 사용자 바운드 AES-GCM). 형식 = <c>PFYT</c> + 스킴 1바이트 + 봉인 본문.</summary>
    public static byte[] Seal(byte[] plain)
    {
        ArgumentNullException.ThrowIfNull(plain);
        byte scheme;
        byte[] body;
#if WINDOWS
        try { body = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); scheme = SchemeDpapi; }
        catch (CryptographicException) { body = Encoding.ASCII.GetBytes(LocalCipher.Protect(Encoding.UTF8.GetString(plain), CipherPurpose)); scheme = SchemeLocalCipher; }
#else
        body = Encoding.ASCII.GetBytes(LocalCipher.Protect(Encoding.UTF8.GetString(plain), CipherPurpose));
        scheme = SchemeLocalCipher;
#endif
        var buf = new byte[Magic.Length + 1 + body.Length];
        Buffer.BlockCopy(Magic, 0, buf, 0, Magic.Length);
        buf[Magic.Length] = scheme;
        Buffer.BlockCopy(body, 0, buf, Magic.Length + 1, body.Length);
        return buf;
    }

    /// <summary>개봉(형식 · 키 불일치 · 손상 → null · 예외 없음).</summary>
    public static byte[]? Unseal(byte[]? sealedBytes)
    {
        if (sealedBytes is null || sealedBytes.Length <= Magic.Length + 1) return null;
        for (var i = 0; i < Magic.Length; i++) if (sealedBytes[i] != Magic[i]) return null;
        var scheme = sealedBytes[Magic.Length];
        var body = new byte[sealedBytes.Length - Magic.Length - 1];
        Buffer.BlockCopy(sealedBytes, Magic.Length + 1, body, 0, body.Length);
        try
        {
            if (scheme == SchemeDpapi)
            {
#if WINDOWS
                return ProtectedData.Unprotect(body, Entropy, DataProtectionScope.CurrentUser);
#else
                return null;
#endif
            }
            if (scheme == SchemeLocalCipher)
            {
                var json = LocalCipher.Unprotect(Encoding.ASCII.GetString(body), CipherPurpose);
                return json.Length == 0 ? null : Encoding.UTF8.GetBytes(json);
            }
        }
        catch (CryptographicException) { }
        return null;
    }

    private PlutoYouTubeApiCredentials? LoadCredentials()
    {
        lock (_sync)
        {
            if (_credsLoaded) return _creds;
            _credsLoaded = true;
            try
            {
                var path = CredentialStorePath;
                if (!File.Exists(path)) return _creds = null;
                var plain = Unseal(File.ReadAllBytes(path));
                if (plain is null) return _creds = null;   // 다른 PC/사용자 봉인 → 재입력 필요(Drive 로 옮겨 다니지 않으므로 정상 경로 아님)
                try
                {
                    var dto = JsonSerializer.Deserialize<CredDto>(plain);
                    _creds = dto is null ? null : FromDto(dto);
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.YouTubeApi] load credentials: " + ex.GetType().Name); _creds = null; }
            return _creds;
        }
    }

    private static PlutoYouTubeApiCredentials FromDto(CredDto d)
    {
        var channels = new List<PlutoYouTubeApiChannel>();
        if (d.Channels is not null)
            foreach (var c in d.Channels)
                if (IsChannelId(c?.Id) && channels.Count < MaxChannels) channels.Add(new PlutoYouTubeApiChannel(c!.Id!, PlutoMultiLanguageChannelService.NormalizeLocale(c.Locale)));
        return new PlutoYouTubeApiCredentials
        {
            ApiKey = Clean(d.ApiKey), ClientId = Clean(d.ClientId), ClientSecret = Clean(d.ClientSecret), RefreshToken = Clean(d.RefreshToken),
            AccessToken = Clean(d.AccessToken), AccessTokenExpiresUtc = d.AccessTokenExpiresUtc, Channels = channels,
        };
    }

    private static CredDto ToDto(PlutoYouTubeApiCredentials c)
    {
        var list = new List<ChannelDto>(c.Channels.Count);
        foreach (var ch in c.Channels) list.Add(new ChannelDto { Id = ch.ChannelId, Locale = ch.Locale });
        return new CredDto
        {
            ApiKey = c.ApiKey, ClientId = c.ClientId, ClientSecret = c.ClientSecret, RefreshToken = c.RefreshToken,
            AccessToken = c.AccessToken, AccessTokenExpiresUtc = c.AccessTokenExpiresUtc, Channels = list,
        };
    }

    /// <summary>원자 기록(임시 → 교체) · 평문 버퍼 즉시 소거.</summary>
    private bool PersistCredentials(PlutoYouTubeApiCredentials c)
    {
        byte[]? plain = null;
        try
        {
            Directory.CreateDirectory(_dir);
            plain = JsonSerializer.SerializeToUtf8Bytes(ToDto(c));
            var sealedBytes = Seal(plain);
            var path = CredentialStorePath;
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, sealedBytes);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.YouTubeApi] persist credentials: " + ex.GetType().Name); return false; }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }

    public Task<PlutoYouTubeApiSaveResult> SaveCredentialsAsync(PlutoYouTubeApiCredentials credentials, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ct.ThrowIfCancellationRequested();
        var prev = LoadCredentials();
        if (prev is not null)
        {
            var newRefresh = Clean(credentials.RefreshToken);
            var rotated = newRefresh is not null && !string.Equals(newRefresh, prev.RefreshToken, StringComparison.Ordinal);
            credentials = credentials with
            {
                ApiKey = Clean(credentials.ApiKey) ?? prev.ApiKey,
                ClientId = Clean(credentials.ClientId) ?? prev.ClientId,
                ClientSecret = Clean(credentials.ClientSecret) ?? prev.ClientSecret,
                RefreshToken = newRefresh ?? prev.RefreshToken,
                AccessToken = rotated ? Clean(credentials.AccessToken) : Clean(credentials.AccessToken) ?? prev.AccessToken,
                AccessTokenExpiresUtc = rotated ? credentials.AccessTokenExpiresUtc : credentials.AccessTokenExpiresUtc ?? prev.AccessTokenExpiresUtc,
                Channels = credentials.Channels.Count == 0 ? prev.Channels : credentials.Channels,
            };
        }
        var channels = new List<PlutoYouTubeApiChannel>();
        var bad = new List<string>();
        foreach (var ch in credentials.Channels)
        {
            if (!IsChannelId(ch?.ChannelId?.Trim())) { bad.Add(ch?.ChannelId ?? "?"); continue; }
            var loc = ch!.Locale is null ? null : PlutoMultiLanguageChannelService.NormalizeLocale(ch.Locale);
            if (ch.Locale is not null && loc is null) { bad.Add(ch.ChannelId + "=" + ch.Locale); continue; }
            if (channels.Count < MaxChannels && !channels.Exists(x => string.Equals(x.ChannelId, ch.ChannelId.Trim(), StringComparison.Ordinal)))
                channels.Add(new PlutoYouTubeApiChannel(ch.ChannelId.Trim(), loc));
        }
        if (bad.Count > 0) return Task.FromResult(new PlutoYouTubeApiSaveResult(false, "⚠ 채널 형식 오류: " + string.Join(", ", bad) + " — UC로 시작하는 24자 채널 ID(선택: =ja-JP)"));
        var clean = new PlutoYouTubeApiCredentials
        {
            ApiKey = Clean(credentials.ApiKey), ClientId = Clean(credentials.ClientId), ClientSecret = Clean(credentials.ClientSecret),
            RefreshToken = Clean(credentials.RefreshToken), AccessToken = Clean(credentials.AccessToken), AccessTokenExpiresUtc = credentials.AccessTokenExpiresUtc, Channels = channels,
        };
        if (!clean.HasApiKey && !clean.HasOAuth) return Task.FromResult(new PlutoYouTubeApiSaveResult(false, "⚠ API 키 또는 OAuth(클라이언트 ID · 시크릿 · 리프레시 토큰) 중 하나가 필요합니다."));
        if (channels.Count == 0) return Task.FromResult(new PlutoYouTubeApiSaveResult(false, "⚠ 채널 ID를 1개 이상 입력하세요."));
        if (!PersistCredentials(clean)) return Task.FromResult(new PlutoYouTubeApiSaveResult(false, "⚠ 암호화 저장 실패(로컬 앱 데이터 쓰기 불가)."));
        lock (_sync)
        {
            _creds = clean;
            _credsLoaded = true;
            EnsureStateLoadedLocked();
            _state.UploadsPlaylists = new Dictionary<string, string>(StringComparer.Ordinal);   // 채널 구성이 바뀌었을 수 있음 → 캐시 폐기
            PersistStateLocked();
        }
        RaiseChanged();
        return Task.FromResult(new PlutoYouTubeApiSaveResult(true, "🔐 암호화 저장 완료(" + (clean.HasOAuth ? "OAuth" : "API 키") + " · 채널 " + channels.Count.ToString(CultureInfo.InvariantCulture) + "개 · 이 PC 전용 · Drive 미동기화)"));
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        PlutoYouTubeApiCredentials? old;
        old = LoadCredentials();
        lock (_sync) { _creds = null; _credsLoaded = true; }
        // 리프레시 토큰 폐기 요청(최선 노력 · 실패 무시 · 네트워크 정책 준수)
        if (old?.HasOAuth == true && TransportAllowed)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, RevokeEndpoint) { Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("token", old.RefreshToken!) }) };
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                Interlocked.Increment(ref _networkAttempts);
                using var _ = await Client().SendAsync(req, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { System.Diagnostics.Debug.WriteLine("[Pluto.YouTubeApi] revoke: " + ex.GetType().Name); }
        }
        WipeFile(CredentialStorePath);
        lock (_sync)
        {
            EnsureStateLoadedLocked();
            _state.UploadsPlaylists = new Dictionary<string, string>(StringComparer.Ordinal);
            _state.LastSummaryKorean = "🔌 연결 해제됨 — 저장된 자격 증명 삭제";
            PersistStateLocked();
        }
        RaiseChanged();
    }

    /// <summary>덮어쓰기(0) 후 삭제 — 휴지통 · 복구 도구로 봉인 사본이 남지 않게.</summary>
    private static void WipeFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var len = new FileInfo(path).Length;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                var zero = new byte[Math.Min(len, 4096)];
                for (long done = 0; done < len; done += zero.Length) fs.Write(zero, 0, (int)Math.Min(zero.Length, len - done));
                fs.Flush(true);
            }
            File.Delete(path);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.YouTubeApi] wipe: " + ex.GetType().Name); try { File.Delete(path); } catch (Exception) { } }
    }

    // ── 💸 쿼터 · 상태 ──

    private sealed class StateDto
    {
        public string? QuotaDayKey { get; set; }
        public int QuotaUnitsUsed { get; set; }
        public DateTimeOffset? CooldownUntilUtc { get; set; }
        public DateTimeOffset? LastSyncUtc { get; set; }
        public string? LastSummaryKorean { get; set; }
        public Dictionary<string, string>? UploadsPlaylists { get; set; }
    }

    private void EnsureStateLoadedLocked()
    {
        if (_stateLoaded) return;
        _stateLoaded = true;
        try
        {
            var path = Path.Combine(_dir, StateFileName);
            if (File.Exists(path)) _state = JsonSerializer.Deserialize<StateDto>(File.ReadAllBytes(path)) ?? new StateDto();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.YouTubeApi] state load: " + ex.GetType().Name); _state = new StateDto(); }
        _state.UploadsPlaylists ??= new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private void PersistStateLocked()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var path = Path.Combine(_dir, StateFileName);
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(_state));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.YouTubeApi] state persist: " + ex.GetType().Name); }   // [부분 구제] 메모리 유지
    }

    private static TimeZoneInfo? _pacific;

    /// <summary>YouTube 쿼터 재설정 기준(태평양 시각) — 조회 실패 시 UTC−8 고정.</summary>
    private static TimeZoneInfo Pacific()
    {
        if (_pacific is { } p) return p;
        foreach (var id in new[] { "Pacific Standard Time", "America/Los_Angeles" })
        {
            try { return _pacific = TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception) { }
        }
        return _pacific = TimeZoneInfo.CreateCustomTimeZone("PFS-PST", TimeSpan.FromHours(-8), "PST", "PST");
    }

    /// <summary>태평양 날짜 키(<c>yyyy-MM-dd</c>).</summary>
    public static string QuotaDayKey(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Pacific()).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>다음 태평양 자정(UTC).</summary>
    public static DateTimeOffset NextQuotaResetUtc(DateTimeOffset utc)
    {
        var tz = Pacific();
        var local = TimeZoneInfo.ConvertTime(utc, tz);
        var midnight = local.Date.AddDays(1);
        return new DateTimeOffset(midnight, tz.GetUtcOffset(midnight)).ToUniversalTime();
    }

    private void RollQuotaDayLocked(DateTimeOffset now)
    {
        var key = QuotaDayKey(now);
        if (string.Equals(_state.QuotaDayKey, key, StringComparison.Ordinal)) return;
        _state.QuotaDayKey = key;
        _state.QuotaUnitsUsed = 0;
    }

    public PlutoYouTubeApiStatusSnapshot GetStatus()
    {
        var c = LoadCredentials();
        lock (_sync)
        {
            EnsureStateLoadedLocked();
            RollQuotaDayLocked(UtcNow());
            var mode = c is null ? "미설정" : c.HasOAuth ? "OAuth" : c.HasApiKey ? "API 키" : "미설정";
            var masked = c is null ? "—" : c.HasOAuth ? Mask(c.ClientId) : Mask(c.ApiKey);
            var cooldown = _state.CooldownUntilUtc is { } cd && cd > UtcNow() ? cd : (DateTimeOffset?)null;
            return new PlutoYouTubeApiStatusSnapshot(c?.IsConfigured == true, TransportAllowed && c?.IsConfigured == true, mode, masked, c?.Channels.Count ?? 0,
                _state.LastSyncUtc, _state.LastSummaryKorean, _state.QuotaUnitsUsed, DailyUnitBudget, cooldown, CredentialStorePath);
        }
    }

    /// <summary>마지막 4자만 노출(8자 미만 → 전부 가림).</summary>
    public static string Mask(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return "—";
        return secret.Length < 8 ? "••••" : "••••" + secret[^4..];
    }

    /// <summary>🛡️ 오류 문구에서 비밀값 제거(상태 파일 · 토스트 · 진단으로 새지 않게).</summary>
    public static string Redact(string? message, PlutoYouTubeApiCredentials? c)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;
        var s = message;
        if (c is not null)
            foreach (var secret in new[] { c.ApiKey, c.ClientSecret, c.RefreshToken, c.AccessToken })
                if (!string.IsNullOrEmpty(secret) && secret.Length >= 6) s = s.Replace(secret, "***", StringComparison.Ordinal);
        return s.Length > 300 ? s[..300] : s;
    }

    // ── 🌐 HTTP ──

    private HttpClient Client()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _http ??= _injectedHandler is not null
                ? new HttpClient(_injectedHandler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan }
                : new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        }
    }

    /// <summary>쿼터 소진 신호(<c>quotaExceeded</c> · <c>dailyLimitExceeded</c>).</summary>
    private sealed class QuotaExhaustedException : Exception { public QuotaExhaustedException() : base("quotaExceeded") { } }
    /// <summary>자체 예산 소진.</summary>
    private sealed class BudgetExhaustedException : Exception { public BudgetExhaustedException() : base("budget") { } }
    /// <summary>인증 실패(리프레시 거부 · 401 반복 · 키 무효).</summary>
    private sealed class AuthFailedException : Exception { public AuthFailedException(string m) : base(m) { } }

    /// <summary>한 번의 동기화 실행 컨텍스트(요청 · 재시도 · unit 집계).</summary>
    private sealed class RunCounters
    {
        public int Requests, Retries, Units;
    }

    /// <summary>403/429 응답 본문의 첫 오류 reason(<c>quotaExceeded</c> 등 · 없음 → 빈 문자열).</summary>
    public static string ErrorReason(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            {
                if (err.TryGetProperty("errors", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var e in arr.EnumerateArray())
                        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String) return r.GetString() ?? string.Empty;
                if (err.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String) return st.GetString() ?? string.Empty;
            }
        }
        catch (JsonException) { }
        return string.Empty;
    }

    /// <summary>재시도 대기 — <c>Retry-After</c>(초 · 날짜) 우선 · 없으면 지수(1·2·4초) · 상한 <see cref="MaxRetryAfter"/>.</summary>
    public static TimeSpan BackoffDelay(HttpResponseMessage? response, int attempt, DateTimeOffset now)
    {
        TimeSpan d = TimeSpan.FromSeconds(Math.Pow(2, Math.Clamp(attempt - 1, 0, 5)));
        if (response?.Headers.RetryAfter is { } ra)
        {
            if (ra.Delta is { } delta) d = delta;
            else if (ra.Date is { } date) d = date - now;
        }
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        return d > MaxRetryAfter ? MaxRetryAfter : d;
    }

    private async Task<JsonDocument> GetJsonAsync(string relative, RunCounters run, CancellationToken ct)
    {
        var refreshedFor401 = false;
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            ChargeUnit(run);
            var creds = await EnsureAccessAsync(forceRefresh: false, ct).ConfigureAwait(false);
            using var req = new HttpRequestMessage(HttpMethod.Get, ApiBase + relative);
            if (creds.HasOAuth) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.AccessToken);
            else req.Headers.TryAddWithoutValidation("X-Goog-Api-Key", creds.ApiKey);   // 🔐 URL 에 키를 싣지 않음(로그 · 예외 메시지 · 프록시 기록 누출 차단)
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeout);
            HttpResponseMessage resp;
            Interlocked.Increment(ref _networkAttempts);
            run.Requests++;
            try { resp = await Client().SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (attempt >= MaxAttempts) throw new HttpRequestException("요청 시간 초과");
                run.Retries++;
                await Delay(BackoffDelay(null, attempt, UtcNow()), ct).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException) when (attempt < MaxAttempts)
            {
                run.Retries++;
                await Delay(BackoffDelay(null, attempt, UtcNow()), ct).ConfigureAwait(false);
                continue;
            }
            using (resp)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    try { return JsonDocument.Parse(body); }
                    catch (JsonException) { throw new InvalidDataException("YouTube API 응답 스키마 위반(JSON 아님)"); }
                }
                var reason = ErrorReason(body);
                var code = (int)resp.StatusCode;
                if (code == 403 && reason is "quotaExceeded" or "dailyLimitExceeded") throw new QuotaExhaustedException();
                if (code == 401 && creds.HasOAuth && !refreshedFor401)
                {
                    refreshedFor401 = true;
                    await EnsureAccessAsync(forceRefresh: true, ct).ConfigureAwait(false);
                    attempt--;   // 토큰 갱신 재시도는 백오프 시도로 세지 않음
                    continue;
                }
                if (code is 401 || (code == 400 && reason == "keyInvalid") || (code == 403 && reason is "forbidden" or "accessNotConfigured" or "keyInvalid" or "ipRefererBlocked"))
                    throw new AuthFailedException("HTTP " + code.ToString(CultureInfo.InvariantCulture) + (reason.Length > 0 ? " " + reason : ""));
                var retryable = code == 429 || code >= 500 || (code == 403 && reason is "rateLimitExceeded" or "userRateLimitExceeded");
                if (!retryable || attempt >= MaxAttempts)
                    throw new HttpRequestException("YouTube API HTTP " + code.ToString(CultureInfo.InvariantCulture) + (reason.Length > 0 ? " " + reason : ""));
                run.Retries++;
                await Delay(BackoffDelay(resp, attempt, UtcNow()), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>요청 전 1 unit 선차감(실패 요청도 Google 이 집계할 수 있어 보수적) · 자체 예산 초과 → 중단.</summary>
    private void ChargeUnit(RunCounters run)
    {
        lock (_sync)
        {
            EnsureStateLoadedLocked();
            RollQuotaDayLocked(UtcNow());
            if (_state.QuotaUnitsUsed + 1 > DailyUnitBudget) throw new BudgetExhaustedException();
            _state.QuotaUnitsUsed++;
            run.Units++;
        }
    }

    /// <summary>OAuth 액세스 토큰 보장(만료 <see cref="TokenRefreshSkew"/> 전 갱신 · 회전된 리프레시 토큰 반영 · 봉인 재저장). API 키 모드는 그대로.</summary>
    private async Task<PlutoYouTubeApiCredentials> EnsureAccessAsync(bool forceRefresh, CancellationToken ct)
    {
        var c = LoadCredentials() ?? throw new AuthFailedException("미설정");
        if (!c.HasOAuth) return c;
        var now = UtcNow();
        if (!forceRefresh && !string.IsNullOrEmpty(c.AccessToken) && c.AccessTokenExpiresUtc is { } exp && exp - now > TokenRefreshSkew) return c;
        using var req = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client_id", c.ClientId!),
                new KeyValuePair<string, string>("client_secret", c.ClientSecret!),
                new KeyValuePair<string, string>("refresh_token", c.RefreshToken!),
                new KeyValuePair<string, string>("grant_type", "refresh_token"),
            }),
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RequestTimeout);
        Interlocked.Increment(ref _networkAttempts);
        using var resp = await Client().SendAsync(req, cts.Token).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new AuthFailedException("토큰 갱신 거부 HTTP " + ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture) + " — 다시 연결하세요");
        string? access = null, rotated = null;
        long expiresIn = 0;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("access_token", out var a) && a.ValueKind == JsonValueKind.String) access = a.GetString();
                if (root.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number) e.TryGetInt64(out expiresIn);
                if (root.TryGetProperty("refresh_token", out var r) && r.ValueKind == JsonValueKind.String) rotated = r.GetString();
            }
        }
        catch (JsonException) { }
        if (string.IsNullOrWhiteSpace(access) || expiresIn <= 0) throw new AuthFailedException("토큰 응답 스키마 위반");
        var next = c with
        {
            AccessToken = access, AccessTokenExpiresUtc = now.AddSeconds(Math.Min(expiresIn, 86_400)),
            RefreshToken = string.IsNullOrWhiteSpace(rotated) ? c.RefreshToken : rotated,
        };
        lock (_sync) { _creds = next; _credsLoaded = true; }
        PersistCredentials(next);   // 실패 → [부분 구제] 메모리 토큰으로 계속(다음 실행에 재갱신)
        return next;
    }

    // ── 🔄 동기화 ──

    private PlutoYouTubeApiSyncResult? Preflight()
    {
        if (KillSwitchOn()) return new PlutoYouTubeApiSyncResult(PlutoYouTubeApiSyncState.Disabled, "⛔ YouTube API 비활성(" + DisableEnvVar + ")");
        var c = LoadCredentials();
        if (c is null || !c.IsConfigured) return new PlutoYouTubeApiSyncResult(PlutoYouTubeApiSyncState.NotConfigured, "⚙ YouTube Data API 미설정 — 관리자 설정에서 API 키/OAuth · 채널 ID를 저장하세요.");
        if (!TransportAllowed) return new PlutoYouTubeApiSyncResult(PlutoYouTubeApiSyncState.Disabled, "⛔ 격리 환경(" + PlutoDriveStorageManager.DriveEnvVar + "=off) — 실 네트워크 호출 생략");
        lock (_sync)
        {
            EnsureStateLoadedLocked();
            if (_state.CooldownUntilUtc is { } cd && cd > UtcNow())
                return new PlutoYouTubeApiSyncResult(PlutoYouTubeApiSyncState.QuotaCooldown, "⏸ YouTube 일일 쿼터 소진 — " + cd.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + " 이후 재시도");
            RollQuotaDayLocked(UtcNow());
            if (_state.QuotaUnitsUsed >= DailyUnitBudget)
                return new PlutoYouTubeApiSyncResult(PlutoYouTubeApiSyncState.BudgetExhausted, "⏸ 자체 일일 예산 " + DailyUnitBudget.ToString(CultureInfo.InvariantCulture) + " unit 소진 — 태평양 자정 이후 재시도");
        }
        return null;
    }

    public async Task<PlutoYouTubeApiSyncResult> TestConnectionAsync(CancellationToken ct = default)
    {
        if (Preflight() is { } blocked) return blocked;
        if (!await _syncGate.WaitAsync(0, ct).ConfigureAwait(false)) return new PlutoYouTubeApiSyncResult(PlutoYouTubeApiSyncState.Busy, "⏳ 이미 동기화 중입니다.");
        var run = new RunCounters();
        try
        {
            var creds = LoadCredentials()!;
            var resolved = await ResolveUploadsAsync(creds, run, forceRefresh: true, ct).ConfigureAwait(false);
            var msg = "✅ 연결 정상 — 채널 " + resolved.Count.ToString(CultureInfo.InvariantCulture) + "/" + creds.Channels.Count.ToString(CultureInfo.InvariantCulture) + "개 확인 · " + run.Units.ToString(CultureInfo.InvariantCulture) + " unit";
            var state = resolved.Count == creds.Channels.Count ? PlutoYouTubeApiSyncState.Synced : PlutoYouTubeApiSyncState.Partial;
            if (resolved.Count < creds.Channels.Count) msg += " · ⚠ 찾지 못한 채널 " + (creds.Channels.Count - resolved.Count).ToString(CultureInfo.InvariantCulture) + "개";
            return Finish(new PlutoYouTubeApiSyncResult(state, msg) { ChannelsSynced = resolved.Count, HttpRequests = run.Requests, QuotaUnitsUsed = run.Units, Retries = run.Retries }, recordSync: false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Finish(Classify(ex, run, null, 0, 0), recordSync: false); }
        finally { _syncGate.Release(); }
    }

    public async Task<PlutoYouTubeApiSyncResult> SyncNowAsync(CancellationToken ct = default)
    {
        if (Preflight() is { } blocked) return blocked;
        if (!await _syncGate.WaitAsync(0, ct).ConfigureAwait(false)) return new PlutoYouTubeApiSyncResult(PlutoYouTubeApiSyncState.Busy, "⏳ 이미 동기화 중입니다.");
        var run = new RunCounters();
        var rows = new List<YouTubeAnalyticsRow>();
        var channelsDone = 0;
        try
        {
            var creds = LoadCredentials()!;
            Exception? stop = null;
            var idLocale = new Dictionary<string, string?>(StringComparer.Ordinal);
            var order = new List<string>();
            var owners = new HashSet<string>(StringComparer.Ordinal);
            foreach (var ch in creds.Channels) owners.Add(ch.ChannelId);
            try
            {
                var uploads = await ResolveUploadsAsync(creds, run, forceRefresh: false, ct).ConfigureAwait(false);
                foreach (var ch in creds.Channels)
                {
                    if (!uploads.TryGetValue(ch.ChannelId, out var playlist)) continue;
                    using var doc = await GetJsonAsync("playlistItems?part=contentDetails&maxResults=" + MaxRecentUploadsPerChannel.ToString(CultureInfo.InvariantCulture)
                        + "&playlistId=" + Uri.EscapeDataString(playlist), run, ct).ConfigureAwait(false);
                    foreach (var vid in ParsePlaylistVideoIds(doc))
                        if (idLocale.TryAdd(vid, ch.Locale)) order.Add(vid);
                    channelsDone++;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { stop = ex; }   // [부분 구제] 이미 모은 ID 는 계속 처리

            // 🧮 채널 경계를 넘는 50개 묶음(채널 4개 × 12개 = 1 요청)
            for (var i = 0; stop is null && i < order.Count; i += MaxIdsPerRequest)
            {
                var n = Math.Min(MaxIdsPerRequest, order.Count - i);
                var ids = string.Join(",", order.GetRange(i, n));
                try
                {
                    using var doc = await GetJsonAsync("videos?part=snippet,statistics,contentDetails&maxResults=" + MaxIdsPerRequest.ToString(CultureInfo.InvariantCulture)
                        + "&id=" + Uri.EscapeDataString(ids), run, ct).ConfigureAwait(false);
                    rows.AddRange(ParseVideoRows(doc, idLocale, owners));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { stop = ex; }
            }

            PlutoTelemetryBatchImportResult? import = null;
            if (rows.Count > 0 && _importer is not null)
                import = await _importer.ImportYouTubeApiRowsAsync(rows, PlatformName, ct).ConfigureAwait(false);

            if (stop is not null)
            {
                var partial = Classify(stop, run, import, channelsDone, rows.Count);
                return Finish(partial, recordSync: rows.Count > 0);
            }
            var summary = "🔄 YouTube API 동기화 — 채널 " + channelsDone.ToString(CultureInfo.InvariantCulture) + "개 · 영상 " + rows.Count.ToString(CultureInfo.InvariantCulture) + "건 · "
                + run.Units.ToString(CultureInfo.InvariantCulture) + " unit" + (import is not null ? " · " + import.SummaryKorean : "");
            return Finish(new PlutoYouTubeApiSyncResult(PlutoYouTubeApiSyncState.Synced, summary)
            {
                ChannelsSynced = channelsDone, VideosFetched = rows.Count, HttpRequests = run.Requests, QuotaUnitsUsed = run.Units, Retries = run.Retries, Import = import,
            }, recordSync: true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Finish(Classify(ex, run, null, channelsDone, rows.Count), recordSync: false); }
        finally { _syncGate.Release(); }
    }

    /// <summary>예외 → 상태(쿼터 소진은 태평양 자정까지 냉각 영속 · 비밀값 가림). 받은 행이 있으면 Partial.</summary>
    private PlutoYouTubeApiSyncResult Classify(Exception ex, RunCounters run, PlutoTelemetryBatchImportResult? import, int channels, int videos)
    {
        var creds = LoadCredentials();
        PlutoYouTubeApiSyncState state;
        string msg;
        switch (ex)
        {
            case QuotaExhaustedException:
                var until = NextQuotaResetUtc(UtcNow());
                lock (_sync) { EnsureStateLoadedLocked(); _state.CooldownUntilUtc = until; PersistStateLocked(); }
                state = PlutoYouTubeApiSyncState.QuotaCooldown;
                msg = "⏸ YouTube 일일 쿼터 소진(quotaExceeded) — " + until.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + " 이후 자동 재개";
                break;
            case BudgetExhaustedException:
                state = PlutoYouTubeApiSyncState.BudgetExhausted;
                msg = "⏸ 자체 일일 예산 " + DailyUnitBudget.ToString(CultureInfo.InvariantCulture) + " unit 도달 — 남은 영상은 태평양 자정 이후";
                break;
            case AuthFailedException a:
                state = PlutoYouTubeApiSyncState.AuthFailed;
                msg = "🔐 인증 실패 — " + Redact(a.Message, creds);
                break;
            case InvalidDataException d:
                state = PlutoYouTubeApiSyncState.Failed;
                msg = "⚠ " + Redact(d.Message, creds);
                break;
            default:
                state = PlutoYouTubeApiSyncState.Failed;
                msg = "⚠ YouTube API 오류 — " + Redact(ex.Message, creds);
                break;
        }
        if (videos > 0 && state is not PlutoYouTubeApiSyncState.AuthFailed)
        {
            msg = "[부분 구제] 영상 " + videos.ToString(CultureInfo.InvariantCulture) + "건 반영 · " + msg + (import is not null ? " · " + import.SummaryKorean : "");
            if (state is PlutoYouTubeApiSyncState.Failed) state = PlutoYouTubeApiSyncState.Partial;
        }
        return new PlutoYouTubeApiSyncResult(state, msg) { ChannelsSynced = channels, VideosFetched = videos, HttpRequests = run.Requests, QuotaUnitsUsed = run.Units, Retries = run.Retries, Import = import };
    }

    private PlutoYouTubeApiSyncResult Finish(PlutoYouTubeApiSyncResult r, bool recordSync)
    {
        lock (_sync)
        {
            EnsureStateLoadedLocked();
            if (recordSync) _state.LastSyncUtc = UtcNow();
            _state.LastSummaryKorean = r.SummaryKorean;
            PersistStateLocked();
        }
        RaiseChanged();
        return r;
    }

    /// <summary><c>channels.list?part=contentDetails&amp;id=…</c>(최대 50개 1요청) → 채널 → 업로드 재생목록. 캐시 적중 채널은 요청 생략.</summary>
    private async Task<Dictionary<string, string>> ResolveUploadsAsync(PlutoYouTubeApiCredentials creds, RunCounters run, bool forceRefresh, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<string>();
        lock (_sync)
        {
            EnsureStateLoadedLocked();
            foreach (var ch in creds.Channels)
                if (!forceRefresh && _state.UploadsPlaylists!.TryGetValue(ch.ChannelId, out var pl) && pl.Length > 2) result[ch.ChannelId] = pl;
                else missing.Add(ch.ChannelId);
        }
        if (missing.Count == 0) return result;
        using var doc = await GetJsonAsync("channels?part=contentDetails&maxResults=" + MaxIdsPerRequest.ToString(CultureInfo.InvariantCulture) + "&id=" + Uri.EscapeDataString(string.Join(",", missing)), run, ct).ConfigureAwait(false);
        var items = RequireItems(doc);
        foreach (var it in items.EnumerateArray())
        {
            if (it.ValueKind != JsonValueKind.Object || !it.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
            var id = idEl.GetString();
            if (!IsChannelId(id) || !missing.Contains(id!)) continue;
            if (it.TryGetProperty("contentDetails", out var cd) && cd.ValueKind == JsonValueKind.Object && cd.TryGetProperty("relatedPlaylists", out var rp) && rp.ValueKind == JsonValueKind.Object
                && rp.TryGetProperty("uploads", out var up) && up.ValueKind == JsonValueKind.String && up.GetString() is { Length: > 2 } uploads)
                result[id!] = uploads;
        }
        lock (_sync)
        {
            foreach (var kv in result) _state.UploadsPlaylists![kv.Key] = kv.Value;
            PersistStateLocked();
        }
        return result;
    }

    /// <summary>목록 응답 스키마 — <c>items</c> 배열 필수(없음 → 즉시 실패).</summary>
    private static JsonElement RequireItems(JsonDocument doc)
    {
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("YouTube API 응답 스키마 위반(items 배열 없음)");
        return items;
    }

    /// <summary><c>playlistItems.list</c> → 영상 ID(형식 검증 · 순서 유지).</summary>
    public static List<string> ParsePlaylistVideoIds(JsonDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var ids = new List<string>();
        foreach (var it in RequireItems(doc).EnumerateArray())
        {
            if (it.ValueKind != JsonValueKind.Object) continue;
            string? vid = null;
            if (it.TryGetProperty("contentDetails", out var cd) && cd.ValueKind == JsonValueKind.Object && cd.TryGetProperty("videoId", out var v) && v.ValueKind == JsonValueKind.String) vid = v.GetString();
            if (YouTubeStudioAnalyticsNormalizer.IsYouTubeVideoId(vid)) ids.Add(vid!);
        }
        return ids;
    }

    private static long CountOrMinus1(JsonElement stats, string name)
    {
        if (stats.ValueKind != JsonValueKind.Object || !stats.TryGetProperty(name, out var v)) return -1;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return n;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var m) && m >= 0) return m;
        return -1;
    }

    /// <summary>
    /// <c>videos.list</c> → 관측 행(<see cref="YouTubeAnalyticsRow.HasPrecisionMetrics"/> = false · 유지율/완주/CTR 0 자리표시).
    /// 스키마 위반 항목(ID · 제목 · 조회수 누락) 제외 · 🛡️ 설정 채널이 아닌 <c>snippet.channelId</c> 영상 제외(재생목록 오설정 · 타 채널 오연결 차단).
    /// 로캘 = 채널 설정 로캘 → <c>defaultAudioLanguage</c> → <c>defaultLanguage</c>(약한 신호).
    /// </summary>
    public static List<YouTubeAnalyticsRow> ParseVideoRows(JsonDocument doc, IReadOnlyDictionary<string, string?>? channelLocaleById = null, ISet<string>? ownerChannels = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var rows = new List<YouTubeAnalyticsRow>();
        foreach (var it in RequireItems(doc).EnumerateArray())
        {
            if (it.ValueKind != JsonValueKind.Object || !it.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
            var id = idEl.GetString();
            if (!YouTubeStudioAnalyticsNormalizer.IsYouTubeVideoId(id)) continue;
            if (!it.TryGetProperty("snippet", out var sn) || sn.ValueKind != JsonValueKind.Object) continue;
            if (!sn.TryGetProperty("title", out var tEl) || tEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(tEl.GetString())) continue;
            if (ownerChannels is { Count: > 0 })
            {
                var owner = sn.TryGetProperty("channelId", out var oc) && oc.ValueKind == JsonValueKind.String ? oc.GetString() : null;
                if (owner is null || !ownerChannels.Contains(owner)) continue;
            }
            if (!it.TryGetProperty("statistics", out var st)) continue;
            var views = CountOrMinus1(st, "viewCount");
            if (views < 0) continue;
            DateTimeOffset? published = null;
            if (sn.TryGetProperty("publishedAt", out var pEl) && pEl.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(pEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var p)) published = p;
            string? lang = null;
            if (channelLocaleById is not null && channelLocaleById.TryGetValue(id!, out var cl)) lang = cl;
            if (lang is null && sn.TryGetProperty("defaultAudioLanguage", out var al) && al.ValueKind == JsonValueKind.String) lang = PlutoMultiLanguageChannelService.NormalizeLocale(al.GetString());
            if (lang is null && sn.TryGetProperty("defaultLanguage", out var dl) && dl.ValueKind == JsonValueKind.String) lang = PlutoMultiLanguageChannelService.NormalizeLocale(dl.GetString());
            var title = tEl.GetString()!.Trim();
            if (title.Length > 300) title = title[..300];
            rows.Add(new YouTubeAnalyticsRow(id, title, published, views, 0, true, 0, 0, 0, true)
            {
                Likes = CountOrMinus1(st, "likeCount"),
                Comments = CountOrMinus1(st, "commentCount"),
                ExportLanguage = lang,
                HasPrecisionMetrics = false,
            });
        }
        return rows;
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.YouTubeApi] Changed handler: " + ex.Message); }   // 구독자 예외 격리
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _http?.Dispose();
            _http = null;
            _creds = null;
        }
        _syncGate.Dispose();
        Changed = null;
    }
}
