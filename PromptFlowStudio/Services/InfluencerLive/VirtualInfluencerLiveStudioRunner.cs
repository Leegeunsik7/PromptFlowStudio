using System.Globalization;
using PromptFlowStudio.Services.AvatarActing;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.LiveDubbing;
using PromptFlowStudio.Services.Localization.Dubbing;
using PromptFlowStudio.Services.OmniChannel;
using PromptFlowStudio.Services.SplatEnvironment;
using PromptFlowStudio.Services.VirtualLive;
namespace PromptFlowStudio.Services.InfluencerLive;

/// <summary>
/// [PromptFlow Studio v17.61.0] 🎙️ 버추얼 인플루언서 라이브 스튜디오 러너(60 Hz 방송 루프 + 클립 합성 전용 스레드).
/// 프레임 = 데모 시청자 와이어(NDJSON · IRCv3 · 좋아요 · 슈퍼챗 · 레이드/봇/비속어 혼입) → 채팅 엔진 Ingest(세이프티 가드 · 의도 분류 · 리액션 큐)
/// → Dispatch(v17.60 연기 디렉터 SayNow 선점) → v17.60/57/58/55 아바타 렌더 → 음성/환호 오디오 → 하이라이트 합성기(캡처 · 계측 · 스파이크).
/// 클립 스레드가 9:16 fMP4 를 합성하고 자동 배포 시 v17.51 옴니채널 러너로 바이너리를 직결한다. 연산 구간 무할당.
/// </summary>
public sealed class VirtualInfluencerLiveStudioRunner : IDisposable, IInfluencerReactionSink, PromptFlowStudio.Services.StreamGateway.IChatDispatchTarget
{
    public const int Hz = 60, Hist = 240, Users = 384, Bots = 64, WireCap = 64 * 1024, ReactLog = 16;
    public const int HypeMs = 9000, Spf = 48000 / Hz;

    private static readonly string[] Chatter =
    {
        "안녕하세요~", "오늘 방송 재밌네요", "처음 왔어요", "출근길에 보는 중", "배경 너무 이쁘다", "목소리 좋다", "오늘도 왔습니다", "first time here",
        "hello from LA", "음악 좋네요", "점심 먹으면서 봐요", "다들 반가워요",
    };
    private static readonly string[] Questions = { "이 노래 제목이 뭐예요?", "다음 방송은 언제예요?", "어떻게 그렇게 춤을 잘 춰요?", "what game is this?", "몇 살이에요?", "그 옷 어디서 샀어요?" };
    private static readonly string[] Praises = { "너무 귀여워요!!", "오늘 방송 최고예요", "사랑해요 💖", "목소리 천재", "love this stream", "so cute", "진짜 멋있다" };
    private static readonly string[] Humors = { "ㅋㅋㅋㅋㅋ", "아 개웃겨 ㅋㅋ", "빵터졌다 ㅋㅋㅋ", "lol", "ㅎㅎㅎ 귀엽다", "lmao" };
    private static readonly string[] HypeLines = { "와 미쳤다 ㅋㅋㅋㅋ", "레전드 각", "클립 따라!!", "이거 실화냐 ㅋㅋ", "OMG", "대박 대박", "ㅋㅋㅋㅋㅋㅋ 최고" };
    private static readonly string[] DonationMsgs = { "응원합니다!", "항상 고마워요", "춤 보여주세요!!", "생일 축하해요!", "최고의 방송 💖" };
    private static readonly int[] DonationAmounts = { 1000, 2000, 5000, 10000, 30000, 50000, 100000 };
    /// <summary>데모 유해 혼입 — 다국어 · 우회 표기(가드 100 % 차단 대상).</summary>
    public static readonly string[] ToxicSamples =
    {
        "ㅅ ㅂ 노잼", "씨~발 뭐야", "시1발 진짜", "f u c k this", "sh1t stream", "Ｆｕｃｋ", "b!tch", "병 신 같네", "개.새.끼", "死ね", "傻逼", "puta madre hijo de puta", "카지노 사이트 홍보", "free robux at http bit ly",
    };
    private const string RaidText = "RAID RAID 🌊 copypasta 🌊 RAID RAID";

