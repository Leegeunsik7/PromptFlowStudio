using PromptFlowStudio.Services.Diagnostics;
namespace PromptFlowStudio.Services.ViralReels;

public enum ReelsSlotState : byte { Empty, Scheduled, DryRunPublished, Blocked }

/// <summary>발행 스케줄러 큐 슬롯(값 형식).</summary>
public struct ReelsPublishSlot
{
    public int ReelId;
    public ReelDubLang Lang;
    public long DueTick;
    public ReelsSlotState State;
    public ReelsPublishVerdict Verdict;
    public float Ssim;
}

/// <summary>
/// [PromptFlow Studio v17.47.0] 📱 AI 릴스 바이럴 리믹스 스튜디오 러너(60 Hz 전용 스레드).
/// 매 프레임 피드 배치 인제스천(패스 완료 시 1위 릴스 자동 선택) → 3초 창 분해 → 리믹스 합성 → 오리지널리티 가드 → 발행 큐 판정.
/// 3초 창이 끝나면 긴장 아크/비트 맵을 확정하고 다음 창을 시작한다. 발행은 드라이런 — 실 Graph API 발행은
/// <see cref="PromptFlowStudio.Services.SocialMedia.InstagramReelsService"/> 수동 승인 경로로만 수행한다.
/// </summary>
public sealed class ViralReelsStudioRunner : IDisposable
{
    public const int Hz = 60, Hist = 300, QueueCap = 8, W = RealtimeViralReelsIngestionEngine.W, H = RealtimeViralReelsIngestionEngine.H, Px = W * H;
    public const int BmpBytes = 54 + Px * 3;
    private readonly object _gate = new();
    private readonly float[] _ssimS = new float[Hist], _berS = new float[Hist], _flowS = new float[Hist], _rmsS = new float[Hist], _msS = new float[Hist];
    private readonly ReelsPublishSlot[] _queue = new ReelsPublishSlot[QueueCap];
    private readonly string[] _linked;
    private int _head, _series, _frame, _qHead;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;

    public ViralReelsStudioRunner(IServiceProvider sp) : this()
    {
        var names = new List<string>(6);
        if (sp.GetService(typeof(NarrativeTimeline.NarrativeTimelineStudioRunner)) is not null) names.Add("v17.40 타임라인");
        if (sp.GetService(typeof(LiveDubbing.MultilingualDubbingLipSyncStudioRunner)) is not null) names.Add("v17.32 더빙");
        if (sp.GetService(typeof(SetExtension.VirtualSetExtensionStudioRunner)) is not null) names.Add("v17.46 세트 확장");
        if (sp.GetService(typeof(CinematicDirector.CinematicDirectorStudioRunner)) is not null) names.Add("v17.39 멀티캠");
        if (sp.GetService(typeof(LensOptics.CinematicLensOpticsStudioRunner)) is not null) names.Add("v17.43 렌즈");
        if (sp.GetService(typeof(ProceduralFoley.ProceduralFoleyStudioRunner)) is not null) names.Add("v17.38 폴리");
        _linked = names.ToArray();
    }
    public ViralReelsStudioRunner()
    {
        _linked = Array.Empty<string>();
        while (!Ingest.IngestBatch()) { }
        SelectReel(Ingest.Top(0).Id);
    }

    public event Action? OnChange;
    public RealtimeViralReelsIngestionEngine Ingest { get; } = new();
    public AutonomousReelsStudioRemixSynthesizer Synth { get; } = new();
    public ReelsOriginalityAntiShadowbanGuard Guard { get; } = new();
    public IReadOnlyList<string> LinkedStudios => _linked;
    public bool PinReel { get; private set; }
    public int Frame => _frame;
    public int LastHookFrame { get; private set; } = -1;
    public int LastBeatCount { get; private set; }
    public long Windows { get; private set; }
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public double MaxTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public int SeriesCount => _series;
    public float SsimSeries(int i) => _ssimS[Ix(i)];
    public float BerSeries(int i) => _berS[Ix(i)];
    public float FlowSeries(int i) => _flowS[Ix(i)];
    public float RmsSeries(int i) => _rmsS[Ix(i)];
    public float TickMsSeries(int i) => _msS[Ix(i)];
    private int Ix(int i) => (_head - 1 - i + 2 * Hist) % Hist;
    /// <summary>i = 0 이 최신 예약.</summary>
    public ReelsPublishSlot QueueSlot(int i) { lock (_gate) return _queue[(_qHead - 1 - i + 2 * QueueCap) % QueueCap]; }
    public int Published { get; private set; }
    public int Blocked { get; private set; }

