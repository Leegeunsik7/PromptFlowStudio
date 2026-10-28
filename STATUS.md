# Current Project Status
- Active Milestone: 【Perf】 UI 스로틀링 · ArrayPool 프레임 풀링 · VRAM 텔레메트리 캐시 · 테스트 카테고리 분리
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (net8.0-android 미검증: SDK/JDK 미설치)
- Tests: 전체 672/672 PASS(직렬 11m19s, 기존 14m43s) · FastUnit 123/123 PASS(병렬 ~3s)
- Updated: 2026-10-29 (KST)

## Completed
- Perf UI: `RenderQueueViewModel` 진행률 통지 100ms 스로틀 + 트레일링 1회 · 상태 전이(완료/실패/일시정지) 즉시 · `Version` 기반 `RenderQueue.razor` `ShouldRender` · `CameraStudio` 텔레메트리 틱 렌더 스킵(`StateVersion`)
- Perf 메모리: `NetworkCameraStreamReceiver` 수신 버퍼 `ArrayPool<byte>` 대여/확장/반납 · `CapturedFrameMeta` 원본 바이트 보관 + data URL 온디맨드(약참조 캐시) · I2V 롤 복원 base64 디코드 제거
- Perf GPU: `ResourceMonitorService` nvidia-smi 1.5s TTL 캐시 + 동시 호출 합류(`GpuQueryCount`)
- Perf 테스트: 전 테스트 클래스 `[Trait("Category","FastUnit"|"Integration")]` — I/O·프로세스·GC·타이밍 의존은 Integration
- Step4: 렌더큐→갤러리 딥링크 · I2V 복원 · VRAM ≥95% 자동 일시정지

## Known Issues
- FastUnit 분류는 정적 휴리스틱(I/O·GC·Delay 마커) — 순수 테스트 일부가 Integration 에 남음(123/672)
- VRAM 가드는 nvidia-smi 있을 때만 · 비용 예약 메모리 전용 · 시뮬레이션 폴백은 실제 미디어 미생성
- 모바일: `ViewModels/`·`Blazor/Pages/` 미링크 · Android 빌드 미검증
