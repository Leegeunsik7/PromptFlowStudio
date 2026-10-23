using System.Globalization;

namespace PromptFlowStudio.Services.GoldenHourEngagement;

/// <summary>댓글 스트림 1 행 — 값 타입 · 링 슬롯 재사용(본문은 정적 풀 인덱스 참조).</summary>
public struct GoldenHourCommentRow
{
    public long Id;
    public int Pool;
    public double TUnix;
    public CommentSentiment Sentiment;
    public CommentHideReason Hide;
    public bool Hidden, Replied, Queued;
}

/// <summary>
/// [PromptFlow Studio v17.95.0] 💬 골든아워 댓글 &amp; 인게이지먼트 부스터 러너(HUD <c>GheRun</c>).
///   ▸ 체인: 업로드 → <see cref="GoldenHourSmartEngagementResponder"/> 60 s 내 앵커 고정 → 5 s 틱 댓글 스트림 →
///     <see cref="ZeroAllocCommentSentimentTriageEngine"/> 분류/숨김 → 질문 · 긍정 답글 큐 → <see cref="EngagementVelocityAntiBotSafetyGuard"/> 지터/쿼터 허가 → 답글 발행.
///   ▸ 시뮬레이션 가상 시계(5 s 단위 전진) · 결정적 xorshift 댓글 생성기 · 인메모리 <see cref="SimulatedCommentModerationGateway"/>.
/// </summary>
public sealed class GoldenHourEngagementStudioRunner : IDisposable
{
    public const int StepSec = 5, RowCap = 40, QueueCap = 64, LogSize = 32, GoldenHourSec = 3600;
    public const long SimEpoch = 1_800_000_000;

    /// <summary>모의 댓글 풀 — [0, BenignCount) 일반 시청자 · 이후 유해(스팸 · 욕설 · 트롤 · 도배).</summary>
    public static readonly string[] PoolAuthors =
    {
        "민지", "코딩하는곰", "jay_k", "하루", "서연", "초보유튜버", "Leo", "다온", "현우", "수아", "익명1", "taro", "시청자7", "민호",
        "수익왕", "crypto_bot", "맞구독해요", "어그로꾼", "troll99", "도배맨",
    };

    public static readonly string[] PoolTexts =
    {
        "와 이번 편 진짜 최고예요 👍", "꿀팁 감사합니다 덕분에 해결했어요", "awesome edit, love it 🔥", "ㅋㅋㅋ 편집 센스 대박", "항상 응원합니다 ❤",
        "이거 어떤 프로그램으로 편집하셨나요?", "what mic do you use?", "다음 편은 언제 올라와요?", "설정값 어디서 바꾸는지 알려주세요", "BGM 제목 궁금해요!",
        "방금 봤어요", "첫 댓글", "이번 건 좀 지루하고 아쉽네요", "솔직히 별로였음",
        "부업으로 월 500 수익 보장 👉 https://bit.ly/x9 무료 상담 카톡", "free money dm me https://t.me/xx www.scam.xyz", "맞구독 하실 분 선착순 클릭 https://open.kakao.com/o/x",
        "ㅅㅂ 이게 뭐냐 시간 버렸네", "이딴 걸 영상이라고 올리냐 구독취소함", "ㅏㅏㅏㅏㅏㅏㅏㅏㅏㅏㅏㅏㅏㅏ",
    };

    public const int BenignCount = 14, SpamFirst = 14, SpamCount = 3, QuestionFirst = 5, QuestionCount = 5;

    private readonly GoldenHourCommentRow[] _rows = new GoldenHourCommentRow[RowCap];
    private readonly GoldenHourCommentRow[] _queue = new GoldenHourCommentRow[QueueCap];
    private readonly string[] _log = new string[LogSize];
    private int _rowHead, _rowCount, _qHead, _qCount, _logHead, _logCount;
    private long _seq;
    private uint _rng = 0x2545F491u;

    public GoldenHourEngagementStudioRunner(IServiceProvider sp) : this() { }

    public GoldenHourEngagementStudioRunner()
    {
        Triage = new ZeroAllocCommentSentimentTriageEngine(Gateway);
    }

