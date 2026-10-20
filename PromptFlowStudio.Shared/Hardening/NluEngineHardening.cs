using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PromptFlowStudio.Shared.Hardening;

/// <summary>
/// [PromptFlow Studio v4.4.1] NLU 코어 예외/정규식 안전성(Task 1 · 순수 BCL · thread-safe).
///   ▸ Null/Empty/이모지/특수문자 혼합 입력에 Safe Navigation(?., ??) 철저 적용 → NRE 원천 차단.
///   ▸ 모든 파싱 정규식에 <b>matchTimeout</b> 부여 → ReDoS/CPU 프리징 차단(타임아웃 시 문자 기반 안전 폴백).
/// </summary>
public static class NluEngineHardening
{
    /// <summary>입력 상한(초장문 방어 · 이 길이로 절단).</summary>
    public const int MaxInputChars = 20000;
    /// <summary>정규식 매치 타임아웃(ReDoS 차단).</summary>
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    // 토큰 추출(선형 · 타임아웃 부여). Compiled + timeout.
    private static readonly Regex TokenRegex = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled, RegexTimeout);

    // 인텐트 키워드(정규식 아님 · Contains 로 안전 매칭).
    private static readonly (string Intent, string[] Keys)[] IntentKeywords =
    {
        ("baby", new[] { "아기", "baby", "육아", "숏츠" }),
        ("corporate", new[] { "기업", "사옥", "홍보", "b2b", "corporate" }),
        ("automotive", new[] { "자동차", "검사", "장비", "plc", "다이나모", "pods" }),
        ("voice", new[] { "보이스", "나레이션", "더빙", "voice" }),
        ("deploy", new[] { "배포", "스토어", "빌드", "deploy", "store" }),
    };

    /// <summary>입력 정규화 — null→"", 공백 정리, 상한 절단, 제어문자 제거(이모지/한글/특수문자 보존).</summary>
    public static string SafeNormalize(string? input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        var s = input.Length > MaxInputChars ? input.Substring(0, MaxInputChars) : input;   // 초장문 방어
        var sb = new StringBuilder(s.Length);
        var prevSpace = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch)) { if (!prevSpace) { sb.Append(' '); prevSpace = true; } continue; }   // 탭/개행 포함 → 단일 공백(단어 경계 보존)
            if (char.IsControl(ch)) { continue; }                                                            // 비공백 제어문자만 제거
            sb.Append(ch); prevSpace = false;
        }
        return sb.ToString().Trim();
    }

    /// <summary>ReDoS 안전 정규식 매치 — 타임아웃/잘못된 패턴은 false(예외 전파 없음).</summary>
    public static bool SafeIsMatch(string? input, string? pattern, TimeSpan? timeout = null)
    {
        if (input is null || string.IsNullOrEmpty(pattern)) return false;
        try { return Regex.IsMatch(input, pattern, RegexOptions.None, timeout ?? RegexTimeout); }
        catch (RegexMatchTimeoutException) { return false; }   // ReDoS 차단
        catch (ArgumentException) { return false; }             // 잘못된 패턴
    }

    /// <summary>인텐트 감지(Contains 기반 · 정규식 없음 · 항상 안전).</summary>
    public static IReadOnlyList<string> DetectIntents(string? normalized)
    {
        var s = (normalized ?? "").ToLowerInvariant();
        if (s.Length == 0) return Array.Empty<string>();
        var found = new List<string>();
        foreach (var (intent, keys) in IntentKeywords)
            if (keys.Any(k => s.Contains(k, StringComparison.Ordinal))) found.Add(intent);
        return found;
    }

    /// <summary>
    /// 안전 파싱 — 어떤 입력(null/이모지/초장문)에도 예외 없이 결과 반환.
    ///   토큰 정규식 타임아웃 시 <see cref="NluParseResult.TimedOut"/>=true + 문자 기반 폴백 토큰화.
    /// </summary>
    public static NluParseResult SafeParse(string? input)
    {
        var norm = SafeNormalize(input);
        if (norm.Length == 0) return new NluParseResult(true, false, Array.Empty<string>(), Array.Empty<string>(), "");
        try
        {
            var tokens = new List<string>();
            foreach (Match m in TokenRegex.Matches(norm)) tokens.Add(m.Value);
            return new NluParseResult(true, false, tokens, DetectIntents(norm), norm);
        }
        catch (RegexMatchTimeoutException)
        {
            return new NluParseResult(true, true, CharTokens(norm), DetectIntents(norm), norm);   // 안전 폴백
        }
        catch (Exception)
        {
            return new NluParseResult(false, false, Array.Empty<string>(), Array.Empty<string>(), norm);
        }
    }

    // 정규식 없는 문자 기반 토큰화(폴백 · ReDoS 불가).
    private static IReadOnlyList<string> CharTokens(string s)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list;
    }
}
