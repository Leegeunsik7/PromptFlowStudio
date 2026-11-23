# Current Project Status
- Active Milestone: ✅ Step 34 — 다국어 자막 자동 번역(`PlutoSubtitleLocalizationService` · 시리즈 용어집 · 고유명사 자리표시자 보존 · 비파괴 로캘 트랙)
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⏸ Mobile net8.0-ios — `ios` 워크로드 미설치(미검증)
- Tests: ✅ 1370/1370 — 전체 단일 실행 통과(종료 코드 0 · `build-server shutdown` 후) · Step 34 신규 `PlutoSubtitleTranslationAndGlossaryTests.cs` 8건
- Updated: 2026-11-23 (KST) · 기준 `dea1805` · 불변식 `CorePlutoFileCount=9` · `MergeableHubs=6` · `CompanionSyncFiles=2` 유지

## Completed — Step 34
- 📖 용어집: `pluto_series_arc.json` 선택 필드 `Glossary`(테마 범위 → 원어 → EN/JA/ES · LWW · 삭제 표식) · 레거시 파일 양방향 호환 · `PlutoConflictMergeService` 결정론 병합
- 🛡️ 방패: 위치별 가장 긴 용어 우선 → `[[Gn]]` · `@핸들`/`#태그`/대문자 라틴 단어/원문 `[[…]]` 보호 · 공백·대소문자·전각 괄호 수리 복원
- 🎙️ 큐별 낭독 글자 예산(`cps × 1.30 × 가용 초`) + 로캘 문화 톤 전달 · 예산 초과 보고
- 🔒 오프라인: 미주입·미구성·`PROMPTFLOW_PLUTO_DRIVE=off`·`PROMPTFLOW_DISABLE_SUBTITLE_TRANSLATION=1`·회로 차단 → 호출 0 · 용어집 폴백
- 🌐 비파괴 트랙: (맥락, 로캘) 보관 · `PlutoLocaleSubtitleFitter.Project(..., track)` 본문 대체 → 한글 잔존 경고 해제 · TTS 재적합 · 원문 불변
- 🖥️ `Workspace.razor` `[🌐 선택 언어 자동 번역]` + 📖 용어집 미니 편집기 · DI `MainForm.cs`+`MauiProgram.cs`(LLM 페일오버 → JSON 스키마 검증)
- 🛡️ 자체 추가 4: ① 자리표시자 무결성(누락/중복/미지 → 큐 단위 폴백) + 되돌려주기(한글 잔존) 가드 ② 낡은 트랙 펜스(원문 지문·구간 변경 → 미사용·"다시 번역") ③ 삭제 표식 LWW + 시즌 무관 범위(전역 `*` 상속) ④ 예산·톤 요청 계약 + 초과 집계

## Notes
- ⚠️ `.git` 은 `D:\Program` — `--git-dir=D:\Program\.git --work-tree=D:\Program\PromptFlowStudio` 필요
- ⚠️ 부하 민감 테스트(`OmniLiveRestream` 50ms · `SemanticChatWeather` 자동 날씨) — 전체 실행 전 `dotnet build-server shutdown`
