using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PromptFlowStudio.Services.Storyboard;

// ════════════════════════════════════════════════════════════════════════════
//  [PromptFlowStudio] v2.3.0 클라우드(Claude) 자율 창의 확장 — 순수 BCL(외부 NuGet 0 · 전부 테스트 가능).
//    핵심 4대 태스크 외에 연출력/몰입감/안정성을 끌어올리는 3가지 독자 기능을 발상·구현:
//    ① 스토리 아크 품질 분석기(연출 코칭)  ② 노드 클릭 SFX 시연(몰입)  ③ B2B 3D 로고 엠보싱 워터마크(브랜딩).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>스토리 아크 품질 평가 결과 — 점수(0~100)·등급·라벨·개선 팁.</summary>
public sealed record ArcQuality(int Score, string Grade, string Label, string Tip);

/// <summary>
/// [자율확장 ①] 스토리 아크 품질 분석기 — 컷별 긴장도 곡선을 평가해 "빌드업·클라이맥스·여운"이 좋은지 코칭.
///   ▸ 변동폭(밋밋함 감점) · 클라이맥스 존재/위치(중후반 가점) · 상승 흐름 · 여운(하강) 종합 → 점수/등급/팁.
///   ▸ <see cref="CinematicTensionGraph"/> 헤더 배지로 노출해 크리에이터가 연출을 즉시 개선하도록 돕는다(순수 함수).
/// </summary>
public static class TensionArcQualityAnalyzer
{
    /// <summary>긴장도 리스트로 아크 품질을 평가한다(빈/단일 컷은 안내).</summary>
    public static ArcQuality Analyze(IReadOnlyList<int> tensions)
    {
        var t = (tensions ?? Array.Empty<int>()).Select(v => Math.Clamp(v, 0, 100)).ToList();
        if (t.Count < 2)
            return new(0, "—", "컷이 더 필요해요", "컷을 2개 이상 구성하면 스토리 아크가 분석됩니다.");

        var max = t.Max();
        var min = t.Min();
        var range = max - min;                                   // 변동폭(밋밋함 방지)
        var peakIdx = t.IndexOf(max);
        var peakPos = peakIdx / (double)(t.Count - 1);           // 클라이맥스 위치(중후반이 이상적)

        // 상승 정도 — 피크 이전 구간이 대체로 오르는가.
        var rises = 0;
        for (var i = 1; i <= peakIdx; i++) if (t[i] >= t[i - 1]) rises++;
        var buildup = peakIdx > 0 ? rises / (double)peakIdx : 0;

        // 여운 — 피크 이후 하강이 있는가.
        var hasPayoff = peakIdx < t.Count - 1 && t[^1] < max;

        // 점수 합산(변동폭 40 + 클라이맥스 위치 25 + 빌드업 20 + 여운 15).
        var sRange = Math.Clamp(range / 60.0, 0, 1) * 40;
        var sPeak = (peakPos is >= 0.5 and <= 0.92 ? 1.0 : Math.Max(0, 1 - Math.Abs(peakPos - 0.7) * 1.6)) * 25;
        var sBuild = buildup * 20;
        var sPay = hasPayoff ? 15 : 0;
        var score = (int)Math.Round(Math.Clamp(sRange + sPeak + sBuild + sPay, 0, 100));

        var (grade, label, tip) = score switch
        {
            >= 85 => ("S", "완벽한 몰입 아크", "기승전결이 탁월해요. 이대로 생성하면 몰입도 최상!"),
            >= 70 => ("A", "탄탄한 스토리 아크", "좋은 흐름이에요. 클라이맥스를 한 컷 더 강조하면 S급!"),
            >= 50 => ("B", "무난한 아크", "변화가 보여요. 중반 긴장을 낮추고 후반 피크를 높여 대비를 키워보세요."),
            >= 30 => ("C", "밋밋한 편", "긴장도 변화가 약해요. 클라이맥스 컷의 텐션을 크게 올려 반전을 주세요."),
            _     => ("D", "평탄한 곡선", "컷마다 긴장도가 비슷해요. 조용→폭발 대비를 넣어야 숏츠가 살아납니다."),
        };
        return new(score, grade, label, tip);
    }
}

/// <summary>SFX 시연 큐 1개 — 타입·이모지·한국어 라벨·발사 시각(ms).</summary>
public sealed record SfxCueInfo(string Type, string Emoji, string Label, int Ms);

/// <summary>씬의 SFX 시연 정보(노드 클릭 팝업용).</summary>
public sealed record SfxAudition(bool Ok, IReadOnlyList<SfxCueInfo> Cues);

