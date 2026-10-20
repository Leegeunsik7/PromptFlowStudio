using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.DigitalHuman;
using PromptFlowStudio.Services.Neural3d;

namespace PromptFlowStudio.Services.HairCloth;

public enum BodyMotion : byte { Idle, Walk, Spin, Headbang, Jump }

/// <summary>
/// [PromptFlow Studio v16.83.0] 💇 AI 신경망 헤어 &amp; 의상 다이내믹스 스튜디오 러너(60 Hz · <see cref="TickOnce"/> 무할당).
/// 모션(대기 · 걷기 · 회전 · 헤드뱅 · 점프) → 바디 리그 포즈 → 헤어 DFTL + 의상 XPBD(겉보기 바람 = 바람 − 몸 속도) →
/// 딥 오패시티 그림자 → 헤어/의상 셰이딩 + v16.82 얼굴 SSS 피부 오버레이 → 3DGS 합성(얼굴 1920 + 의상 1120 + 표시 가닥).
/// </summary>
public sealed class NeuralHairClothStudioRunner : IDisposable
{
    public const int Hz = 60, DefaultStrands = 16000, DisplayStrands = 800;
    public static readonly Vector3 Eye = new(0.35f, -0.25f, 2.3f);

    private readonly object _gate = new();
    private readonly NeuralStrandHairDynamicsEngine _hair;
    private readonly GenerativeGarmentClothCollisionSynthesizer _cloth;
    private readonly NeuralHairClothShaderIntegrationGuard _shader;
    private readonly NeuralFacialMuscleSimulator _face = new();
    private readonly PhotorealisticMicroSkinSssSynthesizer _skin = new();
    private readonly GaussianScene _splats;
    private CancellationTokenSource? _cts;
    private long _frame;
    private float _t, _yaw, _spinRate;
    private Vector3 _prevOffset;

    public NeuralHairClothStudioRunner(IServiceProvider sp)
        : this((NeuralStrandHairDynamicsEngine?)sp.GetService(typeof(NeuralStrandHairDynamicsEngine)) ?? new NeuralStrandHairDynamicsEngine(),
               (GenerativeGarmentClothCollisionSynthesizer?)sp.GetService(typeof(GenerativeGarmentClothCollisionSynthesizer)) ?? new GenerativeGarmentClothCollisionSynthesizer(),
               (NeuralHairClothShaderIntegrationGuard?)sp.GetService(typeof(NeuralHairClothShaderIntegrationGuard)) ?? new NeuralHairClothShaderIntegrationGuard()) { }

    public NeuralHairClothStudioRunner(NeuralStrandHairDynamicsEngine hair, GenerativeGarmentClothCollisionSynthesizer cloth, NeuralHairClothShaderIntegrationGuard shader)
    {
        _hair = hair; _cloth = cloth; _shader = shader;
        _cloth.Body = _hair.Body;
        _skin.Bind(_face);
        _face.SetExpression(FacePreset.Joy, 0.35f);
        for (var k = 0; k < 30; k++) _face.Step(1f / Hz);
        _hair.SetActiveStrands(Math.Min(DefaultStrands, _hair.Capacity));
        _splats = new GaussianScene(NeuralFacialMuscleSimulator.VertexCount + GenerativeGarmentClothCollisionSynthesizer.MaxVerts + DisplayStrands * (NeuralStrandHairDynamicsEngine.NodesPerStrand - 1));
        SetWind(200f, 0f, 0.35f);
        NewShot();
    }

    public event Action? OnChange;

    public NeuralStrandHairDynamicsEngine Hair => _hair;
    public GenerativeGarmentClothCollisionSynthesizer Cloth => _cloth;
    public NeuralHairClothShaderIntegrationGuard Shader => _shader;
    public HumanBodyColliderRig Body => _hair.Body;
    public NeuralFacialMuscleSimulator Face => _face;
    public GaussianScene Splats => _splats;
    public bool IsRunning => _cts is not null;
    public long Frame { get { lock (_gate) return _frame; } }
    public BodyMotion Motion { get; private set; }
    public float WindYawDeg { get; private set; }
    public float WindSpeed { get; private set; }
    public float Turbulence { get; private set; }
    public Vector3 ApparentWind { get; private set; }
    public int Stride => Math.Max(1, _hair.ActiveStrands / DisplayStrands);
    public double LastTickMs { get; private set; }
    public string? LastAction { get; private set; }

