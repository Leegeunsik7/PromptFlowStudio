using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;

namespace PromptFlowStudio.Services.Inference;

/// <summary>샌드박스 실행 결과(불변) — 성공/이름/경과ms/할당 바이트/오류.</summary>
public sealed record PluginResult(bool Ok, string Name, long ElapsedMs, long AllocatedBytes, string? Error)
{
    public bool TimedOut => Error == "시간 초과";
    public bool OverAllocated => Error == "메모리 임계 초과";
}

/// <summary>
/// [PromptFlow Studio v8.6.3] 네이티브 플러그인 격리 샌드박스(관찰 가능 싱글턴 · 순수 BCL · 외부 NuGet 0 · IDisposable).
///   ▸ 외부 확장 어셈블리를 수집 가능한 <see cref="AssemblyLoadContext"/>(collectible)로 <b>메모리 격리</b> 로드 → <see cref="UnloadPlugin"/> 시 완전 회수.
///   ▸ 확장 실행은 전용 스레드에서 구동해 <b>실행 시간</b>(타임아웃)·<b>메모리 할당</b>(<see cref="GC.GetAllocatedBytesForCurrentThread"/>) 임계를 강제 → 초과 시 결과를 실패로 격리.
///   ▸ 어떤 플러그인 예외/폭주도 호출자 스레드를 오염시키지 않는다(격리 · Graceful). 코어 파이프라인과 상태 공유 없음.
///   ⚠ <see cref="OnChange"/> 는 실행 완료(임의) 스레드에서 온다 — 컴포넌트는 InvokeAsync(StateHasChanged)로 마샬링.
/// </summary>
public sealed class NativePluginSandbox : IDisposable
{
    // v12.1.0 트리밍 경계: 외부 어셈블리를 런타임에 로드/리플렉션하는 것이 이 클래스의 존재 목적이라 소스 생성기로 대체 불가 -
    //   정적 분석기가 볼 수 없는 코드를 다룬다는 사실을 공개 진입점(LoadPlugin/Invoke)에 명시해 호출측으로 책임을 올린다.
    private const string DynamicLoadingReason = "외부 플러그인 어셈블리를 런타임에 로드하고 리플렉션으로 타입/메서드를 찾는다 - 트리밍/AOT 시 플러그인이 의존하는 코드가 제거될 수 있다.";

    private const int LogCap = 40;
    private readonly ConcurrentDictionary<string, PluginContext> _plugins = new();
    private readonly object _gate = new();
    private readonly List<PluginResult> _log = new();

    public event Action? OnChange;
    private void Notify() => OnChange?.Invoke();

    // ── 관측 지표(HUD) ──
    public long Executions { get; private set; }
    public long Failures { get; private set; }
    public long Rejected { get; private set; }   // 임계(시간/메모리) 위반
    public int LoadedPlugins => _plugins.Count;
    public string? LastResult { get; private set; }
    public IReadOnlyList<PluginResult> Recent { get { lock (_gate) return _log.Take(16).ToList(); } }

    /// <summary>확장 어셈블리를 격리 컨텍스트로 로드(수집 가능). 반환=어셈블리 FullName(실패 시 null).</summary>
    [RequiresUnreferencedCode(DynamicLoadingReason)]
    public string? LoadPlugin(string name, string assemblyPath)
    {
        try
        {
            var ctx = new PluginContext(name, assemblyPath);
            _plugins[name] = ctx;
            Notify();
            return ctx.Loaded?.FullName;
        }
        catch (Exception ex) { Program.Log("Sandbox.Load", ex); return null; }
    }

    /// <summary>격리 컨텍스트 언로드(collectible ALC 회수 요청).</summary>
    public void UnloadPlugin(string name)
    {
        if (_plugins.TryRemove(name, out var ctx))
        {
            try { ctx.Alc.Unload(); } catch { /* 무시 */ }
            Notify();
        }
    }

