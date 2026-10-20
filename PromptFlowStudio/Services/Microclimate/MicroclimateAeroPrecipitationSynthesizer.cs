using System.Numerics;
using PromptFlowStudio.Services.ContinuumPhysics;

namespace PromptFlowStudio.Services.Microclimate;

/// <summary>
/// [PromptFlow Studio v17.28.0] 🌬 미기후 에어로-강수 결합 합성기(<see cref="StepAero"/> · <see cref="StepPrecipitation"/> 무할당).
///   ▸ 공기역학 항력: 액터 머리 위치 풍속(<see cref="RealtimeAtmosphericWeatherSimulationEngine.WindAt"/>)을 헤어 가이드 프록시(24 가닥 × 8 노드 · v17.15 가이드 규격)와
///     의상 프록시(망토 10×12 정점 · v17.16) 에 투영 — 헤어 a = k·|v_rel|·v_rel · 의상 a = k·(v_rel·n)|v_rel·n|·n · PBD 거리 구속 · 머리/몸통 충돌.
///   ▸ 강수 입자 링버퍼(4096): 기둥 강수율(mm/h) × Δt 누적기 → 입자 분량(적응형 mm) 차면 운저에서 방출 · 종단 속도(비 7 · 눈 1.2 m/s) + 풍장 표류 · 낙하 중 융해 · 링 포화 시 즉시 침착(질량 보존).
///   ▸ 젖음 맵(32×32 기둥 · v17.19 막 두께 µm → 젖음 = 막/20) · 증발(풍속 · 습도 · 포화 수증기압) · 액터 바람받이 젖음.
///   ▸ 적설: 눈 입자 SWE → 기둥 적설(cm) · 영상 융해 → 젖음 · 액터 주변 3.2 m 패치(0.1 m 셀 · 바위 돔)에서 풍하 표류 + v17.26 드루커-프라거 마찰각(30°) 안식각 사태 이완.
/// </summary>
public sealed class MicroclimateAeroPrecipitationSynthesizer
{
    public const int Strands = 24, Nodes = 8, HairN = Strands * Nodes, CapeW = 10, CapeH = 12, CapeN = CapeW * CapeH;
    public const int Ring = 4096, Patch = 32, Cols = RealtimeAtmosphericWeatherSimulationEngine.Columns;
    public const float Seg = 0.04f, HeadR = 0.11f, CapeSeg = 0.06f, HairK = 0.17f, CapeK = 0.6f, PatchCell = 0.1f, RainFall = 7f, SnowFall = 1.2f, FilmFull = 20f;

    // 헤어/의상 PBD
    private readonly Vector3[] _hx = new Vector3[HairN], _hp = new Vector3[HairN], _cx = new Vector3[CapeN], _cp = new Vector3[CapeN], _hRoot = new Vector3[Strands];
    // 강수 입자
    private readonly float[] _px = new float[Ring], _py = new float[Ring], _pz = new float[Ring], _amt = new float[Ring];
    private readonly bool[] _snow = new bool[Ring], _live = new bool[Ring];
    private int _head;
    private uint _rng = 0x51ED270Bu;
    // 지면 맵
    private readonly float[] _film = new float[Cols], _snowCm = new float[Cols], _pending = new float[Cols];
    private readonly float[] _rock = new float[Patch * Patch], _patch = new float[Patch * Patch], _tmp = new float[Patch * Patch];
    private Vector3 _actor, _headC;

    public MicroclimateAeroPrecipitationSynthesizer()
    {
        for (var j = 0; j < Patch; j++)
            for (var i = 0; i < Patch; i++)
            {
                float dx = (i - Patch / 2 + 0.5f) * PatchCell, dz = (j - Patch / 2 + 0.5f) * PatchCell;
                var r2 = (dx - 0.6f) * (dx - 0.6f) + dz * dz;
                _rock[j * Patch + i] = r2 < 1f ? 0.6f * (1f - r2) : 0f;   // 바위 돔(반경 1 m · 0.6 m)
            }
        Place(Vector3.Zero);
    }

