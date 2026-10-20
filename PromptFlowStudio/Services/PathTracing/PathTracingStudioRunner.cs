using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Neural3d;
using PromptFlowStudio.Services.OpenUsd;

namespace PromptFlowStudio.Services.PathTracing;

/// <summary>
/// [PromptFlow Studio v16.74.0] ✨ AI 신경망 패스 트레이싱 &amp; 디노이저 스튜디오 러너(20Hz · <see cref="TickOnce"/> 무할당).
/// 장면 = USDA 스튜디오(금/옥/피부 구 · 발광 램프 · 배경 · 받침) + MaterialX 재질 + 3DGS 연기 구름(이질 매질) + 높이 제한 안개.
/// 틱: (궤도 카메라) → 패스 트레이스(SPP) → 시공간 디노이즈/업스케일 → 8프레임마다 정적 누적 참조 대비 PSNR(노이즈/디노이즈).
/// </summary>
public sealed class PathTracingStudioRunner : IDisposable
{
    public const int Hz = 20;

    public const string StudioUsda = """
#usda 1.0
(
    defaultPrim = "Studio"
    upAxis = "Y"
)

def Xform "Studio"
{
    def Sphere "Gold"
    {
        double radius = 0.6
        color3f[] primvars:displayColor = [(1, 0.78, 0.34)]
        double3 xformOp:translate = (-1.5, 0.6, 0)
    }
    def Sphere "Jade"
    {
        double radius = 0.6
        color3f[] primvars:displayColor = [(0.35, 0.7, 0.45)]
        double3 xformOp:translate = (0, 0.6, 0.3)
    }
    def Sphere "Skin"
    {
        double radius = 0.6
        color3f[] primvars:displayColor = [(0.8, 0.55, 0.45)]
        double3 xformOp:translate = (1.5, 0.6, 0)
    }
    def Sphere "Lamp"
    {
        double radius = 0.25
        color3f[] primvars:displayColor = [(1, 0.9, 0.7)]
        double3 xformOp:translate = (-2.4, 2.1, -1.2)
    }
    def Cube "Backdrop"
    {
        double size = 6
        color3f[] primvars:displayColor = [(0.7, 0.7, 0.72)]
        double3 xformOp:translate = (0, 3, -5)
    }
    def Cube "Plinth"
    {
        double size = 0.5
        color3f[] primvars:displayColor = [(0.2, 0.2, 0.22)]
        double3 xformOp:translate = (2.6, 0.25, -1.2)
    }
}
""";

    public const string StudioMtlx = """
<?xml version="1.0" encoding="UTF-8"?>
<materialx version="1.38">
  <standard_surface name="SR_Gold" type="surfaceshader">
    <input name="base_color" type="color3" value="1, 0.78, 0.34" />
    <input name="metalness" type="float" value="1" />
    <input name="specular_roughness" type="float" value="0.25" />
  </standard_surface>
  <surfacematerial name="Gold" type="material"><input name="surfaceshader" type="surfaceshader" nodename="SR_Gold" /></surfacematerial>
  <standard_surface name="SR_Jade" type="surfaceshader">
    <input name="base_color" type="color3" value="0.35, 0.7, 0.45" />
    <input name="specular_roughness" type="float" value="0.15" />
    <input name="specular_IOR" type="float" value="1.6" />
    <input name="subsurface" type="float" value="0.8" />
    <input name="subsurface_color" type="color3" value="0.45, 0.9, 0.55" />
    <input name="subsurface_radius" type="color3" value="0.2, 0.2, 0.2" />
  </standard_surface>
  <surfacematerial name="Jade" type="material"><input name="surfaceshader" type="surfaceshader" nodename="SR_Jade" /></surfacematerial>
  <standard_surface name="SR_Skin" type="surfaceshader">
    <input name="base_color" type="color3" value="0.8, 0.55, 0.45" />
    <input name="specular" type="float" value="0.6" />
    <input name="specular_roughness" type="float" value="0.45" />
    <input name="specular_IOR" type="float" value="1.4" />
    <input name="subsurface" type="float" value="0.7" />
    <input name="subsurface_color" type="color3" value="0.95, 0.6, 0.45" />
    <input name="subsurface_radius" type="color3" value="0.05, 0.05, 0.05" />
  </standard_surface>
  <surfacematerial name="Skin" type="material"><input name="surfaceshader" type="surfaceshader" nodename="SR_Skin" /></surfacematerial>
  <standard_surface name="SR_Lamp" type="surfaceshader">
    <input name="base_color" type="color3" value="0.1, 0.1, 0.1" />
    <input name="emission" type="float" value="12" />
    <input name="emission_color" type="color3" value="1, 0.85, 0.6" />
  </standard_surface>
  <surfacematerial name="Lamp" type="material"><input name="surfaceshader" type="surfaceshader" nodename="SR_Lamp" /></surfacematerial>
  <UsdPreviewSurface name="SR_Backdrop" type="surfaceshader">
    <input name="diffuseColor" type="color3" value="0.7, 0.7, 0.72" />
    <input name="roughness" type="float" value="0.8" />
  </UsdPreviewSurface>
  <surfacematerial name="Backdrop" type="material"><input name="surfaceshader" type="surfaceshader" nodename="SR_Backdrop" /></surfacematerial>
  <standard_surface name="SR_Floor" type="surfaceshader">
    <input name="base_color" type="color3" value="0.45, 0.45, 0.47" />
    <input name="specular_roughness" type="float" value="0.6" />
  </standard_surface>
  <surfacematerial name="Floor" type="material"><input name="surfaceshader" type="surfaceshader" nodename="SR_Floor" /></surfacematerial>
</materialx>
""";

