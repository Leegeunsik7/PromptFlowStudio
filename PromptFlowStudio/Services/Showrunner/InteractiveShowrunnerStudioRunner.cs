using System.Globalization;
using PromptFlowStudio.Services.BroadcastMcr;
using PromptFlowStudio.Services.CinematicDirector;
using PromptFlowStudio.Services.SplatEnvironment;
using PromptFlowStudio.Services.StreamGateway;
using PromptFlowStudio.Services.Talkshow;

namespace PromptFlowStudio.Services.Showrunner;

/// <summary>
/// [PromptFlow Studio v17.67.0] 🎪 AI 자율 쇼러너 &amp; 인터랙티브 스튜디오 러너 — v17.66 MCR 장면 레이어(<see cref="IMcrSceneLayer"/>).
///   ▸ 수신: v17.62 게이트웨이 커맨드 탭(세이프티 통과 채팅) · 시뮬레이션 버튼 → 가드 수신 링(락 1 회 · 무할당).
///   ▸ MCR 틱(컴포지터 갱신 직전): 버킷 리필 → 틱당 ≤ 48 커맨드 파싱 · 가격 · 쿨다운 · 버킷 판정 → 풀 만석 시 카메라 밖 최고령 → 최고령 재활용 → 스폰 →
///     골격 캡슐 갱신 · 2 서브스텝 물리 · 시청자 슬롯 스플랫 기록 → 쇼러너(피격 애드리브 · 표정 · 리액션 CU) → 레이어 비용 관측(초과 시 유입 반감).
///   ▸ 쇼러너 출력은 v17.65 토크쇼(FSM 강제 발언권 · 감정 주입)와 v17.66 MCR(클로즈업 · 와이드 큐)로 같은 프레임에 디스패치.
/// </summary>
public sealed class InteractiveShowrunnerStudioRunner : IMcrSceneLayer, IShowrunnerOutput, IChatDispatchTarget, IDisposable
{
    public const int Hist = 240;
    public static readonly int[] DefaultPrices = { 1000, 2000, 0, 0 };

    private readonly BroadcastMcrStudioRunner _mcr;
    private readonly bool _ownMcr;
    private readonly StreamGatewayClipperStudioRunner? _gateway;
    private readonly int[] _price = (int[])DefaultPrices.Clone();
    private readonly long[] _byKind = new long[4];
    private readonly char[] _user = new char[PhysicsSpamPoolingSafetyGuard.UserCap], _text = new char[PhysicsSpamPoolingSafetyGuard.TextCap];
    private readonly char[] _confettiUser = new char[PhysicsSpamPoolingSafetyGuard.UserCap];
    private readonly float[] _layerS = new float[Hist], _activeS = new float[Hist];
    private int _sHead, _sN;
    private long _layerFrames;

    public InteractiveShowrunnerStudioRunner(IServiceProvider sp) : this(
        sp.GetService(typeof(BroadcastMcrStudioRunner)) as BroadcastMcrStudioRunner,
        sp.GetService(typeof(StreamGatewayClipperStudioRunner)) as StreamGatewayClipperStudioRunner) { }

    public InteractiveShowrunnerStudioRunner(BroadcastMcrStudioRunner? mcr = null, StreamGatewayClipperStudioRunner? gateway = null)
    {
        _ownMcr = mcr is null;
        _mcr = mcr ?? new BroadcastMcrStudioRunner();
        _gateway = gateway;
        _mcr.Layer = this;
    }

    public event Action? OnChange;
    public BroadcastMcrStudioRunner Mcr => _mcr;
    public MultiAvatarTalkshowStudioRunner Talkshow => _mcr.Talkshow;
    public ViewerInteractivePhysicsSpawnerEngine Spawner { get; } = new();
    public PhysicsSpamPoolingSafetyGuard Guard { get; } = new();
    public AutonomousShowrunnerAdlibSynthesizer Showrunner { get; } = new();
    public bool GatewayAvailable => _gateway is not null;
    public bool LinkGateway { get; private set; }
    public bool IsRunning => _mcr.IsRunning;
    public double LastLayerMs { get; private set; }
    public double MaxLayerMs { get; private set; }
    public long LayerOverBudget { get; private set; }
    public long GcStalls { get; private set; }
    public long LayerFrames => _layerFrames;
    public long Malformed { get; private set; }
    public string? LastAction { get; private set; }
    public int Price(DropKind k) => _price[(int)k];
    public long SpawnedBy(DropKind k) => _byKind[(int)k];
    public int SeriesCount => _sN;
    private int Idx(int i) => (_sHead - 1 - i + 2 * Hist) % Hist;
    public float LayerMsSeries(int i) => _layerS[Idx(i)];
    public float ActiveSeries(int i) => _activeS[Idx(i)];

