using System.Numerics;
using System.Runtime.Intrinsics;
using PromptFlowStudio.Services.Garment;
using PromptFlowStudio.Services.SkinOptics;

namespace PromptFlowStudio.Services.SpatialHaptics;

public enum HapticMaterial : byte { Skin = 0, Silk, Cotton, Denim, Leather, Prop }

/// <summary>햅틱 인터페이스 포인트(HIP): v17.13 손가락 끝 5 + v17.22 XR 컨트롤러 팁.</summary>
public enum HapticPoint : byte { Thumb = 0, Index, Middle, Ring, Little, Controller }

/// <summary>재질별 기본 강성(N/m) · 점성(N·s/m) · 마찰(직물은 v17.16 프리셋 마찰 계수) · 미세 거칠기.</summary>
public static class HapticMaterials
{
    public const int Count = 6;
    public static readonly string[] Names = { "피부", "실크", "면", "데님", "가죽", "소품(목재)" };
    public static readonly float[] DefaultStiffness = { 900f, 700f, 1100f, 1600f, 3000f, 6000f };
    public static readonly float[] DefaultDamping = { 1.2f, 0.8f, 1.2f, 1.5f, 2f, 2f };

    public static HapticMaterial FromFabric(FabricPreset p) => p switch
    {
        FabricPreset.Silk => HapticMaterial.Silk,
        FabricPreset.Denim => HapticMaterial.Denim,
        FabricPreset.Leather => HapticMaterial.Leather,
        _ => HapticMaterial.Cotton,
    };

    /// <summary>건조 쿨롱 마찰 계수 μ(직물 = <see cref="AnisotropicFabricDrapingWrinkleSynthesizer.Presets"/> 마찰).</summary>
    public static float Friction(HapticMaterial m) => m switch
    {
        HapticMaterial.Silk => AnisotropicFabricDrapingWrinkleSynthesizer.Presets[(int)FabricPreset.Silk].Friction,
        HapticMaterial.Cotton => AnisotropicFabricDrapingWrinkleSynthesizer.Presets[(int)FabricPreset.Cotton].Friction,
        HapticMaterial.Denim => AnisotropicFabricDrapingWrinkleSynthesizer.Presets[(int)FabricPreset.Denim].Friction,
        HapticMaterial.Leather => AnisotropicFabricDrapingWrinkleSynthesizer.Presets[(int)FabricPreset.Leather].Friction,
        HapticMaterial.Skin => 0.45f,
        _ => 0.3f,
    };
}

/// <summary>HIP 1개의 접촉 상태(1 ms 서브사이클 결과).</summary>
public struct HapticContact
{
    public bool InContact;
    public HapticMaterial Material;
    public int Sphere;
    public Vector3 Hip, Proxy, Normal, Force;
    public float Penetration, NormalForce, FrictionForce, SlideSpeed;
    public bool Sticking;
}

/// <summary>무할당 SIMD 커널(스칼라 쌍둥이와 비트 동일 — FMA 미사용 · 동일 연산 순서).</summary>
public static class HapticKernels
{
    /// <summary>구 SDF 일괄: d[i] = |p − c_i| − r_i (길이 4 배수).</summary>
    public static void SphereSdfSimd(ReadOnlySpan<float> cx, ReadOnlySpan<float> cy, ReadOnlySpan<float> cz, ReadOnlySpan<float> r, Vector3 p, Span<float> d)
    {
        var px = Vector128.Create(p.X); var py = Vector128.Create(p.Y); var pz = Vector128.Create(p.Z);
        for (var i = 0; i + 4 <= d.Length; i += 4)
        {
            var dx = px - Vector128.Create(cx[i], cx[i + 1], cx[i + 2], cx[i + 3]);
            var dy = py - Vector128.Create(cy[i], cy[i + 1], cy[i + 2], cy[i + 3]);
            var dz = pz - Vector128.Create(cz[i], cz[i + 1], cz[i + 2], cz[i + 3]);
            var v = Vector128.Sqrt(dx * dx + dy * dy + dz * dz) - Vector128.Create(r[i], r[i + 1], r[i + 2], r[i + 3]);
            v.CopyTo(d.Slice(i, 4));
        }
    }

