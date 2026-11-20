# Current Project Status
- Active Milestone: ✅ Step 23 — Downloads 자동 감시 · 디바운스 수집 · SHA-256 영속 중복 차단 · Workspace 실시간 토스트 완료
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⚠ Mobile net8.0-ios — `ios` 워크로드 미설치(미검증)
- Tests: ✅ 1277/1277 — 전체 단일 실행 통과(19분 1초 · 종료 코드 0 · 기존 1269 + Step 23 8)
- Updated: 2026-11-20 (KST) · 기준 `4cc8eda`

## Completed — Step 23
- 📥 `YouTubeDownloadsAutoWatcherService` — FileSystemWatcher(Created/Renamed/Changed) + 60초 재조정 스윕 이중 탐지 · DI 데스크톱/모바일 Singleton
- ⏳ 준비 게이트 — `.crdownload/.part/.tmp`·숨김·0바이트 제외 · 파일별 디바운스 1.3초 · 크기/mtime 안정 · `FileShare.Read` 유계 재시도
- 🔇 비 YouTube 무시 — 이름 서명 또는 CSV 헤더 스키마 엿보기(ZIP은 이름 필수) · 무소음 거부 장부
- ♻️ SHA-256 장부 — `AppDataPaths/PromptFlowStudio/Pluto/downloads_watcher.json` 원자적 교체 · 4096 유계 · 재시작 후 재수집/재토스트 0 · 새 해시 → Step 22 성숙 증분
- 🔒 격리 — `PROMPTFLOW_PLUTO_DRIVE=off` · `PROMPTFLOW_DISABLE_DOWNLOADS_WATCHER=1` → 실 Downloads 경로 미해석·미기동(명시 폴더만 허용)
- 🖥️ Workspace — [👁️ 다운로드 자동 감시 ON/OFF] 토글(영속 · 기본 ON) · `📥 유튜브 자동 수집 완료: 신규 N건 · 성숙 보정 M건 (파일명)` · 두뇌 리포트/추천 즉시 갱신 · 0건 학습 토스트 생략
- 🧠 자체 추가 — ① FSW 버퍼 오버플로 → 즉시 재스캔 · 기타 오류 → 감시기 재구성 ② 수동 가져오기·이벤트·스윕 단일 게이트 + in-flight 해시 + 게이트 내 재확인(이중 반영/재토스트 차단) ③ 구독자별 예외 격리 + 회로 폐기 안전 `SafeUiUpdate` ④ 일시 실패 3회 후 격리(스윕 재시도 폭주 차단)
- 🧪 `PlutoDownloadsAutoWatcherTests.cs` 8건

## Notes
- ⚠️ `.git` 은 `D:\Program` — `--git-dir=D:\Program\.git --work-tree=D:\Program\PromptFlowStudio` 필요
- ⚠️ 동시 세션의 외부 `testhost` 종료 시 전체 실행 중단 — 전체 실행은 조용한 머신에서
