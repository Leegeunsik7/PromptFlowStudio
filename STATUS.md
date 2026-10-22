# Current Project Status
- Active Milestone: v17.87.0 완료 (Complete) — AI 라이브 클라우드 아카이브 볼트 & 콜드 스토리지 스튜디오
- Build Status: Desktop Release -warnaserror 0/0 · net8.0-ios Release -warnaserror 0/0 · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 498/499 전량 회귀(단일 CPU · MaxCpuCount=1 · DisableParallelization) + 실패 1 `DistributedGpuCluster_And_CloudRenderingSwarm_Check`(silent fallbacks · 벽시계 타이밍) 격리 재실행 1/1 통과
- Sidebar: 7대 카테고리 · 163 엔트리
- Updated At: 2026-10-22 (KST)

## Completed Deliverables
- ARC-1: `LiveArchive/ZeroAllocLiveSegmentRecordingEngine` — v17.83 송출 AU/AAC → 5/15 분 IDR 경계 분할 fMP4(ftyp+moov 후 GOP 단위 moof+mdat 1 회 Write+Flush) · `.part` → `.mp4` 원자 확정 · 충돌 시 마지막 완결 조각까지 절단 복구 · FNV 해시 · 핫패스 무할당
- ARC-2: `MultiCloudVaultArchivalGateway` — 순수 BCL SigV4(AWS 공식 벡터 2 종 일치 · 무할당 서명) · S3/R2/MinIO 8 MB 멀티파트(Initiate/UploadPart/Complete · 실패 시 Abort) · 타임아웃 · 지수 백오프 · 토큰 버킷 20 Mbps 셰이핑 · 시크릿 환경변수/메모리 한정 · 내장 SigV4 실검증 시뮬레이터 버킷
- ARC-3: `StorageCostLifecycleTieringGuard` — 여유 < 10 GB 시 업로드 완료본만 오래된 순 회수(미업로드 보존·위험 카운트) · 7/30/90 일 IA/Glacier/DeepArchive 태그 + 버킷 수명주기 XML · 월 비용 추정
- ARC-4: `LiveArchiveCloudVaultStudioRunner`(v17.83 탭 체인 — v17.84 클리퍼 보존 전달) + `LiveArchiveCloudVaultStudioModal` · 카탈로그 `_liveArchiveVault` · HUD `gth-v1787`(`LavRun`) · DI 양쪽
- ARC-5: `LiveArchiveCloudVault_And_SegmentRecording_Check`(TimingSensitive) · 크롤러 163 모달 결함 0
- 수정: 사이드바 스크롤 소실(그리드 행 높이 무제한 → 하단 메뉴 잘림) — `app.css` 셸 행/사이드바/nav `min-height:0` 고정

## Known Issues / 환경 노트
- 호스트 CPU 경합 시 타이밍 민감 테스트 간헐 초과 가능(격리 재실행 통과 기조)
- 실 클라우드 버킷 전송 미검증(시뮬레이터 버킷만) · 리허설 기본 저장소 인메모리(디스크 저장소 `DiskSegmentStore` 결선은 선택) · Android 빌드 · GUI 육안 미검증
