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
    double ThreeSecondHookRetentionPct, double AverageCompletionPct, double ClickThroughRatePct, DateTimeOffset MeasuredAtUtc);

/// <summary>📈 폐루프 강화 결과 — 돌파 성공(패턴 부스트 + XP) · 오답 노트 벌점 기록 여부 · XP 증분 · 패턴 배수 · 요약.</summary>
public sealed record PlutoReinforcementOutcome(bool IsBreakoutSuccess, bool RecordedMistakePenalty, int XpDelta, double PatternWeightMultiplier, string SummaryMessage)
{
    /// <summary>같은 영상 중복 반영 차단(alreadyRegistered).</summary>
    public bool AlreadyRegistered { get; init; }
    /// <summary>오답 노트 벌점 강도(0 = 미기록).</summary>
    public double PenaltySeverity { get; init; }
}

/// <summary>📈 Pluto 폐루프 성과 강화 — 게시 후 지표 → 학습 메모리(패턴 가중치 · XP) / 오답 노트(약한 훅 회피).</summary>
public interface IPlutoClosedLoopReinforcementService
{
    /// <summary>성과 반영(동일 플랫폼+영상 ID 재반영 차단 · 실패 시 [부분 구제] 결과 반환 · 취소만 전파).</summary>
    Task<PlutoReinforcementOutcome> IngestPerformanceAsync(PlutoVideoPerformanceTelemetry telemetry, CancellationToken ct = default);

    /// <summary>마지막 반영 결과(없음 → null).</summary>
    PlutoReinforcementOutcome? LastOutcome { get; }

    /// <summary>반영 통지(백그라운드 스레드 가능).</summary>
    event Action? Changed;
}

/// <summary>
/// 📈 <see cref="IPlutoClosedLoopReinforcementService"/> 구현.
///   ▸ 돌파: 3초 유지율 ≥ 65 % · CTR ≥ 7.5 % · 완주율 ≥ 55 % 중 하나 → 훅/스타일/LUT/BGM 패턴 ×1.15~1.35(상한 2.5) + XP +15~35 · 오답 노트 벌점 상쇄.
///   ▸ 부진: 3초 유지율 &lt; 42 % 또는 CTR &lt; 2.2 % → 훅 스타일 + 스타일 태그를 결손 비례 벌점(1.0~2.0)으로 오답 노트 기록(14일 반감기 · 1.8 차단).
///   ▸ 돌파 판정 우선(혼합 신호에서 승리 패턴을 벌하지 않음). 반영 장부 <c>pluto_reinforcement_ledger.json</c>(원자 기록)로 중복 반영 차단.
/// </summary>
public sealed class PlutoClosedLoopReinforcementService : IPlutoClosedLoopReinforcementService, IDisposable
{
    public const string LedgerFileName = "pluto_reinforcement_ledger.json";
    public const string XpSourceKey = "xp:closed-loop";
    public const double BreakoutHookPct = 65.0, BreakoutCtrPct = 7.5, BreakoutCompletionPct = 55.0;
    public const double PoorHookPct = 42.0, PoorCtrPct = 2.2;
    public const double MinBoost = 1.15, MaxBoost = 1.35;
    public const int MinXp = 15, MaxXp = 35;
    public const double MinPenalty = 1.0, MaxPenalty = 2.0;
    public const int LedgerCapacity = 500;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly PlutoLearningService? _learning;
    private readonly IPlutoMistakeNotebookService? _notebook;
    private readonly IPlutoStorageManager? _storage;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<string> _ledger = new(32);
    private readonly HashSet<string> _ledgerSet = new(StringComparer.OrdinalIgnoreCase);
    private bool _ledgerLoaded;
    private volatile PlutoReinforcementOutcome? _last;
    private int _disposed;

    public PlutoClosedLoopReinforcementService(PlutoLearningService? learning, IPlutoMistakeNotebookService? notebook, IPlutoStorageManager? storage = null)
    {
        _learning = learning;
        _notebook = notebook;
        _storage = storage;
    }

    public event Action? Changed;

    public PlutoReinforcementOutcome? LastOutcome => _last;

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
        var poor = !breakout && (hook < PoorHookPct || ctr < PoorCtrPct);
        var deficit = 0.0;
        if (poor)
        {
            if (hook < PoorHookPct) deficit = Math.Max(deficit, (PoorHookPct - hook) / PoorHookPct);
            if (ctr < PoorCtrPct) deficit = Math.Max(deficit, (PoorCtrPct - ctr) / PoorCtrPct);
            deficit = Math.Clamp(deficit, 0.0, 1.0);
        }
        return (breakout, strength, poor, deficit);
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

