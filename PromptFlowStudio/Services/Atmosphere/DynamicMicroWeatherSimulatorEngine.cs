using System.Numerics;
using PromptFlowStudio.Services.HairCloth;

namespace PromptFlowStudio.Services.Atmosphere;

public enum PrecipKind : byte { None, Rain, Sleet, Snow }

/// <summary>기상 시나리오(목표 상태): 강수 mm/h · 운량 0..1 · 기온 °C · 상대습도 0..1 · 풍속 m/s(10 m) · 풍향 °(불어가는 방향) · 뇌우 0..1 · 안개 0..1.</summary>
public struct WeatherScenario
{
    public float Precipitation, CloudCoverage, Temperature, Humidity, WindSpeed, WindYawDeg, Storm, Fog;

    public static WeatherScenario Clear => new() { Precipitation = 0f, CloudCoverage = 0.1f, Temperature = 18f, Humidity = 0.45f, WindSpeed = 2f, WindYawDeg = 60f, Storm = 0f, Fog = 0f };
}

/// <summary>
/// [PromptFlow Studio v16.84.0] 🌦 동적 미세 기상 &amp; 미세 기후 시뮬레이션 합성기(무할당 · 결정적 RNG).
///   ▸ 미세 기후: 목표 시나리오로 시정수 완화 · Magnus 이슬점 · 방사 안개(이슬점 편차 &lt; 2 °C · 약풍) · 강수 → 습도 상승 · 기온 → 강수 상(비/진눈깨비/눈).
///   ▸ 강수 입자 6000(SoA): Marshall-Palmer 입경 분포 · Gunn-Kinzer 종단 속도 · 눈 결정 흔들림 · 이완 시간 τ = v_t/g 로 바람 추종(SIMD 이완/적분 = StrandSimd).
///   ▸ 바람장 16²: 로그 프로파일(z₀ = 0.03 m) + 주기 값 노이즈 유선함수 ψ 의 회전(curl) 돌풍 → 이산 발산 0 · 동결 난류(풍하 이류).
///   ▸ 운량장 32²: 주기 fBm 임계값(상층풍 ×3 이류) · 번개: 비균질 포아송(율 ∝ 뇌우²) · 귀환 뇌격 2~4 · 랜덤 워크 볼트 · 천둥 지연 = 거리/343 m/s.
/// </summary>
public sealed class DynamicMicroWeatherSimulatorEngine
{
    public const int Capacity = 6000, GridN = 16, CloudN = 32, BoltPoints = 48, MaxStrokes = 4;
    public const float BoxHalf = 20f, BoxHeight = 24f, Roughness0 = 0.03f;
    private const int NoisePeriod = 4;

    private readonly object _gate = new();
    private readonly float[] _px = new float[Capacity], _py = new float[Capacity], _pz = new float[Capacity];
    private readonly float[] _vx = new float[Capacity], _vy = new float[Capacity], _vz = new float[Capacity];
    private readonly float[] _tx = new float[Capacity], _ty = new float[Capacity], _tz = new float[Capacity], _k = new float[Capacity];
    private readonly float[] _vt = new float[Capacity], _size = new float[Capacity], _phase = new float[Capacity], _kindHash = new float[Capacity];
    private readonly byte[] _kind = new byte[Capacity];
    private readonly float[] _psi = new float[GridN * GridN], _gu = new float[GridN * GridN], _gw = new float[GridN * GridN];
    private readonly float[] _cloud = new float[CloudN * CloudN];
    private readonly float[] _boltX = new float[BoltPoints], _boltY = new float[BoltPoints];
    private readonly float[] _strokeT = new float[MaxStrokes];
    private ulong _rng;
    private float _t, _flashT = -10f;
    private int _strokes, _impactsWindow;
    private float _impactClock;
    private WeatherScenario _target, _state;

    public DynamicMicroWeatherSimulatorEngine(ulong seed = 0x1684_5EED)
    {
        _rng = seed | 1;
        for (var i = 0; i < Capacity; i++)
        {
            _px[i] = (Next01() * 2f - 1f) * BoxHalf; _py[i] = Next01() * BoxHeight; _pz[i] = (Next01() * 2f - 1f) * BoxHalf;
            _phase[i] = Next01() * MathF.Tau; _kindHash[i] = Next01();
        }
        _target = _state = WeatherScenario.Clear;
        ResampleAll();
        Step(0f);
    }

