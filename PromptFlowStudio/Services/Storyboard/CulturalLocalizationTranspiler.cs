using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>v16.24.0 문자 체계(자막 줄 길이 · 음절 추정 · 폰트).</summary>
public enum LocaleScript { Hangul, Latin, Cjk, Kana, Thai, Devanagari, Arabic, Hebrew, Cyrillic, Greek }

/// <summary>
/// v16.24.0 더빙/현지화 대상 로캘 — 코드(BCP-47) · 표시명 · 국기 · Edge 여/남 음성 · 기준 F0(Hz) · 통화 · 야드파운드법 · 문자 체계 · 초당 음절 · 미리듣기 문장.
/// </summary>
public sealed record LocaleProfile(
    string Code, string Name, string Native, string Flag, string EdgeFemale, string EdgeMale,
    string Currency, bool Imperial, LocaleScript Script, double SyllablesPerSecond, string Sample, bool Rtl = false);

/// <summary>v16.24.0 현지화 변환 1건(종류 · 원문 · 변환).</summary>
public readonly record struct LocalizationChange(string Kind, string From, string To);

/// <summary>v16.24.0 현지화 결과 — 로캘 · 줄 목록(입력 순서) · CTA · 변경 내역 · 각색 메모 · AI 사용 여부 · 상태 문구.</summary>
public sealed record LocalizationResult(
    LocaleProfile Locale, IReadOnlyList<string> Lines, string Cta, IReadOnlyList<LocalizationChange> Changes,
    IReadOnlyList<string> Notes, bool AiUsed, string Message);

/// <summary>
/// [PromptFlowStudio] v16.24.0 🌏 문화권별 텍스트/화폐/단위 현지화 변환기 — 프롬프트·대본·CTA → 대상 로캘 각색.
///   ▸ 결정적 사전 변환(오프라인 · 테스트 가능): 화폐 KRW(₩·원·만/천/억 원) → 대상 통화(참고 환율 · <c>PFS_FX_RATES</c> 덮어쓰기 · 자릿수 규칙)
///     · 단위 cm/mm/m/km/kg/g/ml/L/℃ → 야드파운드(미국) 또는 기호 정규화 · 관용구 → 평이한 의미 · 문화권 금기어 대체(+경고 메모).
///   ▸ Gemini 각색(선택): 사전 변환본을 대상 언어로 번역·각색(변환된 금액/단위 유지 지시) — Strict JSON 즉시 검증(줄 수 일치 · 문자열 · 길이 상한).
///   ▸ AI 키 없음/실패/검증 실패 → v2.6.0 오프라인 글로서리(<see cref="Media.GlobalMultiDubbingEngine.Translate"/>) → 원문 유지([부분 구제] · 예외 0).
/// </summary>
public sealed partial class CulturalLocalizationTranspiler
{
    public const string FxRatesEnv = "PFS_FX_RATES";
    public const int MaxLines = 64;
    public const int MaxLineChars = 400;

