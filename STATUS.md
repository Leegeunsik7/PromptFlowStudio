# Current Project Status
- Active Milestone: 【Governance-Opt】 ⚡ 장중 듀얼 트랙 게이트 · 🚀 Process.Start 단일 관문 · 🛡️ Win32 P/Invoke 런타임 OS 가드
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (Mobile: 로컬 Android SDK 부재로 미빌드)
- Tests: FastUnit 623/623 · 전체 856/856 PASS (Release)
- Updated: 2026-11-05 (KST)

## Completed
- ⚡ `ProcessCategory { HeavyEncoding, LightweightProbe, ShellExternal }` · `ProcessRunner.Classify`(ffprobe · `-frames:v 1`/`-vframes 1` = 경량 자동 분류) · `ProbeGate` 훅
- ⚡ `TradingSafetyGuardService.RunProbeAsync`/`RunCategorizedAsync` — 경량 프로브 독립 퍼밋(4) · 중량 슬롯(1) 점유 중에도 즉시 실행 · `ActiveProbeSlots`
- ⚡ `IProcessRunner.RunAsync(…, ProcessCategory)` · `RunGatedAsync(exe, category, body)` · `LumaFaceDetector`(1프레임) 경량 트랙 · `nvidia-smi` 경량 프로브 이관
- 🚀 `IProcessRunner.LaunchExternal`/`LaunchExternalAsync`(http/https 외 스킴·제어문자·부재 경로 차단 · 예외 격리) · `SystemProcessRunner.Gateway`
- 🚀 `Process.Start`/`ProcessCompat.Start` 호출부 이관(전체 9개): MediaLibrary · DeepLink · AutoUpdate · ResourceMonitor · CommercialEnhancement · ZeroTrust · YouTubeOAuth · StockSuiteExporter · HUD — 남은 직접 호출은 `ProcessRunner.LaunchExternal` 내부 1곳
- 🛡️ `NativePlatformGuard.IsWindows`(테스트용 `SimulateNonWindows` 스코프) → `StockSuiteProcessMonitor.ActivateWindow` · `TrySetDesktopWallpaper` no-op 가드
- 테스트 `GovernanceOptTests` 3종

## Next
- Mobile Android SDK 환경에서 빌드 검증
