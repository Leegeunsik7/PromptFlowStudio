using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Neural3d;

namespace PromptFlowStudio.Services.OpenUsd;

/// <summary>
/// [PromptFlow Studio v16.72.0] 🪐 AI OpenUSD &amp; 제너레이티브 월드 스튜디오 러너.
/// 레이어 스택: 슬롯 0 = session(물리 결과 over · 강함) · 1 = hero 자산(USDA 데모 · 시간 샘플 드론 · 강체 구) · 2 = procedural(생성기 자산).
/// 60Hz 루프(<see cref="TickOnce"/> 무할당): 물리 스텝 → 강체 위치 → session 레이어 over → 재합성 → 시간 코드(24fps) 평가 → 3DGS 바인딩(+ 지형 스플랫) → 15프레임마다 음향 메시.
/// </summary>
public sealed class OpenUsdSpatialStudioRunner : IDisposable
{
    public const int Hz = 60, MapSize = 96, MaxSplats = 8192;
    public const int SessionSlot = 0, HeroSlot = 1, ProceduralSlot = 2;

    public const string HeroUsda = """
#usda 1.0
(
    defaultPrim = "World"
    upAxis = "Y"
    metersPerUnit = 1
)

def Xform "World"
{
    def Xform "Hero" (kind = "component")
    {
        double3 xformOp:translate = (0, 0, 0)
        uniform token[] xformOpOrder = ["xformOp:translate"]

        def Sphere "BallA"
        {
            double radius = 0.4
            color3f[] primvars:displayColor = [(0.95, 0.32, 0.2)]
            double3 xformOp:translate = (-0.6, 6, 0)
            float physics:mass = 2
            bool physics:rigidBodyEnabled = true
        }
        def Sphere "BallB"
        {
            double radius = 0.3
            color3f[] primvars:displayColor = [(0.25, 0.6, 0.98)]
            double3 xformOp:translate = (0.5, 8, 0.2)
            float physics:mass = 1
            bool physics:rigidBodyEnabled = true
        }
        def Sphere "BallC"
        {
            double radius = 0.5
            color3f[] primvars:displayColor = [(0.98, 0.85, 0.2)]
            double3 xformOp:translate = (0.1, 10, -0.4)
            float physics:mass = 3
            bool physics:rigidBodyEnabled = true
        }
        def Cube "Pedestal"
        {
            double size = 1.2
            color3f[] primvars:displayColor = [(0.5, 0.5, 0.55)]
            double3 xformOp:translate = (2.5, 0.6, 2)
        }
        def Sphere "Drone"
        {
            double radius = 0.2
            color3f[] primvars:displayColor = [(0.6, 1, 0.7)]
            double3 xformOp:translate.timeSamples = {
                0: (-4, 5, -4),
                48: (4, 6, -4),
                96: (4, 5, 4),
                144: (-4, 6, 4),
                192: (-4, 5, -4),
            }
        }
        def Points "Halo"
        {
            point3f[] points = [(1, 3, 0), (0.71, 3, 0.71), (0, 3, 1), (-0.71, 3, 0.71), (-1, 3, 0), (-0.71, 3, -0.71), (0, 3, -1), (0.71, 3, -0.71)]
            color3f[] primvars:displayColor = [(0.9, 0.7, 1)]
        }
    }
}
""";

    private readonly object _gate = new();
    private readonly OpenUsdSpatialAssetCompositorEngine _usd;
    private readonly GenerativeWorldProceduralEngine _gen;
    private readonly NeuralPhysicsCollisionSimulator _phys;
    private readonly UsdLayer _session = new("session.usda"), _hero = new("hero.usda"), _proc = new("procedural.usda");
    private readonly GaussianScene _splats = new(MaxSplats);
    private readonly int[] _bodyPrim = new int[NeuralPhysicsCollisionSimulator.MaxBodies];
    private readonly int[] _bodySession = new int[NeuralPhysicsCollisionSimulator.MaxBodies];
    private readonly byte[] _baseMap = new byte[MapSize * MapSize * 4];
    private CancellationTokenSource? _cts;
    private long _frame;
    private bool _physicsOn = true, _terrainSplats = true;
    private float _originY;

