using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Account;   // LicenseService.DataAreaRoot()
using PromptFlowStudio.Services.Storyboard;

namespace PromptFlowStudio.Services.Media;

/// <summary>v16.14.0 동화 나레이션 감정 톤 — 구름(포근·잔잔) · 밝음(경쾌) · 동화(구연동화 낭독).</summary>
public enum NarrationTone { Cloud, Bright, FairyTale }

/// <summary>v16.14.0 TTS 공급자 — Auto 는 ElevenLabs(키 보유 시) → Edge → 로컬(Kokoro/F5) 순 폴백.</summary>
public enum TtsProvider { Auto, ElevenLabs, Edge, Local }

/// <summary>v16.14.0 나레이션 큐 — 음성 파일 · 최종 타임라인 시작(초) · 길이(초) · 대본(자막 원문) · 볼륨(0~2).</summary>
public sealed record VoiceCue(string Path, double AtSeconds, double DurationSeconds, string Text, double Volume = 1.0);

/// <summary>v16.14.0 톤 → 엔진별 운율 파라미터(Edge SSML rate/pitch · ElevenLabs voice_settings).</summary>
public readonly record struct NarrationProsody(string EdgeVoice, string Rate, string Pitch, double Speed,
    double Stability, double Similarity, double Style);

/// <summary>
/// [PromptFlowStudio] v16.14.0 🎙️ AI 동화 나레이션 TTS 래퍼 — 스토리보드 컷 대본(VoiceText, 없으면 Prompt 미사용) → MP3/WAV 나레이션.
///   ▸ 톤(<see cref="NarrationTone"/>) × 배속(0.5~2.0) → <see cref="Prosody"/> 로 Edge SSML rate/pitch · ElevenLabs speed/stability 를 일괄 산출.
///   ▸ ElevenLabs: 키는 환경변수 <c>ELEVENLABS_API_KEY</c>(하드코딩 금지) · voice 는 <c>ELEVENLABS_VOICE_ID</c> · 타임아웃 30초 · 429/5xx 지수 백오프 3회
///     · 응답 스키마 즉시 검증(audio/* + MP3/ID3 매직) — 실패는 Edge(BCL ClientWebSocket) → 로컬 TTS 로 폴백(Graceful null).
///   ▸ 결과는 <c>Media/_tts/</c> 콘텐츠 해시 캐시 · 길이는 WAV 헤더 실측, 그 외는 대본 길이 추정(<see cref="EstimateSeconds"/>).
///   ▸ <see cref="SynthesizeStoryAsync"/>: 컷 시작 + <see cref="LeadInSeconds"/> 에 배치 · 컷별 실패는 건너뜀([부분 구제]).
/// </summary>
public sealed class AiVoiceoverTtsService : IDisposable
{
    public const string ApiKeyEnv = "ELEVENLABS_API_KEY";
    public const string VoiceIdEnv = "ELEVENLABS_VOICE_ID";
    public const string DefaultElevenVoice = "21m00Tcm4TlvDq8ikWAM";
    public const string ElevenModel = "eleven_multilingual_v2";
    public const string EdgeVoice = "ko-KR-SunHiNeural";

    /// <summary>컷 시작 → 나레이션 시작 지연(초) — 장면 전환 직후 숨 고르기.</summary>
    public const double LeadInSeconds = 0.3;

    /// <summary>한국어 동화 낭독 기준 초당 글자 수(배속 1.0 · 공백 제외).</summary>
    public const double CharsPerSecond = 6.5;

    private const int MaxAttempts = 3;
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(30);

    private readonly IVoiceoverService? _edge;
    private readonly ILocalTtsService? _local;
    private readonly Func<string?> _apiKey;
    private readonly HttpClient _http;
    private readonly string _ttsDir;
    private readonly PromptFlowStudio.Services.Telemetry.ProductionCostTrackerService? _cost;

    public AiVoiceoverTtsService(IVoiceoverService? edge = null, ILocalTtsService? local = null,
        HttpMessageHandler? handler = null, Func<string?>? apiKey = null,
        PromptFlowStudio.Services.Telemetry.ProductionCostTrackerService? cost = null)
    {
        _cost = cost;
        _edge = edge;
        _local = local;
        _apiKey = apiKey ?? (() => Environment.GetEnvironmentVariable(ApiKeyEnv));
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = HttpTimeout;
        _ttsDir = edge?.TtsDir ?? Path.Combine(LicenseService.DataAreaRoot(), "Media", "_tts");
    }

    public bool HasElevenLabsKey => !string.IsNullOrWhiteSpace(_apiKey());
    public bool IsAvailable => HasElevenLabsKey || _edge is not null || _local is { IsAvailable: true };

