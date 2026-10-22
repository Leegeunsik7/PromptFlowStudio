using System.Diagnostics;
using System.Globalization;
using PromptFlowStudio.Services.LiveRestream;
using PromptFlowStudio.Services.ViralVoiceFx;

namespace PromptFlowStudio.Services.LiveClipper;

/// <summary>
/// [PromptFlow Studio v17.84.0] ⚡ 실시간 라이브 킬러클립 오토 클리퍼 러너.
///   ▸ 라이브 틱(<see cref="Tick"/> 무할당): 리허설 H.264 Annex-B(AUD · SPS/PPS + IDR · GOP 2 s) + AAC(ADTS) → <see cref="RollingCircularVideoBufferEngine"/> 120 s 순환 보관 ·
///     채팅 유속 · 후원 · 48 kHz 군중 음향 PCM · 아바타 감정 → <see cref="MultimodalClimaxSpikeDetector"/> 1 s 채점 →
///     클립 확정(직후 15 s 녹화 완료) 즉시 링 슬라이스 → <see cref="InstantShortsPackagingPipelineGuard"/> 저우선순위 큐.
///   ▸ v17.83 연동(<see cref="Linked"/>): <see cref="OmniLiveRestreamStudioRunner.MediaTap"/> 로 실 송출 AU/AAC 를 링에 직결 · 통합 채팅 총량/슈퍼챗 ₩ · v17.80 감정 상태를 신호로 사용.
/// </summary>
public sealed class LiveToShortsAutoClipperStudioRunner : IDisposable, ILiveMediaTap
{
    public const int Fps = 30, Gop = Fps * 2, AudioRate = 48_000, AacSamples = 1024, VideoKbps = 900, AudioKbps = 96, LiveHz = 60;
    public const int RehearsalRingKbps = 1_200, LinkedRingKbps = 6_500, HypeSec = 5, AutoHypeSec = 50;
    public const long FrameUs = 1_000_000 / Fps, BudgetUs = 1_000_000 / LiveHz;
    public const float BaseChatPerSec = 3f, HypeChatPerSec = 18f;

    private readonly object _gate = new();
    private readonly OmniLiveRestreamStudioRunner? _live;
    private readonly byte[] _au = new byte[64 * 1024];
    private readonly byte[] _aac = new byte[1024];
    private readonly float[] _pcm = new float[AudioRate / 10];
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _nowUs, _vAcc, _aAcc, _hypeAtUs = -1, _nextAutoHypeUs = 12_000_000, _punchInUs = -1, _lastChat, _lastKrw;
    private int _gopPos;
    private uint _seed = 0xC11F5EEDu;
    private double _chatAcc, _phase;
    private bool _hypeDonated, _linked;

    public LiveToShortsAutoClipperStudioRunner(IServiceProvider sp) : this(sp.GetService(typeof(OmniLiveRestreamStudioRunner)) as OmniLiveRestreamStudioRunner) { }

    public LiveToShortsAutoClipperStudioRunner(OmniLiveRestreamStudioRunner? live = null)
    {
        _live = live;
        Ring = new RollingCircularVideoBufferEngine(RehearsalRingKbps);
        Packager = new InstantShortsPackagingPipelineGuard(Ring);
    }

    public event Action? OnChange;
    public RollingCircularVideoBufferEngine Ring { get; }
    public MultimodalClimaxSpikeDetector Detector { get; } = new();
    public InstantShortsPackagingPipelineGuard Packager { get; }
    public bool Running => _loop is { IsCompleted: false };
    public bool LinkAvailable => _live is not null;
    public bool AutoHype { get; set; } = true;
    public string? LastAction { get; private set; }
    public long NowUs => Volatile.Read(ref _nowUs);
    public long Ticks { get; private set; }
    public long VideoFrames { get; private set; }
    public long AudioFrames { get; private set; }
    public long LinkedFrames { get; private set; }
    public long ChatTotal { get; private set; }
    public long DonationKrw { get; private set; }
    public long Hypes { get; private set; }
    public long ClipsSliced { get; private set; }
    public long SliceMisses { get; private set; }
    public double LastTickUs { get; private set; }
    public double MaxTickUs { get; private set; }
    public long BudgetMisses { get; private set; }
    public float SubjectCx { get; private set; } = 0.5f;
    public bool PunchActive => _punchInUs >= 0;
    public float PunchSec => _punchInUs >= 0 ? (NowUs - _punchInUs) / 1e6f : 0f;
    public bool HypeActive => _hypeAtUs >= 0 && NowUs - _hypeAtUs < HypeSec * 1_000_000L;

