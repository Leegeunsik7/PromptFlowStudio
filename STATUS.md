# Current Project Status
- Active Milestone: 【Step 3】 비즈니스 & 양산 스튜디오 정밀 감사 · 커머셜/라이브 큐 결선 · I2V 엔진 실물 바인딩
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (net8.0-android 미검증: SDK/JDK 미설치)
- Tests: 전체 654/654 PASS(직렬 · MaxCpuCount=1 · DisableParallelization) · RenderQueue+Commercial+Batch+MassProduction+LiveStudio 44/44
- Updated: 2026-10-28 (KST)

## Completed
- Step3 I2V: `VideoEngineRenderExecutionEngine.BuildRequest` — 실존 `ReferenceImagePath` → `WanVideoRequest.ImagePath`(I2V 분기 · 누락 시 T2V 폴백) · 캡처 롤/스토리보드 앵커/커머셜 제품 사진 공통
- Step3 커머셜: `🎬 커머셜 렌더 전송` — 6/15/30s·화면비 · 제품 사진 I2V · `BatchRenderJob.Metadata`(로고 세이프존·CTA·디스클레이머) · 완료 토스트 + 렌더 큐 이동 · `.meta.json` 메타 동반
- Step3 양산/비용: `ProductionCostTrackerService` 예상 비용 예약(`ReserveEstimate`/`ReleaseReservation`/`WouldExceedBudget`) · 큐 이탈 시 해제 · 한도 초과 시 `BudgetWarning` + 명시 승인 후 디스패치 · 분석 허브 예약 KPI
- Step3 라이브: `🎥 온에어 배경 비디오 렌더` — Hook·Solution·CTA 비주얼 → 루프 배경 작업(1080x1920 · 10s · loop 메타)
- Step2: 작업실↔스토리보드↔숏폼 결선 · 카메라 캡처 롤/`SmartCameraMerge` · `CameraStudio.razor` 통합
- QA/P1~P3: 화면 캡처 스크립트 · 렌더 실행 엔진·큐 진행률/실패 격리 · 네트워크 카메라 수신기(폴백)

## Known Issues
- 비용 예약은 메모리 전용(재시작 시 소멸) · 단가는 DashScope plus 초당 기준 추정
- 커머셜 로고/디스클레이머 실제 영상 합성(후처리)은 메타 전달까지 — 합성 단계 미결선
- 시뮬레이션 폴백은 실제 미디어 미생성 → `OutputPath` 지정 작업만 메타 기록
- `SingleFileBinaryProcessBootstrap_Check` 직렬 전수 중 벽시계 초과 간헐 이력
- 모바일: `ViewModels/`·`Blazor/Pages/` 미링크 · 로컬 ffmpeg.exe 부재 · Authenticode 미서명 · Android 빌드 미검증
