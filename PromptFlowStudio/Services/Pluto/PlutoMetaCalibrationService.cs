using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>
/// 🎯 메타 보정 관측 1건 — 합성 점수 0~1 척도. 예측 불가(기준선 없음) → <see cref="PredictedExpectedScore"/> = NaN(보정 오차 미집계),
/// 대안 없음 → <see cref="BestAlternativeExpectedScore"/> = NaN(후회 미집계).
/// </summary>
public sealed record PlutoCalibrationObservation(double PredictedExpectedScore, double RealizedScore, double BestAlternativeExpectedScore)
{
    /// <summary>대안 라벨(예: <c>hook:…</c> · A/B 상대 변형) — 후회 귀속 집계용(없음 → null).</summary>
    public string? BestAlternativeLabel { get; init; }
    /// <summary>출처(<c>closed-loop</c> · <c>ab</c>).</summary>
    public string? Source { get; init; }
    /// <summary>🔁 이번에 실제로 선택(= 실측 검증)된 구성 라벨(<c>hook:…</c> · <c>look:…</c> · <c>pair:…</c>) — 일치하는 후회 회복 사전을 소거한다.</summary>
    public IReadOnlyList<string>? ChosenLabels { get; init; }
    /// <summary>🔁 대안 점수가 예측이 아니라 실측(A/B 상대 변형) — 이미 검증된 대안이므로 회복 사전을 적립하지 않는다(후회 귀속 집계는 유지).</summary>
    public bool AlternativeMeasured { get; init; }

    public bool HasPrediction => double.IsFinite(PredictedExpectedScore) && double.IsFinite(RealizedScore);
    public bool HasAlternative => double.IsFinite(BestAlternativeExpectedScore) && double.IsFinite(RealizedScore);
    /// <summary>반사실 후회 = <c>max(0, 최선 대안 − 실현)</c>(대안 없음 → 0).</summary>
    public double Regret => HasAlternative ? Math.Max(0.0, BestAlternativeExpectedScore - RealizedScore) : 0.0;
}

/// <summary>
/// 🎯 반사실 예측 스냅샷 — 기준선 · 선택 구성 예측 점수 · 최선 미선택 대안 예측 점수(없음 → NaN) · 대안 라벨(<c>hook:…</c>/<c>look:…</c>) ·
/// 예측에 참여한 성립 패턴 수.
/// </summary>
public sealed record PlutoExpectancyForecast(bool HasBaseline, double Baseline, double PredictedScore, double BestAlternativeScore, string? BestAlternativeLabel, int EstablishedPatterns)
{
    public static readonly PlutoExpectancyForecast None = new(false, 0.0, double.NaN, double.NaN, null, 0);
    public bool HasAlternative => HasBaseline && double.IsFinite(BestAlternativeScore);
}

/// <summary>💸 후회 귀속 — 선택되지 않은 대안별 누적 반사실 후회.</summary>
public sealed record PlutoRegretAttribution(string Label, double CumulativeRegret, int Count)
{
    /// <summary>🔁 미해소 후회 잔액(반감기 감쇠 반영 · 실측 검증되면 0) — 리포트의 '가장 아쉬운 대안' 선정 기준.</summary>
    public double OutstandingRegret { get; init; }
    /// <summary>✅ 이미 실측 검증됐거나 감쇠로 소멸 — 더 이상 '놓친 대안'이 아니다(누적 값은 이력).</summary>
    public bool IsResolved => !(OutstandingRegret > 0);
}

/// <summary>
/// 🔁 후회 회복 사전 — 거듭 지나친 대안(<see cref="Label"/>)을 챌린저 B 로 재검증하도록 주는 유계 · 시간 감쇠 부스트.
/// <see cref="OutstandingRegret"/> = 아직 검증으로 해소되지 않은 후회 잔액(14일 반감기).
/// </summary>
public sealed record PlutoRegretRecoveryBoost(string Label, double Boost, double OutstandingRegret, int Count)
{
    /// <summary>🔁 1줄(결정론 한국어).</summary>
    public string ToLabel()
    {
        var inv = CultureInfo.InvariantCulture;
        return Label + " · 회복 사전 +" + Boost.ToString("0.00", inv) + " · 미해소 후회 " + OutstandingRegret.ToString("0.00", inv) + " · " + Count.ToString(inv) + "회 지나침";
    }
}

