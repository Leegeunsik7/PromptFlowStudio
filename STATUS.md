# Current Project Status
- Active Milestone: 【Pluto Streaming】 Zero-Alloc SSE 파이프 소비기 + In-Flight 증분 JSON 어셈블러 + 점진 UI 페인트
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio (`-p:NuGetAudit=false` · Mobile 미빌드)
- Tests: 전체 1016/1016 PASS (`~Streaming` 필터 14/14 · 신규 7)
- Updated: 2026-11-11 (KST) · 미커밋

## Completed
- 🌊 `Services/Pluto/PlutoStreamingPipeConsumer.cs`: `PipeReader`(4KiB) + `SequenceReader` 줄 프레이밍 · SSE `data:`/`[DONE]` + Ollama NDJSON(`done:true`) · 스택 `Utf8JsonReader`로 `choices[0].delta.content`/`message.content` 추출(줄 파싱 0B 실측)
- 🧩 `Services/Pluto/PlutoIncrementalJsonAssembler.cs`: 바이트 상태 기계(괄호 비트 스택 · 문자열/이스케이프 · `<think>`/산문/펜스 무시) · 루트 배열 또는 루트 객체 첫 배열의 요소가 닫히는 즉시 방출 · 절단 시 즉석 봉합 → `PlutoJsonSanitizer` 2차 구제
- 🔁 `PlutoLlmFailoverClient.StreamArrayAsync<T>` · `PlutoLlmBridge.StreamArrayAsync<T>` · `PlutoCustomLocalGateway.StreamArrayAsync<T>`(`stream:true` · 유휴 타임아웃 재무장 · 총 60초 · 취소는 서킷 미집계)
- 🎞 `PlutoMilestoneOrchestrator.OrchestrateFourCutPlanStreamAsync`(템플릿 선방출 → 슬롯별 스냅샷 · `StreamedSlotMask`) · `PlutoCopilot.StreamTripletVariantsAsync` · `PlutoSubtitleDirector.TryParseStreamedAct`
- 🖥 `Workspace.razor` 마일스톤: 스냅샷마다 스토리보드 4슬롯 재페인트 + ✅/⏳ 컷 표시 · `PlutoCopilotDrawer.razor`: 변형 카드 점진 표시 + `OnVariantStreamed` EventCallback
- 🧪 `PromptFlowStudio.Tests/PlutoStreamingPipelineTests.cs`

## Notes
- 체인 순서 유지: Gemini(배치 → 요소 재생) 우선 → 로컬 게이트웨이만 실시간 스트리밍. Gemini 가용 시 TTFT 이득 없음
- SSE 다중 `data:` 줄 이벤트는 줄 단위로 개별 처리(이벤트 단위 결합 안 함 — OpenAI 호환 서버는 단일 줄)
- `System.IO.Pipelines`는 BlazorWebView 패키지 전이 의존으로 해소(신규 NuGet 0) — Mobile(MAUI) 빌드는 미검증
- `OnVariantStreamed`는 Workspace에서 미구독 · HUD `gth-v` 카드 미추가(신규 DI 서비스 없음) · 실 로컬 SLM 서버 대상 수동 검증 미실시
