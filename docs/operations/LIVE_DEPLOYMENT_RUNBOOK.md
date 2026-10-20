# PromptFlow Studio — 프로덕션 라이브 배포 런북

**대상: v10.5.0 (F1~F5 Core Feature Spectrum Integrated)** · Release `-warnaserror` 0 오류 / 0 경고 (Desktop `net8.0-windows`)

> ⚠ **정합성 고지**: 이 문서는 실제 소스코드에서 검증 가능한 범위만 기술한다. 원 작업 지시서에는 "Phase 1에서
> 2,831개 테스트 통과를 확인"이라는 절차가 포함돼 있었으나, **이 저장소에는 커밋된 자동화 테스트 프로젝트(xUnit/NUnit
> 등)가 존재하지 않아 그런 테스트 스위트 자체가 없다** — 없는 절차를 있는 것처럼 기술하지 않는다. 대신 이 프로젝트가
> 실제로 사용해 온 검증 수단(Release 0/0 빌드 게이트 + `CLAUDE.md` 명시 "스크래치 콘솔 런타임 프로브" 방법론)을
> Phase 1에 정확히 기술하고, §5에 "커밋된 회귀 테스트 스위트 없음"을 개선 권고로 남긴다. 그 외 수치(지연 임계치,
> VRAM 세이프티 마진, 헬스체크 타임아웃, MMF 슬롯 수, HUD 카드 ID 등)는 모두 아래 각 절에서 실제 소스 파일·라인을
> 근거로 표기했다.

---

## Phase 1 — 배포 전 무결성/보안 검증

1. **Release 빌드 게이트(0 경고/0 오류 필수)** — 레포 루트에서:
   ```bash
   dotnet build PromptFlowStudio/PromptFlowStudio.csproj -c Release -warnaserror
   ```
   `경고 0개 / 오류 0개`가 아니면 배포를 진행하지 않는다. (앱이 실행 중이면 출력 DLL이 잠겨 Debug 빌드만 가능 — 반드시 앱 종료 후 실행.)

2. **DPAPI 시크릿 키 존재 확인** — `SecureSecretVault`(`Services/Security/SecureSecretVault.cs`)가 `#if WINDOWS`
   구간에서 `System.Security.Cryptography.ProtectedData`(CurrentUser scope)로 봉인한 키 파일이 아래 경로에
   실존해야 한다. 없으면 최초 기동 시 `ResolveOrCreate()`가 자동 생성하지만, **배포 전에는 운영 계정 프로필에서
   미리 생성해 두는 것을 권장**(최초 기동 지연/권한 문제 사전 차단):
   ```
   %LocalAppData%\PromptFlowStudio\Keys\{key-name}.key
   ```
   `SecureSecretVault.History`(HUD `gth-v105f1` 카드)에서 `Resolved`/`Generated`/`InsecureFallbacks` 카운터를 확인 —
   **`InsecureFallbacks > 0`이면 배포를 중단**한다(DPAPI 미가용 환경이거나 권한 문제).

3. **실제 검증 수단 — 런타임 프로브 방법론(`CLAUDE.md` 명시 절차)**: 이 저장소는 커밋된 테스트 프로젝트 대신,
   신규/변경된 서비스마다 세션 스크래치 콘솔에 실제 프로덕션 `.cs` 파일을 `<Compile Include>` 절대경로로 링크한 뒤
   실행해 어서션을 통과시키는 방식으로 검증해 왔다(예: F3 캘리브레이터 18/18, F4 OTLP 시각화 21/21, F5 핫픽스
   서명기 20/20 — 세 사례 모두 실행 중 실제 버그를 1건씩 발견·수정 후 재검증됨). 배포 직전에는 최소한 다음 두
   축을 프로브로 재확인한다:
   - **암호/서명 경로**: `ReleaseIntegrityValidator.SignFile()` → `VerifyFile()` 왕복이 위조 없이 통과하는지.
   - **폴백 경로**: `QuantumResistantStateSealer.Sha3Supported`가 대상 배포 호스트에서 `false`(SHA3 미지원 OS)일 때
     `SHA-256(Fallback)` 경로가 정상 작동하는지 — 개발 머신(Windows 10)에서는 항상 `false`로 실측됨.

