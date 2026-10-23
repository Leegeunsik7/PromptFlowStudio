using System.Diagnostics;
using System.Globalization;
using PromptFlowStudio.Services.LiveArchive;
using PromptFlowStudio.Services.MotionTypography;

namespace PromptFlowStudio.Services.VodRepurposing;

/// <summary>v17.88.0 UI 용 챕터 스냅샷(파이프라인 완료 시 1 회 생성 · 불변).</summary>
public sealed record VodChapterView(int Index, long StartMs, long EndMs, string Timecode, string EndTimecode, string Title, string[] Summary, int Words, float Density, float BoundaryScore);

/// <summary>
/// [PromptFlow Studio v17.88.0] 📝 VOD 리퍼포징 &amp; 아티클 스튜디오 러너.
///   ▸ 소스: v17.87 <see cref="LiveArchiveCloudVaultStudioRunner"/> 아카이브 세그먼트(총 길이) 또는 붙여넣은 대본. 대본 미입력 시 아카이브 길이에 맞춘 리허설 대본 합성.
///   ▸ 파이프라인(백그라운드 · in-flight 1): 챕터 분할/요약 → 블로그 · 뉴스레터 마크다운 → 카드뉴스 5 장 + OG 배너 래스터 → 인메모리 PNG.
///   ▸ UI 는 완료 시점 불변 스냅샷(<see cref="Chapters"/> · <see cref="SeoKeywords"/> · 미리보기 데이터 URL)만 읽는다 — 진행 중 엔진 버퍼 직접 접근 없음.
/// </summary>
public sealed class VodRepurposingStudioRunner : IDisposable
{
    public const int DefaultMinutes = 42, MinMinutes = 10, MaxMinutes = 180;

    private static readonly string[][] Topics =
    {
        new[]
        {
            "오늘 라이브 방송 시작합니다 구독자 여러분 반갑습니다.", "이번 주 근황 이야기를 먼저 해볼게요.", "라이브 방송 공지 사항이 하나 있습니다.",
            "구독자 이벤트 당첨자 발표는 방송 끝에 할게요.", "채팅창 인사 전부 보고 있어요.", "오늘 라이브 방송 주제는 영상 편집과 쇼츠 알고리즘입니다.",
        },
        new[]
        {
            "AI 자동 컷편집 기능을 쓰면 편집 시간이 절반으로 줄어요.", "자막 자동 생성은 음성 인식 정확도가 핵심입니다.", "타임라인에서 무음 구간을 자동 컷편집으로 지우면 템포가 살아요.",
            "AI 자동 컷편집 다음에는 자막 싱크를 꼭 확인하세요.", "편집 프리셋을 저장해두면 매번 같은 톤으로 편집할 수 있어요.", "자막 폰트와 자막 위치도 영상 편집 완성도에 영향을 줍니다.",
        },
        new[]
        {
            "쇼츠 알고리즘은 첫 3초 시청 지속률을 가장 중요하게 봐요.", "쇼츠 썸네일보다 첫 장면 후킹이 조회수를 좌우합니다.", "쇼츠 알고리즘 테스트 결과 업로드 시간대도 조회수에 영향이 있었어요.",
            "반복 시청이 많을수록 쇼츠 알고리즘이 추천을 늘립니다.", "조회수가 정체되면 해시태그와 제목 키워드를 바꿔보세요.", "쇼츠 길이는 30초 안쪽이 시청 지속률이 높았습니다.",
        },
        new[]
        {
            "라이브 장비 세팅은 마이크부터 시작하는 게 좋아요.", "콘덴서 마이크는 방음이 안 되면 잡음이 많이 들어옵니다.", "조명은 키 조명 하나와 백라이트 하나면 충분해요.",
            "카메라 노출은 수동으로 고정해야 화면 깜빡임이 없습니다.", "오디오 인터페이스 게인은 피크가 넘지 않게 맞춰주세요.", "라이브 장비 세팅 체크리스트를 커뮤니티에 올려둘게요.",
        },
        new[]
        {
            "채널 수익화 전략은 광고 수익 하나에만 기대면 안 돼요.", "멤버십 혜택을 명확하게 설계하면 후원 전환율이 올라갑니다.", "브랜드 협찬 단가는 평균 조회수 기준으로 협상하세요.",
            "채널 수익화 전략으로 라이브 후원과 멤버십을 같이 운영해요.", "광고 수익은 시청 지속률과 시청자 국가에 따라 달라집니다.", "굿즈 판매도 채널 수익화 전략의 좋은 선택지입니다.",
        },
        new[]
        {
            "시청자 질문 답변 시간을 가져볼게요.", "채팅으로 들어온 질문 중에 편집 프로그램 추천 질문이 많네요.", "시청자 질문 답변 중에 장비 예산 질문도 있었어요.",
            "초보 크리에이터는 업로드 주기를 먼저 정하는 걸 추천해요.", "다음 라이브 방송에서도 시청자 질문 답변 코너를 할게요.", "오늘 라이브 방송 시청해주셔서 감사합니다.",
        },
    };

