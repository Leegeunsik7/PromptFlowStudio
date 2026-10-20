using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Media;   // FfmpegFilterSafety

namespace PromptFlowStudio.Services.Wan;

/// <summary>AI 컬러 그레이딩 결과. Ok=false 면 산출 실패(원본 톤 유지). FilterGraph=검증된 ffmpeg vf 체인, Mood=분위기 라벨.</summary>
public sealed record ColorGrade(bool Ok, string FilterGraph, string Mood, string Message)
{
    public static ColorGrade None(string msg) => new(false, "", "", msg);
}

/// <summary>Gemini 기반 시네마틱 컬러 그레이딩 필터 그래프 생성 계약.</summary>
public interface IGeminiColorGradingService
{
    /// <summary>Gemini API 키가 구성돼 컬러 그레이딩 산출이 가능한지.</summary>
    bool Enabled { get; }

    /// <summary>
    /// 컷 프롬프트(+선택 프레임)의 분위기를 분석해 어울리는 FFmpeg 색보정 필터 체인을 산출한다.
    ///   ⚠ 결과는 <see cref="FfmpegFilterSafety.SanitizeColorGrade"/>로 검증된 안전한 문자열만 반환(위험하면 Ok=false).
    /// </summary>
    Task<ColorGrade> SuggestAsync(string prompt, string? framePath = null, CancellationToken ct = default);
}

/// <summary>
/// [PromptFlowStudio] AI 시네마틱 컬러 그레이딩 — 순수 BCL(<see cref="GeminiClient"/> 재사용, 외부 NuGet 0).
///   ▸ 컷 프롬프트/분위기를 Gemini 로 분석 → 분위기에 맞는 FFmpeg 색보정 필터 체인(예: 사이버펑크 ⇒
///     <c>eq=contrast=1.2:saturation=1.4,colorbalance=rs=0.1:bs=0.2,vignette=PI/4</c>)을 산출해 <see cref="VideoMergeService"/> 의 컷 필터에 삽입.
///   ▸ <b>보안 핵심</b>: Gemini 출력은 비신뢰 → <see cref="FfmpegFilterSafety.SanitizeColorGrade"/>(화이트리스트 필터명 + 필터그래프 탈출/외부리소스 차단)로
///     검증한 문자열만 반환. 검증 실패(위험·비화이트리스트)면 Ok=false → 그레이딩 생략(원본 톤 유지 · Graceful).
///   ▸ 키(appsettings <c>Gemini:ApiKey</c>) 없으면 <see cref="Enabled"/>=false → 완전 비활성.
/// </summary>
public sealed class GeminiColorGradingService(GeminiClient gemini) : IGeminiColorGradingService
{
    /// <inheritdoc/>
    public bool Enabled => gemini.Enabled;

    /// <inheritdoc/>
    public async Task<ColorGrade> SuggestAsync(string prompt, string? framePath = null, CancellationToken ct = default)
    {
        if (!Enabled) return ColorGrade.None("Gemini API 키가 없습니다.");
        if (string.IsNullOrWhiteSpace(prompt) && string.IsNullOrWhiteSpace(framePath))
            return ColorGrade.None("분석할 프롬프트/프레임이 없습니다.");

        try
        {
            GeminiMedia[]? media = null;
            if (!string.IsNullOrWhiteSpace(framePath) && File.Exists(framePath))
            {
                var fi = new FileInfo(framePath);
                if (fi.Length > 0 && fi.Length <= GeminiClient.MaxInlineBytes)
                    media = new[] { new GeminiMedia("image/jpeg", await File.ReadAllBytesAsync(framePath, ct).ConfigureAwait(false)) };
            }

            var instruction = BuildInstruction(prompt);
            var res = await gemini.GenerateAsync(instruction, media, temperature: 0.4, ct: ct).ConfigureAwait(false);
            if (!res.Ok || res.Text is null) return ColorGrade.None(res.Message);

            return Parse(res.Text);
        }
        catch (OperationCanceledException) { return ColorGrade.None("산출이 취소되었습니다."); }
        catch (Exception ex) { Program.Log("GeminiColorGrading.Suggest", ex); return ColorGrade.None("산출 실패: " + ex.Message); }
    }

    private static string BuildInstruction(string prompt)
    {
        return
            "당신은 영화 색보정(컬러 그레이딩) 전문가입니다. 아래 장면의 분위기에 딱 맞는 FFmpeg 색보정 필터 체인을 설계하세요.\n" +
            $"장면 프롬프트: \"{prompt}\"\n" +
            "아래 Strict JSON 으로만 응답하세요(다른 설명·마크다운 금지):\n" +
            "{\"mood\": \"<분위기 한 단어(예: 사이버펑크/따뜻한/차가운/빈티지)>\", \"filter\": \"<ffmpeg 필터 체인>\"}\n" +
            "필터 규칙(엄수): ①오직 색보정/후처리 필터만 사용 — eq, colorbalance, colorchannelmixer, hue, curves, colortemperature, vibrance, vignette, unsharp, gblur, colorlevels, colorcontrast 중에서만. " +
            "②필터는 콤마(,)로 연결하고 각 필터는 name=key=val:key=val 형식. ③절대 금지: 세미콜론(;), 대괄호([]), 따옴표, 역슬래시, 외부 파일/이미지/자막 로드(movie/subtitles/overlay 등). " +
            "예시(사이버펑크): eq=contrast=1.2:saturation=1.4,colorbalance=rs=0.1:bs=0.2,vignette=PI/4";
    }

    /// <summary>모델 JSON({mood,filter}) 파싱 후 <b>안전 검증</b>. 위험/비화이트리스트 필터는 Ok=false.</summary>
    private static ColorGrade Parse(string text)
    {
        try
        {
            var json = GeminiClient.StripFence(text);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var mood = StrOf(root, "mood");
            var rawFilter = StrOf(root, "filter");

            var safe = FfmpegFilterSafety.SanitizeColorGrade(rawFilter);
            if (safe is null)
                return new(false, "", mood, "필터가 안전 검증을 통과하지 못했습니다(그레이딩 생략).");

            return new(true, safe, mood, "그레이딩 산출 완료");
        }
        catch (Exception ex)
        {
            Program.Log("GeminiColorGrading.Parse", ex);
            return ColorGrade.None("그레이딩 응답 해석 실패.");
        }
    }

    private static string StrOf(JsonElement e, string prop)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString())
            : "";
}
