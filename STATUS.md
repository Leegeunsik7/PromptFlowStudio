# Current Project Status
- Active Milestone: 【15초 앰비언트 Cloud 결선】 Wan 2.1 5초 요청 + FFmpeg 3세그먼트 15초 루프 · Cloud 오류 명시 · 옵시디언 Mock
- Build: ✅ Release `-warnaserror -p:NuGetAudit=false` 0/0 (PromptFlowStudio · Tests)
- Tests: ✅ 1108/1108 PASS (AmbientCloudLoopTests 신규)
- Updated: 2026-11-13 (KST)

## Completed
- ☁️ `WanVideoRequest.Duration` → DashScope `parameters.duration`(null 생략). 앰비언트 작업은 항상 5초 요청(15 직접 요청 금지)
- 🔁 Cloud 앰비언트: 핑퐁 ☑ → 정·역·정 `split=3 · reverse · concat=n=3`(원속도 15.00s) / ☐ → `xfade fade 0.75` ×2 + `tpad` + `trim=15`
- 🚫 Cloud 모드 명시 + 엔진 미구성 → `IsAvailable=true`로 큐 Mock 폴백 차단, `☁️ DashScope 오류:` 배너로 Failed
- 🧾 작업실 네거티브 프롬프트 → 작업 메타 `prompt.negative` → Cloud 요청 전달
- 🌑 Mock 앰비언트: `testsrc` → `color=#07090E` 320x180 geq 앰버/시안 동심원 펄스 + bicubic 업스케일 + vignette(720p 5s ≈ 4.6s)

## Notes
- 정·역·정(F→R→F)은 마지막 프레임=원본 끝 프레임 → 무한 반복 시 시작 프레임과 불일치(이음새 1회). 완전 무이음은 기존 2세그먼트 `LoopMode.PingPong`
