using System.Globalization;
using PromptFlowStudio.Services.Screenplay;
using PromptFlowStudio.Services.Storyboard;

namespace PromptFlowStudio.Services.Cinema;

public enum UniverseEventKind : byte { Day, Appear, Speak, Die, Injure, Heal, Revive, Gain, Lose, Use, Outfit, Learn, Mention, Strenuous }

public enum PlotHoleKind : byte { DeadCharacterActs, DoubleDeath, ItemNotOwned, OutfitDiscontinuity, Teleport, TimelineRegression, KnowledgeLeak, InjuryIgnored }

public enum PlotSeverity : byte { Minor, Major, Critical }

/// <summary>컴파일된 유니버스 이벤트(값형 · 인턴 정수 ID) — Arg = 장소/물건/의상/사실 ID 또는 일차.</summary>
public readonly record struct UniverseEvent(int Episode, int Scene, int Character, UniverseEventKind Kind, int Arg, int Day, int Location, bool Flashback, bool Continuous);

/// <summary>개연성 모순 1건(값형) — Ref = 근거 이벤트(사망/부상/이전 장소 등) 인덱스.</summary>
public readonly record struct PlotHole(PlotHoleKind Kind, PlotSeverity Severity, int Event, int Ref);

public sealed class UniverseEpisode
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Title { get; set; } = "";
    public string Branch { get; set; } = "본편";
    public int Order { get; set; }
    public ScreenplayDoc Script { get; set; } = new();
    /// <summary>마스터링용 클립(스토리보드 컷 로컬 경로) · 없으면 대본 씬 길이 기준 타임라인.</summary>
    public List<string> Clips { get; set; } = new();
    public List<double> ClipSeconds { get; set; } = new();
}

public sealed record TimelineNode(int Episode, string Title, string Branch, int Lane, int Scenes, int FirstDay, int LastDay, int Deaths, int Issues);

/// <summary>
/// [PromptFlow Studio v16.41.0] 🧭 시네마틱 유니버스 타임라인 &amp; 설정 개연성 검증기.
///   ▸ 에피소드/스핀오프(브랜치) 대본 → 이벤트 컴파일(인물·장소·물건·의상·사실 문자열 인턴 → 정수 ID): 대사(발화) · 지문 등장 · 씬 헤딩 장소 · 회상/연속 플래그 ·
///     명시 주석 [[DAY n]] [[DIE 이름]] [[INJURE/HEAL/REVIVE 이름]] [[GAIN/LOSE/USE 이름: 물건]] [[OUTFIT 이름: 의상]] [[LEARN/MENTION 이름: 사실]] [[AT 이름: 장소]] + 한국어/영어 지문 휴리스틱(사망·부상·격투).
///   ▸ 검증(무할당 · 사전 할당 상태 배열/결과 버퍼 재사용): 사망 후 행동 · 중복 사망 · 미소지 물건 사용/분실 · 같은 날 의상 불연속 · 연속 씬 순간이동 · 일차 역행 · 학습 전 지식 언급 · 부상 직후 격한 행동.
///   ▸ 회상(FLASHBACK/회상) 씬은 생사·일차 검사 제외 · 브랜치(스핀오프)는 본편 기준 상태를 분기 복제해 독립 검증.
/// </summary>
public sealed class CinematicUniverseTimelineEngine
{
    public const int MaxIssues = 1024, InjuryWindowDays = 1;

    private readonly List<UniverseEpisode> _episodes = new();
    private readonly Dictionary<string, int> _charIds = new(StringComparer.Ordinal), _symIds = new(StringComparer.Ordinal);
    private readonly List<string> _chars = new(), _syms = new();
    private readonly object _gate = new();
    private UniverseEvent[] _events = Array.Empty<UniverseEvent>();
    private int _eventCount;
    private readonly PlotHole[] _issues = new PlotHole[MaxIssues];
    private int _issueCount;
    // 검증 상태(캐릭터 · 심볼 수 변경 시에만 재할당)
    private int[] _deathEvt = Array.Empty<int>(), _injuryEvt = Array.Empty<int>(), _injuryDay = Array.Empty<int>(), _loc = Array.Empty<int>(), _locEvt = Array.Empty<int>(),
                  _outfit = Array.Empty<int>(), _outfitDay = Array.Empty<int>(), _outfitEvt = Array.Empty<int>(), _lastDay = Array.Empty<int>(), _lastDayEvt = Array.Empty<int>(), _scene = Array.Empty<int>();
    private bool[] _owns = Array.Empty<bool>(), _knows = Array.Empty<bool>();
    private int[] _branchOf = Array.Empty<int>();
    private int _symCap, _charCap, _branchCount = 1;

