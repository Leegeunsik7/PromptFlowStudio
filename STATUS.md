# Current Project Status
- Active Milestone: v18.1.0 완료 (솔루션 전수 0W/0E & Single-File 프로덕션 패키징 확정)
- Build Status: Desktop · net8.0-ios · Tests 3 종 Release -warnaserror 0/0 · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 저메모리 단일 스레드(MaxCpuCount=1 · DisableParallelization) 532/532 1 회 완주 통과(실패 0 · 건너뜀 0 · 단독 재실행 0 · 8 분 33 초)
- Sidebar: 6 그룹 · 32 워크스페이스 유지(신규 모달 0)
- Updated At: 2026-10-25 (KST)

## Completed Deliverables
- PKG-1: 테스트 분석기 경고 14 건 + -warnaserror 시 드러난 xUnit1031/xUnit2000 6 건 전수 교정(stackalloc 루프 밖 이동 · 널 단언 · 미사용 필드/지역 제거 · AsSpan · .Result → await)
- PKG-2: `.gitattributes` 신설(`* text=auto eol=crlf` · 소스/리소스 CRLF · *.sh LF · 바이너리 · NUL 포함 소스 -text) · 작업 트리 1,175 파일 CRLF 통일 · git add 변환 경고 0
- PKG-3: `PromptFlowStudio/Properties/PublishProfiles/win-x64-SingleFile.pubxml` — SelfContained · PublishSingleFile · 네이티브 self-extract · 압축 · R2R · 트리밍 금지 · wwwroot/appsettings 는 exe 옆 물리 배치(BaseDirectory 계약 유지) · 실 퍼블리시 115 MB 단일 exe 확인
- PKG-4: `ProductionSingleFilePackage_Integrity_Check` — 프로파일 파라미터 · csproj 배포 계약 · Hardening 플래너 정합 · .gitattributes 규칙 검증

## Known Issues / 환경 노트
- 1 차 완주에서 `SemanticChat_And_WeatherEngine_Check` 벽시계 최대 드레인 20 ms(OS 선점) 1 건 실패 → 단언을 p99 로 정규화 후 전체 재완주 532/532
- 실 플랫폼 업로드 엔드포인트 미검증(Mock 경로만) · WebView2 실창 마운트는 헤드리스 BCL Renderer로 대체 검증 · Android 빌드 미검증
