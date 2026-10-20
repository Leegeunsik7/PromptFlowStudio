using System.Numerics;

namespace PromptFlowStudio.Services.Sports;

public enum MatchEventKind : byte { Kickoff, Pass, Shot, Save, Goal, Tackle, OutOfPlay }

/// <summary>경기 이벤트(값형 · 피치 좌표 m · Value = 패스 수신자/슛 거리 등).</summary>
public readonly record struct MatchEvent(double TimeMs, MatchEventKind Kind, int Player, int Team, float X, float Y, float Value);

/// <summary>
/// [PromptFlow Studio v16.45.0] ⚽ 결정적 축구 경기 시뮬레이터(데모 소스 · 시드 고정).
///   ▸ 피치 105×68m(X = 길이 · Y = 폭 · Z = 높이) · 22명(0~10 홈 → +X 공격 · 11~21 원정) · 포메이션 앵커 + 볼 추종 시프트 · 최근접 압박.
///   ▸ 볼: 드리블 · 패스 · 슛(포물선 · 중력 · 바운드 · 마찰) · 키퍼 선방 · 골/아웃(골킥 · 스로인) · 킥오프 재개.
///   ▸ 관중 함성 dB 모델(위험 지역 · 슛 · 골 흥분 감쇠) → 오디오 노이즈 합성. Step/오디오 경로 무할당.
/// </summary>
public sealed class SportsMatchSimulator
{
    public const float PitchLength = 105f, PitchWidth = 68f, GoalHalfWidth = 3.66f, GoalHeight = 2.44f, Gravity = 9.81f;
    public const int PlayersPerTeam = 11, PlayerCount = 22;

    private static readonly Vector2[] Formation =
    {
        new(4, 34), new(20, 10), new(17, 26), new(17, 42), new(20, 58), new(36, 12), new(33, 28), new(33, 40), new(36, 56), new(48, 27), new(48, 41),
    };

    public static readonly string[] Names =
    {
        "김현우", "박준서", "이도윤", "최민재", "정우진", "강태민", "조성훈", "윤지호", "장하람", "손민혁", "황재원",
        "Silva", "Müller", "Rossi", "Dubois", "García", "Novak", "Jensen", "Costa", "Kane", "Yamada", "Okafor",
    };

    public readonly Vector3[] Players = new Vector3[PlayerCount];
    private readonly Vector3[] _vel = new Vector3[PlayerCount];
    private readonly int[] _score = new int[2];
    private readonly Random _rng;
    private double _decideAt, _restartAt = double.NaN, _noPickupUntil;
    private int _kickoffTeam, _receiver = -1;
    private bool _saveRolled;
    private float _excite;

    public SportsMatchSimulator(int seed = 7)
    {
        _rng = new Random(seed);
        Reset(0, 0);
    }

    public Vector3 Ball;
    public Vector3 BallVel;
    public int Carrier { get; private set; } = -1;
    public int LastTouch { get; private set; } = -1;
    public float CrowdDb { get; private set; } = 72f;
    public float Excitement => _excite;
    public bool InRestart => !double.IsNaN(_restartAt);
    public int ScoreOf(int team) => _score[team & 1];

    public static int TeamOf(int p) => p < PlayersPerTeam ? 0 : 1;
    public static bool IsKeeper(int p) => p == 0 || p == PlayersPerTeam;
    public static float AttackGoalX(int team) => team == 0 ? PitchLength : 0f;

    public static Vector2 Anchor(int p)
    {
        var f = Formation[p % PlayersPerTeam];
        return TeamOf(p) == 0 ? f : new Vector2(PitchLength - f.X, PitchWidth - f.Y);
    }

    public void Reset(int kickoffTeam, double nowMs)
    {
        for (var p = 0; p < PlayerCount; p++) { var a = Anchor(p); Players[p] = new Vector3(a.X, a.Y, 0); _vel[p] = Vector3.Zero; }
        Ball = new Vector3(PitchLength / 2, PitchWidth / 2, 0);
        BallVel = Vector3.Zero;
        var c = kickoffTeam == 0 ? 9 : PlayersPerTeam + 9;
        Players[c] = new Vector3(PitchLength / 2 + (kickoffTeam == 0 ? -0.7f : 0.7f), PitchWidth / 2, 0);
        Carrier = c;
        _receiver = -1;
        LastTouch = c;
        _decideAt = nowMs + 900;
    }

