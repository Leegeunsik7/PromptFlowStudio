# Current Project Status
- Active Milestone: 【Pluto 배치 사진 큐레이션 · AI 베스트 컷 자동 선별】 라플라시안 선명도 + 64bit dHash + 3막 배열 → 몽타주 프리뷰/익스포트
- Build: ✅ Release `-warnaserror -p:NuGetAudit=false` 0/0 (PromptFlowStudio · Tests)
- Tests: ✅ 1096/1096 PASS (Curation|Montage|Focal 필터 15/15)
- Updated: 2026-11-13 (KST)

## Completed
- ✨ `PlutoPhotoCurationEngine`: ≤128² 썸네일 1회 디코드 · 초점 가중(r 0.35 · 2×) ∇²Y 분산/0.045 · 흐림 <0.18 · 노출 Y∉[0.14, 0.92] · Composite 0.45/0.30/0.25 · dHash 9×8 탐욕 NMS(Hamming ≤8) · 게이트별 최소 컷 구제 · Vision 표정 보정(동시 3 · 4초)
- 🎬 3막 배열: Act 1 = max(0.6·Sal+0.4·Sharp) · Act 3 = 잔여 max Composite · Act 2 = Σ|ΔX| 최대 순열
- 🖥️ Workspace: 최대 30장 · 장당 15MB · 5장 초과 자동 선별 · 4장↑ `[✨ AI 베스트 컷 자동 선별]` · 큐레이션 필 + `[선별 상세]`(Act 배지 · 제외 사유) · 선별 초점 캐시 직결
- 🔌 DI: `MainForm.cs` · `MauiProgram.cs` Singleton
- 🧪 `PlutoPhotoCurationTests` 4종

## Notes
- GDI+ 디코드 불가 파일은 중립값(선명 0.65 · 경로 해시)으로 후보 유지 · 누락 파일은 `UnreadableFile`
- Mobile(MAUI) 빌드 · 실제 연사 사진 품질 · Vision 보정 실호출 미검증
