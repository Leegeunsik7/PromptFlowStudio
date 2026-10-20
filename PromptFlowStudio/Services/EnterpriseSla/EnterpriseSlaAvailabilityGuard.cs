using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PromptFlowStudio.Services.EnterpriseSla;

/// <summary>복구 이력 매니페스트 레코드(해시 체인 · canonical = 전 필드 + 누적 장애 수).</summary>
public sealed record RecoveryRecord(long Seq, string Utc, string Subsystem, string Incident, string Action, double MttrMs, double SwapUs, double Availability, long TotalIncidents, string PrevHash, string Hash);

/// <summary>매니페스트 내보내기 봉투(집계 필드 서명).</summary>
public sealed record SlaManifestExport(int Version, int Count, double Availability, double DowntimeMs, double WindowMs, long Incidents, string HeadHash, bool Signed, string Signature, List<RecoveryRecord> Records);

/// <summary>
/// [PromptFlow Studio v16.95.0] 🛡️ 엔터프라이즈 99.999% SLA 자율 복구 &amp; 가용성 가드.
/// · 감지 엔진 발견 → 자동 수습: 멈춤/무한 루프 = 격리 + 핫스왑 · 락 교착 = 희생자 대기 해제 + 핫스왑 · 메모리 압박 = 최대 점유 레플리카 격리 + 힙 압축(10 s 제한) ·
///   스레드 풀 기아 = 최소 워커 상향(옵트인 · 원값 복원) · 런타임 예외 = Run 경계 격리 → 브레이커 · 미관찰 Task 예외 = SetObserved.
/// · 가용성 = 1 − Σ다운타임 / (가동 창 × 서브시스템 수) · 9의 개수 · 99.999% 오류 예산 잔여.
/// · 복구 매니페스트: SHA-256 해시 체인(PROMPTFLOW_SLA_KEY 설정 시 HMAC-SHA256) · 집계 서명 · FixedTimeEquals 검증 · 원자 JSON 내보내기.
/// </summary>
public sealed class EnterpriseSlaAvailabilityGuard : IDisposable
{
    public const double TargetAvailability = 0.99999;
    public const int ManifestCap = 512, SeriesLen = 120;
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";
    public static readonly string[] IncidentNames = ThreadDeadlockAutoProbingEngine.KindNames;

    private static readonly double TickMs = 1000.0 / Stopwatch.Frequency;

    private readonly object _gate = new();
    private readonly int[] _slotToSub = new int[ThreadDeadlockAutoProbingEngine.MaxSlots];
    private readonly List<RecoveryRecord> _manifest = new();
    private readonly float[] _series = new float[SeriesLen];
    private readonly byte[]? _key;
    private long _lastFinding, _lastEvent, _incidents, _contained, _deadlocksBroken, _gcCompactions, _poolBoosts, _recordSeq;
    private long _armedTicks, _armedTs, _lastGcTs;
    private double _mttrSum; private long _mttrN;
    private int _seriesHead, _seriesCount, _origMinWorkers = -1, _origMinIo;
    private string _head = GenesisHash;
    private bool _globalAttached;

    public EnterpriseSlaAvailabilityGuard(ThreadDeadlockAutoProbingEngine engine, ZeroDowntimeProcessFailoverSynthesizer synth, byte[]? key = null)
    {
        Engine = engine;
        Synth = synth;
        _key = key ?? KeyFromEnvironment();
        for (var i = 0; i < _slotToSub.Length; i++) _slotToSub[i] = -1;
        Engine.FindingsDetected += OnFindings;
    }

    public event Action? OnChange;

