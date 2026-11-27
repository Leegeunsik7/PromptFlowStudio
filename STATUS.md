# STATUS (2026-11-28)

## 최근 작업 — 4컷 성장 일기 · 15초 릴스 멀티 컷 정비
- 🎞️ `MultiCutContinuity`(신규): 컷 1 피사체/의상/장소 앵커 추출 → 컷 2~ 전파(피사체 구문 마지막 유지) · 🧠 플루토 학습 앵커 전 컷 공통(최대 2) · 하드 컷 메타 기본
  - `StoryboardViewModel` · `AiShortsViewModel` 에 선택 주입(`PlutoVideoLearningMemoryService?` · DI 자동 해석, 양쪽 등록 기존 유지)
- 🔁 결함 컷 단건 재생성: `IRenderQueueService.RegenerateJob`(기본 false) · `RenderQueueService` 구현 — 같은 작업/그룹/CutIndex 재적재, 새 시드, QC 메타 초기화, 원문 프롬프트 복귀, `cut.regen` 기록
  - `BatchRenderJob.Seed` init → set
- 🩺 컷별 QC 배지(대기·렌더·자가 복구 중·통과·복구 완료·경고·실패) + ↻ 버튼 — 스토리보드 합본 행 · 릴스 3컷 타임라인
- 자율 발견 수정: 수동 합본 ↔ 4컷 자동 합성 동시 실행 차단 · 릴스 렌더 중 재전송(중복 렌더) 차단 · 새 전송/재생성 시 낡은 합본(발행 대상) 해제 · 합본 중 화면 이탈 시 ffmpeg 취소(CTS) · 상태 지문으로 배지 갱신(진행률 틱 렌더 없음)
- Pluto 9/6/2 · DI 패리티 유지

## 검증
- Release `-m:2 -p:BuildInParallel=false -warnaserror -p:NuGetAudit=false` 경고 0 / 오류 0 (Desktop · Tests · bin/Release — 앱 미실행)
- 지정 필터 xUnit 123/123 통과(exit 0) · 신규 6건(`MultiCutTransitionModeTests`)
- 확장 실행(StoryboardSync · AiShorts · RenderQueue · 모달 크롤러 포함) 175/176 — `InstallerPackaging_And_FfmpegBundle_Check` 부하 중 1회 실패, 단독 재실행 통과(PS 하위 프로세스 플래키)
- 전체 테스트 · Mobile 빌드 · 실앱 시각 확인 미실행
