using System.Globalization;
using PromptFlowStudio.Services.BroadcastMcr;
using PromptFlowStudio.Services.SplatEnvironment;
using PromptFlowStudio.Services.StreamGateway;

namespace PromptFlowStudio.Services.SemanticChat;

/// <summary>
/// v17.72.0 군중 채팅 시뮬레이터(플랫폼별 생산자 스레드 3 · 무할당 템플릿) — 분위기 혼합 비율 · 총 msg/s · 독성/스팸 비율로 애그리게이터에 병렬 투입.
/// </summary>
public sealed class CrowdChatSimulator : IDisposable
{
    public static readonly string[][] Templates =
    {
        new[] { "가즈아!!!", "와 미쳤다 레전드", "대박 ㄷㄷㄷ", "letsgo POG", "소름 돋았다 🔥", "드가자 🚀🚀" },
        new[] { "ㅋㅋㅋㅋㅋ 웃기네", "너무 귀여워요 ❤", "오늘 방송 최고 ㅎㅎ", "lol haha", "힐링된다 😊", "감사합니다 사랑해요" },
        new[] { "ㅠㅠㅠ 슬프다", "벌써 끝나요? 아쉽다", "눈물 나 😢", "가지마 ㅜㅜ", "so sad 😭", "보고싶을 거야 💔" },
        new[] { "아 진짜 짜증나", "최악이다 환불해", "렉 장난하냐 😡", "wtf worst", "열받네 🤬", "어이없다 👎" },
        new[] { "안녕하세요", "오늘 몇 시까지 해요?", "처음 왔어요", "배고프다", "지금 몇 곡째?", "음량 괜찮아요" },
    };
    public static readonly string[] ToxicTemplates = { "씨발 뭐야", "병신같네", "fuck this", "카지노 바카라 홍보" };

    private readonly UnifiedCrossPlatformChatAggregator _agg;
    private readonly float[] _mix = { 0.2f, 0.2f, 0.2f, 0.2f, 0.2f };
    private Thread?[] _t = new Thread?[3];
    private volatile bool _run;

    public CrowdChatSimulator(UnifiedCrossPlatformChatAggregator agg) => _agg = agg;

    public float RatePerSec { get; set; } = 30f;
    public float ToxicRatio { get; set; } = 0.02f;
    public bool Running => _run;
    public long Produced => Interlocked.Read(ref _produced);
    private long _produced;

    /// <summary>분위기 혼합(Hype · Joy · Sadness · Anger · Neutral) — 합 정규화.</summary>
    public void SetMix(float hype, float joy, float sad, float anger, float neutral)
    {
        var s = hype + joy + sad + anger + neutral; if (s <= 0) s = 1;
        lock (_mix) { _mix[0] = hype / s; _mix[1] = joy / s; _mix[2] = sad / s; _mix[3] = anger / s; _mix[4] = neutral / s; }
    }

    public void Start()
    {
        if (_run) return;
        _run = true;
        for (var i = 0; i < 3; i++) { var p = (ChatPlatform)i; _t[i] = new Thread(() => Produce(p)) { IsBackground = true, Name = "PF.CrowdSim." + i.ToString(CultureInfo.InvariantCulture) }; _t[i]!.Start(); }
    }

    public void Stop()
    {
        _run = false;
        for (var i = 0; i < 3; i++) { var t = _t[i]; _t[i] = null; if (t is not null && t != Thread.CurrentThread) t.Join(1000); }
    }

    private void Produce(ChatPlatform p)
    {
        var rng = 0x9E37u + (uint)p * 7919u;
        Span<char> user = stackalloc char[12];
        var sw = System.Diagnostics.Stopwatch.StartNew(); var acc = 0.0; var last = 0.0;
        while (_run)
        {
            var now = sw.Elapsed.TotalSeconds;
            acc += (now - last) * RatePerSec / 3.0; last = now;
            while (acc >= 1 && _run) { acc -= 1; Emit(p, ref rng, user); }
            Thread.Sleep(5);
        }
    }

    /// <summary>메시지 1 건 투입(무할당 · 테스트 직접 호출 가능).</summary>
    public bool Emit(ChatPlatform p, ref uint rng, Span<char> user)
    {
        rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
        var u = (rng & 0xFFFF) / 65536f;
        "fan".AsSpan().CopyTo(user);
        ((int)(rng >> 16) % 5000).TryFormat(user[3..], out var w, default, CultureInfo.InvariantCulture);
        string text;
        var pick = (int)((rng >> 8) % 6);
        if (u < ToxicRatio) text = ToxicTemplates[pick % ToxicTemplates.Length];
        else
        {
            var r = (u - ToxicRatio) / Math.Max(1e-6f, 1f - ToxicRatio); var k = 4; var c = 0f;
            for (var i = 0; i < 5; i++) { c += Volatile.Read(ref _mix[i]); if (r < c) { k = i; break; } }
            text = Templates[k][pick];
        }
        Interlocked.Increment(ref _produced);
        return _agg.Offer(p, user[..(3 + w)], text, (rng & 63) == 0 ? 1000 : 0);
    }

