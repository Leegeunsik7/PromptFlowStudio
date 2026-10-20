using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using PromptFlowStudio.Services.World;

namespace PromptFlowStudio.Services.Edge;

/// <summary>외부 NPU 런타임 결선점(ONNX Runtime/QNN/OpenVINO 등) — 해시 특징(int8) → 점수. 미결선/실패 시 내장 CPU-SIMD int8 폴백.</summary>
public interface IEdgeNpuBackend
{
    string Name { get; }
    bool TryScore(ReadOnlySpan<sbyte> features, out float score);
}

/// <summary>캐릭터 일관성 파라미터 — 결정적 시드 · 팔레트 · int8 정체성 임베딩(16) · 잠금 프롬프트 토큰.</summary>
public sealed record EdgeCharacterParams(string Name, uint Seed, string PaletteHex, sbyte[] Embedding, string LockToken, string? Look);

/// <summary>온디바이스 처리 결과.</summary>
public sealed record EdgeAgentResult(string Input, string Refined, string Negative, string Variant, float TeacherAcceptance, float StudentScore, float QualityBefore, float QualityAfter,
    int Entities, int Relations, int Events, int Issues, int AutoFixed, float DurationSec, IReadOnlyList<EdgeCharacterParams> Characters, string Backend, double Millis, bool OnDevice);

/// <summary>
/// [PromptFlow Studio v16.64.0] 🤖 온디바이스 에지 AI 에이전트 엔진 — 클라우드 API 오프로드 0(HTTP 미사용 · <see cref="CloudCalls"/> 상시 0).
///   ▸ 프롬프트 정제 · 4D 장면 그래프: v16.54 <see cref="NeuralSceneGraphPromptOptimizer"/>(규칙 NLU + 퍼셉트론)를 로컬 교사로 실행.
///   ▸ 경량 학생 모델: 문자 2/3-gram 해시 특징(256 · int8 포화) × int8 양자화 가중치 — Vector256 sbyte→short→int 확장 내적(무할당).
///     교사(정제 후보 채택 확률)로 온라인 SGD 증류 → 대칭 양자화 → 스웜 노드가 변형 후보 대량 순위화에 사용(<see cref="ScoreFast"/>).
///   ▸ 캐릭터 일관성: 이름 FNV 시드 · HSV 팔레트 · 정체성 임베딩(int8) · 외형 묘사 간 코사인 일관성(<see cref="LookConsistency"/>).
///   ▸ NPU: D3D12/DirectML 가용성 프로브 + <see cref="Npu"/> 결선점 — 미결선 시 CPU-SIMD int8.
/// </summary>
public sealed class EdgeAutonomousAiAgentEngine
{
    public const int Dims = 256, EmbedDims = 16;

    private readonly object _gate = new();
    private readonly NeuralSceneGraphPromptOptimizer _teacher;
    private readonly float[] _w = new float[Dims];
    private readonly sbyte[] _wq = new sbyte[Dims];
    private float _bias, _scale = 1f / 127f;
    private long _processed, _fastScores, _distillSteps, _npuHits;
    private double _lastMs, _fastUs;

    public EdgeAutonomousAiAgentEngine() : this((NeuralSceneGraphPromptOptimizer?)null) { }
    public EdgeAutonomousAiAgentEngine(IServiceProvider sp) : this(sp.GetService(typeof(NeuralSceneGraphPromptOptimizer)) as NeuralSceneGraphPromptOptimizer) { }
    public EdgeAutonomousAiAgentEngine(NeuralSceneGraphPromptOptimizer? teacher)
    {
        _teacher = teacher ?? new NeuralSceneGraphPromptOptimizer();
        _bias = 0.5f;
    }

    public event Action? OnChange;

    /// <summary>온디바이스 추론 토글(false = 정제 우회 · 원문 통과).</summary>
    public bool Enabled { get; set; } = true;
    public IEdgeNpuBackend? Npu { get; set; }
    public bool DirectMlCapable { get; } = PromptFlowStudio.Services.Inference.DirectMlNativeAdapter.IsDirectMlCapable();
    public string Backend => Npu is { } n ? "NPU:" + n.Name : Vector256.IsHardwareAccelerated ? "CPU-SIMD int8(AVX2)" : "CPU int8(스칼라)";
    /// <summary>클라우드 API 호출 수(설계상 항상 0 — 감사용).</summary>
    public long CloudCalls => 0;
    public long Processed => Interlocked.Read(ref _processed);
    public long FastScores => Interlocked.Read(ref _fastScores);
    public long DistillSteps => Interlocked.Read(ref _distillSteps);
    public long NpuHits => Interlocked.Read(ref _npuHits);
    public double LastMillis => Volatile.Read(ref _lastMs);
    public double FastScoreMicros => Volatile.Read(ref _fastUs);
    public EdgeAgentResult? Last { get; private set; }
    public string? LastAction { get; private set; }

    // ══════════════════════ 특징 · int8 내적 ══════════════════════