    public bool AeroEnabled { get; set; } = true;
    public bool PrecipEnabled { get; set; } = true;
    public float RainOverrideMmH { get; set; }
    /// <summary>입자 1개가 운반하는 물(mm) — 공중 입자 수가 링의 50–80 % 에 머물도록 적응.</summary>
    public float ParticleMm { get; private set; } = 0.02f;
    public Vector3 WindAtHead { get; private set; }
    public float HairDeflectionDeg { get; private set; }
    public float CapeLiftDeg { get; private set; }
    public float MaxNodeSpeed { get; private set; }
    public int Airborne { get; private set; }
    public int AirborneSnow { get; private set; }
    public long Emitted { get; private set; }
    public long Deposited { get; private set; }
    public long Overflow { get; private set; }
    public double EmittedMm { get; private set; }
    public double DepositedMm { get; private set; }
    /// <summary>공중 입자 + 기둥 누적기에 남은 물(mm) — 방출 = 침착 + 이 값(질량 보존 검증).</summary>
    public double InFlightMm { get { double s = 0; for (var k = 0; k < Ring; k++) if (_live[k]) s += _amt[k]; for (var c = 0; c < Cols; c++) s += _pending[c]; return s; } }
    public float ActorFilmUm { get; private set; }
    public float ActorWetness => Math.Clamp(ActorFilmUm / FilmFull, 0f, 1f);
    public float MeanWetness { get; private set; }
    public float MeanSnowCm { get; private set; }
    public float PatchMaxSlope { get; private set; }
    public float PatchSnowM3 { get; private set; }
    public long Avalanches { get; private set; }
    public float PatchAddedM3 { get; private set; }
    public double LastAeroUs { get; private set; }
    public double LastPrecipUs { get; private set; }
    public Vector3 HairNode(int s, int k) => _hx[s * Nodes + k];
    public Vector3 CapeVertex(int i, int j) => _cx[j * CapeW + i];
    public Vector3 HeadCenter => _headC;
    public Vector3 ActorBase => _actor;
    public float Wetness(int c) => Math.Clamp(_film[c] / FilmFull, 0f, 1f);
    public float SnowCm(int c) => _snowCm[c];
    public float PatchSnow(int i, int j) => _patch[j * Patch + i];
    public float PatchRock(int i, int j) => _rock[j * Patch + i];
    public bool ParticleLive(int k) => _live[k];
    public Vector3 Particle(int k) => new(_px[k], _py[k], _pz[k]);
    public bool ParticleSnow(int k) => _snow[k];
    public static float ReposeTan => MathF.Tan(ContinuumMaterials.FrictionAngleDeg * (MathF.PI / 180f));

    /// <summary>액터 배치(지면 기준) · 헤어/망토 정지 자세.</summary>
    public void Place(Vector3 groundPos)
    {
        _actor = groundPos;
        _headC = groundPos + new Vector3(0f, 1.65f, 0f);
        for (var s = 0; s < Strands; s++)
        {
            var a = s * (2f * MathF.PI / Strands);
            var root = _headC + new Vector3(MathF.Sin(a) * HeadR * 0.95f, HeadR * 0.35f, MathF.Cos(a) * HeadR * 0.95f);
            _hRoot[s] = root;
            var outward = Vector3.Normalize(new Vector3(MathF.Sin(a), 0f, MathF.Cos(a)));
            for (var k = 0; k < Nodes; k++)
            {
                var p = root + outward * (0.01f * k) - Vector3.UnitY * (Seg * k);
                _hx[s * Nodes + k] = p; _hp[s * Nodes + k] = p;
            }
        }
        for (var j = 0; j < CapeH; j++)
            for (var i = 0; i < CapeW; i++)
            {
                var p = groundPos + new Vector3((i - (CapeW - 1) * 0.5f) * CapeSeg, 1.45f - j * CapeSeg, -0.16f);
                _cx[j * CapeW + i] = p; _cp[j * CapeW + i] = p;
            }
    }

    public void ResetGround()
    {
        Array.Clear(_film); Array.Clear(_snowCm); Array.Clear(_patch); Array.Clear(_live); Array.Clear(_pending);
        ParticleMm = 0.02f;
        Airborne = AirborneSnow = 0; Emitted = Deposited = Overflow = Avalanches = 0; EmittedMm = DepositedMm = 0; ActorFilmUm = 0f; PatchAddedM3 = 0f;
    }