    /// <summary>v17.83 송출 연동 — 링 6.5 Mbps 재구성 · 미디어 탭 결선(해제 시 리허설 1.2 Mbps 복귀).</summary>
    public bool Linked
    {
        get => _linked;
        set
        {
            if (_live is null || value == _linked) return;
            lock (_gate)
            {
                _linked = value;
                Ring.Configure(value ? LinkedRingKbps : RehearsalRingKbps);
                if (value) { _lastChat = _live.Chat.Total; _lastKrw = _live.Chat.TotalKrw; _live.MediaTap = this; }
                else if (ReferenceEquals(_live.MediaTap, this)) _live.MediaTap = null;
            }
            LastAction = value ? "🔗 v17.83 라이브 송출 연동 — 실 송출 AU/AAC 링 직결(6.5 Mbps × 120 s)" : "⛓️‍💥 연동 해제 — 리허설 소스(1.2 Mbps)";
            Raise();
        }
    }

    // ══════════════════════ 제어(비핫패스) ══════════════════════

    public void Start()
    {
        if (Running) return;
        Packager.StartWorker();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew(); long last = 0, lastRaise = 0;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var now = sw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
                    var dt = last == 0 ? BudgetUs : Math.Clamp(now - last, 1_000L, 100_000L);
                    last = now;
                    Tick(dt);
                    if (now - lastRaise >= 250_000) { lastRaise = now; Raise(); }
                    await Task.Delay(1000 / LiveHz, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { PromptFlowStudio.Program.Log("LiveClipper.Loop", ex); }
        }, ct);
        LastAction = "▶ 오토 클리퍼 가동 — 120 s 링 기록 · 1 s 스파이크 채점 · 백그라운드 패키징";
        Raise();
    }

    public void Stop()
    {
        StopLoop();
        Packager.StopWorker();
        LastAction = "⏸ 오토 클리퍼 정지";
        Raise();
    }

    private void StopLoop()
    {
        var cts = _cts; var loop = _loop;
        if (cts is null) return;
        cts.Cancel();
        try { loop?.Wait(1000); } catch (AggregateException) { }
        cts.Dispose(); _cts = null; _loop = null;
    }

    /// <summary>수동 전진(루프 정지 시) — 시뮬레이션 초 단위 + 대기 패키징 인라인 처리.</summary>
    public void Advance(float seconds)
    {
        var ticks = (int)Math.Clamp(seconds * LiveHz, 1, 3600 * LiveHz);
        for (var i = 0; i < ticks; i++) Tick(BudgetUs);
        if (!Packager.WorkerRunning) while (Packager.ProcessOne()) { }
        LastAction = "⏩ " + seconds.ToString("0.#", CultureInfo.InvariantCulture) + " s 전진";
        Raise();
    }

    /// <summary>🔥 하이라이트 상황 연출 — 5 s 채팅 6× · 환호/웃음 고역 · 놀람→흥분 · ₩30,000 후원.</summary>
    public void TriggerHype(bool donate = true)
    {
        lock (_gate) { StartHype(donate); }
        LastAction = "🔥 하이라이트 연출 — 채팅 폭주 · 환호 · 감정 급변" + (donate ? " · ₩30,000 후원" : null);
        Raise();
    }

    private void StartHype(bool donate)
    {
        _hypeAtUs = _nowUs; _hypeDonated = !donate; Hypes++;
        Detector.OnEmotion(AvatarEmotionTag.Surprise);
    }

    public void PunchIn()
    {
        lock (_gate) _punchInUs = _nowUs;
        LastAction = "🔴 Punch In — 수동 클립 기록 시작";
        Raise();
    }

    public void PunchOut()
    {
        long sec = 0;
        lock (_gate)
        {
            if (_punchInUs < 0) return;
            var start = Math.Min(_punchInUs, _nowUs - 3_000_000);   // 최소 3 s
            Detector.AddManual(start, _nowUs);
            sec = (_nowUs - start) / 1_000_000;
            _punchInUs = -1;
            DrainReady();
        }
        if (!Packager.WorkerRunning) while (Packager.ProcessOne()) { }
        LastAction = "⬛ Punch Out — 수동 클립 " + sec.ToString(CultureInfo.InvariantCulture) + " s 패키징 큐 투입";
        Raise();
    }

    /// <summary>⚡ 즉석 클립 — 직전 30 s.</summary>
    public void InstantClip()
    {
        lock (_gate)
        {
            Detector.AddManual(Math.Max(0, _nowUs - 30_000_000), _nowUs);
            DrainReady();
        }
        if (!Packager.WorkerRunning) while (Packager.ProcessOne()) { }
        LastAction = "⚡ 즉석 클립 — 직전 30 s 슬라이스";
        Raise();
    }

    public int DispatchAll()
    {
        var n = Packager.DispatchAll(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        LastAction = "🚀 원클릭 배포 — " + n.ToString(CultureInfo.InvariantCulture) + " 건 v17.79/v17.81 배포 큐 이관";
        Raise();
        return n;
    }

    public bool Dispatch(long id)
    {
        var ok = Packager.Dispatch(id, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        LastAction = ok ? "🚀 숏폼 #" + id.ToString(CultureInfo.InvariantCulture) + " 배포 큐 이관" : "⚠️ 이관 불가(상태/쿼터)";
        Raise();
        return ok;
    }

    public bool DetectorEnabled
    {
        get => Detector.Enabled;
        set { Detector.Enabled = value; LastAction = value ? "📈 자동 스파이크 감지 ON" : "✋ 자동 감지 OFF(수동 펀치 전용)"; Raise(); }
    }

    public void Reset()
    {
        lock (_gate)
        {
            Ring.Reset(); Detector.Reset(); Packager.Reset();
            _nowUs = _vAcc = _aAcc = 0; _gopPos = 0; _chatAcc = 0; _hypeAtUs = -1; _punchInUs = -1; _nextAutoHypeUs = 12_000_000;
            Ticks = VideoFrames = AudioFrames = LinkedFrames = ChatTotal = DonationKrw = Hypes = ClipsSliced = SliceMisses = BudgetMisses = 0;
            LastTickUs = MaxTickUs = 0;
            if (_linked && _live is not null) { _lastChat = _live.Chat.Total; _lastKrw = _live.Chat.TotalKrw; }
        }
        LastAction = "♻ 초기화";
        Raise();
    }

    // ══════════════════════ 라이브 틱(무할당 핫패스) ══════════════════════

    public void Tick(long dtUs)
    {
        var t0 = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            Volatile.Write(ref _nowUs, _nowUs + dtUs);
            Ticks++;
            if (!_linked)
            {
                _vAcc += dtUs * Fps;
                while (_vAcc >= 1_000_000) { _vAcc -= 1_000_000; EmitVideo(); }
                _aAcc += dtUs * AudioRate;
                while (_aAcc >= AacSamples * 1_000_000L) { _aAcc -= AacSamples * 1_000_000L; EmitAudio(); }
            }
            Signals(dtUs);
            Detector.Advance(_nowUs);
            DrainReady();
        }
        var us = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
        LastTickUs = us; if (us > MaxTickUs) MaxTickUs = us;
        if (us > BudgetUs) BudgetMisses++;
    }

    private void DrainReady()
    {
        while (Detector.TryDequeueReady(_nowUs, out var clip))
        {
            if (Ring.TrySlice(clip.StartUs, clip.EndUs, out var slice)) { ClipsSliced++; Packager.TryEnqueue(clip, slice, SubjectCx); }
            else SliceMisses++;
        }
    }

    private void Signals(long dtUs)
    {
        if (AutoHype && !_linked && _nowUs >= _nextAutoHypeUs) { _nextAutoHypeUs = _nowUs + AutoHypeSec * 1_000_000L; StartHype(true); }
        var since = _hypeAtUs >= 0 ? _nowUs - _hypeAtUs : long.MaxValue;
        var hype = since < HypeSec * 1_000_000L;
        if (_hypeAtUs >= 0 && !hype) { _hypeAtUs = -1; Detector.OnEmotion(AvatarEmotionTag.Neutral); }
        if (hype && since >= 1_500_000) Detector.OnEmotion(AvatarEmotionTag.Excited);
        if (hype && !_hypeDonated && since >= 1_000_000) { _hypeDonated = true; Detector.OnDonation(30_000); DonationKrw += 30_000; }

        // 피사체(아바타) 수평 위치 — 9:16 크롭 중심
        _phase += dtUs * 1e-6;
        SubjectCx = 0.5f + 0.18f * MathF.Sin((float)(_phase * 0.37));

        // 채팅 유속
        if (_linked && _live is not null)
        {
            var total = _live.Chat.Total; var krw = _live.Chat.TotalKrw;
            if (total > _lastChat) { Detector.OnChat((int)Math.Min(int.MaxValue, total - _lastChat)); ChatTotal += total - _lastChat; }
            if (krw > _lastKrw) { Detector.OnDonation(krw - _lastKrw); DonationKrw += krw - _lastKrw; }
            _lastChat = total; _lastKrw = krw;
            Detector.OnEmotion(_live.Emotion.Emotion switch
            {
                VoiceEmotion.Excited => AvatarEmotionTag.Excited,
                VoiceEmotion.Funny => AvatarEmotionTag.Happy,
                _ => hype ? AvatarEmotionTag.Excited : AvatarEmotionTag.Neutral,
            });
        }
        else
        {
            var rate = hype ? HypeChatPerSec : BaseChatPerSec * (0.8f + 0.4f * Rand01());
            _chatAcc += rate * dtUs * 1e-6;
            if (_chatAcc >= 1) { var k = (int)_chatAcc; _chatAcc -= k; Detector.OnChat(k); ChatTotal += k; }
        }

        // 48 kHz 군중 음향 — 평시 대화(180 Hz 성분 + 저잡음) · 하이라이트 환호/웃음(고역 잡음 + 진폭 상승)
        var n = (int)Math.Min(_pcm.Length, dtUs * AudioRate / 1_000_000);
        if (n <= 0) return;
        var amp = hype ? 0.45f : 0.05f;
        var hf = hype ? 0.6f : 0.1f;
        var t = _phase - n / (double)AudioRate;
        float prev = 0;
        for (var i = 0; i < n; i++)
        {
            var noise = Rand01() * 2f - 1f;
            var hi = noise - prev; prev = noise;   // 1 차 차분 = 고역 강조
            _pcm[i] = amp * ((1f - hf) * MathF.Sin((float)(2 * Math.PI * 180 * (t + i / (double)AudioRate))) * 0.7f + hf * hi * 0.5f + 0.1f * noise);
        }
        Detector.OnAudio(_pcm.AsSpan(0, n), AudioRate);
    }

    private float Rand01()
    {
        _seed ^= _seed << 13; _seed ^= _seed >> 17; _seed ^= _seed << 5;
        return (_seed & 0xFFFFFF) / 16777216f;
    }

    private void EmitVideo()
    {
        var idr = _gopPos == 0;
        var avg = VideoKbps * 1000 / 8 / Fps;
        var size = idr ? avg * 3 : Math.Max(64, (avg * Gop - avg * 3) / (Gop - 1));
        var a = _au.AsSpan();
        var p = Nal(a, 0, 0x09); a[p++] = 0xF0;
        if (idr)
        {
            p = Nal(a, p, 0x67);
            a[p++] = 0x64; a[p++] = 0x00; a[p++] = 0x28; a[p++] = 0xAC; a[p++] = 0xD9; a[p++] = 0x40; a[p++] = 0x78; a[p++] = 0x02; a[p++] = 0x27; a[p++] = 0xE5;
            p = Nal(a, p, 0x68);
            a[p++] = 0xEE; a[p++] = 0x3C; a[p++] = 0x80;
        }
        p = Nal(a, p, idr ? (byte)0x65 : (byte)0x41);
        var end = Math.Min(a.Length, p + size);
        for (; p < end; p++) { var b = RtmpWire.Next(ref _seed); a[p] = b == 0 ? (byte)0x55 : b; }
        Ring.WriteVideo(a.Slice(0, p), _nowUs);
        VideoFrames++;
        _gopPos = (_gopPos + 1) % Gop;
    }

    private static int Nal(Span<byte> a, int p, byte header)
    {
        a[p] = 0; a[p + 1] = 0; a[p + 2] = 0; a[p + 3] = 1; a[p + 4] = header;
        return p + 5;
    }

    private void EmitAudio()
    {
        var raw = AudioKbps * 1000 / 8 * AacSamples / AudioRate;
        var a = _aac.AsSpan();
        a[0] = 0xFF; a[1] = 0xF1; a[2] = 0x4C; a[3] = 0x80; a[4] = (byte)((raw + 7) >> 3); a[5] = 0x1F; a[6] = 0xFC;
        for (var i = 7; i < 7 + raw; i++) { var b = RtmpWire.Next(ref _seed); a[i] = b == 0 ? (byte)0x21 : b; }
        Ring.WriteAudio(a.Slice(0, 7 + raw), _nowUs);
        AudioFrames++;
    }

    // ══════════════════════ v17.83 미디어 탭(송출 스레드 · 무할당) ══════════════════════

    public void OnLiveVideo(ReadOnlySpan<byte> annexB, bool idr) { Ring.WriteVideo(annexB, NowUs); LinkedFrames++; }

    public void OnLiveAudio(ReadOnlySpan<byte> aac) => Ring.WriteAudio(aac, NowUs);

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("LiveClipper.OnChange", ex); }
    }

    public string Summary() =>
        Ticks == 0 ? "대기" :
        "링 " + (Ring.SpanUs / 1e6).ToString("0", CultureInfo.InvariantCulture) + " s · 스코어 " + Detector.Last.Score.ToString("0.00", CultureInfo.InvariantCulture) +
        " · 스파이크 " + Detector.Triggers.ToString(CultureInfo.InvariantCulture) + " · 패키징 " + Packager.Packaged.ToString(CultureInfo.InvariantCulture) +
        " · 배포 " + Packager.Dispatched.ToString(CultureInfo.InvariantCulture) + " · 간섭 " + Packager.HotPathInterference.ToString(CultureInfo.InvariantCulture);

    public void Dispose()
    {
        StopLoop();
        if (_live is not null && ReferenceEquals(_live.MediaTap, this)) _live.MediaTap = null;
        Packager.Dispose();
        OnChange = null;
    }
}
