using System.Globalization;
using System.Security.Cryptography;
using PromptFlowStudio.Services.Autopilot;
using PromptFlowStudio.Services.BeatSync;
using PromptFlowStudio.Services.Publisher;
using PromptFlowStudio.Services.StationMaster;
using PromptFlowStudio.Services.VerticalTranscode;
using PromptFlowStudio.Services.VisualPolish;

namespace PromptFlowStudio.Services.RetentionOptimizer;

/// <summary>
/// [PromptFlow Studio v17.75.0] 📈 릴스 유지율 &amp; A/B 최적화 스튜디오 러너.
///   ▸ 훅 트리밍: <see cref="ViralHookDeadAirAutoTrimmerEngine"/> 스캔/계획/오디오 스플라이스 → EDL 0 프레임 컷 재부호화(백그라운드) · 전후 파형(100 ms dBFS) 보관.
///   ▸ A/B: v17.74 <see cref="AbTestingMetadataVariantGenerator"/> 쌍 + 썸네일 → 2×2 셀 배포 추적 → <see cref="LiveMetricTrackerAndAbPromoterSynthesizer"/>(5 분 폴링 · 95 % 승격).
///     v17.71 퍼블리셔가 실 계정 모드면 그 게이트웨이(HTTP) 사용 · 아니면 자체 분석 시뮬레이터 + 메모리 볼트(가상 시계 · 데모 배속 1 s = 5 분).
///   ▸ 진단: 배포 1 h 후 유지 곡선(YT Analytics · IG = 평균 시청 기반 추정) → <see cref="UnderperformingClipSelfDiagnosisGuard"/> → v17.70 자가진화 · v17.53 더킹 · 방송국 컷 주기 반영.
/// </summary>
public sealed class ViralRetentionOptimizerStudioRunner : IDisposable
{
    public const int SampleRate = 48000, UiChunks = 40, CurveCap = 121, DemoStepSec = 300;
    public const long DiagnoseAfterSec = 3600;

    private readonly MasterStationNetworkStudioRunner? _station;
    private readonly BeatSyncAudioMixStudioRunner? _beat;
    private readonly ReelsVisualPolishStudioRunner? _polish;
    private readonly OAuthTokenVault? _vault;
    private readonly SocialMediaApiGatewayEngine? _ownGateway;
    private readonly byte[] _vaultKey = RandomNumberGenerator.GetBytes(32);
    private readonly float[] _ratio = new float[LiveMetricTrackerAndAbPromoterSynthesizer.RetentionPoints];
    private readonly ClipSecondFeature[] _features = new ClipSecondFeature[CurveCap];
    private readonly int[] _diagnosed = new int[64];
    private readonly long _simBase;
    private long _simOffset;
    private int _diagN, _nextReel = 9001;
    private uint _rng = 0x7E7E_0001u;
    private short[] _pcm = Array.Empty<short>(), _spliced = Array.Empty<short>();
    private byte[]? _thumbA, _thumbB;
    private CancellationTokenSource? _cts;
    private Task? _loop, _trimTask;

    public ViralRetentionOptimizerStudioRunner(IServiceProvider sp) : this(
        sp.GetService(typeof(ReelsVisualPolishStudioRunner)) as ReelsVisualPolishStudioRunner,
        sp.GetService(typeof(MasterStationNetworkStudioRunner)) as MasterStationNetworkStudioRunner,
        sp.GetService(typeof(BeatSyncAudioMixStudioRunner)) as BeatSyncAudioMixStudioRunner,
        sp.GetService(typeof(OmniChannelPublisherStudioRunner)) as OmniChannelPublisherStudioRunner) { }

