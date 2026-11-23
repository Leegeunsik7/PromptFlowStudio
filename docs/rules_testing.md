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

## 병렬 실행 · 직렬 컬렉션 (Step 35)
- `xunit.runner.json` = 컬렉션 병렬(`maxParallelThreads` 4). `DisableParallelization` 컬렉션은 병렬 구간이 끝난 뒤 단독 실행 → 아래 테스트는 반드시 해당 컬렉션에:
  - 벽시계 µs/ms 예산 단언(엔진 내부 `Max*Us`·`Last*Ms` 포함) · `CiFrameBudget` 실측 → `[Collection("TimingSensitivePerformance")]`
  - GC 모드/프로세스 전역 GC 지표 → `[Collection("ProcessGcMode")]`
  - `Environment.SetEnvironmentVariable` → `[Collection("ProcessEnvironment")]`
- 벽시계 상한은 `CiFrameBudget.Scaled(n)`(상한) · `CiFrameBudget.MeetsFloor(값, 하한)`(처리율 하한) — 기준 통과 시 탐침 0회, 미달 시에만 외부 경합 배수(≤ 3)로 재판정.
- CI 워크플로는 `RunConfiguration.DisableParallelization=true` 로 계속 완전 직렬(보수적 게이트).
- 10 s 이상 테스트는 `[Trait("Speed", "Slow")]` — 빠른 내부 루프 `--filter "Speed!=Slow"`.

## 주기 스케줄러 · 타이머 (Step 36)
- 주기 백그라운드 서비스는 `TimeProvider` 주입 + 단발(one-shot) 타이머 재무장. 테스트는 테스트 내부 `ManualClock : TimeProvider`(외부 NuGet 0)로 `Advance` 시 예정 도달 타이머만 발화 → 벽시계 대기 0 · 결정적.
- 발화 완료 대기는 서비스 완료 이벤트 → `TaskCompletionSource` + `WaitAsync(상한)`. "발화하지 않아야 함"은 시계를 예정 직전까지 진행 후 요청 수 불변 단언.
- 완료 이벤트는 실행 중 플래그 해제 **뒤** 발생시켜야 구독자가 곧바로 일으킨 다음 발화가 '실행 중'으로 버려지지 않는다.

## 호스트 기동 데몬 · 킬 스위치 (Step 37)
- 킬 스위치를 읽는 부트스트래퍼는 `Func<string, string?>` 환경 읽기 함수를 주입받는다 → 테스트는 가짜 환경으로 분기 검증(프로세스 환경 무변경 · `ProcessEnvironment` 컬렉션 불필요). 기본 읽기 함수 경로는 모듈 초기화의 `PROMPTFLOW_PLUTO_DRIVE=off` 로 "둘 다 차단"만 단언.
- 기동 유예는 `Task.Delay(d, TimeProvider, ct)` — 수동 시계로 "유예 중 폐기 → 부활 없음"을 벽시계 대기 없이 검증.
- 백그라운드 이벤트 → 피드 기록은 호스트 싱글톤 1곳에서만 구독(페이지 재진입마다 구독 금지) · 중복 키로 재발행/재시작 재생 차단.

## 기동 선조정 · 블랙박스 진단 (Step 38)
- 기동 1회 작업(Drive 조정 등)은 단일 비행 서비스 1곳 — 호스트 초기화 · 데몬 · 페이지가 같은 `Task` 에 합류. 결선은 소스 가드(`PlutoStartupSyncWiring`)로 "호스트는 위임만 · 순서 보존"을 단언.
- 선행 단계 순서 검증은 대상 서비스의 동기 이벤트(예: 대기열 `Changed`) 안에서 후행 서비스 상태(감시기 `IsRunning` · 폴러 `IsAutoSyncArmed`)를 포착.
- 외부 마운트 대기 상한은 `WaitAsync(상한, TimeProvider, ct)` — 수동 시계 `ActiveTimers` 증가를 `SpinWait.SpinUntil` 로 확인한 뒤 `Advance`(타이머 생성 전 진행 경합 차단). 다른 타이머가 있는 시계는 공유하지 말 것.
- 피드 스팸 검증: "조용한 재시작"은 같은 영속 파일로 피드를 재생성해 건수 불변을 단언(영속 중복 키).

## 무할당 단언 · 원클릭 일괄 (Step 39)
- 스레드 국소 무할당 단언(`GetAllocatedBytesForCurrentThread`)은 직렬 컬렉션만으로 안정되지 않는다 — 측정값은 다른 스레드 할당과 무관하고, 전체 실행에서만 보이는 1회성 바이트(예: `TitanReleaseTests` CDC `FindCut` 4,688 B)는 계층 JIT 승격 · OSR 전이가 호출 스레드의 측정 창에 들어온 것(앞선 테스트의 JIT 부하가 승격 타이머를 밀어냄).
  → `SteadyStateAllocation.MinBytes(창)`(`ParallelExecutionGuards.cs`)로 같은 창을 최대 4회 측정해 최소값 0 을 단언. 진짜 할당은 매 창 재현되므로 회귀는 그대로 잡힌다. 창 람다는 측정 **전**에 만들어 둔다(대리자 생성 할당 제외).
  → `TitanReleaseTests` 는 계속 `[Collection("ProcessGcMode")]`(DisableParallelization) — 서명 모드 환경변수 · 프로세스 GC 지표 때문에 직렬 유지.
