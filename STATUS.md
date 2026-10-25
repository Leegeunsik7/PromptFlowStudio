# Current Project Status
- Active Milestone: v18.2.5 완료 (StoryV 매니페스트 영속화 & 샷 편집기 및 E2E 풀루프 무결성 확정)
- Build Status: Desktop · net8.0-ios · Tests 3 종 Release -warnaserror 0/0 · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 저메모리 단일 스레드(MaxCpuCount=1 · DisableParallelization) 538/538 1 회 완주 통과(실패 0 · 건너뜀 0 · 단독 재실행 0 · 8 분 50 초)
- Sidebar: 6 그룹 · 32 워크스페이스 유지(신규 모달 0)
- Updated At: 2026-10-25 (KST)

## Completed Deliverables
- EDT-1: `Services/Storyboard/StoryboardShotNudgeEditor.cs` — `SplitShot`(타임코드 분할 · 대본 공백 경계 양분 · 9:16 크롭 승계) · `MergeWithNext`(구간/대본 합집합) · `NudgeBoundary`(100 ms 격자 · 다음 샷 동기화 · 최소 300 ms 클램프) · 지역 검증 후 원자 기록 · 핫패스 0 B
- EDT-2: `StoryboardSequenceManifestEngine.cs` — `.storyv` Utf8JsonWriter/Reader 직렬화(샷 · 크롭 트랙 · 대본 · 오디오 헤더 · 지문) · 범위/크롭/지문 검증 통과 시에만 원자 복원 · 임시 파일 원자 교체 저장 + 재판독 검증 · 핫패스 0 B
- EDT-2(UI): 스토리보드 툴바 `💾 프로젝트 저장 (.storyv)` · `📂 프로젝트 열기 (.storyv)` · 샷 카드 선택 → ✂ 스플릿 · 🔗 머지 · ±0.1s 넛지(편집/복원 후 팩토리 전송 시 재분할 생략)
- 시퀀서: `EditBuffer` · `Commit`(수동 프레이밍 보존 확정) · `Restore` 추가
- TEST: `StoryV_FullChain_ClosedLoopPipeline_Check` — 대본+WAV → 샷 분할 → 스플릿/넛지/머지 → .storyv 메모리·디스크 왕복 → 팩토리 0-카피(다중 샷 크롭 추종 · ①~④ 렌더) → 퍼블리셔 0-카피(YouTube 8 MB 청크 · Instagram 발행 큐) · 3 회 누수 < 256 KB · 변조 거부

## Known Issues / 환경 노트
- MP3 는 디코딩 없이 비트스트림만 바인딩 → 라우드니스 미계측(게인 1) · .storyv 는 오디오 본문 미포함(헤더만 · 재바인딩 필요) · Android 빌드 미검증
