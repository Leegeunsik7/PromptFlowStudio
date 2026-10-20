using System.Diagnostics;
using System.Globalization;
using System.Numerics;

namespace PromptFlowStudio.Services.Sports;

public enum SportsCamKind : byte { Broadcast, Tactical, PlayerTrack, GoalLeft, GoalRight, Aerial, Reverse, Tight, Crowd }

public enum DirectorEventKind : byte { Take, AutoCut, EventCut, ReplayIn, ReplayOut }

/// <summary>디렉터 이벤트(값형 · 링 버퍼).</summary>
public readonly record struct DirectorEvent(double TimeMs, DirectorEventKind Kind, int From, int To, float Score);

/// <summary>카메라 화면 투영 점(UI 9분할 뷰포트 · 정규화 0~1).</summary>
public readonly record struct ScreenDot(float U, float V, float Radius, byte Team, bool Ball);

/// <summary>경기장 카메라(핀홀 · 종류별 자동 조준 · 점수).</summary>
public sealed class SportsCamera
{
    public string Name { get; init; } = "";
    public SportsCamKind Kind { get; init; }
    public Vector3 Position;
    public Vector3 LookAt;
    public float FovDeg = 30f;
    public int FollowPlayer = -1;
    public Vector3 Fwd, Right, Up;
    public float F = 1f;
    public float Score, Framing, Affinity, BallU = -1, BallV = -1;
    public bool BallVisible;
    public double LostSinceMs = double.NaN;
}

/// <summary>
/// [PromptFlow Studio v16.45.0] ⚽ AI 스포츠 멀티캠 자동 추적 &amp; 자율 스위칭 디렉터.
///   ▸ 월드(볼 3D 위치/속도 · 선수 22명) 갱신 → 9대 카메라(메인 · 전술 와이드 · 선수 추적 · 골대 L/R · 스파이더 · 리버스 · 타이트 · 관중) 종류별 자동 조준(평활) · 핀홀 투영.
///   ▸ 샷 점수(무할당): 볼 가시성 × 중앙성 × 프레이밍(선수 화면 높이 이상치) × 종류 친화도(빌드업 · 위험 지역 · 역습 속도 · 추적 선수 볼 소유 · 골 직후 관중).
///   ▸ 자율 PGM 스위칭: 최소 홀드 + 우세 마진(×1.25) · 볼 유실 400ms 즉시 컷 · 슛/골 이벤트 컷(골대 캠) · 수동 TAKE · 리플레이 인/아웃 기록.
///   ▸ 볼 3D 추적 보조: 휘도 블롭 검출 · 두 카메라 광선 삼각측량 · α-β 필터 예측. 소프트웨어 래스터(피치 · 라인 · 골대 · 선수 · 볼) → PGM 합성 → 하이라이트 버퍼 · 분석 오버레이.
///   ▸ 1-Click 생중계: 전용 스레드 30fps(시뮬레이터 → 디렉터 → 분석 → 하이라이트 → 합성). 틱/합성 경로 워밍업 후 무할당.
/// </summary>
public sealed class AiSportsMulticamAutoDirectorEngine : IDisposable
{
    public const int MaxCameras = 9, EventCap = 128, Fps = 30, OutW = 320, OutH = 180, HalfW = OutW / 2, HalfH = OutH / 2;
    public const float SwitchMargin = 1.25f, PlayerHeight = 1.8f, BallRadius = 0.11f;

    private readonly SportsCamera[] _cams = new SportsCamera[MaxCameras];
    private readonly DirectorEvent[] _events = new DirectorEvent[EventCap];
    private readonly Vector3[] _players = new Vector3[SportsMatchSimulator.PlayerCount];
    private readonly object _gate = new(), _frameGate = new();
    private readonly BiometricSportsAnalyticsOverlayEngine? _analytics;
    private readonly AutomatedSportsHighlightReplayEngine? _highlight;
    private readonly byte[] _clean = new byte[OutW * OutH * 3], _half = new byte[HalfW * HalfH * 3], _replayHalf = new byte[HalfW * HalfH * 3], _out = new byte[OutW * OutH * 4];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Vector3 _ball, _ballVel, _ballSmooth;
    private int _count, _pgm = -1, _pvw = -1, _carrier = -1, _evHead, _evCount, _shotSide = -1;
    private double _pgmSince, _lastCut = double.NegativeInfinity, _shotUntil = double.NegativeInfinity, _goalAt = double.NegativeInfinity;
    private long _lastRaise;
    private long _takes, _autoCuts, _eventCuts, _frames, _ticks;
    private bool _replayOnAir;
    private SportsMatchSimulator? _sim;
    private Thread? _live;
    private volatile bool _run;

    // α-β 볼 추적 필터
    private Vector3 _trkPos, _trkVel;
    private double _trkMs = double.NaN;

    public AiSportsMulticamAutoDirectorEngine() : this(null, null) { }

    public AiSportsMulticamAutoDirectorEngine(IServiceProvider sp)
        : this(sp.GetService(typeof(BiometricSportsAnalyticsOverlayEngine)) as BiometricSportsAnalyticsOverlayEngine, sp.GetService(typeof(AutomatedSportsHighlightReplayEngine)) as AutomatedSportsHighlightReplayEngine) { }