    public OpenUsdSpatialStudioRunner(IServiceProvider sp)
        : this((OpenUsdSpatialAssetCompositorEngine?)sp.GetService(typeof(OpenUsdSpatialAssetCompositorEngine)) ?? new OpenUsdSpatialAssetCompositorEngine(),
               (GenerativeWorldProceduralEngine?)sp.GetService(typeof(GenerativeWorldProceduralEngine)) ?? new GenerativeWorldProceduralEngine(),
               (NeuralPhysicsCollisionSimulator?)sp.GetService(typeof(NeuralPhysicsCollisionSimulator)) ?? new NeuralPhysicsCollisionSimulator()) { }

    public OpenUsdSpatialStudioRunner(OpenUsdSpatialAssetCompositorEngine usd, GenerativeWorldProceduralEngine gen, NeuralPhysicsCollisionSimulator phys)
    {
        _usd = usd; _gen = gen; _phys = phys;
        Generate("안개 낀 숲과 산맥, 노을 forest mountains at sunset", 0);
    }

    public event Action? OnChange;

    public OpenUsdSpatialAssetCompositorEngine Compositor => _usd;
    public GenerativeWorldProceduralEngine Generator => _gen;
    public NeuralPhysicsCollisionSimulator Physics => _phys;
    public GaussianScene Splats => _splats;
    public bool IsRunning => _cts is not null;
    public long Frame { get { lock (_gate) return _frame; } }
    public float TimeCode => _frame * 24f / Hz;
    public int SplatCount { get { lock (_gate) return _splats.Count; } }
    public int BodyCount { get { lock (_gate) return _phys.BodyCount; } }
    public bool PhysicsOn { get { lock (_gate) return _physicsOn; } set { lock (_gate) _physicsOn = value; } }
    public bool TerrainSplats { get { lock (_gate) return _terrainSplats; } set { lock (_gate) _terrainSplats = value; } }
    public string? LastAction { get; private set; }

    /// <summary>프롬프트 → 월드 재생성 → procedural 레이어 · hero 자산 재장착 · 물리 바디 재구성. 제어 평면.</summary>
    public void Generate(string prompt, ulong scenarioSeed)
    {
        lock (_gate)
        {
            _gen.Configure(prompt, scenarioSeed);
            RebuildLocked();
        }
        LastAction = "🌍 생성 · " + _gen.DominantBiome + " · 시드 " + _gen.Seed.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
        Raise();
    }

    public void SetSpectrum(float s)
    {
        lock (_gate)
        {
            _gen.SetSpectrum(s);
            RebuildLocked();
        }
        LastAction = "🎚 시드 스펙트럼 " + s.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        Raise();
    }

    public void ToggleSeedLock()
    {
        lock (_gate)
        {
            _gen.LockSeed(_gen.SeedLocked ? null : _gen.Seed);
            RebuildLocked();
        }
        LastAction = _gen.LastAction;
        Raise();
    }

    public void MuteLayer(int slot, bool muted)
    {
        lock (_gate) _usd.MuteLayer(slot, muted);
        LastAction = _usd.LastAction;
        Raise();
    }

    /// <summary>장면 중앙 위로 구 투하(물리 바디만 · USD 프림 없음).</summary>
    public void DropBall()
    {
        lock (_gate)
        {
            var k = _phys.BodyCount;
            var a = k * 2.399963f;
            var i = _phys.AddSphere(new Vector3(MathF.Cos(a) * 1.5f, _originY + 9, MathF.Sin(a) * 1.5f), 0.25f, 1f, 0.6f);
            if (i >= 0) { _bodyPrim[i] = -1; _bodySession[i] = -1; }
        }
        LastAction = "⚾ 구 투하 · 바디 " + _phys.BodyCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Raise();
    }

    public void ResetPhysics()
    {
        lock (_gate) RebuildLocked();
        LastAction = "↺ 물리 리셋";
        Raise();
    }

