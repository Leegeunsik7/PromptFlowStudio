# Current Project Status
- Active Milestone: 【무엔진 Mock 폴백 차단 · Cloud Wan 2.1 자동 전환 · StockSuite 프로젝트 폴더 · 작업실 툴바 줄바꿈 정리】
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Mobile `net8.0-ios` CoreCompile 0W/0E · ✅ Tests 0W/0E
- Tests: ✅ 1141/1141 PASS (전체 스위트, Release, 14m51s) · `EngineRoutingAndProjectExportTests` 10/10 (신규)
- Updated: 2026-11-15 (KST)

## Completed
- ☁️ `VideoEngineRenderExecutionEngine` — ComfyUI 모드 + 로컬 미구성/오프라인 → 구성된 DashScope 엔진 자동 전환(`engine.notice` 안내)
- ⛔ `BlockSilentMock`(데스크톱) — 명시 Mock 외 무엔진 시 컬러바/링 Mock 생성 없이 `NoEngineMessage` 한국어 배너로 Failed
- 🌙 15초 엠비언트 — 단일 컷 5초 마스터 → 15초 핑퐁 조립 경로 Cloud 자동 전환 시에도 동작 · 모드 배지 · 입력 누락 안내
- 📁 `StockSuiteExporter.Project` — `{슬러그}_{yyyyMMdd}` 하위 폴더 + `project_manifest.json`(SNS·이력) · 이름 비우면 기존 루트 라이브 교체
- 🧩 Workspace: 헤더 3단(기본+상태 배지 / Pluto 2열 스트립 / 도구 행) · 칩 `nowrap`+`flex-shrink:0` · 캔버스 툴바 `flex-wrap` · 프로젝트 폴더 입력 · `Wan API ● Cloud 연결됨`

## Next
- 실제 앱에서 좁은 창 폭(≤1100px) 헤더 레이아웃 육안 확인
- ComfyUI 온라인 판정은 헬스 체크 스냅샷 기반(미점검 시 로컬 설정 우선) — 첫 렌더 직전 즉시 핑 검토
