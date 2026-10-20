using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace PromptFlowStudio.Services.Telemetry;

/// <summary>v16.18.0 과금 대상 서비스. ⚠ 신규 멤버는 끝에 추가(영구 원장 직렬화값 불변).</summary>
public enum CostService { DashScope, SiliconFlow, WanCustom, Gemini, ElevenLabs }

/// <summary>v16.18.0 과금 단위.</summary>
public enum CostUnit { Seconds, Tokens, Characters, Requests }

/// <summary>v16.18.0 원장 1건 — 시각 · 서비스 · 단위 수량 · 토큰(입/출) · USD · 모델 · 태그(생산 스코프).</summary>
public readonly record struct CostEntry(
    DateTimeOffset At, CostService Service, CostUnit Unit, double Quantity, long InputTokens, long OutputTokens,
    double Usd, string? Model, string? Tag);

/// <summary>v16.18.0 생산 1건(배치 작업 · 스코프 종료 시 기록) — 테마/플랫폼별 비용 효율 지표의 원천.</summary>
public readonly record struct ProductionRecord(
    DateTimeOffset At, string Title, string? Theme, string Platform, bool Ok, double Seconds, double Usd, int Clips);

/// <summary>v16.18.0 요금표(USD · 사용자 편집 가능 · 기본값은 공개 단가 근사).</summary>
public sealed class CostRateCard
{
    public double DashScopePlusPerSecond { get; set; } = 0.10;
    public double DashScopeTurboPerSecond { get; set; } = 0.036;
    public double SiliconFlowPerVideo { get; set; } = 0.29;
    public double CustomWanPerSecond { get; set; } = 0.10;
    public double GeminiInputPerMTokens { get; set; } = 0.30;
    public double GeminiOutputPerMTokens { get; set; } = 2.50;
    public double ElevenLabsPer1kChars { get; set; } = 0.30;
}

/// <summary>v16.18.0 예산 한도(0 = 무제한).</summary>
public sealed class CostBudget
{
    public double DailyCapUsd { get; set; }
    public double MonthlyCapUsd { get; set; }
    public bool Enforce { get; set; } = true;
    /// <summary>경고 임계(한도 대비 비율).</summary>
    public double WarnRatio { get; set; } = 0.8;
}

/// <summary>v16.18.0 예산 상태.</summary>
public enum BudgetState { Ok, Warning, Exceeded }

/// <summary>v16.18.0 그룹 집계(플랫폼/테마/서비스).</summary>
public readonly record struct CostBucket(string Key, int Count, int Succeeded, double Usd, double Seconds, int Clips)
{
    public double UsdPerSuccess => Succeeded > 0 ? Usd / Succeeded : 0;
    public double SuccessRate => Count > 0 ? Succeeded / (double)Count : 0;
}

/// <summary>v16.18.0 생산 스코프 — AsyncLocal 로 하위 호출(Wan 제출 · Gemini · TTS)의 비용을 작업에 귀속.</summary>
public sealed class CostScope : IDisposable
{
    private readonly ProductionCostTrackerService _owner;
    private readonly CostScope? _parent;
    private readonly long _started = Environment.TickCount64;
    private double _usd;
    private int _clips;
    private int _done;

    internal CostScope(ProductionCostTrackerService owner, CostScope? parent, string title, string? theme, string platform)
    {
        _owner = owner; _parent = parent; Title = title; Theme = theme; Platform = platform;
    }

    public string Title { get; }
    public string? Theme { get; }
    public string Platform { get; }
    public double Usd => Volatile.Read(ref _usd);
    public int Clips => Volatile.Read(ref _clips);
    public bool Succeeded { get; set; }

    internal void Add(double usd, bool clip)
    {
        double cur, next;
        do { cur = Volatile.Read(ref _usd); next = cur + usd; }
        while (Interlocked.CompareExchange(ref _usd, next, cur) != cur);
        if (clip) Interlocked.Increment(ref _clips);
    }

    /// <summary>스코프 종료 → 생산 기록(1회) + 이전 스코프 복원.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _done, 1) != 0) return;
        _owner.EndScope(this, _parent, (Environment.TickCount64 - _started) / 1000.0);
    }
}

