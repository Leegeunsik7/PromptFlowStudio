using System.Buffers.Binary;
using System.Numerics;
using PromptFlowStudio.Services.Neural3d;

namespace PromptFlowStudio.Services.Metaverse;

public enum SharedEventKind : byte { Grab = 1, Release = 2, Cheer = 3 }

public readonly record struct SharedEvent(SharedEventKind Kind, ushort UserId, ushort ObjectId, ulong Lamport);

[Flags]
public enum HoloLayer { None = 0, Avatars = 1, GazeRays = 2, Vfx = 4, Subtitles = 8, All = 15 }

/// <summary>
/// [PromptFlow Studio v16.61.0] 👥 다중 사용자 공간 상태 동기화 &amp; 신경망 홀로그램 오버레이기(Pure BCL · 무할당 수신/합성).
///   ▸ 상태 "XRU1"(43B): 사용자 포즈 · smallest-three 회전 · 시선 단위벡터 · 송신 seq(중복/역순 폐기) → 사용자별 스냅샷 링(8) → 렌더 시각(now − 보간 지연) 보간 · 최대 120ms 데드 레코닝 외삽.
///   ▸ 상호작용 "XRE1"(17B): 램포트 시계 · 객체×사용자 LWW 레지스터(잡기/놓기) → 소유자 = 잡고 있는 사용자 중 최소 (램포트, 사용자ID) — 수신 순서와 무관하게 전 레플리카 수렴.
///   ▸ 홀로그램 레이어(깊이 오클루전): 👥 관객 아바타 홀로 디스크 · 👁 시선 레이 · ✨ VFX 파티클(512 풀 · 가산 합성) · 💬 입체 자막 플레이트(장면 최근접 깊이 앞 배치 · 눈별 시차 px · 수렴 편안 범위 0.6–4m).
/// </summary>
public sealed class NeuralHologramMultiuserStateSync
{
    public const int MaxUsers = 64, Snaps = 8, MaxObjects = 32, MaxUserId = 256, MaxParticles = 512;
    public const int StatePacketSize = 43, EventPacketSize = 17;
    public const float MaxExtrapolationMs = 120f;

    private readonly object _gate = new();
    private readonly ushort[] _uid = new ushort[MaxUsers];
    private readonly bool[] _active = new bool[MaxUsers];
    private readonly uint[] _lastSeq = new uint[MaxUsers];
    private readonly long[] _lastSeen = new long[MaxUsers];
    private readonly int[] _sHead = new int[MaxUsers], _sCount = new int[MaxUsers];
    private readonly Vector3[] _sp = new Vector3[MaxUsers * Snaps], _sg = new Vector3[MaxUsers * Snaps];
    private readonly Quaternion[] _sq = new Quaternion[MaxUsers * Snaps];
    private readonly long[] _st = new long[MaxUsers * Snaps];
    private readonly ulong[] _stamp = new ulong[MaxObjects * MaxUserId];
    private readonly bool[] _holding = new bool[MaxObjects * MaxUserId];
    private readonly int[] _owner = new int[MaxObjects];
    private readonly Vector3[] _anchor = new Vector3[MaxObjects];
    private readonly Vector3[] _ppos = new Vector3[MaxParticles], _pvel = new Vector3[MaxParticles], _pcol = new Vector3[MaxParticles];
    private readonly float[] _plife = new float[MaxParticles];
    private ulong _lamport;
    private uint _rng = 0x2545F491u;
    private int _pNext, _activeUsers, _liveParticles;
    private long _states, _events, _stale, _invalid, _interp, _extrap, _ownerChanges;

    public NeuralHologramMultiuserStateSync(ushort localUserId = 1)
    {
        LocalUserId = localUserId;
        Array.Fill(_owner, -1);
    }

    public event Action? OnChange;

