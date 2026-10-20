namespace PromptFlowStudio.Services.CloudVerification;

public enum UiControlKind : byte { StudioButton, Toggle, Switch, Slider, Command }
public enum UiStepOutcome : byte { Pending, Pass, BindingMismatch, Exception, Timeout, Quarantined }

/// <summary>
/// 가상 조작 대상: Read = 바인딩 상태(토글 0/1 · 슬라이더 값 · 스튜디오 실행 여부) · Write = 가상 조작(상태 설정/Start·Stop).
/// Command는 Invoke 후 Verify(선택)로 무결성 판정.
/// </summary>
public sealed class UiControlTarget
{
    public required string Group { get; init; }
    public required string Name { get; init; }
    public UiControlKind Kind { get; init; }
    public required Func<double> Read { get; init; }
    public required Action<double> Write { get; init; }
    public double Min { get; init; }
    public double Max { get; init; } = 1d;
    public double Step { get; init; }
    public Action? Invoke { get; init; }
    public Func<bool>? Verify { get; init; }
    // 상태(드라이버/가드 소유)
    public UiStepOutcome Outcome;
    public double LastMs;
    public int Runs, Failures;
    public string? LastNote;
}

/// <summary>
/// [PromptFlow Studio v16.92.0] 🖱 전체 UI 메뉴 &amp; 스위치 가상 인터랙티브 자동 순회 드라이버.
///   ▸ 동적: 대상 순차 조작(Step 1회 = 대상 1개) — 토글/스위치/스튜디오 버튼 = 반전 → 읽기 검증 → 원복 → 읽기 검증,
///     슬라이더 = Min · 중간(스텝 스냅) · Max 쓰기/읽기 왕복(상대 오차 1e-3) → 원복, 명령 = Invoke → Verify. 모든 조작은 <see cref="RealtimeUiExceptionSelfHealingGuard"/> 래핑(실패 시 롤백).
///   ▸ 정적: Razor 마크업 인벤토리(버튼 · @onclick · range 슬라이더 · checkbox · @bind · 헤더 topbar-help · 스토리보드 sb-ptool).
///   DOM 직접 클릭이 아닌 바인딩 계층(서비스 상태) 가상 구동 — 렌더 예외는 <see cref="SelfHealingRenderBoundary"/>가 별도 포착.
/// </summary>
public sealed class FullUiInteractiveMenuAutomatedDriver
{
    public const int MaxTargets = RealtimeUiExceptionSelfHealingGuard.MaxTargets;
    private readonly object _gate = new();
    private readonly UiControlTarget[] _t = new UiControlTarget[MaxTargets];
    private readonly RealtimeUiExceptionSelfHealingGuard _guard;
    private readonly Func<UiControlTarget, bool> _drive;
    private int _n, _cursor;

    public FullUiInteractiveMenuAutomatedDriver(RealtimeUiExceptionSelfHealingGuard guard)
    {
        _guard = guard;
        _drive = Drive;
    }

    public RealtimeUiExceptionSelfHealingGuard Guard => _guard;
    public int Count => _n;
    public UiControlTarget Target(int i) => _t[i];
    public int Cursor => _cursor;
    public long Passes { get; private set; }
    public long Failures { get; private set; }
    public long Steps { get; private set; }
    public int Cycles { get; private set; }
    public int CycleFailures { get; private set; }
    public double LastStepMs { get; private set; }

    // 마크업 인벤토리
    public int RazorFiles { get; private set; }
    public int MarkupButtons { get; private set; }
    public int MarkupHandlers { get; private set; }
    public int MarkupSliders { get; private set; }
    public int MarkupCheckboxes { get; private set; }
    public int MarkupBinds { get; private set; }
    public int HeaderButtons { get; private set; }
    public int StoryboardTools { get; private set; }
    /// <summary>v17.50.1 — 사이드바 6대 카테고리 스튜디오 진입점 수(StudioCatalog · 헤더 topbar-help 이관분 포함).</summary>
    public int NavEntries { get; private set; }
    public int Modals { get; private set; }

    public int Register(UiControlTarget t)
    {
        lock (_gate)
        {
            if (_n >= MaxTargets) return -1;
            for (var i = 0; i < _n; i++) if (_t[i].Group == t.Group && _t[i].Name == t.Name) return i; // alreadyRegistered
            _t[_n] = t;
            return _n++;
        }
    }

    public int CountKind(UiControlKind k) { var c = 0; for (var i = 0; i < _n; i++) if (_t[i].Kind == k) c++; return c; }

    public void Rewind()
    {
        lock (_gate)
        {
            _cursor = 0; CycleFailures = 0;
            for (var i = 0; i < _n; i++) { _t[i].Outcome = UiStepOutcome.Pending; _t[i].LastNote = null; }
        }
    }

    /// <summary>1스텝(대상 1개 가상 조작) → 순회 완료 시 true.</summary>
    public bool Step()
    {
        UiControlTarget t; int i;
        lock (_gate)
        {
            if (_n == 0) return true;
            i = _cursor; t = _t[i];
        }
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var o = _guard.Execute(i, t, _drive);
        LastStepMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        lock (_gate)
        {
            t.Outcome = o; t.Runs++; Steps++;
            if (o == UiStepOutcome.Pass) Passes++;
            else if (o != UiStepOutcome.Quarantined) { Failures++; t.Failures++; CycleFailures++; }
            _cursor++;
            if (_cursor < _n) return false;
            _cursor = 0; Cycles++;
            return true;
        }
    }

