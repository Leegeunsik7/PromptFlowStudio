using System;
using System.Collections.Generic;
using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Wan;

/// <summary>
/// [PromptFlowStudio] v15.15.0 응답 본문 풀 버퍼 — <see cref="HttpContent"/> 스트림을 <see cref="ArrayPool{T}"/> 버퍼로 직접 수신(중간 string 0).
///   ▸ <see cref="Span"/> 은 UTF-8 원본(BOM 제거) 그대로 → <see cref="Utf8JsonReader"/> Zero-Copy 파싱.
///   ▸ <see cref="Text"/> 는 오류 메시지 등 사람이 읽을 때만 지연 물질화. <c>using</c> 으로 반환(사용 후 참조 금지).
/// </summary>
internal readonly struct PooledUtf8Body : IDisposable
{
    /// <summary>응답 본문 상한(초과 시 <see cref="InvalidDataException"/> — 비정상 서버 방어).</summary>
    public const int MaxBytes = 8 * 1024 * 1024;

    private readonly byte[]? _buf;
    private readonly int _start;
    private readonly int _length;

    private PooledUtf8Body(byte[] buf, int start, int length) { _buf = buf; _start = start; _length = length; }

    public int Length => _length;
    public ReadOnlySpan<byte> Span => _buf is null ? default : new ReadOnlySpan<byte>(_buf, _start, _length);
    public string Text => _length == 0 ? "" : Encoding.UTF8.GetString(Span);

    public static async ValueTask<PooledUtf8Body> ReadAsync(HttpContent content, CancellationToken ct)
    {
        var hint = content.Headers.ContentLength;
        var buf = ArrayPool<byte>.Shared.Rent(hint is long h && h > 0 && h <= MaxBytes ? (int)h + 1 : 4096);
        int len = 0;
        try
        {
            var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                while (true)
                {
                    if (len == buf.Length)
                    {
                        if (len >= MaxBytes) throw new InvalidDataException($"응답 본문이 상한({MaxBytes / 1048576}MB)을 초과했습니다.");
                        var grown = ArrayPool<byte>.Shared.Rent(Math.Min(MaxBytes, len * 2));
                        Buffer.BlockCopy(buf, 0, grown, 0, len);
                        ArrayPool<byte>.Shared.Return(buf);
                        buf = grown;
                    }
                    int n = await stream.ReadAsync(buf.AsMemory(len), ct).ConfigureAwait(false);
                    if (n == 0) break;
                    len += n;
                }
            }
            int start = len >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF ? 3 : 0;   // UTF-8 BOM(Utf8JsonReader 는 BOM 미허용)
            return new PooledUtf8Body(buf, start, len - start);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buf);
            throw;
        }
    }

    public void Dispose()
    {
        if (_buf is not null) ArrayPool<byte>.Shared.Return(_buf);
    }
}

/// <summary>
/// [PromptFlowStudio] v15.15.0 Wan 응답 스키마 단일 패스 스캐너(<see cref="Utf8JsonReader"/> · DOM 0).
///   DashScope(제출·조회·getPolicy·오류) + SiliconFlow(제출·조회) 응답에서 쓰는 필드만 1회 순회로 추출 — 문자열은 일치한 값만 물질화.
///   의미는 기존 <c>JsonDocument</c> + <c>Str()</c> 와 동일: 문자열이 아닌 값은 "" · 중복 키는 마지막 값 · 문서 전체 검증(손상 시 <see cref="JsonException"/>).
/// </summary>
internal struct WanWire
{
    public JsonTokenType RootKind;
    // 루트
    public string Code, Message, RequestId, RequestIdCamel, Status, Reason;
    // output{}(DashScope)
    public bool HasOutput;
    public string TaskId, TaskStatus, VideoUrl, OutputCode, OutputMessage;
    // data{}(getPolicy)
    public bool HasData;
    public string UploadHost, UploadDir, AccessKeyId, Signature, Policy, ObjectAcl, ForbidOverwrite;
    public int MaxFileSizeMb;   // v16.3.1: 정책 상한(0 = 미제공)
    // results.videos[0].url(SiliconFlow)
    public string FirstVideoUrl;

