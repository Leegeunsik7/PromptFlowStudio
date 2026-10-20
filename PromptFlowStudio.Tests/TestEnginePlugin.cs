using System;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Plugins.Sdk;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Tests;

/// <summary>v16.29.0 플러그인 프레임워크 테스트용 엔진 플러그인 — 테스트 DLL 사본을 collectible ALC 로 로드해 SDK 계약을 검증한다.</summary>
public sealed class TestEnginePlugin : IVideoEnginePlugin
{
    public string Id => "test-engine";
    public string Name => "Test Engine";
    public string Version => "1.0.0";
    public PluginKind Kind => PluginKind.VideoEngine;
    public bool Enabled { get; private set; }
    public EngineCapabilities Capabilities { get; } = new(0.5, 1, 0, 1920, 1080, 30, 10, true, false, new[] { "general" });

    public void Initialize(IPluginHost host) => Enabled = host.HostVersion.Length > 0;

    public Task<WanGenerationResult> GenerateAsync(WanVideoRequest request, CancellationToken ct)
        => Task.FromResult(new WanGenerationResult(true, WanTaskStatus.Succeeded, "plugin:" + request.Prompt, "ok", "p1", TimeSpan.Zero));
}
