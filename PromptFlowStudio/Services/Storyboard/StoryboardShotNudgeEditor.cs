namespace PromptFlowStudio.Services.Storyboard;

/// <summary>[v18.2.5] 샷 편집 결과 — 적용 · 인덱스 오류 · 타임코드 범위 밖 · 최소 길이 위반 · 용량 초과 · 변화 없음.</summary>
public enum ShotEditStatus : byte { None, Applied, InvalidIndex, OutOfRange, TooShort, Capacity, NoChange }

/// <summary>
/// [PromptFlow Studio v18.2.5] ✂ 무할당 샷 스플릿/머지 · 경계 넛지 편집기 — <see cref="StoryboardKeyframeSequencer"/> 키프레임 고정 배열을 제자리 편집한다.
///   ▸ <see cref="SplitShot"/>: 타임코드에서 샷 1 개 → 2 개(대본 슬라이스는 가장 가까운 공백에서 양분 · 9:16 크롭 프레이밍은 양쪽 승계).
///   ▸ <see cref="MergeWithNext"/>: 인접 2 샷 → 1 개(대본 오프셋 · 재생 구간 합집합 · 앞 샷 프레이밍 유지).
///   ▸ <see cref="NudgeBoundary"/>: 샷 끝 경계를 100 ms 격자로 이동 — 다음 샷 시작이 함께 움직여 구간 연속성 유지(양쪽 최소 길이 보장).
///   검증은 전부 지역 값으로 끝낸 뒤 한 번에 기록(실패 시 시퀀서 불변) · 확정 시 Revision/지문/크롭 트랙 동시 갱신(0 B).
/// </summary>
public sealed class StoryboardShotNudgeEditor
{
    public const int NudgeStepMs = 100;
    /// <summary>편집 후 샷 1 개 최소 길이(ms).</summary>
    public const int MinShotMs = 300;

    private readonly StoryboardKeyframeSequencer _seq;

    public StoryboardShotNudgeEditor(StoryboardKeyframeSequencer sequencer) => _seq = sequencer ?? throw new ArgumentNullException(nameof(sequencer));

    public StoryboardKeyframeSequencer Sequencer => _seq;
    public ShotEditStatus LastStatus { get; private set; }
    /// <summary>마지막 넛지에 실제 적용된 이동량(ms · 격자 스냅 후).</summary>
    public int LastNudgeMs { get; private set; }
    public long Edits { get; private set; }
    public long Rejected { get; private set; }

    /// <summary><paramref name="splitTime"/>(시퀀스 절대 시각)에서 샷 분할 — 양쪽이 <see cref="MinShotMs"/> 이상이어야 한다.</summary>
    public ShotEditStatus SplitShot(int shotIndex, TimeSpan splitTime)
    {
        var n = _seq.Count;
        if ((uint)shotIndex >= (uint)n) return Done(ShotEditStatus.InvalidIndex);
        if (n >= StoryboardKeyframeSequencer.MaxShots) return Done(ShotEditStatus.Capacity);
        var keys = _seq.EditBuffer;
        var a = keys[shotIndex];
        var ms = splitTime.Ticks / TimeSpan.TicksPerMillisecond;
        if (ms <= a.StartMs || ms >= a.EndMs) return Done(ShotEditStatus.OutOfRange);
        var at = (int)ms;
        if (at - a.StartMs < MinShotMs || a.EndMs - at < MinShotMs) return Done(ShotEditStatus.TooShort);

        var cut = TextCut(_seq.Script, a.TextStart, a.TextLength, (long)(at - a.StartMs) * a.TextLength / a.DurationMs);
        var b = a;
        b.StartMs = at;
        b.DurationMs = a.EndMs - at;
        a.DurationMs = at - a.StartMs;
        var aEnd = cut;
        while (aEnd > a.TextStart && char.IsWhiteSpace(_seq.Script[aEnd - 1])) aEnd--;
        var bStart = cut;
        var end = a.TextStart + a.TextLength;
        while (bStart < end && char.IsWhiteSpace(_seq.Script[bStart])) bStart++;
        b.TextStart = bStart;
        b.TextLength = end - bStart;
        a.TextLength = aEnd - a.TextStart;

        keys.Slice(shotIndex + 1, n - shotIndex - 1).CopyTo(keys[(shotIndex + 2)..]);
        keys[shotIndex] = a;
        keys[shotIndex + 1] = b;
        _seq.Commit(n + 1);
        return Done(ShotEditStatus.Applied);
    }

