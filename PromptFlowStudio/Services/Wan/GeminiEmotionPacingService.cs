using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Media;   // EmotionPacing

namespace PromptFlowStudio.Services.Wan;

/// <summary>컷 1개의 감정 상태 분석 결과. Ok=false 면 분석 실패(기본 믹싱 유지). Tension 0~100, Mood=Cinematic/Suspense/Upbeat 등.</summary>
public sealed record EmotionCue(bool Ok, int Tension, string Mood, string Message)
{
    public static EmotionCue Fail(string msg) => new(false, 0, "", msg);
}

/// <summary>Gemini 멀티모달 감정선 분석 & 동적 BGM 오케스트레이션 계약.</summary>
public interface IGeminiEmotionPacingService
{
    /// <summary>Gemini API 키가 구성돼 감정 분석이 가능한지.</summary>
    bool Enabled { get; }

    /// <summary>컷의 나레이션 오디오(+선택 비디오)와 대사를 분석해 감정 상태(Tension/Mood)를 산출. 실패는 Graceful(Ok=false).</summary>
    Task<EmotionCue> AnalyzeAsync(string? narrationAudioPath, string? videoPath, string voiceText, CancellationToken ct = default);

    /// <summary>컷별 감정 큐 배열을 하나의 BGM 오케스트레이션 플랜(<see cref="EmotionPacing"/>)으로 종합(긴장 아크→덕킹 깊이/페이드).</summary>
    EmotionPacing BuildPacing(IReadOnlyList<EmotionCue> cues);
}

/// <summary>
/// [PromptFlowStudio] 멀티모달 감정선 & 동적 BGM 오케스트레이션 — 순수 BCL(<see cref="GeminiClient"/> 재사용, 외부 NuGet 0).
///   ▸ 컷별 나레이션 오디오 + (선택)비디오 + 대사를 Gemini 로 분석해 <b>Tension(0~100)</b>·<b>Mood</b> 산출 →
///     <see cref="BuildPacing"/> 가 긴장 아크를 종합해 BGM 덕킹 깊이·오디오 Fade In/Out 타임코드를 결정.
///   ▸ <see cref="VideoMergeService"/>가 이 플랜으로 <b>컷별 BGM 볼륨 엔벨로프</b>(긴장 씬 BGM 고조)와 <b>덕킹 강도</b>·<b>페이드</b>를 자동 조절.
///   ▸ 키 없음/실패는 Ok=false → 기존 고정 덕킹/믹싱으로 100% Graceful Fallback.
/// </summary>
public sealed class GeminiEmotionPacingService(GeminiClient gemini) : IGeminiEmotionPacingService
{
    /// <inheritdoc/>
    public bool Enabled => gemini.Enabled;

    /// <inheritdoc/>
    public async Task<EmotionCue> AnalyzeAsync(string? narrationAudioPath, string? videoPath, string voiceText, CancellationToken ct = default)
    {
        if (!Enabled) return EmotionCue.Fail("Gemini API 키가 없습니다.");

        try
        {
            var media = new List<GeminiMedia>();
            if (!string.IsNullOrWhiteSpace(narrationAudioPath) && File.Exists(narrationAudioPath))
            {
                var af = new FileInfo(narrationAudioPath);
                if (af.Length > 0 && af.Length <= GeminiClient.MaxInlineBytes)
                    media.Add(new GeminiMedia(AudioMime(narrationAudioPath), await File.ReadAllBytesAsync(narrationAudioPath, ct).ConfigureAwait(false)));
            }
            if (!string.IsNullOrWhiteSpace(videoPath) && File.Exists(videoPath))
            {
                var vf = new FileInfo(videoPath);
                if (vf.Length > 0 && vf.Length <= GeminiClient.MaxInlineBytes)
                    media.Add(new GeminiMedia("video/mp4", await File.ReadAllBytesAsync(videoPath, ct).ConfigureAwait(false)));
            }
            if (media.Count == 0 && string.IsNullOrWhiteSpace(voiceText))
                return EmotionCue.Fail("분석할 오디오/비디오/대사가 없습니다.");

            var instruction = BuildInstruction(voiceText);
            var res = await gemini.GenerateAsync(instruction, media.Count > 0 ? media : null, temperature: 0.3, ct: ct).ConfigureAwait(false);
            if (!res.Ok || res.Text is null) return EmotionCue.Fail(res.Message);

            return Parse(res.Text);
        }
        catch (OperationCanceledException) { return EmotionCue.Fail("분석이 취소되었습니다."); }
        catch (Exception ex) { Program.Log("GeminiEmotionPacing.Analyze", ex); return EmotionCue.Fail("분석 실패: " + ex.Message); }
    }

