# Current Project Status

- Active Milestone: 【Pluto Local SLM Optimization】 🗜 프롬프트 다이어트 · 🩹 JSON 자가 치유 · 🧷 KV-캐시 불변 접두 · ⏱ 단계형 진행/즉시 취소
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (Mobile: 로컬 Android SDK 부재로 미빌드)
- Tests: `~Pluto|~Publish` 76/76 PASS (호스트 RAM 제약으로 전체 스위트 미실행)
- Updated: 2026-11-10 (KST)

## Completed
- 🩹 `PlutoJsonSanitizer.Sanitize` — think/펜스/서두·후미 제거 · 후행 쉼표 · 잘린 문자열/키/괄호 봉합 · 문자열 내 개행 정규화(문자열 인식 스캐너)
- 🔌 결선: `PlutoLlmBridge.TryParse`(Copilot·Triplet·Subtitle·Milestone·Caption·Radar·Trend 공통) · 게이트웨이 단계 응답 · `PlutoCustomLocalGateway.NormalizeContent` · `PublishViewModel` SEO
- 🗜 `PlutoPromptCompressor` — 8개 Pluto/SEO 지시문 컴팩트 스키마(토큰 ≥50%↓, 트리플렛 ~60%↓) · `system`=불변 접두 · `user`=`---`/[CTX] 앵커/[REQ]
- 🔁 체인: 게이트웨이만 컴팩트 프롬프트, Gemini는 원본 서술형 유지
- ⏱ `PlutoCopilotDrawer` — 3단계 진행(0/1/2.5초) · ⏹ 생성 취소 · `Open=false` 시 즉시 취소(진행 HTTP 중단)
- 🧪 `PlutoLocalOptimizationTests` 3종 · SEO 게이트웨이 어서션을 컴팩트 지시문 기준으로 갱신

## Next
- 모바일 빌드 검증(Android SDK 확보 시) · 전체 스위트 회귀(여유 RAM 확보 시)
