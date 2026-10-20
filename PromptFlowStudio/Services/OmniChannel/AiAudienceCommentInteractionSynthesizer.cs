namespace PromptFlowStudio.Services.OmniChannel;

public enum CommentIntent : byte { Neutral, Positive, Question, Rebuttal, Spam }
public enum CommentLang : byte { Ko, En, Ja, Es }
public enum ReplyState : byte { None, Suggested, Sent, Manual, Skipped, BlockedSimilar, Waiting }

/// <summary>댓글 링 슬롯(값 형식) — 본문/답글 문자는 합성기 소유 고정 영역.</summary>
public struct OmniComment
{
    public OmniPlatform Platform;
    public ulong Id, AuthorHash;
    public CommentIntent Intent;
    public CommentLang Lang;
    public float Confidence;
    public double At;
    public int TextLen, ReplyLen;
    public ReplyState Reply;
    public double ClassifyUs;
}

/// <summary>
/// [PromptFlow Studio v17.51.0] 💬 AI 시청자 댓글 감성 분석 &amp; 인터랙션 합성기(<see cref="Ingest"/> · <see cref="Classify"/> · <see cref="SynthesizeReply"/> · <see cref="WritePinnedHook"/> 무할당).
/// <b>분류</b>: 스팸 규칙(URL · 홍보 · 팔로우 유도 · 6 연속 반복 문자 · @ 난사) 우선 → 의문(?, ？, ¿ · 4 개 언어 의문사) → 반박/긍정 어휘 가중 합(이모지 포함).
/// 언어는 문자 체계(한글 · 가나/한자 · 스페인어 표지 ñ¿¡ 악센트)로 판별. 128 슬롯 링 · 본문 280 자 상한.
/// <b>합성</b>: 의도 × 언어 × 6 변형 템플릿에 질문 핵심 구절 인용 · v17.40 내러티브 긴장도(장면 번호 · 분위기 어휘)를 삽입하고, 가드가 최근 답글과의
/// 레벤슈타인 유사도 ≤ 0.40 을 만족하는 첫 변형을 고른다. 스팸에는 답하지 않으며 반박에는 논쟁 대신 감사/설명형만 제안한다.
/// <b>고정 질문 훅</b>: 게시 3 분 내 참여 유도 질문(카테고리 · 언어)을 작성 — 고정(pin)은 플랫폼 API 미지원이라 수동.
/// AI 서명(기본 ON)으로 자동 작성 사실을 표시한다.
/// </summary>
public sealed class AiAudienceCommentInteractionSynthesizer
{
    public const int Cap = 128, TextCap = 280, Variants = 6;
    public const string Sig = " · 🤖AI";
    private readonly OmniComment[] _ring = new OmniComment[Cap];
    private readonly char[] _text = new char[Cap * TextCap], _reply = new char[Cap * TextCap];
    private int _head, _count;
    private static readonly string[] Pos = { "love", "great", "amazing", "awesome", "nice", "cool", "best", "wow", "🔥", "😍", "❤", "👏", "😂", "대박", "최고", "좋아", "멋지", "굿", "짱", "레전드", "すごい", "最高", "好き", "かわいい", "神", "increíble", "genial", "me encanta", "hermoso", "buenísimo" };
    private static readonly string[] Neg = { "fake", "wrong", "not true", "clickbait", "boring", "worst", "stolen", "가짜", "거짓", "별로", "아닌데", "틀렸", "노잼", "구라", "嘘", "違う", "つまらない", "やらせ", "falso", "mentira", "aburrido", "no es cierto" };
    private static readonly string[] Ask = { "how ", "what ", "why ", "where ", "when ", "which ", "who ", "can you", "어떻게", "뭐", "왜", "어디", "언제", "무슨", "나요", "까요", "인가요", "どう", "なに", "何", "なぜ", "どこ", "いつ", "ですか", "cómo", "qué", "por qué", "dónde", "cuándo" };
    private static readonly string[] SpamKw = { "http", "www.", ".com", "follow me", "check my", "dm me", "sub4sub", "free money", "giveaway", "promo code", "팔로우", "맞팔", "구독해", "홍보", "무료 증정", "フォロー", "sígueme" };
    private static readonly string[][,] Templates = BuildTemplates();
    private static readonly string[] Moods = { "잔잔한", "calm", "静かな", "tranquila", "긴장감 넘치는", "tense", "緊張の", "tensa", "클라이맥스", "climax", "クライマックス", "clímax" };

