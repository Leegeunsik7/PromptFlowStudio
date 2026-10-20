using Microsoft.Extensions.DependencyInjection;
using PromptFlowStudio.Shared.Cross;
using PromptFlowStudio.Shared.Deploy;
using PromptFlowStudio.Shared.Haptics;
using PromptFlowStudio.Shared.Store;

namespace PromptFlowStudio.Shared;

/// <summary>
/// [PromptFlow Studio v4.1] 공용 서비스 DI 통합 등록 — 데스크톱/모바일 호스트가 단 1줄로 공용 시임을 등록한다.
///   ▸ 기본은 <b>데스크톱 안전 폴백</b> 구현을 등록(미지원 하드웨어는 Graceful).
///   ▸ 모바일(MAUI) 호스트는 이 호출 <b>이후</b> 안드로이드 네이티브 구현으로 override 한다(TryAdd 아님 → 마지막 등록 우선).
/// </summary>
public static class SharedServiceRegistry
{
    /// <summary>공용 서비스/시임을 등록한다(데스크톱 폴백). 반환값으로 체이닝.</summary>
    public static IServiceCollection RegisterSharedServices(this IServiceCollection services)
    {
        // ── 공용 햅틱(데스크톱 폴백) ──
        services.AddSingleton<IHapticEngine, DesktopHapticEngine>();

        // ── v4.2 크로스플랫폼 시임 ──
        services.AddSingleton<IParityProbe, DefaultParityProbe>();       // 실 호스트(데스크톱/모바일)가 override
        services.AddSingleton<GlobalMultiPlatformAdapter>();             // OS 언어/i18n/PWA (관찰 가능)

        // ── v4.3 스토어 배포 시임 ──
        services.AddSingleton<ISchemaCompatProbe, DefaultSchemaCompatProbe>();   // 데스크톱이 StoryboardProject 왕복으로 override

        // ── v4.4 CI/CD 관제탑 시임 ──
        services.AddSingleton<IBuildLauncher, DefaultBuildLauncher>();           // 데스크톱이 ps1/sh 실행 런처로 override
        services.AddSingleton<StoreUploadRestGateway>();                         // 순수 BCL 스토어 업로드 게이트웨이
        return services;
    }
}
