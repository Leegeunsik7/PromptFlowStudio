using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>추천 결과 — 테마 + 연관성 점수(0~1) + 표시 라벨(%).</summary>
public sealed record ThemeRecommendation(BabyThemeDef Theme, double Score, string ScoreLabel);

/// <summary>
/// [PromptFlowStudio] 문맥 분석 기반 AI 프리셋 스마트 추천 엔진(v3.2.0) — 순수 C# BCL TF-IDF 코사인 유사도(외부 ML 라이브러리 0).
///   ▸ 147종 백과사전(아기 102 + 기업 12 + 자동차 33)을 문서 코퍼스로 색인 — 각 테마의 제목/훅/예시/키/비트를 가중 토큰화.
///   ▸ 한국어 대응: 단어 토큰 + 2-gram(글자 바이그램)로 "백일"↔"백일잔치", "PODS 소음" 부분 매칭. IDF 로 흔한 토큰 가중치 하향.
///   ▸ 사용자 대본/키워드 입력 → 질의 TF-IDF 벡터와 각 테마 벡터의 <b>코사인 유사도</b> 계산 → 상위 N 추천(<see cref="Recommend"/>). 오프라인·결정론적.
/// </summary>
public sealed class SmartThemeRecommender
{
    private readonly BabyThemeDatabase _baby;
    private readonly CorporateThemeDatabase _corp;
    private readonly AutomotiveEquipmentDatabase _auto;

    public SmartThemeRecommender(BabyThemeDatabase baby, CorporateThemeDatabase corp, AutomotiveEquipmentDatabase auto)
    { _baby = baby; _corp = corp; _auto = auto; }

    private List<Doc>? _docs;
    private Dictionary<string, double>? _idf;

    private sealed class Doc { public BabyThemeDef Theme = default!; public Dictionary<string, double> Vec = new(); }

    /// <summary>색인 대상 전체 테마(147종).</summary>
    public IReadOnlyList<BabyThemeDef> AllThemes => _baby.AllThemes.Concat(_corp.AllThemes).Concat(_auto.AllThemes).ToList();

    /// <summary>질의(대본/키워드)로 상위 N개 테마를 추천한다(점수 내림차순 · 0점 제외).</summary>
    public IReadOnlyList<ThemeRecommendation> Recommend(string? query, int topN = 3)
    {
        EnsureIndex();
        var qtf = TermFreq(Terms(query));
        if (qtf.Count == 0) return Array.Empty<ThemeRecommendation>();
        var qvec = ToTfIdf(qtf);
        var qnorm = Norm(qvec);
        if (qnorm <= 0) return Array.Empty<ThemeRecommendation>();

        var scored = new List<ThemeRecommendation>();
        foreach (var d in _docs!)
        {
            var sim = Cosine(qvec, qnorm, d.Vec);
            if (sim > 0.0001) scored.Add(new ThemeRecommendation(d.Theme, sim, $"{Math.Round(sim * 100)}%"));
        }
        return scored.OrderByDescending(r => r.Score).Take(Math.Max(1, topN)).ToList();
    }

    // ── 색인(1회 캐시) ──
    private void EnsureIndex()
    {
        if (_docs is not null) return;
        var themes = AllThemes;
        var rawDocs = themes.Select(t => new { Theme = t, Tf = TermFreq(DocTerms(t)) }).ToList();

        // IDF = log(N / (1+df)).
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var d in rawDocs)
            foreach (var term in d.Tf.Keys)
                df[term] = df.GetValueOrDefault(term) + 1;
        var n = Math.Max(1, rawDocs.Count);
        _idf = df.ToDictionary(kv => kv.Key, kv => Math.Log(n / (1.0 + kv.Value)) + 1.0, StringComparer.Ordinal);

        _docs = rawDocs.Select(d => new Doc { Theme = d.Theme, Vec = ToTfIdf(d.Tf) }).ToList();
    }

    private Dictionary<string, double> ToTfIdf(Dictionary<string, double> tf)
    {
        var v = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (term, f) in tf)
            if (_idf!.TryGetValue(term, out var idf)) v[term] = f * idf;
            else v[term] = f * 1.0;   // 코퍼스 미등장 질의 토큰(약가중)
        return v;
    }

    private static double Cosine(Dictionary<string, double> q, double qnorm, Dictionary<string, double> d)
    {
        double dot = 0;
        // 질의 벡터가 짧으므로 질의 기준 순회.
        foreach (var (term, qv) in q)
            if (d.TryGetValue(term, out var dv)) dot += qv * dv;
        var dnorm = Norm(d);
        return dnorm <= 0 ? 0 : dot / (qnorm * dnorm);
    }

    private static double Norm(Dictionary<string, double> v) => Math.Sqrt(v.Values.Sum(x => x * x));

    private static Dictionary<string, double> TermFreq(IEnumerable<string> terms)
    {
        var tf = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var t in terms) tf[t] = tf.GetValueOrDefault(t) + 1;
        return tf;
    }

    // 테마 문서 토큰(가중: 제목×3 · 훅×2 · 키×2 · 예시×1 · 비트×1).
    private static IEnumerable<string> DocTerms(BabyThemeDef t)
    {
        var terms = new List<string>();
        for (var i = 0; i < 3; i++) terms.AddRange(Terms(t.Title));
        for (var i = 0; i < 2; i++) terms.AddRange(Terms(t.Hook));
        for (var i = 0; i < 2; i++) terms.AddRange(Terms(t.Key));
        terms.AddRange(Terms(t.Example));
        if (t.Beats is not null) foreach (var b in t.Beats) { terms.AddRange(Terms(b.V)); terms.AddRange(Terms(b.G)); terms.AddRange(Terms(b.Shot)); }
        return terms;
    }

    /// <summary>텍스트 → 토큰(소문자 단어 + 길이≥2 단어의 글자 2-gram). 한국어 부분 매칭 지원.</summary>
    public static IEnumerable<string> Terms(string? text)
    {
        var s = (text ?? "").ToLowerInvariant();
        var word = new StringBuilder();
        var outp = new List<string>();
        void Flush()
        {
            if (word.Length == 0) return;
            var w = word.ToString();
            outp.Add(w);
            if (w.Length >= 2)
                for (var i = 0; i < w.Length - 1; i++) outp.Add(w.Substring(i, 2));   // 2-gram
            word.Clear();
        }
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch)) word.Append(ch);
            else Flush();
        }
        Flush();
        return outp;
    }
}
