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
    private readonly PromptFlowStudio.Services.Chart.ChartStillGeneratorService? _quotes;
    private readonly PromptFlowStudio.Services.Ui.UserPreferenceService? _prefs;
    private string? _publishPath;   // BGM 합성본(없으면 원본)
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _seoCts;
    private int _disposed;

    public PublishViewModel(IYouTubeShortsPublisher yt, IInstagramReelsPublisher ig, GeminiPromptService? gemini = null, PromptFlowStudio.Services.Export.StockSuiteExporter? stockSuite = null,
        PromptFlowStudio.Services.Export.AudioMuxService? audioMux = null,
        PromptFlowStudio.Services.Chart.ChartStillGeneratorService? quotes = null,
        PromptFlowStudio.Services.Ui.UserPreferenceService? prefs = null)
    {
        _audioMux = audioMux;
        _quotes = quotes;
        _prefs = prefs;
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
    /// <summary>📊 우하단 라이브 티커 HUD 배지 번인(티커 공란 → 생략 폴백).</summary>
    public bool StockSuiteTickerBadge { get; set; }

    private PromptFlowStudio.Services.Export.LutProfile? _stockLut;
    /// <summary>🎨 LUT 번인 — 미선택이면 내보내기 서비스 현재 값(작업실 LUT).</summary>
    public PromptFlowStudio.Services.Export.LutProfile StockSuiteLut
    {
        get => _stockLut ?? _stockSuite?.Lut ?? PromptFlowStudio.Services.Export.LutProfile.None;
        set { _stockLut = value; Notify(); }
    }

    // ── ⚡ 핀테크 퀵 프리셋 ──
    public const string CustomPresetKey = PromptFlowStudio.Services.Ui.UserPreferenceService.Keys.FintechCustomPreset;
    public PromptFlowStudio.Services.Export.FintechPresetKind? ActivePreset { get; private set; }
    public string? PresetMessage { get; private set; }
    public bool HasCustomPreset => _prefs is not null && PromptFlowStudio.Services.Export.FintechExportPreset.TryDeserialize(_prefs.Get(CustomPresetKey)) is not null;

    /// <summary>현재 스위치 11종 스냅샷.</summary>
    public PromptFlowStudio.Services.Export.FintechExportPreset CapturePreset()
        => new(StockSuiteFormat, StockSuiteSharpen, StockSuiteSmooth60, StockSuiteResolution, StockSuiteWatermark, StockSuiteTickerBadge,
            StockSuiteLut, Bgm, MultiHtsAspect, ToYouTube, ToInstagram);

    /// <summary>프리셋 → 모든 스위치 즉시 반영(1회 통지).</summary>
    public void ApplyPreset(PromptFlowStudio.Services.Export.FintechExportPreset p, PromptFlowStudio.Services.Export.FintechPresetKind kind)
    {
        ArgumentNullException.ThrowIfNull(p);
        _stockFormat = p.Format;
        _stockSharpen = p.Sharpen;
        _stockSmooth60 = p.Smooth60;
        StockSuiteResolution = p.Resolution;
        StockSuiteWatermark = p.Watermark;
        StockSuiteTickerBadge = p.TickerBadge;
        _stockLut = p.Lut;
        Bgm = p.Bgm;
        MultiHtsAspect = p.MultiHtsAspect;
        ToYouTube = p.ToYouTube;
        ToInstagram = p.ToInstagram;
        ActivePreset = kind;
        ActiveCustomPresetName = null;
        PresetMessage = "⚡ " + PromptFlowStudio.Services.Export.FintechExportPreset.Label(kind) + " 적용";
        Notify();
    }

    /// <summary>내장/사용자 프리셋 적용 — 사용자 프리셋 부재 시 false.</summary>
    public bool ApplyPreset(PromptFlowStudio.Services.Export.FintechPresetKind kind)
    {
        var p = kind == PromptFlowStudio.Services.Export.FintechPresetKind.Custom
            ? (_prefs is null ? null : PromptFlowStudio.Services.Export.FintechExportPreset.TryDeserialize(_prefs.Get(CustomPresetKey)))
            : PromptFlowStudio.Services.Export.FintechExportPreset.BuiltIn(kind);
        if (p is null) { PresetMessage = "⚠ 저장된 사용자 프리셋이 없습니다."; Notify(); return false; }
        ApplyPreset(p, kind);
        return true;
    }

    /// <summary>💾 현재 스위치를 사용자 프리셋으로 저장(Prefs) — <see cref="CustomPresetName"/> 이 있으면 이름 붙은 목록에도 업서트.</summary>
    public bool SaveCustomPreset() => SaveCustomPreset(CustomPresetName);

    /// <summary>💾 저장 시 사용할 사용자 프리셋 이름(모달 입력 · 공백 = 단일 레거시 슬롯만).</summary>
    public string CustomPresetName { get; set; } = "";

    /// <summary>💾 이름 붙은 사용자 프리셋 목록(재시작 후 Prefs 에서 복원 · 미주입 = 빈 목록).</summary>
    public IReadOnlyList<PromptFlowStudio.Services.Export.FintechExportPreset> CustomPresets
        => _prefs is null ? Array.Empty<PromptFlowStudio.Services.Export.FintechExportPreset>() : _prefs.CustomPresets;

    /// <summary>💾 현재 스위치 → 레거시 슬롯 + (이름 有) 이름 붙은 목록 영속.</summary>
    public bool SaveCustomPreset(string? name)
    {
        if (_prefs is null) { PresetMessage = "⚠ 환경설정 저장소 없음"; Notify(); return false; }
        var snap = CapturePreset();
        _prefs.Set(CustomPresetKey, snap.Serialize());
        var named = _prefs.SaveCustomPreset(name, snap);
        ActivePreset = PromptFlowStudio.Services.Export.FintechPresetKind.Custom;
        ActiveCustomPresetName = named ? name!.Trim() : null;
        PresetMessage = named ? "💾 사용자 프리셋 '" + ActiveCustomPresetName + "' 저장 완료" : "💾 사용자 프리셋 저장 완료";
        Notify();
        return true;
    }

    /// <summary>활성 이름 붙은 사용자 프리셋(null = 내장/레거시).</summary>
    public string? ActiveCustomPresetName { get; private set; }

    /// <summary>💾 이름 붙은 사용자 프리셋 적용(대소문자 무시) — 부재 시 false.</summary>
    public bool ApplyCustomPreset(string? name)
    {
        var n = name?.Trim();
        PromptFlowStudio.Services.Export.FintechExportPreset? hit = null;
        if (!string.IsNullOrEmpty(n))
            foreach (var p in CustomPresets)
                if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)) { hit = p; break; }
        if (hit is null) { PresetMessage = "⚠ 사용자 프리셋을 찾을 수 없습니다: " + n; Notify(); return false; }
        ApplyPreset(hit, PromptFlowStudio.Services.Export.FintechPresetKind.Custom);
        ActiveCustomPresetName = hit.Name;
        PresetMessage = "⚡ 💾 " + hit.Name + " 적용";
        Notify();
        return true;
    }

    // ── 👁️ 1프레임 번인 미리보기 ──
    /// <summary>🎞️ 미리보기 추출 시각(초) — 모달 열 때 작업실 스크러버 현재 재생 위치로 설정(기본 1초 · 무효/음수 = 0).</summary>
    public double PreviewTimestampSec
    {
        get => _previewTs;
        set => _previewTs = double.IsFinite(value) && value > 0 ? value : 0;
    }
    private double _previewTs = 1.0;

    /// <summary>🔇 Silent Ambient Guard 경고 — BGM 선택 상태에서 StockSuite 배경은 무음 강제(null = 경고 없음).</summary>
    public string? StockSuiteSilentWarning => Bgm != PromptFlowStudio.Services.Export.SnsBgmPreset.None
        ? PromptFlowStudio.Services.Export.StockSuiteExporter.SilentAmbientWarning : null;

    /// <summary>직전 미리보기 JPEG 절대 경로(null = 없음/실패).</summary>
    public string? PreviewFramePath { get; private set; }
    public bool IsPreviewBusy { get; private set; }
    public string? PreviewMessage { get; private set; }

    /// <summary>현재 스위치 → 미리보기 옵션(크롭/스케일 · LUT · 워터마크 · 티커 배지).</summary>
    public PromptFlowStudio.Services.Export.ExportOptions CapturePreviewOptions() => new()
    {
        Sharpen = StockSuiteSharpen,
        Resolution = StockSuiteResolution,
        Watermark = StockSuiteWatermark,
        Lut = StockSuiteLut,
        TickerBadge = StockSuiteTickerBadge && !string.IsNullOrWhiteSpace(TickerSymbol)
            ? new PromptFlowStudio.Services.Export.TickerBadgeData(TickerSymbol, TickerCompany, TickerPrice, TickerChangePercent) : null,
        AtSeconds = PreviewTimestampSec,
    };

    /// <summary>👁️ 전체 합성 체인 1프레임 JPEG 생성 — 미주입/진행 중/영상 없음이면 false.</summary>
    public async Task<bool> GeneratePreviewFrameAsync(CancellationToken ct = default)
    {
        if (_stockSuite is null || IsPreviewBusy || !IsOpen || string.IsNullOrWhiteSpace(VideoPath)) return false;
        IsPreviewBusy = true;
        PreviewMessage = "👁️ 미리보기 생성 중…";
        Notify();
        try
        {
            var path = await _stockSuite.GeneratePreviewFrameAsync(VideoPath, CapturePreviewOptions(), ct).ConfigureAwait(false);
            var ok = path.Length > 0;
            PreviewFramePath = ok ? path : null;
            PreviewDataUri = ok ? await LoadPreviewDataUriAsync(path, ct).ConfigureAwait(false) : null;
            PreviewMessage = ok ? "👁️ 1프레임 번인 미리보기 (" + PromptFlowStudio.Services.Export.StockSuiteExporter.ResolutionTag(StockSuiteResolution, StockSuiteSharpen).TrimEnd(' ', '·')
                    + " · 🎞️ " + PromptFlowStudio.Services.Export.StockSuiteExporter.FormatSeekSeconds(PreviewTimestampSec) + "s)"
                : "⚠ " + (_stockSuite.LastPreviewError ?? "미리보기 실패");
            return ok;
        }
        catch (Exception ex)
        {
            Program.Log("Publish.Preview", ex);
            PreviewMessage = "⚠ 미리보기 실패: " + ex.Message;
            return false;
        }
        finally { IsPreviewBusy = false; Notify(); }
    }

    /// <summary>미리보기 JPEG 최대 인라인 크기(초과 시 썸네일 생략 · 경로만 표시).</summary>
    public const int MaxPreviewInlineBytes = 4 * 1024 * 1024;

    /// <summary>👁️ 인라인 썸네일용 data URI(생성 시 1회 계산 · 렌더 경로 재할당 없음).</summary>
    public string? PreviewDataUri { get; private set; }

    private static async Task<string?> LoadPreviewDataUriAsync(string path, CancellationToken ct)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length == 0 || fi.Length > MaxPreviewInlineBytes) return null;
            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            return "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Program.Log("Publish.PreviewLoad", ex); return null; }
    }

    /// <summary>미리보기 패널 닫기.</summary>
    public void ClearPreviewFrame()
    {
        PreviewFramePath = null;
        PreviewDataUri = null;
        PreviewMessage = null;
        Notify();
    }
    public string TickerSymbol { get; set; } = "";
    public string TickerCompany { get; set; } = "";
    public decimal TickerPrice { get; set; }
    public double TickerChangePercent { get; set; }
    /// <summary>📈 마지막 자동 채움 출처 메시지(null = 미조회/실패).</summary>
    public string? TickerQuoteMessage { get; private set; }

    /// <summary>표시용 시세 — 국내(6자리 숫자 코드) <c>74,500원</c> · 그 외 <c>$185.50</c>.</summary>
    public string TickerPriceText => FormatQuotePrice(TickerSymbol, TickerPrice);
    /// <summary>표시용 등락률 — <c>+2.4%</c> · <c>-1.2%</c>.</summary>
    public string TickerChangeText => FormatQuoteChange(TickerChangePercent);

    /// <summary>국내 종목 코드(6자리 숫자 · <c>.KS</c>/<c>.KQ</c> 접미사 허용).</summary>
    public static bool IsKrxTicker(string? ticker)
    {
        var s = (ticker ?? "").Trim();
        var dot = s.IndexOf('.');
        if (dot >= 0) s = s[..dot];
        if (s.Length != 6) return false;
        foreach (var c in s) if (c is < '0' or > '9') return false;
        return true;
    }

    public static string FormatQuotePrice(string? ticker, decimal price)
        => IsKrxTicker(ticker)
            ? decimal.Round(price, 0, MidpointRounding.AwayFromZero).ToString("#,0", System.Globalization.CultureInfo.InvariantCulture) + "원"
            : "$" + price.ToString("#,0.00", System.Globalization.CultureInfo.InvariantCulture);

    public static string FormatQuoteChange(double pct)
        => double.IsFinite(pct) ? (pct >= 0 ? "+" : "") + pct.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%" : "0.0%";

    /// <summary>티커 입력 → 저장 후 캐시 시세 자동 채움(수동 편집은 이후 그대로 유지).</summary>
    public void SetTicker(string? ticker)
    {
        TickerSymbol = (ticker ?? "").Trim();
        TryAutoFillQuote();
        Notify();
    }

    /// <summary>
    /// 📈 IPC <c>QuotePushed</c> 캐시 → <c>quotes.json</c> 순 최신 캔들 조회 → 시세(마지막 종가) · 등락률(직전 종가 대비) 자동 채움.
    /// 미주입 · 티커 공란 · 시세 없음 → false(기존 입력 보존).
    /// </summary>
    public bool TryAutoFillQuote()
    {
        TickerQuoteMessage = null;
        if (_quotes is null || string.IsNullOrWhiteSpace(TickerSymbol)) return false;
        PromptFlowStudio.Services.Chart.OhlcvCandle[]? c;
        try { c = _quotes.ResolveQuotes(TickerSymbol); }
        catch (Exception ex) { Program.Log("Publish.QuoteAutoFill", ex); return false; }
        if (c is not { Length: >= 2 }) return false;
        var last = c[^1].Close;
        var prev = c[^2].Close;
        if (!double.IsFinite(last) || last <= 0 || last >= 1e15) return false;
        TickerPrice = Math.Round((decimal)last, 2, MidpointRounding.AwayFromZero);
        TickerChangePercent = prev > 0 ? Math.Round((last - prev) / prev * 100.0, 2, MidpointRounding.AwayFromZero) : 0;
        TickerQuoteMessage = "📈 캐시 시세 자동 채움 — " + TickerPriceText + " · " + TickerChangeText;
        return true;
    }
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
    /// <summary>🎞️ 갤러리 진입 — 미디어 바인딩 + 갤러리 미리보기 시각(호버/중간점) 전달(무효 = 1초).</summary>
    public void Initialize(string mediaPath, double currentTimestampSec, string? projectName = null, string? srtPath = null, string? prompt = null, float durationSec = 0)
    {
        OpenForMedia(mediaPath, projectName, srtPath, prompt, durationSec);
        PreviewTimestampSec = double.IsFinite(currentTimestampSec) && currentTimestampSec > 0 ? currentTimestampSec : 1.0;
        Notify();
    }

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
        if (_prefs is not null) StockSuiteResolution = PromptFlowStudio.Services.Ui.ExportResolutionInfo.ToStockSuite(_prefs.DefaultResolution);   // 🖥️ 벤치마크 Ultra → 4K 기본
        YouTubeProgress = InstagramProgress = 0;
        YouTubeStatus = InstagramStatus = null;
        YouTubeResult = null;
        InstagramResult = null;
        PreviewFramePath = null;   // 👁️ 이전 영상 미리보기 잔존 차단
        PreviewDataUri = null;
        PreviewMessage = null;
        PreviewTimestampSec = 1.0;   // 🎞️ 작업실이 스크러버 위치로 덮어씀(미지정 = 1초)
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
            _stockSuite.BurnTickerBadge = StockSuiteTickerBadge;
            if (_stockLut is { } lut) _stockSuite.Lut = lut;   // 🎨 모달/프리셋 LUT 명시 선택 시에만 덮어씀(미선택 = 작업실 LUT 유지)
            _stockSuite.TickerBadge = string.IsNullOrWhiteSpace(TickerSymbol) ? null : new PromptFlowStudio.Services.Export.TickerBadgeData(TickerSymbol, TickerCompany, TickerPrice, TickerChangePercent);
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

    // ── 🖥️ 듀얼 모니터 파노라마 분할 ──
    /// <summary>베젤 간격(px) — 우측 화면 크롭 오프셋 보정.</summary>
    public int DualBezelGapPixels { get; set; } = PromptFlowStudio.Services.Export.StockSuiteExporter.DefaultBezelGapPixels;

    /// <summary>🖥️ 울트라와이드 원본 → <c>bg_screen_1/2</c> 좌/우 분할 + 멀티 디스플레이 설정 갱신. 미주입/진행 중이면 false.</summary>
    public async Task<bool> ExportDualMonitorSplitAsync(CancellationToken ct = default)
    {
        if (_stockSuite is null || IsStockSuiteBusy || !IsOpen) return false;
        IsStockSuiteBusy = true;
        StockSuiteMessage = "🖥️ 듀얼 모니터 분할 중…";
        Notify();
        try
        {
            var r = await _stockSuite.ExportDualMonitorSplitAsync(VideoPath, DualBezelGapPixels, null, StockSuiteFormat, ct).ConfigureAwait(false);
            StockSuiteMessage = r.Ok ? r.Message : "⚠ " + r.Message;
            return r.Ok;
        }
        catch (Exception ex)
        {
            Program.Log("Publish.DualSplit", ex);
            StockSuiteMessage = "⚠ 듀얼 모니터 분할 실패: " + ex.Message;
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
        CleanupPreviewTemp();   // 🧹 모달 닫기 → 세션 미리보기 임시 파일 정리
        Notify();
    }

    /// <summary>🧹 미리보기 임시 파일 삭제 + 패널 상태 초기화(예외 격리).</summary>
    private void CleanupPreviewTemp()
    {
        PreviewFramePath = null;
        PreviewDataUri = null;
        PreviewMessage = null;
        try { _stockSuite?.CleanupPreviewFrames(); }
        catch (Exception ex) { Program.Log("Publish.PreviewCleanup", ex); }
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
        CleanupPreviewTemp();
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
