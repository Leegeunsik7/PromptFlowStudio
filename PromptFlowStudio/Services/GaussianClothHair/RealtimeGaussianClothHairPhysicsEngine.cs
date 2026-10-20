using System.Numerics;
using System.Runtime.InteropServices;
using PromptFlowStudio.Services.NeuralRagdoll;
using PromptFlowStudio.Services.SkinnedSplatAvatar;
using PromptFlowStudio.Services.SplatEnvironment;
namespace PromptFlowStudio.Services.GaussianClothHair;

public enum FabricPreset : byte { Silk, Denim, Leather }

/// <summary>원단 물성 — 면밀도 kg/m² · 구속 강성(신장/전단/굽힘) · 감쇠 · 항력/양력 계수 · 마찰 · 반발 · 두께 m · 선형 RGB.</summary>
public readonly record struct FabricMaterial(string Name, float Density, float Stretch, float Shear, float Bend, float Damping, float Drag, float Lift, float Friction, float Restitution, float Thickness, Vector3 Color)
{
    public static FabricMaterial Of(FabricPreset p) => p switch
    {
        FabricPreset.Denim => new("🧵 데님", 0.40f, 1f, 0.85f, 0.30f, 0.012f, 1.0f, 0.25f, 0.55f, 0.05f, 0.010f, new Vector3(0.16f, 0.24f, 0.42f)),
        FabricPreset.Leather => new("🐂 레더", 0.85f, 1f, 0.95f, 0.70f, 0.020f, 0.8f, 0.15f, 0.65f, 0.02f, 0.012f, new Vector3(0.26f, 0.14f, 0.08f)),
        _ => new("🪡 실크", 0.09f, 1f, 0.50f, 0.04f, 0.006f, 1.2f, 0.45f, 0.18f, 0.10f, 0.006f, new Vector3(0.78f, 0.16f, 0.28f)),
    };
}

/// <summary>
/// [PromptFlow Studio v17.59.0] 👗 실시간 3DGS PBD 직물 격자 &amp; 헤어 스트랜드 동역학 엔진(<see cref="Step"/> · <see cref="WriteSplats"/> 무할당 · 60 fps).
/// 의상 = 골반 고정 플레어 스커트 원통 격자 48 × 20(구조 · 전단 · 1 칸 건너뜀 굽힘 거리 구속) · 헤어 = 두피 160 가닥 × 12 노드 가이드 체인(신장 + i/i+2 굽힘 · 뿌리 2 노드 머리 고정).
/// 프레임 = 외력(중력 + 공기역학) → SoA <see cref="Vector{T}"/> 베를레 적분 → 고정 노드(v17.57 FK 관절 프레임) → Gauss-Seidel 구속 반복(반복수 보정 강성 1 − (1 − k)^(1/n))
/// + 열별 허리 고정점 장거리 테더(LRA · 단방향) + 마지막 반복 헤어 FTL(뿌리 → 끝 비신장) + 신체 캡슐 충돌.
/// 스플랫: 의상 20 000 = 격자 셀 쌍선형 좌표 → Σ = aaᵀ + bbᵀ + ccᵀ(a, b = 변형 접선 ∂P/∂u · ∂P/∂v 의 휴지 길이 정규화 · c = 법선 × 두께) 로 직물 신장/전단을 그대로 따르는 타원체,
/// 헤어 15 000 = 세그먼트 보간 + 횡 오프셋 → Σ = σr²I + (σl² − σr²)TTᵀ(스트랜드 흐름 방향 T 정렬 · 항상 양정치).
/// </summary>
public sealed class RealtimeGaussianClothHairPhysicsEngine
{
    public const int SkirtU = 48, SkirtV = 20, Strands = 160, Nodes = 12, DefaultIterations = 10, MaxSubsteps = 4;
    public const int ClothParticles = SkirtU * SkirtV, HairParticles = Strands * Nodes, Particles = ClothParticles + HairParticles;
    public const int ClothSplats = 20_000, HairSplats = 15_000, Splats = ClothSplats + HairSplats;
    public const float WaistLift = 0.10f, WaistR = 0.205f, HemR = 0.52f, SkirtLen = 0.52f, HairLen = 0.34f, ScalpLift = 0.006f, HairThickness = 0.004f, StrandRadius = 0.007f;
    public const byte Free = 255;
    public static readonly Vector3 Gravity = new(0f, -9.81f, 0f);
    private static readonly int Pad = (Particles + Vector<float>.Count - 1) / Vector<float>.Count * Vector<float>.Count;

