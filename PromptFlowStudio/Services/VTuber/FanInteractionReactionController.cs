using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using PromptFlowStudio.Services.Media;
using PromptFlowStudio.Services.Music;
using PromptFlowStudio.Services.Sound;
using PromptFlowStudio.Services.Storyboard;

namespace PromptFlowStudio.Services.VTuber;

public enum ReactionTier { None, Thanks, Cheer, Hype, Legendary }

/// <summary>후원 이벤트 — 멱등 키(Id) · 팬 · 금액/통화 · 메시지 · 플랫폼.</summary>
public sealed record DonationEvent(Guid Id, string FanId, string FanName, double Amount, string Currency, string Message, string Platform = "youtube");

/// <summary>리액션 큐 — 등급 · VFX · 징글 · 감사 댄스 박 수 · 길이 · 배너.</summary>
public sealed record ReactionCue(Guid Id, string FanName, double AmountKrw, ReactionTier Tier, string Vfx, string Jingle, int DanceBeats, int DurationMs, string Banner, string Detail, long EnqueuedMs);

/// <summary>자율 방송 옵션 — LLM · TTS · 데모 채팅 · 공백 멘트 간격 · 답변 최소 간격 · Twitch 채널(읽기 전용 익명 IRC).</summary>
public sealed record SessionOptions(bool UseLlm = true, bool UseTts = true, bool DemoChat = false, int IdleSeconds = 25, int MinReplyGapMs = 2500, string? TwitchChannel = null);

/// <summary>
/// [PromptFlow Studio v16.38.0] 🎉 팬 후원 리액션 &amp; 안무 트리거 컨트롤러 + 1-Click 자율 방송 세션.
///   ▸ 후원 → 등급(₩1천/1만/5만/20만 · 통화 환산) + 키워드(춤/하트/생일) → VFX(스파클/폭죽/메가 폭죽/컨페티/하트) · 절차적 징글(차임/팡파르/레전더리/생일/러브 · BCL WAV) ·
///     v16.37.0 감사 안무(파워 히트 → 스타일 훅 → 프리즈) · 배너.
///   ▸ 큐: 등급 우선 + FIFO · 상한 64(하위 등급 축출) · Guid 멱등(상한 집합) · Tick(무할당 유휴 경로) → 브릿지 오버레이/안무/표정 즉시 송출.
///   ▸ 자율 방송: 채팅 수신함(상한 200 · 후원 우선) → RAG 답변(in-flight 1슬롯 · 최소 간격) → TTS(선택) → 입모양 발화 · 공백 시 세계관 멘트 · 데모 팬 채팅 ·
///     Twitch 익명 IRC(태그 · Bits → USD 0.01) 수신 · 실시간 지표(분당 메시지 · 리액션 · 드롭).
/// </summary>
public sealed class FanInteractionReactionController : IDisposable
{
    public const int QueueCap = 64, InboxCap = 200;
    public static readonly string[] Jingles = { "chime", "fanfare", "legendary", "birthday", "love" };

    private readonly VTuberPersonaMemoryEngine _memory;
    private readonly LiveVTuberBroadcastingBridge _bridge;
    private readonly AiVoiceoverTtsService? _tts;
    private readonly object _gate = new();
    private readonly List<ReactionCue> _queue = new(QueueCap + 1);
    private readonly HashSet<Guid> _seen = new(1025);
    private readonly ConcurrentQueue<ChatMessage> _inbox = new(), _priority = new();
    private readonly long[] _msgTimes = new long[256];
    private readonly List<VTuberReply> _recent = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private ReactionCue? _current;
    private long _currentEnd, _played, _dropped, _messages, _inboxCount;
    private int _msgPos, _inFlight;
    private CancellationTokenSource? _session;
    private SessionOptions _opts = new();
    private DateTime _startedUtc;

