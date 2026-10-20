# PromptFlow Studio v8.0.0 — Release & B2B 배포 체크리스트

마감일 2026-09-22 · Release `-warnaserror` 0/0 · .NET 8 Blazor Hybrid(WinForms) · 외부 NuGet 0.

## 1. 빌드·검증
- [ ] `dotnet build -c Release -warnaserror` → 0/0 (앱 종료 후 실행: Debug 산출물 잠금 회피)
- [ ] `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true`
- [ ] `ffmpeg.exe` 루트 동봉(미동봉 시 병합/썸네일 런타임 Graceful 비활성)
- [ ] `Tools\{engine}\` 오픈소스 CLI 동봉(선택 · Real-ESRGAN 등) · ⚠ `Tools\PromptFlowKeyGen`(개인키) 배포 제외 확인

## 2. B2B 보안·멀티테넌시
- [ ] `ApiGatewayEngine.ConfigureSecret` / `C2PaWatermarkEngine.ConfigureSecret` 프로덕션 시크릿 주입(기본값 미사용)
- [ ] 테넌트 등록: `ApiGatewayEngine.RegisterTenant(id, apiKey, rps, burst)` — API Key는 HMAC 해시 저장(원문 미보관)
- [ ] REST Edge 포트/URL ACL: `EmbeddedHttpServer.Start(port)` localhost 기본, 외부 노출 시 방화벽·리버스프록시(TLS) 앞단 배치
- [ ] C2PA 서명 검증 경로: 배포 자산 `VerifyManifest` 통과 확인, 변조 경고→관제 HUD 발화 확인

## 3. 런타임 의존
- [ ] WebView2 Evergreen 상주(Win10/11 기본)
- [ ] DirectML 가속: `d3d12.dll` 프로브(자동) · 미가속 시 BCL 폴백(정상)
- [ ] `%LocalAppData%/PromptFlowStudio` 쓰기 권한(설정·라이선스·테마·미디어)

## 4. 배포 산출물
- [ ] Inno Setup: `installer/PromptFlowStudio.iss` + `installer/build.ps1`(publish+ISCC)
- [ ] CI: 태그 `v8.0.0` 푸시 → `.github/workflows/release.yml`(Setup.exe 업로드)
- [ ] `RELEASE_NOTES.md` v8.0.0 항목 갱신(릴리스 모달 body 소스)

## 5. Git 태깅 (⚠ 현재 저장소 미초기화 — 승인 후 실행)
```bash
git init && git add -A && git commit -m "chore: v8.0.0 finalize (Agent Swarm · Spatial 3D · C2PA)"
git tag -a v8.0.0 -m "PromptFlow Studio v8.0.0"
git push origin main --tags   # 원격 설정 후
```

## 6. 스모크(수동)
- [ ] [🎬 AI 타임라인]: 오토디렉팅→자율교정(🤖)→공간3D(🥽)→C2PA(🔏)→FCPXML/OTIO 내보내기
- [ ] [📊 글로벌 관제]: 스웜/공간3D·C2PA·하이브리드·Edge·RPS·VRAM 카드 표출
- [ ] 모바일 시뮬: 세로 빠른메뉴 8타일 + 테마 7종 칩 · 가로 닥 겹침 없음
