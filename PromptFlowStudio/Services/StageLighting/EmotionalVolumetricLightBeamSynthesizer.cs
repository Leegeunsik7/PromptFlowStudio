using System.Numerics;

namespace PromptFlowStudio.Services.StageLighting;

public enum LightingMood : byte { DramaticNoir, CyberNeon, GoldenHour, ThrillerRed }

/// <summary>조명 무드 프리셋(미레드 시프트 · 젤 A/B · 기본 젤 비율 · 키/필 비 · 헤이즈 · 고보 · 절정 플리커/스트로브 · 젤 순환 Hz).</summary>
public readonly record struct MoodSpec(string Name, float MiredShift, Vector3 GelA, Vector3 GelB, float GelBase, float KeyFillLow, float KeyFillHigh, float Haze, int Gobo, float FlickerHz, float StrobeHz, float CycleHz);

/// <summary>
/// [PromptFlow Studio v17.41.0] 🌫 감정 긴장도 연동 볼류메트릭 빔 &amp; 색온도 크로스페이드 합성기(<see cref="Step"/> 무할당 · 고정 링버퍼).
///   ▸ 색온도: v17.40 긴장도 0 → 1 = 텅스텐 2700 K → 주광 6500 K(미레드 공간 보간 · 무드 시프트) → 임계 감쇠 스프링(ω 2.2) — 계단 입력에도 연속.
///   ▸ 젤: 무드 기본 비율 + 긴장 0.7↑ 사이키델릭 혼합(젤 A↔B 정현 순환 0.5+T 배속) · 무드 전환 2 s 크로스페이드(수치 선형 · 고보 중간점 교체).
///   ▸ 레벨: 키 0.55+0.4T · 필 = 키/키필비(무드 저→고) · 림 0.45+0.5T · 줌 키 20→12° · 절정(T 0.82→0.95) 플리커/스트로브 요청(광학 가드가 승인).
///   ▸ 볼류메트릭 콘: 산란 σs = 0.004 + 0.03·헤이즈 누적(τ 6 s) + v17.28 안개 밀도 · 소광 1.15σs · Henyey-Greenstein g 0.5+0.3T →
///     <see cref="ShaftStrength"/> = I·(1−e^(−σs·L))·4π·HG(cos θ_카메라)·투과율.
/// </summary>
public sealed class EmotionalVolumetricLightBeamSynthesizer
{
    public const int Series = 600, Decim = 6;
    public const float WarmK = 2700f, CoolK = 6500f, MinK = 2000f, MaxK = 10000f, Omega = 2.2f, MoodFadeS = 2f, GelTau = 0.8f, HazeTau = 6f;

    public static readonly MoodSpec[] Moods =
    {
        new("Dramatic Noir", 0f, new Vector3(0.55f, 0.68f, 1f), new Vector3(0.9f, 0.95f, 1f), 0.1f, 4f, 10f, 0.55f, 1, 0f, 0f, 0f),
        new("Cyber Neon", -25f, new Vector3(1f, 0.08f, 0.75f), new Vector3(0.05f, 0.85f, 1f), 0.55f, 2.5f, 6f, 0.8f, 3, 0f, 12f, 0.35f),
        new("Golden Hour", 45f, new Vector3(1f, 0.62f, 0.28f), new Vector3(1f, 0.45f, 0.18f), 0.25f, 1.6f, 3.2f, 0.35f, 2, 0f, 0f, 0f),
        new("Thriller Red", 30f, new Vector3(1f, 0.04f, 0.03f), new Vector3(0.65f, 0.02f, 0.05f), 0.3f, 6f, 14f, 0.6f, 4, 9f, 0f, 0f),
    };

    private readonly float[] _tS = new float[Series], _kS = new float[Series], _gS = new float[Series], _shS = new float[Series];
    private LightingMood _from, _to;
    private float _blend = 1f, _mired = 1e6f / WarmK, _miredVel, _hue, _hazeAcc;
    private int _sHead, _sCount, _frame;
    private float _shaftAcc; private int _shaftN;

