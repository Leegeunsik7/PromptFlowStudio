using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🎯 정규화 초점(0~1 · 좌상단 원점).</summary>
public readonly record struct FocalPoint(double X, double Y);

/// <summary>📐 정규화 크롭 사각형(0~1) — X+Width ≤ 1 · Y+Height ≤ 1 보장.</summary>
public readonly record struct NormalizedCropRect(double X, double Y, double Width, double Height);

/// <summary>📐 픽셀 크롭(FFmpeg <c>crop=w:h:x:y</c>) — 원본 경계 내부 · 짝수 폭/높이.</summary>
public readonly record struct PixelCropRect(int X, int Y, int Width, int Height);

/// <summary>📱 SNS 게시 키트 — 훅 제목 3종 · KO/EN 캡션 · 해시태그 12개 · 추천 게시 시간대.</summary>
public sealed record PlutoSnsPublishingKit(IReadOnlyList<string> Titles, string CaptionKo, string CaptionEn, IReadOnlyList<string> Hashtags, string RecommendedPostingWindow)
{
    /// <summary>LLM(Gemini/PlutoCore/로컬 게이트웨이) 생성 여부(false = 큐레이션 템플릿).</summary>
    public bool UsedLlm { get; init; }

    /// <summary>📋 복사용 전체 본문(첫 제목 + KO/EN 캡션 + 해시태그).</summary>
    public string FullText
    {
        get
        {
            var sb = new StringBuilder(512);
            if (Titles.Count > 0) sb.Append(Titles[0]).Append('\n').Append('\n');
            sb.Append(CaptionKo);
            if (CaptionEn.Length > 0) sb.Append('\n').Append('\n').Append(CaptionEn);
            sb.Append('\n').Append('\n').Append(string.Join(' ', Hashtags));
            return sb.ToString();
        }
    }
}

/// <summary>📱 9:16 릴스/쇼츠 패키저 — 피사체 중심 세로 크롭 + SNS 게시 키트.</summary>
public interface IPlutoMultiFormatPackager
{
    /// <summary>초점 중심 9:16 정규화 크롭(원본 경계 내부 클램프).</summary>
    NormalizedCropRect ComputeVerticalSmartCropRect(int sourceWidth, int sourceHeight, FocalPoint focalPoint, double targetAspect = 9.0 / 16.0);

    /// <summary>게시 키트(Gemini → PlutoCore → 로컬 게이트웨이 → 큐레이션 템플릿 · 오답 노트 차단 태그 제외 · 취소만 전파).</summary>
    Task<PlutoSnsPublishingKit> GeneratePublishingKitAsync(string themeHint, IReadOnlyList<string> activeTags, IReadOnlyList<SubtitleCue>? subtitles = null, CancellationToken ct = default);
}

/// <summary>📱 <see cref="IPlutoMultiFormatPackager"/> 구현(순수 BCL · 결정론 수학 + 4단 페일오버 카피).</summary>
public sealed class PlutoMultiFormatPackager : IPlutoMultiFormatPackager
{
    public const int OutputWidth = 1080, OutputHeight = 1920;
    public const int TitleCount = 3, HashtagCount = 12;
    /// <summary>이미지 헤더 판독 상한(EXIF 썸네일 포함 JPEG SOF 탐색).</summary>
    public const int HeaderProbeBytes = 256 * 1024;

    public const string LlmInstruction =
        "You are Pluto, a viral short-form packaging strategist for YouTube Shorts and Instagram Reels. " +
        "Respond with JSON only: {\"titles\":[string,string,string],\"captionKo\":string,\"captionEn\":string,\"hashtags\":[string],\"postingWindow\":string} - " +
        "titles: exactly 3 scroll-stopping Korean hook titles with emoji (max 60 chars each, different hook angles: curiosity, emotion, payoff); " +
        "captionKo: 2-3 warm Korean lines with emojis; captionEn: natural English translation (max 220 chars); " +
        "hashtags: 12 trend-aware hashtags mixing Korean and English, each starting with '#', no spaces; " +
        "postingWindow: best posting time window in KST with a short reason.";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] GenericTags =
    {
        "#Shorts", "#Reels", "#쇼츠", "#릴스", "#감성영상", "#일상기록", "#viral", "#fyp", "#explore", "#추천", "#소통", "#reelsinstagram",
        "#instadaily", "#shortsvideo", "#trending", "#daily",
    };

    private readonly IPlutoLlmClient? _llm;
    private readonly IPlutoMistakeNotebookService? _notebook;
    private readonly Func<DateTime> _utcNow;
    private readonly IPlutoAdaptivePatternRanker? _ranker;

    public PlutoMultiFormatPackager(IPlutoLlmClient? llm, IPlutoMistakeNotebookService? notebook, Func<DateTime>? utcNow = null, IPlutoAdaptivePatternRanker? ranker = null)
    {
        _llm = llm;
        _notebook = notebook;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _ranker = ranker;
    }

    public const string ProvenHookPrefix = "Proven high-retention hook angle (from past breakout): ";

    /// <summary>
    /// 🧠 학습 랭킹 반영 — 강화된 학습 태그(활성 태그 밖 포함)를 합류시켜 (학습 가중치 − 오답 감점) 순 재정렬 + 강화된 1위 훅.
    /// 랭커 없음/학습 신호 없음/실패 → 입력 그대로([부분 구제]).
    /// </summary>
    public static (IReadOnlyList<string> Tags, string? LearnedHook) ApplyAdaptiveRanking(IPlutoAdaptivePatternRanker? ranker, IReadOnlyList<string>? activeTags)
    {
        IReadOnlyList<string> input = activeTags ?? Array.Empty<string>();
        if (ranker is null) return (input, null);
        try
        {
            var rec = ranker.GetCurrentRecommendations();
            if (!rec.HasReinforcedHistory) return (input, null);
            var merged = new List<string>(input.Count + rec.TopTags.Count);
            for (var i = 0; i < rec.TopTags.Count; i++) if (rec.TopTags[i].IsReinforced) merged.Add(rec.TopTags[i].DisplayLabel);
            for (var i = 0; i < input.Count; i++) merged.Add(input[i]);
            var ranked = ranker.RankTags(merged, HashtagCount);
            var hook = rec.TopHook is { IsReinforced: true, IsPenalized: false } th ? th.DisplayLabel : null;
            return (ranked, hook);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Packager] ranker: " + ex.Message); return (input, null); }
    }

    public NormalizedCropRect ComputeVerticalSmartCropRect(int sourceWidth, int sourceHeight, FocalPoint focalPoint, double targetAspect = 9.0 / 16.0)
        => ComputeCrop(sourceWidth, sourceHeight, focalPoint, targetAspect);

    /// <summary>
    /// 📐 W = min(1, a·h/w) · H = min(1, w/(a·h)) — 원본이 목표보다 넓으면 폭만, 좁으면 높이만 축소(최대 면적 유지).
    /// X = clamp(fx − W/2, 0, 1 − W) · Y = clamp(fy − H/2, 0, 1 − H) → 경계 밖 픽셀 0. 비유한 초점 → 중앙.
    /// </summary>
    public static NormalizedCropRect ComputeCrop(int sourceWidth, int sourceHeight, FocalPoint focalPoint, double targetAspect = 9.0 / 16.0)
    {
        if (sourceWidth <= 0) throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        if (sourceHeight <= 0) throw new ArgumentOutOfRangeException(nameof(sourceHeight));
        if (!double.IsFinite(targetAspect) || targetAspect <= 0) throw new ArgumentOutOfRangeException(nameof(targetAspect));
        var src = (double)sourceWidth / sourceHeight;
        double w = 1.0, h = 1.0;
        if (src > targetAspect) w = Math.Clamp(targetAspect / src, 0.0, 1.0);
        else if (src < targetAspect) h = Math.Clamp(src / targetAspect, 0.0, 1.0);
        var fx = double.IsFinite(focalPoint.X) ? Math.Clamp(focalPoint.X, 0.0, 1.0) : 0.5;
        var fy = double.IsFinite(focalPoint.Y) ? Math.Clamp(focalPoint.Y, 0.0, 1.0) : 0.5;
        var x = Math.Clamp(fx - w / 2, 0.0, Math.Max(0.0, 1.0 - w));
        var y = Math.Clamp(fy - h / 2, 0.0, Math.Max(0.0, 1.0 - h));
        return new NormalizedCropRect(x, y, w, h);
    }

    /// <summary>📐 정규화 → 픽셀(짝수 폭/높이 · 원본 내부 클램프).</summary>
    public static PixelCropRect ToPixelRect(NormalizedCropRect r, int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        var cw = EvenPx(r.Width, sourceWidth);
        var ch = EvenPx(r.Height, sourceHeight);
        return new PixelCropRect(Place(r.X, r.Width, sourceWidth, cw), Place(r.Y, r.Height, sourceHeight, ch), cw, ch);
    }

    /// <summary>정규화 길이 → 짝수 픽셀(1px 원본은 1 · 상한 원본 크기).</summary>
    private static int EvenPx(double size, int full)
    {
        var px = (int)Math.Floor((double.IsFinite(size) ? Math.Clamp(size, 0.0, 1.0) : 1.0) * full);
        px = Math.Min(full, Math.Max(2, px));
        return px > 1 ? px & ~1 : px;
    }

    /// <summary>짝수 정렬로 줄어든 크롭을 정규화 사각형 중심에 재배치 — 경계에 붙은 사각형은 경계 유지.</summary>
    private static int Place(double start, double size, int full, int px)
    {
        const double eps = 1e-9;
        if (start <= eps) return 0;
        if (start + size >= 1.0 - eps) return full - px;
        var c = (start + size / 2) * full - px / 2.0;
        return Math.Clamp((int)Math.Round(c, MidpointRounding.AwayFromZero), 0, full - px);
    }

    /// <summary>🎯 원본 초점 → 크롭 좌표계 초점(0~1 클램프).</summary>
    public static (double X, double Y) RemapFocal((double X, double Y) focal, NormalizedCropRect r)
    {
        var fx = double.IsFinite(focal.X) ? focal.X : 0.5;
        var fy = double.IsFinite(focal.Y) ? focal.Y : 0.5;
        return (r.Width > 0 ? Math.Clamp((fx - r.X) / r.Width, 0.0, 1.0) : 0.5, r.Height > 0 ? Math.Clamp((fy - r.Y) / r.Height, 0.0, 1.0) : 0.5);
    }

    /// <summary>🎞 몽타주 <c>filter_complex</c> 의 각 입력 <c>[i:v]</c> 직후에 <c>crop=w:h:x:y,</c> 삽입(null 항목 → 원본 유지).</summary>
    public static string InjectSourceCrops(string filterGraph, IReadOnlyList<PixelCropRect?> crops)
    {
        ArgumentNullException.ThrowIfNull(filterGraph);
        if (crops is null || crops.Count == 0) return filterGraph;
        var g = filterGraph;
        for (var i = 0; i < crops.Count; i++)
        {
            if (crops[i] is not { } c) continue;
            var label = "[" + i.ToString(Inv) + ":v]";
            var at = g.IndexOf(label, StringComparison.Ordinal);
            if (at < 0) continue;
            var ins = "crop=" + c.Width.ToString(Inv) + ":" + c.Height.ToString(Inv) + ":" + c.X.ToString(Inv) + ":" + c.Y.ToString(Inv) + ",";
            g = g.Insert(at + label.Length, ins);
        }
        return g;
    }

    /// <summary>🖼 파일 헤더(최대 <see cref="HeaderProbeBytes"/>)로 픽셀 크기 판독 — 실패 → false(예외 없음).</summary>
    public static bool TryReadImageFileSize(string path, out int width, out int height)
    {
        width = 0; height = 0;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096);
            var len = (int)Math.Min(fs.Length, HeaderProbeBytes);
            if (len <= 0) return false;
            var buf = new byte[len];
            var read = 0;
            while (read < len)
            {
                var n = fs.Read(buf, read, len - read);
                if (n <= 0) break;
                read += n;
            }
            return TryReadImageSize(buf.AsSpan(0, read), out width, out height);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>PNG/JPEG/WebP/BMP 헤더에서 픽셀 크기를 판독(디코딩 없이 · 순수 BCL).</summary>
    public static bool TryReadImageSize(ReadOnlySpan<byte> d, out int width, out int height)
    {
        width = 0; height = 0;
        // PNG: 8B 시그니처 + IHDR(폭/높이 BE)
        if (d.Length >= 24 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47)
        {
            width = BinaryPrimitives.ReadInt32BigEndian(d.Slice(16, 4));
            height = BinaryPrimitives.ReadInt32BigEndian(d.Slice(20, 4));
            return width > 0 && height > 0;
        }
        // BMP: BITMAPINFOHEADER 폭/높이 LE(높이 음수 = top-down)
        if (d.Length >= 26 && d[0] == (byte)'B' && d[1] == (byte)'M')
        {
            width = BinaryPrimitives.ReadInt32LittleEndian(d.Slice(18, 4));
            height = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(d.Slice(22, 4)));
            return width > 0 && height > 0;
        }
        // WebP: RIFF....WEBP + VP8 / VP8L / VP8X
        if (d.Length >= 30 && d[0] == (byte)'R' && d[1] == (byte)'I' && d[8] == (byte)'W' && d[9] == (byte)'E')
        {
            var fourcc = d.Slice(12, 4);
            if (fourcc.SequenceEqual("VP8 "u8))
            {
                width = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(26, 2)) & 0x3FFF;
                height = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(28, 2)) & 0x3FFF;
            }
            else if (fourcc.SequenceEqual("VP8L"u8))
            {
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(21, 4));
                width = (int)(bits & 0x3FFF) + 1;
                height = (int)((bits >> 14) & 0x3FFF) + 1;
            }
            else if (fourcc.SequenceEqual("VP8X"u8))
            {
                width = (d[24] | d[25] << 8 | d[26] << 16) + 1;
                height = (d[27] | d[28] << 8 | d[29] << 16) + 1;
            }
            return width > 0 && height > 0;
        }
        // JPEG: SOFn 마커 스캔(DHT/JPG/DAC 제외)
        if (d.Length >= 4 && d[0] == 0xFF && d[1] == 0xD8)
        {
            var i = 2;
            while (i + 9 < d.Length)
            {
                if (d[i] != 0xFF) { i++; continue; }
                var marker = d[i + 1];
                if (marker == 0xFF) { i++; continue; }
                if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
                var len = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(i + 2, 2));
                if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                {
                    height = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(i + 5, 2));
                    width = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(i + 7, 2));
                    return width > 0 && height > 0;
                }
                if (len < 2) return false;
                i += 2 + len;
            }
        }
        return false;
    }

    // ── 📱 SNS 게시 키트 ──

    public async Task<PlutoSnsPublishingKit> GeneratePublishingKitAsync(string themeHint, IReadOnlyList<string> activeTags, IReadOnlyList<SubtitleCue>? subtitles = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlySet<string>? suppressed = null;
        var negative = "";
        try
        {
            if (_notebook is not null)
            {
                await _notebook.EnsureLoadedAsync(ct).ConfigureAwait(false);
                suppressed = _notebook.GetSuppressedTokens();
                negative = _notebook.BuildNegativeConstraintPrompt(5);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Packager] notebook: " + ex.Message); }   // [부분 구제] 차단 없음

        if (_ranker is not null)
        {
            try { await _ranker.EnsureLoadedAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Packager] ranker load: " + ex.Message); }
        }
        var (rankedTags, learnedHook) = ApplyAdaptiveRanking(_ranker, activeTags);
        activeTags = rankedTags;

        var local = BuildTemplateKit(themeHint, activeTags, subtitles, suppressed, _utcNow(), learnedHook);
        var user = new StringBuilder(512)
            .Append("Theme: ").Append(string.IsNullOrWhiteSpace(themeHint) ? "baby & family moments" : themeHint.Trim()).Append('\n')
            .Append("Category: ").Append(PlutoSocialCopywriter.InferCategory(themeHint).ToString()).Append('\n')
            .Append("Active trend tags: ").Append(activeTags is { Count: > 0 } ? string.Join(", ", activeTags) : "(none)").Append('\n');
        if (subtitles is { Count: > 0 })
        {
            user.Append("Story beats (3-act subtitles): ");
            for (var i = 0; i < subtitles.Count && i < 5; i++)
            {
                if (i > 0) user.Append(" / ");
                user.Append(subtitles[i].Text);
            }
            user.Append('\n');
        }
        if (learnedHook is not null) user.Append(ProvenHookPrefix).Append(learnedHook.Replace('\n', ' ').Trim()).Append(" - lead the first title with this framing\n");
        if (negative.Length > 0) user.Append(negative).Append('\n');

        string? text;
        try { text = await PlutoLlmBridge.TryCompleteAsync(_llm, LlmInstruction, user.ToString(), 0.85, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Packager] llm: " + ex.Message); text = null; }
        return (text is null ? null : ParseLlmKit(text, local, suppressed)) ?? local;
    }

    /// <summary>LLM JSON 즉시 스키마 검증 — 제목 ≥ 1 · 한글 캡션 필수 · 해시태그 ≥ 5(미달 → null). 부족분은 템플릿으로 보충([부분 구제]).</summary>
    public static PlutoSnsPublishingKit? ParseLlmKit(string json, PlutoSnsPublishingKit local, IReadOnlySet<string>? suppressed)
    {
        using var doc = PlutoLlmBridge.TryParse(json);
        if (doc is null) return null;
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object) return null;
        var captionKo = PlutoLlmBridge.Str(r, "captionKo", 400);
        if (captionKo.Length == 0) return null;
        if (!r.TryGetProperty("titles", out var ts) || ts.ValueKind != JsonValueKind.Array) return null;
        var titles = new List<string>(TitleCount);
        var titleSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in ts.EnumerateArray())
        {
            if (titles.Count >= TitleCount) break;
            if (e.ValueKind != JsonValueKind.String) continue;
            var t = (e.GetString() ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (t.Length > 80) t = t[..80].TrimEnd();
            if (t.Length > 0 && titleSeen.Add(t)) titles.Add(t);
        }
        if (titles.Count == 0) return null;
        for (var i = 0; i < local.Titles.Count && titles.Count < TitleCount; i++)
            if (titleSeen.Add(local.Titles[i])) titles.Add(local.Titles[i]);

        if (!r.TryGetProperty("hashtags", out var h) || h.ValueKind != JsonValueKind.Array) return null;
        var llmTags = new List<string>(HashtagCount);
        var llmSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in h.EnumerateArray())
            if (e.ValueKind == JsonValueKind.String && !IsSuppressed(e.GetString(), suppressed)) PlutoLlmBridge.AddTag(llmTags, llmSeen, e.GetString(), HashtagCount);
        if (llmTags.Count < 5) return null;
        foreach (var t in local.Hashtags) PlutoLlmBridge.AddTag(llmTags, llmSeen, t, HashtagCount);

        var en = PlutoLlmBridge.Str(r, "captionEn", 300);
        var window = PlutoLlmBridge.Str(r, "postingWindow", 120);
        return new PlutoSnsPublishingKit(titles, captionKo, en.Length > 0 ? en : local.CaptionEn, llmTags, window.Length > 0 ? window : local.RecommendedPostingWindow) { UsedLlm = true };
    }

    /// <summary>🧩 큐레이션 템플릿 키트(결정론) — 제목 3 · KO/EN 캡션 · 해시태그 12(활성 태그 → 테마 풀 → 범용 풀 · 차단 태그 제외).</summary>
    public static PlutoSnsPublishingKit BuildTemplateKit(string? themeHint, IReadOnlyList<string>? activeTags, IReadOnlyList<SubtitleCue>? subtitles, IReadOnlySet<string>? suppressed, DateTime utcNow,
        string? learnedHook = null)
    {
        var cat = PlutoSocialCopywriter.InferCategory(themeHint);
        var hook = subtitles is { Count: > 0 } && !string.IsNullOrWhiteSpace(subtitles[0].Text) ? subtitles[0].Text.Trim() : null;
        if (hook is { Length: > 40 }) hook = hook[..40].TrimEnd();
        var (t1, t2, t3, ko, en) = cat switch
        {
            StudioThemeCategory.PetsCompanions => ("🐾 3초 만에 심쿵하는 우리 집 귀염둥이", "😳 끝까지 보면 웃음 터지는 반전 🐶", "💛 이 표정 하나로 하루 피로 끝",
                "🐾 오늘도 우리 집 귀염둥이가 다 했어요\n눈만 마주쳐도 행복해지는 순간 🥰", "🐾 Our little furball stole the show again — pure joy in every glance 🥰"),
            StudioThemeCategory.HealingNature => ("🌿 15초만 보고 숨 고르세요", "🎧 소리 켜고 보면 마음이 녹아요", "✨ 마지막 장면에서 멈추게 되는 풍경",
                "🌿 잠깐 멈추고 숨 한번 크게 쉬어가요\n마음까지 맑아지는 15초 🍃", "🌿 Pause for 15 seconds and breathe — a little calm for your day 🍃"),
            StudioThemeCategory.DailyVlog => ("☕ 평범한 하루가 영화가 되는 순간", "🎬 이렇게 찍으면 일상도 감성 폭발", "✨ 끝까지 보면 따라 하고 싶어지는 루틴",
                "☕ 소소하지만 확실한 오늘의 행복\n여러분의 하루는 어땠나요? 💬", "☕ Small but certain happiness — how was your day? 💬"),
            _ => ("🍼 3초 만에 녹아내리는 우리 아기 표정", "🥹 마지막 컷에서 눈물 버튼 눌려요", "👶 매일 자라는 기적, 15초에 담았어요",
                "🍼 하루하루 자라는 게 보일 때마다 마음이 몽글몽글해져요 🥹\n여러분 아기의 첫 순간도 댓글로 들려주세요 💛", "🍼 Watching our little one grow every day melts our hearts 🥹 Share your baby's first moments below 💛"),
        };
        var titles = new List<string>(TitleCount);
        if (!string.IsNullOrWhiteSpace(learnedHook) && !IsSuppressed(learnedHook, suppressed))   // 🧠 돌파 검증 훅 프레이밍 우선
        {
            var lh = learnedHook.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (lh.Length > 40) lh = lh[..40].TrimEnd();
            titles.Add("🔥 " + lh);
        }
        if (hook is not null && titles.Count < TitleCount && !(titles.Count > 0 && titles[0].EndsWith(hook, StringComparison.Ordinal))) titles.Add("✨ " + hook);
        foreach (var t in new[] { t1, t2, t3 }) if (titles.Count < TitleCount && !titles.Contains(t)) titles.Add(t);

        var tags = new List<string>(HashtagCount);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (activeTags is not null)
            foreach (var t in activeTags) if (!IsSuppressed(t, suppressed)) PlutoLlmBridge.AddTag(tags, seen, t, HashtagCount);
        var theme = PlutoSocialCopywriter.Compose(null, themeHint, SocialPlatform.Both).Hashtags;
        foreach (var t in theme) if (!IsSuppressed(t, suppressed)) PlutoLlmBridge.AddTag(tags, seen, t, HashtagCount);
        foreach (var t in GenericTags) if (!IsSuppressed(t, suppressed)) PlutoLlmBridge.AddTag(tags, seen, t, HashtagCount);

        return new PlutoSnsPublishingKit(titles, ko, en, tags, PostingWindowFor(cat, utcNow));
    }

    // ══════════════════════ 📋 Pluto 4.6 — 플랫폼 정규화 원클릭 복사 포매터(결정론) ══════════════════════
    public const int ShortsTitleMaxChars = 95;
    public const int ShortsMaxHashtags = 5;
    public const int ReelsMaxHashtags = 12;
    public const string Ellipsis = "…";

    /// <summary>
    /// ▶️ YouTube Shorts — 첫 제목(≤ 95자 · 단어 경계 · 서로게이트 비분할) + KO/EN 캡션 + 정규화 해시태그 ≤ 5개(중복 제거).
    /// </summary>
    public static string FormatForYouTubeShorts(PlutoSnsPublishingKit kit)
    {
        ArgumentNullException.ThrowIfNull(kit);
        var sb = new StringBuilder(512);
        var title = kit.Titles is { Count: > 0 } ? ClampTitle(kit.Titles[0], ShortsTitleMaxChars) : "";
        if (title.Length > 0) sb.Append(title).Append('\n').Append('\n');
        AppendCaptions(sb, kit);
        var tags = NormalizeHashtags(kit.Hashtags, ShortsMaxHashtags);
        if (tags.Count > 0) sb.Append('\n').Append('\n').AppendJoin(' ', tags);
        return sb.ToString().Trim();
    }

    /// <summary>📸 Instagram Reels — KO/EN 캡션 + 중복 제거 해시태그 ≤ 12개 + 추천 게시 시간대.</summary>
    public static string FormatForInstagramReels(PlutoSnsPublishingKit kit)
    {
        ArgumentNullException.ThrowIfNull(kit);
        var sb = new StringBuilder(640);
        AppendCaptions(sb, kit);
        var tags = NormalizeHashtags(kit.Hashtags, ReelsMaxHashtags);
        if (tags.Count > 0) sb.Append('\n').Append('\n').AppendJoin(' ', tags);
        if (!string.IsNullOrWhiteSpace(kit.RecommendedPostingWindow))
            sb.Append('\n').Append('\n').Append("🕗 추천 게시 시간: ").Append(kit.RecommendedPostingWindow.Trim());
        return sb.ToString().Trim();
    }

    private static void AppendCaptions(StringBuilder sb, PlutoSnsPublishingKit kit)
    {
        var ko = kit.CaptionKo?.Trim() ?? "";
        var en = kit.CaptionEn?.Trim() ?? "";
        sb.Append(ko);
        if (en.Length > 0) { if (ko.Length > 0) sb.Append('\n').Append('\n'); sb.Append(en); }
    }

    /// <summary>✂️ 제목 클램프 — 초과 시 단어 경계(뒤 40% 내 공백) 또는 서로게이트 안전 지점에서 자르고 <c>…</c> 부착(총 ≤ <paramref name="maxChars"/>).</summary>
    public static string ClampTitle(string? title, int maxChars = ShortsTitleMaxChars)
    {
        var t = string.IsNullOrWhiteSpace(title) ? "" : title.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (t.Length <= maxChars || maxChars <= Ellipsis.Length) return t.Length <= maxChars ? t : t[..maxChars];
        var cut = maxChars - Ellipsis.Length;
        if (char.IsHighSurrogate(t[cut - 1])) cut--;   // 서로게이트 쌍 비분할
        var space = t.LastIndexOf(' ', cut);
        if (space >= cut * 6 / 10) cut = space;
        return t[..cut].TrimEnd() + Ellipsis;
    }

    /// <summary>#️⃣ 해시태그 정규화 — 공백/구두점 제거(문자·숫자·_ 유지) · <c>#</c> 1개 접두 · 대소문자 무시 중복 제거 · 최대 <paramref name="max"/>개.</summary>
    public static IReadOnlyList<string> NormalizeHashtags(IReadOnlyList<string>? raw, int max)
    {
        var result = new List<string>(Math.Max(0, max));
        if (raw is null || max <= 0) return result;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder(32);
        for (var i = 0; i < raw.Count && result.Count < max; i++)
        {
            var s = raw[i];
            if (string.IsNullOrWhiteSpace(s)) continue;
            sb.Clear();
            for (var j = 0; j < s.Length; j++)
            {
                var c = s[j];
                if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
                else if (char.IsHighSurrogate(c) && j + 1 < s.Length && char.IsLowSurrogate(s[j + 1]) && char.IsLetterOrDigit(s, j)) { sb.Append(c).Append(s[j + 1]); j++; }
            }
            if (sb.Length == 0 || sb.Length > 40) continue;
            var tag = "#" + sb;
            if (seen.Add(tag)) result.Add(tag);
        }
        return result;
    }

    /// <summary>🕗 테마별 추천 게시 시간대(KST) — 주말은 오전 창 추가.</summary>
    public static string PostingWindowFor(StudioThemeCategory cat, DateTime utcNow)
    {
        var kst = utcNow.AddHours(9);
        var weekend = kst.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        var core = cat switch
        {
            StudioThemeCategory.PetsCompanions => "19:00–21:00 KST (퇴근 후 힐링 스크롤)",
            StudioThemeCategory.HealingNature => "22:00–24:00 KST (잠들기 전 휴식)",
            StudioThemeCategory.DailyVlog => "12:00–13:00 · 18:00–20:00 KST (점심 · 퇴근길)",
            _ => "20:30–22:30 KST (아이 재운 뒤 육아 골든타임)",
        };
        return weekend ? "주말 09:00–11:00 KST 또는 " + core : core;
    }

    private static bool IsSuppressed(string? tag, IReadOnlySet<string>? suppressed)
    {
        if (suppressed is null || suppressed.Count == 0 || string.IsNullOrWhiteSpace(tag)) return false;
        var bare = tag.Trim().TrimStart('#');
        foreach (var s in suppressed)
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            if (string.Equals(s.Trim().TrimStart('#'), bare, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
