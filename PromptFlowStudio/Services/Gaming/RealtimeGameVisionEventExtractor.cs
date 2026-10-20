using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PromptFlowStudio.Services.Gaming;

public enum GameGenre { LoL, Valorant, Fps, Rpg }

public enum GameEventKind : byte { None, MatchStart, Kill, MultiKill, Clutch, Assist, Death, LowHealth, Danger, Victory, Defeat }

public enum BarColor : byte { Green, Red, White }

public enum CaptureSource : byte { Screen, Demo }

/// <summary>게임 이벤트(값 형식 · 캡처 스레드에서 무할당 발행).</summary>
public readonly record struct GameEvent(GameEventKind Kind, long AtMs, float Hp, short Kills, short Deaths, short Assists, byte Streak, float Confidence);

/// <summary>정규화 ROI(0~1).</summary>
public readonly record struct RoiF(float X, float Y, float W, float H)
{
    public void ToPixels(int fw, int fh, out int x, out int y, out int w, out int h)
    {
        x = Math.Clamp((int)(X * fw), 0, Math.Max(0, fw - 1));
        y = Math.Clamp((int)(Y * fh), 0, Math.Max(0, fh - 1));
        w = Math.Clamp((int)(W * fw), 1, fw - x);
        h = Math.Clamp((int)(H * fh), 1, fh - y);
    }
}

/// <summary>게임 종목별 HUD 레이아웃(체력바 · K/D/A · 미니맵 · 승패 배너).</summary>
public sealed record GameProfile(GameGenre Genre, string Label, RoiF Health, BarColor HealthColor, RoiF Kda, RoiF Minimap, RoiF Banner);

/// <summary>현재 게임 상태 스냅샷(값 형식).</summary>
public readonly record struct GameStateSnapshot(float Hp, short Kills, short Deaths, short Assists, byte Streak, byte DeathStreak, float Danger, GameEventKind Phase, long Frames, double Fps, double AnalyzeMs);

/// <summary>데모/테스트용 합성 HUD 상태(Banner 0=없음 · 1=승리 · 2=패배).</summary>
public readonly record struct DemoHudState(float Hp, short Kills, short Deaths, short Assists, byte Banner, byte Enemies);

public sealed record CaptureOptions(CaptureSource Source = CaptureSource.Demo, GameGenre Genre = GameGenre.LoL, int X = 0, int Y = 0, int Width = 0, int Height = 0, int TargetFps = 60, double Sensitivity = 0.5);

/// <summary>
/// [PromptFlow Studio v16.39.0] 🎮 실시간 게임 화면 AI 비전 &amp; 이벤트 추출기.
///   ▸ 캡처: Windows GDI BitBlt → DIB 섹션(BGRA top-down) → 재사용 버퍼(프레임당 무할당 · 영역 지정 · 60FPS 전용 스레드 · Stopwatch 데드라인).
///     ※ DXGI Desktop Duplication(D3D11 COM)은 미구현 — 전체화면 독점 모드 게임은 검은 화면 가능(테두리 없는 창 모드 권장). 모바일/비Windows 는 데모 소스만.
///   ▸ 비전: 종목별 ROI 프로필 · 체력바 채움 비율(3행 샘플) · 5x7 비트맵 템플릿 OCR(열 투영 분할 → 고정 피치 셀 샘플 → 해밍 최근접) → "K/D/A" ·
///     미니맵 적 핑(빨강 밀도) · 승패 배너(금/빨강 커버리지 3연속 확정 · 래치).
///   ▸ 이벤트: OCR 2프레임 안정화 → Kill/MultiKill(10초 창)/Clutch(체력 25%↓ 킬)/Assist/Death/LowHealth(히스테리시스)/Danger/Victory/Defeat/MatchStart(K/D/A 리셋).
///   ▸ Analyze 핫패스 무할당(스크래치 배열 · stackalloc · 값형 이벤트 · 링버퍼 64) · 콜백 예외 격리.
/// </summary>
public sealed class RealtimeGameVisionEventExtractor : IDisposable
{
    public const int HistoryCap = 64, DemoWidth = 640, DemoHeight = 360;
    public const long MultiKillWindowMs = 10_000;

