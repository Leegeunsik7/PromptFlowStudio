using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using PromptFlowStudio.Services.Export;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🎬 15초 3막 시나리오 1막.</summary>
public readonly record struct PlutoAct(int StartSec, int EndSec, string LabelKo, string PromptEn);

/// <summary>🪐 Copilot 시나리오 초안 — 훅 제목 · 3막 프롬프트 · 카메라 · BGM.</summary>
public sealed record PlutoCopilotScript(
    string Memo,
    StudioThemeCategory Category,
    string MilestoneKo,
    string HookTitle,
    IReadOnlyList<PlutoAct> Acts,
    string VideoPrompt,
    string CameraMotion,
    BabyBgmTrack BgmTrack,
    string BgmLabel)
{
    /// <summary>🚀 스튜디오 주입용 프롬프트(3막 + 카메라).</summary>
    public string BuildStudioPrompt() => VideoPrompt + ", camera: " + CameraMotion;
}

/// <summary>
/// 🪐 Pluto Interactive Copilot — 사용자의 일상 메모(한국어) → 마일스톤·장소·감정 해석 → 15초 3막(분투 → 성공 → 여운) 바이럴 시나리오.
/// DNA 렉시콘 상위 조명/분위기 토큰을 우선 반영(로컬 · 외부 호출 없음).
/// </summary>
public sealed class PlutoCopilot
{
    public const int TotalSeconds = 15;
    private static readonly Regex DayCount = new(@"(\d{1,4})\s*일", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex MonthCount = new(@"(\d{1,2})\s*개월", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private readonly PlutoPreferenceLexicon? _lexicon;

    public PlutoCopilot() : this(null) { }
    public PlutoCopilot(PlutoPreferenceLexicon? lexicon) => _lexicon = lexicon;

    private sealed record Milestone(string[] Keys, StudioThemeCategory Category, string Ko, string Emoji, string Subject, string Struggle, string Triumph, string Outro, string Camera, BabyBgmTrack Bgm);

    private static readonly Milestone[] Milestones =
    {
        new(new[] { "뒤집기", "뒤집", "롤오버" }, StudioThemeCategory.BabyParenting, "첫 뒤집기", "🍼", "baby",
            "baby wriggling and straining with tiny legs kicking, trying to roll over on a soft playmat",
            "baby finally flipping onto the tummy, lifting head up in triumph",
            "baby looking straight into the camera with a sweet gummy smile",
            "eye-level slow dolly-in → macro close-up on the smile", BabyBgmTrack.MusicBox),
        new(new[] { "걸음", "걸었", "걸어", "첫발" }, StudioThemeCategory.BabyParenting, "첫 걸음마", "👣", "baby",
            "baby gripping the sofa edge with wobbly knees, hesitating",
            "baby letting go and taking three wobbly steps forward",
            "baby tumbling into a parent's open arms with a giggle",
            "low-angle tracking dolly → slow-motion catch", BabyBgmTrack.Acoustic),
        new(new[] { "배밀이", "기어", "기었" }, StudioThemeCategory.BabyParenting, "첫 배밀이", "🐛", "baby",
            "baby pushing with tiny arms toward a favorite toy, inching slowly",
            "baby grabbing the toy after a determined crawl",
            "baby rolling onto the back hugging the toy proudly",
            "floor-level lateral slide → push-in", BabyBgmTrack.Acoustic),
        new(new[] { "앉았", "앉기", "혼자 앉" }, StudioThemeCategory.BabyParenting, "혼자 앉기", "🧸", "baby",
            "baby wobbling side to side, trying to balance while sitting",
            "baby sitting upright all alone, arms raised",
            "baby clapping and beaming at the camera",
            "eye-level slow orbit", BabyBgmTrack.MusicBox),
        new(new[] { "이유식", "먹었", "첫 맛", "먹방" }, StudioThemeCategory.BabyParenting, "첫 이유식", "🥄", "baby",
            "baby staring suspiciously at a spoon of puree",
            "baby tasting the first bite with a scrunched surprised face",
            "baby opening the mouth wide asking for more, messy cheeks",
            "macro close-up → gentle pull-back", BabyBgmTrack.MusicBox),
        new(new[] { "이빨", "치아", "첫 이가", "이가 났" }, StudioThemeCategory.BabyParenting, "첫 이빨", "🦷", "baby",
            "baby chewing on a teether, slightly fussy",
            "a tiny first tooth peeking out in a wide grin",
            "baby giggling while parent gently tickles",
            "macro close-up with rack focus", BabyBgmTrack.Lullaby),
        new(new[] { "잠", "잤", "재웠", "낮잠" }, StudioThemeCategory.BabyParenting, "꿀잠 성공", "😴", "baby",
            "baby fussing and rubbing eyes in a dim nursery",
            "baby's eyelids slowly closing while hugging a plush toy",
            "baby sleeping peacefully, tiny chest rising and falling",
            "overhead slow push-in", BabyBgmTrack.Lullaby),
        new(new[] { "강아지", "댕댕", "멍멍", "산책" }, StudioThemeCategory.PetsCompanions, "댕댕이 하루", "🐶", "puppy",
            "puppy sniffing around curiously, ears perked",
            "puppy dashing toward the owner with ears flapping",
            "puppy flopping down for belly rubs with a happy face",
            "low-angle tracking pan → slow-motion peak", BabyBgmTrack.Acoustic),
        new(new[] { "고양이", "냥이", "냥" }, StudioThemeCategory.PetsCompanions, "냥이 하루", "🐱", "cat",
            "cat crouching and wiggling before a pounce",
            "cat leaping at a feather toy mid-air",
            "cat curling up in a sunbeam, slow blink at the camera",
            "macro slow push-in", BabyBgmTrack.MusicBox),
    };

    private static readonly (string Key, string En)[] Places =
    {
        ("거실", "sunlit living room"), ("침대", "cozy crib"), ("아기침대", "cozy crib"), ("공원", "green park lawn"),
        ("목욕", "warm bathroom"), ("욕실", "warm bathroom"), ("놀이매트", "pastel playmat"), ("부엌", "bright kitchen"),
        ("주방", "bright kitchen"), ("마당", "sunny backyard"), ("바다", "seaside"), ("카페", "cozy café"),
    };

    /// <summary>메모 → 15초 3막 시나리오. 빈 메모는 <see cref="ArgumentException"/>.</summary>
    public PlutoCopilotScript Draft(string memo, DateTime? now = null)
    {
        if (string.IsNullOrWhiteSpace(memo)) throw new ArgumentException("memo required", nameof(memo));
        var text = memo.Trim();
        var t = now ?? DateTime.Now;

        var ms = Detect(text);
        var place = "cozy nursery";
        foreach (var (k, en) in Places) if (text.Contains(k, StringComparison.Ordinal)) { place = en; break; }
        if (ms.Category == StudioThemeCategory.PetsCompanions && place == "cozy nursery") place = "cozy living room";
        var smiled = text.Contains('웃', StringComparison.Ordinal) || text.Contains("방긋", StringComparison.Ordinal);

        var lighting = TopOr(DnaBucket.Lighting, PlutoGenerativeEngine.ContextFor(t).LightingEn);
        var atmosphere = TopOr(DnaBucket.Atmosphere, ms.Category == StudioThemeCategory.BabyParenting ? "pastel tone" : "warm bokeh");

        var outro = ms.Outro;
        if (smiled && !outro.Contains("smile", StringComparison.Ordinal)) outro += ", breaking into a bright smile";

        var acts = new[]
        {
            new PlutoAct(0, 5, "① 도전 (Beginning struggle)", ms.Struggle + " in a " + place + ", " + lighting),
            new PlutoAct(5, 10, "② 성공의 순간 (Triumph)", ms.Triumph + ", " + atmosphere),
            new PlutoAct(10, TotalSeconds, "③ 여운 (Sweet outro)", outro + ", " + atmosphere + ", soft vignette"),
        };
        var sb = new System.Text.StringBuilder(512);
        sb.Append("15-second vertical short, 3-act story. ");
        for (var i = 0; i < acts.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append("Act ").Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" (")
              .Append(acts[i].StartSec.ToString(CultureInfo.InvariantCulture)).Append('-')
              .Append(acts[i].EndSec.ToString(CultureInfo.InvariantCulture)).Append("s): ").Append(acts[i].PromptEn).Append('.');
        }

        return new PlutoCopilotScript(text, ms.Category, ms.Ko, BuildHook(text, ms), acts, sb.ToString(), ms.Camera, ms.Bgm,
            StockSuiteExporter.BabyBgmLabel(ms.Bgm));
    }

    private static Milestone Detect(string text)
    {
        foreach (var m in Milestones)
            foreach (var k in m.Keys)
                if (text.Contains(k, StringComparison.Ordinal)) return m;
        return new Milestone(Array.Empty<string>(), StudioThemeCategory.BabyParenting, "오늘의 작은 순간", "✨", "baby",
            "baby curiously exploring the surroundings with wide eyes",
            "baby discovering something new and gasping with delight",
            "baby cuddling into a parent's arms with a sweet smile",
            "eye-level gentle handheld follow → slow push-in", BabyBgmTrack.MusicBox);
    }

    private static string BuildHook(string text, Milestone ms)
    {
        var d = DayCount.Match(text);
        if (d.Success && int.TryParse(d.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days > 0)
            return days.ToString(CultureInfo.InvariantCulture) + "일 만에 일어난 작은 기적 " + ms.Emoji;
        var mo = MonthCount.Match(text);
        if (mo.Success && int.TryParse(mo.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var months) && months > 0)
            return months.ToString(CultureInfo.InvariantCulture) + "개월 아기의 " + ms.Ko + " " + ms.Emoji;
        return ms.Ko + ", 우리 집에 찾아온 작은 기적 " + ms.Emoji;
    }

    private string TopOr(DnaBucket b, string fallback)
    {
        if (_lexicon is null) return fallback;
        var top = _lexicon.TopTokens(b, 1);
        return top.Count > 0 ? top[0] : fallback;
    }
}
