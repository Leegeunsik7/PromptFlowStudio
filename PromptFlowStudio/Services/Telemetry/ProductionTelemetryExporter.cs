using System;
using System.Buffers;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using PromptFlowStudio.Services.Security;   // SecureSecretVault(v10.5.0-F1)

namespace PromptFlowStudio.Services.Telemetry;

public enum ExportCircuitState { Closed, Open, HalfOpen }

public sealed record ExportBatchResult(bool Sent, int RecordCount, int MaskedFields, string PayloadDigest8, string BatchSignature8, double ElapsedMs);

public sealed record ExportCircuitView(ExportCircuitState State, int ConsecutiveFailures, long Sent, long Failed, long Buffered, double CooldownSec);

/// <summary>서킷브레이커 상태 전이 1건(무변화 틱은 기록 안 함) — v10.5.0-F4 OTLP Visualizer 결선용.</summary>
public sealed record CircuitTransitionView(ExportCircuitState From, ExportCircuitState To, DateTimeOffset At);

/// <summary>
/// v10.0-LTS(LTS-10.0.2) 제로트러스트 프로덕션 텔레메트리 익스포터 — <see cref="EnterpriseAuditTelemetryAggregator"/>의
/// 신규 원장 엔트리를 폴링해 PII(이메일/전화/사설 IPv4 등)를 정규식으로 자동 마스킹한 뒤 OTLP(OpenTelemetry Protocol)
/// Logs 데이터 모델 호환 JSON 배치로 직렬화하고 HMAC 배치서명(제로트러스트 발신 무결성 · PQC 스텁)을 첨부해 원격 릴레이로
/// 내보낸다. 네트워크 실패가 누적되면 서킷 브레이커(Closed→Open→HalfOpen)가 트립되어 전송을 유예하고 버퍼에
/// 적재(백프레셔), 쿨다운 경과 후 반개방 프로브로 자율 재개한다. 순수 BCL(System.Text.Json/RegularExpressions/Cryptography).
/// </summary>
public sealed class ProductionTelemetryExporter : IDisposable
{
    private const int FailureThreshold = 3;
    private const double CooldownSec = 6;
    private const int BufferCap = 64;
    private const int HistoryCap = 64;   // v10.5.0-F4: Visualizer 기본 조회창(50건)보다 여유있게 보관

    // v10.5.0-F1: 하드코딩 리터럴 → SecureSecretVault 이관(환경변수→DPAPI→Graceful 폴백). CLAUDE.md Rule 7 소급 적용.
    private readonly byte[] SignKey;
    private static readonly Regex EmailRx = new(@"[\w\.\-]+@[\w\-]+\.[\w\.\-]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Ipv4Rx = new(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PhoneRx = new(@"\b01[0-9]-?\d{3,4}-?\d{4}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly EnterpriseAuditTelemetryAggregator _aggregator;
    private readonly object _gate = new();
    private readonly Queue<AuditEntryView> _buffer = new();
    // v10.5.0-F4: OtlpTelemetryVisualizerProvider 구독용 — 배치 결과/서킷 전이 이력(Flush()·Tick() 공용 경로에서만 적재).
    //   CircularBuffer<T> 자체가 스레드 세이프(내부 락)라 프로퍼티 게터는 _gate 밖에서도 안전하게 스냅샷 가능.
    private readonly CircularBuffer<ExportBatchResult> _history = new(HistoryCap);
    private readonly CircularBuffer<CircuitTransitionView> _transitions = new(HistoryCap);
    private readonly Random _rng = new(10020);
    // v10.4.0 Zero-Alloc(CLAUDE.md 규칙6): 플러시마다 재생성하던 익명타입 그래프+List<object>+JsonSerializer.Serialize
    // 대신 재사용 버퍼+Utf8JsonWriter 로 직접 토큰 스트리밍 — 안정 상태(버퍼 용량 고정 후)에서 플러시당 할당 0에 수렴.
    private readonly ArrayBufferWriter<byte> _jsonBuf = new(4096);
    private readonly Utf8JsonWriter _jsonWriter;
    private System.Threading.Timer? _timer;
    private long _lastExportedSeq;
    private ExportCircuitState _state = ExportCircuitState.Closed;
    private int _consecutiveFailures;
    private DateTimeOffset _openedUntil;
    private bool _forceNetworkFault;
    private long _sent, _failed;
    private bool _disposed;

    public event Action? OnChange;

    public ProductionTelemetryExporter(EnterpriseAuditTelemetryAggregator aggregator, SecureSecretVault vault)
    {
        _aggregator = aggregator;
        SignKey = vault.ResolveOrCreate("production-telemetry-exporter");
        _aggregator.OnChange += OnAggregatorChanged;
        _jsonWriter = new Utf8JsonWriter(_jsonBuf);
    }

    public bool DemoRunning => _timer is not null;
    public string? LastAction { get; private set; }
    public long Sent => Interlocked.Read(ref _sent);
    public long Failed => Interlocked.Read(ref _failed);
    public int BufferedCount { get { lock (_gate) return _buffer.Count; } }

    /// <summary>최근 배치 결과(최신순 스냅샷) — v10.5.0-F4 Visualizer 바인딩용.</summary>
    public IReadOnlyList<ExportBatchResult> RecentBatches => _history.SnapshotNewestFirst();
    /// <summary>최근 서킷브레이커 상태 전이(최신순 스냅샷) — 무변화 틱은 포함되지 않음.</summary>
    public IReadOnlyList<CircuitTransitionView> RecentTransitions => _transitions.SnapshotNewestFirst();

    public ExportCircuitView Circuit
    {
        get
        {
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                return new ExportCircuitView(_state, _consecutiveFailures, Interlocked.Read(ref _sent), Interlocked.Read(ref _failed), _buffer.Count,
                    _state == ExportCircuitState.Open ? Math.Max(0, (_openedUntil - now).TotalSeconds) : 0);
            }
        }
    }

