using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>
/// 🎞 Multi-Variant Triplet Director — 같은 메모를 3가지 시네마틱 스타일(📷 스튜디오 화보 · 🎨 픽사풍 3D · 📼 16mm 레트로)로 동시 해석.
/// 🧠 Gemini 1회 호출(3.5초 예산) 우선 · 슬롯별 스키마 검증 · 누락/실패 슬롯은 로컬 템플릿 + 스타일 모디파이어 사전으로 결정론적 보강([부분 구제]).
/// </summary>
public sealed partial class PlutoCopilot
{
    public const string StudioVariantId = "studio";
    public const string PixarVariantId = "pixar";
    public const string RetroVariantId = "retro";

    /// <summary>🎞 트리플렛 3막 시간 구간(Hook 0-4s · Climax 4-11s · Emotional Smile 11-15s).</summary>
    public static readonly (int Start, int End)[] TripletActWindows = { (0, 4), (4, 11), (11, TotalSeconds) };

    private sealed record TripletStyle(string Id, string StyleName, string Look, string HookMod, string ClimaxMod, string EndingMod, string Camera, BabyBgmTrack Bgm);

    /// <summary>🎨 스타일 모디파이어 사전(고정 순서 — 스튜디오 · 픽사 · 레트로).</summary>
    private static readonly TripletStyle[] TripletStyles =
    {
        new(StudioVariantId, "📷 스튜디오 감성 화보",
            "Studio portrait photoshoot look, 85mm f/1.4 lens, creamy bokeh, natural rim light, emotional realism",
            "intimate 85mm portrait framing with soft catchlights in the eyes",
            "creamy f/1.4 bokeh background, natural rim light tracing the silhouette",
            "emotional realism, soft window light, a genuine heartfelt smile",
            "slow 85mm dolly-in with shallow depth of field → rack focus to the eyes", BabyBgmTrack.Acoustic),
        new(PixarVariantId, "🎨 픽사풍 3D 감성 애니",
            "Pixar-style 3D animated film look, whimsical vibrant 3D render, warm pastel lighting, big expressive eyes",
            "whimsical stylized 3D character with big curious expressive eyes",
            "vibrant squash-and-stretch animated motion with sparkles of joy, warm pastel lighting",
            "warm pastel glow, big expressive eyes, a heartwarming animated smile",
            "playful animated crane-down → bouncy 3D orbit around the subject", BabyBgmTrack.MusicBox),
        new(RetroVariantId, "📼 16mm 레트로 홈캠",
            "16mm retro film home video, 1990s vintage film grain, gentle vignette, faded warm colors, authentic mom-cam handheld",
            "authentic handheld mom-cam framing, 16mm film grain",
            "1990s vintage home-video feel with natural handheld shake",
            "gentle vignette, faded warm film colors, a nostalgic tender smile",
            "handheld mom-cam with natural shake → gentle manual zoom-in", BabyBgmTrack.Lullaby),
    };

    public const string LlmTripletInstruction =
        "You are Pluto, a warm short-form video director. " +
        "Turn this parent's real-life baby/pet/daily memory into THREE distinct cinematic style interpretations of the same 15-second 3-act short " +
        "(Act 1: Hook [0-4s], Act 2: Climax [4-11s], Act 3: Emotional Smile [11-15s]). " +
        "Styles: \"studio\" = studio portrait photoshoot (85mm f/1.4 creamy bokeh, natural rim light, emotional realism); " +
        "\"pixar\" = Pixar-style 3D animation (whimsical vibrant 3D look, warm pastel lighting, expressive eyes); " +
        "\"retro\" = 16mm retro home film (1990s vintage film grain, gentle vignette, authentic mom-cam handheld). " +
        "Respond with JSON only: {\"variants\":[{\"style\":\"studio\"|\"pixar\"|\"retro\",\"hookTitle\":string,\"act1\":string,\"act2\":string,\"act3\":string,\"camera\":string,\"bgm\":\"lullaby\"|\"musicbox\"|\"acoustic\"}]} - " +
        "exactly 3 variants, one per style; hookTitle: Korean viral hook title with one emoji (max 40 chars); " +
        "act1/act2/act3: vivid English text-to-video prompts tailored to that style's visual aesthetic; camera: English camera movement directive.";

