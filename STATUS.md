# STATUS (2026-11-26)

## 최근 작업 — 플루토 자율 비디오 디렉터 확장
- 🎯 자동 첫 프레임 시드 고정: `GeminiPromptService.GenerateFirstFramePreviewAsync`(원격 전용 · 로컬 폴백 PNG 무시 · 키 없으면 HTTP 0) · `RenderQueueService.TryAutoVisualAnchorAsync` → 컷 1 I2V 시드
  - 작업실 토글 `🎯 자동 첫 프레임 시드 고정`(기본 = Gemini 키 설정 시 ON) · 고정 미리보기/업로드/얼굴 프로필 우선
- 🩺 렌더 후 QC: `FFmpegVideoStitchService.AnalyzeRenderQualityAsync/EvaluateFrames`(길이 0 · 시작/중간/끝 정지 · 검정 · 손상 — 32×18 gray 샘플)
  - `RenderQueueService.TryQcSelfHealAsync`: 결함 → `PlutoVideoLearningMemoryService.RecordRenderQualityDefect` + 안정화 프롬프트/네거티브로 1회 재시도, 재결함 시 `QcDegraded` 경고 후 클립 노출(Mock 엔진 · 원격 산출 · ffmpeg 부재는 생략)
  - 큐 렌더(10s/15s/앰비언트/장편)에만 적용 — 5초 단일 생성은 큐 미경유
- ✨ `플루토 감독 확장` 버튼(프롬프트 박스 옆 · 학습 디렉터 보정본 미리보기/적용)
- 📥 `기존 갤러리 학습 흡수`: `ImportGalleryBaselineFromDirectory`(`*.meta.json` · Mock 제외 · 프롬프트 해시 중복 제거 · 멱등 · 기준선 가중치 0.5)
- `BatchRenderJob.Prompt` init → set(QC 안정화 재시도)
- DI 변경 없음(학습 메모리는 작업실에서 큐에 결선) · Pluto 9/6/2 유지

## 검증
- Release `-warnaserror -p:NuGetAudit=false` 경고 0 / 오류 0 (Desktop · Tests · bin/Release — 앱 미실행)
- xUnit 1593/1593 통과(exit 0) · 신규 `PlutoAutoAnchorQcSelfHealAndGalleryBootstrapTests` 11건
- Mobile 빌드 미실행(MAUI 워크로드 범위 외)
