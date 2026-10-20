using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace PromptFlowStudio.Services.Media;

/// <summary>
/// [PromptFlowStudio] 로컬 미디어 자산화 서비스 — 원격 영상(Wan video_url · 24h 시한부)을 디스크로 스트리밍 다운로드해 영구 보관한다.
///   ▸ Zero-RAM: <see cref="HttpCompletionOption.ResponseHeadersRead"/> + 고정버퍼 펌프(ArrayPool)로 스트림→디스크 직결(파일 전체를 byte[] 로 안 올림).
///   ▸ Graceful Fallback: 네트워크/IO 예외가 전체 작업 실패로 번지지 않게 예외를 흡수하고 원격 URL 을 폴백으로 반환 + 셸 로그 기록.
///   ▸ 재생 경로 변환: BlazorWebView(https 가상 오리진)에서 로컬파일 재생이 막히므로, 가상호스트 URL(디스크 직접 스트리밍)로 변환해 제공.
///   BCL HttpClient + System.IO 만 사용(외부 NuGet 0). 싱글턴(단일 사용자 데스크톱).
/// </summary>
public sealed class LocalMediaService(IConfiguration cfg, IVideoMergeService merge) : ILocalMediaService
{
    /// <summary>
    /// 미디어 전용 HttpClient. ⚠ 대용량 다운로드가 전체 타임아웃에 걸려 잘리지 않도록 무한 타임아웃 —
    ///   실제 취소/시간제한은 <see cref="DownloadAsync"/> 의 <see cref="CancellationToken"/> 이 담당한다.
    /// </summary>
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>디스크→디스크 펌프 버퍼 크기(80KB). CopyToAsync 기본값과 동일 계열.</summary>
    private const int BufferSize = 81920;

    /// <inheritdoc/>
    public string MediaRoot { get; } =
        string.IsNullOrWhiteSpace(cfg["Media:Root"])
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", "Media")
            : cfg["Media:Root"]!;

    /// <inheritdoc/>
    public string VirtualHost { get; } =
        string.IsNullOrWhiteSpace(cfg["Media:VirtualHost"]) ? "media.promptflow.local" : cfg["Media:VirtualHost"]!;

    /// <inheritdoc/>
    public async Task<LocalMediaResult> DownloadAsync(
        string jobId, string remoteUrl, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
            return new(false, null, remoteUrl ?? "", remoteUrl ?? "", 0, "원격 URL 이 비어 있습니다.");

        var dir       = Path.Combine(MediaRoot, SafeSegment(jobId));
        var finalPath = Path.Combine(dir, FileNameFromUrl(remoteUrl));
        var tempPath  = finalPath + ".part";   // 완료된 파일만 최종 이름을 갖도록 부분파일에 먼저 쓴다.

        // ── 로컬 엔진 출력(로컬 파일 경로) — HTTP 대신 미디어 루트로 복사 ──
        if (IsLocalFile(remoteUrl))
        {
            try
            {
                Directory.CreateDirectory(dir);
                var localFinal = Path.Combine(dir, Path.GetFileName(remoteUrl));
                File.Copy(remoteUrl, localFinal, overwrite: true);
                var lb = new FileInfo(localFinal).Length;
                try { await merge.ExtractPosterAsync(localFinal, ct).ConfigureAwait(false); } catch { /* 썸네일 실패 무시 */ }
                progress?.Report(100);
                return new(true, localFinal, ToPlaybackUrl(localFinal), remoteUrl, lb, "로컬 자산화 완료");
            }
            catch (Exception ex)
            {
                Program.Log("LocalMediaService.LocalCopy", ex);
                return new(false, null, remoteUrl, remoteUrl, 0, "로컬 복사 실패: " + ex.Message);
            }
        }

        try
        {
            Directory.CreateDirectory(dir);

            // 헤더만 먼저 받는다(본문은 아직 스트림). 여기서 응답 스트림을 열어 디스크로 직접 흘린다.
            using var resp = await Http
                .GetAsync(remoteUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength;   // 없을 수 있음(청크 전송)

            // ⚠ Zero-RAM 핵심: 응답 본문을 통째로 읽지 않고 네트워크 스트림 → 파일 스트림으로 펌프.
            await using (var net  = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write,
                             FileShare.None, BufferSize, FileOptions.Asynchronous))
            {
                await PumpAsync(net, file, total, progress, ct).ConfigureAwait(false);
                await file.FlushAsync(ct).ConfigureAwait(false);
            }

            // 부분파일 → 최종파일 교체(반쯤 받다 죽은 파일이 완성본처럼 보이는 것을 방지).
            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(tempPath, finalPath);

            var bytes = new FileInfo(finalPath).Length;
            try { await merge.ExtractPosterAsync(finalPath, ct).ConfigureAwait(false); } catch { /* 썸네일 실패는 무시(영상은 정상) */ }
            progress?.Report(100);
            return new(true, finalPath, ToPlaybackUrl(finalPath), remoteUrl, bytes, "다운로드 완료");
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
            // 취소는 오류가 아니다 — 원격 URL 은 아직 유효(24h)하므로 폴백으로 유지.
            return new(false, null, remoteUrl, remoteUrl, 0, "다운로드가 취소되었습니다.");
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            // ⚠ Graceful Fallback: 다운로드 실패가 작업 전체를 무너뜨리지 않게 한다.
            //   원격 URL(24h)을 재생 폴백으로 반환하고 원인은 셸 로그에 남긴다.
            Program.Log("LocalMediaService.DownloadAsync", ex);
            return new(false, null, remoteUrl, remoteUrl, 0, "로컬 다운로드 실패(원격 URL 유지): " + ex.Message);
        }
    }