    public void Dispose() => Stop();
}

/// <summary>
/// [PromptFlow Studio v17.72.0] 🌩️ 시맨틱 채팅 &amp; 날씨 엔진 스튜디오 러너 — v17.66 MCR 장면 레이어.
///   ▸ MCR 틱(무할당): 통합 애그리게이터 Drain(≤ 2 ms · 512 건) → 시맨틱 분석기 → 5 s 감성 벡터 → 날씨 엔진 Step → (래스터 변조기 · PGM 오버레이 체인은 MCR 이 호출).
///   ▸ 결선: v17.62 게이트웨이 <c>ChatTap</c>(실 Twitch/YouTube/치지직) · 래스터 변조기 체인(v17.56 그림자 → 날씨) · PGM 오버레이 체인(날씨 → v17.68 CTA 배너).
/// </summary>
public sealed class SemanticChatWeatherStudioRunner : IMcrSceneLayer, IDisposable
{
    public const int Hist = 240, DrainMax = 512;
    public const double DrainBudgetUs = 2000;

    private readonly BroadcastMcrStudioRunner _mcr;
    private readonly bool _ownMcr;
    private readonly StreamGatewayClipperStudioRunner? _gateway;
    private readonly float[][] _vecS = { new float[Hist], new float[Hist], new float[Hist], new float[Hist] };
    private readonly float[] _layerS = new float[Hist];
    private int _sHead, _sN, _hist;
    private UnifiedGaussianSceneCompositorEngine? _hooked;
    private long _layerFrames;

    public SemanticChatWeatherStudioRunner(IServiceProvider sp) : this(
        sp.GetService(typeof(BroadcastMcrStudioRunner)) as BroadcastMcrStudioRunner,
        sp.GetService(typeof(StreamGatewayClipperStudioRunner)) as StreamGatewayClipperStudioRunner) { }

    public SemanticChatWeatherStudioRunner(BroadcastMcrStudioRunner? mcr = null, StreamGatewayClipperStudioRunner? gateway = null)
    {
        _ownMcr = mcr is null;
        _mcr = mcr ?? new BroadcastMcrStudioRunner();
        _gateway = gateway;
        Analyzer = new SemanticSentimentToxicityAnalyzer();
        Simulator = new CrowdChatSimulator(Aggregator);
    }

    public event Action? OnChange;
    public BroadcastMcrStudioRunner Mcr => _mcr;
    public UnifiedCrossPlatformChatAggregator Aggregator { get; } = new();
    public SemanticSentimentToxicityAnalyzer Analyzer { get; }
    public ChatDrivenVolumetricWeatherEngine Weather { get; } = new();
    public CrowdChatSimulator Simulator { get; }
    public bool GatewayAvailable => _gateway is not null;
    public bool LinkGateway { get; private set; }
    public bool Attached { get; private set; }
    public bool IsRunning => _mcr.IsRunning;
    public SentimentVector Vector { get; private set; }
    public long LayerFrames => _layerFrames;
    public long LayerOverBudget { get; private set; }
    public double LastLayerUs { get; private set; }
    public double MaxLayerUs { get; private set; }
    public string? LastAction { get; private set; }
    public int SeriesCount => _sN;
    public float VectorSeries(CrowdEmotion e, int i) => _vecS[(int)e][(_sHead - 1 - i + 2 * Hist) % Hist];
    public float LayerSeries(int i) => _layerS[(_sHead - 1 - i + 2 * Hist) % Hist];

    // ══════════════════════ 제어(비핫패스) ══════════════════════
    public void Attach()
    {
        _mcr.AttachLayer(this);
        var cur = _mcr.Mcr.Overlay;
        if (!ReferenceEquals(cur, Weather)) { Weather.NextOverlay = cur; _mcr.Mcr.Overlay = Weather; }
        Attached = true;
        LastAction = "🔗 MCR 레이어 · PGM 날씨 오버레이 · 래스터 변조기 체인 결선"; Raise();
    }

    public void Detach()
    {
        _mcr.DetachLayer(this);
        if (ReferenceEquals(_mcr.Mcr.Overlay, Weather)) _mcr.Mcr.Overlay = Weather.NextOverlay;
        Weather.NextOverlay = null;
        if (_hooked is not null && ReferenceEquals(_hooked.Raster.Modulator, Weather)) _hooked.Raster.Modulator = Weather.Inner;
        _hooked = null; Weather.Inner = null;
        Attached = false;
    }

