using System;

namespace PromptFlowStudio.Services.Wan;

/// <summary>
/// [PromptFlowStudio] v15.4.1 DashScope 튜닝 어댑터 — 클라우드 Wan 2.1 은 steps/cfg/motion 파라미터를 받지 않으므로
///   <see cref="WanTuning"/> 값을 프롬프트 꼬리 품질 토큰(정적 리터럴)으로 변환해 결합한다(근사 반영 — 서버 샘플러 파라미터 자체는 불변).
///   ▸ 기본 구간(Steps 21~39 · CFG 3.5~9 미만 · Motion 96~160 · 스위치 off)은 토큰 없음 → 원본 참조 그대로(0B).
///   ▸ 결합 시 길이 1회 계산 후 <c>string.Create</c> 1회(중간 문자열 0), 이미 포함된 토큰은 생략, 800자 상한을 넘기는 토큰은 뒤에서부터 버린다.
/// </summary>
public static class DashScopeTuningAdapter
{
    public const int MaxPromptChars = 800;

    public const string StepsLow = "fast draft render";
    public const string StepsHigh = "ultra fine detail, high fidelity rendering";
    public const string CfgLow = "loose creative interpretation";
    public const string CfgHigh = "strictly faithful to the prompt, precise composition";
    public const string MotionStill = "very subtle gentle motion, nearly still camera";
    public const string MotionSoft = "soft slow motion";
    public const string MotionLively = "lively dynamic motion";
    public const string MotionEnergetic = "energetic fast dynamic motion";
    public const string FaceSharpen = "sharp detailed baby face, clear bright eyes, natural soft skin";
    public const string Interp60 = "smooth fluid 60fps motion, no frame stutter";

    public static string? StepsToken(int steps) => steps <= 20 ? StepsLow : steps >= 40 ? StepsHigh : null;
    public static string? CfgToken(float cfg) => cfg <= 3.5f ? CfgLow : cfg >= 9f ? CfgHigh : null;
    public static string? MotionToken(int bucket) => bucket switch
    {
        <= 60 => MotionStill,
        <= 95 => MotionSoft,
        <= 160 => null,
        <= 210 => MotionLively,
        _ => MotionEnergetic,
    };

    /// <summary>튜닝 토큰을 프롬프트 뒤에 ", " 로 결합. 붙일 토큰이 없으면 입력 참조 그대로 반환.</summary>
    public static string Apply(string prompt, WanTuning t)
    {
        prompt ??= "";
        var t0 = Keep(prompt, StepsToken(t.Steps));
        var t1 = Keep(prompt, CfgToken(t.CfgScale));
        var t2 = Keep(prompt, MotionToken(t.MotionBucket));
        var t3 = Keep(prompt, t.FaceSharpen ? FaceSharpen : null);
        var t4 = Keep(prompt, t.Interpolate60 ? Interp60 : null);

        var head = prompt.AsSpan().TrimEnd();
        while (head.Length > 0 && head[^1] == ',') head = head[..^1].TrimEnd();
        var headLen = head.Length;

        // 800자 상한: 넘치면 뒤 토큰부터 버린다(사용자 원문은 절대 자르지 않음).
        var len = Total(headLen, t0, t1, t2, t3, t4);
        if (len > MaxPromptChars) { t4 = null; len = Total(headLen, t0, t1, t2, t3, t4); }
        if (len > MaxPromptChars) { t3 = null; len = Total(headLen, t0, t1, t2, t3, t4); }
        if (len > MaxPromptChars) { t2 = null; len = Total(headLen, t0, t1, t2, t3, t4); }
        if (len > MaxPromptChars) { t1 = null; len = Total(headLen, t0, t1, t2, t3, t4); }
        if (len > MaxPromptChars) { t0 = null; len = headLen; }
        if (t0 is null && t1 is null && t2 is null && t3 is null && t4 is null) return prompt;

        return string.Create(len, (prompt, headLen, t0, t1, t2, t3, t4), static (dst, st) =>
        {
            st.prompt.AsSpan(0, st.headLen).CopyTo(dst);
            var pos = st.headLen;
            Put(dst, ref pos, st.t0); Put(dst, ref pos, st.t1); Put(dst, ref pos, st.t2); Put(dst, ref pos, st.t3); Put(dst, ref pos, st.t4);
        });
    }

    private static string? Keep(string prompt, string? token)
        => token is null || prompt.Contains(token, StringComparison.Ordinal) ? null : token;

    private static int Total(int headLen, string? a, string? b, string? c, string? d, string? e)
    {
        var len = headLen;
        len = Add(len, a); len = Add(len, b); len = Add(len, c); len = Add(len, d); len = Add(len, e);
        return len;
    }

    // 첫 조각이 비어 있으면 구분자 없이 시작.
    private static int Add(int len, string? token) => token is null ? len : len + (len > 0 ? 2 : 0) + token.Length;

    private static void Put(Span<char> dst, ref int pos, string? token)
    {
        if (token is null) return;
        if (pos > 0) { dst[pos++] = ','; dst[pos++] = ' '; }
        token.AsSpan().CopyTo(dst[pos..]);
        pos += token.Length;
    }
}
