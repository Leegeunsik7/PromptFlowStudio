using System.Buffers.Binary;
using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Neural3d;
using PromptFlowStudio.Services.OpenUsd;

namespace PromptFlowStudio.Services.Twin;

public enum TwinPacketKind : byte { Pose = 0, Environment = 1, Command = 2 }

/// <summary>트윈 와이어 패킷(64B · 리틀엔디언 · CRC32). 위치는 송신 측 좌표계(수신 = 물리 프레임 · 명령 = 물리 프레임).</summary>
public readonly record struct TwinPacket(TwinPacketKind Kind, ushort NodeId, uint Seq, long TimeMicros, Vector3 Position, Quaternion Rotation, float Temperature, float Humidity, float Lux);

/// <summary>
/// [PromptFlow Studio v16.73.0] ♊ 실시간 디지털 트윈 피지컬 동기화 엔진.
///   ▸ 수신: IoT/카메라/센서 64B 패킷("TWN1" · 노드 ID · 시퀀스 · 캡처 시각 μs · 포즈 · 환경 · CRC32) 무할당 디코드 — CRC 불일치/미등록 노드/역순·중복 시퀀스(랩어라운드 안전) 폐기.
///   ▸ 매핑: 물리 프레임 → 가상(USD/3DGS) 프레임 보정(요 회전 · 균일 스케일 · 오프셋)과 역변환 — 양방향.
///   ▸ 지연 보상: 캡처 시각 기준 속도 추정 데드레커닝(상한 <see cref="MaxExtrapolationMs"/>) → 지수 평활(<see cref="SmoothingTau"/>) · 오프라인 판정 · 동기 오차 · 지연 P50/P95.
///   ▸ 출력: OpenUSD 레이어(v16.72) 프림 translate · 3DGS(v16.58) 가우시안 범위 이동 · 합성 장면 객체 — 매 <see cref="Tick"/> 무할당.
///   ▸ 역방향: USD 레이어에서 가상 편집 감지(<see cref="SyncFromUsd"/>) 또는 <see cref="Command"/> → 물리 프레임 명령 패킷 → 전송 결선점 또는 송신 큐.
/// </summary>
public sealed class RealtimeDigitalTwinPhysicalSyncEngine
{
    public const int PacketSize = 64, MaxNodes = 128, LatencyWindow = 256, CommandCapacity = 64;

    public interface ITwinTransport
    {
        void Send(ReadOnlySpan<byte> packet);
    }

    private readonly object _gate = new();
    private readonly ushort[] _id = new ushort[MaxNodes];
    private readonly string[] _name = new string[MaxNodes], _label = new string[MaxNodes];
    private readonly byte[] _shape = new byte[MaxNodes], _class = new byte[MaxNodes];
    private readonly bool[] _hasSeq = new bool[MaxNodes], _hasPose = new bool[MaxNodes], _online = new bool[MaxNodes], _hasCmd = new bool[MaxNodes];
    private readonly uint[] _lastSeq = new uint[MaxNodes];
    private readonly long[] _ts = new long[MaxNodes], _recv = new long[MaxNodes], _nodePackets = new long[MaxNodes];
    private readonly Vector3[] _half = new Vector3[MaxNodes], _color = new Vector3[MaxNodes];
    private readonly Vector3[] _raw = new Vector3[MaxNodes], _vel = new Vector3[MaxNodes], _filt = new Vector3[MaxNodes], _written = new Vector3[MaxNodes];
    private readonly Vector3[] _gApplied = new Vector3[MaxNodes], _cmdTarget = new Vector3[MaxNodes];
    private readonly Quaternion[] _rot = new Quaternion[MaxNodes];
    private readonly float[] _temp = new float[MaxNodes], _hum = new float[MaxNodes], _lux = new float[MaxNodes], _err = new float[MaxNodes], _lastLat = new float[MaxNodes];
    private readonly int[] _prim = new int[MaxNodes], _gStart = new int[MaxNodes], _gCount = new int[MaxNodes], _sceneObj = new int[MaxNodes];
    private readonly float[] _lat = new float[LatencyWindow], _latScratch = new float[LatencyWindow];
    private readonly byte[] _cmd = new byte[CommandCapacity * PacketSize];
    private int _n, _latHead, _latN, _cmdHead, _cmdCount, _onlineCount;
    private UsdLayer? _layer;
    private GaussianScene? _gs;
    private SyntheticScene? _scene;
    private Quaternion _calRot = Quaternion.Identity, _calInv = Quaternion.Identity;
    private float _calScale = 1;
    private Vector3 _calOffset;
    private uint _cmdSeq;
    private long _packets, _accepted, _crc, _stale, _unknown, _rejected, _commands, _cmdDropped, _edits, _lastTick;
    private float _maxErr;

