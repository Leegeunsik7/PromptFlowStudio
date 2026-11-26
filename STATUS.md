# STATUS (2026-11-26)

## 최근 작업
- 🧠 플루토 자기학습 비디오 디렉터 메모리: `Services/AI/PlutoVideoLearningMemoryService`
  - 영속 `%LocalAppData%\PromptFlowStudio\pluto_director_memory.json`(주입 경로 ctor · 원자적 저장 · 손상 → seed 복구)
  - seed 5종(피사체/의상 · eye-level/트래킹 · 순방향 인과 물리 · 안티 왜곡 네거티브 · 앰비언트 로우키)
  - `GetLearnedGuidanceForPrompt` · `RecordPositiveOutcome`(👍/내보내기 → 레시피 강화) · `RecordFailureAndGetRefinement`(👎 사유 4종 → 회피 규칙 + 즉시 보정본)
- 프리플라이트 연동: `PromptPreflightDirector.RefineLocal/BuildInstruction/FromAi` 학습 가이던스 오버로드 · `GeminiPromptService.AnalyzeAndRefinePromptAsync(..., learned)` (Gemini 지시문 + 로컬 폴백 양쪽)
- 작업실: `🧠 플루토 학습 패턴 N건 적용됨` 배지 · 라이브 비디오 캔버스 `👍 잘 나옴 (학습)` / `👎 어색함 (보완 재생성)`(사유 칩 · 즉시 재생성 토글)
- DI: `MainForm.cs` · `MauiProgram.cs` 동일 팩토리 등록(패리티)
- 테스트: `PlutoVideoLearningMemoryTests`(격리 경로 영속 · 👎 → 유사 프롬프트 보정 변화 · 👍 강화 · AI/오프라인 주입 · DI 패리티 · Pluto 9/6/2)

## 검증
- Release `-warnaserror -p:NuGetAudit=false` 경고 0 / 오류 0 (Desktop · Tests · bin/Release — 앱 미실행)
- xUnit 1582/1582 통과(exit 0) · Pluto 9/6/2 유지 · Mobile 빌드 미실행(MAUI 워크로드 범위 외)
