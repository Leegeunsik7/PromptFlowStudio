using System;
using System.Globalization;

namespace PromptFlowStudio.Services.Gallery;

/// <summary>
/// ⏳ 갤러리 일괄 작업(번들 내보내기 · 일괄 삭제) 진행 보고 — <see cref="IProgress{T}"/> 페이로드(값 타입 · 무할당).
/// <paramref name="Completed"/> = 완료 수, <paramref name="CurrentFileName"/> = 처리 중 파일명(완료 보고 시 null).
/// </summary>
public readonly record struct BatchProgressReport(int Completed, int Total, string? CurrentFileName)
{
    /// <summary>진행률 0~1(총 0 → 0).</summary>
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Completed / Total, 0, 1);

    /// <summary>텍스트 막대 — <c>[████████░░░░]</c>(기본 12칸).</summary>
    public string RenderBar(int width = 12)
    {
        if (width < 1) width = 1;
        var filled = (int)Math.Round(Fraction * width, MidpointRounding.AwayFromZero);
        return string.Create(width + 2, filled, static (span, f) =>
        {
            span[0] = '[';
            for (var i = 1; i < span.Length - 1; i++) span[i] = i <= f ? '█' : '░';
            span[^1] = ']';
        });
    }

    /// <summary>모달 표시 문구 — <c>[████████░░░░] 6/12 Processing... (name)</c>.</summary>
    public string Format(int width = 12)
    {
        var inv = CultureInfo.InvariantCulture;
        var s = RenderBar(width) + " " + Completed.ToString(inv) + "/" + Total.ToString(inv) + " Processing...";
        return string.IsNullOrEmpty(CurrentFileName) ? s : s + " (" + CurrentFileName + ")";
    }
}

/// <summary>🗑️ 일괄 삭제 결과 — 취소 시에도 이미 삭제된 항목 수 보존([부분 구제]).</summary>
public readonly record struct BatchDeleteResult(int Deleted, int Requested, bool Canceled);
