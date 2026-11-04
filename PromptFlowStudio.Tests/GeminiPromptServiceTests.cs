using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.AI;
using PromptFlowStudio.Services.Render;
using Xunit;

namespace PromptFlowStudio.Tests;

public class GeminiPromptServiceTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        public int Calls;
        public string? LastBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return f(request);
        }
    }

    private static HttpResponseMessage Text(string t) => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":" + System.Text.Json.JsonSerializer.Serialize(t) + "}]}}]}", Encoding.UTF8, "application/json"),
    };

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task EnhancePrompt_InAmbientMode_InjectsLoopFriendlyKeywords()
    {
        // 시스템 지침에 앰비언트 루프 지시 강제 주입
        Assert.Contains("loop-friendly", GeminiPromptService.BuildSystemInstruction(VideoDurationMode.Sec15_AmbientSlow));
        Assert.DoesNotContain("loop-friendly", GeminiPromptService.BuildSystemInstruction(VideoDurationMode.Sec5_Single));
        Assert.Equal(VideoDurationMode.Sec15_AmbientSlow, GeminiPromptService.ParseDuration("15s-Ambient"));

        // API 경로: 요청 본문에 지침 포함 + 모델이 루프를 누락해도 루프 키워드 보장
        var h = new StubHandler(_ => Text("a quiet trading desk at night"));
        var svc = new GeminiPromptService(new HttpClient(h), () => "k");
        var r = await svc.EnhancePromptAsync("트레이딩 데스크", "15s-Ambient");
        Assert.False(r.UsedFallback);
        Assert.Contains("loop-friendly", h.LastBody);
        Assert.Contains("ping-pong loop", r.Prompt);
        Assert.Contains("subtle motion", r.Prompt);

        // 로컬 폴백 경로도 동일 키워드 포함
        var local = GeminiPromptService.FallbackLocalEnhance("트레이딩 데스크", VideoDurationMode.Sec15_AmbientSlow);
        Assert.Contains("ping-pong loop", local);
        Assert.Contains("slow camera drift", local);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task EnhancePrompt_WithoutApiKey_UsesLocalFallbackSuccessfully()
    {
        var h = new StubHandler(_ => throw new InvalidOperationException());
        var svc = new GeminiPromptService(new HttpClient(h), () => null);
        var r = await svc.EnhancePromptAsync("노을 해변의 골든리트리버", VideoDurationMode.Sec5_Single);
        Assert.True(r.UsedFallback);
        Assert.Equal(0, h.Calls);
        Assert.StartsWith("sunset beach golden retriever, ", r.Prompt);
        Assert.Contains("35mm lens", r.Prompt);
        Assert.Equal(GeminiPromptService.TranslateFallbackNotice, r.Message);
        Assert.Equal("노을 해변의 골든리트리버", r.SourceKorean);
        Assert.DoesNotContain("ping-pong", r.Prompt);

        // 네트워크 단절도 예외 없이 폴백, 재강화 시 중복 결합 없음
        var down = new GeminiPromptService(new HttpClient(new StubHandler(_ => throw new HttpRequestException("down"))), () => "k");
        var r2 = await down.EnhancePromptAsync(r.Prompt, VideoDurationMode.Sec5_Single);
        Assert.True(r2.UsedFallback);
        Assert.Equal(r.Prompt, r2.Prompt);

        // 이미지 없음/키 없음 Vision 도 폴백
        var v = await svc.AnalyzeReferenceImageAsync("Z:/__missing__.png", "바람에 날리는 머리카락");
        Assert.True(v.UsedFallback);
        Assert.Contains("natural subtle subject motion", v.Prompt);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task StoryboardDirector_GeneratesFourDistinctShots()
    {
        const string story = "심야 월스트리트 퀀트 트레이더의 하루";

        // 로컬 폴백(키 없음)
        var svc = new GeminiPromptService(new HttpClient(new StubHandler(_ => throw new InvalidOperationException())), () => null);
        var (shots, fallback) = await svc.DirectStoryboardAsync(story);
        Assert.True(fallback);
        Assert.Equal(4, shots.Count);
        Assert.Equal(4, shots.Select(s => s.Prompt).Distinct().Count());
        Assert.Equal(4, shots.Select(s => s.Camera).Distinct().Count());
        Assert.Equal(new[] { 1, 2, 3, 4 }, shots.Select(s => s.Index));
        Assert.All(shots, s => Assert.True(s.DurationSec > 0f));
        Assert.All(shots, s => Assert.Contains(story, s.Prompt));

        // API 성공 JSON → 스키마 검증 통과
        const string json = "{\"shots\":[" +
            "{\"title\":\"A\",\"prompt\":\"p1\",\"camera\":\"Crane Up\",\"durationSec\":5}," +
            "{\"title\":\"B\",\"prompt\":\"p2\",\"camera\":\"Pan\",\"durationSec\":5}," +
            "{\"title\":\"C\",\"prompt\":\"p3\",\"camera\":\"Dolly Zoom\",\"durationSec\":4}," +
            "{\"title\":\"D\",\"prompt\":\"p4\",\"camera\":\"Static\",\"durationSec\":6}]}";
        var api = new GeminiPromptService(new HttpClient(new StubHandler(_ => Text(json))), () => "k");
        var (s2, f2) = await api.DirectStoryboardAsync(story);
        Assert.False(f2);
        Assert.Equal(new[] { "p1", "p2", "p3", "p4" }, s2.Select(s => s.Prompt));

        // 스키마 불일치(3컷) → 로컬 4컷 폴백
        var bad = new GeminiPromptService(new HttpClient(new StubHandler(_ => Text("{\"shots\":[{\"prompt\":\"x\"}]}"))), () => "k");
        var (s3, f3) = await bad.DirectStoryboardAsync(story);
        Assert.True(f3);
        Assert.Equal(4, s3.Count);

        Assert.Empty((await svc.DirectStoryboardAsync("  ")).Shots);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task GeminiPromptService_GenerateSnsMetadata_ReturnsValidTitleAndTags()
    {
        // ① Gemini 정상 응답 — 스키마 검증 · '#' 보정 · 공백 제거 · 중복 제거 · 최대 8개 · 플랫폼/듀레이션 전달
        var json = "{\"title\":\"네온 금융 도시의 새벽\",\"description\":\"AI 시네마틱 숏폼.\",\"hashtags\":[\"#Fintech\",\"AI Video\",\"#fintech\",\"#Cinematic\",\"#Shorts\",\"#Neon\",\"#Seoul\",\"#Stock\",\"#Market\",\"#Extra\"]}";
        var h = new StubHandler(_ => Text(json));
        var api = new GeminiPromptService(new HttpClient(h), () => "k");
        var r = await api.GenerateSnsMetadataAsync("neon fintech city at dawn, slow 360-degree orbit shot around the subject", "instagram", 15f);
        Assert.False(r.UsedFallback);
        Assert.Equal("네온 금융 도시의 새벽", r.Title);
        Assert.Equal("AI 시네마틱 숏폼.", r.Description);
        Assert.InRange(r.Tags.Count, GeminiPromptService.MinSnsTags, GeminiPromptService.MaxSnsTags);
        Assert.Equal(8, r.Tags.Count);
        Assert.All(r.Tags, t => { Assert.StartsWith("#", t); Assert.DoesNotContain(" ", t); });
        Assert.Contains("#AIVideo", r.Tags);
        Assert.Single(r.Tags, t => string.Equals(t, "#Fintech", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, h.Calls);
        Assert.Contains("Instagram Reels", h.LastBody);
        Assert.Contains("15 seconds", h.LastBody);

        // ② API 키 누락 → 네트워크 호출 0 · 로컬 템플릿(프롬프트 첫 구절 제목 + #StockSuite #AIAnimation)
        var nh = new StubHandler(_ => throw new InvalidOperationException("must not call"));
        var local = await new GeminiPromptService(new HttpClient(nh), () => null).GenerateSnsMetadataAsync("neon fintech city at dawn, orbit", "YouTube");
        Assert.True(local.UsedFallback);
        Assert.Equal(0, nh.Calls);
        Assert.Contains("neon fintech city at dawn", local.Title);
        Assert.EndsWith("#Shorts", local.Title);
        Assert.Contains("#StockSuite", local.Tags);
        Assert.Contains("#AIAnimation", local.Tags);
        Assert.DoesNotContain("#Reels", local.Tags);
        Assert.InRange(local.Tags.Count, GeminiPromptService.MinSnsTags, GeminiPromptService.MaxSnsTags);

        // ③ 스키마 불일치(태그 5개 미만)/HTTP 실패 → 로컬 폴백 · 빈 프롬프트도 무예외
        var bad = await new GeminiPromptService(new HttpClient(new StubHandler(_ => Text("{\"title\":\"t\",\"hashtags\":[\"#a\"]}"))), () => "k").GenerateSnsMetadataAsync("p", "reels");
        Assert.True(bad.UsedFallback);
        Assert.DoesNotContain("#Shorts", bad.Tags);
        var down = await new GeminiPromptService(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest))), () => "k").GenerateSnsMetadataAsync("p", null);
        Assert.True(down.UsedFallback);
        var empty = await api.GenerateSnsMetadataAsync("  ", "youtube");
        Assert.True(empty.UsedFallback);
        Assert.False(string.IsNullOrWhiteSpace(empty.Title));
        Assert.Null(GeminiPromptService.ParseSnsMetadata("not json"));
        Assert.Equal("YouTube Shorts + Instagram Reels", GeminiPromptService.NormalizePlatform("YouTube Shorts + Instagram Reels"));
    }
}
