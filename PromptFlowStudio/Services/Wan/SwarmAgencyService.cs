using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Wan;

/// <summary>스웜 토론 1발언(페르소나 + 내용) — UI 트랜스크립트 표시용.</summary>
public sealed record SwarmMessage(string Role, string Content);

/// <summary>스웜이 확정한 컷 1개(영문 프롬프트 + 한국어 나레이션).</summary>
public sealed record SwarmCut(string Prompt, string VoiceText);

/// <summary>Multi-Agent Swarm 최종 기획안. Ok=false 면 실패(수동 기획 폴백).</summary>
public sealed record SwarmPlan(
    bool Ok, string Title, string ViralHook, IReadOnlyList<SwarmCut> Cuts, IReadOnlyList<SwarmMessage> Transcript, string Message)
{
    public static SwarmPlan Fail(string msg, IReadOnlyList<SwarmMessage>? t = null) =>
        new(false, "", "", Array.Empty<SwarmCut>(), t ?? Array.Empty<SwarmMessage>(), msg);
}

/// <summary>Multi-Agent Swarm(다중 에이전트 자율 토론 기획) 계약.</summary>
public interface ISwarmAgencyService
{
    /// <summary>Gemini 키가 구성돼 스웜 기획이 가능한지.</summary>
    bool Enabled { get; }

    /// <summary>
    /// 3개 Gemini 페르소나(Writer/Director/Marketer)가 자율 토론하며 숏츠 기획을 다듬는다.
    ///   Writer(초안) → Director(시각/매치컷 비판) → Marketer(바이럴 훅 비판) → Writer(수정/확정) 최대 <paramref name="maxLoops"/>회.
    ///   각 발언은 <paramref name="progress"/> 로 실시간 방송(UI 트랜스크립트). 실패는 Graceful(Ok=false).
    /// </summary>
    Task<SwarmPlan> RunAsync(string topic, int maxLoops = 3, IProgress<SwarmMessage>? progress = null, CancellationToken ct = default);
}

/// <summary>
/// [PromptFlowStudio] Multi-Agent Swarm — 순수 BCL(<see cref="GeminiClient"/> 재사용, 외부 NuGet 0).
///   ▸ 단일 호출을 넘어 <b>Writer·Director·Marketer</b> 3 페르소나가 순차 상호작용(Crew)하며 기획을 반복 개선:
///     Writer 초안 → Director 시각/매치컷 비판 → Marketer 바이럴 훅 비판 → Writer 수정(확정 시 조기 종료) — 최대 3 Loop.
///   ▸ 각 단계는 Gemini 텍스트 호출. 트랜스크립트를 <see cref="IProgress{T}"/> 로 스트리밍해 UI 가 토론 과정을 보여준다.
///   ▸ 최종 Writer JSON({title,hook,cuts[]})을 파싱해 스토리보드에 그대로 적용(사용자 Approve 또는 Auto-Approve).
///   ▸ 키 없음/실패는 Ok=false → 수동 기획(위저드)로 100% Graceful Fallback.
/// </summary>
public sealed class SwarmAgencyService(GeminiClient gemini) : ISwarmAgencyService
{
    /// <inheritdoc/>
    public bool Enabled => gemini.Enabled;

