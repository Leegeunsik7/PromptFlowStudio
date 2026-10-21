using System.Numerics;
using PromptFlowStudio.Services.BroadcastMcr;
using PromptFlowStudio.Services.MotionTypography;
using PromptFlowStudio.Services.SplatEnvironment;

namespace PromptFlowStudio.Services.StationMaster;

[Flags]
public enum FailoverCause : byte { None = 0, LlmTimeout = 1, FrameDrop = 2, Network = 4, Hang = 8, Manual = 16 }
public enum FailoverState : byte { Normal, Standby, Recovering }

/// <summary>v17.70.0 MCR 스레드 정지(행) 시 워치독이 직접 밀어내는 스탠바이 프레임 소비자(송출 인코더 · 모니터) — 무할당.</summary>
public interface IStandbyFrameSink
{
    void OnStandbyFrame(ReadOnlySpan<byte> rgb, long frame);
}

/// <summary>
/// [PromptFlow Studio v17.70.0] 🛡 무중단 페일오버 &amp; 재난 방송 세이프티 가드(<see cref="IMcrStandbySource"/> · 핫패스 무할당).
///   ▸ 감시(MCR 틱 하트비트 · 60 Hz): ① LLM 추론 진행 시간 &gt; 500 ms ② 출력 프레임 주기 &gt; 33 ms 3 연속 ③ 네트워크 순단(보고/프로브) — 판정 즉시 스탠바이 무장
///     → 같은 틱 MCR 프레임이 예비 루프로 전면 대체(렌더 생략). ④ 워치독(1 ms 폴링 · AboveNormal): 하트비트 공백 &gt; 50 ms = 행 → <see cref="IStandbyFrameSink"/> 직송.
///   ▸ 전환 지연 = 조건 성립 시각(LLM 시작 + 500 ms · 3 번째 초과 프레임 관측 · 순단 보고) → 첫 스탠바이 프레임. 50 ms 이내 = 성공(<see cref="OnTimeRatio"/>).
///   ▸ 예비 콘텐츠: v17.55 3DGS 장면(4 만 스플랫) 오빗 48 프레임 핑퐁 루프(320×192 RGB · 사전 베이크) + v17.52 SDF 대기 안내 자막 번인 · 베이크 실패 시 BCL 그라데이션 폴백.
///   ▸ 복구: 원인 해소 후 <see cref="HoldMs"/>(재발 시 2 배 · 최대 60 s) 유지 → 24 프레임 디졸브 복귀(256 → 0).
/// </summary>
public sealed class ZeroDowntimeFailoverDisasterGuard : IMcrStandbySource, IDisposable
{
    public const int W = UnifiedGaussianSceneCompositorEngine.W, H = UnifiedGaussianSceneCompositorEngine.H, FrameBytes = W * H * 3, LoopFrames = 48, RecoverFrames = 24, LogCap = 32;
    public const double LlmTimeoutMs = 500, SlowFrameMs = 33, SwitchBudgetMs = 50, HangMs = 50;
    public const int SlowStreak = 3;

    public struct Incident
    {
        public FailoverCause Cause;
        public double AtMs, SwitchMs, DurationMs;
        public bool OnTime;
    }

    private readonly byte[][] _loop = new byte[LoopFrames][];
    private readonly Incident[] _log = new Incident[LogCap];
    private readonly object _bake = new();
    private readonly double _tickMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    private long _lastBeat, _llmStart, _condTs, _standbySince, _clearSince, _netDownSince;
    private int _slow, _recoverLeft, _logHead, _logN, _causes;
    private volatile int _state;
    private volatile bool _netDown, _manual;
    private int _pendingSwitch;
    private double _holdMs;
    private Thread? _watchdog;
    private volatile bool _watching;
    private long _hangFrame;

    private double _baseHold = 3_000;

    public ZeroDowntimeFailoverDisasterGuard() => _holdMs = _baseHold;