/// <summary>
/// [PromptFlowStudio] v16.18.0 💰 AI API 비용 &amp; 토큰 실시간 추적기 — Wan 2.1(DashScope/SiliconFlow/Custom) · Gemini · ElevenLabs(순수 BCL).
///   ▸ 기록: 호출 지점(WanApiService 제출 성공 · GeminiClient usageMetadata · ElevenLabs 합성 성공)이 단위 수량(초/토큰/문자/REQ)을 보고 → 요금표로 USD 환산 → 원장.
///   ▸ 귀속: <see cref="BeginScope"/>(AsyncLocal) 로 배치 작업 하위 호출 비용을 작업/테마/플랫폼에 귀속 → 종료 시 <see cref="ProductionRecord"/>.
///   ▸ 예산: 일간/월간 한도 도달 시 <see cref="IsVideoGenerationBlocked"/>=true → Wan 신규 제출 거부 · 배치 큐 자동 일시정지(<see cref="OnBudgetExceeded"/>).
///     한도 상향/해제 또는 날짜·월 경계 통과 시 자동 해제(<see cref="OnBudgetRecovered"/>).
///   ▸ 영구 저장: %LocalAppData%\PromptFlowStudio\Analytics\cost_ledger.json(원자적 교체 · 변경 후 2초 지연 일괄 기록 · Dispose 시 즉시).
///     원장 10,000건 · 생산 2,000건 · 일별 합계 400일 상한(초과 시 오래된 것부터 제거).
///   ▸ 기록 경로는 잠금 1회 + 값형 엔트리(LINQ 0) · 구독자 예외 격리.
/// </summary>
public sealed class ProductionCostTrackerService : IDisposable
{
    public const int MaxEntries = 10_000;
    public const int MaxProductions = 2_000;
    public const int MaxDays = 400;
    public const double DefaultClipSeconds = 5;
    private static readonly TimeSpan FlushDelay = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private readonly object _gate = new();
    private readonly string? _path;
    private readonly Func<DateTimeOffset> _clock;
    private readonly List<CostEntry> _entries = new(256);
    private readonly List<ProductionRecord> _productions = new(64);
    private readonly SortedDictionary<int, double> _daily = new();   // yyyyMMdd(로컬) → USD
    private readonly double[] _byService = new double[5];
    private readonly long[] _countByService = new long[5];
    private readonly AsyncLocal<CostScope?> _scope = new();
    private readonly System.Threading.Timer _flushTimer;
    private CostRateCard _rates = new();
    private CostBudget _budget = new();
    private BudgetState _state;
    private long _clips;
    private long _blockedRequests;
    private int _dirty;
    private int _disposed;

    /// <summary>DI 기본 — 영구 원장 경로 사용.</summary>
    public ProductionCostTrackerService() : this(DefaultPath(), null) { }

    /// <param name="path">원장 파일(null = 메모리 전용 · 테스트).</param>
    /// <param name="clock">시계(테스트 주입).</param>
    public ProductionCostTrackerService(string? path, Func<DateTimeOffset>? clock)
    {
        _path = path;
        _clock = clock ?? (static () => DateTimeOffset.Now);
        _flushTimer = new System.Threading.Timer(OnFlushTick, null, Timeout.Infinite, Timeout.Infinite);
        Load();
        lock (_gate) _state = EvaluateLocked();
    }

    public static string DefaultPath() => Path.Combine(Account.LicenseService.DataAreaRoot(), "Analytics", "cost_ledger.json");

    /// <summary>원장/예산/요금표 변경(백그라운드 스레드 가능 · 컴포넌트는 InvokeAsync 로 마샬링).</summary>
    public event Action? OnChange;
    /// <summary>예산 한도 도달(상태 전이 시 1회).</summary>
    public event Action<string>? OnBudgetExceeded;
    /// <summary>예산 한도 해제(상향/경계 통과).</summary>
    public event Action? OnBudgetRecovered;

