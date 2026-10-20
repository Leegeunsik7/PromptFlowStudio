using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Media;

/// <summary>v17.30.1 카드뉴스 슬라이드 종류 — Hook(1) → 본문(3) → CTA 요약(1).</summary>
public enum CardNewsSlideKind { Hook, Body, Cta }

/// <summary>v17.30.1 렌더 단위 슬라이드(줄바꿈 전 원문).</summary>
public sealed record CardNewsSlide(CardNewsSlideKind Kind, string Title, string Body);

/// <summary>v17.30.1 카드뉴스 덱 — 본문은 정확히 3장. 색상은 ffmpeg/CSS 공용 <c>#RRGGBB</c>.</summary>
public sealed record CardNewsDeck(
    string Hook, string HookSub, IReadOnlyList<CardNewsSlide> Bodies, string CtaTitle, string CtaSummary,
    string OutputPath, double SlideSeconds = 4.0, string Background = "#0F172A", string Accent = "#38BDF8");

/// <summary>v17.30.1 생성 결과 — 실패 시 HTML 템플릿 폴백 경로를 함께 반환([부분 구제]).</summary>
public sealed record CardNewsReelsResult(bool Ok, string? VideoPath, string? HtmlPath, string Message);

/// <summary>
/// [PromptFlowStudio] v17.30.1 🎴 카드뉴스 9:16 릴스 생성기 — 1080×1920 슬라이드(Hook · 본문 3 · CTA)를 MP4 로 합성(순수 BCL · 외부 ffmpeg 프로세스).
///   ▸ 레이아웃: lavfi <c>color</c> 캔버스 + <c>drawbox</c> 액센트 바 + <c>drawtext textfile=</c>(한글 인라인 이스케이프 회피 · expansion=none) + 페이드 인/아웃 → <c>concat</c>.
///   ▸ 무음 AAC 트랙(anullsrc) 동봉 — 오디오 없는 릴스 업로드 거부 회피 · H.264 yuv420p · +faststart(Graph API 권장 규격).
///   ▸ 동일 슬라이드의 HTML/CSS 템플릿(<see cref="RenderHtml"/>)을 항상 기록 — ffmpeg 부재/실패 시 즉시 폴백 산출물(프리뷰·수동 캡처).
///   ▸ 실행부는 <see cref="FfmpegVideoMixerService.RunAsync"/> 공유(진행률 out_time_us · Graceful 결과 · 예외 0).
/// </summary>
public sealed class CardNewsReelsGenerator
{
    public const int Width = 1080;
    public const int Height = 1920;
    public const int Fps = 30;
    public const int Margin = 96;
    public const int TitleFontSize = 76;
    public const int BodyFontSize = 52;
    public const int TitleWrapUnits = 22;   // 전각 1자 = 2단위 → 한글 약 11자/줄
    public const int BodyWrapUnits = 32;
    public const int TitleMaxLines = 4;
    public const int BodyMaxLines = 9;
    public const double FadeSeconds = 0.4;
    public const double MinSlideSeconds = 2.0;
    public const double MaxSlideSeconds = 15.0;

    private readonly Func<string?> _ffmpeg;

    public CardNewsReelsGenerator(IVideoMergeService merge) : this(() => merge.ResolvedFfmpegPath) { }

    /// <summary>테스트/커스텀 경로 주입용.</summary>
    public CardNewsReelsGenerator(Func<string?> ffmpegPath) => _ffmpeg = ffmpegPath;

    public bool IsAvailable => _ffmpeg() is not null;

    /// <summary>덱 검증 — 오류 메시지(정상이면 null).</summary>
    public static string? Validate(CardNewsDeck deck)
    {
        if (deck is null) return "덱이 비어 있습니다.";
        if (string.IsNullOrWhiteSpace(deck.Hook)) return "Hook 문구가 비어 있습니다.";
        if (deck.Bodies is null || deck.Bodies.Count != 3) return "본문 슬라이드는 정확히 3장이어야 합니다.";
        for (var i = 0; i < deck.Bodies.Count; i++)
            if (string.IsNullOrWhiteSpace(deck.Bodies[i].Title) && string.IsNullOrWhiteSpace(deck.Bodies[i].Body))
                return "본문 " + (i + 1).ToString(CultureInfo.InvariantCulture) + " 이 비어 있습니다.";
        if (string.IsNullOrWhiteSpace(deck.CtaTitle)) return "CTA 문구가 비어 있습니다.";
        if (string.IsNullOrWhiteSpace(deck.OutputPath)) return "출력 경로가 비어 있습니다.";
        if (!IsHexColor(deck.Background) || !IsHexColor(deck.Accent)) return "색상은 #RRGGBB 형식이어야 합니다.";
        return null;
    }

