using System.Text;
using System.Buffers.Text;
using System.Net.Http;
using System.Security.Cryptography;
namespace PromptFlowStudio.Services.GraphBridge;

public enum GraphHttpMethod : byte { Get, Post, Put, Delete }

/// <summary>응답 수신 버퍼(재사용) — 본문 64 KB · Retry-After · X-App-Usage 원문.</summary>
public sealed class GraphResponseBuffer
{
    public const int BodyCap = 64 * 1024, UsageCap = 256, LocationCap = 1024;
    public readonly byte[] Body = new byte[BodyCap];
    public readonly byte[] AppUsage = new byte[UsageCap];
    /// <summary>v17.51 — Location(재개 업로드 세션 URL) · Range(308 Resume Incomplete 확정 범위) 응답 헤더.</summary>
    public readonly byte[] Location = new byte[LocationCap], Range = new byte[64];
    public int LocationLength, RangeLength;
    public ReadOnlySpan<byte> LocationSpan => Location.AsSpan(0, LocationLength);
    public ReadOnlySpan<byte> RangeSpan => Range.AsSpan(0, RangeLength);
    public int Length, AppUsageLength, RetryAfterSec;
    public bool Truncated;
    public ReadOnlySpan<byte> BodySpan => Body.AsSpan(0, Length);
    public ReadOnlySpan<byte> AppUsageSpan => AppUsage.AsSpan(0, AppUsageLength);
    public void Reset() { Length = 0; AppUsageLength = 0; RetryAfterSec = 0; Truncated = false; LocationLength = 0; RangeLength = 0; }
}

/// <summary>
/// Graph API 전송 계층 — 요청은 UTF-8 URL · 헤더 줄(<c>Name: value\r\n</c>) · 본문 메모리로 전달되고, 응답은 <see cref="GraphResponseBuffer"/> 에 기록된다.
/// 반환값 = HTTP 상태(0 = 네트워크 실패). 동기 완료 구현(시뮬레이터)은 무할당.
/// </summary>
public interface IGraphTransport
{
    ValueTask<int> SendAsync(GraphHttpMethod method, ReadOnlyMemory<byte> url, ReadOnlyMemory<byte> headers, ReadOnlyMemory<byte> body, GraphResponseBuffer resp, CancellationToken ct);
}

/// <summary>UTF-8 바이트 조립 헬퍼(무할당).</summary>
public static class GraphBytes
{
    public static void Put(Span<byte> d, ref int n, ReadOnlySpan<byte> s) { s.CopyTo(d[n..]); n += s.Length; }
    public static void Put(Span<byte> d, ref int n, long v) { Utf8Formatter.TryFormat(v, d[n..], out var w); n += w; }
    public static void Put(Span<byte> d, ref int n, ulong v) { Utf8Formatter.TryFormat(v, d[n..], out var w); n += w; }

    /// <summary>application/x-www-form-urlencoded 퍼센트 인코딩(RFC 3986 unreserved 외 전부).</summary>
    public static void PutEncoded(Span<byte> d, ref int n, ReadOnlySpan<byte> utf8)
    {
        for (var i = 0; i < utf8.Length; i++)
        {
            var c = utf8[i];
            if (c is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~') d[n++] = c;
            else { d[n++] = (byte)'%'; d[n++] = Hex(c >> 4); d[n++] = Hex(c & 15); }
        }
    }

    private static byte Hex(int v) => (byte)(v < 10 ? '0' + v : 'A' + v - 10);

    /// <summary>헤더 줄에서 값 탐색(대소문자 무시) — 없으면 빈 스팬.</summary>
    public static ReadOnlySpan<byte> Header(ReadOnlySpan<byte> headers, ReadOnlySpan<byte> name)
    {
        while (!headers.IsEmpty)
        {
            var eol = headers.IndexOf("\r\n"u8);
            var line = eol < 0 ? headers : headers[..eol];
            headers = eol < 0 ? default : headers[(eol + 2)..];
            var colon = line.IndexOf((byte)':');
            if (colon <= 0 || !Ascii.EqualsIgnoreCase(line[..colon], name)) continue;
            return line[(colon + 1)..].TrimStart((byte)' ');
        }
        return default;
    }

    public static bool Contains(ReadOnlySpan<byte> s, ReadOnlySpan<byte> v) => s.IndexOf(v) >= 0;
}

/// <summary>재사용 요청 슬롯 — URL/헤더 버퍼 · in-flight 1 슬롯 · 완료 후 헤더(토큰) 즉시 제로화.</summary>
public sealed class GraphCall
{
    public const int UrlCap = 2048, HdrCap = 2048;
    public readonly byte[] Url = new byte[UrlCap], Hdr = new byte[HdrCap];
    public readonly GraphResponseBuffer Resp = new();
    public int UrlLen, HdrLen;
    public GraphHttpMethod Method;
    public ReadOnlyMemory<byte> Body;
    public bool UrlHasSecret;
    private ValueTask<int> _pending;
    public bool InFlight { get; private set; }
    public long Sent { get; private set; }
    public long NetErrors { get; private set; }

