# Current Project Status
- Active Milestone: 순차 작업 B 【무선 카메라】 스트림 연결 · 프레임 캡처 · I2V 첫 프레임 핸드오프 UI 결선 완료
- Build Status: Release 빌드 (-warnaserror 0W/0E) — PromptFlowStudio · PromptFlowStudio.Tests · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 전체 613/613 PASS(직렬 · DisableParallelization) · WirelessCameraTests 3/3
- Updated At: 2026-10-27 (KST)

## Completed Deliverables
- WC-1: `Services/Camera/WirelessCameraService` — RTSP/SRT/RTMP/WebRTC/HTTP(S) 검증 → Connecting → Streaming(CTS 취소) · 프로토콜별 지연 텔레메트리 · 24bpp BMP 프록시 캡처(data URL) · I2V 대기 버퍼(1회 소비)
- WC-2: `ViewModels/WirelessCameraViewModel` — `ToggleConnection` · `CaptureCurrentFrame` · `SendToI2vReference` · `RefreshTelemetry`
- WC-3: `WirelessCamera.razor`(/wireless-camera) 컨트롤 바 · 뷰파인더 HUD(FPS/ms/Mbps) + 3x3 그리드 · 캡처 덱 · 사이드 메뉴(📡)/가이드 등록 · `Workspace.razor` 진입 시 캡처 프레임 → I2V 첫 프레임 흡수
- DI: `MainForm.cs`(서비스+VM) · `MauiProgram.cs`(서비스)
- 이전: 6번 숏폼(AS-1~3) · 5번 커머셜 · 4번 라이브 스튜디오 · 렌더 큐 · 3번 대량 양산 · 2번 스토리보드 · 1번 프롬프트 합성기

## Known Issues / 환경 노트
- `UnifiedGaussian_And_BroadcastMcr_Check` · `SingleFileBinaryProcessBootstrap_Check` 직렬 전수 중 부하성 벽시계 초과 간헐 관측 — 재실행 PASS
- 무선 카메라·렌더 워커·라이브/커머셜/숏폼 송출은 가상 파이프라인(시뮬레이션) — 실제 RTSP 디코더/렌더 소비자 미결선
- 모바일: `ViewModels/`·`Blazor/Pages/` 미링크 → `MauiProgram.cs` VM 미등록
- 로컬 ffmpeg.exe 부재로 `-AllowMissingFfmpeg` 빌드 · Authenticode 미서명 · Android 빌드 미검증