    /// <inheritdoc/>
    public async Task<SwarmPlan> RunAsync(string topic, int maxLoops = 3, IProgress<SwarmMessage>? progress = null, CancellationToken ct = default)
    {
        if (!Enabled) return SwarmPlan.Fail("Gemini API 키가 없습니다.");
        if (string.IsNullOrWhiteSpace(topic)) return SwarmPlan.Fail("기획 주제를 입력하세요.");

        var loops = Math.Clamp(maxLoops, 1, 3);
        var transcript = new List<SwarmMessage>();

        void Emit(string role, string content)
        {
            var m = new SwarmMessage(role, content);
            transcript.Add(m);
            progress?.Report(m);
        }

        try
        {
            // ① Writer 초안
            Emit("Writer", "주제를 받아 숏츠 초안을 작성합니다…");
            var draftJson = await CallAsync(WriterDraftPrompt(topic), ct).ConfigureAwait(false);
            if (draftJson is null) return SwarmPlan.Fail("Writer 초안 생성 실패.", transcript);
            var plan = ParsePlan(draftJson);
            if (plan is null) return SwarmPlan.Fail("Writer 초안 해석 실패.", transcript);
            Emit("Writer", $"초안 «{plan.Value.Title}» · {plan.Value.Cuts.Count}컷 · 훅: {plan.Value.Hook}");

            for (var loop = 1; loop <= loops; loop++)
            {
                ct.ThrowIfCancellationRequested();

                // ② Director 비판
                var dir = await CallAsync(DirectorPrompt(draftJson!), ct).ConfigureAwait(false);
                var dirCrit = ExtractField(dir, "critique");
                Emit("Director", string.IsNullOrWhiteSpace(dirCrit) ? "(의견 없음)" : dirCrit);

                // ③ Marketer 비판
                var mkt = await CallAsync(MarketerPrompt(draftJson!), ct).ConfigureAwait(false);
                var mktCrit = ExtractField(mkt, "critique");
                var newHook = ExtractField(mkt, "hook");
                Emit("Marketer", (string.IsNullOrWhiteSpace(mktCrit) ? "(의견 없음)" : mktCrit) + (string.IsNullOrWhiteSpace(newHook) ? "" : $" · 제안 훅: {newHook}"));

                // ④ Writer 수정/확정
                var revised = await CallAsync(WriterRevisePrompt(draftJson!, dirCrit, mktCrit, newHook), ct).ConfigureAwait(false);
                if (revised is null) { Emit("Writer", "수정 실패 — 직전 안을 확정합니다."); break; }
                var confirmed = ExtractBool(revised, "confirmed");
                var revisedPlan = ParsePlan(revised);
                if (revisedPlan is not null) { plan = revisedPlan; draftJson = revised; }

                if (confirmed || loop == loops)
                {
                    Emit("Writer", confirmed ? $"수정 완료 — 확정합니다(«{plan.Value.Title}»)." : $"최대 반복 도달 — 현재 안 확정(«{plan.Value.Title}»).");
                    break;
                }
                Emit("Writer", $"피드백 반영 수정 (Loop {loop}) — «{plan.Value.Title}»");
            }

            var p = plan.Value;
            if (p.Cuts.Count == 0) return SwarmPlan.Fail("확정된 컷이 없습니다.", transcript);
            return new(true, p.Title, p.Hook, p.Cuts, transcript, "기획 확정");
        }
        catch (OperationCanceledException) { return SwarmPlan.Fail("기획이 취소되었습니다.", transcript); }
        catch (Exception ex) { Program.Log("SwarmAgency.Run", ex); return SwarmPlan.Fail("기획 실패: " + ex.Message, transcript); }
    }

    private async Task<string?> CallAsync(string prompt, CancellationToken ct)
    {
        var res = await gemini.GenerateAsync(prompt, null, temperature: 0.7, ct: ct).ConfigureAwait(false);
        return res.Ok ? res.Text : null;
    }

    // ── 페르소나 프롬프트 ──
    private static string WriterDraftPrompt(string topic) =>
        "당신은 바이럴 숏츠 전문 작가(Writer)입니다. 아래 주제로 5컷짜리 숏폼 영상 대본 초안을 작성하세요.\n" +
        $"주제: \"{topic}\"\n" +
        "Strict JSON 으로만 응답(다른 설명·마크다운 금지):\n" +
        "{\"title\":\"<한국어 제목>\",\"hook\":\"<3초 안에 시선을 잡는 오프닝 훅>\",\"cuts\":[{\"prompt\":\"<영문 영상 프롬프트>\",\"voiceText\":\"<한국어 나레이션>\"}]}\n" +
        "cuts 는 정확히 5개. prompt 는 Wan 2.1 영상 생성용 영문(시네마틱 묘사), voiceText 는 자연스러운 한국어 나레이션.";

