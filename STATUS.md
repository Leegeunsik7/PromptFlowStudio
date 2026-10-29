# Current Project Status
- Active Milestone: 【SNS 자동 발행】 YouTube Shorts · Instagram Reels 원클릭 퍼블리싱 + 자막 번들링
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (net8.0-android 미검증: SDK/JDK 미설치)
- Tests: 전체 721/721 PASS(직렬 9m41s) · FastUnit 489/489 PASS(병렬 ~26s)
- Updated: 2026-10-29 (KST)

## Completed
- YouTube: `Services/Publishing/YouTubeShortsPublisher` — OAuth 리프레시(만료 60초 전 갱신 · 401 강제 재발급) → resumable 업로드 → 옆 `.srt` 를 `captions.insert`(ko/en 감지) · 자격 미구성 시 모의 발행
- Instagram: `InstagramReelsPublisher` — 공개 URL 은 기존 `InstagramReelsService` Graph(REELS 컨테이너→media_publish) 위임 · 웹훅 브릿지(Zapier/Make · 토큰 미포함) 폴백 · 로컬 브릿지(공개 동의 필수)
- 포맷터: `SnsPublishFormatter` 훅 제목 `#Shorts #AI`(≤100) · 대본 타임스탬프 설명 · 태그(≤30/500자) · 릴스 캡션(≤2200 · 해시태그 ≤30)
- UI: `PublishModal.razor` + 싱글톤 `PublishViewModel`(채널 체크 · 공개 범위 기본 비공개 · YouTube 예약 · 채널별 진행률/취소 · 실패 격리) — Storyboard · AI 숏폼 합본 후 `🚀 SNS 즉시 발행`
- 설정: `/app-settings` SNS 채널 연동(관리자) — `AppSettingsManager.SaveSns` 필드별 LocalCipher 봉인 `sns_credentials.json` · 연결 테스트/유효성 검사 배지

## Known Issues
- 기존 `Wan.IYouTubePublishService` 와 이름 충돌 회피로 계약명 `IYouTubeShortsPublisher`/`IInstagramReelsPublisher` 사용(작업 오더 파일명과 상이)
- 실 Google/Meta 엔드포인트 미검증(가짜 핸들러 테스트만) · Graph 예약 발행 미지원(예약은 YouTube 만)
- `PublishModal` 은 데스크톱 전용(Mobile.csproj RazorComponent Remove) · 모바일은 서비스만 DI
