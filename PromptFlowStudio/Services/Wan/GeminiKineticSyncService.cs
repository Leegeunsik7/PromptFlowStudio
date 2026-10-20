using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Media;   // KineticCaptionWord/KineticSfxCue

namespace PromptFlowStudio.Services.Wan;

/// <summary>키네틱 자막/SFX 타임코드 추출 결과(컷 1개 기준). Ok=false 면 추출 실패(정적 자막으로 폴백).</summary>
public sealed record KineticSyncResult(
    bool Ok, IReadOnlyList<KineticCaptionWord> Words, IReadOnlyList<KineticSfxCue> SfxTriggers, string Message)
{
    /// <summary>추출 실패/비활성 시의 빈 결과.</summary>
    public static KineticSyncResult Empty(string msg) =>
        new(false, Array.Empty<KineticCaptionWord>(), Array.Empty<KineticSfxCue>(), msg);
}

/// <summary>Gemini 멀티모달 기반 키네틱 타이포그래피/SFX 정밀 타임코드 추출 계약.</summary>
public interface IGeminiKineticSyncService
{
    /// <summary>Gemini API 키가 구성돼 키네틱 추출이 가능한지.</summary>
    bool Enabled { get; }

    /// <summary>
    /// 컷의 나레이션 오디오(+선택 비디오)와 대사 텍스트를 Gemini 에 전달해 틱톡/릴스 스타일 키네틱 타이포그래피 타임코드
    ///   (단어별 start_ms/end_ms/highlight)와 SFX 트리거(sfx_type/timestamp_ms)를 산출. 모든 타임코드는 <b>컷-로컬 밀리초</b>.
    /// </summary>
    /// <param name="narrationAudioPath">컷 나레이션 오디오(wav/mp3). 없으면 null(텍스트+비디오만).</param>
    /// <param name="videoPath">컷 비디오(mp4, 선택 · 인라인 상한 이하일 때만 첨부).</param>
    /// <param name="voiceText">컷 대사(정확한 단어 토큰 기준 제공 — 모델이 이 문구를 단어로 쪼개 타임코드 부여).</param>
    /// <param name="clipDurationMs">컷 길이(ms, 기본 5000) — 타임코드 클램프 범위.</param>
    Task<KineticSyncResult> AnalyzeAsync(
        string? narrationAudioPath, string? videoPath, string voiceText, int clipDurationMs = 5000, CancellationToken ct = default);
}

/// <summary>
/// [PromptFlowStudio] 키네틱 하이라이트 자막 & SFX 정밀 타임코드 추출 — 순수 BCL(<see cref="GeminiClient"/> 재사용, 외부 NuGet 0).
///   ▸ 나레이션 오디오 + (선택)비디오 + 대사문구를 멀티모달로 전달 → 단어별 밀리초 타임코드(<c>words[]</c>)와 전환 효과음(<c>sfxTriggers[]</c>)을
///     Strict JSON 으로 회수. <see cref="VideoMergeService"/>가 이를 <b>단어 단위 색상 전환 drawtext</b> + <b>밀리초 SFX 믹싱</b>으로 번인.
///   ▸ 타임코드는 컷-로컬(0~clipDurationMs)로 클램프·검증(start&lt;end, 범위 밖 제거) — 병합 파이프라인의 배속(atempo/setpts) 보정은 VideoMergeService 담당.
///   ▸ 키 없음/실패/빈 결과는 <see cref="KineticSyncResult.Empty"/> → 정적 자막(textfile drawtext)로 100% Graceful Fallback.
/// </summary>
public sealed class GeminiKineticSyncService(GeminiClient gemini) : IGeminiKineticSyncService
{
    private const int MaxWords = 80;   // 컷당 단어 상한(폭주 방지)
    private const int MaxSfx = 12;     // 컷당 SFX 상한

    /// <inheritdoc/>
    public bool Enabled => gemini.Enabled;

    /// <inheritdoc/>
    public async Task<KineticSyncResult> AnalyzeAsync(
        string? narrationAudioPath, string? videoPath, string voiceText, int clipDurationMs = 5000, CancellationToken ct = default)
    {
        if (!Enabled) return KineticSyncResult.Empty("Gemini API 키가 없습니다.");
        if (string.IsNullOrWhiteSpace(voiceText)) return KineticSyncResult.Empty("대사 텍스트가 없습니다.");

        var maxMs = clipDurationMs > 0 ? clipDurationMs : 5000;

        try
        {
            var media = new List<GeminiMedia>();

            // 나레이션 오디오(있으면) — 정확한 발화 타이밍의 핵심 신호.
            if (!string.IsNullOrWhiteSpace(narrationAudioPath) && File.Exists(narrationAudioPath))
            {
                var af = new FileInfo(narrationAudioPath);
                if (af.Length > 0 && af.Length <= GeminiClient.MaxInlineBytes)
                    media.Add(new GeminiMedia(AudioMime(narrationAudioPath), await File.ReadAllBytesAsync(narrationAudioPath, ct).ConfigureAwait(false)));
            }

            // 비디오(있고 상한 이하면) — 컷 전환/모션 피크로 SFX 시점 추론 보강.
            if (!string.IsNullOrWhiteSpace(videoPath) && File.Exists(videoPath))
            {
                var vf = new FileInfo(videoPath);
                if (vf.Length > 0 && vf.Length <= GeminiClient.MaxInlineBytes)
                    media.Add(new GeminiMedia("video/mp4", await File.ReadAllBytesAsync(videoPath, ct).ConfigureAwait(false)));
            }

            var instruction = BuildInstruction(voiceText, maxMs, media.Count > 0);
            var res = await gemini.GenerateAsync(instruction, media.Count > 0 ? media : null, temperature: 0.2, ct: ct).ConfigureAwait(false);
            if (!res.Ok || res.Text is null) return KineticSyncResult.Empty(res.Message);

            return Parse(res.Text, maxMs);
        }
        catch (OperationCanceledException) { return KineticSyncResult.Empty("추출이 취소되었습니다."); }
        catch (Exception ex) { Program.Log("GeminiKineticSync.Analyze", ex); return KineticSyncResult.Empty("추출 실패: " + ex.Message); }
    }

