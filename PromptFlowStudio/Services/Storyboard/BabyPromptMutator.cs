using System;
using System.Collections.Generic;
using System.Linq;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>예술 텍스처 스타일 — 키/이모지/한글명/영문 프롬프트 조각(Wan 주입용).</summary>
public sealed record ArtTexture(string Key, string Emoji, string Name, string Fragment);

/// <summary>
/// [PromptFlowStudio] v3.7.0 무한 변종 프로시저럴 프롬프트 &amp; 아트 믹서(Task 1 · 순수 BCL).
///   ▸ 아기 테마의 authored 샷 프롬프트에 <b>예술 텍스처 12종</b>(클레이·수채물결·매지컬 스노우볼·드리미 파스텔 보케 등)과
///     <b>프로시저럴 카메라 마이크로 샷</b> + <b>감성 악센트</b>를 시드 기반으로 동적 주입한다.
///   ▸ 시드가 다르면 결과 문자열이 <b>반드시</b> 달라져(seed# 각인) 렌더할 때마다 전 세계 단 하나뿐인 구도·아트가 완성.
///   ▸ 재적용 멱등(<c>art-mix</c> 마커 이후를 제거 후 재주입) · 컷별 고유 시드로 한 프로젝트 안에서도 컷마다 변주.
/// </summary>
public static class BabyPromptMutator
{
    /// <summary>예술 텍스처 12종.</summary>
    public static readonly IReadOnlyList<ArtTexture> Textures = new[]
    {
        new ArtTexture("claymation", "🟠", "클레이 아트", "handmade claymation stop-motion look, soft plasticine textures, tactile matte clay surfaces, tiny fingerprint detail"),
        new ArtTexture("waterwave", "🌊", "수채 물결", "hand-drawn watercolor illustration, wet-on-wet bleeding washes, rippling water-wave brushstrokes, soft paper grain"),
        new ArtTexture("glassglobe", "🔮", "매지컬 스노우볼", "enchanted glass snow-globe world, dreamy refracted glass dome, drifting glitter snow, tiny magical reflections"),
        new ArtTexture("pastelbokeh", "🎀", "드리미 파스텔 보케", "dreamy pastel bokeh, creamy shallow-focus orbs, cotton-candy color grade, gentle glow blooms"),
        new ArtTexture("paperart", "📄", "페이퍼 아트", "layered paper-cut diorama, cardstock shadow depth, crisp die-cut edges, handcrafted collage feel"),
        new ArtTexture("oilpastel", "🖍️", "오일 파스텔", "chunky oil-pastel crayon strokes, waxy blended pigments, textured drawing-paper tooth, storybook warmth"),
        new ArtTexture("feltcraft", "🧶", "펠트 크래프트", "needle-felted wool craft look, fuzzy fiber texture, soft handmade plush shapes, cozy stitch detail"),
        new ArtTexture("storybook", "📖", "동화 일러스트", "classic hand-inked storybook illustration, delicate line art, gentle watercolor fill, nostalgic picture-book charm"),
        new ArtTexture("stopmotion", "🎬", "스톱모션 니트", "miniature stop-motion set, knitted and wooden props, handcrafted diorama lighting, tactile frame-by-frame feel"),
        new ArtTexture("holofoil", "✨", "홀로 포일", "iridescent holographic foil sheen, rainbow prism highlights, glossy metallic shimmer, premium collectible finish"),
        new ArtTexture("embroidery", "🪡", "자수 프레임", "hand-embroidered fabric art, satin-stitch thread texture, hoop-framed cloth, cozy artisanal needlework"),
        new ArtTexture("stainedglass", "🪟", "스테인드글라스", "luminous stained-glass mosaic, glowing leaded color panels, soft cathedral light rays, jewel-toned translucency"),
    };

    /// <summary>프로시저럴 카메라 마이크로 샷 풀(시드 선택).</summary>
    private static readonly string[] MicroShots =
    {
        "subtle 6-degree dolly orbit with breathing parallax",
        "slow macro push-in with a delicate rack-focus reveal",
        "gentle handheld micro-sway, organic breathing motion",
        "smooth crane bloom rising over the subject",
        "soft arc pan drifting around the cheek line",
        "tender pull-focus from foreground bokeh to the eyes",
        "featherlight tilt-up catching the eyelash sparkle",
        "creeping dolly-in with a whisper of lens breathing",
        "slow spiral orbit with cinematic gravity",
        "hushed slider glide across the smile",
    };

    /// <summary>시드로 고유성을 더하는 감성 악센트(입자/광).</summary>
    private static readonly string[] Accents =
    {
        "drifting golden dust motes", "tiny floating heart bokeh", "soft aurora rim light",
        "petal-fall particles", "warm firefly glints", "powder-sugar snow sparkle",
        "pearl-sheen highlights", "dandelion seed drift", "candy-glow lens flare", "milky dream haze",
    };

    /// <summary>주입 마커 — 재적용 시 이 이후를 잘라내어 멱등 보장.</summary>
    private const string Marker = ", art-mix [";

    /// <summary>키로 텍스처 조회(없으면 null).</summary>
    public static ArtTexture? FindTexture(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : Textures.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>매 합성마다 고유한 새 시드.</summary>
    public static int NewSeed() => unchecked((Environment.TickCount * 397) ^ Guid.NewGuid().GetHashCode());

    /// <summary>베이스 시드 + 컷 인덱스 → 컷 고유 시드(결정론적 믹싱).</summary>
    public static int CombineSeed(int baseSeed, int index) => unchecked(baseSeed * 486187739 + index * 40503 + 17);

    /// <summary>
    /// 단일 프롬프트에 아트 텍스처 + 프로시저럴 마이크로 샷 + 감성 악센트를 시드 기반으로 주입한다(멱등).
    ///   <paramref name="artKey"/> 지정 시 그 텍스처 고정, 비면 시드로 선택 → 매번 고유.
    /// </summary>
    public static string Mutate(string basePrompt, int seed, string? artKey = null)
    {
        var baseline = StripPriorMix(basePrompt ?? "");
        var rnd = new Random(seed);
        var tex = FindTexture(artKey) ?? Textures[rnd.Next(Textures.Count)];
        var micro = MicroShots[rnd.Next(MicroShots.Length)];
        var accent = Accents[rnd.Next(Accents.Length)];
        // seed# 각인으로 서로 다른 시드는 결과가 반드시 달라진다(전 세계 단 하나).
        return $"{baseline}{Marker}{tex.Name} · {micro} · {accent} · one-of-a-kind seed#{seed}], {tex.Fragment}";
    }

    /// <summary>프로젝트 전 컷에 컷별 고유 시드로 아트 변종을 주입한다(적용 컷 수 반환).</summary>
    public static int MutateProject(StoryboardProject project, int baseSeed, string? artKey = null)
    {
        if (project is null) return 0;
        var count = 0;
        for (var i = 0; i < project.Scenes.Count; i++)
        {
            var scene = project.Scenes[i];
            scene.Prompt = Mutate(scene.Prompt, CombineSeed(baseSeed, i), string.IsNullOrWhiteSpace(artKey) ? null : artKey);
            count++;
        }
        return count;
    }

    private static string StripPriorMix(string prompt)
    {
        var idx = prompt.IndexOf(Marker, StringComparison.Ordinal);
        return idx >= 0 ? prompt[..idx] : prompt;
    }
}
