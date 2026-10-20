using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Inference;
using PromptFlowStudio.Services.Integration;

namespace PromptFlowStudio.Services.Agents;

/// <summary>[v9.2.3] 수집 신호 종류.</summary>
public enum CrashSignalKind
{
    /// <summary>처리되지 않은 런타임 예외.</summary>
    Exception = 0,
    /// <summary>관측되지 않은 Task 예외.</summary>
    UnobservedTask = 1,
    /// <summary>NPU/추론 병목(임계 초과 지연).</summary>
    NpuStall = 2,
    /// <summary>발열 스롯틀링 징후.</summary>
    ThermalThrottle = 3,
    /// <summary>메모리 압박(GC 세대 급증).</summary>
    MemoryPressure = 4,
}

/// <summary>[v9.2.3] 발열/스롯틀 등급.</summary>
public enum ThermalState
{
    /// <summary>정상.</summary>
    Nominal = 0,
    /// <summary>주의(지연 상승 관측).</summary>
    Warm = 1,
    /// <summary>스롯틀링 추정.</summary>
    Throttling = 2,
    /// <summary>임계(즉시 감속 권고).</summary>
    Critical = 3,
}

/// <summary>[v9.2.3] 링버퍼에서 읽어낸 신호 1건(조회 시점에만 문자열화 — 수집 경로는 무할당).</summary>
public sealed record CrashSignalView(
    long Sequence,
    CrashSignalKind Kind,
    string Source,
    double Value,
    DateTimeOffset At);

/// <summary>
/// [v9.2.3] 텔레메트리 반출 리포트.
/// ⚠ <see cref="Canonical"/> 에는 신호 목록뿐 아니라 <b>집계 카운트 전량</b>이 바인딩된다 — 누락 시 카운트 위조가 검출되지 않는다(v9 봉인 헌장).
/// </summary>
public sealed record CrashTelemetryReport(
    string ReportId,
    DateTimeOffset At,
    int ExceptionCount,
    int UnobservedCount,
    int NpuStallCount,
    int ThermalCount,
    int MemoryPressureCount,
    int DroppedCount,
    int SignalCount,
    ThermalState Thermal,
    double NpuLatencyMs,
    double PeakNpuLatencyMs,
    long ManagedHeapBytes,
    int Gen2Collections,
    string Canonical,
    string Digest,
    string Signature,
    long LedgerSequence);

/// <summary>
/// [PromptFlow Studio v9.2.3] 자율 크래시 &amp; NPU 텔레메트리 익스포터.
///   ▸ 런타임 예외·관측되지 않은 Task 예외·NPU 병목·발열 스롯틀링·메모리 압박을 <b>고정 크기 링버퍼</b>에 수집한다.
///   ▸ 수집 경로는 무할당 — 슬롯은 <c>struct</c> 배열, 메시지는 사전 할당 <c>char[]</c> 블록에 <see cref="Span{T}"/> 복사, 카운터는 <c>Interlocked</c>.
///   ▸ 반출 시 정규화 문자열(집계 카운트 포함)을 SHA-256 지문 + HMAC-SHA256 서명하고, WORM 원장(<see cref="QuantumResistantLedgerSigner"/>)에 하드바인딩한다.
///   ▸ 제3헌장: 전역 예외 훅과 2초 샘플 타이머는 <see cref="Dispose"/> 에서 반드시 해제한다.
///   ▸ 제5헌장: 원장/샘플러 미주입·실패는 예외 전파 없이 서명만 생략하고 수집을 계속한다.
/// </summary>
public sealed class AutonomousCrashTelemetryExporter : IDisposable
{
    private const int Capacity = 512;
    private const int MessageChars = 96;
    private const int SampleMs = 2_000;
    private const double NpuStallMs = 120d;
    private const double ThermalWarmRatio = 1.5d;
    private const double ThermalThrottleRatio = 2.2d;
    private const double ThermalCriticalRatio = 3.2d;