    public static void SphereSdfScalar(ReadOnlySpan<float> cx, ReadOnlySpan<float> cy, ReadOnlySpan<float> cz, ReadOnlySpan<float> r, Vector3 p, Span<float> d)
    {
        for (var i = 0; i < d.Length; i++)
        {
            float dx = p.X - cx[i], dy = p.Y - cy[i], dz = p.Z - cz[i];
            d[i] = MathF.Sqrt(dx * dx + dy * dy + dz * dz) - r[i];
        }
    }

    /// <summary>켈빈-보이트 법선력 일괄: Fn = max(0, k·δ + c·δ̇) (당김 금지 · 길이 4 배수).</summary>
    public static void KelvinVoigtSimd(ReadOnlySpan<float> k, ReadOnlySpan<float> c, ReadOnlySpan<float> pen, ReadOnlySpan<float> penRate, Span<float> fn)
    {
        var z = Vector128<float>.Zero;
        for (var i = 0; i + 4 <= fn.Length; i += 4)
        {
            var v = Vector128.Create(k[i], k[i + 1], k[i + 2], k[i + 3]) * Vector128.Create(pen[i], pen[i + 1], pen[i + 2], pen[i + 3])
                  + Vector128.Create(c[i], c[i + 1], c[i + 2], c[i + 3]) * Vector128.Create(penRate[i], penRate[i + 1], penRate[i + 2], penRate[i + 3]);
            Vector128.ConditionalSelect(Vector128.GreaterThan(v, z), v, z).CopyTo(fn.Slice(i, 4));
        }
    }

    public static void KelvinVoigtScalar(ReadOnlySpan<float> k, ReadOnlySpan<float> c, ReadOnlySpan<float> pen, ReadOnlySpan<float> penRate, Span<float> fn)
    {
        for (var i = 0; i < fn.Length; i++)
        {
            var v = k[i] * pen[i] + c[i] * penRate[i];
            fn[i] = v > 0f ? v : 0f;
        }
    }
}

/// <summary>
/// [PromptFlow Studio v17.24.0] 🧤 실시간 1000 Hz 6DoF 역각 피드백 &amp; 프록시 접촉 엔진(<see cref="SubStep"/> 무할당).
///   ▸ 접촉 장면: 액터 몸통/어깨(의상 직물) · 머리/목/손목(피부 · 요골 맥동) · 소품 구 = 구 12 슬롯 SoA(60 Hz 갱신 · 호흡/PPG 변위).
///   ▸ 갓-오브젝트: HIP 침투 시 구 합집합 표면으로 반복 사영(≤ 4회) → 표면 구속 프록시 · 마찰 원뿔(|접선 신장| ≤ μ·Fn/k) 고착/미끄럼.
///   ▸ 켈빈-보이트: Fn = max(0, k·δ + c·δ̇) (SIMD 4 레인) · 마찰력 = k·(δ − n·δn) · 6DoF 렌치 = ΣF · Σ(r × F)(손바닥 중심 기준).
///   ▸ 디바이스: HIP별 점질량(60 g) + 사용자 손 임피던스(스프링·댐퍼) · 1 샘플 전송 지연 → 가드(<see cref="DualRatePassivityAntiBuzzingGuard"/>) 후 모터 출력.
/// </summary>
public sealed class RealtimeKinestheticForceFeedbackEngine
{
    public const int Hips = 6, Lanes = 8, Spheres = 12, ProjectIters = 4;
    public const float Dt = 0.001f, DeviceMass = 0.06f, DeviceDamping = 0.3f, HandStiffness = 120f, HandDamping = 1.2f, FrameMs = 1000f / 60f;
    public const float BreathAmp = 0.008f, PulseAmp = 0.0004f;
    public static readonly string[] HipNames = { "엄지", "검지", "중지", "약지", "소지", "XR 컨트롤러" };
    public static readonly Vector3[] FingerOffsets =
    {
        new(-0.05f, -0.03f, 0f), new(-0.025f, 0.02f, 0f), new(0f, 0.025f, 0f), new(0.022f, 0.018f, 0f), new(0.042f, 0.008f, 0f),
    };
    // 기본 장면(레스트): 가슴 좌/우 · 복부 · 어깨 좌/우 · 머리 · 목 · 손목 · 전완 · 소품 구 · 패딩 2
    public static readonly Vector3[] RestCenter =
    {
        new(-0.09f, 1.32f, 0.4f), new(0.09f, 1.32f, 0.4f), new(0f, 1.12f, 0.4f), new(-0.19f, 1.42f, 0.42f), new(0.19f, 1.42f, 0.42f),
        new(0f, 1.62f, 0.42f), new(0f, 1.49f, 0.42f), new(0.3f, 1.1f, 0.3f), new(0.27f, 1.2f, 0.34f), new(-0.32f, 1.05f, 0.15f),
        new(0f, -50f, 0f), new(0f, -50f, 0f),
    };
    public static readonly float[] RestRadius = { 0.12f, 0.12f, 0.13f, 0.07f, 0.07f, 0.1f, 0.05f, 0.04f, 0.045f, 0.06f, 0.001f, 0.001f };
    public const int WristSphere = 7, PropSphere = 9, GarmentSpheres = 5;

