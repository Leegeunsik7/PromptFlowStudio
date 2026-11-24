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

    // 📊 Step 45 — YouTube Analytics API v2 정밀 지표(CTR · 유지율)
    /// <summary>이번 실행의 정밀 지표 경로 상태(호출하지 않았으면 API 키 관측 전용 · 꺼짐 · 미설정).</summary>
    public PlutoYouTubeAnalyticsPrecisionMode AnalyticsMode { get; init; } = PlutoYouTubeAnalyticsPrecisionMode.NotConfigured;
    /// <summary>Analytics v2 정밀 행 반영 결과(학습 · 성숙 · 기반영 · 미연결 · null = 반영 없음).</summary>
    public PlutoTelemetryBatchImportResult? PrecisionImport { get; init; }
    /// <summary>정밀 학습 경로로 승격한 행 수.</summary>
    public int AnalyticsPromotedCount { get; init; }
    /// <summary>🕒 보고 지연 창 · 0 지표 · 저조회로 이번에 승격 보류한 영상 수(관측 전용 유지 · 다음 동기화에 재조회).</summary>
    public int AnalyticsDeferredCount { get; init; }
    /// <summary>🔐 <c>yt-analytics.readonly</c> 범위 부족(403)으로 건너뛴 채널 수.</summary>
    public int AnalyticsScopeMissingChannels { get; init; }
    /// <summary>정밀 학습 직후 기획 채점 · 유기적 복권 스윕 결과(실행 안 함 → null).</summary>
    public PlutoBriefSweepResult? BriefSweep { get; init; }
}

/// <summary>📊 Step 45 — YouTube Analytics API v2 정밀 지표(CTR · 유지율) 동기화 모드.</summary>
public enum PlutoYouTubeAnalyticsPrecisionMode
{
    /// <summary>자격 증명 · 채널 미설정.</summary>
    NotConfigured,
    /// <summary>사용자가 정밀 지표 동기화를 끔(영속) · 킬 스위치 · 격리 환경.</summary>
    Disabled,
    /// <summary>API 키만 — Data API 관측 전용(<see cref="YouTubeAnalyticsRow.HasPrecisionMetrics"/> = false).</summary>
    ApiKeyObserveOnly,
    /// <summary>OAuth 는 있으나 <c>yt-analytics.readonly</c> 범위 없음(403) — 재승인 대기 · 관측 전용으로 강등.</summary>
    AwaitingScope,
    /// <summary>OAuth + 범위 확인 — CTR · 유지율 정밀 학습 활성.</summary>
    Active,
}

/// <summary>📊 Step 45 — 설정 카드 배지용 정밀 지표 상태(비밀값 없음).</summary>
public sealed record PlutoYouTubeAnalyticsPrecisionStatus(PlutoYouTubeAnalyticsPrecisionMode Mode, bool Enabled, int ScopeMissingChannels, int ChannelCount,
    DateTimeOffset? ScopeRetryUtc, DateTimeOffset? LastPrecisionUtc, int LastPromotedCount)
{
    public string BadgeKorean => Mode switch
    {
        PlutoYouTubeAnalyticsPrecisionMode.Active => ScopeMissingChannels > 0
            ? "📊 CTR · 유지율 정밀 동기화 활성(채널 " + (ChannelCount - ScopeMissingChannels).ToString(CultureInfo.InvariantCulture) + "/" + ChannelCount.ToString(CultureInfo.InvariantCulture) + " · 나머지 범위 대기)"
            : "📊 CTR · 유지율 정밀 동기화 활성",
        PlutoYouTubeAnalyticsPrecisionMode.AwaitingScope => "🔐 yt-analytics.readonly 범위 대기 — 관측 전용(재승인 후 저장)",
        PlutoYouTubeAnalyticsPrecisionMode.ApiKeyObserveOnly => "👁 API 키 관측 전용(CTR · 유지율은 OAuth 또는 Studio 내보내기)",
        PlutoYouTubeAnalyticsPrecisionMode.Disabled => "⏸ 정밀 지표 동기화 꺼짐",
        _ => "⚙ 정밀 지표 미설정",
    };

    /// <summary>배지 색 등급(0 정상 · 3 대기/관측 · 2 꺼짐).</summary>
    public int Severity => Mode switch
    {
        PlutoYouTubeAnalyticsPrecisionMode.Active => 0,
        PlutoYouTubeAnalyticsPrecisionMode.Disabled => 2,
        _ => 3,
    };
}

/// <summary>🔄 설정 카드 표시용 상태(비밀값 없음).</summary>
public sealed record PlutoYouTubeApiStatusSnapshot(bool IsConfigured, bool IsNetworkAllowed, string AuthMode, string MaskedCredential, int ChannelCount,
    DateTimeOffset? LastSyncUtc, string? LastSummaryKorean, int QuotaUnitsUsedToday, int DailyUnitBudget, DateTimeOffset? CooldownUntilUtc, string CredentialStorePath);

/// <summary>🔐 자격 증명 저장 결과.</summary>
public sealed record PlutoYouTubeApiSaveResult(bool Ok, string MessageKorean);

/// <summary>⏱️ Step 36 — 주기 자동 동기화 단계.</summary>
public enum PlutoYouTubeApiAutoSyncPhase
{
    /// <summary>사용자가 끔(영속).</summary>
    Off,
    /// <summary>미설정 · 킬 스위치 · 하네스 격리 → 타이머 없음.</summary>
    Unavailable,
    /// <summary>켜져 있으나 아직 기동 요청 없음.</summary>
    NotStarted,
    Scheduled,
    Running,
    /// <summary>일시 오류(429 · 5xx · 시간 초과) 연속 → 지수 백오프.</summary>
    Backoff,
    /// <summary><c>quotaExceeded</c> 또는 자체 예산 소진 → 태평양 자정까지.</summary>
    QuotaPaused,
    /// <summary>남은 예산 &lt; 예비분 → 태평양 자정까지 자동만 정지(수동 가능).</summary>
    ReservePaused,
    /// <summary>인증 실패 → 자격 증명 재저장 전까지 정지(토큰 엔드포인트 반복 타격 차단).</summary>
    AuthPaused,
}

/// <summary>⏱️ Step 36 — 주기 자동 동기화 상태(설정 카드 표시용 · 비밀값 없음).</summary>
public sealed record PlutoYouTubeApiAutoSyncStatus(bool Enabled, bool Armed, PlutoYouTubeApiAutoSyncPhase Phase, DateTimeOffset? NextDueUtc, int ConsecutiveFailures,
    DateTimeOffset? LastAutoRunUtc, string? LastAutoSummaryKorean, long TickCount, int QuotaRemaining, int ReserveUnits, TimeSpan Interval)
{
    public string StatusKorean
    {
        get
        {
            var inv = CultureInfo.InvariantCulture;
            var at = NextDueUtc is { } d ? d.ToLocalTime().ToString("MM-dd HH:mm", inv) : "—";
            return Phase switch
            {
                PlutoYouTubeApiAutoSyncPhase.Off => "⏸ 주기 자동 동기화 꺼짐(수동 동기화만)",
                PlutoYouTubeApiAutoSyncPhase.Unavailable => "🔒 미설정 · 격리 환경 — 자동 동기화 대기 안 함(네트워크 0)",
                PlutoYouTubeApiAutoSyncPhase.NotStarted => "⏱️ 자동 동기화 대기(작업 공간 진입 시 기동)",
                PlutoYouTubeApiAutoSyncPhase.Running => "🔄 백그라운드 자동 동기화 실행 중…",
                PlutoYouTubeApiAutoSyncPhase.Backoff => "⏳ 일시 오류 " + ConsecutiveFailures.ToString(inv) + "회 연속 — " + at + " 재시도(지수 백오프)",
                PlutoYouTubeApiAutoSyncPhase.QuotaPaused => "⏸ 쿼터 소진 — " + at + "(태평양 자정 이후) 자동 재개",
                PlutoYouTubeApiAutoSyncPhase.ReservePaused => "⏸ 남은 쿼터 " + QuotaRemaining.ToString(inv) + " unit < 예비 " + ReserveUnits.ToString(inv) + " — " + at + " 자동 재개(수동은 가능)",
                PlutoYouTubeApiAutoSyncPhase.AuthPaused => "🔐 인증 실패로 자동 동기화 정지 — 자격 증명을 다시 저장하세요",
                _ => "⏱️ 다음 자동 동기화 " + at + " (" + ((int)Interval.TotalMinutes).ToString(inv) + "분 주기)",
            };
        }
    }
}

