using PromptFlowStudio.Services.VerticalTranscode;

namespace PromptFlowStudio.Services.LiveToReels;

public enum ReelsPlatform : byte { Instagram, YouTube, TikTok }
public enum ReelsUploadPhase : byte { Queued, Uploading, Backoff, Done, Failed }

/// <summary>v17.69.0 플랫폼 업로드 레그(값 형식 · 워커 스레드 소유).</summary>
public struct ReelsUploadLeg
{
    public ReelsPlatform Platform;
    public ReelsUploadPhase Phase;
    public long Sent, Total;
    public int ChunkSize, Retries;
    public double StartedMs, FinishedMs, NextAtMs;
    public uint Hash;
    public readonly int ChunkCount => ChunkSize > 0 ? (int)((Total + ChunkSize - 1) / ChunkSize) : 0;
    public readonly int ChunkIndex => ChunkSize > 0 ? (int)Math.Min(Sent / ChunkSize, Math.Max(0, ChunkCount - 1)) : 0;
    public readonly float Progress => Total > 0 ? (float)((double)Sent / Total) : 0f;
}

/// <summary>v17.69.0 업로드 청크 전송(워커 스레드 · 무할당). false = 일시 실패(지수 백오프 재시도).</summary>
public interface IReelsUploadTransport
{
    bool Send(ReelsPlatform platform, int reelId, long offset, long total, ReadOnlySpan<byte> chunk);
}

/// <summary>v17.69.0 업로드 작업(릴 1 개 × 3 플랫폼 레그).</summary>
public sealed class ReelsUploadJob
{
    internal ReelsUploadJob(int reelId, SegmentedChunkStream media)
    {
        ReelId = reelId; Media = media;
        for (var i = 0; i < Legs.Length; i++) { Legs[i].Platform = (ReelsPlatform)i; Legs[i].Total = media.Length; Legs[i].ChunkSize = NonBlockingOmnichannelPublishingGuard.ChunkSizeOf((ReelsPlatform)i); }
    }
    public int ReelId { get; }
    public SegmentedChunkStream Media { get; }
    public ReelsUploadLeg[] Legs { get; } = new ReelsUploadLeg[NonBlockingOmnichannelPublishingGuard.Platforms];
    public bool Finished { get { foreach (ref readonly var l in Legs.AsSpan()) if (l.Phase is not (ReelsUploadPhase.Done or ReelsUploadPhase.Failed)) return false; return true; } }
}

/// <summary>
/// v17.69.0 공유 업링크 병목 모델(가정용/현장 라우터 FIFO) — 라이브 비트스트림 + 릴스 업로드가 같은 큐를 공유 → 업로드 과잉 시 라이브 지연/드롭.
/// </summary>
public sealed class SimulatedUplinkModel : IReelsUploadTransport
{
    public const double OverflowMs = 250;
    private readonly long[] _recv = new long[NonBlockingOmnichannelPublishingGuard.Platforms];
    private readonly uint[] _hash = new uint[NonBlockingOmnichannelPublishingGuard.Platforms];
    private readonly int[] _reel = new int[NonBlockingOmnichannelPublishingGuard.Platforms];
    private double _queue, _pendingUpload;

    public double CapacityKbps { get; set; } = 20_000;
    public double LiveKbps { get; set; } = 6_000;
    /// <summary>가드가 모르는 교차 트래픽(동일 회선 타 기기) — 예산 추정 오차 → 지연 기반 배압만이 보호.</summary>
    public double CrossTrafficKbps { get; set; }
    public double QueueDelayMs => _queue * 8 / Math.Max(1, CapacityKbps - CrossTrafficKbps);
    public double LiveOfferedBytes { get; private set; }
    public double LiveDroppedBytes { get; private set; }
    public long LiveDropEvents { get; private set; }
    public int FailEvery { get; set; }
    public long Calls { get; private set; }
    public long Rejected { get; private set; }
    public long Received(ReelsPlatform p) => _recv[(int)p];
    public uint ReceivedHash(ReelsPlatform p) => _hash[(int)p];

    /// <summary>청크 수신(오프셋 연속성 검증 · FNV-1a 무결성 해시).</summary>
    public bool Send(ReelsPlatform platform, int reelId, long offset, long total, ReadOnlySpan<byte> chunk)
    {
        Calls++;
        var i = (int)platform;
        if (FailEvery > 0 && Calls % FailEvery == 0) { Rejected++; return false; }
        if (offset == 0) { _reel[i] = reelId; _recv[i] = 0; _hash[i] = NonBlockingOmnichannelPublishingGuard.FnvSeed; }
        if (_reel[i] != reelId || offset != _recv[i]) { Rejected++; return false; }
        _recv[i] += chunk.Length; _hash[i] = NonBlockingOmnichannelPublishingGuard.Fnv(_hash[i], chunk);
        _pendingUpload += chunk.Length;
        return true;
    }

