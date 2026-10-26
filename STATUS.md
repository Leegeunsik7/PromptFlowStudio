# Current Project Status
- Active Milestone: 2번 워크플로우 【스토리보드(멀티 컷)】 시퀀서 · 뷰모델 추가
- Build Status: Release 빌드 (-warnaserror 0W/0E) — PromptFlowStudio · PromptFlowStudio.Tests · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 전체 579/579 PASS(직렬) · StoryboardSequencerTests 16/16 · PromptSynthesizerTests 22/22
- Sidebar: 6 그룹 · 32 워크스페이스 유지(신규 모달 0)
- Updated At: 2026-10-26 (KST)

## Completed Deliverables
- SB-1: `Services/Storyboard/StoryboardSequencerService.cs` — StoryboardCut · TransitionType · BreakdownScript · PropagateConsistency
- SB-2: `ViewModels/StoryboardViewModel.cs` — Cuts 컬렉션 · Add/Remove/MoveUp/MoveDown · ReceiveGeneratedScript · TotalDurationSec
- SB-3: DI 등록 `MainForm.cs` + `MauiProgram.cs` 양쪽(시퀀서 서비스)
- SB-4: `PromptFlowStudio.Tests/StoryboardSequencerTests.cs` — 16케이스
- CI: `release.yml` publish-release `files`에 `*.verify.json` 추가 → TitanReleaseTests 계약 복구

## Known Issues / 환경 노트
- StoryboardViewModel 은 Storyboard.razor / Workspace.razor UI 미결선(수신 API만 제공)
- 로컬 ffmpeg.exe 부재로 `-AllowMissingFfmpeg` 빌드 — 정식 배포 전 `-FfmpegPath` 지정 재빌드 필요
- Authenticode 미서명(인증서 미제공) · Android 빌드는 환경 제약으로 미검증
