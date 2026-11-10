# Current Project Status
- Active Milestone: 【Pluto Deep-Motion】 Ken Burns 모션 컴파일러(사진 1장 → 15초 60fps zoompan)
- Build: Release `-warnaserror` 0W/0E PromptFlowStudio · PromptFlowStudio.Tests (`-p:NuGetAudit=false` — NU1900 취약점 피드 조회 실패 우회 · Mobile 미빌드)
- Tests: 전체 1007/1007 PASS (Motion|Vision 필터 98)
- Updated: 2026-11-10 (KST)

## Completed
- 🎥 `Services/Pluto/PlutoMotionCompiler.cs`: `PlutoMotionSegment`/`PlutoMotionTrajectory`/`PlutoEasingFunction` · `SynthesizeFourAct` · `CompileFfmpegZoomPanFilter` · C# 미러 `EvaluateFrame`
  - z: `max(1,min(10,if(lte(on,240),…)))` 3t²−2t³ · x/y: `max(0,min(iw-iw/zoom,f·iw-iw/zoom/2))` 클램프 · 4배 Lanczos 슈퍼샘플(서브픽셀)
- 🎯 `PlutoVisionStoryPlan.DetectedFocalPoint`/`MotionPath` — Vision `focal_x/focal_y`([0,1] 스키마 검증 · 비전 출처만 채택) → 삼분할 폴백(아기 0.50,0.38 · 반려 0.50,0.55)
- 📦 `StockSuiteExporter.ExportMotionVideoFromPhotoAsync` + `BuildMotionVideoArgs` — 자막 번인(+srt) → BGM −14 LUFS + SFX 먹싱, [부분 구제] 자막/오디오 실패 격리
- 🧪 `PlutoMotionCompilerTests` 4건(컴파일 수식 문자열 직접 평가기로 0~901프레임 경계·연속성 검증)

## Open
- 실 FFmpeg 렌더 미검증(이 PC PATH 에 ffmpeg 없음) — zoompan `on` 0/1-기반 양쪽 안전하게 설계했으나 실기 1회 확인 필요
- UI 결선(Trend Lab 디렉터 HUD "모션 영상 내보내기" 버튼) 미구현 — 스펙 범위 외
- 초점이 가장자리일 때 줌 1.25 기준 뷰포트 중심은 [0.4,0.6]으로 클램프(레터박스 방지 우선)