    // 신규 원장 엔트리를 큐에 적재만(무거운 전송은 타이머 틱/명시적 Flush 에서 처리 · 콜백 스레드 블로킹 회피).
    private void OnAggregatorChanged()
    {
        lock (_gate)
        {
            foreach (var e in _aggregator.RecentEntries)
            {
                if (e.Seq <= _lastExportedSeq) break;   // 최신순 리스트 — 이미 반영된 지점 도달 시 중단
                if (_buffer.Count >= BufferCap) _buffer.Dequeue();   // 백프레셔 상한(가장 오래된 대기분 폐기)
                _buffer.Enqueue(e);
            }
            if (_buffer.Count > 0) _lastExportedSeq = MaxSeqLocked();
        }
        OnChange?.Invoke();
    }

    private long MaxSeqLocked()
    {
        long max = _lastExportedSeq;
        foreach (var e in _buffer) if (e.Seq > max) max = e.Seq;
        return max;
    }

    /// <summary>버퍼 대기분을 즉시 배치 전송 시도(서킷 Open 이면 유예).</summary>
    public ExportBatchResult Flush()
    {
        ExportBatchResult r;
        lock (_gate) { var before = _state; r = FlushLocked(); RecordHistoryLocked(r, before); }
        OnChange?.Invoke();
        return r;
    }

    // v10.5.0-F4: Flush()/Tick() 공용 이력 적재 — FlushLocked() 자체는 무변경(기존 검증 로직 보존).
    private void RecordHistoryLocked(ExportBatchResult r, ExportCircuitState before)
    {
        _history.Add(r);
        if (_state != before) _transitions.Add(new CircuitTransitionView(before, _state, DateTimeOffset.UtcNow));
    }

    private ExportBatchResult FlushLocked()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var now = DateTimeOffset.UtcNow;
        if (_state == ExportCircuitState.Open && now < _openedUntil)
        {
            sw.Stop();
            LastAction = $"서킷 Open — 전송 유예(버퍼 {_buffer.Count}건)";
            return new ExportBatchResult(false, 0, 0, "--------", "--------", sw.Elapsed.TotalMilliseconds);
        }
        if (_state == ExportCircuitState.Open) _state = ExportCircuitState.HalfOpen;
        if (_buffer.Count == 0)
        {
            sw.Stop();
            return new ExportBatchResult(true, 0, 0, "--------", "--------", sw.Elapsed.TotalMilliseconds);
        }

