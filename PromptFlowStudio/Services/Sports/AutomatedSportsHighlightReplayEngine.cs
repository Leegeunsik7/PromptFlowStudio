using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using PromptFlowStudio.Services.Vfx;

namespace PromptFlowStudio.Services.Sports;

public enum HighlightKind : byte { Goal, CrowdSurge, Shot, Manual }

public enum ReplayState : byte { Idle, Preparing, Ready, Playing }

/// <summary>하이라이트 클립 메타(원본 프레임 수 · 슬로 배율 · 함성 피크 · 준비 시간).</summary>
public sealed record HighlightClip(int Id, HighlightKind Kind, double TriggerMs, double StartMs, int SourceFrames, int SlowFactor, float PeakDb, double PrepareMs, string Title);

/// <summary>
/// [PromptFlow Studio v16.45.0] 🔁 AI 자동 하이라이트 감지 &amp; 초슬로모션 인스턴트 리플레이 엔진.
///   ▸ 프레임 링 버퍼(rgb24 · 30fps × 7초 · 고정 슬롯 · 무할당 복사) · 관중 오디오 RMS → dB(20·log10+100) · 기준선 EWMA(8초 · 서지 중 동결) 대비 +10dB 300ms 지속 → 함성 서지.
///   ▸ 득점 판정: 볼 궤적 골라인 교차 보간(포스트 사이 · 크로스바 아래) · 경기 골 이벤트 — 쿨다운 8초 중복 제거.
///   ▸ 트리거 → 직전 5초 프레임 스냅샷 → 백그라운드 광학 흐름(블록 매칭 · MotionFrameInterpolationEngine) 사전 계산 → 120fps(×4) 양방향 워프 보간 재생.
///   ▸ 재생: 출력 프레임마다 보간 프레임 1장(PGM 인젝션) — 수집/오디오/재생 경로 무할당. 클립 목록 상한 24 · 진행 중 트리거는 드롭 집계.
/// </summary>
public sealed class AutomatedSportsHighlightReplayEngine : IDisposable
{
    public const int SourceFps = 30, TargetFps = 120, PreRollSec = 5, RingFrames = SourceFps * (PreRollSec + 2), MaxClipFrames = SourceFps * PreRollSec + 1, FlowRadius = 4, ClipCap = 24;
    public const float SurgeDb = 10f;
    public const double SurgeHoldMs = 300, CooldownMs = 8000;

    private readonly byte[][] _ring, _clip;
    private readonly double[] _ringTs = new double[RingFrames];
    private readonly FlowField[] _flows;
    private readonly byte[] _lumaA, _lumaB;
    private readonly object _ringGate = new();
    private readonly List<HighlightClip> _clips = new();
    private int _head, _count, _clipN, _playPos, _state, _nextId = 1;
    private float _baseline = float.NaN, _short, _peakDb;
    private double _surgeSince = double.NaN, _lastAudioMs = double.NaN, _lastTriggerMs = double.NegativeInfinity, _lastFrameMs;
    private Vector3 _prevBall;
    private bool _hasBall;
    private long _triggers, _dropped, _played, _lastRaise;
    private CancellationTokenSource? _cts;

    public AutomatedSportsHighlightReplayEngine() : this(AiSportsMulticamAutoDirectorEngine.HalfW, AiSportsMulticamAutoDirectorEngine.HalfH) { }

    public AutomatedSportsHighlightReplayEngine(int width, int height)
    {
        Width = width; Height = height; FrameBytes = width * height * 3;
        _ring = new byte[RingFrames][];
        for (var i = 0; i < RingFrames; i++) _ring[i] = new byte[FrameBytes];
        _clip = new byte[MaxClipFrames][];
        for (var i = 0; i < MaxClipFrames; i++) _clip[i] = new byte[FrameBytes];
        _flows = new FlowField[MaxClipFrames - 1];
        for (var i = 0; i < _flows.Length; i++) _flows[i] = new FlowField(width, height, MotionFrameInterpolationEngine.FlowBlock);
        _lumaA = new byte[width * height]; _lumaB = new byte[width * height];
    }

