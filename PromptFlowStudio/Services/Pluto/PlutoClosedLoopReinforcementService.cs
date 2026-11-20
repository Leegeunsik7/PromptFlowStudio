using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>📈 게시 후 영상 성과 텔레메트리(퍼센트 0~100) — 크리에이터가 YouTube/Instagram 지표를 직접 입력.</summary>
public sealed record PlutoVideoPerformanceTelemetry(string VideoId, string Platform, string HookStyle, string ColorLookId, string BgmMood, IReadOnlyList<string> StyleTags,
    double ThreeSecondHookRetentionPct, double AverageCompletionPct, double ClickThroughRatePct, DateTimeOffset MeasuredAtUtc)
{
    /// <summary>🧭 콘텐츠 도메인(예: <c>BabyStudio</c> · <c>GeneralCinematic</c>) — 지정 시 <c>domain_hook:{Domain}+{HookStyle}</c> 상호작용 키 추적(미지정 → 생략).</summary>
    public string? Domain { get; init; }
    /// <summary>🎞️ 렌더 포맷(예: <c>Ambient15s</c> · <c>Montage</c> · <c>Series3</c>) — <see cref="Domain"/>과 함께 컨텍스트 기준선 키 <c>ctx:{Domain}|{RenderMode}</c>(미지정 → <c>Any</c>).</summary>
    public string? RenderMode { get; init; }
}

/// <summary>📈 폐루프 강화 결과 — 돌파 성공(패턴 부스트 + XP) · 오답 노트 벌점 기록 여부 · XP 증분 · 패턴 배수 · 요약.</summary>
public sealed record PlutoReinforcementOutcome(bool IsBreakoutSuccess, bool RecordedMistakePenalty, int XpDelta, double PatternWeightMultiplier, string SummaryMessage)
{
    /// <summary>같은 영상 중복 반영 차단(alreadyRegistered).</summary>
    public bool AlreadyRegistered { get; init; }
    /// <summary>오답 노트 벌점 강도(0 = 미기록).</summary>
    public double PenaltySeverity { get; init; }
    /// <summary>🩺 벌점 실패 차원(최대 정규화 결손 · 벌점 없음 → null).</summary>
    public string? FailureDimension { get; init; }
    /// <summary>🛡️ 이번 벌점만으로 오답 노트 차단 가드 임계(≥ <see cref="PlutoMistakeNotebookService.BlockThreshold"/>) 도달.</summary>
    public bool GuardActivated => PenaltySeverity >= PlutoMistakeNotebookService.BlockThreshold;
    /// <summary>🎯 2-way 귀속 — 훅이 다른 조합/도메인에서 성공 이력이 있어 벌점을 이 조합 키(<c>pair:</c> · <c>domain_hook:</c>)로 격리(전역 훅 벌점 면제 · 없음 → null).</summary>
    public string? IsolatedPairKey { get; init; }
    /// <summary>📐 컨텍스트 관측(기준선 · 잔차 알파 · z · 드리프트) — 학습 메모리 미연결/중복 반영 → null.</summary>
    public PlutoContextualObservation? Context { get; init; }
    /// <summary>📐 포맷 기준선 대비 잔차 알파(합성 점수 0~1 · 없음 → 0).</summary>
    public double ResidualAlpha => Context?.ResidualAlpha ?? 0.0;
    /// <summary>📐 잔차 알파가 보상/벌점을 실제로 바꿈(돌파 부스트 감쇄 · 알파 부스트 · 벌점 면제).</summary>
    public bool ContextualAlphaApplied { get; init; }
    /// <summary>📐 절대 기준 미달이지만 포맷 기준선을 상회해 오답 노트 벌점을 면제함.</summary>
    public bool ContextualPenaltyWaived { get; init; }
    /// <summary>🌊 이번 반영에서 레짐 드리프트 감지(탐색 서지 발동).</summary>
    public bool RegimeDriftDetected => Context?.DriftDetected ?? false;
    /// <summary>🎯 이번 반영의 메타 보정 관측(예측 · 실현 · 최선 대안) — 메타 보정 미연결/근거 없음 → null.</summary>
    public PlutoCalibrationObservation? CalibrationObservation { get; init; }
    /// <summary>💸 이번 반영의 반사실 후회(없음 → 0).</summary>
    public double CounterfactualRegret => CalibrationObservation?.Regret ?? 0.0;
}

/// <summary>
/// ⚖️ A/B 실험 차등 강화 요청 — <see cref="Winner"/>/<see cref="Loser"/> 텔레메트리는 변형 간 <b>차이 나는 특성만</b> 담는다(공통 특성은 빈 문자열).
/// <see cref="LiftPct"/> = 승자 합성 점수의 패자 대비 상승률(%) · <see cref="IsTie"/> → 변경 없음(장부만 기록).
/// </summary>
public sealed record PlutoExperimentReinforcementRequest(string ExperimentId, PlutoVideoPerformanceTelemetry Winner, PlutoVideoPerformanceTelemetry Loser, double LiftPct, bool IsTie)
{
    /// <summary>🩺 패자 실패 차원(장부가 A/B 기준 42 % · 30 % · 1.5 % 결손 최대치로 분류) — null → <c>HookRetention</c>(하위 호환).</summary>
    public string? LoserFailureDimension { get; init; }
    /// <summary>📐 패자 최대 정규화 결손(0~1 · 장부 산출) — null → <see cref="Loser"/> 지표로 A/B 기준 재계산.</summary>
    public double? LoserSteepestDeficit { get; init; }
    /// <summary>🧬 실험 컨텍스트(테마 분류 도메인) — 지정 시 차이 나는 BGM · 태그의 짝 상대 컨텍스트 알파를 기록(미지정 → 생략 · 기존 동작).</summary>
    public string? ContextDomain { get; init; }
    /// <summary>🧬 실험 렌더 포맷(미지정 → <see cref="PlutoContextAlphaBackfill.DefaultRenderMode"/>).</summary>
    public string? ContextRenderMode { get; init; }
}

/// <summary>📈 Pluto 폐루프 성과 강화 — 게시 후 지표 → 학습 메모리(패턴 가중치 · XP) / 오답 노트(약한 훅 회피).</summary>
public interface IPlutoClosedLoopReinforcementService
{
    /// <summary>성과 반영(동일 플랫폼+영상 ID 재반영 차단 · 실패 시 [부분 구제] 결과 반환 · 취소만 전파).</summary>
    Task<PlutoReinforcementOutcome> IngestPerformanceAsync(PlutoVideoPerformanceTelemetry telemetry, CancellationToken ct = default);