    public event Action? OnChange;
    public SimulatedCommentModerationGateway Gateway { get; } = new();
    public ZeroAllocCommentSentimentTriageEngine Triage { get; }
    public GoldenHourSmartEngagementResponder Responder { get; } = new();
    public EngagementVelocityAntiBotSafetyGuard Guard { get; } = new();
    public double SimNow { get; private set; } = SimEpoch;
    public bool AutoReply { get; private set; } = true;
    public long Ticks { get; private set; }
    public long Replies { get; private set; }
    public long ReplyFailures { get; private set; }
    public long QueueDrops { get; private set; }
    public int QueueCount => _qCount;
    public int RowCount => _rowCount;
    public int LogCount => _logCount;
    public string? LastAction { get; private set; }
    public long ElapsedSec => Responder.Uploaded ? (long)(SimNow - Responder.UploadUnix) : 0;
    public long RemainingSec => Responder.Uploaded ? Math.Max(0, GoldenHourSec - ElapsedSec) : GoldenHourSec;
    public float HourlyRatio => Guard.HourlyRatio(SimNow);
    public int HourlyCount => Guard.CountInWindow(SimNow);

    /// <summary>최근 댓글(0 = 최신).</summary>
    public ref readonly GoldenHourCommentRow Row(int i) => ref _rows[(_rowHead - 1 - i + RowCap * 2) % RowCap];

    /// <summary>최근 로그(0 = 최신).</summary>
    public string Log(int i) => _log[(_logHead - 1 - i + LogSize * 2) % LogSize];

    // ══════════════════════ 조작(비핫패스) ══════════════════════

    /// <summary>업로드(골든아워 시작) — 스트림 · 큐 · 가드 초기화 후 즉시 앵커 고정 시도.</summary>
    public void Upload()
    {
        ClearStream();
        Responder.MarkUploaded(SimNow);
        Push("🎬 업로드 — 골든아워 60 분 시작 · 톤 " + GoldenHourSmartEngagementResponder.ToneName(Responder.Tone) + " · 시간당 상한 " + Id(Guard.HourlyCap));
        TryPin();
        LastAction = "🎬 업로드 · " + (Responder.Pinned ? "📌 고정 완료" : "📌 고정 재시도 대기");
        Raise();
    }

    /// <summary>5 s 단위 전진 — 고정 재시도 · 댓글 유입/분류/숨김 · 답글 허가/발행. 반환 = 유입 댓글 수.</summary>
    public int Advance(int seconds)
    {
        if (!Responder.Uploaded) Upload();
        var steps = Math.Max(1, seconds / StepSec);
        var ingested = 0;
        for (var k = 0; k < steps; k++) ingested += Step();
        LastAction = "⏩ +" + Mmss(steps * (long)StepSec) + " · 유입 " + Id(ingested) + " · 숨김 " + Id(Triage.Hidden) + " · 답글 " + Id(Replies) + " · 시간당 " + Id(HourlyCount) + "/" + Id(Guard.HourlyCap);
        Raise();
        return ingested;
    }

    public void RunGoldenHour()
    {
        if (!Responder.Uploaded) Upload();
        var remain = RemainingSec;
        if (remain > 0) Advance((int)remain);
    }

    /// <summary>1 틱(5 s). 핫패스 경로(분류 · 조합 · 허가)는 무할당 — 로그 문자열은 이벤트 발생 시에만.</summary>
    private int Step()
    {
        SimNow += StepSec;
        Ticks++;
        if (!Responder.Pinned) TryPin();
        var n = 0;
        var x = Next();
        if (x % 3 != 0) { Ingest(PickPool(x)); n++; }
        if (ElapsedSec < 600 && (x >> 3) % 2 == 0) { Ingest(PickPool(Next())); n++; }   // 첫 10 분 유입 급증
        ProcessReplies();
        return n;
    }

    public void InjectSpamRaid()
    {
        if (!Responder.Uploaded) Upload();
        var before = Triage.Hidden;
        for (var i = 0; i < 6; i++) Ingest(SpamFirst + i % (PoolTexts.Length - SpamFirst));
        LastAction = "🚨 스팸 · 트롤 공습 6 건 주입 → 숨김 +" + Id(Triage.Hidden - before);
        Raise();
    }

