using System;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>
/// 🎞 Pluto 4컷 성장 마일스톤 오케스트레이터 — ① 아침 훅 → ② 액션(터미타임) → ③ 클라이맥스 미소 → ④ 평온한 잠.
///   ▸ 활성 프로필 일관성 앵커(<see cref="PlutoIdentityVault"/>)를 4컷 전 프롬프트 선두에 삽입(멱등).
///   ▸ 🧠 <see cref="PlutoLlmBridge"/> 체인(Gemini → 로컬 게이트웨이 · 단계별 3.5초) 우선 · 슬롯별 스키마 검증 ·
///     누락/실패 슬롯은 테마 템플릿으로 결정론적 보강([부분 구제]). 호출자 취소만 전파.
/// </summary>
public sealed class PlutoMilestoneOrchestrator
{
    public const int SceneCount = 4;
    public const int MaxPromptLength = 600;
    public const int MaxTitleLength = 30;
    public const int MaxNoteLength = 120;
    public const string DefaultBabyAnchor = "same consistent baby";
    public const string DefaultPetAnchor = "same consistent pet";

    /// <summary>슬롯 시간 구간(초) — 15초 쇼츠.</summary>
    public static readonly (int Start, int End)[] SlotWindows = { (0, 4), (4, 8), (8, 12), (12, 15) };

    /// <summary>슬롯별 카메라 — 부드러운 연결(푸시인 → 로우 오비트 → 매크로 → 풀백).</summary>
    public static readonly string[] SlotCameras = { "Eye-Level Push-In", "Low Orbit", "Macro Close-Up", "Slow Pull-Back" };

    public const string LlmMilestoneInstruction =
        "You are Pluto, a cinematic director for 15-second vertical baby/pet growth diary shorts. " +
        "Write exactly 4 coordinated scenes of ONE continuous day with the SAME subject: " +
        "slot1 [0-4s] morning hook, slot2 [4-8s] playful action such as tummy time, slot3 [8-12s] climax big smile, slot4 [12-15s] peaceful sleep. " +
        "Keep wardrobe, lighting palette and room consistent across slots. Prompts in English for a text-to-video model, max 400 characters, no names. Titles in natural Korean, max 30 characters. " +
        "Respond with JSON only: {\"scenes\":[{\"slot\":1,\"title\":string,\"prompt\":string,\"camera\":string,\"bgm\":string}, ... 4 items]}.";

    private sealed record ThemeTemplate(PlutoMilestoneTheme Theme, string Name, string Bgm, string[][] Titles, string[][] Prompts);

    private const string LullabyBgm = "Soft Piano Lullaby";