    /// <summary>
    /// ⚖️ A/B 실험 차등 강화(같은 실험 ID 재반영 차단) — 승자 차등 특성 부스트(상승률·돌파 강도 비례 ×1.15~1.35 + XP) ·
    /// 패자는 절대 실패(훅 42 % · 완주 30 % · CTR 1.5 % 최대 정규화 결손 &gt; 0)일 때만 결손 비례 오답 노트 벌점(근소 패배 보호).
    /// 승자 자신이 절대 실패면 부스트 생략. 실패 시 [부분 구제] · 취소만 전파.
    /// </summary>
    Task<PlutoReinforcementOutcome> IngestExperimentOutcomeAsync(PlutoExperimentReinforcementRequest request, CancellationToken ct = default);

    /// <summary>마지막 반영 결과(없음 → null).</summary>
    PlutoReinforcementOutcome? LastOutcome { get; }

    /// <summary>반영 통지(백그라운드 스레드 가능).</summary>
    event Action? Changed;
}

/// <summary>
/// 📈 <see cref="IPlutoClosedLoopReinforcementService"/> 구현.
///   ▸ 돌파: 3초 유지율 ≥ 65 % · CTR ≥ 7.5 % · 완주율 ≥ 55 % 중 하나 → 훅/스타일/LUT/BGM 패턴 ×1.15~1.35(상한 2.5) + XP +15~35 · 오답 노트 벌점 상쇄.
///   ▸ 부진: 3초 유지율 &lt; 42 % · 완주율 &lt; 30 % · CTR &lt; 2.2 % 중 최대 정규화 결손 &gt; 0 → 훅 스타일 + 스타일 태그를 결손 비례 벌점(1.0~2.0 · 완주/CTR ×1.25)으로 오답 노트 기록(21일 반감기 · 1.8 차단).
///   ▸ 돌파 판정 우선(혼합 신호에서 승리 패턴을 벌하지 않음). 반영 장부 <c>pluto_reinforcement_ledger.json</c>(원자 기록)로 중복 반영 차단.
///   ▸ 🔗 2-way 귀속: 훅×룩(<c>pair:</c>) · 도메인×훅(<c>domain_hook:</c>) 조합 키를 단일 특성과 함께 강화/시행 기록 ·
///     부진 훅이 다른 조합/도메인에서 성공 이력이 있으면 벌점을 조합 키에만 격리(훅 전역 오염 방지).
///   ▸ 🎰 모든 반영(돌파 · 부진 · 평균)이 패턴 시행 수를 +1 → 랭커 UCB1 탐색 항의 표본 수.
///   ▸ 📐 컨텍스트 잔차 알파: 성립한 (Domain, RenderMode) 기준선 대비 z 로 돌파 부스트를 감쇄(포맷 편승 → 보상 ↓) ·
///     절대 기준 미달/평균이어도 포맷을 z ≥ 1 상회하면 벌점 면제 + 알파 부스트(×1.075~1.15). 미성립 컨텍스트는 기존 동작과 동일.
///   ▸ 🌊 예측 잔차(기대 − 관측)를 Page-Hinkley 검정에 공급 → 급격한 레짐 전환 시 탐색 서지.
///   ▸ A/B 차등 강화는 두 변형이 같은 포맷을 공유(설계상 컨텍스트 통제)하므로 잔차 보정 대상이 아니다.
/// </summary>
public sealed class PlutoClosedLoopReinforcementService : IPlutoClosedLoopReinforcementService, IDisposable
{
    public const string LedgerFileName = "pluto_reinforcement_ledger.json";
    public const string XpSourceKey = "xp:closed-loop";
    public const double BreakoutHookPct = 65.0, BreakoutCtrPct = 7.5, BreakoutCompletionPct = 55.0;
    public const double PoorHookPct = 42.0, PoorCtrPct = 2.2;
    /// <summary>🩺 완주율 진단 기준(%) — 부진 판정 영상의 실패 차원 분류에만 사용.</summary>
    public const double PoorCompletionPct = 30.0;
    /// <summary>📐 A/B 패자 공용 결손 기준(%) — 3초 유지율 · 완주율 · CTR.</summary>
    public const double AbHookThresholdPct = 42.0, AbCompletionThresholdPct = 30.0, AbCtrThresholdPct = 1.5;
    /// <summary>📐 완주/CTR 결손 증폭 — 결손 ≥ 64 %(예: 완주 10 % · CTR 0.4 %)면 단일 벌점으로 차단 가드(1.8) 도달.</summary>
    public const double NonHookDeficitGain = 1.25;
    public const double MinBoost = 1.15, MaxBoost = 1.35;
    public const int MinXp = 15, MaxXp = 35;
    public const double MinPenalty = 1.0, MaxPenalty = 2.0;
    public const int LedgerCapacity = 500;
    /// <summary>A/B 상승률 이 값(%) 이상 → 최대 차등 부스트.</summary>
    public const double FullBoostLiftPct = 50.0;
    public const string ExperimentLedgerPrefix = "ab|";
    /// <summary>🎯 '다른 곳에서 성공' 증거 최소 초과 가중치(반감기 감쇠 반영) — 1.15 부스트가 약 22일 뒤 소멸.</summary>
    public const double SuccessEvidenceMargin = 0.05;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly PlutoLearningService? _learning;
    private readonly IPlutoMistakeNotebookService? _notebook;
    private readonly IPlutoStorageManager? _storage;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<string> _ledger = new(32);
    private readonly HashSet<string> _ledgerSet = new(StringComparer.OrdinalIgnoreCase);
    private bool _ledgerLoaded;
    private bool _ledgerReadOnly;   // 🛡️ 장부 읽기 불가(잠금·권한) 세션 — 디스크 장부 덮어쓰기 금지(중복 차단 이력 보호)
    private volatile PlutoReinforcementOutcome? _last;
    private int _disposed;

    public PlutoClosedLoopReinforcementService(PlutoLearningService? learning, IPlutoMistakeNotebookService? notebook, IPlutoStorageManager? storage = null)
    {
        _learning = learning;
        _notebook = notebook;
        _storage = storage;
    }

    /// <summary>🎯 메타 보정 감사(미연결 → 생략 · 기존 동작과 동일).</summary>
    public IPlutoMetaCalibrationService? MetaCalibration { get; init; }

    /// <summary>📺 시리즈 아크 메모리(미연결 → 생략) — 시리즈 편 성과를 편 간 유지율 추세에 귀속.</summary>
    public IPlutoSeriesArcMemoryService? SeriesArc { get; init; }

