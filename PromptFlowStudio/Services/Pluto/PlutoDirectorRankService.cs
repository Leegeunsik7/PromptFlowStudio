using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🎬 Pluto 디렉터 랭크 — 누적 XP → 시네마토그래피 티어 · 프롬프트 광학 보강.</summary>
public interface IPlutoDirectorRankService
{
    /// <summary>현재 누적 XP(학습 메모리 합산 · 실패 시 0).</summary>
    int CurrentXp { get; }

    PlutoDirectorTier GetCurrentTier();

    /// <summary>현재 티어 광학 모디파이어를 프롬프트 말미에 결합(멱등 · 빈 프롬프트는 그대로).</summary>
    string EnrichPromptWithOptics(string basePrompt);

    /// <summary>XP 변경 통지(백그라운드 스레드 가능).</summary>
    event Action? Changed;

    /// <summary>학습 메모리 지연 적재([부분 구제] — 실패 시 0 XP 기준, 취소만 전파).</summary>
    Task EnsureLoadedAsync(CancellationToken ct = default);
}

/// <summary>
/// 🎬 Pluto Autonomous Prompt Evolution — <see cref="PlutoLearningService"/> 누적 XP(pluto_trend_memory.json)를
/// 4단계 디렉터 랭크(내추럴 비기너 → 감성 스토리텔러 → 시네마틱 마스터 → 바이럴 마에스트로)로 환산,
/// 티어별 광학 키워드를 코파일럿 · 마일스톤 프롬프트(LLM 요청 + 결정론 템플릿)에 자동 주입.
/// </summary>
public sealed class PlutoDirectorRankService : IPlutoDirectorRankService, IDisposable
{
    public const int MaxLevel = 4;

    /// <summary>🎬 티어 사전(오름차순 · 고정).</summary>
    public static readonly PlutoDirectorTier[] Tiers =
    {
        new(1, "내추럴 비기너", "Natural Storyteller", 0, 20,
            new[] { "standard warm daylight", "50mm lens", "clean natural focus" },
            ", standard warm daylight, 50mm lens, clean natural focus",
            new[] { "50mm 표준 렌즈", "따스한 자연광" }),
        new(2, "감성 스토리텔러", "Emotional Colorist", 20, 50,
            new[] { "85mm portrait framing", "f/1.4 shallow depth of field", "golden-hour rim backlight", "delicate film grain" },
            ", cinematic 85mm portrait framing, f/1.4 shallow depth of field, warm golden-hour rim backlight, delicate film grain, natural skin tones",
            new[] { "85mm f/1.4", "골든아워 역광", "필름 그레인" }),
        new(3, "시네마틱 마스터", "Cinematic Master", 50, 100,
            new[] { "anamorphic horizontal lens flare", "volumetric light rays (Tyndall effect)", "subtle dolly push-in", "4K skin texture retention" },
            ", anamorphic horizontal lens flare, volumetric light rays with Tyndall effect, subtle dolly push-in, 4K skin texture retention",
            new[] { "아나모픽 렌즈 플레어", "틴들 볼류메트릭 광선", "4K 피부 텍스처" }),
        new(4, "바이럴 마에스트로", "Viral Maestro", 100, 200,
            new[] { "0.5s micro whip-zoom hook", "algorithmic beat-synced pacing", "dynamic atmospheric contrast" },
            ", 0.5s micro whip-zoom hook, algorithmic beat-synced pacing, dynamic atmospheric contrast, anamorphic cinematic depth",
            new[] { "0.5초 휩줌 훅", "비트 싱크 편집", "다이내믹 대기 대비" }),
    };

    private readonly PlutoLearningService? _learning;
    private readonly Func<int>? _xpProvider;
    private int _disposed;

    /// <summary>🧠 학습 메모리 결선(DI 기본). null → 항상 Lv.1.</summary>
    public PlutoDirectorRankService(PlutoLearningService? learning)
    {
        _learning = learning;
        if (learning is not null) learning.Changed += OnLearningChanged;
    }

    /// <summary>⚡ XP 공급자 직접 지정(테스트 · 오프라인).</summary>
    public PlutoDirectorRankService(Func<int> xpProvider)
    {
        _xpProvider = xpProvider ?? throw new ArgumentNullException(nameof(xpProvider));
    }

    public event Action? Changed;

    public int CurrentXp
    {
        get
        {
            try { return Math.Max(0, _xpProvider is not null ? _xpProvider() : _learning?.CurrentXP ?? 0); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Director] xp: " + ex.Message); return 0; }   // [부분 구제] Lv.1 기준
        }
    }