    /// <summary>🎞 메모 → 3가지 스타일 변형(Gemini 우선 · 3.5초 내 로컬 폴백). 빈 메모는 <see cref="ArgumentException"/>.</summary>
    public Task<IReadOnlyList<PlutoScriptVariant>> DraftTripletVariantsAsync(string userMemo, CancellationToken cancellationToken = default)
        => DraftTripletVariantsAsync(userMemo, DateTime.Now, cancellationToken);

    /// <summary>🎞 <paramref name="now"/> 기준(계절·시간대 조명) 트리플렛 — 테스트 결정론용 오버로드.</summary>
    public async Task<IReadOnlyList<PlutoScriptVariant>> DraftTripletVariantsAsync(string userMemo, DateTime now, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userMemo)) throw new ArgumentException("memo required", nameof(userMemo));
        await EnsureContextLoadedAsync(cancellationToken).ConfigureAwait(false);
        var local = Draft(userMemo, now);   // 검증 + 폴백 기준선(마일스톤 · 장소 · 조명)
        var text = await PlutoLlmBridge.TryCompleteAsync(_llm, LlmTripletInstruction, BuildLlmScriptRequest(local, now), 0.9, cancellationToken).ConfigureAwait(false);
        return BuildTriplet(local, text);
    }

    /// <summary>⚡ 로컬 결정론 트리플렛(LLM 미사용).</summary>
    public IReadOnlyList<PlutoScriptVariant> DraftTripletLocal(string memo, DateTime? now = null) => BuildTriplet(Draft(memo, now), null);

    private PlutoScriptVariant[] BuildTriplet(PlutoCopilotScript local, string? llmJson)
    {
        var slots = new PlutoScriptVariant?[TripletStyles.Length];
        var anchor = IdentityAnchor(local.Category);
        var pacing = TopPattern(local.Category) is { } viral ? PlutoSocialRadarEngine.PatternDirective(viral) : null;
        if (llmJson is not null) FillFromLlm(llmJson, local, anchor, pacing, slots);
        var result = new PlutoScriptVariant[slots.Length];
        for (var i = 0; i < result.Length; i++)
        {
            var v = slots[i] ?? LocalVariant(TripletStyles[i], local, anchor, pacing);   // [부분 구제] 누락 슬롯만 로컬 보강
            result[i] = Director is null ? v : v with { Prompt = Optics(v.Prompt) };   // 🎬 디렉터 티어 광학 주입(LLM · 로컬 공통 · 멱등)
        }
        return result;
    }

    private static PlutoScriptVariant LocalVariant(TripletStyle s, PlutoCopilotScript local, string anchor, string? pacing)
    {
        var a1 = ActBase(local, 0) + ", " + s.HookMod;
        var a2 = ActBase(local, 1) + ", " + s.ClimaxMod;
        var a3 = ActBase(local, 2) + ", " + s.EndingMod;
        var hook = s.Id switch
        {
            PixarVariantId => local.MilestoneKo + ", 동화 속 주인공이 되다 🎨",
            RetroVariantId => "그 시절 홈캠으로 남긴 " + local.MilestoneKo + " 📼",
            _ => local.HookTitle,
        };
        return new PlutoScriptVariant(s.Id, s.StyleName, hook, ComposeTripletPrompt(s, a1, a2, a3, anchor, pacing), s.Camera,
            StockSuiteExporter.BabyBgmLabel(s.Bgm), a1, a2, a3) { BgmTrack = s.Bgm, MilestoneKo = local.MilestoneKo };
    }

    private static string ActBase(PlutoCopilotScript local, int i)
        => local.Acts.Count > i ? local.Acts[i].PromptEn.TrimEnd('.', ' ') : "baby sharing a sweet everyday moment";

    /// <summary>스타일 룩 선두 + 3막 타이밍 + SNS 페이싱 + 🍼 아이덴티티 앵커(멱등).</summary>
    private static string ComposeTripletPrompt(TripletStyle s, string a1, string a2, string a3, string anchor, string? pacing)
    {
        var sb = new StringBuilder(s.Look.Length + a1.Length + a2.Length + a3.Length + 192);
        sb.Append(s.Look).Append(". 15-second vertical short, 3-act story. ")
          .Append("Act 1 (0-4s) Hook: ").Append(a1.TrimEnd('.', ' ')).Append(". ")
          .Append("Act 2 (4-11s) Climax: ").Append(a2.TrimEnd('.', ' ')).Append(". ")
          .Append("Act 3 (11-15s) Emotional smile: ").Append(a3.TrimEnd('.', ' ')).Append('.');
        if (pacing is { Length: > 0 }) sb.Append(" SNS pacing: ").Append(pacing).Append('.');
        return PlutoIdentityVault.ApplyAnchor(sb.ToString(), anchor);
    }

    /// <summary>LLM 트리플렛 JSON 슬롯별 즉시 검증 — 3막 모두 8자 이상이어야 채택(미달 슬롯은 비워 로컬 보강).</summary>
    private static void FillFromLlm(string json, PlutoCopilotScript local, string anchor, string? pacing, PlutoScriptVariant?[] slots)
    {
        using var doc = PlutoLlmBridge.TryParse(json);
        if (doc is null) return;
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("variants", out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        foreach (var e in arr.EnumerateArray())
        {
            var a1 = PlutoLlmBridge.Str(e, "act1", 500);
            var a2 = PlutoLlmBridge.Str(e, "act2", 500);
            var a3 = PlutoLlmBridge.Str(e, "act3", 500);
            if (a1.Length < 8 || a2.Length < 8 || a3.Length < 8) continue;
            var idx = StyleSlot(PlutoLlmBridge.Str(e, "style", 40), slots);
            if (idx < 0) continue;
            var s = TripletStyles[idx];
            var hook = PlutoLlmBridge.Str(e, "hookTitle", 60);
            var camera = PlutoLlmBridge.Str(e, "camera", 200);
            var bgm = ParseBgm(PlutoLlmBridge.Str(e, "bgm", 40)) ?? s.Bgm;
            slots[idx] = new PlutoScriptVariant(s.Id, s.StyleName, hook.Length == 0 ? local.HookTitle : hook,
                ComposeTripletPrompt(s, a1, a2, a3, anchor, pacing), camera.Length == 0 ? s.Camera : camera,
                StockSuiteExporter.BabyBgmLabel(bgm), a1, a2, a3) { BgmTrack = bgm, MilestoneKo = local.MilestoneKo, UsedLlm = true };
        }
    }

    /// <summary>스타일 키 → 슬롯(이미 채워진 슬롯 중복 차단). 미인식 키는 첫 빈 슬롯.</summary>
    private static int StyleSlot(string style, PlutoScriptVariant?[] slots)
    {
        var idx = -1;
        if (style.Contains("studio", StringComparison.OrdinalIgnoreCase) || style.Contains("portrait", StringComparison.OrdinalIgnoreCase) || style.Contains("화보", StringComparison.Ordinal)) idx = 0;
        else if (style.Contains("pixar", StringComparison.OrdinalIgnoreCase) || style.Contains("3d", StringComparison.OrdinalIgnoreCase) || style.Contains("애니", StringComparison.Ordinal)) idx = 1;
        else if (style.Contains("retro", StringComparison.OrdinalIgnoreCase) || style.Contains("16mm", StringComparison.OrdinalIgnoreCase) || style.Contains("홈캠", StringComparison.Ordinal)) idx = 2;
        if (idx >= 0) return slots[idx] is null ? idx : -1;
        for (var i = 0; i < slots.Length; i++) if (slots[i] is null) return i;
        return -1;
    }
}
