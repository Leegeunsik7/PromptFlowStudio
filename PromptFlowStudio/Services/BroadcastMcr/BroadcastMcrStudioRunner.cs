using System.Globalization;
using System.Numerics;
using PromptFlowStudio.Services.CinematicDirector;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.GlobalDubbing;
using PromptFlowStudio.Services.SplatEnvironment;
using PromptFlowStudio.Services.StageLighting;
using PromptFlowStudio.Services.StreamGateway;
using PromptFlowStudio.Services.Talkshow;

namespace PromptFlowStudio.Services.BroadcastMcr;

/// <summary>
/// [PromptFlow Studio v17.66.0] 🎛️ 브로드캐스트 주조정실(MCR) 스튜디오 러너(60 Hz 전용 스레드 · 틱 무할당).
///   ▸ v17.65 토크쇼(좌석 · 상호 시선 · 안면 52 · 자동 멀티캠) → 통합 3DGS 컴포지터 포즈/표정 · v17.39 4 캠 + PPL 인서트 캠 → MCR 소스 5.
///   ▸ v17.41 DMX 키 픽스처 → 접지 그림자 광원 · v17.64 다국어 라우터 플랫폼 트랙 → 송출 오디오 · v17.62 게이트웨이 압축 링 → 인코더 탭.
///   ▸ 자동 디렉팅: 토크쇼 라이브 캠 변경 → PVW 프리셋 → 투샷/와이드 = MIX 12 f · 그 외 = CUT. 결선 러너가 정지 상태면 MCR 틱이 프레임 동기로 대신 구동.
/// </summary>
/// <summary>v17.67 MCR 장면 레이어(시청자 물리 프롭 등) — MCR 틱 스레드에서 컴포지터 갱신 직전 호출(무할당).</summary>
public interface IMcrSceneLayer
{
    void Step(UnifiedGaussianSceneCompositorEngine comp, in SplatCamera pgm, double nowMs, float dt);
}

public sealed class BroadcastMcrStudioRunner : IDisposable
{
    public const int Hz = 60, Hist = 240, PreviewBytes = 54 + UnifiedGaussianSceneCompositorEngine.FrameBytes;
    public const float Dt = 1f / Hz, Aspect = (float)UnifiedGaussianSceneCompositorEngine.W / UnifiedGaussianSceneCompositorEngine.H;
    public static readonly int[] EnvPresets = { 60_000, 150_000, 300_000 };

    private readonly object _gate = new(), _prevGate = new();
    private readonly MultiAvatarTalkshowStudioRunner _talk;
    private readonly bool _ownTalk;
    private readonly GlobalDubbingStudioRunner? _dub;
    private readonly StreamGatewayClipperStudioRunner? _gateway;
    private readonly IntelligentStageLightingStudioRunner? _lights;
    private readonly float[] _audio = new float[BroadcastMcrMasterControlSynthesizer.TickSamples];
    private readonly float[] _msS = new float[Hist], _occS = new float[Hist], _vuS = new float[Hist];
    private readonly byte[] _pgmBmp = new byte[PreviewBytes], _pvwBmp = new byte[PreviewBytes];
    private readonly int _envSplats, _bodySplats, _faceSplats;
    private UnifiedGaussianSceneCompositorEngine? _comp;
    private int _sHead, _sN;
    private long _tick, _lastRouted = -1;
    private DirectorCam _lastLive = (DirectorCam)255;
    private int _cue = -1, _cueSource = (int)DirectorCam.Action;
    private long _cueTick, _holdUntil;
    public const int CueHoldFrames = 90;
    private double _phase;
    private Thread? _loop;
    private volatile bool _running;

    public BroadcastMcrStudioRunner(IServiceProvider sp) : this(
        sp.GetService(typeof(MultiAvatarTalkshowStudioRunner)) as MultiAvatarTalkshowStudioRunner,
        sp.GetService(typeof(GlobalDubbingStudioRunner)) as GlobalDubbingStudioRunner,
        sp.GetService(typeof(StreamGatewayClipperStudioRunner)) as StreamGatewayClipperStudioRunner,
        sp.GetService(typeof(IntelligentStageLightingStudioRunner)) as IntelligentStageLightingStudioRunner) { }

