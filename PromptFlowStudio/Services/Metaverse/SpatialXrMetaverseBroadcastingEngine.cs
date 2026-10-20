using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Neural3d;
using PromptFlowStudio.Services.World;

namespace PromptFlowStudio.Services.Metaverse;

public enum XrDeviceKind : byte { VisionPro = 0, Quest3 = 1, WebXr = 2 }

/// <summary>6DoF 포즈(카메라 기준 +X 오른쪽 · +Y 위 · +Z 전방 — <see cref="SplatCamera"/> 규약과 동일).</summary>
public readonly record struct XrPose(Vector3 Position, Quaternion Orientation)
{
    public static readonly XrPose Identity = new(Vector3.Zero, Quaternion.Identity);
    public Vector3 Right => Vector3.Transform(Vector3.UnitX, Orientation);
    public Vector3 Up => Vector3.Transform(Vector3.UnitY, Orientation);
    public Vector3 Forward => Vector3.Transform(Vector3.UnitZ, Orientation);

    /// <summary>요(+ = 오른쪽) · 피치(+ = 위) deg — <see cref="SplatCamera.FromYawPitch"/>와 같은 시선.</summary>
    public static XrPose FromYawPitch(Vector3 position, float yawDeg, float pitchDeg)
        => new(position, Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitY, yawDeg * (MathF.PI / 180f)) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, -pitchDeg * (MathF.PI / 180f))));
}

public enum SceneApplyResult : byte { Applied = 0, Stale, Gap, Invalid }

/// <summary>와이어 공용 헬퍼 — 쿼터니언 smallest-three(7B) · 단위 벡터 i16×3(6B).</summary>
public static class XrWire
{
    private const float QMax = 0.70710678f;

    private static float Comp(in Quaternion q, int i) => i switch { 0 => q.X, 1 => q.Y, 2 => q.Z, _ => q.W };

    public static void WriteQuat(Span<byte> d, Quaternion q)
    {
        q = Quaternion.Normalize(q);
        var idx = 0; var best = MathF.Abs(q.X);
        if (MathF.Abs(q.Y) > best) { best = MathF.Abs(q.Y); idx = 1; }
        if (MathF.Abs(q.Z) > best) { best = MathF.Abs(q.Z); idx = 2; }
        if (MathF.Abs(q.W) > best) idx = 3;
        var sign = Comp(q, idx) < 0f ? -1f : 1f;
        d[0] = (byte)idx;
        var o = 1;
        for (var i = 0; i < 4; i++)
        {
            if (i == idx) continue;
            var v = Math.Clamp(Comp(q, i) * sign / QMax, -1f, 1f);
            BinaryPrimitives.WriteInt16LittleEndian(d[o..], (short)MathF.Round(v * 32767f));
            o += 2;
        }
    }

    public static Quaternion ReadQuat(ReadOnlySpan<byte> s)
    {
        var idx = s[0] & 3;
        float a = BinaryPrimitives.ReadInt16LittleEndian(s[1..]) / 32767f * QMax;
        float b = BinaryPrimitives.ReadInt16LittleEndian(s[3..]) / 32767f * QMax;
        float c = BinaryPrimitives.ReadInt16LittleEndian(s[5..]) / 32767f * QMax;
        var l = MathF.Sqrt(MathF.Max(0f, 1f - a * a - b * b - c * c));
        var q = idx switch { 0 => new Quaternion(l, a, b, c), 1 => new Quaternion(a, l, b, c), 2 => new Quaternion(a, b, l, c), _ => new Quaternion(a, b, c, l) };
        return Quaternion.Normalize(q);
    }

    public static void WriteUnit(Span<byte> d, Vector3 v)
    {
        var n = v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : Vector3.UnitZ;
        BinaryPrimitives.WriteInt16LittleEndian(d, (short)MathF.Round(n.X * 32767f));
        BinaryPrimitives.WriteInt16LittleEndian(d[2..], (short)MathF.Round(n.Y * 32767f));
        BinaryPrimitives.WriteInt16LittleEndian(d[4..], (short)MathF.Round(n.Z * 32767f));
    }