    public AiSportsMulticamAutoDirectorEngine(BiometricSportsAnalyticsOverlayEngine? analytics, AutomatedSportsHighlightReplayEngine? highlight)
    {
        _analytics = analytics;
        _highlight = highlight;
    }

    public event Action? OnChange;

    public int CameraCount => _count;
    public int Program => _pgm;
    public int Preview => _pvw;
    public bool AutoDirector { get; set; } = true;
    public double MinHoldMs { get; set; } = 2500;
    public double LostBallMs { get; set; } = 400;
    public long Takes => Interlocked.Read(ref _takes);
    public long AutoCuts => Interlocked.Read(ref _autoCuts);
    public long EventCuts => Interlocked.Read(ref _eventCuts);
    public long Frames => Interlocked.Read(ref _frames);
    public long Ticks => Interlocked.Read(ref _ticks);
    public bool IsLive => _live is not null;
    public bool ReplayOnAir => _replayOnAir;
    public SportsMatchSimulator? Simulator => _sim;
    public Vector3 Ball => _ball;
    public Vector3 BallVelocity => _ballVel;
    public int BallCarrier => _carrier;
    public double NowMs => _clock.Elapsed.TotalMilliseconds;
    public string? LastAction { get; private set; }
    public BiometricSportsAnalyticsOverlayEngine? Analytics => _analytics;
    public AutomatedSportsHighlightReplayEngine? Highlights => _highlight;

    public SportsCamera Camera(int i) => _cams[i];
    public string NameOf(int cam) => (uint)cam < (uint)_count ? _cams[cam].Name : "—";
    public float ScoreOf(int cam) => (uint)cam < (uint)_count ? _cams[cam].Score : 0f;

    public int CopyEvents(Span<DirectorEvent> dst)
    {
        lock (_gate)
        {
            var n = Math.Min(dst.Length, _evCount);
            for (var i = 0; i < n; i++) dst[i] = _events[(_evHead - 1 - i + EventCap) % EventCap];
            return n;
        }
    }

    // ══════════════════════ 카메라 ══════════════════════

    public int AddCamera(string name, SportsCamKind kind, Vector3 position, float fovDeg, int follow = -1)
    {
        lock (_gate)
        {
            if (_count >= MaxCameras) return -1;
            var c = new SportsCamera { Name = name, Kind = kind, Position = position, FovDeg = fovDeg, FollowPlayer = follow, LookAt = new Vector3(SportsMatchSimulator.PitchLength / 2, SportsMatchSimulator.PitchWidth / 2, 0) };
            _cams[_count] = c;
            Basis(c);
            if (_pgm < 0) { _pgm = _count; _pgmSince = NowMs; }
            return _count++;
        }
    }

    /// <summary>표준 9캠 구성.</summary>
    public void ConfigureDefault()
    {
        if (_count > 0) return;
        const float L = SportsMatchSimulator.PitchLength, W = SportsMatchSimulator.PitchWidth;
        AddCamera("CAM 1 메인 중계", SportsCamKind.Broadcast, new Vector3(L / 2, -32, 20), 28);
        AddCamera("CAM 2 전술 와이드", SportsCamKind.Tactical, new Vector3(L / 2, -48, 42), 58);
        AddCamera("CAM 3 선수 추적", SportsCamKind.PlayerTrack, new Vector3(L / 2 - 20, -14, 9), 16, 9);
        AddCamera("CAM 4 골대 좌", SportsCamKind.GoalLeft, new Vector3(-9, W / 2, 4.5f), 48);
        AddCamera("CAM 5 골대 우", SportsCamKind.GoalRight, new Vector3(L + 9, W / 2, 4.5f), 48);
        AddCamera("CAM 6 스파이더", SportsCamKind.Aerial, new Vector3(L / 2, W / 2 - 12, 26), 46);
        AddCamera("CAM 7 리버스", SportsCamKind.Reverse, new Vector3(L / 2, W + 30, 16), 30);
        AddCamera("CAM 8 터치라인 타이트", SportsCamKind.Tight, new Vector3(L / 2, -4, 2.2f), 14);
        AddCamera("CAM 9 관중석", SportsCamKind.Crowd, new Vector3(L / 2, -6, 3), 50);
    }

    private static void Basis(SportsCamera c)
    {
        var f = c.LookAt - c.Position;
        f = f.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(f);
        var r = Vector3.Cross(f, Vector3.UnitZ);
        r = r.LengthSquared() < 1e-6f ? Vector3.UnitY : Vector3.Normalize(r);
        c.Fwd = f; c.Right = r; c.Up = Vector3.Cross(r, f);
        c.F = 1f / MathF.Tan(c.FovDeg * MathF.PI / 360f);
    }

