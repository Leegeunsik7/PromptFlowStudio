using System.Numerics;
using PromptFlowStudio.Services.AnatomicalTissue;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Garment;
using PromptFlowStudio.Services.HoloTelepresence;
using PromptFlowStudio.Services.SkinOptics;

namespace PromptFlowStudio.Services.FluidWetness;

/// <summary>
/// [PromptFlow Studio v17.19.0] 💧 AI 다상 유체 습윤 &amp; 액적 스튜디오 러너(60 Hz 전용 스레드 · 결정적).
/// 날씨 시나리오(맑음 → 소나기 → 폭우 → 건조) 또는 수동 비/땀 + v17.00 디렉터 정서 → v17.17 혈류(심박 · 분노 · 긴장) → 땀 분비율 →
/// 가드 사전(모발 수분 · 모세관 인력 · 의상 감쇠/굽힘) → v17.16 의상 + v17.15 모발 스텝 → 가드 사후(점성 흡착 · 이마/목 흡착) →
/// 피부 피막/직물 위킹 → 액적 활주 → 의상 젖음 암전 · 계측 · 히트맵.
/// </summary>
public sealed class FluidWetnessStudioRunner : IDisposable
{
    public const int Hz = 60, SeriesLen = 240, W = MultiphaseSurfaceWetnessCapillaryEngine.W, H = MultiphaseSurfaceWetnessCapillaryEngine.H, AtlasW = 160, AtlasH = 120;
    public const float Dt = 1f / Hz, Cycle = 40f;

    private readonly object _gate = new();
    private readonly float[] _msS = new float[SeriesLen], _skinS = new float[SeriesLen], _massS = new float[SeriesLen], _dropS = new float[SeriesLen], _hairS = new float[SeriesLen], _flutS = new float[SeriesLen];
    private readonly byte[] _bmpMap = new byte[54 + W * H * 3];
    private readonly byte[] _bmpAtlas = new byte[54 + AtlasW * AtlasH * 3];
    private readonly float[] _atlas = new float[AtlasW * AtlasH], _atlasN = new float[AtlasW * AtlasH];
    private readonly float[] _shade;
    private int _seriesHead, _seriesCount;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;
    private float _time;

    public FluidWetnessStudioRunner(IServiceProvider sp) : this() { }

    public FluidWetnessStudioRunner()
    {
        Garment = new GarmentDynamicsStudioRunner(new NeuralAnatomicalMuscleFasciaEngine(), 48);
        Skin = new SeparableNeuralSkinSssEngine();
        Vascular = new MicroVascularHemodynamicsFlushingSynthesizer(Skin);
        Director = new UniversalAgiCinematicDirectorSynthesizer();
        Wet = new MultiphaseSurfaceWetnessCapillaryEngine(Skin, Garment.Engine, Garment.Draping);
        Drops = new MicroDropletSlidingAdhesionSynthesizer(Skin, Wet);
        Guard = new WetFiberTensionGarmentAdherenceGuard(Garment.Hair, Garment.HairGuard, Garment.Engine, Garment.Guard, Garment.Draping, Wet);
        _shade = new float[Garment.Engine.P];
        _shade.AsSpan().Fill(1f);
        Garment.ShadeScale = _shade;
        LastAction = "📋 대기 · 피부 피막 " + W + "² · 직물 입자 " + Garment.Engine.P + " · 모발 가이드 " + Garment.Hair.G + " · 액적 ≤ " + MicroDropletSlidingAdhesionSynthesizer.MaxDrops + " · " + Wet.Path;
    }

    public event Action? OnChange;

