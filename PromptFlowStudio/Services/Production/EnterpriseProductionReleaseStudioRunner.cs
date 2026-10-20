using System.Diagnostics;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.Production;

/// <summary>
/// [PromptFlow Studio v16.66.0] 🚀 엔터프라이즈 상용 릴리스 스튜디오 러너.
/// 데모 자산(스토리보드 16KB · 4D 월드 8KB · AI 핫패치 4KB) 3티어 복제 · 5개 런타임 모듈(렌더러 · 오디오 · 동기화 에이전트 · AI 추론 · 인코더) 감사.
/// 틱: 로컬 스토리보드 부분 편집(15틱) · 4D 파라미터 조정(60틱) → OTA 델타 동기화 → 모듈 하트비트/메모리 샘플(주입 결함 반영) → 자율 복구.
/// 결함 주입: 예외 버스트 · 누수 · 데드락 · 행 · 패킷 변조 · 링크 단절. 1-Click 패키징: 5 플랫폼 패키징 → 서명 → 검증(Windows는 실 산출물 우선).
/// 30Hz PeriodicTimer · <see cref="TickOnce"/> 무할당.
/// </summary>
public sealed class EnterpriseProductionReleaseStudioRunner : IDisposable
{
    public const int TickHz = 30;
    public const string Version = "16.66.0";
    public static readonly string[] ModuleNames = { "렌더러", "오디오 엔진", "동기화 에이전트", "AI 추론", "인코더" };
    private static readonly InvalidOperationException InjectedFault = new("주입된 런타임 결함 — api_key=sk-demoSECRET12345 user@example.com C:\\Users\\operator\\cache");

    private readonly object _gate = new();
    private readonly GlobalCloudEdgeOtaSyncEngine _sync;
    private readonly AiSelfHealingQaTelemetryAuditor _qa;
    private readonly EnterpriseProductionPackagingVerifier _pkg;
    private readonly int[] _modules = new int[5];
    private readonly bool[] _hung = new bool[5], _leaking = new bool[5];
    private readonly long[] _mem = new long[5];
    private readonly byte[] _edit = new byte[64];
    private CancellationTokenSource? _cts, _pkgCts;
    private uint _rng = 0x6D2B79F5;
    private long _frameSeq;
    private int _packaging;
    private IReadOnlyList<PlatformVerifyReport> _reports = Array.Empty<PlatformVerifyReport>();

    public EnterpriseProductionReleaseStudioRunner(IServiceProvider sp)
        : this((GlobalCloudEdgeOtaSyncEngine?)sp.GetService(typeof(GlobalCloudEdgeOtaSyncEngine)) ?? new GlobalCloudEdgeOtaSyncEngine(),
               (AiSelfHealingQaTelemetryAuditor?)sp.GetService(typeof(AiSelfHealingQaTelemetryAuditor)) ?? new AiSelfHealingQaTelemetryAuditor(),
               (EnterpriseProductionPackagingVerifier?)sp.GetService(typeof(EnterpriseProductionPackagingVerifier)) ?? new EnterpriseProductionPackagingVerifier()) { }

    public EnterpriseProductionReleaseStudioRunner(GlobalCloudEdgeOtaSyncEngine sync, AiSelfHealingQaTelemetryAuditor qa, EnterpriseProductionPackagingVerifier pkg)
    {
        _sync = sync; _qa = qa; _pkg = pkg;
        if (_sync.AssetCount == 0)
        {
            StoryboardAsset = _sync.Register("storyboard.pfs", SyncAssetKind.Storyboard, Seed(16 * 1024, 1));
            WorldAsset = _sync.Register("world4d.params", SyncAssetKind.World4D, Seed(8 * 1024, 2));
            HotPatchAsset = _sync.Register("ai-hotpatch.bin", SyncAssetKind.HotPatch, Seed(4 * 1024, 3));
        }
        else { StoryboardAsset = 0; WorldAsset = Math.Min(1, _sync.AssetCount - 1); HotPatchAsset = _sync.AssetCount - 1; }
        if (_qa.ModuleCount == 0)
            for (var i = 0; i < _modules.Length; i++) _modules[i] = _qa.Register(ModuleNames[i], OnRestart);
        else
            for (var i = 0; i < _modules.Length; i++) _modules[i] = Math.Min(i, _qa.ModuleCount - 1);
        for (var i = 0; i < _mem.Length; i++) _mem[i] = 64L * 1024 * 1024;
    }

