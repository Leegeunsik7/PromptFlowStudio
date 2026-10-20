using System.Numerics;
using PromptFlowStudio.Services.Atmosphere;
using PromptFlowStudio.Services.CinematicMastering;
using PromptFlowStudio.Services.Neural3d;
using PromptFlowStudio.Services.OpenUsd;

namespace PromptFlowStudio.Services.Continuity;

[Flags]
public enum CorrectionChannel : byte { None = 0, Props = 1, Light = 2, Wardrobe = 4, Marks = 8, Pose = 16, Vfx = 32, All = 63 }

/// <summary>
/// [PromptFlow Studio v16.88.0] 🛠 자동 연속성 보정 &amp; 렌더 파라미터 가드(무할당 · 프레임 완화 τ · 크기 상한 → 수동 검토 이관).
///   ▸ 소품: 기준 위치/요로 지수 완화 → OpenUSD 트랜스폼(v16.72 UsdLayer.SetTranslate · 소품↔프림 매핑) + 3DGS 가우시안 그룹(v16.58 · 동일 Δ 평행 이동) · 소실/출현 복원.
///   ▸ 조명/룩: 키 라이트 slerp · 색온도/강도 완화 → 보정 컬러 룩(v16.79 GradeLook: 노출 = log₂(I_ref/I) · 슬로프 = RGB(K_ref)/RGB(K) 화이트 밸런스 · 휘도 정규화).
///   ▸ 의상/메이크업/상처: 젖음 · 주름 · 손상(감소 금지) · 의상 id 복원 · 메이크업 · 마크(소실 → 복원 · 반전/이동 → 기준 관절/오프셋).
///   ▸ 포즈(연속 컷): 관절 SIMD 완화(매치 온 액션) · VFX: 강수 밀도 → v16.84 기상 시나리오 강수 정합.
///   ▸ 가드: 소품 Δ &gt; MaxPropShift · 조명 Δ &gt; MaxLightDeg → 의도적 변경 추정(자동 보정 보류 · 수동 검토) · 잔차 재검증 피드백.
/// </summary>
public sealed class AutomaticContinuityCorrectionGuard
{
    public const int MaxGroups = ContinuitySnapshot.MaxProps;
    private readonly object _gate = new();
    private readonly int[] _usdPrim = new int[MaxGroups], _gsStart = new int[MaxGroups], _gsCount = new int[MaxGroups];
    private readonly bool[] _review = new bool[MaxGroups];
    private UsdLayer? _usd;
    private GaussianScene? _gs;
    private DynamicMicroWeatherSimulatorEngine? _weather;

    public AutomaticContinuityCorrectionGuard() { Array.Fill(_usdPrim, -1); }

    public CorrectionChannel Enabled { get; set; } = CorrectionChannel.All;
    public float Tau { get; set; } = 0.2f;
    public float MaxPropShift { get; set; } = 1.5f;
    public float MaxLightDeg { get; set; } = 60f;

    // ── 계측 ──
    public long Applied { get; private set; }
    public long ManualReviews { get; private set; }
    public float LastResidual { get; private set; }
    public float PropCorrectionM { get; private set; }
    public float LightCorrectionDeg { get; private set; }
    public float PoseCorrectionM { get; private set; }
    public int MarksRestored { get; private set; }
    public int UsdWrites { get; private set; }
    public int SplatMoves { get; private set; }
    public GradeLook CorrectionLook { get; private set; } = GradeLook.Neutral;
    public float VfxDensityTarget { get; private set; } = -1f;
    public double LastStepMs { get; private set; }
    public bool NeedsReview(int prop) => _review[prop];
    public int PrimOf(int prop) => _usdPrim[prop];

    public bool On(CorrectionChannel c) => (Enabled & c) != 0;

    public void BindUsd(UsdLayer? layer) { lock (_gate) _usd = layer; }
    public void MapPropToPrim(int prop, int prim) { lock (_gate) _usdPrim[prop] = prim; }
    public void BindSplats(GaussianScene? scene) { lock (_gate) _gs = scene; }
    public void MapPropToSplats(int prop, int start, int count) { lock (_gate) { _gsStart[prop] = start; _gsCount[prop] = count; } }
    public void BindWeather(DynamicMicroWeatherSimulatorEngine? w) { lock (_gate) _weather = w; }

