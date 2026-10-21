using System.Globalization;
using PromptFlowStudio.Services.BeatSync;
using PromptFlowStudio.Services.BroadcastMcr;
using PromptFlowStudio.Services.GlobalDubbing;
using PromptFlowStudio.Services.LiveShowroom;
using PromptFlowStudio.Services.Localization.Dubbing;
using PromptFlowStudio.Services.OmniChannel;
using PromptFlowStudio.Services.Showrunner;
using PromptFlowStudio.Services.StreamGateway;
using PromptFlowStudio.Services.VerticalTranscode;

namespace PromptFlowStudio.Services.LiveToReels;

public enum FactoryReelPhase : byte { Clipping, Packaging, Ready, Publishing, Published, Failed }

/// <summary>v17.69.0 팩토리 릴 1 개(감지 → 클립 → 패키지 → 배포 상태).</summary>
public sealed class FactoryReel : IDisposable
{
    public int Id { get; init; }
    public ClimaxClipRequest Request { get; init; }
    public DateTime CreatedUtc { get; init; }
    public FactoryReelPhase Phase { get; internal set; }
    public string Caption { get; internal set; } = "";
    public SegmentedChunkStream? Media { get; internal set; }
    public long Bytes => Media?.Length ?? 0;
    public int Tracks { get; internal set; }
    public int DubTracks { get; internal set; }
    public int Cues { get; internal set; }
    public bool CaptionSafe { get; internal set; }
    public double DurationSec { get; internal set; }
    public double PackMs { get; internal set; }
    public double ClipMs { get; internal set; }
    public string? Failure { get; internal set; }
    public ReelsUploadJob? Job { get; internal set; }
    public bool HandedOff { get; internal set; }
    public void Dispose() { Media?.Dispose(); Media = null; }
}

/// <summary>
/// [PromptFlow Studio v17.69.0] 🏭 라이브-투-릴스 자동 팩토리 러너.
///   ▸ 감지 스레드(60 Hz): v17.62 채팅 수락 · v17.67 피격/헤드샷 · v17.68 판매 수량/품절 카운터 델타 폴링 → <see cref="RealtimeViralClimaxDetectorEngine"/>(무할당).
///   ▸ 패키징 워커(Lowest): 클라이맥스 요청 링 → v17.62 무손실 클립(<see cref="StreamGatewayClipperStudioRunner.ClipNow"/> · 미가동 시 합성 fMP4 소스) →
///     <see cref="LiveToReelsPackagingPipelineSynthesizer"/>(SDF 자막 · BGM 훅 · 더빙 트랙) → 릴 큐 → <see cref="NonBlockingOmnichannelPublishingGuard"/> 3 플랫폼 병렬 업로드.
///   ▸ v17.64 더빙 레인 캡처(<see cref="IDubAudioSink"/> 체인 · 30 s 링) · v17.53 BPM · v17.66 MCR 틱 ms 프로브 · v17.51 핸드오프(배압 여유 시에만).
/// </summary>
public sealed class LiveToReelsFactoryStudioRunner : IClimaxClipDispatcher, IDubAudioSink, IDisposable
{
    public const int MaxReels = 12, ReqRing = 8, DubSec = 30, DubCap = DubSec * 48000, Hz = 60;

    private readonly StreamGatewayClipperStudioRunner? _gateway;
    private readonly InteractiveShowrunnerStudioRunner? _showrunner;
    private readonly LiveCommerceStudioRunner? _commerce;
    private readonly GlobalDubbingStudioRunner? _dubbing;
    private readonly BeatSyncAudioMixStudioRunner? _beat;
    private readonly BroadcastMcrStudioRunner? _mcr;
    private readonly OmniChannelEngagementStudioRunner? _omni;
    private readonly List<FactoryReel> _reels = new(MaxReels);
    private readonly ClimaxClipRequest[] _req = new ClimaxClipRequest[ReqRing];
    private readonly object _reqGate = new();
    private readonly AutoResetEvent _packEvt = new(false);
    private readonly DubLanguage[] _dubLang = new DubLanguage[ReelsPackageSpec.MaxDubs];
    private readonly long[] _dubLastPos = new long[ReelsPackageSpec.MaxDubs];
    private readonly int[] _dubHead = new int[ReelsPackageSpec.MaxDubs], _dubFill = new int[ReelsPackageSpec.MaxDubs];
    private short[][]? _dubRing;
    private int _dubLanes;
    private IDubAudioSink? _prevSink;
    private int _reqHead, _reqN, _reelId, _simLeft;
    private long _lastChat = -1, _lastHits = -1, _lastHead = -1, _lastUnits = -1, _clock;
    private bool _lastSoldOut;
    private Thread? _detect, _pack;
    private volatile bool _running, _packing;
    private SegmentedChunkStream? _synthetic;
    private double _syntheticSec;

