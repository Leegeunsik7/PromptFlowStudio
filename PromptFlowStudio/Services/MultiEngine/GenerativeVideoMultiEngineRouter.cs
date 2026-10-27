using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Plugins.Sdk;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Services.MultiEngine;

/// <summary>v16.29.0 라우팅 요청(해상도 · 프레임률 · 길이 · 장르 · 예산 · 지연 한도 · i2v 여부).</summary>
public readonly record struct RoutingRequest(int Width, int Height, int Fps, double DurationSeconds, string Genre, double BudgetUsd, double MaxLatencySeconds, bool HasImage);

/// <summary>v16.29.0 라우팅 가중(UI 슬라이더 0~1).</summary>
public sealed record RoutingWeights(double Quality = 0.5, double Speed = 0.25, double Cost = 0.25);

/// <summary>점수 계산 입력(무할당 값형) — 기준 능력 + 실측 통계.</summary>
public readonly record struct EngineSnapshot(bool Available, double Quality, double SecondsPerClipSecond, double UsdPerSecond, int MaxWidth, int MaxHeight, int MaxFps, double MaxDuration,
    bool TextToVideo, bool ImageToVideo, double SuccessRate, bool GenreMatch);

/// <summary>v16.29.0 엔진 실측 벤치마크(UI 칩).</summary>
public sealed record EngineBenchmark(string Key, string DisplayName, bool Enabled, bool CircuitOpen, double Quality, double SecondsPerClipSecond, double UsdPerSecond,
    double SuccessRate, int Attempts, int Successes, double MeasuredLatencySeconds, string Source, string? LastError);

public sealed record RouteDecision(string EngineKey, double Score, bool Relaxed, IReadOnlyList<string> Ranking, string Reason);

/// <summary>
/// [PromptFlowStudio] v16.29.0 ⚡ 차세대 AI 비디오 멀티 엔진 동적 루터 — Wan 2.1 · Sora 2 · Luma Dream Machine · Runway Gen-3 · 로컬 ComfyUI · 플러그인 엔진 통합.
///   ▸ <see cref="IVideoGenerationEngine"/>(Key "Router") 로 등록 → 엔진 선택기에서 고르면 스토리보드/워크스페이스 생성이 자동 라우팅.
///   ▸ 점수(<see cref="Rank"/> · 무할당 Span): 하드 제약(활성 · 서킷 · t2v/i2v · 길이 · 예산 · 지연 한도) → 소프트(해상도/fps 부족 감점 · 장르 가산)
///     → wq·품질 + ws·(1−지연 정규화) + wc·(1−비용 정규화) × 성공률. 제약을 만족하는 엔진이 없으면 활성 엔진 전체로 완화(Relaxed).
///   ▸ 장애 조치: 순위대로 최대 <see cref="MaxFailover"/>개 시도 · 엔진별 프롬프트는 <see cref="GenerativePromptTranspilerEngine"/> 방언으로 변환
///     · 연속 3회 실패 120초 서킷 · 성공률/지연은 EWMA 실측(기준값을 점진 대체).
///   ▸ 원격 어댑터(순수 BCL HttpClient · 키는 환경변수만): Sora 2(<c>OPENAI_API_KEY</c> · /v1/videos 제출 → 폴링 → content 다운로드),
///     Luma(<c>LUMAAI_API_KEY</c> · dream-machine/v1/generations), Runway(<c>RUNWAYML_API_SECRET</c> · image_to_video · 참조 이미지 필수).
///     제출/폴링 10분 데드라인 · 429/5xx 지수 백오프 · 응답 스키마 즉시 검증. ⚠ 엔드포인트/요금 기준값은 공개 문서 기준 추정치 — 실측으로 보정.
/// </summary>
public sealed class GenerativeVideoMultiEngineRouter : IVideoGenerationEngine, IDisposable
{
    public const int MaxFailover = 3;
    public const int CircuitFailures = 3;
    public static readonly TimeSpan CircuitCooldown = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan JobDeadline = TimeSpan.FromMinutes(10);

    private sealed class Engine
    {
        public required string Key;
        public required string Display;
        public required PromptDialect Dialect;
        public required EngineCapabilities Caps;
        public required Func<bool> Enabled;
        public required Func<WanVideoRequest, IProgress<WanProgress>?, CancellationToken, Task<WanGenerationResult>> Generate;
        public string Source = "기준값";
        public int Attempts, Successes, Failures;
        public double SuccessEwma = 0.9, LatencyEwma;
        public long OpenUntil;
        public string? LastError;
    }

