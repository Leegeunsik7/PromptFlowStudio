using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>
/// [PromptFlowStudio] 8대 플랫폼 특화 OSMU 메타데이터 & 태그 엔진 — 순수 BCL(외부 NuGet 0 · 완전 오프라인).
///   ▸ 1회 생성 콘텐츠(<see cref="BabySeoAgent"/> A/B SEO)를 <b>8대 플랫폼 알고리즘 특성</b>에 맞춰 재구성한다:
///     ①유튜브 숏츠 ②인스타 릴스 ③틱톡(기존 <see cref="OsmuMetadataPackager"/>) +
///     ④네이버 클립(검색 키워드) ⑤당근 스토리(동네 공감) ⑥샤오홍슈 RED(노트 감성) ⑦핀터레스트(에버그린 핀 키워드) ⑧치지직(클립 밈).
///   ▸ <see cref="OsmuExportModal"/>(Blazor)가 8대 플랫폼 탭 + 원클릭 복사/딥링크로 노출한다.
/// </summary>
public sealed class PlatformSeoAgent(BabySeoAgent seo, OsmuMetadataPackager osmu)
{
    // 신규 5대 플랫폼 태그 시드(플랫폼 문화 반영).
    private static readonly string[] NaverTags = { "네이버클립", "클립챌린지", "육아", "아기", "육아일상", "베이비" };
    private static readonly string[] KarrotTags = { "당근", "동네생활", "우리동네", "육아", "아기", "동네이웃" };
    private static readonly string[] RedTags = { "小红书", "育儿", "宝宝", "萌娃", "babyvlog", "육아" };
    private static readonly string[] PinTags = { "baby", "parenting", "babyvideos", "cutebaby", "momlife", "babyshorts" };
    private static readonly string[] ChzzkTags = { "치지직", "치지직클립", "클립", "육아", "아기밈", "밈" };

    /// <summary>8대 플랫폼용 메타데이터를 재구성한다(기존 3 + 신규 5). 실패 없이 항상 8개 반환.</summary>
    public IReadOnlyList<PlatformMeta> PackageAll(StoryboardProject project)
    {
        var pkg = osmu.Package(project);              // ①②③ 유튜브/인스타/틱톡(기존 로직 그대로)
        var s = seo.Generate(project);
        return new List<PlatformMeta>(pkg.All)
        {
            BuildNaverClip(s),
            BuildKarrot(s),
            BuildXiaohongshu(s),
            BuildPinterest(),
            BuildChzzk(s),
        };
    }

    private static string PickTitle(BabySeoResult s, string typeKeyword)
        => s.Titles.FirstOrDefault(t => t.Type.Contains(typeKeyword))?.Text
           ?? s.Titles.FirstOrDefault()?.Text ?? "";

    // ④ 네이버 클립 — 검색 알고리즘 중심(정보형 제목 + 검색 유입 키워드 전면 배치).
    private static PlatformMeta BuildNaverClip(BabySeoResult s)
    {
        var title = PickTitle(s, "정보");
        var tags = s.Hashtags.Concat(NaverTags).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();
        var body = new StringBuilder()
            .AppendLine(title).AppendLine()
            .Append("🔎 검색 유입 키워드: ").AppendLine(string.Join(", ", s.Keywords.Take(8)))
            .AppendLine("👶 우리 아기 리얼 반응 — 네이버 클립에서 더 많은 육아 영상을 만나보세요.")
            .ToString().Trim();
        return new PlatformMeta("Naver Clip", "🟢", title, body, tags);
    }

    // ⑤ 당근 스토리 — 동네 커뮤니티 공감(따뜻·이웃 소통체).
    private static PlatformMeta BuildKarrot(BabySeoResult s)
    {
        var tags = KarrotTags.Concat(s.Hashtags.Take(4)).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();
        var body = new StringBuilder()
            .AppendLine($"우리 동네 이웃님들 이것 좀 보세요 🥰 ({s.ThumbnailText})").AppendLine()
            .AppendLine("우리 아기 이런 표정 짓는데 너무 웃겨서 공유해요 ㅎㅎ")
            .AppendLine("동네 육아맘·대디들 공감 눌러주세요 🧡 댓글로 우리 동네 육아 이야기 나눠요!")
            .ToString().Trim();
        return new PlatformMeta("Karrot Story", "🥕", "", body, tags);
    }

    // ⑥ 샤오홍슈(RED) — 노트 감성 텍스트(이모지·중문/한글 혼용 표제어).
    private static PlatformMeta BuildXiaohongshu(BabySeoResult s)
    {
        var tags = RedTags.Concat(s.Hashtags.Take(4)).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
        var title = "🍼✨ " + PickTitle(s, "호기심");
        var body = new StringBuilder()
            .AppendLine(title).AppendLine()
            .AppendLine("宝宝的反应太可爱了🥹 오늘도 아기 덕분에 힐링 완료 💕")
            .AppendLine("📌 记录 | 育儿日常 | 萌娃时刻")
            .AppendLine("점점 자라는 모습을 노트로 남겨요 🌱 여러분 아기도 이랬나요?")
            .ToString().Trim();
        return new PlatformMeta("Xiaohongshu (RED)", "📕", title, body, tags);
    }

    // ⑦ 핀터레스트 — 에버그린 핀 키워드(영문 SEO 설명).
    private static PlatformMeta BuildPinterest()
    {
        var tags = PinTags.ToList();
        const string title = "Adorable Baby Reaction | Cute Baby Shorts";
        var body = new StringBuilder()
            .AppendLine(title).AppendLine()
            .AppendLine("Funny and heartwarming baby reactions you'll want to save! 👶💕")
            .AppendLine("Perfect for parenting inspiration, baby milestones, and cute baby video ideas.")
            .Append("Keywords: ")
            .AppendLine(string.Join(", ", new[] { "baby reaction", "cute baby", "funny baby", "parenting tips", "baby milestones", "baby video ideas" }))
            .ToString().Trim();
        return new PlatformMeta("Pinterest", "📌", title, body, tags);
    }

    // ⑧ 치지직 — 스트리밍 클립 밈 문화(밈각·박제체).
    private static PlatformMeta BuildChzzk(BabySeoResult s)
    {
        var tags = ChzzkTags.Concat(s.Hashtags.Take(4)).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();
        var title = PickTitle(s, "유머");
        var body = new StringBuilder()
            .AppendLine(title).AppendLine()
            .AppendLine("이거 클립 안 따면 손해 ㅋㅋㅋ 아기 리액션 밈각 🔥")
            .Append("치지직 클립으로 박제 완료 · 다시보기 필수 👀")
            .ToString().Trim();
        return new PlatformMeta("Chzzk", "🎮", title, body, tags);
    }
}