    public FanInteractionReactionController(IServiceProvider sp)
        : this((VTuberPersonaMemoryEngine)sp.GetService(typeof(VTuberPersonaMemoryEngine))!, (LiveVTuberBroadcastingBridge)sp.GetService(typeof(LiveVTuberBroadcastingBridge))!,
               sp.GetService(typeof(AiVoiceoverTtsService)) as AiVoiceoverTtsService) { }

    public FanInteractionReactionController(VTuberPersonaMemoryEngine memory, LiveVTuberBroadcastingBridge bridge, AiVoiceoverTtsService? tts = null)
    {
        _memory = memory;
        _bridge = bridge;
        _tts = tts;
    }

    public event Action? OnChange;

    public ReactionCue? Current => _current;
    public int QueueLength { get { lock (_gate) return _queue.Count; } }
    public long Played => Interlocked.Read(ref _played);
    public long Dropped => Interlocked.Read(ref _dropped);
    public long Messages => Interlocked.Read(ref _messages);
    public int InboxLength => (int)Interlocked.Read(ref _inboxCount);
    public string? SessionPersonaId { get; private set; }
    public bool InSession => _session is not null;
    public TimeSpan Uptime => InSession ? DateTime.UtcNow - _startedUtc : TimeSpan.Zero;
    public string? LastAction { get; private set; }
    public string? TwitchStatus { get; private set; }
    public IReadOnlyList<VTuberReply> RecentReplies { get { lock (_recent) return _recent.ToArray(); } }
    public long NowMs => _clock.ElapsedMilliseconds;

    public IReadOnlyList<ReactionCue> Pending { get { lock (_gate) return _queue.ToArray(); } }

    /// <summary>최근 60초 메시지 수(링 버퍼 · 무할당).</summary>
    public int MessagesPerMinute
    {
        get
        {
            var now = NowMs; var n = 0;
            lock (_msgTimes) foreach (var t in _msgTimes) if (t > 0 && now - t <= 60_000) n++;
            return n;
        }
    }

    // ── 리액션 계획(순수) ─────────────────────────────────────────────────────

    public static ReactionTier TierOf(double krw) => krw >= 200_000 ? ReactionTier.Legendary : krw >= 50_000 ? ReactionTier.Hype : krw >= 10_000 ? ReactionTier.Cheer : krw >= 1_000 ? ReactionTier.Thanks : ReactionTier.None;

    public static string TierLabel(ReactionTier t) => t switch
    {
        ReactionTier.Legendary => "👑 레전더리", ReactionTier.Hype => "🔥 하이프", ReactionTier.Cheer => "🎆 치어", ReactionTier.Thanks => "✨ 땡큐", _ => "—",
    };

    /// <summary>후원 → 리액션 큐(등급 · 키워드 오버라이드).</summary>
    public static ReactionCue Plan(DonationEvent d, long nowMs)
    {
        var krw = VTuberPersonaMemoryEngine.ToKrw(d.Amount, d.Currency);
        var tier = TierOf(krw);
        var (vfx, jingle, beats, dur) = tier switch
        {
            ReactionTier.Legendary => ("mega-fireworks", "legendary", 16, 10_000),
            ReactionTier.Hype => ("fireworks", "fanfare", 8, 7_000),
            ReactionTier.Cheer => ("fireworks", "fanfare", 4, 5_000),
            ReactionTier.Thanks => ("sparkle", "chime", 0, 3_500),
            _ => ("sparkle", "chime", 0, 2_500),
        };
        var m = (d.Message ?? "").ToLowerInvariant();
        if (m.Contains("춤") || m.Contains("댄스") || m.Contains("dance")) { beats = Math.Max(beats, 8); if (tier == ReactionTier.None) tier = ReactionTier.Thanks; }
        if (m.Contains("하트") || m.Contains("사랑") || m.Contains("love") || m.Contains('❤')) { vfx = tier >= ReactionTier.Hype ? vfx : "hearts"; jingle = tier >= ReactionTier.Legendary ? jingle : "love"; }
        if (m.Contains("생일") || m.Contains("birthday")) { vfx = "confetti"; jingle = "birthday"; }
        dur = Math.Max(dur, beats * 500 + 1500);
        var banner = d.FanName + "님 " + (krw >= 1 ? krw.ToString("#,0", CultureInfo.InvariantCulture) + "원 " : "") + "후원 감사합니다!";
        return new ReactionCue(d.Id, d.FanName, krw, tier, vfx, jingle, beats, dur, banner, VTuberPersonaMemoryEngine.Clip(d.Message, 60), nowMs);
    }

