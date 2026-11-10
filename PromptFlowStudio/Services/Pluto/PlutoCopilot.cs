using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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
    /// <summary>🧠 Gemini LLM 시나리오 여부(false = 로컬 키워드·마일스톤 템플릿).</summary>
    public bool UsedLlm { get; init; }

    /// <summary>🚀 스튜디오 주입용 프롬프트(3막 + 카메라).</summary>
    public string BuildStudioPrompt() => VideoPrompt + ", camera: " + CameraMotion;
}

/// <summary>
/// 🪐 Pluto Interactive Copilot — 사용자의 일상 메모(한국어) → 마일스톤·장소·감정 해석 → 15초 3막(분투 → 성공 → 여운) 바이럴 시나리오.
/// DNA 렉시콘 상위 조명/분위기 토큰을 우선 반영. 🧠 <see cref="IPlutoLlmClient"/> 주입 시 <see cref="DraftScriptFromMemoAsync"/> 가 Gemini 스토리텔링 우선 · 실패 시 로컬 템플릿 폴백.
/// </summary>
public sealed partial class PlutoCopilot
{
    public const int TotalSeconds = 15;
    private static readonly Regex DayCount = new(@"(\d{1,4})\s*일", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex MonthCount = new(@"(\d{1,2})\s*개월", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private readonly PlutoPreferenceLexicon? _lexicon;
    private readonly IPlutoLlmClient? _llm;
    private readonly PlutoSocialRadarEngine? _radar;

    public PlutoCopilot() : this(null, null) { }
    public PlutoCopilot(PlutoPreferenceLexicon? lexicon) : this(lexicon, null) { }
    public PlutoCopilot(PlutoPreferenceLexicon? lexicon, IPlutoLlmClient? llm) : this(lexicon, llm, null) { }

    /// <summary>📈 <paramref name="radar"/> 주입 시 SNS 상위 바이럴 패턴(페이싱 · 훅 구조)을 시나리오 프롬프트에 반영.</summary>
    public PlutoCopilot(PlutoPreferenceLexicon? lexicon, IPlutoLlmClient? llm, PlutoSocialRadarEngine? radar)
    {
        _lexicon = lexicon;
        _llm = llm;
        _radar = radar;
    }

    /// <summary>🍼 아이덴티티 볼트 — 활성 프로필 구분(Baby/Pet)과 감지 테마 일치 시 캐릭터 일관성 앵커를 시나리오 프롬프트 선두에 삽입.</summary>
    public PlutoIdentityVault? Identity { get; init; }

    /// <summary>🧠 LLM 결선 여부(키 미설정이어도 true — 실제 사용 여부는 <see cref="PlutoCopilotScript.UsedLlm"/>).</summary>
    public bool HasLlm => _llm is not null;

    public const string LlmScriptInstruction =
        "You are Pluto, a warm short-form video storyteller. " +
        "Transform this parent's real-life baby/pet/daily memory into a captivating 15-second 3-act short-form video script " +
        "(Act 1: Hook/Setup [0-4s], Act 2: Climax/Action [4-11s], Act 3: Emotional Lingering Smile [11-15s]). " +
        "Also specify camera movement and matching baby/acoustic BGM. " +
        "Respond with JSON only: {\"hookTitle\":string,\"milestone\":string,\"acts\":[{\"label\":string,\"prompt\":string}],\"camera\":string,\"bgm\":\"lullaby\"|\"musicbox\"|\"acoustic\"} - " +
        "hookTitle: Korean viral hook title with one emoji (max 40 chars); milestone: short Korean label of the moment; " +
        "acts: exactly 3 items, label is a short Korean act name, prompt is a vivid English text-to-video prompt for that act; " +
        "camera: English camera movement directive.";

    /// <summary>🧠 LLM 3막 시간 구간(Hook 0-4s · Climax 4-11s · Lingering 11-15s).</summary>
    private static readonly (int Start, int End)[] LlmActWindows = { (0, 4), (4, 11), (11, TotalSeconds) };

    /// <summary>
    /// 🧠 메모 → 15초 3막 시나리오(Gemini 우선). LLM 미설정/예외/3.5초 초과/스키마 불일치 → 로컬 키워드·마일스톤 템플릿(<see cref="Draft"/>) 무중단 폴백.
    /// 빈 메모는 <see cref="ArgumentException"/>.
    /// </summary>
    public async Task<PlutoCopilotScript> DraftScriptFromMemoAsync(string userMemo, DateTime? now = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userMemo)) throw new ArgumentException("memo required", nameof(userMemo));
        await EnsureContextLoadedAsync(ct).ConfigureAwait(false);
        var local = Draft(userMemo, now);   // 검증 + 폴백 기준선(카테고리 · BGM)
        var text = await PlutoLlmBridge.TryCompleteAsync(_llm, LlmScriptInstruction, BuildLlmScriptRequest(local, now ?? DateTime.Now), 0.85, ct).ConfigureAwait(false);
        var llm = text is null ? null : ParseLlmScript(text, local);
        if (llm is null) return local;
        var anchor = IdentityAnchor(local.Category);
        return anchor.Length == 0 ? llm : llm with { VideoPrompt = PlutoIdentityVault.ApplyAnchor(llm.VideoPrompt, anchor) };   // 🍼 LLM 결과에도 앵커 보장(멱등)
    }