    public ITwinTransport? Transport { get; set; }
    public float SmoothingTau { get; set; } = 0.05f;
    public float MaxExtrapolationMs { get; set; } = 250;
    public float OfflineMs { get; set; } = 1000;
    public float VelocityBlend { get; set; } = 0.6f;
    public float EditEpsilon { get; set; } = 1e-3f;

    public int NodeCount => _n;
    public int OnlineCount => _onlineCount;
    public long Packets => Interlocked.Read(ref _packets);
    public long Accepted => Interlocked.Read(ref _accepted);
    public long CrcErrors => Interlocked.Read(ref _crc);
    public long Stale => Interlocked.Read(ref _stale);
    public long Unknown => Interlocked.Read(ref _unknown);
    public long Rejected => Interlocked.Read(ref _rejected);
    public long CommandsSent => Interlocked.Read(ref _commands);
    public long CommandsDropped => Interlocked.Read(ref _cmdDropped);
    public long EditsDetected => Interlocked.Read(ref _edits);
    public int PendingCommands => _cmdCount;
    public float MaxSyncError => _maxErr;
    public string? LastAction { get; private set; }

    public ushort NodeId(int i) => _id[i];
    public string Name(int i) => _name[i];
    public string Label(int i) => _label[i];
    public byte Class(int i) => _class[i];
    public SimShape Shape(int i) => (SimShape)_shape[i];
    public Vector3 Half(int i) => _half[i];
    public Vector3 Color(int i) => _color[i];
    public bool HasPose(int i) => _hasPose[i];
    public Vector3 Filtered(int i) => _filt[i];
    public Vector3 Raw(int i) => _raw[i];
    public Vector3 Velocity(int i) => _vel[i];
    public Quaternion Rotation(int i) => _rot[i];
    public bool IsOnline(int i) => _online[i];
    public float SyncError(int i) => _err[i];
    public float LastLatencyMs(int i) => _lastLat[i];
    public float Temperature(int i) => _temp[i];
    public float Humidity(int i) => _hum[i];
    public float Lux(int i) => _lux[i];
    public int PrimOf(int i) => _prim[i];
    public int SceneObjectOf(int i) => _sceneObj[i];
    public long PacketsOf(int i) => _nodePackets[i];
    public bool HasCommand(int i) => _hasCmd[i];
    public Vector3 CommandTarget(int i) => _cmdTarget[i];

    public int IndexOf(ushort id)
    {
        for (var i = 0; i < _n; i++) if (_id[i] == id) return i;
        return -1;
    }

    // ══════════════════════ 구성(제어 평면) ══════════════════════

    /// <summary>노드 등록(name = USD 프림 이름 · [A-Za-z0-9_]). 이미 있으면 그 인덱스.</summary>
    public int RegisterNode(ushort id, string name, string label, SimShape shape, byte cls, Vector3 half, Vector3 color)
    {
        if (string.IsNullOrEmpty(name) || char.IsAsciiDigit(name[0])) throw new ArgumentException("노드 이름은 영문/밑줄로 시작해야 합니다.", nameof(name));
        foreach (var ch in name) if (!char.IsAsciiLetterOrDigit(ch) && ch != '_') throw new ArgumentException("노드 이름은 [A-Za-z0-9_] 만 허용됩니다.", nameof(name));
        lock (_gate)
        {
            var existing = IndexOf(id);
            if (existing >= 0) return existing;
            if (_n >= MaxNodes) return -1;
            var i = _n++;
            _id[i] = id; _name[i] = name; _label[i] = label; _shape[i] = (byte)shape; _class[i] = cls; _half[i] = half; _color[i] = color;
            _prim[i] = -1; _gStart[i] = -1; _gCount[i] = 0; _sceneObj[i] = -1;
            _hasSeq[i] = _hasPose[i] = _online[i] = _hasCmd[i] = false;
            _rot[i] = Quaternion.Identity;
            _temp[i] = _hum[i] = _lux[i] = float.NaN;
            return i;
        }
    }

