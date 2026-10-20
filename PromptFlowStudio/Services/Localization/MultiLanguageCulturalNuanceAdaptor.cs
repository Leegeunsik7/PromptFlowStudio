using System.Text;

namespace PromptFlowStudio.Services.Localization;

public enum NuanceKind : byte { Idiom, Slang, Humor, Taboo, CulturalContext, Honorific, VisualTaboo, Register }

public enum NuanceAction : byte { Replaced, Softened, Annotated, Flagged, Kept }

public sealed record NuanceFinding(NuanceKind Kind, NuanceAction Action, string Trigger, string? Literal, string? Adapted, string Note);

public sealed record SubtitleCue(int Index, double StartMs, double EndMs, string Line1, string? Line2, float Cps, bool CpsOverflow);

public sealed record AdaptationResult(DubLanguage Target, string Source, string Literal, string Adapted, IReadOnlyList<NuanceFinding> Findings,
    IReadOnlyList<SubtitleCue> Subtitles, float NuanceScore, bool FromMemory, string Register);

/// <summary>외부 기계 번역 결선점(자체 호스팅 NMT 등) — 미결선 시 번역 메모리(TM) · 미등록 문장은 원문 + 표식.</summary>
public interface IMachineTranslationBackend
{
    string Name { get; }
    bool TryTranslate(string text, DubLanguage from, DubLanguage to, out string translated);
}

/// <summary>
/// [PromptFlow Studio v16.69.0] 🌏 다국어 문화적 뉘앙스 &amp; 문맥 적응기 — 원문(한국어) 트리거 감지 → 대상 언어 직역 구간을 문화 등가 표현으로 재구성.
/// 규칙: 관용구 · 은어 · 유머/완곡어 · 금기(숫자 4 · 주류) · 문화 맥락 주석 · 호칭/경어 · 화체(반말/존댓말) · 시각 요소 금기(제스처/소품).
/// 번역: 외부 NMT 결선 → 번역 메모리(데모 대본 직역) → 미등록(원문 표식 · 규칙은 '검토 필요'로 표기).
/// 자막: 언어별 줄 길이(라틴 42 · CJK 16) · 읽기 속도(CPS: 라틴 17 · ko 12 · zh 9 · ja 4) 기준 큐 분할/오버플로 표기.
/// </summary>
public sealed class MultiLanguageCulturalNuanceAdaptor
{
    private sealed record Rule(NuanceKind Kind, NuanceAction Action, string Trigger, string Note, string?[] Literal, string?[] Adapted);
    private sealed record VisualRule(string Tag, NuanceKind Kind, DubLanguage[] Affected, string Note);

    public static readonly DubLanguage[] Targets = { DubLanguage.En, DubLanguage.Ja, DubLanguage.Zh, DubLanguage.Es, DubLanguage.Fr, DubLanguage.De, DubLanguage.Ar };

    /// <summary>데모 대본(원문 한국어) + 시각 태그.</summary>
    public static readonly (string Text, string[] Visual)[] DemoScript =
    {
        ("형, 이번 작전은 식은 죽 먹기야. 대박 날 거야!", new[] { "gesture:thumbs_up" }),
        ("4층 사무실에서 소주 한잔하면서 계획을 짜자.", new[] { "prop:soju_bottle" }),
        ("빨간 펜으로 이름 쓰지 마, 재수 없어.", new[] { "prop:red_ink_name" }),
        ("라면 먹고 갈래?", new[] { "gesture:v_sign_palm_in" }),
    };