    public event Action? OnChange;

    public GlobalCloudEdgeOtaSyncEngine Sync => _sync;
    public AiSelfHealingQaTelemetryAuditor Qa => _qa;
    public EnterpriseProductionPackagingVerifier Packager => _pkg;
    public int StoryboardAsset { get; }
    public int WorldAsset { get; }
    public int HotPatchAsset { get; }
    public bool IsRunning => _cts is not null;
    public bool IsPackaging => Volatile.Read(ref _packaging) != 0;
    public long FrameSeq => Interlocked.Read(ref _frameSeq);
    public IReadOnlyList<PlatformVerifyReport> Reports => Volatile.Read(ref _reports);
    public string? LastAction { get; private set; }

    public int ModuleId(int i) => _modules[i];
    public bool IsHung(int i) { lock (_gate) return _hung[i]; }
    public bool IsLeaking(int i) { lock (_gate) return _leaking[i]; }

    private static byte[] Seed(int n, uint s)
    {
        var b = new byte[n];
        for (var i = 0; i < n; i++) { s = s * 1664525 + 1013904223; b[i] = (byte)(s >> 24); }
        return b;
    }

    private void OnRestart(int module)
    {
        lock (_gate)
            for (var i = 0; i < _modules.Length; i++)
                if (_modules[i] == module) { _hung[i] = false; _leaking[i] = false; _mem[i] = 64L * 1024 * 1024; }
    }

    // ── 결함 주입 ──
    public void InjectExceptionBurst(int i)
    {
        for (var k = 0; k < AiSelfHealingQaTelemetryAuditor.BurstCount; k++) _qa.ReportException(_modules[i], InjectedFault);
        Act("💥 " + ModuleNames[i] + " 예외 버스트 주입");
    }

    public void InjectLeak(int i) { lock (_gate) _leaking[i] = true; Act("💧 " + ModuleNames[i] + " 메모리 누수 주입"); }
    public void InjectHang(int i) { lock (_gate) _hung[i] = true; Act("🧊 " + ModuleNames[i] + " 행(하트비트 정지) 주입"); }

    public void InjectDeadlock(int a, int b)
    {
        _qa.BeginWait(_modules[a], _modules[b]);
        _qa.BeginWait(_modules[b], _modules[a]);
        Act("🔒 " + ModuleNames[a] + " ↔ " + ModuleNames[b] + " 데드락 주입");
    }

    /// <summary>클라우드 발행 AI 핫패치(클라우드 → 에지 → 로컬 우선 전파).</summary>
    public void PublishHotPatch()
    {
        Span<byte> p = stackalloc byte[512];
        lock (_gate) for (var i = 0; i < p.Length; i++) p[i] = (byte)Next();
        _sync.Edit(HotPatchAsset, SyncTier.Cloud, 0, p);
        Act("🩹 클라우드 AI 핫패치 발행");
    }

    private uint Next() { _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5; return _rng; }