    private static readonly ThemeTemplate[] Templates =
    {
        new(PlutoMilestoneTheme.Day100, "100일의 기적", "Music Box Waltz",
            new[]
            {
                new[] { "백일 아침의 첫 눈맞춤", "햇살이 깨운 백일 아침" },
                new[] { "힘찬 터미타임 도전", "꼼지락 발차기 놀이" },
                new[] { "백일 상 앞 함박웃음", "잇몸 미소 대폭발" },
                new[] { "포근한 백일 밤", "새근새근 꿈나라" },
            },
            new[]
            {
                new[] { "{s} waking up in a sunlit crib on the 100th-day morning, soft golden window light, tiny yawn and stretch, pastel nursery with a '100' balloon", "{s} opening eyes to gentle morning light beside a hand-knitted '100 days' garland, warm cozy nursery" },
                new[] { "{s} doing tummy time on a fluffy cream blanket, lifting head proudly, tiny hands pushing up, a parent's hand cheering nearby", "{s} kicking little legs on a pastel play mat under a slowly turning mobile, energetic and curious" },
                new[] { "{s} bursting into a big gummy smile at the camera, traditional 100-day table with rice cakes softly blurred behind, warm bokeh", "{s} giggling with sparkling eyes in front of a 100-day celebration table, cream and gold palette, soft bokeh" },
                new[] { "{s} peacefully asleep swaddled in a soft blanket, dim warm night light, gentle breathing, star mobile slowly turning", "{s} sleeping on a parent's chest under a warm lamp, tiny fingers curled, calm 100-day night" },
            }),
        new(PlutoMilestoneTheme.FirstBirthday, "첫돌의 감동", "Celebration Pizzicato",
            new[]
            {
                new[] { "돌날 아침의 설렘", "한복 입는 첫돌 아침" },
                new[] { "돌잡이 대작전", "아장아장 파티 탐험" },
                new[] { "케이크 앞 환한 웃음", "박수 속 첫돌 미소" },
                new[] { "파티 끝 달콤한 꿈", "품에 안겨 잠든 첫돌" },
            },
            new[]
            {
                new[] { "{s} waking up on the first birthday morning, a tiny hanbok laid out nearby, sunbeams through sheer curtains", "{s} being dressed in a colorful tiny hanbok on the first birthday morning, bright cheerful room, soft daylight" },
                new[] { "{s} crawling toward doljabi items on a festive table, reaching curiously for a tiny pencil and a spool of thread", "{s} taking wobbly steps across a decorated party room toward balloons, arms out for balance" },
                new[] { "{s} clapping and laughing in front of a small first birthday cake with one candle, family applause, confetti bokeh", "{s} beaming a huge smile as the single candle glows on the first birthday cake, warm festive bokeh" },
                new[] { "{s} sleeping soundly in a parent's arms after the party, little party hat beside, soft lamp glow", "{s} drifting into a deep nap sleep on a cozy sofa among deflating balloons, gentle evening light, peaceful after-party calm" },
            }),
        new(PlutoMilestoneTheme.DailyRoutine, "우리아이 하루", "Acoustic Morning Ukulele",
            new[]
            {
                new[] { "굿모닝 기지개", "창가의 아침 인사" },
                new[] { "말랑말랑 놀이 시간", "엎드려 세상 구경" },
                new[] { "까르르 웃음 폭발", "까꿍 놀이 하이라이트" },
                new[] { "꿈나라 낮잠", "토닥토닥 저녁 잠" },
            },
            new[]
            {
                new[] { "{s} stretching arms wide after waking up, morning sunlight streaming across a white bedsheet, sleepy smile", "{s} looking out a bright window in the morning, soft curtains moving, curious little face" },
                new[] { "{s} playing with soft fabric blocks on a play mat, grabbing and shaking them happily", "{s} doing tummy time and reaching for a colorful rattle, determined little face, natural daylight" },
                new[] { "{s} laughing out loud during a peekaboo game, eyes crinkling with joy, warm living-room light", "{s} giggling while being gently tickled, pure joyful expression, soft background bokeh" },
                new[] { "{s} in a peaceful nap sleep under a light blanket, afternoon light fading softly, slow calm breathing", "{s} falling asleep while being gently patted, dim cozy bedroom, warm night light" },
            }),
    };

    private readonly IPlutoLlmClient? _llm;
    private readonly PlutoIdentityVault? _identity;

    /// <summary>⏱ 단계별 LLM 예산(기본 3.5초 · 테스트 결정론용 주입 · null이면 체인 기본값).</summary>
    public TimeSpan? LlmBudget { get; init; }

    public PlutoMilestoneOrchestrator(IPlutoLlmClient? llm = null, PlutoIdentityVault? identity = null)
    {
        _llm = llm;
        _identity = identity;
    }

    /// <summary>🎞 마일스톤 키("100일의 기적"·"첫돌"·"하루"·enum 이름) → 4컷 플랜. LLM 실패/미설정/3.5초 초과 → 템플릿.</summary>
    public async Task<PlutoMilestonePlan> OrchestrateFourCutPlanAsync(string milestoneKey, string? userNote = null, CancellationToken ct = default)
    {
        var theme = ParseTheme(milestoneKey);
        var (anchor, isPet) = await ResolveAnchorAsync(ct).ConfigureAwait(false);
        var local = BuildLocal(theme, anchor, isPet, userNote);
        var user = BuildLlmRequest(local, userNote);
        var text = await PlutoLlmBridge.TryCompleteAsync(_llm, LlmMilestoneInstruction, user, 0.85, ct, LlmBudget).ConfigureAwait(false);
        return Merge(local, text);
    }