- 사이드카 묶음 원자성 검증: 사전 검증 실패는 같은 이름 폴더 심기, 커밋 도중 실패는 대상 파일을 `FileShare.None` 으로 연 채 실행 → 이전 내용 바이트 동일 · `*.tmp`/`*.bak` 잔류 0 단언.
- 단일 비행(같은 마스터) 검증은 `TaskCompletionSource` 게이트 번역기로 첫 실행을 붙잡고 진입 신호 후 두 번째 호출 `Busy` · 무기록을 단언 → 게이트 해제 후 잠금 누수 없음(재실행 가능)까지 확인. 클립 ID 는 테스트마다 GUID(프로세스 정적 단일 비행 키 충돌 방지).

## 전 언어 일괄 렌더 (Step 40)
- 렌더 오케스트레이터는 `PlutoLocaleRenderDelegate` 주입 → 테스트는 결정론 페이크 렌더러(스테이징 경로에 `ftyp` 서명 바이트만 기록 · FFmpeg 0). 로캘별 훅으로 예외 · 취소 · 손상 산출물 · 렌더 도중 게시 연결을 주입.
- 디스크 여유 공간은 `Func<string,long?>` 탐침 주입(실제 드라이브 무접촉) · 시계는 고정 `Func<DateTimeOffset>`.
- 산출물 폴더 잔류 검사(`*.rendering.mp4` · `*.tmp` · `*.bak`)를 할 때는 다른 서비스의 비동기 영속 파일(활동 피드 `*.json.tmp`)을 **별도 하위 폴더**에 둔다 — 같은 폴더면 피드 디바운스 저장과 경합해 간헐 실패.
- 단일 비행 공유(일괄 패키지 ↔ 일괄 렌더)는 `TaskCompletionSource` 게이트 렌더러로 첫 실행을 붙잡고 양쪽 두 번째 호출 `Busy` · 사이드카 무기록 → 해제 후 잠금 누수 없음까지 단언.
- `TitanReleaseTests` 의 원시 `GetAllocatedBytesForCurrentThread() - a0` 단언은 전체 실행에서 같은 JIT/OSR 1회성 바이트로 간헐 실패할 수 있다(Step 40 관측: `NeuralTemporalConsistencyGuard.Process` 7,528 B) → 실패 지점만 `SteadyStateAllocation.MinBytes` 로 외과 전환.

## 무할당 단언 전량 정상 상태화 · 외부 실행 재시도 · Stage 10 (Step 41)
- `TitanReleaseTests.cs` 의 원시 무할당 단언(`Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - a0)` · `Assert.True(… == 0, …)` · `var alloc = … - before`)은 전부 `SteadyStateAllocation.MinBytes(() => { 창 })` — 새 원시 단언 추가 금지.
  - 창 밖으로 새는 로컬(결과 · 누적기 `all`/`ok`/`acc`/`vsum`)은 창 **밖에서 선언**하고 창 첫 줄에서 리셋 → 재시도 창이 동일 작업을 측정.
  - 창이 상태를 전진(핸드오프 시퀀스 · 구독 통지 · 소비 카운터)시키면 `MinBytes(창, out var windows)` 후 `1 + windows` 로 단언(고정 `2` 금지).
  - 소비형/제자리 창(파이프라인 완주 · 채널 드레인 · 입력 버퍼 제자리 변환)은 `MinBytes(창, beforeRetry, out _)` — 재시도 직전 창 밖에서 재기동/원본 복원. 복원 없이는 재시도 창이 빈 작업으로 0 B 를 내는 공허한 통과.
  - `stackalloc` Span · `ref`/`out` 매개변수를 쓰는 창은 람다 캡처 불가 → `var p = SteadyStateAllocation.Start(); do { 창 } while (p.Retry()); Assert.Equal(0, p.MinBytes);`
  - 0 B 가 아닌 상수 메모리 차분(big − small) 측정과 프레임 게이트 누적(`if (f >= N) alloc += …`) · `await` 포함 창은 원시 단일 창 유지(재시도 대상 아님).
  - 멱등성 감사: `PFS_ALLOC_FORCE_ALL_WINDOWS=1` 로 해당 클래스를 돌리면 0 B 조기 반환 없이 항상 4 창 실행 → 재시도에 깨지는 후속 단언을 단독 실행에서도 드러낸다(변경 시 1회 권장).
- 실 프로세스 생성(`cmd.exe` 등) 단언은 `ProcessEnvironment` 컬렉션 + 유효 대상만 유한 재시도(3회 · 100/200 ms) — 검증(`ValidateExternalTarget`) 통과를 먼저 단언하고 무효 대상 `false` 단언에는 재시도 금지.
- 플라이휠 자가진단은 10 단계 — Stage 10 은 고유 마스터 ID(프로세스 전역 일괄 단일 비행 슬롯 충돌 방지) · 주입 렌더러/디스크 탐침/시계/오프라인 번역기 · 카탈로그는 산출물 폴더 밖.