    private readonly float[] _cx = new float[Spheres], _cy = new float[Spheres], _cz = new float[Spheres], _r = new float[Spheres];
    private readonly float[] _pcx = new float[Spheres], _pcy = new float[Spheres], _pcz = new float[Spheres], _pr = new float[Spheres];
    private readonly float[] _wcx = new float[Spheres], _wcy = new float[Spheres], _wcz = new float[Spheres], _wr = new float[Spheres];
    private readonly HapticMaterial[] _mat = new HapticMaterial[Spheres];
    private readonly float[] _sdf = new float[Spheres];
    private readonly float[] _k = new float[Lanes], _c = new float[Lanes], _pen = new float[Lanes], _penRate = new float[Lanes], _fn = new float[Lanes];
    private readonly float[] _stiff = new float[HapticMaterials.Count], _damp = new float[HapticMaterials.Count];
    private readonly HapticContact[] _contacts = new HapticContact[Hips];
    private readonly Vector3[] _x = new Vector3[Hips], _v = new Vector3[Hips], _pending = new Vector3[Hips], _applied = new Vector3[Hips], _proxyPrev = new Vector3[Hips];
    private readonly Vector3[] _target = new Vector3[Hips], _targetVel = new Vector3[Hips];
    private double _frameStartMs, _hapMs;
    private HapticMaterial _garment = HapticMaterial.Denim;

    public RealtimeKinestheticForceFeedbackEngine(DualRatePassivityAntiBuzzingGuard guard)
    {
        Guard = guard;
        for (var m = 0; m < HapticMaterials.Count; m++) { _stiff[m] = HapticMaterials.DefaultStiffness[m]; _damp[m] = HapticMaterials.DefaultDamping[m]; }
        UpdateScene(0, 0f, 0f);
        UpdateScene(0, 0f, 0f);
        for (var i = 0; i < Hips; i++)
        {
            HipTarget(0, i, true, out _x[i], out _);
            _proxyPrev[i] = _x[i];
        }
    }

    public DualRatePassivityAntiBuzzingGuard Guard { get; }
    public bool UseSimd { get; set; } = Vector128.IsHardwareAccelerated;
    public bool HandMotion { get; set; } = true;
    public float Wetness { get; set; }
    public long SubSteps { get; private set; }
    public double SimMs => _hapMs;
    public Vector3 WrenchForce { get; private set; }
    public Vector3 WrenchTorque { get; private set; }
    public Vector3 PalmCenter { get; private set; }
    public int ActiveContacts { get; private set; }
    public float PeakForce { get; private set; }
    public float LastSubStepUs { get; private set; }
    public float MaxSubStepUs { get; private set; }
    public HapticMaterial Garment
    {
        get => _garment;
        set { _garment = value; for (var s = 0; s < GarmentSpheres; s++) _mat[s] = value; }
    }