    /// <summary>⚡ 로컬 결정론 플랜(LLM 미사용) — 같은 입력 → 같은 결과.</summary>
    public static PlutoMilestonePlan BuildLocal(PlutoMilestoneTheme theme, string? anchor, bool isPet = false, string? userNote = null)
    {
        var t = TemplateOf(theme);
        var a = string.IsNullOrWhiteSpace(anchor) ? (isPet ? DefaultPetAnchor : DefaultBabyAnchor) : anchor.Trim();
        var note = CleanNote(userNote);
        var subject = isPet ? "the pet" : "the baby";
        var seed = StableHash(t.Name + "|" + a + "|" + note);
        var scenes = new PlutoMilestoneScene[SceneCount];
        for (var i = 0; i < SceneCount; i++)
        {
            var v = (int)((seed >> (i * 3)) & 1);
            var body = t.Prompts[i][v].Replace("{s}", subject, StringComparison.Ordinal);
            if (note.Length > 0) body += ", " + note;
            scenes[i] = NewScene(i + 1, t.Titles[i][v], PlutoIdentityVault.ApplyAnchor(body, a), SlotCameras[i], i == SceneCount - 1 ? LullabyBgm : t.Bgm);
        }
        return new PlutoMilestonePlan { MilestoneName = t.Name, Theme = theme, Scenes = scenes, PersonaConsistencyAnchor = a, UsedLlm = false };
    }

    /// <summary>키 → 테마(enum 이름 · 한글 표시명 · 키워드). 미인식 → <see cref="PlutoMilestoneTheme.DailyRoutine"/>.</summary>
    public static PlutoMilestoneTheme ParseTheme(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return PlutoMilestoneTheme.DailyRoutine;
        var k = key.Trim();
        if (Enum.TryParse<PlutoMilestoneTheme>(k, true, out var e) && Enum.IsDefined(e)) return e;
        if (k.Contains("100", StringComparison.Ordinal) || k.Contains("백일", StringComparison.Ordinal) || k.Contains("hundred", StringComparison.OrdinalIgnoreCase)) return PlutoMilestoneTheme.Day100;
        if (k.Contains("돌", StringComparison.Ordinal) || k.Contains("birthday", StringComparison.OrdinalIgnoreCase) || k.Contains("생일", StringComparison.Ordinal)) return PlutoMilestoneTheme.FirstBirthday;
        return PlutoMilestoneTheme.DailyRoutine;
    }

    public static string DisplayName(PlutoMilestoneTheme theme) => TemplateOf(theme).Name;

    /// <summary>슬롯(1~4) → "0-4s" 형식.</summary>
    public static string TimeRangeOf(int slot)
    {
        var w = SlotWindows[Math.Clamp(slot, 1, SceneCount) - 1];
        return w.Start.ToString(CultureInfo.InvariantCulture) + "-" + w.End.ToString(CultureInfo.InvariantCulture) + "s";
    }

    public static float DurationOf(int slot)
    {
        var w = SlotWindows[Math.Clamp(slot, 1, SceneCount) - 1];
        return w.End - w.Start;
    }

    /// <summary>LLM JSON 슬롯별 검증 — 유효 슬롯만 채택(앵커 재삽입 · 길이 상한), 나머지는 로컬 유지. 하나도 없으면 로컬 그대로.</summary>
    internal static PlutoMilestonePlan Merge(PlutoMilestonePlan local, string? llmJson)
    {
        using var doc = PlutoLlmBridge.TryParse(llmJson);
        if (doc is null) return local;
        var root = doc.RootElement;
        JsonElement arr;
        if (root.ValueKind == JsonValueKind.Array) arr = root;
        else if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("scenes", out arr) || arr.ValueKind != JsonValueKind.Array) return local;