    public LiveToReelsFactoryStudioRunner(IServiceProvider sp) : this(
        sp.GetService(typeof(StreamGatewayClipperStudioRunner)) as StreamGatewayClipperStudioRunner,
        sp.GetService(typeof(InteractiveShowrunnerStudioRunner)) as InteractiveShowrunnerStudioRunner,
        sp.GetService(typeof(LiveCommerceStudioRunner)) as LiveCommerceStudioRunner,
        sp.GetService(typeof(GlobalDubbingStudioRunner)) as GlobalDubbingStudioRunner,
        sp.GetService(typeof(BeatSyncAudioMixStudioRunner)) as BeatSyncAudioMixStudioRunner,
        sp.GetService(typeof(BroadcastMcrStudioRunner)) as BroadcastMcrStudioRunner,
        sp.GetService(typeof(OmniChannelEngagementStudioRunner)) as OmniChannelEngagementStudioRunner) { }

    public LiveToReelsFactoryStudioRunner(StreamGatewayClipperStudioRunner? gateway = null, InteractiveShowrunnerStudioRunner? showrunner = null, LiveCommerceStudioRunner? commerce = null,
        GlobalDubbingStudioRunner? dubbing = null, BeatSyncAudioMixStudioRunner? beat = null, BroadcastMcrStudioRunner? mcr = null, OmniChannelEngagementStudioRunner? omni = null)
    {
        _gateway = gateway; _showrunner = showrunner; _commerce = commerce; _dubbing = dubbing; _beat = beat; _mcr = mcr; _omni = omni;
        Detector.Dispatcher = this;
        Guard.FrameProbe = ProbeFrameMs;
        Guard.OnJobFinished += OnJobFinished;
        if (gateway is not null) Guard.LiveKbps = gateway.Kbps;
    }

    public event Action? OnChange;
    public RealtimeViralClimaxDetectorEngine Detector { get; } = new();
    public LiveToReelsPackagingPipelineSynthesizer Packager { get; } = new();
    public NonBlockingOmnichannelPublishingGuard Guard { get; } = new();
    public ReelsPackageSpec Spec { get; } = new();
    public bool GatewayAvailable => _gateway is not null;
    public bool ShowrunnerAvailable => _showrunner is not null;
    public bool CommerceAvailable => _commerce is not null;
    public bool DubbingAvailable => _dubbing is not null;
    public bool BeatAvailable => _beat is not null;
    public bool McrAvailable => _mcr is not null;
    public bool OmniAvailable => _omni is not null;
    public bool LinkDubbing { get; private set; }
    public bool LinkOmni { get; private set; }
    public bool AutoPublish { get; private set; } = true;
    public bool SimTraffic { get; private set; }
    public bool IsRunning => _running;
    public long NowMs => _clock;
    public long Ticks { get; private set; }
    public long ClipFallbacks { get; private set; }
    public long Packaged { get; private set; }
    public long PackFailures { get; private set; }
    public long Published { get; private set; }
    public long HandOffs { get; private set; }
    public long FramesSampled { get; private set; }
    public long FramesOverBudget { get; private set; }
    public double MaxFrameMs { get; private set; }
    public string? LastAction { get; private set; }
    public int DubLanes => _dubLanes;
    public DubLanguage DubLane(int i) => _dubLang[i];
    public int ReelCount { get { lock (_reels) return _reels.Count; } }
    public FactoryReel? Reel(int i) { lock (_reels) return (uint)i < (uint)_reels.Count ? _reels[i] : null; }
    public FactoryReel? FindReel(int id) { lock (_reels) { foreach (var r in _reels) if (r.Id == id) return r; } return null; }
    public int PendingRequests { get { lock (_reqGate) return _reqN; } }