    /// <summary>📺 시리즈 편 성과 귀속 — 실패 격리([부분 구제] 강화 결과 유지 · 취소만 전파).</summary>
    private async Task ObserveSeriesArcSafeAsync(PlutoVideoPerformanceTelemetry t, CancellationToken ct)
    {
        if (SeriesArc is null) return;
        try { await SeriesArc.RecordEpisodePerformanceAsync(t, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.ClosedLoop] series arc: " + ex.Message); }
    }

    /// <summary>🎯 반사실 예측 스냅샷(관측 반영 이전) — 실패 격리.</summary>
    private PlutoExpectancyForecast ForecastSafe(PlutoVideoPerformanceTelemetry t)
    {
        if (MetaCalibration is null || _learning is null) return PlutoExpectancyForecast.None;
        try { return _learning.ForecastExpectancy(t.Domain, t.RenderMode, t.HookStyle, t.ColorLookId); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.ClosedLoop] forecast: " + ex.Message); return PlutoExpectancyForecast.None; }
    }

    /// <summary>🎯 메타 보정 기록 — 실패 격리([부분 구제] 강화 결과 유지 · 취소만 전파). 근거 없음 → null.</summary>
    private async Task<PlutoCalibrationObservation?> RecordCalibrationSafeAsync(double predicted, double realized, double alternative, string? alternativeLabel, string source, CancellationToken ct,
        PlutoVideoPerformanceTelemetry? chosen = null, bool alternativeMeasured = false)
    {
        if (MetaCalibration is null) return null;
        var obs = new PlutoCalibrationObservation(predicted, realized, alternative)
        {
            BestAlternativeLabel = alternativeLabel, Source = source, AlternativeMeasured = alternativeMeasured,
            ChosenLabels = chosen is null ? null : RehearsalLabels(chosen),
        };
        if (!obs.HasPrediction && !obs.HasAlternative && obs.ChosenLabels is not { Count: > 0 }) return null;
        try { await MetaCalibration.RecordOutcomeAsync(obs, ct).ConfigureAwait(false); return obs; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.ClosedLoop] calibration: " + ex.Message); return null; }
    }

    public event Action? Changed;

    public PlutoReinforcementOutcome? LastOutcome => _last;

    /// <summary>🛡️ 장부 읽기 불가 세션 여부 — true면 이번 세션 반영은 메모리 장부로만 중복 차단하고 디스크 장부는 보존.</summary>
    public bool IsLedgerReadOnly => _ledgerReadOnly;

    /// <summary>📈 판정(순수 함수) — 돌파 강도 0~1 · 결손 강도 0~1.</summary>
    public static (bool Breakout, double Strength, bool Poor, double Deficit) Evaluate(PlutoVideoPerformanceTelemetry t)
    {
        ArgumentNullException.ThrowIfNull(t);
        var hook = Pct(t.ThreeSecondHookRetentionPct);
        var comp = Pct(t.AverageCompletionPct);
        var ctr = Pct(t.ClickThroughRatePct);
        var breakout = hook >= BreakoutHookPct || ctr >= BreakoutCtrPct || comp >= BreakoutCompletionPct;
        var strength = 0.0;
        if (breakout)
        {
            strength = Math.Max(strength, (hook - BreakoutHookPct) / (100.0 - BreakoutHookPct));
            strength = Math.Max(strength, (ctr - BreakoutCtrPct) / BreakoutCtrPct);
            strength = Math.Max(strength, (comp - BreakoutCompletionPct) / (100.0 - BreakoutCompletionPct));
            strength = Math.Clamp(strength, 0.0, 1.0);
        }
        var (steepest, _) = SteepestNormalizedDeficit(hook, comp, ctr, PoorHookPct, PoorCompletionPct, PoorCtrPct);
        var poor = !breakout && steepest > 0;
        return (breakout, strength, poor, poor ? steepest : 0.0);
    }

    /// <summary>
    /// 📐 최대 정규화 결손(순수 함수) — <c>max(0, (기준 − 값) / 기준)</c>를 훅 · 완주 · CTR에 대해 계산해 최대치와 차원 반환
    /// (동률 → 훅 &gt; 완주 &gt; CTR · 결손 없음 → (0, GeneralPenalty)). 기본 기준 = A/B 42 % · 30 % · 1.5 %.
    /// </summary>
    public static (double Deficit, string Dimension) SteepestNormalizedDeficit(double hookPct, double completionPct, double ctrPct,
        double hookThreshold = AbHookThresholdPct, double completionThreshold = AbCompletionThresholdPct, double ctrThreshold = AbCtrThresholdPct)
    {
        var hookDef = NormalizedDeficit(Pct(hookPct), hookThreshold);
        var compDef = NormalizedDeficit(Pct(completionPct), completionThreshold);
        var ctrDef = NormalizedDeficit(Pct(ctrPct), ctrThreshold);
        if (hookDef <= 0 && compDef <= 0 && ctrDef <= 0) return (0.0, PlutoMistakeNotebookService.DimensionGeneral);
        if (hookDef >= compDef && hookDef >= ctrDef) return (hookDef, PlutoMistakeNotebookService.DimensionHookRetention);
        return compDef >= ctrDef ? (compDef, PlutoMistakeNotebookService.DimensionCompletionDrop) : (ctrDef, PlutoMistakeNotebookService.DimensionLowCtr);
    }

    /// <summary>📐 단일 지표 정규화 결손 0~1(기준 ≤ 0 → 0).</summary>
    public static double NormalizedDeficit(double value, double threshold)
        => threshold > 0 && double.IsFinite(value) ? Math.Clamp((threshold - value) / threshold, 0.0, 1.0) : 0.0;

    /// <summary>
    /// 📐 최대 결손 → 오답 노트 벌점 — 훅 결손은 기존 <see cref="PenaltyFor"/>(1.0~2.0) 그대로 ·
    /// 완주/CTR 결손은 ×<see cref="NonHookDeficitGain"/> 비례 증폭(상한 2.0)해 중대 결손이 단일 벌점으로 차단 가드 도달. 결손 0 → 0.
    /// </summary>
    public static double PenaltyForSteepestDeficit(double deficit, string? dimension)
    {
        if (!double.IsFinite(deficit) || deficit <= 0) return 0.0;
        var gain = string.Equals(dimension, PlutoMistakeNotebookService.DimensionCompletionDrop, StringComparison.Ordinal)
            || string.Equals(dimension, PlutoMistakeNotebookService.DimensionLowCtr, StringComparison.Ordinal) ? NonHookDeficitGain : 1.0;
        return PenaltyFor(deficit * gain);
    }

    /// <summary>🩺 실패 차원 한글 라벨(요약 메시지용).</summary>
    public static string DimensionLabelKorean(string? dimension) => dimension switch
    {
        PlutoMistakeNotebookService.DimensionHookRetention => "3초 훅 이탈",
        PlutoMistakeNotebookService.DimensionCompletionDrop => "완주 이탈",
        PlutoMistakeNotebookService.DimensionLowCtr => "저 CTR",
        _ => "일반 부진",
    };

    /// <summary>🩺 실패 차원 분류(순수 함수) — 3초 유지율 · 완주율 · CTR 결손 비율 중 최대(동률 → 훅 &gt; 완주 &gt; CTR) · 결손 없음 → GeneralPenalty.</summary>
    public static string ClassifyFailureDimension(PlutoVideoPerformanceTelemetry t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return SteepestNormalizedDeficit(t.ThreeSecondHookRetentionPct, t.AverageCompletionPct, t.ClickThroughRatePct, PoorHookPct, PoorCompletionPct, PoorCtrPct).Dimension;
    }

    /// <summary>돌파 강도 → 패턴 배수(1.15~1.35).</summary>
    public static double BoostFor(double strength) => MinBoost + (MaxBoost - MinBoost) * Math.Clamp(strength, 0.0, 1.0);

    /// <summary>돌파 강도 → XP(15~35).</summary>
    public static int XpFor(double strength) => MinXp + (int)Math.Round((MaxXp - MinXp) * Math.Clamp(strength, 0.0, 1.0), MidpointRounding.AwayFromZero);

    /// <summary>결손 강도 → 오답 노트 벌점(1.0~2.0).</summary>
    public static double PenaltyFor(double deficit) => MinPenalty + (MaxPenalty - MinPenalty) * Math.Clamp(deficit, 0.0, 1.0);

    /// <summary>강화 대상 패턴 토큰(<c>hook:</c> · <c>look:</c> · <c>bgm:</c> · <c>style:</c>) — 공백/중복 제외.</summary>
    public static IReadOnlyList<string> PatternTokens(PlutoVideoPerformanceTelemetry t)
    {
        var list = new List<string>(4 + (t.StyleTags?.Count ?? 0));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Add(list, seen, "hook:", t.HookStyle);
        Add(list, seen, "look:", t.ColorLookId);
        Add(list, seen, "bgm:", t.BgmMood);
        if (t.StyleTags is not null) for (var i = 0; i < t.StyleTags.Count; i++) Add(list, seen, "style:", t.StyleTags[i]);
        return list;
    }

    /// <summary>⚖️ 다목적(훅 유지 · 완주 · CTR) 성과만 귀속하는 모달리티 토큰 — <c>bgm:</c> · <c>style:</c>(기대치 · 드리프트 검정에는 불참).</summary>
    public static IReadOnlyList<string> ModalityObjectiveTokens(PlutoVideoPerformanceTelemetry t)
    {
        ArgumentNullException.ThrowIfNull(t);
        var list = new List<string>(1 + (t.StyleTags?.Count ?? 0));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Add(list, seen, "bgm:", t.BgmMood);
        if (t.StyleTags is not null) for (var i = 0; i < t.StyleTags.Count; i++) Add(list, seen, "style:", t.StyleTags[i]);
        return list;
    }

    /// <summary>🔁 후회 리허설 소거 라벨 — 이번 영상이 실제로 검증한 <c>hook:</c> · <c>look:</c> · <c>pair:</c> 구성.</summary>
    public static IReadOnlyList<string> RehearsalLabels(PlutoVideoPerformanceTelemetry t)
    {
        ArgumentNullException.ThrowIfNull(t);
        var list = new List<string>(3);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Add(list, seen, "hook:", t.HookStyle);
        Add(list, seen, "look:", t.ColorLookId);
        if (PlutoPairAttribution.PairKey(t.HookStyle, t.ColorLookId) is { } pair && seen.Add(pair)) list.Add(pair);
        return list;
    }

    /// <summary>⚖️ 다목적 성과(A/B 변형) 기록 — 실패 격리([부분 구제] 판정 결과 유지 · 취소만 전파).</summary>
    private async Task RecordObjectivesSafeAsync(PlutoVideoPerformanceTelemetry t, CancellationToken ct)
    {
        if (_learning is null) return;
        try
        {
            var tokens = PatternTokens(t);   // ⚖️ 전 모달리티(hook · look · bgm · style) — A/B 변형은 차이 나는 특성만 담겨 있다
            if (tokens.Count > 0)
                await _learning.RecordPatternObjectivesAsync(tokens, PlutoObjectiveVector.FromPercent(t.ThreeSecondHookRetentionPct, t.AverageCompletionPct, t.ClickThroughRatePct), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.ClosedLoop] objectives: " + ex.Message); }
    }

    /// <summary>🧬 A/B 짝 컨텍스트 알파(BGM · 태그) — 실패 격리([부분 구제] 판정 · 강화 결과 유지 · 취소만 전파).</summary>
    private async Task RecordExperimentContextAlphaSafeAsync(PlutoExperimentReinforcementRequest r, CancellationToken ct)
    {
        if (_learning is null || string.IsNullOrWhiteSpace(r.ContextDomain)) return;
        try
        {
            var samples = PlutoContextAlphaBackfill.PairSamples(r.ExperimentId, r.ContextDomain, string.IsNullOrWhiteSpace(r.ContextRenderMode) ? PlutoContextAlphaBackfill.DefaultRenderMode : r.ContextRenderMode,
                PlutoContextualBaseline.CompositeScore(r.Winner), PlutoContextualBaseline.CompositeScore(r.Loser), ModalityObjectiveTokens(r.Winner), ModalityObjectiveTokens(r.Loser), DateTimeOffset.UtcNow);
            if (samples.Count > 0) await _learning.RecordContextAlphaSamplesAsync(samples, historical: false, null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.ClosedLoop] context alpha: " + ex.Message); }
    }

    /// <summary>오답 노트 기록 토큰 — 훅 스타일 + 스타일 태그(원문 · 중복 제외).</summary>
    public static IReadOnlyList<string> MistakeTokens(PlutoVideoPerformanceTelemetry t)
    {
        var list = new List<string>(1 + (t.StyleTags?.Count ?? 0));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Add(list, seen, "", t.HookStyle);
        if (t.StyleTags is not null) for (var i = 0; i < t.StyleTags.Count; i++) Add(list, seen, "", t.StyleTags[i]);
        return list;
    }

    /// <summary>🔗 2-way 상호작용 키 — <c>pair:{Hook}+{Look}</c> · <c>domain_hook:{Domain}+{Hook}</c>(구성 요소 누락 → 생략).</summary>
    public static IReadOnlyList<string> InteractionTokens(PlutoVideoPerformanceTelemetry t)
    {
        ArgumentNullException.ThrowIfNull(t);
        var list = new List<string>(2);
        if (PlutoPairAttribution.PairKey(t.HookStyle, t.ColorLookId) is { } pair) list.Add(pair);
        if (PlutoPairAttribution.DomainHookKey(t.Domain, t.HookStyle) is { } dh) list.Add(dh);
        return list;
    }

    private static List<string> Concat(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var list = new List<string>(a.Count + b.Count);
        for (var i = 0; i < a.Count; i++) list.Add(a[i]);
        for (var i = 0; i < b.Count; i++) list.Add(b[i]);
        return list;
    }

    /// <summary>
    /// 🎯 부진 귀속(2-way) — 훅이 ① 다른 룩과의 조합 ② 다른 도메인 ③ (조합 이력 없는) 단일 특성 강화로 성공 증거
    /// (감쇠 반영 가중치 &gt; 1 + <see cref="SuccessEvidenceMargin"/>)가 있으면 벌점 대상을 조합 키로 교체해 훅 전역 오염을 막는다.
    /// 증거 없음 · 조합 키 구성 불가 · 학습 메모리 미연결 → 기존 전역 토큰(<see cref="MistakeTokens"/>). 스타일 태그는 항상 전역 유지.
    /// </summary>
    public (IReadOnlyList<string> Tokens, string? IsolatedKey) AttributeMistake(PlutoVideoPerformanceTelemetry t)
    {
        ArgumentNullException.ThrowIfNull(t);
        var global = MistakeTokens(t);
        if (_learning is null || string.IsNullOrWhiteSpace(t.HookStyle)) return (global, null);
        var pairKey = PlutoPairAttribution.PairKey(t.HookStyle, t.ColorLookId);
        var domainKey = PlutoPairAttribution.DomainHookKey(t.Domain, t.HookStyle);
        if (pairKey is null && domainKey is null) return (global, null);

        bool otherLook = false, otherDomain = false, marginal;
        try
        {
            const double threshold = PlutoLearningService.PatternBaseWeight + SuccessEvidenceMargin;
            var hookPart = PlutoPairAttribution.HookPart(t.HookStyle);
            var lookPart = PlutoPairAttribution.Part(t.ColorLookId, PlutoPairAttribution.MaxSidePartLength) ?? "";
            var domainPart = PlutoPairAttribution.Part(t.Domain, PlutoPairAttribution.MaxSidePartLength) ?? "";

            var pairs = _learning.GetPatternWeights(PlutoPairAttribution.PairPrefix + hookPart + PlutoPairAttribution.Separator);
            for (var i = 0; i < pairs.Count; i++)
            {
                var look = pairs[i].Key;
                if (look.IndexOf(PlutoPairAttribution.Separator) >= 0) continue;   // 더 긴 훅('A+B')의 키 — 이 훅의 조합 아님
                if (pairs[i].Value > threshold && !string.Equals(look, lookPart, StringComparison.Ordinal)) { otherLook = true; break; }
            }
            var domains = _learning.GetPatternWeights(PlutoPairAttribution.DomainHookPrefix);
            for (var i = 0; i < domains.Count; i++)
            {
                if (!(domains[i].Value > threshold)) continue;
                if (!PlutoPairAttribution.TrySplitDomainHook(PlutoPairAttribution.DomainHookPrefix + domains[i].Key, out var d, out var h)) continue;
                if (string.Equals(h, hookPart, StringComparison.Ordinal) && !string.Equals(d, domainPart, StringComparison.OrdinalIgnoreCase)) { otherDomain = true; break; }
            }
            var hookRaw = t.HookStyle.Trim();
            if (hookRaw.Length > PlutoMistakeNotebookService.MaxTokenLength) hookRaw = hookRaw[..PlutoMistakeNotebookService.MaxTokenLength];
            var ownPair = pairKey is null ? PlutoLearningService.PatternBaseWeight : _learning.GetPatternWeight(pairKey);
            var ownDomain = domainKey is null ? PlutoLearningService.PatternBaseWeight : _learning.GetPatternWeight(domainKey);
            // 조합 이력 이전(Step 11 이하) 데이터 — 훅 단독 강화 이력은 있으나 이 조합 자체는 승리한 적 없음
            marginal = !otherLook && !otherDomain && _learning.GetPatternWeight("hook:" + hookRaw) > threshold && ownPair <= threshold && ownDomain <= threshold;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.ClosedLoop] attribution: " + ex.Message); return (global, null); }   // [부분 구제] 전역 귀속

        var isolated = new List<string>(2 + global.Count);
        if (pairKey is not null && (otherLook || marginal)) isolated.Add(pairKey);
        if (domainKey is not null && (otherDomain || (marginal && pairKey is null))) isolated.Add(domainKey);
        if (isolated.Count == 0) return (global, null);
        var first = isolated[0];
        var seen = new HashSet<string>(isolated, StringComparer.OrdinalIgnoreCase);
        if (t.StyleTags is not null) for (var i = 0; i < t.StyleTags.Count; i++) Add(isolated, seen, "", t.StyleTags[i]);
        return (isolated, first);
    }

    /// <summary>📐🌊 컨텍스트 관측(기준선 · 잔차 · 드리프트) — 실패 격리([부분 구제] 보정 없이 기존 판정 유지).</summary>
    private async Task<PlutoContextualObservation?> ObserveContextSafeAsync(PlutoVideoPerformanceTelemetry t, CancellationToken ct)
    {
        if (_learning is null) return null;
        try
        {
            var tokens = new List<string>(2);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Add(tokens, seen, "hook:", t.HookStyle);
            Add(tokens, seen, "look:", t.ColorLookId);
            return await _learning.RecordContextualObservationAsync(t.Domain, t.RenderMode, PlutoContextualBaseline.CompositeScore(t), tokens, ct,
                PlutoObjectiveVector.FromPercent(t.ThreeSecondHookRetentionPct, t.AverageCompletionPct, t.ClickThroughRatePct), ModalityObjectiveTokens(t)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.ClosedLoop] context: " + ex.Message); return null; }
    }

    /// <summary>🎰 시행 기록(가중치 불변) — 실패 격리([부분 구제] 반영 결과 유지).</summary>
    private async Task RecordTrialsSafeAsync(PlutoVideoPerformanceTelemetry t, CancellationToken ct)
    {
        if (_learning is null) return;
        try
        {
            var tokens = Concat(PatternTokens(t), InteractionTokens(t));
            if (tokens.Count > 0) await _learning.RecordPatternTrialsAsync(tokens, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.ClosedLoop] trials: " + ex.Message); }
    }

    public async Task<PlutoReinforcementOutcome> IngestPerformanceAsync(PlutoVideoPerformanceTelemetry telemetry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        if (string.IsNullOrWhiteSpace(telemetry.VideoId)) throw new ArgumentException("VideoId required", nameof(telemetry));
        ct.ThrowIfCancellationRequested();
        var key = LedgerKey(telemetry);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLedgerLoadedAsync(ct).ConfigureAwait(false);
            if (_ledgerSet.Contains(key))
                return Publish(new PlutoReinforcementOutcome(false, false, 0, 1.0, "⏭ 이미 반영된 영상입니다: " + telemetry.VideoId.Trim()) { AlreadyRegistered = true });

            var (breakout, strength, poor, deficit) = Evaluate(telemetry);
            var xp = 0;
            var mult = 1.0;
            var penalty = 0.0;
            string? dimension = null;
            string? isolatedKey = null;
            var warn = "";

            var forecast = ForecastSafe(telemetry);   // 🎯 관측이 기준선·기대치를 갱신하기 전의 예측(메타 보정 감사용)
            // 📐 관측 이전 기준선 대비 잔차 알파 · 🌊 Page-Hinkley 갱신(강화/벌점 적용 전에 수행)
            var ctx = await ObserveContextSafeAsync(telemetry, ct).ConfigureAwait(false);
            await ObserveSeriesArcSafeAsync(telemetry, ct).ConfigureAwait(false);   // 📺 시리즈 편 → 편 간 유지율 추세(비시리즈 영상은 무동작)
            var established = ctx is { IsEstablished: true };
            var z = ctx?.ZScore ?? 0.0;
            var formatOutperformer = established && z >= PlutoContextualBaseline.OutperformZ;
            var alphaApplied = false;
            var penaltyWaived = false;

            if (breakout)
            {
                var gain = PlutoContextualBaseline.BoostGain(z, established);
                mult = Math.Min(MaxBoost, 1.0 + (BoostFor(strength) - 1.0) * gain);   // 포맷 편승(z ≤ 0) → 부스트 감쇄
                alphaApplied = established && gain < 1.0 - 1e-9;
                xp = XpFor(strength);
                try
                {
                    if (_learning is not null) await _learning.ApplyPatternReinforcementAsync(Concat(PatternTokens(telemetry), InteractionTokens(telemetry)), mult, XpSourceKey, xp, ct).ConfigureAwait(false);
                    else { xp = 0; warn = " · ⚠ 학습 메모리 미연결"; }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.ClosedLoop] boost: " + ex.Message); xp = 0; warn = " · ⚠ 학습 기록 실패"; }   // [부분 구제]
                try
                {
                    if (_notebook is not null) await _notebook.RecordPositiveReliefAsync(Concat(MistakeTokens(telemetry), InteractionTokens(telemetry)), ct: ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.ClosedLoop] relief: " + ex.Message); }
            }
            else if (poor && formatOutperformer)
            {
                penaltyWaived = true;   // 📐 절대 기준 미달이지만 자기 포맷 기준선은 상회 — 포맷 편향을 패턴 탓으로 돌리지 않음
            }
            else if (poor)
            {
                dimension = ClassifyFailureDimension(telemetry);
                penalty = PenaltyForSteepestDeficit(deficit, dimension);   // 📐 훅 · 완주 · CTR 최대 정규화 결손 비례
                var (mistakes, isolated) = AttributeMistake(telemetry);   // 🎯 2-way 귀속(조합 격리)
                try
                {
                    if (_notebook is not null)
                    {
                        await _notebook.RecordDiagnosedMistakeAsync(mistakes, penalty, dimension, "closed-loop: " + telemetry.Platform + " " + telemetry.VideoId.Trim(), ct).ConfigureAwait(false);
                        isolatedKey = isolated;
                    }
                    else { penalty = 0; warn = " · ⚠ 오답 노트 미연결"; }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.ClosedLoop] penalty: " + ex.Message); penalty = 0; warn = " · ⚠ 오답 노트 기록 실패"; }
            }
            var alphaBoosted = false;
            if (!breakout && formatOutperformer && _learning is not null)
            {
                // 📐 알파 부스트 — 절대 돌파는 아니지만 포맷 기준선을 z ≥ 1 상회(강화에서 시행 +1)
                var alphaMult = PlutoContextualBaseline.AlphaBoost(z, true);
                try
                {
                    await _learning.ApplyPatternReinforcementAsync(Concat(PatternTokens(telemetry), InteractionTokens(telemetry)), alphaMult, XpSourceKey, 0, ct).ConfigureAwait(false);
                    mult = alphaMult;
                    alphaBoosted = alphaApplied = true;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.ClosedLoop] alpha boost: " + ex.Message); warn += " · ⚠ 알파 부스트 기록 실패"; }
            }
            if (!breakout && !alphaBoosted) await RecordTrialsSafeAsync(telemetry, ct).ConfigureAwait(false);   // 🎰 부진 · 평균도 시행으로 집계(돌파/알파 부스트는 강화에서 +1)

            var summary = Summarize(telemetry, breakout, poor && !penaltyWaived, xp, mult, penalty, dimension);
            if (isolatedKey is not null) summary += " · 🎯 조합 격리(" + isolatedKey + ") — 훅 전역 벌점 면제";
            if (ctx is not null && established)
            {
                summary += " · 📐 포맷 기준선 " + (ctx.Baseline * 100).ToString("0.#", Inv) + " 대비 " + (ctx.ResidualAlpha >= 0 ? "+" : "−")
                    + Math.Abs(ctx.ResidualAlpha * 100).ToString("0.#", Inv) + "p(z " + z.ToString("+0.0;−0.0", Inv) + ")";
                if (penaltyWaived) summary += " · 🛡️ 포맷 대비 초과 성과 — 벌점 면제";
                if (alphaBoosted) summary += " · 알파 부스트 ×" + mult.ToString("0.00", Inv);
                else if (breakout && alphaApplied) summary += " · 포맷 편승 보정 ×" + mult.ToString("0.00", Inv);
            }
            if (ctx is { DriftDetected: true }) summary += " · 🌊 레짐 변화 감지 — 탐색 서지 발동";
            // 🎯 예측 vs 실현 · 💸 반사실 후회 감사(예측 근거가 있을 때만)
            var calibration = forecast.HasBaseline
                ? await RecordCalibrationSafeAsync(forecast.PredictedScore, PlutoContextualBaseline.CompositeScore(telemetry), forecast.BestAlternativeScore, forecast.BestAlternativeLabel, "closed-loop", ct, telemetry).ConfigureAwait(false)
                : null;
            // 🔁 예측 근거(기준선)가 아직 없어도 '실측 검증된' 후회 리허설 대상은 소거(보정 창에는 기록되지 않음)
            if (!forecast.HasBaseline)
                await RecordCalibrationSafeAsync(double.NaN, PlutoContextualBaseline.CompositeScore(telemetry), double.NaN, null, "closed-loop", ct, telemetry).ConfigureAwait(false);
            if (calibration is { Regret: > PlutoMetaCalibrationService.RegretDeadBand })
                summary += " · 💸 반사실 후회 " + calibration.Regret.ToString("0.00", Inv) + (calibration.BestAlternativeLabel is { } alt ? "(대안 " + alt + ")" : "");
            var outcome = new PlutoReinforcementOutcome(breakout, penalty > 0, xp, mult, summary + warn)
            {
                CalibrationObservation = calibration,
                PenaltySeverity = penalty,
                FailureDimension = penalty > 0 ? dimension : null,
                IsolatedPairKey = isolatedKey,
                Context = ctx,
                ContextualAlphaApplied = alphaApplied || penaltyWaived,
                ContextualPenaltyWaived = penaltyWaived,
            };
            await AppendLedgerAsync(key, ct).ConfigureAwait(false);
            return Publish(outcome);
        }
        finally { _gate.Release(); }
    }

    /// <summary>⚖️ 상승률(%) + 승자 절대 돌파 강도 → 차등 부스트 강도 0~1.</summary>
    public static double ExperimentStrength(PlutoVideoPerformanceTelemetry winner, double liftPct)
    {
        ArgumentNullException.ThrowIfNull(winner);
        var lift = double.IsFinite(liftPct) ? Math.Clamp(liftPct / FullBoostLiftPct, 0.0, 1.0) : 0.0;
        var (breakout, strength, _, _) = Evaluate(winner);
        return breakout ? Math.Max(lift, strength) : lift;
    }

    public async Task<PlutoReinforcementOutcome> IngestExperimentOutcomeAsync(PlutoExperimentReinforcementRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Winner);
        ArgumentNullException.ThrowIfNull(request.Loser);
        if (string.IsNullOrWhiteSpace(request.ExperimentId)) throw new ArgumentException("ExperimentId required", nameof(request));
        ct.ThrowIfCancellationRequested();
        var key = ExperimentLedgerPrefix + request.ExperimentId.Trim();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLedgerLoadedAsync(ct).ConfigureAwait(false);
            if (_ledgerSet.Contains(key))
                return Publish(new PlutoReinforcementOutcome(false, false, 0, 1.0, "⏭ 이미 반영된 실험입니다: " + request.ExperimentId.Trim()) { AlreadyRegistered = true });

            var xp = 0;
            var mult = 1.0;
            var penalty = 0.0;
            string? loserDim = null;
            string? isolatedKey = null;
            var boosted = false;
            var warn = "";
            var winHook = Pct(request.Winner.ThreeSecondHookRetentionPct);
            var winForecast = ForecastSafe(request.Winner);   // 🎯 강화 반영 전 예측
            var loseForecast = ForecastSafe(request.Loser);

            if (!request.IsTie && winHook >= PoorHookPct)
            {
                var tokens = Concat(PatternTokens(request.Winner), InteractionTokens(request.Winner));
                var strength = ExperimentStrength(request.Winner, request.LiftPct);
                mult = BoostFor(strength);
                xp = XpFor(strength);
                boosted = true;
                try
                {
                    if (_learning is not null && tokens.Count > 0) await _learning.ApplyPatternReinforcementAsync(tokens, mult, XpSourceKey, xp, ct).ConfigureAwait(false);
                    else { xp = 0; mult = 1.0; boosted = false; warn = _learning is null ? " · ⚠ 학습 메모리 미연결" : " · ⚠ 차등 특성 없음"; }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.ClosedLoop] ab boost: " + ex.Message); xp = 0; mult = 1.0; boosted = false; warn = " · ⚠ 학습 기록 실패"; }   // [부분 구제]
                try
                {
                    var relief = Concat(MistakeTokens(request.Winner), InteractionTokens(request.Winner));
                    if (boosted && _notebook is not null && relief.Count > 0) await _notebook.RecordPositiveReliefAsync(relief, ct: ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.ClosedLoop] ab relief: " + ex.Message); }
            }

            // 🛡️ 패자 보호 — 절대 실패(훅 42 % · 완주 30 % · CTR 1.5 % 중 하나라도 미달)일 때만 벌점(무승부 포함 · 근소 패배는 면제)
            var (loseDeficit, loseDimCalc) = SteepestNormalizedDeficit(request.Loser.ThreeSecondHookRetentionPct, request.Loser.AverageCompletionPct, request.Loser.ClickThroughRatePct);
            if (request.LoserSteepestDeficit is { } given && double.IsFinite(given)) loseDeficit = Math.Clamp(given, 0.0, 1.0);
            if (loseDeficit > 0)
            {
                var (mistakes, isolated) = AttributeMistake(request.Loser);   // 🎯 2-way 귀속(조합 격리)
                if (mistakes.Count > 0)
                {
                    loserDim = request.LoserFailureDimension ?? loseDimCalc;
                    penalty = PenaltyForSteepestDeficit(loseDeficit, loserDim);
                    try
                    {
                        if (_notebook is not null)
                        {
                            await _notebook.RecordDiagnosedMistakeAsync(mistakes, penalty, loserDim, "ab-experiment: " + request.ExperimentId.Trim(), ct).ConfigureAwait(false);
                            isolatedKey = isolated;
                        }
                        else { penalty = 0; warn += " · ⚠ 오답 노트 미연결"; }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.ClosedLoop] ab penalty: " + ex.Message); penalty = 0; warn += " · ⚠ 오답 노트 기록 실패"; }
                }
            }

            // 🎰 시행 집계 — 부스트된 승자는 강화에서 +1 · 그 외 승자/패자는 가중치 불변 시행
            if (!boosted) await RecordTrialsSafeAsync(request.Winner, ct).ConfigureAwait(false);
            await RecordTrialsSafeAsync(request.Loser, ct).ConfigureAwait(false);

            var sb = new StringBuilder(160);
            if (request.IsTie) sb.Append("🤝 무승부 · 패턴 변경 없음");
            else if (boosted) sb.Append("🏆 승자 차등 특성 ×").Append(mult.ToString("0.00", Inv)).Append(" · +").Append(xp.ToString(Inv)).Append(" XP");
            else if (winHook < PoorHookPct) sb.Append("➖ 승자도 절대 실패선 미만 · 부스트 생략");
            else sb.Append("➖ 부스트 미적용");
            if (penalty > 0)
            {
                sb.Append(" · 🛡️ 패자 절대 실패(").Append(DimensionLabelKorean(loserDim)).Append(") 벌점 ").Append(penalty.ToString("0.00", Inv));
                if (penalty >= PlutoMistakeNotebookService.BlockThreshold) sb.Append(" · 🚫 ").Append(DimensionLabelKorean(loserDim)).Append(" 가드 활성화");
            }
            else if (!request.IsTie) sb.Append(" · 🛡️ 패자 보호(오답 노트 면제)");
            if (penalty > 0 && isolatedKey is not null) sb.Append(" · 🎯 조합 격리(").Append(isolatedKey).Append(')');

            // 🎯💸 A/B 는 반사실이 실측된다 — 각 변형의 최선 대안 = 상대 변형의 실현 점수(패자 후회 = 승자 − 패자 · 승자 후회 = 0)
            // ⚖️ 변형별 다목적 성과(훅 · 완주 · CTR) — 파레토 프런티어 근거
            await RecordObjectivesSafeAsync(request.Winner, ct).ConfigureAwait(false);
            await RecordObjectivesSafeAsync(request.Loser, ct).ConfigureAwait(false);
            await RecordExperimentContextAlphaSafeAsync(request, ct).ConfigureAwait(false);   // 🧬 소급과 같은 공식(짝 평균 기준) · 실험 ID 단위 1회
            PlutoCalibrationObservation? loserCalibration = null;
            if (MetaCalibration is not null)
            {
                var winScore = PlutoContextualBaseline.CompositeScore(request.Winner);
                var loseScore = PlutoContextualBaseline.CompositeScore(request.Loser);
                var winTokens = PatternTokens(request.Winner);
                var loseTokens = PatternTokens(request.Loser);
                await RecordCalibrationSafeAsync(winForecast.PredictedScore, winScore, loseScore, loseTokens.Count > 0 ? loseTokens[0] : null, "ab", ct, request.Winner, alternativeMeasured: true).ConfigureAwait(false);
                loserCalibration = await RecordCalibrationSafeAsync(loseForecast.PredictedScore, loseScore, winScore, winTokens.Count > 0 ? winTokens[0] : null, "ab", ct, request.Loser, alternativeMeasured: true).ConfigureAwait(false);
            }

            var outcome = new PlutoReinforcementOutcome(boosted, penalty > 0, xp, mult, sb.ToString() + warn)
            {
                CalibrationObservation = loserCalibration,
                PenaltySeverity = penalty,
                FailureDimension = penalty > 0 ? loserDim : null,
                IsolatedPairKey = penalty > 0 ? isolatedKey : null,
            };
            await AppendLedgerAsync(key, ct).ConfigureAwait(false);
            return Publish(outcome);
        }
        finally { _gate.Release(); }
    }

    private static string Summarize(PlutoVideoPerformanceTelemetry t, bool breakout, bool poor, int xp, double mult, double penalty, string? dimension)
    {
        var sb = new StringBuilder(160);
        sb.Append("3초 ").Append(Pct(t.ThreeSecondHookRetentionPct).ToString("0.#", Inv)).Append("% · CTR ").Append(Pct(t.ClickThroughRatePct).ToString("0.#", Inv))
          .Append("% · 완주 ").Append(Pct(t.AverageCompletionPct).ToString("0.#", Inv)).Append("% → ");
        if (breakout) sb.Append("🚀 돌파 성공 · 패턴 ×").Append(mult.ToString("0.00", Inv)).Append(" · +").Append(xp.ToString(Inv)).Append(" XP");
        else if (poor)
        {
            sb.Append("🛡️ 부진(").Append(DimensionLabelKorean(dimension)).Append(") · 오답 노트 벌점 ").Append(penalty.ToString("0.00", Inv)).Append(" (훅 '").Append(t.HookStyle?.Trim() ?? "").Append("' 회피 학습)");
            if (penalty >= PlutoMistakeNotebookService.BlockThreshold) sb.Append(" · 🚫 ").Append(DimensionLabelKorean(dimension)).Append(" 가드 활성화");
        }
        else sb.Append("➖ 평균 성과 · 변경 없음");
        return sb.ToString();
    }

    private PlutoReinforcementOutcome Publish(PlutoReinforcementOutcome o)
    {
        _last = o;
        var h = Changed;
        if (h is not null)
            foreach (Action a in h.GetInvocationList())
            {
                try { a(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.ClosedLoop] Changed handler: " + ex.Message); }
            }
        return o;
    }

    private static string LedgerKey(PlutoVideoPerformanceTelemetry t)
        => (string.IsNullOrWhiteSpace(t.Platform) ? "any" : t.Platform.Trim()) + "|" + t.VideoId.Trim();

    private async Task EnsureLedgerLoadedAsync(CancellationToken ct)
    {
        if (_ledgerLoaded) return;
        _ledgerLoaded = true;
        if (_storage is null) return;
        try
        {
            var json = await _storage.ReadAllTextAsync(LedgerFileName, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                // 🛡️ 저장소는 '파일 없음'과 '읽기 실패(잠금·권한)'를 모두 빈 문자열로 돌려준다 — 디스크에 내용이 있는데 못 읽었다면
                //    빈 장부로 덮어쓰지 않도록 읽기 전용 세션으로 전환(메모리 장부로만 중복 차단).
                _ledgerReadOnly = LedgerHasContentOnDisk();
                return;
            }
            var arr = JsonSerializer.Deserialize<List<string>>(json);
            if (arr is null) return;
            foreach (var k in arr)
                if (!string.IsNullOrWhiteSpace(k) && _ledgerSet.Add(k)) _ledger.Add(k);
        }
        catch (OperationCanceledException) { _ledgerLoaded = false; throw; }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or System.IO.IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.ClosedLoop] ledger load: " + ex.Message);   // [부분 구제] 빈 장부
            if (ex is System.IO.IOException or UnauthorizedAccessException) _ledgerReadOnly = true;   // 🛡️ 읽기 불가 → 기존 장부 보존
        }
    }

    private bool LedgerHasContentOnDisk()
    {
        try
        {
            var path = _storage?.ResolveDataPath(LedgerFileName);
            return !string.IsNullOrEmpty(path) && System.IO.File.Exists(path) && new System.IO.FileInfo(path).Length > 0;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.ClosedLoop] ledger probe: " + ex.Message);
            return true;   // 존재 여부조차 확인 불가 → 보수적으로 보존
        }
    }

    private async Task AppendLedgerAsync(string key, CancellationToken ct)
    {
        if (_ledgerSet.Add(key)) _ledger.Add(key);
        while (_ledger.Count > LedgerCapacity) { _ledgerSet.Remove(_ledger[0]); _ledger.RemoveAt(0); }
        if (_storage is null || _ledgerReadOnly) return;
        try { await _storage.WriteAllTextAtomicAsync(LedgerFileName, JsonSerializer.Serialize(_ledger), CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or NotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.ClosedLoop] ledger save: " + ex.Message);   // [부분 구제] 메모리 장부 유지
        }
    }

    private static double Pct(double v) => double.IsFinite(v) ? Math.Clamp(v, 0.0, 100.0) : 0.0;

    private static void Add(List<string> list, HashSet<string> seen, string prefix, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        var v = raw.Trim();
        if (v.Length > PlutoMistakeNotebookService.MaxTokenLength) v = v[..PlutoMistakeNotebookService.MaxTokenLength];
        var token = prefix.Length == 0 ? v : prefix + v;
        if (seen.Add(token)) list.Add(token);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Changed = null;
        _gate.Dispose();
    }
}