    public static Vector3 ReadUnit(ReadOnlySpan<byte> s)
    {
        var v = new Vector3(BinaryPrimitives.ReadInt16LittleEndian(s), BinaryPrimitives.ReadInt16LittleEndian(s[2..]), BinaryPrimitives.ReadInt16LittleEndian(s[4..])) / 32767f;
        return v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : Vector3.UnitZ;
    }

    public static void WriteVec(Span<byte> d, Vector3 v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(d, v.X);
        BinaryPrimitives.WriteSingleLittleEndian(d[4..], v.Y);
        BinaryPrimitives.WriteSingleLittleEndian(d[8..], v.Z);
    }

    public static Vector3 ReadVec(ReadOnlySpan<byte> s)
        => new(BinaryPrimitives.ReadSingleLittleEndian(s), BinaryPrimitives.ReadSingleLittleEndian(s[4..]), BinaryPrimitives.ReadSingleLittleEndian(s[8..]));

    /// <summary>쿼터니언 각도 차(deg · 부호 무관).</summary>
    public static float AngleDeg(Quaternion a, Quaternion b)
    {
        var d = MathF.Min(1f, MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))));
        return 2f * MathF.Acos(d) * (180f / MathF.PI);
    }
}

/// <summary>수신 측 3D 씬 상태 미러(키/델타 패킷 적용 · 손실 시 키 대기) — 무할당.</summary>
public sealed class SpatialSceneMirror
{
    private readonly Vector3[] _pos = new Vector3[SpatialXrMetaverseBroadcastingEngine.MaxEntities];
    private readonly Quaternion[] _rot = new Quaternion[SpatialXrMetaverseBroadcastingEngine.MaxEntities];
    private readonly byte[] _state = new byte[SpatialXrMetaverseBroadcastingEngine.MaxEntities];   // 0 = 없음 · 1 = 숨김 · 2 = 표시
    private uint _lastSeq;
    private bool _synced;

    public long Applied { get; private set; }
    public long Gaps { get; private set; }
    public long Stale { get; private set; }
    public bool NeedsKey => !_synced;
    public uint LastSeq => _lastSeq;
    public long LastTimeUs { get; private set; }

    public bool Has(int id) => _state[id] != 0;
    public bool Visible(int id) => _state[id] == 2;
    public Vector3 Position(int id) => _pos[id];
    public Quaternion Rotation(int id) => _rot[id];

    public SceneApplyResult Apply(ReadOnlySpan<byte> p)
    {
        const int H = SpatialXrMetaverseBroadcastingEngine.HeaderSize, R = SpatialXrMetaverseBroadcastingEngine.Rec;
        if (p.Length < H || !p[..4].SequenceEqual("XRS1"u8)) return SceneApplyResult.Invalid;
        var key = (p[4] & 1) != 0;
        int n = BinaryPrimitives.ReadUInt16LittleEndian(p[6..]);
        var seq = BinaryPrimitives.ReadUInt32LittleEndian(p[8..]);
        var baseSeq = BinaryPrimitives.ReadUInt32LittleEndian(p[12..]);
        if (p.Length < H + n * R) return SceneApplyResult.Invalid;
        if (_synced && (int)(seq - _lastSeq) <= 0) { Stale++; return SceneApplyResult.Stale; }
        if (!key && (!_synced || baseSeq != _lastSeq)) { _synced = false; Gaps++; return SceneApplyResult.Gap; }
        if (key) Array.Clear(_state);
        for (var i = 0; i < n; i++)
        {
            var r = p.Slice(H + i * R, R);
            int id = BinaryPrimitives.ReadUInt16LittleEndian(r);
            if (id >= _state.Length) continue;
            var fl = r[2];
            if ((fl & 2) != 0) { _state[id] = 0; continue; }
            _state[id] = (byte)((fl & 1) != 0 ? 2 : 1);
            _pos[id] = XrWire.ReadVec(r[3..]);
            _rot[id] = XrWire.ReadQuat(r[15..]);
        }
        _lastSeq = seq; _synced = true;
        LastTimeUs = BinaryPrimitives.ReadInt64LittleEndian(p[16..]);
        Applied++;
        return SceneApplyResult.Applied;
    }
}