    /// <summary>물리 → 가상 보정: v = R(yaw)·(p·scale) + offset.</summary>
    public void SetCalibration(float yawDeg, float scale, Vector3 offset)
    {
        lock (_gate)
        {
            _calRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yawDeg * (MathF.PI / 180f));
            _calInv = Quaternion.Inverse(_calRot);
            _calScale = scale > 1e-6f ? scale : 1f;
            _calOffset = offset;
        }
    }

    public Vector3 ToVirtual(Vector3 physical) => Vector3.Transform(physical * _calScale, _calRot) + _calOffset;
    public Vector3 ToPhysical(Vector3 virt) => Vector3.Transform(virt - _calOffset, _calInv) / _calScale;

    /// <summary>USD 레이어 결선: 부모 프림 아래 노드별 Sphere/Cube 정의 프림 생성(크기/색/비강체).</summary>
    public void BindUsd(UsdLayer layer, int parentPrim)
    {
        lock (_gate)
        {
            _layer = layer;
            for (var i = 0; i < _n; i++)
            {
                var sphere = _shape[i] == (byte)SimShape.Sphere;
                var p = layer.AddPrim(parentPrim, _name[i], UsdSpec.Def, sphere ? UsdPrimType.Sphere : UsdPrimType.Cube);
                _prim[i] = p;
                if (p < 0) continue;
                if (sphere) layer.SetRadius(p, _half[i].X); else layer.SetSize(p, _half[i].X * 2);
                layer.SetColor(p, _color[i].X, _color[i].Y, _color[i].Z);
                layer.SetRigidBody(p, false);
                layer.SetTranslate(p, _filt[i].X, _filt[i].Y, _filt[i].Z);
                _written[i] = _filt[i];
            }
        }
    }

    /// <summary>3DGS 결선: 노드 i 가 가우시안 [start, start+count) 를 소유 — 동기화 시 위치 증분만큼 평행 이동.</summary>
    public void BindGaussians(int node, GaussianScene scene, int start, int count)
    {
        lock (_gate)
        {
            _gs = scene;
            _gStart[node] = start; _gCount[node] = count; _gApplied[node] = _filt[node];
        }
    }

    public void BindSceneObject(int node, SyntheticScene scene, int obj)
    {
        lock (_gate) { _scene = scene; _sceneObj[node] = obj; }
    }

    // ══════════════════════ 와이어 포맷 ══════════════════════

    public static void Encode(in TwinPacket p, Span<byte> dst)
    {
        if (dst.Length < PacketSize) throw new ArgumentException("64바이트 이상 필요", nameof(dst));
        var d = dst[..PacketSize];
        d.Clear();
        "TWN1"u8.CopyTo(d);
        d[4] = 1; d[5] = (byte)p.Kind;
        BinaryPrimitives.WriteUInt16LittleEndian(d[6..], p.NodeId);
        BinaryPrimitives.WriteUInt32LittleEndian(d[8..], p.Seq);
        BinaryPrimitives.WriteInt64LittleEndian(d[12..], p.TimeMicros);
        BinaryPrimitives.WriteSingleLittleEndian(d[20..], p.Position.X);
        BinaryPrimitives.WriteSingleLittleEndian(d[24..], p.Position.Y);
        BinaryPrimitives.WriteSingleLittleEndian(d[28..], p.Position.Z);
        BinaryPrimitives.WriteSingleLittleEndian(d[32..], p.Rotation.W);
        BinaryPrimitives.WriteSingleLittleEndian(d[36..], p.Rotation.X);
        BinaryPrimitives.WriteSingleLittleEndian(d[40..], p.Rotation.Y);
        BinaryPrimitives.WriteSingleLittleEndian(d[44..], p.Rotation.Z);
        BinaryPrimitives.WriteSingleLittleEndian(d[48..], p.Temperature);
        BinaryPrimitives.WriteSingleLittleEndian(d[52..], p.Humidity);
        BinaryPrimitives.WriteSingleLittleEndian(d[56..], p.Lux);
        BinaryPrimitives.WriteUInt32LittleEndian(d[60..], OpenUsdSpatialAssetCompositorEngine.Crc32(d[..60]));
    }

    public static bool TryDecode(ReadOnlySpan<byte> s, out TwinPacket p)
    {
        p = default;
        if (s.Length < PacketSize || !s.StartsWith("TWN1"u8) || s[4] != 1 || s[5] > (byte)TwinPacketKind.Command) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(s[60..]) != OpenUsdSpatialAssetCompositorEngine.Crc32(s[..60])) return false;
        p = new TwinPacket((TwinPacketKind)s[5], BinaryPrimitives.ReadUInt16LittleEndian(s[6..]), BinaryPrimitives.ReadUInt32LittleEndian(s[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(s[12..]),
            new Vector3(F(s, 20), F(s, 24), F(s, 28)), new Quaternion(F(s, 36), F(s, 40), F(s, 44), F(s, 32)),
            F(s, 48), F(s, 52), F(s, 56));
        return true;
    }

    private static float F(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadSingleLittleEndian(s[o..]);

    // ══════════════════════ 수신 · 틱(무할당) ══════════════════════

    /// <summary>패킷 1개 수신(nowMicros = 로컬 수신 시각 · 캡처 시각과 같은 시계 가정). 반환 = 채택 여부.</summary>
    public bool Ingest(ReadOnlySpan<byte> packet, long nowMicros)
    {
        lock (_gate)
        {
            _packets++;
            if (!TryDecode(packet, out var p)) { _crc++; return false; }
            if (p.Kind == TwinPacketKind.Command) { _rejected++; return false; }
            var n = IndexOf(p.NodeId);
            if (n < 0) { _unknown++; return false; }
            // 네트워크 지연은 역순 폐기 패킷 포함 측정
            var lat = (nowMicros - p.TimeMicros) / 1000f;
            if (lat >= 0)
            {
                _lastLat[n] = lat;
                _lat[_latHead] = lat; _latHead = (_latHead + 1) % LatencyWindow;
                if (_latN < LatencyWindow) _latN++;
            }
            if (_hasSeq[n] && (int)(p.Seq - _lastSeq[n]) <= 0) { _stale++; return false; }
            _hasSeq[n] = true; _lastSeq[n] = p.Seq;
            _recv[n] = nowMicros;
            _nodePackets[n]++;
            if (!float.IsNaN(p.Temperature)) _temp[n] = p.Temperature;
            if (!float.IsNaN(p.Humidity)) _hum[n] = p.Humidity;
            if (!float.IsNaN(p.Lux)) _lux[n] = p.Lux;
            if (p.Kind == TwinPacketKind.Pose)
            {
                var v = ToVirtual(p.Position);
                if (_hasPose[n])
                {
                    var dt = (p.TimeMicros - _ts[n]) / 1e6f;
                    if (dt > 1e-4f && dt < 1f) _vel[n] = Vector3.Lerp(_vel[n], (v - _raw[n]) / dt, VelocityBlend);
                    else if (dt >= 1f) _vel[n] = Vector3.Zero;
                }
                else { _filt[n] = v; _hasPose[n] = true; }
                _raw[n] = v; _ts[n] = p.TimeMicros;
                _rot[n] = Quaternion.Normalize(_calRot * p.Rotation);
            }
            _accepted++;
            return true;
        }
    }

    /// <summary>데드레커닝 → 평활 → 출력(USD · 3DGS · 합성 장면). 무할당.</summary>
    public void Tick(long nowMicros)
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Twin.Sync.Tick");
            var dt = _lastTick > 0 ? (nowMicros - _lastTick) / 1e6f : 0f;
            _lastTick = nowMicros;
            var alpha = dt > 0 ? 1f - MathF.Exp(-dt / MathF.Max(SmoothingTau, 1e-4f)) : 1f;
            var maxExt = (long)(MaxExtrapolationMs * 1000);
            var offline = (long)(OfflineMs * 1000);
            var online = 0;
            var maxErr = 0f;
            var gsDirty = false;
            for (var n = 0; n < _n; n++)
            {
                _online[n] = _hasSeq[n] && nowMicros - _recv[n] <= offline;
                if (_online[n]) online++;
                if (!_hasPose[n]) continue;
                var age = Math.Clamp(nowMicros - _ts[n], 0, maxExt) / 1e6f;
                var pred = _raw[n] + _vel[n] * age;
                _filt[n] = Vector3.Lerp(_filt[n], pred, alpha);
                _err[n] = Vector3.Distance(_filt[n], pred);
                if (_err[n] > maxErr) maxErr = _err[n];
                var f = _filt[n];
                if (_layer is { } layer && _prim[n] >= 0) { layer.SetTranslate(_prim[n], f.X, f.Y, f.Z); _written[n] = f; }
                if (_gs is { } gs && _gStart[n] >= 0 && _gCount[n] > 0)
                {
                    var delta = f - _gApplied[n];
                    var end = Math.Min(gs.Count, _gStart[n] + _gCount[n]);
                    for (var g = _gStart[n]; g < end; g++) { gs.Pos[g * 3] += delta.X; gs.Pos[g * 3 + 1] += delta.Y; gs.Pos[g * 3 + 2] += delta.Z; }
                    _gApplied[n] = f;
                    gsDirty = true;
                }
                if (_scene is { } sc && _sceneObj[n] >= 0) sc.SetPosition(_sceneObj[n], f);
            }
            if (gsDirty && _gs is not null) _gs.Version++;
            _onlineCount = online;
            _maxErr = maxErr;
        }
    }

    /// <summary>지연 백분위(최근 256개 · 무할당).</summary>
    public float LatencyPercentile(float q)
    {
        lock (_gate)
        {
            if (_latN == 0) return 0;
            var s = _latScratch.AsSpan(0, _latN);
            _lat.AsSpan(0, _latN).CopyTo(s);
            s.Sort();
            return s[Math.Clamp((int)MathF.Ceiling(q * _latN) - 1, 0, _latN - 1)];
        }
    }

    // ══════════════════════ 역방향(가상 → 물리) ══════════════════════

    /// <summary>USD 레이어에서 마지막 기록 이후 바뀐 translate = 가상 편집 → 명령 송신. 반환 = 감지 수.</summary>
    public int SyncFromUsd(long nowMicros)
    {
        lock (_gate)
        {
            if (_layer is not { } layer) return 0;
            var edits = 0;
            for (var n = 0; n < _n; n++)
            {
                if (_prim[n] < 0) continue;
                var (x, y, z) = layer.Translate(_prim[n]);
                var t = new Vector3(x, y, z);
                if (Vector3.Distance(t, _written[n]) <= EditEpsilon) continue;
                _written[n] = t;
                Command(n, t, nowMicros);
                _edits++;
                edits++;
            }
            return edits;
        }
    }

    /// <summary>가상 목표 → 물리 프레임 명령 패킷(전송 결선점 우선 · 없으면 송신 큐 — 가득 차면 가장 오래된 것 폐기).</summary>
    public void Command(int node, Vector3 virtualTarget, long nowMicros)
    {
        lock (_gate)
        {
            Span<byte> buf = stackalloc byte[PacketSize];
            Encode(new TwinPacket(TwinPacketKind.Command, _id[node], ++_cmdSeq, nowMicros, ToPhysical(virtualTarget), Quaternion.Identity, float.NaN, float.NaN, float.NaN), buf);
            _hasCmd[node] = true; _cmdTarget[node] = virtualTarget;
            _commands++;
            if (Transport is { } t)
            {
                try { t.Send(buf); }
                catch (Exception ex) { PromptFlowStudio.Program.Log("Twin.Transport", ex); }
                return;
            }
            if (_cmdCount == CommandCapacity) { _cmdHead = (_cmdHead + 1) % CommandCapacity; _cmdCount--; _cmdDropped++; }
            buf.CopyTo(_cmd.AsSpan((_cmdHead + _cmdCount) % CommandCapacity * PacketSize, PacketSize));
            _cmdCount++;
        }
    }

    public bool TryDequeueCommand(Span<byte> dst)
    {
        lock (_gate)
        {
            if (_cmdCount == 0) return false;
            _cmd.AsSpan(_cmdHead * PacketSize, PacketSize).CopyTo(dst);
            _cmdHead = (_cmdHead + 1) % CommandCapacity; _cmdCount--;
            return true;
        }
    }
}
