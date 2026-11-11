using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🔄 자율 수확 1회 결과.</summary>
public sealed record PlutoHarvestResult(
    bool Harvested,
    string Reason,
    int XpAwarded,
    string? TopTag,
    int TokenCount,
    bool FromGenerator,
    DateTime? HarvestedUtc);

/// <summary>🔄 Drive 볼트에 남기는 학습 바이럴 패턴 스냅샷(카테고리별 훅/렌즈/조명/페이싱 + 병합 토큰).</summary>
public sealed record PlutoViralPatternSnapshot(
    DateTime HarvestedUtc,
    bool FromGenerator,
    IReadOnlyList<PlutoViralPatternSnapshot.Niche> Niches,
    IReadOnlyList<ViralTrendToken> Tokens)
{
    public sealed record Niche(StudioThemeCategory Category, string HookStructure, string LensAngle, string Lighting, string Pacing);
}

/// <summary>
/// 🔄 Pluto 자율 트렌드 수확기 — 기동 <see cref="DefaultStartupDelay"/> 후 1회 + <see cref="DefaultInterval"/> 주기로 트렌드 메모리 신선도 점검.
/// 최근 적재(수동 발굴 포함)가 <see cref="DefaultStaleAfter"/> 초과 → 아기/반려동물 니치별 생성형 발굴(LLM 페일오버 체인 · 실패 시 카탈로그 폴백)
/// → <see cref="PlutoTrendAnalyzer"/> 토큰 병합(태그별 최고점) → <see cref="PlutoLearningService.IngestViralTrendsAsync"/>(+2 XP · 활성 모디파이어 즉시 갱신)
/// → 패턴 스냅샷 Drive 기록 → <see cref="Changed"/> 통지. 단일 비행(동시 수확 차단) · 루프 예외 격리 · Dispose 시 취소.
/// </summary>
public sealed class PlutoAutonomousTrendHarvester : IDisposable
{
    public const string PatternsFileName = "pluto_viral_patterns.json";
    public static readonly TimeSpan DefaultStartupDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(6);
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromHours(12);
    /// <summary>니치 1건 발굴 상한 — 초과 시 해당 니치는 사전 점수만 사용.</summary>
    public static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(45);

    private static readonly StudioThemeCategory[] Niches = { StudioThemeCategory.BabyParenting, StudioThemeCategory.PetsCompanions };

    private readonly PlutoLearningService _learning;
    private readonly IPlutoStorageManager? _storage;
    private readonly PlutoTrendEngine? _engine;
    private readonly TimeSpan _startupDelay;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _staleAfter;
    private readonly Func<DateTime> _utcNow;
    private readonly CancellationTokenSource _cts = new();
    private int _started;
    private int _busy;
    private int _disposed;
    private volatile PlutoHarvestResult? _last;