        var scenes = new PlutoMilestoneScene[SceneCount];
        for (var i = 0; i < SceneCount; i++) scenes[i] = local.Scenes[i];
        var used = false;
        var pos = 0;
        foreach (var e in arr.EnumerateArray())
        {
            pos++;
            if (e.ValueKind != JsonValueKind.Object) continue;
            var slot = e.TryGetProperty("slot", out var sv) && sv.ValueKind == JsonValueKind.Number && sv.TryGetInt32(out var n) ? n : pos;
            if (slot < 1 || slot > SceneCount) continue;
            var prompt = PlutoLlmBridge.Str(e, "prompt", MaxPromptLength);
            if (prompt.Length == 0) continue;
            var baseScene = local.Scenes[slot - 1];
            var title = PlutoLlmBridge.Str(e, "title", MaxTitleLength);
            var camera = PlutoLlmBridge.Str(e, "camera", 60);
            var bgm = PlutoLlmBridge.Str(e, "bgm", 60);
            scenes[slot - 1] = NewScene(slot,
                title.Length > 0 ? title : baseScene.SceneTitle,
                PlutoIdentityVault.ApplyAnchor(prompt, local.PersonaConsistencyAnchor),
                camera.Length > 0 ? camera : baseScene.CameraMovement,
                bgm.Length > 0 ? bgm : baseScene.RecommendedBgm);
            used = true;
        }
        if (!used) return local;
        return new PlutoMilestonePlan { MilestoneName = local.MilestoneName, Theme = local.Theme, Scenes = scenes, PersonaConsistencyAnchor = local.PersonaConsistencyAnchor, UsedLlm = true };
    }

    private static PlutoMilestoneScene NewScene(int slot, string title, string prompt, string camera, string bgm) => new()
    {
        SlotIndex = slot,
        TimeRange = TimeRangeOf(slot),
        SceneTitle = title,
        Prompt = prompt,
        CameraMovement = camera,
        RecommendedBgm = bgm,
    };

    private static string BuildLlmRequest(PlutoMilestonePlan local, string? userNote)
    {
        var sb = new StringBuilder(512);
        sb.Append("Milestone: ").Append(local.MilestoneName).Append(" (").Append(local.Theme.ToString()).Append(")\n");
        sb.Append("Identity anchor (do not repeat, it is prepended automatically): ").Append(local.PersonaConsistencyAnchor).Append('\n');
        var note = CleanNote(userNote);
        if (note.Length > 0) sb.Append("Parent note: ").Append(note).Append('\n');
        sb.Append("Slot cameras: ");
        for (var i = 0; i < SceneCount; i++) { if (i > 0) sb.Append(" | "); sb.Append(TimeRangeOf(i + 1)).Append(' ').Append(SlotCameras[i]); }
        return sb.ToString();
    }

    private async Task<(string Anchor, bool IsPet)> ResolveAnchorAsync(CancellationToken ct)
    {
        if (_identity is null) return ("", false);
        try
        {
            var p = await _identity.GetActiveProfileAsync(ct).ConfigureAwait(false);
            return p is null ? ("", false) : (p.GetConsistencyAnchor(), p.IsPet);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.Milestone] identity fallback: " + ex.Message);
            return ("", false);
        }
    }

    private static ThemeTemplate TemplateOf(PlutoMilestoneTheme theme)
    {
        for (var i = 0; i < Templates.Length; i++) if (Templates[i].Theme == theme) return Templates[i];
        return Templates[^1];
    }

    private static string CleanNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return "";
        var s = note.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        return s.Length > MaxNoteLength ? s[..MaxNoteLength].TrimEnd() : s;
    }

    private static uint StableHash(string s)
    {
        var h = 2166136261u;
        for (var i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619u; }
        return h;
    }
}
