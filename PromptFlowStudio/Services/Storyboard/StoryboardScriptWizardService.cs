using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PromptFlowStudio.Services.Ui;   // PromptPresets(카메라 프리셋)

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>오토파서가 만든 컷 1개 초안 — 영문 프롬프트 + 한국어 나레이션 + 카메라 모션.</summary>
public sealed record WizardCut(string Prompt, string VoiceText, string CameraMotion);

/// <summary>주제/대본 한 줄을 스토리보드 컷들로 자동 분할·구성하는 계약(순수 BCL).</summary>
public interface IStoryboardScriptWizardService
{
    /// <summary>입력 텍스트를 3~5개 컷으로 분할하고 각 컷의 프롬프트/나레이션/카메라 모션 초안을 생성. 빈 입력은 빈 목록(Graceful).</summary>
    IReadOnlyList<WizardCut> Generate(string input, int maxCuts = 5);
}

/// <summary>
/// [PromptFlowStudio] 원클릭 스크립트→스토리보드 오토파서 — 외부 NuGet 0(정규식 + 문자열 규칙만).
///   ▸ 문장 부호(. ! ? 。 …)·줄바꿈으로 문장을 나눠, 문장이 충분하면 <b>대본 모드</b>(문장=컷), 부족하면 <b>주제 모드</b>(스토리 아크 5컷)로 구성.
///   ▸ 컷마다: 영문 프롬프트 초안(Ko→En 키워드 매핑 + 위치별 시네마틱 앵글) · 한국어 나레이션(문장 또는 아크 템플릿) · 카메라 모션(<see cref="PromptPresets.Camera"/> 프리셋 매핑).
///   ▸ 형태소 분석기 없이 휴리스틱이라 완벽하진 않다 — 결과는 '초안'(사용자가 컷 편집기에서 다듬는다). 어떤 입력이든 예외 없이 목록 반환.
/// </summary>
public sealed class StoryboardScriptWizardService : IStoryboardScriptWizardService
{
    // 대표 주제 명사 Ko→En(부분일치 스캔). 없으면 원문 키워드 유지.
    private static readonly Dictionary<string, string> Ko2En = new(StringComparer.Ordinal)
    {
        ["고양이"] = "a cute cat", ["강아지"] = "a puppy dog", ["우주"] = "outer space with stars and galaxies",
        ["바다"] = "the deep blue ocean", ["해변"] = "a sunny beach", ["하늘"] = "the vast sky", ["별"] = "shining stars",
        ["숲"] = "a lush forest", ["도시"] = "a modern city", ["산"] = "majestic mountains", ["강"] = "a flowing river",
        ["로봇"] = "a futuristic robot", ["소녀"] = "a young girl", ["소년"] = "a young boy", ["여자"] = "a woman", ["남자"] = "a man",
        ["꽃"] = "blooming flowers", ["비"] = "falling rain", ["눈"] = "falling snow", ["불"] = "burning fire",
        ["자동차"] = "a sleek car", ["기차"] = "a train", ["비행기"] = "an airplane", ["배"] = "a sailing ship",
        ["용"] = "a mighty dragon", ["공룡"] = "a dinosaur", ["새"] = "a flying bird", ["나비"] = "a butterfly",
        ["커피"] = "a cup of coffee", ["음식"] = "delicious food", ["요리"] = "cooking dishes",
        ["여행"] = "a journey", ["모험"] = "an epic adventure", ["꿈"] = "a dream", ["사랑"] = "a love story",
        ["아이"] = "a child", ["할머니"] = "an old grandmother", ["친구"] = "close friends", ["마법"] = "magic spells",
    };

    // 컷 위치별 시네마틱 앵글/스타일(영문).
    private static readonly string[] Angles =
    {
        "cinematic wide establishing shot, soft natural light, film grain",
        "medium tracking shot, shallow depth of field, warm cinematic tones",
        "dynamic low angle shot, dramatic lighting, highly detailed",
        "emotional close-up, bokeh background, golden hour glow",
        "sweeping aerial view, epic scale, volumetric light",
    };

    // 카메라 모션은 /motion 프리셋(PromptPresets.Camera)에서 아크 순서로 매핑.
    private static readonly int[] CamArc = { 5, 4, 7, 2, 3 };   // 드론부감→트래킹→오빗→돌리줌→크레인업

    private static readonly HashSet<string> StopKo = new(StringComparer.Ordinal)
    {
        "그리고","하지만","그러나","이야기","우리","자신","정말","진짜","위해","대한","그것","이것",
        "하는","있는","되는","같은","많은","모든","다시","그냥","이런","저런","어느","매일","마침내",
    };

    private static readonly Regex KoRx = new(@"[가-힣]{2,}", RegexOptions.Compiled);

