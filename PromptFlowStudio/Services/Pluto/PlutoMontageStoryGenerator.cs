using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🎬 몽타주 3막 KO/EN 이중 자막 생성기 — 클립 경계(StartSec/EndSec) 정렬 큐.</summary>
public interface IPlutoMontageStoryGenerator
{
    /// <summary>
    /// 📝 몽타주 플랜(2~5컷) → 컷당 1큐(Act 1 Hook · Act 2 Rhythm · Act 3 Climax) — 정밀 초 큐(<see cref="SubtitleCue.Precise"/>).
    /// Gemini → PlutoCore → 로컬 게이트웨이 → 결정론 템플릿. 호출자 취소만 전파 · 그 외 실패 → 템플릿.
    /// </summary>
    Task<IReadOnlyList<SubtitleCue>> GenerateThreeActSubtitlesAsync(PlutoMontagePlan plan, IReadOnlyList<PlutoCuratedPhotoCandidate>? curatedCuts = null,
        string? themeHint = null, CancellationToken ct = default);

    /// <summary>직전 생성이 LLM 응답을 1컷 이상 채택했는지.</summary>
    bool LastUsedLlm { get; }
}

/// <summary>
/// 🎬 Pluto 몽타주 스토리 생성기 — 컷 구간: Act1 <c>[0.20, end₀−0.15]</c> · 중간 <c>[startᵢ+0.15, endᵢ−0.15]</c> · Act3 <c>[startₗ+0.15, 14.70]</c>,
/// xfade 중앙(컷 포인트) ±0.05초로 인접 큐 비중첩 보장 · 전 구간 <c>[0, 15]</c> 내 클램프.
/// 오답 노트(<see cref="IPlutoMistakeNotebookService.BuildNegativeConstraintPrompt"/>) 부정 제약을 프롬프트에 주입하고 차단 토큰 포함 문구는 템플릿으로 대체.
/// </summary>
public sealed class PlutoMontageStoryGenerator : IPlutoMontageStoryGenerator
{
    public const double HookStartSec = 0.20;
    public const double EdgePadSec = 0.15;
    public const double ClimaxEndSec = 14.70;
    public const double CutGuardSec = 0.05;
    public const double MinCueSec = 0.30;
    public const double TimelineMaxSec = 15.0;
    public const int MaxKoLength = 40;
    public const int MaxEnLength = 72;
    public const double Temperature = 0.8;

    public const string LlmInstruction =
        "You are Pluto, a warm short-form montage storyteller. Write ONE bilingual subtitle line per photo cut of a 15-second vertical montage, "
        + "following a 3-act arc: Act 1 = scroll-stopping hook, Act 2 = rhythmic everyday context, Act 3 = emotional climax. "
        + "Korean (ko) is primary (<= 22 Korean characters, natural spoken tone, no hashtags, no emoji). English (en) is a faithful short translation (<= 60 chars). "
        + "Respond with JSON only: {\"cuts\":[{\"cut\":1,\"act\":1,\"ko\":\"...\",\"en\":\"...\"}, ...]} with exactly one entry per cut in order.";

    private readonly IPlutoLlmClient? _llm;
    private readonly IPlutoMistakeNotebookService? _notebook;
    private volatile bool _lastUsedLlm;

    /// <summary>LLM 전체 예산(체인 각 단계 타임아웃은 브리지 정책 유지).</summary>
    public TimeSpan? LlmBudget { get; init; }

    public bool LastUsedLlm => _lastUsedLlm;

    public PlutoMontageStoryGenerator(IPlutoLlmClient? llm = null, IPlutoMistakeNotebookService? notebook = null)
    {
        _llm = llm;
        _notebook = notebook;
    }

    public async Task<IReadOnlyList<SubtitleCue>> GenerateThreeActSubtitlesAsync(PlutoMontagePlan plan, IReadOnlyList<PlutoCuratedPhotoCandidate>? curatedCuts = null,
        string? themeHint = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ct.ThrowIfCancellationRequested();
        _lastUsedLlm = false;
        var windows = ComputeCueWindows(plan);
        if (windows.Length == 0) return Array.Empty<SubtitleCue>();

        var suppressed = SafeSuppressed();
        var local = BuildTemplateLines(windows.Length, themeHint, Seed(plan), suppressed);
        string? json = null;
        if (_llm is not null)
        {
            var user = BuildUserPrompt(windows, curatedCuts, themeHint, SafeNegativeConstraint());
            try { json = await PlutoLlmBridge.TryCompleteAsync(_llm, LlmInstruction, user, Temperature, ct, LlmBudget).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.MontageStory] llm fallback: " + ex.Message); json = null; }
        }
        var lines = MergeLlm(local, json, suppressed, out var used);
        _lastUsedLlm = used;

        var cues = new SubtitleCue[windows.Length];
        for (var i = 0; i < cues.Length; i++)
            cues[i] = SubtitleCue.Precise(windows[i].Start, windows[i].End, lines[i].Ko, lines[i].En);
        return cues;
    }