    /// <summary>색온도(K) → 정규화 선형 RGB(Tanner Helland 근사 · 6600 K 기준 백색).</summary>
    public static Vector3 KelvinToRgb(float k)
    {
        var t = Math.Clamp(k, 1000f, 40000f) / 100f;
        float r, g, b;
        if (t <= 66f) { r = 1f; g = Math.Clamp(0.39008158f * MathF.Log(t) - 0.63184144f, 0f, 1f); }
        else { r = Math.Clamp(1.29293619f * MathF.Pow(t - 60f, -0.1332047592f), 0f, 1f); g = Math.Clamp(1.12989086f * MathF.Pow(t - 60f, -0.0755148492f), 0f, 1f); }
        b = t >= 66f ? 1f : t <= 19f ? 0f : Math.Clamp(0.54320678f * MathF.Log(t - 10f) - 1.19625408f, 0f, 1f);
        return new Vector3(r, g, b);
    }

    /// <summary>현재 조명 → 기준 조명으로 보이게 하는 보정 룩(노출 · 화이트 밸런스 슬로프 · 휘도 보존).</summary>
    public static GradeLook MatchLook(float kRef, float iRef, float kCur, float iCur)
    {
        var a = KelvinToRgb(kRef); var b = KelvinToRgb(kCur);
        var s = new Vector3(a.X / MathF.Max(b.X, 1e-3f), a.Y / MathF.Max(b.Y, 1e-3f), a.Z / MathF.Max(b.Z, 1e-3f));
        var l = 0.2126f * s.X + 0.7152f * s.Y + 0.0722f * s.Z;
        s /= MathF.Max(l, 1e-3f);
        var ev = MathF.Log2(MathF.Max(iRef, 1e-4f) / MathF.Max(iCur, 1e-4f));
        return new GradeLook(ev, s, Vector3.Zero, Vector3.One, 1f);
    }

    private static float Ap(float cur, float target, float k) => cur + (target - cur) * k;

