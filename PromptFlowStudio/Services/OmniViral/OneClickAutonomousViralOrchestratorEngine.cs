using System.Threading.Channels;
using PromptFlowStudio.Services.ViralExport;

namespace PromptFlowStudio.Services.OmniViral;

/// <summary>v17.90.0 7 단계 바이럴 사이클(①~⑦) — 인덱스가 곧 DAG 노드 번호.</summary>
public enum ViralStage : byte { Clip, Score, Caption, Variant, Schedule, Article, Vault }

/// <summary>노드 상태 — 대기/진행/완료/스킵(캐시 재사용)/실패/중단(승인 대기).</summary>
public enum StageState : byte { Pending, Running, Done, Skipped, Failed, Halted }

/// <summary>단계 실패 원인.</summary>
public enum StageFault : byte { None, LowScore, TokenExpired, Exception }

/// <summary>파이프라인 전체 상태.</summary>
public enum PipelineRunState : byte { Idle, Running, AwaitingApproval, Completed, Aborted }

/// <summary>논블로킹 진행 이벤트(값 타입 · 채널 무할당 전달).</summary>
public readonly record struct StageEvent(ViralStage Stage, StageState State, byte Percent, StageFault Fault, long Seq);

/// <summary>
/// [PromptFlow Studio v17.90.0] 단일 인메모리 파이프라인 컨텍스트 — 7 단계가 모두 이 객체의 사전 할당 배열/필드만 읽고 쓴다(단계 간 복사 · 할당 0).
/// </summary>
public sealed class ViralPipelineContext
{
    public const int MaxSeconds = 3600, MinSeconds = 120, MaxWords = 512, Platforms = 4, StageCount = 7, DubLanguages = 3, GopSeconds = 2;
    public const int ClipLeadSec = 6, ClipTailSec = 24;   // 훅 우선 편집: 정점 −6 s / +24 s(GOP 정렬)
    public const long PartBytes = 8L * 1024 * 1024;

    public static readonly string[] PlatformNames = { "YouTube Shorts", "Instagram Reels", "TikTok", "X" };
    public static readonly int[] PlatformMaxSec = { 60, 90, 60, 140 };
    public static readonly int[] PlatformKbps = { 8000, 6000, 5000, 4000 };
    public static readonly int[] StaggerMinutes = { 0, 15, 30, 45 };

    public readonly float[] Excitement = new float[MaxSeconds];   // 1 s 해상도 흥분도(채팅 가속 · 후원 · RMS 복합)
    public int SourceSeconds;
    public bool HighlightSource;
    public uint Seed;
    public ulong InputFingerprint;

    // ① 킬러클립
    public int ClipStartSec, ClipEndSec, PeakSec;
    public float ClipPeak;
    // ② 스코어(v17.85 4 축)
    public float Hook, Emotion, Visual, Loop, Total;
    public ViralGrade Grade;
    // ③ 자막/더빙
    public int Words, DubTracks;
    public readonly int[] WordStartMs = new int[MaxWords];
    // ④ 변형 렌더
    public readonly long[] VariantBytes = new long[Platforms];
    public readonly int[] VariantSec = new int[Platforms];
    // ⑤ OAuth2 시차 예약
    public readonly long[] SlotUnixMs = new long[Platforms];
    public long NowUnixMs, TokenExpiresUnixMs;
    // ⑥ 아티클
    public int ArticleChars, Chapters;
    // ⑦ 볼트
    public long VaultBytes;
    public int VaultParts;

    // 단계 부기 — 산출물 지문은 체크포인트(직전 완료 단계 보존 · 중복 렌더 차단)
    public readonly StageState[] States = new StageState[StageCount];
    public readonly byte[] Progress = new byte[StageCount];
    public readonly ulong[] Artifact = new ulong[StageCount];
    public readonly ulong[] ArtifactInput = new ulong[StageCount];
    public int DoneMask, CacheMask;

    public int ClipSeconds => ClipEndSec - ClipStartSec;
    public bool TokenValid(long skewMs) => NowUnixMs + skewMs < TokenExpiresUnixMs;
    public void RefreshToken(long nowUnixMs, long ttlMs = 3_600_000) { NowUnixMs = nowUnixMs; TokenExpiresUnixMs = nowUnixMs + ttlMs; }

