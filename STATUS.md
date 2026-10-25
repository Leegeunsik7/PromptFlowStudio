# Current Project Status
- Active Milestone: v18.2.4 완료 (StoryV 순수 C# MP3 파서 & -14 LUFS 노멀라이저)
- Build Status: Desktop · net8.0-ios · Tests 3 종 Release -warnaserror 0/0 · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 저메모리 단일 스레드(MaxCpuCount=1 · DisableParallelization) 537/537 1 회 완주 통과(실패 0 · 건너뜀 0 · 단독 재실행 0 · 9 분 29 초)
- Sidebar: 6 그룹 · 32 워크스페이스 유지(신규 모달 0)
- Updated At: 2026-10-25 (KST)

## Completed Deliverables
- MP3-1: `Services/Storyboard/Mp3FrameHeaderBitstreamParser.cs` — 11 비트 싱크 · MPEG-1/2/2.5 Layer III 비트레이트/샘플레이트/패딩 해석 · ID3v2/ID3v1 건너뜀 · 연속 2 프레임 싱크 확정 · 손상 구간 재동기화 · Xing/Info/VBRI · LAME 지연/패딩 → 프레임 수 · TimeSpan 재생 시간(0 B)
- MP3-2: `LufsLoudnessComplianceNormalizer.cs` — BS.1770-4 K-가중(임의 레이트) · 400 ms/75 % 블록 · 절대 −70/상대 −10 LU 게이팅 · 4× 오버샘플 True Peak · −14 LUFS 게인을 −1 dBTP 상한으로 제한 → `LufsLoudnessReport`(0 B)
- MP3-3: 바인더 MP3 수용(`AudioContainer.Mp3` · 프레임 구간 0-카피 · 실측 길이 비례 재동기화 · 음절 추정 배제) · PCM 바인딩 시 라우드니스 실측 → `StoryboardAudioTrack.Loudness/GainFactor` → 팩토리 ③ 내레이션 게인 적용 · 툴바/AI 음성 `.mp3` 수용
- TEST: `Storyboard_Mp3BitstreamParser_And_LufsNormalizer_Check` — CBR/Xing/VBRI 길이 1 ms · EBU −23 LUFS 기준 · True Peak 제한 · MP3/PCM → 팩토리 게인 관통 · 핫패스 0 B

## Known Issues / 환경 노트
- MP3 는 디코딩 없이 비트스트림만 바인딩 → 라우드니스 미계측(게인 1) · 팩토리 ③ 은 기존 소스 PCM 사용 · 실 TTS 네이티브 단어 경계 미방출(VAD/실측 길이 분배 폴백) · Android 빌드 미검증