    public ViralRetentionOptimizerStudioRunner(ReelsVisualPolishStudioRunner? polish = null, MasterStationNetworkStudioRunner? station = null, BeatSyncAudioMixStudioRunner? beat = null, OmniChannelPublisherStudioRunner? pub = null)
    {
        _polish = polish; _station = station; _beat = beat;
        _simBase = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Simulator = new SimulatedRetentionAnalyticsServer(() => SimNow);
        if (pub is { Simulated: false })
        {
            Gateway = pub.Gateway; Simulated = false;
        }
        else
        {
            // 메모리 전용 볼트(Save 호출 없음 · 디스크 기록 0) — 시뮬레이터 발급 토큰
            _vault = new OAuthTokenVault(Path.Combine(Path.GetTempPath(), "PromptFlowStudio", "retention_sim_vault.bin"), () => (byte[])_vaultKey.Clone());
            _ownGateway = new SocialMediaApiGatewayEngine(Simulator, _vault) { UnixNow = () => SimNow };
            _vault.Store(SocialPlatform.YouTube, "ya29.retention-sim", "", long.MaxValue / 4, SimNow, "UCsimChannel");
            _vault.Store(SocialPlatform.Instagram, "IGQV.retention-sim", "", long.MaxValue / 4, SimNow, "17841400000000000");
            Simulator.Issue(SocialPlatform.YouTube, "ya29.retention-sim"u8);
            Simulator.Issue(SocialPlatform.Instagram, "IGQV.retention-sim"u8);
            Gateway = _ownGateway; Simulated = true;
        }
        Ab = polish?.Ab ?? new AbTestingMetadataVariantGenerator();
        Promoter = new LiveMetricTrackerAndAbPromoterSynthesizer(Gateway, Ab) { UnixNow = Simulated ? () => SimNow : static () => DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
        Promoter.OnPhase += OnPromoterPhase;
    }

    public event Action? OnChange;
    public ViralHookDeadAirAutoTrimmerEngine Trimmer { get; } = new();
    public LiveMetricTrackerAndAbPromoterSynthesizer Promoter { get; }
    public UnderperformingClipSelfDiagnosisGuard Guard { get; } = new();
    public SimulatedRetentionAnalyticsServer Simulator { get; }
    public SocialMediaApiGatewayEngine Gateway { get; }
    public AbTestingMetadataVariantGenerator Ab { get; }
    public bool Simulated { get; }
    public bool StationLinked => _station is not null;
    public bool DuckerLinked => _beat is not null;
    public long SimNow => _simBase + Interlocked.Read(ref _simOffset);
    public bool Running => _loop is { IsCompleted: false };
    public bool Trimming => _trimTask is { IsCompleted: false };
    public string? LastAction { get; private set; }

    // ── 훅 파형(UI) ──
    public float[] BeforeDb { get; } = new float[UiChunks];
    public HookChunkKind[] BeforeKind { get; } = new HookChunkKind[UiChunks];
    public float[] AfterDb { get; } = new float[UiChunks];
    public int BeforeN { get; private set; }
    public int AfterN { get; private set; }
    public int TrimFrames { get; private set; }
    public long TrimmedMp4Bytes { get; private set; }
    public double TrimRenderMs { get; private set; }
    public long Trims { get; private set; }

    // ── 유지 곡선(UI · 초 단위) ──
    public float[] YtCurve { get; } = new float[CurveCap];
    public float[] IgCurve { get; } = new float[CurveCap];
    public int YtCurveN { get; private set; }
    public int IgCurveN { get; private set; }
    public int CurveReel { get; private set; }

    public void Advance(long seconds) { Interlocked.Add(ref _simOffset, seconds); }

    // ══════════════════════ 훅 트리밍 ══════════════════════
    /// <summary>샘플 훅(선행 묵음 0.8 s · 발화 · 중간 묵음 · 1.8 s 자극 버스트) → 스캔 · 계획 · 스플라이스 · 0 프레임 컷 재부호화(백그라운드).</summary>
    public bool TrimSample()
    {
        if (Trimming) return false;
        _trimTask = Task.Run(() =>
        {
            try { RunSampleTrim(); }
            catch (Exception ex) { LastAction = "⚠ 훅 트리밍 실패: " + ex.Message; PromptFlowStudio.Program.Log("Retention.Trim", ex); Raise(); }
        });
        LastAction = "✂️ 샘플 훅 트리밍 시작"; Raise();
        return true;
    }

    public Task WaitTrimAsync() => _trimTask ?? Task.CompletedTask;

    /// <summary>동기 실행(회귀 · 백그라운드 스레드).</summary>
    public HookTrimResult RunSampleTrim(double seconds = 3.5, int width = 216, int height = 384)
    {
        var frames = (int)(seconds * SampleRate);
        if (_pcm.Length != frames) { _pcm = new short[frames]; _spliced = new short[frames]; }
        SynthesizeHookPcm(_pcm, SampleRate);
        var fps = 30; var vf = (int)(seconds * fps);
        var w52 = new float[vf * 52]; var gesture = new float[vf];
        SynthesizeFaceMotion(w52, gesture, fps);
        HookTrimResult r;
        lock (Trimmer)
        {
            Trimmer.ScanAudio(_pcm, SampleRate);
            Trimmer.ScanFaceMotion(w52, 52, fps, gesture);
            r = Trimmer.Plan();
            BeforeN = Math.Min(UiChunks, Trimmer.Chunks);
            for (var c = 0; c < BeforeN; c++) { BeforeDb[c] = Trimmer.RmsDb(c); BeforeKind[c] = Trimmer.Kind(c); }
            var outFrames = Trimmer.SpliceAudio(_pcm, SampleRate, 1, _spliced);
            AfterN = Math.Min(UiChunks, (outFrames * 1000 / SampleRate + ViralHookDeadAirAutoTrimmerEngine.ChunkMs - 1) / ViralHookDeadAirAutoTrimmerEngine.ChunkMs);
            ChunkDb(_spliced.AsSpan(0, outFrames), SampleRate, AfterDb.AsSpan(0, AfterN));
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var enc = new HardwareSubtitleBurnInEngine(width, height) { Enabled = false };
            using var clip = new SegmentedChunkStream();
            enc.BurnReel(new GradientFrameSource(), seconds, fps, clip, 3000, allowMf: false);
            clip.Position = 0;
            using var outS = new SegmentedChunkStream();
            TrimFrames = Trimmer.RenderVideo(clip, outS);
            TrimmedMp4Bytes = outS.Length;
            TrimRenderMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }
        Trims++;
        LastAction = "✂️ 훅 트리밍 · 절삭 " + r.TrimmedMs.ToString(CultureInfo.InvariantCulture) + " ms · 첫 자극 " + (r.StimulusOutMs >= 0 ? r.StimulusOutMs.ToString(CultureInfo.InvariantCulture) + " ms" : "—")
            + (r.ColdOpen ? " (콜드 오픈)" : "") + " · 구간 " + r.Segments.ToString(CultureInfo.InvariantCulture) + " · " + TrimFrames.ToString(CultureInfo.InvariantCulture) + " f";
        Raise();
        return r;
    }

    /// <summary>청크 RMS dBFS(무할당).</summary>
    public static void ChunkDb(ReadOnlySpan<short> pcm, int sr, Span<float> dst)
    {
        var per = sr * ViralHookDeadAirAutoTrimmerEngine.ChunkMs / 1000;
        for (var c = 0; c < dst.Length; c++)
        {
            var a = c * per; var b = Math.Min(pcm.Length, a + per); double s = 0;
            for (var i = a; i < b; i++) s += (double)pcm[i] * pcm[i];
            dst[c] = ViralHookDeadAirAutoTrimmerEngine.ToDbfs(Math.Sqrt(s / Math.Max(1, b - a)) / 32768.0);
        }
    }

    /// <summary>샘플 훅 PCM: 0~0.8 s 실내 잡음(−54 dBFS) · 0.8~1.5 s 발화(−20 dBFS 음절 변조) · 1.5~1.8 s 묵음 · 1.8~2.3 s 자극(−4 dBFS 버스트) · 2.3 s~ 발화.</summary>
    public static void SynthesizeHookPcm(Span<short> pcm, int sr)
    {
        uint rng = 0x1234567u;
        for (var i = 0; i < pcm.Length; i++)
        {
            var t = (double)i / sr;
            rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
            var noise = ((rng & 0xFFFF) / 32768.0 - 1) * 60;   // ≈ −54 dBFS
            double v;
            if (t < 0.8 || (t >= 1.5 && t < 1.8)) v = noise;
            else if (t >= 1.8 && t < 2.3) v = 20000 * Math.Sin(2 * Math.PI * 180 * t) * (0.7 + 0.3 * Math.Sin(2 * Math.PI * 9 * t)) + noise;
            else v = 4200 * Math.Sin(2 * Math.PI * 220 * t) * (0.55 + 0.45 * Math.Abs(Math.Sin(2 * Math.PI * 4 * t))) + noise;
            pcm[i] = (short)Math.Clamp(v, -32768, 32767);
        }
    }

    /// <summary>샘플 안면/제스처 모션: 묵음 구간 정지 · 발화 = 입 모양 변화 · 자극 = 큰 제스처.</summary>
    public static void SynthesizeFaceMotion(Span<float> w52, Span<float> gesture, int fps)
    {
        var frames = gesture.Length;
        for (var f = 0; f < frames; f++)
        {
            var t = (double)f / fps;
            var speaking = !(t < 0.8 || (t >= 1.5 && t < 1.8));
            var burst = t >= 1.8 && t < 2.3;
            var jaw = speaking ? (float)(0.3 + 0.3 * Math.Sin(2 * Math.PI * 4 * t)) : 0.02f;
            for (var k = 0; k < 52; k++) w52[f * 52 + k] = k == 24 ? jaw : speaking ? (float)(0.1 + 0.05 * Math.Sin(t * (k + 3))) : 0.1f;
            gesture[f] = burst ? 0.6f : speaking ? 0.06f : 0f;
        }
    }

    // ══════════════════════ A/B 배포 · 추적 ══════════════════════
    /// <summary>최신 폴리시 릴(v17.74) 또는 샘플 쌍 → YT/IG 2×2 셀 배포(시뮬레이터 프로필 등록) → 추적 개시.</summary>
    public bool DeployDemo()
    {
        if (!Simulated) { LastAction = "⚠ 실 계정 모드 — 데모 배포는 시뮬레이터 전용(Track API 사용)"; Raise(); return false; }
        var polished = _polish?.Count > 0 ? _polish.At(0) : null;
        var reelId = _nextReel++;
        var pair = polished?.Ab is { } pab && Promoter.Find(polished.ReelId, SocialPlatform.YouTube) is null ? pab : null;
        if (pair is not null) reelId = polished!.ReelId;
        pair ??= Ab.Generate(reelId, new ShortformContext { Caption = "직격 리액션", KeyLine = "이게 진짜 된다고?", Cause = 1, Peak = 92, Hits = 4, DurationSec = 24 }, ThumbnailMood.Surprise);
        EnsureThumbs();
        var now = SimNow;
        // 숨은 진실(결정적 난수): 제목 B / 썸네일 B 우세 여부
        var titleB = Next() < 0.7; var thumbB = Next() < 0.5;
        var vid = "ytMain" + reelId.ToString(CultureInfo.InvariantCulture);
        Simulator.Register(vid, 0.05, 9.5, 1800, now, 24, 0.32, 0.45, 0.30);
        var yt = new TrackedAbTest { ReelId = reelId, Platform = SocialPlatform.YouTube, VideoId = vid, Pair = pair, ThumbA = polished?.Png ?? _thumbA, ThumbB = _thumbB, DurationSec = 24, DeployedAtUnix = now };
        var ig = new TrackedAbTest { ReelId = reelId, Platform = SocialPlatform.Instagram, VideoId = "1795" + reelId.ToString(CultureInfo.InvariantCulture), Pair = pair, DurationSec = 24, DeployedAtUnix = now };
        for (var c = 0; c < TrackedAbTest.Cells; c++)
        {
            var ctr = 0.040 + (TrackedAbTest.TitleOf(c) == AbVariant.B == titleB ? 0.014 : 0) + (TrackedAbTest.ThumbOf(c) == AbVariant.B == thumbB ? 0.007 : 0);
            yt.CellIds[c] = vid + "c" + c.ToString(CultureInfo.InvariantCulture);
            ig.CellIds[c] = ig.VideoId + "0" + c.ToString(CultureInfo.InvariantCulture);
            Simulator.Register(yt.CellIds[c], ctr, 10 + c * 0.4, 900, now, 24);
            Simulator.Register(ig.CellIds[c], ctr * 2.4, 7 + c * 0.3, 1400, now, 24);
        }
        var ok = Promoter.Track(yt);
        Promoter.Track(ig);
        LastAction = ok ? "🚀 릴 #" + reelId.ToString(CultureInfo.InvariantCulture) + " A/B 2×2 배포(YT · IG) — 5 분 주기 폴링" : "⚠ 추적 슬롯 포화/중복";
        Raise();
        return ok;
    }

    private double Next() { _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5; return (_rng & 0xFFFFFF) / 16777216.0; }

    private void EnsureThumbs()
    {
        if (_thumbA is not null) return;
        _thumbA = TinyPng(255, 120, 40); _thumbB = TinyPng(40, 140, 255);
    }

    private static byte[] TinyPng(byte r, byte g, byte b)
    {
        const int W = 90, H = 160;
        var rgb = new byte[W * H * 3];
        for (var y = 0; y < H; y++) for (var x = 0; x < W; x++) { var o = (y * W + x) * 3; var k = 0.5f + 0.5f * y / H; rgb[o] = (byte)(r * k); rgb[o + 1] = (byte)(g * k); rgb[o + 2] = (byte)(b * k); }
        using var ms = new MemoryStream();
        PeakEmotion3dgsThumbnailSynthesizer.EncodePng(rgb, W, H, ms);
        return ms.ToArray();
    }

    /// <summary>가상 시계 전진 + 1 틱(폴링 · 판정 · 승격) + 진단 기한 도래 실험 진단.</summary>
    public async Task StepAsync(long seconds = DemoStepSec, CancellationToken ct = default)
    {
        if (Simulated) Advance(seconds);
        await Promoter.TickAsync(ct).ConfigureAwait(false);
        await DiagnoseDueAsync(ct).ConfigureAwait(false);
        Raise();
    }

    public void Start()
    {
        if (Running) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await StepAsync(Simulated ? DemoStepSec : 0, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { LastAction = "⚠ " + ex.Message; PromptFlowStudio.Program.Log("Retention.Loop", ex); }
                try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }, ct);
        LastAction = Simulated ? "▶ 자동 진행(1 s = 가상 5 분)" : "▶ 실시간 폴링"; Raise();
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loop?.Wait(5_000); } catch (AggregateException) { }
        _cts?.Dispose(); _cts = null; _loop = null;
        Raise();
    }

    // ══════════════════════ 진단 · 피드백 ══════════════════════
    private async Task DiagnoseDueAsync(CancellationToken ct)
    {
        var now = Promoter.UnixNow();
        for (var i = 0; i < Promoter.Count; i++)
        {
            var t = Promoter.At(i);
            if (t is null || t.Platform != SocialPlatform.YouTube || now - t.DeployedAtUnix < DiagnoseAfterSec || Diagnosed(t.ReelId)) continue;
            await DiagnoseAsync(t, ct).ConfigureAwait(false);
        }
    }

    private bool Diagnosed(int reelId) { for (var i = 0; i < _diagN; i++) if (_diagnosed[i] == reelId) return true; return false; }

    /// <summary>유지 곡선 수집(YT) · IG 추정 → 가드 진단 → 반영.</summary>
    public async Task<RetentionDiagnosis> DiagnoseAsync(TrackedAbTest t, CancellationToken ct = default)
    {
        if (_diagN < _diagnosed.Length) _diagnosed[_diagN++] = t.ReelId;
        var pts = await Promoter.FetchRetentionAsync(SocialPlatform.YouTube, t.VideoId, t.DeployedAtUnix, _ratio, ct).ConfigureAwait(false);
        YtCurveN = pts > 0 ? UnderperformingClipSelfDiagnosisGuard.ResampleToSeconds(_ratio.AsSpan(0, pts), t.DurationSec, YtCurve) : 0;
        CurveReel = t.ReelId;
        var ig = Promoter.Find(t.ReelId, SocialPlatform.Instagram);
        if (ig is not null)
        {
            double avg = 0; long v = 0;
            foreach (var m in ig.Metrics) { avg += m.AvgViewSec * m.Views; v += m.Views; }
            IgCurveN = v > 0 ? UnderperformingClipSelfDiagnosisGuard.EstimateCurve(avg / v, t.DurationSec, IgCurve) : 0;
        }
        long views = 0; foreach (var m in t.Metrics) views += m.Views;
        BuildFeatures(t.DurationSec);
        var now = Promoter.UnixNow();
        var d = Guard.Diagnose(t.ReelId, t.Platform, YtCurve.AsSpan(0, YtCurveN), _features.AsSpan(0, Math.Min(_features.Length, YtCurveN)), views, t.DeployedAtUnix, now);
        if (d.Verdict == DiagnosisVerdict.Underperforming)
        {
            if (Guard.CutBiasSec == 0 && _station is not null) Guard.BaseCutIntervalSec = _station.EffectiveCutIntervalSec;
            if (Guard.ApplyTo(_station?.Evolution, _beat?.Ducker, now))
            {
                if (_station is not null) _station.CutIntervalSec = Guard.TargetCutIntervalSec;
                LastAction = "🩺 릴 #" + t.ReelId.ToString(CultureInfo.InvariantCulture) + " 완청 " + (d.Completion * 100).ToString("0", CultureInfo.InvariantCulture) + "% · " + UnderperformingClipSelfDiagnosisGuard.CauseName(d.Cause)
                    + " @" + d.DropSec.ToString(CultureInfo.InvariantCulture) + "s → EPG 보정(발화 " + (d.SpeechDelta * 100).ToString("+0;-0;0", CultureInfo.InvariantCulture) + "% · 컷 " + d.CutDeltaSec.ToString("+0.0;-0.0;0", CultureInfo.InvariantCulture) + " s)";
            }
        }
        else LastAction = "🩺 릴 #" + t.ReelId.ToString(CultureInfo.InvariantCulture) + " 진단: " + UnderperformingClipSelfDiagnosisGuard.VerdictName(d.Verdict) + " · 완청 " + (d.Completion * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
        Raise();
        return d;
    }

    /// <summary>클립 구조 특징(데모: v17.69 패키징 기록 대용 — 중반 설명 구간 WPM 저하 + 롱테이크).</summary>
    private void BuildFeatures(double durationSec)
    {
        var n = Math.Min(_features.Length, (int)Math.Ceiling(durationSec) + 1);
        float sinceCut = 0;
        for (var s = 0; s < n; s++)
        {
            var r = s / durationSec;
            var explainer = r > 0.3 && r < 0.6;
            sinceCut = explainer ? sinceCut + 1 : s % 3 == 0 ? 0 : sinceCut + 1;
            _features[s] = new ClipSecondFeature { SpeechWpm = explainer ? 118 : 165, BgmDuckDb = -12, SecSinceCut = sinceCut, Motion = explainer ? 0.2f : 0.45f };
        }
    }

    private void OnPromoterPhase(TrackedAbTest t)
    {
        LastAction = (t.Phase == AbTestPhase.Promoted ? "🏆 " : t.Phase == AbTestPhase.Failed ? "⚠ " : "🧪 ") + SocialMediaApiGatewayEngine.PlatformName(t.Platform) + " 릴 #" + t.ReelId.ToString(CultureInfo.InvariantCulture)
            + " " + LiveMetricTrackerAndAbPromoterSynthesizer.PhaseName(t.Phase) + (t.Note is null ? "" : " — " + t.Note);
        Raise();
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Retention.OnChange", ex); }
    }

    public void Dispose()
    {
        Stop();
        try { _trimTask?.Wait(10_000); } catch (AggregateException) { }
        Promoter.OnPhase -= OnPromoterPhase;
        Promoter.Dispose();
        _ownGateway?.Dispose();
        _vault?.Dispose();
        CryptographicOperations.ZeroMemory(_vaultKey);
        OnChange = null;
    }

    public string Summary() =>
        "트리밍 " + Trimmer.Last.TrimmedMs.ToString(CultureInfo.InvariantCulture) + " ms · A/B 추적 " + Promoter.Active.ToString(CultureInfo.InvariantCulture) + " · 승격 " + Promoter.Promotions.ToString(CultureInfo.InvariantCulture)
        + " · 진단 " + Guard.Underperformers.ToString(CultureInfo.InvariantCulture) + "/" + Guard.Diagnosed.ToString(CultureInfo.InvariantCulture) + " · EPG 보정 " + Guard.Applied.ToString(CultureInfo.InvariantCulture);
}
