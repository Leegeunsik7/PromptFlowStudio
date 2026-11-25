# Current Project Status
- Active Milestone: ✅ 아기 테마 시나리오 확장 + 짧은 프롬프트 자동 확장
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⏸ Mobile net8.0-ios — `ios` 워크로드 미설치(미검증)
- Tests: ✅ 전체 1477/1477 (exit 0) · 집중(BabyPromptAutoExpander·PromptSynthesizer·VisionIntelligence·BabyStudio·PromptInspiration) 70/70
- Updated: 2026-11-25 (KST) · 불변식 `CorePlutoFileCount=9` · `MergeableHubs=6` · `MergeableCompanions=2` 유지(테스트 잠금)

## Completed
- 👶 `Services/Prompt/BabyPromptAutoExpander.cs`: 시나리오 5종(첫걸음·낮잠·목욕/비눗방울·햇살 장난감·부모 교감) · KO/EN/JA/ES 라벨/키워드
- ✨ ≤80자 아기 프롬프트(또는 아기 모드) → 조명·자연 카메라 모션·따뜻한 분위기·안전 가족 문구 자동 결합(멱등 · 중복 조각 생략 · 일반 프롬프트 무변경)
- 🔌 `PromptSynthesizerService`: `PromptContext.AutoBabyExpansion`(기본 true, opt-out 가능) 결선
- 🎛️ `PromptPresets.BabyScenarios` 원클릭 완성형 칩 5종(기존 `Baby` 5종 무변경)
- 🧪 `BabyPromptAutoExpanderTests` (시나리오·다국어·멱등·합성기·프리셋·Pluto 9/6/2)

## Known (latent, 미수정)
- `PlutoMultiLocaleBatchRenderService.BlockReason`: `ex.Message` 미리댁션 — 현재 UI 미노출(피드 Record 단계 리댁션)
- `BabyScenarios` 칩 UI 미결선(Workspace 칩 바 연결은 후속)

## Fast Loops
- Pluto(~30 s): `dotnet test PromptFlowStudio.Tests/PromptFlowStudio.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~Pluto" -p:NuGetAudit=false`
- ⚠️ `.git` 은 `D:\Program` — `--git-dir=D:/Program/.git --work-tree=D:/Program/PromptFlowStudio` 필요
