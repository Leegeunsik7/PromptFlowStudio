# STATUS (2026-11-26)

## 최근 작업
- 첫 화면 미리보기 무실패화(`GeminiPromptService.GenerateFirstFrameImageAsync`)
  - 1차 `gemini-2.5-flash-image`(17s) → 2차 `imagen-3.0-generate-002` `:predict`(스키마 검증) → 전체 28s 예산
  - 키 미설정/원격 전멸 → Pure BCL 결정론적 로컬 구도 PNG(16:9 1280×720 · 9:16 720×1280 · 1:1 1024×1024, 테마 팔레트) + Redact 사유 안내
  - 호출자 취소만 OCE 전파 · 로컬 폴백은 I2V 시작 프레임 바인딩 버튼 숨김(`_ffpLocal`)
- Workspace 컴팩트 레이아웃: 유휴 캔버스 `flex:0 0 auto`·16:9 `min(26vh,216px)` · 튜너 `margin-top:0` · 카드/프롬프트/드롭존/칩/미리보기 간격 축소
- 테스트: `FirstFramePreviewFallbackTests`(폴백 체인 · PNG 유효/결정론/CRC · 레이아웃 마커 · Pluto 9/6/2) · 무키 테스트 갱신

## 검증
- Release `-warnaserror` 경고 0 / 오류 0 (Desktop · Tests — 실행 중 앱 PID 잠금으로 OutDir 우회 빌드)
- xUnit 1520/1520 통과 · Pluto 9/6/2 유지 · DI 변경 없음
