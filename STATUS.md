# Current Project Status
- Active Milestone: ✅ Step 20 — 무재시작 충돌 핫 리로드 · 보정 상태 의미 병합 · 장부 RenderMode/StyleTags 영속 · 라이브 Drive 프로브 · 자율 하드닝 완료
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⚠ Mobile net8.0-ios — `ios` 워크로드 미설치(미검증)
- Tests: ✅ 1259/1259 — 전체 단일 실행 통과(21분 7초 · 종료 코드 0 · 기존 1251 + Step 20 8)
- Updated: 2026-11-20 (KST) · 기준 `4be7458`

## Completed — Step 20
- ♻️ `IPlutoHotReloadable.ReloadFromDiskAsync` — 장부 · 오답 노트 · 렌더 카탈로그 · 시리즈 아크 · 학습 메모리 · 메타 보정 6종: 잠금 안에서 `sync_pending/` 승격 → 파싱 → 원자 교체(손상/부재/잠김/읽기 전용 → 라이브 상태 유지)
- 🔀/↩️ 환경설정 병합·복원 → `MergeConflictLiveAsync`/`RestoreConflictLiveAsync` 즉시 반영(재시작 불필요) · `HubReloaded` → 워크스페이스 대기 실험 · 두뇌 리포트 · 추천 갱신 · 재적재 불가 허브만 다음 실행 적용
- 🎯 `pluto_calibration_state.json` 의미 병합(병합 허브 5→6): 관측 창 키 중복 제거 · 집계 = 공통 이력 1회 + 각 PC 고유분(재병합 멱등) · 후회 귀속 최댓값 · 잔액 = 최근 · 모르는 필드 보존
- 🏷️ 장부 `RenderMode` · `StyleTags`(공유) · `Champion/ChallengerStyleTags` 영속(null 생략 → 구 장부 호환) · 소급은 기록 우선, 구 장부만 몽타주 + 카탈로그 폴백
- 🩺 라이브 Drive 프로브: 스테이징 → 원자 이동 → SHA-256 고정 시간 비교 → 지연(ms) → finally 삭제 · Drive off/루트 부재 → 디스크 무접촉 생략 · 환경설정 표시
- 자율 ① 실시간 A/B 컨텍스트 알파가 영속 태그 사용(소급과 표본 완전 일치) · 재시작 후 대기 실험 RenderMode 복원(ToPlan)
- 자율 ② 복원 형태 가드(`{}` · 다른 허브 파일 · 배열 루트 거절 → 학습 상태 전체 소거 차단)
- 자율 ③ 재적재 전 진행 중 기록 배수(장부 체인 · 아크 플러시) + 배수 이후 등록분 보존 → 병합 되덮기 차단
- 자율 ④ 보정 재적재 시 `.bak` 갱신 → 이후 본 파일 손상 시 병합 이전으로 롤백 방지

## Notes
- ⚠️ 재적재 성공 직후가 아닌 '승격 후 읽기 실패'(극히 드문 잠금) 시 해당 세션 메모리가 다음 기록에서 병합분을 덮을 수 있음
- ⚠️ `.git` 은 `D:\Program` — `--git-dir=D:\Program\.git --work-tree=D:\Program\PromptFlowStudio` 필요