    private readonly object _gate = new(), _clipGate = new();
    private readonly AvatarActingDirectorStudioRunner _acting;
    private readonly bool _ownsActing;
    private readonly OmniChannelEngagementStudioRunner? _omni;
    private readonly string[] _users = new string[Users], _bots = new string[Bots];
    private readonly byte[] _wire = new byte[WireCap];
    private readonly char[] _esc = new char[512];
    private readonly float[] _mono = new float[Spf];
    private readonly float[] _msS = new float[Hist], _rmsS = new float[Hist], _qS = new float[Hist];
    private readonly ViewerReactionCue[] _reacted = new ViewerReactionCue[ReactLog];
    private int _wireLen, _sHead, _sN, _reactHead, _reactN, _toxicPending, _toxicNext, _floodPending;
    private uint _rng = 0x1761u;
    private double _chatAcc, _likeAcc, _phase;
    private long _hypeUntil = long.MinValue, _accepted, _likes, _lastHostMs;
    private bool _autoScriptSaved;
    private Thread? _loop, _clipLoop;
    private volatile bool _running;
    private long _tick;

    public VirtualInfluencerLiveStudioRunner(IServiceProvider sp)
        : this(sp.GetService(typeof(AvatarActingDirectorStudioRunner)) as AvatarActingDirectorStudioRunner, sp.GetService(typeof(OmniChannelEngagementStudioRunner)) as OmniChannelEngagementStudioRunner) { }

    public VirtualInfluencerLiveStudioRunner(AvatarActingDirectorStudioRunner? acting = null, OmniChannelEngagementStudioRunner? omni = null)
    {
        _ownsActing = acting is null;
        _acting = acting ?? new AvatarActingDirectorStudioRunner();
        _omni = omni;
        Guard = new ChatSpamToxicitySafetyGuard();
        Chat = new RealtimeViewerChatReactionTriggerEngine(Guard);
        Clipper = new LiveEngagementHighlightClippingSynthesizer();
        Clipper.OnClip += OnClipReady;
        for (var i = 0; i < Users; i++) _users[i] = "viewer" + i.ToString("000", CultureInfo.InvariantCulture);
        for (var i = 0; i < Bots; i++) _bots[i] = "raid_bot" + i.ToString("00", CultureInfo.InvariantCulture);
    }

    public event Action? OnChange;
    public event Action<LiveHighlightClip>? OnClip;
    public ChatSpamToxicitySafetyGuard Guard { get; }
    public RealtimeViewerChatReactionTriggerEngine Chat { get; }
    public LiveEngagementHighlightClippingSynthesizer Clipper { get; }
    public AvatarActingDirectorStudioRunner Acting => _acting;
    public bool OmniLinked => _omni is not null;
    public bool DemoChat { get; private set; } = true;
    public float ChatRate { get; private set; } = 4f;
    public float LikeRate { get; private set; } = 2f;
    public bool RaidMode { get; private set; }
    public bool AutoPublish { get; private set; }
    public int PublishMask { get; private set; } = 7;
    public bool HypeActive => NowMs < _hypeUntil;
    public long NowMs => Tick * 1000 / Hz;
    public float Rms { get; private set; }
    /// <summary>v17.63.1 — v17.32 소스-필터 음성 엔진(한글 G2P · 운율 · 포먼트) 호스트 레인 0 발화. OFF 면 v17.61 내장 2 배음 톤 폴백.</summary>
    public RealtimeNeuralVoiceCloningDubbingEngine Voice { get; } = new() { CloneEnabled = false };
    public bool VoicePipeline { get; set; } = true;
    public long VoiceLines { get; private set; }
    /// <summary>9:16 추적 크롭 중심 X(프리뷰 320 px 좌표).</summary>
    public float CropX { get; private set; } = RealtimeGaussianSplatRasterizerEngine.W * 0.5f;
    public long LikesTotal => _likes;
    public long Published { get; private set; }
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public double MaxTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public int SeriesCount => _sN;
    private int Idx(int i) => (_sHead - 1 - i + 2 * Hist) % Hist;
    public float FrameMsSeries(int i) => _msS[Idx(i)];
    public float RmsSeries(int i) => _rmsS[Idx(i)];
    public float QueueSeries(int i) => _qS[Idx(i)];
    public int ReactedCount => _reactN;
    public ref readonly ViewerReactionCue Reacted(int i) => ref _reacted[(_reactHead - 1 - i + 2 * ReactLog) % ReactLog];
    /// <summary>v17.62 실방송 채팅 게이트웨이 배압 큐 — 설정 시 매 틱 최대 48 건 · 1.5 ms 예산으로 소비.</summary>
    public PromptFlowStudio.Services.StreamGateway.SocketBackpressureDropoutGuard? ExternalIngress { get; set; }
    public long ExternalDelivered { get; private set; }

