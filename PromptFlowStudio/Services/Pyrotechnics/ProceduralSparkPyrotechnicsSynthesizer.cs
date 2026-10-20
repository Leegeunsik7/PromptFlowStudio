using System.Diagnostics;
using System.Numerics;
using PromptFlowStudio.Services.StageLighting;

namespace PromptFlowStudio.Services.Pyrotechnics;

/// <summary>
/// [PromptFlow Studio v17.42.0] ✨ 절차적 스파크 폭발 &amp; 발광 파티클 조명 방출 합성기(<see cref="Emit"/>/<see cref="Step"/>/<see cref="ComputeLights"/> 무할당).
///   ▸ 고정 65,536 슬롯 SoA 링버퍼(위치 · 속도 · 수명 · 흑체 온도) — 커서 전진 빈 슬롯 재사용 · 1 버스트당 스캔 상한 = 용량 → 초과 시 최고령 덮어쓰기(<see cref="Overflows"/>).
///   ▸ 탄도: 중력 · 지수 항력 · 바닥 반발(e 0.35 · 마찰 0.6) · 뉴턴 냉각 T∞ + (T − T∞)e^(−Δt/τ) — Vector&lt;T&gt; ConditionalSelect(스칼라 쌍둥이 비트 동일).
///   ▸ 발광: 스파크 광도 cd = 0.04·(T/2000 K)⁴ → 4×2×4 공간 빈 가중 중심/색온도 → 상위 7 클러스터 + 화염광(v17.42 유체 방출 중심) = 8 동적 포인트 라이트
///     → v17.41 무대 조명 엔진 주입 · 16×16 바닥(씬 표면) 램버트 반사 조도 맵.
/// </summary>
public sealed class ProceduralSparkPyrotechnicsSynthesizer
{
    public const int Capacity = 65536, Mask = Capacity - 1, BX = 4, BY = 2, BZ = 4, Bins = BX * BY * BZ, Lights = RealtimeIntelligentStageLightingEngine.PointLightSlots, FloorRes = 16;
    public const float Gravity = 9.81f, Drag = 0.45f, Restitution = 0.35f, Friction = 0.6f, CoolTau = 1.1f, AmbientK = 293f, SparkCd = 0.04f, FireCd = 0.6f, ShockSpeed = 40f;

    private static readonly double TickUs = 1_000_000.0 / Stopwatch.Frequency;
    private readonly float[] _px = new float[Capacity], _py = new float[Capacity], _pz = new float[Capacity];
    private readonly float[] _vx = new float[Capacity], _vy = new float[Capacity], _vz = new float[Capacity];
    private readonly float[] _life = new float[Capacity], _max = new float[Capacity], _heat = new float[Capacity];
    private readonly double[] _bw = new double[Bins], _bx = new double[Bins], _by = new double[Bins], _bz = new double[Bins], _bt = new double[Bins];
    private readonly int[] _bn = new int[Bins];
    private readonly DynamicPointLight[] _lights = new DynamicPointLight[Lights];
    private readonly float[] _floor = new float[FloorRes * FloorRes * 3];
    private int _cursor, _hi;
    private uint _rng = 0x9E3779B9u;
    private float _emitAcc;

    public ProceduralSparkPyrotechnicsSynthesizer() => Array.Fill(_life, -1f);

    public bool UseSimd { get; set; } = true;
    public int Live { get; internal set; }
    public int PeakLive { get; private set; }
    public int HighWater => _hi;
    public long Emitted { get; private set; }
    public long Overflows { get; private set; }
    public int LightCount { get; private set; }
    public float TotalCandela { get; private set; }
    public float SparkCandela { get; private set; }
    public float MaxFloorLux { get; private set; }
    public float MeanSparkK { get; private set; }
    public double LastStepUs { get; private set; }
    public double LastLightUs { get; private set; }
    public Vector3 ShockOrigin { get; private set; }
    public float ShockRadius { get; private set; }
    public float ShockStrength { get; private set; }