    public event Action? OnChange;

    public IReadOnlyList<UniverseEpisode> Episodes { get { lock (_gate) return _episodes.ToArray(); } }
    public int EpisodeCount { get { lock (_gate) return _episodes.Count; } }
    public int EventCount => _eventCount;
    public int IssueCount => _issueCount;
    public int CharacterCount => _chars.Count;
    public long Validations { get; private set; }
    public string? LastAction { get; private set; }
    public ReadOnlySpan<UniverseEvent> Events => _events.AsSpan(0, _eventCount);
    public ReadOnlySpan<PlotHole> Issues => _issues.AsSpan(0, _issueCount);
    public string CharacterName(int id) => (uint)id < (uint)_chars.Count ? _chars[id] : "?";
    public string Symbol(int id) => (uint)id < (uint)_syms.Count ? _syms[id] : "?";

    // ══════════════════════ 에피소드 관리 ══════════════════════

    public UniverseEpisode AddEpisode(string title, ScreenplayDoc script, string branch = "본편")
    {
        var e = new UniverseEpisode { Title = string.IsNullOrWhiteSpace(title) ? script.Title : title.Trim(), Script = script, Branch = string.IsNullOrWhiteSpace(branch) ? "본편" : branch.Trim() };
        lock (_gate) { e.Order = _episodes.Count; _episodes.Add(e); }
        LastAction = "➕ 에피소드 · " + e.Title + " (" + e.Branch + ")";
        Raise();
        return e;
    }

    public UniverseEpisode AddFountain(string title, string fountain, string branch = "본편")
        => AddEpisode(title, SceneBeatSheetScriptGenerator.ParseFountain(fountain), branch);

    /// <summary>스토리보드 → 에피소드(컷 = 씬 · 프롬프트/보이스 텍스트를 지문/대사로).</summary>
    public UniverseEpisode AddStoryboard(StoryboardProject p, string branch = "본편")
    {
        var doc = new ScreenplayDoc { Title = p.Title, Source = "Storyboard" };
        foreach (var s in p.Scenes)
        {
            var sc = new ScriptScene { Number = doc.Scenes.Count + 1, Heading = "INT. 컷 " + (doc.Scenes.Count + 1).ToString(CultureInfo.InvariantCulture) + " - 연속" };
            sc.Elements.Add(new ScriptElement { Kind = ScriptElementKind.Action, Text = s.Prompt });
            if (!string.IsNullOrWhiteSpace(s.VoiceText)) sc.Elements.Add(new ScriptElement { Kind = ScriptElementKind.Action, Text = s.VoiceText });
            doc.Scenes.Add(sc);
        }
        var ep = AddEpisode(p.Title, doc, branch);
        foreach (var s in p.Scenes)
            if (!string.IsNullOrEmpty(s.LocalPath)) { ep.Clips.Add(s.LocalPath); ep.ClipSeconds.Add(5); }
        return ep;
    }

    public bool RemoveEpisode(string id)
    {
        lock (_gate)
        {
            var i = _episodes.FindIndex(e => e.Id == id);
            if (i < 0) return false;
            _episodes.RemoveAt(i);
            for (var k = 0; k < _episodes.Count; k++) _episodes[k].Order = k;
        }
        Raise();
        return true;
    }

    public void Clear() { lock (_gate) { _episodes.Clear(); _eventCount = 0; _issueCount = 0; } Raise(); }

    public void RegisterCharacters(IEnumerable<string> names) { lock (_gate) foreach (var n in names) CharId(n); }

    // ══════════════════════ 컴파일(할당 허용 · 편집 시 1회) ══════════════════════

