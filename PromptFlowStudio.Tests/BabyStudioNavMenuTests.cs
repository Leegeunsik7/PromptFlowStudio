using System;
using System.IO;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>Step 4: 좌측 사이드바 — 아기 스튜디오 5대 핵심 워크플로우 2 그룹 + 기본 접힘 고급/개발자 도구 아코디언.</summary>
public class BabyStudioNavMenuTests
{
    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "PromptFlowStudio", "PromptFlowStudio.csproj"))) return d.FullName;
        throw new DirectoryNotFoundException("레포 루트");
    }

    private static string Root => RepoRoot();
    private static string Layout() => File.ReadAllText(Path.Combine(Root, "PromptFlowStudio", "Blazor", "MainLayout.razor"));
    private static string Nav()
    {
        var s = Layout();
        var a = s.IndexOf("<nav class=\"nav\"", StringComparison.Ordinal);
        var b = s.IndexOf("</nav>", a, StringComparison.Ordinal);
        Assert.True(a >= 0 && b > a, "nav 블록 누락");
        return s.Substring(a, b - a);
    }

    private static string Page(string file) => File.ReadAllText(Path.Combine(Root, "PromptFlowStudio", "Blazor", "Pages", file));

    [Fact]
    public void NavMenu_ExposesFiveCoreBabyStudioNavigationLinks()
    {
        var nav = Nav();
        var devAt = nav.IndexOf("data-nav-dev-tools", StringComparison.Ordinal);
        Assert.True(devAt > 0, "고급/개발자 도구 아코디언 누락");

        var g1 = nav.IndexOf("🎨 아기 스튜디오", StringComparison.Ordinal);
        var g2 = nav.IndexOf("📦 앨범 &amp; 보관함", StringComparison.Ordinal);
        Assert.True(g1 >= 0 && g2 > g1 && devAt > g2, "그룹 순서: 아기 스튜디오 → 앨범&보관함 → 개발자 도구");

        (string Core, string Href, string Label, string PageFile, string Route)[] core =
        {
            ("single-cut",   "href=\"\"",           "1컷 단편 제작",   "Workspace.razor",       "@page \"/workspace\""),
            ("growth-diary", "href=\"storyboard\"", "4컷 성장 일기",   "Storyboard.razor",      "@page \"/storyboard\""),
            ("shorts",       "href=\"ai-cutdown\"", "15초 릴스·쇼츠",  "AiShortsCutdown.razor", "@page \"/ai-cutdown\""),
            ("album",        "href=\"gallery\"",    "아기 영상 앨범",  "MediaGallery.razor",    "@page \"/gallery\""),
            ("queue",        "href=\"queue\"",      "렌더링 대기열",   "RenderQueue.razor",     "@page \"/queue\""),
            ("settings",     "href=\"settings\"",   "스튜디오 설정",   "Settings.razor",        "@page \"/settings\""),
        };
        foreach (var c in core)
        {
            var at = nav.IndexOf("data-nav-core=\"" + c.Core + "\"", StringComparison.Ordinal);
            Assert.True(at > 0 && at < devAt, "핵심 링크가 개발자 도구 위에 없음: " + c.Core);
            var tagStart = nav.LastIndexOf("<NavLink", at, StringComparison.Ordinal);
            var tagEnd = nav.IndexOf("</NavLink>", at, StringComparison.Ordinal);
            var tag = nav.Substring(tagStart, tagEnd - tagStart);
            Assert.Contains(c.Href, tag, StringComparison.Ordinal);
            Assert.Contains(c.Label, tag, StringComparison.Ordinal);
            Assert.Contains(c.Route, Page(c.PageFile), StringComparison.Ordinal);
        }
        Assert.Contains("Match=\"NavLinkMatch.All\" data-nav-core=\"single-cut\"", nav, StringComparison.Ordinal);
    }

    [Fact]
    public void NavMenu_DeveloperToolsAccordion_IsCollapsedByDefault()
    {
        var s = Layout();
        var nav = Nav();
        Assert.Contains("private bool _devToolsOpen;", s, StringComparison.Ordinal);
        Assert.DoesNotContain("_devToolsOpen = true", s, StringComparison.Ordinal);
        Assert.Contains("🛠️ 고급 / 개발자 도구", nav, StringComparison.Ordinal);

        var gate = nav.IndexOf("@if (_devToolsOpen)", StringComparison.Ordinal);
        var bodyEnd = nav.IndexOf("</div>\r\n                }", gate, StringComparison.Ordinal);
        if (bodyEnd < 0) bodyEnd = nav.IndexOf("</div>\n                }", gate, StringComparison.Ordinal);
        Assert.True(gate > 0 && bodyEnd > gate, "개발자 도구 본문이 _devToolsOpen 게이트 밖에 있음");
        var body = nav.Substring(gate, bodyEnd - gate);

        string[] dev = { "24/7 AI 라이브 스튜디오", "커머셜 브랜드 스튜디오", "생산성 &amp; 비용", "노드 오케스트레이터", "비주얼 노드 플로우", "모니터 시뮬레이터", "글로벌 관제" };
        foreach (var t in dev)
        {
            Assert.Contains(t, body, StringComparison.Ordinal);
            Assert.True(nav.IndexOf(t, StringComparison.Ordinal) > gate, "개발자 링크가 게이트 밖에 노출: " + t);
        }

        // 라우트 보존 — 페이지 컴포넌트는 삭제하지 않는다.
        string[] hrefs = { "live-studio", "brand-studio", "analytics", "flow", "node-studio" };
        foreach (var h in hrefs) Assert.Contains("href=\"" + h + "\"", body, StringComparison.Ordinal);

        // 하단: 테마 스위치·프로필 유지, 중복 방송 토글 제거.
        Assert.Contains("new SideOpt(\"theme\"", s, StringComparison.Ordinal);
        Assert.DoesNotContain("new SideOpt(\"broadcast\"", s, StringComparison.Ordinal);
        Assert.Contains("class=\"side-foot\"", s, StringComparison.Ordinal);
    }
}