    /// <summary>v17.62 <see cref="PromptFlowStudio.Services.StreamGateway.IChatDispatchTarget"/> — 틱 잠금 내 호출(무할당).</summary>
    public bool Deliver(PromptFlowStudio.Services.StreamGateway.ChatPlatform platform, ReadOnlySpan<char> user, ReadOnlySpan<char> text, int amount, PromptFlowStudio.Services.StreamGateway.ChatRole role, long nowMs)
    {
        var a = Chat.Accepted;
        Chat.Accept(user, text, amount, NowMs);
        ExternalDelivered++;
        if (Chat.Accepted == a) return false;
        Clipper.OnChat();
        return true;
    }

    // ══════════════════════ IInfluencerReactionSink ══════════════════════
    public bool Busy => _acting.Gesture.Speaking || _acting.Gesture.Clock < _acting.Gesture.ScheduleEndS;

    public void React(in ViewerReactionCue cue, ReadOnlySpan<char> line)
    {
        var interrupt = cue.Intent == ViewerChatIntent.Donation || !Busy;
        _acting.SayNow(line, interrupt);
        Speak(line, interrupt);
        if (cue.Gesture == ReactionGesture.HeartHands) _acting.TriggerHeartPose();
        _reacted[_reactHead] = cue; _reactHead = (_reactHead + 1) % ReactLog; if (_reactN < ReactLog) _reactN++;
        if (cue.Intent is ViewerChatIntent.Donation or ViewerChatIntent.Humor) Clipper.SetCaption(line);
    }

    // ══════════════════════ 제어(비핫패스) ══════════════════════
    public void Prepare() { _acting.Prepare(); Clipper.Prepare(); }

    public void SetDemoChat(bool on) { lock (_gate) DemoChat = on; LastAction = on ? "💬 데모 시청자 채팅 ON" : "💬 데모 채팅 OFF(수동 입력만)"; Raise(); }

    public void SetChatRate(float perSec) { lock (_gate) ChatRate = Math.Clamp(perSec, 0f, 60f); LastAction = "💬 평소 채팅 " + ChatRate.ToString("0.0", CultureInfo.InvariantCulture) + " msg/s"; Raise(); }

    public void SetGuard(bool on) { lock (_gate) Guard.Enabled = on; LastAction = on ? "🛡 도배/비속어 가드 ON" : "⛔ 가드 OFF(비교)"; Raise(); }

    public void SetRaid(bool on) { lock (_gate) RaidMode = on; LastAction = on ? "🌊 스팸 레이드 공격 시뮬레이션 ON(봇 64 · 30 msg/s 동일 본문 + 단일 ID 플러드)" : "🌊 레이드 종료"; Raise(); }

    public void SetClipping(bool on) { lock (_gate) Clipper.Enabled = on; LastAction = on ? "✂ 자동 하이라이트 클리핑 ON" : "✂ 자동 클리핑 OFF"; Raise(); }

