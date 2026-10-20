namespace PromptFlowStudio.Services.Showrunner;

public enum ShowrunnerCue : byte { HeadHit, BodyHit, Splash, Confetti, ChatSpike }

/// <summary>v17.67.0 쇼러너 개입 기록(로그 링 · 무할당).</summary>
public struct ShowrunnerIntervention
{
    public ShowrunnerCue Cue;
    public DropKind Kind;
    public sbyte Avatar;
    public float Surprise, Anger;
    public long AtFrame;
    public int TextLen;
    public bool CloseUp;
}

/// <summary>v17.67.0 쇼러너 출력 결선 — 토크쇼 애드리브 주입 · 표정 · MCR 클로즈업(무할당 · MCR 틱 스레드).</summary>
public interface IShowrunnerOutput
{
    bool InjectAdlib(int avatar, ReadOnlySpan<char> line);
    void Provoke(int avatar, float surprise, float anger);
    void CueCloseUp(int avatar);
    void CueWide();
}

/// <summary>
/// [PromptFlow Studio v17.67.0] AI 자율 쇼러너 애드리브 &amp; 연출 합성기(무할당).
///   ▸ 입력: 스포너 피격 이벤트 · 커맨드 속도(초당 · 2 s 창 평균 대비) — 우선순위 머리 피격 &gt; 파열 &gt; 몸 피격 &gt; 채팅 폭주 &gt; 컨페티.
///   ▸ 판정: 종류 · 부위 · 파열 → 감정(토마토 머리 = 화남 0.8 · 물풍선 = 놀람 1 · 공 = 놀람 0.6 · 컨페티 = 기쁨) · 템플릿 + 시청자 이름 스팬 합성(char 버퍼).
///   ▸ 출력: v17.65 FSM 강제 발언권 애드리브(대본 인터럽트) · v17.58 표정 주입 · v17.66 MCR 피격 아바타 리액션 CU 컷(같은 MCR 프레임).
///   ▸ 쿨다운: 개입 간 <see cref="CooldownFrames"/>(머리 피격은 절반) · 같은 아바타 연속 피격은 표정만 갱신.
/// </summary>
public sealed class AutonomousShowrunnerAdlibSynthesizer
{
    public const int LineCap = 160, LogRing = 32, CooldownFrames = 90, SpikeWindow = 120;
    public const float SpikeRatio = 3f, SpikeFloor = 6f;

    private static readonly string[] HeadTomato = { "앗, 방금 누구야! ", "님 토마토 던지신 거 다 봤어요!", "으악 토마토! ", "님 이건 반칙이죠!" };
    private static readonly string[] HeadBalloon = { "으악 차가워! ", "님 물풍선 실화예요?", "아 머리 다 젖었잖아요! ", "님 각오하세요!" };
    private static readonly string[] HeadBall = { "아야! ", "님 공 조준 실력 무엇?", "깜짝이야! ", "님 축구 선수세요?" };
    private static readonly string[] Body = { "어이쿠, 깜짝이야! ", "님 조준 좋으시네요?", "앗 옷에 묻었어요! ", "님 세탁비 청구합니다!" };
    private static readonly string[] Confetti = { "축하 폭죽 감사합니다! ", "님 최고예요!", "와 꽃가루다! ", "님 덕분에 파티 분위기네요!" };
    private const string Spike = "와 채팅창 난리 났네요! 여러분 잠깐만 진정해 주세요~";
    private const string Anon = "누군가";

    private readonly char[] _line = new char[LineCap];
    private readonly char[] _user = new char[PhysicsSpamPoolingSafetyGuard.UserCap];
    private readonly ShowrunnerIntervention[] _log = new ShowrunnerIntervention[LogRing];
    private readonly char[] _logText = new char[LogRing * LineCap];
    private readonly float[] _rate = new float[SpikeWindow];
    private int _logHead, _logN, _rateHead, _variant;
    private long _frame, _lastIntervention = -10_000, _lastSpikeFrame = -10_000;
    private float _rateSum, _cmdThisFrame;
    private long _spawnFrame;

    public bool Enabled { get; set; } = true;
    public long Interventions { get; private set; }
    public long Suppressed { get; private set; }
    public long CloseUps { get; private set; }
    public long ChatSpikes { get; private set; }
    public float CommandsPerSec { get; private set; }
    public float BaselinePerSec { get; private set; }
    public long LastLatencyFrames { get; private set; }
    public long MaxLatencyFrames { get; private set; }
    public double LastUs { get; private set; }
    public int LogCount => _logN;
    public ref readonly ShowrunnerIntervention Log(int i) => ref _log[(_logHead - 1 - i + 2 * LogRing) % LogRing];
    public ReadOnlySpan<char> LogText(int i) { var k = (_logHead - 1 - i + 2 * LogRing) % LogRing; return _logText.AsSpan(k * LineCap, _log[k].TextLen); }

    public static string CueName(ShowrunnerCue c) => c switch { ShowrunnerCue.HeadHit => "머리 피격", ShowrunnerCue.BodyHit => "몸 피격", ShowrunnerCue.Splash => "파열", ShowrunnerCue.Confetti => "컨페티", _ => "채팅 폭주" };

    public void ResetMetrics() { Interventions = Suppressed = CloseUps = ChatSpikes = 0; MaxLatencyFrames = 0; }

    /// <summary>커맨드 수신 1 건 집계(속도 · 폭주 감지).</summary>
    public void CountCommand() => _cmdThisFrame++;