    /// <summary>핀홀 투영 → 정규화 화면 좌표(u,v 0~1 · 16:9) · 깊이. 카메라 뒤면 false. 무할당.</summary>
    public static bool Project(SportsCamera c, Vector3 p, out float u, out float v, out float depth)
    {
        var rel = p - c.Position;
        depth = Vector3.Dot(rel, c.Fwd);
        if (depth <= 0.1f) { u = v = -1; return false; }
        var x = Vector3.Dot(rel, c.Right) / depth * c.F / (16f / 9f);
        var y = Vector3.Dot(rel, c.Up) / depth * c.F;
        u = (x + 1) * 0.5f; v = (1 - y) * 0.5f;
        return true;
    }

    public bool Project(int cam, Vector3 p, out float u, out float v, out float depth)
    {
        if ((uint)cam >= (uint)_count) { u = v = depth = -1; return false; }
        return Project(_cams[cam], p, out u, out v, out depth);
    }

    /// <summary>UI 뷰포트용 투영 점(선수 22 + 볼). 반환 = 기록 수. 무할당.</summary>
    public int ProjectDots(int cam, Span<ScreenDot> dst)
    {
        if ((uint)cam >= (uint)_count) return 0;
        var c = _cams[cam];
        var n = 0;
        lock (_gate)
        {
            for (var p = 0; p < _players.Length && n < dst.Length; p++)
            {
                var feet = _players[p];
                if (!Project(c, feet + new Vector3(0, 0, PlayerHeight * 0.5f), out var u, out var v, out var d)) continue;
                if (u < -0.05f || u > 1.05f || v < -0.05f || v > 1.05f) continue;
                var team = (byte)(SportsMatchSimulator.IsKeeper(p) ? 2 + SportsMatchSimulator.TeamOf(p) : SportsMatchSimulator.TeamOf(p));
                dst[n++] = new ScreenDot(u, v, Math.Clamp(PlayerHeight * 0.5f / d * c.F * 0.5f, 0.004f, 0.2f), team, false);
            }
            if (n < dst.Length && Project(c, _ball, out var bu, out var bv, out var bd) && bu >= 0 && bu <= 1 && bv >= 0 && bv <= 1)
                dst[n++] = new ScreenDot(bu, bv, Math.Clamp(BallRadius / bd * c.F * 0.5f, 0.004f, 0.1f), 9, true);
        }
        return n;
    }

    // ══════════════════════ 월드 · 추적 ══════════════════════

    public void UpdateWorld(ReadOnlySpan<Vector3> players, Vector3 ball, Vector3 ballVel, int carrier, double nowMs)
    {
        lock (_gate)
        {
            players[..Math.Min(players.Length, _players.Length)].CopyTo(_players);
            _ball = ball; _ballVel = ballVel; _carrier = carrier;
        }
        TrackBall(ball, nowMs);
    }

    /// <summary>α-β 필터 관측 갱신(측정 잡음 평활 · 속도 추정). 무할당.</summary>
    public void TrackBall(Vector3 measured, double nowMs)
    {
        if (double.IsNaN(_trkMs)) { _trkPos = measured; _trkVel = Vector3.Zero; _trkMs = nowMs; return; }
        var dt = (float)((nowMs - _trkMs) / 1000.0);
        if (dt <= 0) return;
        var pred = _trkPos + _trkVel * dt;
        var r = measured - pred;
        const float alpha = 0.85f, beta = 0.35f;
        _trkPos = pred + alpha * r;
        _trkVel += beta / dt * r;
        _trkMs = nowMs;
    }

    public Vector3 TrackedBall => _trkPos;
    public Vector3 TrackedVelocity => _trkVel;

    /// <summary>예측 위치(포물선 · 지면 클램프).</summary>
    public Vector3 PredictBall(double aheadSec)
    {
        var t = (float)aheadSec;
        var p = _trkPos + _trkVel * t;
        if (p.Z > 0 || _trkVel.Z != 0) p.Z = _trkPos.Z + _trkVel.Z * t - 0.5f * SportsMatchSimulator.Gravity * t * t;
        if (p.Z < 0) p.Z = 0;
        return p;
    }

    /// <summary>휘도 블롭 검출(임계 이상 밝은 화소 무게중심). 반환 = 화소 수(0 = 미검출). 무할당.</summary>
    public static int DetectBlob(ReadOnlySpan<byte> luma, int w, int h, byte threshold, out float cx, out float cy)
    {
        long sx = 0, sy = 0; var n = 0;
        for (var y = 0; y < h; y++)
        {
            var row = luma.Slice(y * w, w);
            for (var x = 0; x < w; x++)
                if (row[x] >= threshold) { sx += x; sy += y; n++; }
        }
        if (n == 0) { cx = cy = -1; return 0; }
        cx = sx / (float)n; cy = sy / (float)n;
        return n;
    }

    /// <summary>화면 좌표(u,v) → 월드 광선 방향.</summary>
    public static Vector3 RayDir(SportsCamera c, float u, float v)
    {
        var x = (u * 2 - 1) * (16f / 9f) / c.F;
        var y = (1 - v * 2) / c.F;
        return Vector3.Normalize(c.Fwd + c.Right * x + c.Up * y);
    }

