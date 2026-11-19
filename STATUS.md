# Current Project Status
- Active Milestone: 【Step 13 + Step 14 — 레짐 드리프트 · 메타 보정 · 정합성 게이트 · 체크포인트 자가 치유 · 회로 차단】 ✅ 완료
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⏳ Mobile net8.0-ios — `ios` 워크로드 미설치(NETSDK1147)
- Tests: ✅ 1210/1210 — 전체 단일 실행 통과(13분 46초 · `dotnet test` 종료 코드 0 · 기존 1192 + Step 13 8 + Step 14 10)
- Updated: 2026-11-19 (KST)

## Completed — Step 13 (기준 `59c4401`)
- 🛡️ NVIDIA `RTWorkQ` 비동기 MFT 해제 후 콜백 0xc0000005 근본 수정(`MediaFoundationCodecs.cs` · DRAIN→FLUSH→Shutdown→유예 Release · `docs/lessons_learned.md` §9)
- 📐 (Domain × RenderMode) 컨텍스트 잔차 알파 · 🎯 베이지안 사후 신뢰 구간 · 🌊 `PlutoRegimeDriftDetector.cs`(Page-Hinkley · 평균 수축 사전 · 탐색 서지)

## Completed — Step 14
- 🎯 `PlutoMetaCalibrationService.cs`: (예측 · 실현 · 최선 대안) 기록 → Brier/RMSE · 누적/평균 반사실 후회 → UCB1 탐색 배수 ×0.6~2.0(`pluto_calibration_state.json` · Drive 허브 등록 · DI 양쪽)
- 🧩 `PlutoPreRenderCoherenceGuard.cs`: 네거티브 도메인 누출·중복·빈/중복 자막 자동 보정 + 역시너지·9:16 폭·읽기 속도 경고 → Workspace 3경로 결선 + 배지
- 🩹 `CutRenderCheckpointService`: 재사용 전 `EnsureValidMp4` · 손상본 격리(quarantine 5개) · `PruneStaleCheckpoints`(TTL + LRU · 30개 · 2GiB · 고아 tmp)
- 🧠 두뇌 리포트 본문: 보정 정확도 · 반사실 후회(상단 3행 헤더 불변)
- ⚡ 자율 ①: `ProviderCircuitBreaker.cs` — 일시 실패 3연속 → 30초~5분 차단 · 탐침 복구 · 체크포인트는 계속 제공
- ⏱️ 자율 ②: 타이밍 결정성 — 스모크 기동 예산을 자식 CPU 시간으로 · `CiFrameBudget.ContentionFactor()` 부하 정규화(스플랫·커머스) · MFT Dispose 경합 테스트 해제 대기
- 💸 자율 ③: 후회 귀속(가장 아쉬운 대안) · A/B 실측 반사실 · 관측 이전 예측 스냅샷(`ForecastExpectancy`)

## Notes
- ⚠️ 부하 정규화는 2개 테스트만 적용 — 고정 `CiFrameBudget.Ms` 단언 약 50곳은 외부 부하(65%+)에서 간헐 실패 가능(이번 세션 전체 실행 3회 중 2회 실패 후 수정)
- ⚠️ `.git`은 `D:\Program` — git 명령에 `--git-dir=D:\Program\.git --work-tree=D:\Program\PromptFlowStudio` 필요

## Next
- 나머지 프레임 예산 단언에 `ContentionFactor()` 확대 · 회로 차단 상태 HUD 카드 · `ios` 워크로드 설치 후 Mobile 빌드 재검증