    public void InjectQuestionFlood()
    {
        if (!Responder.Uploaded) Upload();
        for (var i = 0; i < 12; i++) Ingest(QuestionFirst + i % QuestionCount);
        LastAction = "❓ 질문 폭주 12 건 주입 — 답글 큐 " + Id(_qCount) + " · 지터/쿼터 가드 관찰";
        Raise();
    }

    public void InjectGatewayFailure()
    {
        Gateway.InjectFailures(2, 503);
        LastAction = "🧪 다음 2 회 게이트웨이 503 주입 — 고정/답글 재시도 관찰";
        Raise();
    }

    public void RepublishPinned()
    {
        if (!Responder.Uploaded) { LastAction = "⚠️ 업로드 전 — 먼저 「🎬 업로드 · 골든아워 시작」"; Raise(); return; }
        var ok = Responder.Republish(SimNow, Gateway);
        Push(ok ? "📌 고정 댓글 재발행 — " + Clip(Responder.PinnedTemplate) : "⚠️ 고정 재발행 실패 HTTP " + Id(Responder.LastPinStatus) + " — 다음 틱 재시도");
        LastAction = ok ? "📌 고정 댓글 재발행 완료" : "⚠️ 고정 재발행 실패 HTTP " + Id(Responder.LastPinStatus);
        Raise();
    }

    public void SetTemplate(string? text)
    {
        Responder.PinnedTemplate = text ?? "";
        LastAction = "✏️ 고정 댓글 템플릿 " + Id(Responder.PinnedTemplate.Length) + "자";
        Raise();
    }

    public void SetTone(ChannelTone tone)
    {
        Responder.Tone = tone;
        LastAction = "🎭 채널 톤: " + GoldenHourSmartEngagementResponder.ToneName(tone);
        Raise();
    }

    public void SetHourlyCap(int cap)
    {
        Guard.HourlyCap = cap;
        LastAction = "🛡️ 시간당 답글 상한 " + Id(Guard.HourlyCap);
        Raise();
    }

    public void ToggleAutoReply()
    {
        AutoReply = !AutoReply;
        LastAction = "🤖 자동 답글 " + (AutoReply ? "ON" : "OFF");
        Raise();
    }

    public void Reset()
    {
        ClearStream();
        Responder.Reset();
        Gateway.Reset();
        SimNow = SimEpoch;
        Array.Clear(_log);
        _logHead = _logCount = 0;
        LastAction = "♻ 초기화";
        Raise();
    }

    // ══════════════════════ 내부 ══════════════════════

    private void ClearStream()
    {
        Array.Clear(_rows); Array.Clear(_queue);
        _rowHead = _rowCount = _qHead = _qCount = 0;
        Triage.Reset(); Guard.Reset();
        Ticks = Replies = ReplyFailures = QueueDrops = 0;
        _rng = 0x2545F491u;
    }

    private void TryPin()
    {
        if (Responder.Pinned) return;
        if (!Responder.TryPublishPinned(SimNow, Gateway))
        {
            if (Responder.PinAttempts <= 3) Push("⚠️ 고정 댓글 실패 HTTP " + Id(Responder.LastPinStatus) + " — 다음 틱 재시도");
            return;
        }
        Push((Responder.PinnedInWindow ? "📌 앵커 고정 +" : "⏰ 앵커 지연 고정 +") + Responder.PinDelaySec.ToString("0", CultureInfo.InvariantCulture) + "s — " + Clip(Responder.PinnedTemplate));
    }