/// <summary>🎯 메타 보정 현황 스냅샷(두뇌 리포트 · 랭커 공용).</summary>
public sealed record PlutoCalibrationStatus(int CalibrationSamples, double BrierScore, double Rmse, double Bias, int RegretSamples, double MeanRegret,
    double CumulativeRegret, int TotalRecords, double ExplorationScale)
{
    public static readonly PlutoCalibrationStatus Empty = new(0, 0.0, 0.0, 0.0, 0, 0.0, 0.0, 0, 1.0);

    /// <summary>💸 누적 후회가 가장 큰 미선택 대안(없음 → null).</summary>
    public PlutoRegretAttribution? TopRegretAlternative { get; init; }
    /// <summary>보정 정확도 0~100(= 100 × (1 − RMSE) · 표본 0 → 0).</summary>
    public double AccuracyPct => CalibrationSamples > 0 ? Math.Round(Math.Clamp(1.0 - Rmse, 0.0, 1.0) * 100.0, 1, MidpointRounding.AwayFromZero) : 0.0;
    /// <summary>판정에 충분한 표본.</summary>
    public bool HasEvidence => CalibrationSamples >= PlutoMetaCalibrationService.MinSamples || RegretSamples >= PlutoMetaCalibrationService.MinSamples;
    /// <summary>과신(예측 &gt; 실현 편향이 허용 폭 초과).</summary>
    public bool IsOverconfident => CalibrationSamples >= PlutoMetaCalibrationService.MinSamples && Bias > PlutoMetaCalibrationService.BiasDeadBand;
    /// <summary>탐색 증폭 중(배수 &gt; 1).</summary>
    public bool IsAmplifying => ExplorationScale > 1.0 + 1e-9;
    /// <summary>활용 집중 중(배수 &lt; 1).</summary>
    public bool IsTightening => ExplorationScale < 1.0 - 1e-9;
}

/// <summary>
/// 🎯 Pluto 메타 보정 — 예측 기대 점수 vs 실현 점수(Brier/RMSE · 편향)와 반사실 후회(선택하지 않은 최선 대안 대비 기회 비용)를 감사해
/// UCB1 탐색 상수의 메타 배수를 산출한다.
/// </summary>
public interface IPlutoMetaCalibrationService
{
    /// <summary>상태 선적재(멱등 · 실패 → 빈 상태로 [부분 구제] · 취소만 전파).</summary>
    Task EnsureLoadedAsync(CancellationToken ct = default);

    /// <summary>관측 1건 기록(원자 영속 · 영속 실패 → 메모리 유지). 예측 · 대안이 모두 없으면 무변경.</summary>
    Task<PlutoCalibrationStatus> RecordOutcomeAsync(PlutoCalibrationObservation observation, CancellationToken ct = default);

    /// <summary>현재 보정 현황(미적재 → <see cref="PlutoCalibrationStatus.Empty"/>).</summary>
    PlutoCalibrationStatus GetStatus();

    /// <summary>
    /// 🎚️ 메타 보정 탐색 배수 — 과신 · 후회 상승 → 1 초과(최대 <see cref="PlutoMetaCalibrationService.MaxScale"/>) ·
    /// 보정 양호 + 후회 낮음 → 1 미만(최소 <see cref="PlutoMetaCalibrationService.MinScale"/>) · 표본 부족 → 1.
    /// </summary>
    double GetCalibratedExplorationScale();

    /// <summary>
    /// 🔁 능동 후회 리허설 — 미해소 후회 잔액이 <see cref="PlutoMetaCalibrationService.RecoveryThreshold"/> 이상인 미선택 대안의 회복 사전
    /// (부스트 내림차순 · 동률 → 라벨 사전순). <paramref name="blockedTokens"/>(오답 노트 차단 토큰/조합 키)에 걸린 대안은 제외.
    /// </summary>
    IReadOnlyList<PlutoRegretRecoveryBoost> GetRegretRecoveryBoosts(IReadOnlySet<string>? blockedTokens = null) => Array.Empty<PlutoRegretRecoveryBoost>();

    /// <summary>기록 통지(백그라운드 스레드 가능).</summary>
    event Action? Changed;
}

