using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Storyboard;   // StoryboardProject

namespace PromptFlowStudio.Services.Mobile;

/// <summary>전송 자산 참조 — 이름/크기/SHA-256.</summary>
public sealed record AssetRef(string Name, long Size, string Sha256);

/// <summary>렌더 잡 봉투 — 잡ID/project.json/자산목록/체크섬/스키마버전.</summary>
public sealed record RenderJobEnvelope(string JobId, string ProjectJson, IReadOnlyList<AssetRef> Assets, string Checksum, int SchemaVersion);

/// <summary>암호화 페이로드 — Base64 IV/Cipher + 체크섬(전송 본문).</summary>
public sealed record EncryptedPayload(string JobId, string Iv, string Cipher, string Checksum, int SchemaVersion);

/// <summary>렌더 진행 단계.</summary>
public enum RenderPhase { Queued, Uploading, Rendering, Downloading, Done, Failed }

/// <summary>렌더 진행 보고.</summary>
public sealed record RenderProgress(RenderPhase Phase, double Percent, string Message);

/// <summary>리모트 렌더 결과 — 성공/잡ID/영상·PDF 경로/메시지.</summary>
public sealed record RenderJobResult(bool Ok, string JobId, string? VideoPath, string? SpecPdfPath, string Message);

/// <summary>
/// [PromptFlowStudio] v4.0.0 리모트 렌더링 &amp; 오프로딩 클라이언트(Task 3 · 순수 BCL HTTP/WebSocket · 외부 SDK 0).
///   ▸ 스마트폰의 렌더 명령을 로컬 PC(RTX GPU) 워커로 위임 — <b>project.json + 자산을 AES-256 암호화</b> 전송(<see cref="Encrypt"/>).
///   ▸ HTTP 로 잡 제출 → <b>WebSocket</b>(<see cref="ClientWebSocket"/>)으로 실시간 진행 수신 → 완료 60fps MP4 + PDF 스펙 시트 다운로드.
///   ▸ 워커 부재/오프라인은 Graceful(Failed 반환 · 앱은 로컬 폴백). 순수 헬퍼(봉투/체크섬/URL/암호화)는 테스트 가능.
/// </summary>
public sealed class RemoteRenderClient
{
    /// <summary>봉투 스키마 버전(워커 호환 협상).</summary>
    public const int SchemaVersion = 1;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(30) };

    // ══════════════════════ 순수 헬퍼(테스트 가능) ══════════════════════

    /// <summary>SHA-256 16진 문자열.</summary>
    public static string Sha256Hex(byte[] data)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(data ?? Array.Empty<byte>());
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    /// <summary>패스프레이즈 → AES-256 키(32바이트).</summary>
    public static byte[] DeriveKey(string passphrase)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(Encoding.UTF8.GetBytes(passphrase ?? ""));
    }

    /// <summary>AES-256-CBC 암호화(랜덤 IV 산출).</summary>
    public static byte[] Encrypt(byte[] plain, byte[] key, out byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key; aes.GenerateIV(); iv = aes.IV;
        using var enc = aes.CreateEncryptor();
        return enc.TransformFinalBlock(plain, 0, plain.Length);
    }

    /// <summary>AES-256-CBC 복호화.</summary>
    public static byte[] Decrypt(byte[] cipher, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key; aes.IV = iv;
        using var dec = aes.CreateDecryptor();
        return dec.TransformFinalBlock(cipher, 0, cipher.Length);
    }

    /// <summary>프로젝트 → 렌더 잡 봉투(project.json 직렬화 + 체크섬).</summary>
    public static RenderJobEnvelope BuildEnvelope(StoryboardProject project, IReadOnlyList<AssetRef>? assets = null)
    {
        var json = JsonSerializer.Serialize(project, PromptFlowJsonContext.Default.StoryboardProject);
        var checksum = Sha256Hex(Encoding.UTF8.GetBytes(json));
        return new RenderJobEnvelope(Guid.NewGuid().ToString("N")[..12], json, assets ?? Array.Empty<AssetRef>(), checksum, SchemaVersion);
    }

    /// <summary>봉투를 암호화 페이로드로 봉인.</summary>
    public static EncryptedPayload Seal(RenderJobEnvelope env, byte[] key)
    {
        var body = JsonSerializer.Serialize(env, PromptFlowJsonContext.Default.RenderJobEnvelope);
        var cipher = Encrypt(Encoding.UTF8.GetBytes(body), key, out var iv);
        return new EncryptedPayload(env.JobId, Convert.ToBase64String(iv), Convert.ToBase64String(cipher), env.Checksum, SchemaVersion);
    }

    /// <summary>암호화 페이로드를 봉투로 복원(워커 측/검증용).</summary>
    public static RenderJobEnvelope? Unseal(EncryptedPayload payload, byte[] key)
    {
        try
        {
            var plain = Decrypt(Convert.FromBase64String(payload.Cipher), key, Convert.FromBase64String(payload.Iv));
            return JsonSerializer.Deserialize(Encoding.UTF8.GetString(plain), PromptFlowJsonContext.Default.RenderJobEnvelope);
        }
        catch { return null; }
    }

    /// <summary>워커 URL 유효성(http/https/ws/wss + 호스트).</summary>
    public static bool IsValidWorkerUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u)
           && (u.Scheme is "http" or "https" or "ws" or "wss") && !string.IsNullOrEmpty(u.Host);

    /// <summary>base + 경로 결합(중복 슬래시 정리).</summary>
    public static string BuildEndpoint(string baseUrl, string path)
        => (baseUrl ?? "").TrimEnd('/') + "/" + (path ?? "").TrimStart('/');

    /// <summary>http(s) → ws(s) 변환.</summary>
    public static string ToWebSocketUrl(string httpUrl)
    {
        var u = (httpUrl ?? "").Trim();
        if (u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return "wss://" + u.Substring(8);
        if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return "ws://" + u.Substring(7);
        return u;
    }

    // ══════════════════════ 오케스트레이션(HTTP 제출 → WS 진행 → 다운로드) ══════════════════════

    internal sealed record SubmitAccept(string JobId, string ProgressUrl, string ResultUrl);
    internal sealed record WsFrame(string Phase, double Percent, string Message);
    internal sealed record ResultManifest(string VideoUrl, string SpecUrl, string Message);

    /// <summary>
    /// 워커로 렌더 잡을 오프로딩하고 결과(60fps MP4 + PDF)를 <paramref name="outDir"/>에 저장한다.
    ///   워커 부재/오류/오프라인은 Graceful(Failed 반환 · 호출부가 로컬 폴백).
    /// </summary>
    public async Task<RenderJobResult> SubmitAsync(string workerUrl, StoryboardProject project, byte[] key, string outDir,
        IProgress<RenderProgress>? progress = null, IReadOnlyList<AssetRef>? assets = null, CancellationToken ct = default)
    {
        if (!IsValidWorkerUrl(workerUrl)) return new(false, "", null, null, "워커 URL 이 유효하지 않습니다.");
        var env = BuildEnvelope(project, assets);
        try
        {
            Directory.CreateDirectory(outDir);
            progress?.Report(new(RenderPhase.Uploading, 0.05, "암호화 전송 중"));
            var sealed_ = Seal(env, key);
            var submitBody = new StringContent(JsonSerializer.Serialize(sealed_, PromptFlowJsonContext.Default.EncryptedPayload), Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(BuildEndpoint(workerUrl, "api/render/submit"), submitBody, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return new(false, env.JobId, null, null, $"워커 제출 실패({(int)resp.StatusCode})");
            var accept = JsonSerializer.Deserialize(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false), PromptFlowJsonContext.Default.RemoteSubmitAccept);
            if (accept is null) return new(false, env.JobId, null, null, "워커 응답 파싱 실패");

            await ListenProgressAsync(accept.ProgressUrl, progress, ct).ConfigureAwait(false);

            progress?.Report(new(RenderPhase.Downloading, 0.9, "결과 다운로드 중"));
            var manResp = await _http.GetStringAsync(BuildEndpoint(workerUrl, accept.ResultUrl), ct).ConfigureAwait(false);
            var man = JsonSerializer.Deserialize(manResp, PromptFlowJsonContext.Default.RemoteResultManifest);
            if (man is null) return new(false, env.JobId, null, null, "결과 매니페스트 파싱 실패");

            var videoPath = await DownloadAsync(BuildEndpoint(workerUrl, man.VideoUrl), Path.Combine(outDir, "remote_render_60fps.mp4"), ct).ConfigureAwait(false);
            var specPath = string.IsNullOrWhiteSpace(man.SpecUrl) ? null
                : await DownloadAsync(BuildEndpoint(workerUrl, man.SpecUrl), Path.Combine(outDir, "remote_spec.pdf"), ct).ConfigureAwait(false);

            progress?.Report(new(RenderPhase.Done, 1.0, "완료"));
            return new(videoPath is not null, env.JobId, videoPath, specPath, videoPath is not null ? "리모트 렌더 완료" : "영상 다운로드 실패");
        }
        catch (OperationCanceledException) { return new(false, env.JobId, null, null, "취소됨"); }
        catch (Exception ex) { Program.Log("RemoteRender.Submit", ex); progress?.Report(new(RenderPhase.Failed, 0, ex.Message)); return new(false, env.JobId, null, null, "오프라인/워커 오류 — 로컬 렌더로 폴백: " + ex.Message); }
    }

    private async Task ListenProgressAsync(string progressUrl, IProgress<RenderProgress>? progress, CancellationToken ct)
    {
        try
        {
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(ToWebSocketUrl(progressUrl)), ct).ConfigureAwait(false);
            var buf = new byte[4096];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
                var text = Encoding.UTF8.GetString(buf, 0, result.Count);
                var frame = SafeParse(text);
                if (frame is not null)
                {
                    var phase = Enum.TryParse<RenderPhase>(frame.Phase, true, out var p) ? p : RenderPhase.Rendering;
                    progress?.Report(new(phase, Math.Clamp(frame.Percent, 0, 1), frame.Message));
                    if (phase is RenderPhase.Done or RenderPhase.Failed) break;
                }
            }
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", ct).ConfigureAwait(false); } catch { }
        }
        catch (Exception ex) { Program.Log("RemoteRender.Progress", ex); }
    }

    private static WsFrame? SafeParse(string text) { try { return JsonSerializer.Deserialize(text, PromptFlowJsonContext.Default.RemoteWsFrame); } catch { return null; } }

    private async Task<string?> DownloadAsync(string url, string outPath, CancellationToken ct)
    {
        try
        {
            var bytes = await _http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
            await File.WriteAllBytesAsync(outPath, bytes, ct).ConfigureAwait(false);
            return File.Exists(outPath) ? outPath : null;
        }
        catch (Exception ex) { Program.Log("RemoteRender.Download", ex); return null; }
    }
}