    /// <summary>톤 × 배속 → 운율(순수 함수). 배속은 0.5~2.0 으로 클램프.</summary>
    public static NarrationProsody Prosody(NarrationTone tone, double speed = 1.0)
    {
        var s = Math.Clamp(double.IsFinite(speed) ? speed : 1.0, 0.5, 2.0);
        // 톤 기본 템포(동화는 또박또박 · 구름은 느리고 낮게 · 밝음은 경쾌하게)
        var (tempo, pitchHz, stability, style) = tone switch
        {
            NarrationTone.Cloud => (0.88, -2, 0.70, 0.20),
            NarrationTone.Bright => (1.06, 6, 0.40, 0.55),
            _ => (0.94, 3, 0.55, 0.45),
        };
        var eff = tempo * s;
        var ratePct = (int)Math.Round((eff - 1.0) * 100);
        var rate = (ratePct >= 0 ? "+" : "") + ratePct.ToString(CultureInfo.InvariantCulture) + "%";
        var pitch = (pitchHz >= 0 ? "+" : "") + pitchHz.ToString(CultureInfo.InvariantCulture) + "Hz";
        return new(EdgeVoice, rate, pitch, Math.Round(Math.Clamp(eff, 0.7, 1.2), 2), stability, 0.75, style);
    }