    private static readonly int[] TopicWeights = { 4, 8, 9, 6, 8, 7 };
    private static readonly string[] Fillers = { "음, ", "어 ", "그니까 ", "약간 ", "이제 ", "뭐 " };

    private readonly LiveArchiveCloudVaultStudioRunner? _archive;
    private readonly CancellationTokenSource _life = new();
    private readonly char[] _gen = new char[512], _kicker = new char[96], _punch = new char[320], _footer = new char[200], _title = new char[160];
    private readonly string?[] _preview = new string?[ViralCardNewsOgRasterizerEngine.Slots];
    private readonly string?[] _slideText = new string?[ViralCardNewsOgRasterizerEngine.Slots];
    private Task? _pipeline;
    private int _busy;
    private uint _seed = 0x5EEDF00Du;
    private string? _transcript;

    public VodRepurposingStudioRunner(IServiceProvider sp) : this(sp.GetService(typeof(LiveArchiveCloudVaultStudioRunner)) as LiveArchiveCloudVaultStudioRunner) { }

    public VodRepurposingStudioRunner(LiveArchiveCloudVaultStudioRunner? archive = null, SdfGlyphAtlas? atlas = null)
    {
        _archive = archive;
        Rasterizer = new ViralCardNewsOgRasterizerEngine(atlas);
    }

    public event Action? OnChange;
    public LongformTranscriptChapterSummarizerEngine Summarizer { get; } = new();
    public MarkdownArticleNewsletterSynthesizer Blog { get; } = new();
    public MarkdownArticleNewsletterSynthesizer Newsletter { get; } = new();
    public ViralCardNewsOgRasterizerEngine Rasterizer { get; }
    public bool HasArchive => _archive is not null;
    public bool Busy => Volatile.Read(ref _busy) != 0;
    public ArticleTemplate Template { get; set; } = ArticleTemplate.Blog;
    public int SelectedChapter { get; set; }
    public int SelectedSlide { get; set; }
    public string Title { get; set; } = "";
    public bool UserTranscript => _transcript is not null;
    public long Runs { get; private set; }
    public long Failures { get; private set; }
    public double LastPipelineMs { get; private set; }
    public double LastRasterMs { get; private set; }
    public long LastPngBytes { get; private set; }
    public string? LastAction { get; private set; }
    public string SourceLabel { get; private set; } = "리허설 대본(" + DefaultMinutes.ToString(CultureInfo.InvariantCulture) + "분)";
    public VodChapterView[] Chapters { get; private set; } = Array.Empty<VodChapterView>();
    public string[] SeoKeywords { get; private set; } = Array.Empty<string>();
    public string ChapterList { get; private set; } = "";
    public string? PreviewUrl(int slot) => _preview[slot];
    public string? SlideText(int slot) => _slideText[slot];
    public string Markdown(ArticleTemplate t) => t == ArticleTemplate.Blog ? Blog.Snapshot() : Newsletter.Snapshot();

