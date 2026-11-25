# Current Project Status
- Active Milestone: ✅ 고위험 경로 심층 감사 — 오토파일럿 Disable→Enable 잔류 릴 재게시 결함 수정
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⏸ Mobile net8.0-ios — `ios` 워크로드 미설치(미검증)
- Tests: ✅ 집중 스위트(Autopilot·Quota·Standby·LiveToReels·Publisher·Locale·SelfTest·Redact·Pluto) 605/605 · 전체 1463/1465(2회 실행, 매회 상이한 부하 민감 성능/할당 단언 2건 — 단독 재실행 전량 통과)
- Updated: 2026-11-25 (KST) · 불변식 `CorePlutoFileCount=9` · `MergeableHubs=6` · `MergeableCompanions=2` 유지

## Completed
- 🐞 `ZeroTouchAutopilotMasterEngine.Enable()`: `_reelId`·`_task` 리셋 — Disable(방송·반응·쿨다운 중) 후 재가동 시 옛 릴 재번-인·중복 게시 차단
- 🧪 `AutopilotCycleCancellationTests.ReEnable_AfterDisable_DiscardsStaleReel_NoReclip` (수정 전 실패 확인)
- 🔍 감사 무결함: 사이클/재시도 취소·핀/임대·AutoPublish 복원·스탠바이 보류·합성 폴백 차단·쿼터 큐·Workspace 리댁션·셀프테스트 10단계·다국어 배치 렌더

## Known (latent, 미수정)
- `PlutoMultiLocaleBatchRenderService.BlockReason`: `ex.Message` 미리댁션 — 현재 UI 미노출(피드 Record 단계 리댁션)

## Fast Loops
- Pluto(~30 s): `dotnet test PromptFlowStudio.Tests/PromptFlowStudio.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~Pluto" -p:NuGetAudit=false`
- ⚠️ `.git` 은 `D:\Program` — `--git-dir=D:/Program/.git --work-tree=D:/Program/PromptFlowStudio` 필요
