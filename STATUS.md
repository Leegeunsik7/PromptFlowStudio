# Current Project Status
- Active Milestone: 【Pluto 오답 노트(자율 회피 엔진)】 검증 완료 + 생성형 엔진 부정 제약 결선
- Build: ✅ Release `-warnaserror` 0/0 (PromptFlowStudio · Tests) — No-Build Mode 해제
- Tests: ✅ 1077/1077 PASS (Pluto 대상 필터 75/75)
- Updated: 2026-11-13 (KST)

## Completed
- ✅ `a90fe6a`(RSS 페처 · Drive 소셜 레이더) · `d43bec1`(오답 노트) 빌드/테스트 검증 — 수정 사항 없음
- 🛡️ `PlutoGenerativeEngine`: 선택 끝 파라미터 `IPlutoMistakeNotebookService? mistakeNotebook`(하위 호환) · `BuildLlmTrendInstruction()` — 차단 토큰 존재 시 `Avoid motifs/styles: …` 를 LLM 인스트럭션 말미에 덧붙임(예외 격리 · 로드 실패 [부분 구제])
- DI 양쪽(MainForm/MauiProgram) 생성형 엔진에 오답 노트 주입
- 🧪 `GenerativeEngine_AppendsNegativeConstraint_WhenTokensSuppressed` 추가

## Notes
- TrendLab/LearningService는 DI 싱글턴 경유로 자동 결선(별도 수정 불요)
- Mobile(MAUI) 빌드 미검증
