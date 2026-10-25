# Current Project Status
- Active Milestone: v18.2.2 완료 (StoryV 실기 오디오 스트림 실결선 & 팩토리 관통)
- Build Status: Desktop · net8.0-ios · Tests 3 종 Release -warnaserror 0/0 · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 저메모리 단일 스레드(MaxCpuCount=1 · DisableParallelization) 535/535 1 회 완주 통과(실패 0 · 건너뜀 0 · 단독 재실행 0 · 11 분 31 초)
- Sidebar: 6 그룹 · 32 워크스페이스 유지(신규 모달 0)
- Updated At: 2026-10-25 (KST)

## Completed Deliverables
- AUD-1: `Services/Storyboard/StoryboardAudioSourceStreamBinder.cs` — WAV RIFF/원시 PCM 16-bit 소유 버퍼 바인딩(용량 내 0 B) · 레이트/채널/길이 · TTS 단어 경계 → VAD → 실측 길이 비례 순 `Resync`(오디오 바인딩 시 음절 추정 배제) · `StoryboardAudioTrack` 0-카피 스냅샷/낡은 버퍼 차단
- AUD-2: 브리지 `Push(seq, audio)` → `ShortsFactoryPipelineContext.SourceAudio` 0-카피 · ② 자막 시작 = 실측 발화 · ③ `ApplyRetention(pcm16, rate, ch, …)` 48 kHz 최근접 무할당 소비 · ④ fMP4 ipcm 트랙 내레이션 합성(`NarrationSamples`) · 낡은 오디오 = 기존 소스 PCM 폴백
- AUD-3: 스토리보드 툴바 🎙️ 오디오 파일 불러오기(WAV/PCM/단어 경계 CSV) · 포맷/길이/샘플레이트/정렬 출처 배지 · 샷 인스펙터 즉시 실측 리프레시 · 🎬 전송 시 오디오 동반
- AUD-4: `Storyboard_RealAudioBinding_And_FactoryPassthrough_Check` — WAV 파싱 · 실측 재동기화 0 B · 0-카피 관통 · ②~④ 렌더 소비 · 낡은 오디오 폴백

## Known Issues / 환경 노트
- 압축 오디오(MP3/AAC) 디코딩 미지원(Pure BCL · WAV PCM 16-bit 만) · 실 TTS 엔진 단어 경계 자동 수신 미결선(CSV 수동 로드) · 실 플랫폼 업로드 엔드포인트 미검증 · Android 빌드 미검증
