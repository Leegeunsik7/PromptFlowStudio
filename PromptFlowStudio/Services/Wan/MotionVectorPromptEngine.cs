using System;

namespace PromptFlowStudio.Services.Wan;

/// <summary>
/// [PromptFlowStudio] v16.5.0 🧭 Motion-Vector 프롬프트 엔진 — I2V 역방향 모션(뒷걸음·되감기) 오류 방지.
///   ▸ I2V 요청 네거티브에 역방향 방지 키워드를 멱등 결합(사용자 네거티브 원형 보존 · 앞에 유지).
///   ▸ 폐쇄 공간 구도(실내·복도·터널 등) 감지 + 카메라 무빙 미지정 → Dolly Out 카메라 무빙 자동 매핑.
///   ▸ 입력 스캔은 span IndexOf(OrdinalIgnoreCase)만 — LINQ·Regex 0. 결합이 필요할 때만 문자열 1회 생성.
///   순수 BCL · 모바일 링크.
/// </summary>
public static class MotionVectorPromptEngine
{
    /// <summary>역방향 모션 방지 네거티브 키워드(I2V 전용).</summary>
    public const string ReverseGuardNegative =
        "moving backwards, reverse motion, backward walking, rewinding, reversed playback, time reversal, moonwalking";

    /// <summary>폐쇄 공간 감지 시 자동 매핑하는 Dolly Out 카메라 무빙.</summary>
    public const string DollyOutCamera =
        "slow dolly out camera movement, camera smoothly pulling back, subject moving forward naturally";

    private const string ReverseMarker = "reverse motion";

    /// <summary>KF2V(첫/끝 프레임 보간) DashScope 모델.</summary>
    public const string KeyframeModel = "wan2.1-kf2v-plus";

    // 폐쇄 공간 구도 키워드(영/한).
    private static readonly string[] EnclosedKeywords =
    {
        "room", "corridor", "hallway", "interior", "indoor", "elevator", "tunnel", "cave", "closet",
        "bathroom", "kitchen", "bedroom", "office", "cabin", "basement", "staircase", "narrow alley",
        "방 안", "실내", "복도", "엘리베이터", "터널", "동굴", "침실", "주방", "욕실", "사무실", "지하실", "계단", "골목",
    };

    // 이미 카메라 무빙이 지정된 경우(사용자 의도 우선) 자동 매핑을 건너뛴다.
    private static readonly string[] CameraKeywords =
    {
        "dolly", "zoom", "pan ", "orbit", "drone", "crane", "panning", "tilt", "tracking shot", "crane", "orbit", "push in", "pull back",
        "static camera", "camera movement", "줌인", "줌아웃", "팬", "틸트", "돌리", "카메라가",
    };

    /// <summary>프롬프트가 폐쇄 공간 구도인지(키워드 스캔 · 0B).</summary>
    public static bool IsEnclosedSpace(string? prompt) => ContainsAny(prompt, EnclosedKeywords);

    /// <summary>프롬프트에 카메라 무빙이 이미 지정되어 있는지(0B).</summary>
    public static bool HasCameraMove(string? prompt) => ContainsAny(prompt, CameraKeywords);

    /// <summary>
    /// 폐쇄 공간 + 카메라 무빙 미지정 → 말미에 Dolly Out 결합. 그 외엔 원본 참조 그대로(0B · 멱등).
    /// </summary>
    public static string ApplyCameraCorrection(string? prompt)
    {
        var p = prompt ?? "";
        if (!IsEnclosedSpace(p) || HasCameraMove(p)) return p;
        var t = p.AsSpan().TrimEnd();
        if (t.Length > 0 && t[^1] == ',') t = t[..^1];
        return t.Length == 0 ? DollyOutCamera : string.Concat(t, ", ", DollyOutCamera);
    }

    /// <summary>
    /// 네거티브 말미에 역방향 방지 키워드 결합(멱등). 사용자 네거티브는 앞에 원형 유지.
    /// </summary>
    public static string AppendReverseGuard(string? negative)
    {
        var n = negative ?? "";
        if (n.AsSpan().IndexOf(ReverseMarker, StringComparison.OrdinalIgnoreCase) >= 0) return n;
        var t = n.AsSpan().Trim();
        if (t.Length > 0 && t[^1] == ',') t = t[..^1];
        return t.Length == 0 ? ReverseGuardNegative : string.Concat(t, ", ", ReverseGuardNegative);
    }

    /// <summary>요청이 첫/끝 프레임 이중 키프레임(보간) 모드인지 — 첫 프레임 + 끝 프레임 모두 있어야 성립.</summary>
    public static bool IsDualKeyframe(WanVideoRequest? request) =>
        request is not null
        && (!string.IsNullOrWhiteSpace(request.ImagePath) || !string.IsNullOrWhiteSpace(request.ImageUrl))
        && (!string.IsNullOrWhiteSpace(request.LastFramePath) || !string.IsNullOrWhiteSpace(request.LastFrameUrl));

    /// <summary>
    /// I2V(단일/이중 키프레임) 요청 가공 — 카메라 보정 프롬프트 + 역방향 방지 네거티브. t2v 는 원본 그대로 반환.
    /// </summary>
    public static WanVideoRequest Process(WanVideoRequest request)
    {
        var isI2v = !string.IsNullOrWhiteSpace(request.ImagePath) || !string.IsNullOrWhiteSpace(request.ImageUrl);
        if (!isI2v) return request;
        var prompt = ApplyCameraCorrection(request.Prompt);
        var negative = AppendReverseGuard(request.NegativePrompt);
        if (ReferenceEquals(prompt, request.Prompt) && ReferenceEquals(negative, request.NegativePrompt)) return request;
        return request with { Prompt = prompt, NegativePrompt = negative };
    }

    private static bool ContainsAny(string? text, string[] keys)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var s = text.AsSpan();
        for (int i = 0; i < keys.Length; i++)
            if (s.IndexOf(keys[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }
}
