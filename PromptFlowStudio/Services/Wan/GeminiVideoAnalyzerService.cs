using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Wan;

/// <summary>네이티브 비디오(mp4 전체) 시간축 분석 결과. Ok=false 면 분석 불가(원본 유지). TemporalScore 0~100, MotionPrompt=모션 보정 프롬프트.</summary>
public sealed record VideoAnalysis(bool Ok, int TemporalScore, string Reason, string MotionPrompt, string Message);

/// <summary>Gemini 네이티브 MP4 통비디오 시각 분석 계약(단일 프레임이 아닌 영상 전체 시간축 추론).</summary>
public interface IGeminiVideoAnalyzerService
{
    /// <summary>Gemini API 키가 구성돼 비디오 분석이 가능한지.</summary>
    bool Enabled { get; }

    /// <summary>
    /// 생성된 컷 mp4 전체를 Gemini 에 인라인 전달해 <b>시간에 따른 모션 자연스러움·프레임 간 캐릭터 일관성·물리 법칙 위반</b>을
    ///   종합 분석하고 <see cref="VideoAnalysis.TemporalScore"/>(0~100) + 모션 보정 프롬프트를 산출. 실패는 Graceful(Ok=false).
    /// </summary>
    Task<VideoAnalysis> AnalyzeAsync(string videoPath, string originalPrompt, bool hasAnchor, CancellationToken ct = default);
}

/// <summary>
/// [PromptFlowStudio] Gemini 네이티브 MP4 통비디오 시각 분석 — 순수 BCL(<see cref="GeminiClient"/> 재사용, 외부 NuGet 0).
///   ▸ 단일 프레임 캡처(GeminiDirectorService)와 달리 <c>cut_*.mp4</c> 파일 전체를 <c>inline_data(video/mp4)</c> 로 전달 →
///     시간축 상의 <b>모션 자연스러움·프레임 간 일관성·물리 위반(손가락 꼬임, 텍스처 붕괴, 워핑)</b>을 종합 평가.
///   ▸ 출력(JSON): <c>{temporalScore, reason, motionPrompt}</c> — 낮으면 모션 안정화가 반영된 개선 영문 프롬프트(motionPrompt) 제시.
///   ▸ 인라인 크기 상한(<see cref="GeminiClient.MaxInlineBytes"/>) 초과 시 분석 생략(Ok=false) — 호출부는 기존 프레임 검수/파이프라인 유지(Graceful).
///   ▸ 키(appsettings <c>Gemini:ApiKey</c>) 없으면 <see cref="Enabled"/>=false → 완전 비활성(기존 렌더링 100% 정상).
/// </summary>
public sealed class GeminiVideoAnalyzerService(GeminiClient gemini) : IGeminiVideoAnalyzerService
{
    /// <inheritdoc/>
    public bool Enabled => gemini.Enabled;

    /// <inheritdoc/>
    public async Task<VideoAnalysis> AnalyzeAsync(string videoPath, string originalPrompt, bool hasAnchor, CancellationToken ct = default)
    {
        if (!Enabled) return new(false, 0, "", "", "Gemini API 키가 없습니다.");
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            return new(false, 0, "", "", "분석할 비디오 파일이 없습니다.");

        try
        {
            var fi = new FileInfo(videoPath);
            if (fi.Length == 0) return new(false, 0, "", "", "비디오 파일이 비어 있습니다.");
            if (fi.Length > GeminiClient.MaxInlineBytes)
                return new(false, 0, "", "", $"비디오가 인라인 분석 상한(16MB)을 초과합니다({fi.Length / (1024 * 1024)}MB) — 프레임 검수로 폴백.");

            var bytes = await File.ReadAllBytesAsync(videoPath, ct).ConfigureAwait(false);
            var media = new[] { new GeminiMedia("video/mp4", bytes) };
            var instruction = BuildInstruction(originalPrompt, hasAnchor);

            var res = await gemini.GenerateAsync(instruction, media, temperature: 0.35, ct: ct).ConfigureAwait(false);
            if (!res.Ok || res.Text is null) return new(false, 0, "", "", res.Message);

            return Parse(res.Text);
        }
        catch (OperationCanceledException) { return new(false, 0, "", "", "분석이 취소되었습니다."); }
        catch (Exception ex) { Program.Log("GeminiVideoAnalyzer.Analyze", ex); return new(false, 0, "", "", "분석 실패: " + ex.Message); }
    }

    private static string BuildInstruction(string prompt, bool hasAnchor)
    {
        var anchor = hasAnchor
            ? "이 영상은 등록된 캐릭터/스타일 앵커와 인물 외모·그림체가 프레임 전반에서 일관되어야 합니다.\n"
            : "";
        return
            "당신은 숏폼 영상 AI 감독입니다. 첨부한 mp4 는 텍스트→영상 AI(Wan 2.1)가 생성한 약 5초 컷 영상 전체입니다.\n" +
            $"원본 프롬프트: \"{prompt}\"\n" +
            anchor +
            "정지 프레임이 아니라 '시간에 따른 움직임'을 종합적으로 보고 아래 JSON 형식으로만 응답하세요(다른 설명·마크다운 금지):\n" +
            "{\"temporalScore\": <0~100 정수>, \"reason\": \"<한국어 한 줄 평가>\", \"motionPrompt\": \"<모션 안정화가 반영된 개선 영문 프롬프트>\"}\n" +
            "평가 기준: ①모션 자연스러움(끊김·급가속·역재생 느낌), ②프레임 간 캐릭터/사물 일관성(형태 유지), " +
            "③물리 법칙 위반(손가락 꼬임·관절 붕괴, 텍스처 뭉개짐·워핑, 배경 흐름 이상). " +
            "temporalScore 가 낮으면(75 미만) motionPrompt 에 위 결함을 바로잡는 개선 영문 프롬프트(예: smooth natural motion, consistent anatomy, stable texture)를 제시하고, 높으면 원본과 동일하게 유지하세요.";
    }

    /// <summary>모델 JSON({temporalScore,reason,motionPrompt}) 파싱. 펜스 제거 + 점수 클램프.</summary>
    private static VideoAnalysis Parse(string text)
    {
        try
        {
            var json = GeminiClient.StripFence(text);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var score = root.TryGetProperty("temporalScore", out var sc) && sc.TryGetInt32(out var si)
                ? Math.Clamp(si, 0, 100)
                : (int.TryParse(StrOf(root, "temporalScore"), out var sp) ? Math.Clamp(sp, 0, 100) : 0);
            var reason = StrOf(root, "reason");
            var motion = StrOf(root, "motionPrompt");
            return new(true, score, reason, motion, "분석 완료");
        }
        catch (Exception ex)
        {
            Program.Log("GeminiVideoAnalyzer.Parse", ex);
            return new(false, 0, "", "", "분석 응답 해석 실패.");
        }
    }

    private static string StrOf(JsonElement e, string prop)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString())
            : "";
}
