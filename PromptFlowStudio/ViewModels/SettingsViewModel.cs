using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Configuration;
using PromptFlowStudio.Services.Engines;
using PromptFlowStudio.Services.Publishing;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.ViewModels;

/// <summary>
/// [PromptFlowStudio] [⚙️ 환경설정] 뷰모델 — ComfyUI 주소 · 클라우드 API 키 · FFmpeg 툴체인 · 기본 렌더 모드.
///   ▸ 비밀 아닌 값은 <see cref="AppSettingsManager"/>(user_settings.json · 원자적), API 키는 기존 머신바운드 암호화 저장소
///     (<see cref="IWanConfigService"/> · <see cref="GeminiConfigService"/>)에 위임 — 평문 키를 새 파일에 남기지 않는다.
///   ▸ 렌더 모드 ComfyUI/Cloud 는 <see cref="EngineConfigService.SetVideoEngine"/> 로 활성 엔진 전환, Mock 은 큐 시뮬레이션 강제.
/// </summary>
public sealed class SettingsViewModel
{
    private const string CloudEngineKey = "Wan";
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(5);

    private readonly AppSettingsManager _settings;
    private readonly IWanConfigService? _wan;
    private readonly GeminiConfigService? _gemini;
    private readonly EngineConfigService? _engines;
    private readonly Func<FfmpegLocation> _locateFfmpeg;
    private readonly IYouTubeShortsPublisher? _yt;
    private readonly IInstagramReelsPublisher? _ig;
    private readonly YouTubeOAuthFlowService? _oauth;

    public SettingsViewModel(AppSettingsManager settings, IWanConfigService? wan, GeminiConfigService? gemini, EngineConfigService? engines,
        IYouTubeShortsPublisher? yt = null, IInstagramReelsPublisher? ig = null, YouTubeOAuthFlowService? oauth = null)
        : this(settings, wan, gemini, engines, FfmpegLocator.Locate, yt, ig, oauth) { }

    internal SettingsViewModel(AppSettingsManager settings, IWanConfigService? wan, GeminiConfigService? gemini,
        EngineConfigService? engines, Func<FfmpegLocation> locateFfmpeg, IYouTubeShortsPublisher? yt = null, IInstagramReelsPublisher? ig = null,
        YouTubeOAuthFlowService? oauth = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _wan = wan;
        _gemini = gemini;
        _engines = engines;
        _locateFfmpeg = locateFfmpeg;
        _yt = yt;
        _ig = ig;
        _oauth = oauth;
        Load();
    }

    public string ComfyUiUrl { get; set; } = AppSettingsManager.DefaultComfyUiUrl;
    public string RenderMode { get; set; } = RenderEngineKind.ComfyUi;
    /// <summary>DashScope(Wan) 키 — Wan 은 DashScope 엔드포인트에서 동작하므로 단일 키.</summary>
    public string DashScopeKey { get; set; } = "";
    public string GeminiKey { get; set; } = "";
    public bool DashScopeConfigured => _wan?.IsConfigured ?? false;
    public bool GeminiConfigured => _gemini?.IsConfigured ?? false;
    public string SettingsFilePath => _settings.FilePath;
    public bool LoadedFromUser => _settings.LoadedFromUser;

    public bool? ComfyOnline { get; private set; }
    public bool IsTesting { get; private set; }
    public string? StatusMessage { get; private set; }
    public bool StatusOk { get; private set; }

    // ── SNS 채널 연동(관리자만 편집 · AppSettingsManager 암호화 저장소) ──
    public string YouTubeClientId { get; set; } = "";
    public string YouTubeClientSecret { get; set; } = "";
    public string YouTubeRefreshToken { get; set; } = "";
    public string InstagramUserId { get; set; } = "";
    public string InstagramAccessToken { get; set; } = "";
    public string InstagramWebhookUrl { get; set; } = "";
    public bool YouTubeConfigured => _settings.Sns.YouTubeConfigured;
    public bool InstagramConfigured => _settings.Sns.InstagramConfigured || _settings.Sns.InstagramBridgeConfigured;
    /// <summary>마지막 연결 테스트 결과(null = 미실행).</summary>
    public bool? YouTubeOnline { get; private set; }
    public bool? InstagramValid { get; private set; }
    public bool IsTestingSns { get; private set; }

    /// <summary>Instagram 장기 액세스 토큰 수명(일) — Graph 장기 토큰 60일.</summary>
    public const int InstagramTokenLifetimeDays = 60;
    /// <summary>연동된 YouTube 채널 표시명(@handle/제목 · 미조회 시 "").</summary>
    public string YouTubeChannelName => _settings.Sns.YouTubeChannelName;
    /// <summary>원클릭 브라우저 로그인 가능 여부(OAuth 클라이언트 구성됨).</summary>
    public bool CanLinkYouTube => _oauth?.HasClient ?? false;
    public bool IsLinkingYouTube { get; private set; }
    /// <summary>Instagram 토큰 잔여 일수(저장 시각 기준 · 미기록 null).</summary>
    public int? InstagramTokenDaysLeft => ComputeTokenDaysLeft(_settings.Sns.InstagramTokenSavedUtc, DateTime.UtcNow);
    private string _igTokenSavedUtc = "";