    /// <summary>두 광선 최근접점 중점 삼각측량(평행 시 false). 반환 err = 광선 간 거리.</summary>
    public static bool Triangulate(Vector3 o1, Vector3 d1, Vector3 o2, Vector3 d2, out Vector3 point, out float err)
    {
        var w0 = o1 - o2;
        float a = Vector3.Dot(d1, d1), b = Vector3.Dot(d1, d2), c = Vector3.Dot(d2, d2), d = Vector3.Dot(d1, w0), e = Vector3.Dot(d2, w0);
        var den = a * c - b * b;
        if (MathF.Abs(den) < 1e-6f) { point = default; err = float.MaxValue; return false; }
        var s = (b * e - c * d) / den;
        var t = (a * e - b * d) / den;
        var p1 = o1 + d1 * s; var p2 = o2 + d2 * t;
        point = (p1 + p2) * 0.5f;
        err = Vector3.Distance(p1, p2);
        return true;
    }

    // ══════════════════════ 이벤트 · 스위칭 ══════════════════════

    public void OnMatchEvent(in MatchEvent e)
    {
        switch (e.Kind)
        {
            case MatchEventKind.Shot:
                _shotUntil = e.TimeMs + 2500;
                _shotSide = SportsMatchSimulator.AttackGoalX(e.Team) > 1 ? 1 : 0;
                break;
            case MatchEventKind.Goal:
                _goalAt = e.TimeMs;
                _shotSide = e.X > SportsMatchSimulator.PitchLength / 2 ? 1 : 0;
                break;
        }
    }

    /// <summary>카메라 조준 · 점수 · 자율 스위칭(무할당).</summary>
    public void Tick(double nowMs)
    {
        Interlocked.Increment(ref _ticks);
        int cut = -1; var kind = DirectorEventKind.AutoCut; var from = -1; float sc = 0;
        lock (_gate)
        {
            if (_count == 0) return;
            _ballSmooth = Vector3.Lerp(_ballSmooth == default ? _ball : _ballSmooth, _ball, 0.35f);
            var speed = new Vector2(_ballVel.X, _ballVel.Y).Length();
            var danger = SportsMatchSimulator.Danger(_ball);
            var shotLive = nowMs < _shotUntil;
            var goalRecent = nowMs - _goalAt < 6000;
            var best = -1; var second = -1;
            for (var i = 0; i < _count; i++)
            {
                var c = _cams[i];
                Aim(c);
                Basis(c);
                var aff = Affinity(c, speed, danger, shotLive, goalRecent);
                c.Affinity = aff;
                var vis = Project(c, _ball, out var u, out var v, out _) && u >= 0.04f && u <= 0.96f && v >= 0.04f && v <= 0.96f;
                c.BallVisible = vis;
                c.BallU = vis ? u : -1; c.BallV = vis ? v : -1;
                if (vis) c.LostSinceMs = double.NaN; else if (double.IsNaN(c.LostSinceMs)) c.LostSinceMs = nowMs;
                float framing;
                if (Project(c, _ball, out _, out _, out var depth) && depth > 0.1f)
                {
                    var hh = PlayerHeight / depth * c.F * 0.5f;          // 화면 높이 대비 선수 비율
                    var ideal = IdealHeight(c.Kind);
                    var q = (hh - ideal) / ideal;
                    framing = MathF.Exp(-q * q * 0.6f);
                }
                else framing = 0;
                c.Framing = framing;
                float centr = 0;
                if (vis) { var du = u - 0.5f; var dv = v - 0.5f; centr = MathF.Max(0, 1 - MathF.Sqrt(du * du + dv * dv) * 1.6f); }
                c.Score = c.Kind == SportsCamKind.Crowd ? aff : aff * (0.25f + 0.75f * (vis ? 0.5f + 0.5f * centr : 0) * (0.4f + 0.6f * framing));
                if (best < 0 || c.Score > _cams[best].Score) { second = best; best = i; }
                else if (second < 0 || c.Score > _cams[second].Score) second = i;
            }
            _pvw = best != _pgm ? best : second;
            if (!AutoDirector || _replayOnAir || best < 0 || best == _pgm) goto done;
            var held = nowMs - _pgmSince;
            var cur = _cams[_pgm];
            var eventCut = (shotLive || goalRecent) && nowMs - _lastCut > 600 && IsEventCam(_cams[best].Kind, shotLive, goalRecent) && !IsEventCam(cur.Kind, shotLive, goalRecent);
            var lost = cur.Kind != SportsCamKind.Crowd && !double.IsNaN(cur.LostSinceMs) && nowMs - cur.LostSinceMs >= LostBallMs && held >= 600;
            if (eventCut) { cut = best; kind = DirectorEventKind.EventCut; }
            else if (lost || (held >= MinHoldMs && _cams[best].Score > cur.Score * SwitchMargin)) cut = best;
            if (cut >= 0)
            {
                from = _pgm; sc = _cams[cut].Score;
                _pgm = cut; _pgmSince = nowMs; _lastCut = nowMs;
                Record(nowMs, kind, from, cut, sc);
                if (kind == DirectorEventKind.EventCut) Interlocked.Increment(ref _eventCuts); else Interlocked.Increment(ref _autoCuts);
            }
        done:;
        }
        if (cut >= 0)
        {
            LastAction = kind == DirectorEventKind.EventCut ? "⚡ 이벤트 컷 → " + _cams[cut].Name : "🤖 AI 컷 → " + _cams[cut].Name;
            Raise(true);
        }
        else Raise(false);
    }

