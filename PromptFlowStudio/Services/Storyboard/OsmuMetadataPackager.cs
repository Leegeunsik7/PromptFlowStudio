using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>한 플랫폼용 재구성 메타데이터.</summary>
public sealed record PlatformMeta(string Platform, string Icon, string Title, string Body, IReadOnlyList<string> Hashtags)
{
    /// <summary>제목+본문+해시태그를 합친 전체 복사용 텍스트.</summary>
    public string FullText
    {
        get
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(Title)) sb.AppendLine(Title).AppendLine();
            if (!string.IsNullOrWhiteSpace(Body)) sb.AppendLine(Body).AppendLine();
            if (Hashtags.Count > 0) sb.Append(string.Join(" ", Hashtags.Select(h => "#" + h)));
            return sb.ToString().Trim();
        }
    }
    public string HashtagLine => string.Join(" ", Hashtags.Select(h => "#" + h));
}

/// <summary>OSMU 패키지(3 플랫폼).</summary>
public sealed record OsmuPackage(bool Ok, PlatformMeta YouTube, PlatformMeta Instagram, PlatformMeta TikTok, string Message)
{
    public IReadOnlyList<PlatformMeta> All => new[] { YouTube, Instagram, TikTok };
}

/// <summary>
/// [PromptFlowStudio] OSMU 멀티 플랫폼 메타데이터 패키저 — 순수 BCL(외부 NuGet 0 · 오프라인).
///   ▸ 1회 생성 콘텐츠(<see cref="BabySeoAgent"/> A/B SEO 결과)를 플랫폼 특성에 맞춰 재구성:
///     ①유튜브 숏츠(상세 설명·검색 태그 중심 · 정보 제공형 제목) ②인스타 릴스(감성·소통 중심 · 댓글 유도 캡션 · 감성 태그)
///     ③틱톡(트렌드 훅 중심 · B급 유머 훅 · fyp 태그).
///   ▸ <see cref="OsmuExportModal"/>(Blazor)가 플랫폼별 탭 + 원클릭 복사로 노출.
/// </summary>
public sealed class OsmuMetadataPackager(BabySeoAgent seo)
{
    private static readonly string[] IgTags = { "육아스타그램", "아기스타그램", "베이비스타그램", "육아소통", "instababy", "momlife" };
    private static readonly string[] TtTags = { "fyp", "foryou", "추천", "babytiktok", "viral" };

    /// <summary>프로젝트에서 3 플랫폼용 메타데이터를 재구성한다(SEO 생성 → 플랫폼 포맷). 항상 결과 반환.</summary>
    public OsmuPackage Package(StoryboardProject project)
    {
        var s = seo.Generate(project);
        var tCuriosity = s.Titles.FirstOrDefault(t => t.Type.Contains("호기심"))?.Text ?? s.Titles.FirstOrDefault()?.Text ?? project.Title;
        var tInfo = s.Titles.FirstOrDefault(t => t.Type.Contains("정보"))?.Text ?? tCuriosity;
        var tHumor = s.Titles.FirstOrDefault(t => t.Type.Contains("유머"))?.Text ?? tCuriosity;

        // ── 유튜브 숏츠: 정보 제공형 제목 + 상세 설명 + 검색 태그 ──
        var ytTags = s.Hashtags.Concat(new[] { "Shorts", "숏츠" }).Distinct(StringComparer.OrdinalIgnoreCase).Take(14).ToList();
        var ytBody = new StringBuilder()
            .AppendLine(tCuriosity).AppendLine()
            .AppendLine("👶 우리 아기의 리얼 반응! 끝까지 보면 반전이 있어요 😆").AppendLine()
            .Append("🔎 이 영상을 찾은 검색어: ").AppendLine(string.Join(", ", s.Keywords.Take(8)))
            .AppendLine("🎬 제작: PromptFlow Studio (Wan 2.1 4K · Kokoro · Gen-Foley)")
            .ToString().Trim();
        var youtube = new PlatformMeta("YouTube Shorts", "▶️", tInfo, ytBody, ytTags);

        // ── 인스타 릴스: 감성 캡션 + 댓글 유도 + 감성 해시태그 ──
        var igTags = s.Hashtags.Concat(IgTags).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();
        var igBody = new StringBuilder()
            .AppendLine($"오늘도 우리 아기 덕분에 한참 웃었어요 🥰 ({s.ThumbnailText})").AppendLine()
            .AppendLine("여러분 아기도 이랬나요? 🍼 댓글로 우리 아기 이야기 들려주세요 💬")
            .AppendLine("저장해두고 힘들 때 다시 보기 🔖")
            .ToString().Trim();
        var instagram = new PlatformMeta("Instagram Reels", "📸", "", igBody, igTags);

        // ── 틱톡: 트렌드 훅(짧고 강하게) + fyp 태그 ──
        var ttTags = s.Hashtags.Take(6).Concat(TtTags).Distinct(StringComparer.OrdinalIgnoreCase).Take(14).ToList();
        var ttBody = new StringBuilder()
            .AppendLine(tHumor)
            .Append("끝까지 보기 필수 👀 저장각")
            .ToString().Trim();
        var tiktok = new PlatformMeta("TikTok", "🎵", "", ttBody, ttTags);

        return new(true, youtube, instagram, tiktok, "OSMU 재구성 완료");
    }
}
