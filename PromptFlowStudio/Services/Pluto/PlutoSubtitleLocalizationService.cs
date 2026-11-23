using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Render;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>📖 Step 34 시리즈 용어집 항목 — 한국어 원어 → 로캘 고정 표기(없는 로캘은 번역기에 맡김).</summary>
public sealed record PlutoGlossaryEntry(string Source, string? EnUs, string? JaJp, string? EsEs)
{
    public const int MaxTermLength = 64;
    public const int MaxTargetLength = 96;

    /// <summary>LWW 시각(저장소가 채움).</summary>
    public DateTimeOffset UpdatedUtc { get; init; }

    /// <summary>로캘 고정 표기(미지정 · 미지원 로캘 → null).</summary>
    public string? TargetFor(string? locale) => PlutoMultiLanguageChannelService.NormalizeLocale(locale) switch
    {
        "en-US" => EnUs,
        "ja-JP" => JaJp,
        "es-ES" => EsEs,
        _ => null,
    };

    /// <summary>원어 정규화(NFC · 공백 정리 · 길이 상한 초과 → 빈 값).</summary>
    public static string NormalizeTerm(string? term)
    {
        var s = PlutoMultiLanguageChannelService.CollapseWhitespace(term);
        return s.Length > MaxTermLength || s.IndexOf("[[", StringComparison.Ordinal) >= 0 ? string.Empty : s;
    }

    /// <summary>대상 표기 정리(빈 값 → null · 자리표시자 모양 거부).</summary>
    public static string? CleanTarget(string? target)
    {
        var s = PlutoMultiLanguageChannelService.CollapseWhitespace(target);
        if (s.Length == 0 || s.Length > MaxTargetLength || s.IndexOf("[[", StringComparison.Ordinal) >= 0 || s.IndexOf("]]", StringComparison.Ordinal) >= 0) return null;
        return s;
    }
}

/// <summary>🛡️ 자리표시자 1개 — <see cref="Placeholder"/>(<c>[[G0]]</c>) ↔ 복원 표기.</summary>
public sealed record PlutoSentinelToken(string Placeholder, string Source, string Replacement, bool IsGlossary);

/// <summary>🛡️ 가림 결과 — 번역기에는 <see cref="MaskedText"/> 만 전달.</summary>
public sealed record PlutoSentinelMask(string MaskedText, IReadOnlyList<PlutoSentinelToken> Tokens);

