namespace PromptFlowStudio.Services.Localization.Dubbing;

public enum DubLanguage : byte { Ko, En, Ja, Zh, Es, Fr, De, It, Pt, Ru, Ar, Hi, Vi }
public enum Rhythm : byte { Syllable, Stress, Mora }
public enum ScriptKind : byte { Hangul, Latin, Cjk, Cyrillic, Arabic, Devanagari }
public enum LineEmotion : byte { Calm, Joy, SadQuestion, Determined, Humor, Gratitude }

/// <summary>언어 프로필: 어족 · 리듬 · 성조 · 발화 속도(음절/s) · 자막 CPS/줄 길이 · 문자 · RTL · 경어 체계 · 의문문 상승.</summary>
public readonly record struct LanguageProfile(DubLanguage Id, string Code, string Name, string Family, string Branch, Rhythm Rhythm, bool Tonal,
    float SyllablesPerSec, float MaxCps, int MaxLineChars, ScriptKind Script, bool Rtl, bool Formality, float QuestionRise);

/// <summary>대사 템플릿(표시 · 로마자 발음) — 토큰 {easy} {funny} {thanks} {thumbs}.</summary>
public readonly record struct LineTemplate(string Display, string Roman);

/// <summary>
/// [PromptFlow Studio v16.89.0] 🌐 다국어 카탈로그(13개 언어 · 원본 한국어 + 대상 12) — 데모 대사 6줄 번역 템플릿 · 관용구/유머 적응표(적응 ↔ 직역) · 경어 변형 · 문화 민감 표현.
/// 번역은 내장 구문집(기계 번역 모델 미사용) · 로마자 = 근사 발음(G2P 입력 · 성조 언어는 음절 뒤 숫자 1~6).
/// </summary>
public static class GlobalLanguageCatalog
{
    public const int Lines = 6, LanguageCount = 13;

    public static readonly LanguageProfile[] Profiles =
    {
        new(DubLanguage.Ko, "ko", "한국어", "한국어족", "한국어", Rhythm.Syllable, false, 6.5f, 12f, 20, ScriptKind.Hangul, false, true, 3f),
        new(DubLanguage.En, "en", "English", "인도유럽", "게르만", Rhythm.Stress, false, 5.0f, 17f, 42, ScriptKind.Latin, false, false, 4f),
        new(DubLanguage.Ja, "ja", "日本語", "일본어족", "일본어", Rhythm.Mora, false, 7.5f, 9f, 16, ScriptKind.Cjk, false, true, 3f),
        new(DubLanguage.Zh, "zh", "中文", "중국티베트", "중국어", Rhythm.Syllable, true, 5.2f, 9f, 16, ScriptKind.Cjk, false, true, 1f),
        new(DubLanguage.Es, "es", "Español", "인도유럽", "로망스", Rhythm.Syllable, false, 7.0f, 17f, 42, ScriptKind.Latin, false, true, 4f),
        new(DubLanguage.Fr, "fr", "Français", "인도유럽", "로망스", Rhythm.Syllable, false, 6.5f, 17f, 42, ScriptKind.Latin, false, true, 4f),
        new(DubLanguage.De, "de", "Deutsch", "인도유럽", "게르만", Rhythm.Stress, false, 5.5f, 17f, 42, ScriptKind.Latin, false, true, 4f),
        new(DubLanguage.It, "it", "Italiano", "인도유럽", "로망스", Rhythm.Syllable, false, 6.8f, 17f, 42, ScriptKind.Latin, false, true, 4f),
        new(DubLanguage.Pt, "pt", "Português", "인도유럽", "로망스", Rhythm.Stress, false, 6.0f, 17f, 42, ScriptKind.Latin, false, true, 4f),
        new(DubLanguage.Ru, "ru", "Русский", "인도유럽", "슬라브", Rhythm.Stress, false, 5.5f, 17f, 42, ScriptKind.Cyrillic, false, true, 3f),
        new(DubLanguage.Ar, "ar", "العربية", "아프리카아시아", "셈", Rhythm.Stress, false, 5.5f, 15f, 42, ScriptKind.Arabic, true, true, 3f),
        new(DubLanguage.Hi, "hi", "हिन्दी", "인도유럽", "인도아리아", Rhythm.Stress, false, 6.0f, 15f, 42, ScriptKind.Devanagari, false, true, 3f),
        new(DubLanguage.Vi, "vi", "Tiếng Việt", "오스트로아시아", "비엣므엉", Rhythm.Syllable, true, 5.5f, 17f, 42, ScriptKind.Latin, false, true, 1f),
    };

