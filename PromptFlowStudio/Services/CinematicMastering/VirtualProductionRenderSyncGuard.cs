using System.Globalization;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.CinematicMastering;

/// <summary>Genlock 프레임 레이트(유리수 · 23.976 = 24000/1001).</summary>
public readonly record struct GenlockRate(int Num, int Den)
{
    public static readonly GenlockRate Fps23976 = new(24000, 1001), Fps24 = new(24, 1), Fps25 = new(25, 1), Fps2997 = new(30000, 1001), Fps30 = new(30, 1), Fps50 = new(50, 1), Fps5994 = new(60000, 1001), Fps60 = new(60, 1);
    public double Fps => Num / (double)Den;
    public double PeriodMs => 1000.0 * Den / Num;
    public int Nominal => (int)Math.Round(Fps);
    public bool DropFrame => Den == 1001 && (Num == 30000 || Num == 60000);
    public string Label => Den == 1 ? Num.ToString(CultureInfo.InvariantCulture) : Fps.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>SMPTE ST 12-1 타임코드(드롭 프레임 29.97/59.94 지원).</summary>
public readonly record struct SmpteTimecode(int Hours, int Minutes, int Seconds, int Frames, bool Drop)
{
    /// <summary>프레임 번호 → 타임코드(드롭 프레임: 매 분 첫 2/4 프레임 번호 건너뜀 · 10분 배수 제외).</summary>
    public static SmpteTimecode FromFrameNumber(long frame, GenlockRate rate)
    {
        var fps = rate.Nominal;
        if (rate.DropFrame)
        {
            var drop = fps / 15;
            long per10 = fps * 600L - drop * 9L, perMin = fps * 60L - drop;
            long d = frame / per10, m = frame % per10;
            frame += 9L * drop * d + (m > drop ? drop * ((m - drop) / perMin) : 0);
        }
        return new SmpteTimecode((int)(frame / (fps * 3600L) % 24), (int)(frame / (fps * 60L) % 60), (int)(frame / fps % 60), (int)(frame % fps), rate.DropFrame);
    }

    /// <summary>무할당 포맷 "HH:MM:SS:FF"(드롭 = ';') · 11자.</summary>
    public int Format(Span<char> dst)
    {
        if (dst.Length < 11) return 0;
        Two(dst, 0, Hours); dst[2] = ':'; Two(dst, 3, Minutes); dst[5] = ':'; Two(dst, 6, Seconds); dst[8] = Drop ? ';' : ':'; Two(dst, 9, Frames);
        return 11;
    }

    private static void Two(Span<char> d, int at, int v) { d[at] = (char)('0' + v / 10 % 10); d[at + 1] = (char)('0' + v % 10); }

    public override string ToString() { Span<char> b = stackalloc char[11]; return new string(b[..Format(b)]); }
}

public enum SyncVerdict : byte { Acquiring, Locked, HeldLate, SwapDeferred, Torn }

/// <summary>
/// [PromptFlow Studio v16.80.0] ⏱ 자율 가상 프로덕션 렌더링 동기화 &amp; 프레임 가드(무할당 루프).
/// Genlock 에지 → 주파수 동기 루프(PI · 락 판정 · 위상 지터 RMS) · SMPTE 타임코드.
/// 가드 ON: 스왑을 Genlock 슬롯(LED 위상)에 고정하고 카메라 셔터 개방(노출 + 롤링 리드아웃) 구간 밖 닫힌 창 중앙으로 LED 갱신 위상 자동 배치,
/// 데드라인 초과 프레임은 이전 프레임 홀드(반쪽 프레임 금지) → 티어링 차단. 가드 OFF: 렌더 완료 즉시 스왑(vsync 없음) → 셔터 창 겹침 = 티어.
/// LED PWM 리프레시 × 노출 정수배 검사(롤링 셔터 밴딩) · 권장 셔터 각.
/// </summary>
public sealed class VirtualProductionRenderSyncGuard
{
    public const int LockFrames = 8;

    private double _periodTicks, _periodEst, _predEdge, _integ, _jitterSq;
    private int _lockRun;
    private long _frameNo;

    public GenlockRate Rate { get; private set; } = GenlockRate.Fps23976;
    public double LedRefreshHz { get; private set; } = 3840;
    public float ShutterAngle { get; private set; } = 180f;
    public double ReadoutMs { get; private set; } = 8.0;
    public double ShutterPhaseMs { get; set; }
    public double LedProcessorLatencyMs { get; set; } = 1.0;
    public double DeadlineMarginMs { get; set; } = 1.0;
    public double LockToleranceUs { get; set; } = 150.0;
    public double Ki { get; set; } = 0.02;
    public bool Enabled { get; set; } = true;
    public bool AutoPhase { get; set; } = true;
    /// <summary>LED 프로세서 갱신 위상(Genlock 에지 기준 ms).</summary>
    public double LedPhaseMs { get; private set; }

    // ── 상태/계측 ──
    public SyncVerdict LastVerdict { get; private set; }
    public bool Locked => _lockRun >= LockFrames;
    public double PhaseErrorUs { get; private set; }
    public double JitterUs => Math.Sqrt(_jitterSq);
    public double PeriodEstimateMs => _periodEst / TimeSpan.TicksPerMillisecond;
    public double LastSwapPhaseMs { get; private set; }
    public double LastRenderSlackMs { get; private set; }
    public long Frames { get; private set; }
    public long LockedFrames { get; private set; }
    public long Held { get; private set; }
    public long Deferred { get; private set; }
    public long TearsPrevented { get; private set; }
    public long Tears { get; private set; }
    public SmpteTimecode Timecode { get; private set; }
    public string? LastAction { get; private set; }

    public VirtualProductionRenderSyncGuard() => Configure(GenlockRate.Fps23976, 3840, 180f, 8.0);

    public double PeriodMs => Rate.PeriodMs;
    public double ExposureMs => ShutterAngle / 360.0 * PeriodMs;
    /// <summary>셔터 개방(노출 + 롤링 리드아웃) 구간 길이.</summary>
    public double OpenWindowMs => ExposureMs + ReadoutMs;
    /// <summary>LED 를 갱신해도 안전한 닫힌 창(≤ 0 이면 회피 불가).</summary>
    public double ClosedWindowMs => PeriodMs - OpenWindowMs;
    public double LedUpdateMs => 1000.0 / LedRefreshHz;
    public bool Feasible => ClosedWindowMs > LedUpdateMs;
    /// <summary>노출 중 LED PWM 사이클 수(정수에 가까울수록 밴딩 없음).</summary>
    public double BandingCycles => ExposureMs * 0.001 * LedRefreshHz;
    /// <summary>롤링 셔터 밴딩 위험 0..1(소수 잔차 / 사이클 수 기반).</summary>
    public double BandingRisk { get { var c = BandingCycles; var res = Math.Abs(c - Math.Round(c)); return Math.Clamp(res / Math.Max(1.0, c) * 50.0, 0.0, 1.0); } }
    /// <summary>노출이 LED 사이클 정수배가 되는 가장 가까운 셔터 각.</summary>
    public double RecommendedShutterAngle { get { var c = Math.Max(1.0, Math.Round(BandingCycles)); return c / LedRefreshHz * 1000.0 / PeriodMs * 360.0; } }

    /// <summary>제어 평면: 레이트 · LED 리프레시 · 셔터 각 · 리드아웃(PLL/카운터 초기화).</summary>
    public void Configure(GenlockRate rate, double ledRefreshHz, float shutterAngle, double readoutMs)
    {
        Rate = rate; LedRefreshHz = Math.Max(60, ledRefreshHz); ShutterAngle = Math.Clamp(shutterAngle, 1f, 360f); ReadoutMs = Math.Max(0, readoutMs);
        _periodTicks = TimeSpan.TicksPerSecond * (double)rate.Den / rate.Num;
        ResetCounters();
        PlacePhase();
        LastAction = "⏱ Genlock " + rate.Label + " fps · LED " + LedRefreshHz.ToString("0", CultureInfo.InvariantCulture) + " Hz · 셔터 " + ShutterAngle.ToString("0.#", CultureInfo.InvariantCulture) + "°";
    }

    public void ResetCounters()
    {
        _periodEst = _periodTicks; _predEdge = 0; _integ = 0; _jitterSq = 0; _lockRun = 0; _frameNo = 0;
        Frames = LockedFrames = Held = Deferred = TearsPrevented = Tears = 0; PhaseErrorUs = 0;
        Timecode = SmpteTimecode.FromFrameNumber(0, Rate);
    }

    /// <summary>LED 갱신 위상 = 닫힌 창 중앙(셔터 닫힘 직후 ~ 다음 개방 전).</summary>
    public void PlacePhase()
    {
        if (!AutoPhase) return;
        var p = PeriodMs;
        LedPhaseMs = Mod(ShutterPhaseMs + OpenWindowMs + ClosedWindowMs * 0.5 - LedProcessorLatencyMs - LedUpdateMs * 0.5, p);
    }

    public void SetLedPhase(double ms) { AutoPhase = false; LedPhaseMs = Mod(ms, PeriodMs); }

    /// <summary>
    /// 핫패스: Genlock 에지 1개 처리(무할당). <paramref name="renderDoneTicks"/> = 이 슬롯에 표시할 프레임 렌더 완료 시각.
    /// </summary>
    public SyncVerdict Tick(long genlockEdgeTicks, long renderDoneTicks)
    {
        using var guard = ZeroGcRegionScope.Enter("VpSync.Tick");
        Frames++;
        // ── 주파수 동기 루프(PI) ──
        if (_predEdge == 0) { _predEdge = genlockEdgeTicks + _periodEst; _lockRun = 0; PhaseErrorUs = 0; }
        else
        {
            var err = genlockEdgeTicks - _predEdge;
            PhaseErrorUs = err / 10.0;
            _integ = Math.Clamp(_integ + err, -_periodTicks, _periodTicks);
            _periodEst = _periodTicks + Ki * _integ + 0.25 * err;
            _predEdge = genlockEdgeTicks + _periodEst;
            _jitterSq += (PhaseErrorUs * PhaseErrorUs - _jitterSq) * 0.05;
            _lockRun = Math.Abs(PhaseErrorUs) <= LockToleranceUs ? Math.Min(_lockRun + 1, 1 << 20) : 0;
        }
        if (Locked) LockedFrames++;
        Timecode = SmpteTimecode.FromFrameNumber(_frameNo++, Rate);

        var p = PeriodMs;
        var renderRel = (renderDoneTicks - genlockEdgeTicks) / (double)TimeSpan.TicksPerMillisecond;
        SyncVerdict v;
        if (Enabled)
        {
            // 스왑 = Genlock 슬롯 LED 위상(셔터 닫힌 창) 고정
            var deadline = LedPhaseMs - DeadlineMarginMs;
            LastRenderSlackMs = deadline - renderRel;
            LastSwapPhaseMs = LedPhaseMs;
            if (renderRel > deadline) { Held++; v = SyncVerdict.HeldLate; }
            else if (OverlapsShutter(Mod(renderRel, p) + LedProcessorLatencyMs, LedUpdateMs))
            {
                // 즉시 스왑했다면 노출 중 갱신 → 닫힌 창으로 지연
                Deferred++; TearsPrevented++; v = SyncVerdict.SwapDeferred;
            }
            else v = Locked ? SyncVerdict.Locked : SyncVerdict.Acquiring;
            if (!Feasible && v != SyncVerdict.HeldLate) { Tears++; v = SyncVerdict.Torn; }
        }
        else
        {
            // vsync 없음: 렌더 완료 즉시 스왑
            var sw = Mod(renderRel, p);
            LastSwapPhaseMs = sw; LastRenderSlackMs = 0;
            if (OverlapsShutter(sw + LedProcessorLatencyMs, LedUpdateMs)) { Tears++; v = SyncVerdict.Torn; }
            else v = Locked ? SyncVerdict.Locked : SyncVerdict.Acquiring;
        }
        LastVerdict = v;
        return v;
    }

    /// <summary>LED 갱신 구간 [start, start+len)(에지 기준 ms · 순환)이 셔터 개방 구간과 겹치는지.</summary>
    public bool OverlapsShutter(double startMs, double lenMs)
    {
        var p = PeriodMs;
        var s0 = Mod(ShutterPhaseMs, p); var s1 = s0 + OpenWindowMs;
        var a0 = Mod(startMs, p); var a1 = a0 + lenMs;
        for (var k = -1; k <= 1; k++)
        {
            var b0 = a0 + k * p; var b1 = a1 + k * p;
            if (b0 < s1 && s0 < b1) return true;
        }
        return false;
    }

    private static double Mod(double x, double p) { var r = x % p; return r < 0 ? r + p : r; }
}