    /// <summary>Hook → 본문 3 → CTA 순 5장.</summary>
    public static List<CardNewsSlide> BuildSlides(CardNewsDeck deck)
    {
        var list = new List<CardNewsSlide>(deck.Bodies.Count + 2) { new(CardNewsSlideKind.Hook, deck.Hook, deck.HookSub ?? "") };
        foreach (var b in deck.Bodies) list.Add(b with { Kind = CardNewsSlideKind.Body });
        list.Add(new(CardNewsSlideKind.Cta, deck.CtaTitle, deck.CtaSummary ?? ""));
        return list;
    }

    public static double SlideSecondsOf(CardNewsDeck deck) => Math.Clamp(deck.SlideSeconds, MinSlideSeconds, MaxSlideSeconds);

    public static double TotalSeconds(CardNewsDeck deck) => SlideSecondsOf(deck) * (deck.Bodies.Count + 2);

    /// <summary>표시 폭 기준 줄바꿈(전각 2 · 반각 1단위) — 공백 우선 분할, 최대 줄 초과 시 말줄임.</summary>
    public static string Wrap(string text, int maxUnits, int maxLines)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new StringBuilder(text.Length + 8);
        var lines = 0;
        foreach (var para in text.Replace("\r", "").Split('\n'))
        {
            var line = new StringBuilder(64);
            var units = 0;
            foreach (var word in para.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (units > 0 && units + 1 + UnitsOf(word) > maxUnits)
                {
                    if (!Emit(sb, line, ref lines, maxLines)) return sb.ToString();
                    units = 0;
                }
                if (units > 0) { line.Append(' '); units++; }
                // 단어 자체가 한 줄보다 길면 글자 단위로 강제 분할
                foreach (var ch in word)
                {
                    var cu = UnitOf(ch);
                    if (units > 0 && units + cu > maxUnits)
                    {
                        if (!Emit(sb, line, ref lines, maxLines)) return sb.ToString();
                        units = 0;
                    }
                    line.Append(ch);
                    units += cu;
                }
            }
            if (!Emit(sb, line, ref lines, maxLines)) return sb.ToString();
        }
        return sb.ToString();
    }

    private static bool Emit(StringBuilder sb, StringBuilder line, ref int lines, int maxLines)
    {
        if (lines == maxLines)
        {
            // 한도 초과 — 마지막 줄 끝을 말줄임으로 치환
            if (sb.Length > 0 && sb[^1] != '…') sb.Append('…');
            line.Clear();
            return false;
        }
        if (lines > 0) sb.Append('\n');
        sb.Append(line);
        line.Clear();
        lines++;
        return true;
    }

    private static int UnitOf(char c) => c >= '⺀' ? 2 : 1;

    private static int UnitsOf(string s) { var u = 0; foreach (var c in s) u += UnitOf(c); return u; }

    /// <summary>ffmpeg 인자 — 슬라이드별 텍스트 파일 경로(제목/본문/페이지)는 호출측이 기록.</summary>
    public static List<string> BuildArgs(CardNewsDeck deck, IReadOnlyList<(string Title, string Body, string Page)> textFiles, string? fontFile)
    {
        var n = textFiles.Count;
        var sec = SlideSecondsOf(deck);
        var s = FfmpegVideoMixerService.Inv(sec);
        var bg = "0x" + deck.Background.TrimStart('#');
        var accent = "0x" + deck.Accent.TrimStart('#');
        var font = fontFile is null ? "" : "fontfile=" + FontResolver.EscapeDrawPath(fontFile) + ":";
        var args = new List<string>(8 + n * 6 + 24) { "-y", "-hide_banner", "-nostats" };
        for (var i = 0; i < n; i++)
        {
            args.Add("-f"); args.Add("lavfi");
            args.Add("-i"); args.Add("color=c=" + bg + ":s=" + Width.ToString(CultureInfo.InvariantCulture) + "x" + Height.ToString(CultureInfo.InvariantCulture)
                + ":d=" + s + ":r=" + Fps.ToString(CultureInfo.InvariantCulture));
        }
        args.Add("-f"); args.Add("lavfi");
        args.Add("-t"); args.Add(FfmpegVideoMixerService.Inv(sec * n));
        args.Add("-i"); args.Add("anullsrc=r=44100:cl=stereo");

        var fc = new StringBuilder(512 * n);
        var fadeOut = FfmpegVideoMixerService.Inv(Math.Max(0, sec - FadeSeconds));
        var fade = FfmpegVideoMixerService.Inv(FadeSeconds);
        var m = Margin.ToString(CultureInfo.InvariantCulture);
        for (var i = 0; i < n; i++)
        {
            var hook = i == 0 || i == n - 1;
            var titleY = hook ? "(h*0.36)" : "(h*0.18)";
            var bodyY = hook ? "(h*0.36+th+64)" : "(h*0.18+380)";
            var tf = textFiles[i];
            var idx = i.ToString(CultureInfo.InvariantCulture);
            fc.Append('[').Append(idx).Append(":v]");
            fc.Append("drawbox=x=").Append(m).Append(":y=").Append(hook ? "(h*0.36-56)" : "(h*0.18-56)")
              .Append(":w=120:h=12:color=").Append(accent).Append(":t=fill,");
            fc.Append("drawtext=").Append(font).Append("textfile=").Append(FontResolver.EscapeDrawPath(tf.Title))
              .Append(":expansion=none:fontcolor=white:fontsize=").Append(TitleFontSize.ToString(CultureInfo.InvariantCulture))
              .Append(":line_spacing=18:x=").Append(m).Append(":y=").Append(titleY).Append(',');
            fc.Append("drawtext=").Append(font).Append("textfile=").Append(FontResolver.EscapeDrawPath(tf.Body))
              .Append(":expansion=none:fontcolor=0xCBD5E1:fontsize=").Append(BodyFontSize.ToString(CultureInfo.InvariantCulture))
              .Append(":line_spacing=22:x=").Append(m).Append(":y=").Append(bodyY).Append(',');
            fc.Append("drawtext=").Append(font).Append("textfile=").Append(FontResolver.EscapeDrawPath(tf.Page))
              .Append(":expansion=none:fontcolor=").Append(accent).Append(":fontsize=40:x=w-tw-").Append(m).Append(":y=h-180,");
            fc.Append("fade=t=in:st=0:d=").Append(fade).Append(",fade=t=out:st=").Append(fadeOut).Append(":d=").Append(fade)
              .Append(",format=yuv420p,setsar=1[v").Append(idx).Append("];");
        }
        for (var i = 0; i < n; i++) fc.Append("[v").Append(i.ToString(CultureInfo.InvariantCulture)).Append(']');
        fc.Append("concat=n=").Append(n.ToString(CultureInfo.InvariantCulture)).Append(":v=1:a=0[out]");

        args.Add("-filter_complex"); args.Add(fc.ToString());
        args.Add("-map"); args.Add("[out]");
        args.Add("-map"); args.Add(n.ToString(CultureInfo.InvariantCulture) + ":a");
        args.Add("-c:v"); args.Add("libx264");
        args.Add("-preset"); args.Add("medium");
        args.Add("-crf"); args.Add("20");
        args.Add("-pix_fmt"); args.Add("yuv420p");
        args.Add("-r"); args.Add(Fps.ToString(CultureInfo.InvariantCulture));
        args.Add("-c:a"); args.Add("aac");
        args.Add("-b:a"); args.Add("128k");
        args.Add("-shortest");
        args.Add("-movflags"); args.Add("+faststart");
        args.Add("-progress"); args.Add("pipe:1");
        args.Add(deck.OutputPath);
        return args;
    }

    /// <summary>9:16 HTML/CSS 템플릿 — 프리뷰·폴백 산출물(텍스트는 HTML 인코딩).</summary>
    public static string RenderHtml(CardNewsDeck deck)
    {
        var slides = BuildSlides(deck);
        var sb = new StringBuilder(2048 + slides.Count * 512);
        sb.Append("<!DOCTYPE html><html lang=\"ko\"><head><meta charset=\"utf-8\"><title>CardNews Reels</title><style>")
          .Append("body{margin:0;background:#020617;display:flex;flex-wrap:wrap;gap:24px;padding:24px;font-family:'Malgun Gothic','Noto Sans KR',sans-serif}")
          .Append(".card{width:1080px;height:1920px;box-sizing:border-box;padding:").Append(Margin.ToString(CultureInfo.InvariantCulture))
          .Append("px;background:").Append(deck.Background).Append(";color:#fff;position:relative;display:flex;flex-direction:column;justify-content:center}")
          .Append(".card.body{justify-content:flex-start;padding-top:330px}")
          .Append(".bar{width:120px;height:12px;background:").Append(deck.Accent).Append(";margin-bottom:44px}")
          .Append("h1{font-size:").Append(TitleFontSize.ToString(CultureInfo.InvariantCulture)).Append("px;line-height:1.25;margin:0 0 64px;white-space:pre-line}")
          .Append("p{font-size:").Append(BodyFontSize.ToString(CultureInfo.InvariantCulture)).Append("px;line-height:1.45;color:#CBD5E1;margin:0;white-space:pre-line}")
          .Append(".pg{position:absolute;right:").Append(Margin.ToString(CultureInfo.InvariantCulture)).Append("px;bottom:140px;font-size:40px;color:").Append(deck.Accent).Append('}')
          .Append("</style></head><body>");
        for (var i = 0; i < slides.Count; i++)
        {
            var sl = slides[i];
            sb.Append("<section class=\"card ").Append(sl.Kind == CardNewsSlideKind.Body ? "body" : "hook").Append("\"><div class=\"bar\"></div><h1>")
              .Append(WebUtility.HtmlEncode(sl.Title ?? "")).Append("</h1><p>").Append(WebUtility.HtmlEncode(sl.Body ?? "")).Append("</p><div class=\"pg\">")
              .Append(PageLabel(i, slides.Count)).Append("</div></section>");
        }
        return sb.Append("</body></html>").ToString();
    }

    public static string PageLabel(int index, int total) =>
        (index + 1).ToString(CultureInfo.InvariantCulture) + " / " + total.ToString(CultureInfo.InvariantCulture);

    /// <summary>생성 실행 — HTML 템플릿 선기록 → 텍스트 파일 → ffmpeg 합성. 실패해도 HTML 은 보존([부분 구제]).</summary>
    public async Task<CardNewsReelsResult> GenerateAsync(CardNewsDeck deck, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var err = Validate(deck);
        if (err is not null) return new(false, null, null, err);

        string? html = null;
        string? work = null;
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(deck.OutputPath))!;
            Directory.CreateDirectory(dir);
            html = Path.ChangeExtension(Path.GetFullPath(deck.OutputPath), ".html");
            await File.WriteAllTextAsync(html, RenderHtml(deck), new UTF8Encoding(false), ct).ConfigureAwait(false);

            var exe = _ffmpeg();
            if (exe is null) return new(false, null, html, "ffmpeg 미탑재 — HTML 템플릿만 생성했습니다(9:16 프리뷰).");

            var slides = BuildSlides(deck);
            work = Path.Combine(dir, ".cardnews_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(work);
            var files = new List<(string, string, string)>(slides.Count);
            var utf8 = new UTF8Encoding(false);
            for (var i = 0; i < slides.Count; i++)
            {
                var sl = slides[i];
                var stem = Path.Combine(work, "s" + i.ToString(CultureInfo.InvariantCulture));
                var t = stem + "_t.txt"; var b = stem + "_b.txt"; var p = stem + "_p.txt";
                await File.WriteAllTextAsync(t, Wrap(sl.Title, TitleWrapUnits, TitleMaxLines), utf8, ct).ConfigureAwait(false);
                await File.WriteAllTextAsync(b, Wrap(sl.Body, BodyWrapUnits, BodyMaxLines), utf8, ct).ConfigureAwait(false);
                await File.WriteAllTextAsync(p, PageLabel(i, slides.Count), utf8, ct).ConfigureAwait(false);
                files.Add((t, b, p));
            }

            var r = await FfmpegVideoMixerService.RunAsync(exe, BuildArgs(deck, files, FontResolver.FindKoreanFont(bold: true)),
                deck.OutputPath, TotalSeconds(deck), progress, ct).ConfigureAwait(false);
            return r.Ok
                ? new(true, r.OutputPath, html, "릴스 생성 완료 — " + FfmpegVideoMixerService.Inv(TotalSeconds(deck)) + "초 · 1080×1920")
                : new(false, null, html, r.Message + " — HTML 템플릿은 보존했습니다.");
        }
        catch (OperationCanceledException) { return new(false, null, html, "취소됨"); }
        catch (Exception ex) { return new(false, null, html, "생성 예외: " + ex.Message); }
        finally
        {
            if (work is not null) try { Directory.Delete(work, recursive: true); } catch { /* 임시 폴더 정리 실패 무시 */ }
        }
    }

    private static bool IsHexColor(string? c)
    {
        if (c is null || c.Length != 7 || c[0] != '#') return false;
        for (var i = 1; i < 7; i++) if (!Uri.IsHexDigit(c[i])) return false;
        return true;
    }
}
