using System.Diagnostics;
using System.Globalization;

namespace PromptFlowStudio.Services.TrendJacking;

/// <summary>
/// [PromptFlow Studio v17.96.0] 🔥 실시간 트렌드재킹 &amp; 해시태그 레이더 러너(HUD <c>TrjRun</c>).
///   ▸ 체인: 모의 급상승 피드(재사용 char 버퍼 · 1 분 틱) → <see cref="RealtimeSocialTrendSpikeRadarEngine"/> 파싱/가속도/연관도 스캔 →
///     <see cref="ViralHashtagDynamicMetadataInjector"/> 3 플랫폼 메타데이터 미리보기 → 원클릭 주입 시 <see cref="TrendSpikeCooldownAntiSpamGuard"/> 4 시간 쿨다운 기록.
///   ▸ 시뮬레이션 가상 시계(60 s 단위 전진) · 결정적 xorshift 지터.
/// </summary>
public sealed class TrendJackingStudioRunner : IDisposable
{
    public const int StepSec = 60, LogSize = 32, FeedCap = 1024;
    public const long SimEpoch = 1_800_000_000;

    /// <summary>모의 급상승 토픽 — 키워드 · 기본 언급량 · 분당 증가량.</summary>
    public static readonly string[] FeedKeywords = { "아이폰16", "야간 촬영", "캡컷 템플릿", "흑백요리사", "로제 아파트", "할로윈 메이크업", "갤럭시 S25", "다이소 꿀템", "수능 D-30", "카메라 꿀팁", "비트코인 신고가", "좀비 분장" };
    private static readonly int[] FeedBase = { 120, 60, 80, 300, 260, 90, 150, 70, 200, 50, 180, 40 };
    private static readonly int[] FeedGrowth = { 18, 9, 12, 25, 22, 15, 10, 6, -5, 7, 14, 8 };

    /// <summary>대본 프리셋 — 제목 · 대본 · 태그 · 요약 · 틈새 태그.</summary>
    public static readonly string[] PresetNames = { "📱 아이폰16 야간 촬영 꿀팁", "🎃 10분 할로윈 메이크업" };
    private static readonly string[] PresetScripts =
    {
        "아이폰16 카메라로 야간 촬영할 때 꼭 바꿔야 할 설정 3가지! 야간 모드 노출 고정과 프로레스 촬영, 그리고 캡컷 보정까지 아이폰16 하나로 끝냅니다.",
        "10분 완성 할로윈 메이크업! 다이소 재료만으로 좀비 분장과 글리터 포인트까지 따라 해 보세요.",
    };
    private static readonly string[] PresetTags = { "아이폰16,야간촬영,카메라꿀팁,아이폰카메라,캡컷", "할로윈메이크업,다이소,분장,메이크업튜토리얼" };
    private static readonly string[] PresetSummaries = { "아이폰16 야간 촬영 설정 3가지로 밤 사진 퀄리티 2배 올리기", "다이소 재료로 10분 만에 끝내는 좀비 할로윈 메이크업" };
    private static readonly string[] PresetNiches = { "야간촬영,카메라꿀팁,아이폰카메라,폰카꿀팁", "메이크업튜토리얼,다이소꿀템,분장,뷰티" };

    private readonly char[] _feed = new char[FeedCap];
    private readonly string[] _log = new string[LogSize];
    private int _logHead, _logCount, _feedLen, _lastTop = -1;
    private uint _rng = 0x6C8E9CF5u;
    private string _summary = PresetSummaries[0], _niche = PresetNiches[0];

    public TrendJackingStudioRunner(IServiceProvider sp) : this() { }

    public TrendJackingStudioRunner() => ApplyPreset(0);

    public event Action? OnChange;
    public RealtimeSocialTrendSpikeRadarEngine Radar { get; } = new();
    public ViralHashtagDynamicMetadataInjector Injector { get; } = new();
    public TrendSpikeCooldownAntiSpamGuard Guard { get; } = new();
    public double SimNow { get; private set; } = SimEpoch;
    public int Preset { get; private set; }
    public TrendPlatform Platform { get; private set; }
    public long Ticks { get; private set; }
    public long Injections { get; private set; }
    public long InjectedTags { get; private set; }
    public double LastScanMicros { get; private set; }
    public double MaxScanMicros { get; private set; }
    public int LogCount => _logCount;
    public string? LastAction { get; private set; }
    public string Summary => _summary;
    public string Niche => _niche;
    public ReadOnlySpan<char> LastFeed => _feed.AsSpan(0, _feedLen);
    public int ActiveCooldowns => Guard.ActiveCooldowns(SimNow);
    public long ElapsedMin => (long)(SimNow - SimEpoch) / 60;

