using System.Numerics;
using PromptFlowStudio.Services.Cinematography;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.OpenUsd;

namespace PromptFlowStudio.Services.VideoDiffusion;

/// <summary>
/// [PromptFlow Studio v16.87.0] 🎬 AI 비디오 디퓨전 &amp; 시공간 일관성 스튜디오 러너(틱 60 Hz · 생성 30 fps · <see cref="TickOnce"/> 무할당).
/// 피사체 보행(원형 경로 · v16.75 스켈레톤 FK) → v16.85 카메라 궤적(지형 결선) → ControlNet 깊이/포즈/카메라 맵 →
/// 목표 라텐트 → CFG-DDIM 디노이징(플로 워핑 노이즈) → 디코드 → 옵티컬 플로 일관성 가드 → 엔진 라텐트 플로 피드백.
/// </summary>
public sealed class LatentVideoDiffusionStudioRunner : IDisposable
{
    public const int Hz = 60, Fps = 30;

    private readonly object _gate = new();
    private readonly RealtimeLatentVideoDiffusionEngine _engine;
    private readonly NeuralTemporalConsistencyGuard _guard;
    private readonly ControlNetSpatialConditioningSynthesizer _control;
    private readonly NeuralCameraTrajectoryEngine _camera = new() { Move = CameraMove.Follow, Distance = 6f, Height = 1.8f, AzimuthOffsetDeg = 30f, Scale = ShotScale.Full };
    private readonly CachedHeightField _terrain = new();
    private CancellationTokenSource? _cts;
    private long _frame;
    private float _t;
    private Vector2 _grain;

    public LatentVideoDiffusionStudioRunner(IServiceProvider sp)
        : this((RealtimeLatentVideoDiffusionEngine?)sp.GetService(typeof(RealtimeLatentVideoDiffusionEngine)) ?? new RealtimeLatentVideoDiffusionEngine(),
               (NeuralTemporalConsistencyGuard?)sp.GetService(typeof(NeuralTemporalConsistencyGuard)) ?? new NeuralTemporalConsistencyGuard(),
               (ControlNetSpatialConditioningSynthesizer?)sp.GetService(typeof(ControlNetSpatialConditioningSynthesizer)) ?? new ControlNetSpatialConditioningSynthesizer(),
               (GenerativeWorldProceduralEngine?)sp.GetService(typeof(GenerativeWorldProceduralEngine))) { }

    public LatentVideoDiffusionStudioRunner(RealtimeLatentVideoDiffusionEngine engine, NeuralTemporalConsistencyGuard guard, ControlNetSpatialConditioningSynthesizer control, GenerativeWorldProceduralEngine? world = null)
    {
        _engine = engine; _guard = guard; _control = control;
        if (world is not null) _terrain.Bake(world, Vector2.Zero, 260f); else _terrain.BakeHills(260f);
        UsesWorld = world is not null;
        _control.BindTerrain(_terrain);
        _camera.BindTerrain(_terrain);
        NewShot();
    }

    public event Action? OnChange;

    public RealtimeLatentVideoDiffusionEngine Engine => _engine;
    public NeuralTemporalConsistencyGuard Guard => _guard;
    public ControlNetSpatialConditioningSynthesizer Control => _control;
    public NeuralCameraTrajectoryEngine Camera => _camera;
    public bool UsesWorld { get; }
    public bool IsRunning => _cts is not null;
    public long Frame { get { lock (_gate) return _frame; } }
    public long GeneratedFrames => _engine.Frames;
    public float WalkSpeed { get; set; } = 1.3f;
    public float CameraJitter { get; set; }
    public Vector3 Subject { get; private set; }
    public double LastTickMs { get; private set; }
    public double LastFrameMs { get; private set; }
    public string? LastAction { get; private set; }

    public static string NoiseName(NoiseMode m) => m switch { NoiseMode.Shared => "공유", NoiseMode.FlowWarped => "플로 워핑", _ => "독립" };

