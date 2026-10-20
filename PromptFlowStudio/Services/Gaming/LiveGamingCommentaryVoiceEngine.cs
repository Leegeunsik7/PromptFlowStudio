using System.Diagnostics;
using System.Globalization;
using PromptFlowStudio.Services.Media;
using PromptFlowStudio.Services.Storyboard;
using PromptFlowStudio.Services.VTuber;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Services.Gaming;

public sealed record CommentaryOptions(bool UseLlm = false, bool UseTts = true, int BudgetMs = 1000, int CooldownMs = 2500, int BanterSeconds = 20, bool OverlayFx = true);

public sealed record CommentaryLine(GameEventKind Kind, string Text, FacialEmotion Emotion, string Source, long LatencyMs, bool Voiced, DateTime AtUtc);

/// <summary>
/// [PromptFlow Studio v16.39.0] 🎙 라이브 게이밍 AI 실시간 해설 &amp; 리액션 엔진.
///   ▸ 게임 이벤트(v16.39.0 비전 추출기) → 우선순위 1슬롯 코얼레싱(캡처 스레드 무할당 · 고우선 이벤트 선점) → v16.38.0 V-Tuber 페르소나(이름/캐치프레이즈) 해설.
///   ▸ 1초 예산: [선택] Gemini 재작성(예산 55%) → TTS(남은 예산 · 초과 시 무음 자막) → 입모양/감정 → 브릿지 Speak(오버레이 자막 · VMC/VTS) · 지연 측정.
///   ▸ 이벤트 공백 시 비하인드 토크(전적/상태 기반) · 대형 이벤트 오버레이 VFX(멀티킬/클러치/승리).
/// </summary>
public sealed class LiveGamingCommentaryVoiceEngine : IDisposable
{
    private readonly RealtimeGameVisionEventExtractor _vision;
    private readonly VTuberPersonaMemoryEngine _memory;
    private readonly LiveVTuberBroadcastingBridge _bridge;
    private readonly AiVoiceoverTtsService? _tts;
    private readonly GeminiClient? _gemini;
    private readonly object _gate = new();
    private readonly List<CommentaryLine> _recent = new();
    private readonly Action<GameEvent> _onEvent;
    private GameEvent _pending;
    private bool _hasPending;
    private int _inFlight, _seq;
    private long _fired, _voiced, _coalesced, _latencySum, _maxLatency, _lastLatency, _lastEventMs;
    private CancellationTokenSource? _session;
    private CommentaryOptions _opts = new();

    public LiveGamingCommentaryVoiceEngine(IServiceProvider sp)
        : this((RealtimeGameVisionEventExtractor)sp.GetService(typeof(RealtimeGameVisionEventExtractor))!, (VTuberPersonaMemoryEngine)sp.GetService(typeof(VTuberPersonaMemoryEngine))!,
               (LiveVTuberBroadcastingBridge)sp.GetService(typeof(LiveVTuberBroadcastingBridge))!, sp.GetService(typeof(AiVoiceoverTtsService)) as AiVoiceoverTtsService,
               sp.GetService(typeof(GeminiClient)) as GeminiClient) { }

    public LiveGamingCommentaryVoiceEngine(RealtimeGameVisionEventExtractor vision, VTuberPersonaMemoryEngine memory, LiveVTuberBroadcastingBridge bridge, AiVoiceoverTtsService? tts = null, GeminiClient? gemini = null)
    {
        _vision = vision;
        _memory = memory;
        _bridge = bridge;
        _tts = tts;
        _gemini = gemini;
        _onEvent = OnGameEvent;
    }

    public event Action? OnChange;

    public bool InSession => _session is not null;
    public bool LlmAvailable => _gemini?.Enabled == true;
    public string? PersonaId { get; private set; }
    public string? LastAction { get; private set; }
    public long Fired => Interlocked.Read(ref _fired);
    public long Voiced => Interlocked.Read(ref _voiced);
    public long Coalesced => Interlocked.Read(ref _coalesced);
    public long LastLatencyMs => Interlocked.Read(ref _lastLatency);
    public long MaxLatencyMs => Interlocked.Read(ref _maxLatency);
    public double AvgLatencyMs { get { var n = Fired; return n == 0 ? 0 : Interlocked.Read(ref _latencySum) / (double)n; } }
    public CommentaryOptions Options => _opts;
    public IReadOnlyList<CommentaryLine> Recent { get { lock (_recent) return _recent.ToArray(); } }