    /// <summary>dt 진행 — 라이브 먼저 도착(인코더 CBR) · 업로드 합산 · 용량만큼 배출 · 초과 큐 = 라이브 프레임 드롭.</summary>
    public void Advance(double dtSec)
    {
        var live = LiveKbps * 125 * dtSec;
        LiveOfferedBytes += live;
        _queue += live + _pendingUpload; _pendingUpload = 0;
        _queue = Math.Max(0, _queue - Math.Max(0, CapacityKbps - CrossTrafficKbps) * 125 * dtSec);
        var cap = OverflowMs * CapacityKbps / 8;
        if (_queue > cap) { var drop = Math.Min(_queue - cap, live); LiveDroppedBytes += drop; LiveDropEvents++; _queue = cap; }
    }

    public void Reset() { _queue = _pendingUpload = 0; LiveOfferedBytes = LiveDroppedBytes = 0; LiveDropEvents = Calls = Rejected = 0; Array.Clear(_recv); Array.Clear(_reel); }
}

/// <summary>
/// [PromptFlow Studio v17.69.0] 논블로킹 옴니채널 배포 &amp; 네트워크 배압 세이프티 가드.
///   ▸ 격리: 전용 워커(ThreadPriority.Lowest · 배경) · 10 ms 틱 · MCR 틱 ms 프로브 &gt; YieldMs 면 해당 틱 전송 양보(CPU 사이클 반납) · 64 KB 스크래치 무할당 전송.
///   ▸ 대역 예산: 업로드 상한 = 용량 − 라이브 비트레이트 × Headroom(1 순위 보장) · 토큰 버킷(50 ms 버스트).
///   ▸ 배압(LEDBAT 형): 큐 지연 &gt; TargetDelayMs → 승산 감소(×0.7 · 100 ms 1 회) · 이하 → 가산 증가(+Additive/s) · 하한 MinKbps.
///   ▸ Instagram(4 MB) · YouTube(8 MB · 256 KiB 배수) · TikTok(10 MB) 청크 레그 라운드로빈 병렬 · 실패 시 지수 백오프(≤ 5 회) · FNV-1a 무결성.
/// </summary>
public sealed class NonBlockingOmnichannelPublishingGuard : IDisposable
{
    public const int Platforms = 3, MaxJobs = 8, Hist = 240, SliceBytes = 64 * 1024, TickMs = 10, MaxRetries = 5;
    public const uint FnvSeed = 2166136261;
    public const double TargetDelayMs = 40, Headroom = 1.25, MinKbps = 256, AdditiveKbpsPerSec = 2_000, YieldMs = 13;

    private readonly object _gate = new();
    private readonly ReelsUploadJob?[] _jobs = new ReelsUploadJob?[MaxJobs];
    private readonly byte[] _scratch = new byte[SliceBytes];
    private readonly float[] _rateS = new float[Hist], _delayS = new float[Hist];
    private readonly long[] _sentBy = new long[Platforms];
    private readonly ManualResetEventSlim _wake = new(false);
    private Thread? _worker;
    private volatile bool _running;
    private double _nowMs, _tokens, _rateKbps = 2_000, _lastCutMs = -1e9, _histAcc;
    private int _rr, _sHead, _sN;

    public NonBlockingOmnichannelPublishingGuard(IReelsUploadTransport? transport = null)
    {
        Link = new SimulatedUplinkModel();
        Transport = transport ?? Link;
    }

