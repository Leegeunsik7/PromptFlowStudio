using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Neural3d;
using PromptFlowStudio.Services.PathTracing;

namespace PromptFlowStudio.Services.Cinematography;

/// <summary>
/// [PromptFlow Studio v16.85.0] 🎥 AI 신경망 시네마토그래피 &amp; 구도 스튜디오 러너(60 Hz 카메라 · 20 Hz 렌더 · <see cref="TickOnce"/> 무할당).
/// 피사체 안무(8자 보행) → 궤적 최적화/추종 → 구도 평가 → 자동 구도(제안 눈 위치 → CompositionTarget 완화) →
/// 3DGS 라스터(지면 · 기둥 장애물 · 실용 조명 · 인물 2) 또는 v16.74 경로 추적 → 아나모픽 보케/스트릭/수차 합성.
/// </summary>
public sealed class NeuralCinematographyStudioRunner : IDisposable
{
    public const int Hz = 60, RenderW = 256, RenderH = 107, PtW = 192, PtH = 80, PillarCount = 6, LightCount = 24;
    private const int Ground = 24;

    private readonly object _gate = new();
    private readonly NeuralCameraTrajectoryEngine _traj;
    private readonly GenerativeShotFramingEvaluator _eval;
    private readonly AnamorphicLensBokehSynthesizer _lens;
    private readonly AiGaussianSplattingRasterizerEngine _ras = new();
    private readonly GaussianScene _scene = new(Ground * Ground + PillarCount * 24 + LightCount + 32);
    private readonly byte[] _rgb = new byte[RenderW * RenderH * 3];
    private readonly float[] _depth = new float[RenderW * RenderH];
    private readonly Vector3[] _pillars = new Vector3[PillarCount];
    private readonly MaterialXBsdfShaderSynthesizer _mtl = new();
    private VolumetricPathTracingLightTransportEngine? _pt;
    private int _mRed, _mPillar, _mLight, _mBlue;
    private CancellationTokenSource? _cts;
    private long _frame;
    private float _t;
    private Vector2 _compose = new(0.5f, 0.4f);

    public NeuralCinematographyStudioRunner(IServiceProvider sp)
        : this((NeuralCameraTrajectoryEngine?)sp.GetService(typeof(NeuralCameraTrajectoryEngine)) ?? new NeuralCameraTrajectoryEngine(),
               (GenerativeShotFramingEvaluator?)sp.GetService(typeof(GenerativeShotFramingEvaluator)) ?? new GenerativeShotFramingEvaluator(),
               (AnamorphicLensBokehSynthesizer?)sp.GetService(typeof(AnamorphicLensBokehSynthesizer)) ?? new AnamorphicLensBokehSynthesizer()) { }

    public NeuralCinematographyStudioRunner(NeuralCameraTrajectoryEngine traj, GenerativeShotFramingEvaluator frame, AnamorphicLensBokehSynthesizer lens)
    {
        _traj = traj; _eval = frame; _lens = lens;
        _lens.Resize(RenderW, RenderH);
        for (var i = 0; i < PillarCount; i++)
        {
            var a = i * MathF.Tau / PillarCount + 0.3f;
            _pillars[i] = new Vector3(MathF.Cos(a) * (i % 2 == 0 ? 4.5f : 7.5f), 0f, MathF.Sin(a) * (i % 2 == 0 ? 3.5f : 6f));
        }
        _traj.ClearObstacles();
        for (var i = 0; i < PillarCount; i++)
        {
            _traj.AddObstacle(_pillars[i] + Vector3.UnitY * 1.0f, 0.7f);
            _traj.AddObstacle(_pillars[i] + Vector3.UnitY * 2.6f, 0.7f);
        }
        _mRed = _mtl.Register("jacket", new PbrMaterial { BaseColor = new(0.7f, 0.08f, 0.06f), Roughness = 0.6f, Specular = 0.5f, Ior = 1.5f, Opacity = 1f, SubsurfaceColor = Vector3.One, SubsurfaceRadius = 0.1f });
        _mBlue = _mtl.Register("coat", new PbrMaterial { BaseColor = new(0.1f, 0.2f, 0.6f), Roughness = 0.6f, Specular = 0.5f, Ior = 1.5f, Opacity = 1f, SubsurfaceColor = Vector3.One, SubsurfaceRadius = 0.1f });
        _mPillar = _mtl.Register("concrete", new PbrMaterial { BaseColor = new(0.55f, 0.53f, 0.5f), Roughness = 0.8f, Specular = 0.5f, Ior = 1.5f, Opacity = 1f, SubsurfaceColor = Vector3.One, SubsurfaceRadius = 0.1f });
        _mLight = _mtl.Register("practical", new PbrMaterial { BaseColor = new(0.1f), Roughness = 0.5f, Ior = 1.5f, Opacity = 1f, SubsurfaceColor = Vector3.One, SubsurfaceRadius = 0.1f, Emission = new(12f, 9f, 6f) });
        NewShot();
    }

