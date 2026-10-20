using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>v16.19.0 CM 길이(초).</summary>
public enum AdDuration { Sec15 = 15, Sec30 = 30 }

/// <summary>v16.19.0 나레이션 톤.</summary>
public enum AdTone { Warm, Playful, Premium, Trustworthy }

/// <summary>v16.19.0 제품 브리프 — 제품명 · 브랜드 · 특징(최대 8) · 타깃 · 테마 카테고리.</summary>
public sealed record AdProductBrief(string ProductName, string Brand, IReadOnlyList<string> Features, string? Audience = null, string? ThemeCategory = null);

/// <summary>v16.19.0 CM 대본 1줄 — 시작/길이(초) · 나레이션 · 화면 자막(카피).</summary>
public sealed record AdScriptLine(double Start, double Seconds, string Narration, string OnScreen);

/// <summary>v16.19.0 BGM 페어링 — 무드 · BPM(60~160) · 악기 · 징글 문구.</summary>
public sealed record AdBgmPairing(string Mood, int Bpm, string Instruments, string Jingle);

/// <summary>v16.19.0 CM 카피 패키지 — 헤드라인 · CTA · 나레이션 톤 · BGM · 타임라인 대본 · 표시광고 점검 메모.</summary>
public sealed record CommercialAdScript(
    AdDuration Duration, string Headline, string Cta, string NarrationTone, AdBgmPairing Bgm,
    IReadOnlyList<AdScriptLine> Lines, IReadOnlyList<string> ComplianceNotes, bool AiUsed, string Message)
{
    public double TotalSeconds => (int)Duration;
}

/// <summary>
/// [PromptFlowStudio] v16.19.0 ✍️ 상업용 광고 카피 &amp; 징글 AI 생성기 — 제품 특징 → Gemini Strict JSON → 15s/30s CM 대본 · 나레이션 톤 · BGM 페어링.
///   ▸ Gemini 출력은 비신뢰 → 스키마 즉시 검증(headline/cta/tone 문자열 · bgm 객체 · lines 배열) · 줄 수 상한(15s 6 · 30s 10) ·
///     타임라인 정규화(각 줄 길이를 총 길이에 맞게 비례 스케일 · 최소 1초) · BPM 60~160 클램프 · 문자열 상한.
///   ▸ 아동용품 표시광고 점검: 최상급/절대 표현·의학적 효능·안전 단정 문구 탐지 → 점검 메모(자동 삭제 아님 · 사용자 판단).
///   ▸ 키 없음/실패/검증 실패는 특징 기반 로컬 템플릿으로 100% 폴백(예외 0).
/// </summary>
public sealed class CommercialAdScriptAiGenerator
{
    public const int MaxFeatures = 8;
    public const int MaxFieldChars = 120;

    /// <summary>아동용품 광고 주의 표현(표시·광고 공정화 · 어린이제품 안전 표기 관행).</summary>
    public static readonly IReadOnlyList<string> RiskyClaims = new[]
    {
        "최고", "1위", "넘버원", "100%", "완벽", "무조건", "절대", "치료", "예방", "의사 추천", "소아과 추천", "부작용 없", "무독성 보장", "가장 안전",
    };

    private readonly Func<string, CancellationToken, Task<GeminiResult>>? _generate;
    private readonly Func<bool> _enabled;

    public CommercialAdScriptAiGenerator(GeminiClient? gemini = null)
    {
        _generate = gemini is null ? null : (p, ct) => gemini.GenerateAsync(p, null, 0.8, ct);
        _enabled = () => gemini is { Enabled: true };
    }

    /// <param name="generate">지시문 → Gemini 결과(테스트 주입).</param>
    public CommercialAdScriptAiGenerator(Func<string, CancellationToken, Task<GeminiResult>> generate)
    {
        _generate = generate;
        _enabled = static () => true;
    }

    public bool AiEnabled => _generate is not null && _enabled();

    public static int MaxLinesOf(AdDuration d) => d == AdDuration.Sec15 ? 6 : 10;

    public static string ToneLabel(AdTone t) => t switch
    {
        AdTone.Warm => "따뜻한 육아 공감",
        AdTone.Playful => "발랄한 키즈 에너지",
        AdTone.Premium => "프리미엄 신뢰감",
        _ => "차분한 전문가 신뢰",
    };