    /// <summary>📈🍼 레이더 · 아이덴티티 볼트 지연 로드([부분 구제] — 실패 시 해당 컨텍스트 없이 생성, 취소만 전파).</summary>
    private async Task EnsureContextLoadedAsync(CancellationToken ct)
    {
        if (_radar is not null)
        {
            try { await _radar.EnsureLoadedAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Copilot] radar load: " + ex.Message); }   // [부분 구제] 벤치마크 패턴만 사용
        }
        if (Identity is not null)
        {
            try { await Identity.EnsureLoadedAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Copilot] identity load: " + ex.Message); }   // [부분 구제] 앵커 없이 생성
        }
    }

    /// <summary>🍼 활성 프로필 앵커(미주입/불일치 테마 → 빈 문자열).</summary>
    private string IdentityAnchor(StudioThemeCategory category)
    {
        if (Identity is null) return "";
        try { return PlutoIdentityVault.AnchorFor(Identity.ActiveProfile, category); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Copilot] identity: " + ex.Message); return ""; }
    }

    private string BuildLlmScriptRequest(PlutoCopilotScript local, DateTime now)
    {
        var ctx = PlutoGenerativeEngine.ContextFor(now);
        var sb = new System.Text.StringBuilder(local.Memo.Length + 256);
        sb.Append("Memo (Korean): ").Append(local.Memo).Append('\n')
          .Append("Detected theme: ").Append(local.Category.ToString()).Append('\n')
          .Append("Season / time: ").Append(ctx.SeasonEn).Append(", ").Append(ctx.TimeEn).Append('\n')
          .Append("Preferred lighting: ").Append(TopOr(DnaBucket.Lighting, ctx.LightingEn)).Append('\n')
          .Append("Preferred atmosphere: ").Append(TopOr(DnaBucket.Atmosphere, "warm pastel tone")).Append('\n');
        var anchor = IdentityAnchor(local.Category);
        if (anchor.Length > 0)
            sb.Append("Subject identity anchor (keep this exact same character in every act): ").Append(anchor).Append('\n');
        if (TopPattern(local.Category) is { } p)
            sb.Append("Proven SNS viral formula (apply its hook timing and cut pacing): ").Append(p.Title).Append(" — ").Append(PlutoSocialRadarEngine.PatternDirective(p)).Append('\n');
        return sb.ToString();
    }

