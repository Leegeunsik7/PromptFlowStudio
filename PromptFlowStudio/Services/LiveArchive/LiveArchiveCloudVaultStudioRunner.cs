using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using PromptFlowStudio.Services.LiveRestream;

namespace PromptFlowStudio.Services.LiveArchive;

/// <summary>
/// [PromptFlow Studio v17.87.0] 💾 라이브 클라우드 아카이브 볼트 러너.
///   ▸ 입력: v17.83 <see cref="OmniLiveRestreamStudioRunner.MediaTap"/> 체인 결선(기존 탭 — v17.84 클리퍼 등 — 은 보존 후 전달) →
///     최종 송출 AU/AAC 를 <see cref="ZeroAllocLiveSegmentRecordingEngine"/> 로 5/15 분 fMP4 세그먼트화. 미결선 시 합성 인코더 리허설.
///   ▸ 확정 세그먼트 → 업로드 큐(고정 링) → 단일 in-flight 워커 → <see cref="MultiCloudVaultArchivalGateway"/> 8 MB 멀티파트(20 Mbps 셰이핑).
///   ▸ 5 s 주기 <see cref="StorageCostLifecycleTieringGuard"/> 스캔 → 업로드 완료본 로컬 자동 회수 · 경과일 티어 태그 승격.
/// </summary>
public sealed class LiveArchiveCloudVaultStudioRunner : IDisposable, ILiveMediaTap
{
    public const int Fps = 30, Gop = 60, AudioRate = 48_000, AacSamples = 1024, VideoKbps = 800, AudioKbps = 128, LoopHz = 30, ScanSec = 5, QueueCap = 256;
    public const long RehearsalFreeBytes = StorageCostLifecycleTieringGuard.DefaultMinFree + 128L * 1024 * 1024;
    public static readonly int[] SegmentMinuteOptions = { 5, 15 };

    private readonly object _gate = new();
    private readonly byte[] _au = new byte[256 * 1024], _aac = new byte[2048];
    private readonly int[] _queue = new int[QueueCap];
    private readonly OmniLiveRestreamStudioRunner? _live;
    private readonly CancellationTokenSource _life = new();
    private ILiveMediaTap? _prevTap;
    private CancellationTokenSource? _cts;
    private Task? _loop, _pump, _tagPump;
    private int _qHead, _qTail, _pumping, _tagging, _gopPos;
    private long _nowUs, _vAcc, _aAcc, _scanAccUs, _linkT0;
    private uint _seed = 0xA2C417EDu;
    private bool _linked;

    public LiveArchiveCloudVaultStudioRunner(IServiceProvider sp) : this(sp.GetService(typeof(OmniLiveRestreamStudioRunner)) as OmniLiveRestreamStudioRunner) { }

    public LiveArchiveCloudVaultStudioRunner(OmniLiveRestreamStudioRunner? live = null, ISegmentStore? store = null, HttpMessageHandler? transport = null)
    {
        _live = live;
        Store = store ?? new MemorySegmentStore(RehearsalFreeBytes);
        Recorder = new ZeroAllocLiveSegmentRecordingEngine(Store) { OnSealed = EnqueueUpload };
        Gateway = new MultiCloudVaultArchivalGateway(transport);
        // 리허설 시뮬레이터 버킷 — 세션마다 무작위 자격(하드코딩 시크릿 없음)
        Gateway.Configure(VaultEndpoint.Preset(VaultProvider.AwsS3, "", "pfs-live-archive", "ap-northeast-2", "PFSSIM" + Convert.ToHexString(RandomNumberGenerator.GetBytes(7)), Convert.ToHexString(RandomNumberGenerator.GetBytes(20))), out _);
    }