    // ══════════════════════ 소스(비핫패스) ══════════════════════

    /// <summary>v17.87 아카이브 확정 세그먼트 총 길이(분) — 없으면 0.</summary>
    public int ArchiveMinutes()
    {
        if (_archive is null) return 0;
        var rec = _archive.Recorder;
        long us = 0;
        for (var i = rec.FirstIndex; i < rec.SegmentCount; i++) us += rec.Segment(i).DurationUs;
        return (int)(us / 60_000_000L);
    }

    /// <summary>붙여넣은 대본 사용(빈 문자열 = 리허설 대본으로 복귀).</summary>
    public int LoadTranscript(string? text)
    {
        if (Busy) return 0;
        _transcript = string.IsNullOrWhiteSpace(text) ? null : text;
        if (_transcript is null) Summarizer.Clear();
        var n = _transcript is null ? 0 : Summarizer.Load(_transcript);
        SourceLabel = _transcript is null ? "리허설 대본" : "붙여넣은 대본 · 발화 " + n.ToString(CultureInfo.InvariantCulture) + "개";
        LastAction = _transcript is null ? "📄 대본 해제 — 리허설 대본 사용" : "📄 대본 적재 — 발화 " + n.ToString(CultureInfo.InvariantCulture) + "개 · 추임새 " + Summarizer.FillersRemoved.ToString(CultureInfo.InvariantCulture) + "개 정제";
        Raise();
        return n;
    }

    /// <summary>리허설 대본 합성(6 주제 · 추임새/반복어 포함) — 아카이브가 있으면 그 길이에 맞춘다.</summary>
    public int LoadRehearsal(int minutes = 0)
    {
        if (minutes <= 0) { var a = ArchiveMinutes(); minutes = a >= MinMinutes ? a : DefaultMinutes; }
        minutes = Math.Clamp(minutes, MinMinutes, MaxMinutes);
        Summarizer.Clear();
        _seed = 0x5EEDF00Du;
        var totalW = 0;
        foreach (var w in TopicWeights) totalW += w;
        long t = 0, total = minutes * 60_000L;
        var topic = 0; long topicEnd = TopicWeights[0] * total / totalW;
        while (t < total)
        {
            while (t >= topicEnd && topic < Topics.Length - 1) { topic++; topicEnd += TopicWeights[topic] * total / totalW; }
            var lines = Topics[topic];
            var n = 0;
            if (Next(4) == 0) n = Put(n, Fillers[Next(Fillers.Length)]);
            var s = lines[Next(lines.Length)];
            var sp = s.IndexOf(' ');
            if (sp > 0 && Next(5) == 0) { n = Put(n, s.AsSpan(0, sp)); n = Put(n, " "); }   // 반복어("라이브 라이브")
            n = Put(n, s);
            var dur = (long)(n / LongformTranscriptChapterSummarizerEngine.CharsPerSecond * 1000) + 600 + Next(900);
            Summarizer.AddUtterance(t, t + dur, _gen.AsSpan(0, n));
            t += dur;
        }
        _transcript = null;
        var arch = ArchiveMinutes();
        SourceLabel = arch >= MinMinutes ? "v17.87 아카이브 VOD " + arch.ToString(CultureInfo.InvariantCulture) + "분 · 리허설 대본" : "리허설 대본(" + minutes.ToString(CultureInfo.InvariantCulture) + "분)";
        return Summarizer.UtteranceCount;
    }

    private int Put(int n, ReadOnlySpan<char> s)
    {
        var k = Math.Min(s.Length, _gen.Length - n);
        s[..k].CopyTo(_gen.AsSpan(n));
        return n + k;
    }

    private int Next(int mod)
    {
        _seed ^= _seed << 13; _seed ^= _seed >> 17; _seed ^= _seed << 5;
        return (int)(_seed % (uint)mod);
    }

