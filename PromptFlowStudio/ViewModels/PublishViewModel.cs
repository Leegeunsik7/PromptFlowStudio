using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.AI;
using PromptFlowStudio.Services.Publishing;

namespace PromptFlowStudio.ViewModels;

/// <summary>
/// [PromptFlowStudio] 🚀 SNS 원클릭 발행 모달 뷰모델 — 합본(<c>full.mp4</c>) → YouTube Shorts · Instagram Reels 동시 발행.
///   ▸ <see cref="Open"/>: 훅 문장 → 제목(<c>#Shorts #AI</c>) · 씬 대본 → 타임스탬프 설명 · 태그 · 릴스 캡션 자동 채움(편집 가능).
///   ▸ 공개 범위 기본값 private(권장) · 예약은 YouTube 만(publishAt, private 강제).
///   ▸ <see cref="StartAsync"/>: 선택 채널 병렬 전송 · 채널별 진행률 · 취소(CTS) · 실패는 결과로 격리(한 채널 실패가 다른 채널을 막지 않음).
/// </summary>
public sealed class PublishViewModel : IDisposable
{
    public static readonly (string Key, string Label)[] PrivacyOptions =
    {
        ("private", "🔒 비공개 (권장)"),
        ("unlisted", "🔗 일부공개"),
        ("public", "🌐 즉시공개"),
    };

    private readonly IYouTubeShortsPublisher _yt;
    private readonly IInstagramReelsPublisher _ig;
    private readonly GeminiPromptService? _gemini;
    private readonly PromptFlowStudio.Services.Export.StockSuiteExporter? _stockSuite;
    private readonly PromptFlowStudio.Services.Export.AudioMuxService? _audioMux;
    private string? _publishPath;   // BGM 합성본(없으면 원본)
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _seoCts;
    private int _disposed;

    public PublishViewModel(IYouTubeShortsPublisher yt, IInstagramReelsPublisher ig, GeminiPromptService? gemini = null, PromptFlowStudio.Services.Export.StockSuiteExporter? stockSuite = null,
        PromptFlowStudio.Services.Export.AudioMuxService? audioMux = null)
    {
        _audioMux = audioMux;
        _yt = yt ?? throw new ArgumentNullException(nameof(yt));
        _ig = ig ?? throw new ArgumentNullException(nameof(ig));
        _gemini = gemini;
        _stockSuite = stockSuite;
    }

    /// <summary>상태 변경 통지(백그라운드 스레드 가능 — 구독자는 InvokeAsync(StateHasChanged)).</summary>
    public event Action? OnChange;

    public bool IsOpen { get; private set; }
    public string VideoPath { get; private set; } = "";
    public string? SrtPath { get; private set; }
    /// <summary>발행 컨텍스트 프로젝트 이름(작업실 <c>ws_…</c> · 갤러리 폴더/파일명 · 스토리보드 제목).</summary>
    public string? ProjectName { get; private set; }
    public bool ToYouTube { get; set; } = true;
    public bool ToInstagram { get; set; } = true;
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string TagsText { get; set; } = "";
    public string Caption { get; set; } = "";
    public string Privacy { get; set; } = "private";
    public bool Schedule { get; set; }
    public DateTime ScheduleLocal { get; set; } = DateTime.Now.AddHours(1);
    /// <summary>릴스용 공개 HTTPS URL(있으면 Graph API 직결, 없으면 웹훅/로컬 브릿지).</summary>
    public string PublicVideoUrl { get; set; } = "";
    /// <summary>릴스 공개 게시 동의(로컬 브릿지 경로 필수).</summary>
    public bool InstagramConsent { get; set; }
    /// <summary>SEO 자동 완성 입력 — 작업실 프롬프트(없으면 훅/프로젝트 이름).</summary>
    public string SourcePrompt { get; private set; } = "";
    /// <summary>SEO 자동 완성 듀레이션 힌트(초, 0=미상).</summary>
    public float DurationSec { get; private set; }
    public bool IsSeoBusy { get; private set; }
    public string? SeoMessage { get; private set; }
    /// <summary>💼 StockSuite 내보내기 진행/결과 메시지.</summary>
    public bool IsStockSuiteBusy { get; private set; }
    public string? StockSuiteMessage { get; private set; }
    public bool StockSuiteAvailable => _stockSuite is not null;
    /// <summary>직전 내보내기 성공 여부(🚀 실행 버튼 노출 조건).</summary>
    public bool StockSuiteExported { get; private set; }
    /// <summary>StockSuite 가 이미 실행 중 — 재실행 확인 대기.</summary>
    public bool StockSuiteRestartPending { get; private set; }
    public bool CanLaunchStockSuite => _stockSuite?.CanLaunch == true;