    public ThreadDeadlockAutoProbingEngine Engine { get; }
    public ZeroDowntimeProcessFailoverSynthesizer Synth { get; }
    public bool IsSigned => _key is not null;
    public bool ApplyThreadPool { get; set; }
    public bool IsArmed => Interlocked.Read(ref _armedTs) != 0;
    public long Incidents => Interlocked.Read(ref _incidents);
    public long ExceptionsContained => Interlocked.Read(ref _contained);
    public long DeadlocksBroken => Interlocked.Read(ref _deadlocksBroken);
    public long GcCompactions => Interlocked.Read(ref _gcCompactions);
    public long PoolBoosts => Interlocked.Read(ref _poolBoosts);
    public double MeanMttrMs => _mttrN == 0 ? 0 : _mttrSum / _mttrN;
    public string? LastException { get; private set; }
    public string? LastAction { get; private set; }
    public string HeadHash { get { lock (_gate) return _head; } }
    public int ManifestCount { get { lock (_gate) return _manifest.Count; } }
    public int SeriesCount => _seriesCount;

    private static byte[]? KeyFromEnvironment()
    {
        var k = Environment.GetEnvironmentVariable("PROMPTFLOW_SLA_KEY");
        return string.IsNullOrEmpty(k) ? null : SHA256.HashData(Encoding.UTF8.GetBytes(k));
    }

    /// <summary>감시 슬롯 → 서브시스템 결선.</summary>
    public void Bind(int watchSlot, int subsystem) => _slotToSub[watchSlot] = subsystem;
    public int SubsystemOf(int watchSlot) => watchSlot < 0 ? -1 : _slotToSub[watchSlot];

    // ── 가동 창 · 가용성 ──
    public void Arm() { if (Interlocked.CompareExchange(ref _armedTs, Stopwatch.GetTimestamp(), 0) == 0) Raise(); }

    public void Disarm()
    {
        var t = Interlocked.Exchange(ref _armedTs, 0);
        if (t != 0) Interlocked.Add(ref _armedTicks, Stopwatch.GetTimestamp() - t);
    }

    public double WindowMs
    {
        get
        {
            var t = Interlocked.Read(ref _armedTs);
            return (Interlocked.Read(ref _armedTicks) + (t != 0 ? Stopwatch.GetTimestamp() - t : 0)) * TickMs;
        }
    }

    public double DowntimeMs
    {
        get
        {
            var d = 0.0;
            for (var s = 0; s < Synth.Count; s++) d += Synth.DowntimeMs(s);
            return d;
        }
    }

    public double Availability
    {
        get
        {
            var n = Synth.Count;
            var w = WindowMs * n;
            return n == 0 || w <= 0 ? 1.0 : Math.Clamp(1.0 - DowntimeMs / w, 0.0, 1.0);
        }
    }

    /// <summary>9의 개수(−log10(1−A) · 상한 9).</summary>
    public double Nines => Math.Min(9.0, -Math.Log10(Math.Max(1e-9, 1.0 - Availability)));
    public double ErrorBudgetMs => WindowMs * Synth.Count * (1.0 - TargetAvailability);
    public double BudgetRemainingMs => ErrorBudgetMs - DowntimeMs;
    public bool SlaMet => Availability >= TargetAvailability;

    public float Series(int i) => _series[(_seriesHead - 1 - i + SeriesLen * 2) % SeriesLen];

    // ── 자동 수습 ──
    private void OnFindings(int n)
    {
        try { if (HandleFindings() > 0) { Tick(); Raise(); } }
        catch (Exception ex) { PromptFlowStudio.Program.Log("Sla.Guard.Findings", ex); }
    }

    /// <summary>미처리 발견 일괄 수습 → 처리 수.</summary>
    public int HandleFindings()
    {
        lock (_gate)
        {
            var head = Engine.FindingSeq;
            var from = Math.Max(_lastFinding, head - ThreadDeadlockAutoProbingEngine.FindingCap);
            var handled = 0;
            for (var q = from + 1; q <= head; q++)
                if (Engine.TryGetFinding(q, out var f)) { Remediate(f); handled++; }
            _lastFinding = head;
            return handled;
        }
    }