    public event Action? OnChange;

    public int Width { get; }
    public int Height { get; }
    public int FrameBytes { get; }
    public static int SlowFactor => TargetFps / SourceFps;
    public bool AutoReplay { get; set; } = true;
    public bool AutoPlay { get; set; } = true;
    public bool AutoReplayShots { get; set; }
    public ReplayState State => (ReplayState)Volatile.Read(ref _state);
    public int BufferedFrames { get { lock (_ringGate) return _count; } }
    public int ClipFrames => _clipN;
    public float CrowdDb => _short;
    public float BaselineDb => _baseline;
    public float ExcitementDb => float.IsNaN(_baseline) ? 0 : _short - _baseline;
    public long Triggers => Interlocked.Read(ref _triggers);
    public long Dropped => Interlocked.Read(ref _dropped);
    public long ReplaysPlayed => Interlocked.Read(ref _played);
    public Task? PendingPrepare { get; private set; }
    public string? LastAction { get; private set; }
    public IReadOnlyList<HighlightClip> Clips { get { lock (_clips) return _clips.ToArray(); } }
    public int TotalReplayFrames => _clipN < 2 ? 0 : (_clipN - 1) * SlowFactor + 1;
    public float PlaybackProgress => TotalReplayFrames == 0 ? 0 : Math.Clamp(Volatile.Read(ref _playPos) / (float)TotalReplayFrames, 0, 1);

    // ══════════════════════ 수집(무할당) ══════════════════════

    public void PushFrame(ReadOnlySpan<byte> rgb, double tMs)
    {
        if (rgb.Length < FrameBytes) throw new ArgumentException("프레임 크기 불일치");
        lock (_ringGate)
        {
            rgb[..FrameBytes].CopyTo(_ring[_head]);
            _ringTs[_head] = tMs;
            _head = (_head + 1) % RingFrames;
            if (_count < RingFrames) _count++;
        }
        _lastFrameMs = tMs;
    }

    public static float RmsDb(ReadOnlySpan<float> s)
    {
        if (s.IsEmpty) return 0;
        double acc = 0;
        for (var i = 0; i < s.Length; i++) acc += s[i] * s[i];
        return (float)(20 * Math.Log10(Math.Sqrt(acc / s.Length) + 1e-9) + 100);
    }

    /// <summary>관중 오디오 블록 → 함성 레벨/기준선 갱신 · 서지 지속 시 하이라이트 트리거. 무할당.</summary>
    public void PushAudio(ReadOnlySpan<float> samples, double tMs)
    {
        var db = RmsDb(samples);
        if (float.IsNaN(_baseline)) { _baseline = db; _short = db; _lastAudioMs = tMs; return; }
        var dt = (float)Math.Clamp((tMs - _lastAudioMs) / 1000.0, 0, 0.2);
        _lastAudioMs = tMs;
        _short += (db - _short) * (1 - MathF.Exp(-dt / 0.25f));
        var ex = _short - _baseline;
        if (ex >= SurgeDb)
        {
            if (double.IsNaN(_surgeSince)) { _surgeSince = tMs; _peakDb = _short; }
            _peakDb = MathF.Max(_peakDb, _short);
            if (AutoReplay && tMs - _surgeSince >= SurgeHoldMs) TryTrigger(HighlightKind.CrowdSurge, tMs, false);
        }
        else
        {
            _surgeSince = double.NaN;
            _baseline += (db - _baseline) * (1 - MathF.Exp(-dt / 8f));
        }
    }

    /// <summary>볼 궤적 골라인 교차(교차 지점 보간 · 포스트 사이 · 크로스바 아래). side 0 = 좌(X=0) · 1 = 우.</summary>
    public static bool CrossedGoalLine(Vector3 a, Vector3 b, out int side)
    {
        const float L = SportsMatchSimulator.PitchLength;
        side = -1;
        float gx;
        if (a.X > 0 && b.X <= 0) { side = 0; gx = 0; }
        else if (a.X < L && b.X >= L) { side = 1; gx = L; }
        else return false;
        var f = (gx - a.X) / (b.X - a.X);
        var y = a.Y + (b.Y - a.Y) * f;
        var z = a.Z + (b.Z - a.Z) * f;
        return MathF.Abs(y - SportsMatchSimulator.PitchWidth / 2) < SportsMatchSimulator.GoalHalfWidth && z >= 0 && z < SportsMatchSimulator.GoalHeight;
    }

