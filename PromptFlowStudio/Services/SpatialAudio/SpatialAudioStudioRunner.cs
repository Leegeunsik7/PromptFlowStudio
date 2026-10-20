using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Neural3d;
using PromptFlowStudio.Services.OpenUsd;

namespace PromptFlowStudio.Services.SpatialAudio;

public enum AcousticRoomPreset : byte { ConcreteHall = 0, Studio, OutdoorForest }

/// <summary>
/// [PromptFlow Studio v16.76.0] 🎧 AI 공간 오디오 &amp; 음향 레이트레이싱 스튜디오 러너(48 kHz · 블록 1920 = 25 Hz · <see cref="TickOnce"/> 무할당).
/// 장면 = USDA(기둥 · 소파 · 유리창) + 3DGS 덤불(식생 산란) · 방 프리셋. 틱: 초점 음원 이동 시/주기적 음선 추적 → RT60·폐색 → 사운드스케이프 블록(도플러 · VBAP 7.1.4 · FDN) →
/// 객체별 HRTF 바이노럴(초점 음원은 초기 반사 탭 포함) + 잔향 L/R → 바이노럴 스테레오 출력 · 레벨 미터. 미리듣기 WAV(제어 평면).
/// </summary>
public sealed class SpatialAudioStudioRunner : IDisposable
{
    public const int SampleRate = 48000, Block = 1920, Hz = SampleRate / Block;

    public const string SceneUsda = """
#usda 1.0
(
    defaultPrim = "Hall"
    upAxis = "Y"
)

def Xform "Hall"
{
    def Cube "ConcretePillar"
    {
        double size = 1
        double3 xformOp:translate = (0, 0.5, -1)
        float3 xformOp:scale = (1, 1, 1)
    }
    def Cube "PillarTop"
    {
        double size = 1
        double3 xformOp:translate = (0, 1.5, -1)
    }
    def Cube "PillarTop2"
    {
        double size = 1
        double3 xformOp:translate = (0, 2.5, -1)
    }
    def Cube "FabricSofa"
    {
        double size = 0.8
        double3 xformOp:translate = (-3, 0.4, 2)
    }
    def Sphere "GlassGlobe"
    {
        double radius = 0.4
        double3 xformOp:translate = (3, 1.2, 1.5)
    }
}
""";

    private readonly object _gate = new();
    private readonly RealtimeAcousticRaytracingEngine _rt;
    private readonly Generative3DSoundscapeSynthesizer _syn;
    private readonly NeuralBinauralHrtfSpatializer _hrtf;
    private readonly OpenUsdSpatialAssetCompositorEngine _usd = new();
    private readonly UsdLayer _scene = new("hall.usda");
    private readonly GaussianScene _bushes = new(64);
    private readonly float[] _outL = new float[Block], _outR = new float[Block];
    private readonly EarlyReflection[] _taps = new EarlyReflection[NeuralBinauralHrtfSpatializer.MaxTaps];
    private CancellationTokenSource? _cts;
    private long _frame;
    private int _focus;
    private int _tapN;
    private Vector3 _listener = new(0, 1.6f, 2.5f), _tracedSrc;
    private float _yaw, _pitch, _peakL, _peakR, _rmsL, _rmsR;
    private bool _dirty = true, _virtualizeBed;
    private AcousticRoomPreset _room;

    public SpatialAudioStudioRunner(IServiceProvider sp)
        : this((RealtimeAcousticRaytracingEngine?)sp.GetService(typeof(RealtimeAcousticRaytracingEngine)) ?? new RealtimeAcousticRaytracingEngine(),
               (Generative3DSoundscapeSynthesizer?)sp.GetService(typeof(Generative3DSoundscapeSynthesizer)) ?? new Generative3DSoundscapeSynthesizer(SampleRate),
               (NeuralBinauralHrtfSpatializer?)sp.GetService(typeof(NeuralBinauralHrtfSpatializer)) ?? new NeuralBinauralHrtfSpatializer(SampleRate)) { }

