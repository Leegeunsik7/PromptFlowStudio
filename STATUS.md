# Current Project Status
- Active Milestone: 【Pluto Real-Time Previewer】 60fps 캔버스 Ken Burns + Web Audio 마스터 클록 + KO/EN 자막 크로스페이드
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio (`-p:NuGetAudit=false` · Mobile 미빌드)
- Tests: 전체 1021/1021 PASS (`~Previewer|~Motion` 29/29 · 신규 5)
- Updated: 2026-11-11 (KST) · SSE 코어 커밋 6f8d9b2 푸시 완료(origin/master·main) · 프리뷰어 미커밋

## Completed
- 🎞 `Services/Pluto/PlutoMotionCompiler.Preview.cs`: `ExportPreviewContract(Json)`(내부 `Resolve` 세그먼트 그대로 노출 · 이징 다항식 계수) · `PlutoMotionPreviewModel.Evaluate`(JS 미러) · `SubtitleAlpha`(150ms) · `ToJson/FromJson/SubtitlesJson`
- 🖌 `wwwroot/js/pluto-motion-previewer.js`: rAF 루프 · `setTransform(z,0,0,z,-x·W·z,-y·H·z)` 클램프 뷰포트 · `AudioContext.currentTime` 마스터 클록(불가 시 `performance.now()` 폴백) · BGM 루프 + SFX 3종(0.8/5.5/13.2s) 합성 · 8ms 페이드 무팝 스크럽/재배치
- 🧩 `Blazor/Components/PlutoMotionPreviewer.razor`: 9:16 캔버스 · ▶/⏸(Space) · 스크러버 0~15s · 0.5×/1×/2× · 파라미터 변경 시 in-place 핫스왑(`@inject` 없음 · `Js` 파라미터)
- 🖥 `Workspace.razor`: 사진 스토리 완료 시 프리뷰 표시 · 프롬프트 타이핑/BGM 트랙/디렉터 랭크 변경 → 핫스왑(`PlutoDirector.Changed` 구독/해제 쌍)
- 🔗 `wwwroot/index.html` + `PromptFlowStudio.Mobile/wwwroot/index.html` 스크립트 태그
- 🧪 `PromptFlowStudio.Tests/PlutoMotionPreviewerTests.cs`

## Notes
- 실제 WebView2 내 재생/오디오 동기/스크럽은 수동 검증 미실시(JS 평가기는 node 로 값만 확인) · Mobile(MAUI) 빌드 미검증
- 프리뷰 자막은 로컬 결정론 초안(`GenerateLocal`) — 렌더 시 LLM 편지와 문구가 다를 수 있음 · 자막 구간은 스펙대로 0-4/4-12/12-15(번인 SRT 1-4/5-10/11-14 와 상이)
- 프리뷰 SFX 는 JS 재합성(음색이 `PlutoAudioSynthesizer` WAV 와 동일하지 않음 · 오프셋/볼륨만 일치)
- 디렉터 랭크는 궤적 자체를 바꾸지 않음(궤적은 사진 스토리 플랜 산출) — 랭크 변경 시 자막/상태만 재평가
- 불안정: `TitanReleaseTests.ZeroTouchAutopilot_And_BurnInGuard_Check`(µs 시간 예산) 전체 실행 3회 중 1회 실패 · 단독/재실행 통과 · 프리뷰어와 무관
