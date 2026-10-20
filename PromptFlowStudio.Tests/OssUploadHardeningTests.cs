using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Operations;
using PromptFlowStudio.Services.Platform;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Tests;

/// <summary>v16.3.1 OSS 업로드 강화 — 고용량 멀티파트 규격 · 파일명 정규화 · 원인별 오류 · 사용자 알림/재시도.</summary>
public class OssUploadHardeningTests
{
    [Fact]
    public async Task LargeImage_MultipartUpload_And_ErrorAlert_Check()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pfs-oss-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 6MB JPG · 유니코드/공백/특수문자 파일명
            var img = Path.Combine(dir, "고화질 사진 (최종) #1.JPG");
            var jpg = System.Security.Cryptography.RandomNumberGenerator.GetBytes(6 * 1024 * 1024 + 123);
            jpg[0] = 0xFF; jpg[1] = 0xD8; jpg[2] = 0xFF; jpg[3] = 0xE0;
            File.WriteAllBytes(img, jpg);

            // ══ ① 정상 업로드: boundary 비인용 · 필드 Content-Type 없음 · file 마지막 · ASCII filename/key · 정확한 Content-Length ══
            var oss = new OssHandler();
            var svc = NewService(oss);
            var up = await svc.UploadImageAsync(img);
            Assert.True(up.Ok, up.Message);
            Assert.Equal(WanUploadFailure.None, up.Failure);
            Assert.Equal(1, oss.Uploads);
            Assert.Null(oss.LastChunked == true ? "chunked" : null);
            Assert.Equal(oss.LastBody!.Length, oss.LastContentLength);

            var ct = oss.LastContentType!;
            Assert.StartsWith("multipart/form-data; boundary=", ct);
            var boundary = ct["multipart/form-data; boundary=".Length..];
            Assert.DoesNotContain("\"", boundary);
            var parts = SplitMultipart(oss.LastBody!, boundary);
            Assert.Equal(new[] { "key", "policy", "OSSAccessKeyId", "Signature", "x-oss-content-type", "success_action_status", "x-oss-object-acl", "file" }, parts.ConvertAll(p => p.Name).ToArray());
            foreach (var p in parts.GetRange(0, parts.Count - 1)) Assert.DoesNotContain("Content-Type:", p.Headers);
            var file = parts[^1];
            Assert.Contains("filename=\"", file.Headers);
            Assert.DoesNotContain("filename*", file.Headers);
            Assert.True(IsAscii(file.Headers), "파트 헤더에 비ASCII");
            Assert.Contains("Content-Type: image/jpeg", file.Headers);
            Assert.Equal(jpg, file.Body);                                         // 6MB 바이트 무손실
            var key = Encoding.UTF8.GetString(parts[0].Body);
            Assert.Equal("oss://" + key, up.OssUrl);
            Assert.StartsWith("dir-x/", key);
            Assert.True(IsAscii(key) && !key.Contains(' '), key);
            Assert.EndsWith("_1.jpg", key);                                       // "…_최종_1" → ASCII 축약 + 확장자 소문자
            Assert.Equal("image/jpeg", Encoding.UTF8.GetString(parts[4].Body));

            // ══ ② OSS 400 MalformedPOSTRequest(XML) → 원인 분류 · 원문 XML 비노출 · 재시도 안 함 ══
            var bad = new OssHandler { UploadStatus = { HttpStatusCode.BadRequest } };
            var r400 = await NewService(bad).UploadImageAsync(img);
            Assert.False(r400.Ok);
            Assert.Equal(WanUploadFailure.MalformedRequest, r400.Failure);
            Assert.DoesNotContain("<", r400.Message);
            Assert.Contains("MalformedPOSTRequest", r400.Message);
            Assert.Equal(1, bad.Uploads);

            // ③ 503 → 지수 백오프 재시도 후 성공
            var flaky = new OssHandler { UploadStatus = { HttpStatusCode.ServiceUnavailable } };
            var r503 = await NewService(flaky).UploadImageAsync(img);
            Assert.True(r503.Ok, r503.Message);
            Assert.Equal(2, flaky.Uploads);

            // ④ 네트워크 단절 → 3회 시도 후 Network 분류
            var down = new OssHandler { ThrowOnUpload = true };
            var rNet = await NewService(down).UploadImageAsync(img);
            Assert.Equal(WanUploadFailure.Network, rNet.Failure);
            Assert.Equal(3, down.Uploads);

            // ⑤ 정책 상한(max_file_size_mb) · 32MB 상한 · 위장 확장자 → 네트워크 이전/업로드 이전 차단
            var small = new OssHandler { MaxFileSizeMb = 5 };
            var rPol = await NewService(small).UploadImageAsync(img);
            Assert.Equal(WanUploadFailure.TooLarge, rPol.Failure);
            Assert.Equal(0, small.Uploads);