    public void SetAutoPublish(bool on)
    {
        if (on && _omni is null) return;
        lock (_gate) AutoPublish = on;
        LastAction = on ? "🚀 클립 생성 즉시 v17.51 옴니채널 자동 배포" : "🚀 수동 배포(원클릭)";
        Raise();
    }

    public void SetPublishMask(int mask) { lock (_gate) PublishMask = mask & 7; Raise(); }

    public bool ConfigureOutput(int width, int height, int kbps, bool allowMf, bool preferHw)
    {
        bool ok;
        lock (_clipGate) ok = Clipper.Configure(width, height, kbps, allowMf, preferHw);
        LastAction = ok ? "⚙ 클립 출력 " + width.ToString(CultureInfo.InvariantCulture) + "×" + height.ToString(CultureInfo.InvariantCulture) + " · " + kbps.ToString(CultureInfo.InvariantCulture) + " kbps" : "⚠ 합성 중 — 설정 변경 불가";
        Raise();
        return ok;
    }

    /// <summary>하이프 버스트(9 s) — 채팅 5× · 좋아요 15× · 환호 오디오 · 슈퍼챗 혼입.</summary>
    public void TriggerHype() { lock (_gate) _hypeUntil = NowMs + HypeMs; LastAction = "🔥 하이프 버스트 9 s"; Raise(); }

    public void InjectToxic(int count = 1) { lock (_gate) _toxicPending += Math.Max(1, count); LastAction = "🤬 유해 채팅 " + count.ToString(CultureInfo.InvariantCulture) + " 건 혼입"; Raise(); }

    public void InjectBotFlood() { lock (_gate) _floodPending += 30; LastAction = "🤖 단일 ID 봇 플러드 30 건"; Raise(); }

    public ChatSafetyVerdict SendChat(string user, string text, int amount = 0)
    {
        ChatSafetyVerdict v;
        lock (_gate)
        {
            var a = Chat.Accepted;
            v = Chat.Accept(string.IsNullOrWhiteSpace(user) ? "me" : user, text, Math.Max(0, amount), NowMs);
            if (Chat.Accepted > a) Clipper.OnChat();
        }
        LastAction = "✉ " + ChatSpamToxicitySafetyGuard.VerdictName(v) + " · " + text;
        Raise();
        return v;
    }

    public bool ClipNow()
    {
        bool ok;
        lock (_gate) ok = Clipper.ClipNow(NowMs, LiveEngagementHighlightClippingSynthesizer.MinClipSec);
        LastAction = ok ? "✂ 수동 클립 확정" : "⚠ 클립 불가: " + Clipper.LastEvent;
        Raise();
        return ok;
    }

    /// <summary>원클릭 배포 — 클립 복제본을 v17.51 옴니채널 배포 큐로(소유권 이전).</summary>
    public bool PublishClip(int id)
    {
        var clip = Clipper.FindClip(id);
        if (clip is null || _omni is null) { LastAction = _omni is null ? "⚠ v17.51 옴니채널 미연동" : "⚠ 클립 없음"; Raise(); return false; }
        var media = LiveEngagementHighlightClippingSynthesizer.CloneMedia(clip);
        if (media is null) return false;
        _omni.SetMask(PublishMask);
        var ok = _omni.PublishMaster(media);
        if (ok) { clip.Published = true; Published++; }
        LastAction = ok ? "🚀 클립 #" + id.ToString(CultureInfo.InvariantCulture) + " → Shorts/Reels/TikTok 배포 큐" : "⚠ 배포 실패: " + _omni.LastAction;
        Raise();
        return ok;
    }

    /// <summary>클립 MP4 저장(UI · 할당 허용).</summary>
    public string? SaveClip(int id, string? path = null)
    {
        var clip = Clipper.FindClip(id);
        if (clip?.Media is not { } m) return null;
        try
        {
            path ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "PromptFlow", "LiveClips", "highlight_" + clip.CreatedUtc.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + id.ToString(CultureInfo.InvariantCulture) + ".mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var fs = File.Create(path);
            var buf = new byte[81920]; long pos = 0; int k;
            while ((k = m.ReadAt(pos, buf)) > 0) { fs.Write(buf, 0, k); pos += k; }
            LastAction = "💾 " + path;
            Raise();
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            PromptFlowStudio.Program.Log("InfluencerLive.SaveClip", ex);
            LastAction = "⚠ 저장 실패: " + ex.Message;
            Raise();
            return null;
        }
    }