    public static readonly LineEmotion[] Emotions = { LineEmotion.Calm, LineEmotion.Joy, LineEmotion.SadQuestion, LineEmotion.Determined, LineEmotion.Humor, LineEmotion.Gratitude };
    public static readonly bool[] IsQuestion = { false, false, true, false, false, false };

    // [언어][줄]
    public static readonly LineTemplate[][] Text =
    {
        new LineTemplate[] { new("괜찮아, 내가 여기 있어.", "gwaen cha na, nae ga yeo gi i sseo"), new("이건 {easy}야! {thumbs}", "i geon {easy} ya"), new("왜 아무 말도 안 했어?", "wae a mu mal do an hae sseo"),
            new("비가 와도 우린 간다.", "bi ga wa do u rin gan da"), new("넌 정말 {funny}이야.", "neon jeong mal {funny} i ya"), new("{thanks}, 정말로.", "{thanks}, jeong mal lo") },
        new LineTemplate[] { new("It's okay, I'm right here.", "its o kei, aim rait hir"), new("This is {easy}! {thumbs}", "this iz {easy}"), new("Why didn't you say anything?", "wai di dent yu sei e ni thing"),
            new("Rain or shine, we go.", "rein or shain, wi go"), new("You're {funny}, honestly.", "yur {funny}, o nest li"), new("{thanks}, really.", "{thanks}, ri li") },
        new LineTemplate[] { new("大丈夫、私がここにいるよ。", "dai jou bu, wa ta shi ga ko ko ni i ru yo"), new("こんなの{easy}だ！{thumbs}", "kon na no {easy} da"), new("どうして何も言わなかったの？", "dou shi te na ni mo i wa na kat ta no"),
            new("雨が降っても、僕らは行く。", "a me ga fut te mo, bo ku ra wa i ku"), new("君って本当に{funny}だね。", "ki mit te hon tou ni {funny} da ne"), new("{thanks}、本当に。", "{thanks}, hon tou ni") },
        new LineTemplate[] { new("没关系，我就在这里。", "mei2 guan1 xi5, wo3 jiu4 zai4 zhe4 li3"), new("这简直是{easy}！{thumbs}", "zhe4 jian3 zhi2 shi4 {easy}"), new("你为什么什么都没说？", "ni3 wei4 shen2 me5 shen2 me5 dou1 mei2 shuo1"),
            new("就算下雨，我们也要走。", "jiu4 suan4 xia4 yu3, wo3 men5 ye3 yao4 zou3"), new("你真是个{funny}。", "ni3 zhen1 shi4 ge5 {funny}"), new("{thanks}，真的。", "{thanks}, zhen1 de5") },
        new LineTemplate[] { new("Tranquilo, estoy aquí.", "tran ki lo, es toi a ki"), new("¡Esto es {easy}! {thumbs}", "es to es {easy}"), new("¿Por qué no dijiste nada?", "por ke no di khis te na da"),
            new("Llueva o no, nos vamos.", "yue va o no, nos va mos"), new("Eres {funny}, de verdad.", "e res {funny}, de ver dad"), new("{thanks}, de verdad.", "{thanks}, de ver dad") },
        new LineTemplate[] { new("Ça va, je suis là.", "sa va, je sui la"), new("C'est {easy} ! {thumbs}", "se {easy}"), new("Pourquoi tu n'as rien dit ?", "pur kua tu na rien di"),
            new("Pluie ou pas, on y va.", "plui u pa, on i va"), new("Tu es {funny}, vraiment.", "tu e {funny}, vre man"), new("{thanks}, vraiment.", "{thanks}, vre man") },
        new LineTemplate[] { new("Schon gut, ich bin hier.", "shon gut, ikh bin hir"), new("Das ist {easy}! {thumbs}", "das ist {easy}"), new("Warum hast du nichts gesagt?", "va rum hast du nikhts ge zagt"),
            new("Auch wenn es regnet, wir gehen.", "aukh ven es reg net, vir ge en"), new("Du bist {funny}, ehrlich.", "du bist {funny}, er likh"), new("{thanks}, wirklich.", "{thanks}, virk likh") },
        new LineTemplate[] { new("Va tutto bene, sono qui.", "va tut to be ne, so no kui"), new("È {easy}! {thumbs}", "e {easy}"), new("Perché non hai detto niente?", "per ke non ai det to nien te"),
            new("Anche se piove, andiamo.", "an ke se pio ve, an dia mo"), new("Sei {funny}, davvero.", "sei {funny}, dav ve ro"), new("{thanks}, davvero.", "{thanks}, dav ve ro") },
        new LineTemplate[] { new("Está tudo bem, estou aqui.", "es ta tu du bein, es tou a ki"), new("Isso é {easy}! {thumbs}", "i su e {easy}"), new("Por que você não disse nada?", "por ke vo se naun dis si na da"),
            new("Chova ou não, nós vamos.", "sho va ou naun, nos va mus"), new("Você é {funny}, sério.", "vo se e {funny}, se riu"), new("{thanks}, de verdade.", "{thanks}, de ver da de") },
        new LineTemplate[] { new("Всё хорошо, я здесь.", "vsio kha ra sho, ya zdes"), new("Это {easy}! {thumbs}", "e ta {easy}"), new("Почему ты ничего не сказал?", "pa che mu ti ni che vo ne ska zal"),
            new("Даже в дождь мы идём.", "da zhe v dozhd mi i diom"), new("Ты {funny}, честно.", "ti {funny}, chest na"), new("{thanks}, правда.", "{thanks}, prav da") },
        new LineTemplate[] { new("لا بأس، أنا هنا.", "la bas, a na hu na"), new("هذا {easy}! {thumbs}", "ha dha {easy}"), new("لماذا لم تقل شيئًا؟", "li ma dha lam ta qul shai an"),
            new("حتى لو أمطرت، سنذهب.", "hat ta law am ta rat, sa nadh hab"), new("أنت {funny} حقًا.", "an ta {funny} haq qan"), new("{thanks}، حقًا.", "{thanks}, haq qan") },
        new LineTemplate[] { new("कोई बात नहीं, मैं यहाँ हूँ।", "ko i baat na hin, main ya han hun"), new("यह तो {easy} है! {thumbs}", "ye to {easy} hai"), new("तुमने कुछ कहा क्यों नहीं?", "tum ne kuch ka ha kyon na hin"),
            new("बारिश हो तो भी हम जाएँगे।", "baa rish ho to bhi ham ja en ge"), new("तुम सच में {funny} हो।", "tum sach men {funny} ho"), new("{thanks}, सच में।", "{thanks}, sach men") },
        new LineTemplate[] { new("Không sao đâu, có tôi ở đây.", "khong1 sao1 dau1, co3 toi1 o4 day1"), new("Cái này {easy}! {thumbs}", "cai3 nay2 {easy}"), new("Sao anh không nói gì?", "sao1 anh1 khong1 noi3 gi2"),
            new("Dù mưa, chúng ta vẫn đi.", "du2 mua1, chung3 ta1 van4 di1"), new("Cậu {funny} thật đấy.", "cau4 {funny} that4 day3"), new("{thanks}, thật đấy.", "{thanks}, that4 day3") },
    };