/// <summary>
/// 🎯 <see cref="IPlutoMetaCalibrationService"/> 구현(순수 BCL · 결정론) — <c>pluto_calibration_state.json</c>(☁️ Drive 볼트 · 로컬 폴백 · 원자 기록).
///   ▸ 롤링 창 = 최근 <see cref="WindowCapacity"/>건(보정 오차 · 평균 후회) · 누적 후회 · 대안별 후회 귀속은 창과 무관하게 영속.
///   ▸ Brier = mean((예측 − 실현)²) · RMSE = √Brier · 편향 = mean(예측 − 실현)(양수 = 과신).
///   ▸ 압력 = max(과신 압력, 후회 압력) — 각각 허용 폭(dead band) 초과분을 선형 정규화 → 배수 = 1 + (Max − 1) × 압력.
///   ▸ 압력 0 + RMSE ≤ <see cref="WellCalibratedRmse"/> → 표본 수에 비례해 <see cref="MinScale"/> 쪽으로 수축(고신뢰 활용).
///   ▸ 표본 &lt; <see cref="MinSamples"/> → 1.0(기존 동작과 완전 동일).
///   ▸ 🔁 후회 회복 사전: 대안별 '미해소 후회 잔액'(예측 반사실만 적립 · <see cref="RecoveryHalfLifeDays"/>일 반감기 · 그 대안이 실제 선택되면 0).
///   ▸ 🛡️ 내구성: 기록마다 <c>.bak</c> 마지막 정상본 동반 기록 → 본 파일 손상(Drive 0 B 자리표시자 · 잘린 동기화) 시 자동 복구.
///     적재 불가(잠금·권한) 세션은 디스크 덮어쓰기 금지 — 다음 기록 때 재적재를 시도해 잠금이 풀렸으면 세션 중 관측을 디스크 상태 위에 재생(자가 치유).
/// </summary>
public sealed class PlutoMetaCalibrationService : IPlutoMetaCalibrationService, IPlutoHotReloadable, IDisposable
{
    public const string FileName = "pluto_calibration_state.json";
    public const int WindowCapacity = 64;
    public const int MinSamples = 3;
    /// <summary>수축(활용 집중)이 최대에 이르는 표본 수.</summary>
    public const int FullConfidenceSamples = 10;
    public const double MinScale = 0.6, MaxScale = 2.0;
    /// <summary>과신 편향 허용 폭 · 최대 압력 지점(합성 점수 0~1).</summary>
    public const double BiasDeadBand = 0.04, BiasFull = 0.15;
    /// <summary>평균 후회 허용 폭 · 최대 압력 지점.</summary>
    public const double RegretDeadBand = 0.03, RegretFull = 0.15;
    public const double WellCalibratedRmse = 0.08;
    public const int MaxAttributions = 32;
    public const int MaxLabelLength = 120;
    /// <summary>🔁 회복 사전 발동 잔액 · 최대 부스트 도달 잔액 · 부스트 상한(UCB1 점수 척도) · 잔액 반감기(일).</summary>
    public const double RecoveryThreshold = 0.10, RecoveryFullRegret = 0.50, MaxRecoveryBoost = 0.50, RecoveryHalfLifeDays = 14.0;
    /// <summary>🛡️ 마지막 정상본 확장자.</summary>
    public const string BackupSuffix = ".bak";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly string _path;
    private readonly IPlutoStorageManager? _storage;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly List<RecordDto> _window = new(WindowCapacity);
    private readonly Dictionary<string, AttributionDto> _attributions = new(StringComparer.Ordinal);
    private double _cumulativeRegret;
    private int _totalRecords;
    private volatile bool _loaded;
    private volatile bool _readOnly;
    private volatile bool _recoveredFromBackup;
    private int _healedSessions;
    private readonly List<(PlutoCalibrationObservation Obs, DateTime Utc)> _readOnlyPending = new();
    private int _disposed;

