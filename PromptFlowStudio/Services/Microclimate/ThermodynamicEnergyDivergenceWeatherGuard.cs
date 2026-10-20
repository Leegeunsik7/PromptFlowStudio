namespace PromptFlowStudio.Services.Microclimate;

/// <summary>
/// [PromptFlow Studio v17.28.0] 🛡 열역학 에너지 발산 &amp; 기압 폭주 방어 가드(<see cref="Project"/> · <see cref="EndFrame"/> 무할당).
///   ▸ 헬름홀츠-호지 분해: u = u_df + ∇φ — 발산(SIMD) → 포아송 ∇²φ = ∇·u(중심 차분과 정합하는 2칸 라플라시안) 색 SOR <see cref="Iterations"/>회(ω 1.8 · 색 마스크 SIMD 제자리 · 노이만 경계) → u −= ∇φ(SIMD) · 투영 전/후 RMS 발산 측정.
///   ▸ 수치 위생: NaN/∞ 속도 셀을 0 으로 격리(이웃 오염 차단) · 마하 0.3(102.9 m/s) 크기 클램프.
///   ▸ CFL: 하위 단계 Δt ≤ 0.8·Δx / 최대 풍속(최대 8) — 강풍 시 반라그랑주 역추적 거리 제한.
///   ▸ 에너지 클램프: 운동 에너지가 (직전 + 외력 일 + 2 %) 를 넘으면 초과분만큼 전 격자 속도 축소.
///   ▸ 가드 OFF: 투영 · 클램프 · 위생 · CFL 모두 생략(발산 누적 · NaN 전파 · 초음속 폭주 비교).
/// </summary>
public sealed class ThermodynamicEnergyDivergenceWeatherGuard
{
    public const float SoundSpeed = 343f, MachLimit = 0.3f, MaxSpeed = SoundSpeed * MachLimit, Cfl = 0.8f, EnergyTol = 0.02f;
    public const int MaxSubsteps = 8;

    private float[] _red = Array.Empty<float>(), _black = Array.Empty<float>();
    private bool[] _measure = Array.Empty<bool>();
    private int[] _bIdx = Array.Empty<int>(), _bNbr = Array.Empty<int>();
    private float _kePrev = float.NaN;

    public bool Enabled { get; set; } = true;
    public bool MachClamp { get; set; } = true;
    public bool EnergyClamp { get; set; } = true;
    public int Iterations { get; set; } = 40;
    public const float Omega = 1.8f;
    public long Projections { get; private set; }
    public long MachClamps { get; private set; }
    public long NanScrubbed { get; private set; }
    public long EnergyClamps { get; private set; }
    public float DivergenceBefore { get; private set; }
    public float DivergenceAfter { get; private set; }
    public float MaxObservedSpeed { get; private set; }
    public int LastSubsteps { get; private set; }
    public double LastProjectUs { get; private set; }

    public void OnReset() { _kePrev = float.NaN; }

    public void ResetMetrics()
    {
        Projections = MachClamps = NanScrubbed = EnergyClamps = 0;
        MaxObservedSpeed = 0f;
    }

    public int PlanSubsteps(RealtimeAtmosphericWeatherSimulationEngine e, float dt, out float h)
    {
        if (!Enabled) { h = dt; LastSubsteps = 1; return 1; }
        var v = MathF.Max(e.MaxSpeed, 1f);
        var n = Math.Clamp((int)MathF.Ceiling(v * dt / (Cfl * RealtimeAtmosphericWeatherSimulationEngine.H)), 1, MaxSubsteps);
        h = dt / n;
        LastSubsteps = n;
        return n;
    }

