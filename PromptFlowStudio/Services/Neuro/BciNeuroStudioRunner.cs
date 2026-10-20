using System.Diagnostics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.World;

namespace PromptFlowStudio.Services.Neuro;

/// <summary>
/// [PromptFlow Studio v16.56.0] 🧠 BCI 바이오 미디어 스튜디오 러너 — 관객 감정 시나리오 아크(평온 → 긴장 → 공포 피크 → 안도 → 이탈 → 환희 → 해소)
/// → 시뮬레이터 PFBS 패킷 → 감정 파서 → 집단 집계(피크) → 변조기(집단/개인 입력) → 분기 결정 → 그레이딩 프리뷰(4D 월드 프레임 우선 · 테스트 카드 폴백).
/// 32Hz PeriodicTimer 루프 · <see cref="TickOnce"/> 무할당(테스트 공용).
/// </summary>
public sealed class BciNeuroStudioRunner : IDisposable
{
    public const int FrameWidth = WorldModelStudioRunner.FrameWidth, FrameHeight = WorldModelStudioRunner.FrameHeight, TickHz = 32;
    public const double ArcSeconds = 120.0;
    // 시각(s) · 몰입 · 흥분 · 쾌적 · 공포
    private static readonly float[] Arc =
    {
        0f, 0.55f, 0.35f, 0.6f, 0.1f,
        15f, 0.55f, 0.35f, 0.6f, 0.1f,
        28f, 0.85f, 0.75f, 0.35f, 0.55f,
        30f, 0.95f, 0.95f, 0.2f, 0.9f,
        34f, 0.92f, 0.9f, 0.25f, 0.8f,
        40f, 0.7f, 0.5f, 0.55f, 0.25f,
        50f, 0.4f, 0.3f, 0.5f, 0.1f,
        62f, 0.35f, 0.3f, 0.5f, 0.1f,
        67f, 0.92f, 0.88f, 0.85f, 0.1f,
        73f, 0.9f, 0.85f, 0.85f, 0.1f,
        80f, 0.6f, 0.4f, 0.7f, 0.1f,
        120f, 0.55f, 0.35f, 0.6f, 0.1f,
    };

    private readonly object _gate = new();
    private readonly NeuroBioSignalEmotionParserEngine _parser;
    private readonly BioFeedbackGenerativeMediaModifier _mod;
    private readonly CollectiveEmotionHeatmapAggregator _agg;
    private readonly WorldModelStudioRunner? _world;
    private readonly NeuroSignalSimulator _sim = new();
    private readonly byte[] _base = new byte[FrameWidth * FrameHeight * 3];
    private readonly byte[] _front = new byte[FrameWidth * FrameHeight * 3];
    private readonly float[] _gain = new float[NeuroBioSignalEmotionParserEngine.MaxSubjects];
    private readonly float[] _lag = new float[NeuroBioSignalEmotionParserEngine.MaxSubjects];
    private readonly float[] _bias = new float[NeuroBioSignalEmotionParserEngine.MaxSubjects];
    private CancellationTokenSource? _cts;
    private double _media, _clock;
    private long _frameSeq, _worldSeq = -1;
    private int _audience;

    public BciNeuroStudioRunner(IServiceProvider sp)
        : this((NeuroBioSignalEmotionParserEngine?)sp.GetService(typeof(NeuroBioSignalEmotionParserEngine)) ?? new NeuroBioSignalEmotionParserEngine(),
               (BioFeedbackGenerativeMediaModifier?)sp.GetService(typeof(BioFeedbackGenerativeMediaModifier)) ?? new BioFeedbackGenerativeMediaModifier(),
               (CollectiveEmotionHeatmapAggregator?)sp.GetService(typeof(CollectiveEmotionHeatmapAggregator)) ?? new CollectiveEmotionHeatmapAggregator(),
               sp.GetService(typeof(WorldModelStudioRunner)) as WorldModelStudioRunner) { }

    public BciNeuroStudioRunner(NeuroBioSignalEmotionParserEngine parser, BioFeedbackGenerativeMediaModifier mod, CollectiveEmotionHeatmapAggregator agg, WorldModelStudioRunner? world = null)
    {
        _parser = parser; _mod = mod; _agg = agg; _world = world;
        BuildTestCard(_base);
        _base.AsSpan().CopyTo(_front);
        SetAudience(48);
    }

    public event Action? OnChange;

    public bool IsRunning => _cts is not null;
    public bool UseCollective { get; set; } = true;
    public int FocusSubject { get; set; }
    public double MediaTimeSec => _media;
    public long FrameSeq => Interlocked.Read(ref _frameSeq);
    public int Audience => _audience;
    public NeuroSignalSimulator Simulator => _sim;
    public string? LastAction { get; private set; }

    /// <summary>시뮬 관객 수 설정(개인차: 반응 배율 · 지연 · 편향 · 좌석 배치).</summary>
    public void SetAudience(int n)
    {
        lock (_gate)
        {
            _audience = Math.Clamp(n, 1, Math.Min(NeuroBioSignalEmotionParserEngine.MaxSubjects, CollectiveEmotionHeatmapAggregator.SeatRows * CollectiveEmotionHeatmapAggregator.SeatCols));
            _sim.SetCount(_audience);
            var rng = 0x51ED27u;
            for (var i = 0; i < _audience; i++)
            {
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                _gain[i] = 0.7f + (rng & 255) / 255f * 0.5f;
                _lag[i] = ((rng >> 8) & 255) / 255f * 1.5f;
                _bias[i] = (((rng >> 16) & 255) / 255f - 0.5f) * 0.1f;
                _agg.SetSeat(i, i / 12, i % 12 + 10);
            }
        }
        LastAction = "👥 관객 " + _audience + "명";
        Raise();
    }