/// <summary>
/// [PromptFlow Studio v16.61.0] 🥽 실시간 공간 XR 메타버스 스트리밍 &amp; 3D 씬 동기화 엔진(Pure BCL · 무할당 틱).
///   ▸ 6DoF 가상 스튜디오: v16.58 3DGS 장면(스테이지 스플랫 · 외부 PLY/3DGS 스튜디오 임포트) + v16.54 4D 월드 객체를 엔티티별 가우시안 클러스터로 실시간 미러링.
///   ▸ 스테레오 렌더: 헤드 포즈 ± IPD/2 좌/우 눈 <see cref="SplatCamera"/> → v16.58 라스터라이저 · 눈별 RGB/깊이(홀로그램 오클루전용).
///   ▸ 지연 보상: 최근 두 헤드 샘플의 선속도 + 각속도(축-각 외삽)로 모션-투-포톤 예측.
///   ▸ 씬 상태 스트림 "XRS1": 키/델타(1mm · 0.25° 임계) · baseSeq 체인(손실 즉시 감지 → 다음 키 프레임까지 보류) · smallest-three 쿼터니언 · gRPC 길이 접두 프레이밍 · WebXR 뷰어 포즈 UTF-8 JSON · UDP 송출.
/// </summary>
public sealed class SpatialXrMetaverseBroadcastingEngine : IDisposable
{
    public const int MaxEntities = 128, CameraEntity = MaxEntities - 1, SplatsPerObject = 26, Rec = 22, HeaderSize = 24;
    public const int MaxPacketSize = HeaderSize + MaxEntities * Rec;
    public const float PosEpsilon = 0.001f, RotEpsilonDeg = 0.25f;

    private readonly object _gate = new();
    private readonly AiGaussianSplattingRasterizerEngine _ras;
    private readonly GaussianScene _scene = new(4096);
    private readonly Vector3[] _sphereOff = new Vector3[SplatsPerObject], _boxOff = new Vector3[SplatsPerObject];
    private readonly Vector3[] _pos = new Vector3[MaxEntities], _txPos = new Vector3[MaxEntities];
    private readonly Quaternion[] _rot = new Quaternion[MaxEntities], _txRot = new Quaternion[MaxEntities];
    private readonly byte[] _state = new byte[MaxEntities], _txState = new byte[MaxEntities];
    private readonly byte[] _packet = new byte[MaxPacketSize];
    private byte[] _eyeL = Array.Empty<byte>(), _eyeR = Array.Empty<byte>();
    private float[] _depL = Array.Empty<float>(), _depR = Array.Empty<float>();
    private int _eyeW, _eyeH, _objBase, _objSlots;
    private XrPose _head = XrPose.FromYawPitch(new Vector3(0f, 1.6f, -3f), 0f, -8f), _prevHead;
    private long _headUs, _prevHeadUs;
    private uint _seq, _lastKeySeq;
    private bool _forceKey = true;
    private Socket? _udp;
    private long _keyFrames, _deltaFrames, _bytesOut, _packets, _sendErrors, _frames;
    private int _lastBytes, _lastEntities, _visible;
    private double _encodeUs, _renderMs;

