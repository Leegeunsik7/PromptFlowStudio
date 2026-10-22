namespace PromptFlowStudio.Services.LiveRestream;

/// <summary>
/// [PromptFlow Studio v17.83.0] 네트워크 적응형 가변 비트레이트(ABR) 세이프티 가드(무할당).
///   ▸ 100 ms 주기 샘플: 송신 큐 적체(ms 환산 = 바이트·8 / 현재 kbps) · RTT 상승분(최소 RTT 기준선 대비) → 정체 지수 0~2.
///   ▸ 감쇄: 지수 &gt; 0.7 이 3 샘플(300 ms) 연속 → 한 단계 하향(6 → 4 → 2.5 Mbps · 단계 간 1 s 홀드).
///     복구: 지수 &lt; 0.25 가 5 s 연속 → 한 단계 상향.
///   ▸ IDR 정렬: 목표 티어는 예약만 하고 키프레임 요청 플래그를 올림 → 인코더가 다음 프레임을 IDR 로 강제 → <see cref="OnFrame"/>(idr) 에서만 커밋.
///     비-IDR 프레임 중 전환 0 건(<see cref="UnalignedSwitches"/>) → 참조 프레임 불일치로 인한 픽셀 깨짐 0.
/// </summary>
public sealed class AdaptiveBitrateNetworkCongestionGuard
{
    public const long SampleUs = 100_000, HoldUs = 1_000_000;
    public const int DownSamples = 3, UpSamples = 50, History = 120;
    public const float DownIndex = 0.7f, UpIndex = 0.25f, QueueBudgetMs = 500f, RttBudgetMs = 300f;
    public static readonly int[] TierKbps = { 6000, 4000, 2500 };

    private readonly float[] _histIdx = new float[History];
    private readonly short[] _histKbps = new short[History];
    private long _lastSample = long.MinValue, _lastSwitch = long.MinValue / 2;
    private int _over, _under, _head, _filled;
    private double _baseRtt = double.MaxValue;

    public bool Enabled { get; set; } = true;
    public int Tier { get; private set; }
    public int TargetTier { get; private set; }
    public int Kbps => TierKbps[Tier];
    public bool KeyframeRequested { get; private set; }
    public float CongestionIndex { get; private set; }
    public double QueueMs { get; private set; }
    public double RttMs { get; private set; }
    public double BaselineRttMs => _baseRtt == double.MaxValue ? 0 : _baseRtt;
    public long Samples { get; private set; }
    public long Downshifts { get; private set; }
    public long Upshifts { get; private set; }
    public long Switches => Downshifts + Upshifts;
    public long UnalignedSwitches { get; private set; }
    public long DeferredFrames { get; private set; }
    public int HistoryCount => _filled;
    public bool SwitchPending => TargetTier != Tier;

    public static string TierName(int t) => t switch { 0 => "Tier 1 · 6 Mbps", 1 => "Tier 2 · 4 Mbps", _ => "Tier 3 · 2.5 Mbps" };

    /// <summary>히스토리(i = 0 가장 오래됨).</summary>
    public float HistIndex(int i) => _histIdx[(_head - _filled + i + History) % History];
    public int HistKbps(int i) => _histKbps[(_head - _filled + i + History) % History];

    public void Reset()
    {
        Tier = TargetTier = 0; KeyframeRequested = false; CongestionIndex = 0; QueueMs = RttMs = 0;
        _over = _under = _head = _filled = 0; _lastSample = long.MinValue; _lastSwitch = long.MinValue / 2; _baseRtt = double.MaxValue;
        Samples = Downshifts = Upshifts = UnalignedSwitches = DeferredFrames = 0;
    }

    /// <summary>주기 샘플(100 ms 미만 경과 시 무시). 반환 = 샘플 처리 여부.</summary>
    public bool Sample(long pendingBytes, double rttMs, long nowUs)
    {
        if (_lastSample != long.MinValue && nowUs - _lastSample < SampleUs) return false;
        _lastSample = nowUs;
        Samples++;
        RttMs = rttMs;
        if (rttMs > 0) _baseRtt = Math.Min(_baseRtt == double.MaxValue ? rttMs : _baseRtt * 1.0005, rttMs); // 느린 상향 망각
        QueueMs = pendingBytes * 8.0 / Math.Max(1, TierKbps[Tier]);
        var rise = Math.Max(0, rttMs - BaselineRttMs);
        CongestionIndex = (float)Math.Clamp(Math.Max(QueueMs / QueueBudgetMs, rise / RttBudgetMs), 0, 2);
        _histIdx[_head] = CongestionIndex; _histKbps[_head] = (short)TierKbps[Tier];
        _head = (_head + 1) % History; if (_filled < History) _filled++;
        if (!Enabled) { _over = _under = 0; return true; }

        if (CongestionIndex > DownIndex) { _over++; _under = 0; }
        else if (CongestionIndex < UpIndex) { _under++; _over = 0; }
        else _over = _under = 0;

        if (SwitchPending) return true;
        if (_over >= DownSamples && Tier < TierKbps.Length - 1 && nowUs - _lastSwitch >= HoldUs)
        {
            TargetTier = Tier + 1; KeyframeRequested = true; _over = 0;
        }
        else if (_under >= UpSamples && Tier > 0)
        {
            TargetTier = Tier - 1; KeyframeRequested = true; _under = 0;
        }
        return true;
    }

    /// <summary>인코더 프레임 직전 호출 — IDR 에서만 예약 티어 커밋. 반환 = 이번 프레임에서 전환 발생.</summary>
    public bool OnFrame(bool idr, long nowUs)
    {
        if (!SwitchPending) return false;
        if (!idr) { DeferredFrames++; return false; }
        if (TargetTier > Tier) Downshifts++; else Upshifts++;
        Tier = TargetTier; KeyframeRequested = false; _lastSwitch = nowUs;
        return true;
    }

    /// <summary>방어적 진단 — 외부 경로가 비-IDR 에서 티어를 바꾸려 하면 계수(정상 경로에서는 항상 0).</summary>
    public void ForceUnaligned() => UnalignedSwitches++;
}
