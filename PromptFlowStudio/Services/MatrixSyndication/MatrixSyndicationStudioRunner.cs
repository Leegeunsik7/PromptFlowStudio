using System.Diagnostics;
using System.Globalization;
using PromptFlowStudio.Services.Autopilot;
using PromptFlowStudio.Services.Localization.Dubbing;
using PromptFlowStudio.Services.SmartPublishing;

namespace PromptFlowStudio.Services.MatrixSyndication;

/// <summary>
/// [PromptFlow Studio v17.81.0] 🌐 다계정 매트릭스 &amp; 신디케이션 스튜디오 러너.
///   ▸ <see cref="CompliantMultiAccountMatrixRouterEngine"/>(소유 채널 계층 · 1:1 변형 매핑 · 45~120 분 시차 · 골든타임 · 채널별 쿼터) →
///     <see cref="ChannelVariantMultiFormatRenderer"/>(9:16/1:1 · 범퍼 · 로고 · 다국어 자막 번-인) → <see cref="AccountHealthSafetyPauseGuard"/>(30 분 도달 0 · API 거절 → 동결 · 웹훅 · 사람 확인).
///   ▸ 시뮬레이션 시계(<see cref="SimNow"/>): 분 단위 전진 → 기한 도래 항목 발행 → 감시 슬롯 · 도달 주입(0 도달/거절 주입은 관리자 테스트용).
///   ▸ 라이브 프리뷰: 선택 채널 변형을 10 fps 로 렌더 · 썸네일 갱신(무할당) · OnChange 250 ms 스로틀.
/// </summary>
public sealed class MatrixSyndicationStudioRunner : IDisposable
{
    public const int ThumbW = 108, ThumbVH = 192, ThumbSH = 108, LiveFps = 10;
    public const float ClipSec = 15f;
    public const string DemoCaption = "여러분 오늘도 감사합니다";

    private readonly RemoteCreatorTelemetryDispatcher? _ownedAlerts;
    private readonly byte[] _src = new byte[ChannelVariantMultiFormatRenderer.W * ChannelVariantMultiFormatRenderer.H * 3];
    private readonly byte[] _frame = new byte[ChannelVariantMultiFormatRenderer.W * ChannelVariantMultiFormatRenderer.H * 3];
    private readonly byte[][] _thumb = new byte[CompliantMultiAccountMatrixRouterEngine.MaxChannels][];
    private readonly bool[] _zeroReach = new bool[CompliantMultiAccountMatrixRouterEngine.MaxChannels];
    private readonly int[] _reject = new int[CompliantMultiAccountMatrixRouterEngine.MaxChannels];
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _content;

    public MatrixSyndicationStudioRunner(IServiceProvider sp) : this(sp.GetService(typeof(ZeroTouchAutopilotStudioRunner)) as ZeroTouchAutopilotStudioRunner) { }

    public MatrixSyndicationStudioRunner(ZeroTouchAutopilotStudioRunner? autopilot = null)
    {
        if (autopilot is null) _ownedAlerts = new RemoteCreatorTelemetryDispatcher(new SimulatedWebhookServer());
        Alerts = autopilot?.Telemetry ?? _ownedAlerts!;
        AlertsShared = autopilot is not null;
        Guard = new AccountHealthSafetyPauseGuard(Router, Alerts);
        for (var i = 0; i < _thumb.Length; i++) _thumb[i] = new byte[ThumbW * ThumbVH * 3];
        var t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        SimNow = t - t % 300;
        SeedChannels();
    }

    public event Action? OnChange;
    public CompliantMultiAccountMatrixRouterEngine Router { get; } = new();
    public ChannelVariantMultiFormatRenderer Renderer { get; } = new();
    public AccountHealthSafetyPauseGuard Guard { get; }
    public RemoteCreatorTelemetryDispatcher Alerts { get; }
    public bool AlertsShared { get; }
    public long SimNow { get; private set; }
    public int Selected { get; set; }
    public float PreviewT { get; set; } = 4f;
    public bool Running => _loop is { IsCompleted: false };
    public string? LastAction { get; private set; }
    public long LiveFrames { get; private set; }
    public long Published { get; private set; }
    public int LastContent => _content;
    public bool ZeroReachInjected(int ch) => _zeroReach[ch];
    public int RejectInjected(int ch) => _reject[ch];
    public ReadOnlySpan<byte> Thumb(int ch) => _thumb[ch].AsSpan(0, ThumbW * ThumbH(ch) * 3);
    public int ThumbH(int ch) => Router.Channel(ch).Variant.Aspect == VariantAspect.Vertical916 ? ThumbVH : ThumbSH;
    public ReadOnlySpan<byte> Frame => _frame;
    public ReadOnlySpan<byte> Source => _src;

