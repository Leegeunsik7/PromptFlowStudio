using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Prompt;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Services.AI;

/// <summary>Gemini 강화 결과 — <see cref="UsedFallback"/>=true 면 로컬 룰셋으로 강화됨(키 없음/호출 실패).</summary>
public sealed record GeminiEnhanceOutcome(string Prompt, bool UsedFallback, string Message, string? SourceKorean = null)
{
    /// <summary>한글 원문을 영문 시네마틱으로 번역·치환한 결과인지.</summary>
    public bool Translated => SourceKorean is not null;
}

/// <summary>스토리보드 제미나이 디렉터 1컷 — 프롬프트 · 카메라 앵글 · 듀레이션(초).</summary>
public sealed record DirectorShot(int Index, string Title, string Prompt, string Camera, float DurationSec);

/// <summary>✨ 대량생산 일괄 기획 1편 — 제목·영문 프롬프트·카메라·태그.</summary>
public sealed record BatchPromptItem(int Index, string Title, string Prompt, string Camera, IReadOnlyList<string> Tags);

/// <summary>✨ 대량생산 일괄 기획 결과 — UsedFallback=true 면 로컬 템플릿(일부/전체).</summary>
public sealed record BatchPlanResult(IReadOnlyList<BatchPromptItem> Items, bool UsedFallback, string Message);

/// <summary>SNS 발행 SEO 메타 — <see cref="UsedFallback"/>=true 면 로컬 템플릿(키 없음/호출 실패/스키마 불일치).</summary>
/// <summary>📈 증시 시황 무드.</summary>
public enum MarketMood { BullRally, BearDefense, HighVolatility, MidnightWallStreet }

/// <summary>시황 무드 칩 프리셋 — Tokens 는 프롬프트에 주입되는 조명·색감·동세 블록.</summary>
public sealed record MarketMoodPreset(MarketMood Mood, string Emoji, string Label, string English, string Tokens);

/// <summary>3-Way 스타일 변형 1종 — 프롬프트·추천 카메라·결정적 시드.</summary>
public sealed record StyleVariant(int Index, string StyleKey, string Title, string Prompt, string Camera, long Seed);

/// <summary>3-Way 변형 결과.</summary>
public sealed record ThreeWayVariantResult(IReadOnlyList<StyleVariant> Variants, bool UsedFallback, string Message);

public sealed record SnsSeoMetadata(string Title, string Description, IReadOnlyList<string> Tags, bool UsedFallback, string Message);

/// <summary>
/// [PromptFlowStudio] ✨ 제미나이 AI 스튜디오 엔진 — Wan 2.1 워크플로우 결합형 프롬프트 서비스(순수 BCL).
///   ▸ 듀레이션 인지 System Instruction(15s 앰비언트 → 루프 친화 미세 동세 지침 강제 주입).
///   ▸ Vision I2V 역추출(<see cref="AnalyzeReferenceImageAsync"/> · 이미지 base64 인라인).
///   ▸ 스토리보드 4컷 자동 디렉팅(<see cref="DirectStoryboardAsync"/>).
///   ▸ 키 없음/네트워크/HTTP/스키마 오류 → 예외 없이 <see cref="FallbackLocalEnhance"/>/<see cref="BuildLocalShots"/> 로 무중단.
///   ▸ 429/5xx 지수 백오프 1회 재시도, 호출 전체 <see cref="CallTimeout"/> 상한.
/// </summary>
public sealed class GeminiPromptService
{
    public const string Model = "gemini-2.5-flash";
    public const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/models/" + Model + ":generateContent";

    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(25);
    private const int MaxAttempts = 2;
    private const int BaseBackoffMs = 400;
    public const long MaxImageBytes = GeminiClient.MaxInlineBytes;

    public const string BaseInstruction =
        "You are a cinematic prompt engineer for the Wan 2.1 video model. " +
        "Rewrite the user's idea into ONE vivid English prompt (max 120 words) that keeps the original subject and action unchanged, " +
        "expanding it with volumetric lighting, detailed material textures, depth of field, lens character, atmosphere and color grading. " +
        "Output only the final prompt text — no quotes, no markdown, no explanations.";

    public const string AmbientInstruction =
        "Generate continuous subtle motion, slow camera drift, soft lighting shifts, zero abrupt cuts, " +
        "loop-friendly ambient aesthetic suitable for background wallpaper.";

    public const string I2VInstruction =
        "Analyze the attached reference image (color palette, main subject, composition, lighting). " +
        "Write ONE English Wan 2.1 image-to-video prompt (max 100 words) that describes the existing scene faithfully " +
        "and adds natural, physically plausible motion of the subject and environment plus a gentle camera move. " +
        "Do not introduce new subjects. Output only the prompt text.";

    public const string DirectorInstruction =
        "You are a film director. Split the one-line story into exactly 4 shots for the Wan 2.1 video model. " +
        "Respond with JSON only: {\"shots\":[{\"title\":string,\"prompt\":string,\"camera\":string,\"durationSec\":number}]} " +
        "with 4 items. Prompts are vivid English, max 80 words each; durationSec is 5.";

    /// <summary>로컬 폴백 시네마틱 키워드.</summary>
    public const string LocalCinematicTokens = "8K photorealistic, shallow depth of field, 35mm lens, smooth motion, soft volumetric lighting, professional color grading";
    /// <summary>15s 앰비언트 로컬 폴백 루프 키워드(핑퐁 루프 · 미세 동세).</summary>
    public const string LocalAmbientTokens = "seamless ping-pong loop, continuous subtle motion, slow camera drift, soft lighting shifts, no abrupt cuts, ambient wallpaper aesthetic";
    public const string LocalI2VTokens = "natural subtle subject motion, gentle parallax camera move, preserve original composition and color palette";

