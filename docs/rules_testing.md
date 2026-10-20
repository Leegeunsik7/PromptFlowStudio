# 테스트 규칙 (xUnit · PromptFlowStudio.Tests)

## 포트 0 (Ephemeral Port)
- 고정 포트 금지(병렬 실행·CI 충돌). TCP/UDP/HttpListener는 포트 `0`으로 바인드 후 실제 포트를 읽어 사용:
  `var p = ((IPEndPoint)sock.LocalEndPoint!).Port;`
- HttpListener는 포트 0 불가 → `TcpListener(…, 0)`로 빈 포트 확보 후 즉시 해제·재사용 시 경합 가능성 인지, 가능하면 소켓 직접 바인드.
- 루프백(`IPAddress.Loopback`)만 사용, 외부 네트워크 의존 금지.

## TaskCompletionSource 동기화
- `Thread.Sleep`/고정 `Task.Delay` 대기 금지. 이벤트·콜백은 `new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously)`로 신호.
- 대기에는 항상 상한: `await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));` (타임아웃 시 실패로 즉시 가시화).
- "발생하지 않아야 함" 검증만 짧은 고정 지연 허용(최소화).
- 전 CPU 부하에서도 통과해야 함(타이밍 여유 확보) — 부하 2회 연속 통과를 안정성 기준으로.

## GC 컬렉션 격리
- 할당/GC 카운트 단언 테스트는 `[Collection("GC")]` 등 단일 컬렉션으로 직렬화(병렬 테스트의 할당이 카운터 오염).
- `ZeroGcCounters` 인스턴스 주입으로 `Global` 공유 상태 회피, 테스트 전 `Reset()`.
- `GC.TryStartNoGCRegion`은 프로세스 전역 → 테스트에서 직접 호출 금지(`ZeroGcRegionScope.EnterCritical` 경유), 종료는 `finally`.
- 측정 전 워밍업 1회(JIT 할당 제거), 필요 시 `GC.Collect(); GC.WaitForPendingFinalizers();` 후 기준선.
- 타이머/CTS/소켓은 `Dispose`, 테스트 간 정적 상태 잔류 금지.

## 장기 운용·누수 하네스
- 프로세스 전역 지표(`GC.CollectionCount(2)`·`GC.GetTotalAllocatedBytes`·`GC.GetTotalMemory`) 단언 테스트는 `[Collection("ProcessGcMode")]`(DisableParallelization)에서만.
- 워밍업(JIT·`ArrayPool` LOH 대여·캐시 초기화) 후 기준선 → 부하 → 완전 수집 2회 후 잔류 증가량 상한(예 < 2MB)으로 누수 판정. 총 할당량은 진단 메시지용, 단언은 스레드별 `GetAllocatedBytesForCurrentThread`.
- 느린 외부 의존(VLM 등)은 `TaskCompletionSource` 게이트형 페이크로 응답 시점을 테스트가 통제 → 백프레셔 드롭 수가 결정적(`요청 + 드롭 == 틱`).
- 종료 대기는 `SpinWait.SpinUntil(cond, 상한ms)`을 `Assert.True`로 감싸 무한 대기 차단.
- Gen2 "0건" 단언은 블로킹 기준: `GC.GetGCMemoryInfo(GCKind.FullBlocking).Index`를 기준선 직후/부하 직후 비교. `CollectionCount(2)`는 백그라운드(동시) Gen2까지 세어 타 할당원(메시·암호) 승격 타이밍에 따라 플래키.