    public BroadcastMcrStudioRunner(MultiAvatarTalkshowStudioRunner? talk = null, GlobalDubbingStudioRunner? dub = null, StreamGatewayClipperStudioRunner? gateway = null,
        IntelligentStageLightingStudioRunner? lights = null, int envSplats = UnifiedGaussianSceneCompositorEngine.DefaultEnv, int bodySplats = UnifiedGaussianSceneCompositorEngine.DefaultBody,
        int faceSplats = UnifiedGaussianSceneCompositorEngine.DefaultFace)
    {
        _ownTalk = talk is null;
        _talk = talk ?? new MultiAvatarTalkshowStudioRunner(1500);
        _dub = dub; _gateway = gateway; _lights = lights;
        _envSplats = envSplats; _bodySplats = bodySplats; _faceSplats = faceSplats;
        LinkDubbing = dub is not null;
    }

    public event Action? OnChange;
    public BroadcastMcrMasterControlSynthesizer Mcr { get; } = new();
    public DynamicPplOcclusionBrandGuard Ppl { get; } = new();
    public UnifiedGaussianSceneCompositorEngine? Compositor => _comp;
    public MultiAvatarTalkshowStudioRunner Talkshow => _talk;
    public bool DubbingAvailable => _dub is not null;
    public bool GatewayAvailable => _gateway is not null;
    public bool LightsAvailable => _lights is not null;
    public bool LinkDubbing { get; private set; }
    public bool LinkLights { get; private set; }
    public bool TapGateway { get; private set; }
    public bool AutoDirect { get; private set; } = true;
    /// <summary>v17.67 장면 레이어(쇼러너 물리 스포너) · 클로즈업 큐 지표.</summary>
    public IMcrSceneLayer? Layer { get; set; }
    private readonly IMcrSceneLayer?[] _extra = new IMcrSceneLayer?[4];
    /// <summary>v17.68 추가 장면 레이어(라이브 커머스 등 · 최대 4) — <see cref="Layer"/> 다음 순서로 호출.</summary>
    public bool AttachLayer(IMcrSceneLayer l) { lock (_gate) { for (var i = 0; i < _extra.Length; i++) if (ReferenceEquals(_extra[i], l)) return true; for (var i = 0; i < _extra.Length; i++) if (_extra[i] is null) { _extra[i] = l; return true; } } return false; }
    public void DetachLayer(IMcrSceneLayer l) { lock (_gate) for (var i = 0; i < _extra.Length; i++) if (ReferenceEquals(_extra[i], l)) _extra[i] = null; }
    /// <summary>v17.68 인서트 캠(소스 5) 타깃 재지정 — null 이면 PPL 소품.</summary>
    public System.Numerics.Vector3? InsertTarget { get; set; }
    public float InsertDistance { get; set; } = 0.62f;
    public long CueCuts { get; private set; }
    public long LastCueLatencyFrames { get; private set; }
    public double NowMs => Tick * 1000.0 / Hz;
    public bool TestTone { get; private set; }
    public ChatPlatform AudioPlatform { get; private set; } = ChatPlatform.Twitch;
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public double MaxTickMs { get; private set; }
    public long FramesOverBudget { get; private set; }
    public string? LastAction { get; private set; }
    public int EnvSplats => _envSplats;
    public int SeriesCount => _sN;
    private int Idx(int i) => (_sHead - 1 - i + 2 * Hist) % Hist;
    public float FrameMsSeries(int i) => _msS[Idx(i)];
    public float OcclusionSeries(int i) => _occS[Idx(i)];
    public float VuSeries(int i) => _vuS[Idx(i)];

    // ══════════════════════ 제어(비핫패스) ══════════════════════
    private void EnsureScene()
    {
        if (_comp is not null) return;
        _comp = new UnifiedGaussianSceneCompositorEngine(_envSplats, _bodySplats, _faceSplats, _talk.Active);
        Ppl.Anchor = UnifiedGaussianSceneCompositorEngine.DeskCenter + new Vector3(0.22f, 0f, 0.08f);
    }

    public void Prepare() { lock (_gate) EnsureScene(); }