    private readonly Slot[] _slots = new Slot[Capacity];
    private readonly char[] _messages = new char[Capacity * MessageChars];
    private readonly byte[] _signKey;
    private readonly OnDeviceVisionInferenceEngine? _vision;
    private readonly MobileLowPowerOptimizer? _power;
    private readonly QuantumResistantLedgerSigner? _ledger;
    // CS0104 회피: WinForms 헤드의 System.Windows.Forms.Timer 와 충돌하므로 완전 한정명 고정.
    private readonly System.Threading.Timer _sampler;
    private readonly UnhandledExceptionEventHandler _domainHook;
    private readonly EventHandler<UnobservedTaskExceptionEventArgs> _taskHook;

    private long _sequence;
    private int _exceptions;
    private int _unobserved;
    private int _npuStalls;
    private int _thermal;
    private int _memoryPressure;
    private int _dropped;
    private int _exports;
    private double _baselineNpuMs;
    private double _emaNpuMs;
    private double _peakNpuMs;
    private bool _disposed;

    public AutonomousCrashTelemetryExporter(
        OnDeviceVisionInferenceEngine? vision = null,
        MobileLowPowerOptimizer? power = null,
        QuantumResistantLedgerSigner? ledger = null,
        string? signingSecret = null)
    {
        _vision = vision;
        _power = power;
        _ledger = ledger;
        _signKey = SHA256.HashData(Encoding.UTF8.GetBytes(
            string.IsNullOrWhiteSpace(signingSecret) ? "promptflow-crash-telemetry-v92" : signingSecret!));

        _domainHook = (_, e) => Capture(CrashSignalKind.Exception, "AppDomain",
            (e.ExceptionObject as Exception)?.Message ?? "unhandled", 0d);
        _taskHook = (_, e) =>
        {
            Capture(CrashSignalKind.UnobservedTask, "TaskScheduler", e.Exception.GetBaseException().Message, 0d);
            e.SetObserved();   // 프로세스 강제 종료 방지(관측 처리)
        };

        AppDomain.CurrentDomain.UnhandledException += _domainHook;
        TaskScheduler.UnobservedTaskException += _taskHook;

        _sampler = new System.Threading.Timer(_ => SampleTick(), null, SampleMs, SampleMs);
    }

    /// <summary>상태 변경 브로드캐스트(구독자는 <c>InvokeAsync(StateHasChanged)</c> 로 마셜링할 것).</summary>
    public event Action? OnChange;

    /// <summary>임계 신호(스롯틀 Critical·예외) 발생 알림.</summary>
    public event Action<CrashSignalView>? OnCriticalSignal;

    /// <summary>누적 예외 수.</summary>
    public int ExceptionCount => Volatile.Read(ref _exceptions);

    /// <summary>누적 미관측 Task 예외 수.</summary>
    public int UnobservedCount => Volatile.Read(ref _unobserved);

    /// <summary>누적 NPU 병목 수.</summary>
    public int NpuStallCount => Volatile.Read(ref _npuStalls);

    /// <summary>누적 발열 스롯틀 징후 수.</summary>
    public int ThermalEventCount => Volatile.Read(ref _thermal);

    /// <summary>누적 메모리 압박 수.</summary>
    public int MemoryPressureCount => Volatile.Read(ref _memoryPressure);

    /// <summary>링버퍼 상한 초과로 덮어쓴 신호 수.</summary>
    public int DroppedCount => Volatile.Read(ref _dropped);

    /// <summary>전체 수집 신호 수.</summary>
    public long TotalSignals => Interlocked.Read(ref _sequence);

    /// <summary>반출 누적 횟수.</summary>
    public int ExportCount => Volatile.Read(ref _exports);

    /// <summary>현재 발열/스롯틀 등급.</summary>
    public ThermalState Thermal { get; private set; } = ThermalState.Nominal;

    /// <summary>NPU/추론 지연 EWMA(ms).</summary>
    public double NpuLatencyMs => _emaNpuMs;

    /// <summary>관측된 최대 NPU 지연(ms).</summary>
    public double PeakNpuLatencyMs => _peakNpuMs;

