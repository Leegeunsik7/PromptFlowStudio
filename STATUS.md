# Current Project Status
- Active Milestone: v18.2.0 완료 (StoryV 스토리보드 시퀀서 실결선 & 팩토리 연동)
- Build Status: Desktop · net8.0-ios · Tests 3 종 Release -warnaserror 0/0 · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 저메모리 단일 스레드(MaxCpuCount=1 · DisableParallelization) 533/533 1 회 완주 통과(실패 0 · 건너뜀 0 · 단독 재실행 0 · 9 분 19 초)
- Sidebar: 6 그룹 · 32 워크스페이스 유지(신규 모달 0)
- Updated At: 2026-10-25 (KST)

## Completed Deliverables
- STV-1: `Services/Storyboard/StoryboardShotBreakdownEngine.cs` — ReadOnlySpan 1 회 스캔 · 문장 부호 하드 컷 · 호흡(1.2 s) 이상 쉼표 소프트 컷 · 5 s 초과 공백 강제 분할 · 음절 × 180 ms + 쉼(400/200 ms) 타임코드 · 와이드/미디엄/클로즈업 앵글 · 호출자 Span 출력(0 B)
- STV-2: `Services/Storyboard/StoryboardKeyframeSequencer.cs` — 시작 시각 정렬 · 샷 번호 · 앵글별 9:16 크롭(1.0×/1.25×/1.6×) · 3 분할 교차 중심(X/Y ‰) · FNV 지문 · 스토리보드 페이지 🎞 샷 카드 인스펙터(번호 · 타임코드 · 앵글 · 대본)
- STV-3: `Services/Ui/StoryboardToShortsFactoryBridge.cs` — 키프레임 · 대본 ReadOnlyMemory 0-카피 인계 · 낡은 시퀀스 거부 · 🎬 숏폼 팩토리로 시퀀스 전송 버튼 → MainLayout 팩토리 ① 탭 오픈 → `ShortsFactoryPipelineContext.ApplyStoryboard`(① 절삭 0~총 길이 · 첫 샷 리프레임 · ② 대본) · DI 데스크톱/모바일 양쪽
- STV-4: `Storyboard_SequencerAndFactoryBridge_Check` — 분할 정확도 · 크롭 규격 · 무할당(분할/재로드/핸드오프 0 B) · 핸드오프 지문/파이프라인 정합

## Known Issues / 환경 노트
- 샷 템포는 고정 음절 길이 추정(실 TTS 음소 정렬 미결선) · 실 플랫폼 업로드 엔드포인트 미검증(Mock 경로만) · Android 빌드 미검증
