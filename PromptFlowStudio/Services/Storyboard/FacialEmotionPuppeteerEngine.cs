using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>v16.20.0 장면 미세 표정 프리셋.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FacialEmotion>))]
public enum FacialEmotion { Neutral, HappySmile, Giggle, Surprised, Curious, Pout }

/// <summary>
/// v16.20.0 안면 랜드마크 가이드(−1~1) — 입꼬리 · 입 벌림 · 눈썹 · 눈 크기 · 볼 · 입술 오므림.
///   립싱크 입모양 진폭(<see cref="FacialEmotionPuppeteerEngine.MouthScaleOf"/>)과 프롬프트 강도 문구의 근거.
/// </summary>
public readonly record struct LandmarkGuide(double MouthCornerLift, double MouthOpen, double BrowRaise, double EyeOpen, double CheekRaise, double LipPucker)
{
    /// <summary>가장 큰 변위(절대값) — 안면 보호 상한 판정용.</summary>
    public double MaxMagnitude => Math.Max(Math.Max(Math.Max(Math.Abs(MouthCornerLift), Math.Abs(MouthOpen)), Math.Max(Math.Abs(BrowRaise), Math.Abs(EyeOpen))),
        Math.Max(Math.Abs(CheekRaise), Math.Abs(LipPucker)));

    public LandmarkGuide Scale(double k) => new(MouthCornerLift * k, MouthOpen * k, BrowRaise * k, EyeOpen * k, CheekRaise * k, LipPucker * k);
}

/// <summary>v16.20.0 표정 프리셋 정의 — 아이콘 · 라벨 · 기준 랜드마크 · 영문 표정 구문 · 상충(제거) 단어.</summary>
public sealed record EmotionPreset(FacialEmotion Emotion, string Icon, string Label, LandmarkGuide Guide, string Expression, IReadOnlyList<string> Conflicts);

/// <summary>v16.20.0 표정 주입 결과 — 보정 프롬프트 · 네거티브 · 적용 랜드마크(보호 상한 반영) · 실효 강도 · 아기 프로필 여부.</summary>
public readonly record struct EmotionInjection(string Prompt, string? Negative, LandmarkGuide Guide, double Intensity, bool BabyProfile, bool Changed);

/// <summary>
/// [PromptFlowStudio] v16.20.0 😀 아기/인물 미세 표정 퍼핏 엔진 — 장면 감정 키워드 → Wan 프롬프트 표정 구문 + 안면 랜드마크 가이드 + 얼굴 보호 네거티브(순수 함수 · 무상태).
///   ▸ 표정: 활짝 웃음 · 까르르 · 놀람 · 호기심 · 삐침/칭얼거림. 강도(0~1) → subtle/gentle/clear 3단 문구(“extreme” 류 과장 금지).
///   ▸ 안면 보호: 아기 프로필은 랜드마크 변위 상한 0.55(성인 0.8) · 실효 강도 상한 0.75 · 정체성 유지 구문(same face identity, consistent facial proportions)
///     + 왜곡 방지 네거티브(distorted face, deformed mouth, extra teeth …) 자동 결합 · 프롬프트의 상충 표정 단어 제거.
///   ▸ 연속성: <see cref="SmoothIntensities"/> 가 인접 컷 강도 급변(&gt;0.35)을 완화 — 컷 경계 표정 튐 방지.
///   ▸ 멱등: 이미 주입된 표정 구문/네거티브 토큰은 재추가하지 않음. Neutral 은 입력을 그대로 반환(기존 회귀 0).
/// </summary>
public static class FacialEmotionPuppeteerEngine
{
    public const double DefaultIntensity = 0.6;
    public const double BabyLandmarkCap = 0.55, AdultLandmarkCap = 0.8, BabyIntensityCap = 0.75;
    public const double MaxSceneDelta = 0.35;
    public const int MaxPromptChars = 1500;

    /// <summary>정체성 유지 구문(표정만 바꾸고 얼굴은 유지).</summary>
    public const string IdentityGuard = "same face identity, consistent facial proportions, natural symmetrical eyes, soft natural skin texture";

