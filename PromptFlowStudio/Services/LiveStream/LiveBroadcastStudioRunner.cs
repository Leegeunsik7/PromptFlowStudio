using System.Buffers.Binary;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.LiveStream;

/// <summary>
/// [PromptFlow Studio v16.71.0] 📺 AI 자율 라이브 방송 &amp; 스트리밍 스튜디오 러너.
/// 60fps 루프(<see cref="TickOnce"/> 무할당): 데모 관객(결정적 채팅/후원/투표 스크립트) → 디렉터 틱 → 카메라 바이어스 → 소스 캡처(데모 활동 스크립트) →
/// 스위처 렌더 → 온에어 시 프로그램 프레임을 대상별 렌디션으로 멀티캐스트. 인코더는 비트레이트 크기 합성 페이로드(실 H.264 인코드 결선 전)이다.
/// </summary>
public sealed class LiveBroadcastStudioRunner : IDisposable
{
    public const float EncodeMs = 8;

    private static readonly string[] CrowdUsers = BuildUsers();
    private static readonly string[][] CrowdLines =
    {
        new[] { "안녕하세요~", "오늘 방송 뭐해요?", "첫 방문입니다", "hello from LA", "음질 좋네요" },
        new[] { "대박!!!", "미쳤다 ㅋㅋㅋ", "레전드 각", "가즈아!!", "POG POG", "와 실화냐" },
        new[] { "ㅋㅋㅋㅋㅋ", "아 웃기다 ㅋㅋ", "귀엽다 ㅎㅎ", "lol 😂", "너무 좋아요" },
        new[] { "ㅠㅠ 슬프다", "눈물나요", "아쉽다 ㅠ", "sad...", "마음 아파" },
    };

    private readonly object _gate = new();
    private readonly AiLiveBroadcastAutonomousSwitchingEngine _sw;
    private readonly InteractiveStreamViewerFeedbackDirector _dir;
    private readonly UltraLowLatencyMultiPlatformStreamer _out;
    private readonly float[] _bias = new float[AiLiveBroadcastAutonomousSwitchingEngine.MaxSources];
    private readonly byte[] _programFrame = new byte[AiLiveBroadcastAutonomousSwitchingEngine.FrameBytes];
    private CancellationTokenSource? _cts;
    private long _frame;
    private ulong _rng = 0xC0FFEE1671UL;
    private bool _onAir, _demoCrowd = true, _demoActivity = true;
    private DirectorState _state;
    private long _onAirSince;

    public LiveBroadcastStudioRunner(IServiceProvider sp)
        : this((AiLiveBroadcastAutonomousSwitchingEngine?)sp.GetService(typeof(AiLiveBroadcastAutonomousSwitchingEngine)) ?? new AiLiveBroadcastAutonomousSwitchingEngine(),
               (InteractiveStreamViewerFeedbackDirector?)sp.GetService(typeof(InteractiveStreamViewerFeedbackDirector)) ?? new InteractiveStreamViewerFeedbackDirector(),
               (UltraLowLatencyMultiPlatformStreamer?)sp.GetService(typeof(UltraLowLatencyMultiPlatformStreamer)) ?? new UltraLowLatencyMultiPlatformStreamer()) { }

    public LiveBroadcastStudioRunner(AiLiveBroadcastAutonomousSwitchingEngine sw, InteractiveStreamViewerFeedbackDirector dir, UltraLowLatencyMultiPlatformStreamer output)
    {
        _sw = sw; _dir = dir; _out = output;
    }

    public event Action? OnChange;

    public AiLiveBroadcastAutonomousSwitchingEngine Switcher => _sw;
    public InteractiveStreamViewerFeedbackDirector Director => _dir;
    public UltraLowLatencyMultiPlatformStreamer Streamer => _out;
    public bool IsRunning => _cts is not null;
    public bool OnAir { get { lock (_gate) return _onAir; } }
    public long Frame { get { lock (_gate) return _frame; } }
    public long NowMs { get { lock (_gate) return _frame * 1000 / AiLiveBroadcastAutonomousSwitchingEngine.Fps; } }
    public long OnAirMs { get { lock (_gate) return _onAir ? (_frame - _onAirSince) * 1000 / AiLiveBroadcastAutonomousSwitchingEngine.Fps : 0; } }
    public DirectorState State { get { lock (_gate) return _state; } }
    public bool DemoCrowd { get { lock (_gate) return _demoCrowd; } set { lock (_gate) _demoCrowd = value; } }
    public bool DemoActivity { get { lock (_gate) return _demoActivity; } set { lock (_gate) _demoActivity = value; } }
    public string? LastAction { get; private set; }

