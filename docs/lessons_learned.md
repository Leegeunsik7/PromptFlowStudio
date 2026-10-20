# Lessons Learned (요약 레지스트리)
> 정착된 지침은 `rules_zero_alloc.md`·`rules_testing.md`로 승격됨. 여기엔 증상·원인·위치만.

## 1. GC.TryStartNoGCRegion 함정 → rules_zero_alloc §ZeroGcRegionScope
- 증상: 핫패스마다 걸자 프레임마다 블로킹 GC(gen2 +1) → 60FPS 역효과.
- 원인: 진입 시 블로킹 GC 선행 · 프로세스 전역(중첩 시 `InvalidOperationException`) · 타 스레드 할당이 예산 소모 → 영역 조용히 종료, 이후 `EndNoGCRegion`도 예외.
- 잔여 규칙: `EndNoGCRegion` 전 `GCSettings.LatencyMode == NoGCRegion` 확인.

## 2. async void / 콜백 예외 → 프로세스 종료 → rules_zero_alloc §수명·예외 격리
- 사례: `OperationsCanaryHealthChecker` 타이머, `VisionContextAnalyzer` 구독자, Blazor `new Timer(async _ => …)`(→ `SafeDebouncer`로 교체, v13.0.1).
- 파기 후 잔류 틱이 반환된 풀 버퍼 재대여 → Dispose는 진행 중 틱 대기 후 반환, 파기 후 Tick 무동작.

## 3. UDP SIO_UDP_CONNRESET → rules_zero_alloc §수명·예외 격리
- 증상: 상대 부재 시 ICMP로 `WSAECONNRESET(10054)` → 탐지 루프 break(영구 사망).
- 위치: `EdgeP2PTaskRelay.cs`, 회귀 `WanPipelineTests`.

## 4. 카나리 프로브의 감사 링버퍼 잠식 (v14.0.1) → rules_zero_alloc §상한 컬렉션
- 증상: 10초 주기 카나리 Resolve가 32건 감사 이력을 약 5분 만에 전부 밀어내 사용자 시크릿 접근 기록 소실.
- 조치: `SecureSecretVault.CanaryKeyName` 분기 → `CanaryResolved` 전용 카운터. 회귀 `SecureSecretVaultCanaryIsolationTests`·`LongRunResourceHygieneTests`.

## 5. 기타
- 로그 홍수: 동일 오류 60초 1건 가드 + 생략 건수 표기(1MB 상한 초기화 시 최초 원인 소실 주의).
- 비밀 저장: `UnauthorizedAccessException`은 `IOException` 계열 아님 → 별도 catch 누락 시 호출측(서명·카나리)까지 전파.
- 메시 라우터 `SeedDemoMesh(9)`는 코드 링크로 `studio↔edge-04` 직결 → 재라우팅 검증 목적지는 중계가 필요한 `edge-02` 사용.

## 6. 장기 부하 하네스 (v14.2.0)
- 10k 동시 부하에서도 Gen2 0·잔류 < 2MB — 단, `ArrayPool` 8MB 첫 대여(LOH)를 워밍업 밖에 두면 기준선이 오염되므로 반드시 워밍업에서 선대여.
- 느린 VLM을 `Task.Delay` 페이크로 흉내내면 CPU 부하에 따라 요청/드롭 비율이 흔들림 → TCS 게이트형 페이크로만 결정적 단언 가능.

## 7. 가변 VLM 샘플링 (v14.3.0)
- 런타임 주기 변경은 `Timer.Change`를 `_gate` 안에서 `_timer` null 확인 후 호출 — Stop(락 안 null 교체 → 락 밖 Dispose)과 경합해도 파기된 타이머 Change(ODE) 없음.
- 백분위 창(64)은 `stackalloc float[64]` 복사 후 `Span.Sort` → 할당 0; float EWMA는 `SingleToInt32Bits`+`CompareExchange` 루프로 락 없이 갱신.
- (v14.3.1) 전체 스위트에서 장기부하 테스트 Gen2 단언 간헐 실패(~5%): 메시 라우터 ~200MB 할당 승격 → **백그라운드** Gen2. 프레임 정지 판정은 블로킹 GC 인덱스 불변으로(→ rules_testing 승격).
- (v15.0.0) 메시 ~200MB 원인: Send당 Dijkstra 사전 3개·List·LINQ·정규화 문자열·`new HMACSHA512`·hex 문자열·보간 LastAction. `Route` 0B 코어로 해소. 마지막 잔여(~100B/회)는 멱등 `HashSet` 성장 리사이즈 → 용량 선할당으로 0B.

## 8. HttpListener WebSocket 정지 교착 (v17.28.0 확정)
- 증상: 전체 테스트 testhost 무CPU 정체(간헐) — blame-hang 덤프상 `LiveSessionSyncBridge.Stop()` → `CloseAsync`(CloseOutputAsyncCore → Abort)와 취소된 `ReceiveAsync`(Abort) · keepalive 타이머가 `WebSocketBase` 내부 락을 교차 대기.
- 규칙: 수신 루프 취소와 동시에 소켓을 닫는 정지 경로는 `CloseAsync` 금지 → `Abort()`만. 정상 종료(원격 Close 수신 후)에서만 `CloseAsync`.
- 진단: `dotnet test --blame-hang --blame-hang-timeout 8m` → `dotnet-dump analyze <dmp> -c "clrstack -all"`.