    public ushort LocalUserId { get; }
    public ulong Lamport => Interlocked.Read(ref _lamport);
    public int ActiveUsers => Volatile.Read(ref _activeUsers);
    public int LiveParticles => Volatile.Read(ref _liveParticles);
    public long StatesReceived => Interlocked.Read(ref _states);
    public long EventsApplied => Interlocked.Read(ref _events);
    public long StaleDropped => Interlocked.Read(ref _stale);
    public long InvalidPackets => Interlocked.Read(ref _invalid);
    public long Interpolations => Interlocked.Read(ref _interp);
    public long Extrapolations => Interlocked.Read(ref _extrap);
    public long OwnerChanges => Interlocked.Read(ref _ownerChanges);
    public float InterpolationDelayMs { get; set; } = 100f;
    public long UserTimeoutUs { get; set; } = 5_000_000;
    public HoloLayer Layers { get; set; } = HoloLayer.All;
    public string? Subtitle { get; set; }
    public float PreferredSubtitleDepth { get; set; } = 1.6f;
    public float SubtitleDepth { get; private set; }
    public float SubtitleDisparityPx { get; private set; }
    public float SceneNearestDepth { get; private set; }
    public bool SubtitleComfortable => SubtitleDepth is >= 0.6f and <= 4f;
    public string? LastAction { get; private set; }

    public bool IsActive(int slot) => _active[slot];
    public ushort UserIdAt(int slot) => _uid[slot];
    public int Owner(int obj) => (uint)obj < MaxObjects ? _owner[obj] : -1;
    public void SetObjectAnchor(int obj, Vector3 p) { if ((uint)obj < MaxObjects) _anchor[obj] = p; }

    // ══════════════════════ 와이어 ══════════════════════

    public static int WriteState(Span<byte> d, ushort user, uint seq, long timestampUs, Vector3 pos, Quaternion rot, Vector3 gaze)
    {
        "XRU1"u8.CopyTo(d);
        BinaryPrimitives.WriteUInt16LittleEndian(d[4..], user);
        BinaryPrimitives.WriteUInt32LittleEndian(d[6..], seq);
        BinaryPrimitives.WriteInt64LittleEndian(d[10..], timestampUs);
        XrWire.WriteVec(d[18..], pos);
        XrWire.WriteQuat(d[30..], rot);
        XrWire.WriteUnit(d[37..], gaze);
        return StatePacketSize;
    }

    public static int WriteEvent(Span<byte> d, in SharedEvent e)
    {
        "XRE1"u8.CopyTo(d);
        d[4] = (byte)e.Kind;
        BinaryPrimitives.WriteUInt16LittleEndian(d[5..], e.UserId);
        BinaryPrimitives.WriteUInt16LittleEndian(d[7..], e.ObjectId);
        BinaryPrimitives.WriteUInt64LittleEndian(d[9..], e.Lamport);
        return EventPacketSize;
    }

    /// <summary>로컬 상호작용 발행(램포트 증가 · 로컬 적용) → 브로드캐스트용 패킷.</summary>
    public int EmitLocal(Span<byte> d, SharedEventKind kind, ushort obj)
    {
        SharedEvent e;
        lock (_gate)
        {
            e = new SharedEvent(kind, LocalUserId, obj, ++_lamport);
            ApplyEvent(e);
        }
        return WriteEvent(d, e);
    }

