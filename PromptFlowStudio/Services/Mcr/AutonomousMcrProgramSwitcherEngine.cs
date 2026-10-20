using System.Diagnostics;
using System.Globalization;

namespace PromptFlowStudio.Services.Mcr;

public enum McrSourceKind : byte { Camera, Playout, Remote, Slate }

public enum SignalHealth : byte { Ok, Silent, Frozen, Black, Lost }

public enum McrEventKind : byte { Take, AutoTake, Bypass, Restore, Fault, Recover, Preview, AdBreak }

public enum InjectedFault : byte { None, Black, Freeze, Silence, Loss }

/// <summary>MCR 이벤트(값형 · 링 버퍼).</summary>
public readonly record struct McrEvent(double TimeMs, McrEventKind Kind, int From, int To, SignalHealth Reason, double LatencyMs);

/// <summary>
/// [PromptFlow Studio v16.43.0] 📺 자율 방송 주조종실(MCR) PGM/PVW 스위칭 엔진.
///   ▸ 입력: 채널별 루마 평면 + 오디오 블록(Ingest · 무할당) → 32×18 그리드 다운샘플 · 평균 휘도/분산(블랙) · 그리드 차분(프리즈) · RMS(무음) · 마지막 프레임 시각(유실).
///   ▸ 0초 에머전시 바이패스: Tick 에서 PGM 장애(블랙/프리즈/유실) 판정 즉시 같은 틱에 PVW(정상) → 정상 채널 최고 점수 → 루프백 슬레이트 순 전환 · 장애 해소 후 홀드 경과 시 원 채널 자동 복귀(옵션).
///   ▸ AI 자율 연출: 채널 활동 점수(모션 · 오디오 · 휘도 대비) → PVW 자동 큐 · 최소 홀드 후 우세(×1.25) 시 컷 테이크.
///   ▸ 수동: PVW 지정 · TAKE · 바이패스 자동/수동 토글 · 강제 바이패스 · 데모 소스(전용 스레드 30fps · 장애 주입) — 틱/수집 경로 워밍업 후 무할당.
/// </summary>
public sealed class AutonomousMcrProgramSwitcherEngine : IDisposable
{
    public const int GridW = 32, GridH = 18, GridN = GridW * GridH, MaxChannels = 8, EventCap = 256, Fps = 30;
    public const float BlackLuma = 18f, BlackVariance = 30f, FreezeDiff = 0.6f, SilenceRms = 1e-3f;
    public const int BlackFrames = 3, FreezeFrames = 15;
    public const double LossMs = 120, SilenceMs = 2000;

    private sealed class Channel
    {
        public string Name = "";
        public McrSourceKind Kind;
        public readonly float[] Grid = new float[GridN], Prev = new float[GridN];
        public float Luma, Variance, Diff, Rms, Activity;
        public double LastFrameMs = double.NegativeInfinity, LastSoundMs, FaultSinceMs = double.NaN, BlackStartMs = double.NaN, FreezeStartMs = double.NaN;
        public int BlackRun, FreezeRun;
        public long Frames;
        public bool HasPrev;
        public SignalHealth Health = SignalHealth.Lost;
        public InjectedFault Fault;
    }

    private readonly Channel[] _ch = new Channel[MaxChannels];
    private readonly McrEvent[] _events = new McrEvent[EventCap];
    private readonly object _gate = new();
    private readonly ContextualServerSideAdInsertionEngine? _ssai;
    private int _count, _pgm = -1, _pvw = -1, _slate = -1, _evHead, _evCount, _bypassedFrom = -1;
    private double _pgmSinceMs, _restoreHealthySinceMs = double.NaN, _adUntilMs;
    private long _bypasses, _takes, _autoTakes;
    private double _lastBypassLatency;
    private Thread? _demo;
    private volatile bool _run;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastRaise;

    public AutonomousMcrProgramSwitcherEngine() : this((ContextualServerSideAdInsertionEngine?)null) { }

