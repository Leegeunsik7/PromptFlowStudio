using System.Globalization;
using PromptFlowStudio.Services.MatrixSyndication;
using PromptFlowStudio.Services.ViralExport;

namespace PromptFlowStudio.Services.PostPublishSplitTest;

/// <summary>
/// [PromptFlow Studio v17.93.0] ⚖️ 포스트-퍼블리시 A/B &amp; 패자 회수기 러너(HUD <c>PstRun</c>).
///   ▸ v17.85 스코어러(훅 카피 · 썸네일 슬롯 · MP4 출력)와 v17.81 매트릭스 러너를 DI 싱글톤으로 주입 — 없으면 매트릭스만 로컬 폴백(소유 · Dispose).
///   ▸ 시뮬레이션 시계 5 분 틱마다 변형별 플랫폼 지표를 폴링 → <see cref="PostPublishSplitTestingEngine"/> 기록 · 판정 →
///     승자 확정 시 <see cref="ViralWinnerSubChannelPromoterGuard"/> 매트릭스 승격, 90 분 경과 시 <see cref="LowPerformanceAutoPrunerEngine"/> 회수.
/// </summary>
public sealed class PostPublishSplitTestStudioRunner : IDisposable
{
    public const int LogSize = 32, MinStaggerMin = 0, MaxStaggerMin = 30;
    private static readonly string[] DefaultHooks = { "이거 모르면 손해… 진짜 마지막 3초 반전?", "구독자 98%가 놓친 이 장면 하나" };

    private readonly MatrixSyndicationStudioRunner? _ownedMatrix;
    private readonly byte[] _fallbackMp4;
    private readonly string[] _log = new string[LogSize];
    private int _logHead, _logCount, _content = 9300;

    public PostPublishSplitTestStudioRunner(IServiceProvider sp)
        : this(sp.GetService(typeof(MatrixSyndicationStudioRunner)) as MatrixSyndicationStudioRunner, sp.GetService(typeof(ViralPredictorShortsExportStudioRunner)) as ViralPredictorShortsExportStudioRunner) { }

    public PostPublishSplitTestStudioRunner(MatrixSyndicationStudioRunner? matrix = null, ViralPredictorShortsExportStudioRunner? predictor = null)
    {
        MatrixInjected = matrix is not null;
        Matrix = matrix ?? (_ownedMatrix = new MatrixSyndicationStudioRunner());
        Predictor = predictor;
        Gateway = new SimulatedVideoVisibilityGateway();
        Pruner = new LowPerformanceAutoPrunerEngine(Gateway);
        Promoter = new ViralWinnerSubChannelPromoterGuard(Matrix.Router);
        _fallbackMp4 = new byte[32 * 1024];
        _fallbackMp4[3] = 0x18;
        "ftypisom"u8.CopyTo(_fallbackMp4.AsSpan(4));
        SimNow = Matrix.SimNow;
    }

    public event Action? OnChange;
    public PostPublishSplitTestingEngine Engine { get; } = new();
    public LowPerformanceAutoPrunerEngine Pruner { get; }
    public ViralWinnerSubChannelPromoterGuard Promoter { get; }
    public SimulatedVideoVisibilityGateway Gateway { get; }
    public MatrixSyndicationStudioRunner Matrix { get; }
    public ViralPredictorShortsExportStudioRunner? Predictor { get; }
    public bool MatrixInjected { get; }
    public bool PredictorInjected => Predictor is not null;
    public long SimNow { get; private set; }
    public int StaggerMinutes { get; private set; } = 10;
    public bool SplitByChannel { get; private set; }
    public bool BWinsScenario { get; private set; }
    public bool DeleteMode => Pruner.Mode == PruneAction.Delete;
    public long Ticks { get; private set; }
    public int LastRouted { get; private set; }
    public string? LastAction { get; private set; }
    public int LogCount => _logCount;
    public long ElapsedSec => Engine.Phase == SplitTestPhase.Idle ? 0 : Math.Max(0, SimNow - Engine.StartUnix);
    public long RemainingSec => Engine.RemainingSec(SimNow);

    /// <summary>최근 로그(0 = 최신).</summary>
    public string Log(int i) => _log[(_logHead - 1 - i + LogSize * 2) % LogSize];

    // ══════════════════════ 조작(비핫패스) ══════════════════════

    public void SetStagger(int minutes)
    {
        StaggerMinutes = Math.Clamp(minutes, MinStaggerMin, MaxStaggerMin);
        LastAction = "⏱ B 시차 " + StaggerMinutes.ToString(CultureInfo.InvariantCulture) + "분";
        Raise();
    }