    /// <summary>실시간 Δt(1/60 s) 헤어 · 망토 공기역학 PBD.</summary>
    public void StepAero(RealtimeAtmosphericWeatherSimulationEngine e, float dt)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        // 최하층 셀 중심(25 m) 풍속 → 머리 높이 로그 법칙 보정(z0 5 cm) · 지면 근처 연직 성분 감쇠
        var cell = e.WindAt(new Vector3(_headC.X, _actor.Y + 0.5f * RealtimeAtmosphericWeatherSimulationEngine.H, _headC.Z));
        const float logK = 0.568f;   // ln(1.7/0.05)/ln(25/0.05)
        var wind = AeroEnabled ? new Vector3(cell.X * logK, cell.Y * (1.65f / 25f), cell.Z * logK) : Vector3.Zero;
        if (!float.IsFinite(wind.X + wind.Y + wind.Z)) wind = Vector3.Zero;
        WindAtHead = wind;
        const int sub = 2;
        var h = dt / sub;
        var g = new Vector3(0f, -9.81f, 0f);
        var vmax = 0f;
        for (var s = 0; s < sub; s++)
        {
            // 헤어: 베를레 + 이차 항력
            for (var n = 0; n < HairN; n++)
            {
                if (n % Nodes == 0) { _hx[n] = _hRoot[n / Nodes]; _hp[n] = _hx[n]; continue; }
                var v = (_hx[n] - _hp[n]) / h;
                var rel = wind - v;
                var a = g + HairK * rel.Length() * rel;
                var nx = _hx[n] + (_hx[n] - _hp[n]) * 0.985f + a * (h * h);
                _hp[n] = _hx[n]; _hx[n] = nx;
            }
            for (var it = 0; it < 4; it++)
                for (var st = 0; st < Strands; st++)
                    for (var k = 1; k < Nodes; k++)
                    {
                        int a = st * Nodes + k - 1, b = a + 1;
                        var d = _hx[b] - _hx[a];
                        var l = d.Length();
                        if (l < 1e-9f) continue;
                        var c = d * ((l - Seg) / l);
                        if (k == 1) _hx[b] -= c;
                        else { _hx[a] += 0.5f * c; _hx[b] -= 0.5f * c; }
                        // 두피 구 충돌
                        var r = _hx[b] - _headC;
                        var rl = r.Length();
                        if (rl < HeadR) _hx[b] = _headC + r * (HeadR / MathF.Max(rl, 1e-6f));
                    }
            // 망토: 법선 투영 항력
            for (var j = 0; j < CapeH; j++)
                for (var i = 0; i < CapeW; i++)
                {
                    var n = j * CapeW + i;
                    if (j == 0) { _cp[n] = _cx[n]; continue; }
                    var tx = _cx[j * CapeW + Math.Min(i + 1, CapeW - 1)] - _cx[j * CapeW + Math.Max(i - 1, 0)];
                    var ty = _cx[(j - 1) * CapeW + i] - _cx[Math.Min(j + 1, CapeH - 1) * CapeW + i];
                    var nn = Vector3.Cross(tx, ty);
                    var nl = nn.Length();
                    nn = nl > 1e-9f ? nn / nl : Vector3.UnitZ;
                    var v = (_cx[n] - _cp[n]) / h;
                    var rel = wind - v;
                    var vn = Vector3.Dot(rel, nn);
                    var a = g + CapeK * vn * MathF.Abs(vn) * nn + 0.1f * CapeK * (rel - vn * nn) * rel.Length();
                    var nx = _cx[n] + (_cx[n] - _cp[n]) * 0.98f + a * (h * h);
                    _cp[n] = _cx[n]; _cx[n] = nx;
                }
            for (var it = 0; it < 6; it++)
            {
                for (var j = 0; j < CapeH; j++)
                    for (var i = 0; i < CapeW; i++)
                    {
                        var n = j * CapeW + i;
                        if (i + 1 < CapeW) Constrain(_cx, n, n + 1, CapeSeg, j == 0);
                        if (j + 1 < CapeH) Constrain(_cx, n, n + CapeW, CapeSeg, j == 0);
                    }
                // 몸통 캡슐(등 뒤 z ≥ −0.14) 비침투
                for (var n = CapeW; n < CapeN; n++)
                {
                    var p = _cx[n];
                    var rel = new Vector2(p.X - _actor.X, p.Z - _actor.Z);
                    if (p.Y > _actor.Y + 0.1f && p.Y < _actor.Y + 1.5f && rel.Length() < 0.15f)
                    {
                        var r = rel.Length() > 1e-6f ? Vector2.Normalize(rel) : new Vector2(0f, -1f);
                        _cx[n] = new Vector3(_actor.X + r.X * 0.15f, p.Y, _actor.Z + r.Y * 0.15f);
                    }
                }
            }
        }
        // 지표
        double hd = 0;
        for (var st = 0; st < Strands; st++)
        {
            var d = _hx[st * Nodes + Nodes - 1] - _hx[st * Nodes];
            var l = d.Length();
            hd += l > 1e-6f ? MathF.Acos(Math.Clamp(-d.Y / l, -1f, 1f)) : 0f;
            for (var k = 0; k < Nodes; k++) vmax = MathF.Max(vmax, (_hx[st * Nodes + k] - _hp[st * Nodes + k]).Length() / h);
        }
        HairDeflectionDeg = (float)(hd / Strands) * (180f / MathF.PI);
        var bottom = Vector3.Zero;
        for (var i = 0; i < CapeW; i++) bottom += _cx[(CapeH - 1) * CapeW + i] - _cx[i];
        bottom /= CapeW;
        var bl = bottom.Length();
        CapeLiftDeg = bl > 1e-6f ? MathF.Acos(Math.Clamp(-bottom.Y / bl, -1f, 1f)) * (180f / MathF.PI) : 0f;
        MaxNodeSpeed = vmax;
        LastAeroUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private static void Constrain(Vector3[] x, int a, int b, float rest, bool pinA)
    {
        var d = x[b] - x[a];
        var l = d.Length();
        if (l < 1e-9f) return;
        var c = d * ((l - rest) / l);
        if (pinA) x[b] -= c;
        else { x[a] += 0.5f * c; x[b] -= 0.5f * c; }
    }

