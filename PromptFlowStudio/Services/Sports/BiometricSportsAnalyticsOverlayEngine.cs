using System.Globalization;
using System.Numerics;
using System.Text.Json;
using PromptFlowStudio.Services.Newsroom;

namespace PromptFlowStudio.Services.Sports;

/// <summary>선수 누적 지표(값형 스냅샷).</summary>
public readonly record struct PlayerStat(float SpeedKmh, float TopKmh, float DistanceM, int Sprints, float HeartRate, bool HrExternal, float Xg, float Xa, int Shots, int Passes, int Tackles);

/// <summary>슛 분석(거리 · 골문 시야각 · 블로커 · xG).</summary>
public readonly record struct ShotInfo(double TimeMs, int Player, int Team, float X, float Y, float DistanceM, float AngleDeg, int Blockers, float Xg, int Assist, bool Goal);

/// <summary>
/// [PromptFlow Studio v16.45.0] 📊 바이오메트릭 &amp; 경기 데이터 분석 오버레이 엔진.
///   ▸ 선수 22명 추적 좌표 → 순간 속도(EWMA · km/h) · 최고 속도 · 이동 거리 · 스프린트(25km/h 진입 · 21km/h 해제 히스테리시스) · 볼 점유율.
///   ▸ 심박: 웨어러블 피드(JSON 스키마 검증 · 30~230bpm) 우선 · 5초 공백 시 부하 모델(속도 · 누적 거리 · 상승/하강 시상수) 폴백 · 심박 존 색상.
///   ▸ 슛: 골문 시야각(포스트 벡터 사잇각) · 거리 · 슛 삼각형 내 수비 블로커 → 로지스틱 xG · 직전 6초 패스 → xA 귀속 · 골 판정 마킹.
///   ▸ 오버레이(RGBA 합성): 스코어/xG 바 · 3D 피치 미니맵(선수 · 볼 · 포커스) · 선수 카드(이름 · 속도 · 거리 · 심박 바) · 슛 카드 · REPLAY 배지.
///     숫자는 사전 래스터 글리프 조합 · 이름 마스크 선수별 캐시 → 워밍업 후 Update/Render 무할당.
/// </summary>
public sealed class BiometricSportsAnalyticsOverlayEngine
{
    public const int MaxPlayers = SportsMatchSimulator.PlayerCount, ShotCap = 64;
    public const float SprintOnKmh = 25f, SprintOffKmh = 21f, HrMin = 30f, HrMax = 230f;
    public const double HrFeedTimeoutMs = 5000, AssistWindowMs = 6000;

    private enum Label { Kmh, Km, Bpm, Xg, Deg, M, Replay, Live, Dash, Xa }
    private static readonly string[] LabelText = { "km/h", "km", "bpm", "xG", "°", "m", "REPLAY", "LIVE", "-", "xA" };

    private readonly object _gate = new();
    private readonly Vector3[] _prev = new Vector3[MaxPlayers], _pos = new Vector3[MaxPlayers];
    private readonly float[] _speed = new float[MaxPlayers], _top = new float[MaxPlayers], _dist = new float[MaxPlayers], _hr = new float[MaxPlayers], _xg = new float[MaxPlayers], _xa = new float[MaxPlayers];
    private readonly int[] _sprints = new int[MaxPlayers], _shots = new int[MaxPlayers], _passes = new int[MaxPlayers], _tackles = new int[MaxPlayers];
    private readonly bool[] _sprinting = new bool[MaxPlayers];
    private readonly double[] _hrFeedMs = new double[MaxPlayers];
    private readonly ShotInfo[] _shotRing = new ShotInfo[ShotCap];
    private readonly int[] _goals = new int[2], _lastPasser = { -1, -1 };
    private readonly float[] _teamXg = new float[2];
    private readonly double[] _possMs = new double[2], _lastPassMs = { double.NegativeInfinity, double.NegativeInfinity };
    private readonly TextMask?[] _digits = new TextMask?[11], _labels = new TextMask?[LabelText.Length], _names = new TextMask?[MaxPlayers];
    private string[] _nameText = SportsMatchSimulator.Names;
    private Vector3 _ball;
    private int _carrier = -1, _shotHead, _shotCount, _glyphPx, _focusShown = -1;
    private double _lastMs = double.NaN;
    private bool _hasPrev;
    private long _frames, _updates, _hrSamples, _lastRaise;