    public AutonomousMcrProgramSwitcherEngine(IServiceProvider sp) : this(sp.GetService(typeof(ContextualServerSideAdInsertionEngine)) as ContextualServerSideAdInsertionEngine) { }

    public AutonomousMcrProgramSwitcherEngine(ContextualServerSideAdInsertionEngine? ssai) => _ssai = ssai;

    public event Action? OnChange;

    public int ChannelCount => _count;
    public int Program => _pgm;
    public int Preview => _pvw;
    public int SlateChannel => _slate;
    public bool AutoBypass { get; set; } = true;
    public bool AutoDirector { get; set; }
    public bool AutoRestore { get; set; } = true;
    public double MinHoldMs { get; set; } = 4000;
    public double RestoreHoldMs { get; set; } = 2000;
    public long Bypasses => Interlocked.Read(ref _bypasses);
    public long Takes => Interlocked.Read(ref _takes);
    public long AutoTakes => Interlocked.Read(ref _autoTakes);
    public double LastBypassLatencyMs => _lastBypassLatency;
    public bool InBypass => _bypassedFrom >= 0;
    public bool DemoRunning => _demo is not null;
    public bool AdBreakActive(double nowMs) => nowMs < _adUntilMs;
    public double NowMs => _clock.Elapsed.TotalMilliseconds;
    public string? LastAction { get; private set; }

    public string NameOf(int ch) => (uint)ch < (uint)_count ? _ch[ch].Name : "—";
    public McrSourceKind KindOf(int ch) => _ch[ch].Kind;
    public SignalHealth HealthOf(int ch) => (uint)ch < (uint)_count ? _ch[ch].Health : SignalHealth.Lost;
    public float LumaOf(int ch) => _ch[ch].Luma;
    public float ActivityOf(int ch) => _ch[ch].Activity;
    public float RmsOf(int ch) => _ch[ch].Rms;
    public long FramesOf(int ch) => _ch[ch].Frames;
    public InjectedFault FaultOf(int ch) => _ch[ch].Fault;

    /// <summary>썸네일(16×9 평균 휘도 0~255) 복사.</summary>
    public void CopyThumb(int ch, Span<float> dst16x9)
    {
        if ((uint)ch >= (uint)_count) { dst16x9.Clear(); return; }
        lock (_gate)
        {
            var g = _ch[ch].Grid;
            for (var y = 0; y < 9; y++)
                for (var x = 0; x < 16; x++)
                    dst16x9[y * 16 + x] = (g[(y * 2) * GridW + x * 2] + g[(y * 2) * GridW + x * 2 + 1] + g[(y * 2 + 1) * GridW + x * 2] + g[(y * 2 + 1) * GridW + x * 2 + 1]) * 0.25f;
        }
    }

    public int CopyEvents(Span<McrEvent> dst)
    {
        lock (_gate)
        {
            var n = Math.Min(dst.Length, _evCount);
            for (var i = 0; i < n; i++) dst[i] = _events[(_evHead - 1 - i + EventCap) % EventCap];
            return n;
        }
    }

    // ══════════════════════ 채널 ══════════════════════

    public int AddChannel(string name, McrSourceKind kind)
    {
        lock (_gate)
        {
            if (_count >= MaxChannels) return -1;
            var c = new Channel { Name = name, Kind = kind };
            _ch[_count] = c;
            if (kind == McrSourceKind.Slate) { _slate = _count; c.Health = SignalHealth.Ok; }
            if (_pgm < 0 && kind != McrSourceKind.Slate) _pgm = _count;
            else if (_pvw < 0 && kind != McrSourceKind.Slate) _pvw = _count;
            return _count++;
        }
    }

    /// <summary>표준 데모 구성: CAM1 · CAM2 · 플레이아웃 · 원격 · 에머전시 슬레이트(루프백).</summary>
    public void ConfigureDefault()
    {
        if (_count > 0) return;
        AddChannel("CAM 1 메인", McrSourceKind.Camera);
        AddChannel("CAM 2 와이드", McrSourceKind.Camera);
        AddChannel("PLAYOUT 서버", McrSourceKind.Playout);
        AddChannel("REMOTE 중계", McrSourceKind.Remote);
        AddChannel("EMERGENCY 슬레이트", McrSourceKind.Slate);
    }