    public PlutoDirectorTier GetCurrentTier() => TierFor(CurrentXp);

    /// <summary>XP → 티어(음수 → Lv.1 · 100+ → Lv.4).</summary>
    public static PlutoDirectorTier TierFor(int xp)
    {
        for (var i = Tiers.Length - 1; i > 0; i--)
            if (xp >= Tiers[i].RequiredXp) return Tiers[i];
        return Tiers[0];
    }

    /// <summary>티어 내 진행률 0~100(최고 티어는 NextTierXp 기준 포화).</summary>
    public static int ProgressPercent(int xp, PlutoDirectorTier tier)
    {
        // long 산술 — int.MaxValue 급 XP · 비정상 티어 경계에서도 오버플로/0 나눗셈 없이 0~100 포화.
        var span = (long)tier.NextTierXp - tier.RequiredXp;
        if (span <= 0 || xp >= tier.NextTierXp) return 100;
        return (int)Math.Clamp(((long)xp - tier.RequiredXp) * 100 / span, 0L, 100L);
    }

    /// <summary>🪐 배지 라벨 — "🪐 Pluto Lv.2 감성 스토리텔러 (25/50 XP)".</summary>
    public static string BadgeLabel(int xp, PlutoDirectorTier tier)
        => "🪐 Pluto Lv." + tier.Level.ToString(CultureInfo.InvariantCulture) + " " + tier.Title
           + " (" + Math.Max(0, xp).ToString(CultureInfo.InvariantCulture) + "/" + tier.NextTierXp.ToString(CultureInfo.InvariantCulture) + " XP)";

    /// <summary>🪐 통합 HUD 칭호 — "🪐 감성 스토리텔러 (Lv.2)".</summary>
    public static string HudTitle(PlutoDirectorTier tier)
        => "🪐 " + tier.Title + " (Lv." + tier.Level.ToString(CultureInfo.InvariantCulture) + ")";

    /// <summary>🪐 통합 HUD XP — "(25/50 XP)".</summary>
    public static string XpLabel(int xp, PlutoDirectorTier tier)
        => "(" + Math.Max(0, xp).ToString(CultureInfo.InvariantCulture) + "/" + tier.NextTierXp.ToString(CultureInfo.InvariantCulture) + " XP)";

    /// <summary>🪐 해금된(현재 티어 이하) 한글 광학 특성 누적 — "50mm 표준 렌즈, 따스한 자연광, 85mm f/1.4, …".</summary>
    public static string UnlockedOpticsTooltip(PlutoDirectorTier tier)
    {
        var sb = new System.Text.StringBuilder(160);
        for (var i = 0; i < Tiers.Length && Tiers[i].Level <= tier.Level; i++)
        {
            var traits = Tiers[i].KoreanTraits;
            for (var j = 0; j < traits.Count; j++)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(traits[j]);
            }
        }
        return sb.ToString();
    }

    public string EnrichPromptWithOptics(string basePrompt) => Enrich(basePrompt, GetCurrentTier());

    /// <summary>프롬프트 말미에 티어 모디파이어 결합 — 이미 포함 시 그대로(멱등). 마침표 종결은 보존.</summary>
    public static string Enrich(string basePrompt, PlutoDirectorTier tier)
    {
        if (string.IsNullOrWhiteSpace(basePrompt)) return basePrompt ?? "";
        var core = tier.PromptModifier.AsSpan().TrimStart(", ");
        if (basePrompt.AsSpan().IndexOf(core, StringComparison.OrdinalIgnoreCase) >= 0) return basePrompt;
        var b = basePrompt.AsSpan().TrimEnd();
        var period = b.Length > 0 && b[^1] == '.';
        if (period) b = b[..^1].TrimEnd();
        return string.Concat(b, tier.PromptModifier, period ? "." : "");
    }

    /// <summary>LLM 요청 지시문 1줄(코파일럿 · 마일스톤 공용).</summary>
    public static string LlmDirective(PlutoDirectorTier tier)
        => "Director rank Lv." + tier.Level.ToString(CultureInfo.InvariantCulture) + " " + tier.EnglishTitle
           + " — weave these cinematography optics into every act/scene prompt: " + string.Join(", ", tier.CinematographyKeywords);

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_learning is null) return;
        try { await _learning.EnsureLoadedAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Director] load: " + ex.Message); }   // [부분 구제] 0 XP 기준
    }

    private void OnLearningChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Director] changed: " + ex.Message); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_learning is not null) _learning.Changed -= OnLearningChanged;
        Changed = null;
    }
}