    /// <summary>패킷 수신(무할당) — 상태/이벤트 자동 판별.</summary>
    public bool Receive(ReadOnlySpan<byte> p, long nowUs)
    {
        if (p.Length >= StatePacketSize && p[..4].SequenceEqual("XRU1"u8))
        {
            var user = BinaryPrimitives.ReadUInt16LittleEndian(p[4..]);
            if (user == LocalUserId) return false;
            var seq = BinaryPrimitives.ReadUInt32LittleEndian(p[6..]);
            var ts = BinaryPrimitives.ReadInt64LittleEndian(p[10..]);
            lock (_gate)
            {
                var s = SlotOf(user, true);
                if (s < 0) { Interlocked.Increment(ref _invalid); return false; }
                if (_sCount[s] > 0 && (int)(seq - _lastSeq[s]) <= 0) { Interlocked.Increment(ref _stale); return false; }
                _lastSeq[s] = seq; _lastSeen[s] = nowUs;
                var k = s * Snaps + _sHead[s];
                _sp[k] = XrWire.ReadVec(p[18..]);
                _sq[k] = XrWire.ReadQuat(p[30..]);
                _sg[k] = XrWire.ReadUnit(p[37..]);
                _st[k] = ts;
                _sHead[s] = (_sHead[s] + 1) % Snaps;
                if (_sCount[s] < Snaps) _sCount[s]++;
            }
            Interlocked.Increment(ref _states);
            return true;
        }
        if (p.Length >= EventPacketSize && p[..4].SequenceEqual("XRE1"u8))
        {
            var e = new SharedEvent((SharedEventKind)p[4], BinaryPrimitives.ReadUInt16LittleEndian(p[5..]), BinaryPrimitives.ReadUInt16LittleEndian(p[7..]), BinaryPrimitives.ReadUInt64LittleEndian(p[9..]));
            bool ok;
            lock (_gate)
            {
                if (e.Lamport > _lamport) _lamport = e.Lamport;
                ok = ApplyEvent(e);
            }
            return ok;
        }
        Interlocked.Increment(ref _invalid);
        return false;
    }

    private int SlotOf(ushort user, bool create)
    {
        var free = -1;
        for (var i = 0; i < MaxUsers; i++)
        {
            if (_active[i]) { if (_uid[i] == user) return i; }
            else if (free < 0) free = i;
        }
        if (!create || free < 0) return -1;
        _active[free] = true; _uid[free] = user; _sCount[free] = 0; _sHead[free] = 0; _lastSeq[free] = 0;
        _activeUsers++;
        return free;
    }

    /// <summary>객체×사용자 LWW 레지스터 적용(락 내부) → 소유자 재계산.</summary>
    private bool ApplyEvent(in SharedEvent e)
    {
        if (e.UserId >= MaxUserId) { Interlocked.Increment(ref _invalid); return false; }
        Interlocked.Increment(ref _events);
        if (e.Kind == SharedEventKind.Cheer)
        {
            var s = SlotOf(e.UserId, false);
            var at = s >= 0 && _sCount[s] > 0 ? _sp[s * Snaps + (_sHead[s] + Snaps - 1) % Snaps] + new Vector3(0f, 0.5f, 0f) : new Vector3(0f, 1.5f, 0f);
            Burst(at, Hue(e.UserId), 24);
            return true;
        }
        if (e.ObjectId >= MaxObjects) { Interlocked.Increment(ref _invalid); return false; }
        var k = e.ObjectId * MaxUserId + e.UserId;
        if (e.Lamport <= _stamp[k]) return false;
        _stamp[k] = e.Lamport;
        _holding[k] = e.Kind == SharedEventKind.Grab;
        var owner = -1; var best = ulong.MaxValue;
        var b = e.ObjectId * MaxUserId;
        for (var u = 0; u < MaxUserId; u++)
            if (_holding[b + u] && _stamp[b + u] < best) { best = _stamp[b + u]; owner = u; }
        if (owner != _owner[e.ObjectId])
        {
            _owner[e.ObjectId] = owner;
            Interlocked.Increment(ref _ownerChanges);
            if (owner >= 0) Burst(_anchor[e.ObjectId], Hue((ushort)owner), 16);
        }
        return true;
    }

