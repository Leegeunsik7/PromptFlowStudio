using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;

namespace PromptFlowStudio.Services.GlobalOps;

/// <summary>핫 로드 플러그인 계약(공유 어셈블리 타입 — 기본 컨텍스트로 위임 해석).</summary>
public interface IHotPipelineModule
{
    string Name { get; }
    double Score(ReadOnlySpan<double> features);
    string RewritePrompt(string prompt);
}

public enum HotModuleKind : byte { ScoreExpression, PromptRules, Assembly }

public enum HotSwapOutcome : byte { Activated, RejectedCompile, RejectedCanary, RejectedSignature, RolledBack }

/// <summary>활성 모듈 스냅샷(불변 — 원자 교체 단위).</summary>
public sealed class HotModuleVersion
{
    internal HotModuleVersion(string slot, int version, HotModuleKind kind, string source, Func<double[], double>? score, Func<string, string>? rewrite, IHotPipelineModule? plugin, AssemblyLoadContext? alc, string sha256)
    { Slot = slot; Version = version; Kind = kind; Source = source; ScoreFn = score; RewriteFn = rewrite; Plugin = plugin; Context = alc; Sha256 = sha256; }

    public string Slot { get; }
    public int Version { get; }
    public HotModuleKind Kind { get; }
    public string Source { get; }
    public string Sha256 { get; }
    public DateTimeOffset ActivatedAt { get; internal set; }
    internal Func<double[], double>? ScoreFn { get; }
    internal Func<string, string>? RewriteFn { get; }
    internal IHotPipelineModule? Plugin { get; }
    internal AssemblyLoadContext? Context { get; }
}

/// <summary>교체 이력 항목.</summary>
public sealed record HotSwapRecord(DateTimeOffset At, string Slot, int Version, HotModuleKind Kind, HotSwapOutcome Outcome, string Detail);

/// <summary>카나리 검증 벡터(입력 → 기대 범위).</summary>
public readonly record struct CanaryCase(double[] Features, double Min, double Max);

/// <summary>
/// [PromptFlow Studio v16.52.0] ♻ 실시간 가동 모듈 라이브 핫 리로딩 트랜스파일러.
///   ▸ 점수 식 DSL → 토크나이저 · 재귀 하강 파서 → System.Linq.Expressions 트리 → 컴파일 델리게이트(변수 = 피처 인덱스 · + - * / ^ · 비교 · 삼항 · min/max/clamp/abs/sqrt/log/exp/sigmoid) — Roslyn 없이 BCL 트랜스파일.
///   ▸ 프롬프트 규칙 DSL(when "키워드" [and/not] → append/prepend/replace "a" with "b"/block) → 순차 규칙 함수.
///   ▸ 어셈블리 핫 스왑: 수집 가능 AssemblyLoadContext · SHA-256 허용 목록 또는 HMAC 서명 필수 · IHotPipelineModule 탐색 · 구 컨텍스트 Unload.
///   ▸ 무중단 교체: 컴파일 → 카나리(범위 · NaN/∞ 거부) → Interlocked.Exchange 원자 교체 · 버전 이력 · 1단계 롤백 · 슬롯별 실행 무할당(재사용 피처 버퍼).
/// </summary>
public sealed class LiveProductionHotReloadingEngine : IDisposable
{
    public const int MaxFeatures = 16, HistoryCap = 200;
    public const string AssemblyKeyName = "hot_reload_assembly_hmac";

    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly List<HotSwapRecord> _history = new();
    private readonly HashSet<string> _allowedSha = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<byte[]>? _key;
    private readonly object _gate = new();
    private long _swaps, _rejects, _rollbacks, _calls;

    private sealed class Slot
    {
        public HotModuleVersion? Current, Previous;
        public readonly double[] Buffer = new double[MaxFeatures];
        public string[] Features = Array.Empty<string>();
        public int NextVersion = 1;
    }

    public LiveProductionHotReloadingEngine(IServiceProvider sp)
        : this(sp.GetService(typeof(Security.SecureSecretVault)) is Security.SecureSecretVault v ? () => v.ResolveOrCreate(AssemblyKeyName) : null) { }

