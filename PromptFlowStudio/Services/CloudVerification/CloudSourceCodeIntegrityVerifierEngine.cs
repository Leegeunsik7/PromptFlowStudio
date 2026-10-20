using System.Text.RegularExpressions;

namespace PromptFlowStudio.Services.CloudVerification;

public enum VerifyRule : byte { HotPathLinq, HotPathInterpolation, HotPathStringAlloc, HotPathHeapAlloc, HotPathBoxing, AsyncVoid, UnobservedTask, SyncOverAsync, SwallowedCatch, RazorLoopSvgText, RazorRangeUnbounded, RazorDeadButton }
public enum VerifySeverity : byte { Info, Warning, Error }

/// <summary>정적 검증 결과(값 형식 · 파일 인덱스 → <see cref="CloudSourceCodeIntegrityVerifierEngine.FileName"/>).</summary>
public readonly record struct SourceFinding(int File, int Line, VerifyRule Rule, VerifySeverity Severity, string Member);

/// <summary>동적 할당 프로브 결과(GC.GetAllocatedBytesForCurrentThread 실측).</summary>
public readonly record struct AllocationProbe(string Name, double BytesPerCall, double MsPerCall, bool ZeroAlloc);

/// <summary>
/// [PromptFlow Studio v16.92.0] 🔍 소스 코드 자율 구문 정합성 &amp; Zero-Alloc 검증 엔진(Pure BCL · Roslyn 미사용).
///   ▸ 렉서: 주석 · 문자열(일반/축자/보간 홀 중첩/원시) · 문자 리터럴을 공백으로 정규화(줄 보존) + 보간 시작 위치 수집.
///   ▸ 구조 트리: 중괄호 스택 헤더 분류(메서드/접근자/람다/제어 블록/형식) → 메서드 본문 범위.
///     핫패스 = 이름(TickOnce/Tick/Step/OnTick/RenderFrame/ProcessFrame) 또는 본문 내 <c>ZeroGcRegionScope.Enter</c> 마커 · 중첩 블록 상속.
///   ▸ 의미 보강: 코퍼스 전체 struct/record struct/enum 이름 학습 → 값 형식 <c>new</c>는 힙 할당에서 제외.
///   ▸ 규칙 12종: 핫패스 LINQ/보간/문자열 할당/힙 할당/박싱 · async void · 미관찰 Task · 동기 대기 · 예외 삼킴 · Razor 루프 SVG text/범위 슬라이더/무동작 버튼.
///   ▸ 동적: <see cref="ProbeAllocations"/> — 실제 핫패스 델리게이트 반복 호출 바이트/호출 실측.
///   "클라우드" 실행 = CI/원격 러너에서 동일 엔진 구동(소스 루트: 환경변수 PROMPTFLOW_SOURCE_ROOT) · 외부 업로드 없음.
/// </summary>
public sealed class CloudSourceCodeIntegrityVerifierEngine
{
    public const int Rules = 12, MaxFindings = 4096, MaxProbes = 16;
    public static readonly string[] RuleNames = { "핫패스 LINQ", "핫패스 보간", "핫패스 문자열", "핫패스 힙 new", "핫패스 박싱", "async void", "미관찰 Task", "동기 대기", "예외 삼킴", "Razor 루프 text", "범위 무경계", "무동작 버튼" };
    public static readonly string[] RuleCodes = { "LNQ", "INT", "STR", "NEW", "BOX", "AV", "TSK", "SYN", "CAT", "TXT", "RNG", "BTN" };
    private static readonly string[] HotNames = { "TickOnce", "Tick", "Step", "OnTick", "RenderFrame", "ProcessFrame" };
    private static readonly string[] BlockKeywords = { "if", "else", "for", "foreach", "while", "do", "switch", "try", "catch", "finally", "using", "lock", "fixed", "unsafe", "checked", "unchecked", "when" };
    private static readonly string[] TypeKeywords = { "class", "struct", "record", "interface", "namespace", "enum" };
    private static readonly string[] HeapCollections = { "List", "Dictionary", "HashSet", "Queue", "Stack", "StringBuilder", "ConcurrentQueue", "ConcurrentDictionary", "SortedList", "LinkedList" };

