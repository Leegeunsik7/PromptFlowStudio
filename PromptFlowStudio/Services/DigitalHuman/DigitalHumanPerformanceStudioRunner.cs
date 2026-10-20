using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Neural3d;

namespace PromptFlowStudio.Services.DigitalHuman;

/// <summary>
/// [PromptFlow Studio v16.82.0] 👤 AI 디지털 휴먼 &amp; 신경망 표정 스튜디오 러너(60 Hz · <see cref="TickOnce"/> 무할당).
/// 대사 → 감정선(정서가 · 각성 · 공포) → 표정 레이어 + 글자 단위 비짐(13 자/s) · 시선 가드(카메라 · 관객 3 · 회피) →
/// 근육 시뮬레이터(52 ARKit + FLAME 턱) → SSS 피부 셰이딩 → 3DGS 스플랫(얼굴 1920 + 안구 2×96 · 동공 지름/깜빡임 반영).
/// </summary>
public sealed class DigitalHumanPerformanceStudioRunner : IDisposable
{
    public const int Hz = 60, EyePoints = 96, CharsPerSecond = 13;
    public const float IrisRadiusMm = 6f;

    public static readonly string[] DefaultScript =
    {
        "안녕하세요, 오늘 와 주셔서 정말 고마워요!",
        "그날 밤… 너무 무서웠어요. 위험하다는 걸 알았거든요.",
        "미안해요. 그때 곁에 있어 주지 못해서 슬퍼요.",
        "세상에, 진짜요? 깜짝 놀랐어요!",
        "더는 참지 않겠어요. 정말 화가 나요.",
    };

    private readonly object _gate = new();
    private readonly NeuralFacialMuscleSimulator _face;
    private readonly PhotorealisticMicroSkinSssSynthesizer _skin;
    private readonly AutonomousDigitalHumanGazeGuard _gaze;
    private readonly GaussianScene _splats = new(NeuralFacialMuscleSimulator.VertexCount + 2 * EyePoints);
    private readonly Vector3[] _eyePts = new Vector3[EyePoints];
    private CancellationTokenSource? _cts;
    private long _frame;
    private string? _line;
    private int _cursor;
    private float _charT;
    private Vector3 _camera = new(0f, 0.02f, 0.75f);

    public DigitalHumanPerformanceStudioRunner(IServiceProvider sp)
        : this((NeuralFacialMuscleSimulator?)sp.GetService(typeof(NeuralFacialMuscleSimulator)) ?? new NeuralFacialMuscleSimulator(),
               (PhotorealisticMicroSkinSssSynthesizer?)sp.GetService(typeof(PhotorealisticMicroSkinSssSynthesizer)) ?? new PhotorealisticMicroSkinSssSynthesizer(),
               (AutonomousDigitalHumanGazeGuard?)sp.GetService(typeof(AutonomousDigitalHumanGazeGuard)) ?? new AutonomousDigitalHumanGazeGuard()) { }

    public DigitalHumanPerformanceStudioRunner(NeuralFacialMuscleSimulator face, PhotorealisticMicroSkinSssSynthesizer skin, AutonomousDigitalHumanGazeGuard gaze)
    {
        _face = face; _skin = skin; _gaze = gaze;
        _skin.Bind(_face);
        // 안구 표면 점(피보나치 구 · 전면 캡)
        var golden = MathF.PI * (3f - MathF.Sqrt(5f));
        for (var i = 0; i < EyePoints; i++)
        {
            var z = 1f - 1.2f * (i + 0.5f) / EyePoints; var r = MathF.Sqrt(MathF.Max(0f, 1f - z * z)); var a = golden * i;
            _eyePts[i] = new Vector3(r * MathF.Cos(a), r * MathF.Sin(a), z);
        }
        NewTake();
    }

    public event Action? OnChange;

