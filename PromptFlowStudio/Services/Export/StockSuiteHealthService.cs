using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.ProcessBridge;

namespace PromptFlowStudio.Services.Export;

/// <summary>🩺 연동 진단 등급 — 최악 항목이 전체 등급.</summary>
public enum IntegrationHealth { Healthy = 0, Degraded = 1, Unhealthy = 2 }

/// <summary>🩺 진단 항목 키.</summary>
public enum IntegrationCheck { AssetDirectory, DisplaySchema, ManifestIntegrity, IpcPipeConnection }

/// <summary>🩺 단일 항목 진단 — <see cref="Repairable"/> = 1클릭 복구 대상.</summary>
public sealed record HealthCheckItem(IntegrationCheck Check, IntegrationHealth Status, string Message, bool Repairable);

/// <summary>🩺 IPC 프로브 결과 — 응답 지연(ms, 실패 시 -1).</summary>
public sealed record IpcProbeResult(IntegrationHealth Status, double LatencyMs, string Message, bool Repairable = false);

/// <summary>🩺 StockSuite 연동 진단 리포트.</summary>
public sealed record HealthCheckResult(IntegrationHealth Overall, IReadOnlyList<HealthCheckItem> Items, double IpcLatencyMs, DateTimeOffset CheckedAt)
{
    public bool NeedsRepair
    {
        get
        {
            for (var i = 0; i < Items.Count; i++) if (Items[i].Status != IntegrationHealth.Healthy && Items[i].Repairable) return true;
            return false;
        }
    }

    public HealthCheckItem? Find(IntegrationCheck c)
    {
        for (var i = 0; i < Items.Count; i++) if (Items[i].Check == c) return Items[i];
        return null;
    }
}

/// <summary>🩹 1클릭 복구 결과 — 복구 후 재진단 포함.</summary>
public sealed record HealthRepairResult(int Repaired, IReadOnlyList<string> Actions, HealthCheckResult After)
{
    public bool Ok => After.Overall != IntegrationHealth.Unhealthy;
}

/// <summary>
/// 🩺 StockSuite 연동 헬스 닥터 — 에셋 폴더 권한 · <c>stocksuite_displays.json</c> 스키마 · <c>.stocksuite_manifest.json</c> 무결성 · <c>\\.\pipe\StockSuiteBridge</c> 연결/지연 4대 진단.
/// 🩹 복구: 폴더 생성(상위 존재 시) · 깨진 JSON 은 <c>.corrupt</c> 로 보존 후 표준 규격 원자적 재작성 · 중지된 IPC 브릿지 재기동.
/// 예외 없이 결과 반환(항목별 격리).
/// </summary>
public sealed class StockSuiteHealthService
{
    public const string CorruptSuffix = ".corrupt";
    public const int PipeProbeTimeoutMs = 250;
    private const string ProbeFilePrefix = ".pfs-health-";

    private readonly Func<string?> _targetDir;
    private readonly StockSuiteIpcBridge? _bridge;
    private readonly string _pipeName;

    public StockSuiteHealthService(Func<string?> targetDirectory, StockSuiteIpcBridge? bridge = null, string? pipeName = null)
    {
        _targetDir = targetDirectory ?? (static () => null);
        _bridge = bridge;
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? bridge?.PipeName ?? StockSuiteIpcBridge.DefaultPipeName : pipeName;
    }

    public Func<DateTimeOffset> UtcNow { get; set; } = static () => DateTimeOffset.UtcNow;

    /// <summary>IPC 프로브 교체점(테스트) — null 이면 기본(브릿지 상태 또는 클라이언트 접속 측정).</summary>
    public Func<CancellationToken, Task<IpcProbeResult>>? IpcProbe { get; set; }

    private string? Dir
    {
        get
        {
            try { var d = _targetDir(); return string.IsNullOrWhiteSpace(d) ? null : d.Trim().Trim('"'); }
            catch (Exception ex) { Program.Log("StockSuite.Health.Path", ex); return null; }
        }
    }