    public readonly float[] X = new float[Pad], Y = new float[Pad], Z = new float[Pad], PX = new float[Pad], PY = new float[Pad], PZ = new float[Pad];
    public readonly float[] W = new float[Pad], AX = new float[Pad], AY = new float[Pad], AZ = new float[Pad];
    private readonly float[] _damp = new float[Pad], _tether = new float[ClothParticles];
    private readonly Vector3[] _rest = new Vector3[Particles];
    private readonly byte[] _pinJ = new byte[Particles], _home = new byte[Particles];
    private readonly int[] _pins;
    private readonly Vector3[] _pinS, _pinT;
    private readonly int[] _ca, _cb;
    private readonly float[] _cl;
    private readonly int _structEnd, _shearEnd, _bendEnd, _hairStretchEnd, _hairBendEnd;
    // 의상 스플랫(셀 4 모서리 · 쌍선형 좌표 · 층 오프셋 · 휴지 접선 역길이 · 축 스케일 · 음영)
    private readonly int[] _q00 = new int[ClothSplats], _q10 = new int[ClothSplats], _q01 = new int[ClothSplats], _q11 = new int[ClothSplats];
    private readonly float[] _su = new float[ClothSplats], _sv = new float[ClothSplats], _sOff = new float[ClothSplats], _sInvDu = new float[ClothSplats], _sInvDv = new float[ClothSplats];
    private readonly float[] _sScU = new float[ClothSplats], _sScV = new float[ClothSplats], _shade = new float[ClothSplats];
    // 헤어 스플랫(세그먼트 시작 노드 · 보간 t · 휴지 횡 오프셋 · 반경/길이)
    private readonly int[] _hSeg = new int[HairSplats];
    private readonly float[] _ht = new float[HairSplats], _hR = new float[HairSplats], _hL = new float[HairSplats];
    private readonly Vector3[] _hOff = new Vector3[HairSplats];
    private float _skirtSign = 1f;
    private int _clothStart = -1, _hairStart = -1;
    private bool _placed;

    public RealtimeGaussianClothHairPhysicsEngine(FabricPreset fabric = FabricPreset.Silk)
    {
        Material = FabricMaterial.Of(fabric);
        Fabric = fabric;
        BuildRest(out var pins);
        _pins = pins; _pinS = new Vector3[pins.Length]; _pinT = new Vector3[pins.Length];
        BuildConstraints(out _ca, out _cb, out _cl, out _structEnd, out _shearEnd, out _bendEnd, out _hairStretchEnd, out _hairBendEnd);
        for (var i = 0; i < ClothParticles; i++) _tether[i] = Vector3.Distance(_rest[i], _rest[i % SkirtU]);
        ApplyDamping();
    }

    public FabricPreset Fabric { get; private set; }
    public FabricMaterial Material { get; private set; }
    /// <summary>헤어 굽힘 강성 0..1(i/i+2 거리 구속).</summary>
    public float HairStiffness { get; set; } = 0.35f;
    public float HairDamping { get; set; } = 0.02f;
    public int Iterations { get; set; } = DefaultIterations;
    public bool TetherEnabled { get; set; } = true;
    public bool UseSimd { get; set; } = Vector.IsHardwareAccelerated;
    public long Frames { get; private set; }
    public int Substeps { get; private set; } = 1;
    public double LastStepUs { get; private set; }
    public double LastSplatUs { get; private set; }
    public int ClothStart => _clothStart;
    public int HairStart => _hairStart;
    public int PinCount => _pins.Length;
    public int StructuralCount => _structEnd;
    public int ConstraintCount => _hairBendEnd;
    public int HairStretchStart => _bendEnd;
    public int HairStretchEnd => _hairStretchEnd;
    public int ConstraintA(int c) => _ca[c];
    public int ConstraintB(int c) => _cb[c];
    public float RestLength(int c) => _cl[c];
    public bool IsHair(int i) => i >= ClothParticles;
    public byte PinJoint(int i) => _pinJ[i];
    public Vector3 Position(int i) => new(X[i], Y[i], Z[i]);
    public Vector3 Previous(int i) => new(PX[i], PY[i], PZ[i]);
    public Vector3 RestPosition(int i) => _rest[i];
    public float Thickness(int i) => i < ClothParticles ? Material.Thickness : HairThickness;
    public static int Cloth(int u, int v) => v * SkirtU + (u % SkirtU + SkirtU) % SkirtU;
    public static int Hair(int strand, int node) => ClothParticles + strand * Nodes + node;
    public void SetPosition(int i, Vector3 p) { X[i] = p.X; Y[i] = p.Y; Z[i] = p.Z; }
    public void SetPrevious(int i, Vector3 p) { PX[i] = p.X; PY[i] = p.Y; PZ[i] = p.Z; }