    private readonly object _gate = new();
    private readonly List<Engine> _engines = new();
    private readonly HashSet<string> _disabled = new(StringComparer.Ordinal);
    private readonly GenerativePromptTranspilerEngine _transpiler;
    private readonly PluginExtensionMarketplaceFramework? _plugins;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private int _routed, _failovers;
    private double _spentUsd;

    public GenerativeVideoMultiEngineRouter(IWanApiService? wan = null, ComfyUiVideoEngine? comfy = null, PluginExtensionMarketplaceFramework? plugins = null,
        GenerativePromptTranspilerEngine? transpiler = null)
        : this(transpiler ?? new GenerativePromptTranspilerEngine(), plugins, null)
    {
        if (wan is IVideoGenerationEngine w)
            Register("wan", "Alibaba Wan 2.1", PromptDialect.WanTags, new EngineCapabilities(0.72, 24, 0.10, 1280, 720, 30, 5, true, true, new[] { "anime", "general", "baby", "product" }),
                () => w.Enabled, (r, p, ct) => w.GenerateAsync(r, p, ct));
        if (comfy is not null)
            Register("comfy", "로컬 GPU(ComfyUI)", PromptDialect.WanTags, new EngineCapabilities(0.65, 60, 0, 1280, 720, 24, 5, true, true, new[] { "general" }),
                () => comfy.Enabled, (r, p, ct) => comfy.GenerateAsync(r, p, ct));
        RegisterRemoteAdapters(() => Environment.GetEnvironmentVariable("OPENAI_API_KEY"), () => Environment.GetEnvironmentVariable("LUMAAI_API_KEY"),
            () => Environment.GetEnvironmentVariable("RUNWAYML_API_SECRET"));
        if (plugins is not null) { plugins.OnChange += SyncPlugins; SyncPlugins(); }
    }

    /// <summary>테스트/고급 구성용 — 엔진 0개로 시작(<see cref="Register"/> · <see cref="RegisterRemoteAdapters"/>).</summary>
    public GenerativeVideoMultiEngineRouter(GenerativePromptTranspilerEngine transpiler, PluginExtensionMarketplaceFramework? plugins, HttpMessageHandler? handler)
    {
        _transpiler = transpiler;
        _plugins = plugins;
        _ownsHttp = true;
        _http = handler is null ? new HttpClient { Timeout = TimeSpan.FromMinutes(5) } : new HttpClient(handler, false) { Timeout = TimeSpan.FromMinutes(5) };
    }

    public event Action? OnChange;

    public string Key => "Router";
    public string DisplayName => "⚡ 멀티 엔진 루터(자동)";
    public bool Enabled { get { lock (_gate) foreach (var e in _engines) if (SafeEnabled(e) && !_disabled.Contains(e.Key)) return true; return false; } }

    public RoutingWeights Weights { get; set; } = new();
    /// <summary>고정 엔진(null = 자동).</summary>
    public string? PinnedEngine { get; set; }
    public string DefaultGenre { get; set; } = "general";
    /// <summary>클립당 예산 USD(0 = 무제한).</summary>
    public double BudgetPerClipUsd { get; set; }
    /// <summary>클립당 허용 지연 초(0 = 무제한).</summary>
    public double MaxLatencySeconds { get; set; }
    public int Routed => Volatile.Read(ref _routed);
    public int Failovers => Volatile.Read(ref _failovers);
    public double EstimatedSpendUsd { get { lock (_gate) return _spentUsd; } }
    public RouteDecision? LastDecision { get; private set; }
    public string? LastAction { get; private set; }
    public GenerativePromptTranspilerEngine Transpiler => _transpiler;

    // ══════════════════════ 등록 ══════════════════════

    public void Register(string key, string display, PromptDialect dialect, EngineCapabilities caps, Func<bool> enabled,
        Func<WanVideoRequest, IProgress<WanProgress>?, CancellationToken, Task<WanGenerationResult>> generate, string source = "기준값")
    {
        lock (_gate)
        {
            _engines.RemoveAll(e => e.Key == key);
            _engines.Add(new Engine { Key = key, Display = display, Dialect = dialect, Caps = caps, Enabled = enabled, Generate = generate, Source = source });
        }
        Raise();
    }

    public void Unregister(string key) { lock (_gate) _engines.RemoveAll(e => e.Key == key); Raise(); }

    public void SetEngineEnabled(string key, bool enabled)
    {
        lock (_gate) { if (enabled) _disabled.Remove(key); else _disabled.Add(key); }
        Raise();
    }

