# Current Project Status
- Active Milestone: 【Pluto DSP】 FFT 비트 검출 · 비트 동기 양자화 · BS.1770-4 트루피크 마스터링
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio (`-p:NuGetAudit=false` · Mobile 미빌드)
- Tests: 전체 1037/1037 PASS (`~Dsp|~BeatSync` 28/28 · 신규 16)
- Updated: 2026-11-11 (KST) · 프리뷰어 커밋 d86767a 푸시 완료(origin/master·main)

## Completed
- 🥁 `Services/Pluto/Dsp/PlutoAudioBeatDetector.cs`: N=2048/H=512 Hann · Radix-2 FFT · 스펙트럴 플럭스 · 15프레임 중앙값×1.35 임계 · Dixon IOI 군집 + 위상 탐색/최소제곱 정밀화 · 무음/파싱 실패 → 0..15s 1초 격자
- 🧲 `Dsp/PlutoBeatSyncQuantizer.cs`: 막 경계 ±0.25s · SFX ±0.35s 최근접 비트 스냅(15.0s 불변) · `PlutoMotionCompiler.RetimeBoundaries`(C¹ 보존 · 붕괴 레이아웃 거부)
- 🎛️ `Dsp/PlutoTruePeakMasteringEngine.cs`: K-가중 2단 바이쿼드(48k 규격 계수 · 타 레이트 쌍선형) · −70/−10 게이팅 · 48탭 Kaiser-sinc 4×폴리페이즈 `Vector<float>` · 5ms 링버퍼 룩어헤드 + 박스카 램프 · 40/250ms 이중 릴리즈 · −14 LUFS 할선 수렴 · PCM16 양자화 후 TP 재검증 트림
- 🎚️ `Dsp/PlutoPcmWav.cs`: 풀 버퍼용 WAV 헤더/디코드/PCM16 인코드
- 🔌 `StockSuiteExporter.BabyStudio.cs`: FFmpeg 투입 전 BGM 마스터링 사본 + SFX 큐 비트 스냅 · 모션 내보내기 막 경계 스냅(`BeatSyncEnabled`·`TruePeakMasteringEnabled` · 실패 시 원본 경로)
- 🧩 DI: `MainForm.cs` + `MauiProgram.cs` (`IPlutoAudioBeatDetector`·`IPlutoBeatSyncQuantizer`)

## Notes
- BGM 이 이미 C# 마스터링(−14 LUFS)된 뒤에도 FFmpeg `loudnorm`+`volume=0.25` 체인은 그대로 유지(기존 믹스 레벨 계약 보존)
- 비트 그리드는 합성 펄스로만 검증 — 실제 `AiGenerativeMusicEngine` BGM 에서 스냅 품질은 수동 확인 필요 · Workspace 프리뷰어는 아직 비트 스냅 궤적 미반영
- Mobile(MAUI) 빌드 미검증 · 전체 테스트 13분대(직전 실행 잔류 testhost 잠금 1회 → 종료 후 재실행)