    public static string FabricName(FabricPreset p) => FabricMaterial.Of(p).Name;

    public static Vector3 Frame(Quaternion q, Vector3 pivot, Vector3 restPivot, Vector3 rest) => Vector3.Transform(rest - restPivot, q) + pivot;

    // ══════════════════════ 휴지 기하(비핫패스) ══════════════════════
    private void BuildRest(out int[] pins)
    {
        var rest = RagdollSkeleton.Rest;
        var side = Vector3.Normalize(rest[(int)RagdollJoint.RHip] - rest[(int)RagdollJoint.LHip]);
        var up = Vector3.Normalize(rest[(int)RagdollJoint.Chest] - rest[(int)RagdollJoint.Pelvis]);
        var fwd = Vector3.Normalize(Vector3.Cross(side, up));
        var pel = rest[(int)RagdollJoint.Pelvis];
        var pl = new List<int>();
        for (var v = 0; v < SkirtV; v++)
        {
            var s = v / (float)(SkirtV - 1);
            var r = WaistR + (HemR - WaistR) * s;
            var y = WaistLift - SkirtLen * s;
            for (var u = 0; u < SkirtU; u++)
            {
                var a = MathF.Tau * u / SkirtU;
                var i = Cloth(u, v);
                _rest[i] = pel + up * y + side * (r * MathF.Cos(a)) + fwd * (r * MathF.Sin(a));
                _home[i] = (byte)RagdollJoint.Pelvis;
                _pinJ[i] = v == 0 ? (byte)RagdollJoint.Pelvis : Free;
                if (v == 0) pl.Add(i);
            }
        }
        // 두피 뿌리(황금비 준균일 · 정수리 전 방위 / 측후두 ±120° · 얼굴 전면 제외) → 머리 캡슐 표면 + 리프트 · 성장 시 캡슐 투영(두피 위로 눕힌 뒤 하강)
        Vector3 hA = rest[(int)RagdollJoint.Head], hB = rest[(int)RagdollJoint.HeadTop];
        var hr = RagdollSkeleton.BoneRadius[(int)RagdollJoint.HeadTop];
        var hc = (hA + hB) * 0.5f;
        var seg = HairLen / (Nodes - 1);
        for (var made = 0; made < Strands; made++)
        {
            var g = made * 0.6180339887f; g -= MathF.Floor(g);
            var se = -0.05f + 1.05f * (made + 0.5f) / Strands; var ce = MathF.Sqrt(MathF.Max(0f, 1f - se * se));
            var az = -MathF.PI * 0.5f + (g - 0.5f) * 2f * (se > 0.6f ? MathF.PI : 2.1f);
            var d = side * (ce * MathF.Cos(az)) + fwd * (ce * MathF.Sin(az)) + up * se;
            var far = hc + d;
            var q = ClosestOnSeg(hA, hB, far, out _);
            var n = Vector3.Normalize(far - q);
            var root = q + n * (hr + ScalpLift);
            var x = root;
            for (var j = 0; j < Nodes; j++)
            {
                var i = Hair(made, j);
                if (j > 0)
                {
                    var bl = Math.Clamp(j / 4f, 0f, 1f);
                    var dir = Vector3.Normalize(n * (1f - bl) - up * bl - fwd * 0.15f * bl);
                    x += dir * seg;
                    var qq = ClosestOnSeg(hA, hB, x, out _); var dd = Vector3.Distance(x, qq);
                    if (dd < hr + ScalpLift) x = qq + (x - qq) / MathF.Max(dd, 1e-6f) * (hr + ScalpLift);
                    var nk = rest[(int)RagdollJoint.Neck]; var qn = ClosestOnSeg(nk, hA, x, out _); var dn = Vector3.Distance(x, qn);
                    var rn = RagdollSkeleton.BoneRadius[(int)RagdollJoint.Head] + 0.01f;
                    if (dn < rn) x = qn + (x - qn) / MathF.Max(dn, 1e-6f) * rn;
                }
                _rest[i] = x;
                _home[i] = (byte)RagdollJoint.Head;
                _pinJ[i] = j < 2 ? (byte)RagdollJoint.Head : Free;
                if (j < 2) pl.Add(i);
            }
        }
        pins = pl.ToArray();
    }