    public event Action? OnChange;

    public NeuralCameraTrajectoryEngine Trajectory => _traj;
    public GenerativeShotFramingEvaluator Framing => _eval;
    public AnamorphicLensBokehSynthesizer Lens => _lens;
    public GaussianScene Scene => _scene;
    public bool IsRunning => _cts is not null;
    public long Frame { get { lock (_gate) return _frame; } }
    public bool AutoCompose { get; set; } = true;
    public bool UsePathTracer { get; private set; }
    public float WalkSpeed { get; set; } = 1f;
    public FramingResult LastFraming { get; private set; }
    public Vector3 Secondary { get; private set; }
    public Vector3 PillarPosition(int i) => _pillars[i];
    public double LastTickMs { get; private set; }
    public double LastRenderMs { get; private set; }
    public long RenderedFrames { get; private set; }
    public string? LastAction { get; private set; }
    public int FrameWidth => _lens.Width;
    public int FrameHeight => _lens.Height;

    /// <summary>안무: 8자 보행(속도 배율) — 위치 · 속도 해석해.</summary>
    public (Vector3 Pos, Vector3 Vel) SubjectAt(float t)
    {
        var w = 0.21f * WalkSpeed;
        return (new Vector3(6f * MathF.Sin(w * t), 0f, 4f * MathF.Sin(2f * w * t)),
                new Vector3(6f * w * MathF.Cos(w * t), 0f, 8f * w * MathF.Cos(2f * w * t)));
    }

    public void NewShot()
    {
        lock (_gate)
        {
            _t = 0f; _frame = 0;
            var (p, v) = SubjectAt(0f);
            Secondary = new Vector3(-2.5f, 0f, 8f);
            _traj.Reset(p, v);
            _compose = new Vector2(0.5f, 0.4f);
            Advance(1f / Hz, true);
        }
        LastAction = "🎬 새 샷 — " + NeuralCameraTrajectoryEngine.MoveName(_traj.Move) + " · " + NeuralCameraTrajectoryEngine.ScaleName(_traj.Scale);
        Raise();
    }

    public void SetMove(CameraMove m) { _traj.Move = m; LastAction = "🎥 무브 " + NeuralCameraTrajectoryEngine.MoveName(m); Raise(); }
    public void SetScale(ShotScale s) { _traj.Scale = s; _traj.AutoZoom = true; LastAction = "🔍 샷 " + NeuralCameraTrajectoryEngine.ScaleName(s); Raise(); }
    public void SetFocus(FocusTarget f) { _traj.Focus = f; LastAction = f == FocusTarget.Secondary ? "🎯 랙 포커스 → 보조 인물" : f == FocusTarget.Manual ? "🎯 수동 초점" : "🎯 랙 포커스 → 주 피사체"; Raise(); }
    public void SetDutch(float deg) { _traj.DutchDeg = Math.Clamp(deg, -30f, 30f); Raise(); }

    public void SetPathTracer(bool on)
    {
        lock (_gate)
        {
            if (on && _pt is null) { _pt = new VolumetricPathTracingLightTransportEngine(_mtl); _pt.SetResolution(PtW, PtH); _pt.MaxBounces = 3; }
            UsePathTracer = on;
            _lens.Resize(on ? PtW : RenderW, on ? PtH : RenderH);
        }
        LastAction = on ? "💡 v16.74 경로 추적 입력" : "🧊 v16.58 3DGS 입력";
        Raise();
    }