    public static string MotionName(BodyMotion m) => m switch
    {
        BodyMotion.Walk => "걷기", BodyMotion.Spin => "회전", BodyMotion.Headbang => "헤드뱅", BodyMotion.Jump => "점프", _ => "대기",
    };

    /// <summary>제어 평면: 새 샷(포즈 원점 · 헤어/의상 휴지 배치).</summary>
    public void NewShot()
    {
        lock (_gate)
        {
            _t = 0f; _yaw = 0f; _spinRate = 0f; _prevOffset = Vector3.Zero;
            _hair.Body.SetPose(0f, Vector3.Zero, 0f, 0f, 0f);
            _hair.Reset();
            _cloth.Configure(_cloth.Garment, _cloth.Fabric);
            for (var k = 0; k < 20; k++) { _hair.Step(1f / Hz); _cloth.Step(1f / Hz); }
            _frame = 0;
            Render();
        }
        LastAction = "🎬 새 샷 — " + NeuralStrandHairDynamicsEngine.StyleName(_hair.Style) + " · " + GenerativeGarmentClothCollisionSynthesizer.GarmentName(_cloth.Garment) + " " + _cloth.Material.Name;
        Raise();
    }

    public void SetMotion(BodyMotion m) { Motion = m; LastAction = "🕺 모션 " + MotionName(m); Raise(); }

    public void SetStrandCount(int n)
    {
        lock (_gate) _hair.SetActiveStrands(n);
        LastAction = "💇 가닥 " + _hair.ActiveStrands.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Raise();
    }

    public void SetStyle(HairStyle s) { lock (_gate) _hair.SetStyle(s); LastAction = _hair.LastAction; Raise(); }
    public void SetHairColor(HairColorPreset c) { lock (_gate) _shader.SetHairColor(c); LastAction = _shader.LastAction; Raise(); }
    public void SetModel(HairShadingModel m) { lock (_gate) _shader.Model = m; LastAction = m == HairShadingModel.Marschner ? "✨ Marschner R/TT/TRT" : "✨ Kajiya-Kay"; Raise(); }

    public void SetGarment(GarmentType g, FabricKind f)
    {
        lock (_gate) _cloth.Configure(g, f);
        LastAction = _cloth.LastAction;
        Raise();
    }

    public void SetClothStiffness(float stretchScale, float bendScale)
    {
        _cloth.StretchScale = Math.Clamp(stretchScale, 0.01f, 100f); _cloth.BendScale = Math.Clamp(bendScale, 0.01f, 100f);
        Raise();
    }

    /// <summary>바람 벡터(수평 방위 ° · m/s · 난류 0..1).</summary>
    public void SetWind(float yawDeg, float speed, float turbulence)
    {
        WindYawDeg = yawDeg; WindSpeed = Math.Clamp(speed, 0f, 25f); Turbulence = Math.Clamp(turbulence, 0f, 1f);
        _hair.Turbulence = Turbulence; _cloth.Turbulence = Turbulence;
        Raise();
    }

    public Vector3 WindVector
    {
        get { var a = WindYawDeg * MathF.PI / 180f; return new Vector3(MathF.Sin(a), 0f, MathF.Cos(a)) * WindSpeed; }
    }