    /// <summary>LLM 시나리오 JSON 스키마 즉시 검증 — 3막 프롬프트 모두 필요(누락 시 null). BGM 미인식은 로컬 기준선 유지.</summary>
    public static PlutoCopilotScript? ParseLlmScript(string json, PlutoCopilotScript local)
    {
        using var doc = PlutoLlmBridge.TryParse(json);
        if (doc is null) return null;
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("acts", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        if (arr.GetArrayLength() < LlmActWindows.Length) return null;

        var acts = new PlutoAct[LlmActWindows.Length];
        var i = 0;
        foreach (var e in arr.EnumerateArray())
        {
            if (i >= acts.Length) break;
            var prompt = PlutoLlmBridge.Str(e, "prompt", 600);
            if (prompt.Length < 8) return null;
            var label = PlutoLlmBridge.Str(e, "label", 40);
            if (label.Length == 0) label = local.Acts.Count > i ? local.Acts[i].LabelKo : "Act " + (i + 1).ToString(CultureInfo.InvariantCulture);
            var (st, en) = LlmActWindows[i];
            acts[i] = new PlutoAct(st, en, label, prompt);
            i++;
        }

        var hook = PlutoLlmBridge.Str(r, "hookTitle", 60);
        var milestone = PlutoLlmBridge.Str(r, "milestone", 30);
        var camera = PlutoLlmBridge.Str(r, "camera", 200);
        var bgm = ParseBgm(PlutoLlmBridge.Str(r, "bgm", 40)) ?? local.BgmTrack;

        var sb = new System.Text.StringBuilder(768);
        sb.Append("15-second vertical short, 3-act story. ");
        for (var k = 0; k < acts.Length; k++)
        {
            if (k > 0) sb.Append(' ');
            sb.Append("Act ").Append((k + 1).ToString(CultureInfo.InvariantCulture)).Append(" (")
              .Append(acts[k].StartSec.ToString(CultureInfo.InvariantCulture)).Append('-')
              .Append(acts[k].EndSec.ToString(CultureInfo.InvariantCulture)).Append("s): ").Append(acts[k].PromptEn.TrimEnd('.')).Append('.');
        }

        return new PlutoCopilotScript(local.Memo, local.Category,
            milestone.Length == 0 ? local.MilestoneKo : milestone,
            hook.Length == 0 ? local.HookTitle : hook,
            acts, sb.ToString(),
            camera.Length == 0 ? local.CameraMotion : camera,
            bgm, StockSuiteExporter.BabyBgmLabel(bgm)) { UsedLlm = true };
    }

    private static BabyBgmTrack? ParseBgm(string s)
    {
        if (s.Length == 0) return null;
        if (s.Contains("music", StringComparison.OrdinalIgnoreCase) || s.Contains("box", StringComparison.OrdinalIgnoreCase) || s.Contains("오르골", StringComparison.Ordinal)) return BabyBgmTrack.MusicBox;
        if (s.Contains("acoustic", StringComparison.OrdinalIgnoreCase) || s.Contains("guitar", StringComparison.OrdinalIgnoreCase) || s.Contains("어쿠스틱", StringComparison.Ordinal)) return BabyBgmTrack.Acoustic;
        if (s.Contains("lullaby", StringComparison.OrdinalIgnoreCase) || s.Contains("자장가", StringComparison.Ordinal)) return BabyBgmTrack.Lullaby;
        return null;
    }

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
        if (TopPattern(ms.Category) is { } viral)
            sb.Append(" SNS pacing: ").Append(PlutoSocialRadarEngine.PatternDirective(viral)).Append('.');

        var videoPrompt = PlutoIdentityVault.ApplyAnchor(sb.ToString(), IdentityAnchor(ms.Category));   // 🍼 캐릭터 일관성 앵커 선두 삽입
        return new PlutoCopilotScript(text, ms.Category, ms.Ko, BuildHook(text, ms), acts, videoPrompt, ms.Camera, ms.Bgm,
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

    /// <summary>📈 레이더 최상위 바이럴 패턴(미주입/예외 → null).</summary>
    private SocialTrendInsight? TopPattern(StudioThemeCategory category)
    {
        if (_radar is null) return null;
        try
        {
            var top = _radar.TopViralPatterns(category, 1);
            return top.Count > 0 ? top[0] : null;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Copilot] radar: " + ex.Message); return null; }
    }

    private string TopOr(DnaBucket b, string fallback)
    {
        if (_lexicon is null) return fallback;
        var top = _lexicon.TopTokens(b, 1);
        return top.Count > 0 ? top[0] : fallback;
    }
}
