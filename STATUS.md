# Current Project Status
- Active Milestone: 【Step 19 — SHA-256 동기화 가드 · 잠금 재시도 · 기준선 부트스트랩 · 충돌 합집합 병합 UI · BGM/태그 컨텍스트 알파 소급 · 자율 하드닝】 ✅ 완료
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⏳ Mobile net8.0-ios — `ios` 워크로드 미설치(미검증)
- Tests: ✅ 1251/1251 — 전체 단일 실행 통과(14분 38초 · `dotnet test` 종료 코드 0 · 기존 1243 + Step 19 8)
- Updated: 2026-11-20 (KST) · 기준 `12d560e`

## Completed — Step 19
- #️⃣ SHA-256 내용 동일성 가드: 시각 차(> 2초)여도 내용이 같으면 쓰기 반환 · 미러 갱신 · 충돌 사본 없이 장부만 갱신 · 장부 = {로컬/Drive 시각 · 해시}(Step 18 시각 전용 장부 그대로 읽힘)
- 🧾 기준선 부트스트랩: 기록 없는 핵심 파일 중 내용이 같은 것만 기준선 기록(기동 · 백업 검증) → '첫' 분기도 감지 · 분기 판정은 해시 기준
- 🔒 Drive 데몬 일시 잠금(공유/잠금 위반 · 접근 거부) 유한 지수 백오프 + 지터 재시도(기본 5회) — 쓰기 반환 · 미러 · 해시 · 장부 · 충돌 사본
- ⚔️ `PlutoConflictMergeService`: JsonNode 합집합 병합(실험 장부 · 오답 노트 · 렌더 카탈로그 · 시리즈 아크 · 학습 메모리 · 모르는 필드 보존 · 결정론) · 병합/사본 복원/무시 · 환경설정 충돌 섹션 · 두뇌 리포트 본문 표시(상단 3행 헤더 불변)
- 🧬 `PlutoContextAlphaBackfill`: 과거 A/B 장부 → (테마 분류 Domain, 몽타주) BGM · 태그 컨텍스트 알파 1회 소급(완료 표식 · 실험 ID 표식) · 태그는 렌더 카탈로그 조인 · 짝 평균 기준선 · 훅/룩/기준선/다목적 불변
- 자율 ①: 해결은 '적재 직전'에만 적용(`sync_pending/`) — 실행 중 서비스의 메모리 상태가 병합 결과를 되덮는 경로 차단 · 손상 병합은 `.rejected` 보존
- 자율 ②: 첫 복원(로컬 부재)도 검증 + 원자 — 0 B/잘린 Drive 자리표시를 미러로 굳히지 않음 · 충돌 사본 같은 초 덮어쓰기 · 같은 내용 중복 방지
- 자율 ③: 이후 A/B 판정도 같은 공식으로 실시간 컨텍스트 알파 기록 · 소급과 실험 ID 단위 정확히 1회
- 🧪 기존 플래키 1건: `NeuralHairCloth_And_GarmentDynamics_Check` 8k 가닥 16.6 ms 단언 → 공용 `CiFrameBudget.Scaled`(유휴 = 동일 상한 · 부하 비례 완화)

## Notes
- ⚠️ 병합/복원은 다음 실행(해당 허브 첫 적재) 때 반영 · 메타 보정 · 정체성 볼트 · 소셜 레이더 · 바이럴 패턴은 의미 병합 미지원(복원/무시만)
- ⚠️ 소급은 렌더 포맷을 '몽타주'로 가정(A/B 는 몽타주에서만 생성) · 태그는 카탈로그(최근 100건)에 남은 변형 영상만
- ⚠️ 실제 Google Drive 클라이언트 대상 검증은 아직 없음(임시 폴더 · 파일 잠금 모사로만 검증)
- ⚠️ `.git`은 `D:\Program` — git 명령에 `--git-dir=D:\Program\.git --work-tree=D:\Program\PromptFlowStudio` 필요