    private void BuildConstraints(out int[] ca, out int[] cb, out float[] cl, out int structEnd, out int shearEnd, out int bendEnd, out int hairStretchEnd, out int hairBendEnd)
    {
        var a = new List<int>(); var b = new List<int>();
        void Add(int i, int j) { a.Add(i); b.Add(j); }
        // 구조(행 순서 상→하: 세로 → 가로) — 고정 행부터 Gauss-Seidel 전파
        for (var v = 0; v < SkirtV - 1; v++)
        {
            for (var u = 0; u < SkirtU; u++) Add(Cloth(u, v), Cloth(u, v + 1));
            for (var u = 0; u < SkirtU; u++) Add(Cloth(u, v + 1), Cloth(u + 1, v + 1));
        }
        for (var u = 0; u < SkirtU; u++) Add(Cloth(u, 0), Cloth(u + 1, 0));
        structEnd = a.Count;
        for (var v = 0; v < SkirtV - 1; v++)
            for (var u = 0; u < SkirtU; u++) { Add(Cloth(u, v), Cloth(u + 1, v + 1)); Add(Cloth(u + 1, v), Cloth(u, v + 1)); }
        shearEnd = a.Count;
        for (var v = 0; v < SkirtV; v++)
            for (var u = 0; u < SkirtU; u++) { Add(Cloth(u, v), Cloth(u + 2, v)); if (v + 2 < SkirtV) Add(Cloth(u, v), Cloth(u, v + 2)); }
        bendEnd = a.Count;
        for (var s = 0; s < Strands; s++) for (var j = 0; j < Nodes - 1; j++) Add(Hair(s, j), Hair(s, j + 1));
        hairStretchEnd = a.Count;
        for (var s = 0; s < Strands; s++) for (var j = 0; j < Nodes - 2; j++) Add(Hair(s, j), Hair(s, j + 2));
        hairBendEnd = a.Count;
        ca = a.ToArray(); cb = b.ToArray(); cl = new float[ca.Length];
        for (var c = 0; c < ca.Length; c++) cl[c] = Vector3.Distance(_rest[ca[c]], _rest[cb[c]]);
    }

    private void ApplyDamping()
    {
        Array.Fill(_damp, 1f);
        _damp.AsSpan(0, ClothParticles).Fill(Material.Damping);
        _damp.AsSpan(ClothParticles, HairParticles).Fill(HairDamping);
        for (var i = 0; i < Particles; i++) W[i] = _pinJ[i] == Free ? 1f : 0f;
    }

    public static Vector3 ClosestOnSeg(Vector3 a, Vector3 b, Vector3 x, out float t)
    {
        var d = b - a; var l2 = d.LengthSquared();
        t = l2 <= 1e-12f ? 0f : Math.Clamp(Vector3.Dot(x - a, d) / l2, 0f, 1f);
        return a + d * t;
    }

    /// <summary>원단 프리셋 교체(비핫패스) — 감쇠 · 색 재기록.</summary>
    public void SetFabric(FabricPreset p, GaussianSplatScene? scene = null)
    {
        Fabric = p; Material = FabricMaterial.Of(p);
        _damp.AsSpan(0, ClothParticles).Fill(Material.Damping);
        if (scene is not null && _clothStart >= 0) WriteClothColors(scene);
    }

    public void SetHairDamping(float d) { HairDamping = Math.Clamp(d, 0f, 0.5f); _damp.AsSpan(ClothParticles, HairParticles).Fill(HairDamping); }