    // ══════════════════════ 제어 ══════════════════════
    public void SelectReel(int id)
    {
        lock (_gate)
        {
            Ingest.BeginReel(id); Synth.Reset(); Guard.Reset();
            _frame = 0; LastHookFrame = -1;
        }
        LastAction = "🎯 릴스 #" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 분해 시작";
        Raise();
    }
    public void SetPin(bool on) { lock (_gate) PinReel = on; LastAction = on ? "📌 릴스 고정" : "📌 랭킹 1위 자동 추적"; Raise(); }
    public void SetLanguage(ReelDubLang l) { lock (_gate) Synth.Language = l; LastAction = "🗣 더빙 " + AutonomousReelsStudioRemixSynthesizer.LangName(l); Raise(); }
    public void SetLevel(int lv) { lock (_gate) Synth.TransformLevel = Math.Clamp(lv, 0, AutonomousReelsStudioRemixSynthesizer.MaxLevel); LastAction = "🎛 변환 " + AutonomousReelsStudioRemixSynthesizer.LevelName(Synth.TransformLevel); Raise(); }
    public void SetSourceAudio(float mix) { lock (_gate) Synth.SourceAudioMix = Math.Clamp(mix, 0f, 1f); Raise(); }
    public void SetFlare(bool on) { lock (_gate) Synth.FlareEnabled = on; Raise(); }
    public void SetAutoEscalate(bool on) { lock (_gate) Guard.AutoEscalate = on; LastAction = on ? "🛡 유사도 자동 상향 ON" : "🛡 자동 상향 OFF"; Raise(); }
    public void SetAudioProtect(bool on) { lock (_gate) Guard.AutoProtectAudio = on; LastAction = on ? "🎧 원본 오디오 차단 ON" : "🎧 원본 오디오 차단 OFF"; Raise(); }
    public void SetCredit(bool on) { lock (_gate) Guard.CreditAttached = on; LastAction = on ? "✍ 원작 크레딧 표기" : "✍ 크레딧 제거"; Raise(); }

    /// <summary>발행 예약(드라이런) — delaySec 후 가드 판정으로 확정.</summary>
    public void Schedule(double delaySec)
    {
        lock (_gate)
        {
            ref var s = ref _queue[_qHead];
            s.ReelId = Ingest.ReelId; s.Lang = Synth.Language; s.DueTick = _tick + (long)(Math.Max(delaySec, 0) * Hz); s.State = ReelsSlotState.Scheduled; s.Verdict = ReelsPublishVerdict.Warming; s.Ssim = 0f;
            _qHead = (_qHead + 1) % QueueCap;
        }
        LastAction = "🗓 발행 예약 +" + delaySec.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " s";
        Raise();
    }

    /// <summary>AI 캡션/해시태그(UI 전용 · 할당 허용) — 훅 타임코드 · 카테고리 · 언어 · 원작 크레딧.</summary>
    public string BuildCaption()
    {
        int id, cat; ReelDubLang lang; float hookMs; bool credit;
        lock (_gate)
        {
            id = Ingest.ReelId; lang = Synth.Language; hookMs = Synth.RemixHookMs; credit = Guard.CreditAttached;
            cat = 0;
            for (var i = 0; i < Ingest.TopCount; i++) if (Ingest.Top(i).Id == id) { cat = Ingest.Top(i).Category; break; }
        }
        var head = lang switch
        {
            ReelDubLang.English => "Wait for it… 🎬 Cinematic remix in 3 seconds",
            ReelDubLang.Spanish => "Espera… 🎬 Remix cinematográfico en 3 segundos",
            _ => "最後まで見て… 🎬 3秒でシネマティック・リミックス",
        };
        string[] tags = { "#dance", "#food", "#travel", "#fitness", "#comedy" };
        var langTag = lang switch { ReelDubLang.English => "#reelsUS", ReelDubLang.Spanish => "#reelsES", _ => "#リール" };
        var sb = new System.Text.StringBuilder(256);
        sb.Append(head).Append(" (hook ").Append(hookMs.ToString("0", System.Globalization.CultureInfo.InvariantCulture)).Append(" ms)");
        if (credit) sb.Append("\n🎥 Original: @creator_").Append(id.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" · remixed with PromptFlow Studio");
        sb.Append("\n#reels #remix #cinematic ").Append(tags[Math.Clamp(cat, 0, tags.Length - 1)]).Append(' ').Append(langTag);
        return sb.ToString();
    }

