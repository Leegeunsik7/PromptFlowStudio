using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🏆 A/B 변형 1개의 게시 후 지표(퍼센트 0~100).</summary>
public sealed record PlutoExperimentMetricsInput(double ThreeSecondHookRetentionPct, double AverageCompletionPct, double ClickThroughRatePct);

/// <summary>🏆 A/B 실험 판정 결과 — 승자 라벨(<c>ChampionA</c> · <c>ChallengerB</c> · <c>Tie</c>) · 합성 점수 · 상승률 · XP · 요약.</summary>
public sealed record PlutoExperimentEvaluationResult(string ExperimentId, string WinnerLabel, double ChampionScore, double ChallengerScore, double LiftPct, int TotalXpAwarded,
    string SummaryMessage, DateTimeOffset EvaluatedAtUtc)
{
    /// <summary>같은 실험 재판정 차단(기존 결과 반환).</summary>
    public bool AlreadyRegistered { get; init; }
    /// <summary>패자 오답 노트 벌점 기록 여부(절대 실패선 미만일 때만).</summary>
    public bool LoserPenalized { get; init; }
}

/// <summary>🎞️ A/B 실험에 결선된 변형별 렌더 영상(.mp4) 경로(미렌더 → null).</summary>
public sealed record PlutoExperimentArtifactSnapshot(string ExperimentId, string? ChampionVideoPath, string? ChallengerVideoPath);

/// <summary>🧬 Step 20 — 장부에 영속된 실험 컨텍스트(렌더 포맷 · 변형별 유효 스타일 태그 · 기록 없음 → null).</summary>
public sealed record PlutoExperimentContextSnapshot(string ExperimentId, string? RenderMode, IReadOnlyList<string>? ChampionStyleTags, IReadOnlyList<string>? ChallengerStyleTags);

/// <summary>🏆 Pluto 4.4 짝지은 A/B 실험 평가 장부.</summary>
public interface IPlutoExperimentLedgerService
{
    /// <summary>실험 등록(같은 ID 재등록 → 최신 플랜으로 갱신 · 판정 완료 실험은 불변).</summary>
    void RegisterExperiment(PlutoVariantABPlan plan);

    /// <summary>
    /// 챔피언 A vs 챌린저 B 지표 → 합성 점수 · 승자 · 상승률 → 폐루프 차등 강화 → 장부 원자 기록.
    /// 같은 실험 재판정 → 기존 결과(<see cref="PlutoExperimentEvaluationResult.AlreadyRegistered"/>). 강화/저장 실패 → [부분 구제] · 취소만 전파.
    /// </summary>
    Task<PlutoExperimentEvaluationResult> EvaluateExperimentAsync(PlutoVariantABPlan plan, PlutoExperimentMetricsInput championMetrics, PlutoExperimentMetricsInput challengerMetrics,
        CancellationToken ct = default);

    /// <summary>최근 판정(최신 우선).</summary>
    IReadOnlyList<PlutoExperimentEvaluationResult> GetRecentEvaluations(int maxCount = 20);

    /// <summary>📋 판정 대기(<c>Pending</c>) 실험(최신 등록 우선 · 앱 재시작 후에도 장부에서 복원).</summary>
    IReadOnlyList<PlutoVariantABPlan> GetPendingExperiments(int maxCount = 20);

    /// <summary>장부 선적재(재시작 후 대기 실험 · 최근 판정 표시용 · 실패 → 빈 장부).</summary>
    Task EnsureLoadedAsync(CancellationToken ct = default);

    /// <summary>
    /// 🎞️ 변형 렌더 영상 결선 — 비어 있지 않은 경로만 해당 실험에 병합(빈 값 → 기존 유지) · 원자 기록(Drive → %LOCALAPPDATA% 폴백).
    /// Pending → Evaluated 전이 후에도 보존 · 미등록 실험은 장부 적재 후 재시도(없으면 무시) · 예외 전파 금지(인자 검증 제외).
    /// </summary>
    void AttachVariantArtifacts(string experimentId, string? championVideoPath, string? challengerVideoPath);

    /// <summary>🎞️ 실험 변형 영상 스냅샷(미등록 → null).</summary>
    PlutoExperimentArtifactSnapshot? GetExperimentArtifacts(string experimentId);

    /// <summary>
    /// 🏷️ Step 20 — 변형 렌더 시점 스타일 태그 영구 기록(null → 그 변형 유지 · 판정 후에도 보존 · 예외 전파 금지(인자 검증 제외)).
    /// 기본 구현 → 무동작(태그 미영속 장부).
    /// </summary>
    void AttachVariantStyleTags(string experimentId, IReadOnlyList<string>? championTags, IReadOnlyList<string>? challengerTags) { }

