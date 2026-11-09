using System;
using System.Collections.Generic;

namespace PromptFlowStudio.Services.Publishing;

/// <summary>📱 SNS 패스트트랙 프리셋 종류.</summary>
public enum SocialPresetKind
{
    YouTubeShorts = 0,
    InstagramReels = 1,
}

/// <summary>
/// 📱 YouTube Shorts · Instagram Reels 원클릭 내보내기 사양 — 9:16 세로(1080×1920) · fps · 라우드니스 · 필름 그레인 · 기본 해시태그.
/// </summary>
public sealed record SocialExportPreset(
    SocialPresetKind Kind,
    string Label,
    int Width,
    int Height,
    int Fps,
    double? LoudnessLufs,
    bool FilmGrain,
    IReadOnlyList<string> Hashtags)
{
    public static readonly SocialExportPreset YouTubeShorts = new(SocialPresetKind.YouTubeShorts, "🔴 YouTube Shorts",
        1080, 1920, 60, -14.0, false, new[] { "#Shorts", "#YouTubeShorts" });

    public static readonly SocialExportPreset InstagramReels = new(SocialPresetKind.InstagramReels, "🟣 Instagram Reels",
        1080, 1920, 30, null, true, new[] { "#Reels", "#InstaReels", "#reelsinstagram" });

    public static SocialExportPreset For(SocialPresetKind kind) => kind == SocialPresetKind.InstagramReels ? InstagramReels : YouTubeShorts;

    /// <summary>9:16 세로 여부.</summary>
    public bool IsVertical916 => Width * 16 == Height * 9;

    public string AspectLabel => IsVertical916 ? "9:16" : Width + "×" + Height;

    /// <summary>표시용 사양 요약.</summary>
    public string SpecSummary => Width.ToString(System.Globalization.CultureInfo.InvariantCulture) + "×" + Height.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " (" + AspectLabel + ") · " + Fps.ToString(System.Globalization.CultureInfo.InvariantCulture) + "fps"
        + (LoudnessLufs is { } l ? " · " + l.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " LUFS" : "")
        + (FilmGrain ? " · 소프트 필름 그레인" : "");

    /// <summary>기존 태그 목록에 프리셋 해시태그를 앞쪽에 병합(대소문자 무시 중복 제거 · '#' 유무 무시).</summary>
    public IReadOnlyList<string> MergeTags(IReadOnlyList<string> existing)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var r = new List<string>(Hashtags.Count + existing.Count);
        void Add(string t)
        {
            var s = (t ?? "").Trim();
            if (s.Length == 0) return;
            if (seen.Add(s.TrimStart('#'))) r.Add(s);
        }
        foreach (var t in Hashtags) Add(t);
        foreach (var t in existing) Add(t);
        return r;
    }
}