    private static bool IsEventCam(SportsCamKind k, bool shot, bool goal)
        => goal ? k == SportsCamKind.Crowd || k == SportsCamKind.GoalLeft || k == SportsCamKind.GoalRight : shot && (k == SportsCamKind.GoalLeft || k == SportsCamKind.GoalRight);

    private static float IdealHeight(SportsCamKind k) => k switch
    {
        SportsCamKind.Tactical => 0.035f, SportsCamKind.Broadcast => 0.08f, SportsCamKind.PlayerTrack or SportsCamKind.Tight => 0.3f,
        SportsCamKind.Aerial => 0.06f, SportsCamKind.GoalLeft or SportsCamKind.GoalRight => 0.12f, _ => 0.08f,
    };

    private void Aim(SportsCamera c)
    {
        const float L = SportsMatchSimulator.PitchLength, W = SportsMatchSimulator.PitchWidth;
        Vector3 target;
        switch (c.Kind)
        {
            case SportsCamKind.Tactical: target = new Vector3(_ballSmooth.X * 0.6f + L / 2 * 0.4f, W / 2, 0); break;
            case SportsCamKind.PlayerTrack:
                var fp = (uint)c.FollowPlayer < (uint)_players.Length ? c.FollowPlayer : Math.Max(0, _carrier);
                var pp = _players[fp];
                c.Position = Vector3.Lerp(c.Position, new Vector3(pp.X - 6, -14, 9), 0.05f);
                target = pp + new Vector3(0, 0, 1f);
                break;
            case SportsCamKind.GoalLeft: target = new Vector3(Math.Clamp(_ballSmooth.X, 6, 30), Math.Clamp(_ballSmooth.Y, W / 2 - 14, W / 2 + 14), 0.5f); break;
            case SportsCamKind.GoalRight: target = new Vector3(Math.Clamp(_ballSmooth.X, L - 30, L - 6), Math.Clamp(_ballSmooth.Y, W / 2 - 14, W / 2 + 14), 0.5f); break;
            case SportsCamKind.Aerial:
                c.Position = Vector3.Lerp(c.Position, new Vector3(_ballSmooth.X, _ballSmooth.Y - 14, 26), 0.06f);
                target = _ballSmooth;
                break;
            case SportsCamKind.Tight:
                c.Position = Vector3.Lerp(c.Position, new Vector3(Math.Clamp(_ballSmooth.X, 5, L - 5), -4, 2.2f), 0.08f);
                target = _ballSmooth + new Vector3(0, 0, 0.8f);
                break;
            case SportsCamKind.Crowd: target = new Vector3(L / 2, -30, 8); break;
            default: target = _ballSmooth + new Vector3(0, 0, 0.5f); break;
        }
        c.LookAt = Vector3.Lerp(c.LookAt, target, 0.3f);
    }

    private float Affinity(SportsCamera c, float speed, float danger, bool shot, bool goal)
    {
        const float L = SportsMatchSimulator.PitchLength;
        var nearLeft = _ball.X < L / 2;
        return c.Kind switch
        {
            SportsCamKind.Broadcast => 0.62f,
            SportsCamKind.Tactical => 0.3f + 0.35f * (1 - danger) * (speed < 5 ? 1f : 0.4f),
            SportsCamKind.PlayerTrack => 0.2f + (_carrier >= 0 && (_carrier == c.FollowPlayer || c.FollowPlayer < 0) ? 0.65f : 0f),
            SportsCamKind.GoalLeft => (nearLeft ? 0.15f + 0.75f * danger : 0.05f) + (shot && _shotSide == 0 ? 0.6f : 0) + (goal && _shotSide == 0 ? 0.3f : 0),
            SportsCamKind.GoalRight => (!nearLeft ? 0.15f + 0.75f * danger : 0.05f) + (shot && _shotSide == 1 ? 0.6f : 0) + (goal && _shotSide == 1 ? 0.3f : 0),
            SportsCamKind.Aerial => 0.28f + 0.45f * Math.Clamp((speed - 6) / 12f, 0, 1),
            SportsCamKind.Reverse => 0.18f + 0.2f * danger,
            SportsCamKind.Tight => 0.22f + (_carrier >= 0 && speed > 4 && speed < 9 ? 0.25f : 0),
            SportsCamKind.Crowd => goal ? 1.6f : 0.02f,
            _ => 0.3f,
        };
    }

    public bool Take(int cam)
    {
        string name;
        lock (_gate)
        {
            if ((uint)cam >= (uint)_count || cam == _pgm) return false;
            var now = NowMs;
            Record(now, DirectorEventKind.Take, _pgm, cam, _cams[cam].Score);
            _pgm = cam; _pgmSince = now; _lastCut = now;
            name = _cams[cam].Name;
        }
        Interlocked.Increment(ref _takes);
        LastAction = "✂ TAKE → " + name;
        Raise(true);
        return true;
    }

