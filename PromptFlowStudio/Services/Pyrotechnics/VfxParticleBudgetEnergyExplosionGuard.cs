using System.Diagnostics;
using System.Numerics;

namespace PromptFlowStudio.Services.Pyrotechnics;

/// <summary>
/// [PromptFlow Studio v17.42.0] 🛡 파티클 버짓 초과 &amp; 플라즈마 수치 발산 방어 가드(무할당).
///   ▸ 버짓: 단일 버스트 ≤ <see cref="Budget"/>(기본 65,536) 클램프 → 활성 + 요청 &gt; 버짓이면 가시도 점수
///     s = 0.5·잔여 수명비 + 0.3·정규 온도 + 0.2/(1 + d²/25)(카메라 거리) 64-빈 히스토그램 컷으로 하위부터 정확히 초과분만 가지치기(O(n) 2패스 · 링 덮어쓰기 0).
///     85% 초과 시 연속 방출 LOD 배율(<see cref="EmissionScale"/>) 선형 감쇄.
///   ▸ 수치: Vector&lt;T&gt; 일괄 — NaN/±Inf → 0 수리 · CFL 속도 상한(4 셀/프레임) · θ ∈ [0, 5700 K] · 연료/매연/압력 범위 클램프.
///   ▸ 열역학 에너지 보존 클램프: Σθ ≤ (직전 Σθ + 프레임 열 입력(주입 + 연소 발열))·(1 + 8%) — 초과 시 θ 전역 비례 축소.
/// </summary>
public sealed class VfxParticleBudgetEnergyExplosionGuard
{
    public const int DefaultBudget = ProceduralSparkPyrotechnicsSynthesizer.Capacity, ScoreBins = 64;
    public const float MaxExcess = 5700f, MaxFuel = 8f, MaxDensity = 12f, MaxPressure = 1e4f, CflCells = 4f, EnergyTolerance = 0.08f, LodKnee = 0.85f;

    private static readonly double TickUs = 1_000_000.0 / Stopwatch.Frequency;
    private readonly int[] _hist = new int[ScoreBins];
    private float _lastE;
    private int _budget = DefaultBudget;

    public bool Enabled { get; set; } = true;
    public int Budget { get => _budget; set => _budget = Math.Clamp(value, 1024, ProceduralSparkPyrotechnicsSynthesizer.Capacity); }
    public float EmissionScale { get; private set; } = 1f;
    public long Requested { get; private set; }
    public long ClampedEmits { get; private set; }
    public long Pruned { get; private set; }
    public long PruneEvents { get; private set; }
    public int PeakLive { get; private set; }
    public long NanRepairs { get; private set; }
    public long RangeClamps { get; private set; }
    public long SpeedClamps { get; private set; }
    public long EnergyClamps { get; private set; }
    public float LastEnergyRatio { get; private set; } = 1f;
    public float MaxEnergyRatio { get; private set; }
    public double LastPruneUs { get; private set; }
    public double LastSanitizeUs { get; private set; }

    public void ResetEnergy(float e = 0f) => _lastE = e;

    public void ResetMetrics()
    {
        Requested = ClampedEmits = Pruned = PruneEvents = NanRepairs = RangeClamps = SpeedClamps = EnergyClamps = 0;
        PeakLive = 0; MaxEnergyRatio = 0f;
    }

    // ══════════════════════ 파티클 버짓 ══════════════════════

    /// <summary>방출 승인 — 반환 = 허용 수. 필요 시 저가시도 파티클을 정확히 초과분만 가지치기.</summary>
    public int Admit(ProceduralSparkPyrotechnicsSynthesizer s, int want, Vector3 camera)
    {
        Requested += want;
        if (!Enabled || want <= 0) return want;
        var allow = Math.Min(want, _budget);
        ClampedEmits += want - allow;
        var need = s.Live + allow - _budget;
        if (need > 0) Prune(s, need, camera);
        if (s.Live + allow > PeakLive) PeakLive = s.Live + allow;
        return allow;
    }

    /// <summary>프레임 집행 — 버짓 하향 조정 시 즉시 가지치기 · LOD 배율 갱신.</summary>
    public void Enforce(ProceduralSparkPyrotechnicsSynthesizer s, Vector3 camera)
    {
        if (!Enabled) { EmissionScale = 1f; return; }
        if (s.Live > _budget) Prune(s, s.Live - _budget, camera);
        if (s.Live > PeakLive) PeakLive = s.Live;
        var knee = LodKnee * _budget;
        EmissionScale = s.Live <= knee ? 1f : Math.Clamp((_budget - s.Live) / (_budget - knee), 0.05f, 1f);
    }

    public int Prune(ProceduralSparkPyrotechnicsSynthesizer s, int need, Vector3 camera)
    {
        var t0 = Stopwatch.GetTimestamp();
        var life = s.LifeArray; var max = s.MaxLifeArray; var heat = s.HeatArray; var px = s.PxArray; var py = s.PyArray; var pz = s.PzArray;
        var hi = s.HighWater;
        Array.Clear(_hist);
        for (var i = 0; i < hi; i++)
        {
            if (life[i] <= 0f) continue;
            _hist[Score(life[i], max[i], heat[i], px[i] - camera.X, py[i] - camera.Y, pz[i] - camera.Z)]++;
        }
        int cut = 0, below = 0;
        while (cut < ScoreBins - 1 && below + _hist[cut] < need) below += _hist[cut++];
        var partial = need - below; // 컷 빈에서 추가로 제거할 수
        var killed = 0;
        for (var i = 0; i < hi && killed < need; i++)
        {
            if (life[i] <= 0f) continue;
            var b = Score(life[i], max[i], heat[i], px[i] - camera.X, py[i] - camera.Y, pz[i] - camera.Z);
            if (b < cut || (b == cut && partial-- > 0)) { life[i] = -1f; killed++; }
        }
        s.Live -= killed;
        Pruned += killed; PruneEvents++;
        LastPruneUs = (Stopwatch.GetTimestamp() - t0) * TickUs;
        return killed;
    }

