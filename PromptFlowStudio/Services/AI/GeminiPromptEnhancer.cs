using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Prompt;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Services.AI;

/// <summary>
/// [PromptFlowStudio] 🪄 AI 문장 자동 강화 — 사용자 프롬프트를 Gemini(<c>gemini-2.5-flash:generateContent</c>)로
///   Wan 2.1 시네마틱 구문(볼륨메트릭 라이팅 · 재질 텍스처 · 피사계 심도)으로 확장한다(순수 BCL HttpClient/System.Text.Json).
///   ▸ API 키는 호출 시점에 provider 에서 라이브로 읽음(하드코딩 금지 · GeminiConfigService 근원).
///   ▸ 키 없음/네트워크 단절/타임아웃/HTTP 오류/빈 응답/예외 → 예외 없이 원본 <c>basePrompt</c> 를 그대로 반환(Graceful Fallback).
///   ▸ 429/5xx 는 지수 백오프로 1회 재시도, 전체 호출은 <see cref="CallTimeout"/> 내에서 끝난다.
/// </summary>
public sealed class GeminiPromptEnhancer
{
    public const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent";

    /// <summary>호출 1회 전체 상한(재시도 포함) — 초과 시 원본 반환.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(20);

    private const int MaxAttempts = 2;
    private const int BaseBackoffMs = 400;

    public const string SystemPrompt =
        "You are a cinematic prompt engineer for the Wan 2.1 text-to-video model. " +
        "Rewrite the user's idea into ONE vivid English prompt (max 120 words) that keeps the original subject and action unchanged, " +
        "and expands it with volumetric lighting, physically detailed material textures, depth of field and lens character, " +
        "atmosphere and color grading. Respect the requested camera motion and visual style if given. " +
        "Output only the final prompt text — no quotes, no markdown, no explanations.";

    private readonly HttpClient _http;
    private readonly Func<string?> _apiKey;

    public GeminiPromptEnhancer(HttpClient http, Func<string?> apiKeyProvider)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _apiKey = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));
    }

    /// <summary>DI 편의 생성자 — 키 근원을 GeminiConfigService(관리자 UI 동적 설정)로 둔다.</summary>
    public GeminiPromptEnhancer(HttpClient http, GeminiConfigService cfg) : this(http, () => cfg.ApiKey) { }

    /// <summary>프롬프트를 시네마틱하게 강화. 실패 시 <paramref name="basePrompt"/> 를 그대로 반환(절대 throw 하지 않음 · 취소 포함).</summary>
    public async Task<string> EnhancePromptAsync(string basePrompt, string? style, string? camera, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(basePrompt)) return basePrompt;
        string? key;
        try { key = _apiKey(); } catch { return basePrompt; }
        if (string.IsNullOrWhiteSpace(key)) return basePrompt;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(CallTimeout);
            var payload = BuildPayload(basePrompt, style, camera).ToJsonString();

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
                if (!resp.IsSuccessStatusCode) return basePrompt;

                var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                return Sanitize(GeminiClient.ExtractText(body)) ?? basePrompt;
            }
            return basePrompt;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is not OperationCanceledException) Program.Log("GeminiPromptEnhancer.Enhance", ex);
            return basePrompt;
        }
    }

    private static JsonObject BuildPayload(string basePrompt, string? style, string? camera)
    {
        var sb = new StringBuilder(basePrompt.Length + 96).Append("Prompt: ").Append(basePrompt.Trim());
        if (!string.IsNullOrWhiteSpace(camera)) sb.Append("\nCamera motion: ").Append(camera.Trim());
        if (!string.IsNullOrWhiteSpace(style)) sb.Append("\nVisual style: ").Append(style.Trim());

        return new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = SystemPrompt }) },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = sb.ToString() }),
            }),
            ["generationConfig"] = new JsonObject { ["temperature"] = 0.6 },
        };
    }

    /// <summary>응답 정리(따옴표/펜스 제거 · 800자 예산 클립). 빈 결과는 null → 원본 폴백.</summary>
    private static string? Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = GeminiClient.StripFence(text).Trim().Trim('"', '\'', '`').Trim();
        if (s.Length == 0) return null;
        return s.Length <= PromptSynthesizerService.MaxCharacterLimit ? s : s[..PromptSynthesizerService.MaxCharacterLimit].TrimEnd();
    }
}
