using System;
using System.Collections.Generic;
using System.Text;
using PromptFlowStudio.Services.Pluto;

namespace PromptFlowStudio.Services.Prompt;

/// <summary>아기 시나리오 1종 — 키 · 이모지 · KO/EN/JA/ES 라벨 · 다국어 감지 키워드 · 시드 문장 · 자동 확장 조각(피사체/조명/카메라/분위기).</summary>
public sealed record BabyScenario(
    string Key, string Emoji,
    string LabelKo, string LabelEn, string LabelJa, string LabelEs,
    string[] Keywords, string Seed,
    string Subject, string Lighting, string Camera, string Atmosphere);

/// <summary>
/// [PromptFlowStudio] 👶 아기 테마 자동 프롬프트 확장기(순수 BCL · 상태 없음).
///   ▸ 짧은 아기 키워드 프롬프트(≤ <see cref="ShortPromptThreshold"/>자) 또는 아기 모드에서 시나리오를 KO/EN/JA/ES 키워드로 감지해
///     조명 · 부드러운 자연 카메라 모션 · 따뜻한 분위기 · 안전한 가족 친화 문구를 자동 결합한다.
///   ▸ 멱등: <see cref="SafetyPhrase"/> 가 이미 있으면 재확장하지 않음. 이미 포함된 조각은 중복 결합하지 않음.
///   ▸ 아기 토큰이 없고 아기 모드도 아니면 입력을 그대로 반환(일반 프롬프트 회귀 0).
/// </summary>
public static class BabyPromptAutoExpander
{
    /// <summary>자동 확장 대상 최대 길이 — 이보다 긴 프롬프트는 사용자가 직접 작성한 것으로 보고 보존.</summary>
    public const int ShortPromptThreshold = 80;

    /// <summary>안전 · 가족 친화 문구(멱등 마커 겸용).</summary>
    public const string SafetyPhrase = "safe wholesome family-friendly scene, baby gently supported and supervised";

    /// <summary>시나리오 미감지 시 기본 조명.</summary>
    public const string DefaultLighting = "soft warm natural window light, gentle diffused glow";

    /// <summary>시나리오 미감지 시 기본 카메라 모션.</summary>
    public const string DefaultCamera = "slow smooth natural camera drift, gentle eye-level push-in";

    /// <summary>시나리오 미감지 시 기본 분위기.</summary>
    public const string DefaultAtmosphere = "warm cozy atmosphere, soft pastel tones, shallow depth of field";

    /// <summary>공통 품질 꼬리.</summary>
    public const string QualityTail = "high detail, cinematic, temporally consistent";

    /// <summary>JA/ES 아기 토큰(KO/EN 은 <see cref="PromptDomainClassifier.BabyTokens"/> 재사용).</summary>
    public static readonly string[] ExtraBabyTokens = { "赤ちゃん", "ベビー", "幼児", "新生児", "bebé", "bebés", "recién nacido" };

    /// <summary>아기 시나리오 5종 — 첫걸음 · 낮잠 · 목욕/비눗방울 · 햇살 장난감 탐험 · 부모와 교감.</summary>
    public static readonly IReadOnlyList<BabyScenario> Scenarios = new[]
    {
        new BabyScenario("firststeps", "👣", "첫걸음 · 기어가기", "First Steps & Crawling", "はじめの一歩・はいはい", "Primeros pasos y gateo",
            new[] { "first step", "crawl", "walking", "toddling", "첫걸음", "걸음마", "기어", "아장아장", "一歩", "はいはい", "よちよち", "primeros pasos", "gatea", "gateo", "camina" },
            "아기 첫걸음",
            "toddler taking wobbly first steps on a soft play rug, tiny arms reaching forward",
            "soft morning sunlight through sheer curtains",
            "low eye-level tracking shot gently following each step",
            "proud joyful milestone moment in a warm living room"),
        new BabyScenario("naptime", "💤", "포근한 낮잠", "Cozy Naptime", "ほっこりお昼寝", "Siesta acogedora",
            new[] { "nap", "sleep", "asleep", "낮잠", "잠든", "자는", "수면", "새근", "お昼寝", "寝顔", "眠", "siesta", "dormid", "durmiendo" },
            "낮잠 자는 아기",
            "baby peacefully asleep in a cozy crib, tiny chest gently rising with each breath",
            "dim warm amber nightlight glow with soft falloff",
            "very slow subtle push-in, nearly still frame",
            "hushed tranquil lullaby mood, soft knitted blanket textures"),
        new BabyScenario("bath", "🫧", "보글보글 목욕", "Gentle Bath & Bubbles", "やさしいお風呂とシャボン玉", "Baño suave y burbujas",
            new[] { "bath", "bubble", "목욕", "비눗방울", "거품", "물놀이", "お風呂", "泡", "シャボン", "baño", "burbuja" },
            "목욕하는 아기",
            "giggling baby in a shallow warm bath with floating soap bubbles, a parent's hand gently supporting",
            "bright soft bathroom light with sparkling bubble highlights",
            "gentle slow orbit at bath-edge height",
            "playful fresh clean mood with iridescent bubbles"),
        new BabyScenario("toydiscovery", "🧸", "햇살 속 장난감 탐험", "Sunlit Toy Discovery", "陽だまりのおもちゃ探検", "Descubriendo juguetes al sol",
            new[] { "toy", "blocks", "teddy", "장난감", "블록", "인형", "곰인형", "おもちゃ", "積み木", "ぬいぐるみ", "juguete", "bloques", "peluche" },
            "장난감 가지고 노는 아기",
            "curious baby discovering a colorful wooden toy on a soft play mat",
            "warm sunlit afternoon beams with floating golden dust motes",
            "slow macro push-in with a tender rack focus to the eyes",
            "sense of wonder and curiosity in a soft pastel playroom"),
        new BabyScenario("parentbond", "🤱", "엄마 아빠와 교감", "Joyful Parent-Baby Moments", "親子のふれあい", "Momentos con mamá y papá",
            new[] { "mother", "father", "mommy", "daddy", "parent", "family", "엄마", "아빠", "부모", "가족", "ママ", "パパ", "親子", "mamá", "papá", "familia" },
            "엄마 아빠와 웃는 아기",
            "parent tenderly cuddling and playing peekaboo with a laughing baby",
            "golden hour backlight with a soft warm rim glow",
            "smooth slow arc around parent and baby",
            "heartfelt loving family bond, warm embrace"),
    };

