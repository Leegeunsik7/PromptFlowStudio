namespace PromptFlowStudio.Tests;

/// <summary>v18.0.7 벽시계 프레임 예산 CI 헤드룸 - 실 Stopwatch 계측은 유지하고 판정 상한만 30 FPS 방어선(33.3 ms)으로 정규화.
///   단일 코어 부하(MaxCpuCount=1 · 타 프로세스 선점) 지터가 60 FPS(16.7 ms) 경계를 넘나드는 플래키를 제거하되,
///   2 배 이상 성능 회귀(중앙값 33.3 ms 초과)는 여전히 실측으로 잡는다. 워밍업/최선 창(최대 3~5 창) 패턴과 병용.</summary>
internal static class CiFrameBudget
{
    public const double Ms = 33.3;
    public const float MsF = 33.3f;
}