    public PlutoAutonomousTrendHarvester(
        PlutoLearningService learning,
        IPlutoStorageManager? storage = null,
        PlutoTrendEngine? engine = null,
        TimeSpan? startupDelay = null,
        TimeSpan? interval = null,
        TimeSpan? staleAfter = null,
        Func<DateTime>? utcNow = null)
    {
        _learning = learning ?? throw new ArgumentNullException(nameof(learning));
        _storage = storage;
        _engine = engine;
        _startupDelay = startupDelay is { } d && d >= TimeSpan.Zero ? d : DefaultStartupDelay;
        _interval = interval is { } i && i > TimeSpan.Zero ? i : DefaultInterval;
        _staleAfter = staleAfter is { } s && s >= TimeSpan.Zero ? s : DefaultStaleAfter;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>상태 변경(수확 시작/완료) 통지 — 백그라운드 스레드.</summary>
    public event Action? Changed;

    public bool IsRunning => Volatile.Read(ref _started) == 1 && Volatile.Read(ref _disposed) == 0;
    public bool IsHarvesting => Volatile.Read(ref _busy) == 1;
    public PlutoHarvestResult? LastResult => _last;
    public TimeSpan Interval => _interval;
    public TimeSpan StaleAfter => _staleAfter;

    /// <summary>최근 트렌드 메모리 갱신 시각(수동 발굴 + 자율 수확 공통).</summary>
    public DateTime? LastHarvestUtc => _learning.LastViralIngestUtc;

    public bool IsStale(DateTime nowUtc) => LastHarvestUtc is not { } last || nowUtc - last > _staleAfter;

    /// <summary>"방금 전" · "N분 전" · "N시간 전" · "N일 전" · 기록 없음 → "기록 없음".</summary>
    public static string FormatTimeAgo(DateTime? whenUtc, DateTime nowUtc)
    {
        if (whenUtc is not { } w) return "기록 없음";
        var d = nowUtc - w;
        if (d < TimeSpan.FromMinutes(1)) return "방금 전";
        if (d < TimeSpan.FromHours(1)) return ((int)d.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "분 전";
        if (d < TimeSpan.FromDays(1)) return ((int)d.TotalHours).ToString(CultureInfo.InvariantCulture) + "시간 전";
        return ((int)d.TotalDays).ToString(CultureInfo.InvariantCulture) + "일 전";
    }

    /// <summary>UI 배지 문구.</summary>
    public string StatusLabel
    {
        get
        {
            if (IsHarvesting) return "🔄 자율 탐색 중…";
            var ago = FormatTimeAgo(LastHarvestUtc, _utcNow());
            return IsRunning ? "🔄 자율 탐색 가동 중 (최근 갱신: " + ago + ")" : "⏸ 자율 탐색 대기 (최근 갱신: " + ago + ")";
        }
    }

    /// <summary>백그라운드 루프 기동(멱등) — 지연 후 1회 점검 → 주기 점검.</summary>
    public void Start()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _ = Task.Run(() => LoopAsync(_cts.Token));
        RaiseChanged();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(_startupDelay, ct).ConfigureAwait(false);
            await TickAsync(ct).ConfigureAwait(false);
            using var timer = new PeriodicTimer(_interval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await TickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        try { await TryHarvestAsync(false, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Harvest] tick failed: " + ex.Message); }
    }

    /// <summary>
    /// 신선도 점검 후 수확 — <paramref name="force"/> = 신선해도 강제. 동시 호출 → "busy" 스킵. 취소만 전파.
    /// </summary>
    public async Task<PlutoHarvestResult> TryHarvestAsync(bool force = false, CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return new PlutoHarvestResult(false, "busy", 0, null, 0, false, LastHarvestUtc);
        var raised = false;
        try
        {
            await _learning.EnsureLoadedAsync(ct).ConfigureAwait(false);
            if (!force && !IsStale(_utcNow()))
                return _last = new PlutoHarvestResult(false, "fresh", 0, null, 0, false, LastHarvestUtc);

            RaiseChanged();
            raised = true;
            var best = new Dictionary<string, ViralTrendToken>(StringComparer.OrdinalIgnoreCase);
            var niches = new List<PlutoViralPatternSnapshot.Niche>(Niches.Length);
            var fromGen = false;
            foreach (var niche in Niches)
            {
                var samples = await DiscoverAsync(niche, ct).ConfigureAwait(false);
                fromGen |= samples is not null && _engine is { CanDiscover: true };
                var a = PlutoTrendAnalyzer.Analyze(niche, samples);
                niches.Add(new PlutoViralPatternSnapshot.Niche(niche, a.HookStructure, a.LensAngle, a.Lighting, a.Pacing));
                foreach (var t in a.Tokens)
                    if (!best.TryGetValue(t.Tag, out var cur) || t.Score > cur.Score) best[t.Tag] = t;
            }

            var tokens = new List<ViralTrendToken>(best.Values);
            tokens.Sort((x, y) => { var c = y.Score.CompareTo(x.Score); return c != 0 ? c : string.CompareOrdinal(x.Tag, y.Tag); });

            var before = _learning.CurrentXP;
            var after = await _learning.IngestViralTrendsAsync(tokens, ct).ConfigureAwait(false);
            var now = _utcNow();

            if (_storage is not null)
            {
                try
                {
                    var json = JsonSerializer.Serialize(new PlutoViralPatternSnapshot(now, fromGen, niches, tokens));
                    await _storage.WriteAllTextAtomicAsync(PatternsFileName, json, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Harvest] snapshot failed: " + ex.Message); }   // [부분 구제] 메모리 적재는 유지
            }

            return _last = new PlutoHarvestResult(true, force ? "forced" : "stale", Math.Max(0, after - before),
                tokens.Count > 0 ? tokens[0].Tag : null, tokens.Count, fromGen, LastHarvestUtc ?? now);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
            if (raised) RaiseChanged();
        }
    }

    /// <summary>니치 발굴 — 엔진 부재/타임아웃/실패 → null(사전 점수만). 외부 취소만 전파.</summary>
    private async Task<IReadOnlyList<PlutoTrend>?> DiscoverAsync(StudioThemeCategory niche, CancellationToken ct)
    {
        if (_engine is null) return null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(DiscoveryTimeout);
        try { return await _engine.DiscoverFreshTrendsAsync(niche, 4, linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.Harvest] discover failed: " + ex.Message);
            return null;
        }
    }

    private void RaiseChanged()
    {
        var h = Changed;
        if (h is null) return;
        foreach (Action a in h.GetInvocationList())
        {
            try { a(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Harvest] Changed handler: " + ex.Message); }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Changed = null;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _cts.Dispose();
    }
}
