using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PromptFlowStudio.Services.CloudVerification;

public enum IncidentKind : byte { NullReference, InvalidOperation, Argument, IndexRange, Other, BindingMismatch, Timeout, AbnormalLoop, UnobservedTask, Render, RollbackFailed }

/// <summary>사고 기록(값 형식).</summary>
public readonly record struct UiIncident(long Seq, int Target, IncidentKind Kind, string Type, string Message, double Ms, bool RolledBack);

/// <summary>
/// [PromptFlow Studio v16.92.0] 🩺 실시간 UI 예외 탐지 &amp; 자율 상태 복원 가드.
///   ▸ 실행 래퍼: 조작 전 바인딩 상태 스냅샷 → 조작 → 예외(NullReference/InvalidOperation/Argument/IndexRange/기타) · 바인딩 불일치 · 지연(타임아웃) 판정 → 스냅샷 롤백.
///   ▸ 비정상 루프: 대상별 연속 실패 ≥ <see cref="LoopLimit"/> → 격리(드라이버 생략) · 렌더 경계 연속 예외 ≥ 한도 → 자동 복구 중단.
///   ▸ 전역 캡처(무장 시): TaskScheduler.UnobservedTaskException 구독/해제 쌍 · 렌더 예외(<see cref="SelfHealingRenderBoundary"/>) 보고.
///   ▸ 진단 매니페스트: System.Text.Json · canonical 페이로드(집계 필드 포함) SHA-256 다이제스트.
/// </summary>
public sealed class RealtimeUiExceptionSelfHealingGuard : IDisposable
{
    public const int LogSize = 128, MaxTargets = 128;
    private readonly object _gate = new();
    private readonly UiIncident[] _log = new UiIncident[LogSize];
    private readonly int[] _consecutive = new int[MaxTargets];
    private readonly bool[] _quarantined = new bool[MaxTargets];
    private int _logHead, _logCount, _renderStreak;
    private long _seq;
    private bool _armed;

    public double TimeoutMs { get; set; } = 250d;
    public int LoopLimit { get; set; } = 3;
    public int RenderLoopLimit { get; set; } = 5;

    public long Executions { get; private set; }
    public long Incidents { get; private set; }
    public long Exceptions { get; private set; }
    public long Mismatches { get; private set; }
    public long Timeouts { get; private set; }
    public long Rollbacks { get; private set; }
    public long RollbackFailures { get; private set; }
    public long RenderErrors { get; private set; }
    public long UnobservedTasks { get; private set; }
    public int QuarantinedCount { get; private set; }
    public bool IsArmed => _armed;
    public bool RenderLoopTripped => _renderStreak >= RenderLoopLimit;
    public int LogCount => _logCount;
    public UiIncident Log(int back) => _log[(_logHead - 1 - back + 2 * LogSize) % LogSize];
    public bool IsQuarantined(int target) => (uint)target < MaxTargets && _quarantined[target];
    public string? LastManifest { get; private set; }
    public string? LastDigest { get; private set; }

    public event Action? OnIncident;

    public static string KindName(IncidentKind k) => k switch
    {
        IncidentKind.NullReference => "NullReference", IncidentKind.InvalidOperation => "InvalidOperation", IncidentKind.Argument => "Argument", IncidentKind.IndexRange => "IndexRange",
        IncidentKind.BindingMismatch => "바인딩 불일치", IncidentKind.Timeout => "지연 초과", IncidentKind.AbnormalLoop => "비정상 루프 격리", IncidentKind.UnobservedTask => "미관찰 Task",
        IncidentKind.Render => "렌더 예외", IncidentKind.RollbackFailed => "롤백 실패", _ => "기타 예외",
    };

    public static IncidentKind Classify(Exception ex) => ex switch
    {
        NullReferenceException => IncidentKind.NullReference,
        ObjectDisposedException or InvalidOperationException => IncidentKind.InvalidOperation,
        ArgumentException => IncidentKind.Argument,
        IndexOutOfRangeException or ArgumentOutOfRangeException => IncidentKind.IndexRange,
        _ => IncidentKind.Other,
    };

    /// <summary>무장: 전역 미관찰 Task 예외 구독(해제는 <see cref="Disarm"/>/<see cref="Dispose"/>).</summary>
    public void Arm()
    {
        if (_armed) return;
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        _armed = true;
    }

    public void Disarm()
    {
        if (!_armed) return;
        TaskScheduler.UnobservedTaskException -= OnUnobserved;
        _armed = false;
    }

