# Current Project Status

- Active Milestone: 【HTS】 HTS HUD 가독성 시뮬레이터 · 커스텀 프리셋 저장 엔진 · StockSuite 제로-오디오 스트립
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (net8.0-android 미검증: Android SDK 미설치)
- Tests: FastUnit 554/554 PASS (Release, ~33s)
- Updated: 2026-11-04 (KST)

## Completed
- HtsHudOverlay.razor(신규): 종합차트 캔들·MA · 10단 호가 · 체결 · 잔고/미체결 바 목업(정적 결정론 데이터 · pointer-events 차단)
- Workspace: 캔버스 헤더 `📊 HTS HUD` 토글 + 투명도(20~80%)·어둡기(0~50%) 슬라이더
- PresetManagerService(신규): `user_presets.json` — 프롬프트·네거티브·카메라·스타일·듀레이션 · 동명 덮어쓰기 · 원자 교체 · `.corrupt.bak`
- Workspace: `⭐ 현재 설정을 프리셋으로 저장` 인라인 폼 · 제미나이 스튜디오 `⭐ 내 프리셋` 칩(원클릭 복원/삭제) · Changed 구독/해제
- StockSuiteExporter: MP4/WebM 원본도 `-map 0:v:0 -c:v copy -an -sn -dn`(+faststart) 무음 스트립 · ffmpeg 부재/실패 시 원본 복사 폴백
- 이전 묶음 포함: 갤러리 Mock 정화 · WebM(VP9) 내보내기 · StockSuite 적용 뱃지
- DI: MainForm · MauiProgram 양쪽 PresetManagerService 결선
- Tests: PresetManagerService_SavesAndLoadsUserPresetsSuccessfully · StockSuiteExporter_AppliesAudioDropOption_ForHtsOptimizedExport

## Next
- 실제 ffmpeg 환경에서 무음 스트립/VP9 결과 HTS 루프 재생 E2E 확인
