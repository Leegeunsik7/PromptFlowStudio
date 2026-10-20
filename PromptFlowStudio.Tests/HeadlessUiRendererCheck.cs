using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace PromptFlowStudio.Tests;

/// <summary>
/// [PromptFlow Studio v17.63.0] 헤드리스 UI 가상 렌더링 검증기 — 외부 bUnit 없이 .NET 8 내장 <see cref="HtmlRenderer"/> 로
/// 스튜디오 모달을 정적 HTML 까지 렌더(OnInitialized → BuildRenderTree) 후 렌더러 Dispose 로 컴포넌트 Dispose 경로까지 구동한다.
///   ▸ <see cref="AutoWireProvider"/>: 요청된 구체 러너를 (IServiceProvider) 또는 무인자 생성자로 지연 생성 · 싱글턴 캐시 · IJSRuntime 스텁.
///   ▸ <see cref="LiveSubscriberCount"/>: 러너의 필드형 이벤트 백킹 델리게이트 구독자 수 — 모달 Dispose 후 0 이어야 누수 없음.
///   ▸ <see cref="AuditRazorLifecycle"/>: .razor 소스 정적 감사 — 외부 이벤트 <c>+=</c> 구독은 <c>-=</c> 해제 + IDisposable 구현 필수.
/// </summary>
internal static class HeadlessUiRendererCheck
{
    public static async Task<string> RenderAsync(Type component, IServiceProvider sp)
    {
        await using var html = new HtmlRenderer(sp, NullLoggerFactory.Instance);
        return await html.Dispatcher.InvokeAsync(async () =>
        {
            var root = await html.RenderComponentAsync(component, ParameterView.Empty);
            return root.ToHtmlString();
        });
    }

    public static int LiveSubscriberCount(object target)
    {
        var n = 0;
        for (var t = target.GetType(); t is not null && t != typeof(object); t = t.BaseType)
            foreach (var ev in t.GetEvents(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (t.GetField(ev.Name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)?.GetValue(target) is Delegate d)
                    n += d.GetInvocationList().Length;
        return n;
    }

    private static readonly Regex Subscribe = new(@"(\b_?[A-Za-z]\w*(?:\.\w+)+)\s*\+=\s*([A-Z]\w*)\s*;", RegexOptions.Compiled);

    /// <summary>위반 목록(파일: 구독식) — 빈 목록이면 전 컴포넌트 구독/해제 쌍 + IDisposable 충족.</summary>
    public static List<string> AuditRazorLifecycle(string blazorRoot, out int scanned)
    {
        var bad = new List<string>();
        scanned = 0;
        foreach (var path in Directory.EnumerateFiles(blazorRoot, "*.razor", SearchOption.AllDirectories))
        {
            var src = File.ReadAllText(path);
            var cs = path + ".cs";
            if (File.Exists(cs)) src += File.ReadAllText(cs);
            scanned++;
            var disposable = Regex.IsMatch(src, @"@implements\s+(System\.)?I(Async)?Disposable|:\s*[^{;]*\bI(Async)?Disposable\b");
            foreach (Match m in Subscribe.Matches(src))
            {
                var unsub = m.Groups[1].Value + " -= " + m.Groups[2].Value;
                var tight = m.Groups[1].Value + "-=" + m.Groups[2].Value;
                if (!src.Contains(unsub, StringComparison.Ordinal) && !src.Contains(tight, StringComparison.Ordinal) || !disposable)
                    bad.Add(Path.GetFileName(path) + ": " + m.Value);
            }
        }
        return bad;
    }

    public sealed class AutoWireProvider : IServiceProvider, IDisposable
    {
        private readonly ConcurrentDictionary<Type, object> _map = new();

        public object? GetService(Type t)
        {
            if (t == typeof(IServiceProvider)) return this;
            if (t == typeof(IJSRuntime)) return StubJs.Instance;
            if (_map.TryGetValue(t, out var o)) return o;
            if (!t.IsClass || t.IsAbstract || !t.Name.EndsWith("Runner", StringComparison.Ordinal)) return null;
            var ctor = t.GetConstructor(new[] { typeof(IServiceProvider) });
            var made = ctor is not null ? ctor.Invoke(new object[] { this }) : Activator.CreateInstance(t);
            return made is null ? null : _map.GetOrAdd(t, made);
        }

        public T Get<T>() where T : class => (T)GetService(typeof(T))!;

        public void Dispose()
        {
            foreach (var o in _map.Values)
                try { (o as IDisposable)?.Dispose(); } catch { }
            _map.Clear();
        }
    }

    private sealed class StubJs : IJSRuntime
    {
        public static readonly StubJs Instance = new();
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => default;
    }
}
