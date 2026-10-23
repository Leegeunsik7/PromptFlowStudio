using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using PromptFlowStudio.Services.CopyrightCompliance;
using PromptFlowStudio.Services.LiveArchive;
using PromptFlowStudio.Services.LiveClipper;
using PromptFlowStudio.Services.MatrixSyndication;
using PromptFlowStudio.Services.MultilingualDubbing;
using PromptFlowStudio.Services.ViralExport;
using PromptFlowStudio.Services.VodRepurposing;

namespace PromptFlowStudio.Services.MasterPipeline;

/// <summary>v17.92.0 실러너 체인 7 스테이지.</summary>
public enum RunnerStage : byte { Clip, Score, Compliance, Dubbing, Variant, Article, Vault }

/// <summary>v17.92.0 스테이지 상태(Blocked = 상류 인터락으로 하류 차단).</summary>
public enum RunnerStageState : byte { Pending, Running, Done, Failed, Blocked }

/// <summary>v17.92.0 체인 실행 상태.</summary>
public enum RunnerChainState : byte { Idle, Running, Completed, Aborted }

/// <summary>v17.92.0 스테이지 텔레메트리(실측 µs · 산출물 바이트 · 실패 시 회수 바이트).</summary>
public readonly record struct RunnerStageTelemetry(RunnerStage Stage, RunnerStageState State, long ElapsedUs, long Bytes, long Seq);

/// <summary>
/// v17.92.0 무할당 체인 컨텍스트 — 스테이지 간 페이로드(NALU fMP4 · PCM · 텍스트 · PNG)는 가드 스테이징 리스(풀 대여) 참조와 길이만 전달.
/// 단일 인스턴스를 실행마다 <see cref="Clear"/> 로 재사용한다.
/// </summary>
public sealed class RealRunnerChainContext
{
    public const int StageCount = 7, Cards = ViralCardNewsOgRasterizerEngine.Slides, DubTracks = MultilingualDubbingStudioRunner.TrackCount - 1;

    public int SegmentSeconds;
    public long RunId;
    // ① 클립
    public long ClipId, ClipStartUs, ClipEndUs, ClipBytes;
    // ② 스코어 + 9:16 fMP4(NALU 비트스트림)
    public float Score;
    public ViralGrade Grade;
    public byte[]? Nalu;
    public int NaluLength;
    // ③ 저작권 치환 PCM
    public float RiskBefore, RiskAfter;
    public float[]? Pcm;
    public int PcmLength;
    // ④ 더빙 PCM(EN/ES/JA 연접)
    public float[]? Dub;
    public int DubLength;
    // ⑤ 변형 썸네일 RGB
    public byte[]? Variant;
    public int VariantLength, VariantChannels;
    // ⑥ 마크다운 아티클 + 1:1 카드뉴스 PNG 5
    public char[]? Text;
    public int TextLength;
    public readonly byte[]?[] Card = new byte[]?[Cards];
    public readonly int[] CardLength = new int[Cards];
    // ⑦ 볼트
    public int VaultObjects;
    public long VaultBytes;

    public readonly RunnerStageState[] States = new RunnerStageState[StageCount];
    public readonly long[] StageUs = new long[StageCount];
    public readonly long[] StageBytes = new long[StageCount];

    public int DoneCount { get { var n = 0; foreach (var s in States) if (s == RunnerStageState.Done) n++; return n; } }
    public long TotalUs { get { long t = 0; foreach (var u in StageUs) t += u; return t; } }

    public ReadOnlySpan<byte> NaluSpan => Nalu is null ? default : Nalu.AsSpan(0, NaluLength);
    public ReadOnlySpan<char> TextSpan => Text is null ? default : Text.AsSpan(0, TextLength);
    public ReadOnlySpan<byte> CardSpan(int i) => Card[i] is { } c ? c.AsSpan(0, CardLength[i]) : default;

    public void Clear(int segmentSeconds, long runId)
    {
        SegmentSeconds = segmentSeconds; RunId = runId;
        ClipId = ClipStartUs = ClipEndUs = ClipBytes = 0;
        Score = 0; Grade = default; RiskBefore = RiskAfter = 0;
        VariantChannels = VaultObjects = 0; VaultBytes = 0;
        DropPayloads();
        Array.Clear(States); Array.Clear(StageUs); Array.Clear(StageBytes);
    }

