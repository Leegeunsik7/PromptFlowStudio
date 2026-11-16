# Current Project Status
- Active Milestone: 【Pluto 4.6+ — 프로젝트별 크리에이터 번들(subtitles.srt · SNS_게시문.txt · thumbnail.jpg)】
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Mobile net8.0-ios CoreCompile 0W/0E
- Tests: ✅ 1157/1157 PASS (전체 스위트, Release) · 신규 3건
- Updated: 2026-11-16 (KST)

## Completed
- 🎁 `StockSuiteExporter.Project` — 프로젝트/편 폴더에 `subtitles.srt`(SubRip, 3막 자막) · `SNS_게시문.txt`(Shorts / Reels·TikTok, CRLF) 원자적 기록
- 🖼️ `thumbnail.jpg` 커버 프레임 — ffmpeg 존재 시 1.0s → 0.2s 시크 순 시도 · JPEG 매직 검증 · 실패 시 무예외 생략
- 🧾 `project_manifest.json` 번들 플래그 · `StockSuiteExportResult.HasSubtitlesSrt/HasSnsCopyText/HasThumbnailJpg`
- 🔔 `Workspace.razor` — 성공 토스트에 `DescribeCreatorBundle` 요약 · 감성 자막 OFF 시 몽타주 큐 폴백
- 🧪 `EngineRoutingAndProjectExportTests` — SRT 타임코드 · 사이드카+매니페스트 플래그 · ffmpeg 부재 시 정상 완료

## Next
- Step 2: 컷 단위 일시 오류 재시도 + SHA-256 디스크 체크포인트 캐시(멀티컷/시리즈 렌더)
