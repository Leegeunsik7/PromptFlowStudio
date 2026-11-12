# Current Project Status
- Active Milestone: 【Pluto 실시간 웹 트렌드 + SNS 레이더 Drive 통합】 Google Trends RSS(geo=KR) → 자율 수확 · 4개 JSON 전량 G:\내 드라이브\DATA\Pluto
- Build: ⚠️ **No-Build Mode** 커밋(2026-11-12) — 로컬 빌드/테스트 미실행 · 신규 PC에서 Release `-warnaserror` + 전체 테스트 검증 필요
- Tests: 신규 `PlutoWebTrendFetcherTests` 11건 작성(미실행) · 직전 기준 1061/1061 PASS
- Updated: 2026-11-12 (KST)

## Completed (미검증)
- 🌐 `PlutoWebTrendFetcher`(`IPlutoWebTrendFetcher`): `trends.google.co.kr/trending/rss?geo=KR` · 4초 타임아웃 · 2MB 상한 · DTD 금지 XmlReader→XDocument · `title`/`approx_traffic`(LocalName 매칭) · 정제·중복 제거·트래픽 내림차순 상위 8 · 실패 → 빈 목록
- 🔥 `PlutoTrendAnalyzer.ClassifyLiveKeyword`/`BuildLiveTrendSamples`: 날씨→골든아워 · 간식→매크로 줌/ASMR · 밈→POV · 일상→16mm 필름 표본 카드
- 🔄 `PlutoAutonomousTrendHarvester`: `webFetcher` 선택 인자(끝 파라미터 · 기존 호출 호환) · 수확 시 실시간 키워드 선수집 → 니치 표본 병합 → 토큰 적재 · `LastLiveKeywords` · 스냅샷 `LiveKeywords`
- ☁️ `PlutoSocialRadarEngine(IPlutoStorageManager, …)` 오버로드 · `pluto_social_radar.json` Drive 볼트 이관(레거시 `%LOCALAPPDATA%\PromptFlowStudio` 1회 시드 · 원본 유지)
- 🪐 DI 양쪽(MainForm/MauiProgram): `IPlutoWebTrendFetcher` 싱글턴 · 레이더 저장소 주입 · 수확기 `webFetcher:` 결선

## Notes
- 신규 PC 첫 작업: `dotnet build PromptFlowStudio/PromptFlowStudio.csproj -c Release -warnaserror` → `dotnet test PromptFlowStudio.Tests`
- RSS 스키마 변경/차단 시 빈 목록 → LLM/카탈로그 폴백(무중단) · Mobile(MAUI) 빌드 미검증