    public string? LedgerPath => _path;
    public BudgetState State { get { lock (_gate) return _state; } }
    public bool IsVideoGenerationBlocked => State == BudgetState.Exceeded;
    public long BlockedRequests => Interlocked.Read(ref _blockedRequests);
    public long TotalClips => Interlocked.Read(ref _clips);
    public CostScope? CurrentScope => _scope.Value;

    public CostRateCard Rates { get { lock (_gate) return Clone(_rates); } }
    public CostBudget Budget { get { lock (_gate) return Clone(_budget); } }

    // ── 순수 단가 계산(테스트 대상) ──

    /// <summary>Wan 제출 1건 USD — DashScope 는 모델(turbo/plus) 초당 단가 · SiliconFlow 는 건당 · Custom 은 초당.</summary>
    public static double WanUsd(CostRateCard r, CostService service, string? model, double seconds) => service switch
    {
        CostService.SiliconFlow => r.SiliconFlowPerVideo,
        CostService.WanCustom => r.CustomWanPerSecond * Math.Max(0, seconds),
        _ => (model is not null && model.Contains("turbo", StringComparison.OrdinalIgnoreCase) ? r.DashScopeTurboPerSecond : r.DashScopePlusPerSecond) * Math.Max(0, seconds),
    };

    public static double GeminiUsd(CostRateCard r, long input, long output)
        => Math.Max(0, input) / 1_000_000.0 * r.GeminiInputPerMTokens + Math.Max(0, output) / 1_000_000.0 * r.GeminiOutputPerMTokens;

    public static double ElevenLabsUsd(CostRateCard r, long chars) => Math.Max(0, chars) / 1000.0 * r.ElevenLabsPer1kChars;

    /// <summary>WanTokenVault 프로바이더 키 → 과금 서비스.</summary>
    public static CostService ServiceOfProvider(string? provider) => provider switch
    {
        "SiliconFlow" => CostService.SiliconFlow,
        "Custom" => CostService.WanCustom,
        _ => CostService.DashScope,
    };

    public static string LabelOf(CostService s) => s switch
    {
        CostService.DashScope => "Wan 2.1 · DashScope",
        CostService.SiliconFlow => "Wan 2.1 · SiliconFlow",
        CostService.WanCustom => "Wan 2.1 · Custom",
        CostService.Gemini => "Gemini",
        _ => "ElevenLabs",
    };

    // ── 기록 ──

    /// <summary>Wan 영상 제출 성공 1건 기록(클립 수 +1).</summary>
    public CostEntry RecordWanSubmit(string? provider, string? model, double seconds = DefaultClipSeconds)
    {
        var svc = ServiceOfProvider(provider);
        CostRateCard r; lock (_gate) r = _rates;
        var unit = svc == CostService.SiliconFlow ? CostUnit.Requests : CostUnit.Seconds;
        var qty = svc == CostService.SiliconFlow ? 1 : Math.Max(0, seconds);
        return Record(svc, unit, qty, 0, 0, WanUsd(r, svc, model, seconds), model, clip: true);
    }

    public CostEntry RecordGemini(string? model, long inputTokens, long outputTokens)
    {
        CostRateCard r; lock (_gate) r = _rates;
        return Record(CostService.Gemini, CostUnit.Tokens, inputTokens + outputTokens, inputTokens, outputTokens,
            GeminiUsd(r, inputTokens, outputTokens), model, clip: false);
    }

    public CostEntry RecordElevenLabs(long characters, string? voice = null)
    {
        CostRateCard r; lock (_gate) r = _rates;
        return Record(CostService.ElevenLabs, CostUnit.Characters, characters, 0, 0, ElevenLabsUsd(r, characters), voice, clip: false);
    }

    private CostEntry Record(CostService svc, CostUnit unit, double qty, long inTok, long outTok, double usd, string? model, bool clip)
    {
        var scope = _scope.Value;
        var e = new CostEntry(_clock(), svc, unit, qty, inTok, outTok, Math.Round(usd, 6), model, scope?.Title);
        scope?.Add(e.Usd, clip);
        if (clip) Interlocked.Increment(ref _clips);
        BudgetState before, after;
        lock (_gate)
        {
            _entries.Add(e);
            if (_entries.Count > MaxEntries) _entries.RemoveRange(0, _entries.Count - MaxEntries);
            var day = DayKey(e.At);
            _daily[day] = (_daily.TryGetValue(day, out var d) ? d : 0) + e.Usd;
            TrimDaysLocked();
            _byService[(int)svc] += e.Usd;
            _countByService[(int)svc]++;
            before = _state;
            after = _state = EvaluateLocked();
        }
        MarkDirty();
        Notify(before, after);
        return e;
    }

