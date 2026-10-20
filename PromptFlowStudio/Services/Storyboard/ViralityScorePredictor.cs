using System;
using System.Collections.Generic;
using System.Linq;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>바이럴 몰입도 예측 결과 — 총점(0~100)·세부 점수·등급·이탈 위험 구간·개선 팁.</summary>
public sealed record ViralityScore(
    int Total, int HookScore, int SfxScore, int KeywordScore, int TempoScore,
    double Wpm, string Grade, IReadOnlyList<string> Risks, string Tip);

/// <summary>
/// [PromptFlowStudio] 바이럴 숏츠 첫 3초 훅(Hook) 점수 예측 연산기(v3.0.0) — 순수 C# BCL 계량 휴리스틱(외부 ML 라이브러리 0).
///   ▸ <b>컷1 SFX 유무</b> + <b>시각 훅 키워드</b>(반전/충격/마법/스파이크…) + <b>나레이션 WPM</b>(분당 단어 수) + 텐션 오프닝을 계량화해 <b>100점 만점 몰입도 점수</b> 산출.
///   ▸ 이탈 위험 구간(약한 훅·템포 처짐·저밀도 사운드)을 가이드로 제시 → 첫 3초 이탈률 최소화 코칭.
/// </summary>
public sealed class ViralityScorePredictor
{
    private const double ClipSecPerCut = 4.6;   // 숏츠 컷 평균 길이(WPM 추정 기준)

    private static readonly string[] ViralKeywords =
    { "반전", "충격", "마법", "스파이크", "귀여운", "폭발", "대박", "리액션", "reveal", "shock", "magic", "spike", "cute", "burst", "twist", "pop", "surprise", "epic", "wow" };

    /// <summary>프로젝트의 바이럴 몰입도를 예측한다.</summary>
    public ViralityScore Predict(StoryboardProject project)
    {
        var scenes = project?.Scenes ?? new List<StoryboardScene>();
        if (scenes.Count == 0)
            return new(0, 0, 0, 0, 0, 0, "-", new[] { "컷이 없습니다 — 스토리보드를 먼저 구성하세요." }, "컷을 추가하세요.");

        var risks = new List<string>();
        var c1 = scenes[0];

        // ── ① 훅(0~30): 컷1 오프닝 텐션 + 훅 키워드 ──
        var c1Tension = c1.PresetTension ?? 0;
        var c1HasKeyword = ViralKeywords.Any(k => (c1.Prompt ?? "").ToLowerInvariant().Contains(k.ToLowerInvariant()));
        var hook = Math.Clamp((int)(c1Tension * 0.2) + (c1HasKeyword ? 14 : 0), 0, 30);
        if (hook < 16) risks.Add("첫 3초 훅이 약함 — 컷1에 반전/충격 요소와 강한 오프닝을 넣으세요.");

        // ── ② SFX(0~25): 컷1 폴리 + 전체 폴리 밀도 ──
        var c1Sfx = (c1.PresetFoley?.Count ?? 0) > 0;
        var foleyCuts = scenes.Count(s => (s.PresetFoley?.Count ?? 0) > 0);
        var sfx = Math.Clamp((c1Sfx ? 14 : 0) + (int)(foleyCuts / (double)scenes.Count * 11), 0, 25);
        if (!c1Sfx) risks.Add("컷1에 효과음이 없음 — 첫 프레임 임팩트 사운드로 주목도를 높이세요.");

        // ── ③ 키워드(0~25): 전 컷 바이럴 시각 키워드 밀도 ──
        var kwCuts = scenes.Count(s => ViralKeywords.Any(k => (s.Prompt ?? "").ToLowerInvariant().Contains(k.ToLowerInvariant())));
        var keyword = Math.Clamp((int)(kwCuts / (double)scenes.Count * 25), 0, 25);

        // ── ④ 템포(0~20): 나레이션 WPM 이상대(130~190) ──
        var words = scenes.Sum(s => WordCount(s.VoiceText));
        var totalSec = Math.Max(1.0, scenes.Count * ClipSecPerCut);
        var wpm = words / (totalSec / 60.0);
        var tempo = WpmScore(wpm);
        if (wpm > 210) risks.Add($"나레이션 템포 과속(WPM {wpm:0}) — 컷당 문장을 줄이세요.");
        else if (wpm > 0 && wpm < 90) risks.Add($"나레이션 템포 느림(WPM {wpm:0}) — 정보 밀도를 높이세요.");

        // ── 텐션 처짐 구간(이탈 위험) ──
        for (var i = 1; i < scenes.Count; i++)
            if ((scenes[i].PresetTension ?? 0) < (scenes[i - 1].PresetTension ?? 0) - 25)
            { risks.Add($"{i + 1}번 컷에서 몰입 급락 위험 — 텐션 유지/전환 강화."); break; }

        var total = Math.Clamp(hook + sfx + keyword + tempo, 0, 100);
        var grade = total >= 85 ? "S" : total >= 70 ? "A" : total >= 55 ? "B" : total >= 40 ? "C" : "D";
        var tip = BuildTip(hook, sfx, keyword, tempo);
        return new(total, hook, sfx, keyword, tempo, Math.Round(wpm, 0), grade, risks, tip);
    }

    /// <summary>WPM → 템포 점수(0~20 · 130~190 이상대 만점, 바깥은 감쇠). 순수·테스트 대상.</summary>
    public static int WpmScore(double wpm)
    {
        if (wpm <= 0) return 6;                       // 나레이션 없음(무음 숏츠) → 중립
        if (wpm >= 130 && wpm <= 190) return 20;      // 이상대
        var dist = wpm < 130 ? 130 - wpm : wpm - 190;
        return (int)Math.Clamp(20 - dist / 6.0, 0, 20);
    }

    private static int WordCount(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return 0;
        var spaceWords = t.Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
        return Math.Max(spaceWords, t.Replace(" ", "").Length / 3);   // 한국어 어절 근사(공백 적을 때 글자수 기반)
    }

    private static string BuildTip(int hook, int sfx, int keyword, int tempo)
    {
        var worst = new (string Name, int Val, int Max, string Tip)[]
        {
            ("훅", hook, 30, "컷1에 반전/충격 오프닝과 강한 첫 프레임을 배치하세요."),
            ("사운드", sfx, 25, "컷1과 임팩트 지점에 포인트 효과음을 추가하세요."),
            ("키워드", keyword, 25, "프롬프트에 바이럴 시각 키워드(마법·반전·리액션)를 더하세요."),
            ("템포", tempo, 20, "나레이션 WPM을 130~190 이상대로 맞추세요."),
        }.OrderBy(x => x.Val / (double)x.Max).First();
        return $"우선 개선: [{worst.Name}] {worst.Tip}";
    }
}
