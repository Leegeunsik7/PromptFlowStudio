using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Neural3d;
using PromptFlowStudio.Services.OpenUsd;

namespace PromptFlowStudio.Services.GenerativeVfx;

/// <summary>스튜디오 지형(완만한 분지 + 암석 둔덕 · 흡수율 = 흙 0.8 / 암석 0.1).</summary>
public sealed class VfxStudioTerrain : IHeightField
{
    public float HeightAt(float x, float z) => 0.06f * (x * x + z * z) * 0.25f + 0.18f * MathF.Exp(-((x - 0.9f) * (x - 0.9f) + (z + 0.4f) * (z + 0.4f)) * 3f);
    public float AbsorptionAt(float x, float z) => MathF.Exp(-((x - 0.9f) * (x - 0.9f) + (z + 0.4f) * (z + 0.4f)) * 3f) > 0.4f ? 0.1f : 0.8f;
}

/// <summary>
/// [PromptFlow Studio v16.81.0] 💥 AI 제너레이티브 VFX &amp; 파티클 스튜디오 러너(60 Hz · <see cref="TickOnce"/> 무할당).
/// 씬: 분지 지형(OpenUSD 높이장 계약) · 캐릭터 3DGS(가우시안 220 · 프록시 구) · 물줄기 노즐(액체) · 굴뚝 연기/화염 이미터 → 유체 엔진 ↔ 충돌 가드 ↔ 볼륨 합성기 →
/// 유체 입자 3DGS 레이어(<see cref="FluidSplats"/>) 컴포지팅. 폭발 이벤트는 볼륨 + 파편 + 가스 입자 동시 발동.
/// </summary>
public sealed class GenerativeVfxStudioRunner : IDisposable
{
    public const int Hz = 60, DefaultCapacity = 12000, NozzlePerFrame = 40, SplatBudget = 4096;

    private readonly object _gate = new();
    private readonly NeuralParticleFluidDynamicsEngine _fluid;
    private readonly VolumetricFireSmokeAtmosphericSynthesizer _volume;
    private readonly InteractiveVfxCollisionSplashGuard _guard;
    private readonly VfxStudioTerrain _terrain = new();
    private readonly GaussianScene _character = new(256), _fluidSplats = new(SplatBudget);
    private CancellationTokenSource? _cts;
    private long _frame;
    private bool _nozzle = true;
    private Vector3 _charPos = new(-0.3f, 0f, 0.2f);

    public GenerativeVfxStudioRunner(IServiceProvider sp)
        : this((NeuralParticleFluidDynamicsEngine?)sp.GetService(typeof(NeuralParticleFluidDynamicsEngine)) ?? new NeuralParticleFluidDynamicsEngine(DefaultCapacity),
               (VolumetricFireSmokeAtmosphericSynthesizer?)sp.GetService(typeof(VolumetricFireSmokeAtmosphericSynthesizer)) ?? new VolumetricFireSmokeAtmosphericSynthesizer(),
               (InteractiveVfxCollisionSplashGuard?)sp.GetService(typeof(InteractiveVfxCollisionSplashGuard)) ?? new InteractiveVfxCollisionSplashGuard()) { }

    public GenerativeVfxStudioRunner(NeuralParticleFluidDynamicsEngine fluid, VolumetricFireSmokeAtmosphericSynthesizer volume, InteractiveVfxCollisionSplashGuard guard)
    {
        _fluid = fluid; _volume = volume; _guard = guard;
        _fluid.Collider = _guard;
        _fluid.Substeps = 1; // 60 Hz 예산: 강성은 기준 스텝 정규화로 서브스텝 무관
        _guard.Terrain = _terrain;
        _guard.TerrainMin = new Vector2(_fluid.DomainMin.X, _fluid.DomainMin.Z); _guard.TerrainMax = new Vector2(_fluid.DomainMax.X, _fluid.DomainMax.Z);
        BuildCharacter();
        _guard.BindSplatProxies(_character, 48);
        NewShot();
    }

    public event Action? OnChange;

    public NeuralParticleFluidDynamicsEngine Fluid => _fluid;
    public VolumetricFireSmokeAtmosphericSynthesizer Volume => _volume;
    public InteractiveVfxCollisionSplashGuard Guard => _guard;
    public IHeightField Terrain => _terrain;
    public GaussianScene Character => _character;
    public GaussianScene FluidSplats => _fluidSplats;
    public bool IsRunning => _cts is not null;
    public bool NozzleOn => _nozzle;
    public long Frame { get { lock (_gate) return _frame; } }
    public static Vector3 NozzlePosition => new(-1.3f, 2.1f, -0.2f);
    public static Vector3 NozzleDirection => Vector3.Normalize(new Vector3(0.75f, -0.35f, 0.15f));
    public string? LastAction { get; private set; }

