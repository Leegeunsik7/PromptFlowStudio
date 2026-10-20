using System.Globalization;
using System.Numerics;
using System.Text;
using PromptFlowStudio.Services.Cinematography;

namespace PromptFlowStudio.Services.Localization.Dubbing;

public enum SubtitlePlacement : byte { Bottom, Top, BottomPlate }

[Flags]
public enum SubtitleIssue : ushort
{
    None = 0, IdiomAdapted = 1, HumorAdapted = 2, SensitiveReplaced = 4, FormalityApplied = 8, CpsExceeded = 16, LineTooLong = 32,
    TooShort = 64, FaceOverlap = 128, Rtl = 256, Relocated = 512, LiteralIdiom = 1024,
}

/// <summary>적응 결과(제어 평면 문자열 + 점검 플래그).</summary>
public sealed class LocalizedLine
{
    public string Display = "", Roman = "", Line1 = "", Line2 = "";
    public SubtitleIssue Issues;
    public float Cps, Duration;
    public int Chars, Replacements;
    public string Notes = "";
}

/// <summary>
/// [PromptFlow Studio v16.89.0] 🧭 문화적 컨텍스트 적응 &amp; 자막 로컬라이제이션 가드(적응 = 제어 평면 · 배치 점검 = 무할당).
///   ▸ 관용구/유머: 토큰({easy} {funny}) → 대상 문화 등가 표현(직역 모드 비교 · 유머 = 의미 기반 대체) · 경어({thanks}: 관계 = 반말/존대) · 민감 표현(대상 지역 마스크 → 대체 + 사유).
///   ▸ 가독성: 언어별 최대 CPS(라틴 17 · CJK 9 등) · 줄당 글자(42/16) · 2줄 균형 분할(공백/구두점 · CJK = 글자 경계) · 최소 표시 0.83 s · RTL 표시.
///   ▸ 배치(v16.85 구도 결선): 프레이밍 결과(눈 위치 · 정수리 · 머리 점유) → 얼굴/턱 박스 · 하단 안전 영역(아래 8%) 자막 박스 겹침 → 상단 이동 → 상단도 겹치면 하단 + 반투명 판.
/// </summary>
public sealed class CulturalLocalizationSubtitleGuard
{
    public const float SafeMargin = 0.08f, LineHeight = 0.065f, MinDuration = 0.83f;
    private readonly StringBuilder _sb = new(256);

    public bool AdaptIdioms { get; set; } = true;
    public bool FormalRegister { get; set; }
    public bool ReplaceSensitive { get; set; } = true;

    // ── 배치 계측(무할당) ──
    public SubtitlePlacement Placement { get; private set; }
    public Vector4 FaceBox { get; private set; }
    public Vector4 SubtitleBox { get; private set; }
    public float OverlapArea { get; private set; }
    public long Relocations { get; private set; }
    public long Checks { get; private set; }

    public static bool IsCjk(ScriptKind s) => s == ScriptKind.Cjk;

    /// <summary>대사 적응(제어 평면): 템플릿 → 관용구/유머/경어/민감 표현 → 줄 분할 · CPS 점검.</summary>
    public LocalizedLine Adapt(DubLanguage lang, int line, float duration, LocalizedLine? reuse = null)
    {
        var r = reuse ?? new LocalizedLine();
        r.Issues = SubtitleIssue.None; r.Replacements = 0; r.Notes = "";
        var prof = GlobalLanguageCatalog.Profile(lang);
        var tpl = GlobalLanguageCatalog.Text[(int)lang][line];
        string disp = tpl.Display, rom = tpl.Roman;
        var notes = new StringBuilder();
        for (var k = 0; k < GlobalLanguageCatalog.IdiomTokens.Length; k++)
        {
            var tok = GlobalLanguageCatalog.IdiomTokens[k];
            if (!disp.Contains(tok, StringComparison.Ordinal)) continue;
            var e = GlobalLanguageCatalog.Idioms[k][(int)lang];
            var adapted = AdaptIdioms || lang == DubLanguage.Ko;
            disp = disp.Replace(tok, adapted ? e.Adapted : e.Literal, StringComparison.Ordinal);
            rom = rom.Replace(tok, adapted ? e.AdaptedRoman : e.LiteralRoman, StringComparison.Ordinal);
            if (lang != DubLanguage.Ko)
            {
                if (adapted) { r.Issues |= GlobalLanguageCatalog.IdiomIsHumor[k] ? SubtitleIssue.HumorAdapted : SubtitleIssue.IdiomAdapted; notes.Append(GlobalLanguageCatalog.IdiomIsHumor[k] ? "유머 등가: " : "관용구 등가: ").Append(e.Adapted).Append(" (직역 ").Append(e.Literal).Append(") · "); }
                else { r.Issues |= SubtitleIssue.LiteralIdiom; notes.Append("⚠ 직역: ").Append(e.Literal).Append(" · "); }
                r.Replacements++;
            }
        }
        if (disp.Contains("{thanks}", StringComparison.Ordinal))
        {
            var t = GlobalLanguageCatalog.Thanks[(int)lang];
            disp = disp.Replace("{thanks}", FormalRegister ? t.Formal : t.Casual, StringComparison.Ordinal);
            rom = rom.Replace("{thanks}", FormalRegister ? t.FormalRoman : t.CasualRoman, StringComparison.Ordinal);
            if (FormalRegister && prof.Formality) { r.Issues |= SubtitleIssue.FormalityApplied; notes.Append("경어 적용 · "); }
        }
        foreach (var s in GlobalLanguageCatalog.Sensitive)
        {
            if (!disp.Contains(s.Token, StringComparison.Ordinal)) continue;
            var hit = (s.LangMask & (1 << (int)lang)) != 0 && ReplaceSensitive;
            disp = disp.Replace(s.Token, hit ? s.Replacement : s.Default, StringComparison.Ordinal);
            if (hit) { r.Issues |= SubtitleIssue.SensitiveReplaced; r.Replacements++; notes.Append("민감 표현 대체: ").Append(s.Default).Append("→").Append(s.Replacement).Append(" (").Append(s.Reason).Append(") · "); }
        }
        r.Display = disp; r.Roman = rom;
        // 가독성(텍스트 요소 = 그래핌 단위)
        var chars = new StringInfo(disp).LengthInTextElements;
        r.Chars = chars; r.Duration = MathF.Max(duration, MinDuration);
        r.Cps = chars / r.Duration;
        if (duration < MinDuration) r.Issues |= SubtitleIssue.TooShort;
        if (r.Cps > prof.MaxCps) r.Issues |= SubtitleIssue.CpsExceeded;
        Split(disp, prof, r);
        if (prof.Rtl) r.Issues |= SubtitleIssue.Rtl;
        r.Notes = notes.Length > 0 ? notes.ToString(0, notes.Length - 3) : "";
        return r;
    }