    // 언어 인덱스: Ko, En, Ja, Zh, Es, Fr, De, Ar
    private static readonly Dictionary<string, string?[]> Memory = new(StringComparer.Ordinal)
    {
        [DemoScript[0].Text] = new string?[]
        {
            null,
            "Bro, this operation is eating cold porridge. It's going to be daebak!",
            "兄貴、今回の作戦は冷めたお粥を食べることだ。テバクになるぞ！",
            "哥，这次行动就是吃冷粥。会大发的！",
            "Hermano, esta operación es comer gachas frías. ¡Va a ser daebak!",
            "Frérot, cette opération, c'est manger de la bouillie froide. Ça va être daebak !",
            "Bruder, diese Operation ist kalten Brei essen. Das wird daebak!",
            "يا أخي، هذه العملية أكل عصيدة باردة. ستكون ديباك!",
        },
        [DemoScript[1].Text] = new string?[]
        {
            null,
            "Let's make a plan over a glass of soju in the 4th-floor office.",
            "4階の事務所で焼酎を一杯やりながら計画を立てよう。",
            "我们在4楼办公室边喝烧酒边做计划吧。",
            "Hagamos un plan tomando un soju en la oficina del 4º piso.",
            "Faisons un plan autour d'un verre de soju au bureau du 4e étage.",
            "Lass uns bei einem Glas Soju im Büro im 4. Stock einen Plan machen.",
            "لنضع خطة ونحن نشرب السوجو في مكتب الطابق الرابع.",
        },
        [DemoScript[2].Text] = new string?[]
        {
            null,
            "Don't write names in red pen, it's unlucky.",
            "赤ペンで名前を書くな、縁起が悪い。",
            "别用红笔写名字，不吉利。",
            "No escribas nombres con bolígrafo rojo, da mala suerte.",
            "N'écris pas de noms au stylo rouge, ça porte malheur.",
            "Schreib keine Namen mit rotem Stift, das bringt Unglück.",
            "لا تكتب الأسماء بالقلم الأحمر، هذا نذير شؤم.",
        },
        [DemoScript[3].Text] = new string?[]
        {
            null,
            "Do you want to eat ramen before you go?",
            "ラーメン食べていく？",
            "要不要吃了拉面再走？",
            "¿Quieres comer ramen antes de irte?",
            "Tu veux manger des ramen avant de partir ?",
            "Willst du noch Ramen essen, bevor du gehst?",
            "هل تريد أن تأكل الرامن قبل أن تذهب؟",
        },
    };

    private static readonly Rule[] Rules =
    {
        new(NuanceKind.Idiom, NuanceAction.Replaced, "식은 죽 먹기", "관용구 직역 시 의미 상실 → 문화 등가 관용구",
            new string?[] { null, "eating cold porridge", "冷めたお粥を食べることだ", "吃冷粥", "comer gachas frías", "manger de la bouillie froide", "kalten Brei essen", "أكل عصيدة باردة" },
            new string?[] { null, "a piece of cake", "朝飯前だ", "小菜一碟", "pan comido", "du gâteau", "ein Kinderspiel", "سهلة جدًا" }),
        new(NuanceKind.Slang, NuanceAction.Replaced, "대박", "한국 은어(음차 불통) → 현지 구어",
            new string?[] { null, "daebak", "テバク", null, "daebak", "daebak", "daebak", "ديباك" },
            new string?[] { null, "a huge hit", "大当たり", null, "un exitazo", "un carton", "der Hammer", "نجاحًا كبيرًا" }),
        new(NuanceKind.Taboo, NuanceAction.Softened, "4층", "숫자 4(死) 동음 금기 — 층수 비특정화",
            new string?[] { null, null, "4階", "4楼", null, null, null, null },
            new string?[] { null, null, "上の階", "楼上", null, null, null, null }),
        new(NuanceKind.Taboo, NuanceAction.Softened, "소주", "주류 묘사 — 보수 문화권 비알코올 대체",
            new string?[] { null, null, null, null, null, null, null, "نشرب السوجو" },
            new string?[] { null, null, null, null, null, null, null, "نشرب الشاي" }),
        new(NuanceKind.CulturalContext, NuanceAction.Annotated, "빨간 펜으로 이름", "붉은 글씨 이름 = 죽음(한·중 공유) — 비공유 문화권 맥락 보강",
            new string?[] { null, "it's unlucky", null, null, "da mala suerte", "ça porte malheur", "das bringt Unglück", "هذا نذير شؤم" },
            new string?[] { null, "back home, that means death", null, null, "en Corea eso significa la muerte", "en Corée, ça veut dire la mort", "in Korea bedeutet das den Tod", "في كوريا يعني ذلك الموت" }),
        new(NuanceKind.Humor, NuanceAction.Replaced, "라면 먹고 갈래", "완곡 유혹 관용(영화 대사 유래) — 현지 등가 완곡어",
            new string?[] { null, "Do you want to eat ramen before you go?", "ラーメン食べていく？", "要不要吃了拉面再走？", "¿Quieres comer ramen antes de irte?", "Tu veux manger des ramen avant de partir ?", "Willst du noch Ramen essen, bevor du gehst?", "هل تريد أن تأكل الرامن قبل أن تذهب؟" },
            new string?[] { null, "Want to come up for a coffee?", "うちでお茶でも飲んでいく？", "要不要上来喝杯茶？", "¿Subes a tomar un café?", "Tu montes boire un verre ?", "Kommst du noch auf einen Kaffee hoch?", "هل تريد أن تشرب الشاي قبل أن تذهب؟" }),
        new(NuanceKind.Honorific, NuanceAction.Kept, "형", "친족 호칭(손위 남성 · 친밀) — 위계/친밀 보존 호칭 선택",
            new string?[] { null, "Bro", "兄貴", "哥", "Hermano", "Frérot", "Bruder", "يا أخي" },
            new string?[] { null, null, null, null, null, null, null, null }),
    };