    public async Task<HealthCheckResult> ValidateIntegrationHealthAsync(CancellationToken ct = default)
    {
        var dir = Dir;
        var items = new List<HealthCheckItem>(4);
        var dirItem = Safe(IntegrationCheck.AssetDirectory, () => CheckDirectory(dir));
        items.Add(dirItem);
        var dirOk = dirItem.Status != IntegrationHealth.Unhealthy;
        items.Add(dirOk ? Safe(IntegrationCheck.DisplaySchema, () => CheckDisplay(dir!)) : Skipped(IntegrationCheck.DisplaySchema));
        items.Add(dirOk ? Safe(IntegrationCheck.ManifestIntegrity, () => CheckManifest(dir!)) : Skipped(IntegrationCheck.ManifestIntegrity));

        IpcProbeResult ipc;
        try { ipc = await (IpcProbe ?? DefaultProbeAsync)(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Program.Log("StockSuite.Health.Ipc", ex); ipc = new(IntegrationHealth.Unhealthy, -1, "IPC 진단 실패 — " + ex.GetType().Name); }
        items.Add(new HealthCheckItem(IntegrationCheck.IpcPipeConnection, ipc.Status, ipc.Message, ipc.Repairable));

        var overall = IntegrationHealth.Healthy;
        for (var i = 0; i < items.Count; i++) if (items[i].Status > overall) overall = items[i].Status;
        return new HealthCheckResult(overall, items, ipc.LatencyMs, UtcNow());
    }

    /// <summary>🩹 1클릭 복구 — 비정상 + 복구 가능 항목만 표준 규격 재작성 후 재진단.</summary>
    public async Task<HealthRepairResult> RepairAsync(HealthCheckResult? report = null, CancellationToken ct = default)
    {
        report ??= await ValidateIntegrationHealthAsync(ct).ConfigureAwait(false);
        var actions = new List<string>(4);
        var dir = Dir;
        var ok = 0;
        if (dir is not null)
        {
            if (Bad(report, IntegrationCheck.AssetDirectory))
                ok += TryAct(actions, "에셋 폴더", () => RepairDirectory(dir));
            if (Directory.Exists(dir))
            {
                if (Bad(report, IntegrationCheck.DisplaySchema)) ok += TryAct(actions, "디스플레이 설정", () => RepairDisplay(dir));
                if (Bad(report, IntegrationCheck.ManifestIntegrity)) ok += TryAct(actions, "매니페스트", () => RepairManifest(dir));
            }
        }
        if (Bad(report, IntegrationCheck.IpcPipeConnection) && _bridge is { IsRunning: false } b)
            ok += TryAct(actions, "IPC 브릿지", () => { b.Start(); return "🔌 StockSuiteBridge 파이프 서버 재기동"; });

        var after = await ValidateIntegrationHealthAsync(ct).ConfigureAwait(false);
        return new HealthRepairResult(ok, actions, after);

        static bool Bad(HealthCheckResult r, IntegrationCheck c) => r.Find(c) is { Status: not IntegrationHealth.Healthy, Repairable: true };
    }

    public static string Label(IntegrationCheck c) => c switch
    {
        IntegrationCheck.AssetDirectory => "에셋 폴더 권한",
        IntegrationCheck.DisplaySchema => "디스플레이 규격",
        IntegrationCheck.ManifestIntegrity => "매니페스트 무결성",
        _ => "IPC 파이프",
    };

    public static string Badge(IntegrationHealth h) => h switch
    {
        IntegrationHealth.Healthy => "🟢 Healthy",
        IntegrationHealth.Degraded => "🟡 Degraded",
        _ => "🔴 Unhealthy",
    };

    // ══════════════════════ 진단 ══════════════════════

    private static HealthCheckItem CheckDirectory(string? dir)
    {
        if (dir is null) return new(IntegrationCheck.AssetDirectory, IntegrationHealth.Unhealthy, "StockSuite 에셋 폴더 미설정 — 환경설정에서 경로를 지정하세요.", false);
        if (!Directory.Exists(dir))
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(dir));
            var canCreate = parent is not null && Directory.Exists(parent);
            return new(IntegrationCheck.AssetDirectory, IntegrationHealth.Unhealthy, "에셋 폴더 없음 — " + dir, canCreate);
        }
        var probe = Path.Combine(dir, ProbeFilePrefix + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(probe, new byte[] { 0x50, 0x46, 0x53 });
            File.Delete(probe);
            return new(IntegrationCheck.AssetDirectory, IntegrationHealth.Healthy, "쓰기/삭제 권한 정상", false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(probe)) File.Delete(probe); } catch (Exception ex2) when (ex2 is IOException or UnauthorizedAccessException) { }
            return new(IntegrationCheck.AssetDirectory, IntegrationHealth.Unhealthy, "쓰기/삭제 권한 없음 — " + ex.GetType().Name, false);
        }
    }

    internal static HealthCheckItem CheckDisplay(string dir)
    {
        var p = Path.Combine(dir, StockSuiteExporter.DisplayConfigName);
        if (!File.Exists(p)) return new(IntegrationCheck.DisplaySchema, IntegrationHealth.Degraded, StockSuiteExporter.DisplayConfigName + " 없음 — 기본 듀얼 규격 생성 가능", true);
        var err = ValidateDisplayJson(File.ReadAllBytes(p));
        return err is null
            ? new(IntegrationCheck.DisplaySchema, IntegrationHealth.Healthy, "듀얼 디스플레이 규격 유효", false)
            : new(IntegrationCheck.DisplaySchema, IntegrationHealth.Unhealthy, "규격 위반 — " + err, true);
    }

    /// <summary>디스플레이 JSON 스키마 검증 — 위반 사유(정상 = null).</summary>
    public static string? ValidateDisplayJson(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
            using var doc = JsonDocument.ParseValue(ref reader);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return "루트가 객체가 아님";
            if (!r.TryGetProperty("mode", out var mode) || mode.ValueKind != JsonValueKind.String || (mode.GetString() ?? "").Length == 0) return "mode 누락";
            if (!r.TryGetProperty("bezelGapPixels", out var bz) || !bz.TryGetInt32(out var gap) || gap < 0 || gap > StockSuiteExporter.MaxBezelGapPixels) return "bezelGapPixels 범위 위반";
            if (!r.TryGetProperty("screens", out var screens) || screens.ValueKind != JsonValueKind.Array || screens.GetArrayLength() == 0) return "screens 배열 누락";
            foreach (var s in screens.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object) return "screen 항목이 객체가 아님";
                if (!s.TryGetProperty("asset", out var a) || a.ValueKind != JsonValueKind.String) return "screen.asset 누락";
                var name = a.GetString() ?? "";
                if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "screen.asset 파일명 위반";
                if (!PositiveInt(s, "width") || !PositiveInt(s, "height")) return "screen 해상도 위반";
                if (!s.TryGetProperty("index", out var ix) || !ix.TryGetInt32(out _)) return "screen.index 누락";
            }
            return null;
        }
        catch (JsonException) { return "JSON 파싱 실패"; }
        catch (InvalidOperationException) { return "타입 불일치"; }

        static bool PositiveInt(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) && i > 0;
    }

    internal static HealthCheckItem CheckManifest(string dir)
    {
        var p = Path.Combine(dir, StockSuiteExporter.ManifestName);
        var active = FindActiveAsset(dir);
        if (!File.Exists(p))
            return active is null
                ? new(IntegrationCheck.ManifestIntegrity, IntegrationHealth.Healthy, "내보낸 에셋 없음 — 매니페스트 불필요", false)
                : new(IntegrationCheck.ManifestIntegrity, IntegrationHealth.Degraded, "매니페스트 없음 — " + Path.GetFileName(active) + " 출처 추적 불가", true);
        var m = StockSuiteExporter.TryReadManifest(dir);
        if (m is null) return new(IntegrationCheck.ManifestIntegrity, IntegrationHealth.Unhealthy, "매니페스트 손상 — JSON/스키마 위반", true);
        if (!IsSha256Hex(m.SourceSha256)) return new(IntegrationCheck.ManifestIntegrity, IntegrationHealth.Unhealthy, "sourceSha256 형식 위반", true);
        if (!File.Exists(Path.Combine(dir, m.ActiveAsset))) return new(IntegrationCheck.ManifestIntegrity, IntegrationHealth.Unhealthy, "활성 에셋 누락 — " + m.ActiveAsset, true);
        if (m.BackupAsset is { Length: > 0 } ba)
        {
            if (!IsSha256Hex(m.BackupSha256)) return new(IntegrationCheck.ManifestIntegrity, IntegrationHealth.Unhealthy, "backupSha256 형식 위반", true);
            var bp = Path.Combine(dir, ba);
            if (!File.Exists(bp)) return new(IntegrationCheck.ManifestIntegrity, IntegrationHealth.Degraded, "백업 파일 누락 — " + ba + " (롤백 불가)", true);
            if (m.BackupBytes > 0 && new FileInfo(bp).Length != m.BackupBytes) return new(IntegrationCheck.ManifestIntegrity, IntegrationHealth.Degraded, "백업 크기 불일치 — " + ba, true);
        }
        return new(IntegrationCheck.ManifestIntegrity, IntegrationHealth.Healthy, "해시·백업 무결성 정상", false);
    }

    public static bool IsSha256Hex(string? s)
    {
        if (s is null || s.Length != 64) return false;
        foreach (var c in s) if (!char.IsAsciiHexDigit(c)) return false;
        return true;
    }

    private async Task<IpcProbeResult> DefaultProbeAsync(CancellationToken ct)
    {
        var b = _bridge;
        if (b is not null)
        {
            if (b.IsConnected) return new(IntegrationHealth.Healthy, 0, "StockSuite 접속 중(양방향)");
            return b.IsRunning
                ? new(IntegrationHealth.Degraded, -1, "파이프 서버 대기 — StockSuite 미접속(파일 폴백)")
                : new(IntegrationHealth.Unhealthy, -1, "IPC 브릿지 중지됨", true);
        }
        // 외부 서버 프로브 — 클라이언트 접속 지연 측정.
        var sw = Stopwatch.StartNew();
        try
        {
            using var c = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await c.ConnectAsync(PipeProbeTimeoutMs, ct).ConfigureAwait(false);
            var ms = sw.Elapsed.TotalMilliseconds;
            return new(IntegrationHealth.Healthy, ms, @"\\.\pipe\" + _pipeName + " 응답 " + ms.ToString("0.0", CultureInfo.InvariantCulture) + "ms");
        }
        catch (TimeoutException) { return new(IntegrationHealth.Unhealthy, -1, @"\\.\pipe\" + _pipeName + " 응답 없음(" + PipeProbeTimeoutMs.ToString(CultureInfo.InvariantCulture) + "ms 초과)"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return new(IntegrationHealth.Unhealthy, -1, "파이프 연결 실패 — " + ex.GetType().Name);
        }
    }

    // ══════════════════════ 복구 ══════════════════════

    private static string RepairDirectory(string dir)
    {
        Directory.CreateDirectory(dir);
        return "📁 에셋 폴더 생성 — " + dir;
    }

    private string RepairDisplay(string dir)
    {
        var p = Path.Combine(dir, StockSuiteExporter.DisplayConfigName);
        PreserveCorrupt(p);
        var fmt = File.Exists(Path.Combine(dir, StockSuiteExporter.DualScreenAssetName(0, StockSuiteExportFormat.Mp4))) ? StockSuiteExportFormat.Mp4 : StockSuiteExportFormat.WebMVp9;
        var json = StockSuiteExporter.BuildDisplayConfigJson(StockSuiteExporter.DualScreenAssetName(0, fmt), StockSuiteExporter.DualScreenAssetName(1, fmt), StockSuiteExporter.DefaultBezelGapPixels, UtcNow());
        AtomicWrite(p, json);
        return "🖥️ " + StockSuiteExporter.DisplayConfigName + " 표준 듀얼 규격 재작성";
    }

    private string RepairManifest(string dir)
    {
        var p = Path.Combine(dir, StockSuiteExporter.ManifestName);
        var active = FindActiveAsset(dir);
        if (active is null)
        {
            if (!File.Exists(p)) return "🧾 매니페스트 — 활성 에셋 없음(조치 불필요)";
            PreserveCorrupt(p);
            File.Delete(p);
            return "🧾 고아 매니페스트 격리(" + CorruptSuffix + ")";
        }
        PreserveCorrupt(p);
        var name = Path.GetFileName(active);
        string? backup = null, backupSha = null;
        long backupBytes = 0;
        var bp = StockSuiteExporter.BackupPathFor(active);
        if (File.Exists(bp))
        {
            backup = Path.GetFileName(bp);
            backupSha = StockSuiteExporter.ComputeSha256Hex(bp);
            backupBytes = new FileInfo(bp).Length;
        }
        var m = new StockSuiteManifest(name, "repaired", StockSuiteExporter.ComputeSha256Hex(active), new FileInfo(active).Length, UtcNow(), backup, backup is null ? null : "repaired", backupSha, backupBytes);
        AtomicWrite(p, StockSuiteExporter.BuildManifestJson(m));
        return "🧾 매니페스트 재작성 — " + name + (backup is null ? "" : " · 백업 " + backup);
    }

    private static string? FindActiveAsset(string dir)
    {
        var mp4 = Path.Combine(dir, StockSuiteExporter.DefaultAssetName);
        var webm = Path.Combine(dir, StockSuiteExporter.WebMAssetName);
        var hasMp4 = File.Exists(mp4);
        var hasWebm = File.Exists(webm);
        if (hasMp4 && hasWebm) return File.GetLastWriteTimeUtc(webm) > File.GetLastWriteTimeUtc(mp4) ? webm : mp4;
        return hasMp4 ? mp4 : hasWebm ? webm : null;
    }

    private static void PreserveCorrupt(string path)
    {
        if (!File.Exists(path)) return;
        try { File.Copy(path, path + CorruptSuffix, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Program.Log("StockSuite.Health.Preserve", ex); }
    }

    private static void AtomicWrite(string path, string text)
    {
        var tmp = path + ".~pfs";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, true);
    }

    private static int TryAct(List<string> actions, string what, Func<string> act)
    {
        try { actions.Add(act()); return 1; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            Program.Log("StockSuite.Health.Repair", ex);
            actions.Add("⚠ " + what + " 복구 실패 — " + ex.GetType().Name);
            return 0;
        }
    }

    private static HealthCheckItem Safe(IntegrationCheck c, Func<HealthCheckItem> f)
    {
        try { return f(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Program.Log("StockSuite.Health." + c.ToString(), ex);
            return new(c, IntegrationHealth.Unhealthy, "진단 실패 — " + ex.GetType().Name, false);
        }
    }

    private static HealthCheckItem Skipped(IntegrationCheck c) => new(c, IntegrationHealth.Degraded, "에셋 폴더 문제로 진단 보류", true);
}