    /// <summary>🎵 SNS 배경음악(무음 기본) · 사용자 MP3 경로 · 합성 상태.</summary>
    public bool BgmAvailable => _audioMux is not null;
    public PromptFlowStudio.Services.Export.SnsBgmPreset Bgm { get; set; } = PromptFlowStudio.Services.Export.SnsBgmPreset.None;
    public string CustomBgmPath { get; set; } = "";
    public string? BgmStatus { get; private set; }
    private PromptFlowStudio.Services.Export.StockSuiteExportFormat? _stockFormat;
    /// <summary>모달 포맷 선택 — 미선택이면 환경설정 기본(MP4 | WebM VP9).</summary>
    public PromptFlowStudio.Services.Export.StockSuiteExportFormat StockSuiteFormat
    {
        get => _stockFormat ?? _stockSuite?.DefaultFormat ?? PromptFlowStudio.Services.Export.StockSuiteExportFormat.Mp4;
        set { _stockFormat = value; Notify(); }
    }

    private bool? _stockSharpen;
    /// <summary>🔎 고선명 샤프닝(1080p Lanczos + Unsharp) — 미선택이면 환경설정 기본(ON).</summary>
    public bool StockSuiteSharpen
    {
        get => _stockSharpen ?? _stockSuite?.DefaultSharpen ?? true;
        set { _stockSharpen = value; Notify(); }
    }

    private bool? _stockSmooth60;
    /// <summary>⚡ 60fps 모션 스무딩(minterpolate) — 미선택이면 환경설정 기본(OFF).</summary>
    public bool StockSuiteSmooth60
    {
        get => _stockSmooth60 ?? _stockSuite?.DefaultSmooth60 ?? false;
        set { _stockSmooth60 = value; Notify(); }
    }

    /// <summary>🖥️ 해상도 프로필 — 1080p(기본) | 4K UHD(3840×2160 Lanczos).</summary>
    public PromptFlowStudio.Services.Export.StockSuiteResolution StockSuiteResolution { get; set; } = PromptFlowStudio.Services.Export.StockSuiteResolution.Fhd1080;
    /// <summary>📺 핀테크 HUD 워터마크(좌상단 브랜드 · 우상단 LIVE KST) 번인.</summary>
    public bool StockSuiteWatermark { get; set; }
    /// <summary>↩️ 이전 배경 백업 존재 — 복원 버튼 노출.</summary>
    public bool CanRollbackStockSuite => _stockSuite?.CanRollback() == true;

    /// <summary>↩️ 이전 배경으로 복원 — 활성 ↔ 백업 맞교환.</summary>
    public bool RollbackStockSuite()
    {
        if (_stockSuite is null || IsStockSuiteBusy) return false;
        try
        {
            var r = _stockSuite.RollbackPrevious();
            StockSuiteMessage = r.Ok ? r.Message : "⚠ " + r.Message;
            return r.Ok;
        }
        catch (Exception ex)
        {
            Program.Log("Publish.StockSuiteRollback", ex);
            StockSuiteMessage = "⚠ 롤백 실패: " + ex.Message;
            return false;
        }
        finally { Notify(); }
    }

    public bool YouTubeEnabled => _yt.Enabled;
    public bool InstagramEnabled => _ig.Enabled;
    public bool IsBusy { get; private set; }
    public double YouTubeProgress { get; private set; }
    public double InstagramProgress { get; private set; }
    public string? YouTubeStatus { get; private set; }
    public string? InstagramStatus { get; private set; }
    public ShortsPublishResult? YouTubeResult { get; private set; }
    public ReelsPublishOutcome? InstagramResult { get; private set; }

    /// <summary>선택 채널 진행률 평균(0~1).</summary>
    public double OverallProgress => (ToYouTube, ToInstagram) switch
    {
        (true, true) => (YouTubeProgress + InstagramProgress) / 2,
        (true, false) => YouTubeProgress,
        (false, true) => InstagramProgress,
        _ => 0,
    };

    public bool CanStart => !IsBusy && (ToYouTube || ToInstagram) && Title.Trim().Length > 0 && File.Exists(VideoPath);

    /// <summary>모달 열기 — 합본 경로 + 훅 + 씬 대본으로 메타데이터 자동 생성(이전 결과 초기화).</summary>
    public void Open(string videoPath, string? hook, IReadOnlyList<ScriptBeat>? beats) => Open(videoPath, hook, beats, null, null);