    public readonly bool RootIsObject => RootKind == JsonTokenType.StartObject;

    /// <summary>루트가 객체가 아니면 기존 <c>JsonElement.TryGetProperty</c> 와 같이 <see cref="InvalidOperationException"/>.</summary>
    public readonly void RequireObject()
    {
        if (!RootIsObject) throw new InvalidOperationException($"응답 루트가 JSON 객체가 아닙니다({RootKind}).");
    }

    public static WanWire Parse(ReadOnlySpan<byte> utf8)
    {
        var w = new WanWire
        {
            Code = "", Message = "", RequestId = "", RequestIdCamel = "", Status = "", Reason = "",
            FirstVideoUrl = "",
        };
        w.ResetOutput();
        w.ResetData();

        var r = new Utf8JsonReader(utf8, default(JsonReaderOptions));
        if (!r.Read()) throw new JsonException("응답 본문이 비어 있습니다.");
        w.RootKind = r.TokenType;
        if (r.TokenType != JsonTokenType.StartObject)
        {
            r.Skip();
        }
        else
        {
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                if (r.ValueTextEquals("output"u8))          { w.HasOutput = true; w.ResetOutput(); ReadOutput(ref r, ref w); }
                else if (r.ValueTextEquals("data"u8))       { w.HasData = true; w.ResetData(); ReadData(ref r, ref w); }
                else if (r.ValueTextEquals("results"u8))    { w.FirstVideoUrl = ReadResults(ref r); }
                else if (r.ValueTextEquals("code"u8))       w.Code = ReadStr(ref r);
                else if (r.ValueTextEquals("message"u8))    w.Message = ReadStr(ref r);
                else if (r.ValueTextEquals("request_id"u8)) w.RequestId = ReadStr(ref r);
                else if (r.ValueTextEquals("requestId"u8))  w.RequestIdCamel = ReadStr(ref r);
                else if (r.ValueTextEquals("status"u8))     w.Status = ReadStr(ref r);
                else if (r.ValueTextEquals("reason"u8))     w.Reason = ReadStr(ref r);
                else SkipValue(ref r);
            }
        }
        if (r.Read()) throw new JsonException("루트 값 뒤에 추가 토큰이 있습니다.");   // (리더가 대부분 먼저 예외)
        return w;
    }

    private void ResetOutput() { TaskId = ""; TaskStatus = ""; VideoUrl = ""; OutputCode = ""; OutputMessage = ""; }

    private void ResetData()
    {
        UploadHost = ""; UploadDir = ""; AccessKeyId = ""; Signature = ""; Policy = ""; ObjectAcl = ""; ForbidOverwrite = ""; MaxFileSizeMb = 0;
    }

    // 프로퍼티명 위치에서 호출 — 값이 문자열이면 물질화, 그 외(객체/배열 포함)는 건너뛰고 "".
    private static string ReadStr(ref Utf8JsonReader r)
    {
        r.Read();
        if (r.TokenType == JsonTokenType.String) return r.GetString() ?? "";
        r.Skip();
        return "";
    }

    // 숫자(또는 숫자 문자열) → int, 그 외 0.
    private static int ReadInt(ref Utf8JsonReader r)
    {
        r.Read();
        if (r.TokenType == JsonTokenType.Number && r.TryGetInt32(out var n)) return n;
        if (r.TokenType == JsonTokenType.String && int.TryParse(r.ValueSpan, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out n)) return n;
        r.Skip();
        return 0;
    }

    private static void SkipValue(ref Utf8JsonReader r)
    {
        r.Read();
        r.Skip();   // 원시값은 no-op · 객체/배열은 끝까지
    }

    private static void ReadOutput(ref Utf8JsonReader r, ref WanWire w)
    {
        r.Read();
        if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); return; }
        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            if (r.ValueTextEquals("task_id"u8))          w.TaskId = ReadStr(ref r);
            else if (r.ValueTextEquals("task_status"u8)) w.TaskStatus = ReadStr(ref r);
            else if (r.ValueTextEquals("video_url"u8))   w.VideoUrl = ReadStr(ref r);
            else if (r.ValueTextEquals("code"u8))        w.OutputCode = ReadStr(ref r);
            else if (r.ValueTextEquals("message"u8))     w.OutputMessage = ReadStr(ref r);
            else SkipValue(ref r);
        }
    }

    private static void ReadData(ref Utf8JsonReader r, ref WanWire w)
    {
        r.Read();
        if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); return; }
        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            if (r.ValueTextEquals("upload_host"u8))                 w.UploadHost = ReadStr(ref r);
            else if (r.ValueTextEquals("upload_dir"u8))             w.UploadDir = ReadStr(ref r);
            else if (r.ValueTextEquals("oss_access_key_id"u8))      w.AccessKeyId = ReadStr(ref r);
            else if (r.ValueTextEquals("signature"u8))              w.Signature = ReadStr(ref r);
            else if (r.ValueTextEquals("policy"u8))                 w.Policy = ReadStr(ref r);
            else if (r.ValueTextEquals("x_oss_object_acl"u8))       w.ObjectAcl = ReadStr(ref r);
            else if (r.ValueTextEquals("x_oss_forbid_overwrite"u8)) w.ForbidOverwrite = ReadStr(ref r);
            else if (r.ValueTextEquals("max_file_size_mb"u8))       w.MaxFileSizeMb = ReadInt(ref r);
            else SkipValue(ref r);
        }
    }

    // results{ videos:[ { url } , ... ] } → 첫 영상 url(구조 불일치 시 "").
    private static string ReadResults(ref Utf8JsonReader r)
    {
        r.Read();
        if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); return ""; }
        var url = "";
        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            if (!r.ValueTextEquals("videos"u8)) { SkipValue(ref r); continue; }
            r.Read();
            url = "";
            if (r.TokenType != JsonTokenType.StartArray) { r.Skip(); continue; }
            if (r.Read() && r.TokenType != JsonTokenType.EndArray)
            {
                if (r.TokenType == JsonTokenType.StartObject)
                {
                    while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                    {
                        if (r.ValueTextEquals("url"u8)) url = ReadStr(ref r);
                        else SkipValue(ref r);
                    }
                }
                else r.Skip();
                while (r.Read() && r.TokenType != JsonTokenType.EndArray) r.Skip();   // 나머지 원소
            }
        }
        return url;
    }
}

