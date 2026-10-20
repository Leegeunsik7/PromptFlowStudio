# Zero-Alloc 규칙 (핫패스: 추론 루프·HUD 갱신·워커 틱·60FPS/10ms 주기)

## 금지
- LINQ, 박싱(값형→object/인터페이스), 문자열 보간·`+` 결합, 람다 캡처, `params` 배열, 매 틱 `new byte[]`/`new List<>`.

## Span / ReadOnlySpan<char>
- 파싱·슬라이싱은 `Substring`/`Split` 대신 `ReadOnlySpan<char>` + `IndexOf`/`Slice`. 숫자는 `int.TryParse(span, NumberStyles, InvariantCulture, out v)`.
- 소형 임시 버퍼: `Span<byte> b = stackalloc byte[N];` (N ≤ 256~1024, 루프 안 stackalloc 금지).
- `ref struct`/`Span`은 async 메서드·람다·필드에 보관 불가 → 동기 구간에서만 사용, async 경계엔 `Memory<T>`.
- 문자열 결합이 불가피하면 재사용 `StringBuilder`(Clear 후 재사용) 또는 `string.Create`.

## ArrayPool<T>
- `ArrayPool<T>.Shared.Rent(min)` → 반환 길이는 min 이상(정확 길이 가정 금지) → 항상 `try/finally`에서 `Return`.
- 비밀/민감 데이터는 `Return(arr, clearArray: true)`.
- Dispose 시 진행 중 틱 종료를 기다린 뒤 반환(use-after-return 차단). 반환 후 참조 금지.
- 장기 보유 버퍼는 인스턴스 필드로 1회 할당·재사용.

## ZeroGcRegionScope (`Services/Diagnostics/ZeroGcRegionScope.cs`)
- 기본: `using var _ = ZeroGcRegionScope.Enter("site");` — 스레드별 할당 바이트 전후 비교 가드(ref struct, 할당 0). GC 없이 할당 관측 시 위반 집계(`ZeroGcCounters.Violations`). 핫패스는 이것만 사용.
- `EnterCritical` (opt-in, 드문 짧은 버스트 전용): 참조 카운트로 첫 진입만 `TryStartNoGCRegion`. 매 프레임 사용 금지(진입 시 블로킹 GC 유발).
- `GcLatencySession.Acquire()/Release()`: 실시간 루프 가동 동안 SustainedLowLatency 유지(스트리밍 세션 권장).
- 테스트는 `ZeroGcCounters` 인스턴스를 주입해 `Global` 오염 방지.
- 위반/Inconclusive(GC 겹침) 구분: Inconclusive는 위반으로 세지 않음.

## 이벤트/구독자
- `OnChange` 등 구독자 목록은 캐시 배열 스냅샷으로 순회(무할당), 구독자 예외는 격리 후 카운터 증가.

## 백프레셔 (느린 소비자: VLM·원격 추론)
- 대기열 금지 — in-flight 1슬롯: `if (Interlocked.Exchange(ref _inFlight, 1) == 0) { _ = RunAsync(...); } else Interlocked.Increment(ref _dropped);`, 해제는 `RunAsync`의 `finally`에서 `Volatile.Write(ref _inFlight, 0)`.
- 요청별 데드라인은 `using var d = CancellationTokenSource.CreateLinkedTokenSource(ct); d.CancelAfter(t);` (응답 미도착에도 타이머 잔류 0).
- 오래된 응답이 새 결과를 덮지 않게 시퀀스 비교(`if (_last.Seq <= seq) _last = r;`).

## 상한 컬렉션·감사 링버퍼
- 이력/중복 집합은 고정 상한(`Insert(0)`+`RemoveAt(Count-1)` 또는 초과 시 `Clear`) — 무한 성장 컬렉션 금지.
- 내부 프로브(카나리·헬스체크)는 사용자 감사 이력에 기록 금지: 공개 상수 키(`SecureSecretVault.CanaryKeyName`)로 식별해 `Record` 진입부에서 전용 `Interlocked` 카운터로 분기·조기 반환(이력·`LastAction`·`OnChange` 미접촉).

## 수명·예외 격리 (타이머·콜백·소켓)
- `new Timer(async _ => ...)` 금지(async void) → 지연/디바운스는 `SafeDebouncer.Schedule(delay, ct => work(ct))`, 주기 틱은 동기 콜백 최상위 `try/catch` + `Program.Log`.
- CTS 교체는 `Interlocked.Exchange(ref _cts, next)` 후 이전 것 `Cancel()`+`Dispose()`, 완료 측은 `CompareExchange(ref _cts, null, mine)` 성공 시에만 `Dispose`.
- Windows UDP 소켓은 생성 직후 `IOControl(unchecked((int)0x9800000C), new byte[4], null)`(SIO_UDP_CONNRESET off, try/catch) + 수신 루프는 예외 시 break 대신 백오프 `continue`.

