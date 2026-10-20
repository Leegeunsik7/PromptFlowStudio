using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;

namespace PromptFlowStudio.Services.Hardening;

public enum AuditCategory : byte { DependencyInjection, UiInjection, Telemetry, Fallback, ZeroAlloc, SelfHealing, Parity }

public enum AuditOutcome : byte { Pass, Warn, Fail, Fixed, Skipped }

/// <summary>진단 프로브(분류 · 대상 · 검사 · 선택적 복구 픽스).</summary>
public sealed record AuditProbe(AuditCategory Category, string Target, Func<(AuditOutcome Outcome, string Detail)> Check, Func<bool>? Fix = null);

/// <summary>프로브 결과.</summary>
public readonly record struct AuditResult(AuditCategory Category, string Target, AuditOutcome Outcome, string Detail, double ElapsedMs);

/// <summary>진단 리포트(분류별 집계 · 소요 · 예산 초과 여부).</summary>
public sealed record AuditReport(IReadOnlyList<AuditResult> Results, double ElapsedMs, bool BudgetExceeded, int Fixed)
{
    public int Count(AuditOutcome o) { var n = 0; foreach (var r in Results) if (r.Outcome == o) n++; return n; }
    public int Count(AuditCategory c, AuditOutcome o) { var n = 0; foreach (var r in Results) if (r.Category == c && r.Outcome == o) n++; return n; }
    public bool Healthy => Count(AuditOutcome.Fail) == 0;
}

/// <summary>
/// [PromptFlow Studio v16.51.0] 🩺 E2E 모듈 연동 &amp; 자율 무결성 시스템 진단기.
///   ▸ DI: 등록된 PromptFlowStudio 서비스 전량 해석(생성 예외 포착) · UI: 모든 Razor 컴포넌트 [Inject] 의존성 해석 가능 여부(모달 ↔ DI 결선 누락 검출) · 모달 OnClose 계약.
///   ▸ 텔레메트리: 서비스 OnChange 이벤트/LastAction 계약 · 폴백: LLM 미연결 BCL 경로 E2E 스모크(토크쇼 · 멀티캠 · QR · 옴니채널 드라이런 · 정산 원장) · Zero-Alloc 가드 위반 사이트.
///   ▸ 자율 복구: 실패 프로브의 픽스 주입(격리 시스템 해제 · 실패 채널 리셋 · 위반 사이트 SIMD 우회) → 재검사(Fixed) · 데스크톱/모바일 DI 소스 패리티 비교.
///   ▸ 예산(기본 1초) 내 순차 진단 — 초과 시 잔여 Skipped.
/// </summary>
public sealed class EnterpriseE2eSystemHealthAuditor
{
    private readonly IServiceProvider _sp;
    private readonly Type[] _registered;
    private readonly List<AuditProbe> _extra = new();
    private long _runs, _fixes;

    public EnterpriseE2eSystemHealthAuditor(IServiceProvider sp, IEnumerable<Type> registeredServiceTypes)
    {
        _sp = sp;
        var set = new HashSet<Type>();
        foreach (var t in registeredServiceTypes) if (!t.IsGenericTypeDefinition) set.Add(t);
        _registered = set.ToArray();
    }

    public event Action? OnChange;

    public double BudgetMs { get; set; } = 1000;
    public bool AutoFix { get; set; } = true;
    public Assembly[] ComponentAssemblies { get; set; } = { typeof(EnterpriseE2eSystemHealthAuditor).Assembly };
    public AuditReport? Last { get; private set; }
    public long Runs => Interlocked.Read(ref _runs);
    public long FixesApplied => Interlocked.Read(ref _fixes);
    public int RegisteredCount => _registered.Length;
    public string? LastAction { get; private set; }

    public void AddProbe(AuditProbe p) { lock (_extra) _extra.Add(p); }

    // ══════════════════════ 프로브 구성 ══════════════════════

    public static bool IsAppType(Type t) => t.Namespace is { } ns && ns.StartsWith("PromptFlowStudio", StringComparison.Ordinal);

