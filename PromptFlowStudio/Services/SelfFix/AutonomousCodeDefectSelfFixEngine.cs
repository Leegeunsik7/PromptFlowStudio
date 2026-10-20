using System.Text;
using System.Text.RegularExpressions;
using PromptFlowStudio.Services.CloudVerification;

namespace PromptFlowStudio.Services.SelfFix;

/// <summary>삽입형 패치(원문 오프셋 기준 · 삭제 없음): Before/After = 해당 줄 원문/수정본(Diff 프리뷰).</summary>
public readonly record struct SourcePatch(string File, int Line, int Offset, VerifyRule Rule, string Insert, string Anchor, string Before, string After);

/// <summary>자동 수정 불가(의미 변경 위험) 결함 — 수동 조치 권고.</summary>
public readonly record struct FixAdvisory(string File, int Line, VerifyRule Rule, string Member, string Reason);

/// <summary>
/// [PromptFlow Studio v16.93.0] 🛠 자율 C#/Razor 소스 결함 자동 수정 &amp; 패치 엔진.
///   ▸ 구조 인식: v16.92 렉서(<see cref="CloudSourceCodeIntegrityVerifierEngine.Sanitize"/>)로 주석/문자열 제외 · Razor는 @code 이후만 · 위치 = 원문 오프셋(길이 보존).
///   ▸ 자동 수정(의미 보존 · 삽입만): 미관찰 Task 문장(<c>InvokeAsync(…);</c> · <c>Task.Run(…);</c> · <c>Task.Factory.StartNew(…);</c>) → <c>_ = </c> 명시적 폐기.
///     제외: await/return/대입/체인(.ConfigureAwait/.ContinueWith …) · 이미 폐기된 문장 · 제네릭 호출(JS interop) — 재계획 시 0건(멱등).
///   ▸ 권고(자동 수정 금지): 핫패스 보간/문자열/힙/LINQ/박싱 · async void · 동기 대기 · Razor 규칙 — 할당 제거는 설계 변경이 필요해 기계적 치환 시 정합성 훼손.
///   ▸ 적용: 오프셋 내림차순 삽입 + 앵커 재검증(원문 변경 시 해당 패치 생략 = 충돌).
/// </summary>
public sealed class AutonomousCodeDefectSelfFixEngine
{
    public const string Discard = "_ = ";
    private static readonly Regex FixRx = new(@"(?<=[;{}]\s*)(?<call>(?:this\.|Dispatcher\.)?InvokeAsync|Task\.Run|Task\.Factory\.StartNew)\s*\(", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly object _gate = new();
    private readonly List<SourcePatch> _patches = new();
    private readonly List<FixAdvisory> _advisories = new();

    public int PatchCount { get { lock (_gate) return _patches.Count; } }
    public SourcePatch Patch(int i) { lock (_gate) return _patches[i]; }
    public int AdvisoryCount { get { lock (_gate) return _advisories.Count; } }
    public FixAdvisory Advisory(int i) { lock (_gate) return _advisories[i]; }
    public int FilesScanned { get; private set; }
    public int FilesWithPatches { get; private set; }
    public double LastPlanMs { get; private set; }
    public string? Root { get; private set; }

    public static string AdvisoryReason(VerifyRule r) => r switch
    {
        VerifyRule.HotPathInterpolation or VerifyRule.HotPathStringAlloc => "핫패스 문자열 생성 — 캐시 문자열/StringBuilder 재사용/Span 포맷으로 수동 전환",
        VerifyRule.HotPathHeapAlloc => "핫패스 힙 할당 — 필드 1회 할당 재사용 또는 ArrayPool",
        VerifyRule.HotPathLinq => "핫패스 LINQ — for 루프 수동 전개",
        VerifyRule.HotPathBoxing => "박싱 — 제네릭/값 형식 오버로드로 수동 전환",
        VerifyRule.AsyncVoid => "async void — 시그니처 변경은 호출부/델리게이트 영향 → 수동(Task 반환 + 예외 격리)",
        VerifyRule.SyncOverAsync => "동기 대기 — 교착 위험, await 전파 필요(수동)",
        VerifyRule.RazorLoopSvgText => "루프 내 SVG text — <g> 래핑(수동 확인)",
        VerifyRule.RazorRangeUnbounded => "range min/max 명시(값 도메인 수동 결정)",
        VerifyRule.RazorDeadButton => "핸들러 없는 버튼 — 의도 확인 후 @onclick 결선",
        VerifyRule.UnobservedTask => "미관찰 Task(사용자 메서드) — 반환 형식 확인 후 수동 폐기/await",
        _ => "수동 검토",
    };

    public void Reset() { lock (_gate) { _patches.Clear(); _advisories.Clear(); } FilesScanned = FilesWithPatches = 0; }

    /// <summary>소스 루트 전수 계획(파일 미수정).</summary>
    public async Task<int> PlanAsync(string root, CancellationToken ct)
    {
        Reset(); Root = root;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var verifier = new CloudSourceCodeIntegrityVerifierEngine();
        var files = CloudSourceCodeIntegrityVerifierEngine.EnumerateSources(root);
        var texts = new string?[files.Count];
        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try { texts[i] = await File.ReadAllTextAsync(files[i], ct).ConfigureAwait(false); verifier.LearnValueTypes(texts[i]!); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { PromptFlowStudio.Program.Log("SelfFix.Plan.Read", ex); }
        }
        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (texts[i] is not { } t) continue;
            PlanFile(Path.GetRelativePath(root, files[i]).Replace('\\', '/'), t, verifier);
            if ((i & 63) == 0) await Task.Yield();
        }
        LastPlanMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        return PatchCount;
    }

