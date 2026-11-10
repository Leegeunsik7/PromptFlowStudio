# Current Project Status

- Active Milestone: 【Pluto Bilingual Subtitle Director】 💌 KO/EN 이중 감성 편지 자막 · 2줄 FFmpeg 번인 · 이중 언어 `.srt`
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (Mobile: 로컬 Android SDK 부재로 미빌드)
- Tests: `~Subtitle|~Pluto` 56/56 · `~StockSuite|~EscapeDrawText|~Publish` 88/88 PASS (호스트 RAM 제약으로 전체 스위트 미실행)
- Updated: 2026-11-10 (KST)

## Completed
- 🧱 `PlutoSubtitleAct`(KoreanText/EnglishText/Text 호환) · `PlutoSubtitleScript.Act{1..3}En` · `Acts` · `ToBilingualClipboardText()`
- 🧠 LLM 스키마 `[{"act","startSec","endSec","ko","en"}]`(또는 `{"acts":[…]}`) · KO+EN 쌍 유효 막만 채택 · 나머지 로컬 쌍 보강([부분 구제])
- 🍼 8테마(birthday·hundred·walk·sleep·bath·pet·season·default) KO/EN 템플릿 쌍 · EN 호칭 `{n}`(기본 → "My little one")
- 🗜 `PlutoPromptCompressor` 자막 컴팩트 스키마 이중 언어화(토큰 ≥50%↓ 유지)
- 🎬 `SubtitleCue.SecondaryText` — KO 24pt 웜 크림 `h-164` + EN 16pt 반투명 `h-120`(4K 2배) · 전량 `EscapeDrawText` · `⚠ 자막 생략` 폴백 유지
- 📝 `BuildSrt` — 블록당 KO/EN 2줄(보조 없음 → 기존 단일 줄 호환)
- 🖥 `PublishModal` 막별 KO/EN 미리보기 · 📋 이중 언어 복사 · Workspace 툴팁 갱신

## Next
- 모바일 빌드 검증(Android SDK 확보 시) · 전체 스위트 회귀(여유 RAM 확보 시)