/// <summary>
/// [PromptFlowStudio] v16.3.1 OSS PostObject 멀티파트 본문 — <see cref="MultipartFormDataContent"/> 대체(OSS 400 MalformedPOSTRequest 방지).
///   ▸ boundary 는 토큰 문자만(따옴표 없음) · 필드 파트에 Content-Type 미부착 · name/filename 은 항상 따옴표 · CRLF 규격(RFC 7578).
///   ▸ filename 은 ASCII 안전형(<see cref="SafeAsciiFileName"/>) — form-data 에서 RFC 5987 <c>filename*</c> 는 금지(RFC 7578 §4.2)이며 OSS 파서가 거부.
///   ▸ <see cref="TryComputeLength"/> 가 머리+파일+꼬리 정확 길이 → 항상 Content-Length 전송(chunked 없음). 파일은 직렬화 시점에 열어
///     풀 버퍼(81920B)로 스트리밍 — 32MB 이미지도 전체 로드 없음 · 재전송(재시도) 시 새로 연다. 길이 불일치(전송 중 파일 변경)는 <see cref="IOException"/>.
/// </summary>
internal sealed class OssPostObjectContent : HttpContent
{
    private const int BufferSize = 81920;
    private readonly byte[] _head;
    private readonly byte[] _tail;
    private readonly string _filePath;
    private readonly long _fileLength;

