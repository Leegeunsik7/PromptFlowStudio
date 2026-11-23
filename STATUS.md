# Current Project Status
- Active Milestone: ✅ Step 40 (40단계 캡스톤) — 원클릭 전 언어(KO · EN · JA · ES) 일괄 렌더/패키지 · `{base}_{locale}.mp4` + `{base}_{locale}_publish_pack.txt` 짝 커밋
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⏸ Mobile net8.0-ios — `ios` 워크로드 미설치(미검증)
- Tests: ✅ 1410/1410 전체 단일 실행(종료 코드 0 · 11 m 31 s) · Pluto 루프 447/447 · 신규 `PlutoMultiLocaleBatchRenderTests` 9 건
- Updated: 2026-11-23 (KST) · 기준 `fe7fb65` · 불변식 `CorePlutoFileCount=9` · `MergeableHubs=6` · `CompanionSyncFiles=2` 유지

## Completed — Step 40
- 🎬 `IPlutoMultiLocaleBatchRenderService`(싱글톤 · Desktop+MAUI): Step 39 사전 분기/패키지 → 로캘 자막/TTS 투영(마스터 타임라인 재사용) → 주입 렌더러 → 검증 커밋 → 카탈로그 `{BaseClipId}::{Locale}` 출력 경로 결선(KO=마스터 경로 유지)
- 🔒 게시 로캘 건너뜀(파일 무변경) · 🎙️ TTS 1.30x 초과 → 기본 보류(`RenderFlagged` 정책 선택) · 형제 로캘 계속 · 취소 시 커밋된 로캘 보존
- 🖱️ Workspace `[🎬 전 언어 일괄 렌더/패키지]`(상단 헤더 무변경) · 로캘별 토스트 · 활동 피드 `LocaleRender`
- 🛡️ 자체 추가: ① 스테이징 렌더 + `ftyp` 검증 커밋 ② 영상/사이드카 짝 커밋 · 실패 시 이전 MP4 복원 ③ 낭독 오디오 > 타임라인+0.25 s → 커밋 거부(A/V 싱크) ④ 커밋 직전 게시 재확인(TOCTOU) ⑤ 디스크 여유 공간 사전 점검 · 마스터 경로 충돌/접미사 누적 가드 ⑥ 일괄 패키지와 마스터 단일 비행 공유
- 🧷 `TitanReleaseTests` 시간 일관성 무할당 단언 → `SteadyStateAllocation.MinBytes`(전체 실행 JIT 1회성 7,528 B)

## Fast Loops
- Pluto(~30 s): `dotnet test PromptFlowStudio.Tests/PromptFlowStudio.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~Pluto" -p:NuGetAudit=false`
- ⚠️ `.git` 은 `D:\Program` — `--git-dir=D:/Program/.git --work-tree=D:/Program/PromptFlowStudio` 필요