    public bool IsEngineEnabled(string key) { lock (_gate) return !_disabled.Contains(key); }

    /// <summary>Sora 2 · Luma · Runway 원격 어댑터 등록(키 공급자).</summary>
    public void RegisterRemoteAdapters(Func<string?> openAiKey, Func<string?> lumaKey, Func<string?> runwayKey)
    {
        Register("sora2", "OpenAI Sora 2", PromptDialect.SoraNarrative, new EngineCapabilities(0.92, 30, 0.10, 1280, 720, 30, 12, true, false, new[] { "cinematic", "realistic", "story" }),
            () => !string.IsNullOrWhiteSpace(openAiKey()), (r, p, ct) => SoraGenerateAsync(r, openAiKey() ?? "", p, ct));
        Register("luma", "Luma Dream Machine(Ray 2)", PromptDialect.LumaConcise, new EngineCapabilities(0.85, 20, 0.07, 1920, 1080, 24, 9, true, false, new[] { "cinematic", "nature", "product" }),
            () => !string.IsNullOrWhiteSpace(lumaKey()), (r, p, ct) => LumaGenerateAsync(r, lumaKey() ?? "", p, ct));
        Register("runway", "Runway Gen-3 Alpha Turbo", PromptDialect.RunwayCamera, new EngineCapabilities(0.80, 6, 0.05, 1280, 768, 24, 10, false, true, new[] { "action", "cinematic", "product" }),
            () => !string.IsNullOrWhiteSpace(runwayKey()), (r, p, ct) => RunwayGenerateAsync(r, runwayKey() ?? "", p, ct));
    }

