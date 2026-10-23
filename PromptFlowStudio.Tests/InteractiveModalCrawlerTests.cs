#pragma warning disable BL0006 // RenderTree 프레임 직접 순회는 크롤러의 본질(외부 bUnit 없이 BCL Renderer 로 실 이벤트 디스패치)
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using PromptFlowStudio.Services.Ui;
using Xunit.Abstractions;

namespace PromptFlowStudio.Tests;

/// <summary>
/// [PromptFlow Studio] 175 카탈로그 모달 동적 인터랙션 크롤러 — 외부 bUnit 없이 BCL <see cref="Renderer"/> 서브클래스로
/// 프로덕션 DI 그래프(<c>MainForm.ConfigureServices</c>)에 실제 마운트 → 렌더 트리의 모든 onclick/onchange/oninput 핸들러를
/// <see cref="Renderer.DispatchEventAsync"/> 로 실발화 → 예외 · 빈 람다 핸들러 · 값이 고정된 스위치/슬라이더/aria-pressed 토글을 전수 판정.
/// </summary>
[Collection("ProcessGcMode")]
public class InteractiveModalCrawlerTests
{
    private const int MaxDispatchPerModal = 400;
    private static readonly TimeSpan DispatchWait = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan AsyncSettle = TimeSpan.FromSeconds(3);
    private readonly ITestOutputHelper _out;

    public InteractiveModalCrawlerTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task AllCatalogModals_MountAndEveryInteractiveControlDispatchesWithLiveBinding()
    {
        var targets = ResolveCatalogTargets();
        Assert.Equal(StudioCatalog.Entries.Length, targets.Count);

        var services = new ServiceCollection();
        using var owner = new System.Windows.Forms.Control();
        MainForm.ConfigureServices(services, new ConfigurationBuilder().Build(), owner);
        services.AddSingleton<PromptFlowStudio.Services.Platform.IFilePickerService, HeadlessDialogs>();   // 모달 대화상자 대신 취소(null) — 크롤 중 UI 블로킹 차단
        services.AddSingleton<PromptFlowStudio.Services.Platform.IVideoExportService, HeadlessDialogs>();
        services.AddSingleton<PromptFlowStudio.Services.Platform.IClipboardService, HeadlessDialogs>();
        await using var root = services.BuildServiceProvider();

        var stats = new CatStats[StudioCatalog.Categories.Length];
        for (var i = 0; i < stats.Length; i++) stats[i] = new CatStats();
        var defects = new List<string>();

        var only = Environment.GetEnvironmentVariable("PFS_CRAWL_ONLY");   // 디버그 재현용 키 필터(쉼표 구분) — 미설정 = 175 전수
        foreach (var t in targets)
        {
            if (!string.IsNullOrEmpty(only) && Array.IndexOf(only.Split(','), t.Entry.Key) < 0) continue;
            var s = stats[t.Entry.Category];
            s.Modals++;
            var r = await CrawlAsync(root, t);
            if (r.MountError is null) s.Mounted++; else defects.Add("[MOUNT] " + t.Entry.Key + " " + t.Component.Name + ": " + r.MountError);
            s.Switches += r.Switches; s.Sliders += r.Sliders; s.Buttons += r.Buttons; s.Selects += r.Selects; s.Inputs += r.Inputs;
            s.Dispatched += r.Dispatched; s.Healthy += r.Healthy; s.Reacted += r.Reacted; s.Disabled += r.Disabled;
            defects.AddRange(r.Defects);
        }

        var sb = new StringBuilder();
        sb.AppendLine("| Cat | 모달 | 마운트 | 실패 | 스위치 | 슬라이더 | 버튼 | 셀렉트 | 입력 | 디스패치 | 정상 | 정상률 | DOM반응 | 비활성 |");
        var total = new CatStats();
        for (var c = 0; c < stats.Length; c++) { Row(sb, c.ToString(CultureInfo.InvariantCulture), stats[c]); total.Add(stats[c]); }
        Row(sb, "Σ", total);
        sb.AppendLine("결함 " + defects.Count.ToString(CultureInfo.InvariantCulture) + "건");
        foreach (var d in defects) sb.AppendLine("  " + d);
        _out.WriteLine(sb.ToString());
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "pfs_modal_crawl_report.md"), sb.ToString()); } catch { /* 리포트 사본은 best-effort */ }

        Assert.Equal(total.Modals, total.Mounted);
        if (string.IsNullOrEmpty(only)) Assert.Equal(targets.Count, total.Modals);
        Assert.True(total.Dispatched > 0);
        Assert.True(defects.Count == 0, sb.ToString());
    }

    private static void Row(StringBuilder sb, string cat, CatStats s)
    {
        var inv = CultureInfo.InvariantCulture;
        sb.Append("| ").Append(cat).Append(" | ").Append(s.Modals).Append(" | ").Append(s.Mounted).Append(" | ").Append(s.Modals - s.Mounted)
          .Append(" | ").Append(s.Switches).Append(" | ").Append(s.Sliders).Append(" | ").Append(s.Buttons).Append(" | ").Append(s.Selects).Append(" | ").Append(s.Inputs)
          .Append(" | ").Append(s.Dispatched).Append(" | ").Append(s.Healthy)
          .Append(" | ").Append(s.Dispatched == 0 ? "-" : (100.0 * s.Healthy / s.Dispatched).ToString("0.0", inv) + "%")
          .Append(" | ").Append(s.Dispatched == 0 ? "-" : (100.0 * s.Reacted / s.Dispatched).ToString("0.0", inv) + "%")
          .Append(" | ").Append(s.Disabled).AppendLine(" |");
    }

    // ══ 카탈로그 키 → 실제 모달 컴포넌트 결선(MainLayout/Storyboard 마크업이 단일 진실 원천) ══

    private sealed record Target(StudioEntry Entry, Type Component, string[] PassedParams);

    private static List<Target> ResolveCatalogTargets()
    {
        var blazor = Path.Combine(RepoRoot(), "PromptFlowStudio", "Blazor");
        var layout = MapFlags(File.ReadAllText(Path.Combine(blazor, "MainLayout.razor")));
        var board = MapFlags(File.ReadAllText(Path.Combine(blazor, "Pages", "Storyboard.razor")));
        var asm = typeof(MainForm).Assembly;
        var byName = new Dictionary<string, List<Type>>(StringComparer.Ordinal);
        foreach (var t in asm.GetTypes())
            if (!t.IsAbstract && typeof(IComponent).IsAssignableFrom(t))
                (byName.TryGetValue(t.Name, out var l) ? l : byName[t.Name] = new List<Type>()).Add(t);

        var list = new List<Target>();
        foreach (var e in StudioCatalog.Entries)
        {
            (string Comp, string[] Attrs) m;
            if (e.Key == "@palette") m = ("CommandPalette", Array.Empty<string>());
            else if (e.Key == "@help") m = ("HelpDrawer", Array.Empty<string>());
            else if (e.Key.StartsWith("sb:", StringComparison.Ordinal)) Assert.True(board.TryGetValue(e.Key[3..], out m), "스토리보드 결선 없음: " + e.Key);
            else Assert.True(layout.TryGetValue(e.Key.StartsWith("L_", StringComparison.Ordinal) ? e.Key[1..] : e.Key, out m), "레이아웃 결선 없음: " + e.Key);
            Assert.True(byName.TryGetValue(m.Comp, out var types) && types.Count == 1, "컴포넌트 타입 해석 실패: " + m.Comp);
            list.Add(new Target(e, types![0], m.Attrs));
        }
        return list;
    }

    private static readonly Regex FlagBlock = new(@"@if\s*\(\s*(_\w+)\s*\)\s*\{\s*<([A-Z]\w*)([^>]*)>", RegexOptions.Compiled);
    private static readonly Regex AttrName = new(@"(?<![@\w:-])([A-Z]\w*)\s*=", RegexOptions.Compiled);

    private static Dictionary<string, (string, string[])> MapFlags(string src)
    {
        var map = new Dictionary<string, (string, string[])>(StringComparer.Ordinal);
        foreach (Match m in FlagBlock.Matches(src))
        {
            var attrs = new List<string>();
            foreach (Match a in AttrName.Matches(m.Groups[3].Value)) attrs.Add(a.Groups[1].Value);
            map.TryAdd(m.Groups[1].Value, (m.Groups[2].Value, attrs.ToArray()));
        }
        return map;
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "PromptFlowStudio", "PromptFlowStudio.csproj"))) return d.FullName;
        throw new DirectoryNotFoundException("레포 루트");
    }

    // ══ 크롤 1모달 ══

    private sealed class CrawlResult
    {
        public string? MountError;
        public int Switches, Sliders, Buttons, Selects, Inputs, Dispatched, Healthy, Reacted, Disabled;
        public readonly List<string> Defects = new();
    }

    private sealed class CatStats
    {
        public int Modals, Mounted, Switches, Sliders, Buttons, Selects, Inputs, Dispatched, Healthy, Reacted, Disabled;
        public void Add(CatStats o)
        {
            Modals += o.Modals; Mounted += o.Mounted; Switches += o.Switches; Sliders += o.Sliders; Buttons += o.Buttons; Selects += o.Selects;
            Inputs += o.Inputs; Dispatched += o.Dispatched; Healthy += o.Healthy; Reacted += o.Reacted; Disabled += o.Disabled;
        }
    }

    private static async Task<CrawlResult> CrawlAsync(ServiceProvider root, Target t)
    {
        var res = new CrawlResult();
        await using var scope = root.CreateAsyncScope();
        var sp = new CrawlProvider(scope.ServiceProvider);
        if (t.Entry.Key == "@palette") sp.GetRequiredService<CommandPaletteService>().Open();
        if (t.Entry.Key == "@help") sp.GetRequiredService<PromptFlowStudio.Services.Help.HelpState>().Open();
        var r = new CrawlRenderer(sp);
        var tag = t.Entry.Key + " " + t.Component.Name;
        try
        {
            int id;
            try
            {
                id = await r.Dispatcher.InvokeAsync(() => r.Mount(t.Component));
                var render = r.Dispatcher.InvokeAsync(() => r.RenderAsync(id, RootParameters(t)));
                await Task.WhenAny(render, Task.Delay(TimeSpan.FromSeconds(3)));
                if (render.IsFaulted) throw render.Exception!.GetBaseException();
                if (r.TakeErrors() is { } boot) throw boot;
            }
            catch (Exception ex) { res.MountError = ex.GetType().Name + ": " + ex.Message; return res; }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pending = new List<(string Key, Task Task)>();
            var dispatches = 0;
            while (dispatches < MaxDispatchPerModal)
            {
                var snapshot = await r.Dispatcher.InvokeAsync(() => r.Scan(id));
                string? next = null;
                foreach (var c in snapshot.Controls)
                    if (seen.Add(c.Key))
                    {
                        if (c.Disabled) { res.Disabled++; continue; }
                        next = c.Key;
                        break;
                    }
                if (next is null) break;

                Ctl? before = null;
                var task = await r.Dispatcher.InvokeAsync<Task>(() =>
                {
                    var now = r.Scan(id);
                    before = now.Find(next);
                    return before is null ? Task.CompletedTask : r.DispatchEventAsync(before.HandlerId, null, ArgsFor(before));
                });
                if (before is null) continue;
                dispatches++;
                res.Dispatched++;
                switch (before.Kind) { case "switch": res.Switches++; break; case "slider": res.Sliders++; break; case "select": res.Selects++; break; case "input": res.Inputs++; break; default: res.Buttons++; break; }

                // 비동기 핸들러(네트워크 프로브 등)는 완료까지 최대 AsyncSettle 대기 후 판정 — 미완료면 STUCK 판정 보류(오탐 방지)
                var settled = await Task.WhenAny(task, Task.Delay(before.Kind == "button" ? DispatchWait : AsyncSettle)) == task;
                if (!settled) pending.Add((tag + " ▸ " + before.Key, task));
                var fault = task.IsFaulted ? task.Exception!.GetBaseException() : r.TakeErrors();
                if (fault is null && before.FollowId != 0)
                {
                    var follow = await r.Dispatcher.InvokeAsync<Task>(() =>
                    {
                        var mid = r.Scan(id).Find(next);
                        return mid is null || mid.FollowId == 0 ? Task.CompletedTask : r.DispatchEventAsync(mid.FollowId, null, ArgsFor(before));
                    });
                    if (await Task.WhenAny(follow, Task.Delay(DispatchWait)) != follow) pending.Add((tag + " ▸ " + before.Key + " (change)", follow));
                    fault = follow.IsFaulted ? follow.Exception!.GetBaseException() : r.TakeErrors();
                }
                var after = await r.Dispatcher.InvokeAsync(() => r.Scan(id));
                var ok = true;
                if (fault is not null) { ok = false; res.Defects.Add("[THROW] " + tag + " ▸ " + before.Key + ": " + fault.GetType().Name + " " + fault.Message); }
                if (before.EmptyHandler) { ok = false; res.Defects.Add("[EMPTY] " + tag + " ▸ " + before.Key + " — 빈 람다 핸들러(" + before.Event + ")"); }
                if (settled && Stuck(before, after.Find(next))) { ok = false; res.Defects.Add("[STUCK] " + tag + " ▸ " + before.Key + " — 값 고정(" + before.Event + (before.FollowId != 0 ? "+onchange" : "") + " · " + (before.Value ?? before.Checked.ToString()) + "→" + (before.Target ?? "toggle") + ")"); }
                if (after.Hash != snapshot.Hash) res.Reacted++;
                if (ok) res.Healthy++;
            }
            if (dispatches >= MaxDispatchPerModal) res.Defects.Add("[CAP] " + tag + " — 디스패치 상한 " + MaxDispatchPerModal.ToString(CultureInfo.InvariantCulture) + " 도달(무한 생성 컨트롤 의심)");

            foreach (var (key, task) in pending)
            {
                await Task.WhenAny(task, Task.Delay(DispatchWait));
                if (task.IsFaulted) res.Defects.Add("[THROW-ASYNC] " + key + ": " + task.Exception!.GetBaseException().Message);
            }
        }
        finally
        {
            try { await r.Dispatcher.InvokeAsync(() => r.Dispose()); }
            catch (Exception ex) { res.Defects.Add("[DISPOSE] " + tag + ": " + ex.GetType().Name + " " + ex.Message); }
            if (r.TakeErrors() is { } late) res.Defects.Add("[LATE] " + tag + ": " + late.GetType().Name + " " + late.Message);
        }
        return res;
    }

    private static ParameterView RootParameters(Target t)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var name in t.PassedParams)
        {
            var p = t.Component.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (p?.GetCustomAttribute<ParameterAttribute>() is null) continue;
            var pt = p.PropertyType;
            if (pt == typeof(EventCallback)) dict[name] = EventCallback.Factory.Create(new object(), () => { });
            else if (pt.IsGenericType && pt.GetGenericTypeDefinition() == typeof(EventCallback<>)) dict[name] = Activator.CreateInstance(pt, null, (Action)(() => { }));
            else if (pt.IsClass && pt != typeof(string) && pt.GetConstructor(Type.EmptyTypes) is not null) dict[name] = Activator.CreateInstance(pt);
        }
        return ParameterView.FromDictionary(dict);
    }

    /// <summary>바인딩 고정 판정 — 체크박스 checked · 슬라이더 value · aria-pressed=false 토글이 발화 후에도 그대로면 껍데기.</summary>
    private static bool Stuck(Ctl b, Ctl? a)
    {
        if (a is null) return false;   // 컨트롤 자체가 사라짐 = 상태 전이 발생
        return b.Kind switch
        {
            "switch" => b.Checked == a.Checked,
            "slider" => b.Value == a.Value && b.Value != b.Target,
            "button" => b.Pressed == "false" && a.Pressed == "false",
            _ => false,
        };
    }

    private static EventArgs ArgsFor(Ctl c)
    {
        EventArgs args = c.Kind switch
        {
            "switch" when c.Event != "onclick" => new ChangeEventArgs { Value = !c.Checked },
            "slider" or "select" or "input" => new ChangeEventArgs { Value = c.Target },
            _ => new Microsoft.AspNetCore.Components.Web.MouseEventArgs { Button = 0, Detail = 1 },
        };
        var want = c.ArgType;
        if (want is null || want.IsInstanceOfType(args)) return args;
        if (want == typeof(ChangeEventArgs)) return new ChangeEventArgs { Value = c.Target };
        return want.GetConstructor(Type.EmptyTypes) is not null ? (EventArgs)Activator.CreateInstance(want)! : EventArgs.Empty;
    }

    // ══ BCL 렌더러 + 렌더 트리 스캐너 ══

    private sealed class Ctl
    {
        public string Key = "", Kind = "", Event = "", Label = "";
        public ulong HandlerId, FollowId;
        public bool Disabled, Checked, EmptyHandler;
        public string? Value, Target, Pressed;
        public Type? ArgType;
    }

    private sealed class Snapshot
    {
        public readonly List<Ctl> Controls = new();
        public long Hash;
        public Ctl? Find(string key) { foreach (var c in Controls) if (c.Key == key) return c; return null; }
    }

    private sealed class CrawlRenderer : Renderer
    {
        private readonly List<Exception> _errors = new();
        public CrawlRenderer(IServiceProvider sp) : base(sp, NullLoggerFactory.Instance) { }
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override void HandleException(Exception exception) { lock (_errors) _errors.Add(exception); }
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;

        public int Mount(Type t) => AssignRootComponentId(InstantiateComponent(t));
        public Task RenderAsync(int id, ParameterView p) => RenderRootComponentAsync(id, p);

        public Exception? TakeErrors()
        {
            lock (_errors)
            {
                if (_errors.Count == 0) return null;
                var e = _errors.Count == 1 ? _errors[0] : new AggregateException(_errors);
                _errors.Clear();
                return e;
            }
        }

        public Snapshot Scan(int rootId)
        {
            var s = new Snapshot();
            var occ = new Dictionary<string, int>(StringComparer.Ordinal);
            Walk(rootId, s, occ);
            return s;
        }

        private void Walk(int componentId, Snapshot s, Dictionary<string, int> occ)
        {
            var range = GetCurrentRenderTreeFrames(componentId);
            var f = range.Array;
            for (var i = 0; i < range.Count; i++)
            {
                ref var fr = ref f[i];
                switch (fr.FrameType)
                {
                    case RenderTreeFrameType.Component:
                        Walk(fr.ComponentId, s, occ);
                        i += fr.ComponentSubtreeLength - 1;
                        break;
                    case RenderTreeFrameType.Text: s.Hash = Mix(s.Hash, fr.TextContent); break;
                    case RenderTreeFrameType.Markup: s.Hash = Mix(s.Hash, fr.MarkupContent); break;
                    case RenderTreeFrameType.Element:
                        s.Hash = Mix(s.Hash, fr.ElementName);
                        ReadElement(f, i, range.Count, s, occ);
                        break;
                    case RenderTreeFrameType.Attribute:
                        if (fr.AttributeEventHandlerId == 0 && fr.AttributeValue is string or bool or int or double or float or long or decimal)
                            s.Hash = Mix(Mix(s.Hash, fr.AttributeName), Convert.ToString(fr.AttributeValue, CultureInfo.InvariantCulture));
                        break;
                }
            }
        }

        private static void ReadElement(RenderTreeFrame[] f, int i, int count, Snapshot s, Dictionary<string, int> occ)
        {
            var el = f[i].ElementName;
            string? type = null, aria = null, title = null, value = null, min = null, max = null, pressed = null, role = null;
            bool disabled = false, chk = false;
            ulong click = 0, change = 0, input = 0;
            object? hClick = null, hChange = null, hInput = null;
            var j = i + 1;
            for (; j < count && f[j].FrameType == RenderTreeFrameType.Attribute; j++)
            {
                var a = f[j];
                var v = a.AttributeValue;
                switch (a.AttributeName)
                {
                    case "onclick" when a.AttributeEventHandlerId != 0: click = a.AttributeEventHandlerId; hClick = v; break;
                    case "onchange" when a.AttributeEventHandlerId != 0: change = a.AttributeEventHandlerId; hChange = v; break;
                    case "oninput" when a.AttributeEventHandlerId != 0: input = a.AttributeEventHandlerId; hInput = v; break;
                    case "type": type = v as string; break;
                    case "role": role = v as string; break;
                    case "aria-label": aria = v as string; break;
                    case "title": title = v as string; break;
                    case "value": value = Convert.ToString(v, CultureInfo.InvariantCulture); break;
                    case "min": min = Convert.ToString(v, CultureInfo.InvariantCulture); break;
                    case "max": max = Convert.ToString(v, CultureInfo.InvariantCulture); break;
                    case "aria-pressed": pressed = Convert.ToString(v, CultureInfo.InvariantCulture); break;
                    case "checked": chk = v is not false && v is not null && !"false".Equals(v as string, StringComparison.Ordinal); break;
                    case "disabled": disabled = v is not false && v is not null; break;
                }
            }
            if (click == 0 && change == 0 && input == 0) return;

            var c = new Ctl { Disabled = disabled, Checked = chk, Value = value, Pressed = pressed };
            var isInput = el is "input" or "textarea";
            if (el == "input" && type == "checkbox" || role == "switch" && el != "button") c.Kind = "switch";
            else if (el == "input" && type == "range") c.Kind = "slider";
            else if (el == "select") c.Kind = "select";
            else if (isInput) c.Kind = "input";
            else c.Kind = "button";

            (c.Event, c.HandlerId, var h) = c.Kind == "button" || c.Kind == "switch" && change == 0 && input == 0
                ? (click != 0 ? ("onclick", click, hClick) : change != 0 ? ("onchange", change, hChange) : ("oninput", input, hInput))
                : (input != 0 ? ("oninput", input, hInput) : change != 0 ? ("onchange", change, hChange) : ("onclick", click, hClick));
            if (c.Event == "oninput" && change != 0) c.FollowId = change;   // 브라우저 실순서 input → change(드래그 미리보기 + 놓을 때 커밋)
            if (c.Kind == "input" && type is "file" or "hidden") { c.Disabled = true; }
            var del = DelegateOf(h);
            c.EmptyHandler = del is not null && IsEmptyBody(del);
            c.ArgType = ArgTypeOf(h, del);

            var end = i + f[i].ElementSubtreeLength;
            c.Target = c.Kind switch
            {
                "slider" => SliderTarget(value, min, max),
                "select" => SelectTarget(f, j, end, value),
                "input" => type switch
                {
                    "number" => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? (n + 1).ToString(CultureInfo.InvariantCulture) : "1",
                    "date" => "2026-01-01", "time" => "12:00", "color" => "#ff0055",
                    _ => "crawl",
                },
                _ => null,
            };
            c.Label = aria ?? title ?? FirstText(f, j, end) ?? "";
            if (c.Label.Length > 28) c.Label = c.Label[..28];
            var baseKey = c.Kind + ":" + el + (type is null ? "" : "[" + type + "]") + ":" + c.Label;
            occ.TryGetValue(baseKey, out var k);
            occ[baseKey] = k + 1;
            c.Key = baseKey + "#" + k.ToString(CultureInfo.InvariantCulture);
            s.Controls.Add(c);
        }

        private static string SliderTarget(string? value, string? min, string? max)
        {
            var inv = CultureInfo.InvariantCulture;
            var lo = double.TryParse(min, NumberStyles.Float, inv, out var a) ? a : 0;
            var hi = double.TryParse(max, NumberStyles.Float, inv, out var b) ? b : 100;
            var cur = double.TryParse(value, NumberStyles.Float, inv, out var v) ? v : lo;
            return (cur < hi ? hi : lo).ToString(inv);
        }

        private static string? SelectTarget(RenderTreeFrame[] f, int from, int end, string? cur)
        {
            string? first = null;
            for (var k = from; k < end; k++)
                if (f[k].FrameType == RenderTreeFrameType.Attribute && f[k].AttributeName == "value" && k > 0)
                {
                    var v = Convert.ToString(f[k].AttributeValue, CultureInfo.InvariantCulture);
                    first ??= v;
                    if (v != cur) return v;
                }
            return first;
        }

        private static string? FirstText(RenderTreeFrame[] f, int from, int end)
        {
            for (var k = from; k < end; k++)
            {
                var t = f[k].FrameType switch
                {
                    RenderTreeFrameType.Text => f[k].TextContent,
                    RenderTreeFrameType.Markup => Regex.Replace(f[k].MarkupContent, "<[^>]+>", " "),
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(t)) return t.Trim();
            }
            return null;
        }

        private static long Mix(long h, string? s)
        {
            unchecked
            {
                h = h * 31 + 17;
                if (s is null) return h;
                foreach (var ch in s) h = h * 1099511628211L ^ ch;
                return h;
            }
        }
    }

    private static Delegate? DelegateOf(object? v) => v switch
    {
        null => null,
        Delegate d => d,
        _ => v.GetType().GetField("Delegate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(v) as Delegate,
    };

    private static Type? ArgTypeOf(object? h, Delegate? d)
    {
        var ht = h?.GetType();
        if (ht is { IsGenericType: true } && ht.GetGenericTypeDefinition() == typeof(EventCallback<>)) return ht.GetGenericArguments()[0];
        var ps = d?.Method.GetParameters();
        return ps is { Length: 1 } && typeof(EventArgs).IsAssignableFrom(ps[0].ParameterType) ? ps[0].ParameterType : null;
    }

    /// <summary>IL 본문이 nop*·ret 또는 <c>Task.CompletedTask</c> 반환뿐이면 가짜 핸들러(<c>() =&gt; { }</c>).</summary>
    private static bool IsEmptyBody(Delegate d)
    {
        foreach (var x in d.GetInvocationList())
        {
            var il = x.Method.GetMethodBody()?.GetILAsByteArray();
            if (il is null) return false;
            var k = 0;
            while (k < il.Length && il[k] == 0x00) k++;
            if (k == il.Length - 1 && il[k] == 0x2A) continue;
            if (il.Length - k == 6 && il[k] == 0x28 && il[k + 5] == 0x2A)
            {
                MethodBase? m = null;
                try { m = x.Method.Module.ResolveMethod(BitConverter.ToInt32(il, k + 1)); } catch { }
                if (m?.DeclaringType == typeof(Task) && m.Name == "get_CompletedTask") continue;
            }
            return false;
        }
        return true;
    }

    // ══ 웹뷰 스코프 대체(IJSRuntime · NavigationManager) — 그 외 전 의존성은 프로덕션 DI 그대로 ══

    private sealed class CrawlProvider : IServiceProvider
    {
        private readonly IServiceProvider _inner;
        private readonly StubNav _nav = new();
        public CrawlProvider(IServiceProvider inner) => _inner = inner;

        public object? GetService(Type t)
        {
            if (t == typeof(IServiceProvider)) return this;
            if (t == typeof(IJSRuntime)) return StubJs.Instance;
            if (t == typeof(NavigationManager)) return _nav;
            return _inner.GetService(t);
        }
    }

    private sealed class HeadlessDialogs : PromptFlowStudio.Services.Platform.IFilePickerService, PromptFlowStudio.Services.Platform.IVideoExportService, PromptFlowStudio.Services.Platform.IClipboardService
    {
        public string? PickFile(string title, string filter, string? initialDir = null) => null;
        public Task<string?> ExportAsync(string sourcePath, string suggestedFileName) => Task.FromResult<string?>(null);
        public bool SetText(string? text) => !string.IsNullOrEmpty(text);
    }

    private sealed class StubNav : NavigationManager
    {
        public StubNav() => Initialize("https://0.0.0.0/", "https://0.0.0.0/");
        protected override void NavigateToCore(string uri, NavigationOptions options) => Uri = ToAbsoluteUri(uri).ToString();
    }

    private sealed class StubJs : IJSRuntime, IJSObjectReference
    {
        public static readonly StubJs Instance = new();
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => Value<TValue>();
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => Value<TValue>();
        public ValueTask DisposeAsync() => default;
        private static ValueTask<TValue> Value<TValue>() => typeof(TValue) == typeof(IJSObjectReference) ? new((TValue)(object)Instance) : default;
    }
}