    /// <summary>☁️ <see cref="IPlutoStorageManager"/> 경유 — Drive 우선 · 로컬 폴백.</summary>
    public PlutoMetaCalibrationService(IPlutoStorageManager storage) : this((string?)null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    public PlutoMetaCalibrationService(string? storagePath = null)
    {
        _path = string.IsNullOrWhiteSpace(storagePath)
            ? Path.Combine(global::PromptFlowStudio.Services.Platform.AppDataPaths.LocalAppData, "PromptFlowStudio", FileName)
            : storagePath;
    }

    /// <summary>🕰️ 결정론 시계 주입(테스트용).</summary>
    public Func<DateTime>? UtcNowProvider { get; init; }

    public string StoragePath => _storage?.ResolveDataPath(FileName) ?? _path;

    /// <summary>🛡️ 적재 불가 세션(디스크 보존 · 메모리만 갱신).</summary>
    public bool IsReadOnlySession => _readOnly;

    /// <summary>🛡️ 본 파일 손상으로 <c>.bak</c> 마지막 정상본에서 복구한 세션.</summary>
    public bool RecoveredFromBackup => _recoveredFromBackup;

    /// <summary>🛡️ 읽기 전용 세션이 잠금 해제 후 자가 치유된 횟수.</summary>
    public int HealedReadOnlySessions => Volatile.Read(ref _healedSessions);

    public string BackupPath => StoragePath + BackupSuffix;

    private DateTime Now() => UtcNowProvider?.Invoke() ?? DateTime.UtcNow;

    public event Action? Changed;

    /// <summary>직렬화 DTO — 관측 1건(NaN 은 null 로 영속).</summary>
    public sealed class RecordDto
    {
        public double? P { get; set; }
        public double R { get; set; }
        public double? A { get; set; }
        public DateTime Utc { get; set; }
        public string? Src { get; set; }
    }

    public sealed class AttributionDto
    {
        public double Regret { get; set; }
        public int Count { get; set; }
        /// <summary>🔁 미해소 후회 잔액(<see cref="Utc"/> 기준 · null = Step 14 파일 → <see cref="Regret"/> 로 간주).</summary>
        public double? Recovery { get; set; }
        /// <summary>🔁 잔액 기준 시각(default → 감쇠 없음).</summary>
        public DateTime Utc { get; set; }
    }

    /// <summary>영속 스냅샷(버전 필드로 향후 스키마 확장 · 미지 필드는 무시되어 상·하위 호환).</summary>
    public sealed class Snapshot
    {
        public int Version { get; set; } = 1;
        public double CumulativeRegret { get; set; }
        public int TotalRecords { get; set; }
        public List<RecordDto>? Window { get; set; }
        public Dictionary<string, AttributionDto>? Attributions { get; set; }
    }

    // ── 🧮 순수 함수 ──

    /// <summary>Brier(평균 제곱 오차) — 유한 쌍만 집계 · 표본 0 → (0, 0, 0).</summary>
    public static (double Brier, double Bias, int Samples) ComputeCalibration(IReadOnlyList<(double Predicted, double Realized)> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        double sq = 0, bias = 0; var n = 0;
        for (var i = 0; i < pairs.Count; i++)
        {
            var (p, r) = pairs[i];
            if (!double.IsFinite(p) || !double.IsFinite(r)) continue;
            var d = Clamp01(p) - Clamp01(r);
            sq += d * d; bias += d; n++;
        }
        return n == 0 ? (0.0, 0.0, 0) : (sq / n, bias / n, n);
    }

    /// <summary>
    /// 🎚️ 메타 탐색 배수(순수 함수) — 과신/후회 압력 → 증폭 · 양호 보정 → 수축 · 표본 부족 → 1.
    /// </summary>
    public static double ComputeExplorationScale(int calibrationSamples, double rmse, double bias, int regretSamples, double meanRegret)
    {
        var over = calibrationSamples >= MinSamples && double.IsFinite(bias) ? Ramp(bias, BiasDeadBand, BiasFull) : 0.0;
        var regret = regretSamples >= MinSamples && double.IsFinite(meanRegret) ? Ramp(meanRegret, RegretDeadBand, RegretFull) : 0.0;
        var pressure = Math.Max(over, regret);
        if (pressure > 0) return 1.0 + (MaxScale - 1.0) * pressure;
        // 수축은 예측 근거(보정 표본)가 있을 때만 — 후회 표본만으로는 '예측이 맞았다'고 말할 수 없다
        if (calibrationSamples >= MinSamples && double.IsFinite(rmse) && rmse <= WellCalibratedRmse)
        {
            var confidence = Math.Clamp((calibrationSamples - (MinSamples - 1)) / (double)(FullConfidenceSamples - (MinSamples - 1)), 0.0, 1.0);
            return 1.0 - (1.0 - MinScale) * confidence;
        }
        return 1.0;
    }

    /// <summary>🔁 회복 부스트(순수 함수) — 잔액 &lt; 임계 → 0 · 이후 선형 · 상한 <see cref="MaxRecoveryBoost"/>.</summary>
    public static double ComputeRecoveryBoost(double outstandingRegret)
    {
        if (!double.IsFinite(outstandingRegret) || outstandingRegret < RecoveryThreshold) return 0.0;
        return MaxRecoveryBoost * Math.Clamp(outstandingRegret / RecoveryFullRegret, 0.0, 1.0);
    }

    /// <summary>🔁 오답 노트 차단 판정 — 라벨 전체 · 접두(<c>hook:</c>/<c>look:</c>) 제외 값 · 조합 키의 훅/룩 어느 하나라도 차단이면 true.</summary>
    public static bool IsBlockedLabel(string label, IReadOnlySet<string>? blocked)
    {
        if (blocked is null || blocked.Count == 0 || string.IsNullOrWhiteSpace(label)) return false;
        if (blocked.Contains(label)) return true;
        if (PlutoPairAttribution.TrySplitPair(label, out var hook, out var look)) return blocked.Contains(hook) || blocked.Contains(look);
        var colon = label.IndexOf(':');
        return colon > 0 && colon < label.Length - 1 && blocked.Contains(label[(colon + 1)..]);
    }

    private static double Ramp(double v, double deadBand, double full) => Math.Clamp((v - deadBand) / (full - deadBand), 0.0, 1.0);
    private static double Clamp01(double v) => Math.Clamp(v, 0.0, 1.0);

    /// <summary>🎯 보정 상태 1줄(결정론 한국어).</summary>
    public static string BuildCalibrationLabel(PlutoCalibrationStatus? s)
    {
        if (s is null || s.CalibrationSamples == 0) return "기록 없음 · 예측 " + MinSamples.ToString(Inv) + "건부터 보정";
        var verdict = s.CalibrationSamples < MinSamples ? "수집 중" : s.IsOverconfident ? "과신" : s.Bias < -BiasDeadBand ? "과소 예측" : s.Rmse <= WellCalibratedRmse ? "양호" : "편차 큼";
        return verdict + " · 정확도 " + s.AccuracyPct.ToString("0.#", Inv) + "% · RMSE " + s.Rmse.ToString("0.000", Inv) + " · Brier " + s.BrierScore.ToString("0.000", Inv)
            + " · 편향 " + (s.Bias >= 0 ? "+" : "−") + Math.Abs(s.Bias).ToString("0.000", Inv) + " · n=" + s.CalibrationSamples.ToString(Inv)
            + " · 탐색 배수 ×" + s.ExplorationScale.ToString("0.00", Inv) + (s.IsAmplifying ? "(증폭)" : s.IsTightening ? "(활용 집중)" : "");
    }

    /// <summary>💸 반사실 후회 상태 1줄(결정론 한국어).</summary>
    public static string BuildRegretLabel(PlutoCalibrationStatus? s)
    {
        if (s is null || s.RegretSamples == 0) return "기록 없음";
        var label = "평균 " + s.MeanRegret.ToString("0.000", Inv) + " · 누적 " + s.CumulativeRegret.ToString("0.00", Inv) + " · n=" + s.RegretSamples.ToString(Inv)
            + (s.MeanRegret > RegretDeadBand && s.RegretSamples >= MinSamples ? " · 상승(탐색 증폭)" : " · 낮음");
        if (s.TopRegretAlternative is { } top && top.CumulativeRegret > 0)
            label += top.IsResolved
                ? " · 미해소 대안 없음(최근 최대: " + top.Label + " · 누적 " + top.CumulativeRegret.ToString("0.00", Inv) + " · 검증/감쇠 완료)"
                : " · 가장 아쉬운 대안: " + top.Label + "(" + top.CumulativeRegret.ToString("0.00", Inv) + " · " + top.Count.ToString(Inv) + "회)";
        return label;
    }

    // ── 💾 상태 ──

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loaded) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await EnsureLoadedLockedAsync(ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task EnsureLoadedLockedAsync(CancellationToken ct)
    {
        if (_loaded) return;
        var (data, status) = await LoadSnapshotAsync(ct).ConfigureAwait(false);
        _readOnly = status == PlutoLoadStatus.Unreadable;
        lock (_sync) ReplaceStateLocked(data);
        _loaded = true;
    }

    // ── ♻️ Step 20 — 실행 중 재적재 ──

    public string HubFileName => FileName;

    /// <summary>
    /// ♻️ 게이트 안에서 대기 해결 승격 → 본 파일 파싱 → 원자 교체 → <c>.bak</c> 를 새 상태로 갱신.
    /// <c>.bak</c> 갱신 이유: 그대로 두면 이후 본 파일이 깨졌을 때 '병합 이전' 정상본으로 조용히 되돌아가 병합된 다른 PC 의 보정 이력이 사라진다.
    /// 본 파일 손상 · 부재 · 잠김 · 읽기 전용 세션 → 라이브 상태 유지 + false(<c>.bak</c> 폴백은 최초 적재 전용).
    /// </summary>
    public async Task<bool> ReloadFromDiskAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0) return false;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_readOnly) return false;
            PlutoHotReload.PromotePending(_storage, FileName);
            var (data, status) = await PlutoJsonStore.TryLoadAsync<Snapshot>(StoragePath, "Pluto.MetaCalibration.Reload", ct).ConfigureAwait(false);
            if (status != PlutoLoadStatus.Ok || data is null) return false;
            string json;
            lock (_sync)
            {
                ReplaceStateLocked(data);
                json = JsonSerializer.Serialize(new Snapshot
                {
                    CumulativeRegret = _cumulativeRegret, TotalRecords = _totalRecords, Window = new List<RecordDto>(_window),
                    Attributions = new Dictionary<string, AttributionDto>(_attributions, StringComparer.Ordinal),
                }, JsonOpts);
            }
            _loaded = true;
            if (_storage is not null) await _storage.WriteAllTextAtomicAsync(FileName + BackupSuffix, json, CancellationToken.None).ConfigureAwait(false);
            else await PlutoJsonStore.WriteAtomicAsync(_path + BackupSuffix, json, "Pluto.MetaCalibration.Backup", CancellationToken.None).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        RaiseChanged();
        return true;
    }

    /// <summary>🛡️ 본 파일 적재 — 손상/빈 본문이면 <c>.bak</c> 마지막 정상본으로 복구(본 파일은 <c>.corrupt</c> 격리 유지).</summary>
    private async Task<(Snapshot? Data, PlutoLoadStatus Status)> LoadSnapshotAsync(CancellationToken ct)
    {
        var (data, status) = await PlutoJsonStore.TryLoadAsync<Snapshot>(StoragePath, "Pluto.MetaCalibration", ct).ConfigureAwait(false);
        if (status == PlutoLoadStatus.Corrupt || (status == PlutoLoadStatus.Ok && data is null))
        {
            var (backup, backupStatus) = await PlutoJsonStore.TryLoadAsync<Snapshot>(BackupPath, "Pluto.MetaCalibration.Backup", ct).ConfigureAwait(false);
            if (backupStatus == PlutoLoadStatus.Ok && backup is not null)
            {
                _recoveredFromBackup = true;
                return (backup, PlutoLoadStatus.Ok);
            }
        }
        return (data, status);
    }

    /// <summary>🛡️ 읽기 전용 세션 자가 치유 — 잠금이 풀렸으면 디스크 상태를 적재하고 세션 중 관측을 그 위에 재생(호출자가 <c>_gate</c> 보유).</summary>
    private async Task TryHealReadOnlyLockedAsync(CancellationToken ct)
    {
        var (data, status) = await LoadSnapshotAsync(ct).ConfigureAwait(false);
        if (status == PlutoLoadStatus.Unreadable) return;   // 아직 잠김 → 메모리 전용 유지
        lock (_sync)
        {
            ReplaceStateLocked(data);
            for (var i = 0; i < _readOnlyPending.Count; i++) ApplyLocked(_readOnlyPending[i].Obs, _readOnlyPending[i].Utc);
            _readOnlyPending.Clear();
        }
        _readOnly = false;
        Interlocked.Increment(ref _healedSessions);
    }

    /// <summary>상태 교체(호출자가 <c>_sync</c> 보유).</summary>
    private void ReplaceStateLocked(Snapshot? data)
    {
        {
            _window.Clear();
            _attributions.Clear();
            _cumulativeRegret = 0;
            _totalRecords = 0;
            if (data is not null)
            {
                if (data.Window is { } w)
                {
                    var start = Math.Max(0, w.Count - WindowCapacity);
                    for (var i = start; i < w.Count; i++)
                    {
                        var r = w[i];
                        if (r is null || !double.IsFinite(r.R)) continue;
                        _window.Add(new RecordDto { P = Finite(r.P), R = Clamp01(r.R), A = Finite(r.A), Utc = r.Utc, Src = Clip(r.Src) });
                    }
                }
                if (data.Attributions is { } attrs)
                    foreach (var kv in attrs)
                    {
                        if (_attributions.Count >= MaxAttributions) break;
                        if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value is null || !double.IsFinite(kv.Value.Regret) || kv.Value.Regret < 0) continue;
                        var bal = kv.Value.Recovery is { } rv && double.IsFinite(rv) && rv >= 0 ? rv : kv.Value.Regret;   // Step 14 파일 → 누적 후회를 잔액으로
                        _attributions[Clip(kv.Key)!] = new AttributionDto { Regret = kv.Value.Regret, Count = Math.Max(0, kv.Value.Count), Recovery = bal, Utc = kv.Value.Utc };
                    }
                _cumulativeRegret = double.IsFinite(data.CumulativeRegret) && data.CumulativeRegret > 0 ? data.CumulativeRegret : 0.0;
                _totalRecords = Math.Max(Math.Max(0, data.TotalRecords), _window.Count);
            }
        }
    }

    private static double? Finite(double? v) => v is { } x && double.IsFinite(x) ? Clamp01(x) : null;

    private static string? Clip(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var v = s.Trim();
        return v.Length > MaxLabelLength ? v[..MaxLabelLength] : v;
    }

    public async Task<PlutoCalibrationStatus> RecordOutcomeAsync(PlutoCalibrationObservation observation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ct.ThrowIfCancellationRequested();
        var auditable = observation.HasPrediction || observation.HasAlternative;
        if (!auditable && observation.ChosenLabels is not { Count: > 0 }) return GetStatus();   // 감사할 근거 없음
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedLockedAsync(ct).ConfigureAwait(false);
            if (_readOnly) await TryHealReadOnlyLockedAsync(ct).ConfigureAwait(false);
            string? json = null;
            lock (_sync)
            {
                var now = Now();
                if (ApplyLocked(observation, now))
                {
                    if (_readOnly)
                    {
                        _readOnlyPending.Add((observation, now));
                        if (_readOnlyPending.Count > WindowCapacity) _readOnlyPending.RemoveAt(0);
                    }
                    json = JsonSerializer.Serialize(new Snapshot
                {
                    CumulativeRegret = _cumulativeRegret, TotalRecords = _totalRecords, Window = new List<RecordDto>(_window),
                    Attributions = new Dictionary<string, AttributionDto>(_attributions, StringComparer.Ordinal),
                }, JsonOpts);
                }
            }
            if (json is null) return GetStatus();   // 보정 근거도 · 소거할 회복 사전도 없음 → 무변경
            if (!_readOnly)
            {
                // 호출자 취소가 기록 도중 끼어들어도 상태는 이미 메모리에 반영됨 → 영속은 끝까지 수행
                // 🛡️ 본 파일 기록 성공 뒤에만 마지막 정상본(.bak) 갱신 — 본 파일이 깨져도 직전 정상 상태가 남는다
                if (_storage is not null)
                {
                    if (await _storage.WriteAllTextAtomicAsync(FileName, json, CancellationToken.None).ConfigureAwait(false))
                        await _storage.WriteAllTextAtomicAsync(FileName + BackupSuffix, json, CancellationToken.None).ConfigureAwait(false);
                }
                else if (await PlutoJsonStore.WriteAtomicAsync(_path, json, "Pluto.MetaCalibration", CancellationToken.None).ConfigureAwait(false))
                    await PlutoJsonStore.WriteAtomicAsync(_path + BackupSuffix, json, "Pluto.MetaCalibration.Backup", CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
        RaiseChanged();
        return GetStatus();
    }

    /// <summary>
    /// 관측 1건을 메모리 상태에 반영(호출자가 <c>_sync</c> 보유) — ① 선택된 라벨의 회복 사전 소거 ② 보정/후회 창 기록 ③ 후회 귀속 · 잔액 적립.
    /// 반환 false = 아무것도 바뀌지 않음.
    /// </summary>
    private bool ApplyLocked(PlutoCalibrationObservation observation, DateTime now)
    {
        var changed = false;
        if (observation.ChosenLabels is { } chosen)
            for (var i = 0; i < chosen.Count; i++)
                if (Clip(chosen[i]) is { } tested && _attributions.TryGetValue(tested, out var ta) && (ta.Recovery ?? ta.Regret) > 0)
                {
                    ta.Recovery = 0.0;   // 🔁 실측 검증 완료 → 리허설 종료(누적 귀속은 리포트용으로 보존)
                    ta.Utc = now;
                    changed = true;
                }
        if (!observation.HasPrediction && !observation.HasAlternative) return changed;
        var rec = new RecordDto
        {
            P = observation.HasPrediction ? Clamp01(observation.PredictedExpectedScore) : null,
            R = Clamp01(observation.RealizedScore),
            A = observation.HasAlternative ? Clamp01(observation.BestAlternativeExpectedScore) : null,
            Utc = now,
            Src = Clip(observation.Source),
        };
        _window.Add(rec);
        if (_window.Count > WindowCapacity) _window.RemoveRange(0, _window.Count - WindowCapacity);
        _totalRecords = _totalRecords == int.MaxValue ? int.MaxValue : _totalRecords + 1;
        if (rec.A is { } alt)
        {
            var regret = Math.Max(0.0, alt - rec.R);
            _cumulativeRegret += regret;
            if (regret > 0 && Clip(observation.BestAlternativeLabel) is { } label) Attribute(label, regret, !observation.AlternativeMeasured, now);
        }
        return true;
    }

    private static double Outstanding(AttributionDto a, DateTime now)
        => Math.Max(0.0, a.Recovery ?? a.Regret) * PlutoLearningService.HalfLifeFactor(a.Utc, now, RecoveryHalfLifeDays);

    public IReadOnlyList<PlutoRegretRecoveryBoost> GetRegretRecoveryBoosts(IReadOnlySet<string>? blockedTokens = null)
    {
        var list = new List<PlutoRegretRecoveryBoost>();
        var now = Now();
        lock (_sync)
            foreach (var kv in _attributions)
            {
                var outstanding = Outstanding(kv.Value, now);
                var boost = ComputeRecoveryBoost(outstanding);
                if (boost <= 0 || IsBlockedLabel(kv.Key, blockedTokens)) continue;
                list.Add(new PlutoRegretRecoveryBoost(kv.Key, boost, outstanding, kv.Value.Count));
            }
        list.Sort(static (a, b) =>
        {
            var c = b.Boost.CompareTo(a.Boost);
            if (c == 0) c = b.OutstandingRegret.CompareTo(a.OutstandingRegret);
            return c != 0 ? c : string.CompareOrdinal(a.Label, b.Label);
        });
        return list;
    }

    /// <summary>대안별 후회 누적 — 용량 초과 시 누적 후회 최소(동률 → 키 사전순) 항목 축출(결정론). <paramref name="credit"/> = 회복 잔액 적립 여부.</summary>
    private void Attribute(string label, double regret, bool credit, DateTime now)
    {
        if (!_attributions.TryGetValue(label, out var a))
        {
            if (_attributions.Count >= MaxAttributions)
            {
                string? victim = null; var min = double.MaxValue;
                foreach (var kv in _attributions)
                    if (kv.Value.Regret < min || (kv.Value.Regret == min && string.CompareOrdinal(kv.Key, victim) < 0)) { min = kv.Value.Regret; victim = kv.Key; }
                if (victim is not null) _attributions.Remove(victim);
            }
            a = new AttributionDto { Recovery = 0.0 };
            _attributions[label] = a;
        }
        if (credit)
        {
            a.Recovery = Outstanding(a, now) + regret;   // 감쇠 후 재기준 + 이번 후회
            a.Utc = now;
        }
        else a.Recovery ??= a.Regret;                   // Step 14 항목의 잔액 기준 고정(이후 Regret 증가분이 잔액으로 새지 않게)
        a.Regret += regret;
        a.Count = a.Count == int.MaxValue ? int.MaxValue : a.Count + 1;
    }

    public PlutoCalibrationStatus GetStatus()
    {
        lock (_sync)
        {
            if (_window.Count == 0 && _totalRecords == 0) return PlutoCalibrationStatus.Empty;
            double sq = 0, bias = 0, regretSum = 0; int n = 0, rn = 0;
            for (var i = 0; i < _window.Count; i++)
            {
                var r = _window[i];
                if (r.P is { } p) { var d = p - r.R; sq += d * d; bias += d; n++; }
                if (r.A is { } a) { regretSum += Math.Max(0.0, a - r.R); rn++; }
            }
            var brier = n > 0 ? sq / n : 0.0;
            var rmse = Math.Sqrt(brier);
            var meanBias = n > 0 ? bias / n : 0.0;
            var meanRegret = rn > 0 ? regretSum / rn : 0.0;
            // 💸 '가장 아쉬운 대안' = 미해소 잔액 최대(실측 검증 · 감쇠로 잔액이 사라진 대안이 리포트에 영구 고착되지 않게) →
            //    동률/전부 해소 시 누적 후회 최대 → 키 사전순. 잔액 비교는 표기 정밀도(0.01) — 분 단위 감쇠 차이로 순위가 뒤집히지 않게.
            PlutoRegretAttribution? top = null;
            var now = Now();
            var topRank = 0.0;
            foreach (var kv in _attributions)
            {
                var outstanding = Outstanding(kv.Value, now);
                var rank = Math.Round(outstanding, 2, MidpointRounding.AwayFromZero);
                if (top is null || rank > topRank
                    || (rank == topRank && (kv.Value.Regret > top.CumulativeRegret
                        || (kv.Value.Regret == top.CumulativeRegret && string.CompareOrdinal(kv.Key, top.Label) < 0))))
                {
                    top = new PlutoRegretAttribution(kv.Key, kv.Value.Regret, kv.Value.Count) { OutstandingRegret = outstanding };
                    topRank = rank;
                }
            }
            return new PlutoCalibrationStatus(n, brier, rmse, meanBias, rn, meanRegret, _cumulativeRegret, _totalRecords,
                ComputeExplorationScale(n, rmse, meanBias, rn, meanRegret)) { TopRegretAlternative = top };
        }
    }

    public double GetCalibratedExplorationScale() => GetStatus().ExplorationScale;

    private void RaiseChanged()
    {
        var h = Changed;
        if (h is null) return;
        foreach (Action a in h.GetInvocationList())
        {
            try { a(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.MetaCalibration] Changed handler: " + ex.Message); }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Changed = null;
        _gate.Dispose();
    }
}
