using System;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Configuration;
using PromptFlowStudio.Services.Engines;
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

    public SettingsViewModel(AppSettingsManager settings, IWanConfigService? wan, GeminiConfigService? gemini, EngineConfigService? engines)
        : this(settings, wan, gemini, engines, FfmpegLocator.Locate) { }

    internal SettingsViewModel(AppSettingsManager settings, IWanConfigService? wan, GeminiConfigService? gemini,
        EngineConfigService? engines, Func<FfmpegLocation> locateFfmpeg)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _wan = wan;
        _gemini = gemini;
        _engines = engines;
        _locateFfmpeg = locateFfmpeg;
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
    }

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
