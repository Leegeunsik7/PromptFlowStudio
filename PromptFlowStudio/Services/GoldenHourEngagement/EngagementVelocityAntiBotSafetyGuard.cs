namespace PromptFlowStudio.Services.GoldenHourEngagement;

public enum VelocityDecision : byte { Granted, Jitter, CoolDown }

/// <summary>
/// [PromptFlow Studio v17.95.0] 🛡️ 인게이지먼트 속도 제어 &amp; 봇 탐지 방어 세이프티 가드.
///   ▸ 답글 1 건 허가마다 다음 허가 시각을 <see cref="JitterMinSec"/>~<see cref="JitterMaxSec"/>(15~45 s) xorshift 의사 랜덤 지터만큼 뒤로 미룸 — 기계적 등간격 연속 발행 방지.
///   ▸ 최근 3600 s 슬라이딩 창 발행 수가 <see cref="HourlyCap"/>(기본 30) 에 도달하면 쿨다운 — 창의 가장 오래된 발행이 빠질 때까지 대기.
///   ▸ 판정 경로는 고정 링(<see cref="RingCap"/>) 순회뿐 · 힙 할당 0.
/// </summary>
public sealed class EngagementVelocityAntiBotSafetyGuard
{
    public const int JitterMinSec = 15, JitterMaxSec = 45, WindowSec = 3600, RingCap = 64, DefaultHourlyCap = 30, MinHourlyCap = 5;

    private readonly double[] _sent = new double[RingCap];
    private int _head, _count, _cap = DefaultHourlyCap;
    private uint _rng;
    private readonly uint _seed;
    private bool _cooling;

    public EngagementVelocityAntiBotSafetyGuard(uint seed = 0x9E3779B9u)
    {
        _seed = seed == 0 ? 0x9E3779B9u : seed;
        _rng = _seed;
    }

    public int HourlyCap { get => _cap; set => _cap = Math.Clamp(value, MinHourlyCap, RingCap); }
    public double NextAllowedUnix { get; private set; } = double.NegativeInfinity;
    public double LastJitterSec { get; private set; }
    public double MinJitterSeen { get; private set; } = double.MaxValue;
    public double MaxJitterSeen { get; private set; }
    public long Granted { get; private set; }
    public long JitterDeferrals { get; private set; }
    public long CoolDownDeferrals { get; private set; }
    public long CoolDowns { get; private set; }
    public bool CoolingDown => _cooling;

    public static string DecisionName(VelocityDecision d) => d switch
    {
        VelocityDecision.Granted => "허가",
        VelocityDecision.Jitter => "지터 대기",
        _ => "쿨다운",
    };

    /// <summary>최근 1 시간 창 발행 수.</summary>
    public int CountInWindow(double nowUnix)
    {
        var n = 0;
        for (var i = 0; i < _count; i++) if (nowUnix - _sent[i] < WindowSec) n++;
        return n;
    }

    public float HourlyRatio(double nowUnix) => (float)CountInWindow(nowUnix) / _cap;

    /// <summary>발행 허가 판정 — 허가 시 발행 기록 + 다음 지터 예약. <paramref name="waitSec"/> = 대기 필요 시간.</summary>
    public VelocityDecision TryAcquire(double nowUnix, out double waitSec)
    {
        var inWindow = 0;
        var oldest = double.MaxValue;
        for (var i = 0; i < _count; i++)
        {
            var t = _sent[i];
            if (nowUnix - t >= WindowSec) continue;
            inWindow++;
            if (t < oldest) oldest = t;
        }
        if (inWindow >= _cap)
        {
            if (!_cooling) { _cooling = true; CoolDowns++; }
            CoolDownDeferrals++;
            waitSec = Math.Max(0, oldest + WindowSec - nowUnix);
            return VelocityDecision.CoolDown;
        }
        _cooling = false;
        if (nowUnix < NextAllowedUnix)
        {
            JitterDeferrals++;
            waitSec = NextAllowedUnix - nowUnix;
            return VelocityDecision.Jitter;
        }
        _sent[_head] = nowUnix;
        _head = (_head + 1) % RingCap;
        if (_count < RingCap) _count++;
        Granted++;
        var j = NextJitterSec();
        LastJitterSec = j;
        if (j < MinJitterSeen) MinJitterSeen = j;
        if (j > MaxJitterSeen) MaxJitterSeen = j;
        NextAllowedUnix = nowUnix + j;
        waitSec = 0;
        return VelocityDecision.Granted;
    }

    /// <summary>xorshift32 → [15, 45] s 균등 지터(0.1 s 해상도).</summary>
    public double NextJitterSec()
    {
        var x = _rng;
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;
        _rng = x;
        const int Steps = (JitterMaxSec - JitterMinSec) * 10 + 1;
        return JitterMinSec + (x % Steps) / 10.0;
    }

    public void Reset()
    {
        Array.Clear(_sent);
        _head = _count = 0;
        _rng = _seed;
        _cooling = false;
        NextAllowedUnix = double.NegativeInfinity;
        LastJitterSec = MaxJitterSeen = 0;
        MinJitterSeen = double.MaxValue;
        Granted = JitterDeferrals = CoolDownDeferrals = CoolDowns = 0;
    }
}