    /// <summary>관용구/유머 적응표: [토큰][언어] = (적응 표시, 적응 발음, 직역 표시, 직역 발음).</summary>
    public static readonly (string Adapted, string AdaptedRoman, string Literal, string LiteralRoman)[][] Idioms =
    {
        new[]
        {
            ("식은 죽 먹기", "si geun juk meok gi", "식은 죽 먹기", "si geun juk meok gi"),
            ("a piece of cake", "a pis ov keik", "eating cold porridge", "i ting kold po ridj"),
            ("朝飯前", "a sa me shi ma e", "冷めたお粥を食べること", "sa me ta o ka yu wo ta be ru ko to"),
            ("小菜一碟", "xiao3 cai4 yi4 die2", "吃冷粥", "chi1 leng3 zhou1"),
            ("pan comido", "pan ko mi do", "comer gachas frías", "ko mer ga chas fri as"),
            ("du gâteau", "du ga to", "manger du porridge froid", "man je du po rij frua"),
            ("ein Kinderspiel", "ain kin der shpil", "kalten Brei essen", "kal ten brai e sen"),
            ("un gioco da ragazzi", "un jo ko da ra ga tsi", "mangiare porridge freddo", "man ja re po rij fre do"),
            ("moleza", "mo le za", "comer mingau frio", "ko mer min gau fri u"),
            ("проще пареной репы", "pro sche pa re noi re pi", "есть холодную кашу", "est kha lod nu yu ka shu"),
            ("سهل جدًا", "sahl jid dan", "أكل عصيدة باردة", "akl a si da ba ri da"),
            ("बाएँ हाथ का खेल", "ba en haath ka khel", "ठंडा दलिया खाना", "than da da li ya kha na"),
            ("dễ như ăn bánh", "de3 nhu1 an1 banh3", "ăn cháo nguội", "an1 chao3 nguoi5"),
        },
        new[]
        {
            ("웃기는 짬뽕", "ut gi neun jjam ppong", "웃기는 짬뽕", "ut gi neun jjam ppong"),
            ("a total clown", "a to tal klaun", "laughing jjamppong noodles", "laf ing jam pong nu dels"),
            ("とんだお笑い草", "ton da o wa rai gu sa", "笑えるちゃんぽん", "wa ra e ru chan pon"),
            ("活宝", "huo2 bao3", "好笑的炒码面", "hao3 xiao4 de5 chao3 ma3 mian4"),
            ("un payaso", "un pa ya so", "un jjamppong gracioso", "un jam pong gra sio so"),
            ("un sacré clown", "un sa kre klun", "un jjamppong drôle", "un jam pong drol"),
            ("ein echter Witzbold", "ain ekh ter vits bolt", "ein lustiges Jjamppong", "ain lus ti ges jam pong"),
            ("un vero pagliaccio", "un ve ro pa lia cho", "uno jjamppong buffo", "u no jam pong bu fo"),
            ("um palhaço", "um pa lia su", "um jjamppong engraçado", "um jam pong en gra sa du"),
            ("настоящий клоун", "na sta ya schi klo un", "смешной чамппон", "smesh noi cham pon"),
            ("مهرّج حقيقي", "mu har rij ha qi qi", "جامبونغ مضحك", "jam bong mud hik"),
            ("पूरा जोकर", "pu ra jo kar", "हँसाने वाला जजांपोंग", "han sa ne va la jam pong"),
            ("đúng là đồ hề", "dung3 la2 do2 he2", "món jjamppong buồn cười", "mon3 jam pong buon2 cuoi2"),
        },
    };

