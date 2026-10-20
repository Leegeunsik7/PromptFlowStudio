using System;
using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Account;   // LicenseService.DataAreaRoot()

namespace PromptFlowStudio.Services.Media;

/// <summary>Edge TTS 나레이션 합성 계약(무료 Microsoft Edge Online Speech).</summary>
public interface IVoiceoverService
{
    string TtsDir { get; }
    /// <summary>텍스트를 한국어 음성 MP3 로 합성해 저장하고 경로를 반환. 실패 시 null(Graceful).</summary>
    Task<string?> SynthesizeAsync(string text, string voice = "ko-KR-SunHiNeural", string? outPath = null, CancellationToken ct = default);
    /// <summary>v16.14.0 SSML prosody(rate 예 "-10%" · pitch 예 "+3Hz") 지정 합성. 기본값이면 <see cref="SynthesizeAsync"/> 와 동일 캐시.</summary>
    Task<string?> SynthesizeProsodyAsync(string text, string voice, string rate, string pitch, string? outPath = null, CancellationToken ct = default)
        => SynthesizeAsync(text, voice, outPath, ct);
}

/// <summary>
/// [PromptFlowStudio] Edge TTS 나레이션 — 외부 NuGet 0, 순수 BCL <see cref="ClientWebSocket"/> 로 Edge 온라인 음성 서비스 연동.
///   프로토콜: wss 연결 → speech.config(JSON) → ssml 전송 → 바이너리 오디오 청크 수신(헤더 2바이트 길이 프리픽스) → turn.end.
///   Sec-MS-GEC 토큰(SHA-256(ticks+TrustedToken))을 계산해 쿼리로 전달한다. 결과는 <c>%LocalAppData%/PromptFlowStudio/Media/_tts/</c>
///   에 콘텐츠 해시 파일명으로 캐시(동일 텍스트·음성 재사용). 어떤 실패든 null 반환 — 병합은 나레이션 없이 진행(Graceful).
///   ⚠ Edge TTS 는 비공식 엔드포인트라 토큰/프로토콜이 바뀔 수 있다(그 경우도 null 폴백).
/// </summary>
public sealed class VoiceoverService(IVideoMergeService merge) : IVoiceoverService
{
    private const string TrustedToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    private const string EdgeVersion = "1-130.0.2849.68";
    private const string WssBase = "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1";

    public string TtsDir { get; } = Path.Combine(LicenseService.DataAreaRoot(), "Media", "_tts");

    public Task<string?> SynthesizeAsync(string text, string voice = "ko-KR-SunHiNeural", string? outPath = null, CancellationToken ct = default)
        => SynthesizeProsodyAsync(text, voice, DefaultRate, DefaultPitch, outPath, ct);

    private const string DefaultRate = "+0%";
    private const string DefaultPitch = "+0Hz";

    public async Task<string?> SynthesizeProsodyAsync(string text, string voice, string rate, string pitch, string? outPath = null, CancellationToken ct = default)
    {
        var r = string.IsNullOrWhiteSpace(rate) ? DefaultRate : rate.Trim();
        var p = string.IsNullOrWhiteSpace(pitch) ? DefaultPitch : pitch.Trim();
        var t = (text ?? "").Trim();
        if (t.Length == 0) return null;
        var v = string.IsNullOrWhiteSpace(voice) ? "ko-KR-SunHiNeural" : voice.Trim();

        try
        {
            Directory.CreateDirectory(TtsDir);
            // 기본 prosody 는 기존 키 그대로(캐시 하위호환) · 변경 시 rate/pitch 포함.
            var key = r == DefaultRate && p == DefaultPitch ? v + "|" + t : v + "|" + r + "|" + p + "|" + t;
            var dest = outPath ?? Path.Combine(TtsDir, Hash(key) + ".mp3");
            // 캐시 히트 — 44.1kHz·스테레오 정규화본을 반환(생성 직후 규격화, 덕킹 믹싱 안전).
            if (File.Exists(dest) && new FileInfo(dest).Length > 0)
                return await AudioNormalizer.ToStereo44kAsync(merge.ResolvedFfmpegPath, dest, ct).ConfigureAwait(false);

            using var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpahaajmgckegomelbmkekmi");
            ws.Options.SetRequestHeader("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0");

            var url = $"{WssBase}?TrustedClientToken={TrustedToken}&Sec-MS-GEC={SecMsGec()}&Sec-MS-GEC-Version={EdgeVersion}";

            using (var connCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connCts.CancelAfter(TimeSpan.FromSeconds(15));
                await ws.ConnectAsync(new Uri(url), connCts.Token).ConfigureAwait(false);
            }

            await SendAsync(ws, ConfigMessage(), ct).ConfigureAwait(false);
            await SendAsync(ws, SsmlMessage(Guid.NewGuid().ToString("N"), v, t, r, p), ct).ConfigureAwait(false);

            var ok = await ReceiveToFileAsync(ws, dest, ct).ConfigureAwait(false);
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).ConfigureAwait(false); } catch { /* 무시 */ }