    public void ObserveBall(double tMs, Vector3 ball)
    {
        if (_hasBall && AutoReplay && CrossedGoalLine(_prevBall, ball, out _)) TryTrigger(HighlightKind.Goal, tMs, false);
        _prevBall = ball;
        _hasBall = true;
    }

    public void OnMatchEvent(in MatchEvent e)
    {
        if (!AutoReplay) return;
        if (e.Kind == MatchEventKind.Goal) TryTrigger(HighlightKind.Goal, e.TimeMs, false);
        else if (e.Kind == MatchEventKind.Shot && AutoReplayShots) TryTrigger(HighlightKind.Shot, e.TimeMs + 1200, false);
    }

    /// <summary>수동 인스턴트 리플레이(쿨다운 무시).</summary>
    public bool FireReplay() => TryTrigger(HighlightKind.Manual, _lastFrameMs, true);

    // ══════════════════════ 트리거 · 사전 계산 ══════════════════════

    public bool TryTrigger(HighlightKind kind, double tMs, bool force)
    {
        if (!force && tMs - _lastTriggerMs < CooldownMs) return false;
        var st = State;
        if (st is ReplayState.Preparing or ReplayState.Playing) { Interlocked.Increment(ref _dropped); return false; }
        int n;
        double start = double.NaN;
        lock (_ringGate)
        {
            n = 0;
            var from = tMs - PreRollSec * 1000.0;
            for (var i = 0; i < _count && n < MaxClipFrames; i++)
            {
                var k = (_head - _count + i + RingFrames) % RingFrames;
                var ts = _ringTs[k];
                if (ts < from || ts > tMs + 1) continue;
                if (n == 0) start = ts;
                _ring[k].AsSpan().CopyTo(_clip[n++]);
            }
        }
        if (n < 2) return false;
        _lastTriggerMs = tMs;
        _clipN = n;
        Volatile.Write(ref _playPos, 0);
        Volatile.Write(ref _state, (int)ReplayState.Preparing);
        Interlocked.Increment(ref _triggers);
        var peak = kind == HighlightKind.CrowdSurge ? _peakDb : _short;
        var cts = new CancellationTokenSource();
        var prev = Interlocked.Exchange(ref _cts, cts);
        if (prev is not null) { try { prev.Cancel(); } catch (ObjectDisposedException) { } prev.Dispose(); }
        var id = _nextId++;
        LastAction = "⏳ 하이라이트 감지 · " + KindLabel(kind) + " · 프레임 " + n.ToString(CultureInfo.InvariantCulture);
        Raise(true);
        PendingPrepare = Task.Run(() => Prepare(id, kind, tMs, start, n, peak, cts));
        return true;
    }

