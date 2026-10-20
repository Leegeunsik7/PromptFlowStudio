#pragma warning disable BL0006 // RenderTree 프레임 직접 순회(헤드리스 렌더러 — 의도적 사용)
using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace PromptFlowStudio.Services.SelfFix;

public enum DomActionKind : byte { Click, Change, Check }
public enum DomOutcome : byte { Pass, Exception, Timeout, Skipped }

/// <summary>RenderTree 상의 구동 가능 요소(이벤트 핸들러 ID 보유).</summary>
public readonly record struct DomElementHandle(int ComponentId, ulong HandlerId, string Element, DomActionKind Kind, string Label, string? InputType, double Min, double Max, bool Checked);

/// <summary>이벤트 구동 기록(값 형식).</summary>
public readonly record struct DomEventRecord(long Seq, string Component, string Element, string Label, DomActionKind Kind, DomOutcome Outcome, double Ms, string? Error);

/// <summary>컴포넌트(모달) 단위 구동 결과.</summary>
public sealed class DomComponentReport
{
    public required string Name { get; init; }
    public bool Mounted;
    public string? MountError;
    public int Elements, Clicks, Changes, Exceptions, Timeouts, Skipped, Batches, CloseRequests;
    public double Ms;
    public bool Clean => Mounted && Exceptions == 0 && Timeouts == 0;
}

/// <summary>
/// [PromptFlow Studio v16.93.0] 🖱 실 Blazor RenderTree &amp; 컴포넌트 DOM 인터랙티브 구동 드라이버(헤드리스 Renderer 서브클래스).
///   ▸ 실제 컴포넌트 인스턴스화([Inject] 실 DI 해석 · IJSRuntime 스텁) → 루트 렌더 → <c>GetCurrentRenderTreeFrames</c> 재귀 순회(자식 컴포넌트 포함).
///   ▸ 요소 수집: onclick/onchange 핸들러 ID · 요소명 · 라벨(텍스트/aria-label/title) · range min/max · checkbox checked · disabled 제외.
///   ▸ 구동: 디스패처 내부에서 재수집 + <c>DispatchEventAsync</c>(실 MouseEventArgs/ChangeEventArgs · EventFieldInfo) — 재렌더로 인한 핸들러 ID 경합 차단.
///   ▸ 예외 전수 수집: 이벤트 Task 장애 + Renderer.HandleException(렌더/수명주기/비동기 핸들러) · 이벤트 타임아웃 · 위험 라벨(삭제/내보내기/전송 …) 생략.
///   ▸ 모달 탐색: 어셈블리 내 *StudioModal 컴포넌트 전수 · OnClose 파라미터 자동 결선(닫기 요청 집계).
/// </summary>
public sealed class RealDomBlazorRenderTreeDriver
{
    public const int LogSize = 256;
    public static readonly string[] DefaultDenyTokens = { "삭제", "제거", "내보내기", "전송", "업로드", "다운로드", "결제", "로그아웃", "종료", "Delete", "Export", "Send", "Upload", "Download" };

    private readonly IServiceProvider _sp;
    private readonly object _gate = new();
    private readonly DomEventRecord[] _log = new DomEventRecord[LogSize];
    private readonly List<DomComponentReport> _reports = new();
    private int _logHead, _logCount;
    private long _seq;

    public RealDomBlazorRenderTreeDriver(IServiceProvider sp) => _sp = new DriverServices(sp);

    public double EventTimeoutMs { get; set; } = 1500d;
    public int SettleMs { get; set; } = 2;
    public int MaxActionsPerComponent { get; set; } = 160;
    public string[] DenyTokens { get; set; } = DefaultDenyTokens;
    /// <summary>컴포넌트 1개 구동 후 훅(상태 복원 · 스튜디오 정지).</summary>
    public Action<Type>? AfterComponent { get; set; }

    public event Action? OnProgress;

    public int ComponentTotal { get; private set; }
    public int ComponentIndex { get; private set; }
    public string? Current { get; private set; }
    public long Events { get; private set; }
    public long Clicks { get; private set; }
    public long Changes { get; private set; }
    public long Exceptions { get; private set; }
    public long Timeouts { get; private set; }
    public long Skipped { get; private set; }
    public long MountFaults { get; private set; }
    public long Batches { get; private set; }
    public double LastRunMs { get; private set; }
    public int LogCount => _logCount;
    public DomEventRecord Log(int back) { lock (_gate) return _log[(_logHead - 1 - back + 2 * LogSize) % LogSize]; }
    public int ReportCount { get { lock (_gate) return _reports.Count; } }
    public DomComponentReport Report(int i) { lock (_gate) return _reports[i]; }
    public double Progress => ComponentTotal == 0 ? 0d : (double)ComponentIndex / ComponentTotal;

