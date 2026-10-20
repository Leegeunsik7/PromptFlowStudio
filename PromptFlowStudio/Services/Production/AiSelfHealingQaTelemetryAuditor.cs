using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PromptFlowStudio.Services.Production;

public enum ModuleHealth : byte { Healthy, Degraded, Quarantined, Recovering }

public enum IncidentKind : byte { Exception, MemoryLeak, Deadlock, Hang }

/// <summary>사고 기록(값 형식 · 문자열은 기존 참조만 보관 — 리포트 생성 시 마스킹).</summary>
public readonly record struct QaIncident(long Seq, int Module, IncidentKind Kind, double AtMs, int StackHash, string? TypeName, string? Message, bool Healed);

/// <summary>서명된 진단 리포트(제로 트러스트 — 기밀 마스킹 · HMAC-SHA256).</summary>
public sealed record SignedDiagnosticReport(byte[] Payload, byte[] Signature, int Redactions, int Incidents);

/// <summary>
/// [PromptFlow Studio v16.66.0] 🛡 AI 자율 복구 연속 QA &amp; 크래시 텔레메트리 감사기.
/// 감지: 예외 버스트(5s 내 3회 → 서킷 격리) · 메모리 누수(32샘플 최소제곱 기울기 &gt; 64KB/샘플 · R² ≥ 0.8) · 대기 그래프 순환(데드락) · 하트비트 정지(행).
/// 복구: 격리(2s) → 복구 중 → 재시작 핸들러 → 정상 · 데드락은 순환 내 최신 모듈(최대 id)을 희생자로 대기 해제 후 재시작 · 누수/행은 즉시 재시작.
/// 리포트: 메시지 기밀 마스킹(API 키 · Bearer · 비밀번호/토큰 · 이메일 · 사용자 경로) · 집계 필드 포함 canonical JSON · HMAC-SHA256(키: 환경변수 또는 프로세스 난수) · FixedTimeEquals 검증.
/// 하트비트 · 메모리 샘플 · <see cref="Tick"/> 무할당.
/// </summary>
public sealed class AiSelfHealingQaTelemetryAuditor
{
    public const string KeyEnvVar = "PROMPTFLOW_TELEMETRY_HMAC_KEY";
    public const int MaxModules = 16, LeakWindow = 32, MaxIncidents = 64, BurstCount = 3;
    public const double BurstWindowMs = 5000, QuarantineMs = 2000, HangTimeoutMs = 1500, RecoverGraceMs = 500;
    public const double LeakBytesPerSample = 64 * 1024, LeakMinR2 = 0.8;

    private static readonly Regex[] SecretPatterns =
    {
        new(@"sk-[A-Za-z0-9_\-]{8,}", RegexOptions.Compiled),
        new(@"Bearer\s+[A-Za-z0-9\._\-]+", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"(password|passwd|pwd|secret|token|api[_-]?key)\s*[=:]\s*[^\s;,]+", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled),
        new(@"[A-Za-z]:\\Users\\[^\\\s]+", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"/(home|Users)/[^/\s]+", RegexOptions.Compiled),
    };

    private readonly object _gate = new();
    private readonly byte[] _key;
    private readonly string[] _names = new string[MaxModules];
    private readonly ModuleHealth[] _health = new ModuleHealth[MaxModules];
    private readonly double[] _lastBeat = new double[MaxModules];
    private readonly double[] _until = new double[MaxModules];
    private readonly double[] _excTimes = new double[MaxModules * BurstCount];
    private readonly int[] _excHead = new int[MaxModules];
    private readonly double[] _mem = new double[MaxModules * LeakWindow];
    private readonly int[] _memCount = new int[MaxModules];
    private readonly int[] _waitFor = new int[MaxModules];
    private readonly long[] _restarts = new long[MaxModules];
    private readonly Action<int>?[] _restartHandlers = new Action<int>?[MaxModules];
    private readonly QaIncident[] _incidents = new QaIncident[MaxIncidents];
    private int _count, _incHead, _incCount;
    private long _seq, _healed, _quarantines, _deadlocks, _leaks, _hangs, _exceptions, _handlerFaults, _reports;
    private double _clock, _recoverySum;
    private long _recoveries;

    public AiSelfHealingQaTelemetryAuditor() : this(LoadKey()) { }

    public AiSelfHealingQaTelemetryAuditor(byte[] hmacKey)
    {
        if (hmacKey is null || hmacKey.Length < 16) throw new ArgumentException("HMAC 키 16B 이상 필요", nameof(hmacKey));
        _key = (byte[])hmacKey.Clone();
        Array.Fill(_waitFor, -1);
        Array.Fill(_excTimes, double.NegativeInfinity);
    }