    public ReadOnlySpan<DynamicPointLight> LightSpan => _lights.AsSpan(0, LightCount);
    public ref readonly DynamicPointLight Light(int i) => ref _lights[i];
    public ReadOnlySpan<float> FloorIrradiance => _floor;
    public float Life(int i) => _life[i];
    public float MaxLife(int i) => _max[i];
    public float HeatK(int i) => _heat[i];
    public Vector3 Position(int i) => new(_px[i], _py[i], _pz[i]);
    public Vector3 Velocity(int i) => new(_vx[i], _vy[i], _vz[i]);

    // 가드 접근(동일 게이트)
    internal float[] LifeArray => _life;
    internal float[] MaxLifeArray => _max;
    internal float[] HeatArray => _heat;
    internal float[] PxArray => _px;
    internal float[] PyArray => _py;
    internal float[] PzArray => _pz;

    public void Clear()
    {
        Array.Fill(_life, -1f);
        Live = 0; _cursor = 0; _hi = 0; _emitAcc = 0f; LightCount = 0; TotalCandela = SparkCandela = 0f; ShockStrength = 0f;
        Array.Clear(_floor);
    }

    public void ResetMetrics() { PeakLive = Live; Emitted = Overflows = 0; }

    private float Next()
    {
        var x = _rng; x ^= x << 13; x ^= x >> 17; x ^= x << 5; _rng = x;
        return (x >> 8) * (1f / 16777216f);
    }

    // ══════════════════════ 방출(무할당 · 링버퍼) ══════════════════════

    /// <summary>탄도 버스트 — 가드 승인 수만큼 빈 슬롯(커서 전진)에 기록. 반환 = 실제 방출 수.</summary>
    public int Emit(Vector3 origin, int count, PyroPresetSpec p, Vector3 baseVel, VfxParticleBudgetEnergyExplosionGuard? guard, Vector3 camera)
    {
        if (count <= 0) return 0;
        if (guard is not null) count = guard.Admit(this, count, camera);
        var scan = Capacity;
        for (var k = 0; k < count; k++)
        {
            var slot = -1;
            while (scan > 0)
            {
                var c = _cursor; _cursor = (_cursor + 1) & Mask; scan--;
                if (_life[c] <= 0f) { slot = c; break; }
            }
            if (slot < 0) { slot = _cursor; _cursor = (_cursor + 1) & Mask; Overflows++; Live--; }
            if (slot + 1 > _hi) _hi = slot + 1;
            float dx, dy, dz;
            if (p.SparkCone > 0f)
            {
                var ct = p.SparkCone + (1f - p.SparkCone) * Next();
                var st = MathF.Sqrt(MathF.Max(0f, 1f - ct * ct));
                var ph = MathF.Tau * Next();
                dx = st * MathF.Cos(ph); dy = ct; dz = st * MathF.Sin(ph);
            }
            else
            {
                var zz = 2f * Next() - 1f;
                var st = MathF.Sqrt(MathF.Max(0f, 1f - zz * zz));
                var ph = MathF.Tau * Next();
                dx = st * MathF.Cos(ph); dy = MathF.Abs(zz) * 0.8f + 0.2f * zz; dz = st * MathF.Sin(ph); // 지면 반사 편향(상반구 가중)
            }
            var sp = p.SparkSpeedMin + (p.SparkSpeedMax - p.SparkSpeedMin) * Next();
            _px[slot] = origin.X + 0.05f * (Next() - 0.5f); _py[slot] = MathF.Max(origin.Y + 0.05f * (Next() - 0.5f), 0f); _pz[slot] = origin.Z + 0.05f * (Next() - 0.5f);
            _vx[slot] = baseVel.X + sp * dx; _vy[slot] = baseVel.Y + sp * dy; _vz[slot] = baseVel.Z + sp * dz;
            var life = p.SparkLifeMin + (p.SparkLifeMax - p.SparkLifeMin) * Next();
            _life[slot] = life; _max[slot] = life;
            _heat[slot] = p.SparkTempK * (0.85f + 0.3f * Next());
            Live++;
        }
        Emitted += count;
        if (Live > PeakLive) PeakLive = Live;
        return count;
    }

