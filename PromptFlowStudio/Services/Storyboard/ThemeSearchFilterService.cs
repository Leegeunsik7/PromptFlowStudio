using System;
using System.Collections.Generic;
using System.Linq;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>
/// [PromptFlowStudio] 테마 실시간 키워드 검색 필터(v3.1.0) — 순수 C# BCL LINQ(외부 라이브러리 0).
///   ▸ 아기 테마(102종)·B2B/자동차 테마(45종)를 장비명(PODS/AEB/가진기/다이나모)·연출 키워드로 즉시 필터링(대소문자 무시·다중 토큰 AND).
///   ▸ 제목·훅·예시·키·비트(나레이션/골드/샷)까지 전문 검색 → 카드 렌더 목록만 좁혀 0.05초 내 반응(N≈150 선형 스캔).
/// </summary>
public sealed class ThemeSearchFilterService
{
    /// <summary>테마가 질의(공백 구분 다중 토큰 · 모두 포함)와 일치하는지.</summary>
    public bool Matches(BabyThemeDef theme, string? query)
    {
        if (theme is null) return false;
        var tokens = Tokenize(query);
        if (tokens.Count == 0) return true;                       // 빈 질의 → 전체 통과
        var hay = Haystack(theme);
        return tokens.All(t => hay.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>테마 목록을 필터링한다(순서 보존).</summary>
    public IReadOnlyList<BabyThemeDef> Filter(IEnumerable<BabyThemeDef> themes, string? query)
    {
        if (themes is null) return Array.Empty<BabyThemeDef>();
        var tokens = Tokenize(query);
        if (tokens.Count == 0) return themes.ToList();
        return themes.Where(t => { var h = Haystack(t); return tokens.All(k => h.Contains(k, StringComparison.OrdinalIgnoreCase)); }).ToList();
    }

    /// <summary>카테고리 목록에서 매칭 테마만 남긴 카테고리를 반환(빈 카테고리 제외 · 탭 구조 유지).</summary>
    public IReadOnlyList<BabyCategory> FilterCategories(IEnumerable<BabyCategory> categories, string? query)
    {
        if (categories is null) return Array.Empty<BabyCategory>();
        if (Tokenize(query).Count == 0) return categories.ToList();
        var outp = new List<BabyCategory>();
        foreach (var c in categories)
        {
            var kept = Filter(c.Themes, query);
            if (kept.Count > 0) outp.Add(c with { Themes = kept });
        }
        return outp;
    }

    /// <summary>필터 결과 총 개수(배지용).</summary>
    public int Count(IEnumerable<BabyThemeDef> themes, string? query) => Filter(themes, query).Count;

    // 검색 대상 문자열(제목/훅/예시/키 + 비트 나레이션·골드·샷).
    private static string Haystack(BabyThemeDef t)
    {
        var beats = t.Beats is null ? "" : string.Join(' ', t.Beats.Select(b => $"{b.V} {b.G} {b.Shot}"));
        return $"{t.Title} {t.Hook} {t.Example} {t.Key} {t.Emoji} {BabyEverydayThemeDatabase.SearchTagsOf(t.CategoryKey)} {beats}";   // v16.6.0 카테고리 동의어 태그
    }

    private static List<string> Tokenize(string? q)
        => (q ?? "").Trim().Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0).ToList();
}