    private static readonly VisualRule[] VisualRules =
    {
        new("gesture:thumbs_up", NuanceKind.VisualTaboo, new[] { DubLanguage.Ar }, "엄지척 — 일부 중동 지역 모욕 제스처 → 컷 대체/블러 권고"),
        new("prop:soju_bottle", NuanceKind.VisualTaboo, new[] { DubLanguage.Ar }, "주류 소품 노출 — 방송 심의 대상 → 라벨 블러 권고"),
        new("prop:red_ink_name", NuanceKind.CulturalContext, new[] { DubLanguage.En, DubLanguage.Es, DubLanguage.Fr, DubLanguage.De, DubLanguage.Ar }, "붉은 이름 클로즈업 — 화면 주석(온스크린 노트) 권고"),
        new("gesture:v_sign_palm_in", NuanceKind.VisualTaboo, new[] { DubLanguage.En }, "손등 V사인 — 영국/호주 모욕 제스처 → 대체 컷 권고"),
    };

    private readonly object _gate = new();
    private long _adaptations, _replaced, _flagged;

    public event Action? OnChange;

    public IMachineTranslationBackend? Translator { get; set; }
    public long Adaptations { get { lock (_gate) return _adaptations; } }
    public long Replaced { get { lock (_gate) return _replaced; } }
    public long Flagged { get { lock (_gate) return _flagged; } }
    public string? LastAction { get; private set; }

    public static int MaxLineChars(DubLanguage l) => l is DubLanguage.Ja or DubLanguage.Zh or DubLanguage.Ko ? 16 : 42;
    public static float MaxCps(DubLanguage l) => l switch { DubLanguage.Ja => 4, DubLanguage.Zh => 9, DubLanguage.Ko => 12, _ => 17 };

    /// <summary>화체 판정 — 문장 종결 어미(존댓말 -요/-니다/-까 · 반말 -야/-자/-어/-래/-지/-마).</summary>
    public static string DetectRegister(string ko)
    {
        int formal = 0, casual = 0;
        var t = ko.AsSpan().Trim();
        var start = 0;
        for (var i = 0; i <= t.Length; i++)
        {
            if (i < t.Length && t[i] is not ('.' or '!' or '?' or ',')) continue;
            var seg = t[start..i].TrimEnd();
            start = i + 1;
            if (seg.IsEmpty) continue;
            if (seg.EndsWith("요") || seg.EndsWith("니다") || seg.EndsWith("까")) formal++;
            else if (seg[^1] is '야' or '자' or '어' or '래' or '지' or '마' or '아' or '해') casual++;
        }
        return formal > casual ? "존댓말(격식)" : casual > 0 ? "반말(친밀)" : "중립";
    }

