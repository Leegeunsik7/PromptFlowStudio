namespace PromptFlowStudio.Tests;

/// <summary>v18.0.7 벽시계 프레임 예산 CI 헤드룸 - 실 Stopwatch 계측은 유지하고 판정 상한만 30 FPS 방어선(33.3 ms)으로 정규화.
///   단일 코어 부하(MaxCpuCount=1 · 타 프로세스 선점) 지터가 60 FPS(16.7 ms) 경계를 넘나드는 플래키를 제거하되,
///   2 배 이상 성능 회귀(중앙값 33.3 ms 초과)는 여전히 실측으로 잡는다. 워밍업/최선 창(최대 3~5 창) 패턴과 병용.
///   Step 15: <see cref="Ms"/> · <see cref="MsF"/> 는 부하 인지 예산(<see cref="FrameBudget"/>) — 모든 <c>값 &lt; CiFrameBudget.Ms</c> 단언이
///   호출부 수정 없이 외부 CPU 경합에 비례해 완화되고, 유휴 머신에서는 기준 상한 그대로 엄격하다.</summary>
internal static class CiFrameBudget
{
    /// <summary>기준 상한(ms) — 유휴 머신 판정값.</summary>
    public const double BaseMs = 33.3;
    public const float BaseMsF = 33.3f;

    /// <summary>부하 인지 프레임 예산 — 비교 연산자가 '기준 미만이면 즉시 통과 · 초과일 때만 부하 계측 후 비례 완화'.</summary>
    public static FrameBudget Ms => new(BaseMs, null);
    /// <summary>float 계측값용(기준 = 33.3f 를 double 로 승격한 값 → 기존 float 비교와 동일 경계).</summary>
    public static FrameBudget MsF => new(BaseMsF, null);

    /// <summary>하위 단계 예산(그림자 패스 등 · 60 FPS 기준 ms)에 적용하는 동일 헤드룸 배수 — 프레임 상한만 정규화하고 하위 예산을 그대로 두면 외부 부하에서 하위 게이트가 먼저 깨진다.</summary>
    public const double SubBudgetHeadroom = 2.0;
    /// <summary>60 프레임 측정 창에서 허용하는 레이어 예산 초과 프레임 수(10 %) — 스케줄러 선점 지터 흡수 · 실제 회귀(대부분 프레임 초과)는 그대로 검출.</summary>
    public const long WindowOverrunTolerance = 6;

    public const double MaxContentionFactor = 3.0;
    /// <summary>유휴 판정 폭 — 원시 계수가 이 값 이하면 '경합 없음'(배수 정확히 1.0 → 기준 상한 불변). 탐침 스레드 기동 지터(수 %)가 예산을 느슨하게 만들지 않게 한다.</summary>
    public const double IdleDeadBand = 1.15;
    /// <summary>부하 계수 캐시 수명(ms) — 재시도 루프 · 프레임 단위 비교가 매번 전 코어 탐침을 돌리지 않게 한다.</summary>
    public const int FactorCacheMs = 500;
    private const int ProbeMs = 40;

    private static readonly object FactorGate = new();
    private static long _factorTick = long.MinValue;
    private static double _factor = 1.0;

    /// <summary>원시 부하 계수 → 적용 배수(순수 함수) — 비유한/유휴 폭 이하 → 정확히 1.0 · 상한 <see cref="MaxContentionFactor"/>.</summary>
    public static double NormalizeFactor(double rawFactor)
        => !double.IsFinite(rawFactor) || rawFactor <= IdleDeadBand ? 1.0 : Math.Min(rawFactor, MaxContentionFactor);

    /// <summary>예산 × 적용 배수(순수 함수) — 경합 없음 → <paramref name="baseBudget"/> 그대로.</summary>
    public static double Scale(double baseBudget, double rawFactor) => baseBudget * NormalizeFactor(rawFactor);

    /// <summary>임의 기준 예산(µs 레이어 예산 등)의 부하 인지 판정 — <c>lmed &lt; CiFrameBudget.Scaled(1_000)</c>.</summary>
    public static FrameBudget Scaled(double baseBudget) => new(baseBudget, null);