    /// <summary>샷 <paramref name="shotIndex"/> 와 다음 샷 결합 — 재생 구간 [앞 시작, 뒤 끝) · 대본 오프셋 합집합.</summary>
    public ShotEditStatus MergeWithNext(int shotIndex)
    {
        var n = _seq.Count;
        if ((uint)shotIndex >= (uint)(n - 1)) return Done(ShotEditStatus.InvalidIndex);
        var keys = _seq.EditBuffer;
        var a = keys[shotIndex];
        ref readonly var b = ref keys[shotIndex + 1];
        var end = Math.Max(a.EndMs, b.EndMs);
        if (a.TextLength == 0) { a.TextStart = b.TextStart; a.TextLength = b.TextLength; }
        else if (b.TextLength > 0)
        {
            var ts = Math.Min(a.TextStart, b.TextStart);
            a.TextLength = Math.Max(a.TextStart + a.TextLength, b.TextStart + b.TextLength) - ts;
            a.TextStart = ts;
        }
        a.DurationMs = end - a.StartMs;
        keys[shotIndex] = a;
        keys.Slice(shotIndex + 2, n - shotIndex - 2).CopyTo(keys[(shotIndex + 1)..]);
        keys[n - 1] = default;
        _seq.Commit(n - 1);
        return Done(ShotEditStatus.Applied);
    }

    /// <summary>
    /// 샷 <paramref name="shotIndex"/> 끝 경계를 <paramref name="deltaMilliseconds"/>(100 ms 격자로 0 방향 절삭) 만큼 이동.
    ///   다음 샷이 있으면 그 시작이 함께 이동(합계 길이 불변) · 마지막 샷은 끝만 이동. 최소 길이에 걸리면 격자 단위로 줄여 적용.
    /// </summary>
    public ShotEditStatus NudgeBoundary(int shotIndex, int deltaMilliseconds)
    {
        LastNudgeMs = 0;
        var n = _seq.Count;
        if ((uint)shotIndex >= (uint)n) return Done(ShotEditStatus.InvalidIndex);
        var d = deltaMilliseconds / NudgeStepMs * NudgeStepMs;
        if (d == 0) return Done(ShotEditStatus.NoChange);
        var keys = _seq.EditBuffer;
        var a = keys[shotIndex];
        var hasNext = shotIndex + 1 < n;
        // 앞 샷 ≥ MinShotMs · 다음 샷 ≥ MinShotMs 를 만족하는 이동 한계(격자 정렬)
        var minD = MinShotMs - a.DurationMs;
        var maxD = hasNext ? keys[shotIndex + 1].DurationMs - MinShotMs : int.MaxValue;
        if (d < minD) d = -(-minD / NudgeStepMs * NudgeStepMs);
        if (d > maxD) d = maxD / NudgeStepMs * NudgeStepMs;
        if (d < minD || d > maxD || d == 0) return Done(ShotEditStatus.TooShort);

        a.DurationMs += d;
        keys[shotIndex] = a;
        if (hasNext)
        {
            ref var b = ref keys[shotIndex + 1];
            b.StartMs += d;
            b.DurationMs -= d;
        }
        LastNudgeMs = d;
        _seq.Commit(n);
        return Done(ShotEditStatus.Applied);
    }

    /// <summary>대본 슬라이스 분할점 — 비례 위치에서 가장 가까운 공백(없으면 비례 위치 · 서로게이트 쌍 보존).</summary>
    private static int TextCut(ReadOnlySpan<char> script, int start, int length, long rel)
    {
        if (length <= 1) return start + length;
        var target = start + (int)Math.Clamp(rel, 1, length - 1);
        var end = start + length;
        for (var r = 0; r < length; r++)
        {
            var lo = target - r;
            if (lo > start && char.IsWhiteSpace(script[lo])) return lo;
            var hi = target + r;
            if (hi < end - 1 && char.IsWhiteSpace(script[hi])) return hi;
            if (lo <= start && hi >= end - 1) break;
        }
        return char.IsLowSurrogate(script[target]) ? target + 1 : target;
    }

    private ShotEditStatus Done(ShotEditStatus s)
    {
        LastStatus = s;
        if (s == ShotEditStatus.Applied) Edits++;
        else Rejected++;
        return s;
    }
}