    private static string BuildInstruction(string voiceText, int maxMs, bool hasMedia)
    {
        var mediaNote = hasMedia
            ? "첨부한 나레이션 오디오(및 비디오)의 실제 발화 타이밍에 맞춰 각 단어의 start_ms/end_ms 를 정하세요.\n"
            : "오디오가 없으므로 자연스러운 한국어 낭독 속도(대략 초당 4~6음절)를 가정해 타임코드를 추정하세요.\n";
        return
            "당신은 틱톡/릴스 키네틱 타이포그래피 편집자입니다. 아래 대사를 단어 단위로 쪼개 화면에 '한 단어씩 팝업'되는 자막의 밀리초 타임코드를 만드세요.\n" +
            $"대사: \"{voiceText}\"\n" +
            mediaNote +
            $"모든 타임코드는 이 컷 내부 기준(0 ~ {maxMs} ms)입니다. 아래 Strict JSON 으로만 응답하세요(다른 설명·마크다운 금지):\n" +
            "{\"words\":[{\"word\":\"우주\",\"start_ms\":120,\"end_ms\":450,\"highlight\":true}]," +
            "\"sfxTriggers\":[{\"sfx_type\":\"whoosh\",\"timestamp_ms\":1200}]}\n" +
            "규칙: ①words 는 대사 어순 그대로, 시간은 겹치지 않게 오름차순. ②의미상 중요한 키워드는 highlight=true(색 강조). " +
            "③sfx_type 은 whoosh/pop/swoosh/ding/transition 중에서 컷 시작·문장 전환·강조 시점에만 최소한으로. ④값이 없으면 빈 배열.";
    }

    /// <summary>모델 JSON 파싱 → 컷-로컬 범위로 검증·클램프한 단어/SFX 목록.</summary>
    private static KineticSyncResult Parse(string text, int maxMs)
    {
        try
        {
            var json = GeminiClient.StripFence(text);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var words = new List<KineticCaptionWord>();
            if (root.TryGetProperty("words", out var wa) && wa.ValueKind == JsonValueKind.Array)
            {
                var prevEnd = 0;
                foreach (var w in wa.EnumerateArray())
                {
                    if (words.Count >= MaxWords) break;
                    var word = StrOf(w, "word").Trim();
                    if (word.Length == 0) continue;
                    var start = IntOf(w, "start_ms");
                    var end = IntOf(w, "end_ms");
                    // 검증: 범위 클램프 + start<end 보정(모델이 뒤집거나 0으로 줄 때).
                    start = Math.Clamp(start, 0, maxMs);
                    end = Math.Clamp(end, 0, maxMs);
                    if (end <= start) end = Math.Min(maxMs, start + 300);   // 최소 표시 300ms
                    if (start < prevEnd) start = prevEnd;                   // 오름차순 보정(겹침 방지)
                    if (end <= start) end = Math.Min(maxMs, start + 200);
                    if (start >= maxMs) break;
                    var hi = BoolOf(w, "highlight");
                    words.Add(new KineticCaptionWord(word, start, end, hi));
                    prevEnd = end;
                }
            }

            var sfx = new List<KineticSfxCue>();
            if (root.TryGetProperty("sfxTriggers", out var sa) && sa.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in sa.EnumerateArray())
                {
                    if (sfx.Count >= MaxSfx) break;
                    var type = StrOf(s, "sfx_type").Trim();
                    if (type.Length == 0) continue;
                    var ts = Math.Clamp(IntOf(s, "timestamp_ms"), 0, maxMs);
                    sfx.Add(new KineticSfxCue(type, ts));
                }
            }

            if (words.Count == 0 && sfx.Count == 0) return KineticSyncResult.Empty("키네틱 데이터가 비어 있습니다.");
            return new(true, words, sfx, "추출 완료");
        }
        catch (Exception ex)
        {
            Program.Log("GeminiKineticSync.Parse", ex);
            return KineticSyncResult.Empty("추출 응답 해석 실패.");
        }
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

    private static int IntOf(JsonElement e, string prop)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(prop, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return (int)Math.Round(d);
        return int.TryParse(v.ToString(), out var p) ? p : 0;
    }

    private static bool BoolOf(JsonElement e, string prop)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(prop, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(v.GetString(), out var b) && b,
            _ => false,
        };
    }
}