    /// <summary>기준선 대비 지연 배율(스롯틀 판정 근거 · 기준선 미확보 시 1).</summary>
    public double ThermalRatio => _baselineNpuMs <= 0.0001d ? 1d : _emaNpuMs / _baselineNpuMs;

    /// <summary>관리 힙 사용량(바이트).</summary>
    public long ManagedHeapBytes => GC.GetTotalMemory(false);

    /// <summary>링버퍼 점유율(0~1).</summary>
    public double BufferFillRatio => Math.Clamp(Math.Min(TotalSignals, Capacity) / (double)Capacity, 0d, 1d);

    /// <summary>마지막 반출 리포트(없으면 null).</summary>
    public CrashTelemetryReport? LastReport { get; private set; }

    /// <summary>마지막 원장 바인딩 결과(미주입 시 null).</summary>
    public long? LastLedgerSequence { get; private set; }

    /// <summary>마지막 오류(정상 시 null).</summary>
    public string? LastError { get; private set; }

    /// <summary>마지막 동작 요약(HUD 1줄).</summary>
    public string? LastAction { get; private set; }

    /// <summary>건전성 — 임계 신호가 없고 스롯틀이 Nominal/Warm 이면 true.</summary>
    public bool IsHealthy => Thermal <= ThermalState.Warm && ExceptionCount == 0;

    /// <summary>최근 신호 스냅샷(최신순 · 조회 시점에만 문자열화).</summary>
    public IReadOnlyList<CrashSignalView> RecentSignals(int take = 12)
    {
        var total = Interlocked.Read(ref _sequence);
        if (total == 0) return Array.Empty<CrashSignalView>();

        var n = (int)Math.Min(Math.Clamp(take, 1, Capacity), total);
        var list = new List<CrashSignalView>(n);

        for (var i = 0; i < n; i++)
        {
            var seq = total - i;
            var idx = (int)((seq - 1) % Capacity);
            ref var slot = ref _slots[idx];
            if (slot.Sequence != seq) continue;   // 순회 중 덮어쓰기 경합 → 해당 건 생략

            var len = Math.Clamp(slot.MessageLength, 0, MessageChars);
            var text = len == 0 ? string.Empty : new string(_messages, idx * MessageChars, len);
            list.Add(new CrashSignalView(slot.Sequence, slot.Kind, text, slot.Value,
                DateTimeOffset.FromUnixTimeMilliseconds(slot.AtUnixMs)));
        }

        return list;
    }

    // ── 수집(무할당 경로) ──

    /// <summary>런타임 예외 보고.</summary>
    public void ReportException(Exception? ex, string source = "runtime")
        => Capture(CrashSignalKind.Exception, source, ex?.GetBaseException().Message ?? "unknown", 0d);

    /// <summary>추론/NPU 지연 보고. 임계 초과 시 병목 신호로 승격하고 스롯틀 등급을 재평가한다.</summary>
    public void ReportInferenceLatency(double ms, string source = "npu")
    {
        if (_disposed) return;

        var value = double.IsNaN(ms) || double.IsInfinity(ms) ? 0d : Math.Max(0d, ms);
        _emaNpuMs = _emaNpuMs <= 0d ? value : _emaNpuMs * 0.7d + value * 0.3d;
        if (value > _peakNpuMs) _peakNpuMs = value;
        if (_baselineNpuMs <= 0d && value > 0d) _baselineNpuMs = value;
        else if (value > 0d && value < _baselineNpuMs) _baselineNpuMs = _baselineNpuMs * 0.9d + value * 0.1d;

        if (value >= NpuStallMs)
        {
            Interlocked.Increment(ref _npuStalls);
            Capture(CrashSignalKind.NpuStall, source, "추론 지연 임계 초과", value);
        }

        EvaluateThermal();
    }

    /// <summary>발열 스롯틀 징후 직접 보고(플랫폼 센서 훅이 있을 때).</summary>
    public void ReportThermal(ThermalState state, string source = "thermal")
    {
        if (_disposed) return;

        if (state > Thermal) Thermal = state;
        Interlocked.Increment(ref _thermal);
        Capture(CrashSignalKind.ThermalThrottle, source, "스롯틀 " + state.ToString(), (double)state);
    }

