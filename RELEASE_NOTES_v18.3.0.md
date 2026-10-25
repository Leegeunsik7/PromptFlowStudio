# PromptFlow Studio v18.3.0 — Production Release Lockdown

**Status: Production Release Frozen** · 2026-10-25 (KST) · 기준 커밋 `ca9709a`(v18.2.6) 이후 릴리스 락다운

- Release `-warnaserror` 0 경고 / 0 오류 — Desktop(`net8.0-windows`) · iOS(`net8.0-ios`) · Tests 3 종
- 기능 테스트 541/541 ALL PASS — 저메모리 단일 스레드(MaxCpuCount=1 · DisableParallelization) 1 회 완주, Flake 0 · 단독 재실행 0
- Known Issues: 0 (net8.0-android 는 SDK/JDK 미설치로 미검증 — 결함 아님, 환경 제약)

## v18.x 시리즈 하이라이트

### 1. 175 개 모달 → 32 개 슈퍼-워크스페이스 통폐합 (v18.0.0 ~ v18.0.1)
- 사이드바 6 그룹 · 32 엔터프라이즈 워크스페이스로 재편, 기존 175 엔진은 워크스페이스 탭 멤버로 흡수(신규 모달 0).
- 딥 파이프라인 상태 하이드레이션 · 키보드 내비게이션 정비.

### 2. AI 숏폼 팩토리 4 단계 E2E 렌더러 & MP4 비트스트림 직결 (v18.0.2 ~ v18.0.3)
- 원클릭 풀체인 렌더 오케스트레이터 + 리텐션 실 슬라이스 버퍼 바인딩 → 하드웨어 먹싱 익스포터 MP4 직결.
- 실 영상 파일 임포트 · 키프레임 썸네일 하드닝.

### 3. 소셜 퍼블리셔 Resumable 업로드 & 바이럴 그로스 전략 체인 (v18.0.4 ~ v18.0.6)
- 숏폼 → 퍼블리셔 핸드오프 브리지, 실 Resumable 업로드 세션 엔진 · 소셜 디스패치 하드닝.
- 바이럴 그로스 전략 파이프라인 딥 바인딩 및 팩토리 브리지.

### 4. 안정화 · 33.3 ms 프레임 예산 CI 표준화 (v18.0.7 ~ v18.1.0)
- 벽시계 프레임 예산 판정 상한을 30 FPS 방어선(`CiFrameBudget.Ms = 33.3`)으로 정규화 — 실측 Stopwatch 유지, 2 배 이상 회귀는 계속 검출.
- 실 디스크 익스포트 검증 · 데스크톱 런타임 스모크 하드닝 · 프로덕션 패키징 0 경고.

### 5. StoryV 시퀀서 — 오디오 정렬 · 9:16 다이내믹 크롭 · `.storyv` 매니페스트 (v18.2.0 ~ v18.2.6)
- 오디오 워드 바운더리 정렬 · TTS 직결 워드 바운더리 · 멀티샷 9:16 다이내믹 리프레임 트래킹.
- 실 오디오 스트림 바인딩 · MP3 비트스트림 파서 · −14 LUFS 라우드니스 노멀라이저.
- `.storyv` 시퀀스 매니페스트 영속화 · 샷 에디터 · 오디오 소스 자동 재연결(SHA-256 체크섬) 하드닝.

## v18.3.0 신규 — 릴리스 락다운

| 항목 | 내용 |
|---|---|
| `PromptFlowStudio.exe --smoke-test [--deep]` | 실 엔트리포인트와 동일한 DI 구성으로 컨테이너를 `ValidateOnBuild` 빌드(등록 전수 의존성 그래프 검증) → 팩토리/인스턴스 등록 · 필수 싱글톤 실해석 → 32 워크스페이스 카탈로그 · 설치 폴더 무결성 확인 후 UI 없이 종료 코드 보고(0 정상 · 1 누락 등록/예외). `--deep` 은 지연 생성 스튜디오 러너까지 전수 인스턴스화. win-x64 단일 파일 실기 기동 약 1.3 초. |
| `PromptFlowStudio.exe --release-checksums {dir}` | `PromptFlowStudio.exe` · `appsettings.json` · `wwwroot/**/*` SHA-256 을 `{hex}  {상대경로}` 형식(`sha256sum -c` 호환)으로 `release-checksums.sha256` 에 원자 기록 후 즉시 재검증(누락 · 변조 · 사후 주입 · 경로 이탈 탐지, `FixedTimeEquals` 비교). |
| `build_titan_release.ps1` 4.5 단계 | 서명 직후 스모크(5 초 가드 · 실패 코드 35) → 체크섬 매니페스트 기록(실패 코드 36). |
| 테스트 | `SingleFileBinaryProcessBootstrap_Check`(퍼블리시 바이너리 서브프로세스 기동 · 표준/에러 리디렉션 · 5 초 타임아웃 · 신선도 검사, 바이너리 부재 시 인프로세스 폴백) · `ReleaseBundleChecksumGenerator_Check`. |

## 검증 명령

```
dotnet build PromptFlowStudio/PromptFlowStudio.csproj -c Release -warnaserror
dotnet test PromptFlowStudio.Tests/PromptFlowStudio.Tests.csproj -c Release --no-build -- RunConfiguration.MaxCpuCount=1 RunConfiguration.DisableParallelization=true
PromptFlowStudio.exe --smoke-test            # 종료 코드 0
sha256sum -c release-checksums.sha256        # 배포 루트에서 전 항목 OK
```