    public void ToggleSplitByChannel()
    {
        SplitByChannel = !SplitByChannel;
        LastAction = SplitByChannel ? "🔀 분기 방식: 채널 분리(A 메인 · B 서브)" : "🔀 분기 방식: 같은 채널 시차 발행";
        Raise();
    }

    public void ToggleBWins()
    {
        BWinsScenario = !BWinsScenario;
        LastAction = "🧪 시나리오: " + (BWinsScenario ? "B 우세" : "A 우세") + " — 다음 배포부터 적용";
        Raise();
    }

    public void ToggleDeleteMode()
    {
        Pruner.Mode = DeleteMode ? PruneAction.Private : PruneAction.Delete;
        LastAction = "🗑️ 회수 방식: " + LowPerformanceAutoPrunerEngine.ActionName(Pruner.Mode);
        Raise();
    }

    /// <summary>A/B 분기 배포 — v17.85 훅 카피 0/1 슬롯(없으면 기본 카피) · 썸네일 슬롯 0/1.</summary>
    public int Deploy()
    {
        var id = ++_content;
        var stagger = SplitByChannel ? 0L : Math.Max(1, StaggerMinutes) * 60L;
        var chB = SplitByChannel && Matrix.Router.ChannelCount > 1 ? 1 : 0;
        if (chB == 0 && stagger == 0) stagger = 60;
        Engine.Deploy(id, Hook(0), 0, 0, Hook(1), 1, chB, SimNow, stagger);
        Push("🚀 #" + Id(id) + " 분기 배포 — A「" + Engine.Track(SplitVariant.A).Hook + "」 · B「" + Engine.Track(SplitVariant.B).Hook + "」 · " +
             (chB != 0 ? "채널 분리" : "시차 " + (stagger / 60).ToString(CultureInfo.InvariantCulture) + "분"));
        LastAction = "🚀 #" + Id(id) + " A/B 배포 — 90 분 골든타임 추적 시작";
        Raise();
        return id;
    }

    private string Hook(int slot)
    {
        var p = Predictor;
        if (p is not null && p.Thumbs.Count > slot)
        {
            var s = p.Thumbs.CopyText(slot);
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }
        return DefaultHooks[slot];
    }

    /// <summary>시뮬레이션 시계 전진(5 분 단위) — 폴링 · 판정 · 승격 · 회수. 반환 = 기록된 표본 수.</summary>
    public int Advance(int minutes)
    {
        if (Engine.Phase == SplitTestPhase.Idle) Deploy();
        var recorded = 0;
        var steps = Math.Max(1, minutes / 5);
        for (var k = 0; k < steps && Engine.Phase != SplitTestPhase.Closed; k++)
        {
            SimNow += PostPublishSplitTestingEngine.IntervalSec;
            Ticks++;
            recorded += Poll(SplitVariant.A) + Poll(SplitVariant.B);
            if (Engine.Evaluate(SimNow))
            {
                Push("🏆 승자 " + PostPublishSplitTestingEngine.VariantName(Engine.Winner) + " 확정 — 신뢰도 " + Pct(Engine.Confidence) + " · 스코어 A " + F2(Engine.Track(SplitVariant.A).Score) + " / B " + F2(Engine.Track(SplitVariant.B).Score));
                PromoteWinner();
            }
            PruneIfDue();
        }
        LastAction = "⏩ +" + (steps * 5).ToString(CultureInfo.InvariantCulture) + "분 · 표본 " + recorded.ToString(CultureInfo.InvariantCulture) + " · " + PostPublishSplitTestingEngine.PhaseName(Engine.Phase) + " · 남은 " + Mmss(RemainingSec);
        Raise();
        return recorded;
    }

    /// <summary>골든타임 종료까지 일괄 전진.</summary>
    public void RunToGoldenEnd()
    {
        if (Engine.Phase == SplitTestPhase.Idle) Deploy();
        var guard = 0;
        while (Engine.Phase != SplitTestPhase.Closed && guard++ < PostPublishSplitTestingEngine.Capacity * 3) Advance(5);
    }

    public void ForceWinner(SplitVariant v)
    {
        if (!Engine.ForceWinner(v, SimNow)) { LastAction = "⚠️ 배포 전 — 먼저 A/B 배포"; Raise(); return; }
        Push("👆 수동 강제 승자 " + PostPublishSplitTestingEngine.VariantName(v));
        PromoteWinner();
        PruneIfDue();
        LastAction = "👆 강제 승자 " + PostPublishSplitTestingEngine.VariantName(v) + " — " + ViralWinnerSubChannelPromoterGuard.ResultName(Promoter.LastResult);
        Raise();
    }