    public string Boundary { get; }

    /// <param name="fields">OSS 폼 필드(순서 유지) — 'file' 은 자동으로 마지막에 붙는다(OSS 규약).</param>
    public OssPostObjectContent(ReadOnlySpan<KeyValuePair<string, string>> fields, string fileName, string contentType, string filePath)
    {
        _filePath = filePath;
        _fileLength = new FileInfo(filePath).Length;
        Boundary = "----PromptFlowOss" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));

        var sb = new StringBuilder(512 + fields.Length * 96);
        foreach (var f in fields)
        {
            if (!IsHeaderSafe(f.Key) || f.Value.AsSpan().IndexOfAny('\r', '\n') >= 0)
                throw new ArgumentException("OSS 폼 필드에 제어 문자가 포함되어 있습니다: " + f.Key, nameof(fields));
            sb.Append("--").Append(Boundary).Append("\r\n")
              .Append("Content-Disposition: form-data; name=\"").Append(f.Key).Append("\"\r\n\r\n")
              .Append(f.Value).Append("\r\n");
        }
        var safeName = SafeAsciiFileName(fileName);
        if (!IsHeaderSafe(contentType)) throw new ArgumentException("Content-Type 이 올바르지 않습니다.", nameof(contentType));
        sb.Append("--").Append(Boundary).Append("\r\n")
          .Append("Content-Disposition: form-data; name=\"file\"; filename=\"").Append(safeName).Append("\"\r\n")
          .Append("Content-Type: ").Append(contentType).Append("\r\n\r\n");
        _head = Encoding.UTF8.GetBytes(sb.ToString());
        _tail = Encoding.ASCII.GetBytes("\r\n--" + Boundary + "--\r\n");

        // boundary 는 토큰 문자뿐이라 MediaTypeHeaderValue 가 따옴표 없이 직렬화한다.
        Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("multipart/form-data");
        Headers.ContentType.Parameters.Add(new System.Net.Http.Headers.NameValueHeaderValue("boundary", Boundary));
    }

    public long FileLength => _fileLength;

    protected override bool TryComputeLength(out long length)
    {
        length = _head.Length + _fileLength + _tail.Length;
        return true;
    }

    protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken ct)
    {
        await stream.WriteAsync(_head, ct).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            long sent = 0;
            int n;
            while ((n = await fs.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
            {
                sent += n;
                if (sent > _fileLength) break;
                await stream.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            }
            if (sent != _fileLength)
                throw new IOException($"업로드 중 이미지 파일 크기가 바뀌었습니다({_fileLength}B → {sent}B).");
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        await stream.WriteAsync(_tail, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// ASCII 안전 파일명 — [A-Za-z0-9._-] 외(유니코드·공백·따옴표·제어문자)는 '_' 로 치환·연속 축약, 기반명 최대 64자, 확장자 소문자 유지.
    /// 기반명이 비면 "image". 결과는 Content-Disposition·OSS 객체 키 모두에 그대로 쓸 수 있다.
    /// </summary>
    public static string SafeAsciiFileName(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? "");
        var ext = Path.GetExtension(name);
        var stem = ext.Length > 0 ? name[..^ext.Length] : name;
        static string Clean(string s, int max)
        {
            var sb = new StringBuilder(Math.Min(s.Length, max));
            bool lastUnderscore = false;
            foreach (var c in s)
            {
                bool ok = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-';
                if (ok) { sb.Append(c); lastUnderscore = false; }
                else if (!lastUnderscore && sb.Length > 0) { sb.Append('_'); lastUnderscore = true; }
                if (sb.Length >= max) break;
            }
            return sb.ToString().Trim('_', '.', '-');
        }
        stem = Clean(stem, 64);
        if (stem.Length == 0) stem = "image";
        ext = Clean(ext.TrimStart('.'), 8).ToLowerInvariant();
        return ext.Length > 0 ? stem + "." + ext : stem;
    }

    private static bool IsHeaderSafe(string s)
    {
        foreach (var c in s) if (c < 0x20 || c > 0x7E || c == '"') return false;
        return s.Length > 0;
    }
}

/// <summary>v16.3.1 업로드 실패 원인 분류 — UI 알림/재시도 판단용(값 형식).</summary>
public enum WanUploadFailure : byte { None, InvalidFile, TooLarge, Network, Timeout, PolicyRejected, CredentialExpired, MalformedRequest, ServerError }

/// <summary>
/// [PromptFlowStudio] v16.3.1 OSS 오류 응답(XML) → 원인 분류 + 사용자용 한 줄. 원문 XML/HTML 은 UI 로 내보내지 않는다(로그 전용).
/// </summary>
internal static class OssErrorMapper
{
    /// <summary>XML 의 첫 &lt;Code&gt;·&lt;Message&gt; 텍스트(없으면 "") — 파서 없이 경계 검색(엔티티/CDATA 미사용 응답 전제 · 최대 200자).</summary>
    public static (string Code, string Message) ParseXmlError(string? body)
    {
        if (string.IsNullOrEmpty(body)) return ("", "");
        return (Tag(body, "Code"), Tag(body, "Message"));

        static string Tag(string s, string tag)
        {
            int a = s.IndexOf("<" + tag + ">", StringComparison.Ordinal);
            if (a < 0) return "";
            a += tag.Length + 2;
            int b = s.IndexOf("</" + tag + ">", a, StringComparison.Ordinal);
            if (b < 0) return "";
            var v = s.AsSpan(a, Math.Min(b - a, 200)).Trim();
            foreach (var c in v) if (c == '<' || c == '>') return "";
            return v.ToString();
        }
    }

    /// <summary>HTTP 상태 + OSS 오류 코드 → (분류, 사용자 메시지).</summary>
    public static (WanUploadFailure Kind, string Message) Map(int status, string ossCode)
    {
        var tag = ossCode.Length > 0 ? $" (HTTP {status} · {ossCode})" : $" (HTTP {status})";
        return ossCode switch
        {
            "MalformedPOSTRequest" or "InvalidArgument" or "InvalidEncryptionAlgorithmError"
                => (WanUploadFailure.MalformedRequest, "이미지 업로드 실패: 업로드 요청 형식을 서버가 거부했습니다. 다시 시도해 주세요." + tag),
            "EntityTooLarge" or "FileGroupTooLarge"
                => (WanUploadFailure.TooLarge, "이미지 업로드 실패: 이미지가 업로드 허용 용량을 초과했습니다." + tag),
            "AccessDenied" or "SignatureDoesNotMatch" or "InvalidAccessKeyId" or "SecurityTokenExpired" or "RequestTimeTooSkewed"
                => (WanUploadFailure.CredentialExpired, "이미지 업로드 실패: 임시 업로드 자격이 만료되었거나 거부되었습니다. 다시 시도하면 새 자격으로 업로드합니다." + tag),
            "InvalidPolicyDocument"
                => (WanUploadFailure.PolicyRejected, "이미지 업로드 실패: 업로드 정책 조건(용량·형식)을 충족하지 못했습니다." + tag),
            "RequestTimeout"
                => (WanUploadFailure.Timeout, "이미지 업로드 실패: 업로드 시간이 초과되었습니다. 네트워크 상태를 확인해 주세요." + tag),
            _ when status >= 500 || status == 429
                => (WanUploadFailure.ServerError, "이미지 업로드 실패: 업로드 서버가 일시적으로 응답하지 않습니다. 잠시 후 다시 시도해 주세요." + tag),
            _ => (WanUploadFailure.PolicyRejected, "이미지 업로드 실패: 업로드 서버가 요청을 거부했습니다." + tag),
        };
    }

    /// <summary>재시도로 회복 가능한 분류(일시 장애).</summary>
    public static bool IsTransient(WanUploadFailure k) => k is WanUploadFailure.Network or WanUploadFailure.Timeout or WanUploadFailure.ServerError;
}