    /// <summary>
    /// 작업실/갤러리 컨텍스트 열기 — 대본(beats) 없이 미디어 경로 · 프로젝트 이름 · 자막(.srt) 경로로 바인딩.
    /// 훅 = 프롬프트(있으면) → 프로젝트 이름 → 파일명 순. <paramref name="srtPath"/> 미지정/부재 시 동명 .srt 자동 탐색.
    /// </summary>
    public void OpenForMedia(string videoPath, string? projectName, string? srtPath = null, string? prompt = null, float durationSec = 0)
    {
        var name = string.IsNullOrWhiteSpace(projectName) ? Path.GetFileNameWithoutExtension(videoPath ?? "") : projectName.Trim();
        var hook = string.IsNullOrWhiteSpace(prompt) ? name : prompt.Trim();
        Open(videoPath ?? "", hook, null, name, srtPath);
        if (!IsBusy) DurationSec = durationSec > 0 ? durationSec : 0;
    }

    /// <summary>현재 채널 선택 → 플랫폼 라벨(둘 다/YouTube/Instagram).</summary>
    public string SeoPlatform => (ToYouTube, ToInstagram) switch
    {
        (true, true) => "YouTube Shorts + Instagram Reels",
        (false, true) => "Instagram Reels",
        _ => "YouTube Shorts",
    };

    /// <summary>
    /// ✨ AI 제목·해시태그 자동 생성 — 프롬프트 · 듀레이션 · 플랫폼 기반 Gemini 호출 → 제목 · 설명 · 태그 · 릴스 캡션 자동 입력.
    /// 키 없음/실패 시 서비스가 로컬 템플릿 폴백. 서비스 미주입이면 로컬 템플릿 직접 사용. 발행 중이거나 생성 중이면 false.
    /// </summary>
    public async Task<bool> GenerateSeoAsync(CancellationToken ct = default)
    {
        if (IsBusy || IsSeoBusy || !IsOpen) return false;
        IsSeoBusy = true;
        SeoMessage = "✨ 제미나이 SEO 생성 중…";
        _seoCts?.Dispose();
        _seoCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _seoCts.Token;
        Notify();
        try
        {
            var platform = SeoPlatform;
            var meta = _gemini is null
                ? GeminiPromptService.BuildLocalSnsMetadata(SourcePrompt, platform)
                : await _gemini.GenerateSnsMetadataAsync(SourcePrompt, platform, DurationSec, token).ConfigureAwait(false);
            if (IsBusy) return false;   // 생성 중 발행 시작 → 사용자 입력 보존
            ApplySeo(meta);
            SeoMessage = (meta.UsedFallback ? "💡 " : "✨ ") + meta.Message;
            return true;
        }
        catch (OperationCanceledException) { SeoMessage = "SEO 생성 취소됨"; return false; }
        catch (Exception ex)
        {
            Program.Log("PublishViewModel.Seo", ex);
            SeoMessage = "SEO 생성 실패: " + ex.Message;
            return false;
        }
        finally
        {
            IsSeoBusy = false;
            Notify();
        }
    }

    /// <summary>SEO 메타를 입력 필드에 반영(제목 100자 · 태그 쉼표 구분 · 캡션 = 설명 + 해시태그).</summary>
    public void ApplySeo(SnsSeoMetadata meta)
    {
        ArgumentNullException.ThrowIfNull(meta);
        var title = (meta.Title ?? "").Trim();
        if (title.Length > GeminiPromptService.MaxSnsTitle) title = title[..GeminiPromptService.MaxSnsTitle].TrimEnd();
        if (title.Length > 0) Title = title;
        Description = meta.Description ?? "";
        TagsText = string.Join(", ", meta.Tags);
        Caption = Description.Length == 0 ? string.Join(" ", meta.Tags) : Description + Environment.NewLine + Environment.NewLine + string.Join(" ", meta.Tags);
    }

