using System.Diagnostics;
using System.Globalization;
using PromptFlowStudio.Services.LiveRestream;

namespace PromptFlowStudio.Services.LiveEngagement;

/// <summary>
/// [PromptFlow Studio v17.86.0] 🎉 라이브 시청자 인터랙션 &amp; 위젯 러너.
///   ▸ 입력: v17.83 <see cref="OmniLiveChatSuperChatAggregatorEngine"/> 링을 비파괴 Seq 커서로 열람(송출 러너의 리액션 Drain 과 독립) →
///     투표 토큰 → <see cref="RealtimeAudienceVotingQuizEngine"/> · 슈퍼챗/비트 KRW → <see cref="DonationGoalStingerTrigger"/>.
///     미연결/무채팅 시 가상 시청자 시뮬레이터(선호 가중 · 10 % 중복 재투표 시도)로 리허설.
///   ▸ 60 Hz 틱(무할당): 위젯 → 오버레이 캔버스 → 소스 비디오 국소 복원 → <see cref="ZeroAllocAlphaOverlayCompositorEngine"/> 1080p 더티 영역 Over 합성.
/// </summary>
public sealed class LiveAudienceEngagementStudioRunner : IDisposable
{
    public const int LiveHz = 60, SimViewers = 4000, NameCap = 16;
    public const long FrameUs = 1_000_000 / LiveHz;
    public static readonly PixelRect PollPanel = new(560, 40, 800, 220);
    public static readonly PixelRect GoalBar = new(460, 990, 1000, 48);
    public static readonly long[] TestDonationsKrw = { 1_000, 10_000, 50_000 };
    private static readonly float[] SimBias = { 0.42f, 0.28f, 0.18f, 0.12f };

    private readonly object _gate = new();
    private readonly char[] _name = new char[NameCap];
    private uint[]? _frame;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _chatSeen = -1, _simAcc;
    private uint _seed = 0xC0FFEE11u;

    public LiveAudienceEngagementStudioRunner(IServiceProvider sp) : this(sp.GetService(typeof(OmniLiveRestreamStudioRunner)) as OmniLiveRestreamStudioRunner) { }

    public LiveAudienceEngagementStudioRunner(OmniLiveRestreamStudioRunner? restream = null) => Restream = restream;

    public event Action? OnChange;
    public OmniLiveRestreamStudioRunner? Restream { get; }
    public ZeroAllocAlphaOverlayCompositorEngine Compositor { get; } = new();
    public RealtimeAudienceVotingQuizEngine Voting { get; } = new();
    public DonationGoalStingerTrigger Donation { get; } = new();
    public bool Running => _loop is { IsCompleted: false };
    public bool SimulateAudience { get; set; } = true;
    /// <summary>가상 시청자 투표율(표/s).</summary>
    public int SimVotesPerSec { get; set; } = 40;
    public long Ticks { get; private set; }
    public long ChatPulled { get; private set; }
    public long ChatVotes { get; private set; }
    public long ChatDonations { get; private set; }
    public long SimVotes { get; private set; }
    public double LastTickUs { get; private set; }
    public string? LastAction { get; private set; }

    /// <summary>합성 결과 1080p 프레임(지연 1 회 할당 · 소스 = 프로시저럴 방송 화면).</summary>
    public ReadOnlySpan<uint> Frame => EnsureFrame();

    private uint[] EnsureFrame()
    {
        if (_frame is not null) return _frame;
        var f = new uint[ZeroAllocAlphaOverlayCompositorEngine.Width * ZeroAllocAlphaOverlayCompositorEngine.Height];
        RestoreSource(f, new PixelRect(0, 0, ZeroAllocAlphaOverlayCompositorEngine.Width, ZeroAllocAlphaOverlayCompositorEngine.Height));
        return _frame = f;
    }

    /// <summary>소스 비디오 픽셀(대각 그라디언트 · 불투명).</summary>
    public static uint SourcePixel(int x, int y) => ZeroAllocAlphaOverlayCompositorEngine.Rgba((byte)(20 + x * 60 / 1920), (byte)(30 + y * 70 / 1080), (byte)(90 + (x + y) * 60 / 3000), 255);

    private static void RestoreSource(uint[] f, in PixelRect r)
    {
        for (var y = r.Y; y < r.Bottom; y++)
        {
            var row = y * ZeroAllocAlphaOverlayCompositorEngine.Width;
            for (var x = r.X; x < r.Right; x++) f[row + x] = SourcePixel(x, y);
        }
    }

