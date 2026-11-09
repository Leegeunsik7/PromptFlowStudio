# Current Project Status

- Active Milestone: 【Baby Studio Step 6】 🎵 캔버스 감성 BGM 믹서 · 🏷️ 동적 성장 마일스톤 뱃지 번인
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (Mobile: 로컬 Android SDK 부재로 미빌드)
- Tests: 전체 907/907 PASS (MaxCpuCount=2)
- Updated: 2026-11-09 (KST)

## Completed
- 🎵 `🎵 감성 BGM` 토글 + 트랙 선택(🌙 자장가 / 🎠 오르골 / 🎸 어쿠스틱) → 내보내기 시 원본 옆 `{name}_baby_bgm.mp4` 합성
  - `loudnorm -14 LUFS → volume=0.25 → afade in/out 1.5s` · OFF 시 합성 생략 · StockSuite 에셋은 Silent Ambient Guard(`-an`) 유지
- 🏷️ `🏷️ 성장 뱃지/자막` → `{이모지} {단계명} · {D-day}`(활성 성장 단계 기반) · 뱃지 클릭 시 인라인 문구 편집
  - 단건 내보내기 drawtext 번인(좌하단 흰 박스 · 이모지 제거) · 실패 시 오버레이 제외 재시도
- 🧪 `BabyBgmAndGrowthBadgeTests` 4건

## Next
- 캔버스 미리보기 BGM 재생(브라우저 오디오) 결선
- 단건 내보내기 영상 길이 프로브 → BGM 페이드아웃 시점 자동화(현재 길이 미상 → 페이드인 + `-shortest`)
- 모바일 빌드 검증(Android SDK 확보 시)