    private static readonly Regex LinqRx = new(@"\.(Select|SelectMany|Where|OrderBy|OrderByDescending|ThenBy|GroupBy|ToList|ToArray|ToDictionary|ToHashSet|Aggregate|Sum|Average|FirstOrDefault|LastOrDefault|Distinct|Any|Zip|Skip|Take)\s*\(", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NewRx = new(@"(?<![\w.])new(?:\s+(?<t>[A-Za-z_][\w.]*)\s*(?<g><[^;(){}]*?>)?)?\s*(?<k>[\[({])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex StrRx = new(@"\bstring\.(Format|Concat|Join)\s*\(|\.ToString\s*\(|""\s*\+(?!\+)|(?<!\+)\+\s*""", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BoxRx = new(@"\(\s*object\s*\)|\bas\s+object\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AsyncVoidRx = new(@"\basync\s+void\s+(?<n>\w+)\s*\((?<p>[^)]*)\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UnobservedRx = new(@"(?<=[;{}]\s*)(?:[A-Za-z_]\w*\.)*(?<n>Task\.Run|Task\.Factory\.StartNew|[A-Za-z_]\w*Async)\s*\(", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SyncRx = new(@"\.GetAwaiter\(\s*\)\s*\.GetResult\(|\b\w*[Tt]ask\w*\.Wait\(\s*\)|Async\([^;\n]*\)\s*\.Result\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CatchRx = new(@"\bcatch\s*(?:\((?<t>[^)]*)\))?\s*(?:when\s*\([^)]*\)\s*)?\{\s*\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ValueTypeRx = new(@"\b(?:record\s+struct|struct|enum)\s+(?<n>[A-Za-z_]\w*)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly object _gate = new();
    private readonly HashSet<string> _valueTypes = new(StringComparer.Ordinal)
    {
        "Vector2", "Vector3", "Vector4", "Quaternion", "Matrix3x2", "Matrix4x4", "Plane", "Complex", "TimeSpan", "DateTime", "DateTimeOffset", "Guid", "Span", "ReadOnlySpan",
        "Memory", "ReadOnlyMemory", "KeyValuePair", "ValueTuple", "ValueTask", "SpinWait", "Vector", "Vector64", "Vector128", "Vector256", "HashCode", "Nullable", "ArraySegment", "Rune",
        "Color", "Point", "PointF", "Size", "SizeF", "Rectangle", "RectangleF", "GCHandle", "Utf8JsonReader", "SequencePosition", "BitVector32", "ConfiguredTaskAwaitable", "CancellationToken", "Range", "Index", "Half", "Int128", "UInt128", "decimal", "int", "long", "float", "double", "byte", "bool", "char",
    };
    private readonly List<string> _files = new();
    private readonly List<int> _fileLines = new();
    private readonly List<int[]> _matrix = new();
    private readonly SourceFinding[] _findings = new SourceFinding[MaxFindings];
    private readonly int[] _ruleCounts = new int[Rules];
    private readonly AllocationProbe[] _probes = new AllocationProbe[MaxProbes];
    private int _findingCount, _probeCount;

    public int FileCount { get { lock (_gate) return _files.Count; } }
    public string FileName(int i) { lock (_gate) return _files[i]; }
    public int FileLines(int i) { lock (_gate) return _fileLines[i]; }
    public int Cell(int file, VerifyRule r) { lock (_gate) return _matrix[file][(int)r]; }
    public int FileTotal(int file) { lock (_gate) { var s = 0; var m = _matrix[file]; for (var k = 0; k < Rules; k++) s += m[k]; return s; } }
    public int FindingCount => _findingCount;
    public long TotalFindings { get; private set; }
    public SourceFinding Finding(int i) => _findings[i];
    public int RuleCount(VerifyRule r) => _ruleCounts[(int)r];
    public int ProbeCount => _probeCount;
    public AllocationProbe Probe(int i) => _probes[i];
    public int ValueTypeCount { get { lock (_gate) return _valueTypes.Count; } }
    public long Errors { get; private set; }
    public long Warnings { get; private set; }
    public long Infos { get; private set; }
    public long TotalLines { get; private set; }
    public int HotMethods { get; private set; }
    public int Methods { get; private set; }
    public double LastScanMs { get; private set; }
    public int FilesTotal { get; private set; }
    public int FilesScanned { get; private set; }
    public string? Root { get; private set; }

    /// <summary>정합성 점수(0~100): 100 / (1 + (3·E + W + 0.1·I) / KLOC).</summary>
    public double IntegrityScore => TotalLines <= 0 ? 100d : 100d / (1d + (3d * Errors + Warnings + 0.1d * Infos) / Math.Max(1d, TotalLines / 1000d));

    public static VerifySeverity SeverityOf(VerifyRule r) => r switch
    {
        VerifyRule.HotPathLinq or VerifyRule.HotPathInterpolation or VerifyRule.AsyncVoid or VerifyRule.RazorLoopSvgText => VerifySeverity.Error,
        VerifyRule.SwallowedCatch => VerifySeverity.Info,
        _ => VerifySeverity.Warning,
    };

    public void Reset()
    {
        lock (_gate)
        {
            _files.Clear(); _fileLines.Clear(); _matrix.Clear();
            _findingCount = 0; _probeCount = 0; Array.Clear(_ruleCounts);
            Errors = Warnings = Infos = TotalLines = TotalFindings = 0; HotMethods = Methods = 0; FilesScanned = FilesTotal = 0;
        }
    }

    /// <summary>1차 패스: 값 형식 이름 학습(struct · record struct · enum).</summary>
    public void LearnValueTypes(string text)
    {
        var code = Sanitize(text, null);
        foreach (Match m in ValueTypeRx.Matches(code)) lock (_gate) _valueTypes.Add(m.Groups["n"].Value);
    }

    public bool IsValueType(string name) { lock (_gate) return _valueTypes.Contains(name); }

    /// <summary>2차 패스: 단일 소스 분석(.cs 전체 · .razor는 @code 블록 C# + 마크업 규칙) → 파일 발견 수.</summary>
    public int AnalyzeSource(string path, string text)
    {
        int fi;
        lock (_gate) { fi = _files.Count; _files.Add(path); _fileLines.Add(CountLines(text)); _matrix.Add(new int[Rules]); TotalLines += _fileLines[fi]; }
        var before = TotalFindings;
        var interp = new List<int>();
        var code = Sanitize(text, interp);
        var lines = LineStarts(text);
        var razor = path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase);
        var csFrom = 0;
        if (razor)
        {
            csFrom = code.IndexOf("@code", StringComparison.Ordinal);
            if (csFrom < 0) csFrom = code.Length;
            AnalyzeMarkup(fi, text, code, csFrom, lines);
        }
        var names = new string?[code.Length + 1];
        var hot = BuildHotMask(code, csFrom, razor, names);

        foreach (Match m in LinqRx.Matches(code, csFrom)) if (hot[m.Index] && LinqShaped(code, m.Index + m.Length - 1)) Add(fi, lines, m.Index, VerifyRule.HotPathLinq, names[m.Index]);
        foreach (var p in interp) if (p >= csFrom && hot[p]) Add(fi, lines, p, VerifyRule.HotPathInterpolation, names[p]);
        foreach (Match m in StrRx.Matches(code, csFrom)) if (hot[m.Index]) Add(fi, lines, m.Index, VerifyRule.HotPathStringAlloc, names[m.Index]);
        foreach (Match m in BoxRx.Matches(code, csFrom)) if (hot[m.Index]) Add(fi, lines, m.Index, VerifyRule.HotPathBoxing, names[m.Index]);
        foreach (Match m in NewRx.Matches(code, csFrom))
        {
            if (!hot[m.Index] || PrecededBy(code, m.Index, "throw")) continue;
            var t = m.Groups["t"].Success ? m.Groups["t"].Value : "";
            var dot = t.LastIndexOf('.'); if (dot >= 0) t = t[(dot + 1)..];
            var k = code[m.Groups["k"].Index];
            var heap = k == '[' || (t.Length > 0 && !IsValueType(t)) || Array.IndexOf(HeapCollections, t) >= 0;
            if (t.Length == 0 && k != '[') heap = false; // 대상 형식 new() — 형식 미상(값 형식 다수) 생략
            if (heap) Add(fi, lines, m.Index, VerifyRule.HotPathHeapAlloc, names[m.Index]);
        }
        foreach (Match m in AsyncVoidRx.Matches(code, csFrom))
        {
            var p = m.Groups["p"].Value;
            if (p.Contains("EventArgs", StringComparison.Ordinal) || p.Contains("sender", StringComparison.Ordinal)) continue;
            Add(fi, lines, m.Index, VerifyRule.AsyncVoid, m.Groups["n"].Value);
        }
        foreach (Match m in UnobservedRx.Matches(code, csFrom))
        {
            if (PrecededBy(code, m.Index, "await") || PrecededBy(code, m.Index, "return") || ChainedAfterCall(code, m.Index + m.Length - 1)) continue;
            Add(fi, lines, m.Index, VerifyRule.UnobservedTask, m.Groups["n"].Value);
        }
        foreach (Match m in SyncRx.Matches(code, csFrom)) Add(fi, lines, m.Index, VerifyRule.SyncOverAsync, names[m.Index]);
        foreach (Match m in CatchRx.Matches(code, csFrom))
        {
            var t = m.Groups["t"].Value;
            if (t.Contains("OperationCanceled", StringComparison.Ordinal) || t.Contains("TaskCanceled", StringComparison.Ordinal) || t.Contains("ObjectDisposed", StringComparison.Ordinal)) continue;
            Add(fi, lines, m.Index, VerifyRule.SwallowedCatch, names[m.Index]);
        }
        lock (_gate) FilesScanned++;
        return (int)(TotalFindings - before);
    }

    /// <summary>Razor 마크업(@code 이전) 규칙: 코드 블록 직속(&lt;g&gt; 밖) SVG &lt;text&gt; · min/max 없는 range · @onclick 없는 버튼.</summary>
    private void AnalyzeMarkup(int fi, string raw, string code, int end, int[] lines)
    {
        int depth = 0, g = 0;
        for (var i = 0; i < end; i++)
        {
            var c = code[i];
            if (c == '{') depth++;
            else if (c == '}') { if (depth > 0) depth--; }
            else if (c == '<' && i + 3 < end && raw[i + 1] == 'g' && (raw[i + 2] == '>' || raw[i + 2] == ' ')) g++;
            else if (c == '<' && i + 4 < end && raw[i + 1] == '/' && raw[i + 2] == 'g' && raw[i + 3] == '>') { if (g > 0) g--; }
            else if (c == '<' && depth > 0 && g == 0 && string.CompareOrdinal(raw, i, "<text", 0, 5) == 0 && i + 5 < raw.Length && (raw[i + 5] == ' ' || raw[i + 5] == '>') && LineStartsWith(raw, i))
                Add(fi, lines, i, VerifyRule.RazorLoopSvgText, null);
        }
        for (var i = raw.IndexOf("<input", 0, end, StringComparison.Ordinal); i >= 0 && i < end; i = raw.IndexOf("<input", i + 6, end - i - 6, StringComparison.Ordinal))
        {
            var tag = raw.AsSpan(i, TagEnd(raw, i) - i);
            if (tag.Contains("\"range\"", StringComparison.Ordinal) && (!tag.Contains("min=", StringComparison.Ordinal) || !tag.Contains("max=", StringComparison.Ordinal)))
                Add(fi, lines, i, VerifyRule.RazorRangeUnbounded, null);
            if (i + 6 >= end) break;
        }
        for (var i = raw.IndexOf("<button", 0, end, StringComparison.Ordinal); i >= 0 && i < end; i = raw.IndexOf("<button", i + 7, end - i - 7, StringComparison.Ordinal))
        {
            var tag = raw.AsSpan(i, TagEnd(raw, i) - i);
            if (!tag.Contains("@onclick", StringComparison.Ordinal) && !tag.Contains("submit", StringComparison.Ordinal) && !tag.Contains("disabled", StringComparison.Ordinal) && !tag.Contains("@attributes", StringComparison.Ordinal))
                Add(fi, lines, i, VerifyRule.RazorDeadButton, null);
            if (i + 7 >= end) break;
        }
    }

    /// <summary>중괄호 스택 구조 트리 → 핫 마스크(메서드 이름 기록 포함).</summary>
    private bool[] BuildHotMask(string code, int from, bool razor, string?[] names)
    {
        var n = code.Length;
        var hot = new bool[n + 1];
        var stack = new Stack<(int Start, bool Hot, string? Name, bool Method)>();
        var methods = new List<(int S, int E, string Name, bool ParentHot)>();
        var lastDelim = from - 1;
        for (var i = from; i < n; i++)
        {
            var c = code[i];
            if (c == ';') { lastDelim = i; continue; }
            if (c == '{')
            {
                var header = code.AsSpan(lastDelim + 1, i - lastDelim - 1).Trim();
                var parentHot = stack.Count > 0 && stack.Peek().Hot;
                var parentName = stack.Count > 0 ? stack.Peek().Name : null;
                var (isMethod, name) = Classify(header);
                if (isMethod) stack.Push((i, parentHot, name, true));
                else stack.Push((i, parentHot, parentName, false));
                lastDelim = i;
                continue;
            }
            if (c == '}')
            {
                if (stack.Count > 0)
                {
                    var f = stack.Pop();
                    if (f.Method && f.Name is not null) methods.Add((f.Start, i, f.Name, f.Hot));
                }
                lastDelim = i;
            }
        }
        // 핫 판정(이름 · 마커) → 바깥 메서드부터 마스크 채움(중첩 블록/람다/로컬 함수 상속)
        methods.Sort(static (a, b) => a.S.CompareTo(b.S));
        foreach (var m in methods)
        {
            lock (_gate) Methods++;
            var isHot = m.ParentHot || hot[m.S] || (!razor && Array.IndexOf(HotNames, m.Name) >= 0) || code.IndexOf("ZeroGcRegionScope.Enter", m.S, m.E - m.S, StringComparison.Ordinal) >= 0;
            if (isHot && !hot[m.S]) lock (_gate) HotMethods++;
            for (var i = m.S; i <= m.E; i++) { if (isHot) hot[i] = true; names[i] ??= m.Name; }
        }
        return hot;
    }

    private static (bool Method, string? Name) Classify(ReadOnlySpan<char> h)
    {
        if (h.Length == 0) return (false, null);
        var first = FirstWord(h);
        if (Array.IndexOf(BlockKeywords, first) >= 0) return (false, null);
        if (first is "get" or "set" or "init" or "add" or "remove") return (true, first);
        var paren = h.IndexOf('(');
        var pre = paren < 0 ? h : h[..paren];
        for (var k = 0; k < TypeKeywords.Length; k++) if (ContainsWord(pre, TypeKeywords[k])) return (false, null);
        if (h.Contains("=>", StringComparison.Ordinal) || h.Contains("new", StringComparison.Ordinal) && (h.EndsWith(")") || h.EndsWith("]") || h.EndsWith("new"))) return (false, null);
        if (h.EndsWith("=") || h.EndsWith(",") || h.EndsWith("(") || h.EndsWith("switch") || h.EndsWith("return")) return (false, null);
        var p = h.IndexOf('(');
        if (p <= 0) return (false, null);
        var e = p - 1;
        while (e >= 0 && h[e] == ' ') e--;
        if (e >= 0 && h[e] == '>') { var depth = 0; for (; e >= 0; e--) { if (h[e] == '>') depth++; else if (h[e] == '<' && --depth == 0) { e--; break; } } }
        var s = e;
        while (s >= 0 && (char.IsLetterOrDigit(h[s]) || h[s] == '_')) s--;
        if (s == e) return (false, null);
        var name = h.Slice(s + 1, e - s).ToString();
        return Array.IndexOf(BlockKeywords, name) >= 0 || name is "base" or "this" or "nameof" or "typeof" ? (false, null) : (true, name);
    }

    private static string FirstWord(ReadOnlySpan<char> h)
    {
        var e = 0;
        while (e < h.Length && (char.IsLetterOrDigit(h[e]) || h[e] == '_')) e++;
        return h[..e].ToString();
    }

    private static bool ContainsWord(ReadOnlySpan<char> h, string w)
    {
        for (var i = h.IndexOf(w, StringComparison.Ordinal); i >= 0; )
        {
            var a = i == 0 || !char.IsLetterOrDigit(h[i - 1]);
            var b = i + w.Length >= h.Length || !char.IsLetterOrDigit(h[i + w.Length]);
            if (a && b) return true;
            var next = h[(i + 1)..].IndexOf(w, StringComparison.Ordinal);
            i = next < 0 ? -1 : i + 1 + next;
        }
        return false;
    }

    internal static bool PrecededBy(string code, int i, string word)
    {
        var e = i - 1;
        while (e >= 0 && char.IsWhiteSpace(code[e])) e--;
        var s = e - word.Length + 1;
        return s >= 0 && string.CompareOrdinal(code, s, word, 0, word.Length) == 0 && (s == 0 || !char.IsLetterOrDigit(code[s - 1]));
    }

    /// <summary>LINQ 형태 판정: 인자 없음(ToList/ToArray/Any …) 또는 람다 인자 — 동명 사용자 메서드(Aggregate(t, x) 등) 제외.</summary>
    private static bool LinqShaped(string code, int open)
    {
        var depth = 0;
        for (var k = open; k < code.Length; k++)
        {
            var c = code[k];
            if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return k == open + 1 || code.AsSpan(open + 1, k - open - 1).Trim().IsEmpty || code.AsSpan(open, k - open).Contains("=>", StringComparison.Ordinal);
            else if (c == ';') return false;
        }
        return false;
    }

    /// <summary>호출 괄호 닫힘 뒤 체인(.GetAwaiter/.Wait/.ContinueWith 등) 여부 — 관찰된 Task.</summary>
    internal static bool ChainedAfterCall(string code, int open)
    {
        var depth = 0;
        for (var k = open; k < code.Length; k++)
        {
            if (code[k] == '(') depth++;
            else if (code[k] == ')' && --depth == 0)
            {
                var e = k + 1;
                while (e < code.Length && char.IsWhiteSpace(code[e])) e++;
                return e < code.Length && code[e] == '.';
            }
            else if (code[k] == ';') return false;
        }
        return false;
    }

    private static bool LineStartsWith(string raw, int i)
    {
        for (var k = i - 1; k >= 0 && raw[k] != '\n'; k--) if (!char.IsWhiteSpace(raw[k])) return false;
        return true;
    }

    private static int TagEnd(string raw, int i)
    {
        var q = '\0';
        for (var k = i + 1; k < raw.Length; k++)
        {
            var c = raw[k];
            if (q != '\0') { if (c == q) q = '\0'; }
            else if (c is '"' or '\'') q = c;
            else if (c == '>') return k + 1;
        }
        return raw.Length;
    }

    private void Add(int fi, int[] lines, int pos, VerifyRule r, string? member)
    {
        var sev = SeverityOf(r);
        var line = LineOf(lines, pos);
        lock (_gate)
        {
            if (_findingCount < MaxFindings) _findings[_findingCount++] = new SourceFinding(fi, line, r, sev, member ?? "");
            _matrix[fi][(int)r]++; _ruleCounts[(int)r]++; TotalFindings++;
            if (sev == VerifySeverity.Error) Errors++; else if (sev == VerifySeverity.Warning) Warnings++; else Infos++;
        }
    }

    /// <summary>렉서 정규화: 주석/문자열/문자 내용 → 공백(개행·따옴표 보존) · 보간 문자열 시작 위치 수집.</summary>
    public static string Sanitize(string s, List<int>? interp)
    {
        var n = s.Length;
        var o = new char[n];
        var i = 0;
        while (i < n)
        {
            var c = s[i];
            if (c == '/' && i + 1 < n && s[i + 1] == '/') { while (i < n && s[i] != '\n') o[i++] = ' '; continue; }
            if (c == '/' && i + 1 < n && s[i + 1] == '*')
            {
                o[i++] = ' '; o[i++] = ' ';
                while (i < n && !(s[i] == '*' && i + 1 < n && s[i + 1] == '/')) { o[i] = s[i] == '\n' ? '\n' : ' '; i++; }
                if (i < n) { o[i++] = ' '; if (i < n) o[i++] = ' '; }
                continue;
            }
            if (c == '@' && i + 1 < n && s[i + 1] == '*') // Razor 주석
            {
                o[i++] = ' '; o[i++] = ' ';
                while (i < n && !(s[i] == '*' && i + 1 < n && s[i + 1] == '@')) { o[i] = s[i] == '\n' ? '\n' : ' '; i++; }
                if (i < n) { o[i++] = ' '; if (i < n) o[i++] = ' '; }
                continue;
            }
            if (c is '"' or '$' or '@')
            {
                var j = i; var dollars = 0; var verbatim = false;
                while (j < n && (s[j] == '$' || s[j] == '@')) { if (s[j] == '$') dollars++; else verbatim = true; j++; }
                if (j < n && s[j] == '"' && (j - i) <= 3)
                {
                    for (var k = i; k <= j; k++) o[k] = s[k];
                    if (dollars > 0) interp?.Add(i);
                    i = ScanString(s, o, j, verbatim, dollars > 0);
                    continue;
                }
                o[i] = c; i++; continue;
            }
            if (c == '\'')
            {
                var k = i + 1;
                if (k < n && s[k] == '\\') k += 2; else k++;
                while (k < n && k - i < 10 && s[k] != '\'' && s[k] != '\n') k++;
                if (k < n && s[k] == '\'') { o[i] = '\''; for (var m = i + 1; m < k; m++) o[m] = ' '; o[k] = '\''; i = k + 1; continue; }
                o[i] = c; i++; continue;
            }
            o[i] = c; i++;
        }
        return new string(o);
    }

    /// <summary>문자열 본문 스캔(q = 여는 따옴표 위치) → 닫힘 다음 위치. 원시("""), 축자("" 이스케이프), 보간 홀 중첩 처리.</summary>
    private static int ScanString(string s, char[] o, int q, bool verbatim, bool interpolated)
    {
        var n = s.Length;
        var quotes = 0;
        while (q + quotes < n && s[q + quotes] == '"') quotes++;
        if (quotes >= 3)
        {
            for (var k = q; k < q + quotes; k++) o[k] = '"';
            var i = q + quotes;
            while (i < n)
            {
                var run = 0;
                while (i + run < n && s[i + run] == '"') run++;
                if (run >= quotes) { for (var k = i; k < i + run; k++) o[k] = '"'; return i + run; }
                if (run > 0) { for (var k = i; k < i + run; k++) o[k] = ' '; i += run; continue; }
                o[i] = s[i] == '\n' ? '\n' : ' '; i++;
            }
            return n;
        }
        if (quotes == 2 && !(verbatim && q + 2 < n && s[q + 2] == '"')) { o[q + 1] = '"'; return q + 2; } // 빈 문자열 ""
        var p = q + 1;
        while (p < n)
        {
            var c = s[p];
            if (!verbatim && c == '\\') { o[p] = ' '; if (p + 1 < n) o[p + 1] = ' '; p += 2; continue; }
            if (c == '"')
            {
                if (verbatim && p + 1 < n && s[p + 1] == '"') { o[p] = o[p + 1] = ' '; p += 2; continue; }
                o[p] = '"'; return p + 1;
            }
            if (!verbatim && c == '\n') { o[p] = '\n'; return p + 1; }
            if (interpolated && c == '{')
            {
                if (p + 1 < n && s[p + 1] == '{') { o[p] = o[p + 1] = ' '; p += 2; continue; }
                var depth = 1; o[p++] = ' ';
                while (p < n && depth > 0)
                {
                    var h = s[p];
                    if (h == '"') { o[p] = ' '; p = ScanNested(s, o, p); continue; }
                    if (h == '{') depth++; else if (h == '}') depth--;
                    o[p] = h == '\n' ? '\n' : ' '; p++;
                }
                continue;
            }
            o[p] = c == '\n' ? '\n' : ' '; p++;
        }
        return n;
    }

    private static int ScanNested(string s, char[] o, int q)
    {
        var p = q + 1;
        while (p < s.Length)
        {
            if (s[p] == '\\') { o[p] = ' '; if (p + 1 < s.Length) o[p + 1] = ' '; p += 2; continue; }
            o[p] = ' ';
            if (s[p++] == '"') return p;
        }
        return p;
    }

    private static int[] LineStarts(string s)
    {
        var l = new List<int>(s.Length / 32 + 1) { 0 };
        for (var i = 0; i < s.Length; i++) if (s[i] == '\n') l.Add(i + 1);
        return l.ToArray();
    }

    private static int LineOf(int[] starts, int pos)
    {
        var k = Array.BinarySearch(starts, pos);
        return (k >= 0 ? k : ~k - 1) + 1;
    }

    private static int CountLines(string s) { var c = 1; foreach (var ch in s) if (ch == '\n') c++; return c; }

    /// <summary>동적 프로브: 워밍업 1회 후 iterations회 호출 — 현재 스레드 할당 바이트/호출 · ms/호출.</summary>
    public AllocationProbe ProbeAllocations(string name, Action action, int iterations = 64)
    {
        action();
        var b0 = GC.GetAllocatedBytesForCurrentThread();
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var k = 0; k < iterations; k++) action();
        var ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        var bytes = (GC.GetAllocatedBytesForCurrentThread() - b0) / (double)Math.Max(1, iterations);
        var r = new AllocationProbe(name, bytes, ms / Math.Max(1, iterations), bytes < 1d);
        lock (_gate) { if (_probeCount < MaxProbes) _probes[_probeCount++] = r; else _probes[MaxProbes - 1] = r; }
        return r;
    }

    /// <summary>소스 루트: 환경변수 PROMPTFLOW_SOURCE_ROOT → 실행 경로 상향 탐색(PromptFlowStudio.csproj).</summary>
    public static string? FindSourceRoot(string? start = null)
    {
        var env = Environment.GetEnvironmentVariable("PROMPTFLOW_SOURCE_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;
        var d = new DirectoryInfo(start ?? AppContext.BaseDirectory);
        for (var k = 0; d is not null && k < 10; k++, d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "PromptFlowStudio.csproj"))) return d.FullName;
            var sub = Path.Combine(d.FullName, "PromptFlowStudio");
            if (File.Exists(Path.Combine(sub, "PromptFlowStudio.csproj"))) return sub;
        }
        return null;
    }