    /// <summary>스테이징 참조 해제(버퍼 자체는 가드가 풀에 반환).</summary>
    public void DropPayloads()
    {
        Nalu = null; Pcm = null; Dub = null; Variant = null; Text = null;
        NaluLength = PcmLength = DubLength = VariantLength = TextLength = 0;
        Array.Clear(Card); Array.Clear(CardLength);
    }
}

/// <summary>
/// [PromptFlow Studio v17.92.0] ⚡ E2E 실러너 직결 체인 실행 엔진 — v17.90 근사 상태 머신을 대체해 실제 싱글톤 러너를 순차 호출.
///   ① v17.84 <see cref="LiveToShortsAutoClipperStudioRunner"/>(링 전진 · 즉석 클립 패키징) → ② v17.85 <see cref="ViralPredictorShortsExportStudioRunner"/>(스코어 · fMP4 인출)
///   → ③ v17.91 <see cref="CopyrightComplianceStudioRunner"/>(지문 검역 · BGM 치환) → ④ v17.82 <see cref="MultilingualDubbingStudioRunner"/>(EN/ES/JA 더빙)
///   → ⑤ v17.81 <see cref="MatrixSyndicationStudioRunner"/>(채널 변형 렌더) → ⑥ v17.88 <see cref="VodRepurposingStudioRunner"/>(아티클 · 카드뉴스 5)
///   → ⑦ v17.87 <see cref="LiveArchiveCloudVaultStudioRunner"/>(SigV4 볼트 적재).
///   ▸ 페이로드: <see cref="RealRunnerChainContext"/> 단일 인스턴스 + <see cref="PipelineInterlockStagingRollbackGuard"/> 풀 리스 — 스팬 복사만(재실행 무할당).
///   ▸ 텔레메트리: 스테이지 시작/완료/실패마다 실측 µs · 바이트를 <see cref="Telemetry"/>(Bounded/DropOldest) + <see cref="Observer"/> 로 디스패치.
///   ▸ 인터락: 예외 · Content ID 위험도 &gt; <see cref="RiskCeiling"/> → 가드 트립 → 상류 버퍼 원자 회수 → 하류 Blocked.
/// </summary>
public sealed class RealRunnerE2EChainExecutionEngine : IDisposable
{
    public const int StageCount = RealRunnerChainContext.StageCount, MinSegmentSeconds = 35, MaxSegmentSeconds = 110, DefaultSegmentSeconds = 40, TelemetryCapacity = 64;
    public const float DefaultRiskCeiling = AcousticFingerprintContentIdAnalyzerEngine.RiskThreshold;

    public static readonly string[] StageNames = { "킬러클립 절삭", "바이럴 스코어 · fMP4 인출", "저작권 검역 · BGM 치환", "다국어 더빙", "채널 변형 렌더", "아티클 · 카드뉴스", "클라우드 볼트 적재" };
    public static readonly string[] StageIcons = { "✂️", "📊", "🛡️", "🗣️", "🎞️", "📝", "💾" };
    public static readonly string[] StageSources = { "v17.84", "v17.85", "v17.91", "v17.82", "v17.81", "v17.88", "v17.87" };
    public static readonly string[] RunnerNames = { "LiveClipper", "ViralPredictor", "CopyrightCompliance", "DubbingLipSync", "ChannelVariant", "VodRepurposing", "LiveArchiveVault" };

    private readonly Channel<RunnerStageTelemetry> _telemetry = Channel.CreateBounded<RunnerStageTelemetry>(new BoundedChannelOptions(TelemetryCapacity)
    {
        FullMode = BoundedChannelFullMode.DropOldest, SingleReader = false, SingleWriter = false,
    });
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _seq, _runId;
    private int _next;