    private void SyncPlugins()
    {
        if (_plugins is null) return;
        var live = _plugins.EnginePlugins();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in live)
        {
            var key = "plugin:" + e.Id;
            keys.Add(key);
            var engine = e;
            bool exists;
            lock (_gate) exists = _engines.Exists(x => x.Key == key);
            if (!exists)
                Register(key, "🧩 " + e.Name, PromptDialect.Passthrough, e.Capabilities, () => engine.Enabled,
                    (r, p, ct) => _plugins.GenerateWithPluginAsync(engine, r, ct), "플러그인 선언");
        }
        lock (_gate) _engines.RemoveAll(x => x.Key.StartsWith("plugin:", StringComparison.Ordinal) && !keys.Contains(x.Key));
        Raise();
    }

    // ══════════════════════ 점수(무할당 · 테스트 대상) ══════════════════════

    /// <summary>
    /// 엔진 순위 — 결과는 <paramref name="order"/>에 점수 내림차순 인덱스, 반환은 유효 개수. <paramref name="relaxed"/>=true 면 하드 제약 무시(활성만).
    /// </summary>
    public static int Rank(ReadOnlySpan<EngineSnapshot> engines, in RoutingRequest req, RoutingWeights w, bool relaxed, Span<int> order, Span<double> scores)
    {
        var n = 0;
        double maxLat = 1, maxCost = 1e-6;
        for (var i = 0; i < engines.Length; i++)
        {
            maxLat = Math.Max(maxLat, engines[i].SecondsPerClipSecond * Math.Max(1, req.DurationSeconds));
            maxCost = Math.Max(maxCost, engines[i].UsdPerSecond * Math.Max(1, req.DurationSeconds));
        }
        var wsum = Math.Max(1e-6, w.Quality + w.Speed + w.Cost);
        for (var i = 0; i < engines.Length; i++)
        {
            var e = engines[i];
            if (!e.Available) continue;
            var dur = Math.Max(1, req.DurationSeconds);
            var lat = e.SecondsPerClipSecond * dur;
            var cost = e.UsdPerSecond * dur;
            if (!relaxed)
            {
                if (req.HasImage ? !e.ImageToVideo && !e.TextToVideo : !e.TextToVideo) continue;
                if (dur > e.MaxDuration + 1e-9) continue;
                if (req.BudgetUsd > 0 && cost > req.BudgetUsd + 1e-9) continue;
                if (req.MaxLatencySeconds > 0 && lat > req.MaxLatencySeconds) continue;
            }
            var q = e.Quality;
            if ((long)req.Width * req.Height > (long)e.MaxWidth * e.MaxHeight) q *= 0.75;
            if (req.Fps > e.MaxFps) q *= 0.9;
            if (req.HasImage && e.ImageToVideo) q *= 1.05;
            if (e.GenreMatch) q *= 1.1;
            var s = (w.Quality * Math.Min(1.2, q) + w.Speed * (1 - lat / maxLat) + w.Cost * (1 - cost / maxCost)) / wsum;
            s *= 0.5 + 0.5 * Math.Clamp(e.SuccessRate, 0, 1);
            var k = n++;
            while (k > 0 && scores[k - 1] < s) { scores[k] = scores[k - 1]; order[k] = order[k - 1]; k--; }
            scores[k] = s; order[k] = i;
        }
        return n;
    }

    /// <summary>"1280*720" / "1280x720" → (w,h) · 실패 시 1280x720.</summary>
    public static (int W, int H) ParseSize(string? size)
    {
        if (string.IsNullOrWhiteSpace(size)) return (1280, 720);
        var s = size.AsSpan();
        var i = s.IndexOfAny('*', 'x');
        if (i <= 0) return (1280, 720);
        return int.TryParse(s[..i], NumberStyles.None, CultureInfo.InvariantCulture, out var w) && int.TryParse(s[(i + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var h) && w > 0 && h > 0
            ? (w, h) : (1280, 720);
    }

    // ══════════════════════ 라우팅 ══════════════════════

    public List<EngineBenchmark> Benchmarks()
    {
        var now = Environment.TickCount64;
        var list = new List<EngineBenchmark>();
        lock (_gate)
            foreach (var e in _engines)
                list.Add(new EngineBenchmark(e.Key, e.Display, SafeEnabled(e) && !_disabled.Contains(e.Key), now < e.OpenUntil, e.Caps.Quality,
                    e.LatencyEwma > 0 ? e.LatencyEwma : e.Caps.SecondsPerClipSecond, e.Caps.UsdPerSecond, e.SuccessEwma, e.Attempts, e.Successes,
                    e.LatencyEwma, e.Attempts > 0 ? "실측" : e.Source, e.LastError));
        return list;
    }

    /// <summary>요청에 대한 라우팅 결정(실행 없음 · UI 미리보기/테스트).</summary>
    public RouteDecision? Decide(in RoutingRequest req)
    {
        Engine[] es;
        var now = Environment.TickCount64;
        lock (_gate) es = _engines.ToArray();
        if (es.Length == 0) return null;
        Span<EngineSnapshot> snaps = es.Length <= 32 ? stackalloc EngineSnapshot[es.Length] : new EngineSnapshot[es.Length];
        Span<int> order = es.Length <= 32 ? stackalloc int[es.Length] : new int[es.Length];
        Span<double> scores = es.Length <= 32 ? stackalloc double[es.Length] : new double[es.Length];
        for (var i = 0; i < es.Length; i++)
        {
            var e = es[i];
            bool avail;
            lock (_gate) avail = !_disabled.Contains(e.Key) && now >= e.OpenUntil;
            avail = avail && SafeEnabled(e) && (PinnedEngine is null || PinnedEngine == e.Key);
            var genre = false;
            foreach (var g in e.Caps.Genres) if (string.Equals(g, req.Genre, StringComparison.OrdinalIgnoreCase)) { genre = true; break; }
            snaps[i] = new EngineSnapshot(avail, e.Caps.Quality, e.LatencyEwma > 0 ? e.LatencyEwma : e.Caps.SecondsPerClipSecond, e.Caps.UsdPerSecond,
                e.Caps.MaxWidth, e.Caps.MaxHeight, e.Caps.MaxFps, e.Caps.MaxDuration, e.Caps.TextToVideo, e.Caps.ImageToVideo, e.SuccessEwma, genre);
        }
        var relaxed = false;
        var n = Rank(snaps, req, Weights, false, order, scores);
        if (n == 0) { relaxed = true; n = Rank(snaps, req, Weights, true, order, scores); }
        if (n == 0) return null;
        var ranking = new List<string>(n);
        for (var i = 0; i < n; i++) ranking.Add(es[order[i]].Key);
        var top = es[order[0]];
        var reason = (relaxed ? "제약 완화 · " : "") + top.Display + " · 점수 " + scores[0].ToString("0.000", CultureInfo.InvariantCulture)
            + " · 예상 " + ((top.LatencyEwma > 0 ? top.LatencyEwma : top.Caps.SecondsPerClipSecond) * Math.Max(1, req.DurationSeconds)).ToString("0", CultureInfo.InvariantCulture) + "초 · $"
            + (top.Caps.UsdPerSecond * Math.Max(1, req.DurationSeconds)).ToString("0.00", CultureInfo.InvariantCulture);
        return new RouteDecision(top.Key, scores[0], relaxed, ranking, reason);
    }

    private static bool SafeEnabled(Engine e)
    {
        try { return e.Enabled(); } catch (Exception ex) { Program.Log("Router.Enabled", ex); return false; }
    }

    public RoutingRequest RequestFor(WanVideoRequest r, double durationSeconds = 5)
    {
        var (w, h) = ParseSize(r.Size);
        var fps = r.Tuning is { Interpolate60: true } ? 60 : 30;
        return new RoutingRequest(w, h, fps, durationSeconds, DefaultGenre, BudgetPerClipUsd, MaxLatencySeconds, !string.IsNullOrWhiteSpace(r.ImagePath) || !string.IsNullOrWhiteSpace(r.ImageUrl));
    }

    /// <summary>IVideoGenerationEngine — 라우팅 + 장애 조치 생성.</summary>
    public async Task<WanGenerationResult> GenerateAsync(WanVideoRequest request, IProgress<WanProgress>? progress = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var rr = RequestFor(request);
        var d = Decide(rr);
        if (d is null) return new WanGenerationResult(false, WanTaskStatus.Failed, null, "사용 가능한 생성 엔진이 없습니다(API 키/엔진 설정 확인).", null, TimeSpan.Zero);
        LastDecision = d;
        Interlocked.Increment(ref _routed);
        LastAction = "⚡ 라우팅 → " + d.EngineKey;
        Raise();
        var errors = new StringBuilder();
        var tries = 0;
        foreach (var key in d.Ranking)
        {
            if (tries >= MaxFailover) break;
            Engine? e;
            lock (_gate) e = _engines.Find(x => x.Key == key);
            if (e is null) continue;
            tries++;
            if (tries > 1) { Interlocked.Increment(ref _failovers); LastAction = "🔁 장애 조치 → " + e.Display; Raise(); }
            var tp = _transpiler.Transpile(request.Prompt, e.Dialect);
            var req = e.Dialect == PromptDialect.Passthrough ? request : request with { Prompt = tp.Prompt, NegativePrompt = tp.Negative ?? request.NegativePrompt };
            var t0 = Stopwatch.GetTimestamp();
            WanGenerationResult r;
            try { r = await e.Generate(req, progress, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Program.Log("Router.Engine", ex); r = new WanGenerationResult(false, WanTaskStatus.Failed, null, ex.Message, null, TimeSpan.Zero); }
            var secs = Stopwatch.GetElapsedTime(t0).TotalSeconds;
            Record(e, r.Ok, secs / Math.Max(1, rr.DurationSeconds), r.Ok ? null : r.Message, rr.DurationSeconds);
            if (r.Ok) return r with { Message = "[" + e.Display + "] " + r.Message, Elapsed = sw.Elapsed };
            if (r.Status == WanTaskStatus.Canceled || ct.IsCancellationRequested) return r;
            errors.Append(e.Key).Append(": ").Append(r.Message).Append(" | ");
        }
        LastAction = "⚠ 전 엔진 실패";
        Raise();
        return new WanGenerationResult(false, WanTaskStatus.Failed, null, "모든 후보 엔진 실패 — " + errors.ToString().TrimEnd(' ', '|'), null, sw.Elapsed);
    }

    private void Record(Engine e, bool ok, double secPerClipSec, string? err, double duration)
    {
        lock (_gate)
        {
            e.Attempts++;
            e.SuccessEwma = e.SuccessEwma * 0.8 + (ok ? 0.2 : 0);
            if (ok)
            {
                e.Successes++; e.Failures = 0; e.LastError = null;
                e.LatencyEwma = e.LatencyEwma <= 0 ? secPerClipSec : e.LatencyEwma * 0.7 + secPerClipSec * 0.3;
                _spentUsd += e.Caps.UsdPerSecond * Math.Max(1, duration);
            }
            else
            {
                e.Failures++; e.LastError = err;
                if (e.Failures >= CircuitFailures) e.OpenUntil = Environment.TickCount64 + (long)CircuitCooldown.TotalMilliseconds;
            }
        }
        Raise();
    }

    public Task<WanUploadResult> UploadImageAsync(string localImagePath, string? model = null, CancellationToken ct = default)
        => Task.FromResult(new WanUploadResult(File.Exists(localImagePath), File.Exists(localImagePath) ? localImagePath : null, "라우터는 엔진 선택 시점에 이미지를 바인딩합니다."));

    // ══════════════════════ 원격 어댑터 ══════════════════════

    /// <summary>Sora 2 size — 세로/가로만(720x1280 · 1280x720).</summary>
    public static string SoraSize(int w, int h) => h > w ? "720x1280" : "1280x720";

    /// <summary>Sora 2 seconds — 4/8/12 중 가장 가까운 값(이상).</summary>
    public static string SoraSeconds(double d) => d <= 4 ? "4" : d <= 8 ? "8" : "12";

    /// <summary>Luma aspect_ratio — 9:16/16:9/1:1.</summary>
    public static string LumaAspect(int w, int h) => w == h ? "1:1" : h > w ? "9:16" : "16:9";

    /// <summary>Runway ratio — gen3a_turbo 허용 768:1280 / 1280:768.</summary>
    public static string RunwayRatio(int w, int h) => h > w ? "768:1280" : "1280:768";

    /// <summary>
    /// 상태 응답 파싱(엔진 공통) — Sora: status queued|in_progress|completed|failed · Luma: state queued|dreaming|completed|failed + assets.video
    /// · Runway: status PENDING|THROTTLED|RUNNING|SUCCEEDED|FAILED + output[0]. 반환 (상태, 비디오 URL, 진행 %, 오류).
    /// </summary>
    public static (WanTaskStatus Status, string? Url, int Percent, string? Error) ParseStatus(string engine, string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (WanTaskStatus.Unknown, null, 0, "빈 응답");
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return (WanTaskStatus.Unknown, null, 0, "스키마 불일치");
            string? Str(JsonElement o, string n) => o.TryGetProperty(n, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            switch (engine)
            {
                case "sora2":
                {
                    var st = Str(r, "status");
                    var pct = r.TryGetProperty("progress", out var pg) && pg.ValueKind == JsonValueKind.Number && pg.TryGetInt32(out var pv) ? Math.Clamp(pv, 0, 100) : 0;
                    var err = r.TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.Object ? Str(er, "message") : null;
                    return st switch
                    {
                        "queued" => (WanTaskStatus.Pending, null, pct, null), "in_progress" => (WanTaskStatus.Running, null, pct, null),
                        "completed" => (WanTaskStatus.Succeeded, null, 100, null), "failed" => (WanTaskStatus.Failed, null, pct, err ?? "생성 실패"),
                        _ => (WanTaskStatus.Unknown, null, pct, "알 수 없는 상태"),
                    };
                }
                case "luma":
                {
                    var st = Str(r, "state");
                    string? url = null;
                    if (r.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Object) url = Str(a, "video");
                    return st switch
                    {
                        "queued" => (WanTaskStatus.Pending, null, 5, null), "dreaming" => (WanTaskStatus.Running, null, 50, null),
                        "completed" => url is not null && IsHttps(url) ? (WanTaskStatus.Succeeded, url, 100, null) : (WanTaskStatus.Failed, null, 100, "영상 URL 누락"),
                        "failed" => (WanTaskStatus.Failed, null, 0, Str(r, "failure_reason") ?? "생성 실패"),
                        _ => (WanTaskStatus.Unknown, null, 0, "알 수 없는 상태"),
                    };
                }
                default:
                {
                    var st = Str(r, "status");
                    string? url = null;
                    if (r.TryGetProperty("output", out var o) && o.ValueKind == JsonValueKind.Array && o.GetArrayLength() > 0 && o[0].ValueKind == JsonValueKind.String) url = o[0].GetString();
                    var pct = r.TryGetProperty("progress", out var pg) && pg.ValueKind == JsonValueKind.Number && pg.TryGetDouble(out var pd) ? (int)Math.Clamp(pd * 100, 0, 100) : 0;
                    return st switch
                    {
                        "PENDING" or "THROTTLED" => (WanTaskStatus.Pending, null, pct, null), "RUNNING" => (WanTaskStatus.Running, null, pct, null),
                        "SUCCEEDED" => url is not null && IsHttps(url) ? (WanTaskStatus.Succeeded, url, 100, null) : (WanTaskStatus.Failed, null, 100, "영상 URL 누락"),
                        "FAILED" or "CANCELLED" => (WanTaskStatus.Failed, null, pct, Str(r, "failure") ?? "생성 실패"),
                        _ => (WanTaskStatus.Unknown, null, pct, "알 수 없는 상태"),
                    };
                }
            }
        }
        catch (JsonException) { return (WanTaskStatus.Unknown, null, 0, "JSON 파싱 실패"); }
    }

    private static bool IsHttps(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    /// <summary>제출 응답에서 id 추출(스키마 검증 · 안전 문자만).</summary>
    public static string? ParseId(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) return null;
            var s = id.GetString();
            if (string.IsNullOrEmpty(s) || s.Length > 128) return null;
            foreach (var ch in s) if (!(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')) return null;
            return s;
        }
        catch (JsonException) { return null; }
    }

    private async Task<(HttpResponseMessage? Resp, string? Error)> SendWithRetryAsync(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            HttpResponseMessage? resp = null;
            try
            {
                using var req = build();
                resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if ((int)resp.StatusCode is 429 or >= 500)
                {
                    resp.Dispose();
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(16, 1 << attempt)), ct).ConfigureAwait(false);
                    continue;
                }
                return (resp, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { resp?.Dispose(); throw; }
            catch (HttpRequestException ex) { resp?.Dispose(); if (attempt == 3) return (null, ex.Message); await Task.Delay(TimeSpan.FromSeconds(1 << attempt), ct).ConfigureAwait(false); }
        }
        return (null, "재시도 한도 초과(429/5xx)");
    }

    /// <summary>제출 → 폴링 공통 루프(10분 데드라인 · 5초 간격).</summary>
    private async Task<WanGenerationResult> SubmitAndPollAsync(string engine, Func<HttpRequestMessage> submit, Func<string, HttpRequestMessage> poll,
        IProgress<WanProgress>? progress, CancellationToken ct, TimeSpan? pollInterval = null)
    {
        var sw = Stopwatch.StartNew();
        using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
        d.CancelAfter(JobDeadline);
        try
        {
            var (resp, err) = await SendWithRetryAsync(submit, d.Token).ConfigureAwait(false);
            if (resp is null) return Fail(err ?? "제출 실패");
            string body;
            using (resp) body = await resp.Content.ReadAsStringAsync(d.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return Fail("제출 거부 HTTP " + ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture) + " · " + Short(body));
            var id = ParseId(body);
            if (id is null) return Fail("제출 응답 스키마 불일치");
            var polls = 0;
            while (true)
            {
                await Task.Delay(pollInterval ?? TimeSpan.FromSeconds(5), d.Token).ConfigureAwait(false);
                polls++;
                var (pr, perr) = await SendWithRetryAsync(() => poll(id), d.Token).ConfigureAwait(false);
                if (pr is null) return Fail(perr ?? "폴링 실패", id);
                string pb;
                using (pr) pb = await pr.Content.ReadAsStringAsync(d.Token).ConfigureAwait(false);
                if (!pr.IsSuccessStatusCode) return Fail("폴링 HTTP " + ((int)pr.StatusCode).ToString(CultureInfo.InvariantCulture), id);
                var (st, url, pct, e2) = ParseStatus(engine, pb);
                progress?.Report(new WanProgress(st, engine + " " + st, pct, polls, sw.Elapsed, id));
                if (st == WanTaskStatus.Succeeded) return new WanGenerationResult(true, st, url ?? id, "생성 완료", id, sw.Elapsed);
                if (st == WanTaskStatus.Failed) return Fail(e2 ?? "생성 실패", id);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Fail("10분 데드라인 초과"); }
        catch (OperationCanceledException) { return new WanGenerationResult(false, WanTaskStatus.Canceled, null, "취소됨", null, sw.Elapsed); }

        WanGenerationResult Fail(string m, string? id = null) => new(false, WanTaskStatus.Failed, null, m, id, sw.Elapsed);
    }

    private static string Short(string s) => s.Length > 160 ? s[..160] : s;

    internal TimeSpan? PollIntervalOverride { get; set; }

    private async Task<WanGenerationResult> SoraGenerateAsync(WanVideoRequest r, string key, IProgress<WanProgress>? progress, CancellationToken ct)
    {
        var (w, h) = ParseSize(r.Size);
        var res = await SubmitAndPollAsync("sora2",
            () =>
            {
                var m = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/videos");
                m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                m.Content = new MultipartFormDataContent
                {
                    { new StringContent("sora-2"), "model" }, { new StringContent(r.Prompt), "prompt" },
                    { new StringContent(SoraSize(w, h)), "size" }, { new StringContent(SoraSeconds(5)), "seconds" },
                };
                return m;
            },
            id => { var m = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/videos/" + id); m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key); return m; },
            progress, ct, PollIntervalOverride).ConfigureAwait(false);
        if (!res.Ok || res.TaskId is null) return res;
        // content 다운로드(인증 필요) → 로컬 파일 경로 반환
        var path = Path.Combine(Path.GetTempPath(), "pfs_sora_" + res.TaskId + ".mp4");
        try
        {
            using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
            d.CancelAfter(TimeSpan.FromMinutes(3));
            var (resp, err) = await SendWithRetryAsync(() =>
            {
                var m = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/videos/" + res.TaskId + "/content");
                m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                return m;
            }, d.Token).ConfigureAwait(false);
            if (resp is null) return res with { Ok = false, Status = WanTaskStatus.Failed, VideoUrl = null, Message = "다운로드 실패: " + err };
            using (resp)
            {
                if (!resp.IsSuccessStatusCode) return res with { Ok = false, Status = WanTaskStatus.Failed, VideoUrl = null, Message = "다운로드 HTTP " + ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture) };
                await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                await resp.Content.CopyToAsync(fs, d.Token).ConfigureAwait(false);
            }
            return res with { VideoUrl = path };
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return res with { Ok = false, Status = WanTaskStatus.Failed, VideoUrl = null, Message = "다운로드 실패: " + ex.Message };
        }
    }

    private Task<WanGenerationResult> LumaGenerateAsync(WanVideoRequest r, string key, IProgress<WanProgress>? progress, CancellationToken ct)
    {
        var (w, h) = ParseSize(r.Size);
        return SubmitAndPollAsync("luma",
            () =>
            {
                var m = new HttpRequestMessage(HttpMethod.Post, "https://api.lumalabs.ai/dream-machine/v1/generations");
                m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                m.Content = JsonBody(wr =>
                {
                    wr.WriteString("prompt", r.Prompt); wr.WriteString("model", "ray-2"); wr.WriteString("aspect_ratio", LumaAspect(w, h));
                    wr.WriteString("resolution", Math.Max(w, h) >= 1920 ? "1080p" : "720p"); wr.WriteString("duration", "5s");
                });
                return m;
            },
            id => { var m = new HttpRequestMessage(HttpMethod.Get, "https://api.lumalabs.ai/dream-machine/v1/generations/" + id); m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key); return m; },
            progress, ct, PollIntervalOverride);
    }

    private async Task<WanGenerationResult> RunwayGenerateAsync(WanVideoRequest r, string key, IProgress<WanProgress>? progress, CancellationToken ct)
    {
        var image = r.ImagePath;
        if (string.IsNullOrWhiteSpace(image) || !File.Exists(image)) return new WanGenerationResult(false, WanTaskStatus.Failed, null, "Runway 는 참조 이미지(로컬)가 필요합니다.", null, TimeSpan.Zero);
        var bytes = await File.ReadAllBytesAsync(image, ct).ConfigureAwait(false);
        if (bytes.Length > 3_000_000) return new WanGenerationResult(false, WanTaskStatus.Failed, null, "참조 이미지가 3MB 를 초과합니다.", null, TimeSpan.Zero);
        var mime = image.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
        var dataUri = "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
        var (w, h) = ParseSize(r.Size);
        return await SubmitAndPollAsync("runway",
            () =>
            {
                var m = new HttpRequestMessage(HttpMethod.Post, "https://api.dev.runwayml.com/v1/image_to_video");
                m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                m.Headers.TryAddWithoutValidation("X-Runway-Version", "2024-11-06");
                m.Content = JsonBody(wr =>
                {
                    wr.WriteString("model", "gen3a_turbo"); wr.WriteString("promptImage", dataUri); wr.WriteString("promptText", r.Prompt);
                    wr.WriteString("ratio", RunwayRatio(w, h)); wr.WriteNumber("duration", 5);
                    if (WanVideoRequest.ToCloudSeed(r.Seed) is int s) wr.WriteNumber("seed", s);
                });
                return m;
            },
            id =>
            {
                var m = new HttpRequestMessage(HttpMethod.Get, "https://api.dev.runwayml.com/v1/tasks/" + id);
                m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                m.Headers.TryAddWithoutValidation("X-Runway-Version", "2024-11-06");
                return m;
            },
            progress, ct, PollIntervalOverride).ConfigureAwait(false);
    }

    private static ByteArrayContent JsonBody(Action<Utf8JsonWriter> write)
    {
        using var ms = new MemoryStream(512);
        using (var w = new Utf8JsonWriter(ms)) { w.WriteStartObject(); write(w); w.WriteEndObject(); }
        var c = new ByteArrayContent(ms.ToArray());
        c.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return c;
    }

    private void Raise()
    {
        var h = OnChange;
        if (h is null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action)d)(); } catch (Exception ex) { Program.Log("Router.OnChange", ex); }
        }
    }

    public void Dispose()
    {
        if (_plugins is not null) _plugins.OnChange -= SyncPlugins;
        if (_ownsHttp) _http.Dispose();
        OnChange = null;
    }
}
