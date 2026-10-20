using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Account;    // LicenseService.DataAreaRoot()
using PromptFlowStudio.Services.Storyboard; // StoryboardProject

namespace PromptFlowStudio.Services.Wan;

/// <summary>글로벌 다국어 번역 & SEO 결과. Ok=false 면 실패(기존 메타데이터 유지). SavedPath=영속화된 metadata_{lang}.json.</summary>
public sealed record LocalizedMetadata(
    bool Ok, string Lang, string Title, string Description, IReadOnlyList<string> Hashtags,
    string ThumbnailText, string? SavedPath, string Message)
{
    public static LocalizedMetadata Fail(string lang, string msg) =>
        new(false, lang, "", "", Array.Empty<string>(), "", null, msg);
}

/// <summary>지원 타깃 언어(코드, 표시명, 영문명).</summary>
public sealed record TargetLanguage(string Code, string Label, string EnglishName);

/// <summary>Gemini 기반 글로벌 다국어 번역 & SEO 자동화 계약.</summary>
public interface IGeminiLocalizationService
{
    /// <summary>Gemini API 키가 구성돼 번역/SEO 가 가능한지.</summary>
    bool Enabled { get; }

    /// <summary>지원 타깃 언어 목록(UI 셀렉트용).</summary>
    IReadOnlyList<TargetLanguage> Languages { get; }

    /// <summary>
    /// 스토리보드의 대본·메타데이터를 타깃 언어로 번역하고 해당 국가 숏폼 알고리즘에 최적화된 제목/썸네일 문구/바이럴 해시태그를 생성,
    ///   <c>metadata_{lang}.json</c> 로 영속화한다. 실패는 Graceful(Ok=false).
    /// </summary>
    Task<LocalizedMetadata> LocalizeAsync(StoryboardProject project, string targetLangCode, CancellationToken ct = default);
}