    private double ProbeFrameMs() => _mcr is { IsRunning: true } m ? m.LastTickMs : _gateway is { IsRunning: true } g ? g.LastTickMs : 0;

    // ══════════════════════ 제어(비핫패스) ══════════════════════
    public void Start()
    {
        if (_running) return;
        _running = true;
        if (_gateway is not null) Guard.LiveKbps = _gateway.Kbps;
        Guard.Start();
        StartPacker();
        _detect = new Thread(DetectLoop) { IsBackground = true, Name = "PF.LiveToReels.Detect" };
        _detect.Start();
        LastAction = "▶ 클라이맥스 감지 · 패키징 · 배포 워커 가동"; Raise();
    }

    public void Stop()
    {
        _running = false;
        var d = _detect; _detect = null;
        if (d is not null && d != Thread.CurrentThread) d.Join(1000);
        StopPacker();
        Guard.Stop();
        LastAction = "⏸ 정지"; Raise();
    }

    private void StartPacker()
    {
        if (_packing) return;
        _packing = true;
        _pack = new Thread(PackLoop) { IsBackground = true, Name = "PF.LiveToReels.Pack" };
        try { _pack.Priority = ThreadPriority.Lowest; } catch (PlatformNotSupportedException) { }
        _pack.Start();
    }

    private void StopPacker()
    {
        _packing = false; _packEvt.Set();
        var p = _pack; _pack = null;
        if (p is not null && p != Thread.CurrentThread) p.Join(3000);
    }

    public void SetThreshold(float v) { Detector.Threshold = Math.Clamp(v, 50f, 99f); Raise(); }
    public void SetAutoPublish(bool on) { AutoPublish = on; LastAction = on ? "🚀 패키지 즉시 3 플랫폼 배포" : "🚀 수동 배포"; Raise(); }
    public void SetSimTraffic(bool on) { SimTraffic = on; LastAction = on ? "🧪 평상시 채팅 시뮬레이션 ON" : "🧪 시뮬레이션 OFF"; Raise(); }
    public void SetCapacity(double kbps) { Guard.CapacityKbps = kbps; Raise(); }
    public void SetLinkOmni(bool on) { if (on && _omni is null) return; LinkOmni = on; LastAction = on ? "🔗 v17.51 실 API 핸드오프(배압 여유 시)" : "🔗 핸드오프 해제"; Raise(); }

    /// <summary>복합 스파이크 주입(채팅 폭주 + 피격 + 구매 · 약 3 s).</summary>
    public void InjectSpike(float seconds = 3f) { Interlocked.Exchange(ref _simLeft, (int)(seconds * Hz)); LastAction = "🔥 복합 스파이크 주입(채팅 · 피격 · 구매)"; Raise(); }

    public void SetLinkDubbing(bool on)
    {
        if (on && _dubbing is null) return;
        if (on && !LinkDubbing)
        {
            _dubRing ??= new[] { new short[DubCap], new short[DubCap], new short[DubCap] };
            for (var i = 0; i < ReelsPackageSpec.MaxDubs; i++) Spec.Dubs[i] ??= new ReelsDubTrack(DubCap);
            _prevSink = _dubbing!.Router.Sink;
            _dubbing.Router.Sink = this;
        }
        else if (!on && LinkDubbing && _dubbing is not null && ReferenceEquals(_dubbing.Router.Sink, this)) _dubbing.Router.Sink = _prevSink;
        LinkDubbing = on;
        LastAction = on ? "🌍 v17.64 다국어 더빙 레인 캡처(30 s)" : "🌍 더빙 캡처 해제"; Raise();
    }