    private static DanceMove HookOf(DanceStyle s) => s switch
    {
        DanceStyle.HipHop => DanceMove.HipHopRock, DanceStyle.House => DanceMove.StepTouch, DanceStyle.Popping => DanceMove.FreezePose, DanceStyle.Contemporary => DanceMove.ArmWave, _ => DanceMove.KPopPoint,
    };

    /// <summary>감사 안무 — 파워 히트(2박) → 스타일 훅 → 프리즈(2박).</summary>
    public static ChoreographyPlan ThankYouDance(DanceStyle style, int beats, double bpm = 120)
    {
        beats = Math.Clamp(beats, 4, 32);
        var mid = beats - 4;
        var segs = mid > 0
            ? new[] { new ChoreoSegment(0, 2, DanceMove.PowerHit, 1, false), new ChoreoSegment(2, mid, HookOf(style), 1, false), new ChoreoSegment(2 + mid, 2, DanceMove.FreezePose, 1, false) }
            : new[] { new ChoreoSegment(0, 2, DanceMove.PowerHit, 1, false), new ChoreoSegment(2, 2, DanceMove.FreezePose, 1, false) };
        var beatSec = 60.0 / bpm;
        return new ChoreographyPlan { Style = style, Bpm = bpm, BeatSec = beatSec, FirstBeatSec = 0, DurationSec = beats * beatSec, Amplitude = 0.9f, Complexity = 0.5f, Segments = segs, HookRepeats = 1, Summary = "감사 댄스" };
    }

    // ── 큐 ──────────────────────────────────────────────────────────────────

    /// <summary>후원 수신 → 리액션 큐 + 답변 수신함(우선). 중복 Id 무시. 반환: 큐 등록 여부.</summary>
    public bool ReceiveDonation(DonationEvent d)
    {
        var cue = Plan(d, NowMs);
        var ok = Enqueue(cue);
        if (ok && SessionPersonaId is { } pid)
        {
            // 후원은 수신 즉시 팬/수익 집계(답변은 발화 순서대로 · 중복 기록 방지 플래그 큐)
            var m = new ChatMessage(d.FanId, d.FanName, d.Message, d.Amount, d.Currency, d.Platform);
            _memory.Ingest(pid, m);
            PushInbox(m, true);
        }
        return ok;
    }

    public bool Enqueue(ReactionCue cue)
    {
        lock (_gate)
        {
            if (_seen.Count >= 1024) _seen.Clear();
            if (!_seen.Add(cue.Id)) return false;
            if (_queue.Count >= QueueCap)
            {
                if (cue.Tier <= _queue[^1].Tier) { Interlocked.Increment(ref _dropped); return false; }
                _queue.RemoveAt(_queue.Count - 1);
                Interlocked.Increment(ref _dropped);
            }
            var i = 0;
            while (i < _queue.Count && _queue[i].Tier >= cue.Tier) i++;
            _queue.Insert(i, cue);
        }
        LastAction = "📥 " + TierLabel(cue.Tier) + " · " + cue.FanName;
        Raise();
        return true;
    }