    private void Record(double t, DirectorEventKind k, int from, int to, float score)
    {
        _events[_evHead] = new DirectorEvent(t, k, from, to, score);
        _evHead = (_evHead + 1) % EventCap;
        if (_evCount < EventCap) _evCount++;
    }

    // ══════════════════════ 소프트웨어 래스터 ══════════════════════

    /// <summary>카메라 시점 렌더(rgb24 · 피치/줄무늬/라인 · 관중석 · 골대 · 선수 · 볼). 무할당.</summary>
    public void RenderCamera(int cam, Span<byte> rgb, int w, int h)
    {
        if ((uint)cam >= (uint)_count) { rgb[..(w * h * 3)].Clear(); return; }
        var c = _cams[cam];
        Vector3 ball; Span<Vector3> pl = stackalloc Vector3[SportsMatchSimulator.PlayerCount];
        lock (_gate) { _players.CopyTo(pl); ball = _ball; }
        const float L = SportsMatchSimulator.PitchLength, W = SportsMatchSimulator.PitchWidth;
        var aspect = 16f / 9f;
        var pix = 2f / (c.F * h);
        for (var py = 0; py < h; py++)
        {
            var ny = 1 - 2 * (py + 0.5f) / h;
            for (var px = 0; px < w; px++)
            {
                var nx = 2 * (px + 0.5f) / w - 1;
                var dir = c.Fwd + c.Right * (nx * aspect / c.F) + c.Up * (ny / c.F);
                var o = (py * w + px) * 3;
                byte r, g, b;
                if (dir.Z >= -1e-4f)
                {
                    var crowd = (px * 7 + py * 13 + (px >> 2) * 5) % 9;
                    r = (byte)(38 + crowd * 6); g = (byte)(42 + crowd * 4); b = (byte)(58 + crowd * 5);
                }
                else
                {
                    var t = -c.Position.Z / dir.Z;
                    var gx = c.Position.X + dir.X * t; var gy = c.Position.Y + dir.Y * t;
                    if (gx < -4 || gx > L + 4 || gy < -4 || gy > W + 4) { r = 46; g = 70; b = 52; }
                    else
                    {
                        var stripe = ((int)MathF.Floor(gx / (L / 12)) & 1) == 0;
                        r = (byte)(stripe ? 46 : 54); g = (byte)(stripe ? 128 : 142); b = (byte)(stripe ? 52 : 58);
                        var lw = 0.09f + t * dir.Length() * pix * 0.9f;
                        if (OnLine(gx, gy, lw)) { r = 235; g = 240; b = 235; }
                    }
                }
                rgb[o] = r; rgb[o + 1] = g; rgb[o + 2] = b;
            }
        }
        // 골대(포스트 · 크로스바)
        for (var s = 0; s < 2; s++)
        {
            var gx = s == 0 ? 0f : L;
            var y0 = W / 2 - SportsMatchSimulator.GoalHalfWidth; var y1 = W / 2 + SportsMatchSimulator.GoalHalfWidth; var z = SportsMatchSimulator.GoalHeight;
            Line3(c, rgb, w, h, new Vector3(gx, y0, 0), new Vector3(gx, y0, z));
            Line3(c, rgb, w, h, new Vector3(gx, y1, 0), new Vector3(gx, y1, z));
            Line3(c, rgb, w, h, new Vector3(gx, y0, z), new Vector3(gx, y1, z));
        }
        // 선수(원근 타원 · 원거리 → 근거리 근사 순서 없음)
        for (var p = 0; p < pl.Length; p++)
        {
            if (!Project(c, pl[p], out var fu, out var fv, out var d) || !Project(c, pl[p] + new Vector3(0, 0, PlayerHeight), out _, out var hv, out _)) continue;
            var hh = (fv - hv) * h * 0.5f;
            if (hh < 0.5f) continue;
            var team = SportsMatchSimulator.TeamOf(p);
            var keeper = SportsMatchSimulator.IsKeeper(p);
            uint col = keeper ? (team == 0 ? 0xF2D21Bu : 0x2BD47Du) : (team == 0 ? 0xDC2832u : 0x2F5BDCu);
            Ellipse(rgb, w, h, fu * w, (fv + hv) * 0.5f * h, MathF.Max(0.8f, hh * 0.32f), hh, col);
            Ellipse(rgb, w, h, fu * w, hv * h + hh * 0.18f, MathF.Max(0.6f, hh * 0.16f), MathF.Max(0.6f, hh * 0.16f), 0xF0C8A0);
        }
        // 볼(+ 그림자)
        if (ball.Z > 0.2f && Project(c, new Vector3(ball.X, ball.Y, 0), out var su, out var sv, out var sd))
            Ellipse(rgb, w, h, su * w, sv * h, MathF.Max(0.8f, BallRadius / sd * c.F * h * 0.5f), MathF.Max(0.5f, BallRadius / sd * c.F * h * 0.25f), 0x1E3A22);
        if (Project(c, ball, out var bu, out var bv, out var bd))
        {
            var br = MathF.Max(1.1f, BallRadius / bd * c.F * h * 0.5f);
            Ellipse(rgb, w, h, bu * w, bv * h, br, br, 0xFFFFFF);
        }
    }

