# Current Project Status
- Active Milestone: 【Pluto 4.5 — 영속 대기 A/B 실험 큐 · 시리즈 원클릭 렌더+내보내기 · Pure-BCL MP4 무결성 가드】
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Mobile net8.0-ios CoreCompile 0W/0E · ✅ Tests 0W/0E
- Tests: ✅ 1147/1147 PASS (전체 스위트, Release, 14m34s) · 신규 3건
- Updated: 2026-11-16 (KST)

## Completed
- 📋 `PlutoExperimentLedgerService` — `RegisterExperiment` 즉시 `Pending` 영속(플랜 전 필드·자막 포함, 디스크 병합 후 원자 기록), `GetPendingExperiments`, 판정 시 `Evaluated` 전이 · 인터페이스에 `EnsureLoadedAsync` 노출
- 📋 Workspace `[📋 대기 중인 A/B 실험 선택]` — 판정 폼 공용 RenderFragment, 재시작 후 현재 실험 없이도 대기 실험 카드로 판정 가능
- 🎬 Workspace `[🎬 시리즈 전편 연속 렌더링 + 일괄 내보내기]` — "시리즈 연속 렌더링 중 (n/N편)..." 진행 표시 → `ExportSeriesBatchToStockSuiteAsync`
- 🛡️ `ExportMediaIntegrityGuard` — FileStream 전용 ISO BMFF 박스 순회(≥1024B · ftyp 선두 · moov 필수 · EOF 초과 감지), 단일/시리즈 복사 전 차단(손상 편은 [부분 구제])
- 🧪 `ExperimentLedger_PendingExperiment…`, `ExportMediaIntegrityGuard_Rejects…`, `StockSuiteExporter_CorruptVideoFile…` · 기존 더미 영상 → `BuildMinimalMp4`

## Next
- 실제 앱에서 재시작 후 대기 실험 복원 · 시리즈 원클릭 렌더 흐름 확인