    private void Prepare(int id, HighlightKind kind, double tMs, double start, int n, float peak, CancellationTokenSource cts)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var ct = cts.Token;
            MotionFrameInterpolationEngine.ToLuma(_clip[0], _lumaA);
            var a = _lumaA; var b = _lumaB;
            for (var i = 0; i < n - 1; i++)
            {
                ct.ThrowIfCancellationRequested();
                MotionFrameInterpolationEngine.ToLuma(_clip[i + 1], b);
                MotionFrameInterpolationEngine.EstimateFlow(a, b, Width, Height, FlowRadius, _flows[i]);
                (a, b) = (b, a);
            }
            var title = KindLabel(kind) + " · " + ((tMs - start) / 1000).ToString("0.0", CultureInfo.InvariantCulture) + "초 → ×" + SlowFactor.ToString(CultureInfo.InvariantCulture) + " 슬로";
            var clip = new HighlightClip(id, kind, tMs, start, n, SlowFactor, peak, sw.Elapsed.TotalMilliseconds, title);
            lock (_clips)
            {
                _clips.Insert(0, clip);
                if (_clips.Count > ClipCap) _clips.RemoveAt(_clips.Count - 1);
            }
            Volatile.Write(ref _state, (int)(AutoPlay ? ReplayState.Playing : ReplayState.Ready));
            LastAction = "🎬 " + title + " · 준비 " + sw.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + "ms";
        }
        catch (OperationCanceledException) { Volatile.Write(ref _state, (int)ReplayState.Idle); }
        catch (Exception ex) { PromptFlowStudio.Program.Log("Highlight.Prepare", ex); Volatile.Write(ref _state, (int)ReplayState.Idle); }
        finally
        {
            if (Interlocked.CompareExchange(ref _cts, null, cts) == cts) cts.Dispose();
        }
        Raise(true);
    }

    public bool PlayReady()
    {
        if (Interlocked.CompareExchange(ref _state, (int)ReplayState.Playing, (int)ReplayState.Ready) != (int)ReplayState.Ready) return false;
        Volatile.Write(ref _playPos, 0);
        LastAction = "▶ 리플레이 재생";
        Raise(true);
        return true;
    }

    public void StopReplay()
    {
        var c = Volatile.Read(ref _cts);
        if (c is not null) { try { c.Cancel(); } catch (ObjectDisposedException) { } }
        if (Interlocked.CompareExchange(ref _state, (int)ReplayState.Idle, (int)ReplayState.Playing) == (int)ReplayState.Playing) { LastAction = "⏹ 리플레이 중단"; Raise(true); }
    }

    // ══════════════════════ 재생(무할당) ══════════════════════

    /// <summary>재생 중이면 다음 보간 프레임(rgb24 Width×Height) 기록 후 true · 끝나면 Idle 전환 후 false.</summary>
    public bool TryRenderReplayFrame(Span<byte> dst)
    {
        if (Volatile.Read(ref _state) != (int)ReplayState.Playing) return false;
        var total = TotalReplayFrames;
        var i = Volatile.Read(ref _playPos);
        if (i >= total)
        {
            if (Interlocked.CompareExchange(ref _state, (int)ReplayState.Idle, (int)ReplayState.Playing) == (int)ReplayState.Playing)
            {
                Interlocked.Increment(ref _played);
                LastAction = "✅ 리플레이 완료 · 라이브 복귀";
                Raise(true);
            }
            return false;
        }
        RenderReplayFrame(i, dst);
        Volatile.Write(ref _playPos, i + 1);
        return true;
    }

    /// <summary>보간 프레임 index(0 ~ TotalReplayFrames-1) 렌더 — 원본 프레임 사이 ×SlowFactor 양방향 워프.</summary>
    public void RenderReplayFrame(int index, Span<byte> dst)
    {
        var f = SlowFactor;
        var src = Math.Clamp(index / f, 0, Math.Max(0, _clipN - 1));
        var frac = index % f;
        if (frac == 0 || src >= _clipN - 1) { _clip[src].AsSpan().CopyTo(dst); return; }
        MotionFrameInterpolationEngine.Interpolate(_clip[src], _clip[src + 1], Width, Height, _flows[src], frac / (float)f, dst);
    }

    public static string KindLabel(HighlightKind k) => k switch
    {
        HighlightKind.Goal => "⚽ GOAL", HighlightKind.CrowdSurge => "📣 관중 함성", HighlightKind.Shot => "🎯 슈팅", _ => "🔁 수동",
    };

    public static string StateLabel(ReplayState s) => s switch
    {
        ReplayState.Preparing => "보간 준비 중", ReplayState.Ready => "재생 대기", ReplayState.Playing => "REPLAY ON AIR", _ => "대기",
    };

    private void Raise(bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now - Interlocked.Read(ref _lastRaise) < 250) return;
        Interlocked.Exchange(ref _lastRaise, now);
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Highlight.OnChange", ex); }
    }

    public void Dispose()
    {
        var c = Interlocked.Exchange(ref _cts, null);
        if (c is null) return;
        try { c.Cancel(); } catch (ObjectDisposedException) { }
    }
}