    // ══════════════════════ 수집(무할당) ══════════════════════

    /// <summary>프레임 수집: 루마 평면(stride) → 그리드 · 휘도/분산/프리즈 차분 · 오디오 RMS.</summary>
    public void Ingest(int ch, ReadOnlySpan<byte> luma, int w, int h, int stride, ReadOnlySpan<float> audio, double nowMs)
    {
        if ((uint)ch >= (uint)_count || w <= 0 || h <= 0) return;
        lock (_gate)
        {
            var c = _ch[ch];
            var g = c.Grid;
            if (c.HasPrev) g.AsSpan().CopyTo(c.Prev);
            double sum = 0, sq = 0;
            for (var gy = 0; gy < GridH; gy++)
            {
                var y = gy * h / GridH + h / (GridH * 2);
                for (var gx = 0; gx < GridW; gx++)
                {
                    var x = gx * w / GridW + w / (GridW * 2);
                    float v = luma[Math.Min(y, h - 1) * stride + Math.Min(x, w - 1)];
                    g[gy * GridW + gx] = v; sum += v; sq += v * v;
                }
            }
            c.Luma = (float)(sum / GridN);
            c.Variance = (float)Math.Max(0, sq / GridN - c.Luma * (double)c.Luma);
            float diff = 0;
            if (c.HasPrev) { for (var i = 0; i < GridN; i++) diff += MathF.Abs(g[i] - c.Prev[i]); diff /= GridN; } else diff = 255;
            c.Diff = diff;
            c.HasPrev = true;
            if (!audio.IsEmpty)
            {
                double e = 0;
                for (var i = 0; i < audio.Length; i++) e += audio[i] * audio[i];
                c.Rms = (float)Math.Sqrt(e / audio.Length);
                if (c.Rms >= SilenceRms) c.LastSoundMs = nowMs;
            }
            if (c.Frames == 0) c.LastSoundMs = nowMs;
            c.LastFrameMs = nowMs;
            c.Frames++;
            var black = c.Luma < BlackLuma && c.Variance < BlackVariance;
            if (black) { if (c.BlackRun++ == 0) c.BlackStartMs = nowMs; } else { c.BlackRun = 0; c.BlackStartMs = double.NaN; }
            if (diff < FreezeDiff && !black) { if (c.FreezeRun++ == 0) c.FreezeStartMs = nowMs; } else { c.FreezeRun = 0; c.FreezeStartMs = double.NaN; }
            c.Activity = c.Activity * 0.85f + 0.15f * (Math.Min(1f, diff / 12f) * 0.6f + Math.Min(1f, c.Rms * 8f) * 0.3f + Math.Min(1f, MathF.Sqrt(c.Variance) / 60f) * 0.1f);
        }
    }

    // ══════════════════════ 판정 · 스위칭(무할당) ══════════════════════

    private SignalHealth Evaluate(Channel c, double now)
    {
        if (c.Kind == McrSourceKind.Slate) return SignalHealth.Ok;
        if (now - c.LastFrameMs > LossMs) return SignalHealth.Lost;
        if (c.BlackRun >= BlackFrames) return SignalHealth.Black;
        if (c.FreezeRun >= FreezeFrames) return SignalHealth.Frozen;
        if (now - c.LastSoundMs > SilenceMs) return SignalHealth.Silent;
        return SignalHealth.Ok;
    }

    private static bool Severe(SignalHealth h) => h is SignalHealth.Black or SignalHealth.Frozen or SignalHealth.Lost;

    private double FaultOnset(Channel c, SignalHealth h, double now) => h switch
    {
        SignalHealth.Black => c.BlackStartMs,
        SignalHealth.Frozen => c.FreezeStartMs,
        SignalHealth.Lost => double.IsNegativeInfinity(c.LastFrameMs) ? now : c.LastFrameMs,
        _ => now,
    };