    /// <summary>시뮬레이션 Δt(가속 시간) 강수 방출 · 낙하 · 침착 · 젖음/적설/사태.</summary>
    public void StepPrecipitation(RealtimeAtmosphericWeatherSimulationEngine e, float dt)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        const int NX = RealtimeAtmosphericWeatherSimulationEngine.NX, NZ = RealtimeAtmosphericWeatherSimulationEngine.NZ, NY = RealtimeAtmosphericWeatherSimulationEngine.NY;
        const float H = RealtimeAtmosphericWeatherSimulationEngine.H;
        var actorCol = (NZ / 2) * NX + NX / 2;
        if (PrecipEnabled)
        {
            // ① 방출(운저 · 기둥별)
            for (var c = 0; c < Cols; c++)
            {
                var mmh = e.ColumnPrecip(c) + (e.ColumnCloud(c) > 0.02f || RainOverrideMmH > 0f ? RainOverrideMmH : 0f);
                if (mmh <= 0f) continue;
                var water = mmh / 3600f * dt;   // mm
                EmittedMm += water;
                _pending[c] += water;
                if (_pending[c] < ParticleMm) continue;   // 기둥 누적기(질량 보존) — 입자 1개 분량이 차면 방출
                int x = c % NX, z = c / NX;
                var baseY = NY - 2;
                for (var y = e.GroundLevel(c); y < NY; y++) if (e.CloudWater(RealtimeAtmosphericWeatherSimulationEngine.Idx(x, y, z)) > 0.02f) { baseY = y; break; }
                var count = Math.Min(4, (int)(_pending[c] / ParticleMm));
                var each = _pending[c] / count;
                _pending[c] = 0f;
                for (var k = 0; k < count; k++)
                {
                    var py = (baseY + Rand()) * H;
                    var px = (x - NX * 0.5f + Rand()) * H;
                    var pz = (z - NZ * 0.5f + Rand()) * H;
                    var snow = e.TemperatureAt(new Vector3(px, py, pz)) < 0.5f;
                    if (_live[_head]) { Deposit(_head); Overflow++; }   // 링 포화: 가장 오래된 입자 즉시 침착(질량 보존)
                    _px[_head] = px; _py[_head] = py; _pz[_head] = pz; _amt[_head] = each; _snow[_head] = snow; _live[_head] = true;
                    _head = (_head + 1) % Ring;
                    Emitted++;
                }
            }
        }
        // ② 낙하 · 표류 · 융해 · 침착
        var air = 0;
        var airSnow = 0;
        for (var k = 0; k < Ring; k++)
        {
            if (!_live[k]) continue;
            var p = new Vector3(_px[k], _py[k], _pz[k]);
            var w = e.WindAt(p);
            if (!float.IsFinite(w.X + w.Y + w.Z)) w = Vector3.Zero;
            var fall = _snow[k] ? SnowFall : RainFall;
            p += new Vector3(w.X, MathF.Min(w.Y, fall * 0.5f) - fall, w.Z) * dt;
            const float half = RealtimeAtmosphericWeatherSimulationEngine.NX * 0.5f * H - 1f;
            p.X = Math.Clamp(p.X, -half, half); p.Z = Math.Clamp(p.Z, -half, half);
            if (_snow[k] && e.TemperatureAt(p) > 1.5f) _snow[k] = false;
            _px[k] = p.X; _py[k] = p.Y; _pz[k] = p.Z;
            var c = Column(p);
            var gy = (e.GroundLevel(c) + 0.5f) * H;
            if (p.Y <= gy)
            {
                Deposit(k);
                continue;
            }
            air++;
            if (_snow[k]) airSnow++;
        }
        Airborne = air; AirborneSnow = airSnow;
        if (air > Ring * 8 / 10) ParticleMm = MathF.Min(5f, ParticleMm * 1.05f);
        else if (air < Ring / 2) ParticleMm = MathF.Max(0.002f, ParticleMm / 1.05f);
        // ③ 증발 · 융해(지면 기온)
        double wet = 0, sn = 0;
        for (var c = 0; c < Cols; c++)
        {
            int x = c % NX, z = c / NX;
            var gi = RealtimeAtmosphericWeatherSimulationEngine.Idx(x, e.GroundLevel(c), z);
            var tc = e.Temperature(gi);
            if (_snowCm[c] > 0f && tc > 0f)
            {
                var m = MathF.Min(_snowCm[c], 0.002f * tc * dt);   // cm/s/°C
                _snowCm[c] -= m;
                _film[c] = MathF.Min(300f, _film[c] + m * 1000f);   // 1 cm 눈 ≈ 1 mm 물
            }
            if (_film[c] > 0f)
            {
                var u = new Vector3(e.U[gi], 0f, e.W[gi]).Length();
                var qs = RealtimeAtmosphericWeatherSimulationEngine.Qs(tc, 1013f);
                var rh = qs > 0f ? Math.Clamp(e.Vapor(gi) / qs, 0f, 1f) : 1f;
                _film[c] = MathF.Max(0f, _film[c] - 0.004f * (1f + 0.4f * (float.IsFinite(u) ? u : 0f)) * (1f - rh) * qs * dt);
            }
            wet += Math.Clamp(_film[c] / FilmFull, 0f, 1f);
            sn += _snowCm[c];
        }
        MeanWetness = (float)(wet / Cols);
        MeanSnowCm = (float)(sn / Cols);
        // ④ 액터 젖음: 액터 기둥 강우 플럭스(mm/h · 지면 기온 > 0.5 °C) × 바람받이 노출 · 증발
        var wind = WindAtHead.Length();
        var aPr = e.ColumnPrecip(actorCol) + (PrecipEnabled ? RainOverrideMmH : 0f);
        var tg = e.TemperatureAt(_actor + new Vector3(0f, 0.5f * H, 0f));
        if (!PrecipEnabled) aPr = 0f;
        var rainFlux = tg > 0.5f ? aPr : 0f;
        var snowFlux = tg > 0.5f ? 0f : aPr;
        ActorFilmUm = MathF.Min(300f, ActorFilmUm + rainFlux / 3600f * dt * 1000f * (1f + 0.25f * wind) * 0.3f);
        var tA = e.TemperatureAt(_headC);
        ActorFilmUm = MathF.Max(0f, ActorFilmUm - 0.002f * (1f + 0.4f * wind) * MathF.Max(0f, tA + 5f) * dt);
        // ⑤ 액터 주변 적설 패치: 강설 SWE 플럭스 → 깊이(m) · 풍하 표류 · 안식각 이완
        if (snowFlux > 0f) DepositPatch(snowFlux / 3600f * dt * 0.01f);   // SWE mm → 깊이 m(밀도 100 kg/m³)
        DriftPatch(dt);
        RelaxPatch();
        LastPrecipUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>패치 전 셀에 균일 적설(m) — 이후 이완은 프레임마다(또는 <see cref="RelaxPatch"/> 직접 호출).</summary>
    public void DepositPatch(float meters)
    {
        if (meters <= 0f) return;
        for (var i = 0; i < _patch.Length; i++) _patch[i] += meters;
        PatchAddedM3 += meters * _patch.Length * PatchCell * PatchCell;
    }