    public RealRunnerE2EChainExecutionEngine(
        LiveToShortsAutoClipperStudioRunner clipper, ViralPredictorShortsExportStudioRunner predictor, CopyrightComplianceStudioRunner compliance,
        MultilingualDubbingStudioRunner dubbing, MatrixSyndicationStudioRunner variant, VodRepurposingStudioRunner vod, LiveArchiveCloudVaultStudioRunner vault,
        PipelineInterlockStagingRollbackGuard? guard = null)
    {
        Clipper = clipper ?? throw new ArgumentNullException(nameof(clipper));
        Predictor = predictor ?? throw new ArgumentNullException(nameof(predictor));
        if (!ReferenceEquals(predictor.Clipper, clipper)) throw new ArgumentException("ViralPredictor 는 같은 LiveClipper 인스턴스를 공유해야 합니다.", nameof(predictor));
        Compliance = compliance ?? throw new ArgumentNullException(nameof(compliance));
        Dubbing = dubbing ?? throw new ArgumentNullException(nameof(dubbing));
        Variant = variant ?? throw new ArgumentNullException(nameof(variant));
        Vod = vod ?? throw new ArgumentNullException(nameof(vod));
        Vault = vault ?? throw new ArgumentNullException(nameof(vault));
        Guard = guard ?? new PipelineInterlockStagingRollbackGuard();
    }

    public LiveToShortsAutoClipperStudioRunner Clipper { get; }
    public ViralPredictorShortsExportStudioRunner Predictor { get; }
    public CopyrightComplianceStudioRunner Compliance { get; }
    public MultilingualDubbingStudioRunner Dubbing { get; }
    public MatrixSyndicationStudioRunner Variant { get; }
    public VodRepurposingStudioRunner Vod { get; }
    public LiveArchiveCloudVaultStudioRunner Vault { get; }
    public PipelineInterlockStagingRollbackGuard Guard { get; }
    public RealRunnerChainContext Context { get; } = new();
    public ChannelReader<RunnerStageTelemetry> Telemetry => _telemetry.Reader;
    /// <summary>동기 관찰자(실행 스레드에서 호출 · 예외 격리).</summary>
    public Action<RunnerStageTelemetry>? Observer { get; set; }
    public RunnerChainState State { get; private set; }
    public bool Busy => _gate.CurrentCount == 0;
    public int NextStage => Volatile.Read(ref _next);
    public long Published => Interlocked.Read(ref _seq);
    public long Runs { get; private set; }
    public long Completions { get; private set; }
    public long Aborts { get; private set; }
    public string? LastError { get; private set; }
    /// <summary>직전 인터락 롤백에서 회수한 스테이징 바이트.</summary>
    public long LastReclaimedBytes { get; private set; }
    /// <summary>치환 후 Content ID 위험도 허용 상한(초과 → 치환 불가 인터락).</summary>
    public float RiskCeiling { get; set; } = DefaultRiskCeiling;
    /// <summary>바이럴 점수 하한(0 = 미적용).</summary>
    public float MinViralScore { get; set; }
    /// <summary>테스트/관제용 결함 주입 스테이지(−1 = 없음) — 해당 스테이지의 실러너 호출 직후 인터락.</summary>
    public int InjectFaultStage { get; set; } = -1;
    public bool UploadToVault { get; set; } = true;

    // ══════════════════════ 실행 제어 ══════════════════════

    /// <summary>새 실행 무장 — 이전 스테이징 회수 · 래치 해제 · 컨텍스트 재사용.</summary>
    public void Begin(int segmentSeconds = DefaultSegmentSeconds)
    {
        Guard.Arm();
        Context.Clear(Math.Clamp(segmentSeconds, MinSegmentSeconds, MaxSegmentSeconds), ++_runId);
        LastError = null;
        Volatile.Write(ref _next, 0);
        State = RunnerChainState.Running;
        Runs++;
    }

    /// <summary>단일 스테이지 수동 스텝 — 대기/종료 상태면 새 실행을 무장한 뒤 ① 부터. 성공 여부.</summary>
    public async Task<bool> StepAsync(int segmentSeconds = DefaultSegmentSeconds, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State != RunnerChainState.Running) Begin(segmentSeconds);
            return await ExecuteStageAsync(_next, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>E2E 전 단계 — 새 실행 무장 후 ①→⑦ 순차(인터락 시 즉시 중단).</summary>
    public async Task<RunnerChainState> RunAllAsync(int segmentSeconds = DefaultSegmentSeconds, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Begin(segmentSeconds);
            while (State == RunnerChainState.Running)
                if (!await ExecuteStageAsync(_next, ct).ConfigureAwait(false)) break;
            return State;
        }
        finally { _gate.Release(); }
    }