    /// <summary>현재 대상 생략(조작 없이 커서 전진 · 순회 경계 시 사이클 증가).</summary>
    public void SkipCursor()
    {
        lock (_gate)
        {
            if (_n == 0) return;
            if (++_cursor >= _n) { _cursor = 0; Cycles++; }
        }
    }

    /// <summary>전수 1회 순회(동기) → 실패 수.</summary>
    public int RunCycle()
    {
        Rewind();
        while (!Step()) { }
        return CycleFailures;
    }

    private static bool Drive(UiControlTarget t)
    {
        switch (t.Kind)
        {
            case UiControlKind.Command:
                t.Invoke?.Invoke();
                if (t.Verify is { } v && !v()) { t.LastNote = "명령 검증 실패"; return false; }
                return true;
            case UiControlKind.Slider:
            {
                var orig = t.Read();
                var span = t.Max - t.Min;
                for (var k = 0; k <= 2; k++)
                {
                    var target = t.Min + span * 0.5 * k;
                    if (t.Step > 0d) target = t.Min + Math.Round((target - t.Min) / t.Step) * t.Step;
                    t.Write(target);
                    var got = t.Read();
                    if (Math.Abs(got - target) > 1e-3 * Math.Max(1d, Math.Abs(target)))
                    {
                        t.LastNote = "슬라이더 왕복 불일치 " + target.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " → " + got.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                        return false;
                    }
                }
                t.Write(orig);
                return Math.Abs(t.Read() - orig) <= 1e-3 * Math.Max(1d, Math.Abs(orig)) || Note(t, "원복 불일치");
            }
            default:
            {
                var orig = t.Read() >= 0.5 ? 1d : 0d;
                var flip = 1d - orig;
                t.Write(flip);
                if ((t.Read() >= 0.5 ? 1d : 0d) != flip) return Note(t, flip > 0.5 ? "켜기 후 상태 미반영" : "끄기 후 상태 미반영");
                t.Write(orig);
                if ((t.Read() >= 0.5 ? 1d : 0d) != orig) return Note(t, "원복 후 상태 미반영");
                return true;
            }
        }
    }

    private static bool Note(UiControlTarget t, string note) { t.LastNote = note; return false; }

    public void ResetInventory()
    {
        RazorFiles = MarkupButtons = MarkupHandlers = MarkupSliders = MarkupCheckboxes = MarkupBinds = HeaderButtons = StoryboardTools = Modals = 0;
        NavEntries = PromptFlowStudio.Services.Ui.StudioCatalog.Entries.Length;
    }

    /// <summary>Razor 마크업 인벤토리 누적(.razor만).</summary>
    public void InventoryMarkup(string path, string raw)
    {
        if (!path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)) return;
        var s = raw.AsSpan();
        lock (_gate)
        {
            RazorFiles++;
            MarkupButtons += Occurrences(s, "<button");
            MarkupHandlers += Occurrences(s, "@onclick=");
            MarkupSliders += Occurrences(s, "type=\"range\"");
            MarkupCheckboxes += Occurrences(s, "type=\"checkbox\"");
            MarkupBinds += Occurrences(s, "@bind");
            HeaderButtons += Occurrences(s, "class=\"topbar-help\"");
            StoryboardTools += Occurrences(s, "class=\"sb-ptool\"");
            if (path.EndsWith("Modal.razor", StringComparison.OrdinalIgnoreCase)) Modals++;
        }
    }

    public static int Occurrences(ReadOnlySpan<char> s, string token)
    {
        var c = 0;
        for (var i = s.IndexOf(token, StringComparison.Ordinal); i >= 0; )
        {
            c++;
            var next = s[(i + token.Length)..].IndexOf(token, StringComparison.Ordinal);
            i = next < 0 ? -1 : i + token.Length + next;
        }
        return c;
    }

    // ── 등록 헬퍼 ──
    public int AddToggle(string group, string name, Func<bool> get, Action<bool> set, UiControlKind kind = UiControlKind.Toggle) =>
        Register(new UiControlTarget { Group = group, Name = name, Kind = kind, Read = () => get() ? 1d : 0d, Write = v => set(v >= 0.5) });

    public int AddSlider(string group, string name, double min, double max, Func<double> get, Action<double> set, double step = 0d) =>
        Register(new UiControlTarget { Group = group, Name = name, Kind = UiControlKind.Slider, Min = min, Max = max, Step = step, Read = get, Write = set });

    public int AddStudio(string group, string name, Func<bool> isRunning, Action start, Action stop) =>
        Register(new UiControlTarget { Group = group, Name = name, Kind = UiControlKind.StudioButton, Read = () => isRunning() ? 1d : 0d, Write = v => { if (v >= 0.5) start(); else stop(); } });

    public int AddCommand(string group, string name, Action invoke, Func<bool>? verify = null) =>
        Register(new UiControlTarget { Group = group, Name = name, Kind = UiControlKind.Command, Read = static () => 0d, Write = static _ => { }, Invoke = invoke, Verify = verify });
}