    public NeuralFacialMuscleSimulator Face => _face;
    public PhotorealisticMicroSkinSssSynthesizer Skin => _skin;
    public AutonomousDigitalHumanGazeGuard Gaze => _gaze;
    public GaussianScene Splats => _splats;
    public bool IsRunning => _cts is not null;
    public long Frame { get { lock (_gate) return _frame; } }
    public Vector3 CameraPosition => _camera;
    public string? Line => _line;
    public int Cursor => _cursor;
    public bool Speaking => _line is not null;
    public DialogueEmotion Emotion { get; private set; } = AutonomousDigitalHumanGazeGuard.AnalyzeDialogue(null);
    public string? LastAction { get; private set; }

    /// <summary>제어 평면: 새 테이크(표정 · 시선 · 표적 초기화).</summary>
    public void NewTake()
    {
        lock (_gate)
        {
            _face.Reset();
            _gaze.Reset();
            _gaze.ClearTargets();
            _gaze.AddTarget(_camera, 1.6f, camera: true);
            _gaze.AddTarget(new Vector3(-0.6f, -0.08f, 1.4f), 0.8f, camera: false);
            _gaze.AddTarget(new Vector3(0.55f, -0.05f, 1.5f), 0.8f, camera: false);
            _gaze.AddTarget(new Vector3(0.15f, 0.2f, 2.2f), 0.5f, camera: false);
            _gaze.SetEmotion(0.55f, 0.4f, 0.05f); _gaze.SnapEmotion();
            _line = null; _cursor = 0; _charT = 0f; _frame = 0;
            Emotion = AutonomousDigitalHumanGazeGuard.AnalyzeDialogue(null);
            TickCore(0f);
        }
        LastAction = "🎬 새 테이크 — 카메라 · 관객 3 표적";
        Raise();
    }

    /// <summary>대사 연기: 감정선 분석 → 표정 혼합 · 시선 감정 가중 · 비짐 재생.</summary>
    public DialogueEmotion Perform(string? line)
    {
        var e = AutonomousDigitalHumanGazeGuard.AnalyzeDialogue(line);
        lock (_gate)
        {
            Emotion = e;
            _face.BlendEmotion(e.Valence, e.Arousal, e.Fear);
            _gaze.SetEmotion(e.Valence, e.Arousal, e.Fear);
            _line = string.IsNullOrWhiteSpace(line) ? null : line;
            _cursor = 0; _charT = 0f;
            _gaze.Speaking = _line is not null;
        }
        LastAction = "🎙 대사 연기 · 감정 " + e.Label;
        Raise();
        return e;
    }

    /// <summary>v17.56 — 3DGS 환경광 역추출 결과 주입(앰비언트 · 림 · 바운스 → 피부 셰이더 채널 · 외부 스레드 · 무할당).</summary>
    public void ApplyEnvironmentLight(System.Numerics.Vector3 ambient, System.Numerics.Vector3 rimColor, System.Numerics.Vector3 rimDir, System.Numerics.Vector3 bounce)
    {
        lock (_gate)
        {
            _skin.Ambient = ambient; _skin.RimColor = rimColor;
            if (rimDir.LengthSquared() > 1e-6f) _skin.RimDir = System.Numerics.Vector3.Normalize(rimDir);
            _skin.FillColor = bounce;
            EnvironmentLightUpdates++;
        }
    }

    public long EnvironmentLightUpdates { get; private set; }

    public void SetPreset(FacePreset p)
    {
        lock (_gate)
        {
            _face.SetExpression(p);
            var (v, a, f) = p switch
            {
                FacePreset.Joy => (0.85f, 0.6f, 0f), FacePreset.Sadness => (0.15f, 0.2f, 0.05f), FacePreset.Anger => (0.1f, 0.85f, 0.05f),
                FacePreset.Surprise => (0.55f, 0.9f, 0.1f), FacePreset.Fear => (0.2f, 0.85f, 0.85f), FacePreset.Disgust => (0.2f, 0.55f, 0.05f),
                _ => (0.5f, 0.4f, 0.05f),
            };
            _gaze.SetEmotion(v, a, f);
        }
        LastAction = "🎭 표정 프리셋 " + PresetName(p);
        Raise();
    }

    public static string PresetName(FacePreset p) => p switch
    {
        FacePreset.Joy => "기쁨", FacePreset.Sadness => "슬픔", FacePreset.Anger => "분노", FacePreset.Surprise => "놀람",
        FacePreset.Fear => "공포", FacePreset.Disgust => "혐오", _ => "중립",
    };

