using System.Globalization;
using PromptFlowStudio.Services.Media;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>
/// [v18.2.3] 샷 9:16 캔버스 레이아웃(1080×1920 논리 px) — 인물 아이레벨 · 머리/어깨 실루엣 · 자막 안전영역 · 하단 앵커 자막 박스.
///   <see cref="Overflow"/> = 자막 박스가 안전영역 위로 침범(인물 얼굴/상체 영역 · 앵글이 타이트할수록 영역이 좁음).
/// </summary>
public struct StoryboardCanvasLayout
{
    public ShotAngle Angle;
    public int EyeLineY, HeadCenterY, HeadRadius, ShoulderY;
    public int ZoneTop, ZoneBottom;
    public int CaptionUnits, CaptionLines, CaptionTop, CaptionBottom;

    public readonly bool Overflow => CaptionTop < ZoneTop;
    public readonly int MaxLines => (ZoneBottom - ZoneTop) / StoryboardVisualCanvasRenderer.LineHeight;
}

/// <summary>
/// [PromptFlow Studio v18.2.3] 🖼 스토리보드 9:16 비주얼 프리뷰 캔버스 — 샷 카드마다 3 분할 그리드 · 인물 아이레벨 · 자막 안전영역을 경량 SVG 로 그린다.
///   ▸ <see cref="Layout"/>: 앵글(와이드/미디엄/클로즈업) → 아이레벨(<see cref="StoryboardKeyframeSequencer.CenterYPermilles"/>) · 실루엣 크기 · 안전영역 상단(인물 하한).
///     안전영역 하단 = 유튜브 숏츠 하단 UI 경계(<see cref="MultiPlatformSafeZoneService.CaptionBottomFraction"/>).
///   ▸ 자막 줄 수 = 전각(한글/CJK) 2 · 반각 1 단위 누적 → 줄당 <see cref="UnitsPerLine"/> 초과 시 줄바꿈 → 하단 앵커 박스 높이 · 안전영역 침범 판정(0 B).
///   ▸ <see cref="TryWriteSvg"/>: 호출자 Span 에 SVG 직렬화(0 B · 수치 InvariantCulture) · <see cref="Svg"/> 는 UI 용 소유 버퍼 → 문자열 1 개.
/// </summary>
public sealed class StoryboardVisualCanvasRenderer
{
    public const int Width = 1080, Height = 1920;
    public const int LineHeight = 80, UnitsPerLine = 28;
    /// <summary>앵글별 머리 반지름 · 안전영역 상단(인물 얼굴/상체 하한 · 와이드 60 % · 미디엄 65 % · 클로즈업 71.7 %).</summary>
    public static readonly int[] HeadRadii = { 70, 130, 240 };
    public static readonly int[] ZoneTops = { 1152, 1248, 1376 };
    /// <summary>안전영역 하단 — 유튜브 숏츠 하단 제목/계정 UI 위(1920 × 0.80 = 1536).</summary>
    public static readonly int ZoneBottom = Height - (int)Math.Round(Height * MultiPlatformSafeZoneService.CaptionBottomFraction(SafePlatform.YouTubeShorts));

    private readonly char[] _svg = new char[4096];

    /// <summary>앵글 + 자막 텍스트 → 캔버스 레이아웃(0 B).</summary>
    public static StoryboardCanvasLayout Layout(ShotAngle angle, ReadOnlySpan<char> caption)
    {
        var a = (int)angle;
        StoryboardCanvasLayout l = default;
        l.Angle = angle;
        l.EyeLineY = Height * StoryboardKeyframeSequencer.CenterYPermilles[a] / 1000;
        l.HeadRadius = HeadRadii[a];
        l.HeadCenterY = l.EyeLineY + l.HeadRadius / 5;
        l.ShoulderY = l.HeadCenterY + l.HeadRadius + l.HeadRadius / 3;
        l.ZoneTop = ZoneTops[a];
        l.ZoneBottom = ZoneBottom;
        l.CaptionLines = CaptionLines(caption, out l.CaptionUnits);
        l.CaptionBottom = ZoneBottom;
        l.CaptionTop = ZoneBottom - l.CaptionLines * LineHeight;
        return l;
    }

