# Current Project Status - Active Milestone: 【Pro Suite】 매니페스트 엄격 매칭/롤백 · 🖥️ 4K UHD & 📺 핀테크 워터마크 인코더 · 🕒 프롬프트 타임머신
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (net8.0-android 미검증: Android SDK 미설치)
- Tests: FastUnit 582/582 PASS (Release)
- Updated: 2026-11-05 (KST)

## Completed
- 🧾 `.stocksuite_manifest.json`: `activeAsset`·`sourceRenderId`·`sourceSha256`·`sourceBytes`·`exportedAt`·`backupAsset`(+백업 출처) 원자 기록 — 직전 활성 출처를 백업 출처로 승계
- 오탐 차단: `GalleryMediaService` 역방향 치유에서 파일명 매칭 제거 → 출처 마커 경로 · 매니페스트 SHA-256(렌더 ID/크기 후보) · 내용 SHA-256만 허용, `*.backup.*` 색인 제외
- ↩️ 롤백: 덮어쓰기 직전 `bg_ambient.backup.mp4` 보존(동일 출처 재내보내기는 생략) · `RollbackPrevious()` 활성↔백업 맞교환(마커·매니페스트 교환, 핫리로드 신호) · PublishModal/갤러리 `[↩️ 이전 배경으로 복원]`
- 🖥️ `StockSuiteResolution.Uhd4K`: `scale=3840:2160:flags=lanczos`(+Unsharp) · PublishModal 해상도 선택
- 📺 HUD 워터마크: 좌상단 `ANG QUANT LAB · REALTIME STREAM` / 우상단 `● LIVE KST` drawtext(4K 2배 스케일) · 폰트 자동 탐색(맑은 고딕→Arial→Segoe, 없으면 fontfile 생략) · drawtext 실패 시 워터마크 제외 체인 재시도
- 🕒 `PromptHistoryService`(신규·DI 양쪽): 10단계 링 버퍼 · 중복 무시 · 구독자 예외 격리 · Workspace `[🕒 되돌리기]` 드롭다운(강화/번역/3-Way/시황 무드 전후 + 입력 정지 1.5초) → 프롬프트(카메라 토큰)·시드 복원
- Tests: `ManifestRollbackWatermarkHistoryTests` 4종 (strict manifest · rollback · 4K/watermark · prompt history)