    /// <summary>Gemini 지시문(Strict JSON 스키마 명시 · 순수 함수).</summary>
    public static string BuildPrompt(AdProductBrief brief, AdDuration duration, AdTone tone)
    {
        ArgumentNullException.ThrowIfNull(brief);
        var sec = (int)duration;
        var sb = new StringBuilder(1400);
        sb.Append("당신은 아동·육아용품 브랜드 TV/숏폼 CM 카피라이터입니다. 아래 제품으로 ").Append(sec).Append("초 광고 대본을 한국어로 작성하세요.\n")
          .Append("제품명: ").Append(Quote(brief.ProductName)).Append('\n')
          .Append("브랜드: ").Append(Quote(brief.Brand)).Append('\n')
          .Append("타깃: ").Append(Quote(string.IsNullOrWhiteSpace(brief.Audience) ? "영유아 부모" : brief.Audience)).Append('\n')
          .Append("영상 테마: ").Append(Quote(string.IsNullOrWhiteSpace(brief.ThemeCategory) ? "일상 육아" : brief.ThemeCategory)).Append('\n')
          .Append("제품 특징:\n");
        var n = 0;
        foreach (var f in brief.Features)
        {
            if (string.IsNullOrWhiteSpace(f)) continue;
            sb.Append("- ").Append(Quote(Cap(f, MaxFieldChars))).Append('\n');
            if (++n >= MaxFeatures) break;
        }
        sb.Append("나레이션 톤: ").Append(ToneLabel(tone)).Append('\n')
          .Append("규칙: 줄 ").Append(MaxLinesOf(duration)).Append("개 이하, 각 줄 sec 합계 = ").Append(sec).Append("초, 첫 줄은 2초 훅, 마지막 줄은 CTA.\n")
          .Append("onScreen 은 화면 자막 카피(14자 이하), narration 은 성우 대사.\n")
          .Append("bgm 은 장면과 어울리는 무드·BPM(60~160)·악기, jingle 은 브랜드명이 들어간 4~8음절 징글 가사.\n")
          .Append("표시광고 준수: 최상급(최고·1위)·절대 표현·의학적 효능·안전 단정 금지, 아동 공포·선정 표현 금지.\n")
          .Append("아래 Strict JSON 으로만 응답(마크다운·설명 금지):\n")
          .Append("{\"headline\":\"\",\"cta\":\"\",\"narrationTone\":\"\",\"bgm\":{\"mood\":\"\",\"bpm\":100,\"instruments\":\"\",\"jingle\":\"\"},")
          .Append("\"lines\":[{\"sec\":2,\"narration\":\"\",\"onScreen\":\"\"}]}");
        return sb.ToString();
    }

    /// <summary>카피 생성(예외 0) — AI 유효 시 AI, 아니면 로컬 템플릿.</summary>
    public async Task<CommercialAdScript> GenerateAsync(AdProductBrief brief, AdDuration duration = AdDuration.Sec15,
        AdTone tone = AdTone.Warm, bool useAi = true, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(brief);
        var note = "로컬 템플릿";
        if (useAi && AiEnabled)
        {
            try
            {
                var res = await _generate!(BuildPrompt(brief, duration, tone), ct).ConfigureAwait(false);
                if (res.Ok && res.Text is not null)
                {
                    var parsed = Parse(res.Text, duration);
                    if (parsed is not null) return parsed with { Message = "Gemini AI" };
                    note = "AI 응답 검증 실패 → 로컬 템플릿";
                }
                else note = "AI 실패(" + res.Message + ") → 로컬 템플릿";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Program.Log("CommercialAdScript.Generate", ex); note = "AI 예외 → 로컬 템플릿"; }
        }
        return Template(brief, duration, tone) with { Message = note };
    }

