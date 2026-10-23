using System.Buffers;
using System.Globalization;

namespace PromptFlowStudio.Services.VodRepurposing;

public enum ArticleTemplate : byte { Blog, Newsletter }

/// <summary>
/// v17.88.0 <see cref="ArrayPool{T}"/> 기반 문자 버퍼 작성기(ValueStringBuilder 대용 · 클래스 필드로 재사용).
/// 용량 부족 시 2 배 대여 → 복사 → 기존 반환. <see cref="Clear"/> 는 길이만 0 — 워밍업 후 무할당.
/// </summary>
public sealed class PooledCharWriter : IDisposable
{
    private char[] _buf;
    private int _len;

    public PooledCharWriter(int capacity = 16 * 1024) => _buf = ArrayPool<char>.Shared.Rent(capacity);

    public int Length => _len;
    public int Capacity => _buf.Length;
    public long Grows { get; private set; }
    public ReadOnlySpan<char> Written => _buf.AsSpan(0, _len);
    public void Clear() => _len = 0;

    public void Append(char c)
    {
        if (_len >= _buf.Length) Grow(1);
        _buf[_len++] = c;
    }

    public void Append(ReadOnlySpan<char> s)
    {
        if (_len + s.Length > _buf.Length) Grow(s.Length);
        s.CopyTo(_buf.AsSpan(_len));
        _len += s.Length;
    }

    public void Append(int v)
    {
        if (_len + 11 > _buf.Length) Grow(11);
        v.TryFormat(_buf.AsSpan(_len), out var w, default, CultureInfo.InvariantCulture);
        _len += w;
    }

    /// <summary>호출자 포매터용 쓰기 창(최소 <paramref name="min"/> 자) — 기록 후 <see cref="Advance"/>.</summary>
    public Span<char> Reserve(int min)
    {
        if (_len + min > _buf.Length) Grow(min);
        return _buf.AsSpan(_len);
    }

    public void Advance(int n) => _len += n;

    public void Line() => Append('\n');

    private void Grow(int need)
    {
        var next = ArrayPool<char>.Shared.Rent(Math.Max(_buf.Length * 2, _len + need));
        _buf.AsSpan(0, _len).CopyTo(next);
        ArrayPool<char>.Shared.Return(_buf);
        _buf = next;
        Grows++;
    }

    public void Dispose()
    {
        var b = _buf;
        _buf = Array.Empty<char>();
        _len = 0;
        if (b.Length > 0) ArrayPool<char>.Shared.Return(b);
    }
}

/// <summary>
/// [PromptFlow Studio v17.88.0] 📰 멀티채널 블로그 &amp; 뉴스레터 마크다운 신디케이터.
///   ▸ 입력: <see cref="LongformTranscriptChapterSummarizerEngine"/> 분석 결과(정제 문장 · 챕터 · SEO). 추임새/반복어는 적재 단계에서 이미 제거.
///   ▸ 구조: H1 제목 → 서론 → 목차(타임코드) → 챕터별 H2(+H3) 본론 → 결론 → 태그.
///     <see cref="ArticleTemplate.Blog"/> = 네이버 블로그/Medium 친근체 · 이모지, <see cref="ArticleTemplate.Newsletter"/> = 전문 어조 · 불릿 중심.
///   ▸ 렌더(<see cref="Render"/>)는 <see cref="PooledCharWriter"/> 에 직접 기록 — 워밍업 후 할당 0. UI 용 문자열은 <see cref="Snapshot"/> 에서만 생성.
/// </summary>
public sealed class MarkdownArticleNewsletterSynthesizer : IDisposable
{
    private static readonly string[] Numbers = { "1️⃣", "2️⃣", "3️⃣", "4️⃣", "5️⃣", "6️⃣", "7️⃣", "8️⃣", "9️⃣", "🔟" };
    private static readonly string[] BlogLeads = { "먼저 ", "이어서 ", "그리고 ", "특히 ", "또 " };

    private readonly PooledCharWriter _w = new();
    private readonly char[] _tmp = new char[512];
    private string? _snapshot;
    private int _snapshotLen = -1;