    public static int PriorityOf(GameEventKind k) => k switch
    {
        GameEventKind.Victory or GameEventKind.Defeat => 9,
        GameEventKind.Clutch => 8,
        GameEventKind.MultiKill => 7,
        GameEventKind.Death => 6,
        GameEventKind.Kill => 5,
        GameEventKind.LowHealth => 4,
        GameEventKind.Danger => 3,
        GameEventKind.Assist => 2,
        GameEventKind.MatchStart => 1,
        _ => 0,
    };

    public static FacialEmotion EmotionOf(GameEventKind k) => k switch
    {
        GameEventKind.Kill or GameEventKind.Assist or GameEventKind.MatchStart => FacialEmotion.HappySmile,
        GameEventKind.MultiKill or GameEventKind.Victory => FacialEmotion.Giggle,
        GameEventKind.Clutch or GameEventKind.Danger or GameEventKind.LowHealth => FacialEmotion.Surprised,
        GameEventKind.Death or GameEventKind.Defeat => FacialEmotion.Pout,
        _ => FacialEmotion.Neutral,
    };

    private static readonly string[][] Bank =
    {
        /* None */       new[] { "{n} 여기 있어!" },
        /* MatchStart */ new[] { "자, 새 판 시작! 오늘은 캐리 각이다!", "준비됐지? {n}이랑 같이 이겨보자!", "첫 판부터 집중! 가보자고!" },
        /* Kill */       new[] { "깔끔하게 한 명 잡았다! {k}킬!", "오 방금 각 좋았어!", "나이스 킬~ 이 기세 그대로!", "에임 미쳤다 진짜!" },
        /* MultiKill */  new[] { "{s}연속 킬! 이거 실화야?!", "멀티킬 터졌다! 채팅창 난리 났어!", "와 쓸어버렸네, {s}명 순삭!" },
        /* Clutch */     new[] { "체력 {h}%에서 킬을?! 이게 클러치지!", "숨 멎을 뻔했어! 미친 클러치!", "딸피 역관광! 레전드 장면 나왔다!" },
        /* Assist */     new[] { "어시스트 굿! 팀플 완벽해!", "도와준 거 다 봤어, 센스 최고!", "킬보다 값진 어시스트!" },
        /* Death */      new[] { "아앗, 잡혔다… 괜찮아 다음에 갚아주자!", "방금은 상대가 잘했어. 침착하게!", "데스 {d}… 포지션만 조금 뒤로 빼자!" },
        /* LowHealth */  new[] { "체력 위험해! 빨리 빠져!", "피 {h}%야, 무리하지 마!", "지금은 후퇴가 정답이야!" },
        /* Danger */     new[] { "미니맵 봐! 적이 몰려와!", "조심해, 적들 우르르 온다!", "지금 싸우면 위험해, 시야 먼저!" },
        /* Victory */    new[] { "승리! 우리가 해냈어!", "이겼다~! 최종 {k}/{d}/{a}, 오늘 폼 미쳤다!", "GG! 다들 응원 고마워!" },
        /* Defeat */     new[] { "아쉽다… 그래도 잘 싸웠어!", "패배지만 배운 게 많아. 다음 판 가자!", "괜찮아, 다음 판은 무조건 이긴다!" },
    };

    private static readonly string[] Banter =
    {
        "지금 {k}/{d}/{a}! 이 페이스면 MVP 각이야.", "채팅 여러분, 승부 예측 투표 아직 열려 있어요!", "사실 나 이 게임 랭크 올리는 중이야, 비밀이야~",
        "이런 소강 상태엔 시야랑 정비부터 챙기자.", "물 한 모금 마시고 집중 모드 들어간다!", "오늘 해설 어때? 채팅으로 점수 매겨줘!",
    };

    /// <summary>이벤트 → 해설 문장(페르소나 · 템플릿 · 결정적 seq).</summary>
    public static string ComposeLine(VTuberPersona? p, GameEvent e, int seq)
    {
        var bank = Bank[(int)e.Kind < Bank.Length ? (int)e.Kind : 0];
        var s = Fill(bank[(seq & 0x7FFFFFFF) % bank.Length], p, e);
        if (p is { Catchphrases.Count: > 0 } && PriorityOf(e.Kind) >= 7 && seq % 2 == 0) s += " " + p.Catchphrases[seq % p.Catchphrases.Count];
        return s;
    }

    public static string ComposeBanter(VTuberPersona? p, GameStateSnapshot s, int seq) =>
        Fill(Banter[(seq & 0x7FFFFFFF) % Banter.Length], p, new GameEvent(GameEventKind.None, 0, s.Hp, s.Kills, s.Deaths, s.Assists, s.Streak, 1));