    public event Action<ReelsUploadJob>? OnJobFinished;
    public SimulatedUplinkModel Link { get; }
    public IReelsUploadTransport Transport { get; set; }
    /// <summary>MCR 렌더 틱 ms 프로브(v17.66) — 예산 근접 시 업로드 양보.</summary>
    public Func<double>? FrameProbe { get; set; }
    /// <summary>실측 큐 지연 프로브(실 네트워크) — 미지정 시 업링크 모델.</summary>
    public Func<double>? DelayProbe { get; set; }
    public double CapacityKbps { get => Link.CapacityKbps; set => Link.CapacityKbps = Math.Clamp(value, 500, 1_000_000); }
    public double LiveKbps { get => Link.LiveKbps; set => Link.LiveKbps = Math.Clamp(value, 0, 200_000); }
    public double BudgetKbps => Math.Max(MinKbps, CapacityKbps - LiveKbps * Headroom);
    public double RateKbps => _rateKbps;
    public double ThrottleRatio => BudgetKbps > 0 ? _rateKbps / BudgetKbps : 0;
    public double QueueDelayMs => DelayProbe?.Invoke() ?? Link.QueueDelayMs;
    public bool Throttling => ThrottleRatio < 0.8;
    public bool IsRunning => _running;
    public bool WorkerLowPriority => _worker is { } w && w.Priority == ThreadPriority.Lowest;
    public long Ticks { get; private set; }
    public long Throttles { get; private set; }
    public long Yields { get; private set; }
    public long Retries { get; private set; }
    public long Completed { get; private set; }
    public long FailedLegs { get; private set; }
    public long SentBytes { get; private set; }
    public double LastStepUs { get; private set; }
    public double MaxStepUs { get; private set; }
    public long SentBy(ReelsPlatform p) => _sentBy[(int)p];
    public int SeriesCount => _sN;
    public float RateSeries(int i) => _rateS[(_sHead - 1 - i + 2 * Hist) % Hist];
    public float DelaySeries(int i) => _delayS[(_sHead - 1 - i + 2 * Hist) % Hist];
    /// <summary>v17.51 실 API 핸드오프 승인(라이브 큐 여유 · 스로틀 해제 상태).</summary>
    public bool CanHandoff => QueueDelayMs < TargetDelayMs && ThrottleRatio >= 0.8;

    public static int ChunkSizeOf(ReelsPlatform p) => p switch { ReelsPlatform.Instagram => 4 * 1024 * 1024, ReelsPlatform.YouTube => 32 * 256 * 1024, _ => 10 * 1024 * 1024 };
    public static string PlatformName(ReelsPlatform p) => p switch { ReelsPlatform.Instagram => "Instagram Reels", ReelsPlatform.YouTube => "YouTube Shorts", _ => "TikTok" };
    public static string PhaseName(ReelsUploadPhase p) => p switch { ReelsUploadPhase.Uploading => "업로드", ReelsUploadPhase.Backoff => "백오프", ReelsUploadPhase.Done => "완료", ReelsUploadPhase.Failed => "실패", _ => "대기" };

    public static uint Fnv(uint h, ReadOnlySpan<byte> s) { for (var i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619; } return h; }

    // ══════════════════════ 제어(비핫패스) ══════════════════════
    public ReelsUploadJob? Enqueue(int reelId, SegmentedChunkStream media)
    {
        lock (_gate)
        {
            for (var i = 0; i < MaxJobs; i++)
                if (_jobs[i] is null || _jobs[i]!.Finished) { var j = new ReelsUploadJob(reelId, media); for (var k = 0; k < Platforms; k++) j.Legs[k].Hash = FnvSeed; _jobs[i] = j; _wake.Set(); return j; }
        }
        return null;
    }