    /// <summary>예산 차단 시 신규 영상 요청 거부 사유(null = 허용). 거부 시 차단 카운터 증가.</summary>
    public string? CheckVideoAllowed()
    {
        BudgetState before, after;
        lock (_gate) { before = _state; after = _state = EvaluateLocked(); }   // 날짜/월 경계 통과 시 자동 해제
        Notify(before, after);
        if (after != BudgetState.Exceeded) return null;
        Interlocked.Increment(ref _blockedRequests);
        return "💸 예산 한도 도달 — 신규 영상 생성이 일시정지되었습니다(오늘 $" + Usd(TodayUsd) + " / 이번 달 $" + Usd(MonthUsd) + "). 대시보드에서 한도를 조정하세요.";
    }

    // ── 생산 스코프 ──

    /// <summary>생산 스코프 시작 — 같은 비동기 흐름의 하위 호출 비용이 귀속된다. Dispose 시 <see cref="ProductionRecord"/> 기록.</summary>
    public CostScope BeginScope(string title, string? theme, string platform)
    {
        var parent = _scope.Value;
        var s = new CostScope(this, parent, string.IsNullOrWhiteSpace(title) ? "(제목 없음)" : title, theme, string.IsNullOrWhiteSpace(platform) ? "None" : platform);
        _scope.Value = s;
        return s;
    }

    internal void EndScope(CostScope s, CostScope? parent, double seconds)
    {
        if (ReferenceEquals(_scope.Value, s)) _scope.Value = parent;
        var rec = new ProductionRecord(_clock(), s.Title, s.Theme, s.Platform, s.Succeeded, Math.Round(seconds, 1), Math.Round(s.Usd, 6), s.Clips);
        lock (_gate)
        {
            _productions.Add(rec);
            if (_productions.Count > MaxProductions) _productions.RemoveRange(0, _productions.Count - MaxProductions);
        }
        MarkDirty();
        Notify(BudgetState.Ok, BudgetState.Ok);
    }

    // ── 설정 ──

    public void UpdateBudget(double dailyCapUsd, double monthlyCapUsd, bool enforce)
    {
        BudgetState before, after;
        lock (_gate)
        {
            _budget.DailyCapUsd = Math.Max(0, dailyCapUsd);
            _budget.MonthlyCapUsd = Math.Max(0, monthlyCapUsd);
            _budget.Enforce = enforce;
            before = _state;
            after = _state = EvaluateLocked();
        }
        MarkDirty();
        Notify(before, after);
    }

    public void UpdateRates(CostRateCard rates)
    {
        ArgumentNullException.ThrowIfNull(rates);
        lock (_gate) _rates = Clone(rates);
        MarkDirty();
        Notify(BudgetState.Ok, BudgetState.Ok);
    }

    // ── 조회 ──

    public double TodayUsd { get { lock (_gate) return _daily.TryGetValue(DayKey(_clock()), out var d) ? d : 0; } }

    public double MonthUsd
    {
        get
        {
            var now = _clock();
            var lo = now.Year * 10000 + now.Month * 100;
            double sum = 0;
            lock (_gate) foreach (var kv in _daily) if (kv.Key > lo && kv.Key < lo + 100) sum += kv.Value;
            return sum;
        }
    }

    public double TotalUsd { get { lock (_gate) { double s = 0; foreach (var v in _byService) s += v; return s; } } }

    public double ServiceUsd(CostService s) { lock (_gate) return _byService[(int)s]; }
    public long ServiceCalls(CostService s) { lock (_gate) return _countByService[(int)s]; }

    public int ProductionCount { get { lock (_gate) return _productions.Count; } }