    /// <inheritdoc/>
    public EmotionPacing BuildPacing(IReadOnlyList<EmotionCue> cues)
    {
        // 컷별 긴장도(실패 컷은 중립 50). 아크 종합으로 덕킹 깊이/페이드 결정.
        var tensions = (cues ?? Array.Empty<EmotionCue>())
            .Select(c => c is { Ok: true } ? Math.Clamp(c.Tension, 0, 100) : 50).ToList();
        if (tensions.Count == 0) tensions.Add(50);

        var avg = tensions.Average();
        // 평균 긴장이 높을수록 덕킹은 약하게(BGM 존재감↑ · 긴장 고조). 0.35(강긴장)~0.9(잔잔).
        var duckingDepth = Math.Clamp(0.9 - (avg / 100.0) * 0.55, 0.35, 0.9);
        // 첫 컷이 잔잔할수록 페이드인 길게(최대 1.5s), 마지막이 고조되면 페이드아웃 짧게(임팩트).
        var firstCalm = 1.0 - tensions[0] / 100.0;
        var lastHigh = tensions[^1] / 100.0;
        var fadeInMs = (int)Math.Round(400 + firstCalm * 1100);       // 0.4~1.5s
        var fadeOutMs = (int)Math.Round(1400 - lastHigh * 900);       // 0.5~1.4s
        return new EmotionPacing(tensions, duckingDepth, fadeInMs, Math.Max(300, fadeOutMs));
    }

    private static string BuildInstruction(string voiceText)
    {
        var vt = string.IsNullOrWhiteSpace(voiceText) ? "(대사 없음)" : voiceText;
        return
            "당신은 영화 음악 감독입니다. 첨부한 컷(나레이션 오디오/영상)과 대사의 감정선을 분석하세요.\n" +
            $"대사: \"{vt}\"\n" +
            "아래 Strict JSON 으로만 응답하세요(다른 설명·마크다운 금지):\n" +
            "{\"tension\": <0~100 정수>, \"mood\": \"<Cinematic|Suspense|Upbeat|Calm|Sad|Epic|Romantic 중 하나>\"}\n" +
            "tension 은 이 컷의 긴장/고조 정도(0=잔잔, 100=최고조 클라이맥스). mood 는 지배적 분위기.";
    }

    private static EmotionCue Parse(string text)
    {
        try
        {
            var json = GeminiClient.StripFence(text);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tension = root.TryGetProperty("tension", out var t) && t.TryGetInt32(out var ti)
                ? Math.Clamp(ti, 0, 100)
                : (int.TryParse(StrOf(root, "tension"), out var tp) ? Math.Clamp(tp, 0, 100) : 50);
            var mood = StrOf(root, "mood");
            if (mood.Length == 0) mood = "Cinematic";
            return new(true, tension, mood, "분석 완료");
        }
        catch (Exception ex) { Program.Log("GeminiEmotionPacing.Parse", ex); return EmotionCue.Fail("분석 응답 해석 실패."); }
    }

    private static string AudioMime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".m4a" or ".aac" => "audio/aac",
        ".ogg" => "audio/ogg",
        ".flac" => "audio/flac",
        _ => "audio/wav",
    };

    private static string StrOf(JsonElement e, string prop)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString())
            : "";
}