    /// <summary>소스 열거(.cs · .razor) — bin/obj/publish/release 제외.</summary>
    public static List<string> EnumerateSources(string root)
    {
        var list = new List<string>();
        var stack = new Stack<string>(); stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var nm = Path.GetFileName(sub);
                    if (nm.Equals("bin", StringComparison.OrdinalIgnoreCase) || nm.Equals("obj", StringComparison.OrdinalIgnoreCase) || nm.Equals("publish", StringComparison.OrdinalIgnoreCase)
                        || nm.Equals("release", StringComparison.OrdinalIgnoreCase) || nm.StartsWith('.') || nm.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
                    stack.Push(sub);
                }
                foreach (var f in Directory.EnumerateFiles(dir))
                    if (f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)) list.Add(f);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { PromptFlowStudio.Program.Log("CloudVerify.Enumerate", ex); }
        }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    /// <summary>전수 스캔(취소 가능 · 2패스): 값 형식 학습 → 분석. onFile(상대 경로, 원문)으로 마크업 인벤토리 결선.</summary>
    public async Task<int> ScanDirectoryAsync(string root, CancellationToken ct, Action<string, string>? onFile = null)
    {
        Reset();
        Root = root;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var files = EnumerateSources(root);
        FilesTotal = files.Count;
        var texts = new string?[files.Count];
        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try { texts[i] = await File.ReadAllTextAsync(files[i], ct).ConfigureAwait(false); LearnValueTypes(texts[i]!); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { PromptFlowStudio.Program.Log("CloudVerify.Read", ex); }
        }
        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (texts[i] is not { } t) continue;
            var rel = Path.GetRelativePath(root, files[i]).Replace('\\', '/');
            AnalyzeSource(rel, t);
            try { onFile?.Invoke(rel, t); } catch (Exception ex) { PromptFlowStudio.Program.Log("CloudVerify.OnFile", ex); }
            if ((i & 31) == 0) await Task.Yield();
        }
        LastScanMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        return files.Count;
    }
}