    public LiveProductionHotReloadingEngine(Func<byte[]>? assemblyKey = null)
    {
        _key = assemblyKey;
        Swap("engagement", HotModuleKind.ScoreExpression, DefaultScoreSource, DefaultFeatures, null, DateTimeOffset.UtcNow);
        Swap("prompt", HotModuleKind.PromptRules, DefaultPromptRules, Array.Empty<string>(), null, DateTimeOffset.UtcNow);
    }

    public event Action? OnChange;

    public static readonly string[] DefaultFeatures = { "ctr", "retention", "cost", "latency" };
    public const string DefaultScoreSource = "clamp(0.55*ctr + 0.45*retention - 0.1*cost - 0.002*latency, 0, 1)";
    public const string DefaultPromptRules = "when \"night\" -> append \", cinematic moody lighting\"\nwhen \"logo\" and not \"brand\" -> append \", no trademarked logos\"";

    public long Swaps => Interlocked.Read(ref _swaps);
    public long Rejects => Interlocked.Read(ref _rejects);
    public long Rollbacks => Interlocked.Read(ref _rollbacks);
    public long Calls => Interlocked.Read(ref _calls);
    public IReadOnlyList<HotSwapRecord> History { get { lock (_gate) return _history.ToArray(); } }
    public string[] SlotNames { get { lock (_gate) return _slots.Keys.ToArray(); } }
    public HotModuleVersion? Current(string slot) { lock (_gate) return _slots.TryGetValue(slot, out var s) ? Volatile.Read(ref s.Current) : null; }
    public HotModuleVersion? Previous(string slot) { lock (_gate) return _slots.TryGetValue(slot, out var s) ? s.Previous : null; }
    public string[] FeaturesOf(string slot) { lock (_gate) return _slots.TryGetValue(slot, out var s) ? s.Features : Array.Empty<string>(); }
    public string? LastAction { get; private set; }

    public void AllowAssemblySha256(string hex) { lock (_gate) _allowedSha.Add(hex); }

    // ══════════════════════ 실행(핫패스) ══════════════════════

    /// <summary>슬롯 점수 실행 — 무할당(슬롯 버퍼 재사용 · 스레드당 1슬롯 호출 가정 시 lock-free 읽기).</summary>
    public double Score(string slot, ReadOnlySpan<double> features)
    {
        Slot s;
        lock (_gate) { if (!_slots.TryGetValue(slot, out s!)) return double.NaN; }
        var m = Volatile.Read(ref s.Current);
        if (m is null) return double.NaN;
        Interlocked.Increment(ref _calls);
        if (m.Plugin is { } p) return p.Score(features);
        if (m.ScoreFn is null) return double.NaN;
        lock (s.Buffer)
        {
            var n = Math.Min(features.Length, MaxFeatures);
            features[..n].CopyTo(s.Buffer);
            if (n < MaxFeatures) Array.Clear(s.Buffer, n, MaxFeatures - n);
            return m.ScoreFn(s.Buffer);
        }
    }

    public string Rewrite(string slot, string prompt)
    {
        var m = Current(slot);
        if (m is null) return prompt;
        Interlocked.Increment(ref _calls);
        return m.Plugin is { } p ? p.RewritePrompt(prompt) : m.RewriteFn is { } f ? f(prompt) : prompt;
    }

    // ══════════════════════ 교체 ══════════════════════

