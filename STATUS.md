# Current Project Status
- Active Milestone: 【Pluto 커스텀 BGM 업로드 · 15초 하이라이트 자동 추출】 프리뷰/익스포트 비트 동기 엔드투엔드
- Build: ✅ Release `-warnaserror -p:NuGetAudit=false` 0/0 (PromptFlowStudio · Tests)
- Tests: ✅ 1092/1092 PASS (Highlight|CustomBgm|Dsp|BeatSync 필터 37/37)
- Updated: 2026-11-13 (KST)

## Completed
- 🎵 `PlutoAudioHighlightExtractor`: N=2048/H=512 · 0.50·RMS+0.35·Flux+0.15·Centroid 누적합 · 0.25s 슬라이딩 · 25s↑ 경계 2s −0.08 · ±0.5s 비트 스냅 · 30ms/250ms 페이드 · 짧은 곡 0 패딩 · 스트리밍 프레임 디코드
- 🎵 `PlutoCustomBgmService`: 25MB 가드 · WAV 직통 · 압축 포맷 FFmpeg(48k/2ch/pcm_s16le · 60s 타임아웃 · 8분 상한) · 임시 파일 finally 정리 · 디코드 캐시(넛지 재사용) · DI 양쪽
- 🎛️ `StockSuiteExporter`: 모션/몽타주 `customBgmWavBytes`(기본 null) → 해석기 일시 교체 → 비트 스냅 · 더킹 · 트루피크 마스터링 동일 경로 · 라벨 `🎧 커스텀 BGM`
- 🎞 JS `setCustomBgm`/`clearCustomBgm` · `PlutoMotionPreviewer.CustomBgmDataUrl`
- 🖥️ Workspace: 업로드 · 하이라이트/BPM 배지 · ◀ -1s / +1s ▶ · ✕ 기본 BGM · 단일 궤적/몽타주 하이라이트 비트 재정렬
- 🧪 `PlutoCustomBgmHighlightTests` 6종

## Notes
- 프리뷰는 data URL 대신 가상호스트 WAV URL 전달(15초 WAV ≈ 2.9MB → JS interop 부담 회피)
- 수동 넛지(`ExtractWindowAt`)는 스냅 없이 정확한 오프셋 유지
- Mobile(MAUI) 빌드 · 실제 FFmpeg 압축 디코드 · 브라우저 재생 미검증
