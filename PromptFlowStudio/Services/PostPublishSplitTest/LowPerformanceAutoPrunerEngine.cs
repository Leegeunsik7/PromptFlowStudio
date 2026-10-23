namespace PromptFlowStudio.Services.PostPublishSplitTest;

public enum PruneAction : byte { None, Private, Delete }

[Flags]
public enum PruneReason : byte { None = 0, Loser = 1, LowVelocity = 2, LowCompletion = 4 }

/// <summary>자동 회수 감사 이벤트 1 건 — 값 타입 · 링 슬롯 재사용.</summary>
public struct PruneAuditEvent
{
    public long Seq, TUnix, Views;
    public int ContentId, Channel, Status, Attempts;
    public SplitVariant Variant;
    public PruneAction Action;
    public PruneReason Reason;
    public float ViewsPerHour, Completion;
    public bool Ok;
}

/// <summary>플랫폼 영상 공개 상태 변경 게이트웨이 — 반환 = HTTP 상태 코드.</summary>
public interface IVideoVisibilityGateway
{
    int Apply(int contentId, int channel, SplitVariant variant, PruneAction action);
}

/// <summary>인메모리 모의 플랫폼 — 비공개/삭제 카운터 · 거절 상태 주입(1 회 소모).</summary>
public sealed class SimulatedVideoVisibilityGateway : IVideoVisibilityGateway
{
    private int _failNext, _failStatus;

    public long Privates { get; private set; }
    public long Deletes { get; private set; }
    public long Calls { get; private set; }

    public void InjectFailures(int count, int status) { _failNext = Math.Max(0, count); _failStatus = status; }

    public int Apply(int contentId, int channel, SplitVariant variant, PruneAction action)
    {
        Calls++;
        if (_failNext > 0) { _failNext--; return _failStatus; }
        if (action == PruneAction.Delete) Deletes++; else if (action == PruneAction.Private) Privates++;
        return 200;
    }
}

/// <summary>
/// [PromptFlow Studio v17.93.0] 🗑️ 저성과 패자 클립 자동 회수 &amp; 채널 평판 가드.
///   ▸ 90 분 골든타임 경과 시 ① 패자 판정 ② 시간당 &lt; 50 뷰 ③ 완청률 &lt; 35 % 중 하나라도 해당하면 회수 대상.
///   ▸ 게이트웨이(<see cref="IVideoVisibilityGateway"/>)로 비공개(기본) 또는 삭제 — 429/5xx 는 최대 3 회 재시도, 이미 회수된 변형은 중복 차단.
///   ▸ 실행 결과 · 사유를 감사 링(64)에 무할당 기록.
/// </summary>
public sealed class LowPerformanceAutoPrunerEngine
{
    public const int AuditCap = 64, MaxAttempts = 3;
    public const float DefaultMinViewsPerHour = 50f, DefaultMinCompletion = 0.35f;

    private readonly PruneAuditEvent[] _audit = new PruneAuditEvent[AuditCap];
    private int _head, _count;
    private long _seq;

    public LowPerformanceAutoPrunerEngine(IVideoVisibilityGateway gateway) => Gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));

    public IVideoVisibilityGateway Gateway { get; }
    public PruneAction Mode { get; set; } = PruneAction.Private;
    public float MinViewsPerHour { get; set; } = DefaultMinViewsPerHour;
    public float MinCompletion { get; set; } = DefaultMinCompletion;
    public long Pruned { get; private set; }
    public long Failures { get; private set; }
    public long DuplicateBlocks { get; private set; }
    public long Retries { get; private set; }
    public int AuditCount => _count;

    /// <summary>감사 이벤트(0 = 최신).</summary>
    public ref readonly PruneAuditEvent Audit(int i) => ref _audit[(_head - 1 - i + AuditCap * 2) % AuditCap];

    public static string ActionName(PruneAction a) => a switch { PruneAction.Private => "비공개", PruneAction.Delete => "삭제", _ => "유지" };

    public static string ReasonText(PruneReason r)
    {
        if (r == PruneReason.None) return "정상";
        var s = "";
        if ((r & PruneReason.Loser) != 0) s = "A/B 패자";
        if ((r & PruneReason.LowVelocity) != 0) s += (s.Length > 0 ? " · " : "") + "저조한 유속";
        if ((r & PruneReason.LowCompletion) != 0) s += (s.Length > 0 ? " · " : "") + "저조한 완청률";
        return s;
    }

    /// <summary>변형 1 개의 회수 사유 판정(무할당).</summary>
    public PruneReason Classify(PostPublishSplitTestingEngine e, SplitVariant v, long nowUnix)
    {
        var t = e.Track(v);
        if (t.Count == 0) return PruneReason.None;
        var r = PruneReason.None;
        if (e.HasWinner && e.Winner != v) r |= PruneReason.Loser;
        if (t.ViewsPerHour(nowUnix) < MinViewsPerHour) r |= PruneReason.LowVelocity;
        if (t.Last.Completion < MinCompletion) r |= PruneReason.LowCompletion;
        return r;
    }

    /// <summary>골든타임 경과 후 두 변형 평가 → 회수 실행. 반환 = 이번 호출 회수 성공 수(골든타임 전 = 0).</summary>
    public int Evaluate(PostPublishSplitTestingEngine e, long nowUnix)
    {
        if (e.Phase == SplitTestPhase.Idle || nowUnix < e.GoldenEndUnix) return 0;
        var n = 0;
        for (var v = SplitVariant.A; v <= SplitVariant.B; v++)
        {
            var r = Classify(e, v, nowUnix);
            if (r == PruneReason.None) continue;
            // 승자가 확정된 경우 승자 클립은 지표 미달이어도 보존(증폭 대상)
            if (e.HasWinner && e.Winner == v) continue;
            if (Prune(e, v, r, nowUnix)) n++;
        }
        return n;
    }

    /// <summary>변형 1 개 즉시 회수(중복 차단 · 재시도 · 감사 기록).</summary>
    public bool Prune(PostPublishSplitTestingEngine e, SplitVariant v, PruneReason reason, long nowUnix)
    {
        var t = e.Track(v);
        if (t.Pruned) { DuplicateBlocks++; return false; }
        var status = 0; var attempts = 0;
        var mode = Mode == PruneAction.None ? PruneAction.Private : Mode;
        while (attempts < MaxAttempts)
        {
            attempts++;
            try { status = Gateway.Apply(t.ContentId, t.Channel, v, mode); }
            catch (Exception ex) { status = 599; PromptFlowStudio.Program.Log("PostPublishSplitTest.Prune", ex); }
            if (status is >= 200 and < 300) break;
            if (status != 429 && status < 500) break;   // 4xx(429 제외)는 재시도 무의미
            Retries++;
        }
        var ok = status is >= 200 and < 300;
        if (ok) { t.Pruned = true; Pruned++; } else Failures++;
        ref var a = ref _audit[_head];
        a.Seq = ++_seq; a.TUnix = nowUnix; a.Views = t.Last.Views;
        a.ContentId = t.ContentId; a.Channel = t.Channel; a.Status = status; a.Attempts = attempts;
        a.Variant = v; a.Action = mode; a.Reason = reason;
        a.ViewsPerHour = t.ViewsPerHour(nowUnix); a.Completion = t.Last.Completion; a.Ok = ok;
        _head = (_head + 1) % AuditCap;
        if (_count < AuditCap) _count++;
        return ok;
    }

    public void ClearAudit() { Array.Clear(_audit); _head = _count = 0; }
}