    public AdaptationResult Adapt(string source, DubLanguage target, IReadOnlyList<string>? visualTags = null, double durationMs = 3000)
    {
        var ti = (int)target;
        string literal; var fromMemory = false;
        if (Translator is { } mt && mt.TryTranslate(source, DubLanguage.Ko, target, out var tr) && !string.IsNullOrWhiteSpace(tr)) literal = tr;
        else if (Memory.TryGetValue(source.Trim(), out var tm) && tm[ti] is { } hit) { literal = hit; fromMemory = true; }
        else if (target == DubLanguage.Ko) literal = source;
        else literal = "[MT 미결선] " + source;

        var adapted = literal;
        var findings = new List<NuanceFinding>();
        foreach (var r in Rules)
        {
            if (!source.Contains(r.Trigger, StringComparison.Ordinal) || target == DubLanguage.Ko) continue;
            var lit = r.Literal[ti]; var ad = r.Adapted[ti];
            if (r.Action == NuanceAction.Kept)
            {
                if (lit is not null && adapted.Contains(lit, StringComparison.Ordinal)) findings.Add(new NuanceFinding(r.Kind, NuanceAction.Kept, r.Trigger, lit, lit, r.Note));
                continue;
            }
            if (lit is null) { findings.Add(new NuanceFinding(r.Kind, NuanceAction.Kept, r.Trigger, null, null, r.Note + " — 대상 문화권 공유/해당 없음")); continue; }
            if (ad is not null && adapted.Contains(lit, StringComparison.Ordinal))
            {
                adapted = adapted.Replace(lit, ad, StringComparison.Ordinal);
                findings.Add(new NuanceFinding(r.Kind, r.Action, r.Trigger, lit, ad, r.Note));
            }
            else findings.Add(new NuanceFinding(r.Kind, NuanceAction.Flagged, r.Trigger, lit, ad, r.Note + " — 직역 구간 미검출(검토 필요)"));
        }
        var register = DetectRegister(source);
        if (target != DubLanguage.Ko)
            findings.Add(new NuanceFinding(NuanceKind.Register, NuanceAction.Kept, register, null, null, target switch
            {
                DubLanguage.Ja => register.StartsWith("반말", StringComparison.Ordinal) ? "常体(タメ口) 유지" : "敬体(です・ます) 유지",
                DubLanguage.De => register.StartsWith("반말", StringComparison.Ordinal) ? "du-Form 유지" : "Sie-Form 유지",
                DubLanguage.Fr => register.StartsWith("반말", StringComparison.Ordinal) ? "tutoiement 유지" : "vouvoiement 유지",
                DubLanguage.Es => register.StartsWith("반말", StringComparison.Ordinal) ? "tuteo 유지" : "usted 유지",
                _ => "화체 어휘 수준 유지",
            }));
        if (visualTags is not null)
            foreach (var tag in visualTags)
                foreach (var v in VisualRules)
                    if (v.Tag == tag && Array.IndexOf(v.Affected, target) >= 0)
                        findings.Add(new NuanceFinding(v.Kind, NuanceAction.Flagged, tag, null, null, v.Note));
        var subs = Segment(adapted, target, durationMs);
        int resolved = 0, actionable = 0, flagged = 0, replaced = 0;
        foreach (var f in findings)
        {
            if (f.Kind == NuanceKind.Register) continue;
            actionable++;
            if (f.Action is NuanceAction.Replaced or NuanceAction.Softened or NuanceAction.Annotated or NuanceAction.Kept) resolved++;
            if (f.Action is NuanceAction.Replaced or NuanceAction.Softened or NuanceAction.Annotated) replaced++;
            if (f.Action == NuanceAction.Flagged) flagged++;
        }
        var score = actionable == 0 ? 1f : resolved / (float)actionable;
        if (literal.StartsWith("[MT", StringComparison.Ordinal)) score *= 0.5f;
        lock (_gate) { _adaptations++; _replaced += replaced; _flagged += flagged; }
        LastAction = "🌏 " + RealtimeNeuralAudioDubbingEngine.LanguageLabel(target) + " 적응 · 재구성 " + replaced + " · 검토 " + flagged;
        Raise();
        return new AdaptationResult(target, source, literal, adapted, findings, subs, score, fromMemory, register);
    }

