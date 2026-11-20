# Current Project Status
- Active Milestone: 【Step 18 — 양방향 Drive 쓰기 반환 · BGM/태그 경험적 베이즈 컨텍스트 특화 · 비파괴 자막 모드/시즌 전환 · 자율 하드닝】 ✅ 완료
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⏳ Mobile net8.0-ios — `ios` 워크로드 미설치(미검증)
- Tests: ✅ 1243/1243 — 전체 단일 실행 통과(21분 22초 · 종료 코드 0 · 기존 1235 + Step 18 8)
- Updated: 2026-11-20 (KST) · 기준 `32da265`

## Completed — Step 18
- ☁️⬆️ `WriteBackNewerLocalToDrive`: 유효 · 비어 있지 않은 로컬이 Drive 보다 2초 넘게 새롭거나 Drive 부재 → 원자 반환(수정 시각 보존 · 비교 후 교체) · 기동(쓰기 반환 → 복원) · 백업 검증 · 재연결 시 고립 파일 즉시 푸시 · 더 새로운 Drive 보호 · `PROMPTFLOW_PLUTO_DRIVE=off` 격리
- 🧬 BGM 무드 · 스타일 태그도 전역(`pattern:`) · 지역(`cpat:`) 잔차 알파 → `RankForContext` 전 모달리티(태그 '#' 변형 병합) · A/B 챔피언 BGM 컨텍스트 특화 · 두뇌 리포트 모달리티 분해 · 구버전 JSON 순위 불변
- 💬 `PlutoSeriesSubtitleOverlay`: 원문(생성 + 수동 편집) ⟂ 덧입힘 ⟂ 장식 — 자동 각색 ↔ 추천만 · 새 시즌 분리는 재합성만(재생성 없음) · `[✏️ 자막 편집]` · 덧입힌 훅/아웃트로를 고치면 그 자리 사용자 고정
- 자율 ①: 첫 해석 쓰기 반환 — 서비스 적재 '이전'에 오프라인 학습을 Drive 로(기동 백그라운드 복원과의 경합으로 최신 학습이 덮이던 경로 차단) · Drive 시드도 검증 + 원자화
- 자율 ②: 분기 충돌 보관 — `pluto_sync_manifest.json`(로컬 전용) 기준 오프라인 편집 + 다른 PC 의 더 새로운 Drive → 덮기 전 `conflicts/` 사본(파일당 3개)
- 자율 ③: 자막 편집 무결성 — 편집 시 옛 영문 줄 비움 · 진행 중 AI 생성 무효화 · 프리뷰 재구성 시 편집 문구 재타이밍 · A/B 변형 적용 시 “n/N편” 장식 유지

## Notes
- ⚠️ 분기 충돌 사본은 보관만(자동 병합 · UI 노출 없음) · 장부 없는 기존 설치의 첫 분기는 보관 불가
- ⚠️ BGM/태그 알파는 Step 18 이후 관측부터 누적(과거 이력은 소급 불가)
- ⚠️ `.git`은 `D:\Program` — git 명령에 `--git-dir=D:\Program\.git --work-tree=D:\Program\PromptFlowStudio` 필요