    /// <summary>Gemini JSON 파싱 + 스키마 검증(무효면 null · 순수 함수).</summary>
    public static CommercialAdScript? Parse(string text, AdDuration duration)
    {
        try
        {
            using var doc = JsonDocument.Parse(GeminiClient.StripFence(text));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!Str(root, "headline", out var headline) || !Str(root, "cta", out var cta)) return null;
            Str(root, "narrationTone", out var toneText);
            if (!root.TryGetProperty("bgm", out var bgmEl) || bgmEl.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("lines", out var linesEl) || linesEl.ValueKind != JsonValueKind.Array) return null;

            Str(bgmEl, "mood", out var mood);
            Str(bgmEl, "instruments", out var inst);
            Str(bgmEl, "jingle", out var jingle);
            var bpm = bgmEl.TryGetProperty("bpm", out var b) && b.ValueKind == JsonValueKind.Number && b.TryGetDouble(out var bv) ? (int)Math.Round(bv) : 100;

            var max = MaxLinesOf(duration);
            var raw = new List<(double Sec, string Nar, string Scr)>(max);
            foreach (var l in linesEl.EnumerateArray())
            {
                if (raw.Count >= max) break;
                if (l.ValueKind != JsonValueKind.Object) continue;
                Str(l, "narration", out var nar);
                Str(l, "onScreen", out var scr);
                if (nar.Length == 0 && scr.Length == 0) continue;
                var sec = l.TryGetProperty("sec", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetDouble(out var sv) ? sv : 0;
                raw.Add((sec, nar, scr));
            }
            if (raw.Count == 0 || headline.Length == 0 || cta.Length == 0) return null;
            var lines = Normalize(raw, (int)duration);
            var bgm = new AdBgmPairing(mood.Length > 0 ? mood : "밝고 따뜻한", Math.Clamp(bpm, 60, 160), inst.Length > 0 ? inst : "어쿠스틱 기타 · 글로켄슈필", jingle);
            return new CommercialAdScript(duration, headline, cta, toneText.Length > 0 ? toneText : "따뜻한", bgm, lines,
                ScanCompliance(headline, cta, lines), true, "Gemini AI");
        }
        catch (JsonException) { return null; }
    }

    /// <summary>타임라인 정규화 — 길이 0 이하 줄은 균등 배분 후 비례 스케일(합계 = total · 최소 1초).</summary>
    public static List<AdScriptLine> Normalize(IReadOnlyList<(double Sec, string Nar, string Scr)> raw, int total)
    {
        var n = raw.Count;
        var list = new List<AdScriptLine>(n);
        if (n == 0) return list;
        double sum = 0;
        for (var i = 0; i < n; i++) sum += raw[i].Sec > 0 ? raw[i].Sec : 0;
        var fill = sum > 0 ? sum / n : (double)total / n;
        var weights = 0.0;
        for (var i = 0; i < n; i++) weights += raw[i].Sec > 0 ? raw[i].Sec : fill;
        var min = Math.Min(1.0, (double)total / n);
        var start = 0.0;
        for (var i = 0; i < n; i++)
        {
            var w = raw[i].Sec > 0 ? raw[i].Sec : fill;
            var sec = i == n - 1
                ? total - start
                : Math.Clamp(Math.Round(w / weights * total, 1), min, Math.Max(min, total - start - min * (n - 1 - i)));
            list.Add(new AdScriptLine(Math.Round(start, 1), Math.Round(sec, 1), raw[i].Nar, raw[i].Scr));
            start += sec;
        }
        return list;
    }

    /// <summary>표시광고 주의 표현 탐지 → 점검 메모.</summary>
    public static List<string> ScanCompliance(string headline, string cta, IReadOnlyList<AdScriptLine> lines)
    {
        var notes = new List<string>(2);
        var sb = new StringBuilder(256).Append(headline).Append('\n').Append(cta);
        foreach (var l in lines) sb.Append('\n').Append(l.Narration).Append('\n').Append(l.OnScreen);
        var all = sb.ToString();
        foreach (var w in RiskyClaims)
            if (all.Contains(w, StringComparison.OrdinalIgnoreCase)) notes.Add("⚠ '" + w + "' 표현 — 실증 자료 없으면 표시광고법 위반 소지");
        notes.Add("ℹ 협찬/PPL 영상은 '유료 광고 포함' 표기가 필요합니다(오버레이 자동 삽입).");
        return notes;
    }