    public List<AuditProbe> BuildProbes()
    {
        var probes = new List<AuditProbe>(_registered.Length * 2 + 64);
        foreach (var t in _registered)
        {
            if (!IsAppType(t)) continue;
            var type = t;
            probes.Add(new AuditProbe(AuditCategory.DependencyInjection, type.Name, () =>
            {
                try { return _sp.GetService(type) is null ? (AuditOutcome.Fail, "미해석") : (AuditOutcome.Pass, "OK"); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or TargetInvocationException or NullReferenceException or IOException or UnauthorizedAccessException or NotSupportedException or TypeInitializationException)
                { return (AuditOutcome.Fail, ex.GetType().Name + ": " + ex.Message); }
            }));
            if (type.GetEvent("OnChange") is not null)
                probes.Add(new AuditProbe(AuditCategory.Telemetry, type.Name, () => type.GetProperty("LastAction") is not null || type.GetEvent("OnChange")!.EventHandlerType == typeof(Action)
                    ? (AuditOutcome.Pass, "OnChange 계약") : (AuditOutcome.Warn, "OnChange 시그니처 비표준")));
        }
        foreach (var asm in ComponentAssemblies)
            foreach (var c in SafeTypes(asm))
            {
                if (!typeof(ComponentBase).IsAssignableFrom(c) || c.IsAbstract || !IsAppType(c)) continue;
                var comp = c;
                var injects = InjectedTypes(comp);
                if (injects.Count > 0)
                    probes.Add(new AuditProbe(AuditCategory.UiInjection, comp.Name, () =>
                    {
                        foreach (var it in injects)
                        {
                            if (!IsAppType(it)) continue;
                            try { if (_sp.GetService(it) is null) return (AuditOutcome.Fail, "@inject " + it.Name + " 미등록"); }
                            catch (Exception ex) when (ex is InvalidOperationException or TargetInvocationException or ArgumentException) { return (AuditOutcome.Fail, "@inject " + it.Name + " 생성 실패: " + ex.Message); }
                        }
                        return (AuditOutcome.Pass, injects.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "개 의존성");
                    }));
                if (comp.Name.EndsWith("Modal", StringComparison.Ordinal))
                    probes.Add(new AuditProbe(AuditCategory.UiInjection, comp.Name + ".OnClose", () =>
                        comp.GetProperty("OnClose") is { } p && p.PropertyType == typeof(EventCallback) && p.IsDefined(typeof(ParameterAttribute)) ? (AuditOutcome.Pass, "닫기 계약") : (AuditOutcome.Warn, "OnClose 파라미터 없음")));
            }
        probes.AddRange(FallbackSmokeProbes());
        AddServiceProbes(probes);
        lock (_extra) probes.AddRange(_extra);
        return probes;
    }

    public static List<Type> InjectedTypes(Type component)
    {
        var res = new List<Type>();
        for (var t = component; t is not null && t != typeof(ComponentBase); t = t.BaseType)
            foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (p.IsDefined(typeof(InjectAttribute))) res.Add(p.PropertyType);
        return res;
    }

