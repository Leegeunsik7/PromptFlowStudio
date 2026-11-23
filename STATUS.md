# Current Project Status
- Active Milestone: ✅ Step 33 — YouTube Data API v3 다채널 직접 동기화(`PlutoYouTubeApiSyncService` · DPAPI 봉인 로컬 전용 자격 증명 · 50개 묶음 폴링 · API+CSV 비파괴 융합)
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⏸ Mobile net8.0-ios — `ios` 워크로드 미설치(미검증)
- Tests: ✅ 1362/1362 — 전체 단일 실행 통과(종료 코드 0 · `build-server shutdown` 후 외부 PowerShell) · Step 33 신규 `PlutoYouTubeApiSyncTests.cs` 9건
- Updated: 2026-11-23 (KST) · 기준 `9565266` · 불변식 `CorePlutoFileCount=9` · `MergeableHubCount=6` · `CompanionSyncFileCount=2` 유지

## Completed — Step 33
- 🔐 자격 증명: API 키 / OAuth(리프레시 토큰) → `%LOCALAPPDATA%\PromptFlowStudio\PlutoLocalOnly\` DPAPI(CurrentUser) 봉인 · 비 Windows AES-GCM 폴백 · 원자 기록 · 만료 전 토큰 1회 갱신
- 🔄 다채널 폴링: `channels.list` → `playlistItems.list` → `videos.list` 50 ID 묶음 · 채널 로캘(`UC…=ja-JP`) → 로캘 변형 연결 · 게시 제목 A/B 귀속
- 💸 쿼터: 요청당 1 unit 보수 집계 · 태평양 자정 일일 자체 예산 · `quotaExceeded` 자정까지 냉각(영속) · 429/5xx `Retry-After`(≤30초)/1·2·4초 백오프
- 🔀 융합: API 관측 차선은 앞설 때만 갱신 · Studio 정밀 지표/워터마크/폐루프 불변(양방향 비파괴)
- 🖥️ `Settings.razor` 연결 카드(저장·테스트·해제 · 비밀값 미표시) · `Workspace.razor` 동기화 · DI `MainForm.cs`+`MauiProgram.cs`
- 🛡️ 자체 추가 5: ① 가짜 지표 가드(API 행 → 폐루프 워터마크·미매칭 대기열 차단) ② 신선한 API 조회수만 Studio 성숙 판정 보강 ③ API 신규 바인딩 시 미매칭 Studio 행 즉시 회수·학습 ④ 비밀값 오류문구 제거·타 채널 영상 폐기·중간 예산 상한·단일 실행 ⑤ Drive/클라우드 경로 봉인 거부 + 해제 시 0 덮어쓰기 삭제·토큰 폐기
- 🧪 격리: `PROMPTFLOW_PLUTO_DRIVE=off` 실 네트워크 0 · `PROMPTFLOW_DISABLE_YOUTUBE_API=1` 전면 차단

## Notes
- ⚠️ `.git` 은 `D:\Program` — `--git-dir=D:\Program\.git --work-tree=D:\Program\PromptFlowStudio` 필요
- ⚠️ 타이밍 테스트(`OmniLiveRestream` 먹스 50ms 상한)는 부하에 민감 — 전체 실행 전 `dotnet build-server shutdown`