    /// <summary>문자 2/3-gram FNV 해시 특징(256 · int8 포화 카운트) — 무할당.</summary>
    public static void Featurize(ReadOnlySpan<char> text, Span<sbyte> dst)
    {
        Span<short> acc = stackalloc short[Dims];
        acc.Clear();
        char a = ' ', b = ' ';
        for (var i = 0; i < text.Length; i++)
        {
            var c = char.ToLowerInvariant(text[i]);
            if (char.IsWhiteSpace(c)) c = ' ';
            var h2 = ((uint)b * 16777619u) ^ c;
            var h3 = (((uint)a * 16777619u ^ b) * 16777619u) ^ c;
            acc[(int)((h2 * 2654435761u) >> 24)]++;
            acc[(int)((h3 * 2246822519u) >> 24)]++;
            a = b; b = c;
        }
        for (var i = 0; i < Dims; i++) dst[i] = (sbyte)Math.Min(127, (int)acc[i]);
    }

    /// <summary>int8 내적(Vector256 sbyte→short→int 확장 · 스칼라 꼬리).</summary>
    public static int DotInt8(ReadOnlySpan<sbyte> x, ReadOnlySpan<sbyte> w)
    {
        var n = Math.Min(x.Length, w.Length);
        var i = 0; var sum = 0;
        if (Vector256.IsHardwareAccelerated && n >= 32)
        {
            var acc = Vector256<int>.Zero;
            ref var rx = ref MemoryMarshal.GetReference(x);
            ref var rw = ref MemoryMarshal.GetReference(w);
            for (; i <= n - 32; i += 32)
            {
                var (xl, xh) = Vector256.Widen(Vector256.LoadUnsafe(ref rx, (nuint)i));
                var (wl, wh) = Vector256.Widen(Vector256.LoadUnsafe(ref rw, (nuint)i));
                var (p0, p1) = Vector256.Widen(xl * wl);
                var (p2, p3) = Vector256.Widen(xh * wh);
                acc += p0 + p1 + p2 + p3;
            }
            sum = Vector256.Sum(acc);
        }
        for (; i < n; i++) sum += x[i] * w[i];
        return sum;
    }

    /// <summary>학생 모델 고속 점수(0..1 · 무할당) — NPU 결선 시 우선.</summary>
    public float ScoreFast(ReadOnlySpan<char> text)
    {
        var t0 = Stopwatch.GetTimestamp();
        Span<sbyte> f = stackalloc sbyte[Dims];
        Featurize(text, f);
        float s;
        if (Npu is { } npu && npu.TryScore(f, out var ns)) { s = ns; Interlocked.Increment(ref _npuHits); }
        else
        {
            int dot; float scale, bias;
            var nn = DotInt8(f, f);
            lock (_gate) { dot = DotInt8(f, _wq); scale = _scale; bias = _bias; }
            s = Sigmoid(nn > 0 ? dot * scale / MathF.Sqrt(nn) + bias : bias);
        }
        Interlocked.Increment(ref _fastScores);
        var us = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
        Volatile.Write(ref _fastUs, _fastUs <= 0 ? us : _fastUs * 0.9 + us * 0.1);
        return s;
    }

    private static float Sigmoid(float z) => 1f / (1f + MathF.Exp(-Math.Clamp(z, -30f, 30f)));
    private static float Logit(float p) { p = Math.Clamp(p, 1e-3f, 1 - 1e-3f); return MathF.Log(p / (1 - p)); }

    /// <summary>교사 확률로 학생 증류(로지스틱 SGD · 특징 정규화) → 대칭 int8 재양자화.</summary>
    public void Distill(ReadOnlySpan<char> text, float teacherProb, int epochs = 8, float lr = 0.05f)
    {
        Span<sbyte> f = stackalloc sbyte[Dims];
        Featurize(text, f);
        Span<float> x = stackalloc float[Dims];
        float norm = 0;
        for (var i = 0; i < Dims; i++) { x[i] = f[i]; norm += x[i] * x[i]; }
        var inv = norm > 0 ? 1f / MathF.Sqrt(norm) : 0f;
        lock (_gate)
        {
            for (var e = 0; e < epochs; e++)
            {
                float z = _bias;
                for (var i = 0; i < Dims; i++) z += _w[i] * x[i] * inv;
                var g = Sigmoid(z) - teacherProb;
                for (var i = 0; i < Dims; i++) _w[i] -= lr * g * x[i] * inv;
                _bias -= lr * g;
            }
            // 대칭 양자화: w ≈ q·max/127 — 추론 z = Σq·f · (max/127) / ‖f‖ + b
            float max = 1e-6f;
            for (var i = 0; i < Dims; i++) max = MathF.Max(max, MathF.Abs(_w[i]));
            for (var i = 0; i < Dims; i++) _wq[i] = (sbyte)Math.Clamp((int)MathF.Round(_w[i] / max * 127f), -127, 127);
            _scale = max / 127f;
            Interlocked.Increment(ref _distillSteps);
        }
    }

    // ══════════════════════ 캐릭터 일관성 ══════════════════════