    public IReadOnlyList<CostEntry> RecentEntries(int max)
    {
        lock (_gate)
        {
            var n = Math.Clamp(max, 0, _entries.Count);
            var list = new List<CostEntry>(n);
            for (var i = _entries.Count - 1; i >= _entries.Count - n; i--) list.Add(_entries[i]);
            return list;
        }
    }

    public IReadOnlyList<ProductionRecord> Productions { get { lock (_gate) return _productions.ToArray(); } }

    /// <summary>최근 N일 일별 비용(오래된 → 최신 · 빈 날 0).</summary>
    public double[] DailySeries(int days)
    {
        var n = Math.Clamp(days, 1, MaxDays);
        var arr = new double[n];
        var today = _clock().Date;
        lock (_gate)
            for (var i = 0; i < n; i++)
                arr[n - 1 - i] = _daily.TryGetValue(DayKey(today.AddDays(-i)), out var v) ? v : 0;
        return arr;
    }

    /// <summary>플랫폼별(byTheme=false) 또는 테마별 생산 비용 효율 집계(USD 내림차순).</summary>
    public List<CostBucket> Buckets(bool byTheme)
    {
        var map = new Dictionary<string, CostBucket>(StringComparer.Ordinal);
        lock (_gate)
            foreach (var p in _productions)
            {
                var key = byTheme ? (string.IsNullOrWhiteSpace(p.Theme) ? "(테마 없음)" : p.Theme!) : p.Platform;
                map.TryGetValue(key, out var b);
                map[key] = new CostBucket(key, b.Count + 1, b.Succeeded + (p.Ok ? 1 : 0), b.Usd + p.Usd, b.Seconds + p.Seconds, b.Clips + p.Clips);
            }
        var list = new List<CostBucket>(map.Count);
        foreach (var kv in map) list.Add(kv.Value);
        list.Sort(static (a, b) => b.Usd.CompareTo(a.Usd));
        return list;
    }

    // ── 리포트 ──

