using System.Buffers.Text;
using System.Diagnostics;
using System.Globalization;
using PromptFlowStudio.Services.LiveShowroom;
using PromptFlowStudio.Services.ViralVoiceFx;

namespace PromptFlowStudio.Services.LiveRestream;

/// <summary>
/// [PromptFlow Studio v17.83.0] 📡 옴니채널 라이브 RTMPS 콕핏 러너.
///   ▸ 송출 틱(<see cref="Tick"/> 무할당): 합성 H.264 Annex-B(AUD · 티어별 SPS/PPS · IDR/P · GOP 2 s · ABR 키프레임 요청 시 강제 IDR) + AAC(ADTS) →
///     <see cref="ZeroAllocMultiTargetRtmpStreamerEngine"/> 제로-카피 분기 → YouTube/Twitch 루프백 인제스트(업링크 배출 · RTT) →
///     <see cref="AdaptiveBitrateNetworkCongestionGuard"/> 100 ms 샘플 → 다음 프레임 비트레이트.
///   ▸ 채팅: YouTube 폴링 JSON · Twitch IRC 프레임 시뮬레이션 → <see cref="OmniLiveChatSuperChatAggregatorEngine"/> → 슈퍼챗 → v17.80 감정 + v17.68 제스처.
///   ▸ 실 송출: <see cref="TlsRtmpTransport"/>(스트림 키는 환경 변수 PFS_RTMPS_YT_KEY / PFS_RTMPS_TWITCH_KEY) — 기본은 루프백 리허설.
/// </summary>
public sealed class OmniLiveRestreamStudioRunner : IDisposable, ILiveChatReactionSink
{
    public const int TargetCount = 2, Fps = 30, Gop = Fps * 2, AudioRate = 48_000, AacSamples = 1024, AudioKbps = 160, LiveHz = 60, Hist = 60;
    public const long FrameUs = 1_000_000 / Fps, WindowUs = 500_000, ChatEveryUs = 700_000;
    public const float MinUplinkMbps = 1f, MaxUplinkMbps = 40f, MaxExtraRttMs = 400f;
    public static readonly string[] TargetNames = { "YouTube Live", "Twitch" };
    private static readonly double[] BaseRtt = { 28, 41 };

    private readonly object _gate = new();
    private readonly LoopbackRtmpServerTransport[] _srv = new LoopbackRtmpServerTransport[TargetCount];
    private readonly byte[] _au = new byte[ZeroAllocMultiTargetRtmpStreamerEngine.MaxAu];
    private readonly byte[] _aac = new byte[1024];
    private readonly byte[] _sim = new byte[2048];
    private readonly long[] _winBytes = new long[TargetCount], _winFrames = new long[TargetCount];
    private readonly float[] _kbpsNow = new float[TargetCount];
    private readonly float[] _histKbps = new float[Hist], _histFps = new float[Hist];
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _vAcc, _aAcc, _chatAcc, _winStart, _poseLeftUs, _videoIdx, _audioIdx, _simN;
    private int _gopPos, _histHead, _histN, _simPos;
    private uint _seed = 0xA5A5F00Du;
    private float _uplink = 20f, _extraRtt;

    public OmniLiveRestreamStudioRunner(IServiceProvider sp) : this() { }

    public OmniLiveRestreamStudioRunner()
    {
        _srv[0] = new LoopbackRtmpServerTransport("a.rtmps.youtube.com", seed: 0x11u);
        _srv[1] = new LoopbackRtmpServerTransport("live.twitch.tv", seed: 0x22u);
        Engine.AddTarget(new RtmpTarget(TargetNames[0], RtmpPlatform.YouTube, "live2", "rtmps://a.rtmps.youtube.com:443/live2", "yt-rehearsal-key", _srv[0]));
        Engine.AddTarget(new RtmpTarget(TargetNames[1], RtmpPlatform.Twitch, "app", "rtmps://live.twitch.tv:443/app", "live_rehearsal_key", _srv[1]));
    }

