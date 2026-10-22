# Current Project Status
- Active Milestone: v17.83.0 완료 (Complete) — 옴니채널 라이브 RTMPS 송출 & 인터랙티브 채팅 스튜디오
- Build Status: Desktop Release -warnaserror 0/0 · net8.0-ios Release -warnaserror 0/0 · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 494/495 전량 회귀(단일 CPU · MaxCpuCount=1 · DisableParallelization) + 실패 1 `DistributedGpuCluster_And_CloudRenderingSwarm_Check` 격리 재실행 2/2 통과(60 ms 하트비트 벽시계 타이밍)
- Sidebar: 7대 카테고리 · 159 엔트리
- Updated At: 2026-10-22 (KST)

## Completed Deliverables
- RTM-1: `LiveRestream/ZeroAllocMultiTargetRtmpStreamerEngine` + `RtmpWire`(C0/C1/C2 · AMF0 · 청크 재조립 · FLV AVC/AAC) — 4096 청크 1 회 직렬화 → YouTube/Twitch 제로-카피 분기 · 신규/드롭 타겟 IDR 게이트 · 무할당
- RTM-2: `OmniLiveChatSuperChatAggregatorEngine` — YouTube 폴링 JSON(Utf8JsonReader · id 중복 제거 · pageToken) + Twitch IRC(bits/resub) → 256 순차 링 → KRW 중요도 → v17.80 감정 + v17.68 제스처 · 무할당
- RTM-3: `AdaptiveBitrateNetworkCongestionGuard` — 100 ms 큐/RTT 정체 지수 · 6 → 4 → 2.5 Mbps 감쇄(1 s 홀드) / 5 s 복구 · IDR 커밋 전용(비정렬 전환 0)
- RTM-4: `OmniLiveRestreamStudioRunner` + `OmniLiveRestreamBroadcastStudioModal` · 카탈로그 `_liveBroadcastRestream` · HUD `gth-v1783`(`RstRun`) · DI · `TlsRtmpTransport`(SslStream · 키 환경 변수)
- RTM-5: `MultiPlatformRtmpRestream_And_AdaptiveBitrate_Check`(TimingSensitive · 루프백 해시 무결성) · 크롤러 159 모달 통과

## Known Issues / 환경 노트
- 호스트 CPU 경합 시 타이밍 민감 테스트 간헐 초과 가능(격리 재실행 통과 기조)
- 실 YouTube/Twitch RTMPS 인제스트 · 실 채팅 API/IRC 소켓 연결 미검증(루프백·와이어 포맷 시뮬레이션) · H.264/AAC 페이로드는 합성 비트스트림 · Android 빌드 · GUI 육안 미검증
