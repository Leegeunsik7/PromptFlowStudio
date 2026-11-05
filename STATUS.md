# Current Project Status
- Active Milestone: 【Stability-Core】 🛡️ Pre-flight 디스크 센티넬 · 🔌 IPC 재접속 워치독 · ⏱️ 듀얼 스크린 동기 매니페스트
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (Mobile: 로컬 Android SDK 부재로 미빌드)
- Tests: FastUnit 626/626 · 전체 859/859 PASS (Release)
- Updated: 2026-11-05 (KST)

## Completed
- 🛡️ `Services/Render/PreFlightResourceSentinel` — `CheckDiskSpace(dir, 5GB)` → `PreFlightCheckResult`(`Insufficient disk space: {free}MB available, {required}MB required.`) · 조회 불가 = 통과 · `FreeSpaceProvider` 주입
- 🛡️ `StockSuiteExporter.PreFlight` · `CanSafelyEncode(estimatedOutputSize)` · 4K/60fps 단건 + 듀얼 분할 착수 전 Fail-Fast(백업·ffmpeg 미착수)
- 🔌 `IpcConnectionState { Disconnected, Listening, Connected, Reconnecting }` · `OnConnectionStateChanged` · `ReconnectInterval`(3초) · 읽기/쓰기 IOException·ObjectDisposed → Reconnecting + 파이프 폐기·재생성
- 🔌 `Workspace.razor` 배지 `🟡 Reconnecting...`(`.ws-ipc-wait`) · 구독/해제 쌍
- ⏱️ `stocksuite_displays_sync.json`(syncId · loopDurationSec · fps · left/rightScreen · masterEpochUtc) 원자 기록 · `DualMonitorSplitResult.SyncManifestPath` · `DualSyncLoopSeconds`/`DualSyncFps`
- ⏱️ IPC `MasterClockSync` — `BuildMasterClockSyncPacket` · `ComputeSyncOffset`(루프 내 오프셋·프레임) · `BroadcastMasterClockSyncAsync`(파이프 전용)
- 🧪 `StabilityCoreSentinelIpcSyncTests` 3건

## Next
- StockSuite 측 `MasterClockSync` 수신 → 두 WebView2 `currentTime` 시크 결선
- 듀얼 루프 길이 ffprobe 자동 산출(현재 기본 15초)