    // ══════════════════════ 제어(비핫패스) ══════════════════════

    public bool CreatePoll(string question, string[] options, bool quiz, int correct, int seconds)
    {
        bool ok;
        lock (_gate) ok = Voting.Configure(question, options, quiz ? VoteMode.Quiz : VoteMode.Poll, correct, seconds);
        LastAction = ok ? (quiz ? "🧠 퀴즈" : "🗳️ 투표") + " 시작 — " + options.Length.ToString(CultureInfo.InvariantCulture) + " 지선다 · " + (seconds > 0 ? seconds.ToString(CultureInfo.InvariantCulture) + " s" : "수동 마감") : "⚠️ 선택지는 2~4 개";
        Raise();
        return ok;
    }

    public void ClosePoll()
    {
        lock (_gate) Voting.Close();
        LastAction = "⏹ 투표 마감 — 총 " + Voting.Total.ToString(CultureInfo.InvariantCulture) + " 표";
        Raise();
    }

    public long RevealQuiz()
    {
        long n;
        lock (_gate) n = Voting.Reveal();
        LastAction = Voting.Revealed ? "✅ 정답 공개 — " + Voting.Label(Voting.CorrectIndex) + " · 정답자 " + n.ToString(CultureInfo.InvariantCulture) + " 명" : "⚠️ 퀴즈 모드/정답 미지정";
        Raise();
        return n;
    }

    /// <summary>테스트 후원 발사(₩ · 임계 이상이면 3 s 골드 스팅어).</summary>
    public bool FireTestDonation(long krw)
    {
        bool fire;
        lock (_gate) fire = Donation.OnDonation(krw, "테스트후원");
        LastAction = "💰 테스트 후원 ₩" + krw.ToString("N0", CultureInfo.InvariantCulture) + (fire ? " · ✨ 골드 스팅어 3 s" : null) + (Donation.GoalReached ? " · 🎯 목표 달성" : null);
        Raise();
        return fire;
    }

    public void SetGoal(long krw)
    {
        lock (_gate) Donation.SetGoal(krw);
        LastAction = "🎯 후원 목표 ₩" + Donation.GoalKrw.ToString("N0", CultureInfo.InvariantCulture);
        Raise();
    }

