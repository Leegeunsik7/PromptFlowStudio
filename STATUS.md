# Current Project Status
- Active Milestone: 【Pluto DSP · PlutoCore 결선】 사이드체인 더킹 + PlutoCore 허브 수명 관리/페일오버
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio (`-p:NuGetAudit=false` · Mobile 미빌드)
- Tests: 전체 1044/1044 PASS (`~Ducking|~PlutoCore|~Dsp` 34/34 · 신규 5)
- Updated: 2026-11-11 (KST)

## Completed
- 🎚️ `Dsp/PlutoSidechainDucker.cs`: SFX 큐 [T−25ms, T_end+180ms] −4 dB · 1-pole 어택 25ms/지수 릴리스 180ms · 구간 정렬/병합 · 루프 할당 0
- 🎛️ `PlutoTruePeakMasteringEngine.TryMasterWavBuffer(wav, duckCues, …)`: 리미팅 후 더킹(게인 ≤ 1 → ≤ −1 dBTP 유지)
- 🎵 `StockSuiteExporter.BabyStudio.cs`: `SidechainDuckingEnabled`(기본 ON) — 비트 스냅 후 큐 기준 더킹 → FFmpeg amix
- 🪐 `PlutoCoreProcessManager.cs`: D:\PROGRAM\PlutoCore 탐지 · `WaitNamedPipe` 파이프 프로브(슬롯 비점유) + 선택 루프백 HTTP 헬스 1.5s · 창 없는 기동/8s 준비 · 스튜디오 기동분만 Stop · 5초 모니터
- 🔁 `PlutoCoreLlmClient.cs` + `PlutoLlmBridge`: Gemini → PlutoCore(Named Pipe · `PLUTOCORE_API_KEY`) → REST 게이트웨이 → 템플릿
- 🧩 `Workspace.razor` 헤더 상태 필(🟢 연결됨 / ⚪ 미실행 [🚀 기동]) · `PlutoLocalAiSettingsModal` 🩺 상태 확인/🚀 실행 패널 · DI 양쪽 등록

## Notes
- PlutoCore는 HTTP가 아닌 Named Pipe(`PlutoCore_SecurePipe`) 허브 — HTTP 헬스는 `PFS_PLUTOCORE_HEALTH_URL` 지정 시에만
- PlutoCore LLM 단계는 `PLUTOCORE_API_KEY` 미설정 시 건너뜀
- 더킹 −4 dB 실청취 밸런스 수동 확인 필요 · Mobile(MAUI) 빌드 미검증
