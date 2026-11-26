namespace PromptFlowStudio.Services.Wan;

/// <summary>DashScope Wan 모델 계열 — 2.1(5초 고정) · 2.6(최대 15초 원테이크 · parameters.duration).</summary>
public enum WanModelFamily : byte { Wan21, Wan26 }

/// <summary>
/// [PromptFlowStudio] Wan 모델 카탈로그(순수 함수 · 0B 상수).
///   ▸ Wan 2.6: <c>wan2.6-t2v</c> / <c>wan2.6-i2v</c> — <c>parameters.duration</c> 5/10/15 지원(네이티브 15초 원테이크).
///   ▸ Wan 2.1: <c>wan2.1-t2v-turbo</c> / <c>wan2.1-i2v-turbo</c> — 5초 고정(duration 생략).
///   ▸ 계정/엔드포인트가 2.6 을 거부(모델 미지원·미개통·미존재)하면 <see cref="ToWan21Fallback"/> 로 5초 요청 1회 재제출.
/// </summary>
public static class WanModelCatalog
{
    public const string Wan26T2v = "wan2.6-t2v";
    public const string Wan26I2v = "wan2.6-i2v";
    public const string Wan21T2v = "wan2.1-t2v-turbo";
    public const string Wan21I2v = "wan2.1-i2v-turbo";
    public const int Wan21MaxSeconds = 5;
    public const int Wan26MaxSeconds = 15;
    public const string FallbackNotice = "Wan 2.6 미지원 계정/엔드포인트 → Wan 2.1 5초로 자동 폴백";

    public static string Label(WanModelFamily f) => f == WanModelFamily.Wan26 ? "Wan 2.6 (15초 원테이크 지원)" : "Wan 2.1 (5초 기본)";

    public static int MaxNativeSeconds(WanModelFamily f) => f == WanModelFamily.Wan26 ? Wan26MaxSeconds : Wan21MaxSeconds;

    public static string ModelFor(WanModelFamily f, bool isI2v) => f == WanModelFamily.Wan26
        ? (isI2v ? Wan26I2v : Wan26T2v)
        : (isI2v ? Wan21I2v : Wan21T2v);

    /// <summary>"Wan26"/"wan2.6"/"2.6" → Wan26, 그 외 Wan21.</summary>
    public static WanModelFamily ParseFamily(string? s)
    {
        var v = (s ?? "").Trim();
        return v.Equals(nameof(WanModelFamily.Wan26), StringComparison.OrdinalIgnoreCase)
            || v.StartsWith("wan2.6", StringComparison.OrdinalIgnoreCase)
            || v.Equals("2.6", StringComparison.Ordinal)
            ? WanModelFamily.Wan26 : WanModelFamily.Wan21;
    }

    public static bool IsWan26(string? model) => model is { Length: > 0 } m && m.StartsWith("wan2.6", StringComparison.OrdinalIgnoreCase);

    /// <summary>DashScope 클립 길이 — 2.6 은 5/10/15 로 스냅(상한 15), 2.1 은 null(필드 생략 · 5초 고정).</summary>
    public static int? DurationFor(WanModelFamily f, double seconds)
    {
        if (f != WanModelFamily.Wan26 || seconds <= Wan21MaxSeconds + 0.5) return null;
        return seconds > 10.5 ? 15 : 10;
    }

    /// <summary>
    /// 모델 미지원 오류 판정 — <c>Model.NotSupported</c>·<c>ModelNotFound</c>·<c>model … not exist/not found/not support/unsupported/access denied</c>.
    /// 쿼터/인증/네트워크 오류는 false(폴백이 아닌 기존 페일오버 경로).
    /// </summary>
    public static bool IsUnsupportedModelError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        var m = message;
        if (m.Contains("Model.NotSupported", StringComparison.OrdinalIgnoreCase)
            || m.Contains("ModelNotFound", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Model.AccessDenied", StringComparison.OrdinalIgnoreCase)
            || m.Contains("model_not_found", StringComparison.OrdinalIgnoreCase)) return true;
        if (!m.Contains("model", StringComparison.OrdinalIgnoreCase)) return false;
        return m.Contains("not exist", StringComparison.OrdinalIgnoreCase)
            || m.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || m.Contains("not support", StringComparison.OrdinalIgnoreCase)
            || m.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
            || m.Contains("access denied", StringComparison.OrdinalIgnoreCase)
            || m.Contains("invalid model", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>2.6 요청 → 2.1 5초 폴백 요청(모델 교체 · duration 생략 · 나머지 보존).</summary>
    public static WanVideoRequest ToWan21Fallback(WanVideoRequest request, bool isI2v)
        => request with { Model = ModelFor(WanModelFamily.Wan21, isI2v), Duration = null };
}