    /// <summary>렌더 시각(µs) 기준 사용자 포즈 보간/외삽(무할당).</summary>
    public bool SampleUser(int slot, long renderUs, out Vector3 pos, out Quaternion rot, out Vector3 gaze)
    {
        pos = default; rot = Quaternion.Identity; gaze = Vector3.UnitZ;
        lock (_gate)
        {
            if (!_active[slot] || _sCount[slot] == 0) return false;
            var n = _sCount[slot]; var b = slot * Snaps;
            var newest = b + (_sHead[slot] + Snaps - 1) % Snaps;
            if (renderUs >= _st[newest])
            {
                pos = _sp[newest]; rot = _sq[newest]; gaze = _sg[newest];
                if (n >= 2)
                {
                    var prev = b + (_sHead[slot] + Snaps - 2) % Snaps;
                    var dtUs = _st[newest] - _st[prev];
                    if (dtUs > 0)
                    {
                        var ahead = MathF.Min((renderUs - _st[newest]) / 1000f, MaxExtrapolationMs);
                        pos += (_sp[newest] - _sp[prev]) * (ahead * 1000f / dtUs);
                        if (ahead > 0f) Interlocked.Increment(ref _extrap);
                    }
                }
                return true;
            }
            for (var i = 1; i < n; i++)
            {
                var a = b + (_sHead[slot] + Snaps - 1 - i) % Snaps;
                if (_st[a] > renderUs) continue;
                var c = b + (_sHead[slot] + Snaps - i) % Snaps;
                var span = _st[c] - _st[a];
                var t = span > 0 ? (float)((renderUs - _st[a]) / (double)span) : 0f;
                pos = Vector3.Lerp(_sp[a], _sp[c], t);
                rot = Quaternion.Slerp(_sq[a], _sq[c], t);
                var g = Vector3.Lerp(_sg[a], _sg[c], t);
                gaze = g.LengthSquared() > 1e-8f ? Vector3.Normalize(g) : _sg[c];
                Interlocked.Increment(ref _interp);
                return true;
            }
            var oldest = b + (_sHead[slot] + Snaps - n) % Snaps;
            pos = _sp[oldest]; rot = _sq[oldest]; gaze = _sg[oldest];
            return true;
        }
    }

    /// <summary>타임아웃 사용자 제거.</summary>
    public int Prune(long nowUs)
    {
        var removed = 0;
        lock (_gate)
            for (var i = 0; i < MaxUsers; i++)
                if (_active[i] && nowUs - _lastSeen[i] > UserTimeoutUs) { _active[i] = false; _activeUsers--; removed++; }
        return removed;
    }

    // ══════════════════════ VFX 파티클 ══════════════════════

    public void EmitBurst(Vector3 at, Vector3 color, int count) { lock (_gate) Burst(at, color, count); }