    private void RebuildLocked()
    {
        _gen.EnsureAround(0, 0);
        _gen.EmitUsd(_proc);
        UsdaReader.Parse(Encoding.UTF8.GetBytes(HeroUsda), _hero);
        _originY = MathF.Max(0, _gen.HeightAt(0, 0));
        // session: hero 루트를 지형 위로(override) · 강체 프림은 over 스펙
        _session.Clear();
        var hero = _session.AddPrim(_session.AddPrim(-1, "World", UsdSpec.Over, UsdPrimType.None), "Hero", UsdSpec.Over, UsdPrimType.None);
        _session.SetTranslate(hero, 0, _originY, 0);
        _usd.SetLayer(ProceduralSlot, _proc);
        _usd.SetLayer(HeroSlot, _hero);
        _usd.SetLayer(SessionSlot, _session);

        _phys.ClearBodies();
        _phys.Field = _gen;
        var st = _usd.Stage;
        for (var i = 0; i < st.Count; i++)
        {
            if (st.SpecOf(i) != UsdSpec.Def || st.TypeOf(i) != UsdPrimType.Sphere || !st.IsRigid(i) || st.MassOf(i) <= 0) continue;
            var s = _usd.State(i);
            var b = _phys.AddSphere(new Vector3(s.X, s.Y, s.Z), st.RadiusOf(i) * s.Scale, st.MassOf(i), 0.45f);
            if (b < 0) break;
            _bodyPrim[b] = i;
            _bodySession[b] = _session.AddPath(st.Path(i), UsdSpec.Over, UsdPrimType.None);
        }
        _usd.Compose();
        var gy = MathF.Max(0, _gen.HeightAt(-0.75f, -4f));
        _phys.ResetCloth(new Vector3(-0.75f, gy + 2.5f, -4.5f), 0.1f);
        var fy = MathF.Max(0, _gen.HeightAt(3.3f, -1.5f));
        _phys.ResetFluid(new Vector3(3f, fy, -1.8f), new Vector3(3.6f, fy + 1.2f, -1.2f));
        _phys.Enclosure = null;
        RenderBaseMap();
        _frame = 0;
        Bind();
    }