    private void Deposit(int k)
    {
        var c = Column(new Vector3(_px[k], 0f, _pz[k]));
        if (_snow[k]) _snowCm[c] += _amt[k];   // SWE 1 mm ≈ 1 cm
        else _film[c] = MathF.Min(300f, _film[c] + _amt[k] * 1000f);
        DepositedMm += _amt[k];
        Deposited++;
        _live[k] = false;
    }

    private static int Column(Vector3 p)
    {
        const int NX = RealtimeAtmosphericWeatherSimulationEngine.NX, NZ = RealtimeAtmosphericWeatherSimulationEngine.NZ;
        const float H = RealtimeAtmosphericWeatherSimulationEngine.H;
        var x = Math.Clamp((int)MathF.Floor(p.X / H + NX * 0.5f), 0, NX - 1);
        var z = Math.Clamp((int)MathF.Floor(p.Z / H + NZ * 0.5f), 0, NZ - 1);
        return z * NX + x;
    }

    // 풍속 5 m/s 초과 시 표면 눈 일부를 풍하 셀로 이송(도약 표류)
    private void DriftPatch(float dt)
    {
        var w = new Vector2(WindAtHead.X, WindAtHead.Z);
        var sp = w.Length();
        if (sp < 5f) return;
        var frac = MathF.Min(0.2f, 0.002f * (sp - 5f) * dt);
        var dir = w / sp;
        int sx = MathF.Abs(dir.X) >= MathF.Abs(dir.Y) ? (dir.X > 0 ? 1 : -1) : 0;
        int sz = sx == 0 ? (dir.Y > 0 ? 1 : -1) : 0;
        Array.Clear(_tmp);
        for (var j = 0; j < Patch; j++)
            for (var i = 0; i < Patch; i++)
            {
                var n = j * Patch + i;
                var ti = i + sx; var tj = j + sz;
                if ((uint)ti >= Patch || (uint)tj >= Patch) { _tmp[n] += _patch[n]; continue; }   // 경계: 이송 없음(보존)
                var m = _patch[n] * frac;
                _tmp[n] += _patch[n] - m;
                _tmp[tj * Patch + ti] += m;
            }
        _tmp.AsSpan().CopyTo(_patch);
    }

