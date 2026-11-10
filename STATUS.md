# Current Project Status

- Active Milestone: 【Pluto Local AI Settings UI + Publish Caption Failover】 🦙 로컬 AI 설정 모달 · 🔁 SNS 캡션 Gemini → 로컬 게이트웨이 → 템플릿
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (Mobile: 로컬 Android SDK 부재로 미빌드)
- Tests: 대상 실행만 — `~Pluto|~Publish` 72/72 PASS · 전체 스위트는 호스트 메모리 여유 부족으로 보류
- Updated: 2026-11-10 (KST)

## Completed
- 🦙 `PlutoLocalAiSettingsModal.razor` — 활성 토글 · 엔드포인트 URL · 모델 식별자 · LAN 허용 · 🔌 연결 테스트(🟢 프로토콜·지연 / 🔴 오프라인 + 팁) · 저장 · `@inject` 없음(파라미터 주입)
- 🖱 진입점 — `Workspace.razor` 툴바 `🦙 로컬 AI 설정` · `PlutoTrendLab.razor` 헤더 버튼
- ⚙️ `PlutoLocalAiSettingsService` — 검증/정규화 → 실행 중 게이트웨이 즉시 반영(서킷 초기화) → `pluto_local_ai_settings.json` 원자 영속([부분 구제]) · 시작 시 파일 > `PFS_LOCAL_AI_*` > 기본값 · API 키는 환경변수 전용(직렬화 제외)
- 📡 `PlutoCustomLocalGateway.PingDetailedAsync` — OpenAI/Ollama 감지 · 지연 · 거부/타임아웃/401·403/경로 불일치 팁 · `ResetState()`
- 🔁 `PublishViewModel.GeneratePlutoSocialDraftAsync` — `PlutoLlmFailoverClient` 경유(미주입 시 Gemini 단독) · 오프라인 시 결정론 템플릿
- 🧩 DI — `MainForm.cs` · `MauiProgram.cs` 양쪽 등록
- 🧪 `PlutoLocalGatewayOptions_PersistsAndAppliesRuntimeChanges` · `PublishViewModel_GeneratesCaptions_ViaFailoverChain`

## Next
- 전체 스위트 재실행(메모리 확보 시) · 모바일 빌드 검증(Android SDK 확보 시)
- `GenerateSeoAsync`(제목/태그 SEO)도 페일오버 체인 결선 검토
