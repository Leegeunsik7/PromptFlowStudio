# Current Project Status
- Active Milestone: v18.2.3 완료 (StoryV 내부 TTS 직결 & SRT/VTT 자막 파서 및 비주얼 캔버스)
- Build Status: Desktop · net8.0-ios · Tests 3 종 Release -warnaserror 0/0 · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 저메모리 단일 스레드(MaxCpuCount=1 · DisableParallelization) 536/536 1 회 완주 통과(실패 0 · 건너뜀 0 · 단독 재실행 0 · 8 분 45 초)
- Sidebar: 6 그룹 · 32 워크스페이스 유지(신규 모달 0)
- Updated At: 2026-10-25 (KST)

## Completed Deliverables
- TTS-1: `Services/Storyboard/InternalTtsWordBoundaryDirectEmitter.cs` — `SpeechWordBoundaryEvent` 수신(범위/역전/역순 거부) · 경계 미방출 엔진은 VAD 발화 구간을 음절+호흡 가중치로 분배 · UTF-8 소유 버퍼 직렬화 → 바인더 직결 · 원자 재동기화(0 B)
- SUB-2: `StandardSubtitleTimestampParser.cs` — SRT/WebVTT Span 1 회 스캔(BOM · NOTE · 큐 설정 · 태그 제거 · 시 생략) · 깨진/역전/역순 큐 건너뛰고 복구 · 쉼표/마침표 미세 구간 단어 경계 등록 → 샷 타임라인(0 B)
- CV-3: `StoryboardVisualCanvasRenderer.cs` — 샷 9:16 SVG(3 분할 · 아이레벨 · 실루엣 · 자막 안전영역 · 숏츠 UI 경계 1536) · 앵글별 안전영역 · 자막 줄 수 침범 판정 녹/적 배지(0 B 직렬화)
- UI: 스토리보드 툴바 🗣 AI 음성 생성 및 정렬(합성 → 직결 → 정렬) · 오디오 파일 불러오기에 .srt/.vtt 추가 · 샷 카드 9:16 캔버스 + 세이프존 배지
- TEST: `Storyboard_TtsDirectEmitter_And_SubtitleParser_Check` — 타임코드 · SRT/VTT 정합 · 구문 복원력 · 직결=CSV 동일 지문 · 세이프존 판정 · 핫패스 0 B

## Known Issues / 환경 노트
- 실 TTS 엔진(ElevenLabs/Edge/로컬) 네이티브 단어 경계 이벤트 미방출 → VAD/실측 길이 분배 폴백 · MP3 디코딩 미지원(MP3 합성 시 길이 기반) · Android 빌드 미검증
