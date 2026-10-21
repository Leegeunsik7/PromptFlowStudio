# Current Project Status
- Active Milestone: v17.74.0 완료 (Complete) — AI 동적 모션 비디오 번-인 & 3DGS 썸네일 스튜디오
- Build Status: Desktop Release -warnaserror 0/0 · Mobile net8.0-ios Release -warnaserror 0/0 · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 482/482 PASS × 1회(Release · MaxCpuCount=2 · 1회 기준 정책 · 신규 1)
- Sidebar: 7대 카테고리 · 150 엔트리
- Updated At: 2026-10-21 (KST)

## Completed Deliverables
- THUMB-1: `VisualPolish/HardwareVideoDemuxFrameDecoder` — `Mp4H264Demuxer`(fMP4 moof/trun · 진행형 stbl · avcC · AVCC → Annex-B · 무할당 판독/재개방) · `MediaFoundationVideoDecoder`(MF H.264 MFT · 저지연 · 가변 링) · `ManagedBaselineH264Decoder`(v17.49 인코더 부분집합 · 비트 동일 · 무할당) · NV12 → RGBA32
- THUMB-2: `DynamicMotionSubtitleBurnInSynthesizer` + `DecodedClipFrameSource` — 실제 동영상 프레임 위 v17.73 SDF 하드서브 · 동일 크기 제로카피 직접 복원 · 리샘플 · 프레임당 예산 계측 · v17.73 오토파일럿 번-인 결선(디코드 불가 = PGM 폴백)
- THUMB-3: `PeakEmotionFrameTracker`(MCR 레이어 · 미소/입벌림/눈 + 피격/열광 부스트 · 1 s 버킷 골든 스냅숏 · 무할당) + `PeakEmotion3dgsThumbnailSynthesizer`(전용 컴포지터 · 오프축 320×192 타일 4×10 = 1080×1920 · 네온 엣지 글로우 · SDF 외곽선 헤드라인 · PNG)
- THUMB-4: `AbTestingMetadataVariantGenerator` — 호기심형 A / 정보형 B · 썸네일 분위기 · 해시태그 정규화 · 대기열 · CTR 베타 P(B>A) 승자 판정
- THUMB-5: `ReelsVisualPolishStudioRunner` + `ReelsVisualPolishStudioModal`(`rvp-`) · 카탈로그 `_visualPolish`(카테고리 4) · HUD `gth-v1774` · DI
- 공용 수정: `SplatCamera.ShiftX/ShiftY`(오프축 · 0 = 기존 비트 동일) · `MfTransform.Feed` 수락 판정 버그 수정(Drain 의 NEED_MORE_INPUT 이 실패로 오보) · MF 디코더 저지연 · MCR `BodySplats/FaceSplats`
- THUMB-6: `VideoDemux_And_ThumbnailSynthesis_Check`(TimingSensitive)

## Pending
- 관리 디코더는 v17.49 관리 인코더 부분집합 전용(MF 인코더 출력 등 일반 스트림 = MF 하드웨어 경로 필요 · 비 Windows 미지원) · 썸네일 JPEG 미지원(PNG)
- 외부 CPU 경합 시 기존 타이밍 테스트 간헐 실패 · Android 빌드 · iOS 실기기 · GUI 육안