    /// <summary>⚡ LLM 미사용 결정론 큐(프리뷰 즉시 표시용 초안).</summary>
    public static IReadOnlyList<SubtitleCue> GenerateLocal(PlutoMontagePlan plan, string? themeHint = null, IReadOnlySet<string>? suppressed = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var windows = ComputeCueWindows(plan);
        var lines = BuildTemplateLines(windows.Length, themeHint, Seed(plan), suppressed);
        var cues = new SubtitleCue[windows.Length];
        for (var i = 0; i < cues.Length; i++)
            cues[i] = SubtitleCue.Precise(windows[i].Start, windows[i].End, lines[i].Ko, lines[i].En);
        return cues;
    }

    // ── ⏱ 구간 산출 ──

    /// <summary>
    /// ⏱ 컷별 자막 구간 — 클립 <c>[StartSec, StartSec+DurationSec]</c> 기준 패딩 후 xfade 중앙 ±<see cref="CutGuardSec"/> 로 비중첩.
    /// 2~5컷 외 → 빈 배열. 모든 구간 ⊂ <c>[0, 15]</c> · 길이 ≥ <see cref="MinCueSec"/>(불가 시 컷 중앙 최소 창).
    /// </summary>
    public static (double Start, double End)[] ComputeCueWindows(PlutoMontagePlan plan)
    {
        var clips = plan?.Clips;
        if (clips is null || clips.Count < PlutoMontageCompiler.MinClips || clips.Count > PlutoMontageCompiler.MaxClips) return Array.Empty<(double, double)>();
        var n = clips.Count;
        var total = plan!.TotalDurationSec > 0 ? Math.Min(plan.TotalDurationSec, TimelineMaxSec) : TimelineMaxSec;
        var lastEnd = Math.Min(ClimaxEndSec, total - (TimelineMaxSec - ClimaxEndSec));
        var w = new (double Start, double End)[n];
        for (var i = 0; i < n; i++)
        {
            var c = clips[i];
            var start = i == 0 ? HookStartSec : c.StartSec + EdgePadSec;
            var end = i == n - 1 ? lastEnd : c.StartSec + c.DurationSec - EdgePadSec;
            if (i > 0)   // 이전 컷과의 xfade 중앙 이후로
            {
                var prev = clips[i - 1];
                var cut = c.StartSec + Math.Max(0, prev.TransitionOutSec) * 0.5;
                start = Math.Max(start, cut + CutGuardSec);
            }
            if (i < n - 1)   // 다음 컷과의 xfade 중앙 이전으로
            {
                var next = clips[i + 1];
                var cut = next.StartSec + Math.Max(0, c.TransitionOutSec) * 0.5;
                end = Math.Min(end, cut - CutGuardSec);
            }
            start = Math.Clamp(start, 0, TimelineMaxSec);
            end = Math.Clamp(end, 0, TimelineMaxSec);
            if (end - start < MinCueSec)   // 극단적으로 짧은 컷 → 컷 중앙 최소 창
            {
                var mid = Math.Clamp(c.StartSec + c.DurationSec * 0.5, MinCueSec * 0.5, TimelineMaxSec - MinCueSec * 0.5);
                start = mid - MinCueSec * 0.5;
                end = mid + MinCueSec * 0.5;
            }
            w[i] = (Math.Round(start, 3), Math.Round(end, 3));
        }
        for (var i = 1; i < n; i++)   // 최종 단조 보정(최소 창 폴백 후에도 비중첩)
        {
            if (w[i].Start < w[i - 1].End + CutGuardSec)
                w[i].Start = Math.Round(Math.Min(w[i - 1].End + CutGuardSec, w[i].End - 0.05), 3);
            if (w[i].Start < w[i - 1].End) w[i - 1].End = Math.Round(Math.Max(w[i - 1].Start + 0.05, w[i].Start - 0.001), 3);
        }
        return w;
    }

    /// <summary>🎭 컷 위치 → 막 번호(첫 1 · 끝 3 · 중간 2).</summary>
    public static int ActOf(int position, int count) => position == 0 ? 1 : position == count - 1 ? 3 : 2;

    // ── 🧠 프롬프트 ──