    /// <summary>수동 하이라이트(현재 시점 기준 seconds 구간).</summary>
    public bool ManualClip(double seconds = 20)
    {
        var s = (long)(Math.Clamp(seconds, 15, 30) * 1000);
        var r = new ClimaxClipRequest { Id = 0, StartMs = _clock - s, EndMs = _clock, OnsetMs = _clock - s, PeakMs = _clock, TriggerMs = _clock, Peak = Detector.Index, Cause = ClimaxCause.Chat };
        return Dispatch(in r);
    }

    /// <summary>클리퍼 미가동 시 합성 fMP4 소스(1080×1920 · 60 fps · 무음 AAC) — 데모/회귀.</summary>
    public void UseSyntheticSource(double seconds = 20)
    {
        var s = SyntheticClip(seconds);
        var old = Interlocked.Exchange(ref _synthetic, s); old?.Dispose();
        _syntheticSec = seconds;
        LastAction = "🧪 합성 fMP4 소스 " + seconds.ToString("0", CultureInfo.InvariantCulture) + " s · " + (s.Length / 1024).ToString("N0", CultureInfo.InvariantCulture) + " KB"; Raise();
    }

    public bool PublishReel(int id)
    {
        var r = FindReel(id);
        if (r?.Media is not { } m || r.Phase is FactoryReelPhase.Publishing or FactoryReelPhase.Published) return false;
        var job = Guard.Enqueue(r.Id, m);
        if (job is null) { LastAction = "⚠ 업로드 슬롯 포화"; Raise(); return false; }
        r.Job = job; r.Phase = FactoryReelPhase.Publishing;
        if (!Guard.IsRunning && _running) Guard.Start();
        LastAction = "🚀 릴 #" + id.ToString(CultureInfo.InvariantCulture) + " → IG Reels · Shorts · TikTok"; Raise();
        return true;
    }

    public string? SaveReel(int id, string? path = null)
    {
        var r = FindReel(id);
        if (r?.Media is not { } m) return null;
        try
        {
            path ??= Path.Combine(Path.GetTempPath(), "PromptFlowReel_" + id.ToString(CultureInfo.InvariantCulture) + ".mp4");
            using var fs = File.Create(path);
            var buf = new byte[81920]; long pos = 0; int k;
            while ((k = m.ReadAt(pos, buf)) > 0) { fs.Write(buf, 0, k); pos += k; }
            LastAction = "💾 " + path; Raise();
            return path;
        }
        catch (Exception ex) { PromptFlowStudio.Program.Log("LiveToReels.Save", ex); LastAction = "⚠ 저장 실패: " + ex.Message; Raise(); return null; }
    }

    public void RemoveReel(int id)
    {
        lock (_reels)
            for (var i = 0; i < _reels.Count; i++)
                if (_reels[i].Id == id && _reels[i].Phase != FactoryReelPhase.Publishing) { _reels[i].Dispose(); _reels.RemoveAt(i); break; }
        Raise();
    }