    public SpatialAudioStudioRunner(RealtimeAcousticRaytracingEngine rt, Generative3DSoundscapeSynthesizer syn, NeuralBinauralHrtfSpatializer hrtf)
    {
        _rt = rt; _syn = syn; _hrtf = hrtf;
        UsdaReader.Parse(Encoding.UTF8.GetBytes(SceneUsda), _scene);
        _usd.SetLayer(0, _scene);
        for (var i = 0; i < 6; i++) _bushes.Add(new Vector3(-4.2f + 0.5f * i, 0.5f, -3.2f), Quaternion.Identity, new Vector3(0.2f), 0.8f, new Vector3(0.2f, 0.5f, 0.2f));
        SetRoom(AcousticRoomPreset.ConcreteHall);
        ApplyPrompt("카페 숲 새");
    }

    public event Action? OnChange;

    public RealtimeAcousticRaytracingEngine Raytracer => _rt;
    public Generative3DSoundscapeSynthesizer Soundscape => _syn;
    public NeuralBinauralHrtfSpatializer Hrtf => _hrtf;
    public bool IsRunning => _cts is not null;
    public long Frame { get { lock (_gate) return _frame; } }
    public AcousticRoomPreset Room => _room;
    public int Focus => _focus;
    public Vector3 Listener => _listener;
    public float HeadYaw => _yaw;
    public float HeadPitch => _pitch;
    public float PeakL => _peakL;
    public float PeakR => _peakR;
    public float RmsL => _rmsL;
    public float RmsR => _rmsR;
    public bool VirtualizeBed { get => _virtualizeBed; set => _virtualizeBed = value; }
    public ReadOnlySpan<float> OutputL => _outL;
    public ReadOnlySpan<float> OutputR => _outR;
    public string? LastAction { get; private set; }

    public void SetRoom(AcousticRoomPreset p)
    {
        lock (_gate)
        {
            _room = p;
            _rt.ClearObstacles();
            switch (p)
            {
                case AcousticRoomPreset.Studio:
                    _rt.SetRoom(new Vector3(-5, 0, -4), new Vector3(5, 3.5f, 4), AcousticMaterial.Curtain, AcousticMaterial.Carpet, AcousticMaterial.Wood);
                    break;
                case AcousticRoomPreset.OutdoorForest:
                    _rt.SetOutdoor(AcousticMaterial.Grass);
                    _rt.AddGaussians(_bushes);
                    break;
                default:
                    _rt.SetRoom(new Vector3(-5, 0, -4), new Vector3(5, 3.5f, 4), AcousticMaterial.Concrete);
                    break;
            }
            _rt.BuildFromUsd(_usd);
            var (mn, mx) = _rt.Room;
            _syn.SetRoomBounds(mn, mx);
            _dirty = true;
        }
        LastAction = "🏛 공간 · " + p;
        Raise();
    }

    public void ApplyPrompt(string prompt)
    {
        lock (_gate)
        {
            _syn.ApplyPrompt(prompt);
            for (var v = 0; v < NeuralBinauralHrtfSpatializer.MaxVoices; v++) _hrtf.ResetVoice(v);
            _focus = _syn.ObjectCount - 1;
            _dirty = true;
        }
        LastAction = _syn.LastAction;
        Raise();
    }

    /// <summary>트랙패드: 초점 음원 위치(x, z · 높이 y).</summary>
    public void MoveFocus(float x, float y, float z)
    {
        lock (_gate)
        {
            if (_focus < 0) return;
            _syn.SetPosition(_focus, new Vector3(x, y, z));
            _dirty = true;
        }
    }

    public void SetFocus(int i) { lock (_gate) { _focus = Math.Clamp(i, 0, Math.Max(0, _syn.ObjectCount - 1)); _dirty = true; } }

    public void SetHead(float yawDeg, float pitchDeg)
    {
        lock (_gate) { _yaw = yawDeg; _pitch = pitchDeg; _hrtf.SetHeadYawPitch(yawDeg, pitchDeg); }
    }

    public void SetListener(Vector3 p) { lock (_gate) { _listener = p; _dirty = true; } }