    private void Ingest(int pool)
    {
        var id = ++_seq;
        var r = Triage.Triage(id, PoolTexts[pool].AsSpan());
        ref var row = ref _rows[_rowHead];
        row = new GoldenHourCommentRow { Id = id, Pool = pool, TUnix = SimNow, Sentiment = r.Sentiment, Hide = r.Hide, Hidden = r.ShouldHide && Triage.AlreadyHidden(id) };
        _rowHead = (_rowHead + 1) % RowCap;
        if (_rowCount < RowCap) _rowCount++;
        if (row.Hidden) Push("🛡️ 숨김 #" + Id(id) + " " + PoolAuthors[pool] + " · " + ZeroAllocCommentSentimentTriageEngine.HideReasonText(r.Hide));
        else if (r.ShouldHide) Push("⚠️ 숨김 실패 #" + Id(id) + " HTTP " + Id(Triage.LastHideStatus));
        if (row.Hidden || !AutoReply || r.Sentiment is not (CommentSentiment.Question or CommentSentiment.Positive)) return;
        row.Queued = true;
        if (_qCount == QueueCap) { _qHead = (_qHead + 1) % QueueCap; _qCount--; QueueDrops++; }
        _queue[(_qHead + _qCount) % QueueCap] = row;
        _qCount++;
    }

    private void ProcessReplies()
    {
        if (_qCount == 0 || !AutoReply) return;
        var wasCooling = Guard.CoolingDown;
        var d = Guard.TryAcquire(SimNow, out var wait);
        if (d == VelocityDecision.CoolDown)
        {
            if (!wasCooling) Push("🧊 시간당 상한 " + Id(Guard.HourlyCap) + " 도달 — 쿨다운 " + Mmss((long)wait) + " · 대기 큐 " + Id(_qCount));
            return;
        }
        if (d != VelocityDecision.Granted) return;
        ref readonly var p = ref _queue[_qHead];
        Responder.TryCompose(p.Sentiment, p.Id, PoolAuthors[p.Pool].AsSpan());
        var st = 0;
        try { st = Gateway.PostReply(p.Id, Responder.LastReply); }
        catch (Exception ex) { PromptFlowStudio.Program.Log("GoldenHourEngagement.Reply", ex); st = 599; }
        if (st is < 200 or >= 300) { ReplyFailures++; Push("⚠️ 답글 실패 #" + Id(p.Id) + " HTTP " + Id(st) + " — 큐 유지"); return; }
        Replies++;
        MarkReplied(p.Id);
        Push("↩️ 답글 #" + Id(p.Id) + " " + new string(Responder.LastReply) + " · 다음 지터 " + Guard.LastJitterSec.ToString("0.0", CultureInfo.InvariantCulture) + "s");
        _qHead = (_qHead + 1) % QueueCap;
        _qCount--;
    }

    private void MarkReplied(long id)
    {
        for (var i = 0; i < RowCap; i++)
            if (_rows[i].Id == id) { _rows[i].Replied = true; _rows[i].Queued = false; return; }
    }

    private int PickPool(uint x)
    {
        var idx = (int)(x % (uint)PoolTexts.Length);
        if (idx >= BenignCount && (x >> 16) % 3 != 0) idx = (int)((x >> 4) % BenignCount);
        return idx;
    }

    private uint Next()
    {
        var x = _rng;
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;
        return _rng = x;
    }

    private void Push(string line)
    {
        lock (_log)
        {
            _log[_logHead] = "[" + Mmss(ElapsedSec) + "] " + line;
            _logHead = (_logHead + 1) % LogSize;
            if (_logCount < LogSize) _logCount++;
        }
    }

    private static string Clip(string s) => s.Length > 40 ? s[..40] + "…" : s;
    private static string Id(long v) => v.ToString(CultureInfo.InvariantCulture);
    public static string Pct(float v) => (v * 100f).ToString("0.0", CultureInfo.InvariantCulture) + "%";
    public static string Mmss(long sec) => (sec / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (sec % 60).ToString("00", CultureInfo.InvariantCulture);

    public string Summary() =>
        "스캔 " + Id(Triage.Scanned) + " · 숨김 " + Id(Triage.Hidden) + " · 답글 " + Id(Replies) + " · 지터 대기 " + Id(Guard.JitterDeferrals) + " · 쿨다운 " + Id(Guard.CoolDowns) +
        " · 큐 " + Id(_qCount) + " · 게이트웨이 호출 " + Id(Gateway.Calls);

    private void Raise()
    {
        try { OnChange?.Invoke(); }
        catch (Exception ex) { PromptFlowStudio.Program.Log("GoldenHourEngagement.OnChange", ex); }
    }

    public void Dispose() => OnChange = null;
}