    public bool AiSignature { get; set; } = true;
    public int Count => _count;
    public long Classified { get; private set; }
    public double MaxClassifyUs { get; private set; }
    public long Spam { get; private set; }
    public ref readonly OmniComment Comment(int i) => ref _ring[Slot(i)];
    public ReadOnlySpan<char> Text(int i) { var s = Slot(i); return _text.AsSpan(s * TextCap, _ring[s].TextLen); }
    public ReadOnlySpan<char> ReplyText(int i) { var s = Slot(i); return _reply.AsSpan(s * TextCap, _ring[s].ReplyLen); }
    /// <summary>i = 0 이 최신.</summary>
    public int Slot(int i) => (_head - 1 - i + 2 * Cap) % Cap;
    public void SetReplyState(int i, ReplyState s) => _ring[Slot(i)].Reply = s;
    public static string IntentName(CommentIntent c) => c switch { CommentIntent.Positive => "긍정", CommentIntent.Question => "호기심", CommentIntent.Rebuttal => "반박", CommentIntent.Spam => "스팸", _ => "중립" };
    public static string LangName(CommentLang l) => l switch { CommentLang.Ko => "KO", CommentLang.Ja => "JA", CommentLang.Es => "ES", _ => "EN" };

    // ══════════════════════ 수집 · 분류(무할당) ══════════════════════
    /// <summary>댓글 적재 + 즉시 분류 — 중복 ID 무시(−1), 성공 시 물리 슬롯(원본 ID 등 부가 데이터 병렬 저장용).</summary>
    public int Ingest(OmniPlatform p, ulong id, ulong authorHash, ReadOnlySpan<char> text, double at)
    {
        for (var k = 0; k < _count; k++) { ref readonly var c = ref _ring[Slot(k)]; if (c.Id == id && c.Platform == p) return -1; }
        ref var e = ref _ring[_head];
        e = default;
        e.Platform = p; e.Id = id; e.AuthorHash = authorHash; e.At = at;
        var n = Math.Min(text.Length, TextCap);
        if (n > 0 && n < text.Length && char.IsHighSurrogate(text[n - 1])) n--;
        text[..n].CopyTo(_text.AsSpan(_head * TextCap));
        e.TextLen = n;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        e.Intent = Classify(_text.AsSpan(_head * TextCap, n), out e.Lang, out e.Confidence);
        e.ClassifyUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        if (e.ClassifyUs > MaxClassifyUs) MaxClassifyUs = e.ClassifyUs;
        if (e.Intent == CommentIntent.Spam) Spam++;
        Classified++;
        var slot = _head;
        _head = (_head + 1) % Cap;
        if (_count < Cap) _count++;
        return slot;
    }

    public static CommentLang DetectLang(ReadOnlySpan<char> s)
    {
        int ko = 0, ja = 0, es = 0;
        foreach (var c in s)
        {
            if (c is >= '가' and <= '힣' or >= 'ㄱ' and <= 'ㆎ') ko++;
            else if (c is >= '぀' and <= 'ヿ' or >= '一' and <= '鿿') ja++;
            else if (c is 'ñ' or 'Ñ' or '¿' or '¡' or 'á' or 'é' or 'í' or 'ó' or 'ú') es += 2;
        }
        if (ko > 0 && ko >= ja) return CommentLang.Ko;
        if (ja > 0) return CommentLang.Ja;
        if (es > 0 || s.IndexOf(" que ", StringComparison.OrdinalIgnoreCase) >= 0 || s.IndexOf(" muy ", StringComparison.OrdinalIgnoreCase) >= 0) return CommentLang.Es;
        return CommentLang.En;
    }

    private static int Hits(ReadOnlySpan<char> s, string[] words)
    {
        var h = 0;
        foreach (var w in words) if (s.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) h++;
        return h;
    }