    public WeatherScenario Target { get { lock (_gate) return _target; } }
    public WeatherScenario State { get { lock (_gate) return _state; } }
    /// <summary>지표 습윤 피드백(0..1 · 습윤 가드 평균) → 지표 상대습도 상승.</summary>
    public float SurfaceMoisture { get; set; }
    public int ActiveParticles { get; private set; }
    public PrecipKind Kind { get; private set; }
    public float SnowFraction { get; private set; }
    public float RelativeHumidity { get; private set; }
    public float DewPoint { get; private set; }
    public float FogAmount { get; private set; }
    public float FogExtinction { get; private set; }
    public float Haze { get; private set; } = 1f;
    public float CloudCover { get; private set; }
    public float CloudOpticalDepth { get; private set; }
    public float Gust { get; private set; }
    public float MeanDropDiameter { get; private set; }
    public float ImpactsPerSecond { get; private set; }
    public int LightningStrikes { get; private set; }
    public float FlashIntensity { get; private set; }
    public float LastStrikeDistance { get; private set; }
    public float LastStrikeAzimuth { get; private set; }
    public float ThunderDelay { get; private set; }
    public float SinceStrike => _t - _flashT;
    public double LastStepMs { get; private set; }
    public string? LastAction { get; private set; }
    public float Time => _t;
    public ReadOnlySpan<float> CloudField => _cloud;
    public ReadOnlySpan<float> BoltX => _boltX;
    public ReadOnlySpan<float> BoltY => _boltY;
    public Vector3 Position(int i) => new(_px[i], _py[i], _pz[i]);
    public Vector3 Velocity(int i) => new(_vx[i], _vy[i], _vz[i]);
    public PrecipKind ParticleKind(int i) => (PrecipKind)_kind[i];
    public float ParticleSize(int i) => _size[i];
    public Vector3 GridWind(int i, int j) => new(_gu[j * GridN + i], 0f, _gw[j * GridN + i]);

    /// <summary>풍하(불어가는) 방향 기본 바람(10 m).</summary>
    public Vector3 MeanWind
    {
        get { var a = _state.WindYawDeg * MathF.PI / 180f; return new Vector3(MathF.Sin(a), 0f, MathF.Cos(a)) * _state.WindSpeed; }
    }

    public static string KindName(PrecipKind k) => k switch { PrecipKind.Rain => "비", PrecipKind.Sleet => "진눈깨비", PrecipKind.Snow => "눈", _ => "없음" };

    /// <summary>Gunn-Kinzer 종단 속도 근사(D mm → m/s).</summary>
    public static float RainTerminalVelocity(float diameterMm) => MathF.Max(0.5f, 9.65f - 10.3f * MathF.Exp(-0.6f * diameterMm));

    /// <summary>Marshall-Palmer λ = 4.1·R^−0.21 (mm⁻¹).</summary>
    public static float MarshallPalmerLambda(float rateMmH) => 4.1f * MathF.Pow(MathF.Max(rateMmH, 0.1f), -0.21f);

    /// <summary>Magnus 이슬점(°C).</summary>
    public static float DewPointOf(float tempC, float rh)
    {
        var g = MathF.Log(Math.Clamp(rh, 0.01f, 1f)) + 17.62f * tempC / (243.12f + tempC);
        return 243.12f * g / (17.62f - g);
    }

    /// <summary>강설 비율(습구 근사 · 0 °C 중심 ±1.5 °C 선형 전이).</summary>
    public static float SnowFractionOf(float tempC) => Math.Clamp((1.5f - tempC) / 3f, 0f, 1f);

    /// <summary>로그 풍속 프로파일 배율(10 m = 1).</summary>
    public static float ProfileFactor(float heightM) => MathF.Log(MathF.Max(heightM, 0.2f) / Roughness0) / MathF.Log(10f / Roughness0);

