# Current Project Status
- Active Milestone: 【완성도 극대화】 4대 상용화 사각지대 해소 — TTS 오디오 믹싱 · SRT 자막 · 엔진 헬스체크 · 디스크 가드
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (net8.0-android 미검증: SDK/JDK 미설치)
- Tests: 전체 693/693 PASS(직렬 9m53s) · FastUnit 461/461 PASS(병렬 ~27s)
- Updated: 2026-10-29 (KST)

## Completed
- TTS 믹싱: `ITtsSynthesisService`/`LocalTtsSynthesisService`(Kokoro/F5 → SAPI COM 늦은 바인딩 → 무음 Mock) · `StitchCutsAsync` 컷 대본(`stitch.script`) WAV → `apad,atrim` 컷 정렬 → `-c:v copy -c:a aac -shortest` · 실패 시 무음 합본 유지
- SRT 자막: `SubtitleGeneratorService` 컷 길이 누적 타임코드 → 합본 옆 `{project}_full.srt`(가상 병합 포함) · Storyboard/AiShorts 대본 메타 동봉
- 엔진 헬스체크: `EngineHealthCheckService` 5초 주기 ComfyUI `/system_stats` Ping(1s) · Cloud 키 구성 판정 · 렌더 큐 상단 배지(참조 카운트 Start/Stop)
- 디스크 가드: `RenderQueueService` 디스패치 직전 출력 드라이브 잔여 < 5GB → `Paused (Disk Full Warning)` · `[🧹 임시 렌더 캐시 비우기]`(concat/mux/tmp/TTS 임시본만 삭제)
- 이전: 렌더 일원화 · 멀티 컷 합본 · 커머셜 번인 · 비용 원장 영속 · 취소 전파

## Known Issues
- 미분류 1개(`ZzCrowdProbe.Probe`) — 전체 실행에만 포함
- SAPI 음성은 OS 설치 음성 언어에 의존(한국어 음성 미설치 시 영어 발음) · Cloud 헬스는 키 구성만 판정(엔드포인트 Ping 미수행)
- concat `-c copy` 는 컷 코덱/해상도 동일 전제 · 모바일 `Blazor/Pages/` 미링크 · Android 빌드 미검증