    /// <summary>1프레임(무할당).</summary>
    public void TickOnce()
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Usd.Runner.Tick");
            if (_physicsOn)
            {
                _phys.Step(1f / Hz);
                // 강체 → session over(부모 월드 역변환 = 로컬)
                for (var b = 0; b < _phys.BodyCount; b++)
                {
                    var prim = _bodyPrim[b]; var sp = _bodySession[b];
                    if (prim < 0 || sp < 0) continue;
                    var p = _phys.BodyPosition(b);
                    var parent = _usd.Stage.ParentOf(prim);
                    if (parent >= 0)
                    {
                        var ps = _usd.State(parent);
                        var inv = ps.Scale != 0 ? 1f / ps.Scale : 1f;
                        _session.SetTranslate(sp, (p.X - ps.X) * inv, (p.Y - ps.Y) * inv, (p.Z - ps.Z) * inv);
                    }
                    else _session.SetTranslate(sp, p.X, p.Y, p.Z);
                }
                _usd.Compose();
            }
            _frame++;
            _usd.Evaluate(TimeCode % 192f);
            Bind();
            if (_frame % 15 == 0)
            {
                var src = _phys.BodyCount > 0 ? _phys.BodyPosition(0) : new Vector3(0, _originY + 1, 0);
                _phys.BuildAcousticMesh(src, new Vector3(0, _originY + 1.7f, 6f));
            }
        }
    }

    private void Bind()
    {
        _usd.BindToGaussians(_splats);
        if (_terrainSplats) _gen.EmitGaussians(_splats, 4, append: true);
        // 물리 전용 바디(투하 구) · 천 · 유체 스플랫
        for (var b = 0; b < _phys.BodyCount && _splats.Count < MaxSplats; b++)
            if (_bodyPrim[b] < 0) _splats.Add(_phys.BodyPosition(b), Quaternion.Identity, new Vector3(_phys.BodyRadius(b) * 0.5f), 0.9f, new Vector3(1f, 0.55f, 0.1f));
        if (_phys.ClothEnabled)
            for (var k = 0; k < NeuralPhysicsCollisionSimulator.ClothCount && _splats.Count < MaxSplats; k += 2)
                _splats.Add(_phys.ClothPoint(k), Quaternion.Identity, new Vector3(0.05f), 0.8f, new Vector3(0.95f, 0.95f, 1f));
        if (_phys.FluidEnabled)
            for (var k = 0; k < NeuralPhysicsCollisionSimulator.FluidCount && _splats.Count < MaxSplats; k++)
                _splats.Add(_phys.FluidPoint(k), Quaternion.Identity, new Vector3(0.03f), 0.7f, new Vector3(0.2f, 0.55f, 1f));
    }

    private void RenderBaseMap()
    {
        const float half = MapSize / 2f;
        var sun = _gen.Light.SunDir;
        for (var y = 0; y < MapSize; y++)
            for (var x = 0; x < MapSize; x++)
            {
                float wx = x - half, wz = y - half;
                var h = _gen.HeightAt(wx, wz);
                var c = GenerativeWorldProceduralEngine.BiomeColor(_gen.BiomeAt(wx, wz));
                var shade = h < 0 ? 0.8f + 0.2f * Math.Clamp(1 + h / 6f, 0f, 1f) : Math.Clamp(0.55f + 0.6f * Vector3.Dot(_gen.NormalAt(wx, wz), sun), 0.35f, 1.15f);
                var o = (y * MapSize + x) * 4;
                _baseMap[o] = (byte)Math.Clamp(c.Z * shade * 255, 0, 255);
                _baseMap[o + 1] = (byte)Math.Clamp(c.Y * shade * 255, 0, 255);
                _baseMap[o + 2] = (byte)Math.Clamp(c.X * shade * 255, 0, 255);
                _baseMap[o + 3] = 255;
            }
    }

    /// <summary>UI 탑다운 맵: 바이옴/음영 + 강체(주황) · 천(흰) · 유체(파랑) · 시간샘플 프림(초록). 32bpp 하향식 BMP. 제어 평면.</summary>
    public byte[] SnapshotBmp()
    {
        const int N = MapSize * MapSize * 4;
        var bmp = new byte[54 + N];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), MapSize);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), -MapSize);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28), 32);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(34), N);
        lock (_gate)
        {
            _baseMap.CopyTo(bmp, 54);
            var px = bmp.AsSpan(54);
            for (var k = 0; k < NeuralPhysicsCollisionSimulator.ClothCount; k += 3) Dot(px, _phys.ClothPoint(k), 0, 255, 255, 255);
            for (var k = 0; k < NeuralPhysicsCollisionSimulator.FluidCount; k += 2) Dot(px, _phys.FluidPoint(k), 0, 40, 140, 255);
            for (var b = 0; b < _phys.BodyCount; b++) Dot(px, _phys.BodyPosition(b), 1, 255, 120, 20);
            var st = _usd.Stage;
            for (var i = 0; i < st.Count; i++)
                if (st.SampleCount(i) > 0) { var s = _usd.State(i); Dot(px, new Vector3(s.X, s.Y, s.Z), 1, 90, 255, 140); }
        }
        return bmp;
    }

    private static void Dot(Span<byte> px, Vector3 p, int r, byte cr, byte cg, byte cb)
    {
        var cx = (int)MathF.Round(p.X + MapSize / 2f); var cy = (int)MathF.Round(p.Z + MapSize / 2f);
        for (var y = cy - r; y <= cy + r; y++)
            for (var x = cx - r; x <= cx + r; x++)
            {
                if ((uint)x >= MapSize || (uint)y >= MapSize) continue;
                var o = (y * MapSize + x) * 4;
                px[o] = cb; px[o + 1] = cg; px[o + 2] = cr;
            }
    }

    /// <summary>합성 스테이지 → USDA + USDZ 파일 내보내기(%LOCALAPPDATA%\PromptFlowStudio\usd). 반환 = USDZ 경로.</summary>
    public string Export()
    {
        byte[] usda;
        int n;
        lock (_gate)
        {
            usda = new byte[1 << 20];
            n = OpenUsdSpatialAssetCompositorEngine.WriteUsda(_usd.Stage, usda);
        }
        if (n < 0) throw new InvalidOperationException("USDA 버퍼 초과");
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", "usd");
        Directory.CreateDirectory(dir);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllBytes(Path.Combine(dir, "world_" + stamp + ".usda"), usda.AsSpan(0, n).ToArray());
        var zip = new byte[n + 512];
        var z = OpenUsdSpatialAssetCompositorEngine.WriteUsdz("world.usda"u8, usda.AsSpan(0, n), zip);
        var path = Path.Combine(dir, "world_" + stamp + ".usdz");
        File.WriteAllBytes(path, zip.AsSpan(0, z).ToArray());
        LastAction = "💾 내보내기 · " + Path.GetFileName(path);
        Raise();
        return path;
    }

    public void Start()
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(() => LoopAsync(cts.Token));
        LastAction = "▶ 시뮬레이션 가동";
        Raise();
    }

    public void Stop()
    {
        var c = Interlocked.Exchange(ref _cts, null);
        if (c is null) return;
        c.Cancel(); c.Dispose();
        LastAction = "⏸ 시뮬레이션 정지";
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
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("Usd.Tick", ex); }
                if ((Frame & 7) == 0) Raise();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Usd.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
