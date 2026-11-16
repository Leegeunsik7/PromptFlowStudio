# Current Project Status
- Active Milestone: 【Step 7 — A/B 패자 실패 차원 정밀 분류 + 16:9·9:16 듀얼 마스터 프로젝트 번들】 ✅ 완료
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Mobile net8.0-ios CoreCompile 0W/0E · ✅ Tests Release 0W/0E
- Tests: ✅ 1175/1175 (전체 1171 통과 + 부하 민감 `*IsolatedTests` 3건 PlutoCore.E2E 종료 후 재실행 통과 + 신규 1건 입력 보정 후 통과) · 신규 3건
- Updated: 2026-11-17 (KST)

## Completed (Step 7)
- 🩺 `PlutoExperimentLedgerService.ClassifyLoserFailureDimension` — 3초 42 % · 완주 30 % · CTR 1.5 % 상대 결손 최대 → HookRetention/CompletionDrop/LowCtr · `PlutoExperimentReinforcementRequest.LoserFailureDimension`(null → HookRetention 하위 호환)
- 📦 `StockSuiteExporter.ExportDualMasterBundleToStockSuiteAsync` — `{슬러그}_Dual_{yyyyMMdd}\` 16:9 + 9:16 영상 · subtitles.srt · SNS_게시문.txt · thumbnail.jpg(best-effort, 9:16) · project_manifest.json(`IsDualMaster`) · 양 영상 무결성 가드(폴더 생성 전 차단)
- 🖱️ `Workspace.razor` — 듀얼 마스터 성공 시 `[📦 듀얼 마스터 프로젝트 폴더 내보내기]`
- 🧪 `EngineRoutingAndProjectExportTests` 신규 3건

## Notes
- A/B 패자 벌점 트리거·크기는 여전히 3초 유지율 결손 기준(차원 라벨만 정밀화)
- `*IsolatedTests` 실시간 프레임 예산 테스트는 타 프로세스(PlutoCore.E2E 등) 부하 시 간헐 실패 → 유휴 재실행 전량 통과

## Next
- 미정