    /// <summary>원인 해소 후 복귀 보류(재발 시 2 배 백오프 기준값) — 설정 즉시 현재 보류에 반영.</summary>
    public double HoldMs { get => _baseHold; set { _baseHold = Math.Clamp(value, 0, 600_000); _holdMs = _baseHold; } }
    public double MaxHoldMs { get; set; } = 60_000;
    /// <summary>네트워크 상태 프로브(하트비트마다 호출 · 캐시 델리게이트 · true = 정상).</summary>
    public Func<bool>? NetworkProbe { get; set; }
    public IStandbyFrameSink? HangSink { get; set; }
    public bool Enabled { get; set; } = true;
    public FailoverState State => (FailoverState)_state;
    public bool StandbyEngaged => _state != (int)FailoverState.Normal;
    /// <summary>IMcrStandbySource — 전면 대체 중(복귀 디졸브 제외).</summary>
    public bool Engaged => _state == (int)FailoverState.Standby && Baked;
    public FailoverCause ActiveCauses => (FailoverCause)Volatile.Read(ref _causes);
    public FailoverCause LastCause { get; private set; }
    public bool Baked { get; private set; }
    public bool BakeFallback { get; private set; }
    public double BakeMs { get; private set; }
    public long Heartbeats { get; private set; }
    public long Switches { get; private set; }
    public long OnTimeSwitches { get; private set; }
    public double LastSwitchMs { get; private set; }
    public double MaxSwitchMs { get; private set; }
    public long StandbyFrames { get; private set; }
    public long HangFrames { get; private set; }
    public long Recoveries { get; private set; }
    public long LlmTimeouts { get; private set; }
    public long FrameDropTrips { get; private set; }
    public long NetworkTrips { get; private set; }
    public long HangTrips { get; private set; }
    public long LlmRequests { get; private set; }
    public double LastFramePeriodMs { get; private set; }
    public double MaxFramePeriodMs { get; private set; }
    public double LastBeatUs { get; private set; }
    public double CurrentHoldMs => _holdMs;
    public double OnTimeRatio => Switches > 0 ? (double)OnTimeSwitches / Switches : 1.0;
    public double StandbyAgeMs => _state == 0 ? 0 : (Now() - _standbySince) * _tickMs;
    public bool LlmPending => Volatile.Read(ref _llmStart) != 0;
    public double LlmAgeMs { get { var s = Volatile.Read(ref _llmStart); return s == 0 ? 0 : (Now() - s) * _tickMs; } }
    public int IncidentCount => _logN;
    public ref readonly Incident IncidentAt(int i) => ref _log[(_logHead - 1 - i + 2 * LogCap) % LogCap];
    public ReadOnlySpan<byte> LoopFrame(int i) => _loop[i] ?? ReadOnlySpan<byte>.Empty;
    public bool WatchdogRunning => _watching;

    public static string CauseName(FailoverCause c) => c switch
    {
        FailoverCause.LlmTimeout => "LLM 지연", FailoverCause.FrameDrop => "프레임 드롭", FailoverCause.Network => "네트워크 순단", FailoverCause.Hang => "렌더 정지",
        FailoverCause.Manual => "수동", FailoverCause.None => "정상", _ => "복합",
    };

    public static string StateName(FailoverState s) => s switch { FailoverState.Standby => "🛟 스탠바이 송출", FailoverState.Recovering => "↩ 복귀 디졸브", _ => "✔ 정상 송출" };

    private static long Now() => System.Diagnostics.Stopwatch.GetTimestamp();