    private static int Score(float life, float max, float heat, float dx, float dy, float dz)
    {
        var lf = Math.Clamp(life / MathF.Max(max, 1e-3f), 0f, 1f);
        var hn = Math.Clamp((heat - ProceduralSparkPyrotechnicsSynthesizer.AmbientK) * (1f / 2500f), 0f, 1f);
        var df = 1f / (1f + (dx * dx + dy * dy + dz * dz) * (1f / 25f));
        return Math.Clamp((int)((0.5f * lf + 0.3f * hn + 0.2f * df) * ScoreBins), 0, ScoreBins - 1);
    }

    // ══════════════════════ 복셀 수치 가드 ══════════════════════

    /// <summary>NaN/Inf 수리 · 범위/CFL 클램프 · (<paramref name="energy"/>) 열역학 에너지 보존 클램프.</summary>
    public void Sanitize(RealtimeNeuralCombustionFluidEngine f, float dt, bool energy)
    {
        if (!Enabled) return;
        var t0 = Stopwatch.GetTimestamp();
        var umax = CflCells * RealtimeNeuralCombustionFluidEngine.Dx / MathF.Max(dt, 1e-4f);
        long nan = 0, rng = 0, spd = 0;
        Clamp(f.UArray, -umax, umax, ref nan, ref spd);
        Clamp(f.VArray, -umax, umax, ref nan, ref spd);
        Clamp(f.WArray, -umax, umax, ref nan, ref spd);
        Clamp(f.FArray, 0f, MaxFuel, ref nan, ref rng);
        Clamp(f.DArray, 0f, MaxDensity, ref nan, ref rng);
        Clamp(f.PArray, -MaxPressure, MaxPressure, ref nan, ref rng);
        var e = Clamp(f.TArray, 0f, MaxExcess, ref nan, ref rng);
        NanRepairs += nan; RangeClamps += rng; SpeedClamps += spd;
        if (energy)
        {
            var allowed = _lastE + MathF.Max(f.FrameHeatInput, 0f);
            var ratio = e / MathF.Max(allowed, 1f);
            LastEnergyRatio = ratio;
            if (ratio > MaxEnergyRatio) MaxEnergyRatio = ratio;
            if (e > allowed * (1f + EnergyTolerance) + 1f)
            {
                Scale(f.TArray, allowed / e);
                e = allowed;
                EnergyClamps++;
            }
            _lastE = e;
        }
        LastSanitizeUs = (Stopwatch.GetTimestamp() - t0) * TickUs;
    }

    /// <summary>클램프 + NaN/Inf → 0. 반환 = Σ(결과). NaN 비교: v ≠ v · Inf: |v| &gt; float.MaxValue.</summary>
    public static float Clamp(float[] a, float lo, float hi, ref long nan, ref long clamped)
    {
        int i = 0, n = a.Length;
        var sum = 0f;
        if (Vector.IsHardwareAccelerated)
        {
            var vlo = new Vector<float>(lo); var vhi = new Vector<float>(hi); var fm = new Vector<float>(float.MaxValue);
            var acc = Vector<float>.Zero; var bad = Vector<int>.Zero; var cl = Vector<int>.Zero;
            for (; i <= n - Vector<float>.Count; i += Vector<float>.Count)
            {
                var v = new Vector<float>(a, i);
                var nf = ~Vector.Equals(v, v) | Vector.GreaterThan(Vector.Abs(v), fm);
                v = Vector.ConditionalSelect(nf, Vector<float>.Zero, v);
                cl += Vector.GreaterThan(v, vhi) | Vector.LessThan(v, vlo);
                v = Vector.Min(Vector.Max(v, vlo), vhi);
                bad += nf;
                acc += v;
                v.CopyTo(a, i);
            }
            nan -= Vector.Sum(bad); clamped -= Vector.Sum(cl);
            sum = Vector.Sum(acc);
        }
        for (; i < n; i++)
        {
            var v = a[i];
            if (!float.IsFinite(v)) { v = 0f; nan++; }
            if (v > hi || v < lo) { clamped++; v = Math.Clamp(v, lo, hi); }
            a[i] = v; sum += v;
        }
        return sum;
    }

    private static void Scale(float[] a, float k)
    {
        int i = 0, n = a.Length;
        if (Vector.IsHardwareAccelerated)
        {
            var vk = new Vector<float>(k);
            for (; i <= n - Vector<float>.Count; i += Vector<float>.Count) (new Vector<float>(a, i) * vk).CopyTo(a, i);
        }
        for (; i < n; i++) a[i] *= k;
    }

    public static int CountNonFinite(RealtimeNeuralCombustionFluidEngine f)
    {
        var c = 0;
        c += Count(f.U); c += Count(f.V); c += Count(f.W); c += Count(f.Temperature); c += Count(f.Fuel); c += Count(f.Density); c += Count(f.Pressure);
        return c;
        static int Count(Span<float> a) { var n = 0; foreach (var v in a) if (!float.IsFinite(v)) n++; return n; }
    }
}