    /// <summary>CSV 리포트(원장 + 생산 섹션 · UTF-8 BOM · RFC4180 인용). 반환: 기록 행 수.</summary>
    public int ExportCsv(string path)
    {
        var sb = new StringBuilder(4096);
        sb.Append("section,at,service_or_title,unit_or_theme,quantity_or_platform,input_tokens,output_tokens,usd,model_or_ok,tag_or_seconds,clips\r\n");
        int rows = 0;
        lock (_gate)
        {
            foreach (var e in _entries)
            {
                sb.Append("cost,").Append(e.At.ToString("o", CultureInfo.InvariantCulture)).Append(',')
                  .Append(LabelOf(e.Service)).Append(',').Append(e.Unit.ToString()).Append(',')
                  .Append(e.Quantity.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                  .Append(e.InputTokens.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(e.OutputTokens.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(e.Usd.ToString("0.######", CultureInfo.InvariantCulture)).Append(',');
                Csv(sb, e.Model); sb.Append(','); Csv(sb, e.Tag); sb.Append(",\r\n");
                rows++;
            }
            foreach (var p in _productions)
            {
                sb.Append("production,").Append(p.At.ToString("o", CultureInfo.InvariantCulture)).Append(',');
                Csv(sb, p.Title); sb.Append(','); Csv(sb, p.Theme); sb.Append(','); Csv(sb, p.Platform);
                sb.Append(",,,").Append(p.Usd.ToString("0.######", CultureInfo.InvariantCulture)).Append(',')
                  .Append(p.Ok ? "true" : "false").Append(',')
                  .Append(p.Seconds.ToString("0.#", CultureInfo.InvariantCulture)).Append(',')
                  .Append(p.Clips.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                rows++;
            }
        }
        WriteAtomic(path, sb.ToString(), bom: true);
        return rows;
    }

    /// <summary>JSON 리포트(요약 + 서비스별 + 플랫폼/테마 집계 + 원장 + 생산).</summary>
    public void ExportJson(string path)
    {
        var services = new List<object>(5);
        for (var i = 0; i < _byService.Length; i++)
        {
            var s = (CostService)i;
            services.Add(new { Service = LabelOf(s), Usd = Math.Round(ServiceUsd(s), 6), Calls = ServiceCalls(s) });
        }
        CostEntry[] entries; ProductionRecord[] prods; CostBudget budget; CostRateCard rates;
        lock (_gate) { entries = _entries.ToArray(); prods = _productions.ToArray(); budget = Clone(_budget); rates = Clone(_rates); }
        var report = new
        {
            GeneratedAt = _clock(),
            Summary = new
            {
                TotalUsd = Math.Round(TotalUsd, 6), TodayUsd = Math.Round(TodayUsd, 6), MonthUsd = Math.Round(MonthUsd, 6),
                Productions = prods.Length, Clips = TotalClips, BlockedRequests, State = State.ToString(),
            },
            Budget = budget, Rates = rates, Services = services,
            ByPlatform = Buckets(false), ByTheme = Buckets(true),
            Entries = entries, Productions = prods,
        };
        WriteAtomic(path, JsonSerializer.Serialize(report, Json), bom: false);
    }

    // ── 내부 ──

    private BudgetState EvaluateLocked()
    {
        if (!_budget.Enforce) return BudgetState.Ok;
        var now = _clock();
        var today = _daily.TryGetValue(DayKey(now), out var t) ? t : 0;
        double month = 0;
        var lo = now.Year * 10000 + now.Month * 100;
        foreach (var kv in _daily) if (kv.Key > lo && kv.Key < lo + 100) month += kv.Value;
        var dr = _budget.DailyCapUsd > 0 ? today / _budget.DailyCapUsd : 0;
        var mr = _budget.MonthlyCapUsd > 0 ? month / _budget.MonthlyCapUsd : 0;
        var ratio = Math.Max(dr, mr);
        if (ratio >= 1) return BudgetState.Exceeded;
        return ratio >= _budget.WarnRatio ? BudgetState.Warning : BudgetState.Ok;
    }

    private void Notify(BudgetState before, BudgetState after)
    {
        if (after == BudgetState.Exceeded && before != BudgetState.Exceeded)
        {
            var msg = "💸 예산 한도 도달 — 오늘 $" + Usd(TodayUsd) + " / 이번 달 $" + Usd(MonthUsd);
            try { OnBudgetExceeded?.Invoke(msg); } catch (Exception ex) { Program.Log("CostTracker.OnBudgetExceeded", ex); }
        }
        else if (before == BudgetState.Exceeded && after != BudgetState.Exceeded)
        {
            try { OnBudgetRecovered?.Invoke(); } catch (Exception ex) { Program.Log("CostTracker.OnBudgetRecovered", ex); }
        }
        try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("CostTracker.OnChange", ex); }
    }

    private void TrimDaysLocked()
    {
        while (_daily.Count > MaxDays)
        {
            using var en = _daily.GetEnumerator();
            if (!en.MoveNext()) break;
            _daily.Remove(en.Current.Key);
        }
    }

    private static int DayKey(DateTimeOffset at) => at.Year * 10000 + at.Month * 100 + at.Day;
    private static int DayKey(DateTime at) => at.Year * 10000 + at.Month * 100 + at.Day;

    public static string Usd(double v) => v.ToString(v < 1 ? "0.000" : "0.00", CultureInfo.InvariantCulture);

    private static void Csv(StringBuilder sb, string? v)
    {
        if (string.IsNullOrEmpty(v)) return;
        var needs = v.AsSpan().IndexOfAny(",\"\r\n") >= 0;
        // 스프레드시트 수식 주입 차단(=,+,-,@ 시작 셀은 ' 접두).
        var formula = v[0] is '=' or '+' or '-' or '@';
        if (!needs && !formula) { sb.Append(v); return; }
        sb.Append('"');
        if (formula) sb.Append('\'');
        foreach (var c in v) { if (c == '"') sb.Append('"'); sb.Append(c); }
        sb.Append('"');
    }

    private static void WriteAtomic(string path, string content, bool bom)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(bom));
        File.Move(tmp, path, overwrite: true);
    }

    private static CostRateCard Clone(CostRateCard r) => new()
    {
        DashScopePlusPerSecond = r.DashScopePlusPerSecond, DashScopeTurboPerSecond = r.DashScopeTurboPerSecond,
        SiliconFlowPerVideo = r.SiliconFlowPerVideo, CustomWanPerSecond = r.CustomWanPerSecond,
        GeminiInputPerMTokens = r.GeminiInputPerMTokens, GeminiOutputPerMTokens = r.GeminiOutputPerMTokens,
        ElevenLabsPer1kChars = r.ElevenLabsPer1kChars,
    };

    private static CostBudget Clone(CostBudget b) => new() { DailyCapUsd = b.DailyCapUsd, MonthlyCapUsd = b.MonthlyCapUsd, Enforce = b.Enforce, WarnRatio = b.WarnRatio };

    // ── 영구 저장 ──

    private sealed class LedgerFile
    {
        public int Version { get; set; } = 1;
        public CostRateCard? Rates { get; set; }
        public CostBudget? Budget { get; set; }
        public Dictionary<string, double>? Daily { get; set; }
        public double[]? ByService { get; set; }
        public long[]? CallsByService { get; set; }
        public long Clips { get; set; }
        public List<CostEntry>? Entries { get; set; }
        public List<ProductionRecord>? Productions { get; set; }
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var f = JsonSerializer.Deserialize<LedgerFile>(File.ReadAllText(_path), Json);
            if (f is null) return;
            lock (_gate)
            {
                if (f.Rates is not null) _rates = f.Rates;
                if (f.Budget is not null) _budget = f.Budget;
                if (f.Entries is not null) _entries.AddRange(f.Entries);
                if (f.Productions is not null) _productions.AddRange(f.Productions);
                if (f.Daily is not null)
                    foreach (var kv in f.Daily)
                        if (int.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var k) && double.IsFinite(kv.Value)) _daily[k] = kv.Value;
                if (f.ByService is { Length: > 0 }) Array.Copy(f.ByService, _byService, Math.Min(f.ByService.Length, _byService.Length));
                if (f.CallsByService is { Length: > 0 }) Array.Copy(f.CallsByService, _countByService, Math.Min(f.CallsByService.Length, _countByService.Length));
                TrimDaysLocked();
            }
            Interlocked.Exchange(ref _clips, Math.Max(0, f.Clips));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            Program.Log("CostTracker.Load", ex);   // 손상 원장 → 빈 원장으로 시작(원본은 보존)
        }
    }

    private void MarkDirty()
    {
        if (_path is null || Volatile.Read(ref _disposed) != 0) return;
        if (Interlocked.Exchange(ref _dirty, 1) == 0)
            try { _flushTimer.Change(FlushDelay, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
    }

    private void OnFlushTick(object? _)
    {
        try { Flush(); }
        catch (Exception ex) { Program.Log("CostTracker.Flush", ex); }
    }

    /// <summary>변경분을 즉시 디스크에 기록(변경 없으면 무동작).</summary>
    public void Flush()
    {
        if (_path is null || Interlocked.Exchange(ref _dirty, 0) == 0) return;
        LedgerFile f;
        lock (_gate)
        {
            var daily = new Dictionary<string, double>(_daily.Count);
            foreach (var kv in _daily) daily[kv.Key.ToString(CultureInfo.InvariantCulture)] = kv.Value;
            f = new LedgerFile
            {
                Rates = Clone(_rates), Budget = Clone(_budget), Daily = daily,
                ByService = (double[])_byService.Clone(), CallsByService = (long[])_countByService.Clone(),
                Clips = Interlocked.Read(ref _clips),
                Entries = new List<CostEntry>(_entries), Productions = new List<ProductionRecord>(_productions),
            };
        }
        try { WriteAtomic(_path, JsonSerializer.Serialize(f, Json), bom: false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.Log("CostTracker.Save", ex);
            Interlocked.Exchange(ref _dirty, 1);   // 다음 변경 때 재시도
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _flushTimer.Dispose();
        try { Flush(); } catch (Exception ex) { Program.Log("CostTracker.Dispose", ex); }
    }
}