    /// <summary>자막 큐 분할 — 절(구두점) 단위 패킹 · 큐당 최대 2줄 · 길이 비례 시간 배분 · CPS 초과 표기.</summary>
    public static List<SubtitleCue> Segment(string text, DubLanguage lang, double durationMs)
    {
        var maxLine = MaxLineChars(lang);
        var cjk = maxLine == 16;
        var clauses = new List<string>();
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            sb.Append(ch);
            if (ch is '.' or '!' or '?' or '。' or '！' or '？' or ',' or '，' or '、' or '،' or '؟')
            {
                var s = sb.ToString().Trim();
                if (s.Length > 0) clauses.Add(s);
                sb.Clear();
            }
        }
        if (sb.ToString().Trim() is { Length: > 0 } rest) clauses.Add(rest);
        // 줄 단위로 채움(라틴은 단어 경계)
        var lines = new List<string>();
        var line = new StringBuilder();
        foreach (var c in clauses)
        {
            var pieces = cjk ? Chunk(c, maxLine) : WordWrap(c, maxLine);
            foreach (var p in pieces)
            {
                var sep = line.Length == 0 || cjk ? "" : " ";
                if (line.Length + sep.Length + p.Length <= maxLine) line.Append(sep).Append(p);
                else { if (line.Length > 0) lines.Add(line.ToString()); line.Clear().Append(p); }
            }
        }
        if (line.Length > 0) lines.Add(line.ToString());
        var total = 0;
        foreach (var l in lines) total += l.Length;
        var cues = new List<SubtitleCue>();
        double t = 0;
        for (var i = 0; i < lines.Count; i += 2)
        {
            var l1 = lines[i]; var l2 = i + 1 < lines.Count ? lines[i + 1] : null;
            var chars = l1.Length + (l2?.Length ?? 0);
            var dur = total > 0 ? durationMs * chars / total : durationMs;
            var cps = (float)(chars / Math.Max(0.001, dur / 1000));
            cues.Add(new SubtitleCue(cues.Count + 1, t, t + dur, l1, l2, cps, cps > MaxCps(lang)));
            t += dur;
        }
        return cues;
    }

    private static List<string> Chunk(string s, int n)
    {
        var r = new List<string>();
        for (var i = 0; i < s.Length; i += n) r.Add(s.Substring(i, Math.Min(n, s.Length - i)));
        return r;
    }

    private static List<string> WordWrap(string s, int n)
    {
        var r = new List<string>();
        var cur = new StringBuilder();
        foreach (var w in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (cur.Length > 0 && cur.Length + 1 + w.Length > n) { r.Add(cur.ToString()); cur.Clear(); }
            if (cur.Length > 0) cur.Append(' ');
            cur.Append(w);
        }
        if (cur.Length > 0) r.Add(cur.ToString());
        return r;
    }

    public static string KindLabel(NuanceKind k) => k switch
    {
        NuanceKind.Idiom => "관용구", NuanceKind.Slang => "은어", NuanceKind.Humor => "유머/완곡", NuanceKind.Taboo => "금기",
        NuanceKind.CulturalContext => "문화 맥락", NuanceKind.Honorific => "호칭", NuanceKind.VisualTaboo => "시각 금기", _ => "화체",
    };

    public static string ActionLabel(NuanceAction a) => a switch
    {
        NuanceAction.Replaced => "재구성", NuanceAction.Softened => "완화", NuanceAction.Annotated => "맥락 보강", NuanceAction.Flagged => "검토", _ => "유지",
    };

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Localization.Nuance.OnChange", ex); }
    }
}
