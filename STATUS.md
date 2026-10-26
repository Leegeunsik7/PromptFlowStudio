# Current Project Status
- Active Milestone: 2번 워크플로우 【스토리보드(멀티 컷)】 UI 결선(Workspace → Storyboard 컷 덱)
- Build Status: Release 빌드 (-warnaserror 0W/0E) — PromptFlowStudio · PromptFlowStudio.Tests · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 전체 579/579 PASS(직렬)
- Sidebar: 6 그룹 · 32 워크스페이스 유지(신규 모달 0)
- Updated At: 2026-10-26 (KST)

## Completed Deliverables
- SB-1: `Services/Storyboard/StoryboardSequencerService.cs` — StoryboardCut · TransitionType · BreakdownScript · PropagateConsistency
- SB-2: `ViewModels/StoryboardViewModel.cs` — Cuts 컬렉션 · Add/Remove/MoveUp/MoveDown · ReceiveGeneratedScript · TotalDurationSec
- SB-3: DI 등록 `MainForm.cs` + `MauiProgram.cs` 양쪽(시퀀서 서비스)
- SB-4: `PromptFlowStudio.Tests/StoryboardSequencerTests.cs` — 16케이스
- SB-5: `StoryboardViewModel` 싱글턴 DI(`MainForm.cs`) · Workspace 「대본 자동 생성」 → ReceiveGeneratedScript(빈 대본 4컷 기본 시놉시스) → /storyboard 전환
- SB-6: `Storyboard.razor` 컷 덱 — 컷 #/지속시간 배지 · 전환 셀렉터 · RawPrompt 바인딩 · 카메라 칩 · 시드/피사체 잠금 배지 · ▲▼✕ · 푸터(컷 수·총 초·+ 새 컷)
- CI: `release.yml` publish-release `files`에 `*.verify.json` 추가 → TitanReleaseTests 계약 복구

## Known Issues / 환경 노트
- 모바일: `ViewModels/`·`Blazor/Pages/` 미링크(+ VM의 `Program.Log` 데스크톱 의존) → `MauiProgram.cs` VM 미등록
- 로컬 ffmpeg.exe 부재로 `-AllowMissingFfmpeg` 빌드 — 정식 배포 전 `-FfmpegPath` 지정 재빌드 필요
- Authenticode 미서명(인증서 미제공) · Android 빌드는 환경 제약으로 미검증