    public void SetManual(ArkitBlendshape k, float w) { lock (_gate) _face.SetLayer(FaceLayer.Manual, k, w); Raise(); }
    public void ClearManual() { lock (_gate) _face.ClearLayer(FaceLayer.Manual); LastAction = "↺ 수동 레이어 초기화"; Raise(); }
    public void SetIdentity(int k, float beta) { lock (_gate) _face.SetIdentity(k, beta); Raise(); }
    public void SetLook(SkinLook l) { lock (_gate) _skin.SetLook(l); LastAction = "🧴 피부 룩 " + PhotorealisticMicroSkinSssSynthesizer.LookName(l); Raise(); }

    /// <summary>카메라 위치(머리 기준 · 좌우 오프셋 m) → 시선 표적 0 이동.</summary>
    public void SetCameraOffset(float x)
    {
        lock (_gate) { _camera = new Vector3(Math.Clamp(x, -1.2f, 1.2f), 0.02f, 0.75f); _gaze.MoveTarget(0, _camera); }
        Raise();
    }

    public void SetLuminance(float cdm2) { _gaze.Luminance = Math.Clamp(cdm2, 0.01f, 10000f); Raise(); }

    /// <summary>1프레임(무할당): 비짐 진행 → 시선 가드 → 근육 → SSS → 3DGS.</summary>
    public void TickOnce()
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("DigitalHuman.Tick");
            TickCore(1f / Hz);
        }
    }

    private void TickCore(float dt)
    {
        if (_line is not null && dt > 0f)
        {
            _charT += dt;
            const float per = 1f / CharsPerSecond;
            while (_charT >= per) { _charT -= per; _cursor++; }
            if (_cursor >= _line.Length) { _line = null; _cursor = 0; _gaze.Speaking = false; _face.SetSpeech(Viseme.Rest, 0f); }
            else _face.SetSpeech(NeuralFacialMuscleSimulator.VisemeOf(_line[_cursor]), 0.8f + 0.25f * _gaze.Arousal);
        }
        _gaze.Step(dt);
        _gaze.WriteToFace(_face);
        _face.Step(dt);
        _skin.Shade(_face, _camera);
        WriteSplats();
        if (dt > 0f) _frame++;
    }

    private void WriteSplats()
    {
        _splats.Clear();
        _skin.WriteSplats(_face, _splats);
        var blink = _gaze.Blink;
        var open = Math.Clamp(1f - 1.4f * blink, 0f, 1f);
        var pupilR = 0.5f * _gaze.PupilMm * 1e-3f; var irisR = IrisRadiusMm * 1e-3f;
        const float R = NeuralFacialMuscleSimulator.EyeRadius;
        for (var side = 1; side >= -1; side -= 2)
        {
            var c = NeuralFacialMuscleSimulator.EyeCenter(side);
            var q = PhotorealisticMicroSkinSssSynthesizer.FromTo(Vector3.UnitZ, _gaze.EyeDirection(side));
            for (var i = 0; i < EyePoints; i++)
            {
                var l = _eyePts[i];
                var lat = MathF.Sqrt(l.X * l.X + l.Y * l.Y) * R;
                var rgb = l.Z <= 0f ? new Vector3(0.82f, 0.78f, 0.76f)
                        : lat < pupilR ? new Vector3(0.02f, 0.02f, 0.025f)
                        : lat < irisR ? new Vector3(0.3f, 0.2f, 0.12f) + (lat / irisR) * new Vector3(0.1f, 0.08f, 0.04f)
                        : new Vector3(0.9f, 0.88f, 0.86f);
                var p = c + Vector3.Transform(l, q) * R;
                _splats.Add(p, q, new Vector3(0.0016f, 0.0016f, 0.0004f), 0.98f * open, rgb);
            }
        }
    }

    public void Start()
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(() => LoopAsync(cts.Token));
        LastAction = "▶ 디지털 휴먼 연기 가동";
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
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("DigitalHuman.Tick", ex); }
                if ((Frame & 7) == 0) Raise();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("DigitalHuman.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
