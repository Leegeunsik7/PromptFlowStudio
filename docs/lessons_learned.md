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
## 8. Pluto 자가 진단 감사 (2026-11-10)
- drawtext `text='…'` 는 FFmpeg 3단계 파싱(그래프 따옴표 → 옵션 `av_get_token` → drawtext 확장): `%`는 `\\%`, `\`는 `\\\\`, `'`는 `'\\\''`(따옴표 닫고 재개) — 1단계(`\%`)만 하면 "Stray %"로 필터 실패 → 오버레이 전부 생략 폴백. 검증은 `PlutoHardeningAuditTests` 파서 포팅 시뮬레이터.
- JSON 저장소 `lock{스냅샷}` → `await gate{기록}` 순서는 동시 기록 시 오래된 스냅샷이 나중에 써지는 Lost-Update. 게이트 안에서 최신 상태 직렬화(`PlutoJsonStore`). 적재 시 일시 잠금을 "빈 파일"로 오인하면 다음 저장이 누적 학습을 덮어씀 → Unreadable 세션은 쓰기 금지.
- 벽시계 단일 최대치(`MaxIngestUs < 2ms`) 단언은 병렬 GC 정지 1회로 깨짐 → N회 중 최선(min) 실측으로 SLA, 최대치는 계측 여부만.
## 9. 비동기 하드웨어 MFT 해제 후 사용 → testhost 0xc0000005 (Step 13 확정)
- 증상: 빌드 직후 전체 실행에서 testhost 가 4~12초 내 `ntdll.dll` 0xc0000005(오프셋 0x1a12d)로 종료(간헐). WER 덤프 2건 스택 동일: `ntdll!RtlpWaitOnCriticalSection ← nvEncMFTH264x ← RTWorkQ!CSerialWorkQueue…OnWorkItemAsyncCallback`.
- 원인: v17.75 하드웨어 H.264 프로브가 비동기 MFT 에 프레임을 넣고 곧바로 최종 `Release` — 드라이버 직렬 작업 큐에 남은 작업 항목이 이미 파괴된 객체의 임계 구역에 진입(부하 시 GPU 기동 지연으로 창이 넓어짐).
- 규칙: 비동기 MFT 는 `DRAIN`(METransformDrainComplete 대기) → `FLUSH` → `END_STREAMING` → `IMFShutdown::Shutdown` 후에만 해제하고 최종 `Release` 는 유예 큐(`Mf.DeferRelease` · 5초)로. 모든 MFT 호출은 인스턴스 게이트(`MfTransform.Enter/Exit`)로 `Dispose` 와 직렬화. `MFShutdown` 은 호출하지 않는다(MFStartup 1회 · MTA 는 `CoIncrementMTAUsage` 로 고정).
- 진단: `Get-WinEvent Application Id=1000` → `%LOCALAPPDATA%\CrashDumps\testhost.exe.<pid>.dmp` → `cdb -z <dmp> -c ".ecxr; kn 14; q"`. 회귀 `PlutoContextualBayesianAndDriftTests.MediaFoundation_ParallelInit…`.
## 10. 부하 민감 타이밍 단언 · 캐시 자가 치유 · 회로 차단 (Step 14)
- 자식 프로세스 기동 예산을 벽시계(`Stopwatch`)로 재면 전체 스위트 부하에 비례해 늘어난다(유휴 1.7초 → 부하 13.6초 · `SingleFileBinaryProcessBootstrap_Check` 간헐 실패). 성능 예산은 `Process.TotalProcessorTime`(스케줄링 대기 제외)로, 벽시계는 교착 판정(넉넉한 상한)에만.
- 디스크 캐시(컷 체크포인트)는 '쓸 때 검증'만으로는 부족 — 재사용 직전에 컨테이너 무결성 재검증, 손상본은 격리(`quarantine\` 최근 5개) 후 미스 처리. 정리는 TTL(기록 시각) + LRU(적중 시각 · 개수 · 총 용량)이며 테스트 시계는 실제 시각보다 미래로 잡아 OS/백신의 접근 시각 갱신이 순서에 끼어들지 못하게 한다.
- 컷 단위 재시도는 프로바이더 전체 장애를 흡수하지 못한다(3컷 × 3부작 × 3회 = 27회 호출). 작업 단위 일시 실패 3연속 → 회로 차단(30초 → 2배 · 상한 5분 · 탐침 1건). 인증/검증 오류는 '응답 중'의 증거이므로 차단 사유가 아니다. 차단 메시지는 재시도 분류기(`CutRetryPolicy.IsTransientMessage`)에 잡히지 않는 문구로.
- 예측 감사는 관측이 기준선·기대치를 갱신하기 **전**에 스냅샷(`ForecastExpectancy`)을 떠야 한다 — 갱신 후에 뜨면 예측이 정답을 이미 본 상태라 보정 오차가 과소 집계된다.
