# Current Project Status
- Active Milestone: 【Step 15 — Drive 허브 8종 · 능동 후회 리허설 · 파레토 다목적 랭킹 · 스위트 전역 부하 인지 예산 · 자율 하드닝】 ✅ 완료
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⏳ Mobile net8.0-ios — `ios` 워크로드 미설치(NETSDK1147 · 이번에도 미검증)
- Tests: ✅ 1219/1219 — 전체 단일 실행 통과(12분 10초 · `dotnet test` 종료 코드 0 · 기존 1210 + Step 15 8 + 감사 보완 1)
- Updated: 2026-11-20 (KST) · 기준 `476edb7`

## Completed — Step 15
- ☁️ Drive 핵심 허브 8종(`pluto_calibration_state.json` 포함 · `CorePlutoFileCount`) — 검증·미러·새 PC 복원(로컬 덮어쓰기 없음) · `PROMPTFLOW_PLUTO_DRIVE=off` 격리 테스트 고정
- 🔁 능동 후회 리허설: `GetRegretRecoveryBoosts()`(미해소 잔액 ≥ 0.10 · 부스트 ≤ 0.50 · 14일 반감기 · 오답 노트 차단 제외) → `PlutoVariantABGenerator` 챌린저 B 배정(`hook:`/`look:`/`pair:`) → 실측되면 폐루프가 소거 · A/B 실측 반사실은 미적립
- ⚖️ `PlutoParetoFrontier.cs`: 패턴별 (3초 · 완주 · CTR) EWMA → 비지배 티어 + 최약 축 보너스(≤ +15%) · 클릭베이트(3초 ≥ 65% ∧ 완주 < 20%) 학습 부스트 무효 + ×0.70 · 근거 없는 후보 점수 불변
- 🧠 두뇌 리포트 본문: ⚖️ 파레토 선두 · 🔁 후회 리허설(상단 3행 헤더 불변)
- ⏱️ `CiFrameBudget.Ms/MsF` → 부하 인지 `FrameBudget`(기준 미만 즉시 통과 · 초과 시에만 계측 · 유휴 폭 1.15 이하 = 배수 1.0 · 상한 ×3 · 500 ms 캐시) — 프레임 단언 전부 + µs/포스트 하위 예산 `Scaled()`
- ⚡ 자율 ①: `ProviderCircuitBreaker.RequestProbe/Reset/StateChanged` + Workspace 차단 배지 · [🔎 지금 탐침] · [♻️ 차단 해제]
- 🛡️ 자율 ②: 보정 상태 `.bak` 마지막 정상본 복구(0 B · 잘린 JSON) + 읽기 전용 세션 자가 치유(잠금 해제 후 디스크 상태 위에 세션 관측 재생)
- 🔍 감사 보완(`d5ab93f` 이후): `ProviderCircuitBreaker` DI 공유 싱글턴(MainForm + MauiProgram → 엔진 주입 · Workspace DI 우선 해석) · '가장 아쉬운 대안' = 미해소 잔액 기준(검증/감쇠된 대안 고착 해소) · 잔여 무조건 `ContentionFactor()` 2곳 조건부화 · "7대" → "8대" 주석 · 비테마 리허설 챌린저 테스트

## Notes
- ⚠️ Step 14 테스트 1건(`MetaCalibration_PersistsRollingWindow_…`)의 손상/잠금 구간을 신규 동작(.bak 복구 · 자가 치유)에 맞게 조정
- ⚠️ 부하 계수는 '프로세스 밖' 선점만 잰다 — 같은 프로세스 내 병렬 테스트 경합은 흡수하지 않음(현재 컬렉션 병렬 off)
- ⚠️ 파레토는 훅·룩만(BGM·태그는 다목적 귀속 없음) · 회로 배지의 남은 냉각 초는 재렌더 때만 갱신
- ⚠️ `.git`은 `D:\Program` — git 명령에 `--git-dir=D:\Program\.git --work-tree=D:\Program\PromptFlowStudio` 필요

## Next
- `ios` 워크로드 설치 후 Mobile 빌드 재검증 · 회로 차단 HUD(`gth-v`) 카드 · A/B 후회 라벨을 조합(`pair:`) 단위로 발행
