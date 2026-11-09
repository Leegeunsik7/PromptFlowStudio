# Current Project Status

- Active Milestone: 【Pluto Trend Lab】 🪐 내보내기 학습 루프 · 📱 SNS 실필터 · 🧠 Pluto 레벨 HUD
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (Mobile: 로컬 Android SDK 부재로 미빌드)
- Tests: 전체 915/915 PASS (MaxCpuCount=2)
- Updated: 2026-11-09 (KST)

## Completed
- 🪐 Export 학습 루프 — 트렌드 랩 `SendPlutoTrend` → Workspace `PubVm.ActivePlutoTrendId` → 발행/StockSuite/멀티 패키지 성공 시 `ExportCompleted`(+0.15) 1회 기록 후 소거
- 📱 `StockSuiteExporter.Social.cs` — Shorts `loudnorm=I=-14:LRA=7:TP=-1.5` · Reels `noise=alls=8:allf=t+u` + `-r 30` · 발행 전 렌더(실패 시 원본 [부분 구제]) · 멀티 패키지 쇼츠 규격 결합
- 🧠 `PlutoLearningService` — 유형별 카운트 영속 · XP(채택1·내보내기3·👍1) · `CurrentLevel/CurrentXP/NextLevelXP/TotalInteractions`
- 🗂️ `/pluto-trends` 상단 🧠 AI 두뇌 HUD(레벨·XP 진행 바·피드백 횟수)
- 🧪 `PlutoTrendIntelligenceTests` +3건

## Next
- 모바일 빌드 검증(Android SDK 확보 시)