    /// <summary>DSL 소스 핫 스왑(컴파일 → 카나리 → 원자 교체).</summary>
    public HotSwapOutcome Swap(string slot, HotModuleKind kind, string source, IReadOnlyList<string> features, IReadOnlyList<CanaryCase>? canary, DateTimeOffset now)
    {
        if (kind == HotModuleKind.Assembly) throw new ArgumentException("어셈블리는 SwapAssembly를 사용하세요.", nameof(kind));
        if (kind == HotModuleKind.ScoreExpression && features.Count > MaxFeatures) return Reject(slot, kind, HotSwapOutcome.RejectedCompile, "피처 상한 초과", now);
        Func<double[], double>? score = null; Func<string, string>? rewrite = null;
        try
        {
            if (kind == HotModuleKind.ScoreExpression) score = CompileScore(source, features);
            else rewrite = CompilePromptRules(source);
        }
        catch (FormatException ex) { return Reject(slot, kind, HotSwapOutcome.RejectedCompile, ex.Message, now); }
        int ver;
        lock (_gate) { ver = GetSlot(slot).NextVersion; }
        var mod = new HotModuleVersion(slot, ver, kind, source, score, rewrite, null, null, Sha(source));
        if (score is not null && canary is not null && !RunCanary(score, canary, out var why)) return Reject(slot, kind, HotSwapOutcome.RejectedCanary, why, now);
        Activate(slot, mod, features.ToArray(), now);
        return HotSwapOutcome.Activated;
    }

