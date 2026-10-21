using PromptFlowStudio.Services.Talkshow;

namespace PromptFlowStudio.Services.LiveShowroom;

public enum ComplianceAction : byte { Replace, Block }
public enum PriceVerdict : byte { Ok, SaleNotBelowList, PercentMismatch, TotalMismatch, Invalid }

/// <summary>v17.68.0 심의 위반 1건(로그 링).</summary>
public struct ComplianceViolation
{
    public byte Rule;
    public ComplianceAction Action;
    public sbyte Avatar;
    public long AtFrame;
    public int TextLen;
}

/// <summary>
/// [PromptFlow Studio v17.68.0] 전자상거래 광고 심의 &amp; 가격 정합성 세이프티 가드(무할당).
///   ▸ 금지어 스캔: 규칙 표(과장 · 의료 효능 · 최상급 · 보장) — 공백 무시 · 라틴 대소문자 무시 문자 단위 매칭(띄어쓰기 · "100 %" 변형 탐지).
///     Replace = 안전 대체어 치환 · Block = 발화 전체 차단(질병 치료 · 의약품 오인). v17.65 토크쇼 <see cref="ITalkLineFilter"/> 로 대본 · 애드리브 · 커스텀 대사 전부 통과.
///   ▸ 가격 정합성: 0 &lt; 할인가 &lt; 정가 · 표시 할인율 ∈ {floor, round}((정가 − 할인가) / 정가 × 100) · 묶음 합계 = 할인가 × 수량 — 불일치면 쇼핑 배너 즉시 비노출.
/// </summary>
public sealed class EcommerceAdComplianceSafetyGuard : ITalkLineFilter
{
    public const int LogRing = 32, LineCap = 256;

    private readonly record struct Rule(string Pattern, string Replacement, ComplianceAction Action, string Category);

    private static readonly Rule[] Rules =
    {
        new("완치", "증상 관리에 도움", ComplianceAction.Replace, "의료 효능"),
        new("100%보장", "만족을 위해 노력", ComplianceAction.Replace, "보장 과장"),
        new("백퍼센트보장", "만족을 위해 노력", ComplianceAction.Replace, "보장 과장"),
        new("100%효과", "많은 분들이 만족", ComplianceAction.Replace, "효과 과장"),
        new("국내유일", "차별화된", ComplianceAction.Replace, "최상급"),
        new("세계최초", "새롭게 선보이는", ComplianceAction.Replace, "최상급"),
        new("업계최저가", "합리적인 가격", ComplianceAction.Replace, "최상급"),
        new("부작용없", "사용 전 주의사항을 확인", ComplianceAction.Replace, "안전성 과장"),
        new("부작용제로", "사용 전 주의사항을 확인", ComplianceAction.Replace, "안전성 과장"),
        new("즉시효과", "꾸준히 사용하시면", ComplianceAction.Replace, "효과 과장"),
        new("기적의", "특별한", ComplianceAction.Replace, "과장"),
        new("무조건", "대부분", ComplianceAction.Replace, "단정"),
        new("guaranteed", "designed", ComplianceAction.Replace, "보장 과장"),
        new("암치료", "", ComplianceAction.Block, "질병 치료 표방"),
        new("질병을치료", "", ComplianceAction.Block, "질병 치료 표방"),
        new("의약품", "", ComplianceAction.Block, "의약품 오인"),
        new("살이빠지는약", "", ComplianceAction.Block, "의약품 오인"),
    };

    private readonly ComplianceViolation[] _log = new ComplianceViolation[LogRing];
    private readonly char[] _logText = new char[LogRing * LineCap];
    private int _logHead, _logN;

    public bool Enabled { get; set; } = true;
    public long Scanned { get; private set; }
    public long Replaced { get; private set; }
    public long Blocked { get; private set; }
    public long PriceChecks { get; private set; }
    public long PriceRejected { get; private set; }
    public PriceVerdict LastPrice { get; private set; }
    public long Frame { get; set; }
    public int RuleCount => Rules.Length;
    public static string RuleCategory(int r) => Rules[r].Category;
    public static string RulePattern(int r) => Rules[r].Pattern;
    public int LogCount => _logN;
    public ref readonly ComplianceViolation Log(int i) => ref _log[(_logHead - 1 - i + 2 * LogRing) % LogRing];
    public ReadOnlySpan<char> LogText(int i) { var k = (_logHead - 1 - i + 2 * LogRing) % LogRing; return _logText.AsSpan(k * LineCap, _log[k].TextLen); }

    public void ResetMetrics() { Scanned = Replaced = Blocked = PriceChecks = PriceRejected = 0; }