    /// <summary>
    /// 고정 버퍼로 <paramref name="src"/>→<paramref name="dst"/> 를 흘리며 진행률을 보고한다(스트리밍 CopyToAsync 의 진행률 계측판).
    ///   버퍼는 <see cref="ArrayPool{T}"/> 에서 대여해 GC 압력 0. 진행률은 정수 % 변화 시에만 보고(과도한 이벤트 방지).
    /// </summary>
    private static async Task PumpAsync(Stream src, Stream dst, long? total, IProgress<double>? progress, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long done = 0;
            int lastPct = -1, n;
            while ((n = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;

                if (progress is not null && total is > 0)
                {
                    var pct = (int)(done * 100L / total.Value);
                    if (pct != lastPct) { lastPct = pct; progress.Report(pct); }
                }
            }
            // 길이 미상(Content-Length 없음)이면 완료 시점에 100 만 보고.
            if (progress is not null && total is not > 0) progress.Report(100);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <inheritdoc/>
    public string ToPlaybackUrl(string localPath)
    {
        var full = Path.GetFullPath(localPath);
        var root = Path.GetFullPath(MediaRoot);
        var rel  = Path.GetRelativePath(root, full);

        // 미디어 루트 밖의 파일("../…" 또는 다른 드라이브)은 가상호스트로 서빙되지 않으므로 file:// 로 폴백.
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
            return ToFileUrl(full);

        // 경로 세그먼트별 URL 인코딩(공백·한글·특수문자 안전). 슬래시는 유지.
        var encoded = string.Join('/', rel.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
        return $"https://{VirtualHost}/{encoded}";
    }

    /// <inheritdoc/>
    public string ToFileUrl(string localPath) => new Uri(Path.GetFullPath(localPath)).AbsoluteUri;

    // ── 유틸 ────────────────────────────────────────────────────────────

    /// <summary>원격 URL 이 아니라 실제 존재하는 로컬 파일 경로인지(로컬 비디오 엔진 출력 판별).</summary>
    private static bool IsLocalFile(string s)
    {
        try
        {
            if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return false;
            return File.Exists(s);
        }
        catch { return false; }
    }

    /// <summary>URL 경로에서 파일명 추출(쿼리 제거). 확장자 없으면 .mp4, 금지문자는 _ 로 치환.</summary>
    private static string FileNameFromUrl(string url)
    {
        var name = "video.mp4";
        try
        {
            var seg = Path.GetFileName(new Uri(url).AbsolutePath);
            if (!string.IsNullOrWhiteSpace(seg)) name = seg;
        }
        catch { /* URL 파싱 실패 → 기본명 */ }

        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        if (!Path.HasExtension(name)) name += ".mp4";
        return name;
    }

    /// <summary>폴더/파일 이름으로 안전한 세그먼트로 정규화(금지문자 치환).</summary>
    private static string SafeSegment(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "job";
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim();
    }

    /// <summary>부분파일 정리(실패해도 무시).</summary>
    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 정리 실패는 삼킨다 */ }
    }
}