    /// <inheritdoc/>
    public IReadOnlyList<WizardCut> Generate(string input, int maxCuts = 5)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0) return Array.Empty<WizardCut>();
        maxCuts = Math.Clamp(maxCuts, 3, 5);

        var sentences = SplitSentences(text);
        var keyword = CleanKeyword(MainToken(text));
        var subject = EnglishSubject(text, keyword);

        var cuts = new List<WizardCut>();

        if (sentences.Count >= 3)
        {
            // ── 대본 모드: 문장을 컷으로(넘치면 균등 병합) ──
            var groups = DistributeSentences(sentences, maxCuts);
            for (var i = 0; i < groups.Count; i++)
            {
                var line = groups[i];
                var subj = EnglishSubject(line, CleanKeyword(MainToken(line)));
                cuts.Add(new WizardCut($"{subj}, {Angles[i % Angles.Length]}", line, CameraFor(i)));
            }
        }
        else
        {
            // ── 주제 모드: 스토리 아크 5컷 자동 구성 ──
            var narr = ArcNarrations(text, keyword);
            for (var i = 0; i < maxCuts; i++)
                cuts.Add(new WizardCut($"{subject}, {Angles[i % Angles.Length]}", narr[i % narr.Count], CameraFor(i)));
        }

        return cuts;
    }

    // ── 문장 분리(부호/줄바꿈) ──
    private static List<string> SplitSentences(string text)
    {
        var parts = Regex.Split(text, @"(?<=[.!?。…])\s+|\r?\n+");
        return parts.Select(p => p.Trim().TrimEnd('.', '。', '…', ' '))
                    .Where(p => p.Length >= 2)
                    .ToList();
    }

    // 문장 수가 maxCuts 보다 많으면 인접 문장을 균등 병합해 정확히 maxCuts 그룹으로.
    private static List<string> DistributeSentences(List<string> sentences, int maxCuts)
    {
        if (sentences.Count <= maxCuts) return sentences;
        var groups = new List<string>();
        double per = (double)sentences.Count / maxCuts;
        for (var g = 0; g < maxCuts; g++)
        {
            var start = (int)Math.Round(g * per);
            var end = (int)Math.Round((g + 1) * per);
            end = Math.Min(end, sentences.Count);
            if (start >= end) start = Math.Min(g, sentences.Count - 1);
            groups.Add(string.Join(" ", sentences.GetRange(start, Math.Max(1, end - start))));
        }
        return groups;
    }

    // ── 영문 주어구: 명사 '토큰' 단위로 Ko→En 매핑(부분일치 오매칭 방지: '배우는'의 '배'≠ship) ──
    private static string EnglishSubject(string text, string keyword)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in KoRx.Matches(text))
        {
            var tok = CleanKeyword(m.Value);                    // 조사 제거 + 알려진 명사 접두 정규화
            if (tok.Length >= 2 && Ko2En.TryGetValue(tok, out var en) && seen.Add(en))
            {
                found.Add(en);
                if (found.Count >= 2) break;                    // 주어구는 최대 2개 결합
            }
        }
        if (found.Count > 0) return string.Join(", ", found);
        if (keyword.Length > 0 && Ko2En.TryGetValue(keyword, out var kEn)) return kEn;
        if (keyword.Length > 0) return keyword;                 // 매핑 없으면 원문 키워드(Wan 다국어 처리)
        return "a cinematic scene";
    }

    // ── 핵심 한글 토큰(빈도·길이 우선) ──
    private static string MainToken(string text)
    {
        var freq = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in KoRx.Matches(text))
        {
            var w = m.Value;
            if (w.Length >= 2 && !StopKo.Contains(w)) freq[w] = freq.TryGetValue(w, out var c) ? c + 1 : 1;
        }
        return freq.Count == 0 ? ""
            : freq.OrderByDescending(k => k.Value).ThenByDescending(k => k.Key.Length).First().Key;
    }

    // 알려진 명사면 그대로, 아니면 끝 1글자 조사 제거(길이 3+ · 명사 오절단 최소화).
    private static string CleanKeyword(string t)
    {
        if (t.Length == 0) return "";
        foreach (var k in Ko2En.Keys)
            if (t.StartsWith(k, StringComparison.Ordinal) && t.Length - k.Length <= 1) return k;
        if (t.Length >= 3 && "은는이가을를에의로과와도".Contains(t[^1])) return t[..^1];
        return t;
    }

    // ── 주제 모드 한국어 나레이션 아크(핵심 키워드 K, 조사 자동 선택) ──
    private static List<string> ArcNarrations(string topic, string k)
    {
        var t = topic.TrimEnd('.', '。', '…', ' ');
        var has = k.Length > 0;
        return new List<string>
        {
            $"{t}.",
            has ? $"매일 {k}{P(k, "을", "를")} 꿈꾸던 순간이 있었죠." : "매일 꿈꾸던 순간이 있었죠.",
            has ? $"드디어 {k}{P(k, "이", "가")} 첫 걸음을 내딛습니다." : "드디어 첫 걸음을 내딛습니다.",
            "예상치 못한 순간, 모든 것이 특별해집니다.",
            has ? $"마침내 {k}{P(k, "은", "는")} 꿈꾸던 곳에 도착합니다." : "마침내 꿈꾸던 곳에 도착합니다.",
        };
    }

    // 받침 유무로 조사 선택(한글 아니면 받침 없음 취급).
    private static string P(string word, string withBatchim, string without)
    {
        if (string.IsNullOrEmpty(word)) return without;
        var last = word[^1];
        if (last < 0xAC00 || last > 0xD7A3) return without;
        return (last - 0xAC00) % 28 != 0 ? withBatchim : without;
    }

    private static string CameraFor(int i) => PromptPresets.Camera[CamArc[i % CamArc.Length]].Snippet;
}
