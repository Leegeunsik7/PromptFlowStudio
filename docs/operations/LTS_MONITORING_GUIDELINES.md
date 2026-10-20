# PromptFlow Studio — LTS 운영 모니터링 가이드라인

**대상: v10.5.0 (F1~F5)** · 관제 채널: `GlobalTelemetryHudView.razor`(데스크톱 전용 우측 슬라이딩 대시보드)

> ⚠ **정합성 고지**: 아래 표에서 "코드 상수"로 표기한 값은 실제 소스 파일·라인에서 확인된 값이다. "운영 권고"로
> 표기한 값은 해당 실측 지표(예: OTLP 성공률)에 대해 이 문서가 새로 제안하는 알림 임계치이며, 코드에 하드코딩된
> 강제 임계치가 아니다 — 자동 알림이 필요하면 별도 구현이 선행돼야 한다.

---

## §1. 핵심 관제 임계치

| 지표 | 주의(Warn) | 위험(Critical) | 근거 |
|---|---|---|---|
| Wan 추론 타일 지연(EWMA) | **120ms**(TargetFrameMs) | **138ms**(TargetFrameMs×1.15=DowngradeRatio) | 코드 상수 — `LivePipelineAutoTuner.cs`: `TargetFrameMs=120`, `DowngradeRatio=1.15`. 138ms를 4틱(`HysteresisTicks`) 연속 초과 시 양자화 자동 다운그레이드(§3-1). |
| VRAM Peak 사용률 | **85%**(세이프티 마진) | **90%**(운영 권고 하드스톱) | 85%는 코드 상수 — `AdaptiveHardwareProfiler.cs`: `VramSafetyMargin=0.85`(OS/타 프로세스용 15% 예비). 90%는 이 문서의 운영 권고치(85% 마진을 실측 초과하는 이상 신호로 간주). |
| OTLP 배치 성공률(최근 50건) | **98%** | **90%** | 운영 권고 — 실측 지표 자체는 코드 상수: `OtlpTelemetryVisualizerProvider.DefaultWindowSize=50`, `Snapshot.SuccessRatePct`(유휴 배치 제외 집계, §3-2). |
| GC Gen0 할당(핫패스) | 리뷰 시 0 기대 | 리뷰 시 0 기대 | 설계 원칙(코드 상수 아님) — `CLAUDE.md` 헌장 규칙6: 추론 루프/HUD 갱신/워커 틱에서 LINQ·박싱·문자열 보간 금지, `Span<T>`/`ArrayPool<T>`/재사용 `StringBuilder`만 허용. 런타임 카운터가 아니라 **코드 리뷰·프로브 시점 체크리스트**로 운용한다. |
| ComfyUI 헬스체크 응답 | — | **2초** 초과 시 즉시 폴백 | 코드 상수 — `ComfyUiWanConnector.cs`: `HealthCheckTimeout=TimeSpan.FromSeconds(2)`. |
| 백엔드 선택 캐시 | 5초(`ProbeTtl`) | — | 코드 상수 — `WanBackendFactory.cs`. |
| 캘리브레이션 재측정 주기 | 24시간(TTL) | — | 코드 상수 — `AutoTunerHardwareCalibrator.cs`: `CacheTtl=TimeSpan.FromHours(24)`. |

---

## §2. Global Telemetry HUD 관제 카드 5종 운용 규격

| 카드 ID | 대상 서비스 | 핵심 표시 지표 | 정상/이상 판정 |
|---|---|---|---|
| **gth-v105f1** | `SecureSecretVault` | `Resolved` / `Generated` / `InsecureFallbacks` 카운터, 최근 5건 해석 이력 | `InsecureFallbacks > 0` → 경고(DPAPI 미가용). |
| **gth-v105f2** | `WanBackendFactory` | `Active` 백엔드(ComfyUi/DirectMlInProcess), `ProbeCount`/`ComfySelections`/`DirectMlSelections` | `ComfyUiAvailable=false` 지속 → 경고(원격 렌더 불가, 로컬 폴백 중). |
| **gth-v105f3** | `AutoTunerHardwareCalibrator` | 배율(`DynamicIterationMultiplier`), 유효반복, `Calibrations`/`Fallbacks` | `LastResult.Ok=false`(폴백 배율 1.0 강제) → 경고(호스트 측정 실패/스파이크). |
| **gth-v105f4** | `OtlpTelemetryVisualizerProvider` | 최근 50건 성공/실패, 성공률%, PII 마스킹 총량, 서킷 상태·전이 건수 | `CurrentState != Closed` → 경고(Open=전송 유예 중, HalfOpen=반개방 프로브 중). §1 임계치 참조. |
| **gth-v105f5** | `HotfixAutoSignerEngine` | `Signed`/`Failures`, 최근 서명 Digest8, 알고리즘 배지(SHA3-256/SHA-256(폴백)) | `Failures` 증가 → 경고(서명 파이프라인 점검 필요). 알고리즘이 예상과 다르면(예: SHA3 지원 호스트인데 폴백 표시) OS 설정 점검. |