    public FfmpegLocation Ffmpeg { get; private set; } = new(null, "미설치");
    public string? FfmpegVersion { get; private set; }

    /// <summary>현재 저장본으로 폼 재적재.</summary>
    public void Load()
    {
        ComfyUiUrl = _settings.ComfyUiUrl;
        RenderMode = _settings.IsMockMode ? RenderEngineKind.Mock
                   : _engines is null ? _settings.RenderMode
                   : string.Equals(_engines.VideoEngineKey, RenderEngineKind.ComfyUi, StringComparison.OrdinalIgnoreCase) ? RenderEngineKind.ComfyUi
                   : RenderEngineKind.Cloud;
        DashScopeKey = _wan?.ApiKey ?? "";
        GeminiKey = _gemini?.ApiKey ?? "";
        var sns = _settings.Sns;
        YouTubeClientId = sns.YouTubeClientId;
        YouTubeClientSecret = sns.YouTubeClientSecret;
        YouTubeRefreshToken = sns.YouTubeRefreshToken;
        InstagramUserId = sns.InstagramUserId;
        InstagramAccessToken = sns.InstagramAccessToken;
        InstagramWebhookUrl = sns.InstagramWebhookUrl;
        _igTokenSavedUtc = sns.InstagramTokenSavedUtc;
    }

