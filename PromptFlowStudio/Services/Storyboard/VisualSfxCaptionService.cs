using System;
using System.Collections.Generic;
using System.Linq;
using PromptFlowStudio.Services.Media;   // VisualSfxBadge / FoleySpec

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>
/// [PromptFlowStudio] 음소거 시청 대응 시각 효과음 뱃지 서비스 — 순수 BCL(외부 NuGet 0).
///   ▸ 육아 부모의 <b>음소거 시청</b> 특성을 반영: Gen-Foley/프리셋 폴리 트리거(옹알이/재채기/웃음/충격 등)의 컷-로컬 타임코드에
///     이모지 결합형 시각 뱃지(예: 에취! 🤧, 콰광! ⚡)를 만들어 소리가 꺼진 상태에서도 '지금 무슨 소리가 났는지' 화면으로 전달.
///   ▸ <see cref="VideoMergeService"/>가 이 뱃지를 화면 <b>상단 중앙</b>에 팝업 drawtext(반투명 박스)로 번인한다(키네틱 자막과 별개 레이어).
///   ▸ <b>오프라인</b>: 프리셋 컷의 authored 폴리(<see cref="StoryboardScene.PresetFoley"/>)를 소비 → Gemini 없이 즉시 구동.
///   ▸ 인식 못 하는 효과음 타입은 뱃지 생략(Graceful) — 의미 있는 코믹/임팩트 순간만 표기.
/// </summary>
public sealed class VisualSfxCaptionService
{
    // 효과음 타입 → (한국어 라벨, 이모지). BabyThemePresetService 가 쓰는 타입을 모두 포함.
    private static readonly Dictionary<string, (string Text, string Emoji)> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sneeze"]   = ("에취!",   "🤧"),
        ["crash"]    = ("콰광!",   "⚡"),
        ["boing"]    = ("뿅!",     "✨"),
        ["giggle"]   = ("까르르",  "😆"),
        ["chomp"]    = ("냠냠",    "😋"),
        ["splash"]   = ("첨벙!",   "💦"),
        ["pop"]      = ("뾰옹",    "💫"),
        ["ding"]     = ("띵~",     "🔔"),
        ["sparkle"]  = ("반짝",    "✨"),
        ["whoosh"]   = ("슉~",     "💨"),
        ["drumroll"] = ("두구두구","🥁"),
        ["suspense"] = ("두근두근","😳"),
        ["cute"]     = ("꼬물",    "🐣"),
        ["squeak"]   = ("삑!",     "🐤"),
        ["pluck"]    = ("띠링",    "🎵"),
        ["cry"]      = ("으앙",    "😭"),
        ["clap"]     = ("짝짝",    "👏"),
        ["kiss"]     = ("쪽",      "💕"),
        ["zoom"]     = ("확!",     "🔍"),
    };

    private const int BadgeMs = 900;    // 뱃지 표시 지속(ms)
    private const int MinGap = 260;     // 근접 뱃지 병합 간격(ms)

    /// <summary>한 컷의 authored 폴리(<see cref="StoryboardScene.PresetFoley"/>)에서 시각 뱃지 목록을 만든다. 컷-로컬 ms 기준. 없으면 빈 목록.</summary>
    public IReadOnlyList<VisualSfxBadge> BuildBadges(StoryboardScene? scene, int clipMs = 5000)
    {
        var list = new List<VisualSfxBadge>();
        if (scene?.PresetFoley is not { Count: > 0 } foley) return list;

        var last = 0; var haveLast = false;
        foreach (var sp in foley.OrderBy(f => f?.TimestampMs ?? 0))
        {
            if (sp is null || !Map.TryGetValue((sp.SfxType ?? "").Trim(), out var m)) continue;
            var s = Math.Clamp(sp.TimestampMs, 0, Math.Max(0, clipMs - 300));
            if (haveLast && s - last < MinGap) continue;   // 근접 뱃지 겹침 방지(가장 이른 것만 · 첫 뱃지는 항상 표시)
            last = s; haveLast = true;
            var e = Math.Min(clipMs, s + BadgeMs);
            list.Add(new VisualSfxBadge(m.Text, m.Emoji, s, e));
        }
        return list;
    }

    /// <summary>효과음 타입에 대응 뱃지가 있는지(진단/미리보기용).</summary>
    public bool Has(string sfxType) => Map.ContainsKey((sfxType ?? "").Trim());
}