    /// <summary>최근 로그(0 = 최신).</summary>
    public string Log(int i) => _log[(_logHead - 1 - i + LogSize * 2) % LogSize];

    /// <summary>최고 골든 토픽(가속도 순) 인덱스 · 없으면 −1.</summary>
    public int TopGolden()
    {
        for (var i = 0; i < Radar.TopicCount; i++) if (Radar.IsGolden(Radar.Ranked(i))) return Radar.Ranked(i);
        return -1;
    }

    // ══════════════════════ 조작(비핫패스) ══════════════════════

    /// <summary>1 분 단위 전진 — 피드 수신 · 스캔 · 미리보기 재조합. 반환 = 골든 토픽 수.</summary>
    public int Advance(int minutes)
    {
        var steps = Math.Max(1, minutes);
        for (var k = 0; k < steps; k++) Step();
        Recompose();
        var top = TopGolden();
        LastAction = "⏩ +" + Id(steps) + "분 · 토픽 " + Id(Radar.TopicCount) + " · 골든 " + Id(Radar.GoldenCount) +
                     (top >= 0 ? " · 1위 " + Radar.Keyword(top).ToString() + " " + Id(Radar.Relevance(top)) + "점" : "") +
                     " · 스캔 " + LastScanMicros.ToString("0.0", CultureInfo.InvariantCulture) + "µs";
        Raise();
        return Radar.GoldenCount;
    }

    /// <summary>원클릭 메타데이터 주입 — 가드 허용 골든 트렌드만 채택 후 쿨다운 기록.</summary>
    public int Inject()
    {
        if (Radar.TopicCount == 0) Advance(1);
        var cdBefore = Guard.BlockedCooldown;
        var n = Recompose();
        if (n == 0)
        {
            var cd = Guard.BlockedCooldown - cdBefore;
            LastAction = cd > 0 ? "🧊 골든 트렌드 " + Id(cd) + "건 모두 4시간 쿨다운 — 주입 보류(틈새 · 메가 태그만 유지)" : "⚠️ 주입 가능한 골든 토픽 없음 — 연관도 ≥ " + Id(RealtimeSocialTrendSpikeRadarEngine.GoldenScore) + " 트렌드 대기";
            Push(LastAction);
            Raise();
            return 0;
        }
        Injector.Commit(Radar, Guard, SimNow);
        Injections++;
        InjectedTags += n;
        Push("💉 주입 #" + Id(Injections) + " 트렌드 " + Id(n) + " · YT 「" + Injector.YouTubeTitle.ToString() + "」");
        LastAction = "💉 메타데이터 주입 완료 · 트렌드 " + Id(n) + " · YT 태그 " + Id(Injector.YouTubeTagCount) + " · IG 태그 " + Id(Injector.InstagramTagCount) + " · 4시간 쿨다운 시작";
        Raise();
        return n;
    }

    /// <summary>무관 트렌드 강제 주입 시도 — 최고 급상승 무관 토픽을 가드에 통과시켜 차단 확인.</summary>
    public TrendInjectDecision ForceIrrelevant()
    {
        if (Radar.TopicCount == 0) Advance(1);
        var t = -1;
        for (var i = 0; i < Radar.TopicCount; i++)
            if (Radar.Relevance(Radar.Ranked(i)) < TrendSpikeCooldownAntiSpamGuard.MinRelevance) { t = Radar.Ranked(i); break; }
        if (t < 0) { LastAction = "✅ 무관 트렌드 없음"; Raise(); return TrendInjectDecision.Allowed; }
        var d = Guard.Evaluate(Radar.Keyword(t), Radar.Relevance(t), SimNow);
        if (d == TrendInjectDecision.Allowed) Guard.Record(Radar.Keyword(t), SimNow);
        LastAction = (d == TrendInjectDecision.BlockedIrrelevant ? "🛑 무관 편승 차단 — " : "⚠️ ") + Radar.Keyword(t).ToString() + " 연관도 " + Id(Radar.Relevance(t)) + "점 < " + Id(TrendSpikeCooldownAntiSpamGuard.MinRelevance) + " · " + TrendSpikeCooldownAntiSpamGuard.DecisionName(d);
        Push(LastAction);
        Raise();
        return d;
    }

    /// <summary>가상 시계 4 시간 경과 — 쿨다운 해제 확인(피드 미수신).</summary>
    public void SkipCooldown()
    {
        SimNow += TrendSpikeCooldownAntiSpamGuard.CooldownSec;
        Recompose();
        LastAction = "⏭ +4시간 — 활성 쿨다운 " + Id(ActiveCooldowns) + " · 재주입 가능 트렌드 " + Id(Injector.PickedCount);
        Push(LastAction);
        Raise();
    }