    public static readonly string[] IdiomTokens = { "{easy}", "{funny}" };
    public static readonly bool[] IdiomIsHumor = { false, true };

    /// <summary>경어 변형 {thanks}: [언어] = (반말 표시, 반말 발음, 존대 표시, 존대 발음).</summary>
    public static readonly (string Casual, string CasualRoman, string Formal, string FormalRoman)[] Thanks =
    {
        ("고마워", "go ma wo", "감사합니다", "gam sa ham ni da"),
        ("Thanks", "thenks", "Thank you so much", "thenk yu so mach"),
        ("ありがとう", "a ri ga tou", "ありがとうございます", "a ri ga tou go za i ma su"),
        ("谢啦", "xie4 la5", "非常感谢您", "fei1 chang2 gan3 xie4 nin2"),
        ("Gracias", "gra sias", "Muchísimas gracias", "mu chi si mas gra sias"),
        ("Merci", "mer si", "Merci beaucoup", "mer si bo ku"),
        ("Danke", "dan ke", "Vielen Dank", "fi len dank"),
        ("Grazie", "gra tsie", "Grazie mille", "gra tsie mil le"),
        ("Valeu", "va leu", "Muito obrigado", "mui tu o bri ga du"),
        ("Спасибо", "spa si ba", "Большое спасибо", "bal sho ye spa si ba"),
        ("شكرًا", "shuk ran", "شكرًا جزيلًا", "shuk ran ja zi lan"),
        ("थैंक्स", "thenks", "बहुत धन्यवाद", "ba hut dhan ya vaad"),
        ("Cảm ơn nha", "cam3 on1 nha1", "Xin cảm ơn rất nhiều", "xin1 cam3 on1 rat3 nhieu2"),
    };

    /// <summary>문화 민감 표현: (토큰, 기본, 대체, 대상 언어 마스크, 사유).</summary>
    public static readonly (string Token, string Default, string Replacement, int LangMask, string Reason)[] Sensitive =
    {
        ("{thumbs}", "👍", "✔", 1 << (int)DubLanguage.Ar, "엄지 제스처 = 일부 중동 지역 무례"),
    };

    public static LanguageProfile Profile(DubLanguage l) => Profiles[(int)l];
    public static string EmotionName(LineEmotion e) => e switch
    {
        LineEmotion.Joy => "기쁨", LineEmotion.SadQuestion => "서운함(의문)", LineEmotion.Determined => "결의", LineEmotion.Humor => "유머", LineEmotion.Gratitude => "감사", _ => "위로",
    };
}