    public static readonly GameProfile[] Profiles =
    {
        new(GameGenre.LoL, "League of Legends", new(0.33f, 0.93f, 0.30f, 0.025f), BarColor.Green, new(0.78f, 0.01f, 0.16f, 0.045f), new(0.84f, 0.73f, 0.155f, 0.26f), new(0.28f, 0.34f, 0.44f, 0.20f)),
        new(GameGenre.Valorant, "VALORANT", new(0.30f, 0.93f, 0.14f, 0.025f), BarColor.White, new(0.42f, 0.01f, 0.16f, 0.045f), new(0.01f, 0.03f, 0.20f, 0.32f), new(0.28f, 0.36f, 0.44f, 0.18f)),
        new(GameGenre.Fps, "FPS (범용)", new(0.03f, 0.92f, 0.20f, 0.025f), BarColor.Green, new(0.80f, 0.02f, 0.16f, 0.045f), new(0.80f, 0.70f, 0.18f, 0.26f), new(0.28f, 0.36f, 0.44f, 0.18f)),
        new(GameGenre.Rpg, "RPG (범용)", new(0.02f, 0.04f, 0.24f, 0.025f), BarColor.Red, new(0.80f, 0.02f, 0.16f, 0.045f), new(0.80f, 0.72f, 0.18f, 0.25f), new(0.28f, 0.36f, 0.44f, 0.18f)),
    };

    public static GameProfile ProfileOf(GameGenre g) => Profiles[(int)g];

    public static string GenreLabel(GameGenre g) => ProfileOf(g).Label;

    public static string KindLabel(GameEventKind k) => k switch
    {
        GameEventKind.MatchStart => "🏁 매치 시작", GameEventKind.Kill => "🗡 킬", GameEventKind.MultiKill => "🔥 멀티킬", GameEventKind.Clutch => "💥 클러치",
        GameEventKind.Assist => "🤝 어시스트", GameEventKind.Death => "💀 데스", GameEventKind.LowHealth => "🩸 체력 위험", GameEventKind.Danger => "⚠ 적 다수",
        GameEventKind.Victory => "🏆 승리", GameEventKind.Defeat => "🥀 패배", _ => "—",
    };

    // ── 5x7 비트맵 폰트(0-9, '/') · 행당 5비트(MSB=좌) ──────────────────────
    private static readonly byte[] Glyphs =
    {
        0x0E,0x11,0x13,0x15,0x19,0x11,0x0E, 0x04,0x0C,0x04,0x04,0x04,0x04,0x0E, 0x0E,0x11,0x01,0x02,0x04,0x08,0x1F, 0x1F,0x02,0x04,0x02,0x01,0x11,0x0E,
        0x02,0x06,0x0A,0x12,0x1F,0x02,0x02, 0x1F,0x10,0x1E,0x01,0x01,0x11,0x0E, 0x06,0x08,0x10,0x1E,0x11,0x11,0x0E, 0x1F,0x01,0x02,0x04,0x08,0x08,0x08,
        0x0E,0x11,0x11,0x0E,0x11,0x11,0x0E, 0x0E,0x11,0x11,0x0F,0x01,0x02,0x0C, 0x01,0x02,0x02,0x04,0x08,0x08,0x10,
    };
    private const string GlyphChars = "0123456789/";

    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly GameEvent[] _history = new GameEvent[HistoryCap];
    private int[] _colInk = new int[256];
    private GameProfile _profile = Profiles[0];
    private double _sens = 0.5;
    private int _histPos, _histCount;
    private long _frames, _events;

    // 상태
    private float _hp = 1f, _danger;
    private short _k = -1, _d = -1, _a = -1, _candK = -1, _candD = -1, _candA = -1;
    private int _candHits, _bannerHits, _bannerKind;
    private byte _streak, _deathStreak;
    private long _lastKillMs = -MultiKillWindowMs * 2;
    private bool _lowLatched, _dangerLatched, _ended;
    private GameEventKind _phase = GameEventKind.None;
    private double _analyzeMs, _fps;

    // 캡처 스레드
    private Thread? _thread;
    private volatile bool _run;
    private byte[] _frame = Array.Empty<byte>();
    private CaptureOptions _opts = new();