    public bool Take(McrTransition t, int frames = 12)
    {
        bool ok; lock (_gate) ok = Mcr.Take(t, frames);
        if (ok) { LastAction = "🎬 " + BroadcastMcrMasterControlSynthesizer.TransitionName(t) + " → PGM " + BroadcastMcrMasterControlSynthesizer.SourceName(Mcr.Preview); Raise(); }
        return ok;
    }

    /// <summary>v17.67 쇼러너 트리거 — 피격 아바타 리액션 CU(v17.65 액션 캠 즉시 재구도) → 다음 MCR 프레임 PGM 컷 · 1.5 s 자동 디렉팅 보류(무할당).</summary>
    public void CueCloseUp(int avatar)
    {
        lock (_gate)
        {
            _talk.Guard.ForceShot(TalkShot.Reaction, avatar, _talk.NowMs);
            _cue = avatar; _cueTick = Tick; _cueSource = (int)DirectorCam.Action;
        }
    }

    /// <summary>v17.68 상품 클로즈업 — 인서트 캠(소스 5)을 상품으로 재지정 → 다음 MCR 프레임 PGM 컷 · 자동 디렉팅 보류(무할당 · MCR 틱 스레드 호출 허용).</summary>
    public void CueInsert(System.Numerics.Vector3 target, float distance = 0.62f)
    {
        InsertTarget = target; InsertDistance = Math.Clamp(distance, 0.2f, 3f);
        _cue = 0; _cueTick = Tick; _cueSource = 4;
    }

    public void SetPreview(int s) { lock (_gate) Mcr.SetPreview(s); LastAction = "🟩 PVW ← " + BroadcastMcrMasterControlSynthesizer.SourceName(s); Raise(); }
    public void SetAutoDirect(bool on) { AutoDirect = on; LastAction = on ? "🤖 v17.65 자동 멀티캠 → MCR 자동 디렉팅" : "🎛 수동 스위칭"; Raise(); }
    public void SetMasterGain(float g) { Mcr.MasterGain = Math.Clamp(g, 0f, 2f); Raise(); }
    public void SetMute(bool m) { Mcr.Mute = m; LastAction = m ? "🔇 마스터 뮤트" : "🔊 마스터 온"; Raise(); }
    public void SetTestTone(bool on) { TestTone = on; LastAction = on ? "🎵 1 kHz −18 dBFS 톤" : "🎵 톤 OFF"; Raise(); }
    public void SetAudioPlatform(ChatPlatform p) { AudioPlatform = p; _lastRouted = -1; LastAction = "🌍 송출 오디오 = " + MultiPlatformLiveChatGatewaySynthesizer.PlatformName(p) + " 트랙"; Raise(); }
    public void SetLinkDubbing(bool on) { LinkDubbing = on && _dub is not null; _lastRouted = -1; LastAction = LinkDubbing ? "🔗 v17.64 다국어 라우터 오디오" : "🔗 오디오 결선 해제"; Raise(); }
    public void SetLinkLights(bool on) { LinkLights = on && _lights is not null; LastAction = LinkLights ? "💡 v17.41 DMX 키 → 접지 그림자" : "💡 기본 키 라이트"; Raise(); }
    public void SetPplEnabled(bool on) { Ppl.Enabled = on; LastAction = on ? "🛡 PPL 브랜드 가드 ON" : "🛡 PPL 가드 OFF"; Raise(); }

    public void ResetMetrics()
    {
        lock (_gate) { Mcr.ResetMetrics(); Ppl.ResetMetrics(); MaxTickMs = 0; FramesOverBudget = 0; }
        LastAction = "♻ 지표 초기화"; Raise();
    }

    /// <summary>송출 시작 — 인코더(v17.49 하드웨어 → 관리 폴백) · v17.62 게이트웨이 링 탭(게이트웨이 자체 인코더 미사용 시).</summary>
    public bool StartOnAir(bool tapGateway, bool allowMf = true)
    {
        bool ok;
        lock (_gate)
        {
            var tap = tapGateway && _gateway is { IsEncoding: false, TapTranscoder: false } ? _gateway.Ring : null;
            ok = Mcr.OpenEncoder(new DiscardSeekableStream(), tap, allowMf);
            TapGateway = ok && tap is not null;
        }
        LastAction = ok ? "🔴 ON-AIR · " + Mcr.EncoderName + (TapGateway ? " → v17.62 압축 링" : "") : "⚠ 인코더 열기 실패";
        Raise();
        return ok;
    }

