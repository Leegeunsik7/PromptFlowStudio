# Current Project Status
- Active Milestone: 프롬프트 합성(PromptSynthesizer) 기능 추가 — 분할 검증 통과
- Build Status: Release 빌드 (-warnaserror 0W/0E) 및 PromptSynthesizerTests 22/22 검증 완료 · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: PromptSynthesizerTests 22/22 PASS · 게이트 3종(ZzCrowdProbe·InstallerPackaging·CiCdPipeline) 3/3 PASS · 전체 스위트 미실행(직전 기준 541/541)
- Sidebar: 6 그룹 · 32 워크스페이스 유지(신규 모달 0)
- Updated At: 2026-10-26 (KST)

## Completed Deliverables
- PS-1: `Services/Prompt/PromptSynthesizerService.cs` — 프롬프트 합성 서비스 신규
- PS-2: `Services/AI/GeminiPromptEnhancer.cs` — 외부 프롬프트 보강기 신규
- PS-3: `ViewModels/PromptInputViewModel.cs` — 입력 뷰모델 신규
- PS-4: DI 등록 `MainForm.cs` + `MauiProgram.cs` 양쪽 · `Workspace.razor` / `FirstFramePreviewDeck.razor` 결선
- PS-5: `PromptFlowStudio.Tests/PromptSynthesizerTests.cs` — 22케이스(Fact/Theory 포함)

## Known Issues / 환경 노트
- 테스트 수 게이트(Total≥541) 반영 위해 커밋 전 `-warnaserror` 재빌드 + 전체 스위트 직렬 실행 필요
- 로컬 ffmpeg.exe 부재로 `-AllowMissingFfmpeg` 빌드 — 정식 배포 전 `-FfmpegPath` 지정 재빌드 필요
- Authenticode 미서명(인증서 미제공) · Android 빌드는 환경 제약으로 미검증
