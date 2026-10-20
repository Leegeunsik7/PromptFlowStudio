using System;
using System.Collections.Generic;
using System.Linq;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>한글 현지화 결과 — 자연스러운 한글 요약 + 연출 태그(한글) + 원본 영문 프롬프트.</summary>
public sealed record LocalizedPrompt(string KoreanSummary, IReadOnlyList<string> DirectionTags, string EnglishPrompt, bool HasEnglish);

/// <summary>
/// [PromptFlowStudio] 한국어 프롬프트 현지화기 — 순수 BCL 오프라인(외부 NuGet 0 · Gemini 불필요).
///   ▸ 영어 시네마틱 프롬프트를 스캔해 <b>연출 태그(한글)</b>를 추출하고, 컷의 나레이션(한글)과 결합해 <b>자연스러운 한글 요약문</b>을 만든다.
///   ▸ 카테고리 순서(피사체→샷→렌즈→모션→조명→색감→화질)로 부분일치 매핑 → 크리에이터가 1초 만에 "무엇을 어떻게 찍는지" 이해.
///   ▸ 태그 미검출/빈 프롬프트도 Graceful(부스팅 유도 안내). 순수 함수(호출부 DI 불필요) — <see cref="StoryboardSceneCard"/>가 사용.
/// </summary>
public sealed class KoreanPromptLocalizer
{
    // (영문 소문자 부분일치, 한글 태그). 카테고리별로 스캔해 태그 목록 구성.
    private static readonly (string En, string Ko)[] Subject =
    {
        ("newborn", "👶 신생아"), ("infant", "👶 아기"), ("toddler", "👶 유아"), ("baby", "👶 아기"),
        ("child", "🧒 아이"), ("family", "👨‍👩‍👧 가족"),
        ("studio", "🎬 스튜디오 배경"), ("outdoor", "🌳 야외"), ("indoor", "🏠 실내"),
        ("nature", "🌿 자연 배경"), ("bedroom", "🛏️ 침실"), ("kitchen", "🍳 주방"), ("park", "🏞️ 공원"),
    };
    private static readonly (string En, string Ko)[] Shot =
    {
        ("extreme close", "익스트림 클로즈업"), ("close-up", "클로즈업"), ("closeup", "클로즈업"),
        ("wide shot", "와이드 샷"), ("full shot", "풀 샷"), ("medium shot", "미디엄 샷"),
        ("overhead", "오버헤드(부감)"), ("low angle", "로우앵글(앙각)"), ("high angle", "하이앵글(부감)"),
        ("pov", "1인칭 시점"), ("portrait", "인물 구도"), ("center framing", "중앙 구도"),
    };
    private static readonly (string En, string Ko)[] Lens =
    {
        ("macro", "🔬 접사(초근접)"), ("85mm", "85mm 망원"), ("50mm", "50mm 표준"), ("35mm", "35mm 광각"),
        ("wide-angle", "광각 렌즈"), ("wide angle", "광각 렌즈"), ("telephoto", "망원 렌즈"), ("fisheye", "어안 렌즈"),
    };
    private static readonly (string En, string Ko)[] Motion =
    {
        ("slow motion", "🐢 슬로모션"), ("slow-mo", "🐢 슬로모션"), ("timelapse", "⏩ 타임랩스"),
        ("dolly", "돌리(전후 이동)"), ("tracking", "트래킹(따라가기)"), ("orbit", "오빗(회전)"),
        ("zoom in", "줌 인"), ("zoom out", "줌 아웃"), ("zoom", "줌"),
        ("pan", "패닝(좌우)"), ("tilt", "틸트(상하)"), ("handheld", "핸드헬드"),
        ("smooth camera", "부드러운 카메라"), ("crane", "크레인 샷"), ("truck", "트럭(횡이동)"),
    };
    private static readonly (string En, string Ko)[] Light =
    {
        ("cinematic lighting", "🎬 시네마틱 조명"), ("soft light", "부드러운 조명"), ("softbox", "소프트박스"),
        ("golden hour", "🌅 골든아워"), ("backlight", "역광"), ("rim light", "림 라이트"),
        ("natural light", "자연광"), ("studio light", "스튜디오 조명"), ("dramatic", "드라마틱 조명"), ("volumetric", "볼류메트릭"),
    };
    private static readonly (string En, string Ko)[] Color =
    {
        ("vibrant", "🎨 선명한 색감"), ("saturated", "진한 채도"), ("pastel", "파스텔 톤"),
        ("warm tone", "따뜻한 톤"), ("warm color", "따뜻한 톤"), ("cool tone", "차가운 톤"),
        ("high contrast", "고대비"), ("muted", "차분한 색감"), ("teal and orange", "틸&오렌지"), ("monochrome", "흑백"),
    };
    private static readonly (string En, string Ko)[] Quality =
    {
        ("photorealistic", "실사풍"), ("photo realistic", "실사풍"), ("realistic", "사실적"),
        ("8k", "8K 고해상도"), ("4k", "4K 고해상도"), ("high resolution", "고해상도"), ("highly detailed", "정밀 디테일"),
        ("bokeh", "보케(배경 흐림)"), ("shallow depth", "얕은 심도"), ("depth of field", "심도 표현"),
        ("film grain", "필름 그레인"), ("hdr", "HDR"), ("masterwork", "마스터급 품질"), ("masterpiece", "마스터급 품질"),
    };

    /// <summary>영문 프롬프트 + (선택) 나레이션(한글 힌트)로부터 한글 요약·연출 태그를 만든다. 항상 결과 반환.</summary>
    public LocalizedPrompt Localize(string? englishPrompt, string? koreanHint = null)
    {
        var en = (englishPrompt ?? "").Trim();
        var lower = en.ToLowerInvariant();
        var tags = new List<string>();

        AddHits(lower, Subject, tags);
        AddHits(lower, Shot, tags);
        AddHits(lower, Lens, tags);
        AddHits(lower, Motion, tags);
        AddHits(lower, Light, tags);
        AddHits(lower, Color, tags);
        AddHits(lower, Quality, tags);

        var summary = BuildSummary((koreanHint ?? "").Trim(), tags, en);
        return new(summary, tags, en, en.Length > 0);
    }

    private static void AddHits(string lower, (string En, string Ko)[] table, List<string> tags)
    {
        foreach (var (enTerm, ko) in table)
            if (lower.Contains(enTerm, StringComparison.Ordinal) && !tags.Contains(ko))
                tags.Add(ko);
    }

    private static string BuildSummary(string hint, List<string> tags, string en)
    {
        if (en.Length == 0)
            return "장면 프롬프트가 비어 있어요. 이 컷의 내용을 입력하면 한글 연출 가이드가 표시됩니다.";

        var core = hint.Length > 0 ? $"“{hint}” 장면" : "이 컷";
        if (tags.Count == 0)
            return $"{core}을(를) 생성합니다. [⚡ 프롬프트 부스팅]으로 시네마틱 연출 태그를 더해 보세요.";

        var top = string.Join(" · ", tags.Take(4));
        return $"{core}을(를) {top} 스타일로 연출합니다.";
    }
}