    public void StopOnAir() { lock (_gate) { Mcr.CloseEncoder(); TapGateway = false; } LastAction = "⏹ 송출 정지"; Raise(); }

    // ══════════════════════ 60 Hz 틱(무할당) ══════════════════════
    public void Step()
    {
        lock (_gate)
        {
            EnsureScene();
            using var zg = ZeroGcRegionScope.Enter("Mcr.Step");
            Compute();
        }
    }

    private void Compute()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _tick);
        var comp = _comp!;
        var talk = _talk;
        // ① v17.65 토크쇼(정지 시 프레임 동기 구동) → 포즈 · 표정
        if (!talk.IsRunning) talk.Step();
        var n = Math.Min(comp.Avatars, talk.Active);
        for (var a = 0; a < n; a++)
        {
            ref readonly var p = ref talk.Gaze.Pose(a);
            comp.SetAvatarPose(a, talk.Seat(a), talk.Gaze.BodyYaw(a), p.HeadYaw, p.HeadPitch + p.Nod);
            comp.SetFaceWeights(a, talk.Face(a).Weights);
        }
        // ② v17.41 DMX 키 라이트
        if (LinkLights && _lights is not null) KeyFromStage(_lights);
        // ③ 소스 카메라(v17.39 4 캠 + PPL 인서트)
        var fr = talk.Guard.Framing;
        for (var c = 0; c < 4; c++)
        {
            ref readonly var rig = ref fr.Rig(c);
            var fwd = rig.Forward.LengthSquared() > 0.5f ? rig.Forward : -Vector3.UnitZ;
            Mcr.SetSource(c, SplatCamera.LookAt(rig.Position, rig.Position + fwd, Math.Clamp(rig.VFovDeg, 8f, 80f), Aspect, 0.05f, 120f));
        }
        var prop = InsertTarget ?? Ppl.Position + new Vector3(0f, UnifiedGaussianSceneCompositorEngine.PropHeight * 0.5f, 0f);
        var k = InsertDistance / 0.62f;
        Mcr.SetSource(4, SplatCamera.LookAt(prop + new Vector3(0.18f * k, 0.14f * k, InsertDistance), prop, 30f, Aspect, 0.05f, 120f));
        // ④ PPL 가드(PGM 카메라 기준) → 소품 · 컴포지터 갱신
        Ppl.Collect(comp);
        Ppl.Step(in Mcr.Camera(Mcr.Program), Dt);
        comp.SetProp(Ppl.Position, true);
        Layer?.Step(comp, in Mcr.Camera(Mcr.Program), NowMs, Dt);
        for (var li = 0; li < _extra.Length; li++) _extra[li]?.Step(comp, in Mcr.Camera(Mcr.Program), NowMs, Dt);
        // v17.70 스탠바이 전면 대체 중 = 주 장면 갱신 · 디렉팅 생략(전환 경로 최단 · 렌더 부하 소거)
        var standby = Mcr.Standby is { Engaged: true };
        if (!standby) comp.Update();
        // ⑤ 쇼러너 긴급 클로즈업 큐(최우선 · 진행 중 트랜지션은 즉시 종료 후 컷) → 자동 디렉팅
        if (_cue >= 0 && !standby)
        {
            var src = _cueSource;
            Mcr.AbortTransition();
            Mcr.SetPreview(src);
            if (Mcr.Program != src) Mcr.Take(McrTransition.Cut);
            LastCueLatencyFrames = Tick - _cueTick; CueCuts++;
            _cue = -1; _holdUntil = Tick + CueHoldFrames; _lastLive = talk.Guard.LiveCam;
        }
        if (AutoDirect && !standby && !Mcr.Transitioning && Tick >= _holdUntil)
        {
            var live = talk.Guard.LiveCam;
            if (live != _lastLive)
            {
                _lastLive = live;
                var s = (int)live;
                if (s != Mcr.Program)
                {
                    Mcr.SetPreview(s);
                    var wide = talk.Guard.Shot is TalkShot.TwoShot or TalkShot.Wide;
                    Mcr.Take(wide ? McrTransition.Mix : McrTransition.Cut, 12);
                }
            }
        }
        // ⑥ 오디오(v17.64 플랫폼 트랙 · 위치 추적 = 정확히 새 샘플만) → 프레임
        PushAudio();
        Mcr.Frame(comp);
        var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        LastTickMs = ms; if (ms > MaxTickMs && _tick > 5) MaxTickMs = ms;
        if (ms > 1000.0 / Hz) FramesOverBudget++;
        _msS[_sHead] = (float)ms; _occS[_sHead] = Ppl.OcclusionRatio; _vuS[_sHead] = Mcr.RmsDb;
        _sHead = (_sHead + 1) % Hist; if (_sN < Hist) _sN++;
    }

    private void PushAudio()
    {
        var tick = BroadcastMcrMasterControlSynthesizer.TickSamples;
        if (LinkDubbing && _dub is not null)
        {
            if (!_dub.IsRunning) _dub.Step();
            var r = _dub.Router;
            var pos = r.Routed(AudioPlatform);
            if (_lastRouted < 0) _lastRouted = Math.Max(0, pos - tick);
            var fresh = (int)Math.Clamp(pos - _lastRouted, 0, _audio.Length);
            _lastRouted = pos;
            if (fresh > 0)
            {
                var got = r.ReadLive(AudioPlatform, _audio.AsSpan(0, fresh));
                Mcr.PushAudio(_audio.AsSpan(0, got));
                return;
            }
            return;
        }
        var amp = TestTone ? 0.125f : 0f;
        var dphi = 2 * Math.PI * 1000 / BroadcastMcrMasterControlSynthesizer.SampleRate;
        for (var i = 0; i < tick; i++) { _phase += dphi; if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI; _audio[i] = amp * (float)Math.Sin(_phase); }
        Mcr.PushAudio(_audio);
    }

    private void KeyFromStage(IntelligentStageLightingStudioRunner li)
    {
        var center = new Vector3(0f, 1f, 0.3f);
        for (var i = 0; i < RealtimeIntelligentStageLightingEngine.Fixtures; i++)
        {
            ref readonly var f = ref li.Engine.Fixture(i);
            if (f.Role != LightRole.Key) continue;
            var dir = f.Position - f.Aim;
            if (dir.LengthSquared() < 1e-4f) continue;
            dir = Vector3.Normalize(dir);
            if (dir.Y < 0.25f) dir = Vector3.Normalize(new Vector3(dir.X, 0.25f, dir.Z));
            _comp!.SetKeyLight(center + dir * 5.5f);
            return;
        }
    }

    public void WritePreviews()
    {
        lock (_prevGate)
        {
            lock (_gate)
            {
                BroadcastMcrMasterControlSynthesizer.WriteBmp(Mcr.ProgramOut, _pgmBmp);
                BroadcastMcrMasterControlSynthesizer.WriteBmp(Mcr.PreviewOut, _pvwBmp);
            }
        }
    }

    public void CopyPreview(bool program, byte[] dst) { lock (_prevGate) (program ? _pgmBmp : _pvwBmp).AsSpan(0, Math.Min(dst.Length, PreviewBytes)).CopyTo(dst); }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            EnsureScene();
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.Mcr" };
            _loop.Start();
        }
        LastAction = "▶ MCR 60 Hz · 통합 3DGS 컴포지트 · PGM/PVW";
        Raise();
    }

    public void Stop()
    {
        Thread? t;
        lock (_gate) { if (!_running) return; _running = false; t = _loop; _loop = null; }
        t?.Join(3000);
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or System.Runtime.InteropServices.COMException) { PromptFlowStudio.Program.Log("Mcr.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }
            if (n % 6 == 0) Raise();
        }
    }

    public string Summary() =>
        "PGM " + BroadcastMcrMasterControlSynthesizer.SourceName(Mcr.Program) + " · 테이크 " + Mcr.Takes.ToString(CultureInfo.InvariantCulture) + " · A/V " + Mcr.AvSyncFrames.ToString(CultureInfo.InvariantCulture)
        + " f · PPL 가림 " + (Ppl.OcclusionRatio * 100f).ToString("0", CultureInfo.InvariantCulture) + " %";

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Mcr.OnChange", ex); }
    }

    public void Dispose()
    {
        Stop();
        Mcr.Dispose();
        _comp?.Dispose();
        if (_ownTalk) _talk.Dispose();
    }
}
