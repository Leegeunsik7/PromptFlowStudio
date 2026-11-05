# Current Project Status - Active Milestone: 【Enterprise Hub】 ☁️ 클라우드 태스크 복구 저널 · 🔌 StockSuite Named Pipe 양방향 IPC · 🔎 갤러리 스마트 검색/💽 스토리지 대시보드
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (net8.0-android 미검증: Android SDK 미설치)
- Tests: FastUnit 589/589 PASS (Release)
- Updated: 2026-11-05 (KST)

## Completed
- ☁️ `Services/Render/TaskJournalService`(신규): 클라우드 발번 즉시 `%LocalAppData%\PromptFlowStudio\Render\active_tasks.json` 원자 기록(taskId·engine·prompt·durationMode `15s-Ambient`·status·startedAt·outputPath) · 로컬 저장 성공/원격 Failed·Canceled 시 제거 · 취소 시 `Interrupted` 유지
- 자동 복구: 앱 시작(`MainForm.OnShown`) + `NetworkChange` 재연결 → DashScope `QueryAsync` → 완료분 MP4 다운로드 → 15초 앰비언트 루프 → 갤러리 `.meta.json`(recovered·cloudTaskId) · 진행 중/단절은 보류 · 24h 경과 미확인 폐기 · 재진입 차단
- `VideoEngineRenderExecutionEngine.Journal` — Cloud 종류만 `JournalingRelay`(첫 task_id 보고 1회 기록) · Workspace 상단 `[🔄 미완료 클라우드 작업 복원 중: N건]` 인디케이터
- 🔌 `Services/Process/StockSuiteIpcBridge`(신규 · ns `ProcessBridge`): `\\.\pipe\StockSuiteBridge` 듀플렉스 서버(CurrentUserOnly · 8KB 버퍼 · 끊김 시 1초 재대기) · 줄 단위 JSON
- 수신 `TickerSelected`(티커 문자셋/길이 검증) → 작업실 관심종목 자동 전환 + Gemini 티커 프롬프트 재생성 · 송신 `ThemeChanged{theme,dimmer}` — HTS 테마/Dimmer 변경 150ms 디바운스 · 미연결/쓰기 1초 타임아웃 → `hts_theme.json`+`hotreload.trigger` 파일 폴백
- 🔎 `GalleryMediaService.Search/MatchesSearch`: 키워드 AND(제목·프롬프트 영/한·파일명·티커) × 화면비(16:9/21:9/32:9/9:16) × 해상도(짧은 변 기준 4K/1080p) × 코덱(MP4/WebM) 칩 · `GalleryItem.SizeBytes`
- 💽 `ComputeStorage`: 총량/디스크 여유 + 4K UHD·1080p MP4·WebM·기타 점유율 게이지 · `[검색된 결과만 일괄 삭제]`(확인 대화) · `[검색된 결과 일괄 내보내기]`(내 비디오\PromptFlowStudio Exports, 메타 동반)
- DI: `MainForm.cs` + `MauiProgram.cs` 양쪽(Journal·IpcBridge 싱글톤)
- Tests: `EnterpriseHubJournalIpcGalleryTests` 3종 (저널 기록/재시작 복구/정리 + 엔진 결선 · IPC 파싱/디스패치/파일 폴백/실 파이프 왕복 · 복합 검색/스토리지/VM 일괄 액션)
