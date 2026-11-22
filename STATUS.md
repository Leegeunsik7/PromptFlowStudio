# Current Project Status
- Active Milestone: ✅ Step 31 — 다국어 지역 채널 분기(ko-KR · en-US · ja-JP · es-ES) · 로캘 훅/자막/TTS 각색 · 로캘 업로드 패키지 · 일/서 Studio 분석 수집
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⏸ Mobile net8.0-ios — `ios` 워크로드 미설치(미검증)
- Tests: ✅ 1344/1344 — 전체 단일 실행 통과(13분 39초 · 종료 코드 0) · Step 31 신규 `PlutoMultiLanguageChannelTests.cs` 9건
- Updated: 2026-11-23 (KST) · 기준 `b934104` · 불변식 `CorePlutoFileCount=9` · `MergeableHubCount=6` · `CompanionSyncFileCount=2` 유지

## Completed — Step 31
- 🌐 `PlutoMultiLanguageChannelService`(DI 양쪽 Singleton) — 내장 4 프로필(훅 스타일 · 편 토큰 `1/3편`/`Ep.1/3`/`第1/3話`/`Ep. 1/3` · CJK 15/11cps vs 라틴 34/17cps · 음성 · 기본 해시태그) · 사용자 로캘 등록 · 안전 영역 줄바꿈 · TTS 0.85x~1.30x 예산
- 🎞️ 카탈로그 `TargetLocale`(기본 ko-KR) · `ParentClipId` · `BranchLocaleVariantAsync` → `{Base}::{Locale}` 독립 YouTube 1:1 · 성숙도 · 기획 채점 · 병합 시 로캘 필드 빈 쪽 채움
- 📋 로캘 업로드 패키지(A/B 제목 토큰 1회 보존 · 로캘 훅/설명/CTA · `#ショート`/`#cortos`) · `{base}_{locale}_publish_pack.txt`(BOM · CRLF · 원자 · 링크 가드)
- 📊 Studio 일/서 헤더(`視聴を選択(%)`/`Se quedaron a ver (%)` → Hook3s) · 언어 `ja`/`es` · 행 `ExportLanguage` · `Workspace.razor` KO · EN · JA · ES 칩(헤더 밖)
- 🧠 자체 추가: 정확 제목 교차 로캘 오연결 재라우팅/모호 거부 · CJK/스페인어 편 토큰 인식(편 가드) · 스페인어 날짜 파싱 · ID 파생 변형 계보(구버전 PC 왕복 · 위조 · 경로 주입 차단) · 문자소 안전 줄바꿈 + 금칙 + 장면 전환 TTS 가드 · 로캘 전환 시 이전 패키지 폐기
- 🧯 기존 경합 수정: `NonBlockingOmnichannelPublishingGuard.Loop` — Join 시간 초과 후 `_wake` 폐기 시 테스트 호스트 종료 → 격리

## Notes
- ⚠️ `.git` 은 `D:\Program` — `--git-dir=D:\Program\.git --work-tree=D:\Program\PromptFlowStudio` 필요
- ⚠️ 성능·타이밍 테스트는 부하에 민감 — 전체 실행 전 `dotnet build-server shutdown`