    public ReelsUploadJob? Find(int reelId) { lock (_gate) { foreach (var j in _jobs) if (j?.ReelId == reelId) return j; } return null; }
    public int ActiveLegs { get { var n = 0; lock (_gate) foreach (var j in _jobs) if (j is not null) foreach (ref readonly var l in j.Legs.AsSpan()) if (l.Phase is ReelsUploadPhase.Queued or ReelsUploadPhase.Uploading or ReelsUploadPhase.Backoff) n++; return n; } }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _worker = new Thread(Loop) { IsBackground = true, Name = "PF.LiveToReels.Upload" };
        try { _worker.Priority = ThreadPriority.Lowest; } catch (PlatformNotSupportedException) { }
        _worker.Start();
    }

    public void Stop()
    {
        _running = false; _wake.Set();
        var w = _worker; _worker = null;
        if (w is not null && w != Thread.CurrentThread) w.Join(2000);
    }

    private void Loop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew(); var last = 0.0;
        while (_running)
        {
            try
            {
                var now = sw.Elapsed.TotalMilliseconds;
                Step(Math.Clamp((now - last) / 1000.0, 0.001, 0.1)); last = now;
            }
            catch (Exception ex) { PromptFlowStudio.Program.Log("LiveToReels.Upload", ex); }
            if (ActiveLegs == 0) { _wake.Wait(200); _wake.Reset(); }
            else Thread.Sleep(TickMs);
        }
    }

    // ══════════════════════ 워커 틱(무할당) ══════════════════════
    public void Step(double dtSec)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _nowMs += dtSec * 1000; Ticks++;
        Link.Advance(dtSec);
        var budget = BudgetKbps;
        var delay = QueueDelayMs;
        // LEDBAT 형 AIMD — 라이브 큐 지연이 목표 초과면 즉시 승산 감소
        if (delay > TargetDelayMs) { if (_nowMs - _lastCutMs >= 100) { _rateKbps *= 0.7; _lastCutMs = _nowMs; Throttles++; } }
        else _rateKbps += AdditiveKbpsPerSec * dtSec * (1 - delay / TargetDelayMs);
        _rateKbps = Math.Clamp(_rateKbps, MinKbps, budget);
        _tokens = Math.Min(_tokens + _rateKbps * 125 * dtSec, _rateKbps * 125 * 0.05 + SliceBytes);

        var frame = FrameProbe?.Invoke() ?? 0;
        if (frame > YieldMs) Yields++;
        else Pump();

        _histAcc += dtSec;
        if (_histAcc >= 0.25) { _histAcc = 0; _rateS[_sHead] = (float)_rateKbps; _delayS[_sHead] = (float)delay; _sHead = (_sHead + 1) % Hist; if (_sN < Hist) _sN++; }
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        LastStepUs = us; if (us > MaxStepUs && Ticks > 3) MaxStepUs = us;
    }

    /// <summary>토큰 한도 내 라운드로빈 슬라이스 전송.</summary>
    private void Pump()
    {
        var guardN = MaxJobs * Platforms * 4;
        while (_tokens >= 1024 && guardN-- > 0)
        {
            ReelsUploadJob? job = null; var leg = -1;
            lock (_gate)
            {
                for (var k = 0; k < MaxJobs * Platforms; k++)
                {
                    var idx = (_rr + k) % (MaxJobs * Platforms);
                    var j = _jobs[idx / Platforms];
                    if (j is null) continue;
                    ref var l = ref j.Legs[idx % Platforms];
                    if (l.Phase == ReelsUploadPhase.Backoff && _nowMs >= l.NextAtMs) l.Phase = ReelsUploadPhase.Uploading;
                    if (l.Phase is ReelsUploadPhase.Queued or ReelsUploadPhase.Uploading) { job = j; leg = idx % Platforms; _rr = idx + 1; break; }
                }
            }
            if (job is null) return;
            SendSlice(job, leg);
        }
    }

    private void SendSlice(ReelsUploadJob job, int leg)
    {
        ref var l = ref job.Legs[leg];
        if (l.Phase == ReelsUploadPhase.Queued) { l.Phase = ReelsUploadPhase.Uploading; l.StartedMs = _nowMs; }
        var k = (int)Math.Min(Math.Min(SliceBytes, (long)_tokens), l.Total - l.Sent);
        // 플랫폼 청크 경계는 넘지 않음(재개 단위 정합)
        var chunkLeft = l.ChunkSize - l.Sent % l.ChunkSize;
        k = (int)Math.Min(k, chunkLeft);
        if (k <= 0) { Finish(job, ref l, ReelsUploadPhase.Done); return; }
        var span = _scratch.AsSpan(0, k);
        var got = job.Media.ReadAt(l.Sent, span);
        if (got != k) { Finish(job, ref l, ReelsUploadPhase.Failed); return; }
        bool ok;
        try { ok = Transport.Send(l.Platform, job.ReelId, l.Sent, l.Total, span); }
        catch (Exception ex) { ok = false; PromptFlowStudio.Program.Log("LiveToReels.Send", ex); }
        _tokens -= k;
        if (!ok)
        {
            Retries++;
            if (++l.Retries > MaxRetries) { Finish(job, ref l, ReelsUploadPhase.Failed); return; }
            l.Phase = ReelsUploadPhase.Backoff; l.NextAtMs = _nowMs + 100 * (1 << l.Retries);
            return;
        }
        l.Hash = Fnv(l.Hash, span); l.Retries = 0;   // 연속 실패 횟수만 백오프 대상
        l.Sent += k; SentBytes += k; _sentBy[leg] += k;
        if (l.Sent >= l.Total) Finish(job, ref l, ReelsUploadPhase.Done);
    }

    private void Finish(ReelsUploadJob job, ref ReelsUploadLeg l, ReelsUploadPhase phase)
    {
        l.Phase = phase; l.FinishedMs = _nowMs;
        if (phase == ReelsUploadPhase.Failed) FailedLegs++;
        if (!job.Finished) return;
        Completed++;
        try { OnJobFinished?.Invoke(job); } catch (Exception ex) { PromptFlowStudio.Program.Log("LiveToReels.JobFinished", ex); }
    }

    public void Dispose()
    {
        Stop();
        OnJobFinished = null;
        _wake.Dispose();
    }
}