    private void Remediate(in HangFinding f)
    {
        Interlocked.Increment(ref _incidents);
        var sub = SubsystemOf(f.Slot);
        var name = IncidentNames[(int)f.Kind];
        switch (f.Kind)
        {
            case HangKind.Stalled:
            case HangKind.Spinning:
                if (sub >= 0) Synth.Trip(sub, name, f.StartTs);
                else Record(Engine.Name(f.Slot), name, "관찰(서브시스템 미결선)", f.DurationMs, 0);
                LastAction = "🛡 " + name + " → " + (sub >= 0 ? Synth.Name(sub) + " 격리·핫스왑" : "관찰");
                break;
            case HangKind.Deadlock:
                if (sub >= 0) Synth.Trip(sub, name, f.StartTs);
                Engine.AbortWait(f.Slot);
                Interlocked.Increment(ref _deadlocksBroken);
                LastAction = "🔓 락 교착(사이클 " + f.CycleLength + ") → 희생자 " + Engine.Name(f.Slot) + " 대기 해제";
                if (sub < 0) Record(Engine.Name(f.Slot), name, "희생자 대기 해제", f.DurationMs, 0);
                break;
            case HangKind.Starvation:
                Record("프로세스", name, BoostThreadPool() ? "스레드 풀 최소 워커 상향" : "권고(실적용 OFF)", f.DurationMs, 0);
                LastAction = "🧵 스레드 풀 기아 " + f.DurationMs.ToString("0", CultureInfo.InvariantCulture) + " ms";
                break;
            case HangKind.MemoryPressure:
                var hog = -1; long max = 0;
                for (var s = 0; s < Synth.Count; s++)
                {
                    var b = Synth.Active(s).ResourceBytes;
                    if (b > max) { max = b; hog = s; }
                }
                if (hog >= 0) Synth.Trip(hog, name, f.StartTs);
                var compacted = CompactHeap();
                Engine.RearmMemory();
                Record(hog >= 0 ? Synth.Name(hog) : "프로세스", name, compacted ? "점유 레플리카 격리 + 힙 압축" : "점유 레플리카 격리", 0, 0);
                LastAction = "💾 메모리 압박 " + f.Metric.ToString("0", CultureInfo.InvariantCulture) + " MB → " + (hog >= 0 ? Synth.Name(hog) + " 격리" : "힙 압축");
                break;
        }
    }

    private bool BoostThreadPool()
    {
        if (!ApplyThreadPool) return false;
        ThreadPool.GetMinThreads(out var w, out var io);
        if (_origMinWorkers < 0) { _origMinWorkers = w; _origMinIo = io; }
        Interlocked.Increment(ref _poolBoosts);
        return ThreadPool.SetMinThreads(Math.Min(w + Environment.ProcessorCount, 512), io);
    }

    public void RestoreThreadPool()
    {
        if (_origMinWorkers < 0) return;
        ThreadPool.SetMinThreads(_origMinWorkers, _origMinIo);
        _origMinWorkers = -1;
    }