    /// <summary>지원 로캘 34종(ko 원본 포함).</summary>
    public static readonly IReadOnlyList<LocaleProfile> Locales = new[]
    {
        new LocaleProfile("ko-KR", "Korean", "한국어", "🇰🇷", "ko-KR-SunHiNeural", "ko-KR-InJoonNeural", "KRW", false, LocaleScript.Hangul, 6.5, "안녕하세요! 목소리 미리듣기입니다."),
        new LocaleProfile("en-US", "English (US)", "English", "🇺🇸", "en-US-JennyNeural", "en-US-GuyNeural", "USD", true, LocaleScript.Latin, 4.2, "Hi there! This is a quick voice preview."),
        new LocaleProfile("en-GB", "English (UK)", "English", "🇬🇧", "en-GB-SoniaNeural", "en-GB-RyanNeural", "GBP", false, LocaleScript.Latin, 4.2, "Hello! This is a quick voice preview."),
        new LocaleProfile("ja-JP", "Japanese", "日本語", "🇯🇵", "ja-JP-NanamiNeural", "ja-JP-KeitaNeural", "JPY", false, LocaleScript.Kana, 7.5, "こんにちは！音声プレビューです。"),
        new LocaleProfile("zh-CN", "Chinese (Simplified)", "简体中文", "🇨🇳", "zh-CN-XiaoxiaoNeural", "zh-CN-YunxiNeural", "CNY", false, LocaleScript.Cjk, 5.0, "你好！这是语音试听。"),
        new LocaleProfile("zh-TW", "Chinese (Traditional)", "繁體中文", "🇹🇼", "zh-TW-HsiaoChenNeural", "zh-TW-YunJheNeural", "TWD", false, LocaleScript.Cjk, 5.0, "你好！這是語音試聽。"),
        new LocaleProfile("es-ES", "Spanish (Spain)", "Español", "🇪🇸", "es-ES-ElviraNeural", "es-ES-AlvaroNeural", "EUR", false, LocaleScript.Latin, 5.5, "¡Hola! Esta es una vista previa de voz."),
        new LocaleProfile("es-MX", "Spanish (Mexico)", "Español (MX)", "🇲🇽", "es-MX-DaliaNeural", "es-MX-JorgeNeural", "MXN", false, LocaleScript.Latin, 5.5, "¡Hola! Esta es una vista previa de voz."),
        new LocaleProfile("fr-FR", "French", "Français", "🇫🇷", "fr-FR-DeniseNeural", "fr-FR-HenriNeural", "EUR", false, LocaleScript.Latin, 5.0, "Bonjour ! Voici un aperçu de la voix."),
        new LocaleProfile("de-DE", "German", "Deutsch", "🇩🇪", "de-DE-KatjaNeural", "de-DE-ConradNeural", "EUR", false, LocaleScript.Latin, 4.5, "Hallo! Das ist eine kurze Stimmprobe."),
        new LocaleProfile("it-IT", "Italian", "Italiano", "🇮🇹", "it-IT-ElsaNeural", "it-IT-DiegoNeural", "EUR", false, LocaleScript.Latin, 5.5, "Ciao! Questa è un'anteprima della voce."),
        new LocaleProfile("pt-BR", "Portuguese (Brazil)", "Português (BR)", "🇧🇷", "pt-BR-FranciscaNeural", "pt-BR-AntonioNeural", "BRL", false, LocaleScript.Latin, 5.0, "Olá! Esta é uma prévia da voz."),
        new LocaleProfile("pt-PT", "Portuguese (Portugal)", "Português (PT)", "🇵🇹", "pt-PT-RaquelNeural", "pt-PT-DuarteNeural", "EUR", false, LocaleScript.Latin, 5.0, "Olá! Esta é uma pré-visualização da voz."),
        new LocaleProfile("ru-RU", "Russian", "Русский", "🇷🇺", "ru-RU-SvetlanaNeural", "ru-RU-DmitryNeural", "RUB", false, LocaleScript.Cyrillic, 4.8, "Привет! Это короткий пример голоса."),
        new LocaleProfile("ar-SA", "Arabic", "العربية", "🇸🇦", "ar-SA-ZariyahNeural", "ar-SA-HamedNeural", "SAR", false, LocaleScript.Arabic, 4.5, "مرحبا! هذه معاينة للصوت.", Rtl: true),
        new LocaleProfile("hi-IN", "Hindi", "हिन्दी", "🇮🇳", "hi-IN-SwaraNeural", "hi-IN-MadhurNeural", "INR", false, LocaleScript.Devanagari, 5.0, "नमस्ते! यह आवाज़ का पूर्वावलोकन है।"),
        new LocaleProfile("id-ID", "Indonesian", "Bahasa Indonesia", "🇮🇩", "id-ID-GadisNeural", "id-ID-ArdiNeural", "IDR", false, LocaleScript.Latin, 5.5, "Halo! Ini adalah pratinjau suara."),
        new LocaleProfile("ms-MY", "Malay", "Bahasa Melayu", "🇲🇾", "ms-MY-YasminNeural", "ms-MY-OsmanNeural", "MYR", false, LocaleScript.Latin, 5.5, "Helo! Ini ialah pratonton suara."),
        new LocaleProfile("th-TH", "Thai", "ไทย", "🇹🇭", "th-TH-PremwadeeNeural", "th-TH-NiwatNeural", "THB", false, LocaleScript.Thai, 5.5, "สวัสดี! นี่คือตัวอย่างเสียง"),
        new LocaleProfile("vi-VN", "Vietnamese", "Tiếng Việt", "🇻🇳", "vi-VN-HoaiMyNeural", "vi-VN-NamMinhNeural", "VND", false, LocaleScript.Latin, 5.5, "Xin chào! Đây là bản nghe thử giọng nói."),
        new LocaleProfile("tr-TR", "Turkish", "Türkçe", "🇹🇷", "tr-TR-EmelNeural", "tr-TR-AhmetNeural", "TRY", false, LocaleScript.Latin, 5.2, "Merhaba! Bu kısa bir ses önizlemesidir."),
        new LocaleProfile("pl-PL", "Polish", "Polski", "🇵🇱", "pl-PL-ZofiaNeural", "pl-PL-MarekNeural", "PLN", false, LocaleScript.Latin, 5.0, "Cześć! To jest krótki podgląd głosu."),
        new LocaleProfile("nl-NL", "Dutch", "Nederlands", "🇳🇱", "nl-NL-ColetteNeural", "nl-NL-MaartenNeural", "EUR", false, LocaleScript.Latin, 4.8, "Hallo! Dit is een korte stemvoorbeeld."),
        new LocaleProfile("sv-SE", "Swedish", "Svenska", "🇸🇪", "sv-SE-SofieNeural", "sv-SE-MattiasNeural", "SEK", false, LocaleScript.Latin, 4.8, "Hej! Det här är en kort röstförhandsvisning."),
        new LocaleProfile("da-DK", "Danish", "Dansk", "🇩🇰", "da-DK-ChristelNeural", "da-DK-JeppeNeural", "DKK", false, LocaleScript.Latin, 4.8, "Hej! Dette er en kort stemmeprøve."),
        new LocaleProfile("nb-NO", "Norwegian", "Norsk", "🇳🇴", "nb-NO-PernilleNeural", "nb-NO-FinnNeural", "NOK", false, LocaleScript.Latin, 4.8, "Hei! Dette er en kort stemmeprøve."),
        new LocaleProfile("fi-FI", "Finnish", "Suomi", "🇫🇮", "fi-FI-NooraNeural", "fi-FI-HarriNeural", "EUR", false, LocaleScript.Latin, 4.8, "Hei! Tämä on lyhyt ääninäyte."),
        new LocaleProfile("cs-CZ", "Czech", "Čeština", "🇨🇿", "cs-CZ-VlastaNeural", "cs-CZ-AntoninNeural", "CZK", false, LocaleScript.Latin, 5.0, "Ahoj! Toto je krátká ukázka hlasu."),
        new LocaleProfile("el-GR", "Greek", "Ελληνικά", "🇬🇷", "el-GR-AthinaNeural", "el-GR-NestorasNeural", "EUR", false, LocaleScript.Greek, 5.0, "Γεια σας! Αυτή είναι μια σύντομη προεπισκόπηση φωνής."),
        new LocaleProfile("he-IL", "Hebrew", "עברית", "🇮🇱", "he-IL-HilaNeural", "he-IL-AvriNeural", "ILS", false, LocaleScript.Hebrew, 5.0, "שלום! זוהי תצוגה מקדימה של הקול.", Rtl: true),
        new LocaleProfile("hu-HU", "Hungarian", "Magyar", "🇭🇺", "hu-HU-NoemiNeural", "hu-HU-TamasNeural", "HUF", false, LocaleScript.Latin, 5.0, "Szia! Ez egy rövid hangminta."),
        new LocaleProfile("ro-RO", "Romanian", "Română", "🇷🇴", "ro-RO-AlinaNeural", "ro-RO-EmilNeural", "RON", false, LocaleScript.Latin, 5.2, "Bună! Aceasta este o previzualizare a vocii."),
        new LocaleProfile("uk-UA", "Ukrainian", "Українська", "🇺🇦", "uk-UA-PolinaNeural", "uk-UA-OstapNeural", "UAH", false, LocaleScript.Cyrillic, 4.8, "Привіт! Це короткий приклад голосу."),
        new LocaleProfile("fil-PH", "Filipino", "Filipino", "🇵🇭", "fil-PH-BlessicaNeural", "fil-PH-AngeloNeural", "PHP", false, LocaleScript.Latin, 5.5, "Kumusta! Ito ay isang maikling preview ng boses."),
    };