    /// <summary>연속 방출(분수 · 불씨) — 가드 LOD 배율 적용 누산기.</summary>
    public int Continuous(float dt, PyroPresetSpec p, Vector3 origin, VfxParticleBudgetEnergyExplosionGuard? guard, Vector3 camera)
    {
        if (p.SparkRate <= 0f) return 0;
        _emitAcc += p.SparkRate * dt * (guard?.EmissionScale ?? 1f);
        var n = (int)_emitAcc;
        _emitAcc -= n;
        return Emit(origin, n, p, Vector3.Zero, guard, camera);
    }

    public void StartShock(Vector3 origin, float strength) { ShockOrigin = origin; ShockRadius = 0f; ShockStrength = strength; }

    // ══════════════════════ 탄도 적분(Vector<T>) ══════════════════════

    public void Step(float dt)
    {
        var t0 = Stopwatch.GetTimestamp();
        var drag = MathF.Exp(-Drag * dt); var cool = MathF.Exp(-dt / CoolTau); var g = Gravity * dt;
        var hi = _hi; int i = 0;
        if (UseSimd && Vector.IsHardwareAccelerated)
        {
            var hv = (hi + Vector<float>.Count - 1) / Vector<float>.Count * Vector<float>.Count;
            var vdt = new Vector<float>(dt); var vdr = new Vector<float>(drag); var vg = new Vector<float>(g); var vc = new Vector<float>(cool);
            var va = new Vector<float>(AmbientK); var vre = new Vector<float>(-Restitution); var vfr = new Vector<float>(Friction); var m1 = new Vector<float>(-1f);
            for (; i < hv; i += Vector<float>.Count)
            {
                Vector.Max(new Vector<float>(_life, i) - vdt, m1).CopyTo(_life, i);
                var vx = new Vector<float>(_vx, i) * vdr; var vy = (new Vector<float>(_vy, i) - vg) * vdr; var vz = new Vector<float>(_vz, i) * vdr;
                var px = new Vector<float>(_px, i) + vx * vdt; var py = new Vector<float>(_py, i) + vy * vdt; var pz = new Vector<float>(_pz, i) + vz * vdt;
                var below = Vector.LessThan(py, Vector<float>.Zero);
                py = Vector.ConditionalSelect(below, Vector<float>.Zero, py);
                vy = Vector.ConditionalSelect(below, vy * vre, vy);
                vx = Vector.ConditionalSelect(below, vx * vfr, vx);
                vz = Vector.ConditionalSelect(below, vz * vfr, vz);
                vx.CopyTo(_vx, i); vy.CopyTo(_vy, i); vz.CopyTo(_vz, i); px.CopyTo(_px, i); py.CopyTo(_py, i); pz.CopyTo(_pz, i);
                (va + (new Vector<float>(_heat, i) - va) * vc).CopyTo(_heat, i);
            }
        }
        for (; i < hi; i++)
        {
            _life[i] = MathF.Max(_life[i] - dt, -1f);
            var vx = _vx[i] * drag; var vy = (_vy[i] - g) * drag; var vz = _vz[i] * drag;
            var px = _px[i] + vx * dt; var py = _py[i] + vy * dt; var pz = _pz[i] + vz * dt;
            if (py < 0f) { py = 0f; vy *= -Restitution; vx *= Friction; vz *= Friction; }
            _vx[i] = vx; _vy[i] = vy; _vz[i] = vz; _px[i] = px; _py[i] = py; _pz[i] = pz;
            _heat[i] = AmbientK + (_heat[i] - AmbientK) * cool;
        }
        if (ShockStrength > 0f) { ShockRadius += ShockSpeed * dt; ShockStrength *= MathF.Exp(-3f * dt); if (ShockStrength < 0.01f || ShockRadius > 12f) ShockStrength = 0f; }
        LastStepUs = (Stopwatch.GetTimestamp() - t0) * TickUs;
    }

    // ══════════════════════ 발광 클러스터 → 포인트 라이트 · 바닥 반사 ══════════════════════