    /// <summary>환경변수(base64 · ≥16B) → 없으면 프로세스 난수 키(하드코딩 금지).</summary>
    public static byte[] LoadKey()
    {
        var env = Environment.GetEnvironmentVariable(KeyEnvVar);
        if (!string.IsNullOrWhiteSpace(env))
        {
            try { var k = Convert.FromBase64String(env.Trim()); if (k.Length >= 16) return k; }
            catch (FormatException) { }
        }
        return RandomNumberGenerator.GetBytes(32);
    }

    public event Action? OnChange;

    public int ModuleCount { get { lock (_gate) return _count; } }
    public double ClockMs { get { lock (_gate) return _clock; } }
    public long Healed { get { lock (_gate) return _healed; } }
    public long Quarantines { get { lock (_gate) return _quarantines; } }
    public long Deadlocks { get { lock (_gate) return _deadlocks; } }
    public long Leaks { get { lock (_gate) return _leaks; } }
    public long Hangs { get { lock (_gate) return _hangs; } }
    public long Exceptions { get { lock (_gate) return _exceptions; } }
    public long HandlerFaults { get { lock (_gate) return _handlerFaults; } }
    public long Reports { get { lock (_gate) return _reports; } }
    public long IncidentTotal { get { lock (_gate) return _seq; } }
    public double MeanRecoveryMs { get { lock (_gate) return _recoveries > 0 ? _recoverySum / _recoveries : 0; } }
    public string? LastAction { get; private set; }

    /// <summary>자율 복구 게이지(0..1) — 정상 1 · 저하 0.6 · 복구 중 0.4 · 격리 0.</summary>
    public float HealthGauge
    {
        get
        {
            lock (_gate)
            {
                if (_count == 0) return 1f;
                var s = 0f;
                for (var i = 0; i < _count; i++) s += _health[i] switch { ModuleHealth.Healthy => 1f, ModuleHealth.Degraded => 0.6f, ModuleHealth.Recovering => 0.4f, _ => 0f };
                return s / _count;
            }
        }
    }

    public int Register(string name, Action<int>? restart = null)
    {
        lock (_gate)
        {
            if (_count >= MaxModules) throw new InvalidOperationException("모듈 상한 초과");
            _names[_count] = name;
            _restartHandlers[_count] = restart;
            _lastBeat[_count] = _clock;
            return _count++;
        }
    }

    public string NameOf(int m) { lock (_gate) return _names[m]; }
    public ModuleHealth HealthOf(int m) { lock (_gate) return _health[m]; }
    public long RestartsOf(int m) { lock (_gate) return _restarts[m]; }
    public int WaitingOn(int m) { lock (_gate) return _waitFor[m]; }

    /// <summary>서킷 — 격리 중 모듈 호출 차단.</summary>
    public bool IsAllowed(int m) { lock (_gate) return _health[m] != ModuleHealth.Quarantined; }

    public void Heartbeat(int m) { lock (_gate) if ((uint)m < (uint)_count) _lastBeat[m] = _clock; }

    public void SampleMemory(int m, long bytes)
    {
        lock (_gate)
        {
            if ((uint)m >= (uint)_count) return;
            var n = _memCount[m];
            var row = _mem.AsSpan(m * LeakWindow, LeakWindow);
            if (n < LeakWindow) { row[n] = bytes; _memCount[m] = n + 1; }
            else { row[1..].CopyTo(row); row[^1] = bytes; }
        }
    }

    public void BeginWait(int waiter, int holder) { lock (_gate) if ((uint)waiter < (uint)_count && (uint)holder < (uint)_count) _waitFor[waiter] = holder; }
    public void EndWait(int waiter) { lock (_gate) if ((uint)waiter < (uint)_count) _waitFor[waiter] = -1; }

    /// <summary>런타임 예외 보고 — 단발 저하 · 버스트 격리.</summary>
    public void ReportException(int m, Exception ex)
    {
        lock (_gate)
        {
            if ((uint)m >= (uint)_count) return;
            _exceptions++;
            var slot = m * BurstCount + _excHead[m];
            _excTimes[slot] = _clock;
            _excHead[m] = (_excHead[m] + 1) % BurstCount;
            var burst = CountExceptions(m) >= BurstCount;
            Record(m, IncidentKind.Exception, StackHash(ex), ex.GetType().Name, ex.Message, healed: false);
            if (_health[m] == ModuleHealth.Quarantined) return;
            if (burst)
            {
                _health[m] = ModuleHealth.Quarantined;
                _until[m] = _clock + QuarantineMs;
                _quarantines++;
                LastAction = "⛔ " + _names[m] + " 예외 버스트 → 격리";
            }
            else if (_health[m] == ModuleHealth.Healthy) _health[m] = ModuleHealth.Degraded;
        }
        Raise();
    }