    /// <summary>완료 패키지 소비 후 스테이징 회수(정상 경로 · 롤백 카운트 없음).</summary>
    public long ReleaseStaging()
    {
        var n = Guard.ReleaseAll();
        Context.DropPayloads();
        return n;
    }

    public void Reset()
    {
        if (!_gate.Wait(0)) return;
        try
        {
            ReleaseStaging();
            Guard.Arm();
            Context.Clear(DefaultSegmentSeconds, _runId);
            Volatile.Write(ref _next, 0);
            State = RunnerChainState.Idle;
            LastError = null;
        }
        finally { _gate.Release(); }
    }

    private async Task<bool> ExecuteStageAsync(int s, CancellationToken ct)
    {
        var ctx = Context;
        var t0 = Stopwatch.GetTimestamp();
        ctx.States[s] = RunnerStageState.Running;
        Publish(s, RunnerStageState.Running, 0, 0);
        try
        {
            if (Guard.IsTripped) throw new PipelineInterlockException(InterlockReason.StageFailed, "상류 인터락 — 하류 실행 차단");
            ct.ThrowIfCancellationRequested();
            var bytes = (RunnerStage)s switch
            {
                RunnerStage.Clip => RunClip(ctx),
                RunnerStage.Score => RunScore(ctx, ct),
                RunnerStage.Compliance => await RunComplianceAsync(ctx).ConfigureAwait(false),
                RunnerStage.Dubbing => RunDubbing(ctx),
                RunnerStage.Variant => RunVariant(ctx),
                RunnerStage.Article => await RunArticleAsync(ctx).ConfigureAwait(false),
                _ => await RunVaultAsync(ctx, ct).ConfigureAwait(false),
            };
            if (InjectFaultStage == s)
                throw new PipelineInterlockException(s == (int)RunnerStage.Compliance ? InterlockReason.ContentIdRisk : InterlockReason.StageFailed,
                    s == (int)RunnerStage.Compliance ? "주입: Content ID 위험도 70 초과 — 치환 불가" : "주입 결함 — " + StageNames[s]);
            var us = Elapsed(t0);
            ctx.StageUs[s] = us; ctx.StageBytes[s] = bytes; ctx.States[s] = RunnerStageState.Done;
            Publish(s, RunnerStageState.Done, us, bytes);
            if (s == StageCount - 1) { State = RunnerChainState.Completed; Completions++; }
            Volatile.Write(ref _next, Math.Min(s + 1, StageCount - 1));
            return true;
        }
        catch (Exception ex)
        {
            var reason = ex is PipelineInterlockException pie ? pie.Reason : ex is OperationCanceledException ? InterlockReason.Cancelled : InterlockReason.Exception;
            Guard.Trip(s, reason, ex.Message);
            var reclaimed = Guard.Rollback();
            LastReclaimedBytes = reclaimed;
            ctx.DropPayloads();
            var us = Elapsed(t0);
            ctx.StageUs[s] = us; ctx.States[s] = RunnerStageState.Failed;
            for (var d = s + 1; d < StageCount; d++) ctx.States[d] = RunnerStageState.Blocked;
            State = RunnerChainState.Aborted;
            Aborts++;
            LastError = StageNames[s] + ": " + ex.Message;
            if (ex is not PipelineInterlockException and not OperationCanceledException) PromptFlowStudio.Program.Log("MasterPipeline.Stage", ex);
            Publish(s, RunnerStageState.Failed, us, reclaimed);
            return false;
        }
    }

    // ══════════════════════ 스테이지(실러너 직결) ══════════════════════

    private long RunClip(RealRunnerChainContext ctx)
    {
        if (!Clipper.Running) Clipper.Advance(ctx.SegmentSeconds);
        Clipper.InstantClip();
        var p = Clipper.Packager;
        for (var i = 0; i < p.ResultCount; i++)
        {
            var r = p.Result(i);
            if (!ViralPredictorShortsExportStudioRunner.Usable(r.Status)) continue;
            ctx.ClipId = r.Id; ctx.ClipStartUs = r.StartUs; ctx.ClipEndUs = r.EndUs; ctx.ClipBytes = r.Bytes;
            return r.Bytes;
        }
        throw new PipelineInterlockException(InterlockReason.StageFailed, "클립 패키징 결과 없음");
    }

