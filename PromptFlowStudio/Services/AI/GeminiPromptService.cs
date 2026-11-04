using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Prompt;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Services.AI;

/// <summary>Gemini 강화 결과 — <see cref="UsedFallback"/>=true 면 로컬 룰셋으로 강화됨(키 없음/호출 실패).</summary>
public sealed record GeminiEnhanceOutcome(string Prompt, bool UsedFallback, string Message);

/// <summary>스토리보드 제미나이 디렉터 1컷 — 프롬프트 · 카메라 앵글 · 듀레이션(초).</summary>
public sealed record DirectorShot(int Index, string Title, string Prompt, string Camera, float DurationSec);

/// <summary>
/// [PromptFlowStudio] ✨ 제미나이 AI 스튜디오 엔진 — Wan 2.1 워크플로우 결합형 프롬프트 서비스(순수 BCL).
///   ▸ 듀레이션 인지 System Instruction(15s 앰비언트 → 루프 친화 미세 동세 지침 강제 주입).
///   ▸ Vision I2V 역추출(<see cref="AnalyzeReferenceImageAsync"/> · 이미지 base64 인라인).
///   ▸ 스토리보드 4컷 자동 디렉팅(<see cref="DirectStoryboardAsync"/>).
///   ▸ 키 없음/네트워크/HTTP/스키마 오류 → 예외 없이 <see cref="FallbackLocalEnhance"/>/<see cref="BuildLocalShots"/> 로 무중단.
///   ▸ 429/5xx 지수 백오프 1회 재시도, 호출 전체 <see cref="CallTimeout"/> 상한.
/// </summary>
public sealed class GeminiPromptService
{
    public const string Model = "gemini-2.0-flash";
    public const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/models/" + Model + ":generateContent";

    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(25);
    private const int MaxAttempts = 2;
    private const int BaseBackoffMs = 400;
    public const long MaxImageBytes = GeminiClient.MaxInlineBytes;

    public const string BaseInstruction =
        "You are a cinematic prompt engineer for the Wan 2.1 video model. " +
        "Rewrite the user's idea into ONE vivid English prompt (max 120 words) that keeps the original subject and action unchanged, " +
        "expanding it with volumetric lighting, detailed material textures, depth of field, lens character, atmosphere and color grading. " +
        "Output only the final prompt text — no quotes, no markdown, no explanations.";

    public const string AmbientInstruction =
        "Generate continuous subtle motion, slow camera drift, soft lighting shifts, zero abrupt cuts, " +
        "loop-friendly ambient aesthetic suitable for background wallpaper.";

    public const string I2VInstruction =
        "Analyze the attached reference image (color palette, main subject, composition, lighting). " +
        "Write ONE English Wan 2.1 image-to-video prompt (max 100 words) that describes the existing scene faithfully " +
        "and adds natural, physically plausible motion of the subject and environment plus a gentle camera move. " +
        "Do not introduce new subjects. Output only the prompt text.";

    public const string DirectorInstruction =
        "You are a film director. Split the one-line story into exactly 4 shots for the Wan 2.1 video model. " +
        "Respond with JSON only: {\"shots\":[{\"title\":string,\"prompt\":string,\"camera\":string,\"durationSec\":number}]} " +
        "with 4 items. Prompts are vivid English, max 80 words each; durationSec is 5.";

    /// <summary>로컬 폴백 시네마틱 키워드.</summary>
    public const string LocalCinematicTokens = "8K photorealistic, shallow depth of field, 35mm lens, smooth motion, soft volumetric lighting, professional color grading";
    /// <summary>15s 앰비언트 로컬 폴백 루프 키워드(핑퐁 루프 · 미세 동세).</summary>
    public const string LocalAmbientTokens = "seamless ping-pong loop, continuous subtle motion, slow camera drift, soft lighting shifts, no abrupt cuts, ambient wallpaper aesthetic";
    public const string LocalI2VTokens = "natural subtle subject motion, gentle parallax camera move, preserve original composition and color palette";

    public const string FallbackNotice = "로컬 사전 기반으로 강화되었습니다";

    private readonly HttpClient _http;
    private readonly Func<string?> _apiKey;