    // ══════════════════════ 감지(60 Hz · 무할당) ══════════════════════
    private void DetectLoop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew(); var next = 0.0;
        while (_running)
        {
            try { Tick((long)sw.Elapsed.TotalMilliseconds); }
            catch (Exception ex) { PromptFlowStudio.Program.Log("LiveToReels.Detect", ex); }
            next += 1000.0 / Hz;
            var wait = next - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait); else if (wait < -100) next = sw.Elapsed.TotalMilliseconds;
        }
    }

    /// <summary>감지 틱(카운터 델타 → 엔진 · 무할당).</summary>
    public void Tick(long nowMs)
    {
        _clock = nowMs; Ticks++;
        long c = 0;
        if (_gateway is not null) Delta(_gateway.Chat.Accepted, ref _lastChat, out c);
        if (c > 0) Detector.AddChat((int)c);
        if (_showrunner is not null)
        {
            Delta(_showrunner.Spawner.Hits, ref _lastHits, out var h); Delta(_showrunner.Spawner.HeadHits, ref _lastHead, out var hh);
            if (h > 0) Detector.AddHit((int)h, (int)hh);
        }
        if (_commerce is not null)
        {
            Delta(_commerce.Sales.UnitsSold, ref _lastUnits, out var u);
            if (u > 0) Detector.AddPurchase((int)u);
            var so = _commerce.Sales.SoldOut;
            if (so && !_lastSoldOut) Detector.AddSoldOut();
            _lastSoldOut = so;
        }
        if (SimTraffic && Ticks % 20 == 0) Detector.AddChat(1);
        var sim = _simLeft;
        if (sim > 0)
        {
            Interlocked.Decrement(ref _simLeft);
            Detector.AddChat(3);
            if (sim % 8 == 0) Detector.AddHit(1, sim % 24 == 0 ? 1 : 0);
            if (sim % 6 == 0) Detector.AddPurchase(1);
            if (sim == 1) Detector.AddSoldOut();
        }
        Detector.Step(nowMs);
        // MCR 프레임 예산 감시(업로드 진행 중)
        if (_mcr is { IsRunning: true } m && Guard.ActiveLegs > 0)
        {
            FramesSampled++;
            var ms = m.LastTickMs;
            if (ms > MaxFrameMs) MaxFrameMs = ms;
            if (ms > 1000.0 / 60) FramesOverBudget++;
        }
    }

    private static void Delta(long cur, ref long last, out long d)
    {
        d = last < 0 || cur < last ? 0 : cur - last;
        last = cur;
    }

    // ══════════════════════ IClimaxClipDispatcher(감지 스레드 · 무할당) ══════════════════════
    public bool Dispatch(in ClimaxClipRequest request)
    {
        lock (_reqGate)
        {
            if (_reqN >= ReqRing) return false;
            _req[(_reqHead + _reqN) % ReqRing] = request; _reqN++;
        }
        if (!_packing) StartPacker();
        _packEvt.Set();
        return true;
    }

    // ══════════════════════ 패키징 워커(Lowest) ══════════════════════
    private void PackLoop()
    {
        while (_packing)
        {
            _packEvt.WaitOne(250);
            while (_packing && TryTake(out var r))
            {
                try { ProcessRequest(in r); }
                catch (Exception ex) { PromptFlowStudio.Program.Log("LiveToReels.Pack", ex); }
            }
        }
    }

    private bool TryTake(out ClimaxClipRequest r)
    {
        lock (_reqGate)
        {
            if (_reqN == 0) { r = default; return false; }
            r = _req[_reqHead]; _reqHead = (_reqHead + 1) % ReqRing; _reqN--; return true;
        }
    }

    /// <summary>클립 → 패키지 → (자동) 배포 — 패키징 워커 또는 동기 회귀 호출.</summary>
    public FactoryReel ProcessRequest(in ClimaxClipRequest req)
    {
        var reel = new FactoryReel { Id = Interlocked.Increment(ref _reelId), Request = req, CreatedUtc = DateTime.UtcNow, Phase = FactoryReelPhase.Clipping };
        lock (_reels)
        {
            if (_reels.Count >= MaxReels)
                for (var i = _reels.Count - 1; i >= 0; i--)
                    if (_reels[i].Phase != FactoryReelPhase.Publishing) { _reels[i].Dispose(); _reels.RemoveAt(i); break; }
            _reels.Insert(0, reel);
        }
        Raise();
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        SegmentedChunkStream? src = null;
        if (_gateway is not null && _gateway.Ring.HasConfig && _gateway.Ring.BufferedSec >= 3)
            src = _gateway.ClipNow(req.Seconds, auto: true)?.Media;
        if (src is null && _synthetic is { } syn) { src = syn; ClipFallbacks++; }
        if (src is null) { UseSyntheticSource(Math.Clamp(req.Seconds, 15, 30)); src = _synthetic; ClipFallbacks++; }
        reel.ClipMs = Ms(t0);
        reel.Phase = FactoryReelPhase.Packaging; Raise();

        reel.Caption = CaptionFor(in req);
        var output = new SegmentedChunkStream();
        bool ok;
        lock (Packager)
        {
            Spec.Caption = reel.Caption;
            Spec.Bpm = _beat is null ? 128 : _beat.Tracker.Locked ? _beat.Tracker.Bpm : _beat.Bgm.Bpm;
            FillDubs(req.Seconds);
            ok = src is not null && Packager.Package(src, Spec, output);
            reel.Tracks = Packager.LastTracks; reel.DubTracks = Packager.LastDubTracks; reel.Cues = Packager.LastCueCount;
            reel.CaptionSafe = Packager.LastCaptionSafe; reel.DurationSec = Packager.LastDurationSec; reel.PackMs = Packager.LastMs;
            if (!ok) reel.Failure = Packager.LastFailure ?? "소스 없음";
        }
        if (!ok) { output.Dispose(); reel.Phase = FactoryReelPhase.Failed; PackFailures++; LastAction = "⚠ 패키징 실패: " + reel.Failure; Raise(); return reel; }
        output.Position = 0;
        reel.Media = output; reel.Phase = FactoryReelPhase.Ready; Packaged++;
        LastAction = "🎬 릴 #" + reel.Id.ToString(CultureInfo.InvariantCulture) + " · " + reel.DurationSec.ToString("0.0", CultureInfo.InvariantCulture) + " s · " + reel.Tracks.ToString(CultureInfo.InvariantCulture)
            + " 트랙 · 패키징 " + reel.PackMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms";
        if (AutoPublish) PublishReel(reel.Id);
        if (LinkOmni && _omni is not null && Guard.CanHandoff) HandOff(reel);
        Raise();
        return reel;
    }

    private void HandOff(FactoryReel reel)
    {
        if (reel.Media is not { } m) return;
        var copy = new SegmentedChunkStream();
        var buf = new byte[81920]; long pos = 0; int k;
        while ((k = m.ReadAt(pos, buf)) > 0) { copy.Write(buf.AsSpan(0, k)); pos += k; }
        copy.Position = 0;
        if (_omni!.PublishMaster(copy)) { reel.HandedOff = true; HandOffs++; }
        else copy.Dispose();
    }

    private static double Ms(long t0) => (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    private string CaptionFor(in ClimaxClipRequest r)
    {
        var product = _commerce?.Sales.Product?.Name;
        return r.Cause switch
        {
            ClimaxCause.Physics => "방금 그 장면 실화냐 직격 리액션 레전드",
            ClimaxCause.Commerce => product is null ? "주문 폭주 품절 대란 실시간 현장" : product + " 품절 대란 실시간 현장",
            _ => "채팅창 폭발 이 순간 놓치면 후회",
        };
    }

    private void FillDubs(double seconds)
    {
        Spec.DubCount = 0;
        if (!LinkDubbing || _dubRing is null) return;
        var want = (int)Math.Min(DubCap, seconds * 48000);
        for (var i = 0; i < _dubLanes; i++)
        {
            var t = Spec.Dubs[i]!;
            var fill = Math.Min(_dubFill[i], want);
            var start = (_dubHead[i] - fill + DubCap) % DubCap;
            var first = Math.Min(fill, DubCap - start);
            _dubRing[i].AsSpan(start, first).CopyTo(t.Pcm);
            _dubRing[i].AsSpan(0, fill - first).CopyTo(t.Pcm.AsSpan(first));
            t.Length = fill; t.Language = _dubLang[i]; t.Enabled = fill > 0;
            Spec.DubCount = i + 1;
        }
    }

    // ══════════════════════ IDubAudioSink(v17.64 라우터 스레드 · 무할당) ══════════════════════
    public void Write(ChatPlatform platform, DubLanguage language, ReadOnlySpan<float> pcm, long samplePos)
    {
        try { _prevSink?.Write(platform, language, pcm, samplePos); } catch (Exception ex) { PromptFlowStudio.Program.Log("LiveToReels.DubChain", ex); }
        var ring = _dubRing;
        if (ring is null || language == DubLanguage.Ko) return;
        var lane = -1;
        for (var i = 0; i < _dubLanes; i++) if (_dubLang[i] == language) { lane = i; break; }
        if (lane < 0)
        {
            if (_dubLanes >= ReelsPackageSpec.MaxDubs) return;
            lane = _dubLanes; _dubLang[lane] = language; _dubLastPos[lane] = -1; _dubLanes++;
        }
        if (samplePos <= _dubLastPos[lane]) return;   // 같은 언어 다중 플랫폼 중복 제거
        _dubLastPos[lane] = samplePos;
        var r = ring[lane]; var h = _dubHead[lane];
        for (var i = 0; i < pcm.Length; i++) { r[h] = (short)(Math.Clamp(pcm[i], -1f, 1f) * 32767f); if (++h == DubCap) h = 0; }
        _dubHead[lane] = h; _dubFill[lane] = Math.Min(DubCap, _dubFill[lane] + pcm.Length);
    }

    // ══════════════════════ 배포 완료(업로드 워커) ══════════════════════
    private void OnJobFinished(ReelsUploadJob job)
    {
        var r = FindReel(job.ReelId);
        if (r is null) return;
        var done = 0;
        foreach (ref readonly var l in job.Legs.AsSpan()) if (l.Phase == ReelsUploadPhase.Done) done++;
        r.Phase = done > 0 ? FactoryReelPhase.Published : FactoryReelPhase.Failed;
        if (done == 0) r.Failure = "3 플랫폼 업로드 실패";
        else Published++;
        LastAction = "✅ 릴 #" + job.ReelId.ToString(CultureInfo.InvariantCulture) + " 배포 " + done.ToString(CultureInfo.InvariantCulture) + "/3";
        Raise();
    }

    // ══════════════════════ 합성 fMP4 소스 ══════════════════════
    public static SegmentedChunkStream SyntheticClip(double seconds, int width = 1080, int height = 1920, int fps = 60)
    {
        var dst = new SegmentedChunkStream();
        var mux = new FragmentedMp4Muxer(dst, width, height, fps, 2 * 1024 * 1024);
        var sps = new byte[] { 0x67, 0x42, 0xC0, 0x28, 0xDA, 0x01, 0x0E, 0x00, 0x7A, 0x10 };
        var pps = new byte[] { 0x68, 0xCE, 0x3C, 0x80 };
        var au = new byte[64 * 1024]; var aac = new byte[64];
        var aacLen = FragmentedMp4Muxer.WriteSilentAacFrame(aac);
        var frames = (long)(seconds * fps); long af = 0;
        for (long f = 0; f < frames; f++)
        {
            var key = f % fps == 0; var n = 0;
            void Nal(ReadOnlySpan<byte> s) { au[n++] = 0; au[n++] = 0; au[n++] = 0; au[n++] = 1; s.CopyTo(au.AsSpan(n)); n += s.Length; }
            if (key) { Nal(sps); Nal(pps); }
            au[n++] = 0; au[n++] = 0; au[n++] = 1; au[n++] = key ? (byte)0x65 : (byte)0x41;
            var payload = key ? 24_000 : 4_000;
            for (var i = 0; i < payload; i++) au[n++] = (byte)(1 + (f * 31 + i * 7) % 254);
            mux.AddVideo(au.AsSpan(0, n), key);
            while (af * FragmentedMp4Muxer.AacFrame * fps < (f + 1) * FragmentedMp4Muxer.AudioRate) { mux.AddAudio(aac.AsSpan(0, aacLen)); af++; }
        }
        mux.Finish();
        dst.Position = 0;
        return dst;
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("LiveToReels.OnChange", ex); }
    }

    public void Dispose()
    {
        Stop();
        if (LinkDubbing) SetLinkDubbing(false);
        Guard.OnJobFinished -= OnJobFinished;
        Guard.Dispose();
        _packEvt.Dispose();
        lock (_reels) { foreach (var r in _reels) r.Dispose(); _reels.Clear(); }
        Interlocked.Exchange(ref _synthetic, null)?.Dispose();
        OnChange = null;
    }

    public string Summary() =>
        "지수 " + Detector.Index.ToString("0", CultureInfo.InvariantCulture) + " · 트리거 " + Detector.Triggers.ToString(CultureInfo.InvariantCulture) + " · 릴 " + Packaged.ToString(CultureInfo.InvariantCulture)
        + " · 배포 " + Published.ToString(CultureInfo.InvariantCulture) + " · 업로드 " + Guard.RateKbps.ToString("N0", CultureInfo.InvariantCulture) + " kbps" + (Guard.Throttling ? " (스로틀링)" : "");
}