    private static string Fill(string t, VTuberPersona? p, GameEvent e) => t
        .Replace("{n}", p?.Name ?? "나", StringComparison.Ordinal)
        .Replace("{k}", e.Kills.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("{d}", e.Deaths.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("{a}", e.Assists.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("{s}", Math.Max((int)e.Streak, 2).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("{h}", ((int)Math.Round(e.Hp * 100)).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    public static string BuildPrompt(VTuberPersona? p, GameEvent e, GameGenre g, string draft) =>
        "너는 게임 방송 코스트리머 V-Tuber '" + (p?.Name ?? "AI") + "'다. 말투: " + (p?.Speech ?? "밝은 반말") + ". 컨셉: " + VTuberPersonaMemoryEngine.Clip(p?.Concept, 80) +
        "\n게임: " + RealtimeGameVisionEventExtractor.GenreLabel(g) + " · 이벤트: " + RealtimeGameVisionEventExtractor.KindLabel(e.Kind) +
        " · K/D/A " + e.Kills.ToString(CultureInfo.InvariantCulture) + "/" + e.Deaths.ToString(CultureInfo.InvariantCulture) + "/" + e.Assists.ToString(CultureInfo.InvariantCulture) +
        " · 체력 " + ((int)(e.Hp * 100)).ToString(CultureInfo.InvariantCulture) + "%\n초안: " + draft +
        "\n위트 있는 실시간 해설 한 문장(40자 이내)만 출력. 이모지·따옴표·설명 금지.";

    // ── 세션 ─────────────────────────────────────────────────────────────

    public bool Start(string? personaId, CommentaryOptions? options = null, int overlayPort = 17860)
    {
        Stop();
        _opts = options ?? new CommentaryOptions();
        PersonaId = personaId is not null && _memory.Find(personaId) is not null ? personaId : null;
        try { _bridge.Start(overlayPort); } catch (Exception ex) { Program.Log("GameCommentary.Bridge", ex); }
        lock (_gate) _hasPending = false;
        _lastEventMs = _vision.NowMs;
        _vision.OnGameEvent += _onEvent;
        var cts = new CancellationTokenSource();
        _session = cts;
        _ = LoopAsync(cts.Token);
        LastAction = "🎙 해설 시작 · " + (PersonaId is null ? "기본 해설자" : _memory.Find(PersonaId)!.Name) + (_opts.UseLlm ? " · Gemini" : " · BCL 템플릿");
        Raise();
        return true;
    }

    public void Stop()
    {
        var cts = Interlocked.Exchange(ref _session, null);
        if (cts is null) return;
        _vision.OnGameEvent -= _onEvent;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        cts.Dispose();
        LastAction = "⏹ 해설 종료";
        Raise();
    }

    /// <summary>캡처 스레드 콜백 — 우선순위 1슬롯 코얼레싱(무할당).</summary>
    private void OnGameEvent(GameEvent e)
    {
        Interlocked.Exchange(ref _lastEventMs, e.AtMs);
        lock (_gate)
        {
            if (_hasPending)
            {
                Interlocked.Increment(ref _coalesced);
                if (PriorityOf(e.Kind) < PriorityOf(_pending.Kind)) return;
            }
            _pending = e;
            _hasPending = true;
        }
    }

    /// <summary>테스트/수동 이벤트 주입.</summary>
    public void Inject(GameEvent e) => OnGameEvent(e);

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        long lastFire = -100_000;
        var banterSeq = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var now = _vision.NowMs;
                    if (Volatile.Read(ref _inFlight) != 0) continue;
                    GameEvent e = default; var has = false;
                    lock (_gate)
                    {
                        if (_hasPending)
                        {
                            var urgent = PriorityOf(_pending.Kind) >= 8;
                            if (urgent || (!_bridge.Speaking && now - lastFire >= _opts.CooldownMs)) { e = _pending; _hasPending = false; has = true; }
                        }
                    }
                    if (has)
                    {
                        lastFire = now;
                        Volatile.Write(ref _inFlight, 1);
                        _ = FireAsync(e, false, ct);
                    }
                    else if (_opts.BanterSeconds > 0 && !_bridge.Speaking && now - Interlocked.Read(ref _lastEventMs) >= _opts.BanterSeconds * 1000L && now - lastFire >= _opts.BanterSeconds * 1000L)
                    {
                        lastFire = now;
                        Volatile.Write(ref _inFlight, 1);
                        var s = _vision.Snapshot();
                        _ = FireAsync(new GameEvent(GameEventKind.None, now, s.Hp, s.Kills, s.Deaths, s.Assists, (byte)(banterSeq++ & 0xFF), 1), true, ct);
                    }
                }
                catch (Exception ex) { Program.Log("GameCommentary.Tick", ex); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task FireAsync(GameEvent e, bool banter, CancellationToken ct)
    {
        try
        {
            var line = await ProduceAsync(e, banter, ct).ConfigureAwait(false);
            lock (_recent) { _recent.Insert(0, line); if (_recent.Count > 50) _recent.RemoveAt(_recent.Count - 1); }
            LastAction = (banter ? "💬 " : "🎙 ") + VTuberPersonaMemoryEngine.Clip(line.Text, 40) + " · " + line.LatencyMs.ToString(CultureInfo.InvariantCulture) + "ms";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Program.Log("GameCommentary.Fire", ex); }
        finally { Volatile.Write(ref _inFlight, 0); Raise(); }
    }

    /// <summary>해설 1건 생성·발화(1초 예산) — 지연은 이벤트 감지 시각부터 Speak 까지.</summary>
    public async Task<CommentaryLine> ProduceAsync(GameEvent e, bool banter, CancellationToken ct)
    {
        var persona = PersonaId is null ? null : _memory.Find(PersonaId);
        var seq = Interlocked.Increment(ref _seq);
        var text = banter ? ComposeBanter(persona, _vision.Snapshot(), e.Streak) : ComposeLine(persona, e, seq);
        var emo = banter ? FacialEmotion.HappySmile : EmotionOf(e.Kind);
        var source = "BCL";
        var budget = Math.Clamp(_opts.BudgetMs, 300, 5000);
        var start = banter ? _vision.NowMs : Math.Min(e.AtMs, _vision.NowMs);
        long Remaining() => budget - (_vision.NowMs - start);

        if (_opts.UseLlm && _gemini is { Enabled: true } g && Remaining() > 300)
        {
            try
            {
                using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
                d.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(100, budget * 0.55 - (_vision.NowMs - start))));
                var r = await g.GenerateAsync(BuildPrompt(persona, e, _vision.Profile.Genre, text), null, 0.9, d.Token).ConfigureAwait(false);
                if (r.Ok && !string.IsNullOrWhiteSpace(r.Text)) { text = VTuberPersonaMemoryEngine.Clip(r.Text.Trim().Trim('"', '\''), 80); source = "Gemini"; }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (Exception ex) { Program.Log("GameCommentary.Llm", ex); }
        }

        string? voice = null;
        if (_opts.UseTts && _tts is { IsAvailable: true } && Remaining() > 150)
        {
            try
            {
                using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
                d.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(50, Remaining() - 80)));
                voice = await _tts.SynthesizeAsync(text, persona?.Voice ?? NarrationTone.Bright, 1.15, ct: d.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { voice = null; }
            catch (Exception ex) { Program.Log("GameCommentary.Tts", ex); }
        }

        SpeechPerformance perf;
        if (voice is not null)
        {
            try
            {
                using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
                d.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(30, Remaining())));
                perf = await _bridge.PrepareSpeechAsync(text, emo, voice, d.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { perf = await _bridge.PrepareSpeechAsync(text, emo, null, ct).ConfigureAwait(false); voice = null; }
        }
        else perf = await _bridge.PrepareSpeechAsync(text, emo, null, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        _bridge.Speak(perf);

        if (_opts.OverlayFx && !banter && PriorityOf(e.Kind) >= 7)
        {
            var (vfx, tier) = e.Kind switch
            {
                GameEventKind.Victory => ("mega-fireworks", 4),
                GameEventKind.Clutch => ("fireworks", 3),
                GameEventKind.Defeat => ("sparkle", 1),
                _ => ("confetti", 2),
            };
            _bridge.PushOverlay(new OverlayCue("reaction", vfx, RealtimeGameVisionEventExtractor.KindLabel(e.Kind), text, null, 3500, tier));
        }

        var latency = Math.Max(0, _vision.NowMs - start);
        Interlocked.Exchange(ref _lastLatency, latency);
        Interlocked.Add(ref _latencySum, latency);
        Interlocked.Increment(ref _fired);
        if (perf.VoiceFile is not null) Interlocked.Increment(ref _voiced);
        long m;
        while (latency > (m = Interlocked.Read(ref _maxLatency)) && Interlocked.CompareExchange(ref _maxLatency, latency, m) != m) { }
        return new CommentaryLine(banter ? GameEventKind.None : e.Kind, text, emo, source + (perf.VoiceFile is not null ? "+TTS" : ""), latency, perf.VoiceFile is not null, DateTime.UtcNow);
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("GameCommentary.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