    // ══════════════════════ 제어(비핫패스) ══════════════════════
    public void SetPrice(DropKind k, int won) { _price[(int)k] = Math.Clamp(won, 0, 1_000_000); LastAction = "💰 " + ViewerInteractivePhysicsSpawnerEngine.KindName(k) + " 단가 " + _price[(int)k].ToString("N0", CultureInfo.InvariantCulture) + "원"; Raise(); }
    public void SetGuard(bool on) { Guard.Enabled = on; LastAction = on ? "🛡 스팸 가드 ON(40 객체/s · 1.5 s 사용자 쿨다운 · 풀 500)" : "⛔ 스팸 가드 OFF"; Raise(); }
    public void SetShowrunner(bool on) { Showrunner.Enabled = on; LastAction = on ? "🎬 AI 쇼러너 개입 ON" : "🎬 쇼러너 OFF(대본 유지)"; Raise(); }

    public void SetLinkGateway(bool on)
    {
        if (on && _gateway is null) return;
        LinkGateway = on;
        if (_gateway is not null) _gateway.CommandTap = on ? this : null;
        LastAction = on ? "🔗 v17.62 게이트웨이 채팅 커맨드 결선(!drop · !confetti)" : "🔗 게이트웨이 해제";
        Raise();
    }

    /// <summary>시뮬레이션 커맨드(UI · 테스트) — 게이트웨이와 동일 수신 링.</summary>
    public bool Submit(string user, string text, int amount = 0) => Guard.Offer(user, text, amount, (long)_mcr.NowMs);

    /// <summary>스팸 공격 시뮬레이션 — <paramref name="count"/> 커맨드 일괄(사용자 <paramref name="users"/> 명 순환).</summary>
    public int Flood(int count, int users = 200)
    {
        Span<char> u = stackalloc char[8];
        var ok = 0; var now = (long)_mcr.NowMs;
        for (var i = 0; i < count; i++)
        {
            u[0] = 'b'; u[1] = 'o'; u[2] = 't';
            (i % users).TryFormat(u[3..], out var w, default, CultureInfo.InvariantCulture);
            if (Guard.Offer(u[..(3 + w)], (i & 3) == 3 ? "!confetti" : "!drop ball", 0, now)) ok++;
        }
        LastAction = "🌊 스팸 공격 " + count.ToString("N0", CultureInfo.InvariantCulture) + " 커맨드 → 수신 링 " + ok.ToString("N0", CultureInfo.InvariantCulture);
        Raise();
        return ok;
    }

    public void ResetMetrics()
    {
        Guard.ResetMetrics(); Showrunner.ResetMetrics(); MaxLayerMs = 0; LayerOverBudget = 0; GcStalls = 0; Malformed = 0;
        LastAction = "♻ 지표 초기화"; Raise();
    }

    public void ClearProps() { Spawner.Clear(); LastAction = "🧹 물리 객체 전량 회수"; Raise(); }
    public void Start() { _mcr.Start(); LastAction = "▶ MCR · 인터랙티브 물리 · 쇼러너"; Raise(); }
    public void Stop() { _mcr.Stop(); LastAction = "⏸ 정지"; Raise(); }
    /// <summary>MCR 프레임 1 개(정지 상태 · 테스트).</summary>
    public void Step() => _mcr.Step();

    // ══════════════════════ IChatDispatchTarget(게이트웨이 스레드) ══════════════════════
    public bool Deliver(ChatPlatform platform, ReadOnlySpan<char> user, ReadOnlySpan<char> text, int amount, ChatRole role, long nowMs)
        => Guard.Offer(user, text, amount, (long)_mcr.NowMs);