    /// <summary>자막 줄 수 — 전각 2 · 반각 1 단위 · 줄당 <see cref="UnitsPerLine"/> · '\n' 강제 줄바꿈 · 줄머리 공백 무시.</summary>
    public static int CaptionLines(ReadOnlySpan<char> caption, out int units)
    {
        int lines = 0, cur = 0;
        units = 0;
        foreach (var c in caption)
        {
            if (c == '\n') { if (cur > 0) { lines++; cur = 0; } continue; }
            if (char.IsWhiteSpace(c) && cur == 0) continue;
            var u = IsFullWidth(c) ? 2 : 1;
            units += u;
            if (cur + u > UnitsPerLine) { lines++; cur = 0; if (char.IsWhiteSpace(c)) continue; }
            cur += u;
        }
        return cur > 0 ? lines + 1 : lines;
    }

    /// <summary>레이아웃 → SVG(viewBox 1080×1920) · 녹색/적색 안전영역 · 3 분할 · 아이레벨 점선 · 실루엣 · 자막 박스. 용량 부족 false(0 B).</summary>
    public static bool TryWriteSvg(in StoryboardCanvasLayout l, Span<char> dst, out int written)
    {
        var w = new Writer(dst);
        var zone = l.Overflow ? "#ef4444" : "#22c55e";
        w.Put("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 1080 1920' class='sb-cv' role='img' aria-label='9:16 프리뷰'>");
        w.Put("<rect width='1080' height='1920' fill='#0f172a'/>");
        w.Put("<path d='M360 0V1920M720 0V1920M0 640H1080M0 1280H1080' stroke='#94a3b8' stroke-opacity='.35' stroke-width='4'/>");
        w.Put("<rect y='"); w.Int(l.ZoneBottom); w.Put("' width='1080' height='"); w.Int(Height - l.ZoneBottom); w.Put("' fill='#64748b' fill-opacity='.35'/>");
        w.Put("<ellipse cx='540' cy='"); w.Int(l.ShoulderY + l.HeadRadius); w.Put("' rx='"); w.Int(l.HeadRadius * 2); w.Put("' ry='"); w.Int(l.HeadRadius); w.Put("' fill='#cbd5e1' fill-opacity='.55'/>");
        w.Put("<circle cx='540' cy='"); w.Int(l.HeadCenterY); w.Put("' r='"); w.Int(l.HeadRadius); w.Put("' fill='#e2e8f0' fill-opacity='.75'/>");
        w.Put("<path d='M0 "); w.Int(l.EyeLineY); w.Put("H1080' stroke='#38bdf8' stroke-width='6' stroke-dasharray='24 16'/>");
        w.Put("<rect y='"); w.Int(l.ZoneTop); w.Put("' width='1080' height='"); w.Int(l.ZoneBottom - l.ZoneTop); w.Put("' fill='"); w.Put(zone); w.Put("' fill-opacity='.18' stroke='"); w.Put(zone); w.Put("' stroke-width='6'/>");
        if (l.CaptionLines > 0)
        {
            w.Put("<rect x='90' y='"); w.Int(l.CaptionTop); w.Put("' width='900' height='"); w.Int(l.CaptionBottom - l.CaptionTop); w.Put("' rx='16' fill='#f8fafc' fill-opacity='.8' stroke='"); w.Put(zone); w.Put("' stroke-width='6'/>");
        }
        w.Put("</svg>");
        written = w.Pos;
        return w.Ok;
    }

    /// <summary>UI 용 — 소유 버퍼 직렬화 후 문자열 1 개(실패 시 빈 문자열).</summary>
    public string Svg(in StoryboardCanvasLayout l) => TryWriteSvg(l, _svg, out var n) ? new string(_svg, 0, n) : string.Empty;

    private static bool IsFullWidth(char c) => c is (>= 'ᄀ' and <= 'ᇿ') or (>= '⺀' and <= '꓏') or (>= '가' and <= '힣') or (>= '豈' and <= '﫿') or (>= '＀' and <= '｠');

    private ref struct Writer
    {
        private readonly Span<char> _dst;
        public int Pos;
        public bool Ok;

        public Writer(Span<char> dst) { _dst = dst; Pos = 0; Ok = true; }

        public void Put(ReadOnlySpan<char> s)
        {
            if (!Ok || !s.TryCopyTo(_dst[Pos..])) { Ok = false; return; }
            Pos += s.Length;
        }

        public void Int(int v)
        {
            if (!Ok || !v.TryFormat(_dst[Pos..], out var n, default, CultureInfo.InvariantCulture)) { Ok = false; return; }
            Pos += n;
        }
    }
}