    public GarmentDynamicsStudioRunner Garment { get; }
    public SeparableNeuralSkinSssEngine Skin { get; }
    public MicroVascularHemodynamicsFlushingSynthesizer Vascular { get; }
    public UniversalAgiCinematicDirectorSynthesizer Director { get; }
    public MultiphaseSurfaceWetnessCapillaryEngine Wet { get; }
    public MicroDropletSlidingAdhesionSynthesizer Drops { get; }
    public WetFiberTensionGarmentAdherenceGuard Guard { get; }
    public bool Scenario { get; set; } = true;
    /// <summary>수동 강우(mm/h · 0..100).</summary>
    public float RainMmH { get; set; }
    /// <summary>수동 땀 강도(0..1 → 최대 3 µm/s · 심박 가중).</summary>
    public float SweatIntensity { get; set; } = 0.2f;
    public float Humidity { get; set; } = 0.6f;
    public float Temperature { get; set; } = 22f;
    /// <summary>0 = 피부 수분 히트맵 · 1 = 젖은 피부 미리보기 · 2 = 노출/땀샘 분포.</summary>
    public int MapView { get; set; }
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public float Time => _time;
    public string Weather { get; private set; } = "맑음";
    public float CurrentRain { get; private set; }
    public float CurrentSweat { get; private set; }
    public float TearDrive { get; private set; }
    public int SeriesCount => _seriesCount;
    private int Idx(int i) => (_seriesHead - 1 - i + SeriesLen * 2) % SeriesLen;
    public float TickMsSeries(int i) => _msS[Idx(i)];
    public float SkinSeries(int i) => _skinS[Idx(i)];
    public float MassSeries(int i) => _massS[Idx(i)];
    public float DropSeries(int i) => _dropS[Idx(i)];
    public float HairSeries(int i) => _hairS[Idx(i)];
    public float FlutterSeries(int i) => _flutS[Idx(i)];

    private static string F1(float v) => v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
    private static string F2(float v) => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    public void SetRain(float mmH) { lock (_gate) { Scenario = false; RainMmH = Math.Clamp(mmH, 0f, 100f); } LastAction = "🌧 강우 " + F1(RainMmH) + " mm/h(수동)"; Raise(); }
    public void SetSweat(float k) { lock (_gate) { Scenario = false; SweatIntensity = Math.Clamp(k, 0f, 1f); } LastAction = "💦 땀 강도 " + F2(SweatIntensity); Raise(); }
    public void SetScenario(bool on) { lock (_gate) Scenario = on; LastAction = on ? "🎬 날씨 시나리오(맑음 → 소나기 → 폭우 → 건조) " + F1(Cycle) + " s 주기" : "🎚 수동 비/땀"; Raise(); }
    public void SetHumidity(float h) { lock (_gate) Humidity = Math.Clamp(h, 0f, 1f); LastAction = "🌫 습도 " + F2(Humidity); Raise(); }
    public void SetTemperature(float c) { lock (_gate) Temperature = Math.Clamp(c, -10f, 45f); LastAction = "🌡 기온 " + F1(Temperature) + " °C"; Raise(); }
    public void SetTears(bool on) { lock (_gate) Drops.Tears = on; LastAction = on ? "😢 눈물(내안각 · 활주)" : "눈물 OFF"; Raise(); }
    public void SetHairCapillary(bool on) { lock (_gate) Guard.HairCapillary = on; LastAction = on ? "💇 모세관 클러스터링 ON" : "💇 모세관 인력 OFF(건조 거동 비교)"; Raise(); }
    public void SetHairAdhesion(bool on) { lock (_gate) Guard.HairAdhesion = on; LastAction = on ? "🧲 이마/목 모발 흡착 ON" : "🧲 모발 흡착 OFF"; Raise(); }
    public void SetGarmentAdhesion(bool on) { lock (_gate) Guard.GarmentAdhesion = on; LastAction = on ? "🧥 젖은 의상 점성 흡착 ON" : "🧥 점성 흡착 OFF — 펄럭임 노출"; Raise(); }
    public void SetFabric(GarmentLayer layer, FabricPreset preset)
    {
        lock (_gate) { Garment.SetFabric(layer, preset); Wet.RefreshFabrics(); Guard.InvalidateLayer((int)layer); }
        LastAction = "🧵 " + GarmentDynamicsStudioRunner.LayerName(layer) + " → " + AnisotropicFabricDrapingWrinkleSynthesizer.Presets[(int)preset].Name + " · 흡수 용량 " + F1(MultiphaseSurfaceWetnessCapillaryEngine.Capacity[(int)preset]) + "× 건조 중량";
        Raise();
    }
    public void Dry() { lock (_gate) { Wet.ResetAll(); Drops.Clear(); Guard.ResetHair(); } LastAction = "🔥 전체 건조(피막 · 직물 · 액적 · 모발 초기화)"; Raise(); }

    // ── 1 틱 ────────────────────────────────────────────────