    public ref readonly HapticContact Contact(int i) => ref _contacts[i];
    public ReadOnlySpan<HapticContact> Contacts => _contacts;
    public Vector3 DevicePosition(int i) => _x[i];
    public Vector3 DeviceVelocity(int i) => _v[i];
    public Vector3 AppliedForce(int i) => _applied[i];
    public Vector3 SphereCenter(int s) => new(_wcx[s], _wcy[s], _wcz[s]);
    public float SphereRadius(int s) => _wr[s];
    public HapticMaterial SphereMaterial(int s) => _mat[s];
    public float Stiffness(HapticMaterial m) => _stiff[(int)m];
    public float Damping(HapticMaterial m) => _damp[(int)m];
    public void SetStiffness(HapticMaterial m, float k) => _stiff[(int)m] = Math.Clamp(k, 50f, 8000f);
    public void SetDamping(HapticMaterial m, float c) => _damp[(int)m] = Math.Clamp(c, 0f, 20f);

    /// <summary>젖음에 따른 윤활 마찰 μ(v17.19 활주 피막 · 건조 대비 최대 −55 %).</summary>
    public float EffectiveFriction(HapticMaterial m) => HapticMaterials.Friction(m) * (1f - 0.55f * Math.Clamp(Wetness, 0f, 1f));

    /// <summary>60 Hz 렌더 프레임 장면 갱신(호흡 흉곽 변위 · 손목 요골 맥동) — 직전 프레임은 1차 보간용으로 보존.</summary>
    public void UpdateScene(double frameMs, float breath, float pulse)
    {
        Array.Copy(_cx, _pcx, Spheres); Array.Copy(_cy, _pcy, Spheres); Array.Copy(_cz, _pcz, Spheres); Array.Copy(_r, _pr, Spheres);
        for (var s = 0; s < Spheres; s++)
        {
            var c = RestCenter[s];
            var r = RestRadius[s];
            if (s < 3) c.Z -= BreathAmp * breath;
            if (s == WristSphere) r += PulseAmp * pulse;
            _cx[s] = c.X; _cy[s] = c.Y; _cz[s] = c.Z; _r[s] = r;
            _mat[s] = s < GarmentSpheres ? _garment : s == PropSphere ? HapticMaterial.Prop : HapticMaterial.Skin;
        }
        _frameStartMs = frameMs;
    }

    /// <summary>사용자 손/컨트롤러 목표 궤적(8 s 주기): 0–3 s 가슴 의상 쓸기 · 3–5 s 손목 맥진 · 5–8 s 컨트롤러로 소품 누르며 두드리기.</summary>
    public static void HipTarget(double tMs, int hip, bool motion, out Vector3 pos, out float phaseSec)
    {
        var t = motion ? (float)(tMs / 1000.0 % 8.0) : 0.5f;
        phaseSec = t;
        if (hip == (int)HapticPoint.Controller)
        {
            var rest = new Vector3(-0.32f, 1.05f, 0.15f - 0.06f - 0.03f);
            if (t >= 5f)
            {
                var u = t - 5f;
                var env = MathF.Min(1f, u / 0.3f) * MathF.Min(1f, (3f - u) / 0.3f);
                rest.Z += env * (0.05f + 0.015f * MathF.Sin(2f * MathF.PI * 1.5f * u));   // 소품 누르며 1.5 Hz 두드림
            }
            pos = rest;
            return;
        }
        Vector3 palm;
        if (t < 3f)
        {
            var s = 0.5f - 0.5f * MathF.Cos(MathF.PI * 2f * t / 3f);     // 좌 → 우 → 좌
            var push = MathF.Min(1f, t * 3f) * MathF.Min(1f, (3f - t) * 3f);
            palm = new Vector3(-0.12f + 0.24f * s, 1.3f, 0.28f - 0.03f + 0.055f * push);
        }
        else if (t < 5f)
        {
            var push = MathF.Min(1f, (t - 3f) * 3f) * MathF.Min(1f, (5f - t) * 3f);
            palm = new Vector3(0.3f + 0.025f, 1.1f - 0.022f, 0.3f - 0.04f - 0.03f + 0.05f * push);
        }
        else palm = new Vector3(0.05f, 1.2f, 0.12f);
        pos = palm + FingerOffsets[hip];
    }

