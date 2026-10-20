using System.Globalization;
using PromptFlowStudio.Services.AvatarActing;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.InfluencerLive;
using PromptFlowStudio.Services.Localization.Dubbing;
using PromptFlowStudio.Services.NarrativeTimeline;
using PromptFlowStudio.Services.StreamGateway;

namespace PromptFlowStudio.Services.GlobalDubbing;

/// <summary>
/// [PromptFlow Studio v17.64.0] 🗣️ 실시간 다국어 더빙 스튜디오 러너(60 Hz 전용 스레드).
///   ▸ 대사 소스: 직접 입력 · 데모 각본 · v17.61 인플루언서 리액션 대사 · v17.40 각본 대사 토큰.
///   ▸ 틱: 스트리밍 번역(≤ 200 ms) → 음소 링 → v17.32 다국어 합성(원본 음색 · v17.60 감정 운율) → 플랫폼 트랙 라우팅 → 미리보기 플랫폼 언어 립싱크(오디오 마스터 시계 · &lt; 50 ms).
/// </summary>
public sealed class GlobalDubbingStudioRunner : IDisposable
{
    public const int Hz = 60, Hist = 240;
    public const double DemoGapMs = 900;

    public static readonly string[] DemoLines =
    {
        "안녕하세요 여러분, 오늘도 와 주셔서 감사합니다.",
        "이번 신곡은 여러분을 위해 준비했어요.",
        "와 정말 고마워요! 당신이 최고예요!",
        "항상 응원해 주셔서 고마워요.",
        "다음 주에 다시 만나요.",
    };

    public static readonly DubLanguage[] Choices = { DubLanguage.Ko, DubLanguage.En, DubLanguage.Ja, DubLanguage.Es, DubLanguage.Zh, DubLanguage.Fr, DubLanguage.De };

    private readonly object _gate = new();
    private readonly VirtualInfluencerLiveStudioRunner? _influencer;
    private readonly NarrativeTimelineStudioRunner? _narrative;
    private readonly AvatarActingDirectorStudioRunner? _avatar;
    private readonly float[] _msS = new float[Hist], _latS = new float[Hist];
    private int _sHead, _sN, _demo;
    private long _tick, _lastVoiceLines = -1;
    private int _lastNarrToken = -1;
    private double _demoDue;
    private Thread? _loop;
    private volatile bool _running;
    private System.Diagnostics.Stopwatch? _wall;

    public GlobalDubbingStudioRunner(IServiceProvider sp) : this(
        sp.GetService(typeof(StreamGatewayClipperStudioRunner)) as StreamGatewayClipperStudioRunner,
        sp.GetService(typeof(VirtualInfluencerLiveStudioRunner)) as VirtualInfluencerLiveStudioRunner,
        sp.GetService(typeof(NarrativeTimelineStudioRunner)) as NarrativeTimelineStudioRunner,
        sp.GetService(typeof(AvatarActingDirectorStudioRunner)) as AvatarActingDirectorStudioRunner) { }

    public GlobalDubbingStudioRunner(StreamGatewayClipperStudioRunner? gateway = null, VirtualInfluencerLiveStudioRunner? influencer = null,
        NarrativeTimelineStudioRunner? narrative = null, AvatarActingDirectorStudioRunner? avatar = null, int faceSplats = LocalizedLipSyncBlendshapeSynthesizer.DefaultSplats)
    {
        _influencer = influencer; _narrative = narrative; _avatar = avatar;
        Router = new MultilingualVoiceSynthAndStreamRouter(gateway);
        Translator.Sink = Router;
        Translator.ActiveMask = Router.TranslationMask;
        LipSync = new LocalizedLipSyncBlendshapeSynthesizer(faceSplats);
    }