    /// <summary>소스 적재(비핫패스) — 흥분도 곡선 합성 · 입력 지문 갱신. 하이라이트 소스는 σ 10 s 정점 0.95, 평범한 소스는 완만한 0.5 굴곡.</summary>
    public void Load(int seconds, uint seed, bool highlight)
    {
        SourceSeconds = Math.Clamp(seconds, MinSeconds, MaxSeconds);
        Seed = seed == 0 ? 0x9E3779B9u : seed;
        HighlightSource = highlight;
        var s = Seed;
        var peak = SourceSeconds * 62 / 100;
        float amp = highlight ? 0.65f : 0.22f, sigma = highlight ? 10f : 40f, floor = highlight ? 0.30f : 0.28f;
        for (var t = 0; t < SourceSeconds; t++)
        {
            s ^= s << 13; s ^= s >> 17; s ^= s << 5;
            var noise = ((s & 0xFFFF) / 65535f - 0.5f) * 0.06f;
            var d = (t - peak) / sigma;
            Excitement[t] = Math.Clamp(floor + amp * MathF.Exp(-0.5f * d * d) + noise, 0f, 1f);
        }
        Array.Clear(Excitement, SourceSeconds, MaxSeconds - SourceSeconds);
        var h = 0xCBF29CE484222325UL;
        h = Mix(h, SourceSeconds); h = Mix(h, Seed); h = Mix(h, highlight ? 1 : 0);
        InputFingerprint = h;
    }

    /// <summary>산출물 · 상태 전체 소거(체크포인트 포함).</summary>
    public void ClearArtifacts()
    {
        Array.Clear(States); Array.Clear(Progress); Array.Clear(Artifact); Array.Clear(ArtifactInput);
        Array.Clear(VariantBytes); Array.Clear(VariantSec); Array.Clear(SlotUnixMs);
        DoneMask = CacheMask = 0;
        ClipStartSec = ClipEndSec = PeakSec = 0; ClipPeak = 0;
        Hook = Emotion = Visual = Loop = Total = 0; Grade = ViralGrade.C;
        Words = DubTracks = ArticleChars = Chapters = VaultParts = 0;
        VaultBytes = 0;
    }

    public static ulong Mix(ulong h, long v)
    {
        h ^= (ulong)v;
        return h * 0x100000001B3UL;
    }
}

/// <summary>
/// [PromptFlow Studio v17.90.0] 👑 원클릭 자율 바이럴 DAG 오케스트레이션 엔진.
///   ▸ DAG: ①클립 → ②스코어 → ③자막/더빙 → ④변형 렌더 → ⑤OAuth2 시차 예약, ①→⑥아티클(독립 분기), ④+⑥ → ⑦볼트.
///   ▸ <see cref="Step"/> 1 회 = 현재 노드 1 슬라이스(10 %) 실행 — 핫패스는 컨텍스트 사전 할당 버퍼만 사용(LINQ · 박싱 · 문자열 0).
///   ▸ 진행/상태는 <see cref="Events"/>(Bounded · DropOldest · 단일 리더) 로 논블로킹 브로드캐스트 — 생산자는 대기하지 않는다.
///   ▸ 노드 완료/실패 시 <see cref="PipelineSafetyInterlockCircuitBreakerGuard"/> 판정 → 중단 노드의 후속만 차단, 독립 분기는 계속 진행.
///   ▸ 재실행 시 입력 지문이 같은 완료 노드는 「스킵(캐시)」 — 중복 렌더 0.
/// </summary>
public sealed class OneClickAutonomousViralOrchestratorEngine
{
    public const int StageCount = ViralPipelineContext.StageCount, SlicesPerStage = 10;
    public const long TokenSkewMs = 60_000;

    /// <summary>노드별 선행 비트마스크(DAG 간선).</summary>
    private static readonly int[] Deps = { 0, 1 << 0, 1 << 1, 1 << 2, 1 << 3, 1 << 0, (1 << 3) | (1 << 5) };

    public static readonly string[] StageNames = { "킬러클립 절삭", "바이럴 스코어링", "키네틱 자막/더빙", "플랫폼 변형 렌더", "OAuth2 시차 예약", "VOD 아티클 생성", "클라우드 볼트 적재" };
    public static readonly string[] StageIcons = { "✂️", "📊", "🔤", "🎞️", "🔐", "📝", "💾" };
    public static readonly string[] StageSources = { "v17.84", "v17.85", "v17.78·v17.82", "v17.81", "v17.79", "v17.88", "v17.87" };

