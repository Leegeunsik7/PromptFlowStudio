# Current Project Status
- Active Milestone: 【Step 12 — UCB1 밴딧 탐색 · 반감기 시간 감쇠 · 2-way 조합 귀속 · 자가 감사 수정】 ✅ 완료
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⏳ Mobile net8.0-ios — `ios` 워크로드 미설치(NETSDK1147)
- Tests: ✅ 1192/1192 — 전체 단일 실행 통과(14분 55초 · 기존 1184 + 신규 8)
- Updated: 2026-11-18 (KST)

## Completed (Step 12 · 기준 `08920c9`)
- ⏳ 반감기 감쇠: 성과 패턴 보상 14일(`PlutoLearningService.PatternHalfLifeDays` · 초과 보상·시행 수 감쇠) · 오답 노트 벌점 14→21일 · `AsOfUtc`/주입 시계로 재현 가능
- 🎰 UCB1: `ComputeUcbScore = mean + C·√(ln(total+1)/(arm+1))` · `RankForExploration` → A/B 챌린저 B는 덜 검증된 훅·룩 · 트렌드 기획안 마지막 1건은 탐색(이력 있을 때만)
- 🔗 2-way 귀속: `pair:{Hook}+{Look}` · `domain_hook:{Domain}+{Hook}`(`PlutoPairAttribution.cs`) — 다른 조합/도메인 성공 이력이 있는 훅의 실패는 조합 키에만 벌점 격리 · 시너지 조합은 랭킹/A/B 부스트
- 🧠 두뇌 리포트 카드 본문: 최강 시너지 조합 · UCB1 탐색률/후보 수/시행 수 · 오답 노트 감쇠 현황(상단 3행 헤더 불변)
- 🩹 자가 감사 ①: 폐루프 중복 차단 장부 — 읽기 실패(Drive 잠금)를 빈 장부로 오인해 덮어쓰던 결함 → 읽기 전용 세션으로 디스크 장부 보존
- 🩹 자가 감사 ②: 오답 노트 — 감쇠로 해제됐어야 할 낡은 차단 래치가 조회 없이 새 벌점을 받으면 재차단되던 결함 → 가산 전 래치 정산
- 🩹 테스트 결정성: `Pluto44ExperimentAndSeriesTests` 가중치 정확 비교에 고정 시계 주입(실시간 감쇠로 부하 시 9자리 비교 실패)
- 신규 테스트: `PlutoBanditAndSynergyIntelligenceTests.cs` 8건

## Notes
- ⚠️ 미해결: 빌드 직후 전체 실행에서 testhost가 `ntdll.dll` 0xc0000005로 시작 4~12초 내 중단 2회(이후 14회 재시도 재현 불가 · 원인 테스트 미특정)
- `SingleFileBinaryProcessBootstrap_Check`는 부하 시 간헐 실패(기동 13.6초) — 유휴 재실행 통과
- ⚠️ `.git`은 `D:\Program`에 있고 소스는 `D:\Program\PromptFlowStudio` — git 명령에 `--git-dir=D:\Program\.git --work-tree=D:\Program\PromptFlowStudio` 필요

## Next
- testhost 네이티브 크래시 원인 추적(덤프 수집) · `.git` 위치 정리 · `ios` 워크로드 설치 후 Mobile 빌드 재검증