    public GeminiPromptService(HttpClient http, Func<string?> apiKeyProvider)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _apiKey = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));
    }

    public GeminiPromptService(HttpClient http, GeminiConfigService cfg) : this(http, () => cfg.ApiKey) { }

    /// <summary>"5s"/"10s"/"15s"/"15s-Ambient" 문자열 → 모드(미지정/미인식은 5s).</summary>
    public static VideoDurationMode ParseDuration(string? mode) => (mode ?? "").Trim().ToLowerInvariant() switch
    {
        "10s" => VideoDurationMode.Sec10_Dual,
        "15s" => VideoDurationMode.Sec15_Triple,
        "15s-ambient" or "ambient" => VideoDurationMode.Sec15_AmbientSlow,
        _ => VideoDurationMode.Sec5_Single,
    };

    /// <summary>듀레이션 인지 System Instruction.</summary>
    public static string BuildSystemInstruction(VideoDurationMode mode) => mode switch
    {
        VideoDurationMode.Sec15_AmbientSlow => BaseInstruction + " " + AmbientInstruction,
        VideoDurationMode.Sec10_Dual or VideoDurationMode.Sec15_Triple =>
            BaseInstruction + " The clip is chained from multiple 5-second shots: keep subject, wardrobe and lighting consistent with steady continuous motion.",
        _ => BaseInstruction,
    };

    /// <summary>로컬 룰셋 강화 — 원문 + 시네마틱 토큰(+앰비언트 루프 토큰). 이미 포함된 토큰은 중복 결합하지 않음.</summary>
    public static string FallbackLocalEnhance(string? prompt, VideoDurationMode mode = VideoDurationMode.Sec5_Single, bool imageToVideo = false)
    {
        var src = (prompt ?? "").Trim().TrimEnd(',', '.').TrimEnd();
        var sb = new StringBuilder(src.Length + 256).Append(src);
        if (imageToVideo) AppendOnce(sb, LocalI2VTokens);
        AppendOnce(sb, LocalCinematicTokens);
        if (mode == VideoDurationMode.Sec15_AmbientSlow) AppendOnce(sb, LocalAmbientTokens);
        var s = sb.ToString();
        return s.Length <= PromptSynthesizerService.MaxCharacterLimit ? s : s[..PromptSynthesizerService.MaxCharacterLimit].TrimEnd();

        static void AppendOnce(StringBuilder b, string tokens)
        {
            if (b.ToString().Contains(tokens, StringComparison.OrdinalIgnoreCase)) return;
            if (b.Length > 0) b.Append(", ");
            b.Append(tokens);
        }
    }

    /// <summary>프롬프트 강화(듀레이션 인지). 절대 throw 하지 않음 — 실패 시 로컬 폴백.</summary>
    public async Task<GeminiEnhanceOutcome> EnhancePromptAsync(string? basePrompt, VideoDurationMode mode,
        string? style = null, string? camera = null, CancellationToken ct = default)
    {
        var src = basePrompt ?? "";
        if (string.IsNullOrWhiteSpace(src)) return new(src, false, "입력이 비어 있습니다.");

        var user = new StringBuilder(src.Length + 96).Append("Prompt: ").Append(src.Trim());
        if (!string.IsNullOrWhiteSpace(camera)) user.Append("\nCamera motion: ").Append(camera.Trim());
        if (!string.IsNullOrWhiteSpace(style)) user.Append("\nVisual style: ").Append(style.Trim());

        var text = await CallAsync(BuildSystemInstruction(mode), user.ToString(), null, false, 0.6, ct).ConfigureAwait(false);
        var clean = Sanitize(text);
        if (clean is null) return new(FallbackLocalEnhance(src, mode), true, FallbackNotice);
        if (mode == VideoDurationMode.Sec15_AmbientSlow && !clean.Contains("loop", StringComparison.OrdinalIgnoreCase))
            clean = FallbackLocalEnhance(clean, mode);   // 모델이 루프 지침을 누락해도 앰비언트 키워드 보장
        return new(clean, false, "Gemini 강화 완료");
    }

    public Task<GeminiEnhanceOutcome> EnhancePromptAsync(string? basePrompt, string? durationMode, CancellationToken ct = default)
        => EnhancePromptAsync(basePrompt, ParseDuration(durationMode), null, null, ct);

    /// <summary>참조 이미지 → Wan 2.1 I2V 프롬프트 역생성(Vision). 실패 시 힌트 기반 로컬 폴백.</summary>
    public async Task<GeminiEnhanceOutcome> AnalyzeReferenceImageAsync(string imagePath, string? userHint,
        VideoDurationMode mode = VideoDurationMode.Sec5_Single, CancellationToken ct = default)
    {
        var hint = userHint ?? "";
        GeminiEnhanceOutcome Fallback(string msg) => new(FallbackLocalEnhance(hint, mode, imageToVideo: true), true, msg);

        if (string.IsNullOrWhiteSpace(imagePath)) return Fallback(FallbackNotice);
        byte[] bytes;
        try
        {
            var fi = new FileInfo(imagePath);
            if (!fi.Exists || fi.Length == 0 || fi.Length > MaxImageBytes) return Fallback(FallbackNotice + " (이미지 확인 불가)");
            if (!HasKey()) return Fallback(FallbackNotice);
            bytes = await File.ReadAllBytesAsync(imagePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is not OperationCanceledException) Program.Log("GeminiPromptService.ReadImage", ex);
            return Fallback(FallbackNotice);
        }

        var user = new StringBuilder(I2VInstruction.Length + hint.Length + 32).Append(I2VInstruction);
        if (!string.IsNullOrWhiteSpace(hint)) user.Append("\nUser hint: ").Append(hint.Trim());
        var sys = mode == VideoDurationMode.Sec15_AmbientSlow ? AmbientInstruction : "Output only the prompt text.";

        var text = await CallAsync(sys, user.ToString(), new GeminiMedia(MimeOf(imagePath), bytes), false, 0.4, ct).ConfigureAwait(false);
        var clean = Sanitize(text);
        return clean is null ? Fallback(FallbackNotice) : new(clean, false, "Gemini Vision I2V 분석 완료");
    }

    /// <summary>한 줄 스토리 → 정확히 4컷(프롬프트·카메라·듀레이션). 키 없음/실패/스키마 불일치 → 로컬 4컷 템플릿.</summary>
    public async Task<(IReadOnlyList<DirectorShot> Shots, bool UsedFallback)> DirectStoryboardAsync(string? story, CancellationToken ct = default)
    {
        var s = (story ?? "").Trim();
        if (s.Length == 0) return (Array.Empty<DirectorShot>(), true);
        var text = await CallAsync(DirectorInstruction, "Story: " + s, null, true, 0.5, ct).ConfigureAwait(false);
        var parsed = text is null ? null : ParseShots(text);
        return parsed is not null ? (parsed, false) : (BuildLocalShots(s), true);
    }

    /// <summary>로컬 4컷 디렉팅 템플릿(전경 → 클로즈업 → 터미널 → 앰비언트 엔딩).</summary>
    public static IReadOnlyList<DirectorShot> BuildLocalShots(string? story)
    {
        var s = (story ?? "").Trim();
        if (s.Length == 0) return Array.Empty<DirectorShot>();
        return new[]
        {
            new DirectorShot(1, "전체 전경", s + ", wide establishing shot of the whole scene, " + LocalCinematicTokens, "Crane Up / Wide", 5f),
            new DirectorShot(2, "디테일 클로즈업", s + ", close-up of key details and glowing screens, " + LocalCinematicTokens, "Smooth Pan / 35mm", 5f),
            new DirectorShot(3, "핵심 액션", s + ", decisive moment of action, rising tension, " + LocalCinematicTokens, "Dolly Zoom", 5f),
            new DirectorShot(4, "앰비언트 엔딩", s + ", dawn city view with ambient monitor glow, bokeh lights, " + LocalAmbientTokens, "Static / Bokeh", 5f),
        };
    }

    /// <summary>디렉터 JSON 스키마 즉시 검증 — 정확히 4컷·필수 필드 누락 시 null.</summary>
    public static IReadOnlyList<DirectorShot>? ParseShots(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(GeminiClient.StripFence(json));
            var root = doc.RootElement;
            JsonElement arr;
            if (root.ValueKind == JsonValueKind.Array) arr = root;
            else if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("shots", out arr) || arr.ValueKind != JsonValueKind.Array) return null;
            if (arr.GetArrayLength() != 4) return null;

            var list = new DirectorShot[4];
            var i = 0;
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) return null;
                var prompt = Str(e, "prompt");
                if (string.IsNullOrWhiteSpace(prompt)) return null;
                var dur = e.TryGetProperty("durationSec", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out var dv) && dv > 0 && dv <= 30 ? (float)dv : 5f;
                var title = Str(e, "title");
                var cam = Str(e, "camera");
                list[i] = new DirectorShot(i + 1, string.IsNullOrWhiteSpace(title) ? "컷 " + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : title!,
                    prompt!.Trim(), string.IsNullOrWhiteSpace(cam) ? "Static" : cam!.Trim(), dur);
                i++;
            }
            return list;
        }
        catch (JsonException) { return null; }

        static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private bool HasKey()
    {
        try { return !string.IsNullOrWhiteSpace(_apiKey()); } catch { return false; }
    }

    /// <summary>generateContent 호출(키 헤더 · 지수 백오프 · 타임아웃). 실패는 null.</summary>
    private async Task<string?> CallAsync(string system, string user, GeminiMedia? media, bool json, double temperature, CancellationToken ct)
    {
        string? key;
        try { key = _apiKey(); } catch { return null; }
        if (string.IsNullOrWhiteSpace(key)) return null;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(CallTimeout);

            var parts = new JsonArray(new JsonObject { ["text"] = user });
            if (media is not null)
                parts.Add(new JsonObject { ["inline_data"] = new JsonObject { ["mime_type"] = media.MimeType, ["data"] = Convert.ToBase64String(media.Data) } });
            var gen = new JsonObject { ["temperature"] = temperature };
            if (json) gen["responseMimeType"] = "application/json";
            var payload = new JsonObject
            {
                ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system }) },
                ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = parts }),
                ["generationConfig"] = gen,
            }.ToJsonString();

            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                if (attempt > 0) await Task.Delay(BaseBackoffMs << (attempt - 1), cts.Token).ConfigureAwait(false);
                using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                };
                req.Headers.TryAddWithoutValidation("x-goog-api-key", key);

                using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500) continue;
                if (!resp.IsSuccessStatusCode) return null;
                var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                return GeminiClient.ExtractText(body);
            }
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is not OperationCanceledException) Program.Log("GeminiPromptService.Call", ex);
            return null;
        }
    }

    private static string? Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = GeminiClient.StripFence(text).Trim().Trim('"', '\'', '`').Trim();
        if (s.Length == 0) return null;
        return s.Length <= PromptSynthesizerService.MaxCharacterLimit ? s : s[..PromptSynthesizerService.MaxCharacterLimit].TrimEnd();
    }

    private static string MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        _ => "image/jpeg",
    };
}