    // ══════════════════════ 예비 루프 베이크(비핫패스) ══════════════════════
    /// <summary>v17.55 3DGS 오빗 루프 + SDF 안내 자막 사전 베이크(실패 시 BCL 그라데이션 폴백 · [부분 구제]).</summary>
    public void Bake(int splats = 40_000, string caption = "잠시 후 방송이 이어집니다", string sub = "PromptFlow 24/7 Station")
    {
        var t0 = Now();
        lock (_bake)
        {
            for (var i = 0; i < LoopFrames; i++) _loop[i] ??= new byte[FrameBytes];
            var ok = false;
            try
            {
                var scene = new GaussianSplatScene(splats);
                scene.Generate(splats, 0xA11B1E57u);
                using var raster = new RealtimeGaussianSplatRasterizerEngine(1 << 16, 1 << 18, 2);
                var guard = new SplatFrustumCullingVramBudgetGuard();
                var sh = new SphericalHarmonicsLightingSynthesizer();
                var aspect = (float)W / H;
                for (var f = 0; f < LoopFrames; f++)
                {
                    var a = 0.6f * MathF.Sin(f / (float)LoopFrames * MathF.PI * 2f);   // 진자 오빗 → 핑퐁 이음매 무단차
                    var cam = SplatCamera.LookAt(new Vector3(4.8f * MathF.Sin(a), 1.9f, 4.8f * MathF.Cos(a)), GaussianSplatScene.SphereCenter, 45f, aspect, 0.1f, 120f);
                    raster.Render(scene, in cam, guard, sh);
                    var dst = _loop[f];
                    for (var y = 0; y < H; y++)
                        for (var x = 0; x < W; x++)
                        {
                            var c = raster.Pixel(x, y); var o = (y * W + x) * 3;
                            dst[o] = UnifiedGaussianSceneCompositorEngine.Tone(c.X * 0.8f); dst[o + 1] = UnifiedGaussianSceneCompositorEngine.Tone(c.Y * 0.8f); dst[o + 2] = UnifiedGaussianSceneCompositorEngine.Tone(c.Z * 0.9f);
                        }
                }
                ok = true;
            }
            catch (Exception ex) when (ex is OutOfMemoryException or InvalidOperationException or ArgumentException or IndexOutOfRangeException)
            {
                PromptFlowStudio.Program.Log("Failover.Bake3dgs", ex);
            }
            if (!ok) Gradient();
            BakeFallback = !ok;
            BurnCaption(caption, sub);
            Baked = true;
        }
        BakeMs = (Now() - t0) * _tickMs;
    }

    private void Gradient()
    {
        for (var f = 0; f < LoopFrames; f++)
        {
            var dst = _loop[f]; var ph = f / (float)LoopFrames * MathF.PI * 2f;
            for (var y = 0; y < H; y++)
                for (var x = 0; x < W; x++)
                {
                    var o = (y * W + x) * 3; var v = 0.5f + 0.5f * MathF.Sin(x * 0.02f + ph) * MathF.Cos(y * 0.03f);
                    dst[o] = (byte)(20 + 30 * v); dst[o + 1] = (byte)(30 + 40 * v); dst[o + 2] = (byte)(60 + 70 * v);
                }
        }
    }

