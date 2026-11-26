# STATUS (2026-11-26)

## 최근 작업
- 단일 프롬프트 장편 모드 추가: `15초 · 원테이크`(`Sec15_OneTake`) · `30초 · 자동`(`Sec30_Auto`) — 파트 1..3 입력 불필요
  - `MultiDurationPlanner.SegmentPlan`: 원테이크 네이티브≥15 → 1×15 · 아니면 3×5 / 30초 ≥15 → 2×15 · ≥10 → 3×10 · 아니면 6×5
  - 다중 세그먼트: 서사 비트 + `ContinuityHint` · 독립 컷 0.25s xfade 합본 `{project}_30s.mp4` · 컷 1만 참조 이미지
  - 그룹 판정 메타 기반(`ShotCountOf` · `IsGroupedShot`) — 기존 10s/15s 동작 불변
- Wan 2.6 지원: `WanModelCatalog`(`wan2.6-t2v`/`wan2.6-i2v` · duration 5/10/15) · 엔진 `BuildRequest` 가 `wan.family` 메타로 모델·duration 지정
  - DashScope 미지원 모델 오류 → 같은 키로 `wan2.1` 5초 1회 재제출 + 안내 · SiliconFlow 는 2.1 로 정규화
  - 모델 선택: `/settings` 영상(Wan) 패널 + 작업실 칩 · `config.json` `Wan.ModelFamily` 영속(`IWanConfigService` 기본 인터페이스 멤버 → 기존 구현체 호환)
  - 네이티브 15초는 Cloud 렌더 모드 + Wan 2.6 일 때만, 그 외 5초 분할
- 테스트: `SinglePromptLongVideoTests`(분할 계획 · 준비도 · 2×15 합본 · duration 15 페이로드 · 2.1 폴백 · Pluto 9/6/2)

## 검증
- Release `-warnaserror` 경고 0 / 오류 0 (Desktop · Tests · bin/Release 직접 빌드 — 앱 미실행)
- xUnit 1546/1546 통과(exit 0) · Pluto 9/6/2 유지 · DI 변경 없음
