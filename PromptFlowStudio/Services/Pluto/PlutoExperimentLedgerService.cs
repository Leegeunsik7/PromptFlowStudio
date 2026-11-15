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

    /// <summary>판정 통지(백그라운드 스레드 가능).</summary>
    event Action? Changed;
}

/// <summary>
/// 🏆 <see cref="IPlutoExperimentLedgerService"/> 구현.
///   ▸ 합성 점수 = 0.50·3초 유지율 + 0.30·완주율 + 0.20·(CTR × 10) · |A−B| &lt; <see cref="TieThreshold"/> → 무승부.
///   ▸ 차등 강화: 승자/패자 간 <b>다른</b> 훅·룩·BGM 만 <see cref="IPlutoClosedLoopReinforcementService.IngestExperimentOutcomeAsync"/> 로 전달.
///   ▸ 장부 <c>pluto_experiment_ledger.json</c> — <see cref="IPlutoStorageManager"/> 원자 기록(Drive → %LOCALAPPDATA% 폴백) · 용량 <see cref="Capacity"/>.
/// </summary>
public sealed class PlutoExperimentLedgerService : IPlutoExperimentLedgerService, IDisposable
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

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly IPlutoClosedLoopReinforcementService? _reinforcement;
    private readonly IPlutoStorageManager? _storage;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly List<Entry> _entries = new(16);
    private bool _loaded;
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
        lock (_sync)
        {
            var e = FindLocked(id);
            if (e is null) { _entries.Add(Entry.From(plan, _utcNow())); TrimLocked(); }
            else if (e.Evaluation is null) e.Apply(plan);
        }
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
            await EnsureLoadedAsync(ct).ConfigureAwait(false);
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
            var request = new PlutoExperimentReinforcementRequest(id, DiffTelemetry(id, "W", win, lose, winM), DiffTelemetry(id, "L", lose, win, loseM), lift, tie);
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

    /// <summary>등록(판정 대기 포함) 실험 수.</summary>
    public int RegisteredCount { get { lock (_sync) return _entries.Count; } }

    /// <summary>장부 선적재(UI 진입 시 최근 판정 표시용 · 실패 → 빈 장부).</summary>
    public async Task EnsureLoadedAsync(CancellationToken ct = default)
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
                    if (e is not null && !string.IsNullOrWhiteSpace(e.ExperimentId) && FindIn(merged, e.ExperimentId) is null) merged.Add(e);
                foreach (var e in _entries)   // 적재 전 메모리 등록분 보존(동일 ID → 메모리 우선)
                {
                    var old = FindIn(merged, e.ExperimentId);
                    if (old is not null) merged.Remove(old);
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

        public static Entry From(PlutoVariantABPlan plan, DateTimeOffset now)
        {
            var e = new Entry { ExperimentId = plan.ExperimentId.Trim(), RegisteredAtUtc = now };
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
        }
    }
}
