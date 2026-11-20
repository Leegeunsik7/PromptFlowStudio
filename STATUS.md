# Current Project Status
- Active Milestone: ✅ Step 21 — 재적재 대기 쓰기 펜스(잠금 중 핫 리로드 후 낡은 메모리 덮어쓰기 차단) 완료
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⚠ Mobile net8.0-ios — `ios` 워크로드 미설치(미검증)
- Tests: ✅ 1261/1261 — 전체 단일 실행 통과(12분 8초 · 종료 코드 0 · 기존 1259 + Step 21 2)
- Updated: 2026-11-20 (KST) · 기준 `d032a2d`

## Completed — Step 21
- 🛡️ 재적재 대기 쓰기 펜스 `_hasUnappliedDiskUpdate` / `HasUnappliedDiskUpdate` — 핫 리로드 6종 싱글턴 전체 적용:
  `PlutoExperimentLedgerService` · `PlutoMistakeNotebookService` · `PlutoRenderCatalogService` · `PlutoSeriesArcMemoryService` · `PlutoLearningService` · `PlutoMetaCalibrationService`
- 승격 후 읽기 실패(잠김) 시 펜스 설정 → 디스크 병합분 미반영 상태의 메모리가 다음 기록에서 덮어쓰지 않음 · 재적재 성공 시 해제
- 🧪 `PlutoHotReloadAndLiveDriveHardeningTests.cs` 다중 서비스 단위 테스트 2건 추가
- Step 20 Notes의 '승격 후 읽기 실패 시 병합분 덮기' 위험 해소

## Notes
- ⚠️ `.git` 은 `D:\Program` — `--git-dir=D:\Program\.git --work-tree=D:\Program\PromptFlowStudio` 필요
- ⚠️ 동시 세션의 외부 `testhost` 종료 시 전체 실행 중단(크래시 이벤트 없음) — 전체 실행은 조용한 머신에서
