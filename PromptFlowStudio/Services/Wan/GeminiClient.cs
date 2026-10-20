using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Wan;

/// <summary>Gemini 멀티모달 호출에 실어 보낼 인라인 미디어(오디오/비디오/이미지). Base64 는 GeminiClient 내부에서 인코딩.</summary>
public sealed record GeminiMedia(string MimeType, byte[] Data);

/// <summary>Gemini 응답 결과. Ok=false 면 실패(원인 Message) — 호출부는 Graceful Fallback.</summary>
public sealed record GeminiResult(bool Ok, string? Text, string Message);

/// <summary>
/// [PromptFlowStudio] Gemini 네이티브 멀티모달 REST 공용 클라이언트 — 외부 NuGet 0(순수 BCL HttpClient/System.Text.Json).
///   ▸ 텍스트 지시문 + 인라인 미디어(이미지/오디오/<b>비디오 mp4</b>) 파트를 <c>gemini-2.5-flash:generateContent</c> 에 전달하고
///     <c>responseMimeType=application/json</c> 으로 구조화 출력을 회수(마크다운 펜스 방어 제거는 <see cref="StripFence"/>).
///   ▸ <see cref="GeminiVideoAnalyzerService"/>/<see cref="GeminiKineticSyncService"/>/<see cref="GeminiColorGradingService"/> 공유 —
///     각 서비스가 HTTP/JSON 을 재구현하지 않도록 한 곳에 모음(GeminiDirectorService 는 기존 검증 흐름 보존 위해 별도 유지).
///   ▸ API 키(appsettings <c>Gemini:ApiKey</c>) 미입력이면 <see cref="Enabled"/>=false → 호출부가 기능을 건너뛰고 기존 파이프라인 100% 정상(Graceful).
///   ⚠ 비디오 인라인 업로드는 요청 크기 제한(약 20MB)이 있어 호출부가 파일 크기를 선검사한다.
/// </summary>
public sealed class GeminiClient(GeminiConfigService cfg, PromptFlowStudio.Services.Telemetry.ProductionCostTrackerService? cost = null)
{
    // 비디오/오디오 추론은 이미지보다 오래 걸릴 수 있어 넉넉한 타임아웃(실제 취소는 CancellationToken).
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    // ⚠ 키/모델은 GeminiConfigService(동적 설정 · 관리자 UI 등록 · 재시작 불필요)에서 라이브로 읽는다.
    private string ApiKey => cfg.ApiKey;
    private string Model  => string.IsNullOrWhiteSpace(cfg.Model) ? "gemini-2.5-flash" : cfg.Model;

    /// <summary>Gemini API 키가 구성돼 호출 가능한지(GeminiConfigService 스냅샷).</summary>
    public bool Enabled => cfg.Enabled;

    /// <summary>인라인 미디어 요청의 안전 상한(바이트) — Base64 팽창(×4/3) 고려해 원본 16MB 캡(약 21MB 요청).</summary>
    public const long MaxInlineBytes = 16L * 1024 * 1024;