    /// <summary>1 틱: 건강 판정 → PGM 장애 시 같은 틱 바이패스 · 복귀 · 자율 연출. 반환 = 이번 틱 전환 여부.</summary>
    public bool Tick(double nowMs)
    {
        var switched = false;
        lock (_gate)
        {
            for (var i = 0; i < _count; i++)
            {
                var c = _ch[i];
                var h = Evaluate(c, nowMs);
                if (h != c.Health)
                {
                    if (Severe(h) && !Severe(c.Health)) { c.FaultSinceMs = FaultOnset(c, h, nowMs); Log(nowMs, McrEventKind.Fault, i, i, h, 0); }
                    else if (h == SignalHealth.Ok && Severe(c.Health)) { c.FaultSinceMs = double.NaN; Log(nowMs, McrEventKind.Recover, i, i, h, 0); }
                    c.Health = h;
                }
            }
            if (_pgm >= 0 && Severe(_ch[_pgm].Health) && AutoBypass)
            {
                var from = _pgm;
                var to = PickBackup(from);
                if (to >= 0 && to != from)
                {
                    var latency = nowMs - (double.IsNaN(_ch[from].FaultSinceMs) ? nowMs : _ch[from].FaultSinceMs);
                    if (_bypassedFrom < 0) _bypassedFrom = from;
                    _pgm = to;
                    if (_pvw == to) _pvw = from;
                    _pgmSinceMs = nowMs;
                    _restoreHealthySinceMs = double.NaN;
                    _lastBypassLatency = latency;
                    Interlocked.Increment(ref _bypasses);
                    Log(nowMs, McrEventKind.Bypass, from, to, _ch[from].Health, latency);
                    LastAction = "🚨 에머전시 바이패스 · " + _ch[from].Name + " → " + _ch[to].Name;
                    switched = true;
                }
            }
            else if (_bypassedFrom >= 0 && AutoRestore && _pgm != _bypassedFrom)
            {
                var o = _ch[_bypassedFrom];
                if (o.Health == SignalHealth.Ok)
                {
                    if (double.IsNaN(_restoreHealthySinceMs)) _restoreHealthySinceMs = nowMs;
                    else if (nowMs - _restoreHealthySinceMs >= RestoreHoldMs)
                    {
                        var from = _pgm;
                        _pgm = _bypassedFrom;
                        if (_pvw == _pgm) _pvw = from == _slate ? PickPreview(_pgm) : from;
                        _bypassedFrom = -1;
                        _pgmSinceMs = nowMs;
                        Log(nowMs, McrEventKind.Restore, from, _pgm, SignalHealth.Ok, 0);
                        LastAction = "✅ 원 채널 복귀 · " + _ch[_pgm].Name;
                        switched = true;
                    }
                }
                else _restoreHealthySinceMs = double.NaN;
            }
            else if (_bypassedFrom >= 0 && _pgm == _bypassedFrom) _bypassedFrom = -1;

            if (!switched && AutoDirector && _bypassedFrom < 0 && _pgm >= 0)
            {
                var best = PickPreview(_pgm);
                if (best >= 0 && best != _pvw) { _pvw = best; Log(nowMs, McrEventKind.Preview, _pgm, best, SignalHealth.Ok, 0); }
                if (_pvw >= 0 && _ch[_pvw].Health == SignalHealth.Ok && nowMs - _pgmSinceMs >= MinHoldMs && _ch[_pvw].Activity > _ch[_pgm].Activity * 1.25f + 0.02f)
                {
                    TakeLocked(nowMs, true);
                    switched = true;
                }
            }
        }
        if (_ssai is not null && _pgm >= 0)
        {
            var c = _ch[_pgm];
            if (_ssai.FeedLive(nowMs / 1000.0, c.Rms, (int)(c.Activity * 100)))
            {
                lock (_gate) { _adUntilMs = nowMs + _ssai.Policy.BreakSeconds * 1000; Log(nowMs, McrEventKind.AdBreak, _pgm, _pgm, SignalHealth.Ok, 0); }
                LastAction = "📢 SCTE-35 광고 큐(라이브) · " + _ssai.Policy.BreakSeconds.ToString("0", CultureInfo.InvariantCulture) + "s";
                switched = true;
            }
        }
        Raise(switched);
        return switched;
    }

