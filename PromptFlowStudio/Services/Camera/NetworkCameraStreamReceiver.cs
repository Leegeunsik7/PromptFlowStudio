using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;

namespace PromptFlowStudio.Services.Camera;

/// <summary>
/// [PromptFlowStudio] 실물 네트워크 카메라 수신기.
///   ▸ http(s): 스냅샷(/shot.jpg 등 단일 이미지) 또는 MJPEG(multipart/x-mixed-replace) 첫 JPEG — HttpClient 타임아웃 3초.
///   ▸ rtsp(s)/rtmp/srt: 설치된 ffmpeg CLI 로 1프레임 MJPEG 파이프 캡처(미설치/미지원 플랫폼은 폴백).
///   ▸ 응답 없음·타임아웃·비이미지 응답·WebRTC → 즉시 <see cref="MockStreamReceiver"/> 폴백(연결은 성공 처리, UI 크래시 0).
///   ▸ 연결 세대(_gen)로 진행 중 연결과 해제 경합 차단 — 해제/재연결 후 늦게 끝난 연결은 커밋하지 않음.
///   ▸ 수신 버퍼는 <see cref="ArrayPool{T}.Shared"/> 대여·반납(LOH 파편화 방지) — 최종 프레임 1장만 정확한 크기로 할당.
/// </summary>
public sealed class NetworkCameraStreamReceiver : ICameraStreamReceiver, IDisposable
{
    public static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan FfmpegTimeout = TimeSpan.FromSeconds(5);
    public const int MaxFrameBytes = 16 * 1024 * 1024;

    private readonly HttpClient _http = new() { Timeout = HttpTimeout };
    private readonly MockStreamReceiver _mock = new();
    private StreamTelemetry _telemetry = StreamTelemetry.Empty;
    private Uri? _uri;
    private long _gen;
    private volatile bool _streaming, _fallback;
    private volatile string? _fallbackReason;

    public bool IsStreaming => _streaming;
    public bool IsFallback => _streaming && _fallback;
    public string? FallbackReason => IsFallback ? _fallbackReason : null;

    public async Task<bool> ConnectAsync(string endpoint, CancellationToken ct)
    {
        var gen = Interlocked.Increment(ref _gen);
        ResetState();
        if (!WirelessCameraService.TryValidateEndpoint(endpoint, out _)) return false;
        var uri = new Uri(endpoint.Trim());

        string? reason = null;
        var sw = Stopwatch.StartNew();
        var frame = await GrabAsync(uri, ct, r => reason = r).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (Interlocked.Read(ref _gen) != gen) return false;   // 해제/재연결로 대체됨

        _uri = uri;
        if (frame is not null) UpdateLiveTelemetry(frame, sw.Elapsed);
        else await EnterFallbackAsync(reason ?? "카메라 응답 없음", ct).ConfigureAwait(false);
        _streaming = true;
        return true;
    }

    public void Disconnect()
    {
        Interlocked.Increment(ref _gen);
        ResetState();
    }

    public async Task<byte[]?> CaptureFrameBytesAsync(CancellationToken ct)
    {
        var uri = _uri;
        if (!_streaming || uri is null) return null;
        if (_fallback) return await _mock.CaptureFrameBytesAsync(ct).ConfigureAwait(false);

        var gen = Interlocked.Read(ref _gen);
        string? reason = null;
        var sw = Stopwatch.StartNew();
        var frame = await GrabAsync(uri, ct, r => reason = r).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (Interlocked.Read(ref _gen) != gen) return null;
        if (frame is not null)
        {
            UpdateLiveTelemetry(frame, sw.Elapsed);
            return frame;
        }
        await EnterFallbackAsync(reason ?? "프레임 수신 실패", ct).ConfigureAwait(false);
        return await _mock.CaptureFrameBytesAsync(ct).ConfigureAwait(false);
    }

    public StreamTelemetry GetCurrentTelemetry() =>
        !_streaming ? StreamTelemetry.Empty : _fallback ? _mock.GetCurrentTelemetry() : _telemetry;

    public void Dispose()
    {
        Disconnect();
        _http.Dispose();
    }

    private void ResetState()
    {
        _streaming = false;
        _fallback = false;
        _fallbackReason = null;
        _uri = null;
        _telemetry = StreamTelemetry.Empty;
        _mock.Disconnect();
    }

    private async Task EnterFallbackAsync(string reason, CancellationToken ct)
    {
        _fallbackReason = reason;
        _fallback = true;
        if (!_mock.IsStreaming && _uri is not null) await _mock.ConnectAsync(_uri.OriginalString, ct).ConfigureAwait(false);
    }

    /// <summary>프레임 크기·왕복 시간 기반 실측 지표(스냅샷은 1/왕복 을 30fps 상한으로).</summary>
    private void UpdateLiveTelemetry(byte[] frame, TimeSpan elapsed)
    {
        var ms = Math.Max(1.0, elapsed.TotalMilliseconds);
        var res = CameraFrameCodec.TryReadSize(frame, out var w, out var h)
            ? w.ToString(System.Globalization.CultureInfo.InvariantCulture) + "x" + h.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : WirelessCameraService.DefaultResolution;
        _telemetry = new StreamTelemetry(Math.Min(WirelessCameraService.NominalFps, 1000.0 / ms), res,
            (int)ms, (int)Math.Min(int.MaxValue, frame.Length * 8L / (long)ms));
    }

