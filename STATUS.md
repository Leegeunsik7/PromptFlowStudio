# Current Project Status
- Active Milestone: ✅ Step 28 — 사후 분석 반감기·유기적 복권 · 렌더 전 셋업 예측 배지 · 통합 Pluto 진단 스냅샷 완료
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⏸ Mobile net8.0-ios — `ios` 워크로드 미설치(미검증)
- Tests: ✅ 1318/1318 — 전체 단일 실행 통과(20분 58초 · 종료 코드 0) · Step 28 신규 `PlutoRehabilitationAndForecastTests.cs` 12건
- Updated: 2026-11-23 (KST) · 기준 `1961336`

## Completed — Step 28
- ⏳ 오답 노트 엔트리별 반감기 — 자동 사후 분석 30일(`Origin=postmortem`) · 피드백 21일 · 레거시 JSON(출처 없음)은 조합 키 + `brief-postmortem:` 사유로 추론
- 🌱 유기적 복권 — 조회 ≥ 50 · SurpriseDelta ≥ +8p 클립의 `pair:`/`domain_hook:` 벌점 × 0.35(클립당 1회 · ID 영속) · 커스텀 클립은 leave-one-out 기대치
- 🔮 `EvaluateSetupForecast(theme, hook, look, bgm, renderMode)` — 0~100 예상 · 신뢰 등급 · 활성 가드 · 안전 룩 → `Workspace.razor` 헤더 밖 배지 + [🔁 안전 룩 교체]
- 🩺 `PlutoSystemDiagnosticService`(DI 양쪽 Singleton) — Drive 9 허브·충돌 · 실측 YouTube·적중률 · 4축 파레토·프레이밍 · 오답 가드 잔여 벌점 Markdown → `Settings.razor` [🩺 플루토 진단 리포트 복사]
- 🧠 자체 추가: ⏱️ 인과 가드(벌점 이전 렌더 클립 복권 불가) · 🧮 사후 분석된 기획 결정의 형제 편 복권 차단 · 🔁 출처 전환 시 이전 반감기로 재기준(벌점 연속) · 🚫 룩으로 해소 불가 차단(domain_hook·훅·BGM)엔 교체 미제안 · 🔒 진단 스냅샷 절대 경로 가림 + 표 셀 이스케이프

## Notes
- ⚠️ `.git` 은 `D:\Program` — `--git-dir=D:\Program\.git --work-tree=D:\Program\PromptFlowStudio` 필요
- ⚠️ 성능·타이밍 테스트는 부하에 민감 — 전체 실행 전 `dotnet build-server shutdown`(VBCSCompiler 상주 시 간헐 실패 관측)