    public BiometricSportsAnalyticsOverlayEngine()
    {
        for (var i = 0; i < MaxPlayers; i++) { _hr[i] = 68f; _hrFeedMs[i] = double.NegativeInfinity; }
    }

    public event Action? OnChange;

    public bool ShowScoreBar { get; set; } = true;
    public bool ShowMinimap { get; set; } = true;
    public bool ShowPlayerCard { get; set; } = true;
    public bool ShowShotCard { get; set; } = true;
    /// <summary>포커스 선수(-1 = 볼 소유자 자동).</summary>
    public int FocusPlayer { get; set; } = -1;
    public long Frames => Interlocked.Read(ref _frames);
    public long Updates => Interlocked.Read(ref _updates);
    public long HrSamples => Interlocked.Read(ref _hrSamples);
    public int ShotCount => _shotCount;
    public string? LastAction { get; private set; }
    public int Goals(int team) => _goals[team & 1];
    public float TeamXg(int team) => _teamXg[team & 1];
    public int CurrentFocus => FocusPlayer >= 0 ? FocusPlayer : _carrier >= 0 ? _carrier : _focusShown;

    public float PossessionPct(int team)
    {
        var t = _possMs[0] + _possMs[1];
        return t <= 0 ? 50f : (float)(_possMs[team & 1] / t * 100);
    }

    public string NameOf(int p) => (uint)p < (uint)_nameText.Length ? _nameText[p] : "—";

    public void SetNames(string[] names)
    {
        lock (_gate) { _nameText = names; Array.Clear(_names); }
    }

    public PlayerStat Stat(int p)
    {
        lock (_gate)
            return new PlayerStat(_speed[p], _top[p], _dist[p], _sprints[p], _hr[p], _lastMs - _hrFeedMs[p] < HrFeedTimeoutMs, _xg[p], _xa[p], _shots[p], _passes[p], _tackles[p]);
    }

    public int CopyShots(Span<ShotInfo> dst)
    {
        lock (_gate)
        {
            var n = Math.Min(dst.Length, _shotCount);
            for (var i = 0; i < n; i++) dst[i] = _shotRing[(_shotHead - 1 - i + ShotCap) % ShotCap];
            return n;
        }
    }

