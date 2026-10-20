using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PromptFlowStudio.Services.Media;   // FoleySpec / VideoTransition

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>폴리싱된 컷 비트 — 나레이션·강조어·시네마틱 샷·긴장도·타임코드(ms)·서사 단계.</summary>
public sealed record PolishedBeat(string Narration, string Gold, string Shot, int Tension, int TimecodeMs, string Phase);

/// <summary>대본 폴리싱 결과 — 비트 목록 + 요약.</summary>
public sealed record PolishResult(IReadOnlyList<PolishedBeat> Beats, string Summary)
{
    public bool HasData => Beats.Count > 0;
}

/// <summary>
/// [PromptFlowStudio] AI 대본 오프라인 폴리싱 &amp; 동적 컷 확장기(v3.3.0) — 순수 C# BCL(외부 ML 라이브러리 0).
///   ▸ 원문 대본을 문장 분해 → <b>서사 구조(도입·전개·위기·결말)</b>로 배분하고, 단계별 <b>긴장도 아크</b>(완만 상승→클라이맥스→해소)를 자동 할당.
///   ▸ 각 문장의 강조어(최장 명사 근사)를 골드 워드로, 단계별 카메라/조명 템플릿으로 <b>Wan 2.1 시네마틱 샷 프롬프트</b>를 오프라인 생성.
///   ▸ 컷별 <b>비트 타임코드</b>(균등 배분)를 부여하고 <see cref="ApplyToProject"/> 로 스토리보드 컷에 즉시 반영(신규 스키마 0 · 역호환).
/// </summary>
public sealed class ScriptPolishEnhancer
{
    private const int DefaultClipMs = 4600;

    // 서사 단계 정의(순서 · 긴장도 기준 · 카메라/조명 무드).
    private static readonly (string Phase, string Lens, string Light, string Mood)[] PhaseStyles =
    {
        ("도입", "35mm establishing", "soft warm morning light", "serene inviting establishing atmosphere"),
        ("전개", "50mm", "natural balanced daylight", "engaging dynamic build-up"),
        ("위기", "85mm tight", "dramatic high-contrast light", "intense climactic tension"),
        ("결말", "50mm", "golden hour warm backlight", "heartfelt resolving finale"),
    };

    /// <summary>원문 대본을 폴리싱해 서사형 컷 비트로 확장한다(targetCuts 개 · 타임코드/긴장도 자동).</summary>
    public PolishResult Polish(string? rawScript, int targetCuts = 6, int clipMs = DefaultClipMs)
    {
        var sentences = SplitSentences(rawScript);
        if (sentences.Count == 0) return new(Array.Empty<PolishedBeat>(), "폴리싱할 대본이 없습니다.");

        var n = Math.Clamp(targetCuts, 3, 20);
        var beats = new List<PolishedBeat>(n);
        for (var i = 0; i < n; i++)
        {
            // 문장 리샘플(문장 수 ≠ 컷 수 대응).
            var src = sentences[(int)Math.Round(i * (sentences.Count - 1) / (double)(n - 1))];
            var (phase, lens, light, mood) = PhaseFor(i, n);
            var narration = PolishSentence(src);
            var gold = PickGold(narration);
            var shot = BuildShot(narration, lens, light, mood);
            var tension = TensionFor(i, n, phase);
            beats.Add(new PolishedBeat(narration, gold, shot, tension, i * clipMs, phase));
        }
        var phases = string.Join("→", beats.Select(b => b.Phase).Distinct());
        return new PolishResult(beats, $"{beats.Count}컷 · {phases} 서사 구조 · 긴장도 아크 자동 배분");
    }

    /// <summary>폴리싱 결과를 스토리보드 컷으로 반영한다(Prompt/VoiceText/긴장도/키네틱). 기존 앵커 유지.</summary>
    public int ApplyToProject(StoryboardProject project, PolishResult result, int clipMs = DefaultClipMs)
    {
        if (project is null || result is null || !result.HasData) return 0;
        var portrait = true;
        var size = portrait ? "720*1280" : "1280*720";
        project.Scenes.Clear();
        foreach (var b in result.Beats)
        {
            project.Scenes.Add(new StoryboardScene
            {
                Mode = string.IsNullOrWhiteSpace(project.AnchorImagePath) ? SceneMode.TextToVideo : SceneMode.ImageToVideo,
                Prompt = b.Shot,
                VoiceText = b.Narration,
                Size = size,
                PresetTension = b.Tension,
                PresetKineticWords = BabyThemePresetService.BuildKineticWords(b.Narration, b.Gold, clipMs),
                EnterTransition = new VideoTransition("fade", 400),
            });
        }
        project.UseKineticCaptions = true;
        project.UseEmotionPacing = true;
        return project.Scenes.Count;
    }

    // ── 순수 헬퍼(테스트 대상) ──

    /// <summary>대본을 문장으로 분해(마침표/물음표/느낌표/줄바꿈 기준 · 공백 정리).</summary>
    public static List<string> SplitSentences(string? raw)
    {
        var s = (raw ?? "").Replace("\r", "\n");
        var parts = s.Split(new[] { '.', '!', '?', '\n', '。', '！', '？' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Select(p => System.Text.RegularExpressions.Regex.Replace(p.Trim(), @"\s+", " "))
                    .Where(p => p.Length > 0).ToList();
    }

    /// <summary>인덱스/총수 → 서사 단계 스타일(도입 25% · 전개 40% · 위기 20% · 결말 15%).</summary>
    public static (string Phase, string Lens, string Light, string Mood) PhaseFor(int i, int n)
    {
        var p = n <= 1 ? 0.0 : i / (double)(n - 1);
        var idx = p < 0.25 ? 0 : p < 0.65 ? 1 : p < 0.85 ? 2 : 3;
        return PhaseStyles[idx];
    }

    /// <summary>단계+위치별 긴장도(도입 낮음 → 전개 상승 → 위기 클라이맥스 → 결말 해소).</summary>
    public static int TensionFor(int i, int n, string phase)
    {
        double p = n <= 1 ? 0 : i / (double)(n - 1);
        return phase switch
        {
            "도입" => 30,
            "전개" => (int)Math.Round(45 + p * 30),        // 45~72 상승
            "위기" => (int)Math.Round(84 + p * 8),         // 84~92 클라이맥스
            _ => 40,                                        // 결말 해소
        };
    }

    private static string PolishSentence(string s)
    {
        s = s.Trim();
        // 종결 어미가 없으면 부드러운 마침 추가(나레이션 호흡).
        if (s.Length > 0 && !"다요죠까!?.…~".Contains(s[^1])) s += "…";
        return s;
    }

    private static string PickGold(string s)
    {
        var words = s.Split(new[] { ' ', ',', '·', '…' }, StringSplitOptions.RemoveEmptyEntries)
                     .Select(w => w.Trim('.', '!', '?', '~')).Where(w => w.Length >= 2).ToList();
        return words.Count == 0 ? "" : words.OrderByDescending(w => w.Length).First();
    }

    private static string BuildShot(string narration, string lens, string light, string mood)
    {
        // 나레이션에서 핵심 명사 후보를 힌트로만 사용(원문 노출 최소화 · 영어 프롬프트 골격).
        var hint = narration.Length > 42 ? narration[..42] : narration;
        return $"cinematic {lens} lens, {light}, {mood}, evoking “{hint}”, 4K UHD, photorealistic, hyper-detailed, film grain";
    }
}