    private static bool OnLine(float x, float y, float lw)
    {
        const float L = SportsMatchSimulator.PitchLength, W = SportsMatchSimulator.PitchWidth;
        if (x < -lw || x > L + lw || y < -lw || y > W + lw) return false;
        if (MathF.Abs(x) < lw || MathF.Abs(x - L) < lw || MathF.Abs(y) < lw || MathF.Abs(y - W) < lw || MathF.Abs(x - L / 2) < lw) return true;
        var dx = x - L / 2; var dy = y - W / 2;
        if (MathF.Abs(MathF.Sqrt(dx * dx + dy * dy) - 9.15f) < lw) return true;
        for (var s = 0; s < 2; s++)
        {
            var lx = s == 0 ? x : L - x;
            if (lx < -lw) continue;
            if (Box(lx, y, 16.5f, 20.16f, lw) || Box(lx, y, 5.5f, 9.16f, lw)) return true;
        }
        return false;
    }

    private static bool Box(float lx, float y, float depth, float half, float lw)
    {
        const float cy = SportsMatchSimulator.PitchWidth / 2;
        var inY = y > cy - half - lw && y < cy + half + lw;
        if (inY && MathF.Abs(lx - depth) < lw) return true;
        return lx < depth + lw && (MathF.Abs(y - (cy - half)) < lw || MathF.Abs(y - (cy + half)) < lw);
    }

    private static void Line3(SportsCamera c, Span<byte> rgb, int w, int h, Vector3 a, Vector3 b)
    {
        if (!Project(c, a, out var au, out var av, out _) || !Project(c, b, out var bu, out var bv, out _)) return;
        float x0 = au * w, y0 = av * h, x1 = bu * w, y1 = bv * h;
        var steps = (int)Math.Clamp(MathF.Max(MathF.Abs(x1 - x0), MathF.Abs(y1 - y0)), 1, 4096);
        for (var i = 0; i <= steps; i++)
        {
            var t = i / (float)steps;
            var x = (int)(x0 + (x1 - x0) * t); var y = (int)(y0 + (y1 - y0) * t);
            if ((uint)x >= (uint)w || (uint)y >= (uint)h) continue;
            var o = (y * w + x) * 3;
            rgb[o] = 250; rgb[o + 1] = 250; rgb[o + 2] = 250;
        }
    }

    private static void Ellipse(Span<byte> rgb, int w, int h, float cx, float cy, float rx, float ry, uint col)
    {
        byte r = (byte)(col >> 16), g = (byte)(col >> 8), b = (byte)col;
        int x0 = Math.Max(0, (int)(cx - rx)), x1 = Math.Min(w - 1, (int)(cx + rx + 1)), y0 = Math.Max(0, (int)(cy - ry)), y1 = Math.Min(h - 1, (int)(cy + ry + 1));
        for (var y = y0; y <= y1; y++)
        {
            var dy = (y + 0.5f - cy) / ry;
            for (var x = x0; x <= x1; x++)
            {
                var dx = (x + 0.5f - cx) / rx;
                if (dx * dx + dy * dy > 1.05f) continue;
                var o = (y * w + x) * 3;
                rgb[o] = r; rgb[o + 1] = g; rgb[o + 2] = b;
            }
        }
    }

    // ══════════════════════ PGM 합성 ══════════════════════

    /// <summary>PGM 합성: 카메라 렌더 → 하이라이트 버퍼(½ 해상도) · 리플레이 인젝션 · 분석 오버레이 → 출력 RGBA. 무할당.</summary>
    public void ComposeProgram(double nowMs)
    {
        RenderCamera(_pgm, _clean, OutW, OutH);
        Downsample2x(_clean, _half, OutW, OutH);
        var replay = false;
        if (_highlight is not null)
        {
            _highlight.PushFrame(_half, nowMs);
            _highlight.ObserveBall(nowMs, _ball);
            replay = _highlight.TryRenderReplayFrame(_replayHalf);
        }
        if (replay != _replayOnAir)
        {
            _replayOnAir = replay;
            lock (_gate) Record(nowMs, replay ? DirectorEventKind.ReplayIn : DirectorEventKind.ReplayOut, _pgm, _pgm, 0);
            LastAction = replay ? "🔁 인스턴트 리플레이 ON AIR" : "▶ 라이브 복귀";
            Raise(true);
        }
        lock (_frameGate)
        {
            if (replay) Upscale2xRgba(_replayHalf, _out, HalfW, HalfH);
            else RgbToRgba(_clean, _out);
            _analytics?.Render(_out, OutW, OutH, OutW * 4, nowMs, replay);
        }
        Interlocked.Increment(ref _frames);
    }

    /// <summary>최신 PGM 출력(rgb24 OutW×OutH) 복사.</summary>
    public void CopyProgramRgb(Span<byte> rgb)
    {
        lock (_frameGate)
            for (int i = 0, j = 0; j < OutW * OutH * 4; i += 3, j += 4) { rgb[i] = _out[j]; rgb[i + 1] = _out[j + 1]; rgb[i + 2] = _out[j + 2]; }
    }

