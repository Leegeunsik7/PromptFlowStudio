using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using PromptFlowStudio.Services.Flow;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>v16.22.0 앵커 종류.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AnchorKind>))]
public enum AnchorKind { Outfit, Prop }

/// <summary>
/// v16.22.0 의상/소품 앵커 — 이름(영문 프롬프트용) · 표시명 · 색(#RRGGBB) · 패턴 · 질감 · 참조 이미지(선택).
///   예: Outfit("dungaree overalls","멜빵바지","#4A78C2","denim","soft cotton") · Prop("stroller","유모차","#E8E2D0","plain","matte").
/// </summary>
public sealed record StyleAnchor(AnchorKind Kind, string Name, string Label, string ColorHex, string Pattern = "plain", string Material = "", string? ImagePath = null);

/// <summary>v16.22.0 대표색 분석 결과 — #RRGGBB · 영문 색 이름 · 한글 색 이름 · 비율.</summary>
public readonly record struct DominantColor(string Hex, string Name, string Korean, double Share);

/// <summary>
/// [PromptFlowStudio] v16.22.0 👕 의상 &amp; 소품 일관성 전이 엔진 — 앵커(색·패턴·질감) → 포지티브 고정 구문 + 네거티브 변형 차단 + ControlNet reference 가이드(순수 함수).
///   ▸ 색: 영역 픽셀 4-bit/채널 양자화 히스토그램(4096 빈 · stackalloc) → 상위 빈 평균 → 최근접 명명색(한/영 24색). 아주 어둡거나 밝은 배경 픽셀은 가중 감소.
///   ▸ 포지티브: "wearing the same {color} {pattern} {material} {outfit}" · "with the same {color} {prop}" — 멱등 · 프롬프트 내 상충 색(같은 의상 단어 앞 다른 색) 제거.
///   ▸ 네거티브: "different outfit", "changed clothing color", "{다른 색} {outfit}"(대비색 2개) · "missing {prop}".
///   ▸ ControlNet: 의상/소품 참조 이미지가 있으면 <see cref="VisualNodeGraph"/> ControlNet 노드에 reference 모드로 결선(얼굴 참조가 우선 · 빈 노드만 채움).
/// </summary>
public static class PropOutfitTransferEngine
{
    public const int MaxAnchors = 8;

    private static readonly (string Name, string Ko, byte R, byte G, byte B)[] Palette =
    {
        ("white", "흰색", 245, 245, 245), ("ivory", "아이보리", 238, 232, 210), ("beige", "베이지", 214, 196, 160), ("light gray", "연회색", 190, 190, 195),
        ("gray", "회색", 128, 128, 132), ("charcoal", "차콜", 60, 62, 66), ("black", "검정", 18, 18, 20), ("red", "빨강", 205, 40, 45),
        ("coral", "코랄", 245, 125, 105), ("pink", "분홍", 240, 160, 185), ("baby pink", "연분홍", 250, 210, 220), ("orange", "주황", 240, 140, 40),
        ("mustard yellow", "머스터드", 215, 175, 50), ("yellow", "노랑", 250, 225, 80), ("mint", "민트", 165, 225, 200), ("green", "초록", 60, 150, 75),
        ("olive", "올리브", 120, 125, 60), ("sky blue", "하늘색", 140, 195, 240), ("blue", "파랑", 50, 100, 200), ("navy", "네이비", 30, 45, 95),
        ("lavender", "라벤더", 190, 170, 230), ("purple", "보라", 120, 70, 160), ("brown", "갈색", 120, 80, 50), ("camel", "카멜", 190, 140, 90),
    };

