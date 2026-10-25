using PromptFlowStudio.Services.Storyboard;

namespace PromptFlowStudio.Services.Ui;

/// <summary>
/// [v18.2.0] 스토리보드 샷 시퀀스 스냅샷 — 키프레임 · 대본은 시퀀서 버퍼를 <see cref="ReadOnlyMemory{T}"/> 로 공유(복사 0).
///   <see cref="Revision"/> = 캡처 시점 시퀀서 번호 — 이후 재분할로 버퍼가 덮이면 <see cref="IsLive"/> 가 false.
/// </summary>
public readonly struct StoryboardShortsSequence
{
    public readonly ReadOnlyMemory<StoryboardKeyframe> Keyframes;
    public readonly ReadOnlyMemory<char> Script;
    public readonly int TotalMs, Revision;
    public readonly ulong Fingerprint;
    public readonly long Sequence;
    public readonly StoryboardKeyframeSequencer? Owner;

    public StoryboardShortsSequence(ReadOnlyMemory<StoryboardKeyframe> keyframes, ReadOnlyMemory<char> script, int totalMs, int revision,
                                    ulong fingerprint, long sequence, StoryboardKeyframeSequencer? owner)
    {
        Keyframes = keyframes; Script = script; TotalMs = totalMs; Revision = revision;
        Fingerprint = fingerprint; Sequence = sequence; Owner = owner;
    }

    public bool IsEmpty => Sequence == 0;
    /// <summary>공유 버퍼가 아직 캡처 시점 시퀀스인지(소유자 없는 외부 버퍼는 항상 true).</summary>
    public bool IsLive => !Keyframes.IsEmpty && (Owner is null || Owner.Revision == Revision);
    /// <summary>현재 내용의 지문 — 캡처 시 <see cref="Fingerprint"/> 와 같으면 단절 0.</summary>
    public ulong ComputeFingerprint() => StoryboardKeyframeSequencer.Hash(Keyframes.Span, Script.Span);
    /// <summary>① 절삭 길이 — 시퀀스 총 길이를 팩토리 클립 범위(3~60 s)로 클램프.</summary>
    public int ClipLengthMs => Math.Clamp(TotalMs, ShortsFactoryWorkspaceContext.MinClipMs, ShortsFactoryWorkspaceContext.MaxClipMs);
    /// <summary>① 리프레임 중심 — 첫 샷 9:16 프레이밍 X(‰).</summary>
    public int FirstCenterXPermille => Keyframes.IsEmpty ? 500 : Keyframes.Span[0].CenterXPermille;
}

/// <summary>
/// [PromptFlow Studio v18.2.0] 🎬 스토리보드 → 숏폼 팩토리 시퀀스 원클릭 인계 브리지.
///   ▸ <see cref="Push"/>: 시퀀서의 샷 키프레임 · 대본을 슬라이스 스냅샷으로 대기 슬롯에 기록(0 B · 빈 시퀀스 거부).
///   ▸ <see cref="TryConsume"/>: 숏폼 팩토리 모달 마운트 시 대기분 소비 → <see cref="Current"/>(재마운트에도 유지).
///   ▸ <see cref="OnHandoff"/>: 인계 통지(구독자 예외 격리) — 레이아웃이 팩토리 ① 탭을 열고, 이미 열린 팩토리는 즉시 재수화.
/// </summary>
public sealed class StoryboardToShortsFactoryBridge
{
    /// <summary>숏폼 팩토리 진입 탭 — ① 절삭 & 리프레임(타임코드 · 대본 입력창 · ② 자막 탭과 공유).</summary>
    public const int FactoryTrimTab = 0;

    private readonly object _gate = new();
    private StoryboardShortsSequence _pending, _current;
    private long _seq, _handoffs, _consumed, _rejected;

    public event Action? OnHandoff;

    public bool HasPending { get { lock (_gate) return !_pending.IsEmpty; } }
    public bool HasCurrent { get { lock (_gate) return !_current.IsEmpty; } }
    public StoryboardShortsSequence Current { get { lock (_gate) return _current; } }
    public long Handoffs => Interlocked.Read(ref _handoffs);
    public long Consumed => Interlocked.Read(ref _consumed);
    public long Rejected => Interlocked.Read(ref _rejected);

    /// <summary>샷이 1 개 이상인 시퀀스만 인계(대기 중인 이전 시퀀스는 덮어씀). 성공 시 통지.</summary>
    public bool Push(StoryboardKeyframeSequencer seq)
    {
        if (seq.Count == 0) { Interlocked.Increment(ref _rejected); return false; }
        lock (_gate)
        {
            _pending = new StoryboardShortsSequence(seq.KeyframeMemory, seq.ScriptMemory, seq.TotalMs, seq.Revision, seq.Fingerprint, ++_seq, seq);
            _handoffs++;
        }
        try { OnHandoff?.Invoke(); }
        catch (Exception) { /* 구독자 예외 격리 — 인계는 이미 기록 */ }
        return true;
    }

    /// <summary>대기 시퀀스 소비 → <see cref="Current"/> 승격(낡은 시퀀스는 폐기 · false).</summary>
    public bool TryConsume(out StoryboardShortsSequence sequence)
    {
        lock (_gate)
        {
            sequence = _pending;
            if (sequence.IsEmpty) return false;
            _pending = default;
            if (!sequence.IsLive) { _rejected++; sequence = default; return false; }
            _current = sequence;
            _consumed++;
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate) { _pending = default; _current = default; }
    }
}
