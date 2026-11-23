namespace PromptFlowStudio.Tests;

/// <summary>Step 35 — 프로세스 전역 환경 변수(<see cref="Environment.SetEnvironmentVariable(string, string?)"/>)를 바꾸는 테스트.
///   컬렉션 병렬(<c>xunit.runner.json</c> parallelizeTestCollections=true)에서 다른 클래스가 같은 변수를 읽는 순간과 겹치지 않도록
///   병렬 구간이 끝난 뒤 단독 실행한다(ProcessGcMode · TimingSensitivePerformance 와 동일 규칙).</summary>
[CollectionDefinition("ProcessEnvironment", DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection { }
