# Current Project Status
- Active Milestone: 【SNS 계정 연동】 YouTube 원클릭 브라우저 OAuth 루프백 로그인 + SNS 자격 UI 간소화
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (net8.0-android 미검증: SDK/JDK 미설치)
- Tests: 전체 723/723 PASS(직렬) · FastUnit 491/491 PASS(병렬 ~28s)
- Updated: 2026-10-29 (KST)

## Completed
- OAuth: `Services/Publishing/YouTubeOAuthFlowService` — HttpListener 루프백(127.0.0.1:5000/oauth2callback · 점유 시 가용 포트) · PKCE S256 · state FixedTimeEquals · offline+consent → RefreshToken 교환 · 채널 @handle 조회 · `SaveSns` 암호화 영속 · 5분 대기/30초 요청/지수백오프 · 연동 해제(revoke best-effort)
- 클라이언트 해석: 환경변수 `PFS_YOUTUBE_CLIENT_ID/SECRET` → appsettings `YouTube:ClientId/ClientSecret` → 저장소(하드코딩 0)
- 자격 모델: `SnsCredentials` + `YouTubeChannelName` · `InstagramTokenSavedUtc`(기본값 "" · 구버전 파일 하위호환)
- UI `/app-settings`: 미연동 `[🔗 유튜브 계정 로그인 연동]` / 연동 `내 채널: @채널명 (연결됨 🟢)` + `[연동 해제]` · 수동 입력은 `고급` 접힘 · Instagram 토큰 발급 안내 팝오버 · 60일 토큰 D-day 배지(유효/임박/만료)
- DI: `MainForm.cs` + `MauiProgram.cs` 양쪽 등록

## Known Issues
- 번들 appsettings `YouTube:ClientId/ClientSecret` 공란 — 배포 전 Google Cloud Desktop OAuth 클라이언트 값 주입 필요(미주입 시 로그인 버튼 비활성 안내)
- 실 Google/Meta 엔드포인트 미검증(가짜 핸들러 + 실 루프백 리스너 테스트) · 모바일은 루프백 미지원(서비스만 DI)
