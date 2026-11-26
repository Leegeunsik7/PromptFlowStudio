# STATUS (2026-11-26)

## 최근 작업
- Workspace 주 생성 버튼(✨ 생성 시작): 프롬프트 전용(T2V)·이미지 보조(I2V) 생성 복구
  - 원인: `CanStart`가 `State.Enabled`(Wan API 키)를 요구 → 15s 앰비언트 등 렌더 큐 경로 프롬프트도 무음 잠금
  - `Services/Render/WorkspaceGenerationReadiness.cs` 추가(순수 함수: ResolvePath · CanPress · Evaluate · BlockMessage)
  - 버튼 활성 = 입력(프롬프트 또는 참조 이미지) + 비Busy만 판단, 엔진 상태 무관
  - 5초 단일(Wan 직결) 키 미구성 시 클릭에서 `PlutoAutonomousActivityFeedService.Redact` 처리된 안내 표시
  - `WanJobState` 예기치 못한 예외 메시지 Redact 적용
- 테스트: `WorkspaceGenerationReadinessTests`(0장 T2V · I2V · 무입력 · Busy · 엔진 미구성 안내 · Pluto 9/6/2)

## 검증
- Release `-warnaserror` 경고 0 / 오류 0 (Desktop · Tests)
- xUnit 1495/1495 통과 · Pluto 불변식 9/6/2 유지 · DI 변경 없음(정적 헬퍼)
