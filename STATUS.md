# Current Project Status

- Active Milestone: 【Pluto Local AI Gateway + 4-Cut Milestone】 🔌 사내 로컬 AI REST 게이트웨이 · 🎞 4컷 성장 마일스톤 오케스트레이터
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (Mobile: 로컬 Android SDK 부재로 미빌드)
- Tests: 대상 실행만 — `~Pluto` 41/41 PASS · 변경 파일 참조 스위트(BabyStudio·Titan·Security·Vision·Storyboard 등) 160/160 PASS · 전체 스위트는 호스트 메모리 여유 부족으로 보류
- Updated: 2026-11-10 (KST)

## Completed
- 🔌 `PlutoCustomLocalGateway` — OpenAI `/v1/chat/completions` 우선 → 404/405/501·연결 실패 시 Ollama `/api/chat`(stream=false) · 성공 프로토콜 기억 · 3.5s 요청 타임아웃 · 지수 백오프 서킷(1→30s) · 응답 1MiB 상한 · `<think>` 제거 · `PingAsync`(`/v1/models`→`/api/tags`)
- ⚙️ `PlutoLocalGatewayOptions` — 기본 `http://localhost:8080` · `local-slm` · 비활성 · `PFS_LOCAL_AI_*` 환경변수 · 비루프백은 `PFS_LOCAL_AI_ALLOW_REMOTE=1` 필요
- 🔁 `PlutoLlmBridge` 체인 — Gemini → 로컬 게이트웨이 → 결정론 템플릿(단계별 예산) · `PlutoLlmFailoverClient`로 기존 Pluto 서비스 전체 결선
- 🎞 `PlutoMilestoneOrchestrator` — 100일의 기적/첫돌의 감동/우리아이 하루 · 0-4/4-8/8-12/12-15s · Push-In→Low Orbit→Macro→Pull-Back · 볼트 앵커 선두 · 슬롯별 [부분 구제]
- 🖱 `Workspace.razor` — 💯/🎂/☀️ 프리셋 → 스토리보드 4슬롯 일괄 적용(`ApplyDirectorShots`)
- 🧩 DI — `MainForm.cs` · `MauiProgram.cs` 양쪽 등록
- 🧪 `PlutoCustomGatewayAndMilestoneTests` 3건

## Next
- 게이트웨이 설정 UI(설정 화면 URL/모델/활성 토글 · Ping 진단) · `PublishViewModel` 캡션 경로 체인 결선
- 전체 스위트 재실행(메모리 확보 시) · 모바일 빌드 검증(Android SDK 확보 시)