    /// <summary>왜곡 방지 네거티브 토큰.</summary>
    public static readonly IReadOnlyList<string> FaceProtectionNegatives = new[]
    {
        "distorted face", "deformed mouth", "asymmetrical eyes", "extra teeth", "melting face", "uncanny smile", "exaggerated grimace", "face morphing",
    };

    public static readonly IReadOnlyList<EmotionPreset> Presets = new[]
    {
        new EmotionPreset(FacialEmotion.Neutral, "😐", "기본", default, "", Array.Empty<string>()),
        new EmotionPreset(FacialEmotion.HappySmile, "😀", "활짝 웃음", new(0.8, 0.35, 0.2, 0.1, 0.7, 0),
            "bright happy smile with lifted cheeks and sparkling eyes", new[] { "crying", "sad", "frowning", "pouting", "angry", "scared" }),
        new EmotionPreset(FacialEmotion.Giggle, "🤭", "까르르", new(0.9, 0.6, 0.3, -0.2, 0.85, 0),
            "giggling laughter with crinkled eyes and an open-mouth grin", new[] { "crying", "sad", "frowning", "pouting", "angry", "serious" }),
        new EmotionPreset(FacialEmotion.Surprised, "😮", "놀람", new(0.1, 0.75, 0.9, 0.8, 0.1, 0.3),
            "surprised face with raised eyebrows, wide eyes and a small round open mouth", new[] { "sleepy", "bored", "eyes closed", "frowning" }),
        new EmotionPreset(FacialEmotion.Curious, "🧐", "호기심", new(0.25, 0.2, 0.55, 0.5, 0.15, 0.1),
            "curious wondering look with slightly raised eyebrows and attentive bright eyes", new[] { "bored", "sleepy", "eyes closed", "angry" }),
        new EmotionPreset(FacialEmotion.Pout, "🥺", "삐침/칭얼거림", new(-0.45, 0.05, -0.2, 0.2, -0.1, 0.7),
            "cute pouting lips with slightly furrowed brows and puppy-dog eyes", new[] { "laughing", "grinning", "big smile", "giggling" }),
    };

    // 감정 키워드(한/영) → 프리셋. 앞쪽이 우선.
    private static readonly (string Key, FacialEmotion Emotion)[] Keywords =
    {
        ("까르르", FacialEmotion.Giggle), ("깔깔", FacialEmotion.Giggle), ("킥킥", FacialEmotion.Giggle), ("giggl", FacialEmotion.Giggle), ("laugh", FacialEmotion.Giggle),
        ("삐침", FacialEmotion.Pout), ("삐쳐", FacialEmotion.Pout), ("칭얼", FacialEmotion.Pout), ("뾰로통", FacialEmotion.Pout), ("투정", FacialEmotion.Pout), ("pout", FacialEmotion.Pout), ("sulk", FacialEmotion.Pout), ("whin", FacialEmotion.Pout),
        ("깜짝", FacialEmotion.Surprised), ("놀라", FacialEmotion.Surprised), ("놀람", FacialEmotion.Surprised), ("우와", FacialEmotion.Surprised), ("surpris", FacialEmotion.Surprised), ("wow", FacialEmotion.Surprised), ("amaz", FacialEmotion.Surprised),
        ("호기심", FacialEmotion.Curious), ("궁금", FacialEmotion.Curious), ("신기", FacialEmotion.Curious), ("탐색", FacialEmotion.Curious), ("curious", FacialEmotion.Curious), ("wonder", FacialEmotion.Curious), ("explor", FacialEmotion.Curious),
        ("웃음", FacialEmotion.HappySmile), ("미소", FacialEmotion.HappySmile), ("방긋", FacialEmotion.HappySmile), ("행복", FacialEmotion.HappySmile), ("신나", FacialEmotion.HappySmile), ("smil", FacialEmotion.HappySmile), ("happy", FacialEmotion.HappySmile), ("joy", FacialEmotion.HappySmile),
    };

    private static readonly string[] BabyKeys = { "baby", "infant", "toddler", "newborn", "child", "kid", "아기", "아가", "신생아", "유아", "아이", "베이비" };

    public static EmotionPreset PresetOf(FacialEmotion e)
    {
        foreach (var p in Presets) if (p.Emotion == e) return p;
        return Presets[0];
    }

