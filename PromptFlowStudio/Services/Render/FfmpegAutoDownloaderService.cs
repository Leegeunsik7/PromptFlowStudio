using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Render;

/// <summary>FFmpeg 정적 빌드 미러 — ZIP URL + (선택) SHA-256 체크섬 URL.</summary>
public sealed record FfmpegMirror(string Label, string ZipUrl, string? Sha256Url);

/// <summary>원클릭 설치 결과.</summary>
public sealed record FfmpegInstallResult(bool Ok, string? FfmpegPath, string Message, bool ChecksumVerified = false);

/// <summary>
/// [PromptFlowStudio] FFmpeg 원클릭 자동 설치 — 순수 BCL(HttpClient + System.IO.Compression · 외부 NuGet 0).
///   ▸ 미러 순차 페일오버(Gyan.dev essentials → GitHub BtbN) · 미러당 2회 지수 백오프(1s→2s) · 전체 10분 타임아웃.
///   ▸ 체크섬 URL 제공 시 SHA-256 검증(<see cref="CryptographicOperations.FixedTimeEquals"/>) — 불일치는 해당 미러 폐기.
///   ▸ ZIP 내 임의 폴더의 <c>ffmpeg.exe</c>/<c>ffprobe.exe</c> 만 파일명 기준 추출(Zip-Slip 차단) → <c>{BaseDirectory}\tools\ffmpeg\bin\</c> 임시파일→원자 교체.
///   ▸ 추출물 PE(MZ) 헤더 즉시 검증. 진행률 <see cref="IProgress{T}"/>: 다운로드 0~0.9 · 추출 0.9~1.0.
///   ▸ Windows x64 전용(<see cref="IsSupported"/>) — 그 외 플랫폼은 즉시 실패 반환.
/// </summary>
public sealed class FfmpegAutoDownloaderService
{
    public const string FfmpegExe = "ffmpeg.exe";
    public const string FfprobeExe = "ffprobe.exe";
    private const int AttemptsPerMirror = 2;
    private const int BufferSize = 81920;
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromMinutes(10);

    public static readonly IReadOnlyList<FfmpegMirror> DefaultMirrors = new[]
    {
        new FfmpegMirror("Gyan.dev (release essentials)", "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
            "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip.sha256"),
        new FfmpegMirror("GitHub BtbN (win64 gpl)", "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip",
            "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/checksums.sha256"),
    };