    public void Send(IGraphTransport t, CancellationToken ct)
    {
        Resp.Reset();
        Sent++;
        InFlight = true;
        try { _pending = t.SendAsync(Method, Url.AsMemory(0, UrlLen), Hdr.AsMemory(0, HdrLen), Body, Resp, ct); }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or OperationCanceledException)
        {
            PromptFlowStudio.Program.Log("GraphBridge.Send", ex);
            _pending = new ValueTask<int>(0);
        }
    }

    /// <summary>완료 시 true · 상태 반환(예외 = 0). 비밀 포함 버퍼 제로화.</summary>
    public bool TryComplete(out int status)
    {
        status = 0;
        if (!InFlight || !_pending.IsCompleted) return false;
        try { status = _pending.Result; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException) { PromptFlowStudio.Program.Log("GraphBridge.Complete", ex); }
        if (status == 0) NetErrors++;
        _pending = default;
        InFlight = false;
        CryptographicOperations.ZeroMemory(Hdr.AsSpan(0, HdrLen));
        if (UrlHasSecret) CryptographicOperations.ZeroMemory(Url.AsSpan(0, UrlLen));
        HdrLen = 0; UrlHasSecret = false;
        return true;
    }
}

/// <summary>
/// 실 전송 — HttpClient(순수 BCL). 요청별 30초 타임아웃 · 응답 본문 64 KB 상한 · Retry-After/X-App-Usage 보존.
/// URL/헤더는 로그에 남기지 않는다(토큰 노출 0). 실 네트워크 경로는 HttpRequestMessage 생성상 할당이 불가피하다.
/// </summary>
public sealed class HttpGraphTransport : IGraphTransport, IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly HttpClient _http;

    public HttpGraphTransport(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
    }

    public ValueTask<int> SendAsync(GraphHttpMethod method, ReadOnlyMemory<byte> url, ReadOnlyMemory<byte> headers, ReadOnlyMemory<byte> body, GraphResponseBuffer resp, CancellationToken ct)
        => new(SendCoreAsync(method, url, headers, body, resp, ct));

    private async Task<int> SendCoreAsync(GraphHttpMethod method, ReadOnlyMemory<byte> url, ReadOnlyMemory<byte> headers, ReadOnlyMemory<byte> body, GraphResponseBuffer resp, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method == GraphHttpMethod.Post ? HttpMethod.Post : method == GraphHttpMethod.Put ? HttpMethod.Put : method == GraphHttpMethod.Delete ? HttpMethod.Delete : HttpMethod.Get, System.Text.Encoding.UTF8.GetString(url.Span));
        if (method is GraphHttpMethod.Post or GraphHttpMethod.Put) req.Content = new ReadOnlyMemoryContent(body);
        var h = System.Text.Encoding.UTF8.GetString(headers.Span);
        foreach (var line in h.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var c = line.IndexOf(':');
            if (c <= 0) continue;
            var name = line[..c]; var value = line[(c + 1)..].Trim();
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) || name.Equals("Content-Range", StringComparison.OrdinalIgnoreCase)) { if (req.Content is not null) req.Content.Headers.TryAddWithoutValidation(name, value); }
            else req.Headers.TryAddWithoutValidation(name, value);
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        using var r = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        if (r.Headers.RetryAfter is { } ra) resp.RetryAfterSec = (int)Math.Min((ra.Delta ?? (ra.Date is { } d ? d - DateTimeOffset.UtcNow : TimeSpan.Zero)).TotalSeconds, 3600);
        if (r.Headers.Location is { } loc) { var ls = loc.OriginalString; if (ls.Length <= GraphResponseBuffer.LocationCap) resp.LocationLength = System.Text.Encoding.UTF8.GetBytes(ls, resp.Location); }
        if (r.Headers.TryGetValues("Range", out var rng)) foreach (var rv in rng) { if (rv.Length <= resp.Range.Length) resp.RangeLength = System.Text.Encoding.ASCII.GetBytes(rv, resp.Range); break; }
        if (r.Headers.TryGetValues("X-App-Usage", out var usage))
            foreach (var u in usage) { resp.AppUsageLength = System.Text.Encoding.UTF8.GetBytes(u.AsSpan(0, Math.Min(u.Length, GraphResponseBuffer.UsageCap / 4)), resp.AppUsage); break; }
        await using var s = await r.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        var n = 0;
        while (n < GraphResponseBuffer.BodyCap)
        {
            var k = await s.ReadAsync(resp.Body.AsMemory(n), cts.Token).ConfigureAwait(false);
            if (k == 0) break;
            n += k;
        }
        resp.Truncated = n == GraphResponseBuffer.BodyCap;
        resp.Length = n;
        return (int)r.StatusCode;
    }

    public void Dispose() => _http.Dispose();
}
