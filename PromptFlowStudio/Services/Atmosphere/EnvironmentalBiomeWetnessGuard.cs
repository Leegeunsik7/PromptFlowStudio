using System.Numerics;
using PromptFlowStudio.Services.Neural3d;
using PromptFlowStudio.Services.OpenUsd;
using PromptFlowStudio.Services.PathTracing;

namespace PromptFlowStudio.Services.Atmosphere;

public enum WetSurface : byte { Skin, Cloth, Hair }

/// <summary>강수 입력(무할당 값 전달): 비/눈 강도(mm/h · 눈 = 물당량) · 기온 °C · 상대습도 · 풍속 · 수평 일사(상대) · 평균 강수 종단 속도.</summary>
public readonly record struct PrecipitationInput(float RainMmH, float SnowMmH, float TemperatureC, float Humidity, float WindSpeed, float Insolation, float FallSpeed);

/// <summary>
/// [PromptFlow Studio v16.84.0] 💧 지형 생태계 &amp; 표면 습윤/적설 피드백 가드(무할당 · 물 수지 보존 검증).
///   ▸ 지형 40² 셀(2.5 m · v16.72 절차 지형 HeightAt/BiomeAt 샘플): 바이옴별 공극률 · 수막 용량 · 침투율.
///   ▸ 물 수지(가속 시간 TimeScale): 강우 × 수평 투영(n_y) → 수막(SIMD 상한/초과) → 평탄 셀 웅덩이 · 경사 셀 유출 · 침투 · 증발(Dalton: (1−RH)(a + b·풍속) · e_s(T)/e_s(20 °C) · 일사).
///   ▸ 적설: 물당량 SWE 누적(경사 &gt; 50° 탈락) · 도일 융설(4 mm/°C/일) + 강우 융설 + 일사 → 융수 = 수막 입력. 결빙 위험(수막 &gt; 0 · T &lt; 0 · 동결 토양 침투 10%) 집계.
///   ▸ 표면 응답(Lagarde 2013 습윤 모델): 공극률 × 습윤 → 확산 알베도 감쇠 · 거칠기 → 수막 0.08 · 웅덩이 = 물 층(F0 0.02 · IOR 1.33) · 눈 = 알베도 0.9 / 거칠기 0.75 / SSS.
///   ▸ 가드: NaN → 원재질 폴백 · 알베도 ≤ 0.95 · 거칠기 [0.02, 1] · PbrMaterial(v16.74) · 3DGS 스플랫(v16.58) SH DC 오버레이 · 캐릭터(피부/의상/헤어) 풍성 강우(바람/종단 속도).
/// </summary>
public sealed class EnvironmentalBiomeWetnessGuard
{
    public const int N = 40, Cells = N * N, Bins = 16;
    public const float CellSize = 2.5f, PuddleCap = 10f, DegreeDay = 4f / 86400f;
    public static readonly Vector3 SnowAlbedo = new(0.90f, 0.92f, 0.95f);

    private readonly object _gate = new();
    private readonly float[] _h = new float[Cells], _nx = new float[Cells], _ny = new float[Cells], _nz = new float[Cells];
    private readonly byte[] _biome = new byte[Cells];
    private readonly float[] _poro = new float[Cells], _cap = new float[Cells], _inf = new float[Cells], _flat = new float[Cells];
    private readonly float[] _film = new float[Cells], _puddle = new float[Cells], _swe = new float[Cells], _over = new float[Cells];
    private readonly float[] _baseR = new float[Cells], _baseG = new float[Cells], _baseB = new float[Cells], _baseRough = new float[Cells];
    private readonly int[] _wetHist = new int[Bins], _snowHist = new int[Bins];

    public readonly float[] Wetness = new float[Cells], PuddleCover = new float[Cells], SnowCover = new float[Cells];
    public readonly float[] AlbedoR = new float[Cells], AlbedoG = new float[Cells], AlbedoB = new float[Cells], Roughness = new float[Cells], F0 = new float[Cells];

    public EnvironmentalBiomeWetnessGuard() => BuildFlat();