    /// <summary>프롬프트(한/영) → 의상/원단 · 헤어 스타일/컬러 · 바람 · 모션.</summary>
    public int TriggerPrompt(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) { LastAction = "⚠ 프롬프트 없음"; Raise(); return 0; }
        static bool Has(string s, string a, string b) => s.Contains(a, StringComparison.OrdinalIgnoreCase) || s.Contains(b, StringComparison.OrdinalIgnoreCase);
        var n = 0;
        var g = _cloth.Garment; var f = _cloth.Fabric;
        if (GenerativeGarmentClothCollisionSynthesizer.ParsePrompt(prompt, ref g, ref f)) { SetGarment(g, f); n++; }
        if (Has(prompt, "곱슬", "curly")) { SetStyle(HairStyle.Curly); n++; }
        else if (Has(prompt, "단발", "bob")) { SetStyle(HairStyle.Bob); n++; }
        else if (Has(prompt, "모피", "fur") || Has(prompt, "털", "furry")) { SetStyle(HairStyle.Fur); n++; }
        else if (Has(prompt, "긴 머리", "long hair") || Has(prompt, "장발", "long")) { SetStyle(HairStyle.Long); n++; }
        if (Has(prompt, "금발", "blonde")) { SetHairColor(HairColorPreset.Blonde); n++; }
        else if (Has(prompt, "흑발", "black")) { SetHairColor(HairColorPreset.Black); n++; }
        else if (Has(prompt, "적발", "red") || Has(prompt, "빨간", "ginger")) { SetHairColor(HairColorPreset.Red); n++; }
        else if (Has(prompt, "은발", "silver") || Has(prompt, "백발", "white")) { SetHairColor(HairColorPreset.Silver); n++; }
        else if (Has(prompt, "갈색", "brown")) { SetHairColor(HairColorPreset.Brown); n++; }
        if (Has(prompt, "폭풍", "storm") || Has(prompt, "강풍", "gale")) { SetWind(WindYawDeg, 14f, 0.7f); n++; }
        else if (Has(prompt, "산들", "breeze") || Has(prompt, "바람", "wind")) { SetWind(WindYawDeg, 4f, 0.4f); n++; }
        else if (Has(prompt, "무풍", "calm")) { SetWind(WindYawDeg, 0f, 0.2f); n++; }
        if (Has(prompt, "회전", "spin") || Has(prompt, "빙글", "twirl")) { SetMotion(BodyMotion.Spin); n++; }
        else if (Has(prompt, "걷", "walk")) { SetMotion(BodyMotion.Walk); n++; }
        else if (Has(prompt, "헤드뱅", "headbang")) { SetMotion(BodyMotion.Headbang); n++; }
        else if (Has(prompt, "점프", "jump")) { SetMotion(BodyMotion.Jump); n++; }
        LastAction = n > 0 ? "✨ 프롬프트 → 반영 " + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + "건" : "⚠ 인식된 키워드 없음";
        Raise();
        return n;
    }

    /// <summary>1프레임(무할당): 모션 → 헤어/의상 물리 → 그림자 → 셰이딩 → 3DGS.</summary>
    public void TickOnce()
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("HairCloth.Tick");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            const float dt = 1f / Hz;
            ApplyMotion(dt);
            _hair.Step(dt);
            _cloth.Step(dt);
            Render();
            _frame++;
            LastTickMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
    }

    private void ApplyMotion(float dt)
    {
        _t += dt;
        float pitch = 0f, hyaw = 0f, leg = 0f; var off = Vector3.Zero;
        var targetSpin = 0f;
        switch (Motion)
        {
            case BodyMotion.Idle:
                off.Y = 0.004f * MathF.Sin(2f * MathF.PI * 0.25f * _t); hyaw = 0.12f * MathF.Sin(0.7f * _t);
                break;
            case BodyMotion.Walk:
                {
                    var ph = 2f * MathF.PI * 0.9f * _t;
                    leg = 0.42f * MathF.Sin(ph); off.Y = 0.018f * MathF.Abs(MathF.Sin(ph)) - 0.01f; off.X = 0.015f * MathF.Sin(ph * 0.5f);
                    hyaw = 0.05f * MathF.Sin(ph * 0.5f);
                    break;
                }
            case BodyMotion.Spin: targetSpin = 2f * MathF.PI * 0.75f; break;
            case BodyMotion.Headbang: pitch = 0.45f * MathF.Sin(2f * MathF.PI * 2f * _t) + 0.1f; break;
            case BodyMotion.Jump:
                {
                    var u = _t % 1.1f;
                    off.Y = u < 0.6f ? 4f * 0.22f * (u / 0.6f) * (1f - u / 0.6f) : 0f;
                    break;
                }
        }
        _spinRate += (targetSpin - _spinRate) * MathF.Min(1f, dt * 1.5f);
        _yaw += _spinRate * dt;
        if (Motion != BodyMotion.Spin && MathF.Abs(_spinRate) < 0.05f)
        {
            // 정면 복귀: 각도 래핑 후 최대 1.2 rad/s 로 되감기(급회전 채찍 방지)
            _yaw = MathF.IEEERemainder(_yaw, 2f * MathF.PI);
            _yaw -= Math.Clamp(_yaw * 2f * dt, -1.2f * dt, 1.2f * dt);
        }
        _hair.Body.SetPose(_yaw, off, pitch, hyaw, leg);
        // 겉보기 바람 = 바람 − 몸 이동 속도(걷기 = 전진 1.3 m/s 상당)
        var bodyVel = (off - _prevOffset) / dt; _prevOffset = off;
        if (Motion == BodyMotion.Walk) bodyVel += new Vector3(MathF.Sin(_yaw), 0f, MathF.Cos(_yaw)) * 1.3f;
        var wind = WindVector - bodyVel * 0.5f;
        ApparentWind = wind;
        _hair.Wind = wind; _cloth.Wind = wind;
    }

    private void Render()
    {
        _shader.BuildShadow(_hair);
        var stride = Stride;
        _shader.ShadeHair(_hair, Eye, stride);
        _shader.ShadeCloth(_cloth, Eye);
        var hp = _hair.Body.HeadPose;
        Matrix4x4.Invert(hp, out var inv);
        _skin.Shade(_face, Vector3.Transform(Eye, inv));
        _shader.OverlaySkin(_face, _skin, hp);
        WriteSplats(stride);
    }

    private void WriteSplats(int stride)
    {
        _splats.Clear();
        var hp = _hair.Body.HeadPose;
        for (var v = 0; v < NeuralFacialMuscleSimulator.VertexCount; v++)
        {
            var n = Vector3.TransformNormal(_face.Normal(v), hp);
            _splats.Add(Vector3.Transform(_face.Position(v), hp), PhotorealisticMicroSkinSssSynthesizer.FromTo(Vector3.UnitZ, n),
                new Vector3(0.0028f, 0.0028f, 0.0005f), 0.95f, new Vector3(_shader.SkinR[v], _shader.SkinG[v], _shader.SkinB[v]));
        }
        for (var v = 0; v < GenerativeGarmentClothCollisionSynthesizer.MaxVerts; v++)
        {
            var n = _cloth.Normal(v);
            _splats.Add(_cloth.Position(v) + n * _cloth.WrinkleOffset[v], PhotorealisticMicroSkinSssSynthesizer.FromTo(Vector3.UnitZ, n),
                new Vector3(0.011f, 0.017f, 0.001f), 0.97f, new Vector3(_shader.ClothR[v], _shader.ClothG[v], _shader.ClothB[v]));
        }
        const int N = NeuralStrandHairDynamicsEngine.NodesPerStrand;
        for (var s = 0; s < _hair.ActiveStrands && _splats.Count + N - 1 <= _splats.Capacity; s += stride)
        {
            var b = s * N;
            for (var k = 0; k < N - 1; k++)
            {
                var a = _hair.Position(b + k); var c = _hair.Position(b + k + 1);
                var d = c - a; var l = d.Length();
                if (l < 1e-6f) continue;
                _splats.Add((a + c) * 0.5f, PhotorealisticMicroSkinSssSynthesizer.FromTo(Vector3.UnitZ, d / l), new Vector3(0.0012f, 0.0012f, 0.5f * l), 0.85f,
                    new Vector3(_shader.HairR[b + k], _shader.HairG[b + k], _shader.HairB[b + k]));
            }
        }
    }

    public void Start()
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(() => LoopAsync(cts.Token));
        LastAction = "▶ 헤어/의상 시뮬레이션 가동";
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
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("HairCloth.Tick", ex); }
                if ((Frame & 7) == 0) Raise();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("HairCloth.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