    /// <summary>대본 낭독 길이 추정(초) — 공백 제외 글자 수 / (CPS × 실효 배속) + 문장 쉼(0.25초/구두점).</summary>
    public static double EstimateSeconds(string? text, NarrationTone tone = NarrationTone.FairyTale, double speed = 1.0)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        int chars = 0, pauses = 0;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) continue;
            if (c is '.' or '!' or '?' or ',' or '…' or '~') pauses++;
            else chars++;
        }
        var tempo = Math.Clamp(double.IsFinite(speed) ? speed : 1.0, 0.5, 2.0) * (tone switch { NarrationTone.Cloud => 0.88, NarrationTone.Bright => 1.06, _ => 0.94 });
        return Math.Round(Math.Max(0.5, chars / (CharsPerSecond * tempo) + pauses * 0.25), 3);
    }

    /// <summary>ElevenLabs 요청 본문(System.Text.Json · 순수 함수).</summary>
    public static string BuildElevenLabsPayload(string text, NarrationProsody p)
        => JsonSerializer.Serialize(new
        {
            text,
            model_id = ElevenModel,
            voice_settings = new { stability = p.Stability, similarity_boost = p.Similarity, style = p.Style, use_speaker_boost = true, speed = p.Speed },
        });

    /// <summary>
    /// 대본 1건 합성 → 파일 경로(실패 null). Auto 는 ElevenLabs → Edge → 로컬 순. 예외 0.
    /// </summary>
    public async Task<string?> SynthesizeAsync(string text, NarrationTone tone = NarrationTone.FairyTale, double speed = 1.0,
        TtsProvider provider = TtsProvider.Auto, CancellationToken ct = default)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return null;
        var p = Prosody(tone, speed);
        try
        {
            if (provider is TtsProvider.Auto or TtsProvider.ElevenLabs && HasElevenLabsKey)
            {
                var eleven = await ElevenLabsAsync(t, p, ct).ConfigureAwait(false);
                if (eleven is not null || provider == TtsProvider.ElevenLabs) return eleven;
            }
            if (provider is TtsProvider.Auto or TtsProvider.Edge && _edge is not null)
            {
                var edge = await _edge.SynthesizeProsodyAsync(t, p.EdgeVoice, p.Rate, p.Pitch, null, ct).ConfigureAwait(false);
                if (edge is not null || provider == TtsProvider.Edge) return edge;
            }
            if (provider is TtsProvider.Auto or TtsProvider.Local && _local is { IsAvailable: true })
                return await _local.SynthesizeAsync(t, null, ct).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex) { Program.Log("AiVoiceover.Synthesize", ex); return null; }
    }

    /// <summary>
    /// v16.24.0 보이스 지정 합성(다국어 더빙) — ElevenLabs(지정/복제 voice_id · multilingual_v2) → Edge(<paramref name="p"/>.EdgeVoice · rate/pitch) 순.
    /// 로컬 TTS 는 언어 지정 불가라 제외. 실패 null · 예외 0.
    /// </summary>
    public async Task<string?> SynthesizeVoiceAsync(string text, NarrationProsody p, string? elevenVoiceId = null,
        TtsProvider provider = TtsProvider.Auto, CancellationToken ct = default)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return null;
        try
        {
            if (provider is TtsProvider.Auto or TtsProvider.ElevenLabs && HasElevenLabsKey)
            {
                var eleven = await ElevenLabsAsync(t, p, ct, elevenVoiceId).ConfigureAwait(false);
                if (eleven is not null || provider == TtsProvider.ElevenLabs) return eleven;
            }
            if (provider is TtsProvider.Auto or TtsProvider.Edge && _edge is not null)
                return await _edge.SynthesizeProsodyAsync(t, p.EdgeVoice, p.Rate, p.Pitch, null, ct).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex) { Program.Log("AiVoiceover.SynthesizeVoice", ex); return null; }
    }

    /// <summary>
    /// 스토리보드 컷 대본 → 나레이션 큐(최종 타임라인). 컷 길이를 넘는 음성은 다음 컷 시작까지 유지(자막도 동일 구간).
    /// </summary>
    public async Task<List<VoiceCue>> SynthesizeStoryAsync(IReadOnlyList<StoryboardScene> scenes, MixTimeline timeline,
        NarrationTone tone = NarrationTone.FairyTale, double speed = 1.0, TtsProvider provider = TtsProvider.Auto,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var cues = new List<VoiceCue>(scenes.Count);
        var n = Math.Min(scenes.Count, timeline.CutStarts.Count);
        for (var i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            var text = scenes[i].VoiceText;
            if (!string.IsNullOrWhiteSpace(text))
            {
                var at = timeline.CutStarts[i] + LeadInSeconds;
                var path = at < timeline.TotalSeconds ? await SynthesizeAsync(text, tone, speed, provider, ct).ConfigureAwait(false) : null;
                if (path is not null)
                {
                    var dur = ProbeSeconds(path) ?? EstimateSeconds(text, tone, speed);
                    cues.Add(new VoiceCue(path, Math.Round(at, 3), Math.Round(Math.Min(dur, timeline.TotalSeconds - at), 3), text.Trim()));
                }
            }
            progress?.Report((i + 1) / (double)n);
        }
        return cues;
    }

    /// <summary>WAV(RIFF) 헤더 실측 길이(초). WAV 가 아니거나 손상이면 null.</summary>
    public static double? ProbeSeconds(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> h = stackalloc byte[12];
            if (fs.Read(h) != 12 || !h[..4].SequenceEqual("RIFF"u8) || !h.Slice(8, 4).SequenceEqual("WAVE"u8)) return null;
            uint byteRate = 0;
            Span<byte> chunk = stackalloc byte[8];
            Span<byte> fmt = stackalloc byte[16];
            while (fs.Read(chunk) == 8)
            {
                var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
                if (chunk[..4].SequenceEqual("fmt "u8) && size >= 16)
                {
                    if (fs.Read(fmt) != 16) return null;
                    byteRate = BinaryPrimitives.ReadUInt32LittleEndian(fmt[8..]);
                    fs.Seek(size - 16 + (size & 1), SeekOrigin.Current);
                }
                else if (chunk[..4].SequenceEqual("data"u8))
                {
                    var len = Math.Min(size, (uint)Math.Max(0, fs.Length - fs.Position));
                    return byteRate == 0 ? null : Math.Round(len / (double)byteRate, 3);
                }
                else fs.Seek(size + (size & 1), SeekOrigin.Current);
            }
            return null;
        }
        catch { return null; }
    }

    // ElevenLabs REST — 429/5xx/타임아웃은 지수 백오프(0.5→1→2초) · 4xx 는 즉시 포기 · 스키마 검증 실패 null.
    private async Task<string?> ElevenLabsAsync(string text, NarrationProsody p, CancellationToken ct, string? voiceOverride = null)
    {
        var key = _apiKey();
        if (string.IsNullOrWhiteSpace(key)) return null;
        var voice = string.IsNullOrWhiteSpace(voiceOverride) ? Environment.GetEnvironmentVariable(VoiceIdEnv) : voiceOverride;
        if (string.IsNullOrWhiteSpace(voice)) voice = DefaultElevenVoice;
        var payload = BuildElevenLabsPayload(text, p);
        var dest = Path.Combine(_ttsDir, "el_" + Hash(voice + "|" + payload) + ".mp3");
        if (File.Exists(dest) && new FileInfo(dest).Length > 0) return dest;
        var url = "https://api.elevenlabs.io/v1/text-to-speech/" + Uri.EscapeDataString(voice) + "?output_format=mp3_44100_128";

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (attempt > 0) await Task.Delay(TimeSpan.FromMilliseconds(500 << (attempt - 1)), ct).ConfigureAwait(false);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.TryAddWithoutValidation("xi-api-key", key);
                req.Headers.TryAddWithoutValidation("Accept", "audio/mpeg");
                req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if (res.StatusCode == HttpStatusCode.TooManyRequests || (int)res.StatusCode >= 500) continue;
                if (!res.IsSuccessStatusCode) return null;
                var media = res.Content.Headers.ContentType?.MediaType;
                if (media is not null && !media.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return null;
                var bytes = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (!IsMp3(bytes)) return null;
                Directory.CreateDirectory(_ttsDir);
                await File.WriteAllBytesAsync(dest, bytes, ct).ConfigureAwait(false);
                _cost?.RecordElevenLabs(text.Length, voice);   // v16.18.0 문자 과금 기록(캐시 적중은 과금 0 → 기록 안 함)
                return dest;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { /* HttpClient 타임아웃 → 재시도 */ }
            catch (HttpRequestException) { /* 네트워크 오류 → 재시도 */ }
        }
        return null;
    }

    /// <summary>MP3 매직(ID3 태그 또는 MPEG 프레임 싱크 0xFFE).</summary>
    public static bool IsMp3(ReadOnlySpan<byte> b)
        => b.Length >= 3 && ((b[0] == (byte)'I' && b[1] == (byte)'D' && b[2] == (byte)'3') || (b[0] == 0xFF && (b[1] & 0xE0) == 0xE0));

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..24].ToLowerInvariant();

    public void Dispose() => _http.Dispose();
}