    /// <summary>Step 35 — 처리율 하한(초당 n 건 이상)의 부하 인지 판정: 기준 하한을 넘으면 탐침 없이 통과 · 미달일 때만 경합 배수만큼 하한을 낮춰 재판정.
    ///   생산자 스레드가 외부 프로세스에 선점되면 처리율이 배수에 반비례해 떨어지므로 <c>값 × 배수 &gt; 하한</c> 과 같다. 유휴(배수 1.0)에선 기준 그대로 엄격.</summary>
    public static bool MeetsFloor(double value, double floor) => value > floor || value * CurrentFactor() > floor;

    /// <summary><see cref="MeetsFloor(double, double)"/> 의 결정론 변형(부하 계수 주입).</summary>
    public static bool MeetsFloor(double value, double floor, Func<double> rawFactorProbe)
        => value > floor || value * NormalizeFactor((rawFactorProbe ?? throw new ArgumentNullException(nameof(rawFactorProbe)))()) > floor;

    /// <summary>부하 계수 공급자 주입(결정론 테스트용).</summary>
    public static FrameBudget Custom(double baseBudget, Func<double> rawFactorProbe) => new(baseBudget, rawFactorProbe ?? throw new ArgumentNullException(nameof(rawFactorProbe)));

    /// <summary>현재 적용 배수(1 ~ 3 · <see cref="FactorCacheMs"/> 캐시 · 유휴 → 정확히 1.0).</summary>
    public static double CurrentFactor()
    {
        lock (FactorGate)
        {
            var now = Environment.TickCount64;
            if (_factorTick != long.MinValue && now - _factorTick < FactorCacheMs) return _factor;
            _factor = NormalizeFactor(ContentionFactor());
            _factorTick = Environment.TickCount64;
            return _factor;
        }
    }

    /// <summary>
    /// 외부 부하 계수(1 ~ 3) — 전 논리 코어에서 <see cref="ProbeMs"/> ms 동안 스핀하고 '요청한 CPU 시간(코어 × 벽시계) ÷ 실제로 받은 프로세스 CPU 시간'을 잰다.
    /// 유휴 머신 ≈ 1.0 · 다른 프로세스(백신 · 동기화 · 타 빌드)가 코어를 선점하면 그만큼 커진다. 워커 풀 병렬 렌더의 벽시계 예산을
    /// 같은 순간의 선점 정도로 정규화해, 코드 회귀(유휴에서도 느림)는 그대로 잡고 머신 경합만 흡수한다. 계측 실패 → 1.0.
    /// </summary>
    public static double ContentionFactor()
    {
        try
        {
            var n = Math.Max(1, Environment.ProcessorCount);
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            using var go = new ManualResetEventSlim(false);
            var threads = new Thread[n];
            for (var i = 0; i < n; i++)
            {
                threads[i] = new Thread(() =>
                {
                    go.Wait();
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < ProbeMs) Thread.SpinWait(200);
                }) { IsBackground = true };
                threads[i].Start();
            }
            var cpu0 = proc.TotalProcessorTime;
            var wall = System.Diagnostics.Stopwatch.StartNew();
            go.Set();
            for (var i = 0; i < n; i++) threads[i].Join();
            wall.Stop();
            proc.Refresh();
            var got = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
            var asked = wall.Elapsed.TotalMilliseconds * n;
            return got > 0 ? Math.Clamp(asked / got, 1.0, MaxContentionFactor) : 1.0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return 1.0; }
    }
}

