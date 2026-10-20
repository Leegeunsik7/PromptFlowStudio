using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PromptFlowStudio.Services.VTuber;

namespace PromptFlowStudio.Services.Gaming;

public enum PollPhase { Idle, Open, Locked, Resolved }

public sealed record CoachTip(string Icon, string Text, int Priority);

public sealed record CompanionCue(string Emote, string Say, string Anchor);

/// <summary>
/// [PromptFlow Studio v16.39.0] 🕹 인게임 HUD 오버레이 &amp; 시청자 예측 동기화기.
///   ▸ 전용 루프백 SSE 오버레이(OBS 브라우저 소스 · 게임 화면 위 투명 레이어): 상단 AI 훈수 바 · 승패 예측 투표창(실시간 막대) · AI 동료 아바타(앵커 ROI 추적 · 말풍선) · 이벤트 토스트.
///   ▸ 훈수: 규칙 기반(체력/미니맵 위험/연속 데스/연속 킬/종목별 팁) — 이벤트·상태 변화 시 갱신.
///   ▸ 예측: 열기/잠금/정산(승리·패배 이벤트 자동 정산 · 매치 시작 자동 오픈) · 팬당 1표(HashSet 중복 차단) · 채팅 !승/!패/!win/!lose 파싱 · 적중 포인트.
///   ▸ 상태 JSON(Utf8JsonWriter · 재사용 버퍼)은 변경 시에만 100ms 주기 송출 · 콜백 예외 격리.
/// </summary>
public sealed class InteractiveGamingOverlayController : IDisposable
{
    public const int DefaultPort = 17861, PollPoints = 100;

    private readonly RealtimeGameVisionEventExtractor _vision;
    private readonly OverlaySseServer _server;
    private readonly object _gate = new();
    private readonly HashSet<string> _voters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _ballots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _points = new(StringComparer.Ordinal);
    private readonly ArrayBufferWriter<byte> _json = new(2048);
    private readonly Action<GameEvent> _onEvent;
    private CancellationTokenSource? _cts;
    private int _winVotes, _loseVotes, _dirty;
    private long _pushes;
    private bool? _result;
    private CoachTip? _tip;
    private CompanionCue _companion = new("🙂", "같이 이겨보자!", "center");
    private string? _toast;
    private long _toastSeq;

    public InteractiveGamingOverlayController(IServiceProvider sp)
        : this((RealtimeGameVisionEventExtractor)sp.GetService(typeof(RealtimeGameVisionEventExtractor))!, null) { }

    public InteractiveGamingOverlayController(RealtimeGameVisionEventExtractor vision, string? mediaRoot)
    {
        _vision = vision;
        _server = new OverlaySseServer(mediaRoot ?? Path.Combine(AppContext.BaseDirectory, "overlay_media"), GameOverlayHtml);
        _onEvent = OnGameEvent;
    }

    public event Action? OnChange;

    public bool IsRunning => _cts is not null;
    public bool AutoPoll { get; set; } = true;
    public bool CoachEnabled { get; set; } = true;
    public string Hashtags { get; set; } = "";
    public string Url => _server.Url;
    public int Clients => _server.Clients;
    public long Pushes => Interlocked.Read(ref _pushes);
    public PollPhase Phase { get; private set; }
    public string Question { get; private set; } = "이번 판 승리할까요?";
    public int WinVotes { get { lock (_gate) return _winVotes; } }
    public int LoseVotes { get { lock (_gate) return _loseVotes; } }
    public double WinPct { get { lock (_gate) { var t = _winVotes + _loseVotes; return t == 0 ? 0.5 : _winVotes / (double)t; } } }
    public bool? Result => _result;
    public CoachTip? Tip => _tip;
    public CompanionCue Companion => _companion;
    public string? LastAction { get; private set; }

    public IReadOnlyList<KeyValuePair<string, long>> Leaderboard
    {
        get { lock (_gate) { var l = new List<KeyValuePair<string, long>>(_points); l.Sort((a, b) => b.Value.CompareTo(a.Value)); if (l.Count > 10) l.RemoveRange(10, l.Count - 10); return l; } }
    }

    public void Start(int port = DefaultPort)
    {
        if (_cts is not null) return;
        _server.Start(port);
        _vision.OnGameEvent += _onEvent;
        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = PushLoopAsync(cts.Token);
        Advise();
        MarkDirty();
        LastAction = "🕹 게임 오버레이 ON · " + _server.Url;
        Raise();
    }

