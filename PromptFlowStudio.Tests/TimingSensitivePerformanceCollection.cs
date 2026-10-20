namespace PromptFlowStudio.Tests;

/// <summary>v17.64.0 벽시계 프레임 예산(16.7 ms · 60 fps) 측정 테스트 — 일반 단위 테스트와 병렬 실행하지 않는다(CPU 경합 → 간헐 실패 차단).</summary>
[CollectionDefinition("TimingSensitivePerformance", DisableParallelization = true)]
public sealed class TimingSensitivePerformanceCollection { }
