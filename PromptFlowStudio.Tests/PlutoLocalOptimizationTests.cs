using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Pluto;
using Xunit;

namespace PromptFlowStudio.Tests;

public sealed class PlutoLocalOptimizationTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public required Func<string, HttpResponseMessage> Respond { get; init; }
        public readonly List<(string Path, string Body)> Requests = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            lock (Requests) Requests.Add((request.RequestUri!.AbsolutePath, body));
            return Respond(request.RequestUri!.AbsolutePath);
        }
    }

    private static HttpResponseMessage OllamaOk(string content)
        => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { model = "local-slm", message = new { role = "assistant", content }, done = true }), Encoding.UTF8, "application/json") };

    private static PlutoLocalGatewayOptions Enabled() => new() { BaseUrl = "http://localhost:8080", ModelName = "local-slm", IsEnabled = true, TimeoutMs = 5000 };

    private static (string System, string User) Messages(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var m = doc.RootElement.GetProperty("messages");
        return (m[0].GetProperty("content").GetString()!, m[1].GetProperty("content").GetString()!);
    }

    [Fact]
    public void PlutoJsonSanitizer_HealsMarkdownPreamble_AndTrailingCommas()
    {
        // 서두 산문 + 펜스(닫힘 없음) + 후행 쉼표 + 문자열 내 비이스케이프 개행 + 잘린 최상위 괄호
        var dirty = "물론이죠! 요청하신 시나리오입니다 [참고용] :)\n```json\n{\n  \"variants\": [\n" +
                    "    {\"style\":\"studio\",\"hookTitle\":\"첫 뒤집기 🎉\",\"act1\":\"baby\nrolls over\",},\n" +
                    "    {\"style\":\"pixar\",\"tags\":[\"#a\",\"#b\",],},\n  ],\n  \"note\": \"comma, } inside string\"\n";
        using (var doc = JsonDocument.Parse(PlutoJsonSanitizer.Sanitize(dirty)))
        {
            var r = doc.RootElement;
            Assert.Equal(2, r.GetProperty("variants").GetArrayLength());
            Assert.Equal("baby rolls over", r.GetProperty("variants")[0].GetProperty("act1").GetString());
            Assert.Equal("첫 뒤집기 🎉", r.GetProperty("variants")[0].GetProperty("hookTitle").GetString());
            Assert.Equal(2, r.GetProperty("variants")[1].GetProperty("tags").GetArrayLength());
            Assert.Equal("comma, } inside string", r.GetProperty("note").GetString());
        }

        // 완결 JSON + 후미 산문(괄호 포함) → 최상위 값만
        Assert.Equal("{\"a\":[1,2]}", PlutoJsonSanitizer.Sanitize("Here you go:\n{\"a\":[1,2,]} Hope this helps {:)}"));
        // 펜스 닫힘 정상
        Assert.Equal("{\"x\":1}", PlutoJsonSanitizer.Sanitize("```json\n{\"x\":1,}\n```\n설명 끝"));
        // 잘림 — 문자열 중간 · 값 없는 키 · 콜론 직후 · 쉼표 직후 · 엇갈린 괄호
        Assert.Equal("{\"act1\":\"hello\"}", PlutoJsonSanitizer.Sanitize("{\"act1\":\"hello"));
        Assert.Equal("{\"a\":1,\"b\":null}", PlutoJsonSanitizer.Sanitize("{\"a\":1,\"b\""));
        Assert.Equal("{\"a\":null}", PlutoJsonSanitizer.Sanitize("{\"a\":"));
        Assert.Equal("[{\"a\":1}]", PlutoJsonSanitizer.Sanitize("[{\"a\":1},"));
        Assert.Equal("{\"a\":[1]}", PlutoJsonSanitizer.Sanitize("{\"a\":[1}"));
        // <think> 제거 · 이스케이프 보존
        Assert.Equal("{\"q\":\"say \\\"hi\\\"\"}", PlutoJsonSanitizer.Sanitize("<think>{\"plan\":1}</think>{\"q\":\"say \\\"hi\\\"\"}"));
        // 정상 JSON 무변경 · JSON 없음 → 트림 원문 · 공백 → 빈 문자열
        const string clean = "{\n  \"variants\": [ { \"style\": \"retro\" } ]\n}";
        Assert.Equal(clean, PlutoJsonSanitizer.Sanitize(clean));
        Assert.Equal("죄송해요, 지금은 어려워요", PlutoJsonSanitizer.Sanitize("  죄송해요, 지금은 어려워요 "));
        Assert.Equal("", PlutoJsonSanitizer.Sanitize("  \n "));
        Assert.Equal("", PlutoJsonSanitizer.Sanitize(null));
        Assert.All(new[] { dirty, "{\"a\":\"b", "[[[", "{\"k\":[{\"z\":" }, s => { using var d = JsonDocument.Parse(PlutoJsonSanitizer.Sanitize(s)); });
    }

    [Fact]
    public void PlutoPromptCompressor_GeneratesInvariantPrefix_AndReducesTokens()
    {
        var a = PlutoPromptCompressor.Compose(PlutoCopilot.LlmTripletInstruction,
            "Memo (Korean): 오늘 아기가 처음 뒤집기 성공\nDetected theme: Baby\nSeason / time: autumn, morning\n" +
            "Subject identity anchor (keep this exact same character in every act): same consistent baby, female, charming dimples\n");
        var b = PlutoPromptCompressor.Compose(PlutoCopilot.LlmTripletInstruction,
            "Memo (Korean): 강아지랑 공원 산책하다 낙엽 위에서 뒹굴었어\nDetected theme: Pet\nSeason / time: autumn, evening\n" +
            "Subject identity anchor (keep this exact same character in every act): same consistent baby, female, charming dimples\n");

        // 🧷 불변 접두 — system 바이트 동일 · user도 [CTX] 앵커 구간까지 동일(메모보다 앞)
        Assert.Equal(a.System, b.System);
        Assert.Contains("\"variants\"", a.System, StringComparison.Ordinal);
        Assert.Contains("studio|pixar|retro", a.System, StringComparison.Ordinal);
        Assert.Contains("lullaby|musicbox|acoustic", a.System, StringComparison.Ordinal);
        var prefix = PlutoPromptCompressor.Separator + "\n" + PlutoPromptCompressor.ContextHeader + "\nanchor: same consistent baby, female, charming dimples\n" + PlutoPromptCompressor.RequestHeader + "\n";
        Assert.StartsWith(prefix, a.User, StringComparison.Ordinal);
        Assert.StartsWith(prefix, b.User, StringComparison.Ordinal);
        Assert.Contains("memo: 오늘 아기가 처음 뒤집기 성공", a.User, StringComparison.Ordinal);
        Assert.Contains("when: autumn, evening", b.User, StringComparison.Ordinal);
        Assert.DoesNotContain(PlutoPromptCompressor.ContextHeader, PlutoPromptCompressor.Compose("s", "Memo (Korean): x").User, StringComparison.Ordinal);

        // 🗜 지시문 토큰 ≥ 50% 절감(알려진 전 지시문) · 미지 지시문은 공백 압축만
        foreach (var full in new[]
                 {
                     PlutoCopilot.LlmScriptInstruction, PlutoCopilot.LlmTripletInstruction, PlutoGenerativeEngine.LlmTrendInstruction,
                     PlutoMilestoneOrchestrator.LlmMilestoneInstruction, PlutoSubtitleDirector.LlmLetterInstruction,
                     PlutoSocialCopywriter.LlmCaptionInstruction, PlutoSocialRadarEngine.LlmRadarInstruction,
                     PromptFlowStudio.Services.AI.GeminiPromptService.SnsSeoInstruction,
                 })
        {
            Assert.True(PlutoPromptCompressor.IsKnown(full));
            var fullT = PlutoPromptCompressor.EstimateTokens(full);
            var compT = PlutoPromptCompressor.EstimateTokens(PlutoPromptCompressor.CompactSystem(full));
            Assert.True(compT * 2 <= fullT, $"{full[..30]}: {compT}/{fullT}");
        }
        Assert.False(PlutoPromptCompressor.IsKnown("custom  rule"));
        Assert.Equal("custom rule", PlutoPromptCompressor.CompactSystem("  custom \t rule "));
        var total = PlutoPromptCompressor.EstimateTokens(PlutoCopilot.LlmTripletInstruction);
        Assert.True(PlutoPromptCompressor.EstimateTokens(a.System) * 100 / total <= 45);   // 트리플렛 ~60% 다이어트
    }

    [Fact]
    public async Task PlutoLlmFailoverClient_AppliesSanitizer_OnGatewayResponses()
    {
        // 🩹 소형 모델 특유 문법 결함(서두 · 펜스 · 후행 쉼표 · 잘린 꼬리)
        var triplet = "네! 3가지 스타일로 준비했어요.\n```json\n{\"variants\":[\n" +
                      "{\"style\":\"studio\",\"hookTitle\":\"화보 속 첫 뒤집기 📸\",\"act1\":\"soft studio light on a baby on a cream blanket\",\"act2\":\"baby rolls over with determined face\",\"act3\":\"baby beams a gummy smile at camera\",\"camera\":\"slow push-in\",\"bgm\":\"musicbox\",},\n" +
                      "{\"style\":\"pixar\",\"hookTitle\":\"3D 뒤집기 대모험 🎨\",\"act1\":\"whimsical 3D nursery with pastel glow\",\"act2\":\"cartoon baby flips over with sparkles\",\"act3\":\"big expressive eyes and joyful giggle\",\"camera\":\"orbit\",\"bgm\":\"lullaby\",},\n" +
                      "{\"style\":\"retro\",\"hookTitle\":\"90년대 홈비디오 감성 📼\",\"act1\":\"16mm grainy living room morning\",\"act2\":\"handheld mom-cam follows the roll\",\"act3\":\"vignette frames a sleepy happy smile\",\"camera\":\"handheld\",\"bgm\":\"acoustic\",}\n" +
                      "]";   // 최상위 '}' 누락 + 펜스 미종료
        var h = new StubHandler { Respond = p => p == "/api/chat" ? OllamaOk(triplet) : new HttpResponseMessage(HttpStatusCode.NotFound) };
        using (var gw = new PlutoCustomLocalGateway(Enabled(), h))
        {
            var chain = new PlutoLlmFailoverClient(null, gw);
            var copilot = new PlutoCopilot(null, chain);
            var variants = await copilot.DraftTripletVariantsAsync("오늘 아기가 거실에서 처음으로 뒤집기 성공하고 방긋 웃었어", new DateTime(2026, 11, 10, 9, 0, 0)).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(3, variants.Count);
            Assert.All(variants, v => Assert.True(v.UsedLlm));   // 템플릿 폴백 없음
            Assert.Contains(variants, v => v.HookTitle == "화보 속 첫 뒤집기 📸");
            Assert.Contains(variants, v => v.HookTitle == "90년대 홈비디오 감성 📼");

            // 🗜 게이트웨이에는 컴팩트 불변 접두 프롬프트 전송(Gemini 서술형 원문 아님)
            var chat = h.Requests.Find(r => r.Path == "/api/chat");
            Assert.NotNull(chat.Body);
            var (sys, user) = Messages(chat.Body);
            Assert.Equal(PlutoPromptCompressor.CompactSystem(PlutoCopilot.LlmTripletInstruction), sys);
            Assert.StartsWith(PlutoPromptCompressor.Separator + "\n", user, StringComparison.Ordinal);
            Assert.Contains("memo: 오늘 아기가", user, StringComparison.Ordinal);
        }

        // 🩹 마일스톤 4컷 — 배열 후행 쉼표 + 잘림도 LLM 채택
        var scenes = "Sure! Here is the plan:\n{\"scenes\":[{\"slot\":1,\"title\":\"아침 햇살\",\"prompt\":\"baby waking up in morning light\",},{\"slot\":2,\"title\":\"터미타임\",\"prompt\":\"tummy time on a play mat";
        var h2 = new StubHandler { Respond = p => p == "/api/chat" ? OllamaOk(scenes) : new HttpResponseMessage(HttpStatusCode.NotFound) };
        using (var gw = new PlutoCustomLocalGateway(Enabled(), h2))
        {
            var plan = await new PlutoMilestoneOrchestrator(new PlutoLlmFailoverClient(null, gw)).OrchestrateFourCutPlanAsync("100일의 기적").WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(plan.UsedLlm);
            Assert.Equal("아침 햇살", plan.Scenes[0].SceneTitle);
            Assert.EndsWith("tummy time on a play mat", plan.Scenes[1].Prompt, StringComparison.Ordinal);
        }

        // 🧠 Gemini 경로(Primary)는 원본 서술형 지시문 그대로
        string? seen = null;
        var gem = new CapturingLlm(s => seen = s);
        await PlutoLlmBridge.TryCompleteAsync(new PlutoLlmFailoverClient(gem, null), PlutoCopilot.LlmTripletInstruction, "u", 0.9, CancellationToken.None);
        Assert.Equal(PlutoCopilot.LlmTripletInstruction, seen);
    }

    private sealed class CapturingLlm : IPlutoLlmClient
    {
        private readonly Action<string> _onSystem;
        public CapturingLlm(Action<string> onSystem) => _onSystem = onSystem;
        public bool IsAvailable => true;
        public Task<string?> CompleteJsonAsync(string system, string user, double temperature, CancellationToken ct)
        {
            _onSystem(system);
            return Task.FromResult<string?>("{\"ok\":1}");
        }
    }
}