    public void ComputeLights(RealtimeNeuralCombustionFluidEngine fluid, bool plasma)
    {
        var t0 = Stopwatch.GetTimestamp();
        Array.Clear(_bw); Array.Clear(_bx); Array.Clear(_by); Array.Clear(_bz); Array.Clear(_bt); Array.Clear(_bn);
        var live = 0; double sk = 0, sw = 0;
        var hi = _hi;
        for (var i = 0; i < hi; i++)
        {
            if (_life[i] <= 0f) continue;
            live++;
            float x = _px[i], y = _py[i], z = _pz[i], t = _heat[i];
            var q = t * (1f / 2000f); q *= q; q *= q;
            var w = SparkCd * q;
            var b = BinOf(x, y, z);
            _bw[b] += w; _bx[b] += w * x; _by[b] += w * y; _bz[b] += w * z; _bt[b] += w * t; _bn[b]++;
            sk += t; sw += w;
        }
        Live = live;
        if (live > PeakLive) PeakLive = live;
        MeanSparkK = live > 0 ? (float)(sk / live) : 0f;
        SparkCandela = (float)sw;

        var n = 0;
        if (fluid.FlameEmission > 1e-3f)
        {
            ref var f = ref _lights[n++];
            f.Position = fluid.FlameCentroid; f.Color = RealtimeNeuralCombustionFluidEngine.FlameColor(fluid.FlameTempK, plasma);
            f.Candela = FireCd * fluid.FlameEmission; f.Sparks = 0;
        }
        while (n < Lights)
        {
            int best = -1; var bw = 1e-3;
            for (var b = 0; b < Bins; b++) if (_bw[b] > bw) { bw = _bw[b]; best = b; }
            if (best < 0) break;
            ref var l = ref _lights[n++];
            l.Position = new Vector3((float)(_bx[best] / bw), (float)(_by[best] / bw), (float)(_bz[best] / bw));
            l.Color = RealtimeNeuralCombustionFluidEngine.FlameColor((float)(_bt[best] / bw), plasma);
            l.Candela = (float)bw; l.Sparks = _bn[best];
            _bw[best] = 0;
        }
        LightCount = n;
        var total = 0f;
        for (var i = 0; i < n; i++) total += _lights[i].Candela;
        TotalCandela = total;

        // 바닥(씬 표면) 램버트 조도 E = Σ I·h/d³ · 색
        var maxE = 0f;
        const float cell = RealtimeNeuralCombustionFluidEngine.Extent / FloorRes;
        for (var gz = 0; gz < FloorRes; gz++)
        for (var gx = 0; gx < FloorRes; gx++)
        {
            float r = 0f, g = 0f, bl = 0f;
            var px = (gx + 0.5f) * cell - RealtimeNeuralCombustionFluidEngine.Half; var pz = (gz + 0.5f) * cell - RealtimeNeuralCombustionFluidEngine.Half;
            for (var k = 0; k < n; k++)
            {
                ref readonly var l = ref _lights[k];
                var h = MathF.Max(l.Position.Y, 0.05f);
                var dx = l.Position.X - px; var dz = l.Position.Z - pz;
                var d2 = dx * dx + h * h + dz * dz;
                var e = l.Candela * h / (d2 * MathF.Sqrt(d2));
                r += e * l.Color.X; g += e * l.Color.Y; bl += e * l.Color.Z;
            }
            var o = (gz * FloorRes + gx) * 3;
            _floor[o] = r; _floor[o + 1] = g; _floor[o + 2] = bl;
            var lum = 0.2126f * r + 0.7152f * g + 0.0722f * bl;
            if (lum > maxE) maxE = lum;
        }
        MaxFloorLux = maxE;
        LastLightUs = (Stopwatch.GetTimestamp() - t0) * TickUs;
    }

    public static int BinOf(float x, float y, float z)
    {
        var ix = Math.Clamp((int)((x + RealtimeNeuralCombustionFluidEngine.Half) * (BX / RealtimeNeuralCombustionFluidEngine.Extent)), 0, BX - 1);
        var iy = y < 1.5f ? 0 : 1;
        var iz = Math.Clamp((int)((z + RealtimeNeuralCombustionFluidEngine.Half) * (BZ / RealtimeNeuralCombustionFluidEngine.Extent)), 0, BZ - 1);
        return ix + BX * (iy + BY * iz);
    }
}
