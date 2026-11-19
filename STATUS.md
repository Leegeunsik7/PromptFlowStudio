# Current Project Status
- Active Milestone: 【Step 16 — 전 모달리티 파레토 귀속 · 시리즈 아크/편 간 유지율 연속성 · 자가 종료 회로 카운트다운 · 자율 하드닝】 ✅ 완료
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⏳ Mobile net8.0-ios — `ios` 워크로드 미설치(이번에도 미검증 · DI 결선은 소스 단언으로만 확인)
- Tests: ✅ 1227/1227 — 전체 단일 실행 통과(21분 19초 · `dotnet test` 종료 코드 0 · 기존 1219 + Step 16 8)
- Updated: 2026-11-20 (KST) · 기준 `98f08e2`

## Completed — Step 16
- ⚖️ 전 모달리티 파레토: BGM 무드 · 스타일 태그도 (3초 · 완주 · CTR) EWMA 귀속(`objectiveOnlyTokens` — 기대치/드리프트 통계 불변 · A/B 변형 포함) → 훅 · 룩과 같은 비지배 티어 · 최약 축 보너스 · 클릭베이트 강등 · 태그 '#' 변형 병합 · 구버전 `pluto_trend_memory.json` 완전 호환
- 📺 `PlutoSeriesArcMemoryService`(`pluto_series_arc.json` · MainForm + MauiProgram 싱글턴): 카탈로그 `시리즈 n/N편` + 제목 테마에서 시리즈 키 유도(스키마 변경 없음) · 유지 지수 = (3초 + 완주)/2 · 편 간 변화/기울기 · −5p 이하 이탈 → 궁금증 훅 + 클리프행어 아웃트로 Lv1~3 · 앵커 룩/BGM 유지 · 폐루프가 편 성과 자동 귀속
- 🎬 Workspace 편 불러오기: 앵커 룩 고정 · 앵커 BGM 선택(내 BGM/수동 선택 보존) · 이탈 시 1막 훅/마지막 자막 반영 · 📺 연속성 배지
- 🧠 두뇌 리포트 본문: ⚖️ 전 모달리티 균형 · 📺 시리즈 아크(상단 3행 헤더 불변)
- ⏱️ `ProviderCircuitCountdown` + `ProviderCircuitBreaker.PromoteExpired()`: Open/HalfOpen 이 있을 때만 1초 `PeriodicTimer` · 냉각 만료 시 호출 없이 HalfOpen 전이 · 전부 Closed 면 즉시 정지·폐기 · HalfOpen 대기 틱은 UI 통지 생략
- ⏳ 자율 ①: 다목적 근거 신선도(유예 14일 + 반감기 28일 · `ObjUtc`) — 클릭베이트 판정이 영구 낙인(흡수 상태)이 되지 않음 · 공백 뒤 새 관측은 재기준
- 🧯 자율 ②: 반개방 탐침 임대(`ProbeLeaseTimeout` 10분) 회수 + 엔진 `BuildRequest` 를 회로 허가 이전으로 이동 — 보고 누락으로 회로가 영구 반개방에 갇히는 경로 차단

## Notes
- ⚠️ 3편 시리즈에서 에스컬레이션이 걸리는 편은 사실상 3편(완결)뿐 → 클리프행어 아웃트로는 4편 이상 시리즈에서만 실제로 붙음(완결편은 궁금증 훅만)
- ⚠️ 같은 테마 · 같은 편수로 시리즈를 다시 만들면 같은 아크로 합쳐짐 · 연속 렌더 중 직전 편 카탈로그 기록이 늦으면 앵커가 한 편 늦게 잡힐 수 있음
- ⚠️ `pluto_series_arc.json` 은 Drive 저장소 관리자 경유이나 핵심 허브 8종(검증·새 PC 복원 목록)에는 미포함
- ⚠️ `.git`은 `D:\Program` — git 명령에 `--git-dir=D:\Program\.git --work-tree=D:\Program\PromptFlowStudio` 필요

## Next
- `ios` 워크로드 설치 후 Mobile 빌드 재검증 · 시리즈 아크 Drive 허브 편입(9종) · 완결편 전용 아웃트로(정주행 유도) · 회로 차단 HUD(`gth-v`) 카드