    /// <summary>
    /// 텍스트 지시문 + 선택적 인라인 미디어를 멀티모달로 전달하고 모델 텍스트(보통 JSON)를 회수한다.
    ///   실패(키 없음/네트워크/HTTP 오류/안전 차단/빈 응답)는 예외 없이 <see cref="GeminiResult"/>(Ok=false)로 반환(Graceful).
    /// </summary>
    /// <param name="instruction">모델 지시문(프롬프트).</param>
    /// <param name="media">인라인 미디어 목록(없으면 텍스트 전용). 각 항목은 MaxInlineBytes 이하여야 안전.</param>
    /// <param name="temperature">생성 온도(0~1). 구조화 추론은 낮게(0.2~0.4).</param>
    public async Task<GeminiResult> GenerateAsync(
        string instruction, IReadOnlyList<GeminiMedia>? media = null, double temperature = 0.3, CancellationToken ct = default)
    {
        if (!Enabled) return new(false, null, "Gemini API 키가 없습니다.");
        if (string.IsNullOrWhiteSpace(instruction)) return new(false, null, "지시문이 비어 있습니다.");

        try
        {
            var parts = new List<JsonNode?> { new JsonObject { ["text"] = instruction } };
            if (media is not null)
            {
                foreach (var m in media)
                {
                    if (m is null || m.Data is null || m.Data.Length == 0) continue;
                    if (m.Data.LongLength > MaxInlineBytes)
                        return new(false, null, $"미디어가 인라인 상한(16MB)을 초과합니다({m.Data.LongLength / (1024 * 1024)}MB).");
                    parts.Add(new JsonObject
                    {
                        ["inline_data"] = new JsonObject
                        {
                            ["mime_type"] = m.MimeType,
                            ["data"] = Convert.ToBase64String(m.Data),
                        },
                    });
                }
            }

            var payload = new JsonObject
            {
                ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(parts.ToArray()) }),
                ["generationConfig"] = new JsonObject
                {
                    ["temperature"] = Math.Clamp(temperature, 0.0, 1.0),
                    ["responseMimeType"] = "application/json",
                },
            };

            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(Model)}:generateContent?key={Uri.EscapeDataString(ApiKey)}";
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            };

            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new(false, null, $"Gemini 오류(HTTP {(int)resp.StatusCode}): {Tail(body)}");

            if (cost is not null && TryReadUsage(body, out var inTok, out var outTok))
                cost.RecordGemini(Model, inTok, outTok);   // v16.18.0 토큰 과금 기록(차단 대상 아님 — 영상 생성만 예산 차단)
            var text = ExtractText(body);
            return text is null
                ? new(false, null, "Gemini 응답에서 텍스트를 찾지 못했습니다(안전 차단 가능).")
                : new(true, text, "OK");
        }
        catch (OperationCanceledException) { return new(false, null, "요청이 취소되었습니다."); }
        catch (Exception ex) { Program.Log("GeminiClient.Generate", ex); return new(false, null, "Gemini 호출 실패: " + ex.Message); }
    }

    /// <summary>v16.18.0 generateContent 응답 usageMetadata → (입력 · 출력+추론) 토큰. 없으면 false.</summary>
    public static bool TryReadUsage(string body, out long input, out long output)
    {
        input = 0; output = 0;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("usageMetadata", out var u) || u.ValueKind != JsonValueKind.Object) return false;
            input = Long(u, "promptTokenCount");
            output = Long(u, "candidatesTokenCount") + Long(u, "thoughtsTokenCount");
            return input > 0 || output > 0;
        }
        catch (JsonException) { return false; }

        static long Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? Math.Max(0, n) : 0;
    }

    /// <summary>generateContent 응답에서 candidates[0].content.parts[*].text 를 결합해 추출(없으면 null).</summary>
    public static string? ExtractText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("candidates", out var cands) || cands.ValueKind != JsonValueKind.Array || cands.GetArrayLength() == 0)
                return null;
            var first = cands[0];
            if (!first.TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
                return null;
            var sb = new StringBuilder();
            foreach (var p in parts.EnumerateArray())
                if (p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    sb.Append(t.GetString());
            var s = sb.ToString().Trim();
            return s.Length > 0 ? s : null;
        }
        catch { return null; }
    }

    /// <summary>모델이 코드펜스(```json …```)로 감싼 경우 제거해 순수 JSON 만 남긴다.</summary>
    public static string StripFence(string s)
    {
        var t = s.Trim();
        if (t.StartsWith("```"))
        {
            var nl = t.IndexOf('\n');
            if (nl >= 0) t = t[(nl + 1)..];
            if (t.EndsWith("```")) t = t[..^3];
        }
        return t.Trim();
    }

    private static string Tail(string s, int n = 300) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s.Trim() : "…" + s[^n..].Trim());
}