    public void Start()
    {
        if (Running) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew(); long last = 0, lastRaise = 0;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var now = sw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
                    var dt = last == 0 ? FrameUs : Math.Clamp(now - last, 1_000L, 100_000L);
                    last = now;
                    Tick(dt);
                    if (now - lastRaise >= 250_000) { lastRaise = now; Raise(); }
                    await Task.Delay(1000 / LiveHz, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { PromptFlowStudio.Program.Log("LiveEngagement.Loop", ex); }
        }, ct);
        LastAction = "▶ 오버레이 합성 루프 시작(60 Hz)";
        Raise();
    }

    public void Stop()
    {
        var cts = _cts; var loop = _loop;
        if (cts is null) return;
        cts.Cancel();
        try { loop?.Wait(1000); } catch (AggregateException) { }
        cts.Dispose(); _cts = null; _loop = null;
        LastAction = "⏸ 합성 루프 정지";
        Raise();
    }

    /// <summary>수동 N 프레임(16.7 ms) 전진.</summary>
    public void Step(int frames = 1)
    {
        for (var i = 0; i < frames; i++) Tick(FrameUs);
        Raise();
    }

    public void Reset()
    {
        if (Running) Stop();
        lock (_gate)
        {
            Voting.Reset(); Donation.Reset(); Compositor.Reset();
            if (_frame is not null) RestoreSource(_frame, new PixelRect(0, 0, ZeroAllocAlphaOverlayCompositorEngine.Width, ZeroAllocAlphaOverlayCompositorEngine.Height));
            Ticks = ChatPulled = ChatVotes = ChatDonations = SimVotes = 0; _simAcc = 0; LastTickUs = 0;
            _chatSeen = Restream?.Chat.Total ?? -1;
        }
        LastAction = "♻ 초기화";
        Raise();
    }

    // ══════════════════════ 60 Hz 틱(무할당 핫패스) ══════════════════════

    public void Tick(long dtUs)
    {
        lock (_gate)
        {
            var t0 = Stopwatch.GetTimestamp();
            PullChat();
            if (SimulateAudience && Voting.Open) SimulateVotes(dtUs);
            Voting.Tick(dtUs);
            Donation.Tick(dtUs);
            Compose();
            Ticks++;
            LastTickUs = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
        }
    }

    /// <summary>위젯 기록 → 직전/현재 영역 소스 복원 → 국소 Over 합성.</summary>
    private void Compose()
    {
        var f = EnsureFrame();
        var c = Compositor;
        c.BeginOverlay();
        Voting.Render(c, PollPanel);
        Donation.Render(c, GoalBar);
        for (var i = 0; i < c.PrevCount; i++) RestoreSource(f, c.Prev(i));
        for (var i = 0; i < c.RegionCount; i++) RestoreSource(f, c.Region(i));
        c.Composite(f);
    }

    /// <summary>옴니 채팅 링 비파괴 열람(Seq 커서) — 투표 토큰 · 후원 KRW.</summary>
    private void PullChat()
    {
        var chat = Restream?.Chat;
        if (chat is null) return;
        var total = chat.Total;
        if (_chatSeen < 0) { _chatSeen = total; return; }   // 최초 결선 = 과거 이력 무시
        if (total <= _chatSeen) return;
        var n = (int)Math.Min(total - _chatSeen, chat.Recent);
        for (var i = n - 1; i >= 0; i--)
        {
            ref readonly var e = ref chat.Event(i);
            if (e.Seq < _chatSeen) continue;   // 경합 덮어쓰기 슬롯
            ChatPulled++;
            if (Voting.Ingest(chat.Author(i), chat.Text(i)) == VoteResult.Accepted) ChatVotes++;
            if (e.AmountKrw > 0 && e.Kind is ChatKind.SuperChat or ChatKind.Bits) { Donation.OnDonation(e.AmountKrw, chat.Author(i)); ChatDonations++; }
        }
        _chatSeen = total;
    }

    /// <summary>가상 시청자 — 선호 가중 선택지 · 10 % 는 기투표자 재시도(중복 방어 검증).</summary>
    private void SimulateVotes(long dtUs)
    {
        _simAcc += dtUs * SimVotesPerSec;
        var guard = 0;
        while (_simAcc >= 1_000_000 && guard++ < 256)
        {
            _simAcc -= 1_000_000;
            var viewer = (int)(Rand() * SimViewers);
            if (Rand() < 0.1f && Voting.VoterCount > 0) viewer = (int)(Rand() * Math.Min(Voting.VoterCount, SimViewers));
            var r = Rand(); var opt = 0; var acc = 0f; float sum = 0;
            for (var i = 0; i < Voting.OptionCount; i++) sum += SimBias[i];
            for (var i = 0; i < Voting.OptionCount; i++) { acc += SimBias[i] / sum; if (r <= acc) { opt = i; break; } opt = i; }
            var nameLen = WriteName(viewer);
            var key = Voting.Key(opt, Voting.OptionCount == 2 && Rand() < 0.5f ? 1 : 0);
            if (Voting.Ingest(_name.AsSpan(0, nameLen), key) == VoteResult.Accepted) SimVotes++;
        }
    }

    private int WriteName(int viewer)
    {
        _name[0] = 'v'; _name[1] = 'i'; _name[2] = 'e'; _name[3] = 'w'; _name[4] = 'e'; _name[5] = 'r';
        viewer.TryFormat(_name.AsSpan(6), out var w, default, CultureInfo.InvariantCulture);
        return 6 + w;
    }

    private float Rand()
    {
        _seed ^= _seed << 13; _seed ^= _seed >> 17; _seed ^= _seed << 5;
        return (_seed & 0xFFFFFF) / 16777216f;
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("LiveEngagement.OnChange", ex); }
    }

    public string Summary() =>
        (Voting.HasPoll ? (Voting.Mode == VoteMode.Quiz ? "퀴즈 " : "투표 ") + Voting.Total.ToString(CultureInfo.InvariantCulture) + " 표 · 중복 차단 " + Voting.Duplicates.ToString(CultureInfo.InvariantCulture) : "투표 대기") +
        " · 후원 ₩" + Donation.CurrentKrw.ToString("N0", CultureInfo.InvariantCulture) + "/" + Donation.GoalKrw.ToString("N0", CultureInfo.InvariantCulture) +
        " · 합성 " + Compositor.AvgUs.ToString("0", CultureInfo.InvariantCulture) + " µs";

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        try { _loop?.Wait(1000); } catch (AggregateException) { }
        _cts?.Dispose(); _cts = null; _loop = null;
        OnChange = null;
    }
}