/// <summary>
/// [PromptFlowStudio] 글로벌 다국어 번역 & SEO 자동화 엔진 — 순수 BCL(<see cref="GeminiClient"/> 재사용, 외부 NuGet 0).
///   ▸ 완성 스토리보드의 대본(컷별 나레이션/프롬프트)과 제목을 타깃 언어(EN/JA/ES/ZH/…)로 자연스럽게 번역 →
///     국가별 유튜브 숏츠/틱톡/릴스 검색 알고리즘에 최적화된 <b>제목·썸네일 문구·바이럴 해시태그</b>를 생성.
///   ▸ 결과를 <c>%LocalAppData%/PromptFlowStudio/Projects/{id}/metadata_{lang}.json</c> 으로 영속화(양산 워크플로우 재사용).
///   ▸ 키 없음/실패는 Ok=false → 기존 메타데이터 유지(Graceful).
/// </summary>
public sealed class GeminiLocalizationService(GeminiClient gemini) : IGeminiLocalizationService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <inheritdoc/>
    public bool Enabled => gemini.Enabled;

    /// <inheritdoc/>
    public IReadOnlyList<TargetLanguage> Languages { get; } = new[]
    {
        new TargetLanguage("EN", "영어 (English)", "English"),
        new TargetLanguage("JA", "일본어 (日本語)", "Japanese"),
        new TargetLanguage("ES", "스페인어 (Español)", "Spanish"),
        new TargetLanguage("ZH", "중국어 간체 (简体中文)", "Simplified Chinese"),
        new TargetLanguage("FR", "프랑스어 (Français)", "French"),
        new TargetLanguage("DE", "독일어 (Deutsch)", "German"),
        new TargetLanguage("PT", "포르투갈어 (Português)", "Portuguese (Brazil)"),
        new TargetLanguage("ID", "인도네시아어 (Indonesia)", "Indonesian"),
        new TargetLanguage("VI", "베트남어 (Tiếng Việt)", "Vietnamese"),
        new TargetLanguage("HI", "힌디어 (हिन्दी)", "Hindi"),
    };

    /// <inheritdoc/>
    public async Task<LocalizedMetadata> LocalizeAsync(StoryboardProject project, string targetLangCode, CancellationToken ct = default)
    {
        var lang = (targetLangCode ?? "EN").Trim().ToUpperInvariant();
        if (!Enabled) return LocalizedMetadata.Fail(lang, "Gemini API 키가 없습니다.");
        if (project is null || project.Scenes.Count == 0) return LocalizedMetadata.Fail(lang, "번역할 스토리보드 내용이 없습니다.");

        var target = Languages.FirstOrDefault(l => l.Code == lang) ?? new TargetLanguage(lang, lang, lang);

        try
        {
            // 대본 취합(컷별 나레이션 우선, 없으면 프롬프트).
            var sb = new StringBuilder();
            for (var i = 0; i < project.Scenes.Count; i++)
            {
                var s = project.Scenes[i];
                var line = !string.IsNullOrWhiteSpace(s.VoiceText) ? s.VoiceText : s.Prompt;
                if (!string.IsNullOrWhiteSpace(line)) sb.Append(i + 1).Append(". ").AppendLine(line.Trim());
            }
            var script = sb.ToString().Trim();
            if (script.Length == 0) return LocalizedMetadata.Fail(lang, "번역할 대본/프롬프트가 없습니다.");

            var instruction = BuildInstruction(project.Title, script, target);
            var res = await gemini.GenerateAsync(instruction, null, temperature: 0.6, ct: ct).ConfigureAwait(false);
            if (!res.Ok || res.Text is null) return LocalizedMetadata.Fail(lang, res.Message);

            var parsed = Parse(res.Text, lang);
            if (!parsed.Ok) return parsed;

            var saved = Persist(project.Id, lang, parsed, script);
            return parsed with { SavedPath = saved, Message = saved is not null ? "번역·SEO 완료" : "번역 완료(저장 실패)" };
        }
        catch (OperationCanceledException) { return LocalizedMetadata.Fail(lang, "번역이 취소되었습니다."); }
        catch (Exception ex) { Program.Log("GeminiLocalization.Localize", ex); return LocalizedMetadata.Fail(lang, "번역 실패: " + ex.Message); }
    }

    private static string BuildInstruction(string title, string script, TargetLanguage target)
    {
        return
            $"당신은 글로벌 숏폼(YouTube Shorts/TikTok/Reels) 현지화 & SEO 전문가입니다. 아래 한국어 콘텐츠를 {target.EnglishName}(으)로 현지화하세요.\n" +
            $"원제목: \"{title}\"\n원본 대본:\n{script}\n\n" +
            $"모든 출력 텍스트는 반드시 {target.EnglishName} 로 작성합니다. 아래 Strict JSON 으로만 응답하세요(다른 설명·마크다운 금지):\n" +
            "{\"title\": \"<SEO 최적화 제목>\", \"description\": \"<설명 2~3문장>\", \"thumbnailText\": \"<썸네일 대형 문구 1~4단어>\", " +
            "\"hashtags\": [\"tag1\",\"tag2\"], \"translatedScript\": [\"line1\",\"line2\"]}\n" +
            $"규칙: ①직역이 아닌 {target.EnglishName} 원어민에게 자연스러운 현지화. ②title 은 해당 국가 숏폼 검색 알고리즘에 맞춘 후킹형(이모지 1~2개 허용). " +
            "③hashtags 는 해당 국가에서 실제 바이럴되는 태그 8~12개(# 없이 단어만, 언어 혼용 가능). ④translatedScript 는 컷 순서대로 번역한 나레이션.";
    }

    private static LocalizedMetadata Parse(string text, string lang)
    {
        try
        {
            var json = GeminiClient.StripFence(text);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var title = StrOf(root, "title");
            var desc = StrOf(root, "description");
            var thumb = StrOf(root, "thumbnailText");

            var tags = new List<string>();
            if (root.TryGetProperty("hashtags", out var ha) && ha.ValueKind == JsonValueKind.Array)
                foreach (var t in ha.EnumerateArray())
                {
                    var tag = (t.ValueKind == JsonValueKind.String ? t.GetString() : t.ToString()) ?? "";
                    tag = SanitizeTag(tag);
                    if (tag.Length > 0 && !tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) tags.Add(tag);
                    if (tags.Count >= 15) break;
                }

            if (title.Length == 0 && tags.Count == 0) return LocalizedMetadata.Fail(lang, "번역 응답이 비어 있습니다.");
            return new(true, lang, title, desc, tags, thumb, null, "번역 완료");
        }
        catch (Exception ex) { Program.Log("GeminiLocalization.Parse", ex); return LocalizedMetadata.Fail(lang, "번역 응답 해석 실패."); }
    }

    /// <summary>metadata_{lang}.json 으로 영속화. 실패 시 null(번역 결과 자체는 유효).</summary>
    private string? Persist(string projectId, string lang, LocalizedMetadata meta, string sourceScript)
    {
        try
        {
            var dir = Path.Combine(LicenseService.DataAreaRoot(), "Projects", SafeSeg(projectId));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"metadata_{lang.ToLowerInvariant()}.json");
            var node = new JsonObject
            {
                ["lang"] = lang,
                ["title"] = meta.Title,
                ["description"] = meta.Description,
                ["thumbnailText"] = meta.ThumbnailText,
                ["hashtags"] = new JsonArray(meta.Hashtags.Select(h => (JsonNode)h!).ToArray()),
                ["hashtagLine"] = string.Join(" ", meta.Hashtags.Select(h => "#" + h)),
                ["generatedAtUtc"] = DateTime.UtcNow.ToString("o"),
            };
            File.WriteAllText(path, node.ToJsonString(JsonOpts), new UTF8Encoding(false));
            return path;
        }
        catch (Exception ex) { Program.Log("GeminiLocalization.Persist", ex); return null; }
    }

    /// <summary>해시태그 정규화 — 문자/숫자만(다국어 letter 포함), 공백/# 제거.</summary>
    private static string SanitizeTag(string raw)
    {
        var sb = new StringBuilder();
        foreach (var c in (raw ?? "").Trim())
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    private static string SafeSeg(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "project";
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim();
    }

    private static string StrOf(JsonElement e, string prop)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString())
            : "";
}
