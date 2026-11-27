# STATUS (2026-11-27)

## 최근 작업 — 작업실 컴팩트 레이아웃 · 5초 단일 생성 QC 자가 치유
- 🩺 5초 단일 생성(`WanJobState.StartAsync`)에 렌더 후 QC 결선 — Completed 방송 전 검사(결함 클립에 메타/Foley/정합성 측정 미부착)
  - 결함 → `PlutoVideoLearningMemoryService.RecordRenderQualityDefect` + `BuildQcRetryRequest`(안정화 앵커 · 800자 상한 보존 · 회피 네거티브)로 1회 재생성, 재결함 시 `QcDegraded` 경고 후 노출
  - Mock 렌더 모드(`SkipQc`) · 원격 URL 폴백 · ffmpeg 부재/QC 예외는 생략 · 취소는 Canceled
  - 캔버스 배지 `🩺 QC 자가 치유됨` / `⚠ QC 경고(재시도 후)`(`data-qc-5s`)
- 📐 컴팩트: 유휴 뷰파인더 `min(20vh,172px)` · 결과 영상 `max-height: min(42vh,400px)` · 진행/오류 박스 축소 · 튜너 1행(라벨·슬라이더·값) · 카드 간격 8px · 좌측 칩/확장 바 여백 축소
- 자율 발견 수정: 액션 행 줄바꿈(토글 가로 넘침) · 원격 보정 예외 → 로컬 폴백(생성 무음 중단 방지) · 낡은 ✨ 확장본 적용 차단 · 보정 대기 중 페이지 이탈 가드
- DI 변경 없음(작업실 OnInitialized 에서 결선 · 람다 this 캡처 없음) · Pluto 9/6/2 유지

## 검증
- Release `-m:2 -warnaserror -p:NuGetAudit=false` 경고 0 / 오류 0 (Desktop · Tests · bin/Release — 앱 미실행)
- 대상 xUnit 153/153 통과(exit 0) · 신규 `WorkspaceCompactAndSingleQcSelfHealTests` 8건 · `FirstFramePreviewFallbackTests` 마커 갱신
- 전체 테스트 · Mobile 빌드 · 실앱 시각 확인 미실행