    private int PickBackup(int from)
    {
        if (_pvw >= 0 && _pvw != from && _ch[_pvw].Health == SignalHealth.Ok && _ch[_pvw].Kind != McrSourceKind.Slate) return _pvw;
        var best = -1; var bs = -1f;
        for (var i = 0; i < _count; i++)
        {
            if (i == from || _ch[i].Kind == McrSourceKind.Slate || _ch[i].Health != SignalHealth.Ok) continue;
            if (_ch[i].Activity > bs) { bs = _ch[i].Activity; best = i; }
        }
        return best >= 0 ? best : _slate;
    }

    private int PickPreview(int pgm)
    {
        var best = -1; var bs = -1f;
        for (var i = 0; i < _count; i++)
        {
            if (i == pgm || _ch[i].Kind == McrSourceKind.Slate || _ch[i].Health != SignalHealth.Ok) continue;
            if (_ch[i].Activity > bs) { bs = _ch[i].Activity; best = i; }
        }
        return best;
    }

    private void TakeLocked(double now, bool auto)
    {
        if (_pvw < 0 || _pvw == _pgm) return;
        var from = _pgm;
        _pgm = _pvw; _pvw = from;
        _pgmSinceMs = now;
        _bypassedFrom = -1;
        if (auto) Interlocked.Increment(ref _autoTakes); else Interlocked.Increment(ref _takes);
        Log(now, auto ? McrEventKind.AutoTake : McrEventKind.Take, from, _pgm, SignalHealth.Ok, 0);
        LastAction = (auto ? "🤖 AI 컷 · " : "✂ TAKE · ") + _ch[from].Name + " → " + _ch[_pgm].Name;
    }

    public void SetPreview(int ch)
    {
        lock (_gate)
        {
            if ((uint)ch >= (uint)_count || ch == _pgm) return;
            _pvw = ch;
            Log(NowMs, McrEventKind.Preview, _pgm, ch, SignalHealth.Ok, 0);
        }
        Raise(true);
    }

    public void Take() { lock (_gate) TakeLocked(NowMs, false); Raise(true); }

    /// <summary>수동 강제 바이패스(슬레이트 우선 선택 가능).</summary>
    public void ForceBypass(bool toSlate = false)
    {
        lock (_gate)
        {
            if (_pgm < 0) return;
            var from = _pgm;
            var to = toSlate ? _slate : PickBackup(from);
            if (to < 0 || to == from) return;
            if (_bypassedFrom < 0) _bypassedFrom = from;
            _pgm = to;
            _pgmSinceMs = NowMs;
            Interlocked.Increment(ref _bypasses);
            _lastBypassLatency = 0;
            Log(NowMs, McrEventKind.Bypass, from, to, SignalHealth.Ok, 0);
            LastAction = "🛑 수동 바이패스 · " + _ch[from].Name + " → " + _ch[to].Name;
        }
        Raise(true);
    }

    private void Log(double t, McrEventKind k, int from, int to, SignalHealth reason, double latency)
    {
        _events[_evHead] = new McrEvent(t, k, from, to, reason, latency);
        _evHead = (_evHead + 1) % EventCap;
        if (_evCount < EventCap) _evCount++;
    }

    // ══════════════════════ 데모 소스(전용 스레드) ══════════════════════

    public void InjectFault(int ch, InjectedFault f) { if ((uint)ch < (uint)_count) { _ch[ch].Fault = f; LastAction = "🧪 장애 주입 · " + _ch[ch].Name + " · " + f; Raise(true); } }