    /// <summary>시나리오 아크 보간(시각 s → 몰입/흥분/쾌적/공포).</summary>
    public static void ArcAt(double t, out float eng, out float aro, out float val, out float fear)
    {
        t %= ArcSeconds;
        if (t < 0) t += ArcSeconds;
        var k = 0;
        while (k + 5 < Arc.Length && Arc[k + 5] <= t) k += 5;
        var k2 = Math.Min(k + 5, Arc.Length - 5);
        var span = Arc[k2] - Arc[k];
        var u = span > 0 ? (float)((t - Arc[k]) / span) : 0f;
        eng = Arc[k + 1] + (Arc[k2 + 1] - Arc[k + 1]) * u;
        aro = Arc[k + 2] + (Arc[k2 + 2] - Arc[k + 2]) * u;
        val = Arc[k + 3] + (Arc[k2 + 3] - Arc[k + 3]) * u;
        fear = Arc[k + 4] + (Arc[k2 + 4] - Arc[k + 4]) * u;
    }

    /// <summary>1틱(무할당) — 관객 목표 → 신호 합성/파싱 → 집계 → 변조 → 프레임 그레이딩.</summary>
    public void TickOnce(double dtSec)
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Neuro.Runner.Tick");
            _media += dtSec; _clock += dtSec;
            for (var i = 0; i < _audience; i++)
            {
                ArcAt(_media - _lag[i], out var e, out var a, out var v, out var f);
                var g = _gain[i]; var b = _bias[i];
                _sim.SetTarget(i, 0.5f + (e - 0.5f) * g + b, 0.5f + (a - 0.5f) * g + b, 0.5f + (v - 0.5f) * g - b, f * g);
            }
            _sim.Step(_parser, dtSec);
            var t = (long)(_clock * 1e6);
            for (var i = 0; i < _audience; i++)
            {
                var idx = _parser.IndexOf(_sim.Device(i));
                if (idx >= 0 && _parser.TryGetState(idx, out var s)) _agg.Report(i, s, t);
            }
            _agg.Aggregate(t, _media);
            var focus = _parser.IndexOf(_sim.Device(Math.Clamp(FocusSubject, 0, _audience - 1)));
            var input = UseCollective ? _agg.CollectiveState : _parser.StateAt(focus);
            _mod.Tick(input, dtSec);
            if (_world is not null && _world.FrameSeq != _worldSeq && _world.FrameSeq > 0) { _world.CopyFrame(_base); _worldSeq = _world.FrameSeq; }
            _base.AsSpan().CopyTo(_front);
            _mod.GradeFrame(_front, FrameWidth, FrameHeight);
            Interlocked.Increment(ref _frameSeq);
        }
    }

    public void CopyFrame(Span<byte> dst) { lock (_gate) _front.AsSpan().CopyTo(dst); }

    /// <summary>집중 피험자 파서 인덱스.</summary>
    public int FocusIndex => _parser.IndexOf(_sim.Device(Math.Clamp(FocusSubject, 0, _audience - 1)));

    private static void BuildTestCard(byte[] rgb)
    {
        for (var y = 0; y < FrameHeight; y++)
        {
            for (var x = 0; x < FrameWidth; x++)
            {
                var o = (y * FrameWidth + x) * 3;
                var sky = y / (float)FrameHeight;
                float r = 40 + 120 * sky, g = 60 + 80 * sky, b = 140 - 40 * sky;
                var dx = (x - FrameWidth * 0.5f) / 14f; var dy = (y - FrameHeight * 0.62f) / 30f;
                if (dx * dx + dy * dy < 1f || y > FrameHeight * 0.85f) { r = 30; g = 26; b = 34; }
                var mx = (x - FrameWidth * 0.78f) / 10f; var my = (y - FrameHeight * 0.22f) / 10f;
                if (mx * mx + my * my < 1f) { r = 240; g = 230; b = 200; }
                rgb[o] = (byte)r; rgb[o + 1] = (byte)g; rgb[o + 2] = (byte)b;
            }
        }
    }

    public void Start()
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(() => LoopAsync(cts.Token));
        LastAction = "▶ BCI 바이오피드백 루프";
        Raise();
    }

    public void Stop()
    {
        var c = Interlocked.Exchange(ref _cts, null);
        if (c is null) return;
        c.Cancel(); c.Dispose();
        LastAction = "⏸ 루프 정지";
        Raise();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / TickHz));
        var last = Stopwatch.GetTimestamp();
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var now = Stopwatch.GetTimestamp();
                var dt = Math.Min(0.25, (now - last) / (double)Stopwatch.Frequency);
                last = now;
                TickOnce(dt);
                if ((FrameSeq & 7) == 0) Raise();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { PromptFlowStudio.Program.Log("Neuro.Runner.Loop", ex); }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Neuro.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
