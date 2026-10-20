using System;

namespace PromptFlowStudio.Services.Wan;

/// <summary>v16.9.0: 프롬프트에서 감지된 피사체 컨텍스트(비트 플래그).</summary>
[Flags]
public enum GuardContext
{
    None = 0,
    Human = 1,
    Object = 2,
    Nature = 4,
}

/// <summary>
/// [PromptFlowStudio] v16.9.0 🛡️ Artifact Guard Engine — AI 비디오 고질 결함(해부학 붕괴·모핑·물리 오류·정지 영상) 자동 방어.
///   ▸ GUARD-1: 경량 키워드 매칭으로 대상(아기/사람 · 사물/공간 · 자연) 감지 → 해부학/물리법칙 네거티브 멱등 병합.
///   ▸ GUARD-2: 모든 I2V/T2V 요청에 시간적 일관성 포지티브 + 모핑·깜빡임 억제 네거티브 기본 탑재.
///   ▸ GUARD-3: 모션 강도가 낮거나(MotionBucket ≤ 60) 모션 서술이 없으면 컨텍스트별 미세 생동감 키워드를 포지티브 후미에 결합.
///   ▸ 사용자 프롬프트/네거티브는 앞에 원형 유지 · 마커로 멱등 · 포지티브는 서버 상한(800자) 초과 시 결합 생략(사용자 텍스트 절단 방지).
///   ▸ 입력 스캔은 span IndexOf(OrdinalIgnoreCase)만 — LINQ·Regex 0. 결합이 필요할 때만 문자열 1회 생성. 순수 BCL · 모바일 링크.
/// </summary>
public static class ContextAwarePromptGuard
{
    /// <summary>인물(아기/사람) 감지 시 해부학 방어 네거티브.</summary>
    public const string AnatomyNegative = "mutated hands, extra limbs, extra fingers, deformed, disfigured face, bad anatomy, fused fingers";

    /// <summary>사물/공간·자연 감지 시 물리법칙 방어 네거티브.</summary>
    public const string PhysicsNegative = "floating objects, incorrect scale, distorted perspective, impossible physics, warped geometry";

    /// <summary>형태 붕괴·깜빡임 억제 네거티브(기본 탑재).</summary>
    public const string TemporalNegative = "melting, morphing, flickering, unstable texture, temporal inconsistency, shape shifting";

    /// <summary>시간적 일관성 강제 포지티브(기본 탑재).</summary>
    public const string TemporalPositive = "temporally consistent, stable textures, consistent identity across frames";

    /// <summary>인물 미세 모션.</summary>
    public const string HumanMicroMotion = "subtle breathing, gentle hair movement in the breeze, natural blinking, natural physics";

    /// <summary>자연 미세 모션.</summary>
    public const string NatureMicroMotion = "leaves gently swaying in the breeze, soft drifting light, natural physics";

    /// <summary>사물/기본 미세 모션.</summary>
    public const string AmbientMicroMotion = "subtle ambient motion, soft light shifting, natural physics";

    /// <summary>미세 모션을 강제하는 저모션 임계값(DashScopeTuningAdapter.MotionStill 구간과 동일).</summary>
    public const int LowMotionBucket = 60;

    /// <summary>서버 프롬프트 상한(WanApiService 800자 절단과 동일).</summary>
    public const int MaxPromptLength = 800;

    private const string AnatomyMarker = "extra limbs";
    private const string PhysicsMarker = "incorrect scale";
    private const string TemporalNegMarker = "morphing";
    private const string TemporalPosMarker = "temporally consistent";
    private const string MicroMotionMarker = "natural physics";

    private static readonly string[] HumanKeywords =
    {
        "baby", "infant", "toddler", "child", "kid", "girl", "boy", "woman", "man ", "person", "people", "face",
        "portrait", "family", "mother", "father", "mom", "dad", "dancer", "hands",
        "아기", "아이", "유아", "소녀", "소년", "여자", "남자", "사람", "인물", "얼굴", "엄마", "아빠", "가족", "손",
    };

    private static readonly string[] ObjectKeywords =
    {
        "car", "cup", "table", "chair", "toy", "ball", "bottle", "product", "furniture", "building", "room",
        "kitchen", "interior", "street", "city", "bridge", "robot", "vehicle", "object",
        "자동차", "컵", "테이블", "의자", "장난감", "제품", "가구", "건물", "방 안", "주방", "실내", "거리", "도시",
    };

    private static readonly string[] NatureKeywords =
    {
        "forest", "tree", "flower", "garden", "ocean", "sea", "beach", "river", "lake", "mountain", "sky", "cloud",
        "grass", "field", "waterfall", "snow", "rain", "sunset", "nature", "leaves", "wind",
        "숲", "나무", "꽃", "정원", "바다", "해변", "호수", "하늘", "구름", "잔디", "들판", "폭포", "노을", "자연",
    };