    public void RemoveClip(int id) { Clipper.RemoveClip(id); Raise(); }

    private void OnClipReady(LiveHighlightClip clip)
    {
        if (AutoPublish && _omni is not null) PublishClip(clip.Id);
        try { OnClip?.Invoke(clip); } catch (Exception ex) { PromptFlowStudio.Program.Log("InfluencerLive.OnClip", ex); }
        Raise();
    }

    // ══════════════════════ 프레임(무할당) ══════════════════════
    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("InfluencerLive.Step");
            Compute();
        }
    }

    private void Compute()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var now = Interlocked.Increment(ref _tick) * 1000 / Hz;
        var hype = now < _hypeUntil;
        var raid = Guard.RaidActive(now);
        // ① 시청자 와이어 합성 → 수신
        _wireLen = 0;
        if (DemoChat) Generate(now, hype);
        var a0 = Chat.Accepted; var l0 = Chat.Likes;
        if (_wireLen > 0) Chat.Ingest(_wire.AsSpan(0, _wireLen), now);
        _accepted = Chat.Accepted; _likes = Chat.Likes;
        if (_accepted > a0) Clipper.OnChat((int)(_accepted - a0));
        ExternalIngress?.Drain(this, PromptFlowStudio.Services.StreamGateway.GatewayClock.NowMs, 48, 1500);
        if (_likes > l0) Clipper.OnLikes((int)(_likes - l0));
        // ② 리액션 디스패치(≤ 100 ms) · 호스트 잡담
        Chat.Dispatch(now, this);
        if (!Busy && now - _lastHostMs > 9000 && Chat.QueueCount == 0) { _lastHostMs = now; var host = AvatarActingDirectorStudioRunner.DefaultLines[(int)(now / 9000 % AvatarActingDirectorStudioRunner.DefaultLines.Length)]; _acting.SayNow(host, false); Speak(host, false); }
        // ③ 아바타(v17.60 · 자체 루프 미가동 시 직접 구동)
        if (!_acting.IsRunning) _acting.Step();
        // ④ 오디오(음성 + 환호) → 계측 · 롤링 버퍼
        Synthesize(hype);
        Clipper.PushAudio(_mono);
        // ⑤ 프레임 캡처(30 fps · 피사체 추적 크롭)
        if (Clipper.CaptureDue(now) && _acting.Raster is { } ras)
        {
            var cx = (float)RealtimeGaussianSplatRasterizerEngine.W * 0.5f;
            if (_acting.Locomotion is { } mm && _acting.Project(mm.Position + new System.Numerics.Vector3(0f, 1.1f, 0f), out var px, out _)) cx = px;
            CropX = cx;
            Clipper.PushFrameRgb(ras.ColorR, ras.ColorG, ras.ColorB, ras.Transmittance, RealtimeGaussianSplatRasterizerEngine.Background, RealtimeGaussianSplatRasterizerEngine.W, RealtimeGaussianSplatRasterizerEngine.H, cx, now, raid);
        }
        // ⑥ 스파이크 감지
        Clipper.Tick(now, raid);
        var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        LastTickMs = ms; if (ms > MaxTickMs && _tick > 5) MaxTickMs = ms;
        _msS[_sHead] = (float)ms; _rmsS[_sHead] = Rms; _qS[_sHead] = Chat.QueueCount;
        _sHead = (_sHead + 1) % Hist; if (_sN < Hist) _sN++;
    }

    private uint Next() { _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5; return _rng; }
    private float Rand() => (Next() >> 8) * (1f / 16777216f);

    private void Generate(long now, bool hype)
    {
        var rate = ChatRate * (hype ? 5f : 1f);
        _chatAcc += rate / Hz;
        while (_chatAcc >= 1)
        {
            _chatAcc -= 1;
            var user = _users[Next() % Users];
            var r = Rand();
            if (hype)
            {
                if (r < 0.08f) Json(user, DonationMsgs[Next() % (uint)DonationMsgs.Length], DonationAmounts[Next() % (uint)DonationAmounts.Length], false);
                else Json(user, HypeLines[Next() % (uint)HypeLines.Length], 0, r < 0.3f);
                continue;
            }
            var pick = r < 0.015f ? DonationMsgs : r < 0.12f ? Questions : r < 0.24f ? Praises : r < 0.34f ? Humors : Chatter;
            Json(user, pick[Next() % (uint)pick.Length], pick == DonationMsgs ? DonationAmounts[Next() % 4] : 0, r > 0.85f);
        }
        _likeAcc += LikeRate * (hype ? 15f : 1f) / Hz;
        if (_likeAcc >= 1) { var n = (int)_likeAcc; _likeAcc -= n; Like(n); }
        if (RaidMode)
        {
            // 레이드: 타 ID 동일 본문 30 msg/s + 단일 ID 플러드 12 msg/s
            if (now % 100 < 1000 / Hz) for (var k = 0; k < 3; k++) Json(_bots[Next() % Bots], RaidText, 0, false);
            if (now % 83 < 1000 / Hz) Json(_bots[0], "follow me free gift", 0, true);
        }
        if (_toxicPending > 0) { _toxicPending--; Json(_users[Next() % Users], ToxicSamples[_toxicNext++ % ToxicSamples.Length], 0, false); }
        if (_floodPending > 0) { var k = Math.Min(_floodPending, 3); _floodPending -= k; for (var i = 0; i < k; i++) Json("spam_bot_77", "구독 이벤트 참여하세요 링크 클릭", 0, false); }
    }

    /// <summary>NDJSON(기본) 또는 Twitch IRCv3 줄을 와이어 버퍼에 무할당 기록.</summary>
    private void Json(string user, string text, int amount, bool irc)
    {
        var need = 96 + user.Length * 3 + text.Length * 6;
        if (_wireLen + need > WireCap) return;
        var w = _wire.AsSpan(_wireLen);
        var n = 0;
        if (irc)
        {
            if (amount > 0) { n += Ascii(w, "@bits="); amount.TryFormat(w[n..], out var d, default, CultureInfo.InvariantCulture); n += d; n += Ascii(w[n..], " "); }
            n += Ascii(w[n..], ":"); n += Utf(w[n..], user); n += Ascii(w[n..], "!"); n += Utf(w[n..], user); n += Ascii(w[n..], "@tmi PRIVMSG #pfstudio :"); n += Utf(w[n..], text);
        }
        else
        {
            n += Ascii(w, amount > 0 ? "{\"type\":\"superchat\",\"user\":\"" : "{\"type\":\"chat\",\"user\":\"");
            n += Utf(w[n..], user);
            n += Ascii(w[n..], "\",\"text\":\"");
            n += Escaped(w[n..], text);
            n += Ascii(w[n..], "\",\"amount\":");
            amount.TryFormat(w[n..], out var d, default, CultureInfo.InvariantCulture); n += d;
            n += Ascii(w[n..], "}");
        }
        w[n++] = (byte)'\n';
        _wireLen += n;
    }

    private void Like(int n)
    {
        if (_wireLen + 48 > WireCap) return;
        var w = _wire.AsSpan(_wireLen);
        var k = Ascii(w, "{\"type\":\"like\",\"count\":");
        n.TryFormat(w[k..], out var d, default, CultureInfo.InvariantCulture); k += d;
        k += Ascii(w[k..], "}\n");
        _wireLen += k;
    }

    private static int Ascii(Span<byte> dst, string s) { for (var i = 0; i < s.Length; i++) dst[i] = (byte)s[i]; return s.Length; }
    private static int Utf(Span<byte> dst, ReadOnlySpan<char> s) => System.Text.Encoding.UTF8.GetBytes(s, dst);

    private int Escaped(Span<byte> dst, string s)
    {
        var e = _esc.AsSpan(); var m = 0;
        foreach (var c in s)
        {
            if (m + 2 > e.Length) break;
            if (c is '"' or '\\') e[m++] = '\\';
            e[m++] = c < ' ' ? ' ' : c;
        }
        return Utf(dst, e[..m]);
    }

    /// <summary>대사 → v17.32 엔진 레인 0 적재(선점 시 레인 리셋 · 400 자 상한 · 무할당).</summary>
    private void Speak(ReadOnlySpan<char> line, bool interrupt)
    {
        if (!VoicePipeline || line.IsEmpty) return;
        if (interrupt) Voice.ResetLane(0);
        if (Voice.Enqueue(0, DubLanguage.Ko, line.Length > 400 ? line[..400] : line) > 0) VoiceLines++;
    }

    private void Synthesize(bool crowd)
    {
        if (VoicePipeline)
        {
            // v17.63.1: 합성음(사인 2 배음) → v17.32 음성 파이프라인 렌더 + 관객 환호 노이즈
            Voice.Render(0, _mono);
            var ch = crowd ? 0.32f : 0.018f;
            double acc = 0;
            for (var i = 0; i < _mono.Length; i++) { var v = _mono[i] + ch * (Rand() * 2f - 1f); _mono[i] = v; acc += v * v; }
            Rms = (float)Math.Sqrt(acc / _mono.Length);
            return;
        }
        var g = _acting.Gesture;
        ref readonly var f = ref g.Current;
        var speaking = g.Speaking;
        var f0 = 170f + 70f * f.Arousal;
        var amp = speaking ? 0.06f + 0.10f * MathF.Min(1f, f.Intensity) : 0f;
        var cheer = crowd ? 0.32f : 0.018f;
        double sq = 0;
        var dphi = 2 * Math.PI * f0 / 48000.0;
        for (var i = 0; i < _mono.Length; i++)
        {
            _phase += dphi; if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
            var v = amp * (float)(Math.Sin(_phase) + 0.45 * Math.Sin(2 * _phase)) + cheer * (Rand() * 2f - 1f);
            _mono[i] = v; sq += v * v;
        }
        Rms = (float)Math.Sqrt(sq / _mono.Length);
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _autoScriptSaved = _acting.AutoScript; _acting.AutoScript = false;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.InfluencerLive" };
            _clipLoop = new Thread(ClipLoop) { IsBackground = true, Name = "PF.InfluencerClip", Priority = ThreadPriority.BelowNormal };
            _loop.Start(); _clipLoop.Start();
        }
        LastAction = "🔴 ON AIR — 60 Hz 라이브 · 자동 하이라이트 클리핑";
        Raise();
    }

    public void Stop()
    {
        Thread? t, c;
        lock (_gate) { if (!_running) return; _running = false; t = _loop; c = _clipLoop; _loop = _clipLoop = null; _acting.AutoScript = _autoScriptSaved; }
        t?.Join(3000); c?.Join(5000);
        LastAction = "⏸ 방송 정지";
        Raise();
    }

    /// <summary>클립 합성 1 회(테스트 · 클립 스레드) — 최대 frames 프레임.</summary>
    public int ClipStep(int frames) { lock (_clipGate) return Clipper.EncodeStep(frames); }

    private void Loop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long n = 0;
        while (_running)
        {
            try { Step(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("InfluencerLive.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }
            if (n % 6 == 0) Raise();
        }
    }

    private void ClipLoop()
    {
        while (_running)
        {
            try { if (ClipStep(6) == 0) Thread.Sleep(20); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or IOException) { PromptFlowStudio.Program.Log("InfluencerLive.Clip", ex); lock (_clipGate) Clipper.Cancel(); }
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("InfluencerLive.OnChange", ex); }
    }

    public void Dispose()
    {
        Stop();
        Clipper.OnClip -= OnClipReady;
        lock (_clipGate) Clipper.Dispose();
        if (_ownsActing) _acting.Dispose();
    }
}