    private void Open(string videoPath, string? hook, IReadOnlyList<ScriptBeat>? beats, string? projectName, string? srtPath)
    {
        if (IsBusy) { IsOpen = true; Notify(); return; }
        VideoPath = videoPath ?? "";
        ProjectName = string.IsNullOrWhiteSpace(projectName) ? hook : projectName;
        SourcePrompt = (hook ?? "").Trim();
        DurationSec = 0;
        SeoMessage = null;
        StockSuiteMessage = null;
        MultiPackageMessage = null;
        StockSuiteExported = StockSuiteRestartPending = false;
        BgmStatus = null;
        SrtPath = srtPath is { Length: > 0 } && File.Exists(srtPath) ? srtPath : YouTubeShortsPublisher.ResolveSrtPath(VideoPath);
        var meta = YouTubeShortsPublisher.BuildMetadata(hook, beats);
        Title = meta.Title;
        Description = meta.Description;
        TagsText = string.Join(", ", meta.Tags);
        Caption = SnsPublishFormatter.BuildReelsCaption(hook, beats, meta.Tags);
        Privacy = "private";
        Schedule = false;
        ScheduleLocal = DateTime.Now.AddHours(1);
        InstagramConsent = false;
        YouTubeProgress = InstagramProgress = 0;
        YouTubeStatus = InstagramStatus = null;
        YouTubeResult = null;
        InstagramResult = null;
        IsOpen = true;
        Notify();
    }

    /// <summary>모달 닫기 — 전송 중이면 백그라운드 계속(다시 열어 진행률 확인).</summary>
    /// <summary>💼 StockSuite로 내보내기 — 현재 영상 → 설정된 에셋 폴더(<c>bg_ambient.mp4</c>) 원자적 덮어쓰기. 미주입/진행 중이면 false.</summary>
    public async Task<bool> ExportToStockSuiteAsync(CancellationToken ct = default)
    {
        if (_stockSuite is null || IsStockSuiteBusy || !IsOpen) return false;
        IsStockSuiteBusy = true;
        StockSuiteMessage = "💼 StockSuite로 복사 중…";
        Notify();
        try
        {
            var r = await _stockSuite.ExportAsync(VideoPath, null, null, ct, StockSuiteFormat, StockSuiteSharpen, StockSuiteSmooth60, StockSuiteResolution, StockSuiteWatermark).ConfigureAwait(false);
            StockSuiteMessage = r.Ok ? r.Message : "⚠ " + r.Message;
            StockSuiteExported = r.Ok;
            StockSuiteRestartPending = false;
            return r.Ok;
        }
        catch (Exception ex)
        {
            Program.Log("Publish.StockSuite", ex);
            StockSuiteMessage = "⚠ StockSuite 내보내기 실패: " + ex.Message;
            return false;
        }
        finally { IsStockSuiteBusy = false; Notify(); }
    }

    // ── 📦 올인원 멀티 타겟 패키지 ──
    public PromptFlowStudio.Services.Export.HtsUltraAspect MultiHtsAspect { get; set; } = PromptFlowStudio.Services.Export.HtsUltraAspect.Ultra21x9;
    public bool IsMultiPackageBusy { get; private set; }
    public string? MultiPackageMessage { get; private set; }

    /// <summary>📦 HTS 울트라와이드 · 모바일 9:16 · 쇼츠 9:16+BGM 동시 생성 — BGM 은 발행 BGM 선택을 따름(무음이면 핀테크 신스웨이브).</summary>
    public async Task<bool> ExportMultiPackageAsync(CancellationToken ct = default)
    {
        if (_stockSuite is null || IsMultiPackageBusy || !IsOpen) return false;
        IsMultiPackageBusy = true;
        MultiPackageMessage = "📦 멀티 패키지 생성 중…";
        Notify();
        try
        {
            var preset = Bgm == PromptFlowStudio.Services.Export.SnsBgmPreset.None ? PromptFlowStudio.Services.Export.SnsBgmPreset.FintechSynthwave : Bgm;
            var opts = new PromptFlowStudio.Services.Export.MultiExportOptions
            {
                HtsAspect = MultiHtsAspect,
                HtsFormat = StockSuiteFormat,
                BgmPreset = preset,
                BgmPath = preset == PromptFlowStudio.Services.Export.SnsBgmPreset.CustomFile ? CustomBgmPath : null,
                DurationSec = DurationSec,
                Smooth60 = StockSuiteSmooth60,
            };
            var r = await _stockSuite.ExportMultiTargetPackageAsync(VideoPath, opts, ct).ConfigureAwait(false);
            MultiPackageMessage = r.Message;
            return r.Ok;
        }
        catch (Exception ex)
        {
            Program.Log("Publish.MultiPkg", ex);
            MultiPackageMessage = "⚠ 멀티 패키지 실패: " + ex.Message;
            return false;
        }
        finally { IsMultiPackageBusy = false; Notify(); }
    }

    /// <summary>🚀 StockSuite 실행 — 이미 실행 중이면 <see cref="StockSuiteRestartPending"/> 로 재실행 확인을 요구.</summary>
    public bool LaunchStockSuite(bool restart = false)
    {
        if (_stockSuite is null) return false;
        var r = _stockSuite.Launch(restart);
        StockSuiteRestartPending = r.AlreadyRunning;
        StockSuiteMessage = r.Message;
        Notify();
        return r.Ok;
    }