    /// <summary>수집 상태 초기화(카운터·링버퍼 시퀀스 리셋 · 반출 이력은 보존).</summary>
    public void Reset()
    {
        if (_disposed) return;

        Interlocked.Exchange(ref _sequence, 0);
        Interlocked.Exchange(ref _exceptions, 0);
        Interlocked.Exchange(ref _unobserved, 0);
        Interlocked.Exchange(ref _npuStalls, 0);
        Interlocked.Exchange(ref _thermal, 0);
        Interlocked.Exchange(ref _memoryPressure, 0);
        Interlocked.Exchange(ref _dropped, 0);
        _emaNpuMs = 0d;
        _peakNpuMs = 0d;
        _baselineNpuMs = 0d;
        Thermal = ThermalState.Nominal;
        LastError = null;
        LastAction = "수집 초기화";
        Notify();
    }

    // ── 반출(SHA-256 지문 + HMAC 서명 + WORM 원장 바인딩) ──

    /// <summary>현재 집계를 리포트로 반출한다. 원장이 주입돼 있으면 해시를 WORM 원장에 추가 바인딩한다.</summary>
    public CrashTelemetryReport Export(string reportId = "")
    {
        var id = string.IsNullOrWhiteSpace(reportId) ? Guid.NewGuid().ToString("N")[..10] : reportId.Trim();
        var at = DateTimeOffset.UtcNow;
        var signals = RecentSignals(24);
        var gen2 = GC.CollectionCount(2);
        var heap = ManagedHeapBytes;

        var canonical = BuildCanonical(id, at, signals, gen2, heap);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        var signature = Convert.ToHexString(HMACSHA256.HashData(_signKey, Encoding.UTF8.GetBytes(canonical)));

        long ledgerSeq = -1;
        try
        {
            if (_ledger is not null)
            {
                var entry = _ledger.SignAndAppend(Encoding.UTF8.GetBytes(canonical), "crash-telemetry");
                ledgerSeq = entry.Seq;
                LastLedgerSequence = ledgerSeq;
            }
        }
        catch (Exception ex)
        {
            LastError = "원장 바인딩 실패: " + ex.Message;   // 반출 자체는 계속(제5헌장)
        }

        var report = new CrashTelemetryReport(
            id, at,
            ExceptionCount, UnobservedCount, NpuStallCount, ThermalEventCount, MemoryPressureCount,
            DroppedCount, signals.Count, Thermal,
            Math.Round(NpuLatencyMs, 2), Math.Round(PeakNpuLatencyMs, 2),
            heap, gen2, canonical, digest, signature, ledgerSeq);

        LastReport = report;
        Interlocked.Increment(ref _exports);
        LastAction = "반출 #" + ExportCount.ToString(CultureInfo.InvariantCulture)
            + " · 신호 " + signals.Count.ToString(CultureInfo.InvariantCulture)
            + (ledgerSeq >= 0 ? " · 원장 seq=" + ledgerSeq.ToString(CultureInfo.InvariantCulture) : " · 원장 미주입");
        Notify();
        return report;
    }