    /// <summary>프롬프트(한/영) → 무브 · 샷 스케일 · 더치 · 초점 · 렌즈 성향.</summary>
    public int TriggerPrompt(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) { LastAction = "⚠ 프롬프트 없음"; Raise(); return 0; }
        static bool Has(string s, string a, string b) => s.Contains(a, StringComparison.OrdinalIgnoreCase) || s.Contains(b, StringComparison.OrdinalIgnoreCase);
        var n = 0;
        if (Has(prompt, "오빗", "orbit") || Has(prompt, "회전", "circle")) { _traj.Move = CameraMove.Orbit; n++; }
        else if (Has(prompt, "돌리", "dolly") || Has(prompt, "트래킹", "tracking")) { _traj.Move = CameraMove.Dolly; n++; }
        else if (Has(prompt, "크레인", "crane") || Has(prompt, "부감", "aerial")) { _traj.Move = CameraMove.Crane; n++; }
        else if (Has(prompt, "핸드헬드", "handheld") || Has(prompt, "흔들", "shaky")) { _traj.Move = CameraMove.Handheld; n++; }
        else if (Has(prompt, "팔로우", "follow") || Has(prompt, "추적", "chase")) { _traj.Move = CameraMove.Follow; n++; }
        if (Has(prompt, "클로즈업", "close-up") || Has(prompt, "close up", "closeup")) { _traj.Scale = ShotScale.CloseUp; n++; }
        else if (Has(prompt, "바스트", "medium close")) { _traj.Scale = ShotScale.MediumCloseUp; n++; }
        else if (Has(prompt, "와이드", "wide") || Has(prompt, "풍경", "establishing")) { _traj.Scale = ShotScale.Wide; n++; }
        else if (Has(prompt, "풀샷", "full shot") || Has(prompt, "전신", "full body")) { _traj.Scale = ShotScale.Full; n++; }
        else if (Has(prompt, "미디엄", "medium")) { _traj.Scale = ShotScale.Medium; n++; }
        if (Has(prompt, "더치", "dutch") || Has(prompt, "기울", "tilted")) { _traj.DutchDeg = 15f; n++; }
        if (Has(prompt, "얕은 심도", "shallow") || Has(prompt, "보케", "bokeh")) { _traj.FStop = 1.4f; _lens.BokehScale = 1.3f; n++; }
        else if (Has(prompt, "딥 포커스", "deep focus")) { _traj.FStop = 11f; n++; }
        if (Has(prompt, "랙 포커스", "rack focus")) { _traj.Focus = _traj.Focus == FocusTarget.Secondary ? FocusTarget.Subject : FocusTarget.Secondary; n++; }
        if (Has(prompt, "아나모픽", "anamorphic") || Has(prompt, "플레어", "flare")) { _lens.Squeeze = 2f; _lens.StreakIntensity = 1f; n++; }
        else if (Has(prompt, "구면", "spherical")) { _lens.Squeeze = 1f; _lens.StreakIntensity = 0f; n++; }
        LastAction = n > 0 ? "✨ 프롬프트 → 반영 " + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + "건" : "⚠ 인식된 키워드 없음";
        Raise();
        return n;
    }

    /// <summary>1프레임(무할당 · 렌더는 3프레임마다 = 20 Hz 뷰파인더).</summary>
    public void TickOnce()
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Cinematography.Tick");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Advance(1f / Hz, _frame % 3 == 0);
            _frame++;
            LastTickMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
    }

    private void Advance(float dt, bool render)
    {
        _t += dt;
        var (p, v) = SubjectAt(_t);
        _traj.Step(dt, p, v, Secondary + Vector3.UnitY * 1.6f);
        var pose = _traj.RenderPose;
        LastFraming = _eval.Evaluate(pose, p, _traj.SubjectHeight, v, Secondary + Vector3.UnitY * 1.0f, true);
        if (AutoCompose)
        {
            var s = GenerativeShotFramingEvaluator.SuggestEyeTarget(LastFraming);
            _compose += (s - _compose) * (1f - MathF.Exp(-dt * 1.5f));
            _traj.CompositionTarget = _compose;
        }
        else _traj.CompositionTarget = new Vector2(0.5f, 0.4f);
        if (render) Render(pose, p);
    }

    private void Render(in CameraPose pose, Vector3 subject)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _lens.SetLens(pose);
        if (UsePathTracer && _pt is not null)
        {
            BuildPathScene(subject);
            var f = pose.Forward;
            _pt.SetCamera(pose.Position, pose.Position + f, NeuralCameraTrajectoryEngine.VerticalFovDeg(pose.FocalMm));
            _pt.RenderFrame(RenderedFrames);
            _lens.ProcessPathTracer(_pt);
        }
        else
        {
            BuildSplats(subject);
            var fy = RenderH * 0.5f / MathF.Tan(NeuralCameraTrajectoryEngine.VerticalFovDeg(pose.FocalMm) * MathF.PI / 360f);
            var cam = new SplatCamera(pose.Position, pose.Right, pose.Up, pose.Forward, fy, fy, RenderW * 0.5f, RenderH * 0.5f, RenderW, RenderH);
            _ras.Render(_scene, cam, new SplatRenderOptions(Near: 0.05f, Far: 120f, Background: new Vector3(0.02f, 0.025f, 0.05f)), _rgb, _depth);
            _lens.ProcessSplat(_rgb, _depth, RenderW, RenderH);
        }
        RenderedFrames++;
        LastRenderMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    private void AddPerson(Vector3 feet, Vector3 dir, Vector3 coat)
    {
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.Atan2(dir.X, dir.Z));
        _scene.Add(feet + new Vector3(0f, 0.45f, 0f), q, new Vector3(0.12f, 0.42f, 0.12f), 0.97f, new Vector3(0.12f, 0.12f, 0.15f));
        _scene.Add(feet + new Vector3(0f, 1.18f, 0f), q, new Vector3(0.22f, 0.32f, 0.14f), 0.98f, coat);
        _scene.Add(feet + new Vector3(0f, 1.6f, 0f), q, new Vector3(0.1f, 0.12f, 0.1f), 0.99f, new Vector3(0.85f, 0.62f, 0.5f));
    }

    private void BuildSplats(Vector3 subject)
    {
        _scene.Clear();
        const float cell = 1.6f;
        for (var j = 0; j < Ground; j++)
            for (var i = 0; i < Ground; i++)
            {
                var c = ((i + j) & 1) == 0 ? 0.32f : 0.24f;
                _scene.Add(new Vector3((i - Ground / 2 + 0.5f) * cell, 0f, (j - Ground / 2 + 0.5f) * cell), Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f),
                    new Vector3(0.95f, 0.95f, 0.02f), 0.98f, new Vector3(c, c * 0.95f, c * 0.9f));
            }
        for (var p = 0; p < PillarCount; p++)
            for (var k = 0; k < 24; k++)
            {
                var a = (k % 8) * MathF.Tau / 8f; var y = 0.4f + (k / 8) * 1.4f;
                var n = new Vector3(MathF.Cos(a), 0f, MathF.Sin(a));
                _scene.Add(_pillars[p] + n * 0.45f + Vector3.UnitY * y, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.Atan2(n.X, n.Z)), new Vector3(0.25f, 0.75f, 0.05f), 0.98f, new Vector3(0.55f, 0.53f, 0.5f));
            }
        for (var l = 0; l < LightCount; l++)
            _scene.Add(new Vector3(-12f + l * 1.05f, 2.6f + 0.25f * MathF.Sin(l * 0.9f), 15f), Quaternion.Identity, new Vector3(0.08f), 1f, new Vector3(1f, 0.92f, 0.75f));
        var (_, v) = SubjectAt(_t);
        AddPerson(subject, v, new Vector3(0.7f, 0.1f, 0.08f));
        AddPerson(Secondary, -Vector3.UnitZ, new Vector3(0.12f, 0.22f, 0.6f));
    }

    private void BuildPathScene(Vector3 subject)
    {
        var pt = _pt!;
        pt.ClearScene();
        for (var p = 0; p < PillarCount; p++) pt.AddBox(_pillars[p] - new Vector3(0.45f, 0f, 0.45f), _pillars[p] + new Vector3(0.45f, 3.6f, 0.45f), _mPillar);
        pt.AddSphere(subject + Vector3.UnitY * 0.9f, 0.45f, _mRed); pt.AddSphere(subject + Vector3.UnitY * 1.6f, 0.13f, _mRed);
        pt.AddSphere(Secondary + Vector3.UnitY * 0.9f, 0.45f, _mBlue); pt.AddSphere(Secondary + Vector3.UnitY * 1.6f, 0.13f, _mBlue);
        for (var l = 0; l < LightCount; l += 3) pt.AddSphere(new Vector3(-12f + l * 1.05f, 2.6f, 15f), 0.12f, _mLight);
    }

    public void Start()
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(() => LoopAsync(cts.Token));
        LastAction = "▶ 시네마토그래피 가동";
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
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("Cinematography.Tick", ex); }
                if ((Frame & 7) == 0) Raise();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Cinematography.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