    private int CountExceptions(int m)
    {
        var c = 0;
        for (var i = 0; i < BurstCount; i++) { if (_clock - _excTimes[m * BurstCount + i] <= BurstWindowMs) c++; }
        return c;
    }

    private static int StackHash(Exception ex)
    {
        var h = new HashCode();
        h.Add(ex.GetType().FullName, StringComparer.Ordinal);
        h.Add(ex.TargetSite?.Name, StringComparer.Ordinal);
        h.Add(ex.HResult);
        return h.ToHashCode();
    }

    /// <summary>1틱(무할당) — 격리 만료 · 누수 · 데드락 · 행 감지 → 자율 복구.</summary>
    public int Tick(double dtMs)
    {
        var actions = 0;
        lock (_gate)
        {
            _clock += dtMs;
            for (var m = 0; m < _count; m++)
            {
                switch (_health[m])
                {
                    case ModuleHealth.Quarantined when _clock >= _until[m]:
                        _recoverySum += _clock - (_until[m] - QuarantineMs); _recoveries++;
                        Restart(m, ModuleHealth.Recovering);
                        MarkHealed(m, IncidentKind.Exception);
                        actions++;
                        continue;
                    case ModuleHealth.Recovering when _clock >= _until[m]:
                    case ModuleHealth.Degraded when _clock - LastIncidentAt(m) >= BurstWindowMs:
                        _health[m] = ModuleHealth.Healthy;
                        continue;
                    case ModuleHealth.Quarantined:
                        continue;
                }
                if (_memCount[m] >= LeakWindow && IsLeaking(m))
                {
                    _leaks++;
                    Record(m, IncidentKind.MemoryLeak, 0, null, null, healed: true);
                    _memCount[m] = 0;
                    Restart(m, ModuleHealth.Recovering);
                    actions++;
                    continue;
                }
                if (_clock - _lastBeat[m] > HangTimeoutMs)
                {
                    _hangs++;
                    Record(m, IncidentKind.Hang, 0, null, null, healed: true);
                    Restart(m, ModuleHealth.Recovering);
                    actions++;
                }
            }
            actions += BreakDeadlocks();
        }
        if (actions > 0) Raise();
        return actions;
    }

    private double LastIncidentAt(int m)
    {
        var last = double.NegativeInfinity;
        for (var i = 0; i < _incCount; i++) { ref readonly var x = ref _incidents[i]; if (x.Module == m && x.AtMs > last) last = x.AtMs; }
        return last;
    }

    /// <summary>최소제곱 기울기(바이트/샘플) · 결정계수.</summary>
    public static (double Slope, double R2) LeakFit(ReadOnlySpan<double> y)
    {
        var n = y.Length;
        if (n < 2) return (0, 0);
        double sx = 0, sy = 0, sxx = 0, sxy = 0, syy = 0;
        for (var i = 0; i < n; i++) { sx += i; sy += y[i]; sxx += (double)i * i; sxy += i * y[i]; syy += y[i] * y[i]; }
        var den = n * sxx - sx * sx;
        if (den == 0) return (0, 0);
        var slope = (n * sxy - sx * sy) / den;
        var vy = n * syy - sy * sy;
        var r2 = vy <= 0 ? 0 : (n * sxy - sx * sy) * (n * sxy - sx * sy) / (den * vy);
        return (slope, r2);
    }

    private bool IsLeaking(int m)
    {
        var (slope, r2) = LeakFit(_mem.AsSpan(m * LeakWindow, LeakWindow));
        return slope > LeakBytesPerSample && r2 >= LeakMinR2;
    }

    /// <summary>대기 그래프(모듈당 1 간선) 순환 탐지 → 순환 내 최대 id 희생 · 대기 해제 · 재시작.</summary>
    private int BreakDeadlocks()
    {
        var broken = 0;
        for (var start = 0; start < _count; start++)
        {
            if (_waitFor[start] < 0) continue;
            var cur = _waitFor[start];
            var steps = 0;
            while (cur >= 0 && cur != start && steps++ < _count) cur = _waitFor[cur];
            if (cur != start) continue;
            var victim = start;
            for (var x = _waitFor[start]; x != start; x = _waitFor[x]) if (x > victim) victim = x;
            _deadlocks++;
            Record(victim, IncidentKind.Deadlock, 0, null, null, healed: true);
            _waitFor[victim] = -1;
            Restart(victim, ModuleHealth.Recovering);
            broken++;
        }
        return broken;
    }

    private void Restart(int m, ModuleHealth next)
    {
        _restarts[m]++;
        _health[m] = next;
        _until[m] = _clock + RecoverGraceMs;
        _lastBeat[m] = _clock;
        _healed++;
        try { _restartHandlers[m]?.Invoke(m); }
        catch (Exception ex) { _handlerFaults++; PromptFlowStudio.Program.Log("Production.SelfHeal.Restart", ex); }
    }