    /// <summary>보행 경로(반경 7 m 원) — 위치 · 속도 · 방위.</summary>
    public (Vector3 Pos, Vector3 Vel, float Yaw) SubjectAt(float t)
    {
        var w = WalkSpeed / 7f; var a = w * t;
        var p = new Vector3(7f * MathF.Sin(a), 0f, 7f * MathF.Cos(a) - 7f);
        p.Y = _terrain.HeightAt(p.X, p.Z);
        var v = new Vector3(7f * w * MathF.Cos(a), 0f, -7f * w * MathF.Sin(a));
        return (p, v, MathF.Atan2(v.X, v.Z));
    }

    public void NewShot()
    {
        lock (_gate)
        {
            _t = 0f; _frame = 0; _grain = Vector2.Zero;
            var (p, v, _) = SubjectAt(0f);
            _camera.Reset(p, v);
            _guard.Reset();
            Advance(1f / Hz, true);
        }
        LastAction = "🎬 새 샷 — " + _engine.Palette.Name + " · " + NoiseName(_engine.Noise) + " 노이즈";
        Raise();
    }

    public int TriggerPrompt(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) { LastAction = "⚠ 프롬프트 없음"; Raise(); return 0; }
        static bool Has(string s, string a, string b) => s.Contains(a, StringComparison.OrdinalIgnoreCase) || s.Contains(b, StringComparison.OrdinalIgnoreCase);
        int n;
        lock (_gate) n = _engine.Configure(prompt);
        if (Has(prompt, "달리", "running")) { WalkSpeed = 3.2f; n++; }
        else if (Has(prompt, "걷", "walking")) { WalkSpeed = 1.3f; n++; }
        if (Has(prompt, "오빗", "orbit")) { _camera.Move = CameraMove.Orbit; n++; }
        else if (Has(prompt, "핸드헬드", "handheld")) { _camera.Move = CameraMove.Handheld; n++; }
        else if (Has(prompt, "돌리", "dolly")) { _camera.Move = CameraMove.Dolly; n++; }
        LastAction = n > 0 ? "✨ 프롬프트 → 반영 " + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + "건 · " + _engine.Palette.Name : "⚠ 인식된 키워드 없음";
        Raise();
        return n;
    }

    public void SetNoise(NoiseMode m) { _engine.Noise = m; LastAction = "🎲 노이즈 " + NoiseName(m); Raise(); }

    /// <summary>1틱(무할당 · 생성은 Hz/Fps 틱마다).</summary>
    public void TickOnce()
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("VideoDiffusion.Tick");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Advance(1f / Hz, _frame % (Hz / Fps) == 0);
            _frame++;
            LastTickMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
    }

    private void Advance(float dt, bool generate)
    {
        _t += dt;
        var (p, v, yaw) = SubjectAt(_t);
        Subject = p;
        _camera.Step(dt, p, v, p + Vector3.UnitY);
        if (!generate) return;
        var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        var cam = _camera.RenderPose;
        if (CameraJitter > 0f) cam.Position += new Vector3(MathF.Sin(37f * _t), MathF.Sin(53f * _t + 1f), 0f) * CameraJitter;
        _control.SetWalkPose(_t * WalkSpeed / 1.5f);
        _control.Build(cam, p, yaw);
        _engine.BuildTargets(_control);
        // 그레인 = 장면 고정(누적 카메라 플로 역이동)
        _grain -= _control.CameraFlowPx;
        _engine.GrainOffset = _grain;
        _engine.Generate(_guard.LatentFlow, _control.ControlStart, _control.ControlEnd);
        _guard.Process(_engine.Frame, _control.CameraFlowPx);
        LastFrameMs = System.Diagnostics.Stopwatch.GetElapsedTime(t1).TotalMilliseconds;
    }

    public void Start()
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(() => LoopAsync(cts.Token));
        LastAction = "▶ 비디오 디퓨전 가동";
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
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("VideoDiffusion.Tick", ex); }
                if ((Frame & 7) == 0) Raise();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("VideoDiffusion.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
