# STATUS (2026-11-26)

## 최근 작업
- 🛡️ 도메인 무관 프리플라이트 프롬프트 디렉터: `GeminiPromptService.AnalyzeAndRefinePromptAsync` + 로컬 규칙 `PromptPreflightDirector`
  - 동작-길이 보정(5s=2 · 10s=3 · 15s=4 · 30s=6 비트, 첫/결과 동작 보존 · 묘사 절 보존 · 반복 제거) · 카메라/피사체/조명 앵커(앰비언트 = 로우키)
  - 인과·물리 연속성 · 안티 아티팩트 네거티브 중복 없는 병합 · 멱등 · Gemini JSON 스키마 검증 실패/키 없음/8초 초과 → 로컬
  - 작업실: `🛡️ AI 프롬프트 자동 보정` 토글(기본 ON) · 1줄 요약 배지 · 🔍 비교 · ↩ 원문 1클릭(입력 박스 비영속)
- 30초 자동 2막 분할: `MultiDurationPlanner.SplitTwoActs` — 1막(0~15s 셋업) / 2막(15~30s 클라이맥스) 동작 절 분배, 묘사/앵커 공통
- 하드 컷 기본: `CutTransitionMode.IndependentHardCut`(0.0s concat) · 0.25s 디졸브는 명시 선택 · `StitchByTransitionAsync`
- 테스트: `UniversalPreflightAndTwoActTests`(7개 장르 · AI 모의/오프라인 · 2막 · 하드 컷 · Pluto 9/6/2)

## 검증
- Release `-warnaserror -p:NuGetAudit=false` 경고 0 / 오류 0 (Desktop · Tests · bin/Release — 앱 미실행)
- xUnit 1568/1568 통과(exit 0) · Pluto 9/6/2 유지 · DI 변경 없음