    /// <summary>특징 기반 로컬 템플릿(오프라인 폴백).</summary>
    public static CommercialAdScript Template(AdProductBrief brief, AdDuration duration, AdTone tone)
    {
        var name = string.IsNullOrWhiteSpace(brief.ProductName) ? "우리 아이 필수템" : Cap(brief.ProductName.Trim(), 40);
        var brand = string.IsNullOrWhiteSpace(brief.Brand) ? name : Cap(brief.Brand.Trim(), 30);
        var feats = new List<string>(MaxFeatures);
        foreach (var f in brief.Features)
        {
            if (!string.IsNullOrWhiteSpace(f)) feats.Add(Cap(f.Trim(), 40));
            if (feats.Count >= MaxFeatures) break;
        }
        if (feats.Count == 0) feats.Add("엄마 아빠가 먼저 고른");
        var raw = new List<(double, string, string)>(MaxLinesOf(duration))
        {
            (2, "오늘도 우리 아이와 함께하는 하루, 준비됐나요?", "오늘도 함께해요"),
        };
        var featLines = duration == AdDuration.Sec15 ? Math.Min(2, feats.Count) : Math.Min(5, feats.Count);
        for (var i = 0; i < featLines; i++) raw.Add((0, name + ", " + feats[i] + ".", Cap(feats[i], 14)));
        raw.Add((0, brand + "와 함께라면 육아가 한결 가벼워져요.", brand));
        raw.Add((3, "지금 " + name + " 만나 보세요!", "지금 만나 보세요"));
        var lines = Normalize(raw, (int)duration);
        var bgm = tone switch
        {
            AdTone.Playful => new AdBgmPairing("발랄·통통 튀는", 118, "우쿨렐레 · 핸드클랩 · 실로폰", brand + " 랄랄라"),
            AdTone.Premium => new AdBgmPairing("세련·포근한", 84, "피아노 · 스트링 패드", brand + ", 포근하게"),
            AdTone.Trustworthy => new AdBgmPairing("차분·안정적인", 92, "어쿠스틱 기타 · 첼로", "믿음 주는 " + brand),
            _ => new AdBgmPairing("따뜻·다정한", 100, "어쿠스틱 기타 · 글로켄슈필", "우리 아이 " + brand),
        };
        var headline = name + " — " + feats[0];
        const string cta = "지금 바로 만나 보세요";
        return new CommercialAdScript(duration, Cap(headline, MaxFieldChars), cta, ToneLabel(tone), bgm, lines,
            ScanCompliance(headline, cta, lines), false, "로컬 템플릿");
    }

    /// <summary>대본 텍스트 내보내기(사람이 읽는 형식).</summary>
    public static string ToText(CommercialAdScript s, AdProductBrief? brief = null)
    {
        var sb = new StringBuilder(1024);
        if (brief is not null) sb.Append("■ ").Append(brief.Brand).Append(" · ").Append(brief.ProductName).Append('\n');
        sb.Append("■ ").Append((int)s.Duration).Append("초 CM · ").Append(s.Message).Append('\n')
          .Append("헤드라인: ").Append(s.Headline).Append('\n')
          .Append("CTA: ").Append(s.Cta).Append('\n')
          .Append("나레이션 톤: ").Append(s.NarrationTone).Append('\n')
          .Append("BGM: ").Append(s.Bgm.Mood).Append(" · ").Append(s.Bgm.Bpm).Append(" BPM · ").Append(s.Bgm.Instruments).Append('\n')
          .Append("징글: ").Append(s.Bgm.Jingle).Append("\n\n");
        foreach (var l in s.Lines)
        {
            sb.Append('[').Append(l.Start.ToString("0.0", CultureInfo.InvariantCulture)).Append("s +")
              .Append(l.Seconds.ToString("0.0", CultureInfo.InvariantCulture)).Append("s] ")
              .Append(l.Narration);
            if (l.OnScreen.Length > 0) sb.Append("  〔").Append(l.OnScreen).Append('〕');
            sb.Append('\n');
        }
        if (s.ComplianceNotes.Count > 0)
        {
            sb.Append("\n점검:\n");
            foreach (var n in s.ComplianceNotes) sb.Append("- ").Append(n).Append('\n');
        }
        return sb.ToString();
    }

    private static bool Str(JsonElement o, string name, out string value)
    {
        if (o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String)
        {
            value = Cap(e.GetString()?.Trim() ?? "", MaxFieldChars);
            return true;
        }
        value = "";
        return false;
    }

    private static string Cap(string s, int max) => s.Length <= max ? s : s[..max];
    private static string Quote(string? s) => "\"" + (s ?? "").Replace("\"", "'") + "\"";
}