    public void SetLinkGateway(bool on)
    {
        if (on && _gateway is null) return;
        LinkGateway = on;
        if (_gateway is not null) _gateway.ChatTap = on ? Aggregator : (ReferenceEquals(_gateway.ChatTap, Aggregator) ? null : _gateway.ChatTap);
        LastAction = on ? "🔗 v17.62 Twitch · YouTube · 치지직 채팅 → 통합 버스" : "🔗 게이트웨이 해제"; Raise();
    }

    public void Start() { if (!Attached) Attach(); _mcr.Start(); LastAction = "▶ MCR · 시맨틱 날씨"; Raise(); }
    public void Stop() { _mcr.Stop(); Simulator.Stop(); LastAction = "⏸ 정지"; Raise(); }
    public void Step() { if (!Attached) Attach(); _mcr.Step(); }

    /// <summary>군중 분위기 시나리오(시뮬레이터 혼합 + 속도).</summary>
    public void SetCrowd(CrowdEmotion? mood, float ratePerSec = 60f)
    {
        if (mood is { } m)
        {
            Span<float> w = stackalloc float[5]; w.Fill(0.08f); w[(int)m] = 0.68f;
            Simulator.SetMix(w[0], w[1], w[2], w[3], w[4]);
        }
        else Simulator.SetMix(0.15f, 0.15f, 0.1f, 0.05f, 0.55f);
        Simulator.RatePerSec = Math.Clamp(ratePerSec, 0f, 2000f);
        if (!Simulator.Running) Simulator.Start();
        LastAction = "🧪 군중 시뮬 · " + (mood is { } mm ? SemanticSentimentToxicityAnalyzer.EmotionIcon(mm) + " " + SemanticSentimentToxicityAnalyzer.EmotionName(mm) : "혼합") + " · " + ratePerSec.ToString("0", CultureInfo.InvariantCulture) + " msg/s"; Raise();
    }

    public void StopCrowd() { Simulator.Stop(); LastAction = "🧪 군중 시뮬 정지"; Raise(); }
    public void SetOverride(WeatherPreset? p, float intensity = 1f) { Weather.SetOverride(p, intensity); LastAction = p is { } q ? "🎛 날씨 강제 · " + ChatDrivenVolumetricWeatherEngine.PresetIcon(q) + " " + ChatDrivenVolumetricWeatherEngine.PresetName(q) : "🤖 감성 자동 날씨"; Raise(); }

    // ══════════════════════ IMcrSceneLayer(MCR 틱 · 무할당) ══════════════════════
    public void Step(UnifiedGaussianSceneCompositorEngine comp, in SplatCamera pgm, double nowMs, float dt)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _layerFrames++;
        if (!ReferenceEquals(_hooked, comp) || !ReferenceEquals(comp.Raster.Modulator, Weather))
        {
            var inner = comp.Raster.Modulator;
            if (!ReferenceEquals(inner, Weather)) { Weather.Inner = inner; comp.Raster.Modulator = Weather; }
            _hooked = comp;
        }
        Aggregator.Drain(Analyzer, DrainMax, DrainBudgetUs);
        var v = Analyzer.Step(Aggregator.Clock());
        Vector = v;
        Weather.Step(in v, dt);
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        LastLayerUs = us; if (us > MaxLayerUs && _layerFrames > 3) MaxLayerUs = us;
        if (us > 1000.0 / 60 * 1000) LayerOverBudget++;
        if (++_hist >= 15)
        {
            _hist = 0;
            for (var e = 0; e < 4; e++) _vecS[e][_sHead] = v[e];
            _layerS[_sHead] = (float)us; _sHead = (_sHead + 1) % Hist; if (_sN < Hist) _sN++;
        }
        if ((_layerFrames & 15) == 0) Raise();
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("SemanticChat.OnChange", ex); }
    }

    public void Dispose()
    {
        Simulator.Dispose();
        Detach();
        if (_gateway is not null && ReferenceEquals(_gateway.ChatTap, Aggregator)) _gateway.ChatTap = null;
        if (_ownMcr) _mcr.Dispose();
        OnChange = null;
    }

    public string Summary() =>
        "감성 " + SemanticSentimentToxicityAnalyzer.EmotionIcon(Vector.Dominant) + " " + (Vector.Max * 100).ToString("0", CultureInfo.InvariantCulture) + "% · 날씨 " + ChatDrivenVolumetricWeatherEngine.PresetName(Weather.Dominant)
        + " · 병합 " + Aggregator.Accepted.ToString("N0", CultureInfo.InvariantCulture) + " · 블라인드 " + Analyzer.Blinded.ToString(CultureInfo.InvariantCulture) + " · 셰딩 " + Aggregator.Shed.ToString(CultureInfo.InvariantCulture);
}