    /// <summary>스킴별 실물 1프레임 — 실패 사유는 콜백으로 전달하고 null. 호출자 취소만 예외로 전파.</summary>
    private async Task<byte[]?> GrabAsync(Uri uri, CancellationToken ct, Action<string> fail)
    {
        try
        {
            switch (uri.Scheme.ToLowerInvariant())
            {
                case "http":
                case "https":
                    var http = await FetchHttpFrameAsync(uri, ct).ConfigureAwait(false);
                    if (http is null) fail("HTTP 응답이 이미지가 아닙니다");
                    return http;
                case "rtsp":
                case "rtsps":
                case "rtmp":
                case "srt":
                    var ffmpeg = ResolveFfmpeg();
                    if (ffmpeg is null) { fail("ffmpeg 미설치 — RTSP 실물 캡처 불가"); return null; }
                    var piped = await FfmpegGrabAsync(ffmpeg, uri, ct).ConfigureAwait(false);
                    if (piped is null) fail("ffmpeg 프레임 캡처 실패/시간 초과");
                    return piped;
                default:
                    fail("WebRTC 시그널링 미지원");
                    return null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { fail("네트워크 타임아웃"); return null; }
        catch (Exception ex)
        {
            fail("연결 실패: " + ex.GetType().Name);
            return null;
        }
    }

    private async Task<byte[]?> FetchHttpFrameAsync(Uri uri, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(HttpTimeout);
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        if (resp.Content.Headers.ContentLength > MaxFrameBytes) return null;
        var media = resp.Content.Headers.ContentType?.MediaType ?? "";
        var s = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        await using (s.ConfigureAwait(false))
        {
            var bytes = media.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase)
                ? await ReadFirstJpegAsync(s, cts.Token).ConfigureAwait(false)
                : await ReadAllCappedAsync(s, cts.Token).ConfigureAwait(false);
            return bytes is not null && CameraFrameCodec.TryReadSize(bytes, out _, out _) ? bytes : null;
        }
    }

    private static async Task<byte[]?> FfmpegGrabAsync(string ffmpeg, Uri uri, CancellationToken ct)
    {
        if (!ProcessCompat.IsSupported) return null;
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-hide_banner"); psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        if (uri.Scheme.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase)) { psi.ArgumentList.Add("-rtsp_transport"); psi.ArgumentList.Add("tcp"); }
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(uri.OriginalString);
        psi.ArgumentList.Add("-frames:v"); psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("image2pipe");
        psi.ArgumentList.Add("-vcodec"); psi.ArgumentList.Add("mjpeg");
        psi.ArgumentList.Add("pipe:1");

        using var p = Process.Start(psi);
        if (p is null) return null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(FfmpegTimeout);
        try
        {
            var drainErr = p.StandardError.ReadToEndAsync(cts.Token);   // stderr 파이프 막힘 방지
            var bytes = await ReadAllCappedAsync(p.StandardOutput.BaseStream, cts.Token).ConfigureAwait(false);
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            await drainErr.ConfigureAwait(false);
            return p.ExitCode == 0 && bytes is not null && CameraFrameCodec.TryReadSize(bytes, out _, out _) ? bytes : null;
        }
        finally
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }

    /// <summary>ffmpeg 위치 — exe 옆(배포 관례) 우선 → CliPathResolver(번들/로컬앱데이터/PATH). 못 찾으면 null.</summary>
    private static string? ResolveFfmpeg()
    {
        try
        {
            var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(local)) return local;
            return CliPathResolver.GetExecutablePath("ffmpeg", "ffmpeg.exe");
        }
        catch { return null; }
    }

    internal const int PoolChunk = 64 * 1024;

    internal static async Task<byte[]?> ReadAllCappedAsync(Stream s, CancellationToken ct)
    {
        var buf = ArrayPool<byte>.Shared.Rent(PoolChunk);
        var len = 0;
        try
        {
            int n;
            while ((n = await s.ReadAsync(buf.AsMemory(len), ct).ConfigureAwait(false)) > 0)
            {
                len += n;
                if (len > MaxFrameBytes) return null;
                if (len == buf.Length) buf = Grow(buf, len);
            }
            return len == 0 ? null : buf.AsSpan(0, len).ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    /// <summary>MJPEG 멀티파트 스트림에서 첫 SOI(FFD8)~EOI(FFD9) 구간 추출.</summary>
    internal static async Task<byte[]?> ReadFirstJpegAsync(Stream s, CancellationToken ct)
    {
        var buf = ArrayPool<byte>.Shared.Rent(PoolChunk);
        int len = 0, start = -1, scanFrom = 1;
        try
        {
            int n;
            while ((n = await s.ReadAsync(buf.AsMemory(len), ct).ConfigureAwait(false)) > 0)
            {
                len += n;
                if (len > MaxFrameBytes) return null;
                for (var i = scanFrom; i < len; i++)
                {
                    if (buf[i - 1] != 0xFF) continue;
                    if (start < 0) { if (buf[i] == 0xD8) start = i - 1; }
                    else if (buf[i] == 0xD9) return buf.AsSpan(start, i + 1 - start).ToArray();
                }
                scanFrom = Math.Max(1, len);
                if (len == buf.Length) buf = Grow(buf, len);
            }
            return null;
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    /// <summary>대여 버퍼 2배 확장 — 새 버퍼 대여 → 유효 구간 복사 → 이전 버퍼 반납.</summary>
    private static byte[] Grow(byte[] buf, int len)
    {
        var next = ArrayPool<byte>.Shared.Rent(Math.Min(buf.Length * 2, MaxFrameBytes + 1));
        buf.AsSpan(0, len).CopyTo(next);
        ArrayPool<byte>.Shared.Return(buf);
        return next;
    }
}
