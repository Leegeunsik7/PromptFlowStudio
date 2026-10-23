namespace PromptFlowStudio.Services.Ui;

// =============================================================================
//  전역 커맨드 팔레트 — 명령 등록 · 검색 · 최근 실행
//
//  ⚠ 명령을 여기 등록만 해 두면 어디서든 Ctrl+K 로 실행된다. 화면마다
//    버튼을 만들 필요가 없다는 것이 이 구조의 핵심이다.
//
//  ⚠ 위험한 명령을 여기 넣지 않는다.
//    E-Stop '발동' 은 팔레트에 없다. 타자 몇 글자와 Enter 로 전 로봇이
//    서면 오조작 한 번이 현장 사고가 된다. 팔레트가 하는 일은 **무장(Arm)
//    까지**이고, 실제 정지는 800ms 길게 누르기라는 물리적 확인을 거친다.
//    편의는 안전 절차를 건너뛰는 이유가 될 수 없다.
// =============================================================================

/// <summary>팔레트에 등록되는 명령 1개.</summary>
public sealed record PaletteCommand(
    string Id,
    string Group,
    string Icon,
    string Title,
    string? Hint,
    string[] Keywords,
    Func<Task> Run)
{
    /// <summary>실행 가능 여부. false 면 목록에 흐리게 나오고 Enter 로 실행되지 않는다.</summary>
    public Func<bool>? CanRun { get; init; }

    /// <summary>⚠ 되돌리기 어려운 명령. 화면이 별도로 표시한다.</summary>
    public bool IsSensitive { get; init; }

    internal string Haystack { get; } =
        string.Join(' ', new[] { Title, Group, Hint ?? "" }.Concat(Keywords));
}

public sealed class CommandPaletteService
{
    private readonly List<PaletteCommand> _commands = new();
    private readonly UserPreferenceService _prefs;

    public CommandPaletteService(UserPreferenceService prefs) => _prefs = prefs;

    public bool IsOpen { get; private set; }
    public event Action? OnChange;

    public IReadOnlyList<PaletteCommand> All => _commands;

    // ── 수명주기 ─────────────────────────────────────────────────────────────
    public void Open()
    {
        if (IsOpen) return;
        IsOpen = true;
        OnChange?.Invoke();
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        OnChange?.Invoke();
    }

    public void Toggle() { IsOpen = !IsOpen; OnChange?.Invoke(); }

    /// <summary>v17.89.0 Ctrl+K → 레이아웃의 🧭 인텐트 허브. 구독자(MainLayout)가 없으면(모바일 등) 기존대로 팔레트를 토글한다.</summary>
    public event Action? IntentHubRequested;

    public void RequestIntentHub()
    {
        if (IntentHubRequested is not { } h) { Toggle(); return; }
        Close();
        try { h(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Palette.IntentHubRequested", ex); }
    }

    // ── 등록 ─────────────────────────────────────────────────────────────────
    /// <summary>
    /// 명령을 등록한다. 같은 Id 면 덮어쓴다.
    /// ⚠ 덮어쓰기가 필요한 이유 — 페이지를 오갈 때 같은 명령이 다시 등록되는데,
    ///   그때마다 목록이 불어나면 팔레트에 같은 항목이 여러 번 뜬다.
    /// </summary>
    public void Register(PaletteCommand cmd)
    {
        var i = _commands.FindIndex(c => c.Id == cmd.Id);
        if (i >= 0) _commands[i] = cmd; else _commands.Add(cmd);
    }

    public void RegisterRange(IEnumerable<PaletteCommand> cmds)
    {
        foreach (var c in cmds) Register(c);
    }

    public void Unregister(string id) => _commands.RemoveAll(c => c.Id == id);

    /// <summary>페이지가 떠날 때 자기 명령만 걷어 간다.</summary>
    public void UnregisterPrefix(string idPrefix)
        => _commands.RemoveAll(c => c.Id.StartsWith(idPrefix, StringComparison.Ordinal));

    // ── 검색 ─────────────────────────────────────────────────────────────────
    /// <summary>
    /// 검색어로 명령을 고른다. 빈 검색어면 최근 실행 → 나머지 순.
    ///
    /// <para>
    /// ⚠ 실행 불가(CanRun=false) 명령도 목록에서 빼지 않는다. 사라지면
    ///   사용자는 그런 기능이 없다고 생각한다. 흐리게 두고 왜 안 되는지
    ///   화면이 보여 준다.
    /// </para>
    /// </summary>
    public IReadOnlyList<PaletteCommand> Search(string? query, int max = 40)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            var recent = _prefs.GetRecent(UserPreferenceService.Keys.RecentCommands);
            var byId = _commands.ToDictionary(c => c.Id, StringComparer.Ordinal);

            var head = recent.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            var tail = _commands.Where(c => !head.Contains(c));
            return head.Concat(tail).Take(max).ToList();
        }

        return _commands
            .Select(c => (Cmd: c, Score: KoreanSearch.SmartScore(c.Haystack, query)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Cmd.Title, StringComparer.Ordinal)
            .Take(max)
            .Select(x => x.Cmd)
            .ToList();
    }

    /// <summary>빈 검색어 상태에서 '최근 실행' 으로 표시할 개수.</summary>
    public int RecentCount(IReadOnlyList<PaletteCommand> shown)
    {
        var recent = _prefs.GetRecent(UserPreferenceService.Keys.RecentCommands);
        var n = 0;
        foreach (var c in shown)
        {
            if (!recent.Contains(c.Id)) break;
            n++;
        }
        return n;
    }

    // ── 실행 ─────────────────────────────────────────────────────────────────
    public async Task RunAsync(PaletteCommand cmd)
    {
        if (cmd.CanRun is not null && !cmd.CanRun()) return;

        _prefs.PushRecent(UserPreferenceService.Keys.RecentCommands, cmd.Id);
        Close();

        // ⚠ 명령 하나가 던져도 팔레트가 죽으면 안 된다. 다음 Ctrl+K 가
        //   안 먹으면 사용자는 앱 전체가 멈춘 줄 안다.
        try { await cmd.Run(); }
        catch (Exception) { /* 호출부가 자기 오류를 화면에 표시할 책임 */ }
    }
}