    /// <summary>의도 분류(무할당 · 서브 밀리초).</summary>
    public static CommentIntent Classify(ReadOnlySpan<char> s, out CommentLang lang, out float confidence)
    {
        lang = DetectLang(s);
        confidence = 0.5f;
        var spam = Hits(s, SpamKw);
        int run = 1, maxRun = 1, at = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '@') at++;
            if (i > 0 && s[i] == s[i - 1] && !char.IsWhiteSpace(s[i])) { if (++run > maxRun) maxRun = run; } else run = 1;
        }
        if (maxRun >= 6) spam++;
        if (at >= 3) spam++;
        if (spam > 0) { confidence = Math.Min(0.6f + 0.2f * spam, 0.99f); return CommentIntent.Spam; }
        var q = s.IndexOfAny('?', '？', '¿') >= 0 ? 2 : 0;
        q += Hits(s, Ask);
        var pos = Hits(s, Pos); var neg = Hits(s, Neg);
        if (neg > pos && neg > 0) { confidence = Math.Min(0.55f + 0.15f * (neg - pos), 0.97f); return CommentIntent.Rebuttal; }
        if (q >= 2 || q >= 1 && pos == 0) { confidence = Math.Min(0.55f + 0.12f * q, 0.97f); return CommentIntent.Question; }
        if (pos > 0) { confidence = Math.Min(0.55f + 0.15f * pos, 0.97f); return CommentIntent.Positive; }
        return CommentIntent.Neutral;
    }

    // ══════════════════════ 답글 · 고정 훅(무할당) ══════════════════════
    private static string[][,] BuildTemplates()
    {
        // [의도(0 긍정 · 1 의문 · 2 반박 · 3 중립)][언어, 변형] — {Q} = 질문 인용 · {S} = 장면 · {M} = 분위기
        return new[]
        {
            new[,]
            {
                { "고마워요! 다음 편은 {M} 장면 {S}부터 더 세게 갑니다 🔥", "와 이 반응 실화인가요 😆 장면 {S} 비하인드도 곧 풀게요", "최고의 칭찬이에요 🙌 어떤 컷이 제일 좋았는지 알려주세요!", "덕분에 힘나요! {M} 분위기 살리느라 밤샜던 보람이 있네요", "감동… 이 댓글 저장해둡니다 📌 다음 영상도 기대해 주세요", "오늘 본 댓글 중 최고예요 😍 친구한테도 공유해 주실래요?" },
                { "Thank you! Next one hits even harder from scene {S} 🔥", "This made my day 😆 Behind-the-scenes of scene {S} coming soon", "Best compliment ever 🙌 Which shot was your favorite?", "Means a lot! Worth the late nights on that {M} vibe", "Saving this comment 📌 Stay tuned for the next drop", "Top comment of the day 😍 Mind sharing it with a friend?" },
                { "ありがとう！次はシーン{S}からもっと攻めます🔥", "この反応うれしすぎる😆 シーン{S}の裏話も近日公開", "最高の褒め言葉です🙌 どのカットが好きでした？", "励みになります！{M}雰囲気づくり頑張った甲斐がありました", "このコメント保存しました📌 次回もお楽しみに", "今日イチのコメント😍 友達にもシェアしてね" },
                { "¡Gracias! El próximo pega más fuerte desde la escena {S} 🔥", "Me alegraste el día 😆 Pronto el detrás de cámaras de la escena {S}", "El mejor cumplido 🙌 ¿Qué toma te gustó más?", "¡Significa mucho! Valió la pena ese ambiente {M}", "Guardo este comentario 📌 Atentos al próximo", "El mejor comentario de hoy 😍 ¿Lo compartes con un amigo?" },
            },
            new[,]
            {
                { "좋은 질문! “{Q}” — 장면 {S}에서 실마리가 나와요 👀 끝까지 보면 보여요", "“{Q}” 궁금하셨죠? 다음 편 {M} 장면에서 제대로 답할게요", "오 날카롭다 🔍 “{Q}” 힌트: 3초 지점을 다시 보세요!", "“{Q}” 이건 고정 댓글에 정리해 둘게요 📌", "정답에 가까워요 😏 “{Q}” 다른 분들 생각은 어때요?", "“{Q}” — 비하인드 영상에서 풀 예정! 알림 켜두세요 🔔" },
                { "Great question! “{Q}” — the clue is in scene {S} 👀", "Wondering “{Q}”? The next {M} scene answers it properly", "Sharp eye 🔍 “{Q}” hint: rewatch the 3-second mark!", "“{Q}” — I'll break it down in the pinned comment 📌", "You're close 😏 “{Q}” what do others think?", "“{Q}” — answering it in the behind-the-scenes, turn on alerts 🔔" },
                { "いい質問！「{Q}」— ヒントはシーン{S}に👀", "「{Q}」気になりますよね？次の{M}シーンで答えます", "鋭い🔍「{Q}」ヒント：3秒地点をもう一度！", "「{Q}」は固定コメントでまとめます📌", "惜しい😏「{Q}」みんなはどう思う？", "「{Q}」はメイキングで解説予定！通知オンで🔔" },
                { "¡Buena pregunta! «{Q}» — la pista está en la escena {S} 👀", "¿Te preguntas «{Q}»? La próxima escena {M} lo responde", "Buen ojo 🔍 «{Q}» pista: mira de nuevo el segundo 3", "«{Q}» lo explico en el comentario fijado 📌", "Casi 😏 «{Q}» ¿qué opinan los demás?", "«{Q}» lo cuento en el detrás de cámaras, activa la campanita 🔔" },
            },
            new[,]
            {
                { "의견 고마워요 🙏 어떤 부분이 아쉬웠는지 알려주시면 반영할게요", "솔직한 피드백 감사해요. 원작 크레딧과 제작 과정은 설명란에 있어요", "그렇게 보일 수도 있겠네요! 장면 {S} 제작 방식은 다음 영상에서 공개할게요", "피드백 환영해요 😊 더 나은 버전으로 돌아올게요", "좋은 지적 감사합니다. 확인해서 보완할게요 🛠", "의견 존중해요! 어떤 스타일을 더 보고 싶은지도 궁금해요" },
                { "Thanks for the honest take 🙏 Tell me what missed and I'll fix it", "Appreciate the feedback. Original credit & process are in the description", "Fair point! I'll show how scene {S} was made in the next one", "Feedback welcome 😊 Coming back with a better cut", "Good catch, thanks — I'll look into it 🛠", "Respect your view! Which style would you rather see?" },
                { "率直な意見ありがとう🙏 どこが惜しかったか教えてください", "フィードバック感謝です。原作クレジットと制作過程は概要欄にあります", "確かに！シーン{S}の作り方は次回公開します", "意見歓迎です😊 もっと良い版で戻ってきます", "ご指摘ありがとう、確認して改善します🛠", "ご意見尊重します！どんなスタイルが見たいですか？" },
                { "Gracias por la sinceridad 🙏 Dime qué faltó y lo mejoro", "Agradezco el comentario. El crédito original y el proceso están en la descripción", "¡Buen punto! En el próximo muestro cómo se hizo la escena {S}", "Comentarios bienvenidos 😊 Vuelvo con una versión mejor", "Buena observación, lo reviso 🛠", "¡Respeto tu opinión! ¿Qué estilo te gustaría ver?" },
            },
            new[,]
            {
                { "봐주셔서 감사해요! 장면 {S} 어땠나요? 👀", "댓글 고마워요 😊 다음 편 아이디어 있으면 남겨주세요", "와줘서 반가워요! {M} 분위기 괜찮았나요?", "감사합니다 🙏 저장해두고 다시 보면 숨은 디테일이 보여요", "고마워요! 어떤 주제가 더 보고 싶어요?", "반가워요 👋 알림 켜두면 다음 편 바로 보실 수 있어요" },
                { "Thanks for watching! How was scene {S}? 👀", "Thanks for the comment 😊 Drop ideas for the next one", "Glad you're here! Did the {M} vibe land?", "Thank you 🙏 Rewatch for the hidden details", "Thanks! What topic should I do next?", "Hey 👋 Turn on alerts to catch the next one first" },
                { "見てくれてありがとう！シーン{S}どうでした？👀", "コメントありがとう😊 次回のアイデアもぜひ", "来てくれてうれしい！{M}雰囲気どうでした？", "ありがとう🙏 見返すと隠れたディテールが", "ありがとう！次は何が見たい？", "こんにちは👋 通知オンで次回をいち早く" },
                { "¡Gracias por verlo! ¿Qué tal la escena {S}? 👀", "Gracias por comentar 😊 Deja ideas para el próximo", "¡Qué bueno verte! ¿Te llegó el ambiente {M}?", "Gracias 🙏 Vuelve a verlo para los detalles ocultos", "¡Gracias! ¿Qué tema quieres ver?", "Hola 👋 Activa la campanita para el próximo" },
            },
        };
    }

    private static int Fill(ReadOnlySpan<char> tpl, ReadOnlySpan<char> quote, int scene, int moodIdx, CommentLang lang, Span<char> dst)
    {
        var n = 0;
        for (var i = 0; i < tpl.Length && n < dst.Length; i++)
        {
            if (tpl[i] == '{' && i + 2 < tpl.Length && tpl[i + 2] == '}')
            {
                var k = tpl[i + 1];
                if (k == 'Q') { var q = quote[..Math.Min(quote.Length, Math.Max(dst.Length - n - 40, 0))]; q.CopyTo(dst[n..]); n += q.Length; }
                else if (k == 'S') { scene.TryFormat(dst[n..], out var w, default, System.Globalization.CultureInfo.InvariantCulture); n += w; }
                else if (k == 'M') { var m = Moods[moodIdx * 4 + (int)lang].AsSpan(); m.CopyTo(dst[n..]); n += m.Length; }
                i += 2; continue;
            }
            dst[n++] = tpl[i];
        }
        return n;
    }

    /// <summary>질문 핵심 구절(첫 문장 · 최대 24 자) 추출.</summary>
    public static ReadOnlySpan<char> QuoteOf(ReadOnlySpan<char> s)
    {
        s = s.Trim();
        var e = s.IndexOfAny('?', '？', '\n');
        if (e > 0) s = s[..e];
        if (s.Length > 24) s = s[..(char.IsHighSurrogate(s[23]) ? 23 : 24)];
        return s.TrimEnd();
    }

    /// <summary>
    /// 답글 합성(무할당) — 가드 유사도 ≤ 0.40 을 만족하는 변형을 슬롯 답글 영역에 기록. 스팸/불가 시 false.
    /// scene · tension 은 v17.40 내러티브(미연동 시 1 · 0.5).
    /// </summary>
    public bool SynthesizeReply(int i, int scene, float tension, OmniPlatformQuotaSpamSafetyGuard guard)
    {
        var s = Slot(i);
        ref var c = ref _ring[s];
        if (c.Intent == CommentIntent.Spam) { c.Reply = ReplyState.Skipped; return false; }
        var it = c.Intent switch { CommentIntent.Positive => 0, CommentIntent.Question => 1, CommentIntent.Rebuttal => 2, _ => 3 };
        var mood = tension > 0.7f ? 2 : tension > 0.4f ? 1 : 0;
        var tpl = Templates[it];
        var quote = QuoteOf(_text.AsSpan(s * TextCap, c.TextLen));
        var dst = _reply.AsSpan(s * TextCap, TextCap);
        var start = (int)(c.Id % Variants);
        for (var v = 0; v < Variants; v++)
        {
            var n = Fill(tpl[(int)c.Lang, (start + v) % Variants], quote, scene, mood, c.Lang, dst);
            if (AiSignature && n + Sig.Length <= TextCap) { Sig.AsSpan().CopyTo(dst[n..]); n += Sig.Length; }
            if (guard.MaxHistorySimilarity(dst[..n]) <= OmniPlatformQuotaSpamSafetyGuard.MaxSimilarity)
            {
                c.ReplyLen = n; c.Reply = ReplyState.Suggested;
                return true;
            }
        }
        c.ReplyLen = 0; c.Reply = ReplyState.BlockedSimilar;
        return false;
    }

    private static readonly string[,] Hooks =
    {
        { "📌 3초 지점에서 뭐가 바뀌었는지 찾은 사람? 정답은 댓글로! 👇", "📌 Did you catch what changed at the 3-second mark? Answer below! 👇", "📌 3秒地点で何が変わったか気づいた人？答えはコメントで👇", "📌 ¿Notaste qué cambió en el segundo 3? ¡Responde abajo! 👇" },
        { "📌 이 장면, 실제일까 AI일까? A/B로 투표해 주세요 🗳", "📌 Real or AI? Vote A or B below 🗳", "📌 これは実写？AI？AかBで投票して🗳", "📌 ¿Real o IA? Vota A o B abajo 🗳" },
        { "📌 다음 편 장소를 골라주세요: 1) 도시 2) 바다 3) 우주 🌍", "📌 Pick the next location: 1) city 2) ocean 3) space 🌍", "📌 次の舞台を選んで：1) 都市 2) 海 3) 宇宙🌍", "📌 Elige el próximo lugar: 1) ciudad 2) mar 3) espacio 🌍" },
    };

    /// <summary>고정 질문 훅 작성(무할당) — 변형 = 카테고리 순환 · 길이 반환.</summary>
    public int WritePinnedHook(CommentLang lang, int variant, Span<char> dst)
    {
        var h = Hooks[((variant % 3) + 3) % 3, (int)lang].AsSpan();
        var n = Math.Min(h.Length, dst.Length);
        h[..n].CopyTo(dst);
        if (AiSignature && n + Sig.Length <= dst.Length) { Sig.AsSpan().CopyTo(dst[n..]); n += Sig.Length; }
        return n;
    }
}