    /// <summary>1틱(무할당).</summary>
    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("FluidWetness.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _time += Dt;
            float rain, sweatK;
            if (Scenario)
            {
                var ph = _time % Cycle;
                if (ph < 8f) { rain = 0f; sweatK = 0.35f; Weather = "맑음 · 운동 땀"; }
                else if (ph < 18f) { rain = 12f; sweatK = 0.2f; Weather = "소나기"; }
                else if (ph < 28f) { rain = 60f; sweatK = 0.1f; Weather = "폭우"; }
                else { rain = 0f; sweatK = 0.05f; Weather = "건조"; }
            }
            else { rain = RainMmH; sweatK = SweatIntensity; Weather = rain > 30f ? "폭우" : rain > 0.5f ? "비" : "맑음"; }
            Director.Tick(Dt);
            Vascular.SetEmotion(Director.Valence, Director.Arousal, Director.Tension, ReadOnlySpan<float>.Empty);
            Vascular.Update(Dt);
            var heat = Math.Clamp((Temperature - 18f) / 15f, 0f, 1.5f);
            var cardio = Math.Clamp((Vascular.Bpm - 60f) / 60f, 0f, 1f);
            var sweat = sweatK * 3f * (0.4f + 0.6f * heat + 0.5f * cardio) + 0.3f * (Vascular.Anger + Vascular.Tension) * sweatK;
            CurrentRain = rain; CurrentSweat = sweat;
            TearDrive = MathF.Max(0f, -Director.Valence - 0.5f);
            Wet.SetEnvironment(rain, Humidity, Temperature, Vector3.Zero, sweat);
            Guard.PreHair(rain, sweat, Humidity, Dt);
            Guard.PreGarment();
            Garment.Step();
            Guard.PostGarment();
            Guard.PostHair();
            Wet.Update(Dt);
            Drops.Update(Dt, rain, TearDrive);
            Wet.FillGarmentShade(_shade);
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            var h = _seriesHead;
            _msS[h] = ms; _skinS[h] = Wet.SkinMeanUm; _massS[h] = Wet.LayerMassRatio(2); _dropS[h] = Drops.Count; _hairS[h] = Guard.HairWetMean; _flutS[h] = Guard.FlutterMm;
            _seriesHead = (h + 1) % SeriesLen;
            if (_seriesCount < SeriesLen) _seriesCount++;
            Interlocked.Increment(ref _tick);
            LastTickMs = ms;
        }
    }

    // ── 모니터(BMP) ─────────────────────────────────────────

    private static byte B8(float v) => (byte)Math.Clamp((int)(MathF.Pow(Math.Clamp(v, 0f, 1f), 1f / 2.2f) * 255f + 0.5f), 0, 255);

    private static void Header(byte[] bmp, int w, int h)
    {
        var d = bmp.AsSpan();
        d.Slice(0, 54).Clear();
        d[0] = (byte)'B'; d[1] = (byte)'M';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(2), bmp.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(10), 54);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(14), 40);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(18), w);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(22), -h);
        d[26] = 1; d[28] = 24;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(34), bmp.Length - 54);
    }

    /// <summary>수분 컬러맵(건조 = 짙은 남색 → 청록 → 백청).</summary>
    private static void Aqua(float t, out float r, out float g, out float b)
    {
        t = Math.Clamp(t, 0f, 1f);
        r = 0.04f + 0.8f * t * t; g = 0.06f + 0.9f * MathF.Sqrt(t) * 0.8f + 0.1f * t; b = 0.18f + 0.82f * MathF.Sqrt(t);
    }

    private void Put(byte[] bmp, int p, float r, float g, float b)
    {
        var o = 54 + p * 3;
        bmp[o] = B8(b); bmp[o + 1] = B8(g); bmp[o + 2] = B8(r);
    }

    /// <summary>피부 수분 맵 BMP(<see cref="MapView"/> · 액적 · 자취 오버레이).</summary>
    public string MapBase64()
    {
        lock (_gate)
        {
            Header(_bmpMap, W, H);
            var s = Skin;
            for (var i = 0; i < SeparableNeuralSkinSssEngine.N; i++)
            {
                float r, g, b;
                if (s.Mask[i] <= 0f) { r = g = b = 0.02f; Put(_bmpMap, i, r, g, b); continue; }
                switch (MapView)
                {
                    case 1:
                        var wv = Wet.SkinWetness(i);
                        r = MultiphaseSurfaceWetnessCapillaryEngine.WetAlbedo(s.AlbedoR[i], wv); g = MultiphaseSurfaceWetnessCapillaryEngine.WetAlbedo(s.AlbedoG[i], wv); b = MultiphaseSurfaceWetnessCapillaryEngine.WetAlbedo(s.AlbedoB[i], wv);
                        var lit = 0.35f + 0.65f * MathF.Max(0f, s.NZ[i] * 0.8f + s.NY[i] * -0.3f);
                        var spec = MathF.Pow(MathF.Max(0f, s.NZ[i]), 1f / MathF.Max(0.02f, MultiphaseSurfaceWetnessCapillaryEngine.WetRoughness(0.45f, wv))) * 0.35f * wv;
                        r = r * lit + spec; g = g * lit + spec; b = b * lit + spec;
                        break;
                    case 2: r = Wet.Exposure(i); g = 0.15f; b = Wet.Gland(i) * 0.6f; break;
                    default: Aqua(Wet.Film(i) / 40f, out r, out g, out b); break;
                }
                Put(_bmpMap, i, r, g, b);
            }
            for (var k = 0; k < Drops.StreakCount && k < 256; k++)
            {
                Drops.Streak(k, out var x0, out var y0, out _, out _, out var age);
                int x = (int)x0, y = (int)y0;
                if ((uint)x >= W || (uint)y >= H) continue;
                var f = MathF.Max(0.15f, 1f - age / 6f);
                Put(_bmpMap, y * W + x, 0.55f * f, 0.85f * f, 1f * f);
            }
            for (var d = 0; d < MicroDropletSlidingAdhesionSynthesizer.MaxDrops; d++)
            {
                if (!Drops.Alive[d]) continue;
                int x = (int)Drops.U[d], y = (int)Drops.V[d];
                if ((uint)x >= W || (uint)y >= H) continue;
                var kind = Drops.Kind[d];
                float r = kind == DropletKind.Tear ? 1f : 0.9f, g = kind == DropletKind.Sweat ? 0.95f : 1f, b = kind == DropletKind.Sweat ? 0.5f : 1f;
                Put(_bmpMap, y * W + x, r, g, b);
                if (Drops.Vol[d] > 6f && x + 1 < W) Put(_bmpMap, y * W + x + 1, r, g, b);
            }
            return Convert.ToBase64String(_bmpMap);
        }
    }

    /// <summary>직물 포화도 아틀라스 BMP(가로 = 둘레 각 · 세로 = 높이 · 행 3 레이어 겹침 = 최외곽 우선).</summary>
    public string AtlasBase64()
    {
        lock (_gate)
        {
            Array.Clear(_atlas); Array.Clear(_atlasN);
            var e = Garment.Engine;
            float y0 = float.MaxValue, y1 = float.MinValue;
            for (var i = 0; i < e.P; i++) { var y = e.Y[i]; if (y < y0) y0 = y; if (y > y1) y1 = y; }
            var span = MathF.Max(0.1f, y1 - y0);
            var c = Garment.Skin.Center[0];
            for (var i = 0; i < e.P; i++)
            {
                var ang = MathF.Atan2(e.Z[i] - c.Z, e.X[i] - c.X);
                var ax = (int)((ang / (2f * MathF.PI) + 0.5f) * (AtlasW - 1));
                var ay = (int)((1f - (e.Y[i] - y0) / span) * (AtlasH - 1));
                if ((uint)ax >= AtlasW || (uint)ay >= AtlasH) continue;
                var p = ay * AtlasW + ax;
                var wgt = 1f + e.LayerOf[i];
                _atlas[p] += Wet.Saturation(i) * wgt; _atlasN[p] += wgt;
            }
            Header(_bmpAtlas, AtlasW, AtlasH);
            for (var p = 0; p < AtlasW * AtlasH; p++)
            {
                if (_atlasN[p] <= 0f) { Put(_bmpAtlas, p, 0.03f, 0.03f, 0.05f); continue; }
                Aqua(_atlas[p] / _atlasN[p], out var r, out var g, out var b);
                Put(_bmpAtlas, p, r, g, b);
            }
            return Convert.ToBase64String(_bmpAtlas);
        }
    }

    /// <summary>의상 3D 렌더(v17.16 · 젖음 암전 반영).</summary>
    public string GarmentBase64() { lock (_gate) return Garment.RenderBase64(); }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.FluidWetness" };
            _loop.Start();
            LastAction = "▶ 습윤 스튜디오 가동 · 60 Hz · 비/땀 → 피막 확산 · 직물 위킹 → 액적 활주 → 모발 클러스터링 · 의상 흡착";
        }
        Raise();
    }

    public void Stop()
    {
        Thread? t;
        lock (_gate) { if (!_running) return; _running = false; t = _loop; _loop = null; }
        t?.Join(3000);
        LastAction = "⏸ 정지";
        Raise();
    }

    private void Loop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long n = 0;
        while (_running)
        {
            try { Step(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("FluidWetness.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }   // 과부하 시 따라잡기 폭주 방지
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("FluidWetness.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