    private int CharId(string name)
    {
        name = name.Trim();
        if (_charIds.TryGetValue(name, out var id)) return id;
        id = _chars.Count; _chars.Add(name); _charIds[name] = id;
        return id;
    }

    private int SymId(string s)
    {
        s = s.Trim();
        if (_symIds.TryGetValue(s, out var id)) return id;
        id = _syms.Count; _syms.Add(s); _symIds[s] = id;
        return id;
    }

    /// <summary>씬 헤딩 → 장소(INT./EXT. 제거 · " - " 앞).</summary>
    public static string LocationOf(string heading)
    {
        var h = heading.Trim();
        foreach (var p in new[] { "INT./EXT.", "INT/EXT.", "I/E.", "INT.", "EXT.", "EST.", "내부.", "외부." })
            if (h.StartsWith(p, StringComparison.OrdinalIgnoreCase)) { h = h[p.Length..].Trim(); break; }
        var dash = h.IndexOf(" - ", StringComparison.Ordinal);
        return dash > 0 ? h[..dash].Trim() : h;
    }

    public static bool IsFlashback(string heading) => heading.Contains("FLASHBACK", StringComparison.OrdinalIgnoreCase) || heading.Contains("회상", StringComparison.Ordinal);
    public static bool IsContinuous(string heading) => heading.Contains("CONTINUOUS", StringComparison.OrdinalIgnoreCase) || heading.Contains("연속", StringComparison.Ordinal);

    private static readonly string[] DeathWords = { "숨을 거둔", "사망한", "사망했", "죽는다", "죽었다", "dies", "is killed" };
    private static readonly string[] InjuryWords = { "부상을 입", "다친다", "다쳤다", "총에 맞", "is wounded", "gets injured" };
    private static readonly string[] StrenuousWords = { "전력으로 달", "질주한", "싸운다", "격투", "뛰어오른", "sprints", "fights" };

    /// <summary>전체 에피소드 → 이벤트 배열 재컴파일. 반환 = 이벤트 수.</summary>
    public int Compile()
    {
        var list = new List<UniverseEvent>(256);
        lock (_gate)
        {
            var branches = new Dictionary<string, int>(StringComparer.Ordinal) { ["본편"] = 0 };
            var day = 1;
            var mainDay = 1;
            for (var ep = 0; ep < _episodes.Count; ep++)
            {
                var e = _episodes[ep];
                if (!branches.ContainsKey(e.Branch)) branches[e.Branch] = branches.Count;
                if (branches[e.Branch] == 0) day = Math.Max(day, mainDay); else day = mainDay;
                for (var s = 0; s < e.Script.Scenes.Count; s++)
                {
                    var sc = e.Script.Scenes[s];
                    var fb = IsFlashback(sc.Heading);
                    var cont = IsContinuous(sc.Heading);
                    var loc = SymId(LocationOf(sc.Heading));
                    var present = new HashSet<int>();
                    foreach (var el in sc.Elements)
                    {
                        var text = el.Text ?? "";
                        // 명시 주석
                        var p = 0;
                        while ((p = text.IndexOf("[[", p, StringComparison.Ordinal)) >= 0)
                        {
                            var end = text.IndexOf("]]", p + 2, StringComparison.Ordinal);
                            if (end < 0) break;
                            var note = text[(p + 2)..end].Trim();
                            p = end + 2;
                            if (TryNote(note, ep, s, loc, fb, cont, ref day, list, present)) continue;
                        }
                        var plain = StripNotes(text);
                        if (el.Kind == ScriptElementKind.Dialogue && !string.IsNullOrWhiteSpace(el.Character))
                        {
                            var c = CharId(el.Character);
                            if (present.Add(c)) list.Add(new(ep, s, c, UniverseEventKind.Appear, loc, day, loc, fb, cont));
                            list.Add(new(ep, s, c, UniverseEventKind.Speak, 0, day, loc, fb, cont));
                        }
                        else if (el.Kind == ScriptElementKind.Action && plain.Length > 0)
                        {
                            for (var c = 0; c < _chars.Count; c++)
                            {
                                var at = plain.IndexOf(_chars[c], StringComparison.Ordinal);
                                if (at < 0) continue;
                                if (present.Add(c)) list.Add(new(ep, s, c, UniverseEventKind.Appear, loc, day, loc, fb, cont));
                                for (; at >= 0; at = plain.IndexOf(_chars[c], at + _chars[c].Length, StringComparison.Ordinal))
                                {
                                    var clause = Clause(plain, at);
                                    if (ContainsAny(clause, DeathWords)) list.Add(new(ep, s, c, UniverseEventKind.Die, 0, day, loc, fb, cont));
                                    else if (ContainsAny(clause, InjuryWords)) list.Add(new(ep, s, c, UniverseEventKind.Injure, 0, day, loc, fb, cont));
                                    else if (ContainsAny(clause, StrenuousWords)) list.Add(new(ep, s, c, UniverseEventKind.Strenuous, 0, day, loc, fb, cont));
                                }
                            }
                        }
                    }
                }
                if (branches[e.Branch] == 0) mainDay = Math.Max(mainDay, day);
            }
            _events = list.ToArray();
            _eventCount = _events.Length;
            EnsureState(branches.Count);
            var bo = _branchOf;
            for (var i = 0; i < _episodes.Count && i < bo.Length; i++) bo[i] = branches[_episodes[i].Branch];
        }
        LastAction = "🧩 컴파일 · 이벤트 " + _eventCount.ToString(CultureInfo.InvariantCulture) + " · 인물 " + _chars.Count.ToString(CultureInfo.InvariantCulture);
        return _eventCount;
    }