    // ══════════════════════ 채널 ══════════════════════
    private void SeedChannels()
    {
        Router.Register("PromptFlow 메인", "@promptflow", ChannelTier.Primary, PublishPlatforms.YouTube, true,
            new ChannelVariantSpec { Aspect = VariantAspect.Vertical916, Logo = BrandLogo.Ring, SubtitleLang = DubLanguage.Ko, IntroSec = 1.0f, OutroSec = 1.0f, Accent = 0x7C3AED });
        Router.Register("PromptFlow Highlights", "@promptflow.hl", ChannelTier.Sub, PublishPlatforms.Instagram, true,
            new ChannelVariantSpec { Aspect = VariantAspect.Square11, Logo = BrandLogo.Hexagon, SubtitleLang = DubLanguage.En, IntroSec = 0.75f, OutroSec = 0.75f, Accent = 0xF97316 });
        Router.Register("PromptFlow Clips JP", "@promptflow.clips", ChannelTier.Clip, PublishPlatforms.YouTube, true,
            new ChannelVariantSpec { Aspect = VariantAspect.Vertical916, Logo = BrandLogo.Diamond, SubtitleLang = DubLanguage.Ja, IntroSec = 0.5f, OutroSec = 0.5f, Accent = 0x10B981 });
        for (var i = 0; i < Router.ChannelCount; i++) Renderer.SetSubtitle(i, DemoCaption, Router.Channel(i).Variant.SubtitleLang);
        for (var i = 0; i < Router.ChannelCount; i++) RenderPreview(i);
    }

    public void SetLogo(int ch, BrandLogo logo)
    {
        lock (_gate)
        {
            var c = Router.Channel(ch);
            var probe = c.Variant; probe.Logo = logo;
            for (var i = 0; i < Router.ChannelCount; i++)
                if (i != ch && ChannelVariantSpec.Differences(in Router.Channel(i).Variant, in probe) < CompliantMultiAccountMatrixRouterEngine.MinVariantDiff)
                {
                    LastAction = "⛔ 로고 변경 거부 — " + Router.Channel(i).Name + " 변형과 구별 축 부족";
                    Raise();
                    return;
                }
            c.Variant.Logo = logo;
            RenderPreview(ch);
        }
        LastAction = "🏷️ " + Router.Channel(ch).Name + " 로고 → " + ChannelVariantMultiFormatRenderer.LogoName(logo);
        Raise();
    }

    // ══════════════════════ 라우팅 · 시뮬레이션 ══════════════════════
    public int RouteNext()
    {
        int made;
        lock (_gate) made = Router.Route(++_content, SimNow, SimNow);
        LastAction = "🌐 콘텐츠 #" + _content.ToString(CultureInfo.InvariantCulture) + " → 변형 " + made.ToString(CultureInfo.InvariantCulture) + "건 시차 예약(≥ " + Router.StaggerMinutes.ToString(CultureInfo.InvariantCulture) + "분)";
        Raise();
        return made;
    }

    /// <summary>시뮬레이션 시계 전진(분) — 기한 도래 발행 · 거절 주입 · 도달 주입 · 30 분 감시 평가. 반환 = 발행 수.</summary>
    public int Advance(int minutes)
    {
        var published = 0;
        lock (_gate)
        {
            var end = SimNow + minutes * 60L;
            while (SimNow < end)
            {
                SimNow = Math.Min(end, SimNow + 300);
                while (Router.TakeDue(SimNow, out var idx))
                {
                    ref readonly var p = ref Router.At(idx);
                    var ch = p.Channel;
                    if (_reject[ch] > 0)
                    {
                        Router.MarkPublished(idx, SimNow, false);
                        Guard.ReportApi(ch, _reject[ch], SimNow);
                        continue;
                    }
                    Router.MarkPublished(idx, SimNow, true);
                    Guard.ReportApi(ch, 200, SimNow);
                    Guard.Track(ch, p.ContentId, SimNow);
                    if (!_zeroReach[ch]) Guard.ObserveReach(ch, p.ContentId, 800 + p.ContentId * 37L + ch * 113L);
                    published++;
                }
                Guard.Evaluate(SimNow);
            }
            Published += published;
        }
        LastAction = "⏩ +" + minutes.ToString(CultureInfo.InvariantCulture) + "분 · 발행 " + published.ToString(CultureInfo.InvariantCulture) + "건 · 동결 " + Guard.AwaitingCount.ToString(CultureInfo.InvariantCulture) + "계정";
        Raise();
        return published;
    }

    public void InjectZeroReach(int ch, bool on) { lock (_gate) _zeroReach[ch] = on; LastAction = "🧪 " + Router.Channel(ch).Name + " 도달 0 주입 " + (on ? "ON" : "OFF"); Raise(); }

    public void InjectReject(int ch, int status) { lock (_gate) _reject[ch] = status; LastAction = "🧪 " + Router.Channel(ch).Name + (status > 0 ? " API 거절 " + status.ToString(CultureInfo.InvariantCulture) + " 주입" : " 거절 주입 해제"); Raise(); }

