using System.Net;
using System.Text;
using PromptFlowStudio.Services.AI;
using PromptFlowStudio.Services.Prompt;
using PromptFlowStudio.ViewModels;

namespace PromptFlowStudio.Tests;

/// <summary>1번 워크플로우 — 프롬프트 합성기(빈 입력 · 800자 절삭 · Subject 우선 · 네거티브 중복 제거) + 강화기 폴백 + T2V/I2V 뷰모델 회귀 잠금.</summary>
public class PromptSynthesizerTests
{
    private readonly PromptSynthesizerService _synth = new();

    [Fact]
    [Trait("Category", "FastUnit")]
    public void EmptyInput_ProducesEmptyPositive_AndDefaultNegatives()
    {
        var r = _synth.Synthesize(new PromptContext(null));
        Assert.Equal("", r.FinalPositivePrompt);
        Assert.Equal(0, r.CharacterCount);
        Assert.False(r.IsOverBudget);
        Assert.Equal("distorted face, mutated limbs, low quality", r.FinalNegativePrompt);
    }

    [Theory]
    [Trait("Category", "FastUnit")]
    [InlineData(801)]
    [InlineData(2000)]
    public void OverBudget_IsTruncatedTo800_AndFlagged(int len)
    {
        var input = new StringBuilder();
        while (input.Length < len) input.Append("golden retriever walking on beach ");
        var r = _synth.Synthesize(new PromptContext(input.ToString(0, len), CameraMotion: "Orbit"));
        Assert.True(r.IsOverBudget);
        Assert.True(r.FinalPositivePrompt.Length <= PromptSynthesizerService.MaxCharacterLimit);
        Assert.Equal(r.FinalPositivePrompt.Length, r.CharacterCount);
        Assert.False(r.FinalPositivePrompt.EndsWith(' ') || r.FinalPositivePrompt.EndsWith(','));
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void ExactlyAtBudget_IsNotFlagged()
    {
        var r = _synth.Synthesize(new PromptContext(new string('a', 800)));
        Assert.False(r.IsOverBudget);
        Assert.Equal(800, r.CharacterCount);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void LockedIdentity_IsInjectedFirst_AndSurvivesTruncation()
    {
        var r = _synth.Synthesize(new PromptContext(new string('x', 1200), "FPV Drone", "Cyberpunk Neo", "Mina", IsIdentityLocked: true));
        Assert.StartsWith("[Subject: Mina]", r.FinalPositivePrompt);
        Assert.True(r.IsOverBudget);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void UnlockedIdentity_IsNotInjected_AndOrderIsMainCameraStyle()
    {
        var r = _synth.Synthesize(new PromptContext("a cat", "Orbit", "8K Photoreal", "Mina", IsIdentityLocked: false));
        Assert.DoesNotContain("[Subject:", r.FinalPositivePrompt);
        Assert.Equal("a cat, camera motion: Orbit, 8K Photoreal render style", r.FinalPositivePrompt);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void CameraOrStyleAlreadyInInput_IsNotDuplicated()
    {
        var r = _synth.Synthesize(new PromptContext("slow orbit around a lighthouse, watercolor", "Orbit", "Watercolor"));
        Assert.Equal("slow orbit around a lighthouse, watercolor", r.FinalPositivePrompt);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void Negatives_AreDeduplicated_CaseInsensitive_WithBabyTokens()
    {
        var r = _synth.Synthesize(new PromptContext("baby smiling", NegativeInput: "Low Quality, blurry, DISTORTED FACE, blurry, , harsh shadows", AutoBabyModifier: true));
        var tokens = r.FinalNegativePrompt.Split(", ");
        Assert.Equal(tokens.Length, new HashSet<string>(tokens, StringComparer.OrdinalIgnoreCase).Count);
        Assert.Equal("distorted face", tokens[0]);
        Assert.Contains("adult facial features", tokens);
        Assert.Contains("blurry", tokens);
        Assert.Single(tokens, t => t.Equals("harsh shadows", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void BabyTokens_AreOmitted_WhenModifierOff()
    {
        var r = _synth.Synthesize(new PromptContext("baby smiling"));
        Assert.DoesNotContain("adult facial features", r.FinalNegativePrompt);
    }

    // ── GeminiPromptEnhancer: Graceful Fallback ──
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; return Task.FromResult(f(request)); }
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task Enhancer_NoKey_ReturnsOriginal_WithoutNetwork()
    {
        var h = new StubHandler(_ => throw new InvalidOperationException());
        var e = new GeminiPromptEnhancer(new HttpClient(h), () => null);
        Assert.Equal("a cat", await e.EnhancePromptAsync("a cat", null, null));
        Assert.Equal(0, h.Calls);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task Enhancer_NetworkFailure_ReturnsOriginal()
    {
        var e = new GeminiPromptEnhancer(new HttpClient(new StubHandler(_ => throw new HttpRequestException("down"))), () => "k");
        Assert.Equal("a cat", await e.EnhancePromptAsync("a cat", "Watercolor", "Orbit"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Enhancer_ServerError_RetriesOnce_ThenReturnsOriginal()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var e = new GeminiPromptEnhancer(new HttpClient(h), () => "k");
        Assert.Equal("a cat", await e.EnhancePromptAsync("a cat", null, null));
        Assert.Equal(2, h.Calls);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task Enhancer_Success_ReturnsModelText_AndSendsKeyHeader()
    {
        string? key = null; string? url = null;
        var h = new StubHandler(req =>
        {
            key = req.Headers.TryGetValues("x-goog-api-key", out var v) ? v.First() : null;
            url = req.RequestUri!.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"candidates":[{"content":{"parts":[{"text":"\"a cat bathed in volumetric light\""}]}}]}"""),
            };
        });
        var e = new GeminiPromptEnhancer(new HttpClient(h), () => "secret");
        Assert.Equal("a cat bathed in volumetric light", await e.EnhancePromptAsync("a cat", null, null));
        Assert.Equal("secret", key);
        Assert.EndsWith("v1beta/models/gemini-2.5-flash:generateContent", url);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task Enhancer_Cancelled_ReturnsOriginal()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var e = new GeminiPromptEnhancer(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))), () => "k");
        Assert.Equal("a cat", await e.EnhancePromptAsync("a cat", null, null, cts.Token));
    }

    // ── PromptInputViewModel: T2V ↔ I2V · 비율 · 글자 수 통지 ──
    private static byte[] Png(int w, int h)
    {
        var b = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(16), w);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(20), h);
        return b;
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task ViewModel_ImageDrop_SwitchesToI2V_AndRemoveRevertsToT2V()
    {
        var vm = new PromptInputViewModel(_synth);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.Equal(PromptInputViewModel.T2VBadge, vm.StatusBadge);

        Assert.True(await vm.HandleImageDroppedAsync(new MemoryStream(Png(1024, 768))));
        Assert.Equal("WAN 2.1 · I2V READY", vm.StatusBadge);
        Assert.Equal("1024 / 768", vm.ReferenceAspectCss);
        Assert.Contains(nameof(vm.StatusBadge), changed);

        vm.RemoveReferenceImage();
        Assert.Equal("WAN 2.1 · T2V READY", vm.StatusBadge);
        Assert.Null(vm.ReferenceImageBytes);
        Assert.Null(vm.ReferenceAspectCss);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task ViewModel_EmptyStream_StaysT2V()
    {
        var vm = new PromptInputViewModel(_synth);
        Assert.False(await vm.HandleImageDroppedAsync(new MemoryStream()));
        Assert.False(vm.IsI2V);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void ViewModel_UserInput_NotifiesCharacterCount()
    {
        var vm = new PromptInputViewModel(_synth);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.UserInput = "hello";
        Assert.Equal(5, vm.CharacterCount);
        Assert.Contains(nameof(vm.CharacterCount), changed);
    }

    [Theory]
    [Trait("Category", "FastUnit")]
    [InlineData("1280*720", "1280x720 (16:9) HUD READY", "1280 / 720")]
    [InlineData("720*1280", "720x1280 (9:16) HUD READY", "720 / 1280")]
    [InlineData("960x960", "960x960 (1:1) HUD READY", "960 / 960")]
    [InlineData("garbage", "1280x720 (16:9) HUD READY", "1280 / 720")]
    public void ViewModel_Hud_FollowsOutputSize(string size, string text, string aspect)
    {
        var vm = new PromptInputViewModel(_synth);
        vm.SetOutputSize(size);
        Assert.Equal(text, vm.HudStatusText);
        Assert.Equal(aspect, vm.HudAspectCss);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void ImageSize_ReadsJpegSof()
    {
        byte[] jpg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x02, 0xD0, 0x05, 0x00, 0x03, 0, 0, 0, 0 };
        Assert.True(PromptInputViewModel.TryReadImageSize(jpg, out var w, out var h));
        Assert.Equal(1280, w);
        Assert.Equal(720, h);
    }
}