    /// <summary>게임 이벤트(캡처 스레드) — 구독자별 예외 격리 · 호출 목록 캐시(발행 시 무할당).</summary>
    public event Action<GameEvent>? OnGameEvent
    {
        add { lock (_subGate) { _onGameEvent += value; _subs = SnapshotSubs(_onGameEvent); } }
        remove { lock (_subGate) { _onGameEvent -= value; _subs = SnapshotSubs(_onGameEvent); } }
    }
    private readonly object _subGate = new();
    private Action<GameEvent>? _onGameEvent;
    private Action<GameEvent>[] _subs = Array.Empty<Action<GameEvent>>();

    private static Action<GameEvent>[] SnapshotSubs(Action<GameEvent>? d)
    {
        if (d is null) return Array.Empty<Action<GameEvent>>();
        var list = d.GetInvocationList();
        var arr = new Action<GameEvent>[list.Length];
        for (var i = 0; i < list.Length; i++) arr[i] = (Action<GameEvent>)list[i];
        return arr;
    }
    public event Action? OnChange;

    public GameProfile Profile => _profile;
    public double Sensitivity { get => _sens; set => _sens = Math.Clamp(value, 0, 1); }
    public long Frames => Interlocked.Read(ref _frames);
    public long Events => Interlocked.Read(ref _events);
    public long NowMs => _clock.ElapsedMilliseconds;
    public bool IsRunning => _thread is not null;
    public CaptureOptions Options => _opts;
    public string? LastAction { get; private set; }
    public static bool ScreenCaptureSupported => OperatingSystem.IsWindows();

    public void SetProfile(GameGenre g) { lock (_gate) { _profile = ProfileOf(g); ResetState(); } }

    public GameStateSnapshot Snapshot()
    {
        lock (_gate) return new GameStateSnapshot(_hp, Math.Max(_k, (short)0), Math.Max(_d, (short)0), Math.Max(_a, (short)0), _streak, _deathStreak, _danger, _phase, _frames, _fps, _analyzeMs);
    }

    /// <summary>최근 이벤트(최신 먼저) 복사 — 반환: 복사 수.</summary>
    public int CopyRecent(Span<GameEvent> dst)
    {
        lock (_gate)
        {
            var n = Math.Min(dst.Length, _histCount);
            for (var i = 0; i < n; i++) dst[i] = _history[(_histPos - 1 - i + HistoryCap) % HistoryCap];
            return n;
        }
    }

    public void ResetState()
    {
        lock (_gate)
        {
            _hp = 1f; _danger = 0; _k = _d = _a = _candK = _candD = _candA = -1; _candHits = _bannerHits = _bannerKind = 0;
            _streak = _deathStreak = 0; _lastKillMs = -MultiKillWindowMs * 2; _lowLatched = _dangerLatched = _ended = false; _phase = GameEventKind.None;
        }
    }

    // ── 비전 분석(핫패스 · 무할당) ───────────────────────────────────────────