    /// <summary>시뮬레이션 1스텝(dt 초 · 0.1 상한). 반환 = 기록 이벤트 수(ev 용량까지). 무할당.</summary>
    public int Step(double dtSec, double nowMs, Span<MatchEvent> ev)
    {
        var n = 0;
        var dt = (float)Math.Clamp(dtSec, 0, 0.1);
        if (dt <= 0) return 0;
        _excite *= MathF.Exp(-dt / 3.5f);
        if (!double.IsNaN(_restartAt))
        {
            if (nowMs >= _restartAt)
            {
                _restartAt = double.NaN;
                Reset(_kickoffTeam, nowMs);
                n = Push(ev, n, new MatchEvent(nowMs, MatchEventKind.Kickoff, Carrier, _kickoffTeam, Ball.X, Ball.Y, 0));
            }
            else
            {
                BallPhysics(dt);
                Ball.X = Math.Clamp(Ball.X, -2f, PitchLength + 2f);
                UpdateCrowd(dt);
                return n;
            }
        }

        MovePlayers(dt, nowMs);

        if (Carrier >= 0)
        {
            var team = TeamOf(Carrier);
            var dir = team == 0 ? 1f : -1f;
            var cp = Players[Carrier];
            Ball = new Vector3(cp.X + dir * 0.7f, cp.Y, 0);
            BallVel = _vel[Carrier];
            // 온볼 압박: 1m 이내 상대 → 초당 18% 탈취
            var presser = Nearest(1 - team, cp.X, cp.Y, false);
            var pd = Vector2.Distance(new Vector2(cp.X, cp.Y), new Vector2(Players[presser].X, Players[presser].Y));
            if (!IsKeeper(Carrier) && pd < 1f && _rng.NextSingle() < 0.18f * dt)
            {
                n = Push(ev, n, new MatchEvent(nowMs, MatchEventKind.Tackle, presser, 1 - team, cp.X, cp.Y, Carrier));
                GiveBall(presser, nowMs);
            }
            else if (nowMs >= _decideAt)
            {
                var dGoal = MathF.Abs(AttackGoalX(team) - cp.X);
                if (!IsKeeper(Carrier) && pd > 2.5f && dGoal > 26f) _decideAt = nowMs + 250;   // 압박 없음 → 전진 드리블 유지
                else n = Decide(nowMs, ev, n);
            }
        }
        else
        {
            BallPhysics(dt);
            if (Ball.X <= 0 || Ball.X >= PitchLength)
            {
                var inGoal = MathF.Abs(Ball.Y - PitchWidth / 2) < GoalHalfWidth && Ball.Z < GoalHeight;
                if (inGoal)
                {
                    var scorer = Ball.X <= 0 ? 1 : 0;
                    _score[scorer]++;
                    _excite = 1f;
                    _kickoffTeam = 1 - scorer;
                    _restartAt = nowMs + 3500;
                    BallVel *= 0.15f;
                    var who = LastTouch >= 0 && TeamOf(LastTouch) == scorer ? LastTouch : (scorer == 0 ? 9 : PlayersPerTeam + 9);
                    n = Push(ev, n, new MatchEvent(nowMs, MatchEventKind.Goal, who, scorer, Ball.X, Ball.Y, _score[scorer]));
                    UpdateCrowd(dt);
                    return n;
                }
                var gk = Ball.X <= 0 ? 0 : PlayersPerTeam;
                n = Push(ev, n, new MatchEvent(nowMs, MatchEventKind.OutOfPlay, LastTouch, TeamOf(gk), Ball.X, Ball.Y, 0));
                GiveBall(gk, nowMs);
            }
            else if (Ball.Y <= 0 || Ball.Y >= PitchWidth)
            {
                var team = LastTouch >= 0 ? 1 - TeamOf(LastTouch) : 0;
                var p = Nearest(team, Ball.X, Ball.Y, true);
                Players[p] = new Vector3(Ball.X, Math.Clamp(Ball.Y, 0.5f, PitchWidth - 0.5f), 0);
                n = Push(ev, n, new MatchEvent(nowMs, MatchEventKind.OutOfPlay, LastTouch, team, Ball.X, Ball.Y, 0));
                GiveBall(p, nowMs);
            }
            else
            {
                // 키퍼 선방
                for (var t = 0; t < 2; t++)
                {
                    var gk = t == 0 ? 0 : PlayersPerTeam;
                    var toward = t == 0 ? BallVel.X < -8 : BallVel.X > 8;
                    if (!toward || LastTouch < 0 || TeamOf(LastTouch) == t) continue;
                    var d = Vector2.Distance(new Vector2(Ball.X, Ball.Y), new Vector2(Players[gk].X, Players[gk].Y));
                    if (d < 1.7f && Ball.Z < 2.7f && !_saveRolled)
                    {
                        _saveRolled = true;   // 슛당 1회 판정
                        if (_rng.NextSingle() < 0.45f)
                        {
                            _excite = MathF.Min(1, _excite + 0.4f);
                            n = Push(ev, n, new MatchEvent(nowMs, MatchEventKind.Save, gk, t, Ball.X, Ball.Y, 0));
                            GiveBall(gk, nowMs);
                        }
                        break;
                    }
                }
                // 볼 획득
                if (Carrier < 0 && nowMs >= _noPickupUntil && Ball.Z < 1.5f)
                {
                    var best = -1; var bd = 1.2f;
                    for (var p = 0; p < PlayerCount; p++)
                    {
                        var d = Vector2.Distance(new Vector2(Ball.X, Ball.Y), new Vector2(Players[p].X, Players[p].Y));
                        if (d < bd && p != LastTouch && (p == _receiver ? BallVel.Length() < 30f : BallVel.Length() < 14f)) { bd = d; best = p; }
                    }
                    if (best >= 0)
                    {
                        if (LastTouch >= 0 && TeamOf(best) != TeamOf(LastTouch))
                            n = Push(ev, n, new MatchEvent(nowMs, MatchEventKind.Tackle, best, TeamOf(best), Ball.X, Ball.Y, LastTouch));
                        GiveBall(best, nowMs);
                    }
                }
            }
        }
        UpdateCrowd(dt);
        return n;
    }