    // 이미 모션이 서술된 경우(사용자 의도 우선) 미세 모션 결합을 건너뛴다(저모션 튜닝은 예외).
    private static readonly string[] MotionKeywords =
    {
        "walking", "running", "dancing", "jumping", "waving", "swaying", "flowing", "moving", "breathing", "blowing",
        "spinning", "playing", "crawling", "laughing", "걷", "뛰", "춤", "흔들", "움직", "날리", "흐르", "기어",
    };

    /// <summary>프롬프트 컨텍스트 감지(키워드 스캔 · 0B).</summary>
    public static GuardContext Detect(string? prompt)
    {
        var c = GuardContext.None;
        if (ContainsAny(prompt, HumanKeywords)) c |= GuardContext.Human;
        if (ContainsAny(prompt, ObjectKeywords)) c |= GuardContext.Object;
        if (ContainsAny(prompt, NatureKeywords)) c |= GuardContext.Nature;
        return c;
    }

    /// <summary>프롬프트에 모션이 이미 서술되어 있는지(0B).</summary>
    public static bool HasMotion(string? prompt) => ContainsAny(prompt, MotionKeywords);

    /// <summary>GUARD-1/2: 네거티브 말미에 시간적 일관성 + 컨텍스트별 방어 키워드 멱등 결합. 추가 없으면 원본 참조 그대로.</summary>
    public static string ApplyNegative(string? negative, GuardContext context)
    {
        var n = negative ?? "";
        var s = n.AsSpan();
        var needTemporal = s.IndexOf(TemporalNegMarker, StringComparison.OrdinalIgnoreCase) < 0;
        var needAnatomy = (context & GuardContext.Human) != 0 && s.IndexOf(AnatomyMarker, StringComparison.OrdinalIgnoreCase) < 0;
        var needPhysics = (context & (GuardContext.Object | GuardContext.Nature)) != 0 && s.IndexOf(PhysicsMarker, StringComparison.OrdinalIgnoreCase) < 0;
        if (!needTemporal && !needAnatomy && !needPhysics) return n;

        var t = s.Trim();
        if (t.Length > 0 && t[^1] == ',') t = t[..^1];
        return string.Concat(
            string.Concat(t, t.Length > 0 && needTemporal ? ", " : "", needTemporal ? TemporalNegative : ""),
            (t.Length > 0 || needTemporal) && needAnatomy ? ", " : "", needAnatomy ? AnatomyNegative : "",
            string.Concat((t.Length > 0 || needTemporal || needAnatomy) && needPhysics ? ", " : "", needPhysics ? PhysicsNegative : ""));
    }

    /// <summary>
    /// GUARD-2/3: 포지티브 말미에 시간적 일관성 + (필요 시) 미세 모션 결합. 빈 프롬프트(i2v 선택)는 보존 · 상한 초과 시 결합 생략.
    /// </summary>
    public static string ApplyPositive(string? prompt, GuardContext context, int? motionBucket)
    {
        var p = prompt ?? "";
        var t = p.AsSpan().TrimEnd();
        if (t.Length > 0 && t[^1] == ',') t = t[..^1];
        if (t.Length == 0) return p;

        var needTemporal = t.IndexOf(TemporalPosMarker, StringComparison.OrdinalIgnoreCase) < 0;
        var lowMotion = motionBucket is int b && b <= LowMotionBucket;
        var needMotion = t.IndexOf(MicroMotionMarker, StringComparison.OrdinalIgnoreCase) < 0 && (lowMotion || !HasMotion(p));
        var motion = (context & GuardContext.Human) != 0 ? HumanMicroMotion
            : (context & GuardContext.Nature) != 0 ? NatureMicroMotion : AmbientMicroMotion;

        var total = t.Length + (needTemporal ? 2 + TemporalPositive.Length : 0);
        if (needTemporal && total > MaxPromptLength) { needTemporal = false; total = t.Length; }
        if (needMotion && total + 2 + motion.Length > MaxPromptLength) needMotion = false;
        if (!needTemporal && !needMotion) return p;

        return string.Concat(
            string.Concat(t, needTemporal ? ", " : "", needTemporal ? TemporalPositive : ""),
            needMotion ? ", " : "", needMotion ? motion : "");
    }

    /// <summary>I2V/T2V 최종 요청 가공 — 컨텍스트 감지 후 포지티브/네거티브 보정. 변경 없으면 원본 참조 그대로.</summary>
    public static WanVideoRequest Process(WanVideoRequest request)
    {
        var context = Detect(request.Prompt);
        int? bucket = request.Tuning is WanTuning tu ? tu.MotionBucket : null;
        var prompt = ApplyPositive(request.Prompt, context, bucket);
        var negative = ApplyNegative(request.NegativePrompt, context);
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
