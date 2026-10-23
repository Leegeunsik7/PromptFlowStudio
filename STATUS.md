# Current Project Status
- Active Milestone: v17.91.0 완료 (Complete) — AI 자율 저작권 검역 · Content ID 세이프티 & BGM 리플레이서 스튜디오
- Build Status: Desktop Release -warnaserror 0/0 · net8.0-ios Release -warnaserror 0/0 · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 503 전량 회귀(단일 CPU · MaxCpuCount=1 · DisableParallelization) 502 통과 + 부하 타이밍 간헐 1 → `DistributedGpuCluster_And_CloudRenderingSwarm_Check`(v17.87 이후 동일 · silent fallbacks 판정) 격리 재실행 3/3 통과
- Sidebar: 7대 카테고리 · 167 엔트리 · 도움말 167/167 결함 0 · 크롤러 167 마운트 결함 0
- Updated At: 2026-10-23 (KST)

## Completed Deliverables
- CPY-1: `Services/CopyrightCompliance/AcousticFingerprintContentIdAnalyzerEngine` — 1024 Hann FFT · 800 홉(16.7 ms) · 12 로그 대역 상위 2 랜드마크 · (f1·f2·Δt) 해시 2^19 테이블 · 모의 상용 3 곡 DB · 0.5 s 창 오프셋 투표(커버리지 × 정밀도) 위험도 0~100 · ≥ 70 구간 10 ms 타임코드(실측 5000~13040 ms) · 프레임 < 1 ms · 재분석 무할당
- CPY-2: `RoyaltyFreeBgmAutoSubstitutionMixer` — 지문 오프셋 → 상호상관 미세 정렬 레퍼런스 LS 소거(−18 dB) · 랜드마크 노치(Q 10 · ≤ 48) · 보컬 사이드체인/잔여 −30 dB · v17.76 SFX 뱅크 기반 Lo-Fi/Synthwave/Hip-Hop 2 마디 루프 크로스페이드 · BS.1770 −14 LUFS(±0.5) · 피크 리미터 클리핑 0
- CPY-3: `PlatformPolicyBrandSafetyGuard` — 정책 50 종(선정성/혐오/위험) + 상표 10 종 · 정규화 Span 탐색(띄어쓰기/기호 우회 차단 · 허용 목록) · `***` 마스킹 · 큐 비례 1 kHz 비프 · 무할당
- CPY-4: `CopyrightComplianceAuditStudioModal` + `CopyrightComplianceStudioRunner`(파형 위험 구간 Red Highlight · 원본/치환 비교 · 재생 헤드 · Safe to Publish 뱃지) · 카탈로그 `_copyrightCompliance`(4번) · 도움말 1행 · HUD `gth-v1791`(`CpyRun`) · DI 양쪽
- CPY-5: `CopyrightCompliance_And_ContentIdAnalyzer_Check`(TimingSensitive) · v17.90 카탈로그 단언 증가 허용

## Known Issues / 환경 노트
- 레퍼런스 소거는 모의 DB 원본 합성음 기준(실환경은 권리자 레퍼런스 파일 필요) · 데모 클립/음원은 절차적 합성
- 재생은 헤드 시뮬레이션(실 오디오 출력 미결선) · GUI 육안 미검증 · Android 빌드 미검증
