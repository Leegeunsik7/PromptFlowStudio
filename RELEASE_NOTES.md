# PromptFlow Studio — Release Notes

**Current: v10.0.0-LTS Ready** · Release `-warnaserror` 0 오류 / 0 경고 (Desktop `net8.0-windows`) · 2026-09-24

> ⚠ **이력 정합성 고지**: 이 문서는 실제 코드/커밋 히스토리(`milestone.xml`, `CLAUDE.md`, 프로젝트 메모리 인덱스)에서
> 검증 가능한 범위만 기술한다. **v1.0.0 ~ v4.3.x 초기 부트스트랩 구간은 이 세션이 접근 가능한 기록에 존재하지 않아
> 상세 이력을 재구성할 수 없다** — 없는 사실을 지어내는 대신 "기록 이전 구간"으로 명시한다. v4.4.0 이후는 프로젝트
> 메모리/CLAUDE.md에 남은 실제 구현 기록을 근거로 작성했다.

---

## 0. 기록 이전 구간 — v1.0.0 ~ v4.3.x (Pre-recorded Bootstrap)

WinForms + BlazorWebView 하이브리드 셸, Storyboard/Wan 렌더 파이프라인의 최초 골격이 이 구간에서 수립된 것으로
추정되나, 태스크 단위의 상세 변경 기록이 현재 프로젝트 메모리 인덱스에 남아있지 않다. 최초로 상세 기록이 시작되는
지점은 v4.4.0(하드닝 1단계)이다.

## 1. v4.4.0 ~ v4.5.0 — Hardening & Weather/Environment Theme Zone

- **하드닝 1~4단계**: 전 모듈 안정화 완료(v4.4.4 "System Fully Hardened").
- **날씨/환경 테마존**(v4.5.0): 3모듈 + 스키마, 스튜디오 탭 마운트(이후 v5.3.4 헤더 버튼+모달로 승격).

## 2. v5.0 ~ v5.1 — Mobile Supremacy & MAUI Head

- v5.0 AR/NFC/자이로 시임 프로토타입 → v5.3.4 에서 PromptFlow 전용 4덱(프롬프트 퀵생성/스토리보드 씬플레이어/Wan
  렌더 큐 모니터/자이로 3D 씬 모션)으로 도메인 정화(AR/NFC/서보 서술 무효화).
- v5.1 `.Mobile`(MAUI) 헤드 최초 결선.

## 3. v5.4 ~ v5.8 — Flow Suite: DAG 엔진 & 모바일 Google-Flow급 UX

- **v5.4** 모바일 인터랙티브 4종 + Wan 백엔드 바인딩(`WanPromptCompiler`/`WanRenderClient`, 오프라인 큐) + Android CI.
- **v5.5** DAG 노드 엔진(`NodeGraphEngine` Kahn 위상정렬, `FlowExecutionPipeline`, `TrueFlowCanvas`) — 인라인 비디오
  프리뷰 → 키프레임 추출 I2V 믹싱 → 앵커 저장/그래프 JSON 직렬화(`.pfGraph`) → 무인 N회 체인러너 → Concat 롱폼
  병합(8포트) + 라이브 프로그레스 HUD.
- **v5.6** 베이스프리라이트/템플릿 라이브러리, 갤러리 자산화(`GalleryAutoSaveService`), 오프라인 스텁
  (`WanLocalStubEngine`), 품질 가드레일(`QualityGuardrailPreset` Soft/Balanced/Strict, 단일 주입점
  `WanRenderClient.SubmitAsync`).
- **v5.7** Long→Shorts 재구성, 다중세그먼트 닥(1/3/4컷 체인 + Concat), pHash 정체성 유지율(`IdentityHashAnalyzer`),
  FFmpeg 실프레임 추출, 닥 체인 취소/이어보기(Resume · `RescuePartial`/`existingCompletedUrls`), 호스트 LLM
  부스터 결선, 뷰포트 레이아웃 스택(플로팅 닥 상시 노출).
- **v5.8** B2B 모바일 대량 양산(`BatchTemplateEngine`), 3D 자이로 라이브 뷰어(Pure CSS/SVG).

## 4. v6.0 — 통합 & 관제 Edge

분산 GPU 워커 그리드 자가치유 페일오버(`DistributedRenderGrid`) · `Channel<T>` HMAC Webhook 디스패처 ·
WebRTC 스틸 폴백 · API 게이트웨이(HMAC 상수시간 대조, Lock-free Token Bucket) · Pure BCL `HttpListener` REST
Edge · 60초 링버퍼 텔레메트리 HUD.

## 5. v7.0 — 온디바이스 AI NLE

`WebGpuInferenceEngine`(DirectML/WebGPU 프로브) · Pure CSS Grid 4트랙 NLE · 감정분석 오토디렉터 +
Pure BCL RIFF WAV PCM 파형 합성(`AudioWaveSynthesizer`) · 렌더 컴파일러 · FCPXML 1.9/OTIO 내보내기.