            var huge = Path.Combine(dir, "huge.png");
            using (var fs = new FileStream(huge, FileMode.Create))
            {
                fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
                fs.SetLength(WanApiService.MaxUploadImageBytes + 1);
            }
            var none = new OssHandler();
            var rHuge = await NewService(none).UploadImageAsync(huge);
            Assert.Equal(WanUploadFailure.TooLarge, rHuge.Failure);
            Assert.Contains("최대 32MB", rHuge.Message);
            Assert.Equal(0, none.Policies);

            var fake = Path.Combine(dir, "not-image.webp");
            File.WriteAllText(fake, "<html>not an image</html>");
            Assert.Equal(WanUploadFailure.InvalidFile, (await NewService(none).UploadImageAsync(fake)).Failure);

            var webp = Path.Combine(dir, "ok.webp");
            File.WriteAllBytes(webp, Concat("RIFF"u8.ToArray(), new byte[4], "WEBPVP8 "u8.ToArray(), new byte[64]));
            var okWebp = new OssHandler();
            Assert.True((await NewService(okWebp).UploadImageAsync(webp)).Ok);
            Assert.Contains("Content-Type: image/webp", SplitMultipart(okWebp.LastBody!, okWebp.LastContentType!["multipart/form-data; boundary=".Length..])[^1].Headers);

            // ⑥ 제출 경로: 업로드 실패 메시지는 접두 중복 없이 전달 → 알림 분류
            var submitBad = new OssHandler { UploadStatus = { HttpStatusCode.BadRequest } };
            var sub = await NewService(submitBad).SubmitAsync(new WanVideoRequest { Prompt = "p", ImagePath = img });
            Assert.False(sub.Ok);
            Assert.StartsWith("이미지 업로드 실패:", sub.Message);
            Assert.DoesNotContain("이미지 업로드 실패: 이미지 업로드 실패", sub.Message);
            Assert.True(ErrorDialogService.IsUploadOrNetworkFailure(sub.Message));

            // ══ ⑦ 사용자 알림: 원문 XML 정화 · 업로드/네트워크 전용 안내 · [다시 시도] 결선 ══
            const string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Error><Code>MalformedPOSTRequest</Code><Message>The body of your POST request is not well-formed multipart/form-data.</Message></Error>";
            var cleaned = ErrorDialogService.CleanMessage(xml);
            Assert.DoesNotContain("<", cleaned);
            Assert.Contains("MalformedPOSTRequest", cleaned);
            Assert.Equal("서버가 오류 문서를 반환했습니다.", ErrorDialogService.CleanMessage("<html><body></body></html>"));

            var h = ErrorDialogService.Humanize("이미지 업로드 실패: 업로드 서버가 일시적으로 응답하지 않습니다. (HTTP 403 · AccessDenied)");
            Assert.Equal(ErrorDialogService.UploadNetworkTitle, h.Title);   // HTTP 403 이 API 키 오류로 오분류되지 않음
            Assert.Contains("다시 시도", h.Hint);
            Assert.Equal(ErrorDialogService.UploadNetworkTitle, ErrorDialogService.Humanize("System.Net.Http.HttpRequestException: connection reset").Title);
            Assert.DoesNotContain("<", ErrorDialogService.Humanize(xml).Message);
            Assert.False(ErrorDialogService.IsUploadOrNetworkFailure(rHuge.Message));   // 용량 초과는 재시도 무의미 → 일반 이미지 안내
            Assert.NotEqual(ErrorDialogService.UploadNetworkTitle, ErrorDialogService.Humanize(rHuge.Message).Title);

            var dialog = new ErrorDialogService();
            int retried = 0, changes = 0;
            dialog.OnChange += () => changes++;
            dialog.Show(r400.Message, () => retried++);
            Assert.NotNull(dialog.Current);
            Assert.NotNull(dialog.RetryAction);
            dialog.Retry();
            Assert.Equal(1, retried);
            Assert.Null(dialog.Current);
            Assert.Null(dialog.RetryAction);
            dialog.Show("x", () => throw new InvalidOperationException("boom"));
            dialog.Retry();                                   // 재시도 예외 격리
            dialog.Show("y");
            Assert.Null(dialog.RetryAction);
            Assert.Equal(5, changes);

            // ⑧ 파일명 정규화
            Assert.Equal("image.png", OssPostObjectContent.SafeAsciiFileName("사진.PNG"));
            Assert.Equal("my_photo_v2.jpeg", OssPostObjectContent.SafeAsciiFileName("my photo \"v2\".JPEG"));
            Assert.Equal("image", OssPostObjectContent.SafeAsciiFileName(""));
            Assert.True(OssPostObjectContent.SafeAsciiFileName(new string('a', 300) + ".jpg").Length <= 68);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    private static WanApiService NewService(HttpMessageHandler handler)
    {
        var vault = new WanTokenVault(null);
        vault.UpdateProviderKeys(WanTokenVault.DashScope, new[] { "ds-x" });
        return new WanApiService(new FakeConfig(), new QuotaRateGuardService(), vault, handler);
    }