    /// <summary>장면 텍스트(프롬프트·나레이션) 감정 키워드 탐지 — 미검출 Neutral.</summary>
    public static FacialEmotion Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return FacialEmotion.Neutral;
        var s = text.AsSpan();
        foreach (var (key, emo) in Keywords)
            if (s.Contains(key.AsSpan(), StringComparison.OrdinalIgnoreCase)) return emo;
        return FacialEmotion.Neutral;
    }

    /// <summary>아기/유아 피사체 여부(보호 상한 강화).</summary>
    public static bool IsBabySubject(string? prompt)
    {
        if (string.IsNullOrEmpty(prompt)) return false;
        var s = prompt.AsSpan();
        foreach (var k in BabyKeys) if (s.Contains(k.AsSpan(), StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>안면 보호 실효 강도 — 0~1 클램프, 아기 상한 0.75.</summary>
    public static double SafeIntensity(double intensity, bool baby)
    {
        var v = double.IsFinite(intensity) ? Math.Clamp(intensity, 0, 1) : DefaultIntensity;
        return baby ? Math.Min(v, BabyIntensityCap) : v;
    }

    /// <summary>보호 상한 반영 랜드마크(최대 변위가 상한을 넘으면 균등 축소 → 표정 비율 유지).</summary>
    public static LandmarkGuide GuideOf(FacialEmotion e, double intensity, bool baby)
    {
        var g = PresetOf(e).Guide.Scale(SafeIntensity(intensity, baby));
        var cap = baby ? BabyLandmarkCap : AdultLandmarkCap;
        var m = g.MaxMagnitude;
        return m > cap ? g.Scale(cap / m) : g;
    }

    /// <summary>강도 → 표정 수식어.</summary>
    public static string IntensityWord(double safeIntensity) => safeIntensity < 0.4 ? "subtle" : safeIntensity < 0.75 ? "gentle" : "clear";

    /// <summary>립싱크 입모양 진폭 배율(0.6~1.25) — 입 벌림/오므림 가이드 기반.</summary>
    public static double MouthScaleOf(FacialEmotion e, double intensity = DefaultIntensity, bool baby = false)
    {
        if (e == FacialEmotion.Neutral) return 1.0;
        var g = GuideOf(e, intensity, baby);
        return Math.Clamp(1.0 + g.MouthOpen * 0.4 - g.LipPucker * 0.5, 0.6, 1.25);
    }

    /// <summary>
    /// 프롬프트 + 네거티브에 표정 주입(멱등 · 순수). Neutral 이면 입력 그대로.
    /// </summary>
    public static EmotionInjection Apply(string? prompt, string? negative, FacialEmotion emotion, double intensity = DefaultIntensity)
    {
        var p = (prompt ?? "").Trim();
        if (emotion == FacialEmotion.Neutral) return new EmotionInjection(p, negative, default, 0, false, false);
        var preset = PresetOf(emotion);
        var baby = IsBabySubject(p);
        var safe = SafeIntensity(intensity, baby);
        var phrase = IntensityWord(safe) + " " + preset.Expression;

        var cleaned = StripConflicts(p, preset.Conflicts);
        var sb = new StringBuilder(cleaned.Length + phrase.Length + IdentityGuard.Length + 8);
        sb.Append(cleaned);
        if (!ContainsIgnoreCase(cleaned, preset.Expression)) AppendClause(sb, phrase);
        if (!ContainsIgnoreCase(cleaned, IdentityGuard)) AppendClause(sb, IdentityGuard);
        var outPrompt = sb.Length > MaxPromptChars ? sb.ToString(0, MaxPromptChars) : sb.ToString();
        var neg = MergeNegative(negative);
        return new EmotionInjection(outPrompt, neg, GuideOf(emotion, safe, baby), safe, baby,
            !string.Equals(outPrompt, p, StringComparison.Ordinal) || !string.Equals(neg, negative, StringComparison.Ordinal));
    }

    /// <summary>네거티브에 얼굴 보호 토큰 결합(중복 제거 · 기존 순서 유지).</summary>
    public static string MergeNegative(string? negative)
    {
        var baseNeg = (negative ?? "").Trim().TrimEnd(',');
        var sb = new StringBuilder(baseNeg.Length + 160).Append(baseNeg);
        foreach (var t in FaceProtectionNegatives)
        {
            if (ContainsIgnoreCase(baseNeg, t)) continue;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(t);
        }
        return sb.ToString();
    }

    /// <summary>인접 컷 강도 급변 완화(제자리) — 이전 컷과 차이가 MaxSceneDelta 를 넘으면 경계로 끌어당김. 조정된 컷 수 반환.</summary>
    public static int SmoothIntensities(Span<double> intensities)
    {
        var changed = 0;
        for (var i = 1; i < intensities.Length; i++)
        {
            var prev = intensities[i - 1];
            var d = intensities[i] - prev;
            if (Math.Abs(d) <= MaxSceneDelta) continue;
            intensities[i] = prev + Math.Sign(d) * MaxSceneDelta;
            changed++;
        }
        return changed;
    }

    /// <summary>스토리보드 컷 표정 자동 할당(Neutral 컷만 · 프롬프트+나레이션 탐지). 할당 수 반환.</summary>
    public static int AutoAssign(IReadOnlyList<StoryboardScene> scenes)
    {
        var n = 0;
        foreach (var s in scenes)
        {
            if (s.Emotion != FacialEmotion.Neutral) continue;
            var e = Detect(s.VoiceText);
            if (e == FacialEmotion.Neutral) e = Detect(s.Prompt);
            if (e == FacialEmotion.Neutral) continue;
            s.Emotion = e;
            n++;
        }
        return n;
    }

    /// <summary>표시 라벨(아이콘 포함).</summary>
    public static string LabelOf(FacialEmotion e)
    {
        var p = PresetOf(e);
        return p.Icon + " " + p.Label;
    }

    /// <summary>랜드마크 요약(UI/로그).</summary>
    public static string Describe(LandmarkGuide g) =>
        "입꼬리 " + Sig(g.MouthCornerLift) + " · 입 " + Sig(g.MouthOpen) + " · 눈썹 " + Sig(g.BrowRaise) + " · 눈 " + Sig(g.EyeOpen) + " · 볼 " + Sig(g.CheekRaise) + " · 오므림 " + Sig(g.LipPucker);

    private static string Sig(double v) => (v >= 0 ? "+" : "") + v.ToString("0.00", CultureInfo.InvariantCulture);

    private static string StripConflicts(string prompt, IReadOnlyList<string> conflicts)
    {
        if (prompt.Length == 0 || conflicts.Count == 0) return prompt;
        string? s = null;
        foreach (var c in conflicts)
        {
            var cur = s ?? prompt;
            var idx = cur.IndexOf(c, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var sb = new StringBuilder(cur.Length);
            var from = 0;
            while (idx >= 0)
            {
                var wordStart = idx == 0 || !char.IsLetter(cur[idx - 1]);
                var end = idx + c.Length;
                var wordEnd = end >= cur.Length || !char.IsLetter(cur[end]);
                if (wordStart && wordEnd) { sb.Append(cur, from, idx - from); from = end; }
                idx = cur.IndexOf(c, end, StringComparison.OrdinalIgnoreCase);
            }
            sb.Append(cur, from, cur.Length - from);
            s = sb.ToString();
        }
        return s is null ? prompt : Tidy(s);
    }

    private static string Tidy(string s)
    {
        var sb = new StringBuilder(s.Length);
        var lastSpace = false;
        foreach (var ch in s)
        {
            if (ch == ' ') { if (!lastSpace) sb.Append(ch); lastSpace = true; continue; }
            if (ch == ',' && sb.Length > 0 && (sb[^1] == ',' || (sb[^1] == ' ' && sb.Length > 1 && sb[^2] == ','))) { lastSpace = false; continue; }
            sb.Append(ch);
            lastSpace = false;
        }
        return sb.ToString().Trim().Trim(',').Trim();
    }

    private static void AppendClause(StringBuilder sb, string clause)
    {
        if (sb.Length > 0 && sb[^1] != ',' && sb[^1] != '.') sb.Append(',');
        if (sb.Length > 0) sb.Append(' ');
        sb.Append(clause);
    }

    private static bool ContainsIgnoreCase(string s, string v) => s.Contains(v, StringComparison.OrdinalIgnoreCase);
}