/// <summary>
/// 🛡️ Step 34 용어집 · 고유명사 자리표시자 방패(순수 · 결정론).
///   ▸ 위치마다 가장 긴 용어 우선(<c>민준이네 카페</c> ⊃ <c>민준</c>) — 겹치는 짧은 용어가 긴 용어를 쪼개지 않는다.
///   ▸ 보호 대상(원문 그대로 복원): <c>@핸들</c> · <c>#태그</c> · 대문자 포함 라틴 단어(<c>BTS</c> · <c>iPhone</c>) · 원문에 이미 있던 <c>[[…]]</c>(자리표시자 충돌 방지).
///   ▸ 복원은 관대하게 수리(공백 · 대소문자 · 전각 괄호 <c>【【Ｇ０】】</c>) 하되, 누락 · 중복 · 미지 번호는 무결성 실패(null) → 호출자가 결정론 폴백.
/// </summary>
public static class PlutoGlossaryShield
{
    private static readonly Regex SentinelPattern = new(
        @"(?:\[\s*\[|［\s*［|【\s*【|〚|\[)\s*[GgＧｇ]\s*([0-9０-９]{1,3})\s*(?:\]\s*\]|］\s*］|】\s*】|〛|\])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    public static string PlaceholderOf(int index) => "[[G" + index.ToString(CultureInfo.InvariantCulture) + "]]";

    /// <summary>용어집 → 가장 긴 원어 우선 정렬(동률 서수) · 대상 표기가 없는 로캘의 용어는 제외(번역기가 옮긴다).</summary>
    public static List<(string Source, string Target)> OrderedTerms(IReadOnlyList<PlutoGlossaryEntry>? glossary, string? locale)
    {
        var list = new List<(string, string)>(glossary?.Count ?? 0);
        if (glossary is null) return list;
        foreach (var g in glossary)
        {
            if (g is null) continue;
            var src = PlutoGlossaryEntry.NormalizeTerm(g.Source);
            var dst = PlutoGlossaryEntry.CleanTarget(g.TargetFor(locale));
            if (src.Length == 0 || dst is null) continue;
            var dup = false;
            foreach (var x in list) if (string.Equals(x.Item1, src, StringComparison.Ordinal)) { dup = true; break; }
            if (!dup) list.Add((src, dst));
        }
        list.Sort(static (a, b) => a.Item1.Length != b.Item1.Length ? b.Item1.Length.CompareTo(a.Item1.Length) : string.CompareOrdinal(a.Item1, b.Item1));
        return list;
    }

    /// <summary>원문 → 자리표시자 가림.</summary>
    public static PlutoSentinelMask Mask(string? text, IReadOnlyList<PlutoGlossaryEntry>? glossary, string? locale)
    {
        var src = PlutoMultiLanguageChannelService.CollapseWhitespace(text);
        var tokens = new List<PlutoSentinelToken>(4);
        if (src.Length == 0) return new PlutoSentinelMask(string.Empty, tokens);
        var terms = OrderedTerms(glossary, locale);
        var sb = new StringBuilder(src.Length + 16);
        var i = 0;
        while (i < src.Length)
        {
            var len = 0;
            string? replacement = null;
            var glossaryHit = false;
            // ① 원문에 이미 있던 [[…]] — 자리표시자와 섞이지 않게 통째로 보호
            if (src[i] == '[' && i + 1 < src.Length && src[i + 1] == '[')
            {
                var close = src.IndexOf("]]", i + 2, StringComparison.Ordinal);
                if (close > 0 && close - i <= 40) len = close + 2 - i;
            }
            // ② 가장 긴 용어집 원어
            if (len == 0)
                foreach (var (s, t) in terms)
                    if (string.CompareOrdinal(src, i, s, 0, s.Length) == 0) { len = s.Length; replacement = t; glossaryHit = true; break; }
            // ③ @핸들 · #태그
            if (len == 0 && (src[i] == '@' || src[i] == '#') && (i == 0 || !char.IsLetterOrDigit(src[i - 1])))
            {
                var j = i + 1;
                while (j < src.Length && (char.IsLetterOrDigit(src[j]) || src[j] is '_' or '.' or '-')) j++;
                while (j > i + 1 && src[j - 1] is '.' or '-') j--;
                if (j > i + 1) len = j - i;
            }
            // ④ 대문자 포함 라틴 단어(브랜드 · 고유명사)
            if (len == 0 && IsAsciiLetter(src[i]) && (i == 0 || !IsAsciiLetterOrDigit(src[i - 1])))
            {
                var j = i;
                var upper = 0;
                while (j < src.Length && IsAsciiLetterOrDigit(src[j])) { if (src[j] is >= 'A' and <= 'Z') upper++; j++; }
                if (j - i >= 2 && upper > 0) len = j - i;
            }
            if (len == 0) { sb.Append(src[i]); i++; continue; }
            var original = src.Substring(i, len);
            var ph = PlaceholderOf(tokens.Count);
            tokens.Add(new PlutoSentinelToken(ph, original, replacement ?? original, glossaryHit));
            sb.Append(ph);
            i += len;
        }
        return new PlutoSentinelMask(sb.ToString(), tokens);
    }

    /// <summary>결정론 폴백 — 번역기 없이 용어집 표기만 치환(나머지 원문 유지).</summary>
    public static string ApplyGlossary(PlutoSentinelMask mask)
    {
        ArgumentNullException.ThrowIfNull(mask);
        // 단일 패스 정규 자리표시자 치환(띄어쓰기 수리 없음 — 남은 한국어 조사에 공백을 끼우지 않음 · 대상 표기 안의 '[[Gn]]' 재치환 없음)
        var m = mask.MaskedText;
        var sb = new StringBuilder(m.Length + 32);
        var i = 0;
        while (i < m.Length)
        {
            if (m[i] == '[' && i + 3 < m.Length && m[i + 1] == '[' && m[i + 2] == 'G')
            {
                var j = i + 3;
                var n = 0;
                while (j < m.Length && m[j] is >= '0' and <= '9' && j - i < 7) { n = n * 10 + (m[j] - '0'); j++; }
                if (j > i + 3 && j + 1 < m.Length && m[j] == ']' && m[j + 1] == ']' && n < mask.Tokens.Count)
                {
                    sb.Append(mask.Tokens[n].Replacement);
                    i = j + 2;
                    continue;
                }
            }
            sb.Append(m[i]);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// 번역 결과의 자리표시자 → 대상 표기 복원. 각 자리표시자가 정확히 1회 나타나야 성공(누락 · 중복 · 미지 번호 → null).
    /// <paramref name="repaired"/> = 공백 · 대소문자 · 전각 괄호 변형을 고쳐 받아들임.
    /// </summary>
    public static string? Restore(string? translated, PlutoSentinelMask mask, string? locale, out bool repaired)
    {
        ArgumentNullException.ThrowIfNull(mask);
        repaired = false;
        if (translated is null) return null;
        var seen = new int[mask.Tokens.Count];
        var bad = false;
        var fixedUp = false;
        var cjk = string.Equals(PlutoMultiLanguageChannelService.NormalizeLocale(locale), "ja-JP", StringComparison.Ordinal);
        string replaced;
        try
        {
            replaced = SentinelPattern.Replace(translated, m =>
            {
                if (!int.TryParse(ToAsciiDigits(m.Groups[1].Value), NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 0 || n >= seen.Length) { bad = true; return m.Value; }
                seen[n]++;
                if (!string.Equals(m.Value, mask.Tokens[n].Placeholder, StringComparison.Ordinal)) fixedUp = true;
                return "\u0001" + n.ToString(CultureInfo.InvariantCulture) + "\u0002";
            });
        }
        catch (RegexMatchTimeoutException) { return null; }
        if (bad) return null;
        for (var k = 0; k < seen.Length; k++) if (seen[k] != 1) return null;
        var sb = new StringBuilder(replaced.Length + 32);
        for (var i = 0; i < replaced.Length; i++)
        {
            if (replaced[i] != '\u0001') { sb.Append(replaced[i]); continue; }
            var end = replaced.IndexOf('\u0002', i);
            var n = int.Parse(replaced.AsSpan(i + 1, end - i - 1), NumberStyles.None, CultureInfo.InvariantCulture);
            var surface = mask.Tokens[n].Replacement;
            if (cjk)
            {
                // 일본어: 번역기가 자리표시자 둘레에 넣은 공백 제거(「ミンジュン は」 → 「ミンジュンは」)
                while (sb.Length > 0 && sb[^1] == ' ' && sb.Length >= 2 && !IsAsciiLetterOrDigit(sb[^2])) { sb.Length--; fixedUp = true; }
                sb.Append(surface);
                var j = end + 1;
                while (j < replaced.Length && replaced[j] == ' ' && j + 1 < replaced.Length && !IsAsciiLetterOrDigit(replaced[j + 1])) { j++; fixedUp = true; }
                i = j - 1;
                continue;
            }
            // 띄어쓰기 언어: 단어에 달라붙은 자리표시자("with[[G0]]and")를 단어 경계로 분리
            if (sb.Length > 0 && char.IsLetterOrDigit(sb[^1]) && surface.Length > 0 && char.IsLetterOrDigit(surface[0])) { sb.Append(' '); fixedUp = true; }
            sb.Append(surface);
            if (end + 1 < replaced.Length && char.IsLetterOrDigit(replaced[end + 1]) && surface.Length > 0 && char.IsLetterOrDigit(surface[^1])) { sb.Append(' '); fixedUp = true; }
            i = end;
        }
        repaired = fixedUp;
        return PlutoMultiLanguageChannelService.CollapseWhitespace(sb.ToString());
    }

    /// <summary>번역기 출력에서 자리표시자를 걷어낸 본문(한글 잔존 검사용).</summary>
    public static string StripSentinels(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        try { return SentinelPattern.Replace(text, " "); }
        catch (RegexMatchTimeoutException) { return text; }
    }

    private static string ToAsciiDigits(string s)
    {
        var a = s.ToCharArray();
        for (var i = 0; i < a.Length; i++) if (a[i] is >= '０' and <= '９') a[i] = (char)('0' + (a[i] - '０'));
        return new string(a);
    }

    private static bool IsAsciiLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
    private static bool IsAsciiLetterOrDigit(char c) => IsAsciiLetter(c) || c is >= '0' and <= '9';
}

/// <summary>🌐 번역기 요청 1건(큐 1개) — 자리표시자는 그대로 두라는 계약 · 낭독 글자 예산 · 문화 톤.</summary>
/// <param name="CharBudget">1.30x 로 장면 안에 끝나는 최대 낭독 글자 수(0 = 낭독 불가 구간 · 짧게).</param>
public sealed record PlutoTranslationRequest(string SourceLocale, string TargetLocale, string MaskedText, int CharBudget, double SegmentSeconds, string ToneHint, IReadOnlyList<string> Placeholders);

/// <summary>🌐 주입식 번역기(실패 → null · 취소 외 예외는 서비스가 격리).</summary>
public delegate Task<string?> PlutoTranslationProvider(PlutoTranslationRequest request, CancellationToken ct);

/// <summary>
/// 🌐 Step 34 비파괴 로캘 자막 트랙 — 원문(한국어 편집본)과 별도로 (맥락, 로캘)마다 보관.
/// <see cref="Texts"/>[i] = 표시 자막 i 의 장식 없는 번역 본문. <see cref="SourceFingerprint"/> 가 현재 원문과 다르면 낡은 트랙(렌더에 쓰지 않음).
/// </summary>
public sealed record PlutoLocaleSubtitleTrack(string ContextKey, string Locale, IReadOnlyList<string> Texts, ulong SourceFingerprint, ulong GlossaryFingerprint,
    int ProviderCueCount, int FallbackCueCount, int RejectedProviderCueCount, int OverBudgetCueCount, DateTimeOffset CreatedUtc)
{
    /// <summary>원문(본문 · 구간)이 번역 시점과 같은가.</summary>
    public bool IsFreshFor(IReadOnlyList<SubtitleCue> displayed, int episode, int total)
        => displayed is not null && displayed.Count == Texts.Count && PlutoSubtitleLocalizationService.SourceFingerprintOf(displayed, episode, total) == SourceFingerprint;
}

public enum PlutoTranslationStatus
{
    /// <summary>모든 한글 큐를 번역기로 옮김.</summary>
    Translated,
    /// <summary>[부분 구제] 일부 큐는 용어집 결정론 폴백.</summary>
    Partial,
    /// <summary>번역기 미구성 · 오프라인 · 회로 차단 → 전량 용어집 폴백(네트워크 0).</summary>
    OfflineFallback,
    /// <summary>기본 로캘 · 빈 자막 · 미등록 로캘.</summary>
    NotApplicable,
}

/// <summary>🌐 번역 결과(트랙 + 상태 + 한국어 1줄).</summary>
public sealed record PlutoLocaleTranslationResult(PlutoTranslationStatus Status, PlutoLocaleSubtitleTrack? Track, string MessageKorean);

/// <summary>🌐 Step 34 다국어 자막 번역(용어집 · 고유명사 보존 · 비파괴 트랙).</summary>
public interface IPlutoSubtitleLocalizationService
{
    /// <summary>표시 자막(장식 포함) → 대상 로캘 트랙(원문 불변 · 저장소에 보관 · 취소만 전파).</summary>
    Task<PlutoLocaleTranslationResult> TranslateAsync(string contextKey, IReadOnlyList<SubtitleCue> displayed, string targetLocale, IReadOnlyList<PlutoGlossaryEntry>? glossary,
        int episode = 0, int total = 0, CancellationToken ct = default);

    /// <summary>보관된 트랙(없음 → null · 신선도는 <see cref="PlutoLocaleSubtitleTrack.IsFreshFor"/>).</summary>
    PlutoLocaleSubtitleTrack? GetTrack(string? contextKey, string? locale);

    /// <summary>번역기 호출 가능(구성 · 네트워크 허용 · 회로 닫힘).</summary>
    bool IsProviderReachable { get; }
}

/// <summary>
/// 🌐 <see cref="IPlutoSubtitleLocalizationService"/> 구현.
///   ▸ 큐마다 ① 가장 긴 용어집 · 고유명사 가림 ② 낭독 글자 예산(<c>TargetCharsPerSecond × 1.30 × 가용 초</c>) · 문화 톤 전달 ③ 자리표시자 수리/무결성 검증 ④ 대상 표기 복원.
///   ▸ 번역기 결과 거부(→ 그 큐만 용어집 폴백): 자리표시자 누락/중복/미지 번호 · 한글 잔존(원문 되돌려주기) · 빈 값 · 비정상 길이.
///   ▸ 🔒 오프라인: 번역기 미주입 · 미구성 · <c>PROMPTFLOW_PLUTO_DRIVE=off</c>(네트워크 번역기) · <c>PROMPTFLOW_DISABLE_SUBTITLE_TRANSLATION=1</c> · 회로 차단 → 호출 0 · 즉시 결정론 폴백(throw 없음).
///   ▸ 트랙은 (맥락, 로캘) 키로 보관(최대 <see cref="MaxTracks"/> · 오래된 것부터 축출) — 원문 지문이 바뀌면 낡은 트랙으로 판정되어 렌더에 쓰이지 않는다.
/// </summary>
public sealed class PlutoSubtitleLocalizationService : IPlutoSubtitleLocalizationService
{
    public const string ProviderName = "subtitle-translate";
    public const string DisableEnvVar = "PROMPTFLOW_DISABLE_SUBTITLE_TRANSLATION";
    public const int MaxTracks = 64;
    public static readonly TimeSpan DefaultPerCueTimeout = TimeSpan.FromSeconds(12);

    private static readonly (string Locale, string Tone)[] ToneHints =
    {
        ("en-US", "American English Shorts voice: direct, value-first, conversational, contractions OK, no honorific filler."),
        ("ja-JP", "自然な日本語のショート動画ナレーション: です・ます調、余韻と好奇心を残す高文脈表現、単語間にスペースを入れない。"),
        ("es-ES", "Español de España para Shorts: cálido, emotivo y con urgencia, tuteo (tú/vosotros), sin latinoamericanismos."),
    };

    private readonly IPlutoMultiLanguageChannelService _channels;
    private readonly PlutoTranslationProvider? _provider;
    private readonly ProviderCircuitBreaker? _breaker;
    private readonly bool _providerUsesNetwork;
    private readonly object _sync = new();
    private readonly Dictionary<string, PlutoLocaleSubtitleTrack> _tracks = new(StringComparer.Ordinal);

    /// <param name="provider">번역기(null → 항상 용어집 폴백).</param>
    /// <param name="providerUsesNetwork">true = 실 네트워크 번역기(테스트 하네스 <c>PROMPTFLOW_PLUTO_DRIVE=off</c> 에서 차단) · false = 로컬/결정론 번역기.</param>
    public PlutoSubtitleLocalizationService(IPlutoMultiLanguageChannelService? channels, PlutoTranslationProvider? provider = null, ProviderCircuitBreaker? breaker = null, bool providerUsesNetwork = true)
    {
        _channels = channels ?? new PlutoMultiLanguageChannelService();
        _provider = provider;
        _breaker = breaker;
        _providerUsesNetwork = providerUsesNetwork;
    }

    /// <summary>번역기 구성 여부(예: LLM 키 설정) — false → 호출 없이 폴백.</summary>
    public Func<bool>? ProviderConfigured { get; init; }

    /// <summary>네트워크 허용 판정(테스트 주입 · 기본 = 환경 변수).</summary>
    public Func<bool>? NetworkAllowed { get; init; }

    public Func<DateTimeOffset>? UtcNowProvider { get; init; }

    public TimeSpan PerCueTimeout { get; init; } = DefaultPerCueTimeout;

    /// <summary>🔒 환경 격리 — 전면 차단(<c>PROMPTFLOW_DISABLE_SUBTITLE_TRANSLATION=1</c>) · 네트워크 번역기는 <c>PROMPTFLOW_PLUTO_DRIVE=off</c> 에서도 차단.</summary>
    public static bool EnvironmentAllowsNetwork()
        => !string.Equals(Environment.GetEnvironmentVariable(DisableEnvVar), "1", StringComparison.Ordinal)
           && !string.Equals(Environment.GetEnvironmentVariable(PlutoDriveStorageManager.DriveEnvVar), "off", StringComparison.OrdinalIgnoreCase);

    public bool IsProviderReachable
    {
        get
        {
            if (_provider is null) return false;
            if (string.Equals(Environment.GetEnvironmentVariable(DisableEnvVar), "1", StringComparison.Ordinal)) return false;
            if (_providerUsesNetwork && !(NetworkAllowed?.Invoke() ?? EnvironmentAllowsNetwork())) return false;
            if (ProviderConfigured is { } cfg && !SafeBool(cfg)) return false;
            return _breaker is null || _breaker.GetSnapshot(ProviderName).State != ProviderCircuitState.Open;
        }
    }

    /// <summary>로캘 문화 톤 힌트(사용자 정의 로캘 → 영어).</summary>
    public static string ToneHintFor(string? locale)
    {
        var norm = PlutoMultiLanguageChannelService.NormalizeLocale(locale);
        foreach (var t in ToneHints) if (string.Equals(t.Locale, norm, StringComparison.Ordinal)) return t.Tone;
        return ToneHints[0].Tone;
    }

    /// <summary>큐 낭독 글자 예산(1.30x 상한 · 장면 전환 가드 제외).</summary>
    public int CharBudgetFor(string locale, double segmentSeconds) => _channels.ComputeTtsBudget(locale, string.Empty, segmentSeconds).MaxUnitsAtMaxRate;

    // ── 지문 ──

    /// <summary>원문 지문(장식 걷은 본문 + ms 구간 · FNV-1a 64) — 원문 편집 · 재타이밍 · 큐 수 변화 감지.</summary>
    public static ulong SourceFingerprintOf(IReadOnlyList<SubtitleCue> displayed, int episode, int total)
    {
        ArgumentNullException.ThrowIfNull(displayed);
        var h = 14695981039346656037UL;
        h = Mix(h, (ulong)displayed.Count);
        for (var i = 0; i < displayed.Count; i++)
        {
            var c = displayed[i];
            var core = PlutoSeriesSubtitleOverlay.StripDecoration(c.Text, i, displayed.Count, episode, total);
            for (var k = 0; k < core.Length; k++) h = Mix(h, core[k]);
            h = Mix(h, 0x1F);
            h = Mix(h, (ulong)(long)Math.Round(c.EffectiveStartSec * 1000.0));
            h = Mix(h, (ulong)(long)Math.Round(c.EffectiveEndSec * 1000.0));
        }
        return h;
    }

    /// <summary>용어집 지문(로캘 표기 기준) — 용어 변경 후 다시 번역 권고 판정.</summary>
    public static ulong GlossaryFingerprintOf(IReadOnlyList<PlutoGlossaryEntry>? glossary, string? locale)
    {
        var h = 14695981039346656037UL;
        foreach (var (s, t) in PlutoGlossaryShield.OrderedTerms(glossary, locale))
        {
            for (var k = 0; k < s.Length; k++) h = Mix(h, s[k]);
            h = Mix(h, 0x1E);
            for (var k = 0; k < t.Length; k++) h = Mix(h, t[k]);
            h = Mix(h, 0x1F);
        }
        return h;
    }

    private static ulong Mix(ulong h, ulong v)
    {
        for (var b = 0; b < 8; b++) { h ^= (byte)(v >> (b * 8)); h *= 1099511628211UL; }
        return h;
    }

    // ── 번역 ──

    public async Task<PlutoLocaleTranslationResult> TranslateAsync(string contextKey, IReadOnlyList<SubtitleCue> displayed, string targetLocale, IReadOnlyList<PlutoGlossaryEntry>? glossary,
        int episode = 0, int total = 0, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(displayed);
        var key = string.IsNullOrWhiteSpace(contextKey) ? "default" : contextKey.Trim();
        var profile = PlutoLocaleSubtitleFitter.ActiveProfile(_channels, targetLocale);
        if (profile is null || displayed.Count == 0)
            return new PlutoLocaleTranslationResult(PlutoTranslationStatus.NotApplicable, null, "🌐 번역 대상 아님(한국어 · 빈 자막 · 미등록 로캘)");
        var loc = profile.LocaleCode;
        var count = displayed.Count;
        var texts = new string[count];
        int viaProvider = 0, fallback = 0, rejected = 0, overBudget = 0, hangulCues = 0;
        var canCall = IsProviderReachable;
        var tone = ToneHintFor(loc);
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var c = displayed[i];
            var core = PlutoSeriesSubtitleOverlay.StripDecoration(c.Text, i, count, episode, total);
            if (!PlutoYouTubePublishPackService.ContainsHangul(core)) { texts[i] = core; continue; }   // 이미 대상어 · 빈 큐
            hangulCues++;
            var seg = c.EffectiveEndSec - c.EffectiveStartSec;
            var budget = CharBudgetFor(loc, seg);
            var mask = PlutoGlossaryShield.Mask(core, glossary, loc);
            string? text = null;
            if (canCall && _breaker is not null && !_breaker.TryAcquire(ProviderName, out _)) canCall = false;   // ⚡ 회로 열림 → 남은 큐 전부 즉시 폴백
            if (canCall)
            {
                var placeholders = new string[mask.Tokens.Count];
                for (var k = 0; k < placeholders.Length; k++) placeholders[k] = mask.Tokens[k].Placeholder;
                var request = new PlutoTranslationRequest(PlutoMultiLanguageChannelService.DefaultLocale, loc, mask.MaskedText, budget, seg > 0 ? seg : 0, tone, placeholders);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(PerCueTimeout);
                try
                {
                    var raw = await _provider!(request, cts.Token).ConfigureAwait(false);
                    if (raw is null) { _breaker?.RecordTransientFailure(ProviderName, "empty"); rejected++; }
                    else if (Validate(raw, mask, loc) is { } ok) { text = ok; viaProvider++; _breaker?.RecordSuccess(ProviderName); }
                    else { _breaker?.RecordReachable(ProviderName); rejected++; }   // 응답은 왔으나 무결성 실패 — 회로는 열지 않음
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { _breaker?.RecordAbandoned(ProviderName); throw; }
                catch (Exception ex)   // 시간 초과 · 전송 오류 → 이 큐만 폴백(격리)
                {
                    rejected++;
                    if (_breaker?.RecordTransientFailure(ProviderName, ex.Message) == true) canCall = false;
                }
            }
            if (text is null) { text = PlutoGlossaryShield.ApplyGlossary(mask); fallback++; }
            if (budget > 0 && PlutoMultiLanguageChannelService.CountSpeechUnits(text) > budget) overBudget++;
            texts[i] = text;
        }
        var track = new PlutoLocaleSubtitleTrack(key, loc, texts, SourceFingerprintOf(displayed, episode, total), GlossaryFingerprintOf(glossary, loc),
            viaProvider, fallback, rejected, overBudget, UtcNowProvider?.Invoke() ?? DateTimeOffset.UtcNow);
        Store(track);
        var inv = CultureInfo.InvariantCulture;
        PlutoTranslationStatus status;
        string msg;
        if (hangulCues == 0) { status = PlutoTranslationStatus.Translated; msg = "🌐 " + loc + " 번역할 한글 자막 없음"; }
        else if (fallback == 0) { status = PlutoTranslationStatus.Translated; msg = "🌐 " + loc + " 자동 번역 " + viaProvider.ToString(inv) + "컷 · 용어집 보존"; }
        else if (viaProvider > 0) { status = PlutoTranslationStatus.Partial; msg = "🌐 " + loc + " 번역 " + viaProvider.ToString(inv) + "컷 · [부분 구제] 용어집 폴백 " + fallback.ToString(inv) + "컷"; }
        else { status = PlutoTranslationStatus.OfflineFallback; msg = "🌐 " + loc + " 번역기 오프라인 — 용어집 치환만 적용 " + fallback.ToString(inv) + "컷(남은 한글은 직접 번역)"; }
        if (overBudget > 0) msg += " · 🎙️ 예산 초과 " + overBudget.ToString(inv) + "컷";
        return new PlutoLocaleTranslationResult(status, track, msg);
    }

    /// <summary>번역기 출력 검증 + 복원(실패 → null).</summary>
    internal static string? Validate(string raw, PlutoSentinelMask mask, string locale)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0 || trimmed.Length > Math.Max(240, mask.MaskedText.Length * 8)) return null;   // 장황한 설명 · 프롬프트 누출 차단
        // 🪞 되돌려주기 가드 — 자리표시자를 뺀 본문에 한글이 남으면 번역이 아님(경고를 거짓으로 지우지 않는다)
        if (PlutoYouTubePublishPackService.ContainsHangul(PlutoGlossaryShield.StripSentinels(trimmed))) return null;
        var restored = PlutoGlossaryShield.Restore(trimmed, mask, locale, out _);
        return string.IsNullOrWhiteSpace(restored) ? null : restored;
    }

    private void Store(PlutoLocaleSubtitleTrack track)
    {
        lock (_sync)
        {
            _tracks[TrackKey(track.ContextKey, track.Locale)] = track;
            while (_tracks.Count > MaxTracks)
            {
                string? oldest = null;
                var at = DateTimeOffset.MaxValue;
                foreach (var kv in _tracks) if (kv.Value.CreatedUtc < at) { at = kv.Value.CreatedUtc; oldest = kv.Key; }
                if (oldest is null) break;
                _tracks.Remove(oldest);
            }
        }
    }

    public PlutoLocaleSubtitleTrack? GetTrack(string? contextKey, string? locale)
    {
        var loc = PlutoMultiLanguageChannelService.NormalizeLocale(locale);
        if (loc is null) return null;
        var key = string.IsNullOrWhiteSpace(contextKey) ? "default" : contextKey.Trim();
        lock (_sync) return _tracks.TryGetValue(TrackKey(key, loc), out var t) ? t : null;
    }

    private static string TrackKey(string context, string locale) => context + "\u001F" + locale;

    private static bool SafeBool(Func<bool> f)
    {
        try { return f(); }
        catch (Exception) { return false; }
    }

    // ── 기본 번역기(LLM) ──

    /// <summary>
    /// 🧠 LLM(<see cref="IPlutoLlmClient"/>) 번역기 — JSON <c>{"text":"…"}</c> 응답 스키마 즉시 검증 · 단계 예산 10초 · 실패 → null.
    /// 미구성(<see cref="IPlutoLlmClient.IsAvailable"/> false)이면 호출하지 않는다.
    /// </summary>
    public static PlutoTranslationProvider FromLlm(IPlutoLlmClient llm)
    {
        ArgumentNullException.ThrowIfNull(llm);
        return async (req, ct) =>
        {
            if (!llm.IsAvailable) return null;
            const string system = "You are a professional subtitle translator for short-form vertical video. Translate Korean subtitle text into the target locale. "
                + "Placeholders like [[G0]] are fixed proper nouns or glossary terms: copy every placeholder exactly once, unchanged, in natural position. "
                + "Respect the speech character budget (count letters, not spaces or punctuation). Reply ONLY with JSON: {\"text\":\"...\"}.";
            var user = "Target locale: " + req.TargetLocale + "\nTone: " + req.ToneHint + "\nCharacter budget: " + req.CharBudget.ToString(CultureInfo.InvariantCulture)
                + "\nPlaceholders: " + string.Join(", ", req.Placeholders) + "\nText: " + req.MaskedText;
            var json = await PlutoLlmBridge.TryCompleteAsync(llm, system, user, 0.2, ct, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            return ParseProviderJson(json);
        };
    }

    /// <summary>LLM JSON 응답 → 번역문(스키마 위반 → null).</summary>
    public static string? ParseProviderJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(PlutoJsonSanitizer.Sanitize(json));
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