    public void SetScenario(in WeatherScenario s)
    {
        lock (_gate)
        {
            _target = s;
            _target.Precipitation = Math.Clamp(s.Precipitation, 0f, 80f); _target.CloudCoverage = Math.Clamp(s.CloudCoverage, 0f, 1f);
            _target.Temperature = Math.Clamp(s.Temperature, -30f, 45f); _target.Humidity = Math.Clamp(s.Humidity, 0.05f, 1f);
            _target.WindSpeed = Math.Clamp(s.WindSpeed, 0f, 30f); _target.Storm = Math.Clamp(s.Storm, 0f, 1f); _target.Fog = Math.Clamp(s.Fog, 0f, 1f);
        }
    }

    /// <summary>즉시 수렴(완화 생략 · 테스트/샷 전환).</summary>
    public void SnapToTarget() { lock (_gate) { _state = _target; ResampleAll(); } }

    /// <summary>프롬프트(한/영) → 시나리오 갱신(반영 키워드 수).</summary>
    public static int ParsePrompt(string? prompt, ref WeatherScenario s)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return 0;
        static bool Has(string p, string a, string b) => p.Contains(a, StringComparison.OrdinalIgnoreCase) || p.Contains(b, StringComparison.OrdinalIgnoreCase);
        var n = 0;
        if (Has(prompt, "폭설", "blizzard")) { s.Precipitation = 12f; s.Temperature = -8f; s.WindSpeed = 14f; s.CloudCoverage = 1f; s.Humidity = 0.9f; n++; }
        else if (Has(prompt, "진눈깨비", "sleet")) { s.Precipitation = 4f; s.Temperature = 0.5f; s.CloudCoverage = 0.95f; s.Humidity = 0.9f; n++; }
        else if (Has(prompt, "눈", "snow")) { s.Precipitation = 3f; s.Temperature = -3f; s.CloudCoverage = 0.9f; s.Humidity = 0.85f; n++; }
        else if (Has(prompt, "폭우", "downpour") || Has(prompt, "장대비", "heavy rain")) { s.Precipitation = 40f; s.Temperature = Math.Max(s.Temperature, 14f); s.CloudCoverage = 1f; s.Humidity = 0.97f; n++; }
        else if (Has(prompt, "이슬비", "drizzle")) { s.Precipitation = 0.8f; s.Temperature = Math.Max(s.Temperature, 10f); s.CloudCoverage = 0.85f; s.Humidity = 0.92f; n++; }
        else if (Has(prompt, "비", "rain")) { s.Precipitation = 8f; s.Temperature = Math.Max(s.Temperature, 12f); s.CloudCoverage = 0.9f; s.Humidity = 0.9f; n++; }
        if (Has(prompt, "뇌우", "thunder") || Has(prompt, "번개", "lightning") || Has(prompt, "폭풍", "storm")) { s.Storm = 0.9f; s.CloudCoverage = 1f; s.WindSpeed = Math.Max(s.WindSpeed, 11f); s.Precipitation = Math.Max(s.Precipitation, 20f); n++; }
        if (Has(prompt, "안개", "fog") || Has(prompt, "물안개", "mist")) { s.Fog = 0.75f; s.WindSpeed = Math.Min(s.WindSpeed, 1.5f); s.Humidity = 0.98f; n++; }
        if (Has(prompt, "맑", "clear") || Has(prompt, "화창", "sunny")) { s.Precipitation = 0f; s.CloudCoverage = 0.05f; s.Storm = 0f; s.Fog = 0f; s.Humidity = 0.4f; n++; }
        else if (Has(prompt, "흐림", "overcast") || Has(prompt, "구름", "cloudy")) { s.CloudCoverage = Math.Max(s.CloudCoverage, 0.85f); n++; }
        if (Has(prompt, "강풍", "gale") || Has(prompt, "태풍", "typhoon")) { s.WindSpeed = 20f; n++; }
        else if (Has(prompt, "바람", "windy")) { s.WindSpeed = Math.Max(s.WindSpeed, 7f); n++; }
        if (Has(prompt, "한겨울", "freezing")) { s.Temperature = -12f; n++; }
        else if (Has(prompt, "한여름", "hot")) { s.Temperature = 32f; n++; }
        return n;
    }

    public int TriggerPrompt(string? prompt)
    {
        var s = Target;
        var n = ParsePrompt(prompt, ref s);
        if (n > 0) SetScenario(s);
        LastAction = n > 0 ? "🌦 기상 프롬프트 반영 " + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + "건" : "⚠ 기상 키워드 없음";
        return n;
    }

    // ══════════════════════ 결정적 RNG · 주기 값 노이즈 ══════════════════════

    private float Next01()
    {
        _rng ^= _rng >> 12; _rng ^= _rng << 25; _rng ^= _rng >> 27;
        return ((_rng * 0x2545F4914F6CDD1DUL) >> 40) * (1f / (1 << 24));
    }

    private static float Hash(int x, int z, int t)
    {
        var h = (uint)x * 374761393u + (uint)z * 668265263u + (uint)t * 1274126177u;
        h = (h ^ (h >> 13)) * 1274126177u;
        return (h ^ (h >> 16)) * (1f / uint.MaxValue);
    }

    private static int Wrap(int i, int p) => ((i % p) + p) % p;

    /// <summary>x/z 주기 P 값 노이즈(시간 축 선형 보간 · 0..1).</summary>
    public static float PeriodicNoise(float x, float z, float t, int period)
    {
        int ix = (int)MathF.Floor(x), iz = (int)MathF.Floor(z), it = (int)MathF.Floor(t);
        float fx = x - ix, fz = z - iz, ft = t - it;
        fx = fx * fx * (3 - 2 * fx); fz = fz * fz * (3 - 2 * fz); ft = ft * ft * (3 - 2 * ft);
        int x0 = Wrap(ix, period), x1 = Wrap(ix + 1, period), z0 = Wrap(iz, period), z1 = Wrap(iz + 1, period);
        float a = Lerp(Lerp(Hash(x0, z0, it), Hash(x1, z0, it), fx), Lerp(Hash(x0, z1, it), Hash(x1, z1, it), fx), fz);
        float b = Lerp(Lerp(Hash(x0, z0, it + 1), Hash(x1, z0, it + 1), fx), Lerp(Hash(x0, z1, it + 1), Hash(x1, z1, it + 1), fx), fz);
        return Lerp(a, b, ft);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private void Resample(int i)
    {
        var rate = MathF.Max(_state.Precipitation, 0.1f);
        var f = SnowFraction; var u = _kindHash[i];
        var kind = f >= 0.85f ? PrecipKind.Snow : f <= 0.15f ? PrecipKind.Rain : u < f - 0.2f ? PrecipKind.Snow : u < f + 0.2f ? PrecipKind.Sleet : PrecipKind.Rain;
        _kind[i] = (byte)kind;
        if (kind == PrecipKind.Snow) { _size[i] = 2f + 4f * Next01(); _vt[i] = 0.7f + 0.5f * Next01(); }
        else if (kind == PrecipKind.Sleet) { _size[i] = 1f + 2f * Next01(); _vt[i] = 2.5f + 2f * Next01(); }
        else
        {
            var d = Math.Clamp(-MathF.Log(MathF.Max(Next01(), 1e-6f)) / MarshallPalmerLambda(rate), 0.3f, 5.5f);
            _size[i] = d; _vt[i] = RainTerminalVelocity(d);
        }
    }

    private void ResampleAll()
    {
        SnowFraction = SnowFractionOf(_state.Temperature);
        for (var i = 0; i < Capacity; i++) { Resample(i); _vx[i] = 0f; _vy[i] = -_vt[i]; _vz[i] = 0f; }
    }

    // ══════════════════════ 1틱 ══════════════════════

    /// <summary>1틱(무할당): 미세 기후 완화 → 바람장 → 운량장 → 강수 입자(SIMD) → 번개.</summary>
    public void Step(float dt)
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _t += dt;
            Relax(ref _state.Precipitation, _target.Precipitation, dt, 4f);
            Relax(ref _state.CloudCoverage, _target.CloudCoverage, dt, 8f);
            Relax(ref _state.Temperature, _target.Temperature, dt, 20f);
            Relax(ref _state.Humidity, _target.Humidity, dt, 10f);
            Relax(ref _state.WindSpeed, _target.WindSpeed, dt, 3f);
            Relax(ref _state.Storm, _target.Storm, dt, 5f);
            Relax(ref _state.Fog, _target.Fog, dt, 10f);
            var dyaw = MathF.IEEERemainder(_target.WindYawDeg - _state.WindYawDeg, 360f);
            _state.WindYawDeg += dyaw * (1f - MathF.Exp(-dt / 3f));

            Microclimate();
            WindField();
            Clouds();
            Particles(dt);
            Lightning(dt);
            LastStepMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
    }

    private static void Relax(ref float x, float target, float dt, float tau) => x += (target - x) * (1f - MathF.Exp(-dt / tau));

    private void Microclimate()
    {
        var p = _state.Precipitation;
        var rh = _state.Humidity + 0.3f * (1f - MathF.Exp(-p / 5f)) * (1f - _state.Humidity);
        RelativeHumidity = Math.Clamp(rh + 0.1f * Math.Clamp(SurfaceMoisture, 0f, 1f) * (1f - rh), 0.05f, 1f);
        DewPoint = DewPointOf(_state.Temperature, RelativeHumidity);
        var spread = _state.Temperature - DewPoint;
        var radiation = spread < 2f && _state.WindSpeed < 3f ? (2f - spread) * 0.5f * (1f - _state.WindSpeed / 3f) * (1f - _state.CloudCoverage * 0.5f) : 0f;
        FogAmount = Math.Clamp(MathF.Max(_state.Fog, radiation) + 0.15f * MathF.Min(1f, p / 30f), 0f, 1f);
        var vis = 20000f * MathF.Pow(50f / 20000f, FogAmount);
        FogExtinction = MathF.Max(0f, 3.912f / vis - 2.2e-4f);
        Haze = 1f + 4f * RelativeHumidity * RelativeHumidity + 2f * _state.Storm;
        var newSnow = SnowFractionOf(_state.Temperature);
        SnowFraction = newSnow;
        Kind = p < 0.05f ? PrecipKind.None : newSnow > 0.85f ? PrecipKind.Snow : newSnow < 0.15f ? PrecipKind.Rain : PrecipKind.Sleet;
        CloudOpticalDepth = 4f + 30f * _state.Storm + 12f * p / (p + 5f);
        Gust = 0.25f + 0.45f * _state.Storm;
    }

    private void WindField()
    {
        const float h = 2f * BoxHalf / GridN, nscale = NoisePeriod / (float)GridN;
        var w = MeanWind;
        // 동결 난류: 노이즈 좌표를 풍하로 이류(Taylor) · ψ 진폭 = 돌풍·풍속 / 공간 주파수
        float ox = -w.X * _t / h * nscale, oz = -w.Z * _t / h * nscale;
        var amp = Gust * MathF.Max(_state.WindSpeed, 0.5f) * h / (nscale * 1.5f);
        for (var j = 0; j < GridN; j++)
            for (var i = 0; i < GridN; i++)
                _psi[j * GridN + i] = amp * (PeriodicNoise(i * nscale + ox, j * nscale + oz, _t * 0.35f, NoisePeriod) - 0.5f);
        for (var j = 0; j < GridN; j++)
            for (var i = 0; i < GridN; i++)
            {
                int ip = (i + 1) % GridN, im = (i + GridN - 1) % GridN, jp = (j + 1) % GridN, jm = (j + GridN - 1) % GridN;
                _gu[j * GridN + i] = w.X + (_psi[jp * GridN + i] - _psi[jm * GridN + i]) / (2f * h);
                _gw[j * GridN + i] = w.Z - (_psi[j * GridN + ip] - _psi[j * GridN + im]) / (2f * h);
            }
    }

    /// <summary>바람장 이산 발산 RMS(중앙 차분 · 주기) — 회전 돌풍이면 ≈ 0.</summary>
    public float GridDivergenceRms()
    {
        lock (_gate)
        {
            const float h = 2f * BoxHalf / GridN;
            double s = 0;
            for (var j = 0; j < GridN; j++)
                for (var i = 0; i < GridN; i++)
                {
                    int ip = (i + 1) % GridN, im = (i + GridN - 1) % GridN, jp = (j + 1) % GridN, jm = (j + GridN - 1) % GridN;
                    var d = (_gu[j * GridN + ip] - _gu[j * GridN + im]) / (2f * h) + (_gw[jp * GridN + i] - _gw[jm * GridN + i]) / (2f * h);
                    s += d * d;
                }
            return (float)Math.Sqrt(s / (GridN * GridN));
        }
    }

    /// <summary>바람장 회전 성분 RMS(비교 기준).</summary>
    public float GridGustRms()
    {
        lock (_gate)
        {
            var w = MeanWind; double s = 0;
            for (var k = 0; k < GridN * GridN; k++) { var a = _gu[k] - w.X; var b = _gw[k] - w.Z; s += a * a + b * b; }
            return (float)Math.Sqrt(s / (GridN * GridN));
        }
    }

    /// <summary>상대 좌표(박스 중심 기준 · y = 지면 위 높이) 바람(쌍선형 · 로그 프로파일).</summary>
    public Vector3 WindAt(float x, float y, float z)
    {
        const float h = 2f * BoxHalf / GridN;
        float gx = (x + BoxHalf) / h - 0.5f, gz = (z + BoxHalf) / h - 0.5f;
        int i0 = (int)MathF.Floor(gx), j0 = (int)MathF.Floor(gz);
        float fx = gx - i0, fz = gz - j0;
        int ia = Wrap(i0, GridN), ib = Wrap(i0 + 1, GridN), ja = Wrap(j0, GridN), jb = Wrap(j0 + 1, GridN);
        var u = Lerp(Lerp(_gu[ja * GridN + ia], _gu[ja * GridN + ib], fx), Lerp(_gu[jb * GridN + ia], _gu[jb * GridN + ib], fx), fz);
        var w = Lerp(Lerp(_gw[ja * GridN + ia], _gw[ja * GridN + ib], fx), Lerp(_gw[jb * GridN + ia], _gw[jb * GridN + ib], fx), fz);
        var f = ProfileFactor(y);
        return new Vector3(u * f, 0f, w * f);
    }

    private void Clouds()
    {
        var w = MeanWind * 3f;
        var th = 1.1f - 1.2f * _state.CloudCoverage;
        float sum = 0f;
        const float s = 8f / CloudN;
        float ox = -w.X * _t * 0.004f, oz = -w.Z * _t * 0.004f;
        for (var j = 0; j < CloudN; j++)
            for (var i = 0; i < CloudN; i++)
            {
                float x = i * s + ox, z = j * s + oz;
                var f = 0.5f * PeriodicNoise(x, z, _t * 0.02f, 8) + 0.25f * PeriodicNoise(2f * x, 2f * z, _t * 0.04f, 16)
                      + 0.125f * PeriodicNoise(4f * x, 4f * z, _t * 0.08f, 32) + 0.0625f * PeriodicNoise(8f * x, 8f * z, _t * 0.16f, 64);
                f /= 0.9375f;
                var c = Math.Clamp((f - th + 0.12f) / 0.24f, 0f, 1f);
                c = c * c * (3f - 2f * c);
                _cloud[j * CloudN + i] = c; sum += c;
            }
        CloudCover = sum / (CloudN * CloudN);
    }

    private void Particles(float dt)
    {
        var target = (int)(Capacity * (1f - MathF.Exp(-_state.Precipitation / 12f)));
        if (_state.Precipitation < 0.05f) target = 0;
        if (target > ActiveParticles)
            for (var i = ActiveParticles; i < target; i++) { Resample(i); _py[i] = Next01() * BoxHeight; _vx[i] = 0f; _vy[i] = -_vt[i]; _vz[i] = 0f; }
        ActiveParticles = target;
        var n = target;
        if (n == 0 || dt <= 0f) { ImpactsPerSecond = 0f; MeanDropDiameter = 0f; return; }

        float dsum = 0f; var rain = 0;
        for (var i = 0; i < n; i++)
        {
            var wv = WindAt(_px[i], _py[i], _pz[i]);
            var tx = wv.X; var tz = wv.Z;
            if (_kind[i] == (byte)PrecipKind.Snow)
            {
                var a = 2.2f * _t + _phase[i];
                tx += 0.35f * MathF.Sin(a); tz += 0.35f * MathF.Cos(1.3f * a);
            }
            else { dsum += _size[i]; rain++; }
            _tx[i] = tx; _ty[i] = -_vt[i]; _tz[i] = tz;
            _k[i] = MathF.Min(1f, dt * 9.81f / _vt[i]);
        }
        MeanDropDiameter = rain > 0 ? dsum / rain : 0f;
        StrandSimd.Relax(_vx.AsSpan(0, n), _tx.AsSpan(0, n), _k.AsSpan(0, n));
        StrandSimd.Relax(_vy.AsSpan(0, n), _ty.AsSpan(0, n), _k.AsSpan(0, n));
        StrandSimd.Relax(_vz.AsSpan(0, n), _tz.AsSpan(0, n), _k.AsSpan(0, n));
        StrandSimd.Predict(_px.AsSpan(0, n), _vx.AsSpan(0, n), _px.AsSpan(0, n), dt);
        StrandSimd.Predict(_py.AsSpan(0, n), _vy.AsSpan(0, n), _py.AsSpan(0, n), dt);
        StrandSimd.Predict(_pz.AsSpan(0, n), _vz.AsSpan(0, n), _pz.AsSpan(0, n), dt);
        const float span = 2f * BoxHalf;
        for (var i = 0; i < n; i++)
        {
            if (_px[i] < -BoxHalf) _px[i] += span; else if (_px[i] >= BoxHalf) _px[i] -= span;
            if (_pz[i] < -BoxHalf) _pz[i] += span; else if (_pz[i] >= BoxHalf) _pz[i] -= span;
            if (_py[i] < 0f)
            {
                _impactsWindow++;
                Resample(i);
                _py[i] += BoxHeight;
                _px[i] = (Next01() * 2f - 1f) * BoxHalf; _pz[i] = (Next01() * 2f - 1f) * BoxHalf;
            }
            else if (_py[i] >= BoxHeight) _py[i] -= BoxHeight;
        }
        _impactClock += dt;
        if (_impactClock >= 0.5f) { ImpactsPerSecond = _impactsWindow / _impactClock; _impactsWindow = 0; _impactClock = 0f; }
    }

    private void Lightning(float dt)
    {
        var rate = 0.25f * _state.Storm * _state.Storm * Math.Clamp((_state.CloudCoverage - 0.3f) / 0.5f, 0f, 1f);
        // 비균질 포아송: 틱당 발생 확률 1 − exp(−λ·dt)(율 변화 즉시 반영)
        if (rate > 1e-4f && dt > 0f && Next01() < 1f - MathF.Exp(-rate * dt))
        {
            LightningStrikes++;
            _flashT = _t;
            _strokes = 2 + (int)(Next01() * 2.999f);
            _strokeT[0] = 0f;
            for (var k = 1; k < _strokes; k++) _strokeT[k] = _strokeT[k - 1] + 0.04f + 0.06f * Next01();
            LastStrikeDistance = 300f + 7700f * Next01() * Next01();
            LastStrikeAzimuth = 360f * Next01();
            ThunderDelay = LastStrikeDistance / 343f;
            var x = 0.2f + 0.6f * Next01();
            for (var k = 0; k < BoltPoints; k++)
            {
                _boltY[k] = k / (float)(BoltPoints - 1);
                if (k > 0) x = Math.Clamp(x + (Next01() - 0.5f) * 0.06f, 0.02f, 0.98f);
                _boltX[k] = x;
            }
        }
        // 귀환 뇌격 포락선: Σ exp(−Δt/30 ms) × 거리 감쇠
        var since = _t - _flashT; var f = 0f;
        for (var k = 0; k < _strokes; k++)
        {
            var d = since - _strokeT[k];
            if (d >= 0f && d < 0.5f) f += MathF.Exp(-d / 0.03f) * (k == 0 ? 1f : 0.7f);
        }
        var dd = LastStrikeDistance / 2000f;
        FlashIntensity = Math.Clamp(f / (1f + dd * dd), 0f, 1.5f);
    }
}