    /// <summary>단일 파일 계획 → 패치 수(권고는 verifier 제공 시 수집).</summary>
    public int PlanFile(string path, string text, CloudSourceCodeIntegrityVerifierEngine? verifier = null)
    {
        FilesScanned++;
        var found = Find(path, text);
        lock (_gate) _patches.AddRange(found);
        if (found.Count > 0) FilesWithPatches++;
        if (verifier is not null)
        {
            var before = verifier.FindingCount;
            verifier.AnalyzeSource(path, text);
            for (var i = before; i < verifier.FindingCount; i++)
            {
                var f = verifier.Finding(i);
                if (f.Severity == VerifySeverity.Info) continue;
                if (f.Rule == VerifyRule.UnobservedTask && Covered(found, f.Line)) continue;
                lock (_gate) _advisories.Add(new FixAdvisory(path, f.Line, f.Rule, f.Member, AdvisoryReason(f.Rule)));
            }
        }
        return found.Count;
    }

    private static bool Covered(List<SourcePatch> ps, int line) { foreach (var p in ps) if (p.Line == line) return true; return false; }

    /// <summary>순수 탐지(상태 비변경): 미관찰 Task 문장 → 삽입 패치.</summary>
    public static List<SourcePatch> Find(string path, string text)
    {
        var list = new List<SourcePatch>();
        var code = CloudSourceCodeIntegrityVerifierEngine.Sanitize(text, null);
        var from = 0;
        if (path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)) { from = code.IndexOf("@code", StringComparison.Ordinal); if (from < 0) return list; }
        foreach (Match m in FixRx.Matches(code, from))
        {
            var open = m.Index + m.Length - 1;
            if (CloudSourceCodeIntegrityVerifierEngine.ChainedAfterCall(code, open)) continue;
            if (!StatementEnds(code, open)) continue;
            var (ls, le) = LineBounds(text, m.Index);
            var line = 1; for (var k = 0; k < ls; k++) if (text[k] == '\n') line++;
            var before = text[ls..le].TrimEnd('\r');
            var after = before.Insert(m.Index - ls, Discard);
            list.Add(new SourcePatch(path, line, m.Index, VerifyRule.UnobservedTask, Discard, m.Groups["call"].Value, before, after));
        }
        return list;
    }

    /// <summary>호출 닫힘 직후 ';' (문장 종결) 확인 — 식 일부(인자/조건) 제외.</summary>
    private static bool StatementEnds(string code, int open)
    {
        var depth = 0;
        for (var k = open; k < code.Length; k++)
        {
            if (code[k] == '(') depth++;
            else if (code[k] == ')' && --depth == 0)
            {
                var e = k + 1;
                while (e < code.Length && char.IsWhiteSpace(code[e])) e++;
                return e < code.Length && code[e] == ';';
            }
        }
        return false;
    }

    private static (int Start, int End) LineBounds(string s, int pos)
    {
        var a = s.LastIndexOf('\n', Math.Max(0, pos - 1)); a = a < 0 ? 0 : a + 1;
        var b = s.IndexOf('\n', pos); if (b < 0) b = s.Length;
        return (a, b);
    }

    /// <summary>파일 패치 적용(오프셋 내림차순 · 앵커 재검증) → (결과, 적용 수, 충돌 수).</summary>
    public static (string Text, int Applied, int Conflicts) ApplyToText(string text, IReadOnlyList<SourcePatch> patches)
    {
        var order = new List<SourcePatch>(patches);
        order.Sort(static (a, b) => b.Offset.CompareTo(a.Offset));
        var sb = new StringBuilder(text);
        int applied = 0, conflicts = 0;
        foreach (var p in order)
        {
            var ok = p.Offset >= 0 && p.Offset + p.Anchor.Length <= text.Length && string.CompareOrdinal(text, p.Offset, p.Anchor, 0, p.Anchor.Length) == 0
                     && !(p.Offset >= p.Insert.Length && string.CompareOrdinal(text, p.Offset - p.Insert.Length, p.Insert, 0, p.Insert.Length) == 0);
            if (!ok) { conflicts++; continue; }
            sb.Insert(p.Offset, p.Insert); applied++;
        }
        return (sb.ToString(), applied, conflicts);
    }

    /// <summary>Diff 프리뷰(통합 형식 · 상한).</summary>
    public string BuildDiff(int max = 60)
    {
        var sb = new StringBuilder(4096);
        string? file = null;
        lock (_gate)
        {
            for (var i = 0; i < _patches.Count && i < max; i++)
            {
                var p = _patches[i];
                if (p.File != file) { file = p.File; sb.Append("--- a/").Append(file).Append('\n').Append("+++ b/").Append(file).Append('\n'); }
                sb.Append("@@ ").Append(p.Line).Append(" @@\n- ").Append(p.Before.Trim()).Append("\n+ ").Append(p.After.Trim()).Append('\n');
            }
            if (_patches.Count > max) sb.Append("… +").Append(_patches.Count - max).Append(" patches\n");
        }
        return sb.ToString();
    }

    /// <summary>계획된 패치 스냅샷(파일별 그룹 · 순서 보존).</summary>
    public Dictionary<string, List<SourcePatch>> GroupByFile()
    {
        var d = new Dictionary<string, List<SourcePatch>>(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
            foreach (var p in _patches)
            {
                if (!d.TryGetValue(p.File, out var l)) d[p.File] = l = new List<SourcePatch>();
                l.Add(p);
            }
        return d;
    }
}