    public SpatialXrMetaverseBroadcastingEngine(int eyeWidth = 160, int eyeHeight = 120, AiGaussianSplattingRasterizerEngine? rasterizer = null)
    {
        _ras = rasterizer ?? new AiGaussianSplattingRasterizerEngine();
        var golden = MathF.PI * (3f - MathF.Sqrt(5f));
        for (var k = 0; k < SplatsPerObject; k++)
        {
            var y = 1f - 2f * (k + 0.5f) / SplatsPerObject;
            var rr = MathF.Sqrt(1f - y * y);
            var s = new Vector3(MathF.Cos(golden * k) * rr, y, MathF.Sin(golden * k) * rr);
            _sphereOff[k] = s * 0.8f;
            var m = MathF.Max(MathF.Abs(s.X), MathF.Max(MathF.Abs(s.Y), MathF.Abs(s.Z)));
            _boxOff[k] = s / m * 0.85f;
        }
        SetEyeResolution(eyeWidth, eyeHeight);
        BuildDefaultStage();
    }

    public event Action? OnChange;

    public GaussianScene Scene => _scene;
    public int StaticSplats => _objBase;
    public int EyeWidth => _eyeW;
    public int EyeHeight => _eyeH;
    public float Ipd { get; set; } = 0.063f;
    public float HfovDeg { get; set; } = 90f;
    public float PredictionMs { get; set; } = 20f;
    public bool Prediction { get; set; } = true;
    public int KeyInterval { get; set; } = 60;
    public XrDeviceKind Device { get; set; } = XrDeviceKind.VisionPro;
    public XrPose Head { get { lock (_gate) return _head; } }
    public long Frames => Interlocked.Read(ref _frames);
    public long KeyFrames => Interlocked.Read(ref _keyFrames);
    public long DeltaFrames => Interlocked.Read(ref _deltaFrames);
    public long BytesOut => Interlocked.Read(ref _bytesOut);
    public long PacketsSent => Interlocked.Read(ref _packets);
    public long SendErrors => Interlocked.Read(ref _sendErrors);
    public int LastPacketBytes => Volatile.Read(ref _lastBytes);
    public int LastEntities => Volatile.Read(ref _lastEntities);
    public int LastVisibleSplats => Volatile.Read(ref _visible);
    public double LastEncodeMicros => Volatile.Read(ref _encodeUs);
    public double LastRenderMs => Volatile.Read(ref _renderMs);
    public bool UdpEnabled => _udp is not null;
    public uint Sequence => _seq;
    public string? LastAction { get; private set; }

    /// <summary>눈별 프레임 버퍼(렌더 직후 유효 · 같은 스레드에서 읽기).</summary>
    public ReadOnlySpan<byte> LeftRgb => _eyeL;
    public ReadOnlySpan<byte> RightRgb => _eyeR;
    public ReadOnlySpan<float> LeftDepth => _depL;
    public ReadOnlySpan<float> RightDepth => _depR;
    internal byte[] LeftBuffer => _eyeL;
    internal byte[] RightBuffer => _eyeR;

    public void SetEyeResolution(int w, int h)
    {
        lock (_gate)
        {
            _eyeW = Math.Clamp(w, 32, 1920); _eyeH = Math.Clamp(h, 32, 1920);
            _eyeL = new byte[_eyeW * _eyeH * 3]; _eyeR = new byte[_eyeW * _eyeH * 3];
            _depL = new float[_eyeW * _eyeH]; _depR = new float[_eyeW * _eyeH];
        }
    }

    // ══════════════════════ 6DoF 스테이지 ══════════════════════