    /// <summary>어셈블리 핫 스왑: 허용 SHA-256 또는 HMAC 서명 검증 → 수집 가능 ALC 로드 → 모듈 탐색 → 원자 교체 → 구 컨텍스트 Unload.</summary>
    public HotSwapOutcome SwapAssembly(string slot, string path, string? hmacHex, string? typeName, DateTimeOffset now)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Reject(slot, HotModuleKind.Assembly, HotSwapOutcome.RejectedCompile, ex.Message, now); }
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        bool trusted;
        lock (_gate) trusted = _allowedSha.Contains(sha);
        if (!trusted && hmacHex is not null && _key is not null)
        {
            try
            {
                var got = Convert.FromHexString(hmacHex);
                var expect = HMACSHA256.HashData(_key(), bytes);
                trusted = got.Length == expect.Length && CryptographicOperations.FixedTimeEquals(got, expect);
            }
            catch (FormatException) { trusted = false; }
        }
        if (!trusted) return Reject(slot, HotModuleKind.Assembly, HotSwapOutcome.RejectedSignature, "서명/허용 목록 불일치 · sha256 " + sha[..12], now);
        var alc = new AssemblyLoadContext("pfs-hot-" + slot + "-" + sha[..8], isCollectible: true);
        try
        {
            Assembly asm;
            using (var ms = new MemoryStream(bytes)) asm = alc.LoadFromStream(ms);
            Type? t = null;
            foreach (var x in SafeTypes(asm))
                if (!x.IsAbstract && typeof(IHotPipelineModule).IsAssignableFrom(x) && x.GetConstructor(Type.EmptyTypes) is not null && (typeName is null || x.FullName == typeName || x.Name == typeName)) { t = x; break; }
            if (t is null) { alc.Unload(); return Reject(slot, HotModuleKind.Assembly, HotSwapOutcome.RejectedCompile, "IHotPipelineModule 구현 없음", now); }
            var plugin = (IHotPipelineModule)Activator.CreateInstance(t)!;
            Span<double> probe = stackalloc double[MaxFeatures];
            var v = plugin.Score(probe);
            if (double.IsNaN(v) || double.IsInfinity(v)) { alc.Unload(); return Reject(slot, HotModuleKind.Assembly, HotSwapOutcome.RejectedCanary, "카나리 NaN/∞", now); }
            int ver;
            lock (_gate) ver = GetSlot(slot).NextVersion;
            Activate(slot, new HotModuleVersion(slot, ver, HotModuleKind.Assembly, plugin.Name + " @ " + Path.GetFileName(path), null, null, plugin, alc, sha), FeaturesOf(slot), now);
            return HotSwapOutcome.Activated;
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or TargetInvocationException or MissingMethodException or InvalidCastException)
        {
            alc.Unload();
            return Reject(slot, HotModuleKind.Assembly, HotSwapOutcome.RejectedCompile, ex.GetType().Name + ": " + ex.Message, now);
        }
    }

    public string SignAssembly(string path) => _key is null ? throw new InvalidOperationException("서명 키 없음") : Convert.ToHexString(HMACSHA256.HashData(_key(), File.ReadAllBytes(path))).ToLowerInvariant();

    /// <summary>직전 버전으로 1단계 롤백.</summary>
    public bool Rollback(string slot, DateTimeOffset now)
    {
        HotModuleVersion? prev, cur;
        lock (_gate)
        {
            if (!_slots.TryGetValue(slot, out var s) || s.Previous is null) return false;
            prev = s.Previous; cur = s.Current;
            Volatile.Write(ref s.Current, prev);
            s.Previous = null;
            Record(new HotSwapRecord(now, slot, prev.Version, prev.Kind, HotSwapOutcome.RolledBack, "v" + cur?.Version.ToString(CultureInfo.InvariantCulture) + " → v" + prev.Version.ToString(CultureInfo.InvariantCulture)));
        }
        if (cur?.Context is { } ctx && !ReferenceEquals(ctx, prev.Context)) ctx.Unload();
        Interlocked.Increment(ref _rollbacks);
        LastAction = "↩ 롤백 · " + slot + " v" + prev.Version.ToString(CultureInfo.InvariantCulture);
        Raise();
        return true;
    }

    private void Activate(string slot, HotModuleVersion mod, string[] features, DateTimeOffset now)
    {
        AssemblyLoadContext? unload = null;
        lock (_gate)
        {
            var s = GetSlot(slot);
            mod.ActivatedAt = now;
            var old = Interlocked.Exchange(ref s.Current, mod);
            if (s.Previous?.Context is { } pc && !ReferenceEquals(pc, old?.Context)) unload = pc;   // 2단계 이전 컨텍스트 해제
            s.Previous = old;
            if (mod.Kind != HotModuleKind.Assembly) s.Features = features;
            s.NextVersion = mod.Version + 1;
            Record(new HotSwapRecord(now, slot, mod.Version, mod.Kind, HotSwapOutcome.Activated, mod.Source.Length > 80 ? mod.Source[..80] + "…" : mod.Source));
        }
        unload?.Unload();
        Interlocked.Increment(ref _swaps);
        LastAction = "♻ 핫 스왑 · " + slot + " v" + mod.Version.ToString(CultureInfo.InvariantCulture) + " (" + KindLabel(mod.Kind) + ")";
        Raise();
    }

    private HotSwapOutcome Reject(string slot, HotModuleKind kind, HotSwapOutcome o, string why, DateTimeOffset now)
    {
        lock (_gate) Record(new HotSwapRecord(now, slot, 0, kind, o, why));
        Interlocked.Increment(ref _rejects);
        LastAction = "⛔ 교체 거부 · " + slot + " · " + why;
        Raise();
        return o;
    }

    private Slot GetSlot(string slot) { if (!_slots.TryGetValue(slot, out var s)) { s = new Slot(); _slots[slot] = s; } return s; }

    private void Record(HotSwapRecord r) { _history.Add(r); if (_history.Count > HistoryCap) _history.RemoveAt(0); }

    public static bool RunCanary(Func<double[], double> f, IReadOnlyList<CanaryCase> cases, out string why)
    {
        var buf = new double[MaxFeatures];
        for (var i = 0; i < cases.Count; i++)
        {
            Array.Clear(buf);
            cases[i].Features.AsSpan(0, Math.Min(cases[i].Features.Length, MaxFeatures)).CopyTo(buf);
            var v = f(buf);
            if (double.IsNaN(v) || double.IsInfinity(v) || v < cases[i].Min || v > cases[i].Max)
            {
                why = "카나리 #" + (i + 1).ToString(CultureInfo.InvariantCulture) + " = " + v.ToString("0.####", CultureInfo.InvariantCulture) + " ∉ [" + cases[i].Min.ToString(CultureInfo.InvariantCulture) + ", " + cases[i].Max.ToString(CultureInfo.InvariantCulture) + "]";
                return false;
            }
        }
        why = "";
        return true;
    }

    private static Type[] SafeTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null).ToArray()!; }
    }

    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    // ══════════════════════ 점수 식 트랜스파일러 ══════════════════════

    /// <summary>식 → 식 트리 → 델리게이트. 오류 시 FormatException(위치 포함).</summary>
    public static Func<double[], double> CompileScore(string source, IReadOnlyList<string> features)
    {
        var p = new ExprParser(source, features);
        var body = p.ParseAll();
        return Expression.Lambda<Func<double[], double>>(body, p.Input).Compile();
    }

    /// <summary>식 트리 문자열(트랜스파일 결과 미리보기).</summary>
    public static string Transpile(string source, IReadOnlyList<string> features)
    {
        var p = new ExprParser(source, features);
        return p.ParseAll().ToString();
    }

    private sealed class ExprParser
    {
        private readonly string _s;
        private readonly IReadOnlyList<string> _features;
        private int _i;
        public readonly ParameterExpression Input = Expression.Parameter(typeof(double[]), "f");

        public ExprParser(string s, IReadOnlyList<string> features) { _s = s; _features = features; }

        public Expression ParseAll()
        {
            if (_s.Length > 2000) throw new FormatException("식이 너무 깁니다(2000자 상한).");
            var e = Ternary(0);
            Skip();
            if (_i < _s.Length) throw Err("예상치 못한 문자 '" + _s[_i] + "'");
            return e;
        }

        private FormatException Err(string m) => new(m + " (위치 " + _i.ToString(CultureInfo.InvariantCulture) + ")");

        private void Skip() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; }

        private bool Eat(string tok) { Skip(); if (string.CompareOrdinal(_s, _i, tok, 0, tok.Length) == 0) { _i += tok.Length; return true; } return false; }

        private Expression Ternary(int depth)
        {
            if (depth > 64) throw Err("중첩 상한 초과");
            var c = Compare(depth);
            if (!Eat("?")) return c;
            var a = Ternary(depth + 1);
            if (!Eat(":")) throw Err("':' 누락");
            var b = Ternary(depth + 1);
            return Expression.Condition(Expression.NotEqual(c, Expression.Constant(0.0)), a, b);
        }

        private Expression Compare(int d)
        {
            var l = Add(d);
            while (true)
            {
                Func<Expression, Expression, BinaryExpression>? op =
                    Eat(">=") ? Expression.GreaterThanOrEqual : Eat("<=") ? Expression.LessThanOrEqual : Eat("==") ? Expression.Equal : Eat("!=") ? Expression.NotEqual
                    : Eat(">") ? Expression.GreaterThan : Eat("<") ? Expression.LessThan : null;
                if (op is null) return l;
                var r = Add(d);
                l = Expression.Condition(op(l, r), Expression.Constant(1.0), Expression.Constant(0.0));
            }
        }

        private Expression Add(int d)
        {
            var l = Mul(d);
            while (true)
            {
                if (Eat("+")) l = Expression.Add(l, Mul(d));
                else if (Eat("-")) l = Expression.Subtract(l, Mul(d));
                else return l;
            }
        }

        private Expression Mul(int d)
        {
            var l = Pow(d);
            while (true)
            {
                if (Eat("*")) l = Expression.Multiply(l, Pow(d));
                else if (Eat("/")) l = Expression.Divide(l, Pow(d));
                else return l;
            }
        }

        private Expression Pow(int d)
        {
            var b = Unary(d);
            return Eat("^") ? Expression.Call(typeof(Math).GetMethod(nameof(Math.Pow))!, b, Pow(d + 1)) : b;
        }

        private Expression Unary(int d)
        {
            if (d > 64) throw Err("중첩 상한 초과");
            if (Eat("-")) return Expression.Negate(Unary(d + 1));
            if (Eat("+")) return Unary(d + 1);
            return Primary(d);
        }

        private Expression Primary(int d)
        {
            Skip();
            if (_i >= _s.Length) throw Err("식이 끝났습니다");
            if (Eat("(")) { var e = Ternary(d + 1); if (!Eat(")")) throw Err("')' 누락"); return e; }
            var c = _s[_i];
            if (char.IsAsciiDigit(c) || c == '.')
            {
                var st = _i;
                while (_i < _s.Length && (char.IsAsciiDigit(_s[_i]) || _s[_i] is '.' or 'e' or 'E' || (_s[_i] is '+' or '-') && _i > st && _s[_i - 1] is 'e' or 'E')) _i++;
                if (!double.TryParse(_s.AsSpan(st, _i - st), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) throw Err("숫자 형식 오류");
                return Expression.Constant(v);
            }
            if (char.IsLetter(c) || c == '_')
            {
                var st = _i;
                while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_')) _i++;
                var id = _s[st.._i];
                if (Eat("("))
                {
                    var args = new List<Expression>();
                    if (!Eat(")"))
                    {
                        do args.Add(Ternary(d + 1)); while (Eat(","));
                        if (!Eat(")")) throw Err("')' 누락");
                    }
                    return Call(id, args);
                }
                if (id == "pi") return Expression.Constant(Math.PI);
                for (var k = 0; k < _features.Count; k++)
                    if (string.Equals(_features[k], id, StringComparison.Ordinal))
                        return Expression.ArrayIndex(Input, Expression.Constant(k));
                throw Err("알 수 없는 변수 '" + id + "'");
            }
            throw Err("예상치 못한 문자 '" + c + "'");
        }

        private Expression Call(string id, List<Expression> a)
        {
            static MethodInfo M(string n, int k) => typeof(Math).GetMethods().First(m => m.Name == n && m.GetParameters().Length == k && m.GetParameters()[0].ParameterType == typeof(double));
            void Need(int k) { if (a.Count != k) throw Err(id + "() 인자 " + k.ToString(CultureInfo.InvariantCulture) + "개 필요"); }
            switch (id)
            {
                case "min": Need(2); return Expression.Call(M("Min", 2), a[0], a[1]);
                case "max": Need(2); return Expression.Call(M("Max", 2), a[0], a[1]);
                case "clamp": Need(3); return Expression.Call(M("Min", 2), Expression.Call(M("Max", 2), a[0], a[1]), a[2]);
                case "abs": Need(1); return Expression.Call(M("Abs", 1), a[0]);
                case "sqrt": Need(1); return Expression.Call(M("Sqrt", 1), a[0]);
                case "log": Need(1); return Expression.Call(M("Log", 1), a[0]);
                case "exp": Need(1); return Expression.Call(M("Exp", 1), a[0]);
                case "sigmoid": Need(1); return Expression.Divide(Expression.Constant(1.0), Expression.Add(Expression.Constant(1.0), Expression.Call(M("Exp", 1), Expression.Negate(a[0]))));
                default: throw Err("알 수 없는 함수 '" + id + "'");
            }
        }
    }

    // ══════════════════════ 프롬프트 규칙 DSL ══════════════════════

    private enum RuleAction : byte { Append, Prepend, Replace, Block }

    private readonly record struct PromptRule(string[] All, string[] None, RuleAction Action, string A, string B);

    /// <summary>규칙: when "kw" [and "kw2"] [and not "kw3"] -> append "x" | prepend "x" | replace "a" with "b" | block. 줄 단위 · # 주석.</summary>
    public static Func<string, string> CompilePromptRules(string source)
    {
        var rules = new List<PromptRule>();
        var lineNo = 0;
        foreach (var raw in source.Split('\n'))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var arrow = line.IndexOf("->", StringComparison.Ordinal);
            if (!line.StartsWith("when ", StringComparison.Ordinal) || arrow < 0) throw new FormatException(lineNo.ToString(CultureInfo.InvariantCulture) + "행: 'when … -> …' 형식이 아닙니다.");
            var cond = line[5..arrow]; var act = line[(arrow + 2)..].Trim();
            var all = new List<string>(); var none = new List<string>();
            var pos = 0; var neg = false;
            while (true)
            {
                var q = NextQuoted(cond, ref pos, lineNo);
                if (q is null) break;
                (neg ? none : all).Add(q.ToLowerInvariant());
                var rest = cond[pos..].TrimStart();
                if (rest.StartsWith("and not ", StringComparison.Ordinal)) { neg = true; pos = cond.Length - rest.Length + 8; }
                else if (rest.StartsWith("and ", StringComparison.Ordinal)) { neg = false; pos = cond.Length - rest.Length + 4; }
                else if (rest.Length == 0) break;
                else throw new FormatException(lineNo.ToString(CultureInfo.InvariantCulture) + "행: 조건 연결은 and / and not 만 허용");
            }
            if (all.Count == 0) throw new FormatException(lineNo.ToString(CultureInfo.InvariantCulture) + "행: 키워드 없음");
            var ap = 0;
            if (act.StartsWith("append ", StringComparison.Ordinal)) { ap = 7; rules.Add(new PromptRule(all.ToArray(), none.ToArray(), RuleAction.Append, NextQuoted(act, ref ap, lineNo) ?? throw Bad(lineNo), "")); }
            else if (act.StartsWith("prepend ", StringComparison.Ordinal)) { ap = 8; rules.Add(new PromptRule(all.ToArray(), none.ToArray(), RuleAction.Prepend, NextQuoted(act, ref ap, lineNo) ?? throw Bad(lineNo), "")); }
            else if (act.StartsWith("replace ", StringComparison.Ordinal))
            {
                ap = 8;
                var a = NextQuoted(act, ref ap, lineNo) ?? throw Bad(lineNo);
                var rest = act[ap..].TrimStart();
                if (!rest.StartsWith("with ", StringComparison.Ordinal)) throw Bad(lineNo);
                ap = act.Length - rest.Length + 5;
                rules.Add(new PromptRule(all.ToArray(), none.ToArray(), RuleAction.Replace, a, NextQuoted(act, ref ap, lineNo) ?? throw Bad(lineNo)));
            }
            else if (act == "block") rules.Add(new PromptRule(all.ToArray(), none.ToArray(), RuleAction.Block, "", ""));
            else throw Bad(lineNo);
            if (rules.Count > 256) throw new FormatException("규칙 상한(256) 초과");
        }
        var compiled = rules.ToArray();
        return prompt =>
        {
            var lower = prompt.ToLowerInvariant();
            var cur = prompt;
            foreach (var r in compiled)
            {
                var hit = true;
                foreach (var k in r.All) if (!lower.Contains(k, StringComparison.Ordinal)) { hit = false; break; }
                if (hit) foreach (var k in r.None) if (lower.Contains(k, StringComparison.Ordinal)) { hit = false; break; }
                if (!hit) continue;
                cur = r.Action switch
                {
                    RuleAction.Append => cur.EndsWith(r.A, StringComparison.Ordinal) ? cur : cur + r.A,
                    RuleAction.Prepend => cur.StartsWith(r.A, StringComparison.Ordinal) ? cur : r.A + cur,
                    RuleAction.Replace => cur.Replace(r.A, r.B, StringComparison.OrdinalIgnoreCase),
                    _ => "",
                };
                if (r.Action == RuleAction.Block) return "";
                lower = cur.ToLowerInvariant();
            }
            return cur;
        };
    }

    private static FormatException Bad(int line) => new(line.ToString(CultureInfo.InvariantCulture) + "행: 동작은 append \"…\" / prepend \"…\" / replace \"a\" with \"b\" / block");

    private static string? NextQuoted(string s, ref int pos, int line)
    {
        var a = s.IndexOf('"', pos);
        if (a < 0) return null;
        var b = s.IndexOf('"', a + 1);
        if (b < 0) throw new FormatException(line.ToString(CultureInfo.InvariantCulture) + "행: 따옴표가 닫히지 않았습니다.");
        pos = b + 1;
        return s[(a + 1)..b];
    }

    public static string KindLabel(HotModuleKind k) => k switch { HotModuleKind.ScoreExpression => "점수 식", HotModuleKind.PromptRules => "프롬프트 규칙", _ => "어셈블리" };

    public static string OutcomeLabel(HotSwapOutcome o) => o switch
    {
        HotSwapOutcome.Activated => "활성", HotSwapOutcome.RejectedCompile => "컴파일 거부", HotSwapOutcome.RejectedCanary => "카나리 거부", HotSwapOutcome.RejectedSignature => "서명 거부", _ => "롤백",
    };

    private void Raise() { try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("HotReload.OnChange", ex); } }

    public void Dispose()
    {
        lock (_gate)
            foreach (var s in _slots.Values)
            {
                s.Current?.Context?.Unload();
                if (s.Previous?.Context is { } pc && !ReferenceEquals(pc, s.Current?.Context)) pc.Unload();
            }
    }
}