    /// <summary>1프레임 보정(무할당): cur(현재 샷 상태)를 reference 로 완화 · 바인딩 대상 갱신. 반환 = 잔여 Δ 합.</summary>
    public float Step(ContinuitySnapshot reference, ContinuitySnapshot cur, CutKind cut, float dt)
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var k = 1f - MathF.Exp(-dt / MathF.Max(Tau, 1e-3f));
            float residual = 0f, propMax = 0f;
            // ── 소품 + USD + 3DGS ──
            if (On(CorrectionChannel.Props))
            {
                var n = Math.Max(reference.PropCount, cur.PropCount);
                for (var i = 0; i < n; i++)
                {
                    _review[i] = false;
                    if (reference.PropHandled[i] || cur.PropHandled[i]) continue;
                    if (reference.PropPresent[i] != cur.PropPresent[i]) { cur.PropPresent[i] = reference.PropPresent[i]; if (cur.PropPresent[i]) cur.SetProp(i, reference.Prop(i)); continue; }
                    if (!reference.PropPresent[i]) continue;
                    var target = reference.Prop(i); var p = cur.Prop(i);
                    var d = Vector3.Distance(p, target);
                    if (d > MaxPropShift) { _review[i] = true; ManualReviews++; continue; }
                    var np = Vector3.Lerp(p, target, k);
                    if (d < 1e-4f) np = target;
                    var delta = np - p;
                    cur.SetProp(i, np);
                    cur.PropYaw[i] = cur.PropYaw[i] + MathF.IEEERemainder(reference.PropYaw[i] - cur.PropYaw[i], 360f) * k;
                    residual += Vector3.Distance(np, target); propMax = MathF.Max(propMax, d);
                    if (_usd is not null && _usdPrim[i] >= 0 && _usdPrim[i] < _usd.Count)
                    {
                        var (x, y, z) = _usd.Translate(_usdPrim[i]);
                        _usd.SetTranslate(_usdPrim[i], x + delta.X, y + delta.Y, z + delta.Z); UsdWrites++;
                    }
                    if (_gs is not null && _gsCount[i] > 0)
                    {
                        var end = Math.Min(_gsStart[i] + _gsCount[i], _gs.Count);
                        for (var g = _gsStart[i]; g < end; g++) { _gs.Pos[g * 3] += delta.X; _gs.Pos[g * 3 + 1] += delta.Y; _gs.Pos[g * 3 + 2] += delta.Z; }
                        if (end > _gsStart[i]) { _gs.Version++; SplatMoves += end - _gsStart[i]; }
                    }
                }
            }
            PropCorrectionM = propMax;
            // ── 조명 + 룩 ──
            if (On(CorrectionChannel.Light))
            {
                var a = Vector3.Normalize(cur.KeyLight); var b = Vector3.Normalize(reference.KeyLight);
                var ang = MathF.Acos(Math.Clamp(Vector3.Dot(a, b), -1f, 1f)) * 180f / MathF.PI;
                LightCorrectionDeg = ang;
                // 시간 경과 컷의 태양 이동은 정상 → 방향 보정 제외(룩만 정합)
                if (cut == CutKind.TimeJump) { CorrectionLook = MatchLook(reference.ColorTempK, reference.Intensity, cur.ColorTempK, cur.Intensity); }
                else if (ang <= MaxLightDeg || cut == CutKind.Continuous)
                {
                    CorrectionLook = MatchLook(reference.ColorTempK, reference.Intensity, cur.ColorTempK, cur.Intensity);
                    cur.KeyLight = Vector3.Normalize(Vector3.Lerp(a, b, k) + b * 1e-6f);
                    var mk = RealtimeScriptSupervisorContinuityEngine.Mired(cur.ColorTempK); var mr = RealtimeScriptSupervisorContinuityEngine.Mired(reference.ColorTempK);
                    cur.ColorTempK = 1e6f / Ap(mk, mr, k);
                    cur.Intensity = MathF.Exp(Ap(MathF.Log(MathF.Max(cur.Intensity, 1e-4f)), MathF.Log(MathF.Max(reference.Intensity, 1e-4f)), k));
                    residual += ang / 90f + MathF.Abs(mk - mr) / 100f;
                }
                else ManualReviews++;
            }
            // ── 의상 · 메이크업 · 마크 ──
            var restored = 0;
            for (var c = 0; c < ContinuitySnapshot.MaxCharacters; c++)
            {
                if (!reference.CharPresent[c] || !cur.CharPresent[c]) continue;
                if (On(CorrectionChannel.Wardrobe))
                {
                    if (cut == CutKind.Continuous || cur.StoryTimeHours - reference.StoryTimeHours < 0.5f) cur.Outfit[c] = reference.Outfit[c];
                    if (cur.Outfit[c] == reference.Outfit[c])
                    {
                        var gap = MathF.Max(0f, cur.StoryTimeHours - reference.StoryTimeHours);
                        var raining = cur.Precipitation > 0.1f || reference.Precipitation > 0.1f;
                        var wTarget = raining ? MathF.Max(cur.Wetness[c], reference.Wetness[c]) : MathF.Max(0f, reference.Wetness[c] - 0.35f * gap);
                        if (!raining && cur.Wetness[c] > reference.Wetness[c]) wTarget = reference.Wetness[c];
                        cur.Wetness[c] = Ap(cur.Wetness[c], wTarget, k);
                        if (cut == CutKind.Continuous) cur.Wrinkle[c] = Ap(cur.Wrinkle[c], reference.Wrinkle[c], k);
                        if (cur.Damage[c] < reference.Damage[c]) cur.Damage[c] = Ap(cur.Damage[c], reference.Damage[c], k);
                        residual += MathF.Abs(cur.Wetness[c] - wTarget);
                    }
                    if (cur.StoryTimeHours - reference.StoryTimeHours < 2f) cur.Makeup[c] = Ap(cur.Makeup[c], reference.Makeup[c], k);
                }
                if (On(CorrectionChannel.Marks))
                    for (var m = 0; m < ContinuitySnapshot.MaxMarks; m++)
                    {
                        ref var a = ref reference.Mark(c, m); ref var b = ref cur.Mark(c, m);
                        if (a.Present && (!b.Present || b.Joint != a.Joint || Vector3.Distance(a.Offset, b.Offset) > 1e-4f)) { b = a; restored++; }
                        else if (!a.Present && b.Present) { b.Present = false; restored++; }
                    }
            }
            MarksRestored += restored;
            // ── 포즈(매치 온 액션) ──
            if (On(CorrectionChannel.Pose) && cut == CutKind.Continuous)
            {
                ContinuitySimd.Approach(cur.JointX, reference.JointX, k);
                ContinuitySimd.Approach(cur.JointY, reference.JointY, k);
                ContinuitySimd.Approach(cur.JointZ, reference.JointZ, k);
                float worst = 0f;
                for (var c = 0; c < ContinuitySnapshot.MaxCharacters; c++)
                {
                    if (!cur.CharPresent[c]) continue;
                    for (var j = 0; j < ContinuitySnapshot.Joints; j++) worst = MathF.Max(worst, Vector3.Distance(cur.Joint(c, j), reference.Joint(c, j)));
                }
                PoseCorrectionM = worst; residual += worst;
            }
            // ── VFX 밀도(강수) ──
            if (On(CorrectionChannel.Vfx))
            {
                VfxDensityTarget = reference.Precipitation;
                cur.Precipitation = Ap(cur.Precipitation, reference.Precipitation, k);
                if (_weather is not null && MathF.Abs(_weather.Target.Precipitation - reference.Precipitation) > 0.05f)
                {
                    var s = _weather.Target; s.Precipitation = reference.Precipitation; _weather.SetScenario(s);
                }
                residual += MathF.Abs(cur.Precipitation - reference.Precipitation) * 0.1f;
            }
            LastResidual = residual;
            Applied++;
            LastStepMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            return residual;
        }
    }
}