    private long RunScore(RealRunnerChainContext ctx, CancellationToken ct)
    {
        if (!Predictor.Select(ctx.ClipId)) throw new PipelineInterlockException(InterlockReason.StageFailed, "클립 #" + ctx.ClipId.ToString(CultureInfo.InvariantCulture) + " 선택 실패");
        var score = Predictor.Score;
        ctx.Score = score.Total; ctx.Grade = score.Grade;
        if (MinViralScore > 0 && score.Total < MinViralScore)
            throw new PipelineInterlockException(InterlockReason.StageFailed, "바이럴 점수 " + score.Total.ToString("0", CultureInfo.InvariantCulture) + " < " + MinViralScore.ToString("0", CultureInfo.InvariantCulture));
        var st = Predictor.ExportNow(ct);
        var mem = Predictor.Exporter.Output;
        if (st != ExportStatus.Ready || mem.IsEmpty) throw new PipelineInterlockException(InterlockReason.ExportFailed, "fMP4 인출 " + FastShortsMuxingHardwareExporterEngine.StatusName(st));
        var buf = Guard.LeaseBytes(mem.Length);
        mem.Span.CopyTo(buf);
        ctx.Nalu = buf; ctx.NaluLength = mem.Length;
        return mem.Length;
    }

    private async Task<long> RunComplianceAsync(RealRunnerChainContext ctx)
    {
        var safe = await Compliance.AuditAsync().ConfigureAwait(false);
        ctx.RiskBefore = Compliance.RiskBefore; ctx.RiskAfter = Compliance.RiskAfter;
        if (!Compliance.Audited || ctx.RiskAfter > RiskCeiling || (!safe && !Compliance.SafeToPublish))
            throw new PipelineInterlockException(InterlockReason.ContentIdRisk,
                "Content ID 위험도 " + ctx.RiskAfter.ToString("0", CultureInfo.InvariantCulture) + " (상한 " + RiskCeiling.ToString("0", CultureInfo.InvariantCulture) + ") — 치환 불가 · 게시 보류");
        return StagePcm(ctx);
    }

    private long StagePcm(RealRunnerChainContext ctx)
    {
        var pcm = Compliance.Substituted;
        var buf = Guard.LeaseFloats(pcm.Length);
        pcm.CopyTo(buf);
        ctx.Pcm = buf; ctx.PcmLength = pcm.Length;
        return (long)pcm.Length * sizeof(float);
    }

    private long RunDubbing(RealRunnerChainContext ctx)
    {
        Dubbing.EnsurePrepared();
        var total = 0;
        for (var t = 1; t <= RealRunnerChainContext.DubTracks; t++) total += Dubbing.Pcm(t).Length;
        if (total == 0) throw new PipelineInterlockException(InterlockReason.StageFailed, "더빙 트랙 비어 있음");
        var buf = Guard.LeaseFloats(total);
        var o = 0;
        for (var t = 1; t <= RealRunnerChainContext.DubTracks; t++) { var p = Dubbing.Pcm(t); p.CopyTo(buf.AsSpan(o)); o += p.Length; }
        ctx.Dub = buf; ctx.DubLength = o;
        return (long)o * sizeof(float);
    }

    private long RunVariant(RealRunnerChainContext ctx)
    {
        Variant.RenderAll();
        var n = Variant.Router.ChannelCount;
        if (n == 0) throw new PipelineInterlockException(InterlockReason.StageFailed, "변형 채널 없음");
        var total = 0;
        for (var c = 0; c < n; c++) total += Variant.Thumb(c).Length;
        var buf = Guard.LeaseBytes(total);
        var o = 0;
        for (var c = 0; c < n; c++) { var th = Variant.Thumb(c); th.CopyTo(buf.AsSpan(o)); o += th.Length; }
        ctx.Variant = buf; ctx.VariantLength = o; ctx.VariantChannels = n;
        return o;
    }

