namespace PromptFlowStudio.Services.OmniViral;

/// <summary>중단 사유.</summary>
public enum HaltReason : byte { None, LowScore, TokenExpired, StageFault, BreakerOpen }

/// <summary>인터락 상태 — 무장/승인 대기/차단기 개방(쿨다운).</summary>
public enum GuardState : byte { Armed, AwaitingApproval, Tripped }

/// <summary>
/// [PromptFlow Studio v17.90.0] 🛡 파이프라인 인터락 서킷브레이커 세이프티 가드.
///   ▸ ② 스코어 &lt; 70(C 등급) · ⑤ OAuth2 토큰 만료 · 단계 예외 → 해당 노드 Halt + 사용자 승인 대기.
///   ▸ Halt 시점 완료 노드 마스크 + 산출물 지문 다이제스트를 체크포인트로 보존 — 승인 재개 시 완료 노드는 재실행하지 않는다.
///   ▸ 연속 예외 <see cref="TripThreshold"/> 회 → 차단기 개방(<see cref="CooldownMs"/> 동안 신규 시작 거부).
/// </summary>
public sealed class PipelineSafetyInterlockCircuitBreakerGuard
{
    public const float DefaultMinScore = 70f;

    private ViralStage _faultStage = (ViralStage)255;

    public float MinScore { get; set; } = DefaultMinScore;
    public int TripThreshold { get; set; } = 3;
    public long CooldownMs { get; set; } = 30_000;
    public bool Enabled { get; set; } = true;

    public GuardState State { get; private set; }
    public HaltReason Reason { get; private set; }
    public ViralStage HaltedStage { get; private set; }
    public int ConsecutiveFaults { get; private set; }
    public long Halts { get; private set; }
    public long Approvals { get; private set; }
    public long Rejections { get; private set; }
    public long Trips { get; private set; }
    public long TrippedAtMs { get; private set; }
    public int CheckpointMask { get; private set; }
    public ulong CheckpointDigest { get; private set; }

    /// <summary>노드 판정(핫패스 · 무할당) — 성공 완료는 <paramref name="fault"/> = None.</summary>
    public HaltReason Inspect(ViralStage stage, StageFault fault, ViralPipelineContext ctx)
    {
        HaltReason r;
        if (fault == StageFault.Exception)
        {
            if (stage != _faultStage) { _faultStage = stage; ConsecutiveFaults = 0; }   // 같은 노드 연속 재시도 실패만 누적
            ConsecutiveFaults++;
            if (ConsecutiveFaults >= TripThreshold)
            {
                State = GuardState.Tripped;
                Reason = HaltReason.BreakerOpen;
                HaltedStage = stage;
                TrippedAtMs = ctx.NowUnixMs;
                Trips++;
                Checkpoint(ctx);
                return HaltReason.BreakerOpen;
            }
            r = HaltReason.StageFault;
        }
        else if (fault == StageFault.TokenExpired) r = HaltReason.TokenExpired;   // 토큰 만료는 비활성 가드여도 진행 불가
        else if (Enabled && stage == ViralStage.Score && ctx.Total < MinScore) r = HaltReason.LowScore;
        else return HaltReason.None;

        State = GuardState.AwaitingApproval;
        Reason = r;
        HaltedStage = stage;
        Halts++;
        Checkpoint(ctx);
        return r;
    }

    /// <summary>노드 성공 — 실패 누적 중이던 그 노드가 성공해야 카운터 해제(독립 분기 성공은 무관).</summary>
    public void NoteSuccess(ViralStage stage)
    {
        if (stage == _faultStage) ConsecutiveFaults = 0;
    }

    /// <summary>시작 허용 — 차단기 개방 중엔 쿨다운 경과 후에만(경과 시 반개방 → 무장).</summary>
    public bool AllowsStart(long nowMs)
    {
        if (State != GuardState.Tripped) return true;
        if (nowMs - TrippedAtMs < CooldownMs) return false;
        State = GuardState.Armed;
        Reason = HaltReason.None;
        ConsecutiveFaults = 0;
        return true;
    }

    public void Arm()
    {
        if (State == GuardState.Tripped) return;
        State = GuardState.Armed;
        Reason = HaltReason.None;
    }

    public bool Approve()
    {
        if (State != GuardState.AwaitingApproval) return false;
        Approvals++;
        State = GuardState.Armed;
        Reason = HaltReason.None;
        return true;
    }

    public void Reject()
    {
        if (State == GuardState.AwaitingApproval) Rejections++;
        if (State != GuardState.Tripped) State = GuardState.Armed;
        Reason = HaltReason.None;
    }

    /// <summary>체크포인트 무결성 — Halt 이후 완료 노드 산출물이 변조/재렌더되지 않았는지.</summary>
    public bool VerifyCheckpoint(ViralPipelineContext ctx) => Digest(ctx, CheckpointMask) == CheckpointDigest;

    /// <summary>체크포인트 봉인 — Halt 시점 + 독립 분기 정착(승인 대기 진입) 시점에 완료 노드 전체를 다시 봉인.</summary>
    public void Checkpoint(ViralPipelineContext ctx)
    {
        CheckpointMask = ctx.DoneMask;
        CheckpointDigest = Digest(ctx, ctx.DoneMask);
    }

    private static ulong Digest(ViralPipelineContext ctx, int mask)
    {
        var h = 0xCBF29CE484222325UL;
        for (var s = 0; s < ViralPipelineContext.StageCount; s++)
            if ((mask & (1 << s)) != 0) h = ViralPipelineContext.Mix(h, (long)ctx.Artifact[s]);
        return h;
    }

    public void Reset()
    {
        State = GuardState.Armed;
        Reason = HaltReason.None;
        ConsecutiveFaults = 0;
        _faultStage = (ViralStage)255;
        Halts = Approvals = Rejections = Trips = 0;
        TrippedAtMs = 0;
        CheckpointMask = 0;
        CheckpointDigest = 0;
    }

    public static string ReasonText(HaltReason r) => r switch
    {
        HaltReason.LowScore => "바이럴 스코어 70점 미만(C 등급) — 배포 전 확인 필요",
        HaltReason.TokenExpired => "OAuth2 액세스 토큰 만료 — 토큰 갱신 후 재개",
        HaltReason.StageFault => "단계 예외 — 해당 단계만 재시도",
        HaltReason.BreakerOpen => "연속 장애로 차단기 개방 — 쿨다운 후 재시작",
        _ => "정상",
    };
}