    /// <summary>BGRA 프레임 1장 분석 → 발행 이벤트 수.</summary>
    public int Analyze(ReadOnlySpan<byte> bgra, int width, int height, int stride, long nowMs)
    {
        if (width < 16 || height < 16 || stride < width * 4 || bgra.Length < stride * (height - 1) + width * 4) return 0;
        var t0 = Stopwatch.GetTimestamp();
        Span<GameEvent> pending = stackalloc GameEvent[6];
        var np = 0;
        lock (_gate)
        {
            var p = _profile;
            _frames++;
            p.Health.ToPixels(width, height, out var hx, out var hy, out var hw, out var hh);
            var hp = MeasureBar(bgra, stride, hx, hy, hw, hh, p.HealthColor);
            p.Minimap.ToPixels(width, height, out var mx, out var my, out var mw, out var mh);
            _danger = Coverage(bgra, stride, mx, my, mw, mh, 2, 2) * 20f;
            if (_danger > 1f) _danger = 1f;
            _hp = hp;

            // 체력 위험(히스테리시스 0.25/0.35)
            var lowTh = 0.20f + (float)_sens * 0.10f;
            if (!_lowLatched && hp > 0.01f && hp < lowTh) { _lowLatched = true; Push(pending, ref np, GameEventKind.LowHealth, nowMs, 1f); }
            else if (_lowLatched && hp > lowTh + 0.10f) _lowLatched = false;

            // 적 다수(미니맵)
            var dTh = 0.75f - (float)_sens * 0.35f;
            if (!_dangerLatched && _danger >= dTh) { _dangerLatched = true; Push(pending, ref np, GameEventKind.Danger, nowMs, _danger); }
            else if (_dangerLatched && _danger < dTh * 0.5f) _dangerLatched = false;

            // K/D/A OCR
            p.Kda.ToPixels(width, height, out var kx, out var ky, out var kw, out var kh);
            if (ReadKda(bgra, stride, kx, ky, kw, kh, out var k, out var d, out var a, out var conf))
            {
                if (k == _candK && d == _candD && a == _candA) _candHits++;
                else { _candK = k; _candD = d; _candA = a; _candHits = 1; }
                if (_candHits == 2) Commit(pending, ref np, k, d, a, nowMs, conf);
            }

            // 승패 배너
            p.Banner.ToPixels(width, height, out var bx, out var by, out var bw, out var bh);
            var gold = Coverage(bgra, stride, bx, by, bw, bh, 1, 4);
            var red = Coverage(bgra, stride, bx, by, bw, bh, 2, 4);
            var bTh = 0.45f - (float)_sens * 0.20f;
            var kind = gold >= bTh ? 1 : red >= bTh ? 2 : 0;
            if (kind != 0 && kind == _bannerKind) _bannerHits++;
            else { _bannerKind = kind; _bannerHits = kind == 0 ? 0 : 1; }
            if (_bannerHits == 3 && !_ended)
            {
                _ended = true;
                _phase = kind == 1 ? GameEventKind.Victory : GameEventKind.Defeat;
                Push(pending, ref np, _phase, nowMs, kind == 1 ? gold : red);
            }

            for (var i = 0; i < np; i++)
            {
                _history[_histPos] = pending[i];
                _histPos = (_histPos + 1) % HistoryCap;
                if (_histCount < HistoryCap) _histCount++;
            }
            _events += np;
            _analyzeMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
        for (var i = 0; i < np; i++)
        {
            var subs = Volatile.Read(ref _subs);
            for (var j = 0; j < subs.Length; j++)
            {
                try { subs[j](pending[i]); } catch (Exception ex) { Program.Log("GameVision.Event", ex); }
            }
        }
        return np;
    }

    private void Push(Span<GameEvent> pending, ref int np, GameEventKind kind, long nowMs, float conf)
    {
        if (np >= pending.Length) return;
        pending[np++] = new GameEvent(kind, nowMs, _hp, Math.Max(_k, (short)0), Math.Max(_d, (short)0), Math.Max(_a, (short)0), _streak, conf);
    }

    private void Commit(Span<GameEvent> pending, ref int np, short k, short d, short a, long nowMs, float conf)
    {
        if (_k < 0) { _k = k; _d = d; _a = a; if (k == 0 && d == 0 && a == 0) { _phase = GameEventKind.MatchStart; Push(pending, ref np, GameEventKind.MatchStart, nowMs, conf); } return; }
        if (k == _k && d == _d && a == _a) return;
        if (k < _k || d < _d || a < _a)
        {
            // 새 매치(리셋)
            _k = k; _d = d; _a = a; _streak = _deathStreak = 0; _ended = false; _bannerHits = 0; _lowLatched = false;
            _phase = GameEventKind.MatchStart;
            Push(pending, ref np, GameEventKind.MatchStart, nowMs, conf);
            return;
        }
        if (k - _k > 5 || d - _d > 5 || a - _a > 5) { _k = k; _d = d; _a = a; return; } // OCR 튐 방어
        if (k > _k)
        {
            var gained = k - _k;
            _streak = (byte)Math.Min(255, (nowMs - _lastKillMs <= MultiKillWindowMs ? _streak : 0) + gained);
            _lastKillMs = nowMs; _deathStreak = 0; _k = k;
            var kind = _hp > 0.01f && _hp < 0.25f + (float)_sens * 0.05f ? GameEventKind.Clutch : _streak >= 2 ? GameEventKind.MultiKill : GameEventKind.Kill;
            _phase = kind;
            Push(pending, ref np, kind, nowMs, conf);
        }
        if (d > _d)
        {
            _d = d; _streak = 0; _deathStreak = (byte)Math.Min(255, _deathStreak + 1);
            _phase = GameEventKind.Death;
            Push(pending, ref np, GameEventKind.Death, nowMs, conf);
        }
        if (a > _a)
        {
            _a = a;
            Push(pending, ref np, GameEventKind.Assist, nowMs, conf);
        }
    }

    /// <summary>체력바 채움 비율(ROI 25/50/75% 3행 평균).</summary>
    public static float MeasureBar(ReadOnlySpan<byte> f, int stride, int x, int y, int w, int h, BarColor color)
    {
        var fill = 0;
        for (var r = 1; r <= 3; r++)
        {
            var row = f.Slice((y + h * r / 4) * stride + x * 4, w * 4);
            for (var i = 0; i < row.Length; i += 4)
            {
                int b = row[i], g = row[i + 1], rr = row[i + 2];
                var hit = color switch
                {
                    BarColor.Green => g > 120 && g > rr + 40 && g > b + 40,
                    BarColor.Red => rr > 130 && rr > g + 60 && rr > b + 60,
                    _ => rr > 200 && g > 200 && b > 200,
                };
                if (hit) fill++;
            }
        }
        return fill / (3f * w);
    }

    /// <summary>색 커버리지(kind 1=금색 · 2=빨강) — step 픽셀 간격 샘플.</summary>
    public static float Coverage(ReadOnlySpan<byte> f, int stride, int x, int y, int w, int h, int kind, int step)
    {
        int hit = 0, total = 0;
        for (var yy = y; yy < y + h; yy += step)
        {
            var row = f.Slice(yy * stride + x * 4, w * 4);
            for (var i = 0; i < row.Length; i += 4 * step)
            {
                int b = row[i], g = row[i + 1], r = row[i + 2];
                total++;
                if (kind == 1 ? r > 190 && g > 140 && b < 120 && r > b + 90 : r > 170 && g < 90 && b < 90) hit++;
            }
        }
        return total == 0 ? 0 : hit / (float)total;
    }

    /// <summary>K/D/A 템플릿 OCR(밝은 글자 · 무할당).</summary>
    public bool ReadKda(ReadOnlySpan<byte> f, int stride, int x, int y, int w, int h, out short k, out short d, out short a, out float confidence)
    {
        k = d = a = 0; confidence = 0;
        if (_colInk.Length < w) _colInk = new int[Math.Max(w, _colInk.Length * 2)];
        Span<char> text = stackalloc char[24];
        var n = Ocr(f, stride, x, y, w, h, _colInk, text, (int)(170 - _sens * 40), out confidence);
        return n > 0 && TryParseKda(text[..n], out k, out d, out a);
    }

    /// <summary>ROI 문자 인식 → 인식 글자 수. colInk 스크래치 길이 ≥ w.</summary>
    public static int Ocr(ReadOnlySpan<byte> f, int stride, int x, int y, int w, int h, Span<int> colInk, Span<char> dst, int inkLum, out float confidence)
    {
        confidence = 0;
        for (var c = 0; c < w; c++)
        {
            var cnt = 0;
            for (var r = 0; r < h; r++)
            {
                var o = (y + r) * stride + (x + c) * 4;
                if (Lum(f, o) >= inkLum) cnt++;
            }
            colInk[c] = cnt;
        }
        int n = 0, totalDist = 0, c0 = 0;
        Span<byte> bits = stackalloc byte[7];
        while (c0 < w && n < dst.Length)
        {
            while (c0 < w && colInk[c0] == 0) c0++;
            if (c0 >= w) break;
            var c1 = c0;
            while (c1 < w && colInk[c1] > 0) c1++;
            // 글리프 행 범위
            int r0 = h, r1 = -1;
            for (var r = 0; r < h; r++)
            {
                var row = (y + r) * stride;
                for (var c = c0; c < c1; c++) if (Lum(f, row + (x + c) * 4) >= inkLum) { if (r < r0) r0 = r; r1 = r; break; }
            }
            var gh = r1 - r0 + 1;
            if (gh >= 7 && c1 - c0 <= gh)
            {
                var cell = gh / 7f;
                var center = (c0 + c1) * 0.5f;
                var left = center - 2.5f * cell;
                for (var cy = 0; cy < 7; cy++)
                {
                    var py = y + r0 + (int)((cy + 0.5f) * cell);
                    byte rowBits = 0;
                    for (var cx = 0; cx < 5; cx++)
                    {
                        var px = (int)(left + (cx + 0.5f) * cell);
                        if (px >= 0 && px < w && Lum(f, py * stride + (x + px) * 4) >= inkLum) rowBits |= (byte)(0x10 >> cx);
                    }
                    bits[cy] = rowBits;
                }
                int best = -1, bestD = int.MaxValue;
                for (var gi = 0; gi < GlyphChars.Length; gi++)
                {
                    var dist = 0;
                    for (var cy = 0; cy < 7; cy++) dist += System.Numerics.BitOperations.PopCount((uint)(bits[cy] ^ Glyphs[gi * 7 + cy]));
                    if (dist < bestD) { bestD = dist; best = gi; }
                }
                if (best >= 0 && bestD <= 5) { dst[n++] = GlyphChars[best]; totalDist += bestD; }
            }
            c0 = c1;
        }
        if (n > 0) confidence = 1f - totalDist / (35f * n);
        return n;
    }

    private static int Lum(ReadOnlySpan<byte> f, int o) => (f[o] * 29 + f[o + 1] * 150 + f[o + 2] * 77) >> 8;

    public static bool TryParseKda(ReadOnlySpan<char> s, out short k, out short d, out short a)
    {
        k = d = a = 0;
        var i1 = s.IndexOf('/');
        if (i1 <= 0) return false;
        var rest = s[(i1 + 1)..];
        var i2 = rest.IndexOf('/');
        if (i2 <= 0) return false;
        return short.TryParse(s[..i1], NumberStyles.None, CultureInfo.InvariantCulture, out k)
            && short.TryParse(rest[..i2], NumberStyles.None, CultureInfo.InvariantCulture, out d)
            && short.TryParse(rest[(i2 + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out a);
    }

    // ── 합성 HUD 렌더(데모 소스 · 테스트) ────────────────────────────────────

    public static void FillRect(Span<byte> f, int stride, int x, int y, int w, int h, byte b, byte g, byte r)
    {
        var px = (uint)(b | (g << 8) | (r << 16) | (0xFF << 24));
        for (var yy = y; yy < y + h; yy++) MemoryMarshal.Cast<byte, uint>(f.Slice(yy * stride + x * 4, w * 4)).Fill(px);
    }

    /// <summary>5x7 폰트 텍스트 렌더 → 끝 x.</summary>
    public static int DrawText(Span<byte> f, int stride, int x, int y, int scale, ReadOnlySpan<char> text, byte b, byte g, byte r)
    {
        foreach (var ch in text)
        {
            var gi = GlyphChars.IndexOf(ch);
            if (gi >= 0)
                for (var cy = 0; cy < 7; cy++)
                    for (var cx = 0; cx < 5; cx++)
                        if ((Glyphs[gi * 7 + cy] & (0x10 >> cx)) != 0) FillRect(f, stride, x + cx * scale, y + cy * scale, scale, scale, b, g, r);
            x += 6 * scale + (ch == '/' ? scale : 0);
        }
        return x;
    }

    /// <summary>프로필 ROI에 맞춰 합성 HUD 프레임 렌더(무할당).</summary>
    public static void RenderHud(Span<byte> f, int w, int h, int stride, GameProfile p, in DemoHudState s)
    {
        FillRect(f, stride, 0, 0, w, h, 38, 30, 24);
        p.Health.ToPixels(w, h, out var hx, out var hy, out var hw, out var hh);
        FillRect(f, stride, hx, hy, hw, hh, 20, 20, 20);
        var fw = (int)(hw * Math.Clamp(s.Hp, 0f, 1f));
        if (fw > 0)
        {
            if (p.HealthColor == BarColor.Green) FillRect(f, stride, hx, hy, fw, hh, 60, 200, 50);
            else if (p.HealthColor == BarColor.Red) FillRect(f, stride, hx, hy, fw, hh, 40, 40, 210);
            else FillRect(f, stride, hx, hy, fw, hh, 240, 240, 240);
        }
        p.Kda.ToPixels(w, h, out var kx, out var ky, out var kw, out var kh);
        var scale = Math.Max(1, kh * 4 / 5 / 7);
        Span<char> t = stackalloc char[20];
        var n = 0;
        s.Kills.TryFormat(t, out var c, default, CultureInfo.InvariantCulture); n += c; t[n++] = '/';
        s.Deaths.TryFormat(t[n..], out c, default, CultureInfo.InvariantCulture); n += c; t[n++] = '/';
        s.Assists.TryFormat(t[n..], out c, default, CultureInfo.InvariantCulture); n += c;
        DrawText(f, stride, kx + scale, ky + (kh - 7 * scale) / 2, scale, t[..n], 235, 235, 235);
        p.Minimap.ToPixels(w, h, out var mx, out var my, out var mw, out var mh);
        FillRect(f, stride, mx, my, mw, mh, 40, 70, 40);
        for (var e = 0; e < s.Enemies; e++)
        {
            var ex = mx + 4 + (e * 37 % Math.Max(1, mw - 12)); var ey = my + 4 + (e * 53 % Math.Max(1, mh - 12));
            FillRect(f, stride, ex, ey, Math.Min(6, mw - (ex - mx)), Math.Min(6, mh - (ey - my)), 40, 40, 230);
        }
        if (s.Banner != 0)
        {
            p.Banner.ToPixels(w, h, out var bx, out var by, out var bw, out var bh);
            if (s.Banner == 1) FillRect(f, stride, bx, by, bw, bh, 60, 190, 240);
            else FillRect(f, stride, bx, by, bw, bh, 50, 50, 210);
        }
    }

    /// <summary>데모 경기 타임라인(60초 루프) → HUD 상태.</summary>
    public static DemoHudState DemoTimeline(double sec)
    {
        var t = sec % 60;
        short k = (short)((t >= 5 ? 1 : 0) + (t >= 9 ? 1 : 0) + (t >= 18 ? 1 : 0) + (t >= 38 ? 1 : 0) + (t >= 44 ? 1 : 0));
        short d = (short)(t >= 25 ? 1 : 0);
        short a = (short)((t >= 31 ? 1 : 0) + (t >= 47 ? 1 : 0));
        var hp = t < 13 ? 0.9f : t < 15 ? 0.6f : t < 20 ? 0.16f : t < 25 ? 0.08f : t < 27 ? 0f : 0.85f;
        if (t >= 58.5) { k = d = a = 0; hp = 1f; }
        var enemies = (byte)(t is >= 22 and < 25 ? 9 : t is >= 35 and < 37 ? 7 : 1);
        byte banner = (byte)(t is >= 52 and < 58.5 ? 1 : 0);
        return new DemoHudState(hp, k, d, a, banner, enemies);
    }

    // ── 캡처 루프 ─────────────────────────────────────────────────────────

    public bool Start(CaptureOptions opts)
    {
        Stop();
        var o = opts with { TargetFps = Math.Clamp(opts.TargetFps, 5, 120) };
        if (o.Source == CaptureSource.Screen && !ScreenCaptureSupported) o = o with { Source = CaptureSource.Demo };
        _opts = o;
        Sensitivity = o.Sensitivity;
        SetProfile(o.Genre);
        _run = true;
        var th = new Thread(CaptureLoop) { IsBackground = true, Name = "PFS-GameVision", Priority = ThreadPriority.AboveNormal };
        _thread = th;
        th.Start();
        LastAction = "🎥 캡처 시작 · " + (o.Source == CaptureSource.Demo ? "데모 경기" : "화면") + " · " + ProfileOf(o.Genre).Label + " · " + o.TargetFps.ToString(CultureInfo.InvariantCulture) + "FPS";
        Raise();
        return true;
    }

    public void Stop()
    {
        var th = _thread;
        if (th is null) return;
        _run = false;
        if (Thread.CurrentThread != th) th.Join(2000);
        _thread = null;
        LastAction = "⏹ 캡처 중지";
        Raise();
    }

    private void CaptureLoop()
    {
        var o = _opts;
        var periodTicks = Stopwatch.Frequency / o.TargetFps;
        var next = Stopwatch.GetTimestamp();
        var start = next;
        long fpsFrames = 0, fpsStart = next, lastRaise = 0;
        ScreenGrabber? grab = null;
        try
        {
            int w, h;
            if (o.Source == CaptureSource.Screen)
            {
                grab = ScreenGrabber.Create(o.X, o.Y, o.Width, o.Height);
                if (grab is null) { LastAction = "⚠ 화면 캡처 실패 — 데모 소스로 전환"; o = o with { Source = CaptureSource.Demo }; _opts = o; }
            }
            if (grab is not null) { w = grab.Width; h = grab.Height; }
            else { w = DemoWidth; h = DemoHeight; }
            if (_frame.Length < w * h * 4) _frame = new byte[w * h * 4];
            var frame = _frame;
            while (_run)
            {
                try
                {
                    var now = Stopwatch.GetTimestamp();
                    var ok = true;
                    if (grab is not null) ok = grab.Grab(frame);
                    else RenderHud(frame, w, h, w * 4, _profile, DemoTimeline((now - start) / (double)Stopwatch.Frequency));
                    if (ok) Analyze(frame, w, h, w * 4, NowMs);
                    fpsFrames++;
                    if (now - fpsStart >= Stopwatch.Frequency)
                    {
                        _fps = fpsFrames * (double)Stopwatch.Frequency / (now - fpsStart);
                        fpsFrames = 0; fpsStart = now;
                    }
                    var ms = NowMs;
                    if (ms - lastRaise >= 250) { lastRaise = ms; Raise(); }
                }
                catch (Exception ex) { Program.Log("GameVision.Tick", ex); }
                next += periodTicks;
                var wait = next - Stopwatch.GetTimestamp();
                if (wait > 0) Thread.Sleep((int)Math.Max(0, wait * 1000 / Stopwatch.Frequency - 1));
                while (Stopwatch.GetTimestamp() < next) Thread.SpinWait(50);
                if (Stopwatch.GetTimestamp() - next > periodTicks * 4) next = Stopwatch.GetTimestamp();
            }
        }
        catch (Exception ex) { Program.Log("GameVision.Loop", ex); }
        finally { grab?.Dispose(); }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("GameVision.OnChange", ex); }
    }

    public void Dispose() => Stop();
}

/// <summary>GDI BitBlt → DIB 섹션 화면 캡처(Windows 전용 · 비Windows 스텁).</summary>
internal sealed class ScreenGrabber : IDisposable
{
    public int Width { get; }
    public int Height { get; }
#if WINDOWS
    private IntPtr _screenDc, _memDc, _bmp, _old, _bits;
    private readonly int _x, _y;

    private ScreenGrabber(int x, int y, int w, int h) { _x = x; _y = y; Width = w; Height = h; }

    public static ScreenGrabber? Create(int x, int y, int w, int h)
    {
        try
        {
            if (w <= 0 || h <= 0) { w = GetSystemMetrics(0); h = GetSystemMetrics(1); x = 0; y = 0; }
            if (w < 16 || h < 16) return null;
            var g = new ScreenGrabber(x, y, w, h);
            g._screenDc = GetDC(IntPtr.Zero);
            g._memDc = CreateCompatibleDC(g._screenDc);
            var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32, biCompression = 0, biSizeImage = 0, biXPelsPerMeter = 0, biYPelsPerMeter = 0, biClrUsed = 0, biClrImportant = 0 };
            g._bmp = CreateDIBSection(g._screenDc, ref bi, 0, out g._bits, IntPtr.Zero, 0);
            if (g._bmp == IntPtr.Zero || g._bits == IntPtr.Zero) { g.Dispose(); return null; }
            g._old = SelectObject(g._memDc, g._bmp);
            return g;
        }
        catch (Exception ex) { Program.Log("GameVision.Grabber", ex); return null; }
    }

    public bool Grab(byte[] dst)
    {
        if (!BitBlt(_memDc, 0, 0, Width, Height, _screenDc, _x, _y, 0x00CC0020 | 0x40000000)) return false;
        Marshal.Copy(_bits, dst, 0, Width * Height * 4);
        return true;
    }

    public void Dispose()
    {
        if (_memDc != IntPtr.Zero) { if (_old != IntPtr.Zero) SelectObject(_memDc, _old); DeleteDC(_memDc); _memDc = IntPtr.Zero; }
        if (_bmp != IntPtr.Zero) { DeleteObject(_bmp); _bmp = IntPtr.Zero; }
        if (_screenDc != IntPtr.Zero) { ReleaseDC(IntPtr.Zero, _screenDc); _screenDc = IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
#else
    public static ScreenGrabber? Create(int x, int y, int w, int h) => null;
    public bool Grab(byte[] dst) => false;
    public void Dispose() { }
#endif
}