    /// <summary>기본 홀로 스테이지(바닥 원판 + 원통형 백드롭) + 4D 객체 클러스터 슬롯.</summary>
    public void BuildDefaultStage()
    {
        lock (_gate)
        {
            _scene.Clear();
            for (var gz = -12; gz <= 12; gz++)
                for (var gx = -12; gx <= 12; gx++)
                {
                    float x = gx * 0.3f, z = gz * 0.3f;
                    if (x * x + z * z > 3.6f * 3.6f) continue;
                    var chk = ((gx + gz) & 1) == 0;
                    _scene.Add(new Vector3(x, 0f, z), Quaternion.Identity, new Vector3(0.16f, 0.012f, 0.16f), 0.9f, chk ? new Vector3(0.12f, 0.14f, 0.22f) : new Vector3(0.2f, 0.24f, 0.34f));
                }
            for (var c = 0; c < 48; c++)
            {
                var a = c * (MathF.PI * 2f / 48f);
                var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -a);
                for (var r = 0; r < 8; r++)
                {
                    var t = r / 7f;
                    _scene.Add(new Vector3(MathF.Sin(a) * 5f, 0.2f + r * 0.4f, MathF.Cos(a) * 5f), q, new Vector3(0.36f, 0.24f, 0.04f), 0.85f,
                        new Vector3(0.35f - 0.25f * t, 0.15f + 0.5f * t, 0.55f + 0.35f * t));
                }
            }
            ReserveObjectSlots();
        }
        LastAction = "🏟 기본 홀로 스테이지";
    }

    /// <summary>v16.58 3DGS 장면 임포트(정적 영역 교체 · 상한 초과 시 균등 솎음) — 반환 = 임포트 스플랫 수.</summary>
    public int ImportStage(GaussianScene src, int maxSplats = 30000, float scale = 1f, Vector3 offset = default)
    {
        if (src.Count == 0) return 0;
        var step = Math.Max(1, (src.Count + maxSplats - 1) / maxSplats);
        var n = 0;
        lock (_gate)
        {
            _scene.Clear();
            for (var i = 0; i < src.Count; i += step)
            {
                var j = _scene.Add(src.Position(i) * scale + offset, src.Rotation(i), src.ScaleOf(i) * scale, src.Opacity[i], Vector3.One * 0.5f);
                src.Sh.AsSpan(i * GaussianScene.ShStride, GaussianScene.ShStride).CopyTo(_scene.Sh.AsSpan(j * GaussianScene.ShStride));
                n++;
            }
            ReserveObjectSlots();
        }
        LastAction = "📥 3DGS 임포트 " + n.ToString(CultureInfo.InvariantCulture) + " 스플랫";
        Raise();
        return n;
    }

    private void ReserveObjectSlots()
    {
        _objBase = _scene.Count;
        _objSlots = AiGenerative4dWorldModelEngine.MaxObjects;
        _scene.EnsureCapacity(_objBase + _objSlots * SplatsPerObject);
        for (var i = 0; i < _objSlots * SplatsPerObject; i++) _scene.Add(Vector3.Zero, Quaternion.Identity, new Vector3(0.05f), 0f, Vector3.One);
        _forceKey = true;
    }

    /// <summary>4D 월드 객체 → 엔티티 포즈 + 가우시안 클러스터 미러(무할당).</summary>
    public void SyncWorld(AiGenerative4dWorldModelEngine world)
    {
        lock (_gate)
        {
            var n = Math.Min(world.ObjectCount, _objSlots);
            for (var o = 0; o < _objSlots; o++)
            {
                var b = _objBase + o * SplatsPerObject;
                if (o >= n)
                {
                    _scene.Opacity.AsSpan(b, SplatsPerObject).Clear();
                    if (_state[o] != 0) _state[o] = 0;
                    continue;
                }
                var (px, py, pz) = world.Position(o);
                var (hx, hy, hz) = world.Half(o);
                var (cr, cg, cb) = world.Color(o);
                var hidden = world.IsHidden(o);
                var c = new Vector3(px, py, pz); var h = new Vector3(hx, hy, hz);
                var offs = world.Shape(o) == WorldShape.Box ? _boxOff : _sphereOff;
                var sc = h * 0.55f;
                for (var k = 0; k < SplatsPerObject; k++)
                {
                    var i = b + k;
                    var p = c + offs[k] * h;
                    _scene.Pos[i * 3] = p.X; _scene.Pos[i * 3 + 1] = p.Y; _scene.Pos[i * 3 + 2] = p.Z;
                    _scene.Scale[i * 3] = sc.X; _scene.Scale[i * 3 + 1] = sc.Y; _scene.Scale[i * 3 + 2] = sc.Z;
                    _scene.Opacity[i] = hidden ? 0f : 0.92f;
                    var sh = i * GaussianScene.ShStride;
                    _scene.Sh[sh] = GaussianScene.ToDc(cr); _scene.Sh[sh + 1] = GaussianScene.ToDc(cg); _scene.Sh[sh + 2] = GaussianScene.ToDc(cb);
                }
                _pos[o] = c; _rot[o] = Quaternion.Identity;
                _state[o] = (byte)(hidden ? 1 : 2);
            }
            _scene.Version++;
        }
    }

    public void SetEntity(int id, Vector3 position, Quaternion rotation, bool visible)
    {
        if ((uint)id >= MaxEntities) return;
        lock (_gate) { _pos[id] = position; _rot[id] = rotation; _state[id] = (byte)(visible ? 2 : 1); }
    }

    public void RemoveEntity(int id) { if ((uint)id < MaxEntities) lock (_gate) _state[id] = 0; }

    public bool HasEntity(int id) => (uint)id < MaxEntities && _state[id] != 0;
    public Vector3 EntityPosition(int id) => _pos[id];

    // ══════════════════════ 헤드 포즈 · 예측 ══════════════════════

    public void UpdateHead(in XrPose pose, long timestampUs)
    {
        lock (_gate)
        {
            _prevHead = _head; _prevHeadUs = _headUs;
            _head = new XrPose(pose.Position, Quaternion.Normalize(pose.Orientation)); _headUs = timestampUs;
            _pos[CameraEntity] = _head.Position; _rot[CameraEntity] = _head.Orientation; _state[CameraEntity] = 2;
        }
    }

    /// <summary>선속도 + 각속도 외삽(aheadMs 후 포즈).</summary>
    public XrPose PredictHead(float aheadMs)
    {
        lock (_gate)
        {
            var dtUs = _headUs - _prevHeadUs;
            if (_prevHeadUs <= 0 || dtUs <= 0 || dtUs > 250_000 || aheadMs <= 0f) return _head;
            var k = aheadMs * 1000f / dtUs;
            var pos = _head.Position + (_head.Position - _prevHead.Position) * k;
            var dq = _head.Orientation * Quaternion.Inverse(_prevHead.Orientation);
            if (dq.W < 0f) dq = new Quaternion(-dq.X, -dq.Y, -dq.Z, -dq.W);
            var angle = 2f * MathF.Acos(Math.Clamp(dq.W, -1f, 1f));
            var s = MathF.Sqrt(MathF.Max(0f, 1f - dq.W * dq.W));
            if (s < 1e-6f || angle < 1e-6f) return new XrPose(pos, _head.Orientation);
            var axis = new Vector3(dq.X, dq.Y, dq.Z) / s;
            var ext = Quaternion.CreateFromAxisAngle(axis, angle * k);
            return new XrPose(pos, Quaternion.Normalize(ext * _head.Orientation));
        }
    }

    public void EyeCameras(in XrPose head, out SplatCamera left, out SplatCamera right)
    {
        Vector3 r = head.Right, u = head.Up, f = head.Forward;
        var fx = _eyeW * 0.5f / MathF.Tan(Math.Clamp(HfovDeg, 30f, 150f) * MathF.PI / 360f);
        var half = r * (Ipd * 0.5f);
        left = new SplatCamera(head.Position - half, r, u, f, fx, fx, _eyeW * 0.5f, _eyeH * 0.5f, _eyeW, _eyeH);
        right = new SplatCamera(head.Position + half, r, u, f, fx, fx, _eyeW * 0.5f, _eyeH * 0.5f, _eyeW, _eyeH);
    }

    /// <summary>예측 헤드 포즈로 좌/우 눈 렌더(무할당) — 반환 = 좌안 가시 스플랫.</summary>
    public int RenderStereo(out SplatCamera left, out SplatCamera right)
    {
        var t0 = Stopwatch.GetTimestamp();
        var head = Prediction ? PredictHead(PredictionMs) : Head;
        int vis;
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Xr.Metaverse.RenderStereo");
            EyeCameras(head, out left, out right);
            var opt = new SplatRenderOptions(Near: 0.05f, Far: 60f, MinOpacity: 0.004f, Density: 1f, ScaleMultiplier: 1f, Background: new Vector3(0.015f, 0.02f, 0.05f));
            vis = _ras.Render(_scene, left, opt, _eyeL, _depL);
            _ras.Render(_scene, right, opt, _eyeR, _depR);
        }
        Volatile.Write(ref _visible, vis);
        Volatile.Write(ref _renderMs, (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
        Interlocked.Increment(ref _frames);
        return vis;
    }

    // ══════════════════════ 씬 상태 스트림 ══════════════════════

    public void RequestKeyFrame() => _forceKey = true;

    /// <summary>"XRS1" 키/델타 패킷 인코딩(무할당 · dst ≥ <see cref="MaxPacketSize"/>) — 반환 = 바이트.</summary>
    public int EncodeScene(Span<byte> dst, long timestampUs, bool forceKey = false)
    {
        if (dst.Length < MaxPacketSize) throw new ArgumentException("dst < MaxPacketSize", nameof(dst));
        var t0 = Stopwatch.GetTimestamp();
        int n = 0, o = HeaderSize;
        bool key;
        uint seq, baseSeq;
        lock (_gate)
        {
            key = forceKey || _forceKey || _seq == 0 || _seq - _lastKeySeq >= (uint)Math.Max(1, KeyInterval);
            baseSeq = _seq;
            seq = ++_seq;
            for (var id = 0; id < MaxEntities; id++)
            {
                var st = _state[id];
                if (!key)
                {
                    if (st == 0 && _txState[id] == 0) continue;
                    if (st == _txState[id] && Vector3.DistanceSquared(_pos[id], _txPos[id]) < PosEpsilon * PosEpsilon && XrWire.AngleDeg(_rot[id], _txRot[id]) < RotEpsilonDeg) continue;
                }
                else if (st == 0) { _txState[id] = 0; continue; }
                var r = dst.Slice(o, Rec);
                BinaryPrimitives.WriteUInt16LittleEndian(r, (ushort)id);
                r[2] = (byte)(st == 0 ? 2 : st == 2 ? 1 : 0);
                XrWire.WriteVec(r[3..], _pos[id]);
                XrWire.WriteQuat(r[15..], _rot[id]);
                _txPos[id] = _pos[id]; _txRot[id] = _rot[id]; _txState[id] = st;
                o += Rec; n++;
            }
            if (key) { _lastKeySeq = seq; _forceKey = false; }
        }
        "XRS1"u8.CopyTo(dst);
        dst[4] = (byte)(key ? 1 : 0); dst[5] = (byte)Device;
        BinaryPrimitives.WriteUInt16LittleEndian(dst[6..], (ushort)n);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[8..], seq);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[12..], baseSeq);
        BinaryPrimitives.WriteInt64LittleEndian(dst[16..], timestampUs);
        if (key) Interlocked.Increment(ref _keyFrames); else Interlocked.Increment(ref _deltaFrames);
        Volatile.Write(ref _lastBytes, o);
        Volatile.Write(ref _lastEntities, n);
        Volatile.Write(ref _encodeUs, (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency);
        return o;
    }

    /// <summary>gRPC 길이 접두 메시지 프레임(압축 0 · BE u32 길이).</summary>
    public static int WriteGrpcFrame(Span<byte> dst, ReadOnlySpan<byte> payload)
    {
        dst[0] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(dst[1..], (uint)payload.Length);
        payload.CopyTo(dst[5..]);
        return payload.Length + 5;
    }

    public static bool TryReadGrpcFrame(ReadOnlySpan<byte> src, out ReadOnlySpan<byte> payload)
    {
        payload = default;
        if (src.Length < 5 || src[0] != 0) return false;
        var len = BinaryPrimitives.ReadUInt32BigEndian(src[1..]);
        if (len > (uint)(src.Length - 5)) return false;
        payload = src.Slice(5, (int)len);
        return true;
    }

    /// <summary>WebXR 클라이언트용 뷰어 포즈 JSON(UTF-8 · 무할당) — {"seq":n,"p":[x,y,z],"q":[x,y,z,w],"ipd":m}.</summary>
    public int WriteWebXrPose(Span<byte> dst, in XrPose head)
    {
        var o = 0;
        Put(dst, ref o, "{\"seq\":"u8);
        _seq.TryFormat(dst[o..], out var w, default, CultureInfo.InvariantCulture); o += w;
        Put(dst, ref o, ",\"p\":["u8);
        Num(dst, ref o, head.Position.X); Put(dst, ref o, ","u8); Num(dst, ref o, head.Position.Y); Put(dst, ref o, ","u8); Num(dst, ref o, head.Position.Z);
        Put(dst, ref o, "],\"q\":["u8);
        Num(dst, ref o, head.Orientation.X); Put(dst, ref o, ","u8); Num(dst, ref o, head.Orientation.Y); Put(dst, ref o, ","u8);
        Num(dst, ref o, head.Orientation.Z); Put(dst, ref o, ","u8); Num(dst, ref o, head.Orientation.W);
        Put(dst, ref o, "],\"ipd\":"u8); Num(dst, ref o, Ipd);
        Put(dst, ref o, "}"u8);
        return o;
    }

    private static void Put(Span<byte> d, ref int o, ReadOnlySpan<byte> s) { s.CopyTo(d[o..]); o += s.Length; }
    private static void Num(Span<byte> d, ref int o, float v) { v.TryFormat(d[o..], out var w, "0.#####", CultureInfo.InvariantCulture); o += w; }

    public void EnableUdp(string host, int port)
    {
        var ep = new IPEndPoint(IPAddress.Parse(host), port);
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows())
        {
            try { s.IOControl(unchecked((int)0x9800000C), new byte[4], null); } catch (SocketException) { } catch (PlatformNotSupportedException) { }
        }
        s.Connect(ep);
        var old = Interlocked.Exchange(ref _udp, s);
        old?.Dispose();
        _forceKey = true;
        LastAction = "📡 XRS1 UDP 송출 → " + host + ":" + port.ToString(CultureInfo.InvariantCulture);
        Raise();
    }

    public void DisableUdp()
    {
        var s = Interlocked.Exchange(ref _udp, null);
        if (s is null) return;
        s.Dispose();
        LastAction = "⏹ UDP 송출 중지";
        Raise();
    }

    /// <summary>씬 인코딩 + (활성 시) UDP 송출 — 반환 = 패킷(내부 버퍼 · 다음 호출 전까지 유효).</summary>
    public ReadOnlySpan<byte> Broadcast(long timestampUs)
    {
        var n = EncodeScene(_packet, timestampUs);
        var pkt = new ReadOnlySpan<byte>(_packet, 0, n);
        if (_udp is { } s)
        {
            try { s.Send(pkt); Interlocked.Increment(ref _packets); Interlocked.Add(ref _bytesOut, n); }
            catch (SocketException) { Interlocked.Increment(ref _sendErrors); _forceKey = true; }
            catch (ObjectDisposedException) { }
        }
        return pkt;
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Xr.Metaverse.OnChange", ex); }
    }

    public void Dispose() => Interlocked.Exchange(ref _udp, null)?.Dispose();
}