    public int Approve(int ch)
    {
        int n;
        lock (_gate) { n = Guard.Approve(ch); if (n >= 0) { _zeroReach[ch] = false; _reject[ch] = 0; } }
        LastAction = n < 0 ? "ℹ️ 동결 상태 아님" : "✅ 관리자 승인 — " + Router.Channel(ch).Name + " 대기 " + n.ToString(CultureInfo.InvariantCulture) + "건 재개";
        Raise();
        return n;
    }

    public int Cancel(int ch)
    {
        int n;
        lock (_gate) { n = Guard.Cancel(ch); if (n >= 0) { _zeroReach[ch] = false; _reject[ch] = 0; } }
        LastAction = n < 0 ? "ℹ️ 동결 상태 아님" : "🗑️ 관리자 취소 — " + Router.Channel(ch).Name + " 대기 " + n.ToString(CultureInfo.InvariantCulture) + "건 폐기(타 계정 이관 없음)";
        Raise();
        return n;
    }

    // ══════════════════════ 렌더(무할당) ══════════════════════
    /// <summary>절차적 원본 9:16 프레임 — 세로 그라데이션 + 이동 피사체 원.</summary>
    public void SynthesizeSource(float t)
    {
        const int w = ChannelVariantMultiFormatRenderer.W, h = ChannelVariantMultiFormatRenderer.H;
        var cx = (int)(w * (0.5f + 0.25f * MathF.Sin(t * 0.9f))); var cy = (int)(h * (0.45f + 0.08f * MathF.Cos(t * 0.7f)));
        const int r2 = 260 * 260;
        for (var y = 0; y < h; y++)
        {
            var o = y * w * 3;
            var gr = (byte)(20 + y * 60 / h); var gg = (byte)(40 + y * 90 / h); var gb = (byte)(90 + y * 100 / h);
            var dy = y - cy; var dy2 = dy * dy;
            for (var x = 0; x < w; x++, o += 3)
            {
                var dx = x - cx;
                if (dx * dx + dy2 < r2) { _src[o] = 250; _src[o + 1] = (byte)(180 + (x & 31)); _src[o + 2] = 90; }
                else { _src[o] = gr; _src[o + 1] = gg; _src[o + 2] = gb; }
            }
        }
    }

    /// <summary>채널 변형 프레임 렌더 + 썸네일 갱신(무할당).</summary>
    public void RenderPreview(int ch)
    {
        SynthesizeSource(PreviewT);
        var c = Router.Channel(ch);
        Renderer.Render(in c.Variant, ch, _src, _frame, PreviewT, ClipSec);
        ChannelVariantMultiFormatRenderer.Thumbnail(_frame, ChannelVariantMultiFormatRenderer.W, ChannelVariantMultiFormatRenderer.OutHeight(c.Variant.Aspect), _thumb[ch], ThumbW, ThumbH(ch));
    }

    public void RenderAll() { lock (_gate) for (var i = 0; i < Router.ChannelCount; i++) RenderPreview(i); Raise(); }

    public void LiveTick()
    {
        lock (_gate)
        {
            PreviewT += 1f / LiveFps;
            if (PreviewT >= ClipSec) PreviewT = 0f;
            RenderPreview(Math.Clamp(Selected, 0, Router.ChannelCount - 1));
            LiveFrames++;
        }
    }

    public void Start()
    {
        if (Running) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew(); long lastRaise = 0;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    LiveTick();
                    if (sw.ElapsedMilliseconds - lastRaise >= 250) { lastRaise = sw.ElapsedMilliseconds; Raise(); }
                    await Task.Delay(1000 / LiveFps, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { PromptFlowStudio.Program.Log("MatrixSyndication.Loop", ex); }
        }, ct);
        LastAction = "▶ 변형 라이브 프리뷰";
        Raise();
    }

    public void Stop()
    {
        var cts = _cts; var loop = _loop;
        if (cts is null) return;
        cts.Cancel();
        try { loop?.Wait(1000); } catch (AggregateException) { }
        cts.Dispose(); _cts = null; _loop = null;
        LastAction = "⏸ 프리뷰 정지";
        Raise();
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("MatrixSyndication.OnChange", ex); }
    }

    public string Summary() =>
        "채널 " + Router.ChannelCount.ToString(CultureInfo.InvariantCulture) + " · 예약 " + Router.CountState(-1, MatrixPostState.Scheduled).ToString(CultureInfo.InvariantCulture) +
        " · 동결 " + Router.CountState(-1, MatrixPostState.Held).ToString(CultureInfo.InvariantCulture) + " · 발행 " + Router.Published.ToString(CultureInfo.InvariantCulture) +
        " · 안전 정지 " + Guard.Pauses.ToString(CultureInfo.InvariantCulture) + " · 알림 " + Guard.AlertsDispatched.ToString(CultureInfo.InvariantCulture) +
        " · 렌더 " + (Renderer.AvgRenderUs / 1000).ToString("0.0", CultureInfo.InvariantCulture) + "ms";

    public void Dispose()
    {
        Stop();
        OnChange = null;
        _ownedAlerts?.Dispose();
    }
}