    public static void Downsample2x(ReadOnlySpan<byte> src, Span<byte> dst, int w, int h)
    {
        int hw = w / 2, hh = h / 2;
        for (var y = 0; y < hh; y++)
            for (var x = 0; x < hw; x++)
            {
                int a = ((y * 2) * w + x * 2) * 3, b = a + 3, c = a + w * 3, d = c + 3, o = (y * hw + x) * 3;
                for (var k = 0; k < 3; k++) dst[o + k] = (byte)((src[a + k] + src[b + k] + src[c + k] + src[d + k] + 2) >> 2);
            }
    }

    public static void Upscale2xRgba(ReadOnlySpan<byte> src, Span<byte> dst, int hw, int hh)
    {
        var w = hw * 2;
        for (var y = 0; y < hh * 2; y++)
        {
            var sy = y >> 1;
            for (var x = 0; x < w; x++)
            {
                int s = (sy * hw + (x >> 1)) * 3, o = (y * w + x) * 4;
                dst[o] = src[s]; dst[o + 1] = src[s + 1]; dst[o + 2] = src[s + 2]; dst[o + 3] = 255;
            }
        }
    }

    private static void RgbToRgba(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        for (int i = 0, j = 0; i < src.Length; i += 3, j += 4) { dst[j] = src[i]; dst[j + 1] = src[i + 1]; dst[j + 2] = src[i + 2]; dst[j + 3] = 255; }
    }

    // ══════════════════════ 1-Click 생중계 ══════════════════════

    public void StartLive(int seed = 7)
    {
        if (_live is not null) return;
        ConfigureDefault();
        _sim ??= new SportsMatchSimulator(seed);
        _analytics?.SetNames(SportsMatchSimulator.Names);
        _run = true;
        var th = new Thread(LiveLoop) { IsBackground = true, Name = "PFS.Sports" };
        _live = th;
        th.Start();
        LastAction = "⚽ 1-Click 스포츠 생중계 가동";
        Raise(true);
    }

    public void StopLive()
    {
        var th = Interlocked.Exchange(ref _live, null);
        if (th is null) return;
        _run = false;
        th.Join(1500);
        LastAction = "⏹ 생중계 정지";
        Raise(true);
    }

    /// <summary>시뮬레이터 1프레임 진행(이벤트 분배 → 분석 → 디렉터 틱 → 합성 · 관중 오디오). 무할당.</summary>
    public void StepLive(SportsMatchSimulator sim, double dtSec, double nowMs, Span<MatchEvent> evBuf, Span<float> audio)
    {
        var n = sim.Step(dtSec, nowMs, evBuf);
        UpdateWorld(sim.Players, sim.Ball, sim.BallVel, sim.Carrier, nowMs);
        for (var i = 0; i < n; i++)
        {
            OnMatchEvent(evBuf[i]);
            _analytics?.OnMatchEvent(evBuf[i], sim.Players);
            _highlight?.OnMatchEvent(evBuf[i]);
        }
        _analytics?.Update(nowMs, sim.Players, sim.Ball, sim.Carrier);
        Tick(nowMs);
        sim.FillCrowdAudio(audio);
        _highlight?.PushAudio(audio, nowMs);
        ComposeProgram(nowMs);
    }

    private void LiveLoop()
    {
        var sim = _sim!;
        var ev = new MatchEvent[16];
        var audio = new float[1600];
        var period = 1000.0 / Fps;
        var next = NowMs; var last = NowMs;
        while (_run)
        {
            var now = NowMs;
            try
            {
                StepLive(sim, (now - last) / 1000.0, now, ev, audio);
                last = now;
            }
            catch (Exception ex) { PromptFlowStudio.Program.Log("Sports.Tick", ex); }
            next += period;
            var wait = next - NowMs;
            if (wait < -period * 4) next = NowMs;
            else if (wait > 1) Thread.Sleep((int)wait);
        }
    }

    public static string KindLabel(SportsCamKind k) => k switch
    {
        SportsCamKind.Broadcast => "메인", SportsCamKind.Tactical => "전술", SportsCamKind.PlayerTrack => "추적", SportsCamKind.GoalLeft => "골대L", SportsCamKind.GoalRight => "골대R",
        SportsCamKind.Aerial => "스파이더", SportsCamKind.Reverse => "리버스", SportsCamKind.Tight => "타이트", _ => "관중",
    };

    public static string EventLabel(DirectorEventKind k) => k switch
    {
        DirectorEventKind.Take => "✂ TAKE", DirectorEventKind.AutoCut => "🤖 AI 컷", DirectorEventKind.EventCut => "⚡ 이벤트 컷", DirectorEventKind.ReplayIn => "🔁 리플레이", _ => "▶ 라이브",
    };

    public string Summary() => NameOf(_pgm) + " · 컷 " + (AutoCuts + EventCuts).ToString(CultureInfo.InvariantCulture);

    private void Raise(bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now - Interlocked.Read(ref _lastRaise) < 250) return;
        Interlocked.Exchange(ref _lastRaise, now);
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Sports.OnChange", ex); }
    }

    public void Dispose() => StopLive();
}