    public ArticleTemplate Template { get; private set; }
    public int Length => _w.Length;
    public ReadOnlySpan<char> Markdown => _w.Written;
    public long Renders { get; private set; }
    public double LastRenderMs { get; private set; }
    public int Headings { get; private set; }
    public int Bullets { get; private set; }

    /// <summary>분석 결과 → 마크다운(제목 비면 SEO 1 위로 자동 생성) — 기록 길이.</summary>
    public int Render(LongformTranscriptChapterSummarizerEngine src, ArticleTemplate template, ReadOnlySpan<char> title = default)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Template = template;
        _w.Clear();
        Headings = Bullets = 0;
        _snapshotLen = -1;
        var blog = template == ArticleTemplate.Blog;
        var n = src.ChapterCount;

        // ── H1 ──
        H(1, blog ? "🎬 " : default);
        if (!title.IsEmpty) _w.Append(title);
        else if (src.SeoKeywordCount > 0) { Seo(src, 0, false); _w.Append(blog ? " 라이브 완벽 정리" : " — 라이브 브리핑"); }
        else _w.Append("라이브 다시보기 정리");
        _w.Line(); _w.Line();

        // ── 서론 ──
        if (blog)
        {
            _w.Append("> 📅 라이브 다시보기 정리 · ⏱ 총 "); Duration(src.DurationMs); _w.Append(" · 📚 "); _w.Append(n); _w.Append("개 챕터\n\n");
            _w.Append("안녕하세요! 👋 오늘 라이브에서 나눈 이야기를 한눈에 보기 쉽게 정리해 봤어요. ");
            if (src.SeoKeywordCount > 0)
            {
                _w.Append("이번 방송의 핵심 키워드는 ");
                for (var i = 0; i < Math.Min(3, src.SeoKeywordCount); i++) { if (i > 0) _w.Append(", "); _w.Append("**"); Seo(src, i, false); _w.Append("**"); }
                _w.Append(" 이에요. 😊");
            }
            _w.Line();
        }
        else
        {
            _w.Append("**이번 호 요약** — 라이브 방송("); Duration(src.DurationMs); _w.Append(")을 "); _w.Append(n); _w.Append("개 주제로 정리했습니다.\n\n");
            H(2, "핵심 요약"); _w.Line();
            for (var c = 0; c < n; c++)
            {
                _w.Append("- **"); Title(src, c); _w.Append("**");
                var best = src.Chapter(c).Best;
                if (best >= 0) { _w.Append(" — "); Sentence(src, best); }
                _w.Line(); Bullets++;
            }
        }
        _w.Line();

        // ── 목차 ──
        H(2, blog ? "📌 목차" : "목차"); _w.Line();
        for (var c = 0; c < n; c++)
        {
            _w.Append("- `"); Tc(src.Chapter(c).StartMs); _w.Append("` "); Title(src, c); _w.Line(); Bullets++;
        }
        _w.Line();

        // ── 본론 ──
        for (var c = 0; c < n; c++)
        {
            ref readonly var ch = ref src.Chapter(c);
            if (blog) { H(2, c < Numbers.Length ? Numbers[c] : default); if (c >= Numbers.Length) { _w.Append(c + 1); _w.Append('.'); } _w.Append(' '); }
            else { H(2, default); _w.Append(c + 1); _w.Append(". "); }
            Title(src, c); _w.Line();
            if (blog) { _w.Append("⏱ `"); Tc(ch.StartMs); _w.Append(" ~ "); Tc(ch.EndMs); _w.Append("`\n\n"); }
            else { _w.Append('*'); Tc(ch.StartMs); _w.Append(" – "); Tc(ch.EndMs); _w.Append("*\n\n"); }
            if (blog)
            {
                var k = 0;
                for (var j = 0; j < 3; j++)
                {
                    var s = j == 0 ? ch.Sum0 : j == 1 ? ch.Sum1 : ch.Sum2;
                    if (s < 0) continue;
                    if (k > 0) _w.Append(' ');
                    if (k > 0 && k < BlogLeads.Length) _w.Append(BlogLeads[k]);
                    Sentence(src, s); k++;
                }
                _w.Append("\n\n");
                if (ch.Best >= 0) { H(3, "💡 포인트"); _w.Line(); _w.Append("> "); Sentence(src, ch.Best); _w.Append("\n\n"); }
            }
            else
            {
                H(3, "주요 내용"); _w.Line();
                for (var j = 0; j < 3; j++)
                {
                    var s = j == 0 ? ch.Sum0 : j == 1 ? ch.Sum1 : ch.Sum2;
                    if (s < 0) continue;
                    _w.Append("- "); Sentence(src, s); _w.Line(); Bullets++;
                }
                _w.Line();
            }
        }

