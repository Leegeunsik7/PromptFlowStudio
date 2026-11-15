# Current Project Status
- Active Milestone: 【Pluto 4.4 — 짝지은 A/B 실험 평가 장부 · 멀티 에피소드 시리즈 분할기】
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Mobile `net8.0-ios` CoreCompile 0W/0E · ✅ Tests 0W/0E
- Tests: ✅ 1131/1131 PASS (전체 스위트, Release, 13m55s) · `Pluto44ExperimentAndSeriesTests` 4/4 (신규)
- Updated: 2026-11-15 (KST)

## Completed
- 🏆 `IPlutoExperimentLedgerService` — 합성 점수(0.5·3초 + 0.3·완주 + 0.2·CTR×10) 승자/무승부 · LiftPct · `pluto_experiment_ledger.json` 원자 기록 · 재판정 차단
- ⚖️ `IPlutoClosedLoopReinforcementService.IngestExperimentOutcomeAsync` — 승자 차등 특성만 부스트 · 패자는 3초 < 42% 때만 오답 노트 벌점
- 📚 `IPlutoSeriesBatchPlanner` — 10~30장 → 서로소 2~3편(편당 3~5컷) · "n/N편" 연속 자막 · 편별 SNS 키트 · 초점 [0.2,0.8]²
- 🩺 `PlutoDriveHealthService` 추적 파일 6+2 (실험 장부 포함)
- 🧩 Workspace: [🏆 A/B 실험 승자 판정 & 차등 학습] · [📚 시리즈 자동 분할 (2~3편)] + 편 불러오기 · 전편 순차 출력 · DI 양쪽 등록

## Next
- 성과 학습 HUD 카드(실험 승률 · XP 증분 · 차단 훅) 검토
- 시리즈 편별 16:9 + 9:16 듀얼 마스터 일괄 출력 검토