    /// <summary>오답 노트 기록 토큰 — 훅 스타일 + 스타일 태그(원문 · 중복 제외).</summary>
    public static IReadOnlyList<string> MistakeTokens(PlutoVideoPerformanceTelemetry t)
    {
        var list = new List<string>(1 + (t.StyleTags?.Count ?? 0));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Add(list, seen, "", t.HookStyle);
        if (t.StyleTags is not null) for (var i = 0; i < t.StyleTags.Count; i++) Add(list, seen, "", t.StyleTags[i]);
        return list;
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
            var warn = "";

            if (breakout)
            {
                mult = BoostFor(strength);
                xp = XpFor(strength);
                try
                {
                    if (_learning is not null) await _learning.ApplyPatternReinforcementAsync(PatternTokens(telemetry), mult, XpSourceKey, xp, ct).ConfigureAwait(false);
                    else { xp = 0; warn = " · ⚠ 학습 메모리 미연결"; }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.ClosedLoop] boost: " + ex.Message); xp = 0; warn = " · ⚠ 학습 기록 실패"; }   // [부분 구제]
                try
                {
                    if (_notebook is not null) await _notebook.RecordPositiveReliefAsync(MistakeTokens(telemetry), ct: ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.ClosedLoop] relief: " + ex.Message); }
            }
            else if (poor)
            {
                penalty = PenaltyFor(deficit);
                try
                {
                    if (_notebook is not null)
                        await _notebook.RecordNegativeFeedbackAsync(MistakeTokens(telemetry), penalty, "closed-loop: " + telemetry.Platform + " " + telemetry.VideoId.Trim(), ct).ConfigureAwait(false);
                    else { penalty = 0; warn = " · ⚠ 오답 노트 미연결"; }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.ClosedLoop] penalty: " + ex.Message); penalty = 0; warn = " · ⚠ 오답 노트 기록 실패"; }
            }

            var outcome = new PlutoReinforcementOutcome(breakout, penalty > 0, xp, mult, Summarize(telemetry, breakout, poor, xp, mult, penalty) + warn) { PenaltySeverity = penalty };
            await AppendLedgerAsync(key, ct).ConfigureAwait(false);
            return Publish(outcome);
        }
        finally { _gate.Release(); }
    }

    private static string Summarize(PlutoVideoPerformanceTelemetry t, bool breakout, bool poor, int xp, double mult, double penalty)
    {
        var sb = new StringBuilder(160);
        sb.Append("3초 ").Append(Pct(t.ThreeSecondHookRetentionPct).ToString("0.#", Inv)).Append("% · CTR ").Append(Pct(t.ClickThroughRatePct).ToString("0.#", Inv))
          .Append("% · 완주 ").Append(Pct(t.AverageCompletionPct).ToString("0.#", Inv)).Append("% → ");
        if (breakout) sb.Append("🚀 돌파 성공 · 패턴 ×").Append(mult.ToString("0.00", Inv)).Append(" · +").Append(xp.ToString(Inv)).Append(" XP");
        else if (poor) sb.Append("🛡️ 부진 · 오답 노트 벌점 ").Append(penalty.ToString("0.00", Inv)).Append(" (훅 '").Append(t.HookStyle?.Trim() ?? "").Append("' 회피 학습)");
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
            if (string.IsNullOrWhiteSpace(json)) return;
            var arr = JsonSerializer.Deserialize<List<string>>(json);
            if (arr is null) return;
            foreach (var k in arr)
                if (!string.IsNullOrWhiteSpace(k) && _ledgerSet.Add(k)) _ledger.Add(k);
        }
        catch (OperationCanceledException) { _ledgerLoaded = false; throw; }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or System.IO.IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.ClosedLoop] ledger load: " + ex.Message);   // [부분 구제] 빈 장부
        }
    }

    private async Task AppendLedgerAsync(string key, CancellationToken ct)
    {
        if (_ledgerSet.Add(key)) _ledger.Add(key);
        while (_ledger.Count > LedgerCapacity) { _ledgerSet.Remove(_ledger[0]); _ledger.RemoveAt(0); }
        if (_storage is null) return;
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