    public float TimeScale { get; set; } = 60f;
    public double TotalIn { get; private set; }
    public double TotalEvaporated { get; private set; }
    public double TotalInfiltrated { get; private set; }
    public double TotalRunoff { get; private set; }
    public double Stored { get { lock (_gate) return SumStored(); } }
    public float MeanWetness { get; private set; }
    public float MeanPuddle { get; private set; }
    public float MeanSnowDepth { get; private set; }
    public float MaxSnowDepth { get; private set; }
    public float SnowCoverage { get; private set; }
    public float IceRisk { get; private set; }
    public float EvaporationMmH { get; private set; }
    public float SkinWetness { get; private set; }
    public float ClothWetness { get; private set; }
    public float HairWetness { get; private set; }
    public float CharacterSnow { get; private set; }
    public float WindDrivenRatio { get; private set; }
    public long NanGuards { get; private set; }
    public long Clamps { get; private set; }
    public double LastStepMs { get; private set; }
    public int LandCells { get; private set; }
    public ReadOnlySpan<int> WetHistogram => _wetHist;
    public ReadOnlySpan<int> SnowHistogram => _snowHist;
    public float Height(int c) => _h[c];
    public Vector3 Normal(int c) => new(_nx[c], _ny[c], _nz[c]);
    public Biome BiomeOf(int c) => (Biome)_biome[c];
    public float Porosity(int c) => _poro[c];
    public float Film(int c) => _film[c];
    public float Puddle(int c) => _puddle[c];
    public float SnowWaterEquivalent(int c) => _swe[c];
    public float SnowDepth(int c) => _swe[c] * 0.01f;
    public static Vector3 CellCenter(int c, float y) => new(((c % N) - (N - 1) * 0.5f) * CellSize, y, ((c / N) - (N - 1) * 0.5f) * CellSize);

    /// <summary>바이옴 → (공극률, 수막 용량 mm, 침투율 mm/h).</summary>
    public static (float Porosity, float Capacity, float Infiltration) BiomeHydrology(Biome b) => b switch
    {
        Biome.Ocean => (0f, 1f, 0f),
        Biome.Beach => (0.8f, 0.25f, 30f),
        Biome.Desert => (0.75f, 0.2f, 25f),
        Biome.Grassland => (0.6f, 1.0f, 10f),
        Biome.Forest => (0.7f, 1.5f, 15f),
        Biome.Taiga => (0.65f, 1.2f, 8f),
        Biome.Snow => (0.3f, 0.5f, 0.5f),
        Biome.Mountain => (0.15f, 0.3f, 1f),
        _ => (0.4f, 0.4f, 3f),
    };

    private void BuildFlat()
    {
        for (var c = 0; c < Cells; c++)
        {
            _h[c] = 0; _nx[c] = 0; _ny[c] = 1; _nz[c] = 0; _biome[c] = (byte)Biome.Grassland;
            var col = GenerativeWorldProceduralEngine.BiomeColor(Biome.Grassland);
            _baseR[c] = col.X; _baseG[c] = col.Y; _baseB[c] = col.Z;
        }
        Prepare();
    }

    /// <summary>절차 지형(v16.72 OpenUSD 월드) 재샘플 + 상태 초기화. 제어 평면(지형 변경 시).</summary>
    public void Rebuild(GenerativeWorldProceduralEngine world)
    {
        lock (_gate)
        {
            for (var c = 0; c < Cells; c++)
            {
                var p = CellCenter(c, 0f);
                _h[c] = world.HeightAt(p.X, p.Z);
                var n = world.NormalAt(p.X, p.Z);
                _nx[c] = n.X; _ny[c] = n.Y; _nz[c] = n.Z;
                var b = world.BiomeAt(p.X, p.Z);
                _biome[c] = (byte)b;
                var col = GenerativeWorldProceduralEngine.BiomeColor(b);
                _baseR[c] = col.X; _baseG[c] = col.Y; _baseB[c] = col.Z;
            }
            Prepare();
        }
    }

    private void Prepare()
    {
        var land = 0;
        for (var c = 0; c < Cells; c++)
        {
            var b = (Biome)_biome[c];
            var (p, cap, inf) = BiomeHydrology(b);
            _poro[c] = p; _cap[c] = cap; _inf[c] = inf;
            _baseRough[c] = b switch { Biome.Ocean => 0.05f, Biome.Mountain => 0.75f, Biome.Snow => 0.7f, Biome.Beach or Biome.Desert => 0.9f, _ => 0.85f };
            var t = Math.Clamp((_ny[c] - 0.97f) / 0.025f, 0f, 1f);
            _flat[c] = t * t * (3f - 2f * t);
            if (b != Biome.Ocean) land++;
        }
        LandCells = land;
        ResetState();
    }

    public void ResetState()
    {
        lock (_gate)
        {
            _film.AsSpan().Clear(); _puddle.AsSpan().Clear(); _swe.AsSpan().Clear();
            TotalIn = TotalEvaporated = TotalInfiltrated = TotalRunoff = 0;
            SkinWetness = ClothWetness = HairWetness = CharacterSnow = 0f;
            UpdateOutputs();
        }
    }