            if (!ok || !File.Exists(dest) || new FileInfo(dest).Length == 0) return null;
            // 생성 직후 44.1kHz·스테레오 정규화(-ar 44100 -ac 2). ffmpeg 없으면 원본(mp3) 반환(Graceful).
            return await AudioNormalizer.ToStereo44kAsync(merge.ResolvedFfmpegPath, dest, ct).ConfigureAwait(false);
        }
        catch (Exception ex) { Program.Log("Voiceover.Synthesize", ex); return null; }
    }

    // 바이너리 오디오 청크를 파일로 저장. text 프레임에 Path:turn.end 오면 종료.
    private static async Task<bool> ReceiveToFileAsync(ClientWebSocket ws, string dest, CancellationToken ct)
    {
        using var recvCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        recvCts.CancelAfter(TimeSpan.FromSeconds(60));
        var buffer = new byte[16384];

        using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        var gotAudio = false;

        while (true)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult res;
            do
            {
                res = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), recvCts.Token).ConfigureAwait(false);
                if (res.MessageType == WebSocketMessageType.Close) return gotAudio;
                ms.Write(buffer, 0, res.Count);
            }
            while (!res.EndOfMessage);

            var data = ms.ToArray();
            if (res.MessageType == WebSocketMessageType.Text)
            {
                if (Encoding.UTF8.GetString(data).Contains("Path:turn.end")) return gotAudio;
            }
            else // Binary: [2바이트 헤더길이][헤더텍스트][오디오]
            {
                if (data.Length < 2) continue;
                int headerLen = (data[0] << 8) | data[1];
                int audioStart = 2 + headerLen;
                if (audioStart < data.Length)
                {
                    await fs.WriteAsync(data.AsMemory(audioStart, data.Length - audioStart), ct).ConfigureAwait(false);
                    gotAudio = true;
                }
            }
        }
    }

    private static Task SendAsync(ClientWebSocket ws, string msg, CancellationToken ct)
        => ws.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, ct);

    private static string ConfigMessage() =>
        $"X-Timestamp:{Ts()}\r\nContent-Type:application/json; charset=utf-8\r\nPath:speech.config\r\n\r\n" +
        "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"},\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}";

    private static string SsmlMessage(string reqId, string voice, string text, string rate, string pitch)
    {
        var ssml = "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='ko-KR'>" +
                   $"<voice name='{voice}'><prosody pitch='{SecurityEscape(pitch)}' rate='{SecurityEscape(rate)}' volume='+0%'>{SecurityEscape(text)}</prosody></voice></speak>";
        return $"X-RequestId:{reqId}\r\nContent-Type:application/ssml+xml\r\nX-Timestamp:{Ts()}\r\nPath:ssml\r\n\r\n{ssml}";
    }

    private static string Ts() =>
        DateTime.UtcNow.ToString("ddd MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'", CultureInfo.InvariantCulture);

    /// <summary>Sec-MS-GEC = SHA-256((5분 단위로 내림한 1601 기준 100ns ticks) + TrustedToken) 대문자 HEX.</summary>
    private static string SecMsGec()
    {
        var winEpoch = new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var seconds = (long)(DateTime.UtcNow - winEpoch).TotalSeconds;
        seconds -= seconds % 300;                 // 5분(300초) 단위로 내림
        var ticks = seconds * 10_000_000L;        // 100ns 단위
        var raw = ticks.ToString(CultureInfo.InvariantCulture) + TrustedToken;
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(raw)));
    }

    private static string SecurityEscape(string s) => System.Security.SecurityElement.Escape(s) ?? s;

    private static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16].ToLowerInvariant();
}
