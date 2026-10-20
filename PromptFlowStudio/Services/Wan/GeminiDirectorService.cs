using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Wan;

/// <summary>AI 감독의 컷 프레임 검수 결과. Ok=false 면 검수 불가(원본 유지). Score 0~100, HealedPrompt=보정 프롬프트.</summary>
public sealed record DirectorReview(bool Ok, int Score, string Reason, string HealedPrompt, string Message);

/// <summary>Gemini 멀티모달 기반 영상 품질 검수 & 프롬프트 보정 계약.</summary>
public interface IGeminiDirectorService
{
    /// <summary>Gemini API 키가 구성돼 자율 검수가 가능한지(appsettings Gemini:ApiKey).</summary>
    bool Enabled { get; }

    /// <summary>컷 프레임(.jpg)을 멀티모달로 평가해 QualityScore/평가/보정 프롬프트를 산출. 실패는 Graceful(Ok=false).</summary>
    Task<DirectorReview> ReviewFrameAsync(string framePath, string originalPrompt, bool hasAnchor, CancellationToken ct = default);
}

/// <summary>
/// [PromptFlowStudio] AI 감독 & 자율 품질 검수 — Gemini 네이티브 멀티모달 REST 연동(외부 NuGet 0, 순수 BCL HttpClient/System.Text.Json).
///   ▸ 컷 생성 후 1~3초 프레임(.jpg)을 base64 로 <c>gemini-2.5-flash:generateContent</c> 에 이미지+지시문으로 전달 →
///     프롬프트 부합도·캐릭터 일관성·시각적 기형을 평가해 <b>QualityScore(0~100)</b>·평가·보정 프롬프트를 JSON 으로 회수.
///   ▸ responseMimeType=application/json 으로 구조화 출력 강제(마크다운 펜스도 방어적으로 제거).
///   ▸ API 키(appsettings <c>Gemini:ApiKey</c>) 미입력이면 <see cref="Enabled"/>=false → 호출부(JobManager)가 자율검수를 건너뛰고 기존 파이프라인 100% 정상 동작(Graceful).
///   ⚠ 원격 AI 판정이라 네트워크/쿼터/안전차단 등 어떤 실패든 Ok=false 로 반환(원본 컷 유지).
/// </summary>
public sealed class GeminiDirectorService(GeminiConfigService cfg) : IGeminiDirectorService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private const string DefaultModel = "gemini-2.5-flash";

    // ⚠ 키/모델은 GeminiConfigService(동적 설정 · 관리자 UI 등록 · 재시작 불필요)에서 라이브로 읽는다.
    private string ApiKey => cfg.ApiKey;
    private string Model  => string.IsNullOrWhiteSpace(cfg.Model) ? DefaultModel : cfg.Model;

    /// <inheritdoc/>
    public bool Enabled => cfg.Enabled;

    /// <inheritdoc/>
    public async Task<DirectorReview> ReviewFrameAsync(string framePath, string originalPrompt, bool hasAnchor, CancellationToken ct = default)
    {
        if (!Enabled) return new(false, 0, "", "", "Gemini API 키가 없습니다.");
        if (string.IsNullOrWhiteSpace(framePath) || !File.Exists(framePath))
            return new(false, 0, "", "", "검수할 프레임 이미지가 없습니다.");

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(framePath, ct).ConfigureAwait(false);
            if (bytes.Length == 0) return new(false, 0, "", "", "프레임 이미지가 비어 있습니다.");
            var b64 = Convert.ToBase64String(bytes);

            var instruction = BuildInstruction(originalPrompt, hasAnchor);

            // ── 멀티모달 요청 본문(JsonObject 로 정확히 제어) ──
            var payload = new JsonObject
            {
                ["contents"] = new JsonArray(
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["parts"] = new JsonArray(
                            new JsonObject { ["text"] = instruction },
                            new JsonObject { ["inline_data"] = new JsonObject { ["mime_type"] = "image/jpeg", ["data"] = b64 } }
                        ),
                    }
                ),
                ["generationConfig"] = new JsonObject
                {
                    ["temperature"] = 0.4,
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
                return new(false, 0, "", "", $"Gemini 오류(HTTP {(int)resp.StatusCode}): {Tail(body)}");

            var text = ExtractText(body);
            if (text is null) return new(false, 0, "", "", "Gemini 응답에서 텍스트를 찾지 못했습니다(안전 차단 가능).");

            return ParseReview(text);
        }
        catch (OperationCanceledException) { return new(false, 0, "", "", "검수가 취소되었습니다."); }
        catch (Exception ex) { Program.Log("GeminiDirector.Review", ex); return new(false, 0, "", "", "검수 실패: " + ex.Message); }
    }

    private static string BuildInstruction(string prompt, bool hasAnchor)
    {
        var anchor = hasAnchor
            ? "이 영상은 등록된 캐릭터/스타일 앵커와 인물 외모·그림체가 일관되어야 합니다.\n"
            : "";
        return
            "당신은 숏폼 영상 AI 감독입니다. 아래 이미지는 텍스트→영상 AI(Wan 2.1)가 생성한 5초 컷의 한 프레임입니다.\n" +
            $"원본 프롬프트: \"{prompt}\"\n" +
            anchor +
            "이 프레임을 시각적으로 엄정하게 평가해 아래 JSON 형식으로만 응답하세요(다른 설명 금지):\n" +
            "{\"score\": <0~100 정수>, \"reason\": \"<한국어 한 줄 평가>\", \"healedPrompt\": \"<개선된 영문 프롬프트>\"}\n" +
            "평가 기준: 프롬프트 부합도, 인물/사물 일관성, 손·얼굴·사물의 기형/깨짐(artifact), 구도·조명·완성도.\n" +
            "score 가 낮으면(75 미만) healedPrompt 에 문제(기형·부합도 등)를 바로잡은 개선 영문 프롬프트를 제시하고, 높으면 원본과 동일하게 유지하세요.";
    }

    /// <summary>generateContent 응답에서 candidates[0].content.parts[*].text 를 결합해 추출.</summary>
    private static string? ExtractText(string body)
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

    /// <summary>모델이 낸 JSON({score,reason,healedPrompt})을 파싱. 마크다운 펜스 방어 제거.</summary>
    private static DirectorReview ParseReview(string text)
    {
        try
        {
            var json = StripFence(text);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var score = root.TryGetProperty("score", out var sc) && sc.TryGetInt32(out var si) ? Math.Clamp(si, 0, 100)
                        : (int.TryParse(StrOf(root, "score"), out var sp) ? Math.Clamp(sp, 0, 100) : 0);
            var reason = StrOf(root, "reason");
            var healed = StrOf(root, "healedPrompt");
            return new(true, score, reason, healed, "검수 완료");
        }
        catch (Exception ex)
        {
            Program.Log("GeminiDirector.Parse", ex);
            return new(false, 0, "", "", "검수 응답 해석 실패.");
        }
    }

    private static string StripFence(string s)
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

    private static string StrOf(JsonElement e, string prop)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString())
            : "";

    private static string Tail(string s, int n = 300) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s.Trim() : "…" + s[^n..].Trim());
}
