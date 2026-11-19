namespace PromptFlowStudio.Tests;

/// <summary>v18.0.7 벽시계 프레임 예산 CI 헤드룸 - 실 Stopwatch 계측은 유지하고 판정 상한만 30 FPS 방어선(33.3 ms)으로 정규화.
///   단일 코어 부하(MaxCpuCount=1 · 타 프로세스 선점) 지터가 60 FPS(16.7 ms) 경계를 넘나드는 플래키를 제거하되,
///   2 배 이상 성능 회귀(중앙값 33.3 ms 초과)는 여전히 실측으로 잡는다. 워밍업/최선 창(최대 3~5 창) 패턴과 병용.</summary>
internal static class CiFrameBudget
{
    public const double Ms = 33.3;
    public const float MsF = 33.3f;
    /// <summary>하위 단계 예산(그림자 패스 등 · 60 FPS 기준 ms)에 적용하는 동일 헤드룸 배수 — 프레임 상한만 정규화하고 하위 예산을 그대로 두면 외부 부하에서 하위 게이트가 먼저 깨진다.</summary>
    public const double SubBudgetHeadroom = 2.0;
    /// <summary>60 프레임 측정 창에서 허용하는 레이어 예산 초과 프레임 수(10 %) — 스케줄러 선점 지터 흡수 · 실제 회귀(대부분 프레임 초과)는 그대로 검출.</summary>
    public const long WindowOverrunTolerance = 6;

    public const double MaxContentionFactor = 3.0;
    private const int ProbeMs = 40;

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