    /// <summary>어셈블리 내 스튜디오 모달 컴포넌트 전수(이름 정렬).</summary>
    public static List<Type> DiscoverStudioModals(Assembly? asm = null)
    {
        var list = new List<Type>();
        foreach (var t in (asm ?? typeof(RealDomBlazorRenderTreeDriver).Assembly).GetTypes())
            if (t.IsClass && !t.IsAbstract && !t.IsGenericTypeDefinition && typeof(IComponent).IsAssignableFrom(t) && t.Name.EndsWith("StudioModal", StringComparison.Ordinal)) list.Add(t);
        list.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list;
    }

    public void Reset()
    {
        lock (_gate)
        {
            _reports.Clear(); _logHead = _logCount = 0;
            Events = Clicks = Changes = Exceptions = Timeouts = Skipped = MountFaults = Batches = 0;
            ComponentIndex = ComponentTotal = 0; Current = null;
        }
    }

    /// <summary>전수 구동(순차 · 취소 가능).</summary>
    public async Task DriveAllAsync(IReadOnlyList<Type> types, CancellationToken ct)
    {
        Reset();
        ComponentTotal = types.Count;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var i = 0; i < types.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            ComponentIndex = i;
            await DriveAsync(types[i], ct).ConfigureAwait(false);
        }
        ComponentIndex = types.Count;
        LastRunMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        Raise();
    }

    /// <summary>단일 컴포넌트 마운트 → 요소 전수 구동 → 언마운트.</summary>
    public async Task<DomComponentReport> DriveAsync(Type type, CancellationToken ct)
    {
        var rep = new DomComponentReport { Name = type.Name };
        lock (_gate) _reports.Add(rep);
        Current = type.Name; Raise();
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var r = new HeadlessRenderer(_sp);
        try
        {
            int root;
            try
            {
                root = await r.Dispatcher.InvokeAsync(() => r.Mount(type)).ConfigureAwait(false);
                var pv = BuildParameters(type, rep);
                await r.Dispatcher.InvokeAsync(() => r.RenderRoot(root, pv)).ConfigureAwait(false);
                if (r.TakeErrors() is { } mountErr) throw mountErr;
                rep.Mounted = true;
            }
            catch (Exception ex)
            {
                rep.MountError = ex.GetType().Name + ": " + ex.Message;
                lock (_gate) MountFaults++;
                Record(type.Name, "mount", "마운트", DomActionKind.Click, DomOutcome.Exception, 0d, rep.MountError);
                return rep;
            }

            var handles = new List<DomElementHandle>(64);
            await r.Dispatcher.InvokeAsync(() => { handles.Clear(); Collect(r, root, handles, 0); }).ConfigureAwait(false);
            rep.Elements = handles.Count;
            for (var k = 0; k < MaxActionsPerComponent; k++)
            {
                ct.ThrowIfCancellationRequested();
                DomElementHandle h = default;
                var found = false; var skip = false;
                Task? dispatch = null;
                var e0 = System.Diagnostics.Stopwatch.GetTimestamp();
                await r.Dispatcher.InvokeAsync(() =>
                {
                    handles.Clear(); Collect(r, root, handles, 0);
                    if (k >= handles.Count) return;
                    found = true; h = handles[k];
                    if (IsDenied(h.Label)) { skip = true; return; }
                    try { dispatch = r.DispatchEventAsync(h.HandlerId, FieldInfo(h), Args(h)); }
                    catch (Exception ex) { dispatch = Task.FromException(ex); }
                }).ConfigureAwait(false);
                if (!found) break;
                if (skip) { rep.Skipped++; lock (_gate) Skipped++; Record(type.Name, h.Element, h.Label, h.Kind, DomOutcome.Skipped, 0d, "위험 라벨 생략"); continue; }

                var outcome = DomOutcome.Pass; string? err = null;
                if (dispatch is not null)
                {
                    using var tcs = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var delay = Task.Delay(TimeSpan.FromMilliseconds(EventTimeoutMs), tcs.Token);
                    var done = await Task.WhenAny(dispatch, delay).ConfigureAwait(false);
                    tcs.Cancel();
                    if (done != dispatch) { outcome = DomOutcome.Timeout; err = "이벤트 지연 > " + EventTimeoutMs.ToString("0", CultureInfo.InvariantCulture) + " ms"; _ = dispatch.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default); }
                    else if (dispatch.IsFaulted) { outcome = DomOutcome.Exception; err = Describe(dispatch.Exception!.InnerException ?? dispatch.Exception); }
                }
                if (SettleMs > 0) await Task.Delay(SettleMs, ct).ConfigureAwait(false);
                if (r.TakeErrors() is { } renderErr && outcome == DomOutcome.Pass) { outcome = DomOutcome.Exception; err = Describe(renderErr); }

                var ms = System.Diagnostics.Stopwatch.GetElapsedTime(e0).TotalMilliseconds;
                if (h.Kind == DomActionKind.Click) { rep.Clicks++; lock (_gate) Clicks++; } else { rep.Changes++; lock (_gate) Changes++; }
                lock (_gate) Events++;
                if (outcome == DomOutcome.Exception) { rep.Exceptions++; lock (_gate) Exceptions++; }
                else if (outcome == DomOutcome.Timeout) { rep.Timeouts++; lock (_gate) Timeouts++; }
                Record(type.Name, h.Element, h.Label, h.Kind, outcome, ms, err);
                if ((k & 7) == 0) Raise();
            }
        }
        finally
        {
            try { await r.Dispatcher.InvokeAsync(async () => await r.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false); }
            catch (Exception ex) { PromptFlowStudio.Program.Log("SelfFix.Dom.Dispose", ex); }
            rep.Batches = r.Batches;
            lock (_gate) Batches += r.Batches;
            rep.Ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            try { AfterComponent?.Invoke(type); } catch (Exception ex) { PromptFlowStudio.Program.Log("SelfFix.Dom.AfterComponent", ex); }
            Raise();
        }
        return rep;
    }

    private bool IsDenied(string label)
    {
        var deny = DenyTokens;
        for (var i = 0; i < deny.Length; i++) if (label.Contains(deny[i], StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string Describe(Exception ex) => ex.GetType().Name + ": " + ex.Message;

    private static ParameterView BuildParameters(Type type, DomComponentReport rep)
    {
        var p = type.GetProperty("OnClose", BindingFlags.Public | BindingFlags.Instance);
        if (p is null || p.PropertyType != typeof(EventCallback) || p.GetCustomAttribute<ParameterAttribute>() is null) return ParameterView.Empty;
        var cb = EventCallback.Factory.Create(rep, () => rep.CloseRequests++);
        return ParameterView.FromDictionary(new Dictionary<string, object?> { ["OnClose"] = cb });
    }

    private static EventFieldInfo? FieldInfo(in DomElementHandle h) => h.Kind switch
    {
        DomActionKind.Change => new EventFieldInfo { ComponentId = h.ComponentId, FieldValue = Mid(h) },
        DomActionKind.Check => new EventFieldInfo { ComponentId = h.ComponentId, FieldValue = !h.Checked },
        _ => null,
    };

    private static EventArgs Args(in DomElementHandle h) => h.Kind switch
    {
        DomActionKind.Change => new ChangeEventArgs { Value = Mid(h) },
        DomActionKind.Check => new ChangeEventArgs { Value = !h.Checked },
        _ => new Microsoft.AspNetCore.Components.Web.MouseEventArgs { Type = "click", Detail = 1, Button = 0 },
    };

    private static string Mid(in DomElementHandle h) => ((h.Min + h.Max) * 0.5).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>현재 RenderTree 재귀 순회 → 구동 가능 요소 수집(문서 순서).</summary>
    private static void Collect(HeadlessRenderer r, int componentId, List<DomElementHandle> list, int depth)
    {
        ArrayRange<RenderTreeFrame> range;
        try { range = r.Frames(componentId); } catch (ArgumentException) { return; }
        var arr = range.Array; var n = range.Count;
        for (var i = 0; i < n; i++)
        {
            ref var f = ref arr[i];
            if (f.FrameType == RenderTreeFrameType.Component)
            {
                if (depth < 12 && f.ComponentId > 0) Collect(r, f.ComponentId, list, depth + 1);
                continue;
            }
            if (f.FrameType != RenderTreeFrameType.Element) continue;
            ulong click = 0, change = 0;
            string? type = null, title = null, aria = null;
            double min = 0d, max = 100d; bool disabled = false, chk = false;
            for (var j = i + 1; j < n && arr[j].FrameType == RenderTreeFrameType.Attribute; j++)
            {
                ref var a = ref arr[j];
                switch (a.AttributeName)
                {
                    case "onclick": if (a.AttributeEventHandlerId != 0) click = a.AttributeEventHandlerId; break;
                    case "onchange": if (a.AttributeEventHandlerId != 0) change = a.AttributeEventHandlerId; break;
                    case "type": type = a.AttributeValue as string; break;
                    case "title": title = a.AttributeValue as string; break;
                    case "aria-label": aria = a.AttributeValue as string; break;
                    case "min": min = Num(a.AttributeValue, 0d); break;
                    case "max": max = Num(a.AttributeValue, 100d); break;
                    case "disabled": disabled = a.AttributeValue is not false; break;
                    case "checked": chk = a.AttributeValue is true; break;
                }
            }
            if (disabled || (click == 0 && change == 0)) continue;
            var label = TextOf(arr, i, f.ElementSubtreeLength) ?? aria ?? title ?? f.ElementName;
            if (click != 0) list.Add(new DomElementHandle(componentId, click, f.ElementName, DomActionKind.Click, label, type, min, max, chk));
            if (change != 0)
            {
                if (type == "checkbox") list.Add(new DomElementHandle(componentId, change, f.ElementName, DomActionKind.Check, label, type, min, max, chk));
                else if (type is "range" or "number") list.Add(new DomElementHandle(componentId, change, f.ElementName, DomActionKind.Change, label, type, min, max, chk));
            }
        }
    }

    private static double Num(object? v, double fallback) =>
        double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;

    private static string? TextOf(RenderTreeFrame[] arr, int i, int subtree)
    {
        System.Text.StringBuilder? sb = null;
        var end = Math.Min(arr.Length, i + subtree);
        for (var k = i + 1; k < end; k++)
        {
            var s = arr[k].FrameType switch { RenderTreeFrameType.Text => arr[k].TextContent, RenderTreeFrameType.Markup => arr[k].MarkupContent, _ => null };
            if (string.IsNullOrWhiteSpace(s) || s[0] == '<') continue;
            (sb ??= new System.Text.StringBuilder()).Append(s.Trim()).Append(' ');
            if (sb.Length > 48) break;
        }
        if (sb is null) return null;
        var t = sb.ToString().Trim();
        return t.Length > 48 ? t[..48] : t;
    }

    private void Record(string comp, string el, string label, DomActionKind kind, DomOutcome o, double ms, string? err)
    {
        lock (_gate)
        {
            _log[_logHead] = new DomEventRecord(++_seq, comp, el, label, kind, o, ms, err);
            _logHead = (_logHead + 1) % LogSize; if (_logCount < LogSize) _logCount++;
        }
    }

    private void Raise()
    {
        try { OnProgress?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("SelfFix.Dom.OnProgress", ex); }
    }

    /// <summary>헤드리스 렌더러: 배치 집계 · 예외 수집 · 보호 API 노출.</summary>
    private sealed class HeadlessRenderer : Renderer
    {
        private readonly Dispatcher _dispatcher = Dispatcher.CreateDefault();
        private readonly List<Exception> _errors = new();

        public HeadlessRenderer(IServiceProvider sp) : base(sp, NullLoggerFactory.Instance) { }

        public override Dispatcher Dispatcher => _dispatcher;
        public int Batches;

        public int Mount(Type t) => AssignRootComponentId(InstantiateComponent(t));
        public Task RenderRoot(int id, ParameterView p) => RenderRootComponentAsync(id, p);
        public ArrayRange<RenderTreeFrame> Frames(int id) => GetCurrentRenderTreeFrames(id);

        public Exception? TakeErrors()
        {
            lock (_errors)
            {
                if (_errors.Count == 0) return null;
                var e = _errors[0]; _errors.Clear();
                return e;
            }
        }

        protected override void HandleException(Exception exception) { lock (_errors) _errors.Add(exception); }

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) { Batches++; return Task.CompletedTask; }
    }

    /// <summary>DI 래퍼: IJSRuntime 스텁(헤드리스) → 나머지는 실 서비스 공급자.</summary>
    private sealed class DriverServices : IServiceProvider
    {
        private readonly IServiceProvider _inner;
        public DriverServices(IServiceProvider inner) => _inner = inner;
        public object? GetService(Type t) => t == typeof(IJSRuntime) ? StubJs.Instance : _inner.GetService(t);
    }

    private sealed class StubJs : IJSRuntime
    {
        public static readonly StubJs Instance = new();
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => default;
    }
}