    private readonly object _gate = new();
    private readonly MaterialXBsdfShaderSynthesizer _mtl;
    private readonly VolumetricPathTracingLightTransportEngine _pt;
    private readonly AiRealtimeSpatiotemporalDenoiser _dn;
    private readonly OpenUsdSpatialAssetCompositorEngine _usd = new();
    private readonly UsdLayer _studio = new("studio.usda");
    private readonly GaussianScene _smoke = new(512);
    private CancellationTokenSource? _cts;
    private long _frame;
    private float _orbitAngle, _psnrNoisy, _psnrDenoised, _sunElevation = 50f, _smokeDensity = 1f;
    private bool _orbit;

    public PathTracingStudioRunner(IServiceProvider sp)
        : this((MaterialXBsdfShaderSynthesizer?)sp.GetService(typeof(MaterialXBsdfShaderSynthesizer)) ?? new MaterialXBsdfShaderSynthesizer(),
               (AiRealtimeSpatiotemporalDenoiser?)sp.GetService(typeof(AiRealtimeSpatiotemporalDenoiser)) ?? new AiRealtimeSpatiotemporalDenoiser()) { }

    public PathTracingStudioRunner(MaterialXBsdfShaderSynthesizer materials, AiRealtimeSpatiotemporalDenoiser denoiser)
    {
        _mtl = materials; _dn = denoiser;
        _pt = new VolumetricPathTracingLightTransportEngine(_mtl);
        Setup();
    }

    public event Action? OnChange;

    public MaterialXBsdfShaderSynthesizer Materials => _mtl;
    public VolumetricPathTracingLightTransportEngine Tracer => _pt;
    public AiRealtimeSpatiotemporalDenoiser Denoiser => _dn;
    public OpenUsdSpatialAssetCompositorEngine Compositor => _usd;
    public bool IsRunning => _cts is not null;
    public long Frame { get { lock (_gate) return _frame; } }
    public float PsnrNoisy => _psnrNoisy;
    public float PsnrDenoised => _psnrDenoised;
    public string? LastAction { get; private set; }

    public bool Orbit { get { lock (_gate) return _orbit; } set { lock (_gate) _orbit = value; } }

    public int Spp
    {
        get => _pt.Spp;
        set { lock (_gate) { _pt.Spp = Math.Clamp(value, 1, 64); } }
    }

    public int MaxBounces
    {
        get => _pt.MaxBounces;
        set { lock (_gate) { _pt.MaxBounces = Math.Clamp(value, 1, 16); _pt.Invalidate(); } }
    }

    public float DenoiseStrength
    {
        get => _dn.Strength;
        set { lock (_gate) _dn.Strength = Math.Clamp(value, 0f, 1f); }
    }

    public float FogDensity
    {
        get => _pt.FogDensity;
        set { lock (_gate) { _pt.FogDensity = Math.Clamp(value, 0f, 2f); _pt.Invalidate(); } }
    }