## 6. v8.0 — 에이전트 스웜 & C2PA

`AgentSwarmOrchestrator`(4에이전트 6라운드 멱등 수렴) · `Spatial3DVideoEngine`(BCL 뎁스 SBS 3D) ·
`C2PaWatermarkEngine`(SHA-256+HMAC-SHA256 JUMBF) · `SystemStressAuditor`(Zero-Alloc 오디터).

## 7. v9.0 — Enterprise Master Release Finalized

`EnterpriseMasterOrchestrator`(5티어 헬스 집계) · `ProductionSecuritySealer`(C2PA/WORM/SAML·OIDC/ZTNA 4기둥
단일 무결성 스냅샷) · `MasterReleaseManifest`(Air-Gapped 라이선스 + SHA-256 빌드 매니페스트). 런타임 실측 프로브로
위변조 검출 전 케이스 확인.

## 8. v9.1 ~ v9.6 — On-Device Sync & Autonomous Services

- v9.1 모바일 제스처 엔진 · 저전력 최적화 · 오프라인 바이너리 캐시(SHA-256 무결성).
- v9.2 온디바이스 비전 추론 · P2P LAN/CRDT 크로스디바이스 상태 동기화 · Zero-Alloc 크래시 텔레메트리.
- v9.3 공간 글래스모피즘 렌더러 · PQC P2P 메시 라우터 · 자가치유 파이프라인 워치독.
- v9.4 자율 워크플로우 합성(DAG) · <50ms 초저지연 엣지 브로드캐스터 · 멀티테넌트 리소스 거버너.
- v9.5 적응형 뉴럴 디렉터(샷 구도) · Raft+BFT 분산 엣지 합의 메시 · Zero-Trust 거버넌스 오디터.
- v9.6 자율 뉴럴 메모리 스트리머(MMF 아레나) · 양자 내성 신원 연합(ECDSA P-384+ML-DSA-65 스텁) · 실시간 이상
  감지 센티널(EWMA z-score + 세션 자율 교체).

## 9. v9.7 ~ v9.9 — Swarm Governance, Quantum Sealing, Self-Optimization

- **v9.7** `AutonomousSwarmGovernanceEngine`(하트비트 쿼럼/리더 재선출/리밸런싱) · `QuantumStorageSealingEngine`
  (Merkle 세그먼트 캐시 봉인+자율 복구) · `SelfOptimizingMultimodalPipeline`(5티어 히스테리시스 VRAM/지연 튜닝).
- **v9.8** `AutonomousNeuralTilingUpscaler`(MMF 타일 4K/8K) · `ConfidentialComputingAttestor`(엔클레이브/TPM
  증명+ZTNA) · `AdaptiveMultiCloudEdgeFailover`(SLA 10ms 세션 무유실 페일오버).
- **v9.9** `SwarmConsensusEngine`(PBFT 3단계 에이전트 합의) · `QuantumResistantStateSealer`(MMF+Span 8슬롯 상태
  스냅샷 봉인/롤백) · `SelfOptimizingPipelineTuner`(PID 연속 튜닝).

## 10. v10.0.0 — Enterprise Core Release

- `EnterpriseAuditTelemetryAggregator`(다중 스포크 SHA-256 해시체인 감사 원장).
- `HardenedSecurityPolicyEnforcer`(글롭 정책 PEP, 위반 누적 Zero-Trust 격리, 결정 해시체인).
- `AutonomousSystemHealthOrchestrator`(서킷 브레이커 Closed/Open/HalfOpen, 합성 헬스 점수).

## 11. v10.0.0-LTS — Production Deployment Readiness (this release)

- **`LtsHotfixPatcher`**(`Services/Operations`): `QuantumResistantStateSealer` 연동 — 패치 패키지를 다이제스트
  +HMAC 서명으로 봉인/재검증하고, 하위 호환성 매트릭스(Desktop/Mobile/Shared RCL 계약 버전 창)를 통과한 패치만
  원자적 트랜잭션(사전 스냅샷 → 적용 → 실패 시 즉시 롤백)으로 적용한다.
- **`ProductionTelemetryExporter`**(`Services/Telemetry`): `EnterpriseAuditTelemetryAggregator` 연동 — 신규
  감사 원장 엔트리를 PII(이메일/전화/사설 IPv4) 자동 마스킹 후 OTLP Logs 호환 JSON 배치로 직렬화, HMAC
  배치서명 첨부, 네트워크 실패 누적 시 서킷 브레이커(Closed→Open→HalfOpen)로 전송 유예·버퍼링.
- **`.github/workflows/release-pipeline.yml`**: Windows/Android/iOS(시뮬레이터 컴파일 게이트)/macOS(Mac
  Catalyst — TargetFrameworks 미선언 시 Graceful 스킵) + Shared RCL NuGet 팩, 전 잡 `-warnaserror` 0/0,
  SHA-256 체크섬, 태그 푸시 시 통합 매니페스트 GitHub Release 게시.