    // ══════════════════════ 프레임(MCR 틱 · 무할당) ══════════════════════
    public void Step(ViewerInteractivePhysicsSpawnerEngine spawner, IShowrunnerOutput output, int confettiThisFrame, ReadOnlySpan<char> confettiUser)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _frame++; _spawnFrame = spawner.Frame;
        // 커맨드 속도(초당 · 2 s 창) · 기준선(느린 EMA)
        _rateSum += _cmdThisFrame - _rate[_rateHead]; _rate[_rateHead] = _cmdThisFrame; _rateHead = (_rateHead + 1) % SpikeWindow; _cmdThisFrame = 0;
        CommandsPerSec = _rateSum * 60f / SpikeWindow;
        BaselinePerSec += (CommandsPerSec - BaselinePerSec) * (CommandsPerSec > BaselinePerSec ? 0.002f : 0.02f);
        if (!Enabled) { while (spawner.TryTakeHit(out _, _user)) { } LastUs = Us(t0); return; }
        // 피격 이벤트 — 프레임 내 최우선 1 건 선택
        PropHitEvent best = default; var bestScore = -1; var bestUser = 0;
        Span<char> tmp = stackalloc char[PhysicsSpamPoolingSafetyGuard.UserCap];
        while (spawner.TryTakeHit(out var e, tmp))
        {
            var sc = (e.Region == HitRegion.Head ? 30 : 10) + (e.Burst ? 5 : 0) + (int)MathF.Min(e.Speed, 9f);
            if (sc > bestScore) { bestScore = sc; best = e; bestUser = e.UserLen; tmp[..e.UserLen].CopyTo(_user); }
        }
        var cool = _frame - _lastIntervention;
        if (bestScore >= 0)
        {
            var head = best.Region == HitRegion.Head;
            var need = head ? CooldownFrames / 2 : CooldownFrames;
            float surprise, anger;
            (surprise, anger) = best.Kind switch
            {
                DropKind.Tomato => head ? (0.5f, 0.85f) : (0.6f, 0.4f),
                DropKind.WaterBalloon => (1f, head ? 0.35f : 0.1f),
                DropKind.Ball => (0.6f, head ? 0.25f : 0f),
                _ => (0.3f, 0f),
            };
            output.Provoke(best.Avatar, surprise, anger);
            if (cool < need) { Suppressed++; LastUs = Us(t0); return; }
            var tpl = head ? best.Kind switch { DropKind.Tomato => HeadTomato, DropKind.WaterBalloon => HeadBalloon, _ => HeadBall } : Body;
            var n = Compose(tpl, _user.AsSpan(0, bestUser));
            var cue = head ? ShowrunnerCue.HeadHit : best.Burst ? ShowrunnerCue.Splash : ShowrunnerCue.BodyHit;
            output.InjectAdlib(best.Avatar, _line.AsSpan(0, n));
            output.CueCloseUp(best.Avatar);
            CloseUps++;
            Record(cue, best.Kind, best.Avatar, surprise, anger, n, true, best.AtFrame);
            LastUs = Us(t0);
            return;
        }
        if (cool < CooldownFrames) { LastUs = Us(t0); return; }
        // 채팅 폭주 → 호스트 진정 멘트 + 와이드
        if (CommandsPerSec >= MathF.Max(SpikeFloor, BaselinePerSec * SpikeRatio) && _frame - _lastSpikeFrame > 600)
        {
            _lastSpikeFrame = _frame; ChatSpikes++;
            Spike.AsSpan().CopyTo(_line);
            output.Provoke(0, 0.6f, 0f);
            output.InjectAdlib(0, _line.AsSpan(0, Spike.Length));
            output.CueWide();
            Record(ShowrunnerCue.ChatSpike, DropKind.Confetti, 0, 0.6f, 0f, Spike.Length, false, _spawnFrame);
        }
        else if (confettiThisFrame > 0)
        {
            var n = Compose(Confetti, confettiUser);
            output.Provoke(0, 0.3f, 0f);
            output.InjectAdlib(0, _line.AsSpan(0, n));
            Record(ShowrunnerCue.Confetti, DropKind.Confetti, 0, 0.3f, 0f, n, false, _spawnFrame);
        }
        LastUs = Us(t0);
    }

    private static double Us(long t0) => (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>템플릿 쌍(접두 · 접미) 교대 선택 + 시청자 이름(없으면 "누군가") → 내부 버퍼(무할당).</summary>
    private int Compose(string[] tpl, ReadOnlySpan<char> user)
    {
        var v = (_variant++ & 1) * 2;
        var n = 0;
        n = Put(n, tpl[v]);
        n = Put(n, user.IsEmpty ? Anon : user);
        n = Put(n, tpl[v + 1]);
        return n;
    }

    private int Put(int n, ReadOnlySpan<char> s) { var k = Math.Min(s.Length, LineCap - n); s[..k].CopyTo(_line.AsSpan(n)); return n + k; }

    private void Record(ShowrunnerCue cue, DropKind kind, int avatar, float surprise, float anger, int len, bool closeUp, long eventFrame)
    {
        ref var r = ref _log[_logHead];
        r.Cue = cue; r.Kind = kind; r.Avatar = (sbyte)avatar; r.Surprise = surprise; r.Anger = anger; r.AtFrame = _frame; r.TextLen = len; r.CloseUp = closeUp;
        _line.AsSpan(0, len).CopyTo(_logText.AsSpan(_logHead * LineCap));
        _logHead = (_logHead + 1) % LogRing; if (_logN < LogRing) _logN++;
        Interventions++; _lastIntervention = _frame;
        LastLatencyFrames = Math.Max(0, _spawnFrame - eventFrame);
        if (LastLatencyFrames > MaxLatencyFrames) MaxLatencyFrames = LastLatencyFrames;
    }
}