    private void GiveBall(int p, double nowMs)
    {
        Carrier = p;
        _receiver = -1;
        LastTouch = p;
        BallVel = Vector3.Zero;
        Ball = new Vector3(Players[p].X, Players[p].Y, 0);
        _decideAt = nowMs + 500 + _rng.Next(900);
    }

    private int Decide(double nowMs, Span<MatchEvent> ev, int n)
    {
        var c = Carrier;
        var team = TeamOf(c);
        var gx = AttackGoalX(team);
        var pos = new Vector2(Players[c].X, Players[c].Y);
        var dGoal = Vector2.Distance(pos, new Vector2(gx, PitchWidth / 2));
        if (!IsKeeper(c) && dGoal < 30f && _rng.NextSingle() < 0.3f + (30f - dGoal) / 30f * 0.6f)
        {
            var ty = PitchWidth / 2 + (_rng.NextSingle() * 2 - 1) * (GoalHalfWidth + 0.9f);
            var tz = _rng.NextSingle() * 2.7f;
            var dir = Vector2.Normalize(new Vector2(gx - pos.X, ty - pos.Y));
            const float speed = 24f;
            var tt = dGoal / speed;
            var vz = MathF.Min(9f, (tz + 0.5f * Gravity * tt * tt) / tt);
            BallVel = new Vector3(dir.X * speed, dir.Y * speed, vz);
            Ball = new Vector3(pos.X + dir.X * 0.8f, pos.Y + dir.Y * 0.8f, 0.1f);
            LastTouch = c;
            Carrier = -1;
            _noPickupUntil = nowMs + 350;
            _excite = MathF.Min(1, _excite + 0.35f);
            _saveRolled = false;
            return Push(ev, n, new MatchEvent(nowMs, MatchEventKind.Shot, c, team, pos.X, pos.Y, dGoal));
        }
        // 패스: 후보 4명 중 전진 이득 최대(35m 이내)
        var best = -1; var bestGain = float.NegativeInfinity;
        var start = team == 0 ? 1 : PlayersPerTeam + 1;
        for (var k = 0; k < 4; k++)
        {
            var q = start + _rng.Next(PlayersPerTeam - 1);
            if (q == c) continue;
            var qp = new Vector2(Players[q].X, Players[q].Y);
            var d = Vector2.Distance(pos, qp);
            if (d > 35f || d < 4f) continue;
            var gain = (team == 0 ? qp.X - pos.X : pos.X - qp.X) + _rng.NextSingle() * 8f;
            if (gain > bestGain) { bestGain = gain; best = q; }
        }
        if (best < 0) { _decideAt = nowMs + 400; return n; }
        var to = new Vector2(Players[best].X, Players[best].Y) + new Vector2(team == 0 ? 2f : -2f, 0);
        var dist = Vector2.Distance(pos, to);
        var pv = Vector2.Normalize(to - pos) * Math.Clamp(dist * 0.9f + 5f, 8f, 18f);
        BallVel = new Vector3(pv.X, pv.Y, dist > 25 ? 4f : 0f);
        Ball = new Vector3(pos.X + pv.X * 0.04f, pos.Y + pv.Y * 0.04f, 0.05f);
        LastTouch = c;
        Carrier = -1;
        _noPickupUntil = nowMs + 350;
        _receiver = best;
        return Push(ev, n, new MatchEvent(nowMs, MatchEventKind.Pass, c, team, pos.X, pos.Y, best));
    }

    private void BallPhysics(float dt)
    {
        Ball += BallVel * dt;
        if (Ball.Z > 0 || BallVel.Z > 0) BallVel.Z -= Gravity * dt;
        if (Ball.Z < 0)
        {
            Ball.Z = 0;
            BallVel.Z = -BallVel.Z * 0.45f;
            if (BallVel.Z < 0.6f) BallVel.Z = 0;
        }
        var f = Ball.Z <= 0.01f ? MathF.Exp(-0.9f * dt) : MathF.Exp(-0.08f * dt);
        BallVel.X *= f; BallVel.Y *= f;
    }

