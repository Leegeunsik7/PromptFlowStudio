using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PromptFlowStudio.Services.Wan;

// ══ v15.16.0 Zenith: Wan 와이어/볼트 DTO ═════════════════════════════════
// JsonObject(DOM) 대신 고정 스키마 DTO + 소스 생성 메타데이터 — 리플렉션·IL2026/IL3050 0.
// null 필드는 WhenWritingNull 로 생략(기존 조건부 포함 의미 그대로).

/// <summary>DashScope video-synthesis 요청 본문.</summary>
internal sealed class DashScopeSubmitBody
{
    [JsonPropertyName("model")]      public string Model { get; set; } = "";
    [JsonPropertyName("input")]      public DashScopeInput Input { get; set; } = new();
    [JsonPropertyName("parameters")] public DashScopeParameters Parameters { get; set; } = new();
}

internal sealed class DashScopeInput
{
    [JsonPropertyName("prompt")]          public string? Prompt { get; set; }
    [JsonPropertyName("negative_prompt")] public string? NegativePrompt { get; set; }
    [JsonPropertyName("img_url")]         public string? ImgUrl { get; set; }
    [JsonPropertyName("first_frame_url")] public string? FirstFrameUrl { get; set; }   // v16.5.0 kf2v
    [JsonPropertyName("last_frame_url")]  public string? LastFrameUrl { get; set; }    // v16.5.0 kf2v
}

internal sealed class DashScopeParameters
{
    [JsonPropertyName("resolution")]    public string? Resolution { get; set; }
    [JsonPropertyName("size")]          public string? Size { get; set; }
    [JsonPropertyName("prompt_extend")] public bool PromptExtend { get; set; }
    [JsonPropertyName("watermark")]     public bool? Watermark { get; set; }
    [JsonPropertyName("seed")]          public int? Seed { get; set; }
}

/// <summary>SiliconFlow /v1/video/submit 요청 본문(i2v image 필드는 Base64ImageJsonContent 가 스트리밍 부착).</summary>
internal sealed class SiliconFlowSubmitBody
{
    [JsonPropertyName("model")]           public string Model { get; set; } = "";
    [JsonPropertyName("prompt")]          public string Prompt { get; set; } = "";
    [JsonPropertyName("image_size")]      public string ImageSize { get; set; } = "";
    [JsonPropertyName("negative_prompt")] public string? NegativePrompt { get; set; }
    [JsonPropertyName("seed")]            public int? Seed { get; set; }
}

/// <summary>SiliconFlow /v1/video/status 요청 본문.</summary>
internal sealed record SiliconFlowStatusBody([property: JsonPropertyName("requestId")] string RequestId);

/// <summary>
/// Wan API·키 볼트 전용 소스 생성 컨텍스트. 볼트 파일 포맷(PascalCase Keys/Endpoints)과 100% 호환.
/// </summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DashScopeSubmitBody))]
[JsonSerializable(typeof(SiliconFlowSubmitBody))]
[JsonSerializable(typeof(SiliconFlowStatusBody))]
[JsonSerializable(typeof(WanVideoRequest))]
[JsonSerializable(typeof(WanTokenVault.VaultFile), TypeInfoPropertyName = "VaultFile")]
[JsonSerializable(typeof(Dictionary<string, string[]>), TypeInfoPropertyName = "KeyMap")]
[JsonSerializable(typeof(Dictionary<string, string>), TypeInfoPropertyName = "EndpointMap")]
[JsonSerializable(typeof(string[]))]
internal sealed partial class WanJsonContext : JsonSerializerContext
{
}