    private void BuildWorkingScene(double ms)
    {
        if (Guard.Enabled && Guard.FirstOrderHold)
        {
            var a = (float)Math.Clamp((ms - _frameStartMs) / FrameMs, 0.0, 1.0);
            for (var s = 0; s < Spheres; s++)
            {
                _wcx[s] = _pcx[s] + (_cx[s] - _pcx[s]) * a; _wcy[s] = _pcy[s] + (_cy[s] - _pcy[s]) * a;
                _wcz[s] = _pcz[s] + (_cz[s] - _pcz[s]) * a; _wr[s] = _pr[s] + (_r[s] - _pr[s]) * a;
            }
        }
        else
        {
            Array.Copy(_cx, _wcx, Spheres); Array.Copy(_cy, _wcy, Spheres); Array.Copy(_cz, _wcz, Spheres); Array.Copy(_r, _wr, Spheres);
        }
    }

    /// <summary>장면 SDF 최솟값(구 인덱스 반환).</summary>
    public float SceneSdf(Vector3 p, out int sphere)
    {
        if (UseSimd) HapticKernels.SphereSdfSimd(_wcx, _wcy, _wcz, _wr, p, _sdf);
        else HapticKernels.SphereSdfScalar(_wcx, _wcy, _wcz, _wr, p, _sdf);
        var best = float.PositiveInfinity; sphere = -1;
        for (var s = 0; s < Spheres; s++) if (_sdf[s] < best) { best = _sdf[s]; sphere = s; }
        return best;
    }

    /// <summary>구 합집합 표면으로 반복 사영(갓-오브젝트 구속).</summary>
    public Vector3 ProjectToSurface(Vector3 p, out int sphere)
    {
        sphere = -1;
        for (var it = 0; it < ProjectIters; it++)
        {
            var d = SceneSdf(p, out var s);
            if (d >= -1e-6f) { if (sphere < 0) sphere = s; break; }
            var c = new Vector3(_wcx[s], _wcy[s], _wcz[s]);
            var dir = p - c;
            var len = dir.Length();
            dir = len > 1e-9f ? dir / len : Vector3.UnitZ * -1f;
            p = c + dir * (_wr[s] + 1e-6f);
            sphere = s;
        }
        return p;
    }

    /// <summary>1 ms 햅틱 서브사이클(무할당): 목표 궤적 → 갓-오브젝트 프록시/마찰 → 켈빈-보이트(SIMD) → 렌치 → 지연 · 가드 → 디바이스 적분.</summary>
    public void SubStep()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var ms = _hapMs;
        BuildWorkingScene(ms);
        var palm = Vector3.Zero;
        for (var i = 0; i < Hips; i++)
        {
            HipTarget(ms, i, HandMotion, out var tp, out _);
            HipTarget(ms - 1.0, i, HandMotion, out var tq, out _);
            _target[i] = tp; _targetVel[i] = (tp - tq) / Dt;
            if (i < 5) palm += _x[i];
        }
        palm /= 5f;
        PalmCenter = palm;