    /// <summary>🧬 Step 20 — 실험의 영속 렌더 포맷 · 변형별 유효 태그(미등록 → null).</summary>
    PlutoExperimentContextSnapshot? GetExperimentContext(string experimentId) => null;

    /// <summary>판정 통지(백그라운드 스레드 가능).</summary>
    event Action? Changed;
}

/// <summary>
/// 🏆 <see cref="IPlutoExperimentLedgerService"/> 구현.
///   ▸ 합성 점수 = 0.50·3초 유지율 + 0.30·완주율 + 0.20·(CTR × 10) · |A−B| &lt; <see cref="TieThreshold"/> → 무승부.
///   ▸ 차등 강화: 승자/패자 간 <b>다른</b> 훅·룩·BGM 만 <see cref="IPlutoClosedLoopReinforcementService.IngestExperimentOutcomeAsync"/> 로 전달.
///   ▸ 장부 <c>pluto_experiment_ledger.json</c> — <see cref="IPlutoStorageManager"/> 원자 기록(Drive → %LOCALAPPDATA% 폴백) · /// 원자 기록(Drive → %LOCALAPPDATA% 폴백) · 용량 <see cref="Capacity"/>.
///   ▸ 4.5 — 등록 즉시 <see cref="StatusPending"/> 로 영속 · 판정 완료 시 <see cref="StatusEvaluated"/> 전이(재시작 후 대기 실험 재개).
/// </summary>
public sealed class PlutoExperimentLedgerService : IPlutoExperimentLedgerService, IPlutoHotReloadable, IDisposable
{
    public const string FileName = "pluto_experiment_ledger.json";
    public const string WinnerChampion = "ChampionA";
    public const string WinnerChallenger = "ChallengerB";
    public const string WinnerTie = "Tie";
    public const double HookWeight = 0.50, CompletionWeight = 0.30, CtrWeight = 0.20, CtrScale = 10.0;
    /// <summary>합성 점수 차 이 값 미만 → 무승부.</summary>
    public const double TieThreshold = 0.5;
    public const string Platform = "ab-experiment";
    public const int Capacity = 200;
    public const string StatusPending = "Pending";
    public const string StatusEvaluated = "Evaluated";
    /// <summary>🏷️ 실험/변형당 영속 스타일 태그 상한.</summary>
    public const int MaxStyleTags = 12;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly IPlutoClosedLoopReinforcementService? _reinforcement;
    private readonly IPlutoStorageManager? _storage;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly List<Entry> _entries = new(16);
    private bool _loaded;
    private Task _registerPersist = Task.CompletedTask;
    private int _disposed;