    public event Action? OnChange;
    public ISegmentStore Store { get; }
    public ZeroAllocLiveSegmentRecordingEngine Recorder { get; }
    public MultiCloudVaultArchivalGateway Gateway { get; }
    public StorageCostLifecycleTieringGuard Guard { get; } = new();
    public bool HasLive => _live is not null;
    public bool Running => _loop is { IsCompleted: false };
    public bool Uploading => Volatile.Read(ref _pumping) != 0;
    public bool AutoUpload { get; set; } = true;
    public bool AutoPrune { get; set; } = true;
    /// <summary>수명주기 시연용 가상 경과일(실 시계 + N 일).</summary>
    public int DayOffset { get; private set; }
    public int SegmentMinutes { get; private set; } = 5;
    public long NowUs => Volatile.Read(ref _nowUs);
    public long Ticks { get; private set; }
    public long Uploaded { get; private set; }
    public long UploadFailures { get; private set; }
    public long QueueDrops { get; private set; }
    public int QueueCount => Volatile.Read(ref _qTail) - Volatile.Read(ref _qHead);
    public int CurrentUpload { get; private set; } = -1;
    public double LastTickUs { get; private set; }
    public string? LastAction { get; private set; }

    /// <summary>v17.83 송출 탭 결선(기존 탭 체인 보존).</summary>
    public bool Linked
    {
        get => _linked;
        set
        {
            if (_live is null || value == _linked) return;
            lock (_gate)
            {
                if (value)
                {
                    _prevTap = ReferenceEquals(_live.MediaTap, this) ? null : _live.MediaTap;
                    _live.MediaTap = this;
                    _linkT0 = Stopwatch.GetTimestamp();
                }
                else
                {
                    if (ReferenceEquals(_live.MediaTap, this)) _live.MediaTap = _prevTap;
                    _prevTap = null;
                }
                _linked = value;
            }
            LastAction = value ? "🔗 v17.83 송출 인코더 결선 — 최종 방송 스트림 녹화" : "🔌 결선 해제 — 합성 인코더 리허설";
            Raise();
        }
    }

    public long FreeBytes => Store.FreeBytes;

    // ══════════════════════ 제어(비핫패스) ══════════════════════

    public void SetSegmentMinutes(int minutes)
    {
        SegmentMinutes = minutes >= 15 ? 15 : 5;
        Recorder.SegmentUs = SegmentMinutes * 60_000_000L;
        LastAction = "⏱ 세그먼트 길이 " + SegmentMinutes.ToString(CultureInfo.InvariantCulture) + " 분(다음 IDR 경계부터)";
        Raise();
    }

    public bool ApplyEndpoint(VaultEndpoint ep, bool simulate)
    {
        var ok = ep.Validate(out var err);
        if (ok) { Gateway.UseSimulator = simulate; ok = Gateway.Configure(ep, out err); }
        LastAction = ok ? "☁️ 볼트 설정 — " + ep + (simulate ? " · 시뮬레이터" : " · 실 전송") : "⚠️ " + err;
        Raise();
        return ok;
    }