    private static string StripNotes(string t)
    {
        if (!t.Contains("[[", StringComparison.Ordinal)) return t;
        var sb = new System.Text.StringBuilder(t.Length);
        var i = 0;
        while (i < t.Length)
        {
            var p = t.IndexOf("[[", i, StringComparison.Ordinal);
            if (p < 0) { sb.Append(t, i, t.Length - i); break; }
            sb.Append(t, i, p - i);
            var e = t.IndexOf("]]", p, StringComparison.Ordinal);
            i = e < 0 ? t.Length : e + 2;
        }
        return sb.ToString();
    }

    private static string Clause(string text, int at)
    {
        var end = text.IndexOfAny(new[] { '.', '!', '?', '\n' }, at);
        return end < 0 ? text[at..] : text[at..end];
    }

    private static bool ContainsAny(string s, string[] words)
    {
        foreach (var w in words) if (s.Contains(w, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private bool TryNote(string note, int ep, int s, int loc, bool fb, bool cont, ref int day, List<UniverseEvent> list, HashSet<int> present)
    {
        var sp = note.IndexOf(' ');
        if (sp <= 0) return false;
        var verb = note[..sp].ToUpperInvariant();
        var rest = note[(sp + 1)..].Trim();
        if (verb == "DAY")
        {
            if (!int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d)) return false;
            day = d;
            list.Add(new(ep, s, -1, UniverseEventKind.Day, d, d, loc, fb, cont));
            return true;
        }
        var colon = rest.IndexOf(':');
        var who = colon >= 0 ? rest[..colon].Trim() : rest;
        var what = colon >= 0 ? rest[(colon + 1)..].Trim() : "";
        if (who.Length == 0) return false;
        var c = CharId(who);
        UniverseEventKind kind;
        switch (verb)
        {
            case "DIE": kind = UniverseEventKind.Die; break;
            case "INJURE": kind = UniverseEventKind.Injure; break;
            case "HEAL": kind = UniverseEventKind.Heal; break;
            case "REVIVE": kind = UniverseEventKind.Revive; break;
            case "GAIN": kind = UniverseEventKind.Gain; break;
            case "LOSE": kind = UniverseEventKind.Lose; break;
            case "USE": kind = UniverseEventKind.Use; break;
            case "OUTFIT": kind = UniverseEventKind.Outfit; break;
            case "LEARN": kind = UniverseEventKind.Learn; break;
            case "MENTION": kind = UniverseEventKind.Mention; break;
            case "AT":
                var l2 = SymId(what);
                present.Add(c);
                list.Add(new(ep, s, c, UniverseEventKind.Appear, l2, day, l2, fb, cont));
                return true;
            default: return false;
        }
        if (kind is UniverseEventKind.Gain or UniverseEventKind.Lose or UniverseEventKind.Use or UniverseEventKind.Outfit or UniverseEventKind.Learn or UniverseEventKind.Mention && what.Length == 0) return false;
        if (kind is not (UniverseEventKind.Die or UniverseEventKind.Revive or UniverseEventKind.Learn) && present.Add(c))
            list.Add(new(ep, s, c, UniverseEventKind.Appear, loc, day, loc, fb, cont));
        list.Add(new(ep, s, c, kind, what.Length > 0 ? SymId(what) : 0, day, loc, fb, cont));
        return true;
    }

    private void EnsureState(int branches)
    {
        _branchCount = Math.Max(1, branches);
        var chars = Math.Max(1, _chars.Count) * _branchCount;
        var syms = Math.Max(1, _syms.Count);
        if (_branchOf.Length < _episodes.Count) _branchOf = new int[Math.Max(16, _episodes.Count * 2)];
        if (_lastDay.Length < _branchCount) { _lastDay = new int[_branchCount + 2]; _lastDayEvt = new int[_branchCount + 2]; }
        if (chars <= _charCap && syms <= _symCap) return;
        _charCap = Math.Max(chars, _charCap * 2);
        _symCap = Math.Max(syms, _symCap * 2);
        _deathEvt = new int[_charCap]; _injuryEvt = new int[_charCap]; _injuryDay = new int[_charCap]; _loc = new int[_charCap]; _locEvt = new int[_charCap];
        _outfit = new int[_charCap]; _outfitDay = new int[_charCap]; _outfitEvt = new int[_charCap]; _scene = new int[_charCap];
        _owns = new bool[_charCap * _symCap]; _knows = new bool[_charCap * _symCap];
    }

    // ══════════════════════ 검증(무할당) ══════════════════════

    /// <summary>개연성 검증(사전 컴파일 필요 · 워밍업 후 무할당). 반환 = 모순 수.</summary>
    public int Validate()
    {
        lock (_gate)
        {
            var nChars = Math.Max(1, _chars.Count);
            var nSyms = Math.Max(1, _syms.Count);
            if (_charCap < nChars * _branchCount || _symCap < nSyms) EnsureState(_branchCount);
            var total = Math.Min(_charCap, nChars * _branchCount);
            _deathEvt.AsSpan(0, total).Fill(-1); _injuryEvt.AsSpan(0, total).Fill(-1); _loc.AsSpan(0, total).Fill(-1); _locEvt.AsSpan(0, total).Fill(-1);
            _outfit.AsSpan(0, total).Fill(-1); _outfitEvt.AsSpan(0, total).Fill(-1); _outfitDay.AsSpan(0, total).Fill(0); _scene.AsSpan(0, total).Fill(-1);
            _lastDay.AsSpan().Fill(0); _lastDayEvt.AsSpan().Fill(-1);
            _owns.AsSpan(0, Math.Min(_owns.Length, total * _symCap)).Clear();
            _knows.AsSpan(0, Math.Min(_knows.Length, total * _symCap)).Clear();
            _issueCount = 0;
            var lastBranchSeeded = 0;

            for (var i = 0; i < _eventCount; i++)
            {
                ref readonly var ev = ref _events[i];
                var br = (uint)ev.Episode < (uint)_branchOf.Length ? _branchOf[ev.Episode] : 0;
                if (br > 0 && (lastBranchSeeded & (1 << Math.Min(br, 30))) == 0) { SeedBranch(br, nChars); lastBranchSeeded |= 1 << Math.Min(br, 30); }
                if (ev.Kind == UniverseEventKind.Day)
                {
                    if (!ev.Flashback && ev.Arg < _lastDay[br] && _lastDayEvt[br] >= 0) Report(PlotHoleKind.TimelineRegression, PlotSeverity.Major, i, _lastDayEvt[br]);
                    if (!ev.Flashback) { _lastDay[br] = ev.Arg; _lastDayEvt[br] = i; }
                    continue;
                }
                if (ev.Character < 0) continue;
                var c = br * nChars + ev.Character;
                if (c >= total) continue;
                var row = c * _symCap;
                var dead = _deathEvt[c] >= 0;
                switch (ev.Kind)
                {
                    case UniverseEventKind.Appear:
                        if (dead && !ev.Flashback) Report(PlotHoleKind.DeadCharacterActs, PlotSeverity.Critical, i, _deathEvt[c]);
                        if (!ev.Flashback && ev.Continuous && _locEvt[c] >= 0 && _loc[c] != ev.Arg)
                        {
                            ref readonly var prev = ref _events[_locEvt[c]];
                            if (prev.Episode == ev.Episode && prev.Scene == ev.Scene - 1) Report(PlotHoleKind.Teleport, PlotSeverity.Major, i, _locEvt[c]);
                        }
                        if (!ev.Flashback) { _loc[c] = ev.Arg; _locEvt[c] = i; _scene[c] = ev.Scene; }
                        break;
                    case UniverseEventKind.Speak:
                    case UniverseEventKind.Strenuous:
                        if (dead && !ev.Flashback) { if (!LastIssueIs(PlotHoleKind.DeadCharacterActs, _deathEvt[c], ev.Episode, ev.Scene)) Report(PlotHoleKind.DeadCharacterActs, PlotSeverity.Critical, i, _deathEvt[c]); }
                        else if (ev.Kind == UniverseEventKind.Strenuous && _injuryEvt[c] >= 0 && ev.Day - _injuryDay[c] <= InjuryWindowDays && !ev.Flashback)
                            Report(PlotHoleKind.InjuryIgnored, PlotSeverity.Minor, i, _injuryEvt[c]);
                        break;
                    case UniverseEventKind.Die:
                        if (ev.Flashback) break;
                        if (dead) Report(PlotHoleKind.DoubleDeath, PlotSeverity.Minor, i, _deathEvt[c]);
                        else _deathEvt[c] = i;
                        break;
                    case UniverseEventKind.Revive: _deathEvt[c] = -1; break;
                    case UniverseEventKind.Injure: if (!ev.Flashback) { _injuryEvt[c] = i; _injuryDay[c] = ev.Day; } break;
                    case UniverseEventKind.Heal: _injuryEvt[c] = -1; break;
                    case UniverseEventKind.Gain: if (ev.Arg < _symCap) _owns[row + ev.Arg] = true; break;
                    case UniverseEventKind.Lose:
                    case UniverseEventKind.Use:
                        if (ev.Arg >= _symCap) break;
                        if (dead && !ev.Flashback) Report(PlotHoleKind.DeadCharacterActs, PlotSeverity.Critical, i, _deathEvt[c]);
                        else if (!_owns[row + ev.Arg] && !ev.Flashback) Report(PlotHoleKind.ItemNotOwned, PlotSeverity.Major, i, -1);
                        if (ev.Kind == UniverseEventKind.Lose) _owns[row + ev.Arg] = false;
                        break;
                    case UniverseEventKind.Outfit:
                        if (ev.Flashback) break;
                        if (_outfitEvt[c] >= 0 && _outfitDay[c] == ev.Day && _outfit[c] != ev.Arg)
                        {
                            ref readonly var po = ref _events[_outfitEvt[c]];
                            if (po.Episode == ev.Episode && ev.Continuous) Report(PlotHoleKind.OutfitDiscontinuity, PlotSeverity.Minor, i, _outfitEvt[c]);
                            else if (po.Episode == ev.Episode && ev.Scene - po.Scene <= 1) Report(PlotHoleKind.OutfitDiscontinuity, PlotSeverity.Minor, i, _outfitEvt[c]);
                        }
                        _outfit[c] = ev.Arg; _outfitDay[c] = ev.Day; _outfitEvt[c] = i;
                        break;
                    case UniverseEventKind.Learn: if (ev.Arg < _symCap) _knows[row + ev.Arg] = true; break;
                    case UniverseEventKind.Mention:
                        if (ev.Arg < _symCap && !_knows[row + ev.Arg]) Report(PlotHoleKind.KnowledgeLeak, PlotSeverity.Major, i, -1);
                        break;
                }
            }
            Validations++;
            return _issueCount;
        }
    }

    /// <summary>스핀오프 브랜치 진입 시 본편(브랜치 0) 상태를 복제(분기 시점 이전 본편 이벤트 기준).</summary>
    private void SeedBranch(int br, int nChars)
    {
        var dst = br * nChars;
        if (dst + nChars > _charCap) return;
        _deathEvt.AsSpan(0, nChars).CopyTo(_deathEvt.AsSpan(dst));
        _injuryEvt.AsSpan(0, nChars).CopyTo(_injuryEvt.AsSpan(dst));
        _injuryDay.AsSpan(0, nChars).CopyTo(_injuryDay.AsSpan(dst));
        _outfit.AsSpan(0, nChars).CopyTo(_outfit.AsSpan(dst));
        _outfitDay.AsSpan(0, nChars).CopyTo(_outfitDay.AsSpan(dst));
        _owns.AsSpan(0, nChars * _symCap).CopyTo(_owns.AsSpan(dst * _symCap));
        _knows.AsSpan(0, nChars * _symCap).CopyTo(_knows.AsSpan(dst * _symCap));
        _lastDay[br] = _lastDay[0];
    }

    private bool LastIssueIs(PlotHoleKind k, int refEvt, int ep, int scene)
    {
        if (_issueCount == 0) return false;
        ref readonly var last = ref _issues[_issueCount - 1];
        return last.Kind == k && last.Ref == refEvt && _events[last.Event].Episode == ep && _events[last.Event].Scene == scene;
    }

    private void Report(PlotHoleKind k, PlotSeverity sev, int evt, int refEvt)
    {
        if (_issueCount < MaxIssues) _issues[_issueCount++] = new PlotHole(k, sev, evt, refEvt);
    }

    /// <summary>컴파일 + 검증 1-Click.</summary>
    public int Analyze()
    {
        Compile();
        var n = Validate();
        LastAction = "🔍 개연성 검증 · 모순 " + n.ToString(CultureInfo.InvariantCulture) + "건 · 이벤트 " + _eventCount.ToString(CultureInfo.InvariantCulture);
        Raise();
        return n;
    }

    // ══════════════════════ 표시(UI · 할당 허용) ══════════════════════

    public static string KindLabel(PlotHoleKind k) => k switch
    {
        PlotHoleKind.DeadCharacterActs => "☠ 사망 후 행동", PlotHoleKind.DoubleDeath => "⚰ 중복 사망", PlotHoleKind.ItemNotOwned => "🎒 미소지 물건",
        PlotHoleKind.OutfitDiscontinuity => "👔 의상 불연속", PlotHoleKind.Teleport => "🌀 순간이동", PlotHoleKind.TimelineRegression => "⏪ 일차 역행",
        PlotHoleKind.KnowledgeLeak => "🧠 학습 전 지식", _ => "🩹 부상 무시",
    };

    public string Where(int evt)
    {
        if ((uint)evt >= (uint)_eventCount) return "—";
        var ev = _events[evt];
        var ep = (uint)ev.Episode < (uint)_episodes.Count ? _episodes[ev.Episode].Title : "?";
        return ep + " · S" + (ev.Scene + 1).ToString(CultureInfo.InvariantCulture) + " · D" + ev.Day.ToString(CultureInfo.InvariantCulture);
    }

    public string Describe(in PlotHole h)
    {
        var ev = _events[h.Event];
        var who = CharacterName(ev.Character);
        var at = Where(h.Event);
        var refAt = h.Ref >= 0 ? Where(h.Ref) : "";
        return h.Kind switch
        {
            PlotHoleKind.DeadCharacterActs => who + " — " + refAt + " 에서 사망했으나 " + at + " 에 등장/행동",
            PlotHoleKind.DoubleDeath => who + " — 이미 " + refAt + " 에서 사망 · " + at + " 에서 다시 사망",
            PlotHoleKind.ItemNotOwned => who + " — 소지하지 않은 '" + Symbol(ev.Arg) + "' 사용/분실 (" + at + ")",
            PlotHoleKind.OutfitDiscontinuity => who + " — 같은 날 의상 '" + Symbol(_events[h.Ref].Arg) + "' → '" + Symbol(ev.Arg) + "' 변경 (" + at + ")",
            PlotHoleKind.Teleport => who + " — 연속 씬에서 '" + Symbol(_events[h.Ref].Arg) + "' → '" + Symbol(ev.Arg) + "' 순간이동 (" + at + ")",
            PlotHoleKind.TimelineRegression => "일차 역행 D" + _events[h.Ref].Arg.ToString(CultureInfo.InvariantCulture) + " → D" + ev.Arg.ToString(CultureInfo.InvariantCulture) + " (" + at + " · 회상 표기 누락)",
            PlotHoleKind.KnowledgeLeak => who + " — 알기 전에 '" + Symbol(ev.Arg) + "' 언급 (" + at + ")",
            _ => who + " — " + refAt + " 부상 직후 격한 행동 (" + at + ")",
        };
    }

    /// <summary>타임라인 노드 그래프(에피소드 · 브랜치 레인 · 일차 범위 · 사망/모순 수).</summary>
    public IReadOnlyList<TimelineNode> BuildGraph()
    {
        lock (_gate)
        {
            var lanes = new Dictionary<string, int>(StringComparer.Ordinal) { ["본편"] = 0 };
            var nodes = new List<TimelineNode>(_episodes.Count);
            for (var ep = 0; ep < _episodes.Count; ep++)
            {
                var e = _episodes[ep];
                if (!lanes.TryGetValue(e.Branch, out var lane)) { lane = lanes.Count; lanes[e.Branch] = lane; }
                int first = int.MaxValue, last = 0, deaths = 0, issues = 0;
                for (var i = 0; i < _eventCount; i++)
                {
                    if (_events[i].Episode != ep) continue;
                    first = Math.Min(first, _events[i].Day); last = Math.Max(last, _events[i].Day);
                    if (_events[i].Kind == UniverseEventKind.Die && !_events[i].Flashback) deaths++;
                }
                for (var k = 0; k < _issueCount; k++) if (_events[_issues[k].Event].Episode == ep) issues++;
                nodes.Add(new TimelineNode(ep, e.Title, e.Branch, lane, e.Script.Scenes.Count, first == int.MaxValue ? 0 : first, last, deaths, issues));
            }
            return nodes;
        }
    }

    // ══════════════════════ 데모 프랜차이즈 ══════════════════════

    /// <summary>의도적 모순 6종을 포함한 3화 + 스핀오프 1화 데모 유니버스.</summary>
    public static IReadOnlyList<(string Title, string Branch, string Fountain)> DemoFranchise() => new[]
    {
        ("EP1 붉은 서약", "본편", """
INT. 왕궁 서재 - 밤

촛불이 흔들리는 첫째 날 밤. [[DAY 1]] [[OUTFIT 서윤: 은빛 갑옷]]

서윤이 오래된 지도를 펼친다. [[GAIN 서윤: 별의 검]]

@서윤
반드시 되찾겠어.

@도현
(낮게)
배신자는 우리 안에 있어. [[LEARN 도현: 배신자의 정체]]

EXT. 성벽 - 밤 연속

바람이 거세다. [[OUTFIT 서윤: 검은 망토]]

도현이 화살에 맞아 쓰러진다. 도현이 숨을 거둔다.
"""),
        ("EP2 그림자 계약", "본편", """
INT. 지하 감옥 - 낮

둘째 날 아침. [[DAY 2]]

@도현
아직 끝나지 않았어.

@서윤
[[USE 서윤: 별의 검]] 이 검이면 충분해. [[MENTION 서윤: 배신자의 정체]]

EXT. 숲 - 낮

서윤이 덫에 걸린다. [[INJURE 서윤]]

서윤이 숲을 질주한다.
"""),
        ("EP3 마지막 왕관", "본편", """
INT. 왕좌의 방 - 밤

왕좌가 비어 있다. [[DAY 1]]

@서윤
[[USE 서윤: 왕의 인장]] 이제 끝이야.

INT. 왕좌의 방 - 회상

오래전 기억. [[DAY 0]]

@도현
기억해, 서윤.
"""),
        ("외전 하윤의 길", "외전", """
EXT. 항구 - 새벽

셋째 날 새벽. [[DAY 3]]

@하윤
도현의 검을 이어받겠어. [[GAIN 하윤: 도현의 검]]
"""),
    };

    private void Raise() { try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("Universe.OnChange", ex); } }
}