    public void SetPreset(int preset)
    {
        ApplyPreset(Math.Clamp(preset, 0, PresetNames.Length - 1));
        if (Radar.TopicCount > 0) { RunScan(); Recompose(); }
        LastAction = "📝 대본 프리셋: " + PresetNames[Preset] + " · 골든 " + Id(Radar.GoldenCount);
        Raise();
    }

    public void SetPlatform(TrendPlatform p)
    {
        Platform = p;
        LastAction = "👁 미리보기: " + ViralHashtagDynamicMetadataInjector.PlatformName(p);
        Raise();
    }

    public void SetSummary(string? text)
    {
        _summary = string.IsNullOrWhiteSpace(text) ? PresetSummaries[Preset] : text.Length > 300 ? text[..300] : text;
        Recompose();
        LastAction = "✏️ 요약문 " + Id(_summary.Length) + "자";
        Raise();
    }

    public void SetNiche(string? text)
    {
        _niche = string.IsNullOrWhiteSpace(text) ? PresetNiches[Preset] : text.Length > 200 ? text[..200] : text;
        Recompose();
        LastAction = "🏷 틈새 태그 갱신";
        Raise();
    }

    public void Reset()
    {
        Radar.Reset(); Injector.Reset(); Guard.Reset();
        SimNow = SimEpoch;
        Ticks = Injections = InjectedTags = 0;
        LastScanMicros = MaxScanMicros = 0;
        _feedLen = 0;
        _lastTop = -1;
        _rng = 0x6C8E9CF5u;
        ApplyPreset(Preset);
        Array.Clear(_log);
        _logHead = _logCount = 0;
        LastAction = "♻ 초기화";
        Raise();
    }

    // ══════════════════════ 내부 ══════════════════════

    private void ApplyPreset(int p)
    {
        Preset = p;
        _summary = PresetSummaries[p];
        _niche = PresetNiches[p];
        Radar.SetContent(PresetScripts[p].AsSpan(), PresetTags[p].AsSpan());
    }

    private void Step()
    {
        Radar.CloseMinute();
        SimNow += StepSec;
        Ticks++;
        var m = (int)Ticks;
        var b = _feed.AsSpan();
        var n = 0;
        for (var i = 0; i < FeedKeywords.Length; i++)
        {
            var cnt = Math.Max(0, FeedBase[i] + FeedGrowth[i] * m + (int)(Next() % 6));
            var kw = FeedKeywords[i].AsSpan();
            if (n + kw.Length + 12 > b.Length) break;
            kw.CopyTo(b[n..]);
            n += kw.Length;
            b[n++] = ':';
            cnt.TryFormat(b[n..], out var w, default, CultureInfo.InvariantCulture);
            n += w;
            b[n++] = ';';
        }
        _feedLen = n;
        Radar.Ingest(LastFeed);
        RunScan();
        var top = TopGolden();
        if (top == _lastTop) return;   // 골든 1위 교체 시에만 기록
        _lastTop = top;
        if (top >= 0) Push("🔥 골든 1위 " + Radar.Keyword(top).ToString() + " +" + Id(Radar.Velocity(top)) + "/분 · 연관 " + Id(Radar.Relevance(top)) + "점");
    }

    private void RunScan()
    {
        var t0 = Stopwatch.GetTimestamp();
        Radar.Scan();
        LastScanMicros = Stopwatch.GetElapsedTime(t0).TotalMilliseconds * 1000.0;
        if (LastScanMicros > MaxScanMicros) MaxScanMicros = LastScanMicros;
    }

    private int Recompose() => Injector.Compose(Radar, Guard, SimNow, _summary.AsSpan(), _niche.AsSpan());

    private uint Next()
    {
        var x = _rng;
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;
        return _rng = x;
    }

    private void Push(string line)
    {
        lock (_log)
        {
            _log[_logHead] = "[+" + Id(ElapsedMin) + "m] " + line;
            _logHead = (_logHead + 1) % LogSize;
            if (_logCount < LogSize) _logCount++;
        }
    }

    private static string Id(long v) => v.ToString(CultureInfo.InvariantCulture);

    public string StatusLine() =>
        "피드 " + Id(Radar.Parsed) + " · 토픽 " + Id(Radar.TopicCount) + " · 골든 " + Id(Radar.GoldenCount) + " · 주입 " + Id(Injections) +
        " · 무관 차단 " + Id(Guard.BlockedIrrelevant) + " · 쿨다운 차단 " + Id(Guard.BlockedCooldown) + " · 최대 스캔 " + MaxScanMicros.ToString("0.0", CultureInfo.InvariantCulture) + "µs";

    private void Raise()
    {
        try { OnChange?.Invoke(); }
        catch (Exception ex) { PromptFlowStudio.Program.Log("TrendJacking.OnChange", ex); }
    }

    public void Dispose() => OnChange = null;
}