    // ══════════════════════ IMcrSceneLayer(MCR 틱 · 무할당) ══════════════════════
    public void Step(UnifiedGaussianSceneCompositorEngine comp, in SplatCamera pgm, double nowMs, float dt)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var gc0 = GC.CollectionCount(0);
        var now = (long)nowMs;
        Guard.Refill(nowMs);
        Spawner.UpdateColliders(comp);
        var confetti = 0; var cuLen = 0;
        for (var k = 0; k < PhysicsSpamPoolingSafetyGuard.DrainPerFrame; k++)
        {
            if (!Guard.TryDequeue(_user, _text, out var cmd)) break;
            Showrunner.CountCommand();
            if (!ViewerInteractivePhysicsSpawnerEngine.Parse(_text.AsSpan(0, cmd.TextLen), out var kind, out var target, out var cost)) { Guard.Unknown(); Malformed++; continue; }
            if (Guard.Admit(cmd.UserHash, cmd.Amount, _price[(int)kind], cost, now) != SpawnVerdict.Admitted) continue;
            var need = kind == DropKind.Confetti ? ViewerInteractivePhysicsSpawnerEngine.ConfettiPieces : 1;
            while (Spawner.FreeSlots < need)
            {
                var victim = Spawner.OldestSlot(in pgm, offscreenOnly: true);
                if (victim < 0) victim = Spawner.OldestSlot(in pgm, offscreenOnly: false);
                if (victim < 0) break;
                Spawner.Recycle(victim); Guard.Recycled++;
            }
            Spawner.RememberUser(cmd.UserHash, _user.AsSpan(0, cmd.UserLen));
            if (Spawner.Spawn(kind, target, cmd.UserHash) > 0) _byKind[(int)kind]++;
            if (kind == DropKind.Confetti) { confetti++; cuLen = cmd.UserLen; _user.AsSpan(0, cuLen).CopyTo(_confettiUser); }
        }
        Spawner.Step(dt);
        Spawner.Write(comp.Scene, comp.ViewerStart);
        Showrunner.Step(Spawner, this, confetti, _confettiUser.AsSpan(0, cuLen));
        var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Guard.ObserveLayer(ms, nowMs);
        _layerFrames++;
        LastLayerMs = ms; if (ms > MaxLayerMs && _layerFrames > 3) MaxLayerMs = ms;
        // GC 겹침 프레임은 판정 불가(Inconclusive) — 레이어 자체는 무할당이므로 타 스레드 할당 기인 · 위반과 분리 집계
        if (GC.CollectionCount(0) != gc0) GcStalls++;
        else if (ms > 1000.0 / 60) LayerOverBudget++;
        _layerS[_sHead] = (float)ms; _activeS[_sHead] = Spawner.Active; _sHead = (_sHead + 1) % Hist; if (_sN < Hist) _sN++;
    }

    // ══════════════════════ IShowrunnerOutput(MCR 틱 · 무할당) ══════════════════════
    public bool InjectAdlib(int avatar, ReadOnlySpan<char> line) => _mcr.Talkshow.InjectAdlib(avatar, line);
    public void Provoke(int avatar, float surprise, float anger) => _mcr.Talkshow.Gaze.Provoke(avatar, surprise, anger);
    public void CueCloseUp(int avatar) => _mcr.CueCloseUp(avatar);
    public void CueWide() { _mcr.Talkshow.Guard.ForceShot(_mcr.Talkshow.Active > 2 ? TalkShot.Wide : TalkShot.TwoShot, 0, _mcr.Talkshow.NowMs); _mcr.SetPreview((int)DirectorCam.Master); }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Showrunner.OnChange", ex); }
    }

    public void Dispose()
    {
        if (ReferenceEquals(_mcr.Layer, this)) _mcr.Layer = null;
        if (_gateway is not null && ReferenceEquals(_gateway.CommandTap, this)) _gateway.CommandTap = null;
        if (_ownMcr) _mcr.Dispose();
    }

    public string Summary() =>
        "활성 " + Spawner.Active.ToString(CultureInfo.InvariantCulture) + " · 스폰 " + Spawner.Spawned.ToString(CultureInfo.InvariantCulture) + " · 피격 " + Spawner.Hits.ToString(CultureInfo.InvariantCulture)
        + " · 개입 " + Showrunner.Interventions.ToString(CultureInfo.InvariantCulture) + " · 레이어 최대 " + MaxLayerMs.ToString("0.00", CultureInfo.InvariantCulture) + " ms";
}