    public event Action? OnChange;
    public ZeroAllocMultiTargetRtmpStreamerEngine Engine { get; } = new();
    public AdaptiveBitrateNetworkCongestionGuard Abr { get; } = new();
    public OmniLiveChatSuperChatAggregatorEngine Chat { get; } = new();
    public RealtimeVoiceEmotionShifterEngine Emotion { get; } = new();
    public LoopbackRtmpServerTransport Server(int i) => _srv[i];
    public bool Live { get; private set; }
    public bool Running => _loop is { IsCompleted: false };
    public string? LastAction { get; private set; }
    public long NowUs { get; private set; }
    public long Ticks { get; private set; }
    public long VideoFramesOut { get; private set; }
    public long AudioFramesOut { get; private set; }
    public long ForcedIdr { get; private set; }
    public float TotalKbps { get; private set; }
    public float Fps1s { get; private set; }
    public float TargetKbps(int i) => _kbpsNow[i];
    public HoldPose Pose { get; private set; }
    public float PoseLeftSec => _poseLeftUs / 1e6f;
    public long LastReactionSeq { get; private set; } = -1;
    public long PinnedSeq { get; private set; } = -1;
    public int HistCount => _histN;
    public float HistKbps(int i) => _histKbps[(_histHead - _histN + i + Hist) % Hist];
    public float HistFps(int i) => _histFps[(_histHead - _histN + i + Hist) % Hist];

    public float UplinkMbps
    {
        get => _uplink;
        set => _uplink = float.IsFinite(value) ? Math.Clamp(value, MinUplinkMbps, MaxUplinkMbps) : 20f;
    }

    public float ExtraRttMs
    {
        get => _extraRtt;
        set => _extraRtt = float.IsFinite(value) ? Math.Clamp(value, 0f, MaxExtraRttMs) : 0f;
    }

    // ══════════════════════ 제어(비핫패스) ══════════════════════

    /// <summary>활성 타겟 전부 세션 수립(핸드셰이크 → publish). 반환 = 송출 중 타겟 수.</summary>
    public int GoLive()
    {
        int ok;
        lock (_gate)
        {
            for (var i = 0; i < TargetCount; i++)
            {
                var t = Engine.Target(i);
                if (!t.Enabled || t.State == RtmpTargetState.Publishing) continue;
                _srv[i].Reopen();
                Engine.Connect(i, Abr.Kbps, AudioKbps);
            }
            ok = Engine.PublishingCount;
            Live = ok > 0;
            _winStart = NowUs;
        }
        LastAction = Live ? "📡 ON AIR — " + ok.ToString(CultureInfo.InvariantCulture) + "/" + TargetCount.ToString(CultureInfo.InvariantCulture) + " 플랫폼 RTMPS 퍼블리시" : "⚠️ 연결 실패 — " + (Engine.Target(0).LastError ?? Engine.Target(1).LastError ?? "활성 타겟 없음");
        Raise();
        return ok;
    }

    public void SetTargetEnabled(int i, bool on)
    {
        if ((uint)i >= TargetCount) return;
        lock (_gate)
        {
            var t = Engine.Target(i);
            t.Enabled = on;
            if (!on && t.State == RtmpTargetState.Publishing) Engine.Disconnect(i);
            else if (on && Live && t.State != RtmpTargetState.Publishing) { _srv[i].Reopen(); Engine.Connect(i, Abr.Kbps, AudioKbps); }
        }
        LastAction = (on ? "🔌 " : "⏏ ") + TargetNames[i] + (on ? " 송출 합류(다음 IDR 게이트)" : " 송출 분리");
        Raise();
    }

    public bool AbrEnabled
    {
        get => Abr.Enabled;
        set { Abr.Enabled = value; LastAction = value ? "🛡️ ABR 세이프티 가드 ON" : "⚠️ ABR 가드 OFF(고정 비트레이트)"; Raise(); }
    }

    /// <summary>슈퍼챗 주입(1 = ₩5,000 · 2 = $20 · 3 = ¥10,000).</summary>
    public void InjectSuperChat(int tier)
    {
        lock (_gate)
        {
            var micros = tier >= 3 ? 10_000_000_000L : tier == 2 ? 20_000_000L : 5_000_000_000L;
            var cur = tier >= 3 ? "JPY"u8 : tier == 2 ? "USD"u8 : "KRW"u8;
            var n = BuildYouTube(ChatKind.SuperChat, "후원천사"u8, tier >= 3 ? "역대급 방송 응원합니다!! 🎉"u8 : "오늘도 화이팅!"u8, micros, cur);
            Chat.IngestYouTubePoll(_sim.AsSpan(0, n));
            Chat.Drain(this);
        }
        LastAction = "💸 YouTube 슈퍼챗 주입 · 티어 " + tier.ToString(CultureInfo.InvariantCulture);
        Raise();
    }