    /// <summary>
    /// 로드된 플러그인의 정적 파라미터리스 메서드를 샌드박스 실행(시간/메모리 임계 강제).
    /// </summary>
    [RequiresUnreferencedCode(DynamicLoadingReason)]
    public PluginResult Invoke(string pluginName, string typeName, string methodName, int timeoutMs = 2000, long maxAllocBytes = 8L * 1024 * 1024)
    {
        if (!_plugins.TryGetValue(pluginName, out var ctx))
            return Record(new PluginResult(false, pluginName, 0, 0, "미로드 플러그인"));

        return RunGuarded($"{pluginName}:{methodName}", () =>
        {
            var asm = ctx.Loaded ?? throw new InvalidOperationException("어셈블리 없음");
            var type = asm.GetType(typeName) ?? throw new MissingMethodException($"타입 없음: {typeName}");
            var mi = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)
                     ?? throw new MissingMethodException($"정적 메서드 없음: {methodName}");
            mi.Invoke(null, null);
        }, timeoutMs, maxAllocBytes);
    }

    /// <summary>
    /// 임의 델리게이트를 샌드박스 실행 — 전용 스레드 + 타임아웃 + 스레드 할당 임계 강제. 인프로세스 확장 훅 테스트용.
    /// </summary>
    public PluginResult RunGuarded(string name, Action work, int timeoutMs = 2000, long maxAllocBytes = 8L * 1024 * 1024)
    {
        var started = Environment.TickCount64;
        long alloc = 0;
        string? error = null;
        var done = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            try { work(); }
            catch (Exception ex) { error = Inner(ex); }
            finally { alloc = GC.GetAllocatedBytesForCurrentThread() - before; done.Set(); }
        }) { IsBackground = true, Name = "pfs-sandbox" };

        thread.Start();
        var finished = done.Wait(Math.Max(1, timeoutMs));
        var elapsed = Environment.TickCount64 - started;

        PluginResult result;
        if (!finished) result = new PluginResult(false, name, elapsed, alloc, "시간 초과");        // 협조 불가 → 결과 격리(스레드는 백그라운드로 방치)
        else if (error is not null) result = new PluginResult(false, name, elapsed, alloc, error);
        else if (alloc > maxAllocBytes) result = new PluginResult(false, name, elapsed, alloc, "메모리 임계 초과");
        else result = new PluginResult(true, name, elapsed, alloc, null);

        return Record(result);
    }

    private PluginResult Record(PluginResult r)
    {
        Executions++;
        if (!r.Ok) Failures++;
        if (r.TimedOut || r.OverAllocated) Rejected++;
        LastResult = $"[{(r.Ok ? "OK" : "FAIL")}] {r.Name} · {r.ElapsedMs}ms · {r.AllocatedBytes / 1024}KB{(r.Error is null ? "" : " · " + r.Error)}";
        lock (_gate) { _log.Insert(0, r); if (_log.Count > LogCap) _log.RemoveRange(LogCap, _log.Count - LogCap); }
        Notify();
        return r;
    }

    private static string Inner(Exception ex) => (ex is TargetInvocationException tie && tie.InnerException is { } i ? i.Message : ex.Message);

    /// <summary>수집 가능 격리 컨텍스트 + 의존성 해석기 래퍼.</summary>
    private sealed class PluginContext
    {
        public readonly AssemblyLoadContext Alc;
        private readonly AssemblyDependencyResolver _resolver;
        public Assembly? Loaded { get; private set; }

        [RequiresUnreferencedCode(DynamicLoadingReason)]
        public PluginContext(string name, string path)
        {
            _resolver = new AssemblyDependencyResolver(path);
            Alc = new IsolatedAlc(name, _resolver);
            Loaded = Alc.LoadFromAssemblyPath(path);   // 격리 컨텍스트로 1회 로드(의존성은 IsolatedAlc.Load 가 해석)
        }

        private sealed class IsolatedAlc : AssemblyLoadContext
        {
            private readonly AssemblyDependencyResolver _res;
            public IsolatedAlc(string name, AssemblyDependencyResolver res) : base(name, isCollectible: true) => _res = res;

            // 기반 AssemblyLoadContext.Load 에 RequiresUnreferencedCode 가 없어 오버라이드에 붙이면 IL2046(시그니처 불일치)이 된다 -
            //   이 컨텍스트는 오직 [RequiresUnreferencedCode] 가 붙은 LoadPlugin 경로로만 생성되므로 여기서는 억제가 정당하다.
            [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "LoadPlugin([RequiresUnreferencedCode])을 통해서만 생성되는 격리 컨텍스트의 의존성 해석 경로 - 경계는 상위 공개 API 에서 이미 명시됨.")]
            protected override Assembly? Load(AssemblyName assemblyName)
            {
                var p = _res.ResolveAssemblyToPath(assemblyName);
                return p is null ? null : LoadFromAssemblyPath(p);   // 격리 로드(기본 컨텍스트로 누수 방지)
            }
        }
    }

    public void Dispose()
    {
        foreach (var k in _plugins.Keys.ToList()) UnloadPlugin(k);
    }
}