    private void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            lock (_gate) UnobservedTasks++;
            var inner = e.Exception.InnerException ?? e.Exception;
            Record(-1, IncidentKind.UnobservedTask, inner.GetType().Name, inner.Message, 0d, false);
        }
        catch (Exception ex) { PromptFlowStudio.Program.Log("CloudVerify.Guard.Unobserved", ex); }
    }

    /// <summary>
    /// 가드 실행: 스냅샷 → <paramref name="action"/>(true = 바인딩 정합) → 실패 시 롤백.
    /// 결과: Pass · BindingMismatch · Exception · Timeout · Quarantined.
    /// </summary>
    public UiStepOutcome Execute(int index, UiControlTarget t, Func<UiControlTarget, bool> action)
    {
        if (IsQuarantined(index)) return UiStepOutcome.Quarantined;
        double snapshot;
        try { snapshot = t.Read(); }
        catch (Exception ex) { lock (_gate) Exceptions++; Record(index, Classify(ex), ex.GetType().Name, "스냅샷 실패: " + ex.Message, 0d, false); return Fail(index, UiStepOutcome.Exception); }
        lock (_gate) Executions++;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        bool ok; Exception? err = null;
        try { ok = action(t); }
        catch (Exception ex) { ok = false; err = ex; }
        var ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        t.LastMs = ms;
        if (err is null && ok && ms <= TimeoutMs) { _consecutive[index] = 0; return UiStepOutcome.Pass; }

        var rolled = Rollback(index, t, snapshot);
        if (err is not null)
        {
            lock (_gate) Exceptions++;
            Record(index, Classify(err), err.GetType().Name, err.Message, ms, rolled);
            return Fail(index, UiStepOutcome.Exception);
        }
        if (!ok)
        {
            lock (_gate) Mismatches++;
            Record(index, IncidentKind.BindingMismatch, t.Kind.ToString(), t.LastNote ?? "읽기 값 ≠ 쓰기 값", ms, rolled);
            return Fail(index, UiStepOutcome.BindingMismatch);
        }
        lock (_gate) Timeouts++;
        Record(index, IncidentKind.Timeout, t.Kind.ToString(), "조작 지연 " + ms.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " ms", ms, rolled);
        return Fail(index, UiStepOutcome.Timeout);
    }

    private UiStepOutcome Fail(int index, UiStepOutcome o)
    {
        if ((uint)index >= MaxTargets) return o;
        if (++_consecutive[index] >= LoopLimit && !_quarantined[index])
        {
            _quarantined[index] = true;
            lock (_gate) QuarantinedCount++;
            Record(index, IncidentKind.AbnormalLoop, "Quarantine", "연속 실패 " + _consecutive[index].ToString(System.Globalization.CultureInfo.InvariantCulture) + "회", 0d, false);
        }
        return o;
    }

    private bool Rollback(int index, UiControlTarget t, double snapshot)
    {
        try
        {
            t.Write(snapshot);
            lock (_gate) Rollbacks++;
            return true;
        }
        catch (Exception ex)
        {
            lock (_gate) RollbackFailures++;
            Record(index, IncidentKind.RollbackFailed, ex.GetType().Name, ex.Message, 0d, false);
            return false;
        }
    }

    /// <summary>렌더 경계 예외 보고 → 자동 복구 허용 여부(연속 한도 미만).</summary>
    public bool ReportRender(Exception ex, string component)
    {
        lock (_gate) { RenderErrors++; _renderStreak++; }
        Record(-1, IncidentKind.Render, ex.GetType().Name, component + ": " + ex.Message, 0d, !RenderLoopTripped);
        return !RenderLoopTripped;
    }

    public void ReportRenderHealthy() { if (_renderStreak > 0) lock (_gate) _renderStreak = 0; }

    public void Release(int target) { if ((uint)target < MaxTargets && _quarantined[target]) { _quarantined[target] = false; _consecutive[target] = 0; lock (_gate) QuarantinedCount--; } }

    public void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_consecutive); Array.Clear(_quarantined);
            _logHead = _logCount = 0; _renderStreak = 0; QuarantinedCount = 0;
            Executions = Incidents = Exceptions = Mismatches = Timeouts = Rollbacks = RollbackFailures = RenderErrors = UnobservedTasks = 0;
        }
    }

    private void Record(int target, IncidentKind k, string type, string msg, double ms, bool rolled)
    {
        lock (_gate)
        {
            _log[_logHead] = new UiIncident(++_seq, target, k, type, msg, ms, rolled);
            _logHead = (_logHead + 1) % LogSize; if (_logCount < LogSize) _logCount++;
            Incidents++;
        }
        try { OnIncident?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("CloudVerify.Guard.OnIncident", ex); }
    }

    /// <summary>진단 매니페스트(JSON) — 집계 + 사고 목록 + 소스/UI 요약 · canonical SHA-256 다이제스트.</summary>
    public string BuildManifest(CloudSourceCodeIntegrityVerifierEngine? source, FullUiInteractiveMenuAutomatedDriver? driver)
    {
        var canon = new StringBuilder(512);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("schema", "promptflow.cloud-verification/1");
            w.WriteString("version", "16.92.0");
            w.WriteString("generatedUtc", DateTime.UtcNow.ToString("O", inv));
            w.WriteStartObject("guard");
            w.WriteNumber("executions", Executions); w.WriteNumber("incidents", Incidents); w.WriteNumber("exceptions", Exceptions);
            w.WriteNumber("mismatches", Mismatches); w.WriteNumber("timeouts", Timeouts); w.WriteNumber("rollbacks", Rollbacks);
            w.WriteNumber("rollbackFailures", RollbackFailures); w.WriteNumber("renderErrors", RenderErrors); w.WriteNumber("unobservedTasks", UnobservedTasks); w.WriteNumber("quarantined", QuarantinedCount);
            w.WriteEndObject();
            canon.Append("g|").Append(Executions).Append('|').Append(Incidents).Append('|').Append(Exceptions).Append('|').Append(Mismatches).Append('|').Append(Timeouts)
                 .Append('|').Append(Rollbacks).Append('|').Append(RollbackFailures).Append('|').Append(RenderErrors).Append('|').Append(UnobservedTasks).Append('|').Append(QuarantinedCount).Append('\n');
            if (source is not null)
            {
                w.WriteStartObject("source");
                w.WriteNumber("files", source.FileCount); w.WriteNumber("lines", source.TotalLines); w.WriteNumber("methods", source.Methods); w.WriteNumber("hotMethods", source.HotMethods);
                w.WriteNumber("errors", source.Errors); w.WriteNumber("warnings", source.Warnings); w.WriteNumber("infos", source.Infos);
                w.WriteNumber("integrityScore", Math.Round(source.IntegrityScore, 2));
                w.WriteStartObject("rules");
                for (var r = 0; r < CloudSourceCodeIntegrityVerifierEngine.Rules; r++) w.WriteNumber(CloudSourceCodeIntegrityVerifierEngine.RuleCodes[r], source.RuleCount((VerifyRule)r));
                w.WriteEndObject();
                w.WriteStartArray("probes");
                for (var i = 0; i < source.ProbeCount; i++)
                {
                    var p = source.Probe(i);
                    w.WriteStartObject(); w.WriteString("name", p.Name); w.WriteNumber("bytesPerCall", Math.Round(p.BytesPerCall, 2)); w.WriteBoolean("zeroAlloc", p.ZeroAlloc); w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
                canon.Append("s|").Append(source.FileCount).Append('|').Append(source.TotalLines).Append('|').Append(source.Errors).Append('|').Append(source.Warnings).Append('|').Append(source.Infos).Append('\n');
            }
            if (driver is not null)
            {
                w.WriteStartObject("ui");
                w.WriteNumber("targets", driver.Count); w.WriteNumber("passes", driver.Passes); w.WriteNumber("failures", driver.Failures); w.WriteNumber("cycles", driver.Cycles);
                w.WriteNumber("razorFiles", driver.RazorFiles); w.WriteNumber("headerButtons", driver.HeaderButtons); w.WriteNumber("storyboardTools", driver.StoryboardTools); w.WriteNumber("navEntries", driver.NavEntries);
                w.WriteNumber("buttons", driver.MarkupButtons); w.WriteNumber("handlers", driver.MarkupHandlers); w.WriteNumber("sliders", driver.MarkupSliders); w.WriteNumber("checkboxes", driver.MarkupCheckboxes);
                w.WriteEndObject();
                canon.Append("u|").Append(driver.Count).Append('|').Append(driver.Passes).Append('|').Append(driver.Failures).Append('|').Append(driver.Cycles).Append('\n');
            }
            w.WriteStartArray("incidents");
            for (var i = 0; i < _logCount; i++)
            {
                var e = Log(i);
                w.WriteStartObject();
                w.WriteNumber("seq", e.Seq); w.WriteNumber("target", e.Target); w.WriteString("kind", e.Kind.ToString()); w.WriteString("type", e.Type);
                w.WriteString("message", e.Message); w.WriteNumber("ms", Math.Round(e.Ms, 3)); w.WriteBoolean("rolledBack", e.RolledBack);
                w.WriteEndObject();
                canon.Append("i|").Append(e.Seq).Append('|').Append(e.Target).Append('|').Append((int)e.Kind).Append('|').Append(e.Type).Append('|').Append(e.RolledBack ? '1' : '0').Append('\n');
            }
            w.WriteEndArray();
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canon.ToString())));
            w.WriteString("canonicalSha256", digest);
            w.WriteEndObject();
            LastDigest = digest;
        }
        LastManifest = Encoding.UTF8.GetString(ms.ToArray());
        return LastManifest;
    }

    public void Dispose() => Disarm();
}