    private readonly Channel<StageEvent> _events = Channel.CreateBounded<StageEvent>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false,
    });

    private int _current = -1, _slice, _scanBest;
    private float _scanMax, _sumB;
    private long _seq;

    public OneClickAutonomousViralOrchestratorEngine(ViralPipelineContext? context = null, PipelineSafetyInterlockCircuitBreakerGuard? guard = null)
    {
        Context = context ?? new ViralPipelineContext();
        Guard = guard ?? new PipelineSafetyInterlockCircuitBreakerGuard();
    }

    public ViralPipelineContext Context { get; }
    public PipelineSafetyInterlockCircuitBreakerGuard Guard { get; }
    public ChannelReader<StageEvent> Events => _events.Reader;
    public PipelineRunState RunState { get; private set; }
    public int CurrentStage => _current;
    public long Published => _seq;
    public long Runs { get; private set; }
    public long Completions { get; private set; }
    public int ReusedStages { get; private set; }        // 누적 캐시 스킵(중복 렌더 차단)
    public int CheckpointReuses { get; private set; }    // 승인 재개 시 재실행하지 않은 완료 노드 수
    public string? LastError { get; private set; }

    /// <summary>테스트/장애 주입 — 지정 노드 1 회 예외.</summary>
    public int InjectFaultStage { get; set; } = -1;

    public int CompletedCount
    {
        get { var n = 0; for (var s = 0; s < StageCount; s++) if (Context.States[s] is StageState.Done or StageState.Skipped) n++; return n; }
    }

    // ══════════════════════ 제어(비핫패스) ══════════════════════

    /// <summary>원클릭 시작 — 완료 산출물은 캐시 후보로 보존(<paramref name="reuseArtifacts"/>). 차단기 개방/진행 중이면 거부.</summary>
    public bool Start(bool reuseArtifacts = true)
    {
        if (RunState is PipelineRunState.Running or PipelineRunState.AwaitingApproval) return false;
        if (!Guard.AllowsStart(Context.NowUnixMs)) return false;
        var c = Context;
        c.CacheMask = reuseArtifacts ? c.DoneMask : 0;
        c.DoneMask = 0;
        Array.Clear(c.States);
        Array.Clear(c.Progress);
        _current = -1;
        LastError = null;
        Guard.Arm();
        Runs++;
        RunState = PipelineRunState.Running;
        return true;
    }

    /// <summary>승인 후 재개 — 저점(LowScore) 노드는 산출물 그대로 완료 처리(재계산 없음), 토큰/예외 노드는 해당 노드만 재실행.</summary>
    public bool ApproveAndResume()
    {
        if (RunState != PipelineRunState.AwaitingApproval) return false;
        var s = (int)Guard.HaltedStage;
        var reason = Guard.Reason;
        if (!Guard.Approve()) return false;
        var c = Context;
        if (reason == HaltReason.LowScore)
        {
            c.States[s] = StageState.Done;
            c.DoneMask |= 1 << s;
            Publish(s, StageState.Done, 100, StageFault.None);
        }
        else
        {
            c.States[s] = StageState.Pending;
            c.Progress[s] = 0;
        }
        for (var k = 0; k < StageCount; k++) if ((c.DoneMask & (1 << k)) != 0) CheckpointReuses++;
        RunState = PipelineRunState.Running;
        return true;
    }

    /// <summary>사용자 거부 — 완료 산출물은 보존한 채 종료.</summary>
    public void Abort()
    {
        if (RunState is not (PipelineRunState.Running or PipelineRunState.AwaitingApproval)) return;
        Guard.Reject();
        if (_current >= 0) { Context.States[_current] = StageState.Failed; _current = -1; }
        RunState = PipelineRunState.Aborted;
    }

    public void Reset()
    {
        Context.ClearArtifacts();
        Guard.Reset();
        _current = -1;
        RunState = PipelineRunState.Idle;
        ReusedStages = CheckpointReuses = 0;
        Runs = Completions = 0;
        LastError = null;
        while (_events.Reader.TryRead(out _)) { }
    }

    /// <summary>동기 일괄 실행(테스트 · 헤드리스) — 종료 또는 승인 대기까지 Step 반복.</summary>
    public PipelineRunState RunToBoundary()
    {
        while (Step()) { }
        return RunState;
    }

    // ══════════════════════ 핫패스 ══════════════════════

    /// <summary>1 슬라이스 진행 — 더 진행할 작업이 있으면 true.</summary>
    public bool Step()
    {
        if (RunState != PipelineRunState.Running) return false;
        var c = Context;
        if (_current < 0)
        {
            var next = PickReady();
            if (next < 0) { Settle(); return false; }
            var input = StageInput(next);
            if ((c.CacheMask & (1 << next)) != 0 && c.ArtifactInput[next] == input)
            {
                c.States[next] = StageState.Skipped;
                c.Progress[next] = 100;
                c.DoneMask |= 1 << next;
                ReusedStages++;
                Publish(next, StageState.Skipped, 100, StageFault.None);
                return true;
            }
            _current = next; _slice = 0;
            c.States[next] = StageState.Running;
            c.Progress[next] = 0;
            Publish(next, StageState.Running, 0, StageFault.None);
        }

        var st = _current;
        StageFault fault;
        try
        {
            if (InjectFaultStage == st) { InjectFaultStage = -1; throw new InvalidOperationException("주입된 단계 장애"); }
            fault = Work(st, _slice);
        }
        catch (Exception ex)
        {
            fault = StageFault.Exception;
            LastError = ex.Message;
        }
        if (fault != StageFault.None) { Fail(st, fault); return RunState == PipelineRunState.Running; }

        _slice++;
        c.Progress[st] = (byte)(_slice * 100 / SlicesPerStage);
        if (_slice < SlicesPerStage) { Publish(st, StageState.Running, c.Progress[st], StageFault.None); return true; }

        c.Artifact[st] = Fingerprint(st);
        c.ArtifactInput[st] = StageInput(st);
        c.CacheMask &= ~(1 << st);
        _current = -1;
        Guard.NoteSuccess((ViralStage)st);
        var halt = Guard.Inspect((ViralStage)st, StageFault.None, c);
        if (halt != HaltReason.None)
        {
            c.States[st] = StageState.Halted;   // 산출물은 보존, 후속 노드만 차단
            Publish(st, StageState.Halted, 100, StageFault.LowScore);
            return true;
        }
        c.States[st] = StageState.Done;
        c.DoneMask |= 1 << st;
        Publish(st, StageState.Done, 100, StageFault.None);
        return true;
    }

    private void Fail(int st, StageFault fault)
    {
        var c = Context;
        _current = -1;
        c.CacheMask &= ~(1 << st);
        var halt = Guard.Inspect((ViralStage)st, fault, c);
        if (halt == HaltReason.BreakerOpen)
        {
            c.States[st] = StageState.Failed;
            Publish(st, StageState.Failed, c.Progress[st], fault);
            RunState = PipelineRunState.Aborted;
            return;
        }
        c.States[st] = StageState.Halted;
        Publish(st, StageState.Halted, c.Progress[st], fault);
    }

    /// <summary>준비 노드 = 대기 상태 · 선행 전부 완료 · 낮은 번호 우선(결정적 위상 순서).</summary>
    private int PickReady()
    {
        var c = Context;
        for (var s = 0; s < StageCount; s++)
            if (c.States[s] == StageState.Pending && (Deps[s] & ~c.DoneMask) == 0) return s;
        return -1;
    }

    private void Settle()
    {
        var c = Context;
        for (var s = 0; s < StageCount; s++)
            if (c.States[s] == StageState.Halted) { Guard.Checkpoint(c); RunState = PipelineRunState.AwaitingApproval; return; }
        for (var s = 0; s < StageCount; s++)
            if (c.States[s] is not (StageState.Done or StageState.Skipped)) { RunState = PipelineRunState.Aborted; return; }
        RunState = PipelineRunState.Completed;
        Completions++;
    }

    private void Publish(int stage, StageState state, byte pct, StageFault fault) =>
        _events.Writer.TryWrite(new StageEvent((ViralStage)stage, state, pct, fault, ++_seq));

    /// <summary>노드 입력 지문 = 소스 지문 ⊕ 선행 노드 산출물 지문(상류가 같은 결과면 하류 캐시 유효).</summary>
    private ulong StageInput(int s)
    {
        var c = Context;
        var h = ViralPipelineContext.Mix(c.InputFingerprint, s + 1);
        var d = Deps[s];
        for (var k = 0; k < StageCount; k++) if ((d & (1 << k)) != 0) h = ViralPipelineContext.Mix(h, (long)c.Artifact[k]);
        return h;
    }

    private ulong Fingerprint(int s)
    {
        var c = Context;
        var h = ViralPipelineContext.Mix(0xCBF29CE484222325UL, s);
        switch ((ViralStage)s)
        {
            case ViralStage.Clip: h = ViralPipelineContext.Mix(h, c.ClipStartSec); h = ViralPipelineContext.Mix(h, c.ClipEndSec); break;
            case ViralStage.Score: h = ViralPipelineContext.Mix(h, (long)(c.Total * 1000)); break;
            case ViralStage.Caption: h = ViralPipelineContext.Mix(h, c.Words); for (var i = 0; i < c.Words; i++) h = ViralPipelineContext.Mix(h, c.WordStartMs[i]); break;
            case ViralStage.Variant: for (var p = 0; p < ViralPipelineContext.Platforms; p++) h = ViralPipelineContext.Mix(h, c.VariantBytes[p]); break;
            case ViralStage.Schedule: for (var p = 0; p < ViralPipelineContext.Platforms; p++) h = ViralPipelineContext.Mix(h, c.SlotUnixMs[p]); break;
            case ViralStage.Article: h = ViralPipelineContext.Mix(h, c.ArticleChars); h = ViralPipelineContext.Mix(h, c.Chapters); break;
            default: h = ViralPipelineContext.Mix(h, c.VaultBytes); h = ViralPipelineContext.Mix(h, c.VaultParts); break;
        }
        return h;
    }

    // ══════════════════════ 단계 작업(슬라이스) ══════════════════════

    private StageFault Work(int s, int slice)
    {
        var c = Context;
        switch ((ViralStage)s)
        {
            case ViralStage.Clip: WorkClip(c, slice); break;
            case ViralStage.Score: WorkScore(c, slice); break;
            case ViralStage.Caption: WorkCaption(c, slice); break;
            case ViralStage.Variant: WorkVariant(c, slice); break;
            case ViralStage.Schedule:
                if (slice == 0 && !c.TokenValid(TokenSkewMs)) return StageFault.TokenExpired;
                WorkSchedule(c, slice);
                break;
            case ViralStage.Article: WorkArticle(c, slice); break;
            default: WorkVault(c, slice); break;
        }
        return StageFault.None;
    }

    /// <summary>① 흥분도 정점 탐색(슬라이스 분할 스캔) → 정점 −6 s/+24 s · 2 s GOP 정렬 절삭.</summary>
    private void WorkClip(ViralPipelineContext c, int slice)
    {
        if (slice == 0) { _scanMax = -1f; _scanBest = 0; }
        int n = c.SourceSeconds, a = slice * n / SlicesPerStage, b = (slice + 1) * n / SlicesPerStage;
        var e = c.Excitement;
        for (var t = a; t < b; t++) if (e[t] > _scanMax) { _scanMax = e[t]; _scanBest = t; }
        if (slice != SlicesPerStage - 1) return;
        c.PeakSec = _scanBest;
        c.ClipPeak = _scanMax;
        var start = Math.Max(0, _scanBest - ViralPipelineContext.ClipLeadSec);
        start -= start % ViralPipelineContext.GopSeconds;
        var end = Math.Min(n, start + ViralPipelineContext.ClipLeadSec + ViralPipelineContext.ClipTailSec);
        c.ClipStartSec = start;
        c.ClipEndSec = end;
    }

    /// <summary>② v17.85 4 축(훅 · 감정 · 시각 환기 · 루프) → 가중 합 0~100 · S/A/B/C.</summary>
    private void WorkScore(ViralPipelineContext c, int slice)
    {
        var e = c.Excitement;
        int a = c.ClipStartSec, b = c.ClipEndSec;
        switch (slice)
        {
            case 2:
            {
                // 훅: 첫 10 s 상위 3 초 평균
                float m0 = 0, m1 = 0, m2 = 0;
                for (var t = a; t < Math.Min(b, a + 10); t++)
                {
                    var v = e[t];
                    if (v > m0) { m2 = m1; m1 = m0; m0 = v; } else if (v > m1) { m2 = m1; m1 = v; } else if (v > m2) m2 = v;
                }
                c.Hook = Math.Clamp((m0 + m1 + m2) / 3f / 0.9f, 0f, 1f) * 100f;
                break;
            }
            case 4: c.Emotion = Math.Clamp(c.ClipPeak, 0f, 1f) * 100f; break;
            case 6:
            {
                float lo = 1f, hi = 0f;
                for (var t = a; t < b; t++) { var v = e[t]; if (v < lo) lo = v; if (v > hi) hi = v; }
                c.Visual = Math.Clamp((hi - lo) / 0.7f, 0f, 1f) * 100f;
                break;
            }
            case 8: c.Loop = b > a ? Math.Clamp(1f - MathF.Abs(e[b - 1] - e[a]), 0f, 1f) * 100f : 0f; break;
            case 9:
                c.Total = 0.35f * c.Hook + 0.30f * c.Emotion + 0.15f * c.Visual + 0.20f * c.Loop;
                c.Grade = AiViralPotentialScoringEngine.GradeOf(c.Total);
                break;
        }
    }

    /// <summary>③ 단어 단위 키네틱 자막 타이밍(초당 2.6 단어 · 흥분도 가속) + 더빙 트랙 3.</summary>
    private void WorkCaption(ViralPipelineContext c, int slice)
    {
        if (slice == 0)
        {
            c.Words = Math.Min(ViralPipelineContext.MaxWords, c.ClipSeconds * 26 / 10);
        }
        int n = c.Words, a = slice * n / SlicesPerStage, b = (slice + 1) * n / SlicesPerStage;
        var clipMs = c.ClipSeconds * 1000f;
        for (var i = a; i < b; i++)
        {
            var sec = c.ClipStartSec + i * c.ClipSeconds / Math.Max(1, n);
            var accel = 1f - 0.15f * c.Excitement[sec];   // 흥분 구간은 단어 간격 압축
            c.WordStartMs[i] = (int)(i * clipMs / Math.Max(1, n) * accel);
        }
        if (slice == SlicesPerStage - 1) c.DubTracks = ViralPipelineContext.DubLanguages;
    }

    /// <summary>④ 4 플랫폼 변형(9:16 · 길이 상한 · 비트레이트) — 플랫폼당 2 슬라이스, 마지막 2 슬라이스는 검증.</summary>
    private void WorkVariant(ViralPipelineContext c, int slice)
    {
        if (slice >= 8) return;
        var p = slice >> 1;
        var sec = Math.Min(c.ClipSeconds, ViralPipelineContext.PlatformMaxSec[p]);
        if ((slice & 1) == 0)
        {
            // 인코드 근사: 프레임 복잡도 적분(흥분도 = 모션량)
            _sumB = 0;
            for (var t = c.ClipStartSec; t < c.ClipStartSec + sec; t++) _sumB += 0.6f + 0.8f * c.Excitement[t];
            return;
        }
        var kbps = ViralPipelineContext.PlatformKbps[p] * (_sumB / Math.Max(1, sec)) + 128;
        c.VariantSec[p] = sec;
        c.VariantBytes[p] = (long)(kbps * 1000 / 8 * sec);
    }

    /// <summary>⑤ 다음 정시 + 0/15/30/45 분 시차 예약(토큰 검사는 슬라이스 0).</summary>
    private static void WorkSchedule(ViralPipelineContext c, int slice)
    {
        if (slice != SlicesPerStage - 1) return;
        const long Hour = 3_600_000;
        var b = (c.NowUnixMs + 600_000 + Hour - 1) / Hour * Hour;
        for (var p = 0; p < ViralPipelineContext.Platforms; p++) c.SlotUnixMs[p] = b + ViralPipelineContext.StaggerMinutes[p] * 60_000L;
    }

    /// <summary>⑥ 원본 VOD → 챕터(5 분 단위) · 아티클 분량(요약 밀도 2.2 자/초).</summary>
    private static void WorkArticle(ViralPipelineContext c, int slice)
    {
        if (slice != SlicesPerStage - 1) return;
        c.Chapters = Math.Clamp(c.SourceSeconds / 300, 1, 30);
        c.ArticleChars = c.SourceSeconds * 22 / 10;
    }

    /// <summary>⑦ 변형본 + 아티클 → 8 MB 멀티파트 적재 계획.</summary>
    private static void WorkVault(ViralPipelineContext c, int slice)
    {
        if (slice != SlicesPerStage - 1) return;
        long bytes = c.ArticleChars * 3L;
        for (var p = 0; p < ViralPipelineContext.Platforms; p++) bytes += c.VariantBytes[p];
        c.VaultBytes = bytes;
        c.VaultParts = (int)((bytes + ViralPipelineContext.PartBytes - 1) / ViralPipelineContext.PartBytes);
    }
}
