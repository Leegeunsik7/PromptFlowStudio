using System.Diagnostics;

namespace PromptFlowStudio.Tests;

/// <summary>
/// v18.0.7 결정론 가상 시계(Pure BCL · TimeProvider 파생) — 테스트가 명시적으로 전진시킬 때만 시간이 흐른다.
/// 벽시계/OS 선점과 무관하므로 단일 코어 고부하 환경에서도 타이밍 단언이 1 회 실행으로 확정된다.
/// 주파수 = Stopwatch.Frequency(기존 "틱 = Stopwatch 틱" 보고 관례와 호환).
/// </summary>
internal sealed class VirtualTimeProvider : TimeProvider
{
    private long _ts = 1;

    public override long TimestampFrequency => Stopwatch.Frequency;
    public override long GetTimestamp() => Interlocked.Read(ref _ts);

    public void Advance(double ms) => Interlocked.Add(ref _ts, (long)(ms * TimestampFrequency / 1000.0));

    /// <summary>단조 전진(과거 시각 요청은 무시).</summary>
    public void AdvanceTo(long timestamp)
    {
        long cur;
        while ((cur = Interlocked.Read(ref _ts)) < timestamp && Interlocked.CompareExchange(ref _ts, timestamp, cur) != cur) { }
    }
}