    /// <summary>리포트 위변조 검증 — 정규화 재계산 후 지문/서명을 상수시간 대조한다.</summary>
    public bool VerifyReport(CrashTelemetryReport? report)
    {
        if (report is null) return false;

        try
        {
            var bytes = Encoding.UTF8.GetBytes(report.Canonical);
            var digest = Convert.ToHexString(SHA256.HashData(bytes));
            if (!string.Equals(digest, report.Digest, StringComparison.OrdinalIgnoreCase)) return false;

            var expected = HMACSHA256.HashData(_signKey, bytes);
            var actual = Convert.FromHexString(report.Signature);
            if (actual.Length != expected.Length) return false;
            if (!CryptographicOperations.FixedTimeEquals(expected, actual)) return false;

            // 집계 위조 차단: 정규화에 바인딩된 카운트가 리포트 필드와 일치해야 한다.
            return report.Canonical.Contains(
                "agg=" + report.ExceptionCount.ToString(CultureInfo.InvariantCulture)
                + "/" + report.UnobservedCount.ToString(CultureInfo.InvariantCulture)
                + "/" + report.NpuStallCount.ToString(CultureInfo.InvariantCulture)
                + "/" + report.ThermalCount.ToString(CultureInfo.InvariantCulture)
                + "/" + report.MemoryPressureCount.ToString(CultureInfo.InvariantCulture)
                + "/" + report.DroppedCount.ToString(CultureInfo.InvariantCulture)
                + "/" + report.SignalCount.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        AppDomain.CurrentDomain.UnhandledException -= _domainHook;
        TaskScheduler.UnobservedTaskException -= _taskHook;
        _sampler.Dispose();

        OnChange = null;
        OnCriticalSignal = null;
    }

    // ── 내부 ──

    /// <summary>무할당 수집 — 슬롯 구조체 기록 + 사전 할당 char 블록 복사.</summary>
    private void Capture(CrashSignalKind kind, string source, string message, double value)
    {
        if (_disposed) return;

        switch (kind)
        {
            case CrashSignalKind.Exception: Interlocked.Increment(ref _exceptions); break;
            case CrashSignalKind.UnobservedTask: Interlocked.Increment(ref _unobserved); break;
            case CrashSignalKind.MemoryPressure: Interlocked.Increment(ref _memoryPressure); break;
            default: break;
        }

        var seq = Interlocked.Increment(ref _sequence);
        if (seq > Capacity) Interlocked.Increment(ref _dropped);

        var idx = (int)((seq - 1) % Capacity);
        var dst = _messages.AsSpan(idx * MessageChars, MessageChars);

        var src = (source ?? string.Empty).AsSpan();
        var msg = (message ?? string.Empty).AsSpan();
        var written = 0;

        written += CopyInto(dst, src, written);
        if (written < MessageChars) { dst[written++] = ':'; }
        if (written < MessageChars) { dst[written++] = ' '; }
        written += CopyInto(dst, msg, written);

        ref var slot = ref _slots[idx];
        slot.Sequence = seq;
        slot.Kind = kind;
        slot.Value = value;
        slot.MessageLength = written;
        slot.AtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (kind == CrashSignalKind.Exception || Thermal == ThermalState.Critical)
        {
            var view = new CrashSignalView(seq, kind,
                written == 0 ? string.Empty : new string(_messages, idx * MessageChars, written),
                value, DateTimeOffset.UtcNow);
            OnCriticalSignal?.Invoke(view);
        }

        Notify();
    }

    private static int CopyInto(Span<char> dst, ReadOnlySpan<char> src, int offset)
    {
        var room = dst.Length - offset;
        if (room <= 0 || src.Length == 0) return 0;

        var n = Math.Min(room, src.Length);
        src[..n].CopyTo(dst[offset..]);
        return n;
    }

    /// <summary>2초 샘플러 — 추론 지연/저전력 프로파일/GC 세대를 표집해 스롯틀·메모리 압박을 자율 판정한다.</summary>
    private void SampleTick()
    {
        if (_disposed) return;

        try
        {
            if (_vision is not null && _vision.TotalRuns > 0)
                ReportInferenceLatency(_vision.TileLatencyMs, "vision-npu");

            // 저전력 거버너가 최소 프로파일로 내려갔다면 발열/전력 스트레스로 간주한다.
            if (_power is not null && _power.Profile == MobilePowerProfile.Critical && Thermal < ThermalState.Throttling)
                ReportThermal(ThermalState.Throttling, "low-power");

            var gen2 = GC.CollectionCount(2);
            if (gen2 > _lastGen2)
            {
                _lastGen2 = gen2;
                if (GC.GetTotalMemory(false) > 512L * 1024L * 1024L)
                    Capture(CrashSignalKind.MemoryPressure, "gc", "Gen2 수집 + 힙 512MB 초과", gen2);
            }

            EvaluateThermal();
        }
        catch (Exception ex)
        {
            LastError = "샘플 실패: " + ex.Message;
        }
    }

    private int _lastGen2;

    /// <summary>기준선 대비 지연 배율로 스롯틀 등급을 재평가한다(상승은 즉시, 하강은 1단계씩 회복).</summary>
    private void EvaluateThermal()
    {
        var ratio = ThermalRatio;
        var next = ratio switch
        {
            >= ThermalCriticalRatio => ThermalState.Critical,
            >= ThermalThrottleRatio => ThermalState.Throttling,
            >= ThermalWarmRatio => ThermalState.Warm,
            _ => ThermalState.Nominal,
        };

        if (next == Thermal) return;

        if (next > Thermal)
        {
            Thermal = next;
            Interlocked.Increment(ref _thermal);
            LastAction = "스롯틀 격상 · " + next + " (지연 배율 " + ratio.ToString("0.0", CultureInfo.InvariantCulture) + "x)";
        }
        else
        {
            Thermal = (ThermalState)((int)Thermal - 1);   // 진동 방지: 1단계씩 회복
            LastAction = "스롯틀 완화 · " + Thermal;
        }

        Notify();
    }

    private void Notify() => OnChange?.Invoke();

    /// <summary>
    /// 정규화 페이로드 — ⚠ 신호 목록뿐 아니라 <b>집계 카운트 전량(agg=…)</b>과 런타임 지표를 함께 바인딩한다.
    /// 누락 시 카운트만 바꿔치기한 리포트가 서명 검증을 통과하는 보안 결함이 된다(v9 봉인 헌장).
    /// </summary>
    private string BuildCanonical(
        string id, DateTimeOffset at, IReadOnlyList<CrashSignalView> signals, int gen2, long heap)
    {
        var sb = new StringBuilder(256 + signals.Count * 48);
        sb.Append("pfs-crash-v9.2.3|id=").Append(id)
          .Append("|at=").Append(at.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture))
          .Append("|agg=").Append(ExceptionCount.ToString(CultureInfo.InvariantCulture))
          .Append('/').Append(UnobservedCount.ToString(CultureInfo.InvariantCulture))
          .Append('/').Append(NpuStallCount.ToString(CultureInfo.InvariantCulture))
          .Append('/').Append(ThermalEventCount.ToString(CultureInfo.InvariantCulture))
          .Append('/').Append(MemoryPressureCount.ToString(CultureInfo.InvariantCulture))
          .Append('/').Append(DroppedCount.ToString(CultureInfo.InvariantCulture))
          .Append('/').Append(signals.Count.ToString(CultureInfo.InvariantCulture))
          .Append("|total=").Append(TotalSignals.ToString(CultureInfo.InvariantCulture))
          .Append("|thermal=").Append(((int)Thermal).ToString(CultureInfo.InvariantCulture))
          .Append("|npu=").Append(NpuLatencyMs.ToString("0.00", CultureInfo.InvariantCulture))
          .Append('/').Append(PeakNpuLatencyMs.ToString("0.00", CultureInfo.InvariantCulture))
          .Append("|heap=").Append(heap.ToString(CultureInfo.InvariantCulture))
          .Append("|gen2=").Append(gen2.ToString(CultureInfo.InvariantCulture));

        for (var i = 0; i < signals.Count; i++)
        {
            var s = signals[i];
            sb.Append("|s").Append(s.Sequence.ToString(CultureInfo.InvariantCulture))
              .Append('=').Append(((int)s.Kind).ToString(CultureInfo.InvariantCulture))
              .Append(':').Append(s.Value.ToString("0.00", CultureInfo.InvariantCulture))
              .Append(':').Append(s.At.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>링버퍼 슬롯(구조체 — 수집 경로 힙 할당 0).</summary>
    private struct Slot
    {
        public long Sequence;
        public long AtUnixMs;
        public CrashSignalKind Kind;
        public double Value;
        public int MessageLength;
    }
}