    // ══════════════════════ 금지어(무할당) ══════════════════════
    /// <summary>위치 i 에서 규칙 패턴 일치 시 원문 소비 길이(공백 건너뜀) · 불일치 0.</summary>
    private static int MatchAt(ReadOnlySpan<char> s, int i, string pat)
    {
        int j = i, k = 0;
        while (k < pat.Length)
        {
            if (j >= s.Length) return 0;
            var c = s[j];
            if (char.IsWhiteSpace(c) && j > i) { j++; continue; }
            if (char.ToLowerInvariant(c) != pat[k]) return 0;
            j++; k++;
        }
        return j - i;
    }

    /// <summary>첫 위반 규칙 인덱스(없으면 −1).</summary>
    public static int FirstViolation(ReadOnlySpan<char> line)
    {
        for (var i = 0; i < line.Length; i++)
            for (var r = 0; r < Rules.Length; r++)
                if (MatchAt(line, i, Rules[r].Pattern) > 0) return r;
        return -1;
    }

    /// <summary>스캔 · 치환 → dst 길이(−1 = 차단). 위반이 없으면 원문 복사.</summary>
    public int Sanitize(ReadOnlySpan<char> line, Span<char> dst, out int firstRule, out bool block)
    {
        Scanned++;
        firstRule = -1; block = false;
        var n = 0;
        for (var i = 0; i < line.Length;)
        {
            var hit = -1; var len = 0;
            for (var r = 0; r < Rules.Length && hit < 0; r++) { var m = MatchAt(line, i, Rules[r].Pattern); if (m > 0) { hit = r; len = m; } }
            if (hit < 0) { if (n < dst.Length) dst[n++] = line[i]; i++; continue; }
            if (firstRule < 0) firstRule = hit;
            if (Rules[hit].Action == ComplianceAction.Block) { block = true; return -1; }
            var rep = Rules[hit].Replacement.AsSpan();
            var k = Math.Min(rep.Length, dst.Length - n);
            rep[..k].CopyTo(dst[n..]); n += k;
            i += len;
        }
        return n;
    }

    public int Filter(int avatar, ReadOnlySpan<char> line, Span<char> dst)
    {
        if (!Enabled) { var k = Math.Min(line.Length, dst.Length); line[..k].CopyTo(dst); return k; }
        var n = Sanitize(line, dst, out var rule, out var block);
        if (rule >= 0)
        {
            if (block) Blocked++; else Replaced++;
            Record(rule, block ? ComplianceAction.Block : ComplianceAction.Replace, avatar, line);
        }
        return n;
    }

    private void Record(int rule, ComplianceAction action, int avatar, ReadOnlySpan<char> text)
    {
        ref var v = ref _log[_logHead];
        v.Rule = (byte)rule; v.Action = action; v.Avatar = (sbyte)avatar; v.AtFrame = Frame;
        v.TextLen = Math.Min(text.Length, LineCap);
        text[..v.TextLen].CopyTo(_logText.AsSpan(_logHead * LineCap));
        _logHead = (_logHead + 1) % LogRing; if (_logN < LogRing) _logN++;
    }

    // ══════════════════════ 가격 정합성(무할당) ══════════════════════
    public static PriceVerdict CheckPrice(long listPrice, long salePrice, int displayedPercent, int qty = 1, long displayedTotal = -1)
    {
        if (listPrice <= 0 || salePrice <= 0 || qty <= 0 || displayedPercent < 0 || displayedPercent > 99) return PriceVerdict.Invalid;
        if (salePrice >= listPrice) return PriceVerdict.SaleNotBelowList;
        var exact = (listPrice - salePrice) * 100.0 / listPrice;
        var fl = (int)Math.Floor(exact + 1e-9); var rd = (int)Math.Round(exact, MidpointRounding.AwayFromZero);
        if (displayedPercent != fl && displayedPercent != rd) return PriceVerdict.PercentMismatch;
        if (displayedTotal >= 0 && displayedTotal != salePrice * qty) return PriceVerdict.TotalMismatch;
        return PriceVerdict.Ok;
    }

    public bool ValidatePrice(long listPrice, long salePrice, int displayedPercent, int qty = 1, long displayedTotal = -1)
    {
        PriceChecks++;
        var v = CheckPrice(listPrice, salePrice, displayedPercent, qty, displayedTotal);
        LastPrice = v;
        if (v != PriceVerdict.Ok) PriceRejected++;
        return v == PriceVerdict.Ok;
    }

    public static string PriceName(PriceVerdict v) => v switch
    {
        PriceVerdict.Ok => "정합", PriceVerdict.SaleNotBelowList => "할인가 ≥ 정가", PriceVerdict.PercentMismatch => "할인율 불일치", PriceVerdict.TotalMismatch => "합계 불일치", _ => "가격 무효",
    };
}