        // ── 갓-오브젝트 프록시 + 마찰 원뿔 ──
        for (var i = 0; i < Lanes; i++) { _k[i] = 0f; _c[i] = 0f; _pen[i] = 0f; _penRate[i] = 0f; }
        for (var i = 0; i < Hips; i++)
        {
            ref var ct = ref _contacts[i];
            var x = _x[i];
            var d0 = SceneSdf(x, out _);
            ct.Hip = x;
            if (d0 >= 0f)
            {
                ct.InContact = false; ct.Proxy = x; ct.Penetration = 0f; ct.Sticking = false; ct.SlideSpeed = 0f; ct.Sphere = -1;
                _proxyPrev[i] = x;
                continue;
            }
            var g = ProjectToSurface(x, out var sp);
            var n = Vector3.Normalize(g - new Vector3(_wcx[sp], _wcy[sp], _wcz[sp]));
            var mat = _mat[sp];
            var k = _stiff[(int)mat];
            var penG = Vector3.Dot(g - x, n);
            var proxy = g;
            var stick = false;
            if (ct.InContact)
            {
                var s = ProjectToSurface(_proxyPrev[i], out _);
                var tan = s - g;
                tan -= n * Vector3.Dot(tan, n);
                var lim = EffectiveFriction(mat) * MathF.Max(penG, 0f);
                var tl = tan.Length();
                if (tl <= lim) { proxy = g + tan; stick = true; }
                else if (tl > 1e-9f) proxy = g + tan * (lim / tl);
                proxy = ProjectToSurface(proxy, out _);
            }
            ct.SlideSpeed = ct.InContact ? Vector3.Distance(proxy, _proxyPrev[i]) / Dt : 0f;
            ct.InContact = true; ct.Sphere = sp; ct.Material = mat; ct.Normal = n; ct.Proxy = proxy; ct.Sticking = stick;
            _proxyPrev[i] = proxy;
            var delta = proxy - x;
            var dn = Vector3.Dot(delta, n);
            ct.Penetration = dn;
            _k[i] = k; _c[i] = _damp[(int)mat]; _pen[i] = dn; _penRate[i] = -Vector3.Dot(_v[i], n);
        }

        // ── 켈빈-보이트(SIMD 4 레인 × 2) ──
        if (UseSimd) HapticKernels.KelvinVoigtSimd(_k, _c, _pen, _penRate, _fn);
        else HapticKernels.KelvinVoigtScalar(_k, _c, _pen, _penRate, _fn);

        var wf = Vector3.Zero; var wt = Vector3.Zero; var active = 0; var peak = 0f;
        for (var i = 0; i < Hips; i++)
        {
            ref var ct = ref _contacts[i];
            var f = Vector3.Zero;
            if (ct.InContact)
            {
                var delta = ct.Proxy - ct.Hip;
                var ft = _k[i] * (delta - ct.Normal * ct.Penetration);
                f = ct.Normal * _fn[i] + ft;
                ct.NormalForce = _fn[i]; ct.FrictionForce = ft.Length();
                active++;
            }
            else { ct.NormalForce = 0f; ct.FrictionForce = 0f; }
            ct.Force = f;
            if (i < 5) { wf += f; wt += Vector3.Cross(ct.Hip - palm, f); }
            var fm = f.Length();
            if (fm > peak) peak = fm;
            // 1 샘플 전송 지연 → 모터 포트 가드
            var delayed = _pending[i];
            _pending[i] = f;
            _applied[i] = Guard.Process(i, delayed, _v[i], Dt);
        }
        WrenchForce = wf; WrenchTorque = wt; ActiveContacts = active; PeakForce = peak;

        // ── 디바이스 동역학(반암시적 오일러) ──
        for (var i = 0; i < Hips; i++)
        {
            var hand = HandStiffness * (_target[i] - _x[i]) + HandDamping * (_targetVel[i] - _v[i]);
            var a = (_applied[i] + hand - DeviceDamping * _v[i]) / DeviceMass;
            _v[i] += a * Dt;
            var dx = _v[i] * Dt;
            _x[i] += dx;
            Guard.Commit(i, _applied[i], dx);
        }
        _hapMs = ms + 1.0;
        SubSteps++;
        var us = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency);
        LastSubStepUs = us;
        if (us > MaxSubStepUs) MaxSubStepUs = us;
    }

    /// <summary>요골 맥동 위상 → PPG(v17.17 파형).</summary>
    public static float Pulse(double tMs, float bpm) => MicroVascularHemodynamicsFlushingSynthesizer.PpgWave((float)(tMs / 1000.0 * bpm / 60.0 % 1.0));
}