    /// <summary>포화 수증기압 비 e_s(T)/e_s(20 °C)(Magnus).</summary>
    public static float SaturationRatio(float tempC) => MathF.Exp(17.62f * tempC / (243.12f + tempC) - 17.62f * 20f / 263.12f);

    /// <summary>Dalton 증발(mm/h): 0.15 · (1−RH)(1 + 0.25·풍속) · e_s 비 · (1 + 2·일사) · T &gt; −5 °C.</summary>
    public static float EvaporationRate(float tempC, float rh, float wind, float insolation) =>
        tempC < -5f ? 0f : 0.15f * (1f - Math.Clamp(rh, 0f, 1f)) * (1f + 0.25f * wind) * SaturationRatio(tempC) * (1f + 2f * Math.Clamp(insolation, 0f, 1f));

    /// <summary>1틱(무할당 · 물 수지 보존): 강수 → 수막/웅덩이/적설 → 증발/침투/유출 → 표면 재질 오버레이 → 캐릭터.</summary>
    public void Step(float dt, in PrecipitationInput p)
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var hrs = dt * TimeScale / 3600f;
            var rain = MathF.Max(0f, p.RainMmH) * hrs; var snow = MathF.Max(0f, p.SnowMmH) * hrs;
            var evap = EvaporationRate(p.TemperatureC, p.Humidity, p.WindSpeed, p.Insolation);
            EvaporationMmH = evap;
            var evapStep = evap * hrs;
            var melt = (DegreeDay * 3600f * MathF.Max(p.TemperatureC, 0f) + 0.0125f * MathF.Max(p.TemperatureC, 0f) * MathF.Max(0f, p.RainMmH) + 0.6f * Math.Clamp(p.Insolation, 0f, 1f) * (p.TemperatureC > -2f ? 1f : 0f)) * hrs;
            // 동결 토양: 침투 10%
            var frozen = p.TemperatureC < 0f ? 0.1f : 1f;
            double din = 0, dev = 0, dinf = 0, drun = 0;
            float ice = 0f;
            for (var c = 0; c < Cells; c++)
            {
                if (_biome[c] == (byte)Biome.Ocean) continue;
                var ny = MathF.Max(_ny[c], 0f);
                var r = rain * ny; var s = snow * ny;
                din += r + s;
                // 경사 > 50°(n_y < 0.64) 눈 탈락 → 유출
                if (ny < 0.64f) { r += s; s = 0f; }
                _swe[c] += s;
                var m = MathF.Min(_swe[c], melt);
                _swe[c] -= m;
                _film[c] += r + m;
                var inf = MathF.Min(_film[c], _inf[c] * frozen * hrs);
                _film[c] -= inf; dinf += inf;
                var ev = MathF.Min(_film[c], evapStep);
                _film[c] -= ev; dev += ev;
                if (_swe[c] > 0f && p.TemperatureC < 0f)
                {
                    var sub = MathF.Min(_swe[c], evapStep * 0.3f);
                    _swe[c] -= sub; dev += sub;
                }
            }
            // 수막 상한(SIMD) → 초과 = 평탄 셀 웅덩이 / 경사 셀 유출
            EnvSimd.ClampOverflow(_film, _cap, _over);
            for (var c = 0; c < Cells; c++)
            {
                if (_biome[c] == (byte)Biome.Ocean) continue;
                var o = _over[c];
                var toP = o * _flat[c];
                _puddle[c] += toP; drun += o - toP;
                if (_puddle[c] > PuddleCap) { drun += _puddle[c] - PuddleCap; _puddle[c] = PuddleCap; }
                var pi = MathF.Min(_puddle[c], _inf[c] * 0.3f * frozen * hrs); _puddle[c] -= pi; dinf += pi;
                var pe = MathF.Min(_puddle[c], evapStep); _puddle[c] -= pe; dev += pe;
                if (p.TemperatureC < 0f && _film[c] + _puddle[c] > 0.05f) ice++;
            }
            TotalIn += din; TotalEvaporated += dev; TotalInfiltrated += dinf; TotalRunoff += drun;
            IceRisk = LandCells > 0 ? ice / LandCells : 0f;
            Character(p, rain, snow, evapStep, melt);
            UpdateOutputs();
            LastStepMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
    }

    private double SumStored()
    {
        double s = 0;
        for (var c = 0; c < Cells; c++) s += _film[c] + _puddle[c] + _swe[c];
        return s;
    }

    /// <summary>캐릭터 표면: 수직면 풍성 강우 비 = 풍속/종단 속도(빗방울 궤적 경사) · 피부 발수 · 의상 흡수(느린 건조) · 헤어 중간.</summary>
    private void Character(in PrecipitationInput p, float rain, float snow, float evapStep, float melt)
    {
        WindDrivenRatio = p.FallSpeed > 0.1f ? MathF.Min(2f, p.WindSpeed / p.FallSpeed) : 0f;
        var catchR = rain * (0.35f + 0.65f * MathF.Min(1f, WindDrivenRatio));
        SkinWetness = Approach(SkinWetness, catchR / 0.05f, evapStep / 0.05f + 0.02f);
        ClothWetness = Approach(ClothWetness, catchR / 2.0f, evapStep / 2.0f * 0.5f);
        HairWetness = Approach(HairWetness, catchR / 0.6f, evapStep / 0.6f * 0.7f);
        CharacterSnow = Math.Clamp(CharacterSnow + snow * 0.02f - melt * 0.05f, 0f, 1f);
    }

    private static float Approach(float w, float gain, float loss) => Math.Clamp(w + gain * (1f - w) - loss, 0f, 1f);

    private void UpdateOutputs()
    {
        float sw = 0f, sp = 0f, sd = 0f, mx = 0f, sc = 0f;
        _wetHist.AsSpan().Clear(); _snowHist.AsSpan().Clear();
        for (var c = 0; c < Cells; c++)
        {
            if (_biome[c] == (byte)Biome.Ocean) { Wetness[c] = 1f; PuddleCover[c] = 1f; SnowCover[c] = 0f; continue; }
            var w = Math.Clamp(_film[c] / _cap[c], 0f, 1f);
            var pu = Math.Clamp((_puddle[c] - 0.5f) / 2.5f, 0f, 1f); pu = pu * pu * (3f - 2f * pu);
            w = MathF.Max(w, pu);
            var sn = 1f - MathF.Exp(-_swe[c] / 2f);
            Wetness[c] = w; PuddleCover[c] = pu; SnowCover[c] = sn;
            sw += w; sp += _puddle[c]; var d = _swe[c] * 0.01f; sd += d; mx = MathF.Max(mx, d); sc += sn;
            _wetHist[Math.Min(Bins - 1, (int)(w * Bins))]++;
            _snowHist[Math.Min(Bins - 1, (int)(sn * Bins))]++;
        }
        var nl = Math.Max(1, LandCells);
        MeanWetness = sw / nl; MeanPuddle = sp / nl; MeanSnowDepth = sd / nl; MaxSnowDepth = mx; SnowCoverage = sc / nl;

        // 재질 오버레이: 셀별 공극률/웅덩이/눈 · 가드(지형 바이옴 기저색 클램프는 집계 제외)
        for (var c = 0; c < Cells; c++)
        {
            var dry = new PbrMaterial
            {
                BaseColor = new Vector3(_baseR[c], _baseG[c], _baseB[c]), Roughness = _baseRough[c], Specular = 0.5f, Ior = 1.5f, Opacity = 1f,
                SubsurfaceColor = Vector3.One, SubsurfaceRadius = 0.1f,
            };
            var m = ApplyCore(dry, Wetness[c], _poro[c], PuddleCover[c], SnowCover[c], out _);
            AlbedoR[c] = m.BaseColor.X; AlbedoG[c] = m.BaseColor.Y; AlbedoB[c] = m.BaseColor.Z;
            Roughness[c] = m.Roughness;
            F0[c] = m.DielectricF0;
        }
    }

    /// <summary>표면 재질 습윤/적설 오버레이(외부 재질용 · 가드 포함).</summary>
    public PbrMaterial Apply(in PbrMaterial dry, float wetness, float porosity, float puddle, float snow)
    {
        var m = ApplyCore(dry, wetness, porosity, puddle, snow, out var guarded);
        if (guarded) Clamps++;
        if (!IsFinite(dry)) NanGuards++;
        return m;
    }

    /// <summary>캐릭터 표면 재질(피부 공극 0.1 · 의상 0.9 · 헤어 0.6).</summary>
    public PbrMaterial ApplyCharacter(in PbrMaterial dry, WetSurface s) => s switch
    {
        WetSurface.Skin => Apply(dry, SkinWetness, 0.1f, 0f, 0f),
        WetSurface.Cloth => Apply(dry, ClothWetness, 0.9f, 0f, CharacterSnow),
        _ => Apply(dry, HairWetness, 0.6f, 0f, CharacterSnow * 0.7f),
    };

    private static bool IsFinite(in PbrMaterial m) =>
        float.IsFinite(m.BaseColor.X) && float.IsFinite(m.BaseColor.Y) && float.IsFinite(m.BaseColor.Z) && float.IsFinite(m.Roughness) && float.IsFinite(m.Ior) && float.IsFinite(m.Specular);

    private static PbrMaterial ApplyCore(in PbrMaterial dryIn, float wet, float porosity, float puddle, float snow, out bool guarded)
    {
        guarded = false;
        var dry = dryIn;
        if (!IsFinite(dry)) { dry = PbrMaterial.Default; guarded = true; }
        wet = float.IsFinite(wet) ? Math.Clamp(wet, 0f, 1f) : 0f; porosity = float.IsFinite(porosity) ? Math.Clamp(porosity, 0f, 1f) : 0f;
        puddle = float.IsFinite(puddle) ? Math.Clamp(puddle, 0f, 1f) : 0f; snow = float.IsFinite(snow) ? Math.Clamp(snow, 0f, 1f) : 0f;
        var m = dry;
        // Lagarde: 확산 감쇠 = lerp(1, 0.2, 공극률·습윤) · 거칠기 → lerp(0.08, r₀·0.6, 공극률)(흡수 표면은 덜 매끈)
        var dark = 1f - 0.8f * porosity * wet;
        m.BaseColor = dry.BaseColor * dark * (1f - 0.6f * puddle * porosity);
        var wetRough = 0.08f + (dry.Roughness * 0.6f - 0.08f) * porosity;
        m.Roughness = dry.Roughness + (MathF.Min(dry.Roughness, wetRough) - dry.Roughness) * wet;
        m.Roughness += (0.02f - m.Roughness) * puddle;
        m.Ior += (1.33f - m.Ior) * puddle; m.Specular += (1f - m.Specular) * MathF.Max(puddle, wet * 0.5f);
        m.Metallic *= 1f - puddle;
        if (snow > 0f)
        {
            m.BaseColor += (SnowAlbedo - m.BaseColor) * snow;
            m.Roughness += (0.75f - m.Roughness) * snow;
            m.Subsurface += (0.4f - m.Subsurface) * snow;
            m.SubsurfaceColor += (new Vector3(0.85f, 0.9f, 1f) - m.SubsurfaceColor) * snow;
            m.SubsurfaceRadius += (0.01f - m.SubsurfaceRadius) * snow;
            m.Metallic *= 1f - snow;
        }
        var c = Vector3.Clamp(m.BaseColor, Vector3.Zero, new Vector3(0.95f));
        if (c != m.BaseColor) guarded = true;
        m.BaseColor = c;
        var r = Math.Clamp(m.Roughness, 0.02f, 1f);
        if (r != m.Roughness) guarded = true;
        m.Roughness = r;
        return m;
    }

    /// <summary>
    /// 3DGS 스플랫 SH DC 오버레이(제자리 · 무할당): 상향 법선(회전 Z축) × 적설 → 눈 알베도 · 습윤 → 공극률 감쇠.
    /// 반환 = 갱신 스플랫 수.
    /// </summary>
    public int ApplyToSplats(GaussianScene scene, float wetness, float porosity, float snow)
    {
        wetness = Math.Clamp(wetness, 0f, 1f); snow = Math.Clamp(snow, 0f, 1f); porosity = Math.Clamp(porosity, 0f, 1f);
        if (wetness <= 0f && snow <= 0f) return 0;
        var dark = 1f - 0.8f * porosity * wetness;
        var n = 0;
        for (var i = 0; i < scene.Count; i++)
        {
            var nrm = Vector3.Transform(Vector3.UnitZ, scene.Rotation(i));
            var up = MathF.Abs(nrm.Y);
            var s = snow * up * up;
            var o = i * GaussianScene.ShStride;
            for (var ch = 0; ch < 3; ch++)
            {
                var c = 0.5f + GaussianScene.ShC0 * scene.Sh[o + ch];
                c *= dark;
                c += ((ch == 0 ? SnowAlbedo.X : ch == 1 ? SnowAlbedo.Y : SnowAlbedo.Z) - c) * s;
                scene.Sh[o + ch] = GaussianScene.ToDc(Math.Clamp(c, 0f, 0.95f));
            }
            n++;
        }
        scene.Version++;
        return n;
    }
}