    private void BuildCharacter()
    {
        _character.Clear();
        var rnd = new Random(1681);
        void Blob(Vector3 c, Vector3 r, int n, Vector3 rgb)
        {
            for (var k = 0; k < n; k++)
            {
                var d = new Vector3((float)rnd.NextDouble() * 2f - 1f, (float)rnd.NextDouble() * 2f - 1f, (float)rnd.NextDouble() * 2f - 1f);
                if (d.LengthSquared() > 1f) d = Vector3.Normalize(d);
                _character.Add(c + d * r, Quaternion.Identity, new Vector3(0.05f), 0.9f, rgb);
            }
        }
        var gy = _terrain.HeightAt(_charPos.X, _charPos.Z);
        var b = _charPos + new Vector3(0, gy, 0);
        Blob(b + new Vector3(0, 1.55f, 0), new Vector3(0.11f, 0.13f, 0.11f), 30, new Vector3(0.85f, 0.68f, 0.55f));
        Blob(b + new Vector3(0, 1.12f, 0), new Vector3(0.2f, 0.28f, 0.12f), 70, new Vector3(0.2f, 0.32f, 0.65f));
        Blob(b + new Vector3(-0.26f, 1.1f, 0), new Vector3(0.06f, 0.26f, 0.06f), 20, new Vector3(0.2f, 0.32f, 0.65f));
        Blob(b + new Vector3(0.26f, 1.1f, 0), new Vector3(0.06f, 0.26f, 0.06f), 20, new Vector3(0.2f, 0.32f, 0.65f));
        Blob(b + new Vector3(-0.1f, 0.42f, 0), new Vector3(0.08f, 0.42f, 0.08f), 40, new Vector3(0.18f, 0.18f, 0.22f));
        Blob(b + new Vector3(0.1f, 0.42f, 0), new Vector3(0.08f, 0.42f, 0.08f), 40, new Vector3(0.18f, 0.18f, 0.22f));
    }

    /// <summary>제어 평면: 새 샷(유체 · 볼륨 · 젖음 초기화 + 굴뚝 · 화염 상주 이미터).</summary>
    public void NewShot()
    {
        lock (_gate)
        {
            _fluid.Clear();
            _guard.Reset();
            _volume.Clear();
            _volume.Trigger(VfxEventKind.Chimney, new Vector3(1.3f, 0f, -1.2f));
            _volume.Trigger(VfxEventKind.Fire, new Vector3(0.9f, 0f, -0.4f), 0.8f);
            _frame = 0; _nozzle = true;
        }
        LastAction = "🎬 새 샷 — 물줄기 · 굴뚝 · 화염";
        Raise();
    }

    public void SetNozzle(bool on) { _nozzle = on; LastAction = on ? "🚿 물줄기 ON" : "🚿 물줄기 OFF"; Raise(); }
    public void SetViscosity(float v) { _fluid.Viscosity = Math.Clamp(v, 0f, 8f); Raise(); }
    public void SetSurfaceTension(float s) { _fluid.SurfaceTension = Math.Clamp(s, 0f, 8f); Raise(); }
    public void SetNeural(bool on) { _fluid.NeuralClosure = on; LastAction = on ? "🧠 신경망 에디 점성 ON" : "🧠 신경망 에디 점성 OFF"; Raise(); }
    public void SetPreset(VolumePreset p) { lock (_gate) _volume.SetPreset(p); LastAction = _volume.LastAction; Raise(); }

    /// <summary>제어 평면: 이벤트 발동(폭발 = 볼륨 + 파편 + 가스 입자).</summary>
    public void Trigger(VfxEventKind kind, Vector3 at, float intensity = 1f)
    {
        lock (_gate)
        {
            _volume.Trigger(kind, at, intensity);
            if (kind == VfxEventKind.Explosion)
            {
                _guard.SpawnDebris(at + new Vector3(0, 0.15f, 0), (int)(60 * intensity), 4.5f * intensity);
                for (var k = 0; k < 6; k++) _fluid.EmitNozzle(at + new Vector3(0, 0.3f + k * 0.04f, 0), Vector3.UnitY, 0.25f, 1.5f * intensity, 40, FluidPhase.Gas, 2.5f);
            }
        }
        LastAction = _volume.LastAction;
        Raise();
    }

    public int TriggerPrompt(string? prompt)
    {
        int n;
        var focus = new Vector3(0.2f, 0f, -0.3f);
        lock (_gate) n = _volume.TriggerFromPrompt(prompt, focus);
        if (n > 0 && _volume.LastEvent == VfxEventKind.Explosion) lock (_gate) _guard.SpawnDebris(focus + new Vector3(0, 0.3f, 0), 60, 4.5f);
        LastAction = n > 0 ? "✨ 프롬프트 → 이벤트 " + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + "개 · " + _volume.LastAction : "⚠ 인식된 VFX 키워드 없음";
        Raise();
        return n;
    }

    /// <summary>1프레임(무할당): 노즐 방출 → 유체(+충돌) → 2차 입자/젖음 → 볼륨 → 3DGS 레이어.</summary>
    public void TickOnce()
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Vfx.Tick");
            const float dt = 1f / Hz;
            if (_nozzle) _fluid.EmitNozzle(NozzlePosition, NozzleDirection, 0.06f, 3.2f, NozzlePerFrame, FluidPhase.Liquid);
            // 캐릭터 미세 흔들림(프록시 이동)
            var sway = 0.004f * MathF.Sin(_frame * 0.05f);
            _guard.TranslateProxies(new Vector3(sway - 0.004f * MathF.Sin((_frame - 1) * 0.05f), 0, 0));
            _fluid.Step(dt);
            _guard.StepSecondary(dt);
            _volume.Step(dt);
            _fluid.WriteSplats(_fluidSplats, SplatBudget);
            _frame++;
        }
    }

    public void Start()
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(() => LoopAsync(cts.Token));
        LastAction = "▶ VFX 시뮬레이션 가동";
        Raise();
    }

    public void Stop()
    {
        var c = Interlocked.Exchange(ref _cts, null);
        if (c is null) return;
        c.Cancel(); c.Dispose();
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
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("Vfx.Tick", ex); }
                if ((Frame & 7) == 0) Raise();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Vfx.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