    private void MovePlayers(float dt, double nowMs)
    {
        var chase0 = Carrier >= 0 && TeamOf(Carrier) == 0 ? -1 : _receiver >= 0 && TeamOf(_receiver) == 0 ? _receiver : Nearest(0, Ball.X, Ball.Y, false);
        var chase1 = Carrier >= 0 && TeamOf(Carrier) == 1 ? -1 : _receiver >= 0 && TeamOf(_receiver) == 1 ? _receiver : Nearest(1, Ball.X, Ball.Y, false);
        var possTeam = Carrier >= 0 ? TeamOf(Carrier) : -1;
        var sway = (float)(nowMs / 1000.0);
        for (var p = 0; p < PlayerCount; p++)
        {
            var team = TeamOf(p);
            Vector2 target; float vmax;
            if (p == Carrier)
            {
                var gx = AttackGoalX(team);
                target = new Vector2(gx, PitchWidth / 2 + (Players[p].Y - PitchWidth / 2) * 0.7f);
                vmax = IsKeeper(p) ? 1.5f : 6.3f;
            }
            else if (p == chase0 || p == chase1)
            {
                target = new Vector2(Ball.X + BallVel.X * 0.4f, Ball.Y + BallVel.Y * 0.4f);
                vmax = Carrier >= 0 ? 5.9f : 7.8f;   // 온볼 압박은 지연 수비(조키)
            }
            else if (IsKeeper(p))
            {
                target = new Vector2(team == 0 ? 1.5f : PitchLength - 1.5f, Math.Clamp(Ball.Y, PitchWidth / 2 - 3.2f, PitchWidth / 2 + 3.2f));
                vmax = 5f;
            }
            else
            {
                var a = Anchor(p);
                var push = possTeam == team ? (team == 0 ? 7f : -7f) : 0f;
                target = new Vector2(a.X + (Ball.X - PitchLength / 2) * 0.55f + push, a.Y + (Ball.Y - PitchWidth / 2) * 0.25f + MathF.Sin(sway * 0.7f + p) * 2.5f);
                vmax = 5.2f;
            }
            var cur = new Vector2(Players[p].X, Players[p].Y);
            var d = target - cur;
            var len = d.Length();
            var step = MathF.Min(len, vmax * dt);
            var mv = len > 1e-4f ? d / len * step : Vector2.Zero;
            var nx = Math.Clamp(cur.X + mv.X, 0.3f, PitchLength - 0.3f);
            var ny = Math.Clamp(cur.Y + mv.Y, 0.3f, PitchWidth - 0.3f);
            _vel[p] = new Vector3((nx - cur.X) / dt, (ny - cur.Y) / dt, 0);
            Players[p] = new Vector3(nx, ny, 0);
        }
    }

    private int Nearest(int team, float x, float y, bool includeKeeper)
    {
        var s = team == 0 ? 0 : PlayersPerTeam;
        var best = -1; var bd = float.MaxValue;
        for (var p = s; p < s + PlayersPerTeam; p++)
        {
            if (!includeKeeper && IsKeeper(p)) continue;
            var dx = Players[p].X - x; var dy = Players[p].Y - y;
            var d = dx * dx + dy * dy;
            if (d < bd) { bd = d; best = p; }
        }
        return best;
    }

    private void UpdateCrowd(float dt)
    {
        var danger = Danger(Ball);
        var target = 70f + 8f * danger + 24f * _excite;
        CrowdDb += (target - CrowdDb) * (1 - MathF.Exp(-dt / 0.35f));
    }

    /// <summary>위험도 0~1(양 골문 30m 이내 근접).</summary>
    public static float Danger(Vector3 ball)
    {
        var dg = MathF.Min(Vector2.Distance(new Vector2(ball.X, ball.Y), new Vector2(0, PitchWidth / 2)), Vector2.Distance(new Vector2(ball.X, ball.Y), new Vector2(PitchLength, PitchWidth / 2)));
        return Math.Clamp(1f - dg / 30f, 0f, 1f);
    }

    /// <summary>관중 함성 오디오(dB → RMS 균일 노이즈 · 20·log10(rms)+100 보정). 무할당.</summary>
    public void FillCrowdAudio(Span<float> dst)
    {
        var amp = MathF.Pow(10f, (CrowdDb - 100f) / 20f) * 1.7320508f;
        for (var i = 0; i < dst.Length; i++) dst[i] = amp * (_rng.NextSingle() * 2f - 1f);
    }

    private static int Push(Span<MatchEvent> ev, int n, in MatchEvent e)
    {
        if (n < ev.Length) ev[n++] = e;
        return n;
    }
}
