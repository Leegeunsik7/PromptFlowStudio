# Current Project Status
- Active Milestone: 【Step 2 — 컷 단위 일시 오류 재시도 + SHA-256 디스크 체크포인트(멀티컷/시리즈 렌더)】
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Mobile net8.0-ios CoreCompile 0W/0E · ✅ Tests Release 0W/0E
- Tests: ✅ 1160/1160 PASS (전체 스위트, Release) · 신규 3건
- Updated: 2026-11-16 (KST)

## Completed
- ♻️ `CutRenderCheckpointService` — SHA-256 컷 키(정규화) · `{LocalAppData}\PromptFlowStudio\cut_checkpoints\{key}.mp4` · 24h 만료 · MP4 무결성 검증 · 임시파일 원자 이동 · 30개 상한 정리
- 🔁 `CutRetryPolicy` — 최대 2회 재시도(1500ms → 3500ms) · HttpRequestException/타임아웃/429·502·503·504만 · 401/403/잘못된 키 즉시 실패 · 호출자 취소 즉시 전파
- 🎬 `VideoEngineRenderExecutionEngine` — 컷 작업(CutIndex) 체크포인트 적중 시 프로바이더 생략(`♻️ 체크포인트 컷 재사용 (컷 #N)`) · 컷 성공 즉시 저장 · 재시도 시 실패 저널 회수
- 🔌 DI: `MainForm.cs` + `MauiProgram.cs` Singleton · 레거시 생성자는 무동작 기본값(Null 체크포인트)
- 🧪 `CutCheckpointAndRetryTests` — 저장/재사용/만료/손상/정리 · 재시도/인증 즉시 실패/취소 · 부분 실패 후 재실행 시 완료 컷 재사용

## Next
- 체크포인트 재사용 알림(`checkpoint.notice`) 렌더 큐 UI 노출