    // ══════════════════════ 파이프라인 ══════════════════════

    /// <summary>백그라운드 파이프라인 실행(진행 중이면 그 작업을 기다린다) — 성공 여부.</summary>
    public async Task<bool> RunAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0) { var p = _pipeline; if (p is not null) await p.ConfigureAwait(false); return false; }
        LastAction = "⚙️ 리퍼포징 중… (챕터 → 아티클 → 카드뉴스)";
        Raise();
        var ok = false;
        try
        {
            var task = Task.Run(RunPipeline, _life.Token);
            _pipeline = task;
            await task.ConfigureAwait(false);
            ok = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Failures++;
            LastAction = "⚠️ 리퍼포징 실패 — " + ex.Message;
            PromptFlowStudio.Program.Log("VodRepurposing.Run", ex);
        }
        finally { Volatile.Write(ref _busy, 0); }
        Raise();
        return ok;
    }

    private void RunPipeline()
    {
        var t0 = Stopwatch.GetTimestamp();
        if (_transcript is not null) Summarizer.Load(_transcript);
        else if (Summarizer.UtteranceCount == 0) LoadRehearsal();
        Summarizer.Analyze();
        _life.Token.ThrowIfCancellationRequested();
        var title = Title.AsSpan().Trim();
        Blog.Render(Summarizer, ArticleTemplate.Blog, title);
        Newsletter.Render(Summarizer, ArticleTemplate.Newsletter, title);
        Blog.Snapshot(); Newsletter.Snapshot();
        BuildViews();
        _life.Token.ThrowIfCancellationRequested();
        RenderCards();
        Runs++;
        LastPipelineMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        LastAction = "✅ 리퍼포징 완료 — 챕터 " + Summarizer.ChapterCount.ToString(CultureInfo.InvariantCulture) + "개 · 아티클 2종 · 카드뉴스 " +
                     ViralCardNewsOgRasterizerEngine.Slides.ToString(CultureInfo.InvariantCulture) + "장 + OG · " + LastPipelineMs.ToString("0", CultureInfo.InvariantCulture) + "ms";
    }

    private void BuildViews()
    {
        var s = Summarizer;
        var views = new VodChapterView[s.ChapterCount];
        Span<char> tc = stackalloc char[16];
        for (var c = 0; c < views.Length; c++)
        {
            ref readonly var ch = ref s.Chapter(c);
            var n = 0;
            var sum = new string[(ch.Sum0 >= 0 ? 1 : 0) + (ch.Sum1 >= 0 ? 1 : 0) + (ch.Sum2 >= 0 ? 1 : 0)];
            if (ch.Sum0 >= 0) sum[n++] = new string(s.SentenceText(ch.Sum0));
            if (ch.Sum1 >= 0) sum[n++] = new string(s.SentenceText(ch.Sum1));
            if (ch.Sum2 >= 0) sum[n++] = new string(s.SentenceText(ch.Sum2));
            var a = new string(tc[..LongformTranscriptChapterSummarizerEngine.FormatTimecode(ch.StartMs, tc)]);
            var b = new string(tc[..LongformTranscriptChapterSummarizerEngine.FormatTimecode(ch.EndMs, tc)]);
            views[c] = new VodChapterView(c, ch.StartMs, ch.EndMs, a, b, new string(_title.AsSpan(0, s.WriteChapterTitle(c, _title))), sum, ch.Words, ch.Density, ch.BoundaryScore);
        }
        var seo = new string[s.SeoKeywordCount];
        for (var i = 0; i < seo.Length; i++) seo[i] = new string(_title.AsSpan(0, s.WriteSeo(i, _title)));
        var list = new char[64 * Math.Max(1, s.ChapterCount) + 64];
        ChapterList = new string(list.AsSpan(0, s.WriteChapterList(list)));
        Chapters = views;
        SeoKeywords = seo;
        if (SelectedChapter >= views.Length) SelectedChapter = 0;
    }

    /// <summary>카드뉴스 5 장(표지 · 하이라이트 3 · 아웃트로) + OG 배너 — 글리프 일괄 준비 후 렌더/인코딩.</summary>
    private void RenderCards()
    {
        var t0 = Stopwatch.GetTimestamp();
        var s = Summarizer; var r = Rasterizer;
        var arch = ArchiveMinutes();
        if (arch > 0) r.SynthesizeKeyframe(arch + s.ChapterCount); else r.SynthesizeKeyframe(s.ChapterCount);
        Span<int> pick = stackalloc int[3];
        PickHighlights(pick, out var picks);

        // 글리프 준비(비핫패스) — 모든 슬롯 텍스트를 먼저 구성해 Ensure
        for (var pass = 0; pass < 2; pass++)
        {
            for (var slot = 0; slot < ViralCardNewsOgRasterizerEngine.Slots; slot++)
            {
                int kn, pn, fn;
                ComposeSlot(slot, pick, picks, out kn, out pn, out fn);
                if (pass == 0)
                {
                    r.Prepare(_kicker.AsSpan(0, kn)); r.Prepare(_punch.AsSpan(0, pn)); r.Prepare(_footer.AsSpan(0, fn));
                    r.Prepare("0123456789 /");
                    continue;
                }
                if (slot == ViralCardNewsOgRasterizerEngine.OgSlot) r.RenderOg(_kicker.AsSpan(0, kn), _punch.AsSpan(0, pn), _footer.AsSpan(0, fn));
                else r.RenderCard(_kicker.AsSpan(0, kn), _punch.AsSpan(0, pn), _footer.AsSpan(0, fn), slot, ViralCardNewsOgRasterizerEngine.Slides);
                LastPngBytes = r.Encode(slot);
                _slideText[slot] = new string(_punch.AsSpan(0, pn));
                _preview[slot] = "data:image/png;base64," + Convert.ToBase64String(r.Png(slot, true));
            }
        }
        LastRasterMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
    }

    /// <summary>요약 최고점 문장이 강한 챕터 3 개(시간순).</summary>
    private void PickHighlights(Span<int> pick, out int count)
    {
        var s = Summarizer;
        count = 0;
        for (var k = 0; k < 3; k++)
        {
            var best = -1; var bestScore = -1f;
            for (var c = 0; c < s.ChapterCount; c++)
            {
                var b = s.Chapter(c).Best;
                if (b < 0 || pick[..count].Contains(c)) continue;
                if (s.Sentence(b).Score > bestScore) { bestScore = s.Sentence(b).Score; best = c; }
            }
            if (best < 0) break;
            pick[count++] = best;
        }
        pick[..count].Sort();
    }

    private void ComposeSlot(int slot, ReadOnlySpan<int> pick, int picks, out int kn, out int pn, out int fn)
    {
        var s = Summarizer;
        kn = pn = fn = 0;
        if (slot == 0 || slot == ViralCardNewsOgRasterizerEngine.OgSlot)
        {
            kn = Fill(_kicker, 0, slot == 0 ? "LIVE 다시보기" : "LIVE VOD");
            pn = WriteTitle(_punch);
            if (slot == 0)
            {
                s.ChapterCount.TryFormat(_footer, out fn, default, CultureInfo.InvariantCulture);
                fn = Fill(_footer, fn, "개 챕터 · 총 ");
                ((int)(s.DurationMs / 60_000)).TryFormat(_footer.AsSpan(fn), out var w, default, CultureInfo.InvariantCulture); fn += w;
                fn = Fill(_footer, fn, "분 · PromptFlow Studio");
            }
            else
                for (var i = 0; i < Math.Min(3, s.SeoKeywordCount); i++) { if (i > 0) fn = Fill(_footer, fn, " · "); fn += s.WriteSeo(i, _footer.AsSpan(fn)); }
            return;
        }
        if (slot == ViralCardNewsOgRasterizerEngine.Slides - 1)
        {
            kn = Fill(_kicker, 0, "FULL VOD");
            pn = Fill(_punch, 0, "전체 영상은 타임코드로 원하는 장면부터 다시 보세요");
            for (var i = 0; i < s.SeoKeywordCount; i++) { if (i > 0) fn = Fill(_footer, fn, " "); fn += s.WriteSeo(i, _footer.AsSpan(fn), true); }
            return;
        }
        var k = slot - 1;
        if (k >= picks) { kn = Fill(_kicker, 0, "HIGHLIGHT"); pn = WriteTitle(_punch); return; }
        var c = pick[k];
        ref readonly var ch = ref s.Chapter(c);
        kn = Fill(_kicker, 0, "CHAPTER ");
        (c + 1).TryFormat(_kicker.AsSpan(kn), out var cw, default, CultureInfo.InvariantCulture); kn += cw;
        kn = Fill(_kicker, kn, " · ");
        kn += LongformTranscriptChapterSummarizerEngine.FormatTimecode(ch.StartMs, _kicker.AsSpan(kn));
        var text = s.SentenceText(ch.Best).TrimEnd('.');
        pn = Fill(_punch, 0, text);
        fn = s.WriteChapterTitle(c, _footer);
    }

    private int WriteTitle(char[] dst)
    {
        var t = Title.AsSpan().Trim();
        if (!t.IsEmpty) return Fill(dst, 0, t);
        if (Summarizer.SeoKeywordCount == 0) return Fill(dst, 0, "라이브 다시보기 핵심 정리");
        var n = Summarizer.WriteSeo(0, dst);
        return Fill(dst, n, " 핵심 정리");
    }

    private static int Fill(char[] dst, int n, ReadOnlySpan<char> s)
    {
        var k = Math.Min(s.Length, dst.Length - n);
        if (k <= 0) return n;
        s[..k].CopyTo(dst.AsSpan(n));
        return n + k;
    }

    /// <summary>슬롯 다운로드 파일명(card_1_1080.png · og_1200x630.png).</summary>
    public static string DownloadName(int slot) =>
        slot == ViralCardNewsOgRasterizerEngine.OgSlot ? "og_1200x630.png" : "card_" + (slot + 1).ToString(CultureInfo.InvariantCulture) + "_1080.png";

    public void Reset()
    {
        if (Busy) return;
        Summarizer.Clear();
        _transcript = null;
        Chapters = Array.Empty<VodChapterView>();
        SeoKeywords = Array.Empty<string>();
        ChapterList = "";
        Array.Clear(_preview); Array.Clear(_slideText);
        Runs = Failures = 0; LastPipelineMs = LastRasterMs = 0; LastPngBytes = 0;
        SelectedChapter = SelectedSlide = 0;
        SourceLabel = "리허설 대본(" + DefaultMinutes.ToString(CultureInfo.InvariantCulture) + "분)";
        LastAction = "♻ 초기화";
        Raise();
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("VodRepurposing.OnChange", ex); }
    }

    public string Summary() =>
        "챕터 " + Chapters.Length.ToString(CultureInfo.InvariantCulture) + " · SEO " + SeoKeywords.Length.ToString(CultureInfo.InvariantCulture) +
        " · 블로그 " + Blog.Length.ToString(CultureInfo.InvariantCulture) + "자 · 뉴스레터 " + Newsletter.Length.ToString(CultureInfo.InvariantCulture) + "자 · 실행 " + Runs.ToString(CultureInfo.InvariantCulture);

    public void Dispose()
    {
        _life.Cancel();
        try { _pipeline?.Wait(2000); } catch (AggregateException) { }
        Blog.Dispose();
        Newsletter.Dispose();
        Rasterizer.Dispose();
        _life.Dispose();
        OnChange = null;
    }
}