    public float FogAlbedo
    {
        get => _pt.FogAlbedo.X;
        set { lock (_gate) { _pt.FogAlbedo = new Vector3(Math.Clamp(value, 0f, 1f)); _pt.Invalidate(); } }
    }

    public float FogAnisotropy
    {
        get => _pt.FogAnisotropy;
        set { lock (_gate) { _pt.FogAnisotropy = Math.Clamp(value, -0.95f, 0.95f); _pt.Invalidate(); } }
    }

    public float SmokeDensity
    {
        get => _smokeDensity;
        set { lock (_gate) { _smokeDensity = Math.Clamp(value, 0f, 5f); _pt.GridDensityScale = _smokeDensity; _pt.Invalidate(); } }
    }

    public float SunElevation
    {
        get => _sunElevation;
        set
        {
            lock (_gate)
            {
                _sunElevation = Math.Clamp(value, 2f, 89f);
                var e = _sunElevation * MathF.PI / 180f;
                _pt.SunDirection = Vector3.Normalize(new Vector3(MathF.Cos(e) * 0.6f, MathF.Sin(e), MathF.Cos(e) * 0.8f));
                _pt.Invalidate();
            }
        }
    }

    public int OutputScale
    {
        get => _dn.Scale;
        set { lock (_gate) _dn.Configure(_pt.Width, _pt.Height, value); }
    }

