# Current Project Status
- Active Milestone: 【Pluto 멀티컷 몽타주 · 자동 색보정】 2~5장 → 15초 60fps 비트 xfade 몽타주
- Build: ✅ Release `-warnaserror -p:NuGetAudit=false` 0/0 (PromptFlowStudio · Tests)
- Tests: ✅ 1082/1082 PASS (Montage|Color|Motion 필터 42/42)
- Updated: 2026-11-13 (KST)

## Completed
- 🎨 `PlutoImageColorAnalyzer`: ≤128px 썸네일 LockBits · Rec.709 휘도/대비/섀도 크러시/역광/웜쿨 · 비Windows·판독 불가 → 중립 · 목표 휘도 0.54 정합([−0.12, +0.15])
- 🎨 `PlutoColorGradingCompiler`: GoldenHourWarm/VintageFilm16mm/NaturalPastel · 바이럴 태그/디렉터 레벨 룩 선택 · 오답 노트 차단 → NaturalPastel · `eq/colorbalance/vignette` Invariant
- 🎞️ `PlutoMontageCompiler`: 컷 중앙 역산으로 ΣT−(N−1)d=15s 구성상 보장 · ±0.25s 비트 스냅(최소 1.8s) · 프레임 정렬 · Push-In/Drift/Pull-Back · xfade 체인 → `[vout]`
- 📤 `StockSuiteExporter.ExportMontageVideoFromPhotosAsync`: 다중 `-i` + filter_complex + 자막 번인([부분 구제] 재시도) → 마스터링/더킹/SFX 먹싱 재사용 · 동시 실행 차단
- 🖥️ Workspace: 다중 선택(최대 5) · 색보정 배지 · `[🎞️ 15초 멀티컷 몽타주 렌더]` · DI 양쪽 등록
- 🧪 `PlutoMontageAndColorTests` 5종

## Notes
- 시그니처 편차: 스펙의 `Task<string>`/`PlutoSubtitleAct` 대신 기존 모션 경로와 동일한 `StockSuiteExportResult`/`SubtitleCue` 사용 + `preferredLook` 인자 추가
- 클립 초점은 현재 폴백 초점(아기/반려) 일괄 — 사진별 Vision 초점 결선은 후속
- Mobile(MAUI) 빌드 미검증 · 실제 FFmpeg 렌더 미검증(러너 모킹 테스트만)
