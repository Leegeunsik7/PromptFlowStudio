# Current Project Status
- Active Milestone: v17.63.2 완료 (Complete) — 모바일 플랫폼 빌드 검증 & DI 하드닝
- Build Status: Desktop Release -warnaserror 0/0 · Mobile net8.0-ios Release -warnaserror 0/0 · net8.0-android 미검증(Android SDK/JDK 미설치 XA5300)
- Test Suite: 471/471 PASS (MaxCpuCount=2 · 신규 7)
- Updated At: 2026-10-20 (KST)

## Completed Deliverables
- MOB-1a: 모호 참조 CS0104 5건 — `Dispatcher`(MAUI 전역 using) 별칭 2파일 · `SpatialAnchor` 별칭(SpatialVideoStudioModal)
- MOB-1b: `ProcessCompat` 시임(iOS 계열 프로세스 미지원 → false/no-op 폴백) — Process.Start/Kill/TotalProcessorTime/StartTime/GetProcessesByName 137곳 · 45파일
- MOB-1c: `AesGcmCompat`(iOS 계열 관리형 GCM 폴백 · 네이티브와 바이트 동일 · 상수시간 GHASH) — AesGcm 사용 10파일 교체
- MOB-1d: 명명 MMF → 비-Windows 익명 매핑 폴백 2건 · 플러그인 ALC/FileSystemWatcher 가드 2파일 · CS1998 `#else` 2건 · iOS 햅틱 CA1422 억제(15.0 최소 타깃)
- MOB-2: Main.razor 도달 컴포넌트 20개 @inject 미등록 0 · 자동생성 서비스 168개 생성자 의존성 미해결 0(정적 분석)
- 회귀: `AesGcmCompatTests`(관리형↔네이티브 KAT 5 · 태그 변조 거부 · 데스크톱 네이티브 경로)

## Pending
- Android SDK + JDK 설치 후 net8.0-android 빌드 · iOS 실기기(Mac 페어링) 서명/AOT · GUI 육안
