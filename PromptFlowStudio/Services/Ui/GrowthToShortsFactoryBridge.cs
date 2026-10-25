using PromptFlowStudio.Services.CompetitorSpy;

namespace PromptFlowStudio.Services.Ui;

/// <summary>
/// [v18.0.6] 그로스 룸 기획안 스냅샷 — 대본 · 훅 가이드 · 해시태그는 전략 컨텍스트 버퍼를 <see cref="ReadOnlyMemory{T}"/> 로 공유(복사 0).
///   <see cref="Revision"/> = 캡처 시점 컨텍스트 번호 — 이후 체인 재실행으로 버퍼가 덮이면 <see cref="IsLive"/> 가 false.
/// </summary>
public readonly struct GrowthShortsPlan
{
    public readonly ReadOnlyMemory<char> Script, HookGuide, Hashtags;
    public readonly HookPattern Hook;
    public readonly int CutsPerSecMilli, Revision;
    public readonly ulong Fingerprint;
    public readonly long Sequence;
    public readonly ViralGrowthStrategyPipelineContext? Owner;

    public GrowthShortsPlan(ReadOnlyMemory<char> script, ReadOnlyMemory<char> hookGuide, ReadOnlyMemory<char> hashtags, HookPattern hook,
                            int cutsPerSecMilli, int revision, ulong fingerprint, long sequence, ViralGrowthStrategyPipelineContext? owner)
    {
        Script = script; HookGuide = hookGuide; Hashtags = hashtags; Hook = hook;
        CutsPerSecMilli = cutsPerSecMilli; Revision = revision; Fingerprint = fingerprint; Sequence = sequence; Owner = owner;
    }

    public bool IsEmpty => Sequence == 0;
    /// <summary>공유 버퍼가 아직 캡처 시점 기획안인지(소유자 없는 외부 버퍼는 항상 true).</summary>
    public bool IsLive => !Script.IsEmpty && (Owner is null || Owner.Revision == Revision);

    /// <summary>현재 내용의 지문 — 캡처 시 <see cref="Fingerprint"/> 와 같으면 단절 0.</summary>
    public ulong ComputeFingerprint() => ViralGrowthStrategyPipelineContext.Hash(Script.Span,
        ViralGrowthStrategyPipelineContext.Hash(HookGuide.Span, ViralGrowthStrategyPipelineContext.Hash(Hashtags.Span, 14695981039346656037UL)));

    /// <summary>권장 컷 빈도 → 클립 길이 기준 줌 펀치 횟수(컷 2 회당 펀치 1 · 0~8).</summary>
    public int ZoomPunchesFor(int clipMs) => Math.Clamp((int)((long)CutsPerSecMilli * clipMs / 2_000_000), 0, ShortsFactoryWorkspaceContext.MaxZoomPunches);
}

/// <summary>
/// [PromptFlow Studio v18.0.6] 🎬 바이럴 그로스 룸 → 숏폼 팩토리 원클릭 기획안 인계 브리지.
///   ▸ <see cref="Push"/>: 완성된 전략 컨텍스트를 슬라이스 스냅샷으로 대기 슬롯에 기록(0 B · 미완성/낡은 체인 거부).
///   ▸ <see cref="Hydrate"/>: 숏폼 팩토리 모달 마운트 시 대기분 소비 → <see cref="Current"/>(재마운트에도 유지).
///   ▸ <see cref="OnHandoff"/>: 인계 통지(구독자 예외 격리) — 팩토리가 이미 열려 있으면 즉시 재수화.
/// </summary>
public sealed class GrowthToShortsFactoryBridge
{
    /// <summary>숏폼 팩토리 진입 탭 — ① 절삭 & 리프레임(대본 입력창 · ② 자막 탭과 공유).</summary>
    public const int FactoryScriptTab = 0;

    private readonly object _gate = new();
    private GrowthShortsPlan _pending, _current;
    private long _seq, _handoffs, _consumed, _rejected;

    public event Action? OnHandoff;

    public bool HasPending { get { lock (_gate) return !_pending.IsEmpty; } }
    public bool HasCurrent { get { lock (_gate) return !_current.IsEmpty; } }
    public GrowthShortsPlan Current { get { lock (_gate) return _current; } }
    public long Handoffs => Interlocked.Read(ref _handoffs);
    public long Consumed => Interlocked.Read(ref _consumed);
    public long Rejected => Interlocked.Read(ref _rejected);

    /// <summary>4 단계 완성 · 공정위 린트 통과 체인만 인계(대기 중인 이전 기획안은 덮어씀). 성공 시 통지.</summary>
    public bool Push(ViralGrowthStrategyPipelineContext ctx)
    {
        if (!ctx.IsComplete) { Interlocked.Increment(ref _rejected); return false; }
        lock (_gate)
        {
            _pending = new GrowthShortsPlan(ctx.ScriptMemory, ctx.HookGuideMemory, ctx.HashtagsMemory, ctx.Hook.Hook,
                                            ctx.Hook.CutsPerSecMilli, ctx.Revision, ctx.Fingerprint, ++_seq, ctx);
            _handoffs++;
        }
        try { OnHandoff?.Invoke(); }
        catch (Exception) { /* 구독자 예외 격리 — 인계는 이미 기록 */ }
        return true;
    }

    /// <summary>대기 기획안 소비 → <see cref="Current"/> 승격(낡은 기획안은 폐기 · false).</summary>
    public bool TryConsume(out GrowthShortsPlan plan)
    {
        lock (_gate)
        {
            plan = _pending;
            if (plan.IsEmpty) return false;
            _pending = default;
            if (!plan.IsLive) { _rejected++; plan = default; return false; }
            _current = plan;
            _consumed++;
            return true;
        }
    }

    public bool Hydrate() => TryConsume(out _);

    public void Clear()
    {
        lock (_gate) { _pending = default; _current = default; }
    }
}