    private async Task<long> RunArticleAsync(RealRunnerChainContext ctx)
    {
        if (!await Vod.RunAsync().ConfigureAwait(false)) throw new PipelineInterlockException(InterlockReason.StageFailed, "리퍼포징 실패 — " + Vod.LastAction);
        return StageArticle(ctx);
    }

    private long StageArticle(RealRunnerChainContext ctx)
    {
        var md = Vod.Markdown(ArticleTemplate.Blog);
        if (md.Length == 0) throw new PipelineInterlockException(InterlockReason.StageFailed, "마크다운 아티클 비어 있음");
        var text = Guard.LeaseChars(md.Length);
        md.AsSpan().CopyTo(text);
        ctx.Text = text; ctx.TextLength = md.Length;
        long bytes = (long)md.Length * sizeof(char);
        for (var i = 0; i < RealRunnerChainContext.Cards; i++)
        {
            var png = Vod.Rasterizer.Png(i);
            if (png.IsEmpty) throw new PipelineInterlockException(InterlockReason.StageFailed, "카드뉴스 " + (i + 1).ToString(CultureInfo.InvariantCulture) + " PNG 없음");
            var b = Guard.LeaseBytes(png.Length);
            png.CopyTo(b);
            ctx.Card[i] = b; ctx.CardLength[i] = png.Length;
            bytes += png.Length;
        }
        return bytes;
    }

    private async Task<long> RunVaultAsync(RealRunnerChainContext ctx, CancellationToken ct)
    {
        if (!UploadToVault) return 0;
        if (Vault.Uploading) await Vault.DrainUploadsAsync(10_000).ConfigureAwait(false);   // 게이트웨이 파트 버퍼 공유 — 녹화 업로드와 직렬화
        var prefix = "packages/run_" + ctx.RunId.ToString("D5", CultureInfo.InvariantCulture) + "_" + ctx.ClipId.ToString(CultureInfo.InvariantCulture) + "/";
        await PutAsync(ctx, ctx.Nalu!, ctx.NaluLength, prefix + "shorts_9x16.mp4", ct).ConfigureAwait(false);
        for (var i = 0; i < RealRunnerChainContext.Cards; i++)
            await PutAsync(ctx, ctx.Card[i]!, ctx.CardLength[i], prefix + "card_" + (i + 1).ToString(CultureInfo.InvariantCulture) + "_1080.png", ct).ConfigureAwait(false);
        var md = Guard.LeaseBytes(Encoding.UTF8.GetMaxByteCount(ctx.TextLength));
        var mdLen = Encoding.UTF8.GetBytes(ctx.Text!, 0, ctx.TextLength, md, 0);
        await PutAsync(ctx, md, mdLen, prefix + "article.md", ct).ConfigureAwait(false);
        return ctx.VaultBytes;
    }

    private async Task PutAsync(RealRunnerChainContext ctx, byte[] buf, int length, string key, CancellationToken ct)
    {
        using var ms = new MemoryStream(buf, 0, length, false);
        var r = await Vault.Gateway.UploadAsync(ms, key, StorageTier.Hot, ct).ConfigureAwait(false);
        if (!r.Ok) throw new PipelineInterlockException(InterlockReason.StageFailed, "볼트 적재 실패 " + key + " — " + r.Error);
        ctx.VaultObjects++;
        ctx.VaultBytes += r.Bytes;
    }

    // ══════════════════════ 텔레메트리 ══════════════════════

    private void Publish(int s, RunnerStageState state, long us, long bytes)
    {
        var e = new RunnerStageTelemetry((RunnerStage)s, state, us, bytes, Interlocked.Increment(ref _seq));
        _telemetry.Writer.TryWrite(e);
        try { Observer?.Invoke(e); }
        catch (Exception ex) { PromptFlowStudio.Program.Log("MasterPipeline.Observer", ex); }
    }

    private static long Elapsed(long t0) => (Stopwatch.GetTimestamp() - t0) * 1_000_000L / Stopwatch.Frequency;

    public void Dispose()
    {
        _telemetry.Writer.TryComplete();
        if (_gate.Wait(5000)) { ReleaseStaging(); _gate.Release(); }
        _gate.Dispose();
    }
}