카드 공통 규격: 모든 카드는 대상 서비스의 `OnChange` 이벤트를 구독해 `InvokeAsync(StateHasChanged)`로 갱신하며
(`CLAUDE.md` 헌장 규칙3), 컴포넌트 dispose 시 반드시 구독 해제된다. 카드 신설 시 이 두 지점(구독/해제)이 쌍으로
존재하는지가 코드 리뷰 체크포인트다.

---

## §3. 이상 징후 대응 프로토콜

### §3-1. EWMA 양자화 자동 다운그레이드
`LivePipelineAutoTuner.ObserveFrame(elapsedMs)`가 타일마다 EWMA(지수가중이동평균, `EwmaAlpha=0.25`)를 갱신한다.
EWMA가 138ms(목표×1.15)를 **4틱 연속**(`HysteresisTicks`) 초과하면 양자화를 즉시 한 단계 낮춘다(Fp16→Fp8→Int8).
반대로 66ms(목표×0.55, `UpgradeHeadroomRatio`) 미만을 4틱 연속 유지하면 한 단계 올린다. **운영자는 개입할 필요가
없다** — 관찰만 하고, 다운그레이드가 반복적으로 발생하면(HUD `LastAction` 이력) 하드웨어 증설/워크로드 조정을
검토한다.

### §3-2. 유휴 배치 제외 OTLP 재전송/집계
`ProductionTelemetryExporter`의 데모 틱(1초 간격)은 신규 전송분이 없어도 `Flush()`가 호출되며, 이때
`RecordCount=0`인 "빈 성공" 배치가 발생한다. `OtlpTelemetryVisualizerProvider.Aggregate()`는 **이 빈 배치를
성공률 집계에서 제외**한다(F4 구현 중 실측 프로브로 발견·수정된 버그 — 수정 전에는 유휴 시간이 길수록 성공률이
허위로 100%에 수렴했다). 운영자가 원시 이력 전체(유휴 틱 포함)를 봐야 할 경우
`ProductionTelemetryExporter.RecentBatches`(최대 64건, `CircularBuffer<T>`)를 직접 조회한다 — HUD 카드는
필터링된 집계(§1 임계치 대상)만 표시한다.

### §3-3. 변조 거부 Fail-Closed
`ReleaseIntegrityValidator.VerifyFile()`/`VerifyAll()`이 다이제스트 또는 HMAC 서명 불일치를 검출하면
(`ArtifactValidationState.TamperDetected`), **해당 아티팩트는 예외 없이 배포/적용을 거부**한다 — "경고만 표시하고
진행"하는 폴백 경로는 이 프로젝트에 존재하지 않는다(의도적 설계, Rule 7 "응답 스키마는 도착 즉시 검증 후 폴백"과
구분되는 지점: 폴백은 *가용성* 문제에 적용되고, 변조는 *무결성* 문제라 폴백 대상이 아니다). `TamperDetected` 카운터가
0보다 크면 즉시 인시던트로 격상하고 `LIVE_DEPLOYMENT_RUNBOOK.md` Phase 4를 실행한다.

### §3-4. 하드웨어 캘리브레이션 자기일관성 이상
`AutoTunerHardwareCalibrator`가 동일 호스트에서 배율이 시간에 따라 크게 요동치면(예: 1.0 근방 유지되다 갑자기
0.3 또는 5.0대로 이동) 측정 스파이크가 아니라 **호스트 자체의 열 스로틀링/타 프로세스 경합**일 수 있다 — 배율
자체는 `[0.1, 10]` 범위를 벗어나면 자동으로 안전 폴백(1.0)되므로 크래시하지 않지만, HUD `LastAction` 이력에서
빈번한 폴백 전환이 보이면 하드웨어 점검 대상으로 표시한다.