    private static Type[] SafeTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null).ToArray()!; }
    }

    /// <summary>LLM/외부 API 미연결 상태 BCL 폴백 경로 E2E 스모크(독립 인스턴스 — 싱글턴 상태 무접촉).</summary>
    public static IEnumerable<AuditProbe> FallbackSmokeProbes()
    {
        yield return new AuditProbe(AuditCategory.Fallback, "Talkshow BCL 진행", () =>
        {
            var t = new Podcast.MultiAgentTalkshowConversationOrchestrator(llm: null) { ExchangesPerAgenda = 2 };
            t.Start(0);
            double now = 0;
            for (var g = 0; t.IsLive && g < 20000; g++) { t.Tick(now); now += 200; }
            return !t.IsLive && t.LinesSpoken > 5 ? (AuditOutcome.Pass, t.LinesSpoken.ToString(System.Globalization.CultureInfo.InvariantCulture) + "줄") : (AuditOutcome.Fail, "토크쇼 미완주");
        });
        yield return new AuditProbe(AuditCategory.Fallback, "QR 자기 판독", () =>
        {
            const string url = "https://shop.example.com/p/audit?c=E2E";
            return Commerce.QrCodeEncoder.Decode(Commerce.QrCodeEncoder.Encode(url)) == url ? (AuditOutcome.Pass, "왕복 일치") : (AuditOutcome.Fail, "판독 불일치");
        });
        yield return new AuditProbe(AuditCategory.Fallback, "멀티캠 합성", () =>
        {
            var cam = new Podcast.AutonomousPodcastVideoMultiCamEngine();
            var f = new byte[160 * 90 * 4];
            cam.Render(f, 160, 90, 160 * 4, 0);
            return f[3] == 255 ? (AuditOutcome.Pass, "프레임 OK") : (AuditOutcome.Fail, "알파 누락");
        });
        yield return new AuditProbe(AuditCategory.Fallback, "옴니채널 드라이런", () =>
        {
            var o = new Empire.OmnichannelGlobalBroadcastDispatcher(null, _ => null) { SimulatedFailRate = 0, BackoffBaseMs = 1 };
            for (var i = 3; i < o.PlatformCount; i++) o.SetEnabled(i, false);
            var r = o.DispatchAsync(new Empire.DispatchPackage("audit", "E2E", "E2E", Array.Empty<string>(), 10, "ko", false), DateTimeOffset.UtcNow, 0).GetAwaiter().GetResult();
            return r.All(x => x.Ok && x.DryRun) ? (AuditOutcome.Pass, "3채널 드라이런") : (AuditOutcome.Fail, "드라이런 실패");
        });
        yield return new AuditProbe(AuditCategory.Fallback, "정산 원장 봉인", () =>
        {
            var key = new byte[32]; key[0] = 7;
            var m = new Empire.ExecutiveEmpireTelemetryAndRevenueMatrix(() => key, 3);
            m.Record(0, Empire.RevenueStream.Ads, 1_000_000, 100, 0);
            m.Settle(DateTimeOffset.UnixEpoch);
            return m.VerifyLedger(m.Ledger) == -1 ? (AuditOutcome.Pass, "HMAC 체인 OK") : (AuditOutcome.Fail, "원장 검증 실패");
        });
    }

    /// <summary>등록 서비스 상태 기반 프로브(+ 복구 픽스).</summary>
    private void AddServiceProbes(List<AuditProbe> probes)
    {
        if (_sp.GetService(typeof(ZeroAllocHotPathProfilingGuard)) is ZeroAllocHotPathProfilingGuard g)
            for (var i = 0; i < g.SiteCount; i++)
            {
                var s = i;
                probes.Add(new AuditProbe(AuditCategory.ZeroAlloc, g.SiteName(s), () =>
                {
                    var st = g.Stats(s);
                    if (!st.Instrumented) return (AuditOutcome.Pass, "미계측");
                    return st.Violations == 0 || st.Bypassed ? (AuditOutcome.Pass, st.Bypassed ? "SIMD 우회 중" : "무할당") : (AuditOutcome.Fail, "위반 " + st.Violations.ToString(System.Globalization.CultureInfo.InvariantCulture) + "회");
                }, () => { g.EnableBypass(s); return true; }));
            }
        if (_sp.GetService(typeof(Empire.ExecutiveEmpireTelemetryAndRevenueMatrix)) is Empire.ExecutiveEmpireTelemetryAndRevenueMatrix mx)
            for (var i = 0; i < mx.SystemCount; i++)
            {
                var s = i;
                probes.Add(new AuditProbe(AuditCategory.SelfHealing, mx.System(s).Version + " " + mx.System(s).Name, () =>
                    mx.System(s).Health switch
                    {
                        Empire.SystemHealth.Quarantined => (AuditOutcome.Fail, "격리됨"),
                        Empire.SystemHealth.Down => (AuditOutcome.Warn, "다운(자율 복구 대기)"),
                        Empire.SystemHealth.Degraded => (AuditOutcome.Warn, "저하"),
                        _ => (AuditOutcome.Pass, Empire.ExecutiveEmpireTelemetryAndRevenueMatrix.HealthLabel(mx.System(s).Health)),
                    }, () => { mx.Release(s); return true; }));
            }
        if (_sp.GetService(typeof(Empire.OmnichannelGlobalBroadcastDispatcher)) is Empire.OmnichannelGlobalBroadcastDispatcher omni)
            probes.Add(new AuditProbe(AuditCategory.SelfHealing, "옴니채널 실패 채널", () =>
            {
                var f = 0;
                for (var i = 0; i < omni.PlatformCount; i++) if (omni.State(i) == Empire.ChannelState.Failed) f++;
                return f == 0 ? (AuditOutcome.Pass, "실패 0") : (AuditOutcome.Fail, "실패 " + f.ToString(System.Globalization.CultureInfo.InvariantCulture) + "채널");
            }, () => { omni.ResetStates(); return true; }));
    }

    // ══════════════════════ 실행 ══════════════════════

    /// <summary>전체 진단(예산 내) → 실패 프로브 픽스 주입 → 재검사.</summary>
    public AuditReport Run()
    {
        var probes = BuildProbes();
        var sw = Stopwatch.StartNew();
        var results = new List<AuditResult>(probes.Count);
        var exceeded = false; var fixedN = 0;
        foreach (var p in probes)
        {
            if (sw.Elapsed.TotalMilliseconds > BudgetMs) { exceeded = true; results.Add(new AuditResult(p.Category, p.Target, AuditOutcome.Skipped, "예산 초과", 0)); continue; }
            var t0 = sw.Elapsed.TotalMilliseconds;
            var (o, d) = Evaluate(p);
            if (o == AuditOutcome.Fail && AutoFix && p.Fix is not null)
            {
                bool ok;
                try { ok = p.Fix(); } catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { ok = false; d += " · 픽스 실패: " + ex.Message; }
                if (ok)
                {
                    var (o2, d2) = Evaluate(p);
                    if (o2 is AuditOutcome.Pass or AuditOutcome.Warn) { o = AuditOutcome.Fixed; d = d + " → " + d2; fixedN++; Interlocked.Increment(ref _fixes); }
                }
            }
            results.Add(new AuditResult(p.Category, p.Target, o, d, sw.Elapsed.TotalMilliseconds - t0));
        }
        var rep = new AuditReport(results, sw.Elapsed.TotalMilliseconds, exceeded, fixedN);
        Last = rep;
        Interlocked.Increment(ref _runs);
        LastAction = "🩺 E2E 진단 " + results.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "항목 · 실패 " + rep.Count(AuditOutcome.Fail).ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " · 복구 " + fixedN.ToString(System.Globalization.CultureInfo.InvariantCulture) + " · " + rep.ElapsedMs.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "ms";
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("E2eAudit.OnChange", ex); }
        return rep;
    }

    private static (AuditOutcome, string) Evaluate(AuditProbe p)
    {
        try { return p.Check(); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { return (AuditOutcome.Fail, ex.GetType().Name + ": " + ex.Message); }
    }

    // ══════════════════════ 데스크톱/모바일 DI 패리티 ══════════════════════

    private static readonly Regex RegRx = new(@"AddSingleton(?:<\s*(?<g>[\w\.]+)\s*(?:,\s*(?<g2>[\w\.]+)\s*)?>)?\s*\((?:\s*sp\s*=>\s*new\s+(?<n>[\w\.]+))?", RegexOptions.Compiled);

    /// <summary>소스 텍스트의 AddSingleton 등록 타입명(구현 우선) 집합.</summary>
    public static HashSet<string> ParseRegistrations(string source)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in RegRx.Matches(source))
        {
            var name = m.Groups["n"].Success ? m.Groups["n"].Value : m.Groups["g2"].Success ? m.Groups["g2"].Value : m.Groups["g"].Value;
            if (name.Length == 0) continue;
            var dot = name.LastIndexOf('.');
            set.Add(dot >= 0 ? name[(dot + 1)..] : name);
        }
        return set;
    }

    /// <summary>데스크톱 전용 · 모바일 전용 등록 비교.</summary>
    public static (List<string> DesktopOnly, List<string> MobileOnly, int Shared) CompareRegistrations(string desktopSource, string mobileSource)
    {
        var d = ParseRegistrations(desktopSource); var m = ParseRegistrations(mobileSource);
        var dOnly = d.Where(x => !m.Contains(x)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var mOnly = m.Where(x => !d.Contains(x)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        return (dOnly, mOnly, d.Count(m.Contains));
    }

    public static string AuditCategoryLabel(AuditCategory c) => c switch
    {
        AuditCategory.DependencyInjection => "DI 서비스", AuditCategory.UiInjection => "UI 결선", AuditCategory.Telemetry => "텔레메트리", AuditCategory.Fallback => "폴백 E2E",
        AuditCategory.ZeroAlloc => "Zero-Alloc", AuditCategory.SelfHealing => "자율 복구", _ => "패리티",
    };
}