    public static EdgeCharacterParams Character(string name, string? look = null)
    {
        var seed = PromptFlowStudio.Services.Synergy.EnhancedScriptToScreenDirector.SeedOf(name);
        var hue = seed % 360; var sat = 0.55f + (seed >> 9 & 63) / 63f * 0.35f; var val = 0.6f + (seed >> 15 & 63) / 63f * 0.35f;
        var (r, g, b) = Hsv(hue, sat, val);
        var emb = new sbyte[EmbedDims];
        Span<sbyte> f = stackalloc sbyte[Dims];
        Featurize(name + "|" + (look ?? ""), f);
        for (var i = 0; i < EmbedDims; i++)
        {
            var s = 0;
            for (var k = i; k < Dims; k += EmbedDims) s += f[k] * (((seed >> (k & 31)) & 1) == 0 ? 1 : -1);
            emb[i] = (sbyte)Math.Clamp(s, -127, 127);
        }
        var hex = "#" + r.ToString("x2", CultureInfo.InvariantCulture) + g.ToString("x2", CultureInfo.InvariantCulture) + b.ToString("x2", CultureInfo.InvariantCulture);
        return new EdgeCharacterParams(name, seed, hex, emb, "<char:" + name + ":" + seed.ToString("x8", CultureInfo.InvariantCulture) + ">", look);
    }

    private static (int R, int G, int B) Hsv(uint h, float s, float v)
    {
        var c = v * s; var x = c * (1 - MathF.Abs(h / 60f % 2 - 1)); var m = v - c;
        var (r, g, b) = (h / 60) switch { 0 => (c, x, 0f), 1 => (x, c, 0f), 2 => (0f, c, x), 3 => (0f, x, c), 4 => (x, 0f, c), _ => (c, 0f, x) };
        return ((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
    }

    /// <summary>외형 묘사 간 일관성(해시 특징 코사인 0..1 · 무할당).</summary>
    public static float LookConsistency(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        Span<sbyte> fa = stackalloc sbyte[Dims];
        Span<sbyte> fb = stackalloc sbyte[Dims];
        Featurize(a, fa); Featurize(b, fb);
        var ab = DotInt8(fa, fb); var aa = DotInt8(fa, fa); var bb = DotInt8(fb, fb);
        return aa == 0 || bb == 0 ? 0f : (float)(ab / Math.Sqrt((double)aa * bb));
    }

    // ══════════════════════ 파이프라인 ══════════════════════

    /// <summary>온디바이스 처리 — 정제 · 장면 그래프 분석 · 증류 · 캐릭터 파라미터(characters: "이름" 또는 "이름=외형").</summary>
    public EdgeAgentResult Process(string prompt, IReadOnlyList<string>? characters = null)
    {
        var sw = Stopwatch.StartNew();
        var chars = new List<EdgeCharacterParams>();
        if (characters is not null)
            foreach (var c in characters)
            {
                var i = c.IndexOf('=');
                chars.Add(i > 0 ? Character(c[..i].Trim(), c[(i + 1)..].Trim()) : Character(c.Trim()));
            }
        EdgeAgentResult r;
        if (!Enabled)
        {
            r = new EdgeAgentResult(prompt, prompt, "", "bypass", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, chars, "우회(온디바이스 OFF)", sw.Elapsed.TotalMilliseconds, false);
        }
        else
        {
            var (g, op) = _teacher.AnalyzeAndOptimize(prompt);
            foreach (var c in op.Candidates) Distill(c.Text, c.Acceptance);
            var lockTokens = "";
            foreach (var c in chars) lockTokens += " " + c.LockToken;
            var refined = chars.Count > 0 ? op.Refined + lockTokens : op.Refined;
            var fixedN = 0; foreach (var i in g.Issues) if (i.AutoFixed) fixedN++;
            r = new EdgeAgentResult(prompt, refined, op.Negative, op.Variant, op.Acceptance, ScoreFast(op.Refined), op.Before.Overall, op.After.Overall,
                g.Entities.Count, g.Edges.Count, g.Events.Count, g.Issues.Count, fixedN, g.Duration, chars, Backend, sw.Elapsed.TotalMilliseconds, true);
        }
        Volatile.Write(ref _lastMs, r.Millis);
        Interlocked.Increment(ref _processed);
        Last = r;
        LastAction = (r.OnDevice ? "🤖 온디바이스 정제 · " : "⏭ 우회 · ") + r.Entities.ToString(CultureInfo.InvariantCulture) + "객체/" + r.Relations.ToString(CultureInfo.InvariantCulture)
            + "관계 · 품질 " + Pct(r.QualityBefore) + "→" + Pct(r.QualityAfter) + " · " + r.Millis.ToString("0.0", CultureInfo.InvariantCulture) + "ms · 클라우드 0";
        Raise();
        return r;
    }

    private static string Pct(float v) => ((int)MathF.Round(v * 100)).ToString(CultureInfo.InvariantCulture) + "%";

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("EdgeAgent.OnChange", ex); }
    }
}