    /// <summary>폼의 SNS 자격 스냅샷.</summary>
    public SnsCredentials SnsDraft => new(YouTubeClientId ?? "", YouTubeClientSecret ?? "", YouTubeRefreshToken ?? "",
        InstagramUserId ?? "", InstagramAccessToken ?? "", InstagramWebhookUrl ?? "",
        string.Equals((YouTubeRefreshToken ?? "").Trim(), _settings.Sns.YouTubeRefreshToken, StringComparison.Ordinal) ? _settings.Sns.YouTubeChannelName : "",
        (InstagramAccessToken ?? "").Trim() is { Length: > 0 } tk
            ? (string.Equals(tk, _settings.Sns.InstagramAccessToken, StringComparison.Ordinal) && _igTokenSavedUtc.Length > 0
                ? _igTokenSavedUtc : DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))
            : "");

    /// <summary>
    /// 저장 — user_settings.json → (<paramref name="includeKeys"/> 일 때) 변경된 키만 암호화 저장소 → 활성 엔진 전환. 하나라도 실패하면 false.
    /// </summary>
    /// <param name="includeKeys">API 키 반영 여부 — 관리자만 true(일반 사용자는 키 열람/변경 불가).</param>
    public bool Save(bool includeKeys)
    {
        var ok = _settings.Save(ComfyUiUrl, RenderMode);
        if (includeKeys)
        {
            var key = (DashScopeKey ?? "").Trim();
            if (_wan is not null && !string.Equals(key, _wan.ApiKey, StringComparison.Ordinal)) ok &= _wan.Save(key, _wan.BaseUrl);
            var gk = (GeminiKey ?? "").Trim();
            if (_gemini is not null && !string.Equals(gk, _gemini.ApiKey, StringComparison.Ordinal)) ok &= _gemini.Save(gk);
            var sns = SnsDraft;
            if (sns.InstagramWebhookUrl.Trim().Length > 0 && !InstagramReelsPublisher.IsValidWebhookUrl(sns.InstagramWebhookUrl.Trim())) ok = false;
            else if (!Equals(Trimmed(sns), _settings.Sns)) ok &= _settings.SaveSns(sns);
            _igTokenSavedUtc = _settings.Sns.InstagramTokenSavedUtc;
        }

        var mode = _settings.RenderMode;
        if (_engines is not null && !string.Equals(mode, RenderEngineKind.Mock, StringComparison.Ordinal))
        {
            var target = string.Equals(mode, RenderEngineKind.ComfyUi, StringComparison.Ordinal) ? RenderEngineKind.ComfyUi : CloudEngineKey;
            if (!string.Equals(_engines.VideoEngineKey, target, StringComparison.OrdinalIgnoreCase))
                try { _engines.SetVideoEngine(target); }
                catch (Exception ex) { Program.Log("SettingsViewModel.SetVideoEngine", ex); ok = false; }
        }

        ComfyUiUrl = _settings.ComfyUiUrl;
        RenderMode = mode;
        StatusOk = ok;
        StatusMessage = ok ? "저장했습니다 — 재시작 후에도 유지됩니다." : "일부 항목 저장에 실패했습니다(로그 확인).";
        return ok;
    }

    private static SnsCredentials Trimmed(SnsCredentials c) => new(c.YouTubeClientId.Trim(), c.YouTubeClientSecret.Trim(), c.YouTubeRefreshToken.Trim(),
        c.InstagramUserId.Trim(), c.InstagramAccessToken.Trim(), c.InstagramWebhookUrl.Trim(), c.YouTubeChannelName.Trim(), c.InstagramTokenSavedUtc.Trim());

    /// <summary>토큰 잔여 일수 — 저장 시각 + 60일 − 현재(음수 = 만료). 시각 미기록/파싱 실패 null.</summary>
    public static int? ComputeTokenDaysLeft(string? savedUtc, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(savedUtc)
            || !DateTime.TryParse(savedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var saved))
            return null;
        return (int)Math.Floor((saved.AddDays(InstagramTokenLifetimeDays) - nowUtc).TotalDays);
    }

    /// <summary>🔗 원클릭 YouTube 연동 — 기본 브라우저 Google 로그인 → 루프백 콜백 → RefreshToken 암호화 저장 → 폼 재적재.</summary>
    public async Task<bool> LinkYouTubeAsync(CancellationToken ct = default)
    {
        if (_oauth is null) { StatusOk = false; StatusMessage = "YouTube 로그인 서비스를 사용할 수 없습니다."; return false; }
        IsLinkingYouTube = true;
        bool ok;
        try { ok = await _oauth.StartInteractiveLoginAsync(ct).ConfigureAwait(false); }
        finally { IsLinkingYouTube = false; }
        if (ok) { ReloadYouTube(); YouTubeOnline = true; }
        StatusOk = ok;
        StatusMessage = ok
            ? "YouTube 계정이 연동되었습니다" + (YouTubeChannelName.Length > 0 ? " — " + YouTubeChannelName : "") + "."
            : _oauth.LastError ?? "YouTube 연동에 실패했습니다.";
        return ok;
    }

    /// <summary>YouTube 필드만 저장본으로 재적재(작성 중인 다른 폼 값 보존).</summary>
    private void ReloadYouTube()
    {
        var sns = _settings.Sns;
        YouTubeClientId = sns.YouTubeClientId;
        YouTubeClientSecret = sns.YouTubeClientSecret;
        YouTubeRefreshToken = sns.YouTubeRefreshToken;
    }

    /// <summary>YouTube 연동 해제 — 토큰 폐기(best-effort) · 로컬 RefreshToken 삭제.</summary>
    public async Task<bool> UnlinkYouTubeAsync(CancellationToken ct = default)
    {
        bool ok;
        if (_oauth is not null) ok = await _oauth.DisconnectAsync(ct).ConfigureAwait(false);
        else ok = _settings.SaveSns(_settings.Sns with { YouTubeRefreshToken = "", YouTubeChannelName = "" });
        if (ok) { ReloadYouTube(); YouTubeOnline = null; }
        StatusOk = ok;
        StatusMessage = ok ? "YouTube 연동을 해제했습니다." : _oauth?.LastError ?? "연동 해제에 실패했습니다.";
        return ok;
    }

    /// <summary>YouTube 연결 테스트 — 저장된 자격으로 토큰 리프레시(저장 후 실행).</summary>
    public async Task<bool> TestYouTubeAsync(CancellationToken ct = default)
    {
        if (_yt is null) { YouTubeOnline = false; return false; }
        IsTestingSns = true;
        try { YouTubeOnline = await _yt.TestConnectionAsync(ct).ConfigureAwait(false); }
        finally { IsTestingSns = false; }
        return YouTubeOnline == true;
    }

    /// <summary>Instagram 자격 유효성 검사 — 입력 중인 UserId/Token 기준(저장하지 않음).</summary>
    public async Task<bool> ValidateInstagramAsync(CancellationToken ct = default)
    {
        if (_ig is null) { InstagramValid = false; return false; }
        IsTestingSns = true;
        try { InstagramValid = await _ig.ValidateCredentialsAsync(InstagramUserId, InstagramAccessToken, ct).ConfigureAwait(false); }
        finally { IsTestingSns = false; }
        return InstagramValid == true;
    }

    /// <summary>ComfyUI <c>/system_stats</c> 연결 테스트(입력 중인 주소 기준).</summary>
    public async Task<bool> TestComfyAsync(CancellationToken ct = default)
    {
        IsTesting = true;
        try { ComfyOnline = await AppSettingsManager.PingComfyUiAsync(ComfyUiUrl, ct).ConfigureAwait(false); }
        finally { IsTesting = false; }
        return ComfyOnline == true;
    }

    /// <summary>FFmpeg 재탐색 + <c>-version</c> 첫 줄 조회(5초 타임아웃 · 실패 시 버전 null).</summary>
    public async Task RefreshFfmpegAsync(CancellationToken ct = default)
    {
        try { Ffmpeg = _locateFfmpeg(); }
        catch (Exception ex) { Program.Log("SettingsViewModel.Ffmpeg", ex); Ffmpeg = new(null, "미설치"); }
        FfmpegVersion = null;
        if (!Ffmpeg.Found) return;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(VersionTimeout);
        var r = await ProcessRunner.RunAsync(Ffmpeg.Path!, new[] { "-hide_banner", "-version" }, ct: cts.Token).ConfigureAwait(false);
        if (!r.Ok) return;
        var text = r.StdOut ?? "";
        var nl = text.IndexOf('\n');
        FfmpegVersion = (nl >= 0 ? text[..nl] : text).Trim();
    }
}