- DI(`MainForm.cs`/`MauiProgram.cs`) + HUD(`GlobalTelemetryHudView.razor` `gth-v100lts` 카드) 결선 완료.

---

## 프로덕션 배포 체크리스트

- [ ] `dotnet build PromptFlowStudio/PromptFlowStudio.csproj -c Release -warnaserror` → 0/0 (필수 게이트).
- [ ] `PromptFlowStudio.Mobile` Android/iOS 워크로드(`maui-android`/`maui-ios`) 설치 후 동일 게이트 통과.
- [ ] `ffmpeg.exe`를 실행 파일 옆에 동봉하거나 사용자 설정(설정▸FFmpeg 경로)으로 지정 — 미동봉 시 영상
      병합/썸네일 기능은 Graceful 비활성화(오류 아님, 기능 축소).
- [ ] 스토어 서명 배포(`store-deploy.yml`)용 시크릿(아래 표) 리포지토리 Settings → Secrets 에 등록.
- [ ] `LtsHotfixPatcher` 호환성 매트릭스 상수(`DesktopMin/Max`, `MobileMin/Max`, `SharedContractMin/Max`,
      현재 9.5–10.99)를 다음 마이너 세대 진입 시 갱신.
- [ ] `ProductionTelemetryExporter`의 로컬 시뮬레이션 전송(`SimulateNetworkSendLocked`)을 실제 원격 릴레이
      (`HttpClient` 기반)로 교체 — 결선점(스텁, 교체 시 상위 무변경).
- [ ] `EnterpriseAuditTelemetryAggregator.VerifyChain()`을 배포 전 CI 단계에서 1회 실행해 원장 해시체인 연속성
      확인(수동 QA 또는 `tools/PromptFlowCiGate` 확장 권장).

## 환경 변수 / 시크릿 규격

이 앱은 **런타임 설정을 환경 변수가 아니라 `UserPreferenceService`(로컬 영속 설정 UI · 예: FFmpeg 경로,
품질 가드레일 프리셋)로 관리**한다. 코드베이스에서 실제로 참조하는 프로세스 환경 변수는 다음 두 가지뿐이다:

| 변수 | 위치 | 용도 |
|---|---|---|
| `PATH` | `Services/Deploy/PromptFlowPackagerService.cs`, `Services/Engines/ProcessRunner.cs` | 외부 CLI(ffmpeg 등) 실행 파일 탐색 |
| `GOOGLE_SA_JSON` | `Tools/PromptFlowCiGate/Program.cs` | Google Play 서비스 계정 자격증명(CI 배포 검수) |

**CI/CD 시크릿**(GitHub Actions Secrets · `store-deploy.yml`/`release.yml`에서 사용, 런타임 앱과 무관):

| 시크릿 | 용도 |
|---|---|
| `ANDROID_KEYSTORE_B64` / `ANDROID_KEY_ALIAS` / `ANDROID_STORE_PASS` / `ANDROID_KEY_PASS` | Android AAB 서명 |
| `GOOGLE_SA_JSON` | Google Play 배포(내부 트랙) |
| `IOS_CERT_P12_B64` / `IOS_PROFILE_B64` / `IOS_SIGNING_IDENTITY` | iOS IPA 서명 |
| `ASC_KEY_ID` / `ASC_ISSUER_ID` / `ASC_PRIVATE_KEY` | App Store Connect(TestFlight) 업로드 |
| `CODE_SIGN_PFX_BASE64` / `CODE_SIGN_PFX_PASSWORD` | Windows EXE 코드사인(현재 `release.yml` 내 주석 처리 · 미적용) |

`release-pipeline.yml`(이번 릴리스 신설)은 **서명 없는 컴파일 게이트 + 체크섬 + 아티팩트 수집**만 수행하며,
스토어 서명 배포는 기존 `store-deploy.yml`이 전담한다(역할 분리 유지).

## LTS 지원 정책

- **호환성 창**: 핫픽스는 제품 SemVer `major.minor` 기준 **9.5 ~ 10.99** 범위(Desktop/Mobile/Shared RCL 동일
  창)에 한해 `LtsHotfixPatcher`가 자동 승인 대상으로 평가한다. 범위 밖 패치는 호환성 매트릭스 미통과로 자동 거부.
- **원자성 보장**: 모든 핫픽스 적용은 사전 상태 스냅샷(`QuantumResistantStateSealer.SealSnapshot`)을 롤백
  지점으로 확보한 뒤 진행하며, 적용 중 실패는 즉시 롤백 상태로 전이한다(부분 적용 상태 없음).
- **감사 가능성**: 모든 정책 판정(`HardenedSecurityPolicyEnforcer`)과 감사 이벤트(`EnterpriseAuditTelemetryAggregator`)
  는 SHA-256 해시체인으로 봉인되어 사후 변조 여부를 상수시간 대조로 검증 가능하다.
- **차기 마일스톤**: Long-Term Support 유지보수 & 실운영(Live Operations) — 신규 기능 개발보다 안정성/보안
  회귀 방지에 우선순위.