    public PlutoExperimentLedgerService(IPlutoClosedLoopReinforcementService? reinforcement, IPlutoStorageManager? storage = null, Func<DateTimeOffset>? utcNow = null)
    {
        _reinforcement = reinforcement;
        _storage = storage;
        _utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);
    }

    public event Action? Changed;

    /// <summary>합성 참여 점수(각 지표 0~100 클램프 · NaN/∞ → 0).</summary>
    public static double CompositeScore(PlutoExperimentMetricsInput m)
    {
        ArgumentNullException.ThrowIfNull(m);
        return HookWeight * Pct(m.ThreeSecondHookRetentionPct) + CompletionWeight * Pct(m.AverageCompletionPct) + CtrWeight * (Pct(m.ClickThroughRatePct) * CtrScale);
    }

    /// <summary>A/B 패자 절대 실패선 — 3초 유지율 · 완주율 · CTR(%).</summary>
    public const double FailHookPct = 42.0, FailCompletionPct = 30.0, FailCtrPct = 1.5;

    /// <summary>
    /// 🩺 A/B 패자 실패 차원(순수 함수) — 3지표 상대 결손 <c>(기준 − 값) / 기준</c> 중 최대(동률 → 훅 &gt; 완주 &gt; CTR).
    /// 결손 없음 → <c>GeneralPenalty</c>.
    /// </summary>
    public static string ClassifyLoserFailureDimension(PlutoExperimentMetricsInput m) => LoserSteepestDeficit(m).Dimension;

    /// <summary>📐 A/B 패자 최대 정규화 결손(0~1) + 차원 — 공용 <see cref="PlutoClosedLoopReinforcementService.SteepestNormalizedDeficit"/> 위임.</summary>
    public static (double Deficit, string Dimension) LoserSteepestDeficit(PlutoExperimentMetricsInput m)
    {
        ArgumentNullException.ThrowIfNull(m);
        return PlutoClosedLoopReinforcementService.SteepestNormalizedDeficit(m.ThreeSecondHookRetentionPct, m.AverageCompletionPct, m.ClickThroughRatePct,
            FailHookPct, FailCompletionPct, FailCtrPct);
    }

    /// <summary>승자 판정(순수 함수) — 상승률 = (승자 − 패자) / 패자 × 100(패자 0 → 100 · 무승부 0).</summary>
    public static (string Winner, double LiftPct) DecideWinner(double championScore, double challengerScore)
    {
        if (Math.Abs(championScore - challengerScore) < TieThreshold) return (WinnerTie, 0.0);
        var challengerWins = challengerScore > championScore;
        double win = challengerWins ? challengerScore : championScore, lose = challengerWins ? championScore : challengerScore;
        var lift = lose > 1e-9 ? (win - lose) / lose * 100.0 : 100.0;
        return (challengerWins ? WinnerChallenger : WinnerChampion, Math.Round(lift, 2, MidpointRounding.AwayFromZero));
    }

    public void RegisterExperiment(PlutoVariantABPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(plan.ExperimentId)) throw new ArgumentException("ExperimentId required", nameof(plan));
        var id = plan.ExperimentId.Trim();
        bool changed;
        lock (_sync)
        {
            var e = FindLocked(id);
            changed = e is null || e.Evaluation is null;
            if (e is null) { _entries.Add(Entry.From(plan, _utcNow())); TrimLocked(); }
            else if (e.Evaluation is null) e.Apply(plan);
            if (changed && _storage is not null) { var prev = _registerPersist; _registerPersist = Task.Run(() => PersistRegisteredAsync(prev)); }   // 직렬 체인(등록 순서 보존)
        }
        if (changed) RaiseChanged();
    }

    /// <summary>📋 등록분 영속 대기(테스트 · 종료 직전 플러시용 · 실패는 이미 격리됨).</summary>
    public Task WhenRegisteredPersistedAsync() { lock (_sync) return _registerPersist; }

    /// <summary>📋 Pending 영속 — 장부 선적재(디스크 기존 실험 병합) 후 원자 기록 · 예외 전파 금지.</summary>
    private async Task PersistRegisteredAsync(Task previous)
    {
        try { await previous.ConfigureAwait(false); } catch (Exception) { /* 이전 체인 실패는 격리됨 */ }
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await EnsureLoadedCoreAsync(CancellationToken.None).ConfigureAwait(false);
                await PersistAsync().ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }
        catch (ObjectDisposedException) { /* 종료 중 */ }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.ExperimentLedger] register persist: " + ex.Message); }   // [부분 구제] 메모리 등록 유지
    }

    public async Task<PlutoExperimentEvaluationResult> EvaluateExperimentAsync(PlutoVariantABPlan plan, PlutoExperimentMetricsInput championMetrics, PlutoExperimentMetricsInput challengerMetrics,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(championMetrics);
        ArgumentNullException.ThrowIfNull(challengerMetrics);
        if (string.IsNullOrWhiteSpace(plan.ExperimentId)) throw new ArgumentException("ExperimentId required", nameof(plan));
        ct.ThrowIfCancellationRequested();
        var id = plan.ExperimentId.Trim();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedCoreAsync(ct).ConfigureAwait(false);
            Entry? entry;
            lock (_sync)
            {
                entry = FindLocked(id);
                if (entry?.Evaluation is { } done) return done with { AlreadyRegistered = true };
                if (entry is null) { entry = Entry.From(plan, _utcNow()); _entries.Add(entry); }
                else entry.Apply(plan);
            }

            var champScore = Math.Round(CompositeScore(championMetrics), 2, MidpointRounding.AwayFromZero);
            var challScore = Math.Round(CompositeScore(challengerMetrics), 2, MidpointRounding.AwayFromZero);
            var (winner, lift) = DecideWinner(champScore, challScore);
            var tie = string.Equals(winner, WinnerTie, StringComparison.Ordinal);
            var challengerWins = string.Equals(winner, WinnerChallenger, StringComparison.Ordinal);
            PlutoExperimentVariant win = challengerWins ? plan.ChallengerB : plan.ChampionA, lose = challengerWins ? plan.ChampionA : plan.ChallengerB;
            PlutoExperimentMetricsInput winM = challengerWins ? challengerMetrics : championMetrics, loseM = challengerWins ? championMetrics : challengerMetrics;

            // ⚖️ 차등 특성만 강화 대상(공통 특성은 실험 변수가 아님)
            var (loseDeficit, loseDim) = LoserSteepestDeficit(loseM);   // 📐 훅 · 완주 · CTR 최대 정규화 결손
            string? recordedMode;
            List<string>? winTags, loseTags;
            lock (_sync)
            {
                recordedMode = entry.RenderMode;
                winTags = entry.EffectiveStyleTags(challengerWins);
                loseTags = entry.EffectiveStyleTags(!challengerWins);
            }
            var request = new PlutoExperimentReinforcementRequest(id, DiffTelemetry(id, "W", win, lose, winM), DiffTelemetry(id, "L", lose, win, loseM), lift, tie)
            {
                LoserFailureDimension = loseDim,   // 🩺 훅 고정 대신 최대 결손 차원
                LoserSteepestDeficit = loseDeficit,   // 📐 결손 > 0 → 벌점 · 크기 비례
                ContextDomain = PromptDomainClassifier.Classify(plan.ThemeHint).ToString(),   // 🧬 A/B 생성기와 같은 분류
                ContextRenderMode = recordedMode ?? plan.ContextRenderMode ?? PlutoContextAlphaBackfill.DefaultRenderMode,
                WinnerContextStyleTags = winTags,   // 🏷️ 실시간 컨텍스트 알파도 소급과 같은 태그 근거(차이 나는 태그만 표본)
                LoserContextStyleTags = loseTags,
            };
            PlutoReinforcementOutcome? outcome = null;
            var warn = "";
            if (_reinforcement is not null)
            {
                try { outcome = await _reinforcement.IngestExperimentOutcomeAsync(request, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.ExperimentLedger] reinforce: " + ex.Message); warn = " · ⚠ 차등 학습 실패"; }   // [부분 구제] 판정은 보존
            }
            else warn = " · ⚠ 폐루프 강화 미연결";

            var result = new PlutoExperimentEvaluationResult(id, winner, champScore, challScore, lift, outcome?.XpDelta ?? 0,
                Summarize(winner, champScore, challScore, lift, win, outcome) + warn, _utcNow()) { LoserPenalized = outcome?.RecordedMistakePenalty ?? false };
            lock (_sync)
            {
                entry.ChampionMetrics = championMetrics;
                entry.ChallengerMetrics = challengerMetrics;
                entry.Evaluation = result;
                entry.Status = StatusEvaluated;
                TrimLocked();
            }
            await PersistAsync().ConfigureAwait(false);
            RaiseChanged();
            return result;
        }
        finally { _gate.Release(); }
    }

    public IReadOnlyList<PlutoExperimentEvaluationResult> GetRecentEvaluations(int maxCount = 20)
    {
        if (maxCount <= 0) return Array.Empty<PlutoExperimentEvaluationResult>();
        lock (_sync)
        {
            var list = new List<PlutoExperimentEvaluationResult>(Math.Min(maxCount, _entries.Count));
            for (var i = _entries.Count - 1; i >= 0 && list.Count < maxCount; i--)
                if (_entries[i].Evaluation is { } r) list.Add(r);
            return list;
        }
    }

    public IReadOnlyList<PlutoVariantABPlan> GetPendingExperiments(int maxCount = 20)
    {
        if (maxCount <= 0) return Array.Empty<PlutoVariantABPlan>();
        lock (_sync)
        {
            var list = new List<PlutoVariantABPlan>(Math.Min(maxCount, _entries.Count));
            for (var i = _entries.Count - 1; i >= 0 && list.Count < maxCount; i--)
                if (_entries[i].Evaluation is null) list.Add(_entries[i].ToPlan());
            return list;
        }
    }

    public void AttachVariantArtifacts(string experimentId, string? championVideoPath, string? challengerVideoPath)
    {
        if (string.IsNullOrWhiteSpace(experimentId)) throw new ArgumentException("ExperimentId required", nameof(experimentId));
        var id = experimentId.Trim();
        var a = NormalizePath(championVideoPath);
        var b = NormalizePath(challengerVideoPath);
        if (a is null && b is null) return;
        bool changed;
        lock (_sync)
        {
            changed = FindLocked(id) is { } e && e.MergeArtifacts(a, b);
            if (_storage is not null) { var prev = _registerPersist; _registerPersist = Task.Run(() => PersistArtifactsAsync(prev, id, a, b)); }   // 직렬 체인 — 적재 후 재병합(디스크 전용 실험 대응)
        }
        if (changed) RaiseChanged();
    }

    public PlutoExperimentArtifactSnapshot? GetExperimentArtifacts(string experimentId)
    {
        if (string.IsNullOrWhiteSpace(experimentId)) return null;
        lock (_sync)
            return FindLocked(experimentId.Trim()) is { } e ? new PlutoExperimentArtifactSnapshot(e.ExperimentId, e.ChampionVideoPath, e.ChallengerVideoPath) : null;
    }

    /// <summary>🎞️ 결선 영속 — 장부 선적재 → 재병합 → 원자 기록 · 예외 전파 금지.</summary>
    private async Task PersistArtifactsAsync(Task previous, string id, string? a, string? b)
    {
        try { await previous.ConfigureAwait(false); } catch (Exception) { /* 이전 체인 실패는 격리됨 */ }
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            bool found, lateMerge;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await EnsureLoadedCoreAsync(CancellationToken.None).ConfigureAwait(false);
                lock (_sync)
                {
                    var e = FindLocked(id);
                    found = e is not null;
                    lateMerge = e is not null && e.MergeArtifacts(a, b);
                }
                if (found) await PersistAsync().ConfigureAwait(false);
            }
            finally { _gate.Release(); }
            if (lateMerge) RaiseChanged();
        }
        catch (ObjectDisposedException) { /* 종료 중 */ }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.ExperimentLedger] artifact persist: " + ex.Message); }   // [부분 구제] 메모리 결선 유지
    }

    public void AttachVariantStyleTags(string experimentId, IReadOnlyList<string>? championTags, IReadOnlyList<string>? challengerTags)
    {
        if (string.IsNullOrWhiteSpace(experimentId)) throw new ArgumentException("ExperimentId required", nameof(experimentId));
        var id = experimentId.Trim();
        var a = Entry.NormalizeTags(championTags);
        var b = Entry.NormalizeTags(challengerTags);
        if (a is null && b is null) return;
        bool changed;
        lock (_sync)
        {
            changed = FindLocked(id) is { } e && MergeTags(e, a, b);
            if (_storage is not null) { var prev = _registerPersist; _registerPersist = Task.Run(() => PersistMutationAsync(prev, id, e2 => MergeTags(e2, a, b))); }
        }
        if (changed) RaiseChanged();
    }

    private static bool MergeTags(Entry e, List<string>? a, List<string>? b)
    {
        var changed = false;
        if (a is not null && !SameTags(e.ChampionStyleTags, a)) { e.ChampionStyleTags = new List<string>(a); changed = true; }
        if (b is not null && !SameTags(e.ChallengerStyleTags, b)) { e.ChallengerStyleTags = new List<string>(b); changed = true; }
        return changed;
    }

    private static bool SameTags(List<string>? x, List<string> y)
    {
        if (x is null || x.Count != y.Count) return false;
        for (var i = 0; i < x.Count; i++) if (!string.Equals(x[i], y[i], StringComparison.Ordinal)) return false;
        return true;
    }

    public PlutoExperimentContextSnapshot? GetExperimentContext(string experimentId)
    {
        if (string.IsNullOrWhiteSpace(experimentId)) return null;
        lock (_sync)
            return FindLocked(experimentId.Trim()) is { } e
                ? new PlutoExperimentContextSnapshot(e.ExperimentId, e.RenderMode, e.EffectiveStyleTags(false), e.EffectiveStyleTags(true))
                : null;
    }

    /// <summary>🏷️ 변경 영속 — 장부 선적재 → 재적용 → 원자 기록 · 예외 전파 금지(직렬 체인).</summary>
    private async Task PersistMutationAsync(Task previous, string id, Func<Entry, bool> mutate)
    {
        try { await previous.ConfigureAwait(false); } catch (Exception) { /* 이전 체인 실패는 격리됨 */ }
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            bool found, late;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await EnsureLoadedCoreAsync(CancellationToken.None).ConfigureAwait(false);
                lock (_sync)
                {
                    var e = FindLocked(id);
                    found = e is not null;
                    late = e is not null && mutate(e);
                }
                if (found) await PersistAsync().ConfigureAwait(false);
            }
            finally { _gate.Release(); }
            if (late) RaiseChanged();
        }
        catch (ObjectDisposedException) { /* 종료 중 */ }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.ExperimentLedger] mutation persist: " + ex.Message); }   // [부분 구제] 메모리 유지
    }

    // ── ♻️ Step 20 — 실행 중 재적재(충돌 병합/복원 즉시 반영) ──

    public string HubFileName => FileName;

    /// <summary>
    /// ♻️ 디스크 → 메모리 재적재 — 등록/결선 기록 체인 배수 → 게이트 안에서 대기 해결 승격 → 파싱 → 교체.
    /// 배수 이후 게이트 획득 전에 막 등록된(아직 디스크에 없는) 실험은 보존. 파싱 실패/파일 없음 → 라이브 장부 유지 + false.
    /// </summary>
    public async Task<bool> ReloadFromDiskAsync(CancellationToken ct = default)
    {
        if (_storage is null || Volatile.Read(ref _disposed) != 0) return false;
        try { await WhenRegisteredPersistedAsync().ConfigureAwait(false); } catch (Exception) { /* 체인 실패는 격리됨 */ }
        HashSet<string> drained;
        lock (_sync)
        {
            drained = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < _entries.Count; i++) drained.Add(_entries[i].ExperimentId);
        }
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            PlutoHotReload.PromotePending(_storage, FileName);
            string json;
            try { json = await _storage.ReadAllTextAsync(FileName, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { return false; }
            if (string.IsNullOrWhiteSpace(json)) return false;
            LedgerDocument? doc;
            try { doc = JsonSerializer.Deserialize<LedgerDocument>(json); }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                System.Diagnostics.Debug.WriteLine("[Pluto.ExperimentLedger] reload rejected: " + ex.Message);   // 라이브 장부 유지
                return false;
            }
            if (doc?.Experiments is not { } list) return false;
            lock (_sync)
            {
                var fresh = new List<Entry>(list.Count + 2);
                foreach (var e in list)
                    if (e is not null && !string.IsNullOrWhiteSpace(e.ExperimentId) && FindIn(fresh, e.ExperimentId) is null) { e.Normalize(); fresh.Add(e); }
                foreach (var e in _entries)   // 배수 이후 등록된 실험(디스크 미반영) 보존 — 뒤이은 기록 체인이 영속
                    if (!drained.Contains(e.ExperimentId) && FindIn(fresh, e.ExperimentId) is null) fresh.Add(e);
                _entries.Clear();
                _entries.AddRange(fresh);
                TrimLocked();
            }
            _loaded = true;
        }
        finally { _gate.Release(); }
        RaiseChanged();
        return true;
    }

    private static string? NormalizePath(string? p) => string.IsNullOrWhiteSpace(p) ? null : p.Trim();

    /// <summary>등록(판정 대기 포함) 실험 수.</summary>
    public int RegisteredCount { get { lock (_sync) return _entries.Count; } }

    /// <summary>장부 선적재(UI 진입 시 대기 실험 · 최근 판정 표시용 · 실패 → 빈 장부).</summary>
    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loaded || _storage is null) { _loaded = true; return; }
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await EnsureLoadedCoreAsync(ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>선적재 본체 — 호출자가 <see cref="_gate"/> 보유.</summary>
    private async Task EnsureLoadedCoreAsync(CancellationToken ct)
    {
        if (_loaded || _storage is null) { _loaded = true; return; }
        _loaded = true;
        try
        {
            var json = await _storage.ReadAllTextAsync(FileName, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) return;
            var doc = JsonSerializer.Deserialize<LedgerDocument>(json);
            if (doc?.Experiments is not { } list) return;
            lock (_sync)
            {
                var merged = new List<Entry>(list.Count + _entries.Count);
                foreach (var e in list)
                    if (e is not null && !string.IsNullOrWhiteSpace(e.ExperimentId) && FindIn(merged, e.ExperimentId) is null) { e.Normalize(); merged.Add(e); }
                foreach (var e in _entries)   // 적재 전 메모리 등록분 보존(동일 ID → 메모리 우선 · 디스크 판정 완료분은 불변)
                {
                    var old = FindIn(merged, e.ExperimentId);
                    if (old is not null)
                    {
                        if (old.Evaluation is not null && e.Evaluation is null) { old.MergeArtifacts(e.ChampionVideoPath, e.ChallengerVideoPath); continue; }
                        e.ChampionVideoPath ??= old.ChampionVideoPath;   // 🎞️ 디스크 결선 영상 보존
                        e.ChallengerVideoPath ??= old.ChallengerVideoPath;
                        merged.Remove(old);
                    }
                    merged.Add(e);
                }
                _entries.Clear();
                _entries.AddRange(merged);
                TrimLocked();
            }
        }
        catch (OperationCanceledException) { _loaded = false; throw; }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or System.IO.IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.ExperimentLedger] load: " + ex.Message);   // [부분 구제] 빈 장부
        }
    }

    // ── 내부 ──

    private static PlutoVideoPerformanceTelemetry DiffTelemetry(string id, string suffix, PlutoExperimentVariant self, PlutoExperimentVariant other, PlutoExperimentMetricsInput m)
        => new(id + "-" + suffix, Platform,
            Diff(self.HookStyle, other.HookStyle), Diff(self.ColorLookId, other.ColorLookId), Diff(self.BgmMood, other.BgmMood), Array.Empty<string>(),
            m.ThreeSecondHookRetentionPct, m.AverageCompletionPct, m.ClickThroughRatePct, DateTimeOffset.UtcNow);

    private static string Diff(string? self, string? other)
        => string.Equals(self?.Trim(), other?.Trim(), StringComparison.Ordinal) ? "" : self?.Trim() ?? "";

    private static string Summarize(string winner, double a, double b, double lift, PlutoExperimentVariant win, PlutoReinforcementOutcome? outcome)
    {
        var sb = new StringBuilder(200);
        sb.Append("A ").Append(a.ToString("0.0", Inv)).Append(" vs B ").Append(b.ToString("0.0", Inv)).Append(" → ");
        if (string.Equals(winner, WinnerTie, StringComparison.Ordinal)) sb.Append("🤝 무승부");
        else
        {
            sb.Append(string.Equals(winner, WinnerChallenger, StringComparison.Ordinal) ? "🧪 챌린저 B 승리" : "🏆 챔피언 A 방어")
              .Append(" (+").Append(lift.ToString("0.#", Inv)).Append("% · 훅 '").Append(win.HookStyle).Append("' · ").Append(win.ColorLookId).Append(')');
        }
        if (outcome is not null) sb.Append(" · ").Append(outcome.SummaryMessage);
        return sb.ToString();
    }

    private Entry? FindLocked(string id) => FindIn(_entries, id);

    private static Entry? FindIn(List<Entry> list, string id)
    {
        for (var i = 0; i < list.Count; i++) if (string.Equals(list[i].ExperimentId, id, StringComparison.Ordinal)) return list[i];
        return null;
    }

    private void TrimLocked()
    {
        while (_entries.Count > Capacity) _entries.RemoveAt(0);
    }

    private async Task PersistAsync()
    {
        if (_storage is null) return;
        string json;
        lock (_sync) json = JsonSerializer.Serialize(new LedgerDocument { Version = 1, Experiments = new List<Entry>(_entries) });
        try { await _storage.WriteAllTextAtomicAsync(FileName, json, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or NotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.ExperimentLedger] save: " + ex.Message);   // [부분 구제] 메모리 장부 유지
        }
    }

    private void RaiseChanged()
    {
        var h = Changed;
        if (h is null) return;
        foreach (Action a in h.GetInvocationList())
        {
            try { a(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.ExperimentLedger] Changed handler: " + ex.Message); }
        }
    }

    private static double Pct(double v) => double.IsFinite(v) ? Math.Clamp(v, 0.0, 100.0) : 0.0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Changed = null;
        _gate.Dispose();
    }

    // ── 직렬화 DTO(System.Text.Json) ──

    internal sealed class LedgerDocument
    {
        public int Version { get; set; }
        public List<Entry>? Experiments { get; set; }
    }

    internal sealed class Entry
    {
        public string ExperimentId { get; set; } = "";
        public string ThemeHint { get; set; } = "";
        public string ChampionHook { get; set; } = "";
        public string ChampionLook { get; set; } = "";
        public string ChampionBgm { get; set; } = "";
        public string ChallengerHook { get; set; } = "";
        public string ChallengerLook { get; set; } = "";
        public string ChallengerBgm { get; set; } = "";
        public DateTimeOffset RegisteredAtUtc { get; set; }
        public PlutoExperimentMetricsInput? ChampionMetrics { get; set; }
        public PlutoExperimentMetricsInput? ChallengerMetrics { get; set; }
        public PlutoExperimentEvaluationResult? Evaluation { get; set; }
        /// <summary><see cref="StatusPending"/> · <see cref="StatusEvaluated"/>(구 장부 누락 → 판정 유무로 보정).</summary>
        public string Status { get; set; } = StatusPending;
        public string ChampionLabel { get; set; } = "";
        public string ChallengerLabel { get; set; } = "";
        public string ChampionPromptSuffix { get; set; } = "";
        public string ChallengerPromptSuffix { get; set; } = "";
        public string HypothesisSummary { get; set; } = "";
        public List<PromptFlowStudio.Services.Export.SubtitleCue>? ChampionSubtitles { get; set; }
        public List<PromptFlowStudio.Services.Export.SubtitleCue>? ChallengerSubtitles { get; set; }
        /// <summary>🎞️ 챔피언 A 렌더 영상(구 장부 누락 → null · <see cref="Apply"/> 는 건드리지 않음).</summary>
        public string? ChampionVideoPath { get; set; }
        /// <summary>🎞️ 챌린저 B 렌더 영상(구 장부 누락 → null).</summary>
        public string? ChallengerVideoPath { get; set; }
        /// <summary>🧬 Step 20 — 실험 렌더 포맷(구 장부 누락 → null = 소급 시 <see cref="PlutoContextAlphaBackfill.DefaultRenderMode"/>).</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public string? RenderMode { get; set; }
        /// <summary>🏷️ Step 20 — 실험 공유 스타일 태그(구 장부 누락 → null = 카탈로그 조인 폴백).</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public List<string>? StyleTags { get; set; }
        /// <summary>🏷️ Step 20 — 챔피언 A 렌더 시점 스타일 태그(미렌더/구 장부 → null).</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public List<string>? ChampionStyleTags { get; set; }
        /// <summary>🏷️ Step 20 — 챌린저 B 렌더 시점 스타일 태그(미렌더/구 장부 → null).</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public List<string>? ChallengerStyleTags { get; set; }

        /// <summary>🏷️ 변형별 유효 태그 = 변형 태그 ∪ 실험 공유 태그(대소문자 무시 중복 제거 · 기록 없음 → null = 카탈로그 폴백).</summary>
        public List<string>? EffectiveStyleTags(bool challenger)
        {
            var own = challenger ? ChallengerStyleTags : ChampionStyleTags;
            if (own is null && StyleTags is null) return null;
            var list = new List<string>((own?.Count ?? 0) + (StyleTags?.Count ?? 0));
            AppendTags(list, own);
            AppendTags(list, StyleTags);
            return list;
        }

        /// <summary>🏷️ 태그 정규화 — 공백 제거 · 빈 값/중복 제외 · 최대 <see cref="MaxStyleTags"/>개(입력 없음/전부 빈 값 → null).</summary>
        public static List<string>? NormalizeTags(IEnumerable<string>? tags)
        {
            if (tags is null) return null;
            var list = new List<string>(4);
            foreach (var t in tags)
            {
                if (list.Count >= MaxStyleTags) break;
                AppendTag(list, t);
            }
            return list.Count == 0 ? null : list;
        }

        private static void AppendTags(List<string> list, List<string>? src)
        {
            if (src is null) return;
            for (var i = 0; i < src.Count; i++) AppendTag(list, src[i]);
        }

        private static void AppendTag(List<string> list, string? t)
        {
            if (string.IsNullOrWhiteSpace(t)) return;
            var v = t.Trim();
            for (var i = 0; i < list.Count; i++) if (string.Equals(list[i], v, StringComparison.OrdinalIgnoreCase)) return;
            list.Add(v);
        }

        /// <summary>비어 있지 않은 경로만 병합 · 반환: 변경 여부.</summary>
        public bool MergeArtifacts(string? a, string? b)
        {
            var changed = false;
            if (a is not null && !string.Equals(ChampionVideoPath, a, StringComparison.Ordinal)) { ChampionVideoPath = a; changed = true; }
            if (b is not null && !string.Equals(ChallengerVideoPath, b, StringComparison.Ordinal)) { ChallengerVideoPath = b; changed = true; }
            return changed;
        }

        public void Normalize() => Status = Evaluation is null ? StatusPending : StatusEvaluated;

        public PlutoVariantABPlan ToPlan()
            => new(ExperimentId, ThemeHint,
                new PlutoExperimentVariant(string.IsNullOrEmpty(ChampionLabel) ? PlutoVariantABGenerator.ChampionLabel : ChampionLabel, ChampionHook, ChampionLook, ChampionBgm, ChampionPromptSuffix,
                    ChampionSubtitles is { } a ? a.ToArray() : Array.Empty<PromptFlowStudio.Services.Export.SubtitleCue>()),
                new PlutoExperimentVariant(string.IsNullOrEmpty(ChallengerLabel) ? PlutoVariantABGenerator.ChallengerLabel : ChallengerLabel, ChallengerHook, ChallengerLook, ChallengerBgm, ChallengerPromptSuffix,
                    ChallengerSubtitles is { } b ? b.ToArray() : Array.Empty<PromptFlowStudio.Services.Export.SubtitleCue>()),
                HypothesisSummary)
            {
                ContextRenderMode = string.IsNullOrWhiteSpace(RenderMode) ? null : RenderMode,   // 🧬 재시작 후 재개한 실험도 같은 컨텍스트로 판정
                ContextStyleTags = StyleTags is { Count: > 0 } st ? st.ToArray() : null,
            };

        public static Entry From(PlutoVariantABPlan plan, DateTimeOffset now)
        {
            var e = new Entry { ExperimentId = plan.ExperimentId.Trim(), RegisteredAtUtc = now, Status = StatusPending };
            e.Apply(plan);
            return e;
        }

        public void Apply(PlutoVariantABPlan plan)
        {
            ThemeHint = plan.ThemeHint ?? "";
            ChampionHook = plan.ChampionA.HookStyle ?? "";
            ChampionLook = plan.ChampionA.ColorLookId ?? "";
            ChampionBgm = plan.ChampionA.BgmMood ?? "";
            ChallengerHook = plan.ChallengerB.HookStyle ?? "";
            ChallengerLook = plan.ChallengerB.ColorLookId ?? "";
            ChallengerBgm = plan.ChallengerB.BgmMood ?? "";
            ChampionLabel = plan.ChampionA.VariantLabel ?? "";
            ChallengerLabel = plan.ChallengerB.VariantLabel ?? "";
            ChampionPromptSuffix = plan.ChampionA.PromptSuffix ?? "";
            ChallengerPromptSuffix = plan.ChallengerB.PromptSuffix ?? "";
            HypothesisSummary = plan.HypothesisSummary ?? "";
            ChampionSubtitles = plan.ChampionA.Subtitles is { Count: > 0 } sa ? new List<PromptFlowStudio.Services.Export.SubtitleCue>(sa) : null;
            ChallengerSubtitles = plan.ChallengerB.Subtitles is { Count: > 0 } sb ? new List<PromptFlowStudio.Services.Export.SubtitleCue>(sb) : null;
            if (!string.IsNullOrWhiteSpace(plan.ContextRenderMode)) RenderMode = plan.ContextRenderMode.Trim();   // 빈 값 → 기존 기록 유지(재시작 후 ToPlan 왕복 보존)
            if (NormalizeTags(plan.ContextStyleTags) is { } tags) StyleTags = tags;
        }
    }
}