## Zero-Alloc 컬렉션·암호 (v15.0.0 · QuantumP2pMeshRouter)
- 그래프 탐색은 문자열 키 `Dictionary/HashSet` 대신 노드 정수 인덱스 + 인스턴스 스크래치 배열(`double[]/int[]/bool[]`, 락 내부·노드 추가 시에만 확장).
- 가변 길이 경로/정규화 버퍼는 `ArrayPool.Rent` + `ref byte[]` 재대여 헬퍼(`EnsureCapacity`), 고정 상한 데이터(서명 체인 = TTL×64B)는 메서드 진입부 `stackalloc`.
- 정규화 문자열 → UTF-8 바이트 직접 기록: `"lit"u8`, `Encoding.UTF8.GetBytes(string, Span<byte>)`, `long/int/Guid.TryFormat(Span<byte>, …, InvariantCulture)`. 기존 문자열 정규화와 **비트 동일성 테스트 필수**(검증기 승인으로 고정).
- 암호 원샷: `HMACSHA512.HashData(key, src, dst)`·`SHA256.HashData(src, dst)`·`FixedTimeEquals(span, span)` — 0B(실측). `new HMACSHA512()`·`ComputeHash` 금지.
- 상한 집합은 **용량 선할당**(`new HashSet<Guid>(cap + 1)`): 미지정 시 상한 도달까지 성장 리사이즈가 회당 ~100B 할당으로 관측됨. `Clear()`는 버킷 유지.
- 멱등 키는 `string` 대신 값 형식(`Guid`) · 결과는 `readonly record struct` + 사유 `enum`(라벨은 리터럴 switch).
- 반환 객체가 계약인 API(`Send` → 레코드·봉투)는 Zero-Alloc 불가 → 같은 코어의 값 반환 변형(`Route`)을 핫패스용으로 분리, 물질화는 nullable 수집기(`capture is not null`)로만.
- HUD 1줄 요약은 상태 필드만 기록하고 getter에서 지연 포맷(`_pending` enum) — 송신 루프 문자열 0.

## SIMD (Vector<T> / Intrinsics)
- 분기 순서: `Avx2.IsSupported`(32B) → `Sse2`/`AdvSimd`(16B) → `Vector.IsHardwareAccelerated`(Vector<T>) → 스칼라. 꼬리는 항상 스칼라 마감, `BitConverter.IsLittleEndian` 아니면 스칼라.
- 로드/스토어: `Vector128/256.LoadUnsafe(ref MemoryMarshal.GetReference(span), (nuint)i)`·`StoreUnsafe`, Vector<T>는 `Unsafe.ReadUnaligned/WriteUnaligned`.
- 바이트 포화 덧셈 이식형: `sum = v + add; sum | Vector.LessThan(sum, v)` (랩어라운드 검출 마스크).
- 픽셀 패턴 연산은 RGBA를 `uint` 패킹 후 `Vector*.Create(uint).AsByte()` 브로드캐스트. 레인별 고정값(예: A=255)은 포화 연산으로 흡수해 분기 제거.
- 모든 경로를 `public`으로 노출하고 시드 고정 난수 × 길이 0..N(비정렬 꼬리 포함) 스칼라 대비 비트 동일 테스트 필수.
- 이미 벡터화된 BCL(`Span.CopyTo`=memmove)을 수작업 SIMD로 대체 금지.