    private void Record(int m, IncidentKind kind, int stack, string? type, string? message, bool healed)
    {
        _incidents[_incHead] = new QaIncident(++_seq, m, kind, _clock, stack, type, message, healed);
        _incHead = (_incHead + 1) % MaxIncidents;
        if (_incCount < MaxIncidents) _incCount++;
    }

    private void MarkHealed(int m, IncidentKind kind)
    {
        for (var i = 0; i < _incCount; i++)
            if (_incidents[i].Module == m && _incidents[i].Kind == kind && !_incidents[i].Healed) _incidents[i] = _incidents[i] with { Healed = true };
    }

    /// <summary>최근 사고(최신 우선) 복사.</summary>
    public int CopyIncidents(Span<QaIncident> dst)
    {
        lock (_gate)
        {
            var n = Math.Min(dst.Length, _incCount);
            for (var i = 0; i < n; i++) dst[i] = _incidents[(_incHead - 1 - i + MaxIncidents * 2) % MaxIncidents];
            return n;
        }
    }

    /// <summary>기밀 마스킹 — (마스킹 문자열, 치환 수).</summary>
    public static (string Text, int Count) Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return ("", 0);
        var total = 0;
        foreach (var re in SecretPatterns)
        {
            var c = 0;
            text = re.Replace(text, _ => { c++; return "[REDACTED]"; });
            total += c;
        }
        return (text, total);
    }

    /// <summary>제로 트러스트 진단 리포트 — 마스킹 + 집계 포함 canonical JSON + HMAC-SHA256.</summary>
    public SignedDiagnosticReport BuildReport()
    {
        var buf = new System.Buffers.ArrayBufferWriter<byte>(4096);
        int redactions = 0, incidents;
        lock (_gate)
        {
            using var w = new Utf8JsonWriter(buf);
            w.WriteStartObject();
            w.WriteString("schema", "PFS-DIAG-1");
            w.WriteNumber("clockMs", Math.Round(_clock, 3));
            w.WriteStartArray("modules");
            for (var m = 0; m < _count; m++)
            {
                w.WriteStartObject();
                w.WriteString("name", _names[m]);
                w.WriteString("health", _health[m].ToString());
                w.WriteNumber("restarts", _restarts[m]);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("incidents");
            incidents = _incCount;
            for (var i = 0; i < _incCount; i++)
            {
                var x = _incidents[(_incHead - _incCount + i + MaxIncidents) % MaxIncidents];
                var (msg, c) = Redact(x.Message);
                redactions += c;
                w.WriteStartObject();
                w.WriteNumber("seq", x.Seq);
                w.WriteString("module", _names[x.Module]);
                w.WriteString("kind", x.Kind.ToString());
                w.WriteNumber("atMs", Math.Round(x.AtMs, 3));
                w.WriteString("stack", x.StackHash.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
                if (x.TypeName is not null) w.WriteString("type", x.TypeName);
                if (msg.Length > 0) w.WriteString("message", msg);
                w.WriteBoolean("healed", x.Healed);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            // 집계 필드 — 사고 삭제/누락 위조 차단
            w.WriteStartObject("aggregate");
            w.WriteNumber("incidentTotal", _seq);
            w.WriteNumber("incidentsListed", _incCount);
            w.WriteNumber("healed", _healed);
            w.WriteNumber("quarantines", _quarantines);
            w.WriteNumber("deadlocks", _deadlocks);
            w.WriteNumber("leaks", _leaks);
            w.WriteNumber("hangs", _hangs);
            w.WriteNumber("redactions", redactions);
            w.WriteEndObject();
            w.WriteEndObject();
            w.Flush();
            _reports++;
        }
        var payload = buf.WrittenSpan.ToArray();
        var sig = HMACSHA256.HashData(_key, payload);
        LastAction = "🧾 서명 진단 리포트 " + payload.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "B · 마스킹 " + redactions.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Raise();
        return new SignedDiagnosticReport(payload, sig, redactions, incidents);
    }

    public bool VerifyReport(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature)
    {
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(_key, payload, mac);
        return signature.Length == 32 && CryptographicOperations.FixedTimeEquals(mac, signature);
    }

    public static string HealthLabel(ModuleHealth h) => h switch { ModuleHealth.Healthy => "정상", ModuleHealth.Degraded => "저하", ModuleHealth.Quarantined => "격리", _ => "복구 중" };
    public static string KindLabel(IncidentKind k) => k switch { IncidentKind.Exception => "예외", IncidentKind.MemoryLeak => "메모리 누수", IncidentKind.Deadlock => "데드락", _ => "행(무응답)" };

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Production.SelfHeal.OnChange", ex); }
    }
}