    /// <summary>틱 — 현재 리액션 종료 시 다음 큐 시작(유휴 경로 무할당). 반환: 새로 시작한 큐.</summary>
    public ReactionCue? Tick(long nowMs, DanceStyle style = DanceStyle.KPop)
    {
        ReactionCue next;
        lock (_gate)
        {
            if (_current is not null && nowMs < _currentEnd) return null;
            _current = null;
            if (_queue.Count == 0) return null;
            next = _queue[0];
            _queue.RemoveAt(0);
            _current = next;
            _currentEnd = nowMs + next.DurationMs;
        }
        try
        {
            var jingle = EnsureJingle(next.Jingle);
            _bridge.PushOverlay(new OverlayCue("reaction", next.Vfx, next.Banner, next.Detail, jingle, next.DurationMs, (int)next.Tier));
            if (next.DanceBeats > 0) _bridge.PlayDance(ThankYouDance(style, next.DanceBeats), next.DanceBeats * 0.5);
            _bridge.SetMood(next.Tier >= ReactionTier.Hype ? FacialEmotion.Surprised : FacialEmotion.HappySmile);
        }
        catch (Exception ex) { Program.Log("Reaction.Start", ex); }
        Interlocked.Increment(ref _played);
        LastAction = "🎉 " + TierLabel(next.Tier) + " · " + next.FanName + " · " + next.Vfx + (next.DanceBeats > 0 ? " · 댄스 " + next.DanceBeats.ToString(CultureInfo.InvariantCulture) + "박" : "");
        Raise();
        return next;
    }

    public void ClearQueue()
    {
        lock (_gate) { _queue.Clear(); _current = null; }
        Raise();
    }

    // ── 징글(BCL 절차적 합성) ─────────────────────────────────────────────────