4. **무결성 최종 게이트**: `ReleaseIntegrityValidator.VerifyAll(resolvePath)`를 배포 대상 아티팩트 전체에 대해
   1회 실행 — `IntegrityCheckReport.Ok == false`(변조/누락 검출)면 **배포 즉시 중단**.

---

## Phase 2 — 아티팩트 번들링 및 서명

1. **4슬롯 패키징** — `ProductionArtifactPackager`(`Services/Operations/ProductionArtifactPackager.cs`)가
   **Windows / Android / iOS / SharedNuGet 4개 플랫폼 슬롯**을 순회하며 존재하는 산출물만 zip화한다(없는 슬롯은
   `Skipped`로 기록, 에러 아님). HUD에서 "패키징 실행" 버튼 또는:
   ```csharp
   var report = packager.RunPackaging();   // PackagingReport: AnyPackaged/Packaged/Skipped/ElapsedMs/DistRoot
   ```
   `report.DistRoot` 하위에 슬롯별 zip이 생성된다.

2. **HMAC/SHA3(SHA-256 폴백) 서명 + 배포 매니페스트 출력** — `HotfixAutoSignerEngine`
   (`Services/Operations/HotfixAutoSignerEngine.cs`)이 `LtsHotfixPatcher`(스테이징/호환성 매트릭스 라이프사이클)와
   `ReleaseIntegrityValidator`(SHA3-256, 런타임 미지원 시 SHA-256 자동 폴백 + `SecureSecretVault` 키 기반 HMAC)를
   그대로 재사용해 각 패키징 산출물을 서명한다:
   ```csharp
   var result = hotfixSigner.SignAndExport(patchFilePath, version, artifactId);
   // result.ManifestPath → {id}_{version}.hotfix-manifest.json
   // result.SigPath      → {id}_{version}.sig  (서명 64자리 16진수 텍스트)
   ```
   출력 위치: `%LocalAppData%\PromptFlowStudio\HotfixReleases\`. **`result.Ok == false`면 해당 아티팩트는 배포
   목록에서 제외**하고 `result.Note` 사유를 인시던트 채널에 기록한다.
   HUD `gth-v105f5` 카드에서 `Signed`/`Failures` 카운터와 최근 서명 알고리즘 배지(SHA3-256 또는 SHA-256(폴백))로
   실시간 확인 가능.

3. **매니페스트 배포 동봉**: 위 `.hotfix-manifest.json` + `.sig` 파일을 실제 패치 zip/DLL과 **반드시 함께** 배포
   채널에 올린다 — 오프라인 설치 환경에서 `.sig`(서명 16진 텍스트)와 매니페스트의 `digestHex`/`signatureHex`를
   대조해 재서명 없이 무결성 확인이 가능하도록 하기 위함이다.

---

## Phase 3 — 실전 배포 / 롤아웃

1. **하드웨어 자율 캘리브레이션(24시간 TTL)** — `AutoTunerHardwareCalibrator`
   (`Services/AI/Wan/AutoTunerHardwareCalibrator.cs`)는 호스트별 CPU 프록시 연산 속도(ops/ms)를 3트라이얼
   중앙값으로 측정해 `WanPerformanceBenchmarker`의 반복 상수 배율을 산출하고 **24시간 동안 캐시**한다
   (`CacheTtl = TimeSpan.FromHours(24)`). 신규 배포 호스트(새 하드웨어)에 처음 배포할 때는:
   ```csharp
   var result = calibrator.ForceRecalibrate();   // TTL 무시 즉시 재측정
   ```
   을 1회 강제 실행해 그 호스트의 배율을 즉시 확정 — 첫 요청이 stale/기본값(SafeFallbackBaseIterations=18.5M,
   배율 1.0)으로 처리되는 것을 방지한다. HUD `gth-v105f3` 카드의 "지금 재캘리브레이션" 버튼과 동일 동작.
   측정 실패/스파이크(배율이 [0.1, 10] 범위 밖)는 자동으로 배율 1.0(원본 18.5M 그대로) Graceful 폴백.

2. **Wan 백엔드 헬스체크 및 자율 폴백** — `WanBackendFactory`가 5초 캐시(`ProbeTtl`)로 상태를 유지하되, 실제
   ComfyUI 헬스체크 HTTP 호출 자체는 `ComfyUiWanConnector.HealthCheckTimeout = TimeSpan.FromSeconds(2)`로
   **2초 타임아웃**이 걸려 있다 — ComfyUI 원격 서버가 2초 내 응답하지 않으면 즉시 `DirectMlWanConnector`(인프로세스
   로컬 렌더)로 자율 폴백한다. 배포 직후에는:
   ```csharp
   factory.ForceRecheck();
   var status = await factory.GetStatusAsync();   // status.Active: ComfyUi | DirectMlInProcess
   ```
   로 의도한 백엔드가 선택되는지 1회 확인 — 로컬 전용 배포 환경(ComfyUI 미가동)이면 `DirectMlInProcess`가
   정상이다(오류 아님).

3. **점진 롤아웃 관찰창**: 배포 직후 최소 5분(캘리브레이션 TTL과 무관하게 실사용 트래픽 관찰 목적) 동안 HUD
   `gth-v105f4`(OTLP 시각화) 카드의 성공률/서킷 상태를 상시 모니터링 — 기준치는
   `docs/operations/LTS_MONITORING_GUIDELINES.md` §1 참조.

---

## Phase 4 — 비상 롤백

1. **MMF 스냅샷 원자적 복원** — `QuantumResistantStateSealer`(`Services/Security/QuantumResistantStateSealer.cs`)는
   라이브 애플리케이션 상태를 `MemoryMappedFile` 백드 **8슬롯 링버퍼**(`SnapshotView(Slot, Version, State,
   Digest8, Bytes)`)에 무복사(`Span<T>`)로 기록한다. 비상 상황에서는:
   ```csharp
   var verify = sealer.VerifyAndHeal();
   // VerifyResult(Valid, SlotsChecked, Tampered, RolledBack, ElapsedMs)
   ```
   를 실행 — **변조 감지 슬롯을 직전 무결 버전으로 자율 롤백**하고 `RolledBack` 건수를 반환한다. 이것이
   원 지시서가 말한 "VerifyAndHeal 자율 복구"의 실제 구현이며, 별도 스크립트 작성 없이 **이 메서드 호출 자체가
   비상 복구 절차**다(운영 스크립트는 이 한 호출을 감싸 로깅/알림만 추가하면 됨).

2. **핫픽스 자체 롤백** — `LtsHotfixPatcher.ApplyAtomic()`으로 적용된 패치가 사후 문제를 일으키면
   `HotfixStage.RolledBack` 전이가 자동/수동으로 기록된다(`RolledBackCount` 카운터, HUD 핫픽스 카드).

3. **Fail-Closed 원칙**: `ReleaseIntegrityValidator.VerifyFile()`/`VerifyAll()`이 `TamperDetected`를 반환하면
   **해당 아티팩트는 어떤 경우에도 배포/적용하지 않는다** — "일단 배포하고 나중에 검증"은 이 프로젝트의 설계
   원칙에 위배된다(§2 LTS_MONITORING_GUIDELINES.md 참조).

---

## §5 개선 권고 (다음 마일스톤 후보)

- **커밋된 자동화 회귀 테스트 스위트 없음** — 현재는 세션별 스크래치 프로브(비영속)로만 검증한다. 이 세션에서
  실제 버그를 3건(F3 이중 스케일링, F4 유휴 배치 오염, ProductionManifestExporter 중복키) 적발한 프로브들을
  `PromptFlowStudio.Tests` 프로젝트로 승격해 CI에 상시 편입하는 것을 권장.
- **2단계 VRAM 임계치(90%) 및 OTLP 성공률 임계치(98%/90%)는 코드 상수가 아닌 운영 권고값** — 실제 알림
  자동화(예: 임계치 초과 시 Webhook)가 필요하면 별도 구현 필요(현재는 HUD 수동 관찰).