/// <summary>
/// ⏱️ 부하 인지 예산 — <c>값 &lt; 예산</c> 은 ① 값이 기준 미만이면 계측 없이 통과(유휴 · 정상 경로 비용 0 · 기준 그대로 엄격)
/// ② 기준 이상일 때만 외부 CPU 경합을 재서 <c>기준 × 배수</c> 로 다시 판정한다. 코드 회귀(유휴에서도 느림)는 배수 1.0 이라 그대로 실패.
/// </summary>
internal readonly struct FrameBudget
{
    private readonly Func<double>? _rawFactorProbe;

    public FrameBudget(double baseBudget, Func<double>? rawFactorProbe)
    {
        Base = baseBudget;
        _rawFactorProbe = rawFactorProbe;
    }

    /// <summary>유휴 머신 기준 상한.</summary>
    public double Base { get; }

    /// <summary>현재 부하를 반영한 상한(계측 수행).</summary>
    public double Current => Base * Factor();

    private double Factor() => _rawFactorProbe is null ? CiFrameBudget.CurrentFactor() : CiFrameBudget.NormalizeFactor(_rawFactorProbe());

    private bool Under(double value) => value < Base || value < Base * Factor();
    private bool Over(double value) => value > Base && value > Base * Factor();

    public static bool operator <(double value, FrameBudget budget) => budget.Under(value);
    public static bool operator >=(double value, FrameBudget budget) => !budget.Under(value);
    public static bool operator >(double value, FrameBudget budget) => budget.Over(value);
    public static bool operator <=(double value, FrameBudget budget) => !budget.Over(value);

    public static bool operator >(FrameBudget budget, double value) => budget.Under(value);
    public static bool operator <=(FrameBudget budget, double value) => !budget.Under(value);
    public static bool operator <(FrameBudget budget, double value) => budget.Over(value);
    public static bool operator >=(FrameBudget budget, double value) => !budget.Over(value);

    public static FrameBudget operator *(double scale, FrameBudget budget) => new(budget.Base * scale, budget._rawFactorProbe);
    public static FrameBudget operator *(FrameBudget budget, double scale) => new(budget.Base * scale, budget._rawFactorProbe);

    public override string ToString() => Base.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " ms(기준)";
}

/// <summary>
/// Step 44 — 창 단위 부하 표본. 최선 창 재시도 게이트에서 판정 시점 1 회 탐침은 '경합이 창 동안 있다가 판정 직전에 사라진' 경우
/// 배수 1.0 으로 실패한다(LiveCommerce 레이어 초과 창 · 기준 실행 실패 실측). 기준을 넘긴 창 직후에만 탐침해 최대 배수를 보관하고
/// 최종 판정은 <c>max(창 최대 배수, 판정 시점 배수)</c> — 기준 통과 창 · 유휴(배수 1.0)에서는 기준 상한 그대로 엄격, 탐침 0 회.
/// </summary>
internal sealed class WindowContention
{
    private readonly Func<double> _rawFactorProbe;

    public WindowContention() : this(CiFrameBudget.CurrentFactor) { }

    /// <summary>부하 계수 공급자 주입(결정론 테스트용).</summary>
    public WindowContention(Func<double> rawFactorProbe) => _rawFactorProbe = rawFactorProbe ?? throw new ArgumentNullException(nameof(rawFactorProbe));

    /// <summary>기준 초과 창에서 관측한 최대 적용 배수(1 ~ 3 · 표본 없음 → 1.0).</summary>
    public double PeakFactor { get; private set; } = 1.0;

    /// <summary>탐침 횟수(기준 초과 창 수).</summary>
    public int Samples { get; private set; }

    /// <summary>창 직후 호출 — 창이 기준을 통과했으면 탐침하지 않는다.</summary>
    public void Observe(bool windowMissedBase)
    {
        if (!windowMissedBase) return;
        Samples++;
        var f = CiFrameBudget.NormalizeFactor(_rawFactorProbe());
        if (f > PeakFactor) PeakFactor = f;
    }

    /// <summary>부하 인지 예산 — 기준 미만 값은 탐침 없이 통과 · 초과일 때만 <c>max(PeakFactor, 현재 배수)</c> 로 재판정.</summary>
    public FrameBudget Budget(double baseBudget) => CiFrameBudget.Custom(baseBudget, () => Math.Max(PeakFactor, CiFrameBudget.NormalizeFactor(_rawFactorProbe())));
}
