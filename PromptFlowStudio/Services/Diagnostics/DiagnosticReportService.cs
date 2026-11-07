using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Render;

namespace PromptFlowStudio.Services.Diagnostics;

/// <summary>
/// 🩺 원클릭 진단 번들 — OS/아키텍처 · GPU 인코더 프로브 · 최근 로그(500줄) · 익명화 설정 요약 · StockSuite 헬스를
/// <c>pfs_diagnostic_{timestamp}.zip</c> 로 패키징. 시크릿 키는 값 마스킹, 사용자 프로필 경로는 <c>%USERPROFILE%</c> 치환.
/// <c>manifest.json</c> 에 엔트리별 SHA-256 기록(무결성 검증 <see cref="VerifyBundle"/>).
/// </summary>
public sealed class DiagnosticReportService
{
    public const int LogTailLines = 500;
    public const string SystemEntry = "system.txt";
    public const string EncoderEntry = "encoder_probe.txt";
    public const string LogEntry = "logs/recent.log";
    public const string ConfigEntry = "config_summary.json";
    public const string HealthEntry = "stocksuite_health.txt";
    public const string ManifestEntry = "manifest.json";
    public const string Redacted = "***REDACTED***";
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(3);
    private static readonly string[] SecretMarkers = ["key", "secret", "token", "password", "pwd", "credential", "auth", "bearer"];

    private readonly HardwareAccelDetector? _accel;
    private readonly StockSuiteHealthService? _health;
    private readonly Func<string?> _logPath;
    private readonly Func<IEnumerable<KeyValuePair<string, string?>>>? _config;
    private readonly Func<string?>? _ffmpegPath;

    public Func<DateTimeOffset> UtcNow { get; set; } = static () => DateTimeOffset.UtcNow;

    public DiagnosticReportService(HardwareAccelDetector? accel = null, StockSuiteHealthService? health = null, Func<string?>? logPathProvider = null,
        Func<IEnumerable<KeyValuePair<string, string?>>>? configProvider = null, Func<string?>? ffmpegPathProvider = null)
    {
        _accel = accel;
        _health = health;
        _logPath = logPathProvider ?? DefaultLogPath;
        _config = configProvider;
        _ffmpegPath = ffmpegPathProvider;
    }

    public static string DefaultLogPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", "logs", "shell.log");

    public static string BundleFileName(DateTimeOffset now) => "pfs_diagnostic_" + now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".zip";

    /// <summary>
    /// 번들 생성 → zip 전체 경로 반환. <paramref name="outputPath"/> 가 <c>.zip</c> 이면 그 경로, 아니면 폴더로 보고 타임스탬프 파일명 생성.
    /// 수집 단계별 예외는 격리(해당 섹션에 오류 문구 기록) — 번들 자체는 항상 생성.
    /// </summary>
    public async Task<string> GenerateReportBundleAsync(string outputPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var now = UtcNow();
        var zipPath = outputPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? Path.GetFullPath(outputPath) : Path.Combine(Path.GetFullPath(outputPath), BundleFileName(now));
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);

        var entries = new List<KeyValuePair<string, string>>(6)
        {
            new(SystemEntry, BuildSystemSpecs(now)),
            new(EncoderEntry, await SafeAsync(() => BuildEncoderProbeAsync(ct), "encoder probe").ConfigureAwait(false)),
            new(LogEntry, await SafeAsync(() => Task.FromResult(ReadLogTail(_logPath(), LogTailLines)), "log tail").ConfigureAwait(false)),
            new(ConfigEntry, await SafeAsync(() => Task.FromResult(BuildConfigSummary(_config?.Invoke())), "config").ConfigureAwait(false)),
            new(HealthEntry, await SafeAsync(() => BuildHealthAsync(ct), "stocksuite health").ConfigureAwait(false)),
        };
        ct.ThrowIfCancellationRequested();