/// <summary>⏱️ Step 36 — 백그라운드 자동 동기화 1회 결과(작업 공간 토스트 · 테스트 신호).</summary>
public sealed record PlutoYouTubeApiAutoSyncEvent(PlutoYouTubeApiSyncResult Result, DateTimeOffset AtUtc)
{
    public int NewlyLinkedCount => Result.Import?.NewlyLinkedCount ?? 0;
    public int QueueRecoveredCount => Result.Import?.QueueRecoveredCount ?? 0;
    public int ObservedCount => Result.Import?.ApiObservedCount ?? 0;
    /// <summary>새 연결 · 대기열 회수가 있을 때만 토스트(조회수 갱신만으로는 알리지 않음).</summary>
    public bool HasImpact => NewlyLinkedCount > 0 || QueueRecoveredCount > 0;

    public string ToastKorean
    {
        get
        {
            var inv = CultureInfo.InvariantCulture;
            var s = "⏱️ API 자동 동기화";
            if (NewlyLinkedCount > 0) s += " · 📺 새 연결 " + NewlyLinkedCount.ToString(inv) + "건";
            if (QueueRecoveredCount > 0) s += " · 📌→✅ 대기열 정밀 지표 회수 " + QueueRecoveredCount.ToString(inv) + "건";
            return s + " · 👁 관측 " + ObservedCount.ToString(inv) + "건";
        }
    }
}

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

    // ⏱️ Step 36 — 쿼터 인지형 주기 자동 동기화
    /// <summary>주기 자동 동기화 사용(영속 · 미설정 시 기본 켬 — 자격 증명 · 채널이 저장되어야 실제 기동).</summary>
    bool AutoSyncEnabled { get; }
    TimeSpan AutoSyncInterval { get; }
    /// <summary>백그라운드 1회 완료(타이머 스레드에서 발생 · 구독자별 예외 격리).</summary>
    event Action<PlutoYouTubeApiAutoSyncEvent>? AutoSyncCompleted;
    /// <summary>멱등 기동 — 미설정 · 격리 · 꺼짐 · 인증 정지면 타이머를 만들지 않고 false. 이후 자격 증명 저장 시 자동 무장.</summary>
    bool StartAutoSync();
    /// <summary>타이머 해제 · 진행 중 자동 실행 취소.</summary>
    void StopAutoSync();
    Task SetAutoSyncEnabledAsync(bool enabled, CancellationToken ct = default);
    PlutoYouTubeApiAutoSyncStatus GetAutoSyncStatus();

    // 📊 Step 45 — YouTube Analytics API v2 정밀 지표(CTR · 유지율)
    /// <summary>정밀 지표 동기화 상태(설정 카드 배지). 기본 구현 = 미설정.</summary>
    PlutoYouTubeAnalyticsPrecisionStatus GetAnalyticsPrecisionStatus()
        => new(PlutoYouTubeAnalyticsPrecisionMode.NotConfigured, false, 0, 0, null, null, 0);
    /// <summary>정밀 지표 동기화 사용(영속 · 기본 켬 — OAuth 일 때만 실제 호출). 끄면 Data API 관측만.</summary>
    Task SetAnalyticsPrecisionEnabledAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
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
    // ⏱️ Step 36 — 주기 자동 동기화
    public static readonly TimeSpan DefaultAutoSyncInterval = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan MinAutoSyncInterval = TimeSpan.FromMinutes(5);
    /// <summary>🛡️ 기동 직후 유예 — 앱을 자주 재시작해도 매번 즉시 동기화하지 않음(재시작 폭주 쿼터 소모 차단).</summary>
    public static readonly TimeSpan AutoSyncStartupGrace = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan AutoBackoffBase = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaxAutoBackoff = TimeSpan.FromHours(6);
    /// <summary>냉각 · 예비 정지 해제 시각 뒤 여유(경계 시각 시계 오차로 즉시 재소진 방지).</summary>
    public static readonly TimeSpan AutoResumeSlack = TimeSpan.FromMinutes(1);
    /// <summary>타이머가 예정보다 이만큼 이상 일찍 발화하면(시계 변경 · 재예약 경합) 실행하지 않고 남은 시간만큼 재무장.</summary>
    public static readonly TimeSpan EarlyFireTolerance = TimeSpan.FromSeconds(1);
    public const int DefaultAutoSyncReserveUnits = 100;
    // 📊 Step 45 — YouTube Analytics API v2
    public const string AnalyticsReportsEndpoint = "https://youtubeanalytics.googleapis.com/v2/reports";
    /// <summary>요청 지표 — CTR 은 공식 v2 지표명 <c>videoThumbnailImpressionsClickRate</c> 로 요청(응답 파서는 레거시 <c>impressionsClickThroughRate</c> 열도 계속 수용).</summary>
    public const string AnalyticsMetrics = "views,estimatedMinutesWatched,averageViewPercentage,videoThumbnailImpressionsClickRate";
    /// <summary>🕒 Analytics 보고 지연(일) — 종료일 = 태평양 오늘 − 이 값. 아직 집계 중인 날을 넣으면 CTR · 유지율이 0/부분값으로 와서 최초 기준선을 영구히 오염시킨다.</summary>
    public const int AnalyticsReportingLagDays = 2;
    /// <summary>조회 시작일 하한(게시일이 더 오래되면 이 기간으로 자름).</summary>
    public const int AnalyticsMaxLookbackDays = 730;
    /// <summary>🔐 범위 부족(403) 채널 재확인 간격 — 매 30분 자동 동기화가 같은 403 으로 쿼터를 태우지 않게. 자격 증명 재저장 시 즉시 해제.</summary>
    public static readonly TimeSpan AnalyticsScopeRetryInterval = TimeSpan.FromHours(24);

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
    // ⏱️ Step 36 — 주기 자동 동기화
    private readonly CancellationTokenSource _life = new();
    private ITimer? _autoTimer;
    private DateTimeOffset? _autoArmedFor;
    private CancellationTokenSource? _autoRunCts;
    private bool _autoRunning;
    private bool _autoStartRequested;
    private int _tickInFlight;
    private long _autoTicks;

    public event Action? Changed;
    public event Action<PlutoYouTubeApiAutoSyncEvent>? AutoSyncCompleted;

    private Func<DateTimeOffset>? _utcNow;

    /// <summary>시계(테스트 주입) — 미지정 시 <see cref="TimeProvider"/>.</summary>
    public Func<DateTimeOffset> UtcNow { get => _utcNow ?? (() => TimeProvider.GetUtcNow()); init => _utcNow = value; }

    private DateTimeOffset Now() => _utcNow is { } f ? f() : TimeProvider.GetUtcNow();

    /// <summary>⏱️ Step 36 — 주기 자동 동기화 타이머 · 시계 원천(테스트는 수동 시계 주입으로 결정적 발화).</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    private TimeSpan _autoInterval = DefaultAutoSyncInterval;

    /// <summary>⏱️ 주기 자동 동기화 간격(기본 30분 · [<see cref="MinAutoSyncInterval"/>, 24시간] 로 고정 — 실수로 1초 주기를 넣어도 쿼터를 태우지 않게).</summary>
    public TimeSpan AutoSyncInterval
    {
        get => _autoInterval;
        init => _autoInterval = value < MinAutoSyncInterval ? MinAutoSyncInterval : value > TimeSpan.FromHours(24) ? TimeSpan.FromHours(24) : value;
    }

    /// <summary>💸 자동 동기화 예비 쿼터 — 남은 일일 예산이 이 값 미만이면 태평양 자정까지 자동 동기화만 멈춤(수동은 가능).</summary>
    public int AutoSyncReserveUnits { get; init; } = DefaultAutoSyncReserveUnits;

    /// <summary>백오프 대기(테스트는 기록만 하고 즉시 반환).</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = static (d, ct) => Task.Delay(d, ct);

    /// <summary>자체 일일 쿼터 예산(Google 기본 10,000 unit 중 다른 도구 몫을 남김).</summary>
    public int DailyUnitBudget { get; init; } = DefaultDailyUnitBudget;

    public string PlatformName { get; init; } = "YouTube Shorts";

    /// <summary>📊 Step 45 — 정밀 학습 직후 기획 채점 · 유기적 복권 즉시 실행(null → 카탈로그 변경 구독에 맡김).</summary>
    public IPlutoBriefOutcomeAttributionService? BriefOutcomes { get; init; }

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
            // 🛡️ Step 36 — 구성 세대 교체: 옛 채널 구성으로 진행 중인 자동 실행 취소 · 인증 정지 해제 · 유예 뒤 재무장
            CancelAutoRunLocked();
            _state.AutoAuthLatched = false;
            _state.AutoConsecutiveFailures = 0;
            _state.AnalyticsScopeMissing = null;   // 📊 Step 45 — 재승인(yt-analytics.readonly) 후 저장 → 범위 대기 즉시 해제
            _state.AnalyticsScopeConfirmed = false;
            _state.AutoNextDueUtc = Now() + AutoSyncStartupGrace;
            DisarmLocked();
            PersistStateLocked();
        }
        if (Volatile.Read(ref _autoStartRequested)) StartAutoSync();
        RaiseChanged();
        return Task.FromResult(new PlutoYouTubeApiSaveResult(true, "🔐 암호화 저장 완료(" + (clean.HasOAuth ? "OAuth" : "API 키") + " · 채널 " + channels.Count.ToString(CultureInfo.InvariantCulture) + "개 · 이 PC 전용 · Drive 미동기화)"));
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        PlutoYouTubeApiCredentials? old;
        old = LoadCredentials();
        lock (_sync)
        {
            _creds = null;
            _credsLoaded = true;
            CancelAutoRunLocked();   // 🛡️ Step 36 — 진행 중 자동 실행이 지워진 자격 증명 이후에 결과를 쓰지 않게
            DisarmLocked();
        }
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
            _state.AutoNextDueUtc = null;
            _state.AutoConsecutiveFailures = 0;
            _state.AutoAuthLatched = false;
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
        // ⏱️ Step 36 — 재시작 후에도 예정 · 백오프 · 인증 정지 유지
        public bool? AutoSyncEnabled { get; set; }
        public DateTimeOffset? AutoNextDueUtc { get; set; }
        public int AutoConsecutiveFailures { get; set; }
        public DateTimeOffset? AutoLastRunUtc { get; set; }
        public string? AutoLastSummaryKorean { get; set; }
        public bool AutoAuthLatched { get; set; }
        // 📊 Step 45 — Analytics v2 정밀 지표
        public bool? AnalyticsEnabled { get; set; }
        /// <summary>채널 → 범위 부족(403) 재확인 시각.</summary>
        public Dictionary<string, DateTimeOffset>? AnalyticsScopeMissing { get; set; }
        public bool AnalyticsScopeConfirmed { get; set; }
        public DateTimeOffset? AnalyticsLastPrecisionUtc { get; set; }
        public int AnalyticsLastPromoted { get; set; }
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
            RollQuotaDayLocked(Now());
            var mode = c is null ? "미설정" : c.HasOAuth ? "OAuth" : c.HasApiKey ? "API 키" : "미설정";
            var masked = c is null ? "—" : c.HasOAuth ? Mask(c.ClientId) : Mask(c.ApiKey);
            var cooldown = _state.CooldownUntilUtc is { } cd && cd > Now() ? cd : (DateTimeOffset?)null;
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
    /// <summary>📊 Step 45 — Analytics v2 범위(<c>yt-analytics.readonly</c>) · 채널 권한 부족.</summary>
    private sealed class AnalyticsScopeException : Exception { public AnalyticsScopeException(string m) : base(m) { } }

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

    /// <param name="analyticsUrl">📊 Step 45 — true: <paramref name="relative"/> 는 Analytics v2 절대 URL · 쿼터 외 403 은 <see cref="AnalyticsScopeException"/>(인증 정지로 번지지 않음).</param>
    private async Task<JsonDocument> GetJsonAsync(string relative, RunCounters run, CancellationToken ct, bool analyticsUrl = false)
    {
        var refreshedFor401 = false;
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            ChargeUnit(run);
            var creds = await EnsureAccessAsync(forceRefresh: false, ct).ConfigureAwait(false);
            if (analyticsUrl && !creds.HasOAuth) throw new AnalyticsScopeException("OAuth 없음");   // 🛡️ API 키는 Analytics v2 에 절대 싣지 않음
            using var req = new HttpRequestMessage(HttpMethod.Get, analyticsUrl ? relative : ApiBase + relative);
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
                await Delay(BackoffDelay(null, attempt, Now()), ct).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException) when (attempt < MaxAttempts)
            {
                run.Retries++;
                await Delay(BackoffDelay(null, attempt, Now()), ct).ConfigureAwait(false);
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
                // 📊 Step 45 — Analytics 403(insufficientPermissions · forbidden · 채널 비소유) = 범위/권한 부족 → 채널 단위 강등(Data API 동기화 · 자동 동기화 인증 정지에 번지지 않음)
                if (analyticsUrl && code == 403 && reason is not ("rateLimitExceeded" or "userRateLimitExceeded"))
                    throw new AnalyticsScopeException("HTTP 403" + (reason.Length > 0 ? " " + reason : ""));
                if (code is 401 || (code == 400 && reason == "keyInvalid") || (code == 403 && reason is "forbidden" or "accessNotConfigured" or "keyInvalid" or "ipRefererBlocked"))
                    throw new AuthFailedException("HTTP " + code.ToString(CultureInfo.InvariantCulture) + (reason.Length > 0 ? " " + reason : ""));
                var retryable = code == 429 || code >= 500 || (code == 403 && reason is "rateLimitExceeded" or "userRateLimitExceeded");
                if (!retryable || attempt >= MaxAttempts)
                    throw new HttpRequestException("YouTube API HTTP " + code.ToString(CultureInfo.InvariantCulture) + (reason.Length > 0 ? " " + reason : ""));
                run.Retries++;
                await Delay(BackoffDelay(resp, attempt, Now()), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>요청 전 1 unit 선차감(실패 요청도 Google 이 집계할 수 있어 보수적) · 자체 예산 초과 → 중단.</summary>
    private void ChargeUnit(RunCounters run)
    {
        lock (_sync)
        {
            EnsureStateLoadedLocked();
            RollQuotaDayLocked(Now());
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
        var now = Now();
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
            if (_state.CooldownUntilUtc is { } cd && cd > Now())
                return new PlutoYouTubeApiSyncResult(PlutoYouTubeApiSyncState.QuotaCooldown, "⏸ YouTube 일일 쿼터 소진 — " + cd.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + " 이후 재시도");
            RollQuotaDayLocked(Now());
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
        finally { ReleaseGate(); }
    }

    public async Task<PlutoYouTubeApiSyncResult> SyncNowAsync(CancellationToken ct = default)
    {
        var r = await SyncInternalAsync(ct).ConfigureAwait(false);
        if (r.Succeeded) OnManualSyncSucceeded();   // ⏱️ Step 36 — 수동 성공 → 다음 자동 예정을 한 주기 뒤로(연속 이중 소모 차단)
        return r;
    }

    /// <summary>수동 · 자동 공용 본체 — 같은 단일 실행 게이트(<c>_syncGate</c>)를 공유해 절대 겹치지 않음.</summary>
    private async Task<PlutoYouTubeApiSyncResult> SyncInternalAsync(CancellationToken ct)
    {
        if (Preflight() is { } blocked) return blocked;
        bool entered;
        try { entered = await _syncGate.WaitAsync(0, ct).ConfigureAwait(false); }
        catch (ObjectDisposedException) { return new PlutoYouTubeApiSyncResult(PlutoYouTubeApiSyncState.Disabled, "⛔ 서비스 종료됨"); }
        if (!entered)
            return new PlutoYouTubeApiSyncResult(PlutoYouTubeApiSyncState.Busy, Volatile.Read(ref _autoRunning) ? "⏳ 백그라운드 자동 동기화 진행 중 — 끝나면 결과가 반영됩니다." : "⏳ 이미 동기화 중입니다.");
        var run = new RunCounters();
        var rows = new List<YouTubeAnalyticsRow>();
        var channelsDone = 0;
        try
        {
            var creds = LoadCredentials()!;
            Exception? stop = null;
            var idLocale = new Dictionary<string, string?>(StringComparer.Ordinal);
            var order = new List<string>();
            var idChannel = new Dictionary<string, string>(StringComparer.Ordinal);   // 📊 Step 45 — 영상 → 소유 채널(Analytics 채널별 조회)
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
                        if (idLocale.TryAdd(vid, ch.Locale)) { order.Add(vid); idChannel[vid] = ch.ChannelId; }
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

            // 📊 Step 45 — Analytics v2 정밀 지표: 관측 · 연결 · 대기열(Studio 실측) 회수가 끝난 '뒤'에만 실행 → 같은 영상의 Studio 행이 항상 먼저 기준선을 잡는다
            var precision = new AnalyticsOutcome(AnalyticsModeFor(creds));
            if (stop is null && precision.Mode == PlutoYouTubeAnalyticsPrecisionMode.Active && rows.Count > 0)
            {
                try { precision = await RunAnalyticsPrecisionAsync(creds, rows, idChannel, run, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is QuotaExhaustedException or BudgetExhaustedException or AuthFailedException) { stop = ex; }   // 쿼터 · 예산 · 토큰 거부는 실행 전체 신호
                catch (Exception ex) { precision = precision with { Error = Redact(ex.Message, LoadCredentials()) }; }   // [부분 구제] 스키마 위반 · 일시 오류 → 관측 결과 유지
            }

            if (stop is not null)
            {
                var partial = Classify(stop, run, import, channelsDone, rows.Count);
                return Finish(WithPrecision(partial, precision), recordSync: rows.Count > 0);
            }
            var summary = "🔄 YouTube API 동기화 — 채널 " + channelsDone.ToString(CultureInfo.InvariantCulture) + "개 · 영상 " + rows.Count.ToString(CultureInfo.InvariantCulture) + "건 · "
                + run.Units.ToString(CultureInfo.InvariantCulture) + " unit" + (import is not null ? " · " + import.SummaryKorean : "") + precision.SummarySuffix();
            return Finish(WithPrecision(new PlutoYouTubeApiSyncResult(PlutoYouTubeApiSyncState.Synced, summary)
            {
                ChannelsSynced = channelsDone, VideosFetched = rows.Count, HttpRequests = run.Requests, QuotaUnitsUsed = run.Units, Retries = run.Retries, Import = import,
            }, precision), recordSync: true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Finish(Classify(ex, run, null, channelsDone, rows.Count), recordSync: false); }
        finally { ReleaseGate(); }
    }

    // ── 📊 Step 45 — YouTube Analytics API v2 정밀 지표(CTR · 유지율) ──

    /// <summary>Analytics v2 영상 1개 지표(<c>dimensions=video</c> 행).</summary>
    public readonly record struct AnalyticsVideoMetrics(long Views, double EstimatedMinutesWatched, double AverageViewPercentage, double CtrPct);

    /// <summary>한 번의 정밀 지표 실행 결과(내부).</summary>
    private sealed record AnalyticsOutcome(PlutoYouTubeAnalyticsPrecisionMode Mode)
    {
        public PlutoTelemetryBatchImportResult? Import { get; init; }
        public int Promoted { get; init; }
        public int Deferred { get; init; }
        public int ScopeMissing { get; init; }
        public PlutoBriefSweepResult? Sweep { get; init; }
        public string? Error { get; init; }

        public string SummarySuffix()
        {
            var inv = CultureInfo.InvariantCulture;
            if (Mode == PlutoYouTubeAnalyticsPrecisionMode.AwaitingScope) return " · 🔐 yt-analytics.readonly 범위 대기 — 관측 전용";
            if (Mode != PlutoYouTubeAnalyticsPrecisionMode.Active) return "";
            var s = " · 📊 정밀 " + Promoted.ToString(inv) + "건";
            if (Import is { } pi) s += "(" + pi.SummaryKorean + ")";
            if (Deferred > 0) s += " · 🕒 보고 지연 보류 " + Deferred.ToString(inv) + "건";
            if (ScopeMissing > 0) s += " · 🔐 범위 대기 채널 " + ScopeMissing.ToString(inv) + "개";
            if (Error is { Length: > 0 } e) s += " · ⚠ 정밀 지표 조회 실패(관측 유지): " + e;
            return s;
        }
    }

    private static PlutoYouTubeApiSyncResult WithPrecision(PlutoYouTubeApiSyncResult r, AnalyticsOutcome o) => r with
    {
        AnalyticsMode = o.Mode, PrecisionImport = o.Import, AnalyticsPromotedCount = o.Promoted, AnalyticsDeferredCount = o.Deferred,
        AnalyticsScopeMissingChannels = o.ScopeMissing, BriefSweep = o.Sweep,
    };

    /// <summary>자격 증명 · 토글 · 범위 래치로 정한 정밀 지표 모드(호출 전 판정).</summary>
    private PlutoYouTubeAnalyticsPrecisionMode AnalyticsModeFor(PlutoYouTubeApiCredentials? c)
    {
        if (c is null || !c.IsConfigured) return PlutoYouTubeAnalyticsPrecisionMode.NotConfigured;
        if (KillSwitchOn() || !TransportAllowed) return PlutoYouTubeAnalyticsPrecisionMode.Disabled;
        if (!c.HasOAuth) return PlutoYouTubeAnalyticsPrecisionMode.ApiKeyObserveOnly;
        lock (_sync)
        {
            EnsureStateLoadedLocked();
            if (_state.AnalyticsEnabled == false) return PlutoYouTubeAnalyticsPrecisionMode.Disabled;
            return ScopeMissingCountLocked(c, Now()) >= c.Channels.Count ? PlutoYouTubeAnalyticsPrecisionMode.AwaitingScope : PlutoYouTubeAnalyticsPrecisionMode.Active;
        }
    }

    private int ScopeMissingCountLocked(PlutoYouTubeApiCredentials c, DateTimeOffset now)
    {
        var n = 0;
        if (_state.AnalyticsScopeMissing is { Count: > 0 } m)
            foreach (var ch in c.Channels)
                if (m.TryGetValue(ch.ChannelId, out var until) && until > now) n++;
        return n;
    }

    /// <summary>🕒 조회 종료일(태평양 오늘 − <see cref="AnalyticsReportingLagDays"/>).</summary>
    public static DateOnly AnalyticsEndDate(DateTimeOffset nowUtc)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, Pacific()).Date).AddDays(-AnalyticsReportingLagDays);

    private static DateOnly PacificDate(DateTimeOffset utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, Pacific()).Date);

    /// <summary>
    /// 🕒 보고 지연 가드 — 게시일(태평양)이 종료일보다 앞서야(완전 집계된 날 ≥ 1) 정밀 승격 후보. 게시 시각 미상 · 너무 최근 → 관측 전용 유지.
    /// 최초 반영은 클립당 1회(alreadyRegistered)라 집계 중 0/부분 CTR 로 잡힌 기준선은 영구 오답이 된다.
    /// </summary>
    public static bool IsAnalyticsReportMature(DateTimeOffset? publishedAtUtc, DateTimeOffset nowUtc)
        => publishedAtUtc is { } p && PacificDate(p) < AnalyticsEndDate(nowUtc);

    /// <summary>
    /// Analytics v2 <c>reports.query</c> 응답 → 영상별 지표. 열 위치가 아닌 <c>columnHeaders</c> 이름으로 매핑(필수: video · views · averageViewPercentage · CTR).
    /// <c>rows</c> 없음 = 데이터 없음(정상). 형식 위반 행 · 잘못된 영상 ID 는 제외.
    /// </summary>
    public static Dictionary<string, AnalyticsVideoMetrics> ParseAnalyticsReport(JsonDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("columnHeaders", out var headers) || headers.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("YouTube Analytics 응답 스키마 위반(columnHeaders 없음)");
        int iVideo = -1, iViews = -1, iMinutes = -1, iAvg = -1, iCtr = -1, idx = 0;
        foreach (var h in headers.EnumerateArray())
        {
            var name = h.ValueKind == JsonValueKind.Object && h.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            switch (name)
            {
                case "video": iVideo = idx; break;
                case "views": iViews = idx; break;
                case "estimatedMinutesWatched": iMinutes = idx; break;
                case "averageViewPercentage": iAvg = idx; break;
                case "impressionsClickThroughRate" or "videoThumbnailImpressionsClickRate": iCtr = idx; break;
            }
            idx++;
        }
        if (iVideo < 0 || iViews < 0 || iAvg < 0 || iCtr < 0)
            throw new InvalidDataException("YouTube Analytics 응답 스키마 위반(video · views · averageViewPercentage · CTR 열 필요)");
        var result = new Dictionary<string, AnalyticsVideoMetrics>(StringComparer.Ordinal);
        if (!root.TryGetProperty("rows", out var rows) || rows.ValueKind == JsonValueKind.Null) return result;
        if (rows.ValueKind != JsonValueKind.Array) throw new InvalidDataException("YouTube Analytics 응답 스키마 위반(rows 배열 아님)");
        foreach (var r in rows.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Array || r.GetArrayLength() < idx) continue;
            var id = r[iVideo].ValueKind == JsonValueKind.String ? r[iVideo].GetString() : null;
            if (!YouTubeStudioAnalyticsNormalizer.IsYouTubeVideoId(id)) continue;
            if (!Num(r[iViews], out var views) || !Num(r[iAvg], out var avg) || !Num(r[iCtr], out var ctr)) continue;
            var minutes = iMinutes >= 0 && Num(r[iMinutes], out var mw) ? mw : 0.0;
            result[id!] = new AnalyticsVideoMetrics((long)Math.Max(0, views), minutes, avg, ctr);
        }
        return result;

        static bool Num(JsonElement e, out double v)
        {
            v = 0;
            return e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out v) && double.IsFinite(v);
        }
    }

    /// <summary>
    /// Data API 관측 행 + Analytics 지표 → 정밀 행(<see cref="YouTubeAnalyticsRow.HasPrecisionMetrics"/> = true). 조회 &lt; <see cref="YouTubeStudioAnalyticsNormalizer.MinViews"/> ·
    /// 유지율/CTR 0(아직 집계 전 · 노출 없음) → false(관측 전용 유지). 유지율 100 % 초과(Shorts 반복) → Studio 와 같은 포화 곡선. 3초 유지(Stayed)는 v2 에 없어 완주율 기반 추정.
    /// </summary>
    public static bool TryBuildPrecisionRow(YouTubeAnalyticsRow observed, AnalyticsVideoMetrics m, out YouTubeAnalyticsRow row)
    {
        ArgumentNullException.ThrowIfNull(observed);
        row = observed;
        if (m.Views < YouTubeStudioAnalyticsNormalizer.MinViews) return false;
        if (!(m.AverageViewPercentage > 0) || !(m.CtrPct > 0) || !double.IsFinite(m.AverageViewPercentage) || !double.IsFinite(m.CtrPct)) return false;
        var completion = YouTubeStudioAnalyticsNormalizer.SaturateCompletion(m.AverageViewPercentage);
        row = new YouTubeAnalyticsRow(observed.YouTubeVideoId, observed.Title, observed.PublishedAtUtc, m.Views,
            PlutoTelemetryBatchImporter.EstimateHookFromCompletion(completion), true, completion, m.AverageViewPercentage, Math.Min(m.CtrPct, 100.0), false)
        {
            Likes = observed.Likes, Comments = observed.Comments, ExportLanguage = observed.ExportLanguage, HasPrecisionMetrics = true,
        };
        return true;
    }

    /// <summary>
    /// 채널별 · 50개 묶음 <c>reports.query</c>(dimensions=video · 1 요청 = 1 unit 보수 집계) → 정밀 승격 → 기획 채점 · 유기적 복권 즉시 실행.
    /// 🔐 채널 403 → 그 채널만 <see cref="AnalyticsScopeRetryInterval"/> 래치(다른 채널 · Data API 결과 유지).
    /// </summary>
    private async Task<AnalyticsOutcome> RunAnalyticsPrecisionAsync(PlutoYouTubeApiCredentials creds, List<YouTubeAnalyticsRow> rows, Dictionary<string, string> idChannel, RunCounters run, CancellationToken ct)
    {
        var inv = CultureInfo.InvariantCulture;
        var now = Now();
        var end = AnalyticsEndDate(now);
        var floor = end.AddDays(-AnalyticsMaxLookbackDays);
        var byId = new Dictionary<string, YouTubeAnalyticsRow>(StringComparer.Ordinal);
        var perChannel = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var deferred = 0;
        foreach (var r in rows)
        {
            if (r.YouTubeVideoId is not { } id || !idChannel.TryGetValue(id, out var ch) || byId.ContainsKey(id)) continue;
            if (!IsAnalyticsReportMature(r.PublishedAtUtc, now)) { deferred++; continue; }
            byId[id] = r;
            if (!perChannel.TryGetValue(ch, out var list)) perChannel[ch] = list = new List<string>();
            list.Add(id);
        }

        var promoted = new List<YouTubeAnalyticsRow>();
        int scopeMissing = 0, queried = 0;
        foreach (var ch in creds.Channels)
        {
            bool latched;
            lock (_sync) latched = _state.AnalyticsScopeMissing is { } m && m.TryGetValue(ch.ChannelId, out var until) && until > now;
            if (latched) { scopeMissing++; continue; }
            if (!perChannel.TryGetValue(ch.ChannelId, out var ids)) continue;
            for (var i = 0; i < ids.Count; i += MaxIdsPerRequest)
            {
                var batch = ids.GetRange(i, Math.Min(MaxIdsPerRequest, ids.Count - i));
                var start = end;
                foreach (var id in batch) { var d = PacificDate(byId[id].PublishedAtUtc!.Value); if (d < start) start = d; }
                if (start < floor) start = floor;
                var url = AnalyticsReportsEndpoint + "?ids=" + Uri.EscapeDataString("channel==" + ch.ChannelId)
                    + "&startDate=" + start.ToString("yyyy-MM-dd", inv) + "&endDate=" + end.ToString("yyyy-MM-dd", inv)
                    + "&metrics=" + Uri.EscapeDataString(AnalyticsMetrics) + "&dimensions=video"
                    + "&filters=" + Uri.EscapeDataString("video==" + string.Join(",", batch)) + "&maxResults=" + MaxIdsPerRequest.ToString(inv);
                Dictionary<string, AnalyticsVideoMetrics> metrics;
                try
                {
                    using var doc = await GetJsonAsync(url, run, ct, analyticsUrl: true).ConfigureAwait(false);
                    metrics = ParseAnalyticsReport(doc);
                }
                catch (AnalyticsScopeException)
                {
                    lock (_sync)
                    {
                        EnsureStateLoadedLocked();
                        (_state.AnalyticsScopeMissing ??= new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal))[ch.ChannelId] = now + AnalyticsScopeRetryInterval;
                        PersistStateLocked();
                    }
                    scopeMissing++;
                    break;   // 이 채널의 남은 묶음도 같은 403 → 쿼터 낭비 차단
                }
                queried++;
                foreach (var id in batch)
                    if (metrics.TryGetValue(id, out var mv) && TryBuildPrecisionRow(byId[id], mv, out var pr)) promoted.Add(pr);
                    else deferred++;
            }
        }

        PlutoTelemetryBatchImportResult? import = null;
        PlutoBriefSweepResult? sweep = null;
        if (promoted.Count > 0 && _importer is not null)
        {
            import = await _importer.ImportYouTubeAnalyticsPrecisionRowsAsync(promoted, PlatformName, ct).ConfigureAwait(false);
            if (import.MatchedAndLearnedCount + import.MaturityDeltaCount > 0 && BriefOutcomes is { } briefs)
            {
                try { sweep = await briefs.EvaluatePendingBriefOutcomesAndRehabilitationAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.YouTubeAnalytics] brief sweep: " + ex.GetType().Name); }   // 채점 실패는 학습과 격리
            }
        }
        lock (_sync)
        {
            EnsureStateLoadedLocked();
            if (queried > 0) _state.AnalyticsScopeConfirmed = true;
            if (promoted.Count > 0) { _state.AnalyticsLastPrecisionUtc = now; _state.AnalyticsLastPromoted = promoted.Count; }
            PersistStateLocked();
        }
        var mode = scopeMissing >= creds.Channels.Count ? PlutoYouTubeAnalyticsPrecisionMode.AwaitingScope : PlutoYouTubeAnalyticsPrecisionMode.Active;
        return new AnalyticsOutcome(mode) { Import = import, Promoted = promoted.Count, Deferred = deferred, ScopeMissing = scopeMissing, Sweep = sweep };
    }

    public PlutoYouTubeAnalyticsPrecisionStatus GetAnalyticsPrecisionStatus()
    {
        var c = LoadCredentials();
        var mode = AnalyticsModeFor(c);
        lock (_sync)
        {
            EnsureStateLoadedLocked();
            var now = Now();
            var missing = c is null ? 0 : ScopeMissingCountLocked(c, now);
            DateTimeOffset? retry = null;
            if (_state.AnalyticsScopeMissing is { Count: > 0 } m)
                foreach (var v in m.Values) if (v > now && (retry is null || v < retry)) retry = v;
            return new PlutoYouTubeAnalyticsPrecisionStatus(mode, _state.AnalyticsEnabled ?? true, missing, c?.Channels.Count ?? 0, retry, _state.AnalyticsLastPrecisionUtc, _state.AnalyticsLastPromoted);
        }
    }

    public Task SetAnalyticsPrecisionEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            EnsureStateLoadedLocked();
            _state.AnalyticsEnabled = enabled;
            PersistStateLocked();
        }
        RaiseChanged();
        return Task.CompletedTask;
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
                var until = NextQuotaResetUtc(Now());
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
            if (recordSync) _state.LastSyncUtc = Now();
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

    /// <summary>🧯 Step 36 — 구독자별 격리 호출: 끊긴 Blazor 회로 핸들러 1개가 던져도 나머지 구독자(다른 페이지 · 설정 카드)는 계속 받는다.</summary>
    private void RaiseChanged()
    {
        if (Changed is not { } h) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action)d)(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.YouTubeApi] Changed handler: " + ex.GetType().Name); }
        }
    }

    private void RaiseAutoSyncCompleted(PlutoYouTubeApiAutoSyncEvent e)
    {
        if (AutoSyncCompleted is not { } h) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action<PlutoYouTubeApiAutoSyncEvent>)d)(e); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.YouTubeApi] AutoSyncCompleted handler: " + ex.GetType().Name); }
        }
    }

    private void ReleaseGate()
    {
        try { _syncGate.Release(); }
        catch (ObjectDisposedException) { }   // Dispose 와 경합한 실행 꼬리
    }

    // ── ⏱️ Step 36 — 쿼터 인지형 주기 자동 동기화 ──

    public bool AutoSyncEnabled { get { lock (_sync) { EnsureStateLoadedLocked(); return _state.AutoSyncEnabled ?? true; } } }

    /// <summary>타이머가 다음 실행을 예약해 둔 상태.</summary>
    public bool IsAutoSyncArmed { get { lock (_sync) return _autoTimer is not null && _autoArmedFor is not null; } }

    /// <summary>백그라운드 자동 실행이 동기화 본체를 수행 중.</summary>
    public bool IsAutoSyncRunning { get { lock (_sync) return _autoRunning; } }

    /// <summary>실제로 동기화 본체까지 간 자동 실행 수(조기 발화 재무장 · 정지 판정은 제외).</summary>
    public long AutoSyncTickCount => Interlocked.Read(ref _autoTicks);

    /// <summary>연속 일시 오류 n회 → <c>min(5분 × 2^(n−1), 6시간)</c>.</summary>
    public static TimeSpan AutoBackoffDelay(int consecutiveFailures)
    {
        var n = Math.Clamp(consecutiveFailures, 1, 16);
        var ticks = AutoBackoffBase.Ticks * (1L << (n - 1));
        return ticks >= MaxAutoBackoff.Ticks ? MaxAutoBackoff : TimeSpan.FromTicks(ticks);
    }

    public bool StartAutoSync()
    {
        Volatile.Write(ref _autoStartRequested, true);   // 지금 미설정이어도 이후 자격 증명 저장 시 자동 무장
        if (!IsNetworkAllowed)
        {
            lock (_sync) DisarmLocked();   // 🔒 미설정 · 킬 스위치 · 하네스 격리 → 타이머 자체를 만들지 않음
            return false;
        }
        lock (_sync)
        {
            if (_disposed) return false;
            EnsureStateLoadedLocked();
            if (!(_state.AutoSyncEnabled ?? true) || _state.AutoAuthLatched) { DisarmLocked(); return false; }
            if (_autoArmedFor is not null || _autoRunning) return true;   // 멱등(페이지 재진입마다 호출돼도 예정 유지)
            var now = Now();
            // 🛡️ 재시작 폭주 차단 — 영속된 예정(백오프 포함) · 마지막 성공 + 주기를 존중하고, 최소 기동 유예를 둔다
            var due = _state.AutoNextDueUtc ?? (_state.LastSyncUtc is { } ls ? ls + AutoSyncInterval : now);
            var floor = now + AutoSyncStartupGrace;
            if (due < floor) due = floor;
            var cap = now + MaxAutoBackoff;
            if (due > cap) due = cap;   // 시계 되감김 · 손상 상태로 영영 안 깨어나는 것 방지
            ArmLocked(ApplyPauseLocked(due, now), now, persist: true);
        }
        RaiseChanged();
        return true;
    }

    public void StopAutoSync()
    {
        Volatile.Write(ref _autoStartRequested, false);
        lock (_sync)
        {
            CancelAutoRunLocked();
            DisarmLocked();
        }
        RaiseChanged();
    }

    public Task SetAutoSyncEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_disposed) return Task.CompletedTask;
            EnsureStateLoadedLocked();
            _state.AutoSyncEnabled = enabled;
            if (!enabled) { CancelAutoRunLocked(); DisarmLocked(); }
            PersistStateLocked();
        }
        if (enabled) StartAutoSync();
        RaiseChanged();
        return Task.CompletedTask;
    }

    public PlutoYouTubeApiAutoSyncStatus GetAutoSyncStatus()
    {
        var allowed = IsNetworkAllowed;
        lock (_sync)
        {
            EnsureStateLoadedLocked();
            var now = Now();
            RollQuotaDayLocked(now);
            var enabled = _state.AutoSyncEnabled ?? true;
            var remaining = Math.Max(0, DailyUnitBudget - _state.QuotaUnitsUsed);
            var armed = _autoTimer is not null && _autoArmedFor is not null;
            var cooling = _state.CooldownUntilUtc is { } cd && cd > now;
            PlutoYouTubeApiAutoSyncPhase phase =
                !enabled ? PlutoYouTubeApiAutoSyncPhase.Off
                : !allowed ? PlutoYouTubeApiAutoSyncPhase.Unavailable
                : _state.AutoAuthLatched ? PlutoYouTubeApiAutoSyncPhase.AuthPaused
                : _autoRunning ? PlutoYouTubeApiAutoSyncPhase.Running
                : cooling || remaining == 0 ? PlutoYouTubeApiAutoSyncPhase.QuotaPaused
                : remaining < AutoSyncReserveUnits ? PlutoYouTubeApiAutoSyncPhase.ReservePaused
                : !armed ? PlutoYouTubeApiAutoSyncPhase.NotStarted
                : _state.AutoConsecutiveFailures > 0 ? PlutoYouTubeApiAutoSyncPhase.Backoff
                : PlutoYouTubeApiAutoSyncPhase.Scheduled;
            return new PlutoYouTubeApiAutoSyncStatus(enabled, armed, phase, armed ? _autoArmedFor : _state.AutoNextDueUtc, _state.AutoConsecutiveFailures,
                _state.AutoLastRunUtc, _state.AutoLastSummaryKorean, Interlocked.Read(ref _autoTicks), remaining, AutoSyncReserveUnits, AutoSyncInterval);
        }
    }

    /// <summary>냉각(<c>quotaExceeded</c> 영속) · 자체 예산 예비분 미만 → 예정 시각을 해제 시각 뒤로 민다.</summary>
    private DateTimeOffset ApplyPauseLocked(DateTimeOffset due, DateTimeOffset now)
    {
        if (_state.CooldownUntilUtc is { } cd && cd > now && due < cd + AutoResumeSlack) due = cd + AutoResumeSlack;
        RollQuotaDayLocked(now);
        if (DailyUnitBudget - _state.QuotaUnitsUsed < AutoSyncReserveUnits)
        {
            var reset = NextQuotaResetUtc(now) + AutoResumeSlack;
            if (due < reset) due = reset;
        }
        return due;
    }

    /// <summary>단발(one-shot) 타이머 재무장 — 주기 타이머가 아니므로 느린 실행 · 절전 복귀가 발화를 쌓지 않는다.</summary>
    private void ArmLocked(DateTimeOffset due, DateTimeOffset now, bool persist)
    {
        if (_disposed) return;
        _state.AutoNextDueUtc = due;
        if (persist) PersistStateLocked();
        _autoArmedFor = due;
        var delay = due - now;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        try
        {
            if (_autoTimer is null) _autoTimer = TimeProvider.CreateTimer(static s => ((PlutoYouTubeApiSyncService)s!).OnAutoTimer(), this, delay, Timeout.InfiniteTimeSpan);
            else _autoTimer.Change(delay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException) { _autoTimer = null; _autoArmedFor = null; }
    }

    private void DisarmLocked()
    {
        _autoArmedFor = null;
        var t = _autoTimer;
        _autoTimer = null;
        try { t?.Dispose(); } catch (ObjectDisposedException) { }
    }

    private void CancelAutoRunLocked()
    {
        try { _autoRunCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>타이머 콜백 — 비동기 꼬리는 버리고(async void 금지) 예외는 전부 격리.</summary>
    private void OnAutoTimer() => _ = AutoTimerFiredAsync();

    private async Task AutoTimerFiredAsync()
    {
        try { await RunAutoSyncTickAsync().ConfigureAwait(false); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.YouTubeApi] auto tick: " + ex.GetType().Name); }
    }

    /// <summary>
    /// ⏱️ 자동 실행 1회(타이머가 호출 · 테스트는 직접 호출 가능). 예정 전 조기 발화 → 재무장만 · 꺼짐/미설정/격리 → 해제 ·
    /// 냉각/예비분 미만 → 해제 시각으로 재예약 · 수동 실행과 겹치면 Busy 로 합류(요청 0) · 결과에 따라 주기 · 지수 백오프 · 인증 정지.
    /// 반환 null = 본체 미실행.
    /// </summary>
    public async Task<PlutoYouTubeApiSyncResult?> RunAutoSyncTickAsync(bool force = false, CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _tickInFlight, 1, 0) != 0) return null;   // 자동 실행끼리 중첩 금지
        PlutoYouTubeApiSyncResult? r;
        try { r = await AutoTickCoreAsync(force, ct).ConfigureAwait(false); }
        finally { Volatile.Write(ref _tickInFlight, 0); }
        // 플래그 해제 뒤 알림 — 구독자가 곧바로 다음 발화를 일으켜도 '실행 중'으로 버려지지 않음
        if (r is not null) RaiseAutoSyncCompleted(new PlutoYouTubeApiAutoSyncEvent(r, Now()));
        return r;
    }

    private async Task<PlutoYouTubeApiSyncResult?> AutoTickCoreAsync(bool force, CancellationToken ct)
    {
        {
            PlutoYouTubeApiSyncResult? gated = null;
            lock (_sync)
            {
                if (_disposed) return null;
                EnsureStateLoadedLocked();
                if (!(_state.AutoSyncEnabled ?? true) || _state.AutoAuthLatched) { DisarmLocked(); return null; }
                var now = Now();
                if (!force && _state.AutoNextDueUtc is { } due && due > now + EarlyFireTolerance) { ArmLocked(due, now, persist: false); return null; }
            }
            var pre = Preflight();
            lock (_sync)
            {
                if (_disposed) return null;
                var now = Now();
                if (pre is { State: PlutoYouTubeApiSyncState.Disabled or PlutoYouTubeApiSyncState.NotConfigured })
                {
                    DisarmLocked();   // 🔒 실행 중 킬 스위치 · 연결 해제 → 이후 요청 0
                    gated = pre;
                }
                else
                {
                    var paused = ApplyPauseLocked(now, now);
                    if (pre is { State: PlutoYouTubeApiSyncState.BudgetExhausted } && paused <= now) paused = NextQuotaResetUtc(now) + AutoResumeSlack;
                    if (paused > now)
                    {
                        ArmLocked(paused, now, persist: true);   // 💸 냉각 · 예비분 보호 — 네트워크 없이 해제 시각으로
                        var rem = Math.Max(0, DailyUnitBudget - _state.QuotaUnitsUsed);
                        gated = pre ?? new PlutoYouTubeApiSyncResult(PlutoYouTubeApiSyncState.BudgetExhausted,
                            "⏸ 남은 쿼터 " + rem.ToString(CultureInfo.InvariantCulture) + " unit < 예비 " + AutoSyncReserveUnits.ToString(CultureInfo.InvariantCulture) + " — 자동 동기화는 태평양 자정 이후(수동 가능)");
                    }
                }
            }
            if (gated is not null)
            {
                RaiseChanged();
                return gated;
            }

            CancellationTokenSource runCts;
            lock (_sync)
            {
                if (_disposed) return null;
                runCts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token, ct);
                _autoRunCts = runCts;
                _autoRunning = true;
                _autoArmedFor = null;
            }
            RaiseChanged();
            PlutoYouTubeApiSyncResult? result = null;
            try { result = await SyncInternalAsync(runCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* 연결 해제 · 재저장 · 끄기 · 종료 — 각 경로가 무장 상태를 스스로 정함 */ }
            finally
            {
                lock (_sync)
                {
                    _autoRunning = false;
                    if (ReferenceEquals(_autoRunCts, runCts)) _autoRunCts = null;
                }
                runCts.Dispose();
            }
            if (result is null)
            {
                // 취소됨 — 재저장(구성 교체)이면 새 구성으로 재무장 · 연결 해제/끄기/정지/종료면 StartAutoSync 가 스스로 거부
                if (Volatile.Read(ref _autoStartRequested)) StartAutoSync(); else RaiseChanged();
                return null;
            }
            if (result.State is not PlutoYouTubeApiSyncState.Busy) Interlocked.Increment(ref _autoTicks);
            ScheduleAfterAutoRun(result);
            RaiseChanged();
            return result;
        }
    }

    private void ScheduleAfterAutoRun(PlutoYouTubeApiSyncResult r)
    {
        lock (_sync)
        {
            if (_disposed) return;
            EnsureStateLoadedLocked();
            var now = Now();
            if (r.State is not PlutoYouTubeApiSyncState.Busy)
            {
                _state.AutoLastRunUtc = now;
                _state.AutoLastSummaryKorean = r.SummaryKorean;
            }
            DateTimeOffset due;
            switch (r.State)
            {
                case PlutoYouTubeApiSyncState.Synced:
                case PlutoYouTubeApiSyncState.Partial:
                    _state.AutoConsecutiveFailures = 0;
                    due = now + AutoSyncInterval;
                    break;
                case PlutoYouTubeApiSyncState.Busy:
                    due = now + AutoSyncInterval;   // 🔀 수동 실행에 합류 — 그쪽 성공이 다시 한 주기 뒤로 리셋
                    break;
                case PlutoYouTubeApiSyncState.AuthFailed:
                    _state.AutoAuthLatched = true;   // 🔐 재저장 전까지 정지 — 거부된 리프레시 토큰으로 토큰 엔드포인트를 주기적으로 두드리지 않음
                    DisarmLocked();
                    PersistStateLocked();
                    return;
                case PlutoYouTubeApiSyncState.Disabled:
                case PlutoYouTubeApiSyncState.NotConfigured:
                    DisarmLocked();
                    PersistStateLocked();
                    return;
                case PlutoYouTubeApiSyncState.BudgetExhausted:
                    due = NextQuotaResetUtc(now) + AutoResumeSlack;
                    break;
                case PlutoYouTubeApiSyncState.QuotaCooldown:
                    due = now + AutoSyncInterval;   // ApplyPause 가 영속 냉각 시각으로 민다
                    break;
                default:   // Failed — 429 · 5xx · 시간 초과(요청 내 3회 재시도 후) → 실행 간 지수 백오프
                    _state.AutoConsecutiveFailures = Math.Min(_state.AutoConsecutiveFailures + 1, 1000);
                    due = now + AutoBackoffDelay(_state.AutoConsecutiveFailures);
                    break;
            }
            if (!(_state.AutoSyncEnabled ?? true)) { DisarmLocked(); PersistStateLocked(); return; }
            ArmLocked(ApplyPauseLocked(due, now), now, persist: true);
        }
    }

    /// <summary>수동 성공 → 실패 누적 · 인증 정지 해제 · 다음 자동 예정을 지금부터 한 주기 뒤로(이미 받은 지표를 곧바로 다시 받지 않음).</summary>
    private void OnManualSyncSucceeded()
    {
        var restart = false;
        lock (_sync)
        {
            if (_disposed) return;
            EnsureStateLoadedLocked();
            var now = Now();
            _state.AutoConsecutiveFailures = 0;
            restart = _state.AutoAuthLatched;
            _state.AutoAuthLatched = false;
            var due = ApplyPauseLocked(now + AutoSyncInterval, now);
            if (_autoTimer is not null && _autoArmedFor is not null) ArmLocked(due, now, persist: true);
            else { _state.AutoNextDueUtc = due; PersistStateLocked(); }
        }
        if (restart && Volatile.Read(ref _autoStartRequested)) StartAutoSync();
        RaiseChanged();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            CancelAutoRunLocked();
            DisarmLocked();
            _disposed = true;
            _http?.Dispose();
            _http = null;
            _creds = null;
        }
        try { _life.Cancel(); } catch (ObjectDisposedException) { }
        _syncGate.Dispose();
        _life.Dispose();
        Changed = null;
        AutoSyncCompleted = null;
    }
}
