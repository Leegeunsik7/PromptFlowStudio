# Current Project Status
- Active Milestone: 【Pluto 사진별 초점 · 멀티컷 60fps 캔버스 프리뷰】 2~5장 몽타주 엔드투엔드
- Build: ✅ Release `-warnaserror -p:NuGetAudit=false` 0/0 (PromptFlowStudio · Tests)
- Tests: ✅ 1086/1086 PASS (Focal|Montage|Previewer 필터 16/16)
- Updated: 2026-11-13 (KST)

## Completed
- 🎯 `PlutoImageFocalDetector`: ≤96px 그리드 · 피부톤 +2.5 · 1.5·|ΔY| · 중앙 가우시안(0.5, 0.42, σ 0.28) 가중 중심 → [0.2, 0.8]² · 비Windows/판독 불가 → (0.50, 0.42)
- 🎯 배치: 로컬 전량 → Vision 설정 시 병렬 보정(Semaphore 3 · 4초) · 실패/시간 초과 → 로컬 유지 · DI 양쪽 등록
- 🎞 `PlutoMontageCompiler.Preview`: `PlutoMontagePreviewPlan`/`Clip`/`PlutoPreviewColorGrade` · 클립별 궤적 계약 · Utf8JsonWriter JSON
- 🖼 JS `setMontagePlan`/`clearMontagePlan`: 클립 이미지 선로드(URL 재사용) · 클립 로컬 시간 궤적 · xfade α 블렌드 · `ctx.filter` 색보정 + 웜 오버레이 · 자막 전 `filter=none`
- 🧩 `PlutoMotionPreviewer.MontagePlan` 파라미터(참조 변경 시만 재직렬화 · null → 단일 모드)
- 🖥️ Workspace: 2~5장 → 초점·색·비트 → 프리뷰 플랜 바인딩(세대 가드) · 렌더 시 동일 사진별 초점 전달
- 🧪 `PlutoPerPhotoFocalAndPreviewTests` 4종

## Notes
- 스펙 편차: 클립 `Trajectory` 타입은 기존 `PlutoMotionPreviewContract` 재사용(JS 파서 공유)
- `PlutoVisionStorySynthesizer.IsVisionConfigured` 추가(비전 경로 사전 점검)
- Mobile(MAUI) 빌드 · 실제 브라우저 캔버스 렌더 미검증