    public void Close()
    {
        IsOpen = false;
        Notify();
    }

    /// <summary>업로드 시작 — 선택 채널 병렬 전송. 모든 선택 채널 성공 시 true.</summary>
    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        if (!CanStart) return false;
        IsBusy = true;
        YouTubeProgress = InstagramProgress = 0;
        YouTubeResult = null;
        InstagramResult = null;
        YouTubeStatus = ToYouTube ? "대기" : null;
        InstagramStatus = ToInstagram ? "대기" : null;
        _cts?.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        Notify();

        try
        {
            _publishPath = await PrepareBgmAsync(token).ConfigureAwait(false);
            var ytTask = ToYouTube ? RunYouTubeAsync(token) : Task.FromResult(true);
            var igTask = ToInstagram ? RunInstagramAsync(token) : Task.FromResult(true);
            var results = await Task.WhenAll(ytTask, igTask).ConfigureAwait(false);
            return results[0] && results[1];
        }
        finally
        {
            IsBusy = false;
            Notify();
        }
    }

    /// <summary>🎵 BGM 합성 — 무음/미주입/실패 시 원본 경로([부분 구제] · 발행은 계속).</summary>
    private async Task<string> PrepareBgmAsync(CancellationToken ct)
    {
        if (_audioMux is null || Bgm == PromptFlowStudio.Services.Export.SnsBgmPreset.None) { BgmStatus = null; return VideoPath; }
        BgmStatus = "🎵 배경음악 합성 중…";
        Notify();
        var r = await _audioMux.MuxAsync(VideoPath, Bgm, CustomBgmPath, DurationSec, ct).ConfigureAwait(false);
        BgmStatus = r.Ok ? r.Message : "⚠ " + r.Message;
        Notify();
        return r.Ok ? r.OutputPath : VideoPath;
    }

    public void Cancel()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        try { _seoCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async Task<bool> RunYouTubeAsync(CancellationToken ct)
    {
        YouTubeStatus = "업로드 중…";
        Notify();
        var tags = SnsPublishFormatter.ParseTags(TagsText);
        DateTime? at = Schedule ? ScheduleLocal.ToUniversalTime() : null;
        var meta = new ShortsMetadata(Title.Trim(), Description, tags, at is null ? Privacy : "private", at);
        try
        {
            var r = await _yt.PublishAsync(_publishPath ?? VideoPath, meta, SrtPath, new InlineProgress(p => { YouTubeProgress = Math.Clamp(p, 0, 1); Notify(); }), ct).ConfigureAwait(false);
            YouTubeResult = r;
            YouTubeStatus = r.Message;
            if (r.Ok) YouTubeProgress = 1;
            return r.Ok;
        }
        catch (Exception ex)
        {
            Program.Log("PublishViewModel.YouTube", ex);
            YouTubeStatus = "업로드 실패: " + ex.Message;
            return false;
        }
        finally { Notify(); }
    }

    private async Task<bool> RunInstagramAsync(CancellationToken ct)
    {
        InstagramStatus = "발행 중…";
        Notify();
        try
        {
            var url = string.IsNullOrWhiteSpace(PublicVideoUrl) ? null : PublicVideoUrl.Trim();
            var r = await _ig.PublishAsync(_publishPath ?? VideoPath, url, Caption ?? "", InstagramConsent,
                new InlineProgress(p => { InstagramProgress = Math.Clamp(p, 0, 1); Notify(); }), ct).ConfigureAwait(false);
            InstagramResult = r;
            InstagramStatus = r.Message;
            if (r.Ok) InstagramProgress = 1;
            return r.Ok;
        }
        catch (Exception ex)
        {
            Program.Log("PublishViewModel.Instagram", ex);
            InstagramStatus = "발행 실패: " + ex.Message;
            return false;
        }
        finally { Notify(); }
    }

    private void Notify()
    {
        var h = OnChange;
        if (h is null) return;
        foreach (Action a in h.GetInvocationList())
        {
            try { a(); } catch (Exception ex) { Program.Log("PublishViewModel.OnChange", ex); }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        OnChange = null;
        Cancel();
        _cts?.Dispose();
        _cts = null;
        _seoCts?.Dispose();
        _seoCts = null;
    }

    /// <summary>동기 진행률 중계(Progress&lt;T&gt; 의 SynchronizationContext 포스팅 회피 — 순서 보장).</summary>
    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