        // ── 결론 ──
        if (blog)
        {
            H(2, "🙌 마무리"); _w.Line();
            _w.Append("오늘 라이브 정리는 여기까지예요! 전체 영상은 위 타임코드로 원하는 부분부터 바로 보실 수 있어요. 도움이 되셨다면 공감 ❤️ 과 댓글 부탁드려요!\n\n");
            for (var i = 0; i < src.SeoKeywordCount; i++) { if (i > 0) _w.Append(' '); Seo(src, i, true); }
            _w.Line();
        }
        else
        {
            H(2, "결론"); _w.Line();
            _w.Append("이번 방송의 주요 논점은 ");
            for (var i = 0; i < Math.Min(3, src.SeoKeywordCount); i++) { if (i > 0) _w.Append(", "); Seo(src, i, false); }
            _w.Append(" 입니다. 세부 내용은 각 타임코드의 원본 VOD를 참고하시기 바랍니다.\n\n---\n**키워드:** ");
            for (var i = 0; i < src.SeoKeywordCount; i++) { if (i > 0) _w.Append(" · "); Seo(src, i, false); }
            _w.Line();
        }

        Renders++;
        LastRenderMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return _w.Length;
    }

    private void H(int level, ReadOnlySpan<char> prefix)
    {
        for (var i = 0; i < level; i++) _w.Append('#');
        _w.Append(' ');
        if (!prefix.IsEmpty) _w.Append(prefix);
        Headings++;
    }

    private void Title(LongformTranscriptChapterSummarizerEngine src, int c) => _w.Append(_tmp.AsSpan(0, src.WriteChapterTitle(c, _tmp)));

    private void Seo(LongformTranscriptChapterSummarizerEngine src, int i, bool hashtag) => _w.Append(_tmp.AsSpan(0, src.WriteSeo(i, _tmp, hashtag)));

    private void Tc(long ms) => _w.Advance(LongformTranscriptChapterSummarizerEngine.FormatTimecode(ms, _w.Reserve(16)));

    /// <summary>문장 + 종결 부호 보정(마크다운 강조 문자 이스케이프).</summary>
    private void Sentence(LongformTranscriptChapterSummarizerEngine src, int s)
    {
        var t = src.SentenceText(s);
        if (t.IsEmpty) return;
        for (var i = 0; i < t.Length; i++)
        {
            var c = t[i];
            if (c is '*' or '_' or '`' or '#') _w.Append('\\');
            _w.Append(i == 0 && char.IsAsciiLetterLower(c) ? char.ToUpperInvariant(c) : c);
        }
        if (t[^1] is not ('.' or '?' or '!' or '…' or '。')) _w.Append('.');
    }

    private void Duration(long ms)
    {
        var min = ms / 60_000;
        if (min >= 60) { _w.Append((int)(min / 60)); _w.Append("시간 "); }
        _w.Append((int)(min % 60)); _w.Append('분');
    }

    /// <summary>UI 용 문자열(같은 렌더면 캐시 재사용 · 비핫패스).</summary>
    public string Snapshot()
    {
        if (_snapshot is null || _snapshotLen != _w.Length) { _snapshot = new string(_w.Written); _snapshotLen = _w.Length; }
        return _snapshot;
    }

    public void Dispose() => _w.Dispose();
}
