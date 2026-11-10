using System;
using System.Collections.Generic;
using System.Text;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>
/// 🩹 로컬 SLM(3B~7B) JSON 자가 치유 — 파싱 직전 단일 패스(문자열 인식 스캐너 · 정규식 미사용 → 문자열 내부 ", }" 오손 없음).
///   ① <c>&lt;think&gt;</c> 블록 · 마크다운 펜스(<c>```json … ```</c>) · 대화형 서두/후미 제거 → 첫 그럴듯한 <c>{</c>/<c>[</c>부터 최상위 값 종료까지 추출
///   ② 닫는 괄호 앞 후행 쉼표 제거 · ③ 잘린 출력의 미닫힘 문자열/키/괄호 자동 보정 · ④ 문자열 내부 비이스케이프 개행/제어문자 → 공백.
/// 정상 JSON은 그대로 통과. JSON 시작점이 없으면 트림된 원문 반환(호출측 스키마 검증이 폴백 결정).
/// </summary>
public static class PlutoJsonSanitizer
{
    public static string Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var text = StripThink(raw.Trim());
        var fenced = ExtractFence(text);
        if (fenced is not null && FindStart(fenced) >= 0) text = fenced;
        var start = FindStart(text);
        return start < 0 ? text.Trim() : Heal(text, start);
    }

    private static string StripThink(string s)
    {
        var end = s.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        return end >= 0 && s.IndexOf("<think>", StringComparison.OrdinalIgnoreCase) is >= 0 and var a && a < end ? (s[..a] + s[(end + 8)..]).Trim() : s;
    }

    /// <summary>첫 코드펜스 본문(언어 태그 줄 제외) — 닫는 펜스 없으면(잘림) 끝까지.</summary>
    private static string? ExtractFence(string s)
    {
        var f = s.IndexOf("```", StringComparison.Ordinal);
        if (f < 0) return null;
        var nl = s.IndexOf('\n', f + 3);
        if (nl < 0) return null;
        var close = s.IndexOf("```", nl + 1, StringComparison.Ordinal);
        return close < 0 ? s[(nl + 1)..] : s[(nl + 1)..close];
    }

    /// <summary>산문 속 괄호(예: "[참고]")를 건너뛰고 JSON 값으로 그럴듯한 첫 시작점.</summary>
    private static int FindStart(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is not ('{' or '[')) continue;
            var j = i + 1;
            while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
            if (j >= s.Length) return i;   // 잘린 직후 — 보정 대상
            var n = s[j];
            if (c == '{' ? n is '"' or '}' : n is '{' or '[' or '"' or ']' or '-' or 't' or 'f' or 'n' || char.IsAsciiDigit(n)) return i;
        }
        return -1;
    }

    private static string Heal(string s, int start)
    {
        var sb = new StringBuilder(s.Length - start + 8);
        var stack = new Stack<char>(8);   // '{' | '['
        bool inStr = false, esc = false, keyPending = false, expectKey = false;
        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (inStr)
            {
                if (esc) { esc = false; sb.Append(c); continue; }
                if (c == '\\') { esc = true; sb.Append(c); continue; }
                if (c == '"')
                {
                    inStr = false;
                    sb.Append(c);
                    if (expectKey) { keyPending = true; expectKey = false; }
                    continue;
                }
                sb.Append(c < ' ' ? ' ' : c);   // ④ 비이스케이프 개행/탭/제어문자 정규화
                continue;
            }
            switch (c)
            {
                case '"':
                    inStr = true;
                    sb.Append(c);
                    break;
                case '{':
                case '[':
                    stack.Push(c);
                    expectKey = c == '{';
                    sb.Append(c);
                    break;
                case '}':
                case ']':
                    if (stack.Count == 0) return Finish(sb);
                    var want = c == '}' ? '{' : '[';
                    while (stack.Count > 0 && stack.Peek() != want) Close(sb, stack.Pop(), ref keyPending);   // 엇갈린 괄호 → 안쪽 먼저 닫음
                    if (stack.Count == 0) return Finish(sb);
                    Close(sb, stack.Pop(), ref keyPending);
                    expectKey = false;
                    if (stack.Count == 0) return Finish(sb);   // 최상위 값 종료 → 후미 산문 폐기
                    break;
                case ',':
                    sb.Append(c);
                    expectKey = stack.Count > 0 && stack.Peek() == '{';
                    keyPending = false;
                    break;
                case ':':
                    sb.Append(c);
                    keyPending = false;
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        // ③ 잘림 — 문자열/키/괄호 순서대로 봉합
        if (inStr)
        {
            if (esc) sb.Length--;   // 미완 이스케이프 제거
            sb.Append('"');
            if (expectKey) { keyPending = true; expectKey = false; }
        }
        while (stack.Count > 0) Close(sb, stack.Pop(), ref keyPending);
        return Finish(sb);
    }

    /// <summary>후행 쉼표 제거 · 값 없는 키/콜론 보정 후 닫기.</summary>
    private static void Close(StringBuilder sb, char open, ref bool keyPending)
    {
        var end = sb.Length;   // 정상 JSON 서식 보존 — 보정이 필요할 때만 후미 공백 절단
        while (end > 0 && char.IsWhiteSpace(sb[end - 1])) end--;
        if (end > 0 && sb[end - 1] == ',') sb.Length = end - 1;
        else if (keyPending || (end > 0 && sb[end - 1] == ':')) sb.Length = end;
        if (keyPending) { sb.Append(":null"); keyPending = false; }
        else if (sb.Length > 0 && sb[^1] == ':') sb.Append("null");
        sb.Append(open == '{' ? '}' : ']');
    }

    private static string Finish(StringBuilder sb) => sb.ToString().Trim();
}
