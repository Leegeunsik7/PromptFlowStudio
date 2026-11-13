# Current Project Status
- Active Milestone: 【Pluto 오답 노트(자율 회피 엔진)】 👎 패턴 벌점 · 14일 반감기 · 슈미트 트리거 히스테리시스(차단 ≥1.8 / 해제 <0.8) · Drive 동기화
- Build: ⚠️ **No-Build Mode** 커밋(2026-11-13) — 로컬 빌드/테스트 미실행 · 신규 PC에서 Release `-warnaserror` + 전체 테스트 검증 필요
- Tests: 신규 `PlutoMistakeNotebookTests` 4건 작성(미실행) · 직전 미검증분 `PlutoWebTrendFetcherTests` 11건 포함 · 마지막 확인 1061/1061 PASS
- Updated: 2026-11-13 (KST)

## Completed (미검증)
- 🛡️ `PlutoMistakeNotebookService`(`IPlutoMistakeNotebookService`): `pluto_mistake_notebook.json`(IPlutoStorageManager 원자 기록 · 읽기 불가 세션 덮어쓰기 금지) · 👎 1.0 / 소프트 스킵 0.4 / 👍·적용 상쇄 1.2(하한 0) · P_eff = P_raw×2^(−Δd/14) · `GetSuppressedTokens` · `BuildNegativeConstraintPrompt` · `ClearAllAsync`
- 🪐 `PlutoLearningService.MistakeNotebook`(init) · `RecordCardFeedbackAsync`(태그·카메라·BGM 토큰 전달) · `RecordSoftSkipAsync`
- 🔥 `PlutoTrendAnalyzer.Analyze(…, mistakes = null)` 차단 태그 제외 · `MatchTags`/`TagForModifier`/`SafeSuppressed` · 수확기 결선
- 🗜 `PlutoPromptCompressor.BlendViralModifiers(…, mistakes = null)` · `Compose(…, mistakes = null)` · `AppendNegativeConstraint`(`avoid:` 압축 라벨)
- 🎬 `PlutoCopilot.MistakeNotebook`(미주입 → Learning 노트) — 바이럴 모디파이어 필터 + LLM 요청 부정 제약
- 🧪 TrendLab: `🛡️ 오답 노트 회피 활성: N개 패턴 차단 중` 배지 + `[초기화]` · 평가 없이 재발굴 → 소프트 스킵
- DI 양쪽(MainForm/MauiProgram) 싱글턴 + Learning 주입

## Notes
- Analyzer/Compressor는 static → "생성자 주입" 대신 선택 끝 파라미터로 하위 호환
- PlutoGenerativeEngine(LLM 카드 합성) 요청에는 부정 제약 미결선 · Mobile(MAUI) 빌드 미검증