    /// <summary>#RRGGBB 파싱(실패 false).</summary>
    public static bool TryParseHex(string? hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        var s = (hex ?? "").Trim().TrimStart('#');
        if (s.Length != 6) return false;
        return byte.TryParse(s.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
            && byte.TryParse(s.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
            && byte.TryParse(s.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
    }

    public static string ToHex(byte r, byte g, byte b) => "#" + r.ToString("X2", CultureInfo.InvariantCulture) + g.ToString("X2", CultureInfo.InvariantCulture) + b.ToString("X2", CultureInfo.InvariantCulture);

    /// <summary>최근접 명명색(가중 RGB 거리 · 인덱스 반환).</summary>
    public static int NearestColor(byte r, byte g, byte b)
    {
        var best = 0;
        var bestD = long.MaxValue;
        for (var i = 0; i < Palette.Length; i++)
        {
            var p = Palette[i];
            long dr = r - p.R, dg = g - p.G, db = b - p.B;
            var rm = (r + p.R) / 2;
            var d = ((512 + rm) * dr * dr >> 8) + 4 * dg * dg + ((767 - rm) * db * db >> 8);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    public static (string Name, string Korean) ColorNameOf(string? hex)
        => TryParseHex(hex, out var r, out var g, out var b) ? (Palette[NearestColor(r, g, b)].Name, Palette[NearestColor(r, g, b)].Ko) : ("", "");

    /// <summary>
    /// RGB24 영역(정규화 사각) 대표색(순수 · 무할당). 배경 가능성이 큰 거의 흰/검 픽셀은 0.25 가중.
    /// </summary>
    public static DominantColor AnalyzeDominantColor(ReadOnlySpan<byte> rgb, int w, int h, FaceRegion region)
    {
        if (w <= 0 || h <= 0 || rgb.Length < w * h * 3) return default;
        var r0 = region.Clamp();
        int x0 = (int)(r0.X * w), y0 = (int)(r0.Y * h), x1 = Math.Min(w, x0 + Math.Max(1, (int)(r0.W * w))), y1 = Math.Min(h, y0 + Math.Max(1, (int)(r0.H * h)));
        Span<float> bins = stackalloc float[4096];
        bins.Clear();
        float total = 0;
        for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                var o = (y * w + x) * 3;
                int r = rgb[o], g = rgb[o + 1], b = rgb[o + 2];
                var max = Math.Max(r, Math.Max(g, b));
                var min = Math.Min(r, Math.Min(g, b));
                var wgt = (min > 235 || max < 25) ? 0.25f : 1f;
                bins[(r >> 4) << 8 | (g >> 4) << 4 | (b >> 4)] += wgt;
                total += wgt;
            }
        if (total <= 0) return default;
        var bestBin = 0;
        for (var i = 1; i < bins.Length; i++) if (bins[i] > bins[bestBin]) bestBin = i;
        // 상위 빈 내부 실제 평균색
        long sr = 0, sg = 0, sb = 0, n = 0;
        for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                var o = (y * w + x) * 3;
                if (((rgb[o] >> 4) << 8 | (rgb[o + 1] >> 4) << 4 | (rgb[o + 2] >> 4)) != bestBin) continue;
                sr += rgb[o]; sg += rgb[o + 1]; sb += rgb[o + 2]; n++;
            }
        var cr = (byte)(sr / Math.Max(1, n));
        var cg = (byte)(sg / Math.Max(1, n));
        var cb = (byte)(sb / Math.Max(1, n));
        var idx = NearestColor(cr, cg, cb);
        return new DominantColor(ToHex(cr, cg, cb), Palette[idx].Name, Palette[idx].Ko, Math.Round(bins[bestBin] / total, 3));
    }

    /// <summary>앵커 1개 → 포지티브 구문.</summary>
    public static string ClauseOf(StyleAnchor a)
    {
        var (color, _) = ColorNameOf(a.ColorHex);
        var sb = new StringBuilder(96);
        sb.Append(a.Kind == AnchorKind.Outfit ? "wearing the same " : "with the same ");
        if (color.Length > 0) sb.Append(color).Append(' ');
        if (!string.IsNullOrWhiteSpace(a.Pattern) && !a.Pattern.Equals("plain", StringComparison.OrdinalIgnoreCase)) sb.Append(a.Pattern.Trim()).Append(' ');
        if (!string.IsNullOrWhiteSpace(a.Material)) sb.Append(a.Material.Trim()).Append(' ');
        sb.Append(a.Name.Trim());
        return sb.ToString();
    }

    /// <summary>대비색 이름 2개(같은 의상의 다른 색 변형 차단용).</summary>
    public static (string A, string B) ContrastColors(string? hex)
    {
        if (!TryParseHex(hex, out var r, out var g, out var b)) return ("", "");
        var self = NearestColor(r, g, b);
        int best1 = -1, best2 = -1;
        long d1 = -1, d2 = -1;
        for (var i = 0; i < Palette.Length; i++)
        {
            if (i == self) continue;
            var p = Palette[i];
            long d = (r - p.R) * (r - p.R) + (g - p.G) * (g - p.G) + (b - p.B) * (b - p.B);
            if (p.Name is "white" or "black") continue;   // 무채색은 조명 차이로 정당하게 보일 수 있어 제외
            if (d > d1) { d2 = d1; best2 = best1; d1 = d; best1 = i; }
            else if (d > d2) { d2 = d; best2 = i; }
        }
        return (best1 >= 0 ? Palette[best1].Name : "", best2 >= 0 ? Palette[best2].Name : "");
    }

    /// <summary>프롬프트에서 앵커 대상(이름) 앞의 다른 명명색 제거(순수) — "red overalls" → "overalls"(앵커 구문이 색 재부여).</summary>
    public static string StripConflictingColors(string prompt, IReadOnlyList<StyleAnchor> anchors)
    {
        var p = prompt;
        foreach (var a in anchors)
        {
            var (own, _) = ColorNameOf(a.ColorHex);
            var name = a.Name.Trim();
            if (name.Length == 0) continue;
            foreach (var c in Palette)
            {
                if (c.Name == own) continue;
                var phrase = c.Name + " " + name;
                var idx = p.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);
                while (idx >= 0)
                {
                    if (idx > 0 && char.IsLetter(p[idx - 1])) { idx = p.IndexOf(phrase, idx + 1, StringComparison.OrdinalIgnoreCase); continue; }
                    p = p.Remove(idx, c.Name.Length + 1);
                    idx = p.IndexOf(phrase, idx, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        return p;
    }

    /// <summary>
    /// 의상/소품 일관성 주입(멱등 · 순수). 반환: 보정 프롬프트 · 네거티브 · 주입 구문 수.
    /// </summary>
    public static (string Prompt, string Negative, int Injected) Apply(string? prompt, string? negative, IReadOnlyList<StyleAnchor>? anchors)
    {
        var p = (prompt ?? "").Trim();
        if (anchors is null || anchors.Count == 0) return (p, (negative ?? "").Trim(), 0);
        p = StripConflictingColors(p, anchors);
        var sb = new StringBuilder(p.Length + anchors.Count * 64).Append(p.TrimEnd(',', ' '));
        var injected = 0;
        var neg = new List<string>(4 + anchors.Count * 2) { "different outfit", "changed clothing color", "inconsistent clothing" };
        var count = 0;
        foreach (var a in anchors)
        {
            if (++count > MaxAnchors) break;
            if (string.IsNullOrWhiteSpace(a.Name)) continue;
            var clause = ClauseOf(a);
            if (!sb.ToString().Contains(clause, StringComparison.OrdinalIgnoreCase))
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(clause);
                injected++;
            }
            if (a.Kind == AnchorKind.Outfit)
            {
                var (c1, c2) = ContrastColors(a.ColorHex);
                if (c1.Length > 0) neg.Add(c1 + " " + a.Name.Trim());
                if (c2.Length > 0) neg.Add(c2 + " " + a.Name.Trim());
            }
            else neg.Add("missing " + a.Name.Trim());
        }
        return (sb.ToString(), CharacterIdentityLockService.MergeTokens(negative, neg), injected);
    }

    /// <summary>
    /// 노드 그래프 ControlNet 결선 — 참조 이미지 없는 ControlNet 노드에 (얼굴 → 첫 의상/소품 이미지) reference 모드 부여. 반환: 결선 노드 수.
    /// </summary>
    public static int ApplyToGraph(VisualNodeGraph graph, string? faceImage, IReadOnlyList<StyleAnchor>? anchors)
    {
        string? reference = faceImage;
        if (string.IsNullOrWhiteSpace(reference) && anchors is not null)
            foreach (var a in anchors) if (!string.IsNullOrWhiteSpace(a.ImagePath)) { reference = a.ImagePath; break; }
        if (string.IsNullOrWhiteSpace(reference)) return 0;
        var n = 0;
        foreach (var node in graph.Nodes)
        {
            if (node.Kind != VisualNodeKind.ControlNet || !string.IsNullOrWhiteSpace(node.AssetPath)) continue;
            node.AssetPath = reference;
            if (string.IsNullOrWhiteSpace(node.Text) || node.Text != "inpaint") node.Text = "reference";
            n++;
        }
        if (n > 0) graph.Raise();
        return n;
    }

    /// <summary>표시 라벨(UI).</summary>
    public static string Describe(StyleAnchor a)
    {
        var (_, ko) = ColorNameOf(a.ColorHex);
        return (a.Kind == AnchorKind.Outfit ? "👕 " : "🧸 ") + (ko.Length > 0 ? ko + " " : "") + a.Label;
    }
}