    private void Burst(Vector3 at, Vector3 color, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var k = _pNext; _pNext = (_pNext + 1) % MaxParticles;
            _ppos[k] = at;
            _pvel[k] = new Vector3(Rand(), Rand() + 0.9f, Rand()) * 1.6f;
            _pcol[k] = color;
            _plife[k] = 0.8f + (Rand() + 0.5f) * 0.6f;
        }
    }

    private float Rand() { var x = _rng; x ^= x << 13; x ^= x >> 17; x ^= x << 5; _rng = x; return (x & 0xFFFFFF) / 16777216f - 0.5f; }

    public static Vector3 Hue(ushort user)
    {
        var h = (user * 0.61803398f) % 1f * 6f;
        var x = 1f - MathF.Abs(h % 2f - 1f);
        var c = (int)h switch { 0 => new Vector3(1f, x, 0f), 1 => new Vector3(x, 1f, 0f), 2 => new Vector3(0f, 1f, x), 3 => new Vector3(0f, x, 1f), 4 => new Vector3(x, 0f, 1f), _ => new Vector3(1f, 0f, x) };
        return Vector3.Lerp(c, Vector3.One, 0.25f);
    }

    public void StepVfx(float dt)
    {
        lock (_gate)
        {
            var live = 0;
            var drag = MathF.Max(0f, 1f - 1.2f * dt);
            for (var i = 0; i < MaxParticles; i++)
            {
                if (_plife[i] <= 0f) continue;
                _plife[i] -= dt;
                _pvel[i] = _pvel[i] * drag + new Vector3(0f, -2.2f * dt, 0f);
                _ppos[i] += _pvel[i] * dt;
                if (_plife[i] > 0f) live++;
            }
            Volatile.Write(ref _liveParticles, live);
        }
    }

    // ══════════════════════ 홀로그램 합성 ══════════════════════

    /// <summary>좌/우 눈 프레임에 홀로그램 레이어 합성(무할당 · 깊이 오클루전).</summary>
    public void Composite(Span<byte> left, Span<byte> right, ReadOnlySpan<float> depL, ReadOnlySpan<float> depR, in SplatCamera camL, in SplatCamera camR, long renderUs)
    {
        lock (_gate)
        {
            PlaceSubtitle(depL, camL, camR);
            CompositeEye(left, depL, camL, renderUs, 1f);
            CompositeEye(right, depR, camR, renderUs, -1f);
        }
    }

    /// <summary>자막 하단 밴드의 장면 최근접 깊이 → 그 앞(0.85×)에 배치 · 시차 px = fx·IPD/z.</summary>
    private void PlaceSubtitle(ReadOnlySpan<float> dep, in SplatCamera camL, in SplatCamera camR)
    {
        int w = camL.Width, h = camL.Height;
        var nearest = float.MaxValue;
        for (var y = (int)(h * 0.7f); y < h; y += 2)
            for (var x = (int)(w * 0.15f); x < (int)(w * 0.85f); x += 2)
            {
                var d = dep[y * w + x];
                if (d > 0f && d < nearest) nearest = d;
            }
        SceneNearestDepth = nearest == float.MaxValue ? 0f : nearest;
        var z = MathF.Min(PreferredSubtitleDepth, nearest == float.MaxValue ? PreferredSubtitleDepth : nearest * 0.85f);
        SubtitleDepth = Math.Clamp(z, 0.6f, 4f);
        SubtitleDisparityPx = camL.Fx * Vector3.Distance(camL.Position, camR.Position) / SubtitleDepth;
    }

    private void CompositeEye(Span<byte> rgb, ReadOnlySpan<float> dep, in SplatCamera cam, long renderUs, float eyeSign)
    {
        int w = cam.Width, h = cam.Height;
        var layers = Layers;
        var renderAt = renderUs - (long)(InterpolationDelayMs * 1000f);
        if ((layers & (HoloLayer.Avatars | HoloLayer.GazeRays)) != 0)
            for (var s = 0; s < MaxUsers; s++)
            {
                if (!_active[s] || !SampleUserLocked(s, renderAt, out var pos, out var gaze)) continue;
                var col = Hue(_uid[s]);
                var head = pos + new Vector3(0f, 1.55f, 0f);
                if ((layers & HoloLayer.Avatars) != 0)
                {
                    DrawDisc(rgb, dep, cam, head, 0.14f, col, 0.55f);
                    DrawDisc(rgb, dep, cam, pos + new Vector3(0f, 0.9f, 0f), 0.2f, col, 0.3f);
                }
                if ((layers & HoloLayer.GazeRays) != 0)
                    for (var k = 1; k <= 12; k++) DrawDot(rgb, dep, cam, head + gaze * (k * 0.12f), col, 0.8f);
            }
        if ((layers & HoloLayer.Vfx) != 0)
            for (var i = 0; i < MaxParticles; i++)
                if (_plife[i] > 0f) DrawDot(rgb, dep, cam, _ppos[i], _pcol[i] * MathF.Min(1f, _plife[i] * 2f), 1f);
        if ((layers & HoloLayer.Subtitles) != 0 && !string.IsNullOrEmpty(Subtitle))
        {
            int pw = (int)(w * 0.7f), ph = Math.Max(6, (int)(h * 0.12f));
            var x0 = (int)((w - pw) * 0.5f + eyeSign * SubtitleDisparityPx * 0.5f);
            var y0 = (int)(h * 0.8f);
            var cyan = new Vector3(0.2f, 0.95f, 1f);
            for (var y = y0; y < Math.Min(h, y0 + ph); y++)
            {
                var edgeY = y == y0 || y == y0 + ph - 1;
                var a = edgeY ? 0.85f : (y % 3 == 0 ? 0.45f : 0.3f);
                for (var x = Math.Max(0, x0); x < Math.Min(w, x0 + pw); x++)
                    Blend(rgb, (y * w + x) * 3, cyan, x == x0 || x == x0 + pw - 1 ? 0.85f : a);
            }
        }
    }

    private bool SampleUserLocked(int slot, long renderUs, out Vector3 pos, out Vector3 gaze)
    {
        var ok = SampleUser(slot, renderUs, out pos, out _, out gaze);
        return ok;
    }

    private static void Blend(Span<byte> rgb, int o, Vector3 c, float a)
    {
        rgb[o] = (byte)Math.Min(255, (int)(rgb[o] * (1f - a) + c.X * 255f * a + 0.5f));
        rgb[o + 1] = (byte)Math.Min(255, (int)(rgb[o + 1] * (1f - a) + c.Y * 255f * a + 0.5f));
        rgb[o + 2] = (byte)Math.Min(255, (int)(rgb[o + 2] * (1f - a) + c.Z * 255f * a + 0.5f));
    }

    private static bool Visible(ReadOnlySpan<float> dep, int i, float z) { var d = dep[i]; return d <= 0f || z < d + 0.05f; }

    internal static void DrawDot(Span<byte> rgb, ReadOnlySpan<float> dep, in SplatCamera cam, Vector3 p, Vector3 col, float a)
    {
        var c = cam.ToCamera(p);
        if (c.Z < 0.05f) return;
        var s = cam.Project(c);
        int x = (int)s.X, y = (int)s.Y;
        for (var dy = 0; dy < 2; dy++)
            for (var dx = 0; dx < 2; dx++)
            {
                int xx = x + dx, yy = y + dy;
                if ((uint)xx >= (uint)cam.Width || (uint)yy >= (uint)cam.Height) continue;
                var i = yy * cam.Width + xx;
                if (!Visible(dep, i, c.Z)) continue;
                var o = i * 3;
                rgb[o] = (byte)Math.Min(255, rgb[o] + (int)(col.X * 255f * a));
                rgb[o + 1] = (byte)Math.Min(255, rgb[o + 1] + (int)(col.Y * 255f * a));
                rgb[o + 2] = (byte)Math.Min(255, rgb[o + 2] + (int)(col.Z * 255f * a));
            }
    }

    internal static void DrawDisc(Span<byte> rgb, ReadOnlySpan<float> dep, in SplatCamera cam, Vector3 p, float radius, Vector3 col, float a)
    {
        var c = cam.ToCamera(p);
        if (c.Z < 0.1f) return;
        var s = cam.Project(c);
        var r = cam.Fx * radius / c.Z;
        if (r < 0.75f) r = 0.75f;
        int x0 = Math.Max(0, (int)(s.X - r)), x1 = Math.Min(cam.Width - 1, (int)(s.X + r));
        int y0 = Math.Max(0, (int)(s.Y - r)), y1 = Math.Min(cam.Height - 1, (int)(s.Y + r));
        var r2 = r * r; var inner = (r - 1.2f) * (r - 1.2f);
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                float fx = x + 0.5f - s.X, fy = y + 0.5f - s.Y;
                var d2 = fx * fx + fy * fy;
                if (d2 > r2) continue;
                var i = y * cam.Width + x;
                if (!Visible(dep, i, c.Z)) continue;
                var aa = d2 >= inner ? 0.9f : (y % 3 == 0 ? a * 1.3f : a);
                Blend(rgb, i * 3, col, MathF.Min(0.95f, aa));
            }
    }

    public void ResetSession()
    {
        lock (_gate)
        {
            Array.Clear(_active); Array.Clear(_stamp); Array.Clear(_holding); Array.Fill(_owner, -1); Array.Clear(_plife);
            _activeUsers = 0; _lamport = 0;
        }
        LastAction = "↺ 세션 초기화";
        Raise();
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Xr.Hologram.OnChange", ex); }
    }
}