    public void Reset()
    {
        Engine.Reset();
        Array.Clear(_log);
        _logHead = _logCount = 0;
        Ticks = 0; LastRouted = 0;
        LastAction = "♻ 초기화";
        Raise();
    }

    // ══════════════════════ 지표 폴링 · 승격 · 회수 ══════════════════════

    /// <summary>모의 플랫폼 지표 — 변형 품질 프로필(우세/열세) 기반 결정적 곡선.</summary>
    private int Poll(SplitVariant v)
    {
        var t = Engine.Track(v);
        var age = SimNow - t.PublishedUnix;
        if (age <= 0) return 0;
        var strong = (v == SplitVariant.B) == BWinsScenario;
        var m = age / 60.0;
        long views = strong ? (long)(m * 14 + m * m * 0.12) : (long)(m * 0.55);
        var completion = strong ? 0.58f + (float)Math.Sin(m * 0.21) * 0.03f : 0.27f + (float)Math.Sin(m * 0.17) * 0.02f;
        var shares = strong ? (int)(views / 22) : (int)(views / 160);
        return Engine.Record(v, SimNow, views, completion, shares) ? 1 : 0;
    }

    private void PromoteWinner()
    {
        if (!Engine.HasWinner) return;
        var w = Engine.Track(Engine.Winner);
        ReadOnlyMemory<byte> mp4 = Predictor?.Exporter.Output ?? ReadOnlyMemory<byte>.Empty;
        if (mp4.IsEmpty) mp4 = _fallbackMp4;
        var asset = new WinnerMasterAsset(w.ContentId, w.Variant, mp4, ("1\n00:00:00,000 --> 00:00:03,000\n" + w.Hook).AsMemory(), ("{\"hook\":\"" + w.Hook.Replace("\"", "'", StringComparison.Ordinal) + "\",\"variant\":\"" + PostPublishSplitTestingEngine.VariantName(w.Variant) + "\"}").AsMemory());
        var r = Promoter.Promote(asset, SimNow, out var routed);
        LastRouted = routed;
        Push((r == PromoteResult.Promoted ? "📣 매트릭스 승격 — 서브 채널 " + routed.ToString(CultureInfo.InvariantCulture) + "건 예약 · 지터 " + (Promoter.History(0).JitterSec / 60).ToString(CultureInfo.InvariantCulture) + "분"
            : "⛔ 승격 " + ViralWinnerSubChannelPromoterGuard.ResultName(r)) + " · #" + Id(w.ContentId));
    }

    private void PruneIfDue()
    {
        var before = Pruner.AuditCount;
        Pruner.Evaluate(Engine, SimNow);
        for (var i = Pruner.AuditCount - before - 1; i >= 0; i--)
        {
            ref readonly var a = ref Pruner.Audit(i);
            Push((a.Ok ? "🗑️ 자동 회수 " : "⚠️ 회수 실패 ") + PostPublishSplitTestingEngine.VariantName(a.Variant) + " → " + LowPerformanceAutoPrunerEngine.ActionName(a.Action) + " · " +
                 LowPerformanceAutoPrunerEngine.ReasonText(a.Reason) + " · " + a.ViewsPerHour.ToString("0", CultureInfo.InvariantCulture) + "뷰/h · 완청 " + Pct(a.Completion) + " · HTTP " + a.Status.ToString(CultureInfo.InvariantCulture));
        }
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

    private static string Id(int id) => id.ToString(CultureInfo.InvariantCulture);
    private static string F2(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    public static string Pct(float v) => (v * 100f).ToString("0.0", CultureInfo.InvariantCulture) + "%";
    public static string Mmss(long sec) => (sec / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (sec % 60).ToString("00", CultureInfo.InvariantCulture);

    public string Summary() =>
        "틱 " + Ticks.ToString(CultureInfo.InvariantCulture) + " · 표본 " + Engine.Samples.ToString(CultureInfo.InvariantCulture) + " · 회수 " + Pruner.Pruned.ToString(CultureInfo.InvariantCulture) +
        " · 승격 " + Promoter.Promotions.ToString(CultureInfo.InvariantCulture) + " (예약 " + Promoter.RoutedPosts.ToString(CultureInfo.InvariantCulture) + ") · 매트릭스 " + (MatrixInjected ? "Injected" : "Local");

    private void Raise()
    {
        try { OnChange?.Invoke(); }
        catch (Exception ex) { PromptFlowStudio.Program.Log("PostPublishSplitTest.OnChange", ex); }
    }

    public void Dispose()
    {
        _ownedMatrix?.Dispose();
        OnChange = null;
    }
}