    /// <summary>징글 파일 보장(미디어 폴더 · 최초 1회 합성). 반환: 파일명(오버레이 /media/).</summary>
    public string? EnsureJingle(string kind)
    {
        if (Array.IndexOf(Jingles, kind) < 0) kind = "chime";
        var name = "jingle_" + kind + ".wav";
        var path = Path.Combine(_bridge.MediaRoot, name);
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(_bridge.MediaRoot);
                var x = RenderJingle(kind, 44100);
                SoundDsp.WriteStereoWav(path, x, x, 44100);
            }
            return name;
        }
        catch (Exception ex) { Program.Log("Reaction.Jingle", ex); return null; }
    }

    /// <summary>징글 합성(모노 · 결정적) — 멜로디(MIDI, 박) · 음색(벨/브라스/소프트).</summary>
    public static float[] RenderJingle(string kind, int rate)
    {
        (int Midi, double Beats)[] mel = kind switch
        {
            "fanfare" => new[] { (67, 0.25), (72, 0.25), (76, 0.25), (79, 0.75), (76, 0.25), (79, 1.5) },
            "legendary" => new[] { (60, 0.25), (64, 0.25), (67, 0.25), (72, 0.25), (76, 0.25), (79, 0.25), (84, 0.5), (79, 0.25), (84, 2.0) },
            "birthday" => new[] { (67, 0.375), (67, 0.125), (69, 0.5), (67, 0.5), (72, 0.5), (71, 1.0) },
            "love" => new[] { (76, 0.5), (79, 0.5), (84, 1.5) },
            _ => new[] { (84, 0.25), (88, 0.25), (91, 0.75) },
        };
        var spb = 60.0 / (kind == "love" ? 96 : 140);
        double total = 0;
        foreach (var (_, b) in mel) total += b;
        var n = (int)((total * spb + 0.8) * rate);
        var y = new float[n];
        double t0 = 0;
        foreach (var (midi, b) in mel)
        {
            var hz = AiGenerativeMusicEngine.Hz(midi);
            var dur = b * spb + (kind == "fanfare" || kind == "legendary" ? 0.05 : 0.5);
            var s = (int)(t0 * rate);
            var e = Math.Min(n, s + (int)(dur * rate));
            var lp = Biquad.LowPass(2400, 0.8, rate);
            double ph = 0;
            for (var i = s; i < e; i++)
            {
                var tt = (i - s) / (float)rate;
                ph += hz / rate; ph -= Math.Floor(ph);
                float v;
                if (kind is "fanfare" or "legendary")
                {
                    var env = MathF.Min(1, tt / 0.03f) * MathF.Min(1, (e - i) / (rate * 0.04f));
                    v = lp.Process((float)(ph * 2 - 1)) * env * 0.35f;
                }
                else
                {
                    var env = MathF.Exp(-tt * (kind == "love" ? 2.5f : 5f)) * MathF.Min(1, tt / 0.004f);
                    var a = (float)(ph * 2 * Math.PI);
                    v = (MathF.Sin(a) + 0.35f * MathF.Sin(a * 2.76f) + 0.15f * MathF.Sin(a * 5.4f)) * env * 0.3f;
                }
                y[i] += v;
            }
            t0 += b * spb;
        }
        if (kind == "legendary")   // 마지막 장3화음 서스테인
        {
            var s = (int)((total - 2.0) * spb * rate);
            foreach (var m in new[] { 72, 76, 79 })
            {
                var hz = AiGenerativeMusicEngine.Hz(m); double ph = 0;
                for (var i = Math.Max(0, s); i < n; i++) { ph += hz / rate; ph -= Math.Floor(ph); y[i] += (float)(Math.Sin(ph * 2 * Math.PI) * 0.12 * Math.Min(1, (n - i) / (rate * 0.3))); }
            }
        }
        var peak = 1e-6f;
        foreach (var v in y) peak = MathF.Max(peak, MathF.Abs(v));
        var g = 0.8f / peak;
        for (var i = 0; i < n; i++) y[i] *= g;
        return y;
    }

    // ── 자율 방송 세션 ───────────────────────────────────────────────────────

    public void ReceiveChat(ChatMessage m)
    {
        if (m.Amount > 0) { ReceiveDonation(new DonationEvent(Guid.NewGuid(), m.FanId, m.FanName, m.Amount, m.Currency, m.Text, m.Platform)); return; }
        PushInbox(m, false);
    }

    private void PushInbox(ChatMessage m, bool priority)
    {
        Interlocked.Increment(ref _messages);
        lock (_msgTimes) { _msgTimes[_msgPos] = NowMs; _msgPos = (_msgPos + 1) % _msgTimes.Length; }
        if (Interlocked.Read(ref _inboxCount) >= InboxCap && _inbox.TryDequeue(out _)) { Interlocked.Decrement(ref _inboxCount); Interlocked.Increment(ref _dropped); }
        (priority ? _priority : _inbox).Enqueue(m);
        Interlocked.Increment(ref _inboxCount);
    }

    /// <summary>1-Click 자율 방송 시작 — 브릿지 기동 · 방송 카운트 · 세션 루프(채팅 답변/리액션/공백 멘트/데모/Twitch).</summary>
    public bool StartSession(string personaId, SessionOptions? options = null, int overlayPort = 17860)
    {
        if (_memory.Find(personaId) is not { } persona) return false;
        StopSession();
        _opts = options ?? new SessionOptions();
        _bridge.Start(overlayPort);
        _memory.RecordBroadcast(personaId);
        SessionPersonaId = personaId;
        _startedUtc = DateTime.UtcNow;
        var cts = new CancellationTokenSource();
        _session = cts;
        _ = SessionLoopAsync(persona, cts.Token);
        if (!string.IsNullOrWhiteSpace(_opts.TwitchChannel)) _ = TwitchLoopAsync(_opts.TwitchChannel!, cts.Token);
        LastAction = "🔴 자율 방송 시작 · " + persona.Name + (_opts.DemoChat ? " · 데모 채팅" : "") + (_opts.TwitchChannel is null ? "" : " · Twitch #" + _opts.TwitchChannel);
        Raise();
        return true;
    }

    public void StopSession()
    {
        var cts = Interlocked.Exchange(ref _session, null);
        if (cts is null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        cts.Dispose();
        SessionPersonaId = null;
        TwitchStatus = null;
        LastAction = "⏹ 자율 방송 종료";
        Raise();
    }

    private static readonly (string Name, string Text)[] DemoLines =
    {
        ("별빛팬", "안녕! 오늘 방송 기다렸어"), ("초코칩", "나는 딸기 케이크 좋아해"), ("밤하늘", "오늘 뭐 할 거야?"), ("별빛팬", "어제 시험 끝났어!"),
        ("새벽감성", "노래 한 곡 불러줘"), ("초코칩", "내 생일 다음주야"), ("코코아", "처음 왔어요 반가워요"), ("밤하늘", "세계관 설정 너무 좋아"),
    };

    private async Task SessionLoopAsync(VTuberPersona persona, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        long lastReply = -100_000, lastSpeech = NowMs, nextDemo = NowMs + 1500;
        var idleSeq = 0; var demoSeq = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var now = NowMs;
                    Tick(now, persona.Dance);
                    if (_opts.DemoChat && now >= nextDemo)
                    {
                        var (n, t) = DemoLines[demoSeq % DemoLines.Length];
                        if (demoSeq % 5 == 4) ReceiveDonation(new DonationEvent(Guid.NewGuid(), "demo_" + n, n, demoSeq % 10 == 9 ? 50_000 : 5_000, "KRW", demoSeq % 10 == 9 ? "춤 춰줘!" : "응원해!", "demo"));
                        else ReceiveChat(new ChatMessage("demo_" + n, n, t, 0, "KRW", "demo"));
                        demoSeq++;
                        nextDemo = now + 3500;
                    }
                    if (_bridge.Speaking || Volatile.Read(ref _inFlight) != 0) { if (_bridge.Speaking) lastSpeech = now; continue; }
                    ChatMessage? m = null; var pre = false;
                    if (now - lastReply >= _opts.MinReplyGapMs) { if (_priority.TryDequeue(out m)) pre = true; else _inbox.TryDequeue(out m); }
                    if (m is not null)
                    {
                        Interlocked.Decrement(ref _inboxCount);
                        lastReply = lastSpeech = now;
                        Volatile.Write(ref _inFlight, 1);
                        _ = ReplyAsync(persona, m, pre, ct);
                    }
                    else if (now - lastSpeech >= _opts.IdleSeconds * 1000L)
                    {
                        lastSpeech = now;
                        Volatile.Write(ref _inFlight, 1);
                        _ = SpeakAsync(persona, _memory.IdleTalk(persona.Id, idleSeq++), FacialEmotion.HappySmile, ct);
                    }
                }
                catch (Exception ex) { Program.Log("VTuber.Session", ex); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReplyAsync(VTuberPersona persona, ChatMessage m, bool alreadyIngested, CancellationToken ct)
    {
        try
        {
            var r = await _memory.RespondAsync(persona.Id, m, _opts.UseLlm, ct, alreadyIngested).ConfigureAwait(false);
            if (r is null) return;
            lock (_recent) { _recent.Insert(0, r); if (_recent.Count > 50) _recent.RemoveAt(_recent.Count - 1); }
            await SpeakCoreAsync(persona, r.Text, r.Emotion, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Program.Log("VTuber.Reply", ex); }
        finally { Volatile.Write(ref _inFlight, 0); Raise(); }
    }

    private async Task SpeakAsync(VTuberPersona persona, string text, FacialEmotion e, CancellationToken ct)
    {
        try { await SpeakCoreAsync(persona, text, e, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Program.Log("VTuber.Idle", ex); }
        finally { Volatile.Write(ref _inFlight, 0); }
    }

    private async Task SpeakCoreAsync(VTuberPersona persona, string text, FacialEmotion e, CancellationToken ct)
    {
        string? voice = null;
        if (_opts.UseTts && _tts is { IsAvailable: true })
        {
            try { voice = await _tts.SynthesizeAsync(text, persona.Voice, 1.05, ct: ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Program.Log("VTuber.Tts", ex); }
        }
        var perf = await _bridge.PrepareSpeechAsync(text, e, voice, ct).ConfigureAwait(false);
        _bridge.Speak(perf);
    }

    // ── Twitch 익명 IRC(읽기 전용) ───────────────────────────────────────────

    public static bool IsValidChannel(string c)
    {
        if (c.Length is < 3 or > 25) return false;
        foreach (var ch in c) if (!(char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch == '_')) return false;
        return true;
    }

    /// <summary>IRC 라인(태그 포함) → 채팅(PRIVMSG 아님 null). Bits → USD 0.01/bit.</summary>
    public static ChatMessage? ParseTwitchLine(string line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        string? tags = null; var rest = line;
        if (rest[0] == '@') { var sp = rest.IndexOf(' '); if (sp < 0) return null; tags = rest[1..sp]; rest = rest[(sp + 1)..]; }
        var pm = rest.IndexOf(" PRIVMSG #", StringComparison.Ordinal);
        if (pm < 0 || rest[0] != ':') return null;
        var bang = rest.IndexOf('!');
        var nick = bang > 1 && bang < pm ? rest[1..bang] : "anon";
        var colon = rest.IndexOf(" :", pm + 10, StringComparison.Ordinal);
        if (colon < 0) return null;
        var text = rest[(colon + 2)..];
        string name = nick, id = nick; double bits = 0;
        if (tags is not null)
            foreach (var kv in tags.Split(';'))
            {
                var eq = kv.IndexOf('=');
                if (eq <= 0) continue;
                var k = kv[..eq]; var v = kv[(eq + 1)..];
                if (k == "display-name" && v.Length > 0) name = v;
                else if (k == "user-id" && v.Length > 0) id = v;
                else if (k == "bits" && double.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b)) bits = b;
            }
        return new ChatMessage("tw_" + id, name, VTuberPersonaMemoryEngine.Clip(text, VTuberPersonaMemoryEngine.MaxText), bits * 0.01, "USD", "twitch");
    }

    private async Task TwitchLoopAsync(string channel, CancellationToken ct)
    {
        channel = channel.Trim().TrimStart('#').ToLowerInvariant();
        if (!IsValidChannel(channel)) { TwitchStatus = "⚠ 채널명 형식 오류"; return; }
        var backoff = 1000;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                using (var d = CancellationTokenSource.CreateLinkedTokenSource(ct)) { d.CancelAfter(5000); await ws.ConnectAsync(new Uri("wss://irc-ws.chat.twitch.tv:443"), d.Token).ConfigureAwait(false); }
                foreach (var cmd in new[] { "CAP REQ :twitch.tv/tags", "PASS SCHMOOPIIE", "NICK justinfan" + Random.Shared.Next(10000, 99999).ToString(CultureInfo.InvariantCulture), "JOIN #" + channel })
                    await ws.SendAsync(Encoding.UTF8.GetBytes(cmd + "\r\n"), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                TwitchStatus = "🟣 Twitch #" + channel + " 수신 중";
                backoff = 1000;
                Raise();
                var buf = new byte[16384]; var sb = new StringBuilder();
                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    var r = await ws.ReceiveAsync(buf, ct).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                    if (!r.EndOfMessage) continue;
                    foreach (var line in sb.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (line.StartsWith("PING", StringComparison.Ordinal)) { await ws.SendAsync(Encoding.UTF8.GetBytes("PONG" + line[4..] + "\r\n"), WebSocketMessageType.Text, true, ct).ConfigureAwait(false); continue; }
                        if (ParseTwitchLine(line) is not { } msg) continue;
                        if (msg.Amount > 0) ReceiveDonation(new DonationEvent(Guid.NewGuid(), msg.FanId, msg.FanName, msg.Amount, msg.Currency, msg.Text, "twitch"));
                        else ReceiveChat(msg);
                    }
                    sb.Clear();
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Program.Log("VTuber.Twitch", ex); TwitchStatus = "⚠ Twitch 재연결 대기: " + ex.Message; Raise(); }
            try { await Task.Delay(backoff, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            backoff = Math.Min(30_000, backoff * 2);
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("Reaction.OnChange", ex); }
    }

    public void Dispose() => StopSession();
}