    /// <summary>실제 더빙 길이 반영(무할당): 표시 시간 · CPS · 최소 표시 재판정.</summary>
    public static void UpdateTiming(LocalizedLine r, DubLanguage lang, float duration)
    {
        var prof = GlobalLanguageCatalog.Profile(lang);
        r.Duration = MathF.Max(duration, MinDuration);
        r.Cps = r.Chars / r.Duration;
        r.Issues &= ~(SubtitleIssue.CpsExceeded | SubtitleIssue.TooShort);
        if (duration < MinDuration) r.Issues |= SubtitleIssue.TooShort;
        if (r.Cps > prof.MaxCps) r.Issues |= SubtitleIssue.CpsExceeded;
    }

    /// <summary>2줄 균형 분할(중앙 근처 공백/구두점 · CJK = 글자 경계).</summary>
    private static void Split(string text, in LanguageProfile prof, LocalizedLine r)
    {
        var len = new StringInfo(text).LengthInTextElements;
        if (len <= prof.MaxLineChars) { r.Line1 = text; r.Line2 = ""; return; }
        var mid = text.Length / 2; var best = -1;
        for (var d = 0; d < text.Length / 2 && best < 0; d++)
        {
            foreach (var p in new[] { mid - d, mid + d })
            {
                if (p <= 0 || p >= text.Length) continue;
                var c = text[p];
                if (c == ' ' || (IsCjk(prof.Script) && (c is '、' or '，' or '。' or '！' or '？' || char.IsLetter(c)))) { best = p; break; }
            }
        }
        if (best < 0) best = mid;
        var cut = text[best] == ' ' ? best : best + (IsCjk(prof.Script) && text[best] is '、' or '，' ? 1 : 0);
        r.Line1 = text[..cut].Trim(); r.Line2 = text[cut..].Trim();
        if (Math.Max(new StringInfo(r.Line1).LengthInTextElements, new StringInfo(r.Line2).LengthInTextElements) > prof.MaxLineChars) r.Issues |= SubtitleIssue.LineTooLong;
    }

    private static float Overlap(Vector4 a, Vector4 b)
    {
        var w = MathF.Min(a.Z, b.Z) - MathF.Max(a.X, b.X); var h = MathF.Min(a.W, b.W) - MathF.Max(a.Y, b.Y);
        return w > 0f && h > 0f ? w * h : 0f;
    }

    /// <summary>
    /// 자막 배치 점검(무할당): v16.85 프레이밍 → 얼굴 박스(x0, y0, x1, y1 정규) · 자막 박스(줄 수 · 글자 폭) → 하단/상단/판.
    /// aspect = 화면비(가로/세로).
    /// </summary>
    public SubtitleIssue Place(in FramingResult framing, int lines, int maxLineChars, bool cjk, float aspect)
    {
        Checks++;
        var issues = SubtitleIssue.None;
        Vector4 face = default;
        if (framing.Visible)
        {
            var hf = MathF.Max(framing.HeadFraction, 0.02f);
            var halfW = hf * 0.45f / aspect;
            // 정수리 → 턱 아래(머리 높이 1.25배 · 입/턱 포함)
            face = new Vector4(framing.Eye.X - halfW, MathF.Max(0f, framing.HeadTop), framing.Eye.X + halfW, MathF.Min(1f, framing.HeadTop + hf * 1.25f));
        }
        FaceBox = face;
        var charW = (cjk ? 0.036f : 0.019f) * 16f / 9f / aspect;
        var width = MathF.Min(0.9f, maxLineChars * charW + 0.04f);
        var h = lines * LineHeight + 0.02f;
        var bottom = new Vector4(0.5f - width * 0.5f, 1f - SafeMargin - h, 0.5f + width * 0.5f, 1f - SafeMargin);
        var top = new Vector4(bottom.X, SafeMargin, bottom.Z, SafeMargin + h);
        var ob = framing.Visible ? Overlap(face, bottom) : 0f;
        var prev = Placement;
        if (ob <= 0f) { Placement = SubtitlePlacement.Bottom; SubtitleBox = bottom; OverlapArea = 0f; }
        else
        {
            issues |= SubtitleIssue.FaceOverlap;
            var ot = Overlap(face, top);
            if (ot <= 0f) { Placement = SubtitlePlacement.Top; SubtitleBox = top; OverlapArea = 0f; issues |= SubtitleIssue.Relocated; }
            else { Placement = SubtitlePlacement.BottomPlate; SubtitleBox = bottom; OverlapArea = ob; }
        }
        if (Placement != prev) Relocations++;
        return issues;
    }
}