    /// <summary>합성 소스 프레임(채널별 이동 패턴 · 장애 반영) → 버퍼(무할당).</summary>
    public static void RenderDemo(int ch, double tMs, Span<byte> luma, int w, int h, Span<float> audio, InjectedFault fault, double frozenAtMs, bool slate)
    {
        var t = (fault == InjectedFault.Freeze ? frozenAtMs : tMs) / 1000.0;
        if (fault == InjectedFault.Black) luma[..(w * h)].Fill(4);
        else if (slate)
        {
            for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) luma[y * w + x] = (byte)((x * 8 / w) * 30 + 20);
        }
        else
        {
            var speed = 0.6 + ch * 0.35;
            var cx = w * (0.5 + 0.35 * Math.Sin(t * speed + ch));
            var cy = h * (0.5 + 0.3 * Math.Cos(t * speed * 0.8 + ch * 2));
            var rad = (w / 7.0) * (1 + 0.2 * ch);
            var drift = (int)(t * 30);   // 배경 드리프트(정상 소스 프리즈 오탐 방지)
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var dx = x - cx; var dy = y - cy;
                    var inside = dx * dx + dy * dy < rad * rad;
                    luma[y * w + x] = (byte)(inside ? 220 - ch * 20 : 50 + (x + y + ch * 17 + drift) % 60);
                }
        }
        var amp = fault == InjectedFault.Silence || slate ? 0f : (float)(0.05 + 0.04 * Math.Sin(t * (1.3 + ch)));
        for (var i = 0; i < audio.Length; i++) audio[i] = amp * MathF.Sin((float)(i * (0.05 + ch * 0.02)));
    }

    public void StartDemo()
    {
        if (_demo is not null) return;
        ConfigureDefault();
        _run = true;
        var th = new Thread(DemoLoop) { IsBackground = true, Name = "PFS.Mcr" };
        _demo = th;
        th.Start();
        LastAction = "▶ MCR 데모 송출 시작";
        Raise(true);
    }

    public void StopDemo()
    {
        var th = Interlocked.Exchange(ref _demo, null);
        if (th is null) return;
        _run = false;
        th.Join(1000);
        LastAction = "⏹ MCR 데모 정지";
        Raise(true);
    }

    private void DemoLoop()
    {
        const int w = 160, h = 90;
        var luma = new byte[w * h];
        var audio = new float[1600];
        var frozenAt = new double[MaxChannels];
        var period = 1000.0 / Fps;
        var next = NowMs;
        while (_run)
        {
            var now = NowMs;
            try
            {
                for (var i = 0; i < _count; i++)
                {
                    var f = _ch[i].Fault;
                    if (f != InjectedFault.Freeze) frozenAt[i] = now;
                    if (f == InjectedFault.Loss) continue;
                    RenderDemo(i, now, luma, w, h, audio, f, frozenAt[i], _ch[i].Kind == McrSourceKind.Slate);
                    Ingest(i, luma, w, h, w, audio, now);
                }
                Tick(now);
            }
            catch (Exception ex) { PromptFlowStudio.Program.Log("Mcr.Tick", ex); }
            next += period;
            var wait = next - NowMs;
            if (wait < -period * 4) next = NowMs;
            else if (wait > 1) Thread.Sleep((int)wait);
        }
    }

    public static string HealthLabel(SignalHealth h) => h switch
    {
        SignalHealth.Ok => "정상", SignalHealth.Silent => "무음", SignalHealth.Frozen => "프리즈", SignalHealth.Black => "블랙", _ => "신호 유실",
    };

    public static string EventLabel(McrEventKind k) => k switch
    {
        McrEventKind.Take => "✂ TAKE", McrEventKind.AutoTake => "🤖 AI 컷", McrEventKind.Bypass => "🚨 바이패스", McrEventKind.Restore => "✅ 복귀",
        McrEventKind.Fault => "⚠ 장애", McrEventKind.Recover => "💚 회복", McrEventKind.Preview => "👁 PVW", _ => "📢 광고 큐",
    };

    private void Raise(bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now - Interlocked.Read(ref _lastRaise) < 250) return;
        Interlocked.Exchange(ref _lastRaise, now);
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Mcr.OnChange", ex); }
    }

    public void Dispose() => StopDemo();
}