    private static bool IsAscii(string s) { foreach (var c in s) if (c > 0x7E) return false; return true; }

    private static byte[] Concat(params byte[][] parts)
    {
        var ms = new MemoryStream();
        foreach (var p in parts) ms.Write(p);
        return ms.ToArray();
    }

    private sealed record Part(string Name, string Headers, byte[] Body);

    // RFC 7578 엄격 파서: --B CRLF 헤더 CRLFCRLF 본문 CRLF … --B-- CRLF
    private static List<Part> SplitMultipart(byte[] body, string boundary)
    {
        var parts = new List<Part>();
        var delim = Encoding.ASCII.GetBytes("--" + boundary);
        var span = body.AsSpan();
        Assert.True(span.StartsWith(delim), "본문이 boundary 로 시작하지 않음");
        Assert.True(span.EndsWith(Encoding.ASCII.GetBytes("\r\n--" + boundary + "--\r\n")), "종결 boundary 불일치");
        int pos = delim.Length;
        var sep = Encoding.ASCII.GetBytes("\r\n--" + boundary);
        while (true)
        {
            Assert.True(span[pos..].StartsWith("\r\n"u8));
            pos += 2;
            int hdrEnd = span[pos..].IndexOf("\r\n\r\n"u8);
            var headers = Encoding.UTF8.GetString(span.Slice(pos, hdrEnd));
            pos += hdrEnd + 4;
            int next = span[pos..].IndexOf(sep);
            var content = span.Slice(pos, next).ToArray();
            pos += next + sep.Length;
            var m = System.Text.RegularExpressions.Regex.Match(headers, "name=\"([^\"]+)\"");
            Assert.True(m.Success, headers);
            parts.Add(new Part(m.Groups[1].Value, headers, content));
            if (span[pos..].StartsWith("--"u8)) break;
        }
        return parts;
    }

    private sealed class OssHandler : HttpMessageHandler
    {
        public readonly List<HttpStatusCode> UploadStatus = new();   // 순서대로 소비 후 200
        public bool ThrowOnUpload;
        public int MaxFileSizeMb = 100;
        public int Uploads, Policies;
        public byte[]? LastBody;
        public long? LastContentLength;
        public bool? LastChunked;
        public string? LastContentType;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("action=getPolicy", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref Policies);
                return Json("{\"data\":{\"upload_host\":\"https://oss.mock/\",\"upload_dir\":\"dir-x\",\"oss_access_key_id\":\"AK\",\"signature\":\"SIG\",\"policy\":\"POL\",\"x_oss_object_acl\":\"private\",\"max_file_size_mb\":" + MaxFileSizeMb + "}}");
            }
            if (url.StartsWith("https://oss.mock/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref Uploads);
                LastContentLength = request.Content!.Headers.ContentLength;
                LastChunked = request.Headers.TransferEncodingChunked;
                LastContentType = request.Content.Headers.ContentType!.ToString();
                using (var ms = new MemoryStream())
                {
                    await request.Content.CopyToAsync(ms, ct);
                    LastBody = ms.ToArray();
                }
                if (ThrowOnUpload) throw new HttpRequestException("connection reset by peer");
                if (UploadStatus.Count > 0)
                {
                    var s = UploadStatus[0];
                    UploadStatus.RemoveAt(0);
                    var code = s == HttpStatusCode.BadRequest ? "MalformedPOSTRequest" : "ServiceUnavailable";
                    return new HttpResponseMessage(s) { Content = new StringContent("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Error>\n  <Code>" + code + "</Code>\n  <Message>The body of your POST request is not well-formed multipart/form-data.</Message>\n  <RequestId>R1</RequestId>\n</Error>", Encoding.UTF8, "application/xml") };
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") };
            }
            if (url.Contains("video-synthesis", StringComparison.Ordinal))
                return Json("{\"output\":{\"task_id\":\"t1\",\"task_status\":\"PENDING\"},\"request_id\":\"r\"}");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class FakeConfig : IWanConfigService
    {
        public string ApiKey => "";
        public string BaseUrl => "https://dashscope.mock";
        public string Model => "wan2.1-t2v-turbo";
        public string Size => "1280*720";
        public int PollIntervalSeconds => 1;
        public int MaxWaitSeconds => 5;
        public bool IsConfigured => false;
        public string FilePath => "";
        public DateTime? SavedAt => null;
#pragma warning disable CS0067
        public event Action? OnConfigChanged;
#pragma warning restore CS0067
        public bool Save(string apiKey, string baseUrl) => false;
    }
}