    /// <summary>드루커-프라거 안식각: 인접 표면 경사 &gt; tan φ 이면 초과분의 절반을 아래로(질량 보존).</summary>
    public void RelaxPatch()
    {
        var lim = ReposeTan * PatchCell;
        for (var pass = 0; pass < 64; pass++)
        {
            var moved = false;
            for (var j = 0; j < Patch; j++)
                for (var i = 0; i < Patch; i++)
                {
                    var n = j * Patch + i;
                    for (var k = 0; k < 4; k++)
                    {
                        int ni = i + (k == 0 ? 1 : k == 1 ? -1 : 0), nj = j + (k == 2 ? 1 : k == 3 ? -1 : 0);
                        if ((uint)ni >= Patch || (uint)nj >= Patch) continue;
                        var m = nj * Patch + ni;
                        var diff = (_rock[n] + _patch[n]) - (_rock[m] + _patch[m]);
                        if (diff <= lim) continue;
                        var mv = MathF.Min(_patch[n], 0.5f * (diff - lim));
                        if (mv <= 1e-7f) continue;
                        _patch[n] -= mv; _patch[m] += mv;
                        moved = true;
                        Avalanches++;
                    }
                }
            if (!moved) break;
        }
        var slope = 0f;
        double vol = 0;
        for (var j = 0; j < Patch; j++)
            for (var i = 0; i < Patch; i++)
            {
                var n = j * Patch + i;
                vol += _patch[n];
                if (_patch[n] <= 1e-4f) continue;
                if (i + 1 < Patch && _patch[n + 1] > 1e-4f) slope = MathF.Max(slope, MathF.Abs((_rock[n] + _patch[n]) - (_rock[n + 1] + _patch[n + 1])) / PatchCell);
                if (j + 1 < Patch && _patch[n + Patch] > 1e-4f) slope = MathF.Max(slope, MathF.Abs((_rock[n] + _patch[n]) - (_rock[n + Patch] + _patch[n + Patch])) / PatchCell);
            }
        PatchMaxSlope = slope;
        PatchSnowM3 = (float)(vol * PatchCell * PatchCell);
    }

    private float Rand()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
        return (_rng >> 8) * (1f / 16777216f);
    }
}
