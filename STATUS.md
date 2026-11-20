# Current Project Status
- Active Milestone: 【Step 17 — Drive 허브 9종 · 무지연 시리즈 앵커 write-through · 시즌 분리 · 계층적 경험적 베이즈 콜드스타트 전이 · 톰슨–UCB · 자율 하드닝】 ✅ 완료
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⏳ Mobile net8.0-ios — `ios` 워크로드 미설치(미검증 · DI 결선은 소스 단언으로만 확인)
- Tests: ✅ 1235/1235 — 전체 단일 실행 통과(17분 50초 · `dotnet test` 종료 코드 0 · 기존 1227 + Step 17 8)
- Updated: 2026-11-20 (KST) · 기준 `c92d283`

## Completed — Step 17
- ☁️ Drive 핵심 허브 9종: `pluto_series_arc.json` 편입(`CorePlutoFileCount = 9`) — 검증 · 미러 · 새 PC 자동 복원 · `PROMPTFLOW_PLUTO_DRIVE=off` 격리 유지
- ⚡ `RecordRenderedEpisode`: 렌더 완료 즉시 메모리 아크 갱신(디스크는 병합 백그라운드 기록) → 연속 렌더 k+1편이 지연 없이 앵커 룩/BGM 상속
- 🆕 시즌: `[🆕 새 시즌 분리]`(1시즌 키 = 기존 키 · 2시즌부터 `@sN`) — 이전 시즌 텔레메트리 보존 · 카탈로그/성과는 영상 ID → 렌더 시각으로 시즌 귀속
- 📐 단일 편 조기 이탈: 1편이 (Domain, RenderMode) 유지율 기준선을 −5p 이상 밑돌면 3편 시리즈의 2편이 궁금증 훅 + 클리프행어 아웃트로 수령
- 💬 `[✍️ 자동 각색 적용]` ↔ `[💡 추천만 표시]` 토글(기본 자동 각색 · 영속) — 추천만: 자막 원문 보존 + 추천 배지
- 🧬 `PlutoEmpiricalBayes`: 지역 시행 < 3 컨텍스트는 α = n/(n + k) 로 전역 검증 사후에 수축(k = σ²/(τ₀² + σ²/n_g)) · `RankForContext` · 지역 통계 `cpat:` 키
- 🎲 `PlutoThompsonUcbSampler`: 활용 + (1−λ)·UCB1 + λ·사후 추첨(Normal · 다목적 근거는 Beta 혼합) + 후회 회복 사전 · `Random` 주입 시에만(미주입 = 기존 결정론 UCB1) · DI 양쪽 주입
- 🧠 두뇌 리포트 본문: 🧬 콜드스타트 전이 · 📺 시리즈 아크(시즌 · 추천만 표기) — 상단 3행 헤더 불변
- 자율 ①: 이중 언어 자막 — 에스컬레이션이 본문만 바꿔 옛 문장의 영문 번역이 남던 문제 수정(영문 짝 교체/확장)
- 자율 ②: 얼어붙은 로컬 미러 — Drive 사본이 2초 넘게 더 새롭고 유효 JSON 일 때만 로컬 갱신(더 새로운 로컬 · 손상/빈 Drive 사본은 불변)
- 자율 ③: 적재 전 write-through 내구성 — 디스크 이력 위에 재적용 · 미적재 부분 스냅샷 기록 금지 · 늦게 온 오래된 카탈로그 항목 무시 · Dispose 경합 안전

## Notes
- ⚠️ 오프라인 중 로컬에 쌓인 기록(로컬이 Drive 보다 새로움)은 덮이지 않지만 Drive 복귀 시 자동 병합되지도 않음(수동 검증 필요)
- ⚠️ 컨텍스트 특화는 훅 · 룩만(BGM · 태그는 전역 순위) · A/B 는 Workspace 에서 `(도메인, "몽타주")` 컨텍스트로만 호출
- ⚠️ `.git`은 `D:\Program` — git 명령에 `--git-dir=D:\Program\.git --work-tree=D:\Program\PromptFlowStudio` 필요