    private void BurnCaption(string caption, string sub)
    {
        try
        {
            var atlas = new SdfGlyphAtlas();
            atlas.Ensure(caption); atlas.Ensure(sub);
            var layer = new RgbaOverlayLayer(W, H);
            var plate = new PxRect(10, H - 62, W - 20, 52);
            layer.FillRoundRect(in plate, 10f, 0.02f, 0.03f, 0.08f, 0.72f);
            var probe = new RgbaOverlayLayer(W, H);
            var b1 = SdfText.Draw(atlas, probe, caption, 0, 0, 22f, 1f, 1f, 1f);
            var b2 = SdfText.Draw(atlas, probe, sub, 0, 0, 12f, 1f, 1f, 1f);
            SdfText.Draw(atlas, layer, caption, (W - b1.W) * 0.5f - b1.X, H - 58, 22f, 1f, 0.9f, 0.2f);
            SdfText.Draw(atlas, layer, sub, (W - b2.W) * 0.5f - b2.X, H - 30, 12f, 0.75f, 0.85f, 1f);
            var px = layer.Pixels;
            for (var f = 0; f < LoopFrames; f++)
            {
                var dst = _loop[f];
                for (var p = 0; p < W * H; p++)
                {
                    var a = px[p * 4 + 3];
                    if (a == 0) continue;
                    var inv = 255 - a; var o = p * 3;
                    dst[o] = (byte)((dst[o] * inv) / 255 + px[p * 4]); dst[o + 1] = (byte)((dst[o + 1] * inv) / 255 + px[p * 4 + 1]); dst[o + 2] = (byte)((dst[o + 2] * inv) / 255 + px[p * 4 + 2]);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or PlatformNotSupportedException or TypeInitializationException)
        {
            PromptFlowStudio.Program.Log("Failover.Caption", ex);
        }
    }

    // ══════════════════════ 감시 입력(무할당) ══════════════════════
    /// <summary>LLM 추론 시작(세그먼트 대본 · 애드리브) — 진행 중 1 건 추적.</summary>
    public void LlmBegin() { Interlocked.Exchange(ref _llmStart, Now()); LlmRequests++; }
    /// <summary>LLM 응답(성공 · 폴백) 수신 — LLM 원인 해제.</summary>
    public void LlmEnd() { Interlocked.Exchange(ref _llmStart, 0); Clear(FailoverCause.LlmTimeout); }

    /// <summary>네트워크 순단 보고(true = 정상 복구).</summary>
    public void ReportNetwork(bool up)
    {
        if (!up && !_netDown) { _netDown = true; _netDownSince = Now(); }
        else if (up && _netDown) { _netDown = false; Clear(FailoverCause.Network); }
    }

    public void SetManual(bool on) { _manual = on; if (on) Trip(FailoverCause.Manual, Now()); else Clear(FailoverCause.Manual); }

    /// <summary>MCR 틱 하트비트(프레임 직전 · MCR 스레드) — 출력 주기 · LLM · 네트워크 판정 → 즉시 무장(같은 틱 프레임 대체).</summary>
    public void Heartbeat()
    {
        var now = Now();
        var prev = Interlocked.Exchange(ref _lastBeat, now);
        Heartbeats++;
        if (!Enabled) return;
        if (Interlocked.Exchange(ref _hangFrame, 0) > 0) Clear(FailoverCause.Hang);
        if (prev != 0)
        {
            var period = (now - prev) * _tickMs;
            LastFramePeriodMs = period; if (period > MaxFramePeriodMs) MaxFramePeriodMs = period;
            if (period > SlowFrameMs) { if (++_slow >= SlowStreak && (_causes & (int)FailoverCause.FrameDrop) == 0) { FrameDropTrips++; Trip(FailoverCause.FrameDrop, now); } }
            else { _slow = 0; if ((_causes & (int)FailoverCause.FrameDrop) != 0 && period < SlowFrameMs * 0.75) Clear(FailoverCause.FrameDrop); }
        }
        var llm = Volatile.Read(ref _llmStart);
        if (llm != 0 && (now - llm) * _tickMs > LlmTimeoutMs && (_causes & (int)FailoverCause.LlmTimeout) == 0)
        {
            LlmTimeouts++;
            Trip(FailoverCause.LlmTimeout, llm + (long)(LlmTimeoutMs / _tickMs));
        }
        if (NetworkProbe is { } probe)
        {
            bool up;
            try { up = probe(); } catch (Exception ex) { up = true; PromptFlowStudio.Program.Log("Failover.NetProbe", ex); }
            ReportNetwork(up);
        }
        if (_netDown && (_causes & (int)FailoverCause.Network) == 0) { NetworkTrips++; Trip(FailoverCause.Network, _netDownSince); }
        var us = (Now() - now) * _tickMs * 1000;
        LastBeatUs = us;
    }

    private void Trip(FailoverCause c, long condTs)
    {
        Interlocked.Or(ref _causes, (int)c);
        LastCause = c;
        _clearSince = 0;
        if (_state == (int)FailoverState.Standby) return;   // 이미 스탠바이 송출 중 → 원인만 추가
        if (_state == (int)FailoverState.Recovering) _holdMs = Math.Min(MaxHoldMs, _holdMs * 2);   // 복귀 중 재발 → 보류 연장
        _condTs = condTs; _recoverLeft = 0;
        Volatile.Write(ref _pendingSwitch, 1);
        _standbySince = Now();
        _state = (int)FailoverState.Standby;
    }

    private void Clear(FailoverCause c)
    {
        var after = Interlocked.And(ref _causes, ~(int)c) & ~(int)c;
        if (after == 0 && _state == (int)FailoverState.Standby && _clearSince == 0) _clearSince = Now();
    }

    // ══════════════════════ IMcrStandbySource(MCR 프레임 · 무할당) ══════════════════════
    public int NextWeight(long frame)
    {
        var st = _state;
        if (st == (int)FailoverState.Normal) return 0;
        if (st == (int)FailoverState.Standby)
        {
            if (Baked) Switched();
            if (_causes == 0 && !_manual && _clearSince != 0 && (Now() - _clearSince) * _tickMs >= _holdMs)
            {
                _state = (int)FailoverState.Recovering; _recoverLeft = RecoverFrames;
            }
            else return Baked ? 256 : 0;
        }
        if (--_recoverLeft <= 0)
        {
            _state = (int)FailoverState.Normal; Recoveries++;
            if (_logN > 0) _log[(_logHead - 1 + LogCap) % LogCap].DurationMs = (Now() - _standbySince) * _tickMs;
            _holdMs = Math.Max(HoldMs, _holdMs * 0.75);
            return 0;
        }
        return Baked ? 256 * _recoverLeft / RecoverFrames : 0;
    }

    private void Switched()
    {
        if (Interlocked.Exchange(ref _pendingSwitch, 0) == 0) return;
        var ms = Math.Max(0, (Now() - _condTs) * _tickMs);
        Switches++; LastSwitchMs = ms; if (ms > MaxSwitchMs) MaxSwitchMs = ms;
        var ok = ms <= SwitchBudgetMs; if (ok) OnTimeSwitches++;
        ref var e = ref _log[_logHead];
        e.Cause = LastCause; e.AtMs = Environment.TickCount64; e.SwitchMs = ms; e.OnTime = ok; e.DurationMs = 0;
        _logHead = (_logHead + 1) % LogCap; if (_logN < LogCap) _logN++;
    }

    public void Write(Span<byte> rgb, long frame)
    {
        var src = Frame(frame);
        if (src is null) return;
        src.AsSpan(0, Math.Min(rgb.Length, FrameBytes)).CopyTo(rgb);
        StandbyFrames++;
    }

    public void Blend(Span<byte> rgb, int w256, long frame)
    {
        var src = Frame(frame);
        if (src is null) return;
        BroadcastMcrMasterControlSynthesizer.Mix(rgb, src.AsSpan(0, Math.Min(rgb.Length, FrameBytes)), rgb, w256);
    }

    private byte[]? Frame(long frame)
    {
        var k = (int)(frame % (2 * LoopFrames - 2));
        if (k >= LoopFrames) k = 2 * LoopFrames - 2 - k;   // 핑퐁
        return _loop[k];
    }

    // ══════════════════════ 워치독(행 감지) ══════════════════════
    public void StartWatchdog()
    {
        if (_watching) return;
        _watching = true;
        _watchdog = new Thread(Watch) { IsBackground = true, Name = "PF.Station.Watchdog" };
        try { _watchdog.Priority = ThreadPriority.AboveNormal; } catch (PlatformNotSupportedException) { }
        _watchdog.Start();
    }

    public void StopWatchdog()
    {
        _watching = false;
        var w = _watchdog; _watchdog = null;
        if (w is not null && w != Thread.CurrentThread) w.Join(1000);
    }

    private void Watch()
    {
        long frame = 0; var nextEmit = 0L; var period = (long)(1000.0 / 60 / _tickMs);
        while (_watching)
        {
            try
            {
                var beat = Volatile.Read(ref _lastBeat);
                var now = Now();
                if (Enabled && beat != 0 && (now - beat) * _tickMs > HangMs && Volatile.Read(ref _lastBeat) == beat)   // 재확인 — 판독 직후 하트비트 갱신 경쟁 배제
                {
                    if ((_causes & (int)FailoverCause.Hang) == 0) { HangTrips++; Trip(FailoverCause.Hang, beat + (long)(HangMs / _tickMs)); nextEmit = 0; }
                    if (now >= nextEmit && Baked)
                    {
                        Switched();
                        var src = Frame(frame++);
                        if (src is not null) { HangSink?.OnStandbyFrame(src, frame); HangFrames++; }
                        Interlocked.Increment(ref _hangFrame);
                        nextEmit = now + period;
                    }
                }
            }
            catch (Exception ex) { PromptFlowStudio.Program.Log("Failover.Watchdog", ex); }
            Thread.Sleep(1);
        }
    }

    public void Dispose() => StopWatchdog();
}