    private void Setup()
    {
        _mtl.LoadMtlx(StudioMtlx);
        UsdaReader.Parse(Encoding.UTF8.GetBytes(StudioUsda), _studio);
        _usd.SetLayer(0, _studio);
        _pt.BuildFromUsd(_usd);
        _pt.GroundMaterial = Math.Max(0, _mtl.Find("Floor"));
        // 3DGS 연기 구름(결정적)
        ulong s = 0x5A0E1674;
        for (var i = 0; i < 220; i++)
        {
            float R() { s ^= s << 13; s ^= s >> 7; s ^= s << 17; return (s >> 40) * (1f / (1 << 24)); }
            var p = new Vector3(-0.3f + (R() - 0.5f) * 1.6f, 1.7f + (R() - 0.5f) * 0.7f, -1.4f + (R() - 0.5f) * 1.0f);
            _smoke.Add(p, Quaternion.Identity, new Vector3(0.12f + 0.1f * R()), 0.5f, new Vector3(0.82f, 0.82f, 0.86f));
        }
        _pt.BindGaussians(_smoke, 4f);
        _pt.SunRadiance = new Vector3(1.1f, 1.05f, 0.95f);
        _pt.SkyZenith = new Vector3(0.16f, 0.26f, 0.48f);
        _pt.SkyHorizon = new Vector3(0.36f, 0.38f, 0.42f);
        _dn.Exposure = 1.6f;
        _pt.FogDensity = 0.04f;
        _pt.FogMax = new Vector3(40, 1.2f, 40);
        _pt.SetResolution(160, 90);
        _dn.Configure(160, 90, 2);
        SunElevation = 50f;
        SetOrbitCamera(0);
        LastAction = "✨ 스튜디오 장면 · 프리미티브 " + (_pt.SphereCount + _pt.BoxCount).ToString(System.Globalization.CultureInfo.InvariantCulture) + " · 재질 " + _mtl.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private void SetOrbitCamera(float a) => _pt.SetCamera(new Vector3(5.6f * MathF.Sin(a), 1.9f, 5.6f * MathF.Cos(a)), new Vector3(0, 0.8f, -0.3f), 42f);

    /// <summary>1프레임: 렌더 → 디노이즈 → (주기적) PSNR. 무할당.</summary>
    public void TickOnce()
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("PathTrace.Runner.Tick");
            if (_orbit) { _orbitAngle += 0.02f; SetOrbitCamera(_orbitAngle); }
            _pt.RenderFrame(_frame);
            _dn.Denoise(_pt.Color, _pt.Albedo, _pt.Normal, _pt.Depth, _pt.MotionX, _pt.MotionY);
            if (_frame % 8 == 7 && _pt.AccumulatedFrames >= 8)
            {
                _psnrNoisy = (float)AiRealtimeSpatiotemporalDenoiser.Psnr(_pt.Color, _pt.Accumulated);
                _psnrDenoised = (float)AiRealtimeSpatiotemporalDenoiser.Psnr(_dn.Output, _pt.Accumulated);
            }
            _frame++;
        }
    }

    // ══════════════════════ 재질(제어 평면) ══════════════════════

    public void SetMaterial(int i, in PbrMaterial m)
    {
        lock (_gate) { _mtl.Set(i, m); _pt.Invalidate(); }
        LastAction = "🎨 재질 갱신 · " + _mtl.Name(i);
        Raise();
    }

    public bool ApplyPrompt(int i, string prompt)
    {
        var s = MaterialXBsdfShaderSynthesizer.SuggestFromPrompt(prompt);
        if (s is null) { LastAction = "⚠ 프리셋 키워드 없음"; Raise(); return false; }
        SetMaterial(i, s.Value.Material);
        LastAction = "🧠 프롬프트 → " + s.Value.Name + " · " + _mtl.Name(i);
        Raise();
        return true;
    }

    public string ExportMtlx()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", "materials");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "studio_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".mtlx");
        File.WriteAllText(path, _mtl.ExportMtlx(), new UTF8Encoding(false));
        LastAction = "💾 MaterialX 내보내기 · " + Path.GetFileName(path);
        Raise();
        return path;
    }

    /// <summary>미리보기 BMP(0 노이즈 · 1 디노이즈(출력 해상도) · 2 알베도 · 3 법선 · 4 깊이 · 5 누적 참조 · 6 모션 벡터). 제어 평면.</summary>
    public byte[] SnapshotBmp(int channel)
    {
        lock (_gate)
        {
            int w = channel == 1 ? _dn.OutputWidth : _pt.Width, h = channel == 1 ? _dn.OutputHeight : _pt.Height;
            var rgba = new byte[w * h * 4];
            var n = w * h;
            switch (channel)
            {
                case 1: _dn.Rgba.CopyTo(rgba); break;
                case 0: case 5:
                    AiRealtimeSpatiotemporalDenoiser.ToneMap(channel == 0 ? _pt.Color : _pt.Accumulated, rgba, _dn.Exposure, new float[n * 3]);
                    break;
                case 2: case 3:
                    var src = channel == 2 ? _pt.Albedo : _pt.Normal;
                    for (var p = 0; p < n; p++)
                        for (var c = 0; c < 3; c++)
                            rgba[p * 4 + c] = (byte)Math.Clamp((channel == 3 ? src[p * 3 + c] * 0.5f + 0.5f : MathF.Sqrt(Math.Clamp(src[p * 3 + c], 0f, 1f))) * 255f, 0, 255);
                    break;
                case 4:
                    var maxD = 1f;
                    foreach (var d in _pt.Depth) if (d > maxD) maxD = d;
                    for (var p = 0; p < n; p++) { var v = _pt.Depth[p] <= 0 ? (byte)0 : (byte)(255 * (1 - _pt.Depth[p] / maxD * 0.85f)); rgba[p * 4] = rgba[p * 4 + 1] = rgba[p * 4 + 2] = v; }
                    break;
                default:
                    for (var p = 0; p < n; p++)
                    {
                        rgba[p * 4] = (byte)Math.Clamp(128 + _pt.MotionX[p] * 16, 0, 255);
                        rgba[p * 4 + 1] = (byte)Math.Clamp(128 + _pt.MotionY[p] * 16, 0, 255);
                        rgba[p * 4 + 2] = 128;
                    }
                    break;
            }
            for (var p = 0; p < n; p++) rgba[p * 4 + 3] = 255;
            var bmp = new byte[54 + n * 4];
            bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 54);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), w);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), -h);
            BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26), 1);
            BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28), 32);
            BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(34), n * 4);
            for (var p = 0; p < n; p++)
            {
                bmp[54 + p * 4] = rgba[p * 4 + 2]; bmp[55 + p * 4] = rgba[p * 4 + 1]; bmp[56 + p * 4] = rgba[p * 4]; bmp[57 + p * 4] = 255;
            }
            return bmp;
        }
    }

    public void Start()
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(() => LoopAsync(cts.Token));
        LastAction = "▶ 렌더 루프 가동";
        Raise();
    }

    public void Stop()
    {
        var c = Interlocked.Exchange(ref _cts, null);
        if (c is null) return;
        c.Cancel(); c.Dispose();
        LastAction = "⏸ 렌더 루프 정지";
        Raise();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / Hz));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { TickOnce(); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("PathTrace.Tick", ex); }
                if ((Frame & 3) == 0) Raise();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("PathTrace.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