    /// <summary>Twitch 비트 1,000(= $10) 치어 주입.</summary>
    public void InjectCheer()
    {
        lock (_gate)
        {
            var n = BuildTwitch("cheer_master"u8, "Cheer1000 레전드 송출!"u8, 1000, false);
            Chat.IngestTwitchIrc(_sim.AsSpan(0, n));
            Chat.Drain(this);
        }
        LastAction = "💜 Twitch Cheer 1000 주입";
        Raise();
    }

    public void Start()
    {
        if (Running) return;
        if (!Live) GoLive();
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
                    var dt = last == 0 ? 1_000_000L / LiveHz : Math.Clamp(now - last, 1_000L, 100_000L);
                    last = now;
                    Tick(dt);
                    if (now - lastRaise >= 250_000) { lastRaise = now; Raise(); }
                    await Task.Delay(1000 / LiveHz, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { PromptFlowStudio.Program.Log("LiveRestream.Loop", ex); }
        }, ct);
        LastAction = "▶ 라이브 송출 루프 시작";
        Raise();
    }

    /// <summary>정상 종료 — FCUnpublish/deleteStream 후 루프 정지.</summary>
    public void Stop()
    {
        StopLoop();
        lock (_gate)
        {
            for (var i = 0; i < TargetCount; i++) if (Engine.Target(i).State == RtmpTargetState.Publishing) Engine.Disconnect(i);
            Live = false;
        }
        LastAction = "⏹ 송출 종료(언퍼블리시)";
        Raise();
    }

    /// <summary>긴급 송출 중단 — 모든 전송 즉시 절단.</summary>
    public void Panic()
    {
        int cut;
        lock (_gate) { cut = Engine.Panic(); Live = false; }
        StopLoop();
        LastAction = "🛑 긴급 송출 중단 — " + cut.ToString(CultureInfo.InvariantCulture) + " 플랫폼 즉시 절단";
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

    /// <summary>수동 1 프레임(33.3 ms) 전진 — 미송출이면 먼저 연결.</summary>
    public void Step()
    {
        if (!Live) GoLive();
        Tick(FrameUs);
        Raise();
    }

    // ══════════════════════ 송출 틱(무할당 핫패스) ══════════════════════

    public void Tick(long dtUs)
    {
        lock (_gate)
        {
            if (!Live) return;
            NowUs += dtUs;
            Ticks++;
            _vAcc += dtUs * Fps;
            while (_vAcc >= 1_000_000) { _vAcc -= 1_000_000; EmitVideo(); }
            _aAcc += dtUs * AudioRate;
            while (_aAcc >= AacSamples * 1_000_000L) { _aAcc -= AacSamples * 1_000_000L; EmitAudio(); }

            var active = 0;
            for (var i = 0; i < TargetCount; i++) if (Engine.Target(i).State == RtmpTargetState.Publishing) active++;
            var per = (int)(_uplink * 1000f / Math.Max(1, active));
            long maxPending = 0; double maxRtt = 0;
            for (var i = 0; i < TargetCount; i++)
            {
                var s = _srv[i];
                s.UplinkKbps = per; s.BaseRttMs = BaseRtt[i] + _extraRtt;
                s.Advance(dtUs);
                if (Engine.Target(i).State != RtmpTargetState.Publishing) continue;
                if (s.PendingBytes > maxPending) maxPending = s.PendingBytes;
                if (s.RttMs > maxRtt) maxRtt = s.RttMs;
            }
            Engine.Service();
            Abr.Sample(maxPending, maxRtt, NowUs);

            _chatAcc += dtUs;
            if (_chatAcc >= ChatEveryUs) { _chatAcc -= ChatEveryUs; SimChat(); }
            Chat.Drain(this);

            if (_poseLeftUs > 0 && (_poseLeftUs -= dtUs) <= 0) { _poseLeftUs = 0; Pose = HoldPose.Rest; Emotion.SetEmotion(VoiceEmotion.Neutral); }
            Window();
        }
    }

    private void Window()
    {
        if (NowUs - _winStart < WindowUs) return;
        var sec = (NowUs - _winStart) / 1e6f;
        float total = 0, fps = 0;
        for (var i = 0; i < TargetCount; i++)
        {
            var t = Engine.Target(i);
            _kbpsNow[i] = (t.BytesSent - _winBytes[i]) * 8f / 1000f / sec;
            var f = (t.VideoFrames - _winFrames[i]) / sec;
            if (f > fps) fps = f;
            total += _kbpsNow[i];
            _winBytes[i] = t.BytesSent; _winFrames[i] = t.VideoFrames;
        }
        TotalKbps = total; Fps1s = fps;
        _histKbps[_histHead] = total; _histFps[_histHead] = fps;
        _histHead = (_histHead + 1) % Hist; if (_histN < Hist) _histN++;
        _winStart = NowUs;
    }

    private void EmitVideo()
    {
        var forced = Abr.KeyframeRequested;
        var idr = _gopPos == 0 || forced;
        if (forced && _gopPos != 0) ForcedIdr++;
        if (idr) _gopPos = 0;
        Abr.OnFrame(idr, NowUs);
        var tier = Abr.Tier;
        var avg = Abr.Kbps * 1000 / 8 / Fps;
        var size = idr ? Math.Min(avg * 3, _au.Length - 64) : Math.Max(64, (avg * Gop - avg * 3) / (Gop - 1));
        var a = _au.AsSpan();
        var p = 0;
        p = Nal(a, p, 0x09); a[p++] = 0xF0;
        if (idr)
        {
            p = Nal(a, p, 0x67);
            a[p++] = 0x64; a[p++] = 0x00; a[p++] = 0x28; a[p++] = 0xAC; a[p++] = (byte)(0xD0 + tier); a[p++] = 0x40; a[p++] = 0x78; a[p++] = 0x02; a[p++] = 0x27; a[p++] = 0xE5;
            p = Nal(a, p, 0x68);
            a[p++] = 0xEE; a[p++] = 0x3C; a[p++] = 0x80;
        }
        p = Nal(a, p, idr ? (byte)0x65 : (byte)0x41);
        var end = Math.Min(a.Length, p + size);
        for (; p < end; p++) { var b = RtmpWire.Next(ref _seed); a[p] = b == 0 ? (byte)0x55 : b; }
        Engine.WriteVideo(a.Slice(0, p), (uint)(_videoIdx * 1000 / Fps));
        _videoIdx++; VideoFramesOut++;
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
        a[0] = 0xFF; a[1] = 0xF1; a[2] = 0x4C; a[3] = 0x80; a[4] = (byte)((raw + 7) >> 3); a[5] = 0x1F; a[6] = 0xFC; // ADTS(보호 없음)
        for (var i = 7; i < 7 + raw; i++) { var b = RtmpWire.Next(ref _seed); a[i] = b == 0 ? (byte)0x21 : b; }
        Engine.WriteAudio(a.Slice(0, 7 + raw), (uint)(_audioIdx * AacSamples * 1000 / AudioRate));
        _audioIdx++; AudioFramesOut++;
    }

    // ══════════════════════ 리액션 결선(v17.80 감정 · v17.68 제스처) ══════════════════════

    public void OnReaction(in ChatEvent e, VoiceEmotion emotion, HoldPose pose, float seconds)
    {
        Emotion.SetEmotion(emotion);
        Pose = pose;
        _poseLeftUs = (long)(seconds * 1e6f);
        LastReactionSeq = e.Seq;
        if (e.Importance >= 3) PinnedSeq = e.Seq;
    }

    // ══════════════════════ 채팅 시뮬레이션(실 와이어 포맷 · 무할당) ══════════════════════

    private void SimChat()
    {
        var k = (int)(_simN % 10);
        int n;
        switch (k)
        {
            case 0: n = BuildYouTube(ChatKind.Chat, "민지"u8, "송출 화질 미쳤다 ㅋㅋ"u8, 0, "USD"u8); Chat.IngestYouTubePoll(_sim.AsSpan(0, n)); break;
            case 1: n = BuildTwitch("pixel_fox"u8, "PogChamp 버퍼링 하나도 없네"u8, 0, false); Chat.IngestTwitchIrc(_sim.AsSpan(0, n)); break;
            case 2: n = BuildYouTube(ChatKind.Chat, "Daniel"u8, "Hello from Toronto!"u8, 0, "USD"u8); Chat.IngestYouTubePoll(_sim.AsSpan(0, n)); break;
            case 3: n = BuildTwitch("neon_owl"u8, "아바타 표정 자연스럽다"u8, 0, false); Chat.IngestTwitchIrc(_sim.AsSpan(0, n)); break;
            case 4: n = BuildTwitch("bit_rain"u8, "Cheer100 응원!"u8, 100, false); Chat.IngestTwitchIrc(_sim.AsSpan(0, n)); break;
            case 5: n = BuildYouTube(ChatKind.Chat, "하늘"u8, "동시 송출 신기하다"u8, 0, "USD"u8); Chat.IngestYouTubePoll(_sim.AsSpan(0, n)); break;
            case 6: n = BuildTwitch("glitch_zero"u8, "6메가 유지되나요?"u8, 0, false); Chat.IngestTwitchIrc(_sim.AsSpan(0, n)); break;
            case 7: n = BuildYouTube(ChatKind.SuperChat, "후원요정"u8, "커피 한 잔!"u8, 3_000_000_000L, "KRW"u8); Chat.IngestYouTubePoll(_sim.AsSpan(0, n)); break;
            case 8: n = BuildTwitch("loyal_viewer"u8, "3개월째 구독 중!"u8, 0, true); Chat.IngestTwitchIrc(_sim.AsSpan(0, n)); break;
            default: n = PingFrame(); Chat.IngestTwitchIrc(_sim.AsSpan(0, n)); break;
        }
    }

    private void Put(ReadOnlySpan<byte> s) { s.CopyTo(_sim.AsSpan(_simPos)); _simPos += s.Length; }
    private void PutNum(long v) { Utf8Formatter.TryFormat(v, _sim.AsSpan(_simPos), out var w); _simPos += w; }

    private int BuildYouTube(ChatKind kind, ReadOnlySpan<byte> author, ReadOnlySpan<byte> text, long micros, ReadOnlySpan<byte> cur)
    {
        var id = ++_simN;
        _simPos = 0;
        Put("{\"kind\":\"youtube#liveChatMessageListResponse\",\"pollingIntervalMillis\":2000,\"nextPageToken\":\"pt"u8); PutNum(id);
        Put("\",\"items\":[{\"id\":\"yt"u8); PutNum(id);
        if (kind == ChatKind.SuperChat)
        {
            Put("\",\"snippet\":{\"type\":\"superChatEvent\",\"displayMessage\":\"\\uD83D\\uDCB8 "u8); Put(text);
            Put("\",\"superChatDetails\":{\"amountMicros\":\""u8); PutNum(micros); Put("\",\"currency\":\""u8); Put(cur);
            Put("\",\"userComment\":\""u8); Put(text); Put("\",\"tier\":3}}"u8);
        }
        else
        {
            Put("\",\"snippet\":{\"type\":\"textMessageEvent\",\"displayMessage\":\""u8); Put(text);
            Put("\",\"textMessageDetails\":{\"messageText\":\""u8); Put(text); Put("\"}}"u8);
        }
        Put(",\"authorDetails\":{\"displayName\":\""u8); Put(author); Put("\",\"isChatSponsor\":false}}]}"u8);
        return _simPos;
    }

    private int BuildTwitch(ReadOnlySpan<byte> nick, ReadOnlySpan<byte> text, long bits, bool sub)
    {
        var id = ++_simN;
        _simPos = 0;
        if (sub)
        {
            Put("@badge-info=subscriber/3;display-name="u8); Put(nick); Put(";id=tw"u8); PutNum(id);
            Put(";msg-id=resub;msg-param-cumulative-months=3 :tmi.twitch.tv USERNOTICE #promptflow :"u8); Put(text); Put("\r\n"u8);
            return _simPos;
        }
        Put("@badge-info=;badges=;"u8);
        if (bits > 0) { Put("bits="u8); PutNum(bits); Put(";"u8); }
        Put("color=#9146FF;display-name="u8); Put(nick); Put(";id=tw"u8); PutNum(id);
        Put(" :"u8); Put(nick); Put("!"u8); Put(nick); Put("@"u8); Put(nick); Put(".tmi.twitch.tv PRIVMSG #promptflow :"u8); Put(text); Put("\r\n"u8);
        return _simPos;
    }

    private int PingFrame()
    {
        _simN++;
        _simPos = 0;
        Put("PING :tmi.twitch.tv\r\n"u8);
        return _simPos;
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("LiveRestream.OnChange", ex); }
    }

    public string Summary() =>
        !Live && Engine.Target(0).Connects == 0 ? "송출 대기" :
        "송출 " + TotalKbps.ToString("0", CultureInfo.InvariantCulture) + " kbps · " + AdaptiveBitrateNetworkCongestionGuard.TierName(Abr.Tier) + " · 정체 " +
        Abr.CongestionIndex.ToString("0.00", CultureInfo.InvariantCulture) + " · 전환 " + Abr.Switches.ToString(CultureInfo.InvariantCulture) + " · 채팅 " +
        Chat.Total.ToString(CultureInfo.InvariantCulture) + " · 슈퍼챗 ₩" + Chat.TotalKrw.ToString("#,0", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        StopLoop();
        lock (_gate) { if (Live) Engine.Panic(); Live = false; }
        OnChange = null;
    }
}
