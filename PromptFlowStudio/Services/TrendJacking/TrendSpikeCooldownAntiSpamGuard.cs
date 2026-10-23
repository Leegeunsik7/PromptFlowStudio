namespace PromptFlowStudio.Services.TrendJacking;

public enum TrendInjectDecision : byte { Allowed, BlockedIrrelevant, CoolingDown }

/// <summary>
/// [PromptFlow Studio v17.96.0] 🛡️ 무관 트렌드 편승 방어 &amp; 스팸 세이프티 가드.
///   ▸ 연관도 <see cref="MinRelevance"/>(70) 미만 키워드 주입 원천 차단 — 플랫폼 'Misleading Metadata' 정책 위반 방어.
///   ▸ 동일 트렌드 태그 재사용 <see cref="CooldownSec"/>(4 시간) 회전 쿨다운 — 키워드 정규형(공백 · '#' 무시 · 대소문자 무시) FNV-1a 해시 링(<see cref="RingCap"/>).
///   ▸ 판정 · 기록 경로 힙 할당 0.
/// </summary>
public sealed class TrendSpikeCooldownAntiSpamGuard
{
    public const int MinRelevance = 70, CooldownSec = 4 * 3600, RingCap = 64;

    private readonly ulong[] _hash = new ulong[RingCap];
    private readonly double[] _usedAt = new double[RingCap];
    private int _head, _count;

    public long Allowed { get; private set; }
    public long BlockedIrrelevant { get; private set; }
    public long BlockedCooldown { get; private set; }
    public long Recorded { get; private set; }

    public static string DecisionName(TrendInjectDecision d) => d switch
    {
        TrendInjectDecision.Allowed => "허용",
        TrendInjectDecision.BlockedIrrelevant => "무관 차단",
        _ => "쿨다운",
    };

    /// <summary>주입 가능 판정(기록 없음). 무할당.</summary>
    public TrendInjectDecision Evaluate(ReadOnlySpan<char> keyword, int relevance, double nowUnix)
    {
        if (relevance < MinRelevance) { BlockedIrrelevant++; return TrendInjectDecision.BlockedIrrelevant; }
        if (RemainingCooldownSec(keyword, nowUnix) > 0) { BlockedCooldown++; return TrendInjectDecision.CoolingDown; }
        Allowed++;
        return TrendInjectDecision.Allowed;
    }

    /// <summary>실주입 확정 — 쿨다운 시작(같은 태그 기존 기록은 갱신).</summary>
    public void Record(ReadOnlySpan<char> keyword, double nowUnix)
    {
        var h = Hash(keyword);
        for (var i = 0; i < _count; i++)
            if (_hash[i] == h) { _usedAt[i] = nowUnix; Recorded++; return; }
        _hash[_head] = h;
        _usedAt[_head] = nowUnix;
        _head = (_head + 1) % RingCap;
        if (_count < RingCap) _count++;
        Recorded++;
    }

    public double RemainingCooldownSec(ReadOnlySpan<char> keyword, double nowUnix)
    {
        var h = Hash(keyword);
        for (var i = 0; i < _count; i++)
            if (_hash[i] == h) return Math.Max(0, _usedAt[i] + CooldownSec - nowUnix);
        return 0;
    }

    /// <summary>현재 쿨다운 중인 태그 수.</summary>
    public int ActiveCooldowns(double nowUnix)
    {
        var n = 0;
        for (var i = 0; i < _count; i++) if (nowUnix - _usedAt[i] < CooldownSec) n++;
        return n;
    }

    public void Reset()
    {
        Array.Clear(_hash); Array.Clear(_usedAt);
        _head = _count = 0;
        Allowed = BlockedIrrelevant = BlockedCooldown = Recorded = 0;
    }

    /// <summary>FNV-1a 64 — 공백 · '#' 무시 · 소문자 정규화.</summary>
    public static ulong Hash(ReadOnlySpan<char> s)
    {
        var h = 14695981039346656037UL;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch) || ch == '#') continue;
            h ^= char.ToLowerInvariant(ch);
            h *= 1099511628211UL;
        }
        return h;
    }
}