    public LightingMood Mood => _to;
    public float MoodBlend => _blend;
    /// <summary>수동 색온도(0 = 긴장도 자동).</summary>
    public float CctOverrideK { get; set; }
    public float Tension { get; private set; }
    public float CctK { get; private set; } = WarmK;
    public float GoalK { get; private set; } = WarmK;
    public float GelMix { get; private set; }
    public Vector3 Gel { get; private set; } = Vector3.One;
    public Vector3 White { get; private set; } = Vector3.One;
    public Vector3 KeyColor { get; private set; } = Vector3.One;
    public Vector3 FillColor { get; private set; } = Vector3.One;
    public Vector3 RimColor { get; private set; } = Vector3.One;
    public Vector3 WashColor { get; private set; } = Vector3.One;
    public float KeyLevel { get; private set; }
    public float FillLevel { get; private set; }
    public float RimLevel { get; private set; }
    public float SpotLevel { get; private set; }
    public float WashLevel { get; private set; }
    public float StrobeLevel { get; private set; }
    public float HazerLevel { get; private set; }
    public float KeyFill { get; private set; } = 4f;
    public float KeyZoom { get; private set; } = 20f;
    public float FillZoom { get; private set; } = 34f;
    public float RimZoom { get; private set; } = 14f;
    public float SpotZoom { get; private set; } = 10f;
    public int Gobo { get; private set; }
    public float GoboRotDps { get; private set; }
    public float Cto { get; private set; }
    public float FlickerHz { get; private set; }
    public float FlickerDepth { get; private set; }
    public float StrobeRequestHz { get; private set; }
    public float Climax { get; private set; }
    public float FogDensity { get; private set; }
    public float Scatter { get; private set; }
    public float Extinction { get; private set; }
    public float Anisotropy { get; private set; } = 0.5f;
    public float HazeAccum => _hazeAcc;
    public float MeanShaft { get; private set; }
    public int MoodChanges { get; private set; }

    public int SeriesCount => _sCount;
    public float TensionSeries(int i) => _tS[Idx(i)];
    public float CctSeries(int i) => _kS[Idx(i)];
    public float GelSeries(int i) => _gS[Idx(i)];
    public float ShaftSeries(int i) => _shS[Idx(i)];
    private int Idx(int i) => (_sHead - 1 - i + 2 * Series) % Series;

    public static string MoodName(LightingMood m) => Moods[(int)m].Name;

    public void SetMood(LightingMood m)
    {
        if (m == _to) return;
        _from = _to; _to = m; _blend = 0f;
        MoodChanges++;
    }

    // ══════════════════════ 프레임(무할당) ══════════════════════

    /// <summary>긴장도(0..1) · 친밀도(−1..1) · v17.28 안개 소광 밀도(1/m) → 색/레벨/콘 매개변수.</summary>
    public void Step(float dt, float tension, float affinity, float fogDensity)
    {
        var t = Tension = float.IsFinite(tension) ? Math.Clamp(tension, 0f, 1f) : Tension;
        affinity = float.IsFinite(affinity) ? Math.Clamp(affinity, -1f, 1f) : 0f;
        _blend = MathF.Min(1f, _blend + dt / MoodFadeS);
        ref readonly var a = ref Moods[(int)_from];
        ref readonly var b = ref Moods[(int)_to];
        var w = SmoothStep(0f, 1f, _blend);
        var shift = Lerp(a.MiredShift, b.MiredShift, w);
        var gelA = Vector3.Lerp(a.GelA, b.GelA, w);
        var gelB = Vector3.Lerp(a.GelB, b.GelB, w);
        var gelBase = Lerp(a.GelBase, b.GelBase, w);
        var haze = Lerp(a.Haze, b.Haze, w);
        var cycle = Lerp(a.CycleHz, b.CycleHz, w);
        Gobo = w < 0.5f ? a.Gobo : b.Gobo;

        // 색온도 — 미레드 공간 임계 감쇠 스프링
        var goalMired = CctOverrideK > 0f ? 1e6f / Math.Clamp(CctOverrideK, MinK, MaxK) : Lerp(1e6f / WarmK, 1e6f / CoolK, SmoothStep(0f, 1f, t)) + shift;
        goalMired = Math.Clamp(goalMired, 1e6f / MaxK, 1e6f / MinK);
        GoalK = 1e6f / goalMired;
        var acc = Omega * Omega * (goalMired - _mired) - 2f * Omega * _miredVel;
        _miredVel += acc * dt;
        _mired = Math.Clamp(_mired + _miredVel * dt, 1e6f / MaxK, 1e6f / MinK);
        CctK = 1e6f / _mired;

        // 젤 — 사이키델릭 순환
        var gelGoal = gelBase + (1f - gelBase) * SmoothStep(0.7f, 1f, t);
        GelMix += (gelGoal - GelMix) * (1f - MathF.Exp(-dt / GelTau));
        _hue += dt * cycle * (0.5f + t); _hue -= MathF.Floor(_hue);
        Gel = Vector3.Lerp(gelA, gelB, cycle > 0f ? 0.5f + 0.5f * MathF.Sin(MathF.Tau * _hue) : 0f);
        White = Kelvin(CctK);
        KeyColor = Norm(Vector3.Lerp(White, Gel, GelMix * 0.45f));
        RimColor = Norm(Vector3.Lerp(Kelvin(MathF.Min(CctK * 1.35f, MaxK)), Gel, GelMix));
        FillColor = Norm(Vector3.Lerp(White, Kelvin(3000f), MathF.Max(affinity, 0f) * 0.3f));
        WashColor = Norm(Vector3.Lerp(White, Gel, MathF.Min(1f, 0.4f + GelMix)));
        Cto = Math.Clamp((CctK - CoolK) / (WarmK - CoolK), 0f, 1f);

        // 레벨 · 줌 · 고보
        KeyLevel = 0.55f + 0.4f * t;
        KeyFill = Lerp(Lerp(a.KeyFillLow, b.KeyFillLow, w), Lerp(a.KeyFillHigh, b.KeyFillHigh, w), t);
        FillLevel = KeyLevel / KeyFill;
        RimLevel = 0.45f + 0.5f * t;
        SpotLevel = 0.35f + 0.45f * t;
        WashLevel = 0.3f + 0.5f * t;
        KeyZoom = 20f - 8f * t; FillZoom = 34f; RimZoom = 14f; SpotZoom = 10f;
        GoboRotDps = Gobo > 0 ? 6f + 54f * t : 0f;
        Climax = SmoothStep(0.82f, 0.95f, t);
        FlickerHz = Lerp(a.FlickerHz, b.FlickerHz, w);
        FlickerDepth = FlickerHz > 0f ? 0.65f * Climax : 0f;
        var strobe = Lerp(a.StrobeHz, b.StrobeHz, w);
        StrobeRequestHz = strobe > 0f && Climax > 0.5f ? strobe : 0f;
        StrobeLevel = StrobeRequestHz > 0f ? Climax : 0f;
        HazerLevel = haze * (0.45f + 0.55f * t);

        // 볼류메트릭
        FogDensity = float.IsFinite(fogDensity) ? Math.Clamp(fogDensity, 0f, 0.2f) : 0f;
        _hazeAcc += (HazerLevel - _hazeAcc) * (1f - MathF.Exp(-dt / HazeTau));
        Scatter = 0.004f + 0.03f * _hazeAcc + FogDensity;
        Extinction = 1.15f * Scatter;
        Anisotropy = MathF.Min(0.5f + 0.3f * t, 0.85f);

        if (++_frame % Decim == 0)
        {
            MeanShaft = _shaftN > 0 ? _shaftAcc / _shaftN : 0f;
            _shaftAcc = 0f; _shaftN = 0;
            _tS[_sHead] = t; _kS[_sHead] = CctK; _gS[_sHead] = GelMix; _shS[_sHead] = MeanShaft;
            _sHead = (_sHead + 1) % Series;
            if (_sCount < Series) _sCount++;
        }
    }