    /// <summary>헬름홀츠-호지 무발산 투영 + 위생 + 마하 클램프.</summary>
    public void Project(RealtimeAtmosphericWeatherSimulationEngine e, float dt)
    {
        if (!Enabled) return;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        const int N = RealtimeAtmosphericWeatherSimulationEngine.N, NX = RealtimeAtmosphericWeatherSimulationEngine.NX, NXY = RealtimeAtmosphericWeatherSimulationEngine.NXY;
        const float h = RealtimeAtmosphericWeatherSimulationEngine.H, inv2h = 0.5f / h, h2 = h * h;
        EnsureBoundary(e);
        float[] u = e.U, v = e.V, w = e.W, p = e.Pressure, div = e.Divergence;
        // ① 위생: 비유한 셀 격리
        for (var i = 0; i < N; i++)
            if (!float.IsFinite(u[i]) || !float.IsFinite(v[i]) || !float.IsFinite(w[i])) { u[i] = v[i] = w[i] = 0f; NanScrubbed++; }
        // ② 발산
        Div(e, u, v, w, div, inv2h);
        DivergenceBefore = Rms(e, div);
        // ③ 포아송(동위치 격자 정합): 중심 차분 ∇·∇ = 2칸 간격 라플라시안 → p = (Σ ±2 이웃 − 4h²·div)/6 · 2칸 색(⌊x/2⌋+⌊y/2⌋+⌊z/2⌋ 홀짝) SOR ω 1.8 · 직전 해 재사용
        for (var it = 0; it < Iterations; it++)
        {
            if (e.UseSimd) { WeatherKernels.SorColorSimd(p, div, _red, 2, 2 * NX, 2 * NXY, 4f * h2, Omega); Neumann(p); WeatherKernels.SorColorSimd(p, div, _black, 2, 2 * NX, 2 * NXY, 4f * h2, Omega); }
            else { WeatherKernels.SorColorScalar(p, div, _red, 2, 2 * NX, 2 * NXY, 4f * h2, Omega); Neumann(p); WeatherKernels.SorColorScalar(p, div, _black, 2, 2 * NX, 2 * NXY, 4f * h2, Omega); }
            Neumann(p);
        }
        // ④ u −= ∇φ
        if (e.UseSimd) WeatherKernels.SubtractGradientSimd(u, v, w, p, NX, NXY, inv2h);
        else WeatherKernels.SubtractGradientScalar(u, v, w, p, NX, NXY, inv2h);
        for (var i = 0; i < N; i++) if (e.Solid(i)) { u[i] = v[i] = w[i] = 0f; }
        Div(e, u, v, w, div, inv2h);
        DivergenceAfter = Rms(e, div);
        // ⑤ 마하 0.3 클램프
        if (MachClamp)
        {
            const float m2 = MaxSpeed * MaxSpeed;
            for (var i = 0; i < N; i++)
            {
                var s2 = u[i] * u[i] + v[i] * v[i] + w[i] * w[i];
                if (s2 <= m2) continue;
                var k = MaxSpeed / MathF.Sqrt(s2);
                u[i] *= k; v[i] *= k; w[i] *= k;
                MachClamps++;
            }
        }
        Projections++;
        LastProjectUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private static void Div(RealtimeAtmosphericWeatherSimulationEngine e, float[] u, float[] v, float[] w, float[] div, float inv2h)
    {
        if (e.UseSimd) WeatherKernels.DivergenceSimd(u, v, w, div, RealtimeAtmosphericWeatherSimulationEngine.NX, RealtimeAtmosphericWeatherSimulationEngine.NXY, inv2h);
        else WeatherKernels.DivergenceScalar(u, v, w, div, RealtimeAtmosphericWeatherSimulationEngine.NX, RealtimeAtmosphericWeatherSimulationEngine.NXY, inv2h);
    }

    /// <summary>자유 대기 셀 RMS 발산(1/s) — 2겹 도메인 껍질 · 고체로부터 2셀 이내(동위치 격자 고체 경계 근사 구간) 제외.</summary>
    private float Rms(RealtimeAtmosphericWeatherSimulationEngine e, float[] div)
    {
        EnsureBoundary(e);
        double s = 0;
        var n = 0;
        for (var i = 0; i < div.Length; i++)
        {
            if (!_measure[i] || !float.IsFinite(div[i])) continue;
            s += div[i] * div[i];
            n++;
        }
        return n > 0 ? (float)Math.Sqrt(s / n) : 0f;
    }

    /// <summary>외부 측정용 RMS 발산(가드 OFF 비교).</summary>
    public float MeasureDivergence(RealtimeAtmosphericWeatherSimulationEngine e)
    {
        Div(e, e.U, e.V, e.W, e.Divergence, 0.5f / RealtimeAtmosphericWeatherSimulationEngine.H);
        return Rms(e, e.Divergence);
    }

    // 도메인 측면 · 바닥 + 고체 셀: φ = 내측 이웃 φ (∂φ/∂n = 0) · 상단: φ = 0
    private void EnsureBoundary(RealtimeAtmosphericWeatherSimulationEngine e)
    {
        if (_bIdx.Length > 0) return;
        const int NX = RealtimeAtmosphericWeatherSimulationEngine.NX, NY = RealtimeAtmosphericWeatherSimulationEngine.NY, NZ = RealtimeAtmosphericWeatherSimulationEngine.NZ;
        _red = new float[RealtimeAtmosphericWeatherSimulationEngine.N];
        _black = new float[RealtimeAtmosphericWeatherSimulationEngine.N];
        _measure = new bool[RealtimeAtmosphericWeatherSimulationEngine.N];
        for (var z = 2; z < NZ - 2; z++)
            for (var y = 2; y < NY - 2; y++)
                for (var x = 2; x < NX - 2; x++)
                {
                    var near = false;
                    for (var dz = -2; dz <= 2 && !near; dz++)
                        for (var dy = -2; dy <= 2 && !near; dy++)
                            for (var dx = -2; dx <= 2 && !near; dx++)
                            {
                                int xx = x + dx, yy = y + dy, zz = z + dz;
                                if ((uint)xx < NX && (uint)yy < NY && (uint)zz < NZ && e.Solid(RealtimeAtmosphericWeatherSimulationEngine.Idx(xx, yy, zz))) near = true;
                            }
                    _measure[RealtimeAtmosphericWeatherSimulationEngine.Idx(x, y, z)] = !near;
                }
        var cnt = 0;
        for (var pass = 0; pass < 2; pass++)
        {
            var k = 0;
            for (var z = 0; z < NZ; z++)
                for (var y = 0; y < NY; y++)
                    for (var x = 0; x < NX; x++)
                    {
                        var face = x < 2 || x > NX - 3 || y < 2 || y > NY - 3 || z < 2 || z > NZ - 3;   // 2칸 스텐실 → 2겹 껍질
                        var i = RealtimeAtmosphericWeatherSimulationEngine.Idx(x, y, z);
                        if (!face && !e.Solid(i))
                        {
                            if (pass == 0) (((x >> 1) + (y >> 1) + (z >> 1)) % 2 == 0 ? _red : _black)[i] = 1f;
                            continue;
                        }
                        if (pass == 1)
                        {
                            int nx = Math.Clamp(x, 2, NX - 3), ny = Math.Clamp(y, 2, NY - 3), nz = Math.Clamp(z, 2, NZ - 3);
                            if (!face) ny = Math.Min(NY - 3, y + 1);   // 고체: 바로 위 셀
                            _bIdx[k] = i; _bNbr[k] = face && y > NY - 3 ? -1 : RealtimeAtmosphericWeatherSimulationEngine.Idx(nx, ny, nz);   // 상단 개방(φ = 0 디리클레) → 순 유입/유출 불균형 흡수
                        }
                        k++;
                    }
            if (pass == 0) { cnt = k; _bIdx = new int[cnt]; _bNbr = new int[cnt]; }
        }
    }

    private void Neumann(float[] p)
    {
        for (var k = 0; k < _bIdx.Length; k++) p[_bIdx[k]] = _bNbr[k] < 0 ? 0f : p[_bNbr[k]];
    }

    /// <summary>프레임 종료: 최대 풍속 기록 · 운동 에너지 클램프(외력 일 + 허용 오차 초과분).</summary>
    public void EndFrame(RealtimeAtmosphericWeatherSimulationEngine e)
    {
        if (e.MaxSpeed > MaxObservedSpeed || !float.IsFinite(e.MaxSpeed)) MaxObservedSpeed = float.IsFinite(e.MaxSpeed) ? e.MaxSpeed : float.PositiveInfinity;
        var ke = e.KineticEnergy;
        if (Enabled && EnergyClamp && float.IsFinite(_kePrev) && float.IsFinite(ke))
        {
            var allowed = _kePrev + MathF.Max(0f, e.ForcingWork) + EnergyTol * _kePrev + 1f;
            if (ke > allowed)
            {
                e.ScaleVelocities(MathF.Sqrt(allowed / ke));
                EnergyClamps++;
                ke = e.KineticEnergy;
            }
        }
        _kePrev = ke;
    }
}