    private bool CompactHeap()
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastGcTs != 0 && (now - _lastGcTs) * TickMs < 10_000) return false;
        _lastGcTs = now;
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        Interlocked.Increment(ref _gcCompactions);
        return true;
    }

    /// <summary>런타임 예외 격리 경계: 성공/실패를 브레이커에 보고(무할당 · 예외 경로 제외).</summary>
    public bool Run<TState>(int sub, int epoch, IFailoverReplica replica, TState state, Func<IFailoverReplica, TState, bool> op)
    {
        try
        {
            if (op(replica, state)) { Synth.ReportSuccess(sub, epoch); return true; }
            Synth.ReportFailure(sub, epoch, "작업 실패 반환");
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Interlocked.Increment(ref _contained);
            Interlocked.Increment(ref _incidents);
            LastException = Synth.Name(sub) + " · " + ex.GetType().Name + ": " + ex.Message;
            Synth.ReportFailure(sub, epoch, "런타임 예외 격리");
            return false;
        }
    }

    /// <summary>프로세스 전역 미관찰 Task 예외 수습(SetObserved) 결선.</summary>
    public void AttachGlobal()
    {
        lock (_gate)
        {
            if (_globalAttached) return;
            TaskScheduler.UnobservedTaskException += OnUnobserved;
            _globalAttached = true;
        }
    }

    private void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        Interlocked.Increment(ref _contained);
        Interlocked.Increment(ref _incidents);
        LastException = "미관찰 Task · " + e.Exception.InnerException?.GetType().Name;
        Record("프로세스", "미관찰 Task 예외", "SetObserved 격리", 0, 0);
    }

    /// <summary>주기 처리: 합성기 이벤트 → 매니페스트 미러링 · 가용성 표본.</summary>
    public void Tick()
    {
        lock (_gate)
        {
            var head = Synth.EventSeq;
            for (var q = Math.Max(_lastEvent, head - ZeroDowntimeProcessFailoverSynthesizer.EventCap) + 1; q <= head; q++)
            {
                if (!Synth.TryGetEvent(q, out var e)) continue;
                if (e.Kind is FailoverKind.Failover or FailoverKind.Forced or FailoverKind.Restored) { _mttrSum += e.DowntimeMs; _mttrN++; }
                var action = e.Kind switch
                {
                    FailoverKind.Down => "대기 레플리카 Open → 서비스 중단",
                    FailoverKind.Recovered => "R" + e.From + " HalfOpen 프로브 → 웜 스탠바이",
                    _ => "R" + e.From + " 격리 → R" + e.To + " 핫스왑(LKG #" + e.LkgSeq.ToString(CultureInfo.InvariantCulture) + ")",
                };
                Record(Synth.Name(e.Subsystem), e.Reason, ZeroDowntimeProcessFailoverSynthesizer.KindNames[(int)e.Kind] + " · " + action, e.DowntimeMs, e.SwapUs);
                if (e.Kind == FailoverKind.Forced) LastAction = "⇄ " + Synth.Name(e.Subsystem) + " 강제 페일오버 " + e.SwapUs.ToString("0.0", CultureInfo.InvariantCulture) + " µs";
                else if (e.Kind == FailoverKind.Down) LastAction = "⛔ " + Synth.Name(e.Subsystem) + " 서비스 중단(양 레플리카 Open)";
            }
            _lastEvent = head;
            _series[_seriesHead] = (float)Nines;
            _seriesHead = (_seriesHead + 1) % SeriesLen;
            if (_seriesCount < SeriesLen) _seriesCount++;
        }
    }

    // ── 매니페스트 ──
    private void Record(string sub, string incident, string action, double mttrMs, double swapUs)
    {
        lock (_gate)
        {
            var seq = ++_recordSeq;
            var utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var avail = Math.Round(Availability, 9);
            mttrMs = Math.Round(mttrMs, 3); swapUs = Math.Round(swapUs, 3);
            var total = Incidents;
            var hash = HashHex(_key, Canonical(seq, utc, sub, incident, action, mttrMs, swapUs, avail, total, _head));
            _manifest.Add(new RecoveryRecord(seq, utc, sub, incident, action, mttrMs, swapUs, avail, total, _head, hash));
            if (_manifest.Count > ManifestCap) _manifest.RemoveAt(0);
            _head = hash;
        }
    }

    public RecoveryRecord? Manifest(int iFromNewest)
    {
        lock (_gate) return iFromNewest < _manifest.Count ? _manifest[_manifest.Count - 1 - iFromNewest] : null;
    }

    private static string Canonical(long seq, string utc, string sub, string incident, string action, double mttr, double swap, double avail, long total, string prev) =>
        string.Join('|', seq.ToString(CultureInfo.InvariantCulture), utc, sub, incident, action,
            mttr.ToString("R", CultureInfo.InvariantCulture), swap.ToString("R", CultureInfo.InvariantCulture), avail.ToString("R", CultureInfo.InvariantCulture),
            total.ToString(CultureInfo.InvariantCulture), prev);

    private static string HashHex(byte[]? key, string canonical)
    {
        var b = Encoding.UTF8.GetBytes(canonical);
        return Convert.ToHexString(key is null ? SHA256.HashData(b) : HMACSHA256.HashData(key, b)).ToLowerInvariant();
    }

    private static bool HexEquals(string a, string b)
    {
        if (a.Length != b.Length || a.Length % 2 != 0) return false;
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(a), Convert.FromHexString(b)); }
        catch (FormatException) { return false; }
    }

    /// <summary>해시 체인 검증(연결 + 재계산 FixedTimeEquals).</summary>
    public static bool VerifyChain(IReadOnlyList<RecoveryRecord> rs, byte[]? key)
    {
        for (var i = 0; i < rs.Count; i++)
        {
            var r = rs[i];
            if (i > 0 && !HexEquals(r.PrevHash, rs[i - 1].Hash)) return false;
            if (!HexEquals(r.Hash, HashHex(key, Canonical(r.Seq, r.Utc, r.Subsystem, r.Incident, r.Action, r.MttrMs, r.SwapUs, r.Availability, r.TotalIncidents, r.PrevHash)))) return false;
        }
        return true;
    }

    public bool VerifyManifest()
    {
        lock (_gate) return VerifyChain(_manifest, _key);
    }

    private static string AggregateCanonical(int version, int count, double avail, double downMs, double windowMs, long incidents, string head) =>
        string.Join('|', version.ToString(CultureInfo.InvariantCulture), count.ToString(CultureInfo.InvariantCulture), avail.ToString("R", CultureInfo.InvariantCulture),
            downMs.ToString("R", CultureInfo.InvariantCulture), windowMs.ToString("R", CultureInfo.InvariantCulture), incidents.ToString(CultureInfo.InvariantCulture), head);

    public static string DefaultExportPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", "sla", "recovery-manifest.json");

    /// <summary>매니페스트 원자 내보내기(System.Text.Json · 집계 서명) → 경로.</summary>
    public string Export(string? path = null)
    {
        path ??= DefaultExportPath;
        SlaManifestExport doc;
        lock (_gate)
        {
            var avail = Math.Round(Availability, 9); var down = Math.Round(DowntimeMs, 3); var win = Math.Round(WindowMs, 3);
            var sig = HashHex(_key, AggregateCanonical(1, _manifest.Count, avail, down, win, Incidents, _head));
            doc = new SlaManifestExport(1, _manifest.Count, avail, down, win, Incidents, _head, _key is not null, sig, new List<RecoveryRecord>(_manifest));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
        LastAction = "📜 매니페스트 내보내기 " + doc.Count + "건";
        Raise();
        return path;
    }

    /// <summary>내보낸 매니페스트 검증(체인 + 헤드 + 집계 서명).</summary>
    public static bool VerifyFile(string path, byte[]? key = null)
    {
        key ??= KeyFromEnvironment();
        SlaManifestExport? doc;
        try { doc = JsonSerializer.Deserialize<SlaManifestExport>(File.ReadAllText(path)); }
        catch (JsonException) { return false; }
        if (doc is null || doc.Records is null || doc.Records.Count != doc.Count) return false;
        if (!VerifyChain(doc.Records, key)) return false;
        if (doc.Count > 0 && !HexEquals(doc.Records[^1].Hash, doc.HeadHash)) return false;
        return HexEquals(doc.Signature, HashHex(key, AggregateCanonical(doc.Version, doc.Count, doc.Availability, doc.DowntimeMs, doc.WindowMs, doc.Incidents, doc.HeadHash)));
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Sla.Guard.OnChange", ex); }
    }

    public void Dispose()
    {
        Engine.FindingsDetected -= OnFindings;
        lock (_gate)
        {
            if (_globalAttached) TaskScheduler.UnobservedTaskException -= OnUnobserved;
            _globalAttached = false;
        }
        RestoreThreadPool();
    }
}
