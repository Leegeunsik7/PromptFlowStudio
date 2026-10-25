using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Forms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace PromptFlowStudio;

/// <summary>
/// v18.3.0 릴리스 락다운: 헤드리스 부트스트랩 스모크(<c>PromptFlowStudio.exe --smoke-test [--deep]</c>).
///   ▸ 실 엔트리포인트와 동일한 <see cref="MainForm.ConfigureServices"/> 로 DI 컨테이너를 <c>ValidateOnBuild</c> 빌드 —
///     타입 등록 전수의 생성자 의존성 그래프를 인스턴스화 없이 검증(누락 등록 즉시 실패).
///   ▸ 정적 검증이 불가능한 팩토리/인스턴스 등록 + 필수 싱글톤은 실제 해석. <c>--deep</c> 은 등록 전수 인스턴스화
///     (지연 생성 스튜디오 러너의 생성자 시뮬 워밍업 포함 · 수 초 소요).
///   ▸ 32 슈퍼-워크스페이스 카탈로그 + 설치 폴더 무결성(변조만 실패) 확인 — UI 윈도우는 띄우지 않는다.
///   ▸ 결과는 종료 코드(0 = 정상 · 1 = 누락 등록/런타임 예외)로 보고. 데스크톱 전용(모바일 링크 대상 아님).
/// </summary>
internal static class SmokeTestBootstrap
{
    public const string Command = "--smoke-test";
    public const string DeepOption = "--deep";
    public const int ExpectedWorkspaces = 32;

    /// <summary>모드와 무관하게 반드시 실제 해석하는 부트스트랩 필수 싱글톤.</summary>
    private static readonly Type[] Essentials =
    {
        typeof(IConfiguration),
        typeof(Services.Account.SessionState),
        typeof(Services.Ui.ThemeService),
        typeof(Services.Ui.StudioNavigationService),
        typeof(Services.Wan.VideoEngineSelector),
    };

    /// <summary>스모크 실행. 예외 없음 — 모든 실패는 종료 코드 1 + <paramref name="message"/> 로 귀결.</summary>
    internal static int Run(bool deep, out string message)
    {
        var t0 = Stopwatch.GetTimestamp();
        try
        {
            var config = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .Build();

            var services = new ServiceCollection();
            using var owner = new Control();
            MainForm.ConfigureServices(services, config, owner);

            var defects = new List<string>();
            ServiceProvider root;
            try { root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true }); }
            catch (AggregateException ex)
            {
                foreach (var inner in ex.InnerExceptions) defects.Add("[GRAPH] " + inner.Message);
                message = Report(false, 0, services.Count, defects, t0);
                return 1;
            }
            // 프로세스는 직후 Environment.Exit 로 종료 — 비동기 전용 Dispose 싱글톤 대응을 위해 컨테이너는 명시 폐기하지 않는다.
            var sp = root.CreateScope().ServiceProvider;

            var resolved = 0;
            foreach (var sd in services)
            {
                if (sd.ServiceType.ContainsGenericParameters) continue;
                if (!deep && IsDeferred(sd)) continue;
                if (TryResolve(sp, sd.ServiceType, defects)) resolved++;
            }
            foreach (var t in Essentials)
                if (TryResolve(sp, t, defects)) resolved++;

            var ws = Services.Ui.StudioWorkspaceCatalog.Workspaces;
            if (ws.Length != ExpectedWorkspaces) defects.Add("[CATALOG] 워크스페이스 " + ws.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var w in ws)
                if (w.Tabs.Length == 0) defects.Add("[CATALOG] 빈 워크스페이스 " + w.Id);

            var integrity = Services.Security.AppIntegrityGuard.VerifyInstalled();
            if (integrity.ShouldShutdown) defects.Add("[INTEGRITY] " + integrity.FirstFailure);

            message = Report(deep, resolved, services.Count, defects, t0)
                + " workspaces=" + ws.Length.ToString(CultureInfo.InvariantCulture)
                + " integrity=" + integrity.Verdict.ToString();
            return defects.Count == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            message = "SMOKE FAIL " + ex.GetType().Name + ": " + ex.Message;
            return 1;
        }
    }

    /// <summary>
    /// 그래프 모드에서 인스턴스화를 미루는 등록: ① 타입 등록(ValidateOnBuild 로 의존성 검증 완료) ② 워크스페이스 진입 시 지연 생성되는
    /// <c>*StudioRunner</c> 팩토리(생성자 시뮬 워밍업 · 부팅 경로 밖). 필수 싱글톤은 항상 해석.
    /// </summary>
    private static bool IsDeferred(ServiceDescriptor sd)
    {
        if (Array.IndexOf(Essentials, sd.ServiceType) >= 0) return false;
        if (sd.ImplementationType is not null) return true;
        return sd.ImplementationFactory is not null && sd.ServiceType.Name.EndsWith("StudioRunner", StringComparison.Ordinal);
    }

    private static bool TryResolve(IServiceProvider sp, Type type, List<string> defects)
    {
        try
        {
            if (sp.GetService(type) is not null) return true;
            defects.Add("[NULL] " + type.FullName);
        }
        catch (Exception ex) { defects.Add("[RESOLVE] " + type.FullName + ": " + ex.GetType().Name + " " + ex.Message); }
        return false;
    }

    private static string Report(bool deep, int resolved, int registered, List<string> defects, long t0)
    {
        var s = "SMOKE " + (defects.Count == 0 ? "PASS" : "FAIL")
            + " mode=" + (deep ? "deep" : "graph")
            + " registered=" + registered.ToString(CultureInfo.InvariantCulture)
            + " resolved=" + resolved.ToString(CultureInfo.InvariantCulture)
            + " defects=" + defects.Count.ToString(CultureInfo.InvariantCulture)
            + " ms=" + Stopwatch.GetElapsedTime(t0).TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture);
        if (defects.Count > 0) s += Environment.NewLine + string.Join(Environment.NewLine, defects);
        return s;
    }
}