        var temp = zipPath + ".~pfs";
        try
        {
            await using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
            {
                var manifest = new Dictionary<string, string>(StringComparer.Ordinal);
                for (var i = 0; i < entries.Count; i++)
                {
                    var bytes = Encoding.UTF8.GetBytes(entries[i].Value);
                    manifest[entries[i].Key] = Convert.ToHexString(SHA256.HashData(bytes));
                    var e = zip.CreateEntry(entries[i].Key, CompressionLevel.Optimal);
                    await using var es = e.Open();
                    await es.WriteAsync(bytes, ct).ConfigureAwait(false);
                }
                var m = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
                {
                    ["generatedAt"] = now.ToString("O", CultureInfo.InvariantCulture),
                    ["entryCount"] = manifest.Count,
                    ["sha256"] = manifest,
                }, new JsonSerializerOptions { WriteIndented = true });
                await using var ms = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal).Open();
                await ms.WriteAsync(m, ct).ConfigureAwait(false);
            }
            File.Move(temp, zipPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        return zipPath;
    }

    /// <summary>번들 무결성 — 필수 엔트리 존재 + manifest SHA-256 일치(고정시간 비교). 실패 사유 또는 null.</summary>
    public static string? VerifyBundle(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var me = zip.GetEntry(ManifestEntry);
            if (me is null) return "manifest 없음";
            using var doc = JsonDocument.Parse(ReadAll(me));
            if (!doc.RootElement.TryGetProperty("sha256", out var hashes)) return "manifest 해시 없음";
            string[] required = [SystemEntry, EncoderEntry, LogEntry, ConfigEntry, HealthEntry];
            for (var i = 0; i < required.Length; i++)
            {
                var e = zip.GetEntry(required[i]);
                if (e is null) return "엔트리 누락: " + required[i];
                if (!hashes.TryGetProperty(required[i], out var h) || h.GetString() is not { } hex) return "해시 누락: " + required[i];
                var actual = SHA256.HashData(ReadAll(e));
                byte[] expected;
                try { expected = Convert.FromHexString(hex); } catch (FormatException) { return "해시 형식 오류: " + required[i]; }
                if (!CryptographicOperations.FixedTimeEquals(actual, expected)) return "해시 불일치: " + required[i];
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { return "번들 손상: " + ex.Message; }
    }

    private static byte[] ReadAll(ZipArchiveEntry e)
    {
        using var s = e.Open();
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    private static async Task<string> SafeAsync(Func<Task<string>> f, string section)
    {
        try { return await f().ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Program.Log("Diag.Bundle", ex); return "⚠ " + section + " 수집 실패: " + ex.GetType().Name + " — " + Anonymize(ex.Message); }
    }

    internal static string BuildSystemSpecs(DateTimeOffset now)
    {
        var sb = new StringBuilder(512);
        sb.Append("generated_utc: ").AppendLine(now.ToString("O", CultureInfo.InvariantCulture));
        sb.Append("os: ").AppendLine(RuntimeInformation.OSDescription);
        sb.Append("os_version: ").AppendLine(Environment.OSVersion.VersionString);
        sb.Append("os_arch: ").AppendLine(RuntimeInformation.OSArchitecture.ToString());
        sb.Append("process_arch: ").AppendLine(RuntimeInformation.ProcessArchitecture.ToString());
        sb.Append("runtime: ").AppendLine(RuntimeInformation.FrameworkDescription);
        sb.Append("cpu_count: ").AppendLine(Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture));
        sb.Append("is_64bit_process: ").AppendLine(Environment.Is64BitProcess ? "true" : "false");
        sb.Append("working_set_mb: ").AppendLine((Environment.WorkingSet / (1024 * 1024)).ToString(CultureInfo.InvariantCulture));
        sb.Append("gc_heap_mb: ").AppendLine((GC.GetTotalMemory(false) / (1024 * 1024)).ToString(CultureInfo.InvariantCulture));
        sb.Append("app_version: ").AppendLine(typeof(DiagnosticReportService).Assembly.GetName().Version?.ToString() ?? "unknown");
        sb.Append("culture: ").AppendLine(CultureInfo.CurrentCulture.Name);
        return sb.ToString();
    }

    private async Task<string> BuildEncoderProbeAsync(CancellationToken ct)
    {
        var sb = new StringBuilder(256);
        string? ff = null;
        if (_ffmpegPath is not null)
        {
            try { ff = _ffmpegPath(); } catch (Exception ex) { sb.Append("ffmpeg_locate_error: ").AppendLine(ex.GetType().Name); }
        }
        sb.Append("ffmpeg: ").AppendLine(string.IsNullOrWhiteSpace(ff) ? "not found" : Anonymize(ff) + (File.Exists(ff) ? "" : " (missing on disk)"));
        if (_accel is null) { sb.AppendLine("gpu_detector: not configured (CPU only)"); return sb.ToString(); }
        var v = await _accel.DetectAsync(ct).ConfigureAwait(false);
        sb.Append("probed: ").AppendLine(_accel.IsProbed ? "true" : "false");
        sb.Append("active_vendor: ").AppendLine(v.ToString());
        sb.Append("active_label: ").AppendLine(HardwareAccelDetector.Label(v));
        sb.Append("h264_encoder: ").AppendLine(HardwareAccelDetector.H264Encoder(v));
        sb.Append("hevc_encoder: ").AppendLine(HardwareAccelDetector.HevcEncoder(v) ?? "n/a");
        return sb.ToString();
    }

    private async Task<string> BuildHealthAsync(CancellationToken ct)
    {
        if (_health is null) return "stocksuite_health: not configured";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(HealthTimeout);
        HealthCheckResult r;
        try { r = await _health.ValidateIntegrationHealthAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return "stocksuite_health: timeout (" + HealthTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "s)"; }
        var sb = new StringBuilder(256);
        sb.Append("overall: ").AppendLine(r.Overall.ToString());
        sb.Append("ipc_latency_ms: ").AppendLine(r.IpcLatencyMs.ToString("0.0", CultureInfo.InvariantCulture));
        sb.Append("checked_at: ").AppendLine(r.CheckedAt.ToString("O", CultureInfo.InvariantCulture));
        for (var i = 0; i < r.Items.Count; i++)
        {
            var it = r.Items[i];
            sb.Append("- ").Append(it.Check.ToString()).Append(": ").Append(it.Status.ToString()).Append(" — ").AppendLine(Anonymize(it.Message));
        }
        return sb.ToString();
    }

    /// <summary>로그 꼬리 N줄(공유 읽기 · 링버퍼) + 익명화. 파일 부재 시 안내 문구.</summary>
    internal static string ReadLogTail(string? path, int maxLines)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return "(log not found: " + Anonymize(path ?? "") + ")";
        var ring = new Queue<string>(maxLines + 1);
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            string? line;
            while ((line = sr.ReadLine()) is not null)
            {
                ring.Enqueue(line);
                if (ring.Count > maxLines) ring.Dequeue();
            }
        }
        var sb = new StringBuilder(ring.Count * 80);
        foreach (var l in ring) sb.AppendLine(Anonymize(l));
        return sb.ToString();
    }

    /// <summary>설정 요약 JSON — 시크릿성 키는 값 마스킹, 값의 사용자 경로 익명화.</summary>
    internal static string BuildConfigSummary(IEnumerable<KeyValuePair<string, string?>>? config)
    {
        var map = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        if (config is not null)
            foreach (var kv in config)
                map[kv.Key] = IsSecretKey(kv.Key) ? (string.IsNullOrEmpty(kv.Value) ? "" : Redacted) : (kv.Value is null ? null : Anonymize(kv.Value));
        return JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>활성 환경설정 요약(경로/토글만 — 시크릿 미포함). 서비스 부재 시 빈 목록.</summary>
    public static IEnumerable<KeyValuePair<string, string?>> SummarizePreferences(PromptFlowStudio.Services.Ui.UserPreferenceService? p)
    {
        if (p is null) return Array.Empty<KeyValuePair<string, string?>>();
        return
        [
            new("ffmpeg.binaryPath", p.FfmpegBinaryPath),
            new("stocksuite.assetPath", p.StockSuiteAssetPath),
            new("stocksuite.executablePath", p.StockSuiteExecutablePath),
            new("stocksuite.format", p.StockSuiteFormat),
            new("stocksuite.sharpen", p.StockSuiteSharpen ? "true" : "false"),
            new("stocksuite.smooth60", p.StockSuiteSmooth60 ? "true" : "false"),
            new("render.cacheMaxGb", p.RenderCacheMaxGb.ToString(CultureInfo.InvariantCulture)),
        ];
    }

    public static bool IsSecretKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        for (var i = 0; i < SecretMarkers.Length; i++)
            if (key.Contains(SecretMarkers[i], StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>사용자 프로필 경로·사용자명·머신명 치환.</summary>
    public static string Anonymize(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 3) s = s.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        var user = Environment.UserName;
        if (user.Length >= 3) s = s.Replace(user, "<user>", StringComparison.OrdinalIgnoreCase);
        var machine = Environment.MachineName;
        if (machine.Length >= 3) s = s.Replace(machine, "<host>", StringComparison.OrdinalIgnoreCase);
        return s;
    }
}