    /// <summary>UI 뷰어 — 24-bit BMP(하단부터 BGR). remix = false 면 리믹스가 유래한 원본 프레임(그레이).</summary>
    public void WriteBmp(Span<byte> dst, bool remix)
    {
        dst[..54].Clear();
        dst[0] = (byte)'B'; dst[1] = (byte)'M';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst[2..], BmpBytes);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst[10..], 54);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst[14..], 40);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst[18..], W);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst[22..], H);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(dst[26..], 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(dst[28..], 24);
        lock (_gate)
        {
            var r = Synth.OutR; var g = Synth.OutG; var b = Synth.OutB; var src = Synth.SourceLuma;
            for (var y = 0; y < H; y++)
            {
                var d = 54 + y * W * 3;
                var s = (H - 1 - y) * W;
                for (var x = 0; x < W; x++)
                {
                    var i = s + x;
                    if (remix) { dst[d + x * 3] = To8(b[i]); dst[d + x * 3 + 1] = To8(g[i]); dst[d + x * 3 + 2] = To8(r[i]); }
                    else { var l = To8(src[i]); dst[d + x * 3] = l; dst[d + x * 3 + 1] = l; dst[d + x * 3 + 2] = l; }
                }
            }
        }
    }

    private static byte To8(float v) => (byte)(Math.Clamp(v, 0f, 1f) * 255f + 0.5f);

    // ══════════════════════ 프레임(무할당) ══════════════════════
    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("ViralReels.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _tick);
            if (Ingest.IngestBatch() && !PinReel && Ingest.TopCount > 0 && Ingest.Top(0).Id != Ingest.ReelId)
            {
                Ingest.BeginReel(Ingest.Top(0).Id); Synth.Reset(); Guard.Reset(); _frame = 0;
            }
            Ingest.Deconstruct(_frame);
            Synth.Compose(Ingest, _frame);
            Guard.Evaluate(_frame, Ingest.Audio, Synth);
            for (var q = 0; q < QueueCap; q++)
            {
                ref var s = ref _queue[q];
                if (s.State != ReelsSlotState.Scheduled || s.DueTick > _tick || Guard.Verdict == ReelsPublishVerdict.Warming) continue;
                s.Verdict = Guard.Verdict; s.Ssim = Guard.SsimAvg;
                if (Guard.Verdict == ReelsPublishVerdict.Ready) { s.State = ReelsSlotState.DryRunPublished; Published++; }
                else { s.State = ReelsSlotState.Blocked; Blocked++; }
            }
            if (++_frame >= RealtimeViralReelsIngestionEngine.WindowFrames)
            {
                LastHookFrame = Ingest.HookFrame; LastBeatCount = Ingest.BeatCount;
                Synth.PrepareArc(Ingest);
                Ingest.ResetWindow();
                _frame = 0;
                Windows++;
            }
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            _ssimS[_head] = Guard.SsimAvg; _berS[_head] = Guard.BerAvg; _flowS[_head] = Ingest.Flow; _rmsS[_head] = Ingest.Rms; _msS[_head] = ms;
            _head = (_head + 1) % Hist;
            if (_series < Hist) _series++;
            LastTickMs = ms;
            if (ms > MaxTickMs && _tick > 10) MaxTickMs = ms;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.ViralReels" };
            _loop.Start();
        }
        LastAction = "▶ 릴스 리믹스 60 Hz";
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("ViralReels.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }
            if (n % 6 == 0) Raise();
        }
    }
    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("ViralReels.Runner.OnChange", ex); }
    }
    public void Dispose() => Stop();
}