    public event Action? OnChange;
    public RealtimeMultilingualTranslationPhonemeEngine Translator { get; } = new();
    public MultilingualVoiceSynthAndStreamRouter Router { get; }
    public LocalizedLipSyncBlendshapeSynthesizer LipSync { get; }
    /// <summary>비디오 표시 시계(ms) — null = 틱 시계(결정적) · 스레드 가동 시 벽시계.</summary>
    public Func<double>? VideoClock { get; set; }
    public ChatPlatform PreviewPlatform { get; private set; } = ChatPlatform.Twitch;
    public bool AutoDemo { get; private set; } = true;
    public bool LinkInfluencer { get; private set; }
    public bool LinkNarrative { get; private set; }
    public bool LinkAvatar { get; private set; }
    public bool InfluencerAvailable => _influencer is not null;
    public bool NarrativeAvailable => _narrative is not null;
    public bool AvatarAvailable => _avatar is not null;
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double NowMs => Tick * 1000.0 / Hz;
    public double LastTickMs { get; private set; }
    public double MaxTickMs { get; private set; }
    public long FramesOverBudget { get; private set; }
    public long LinesFromInfluencer { get; private set; }
    public long LinesFromNarrative { get; private set; }
    public string? LastAction { get; private set; }
    public int SeriesCount => _sN;
    private int Idx(int i) => (_sHead - 1 - i + 2 * Hist) % Hist;
    public float FrameMsSeries(int i) => _msS[Idx(i)];
    public float LatencySeries(int i) => _latS[Idx(i)];
    public DubLanguage PreviewLanguage => Router.PlatformLanguage(PreviewPlatform);

    public static string LangCode(DubLanguage l) => l switch
    {
        DubLanguage.Ko => "KO", DubLanguage.En => "EN", DubLanguage.Ja => "JA", DubLanguage.Es => "ES", DubLanguage.Zh => "ZH", DubLanguage.Fr => "FR", DubLanguage.De => "DE", _ => "··",
    };
    public static string LangName(DubLanguage l) => l switch
    {
        DubLanguage.Ko => "한국어(원본)", DubLanguage.En => "영어", DubLanguage.Ja => "일본어", DubLanguage.Es => "스페인어", DubLanguage.Zh => "중국어", DubLanguage.Fr => "프랑스어", DubLanguage.De => "독일어", _ => "기타",
    };

    // ══════════════════════ 제어(비핫패스) ══════════════════════
    public bool Say(string? korean)
    {
        if (string.IsNullOrWhiteSpace(korean)) return false;
        bool ok;
        lock (_gate) ok = Translator.Submit(korean, NowMs);
        LastAction = ok ? "🗣 원문 적재 · " + korean.Trim() : "⚠ 입력 링 포화 — 거부";
        Raise();
        return ok;
    }

    public bool SetPlatformLanguage(ChatPlatform p, DubLanguage l)
    {
        bool ok;
        lock (_gate)
        {
            ok = Router.SetPlatformLanguage(p, l);
            if (ok) Translator.ActiveMask = Router.TranslationMask;
        }
        LastAction = ok ? "🌍 " + MultiPlatformLiveChatGatewaySynthesizer.PlatformName(p) + " → " + LangCode(l) + " 트랙" : "⚠ 미지원 언어";
        Raise();
        return ok;
    }

    public void SetPreviewPlatform(ChatPlatform p) { PreviewPlatform = p; LastAction = "👄 립싱크 미리보기 · " + MultiPlatformLiveChatGatewaySynthesizer.PlatformName(p) + " (" + LangCode(PreviewLanguage) + ")"; Raise(); }
    public void SetAutoDemo(bool on) { AutoDemo = on; LastAction = on ? "🎬 데모 각본 자동 낭독 ON" : "🎬 데모 각본 OFF"; Raise(); }
    public void SetLinkInfluencer(bool on) { if (on && _influencer is null) return; LinkInfluencer = on; _lastVoiceLines = -1; LastAction = on ? "🔗 v17.61 리액션 대사 결선" : "🔗 v17.61 해제"; Raise(); }
    public void SetLinkNarrative(bool on) { if (on && _narrative is null) return; LinkNarrative = on; _lastNarrToken = -1; LastAction = on ? "🔗 v17.40 각본 대사 결선" : "🔗 v17.40 해제"; Raise(); }
    public void SetLinkAvatar(bool on) { if (on && _avatar is null) return; LinkAvatar = on; LastAction = on ? "🔗 v17.60 아바타 감정 운율 결선" : "🔗 수동 운율"; Raise(); }
    public void SetArousal(float a) { Router.Arousal = Math.Clamp(a, 0f, 1f); Raise(); }
    public void SetValence(float v) { Router.Valence = Math.Clamp(v, -1f, 1f); LipSync.Smile = Math.Max(0f, Router.Valence); Raise(); }

