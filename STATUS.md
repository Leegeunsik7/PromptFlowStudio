# Current Project Status
- Active Milestone: 【Pluto 4.4.1 Polish — 첫 렌더 400ms ComfyUI 프로브 · 성과 피드백 자동 입력 · 시리즈 일괄 내보내기】
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Mobile net8.0-ios CoreCompile 0W/0E · ✅ Tests 0W/0E
- Tests: ✅ 1144/1144 PASS (전체 스위트, Release, 10m52s) · 신규 3건
- Updated: 2026-11-15 (KST)

## Completed
- ⚡ `VideoEngineRenderExecutionEngine.EnsureFastProbeAsync` — ComfyUI 모드 + 로컬·DashScope 둘 다 구성 + 15초 내 결과 없음 → 400ms `/system_stats` 프로브, 오프라인이면 첫 클릭부터 Cloud Wan 2.1로 전환 (`ComfyCheckedAt`·`ComfyProbe`·`Clock` 주입, MainForm 결선)
- 🧾 Workspace `CapturePerfContext` — 단일 영상·15초 앰비언트·모션·몽타주·듀얼 마스터 렌더 완료 시 VideoId/Hook/Look/BGM/Tags 자동 입력, 사진 2장 미만이어도 피드백 패널 토글 표시
- 📦 `StockSuiteExporter.ExportSeriesBatchToStockSuiteAsync` — `{테마}_{yyyyMMdd}\Ep01..` + 편별 `project_manifest.json`(자막·SNS) + `series_manifest.json`, 미렌더 편은 [부분 구제]로 처리, Workspace 시리즈 패널 버튼 추가
- 🧪 `RenderEngine_UnprobedOffline…`, `StockSuiteExporter_SeriesBatch…`, `Workspace_MountsWithAllPluto44Panels…`

## Next
- 시리즈 일괄 내보내기는 이번 세션에 렌더된 편만 대상 — 앱 재시작 후에는 편 영상 재렌더 필요
- 실제 앱에서 ComfyUI 오프라인 + 키 저장 상태로 기동 직후 첫 클릭 동작 확인
