# Current Project Status
- Active Milestone: ✅ Step 32 — 다국어 런타임 결선(로캘 자막 줄바꿈 · TTS 1.30x 적합/초과 경고 · 로캘 패키지 동작 시 변형 자동 보장 · 로캘별 제목 프레이밍 CTR 귀속 · 두뇌/진단/플래너 로캘 분해 · 8단계 자가진단)
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⏸ Mobile net8.0-ios — `ios` 워크로드 미설치(미검증)
- Tests: ✅ 1353/1353 — 전체 단일 실행 통과(20분 22초 · 종료 코드 0) · Step 32 신규 `PlutoMultiLanguageRuntimeIntegrationTests.cs` 9건
- Updated: 2026-11-23 (KST) · 기준 `082fa74` · 불변식 `CorePlutoFileCount=9` · `MergeableHubCount=6` · `CompanionSyncFileCount=2` 유지

## Completed — Step 32
- 💬 `PlutoLocaleSubtitleFitter`(렌더 직전 투영 · 편집 원문 불변) — 로캘 줄당 글자 · 금칙/문자소 줄바꿈 · 2줄 초과 컷 시간 비례 분할 · 로캘 음성 + 0.85x~1.30x 속도 · 초과 글자 수 정확 보고
- 🖥️ `Workspace.razor` — 로캘 TTS 적합/초과 칩(헤더 밖) · 몽타주/듀얼 마스터 렌더 로캘 자막 · 립싱크 로캘 음성 · 제목 복사/사이드카 전 `{Base}::{Locale}` 멱등 보장 · 교차 로캘 기획 1클릭 적용
- 🅰️🅱️ 로캘별 프레이밍 순위(`GetLocaleFramingRanking` · 로캘 귀속 ≥2 → 로캘 실측 · 미달 → 전역 폴백) · 로캘 패키지 선택은 해당 변형에만 기록
- 🧠 `PlutoLocalePerformanceRow` KO · EN · JA · ES 분해 → 두뇌 리포트 본문 · 진단 Markdown `### 🌐 로캘별 성과` · 플래너 `cross-locale-adaptation` 기획(오답 노트 가드 · 다양성 슬롯 비소모)
- 🧪 자가진단 8단계 — ja-JP 줄바꿈/재장식 · 변형 멱등 분기 · 로캘 사이드카 · `視聴を選択(%)` 수집 → 변형 연결
- 🛡️ 자체 추가: 용량 축출 보호(새 변형/마스터 보존 · 미게시 변형 우선 희생) · 로캘 변형 기획 채점 제외(콜드 채널 오답 노트 오염 차단) · 한국어 편 장식 누출 차단/EN 이중 언어 승격/번역 누락 경고 · 로캘 선택 오귀속 재라우팅

## Notes
- ⚠️ `.git` 은 `D:\Program` — `--git-dir=D:\Program\.git --work-tree=D:\Program\PromptFlowStudio` 필요
- ⚠️ 타이밍 테스트(`OmniLiveRestream` 먹스 50ms 상한)는 부하에 민감 — 전체 실행 전 `dotnet build-server shutdown`
