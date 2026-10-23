namespace PromptFlowStudio.Services.RetentionHeatmap;

public enum RescueVerdict : byte { Approved, NoSlumps, InsufficientUplift, OverEdited, Duplicate, DailyCap }

public enum RescueTarget : byte { None, SplitTestVariantR, MatrixSyndication, Queued }

/// <summary>리스큐 이관 기록 1 건(값 형식).</summary>
public struct RescueHandoff
{
    public int ClipId;
    public RescueTarget Target;
    public float UpliftPp, PredictedCompletion;
    public long AtUnix;
}

/// <summary>
/// [PromptFlow Studio v17.98.0] 🛟 리스큐(Rescue) 버전 재배포 세이프티 가드(<see cref="Evaluate"/> · <see cref="Commit"/> 무할당).
///   ▸ 판정: 이탈 절벽 0 = 불필요 · 예상 평균 유지율 상승 &lt; 1.5 %p = 가치 부족 · 분당 펀치 &gt; 8 또는 템포 가속 커버리지 &gt; 40 % = 과편집(시청 피로) ·
///     같은 클립 6 시간 내 재투입 = 중복 · 24 시간 이관 ≥ 3 = 일일 상한.
///   ▸ 이관 대상: v17.93 포스트-퍼블리시 A/B 테스터가 추적 중이 아니면 신규 Variant-R 슬롯 → 아니면 v17.81 다채널 매트릭스 → 둘 다 없으면 로컬 대기열.
///   ▸ 이관 원장: 고정 상한 16 링(가장 오래된 것부터 덮어씀).
/// </summary>
public sealed class RetentionRescueRepublishGuard
{
    public const float MinUpliftPp = 1.5f, MaxTempoCoverage = 0.4f;
    public const int MaxPunchPerMin = 8, DailyCap = 3, LedgerCap = 16;
    public const long DuplicateCooldownSec = 6 * 3600, DaySec = 24 * 3600;

    private readonly RescueHandoff[] _ledger = new RescueHandoff[LedgerCap];
    private int _head, _count;
    private bool _armed;   // 승인 1 회당 커밋 1 회

    public int HandoffCount => _count;
    /// <summary>이관 기록(0 = 최신).</summary>
    public ref readonly RescueHandoff Handoff(int i) => ref _ledger[(_head - 1 - i + LedgerCap * 2) % LedgerCap];
    public RescueVerdict LastVerdict { get; private set; }
    public long Evaluations { get; private set; }
    public long Approved { get; private set; }
    public long RejectedNoSlump { get; private set; }
    public long RejectedUplift { get; private set; }
    public long RejectedOverEdit { get; private set; }
    public long RejectedDuplicate { get; private set; }
    public long RejectedCap { get; private set; }
    public long ToVariantR { get; private set; }
    public long ToMatrix { get; private set; }
    public long Queued { get; private set; }
    public long Rejected => RejectedNoSlump + RejectedUplift + RejectedOverEdit + RejectedDuplicate + RejectedCap;

    public static string VerdictName(RescueVerdict v) => v switch
    {
        RescueVerdict.Approved => "승인",
        RescueVerdict.NoSlumps => "절벽 없음",
        RescueVerdict.InsufficientUplift => "상승 부족",
        RescueVerdict.OverEdited => "과편집",
        RescueVerdict.Duplicate => "중복 재투입",
        _ => "일일 상한",
    };

    public static string TargetName(RescueTarget t) => t switch
    {
        RescueTarget.SplitTestVariantR => "A/B Variant-R",
        RescueTarget.MatrixSyndication => "다채널 매트릭스",
        RescueTarget.Queued => "로컬 대기열",
        _ => "없음",
    };

    /// <summary>대체 가치 판정(무할당).</summary>
    public RescueVerdict Evaluate(int clipId, int slumps, float upliftPp, int punches, int durationMs, int tempoMs, long nowUnix)
    {
        Evaluations++;
        RescueVerdict v;
        if (slumps <= 0) { v = RescueVerdict.NoSlumps; RejectedNoSlump++; }
        else if (upliftPp < MinUpliftPp) { v = RescueVerdict.InsufficientUplift; RejectedUplift++; }
        else if (durationMs > 0 && (punches * 60_000L > (long)MaxPunchPerMin * durationMs || tempoMs > durationMs * MaxTempoCoverage)) { v = RescueVerdict.OverEdited; RejectedOverEdit++; }
        else if (RecentFor(clipId, nowUnix)) { v = RescueVerdict.Duplicate; RejectedDuplicate++; }
        else if (CountSince(nowUnix - DaySec) >= DailyCap) { v = RescueVerdict.DailyCap; RejectedCap++; }
        else { v = RescueVerdict.Approved; Approved++; }
        _armed = v == RescueVerdict.Approved;
        return LastVerdict = v;
    }

    /// <summary>이관 대상 선택 — A/B(추적 중 아님) → 매트릭스 → 대기열.</summary>
    public static RescueTarget SelectTarget(bool abAvailable, bool abBusy, bool matrixAvailable) =>
        abAvailable && !abBusy ? RescueTarget.SplitTestVariantR : matrixAvailable ? RescueTarget.MatrixSyndication : RescueTarget.Queued;

    /// <summary>이관 확정 기록(승인 직후에만 호출). 반환 = 기록 여부.</summary>
    public bool Commit(int clipId, RescueTarget target, float upliftPp, float predictedCompletion, long nowUnix)
    {
        if (!_armed || target == RescueTarget.None) return false;
        _armed = false;
        _ledger[_head] = new RescueHandoff { ClipId = clipId, Target = target, UpliftPp = upliftPp, PredictedCompletion = predictedCompletion, AtUnix = nowUnix };
        _head = (_head + 1) % LedgerCap;
        if (_count < LedgerCap) _count++;
        switch (target)
        {
            case RescueTarget.SplitTestVariantR: ToVariantR++; break;
            case RescueTarget.MatrixSyndication: ToMatrix++; break;
            default: Queued++; break;
        }
        return true;
    }

    public int CountSince(long sinceUnix)
    {
        var c = 0;
        for (var i = 0; i < _count; i++) if (Handoff(i).AtUnix > sinceUnix) c++;
        return c;
    }

    public void Reset()
    {
        Array.Clear(_ledger);
        _head = _count = 0;
        _armed = false;
        LastVerdict = RescueVerdict.Approved;
        Evaluations = Approved = RejectedNoSlump = RejectedUplift = RejectedOverEdit = RejectedDuplicate = RejectedCap = 0;
        ToVariantR = ToMatrix = Queued = 0;
    }

    private bool RecentFor(int clipId, long nowUnix)
    {
        for (var i = 0; i < _count; i++)
        {
            ref readonly var h = ref Handoff(i);
            if (h.ClipId == clipId && nowUnix - h.AtUnix < DuplicateCooldownSec) return true;
        }
        return false;
    }
}
