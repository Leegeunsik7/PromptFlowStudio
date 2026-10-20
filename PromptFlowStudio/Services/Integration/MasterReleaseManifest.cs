using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Integration;

/// <summary>매니페스트 항목 1건(불변 · 배포 산출물 1파일의 SHA-256 지문).</summary>
public sealed record ReleaseFileEntry(string RelativePath, long SizeBytes, string Sha256Hex);

/// <summary>Air-Gapped 자립형 오프라인 라이선스(불변 · HMAC-SHA256 토큰 자체검증 · 외부 서버 조회 0).</summary>
public sealed record OfflineLicense(
    string Licensee, string Edition, int Seats,
    DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, string TokenHex)
{
    /// <summary>만료 여부(UTC 기준).</summary>
    public bool IsExpired => DateTimeOffset.UtcNow > ExpiresAt;

    /// <summary>잔여 일수(만료 시 0).</summary>
    public int DaysRemaining
    {
        get
        {
            var d = (ExpiresAt - DateTimeOffset.UtcNow).TotalDays;
            return d <= 0 ? 0 : (int)Math.Ceiling(d);
        }
    }
}

/// <summary>마스터 릴리스 매니페스트(불변 · 전 산출물 지문 + 루트 해시 + HMAC 서명 + 오프라인 라이선스).</summary>
public sealed record ReleaseManifest(
    string Product, string Version, DateTimeOffset BuiltAt,
    string Framework, string OsDescription, string Architecture,
    int FileCount, long TotalBytes, string RootHashHex, string SignatureHex,
    double DurationMs, IReadOnlyList<ReleaseFileEntry> Files, OfflineLicense? License)
{
    /// <summary>서명 동봉 여부(HMAC 비밀키 구성 시에만 true).</summary>
    public bool IsSigned => SignatureHex.Length > 0;

    /// <summary>총 용량(MB · 표시용).</summary>
    public double TotalMb => TotalBytes / 1024d / 1024d;

    /// <summary>폐쇄망 반출용 정규화 JSON(순수 <see cref="StringBuilder"/> · InvariantCulture · 외부 직렬화기 0).</summary>
    public string ToJson(bool includeFiles = true)
    {
        var sb = new StringBuilder(1024);
        sb.Append("{\"product\":\"").Append(Escape(Product))
          .Append("\",\"version\":\"").Append(Escape(Version))
          .Append("\",\"builtAt\":\"").Append(BuiltAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
          .Append("\",\"framework\":\"").Append(Escape(Framework))
          .Append("\",\"os\":\"").Append(Escape(OsDescription))
          .Append("\",\"arch\":\"").Append(Escape(Architecture))
          .Append("\",\"fileCount\":").Append(FileCount.ToString(CultureInfo.InvariantCulture))
          .Append(",\"totalBytes\":").Append(TotalBytes.ToString(CultureInfo.InvariantCulture))
          .Append(",\"durationMs\":").Append(DurationMs.ToString("0.###", CultureInfo.InvariantCulture))
          .Append(",\"rootHash\":\"").Append(RootHashHex)
          .Append("\",\"signature\":\"").Append(SignatureHex).Append('"');

        sb.Append(",\"license\":");
        if (License is null) sb.Append("null");
        else
            sb.Append("{\"licensee\":\"").Append(Escape(License.Licensee))
              .Append("\",\"edition\":\"").Append(Escape(License.Edition))
              .Append("\",\"seats\":").Append(License.Seats.ToString(CultureInfo.InvariantCulture))
              .Append(",\"issuedAt\":\"").Append(License.IssuedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
              .Append("\",\"expiresAt\":\"").Append(License.ExpiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
              .Append("\",\"token\":\"").Append(License.TokenHex).Append("\"}");

        if (includeFiles)
        {
            sb.Append(",\"files\":[");
            for (var i = 0; i < Files.Count; i++)
            {
                var f = Files[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"path\":\"").Append(Escape(f.RelativePath))
                  .Append("\",\"size\":").Append(f.SizeBytes.ToString(CultureInfo.InvariantCulture))
                  .Append(",\"sha256\":\"").Append(f.Sha256Hex).Append("\"}");
            }
            sb.Append(']');
        }
        return sb.Append('}').ToString();
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

/// <summary>
/// [PromptFlow Studio v9.0.3] 마스터 릴리스 매니페스트 생성기(관찰 가능 싱글턴 · 순수 BCL <see cref="SHA256"/>/<see cref="File"/> · 외부 NuGet 0).
///   ▸ <see cref="BuildAsync"/>: 배포 디렉터리의 산출물(*.dll · *.exe · *.json · *.pri)을 스트리밍 해시(<see cref="IncrementalHash"/>)로 1파일씩 SHA-256 지문화한다 — 전체 적재 0(LOH 미접촉).
///   ▸ 지문 집합을 경로 정렬 후 정규화 결합해 <b>루트 해시</b>로 봉인하고, <see cref="ConfigureSecret"/> 비밀키가 있으면 HMAC-SHA256 서명을 동봉한다.
///   ▸ <see cref="IssueLicense"/>: Air-Gapped 폐쇄망용 자립형 라이선스 토큰(HMAC) 발급 — 외부 라이선스 서버 조회 없이 <see cref="ValidateLicense"/> 로 현장 자체검증.
///   ▸ <see cref="ExportAsync"/>: 매니페스트를 JSON 파일로 반출(USB/오프라인 이관용) — 실패는 삼켜 <see cref="LastError"/> 로만 보고한다(제5헌장).
///   ⚠ <see cref="OnChange"/> 는 해시 스레드(백그라운드)에서 온다 — 컴포넌트는 InvokeAsync(StateHasChanged)로 마샬링(제3헌장). 잠긴 파일은 건너뛰고 계속 진행한다.
/// </summary>
public sealed class MasterReleaseManifest
{
    private const string ProductName = "PromptFlow Studio";
    private const string ProductVersion = "9.0.0";
    private const int MaxFiles = 512;

    private static readonly string[] ManifestPatterns = { "*.dll", "*.exe", "*.json", "*.runtimeconfig.json" };

    private readonly SemaphoreSlim _buildGate = new(1, 1);
    private string? _secret;

    /// <summary>상태 변경 방송(구독: 컴포넌트 OnInitialized · 해지: Dispose).</summary>
    public event Action? OnChange;
    private void Notify() => OnChange?.Invoke();

    // ── 관측 지표(HUD) ──
    public bool IsBuilding { get; private set; }
    public string? CurrentFile { get; private set; }
    public int HashedFiles { get; private set; }
    public long BuildCount { get; private set; }
    public string? RootHash { get; private set; }
    public DateTimeOffset? LastBuiltAt { get; private set; }
    public string? LastExportPath { get; private set; }
    public string? LastError { get; private set; }
    public ReleaseManifest? LastManifest { get; private set; }
    public OfflineLicense? License { get; private set; }

    /// <summary>제품 버전(마스터 릴리스 고정값).</summary>
    public string Version => ProductVersion;

    /// <summary>매니페스트 서명/라이선스 토큰 비밀키 구성(미구성 시 해시만 발급).</summary>
    public void ConfigureSecret(string? secret)
    {
        if (!string.IsNullOrWhiteSpace(secret)) _secret = secret!.Trim();
    }

    /// <summary>배포 산출물 스캔 → 지문/루트 해시/서명 매니페스트 생성.</summary>
    /// <param name="baseDirectory">대상 디렉터리(null 이면 <see cref="AppContext.BaseDirectory"/>).</param>
    public async Task<ReleaseManifest> BuildAsync(string? baseDirectory = null, CancellationToken ct = default)
    {
        await _buildGate.WaitAsync(ct).ConfigureAwait(false);
        var startTicks = DateTime.UtcNow.Ticks;
        try
        {
            IsBuilding = true;
            HashedFiles = 0;
            LastError = null;
            Notify();

            var root = string.IsNullOrWhiteSpace(baseDirectory) ? AppContext.BaseDirectory : baseDirectory!;
            var entries = new List<ReleaseFileEntry>(64);
            long totalBytes = 0;

            foreach (var path in CollectFiles(root))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(path);
                CurrentFile = name;
                try
                {
                    var info = new FileInfo(path);
                    var hash = await HashFileAsync(path, ct).ConfigureAwait(false);
                    entries.Add(new ReleaseFileEntry(RelativeOf(root, path), info.Length, hash));
                    totalBytes += info.Length;
                    HashedFiles = entries.Count;
                    Notify();
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    LastError = $"{name}: {ex.Message}";   // 잠긴 파일/권한 오류는 건너뛰고 계속(제5헌장)
                }
            }

            entries.Sort(static (a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));

            var canonical = BuildCanonical(entries.Count, totalBytes, entries);
            var rootHash = Sha256Hex(canonical);
            var signature = _secret is null ? string.Empty : HmacHex(canonical, _secret);
            var durationMs = (DateTime.UtcNow.Ticks - startTicks) / (double)TimeSpan.TicksPerMillisecond;

            var manifest = new ReleaseManifest(
                ProductName, ProductVersion, DateTimeOffset.UtcNow,
                RuntimeInformation.FrameworkDescription,
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                entries.Count, totalBytes, rootHash, signature, durationMs, entries, License);

            BuildCount++;
            RootHash = rootHash;
            LastBuiltAt = manifest.BuiltAt;
            LastManifest = manifest;
            CurrentFile = null;
            return manifest;
        }
        finally
        {
            IsBuilding = false;
            _buildGate.Release();
            Notify();
        }
    }

    /// <summary>매니페스트 무결성 재검증(루트 해시/서명 상수시간 대조 · 파일 재해시는 하지 않음).</summary>
    public bool VerifyManifest(ReleaseManifest? manifest)
    {
        if (manifest is null) return false;
        var canonical = BuildCanonical(manifest.FileCount, manifest.TotalBytes, manifest.Files);
        if (!FixedEquals(Sha256Hex(canonical), manifest.RootHashHex)) return false;
        if (manifest.SignatureHex.Length == 0) return _secret is null;
        return _secret is not null && FixedEquals(HmacHex(canonical, _secret), manifest.SignatureHex);
    }

    /// <summary>Air-Gapped 자립형 라이선스 발급(HMAC 토큰 · 비밀키 미구성 시 SHA-256 지문 토큰).</summary>
    public OfflineLicense IssueLicense(string licensee, string edition = "Enterprise", int seats = 1, int validDays = 365)
    {
        var issued = DateTimeOffset.UtcNow;
        var expires = issued.AddDays(validDays <= 0 ? 1 : validDays);
        var payload = LicensePayload(licensee, edition, seats, issued, expires);
        var token = _secret is null ? Sha256Hex(payload) : HmacHex(payload, _secret);
        var lic = new OfflineLicense(licensee, edition, seats <= 0 ? 1 : seats, issued, expires, token);
        License = lic;
        Notify();
        return lic;
    }

    /// <summary>라이선스 자체검증(토큰 상수시간 대조 + 만료 확인 · 외부 서버 조회 0).</summary>
    public bool ValidateLicense(OfflineLicense? license)
    {
        if (license is null || license.IsExpired) return false;
        var payload = LicensePayload(license.Licensee, license.Edition, license.Seats, license.IssuedAt, license.ExpiresAt);
        var expected = _secret is null ? Sha256Hex(payload) : HmacHex(payload, _secret);
        return FixedEquals(expected, license.TokenHex);
    }

    /// <summary>매니페스트 JSON 반출(폐쇄망 이관용). 실패 시 false + <see cref="LastError"/>.</summary>
    public async Task<bool> ExportAsync(string filePath, CancellationToken ct = default)
    {
        var manifest = LastManifest;
        if (manifest is null) { LastError = "매니페스트 미생성 — 먼저 BuildAsync 실행"; Notify(); return false; }
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(filePath, manifest.ToJson(), Encoding.UTF8, ct).ConfigureAwait(false);
            LastExportPath = filePath;
            LastError = null;
            Notify();
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Notify();
            return false;
        }
    }

    // ── 내부 유틸 ──

    private static IEnumerable<string> CollectFiles(string root)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) yield break;

        foreach (var pattern in ManifestPatterns)
        {
            string[] found;
            try { found = Directory.GetFiles(root, pattern, SearchOption.TopDirectoryOnly); }
            catch (Exception) { continue; }

            foreach (var f in found)
            {
                if (seen.Count >= MaxFiles) yield break;
                if (seen.Add(f)) yield return f;
            }
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, buffer.Length, useAsync: true);
        int read;
        while ((read = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            hasher.AppendData(buffer, 0, read);

        return Convert.ToHexString(hasher.GetHashAndReset());   // async 메서드 내 stackalloc 불가 → 32B 힙 1회(핫패스 아님)
    }

    private static string RelativeOf(string root, string path)
    {
        try { return Path.GetRelativePath(root, path).Replace('\\', '/'); }
        catch (Exception) { return Path.GetFileName(path); }
    }

    // 파일 지문뿐 아니라 개수/총량까지 봉인해야 "목록은 그대로 두고 집계만 조작"한 위조 매니페스트가 검출된다.
    private static string BuildCanonical(int fileCount, long totalBytes, IReadOnlyList<ReleaseFileEntry> files)
    {
        var sb = new StringBuilder(files.Count * 96 + 64);
        sb.Append(ProductName).Append('|').Append(ProductVersion).Append('|')
          .Append(fileCount.ToString(CultureInfo.InvariantCulture)).Append('|')
          .Append(totalBytes.ToString(CultureInfo.InvariantCulture));
        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            sb.Append('\n').Append(f.RelativePath).Append('|')
              .Append(f.SizeBytes.ToString(CultureInfo.InvariantCulture)).Append('|').Append(f.Sha256Hex);
        }
        return sb.ToString();
    }

    private static string LicensePayload(string licensee, string edition, int seats, DateTimeOffset issued, DateTimeOffset expires)
        => string.Concat(
            ProductName, "|", ProductVersion, "|", licensee, "|", edition, "|",
            seats.ToString(CultureInfo.InvariantCulture), "|",
            issued.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), "|",
            expires.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

    private static string Sha256Hex(string payload)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(payload), hash);
        return Convert.ToHexString(hash);
    }

    private static string HmacHex(string payload, string secret)
    {
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload), mac);
        return Convert.ToHexString(mac);
    }

    private static bool FixedEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
    }
}