    /// <summary>1블록(무할당).</summary>
    public void TickOnce()
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("SpatialAudio.Tick");
            var focusPos = _focus >= 0 && _focus < _syn.ObjectCount ? _syn.Position(_focus) : _listener;
            if (_dirty || Vector3.DistanceSquared(focusPos, _tracedSrc) > 0.25f || _frame % 50 == 0)
            {
                _rt.Trace(focusPos, _listener);
                _tracedSrc = focusPos;
                _syn.SetReverb(0.5f * (_rt.Rt60(1) + _rt.Rt60(2)) is var lo && lo > 0 ? lo : 0.3f, 0.5f * (_rt.Rt60(4) + _rt.Rt60(5)) is var hi && hi > 0 ? hi : 0.2f);
                _tapN = Math.Min(_rt.ReflectionCount, _taps.Length);
                for (var k = 0; k < _tapN; k++) _taps[k] = _rt.Reflection(k);
                _dirty = false;
            }
            // 객체별 폐색(저/고역)
            for (var i = 0; i < _syn.ObjectCount; i++)
                if (_syn.IsActive(i)) _syn.SetOcclusion(i, _rt.OcclusionBetween(_syn.Position(i), _listener, 1), _rt.OcclusionBetween(_syn.Position(i), _listener, 5));
            _syn.ProcessBlock(_listener, Block);
            Array.Clear(_outL); Array.Clear(_outR);
            if (_virtualizeBed) _hrtf.VirtualizeBed(_syn.BedBuffer, Generative3DSoundscapeSynthesizer.MaxBlock, Block, _outL, _outR);
            else
            {
                for (var i = 0; i < _syn.ObjectCount; i++)
                {
                    if (!_syn.IsActive(i)) continue;
                    var dir = _syn.Position(i) - _listener;
                    if (i == _focus) _hrtf.ProcessVoice(i, _syn.ObjectSignal(i, Block), dir, 1f, _outL, _outR, _taps.AsSpan(0, _tapN), Vector3.Distance(_syn.Position(i), _listener) / AcousticBands.SpeedOfSound, MathF.Max(0.25f, _syn.Gain(i)));
                    else _hrtf.ProcessVoice(i, _syn.ObjectSignal(i, Block), dir, 1f, _outL, _outR);
                }
                var rl = _syn.ReverbL(Block); var rr = _syn.ReverbR(Block);
                for (var k = 0; k < Block; k++) { _outL[k] += rl[k] * 0.6f; _outR[k] += rr[k] * 0.6f; }
            }
            float pl = 0, pr = 0; double sl = 0, sr = 0;
            var mg = _syn.MasterGain;
            for (var k = 0; k < Block; k++)
            {
                var l = _outL[k] *= mg; var r = _outR[k] *= mg;
                pl = MathF.Max(pl, MathF.Abs(l)); pr = MathF.Max(pr, MathF.Abs(r)); sl += l * l; sr += r * r;
            }
            _peakL = pl; _peakR = pr; _rmsL = (float)Math.Sqrt(sl / Block); _rmsR = (float)Math.Sqrt(sr / Block);
            _frame++;
        }
    }

    /// <summary>바이노럴 미리듣기 WAV(16-bit 스테레오 · seconds 초 · 현재 상태에서 이어서 렌더). 제어 평면.</summary>
    public byte[] RenderPreviewWav(float seconds)
    {
        lock (_gate)
        {
            var blocks = Math.Max(1, (int)(seconds * Hz));
            var n = blocks * Block;
            var wav = new byte[44 + n * 4];
            "RIFF"u8.CopyTo(wav); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8);
            "WAVEfmt "u8.CopyTo(wav.AsSpan(8)); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1); BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 2);
            BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), SampleRate); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), SampleRate * 4);
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 4); BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
            "data"u8.CopyTo(wav.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), n * 4);
            var o = 44;
            for (var b = 0; b < blocks; b++)
            {
                TickOnce();
                for (var k = 0; k < Block; k++)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(o), (short)Math.Clamp(_outL[k] * 32767f, -32768f, 32767f));
                    BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(o + 2), (short)Math.Clamp(_outR[k] * 32767f, -32768f, 32767f));
                    o += 4;
                }
            }
            LastAction = "🎧 미리듣기 " + seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s";
            return wav;
        }
    }

    public void Start()
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(() => LoopAsync(cts.Token));
        LastAction = "▶ 공간 오디오 루프 가동";
        Raise();
    }

    public void Stop()
    {
        var c = Interlocked.Exchange(ref _cts, null);
        if (c is null) return;
        c.Cancel(); c.Dispose();
        LastAction = "⏸ 공간 오디오 루프 정지";
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
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("SpatialAudio.Tick", ex); }
                if ((Frame & 3) == 0) Raise();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("SpatialAudio.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