    /// <summary>스플랫 바인드(비핫패스) — 장면 슬롯 [clothStart, +20 000) · [hairStart, +15 000).</summary>
    public void Bind(GaussianSplatScene scene, int clothStart, int hairStart, uint seed = 0xC10Fu)
    {
        _clothStart = clothStart; _hairStart = hairStart;
        var rng = seed == 0 ? 1u : seed;
        float R() { rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5; return (rng >> 8) * (1f / 16777216f); }
        const int Cells = SkirtU * (SkirtV - 1);
        Span<float> cum = stackalloc float[Cells];
        var tot = 0f;
        for (var c = 0; c < Cells; c++)
        {
            int u = c % SkirtU, v = c / SkirtU;
            var du = _rest[Cloth(u + 1, v)] - _rest[Cloth(u, v)]; var dv = _rest[Cloth(u, v + 1)] - _rest[Cloth(u, v)];
            tot += Vector3.Cross(du, dv).Length(); cum[c] = tot;
        }
        {
            var du = _rest[Cloth(1, 0)] - _rest[Cloth(0, 0)]; var dv = _rest[Cloth(0, 1)] - _rest[Cloth(0, 0)];
            var mid = (_rest[Cloth(0, 0)] + _rest[Cloth(1, 1)]) * 0.5f; var ax = RagdollSkeleton.Rest[(int)RagdollJoint.Pelvis];
            _skirtSign = Vector3.Dot(Vector3.Cross(du, dv), new Vector3(mid.X - ax.X, 0f, mid.Z - ax.Z)) >= 0f ? 1f : -1f;
        }
        var disc = MathF.Sqrt(tot / ClothSplats);
        for (var k = 0; k < ClothSplats; k++)
        {
            var target = R() * tot; int lo = 0, hi = Cells - 1;
            while (lo < hi) { var m = (lo + hi) >> 1; if (cum[m] < target) lo = m + 1; else hi = m; }
            int u = lo % SkirtU, v = lo / SkirtU;
            _q00[k] = Cloth(u, v); _q10[k] = Cloth(u + 1, v); _q01[k] = Cloth(u, v + 1); _q11[k] = Cloth(u + 1, v + 1);
            _su[k] = R(); _sv[k] = R(); _sOff[k] = (R() - 0.5f) * 0.6f;
            _sInvDu[k] = 1f / MathF.Max(Vector3.Distance(_rest[_q00[k]], _rest[_q10[k]]), 1e-5f);
            _sInvDv[k] = 1f / MathF.Max(Vector3.Distance(_rest[_q00[k]], _rest[_q01[k]]), 1e-5f);
            _sScU[k] = disc * 0.95f; _sScV[k] = disc * 0.95f;
            var stripe = 0.5f + 0.5f * MathF.Sin(MathF.Tau * (u + _su[k]) * 6f / SkirtU);
            _shade[k] = 0.78f + 0.22f * stripe * (0.85f + 0.15f * R());
            scene.Write(clothStart + k, Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, new Vector3(disc, disc, Material.Thickness * 0.3f), 0.96f, Material.Color * _shade[k], 7);
        }
        var seg = HairLen / (Nodes - 1);
        var dark = new Vector3(0.10f, 0.06f, 0.035f); var tip = new Vector3(0.28f, 0.17f, 0.09f);
        for (var k = 0; k < HairSplats; k++)
        {
            var s = (int)(R() * Strands) % Strands; var j = (int)(R() * (Nodes - 1)) % (Nodes - 1);
            var i = Hair(s, j);
            _hSeg[k] = i; _ht[k] = R();
            var T = Vector3.Normalize(_rest[i + 1] - _rest[i]);
            var t1 = Vector3.Normalize(Vector3.Cross(MathF.Abs(T.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX, T)); var t2 = Vector3.Cross(T, t1);
            var a = MathF.Tau * R(); var rr = StrandRadius * MathF.Sqrt(R());
            _hOff[k] = t1 * (rr * MathF.Cos(a)) + t2 * (rr * MathF.Sin(a));
            _hR[k] = 0.0016f; _hL[k] = seg * 0.65f;
            var f = (j + _ht[k]) / (Nodes - 1);
            scene.Write(hairStart + k, Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, new Vector3(_hR[k], _hR[k], _hL[k]), 0.85f, Vector3.Lerp(dark, tip, f * f) * (0.8f + 0.4f * R()), 8);
        }
    }

    private void WriteClothColors(GaussianSplatScene sc)
    {
        const float C0 = SphericalHarmonicsLightingSynthesizer.C0;
        var col = Material.Color;
        for (var k = 0; k < ClothSplats; k++)
        {
            var i = _clothStart + k; var c = col * _shade[k];
            sc.Dr[i] = (c.X - 0.5f) / C0; sc.Dg[i] = (c.Y - 0.5f) / C0; sc.Db[i] = (c.Z - 0.5f) / C0;
        }
    }

    /// <summary>휴지 형상을 현재 FK 자세로 배치(속도 0).</summary>
    public void Place(AvatarFkAnimator anim)
    {
        var rest = RagdollSkeleton.Rest;
        for (var i = 0; i < Particles; i++)
        {
            var j = _home[i];
            var p = Frame(anim.Global(j), anim.Positions[j], rest[j], _rest[i]);
            SetPosition(i, p); SetPrevious(i, p);
        }
        _placed = true;
    }

    // ══════════════════════ 프레임(무할당) ══════════════════════
    /// <summary>
    /// 프레임 dt 를 ≤ 1/60 s 하위 단계(최대 <see cref="MaxSubsteps"/>)로 분할 — 고정 노드는 직전 → 현재 FK 자세 선형 보간,
    /// 신체 캡슐은 프레임 끝 자세 고정(애니메이션 프레임 표본) → 프레임 저하 시 캡슐 도약 터널링은 가드 CCD 가 처리.
    /// </summary>
    public void Step(float dt, AvatarFkAnimator anim, AerodynamicWindBodyCollisionSynthesizer? aero)
    {
        if (!(dt > 0f) || !float.IsFinite(dt)) return;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!_placed) Place(anim);
        var rest = RagdollSkeleton.Rest;
        var pos = anim.Positions;
        for (var k = 0; k < _pins.Length; k++)
        {
            var i = _pins[k]; var j = _pinJ[i];
            _pinS[k] = Position(i);
            _pinT[k] = Frame(anim.Global(j), pos[j], rest[j], _rest[i]);
        }
        var subs = Math.Clamp((int)MathF.Ceiling(dt * 60f - 1e-3f), 1, MaxSubsteps);
        var h = dt / subs;
        for (var s = 1; s <= subs; s++) Substep(h, s / (float)subs, aero);
        Substeps = subs;
        Frames++;
        LastStepUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private void Substep(float dt, float alpha, AerodynamicWindBodyCollisionSynthesizer? aero)
    {
        AX.AsSpan(0, Particles).Clear(); AZ.AsSpan(0, Particles).Clear();
        AY.AsSpan(0, Particles).Fill(Gravity.Y);
        aero?.ApplyForces(this, dt);
        Integrate(dt);
        for (var k = 0; k < _pins.Length; k++) SetPosition(_pins[k], Vector3.Lerp(_pinS[k], _pinT[k], alpha));
        var n = Math.Clamp(Iterations, 1, 64);
        var inv = 1f / n;
        var m = Material;
        float kS = Corr(m.Stretch, inv), kSh = Corr(m.Shear, inv), kB = Corr(m.Bend, inv), kH = Corr(Math.Clamp(HairStiffness, 0f, 1f) * 0.9f, inv);
        for (var it = 0; it < n; it++)
        {
            Project(0, _structEnd, kS);
            Project(_structEnd, _shearEnd, kSh);
            Project(_shearEnd, _bendEnd, kB);
            Project(_hairStretchEnd, _hairBendEnd, kH);
            if (it == n - 1) FollowTheLeader(); else Project(_bendEnd, _hairStretchEnd, 1f);
            if (TetherEnabled) Tether();
            aero?.Collide(this, it == n - 1);
        }
    }

    private static float Corr(float k, float inv) => k >= 1f ? 1f : k <= 0f ? 0f : 1f - MathF.Pow(1f - k, inv);

    private void Integrate(float dt)
    {
        var dt2 = dt * dt;
        var i = 0;
        if (UseSimd && Vector.IsHardwareAccelerated)
        {
            var vx = MemoryMarshal.Cast<float, Vector<float>>(X.AsSpan()); var vy = MemoryMarshal.Cast<float, Vector<float>>(Y.AsSpan()); var vz = MemoryMarshal.Cast<float, Vector<float>>(Z.AsSpan());
            var px = MemoryMarshal.Cast<float, Vector<float>>(PX.AsSpan()); var py = MemoryMarshal.Cast<float, Vector<float>>(PY.AsSpan()); var pz = MemoryMarshal.Cast<float, Vector<float>>(PZ.AsSpan());
            var ax = MemoryMarshal.Cast<float, Vector<float>>(AX.AsSpan()); var ay = MemoryMarshal.Cast<float, Vector<float>>(AY.AsSpan()); var az = MemoryMarshal.Cast<float, Vector<float>>(AZ.AsSpan());
            var dm = MemoryMarshal.Cast<float, Vector<float>>(_damp.AsSpan());
            var one = Vector<float>.One; var d2 = new Vector<float>(dt2);
            for (var k = 0; k < vx.Length; k++)
            {
                var keep = one - dm[k];
                Vector<float> x = vx[k], y = vy[k], z = vz[k];
                vx[k] = x + (x - px[k]) * keep + ax[k] * d2; px[k] = x;
                vy[k] = y + (y - py[k]) * keep + ay[k] * d2; py[k] = y;
                vz[k] = z + (z - pz[k]) * keep + az[k] * d2; pz[k] = z;
            }
            i = vx.Length * Vector<float>.Count;
        }
        for (; i < Pad; i++)
        {
            var keep = 1f - _damp[i];
            float x = X[i], y = Y[i], z = Z[i];
            X[i] = x + (x - PX[i]) * keep + AX[i] * dt2; PX[i] = x;
            Y[i] = y + (y - PY[i]) * keep + AY[i] * dt2; PY[i] = y;
            Z[i] = z + (z - PZ[i]) * keep + AZ[i] * dt2; PZ[i] = z;
        }
    }

    /// <summary>스커트 열 고정점 장거리 테더(휴지 직선 거리 초과분만 투영).</summary>
    private void Tether()
    {
        var x = X; var y = Y; var z = Z;
        for (var i = SkirtU; i < ClothParticles; i++)
        {
            var r = i % SkirtU;
            float dx = x[i] - x[r], dy = y[i] - y[r], dz = z[i] - z[r];
            var d2 = dx * dx + dy * dy + dz * dz; var L = _tether[i];
            if (d2 <= L * L) continue;
            var s = L / MathF.Sqrt(d2);
            x[i] = x[r] + dx * s; y[i] = y[r] + dy * s; z[i] = z[r] + dz * s;
        }
    }

    /// <summary>헤어 FTL — 가닥별 뿌리 → 끝 순서로 자식만 휴지 길이에 맞춤(부모 확정 · 비신장 보장).</summary>
    private void FollowTheLeader()
    {
        var x = X; var y = Y; var z = Z;
        for (var c = _bendEnd; c < _hairStretchEnd; c++)
        {
            int a = _ca[c], b = _cb[c];
            if (W[b] <= 0f) continue;
            float dx = x[b] - x[a], dy = y[b] - y[a], dz = z[b] - z[a];
            var d = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            if (d < 1e-9f) continue;
            var s = _cl[c] / d;
            x[b] = x[a] + dx * s; y[b] = y[a] + dy * s; z[b] = z[a] + dz * s;
        }
    }

    private void Project(int lo, int hi, float k)
    {
        if (k <= 0f) return;
        var x = X; var y = Y; var z = Z; var w = W;
        for (var c = lo; c < hi; c++)
        {
            int a = _ca[c], b = _cb[c];
            float wa = w[a], wb = w[b], ws = wa + wb;
            if (ws <= 0f) continue;
            float dx = x[b] - x[a], dy = y[b] - y[a], dz = z[b] - z[a];
            var d = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            if (d < 1e-9f) continue;
            var s = k * (d - _cl[c]) / (d * ws);
            x[a] += wa * s * dx; y[a] += wa * s * dy; z[a] += wa * s * dz;
            x[b] -= wb * s * dx; y[b] -= wb * s * dy; z[b] -= wb * s * dz;
        }
    }

    /// <summary>스커트 격자 법선(이웃 중심 차분 · 바깥 방향).</summary>
    public Vector3 ClothNormal(int u, int v)
    {
        var du = Position(Cloth(u + 1, v)) - Position(Cloth(u - 1, v));
        var dv = Position(Cloth(u, Math.Min(v + 1, SkirtV - 1))) - Position(Cloth(u, Math.Max(v - 1, 0)));
        var n = Vector3.Cross(du, dv) * _skirtSign;
        var l = n.Length();
        return l > 1e-12f ? n / l : Vector3.UnitY;
    }

    /// <summary>공분산 정렬 스플랫 기록(무할당) — 의상: 쌍선형 접선 프레임 · 헤어: 스트랜드 흐름 방향.</summary>
    public void WriteSplats(GaussianSplatScene sc, Quaternion headRot)
    {
        if (_clothStart < 0) return;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var sn = Material.Thickness * 0.3f; var th = Material.Thickness;
        for (var k = 0; k < ClothSplats; k++)
        {
            Vector3 p00 = Position(_q00[k]), p10 = Position(_q10[k]), p01 = Position(_q01[k]), p11 = Position(_q11[k]);
            float u = _su[k], v = _sv[k];
            var du = (p10 - p00) * (1f - v) + (p11 - p01) * v;
            var dv = (p01 - p00) * (1f - u) + (p11 - p10) * u;
            var nn = Vector3.Cross(du, dv) * _skirtSign; var nl = nn.Length();
            var n = nl > 1e-12f ? nn / nl : Vector3.UnitY;
            var p = p00 + (p10 - p00) * u + (p01 - p00) * v + (p00 - p10 - p01 + p11) * (u * v) + n * (_sOff[k] * th);
            var a = du * (_sScU[k] * _sInvDu[k]); var b = dv * (_sScV[k] * _sInvDv[k]); var c = n * sn;
            WriteCov(sc, _clothStart + k, p, a, b, c);
        }
        for (var k = 0; k < HairSplats; k++)
        {
            var i = _hSeg[k];
            Vector3 a = Position(i), b = Position(i + 1);
            var d = b - a; var l = d.Length();
            var T = l > 1e-9f ? d / l : Vector3.UnitY;
            var o = Vector3.Transform(_hOff[k], headRot); o -= T * Vector3.Dot(o, T);
            var p = a + d * _ht[k] + o;
            float r2 = _hR[k] * _hR[k], l2 = _hL[k] * _hL[k] - r2;
            var j = _hairStart + k;
            sc.X[j] = p.X; sc.Y[j] = p.Y; sc.Z[j] = p.Z;
            sc.C00[j] = r2 + l2 * T.X * T.X; sc.C01[j] = l2 * T.X * T.Y; sc.C02[j] = l2 * T.X * T.Z;
            sc.C11[j] = r2 + l2 * T.Y * T.Y; sc.C12[j] = l2 * T.Y * T.Z; sc.C22[j] = r2 + l2 * T.Z * T.Z;
            sc.MaxSigma[j] = _hL[k];
        }
        LastSplatUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private static void WriteCov(GaussianSplatScene sc, int i, Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        sc.X[i] = p.X; sc.Y[i] = p.Y; sc.Z[i] = p.Z;
        sc.C00[i] = a.X * a.X + b.X * b.X + c.X * c.X;
        sc.C01[i] = a.X * a.Y + b.X * b.Y + c.X * c.Y;
        sc.C02[i] = a.X * a.Z + b.X * b.Z + c.X * c.Z;
        sc.C11[i] = a.Y * a.Y + b.Y * b.Y + c.Y * c.Y;
        sc.C12[i] = a.Y * a.Z + b.Y * b.Z + c.Y * c.Z;
        sc.C22[i] = a.Z * a.Z + b.Z * b.Z + c.Z * c.Z;
        var tr = sc.C00[i] + sc.C11[i] + sc.C22[i];
        sc.MaxSigma[i] = tr > 0f && float.IsFinite(tr) ? MathF.Sqrt(tr) : 0.01f;
    }

    /// <summary>스플랫 k(0 기반 · 헤어) 의 흐름 방향(테스트 · 오버레이).</summary>
    public Vector3 HairTangent(int k)
    {
        var i = _hSeg[k]; var d = Position(i + 1) - Position(i); var l = d.Length();
        return l > 1e-9f ? d / l : Vector3.UnitY;
    }

    public int HairSegment(int k) => _hSeg[k];
    public int ClothCorner(int k) => _q00[k];
}
