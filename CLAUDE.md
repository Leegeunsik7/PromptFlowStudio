# PromptFlow Studio (.NET 8 · WinForms+BlazorWebView · MAUI · Pure BCL)

## 절대 철칙
1. **Pure BCL** — 외부 NuGet 0. OS 종속 API는 `#if WINDOWS`+스텁.
2. **Zero-Alloc 60FPS** — 핫패스(추론 루프·HUD·워커 틱) LINQ/박싱/문자열 보간 금지.
3. **Release 0/0** — `-warnaserror` 경고·에러 0.
4. **xUnit 100% PASS** — 전량 통과 전 완료 보고 금지.

## 지연 로딩 (필요 시에만 Read)
- 핫패스/할당 → `docs/rules_zero_alloc.md`
- 테스트 작성/불안정 테스트 → `docs/rules_testing.md`
- 장애·함정 이력(NoGC·async void·UDP) → `docs/lessons_learned.md`
- 운영/스펙 → `docs/operations/`, `docs/specs/` · 이력 `milestone.xml`

## 명령어 (레포 루트)
- 빌드: `dotnet build PromptFlowStudio/PromptFlowStudio.csproj -c Release -warnaserror`
- 테스트: `dotnet test PromptFlowStudio.Tests`
- 실행: `dotnet run` (루트 `Launcher.csproj`가 위임 — 루트엔 이 파일 하나만 유지)
- 앱 실행 중이면 Debug 잠김. `.sln` 없음.

## 헌장 요약
- 외과적 수정: 검증된 로직(CTS 취소·Resume·폴백·페일오버·Webhook 큐·텔레메트리) 삭제 금지.
- Mobile은 데스크톱 소스를 `<Compile Include>` 와일드카드 링크 → 공유 파일 수정 시 모바일 영향 확인. 모바일/시뮬 서브컴포넌트 `@inject` 금지.
- 신규 모듈: DI는 `MainForm.cs`+`MauiProgram.cs` 양쪽, HUD는 `GlobalTelemetryHudView.razor`에 `gth-vXY` 카드(최신 위)+구독/해제 쌍(≤250ms는 `OnLiveChanged`).
- 백그라운드 콜백 UI 갱신 `InvokeAsync(StateHasChanged)`. 이벤트·Timer·CTS·Channel `Dispose()`. 콜백/타이머 예외 격리.
- Razor: `@(변수)한글`, `<style>` 내 `@@media`/`@@keyframes`, 수치 `InvariantCulture`, 패턴변수 `code` 금지, 루프 내 SVG `<text>`는 `<g>`로 감쌈.
- 실패 시 `[부분 구제]` 보존·`alreadyRegistered` 중복 차단, 외부 실패 1초 내 BCL 폴백, 직렬화 `System.Text.Json`.
- 외부 API: 타임아웃·지수백오프·시크릿 하드코딩 금지(환경변수/DPAPI)·스키마 즉시 검증.
- 서명/봉인 canonical 페이로드에 집계 필드 필수, 비교 `FixedTimeEquals`.
- 스텁 결선점: `WebGpuInferenceEngine.RunNativeTensorInferenceAsync` · `AudioWaveSynthesizer.TrySynthesizeOnnxAsync` · `Spatial3DVideoEngine.EstimateDepthLayers` · `QuantumIdentityFederation`.

## 응답 규칙
- 한국어·결과만. 인사/설명/요약 생략, 완료 보고는 `| 태스크 | 파일 | 빌드결과 |` 표 1개.
- 전체 파일 재출력 금지, grep으로 좁혀 읽기. `bin/obj/publish/release`·로그·zip 읽기 금지.
- 서브태스크 1개 완료 시 멈추고 보고, `/clear` 권장.
- Release 0/0 + 테스트 통과 직후 `STATUS.md` 덮어쓰기(20줄 이내).
