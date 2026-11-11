# Current Project Status
- Active Milestone: 【Pluto DSP 결선】 익스포터 이중 마스터링 제거 · Workspace 프리뷰어 비트 싱크
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio (`-p:NuGetAudit=false` · Mobile 미빌드)
- Tests: 전체 1039/1039 PASS (`~Dsp|~BeatSync|~Previewer|~Export` 98/98 · 신규 2)
- Updated: 2026-11-11 (KST)

## Completed
- 🎛️ `StockSuiteExporter.BabyStudio.cs`: `mastered` 플래그 — C# 트루피크 마스터링 WAV 투입 시 `loudnorm`·`volume=0.25` 생략(페이드만 · AAC 192k/48k 직행) · 마스터링 OFF/실패 → 레거시 체인 폴백(BGM 단독 · BGM+SFX 그래프 공통)
- 🥁 `PlutoMotionCompiler.Preview.cs`: `BuildBeatSyncedPreview`(비트 검출 → 막 경계/SFX 큐 스냅 · 폴백 = 원본) · 계약 `sfx` 배열(없으면 키 생략) + `FromJson` 왕복
- 🎞 `PlutoMotionPreviewer.razor` `SfxCues` 파라미터 · `pluto-motion-previewer.js` 계약 큐 우선(무효 → 기본 큐) · 큐 변경 시 오디오 재배치
- 🧩 `Workspace.razor`: BGM WAV 확보 시 백그라운드 비트 스냅(세대 가드) → `MotionPreviewPath`/`SfxCues` 바인딩 · `🎵 비트 싱크 퀀타이즈 적용됨 (BPM, 비트 정렬)` 배지

## Notes
- 마스터링 경로 BGM 레벨은 이제 −14 LUFS 그대로(이전 대비 약 +12 dB) — 실청취 밸런스(SFX 대비) 수동 확인 필요
- 비트 그리드는 합성 펄스로만 검증 — 실제 `AiGenerativeMusicEngine` BGM 스냅 품질 수동 확인 필요
- Mobile(MAUI) 빌드 미검증 · 전체 테스트 약 12분