    /// <summary>원뿔 빔 광축 샤프트 가시도(0..1): 빔 중점 → 카메라 방향 HG 위상 × 산란량 × 투과율.</summary>
    public float ShaftStrength(Vector3 beam, Vector3 origin, Vector3 camera, float intensity, float length)
    {
        var mid = origin + beam * (0.5f * length);
        var toCam = camera - mid;
        var dc = MathF.Max(toCam.Length(), 1e-3f);
        var cos = Vector3.Dot(beam, toCam / dc);
        var phase = 4f * MathF.PI * HenyeyGreenstein(Anisotropy, cos);
        var scat = 1f - MathF.Exp(-Scatter * length);
        var trans = MathF.Exp(-Extinction * (0.5f * length + dc));
        var s = Math.Clamp(intensity * scat * phase * trans * 6f, 0f, 1f);
        _shaftAcc += s; _shaftN++;
        return s;
    }

    public static float HenyeyGreenstein(float g, float cos)
    {
        var d = 1f + g * g - 2f * g * cos;
        return (1f - g * g) / (4f * MathF.PI * d * MathF.Sqrt(d));
    }

    /// <summary>흑체 색온도 → RGB(Tanner Helland 근사 · 0..1).</summary>
    public static Vector3 Kelvin(float k)
    {
        var t = Math.Clamp(k, 1000f, 40000f) / 100f;
        float r, g, b;
        if (t <= 66f)
        {
            r = 255f;
            g = 99.4708025861f * MathF.Log(t) - 161.1195681661f;
            b = t <= 19f ? 0f : 138.5177312231f * MathF.Log(t - 10f) - 305.0447927307f;
        }
        else
        {
            r = 329.698727446f * MathF.Pow(t - 60f, -0.1332047592f);
            g = 288.1221695283f * MathF.Pow(t - 60f, -0.0755148492f);
            b = 255f;
        }
        return new Vector3(Math.Clamp(r, 0f, 255f), Math.Clamp(g, 0f, 255f), Math.Clamp(b, 0f, 255f)) / 255f;
    }

    private static Vector3 Norm(Vector3 c)
    {
        var m = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        return m > 1e-4f ? c / m : Vector3.One;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float SmoothStep(float a, float b, float x)
    {
        var t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