    public async Task<bool> TestConnectionAsync()
    {
        LastAction = "🔌 연결 테스트 중…"; Raise();
        var ok = await Gateway.TestConnectionAsync(_life.Token).ConfigureAwait(false);
        LastAction = ok ? "✅ 연결 성공 — " + Gateway.Endpoint : "❌ 연결 실패 — " + Gateway.LastError;
        Raise();
        return ok;
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
                    var dt = last == 0 ? 1_000_000L / LoopHz : Math.Clamp(now - last, 1_000L, 200_000L);
                    last = now;
                    Tick(dt);
                    if (now - lastRaise >= 250_000) { lastRaise = now; Raise(); }
                    await Task.Delay(1000 / LoopHz, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { PromptFlowStudio.Program.Log("LiveArchive.Loop", ex); }
        }, ct);
        LastAction = "⏺ 녹화 시작 — " + SegmentMinutes.ToString(CultureInfo.InvariantCulture) + " 분 fMP4 세그먼트";
        Raise();
    }

    public void Stop()
    {
        var cts = _cts; var loop = _loop;
        if (cts is null) return;
        cts.Cancel();
        try { loop?.Wait(1000); } catch (AggregateException) { }
        cts.Dispose(); _cts = null; _loop = null;
        LastAction = "⏸ 녹화 루프 정지(진행 세그먼트 유지)";
        Raise();
    }

    /// <summary>녹화 종료 — 잔여 조각 플러시 후 세그먼트 확정 → 업로드 큐.</summary>
    public void Finish()
    {
        if (Running) Stop();
        Recorder.Stop();
        LastAction = "⏹ 녹화 종료 · 세그먼트 " + Recorder.SegmentCount.ToString(CultureInfo.InvariantCulture) + " 개 확정";
        Raise();
    }

    /// <summary>리허설 가상 시간 전진(합성 인코더).</summary>
    public void Advance(double seconds)
    {
        var steps = (int)Math.Clamp(seconds * LoopHz, 1, 3600 * LoopHz);
        for (var i = 0; i < steps; i++) Tick(1_000_000L / LoopHz);
        LastAction = "⏩ " + seconds.ToString("0", CultureInfo.InvariantCulture) + " s 전진 · 현재 세그먼트 " + (Recorder.CurrentSegmentUs / 1e6).ToString("0", CultureInfo.InvariantCulture) + " s";
        Raise();
    }

    public void CutNow()
    {
        Recorder.RequestCut();
        LastAction = "✂️ 다음 키프레임에서 세그먼트 분할";
        Raise();
    }

    public void RetryFailed()
    {
        var n = 0;
        for (var i = Recorder.FirstIndex; i < Recorder.SegmentCount; i++)
        {
            ref var s = ref Recorder.Segment(i);
            if (s.State != ArchiveSegmentState.Failed || s.Name is null) continue;
            s.State = ArchiveSegmentState.Sealed; EnqueueUpload(i); n++;
        }
        LastAction = "🔁 실패 세그먼트 " + n.ToString(CultureInfo.InvariantCulture) + " 개 재업로드";
        Raise();
    }

    public int RecoverOrphans()
    {
        var before = Recorder.SegmentCount;
        var n = Recorder.RecoverOrphans();
        for (var i = before; i < Recorder.SegmentCount; i++) EnqueueUpload(i);
        LastAction = "🩹 충돌 복구 — 미완 세그먼트 " + n.ToString(CultureInfo.InvariantCulture) + " 개 절단·확정";
        Raise();
        return n;
    }

    /// <summary>가상 경과일 설정 → 수명주기 스캔 · 티어 태그 승격(완료까지 대기).</summary>
    public async Task<int> ApplyTieringAsync(int dayOffset)
    {
        DayOffset = Math.Clamp(dayOffset, 0, 3650);
        var prev = _tagPump;
        if (prev is not null) await prev.ConfigureAwait(false);
        lock (_gate) RunGuard(false);
        var n = await RetagAsync().ConfigureAwait(false);
        LastAction = "🧊 수명주기 +" + DayOffset.ToString(CultureInfo.InvariantCulture) + " 일 — 티어 전환 " + n.ToString(CultureInfo.InvariantCulture) + " 건";
        Raise();
        return n;
    }

    public async Task<bool> ApplyBucketLifecycleAsync()
    {
        var ep = Gateway.Endpoint;
        if (ep is null) return false;
        var ok = await Gateway.ApplyLifecycleAsync(Guard.LifecycleXml(ep.Provider, ep.Prefix), _life.Token).ConfigureAwait(false);
        LastAction = ok ? "📜 버킷 수명주기 규칙 적용(IA " + Guard.IaDays.ToString(CultureInfo.InvariantCulture) + " · Glacier " + Guard.GlacierDays.ToString(CultureInfo.InvariantCulture) + " · Deep " + Guard.DeepDays.ToString(CultureInfo.InvariantCulture) + " 일)" : "⚠️ 수명주기 규칙 실패 — " + Gateway.LastError;
        Raise();
        return ok;
    }

    /// <summary>업로드 큐 소진 대기(테스트 · 종료 직전).</summary>
    public async Task DrainUploadsAsync(int timeoutMs = 30_000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (QueueCount == 0 && !Uploading) return;
            if (!Uploading && AutoUpload) PumpUploads();
            var p = _pump;
            if (p is not null) await Task.WhenAny(p, Task.Delay(50)).ConfigureAwait(false);
            else await Task.Delay(10).ConfigureAwait(false);
        }
    }

    public void Reset()
    {
        if (Running) Stop();
        lock (_gate)
        {
            Recorder.Reset();
            if (Store is MemorySegmentStore m) m.Clear();
            Gateway.Simulator.Clear(); Gateway.ResetStats(); Guard.ResetStats();
            _qHead = _qTail = 0; _gopPos = 0; _nowUs = _vAcc = _aAcc = _scanAccUs = 0;
            Ticks = Uploaded = UploadFailures = QueueDrops = 0; DayOffset = 0; CurrentUpload = -1; LastTickUs = 0;
        }
        LastAction = "♻ 초기화";
        Raise();
    }

    // ══════════════════════ 틱(무할당 핫패스) ══════════════════════

    public void Tick(long dtUs)
    {
        var t0 = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            Volatile.Write(ref _nowUs, _nowUs + dtUs);
            Ticks++;
            if (!_linked)
            {
                _vAcc += dtUs * Fps;
                while (_vAcc >= 1_000_000) { _vAcc -= 1_000_000; EmitVideo(); }
                _aAcc += dtUs * AudioRate;
                while (_aAcc >= AacSamples * 1_000_000L) { _aAcc -= AacSamples * 1_000_000L; EmitAudio(); }
            }
            _scanAccUs += dtUs;
            if (_scanAccUs >= ScanSec * 1_000_000L) { _scanAccUs = 0; RunGuard(); }
        }
        if (QueueCount > 0 && AutoUpload && !Uploading) PumpUploads();
        LastTickUs = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
    }

    private void EmitVideo()
    {
        var idr = _gopPos == 0;
        var avg = VideoKbps * 1000 / 8 / Fps;
        var size = idr ? avg * 3 : Math.Max(64, (avg * Gop - avg * 3) / (Gop - 1));
        var a = _au.AsSpan();
        var p = Nal(a, 0, 0x09); a[p++] = 0xF0;
        if (idr)
        {
            p = Nal(a, p, 0x67);
            a[p++] = 0x64; a[p++] = 0x00; a[p++] = 0x28; a[p++] = 0xAC; a[p++] = 0xD9; a[p++] = 0x40; a[p++] = 0x78; a[p++] = 0x02; a[p++] = 0x27; a[p++] = 0xE5;
            p = Nal(a, p, 0x68);
            a[p++] = 0xEE; a[p++] = 0x3C; a[p++] = 0x80;
        }
        p = Nal(a, p, idr ? (byte)0x65 : (byte)0x41);
        var end = Math.Min(a.Length, p + size);
        for (; p < end; p++) { var b = RtmpWire.Next(ref _seed); a[p] = b == 0 ? (byte)0x55 : b; }
        Recorder.OnVideo(a.Slice(0, p), idr, _nowUs);
        _gopPos = (_gopPos + 1) % Gop;
    }

    private static int Nal(Span<byte> a, int p, byte header)
    {
        a[p] = 0; a[p + 1] = 0; a[p + 2] = 0; a[p + 3] = 1; a[p + 4] = header;
        return p + 5;
    }

    private void EmitAudio()
    {
        var raw = AudioKbps * 1000 / 8 * AacSamples / AudioRate;
        var a = _aac.AsSpan();
        a[0] = 0xFF; a[1] = 0xF1; a[2] = 0x4C; a[3] = 0x80; a[4] = (byte)((raw + 7) >> 3); a[5] = 0x1F; a[6] = 0xFC;
        for (var i = 7; i < 7 + raw; i++) { var b = RtmpWire.Next(ref _seed); a[i] = b == 0 ? (byte)0x21 : b; }
        Recorder.OnAudio(a.Slice(0, 7 + raw), _nowUs);
    }

    // ══════════════════════ v17.83 미디어 탭(송출 스레드 · 무할당 · 체인 전달) ══════════════════════

    public void OnLiveVideo(ReadOnlySpan<byte> annexB, bool idr)
    {
        Recorder.OnVideo(annexB, idr, LinkUs());
        if (_prevTap is { } next) try { next.OnLiveVideo(annexB, idr); } catch (Exception ex) { PromptFlowStudio.Program.Log("LiveArchive.TapChain", ex); }
    }

    public void OnLiveAudio(ReadOnlySpan<byte> aac)
    {
        Recorder.OnAudio(aac, LinkUs());
        if (_prevTap is { } next) try { next.OnLiveAudio(aac); } catch (Exception ex) { PromptFlowStudio.Program.Log("LiveArchive.TapChain", ex); }
    }

    private long LinkUs() => (Stopwatch.GetTimestamp() - _linkT0) * 1_000_000L / Stopwatch.Frequency;

    // ══════════════════════ 업로드 큐 · 워커 ══════════════════════

    /// <summary>확정 콜백(레코더 락 내) — 고정 링 적재만.</summary>
    private void EnqueueUpload(int index)
    {
        lock (_queue)
        {
            if (_qTail - _qHead >= QueueCap) { QueueDrops++; return; }   // 드롭분은 Sealed 로 남아 재시도 가능
            _queue[_qTail % QueueCap] = index;
            Volatile.Write(ref _qTail, _qTail + 1);
        }
    }

    private bool TryDequeue(out int index)
    {
        lock (_queue)
        {
            if (_qHead == _qTail) { index = -1; return false; }
            index = _queue[_qHead % QueueCap];
            Volatile.Write(ref _qHead, _qHead + 1);
            return true;
        }
    }

    private void PumpUploads()
    {
        if (Interlocked.Exchange(ref _pumping, 1) != 0) return;
        _pump = Task.Run(PumpLoopAsync);
    }

    private async Task PumpLoopAsync()
    {
        try
        {
            while (!_life.IsCancellationRequested && AutoUpload && TryDequeue(out var idx)) await UploadOneAsync(idx).ConfigureAwait(false);
        }
        catch (Exception ex) { PromptFlowStudio.Program.Log("LiveArchive.Pump", ex); }
        finally { CurrentUpload = -1; Volatile.Write(ref _pumping, 0); }
        Raise();
    }

    private async Task UploadOneAsync(int idx)
    {
        var name = Recorder.Segment(idx).Name;
        if (name is null || Recorder.Segment(idx).State is not (ArchiveSegmentState.Sealed or ArchiveSegmentState.Recovered)) return;
        var ep = Gateway.Endpoint;
        var key = (ep?.Prefix ?? "live/") + DateTime.UtcNow.ToString("yyyy'/'MM'/'dd'/'", CultureInfo.InvariantCulture) + Path.GetFileName(name);
        Recorder.Segment(idx).State = ArchiveSegmentState.Uploading;
        CurrentUpload = idx;
        Raise();
        VaultUploadResult r;
        try
        {
            using var src = Store.OpenRead(name);
            r = await Gateway.UploadAsync(src, key, StorageTier.Hot, _life.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            r = new VaultUploadResult { Error = ex.Message };
        }
        if (r.Ok)
        {
            Recorder.Segment(idx).State = ArchiveSegmentState.Uploaded;
            Recorder.Segment(idx).ObjectKey = key;
            Recorder.Segment(idx).UploadedBytes = r.Bytes;
            Uploaded++;
            LastAction = "☁️ 업로드 완료 #" + idx.ToString(CultureInfo.InvariantCulture) + " — " + r.Parts.ToString(CultureInfo.InvariantCulture) + " 파트 · " + (r.Bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB · " + Gateway.LastMbps.ToString("0.0", CultureInfo.InvariantCulture) + " Mbps";
        }
        else
        {
            Recorder.Segment(idx).State = ArchiveSegmentState.Failed;   // [부분 구제] 로컬 보존 · 재시도 가능
            UploadFailures++;
            LastAction = "❌ 업로드 실패 #" + idx.ToString(CultureInfo.InvariantCulture) + " — " + r.Error;
        }
        Raise();
    }

    // ══════════════════════ 수명주기 가드 ══════════════════════

    private long NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() + DayOffset * 86_400L;

    private void RunGuard(bool kickRetag = true)
    {
        long free;
        try { free = Store.FreeBytes; } catch (IOException) { free = long.MaxValue; }
        Guard.Scan(Recorder, free, NowUnix());
        if (AutoPrune)
            for (var i = 0; i < Guard.PruneCount; i++)
            {
                ref var s = ref Recorder.Segment(Guard.PruneCandidate(i));
                if (s.Name is not null && Store.Delete(s.Name)) Guard.CommitPrune(ref s);
            }
        if (kickRetag && Guard.RetagCount > 0 && Volatile.Read(ref _tagging) == 0) _tagPump = RetagAsync();
    }

    private async Task<int> RetagAsync()
    {
        if (Interlocked.Exchange(ref _tagging, 1) != 0) { var t = _tagPump; if (t is not null) await t.ConfigureAwait(false); return 0; }
        try
        {
            var n = Guard.RetagCount;
            var idx = new int[n]; var tiers = new StorageTier[n];
            for (var i = 0; i < n; i++) { idx[i] = Guard.RetagCandidate(i); tiers[i] = Guard.RetagTier(i); }
            await Task.Yield();
            var done = 0;
            for (var i = 0; i < n; i++)
            {
                var key = Recorder.Segment(idx[i]).ObjectKey;
                if (key is null) continue;
                if (await Gateway.PutTaggingAsync(key, tiers[i], _life.Token).ConfigureAwait(false)) { Guard.CommitTransition(ref Recorder.Segment(idx[i]), tiers[i]); done++; }
            }
            return done;
        }
        catch (Exception ex) { PromptFlowStudio.Program.Log("LiveArchive.Retag", ex); return 0; }
        finally { Volatile.Write(ref _tagging, 0); }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("LiveArchive.OnChange", ex); }
    }

    public string Summary() =>
        "세그먼트 " + Recorder.SegmentCount.ToString(CultureInfo.InvariantCulture) + " · 업로드 " + Uploaded.ToString(CultureInfo.InvariantCulture) + " · 큐 " + QueueCount.ToString(CultureInfo.InvariantCulture) +
        " · 회수 " + Guard.Pruned.ToString(CultureInfo.InvariantCulture) + " · 티어 전환 " + Guard.Transitions.ToString(CultureInfo.InvariantCulture) +
        " · 로컬 여유 " + (FreeBytes / (double)StorageCostLifecycleTieringGuard.GiB).ToString("0.00", CultureInfo.InvariantCulture) + " GB";

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        try { _loop?.Wait(1000); } catch (AggregateException) { }
        _cts?.Dispose(); _cts = null; _loop = null;
        if (_linked) Linked = false;
        _life.Cancel();
        try { _pump?.Wait(1000); } catch (AggregateException) { }
        try { _tagPump?.Wait(1000); } catch (AggregateException) { }
        Recorder.Dispose();
        Gateway.Dispose();
        _life.Dispose();
        OnChange = null;
    }
}
