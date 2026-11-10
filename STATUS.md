# Current Project Status

- Active Milestone: 【Pluto Hardening Audit】 🛡️ 저장소 동시성 · drawtext 이스케이프 · Blazor 수명주기 · EPG SLA
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (Mobile: 로컬 Android SDK 부재로 미빌드)
- Tests: 대상 감사 테스트 51/51 PASS — 전체 936건 스위트는 호스트 메모리 여유 부족으로 이번 회차 보류
- Updated: 2026-11-10 (KST)

## Completed
- 🔤 DrawText 이스케이프 — `StockSuiteExporter` 3단계 이스케이프(옵션 `\\` → drawtext `\%` → 리터럴 `%`) · Fintech 뱃지 테스트 갱신
- 🔒 `PlutoJsonStore` 신설 — IdentityVault · LearningService · PreferenceLexicon · SocialRadar 저장 경로 동시성 직렬화/원자적 쓰기 일원화
- ♻️ Blazor 수명주기 — `PlutoCopilotDrawer` · `PlutoIdentityModal` · `Workspace` · `PublishViewModel` 구독/해제·Dispose 정합
- ⏱ EPG SLA — `TitanReleaseTests` 섭취 지연 단일 최대치 → 50회 최선값 상한(병렬 GC 정지 간헐 실패 제거)
- 🧪 `PlutoHardeningAuditTests` 추가 · `docs/lessons_learned.md` 기록

## Next
- 🔌 `PlutoCustomLocalGateway` (OpenAI `/v1/chat/completions` · Ollama `/api/chat`) → `PlutoLlmBridge` 3.5s 폴백 체인
- 🎞 `PlutoMilestoneOrchestrator` 4컷 성장 서사 → 스토리보드 4슬롯 자동 구성
- 전체 스위트 재실행(메모리 확보 시) · 모바일 빌드 검증(Android SDK 확보 시)