        var batch = new List<AuditEntryView>(_buffer);
        var masked = 0;
        var digest = WriteOtlpBatchAndHash(batch, ref masked);   // Zero-Alloc: 익명타입 그래프 없이 Utf8JsonWriter 직접 스트리밍
        var signature = HMACSHA256.HashData(SignKey, digest);

        var networkOk = SimulateNetworkSendLocked();
        sw.Stop();
        if (!networkOk)
        {
            _failed++; _consecutiveFailures++;
            if (_consecutiveFailures >= FailureThreshold && _state != ExportCircuitState.Open)
            {
                _state = ExportCircuitState.Open; _openedUntil = now.AddSeconds(CooldownSec);
                LastAction = $"연속 전송실패 {_consecutiveFailures}회 — 서킷 Open({CooldownSec:0}s)";
            }
            else LastAction = "네트워크 전송 실패 — 버퍼 보존";
            return new ExportBatchResult(false, batch.Count, masked, Hex8(digest), Hex8(signature), sw.Elapsed.TotalMilliseconds);
        }

        for (var i = 0; i < batch.Count; i++) _buffer.Dequeue();
        _sent += batch.Count; _consecutiveFailures = 0;
        LastAction = _state != ExportCircuitState.Closed
            ? "HalfOpen 프로브 성공 — Closed 복귀"
            : $"배치 전송 완료 · {batch.Count}건 · 마스킹 {masked}건";
        _state = ExportCircuitState.Closed;
        return new ExportBatchResult(true, batch.Count, masked, Hex8(digest), Hex8(signature), sw.Elapsed.TotalMilliseconds);
    }

    // v10.4.0 Zero-Alloc OTLP 직렬화 — 재사용 ArrayBufferWriter+Utf8JsonWriter 에 토큰을 직접 기록(중간 object 그래프/리스트 0).
    // 반환된 다이제스트는 WrittenSpan(재사용 버퍼의 논스탈릭 뷰)을 그대로 해시 — 별도 byte[] 스냅샷도 만들지 않는다.
    private byte[] WriteOtlpBatchAndHash(List<AuditEntryView> batch, ref int masked)
    {
        _jsonBuf.Clear();
        _jsonWriter.Reset();

        _jsonWriter.WriteStartObject();
        _jsonWriter.WriteStartArray("resourceLogs");
        _jsonWriter.WriteStartObject();
        _jsonWriter.WriteStartObject("resource");
        _jsonWriter.WriteStartArray("attributes");
        WriteAttr(_jsonWriter, "service.name", "promptflow-studio");
        _jsonWriter.WriteEndArray();
        _jsonWriter.WriteEndObject();
        _jsonWriter.WriteStartArray("scopeLogs");
        _jsonWriter.WriteStartObject();
        _jsonWriter.WriteStartObject("scope");
        _jsonWriter.WriteString("name", "PromptFlowStudio.ProductionTelemetryExporter");
        _jsonWriter.WriteEndObject();
        _jsonWriter.WriteStartArray("logRecords");
        foreach (var e in batch)
        {
            var localMasked = 0;
            var body = Mask(e.Action, ref localMasked) + " → " + Mask(e.Resource, ref localMasked);
            masked += localMasked;
            _jsonWriter.WriteStartObject();
            _jsonWriter.WriteNumber("timeUnixNano", e.At.ToUnixTimeMilliseconds() * 1_000_000);
            _jsonWriter.WriteString("severityText", SeverityText(e.Result));
            _jsonWriter.WriteString("body", body);
            _jsonWriter.WriteStartArray("attributes");
            WriteAttr(_jsonWriter, "spoke.id", e.Spoke);
            WriteAttr(_jsonWriter, "audit.seq", e.Seq);
            WriteAttr(_jsonWriter, "audit.hash", e.Hash8);
            _jsonWriter.WriteEndArray();
            _jsonWriter.WriteEndObject();
        }
        _jsonWriter.WriteEndArray();   // logRecords
        _jsonWriter.WriteEndObject();  // scopeLogs[0]
        _jsonWriter.WriteEndArray();   // scopeLogs
        _jsonWriter.WriteEndObject();  // resourceLogs[0]
        _jsonWriter.WriteEndArray();   // resourceLogs
        _jsonWriter.WriteEndObject();  // root
        _jsonWriter.Flush();

        return SHA256.HashData(_jsonBuf.WrittenSpan);
    }

    // 열거형 .ToString().ToUpperInvariant() 는 호출마다 새 문자열을 할당 — 컴파일타임 리터럴(인터닝됨)로 대체(Zero-Alloc).
    private static string SeverityText(AuditResult r) => r switch
    {
        AuditResult.Allow => "ALLOW",
        AuditResult.Deny => "DENY",
        _ => "ERROR",
    };

    private static void WriteAttr(Utf8JsonWriter w, string key, string value)
    {
        w.WriteStartObject();
        w.WriteString("key", key);
        w.WriteString("value", value);
        w.WriteEndObject();
    }

    private static void WriteAttr(Utf8JsonWriter w, string key, long value)
    {
        w.WriteStartObject();
        w.WriteString("key", key);
        w.WriteNumber("value", value);
        w.WriteEndObject();
    }

    private bool SimulateNetworkSendLocked()
    {
        if (_forceNetworkFault) { _forceNetworkFault = false; return false; }
        return _rng.NextDouble() > 0.08;   // 8% 배경 실패율(데모 · 실 전송은 HttpClient 릴레이로 교체 가능한 결선점)
    }

    // 이메일/전화/사설 IPv4 등 PII 후보를 [REDACTED] 로 치환(멱등: 이미 치환된 텍스트는 재매칭되지 않음).
    private static string Mask(string? text, ref int maskedCount)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var count = 0;
        var result = EmailRx.Replace(text, _ => { count++; return "[REDACTED]"; });
        result = PhoneRx.Replace(result, _ => { count++; return "[REDACTED]"; });
        result = Ipv4Rx.Replace(result, _ => { count++; return "[REDACTED]"; });
        maskedCount += count;
        return result;
    }

    public void InjectNetworkFault() { lock (_gate) { _forceNetworkFault = true; LastAction = "네트워크 장애 주입(다음 전송 실패)"; } OnChange?.Invoke(); }

    /// <summary>PII 포함 감사 이벤트를 즉시 원장에 제출(마스킹 데모용 · 버퍼로 자동 유입).</summary>
    public void SubmitPiiSample()
    {
        _aggregator.Submit("prod-node-1", "user.login", "actor=ops@promptflow.io ip=10.20.30.40 phone=010-1234-5678", AuditResult.Allow);
    }

    public void StartDemo()
    {
        lock (_gate)
        {
            if (_timer is not null || _disposed) return;
            _timer = new System.Threading.Timer(_ => Tick(), null, 0, 1000);
            LastAction = "익스포터 오토파일럿 기동";
        }
        OnChange?.Invoke();
    }

    private void Tick()
    {
        lock (_gate) { if (_timer is null) return; var before = _state; var r = FlushLocked(); RecordHistoryLocked(r, before); }
        OnChange?.Invoke();
    }

    public void StopDemo()
    {
        lock (_gate) { _timer?.Dispose(); _timer = null; LastAction = "익스포터 오토파일럿 정지"; }
        OnChange?.Invoke();
    }

    public void ResetAll()
    {
        lock (_gate)
        {
            _buffer.Clear(); _lastExportedSeq = 0; _state = ExportCircuitState.Closed; _consecutiveFailures = 0;
            _forceNetworkFault = false; _sent = 0; _failed = 0;
            _history.Clear(); _transitions.Clear();
            LastAction = "익스포터 초기화";
        }
        OnChange?.Invoke();
    }

    private static string Hex8(byte[] h)
    {
        if (h.Length == 0) return "00000000";
        var sb = new StringBuilder(8);
        for (var i = 0; i < 4 && i < h.Length; i++) sb.Append(h[i].ToString("x2"));
        return sb.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _aggregator.OnChange -= OnAggregatorChanged;
        lock (_gate) { _timer?.Dispose(); _timer = null; _jsonWriter.Dispose(); }
    }
}