    /// <summary>📝 LLM 사용자 프롬프트 — 컷 수 · 막 · 시간 구간 · 큐레이션 역할 · 테마 · 부정 제약.</summary>
    public static string BuildUserPrompt((double Start, double End)[] windows, IReadOnlyList<PlutoCuratedPhotoCandidate>? curatedCuts, string? themeHint, string? negativeConstraint)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(256 + 96 * windows.Length);
        sb.Append("Cut count: ").Append(windows.Length.ToString(inv)).Append('\n');
        var theme = string.IsNullOrWhiteSpace(themeHint) ? "everyday family moments" : Clip(themeHint.Replace('\n', ' ').Trim(), 120);
        sb.Append("Theme hint: ").Append(theme).Append('\n');
        for (var i = 0; i < windows.Length; i++)
        {
            sb.Append("Cut ").Append((i + 1).ToString(inv)).Append(": act ").Append(ActOf(i, windows.Length).ToString(inv))
              .Append(" (").Append(windows[i].Start.ToString("0.00", inv)).Append("s-").Append(windows[i].End.ToString("0.00", inv)).Append("s)");
            if (curatedCuts is not null && i < curatedCuts.Count && curatedCuts[i] is { } cc && !string.IsNullOrWhiteSpace(cc.NarrativeRoleLabel))
                sb.Append(" role: ").Append(cc.NarrativeRoleLabel);
            sb.Append('\n');
        }
        if (!string.IsNullOrWhiteSpace(negativeConstraint)) sb.Append(negativeConstraint.Trim()).Append('\n');
        return sb.ToString();
    }

    private string SafeNegativeConstraint()
    {
        try { return _notebook?.BuildNegativeConstraintPrompt() ?? ""; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.MontageStory] notebook: " + ex.Message); return ""; }
    }

    private IReadOnlySet<string>? SafeSuppressed()
    {
        try { return _notebook?.GetSuppressedTokens(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.MontageStory] notebook: " + ex.Message); return null; }
    }

    // ── 🧩 LLM 병합(스키마 즉시 검증) ──

    /// <summary>LLM JSON(<c>{"cuts":[…]}</c> 또는 루트 배열) → 컷별 KO/EN 채택 · 무효/차단 토큰 → 템플릿 유지.</summary>
    internal static (string Ko, string En)[] MergeLlm((string Ko, string En)[] local, string? json, IReadOnlySet<string>? suppressed, out bool usedLlm)
    {
        usedLlm = false;
        var result = ((string Ko, string En)[])local.Clone();
        using var doc = PlutoLlmBridge.TryParse(json);
        if (doc is null) return result;
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("cuts", out var cuts)) root = cuts;
        if (root.ValueKind != JsonValueKind.Array) return result;
        var ordinal = 0;
        foreach (var el in root.EnumerateArray())
        {
            var idx = ordinal++;
            if (el.ValueKind != JsonValueKind.Object) continue;
            if (el.TryGetProperty("cut", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var cn)) idx = cn - 1;
            if ((uint)idx >= (uint)result.Length) continue;
            var ko = CleanLine(PlutoLlmBridge.Str(el, "ko", MaxKoLength * 2), MaxKoLength);
            var en = CleanLine(PlutoLlmBridge.Str(el, "en", MaxEnLength * 2), MaxEnLength);
            if (ko.Length == 0 || !HasLatinLetter(en) || ContainsSuppressed(ko, suppressed) || ContainsSuppressed(en, suppressed)) continue;
            result[idx] = (ko, en);
            usedLlm = true;
        }
        return result;
    }

    // ── 📚 결정론 템플릿 ──

    private sealed record Theme(string Id, string[] Keywords, (string Ko, string En)[] Hook, (string Ko, string En)[] Rhythm, (string Ko, string En)[] Climax);

    private static readonly Theme[] Themes =
    {
        new("birthday", new[] { "생일", "돌", "birthday", "party", "축하", "케이크", "cake" },
            new[] { ("오늘은 특별한 날이에요", "Today is a very special day"), ("촛불 앞에 모인 우리", "Gathered around the candles") },
            new[] { ("웃음이 끊이지 않던 순간", "Laughter that never stopped"), ("하나하나가 다 선물이었어", "Every moment was a gift"), ("모두가 너를 위해 모였지", "Everyone came together for you") },
            new[] { ("태어나줘서 정말 고마워", "Thank you for being born"), ("내년에도 꼭 함께하자", "Let's celebrate together next year too") }),
        new("travel", new[] { "여행", "travel", "trip", "바다", "beach", "산", "mountain", "캠핑", "camping" },
            new[] { ("우리, 떠나볼까?", "Shall we go?"), ("낯선 길 위의 첫 장면", "The first scene on a new road") },
            new[] { ("발길 닿는 곳마다 추억", "Memories with every step"), ("바람마저 다르게 느껴졌어", "Even the wind felt different"), ("함께라서 더 멀리 왔어", "We came this far together") },
            new[] { ("이 풍경, 오래 기억할게", "I'll remember this view forever"), ("다음 여행도 너와 함께", "Next journey, with you again") }),
        new("pet", new[] { "강아지", "고양이", "반려", "pet", "dog", "cat", "puppy", "kitten", "멍", "냥" },
            new[] { ("이 눈빛, 반칙 아니야?", "Isn't this look unfair?"), ("오늘도 귀여움 출동", "Cuteness reporting for duty") },
            new[] { ("꼬리부터 신난 하루", "A day that starts with a wagging tail"), ("작은 발자국의 대모험", "The great adventure of tiny paws"), ("졸다가 놀다가 또 졸다가", "Napping, playing, napping again") },
            new[] { ("곁에 있어줘서 고마워", "Thank you for staying by my side"), ("너는 우리 가족이야", "You are our family") }),
        new("everyday", Array.Empty<string>(),
            new[] { ("평범한 하루가 반짝인 순간", "When an ordinary day sparkled"), ("이 장면, 그냥 지나칠 수 없지", "A scene too good to scroll past") },
            new[] { ("작은 웃음이 모여서", "Little smiles adding up"), ("천천히, 우리만의 속도로", "Slowly, at our own pace"), ("하루하루가 쌓여 가", "Day by day, it all adds up") },
            new[] { ("이 순간을 오래 간직할게", "I'll hold on to this moment"), ("그래서 오늘도 사랑해", "And that's why I love you today too") }),
    };

    private static Theme DetectTheme(string? hint)
    {
        if (!string.IsNullOrWhiteSpace(hint))
            for (var t = 0; t < Themes.Length; t++)
                for (var k = 0; k < Themes[t].Keywords.Length; k++)
                    if (hint.Contains(Themes[t].Keywords[k], StringComparison.OrdinalIgnoreCase)) return Themes[t];
        return Themes[^1];
    }

    /// <summary>📚 컷 수 → 템플릿 KO/EN — 중간 컷은 서로 다른 Rhythm 줄 · 차단 토큰 포함 줄은 회피(전부 차단 → 기본 줄).</summary>
    internal static (string Ko, string En)[] BuildTemplateLines(int count, string? themeHint, uint seed, IReadOnlySet<string>? suppressed)
    {
        if (count <= 0) return Array.Empty<(string, string)>();
        var theme = DetectTheme(themeHint);
        var lines = new (string Ko, string En)[count];
        for (var i = 0; i < count; i++)
        {
            var act = ActOf(i, count);
            var bank = act == 1 ? theme.Hook : act == 3 ? theme.Climax : theme.Rhythm;
            var offset = act == 2 ? i - 1 : 0;
            lines[i] = PickAllowed(bank, (int)((seed + (uint)offset) % (uint)bank.Length), suppressed);
        }
        return lines;
    }

    private static (string Ko, string En) PickAllowed((string Ko, string En)[] bank, int start, IReadOnlySet<string>? suppressed)
    {
        for (var k = 0; k < bank.Length; k++)
        {
            var c = bank[(start + k) % bank.Length];
            if (!ContainsSuppressed(c.Ko, suppressed) && !ContainsSuppressed(c.En, suppressed)) return c;
        }
        return bank[start % bank.Length];
    }

    private static bool ContainsSuppressed(string text, IReadOnlySet<string>? suppressed)
    {
        if (suppressed is null || suppressed.Count == 0 || string.IsNullOrEmpty(text)) return false;
        foreach (var t in suppressed)
            if (t is { Length: >= 2 } && text.Contains(t.TrimStart('#'), StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static uint Seed(PlutoMontagePlan plan)
    {
        var h = 2166136261u;
        for (var i = 0; i < plan.Clips.Count; i++)
        {
            var p = plan.Clips[i].ImagePath ?? "";
            for (var j = 0; j < p.Length; j++) { h ^= p[j]; h *= 16777619u; }
        }
        return h;
    }

    private static string CleanLine(string s, int max)
    {
        if (s.Length == 0) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (char.IsSurrogate(ch) || ch == '#' || char.IsControl(ch)) continue;   // 이모지 · 해시태그 · 제어 문자 제거
            sb.Append(ch);
        }
        return Clip(sb.ToString().Trim().Trim('"'), max);
    }

    private static bool HasLatinLetter(string s)
    {
        for (var i = 0; i < s.Length; i++) if (s[i] is >= 'a' and <= 'z' or >= 'A' and <= 'Z') return true;
        return false;
    }

    private static string Clip(string s, int max) => s.Length > max ? s[..max].TrimEnd() : s;
}