    /// <summary>KO/EN/JA/ES 아기 토큰 포함 여부.</summary>
    public static bool IsBabyPrompt(string? prompt)
        => PromptDomainClassifier.ContainsBabyToken(prompt) || ContainsAny(prompt, ExtraBabyTokens);

    /// <summary>키로 시나리오 조회(대소문자 무시 · 없으면 null).</summary>
    public static BabyScenario? Get(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        for (var i = 0; i < Scenarios.Count; i++)
            if (string.Equals(Scenarios[i].Key, key, StringComparison.OrdinalIgnoreCase)) return Scenarios[i];
        return null;
    }

    /// <summary>프롬프트의 다국어 키워드로 시나리오 감지(선언 순서 우선 · 없으면 null).</summary>
    public static BabyScenario? MatchScenario(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return null;
        for (var i = 0; i < Scenarios.Count; i++)
            if (ContainsAny(prompt, Scenarios[i].Keywords)) return Scenarios[i];
        return null;
    }

    /// <summary>확장 여부 — 비어있지 않고 · 짧고 · 미확장이며 · (아기 모드 또는 아기 토큰).</summary>
    public static bool ShouldExpand(string? prompt, bool babyMode)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return false;
        var p = prompt.Trim();
        if (p.Length > ShortPromptThreshold) return false;
        if (p.Contains(SafetyPhrase, StringComparison.OrdinalIgnoreCase)) return false;
        return babyMode || IsBabyPrompt(p);
    }

    /// <summary>짧은 아기 프롬프트를 시나리오 맞춤 조각으로 자동 확장(대상이 아니면 트림된 입력 그대로).</summary>
    public static string Expand(string? prompt, bool babyMode = false)
    {
        var p = prompt?.Trim() ?? "";
        if (!ShouldExpand(p, babyMode)) return p;

        var s = MatchScenario(p);
        var sb = new StringBuilder(p.Length + 420);
        sb.Append(p);
        if (s is not null) Append(sb, p, s.Subject);
        Append(sb, p, s?.Lighting ?? DefaultLighting);
        Append(sb, p, s?.Camera ?? DefaultCamera);
        Append(sb, p, s?.Atmosphere ?? DefaultAtmosphere);
        Append(sb, p, QualityTail);
        Append(sb, p, SafetyPhrase);
        return sb.ToString();
    }

    /// <summary>시나리오 프리셋 원클릭 — 시드 문장을 확장한 완성형 프롬프트(키 미존재 시 빈 문자열).</summary>
    public static string ExpandScenario(string? key)
    {
        var s = Get(key);
        return s is null ? "" : Expand(s.Seed, babyMode: true);
    }

    /// <summary>로케일별 라벨(ko/en/ja/es · 그 외 → ko).</summary>
    public static string Label(BabyScenario scenario, string? locale)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        if (string.IsNullOrEmpty(locale)) return scenario.LabelKo;
        if (locale.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return scenario.LabelEn;
        if (locale.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) return scenario.LabelJa;
        if (locale.StartsWith("es", StringComparison.OrdinalIgnoreCase)) return scenario.LabelEs;
        return scenario.LabelKo;
    }

    private static void Append(StringBuilder sb, string original, string part)
    {
        if (part.Length == 0 || original.Contains(part, StringComparison.OrdinalIgnoreCase)) return;
        sb.Append(", ").Append(part);
    }

    private static bool ContainsAny(string? text, string[] tokens)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var span = text.AsSpan();
        for (var i = 0; i < tokens.Length; i++)
            if (span.IndexOf(tokens[i].AsSpan(), StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }
}
