using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PromptFlowStudio.Services.Wan;

/// <summary>[v5.8.0] 대량 양산 배치 항목 — 순번 · 원본 변수 라인(표시용) · 치환 완료 프롬프트.
///   ⚠ 이름은 <b>BatchTemplateItem</b>(Services.Storyboard 의 런타임 <c>BatchItem</c> 클래스와 충돌 회피 · 양 네임스페이스 동시 import 컴포넌트 안전).</summary>
public sealed record BatchTemplateItem(int Index, string VariableValue, string InterpolatedPrompt);

/// <summary>
/// [PromptFlow Studio v5.8.0] B2B 대량 양산 템플릿 엔진 — 순수 BCL(<see cref="Regex"/>/문자열).
///   ▸ 플레이스홀더: {target}/{var}/{subject}=열0 · {location}/{place}=열1 · {style}/{mood}=열2 · {0}{1}{2}…=열 인덱스 · {n}/{index}=순번.
///   ▸ 각 라인(줄바꿈 구분)은 콤마 CSV 열을 허용 → 한 라인이 다중 변수(주제/장소/스타일)를 채운다.
///   ▸ 외부 SDK/파이썬/OpenCV 0. 각 라인 → 1개 <see cref="BatchTemplateItem"/> → FlowExecutionPipeline/WanRenderClient 세그먼트로 자동 전환.
/// </summary>
public static class BatchTemplateEngine
{
    // {name} 형태 토큰만 인식(중괄호+식별자). 미해결 토큰(알 수 없는 이름)은 원문 보존.
    private static readonly Regex TokenRx = new(@"\{(\w+)\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 템플릿과 변수 목록(줄바꿈 구분 · 각 줄은 콤마 CSV 열 허용)을 대량 치환하여 <see cref="BatchTemplateItem"/> 배열 생성.
    /// 변수 목록이 비면 템플릿 자체 1건("기본")을 반환.
    /// </summary>
    public static List<BatchTemplateItem> GenerateBatchItems(string? template, string? rawVariables)
    {
        var list = new List<BatchTemplateItem>();
        if (string.IsNullOrWhiteSpace(template)) return list;

        var lines = (rawVariables ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (lines.Length == 0)
        {
            list.Add(new BatchTemplateItem(1, "기본", Interpolate(template!, Array.Empty<string>(), 1)));
            return list;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var cols = lines[i].Split(',', StringSplitOptions.TrimEntries);
            list.Add(new BatchTemplateItem(i + 1, lines[i], Interpolate(template!, cols, i + 1)));
        }
        return list;
    }

    // 토큰을 열/순번으로 치환(멱등 · 미해결 토큰은 원문 유지 · 열 부족 시 빈 문자열).
    private static string Interpolate(string template, IReadOnlyList<string> cols, int ordinal)
        => TokenRx.Replace(template, m =>
        {
            var key = m.Groups[1].Value.ToLowerInvariant();
            return key switch
            {
                "target" or "var" or "subject" => Col(cols, 0),
                "location" or "place" => Col(cols, 1),
                "style" or "mood" => Col(cols, 2),
                "n" or "index" or "no" => ordinal.ToString(CultureInfo.InvariantCulture),
                _ => int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) ? Col(cols, idx) : m.Value,
            };
        });

    private static string Col(IReadOnlyList<string> cols, int i)
        => i >= 0 && i < cols.Count ? cols[i].Trim() : string.Empty;
}