    public static LocaleProfile? LocaleOf(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        foreach (var l in Locales) if (string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase)) return l;
        // 언어만 지정(en · ja …) → 첫 일치 로캘
        foreach (var l in Locales) if (l.Code.StartsWith(code + "-", StringComparison.OrdinalIgnoreCase)) return l;
        return null;
    }

    /// <summary>KRW 환산 참고 환율(1 단위 = N 원) — 실시간 시세 아님. <c>PFS_FX_RATES="USD=1380;JPY=9.2"</c> 로 덮어쓰기.</summary>
    public static readonly IReadOnlyDictionary<string, double> DefaultKrwRates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
    {
        ["USD"] = 1380, ["GBP"] = 1750, ["EUR"] = 1500, ["JPY"] = 9.2, ["CNY"] = 190, ["TWD"] = 43, ["MXN"] = 75, ["BRL"] = 250,
        ["RUB"] = 15, ["SAR"] = 368, ["INR"] = 16.5, ["IDR"] = 0.085, ["MYR"] = 295, ["THB"] = 39, ["VND"] = 0.054, ["TRY"] = 40,
        ["PLN"] = 350, ["SEK"] = 130, ["DKK"] = 200, ["NOK"] = 128, ["CZK"] = 60, ["ILS"] = 370, ["HUF"] = 3.8, ["RON"] = 300,
        ["UAH"] = 33, ["PHP"] = 24, ["KRW"] = 1,
    };

    // 통화 기호 · 접미 여부 · 소수 자릿수
    private static (string Symbol, bool Suffix, int Decimals) CurrencyFormat(string code) => code.ToUpperInvariant() switch
    {
        "USD" => ("$", false, 2), "GBP" => ("£", false, 2), "EUR" => ("€", false, 2), "JPY" => ("¥", false, 0), "CNY" => ("¥", false, 2),
        "TWD" => ("NT$", false, 0), "MXN" => ("MX$", false, 2), "BRL" => ("R$", false, 2), "RUB" => ("₽", true, 0), "SAR" => ("SAR ", false, 2),
        "INR" => ("₹", false, 0), "IDR" => ("Rp", false, 0), "MYR" => ("RM", false, 2), "THB" => ("฿", false, 0), "VND" => ("₫", true, 0),
        "TRY" => ("₺", false, 2), "PLN" => ("zł", true, 2), "SEK" or "DKK" or "NOK" => ("kr", true, 0), "CZK" => ("Kč", true, 0),
        "ILS" => ("₪", false, 2), "HUF" => ("Ft", true, 0), "RON" => ("lei", true, 2), "UAH" => ("₴", false, 0), "PHP" => ("₱", false, 2),
        _ => ("₩", false, 0),
    };

    // 관용구 → 평이한 의미(직역 시 의미 상실 방지)
    private static readonly (string Idiom, string Plain)[] Idioms =
    {
        ("누워서 떡 먹기", "아주 쉬운 일"), ("식은 죽 먹기", "아주 쉬운 일"), ("그림의 떡", "가질 수 없는 것"),
        ("발 없는 말이 천리 간다", "소문은 빠르게 퍼진다"), ("티끌 모아 태산", "작은 것이 모여 큰 것이 된다"),
        ("눈에 넣어도 아프지 않은", "너무나 사랑스러운"), ("하늘의 별 따기", "매우 어려운 일"), ("꿩 먹고 알 먹고", "한 번에 두 가지 이득"),
        ("금강산도 식후경", "무엇이든 배부른 뒤에"), ("우물 안 개구리", "세상을 좁게 보는 사람"),
    };

    // 문화권 금기 대체(로캘 접두 · 원어 → 대체어 · 메모)
    private static readonly (string LangPrefix, string Word, string Replacement, string Note)[] Taboos =
    {
        ("ar", "돼지고기", "소고기", "할랄 문화권 — 돼지고기 표현 대체"), ("ar", "맥주", "주스", "주류 표현 대체"), ("ar", "와인", "주스", "주류 표현 대체"),
        ("id", "돼지고기", "닭고기", "무슬림 다수 문화권 — 돼지고기 표현 대체"), ("ms", "돼지고기", "닭고기", "할랄 문화권 — 돼지고기 표현 대체"),
        ("ms", "맥주", "음료", "주류 표현 대체"), ("hi", "소고기", "닭고기", "힌두 문화권 — 소고기 표현 대체"),
        ("he", "돼지고기", "소고기", "코셔 문화권 — 돼지고기 표현 대체"),
        ("zh", "시계를 선물", "선물을", "중화권 — 시계 선물(送钟) 금기 회피"), ("ja", "4개", "3개", "일본 — 숫자 4(死) 연상 회피(선물 수량)"),
    };

    private readonly Func<string, CancellationToken, Task<GeminiResult>>? _generate;
    private readonly Func<bool> _enabled;
    private int _converted;
    private int _aiCalls;

    public CulturalLocalizationTranspiler(GeminiClient? gemini = null)
    {
        _generate = gemini is null ? null : (p, ct) => gemini.GenerateAsync(p, null, 0.4, ct);
        _enabled = () => gemini is { Enabled: true };
    }

    /// <param name="generate">지시문 → Gemini 결과(테스트 주입).</param>
    public CulturalLocalizationTranspiler(Func<string, CancellationToken, Task<GeminiResult>> generate)
    {
        _generate = generate;
        _enabled = static () => true;
    }

    public bool AiEnabled => _generate is not null && _enabled();
    public int ConvertedCount => Volatile.Read(ref _converted);
    public int AiCallCount => Volatile.Read(ref _aiCalls);

    /// <summary>환율표(기본 + 환경변수 덮어쓰기 · 잘못된 항목 무시).</summary>
    public static Dictionary<string, double> LoadRates(string? overrides = null)
    {
        var map = new Dictionary<string, double>(DefaultKrwRates, StringComparer.OrdinalIgnoreCase);
        var src = overrides ?? Environment.GetEnvironmentVariable(FxRatesEnv);
        if (string.IsNullOrWhiteSpace(src)) return map;
        foreach (var part in src.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var code = part[..eq].Trim();
            if (code.Length != 3) continue;
            if (double.TryParse(part.AsSpan(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 && double.IsFinite(v)) map[code] = v;
        }
        return map;
    }

    /// <summary>금액 표기(순수) — 기호/접미 · 자릿수 · 천 단위 구분(InvariantCulture).</summary>
    public static string FormatMoney(double amount, string currency)
    {
        var (sym, suffix, dec) = CurrencyFormat(currency);
        var num = amount.ToString(dec == 0 ? "N0" : "N2", CultureInfo.InvariantCulture);
        return suffix ? num + " " + sym : sym + num;
    }

    /// <summary>KRW 금액 → 대상 통화(순수). 환율 없으면 null.</summary>
    public static double? ConvertKrw(double krw, string currency, IReadOnlyDictionary<string, double>? rates = null)
    {
        var map = rates ?? DefaultKrwRates;
        if (!map.TryGetValue(currency, out var rate) || rate <= 0) return null;
        var (_, _, dec) = CurrencyFormat(currency);
        var v = krw / rate;
        return dec == 0 ? Math.Round(v) : Math.Round(v, 2);
    }

    [GeneratedRegex(@"(?:(?<a>\d+)\s*만\s*)?(?<n>\d[\d,]*(?:\.\d+)?)\s*(?<u>억|만|천)?\s*원|(?:₩|KRW\s?)(?<w>\d[\d,]*(?:\.\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex KrwRegex();

    [GeneratedRegex(@"(?<n>\d+(?:\.\d+)?)\s*(?<u>킬로미터|킬로그램|센티미터|밀리리터|미터|그램|리터|km|kg|cm|mm|ml|m|g|L|°C|℃)(?![A-Za-z])", RegexOptions.CultureInvariant)]
    private static partial Regex UnitRegex();

    /// <summary>화폐 변환(순수 · 변경 내역 수집).</summary>
    public static string ConvertCurrency(string text, string currency, IReadOnlyDictionary<string, double>? rates, List<LocalizationChange>? changes)
    {
        if (string.IsNullOrEmpty(text) || string.Equals(currency, "KRW", StringComparison.OrdinalIgnoreCase)) return text ?? "";
        return KrwRegex().Replace(text, m =>
        {
            double krw;
            if (m.Groups["w"].Success) { if (!TryNum(m.Groups["w"].Value, out krw)) return m.Value; }
            else
            {
                if (!TryNum(m.Groups["n"].Value, out var n)) return m.Value;
                var mul = m.Groups["u"].Value switch { "억" => 100_000_000d, "만" => 10_000d, "천" => 1_000d, _ => 1d };
                krw = n * mul;
                if (m.Groups["a"].Success && TryNum(m.Groups["a"].Value, out var a)) krw += a * 10_000d;
            }
            var v = ConvertKrw(krw, currency, rates);
            if (v is null) return m.Value;
            var to = FormatMoney(v.Value, currency);
            changes?.Add(new LocalizationChange("currency", m.Value.Trim(), to));
            return to;
        });
    }

    /// <summary>단위 변환(순수) — 야드파운드 로캘은 환산, 미터법 로캘은 기호 정규화(한글 단위명 → cm/kg …).</summary>
    public static string ConvertUnits(string text, bool imperial, List<LocalizationChange>? changes)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        return UnitRegex().Replace(text, m =>
        {
            if (!TryNum(m.Groups["n"].Value, out var n)) return m.Value;
            var sym = m.Groups["u"].Value switch
            {
                "킬로미터" => "km", "킬로그램" => "kg", "센티미터" => "cm", "밀리리터" => "ml", "미터" => "m", "그램" => "g", "리터" => "L", "℃" => "°C",
                var u => u,
            };
            string to;
            if (!imperial) to = Num(n) + " " + sym;
            else
            {
                to = sym switch
                {
                    "cm" => Num(n / 2.54) + " in", "mm" => Num(n / 25.4) + " in", "m" => Num(n * 3.28084) + " ft", "km" => Num(n * 0.621371) + " mi",
                    "kg" => Num(n * 2.20462) + " lb", "g" => Num(n * 0.035274) + " oz", "ml" => Num(n * 0.033814) + " fl oz", "L" => Num(n * 0.264172) + " gal",
                    "°C" => Num(n * 9 / 5 + 32) + " °F", _ => m.Value,
                };
            }
            if (to != m.Value) changes?.Add(new LocalizationChange("unit", m.Value, to));
            return to;
        });
    }

    /// <summary>결정적 사전 변환(관용구 → 금기어 → 화폐 → 단위 · 순수). ko-KR 대상은 원문 유지.</summary>
    public static string PreTransform(string text, LocaleProfile locale, IReadOnlyDictionary<string, double>? rates,
        List<LocalizationChange>? changes, List<string>? notes)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0 || locale.Code == "ko-KR") return s;
        foreach (var (idiom, plain) in Idioms)
        {
            if (!s.Contains(idiom, StringComparison.Ordinal)) continue;
            s = s.Replace(idiom, plain, StringComparison.Ordinal);
            changes?.Add(new LocalizationChange("idiom", idiom, plain));
        }
        foreach (var (prefix, word, rep, note) in Taboos)
        {
            if (!locale.Code.StartsWith(prefix, StringComparison.Ordinal) || !s.Contains(word, StringComparison.Ordinal)) continue;
            s = s.Replace(word, rep, StringComparison.Ordinal);
            changes?.Add(new LocalizationChange("taboo", word, rep));
            if (notes is not null && !notes.Contains(note)) notes.Add(note);
        }
        s = ConvertCurrency(s, locale.Currency, rates, changes);
        return ConvertUnits(s, locale.Imperial, changes);
    }

    /// <summary>Gemini 각색 지시문(순수).</summary>
    public static string BuildPrompt(IReadOnlyList<string> lines, string cta, LocaleProfile locale, bool culturalAdapt)
    {
        var sb = new StringBuilder(512 + lines.Count * 80);
        sb.Append("You are a professional localization writer for short-form video narration.\n")
          .Append("Translate the Korean lines into natural spoken ").Append(locale.Name).Append(" (").Append(locale.Code).Append(").\n");
        if (culturalAdapt)
            sb.Append("Adapt idioms, jokes, cultural references and taboo expressions for the target culture while keeping the meaning and emotion.\n");
        sb.Append("Keep already-converted money amounts and units exactly as written. Keep each line similar in spoken length (it will be dubbed).\n")
          .Append("Return STRICT JSON only: {\"lines\":[string x ").Append(lines.Count).Append("],\"cta\":string,\"notes\":[string]}\n")
          .Append("lines must have exactly ").Append(lines.Count).Append(" items in the same order.\n\nLINES:\n");
        for (var i = 0; i < lines.Count; i++) sb.Append(i + 1).Append(". ").Append(lines[i]).Append('\n');
        sb.Append("CTA: ").Append(cta);
        return sb.ToString();
    }

    /// <summary>Gemini 응답 스키마 검증(순수) — 줄 수 일치 · 문자열 · 길이 상한. 실패 null.</summary>
    public static (List<string> Lines, string Cta, List<string> Notes)? ParseResponse(string? text, int expected)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var doc = JsonDocument.Parse(GeminiClient.StripFence(text));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("lines", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
            if (arr.GetArrayLength() != expected) return null;
            var lines = new List<string>(expected);
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.String) return null;
                var s = (e.GetString() ?? "").Trim();
                lines.Add(s.Length > MaxLineChars ? s[..MaxLineChars] : s);
            }
            var cta = root.TryGetProperty("cta", out var c) && c.ValueKind == JsonValueKind.String ? (c.GetString() ?? "").Trim() : "";
            var notes = new List<string>(4);
            if (root.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.Array)
                foreach (var e in n.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 and <= 200 } s && notes.Count < 8) notes.Add(s);
            return (lines, cta.Length > MaxLineChars ? cta[..MaxLineChars] : cta, notes);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// 줄 목록 현지화(예외 0) — 사전 변환 → (useAi) Gemini 번역·각색 → 실패 시 오프라인 글로서리/원문.
    /// culturalAdapt=false 면 관용구/금기어 각색 없이 화폐·단위만 변환 후 번역.
    /// </summary>
    public async Task<LocalizationResult> LocalizeAsync(IReadOnlyList<string> lines, string cta, LocaleProfile locale,
        bool culturalAdapt = true, bool useAi = true, IReadOnlyDictionary<string, double>? rates = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(locale);
        var fx = rates ?? LoadRates();
        var changes = new List<LocalizationChange>(8);
        var notes = new List<string>(4);
        var n = Math.Min(lines.Count, MaxLines);
        var pre = new List<string>(n);
        for (var i = 0; i < n; i++)
        {
            var s = culturalAdapt ? PreTransform(lines[i], locale, fx, changes, notes)
                : locale.Code == "ko-KR" ? (lines[i] ?? "").Trim() : ConvertUnits(ConvertCurrency((lines[i] ?? "").Trim(), locale.Currency, fx, changes), locale.Imperial, changes);
            pre.Add(s);
        }
        var preCta = culturalAdapt ? PreTransform(cta ?? "", locale, fx, changes, notes) : (cta ?? "").Trim();
        Interlocked.Add(ref _converted, changes.Count);
        if (locale.Code == "ko-KR") return new(locale, pre, preCta, changes, notes, false, "원본 언어");

        var msg = "오프라인 글로서리";
        if (useAi && AiEnabled && n > 0)
        {
            try
            {
                Interlocked.Increment(ref _aiCalls);
                var res = await _generate!(BuildPrompt(pre, preCta, locale, culturalAdapt), ct).ConfigureAwait(false);
                if (res.Ok && ParseResponse(res.Text, n) is { } parsed)
                {
                    foreach (var note in parsed.Notes) if (!notes.Contains(note)) notes.Add(note);
                    return new(locale, parsed.Lines, parsed.Cta.Length > 0 ? parsed.Cta : preCta, changes, notes, true, "Gemini 각색");
                }
                msg = res.Ok ? "AI 응답 검증 실패 → 오프라인 글로서리" : "AI 실패(" + res.Message + ") → 오프라인 글로서리";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Program.Log("CulturalLocalization.Ai", ex); msg = "AI 예외 → 오프라인 글로서리"; }
        }
        var lang = locale.Code[..locale.Code.IndexOf('-')];
        var outLines = new List<string>(n);
        foreach (var s in pre) outLines.Add(Media.GlobalMultiDubbingEngine.Translate(s, lang));
        return new(locale, outLines, Media.GlobalMultiDubbingEngine.Translate(preCta, lang), changes, notes, false, msg);
    }

    private static bool TryNum(string s, out double v)
        => double.TryParse(s.Replace(",", "", StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);

    private static string Num(double v) => Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture);
}
