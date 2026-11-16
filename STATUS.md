# Current Project Status
- Active Milestone: 【Step 5 — A/B 변형 영상 결선 + 두 변형 연속 렌더 + 미리보기 + A/B 짝 내보내기】 ✅ 완료
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Mobile net8.0-ios CoreCompile 0W/0E · ✅ Tests Release 0W/0E
- Tests: ✅ 1169/1169 PASS (전체 스위트, Release, `--no-build`) · 신규 3건
- Updated: 2026-11-16 (KST)

## Completed (Step 5)
- 🎞️ `PlutoExperimentLedgerService` — `ChampionVideoPath`/`ChallengerVideoPath`(구 장부 역호환) · `AttachVariantArtifacts` · `GetExperimentArtifacts` · Pending→Evaluated 전이 후 보존
- ⚡ `Workspace.razor` — `[⚡ A/B 두 변형 연속 렌더링]`(1/2 Champion A → 2/2 Challenger B · 변형별 출력 경로) · 수동 렌더 시 활성 변형 자동 결선 · `[▶ A안/B안 미리보기]`(판정 카드 공용)
- 📦 `StockSuiteExporter.ExportABExperimentToStockSuiteAsync` — `{테마}_AB_{yyyyMMdd}\VariantA|VariantB` + 번들 + `ab_experiment_manifest.json` · 무결성 검증 · [부분 구제]
- 🧪 `Pluto44ExperimentAndSeriesTests` 신규 3건

## Completed (Step 4)
- 편지 자막 도메인 격리 + 아기 모드 리셋 + 네거티브 미리보기

## Notes
- `*IsolatedTests` 실시간 프레임 예산 테스트는 타 프로젝트 테스트 동시 실행 부하 시 간헐 실패 → 유휴 재실행 전량 통과

## Next
- 미정