/// <summary>
/// [자율확장 ②] 노드 클릭 SFX 시연 서비스 — 텐션 그래프 노드를 클릭하면 그 컷의 폴리(SFX)를 이모지+라벨 팝업으로 시연.
///   ▸ 컷의 <see cref="StoryboardScene.PresetFoley"/>를 타입→이모지/한국어 라벨로 매핑(오프라인·파일 불필요).
///   ▸ UI(그래프)는 이 데이터로 팝업을 띄우고 Web Audio 로 타입별 합성음을 재생 → 몰입감 있는 SFX 미리듣기.
/// </summary>
public sealed class SfxAuditionService
{
    private static readonly Dictionary<string, (string Emoji, string Label)> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["crash"] = ("💥", "쿵!"), ["boing"] = ("🌀", "보잉~"), ["squeak"] = ("🐤", "삑!"), ["giggle"] = ("😄", "까르르"),
        ["pop"] = ("✨", "팝!"), ["sparkle"] = ("🌟", "반짝"), ["ding"] = ("🔔", "딩~"), ["whoosh"] = ("💨", "휙!"),
        ["chomp"] = ("😋", "아작"), ["kiss"] = ("💋", "쪽!"), ["clap"] = ("👏", "짝짝"), ["splash"] = ("💦", "첨벙"),
        ["suspense"] = ("😳", "두구두구"), ["drumroll"] = ("🥁", "두구두구"), ["pluck"] = ("🎵", "통~"), ["cute"] = ("🍼", "뾰옹"),
    };

    /// <summary>타입 → (이모지, 라벨). 미상은 (🔊, type).</summary>
    public static (string Emoji, string Label) Info(string? type)
        => type is not null && Map.TryGetValue(type, out var v) ? v : ("🔊", type ?? "SFX");

    /// <summary>씬의 폴리 SFX 큐를 시연 정보로 변환(없으면 Ok=false).</summary>
    public SfxAudition ForScene(StoryboardScene? scene)
    {
        var foley = scene?.PresetFoley;
        if (foley is null || foley.Count == 0) return new(false, Array.Empty<SfxCueInfo>());
        var cues = foley.Where(f => f is not null && !string.IsNullOrWhiteSpace(f.SfxType))
            .Select(f => { var (e, l) = Info(f.SfxType); return new SfxCueInfo(f.SfxType, e, l, f.TimestampMs); })
            .ToList();
        return new(cues.Count > 0, cues);
    }
}

/// <summary>3D 엠보싱 워터마크 스타일 — 볼록(양각)/오목(음각).</summary>
public enum EmbossStyle { Raised, Engraved }

/// <summary>
/// [자율확장 ③] B2B 3D 로고 엠보싱 워터마크 — 회사 로고를 <b>입체 양각(bevel)</b>으로 합성하는 FFmpeg 필터 빌더(순수 문자열·안전).
///   ▸ 로고를 스케일 후 어두운/밝은 오프셋 복제를 겹쳐 <b>3D 음영 베벨</b>을 만들고 우측 상단에 오버레이 → 프리미엄 브랜딩 톤.
///   ▸ 내부 상수 조합만 사용(외부 입력 미개입) · <see cref="VideoMergeService"/> 워터마크 자리에 대체 삽입 가능.
/// </summary>
public sealed class CorporateWatermarkEmbossService
{
    /// <summary>3×3 엠보스 컨볼루션 커널(양각).</summary>
    public const string RaisedKernel = "-2 -1 0 -1 1 1 0 1 2";
    /// <summary>3×3 엠보스 컨볼루션 커널(음각).</summary>
    public const string EngravedKernel = "2 1 0 1 1 -1 0 -1 -2";

    /// <summary>
    /// 로고 입력(<paramref name="logoInputIdx"/>)을 3D 엠보싱해 우측 상단(마진 24px)에 오버레이하는 filter_complex 조각을 만든다.
    ///   결과 라벨은 <paramref name="baseLabel"/>(예: outv) → <paramref name="outLabel"/>(예: embw). 로고 폭은 출력폭의 ~14%.
    /// </summary>
    public string BuildEmbossOverlay(int logoInputIdx, int outWidth, string baseLabel, string outLabel, EmbossStyle style = EmbossStyle.Raised)
    {
        var logoW = Math.Max(48, outWidth / 7);
        var kernel = style == EmbossStyle.Raised ? RaisedKernel : EngravedKernel;
        // 로고 스케일+알파 유지 → 엠보스 컨볼루션(3D 음영) → 살짝 부드럽게 → 우측 상단 오버레이.
        return
            $"[{logoInputIdx}:v]scale={logoW.ToString(CultureInfo.InvariantCulture)}:-1,format=rgba,"
            + $"convolution='{kernel}':'{kernel}':'{kernel}':'0 0 0 0 1 0 0 0 0',"
            + $"gblur=sigma=0.4[emblogo];"
            + $"[{baseLabel}][emblogo]overlay=W-w-24:24:format=auto[{outLabel}]";
    }
}