    /// <summary>1틱(무할당).</summary>
    public void TickOnce(double dtMs)
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Production.Runner.Tick");
            if (_frameSeq % 15 == 0)
            {
                for (var i = 0; i < _edit.Length; i++) _edit[i] = (byte)Next();
                _sync.Edit(StoryboardAsset, SyncTier.Local, (int)(Next() % (uint)(16 * 1024 - _edit.Length)), _edit);
            }
            if (_frameSeq % 60 == 30) _sync.Edit(WorldAsset, SyncTier.Local, (int)(Next() % 8000u), _edit.AsSpan(0, 32));
            _sync.Tick(dtMs);
            for (var i = 0; i < _modules.Length; i++)
            {
                if (!_hung[i] && _qa.IsAllowed(_modules[i])) _qa.Heartbeat(_modules[i]);
                _mem[i] += _leaking[i] ? 256 * 1024 : (long)(Next() % 4096) - 2048;
                if ((_frameSeq & 1) == 0) _qa.SampleMemory(_modules[i], _mem[i]);
            }
            _qa.Tick(dtMs);
            Interlocked.Increment(ref _frameSeq);
        }
    }

    /// <summary>1-Click 엔터프라이즈 패키징(5 플랫폼) — 진행 중이면 취소 토글.</summary>
    public void TogglePackaging()
    {
        if (Interlocked.Exchange(ref _packaging, 1) != 0)
        {
            try { Volatile.Read(ref _pkgCts)?.Cancel(); } catch (ObjectDisposedException) { }   // 해제는 완료 측 finally
            return;
        }
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _pkgCts, cts)?.Dispose();
        _ = Task.Run(() => PackageAllAsync(cts));
        Act("📦 엔터프라이즈 패키징 시작");
    }

    private async Task PackageAllAsync(CancellationTokenSource cts)
    {
        try { await PackageAllAsync(useRealWindows: true, cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { Act("⏹ 패키징 취소"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { PromptFlowStudio.Program.Log("Production.Package", ex); Act("⚠ 패키징 실패 — " + ex.GetType().Name); }
        finally
        {
            if (Interlocked.CompareExchange(ref _pkgCts, null, cts) == cts) cts.Dispose();
            Volatile.Write(ref _packaging, 0);
            Raise();
        }
    }

    /// <summary>5 플랫폼 패키징 → 검증(공개키만). Windows는 실 산출물(실행 폴더) 우선 · 없으면 데모.</summary>
    public async Task<IReadOnlyList<PlatformVerifyReport>> PackageAllAsync(bool useRealWindows, CancellationToken ct)
    {
        var list = new List<PlatformVerifyReport>(EnterpriseProductionPackagingVerifier.AllPlatforms.Length);
        foreach (var p in EnterpriseProductionPackagingVerifier.AllPlatforms)
        {
            ct.ThrowIfCancellationRequested();
            var arts = p == ReleasePlatform.Windows && useRealWindows ? EnterpriseProductionPackagingVerifier.ScanDirectory(AppContext.BaseDirectory) : null;
            if (arts is null || arts.Count == 0) arts = EnterpriseProductionPackagingVerifier.DemoArtifacts(p, Version);
            var opts = EnterpriseProductionPackagingVerifier.RecommendedOptions(p);
            var m = await Task.Run(() => _pkg.Package(p, Version, opts, arts, ct), ct).ConfigureAwait(false);
            list.Add(_pkg.Verify(m, arts, _pkg.PublicKey.Span, ct));
            Volatile.Write(ref _reports, list.ToArray());
            Raise();
        }
        var pass = 0;
        foreach (var r in list) if (r.Passed) pass++;
        Act("📦 패키징 완료 · " + pass.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + list.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 통과");
        return list;
    }

    public void Start()
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(() => LoopAsync(cts.Token));
        Act("▶ 릴리스 관제 라이브");
    }

    public void Stop()
    {
        var c = Interlocked.Exchange(ref _cts, null);
        if (c is null) return;
        c.Cancel(); c.Dispose();
        Act("⏸ 정지");
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / TickHz));
        var last = Stopwatch.GetTimestamp();
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var now = Stopwatch.GetTimestamp();
                var dt = Math.Min(200.0, (now - last) * 1000.0 / Stopwatch.Frequency);
                last = now;
                try { TickOnce(dt); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("Production.Tick", ex); }
                if ((FrameSeq & 3) == 0) Raise();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Act(string msg) { LastAction = msg; Raise(); }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Production.Runner.OnChange", ex); }
    }

    public void Dispose()
    {
        Stop();
        var p = Interlocked.Exchange(ref _pkgCts, null);
        if (p is not null) { p.Cancel(); p.Dispose(); }
    }
}