    public bool TryGetLastShot(out ShotInfo s)
    {
        lock (_gate)
        {
            if (_shotCount == 0) { s = default; return false; }
            s = _shotRing[(_shotHead - 1 + ShotCap) % ShotCap];
            return true;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_speed); Array.Clear(_top); Array.Clear(_dist); Array.Clear(_xg); Array.Clear(_xa); Array.Clear(_sprints); Array.Clear(_shots); Array.Clear(_passes); Array.Clear(_tackles); Array.Clear(_sprinting);
            Array.Fill(_hr, 68f); Array.Fill(_hrFeedMs, double.NegativeInfinity);
            Array.Clear(_goals); Array.Clear(_teamXg); Array.Clear(_possMs);
            _lastPasser[0] = _lastPasser[1] = -1; _lastPassMs[0] = _lastPassMs[1] = double.NegativeInfinity;
            _shotHead = _shotCount = 0; _hasPrev = false; _lastMs = double.NaN; _carrier = -1;
        }
        LastAction = "♻ 분석 초기화";
        Raise(true);
    }

    // ══════════════════════ 추적 갱신(무할당) ══════════════════════

    public void Update(double nowMs, ReadOnlySpan<Vector3> players, Vector3 ball, int carrier)
    {
        lock (_gate)
        {
            var n = Math.Min(players.Length, MaxPlayers);
            var dt = double.IsNaN(_lastMs) ? 0 : (nowMs - _lastMs) / 1000.0;
            _lastMs = nowMs;
            _ball = ball;
            _carrier = carrier;
            if (carrier >= 0) _focusShown = carrier;
            if (!_hasPrev || dt <= 0 || dt > 1)
            {
                players[..n].CopyTo(_prev);
                players[..n].CopyTo(_pos);
                _hasPrev = true;
                return;
            }
            var fdt = (float)dt;
            if (carrier >= 0) _possMs[SportsMatchSimulator.TeamOf(carrier)] += dt * 1000;
            for (var p = 0; p < n; p++)
            {
                var cur = players[p];
                var dx = cur.X - _prev[p].X; var dy = cur.Y - _prev[p].Y;
                var d = MathF.Sqrt(dx * dx + dy * dy);
                _prev[p] = cur; _pos[p] = cur;
                if (d / fdt > 12.5f) continue;                       // 순간이동(재배치) 제외
                _dist[p] += d;
                var inst = d / fdt * 3.6f;
                var a = 1 - MathF.Exp(-fdt / 0.35f);
                _speed[p] += (inst - _speed[p]) * a;
                if (_speed[p] > _top[p]) _top[p] = _speed[p];
                if (!_sprinting[p] && _speed[p] >= SprintOnKmh) { _sprinting[p] = true; _sprints[p]++; }
                else if (_sprinting[p] && _speed[p] < SprintOffKmh) _sprinting[p] = false;
                if (nowMs - _hrFeedMs[p] >= HrFeedTimeoutMs)
                {
                    var target = HeartRateModel(_speed[p], _dist[p]);
                    var tau = target > _hr[p] ? 6f : 12f;
                    _hr[p] += (target - _hr[p]) * (1 - MathF.Exp(-fdt / tau));
                }
            }
        }
        Interlocked.Increment(ref _updates);
        Raise(false);
    }

    /// <summary>부하 기반 심박 모델(bpm).</summary>
    public static float HeartRateModel(float speedKmh, float distanceM) => Math.Clamp(64f + 4.1f * speedKmh + MathF.Min(22f, distanceM / 1000f * 3f), 55f, 205f);

    public static int HeartZone(float bpm) => bpm < 115 ? 1 : bpm < 140 ? 2 : bpm < 160 ? 3 : bpm < 178 ? 4 : 5;

    public static uint ZoneColor(int zone) => zone switch { 1 => 0x60A5FA, 2 => 0x34D399, 3 => 0xFBBF24, 4 => 0xF97316, _ => 0xEF4444 };

    /// <summary>웨어러블 심박 1건(범위 검증). 반환 = 적용 여부.</summary>
    public bool IngestHeartRate(int player, float bpm, double nowMs)
    {
        if ((uint)player >= MaxPlayers || float.IsNaN(bpm) || bpm < HrMin || bpm > HrMax) return false;
        lock (_gate) { _hr[player] = bpm; _hrFeedMs[player] = nowMs; }
        Interlocked.Increment(ref _hrSamples);
        return true;
    }

    /// <summary>웨어러블 JSON 배열 [{"player":n,"bpm":x}] — 스키마 위반 시 FormatException. 반환 = 적용 수.</summary>
    public int IngestWearableJson(string json, double nowMs)
    {
        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement;
        if (arr.ValueKind == JsonValueKind.Object && arr.TryGetProperty("samples", out var s)) arr = s;
        if (arr.ValueKind != JsonValueKind.Array) throw new FormatException("웨어러블 스키마 불일치(samples 배열 없음)");
        var n = 0;
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("player", out var p) || p.ValueKind != JsonValueKind.Number || !p.TryGetInt32(out var pi) || !e.TryGetProperty("bpm", out var b) || b.ValueKind != JsonValueKind.Number)
                throw new FormatException("웨어러블 샘플 스키마 불일치(player/bpm)");
            if (IngestHeartRate(pi, b.GetSingle(), nowMs)) n++;
        }
        LastAction = "💓 웨어러블 심박 " + n.ToString(CultureInfo.InvariantCulture) + "건";
        Raise(true);
        return n;
    }

    // ══════════════════════ 슛 · xG ══════════════════════

    /// <summary>골문 시야각(rad) — 슛 위치에서 두 포스트 사잇각.</summary>
    public static float ShotAngle(float x, float y, float goalX)
    {
        var a = new Vector2(goalX - x, SportsMatchSimulator.PitchWidth / 2 - SportsMatchSimulator.GoalHalfWidth - y);
        var b = new Vector2(goalX - x, SportsMatchSimulator.PitchWidth / 2 + SportsMatchSimulator.GoalHalfWidth - y);
        var la = a.Length(); var lb = b.Length();
        if (la < 1e-4f || lb < 1e-4f) return MathF.PI;
        return MathF.Acos(Math.Clamp(Vector2.Dot(a, b) / (la * lb), -1f, 1f));
    }

    /// <summary>로지스틱 xG(시야각 · 거리 · 헤더 · 블로커) 0.01~0.97.</summary>
    public static float ExpectedGoals(float distanceM, float angleRad, bool header, int blockers)
    {
        var logit = -1.0f + 2.5f * angleRad - 0.09f * distanceM - (header ? 0.9f : 0f) - 0.35f * blockers;
        return Math.Clamp(1f / (1f + MathF.Exp(-logit)), 0.01f, 0.97f);
    }

    /// <summary>슛 삼각형(슈터 · 두 포스트) 내 상대 선수 수. 무할당.</summary>
    public static int CountBlockers(ReadOnlySpan<Vector3> players, int shooterTeam, float x, float y, float goalX)
    {
        var s = new Vector2(x, y);
        var p1 = new Vector2(goalX, SportsMatchSimulator.PitchWidth / 2 - SportsMatchSimulator.GoalHalfWidth);
        var p2 = new Vector2(goalX, SportsMatchSimulator.PitchWidth / 2 + SportsMatchSimulator.GoalHalfWidth);
        var n = 0;
        for (var i = 0; i < players.Length; i++)
        {
            if (SportsMatchSimulator.TeamOf(i) == shooterTeam) continue;
            var q = new Vector2(players[i].X, players[i].Y);
            float d1 = Cross(q, s, p1), d2 = Cross(q, p1, p2), d3 = Cross(q, p2, s);
            var neg = d1 < 0 || d2 < 0 || d3 < 0; var pos = d1 > 0 || d2 > 0 || d3 > 0;
            if (!(neg && pos)) n++;
        }
        return n;
    }

    private static float Cross(Vector2 p, Vector2 a, Vector2 b) => (p.X - b.X) * (a.Y - b.Y) - (a.X - b.X) * (p.Y - b.Y);

    /// <summary>슛 분석 계산(기록 없음).</summary>
    public static ShotInfo AnalyzeShot(double t, int player, int team, float x, float y, ReadOnlySpan<Vector3> players, bool header = false)
    {
        var gx = SportsMatchSimulator.AttackGoalX(team);
        var ang = ShotAngle(x, y, gx);
        var dist = Vector2.Distance(new Vector2(x, y), new Vector2(gx, SportsMatchSimulator.PitchWidth / 2));
        var bl = CountBlockers(players, team, x, y, gx);
        return new ShotInfo(t, player, team, x, y, dist, ang * 180f / MathF.PI, bl, ExpectedGoals(dist, ang, header, bl), -1, false);
    }

    public void OnMatchEvent(in MatchEvent e, ReadOnlySpan<Vector3> players)
    {
        var team = e.Team & 1;
        string? msg = null;
        lock (_gate)
        {
            switch (e.Kind)
            {
                case MatchEventKind.Pass:
                    if ((uint)e.Player < MaxPlayers) _passes[e.Player]++;
                    _lastPasser[team] = e.Player; _lastPassMs[team] = e.TimeMs;
                    break;
                case MatchEventKind.Tackle:
                    if ((uint)e.Player < MaxPlayers) _tackles[e.Player]++;
                    break;
                case MatchEventKind.Shot:
                    var s = AnalyzeShot(e.TimeMs, e.Player, team, e.X, e.Y, players);
                    var passer = _lastPasser[team];
                    if (passer >= 0 && passer != e.Player && e.TimeMs - _lastPassMs[team] <= AssistWindowMs) { s = s with { Assist = passer }; _xa[passer] += s.Xg; }
                    if ((uint)e.Player < MaxPlayers) { _xg[e.Player] += s.Xg; _shots[e.Player]++; }
                    _teamXg[team] += s.Xg;
                    _shotRing[_shotHead] = s;
                    _shotHead = (_shotHead + 1) % ShotCap;
                    if (_shotCount < ShotCap) _shotCount++;
                    msg = "🎯 슛 xG " + s.Xg.ToString("0.00", CultureInfo.InvariantCulture) + " · " + NameOf(e.Player);
                    break;
                case MatchEventKind.Goal:
                    _goals[team]++;
                    for (var i = 0; i < _shotCount; i++)
                    {
                        var k = (_shotHead - 1 - i + ShotCap) % ShotCap;
                        if (_shotRing[k].Team == team && e.TimeMs - _shotRing[k].TimeMs <= 4000) { _shotRing[k] = _shotRing[k] with { Goal = true }; break; }
                    }
                    msg = "⚽ GOAL · " + NameOf(e.Player);
                    break;
            }
        }
        if (msg is not null) { LastAction = msg; Raise(true); }
    }

    // ══════════════════════ 오버레이 합성 ══════════════════════

    private void EnsureGlyphs(int px)
    {
        if (_glyphPx == px) return;
        for (var i = 0; i < 10; i++) _digits[i] = NewsMotionGraphicsOverlayEngine.Rasterize(((char)('0' + i)).ToString(), px, true);
        _digits[10] = NewsMotionGraphicsOverlayEngine.Rasterize(".", px, true);
        var lp = Math.Max(7, px * 3 / 4);
        for (var i = 0; i < LabelText.Length; i++) _labels[i] = NewsMotionGraphicsOverlayEngine.Rasterize(LabelText[i], i == (int)Label.Replay ? px : lp, true);
        Array.Clear(_names);
        _glyphPx = px;
    }

    private TextMask NameMask(int p)
    {
        var m = _names[p];
        if (m is null) { m = NewsMotionGraphicsOverlayEngine.Rasterize(NameOf(p), Math.Max(7, _glyphPx * 3 / 4), true); _names[p] = m; }
        return m;
    }

    /// <summary>숫자 글리프 합성(소수 자릿수 고정). 반환 = 다음 x. 무할당.</summary>
    public int DrawNumber(Span<byte> d, int w, int h, int stride, float value, int decimals, int x, int y, uint rgb)
    {
        Span<int> dig = stackalloc int[12];
        var scale = decimals switch { 0 => 1, 1 => 10, 2 => 100, _ => 1000 };
        var v = (long)MathF.Round(MathF.Abs(value) * scale);
        var n = 0;
        do { dig[n++] = (int)(v % 10); v /= 10; } while ((v > 0 || n <= decimals) && n < dig.Length);
        for (var i = n - 1; i >= 0; i--)
        {
            var m = _digits[dig[i]]!;
            NewsMotionGraphicsOverlayEngine.DrawMask(d, w, h, stride, m, x, y, rgb, 255, 0);
            x += Math.Max(2, m.Width - 3);
            if (i == decimals && decimals > 0)
            {
                var dot = _digits[10]!;
                NewsMotionGraphicsOverlayEngine.DrawMask(d, w, h, stride, dot, x - 1, y, rgb, 255, 0);
                x += Math.Max(2, dot.Width - 4);
            }
        }
        return x;
    }

    private int DrawLabel(Span<byte> d, int w, int h, int stride, Label l, int x, int y, uint rgb)
    {
        var m = _labels[(int)l]!;
        NewsMotionGraphicsOverlayEngine.DrawMask(d, w, h, stride, m, x, y, rgb, 255, 0);
        return x + m.Width;
    }

    /// <summary>RGBA 프레임 위 분석 그래픽 합성. 워밍업(글리프 래스터) 후 무할당.</summary>
    public void Render(Span<byte> rgba, int w, int h, int stride, double nowMs, bool replay)
    {
        var s = h / 180f;
        var px = Math.Max(8, (int)MathF.Round(11 * s));
        lock (_gate)
        {
            EnsureGlyphs(px);
            var lineH = _digits[0]!.Height;
            int S(float v) => (int)(v * s);

            if (ShowScoreBar)
            {
                int bx = S(6), by = S(6), bw = S(150), bh = Math.Max(lineH + 2, S(16));
                NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, bx, by, bw, bh, 0x0B1220, 215);
                NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, bx, by, S(5), bh, 0xDC2832, 255);
                var x = DrawNumber(rgba, w, h, stride, _goals[0], 0, bx + S(8), by + 1, 0xFFFFFF);
                x = DrawLabel(rgba, w, h, stride, Label.Dash, x + 2, by + 1, 0x94A3B8);
                x = DrawNumber(rgba, w, h, stride, _goals[1], 0, x + 2, by + 1, 0xFFFFFF);
                NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, x + S(3), by, S(5), bh, 0x2F5BDC, 255);
                x = DrawLabel(rgba, w, h, stride, Label.Xg, x + S(14), by + 2, 0xFBBF24);
                x = DrawNumber(rgba, w, h, stride, _teamXg[0], 2, x + 3, by + 1, 0xFDE68A);
                x = DrawLabel(rgba, w, h, stride, Label.Dash, x + 2, by + 1, 0x94A3B8);
                DrawNumber(rgba, w, h, stride, _teamXg[1], 2, x + 2, by + 1, 0xFDE68A);
            }

            if (replay)
            {
                var flash = ((int)(nowMs / 400) & 1) == 0;
                var rm = _labels[(int)Label.Replay]!;
                int rx = w - rm.Width - S(14), ry = S(6);
                NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, rx - S(6), ry, rm.Width + S(12), rm.Height + 2, flash ? 0xFBBF24u : 0xD97706u, 240);
                NewsMotionGraphicsOverlayEngine.DrawMask(rgba, w, h, stride, rm, rx, ry + 1, 0x111111, 255, 0);
            }
            else
            {
                var lm = _labels[(int)Label.Live]!;
                int lx = w - lm.Width - S(12), ly = S(6);
                NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, lx - S(8), ly, lm.Width + S(12), lm.Height + 2, 0xDC2626, 230);
                if (((int)(nowMs / 600) & 1) == 0) NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, lx - S(5), ly + lm.Height / 2 - 1, 3, 3, 0xFFFFFF, 255);
                NewsMotionGraphicsOverlayEngine.DrawMask(rgba, w, h, stride, lm, lx, ly + 1, 0xFFFFFF, 255, 0);
            }

            if (ShowMinimap)
            {
                int mw = S(84), mh = S(54), mx = w - mw - S(6), my = h - mh - S(6);
                NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, mx - 2, my - 2, mw + 4, mh + 4, 0x0B1220, 200);
                NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, mx, my, mw, mh, 0x2E7D32, 210);
                NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, mx + mw / 2, my, 1, mh, 0xFFFFFF, 150);
                NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, mx, my + mh / 2 - S(8), Math.Max(1, S(6)), S(16), 0xFFFFFF, 90);
                NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, mx + mw - Math.Max(1, S(6)), my + mh / 2 - S(8), Math.Max(1, S(6)), S(16), 0xFFFFFF, 90);
                var focus = CurrentFocus;
                var dot = Math.Max(2, S(3));
                for (var p = 0; p < MaxPlayers; p++)
                {
                    var q = _pos[p];
                    var x = mx + (int)(q.X / SportsMatchSimulator.PitchLength * mw) - dot / 2;
                    var y = my + (int)(q.Y / SportsMatchSimulator.PitchWidth * mh) - dot / 2;
                    if (p == focus) NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, x - 1, y - 1, dot + 2, dot + 2, 0xFDE047, 255);
                    NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, x, y, dot, dot, SportsMatchSimulator.TeamOf(p) == 0 ? 0xDC2832u : 0x2F5BDCu, 255);
                }
                var bxm = mx + (int)(_ball.X / SportsMatchSimulator.PitchLength * mw) - 1;
                var bym = my + (int)(_ball.Y / SportsMatchSimulator.PitchWidth * mh) - 1;
                NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, bxm, bym, Math.Max(2, dot - 1), Math.Max(2, dot - 1), 0xFFFFFF, 255);
            }

            var fp = CurrentFocus;
            if (ShowPlayerCard && (uint)fp < MaxPlayers)
            {
                var nm = NameMask(fp);
                int cw = S(126), ch = nm.Height + lineH * 2 + S(8), cx = S(6), cy = h - ch - S(6);
                NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, cx, cy, cw, ch, 0x0B1220, 215);
                NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, cx, cy, S(3), ch, SportsMatchSimulator.TeamOf(fp) == 0 ? 0xDC2832u : 0x2F5BDCu, 255);
                NewsMotionGraphicsOverlayEngine.DrawMask(rgba, w, h, stride, nm, cx + S(7), cy + 2, 0xFFFFFF, 255, 0);
                var ry = cy + nm.Height + 2;
                var x = DrawNumber(rgba, w, h, stride, _speed[fp], 1, cx + S(7), ry, 0x38BDF8);
                x = DrawLabel(rgba, w, h, stride, Label.Kmh, x + 2, ry + 2, 0x94A3B8);
                x = DrawNumber(rgba, w, h, stride, _dist[fp] / 1000f, 2, x + S(8), ry, 0xE2E8F0);
                DrawLabel(rgba, w, h, stride, Label.Km, x + 2, ry + 2, 0x94A3B8);
                var hy = ry + lineH;
                var zone = HeartZone(_hr[fp]);
                var zc = ZoneColor(zone);
                x = DrawNumber(rgba, w, h, stride, _hr[fp], 0, cx + S(7), hy, zc);
                x = DrawLabel(rgba, w, h, stride, Label.Bpm, x + 2, hy + 2, 0x94A3B8);
                var barX = x + S(6); var barW = Math.Max(4, cx + cw - S(6) - barX);
                NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, barX, hy + lineH / 3, barW, Math.Max(2, lineH / 3), 0x1E293B, 255);
                NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, barX, hy + lineH / 3, (int)(barW * Math.Clamp((_hr[fp] - 60f) / 140f, 0f, 1f)), Math.Max(2, lineH / 3), zc, 255);
            }

            if (ShowShotCard && _shotCount > 0)
            {
                var sh = _shotRing[(_shotHead - 1 + ShotCap) % ShotCap];
                var age = nowMs - sh.TimeMs;
                if (age >= 0 && age < 5000)
                {
                    int cw = S(118), ch = lineH + S(6), cx = (w - cw) / 2, cy = S(6);
                    NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, cx, cy, cw, ch, sh.Goal ? 0x14532Du : 0x0B1220u, 225);
                    var x = DrawLabel(rgba, w, h, stride, Label.Xg, cx + S(5), cy + S(4), 0xFBBF24);
                    x = DrawNumber(rgba, w, h, stride, sh.Xg, 2, x + 3, cy + S(3), 0xFFFFFF);
                    x = DrawNumber(rgba, w, h, stride, sh.AngleDeg, 0, x + S(8), cy + S(3), 0xE2E8F0);
                    x = DrawLabel(rgba, w, h, stride, Label.Deg, x + 1, cy + S(3), 0x94A3B8);
                    x = DrawNumber(rgba, w, h, stride, sh.DistanceM, 1, x + S(6), cy + S(3), 0xE2E8F0);
                    DrawLabel(rgba, w, h, stride, Label.M, x + 1, cy + S(4), 0x94A3B8);
                }
            }
        }
        Interlocked.Increment(ref _frames);
    }

    private void Raise(bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now - Interlocked.Read(ref _lastRaise) < 250) return;
        Interlocked.Exchange(ref _lastRaise, now);
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("SportsStats.OnChange", ex); }
    }
}
