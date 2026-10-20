using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Media;   // VideoTransition/TransitionSafety

namespace PromptFlowStudio.Services.Wan;

/// <summary>두 컷 사이 트랜지션 산출 결과. Ok=false 면 산출 실패(전환 없음). Transition=검증된 xfade 스펙, Reason=설명.</summary>
public sealed record TransitionResult(bool Ok, VideoTransition? Transition, string Reason, string Message)
{
    public static TransitionResult Fail(string msg) => new(false, null, "", msg);
}

/// <summary>Gemini 시각 분석 기반 지능형 매치컷/스마트 트랜지션 계약.</summary>
public interface IGeminiTransitionDirectorService
{
    /// <summary>Gemini API 키가 구성돼 트랜지션 산출이 가능한지.</summary>
    bool Enabled { get; }

    /// <summary>
    /// 이전 컷 엔딩 프레임과 현재 컷 시작 프레임을 시각 분석해 두 컷의 피사체 위치·모션 흐름에 어울리는 xfade 전환과 지속시간을 산출.
    ///   실패는 Graceful(Ok=false → 전환 없이 concat).
    /// </summary>
    Task<TransitionResult> SuggestAsync(string prevEndFramePath, string currStartFramePath, CancellationToken ct = default);
}

/// <summary>
/// [PromptFlowStudio] 지능형 매치컷 & 스마트 트랜지션 제네레이터 — 순수 BCL(<see cref="GeminiClient"/> 재사용, 외부 NuGet 0).
///   ▸ 이전 컷(N-1) 엔딩 프레임 + 현재 컷(N) 시작 프레임 2장을 멀티모달로 전달 → 피사체 위치/모션 흐름 분석 →
///     최적 ffmpeg <c>xfade</c> 전환(fade/whip-pan=slide/zoom-in/blur 등) + 지속시간(ms)을 산출.
///   ▸ <b>보안</b>: 전환명은 <see cref="TransitionSafety.MapKind"/>로 고정 화이트리스트에 강제 매핑(밖이면 fade) — 비신뢰 출력이 필터에 그대로 들어가지 않음.
///   ▸ <see cref="VideoMergeService"/>가 컷 경계에 xfade 를 적용(오디오는 acrossfade 로 자동 동기 · 컷 간 연결감 극대화).
///   ▸ 키 없음/실패는 Ok=false → 전환 없이 기존 concat 병합(Graceful).
/// </summary>
public sealed class GeminiTransitionDirectorService(GeminiClient gemini) : IGeminiTransitionDirectorService
{
    private const int MinDurMs = 200;
    private const int MaxDurMs = 1500;

    /// <inheritdoc/>
    public bool Enabled => gemini.Enabled;

    /// <inheritdoc/>
    public async Task<TransitionResult> SuggestAsync(string prevEndFramePath, string currStartFramePath, CancellationToken ct = default)
    {
        if (!Enabled) return TransitionResult.Fail("Gemini API 키가 없습니다.");
        if (string.IsNullOrWhiteSpace(prevEndFramePath) || !File.Exists(prevEndFramePath) ||
            string.IsNullOrWhiteSpace(currStartFramePath) || !File.Exists(currStartFramePath))
            return TransitionResult.Fail("전환 분석용 프레임이 없습니다.");

        try
        {
            var prev = new FileInfo(prevEndFramePath);
            var curr = new FileInfo(currStartFramePath);
            if (prev.Length == 0 || curr.Length == 0 || prev.Length > GeminiClient.MaxInlineBytes || curr.Length > GeminiClient.MaxInlineBytes)
                return TransitionResult.Fail("프레임 크기가 분석 범위를 벗어납니다.");

            var media = new List<GeminiMedia>
            {
                new("image/jpeg", await File.ReadAllBytesAsync(prevEndFramePath, ct).ConfigureAwait(false)),
                new("image/jpeg", await File.ReadAllBytesAsync(currStartFramePath, ct).ConfigureAwait(false)),
            };

            var instruction = BuildInstruction();
            var res = await gemini.GenerateAsync(instruction, media, temperature: 0.35, ct: ct).ConfigureAwait(false);
            if (!res.Ok || res.Text is null) return TransitionResult.Fail(res.Message);

            return Parse(res.Text);
        }
        catch (OperationCanceledException) { return TransitionResult.Fail("산출이 취소되었습니다."); }
        catch (Exception ex) { Program.Log("GeminiTransition.Suggest", ex); return TransitionResult.Fail("산출 실패: " + ex.Message); }
    }

    private static string BuildInstruction()
    {
        return
            "당신은 영상 편집 감독입니다. 첫 번째 이미지는 이전 컷의 '마지막 프레임', 두 번째 이미지는 다음 컷의 '첫 프레임'입니다.\n" +
            "두 프레임의 피사체 위치·구도·색감·모션 흐름을 보고 두 컷을 가장 자연스럽게 잇는 전환을 고르세요.\n" +
            "아래 Strict JSON 으로만 응답하세요(다른 설명·마크다운 금지):\n" +
            "{\"kind\": \"<fade|whip-pan|zoom-in|blur|slide|circle 중 하나>\", \"durationMs\": <200~1500 정수>, \"reason\": \"<한국어 한 줄>\"}\n" +
            "규칙: 피사체가 같은 방향으로 이어지면 whip-pan/slide, 스케일이 바뀌면 zoom-in, 급변하면 blur/fade, 잔잔하면 fade. durationMs 는 전환 길이(빠른 컷=짧게).";
    }

    private static TransitionResult Parse(string text)
    {
        try
        {
            var json = GeminiClient.StripFence(text);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var kind = StrOf(root, "kind");
            var xfade = TransitionSafety.MapKind(kind);   // ⚠ 화이트리스트 강제 매핑(밖이면 fade)

            var dur = root.TryGetProperty("durationMs", out var d) && d.TryGetInt32(out var di) ? di
                      : (int.TryParse(StrOf(root, "durationMs"), out var dp) ? dp : 500);
            dur = Math.Clamp(dur, MinDurMs, MaxDurMs);

            var reason = StrOf(root, "reason");
            return new(true, new VideoTransition(xfade, dur), reason, "산출 완료");
        }
        catch (Exception ex) { Program.Log("GeminiTransition.Parse", ex); return TransitionResult.Fail("전환 응답 해석 실패."); }
    }

    private static string StrOf(JsonElement e, string prop)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString())
            : "";
}