    public void ResetMetrics()
    {
        lock (_gate) { Translator.ResetLatency(); LipSync.ResetSync(); MaxTickMs = 0; FramesOverBudget = 0; }
        LastAction = "♻ 지표 초기화"; Raise();
    }

    // ══════════════════════ 60 Hz 틱(무할당) ══════════════════════
    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("GlobalDub.Step");
            Compute();
        }
    }

    private void Compute()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _tick);
        var now = NowMs;
        PollSources(now);
        Translator.Step(now);
        if (LinkAvatar && _avatar is not null)
        {
            ref readonly var g = ref _avatar.Gesture.Current;
            Router.Arousal = Math.Clamp(g.Arousal, 0f, 1f); Router.Valence = Math.Clamp(g.Valence, -1f, 1f);
            LipSync.Smile = Math.Max(0f, Router.Valence);
        }
        Router.Render();
        var video = VideoClock is { } vc ? vc() : _wall is { } w ? w.Elapsed.TotalMilliseconds : now;
        LipSync.Frame(Router.Voice, Router.LaneOfPlatform(PreviewPlatform), PreviewLanguage, video);
        var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        LastTickMs = ms; if (ms > MaxTickMs && _tick > 5) MaxTickMs = ms;
        if (ms > 1000.0 / Hz) FramesOverBudget++;
        _msS[_sHead] = (float)ms; _latS[_sHead] = Translator.LastLatencyMs(PreviewLanguage); _sHead = (_sHead + 1) % Hist; if (_sN < Hist) _sN++;
    }

    private void PollSources(double now)
    {
        if (LinkInfluencer && _influencer is not null && _influencer.VoiceLines != _lastVoiceLines)
        {
            var first = _lastVoiceLines < 0;
            _lastVoiceLines = _influencer.VoiceLines;
            var line = _influencer.Chat.LastLine;
            if (!first && !line.IsEmpty && Translator.Submit(line, now)) LinesFromInfluencer++;
        }
        if (LinkNarrative && _narrative is not null && _narrative.CurrentLineToken != _lastNarrToken)
        {
            var first = _lastNarrToken < 0;
            _lastNarrToken = _narrative.CurrentLineToken;
            if (!first && _lastNarrToken >= 0 && Translator.Submit(_narrative.Sequencer.Text(_lastNarrToken), now)) LinesFromNarrative++;
        }
        if (AutoDemo && Translator.Idle && Router.Voice.RemainingSamples(0) == 0)
        {
            if (_demoDue <= 0) _demoDue = now + DemoGapMs;
            else if (now >= _demoDue) { Translator.Submit(DemoLines[_demo++ % DemoLines.Length], now); _demoDue = 0; }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _wall = System.Diagnostics.Stopwatch.StartNew();
            LipSync.ResetSync();
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.GlobalDub" };
            _loop.Start();
        }
        LastAction = "▶ 60 Hz 동시 통역 · 다국어 합성 · 립싱크";
        Raise();
    }

    public void Stop()
    {
        Thread? t;
        lock (_gate) { if (!_running) return; _running = false; t = _loop; _loop = null; }
        t?.Join(3000);
        lock (_gate) { _wall = null; LipSync.ResetSync(); }
        LastAction = "⏸ 정지";
        Raise();
    }

    private void Loop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long n = 0;
        while (_running)
        {
            try { Step(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("GlobalDub.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }
            if (n % 6 == 0) Raise();
        }
    }

    public string Summary() =>
        "번역 " + Translator.Chunks.ToString(CultureInfo.InvariantCulture) + " 청크 · 최대 " + Translator.MaxLatencyAll.ToString("0", CultureInfo.InvariantCulture) + " ms · 립 편차 " + LipSync.MaxDriftMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms";

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("GlobalDub.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
