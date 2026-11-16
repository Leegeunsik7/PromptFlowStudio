# Current Project Status
- Active Milestone: 【Step 3 — 도메인 인지 네거티브/자막 격리(BabyStudio vs GeneralCinematic) + 체크포인트 재사용 배지】
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Mobile net8.0-ios CoreCompile 0W/0E · ✅ Tests Release 0W/0E
- Tests: ✅ 1163/1163 PASS (전체 스위트, Release) · 신규 3건
- Updated: 2026-11-16 (KST)

## Completed
- 🧭 `PromptDomainClassifier` — 아기/육아 토큰(KO/EN) 또는 아기 칩 명시 선택 → BabyStudio, 그 외 GeneralCinematic · 일반 네거티브 `blurry, lowres, watermark, distorted geometry, flicker, text artifacts, overexposed` · 잔류 아기 얼굴 가드 제거
- 🖥 `Workspace.razor` — `_babyExplicit`(키워드·무드·성장 단계 클릭) · 단일 생성/큐 적재 네거티브 `EffectiveNegative` · 🎬 일반 시네마틱 네거티브 표시 · 몽타주 테마 힌트 BabyStudio 유지
- 🎬 `PlutoMontageStoryGenerator` — `cinematic` 중립 3막 템플릿 · 키워드 미일치 + GeneralCinematic 힌트 → 시네마틱(빈 힌트/아기 → 기존 everyday 유지)
- ♻️ `VideoEngineRenderExecutionEngine` — `CheckpointBadgeLabel` · `CheckpointNoticeOf` · `SummarizeCheckpointReuse`
- 🟢 `RenderQueue.razor` 작업 행 + `Workspace.razor` 미니 큐 완료 배너 — `♻️ 체크포인트 컷 재사용` 배지 + 상세
- 🧪 `DomainIsolationAndCheckpointUiTests` 3건

## Next
- `PlutoSubtitleDirector`(편지 자막) 도메인 격리 확장 검토
