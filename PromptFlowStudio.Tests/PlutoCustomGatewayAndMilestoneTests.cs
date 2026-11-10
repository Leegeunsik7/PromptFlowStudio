using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Pluto;
using Xunit;

namespace PromptFlowStudio.Tests;

public sealed class PlutoCustomGatewayAndMilestoneTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_milestone_" + Guid.NewGuid().ToString("N"));

    public PlutoCustomGatewayAndMilestoneTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<string, CancellationToken, Task<HttpResponseMessage>> Respond { get; init; } = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        public readonly List<(string Method, string Path, string Body, string? Auth)> Requests = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var path = request.RequestUri!.AbsolutePath;
            lock (Requests) Requests.Add((request.Method.Method, path, body, request.Headers.Authorization?.ToString()));
            return await Respond(path, ct).ConfigureAwait(false);
        }
    }

    private sealed class FakeLlm : IPlutoLlmClient
    {
        public bool IsAvailable { get; init; } = true;
        public string? Response { get; init; }
        public Exception? Throw { get; init; }
        public int Calls;

        public Task<string?> CompleteJsonAsync(string system, string user, double temperature, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Throw is not null) throw Throw;
            return Task.FromResult(Response);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string OllamaReply(string content) => JsonSerializer.Serialize(new { model = "local-slm", message = new { role = "assistant", content }, done = true });
    private static string OpenAiReply(string content) => JsonSerializer.Serialize(new { choices = new[] { new { index = 0, message = new { role = "assistant", content } } } });

    private static PlutoLocalGatewayOptions Enabled(string url = "http://localhost:8080") => new() { BaseUrl = url, ModelName = "local-slm", IsEnabled = true, TimeoutMs = 3500 };

    [Fact]
    public async Task PlutoCustomLocalGateway_FormatsOpenAiAndOllamaPayloadsCorrectly()
    {
        // 기본값
        var d = new PlutoLocalGatewayOptions();
        Assert.Equal("http://localhost:8080", d.BaseUrl);
        Assert.Equal("local-slm", d.ModelName);
        Assert.False(d.IsEnabled);
        Assert.Equal(3500, d.TimeoutMs);

        // 🔁 OpenAI 404 → Ollama 폴백 · 페이로드 스키마
        var h = new StubHandler { Respond = (p, _) => Task.FromResult(p == "/api/chat" ? Json(OllamaReply("{\"ok\":1}")) : new HttpResponseMessage(HttpStatusCode.NotFound)) };
        using (var gw = new PlutoCustomLocalGateway(Enabled(), h))
        {
            Assert.True(gw.IsAvailable);
            Assert.Equal("{\"ok\":1}", await gw.CompleteJsonAsync("SYS 지시", "USER 요청"));
            Assert.Equal(2, h.Requests.Count);
            Assert.Equal(("POST", "/v1/chat/completions"), (h.Requests[0].Method, h.Requests[0].Path));
            Assert.Equal(("POST", "/api/chat"), (h.Requests[1].Method, h.Requests[1].Path));

            using (var oa = JsonDocument.Parse(h.Requests[0].Body))
            {
                var r = oa.RootElement;
                Assert.Equal("local-slm", r.GetProperty("model").GetString());
                var m = r.GetProperty("messages");
                Assert.Equal(2, m.GetArrayLength());
                Assert.Equal("system", m[0].GetProperty("role").GetString());
                Assert.Equal("SYS 지시", m[0].GetProperty("content").GetString());
                Assert.Equal("user", m[1].GetProperty("role").GetString());
                Assert.Equal("USER 요청", m[1].GetProperty("content").GetString());
                Assert.Equal(0.7, r.GetProperty("temperature").GetDouble(), 6);
                Assert.False(r.TryGetProperty("stream", out _));
            }
            using (var ol = JsonDocument.Parse(h.Requests[1].Body))
            {
                var r = ol.RootElement;
                Assert.Equal("local-slm", r.GetProperty("model").GetString());
                Assert.Equal(2, r.GetProperty("messages").GetArrayLength());
                Assert.Equal(JsonValueKind.False, r.GetProperty("stream").ValueKind);
                Assert.Equal(0.7, r.GetProperty("options").GetProperty("temperature").GetDouble(), 6);
            }

            // 성공 프로토콜 기억 → 다음 호출은 Ollama 직행
            Assert.True(gw.PrefersOllama);
            Assert.NotNull(await gw.CompleteJsonAsync("s", "u"));
            Assert.Equal(3, h.Requests.Count);
            Assert.Equal("/api/chat", h.Requests[2].Path);
        }

        // ✅ OpenAI 직접 성공 — choices[0].message.content · <think> 제거 · 산문 속 JSON 추출 · Bearer(환경변수 주입 모사) · 베이스 경로 유지
        var h2 = new StubHandler { Respond = (_, _) => Task.FromResult(Json(OpenAiReply("<think>plan</think>\nSure! {\"a\":\"b\"} done"))) };
        var o2 = Enabled("http://127.0.0.1:9000/llm/");
        o2.ApiKey = "k-test";
        using (var gw = new PlutoCustomLocalGateway(o2, h2))
        {
            Assert.Equal("{\"a\":\"b\"}", await gw.CompleteJsonAsync("s", "u"));
            Assert.Single(h2.Requests);
            Assert.Equal("/llm/v1/chat/completions", h2.Requests[0].Path);
            Assert.Equal("Bearer k-test", h2.Requests[0].Auth);
        }

        // 📡 Ping — /v1/models 실패 → /api/tags 성공
        var h3 = new StubHandler { Respond = (p, _) => Task.FromResult(p == "/api/tags" ? Json("{\"models\":[]}") : new HttpResponseMessage(HttpStatusCode.NotFound)) };
        using (var gw = new PlutoCustomLocalGateway(Enabled(), h3))
        {
            Assert.True(await gw.PingAsync());
            Assert.Equal(new[] { "/v1/models", "/api/tags" }, new[] { h3.Requests[0].Path, h3.Requests[1].Path });
        }

        // 🔒 비활성 · 비루프백(AllowRemote 없음) · 비 http 스킴 → 요청 없이 null
        var h4 = new StubHandler();
        foreach (var o in new[] { new PlutoLocalGatewayOptions(), Enabled("http://10.1.2.3:8080"), Enabled("file:///c:/x") })
        {
            using var gw = new PlutoCustomLocalGateway(o, h4);
            Assert.False(gw.IsAvailable);
            Assert.Null(await gw.CompleteJsonAsync("s", "u"));
            if (o.IsEnabled) Assert.False(await gw.PingAsync());   // 비활성 Ping은 연결 진단용으로 허용 · 프라이버시 게이트는 Ping도 차단
        }
        Assert.Empty(h4.Requests);
        var remote = Enabled("http://10.1.2.3:8080");
        remote.AllowRemote = true;
        Assert.True(remote.TryResolveBase(out _));

        // ❌ 스키마 불일치 · 500 → null(예외 없음) + 서킷 개방(즉시 null · 추가 요청 없음)
        var h5 = new StubHandler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)) };
        using (var gw = new PlutoCustomLocalGateway(Enabled(), h5))
        {
            Assert.Null(await gw.CompleteJsonAsync("s", "u"));
            Assert.True(gw.IsCircuitOpen);
            Assert.False(gw.IsAvailable);
            var n = h5.Requests.Count;
            Assert.Null(await gw.CompleteJsonAsync("s", "u"));
            Assert.Equal(n, h5.Requests.Count);
        }
        var h6 = new StubHandler { Respond = (_, _) => Task.FromResult(Json("{\"unexpected\":true}")) };
        using (var gw = new PlutoCustomLocalGateway(Enabled(), h6)) Assert.Null(await gw.CompleteJsonAsync("s", "u"));

        // 🌐 연결 실패(양 프로토콜) → null
        var h7 = new StubHandler { Respond = static (_, _) => throw new HttpRequestException("refused") };
        using (var gw = new PlutoCustomLocalGateway(Enabled(), h7))
        {
            Assert.Null(await gw.CompleteJsonAsync("s", "u"));
            Assert.Equal(2, h7.Requests.Count);
        }
    }

    [Fact]
    public async Task PlutoLlmBridge_PrioritizesGateway_WhenGeminiFails()
    {
        var gwJson = "{\"from\":\"gateway\"}";
        HttpResponseMessage GwOk() => Json(OllamaReply(gwJson));

        // ① Gemini 성공 → 게이트웨이 미호출
        var hA = new StubHandler { Respond = (_, _) => Task.FromResult(GwOk()) };
        using (var gw = new PlutoCustomLocalGateway(Enabled(), hA))
        {
            var gem = new FakeLlm { Response = "{\"from\":\"gemini\"}" };
            Assert.Equal("{\"from\":\"gemini\"}", await PlutoLlmBridge.TryCompleteAsync(new PlutoLlmFailoverClient(gem, gw), "s", "u", 0.7, CancellationToken.None));
            Assert.Equal(1, gem.Calls);
            Assert.Empty(hA.Requests);
        }

        // ② Gemini 예외 · 빈 응답 · 키 없음 → 게이트웨이 실행
        foreach (var gem in new[] { new FakeLlm { Throw = new InvalidOperationException("boom") }, new FakeLlm { Response = "" }, new FakeLlm { IsAvailable = false } })
        {
            var h = new StubHandler { Respond = (_, _) => Task.FromResult(GwOk()) };
            using var gw = new PlutoCustomLocalGateway(Enabled(), h);
            Assert.Equal(gwJson, await PlutoLlmBridge.TryCompleteAsync(new PlutoLlmFailoverClient(gem, gw), "s", "u", 0.7, CancellationToken.None));
            Assert.Equal(gem.IsAvailable ? 1 : 0, gem.Calls);
            Assert.NotEmpty(h.Requests);
        }
        // Gemini 미주입(null)도 동일
        var hB = new StubHandler { Respond = (_, _) => Task.FromResult(GwOk()) };
        using (var gw = new PlutoCustomLocalGateway(Enabled(), hB))
        {
            var chain = new PlutoLlmFailoverClient(null, gw);
            Assert.True(chain.IsAvailable);
            Assert.Equal(gwJson, await PlutoLlmBridge.TryCompleteAsync(chain, "s", "u", 0.7, CancellationToken.None));
        }

        // ③ 게이트웨이 비활성 → 요청 없이 null(템플릿 폴백) · 체인 가용성 false
        var hC = new StubHandler { Respond = (_, _) => Task.FromResult(GwOk()) };
        using (var gw = new PlutoCustomLocalGateway(new PlutoLocalGatewayOptions(), hC))
        {
            var chain = new PlutoLlmFailoverClient(new FakeLlm { IsAvailable = false }, gw);
            Assert.False(chain.IsAvailable);
            Assert.Null(await PlutoLlmBridge.TryCompleteAsync(chain, "s", "u", 0.7, CancellationToken.None));
            Assert.Empty(hC.Requests);
        }

        // ④ Gemini 실패 + 게이트웨이 무응답(토큰 무시) → 예산 내 null → 오케스트레이터 템플릿 폴백
        var hang = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hD = new StubHandler { Respond = (_, _) => hang.Task };
        try
        {
            using var gw = new PlutoCustomLocalGateway(Enabled(), hD);
            var chain = new PlutoLlmFailoverClient(new FakeLlm { Throw = new TimeoutException() }, gw);
            Assert.Null(await PlutoLlmBridge.TryCompleteAsync(chain, "s", "u", 0.7, CancellationToken.None, TimeSpan.FromMilliseconds(150)).WaitAsync(TimeSpan.FromSeconds(30)));

            var orch = new PlutoMilestoneOrchestrator(chain) { LlmBudget = TimeSpan.FromMilliseconds(150) };
            var plan = await orch.OrchestrateFourCutPlanAsync("100일의 기적").WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(plan.UsedLlm);
            Assert.Equal(PlutoMilestoneOrchestrator.BuildLocal(PlutoMilestoneTheme.Day100, null).Scenes[0].Prompt, plan.Scenes[0].Prompt);
        }
        finally { hang.TrySetResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)); }

        // ⑤ 호출자 취소는 전파
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var hE = new StubHandler { Respond = (_, ct) => ct.IsCancellationRequested ? Task.FromCanceled<HttpResponseMessage>(ct) : Task.FromResult(GwOk()) };
        using (var gw = new PlutoCustomLocalGateway(Enabled(), hE))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PlutoLlmBridge.TryCompleteAsync(new PlutoLlmFailoverClient(new FakeLlm { IsAvailable = false }, gw), "s", "u", 0.7, cts.Token));

        // 중첩 방지
        var inner = new PlutoLlmFailoverClient(new FakeLlm(), null);
        Assert.IsType<FakeLlm>(new PlutoLlmFailoverClient(inner, null).Primary);
    }

    [Fact]
    public async Task PlutoMilestoneOrchestrator_GeneratesFourCoordinatedScenes_WithIdentityAnchor()
    {
        using var vault = new PlutoIdentityVault(Path.Combine(_dir, "vault.json"));
        Assert.True(await vault.SaveProfileAsync(new PlutoIdentityProfile { Id = "p1", SubjectName = "서아", Gender = "female", VisualFeatures = "charming dimples", IsActive = true }));
        var anchor = (await vault.GetActiveProfileAsync())!.GetConsistencyAnchor();
        Assert.Equal("same consistent baby, female, charming dimples", anchor);

        // 테마 키 매핑
        Assert.Equal(PlutoMilestoneTheme.Day100, PlutoMilestoneOrchestrator.ParseTheme("100일의 기적"));
        Assert.Equal(PlutoMilestoneTheme.FirstBirthday, PlutoMilestoneOrchestrator.ParseTheme("첫돌의 감동"));
        Assert.Equal(PlutoMilestoneTheme.FirstBirthday, PlutoMilestoneOrchestrator.ParseTheme("첫돌"));
        Assert.Equal(PlutoMilestoneTheme.DailyRoutine, PlutoMilestoneOrchestrator.ParseTheme("우리아이 하루"));
        Assert.Equal(PlutoMilestoneTheme.Day100, PlutoMilestoneOrchestrator.ParseTheme("Day100"));
        Assert.Equal(PlutoMilestoneTheme.DailyRoutine, PlutoMilestoneOrchestrator.ParseTheme(null));

        var expectedRanges = new[] { "0-4s", "4-8s", "8-12s", "12-15s" };
        var expectedCams = new[] { "Eye-Level Push-In", "Low Orbit", "Macro Close-Up", "Slow Pull-Back" };

        // ⚡ LLM 미설정 → 템플릿 · 3테마 모두 4컷 · 앵커 선두 · 결정론
        var orch = new PlutoMilestoneOrchestrator(null, vault);
        foreach (var key in new[] { "100일의 기적", "첫돌의 감동", "우리아이 하루" })
        {
            var plan = await orch.OrchestrateFourCutPlanAsync(key, "wearing a yellow onesie");
            Assert.False(plan.UsedLlm);
            Assert.Equal(PlutoMilestoneOrchestrator.DisplayName(PlutoMilestoneOrchestrator.ParseTheme(key)), plan.MilestoneName);
            Assert.Equal(anchor, plan.PersonaConsistencyAnchor);
            Assert.Equal(4, plan.Scenes.Count);
            var prompts = new HashSet<string>();
            for (var i = 0; i < 4; i++)
            {
                var s = plan.Scenes[i];
                Assert.Equal(i + 1, s.SlotIndex);
                Assert.Equal(expectedRanges[i], s.TimeRange);
                Assert.Equal(expectedCams[i], s.CameraMovement);
                Assert.StartsWith(anchor + ", ", s.Prompt, StringComparison.Ordinal);
                Assert.Contains("yellow onesie", s.Prompt, StringComparison.Ordinal);
                Assert.False(string.IsNullOrWhiteSpace(s.SceneTitle));
                Assert.False(string.IsNullOrWhiteSpace(s.RecommendedBgm));
                Assert.True(prompts.Add(s.Prompt));
            }
            Assert.Contains("sleep", plan.Scenes[3].Prompt, StringComparison.OrdinalIgnoreCase);

            // 🎬 스토리보드 4슬롯 입력 — 구간 합 15초
            var shots = plan.ToDirectorShots();
            Assert.Equal(4, shots.Length);
            var total = 0f;
            for (var i = 0; i < 4; i++) { Assert.Equal(i + 1, shots[i].Index); Assert.Equal(plan.Scenes[i].Prompt, shots[i].Prompt); Assert.Equal(expectedCams[i], shots[i].Camera); total += shots[i].DurationSec; }
            Assert.Equal(15f, total);

            var again = await orch.OrchestrateFourCutPlanAsync(key, "wearing a yellow onesie");
            for (var i = 0; i < 4; i++) Assert.Equal(plan.Scenes[i].Prompt, again.Scenes[i].Prompt);
        }

        // 🧠 LLM 부분 응답(슬롯 3만 유효 · 슬롯 9 무시) → 슬롯 3 채택(앵커 재삽입) · 나머지 템플릿 보강
        var local = PlutoMilestoneOrchestrator.BuildLocal(PlutoMilestoneTheme.Day100, anchor);
        var llm = new FakeLlm { Response = "```json\n{\"scenes\":[{\"slot\":3,\"title\":\"백일의 함박웃음\",\"prompt\":\"the baby laughing at bubbles, warm light\",\"camera\":\"Macro Close-Up\",\"bgm\":\"Music Box\"},{\"slot\":9,\"prompt\":\"bad\"},{\"slot\":2,\"prompt\":\"\"}]}\n```" };
        var mixed = await new PlutoMilestoneOrchestrator(llm, vault).OrchestrateFourCutPlanAsync("백일");
        Assert.True(mixed.UsedLlm);
        Assert.Equal(1, llm.Calls);
        Assert.Equal(anchor + ", the baby laughing at bubbles, warm light", mixed.Scenes[2].Prompt);
        Assert.Equal("백일의 함박웃음", mixed.Scenes[2].SceneTitle);
        Assert.Equal("8-12s", mixed.Scenes[2].TimeRange);
        Assert.Equal(local.Scenes[0].Prompt, mixed.Scenes[0].Prompt);
        Assert.Equal(local.Scenes[1].Prompt, mixed.Scenes[1].Prompt);
        Assert.Equal(local.Scenes[3].Prompt, mixed.Scenes[3].Prompt);

        // ❌ 잘못된 JSON · 예외 → 전량 템플릿
        foreach (var bad in new[] { new FakeLlm { Response = "not json" }, new FakeLlm { Throw = new InvalidOperationException() }, new FakeLlm { Response = "{\"scenes\":[]}" } })
        {
            var p = await new PlutoMilestoneOrchestrator(bad, vault).OrchestrateFourCutPlanAsync("Day100");
            Assert.False(p.UsedLlm);
            for (var i = 0; i < 4; i++) Assert.Equal(local.Scenes[i].Prompt, p.Scenes[i].Prompt);
        }

        // 프로필 없음 → 기본 앵커 · 반려동물 → pet 앵커
        Assert.StartsWith(PlutoMilestoneOrchestrator.DefaultBabyAnchor + ", ", (await new PlutoMilestoneOrchestrator().OrchestrateFourCutPlanAsync("하루")).Scenes[0].Prompt, StringComparison.Ordinal);
        Assert.StartsWith(PlutoMilestoneOrchestrator.DefaultPetAnchor + ", the pet", PlutoMilestoneOrchestrator.BuildLocal(PlutoMilestoneTheme.DailyRoutine, null, isPet: true).Scenes[0].Prompt, StringComparison.Ordinal);
    }
}
