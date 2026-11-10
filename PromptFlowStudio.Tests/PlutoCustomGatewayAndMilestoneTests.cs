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
using PromptFlowStudio.Services.Publishing;
using PromptFlowStudio.ViewModels;
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
    // ══════════════════════ 🦙 로컬 AI 설정 UI 서비스 · Publish 캡션 페일오버 ══════════════════════

    private sealed class UriHandler : HttpMessageHandler
    {
        public Func<Uri, Task<HttpResponseMessage>> Respond { get; init; } = static _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        public readonly List<Uri> Requests = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request.RequestUri!);
            return Respond(request.RequestUri!);
        }
    }

    private sealed class NoopYt : IYouTubeShortsPublisher
    {
        public bool Enabled => false;
        public Task<ShortsPublishResult> PublishAsync(string videoPath, ShortsMetadata meta, string? srtPath = null, IProgress<double>? progress = null, CancellationToken ct = default)
            => Task.FromResult(new ShortsPublishResult(false, false, null, null, false, "off"));
        public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class NoopIg : IInstagramReelsPublisher
    {
        public bool Enabled => false;
        public Task<ReelsPublishOutcome> PublishAsync(string videoPath, string? publicVideoUrl, string caption, bool publicConsent, IProgress<double>? progress = null, CancellationToken ct = default)
            => Task.FromResult(new ReelsPublishOutcome(false, false, ReelsRoute.Mock, null, "off"));
        public Task<bool> ValidateCredentialsAsync(string? userId, string? accessToken, CancellationToken ct = default) => Task.FromResult(false);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task PlutoLocalGatewayOptions_PersistsAndAppliesRuntimeChanges()
    {
        var path = Path.Combine(_dir, PlutoLocalAiSettingsService.FileName);
        // 서버: 127.0.0.1:9100 = OpenAI 호환(/v1/models) · :9200 = Ollama 전용(/api/tags) · 그 외 포트 연결 거부
        var h = new UriHandler
        {
            Respond = u => u.Port switch
            {
                9100 when u.AbsolutePath == "/v1/models" => Task.FromResult(Json("{\"data\":[]}")),
                9100 => Task.FromResult(Json(OpenAiReply("{\"ok\":1}"))),
                9200 when u.AbsolutePath == "/api/tags" => Task.FromResult(Json("{\"models\":[]}")),
                9200 => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)),
                _ => throw new HttpRequestException("refused"),
            },
        };
        using var gw = new PlutoCustomLocalGateway(new PlutoLocalGatewayOptions { BaseUrl = "http://127.0.0.1:9999", TimeoutMs = 3500 }, h);
        using var svc = new PlutoLocalAiSettingsService(gw, path, h);
        var changed = 0;
        svc.Changed += () => Interlocked.Increment(ref changed);
        Assert.False(gw.IsAvailable);

        // 🔌 연결 테스트 — 초안만 Ping · 실행 중 게이트웨이 불변
        var draft = new PlutoLocalGatewayOptions { IsEnabled = true, BaseUrl = " http://127.0.0.1:9100 ", ModelName = " my-slm " };
        var ok = await svc.TestConnectionAsync(draft);
        Assert.True(ok.IsOnline);
        Assert.Equal(PlutoGatewayPingResult.OpenAiProtocol, ok.Protocol);
        var ollama = await svc.TestConnectionAsync(new PlutoLocalGatewayOptions { BaseUrl = "http://127.0.0.1:9200" });
        Assert.True(ollama.IsOnline);
        Assert.Equal(PlutoGatewayPingResult.OllamaProtocol, ollama.Protocol);
        var refused = await svc.TestConnectionAsync(new PlutoLocalGatewayOptions { BaseUrl = "http://127.0.0.1:9300" });
        Assert.False(refused.IsOnline);
        Assert.Equal(PlutoGatewayPingResult.RefusedTip, refused.Message);
        var lan = await svc.TestConnectionAsync(new PlutoLocalGatewayOptions { BaseUrl = "http://10.0.0.5:9100" });   // 🔒 프라이버시 게이트
        Assert.False(lan.IsOnline);
        Assert.Equal(PlutoGatewayPingResult.InvalidUrlTip, lan.Message);
        Assert.False(gw.Options.IsEnabled);
        Assert.Equal("http://127.0.0.1:9999", gw.Options.BaseUrl);
        Assert.Equal(0, changed);

        // 🚫 잘못된 URL → 적용·영속 모두 거부
        var bad = await svc.SaveAsync(new PlutoLocalGatewayOptions { IsEnabled = true, BaseUrl = "ftp://x" });
        Assert.False(bad.Applied);
        Assert.NotNull(bad.Error);
        Assert.False(File.Exists(path));
        Assert.False(gw.Options.IsEnabled);

        // ⚡ 서킷 열린 상태 → 저장 즉시 해제 · 새 엔드포인트/모델 반영 · 원자 영속
        gw.Options.IsEnabled = true;
        Assert.Null(await gw.CompleteJsonAsync("s", "u"));   // :9999 거부 → 서킷 오픈
        Assert.True(gw.IsCircuitOpen);
        var saved = await svc.SaveAsync(draft);
        Assert.True(saved.Applied);
        Assert.True(saved.Persisted);
        Assert.Equal(1, changed);
        Assert.False(gw.IsCircuitOpen);
        Assert.True(gw.IsAvailable);
        Assert.Equal("http://127.0.0.1:9100", gw.Options.BaseUrl);
        Assert.Equal("my-slm", gw.Options.ModelName);
        Assert.True((await gw.PingDetailedAsync()).IsOnline);
        Assert.Equal("{\"ok\":1}", await gw.CompleteJsonAsync("s", "u"));
        Assert.Equal(9100, h.Requests[^1].Port);
        Assert.True(File.Exists(path));
        Assert.DoesNotContain("ApiKey", await File.ReadAllTextAsync(path), StringComparison.Ordinal);

        // 🚀 재시작 모사 — 저장 파일이 환경변수 기본값을 덮어씀
        var reloaded = PlutoLocalAiSettingsService.LoadEffectiveOptions(path);
        Assert.True(reloaded.IsEnabled);
        Assert.Equal("http://127.0.0.1:9100", reloaded.BaseUrl);
        Assert.Equal("my-slm", reloaded.ModelName);

        // 🔴 비활성 저장 → 즉시 게이트웨이 차단(브리지가 템플릿으로)
        Assert.True((await svc.SaveAsync(new PlutoLocalGatewayOptions { IsEnabled = false, BaseUrl = "http://127.0.0.1:9100" })).Applied);
        Assert.False(gw.IsAvailable);
        Assert.Null(await gw.CompleteJsonAsync("s", "u"));
        Assert.False(PlutoLocalAiSettingsService.LoadEffectiveOptions(path).IsEnabled);

        // 🛡️ 손상 파일 → 예외 없이 환경변수/기본값
        await File.WriteAllTextAsync(path, "{ not json");
        var fallback = PlutoLocalAiSettingsService.LoadEffectiveOptions(path);
        Assert.False(string.IsNullOrWhiteSpace(fallback.BaseUrl));
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task PublishViewModel_GeneratesCaptions_ViaFailoverChain()
    {
        const string caption = "{\"hook\":\"🍼 로컬 AI가 쓴 훅\",\"story\":[\"첫 줄 💛\",\"둘째 줄 🥹\"],\"hashtags\":[\"#로컬캡션\",\"#육아스타그램\",\"#성장일기\",\"#babyreels\",\"#아기스타그램\",\"#딸바보\"]}";
        static PublishViewModel Open(PlutoLlmFailoverClient? chain)
        {
            var vm = new PublishViewModel(new NoopYt(), new NoopIg(), captionLlm: chain);
            vm.OpenForMedia("x.mp4", "x", null, "newborn sleeping on a pastel blanket");
            vm.ToYouTube = true;
            vm.ToInstagram = true;
            return vm;
        }

        // 기준선 — 체인 미주입 → 결정론적 템플릿
        string baseline;
        using (var bare = Open(null))
        {
            var d0 = await bare.GeneratePlutoSocialDraftAsync();
            Assert.NotNull(d0);
            Assert.False(d0!.UsedLlm);
            baseline = d0.FullText;
        }

        // ① Gemini 예외 → 로컬 게이트웨이(OpenAI 호환) 캡션
        var gemini = new FakeLlm { Throw = new HttpRequestException("gemini down") };
        var h = new StubHandler { Respond = (_, _) => Task.FromResult(Json(OpenAiReply(caption))) };
        using (var gw = new PlutoCustomLocalGateway(Enabled(), h))
        using (var vm = Open(new PlutoLlmFailoverClient(gemini, gw)))
        {
            Assert.IsType<PlutoLlmFailoverClient>(vm.CaptionClient);
            var d = await vm.GeneratePlutoSocialDraftAsync();
            Assert.NotNull(d);
            Assert.True(d!.UsedLlm);
            Assert.Equal("🍼 로컬 AI가 쓴 훅", d.HookIntro);
            Assert.Contains("#로컬캡션", d.Hashtags);
            Assert.InRange(d.Hashtags.Count, PlutoSocialCopywriter.MinLlmTags, PlutoSocialCopywriter.MaxTags);
            Assert.Equal(1, gemini.Calls);
            Assert.Contains(h.Requests, r => r.Path == "/v1/chat/completions" && r.Body.Contains("newborn sleeping", StringComparison.Ordinal));
            Assert.Contains("AI", vm.PlutoSocialMessage, StringComparison.Ordinal);
            Assert.False(vm.IsPlutoSocialBusy);
        }

        // ② Gemini 성공 → 게이트웨이 미호출
        var h2 = new StubHandler { Respond = (_, _) => Task.FromResult(Json(OpenAiReply(caption))) };
        using (var gw = new PlutoCustomLocalGateway(Enabled(), h2))
        using (var vm = Open(new PlutoLlmFailoverClient(new FakeLlm { Response = caption.Replace("로컬 AI가 쓴 훅", "제미나이 훅", StringComparison.Ordinal) }, gw)))
        {
            var d = await vm.GeneratePlutoSocialDraftAsync();
            Assert.True(d!.UsedLlm);
            Assert.Equal("🍼 제미나이 훅", d.HookIntro);
            Assert.Empty(h2.Requests);
        }

        // ③ Gemini 미가용 + 게이트웨이 오프라인(연결 거부) → 템플릿과 동일 · 예외 없음
        var h3 = new StubHandler { Respond = (_, _) => throw new HttpRequestException("refused") };
        using (var gw = new PlutoCustomLocalGateway(Enabled(), h3))
        using (var vm = Open(new PlutoLlmFailoverClient(new FakeLlm { IsAvailable = false }, gw)))
        {
            var d = await vm.GeneratePlutoSocialDraftAsync();
            Assert.NotNull(d);
            Assert.False(d!.UsedLlm);
            Assert.Equal(baseline, d.FullText);
            Assert.Contains("해시태그", vm.PlutoSocialMessage, StringComparison.Ordinal);
            Assert.NotEmpty(h3.Requests);
        }
    }
}
