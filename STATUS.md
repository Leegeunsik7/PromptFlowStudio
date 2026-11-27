# STATUS (2026-11-28)

## 최근 작업 — 미디어 앨범 · 렌더 큐 정비
- 🖼️ 앨범 카드/인스펙터: `.meta.json` 기반 🛡️보정본/원문 · 모델(`wan.family`/엔진) · 🩺 QC 배지(자가 치유/경고) · 보정 전 원문 표시
  - Workspace 멀티 듀레이션 적재 시 보정 원문을 `metadata["prompt.raw"]` 로 기록(단일 컷 Wan 경로는 미기록)
- 👍 앨범에서 바로 Pluto 학습 등록(`GalleryViewModel.LearnFromItem` → `RecordPositiveOutcome` + 메타 `plutoLearned`, 중복 학습 차단 · Mock/스냅샷 제외)
- 🗑️ 중복/실패 파일 일괄 정리: `GalleryMediaService.PlanCleanup/ExecuteCleanup` — SHA-256 중복(원본 1개 보존) · 0바이트/1KB 미만 영상 · 고아 메타/포스터, 즐겨찾기·내보냄·학습 등록은 항상 보존, 확인 배너 후 실행
- 🎞️ 렌더 큐: 🎲 새 시드 + 안정화 프롬프트 1클릭 재시도(`IRenderQueueService.RetryFresh`, 실패/취소 + QC 경고 완료 작업) · 🧹 완료/실패 항목 정리(`ClearFinished` — 목록만 정리, 미디어 보존, 진행 중 합본 그룹 보존) · ⚠ QC 경고 배지
- 자율 발견 수정: 0% 대기 작업 재정렬 시 Version 불변으로 화면 미갱신 · 큐 사이드카 `durationSec` 미인식(재생시간 뱃지 누락) · 삭제 시 `.poster.jpg` 고아 누적 · 재생 중 모달/인스펙터 `<video>` 핸들로 삭제 실패(언마운트 → 150ms → 삭제, IOException 2회 재시도) · 정리 중복 실행 차단 · 수동 안정화 재시도 후 QC 자가 치유가 원문을 덮어쓰던 문제
- Pluto 9/6/2 · DI 패리티 유지(신규 DI 없음, 기존 `PlutoVideoLearningMemoryService` 주입)

## 검증
- Release `-m:2 -p:BuildInParallel=false -warnaserror -p:NuGetAudit=false` 경고 0 / 오류 0 (Desktop · Tests · bin/Release — 앱 미실행)
- 지정 필터 xUnit 120/120 통과(exit 0) — 첫 실행에서 `GenerativeMusic_And_Choreography_Check`(5초 벽시계 상한) 부하 중 1회 실패, 단독·재실행 통과
- 추가 실행(`GalleryQueueRefinementTests` 신규 13건 · RenderQueue 스로틀/워커 · 페이지 마커) 50/50 — 재정렬 회귀는 수정 제거 시 실패 확인(대조 실험)
- 전체 테스트 · Mobile 빌드 · 실앱 시각 확인 미실행
