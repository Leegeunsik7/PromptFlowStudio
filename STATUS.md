# Current Project Status
- Active Milestone: 5번 워크플로우 【커머셜 브랜드 스튜디오】 브랜드 킷 · 포맷 프리셋 엔진 및 UI 결선 완료
- Build Status: Release 빌드 (-warnaserror 0W/0E) — PromptFlowStudio · PromptFlowStudio.Tests · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 전체 607/607 PASS(직렬 · DisableParallelization) · CommercialBrandTests 5/5 · UnifiedGaussian_And_BroadcastMcr_Check 단독 3/3
- Updated At: 2026-10-27 (KST)

## Completed Deliverables
- CB-1: `Services/Commercial/CommercialBrandService` — BrandKitProfile(로고·워터마크·HEX·디스클레이머) + Bumper6s/Feed15s/Narrative30s 프리셋 → Wan 2.1 프롬프트·오버레이 메타 합성
- CB-2: `ViewModels/CommercialStudioViewModel` — 포맷/비율 토글 · `GenerateAdPrompt` · `ExportToRenderQueue` → `IRenderQueueService`
- CB-3: `CommercialBrandStudio.razor`(/brand-studio) VM 바인딩 · DI `MainForm.cs`(서비스+VM) · `MauiProgram.cs`(서비스)
- MCR 틱 검증: b9ad2fd 베이스라인·현재 트리 모두 단독 PASS, 커머셜 코드 백그라운드 자원 0 → 37 ms 실패는 전체 스위트 부하성 노이즈(테스트 무수정)
- 이전: 4번 라이브 스튜디오(LS-1~5) · 렌더 큐(RQ-1~6) · 3번 대량 양산 · 2번 스토리보드 · 1번 프롬프트 합성기

## Known Issues / 환경 노트
- `UnifiedGaussian_And_BroadcastMcr_Check` · `SingleFileBinaryProcessBootstrap_Check` 직렬 전수 중 부하성 벽시계 초과 간헐 관측 — 재실행 PASS
- 렌더 워커·라이브/커머셜 송출은 가상 파이프라인(시뮬레이션) — 실제 렌더 소비자 미결선
- 모바일: `ViewModels/`·`Blazor/Pages/` 미링크 → `MauiProgram.cs` VM 미등록
- 로컬 ffmpeg.exe 부재로 `-AllowMissingFfmpeg` 빌드 · Authenticode 미서명 · Android 빌드 미검증