    /// <summary>한글 입력 전용 — 의미 보존 영문 시네마틱 번역 모드.</summary>
    public const string TranslationInstruction =
        "Professional Cinematic Video Prompt Translation. " +
        "The user's prompt is written in Korean. Translate it into ONE vivid English prompt (max 120 words) for the Wan 2.1 video model, " +
        "preserving every subject, action, count, color and mood of the Korean original exactly — do not add or remove story elements. " +
        "Then enrich it with cinematic craft: lighting setup, camera lens and movement, depth of field, material textures, color grading, 8k detail. " +
        "Output English only — no Korean characters, no quotes, no markdown, no explanations.";

    public const string FallbackNotice = "로컬 사전 기반으로 강화되었습니다";
    public const string TranslateFallbackNotice = "로컬 용어집으로 한→영 변환되었습니다(일부 원문 유지)";

    /// <summary>한글(음절·자모·호환 자모) 포함 여부 — 할당 0.</summary>
    public static bool ContainsHangul(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var c in s)
            if (c is (>= '가' and <= '힣') or (>= 'ᄀ' and <= 'ᇿ') or (>= '㄰' and <= '㆏')) return true;
        return false;
    }

    /// <summary>한글 프롬프트 로컬 폴백 — 오프라인 용어집 치환 후 시네마틱 토큰 결합.</summary>
    public static string FallbackLocalTranslate(string? koPrompt, VideoDurationMode mode = VideoDurationMode.Sec5_Single)
    {
        var s = (koPrompt ?? "").Trim();
        if (s.Length == 0) return "";
        var sb = new StringBuilder(s);
        foreach (var (ko, en) in VisualGlossary) sb.Replace(ko, en);
        return FallbackLocalEnhance(Media.GlobalMultiDubbingEngine.Translate(sb.ToString(), "en"), mode);
    }

    /// <summary>오프라인 시각 용어집(긴 표현 우선). 미상 어휘는 원문 유지([부분 구제]).</summary>
    private static readonly (string Ko, string En)[] VisualGlossary =
    {
        ("골든리트리버", "golden retriever"), ("고양이", "cat"), ("강아지", "puppy"), ("아기", "baby"),
        ("노을", "sunset"), ("일출", "sunrise"), ("밤하늘", "night sky"), ("해변", "beach"), ("바다", "ocean"),
        ("도시", "city"), ("네온", "neon"), ("숲속", "forest"), ("빗속", "in the rain"), ("눈밭", "snowfield"),
        ("트레이딩", "trading"), ("데스크", "desk"), ("모니터", "monitor"), ("차트", "chart"), ("카페", "cafe"),
        ("달리는", "running"), ("걷는", "walking"), ("날아가는", "flying"), ("웃는", "smiling"),
        ("의 ", " "), ("에서 ", " at "),
    };

    private readonly HttpClient _http;
    private readonly Func<string?> _apiKey;

    public GeminiPromptService(HttpClient http, Func<string?> apiKeyProvider)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _apiKey = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));
    }

    public GeminiPromptService(HttpClient http, GeminiConfigService cfg) : this(http, () => cfg.ApiKey) { }

    /// <summary>"5s"/"10s"/"15s"/"15s-Ambient" 문자열 → 모드(미지정/미인식은 5s).</summary>
    public static VideoDurationMode ParseDuration(string? mode) => (mode ?? "").Trim().ToLowerInvariant() switch
    {
        "10s" => VideoDurationMode.Sec10_Dual,
        "15s" => VideoDurationMode.Sec15_Triple,
        "15s-ambient" or "ambient" => VideoDurationMode.Sec15_AmbientSlow,
        _ => VideoDurationMode.Sec5_Single,
    };

    /// <summary>듀레이션 인지 System Instruction.</summary>
    public static string BuildSystemInstruction(VideoDurationMode mode) => mode switch
    {
        VideoDurationMode.Sec15_AmbientSlow => BaseInstruction + " " + AmbientInstruction,
        VideoDurationMode.Sec10_Dual or VideoDurationMode.Sec15_Triple =>
            BaseInstruction + " The clip is chained from multiple 5-second shots: keep subject, wardrobe and lighting consistent with steady continuous motion.",
        _ => BaseInstruction,
    };

    /// <summary>로컬 룰셋 강화 — 원문 + 시네마틱 토큰(+앰비언트 루프 토큰). 이미 포함된 토큰은 중복 결합하지 않음.</summary>
    public static string FallbackLocalEnhance(string? prompt, VideoDurationMode mode = VideoDurationMode.Sec5_Single, bool imageToVideo = false)
    {
        var src = (prompt ?? "").Trim().TrimEnd(',', '.').TrimEnd();
        var sb = new StringBuilder(src.Length + 256).Append(src);
        if (imageToVideo) AppendOnce(sb, LocalI2VTokens);
        AppendOnce(sb, LocalCinematicTokens);
        if (mode == VideoDurationMode.Sec15_AmbientSlow) AppendOnce(sb, LocalAmbientTokens);
        var s = sb.ToString();
        return s.Length <= PromptSynthesizerService.MaxCharacterLimit ? s : s[..PromptSynthesizerService.MaxCharacterLimit].TrimEnd();

        static void AppendOnce(StringBuilder b, string tokens)
        {
            if (b.ToString().Contains(tokens, StringComparison.OrdinalIgnoreCase)) return;
            if (b.Length > 0) b.Append(", ");
            b.Append(tokens);
        }
    }

    /// <summary>프롬프트 강화(듀레이션 인지). 절대 throw 하지 않음 — 실패 시 로컬 폴백.</summary>
    public async Task<GeminiEnhanceOutcome> EnhancePromptAsync(string? basePrompt, VideoDurationMode mode,
        string? style = null, string? camera = null, CancellationToken ct = default)
    {
        var src = basePrompt ?? "";
        if (string.IsNullOrWhiteSpace(src)) return new(src, false, "입력이 비어 있습니다.");

        var user = new StringBuilder(src.Length + 96).Append("Prompt: ").Append(src.Trim());
        if (!string.IsNullOrWhiteSpace(camera)) user.Append("\nCamera motion: ").Append(camera.Trim());
        if (!string.IsNullOrWhiteSpace(style)) user.Append("\nVisual style: ").Append(style.Trim());

        var korean = ContainsHangul(src);
        var sys = BuildSystemInstruction(mode);
        if (korean) sys = TranslationInstruction + " " + sys;

        var text = await CallAsync(sys, user.ToString(), null, false, 0.6, ct).ConfigureAwait(false);
        var clean = Sanitize(text);
        if (korean)
        {
            var origin = src.Trim();
            // 모델이 한글을 남기면 Wan 2.1 품질 저하 → 번역 실패로 간주하고 로컬 용어집 폴백
            if (clean is null || ContainsHangul(clean)) return new(FallbackLocalTranslate(src, mode), true, TranslateFallbackNotice, origin);
            if (mode == VideoDurationMode.Sec15_AmbientSlow && !clean.Contains("loop", StringComparison.OrdinalIgnoreCase))
                clean = FallbackLocalEnhance(clean, mode);
            return new(clean, false, "Gemini 한→영 시네마틱 번역 완료", origin);
        }
        if (clean is null) return new(FallbackLocalEnhance(src, mode), true, FallbackNotice);
        if (mode == VideoDurationMode.Sec15_AmbientSlow && !clean.Contains("loop", StringComparison.OrdinalIgnoreCase))
            clean = FallbackLocalEnhance(clean, mode);   // 모델이 루프 지침을 누락해도 앰비언트 키워드 보장
        return new(clean, false, "Gemini 강화 완료");
    }

    public Task<GeminiEnhanceOutcome> EnhancePromptAsync(string? basePrompt, string? durationMode, CancellationToken ct = default)
        => EnhancePromptAsync(basePrompt, ParseDuration(durationMode), null, null, ct);

    /// <summary>참조 이미지 → Wan 2.1 I2V 프롬프트 역생성(Vision). 실패 시 힌트 기반 로컬 폴백.</summary>
    public async Task<GeminiEnhanceOutcome> AnalyzeReferenceImageAsync(string imagePath, string? userHint,
        VideoDurationMode mode = VideoDurationMode.Sec5_Single, CancellationToken ct = default)
    {
        var hint = userHint ?? "";
        GeminiEnhanceOutcome Fallback(string msg) => new(FallbackLocalEnhance(hint, mode, imageToVideo: true), true, msg);

        if (string.IsNullOrWhiteSpace(imagePath)) return Fallback(FallbackNotice);
        byte[] bytes;
        try
        {
            var fi = new FileInfo(imagePath);
            if (!fi.Exists || fi.Length == 0 || fi.Length > MaxImageBytes) return Fallback(FallbackNotice + " (이미지 확인 불가)");
            if (!HasKey()) return Fallback(FallbackNotice);
            bytes = await File.ReadAllBytesAsync(imagePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is not OperationCanceledException) Program.Log("GeminiPromptService.ReadImage", ex);
            return Fallback(FallbackNotice);
        }

        var user = new StringBuilder(I2VInstruction.Length + hint.Length + 32).Append(I2VInstruction);
        if (!string.IsNullOrWhiteSpace(hint)) user.Append("\nUser hint: ").Append(hint.Trim());
        var sys = mode == VideoDurationMode.Sec15_AmbientSlow ? AmbientInstruction : "Output only the prompt text.";

        var text = await CallAsync(sys, user.ToString(), new GeminiMedia(MimeOf(imagePath), bytes), false, 0.4, ct).ConfigureAwait(false);
        var clean = Sanitize(text);
        return clean is null ? Fallback(FallbackNotice) : new(clean, false, "Gemini Vision I2V 분석 완료");
    }

    /// <summary>한 줄 스토리 → 정확히 4컷(프롬프트·카메라·듀레이션). 키 없음/실패/스키마 불일치 → 로컬 4컷 템플릿.</summary>
    public async Task<(IReadOnlyList<DirectorShot> Shots, bool UsedFallback)> DirectStoryboardAsync(string? story, CancellationToken ct = default)
    {
        var s = (story ?? "").Trim();
        if (s.Length == 0) return (Array.Empty<DirectorShot>(), true);
        var text = await CallAsync(DirectorInstruction, "Story: " + s, null, true, 0.5, ct).ConfigureAwait(false);
        var parsed = text is null ? null : ParseShots(text);
        return parsed is not null ? (parsed, false) : (BuildLocalShots(s), true);
    }

    /// <summary>로컬 4컷 디렉팅 템플릿(전경 → 클로즈업 → 터미널 → 앰비언트 엔딩).</summary>
    public static IReadOnlyList<DirectorShot> BuildLocalShots(string? story)
    {
        var s = (story ?? "").Trim();
        if (s.Length == 0) return Array.Empty<DirectorShot>();
        return new[]
        {
            new DirectorShot(1, "전체 전경", s + ", wide establishing shot of the whole scene, " + LocalCinematicTokens, "Crane Up / Wide", 5f),
            new DirectorShot(2, "디테일 클로즈업", s + ", close-up of key details and glowing screens, " + LocalCinematicTokens, "Smooth Pan / 35mm", 5f),
            new DirectorShot(3, "핵심 액션", s + ", decisive moment of action, rising tension, " + LocalCinematicTokens, "Dolly Zoom", 5f),
            new DirectorShot(4, "앰비언트 엔딩", s + ", dawn city view with ambient monitor glow, bokeh lights, " + LocalAmbientTokens, "Static / Bokeh", 5f),
        };
    }

    /// <summary>디렉터 JSON 스키마 즉시 검증 — 정확히 4컷·필수 필드 누락 시 null.</summary>
    public static IReadOnlyList<DirectorShot>? ParseShots(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(GeminiClient.StripFence(json));
            var root = doc.RootElement;
            JsonElement arr;
            if (root.ValueKind == JsonValueKind.Array) arr = root;
            else if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("shots", out arr) || arr.ValueKind != JsonValueKind.Array) return null;
            if (arr.GetArrayLength() != 4) return null;

            var list = new DirectorShot[4];
            var i = 0;
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) return null;
                var prompt = Str(e, "prompt");
                if (string.IsNullOrWhiteSpace(prompt)) return null;
                var dur = e.TryGetProperty("durationSec", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out var dv) && dv > 0 && dv <= 30 ? (float)dv : 5f;
                var title = Str(e, "title");
                var cam = Str(e, "camera");
                list[i] = new DirectorShot(i + 1, string.IsNullOrWhiteSpace(title) ? "컷 " + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : title!,
                    prompt!.Trim(), string.IsNullOrWhiteSpace(cam) ? "Static" : cam!.Trim(), dur);
                i++;
            }
            return list;
        }
        catch (JsonException) { return null; }

        static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    // ══════════════════════ ✨ 대량생산 시리즈 일괄 기획 ══════════════════════

    public const int MinBatchCount = 1;
    public const int MaxBatchCount = 20;
    public const string BatchFallbackNotice = "로컬 앵글 템플릿으로 일괄 기획되었습니다";

    public const string BatchPlannerInstruction =
        "You are a series director for short cinematic videos (fintech, trading room, cinematic ambience). " +
        "Given a theme keyword and a count N, create exactly N DISTINCT shots for the Wan 2.1 video model, each with a different camera angle and visual detail. " +
        "Respond with JSON only: {\"items\":[{\"title\":string,\"prompt\":string,\"camera\":string,\"tags\":[string]}]} with N items. " +
        "title: short Korean title (max 30 chars). prompt: vivid English, max 80 words, no line breaks. tags: 3-5 lowercase English keywords.";

    /// <summary>로컬 폴백 앵글/디테일 로테이션 — 핀테크·트레이딩룸·시네마틱.</summary>
    private static readonly (string Title, string Camera, string Detail)[] BatchAngles =
    {
        ("오프닝 전경", "Crane Up / Wide", "wide establishing shot of a high-tech trading floor, rows of glowing monitors"),
        ("차트 클로즈업", "Macro / 100mm", "extreme close-up of candlestick charts and ticker data reflecting on glass"),
        ("트레이더 시점", "Over-the-Shoulder / 35mm", "over-the-shoulder view of a trader scanning multi-screen dashboards"),
        ("데이터 흐름", "Slow Dolly In", "abstract holographic data streams and fintech UI elements flowing in 3D space"),
        ("도시 야경", "Aerial Drone / Orbit", "aerial night view of the financial district skyline with neon reflections"),
        ("키보드 디테일", "Low Angle / 50mm", "low angle detail of fingers on a backlit mechanical keyboard, bokeh screens"),
        ("서버룸", "Tracking Shot", "tracking shot through a server room with blinking LEDs and cool blue haze"),
        ("결정의 순간", "Dolly Zoom", "decisive moment as a market alert flashes, rising tension, dramatic rim light"),
        ("앰비언트 루프", "Static / Bokeh", "calm ambient desk scene at dawn, soft monitor glow, slow drifting bokeh"),
        ("탑뷰 그리드", "Top-Down / 24mm", "top-down view of a minimalist desk with tablets showing live market heatmaps"),
    };

    private static readonly string[] LocalBatchTags = { "fintech", "trading", "cinematic" };

    /// <summary>
    /// 테마 키워드 1개 → N개(1~20) 상이한 앵글의 영문 프롬프트·메타 세트. 키 없음/실패/스키마 불일치 → 로컬 앵글 템플릿.
    /// 응답이 N개 미만이면 로컬 템플릿으로 보충([부분 구제]). 취소 외 throw 하지 않음.
    /// </summary>
    public async Task<BatchPlanResult> GenerateBatchPromptsAsync(string themeKeyword, int count = 5, CancellationToken ct = default)
    {
        var theme = (themeKeyword ?? "").Trim();
        var n = Math.Clamp(count, MinBatchCount, MaxBatchCount);
        if (theme.Length == 0) return new(Array.Empty<BatchPromptItem>(), true, "테마 키워드를 입력하세요.");
        if (!HasKey()) return new(BuildLocalBatchPrompts(theme, n), true, BatchFallbackNotice);

        var user = "Theme: " + theme + "\nCount: " + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var text = await CallAsync(BatchPlannerInstruction, user, null, true, 0.8, ct).ConfigureAwait(false);
        var parsed = text is null ? null : ParseBatchPrompts(text, n);
        if (parsed is null) return new(BuildLocalBatchPrompts(theme, n), true, BatchFallbackNotice);
        if (parsed.Count >= n) return new(parsed, false, "✨ Gemini 시리즈 " + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + "편 기획 완료");

        var local = BuildLocalBatchPrompts(theme, n);
        var merged = new BatchPromptItem[n];
        for (var i = 0; i < n; i++) merged[i] = i < parsed.Count ? parsed[i] : local[i];
        return new(merged, true, "Gemini " + parsed.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "편 + 로컬 보충");
    }

    /// <summary>로컬 앵글 로테이션 템플릿 — 정확히 <paramref name="count"/>개(1~20), 모두 상이한 프롬프트.</summary>
    public static IReadOnlyList<BatchPromptItem> BuildLocalBatchPrompts(string? theme, int count)
    {
        var t = (theme ?? "").Trim();
        if (t.Length == 0) return Array.Empty<BatchPromptItem>();
        var n = Math.Clamp(count, MinBatchCount, MaxBatchCount);
        var list = new BatchPromptItem[n];
        for (var i = 0; i < n; i++)
        {
            var a = BatchAngles[i % BatchAngles.Length];
            var round = i / BatchAngles.Length;
            var take = (round + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var title = round == 0 ? a.Title : a.Title + " " + take;
            var variant = round == 0 ? "" : ", alternate take " + take + " with shifted color temperature";
            list[i] = new BatchPromptItem(i + 1, title, t + ", " + a.Detail + variant + ", " + LocalCinematicTokens, a.Camera, LocalBatchTags);
        }
        return list;
    }

    /// <summary>벌크 기획 JSON 스키마 즉시 검증 — 배열 없음/프롬프트 누락 시 null. 최대 <paramref name="max"/>개, 중복 프롬프트 제거.</summary>
    public static IReadOnlyList<BatchPromptItem>? ParseBatchPrompts(string json, int max)
    {
        try
        {
            using var doc = JsonDocument.Parse(GeminiClient.StripFence(json));
            var root = doc.RootElement;
            JsonElement arr;
            if (root.ValueKind == JsonValueKind.Array) arr = root;
            else if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("items", out arr) || arr.ValueKind != JsonValueKind.Array) return null;

            var list = new List<BatchPromptItem>(Math.Min(arr.GetArrayLength(), Math.Max(max, 1)));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in arr.EnumerateArray())
            {
                if (list.Count >= max) break;
                if (e.ValueKind != JsonValueKind.Object) return null;
                var prompt = OneLine(Str(e, "prompt"));
                if (prompt.Length == 0) return null;
                if (!seen.Add(prompt)) continue;
                var idx = list.Count + 1;
                var title = OneLine(Str(e, "title"));
                var cam = OneLine(Str(e, "camera"));
                var tags = new List<string>(5);
                if (e.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.Array)
                    foreach (var x in tg.EnumerateArray())
                        if (x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 } tv && tags.Count < 8) tags.Add(tv.Trim());
                list.Add(new BatchPromptItem(idx,
                    title.Length == 0 ? "시리즈 " + idx.ToString(System.Globalization.CultureInfo.InvariantCulture) : (title.Length > 30 ? title[..30] : title),
                    prompt, cam.Length == 0 ? "Static" : cam, tags));
            }
            return list.Count == 0 ? null : list;
        }
        catch (JsonException) { return null; }

        static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static string OneLine(string? s) => (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
    }

    /// <summary>대량생산 붙여넣기 포맷(<c>제목\t프롬프트</c> 한 줄씩) — BatchProductionService.ImportFromText 호환(탭 우선 분리).</summary>
    public static string ToBatchPasteText(IReadOnlyList<BatchPromptItem> items)
    {
        var sb = new StringBuilder(items.Count * 160);
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(items[i].Title).Append('\t').Append(items[i].Prompt);
        }
        return sb.ToString();
    }

    /// <summary>SNS 발행 메타(헤드라인 제목 · 짧은 본문 · 해시태그 5~8개) 생성. 키 없음/실패/스키마 불일치 → 로컬 템플릿. 절대 throw 하지 않음.</summary>
    public async Task<SnsSeoMetadata> GenerateSnsMetadataAsync(string? prompt, string? platform, float durationSec = 0, CancellationToken ct = default)
    {
        var src = (prompt ?? "").Trim();
        var plat = NormalizePlatform(platform);
        if (src.Length == 0 || !HasKey()) return BuildLocalSnsMetadata(src, plat);

        var user = new StringBuilder(src.Length + 96).Append("Platform: ").Append(plat).Append("\nVideo prompt: ").Append(src);
        if (durationSec > 0) user.Append("\nDuration: ").Append(durationSec.ToString("0", System.Globalization.CultureInfo.InvariantCulture)).Append(" seconds");
        var text = await CallAsync(SnsSeoInstruction, user.ToString(), null, true, 0.7, ct).ConfigureAwait(false);
        var parsed = text is null ? null : ParseSnsMetadata(text);
        return parsed ?? BuildLocalSnsMetadata(src, plat);
    }

    public const string SnsSeoInstruction =
        "You are a short-form video SEO copywriter. Write metadata for the given platform in Korean. " +
        "Respond with JSON only: {\"title\":string,\"description\":string,\"hashtags\":[string]} — " +
        "title is a punchy headline (max 90 chars), description is 1-2 short sentences (max 300 chars), " +
        "hashtags has 5 to 8 items targeting fintech and cinematic AI video audiences, each starting with '#', no spaces.";

    public const int MinSnsTags = 5;
    public const int MaxSnsTags = 8;
    public const int MaxSnsTitle = 100;
    public const int MaxSnsDescription = 500;
    public const string SnsFallbackNotice = "로컬 템플릿으로 작성되었습니다";
    private static readonly string[] LocalSnsTags = { "#StockSuite", "#AIAnimation", "#AIVideo", "#Cinematic", "#Fintech", "#Shorts", "#Reels", "#PromptFlow" };

    /// <summary>"youtube"/"instagram"/기타 → 표준 플랫폼 라벨(미지정은 YouTube Shorts).</summary>
    public static string NormalizePlatform(string? platform)
    {
        var p = (platform ?? "").Trim();
        if (p.Contains("insta", StringComparison.OrdinalIgnoreCase) || p.Contains("reels", StringComparison.OrdinalIgnoreCase))
            return p.Contains("you", StringComparison.OrdinalIgnoreCase) ? "YouTube Shorts + Instagram Reels" : "Instagram Reels";
        return "YouTube Shorts";
    }

    /// <summary>로컬 SNS 템플릿 — 프롬프트 첫 구절 기반 제목 + 고정 해시태그 6~8개.</summary>
    public static SnsSeoMetadata BuildLocalSnsMetadata(string? prompt, string? platform)
    {
        var src = (prompt ?? "").Trim();
        var head = src;
        var cut = head.IndexOfAny(new[] { ',', '.', '\n' });
        if (cut > 0) head = head[..cut];
        head = head.Trim();
        if (head.Length == 0) head = "AI 시네마틱 영상";
        if (head.Length > 70) head = head[..70].TrimEnd() + "…";
        var plat = NormalizePlatform(platform);
        var title = "🎬 " + head + (plat.StartsWith("Instagram", StringComparison.Ordinal) ? " #Reels" : " #Shorts");
        var desc = src.Length == 0 ? "AI로 생성한 시네마틱 숏폼 영상입니다." : "AI로 생성한 시네마틱 숏폼 영상 — " + (src.Length > 200 ? src[..200].TrimEnd() + "…" : src);
        var tags = new List<string>(MaxSnsTags);
        for (var i = 0; i < LocalSnsTags.Length && tags.Count < MaxSnsTags; i++)
        {
            var t = LocalSnsTags[i];
            if (plat == "Instagram Reels" && t == "#Shorts") continue;
            if (plat == "YouTube Shorts" && t == "#Reels") continue;
            tags.Add(t);
        }
        return new SnsSeoMetadata(title.Length <= MaxSnsTitle ? title : title[..MaxSnsTitle], desc, tags, true, SnsFallbackNotice);
    }

    /// <summary>SNS JSON 스키마 즉시 검증 — 제목 누락 · 해시태그 5개 미만이면 null. 해시태그는 '#' 보정 · 공백 제거 · 중복 제거 · 최대 8개.</summary>
    public static SnsSeoMetadata? ParseSnsMetadata(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(GeminiClient.StripFence(json));
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            var title = r.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? (t.GetString() ?? "").Trim() : "";
            if (title.Length == 0) return null;
            var desc = r.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? (d.GetString() ?? "").Trim() : "";
            if (!r.TryGetProperty("hashtags", out var h) || h.ValueKind != JsonValueKind.Array) return null;
            var tags = new List<string>(MaxSnsTags);
            foreach (var e in h.EnumerateArray())
            {
                if (tags.Count >= MaxSnsTags) break;
                if (e.ValueKind != JsonValueKind.String) continue;
                var tag = (e.GetString() ?? "").Replace(" ", "", StringComparison.Ordinal).Trim();
                if (tag.Length == 0 || tag == "#") continue;
                if (tag[0] != '#') tag = "#" + tag;
                var dup = false;
                for (var i = 0; i < tags.Count; i++) if (string.Equals(tags[i], tag, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                if (!dup) tags.Add(tag);
            }
            if (tags.Count < MinSnsTags) return null;
            if (title.Length > MaxSnsTitle) title = title[..MaxSnsTitle].TrimEnd();
            if (desc.Length > MaxSnsDescription) desc = desc[..MaxSnsDescription].TrimEnd();
            return new SnsSeoMetadata(title, desc, tags, false, "Gemini SEO 자동 완성");
        }
        catch (JsonException) { return null; }
    }

    private bool HasKey()
    {
        try { return !string.IsNullOrWhiteSpace(_apiKey()); } catch { return false; }
    }

    /// <summary>generateContent 호출(키 헤더 · 지수 백오프 · 타임아웃). 실패는 null.</summary>
    private async Task<string?> CallAsync(string system, string user, GeminiMedia? media, bool json, double temperature, CancellationToken ct)
    {
        string? key;
        try { key = _apiKey(); } catch { return null; }
        if (string.IsNullOrWhiteSpace(key)) return null;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(CallTimeout);

            var parts = new JsonArray(new JsonObject { ["text"] = user });
            if (media is not null)
                parts.Add(new JsonObject { ["inline_data"] = new JsonObject { ["mime_type"] = media.MimeType, ["data"] = Convert.ToBase64String(media.Data) } });
            var gen = new JsonObject { ["temperature"] = temperature };
            if (json) gen["responseMimeType"] = "application/json";
            var payload = new JsonObject
            {
                ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system }) },
                ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = parts }),
                ["generationConfig"] = gen,
            }.ToJsonString();

            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                if (attempt > 0) await Task.Delay(BaseBackoffMs << (attempt - 1), cts.Token).ConfigureAwait(false);
                using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                };
                req.Headers.TryAddWithoutValidation("x-goog-api-key", key);

                using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500) continue;
                if (!resp.IsSuccessStatusCode) return null;
                var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                return GeminiClient.ExtractText(body);
            }
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is not OperationCanceledException) Program.Log("GeminiPromptService.Call", ex);
            return null;
        }
    }

    // ══════════════════════ 📈 증시 시황 감성 인젝터 ══════════════════════

    /// <summary>시황 무드 프리셋 — 조명·색감·피사체 동세 토큰 블록(교체 단위).</summary>
    public static readonly MarketMoodPreset[] MarketMoods =
    {
        new(MarketMood.BullRally, "🔥", "불장/급등", "Bull Rally",
            "bull market rally mood, neon green and emerald volumetric light beams, towering bullish green candlesticks climbing up-right, rapid buy-side trade ticks flashing, energetic upward camera push"),
        new(MarketMood.BearDefense, "🩸", "하락/방어", "Bear Defense",
            "bear market defense mood, deep crimson red order-book depth, solemn blue-hour lighting, dense wall of sell orders stacked tight, slow restrained camera drift"),
        new(MarketMood.HighVolatility, "⚡", "변동성 폭발", "High Volatility",
            "high volatility surge mood, glowing Fibonacci fan lines, dynamic fast panning, lightning-fast refreshing depth chart, flickering strobe accents"),
        new(MarketMood.MidnightWallStreet, "🌙", "나스닥 심야", "Midnight WallStreet",
            "midnight Nasdaq Wall Street mood, rain-streaked window with Wall Street bokeh, calm dark-mode OLED screens, gently flowing order book, soft low-key lighting"),
    };

    public static MarketMoodPreset MoodOf(MarketMood mood)
    {
        foreach (var m in MarketMoods) if (m.Mood == mood) return m;
        return MarketMoods[0];
    }

    /// <summary>
    /// 시황 무드 주입 — 기존 무드 블록을 모두 걷어낸 뒤 선택 무드의 조명·색감·동세 토큰으로 재편(멱등 · 무드 전환 시 누적 없음).
    /// 빈 프롬프트 → 무드 토큰만 반환.
    /// </summary>
    public static string ApplyMarketSentiment(string? prompt, MarketMood mood)
    {
        var s = StripMarketSentiment(prompt);
        var tokens = MoodOf(mood).Tokens;
        return s.Length == 0 ? tokens : s + ", " + tokens;
    }

    /// <summary>적용된 무드 블록 제거(원문 복원).</summary>
    public static string StripMarketSentiment(string? prompt)
    {
        var s = (prompt ?? "").Trim();
        foreach (var m in MarketMoods)
        {
            s = s.Replace(", " + m.Tokens, "", StringComparison.OrdinalIgnoreCase)
                 .Replace(m.Tokens, "", StringComparison.OrdinalIgnoreCase);
        }
        return s.Trim().TrimEnd(',').Trim();
    }

    /// <summary>현재 프롬프트에 적용된 무드(없으면 null) — 칩 활성 표시용.</summary>
    public static MarketMood? ActiveMarketMood(string? prompt)
    {
        if (string.IsNullOrEmpty(prompt)) return null;
        foreach (var m in MarketMoods)
            if (prompt.Contains(m.Tokens, StringComparison.OrdinalIgnoreCase)) return m.Mood;
        return null;
    }

    // ══════════════════════ ✨ 제미나이 3-Way 스타일 변형 엔진 ══════════════════════

    /// <summary>3종 시네마틱 룩 — (키, 제목, 룩 토큰, 추천 카메라).</summary>
    public static readonly (string Key, string Title, string Look, string Camera)[] ThreeWayStyles =
    {
        ("realistic-dark-quant", "Realistic Dark Quant",
            "photorealistic 35mm film lens, Wall Street quant trading desk, restrained cinematic low-key lighting, muted charcoal palette with subtle teal highlights", "Slow Dolly In"),
        ("cyber-neon-depth", "Cyber Neon Depth",
            "holographic depth-layered charts, cyberpunk neon magenta and cyan accents, futuristic trading room, glowing volumetric data streams", "Orbit Pan"),
        ("minimal-modern-studio", "Minimal Modern Studio",
            "minimalist modern studio, clean single-color soft gradient backdrop, sleek uncluttered interface panels, even diffused lighting, generous negative space", "Static Locked-Off"),
    };

    public const string ThreeWayFallbackNotice = "로컬 스타일 매트릭스로 3종 전개되었습니다";

    public const string ThreeWayInstruction =
        "You are a cinematic AI video prompt director for fintech/stock-market visuals. Rewrite the user's idea into exactly 3 English video prompts, " +
        "one per style IN THIS ORDER: (1) Realistic Dark Quant - 35mm photoreal lens, Wall Street quant desk, restrained cinematic lighting; " +
        "(2) Cyber Neon Depth - holographic depth, cyberpunk neon accents, futuristic trading room; " +
        "(3) Minimal Modern Studio - minimalist single-color gradient, clean interface, soft even light. " +
        "Each prompt: one line, max 600 chars, keep the subject, describe lighting, color and motion. " +
        "Return ONLY JSON: {\"items\":[{\"title\":\"...\",\"prompt\":\"...\",\"camera\":\"...\"}]} with exactly 3 items.";

    /// <summary>✨ 1회 기획 → 3종 스타일 동시 전개. 키 없음/실패/스키마 불일치 → 로컬 매트릭스 폴백(무중단).</summary>
    public async Task<ThreeWayVariantResult> GenerateThreeWayVariantsAsync(string? idea, CancellationToken ct = default)
    {
        var t = (idea ?? "").Trim();
        if (t.Length == 0) return new(Array.Empty<StyleVariant>(), true, "아이디어(프롬프트)를 입력하세요.");
        var local = BuildLocalThreeWayVariants(t);
        if (!HasKey()) return new(local, true, ThreeWayFallbackNotice);

        var text = await CallAsync(ThreeWayInstruction, "Idea: " + t, null, true, 0.85, ct).ConfigureAwait(false);
        var parsed = text is null ? null : ParseBatchPrompts(text, ThreeWayStyles.Length);
        if (parsed is null || parsed.Count < ThreeWayStyles.Length) return new(local, true, ThreeWayFallbackNotice);

        var list = new StyleVariant[ThreeWayStyles.Length];
        for (var i = 0; i < list.Length; i++)
        {
            var p = parsed[i].Prompt;
            if (p.Length > PromptSynthesizerService.MaxCharacterLimit) p = p[..PromptSynthesizerService.MaxCharacterLimit].TrimEnd();
            var cam = string.Equals(parsed[i].Camera, "Static", StringComparison.Ordinal) ? local[i].Camera : parsed[i].Camera;
            list[i] = local[i] with { Prompt = p, Camera = cam };
        }
        return new(list, false, "✨ Gemini 3종 스타일 전개 완료");
    }

    /// <summary>로컬 3종 변형 — 동일 아이디어 + 스타일별 룩 토큰·카메라·결정적 시드(아이디어×스타일 FNV-1a).</summary>
    public static IReadOnlyList<StyleVariant> BuildLocalThreeWayVariants(string? idea)
    {
        var t = (idea ?? "").Trim().TrimEnd(',').Trim();
        if (t.Length == 0) return Array.Empty<StyleVariant>();
        var list = new StyleVariant[ThreeWayStyles.Length];
        for (var i = 0; i < list.Length; i++)
        {
            var st = ThreeWayStyles[i];
            var p = t + ", " + st.Look + ", " + st.Camera.ToLowerInvariant() + " camera motion, smooth motion, professional color grading";
            if (p.Length > PromptSynthesizerService.MaxCharacterLimit) p = p[..PromptSynthesizerService.MaxCharacterLimit].TrimEnd();
            list[i] = new StyleVariant(i + 1, st.Key, st.Title, p, st.Camera, VariantSeed(t, st.Key));
        }
        return list;
    }

    /// <summary>결정적 시드 — FNV-1a 32bit(아이디어 + 스타일 키) → 1..int.MaxValue.</summary>
    public static long VariantSeed(string idea, string styleKey)
    {
        var h = 2166136261u;
        foreach (var c in idea) { h ^= c; h *= 16777619u; }
        h ^= '|'; h *= 16777619u;
        foreach (var c in styleKey) { h ^= c; h *= 16777619u; }
        var v = h & 0x7FFFFFFFu;
        return v == 0 ? 1 : v;
    }

    // ══════════════════════ 📊 관심종목(Watchlist) 시네마틱 디렉터 ══════════════════════

    public const string TickerFallbackNotice = "로컬 종목 메타포 사전으로 기획되었습니다";

    public const string TickerInstruction =
        "You are a cinematic AI video prompt director for Wan 2.1 fintech ambient backgrounds. Given a listed company (ticker, name, sector, core business metaphor), " +
        "write ONE English video prompt (one line, max 600 chars) that visualizes the company's core business as a cinematic metaphor, " +
        "and engrave the exact ticker code as a glowing HUD texture (e.g. holographic ticker overlay). No brand logos, no readable text other than the ticker. " +
        "Describe lighting, color, camera motion; seamless loopable ambient. Output ONLY the prompt text.";

    /// <summary>티커/섹터 → 핵심 사업 영문 메타포(미등록은 섹터 키워드 · 최후 범용 금융 메타포).</summary>
    public static string BusinessMetaphorOf(string? ticker, string? sector)
    {
        switch ((ticker ?? "").Trim().ToUpperInvariant())
        {
            case "005930": return "HBM memory stacks and AI foundry wafers glowing on a cleanroom fab line";
            case "000660": return "stacked HBM DRAM dies with through-silicon vias pulsing with data";
            case "373220": return "EV battery cells charging with flowing lithium energy currents";
            case "207940": return "bioreactor tanks and floating antibody molecules in a sterile biopharma plant";
            case "005380": return "electric and hydrogen vehicles gliding through a smart factory assembly line";
            case "NVDA": return "GPU accelerator dies and tensor cores firing in a vast AI datacenter";
            case "AAPL": return "sleek aluminum devices and custom silicon chips floating in a minimal design lab";
            case "MSFT": return "cloud datacenter racks and AI copilot neural streams across a global grid";
            case "GOOGL": return "search index constellations and TPU pods forming a planetary knowledge graph";
            case "AMZN": return "robotic fulfillment centers and cloud server aisles flowing with parcels and data";
            case "META": return "social graph nodes and VR headsets projecting an immersive metaverse";
            case "TSLA": return "autonomous electric cars and gigafactory robots under bright production lights";
        }
        var sec = (sector ?? "").Trim();
        if (sec.Contains("반도체", StringComparison.Ordinal) || sec.Contains("semi", StringComparison.OrdinalIgnoreCase)) return "silicon wafers and chip dies glowing in a semiconductor fab";
        if (sec.Contains("배터리", StringComparison.Ordinal) || sec.Contains("2차전지", StringComparison.Ordinal)) return "battery cells charging with flowing energy currents";
        if (sec.Contains("바이오", StringComparison.Ordinal) || sec.Contains("bio", StringComparison.OrdinalIgnoreCase)) return "floating DNA helices and molecules in a biotech lab";
        if (sec.Contains("자동차", StringComparison.Ordinal) || sec.Contains("auto", StringComparison.OrdinalIgnoreCase)) return "vehicles gliding through a futuristic smart factory";
        if (sec.Contains("금융", StringComparison.Ordinal) || sec.Contains("bank", StringComparison.OrdinalIgnoreCase)) return "golden vault doors and flowing transaction ledgers";
        return "abstract holographic candlestick charts and flowing market data streams";
    }

    /// <summary>로컬 티커 프롬프트 — 핵심 사업 메타포 + 티커 HUD 텍스처 + 시네마틱 룩(결정적).</summary>
    public static string BuildLocalTickerPrompt(string ticker, string? name, string? sector)
    {
        var t = (ticker ?? "").Trim().ToUpperInvariant();
        var p = "Cinematic ambient fintech background: " + BusinessMetaphorOf(t, sector)
            + ", the ticker code \"" + t + "\" engraved as a glowing holographic HUD texture with live price ticks, "
            + "dark trading-room atmosphere, teal and amber volumetric light, slow dolly in, shallow depth of field, seamless loop, 4k, professional color grading";
        return p.Length <= PromptSynthesizerService.MaxCharacterLimit ? p : p[..PromptSynthesizerService.MaxCharacterLimit].TrimEnd();
    }

    /// <summary>📊 종목 칩 → Wan 2.1 시네마틱 프롬프트. 키 없음/실패/티커 누락 응답 → 로컬 메타포 폴백(무중단).</summary>
    public async Task<GeminiEnhanceOutcome> GenerateTickerPromptAsync(string? ticker, string? name, string? sector, CancellationToken ct = default)
    {
        var t = (ticker ?? "").Trim().ToUpperInvariant();
        if (t.Length == 0) return new("", true, "종목 티커가 비었습니다.");
        var local = BuildLocalTickerPrompt(t, name, sector);
        if (!HasKey()) return new(local, true, TickerFallbackNotice);
        var user = "Ticker: " + t + "\nCompany: " + (name ?? "") + "\nSector: " + (sector ?? "") + "\nCore business metaphor: " + BusinessMetaphorOf(t, sector);
        var text = Sanitize(await CallAsync(TickerInstruction, user, null, false, 0.8, ct).ConfigureAwait(false));
        if (text is null || !text.Contains(t, StringComparison.OrdinalIgnoreCase)) return new(local, true, TickerFallbackNotice);
        return new(text, false, "📊 " + t + " " + (name ?? "") + " 시네마틱 기획 완료");
    }

    private static string? Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = GeminiClient.StripFence(text).Trim().Trim('"', '\'', '`').Trim();
        if (s.Length == 0) return null;
        return s.Length <= PromptSynthesizerService.MaxCharacterLimit ? s : s[..PromptSynthesizerService.MaxCharacterLimit].TrimEnd();
    }

    private static string MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        _ => "image/jpeg",
    };
}
