# Current Project Status
- Active Milestone: 【Pluto 4.1 하드닝 — 2/4 큐레이션 교체 · 재정렬 HITL】
- Build: ✅ Release `-warnaserror -p:NuGetAudit=false` 0/0 (PromptFlowStudio · Tests)
- Tests: ✅ 1112/1112 PASS (`PlutoHardeningAndStoryTests` 3건)
- Updated: 2026-11-14 (KST)

## Completed
- 🎬 1/4 `PlutoMontageStoryGenerator`(DI 양쪽) — 컷 경계 정렬 3막 자막 · Gemini → PlutoCore → 로컬 게이트웨이 → 템플릿 · 오답 노트 제약
- 💌 1/4 `SubtitleCue` 소수초(`StartPreciseSec/EndPreciseSec`) · SRT 밀리초 · 프리뷰/렌더 동일 큐
- 🔁 2/4 `IPlutoPhotoCurationEngine.SwapCandidate` — 탈락 후보 승격(막 순번·라벨 승계 · 초점 [0.2,0.8]² 클램프) · 밀려난 컷 `LowerRankCut` · 제외 집계 재산출
- ↕ 2/4 `MoveSelectedCut` — 순번 i+1 · Act1/Act2/Act3 라벨 재부여 · AllEvaluated 반영(순수 변환)
- 🖱 2/4 Workspace `[선별 상세]`: 선택 컷 `[▲]/[▼]` · 탈락 후보 `[채택 교체]`(→ 마지막 슬롯) → `ApplyUpdatedCurationAsync`(큐·초점 동기화 → 플랜/프리뷰·BGM 비트 → 3막 자막 갱신)

## Next
- 3/4 `PlutoDriveHealthService` + Settings 백업 상태 카드(7파일 · `LastMirrorError`)
- 4/4 System.Drawing `OperatingSystem.IsWindows()` 가드 · Mobile 빌드 검증