    public void Stop()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null) return;
        _vision.OnGameEvent -= _onEvent;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        cts.Dispose();
        LastAction = "⏹ 게임 오버레이 OFF";
        Raise();
    }

    // ── 예측 투표 ─────────────────────────────────────────────────────────

    public void OpenPoll(string? question = null)
    {
        lock (_gate)
        {
            _voters.Clear(); _ballots.Clear(); _winVotes = _loseVotes = 0; _result = null;
            if (!string.IsNullOrWhiteSpace(question)) Question = VTuberPersonaMemoryEngine.Clip(question.Trim(), 60);
            Phase = PollPhase.Open;
        }
        LastAction = "🗳 예측 오픈 · " + Question;
        MarkDirty(); Raise();
    }

    public void LockPoll()
    {
        lock (_gate) { if (Phase != PollPhase.Open) return; Phase = PollPhase.Locked; }
        LastAction = "🔒 예측 마감";
        MarkDirty(); Raise();
    }

    /// <summary>정산 → 적중자 수(적중 시 PollPoints 포인트).</summary>
    public int Resolve(bool win)
    {
        int winners;
        lock (_gate)
        {
            if (Phase is PollPhase.Idle or PollPhase.Resolved) return 0;
            Phase = PollPhase.Resolved;
            _result = win;
            winners = 0;
            foreach (var (fan, pick) in _ballots)
            {
                if (pick != win) continue;
                winners++;
                _points[fan] = (_points.TryGetValue(fan, out var p) ? p : 0) + PollPoints;
            }
        }
        LastAction = "🏁 예측 정산 · " + (win ? "승리" : "패배") + " · 적중 " + winners.ToString(CultureInfo.InvariantCulture) + "명";
        MarkDirty(); Raise();
        return winners;
    }

    /// <summary>투표(팬당 1표 · Open 상태만).</summary>
    public bool Vote(string fanId, bool win)
    {
        if (string.IsNullOrWhiteSpace(fanId)) return false;
        lock (_gate)
        {
            if (Phase != PollPhase.Open || !_voters.Add(fanId)) return false;
            _ballots[fanId] = win;
            if (win) _winVotes++; else _loseVotes++;
        }
        MarkDirty();
        return true;
    }

    /// <summary>채팅 명령 파싱(!승 · !패 · !win · !lose) → 투표 반영 여부.</summary>
    public bool ReceiveChat(string fanId, string text) => ParseVote(text) is { } v && Vote(fanId, v);

    public static bool? ParseVote(ReadOnlySpan<char> text)
    {
        var t = text.Trim();
        if (t.Length < 2 || t[0] != '!') return null;
        t = t[1..];
        var sp = t.IndexOf(' ');
        if (sp > 0) t = t[..sp];
        if (t.Equals("승", StringComparison.Ordinal) || t.Equals("승리", StringComparison.Ordinal) || t.Equals("win", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Equals("패", StringComparison.Ordinal) || t.Equals("패배", StringComparison.Ordinal) || t.Equals("lose", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    // ── 훈수 · 동료 아바타 ─────────────────────────────────────────────────

    /// <summary>상태 기반 AI 훈수(우선순위 최고 1건).</summary>
    public static CoachTip Advise(GameGenre g, GameStateSnapshot s)
    {
        if (s.Phase is GameEventKind.Victory) return new("🏆", "승리! 다음 판도 같은 템포로 — 초반 시야 장악부터", 9);
        if (s.Phase is GameEventKind.Defeat) return new("🧠", "패배 복기: 데스가 난 구간의 포지션을 다시 확인해요", 9);
        if (s.Hp > 0.01f && s.Hp < 0.25f) return new("🩹", g == GameGenre.Rpg ? "체력 25% 미만 — 포션/힐 즉시 사용" : "체력 25% 미만 — 즉시 후퇴하고 회복하세요", 8);
        if (s.Danger >= 0.5f) return new("⚠", g == GameGenre.Valorant ? "적 다수 감지 — 유틸 쓰고 사이트 재정비" : "미니맵에 적 다수 — 교전 피하고 시야 확보", 7);
        if (s.DeathStreak >= 2) return new("🧭", "연속 데스 — 한 박자 뒤에서 팀과 함께 움직여요", 6);
        if (s.Streak >= 3) return new("🔥", g == GameGenre.LoL ? "기세 최고 — 드래곤/바론 압박 타이밍!" : "기세 최고 — 공격적으로 주도권 유지!", 5);
        return g switch
        {
            GameGenre.LoL => new("🗺", "와드 체크 · CS 집중 — 미니맵 3초마다 보기", 1),
            GameGenre.Valorant => new("🎯", "크로스헤어 머리 높이 유지 · 이동 후 정지 사격", 1),
            GameGenre.Fps => new("🎧", "발소리 체크 · 엄폐 후 리로드", 1),
            _ => new("🎒", "보스 패턴 관찰 · 쿨타임 스킬 아껴두기", 1),
        };
    }

    public static CompanionCue CompanionFor(GameEventKind k) => k switch
    {
        GameEventKind.Kill => new("👍", "나이스 킬!", "kda"),
        GameEventKind.MultiKill => new("🔥", "멀티킬! 미쳤다!", "kda"),
        GameEventKind.Clutch => new("😱", "클러치 장인!", "health"),
        GameEventKind.Assist => new("🤝", "팀플 굿!", "kda"),
        GameEventKind.Death => new("🫂", "괜찮아, 다시 가자", "center"),
        GameEventKind.LowHealth => new("🩹", "체력 여기 봐! 빠져!", "health"),
        GameEventKind.Danger => new("👉", "미니맵! 적 온다!", "minimap"),
        GameEventKind.Victory => new("🎉", "이겼다!!", "center"),
        GameEventKind.Defeat => new("🥲", "다음 판은 이긴다", "center"),
        GameEventKind.MatchStart => new("🙌", "새 판 화이팅!", "center"),
        _ => new("🙂", "같이 이겨보자!", "center"),
    };

    private void OnGameEvent(GameEvent e)
    {
        try
        {
            _companion = CompanionFor(e.Kind);
            _toast = RealtimeGameVisionEventExtractor.KindLabel(e.Kind);
            Interlocked.Increment(ref _toastSeq);
            if (e.Kind is GameEventKind.Victory or GameEventKind.Defeat) Resolve(e.Kind == GameEventKind.Victory);
            else if (e.Kind == GameEventKind.MatchStart && AutoPoll) OpenPoll();
            Advise();
            MarkDirty();
        }
        catch (Exception ex) { Program.Log("GameOverlay.Event", ex); }
    }

    private void Advise() => _tip = CoachEnabled ? Advise(_vision.Profile.Genre, _vision.Snapshot()) : null;

    private void MarkDirty() => Interlocked.Exchange(ref _dirty, 1);

    // ── 상태 JSON · 송출 ─────────────────────────────────────────────────

    /// <summary>오버레이 상태 JSON(UTF-8).</summary>
    public string BuildStateJson()
    {
        var p = _vision.Profile;
        var s = _vision.Snapshot();
        lock (_gate)
        {
            _json.Clear();
            using var w = new Utf8JsonWriter(_json);
            w.WriteStartObject();
            w.WriteString("type", "game");
            w.WriteString("genre", p.Label);
            w.WriteString("tags", Hashtags);
            w.WriteNumber("hp", Math.Round(s.Hp, 3));
            w.WriteNumber("k", s.Kills); w.WriteNumber("d", s.Deaths); w.WriteNumber("a", s.Assists);
            w.WriteNumber("danger", Math.Round(s.Danger, 3));
            if (_tip is { } tip) { w.WriteStartObject("tip"); w.WriteString("icon", tip.Icon); w.WriteString("text", tip.Text); w.WriteNumber("priority", tip.Priority); w.WriteEndObject(); }
            w.WriteStartObject("poll");
            w.WriteString("phase", Phase.ToString());
            w.WriteString("question", Question);
            w.WriteNumber("win", _winVotes); w.WriteNumber("lose", _loseVotes);
            if (_result is { } r) w.WriteBoolean("result", r);
            w.WriteEndObject();
            var c = _companion;
            var anchor = c.Anchor switch { "health" => p.Health, "minimap" => p.Minimap, "kda" => p.Kda, _ => p.Banner };
            w.WriteStartObject("companion");
            w.WriteString("emote", c.Emote); w.WriteString("say", c.Say); w.WriteString("anchor", c.Anchor);
            w.WriteNumber("x", Math.Round(anchor.X + anchor.W / 2, 3)); w.WriteNumber("y", Math.Round(anchor.Y, 3));
            w.WriteEndObject();
            if (_toast is not null) { w.WriteString("toast", _toast); w.WriteNumber("toastSeq", Interlocked.Read(ref _toastSeq)); }
            w.WriteEndObject();
            w.Flush();
            return Encoding.UTF8.GetString(_json.WrittenSpan);
        }
    }

    /// <summary>즉시 송출 → 수신 클라이언트 수.</summary>
    public int PushNow()
    {
        Interlocked.Exchange(ref _dirty, 0);
        var n = _server.IsRunning ? _server.Broadcast(BuildStateJson()) : 0;
        Interlocked.Increment(ref _pushes);
        return n;
    }

    private async Task PushLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        long lastFull = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var now = _vision.NowMs;
                    if (Volatile.Read(ref _dirty) == 0 && now - lastFull < 1000) continue;
                    lastFull = now;
                    Advise();
                    PushNow();
                    Raise();
                }
                catch (Exception ex) { Program.Log("GameOverlay.Push", ex); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("GameOverlay.OnChange", ex); }
    }

    public void Dispose()
    {
        Stop();
        _server.Dispose();
    }

    public const string GameOverlayHtml = """
<!doctype html><html><head><meta charset="utf-8"><title>PromptFlow Game Overlay</title><meta name="pfs-game-overlay" content="1">
<style>html,body{margin:0;background:transparent;overflow:hidden;font-family:'Pretendard','Malgun Gothic',sans-serif;color:#fff}
#tip{position:fixed;left:50%;top:10px;transform:translateX(-50%);padding:8px 20px;border-radius:999px;background:#000b;border:1px solid #38bdf8;font-size:22px;font-weight:800;opacity:0;transition:opacity .3s;white-space:nowrap}#tip.on{opacity:1}
#poll{position:fixed;left:16px;top:64px;width:300px;padding:12px 14px;border-radius:14px;background:#0f172ae6;border:1px solid #a78bfa;display:none}#poll h4{margin:0 0 8px;font-size:18px}
.bar{display:flex;height:26px;border-radius:8px;overflow:hidden;font-weight:900;font-size:14px}.bar i{display:flex;align-items:center;justify-content:center;font-style:normal;transition:width .4s}
.w{background:#22c55e}.l{background:#ef4444}#pst{margin-top:6px;font-size:13px;opacity:.85}
#comp{position:fixed;transform:translate(-50%,-110%);transition:left .5s cubic-bezier(.2,1.4,.4,1),top .5s cubic-bezier(.2,1.4,.4,1);text-align:center;pointer-events:none}
#comp .e{font-size:64px;filter:drop-shadow(0 4px 8px #0009);animation:bob 1.6s ease-in-out infinite}#comp .s{display:inline-block;margin-top:4px;padding:6px 14px;border-radius:12px;background:#f472b6;font-weight:800;font-size:18px}
#toast{position:fixed;right:20px;top:64px;padding:10px 18px;border-radius:12px;background:linear-gradient(135deg,#f59e0b,#ef4444);font-size:24px;font-weight:900;opacity:0;transform:translateX(40px);transition:all .3s}#toast.on{opacity:1;transform:none}
@keyframes bob{0%,100%{transform:translateY(0)}50%{transform:translateY(-8px)}}</style></head>
<body><div id="tip"></div><div id="poll"><h4 id="pq"></h4><div class="bar"><i class="w" id="pw">승</i><i class="l" id="pl">패</i></div><div id="pst"></div></div>
<div id="comp"><div class="e" id="ce">🙂</div><div class="s" id="cs"></div></div><div id="toast"></div><script>
const $=i=>document.getElementById(i);let ts=-1,tt=0;
new EventSource('/events').onmessage=ev=>{const m=JSON.parse(ev.data);if(m.type!=='game')return;
if(m.tip){$('tip').textContent=m.tip.icon+' '+m.tip.text+(m.tags?'  '+m.tags:'');$('tip').classList.add('on')}else $('tip').classList.remove('on');
const p=m.poll;if(p&&p.phase!=='Idle'){$('poll').style.display='block';$('pq').textContent='🗳 '+p.question;const t=p.win+p.lose,wp=t?Math.round(p.win*100/t):50;
$('pw').style.width=wp+'%';$('pl').style.width=(100-wp)+'%';$('pw').textContent='승 '+wp+'%';$('pl').textContent='패 '+(100-wp)+'%';
$('pst').textContent=p.phase==='Open'?'채팅 !승 / !패 로 참여 · '+t+'표':p.phase==='Locked'?'🔒 마감 · '+t+'표':'🏁 결과: '+(p.result?'승리':'패배')}else $('poll').style.display='none';
const c=m.companion;if(c){$('ce').textContent=c.emote;$('cs').textContent=c.say;$('comp').style.left=(c.x*100)+'vw';$('comp').style.top=(Math.max(.12,c.y)*100)+'vh'}
if(m.toast&&m.toastSeq!==ts){ts=m.toastSeq;$('toast').textContent=m.toast;$('toast').classList.add('on');clearTimeout(tt);tt=setTimeout(()=>$('toast').classList.remove('on'),2500)}};
</script></body></html>
""";
}
