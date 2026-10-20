using Microsoft.Extensions.DependencyInjection;
using PromptFlowStudio.Services.Storyboard;   // VideoFormatFactory / BabyThemeDatabase 등
using PromptFlowStudio.Services.Media;         // 엔진들
using PromptFlowStudio.Services.Wan;
using PromptFlowStudio.Services.Voice;
using PromptFlowStudio.Services.Ui;

namespace PromptFlowStudio.Mobile.Services;

/// <summary>
/// [PromptFlow Studio v4.0.0] 공용 서비스 DI 재등록 — 데스크톱 <c>MainForm</c> 의 등록 목록을 그대로 미러링(로직 무변경).
///   ⚠ 프로덕션 권장: 데스크톱/모바일이 공유하는 단일 등록 확장 메서드로 추출해 이중 관리를 없앤다.
///   ⚠ 플랫폼 종속 구현(ILocalMediaService=BlazorWebView 가상호스트, StudioFolderLinkService=탐색기 열기)은
///     모바일 전용 구현으로 치환해야 한다(여기서는 요지만 표기).
/// </summary>
public static class MobileServiceRegistry
{
    public static void RegisterSharedServices(IServiceCollection services)
    {
        // ── 스토리보드 코어(순수 BCL · 데스크톱과 동일) ──
        services.AddSingleton<BabyThemeDatabase>();
        services.AddSingleton<CustomThemeRegistryService>();   // v16.8.0 커스텀 테마(.pftheme) — 앱 로컬 데이터 폴더
        services.AddSingleton<SmartThemeMatcherService>();   // v16.7.0 (모바일은 디코드 스텁 → 빈 추천)
        services.AddSingleton<CorporateThemeDatabase>();
        services.AddSingleton<AutomotiveEquipmentDatabase>();
        services.AddSingleton<VideoFormatFactory>();

        // ── v3.x 엔진(순수 BCL/FFmpeg · 모바일에서 FFmpegKit 로 구동) ──
        services.AddSingleton<AlibabaWanHybridEngine>();
        services.AddSingleton<EmotionalVoiceSynthesizer>();
        services.AddSingleton<SpatialAudioEngine>();
        services.AddSingleton<BabyKeepsakeOverlayEngine>();
        services.AddSingleton<DigitalKeepsakeCardService>();

        // ── UI 지원 ──
        services.AddSingleton<StudioMemoryAudit>();

        // ── 플랫폼 종속(모바일 전용 구현 치환 지점) ──
        //   services.AddSingleton<ILocalMediaService, MauiLocalMediaService>();
        //   services.AddSingleton<StudioFolderLinkService, MauiShareService>();
        //   … 데스크톱 MainForm 의 나머지 등록을 동일 순서로 추가.
    }
}