    private static string[] BuildUsers()
    {
        var u = new string[64];
        for (var i = 0; i < u.Length; i++) u[i] = "viewer" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
        return u;
    }

    /// <summary>1프레임(무할당).</summary>
    public void TickOnce()
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Live.Runner.Tick");
            var f = _frame;
            var now = f * 1000 / AiLiveBroadcastAutonomousSwitchingEngine.Fps;
            if (_demoCrowd) Crowd(f, now);
            _state = _dir.Tick(now);
            _dir.FillCameraBias(_sw, _bias);
            _sw.SetBias(_bias);
            if (_demoActivity)
            {
                var seg = (int)(f / 360 % 3);   // 6초 단위 발화 교대(앵커 → 게스트 → 동시 침묵)
                _sw.SetActivity(0, 0.05f, seg == 0 ? 0.8f : 0);
                _sw.SetActivity(1, 0.05f, seg == 1 ? 0.8f : 0);
                _sw.SetActivity(2, 0.1f, 0);
                _sw.SetActivity(3, 0.3f + 0.7f * MathF.Max(0, MathF.Sin(f * 0.01f)), 0);
                _sw.SetActivity(4, 0.4f, 0);
                _sw.SetActivity(5, 0.2f, 0);
            }
            _sw.CaptureDemo(f);
            _sw.RenderFrame(f);
            if (_onAir)
            {
                _sw.CopyFrame(-1, _programFrame);
                _out.Push(_programFrame, f % 120 == 0, now, _sw.PipelineDelayMs + EncodeMs);
            }
            _frame = f + 1;
        }
    }

    private void Crowd(long f, long now)
    {
        var phase = (int)(now / 10_000 % CrowdLines.Length);
        var perSec = phase == 1 ? 9 : phase == 0 ? 2 : 5;
        if (Rand() < perSec / 60f)
        {
            var lines = CrowdLines[phase];
            _dir.Chat(CrowdUsers[(int)(RandU() % (ulong)CrowdUsers.Length)], lines[(int)(RandU() % (ulong)lines.Length)], now);
        }
        if (phase == 1 && Rand() < 0.5f / 60f) _dir.Donate(CrowdUsers[(int)(RandU() % 64)], 0, 5000, now);
        if (_dir.Snapshot().PollOpen && Rand() < 4f / 60f) _dir.Vote(CrowdUsers[(int)(RandU() % 64)], (int)(RandU() % 3 == 0 ? 0 : RandU() % 3), now);
    }

    private ulong RandU() { var x = _rng; x ^= x << 13; x ^= x >> 7; x ^= x << 17; _rng = x; return x * 0x2545F4914F6CDD1DUL; }
    private float Rand() => (RandU() >> 40) * (1f / (1 << 24));

    /// <summary>1-Click 온에어 토글(루프 미가동이면 함께 가동).</summary>
    public bool ToggleOnAir(bool startLoop = true)
    {
        bool on;
        lock (_gate)
        {
            _onAir = !_onAir;
            if (_onAir) _onAirSince = _frame;
            on = _onAir;
        }
        if (on && startLoop) Start();
        LastAction = on ? "🔴 ON AIR · " + _out.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "개 대상 멀티캐스트" : "⬛ 송출 종료";
        Raise();
        return on;
    }

    public void StartPoll() => _dir.StartPoll(3, NowMs, 15_000, 5);

    public void Start()
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(() => LoopAsync(cts.Token));
        Raise();
    }

    public void Stop()
    {
        var c = Interlocked.Exchange(ref _cts, null);
        if (c is null) return;
        c.Cancel(); c.Dispose();
        lock (_gate) _onAir = false;
        LastAction = "⏸ 스튜디오 정지";
        Raise();
    }

    /// <summary>UI 썸네일: 32bpp 하향식 BMP(<paramref name="source"/> = −1 프로그램). 제어 평면(할당 허용).</summary>
    public byte[] SnapshotBmp(int source)
    {
        const int W = AiLiveBroadcastAutonomousSwitchingEngine.W, H = AiLiveBroadcastAutonomousSwitchingEngine.H, N = AiLiveBroadcastAutonomousSwitchingEngine.FrameBytes;
        var bmp = new byte[54 + N];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), W);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), -H);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28), 32);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(34), N);
        _sw.CopyFrame(source, bmp.AsSpan(54));
        return bmp;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / AiLiveBroadcastAutonomousSwitchingEngine.Fps));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { TickOnce(); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("Live.Tick", ex); }
                if ((Frame & 7) == 0) Raise();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Live.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