## 비디오 파이프라인 (v15.1.0 · Wan Ultra-Efficiency)
- 가중 보간 SIMD: byte→ushort `Vector*.Widen` 후 `p·(256−w) + c·w + 128 >> 8`, `Narrow`로 복귀 — 상한 65408 < 65536이라 오버플로 없음. Vector256 → Vector128 → 스칼라 꼬리 순, 원소별 연산이라 dest==src 제자리 별칭 허용(`WanPostProcessingEngine.BlendWeightedInto`).
- 저 FPS→60FPS: 펌프 스레드가 키프레임 2장을 보유하고 위상 `elapsed·256/interval`(0~256 clamp)로 보간 송출. 위상 256은 키프레임당 1회만 송출 후 홀드(무변경 재송출 금지). 테스트는 타임스탬프 주입(`PumpPacedTick(long)`)으로 벽시계 없이 결정적 검증.
- 생산↔소비 핸드오프: 순수 2버퍼는 소비 중 생산자 대기가 불가피 → back/front + 교환 슬롯 3개, `인덱스|Dirty`를 int 1개에 담아 `Interlocked.Exchange`로 스왑(양측 대기 0). 단일 생산·단일 소비는 CAS 소유 플래그로 강제하고 동시 2번째 호출은 대기 대신 즉시 false. 소비 전 덮어씀=백프레셔 드롭 카운트, 시퀀스 역행 카운터는 항상 0으로 테스트 잠금(`Consumed + Overwritten == Published`).
- MMF Mutex(최대 50ms)는 렌더러 스레드에서 직접 잡지 않는다 — `StageFrame`(게시) / 전용 `VirtualCam.Pump` 스레드(`SubmitFrame`) 분리. 펌프 스레드는 Dispose에서 먼저 정지, 이벤트 Dispose 경합은 `ObjectDisposedException` 흡수.
- 적응 거버너: EWMA는 float 비트 CAS, 창 카운터는 `Interlocked.Exchange`로 수거, 평가는 CAS 게이트 단일 실행, 레벨 전이는 CAS ±1(경계 초과·건너뛰기 0). 하향 즉시·상향은 연속 여유 창 N회(히스테리시스), 예산×2 스파이크는 창 대기 없이 선제 하향. 상태 문자열은 전이 시 원시값만 기록하고 조회 시 생성.
- 와이어 확장 필드는 `[JsonIgnore(WhenWritingDefault)]` 기본값 0 — 미사용 시 기존 페이로드와 바이트 동일.

## Blazor 원클릭 프리셋 바인딩 (v15.2.0 · 아기 스튜디오)
- 프리셋 주입은 정적 리터럴 **참조 대입**만(`PromptPresets.InjectMasterpiece` — 교체형, 연결/Trim 없음 → 0B). 범위 밖 인덱스는 `(uint)index < (uint)Length`로 현재 입력 유지.
- 루프 내 `@onclick="() => X(c)"`는 렌더마다 클로저 할당 → 핸들러 `Action[]`를 `OnInitialized`에서 1회 생성 후 `@onclick="_arr[i]"`로 전달(Razor가 즉시 평가하므로 `i` 캡처 없음).
- `EventCallback` 수신자가 `this`라 핸들러 종료 시 StateHasChanged 자동 — 수동 호출 불필요.
- 실제 카드 위치는 `Blazor/Pages/Workspace.razor` 우측 레일(`PromptWorkflowView.razor`는 존재하지 않음).

## UI 모디파이어 조합 (v15.2.1 · 아기 스튜디오 울티메이트 매트릭스)
- 테마(교체형)와 모디파이어(누적형)를 분리: 테마는 리터럴 참조 대입(0B), 모디파이어는 `BabyStudioMatrix.AppendModifier`.
- 누적 결합 규칙: 빈 입력 → 모디파이어 리터럴 참조, 이미 포함(`IndexOf(span, Ordinal)`) → 현재 참조 그대로(0B·중복 0), 그 외 꼬리 공백/쉼표를 `ReadOnlySpan` 슬라이스로 자른 뒤 `string.Create(static 람다, 튜플 상태)` 1회 — 결과 문자열 외 중간 할당 0. `TrimEnd()+","+x` 체인 금지.
- 결과가 `@bind` 대상 `string`이면 물질화 1회는 불가피 — 무변경 경로를 참조 반환으로 0B 보장하고 테스트로 잠근다(무변경 10k회 0B, 결합 1회 ≤ 결과 문자열 크기).
- 네거티브 가드 병합은 같은 규칙(`MergeNegative`)으로 사용자 문구를 앞에 보존하고 가드를 뒤에 결합, 세션 필드는 `Volatile`+CAS 루프(`ReferenceEquals(merged, seen)`이면 조기 종료)로 원자 교체 — 사용자 편집과 테마 주입 경합 시 갱신 유실 0.
- 와이어 확장(`WanVideoRequest.NegativePrompt`)은 비었을 때 필드 생략 — 기존 페이로드와 동일.
- 탭/카테고리 핸들러도 `Action[]`/`Action[][]`로 `OnInitialized` 1회 생성.

## 키워드 → 프롬프트 매핑 (v15.9.0 · AudioSfxEngine)
- 분류 결과 조합(SFX×앰비언스)은 정적 초기화 시 전부 미리 결합한 캐시 배열의 참조를 반환 — 런타임 결합/소문자화 0B. 매칭은 `span.IndexOf(kw, OrdinalIgnoreCase)`.
- 한국어 1음절 키워드(`비`·`물`·`새`·`강`)는 오탐(비행·동물·새벽·강아지) → 2음절 이상으로 등록.
- Blazor 동적 리스트(타임라인 트랙)도 슬롯 상한(MaxCuts)만큼 핸들러 `Action[]`를 1회 생성, 핸들러 내부에서 `idx < Count` 가드.