    private static string DirectorPrompt(string json) =>
        "당신은 영상 감독(Director)입니다. 아래 숏츠 대본 JSON 의 시각적 완성도·컷 전환(매치컷)·페이싱을 비판적으로 검토하세요.\n" +
        $"대본: {json}\n" +
        "Strict JSON 으로만: {\"critique\":\"<개선점 2~3가지 한국어>\"}";

    private static string MarketerPrompt(string json) =>
        "당신은 바이럴 마케터(Marketer)입니다. 아래 대본의 오프닝 훅·시청 지속·CTA·검색성을 비판적으로 검토하세요.\n" +
        $"대본: {json}\n" +
        "Strict JSON 으로만: {\"critique\":\"<개선점 2~3가지 한국어>\",\"hook\":\"<더 강력한 오프닝 훅 제안>\"}";

    private static string WriterRevisePrompt(string json, string dirCrit, string mktCrit, string newHook) =>
        "당신은 작가(Writer)입니다. 아래 당신의 대본과 감독/마케터의 피드백을 반영해 대본을 수정하세요. 이미 훌륭하면 그대로 두세요.\n" +
        $"현재 대본: {json}\n" +
        $"감독 피드백: {dirCrit}\n마케터 피드백: {mktCrit}\n마케터 제안 훅: {newHook}\n" +
        "Strict JSON 으로만(초안과 동일 스키마 + confirmed):\n" +
        "{\"title\":\"...\",\"hook\":\"...\",\"cuts\":[{\"prompt\":\"...\",\"voiceText\":\"...\"}],\"confirmed\":<true=더 고칠 것 없음/false=추가 반복 필요>}";

    // ── 파싱 ──
    private readonly struct ParsedPlan
    {
        public ParsedPlan(string title, string hook, IReadOnlyList<SwarmCut> cuts) { Title = title; Hook = hook; Cuts = cuts; }
        public string Title { get; }
        public string Hook { get; }
        public IReadOnlyList<SwarmCut> Cuts { get; }
    }

    private static ParsedPlan? ParsePlan(string? text)
    {
        if (text is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(GeminiClient.StripFence(text));
            var root = doc.RootElement;
            var title = StrOf(root, "title");
            var hook = StrOf(root, "hook");
            var cuts = new List<SwarmCut>();
            if (root.TryGetProperty("cuts", out var ca) && ca.ValueKind == JsonValueKind.Array)
                foreach (var c in ca.EnumerateArray())
                {
                    var prompt = StrOf(c, "prompt").Trim();
                    var vt = StrOf(c, "voiceText").Trim();
                    if (prompt.Length == 0 && vt.Length == 0) continue;
                    cuts.Add(new SwarmCut(prompt, vt));
                    if (cuts.Count >= 8) break;
                }
            if (cuts.Count == 0 && title.Length == 0) return null;
            return new ParsedPlan(title.Length == 0 ? "무제 숏츠" : title, hook, cuts);
        }
        catch { return null; }
    }

    private static string ExtractField(string? text, string field)
    {
        if (text is null) return "";
        try { using var doc = JsonDocument.Parse(GeminiClient.StripFence(text)); return StrOf(doc.RootElement, field); }
        catch { return ""; }
    }

    private static bool ExtractBool(string? text, string field)
    {
        if (text is null) return false;
        try
        {
            using var doc = JsonDocument.Parse(GeminiClient.StripFence(text));
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(field, out var v))
                return v.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String => bool.TryParse(v.GetString(), out var b) && b,
                    _ => false,
                };
        }
        catch { }
        return false;
    }

    private static string StrOf(JsonElement e, string prop)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString())
            : "";
}