    private static readonly Lazy<HttpClient> SharedHttp = new(static () =>
    {
        var h = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };   // 요청별 CTS 로 제어
        h.DefaultRequestHeaders.UserAgent.ParseAdd("PromptFlowStudio-FfmpegBootstrap/1.0");
        return h;
    });

    private readonly HttpClient? _http;
    private readonly IReadOnlyList<FfmpegMirror> _mirrors;
    private readonly Func<int, TimeSpan> _backoff;

    public FfmpegAutoDownloaderService() : this(null, null, null, null) { }

    /// <param name="http">테스트 주입(null = 공유 클라이언트).</param>
    /// <param name="mirrors">null = <see cref="DefaultMirrors"/>.</param>
    /// <param name="baseDir">설치 기준 폴더(null = <see cref="AppContext.BaseDirectory"/>).</param>
    /// <param name="backoff">재시도 대기(시도 번호 0부터) — null = 1s·2s 지수 백오프.</param>
    public FfmpegAutoDownloaderService(HttpClient? http, IReadOnlyList<FfmpegMirror>? mirrors, string? baseDir, Func<int, TimeSpan>? backoff)
    {
        _http = http;
        _mirrors = mirrors is { Count: > 0 } ? mirrors : DefaultMirrors;
        BaseDirectory = string.IsNullOrWhiteSpace(baseDir) ? AppContext.BaseDirectory : baseDir!;
        _backoff = backoff ?? (static n => TimeSpan.FromSeconds(Math.Pow(2, n)));
    }

    public string BaseDirectory { get; }

    /// <summary>다운로드 작업 폴더 루트(<c>pfs_ffdl_{guid}</c> 생성 위치) — null/빈값 = <see cref="Path.GetTempPath"/>. 테스트 격리용.</summary>
    public string? WorkRoot { get; init; }

    /// <summary>다운로드 중 임시 파일 확장자 — 완료 시에만 최종 이름으로 원자 이동, 실패/취소/타임아웃 시 즉시 삭제.</summary>
    public const string TempSuffix = ".tmp";

    /// <summary><see cref="InstallAsync"/> 별칭 — 다운로드(진행률 실시간 보고) → 추출. 취소 시 <see cref="OperationCanceledException"/> 전파 + 임시 파일 롤백.</summary>
    public Task<FfmpegInstallResult> DownloadAndExtractAsync(IProgress<float>? progress = null, CancellationToken ct = default, bool requireSupportedPlatform = true)
        => InstallAsync(progress, ct, requireSupportedPlatform);

    /// <summary>설치 대상 — <c>{BaseDirectory}\tools\ffmpeg\bin</c>(<see cref="FfmpegLocator"/> 1순위 후보).</summary>
    public string TargetBinDirectory => TargetBinOf(BaseDirectory);

    public static string TargetBinOf(string baseDir) => Path.Combine(baseDir, "tools", "ffmpeg", "bin");

    /// <summary>공식 정적 빌드는 Windows 64-bit 전용.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows() && Environment.Is64BitOperatingSystem;

    /// <summary>다운로드 → (체크섬) → 추출 → 검증. 실패는 예외 대신 결과로 반환(취소만 전파).</summary>
    public async Task<FfmpegInstallResult> InstallAsync(IProgress<float>? progress = null, CancellationToken ct = default, bool requireSupportedPlatform = true)
    {
        if (requireSupportedPlatform && !IsSupported) return new(false, null, "FFmpeg 자동 설치는 Windows 64-bit 에서만 지원됩니다.");
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overall.CancelAfter(OverallTimeout);
        var work = Path.Combine(string.IsNullOrWhiteSpace(WorkRoot) ? Path.GetTempPath() : WorkRoot!, "pfs_ffdl_" + Guid.NewGuid().ToString("N"));
        string last = "다운로드 미러가 없습니다.";
        try
        {
            Directory.CreateDirectory(work);
            for (var m = 0; m < _mirrors.Count; m++)
            {
                var mirror = _mirrors[m];
                for (var attempt = 0; attempt < AttemptsPerMirror; attempt++)
                {
                    var zip = Path.Combine(work, "ffmpeg_" + m.ToString(CultureInfo.InvariantCulture) + ".zip");
                    try
                    {
                        if (attempt > 0) await Task.Delay(_backoff(attempt - 1), overall.Token).ConfigureAwait(false);
                        progress?.Report(0f);
                        await DownloadAsync(mirror.ZipUrl, zip, progress, overall.Token).ConfigureAwait(false);
                        var verified = false;
                        if (mirror.Sha256Url is { Length: > 0 } shaUrl)
                        {
                            var expected = await TryFetchChecksumAsync(shaUrl, FileNameOfUrl(mirror.ZipUrl), overall.Token).ConfigureAwait(false);
                            if (expected is not null)
                            {
                                if (!VerifySha256(zip, expected)) { last = mirror.Label + ": SHA-256 체크섬 불일치"; break; }   // 무결성 실패 — 재시도 없이 다음 미러
                                verified = true;
                            }
                        }
                        progress?.Report(0.9f);
                        var exe = ExtractBinaries(zip, BaseDirectory);
                        progress?.Report(1f);
                        return new(true, exe, "FFmpeg 설치 완료 — " + mirror.Label + (verified ? " · SHA-256 검증됨" : " · 체크섬 미확인"), verified);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested && overall.IsCancellationRequested)
                    {
                        return new(false, null, "FFmpeg 다운로드 시간 초과(10분)");
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
                    {
                        last = mirror.Label + ": " + ex.Message;
                        Program.Log("FfmpegAutoDownloader", ex);
                    }
                    finally
                    {
                        TryDelete(zip);                // 불완전/검증 실패 압축 파일 롤백
                        TryDelete(zip + TempSuffix);   // 다운로드 중이던 .tmp 롤백
                    }
                }
            }
            return new(false, null, "FFmpeg 자동 설치 실패 — " + last);
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task DownloadAsync(string url, string dest, IProgress<float>? progress, CancellationToken ct)
    {
        var http = _http ?? SharedHttp.Value;
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1L;
        var tmp = dest + TempSuffix;
        var ok = false;
        try
        {
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                var buf = new byte[BufferSize];
                long read = 0;
                var lastPct = -1;
                int n;
                while ((n = await src.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    read += n;
                    if (total > 0 && progress is not null)
                    {
                        var pct = (int)(Math.Min(read, total) * 100 / total);   // 수신 바이트 / 전체 바이트
                        if (pct != lastPct) { lastPct = pct; progress.Report(0.9f * pct / 100f); }   // 1% 단위로만 보고
                    }
                }
                if (read == 0) throw new InvalidDataException("빈 응답");
                if (total > 0 && read < total) throw new IOException("다운로드가 중간에 끊겼습니다 (" + read.ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture) + " bytes)");
            }
            File.Move(tmp, dest, overwrite: true);   // 완료분만 최종 이름으로 원자 이동
            ok = true;
        }
        finally
        {
            if (!ok) TryDelete(tmp);   // 실패·취소·타임아웃 — 불완전 .tmp 즉시 롤백
        }
    }

    private async Task<string?> TryFetchChecksumAsync(string url, string fileName, CancellationToken ct)
    {
        try
        {
            var http = _http ?? SharedHttp.Value;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            var text = await http.GetStringAsync(url, cts.Token).ConfigureAwait(false);
            return ParseSha256(text, fileName);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (HttpRequestException ex) { Program.Log("FfmpegAutoDownloader.Checksum", ex); return null; }
    }

    /// <summary>체크섬 텍스트 파싱 — 단일 해시(Gyan) 또는 <c>hash  filename</c> 목록(BtbN). 64자 hex 소문자 반환, 없으면 null.</summary>
    public static string? ParseSha256(string? text, string? fileName)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string? firstHash = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 64) continue;
            var sp = line.IndexOfAny(new[] { ' ', '\t' });
            var hash = sp < 0 ? line : line[..sp];
            if (!IsHex64(hash)) continue;
            if (sp < 0) { firstHash ??= hash; continue; }
            var name = line[sp..].Trim().TrimStart('*');
            if (!string.IsNullOrEmpty(fileName) && string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)) return hash.ToLowerInvariant();
        }
        return firstHash?.ToLowerInvariant();
    }

    private static bool IsHex64(string s)
    {
        if (s.Length != 64) return false;
        for (var i = 0; i < s.Length; i++) if (!Uri.IsHexDigit(s[i])) return false;
        return true;
    }

    /// <summary>파일 SHA-256 상수시간 비교.</summary>
    public static bool VerifySha256(string path, string expectedHex)
    {
        if (!IsHex64(expectedHex)) return false;
        byte[] actual;
        using (var fs = File.OpenRead(path)) actual = SHA256.HashData(fs);
        return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expectedHex));
    }

    private static string FileNameOfUrl(string url)
    {
        try { return Path.GetFileName(new Uri(url).AbsolutePath); }
        catch (UriFormatException) { return ""; }
    }

    /// <summary>
    /// ZIP 에서 <c>ffmpeg.exe</c>·<c>ffprobe.exe</c> 추출 → <c>{baseDir}\tools\ffmpeg\bin\</c>. 동명 다수면 <c>/bin/</c> 경로 우선.
    /// 파일명만 사용(Zip-Slip 차단) · 임시파일→원자 교체 · MZ 헤더 검증. ffmpeg.exe 누락/손상 시 <see cref="InvalidDataException"/>.
    /// </summary>
    /// <returns>설치된 ffmpeg.exe 절대 경로.</returns>
    public static string ExtractBinaries(string zipPath, string baseDir)
    {
        var bin = TargetBinOf(baseDir);
        using var zip = ZipFile.OpenRead(zipPath);
        var ffmpeg = PickEntry(zip, FfmpegExe) ?? throw new InvalidDataException("ZIP 에 ffmpeg.exe 가 없습니다.");
        var ffprobe = PickEntry(zip, FfprobeExe);
        Directory.CreateDirectory(bin);
        var exe = ExtractOne(ffmpeg, Path.Combine(bin, FfmpegExe));
        if (ffprobe is not null) ExtractOne(ffprobe, Path.Combine(bin, FfprobeExe));
        return exe;
    }

    private static ZipArchiveEntry? PickEntry(ZipArchive zip, string name)
    {
        ZipArchiveEntry? any = null;
        foreach (var e in zip.Entries)
        {
            if (!string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase) || e.Length == 0) continue;
            var full = e.FullName.Replace('\\', '/');
            if (full.Contains("/bin/", StringComparison.OrdinalIgnoreCase) || full.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)) return e;
            any ??= e;
        }
        return any;
    }

    private static string ExtractOne(ZipArchiveEntry entry, string dest)
    {
        var tmp = dest + ".part";
        try
        {
            entry.ExtractToFile(tmp, overwrite: true);
            if (!HasPeHeader(tmp)) throw new InvalidDataException(entry.Name + " 실행 파일 헤더(MZ) 검증 실패");
            File.Move(tmp, dest, overwrite: true);
            return Path.GetFullPath(dest);
        }
        finally { TryDelete(tmp); }   // 추출 실패(손상 ZIP·헤더 불일치) 시 불완전 .part 롤백 — 성공 시엔 이미 이동되어 no-op
    }

    private static bool HasPeHeader(string path)
    {
        Span<byte> h = stackalloc byte[2];
        using var fs = File.OpenRead(path);
        return fs.Read(h) == 2 && h[0] == (byte)'M' && h[1] == (byte)'Z';
    }
}
