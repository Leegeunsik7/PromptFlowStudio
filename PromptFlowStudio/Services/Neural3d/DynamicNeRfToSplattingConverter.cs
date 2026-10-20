using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using PromptFlowStudio.Services.Teleport;

namespace PromptFlowStudio.Services.Neural3d;

/// <summary>연속 방사 필드(NeRF) 질의 — 위치 · 광선 방향 · 시간 → 밀도 σ(1/m) · 방사 RGB(0..1).</summary>
public interface IRadianceField
{
    (Vector3 Min, Vector3 Max) Bounds { get; }
    void Query(Vector3 p, Vector3 dir, float time, out float sigma, out Vector3 rgb);
}

public enum FieldPrimitive : byte { Sphere, Box, Ground }

/// <summary>
/// 해석적 동적 방사 필드(프리미티브 SDF → 소프트 밀도 · 램버트 + 시점 의존 하이라이트 · 시간 운동: 원 궤도/바운스/회전).
/// 외부 학습 NeRF(Instant-NGP 등) 결선 전 데모/검증용 기준 필드.
/// </summary>
public sealed class AnalyticRadianceField : IRadianceField
{
    public const int MaxPrims = 64;
    private readonly FieldPrimitive[] _kind = new FieldPrimitive[MaxPrims];
    private readonly Vector3[] _center = new Vector3[MaxPrims], _size = new Vector3[MaxPrims], _color = new Vector3[MaxPrims], _orbit = new Vector3[MaxPrims];
    private readonly float[] _density = new float[MaxPrims], _gloss = new float[MaxPrims], _bounce = new float[MaxPrims], _speed = new float[MaxPrims];
    private int _count;
    private Vector3 _min = new(-1.5f, -0.05f, -1.5f), _max = new(1.5f, 2f, 1.5f);
    private static readonly Vector3 LightDir = Vector3.Normalize(new Vector3(0.4f, 0.8f, -0.45f));

    public (Vector3 Min, Vector3 Max) Bounds => (_min, _max);
    public int Count => _count;
    public float Softness { get; set; } = 0.015f;

    public void SetBounds(Vector3 min, Vector3 max) { _min = min; _max = max; }

    /// <summary>프리미티브 추가 — size: 구 반경(X) / 박스 반폭(xyz) · orbit: (반경, 각속도 rad/s, 위상) · bounce: 진폭(m).</summary>
    public int Add(FieldPrimitive kind, Vector3 center, Vector3 size, Vector3 color, float density = 400f, float gloss = 0.3f, Vector3 orbit = default, float bounce = 0f, float speed = 1f)
    {
        if (_count >= MaxPrims) return -1;
        var i = _count++;
        _kind[i] = kind; _center[i] = center; _size[i] = size; _color[i] = color; _density[i] = density; _gloss[i] = gloss; _orbit[i] = orbit; _bounce[i] = bounce; _speed[i] = speed;
        return i;
    }

    public Vector3 CenterAt(int i, float t)
    {
        var c = _center[i];
        var o = _orbit[i];
        if (o.X > 0) c += new Vector3(MathF.Cos(o.Y * t + o.Z) * o.X, 0f, MathF.Sin(o.Y * t + o.Z) * o.X);
        if (_bounce[i] > 0) c.Y += MathF.Abs(MathF.Sin(t * _speed[i] * MathF.PI)) * _bounce[i];
        return c;
    }

    private float Sdf(int i, Vector3 p, float t, out Vector3 normal)
    {
        var c = CenterAt(i, t);
        switch (_kind[i])
        {
            case FieldPrimitive.Sphere:
            {
                var d = p - c;
                var len = d.Length();
                normal = len > 1e-6f ? d / len : Vector3.UnitY;
                return len - _size[i].X;
            }
            case FieldPrimitive.Box:
            {
                var q = Vector3.Abs(p - c) - _size[i];
                var outside = Vector3.Max(q, Vector3.Zero).Length();
                var inside = MathF.Min(MathF.Max(q.X, MathF.Max(q.Y, q.Z)), 0f);
                var lp = p - c;
                normal = q.X >= q.Y && q.X >= q.Z ? new Vector3(MathF.Sign(lp.X), 0, 0) : q.Y >= q.Z ? new Vector3(0, MathF.Sign(lp.Y), 0) : new Vector3(0, 0, MathF.Sign(lp.Z));
                return outside + inside;
            }
            default:
                normal = Vector3.UnitY;
                return p.Y - c.Y;
        }
    }

    public void Query(Vector3 p, Vector3 dir, float time, out float sigma, out Vector3 rgb)
    {
        sigma = 0f; rgb = Vector3.Zero;
        var bestW = 0f;
        for (var i = 0; i < _count; i++)
        {
            var d = Sdf(i, p, time, out var n);
            var occ = 1f / (1f + MathF.Exp(d / Softness));
            if (occ < 1e-3f) continue;
            var s = _density[i] * occ;
            sigma += s;
            var lambert = 0.35f + 0.65f * MathF.Max(0f, Vector3.Dot(n, LightDir));
            var refl = Vector3.Reflect(dir, n);
            var spec = _gloss[i] * MathF.Pow(MathF.Max(0f, Vector3.Dot(refl, LightDir)), 12f);
            var c = _color[i];
            if (_kind[i] == FieldPrimitive.Ground) { var chk = ((int)MathF.Floor(p.X * 4f) + (int)MathF.Floor(p.Z * 4f)) & 1; c *= chk == 0 ? 1f : 0.75f; }
            var col = c * lambert + new Vector3(spec);
            rgb += col * s;
            bestW += s;
        }
        if (bestW > 0) rgb = Vector3.Clamp(rgb / bestW, Vector3.Zero, Vector3.One);
    }

    /// <summary>데모 장면: 체커 지면 · 궤도 회전 붉은 구 · 바운스 파란 구 · 금색 박스.</summary>
    public static AnalyticRadianceField Demo()
    {
        var f = new AnalyticRadianceField();
        f.Add(FieldPrimitive.Ground, Vector3.Zero, Vector3.Zero, new Vector3(0.75f, 0.75f, 0.72f), 600f, 0.05f);
        f.Add(FieldPrimitive.Sphere, new Vector3(0f, 0.35f, 0f), new Vector3(0.3f), new Vector3(0.9f, 0.2f, 0.15f), 500f, 0.6f, new Vector3(0.55f, 0.8f, 0f));
        f.Add(FieldPrimitive.Sphere, new Vector3(0.1f, 0.22f, 0.1f), new Vector3(0.22f), new Vector3(0.15f, 0.35f, 0.95f), 500f, 0.8f, default, 0.4f, 0.9f);
        f.Add(FieldPrimitive.Box, new Vector3(-0.55f, 0.25f, 0.45f), new Vector3(0.22f, 0.25f, 0.22f), new Vector3(0.95f, 0.75f, 0.2f), 600f, 0.4f);
        f.SetBounds(new Vector3(-1.2f, -0.05f, -1.2f), new Vector3(1.2f, 1.2f, 1.2f));
        return f;
    }
}

/// <summary>격자 방사 필드(밀도 + SH 1차 · 삼선형) — 가우시안 → NeRF 역변환 대상.</summary>
public sealed class GridRadianceField : IRadianceField
{
    public readonly int Res;
    public readonly float[] Sigma;
    public readonly float[] Sh;
    private readonly Vector3 _min, _max, _cell;

    public GridRadianceField(int res, Vector3 min, Vector3 max)
    {
        Res = Math.Clamp(res, 4, 256);
        Sigma = new float[Res * Res * Res];
        Sh = new float[Res * Res * Res * GaussianScene.ShStride];
        _min = min; _max = max;
        _cell = (max - min) / Res;
    }

    public (Vector3 Min, Vector3 Max) Bounds => (_min, _max);

    /// <summary>가우시안 장면 → 밀도 격자(2σ 영역 누적 · σ = −ln(1−α)/셀 크기 가중 · SH 가중 평균).</summary>
    public static GridRadianceField FromSplats(GaussianScene s, int res)
    {
        var (mn, mx) = s.Bounds();
        var pad = (mx - mn) * 0.05f + new Vector3(0.05f);
        var g = new GridRadianceField(res, mn - pad, mx + pad);
        var wsum = new float[g.Sigma.Length];
        var cell = MathF.Min(g._cell.X, MathF.Min(g._cell.Y, g._cell.Z));
        for (var i = 0; i < s.Count; i++)
        {
            var p = s.Position(i);
            var sc = s.ScaleOf(i);
            var r = MathF.Max(sc.X, MathF.Max(sc.Y, sc.Z)) * 2f + cell * 0.5f;
            var lo = Vector3.Max((p - new Vector3(r) - g._min) / g._cell, Vector3.Zero);
            var hi = Vector3.Min((p + new Vector3(r) - g._min) / g._cell, new Vector3(g.Res - 1));
            var inv = Quaternion.Conjugate(s.Rotation(i));
            var dens = -MathF.Log(1f - MathF.Min(0.999f, s.Opacity[i])) / cell;
            for (var z = (int)lo.Z; z <= (int)hi.Z; z++)
                for (var y = (int)lo.Y; y <= (int)hi.Y; y++)
                    for (var x = (int)lo.X; x <= (int)hi.X; x++)
                    {
                        var c = g._min + (new Vector3(x, y, z) + new Vector3(0.5f)) * g._cell;
                        var l = Vector3.Transform(c - p, inv) / Vector3.Max(sc, new Vector3(1e-4f));
                        var w = MathF.Exp(-0.5f * l.LengthSquared());
                        if (w < 1e-3f) continue;
                        var k = (z * g.Res + y) * g.Res + x;
                        g.Sigma[k] += dens * w;
                        wsum[k] += w;
                        for (var j = 0; j < GaussianScene.ShStride; j++) g.Sh[k * GaussianScene.ShStride + j] += s.Sh[i * GaussianScene.ShStride + j] * w;
                    }
        }
        for (var k = 0; k < wsum.Length; k++)
            if (wsum[k] > 0) for (var j = 0; j < GaussianScene.ShStride; j++) g.Sh[k * GaussianScene.ShStride + j] /= wsum[k];
        return g;
    }

    public void Query(Vector3 p, Vector3 dir, float time, out float sigma, out Vector3 rgb)
    {
        var f = (p - _min) / _cell - new Vector3(0.5f);
        int x0 = (int)MathF.Floor(f.X), y0 = (int)MathF.Floor(f.Y), z0 = (int)MathF.Floor(f.Z);
        float fx = f.X - x0, fy = f.Y - y0, fz = f.Z - z0;
        sigma = 0; rgb = Vector3.Zero;
        Span<float> sh = stackalloc float[GaussianScene.ShStride];
        sh.Clear();
        var wsum = 0f;
        for (var c = 0; c < 8; c++)
        {
            int x = x0 + (c & 1), y = y0 + ((c >> 1) & 1), z = z0 + (c >> 2);
            if ((uint)x >= (uint)Res || (uint)y >= (uint)Res || (uint)z >= (uint)Res) continue;
            var w = ((c & 1) != 0 ? fx : 1 - fx) * (((c >> 1) & 1) != 0 ? fy : 1 - fy) * ((c >> 2) != 0 ? fz : 1 - fz);
            var k = (z * Res + y) * Res + x;
            sigma += Sigma[k] * w;
            var sw = Sigma[k] * w;
            for (var j = 0; j < GaussianScene.ShStride; j++) sh[j] += Sh[k * GaussianScene.ShStride + j] * sw;
            wsum += sw;
        }
        if (wsum <= 0) return;
        for (var j = 0; j < GaussianScene.ShStride; j++) sh[j] /= wsum;
        float r = GaussianScene.ShC0 * sh[0] - GaussianScene.ShC1 * dir.Y * sh[3] + GaussianScene.ShC1 * dir.Z * sh[6] - GaussianScene.ShC1 * dir.X * sh[9] + 0.5f;
        float g = GaussianScene.ShC0 * sh[1] - GaussianScene.ShC1 * dir.Y * sh[4] + GaussianScene.ShC1 * dir.Z * sh[7] - GaussianScene.ShC1 * dir.X * sh[10] + 0.5f;
        float b = GaussianScene.ShC0 * sh[2] - GaussianScene.ShC1 * dir.Y * sh[5] + GaussianScene.ShC1 * dir.Z * sh[8] - GaussianScene.ShC1 * dir.X * sh[11] + 0.5f;
        rgb = Vector3.Clamp(new Vector3(r, g, b), Vector3.Zero, Vector3.One);
    }
}

public readonly record struct ConvertOptions(int Resolution = 48, float AlphaThreshold = 0.35f, int MaxGaussians = 60000, bool SurfaceOnly = true, float Time = 0f);

/// <summary>
/// 경량 가우시안 스트림 코덱(무할당) — 안정 ID(격자 셀) 기준 키/델타 프레임.
/// 레코드 26B: 위치 3×u16(경계 정규화) · 회전 smallest-three(2+3×10bit) · log 스케일 3×u8 · 불투명도 u8 · DC 3×u8 · SH1 9×i8.
/// 헤더 42B: "PFGS" · ver · flags(1=Key) · seq u32 · upserts u32 · removes u32 · 경계 min xyz · extent xyz f32.
/// </summary>
public sealed class SplatStreamCodec
{
    public const int Rec = 26, HeaderSize = 42;
    public const byte Version = 1, FlagKey = 1;
    private const float LogMin = -9.2f, LogMax = 0.7f, ShScale = 64f;

    private byte[] _state = Array.Empty<byte>();
    private bool[] _present = Array.Empty<bool>();
    private int[] _list = Array.Empty<int>(), _where = Array.Empty<int>();
    private bool[] _seen = Array.Empty<bool>();
    private int _listCount, _ids;
    private Vector3 _min, _ext;
    private uint _seq;
    private bool _hasKey;
    private readonly byte[] _tmp = new byte[Rec];

    public int KeyInterval { get; set; } = 90;
    public int LastUpserts { get; private set; }
    public int LastRemoves { get; private set; }
    public bool LastKey { get; private set; }
    public int Live => _listCount;
    private int _sinceKey;

    public static int MaxEncodedSize(int ids) => HeaderSize + ids * (Rec + 5) + ids * 5;

    public void Reset(int idSpace)
    {
        if (_present.Length < idSpace)
        {
            _state = new byte[idSpace * Rec]; _present = new bool[idSpace]; _seen = new bool[idSpace];
            _list = new int[idSpace]; _where = new int[idSpace];
        }
        Array.Clear(_present); _listCount = 0; _ids = idSpace; _hasKey = false; _sinceKey = 0;
    }

    private static ushort QPos(float v, float mn, float ext) => (ushort)Math.Clamp((int)MathF.Round((v - mn) / ext * 65535f), 0, 65535);
    private static float DPos(ushort q, float mn, float ext) => mn + q / 65535f * ext;
    private static byte QLog(float s) => (byte)Math.Clamp((int)MathF.Round((MathF.Log(MathF.Max(s, 1e-6f)) - LogMin) / (LogMax - LogMin) * 255f), 0, 255);
    private static float DLog(byte q) => MathF.Exp(LogMin + q / 255f * (LogMax - LogMin));
    private static byte QDc(float dc) => (byte)Math.Clamp((int)MathF.Round((0.5f + GaussianScene.ShC0 * dc) * 255f), 0, 255);
    private static float DDc(byte q) => (q / 255f - 0.5f) / GaussianScene.ShC0;

    private static uint QRot(float w, float x, float y, float z)
    {
        Span<float> q = stackalloc float[4] { w, x, y, z };
        var big = 0;
        for (var i = 1; i < 4; i++) if (MathF.Abs(q[i]) > MathF.Abs(q[big])) big = i;
        var sgn = q[big] < 0 ? -1f : 1f;
        uint bits = (uint)big << 30;
        var shift = 20;
        for (var i = 0; i < 4; i++)
        {
            if (i == big) continue;
            var v = q[i] * sgn * 0.70710678f;   // [-0.7071, 0.7071] → [-0.5, 0.5]
            bits |= (uint)Math.Clamp((int)MathF.Round((v + 0.5f) * 1023f), 0, 1023) << shift;
            shift -= 10;
        }
        return bits;
    }

    private static void DRot(uint bits, Span<float> q)
    {
        var big = (int)(bits >> 30);
        var shift = 20;
        var ss = 0f;
        for (var i = 0; i < 4; i++)
        {
            if (i == big) continue;
            var v = (((bits >> shift) & 1023) / 1023f - 0.5f) / 0.70710678f;
            q[i] = v; ss += v * v;
            shift -= 10;
        }
        q[big] = MathF.Sqrt(MathF.Max(0f, 1f - ss));
    }

    private void Pack(GaussianScene s, int i, Span<byte> r)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(r, QPos(s.Pos[i * 3], _min.X, _ext.X));
        BinaryPrimitives.WriteUInt16LittleEndian(r[2..], QPos(s.Pos[i * 3 + 1], _min.Y, _ext.Y));
        BinaryPrimitives.WriteUInt16LittleEndian(r[4..], QPos(s.Pos[i * 3 + 2], _min.Z, _ext.Z));
        BinaryPrimitives.WriteUInt32LittleEndian(r[6..], QRot(s.Rot[i * 4], s.Rot[i * 4 + 1], s.Rot[i * 4 + 2], s.Rot[i * 4 + 3]));
        r[10] = QLog(s.Scale[i * 3]); r[11] = QLog(s.Scale[i * 3 + 1]); r[12] = QLog(s.Scale[i * 3 + 2]);
        r[13] = (byte)Math.Clamp((int)MathF.Round(s.Opacity[i] * 255f), 0, 255);
        var o = i * GaussianScene.ShStride;
        r[14] = QDc(s.Sh[o]); r[15] = QDc(s.Sh[o + 1]); r[16] = QDc(s.Sh[o + 2]);
        for (var k = 0; k < 9; k++) r[17 + k] = (byte)(sbyte)Math.Clamp((int)MathF.Round(s.Sh[o + 3 + k] * ShScale), -127, 127);
    }

    private void Unpack(ReadOnlySpan<byte> r, GaussianScene s, int i)
    {
        s.Pos[i * 3] = DPos(BinaryPrimitives.ReadUInt16LittleEndian(r), _min.X, _ext.X);
        s.Pos[i * 3 + 1] = DPos(BinaryPrimitives.ReadUInt16LittleEndian(r[2..]), _min.Y, _ext.Y);
        s.Pos[i * 3 + 2] = DPos(BinaryPrimitives.ReadUInt16LittleEndian(r[4..]), _min.Z, _ext.Z);
        DRot(BinaryPrimitives.ReadUInt32LittleEndian(r[6..]), s.Rot.AsSpan(i * 4, 4));
        s.Scale[i * 3] = DLog(r[10]); s.Scale[i * 3 + 1] = DLog(r[11]); s.Scale[i * 3 + 2] = DLog(r[12]);
        s.Opacity[i] = r[13] / 255f;
        var o = i * GaussianScene.ShStride;
        s.Sh[o] = DDc(r[14]); s.Sh[o + 1] = DDc(r[15]); s.Sh[o + 2] = DDc(r[16]);
        for (var k = 0; k < 9; k++) s.Sh[o + 3 + k] = (sbyte)r[17 + k] / ShScale;
    }

    private void ListAdd(int id) { _where[id] = _listCount; _list[_listCount++] = id; _present[id] = true; }
    private void ListRemove(int id)
    {
        var at = _where[id];
        var last = _list[--_listCount];
        _list[at] = last; _where[last] = at;
        _present[id] = false;
    }

    private static int WriteVar(Span<byte> d, uint v) { var i = 0; while (v >= 0x80) { d[i++] = (byte)(v | 0x80); v >>= 7; } d[i++] = (byte)v; return i; }
    private static bool ReadVar(ReadOnlySpan<byte> s, ref int p, out uint v)
    {
        v = 0;
        for (var sh = 0; sh < 35; sh += 7) { if (p >= s.Length) return false; var b = s[p++]; v |= (uint)(b & 0x7F) << sh; if (b < 0x80) return true; }
        return false;
    }

    /// <summary>프레임 인코딩(무할당) — ids[i] = scene i 의 안정 ID. 반환 = 바이트(0 = 버퍼 부족).</summary>
    public int Encode(GaussianScene s, ReadOnlySpan<int> ids, Vector3 boundsMin, Vector3 boundsMax, Span<byte> dst, bool forceKey = false)
    {
        var n = s.Count;
        if (dst.Length < MaxEncodedSize(Math.Max(n, _listCount))) return 0;
        var key = forceKey || !_hasKey || _sinceKey >= KeyInterval;
        if (key)
        {
            _min = boundsMin; _ext = Vector3.Max(boundsMax - boundsMin, new Vector3(1e-3f));
            Array.Clear(_present, 0, _ids);
            _listCount = 0;
        }
        "PFGS"u8.CopyTo(dst);
        dst[4] = Version; dst[5] = key ? FlagKey : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(dst[6..], ++_seq);
        BinaryPrimitives.WriteSingleLittleEndian(dst[18..], _min.X); BinaryPrimitives.WriteSingleLittleEndian(dst[22..], _min.Y); BinaryPrimitives.WriteSingleLittleEndian(dst[26..], _min.Z);
        BinaryPrimitives.WriteSingleLittleEndian(dst[30..], _ext.X); BinaryPrimitives.WriteSingleLittleEndian(dst[34..], _ext.Y); BinaryPrimitives.WriteSingleLittleEndian(dst[38..], _ext.Z);
        var p = HeaderSize;
        int ups = 0, rems = 0;
        var tmp = _tmp.AsSpan();
        for (var i = 0; i < n; i++)
        {
            var id = ids[i];
            if ((uint)id >= (uint)_ids) continue;
            _seen[id] = true;
            Pack(s, i, tmp);
            var st = _state.AsSpan(id * Rec, Rec);
            if (_present[id] && tmp.SequenceEqual(st)) continue;
            p += WriteVar(dst[p..], (uint)id);
            tmp.CopyTo(dst[p..]); p += Rec;
            tmp.CopyTo(st);
            if (!_present[id]) ListAdd(id);
            ups++;
        }
        for (var k = _listCount - 1; k >= 0; k--)
        {
            var id = _list[k];
            if (_seen[id]) { _seen[id] = false; continue; }
            p += WriteVar(dst[p..], (uint)id);
            ListRemove(id);
            rems++;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(dst[10..], (uint)ups);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[14..], (uint)rems);
        _hasKey = true;
        _sinceKey = key ? 1 : _sinceKey + 1;
        LastUpserts = ups; LastRemoves = rems; LastKey = key;
        return p;
    }

    /// <summary>디코딩(무할당) → dst 장면 재구성(현존 ID 전부). 키 미수신 상태의 델타는 거부(false).</summary>
    public bool Decode(ReadOnlySpan<byte> src, GaussianScene dst)
    {
        if (src.Length < HeaderSize || !src[..4].SequenceEqual("PFGS"u8) || src[4] != Version) return false;
        var key = (src[5] & FlagKey) != 0;
        if (!key && !_hasKey) return false;
        var seq = BinaryPrimitives.ReadUInt32LittleEndian(src[6..]);
        if (!key && seq != _seq + 1) return false;
        var ups = BinaryPrimitives.ReadUInt32LittleEndian(src[10..]);
        var rems = BinaryPrimitives.ReadUInt32LittleEndian(src[14..]);
        if (key)
        {
            _min = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(src[18..]), BinaryPrimitives.ReadSingleLittleEndian(src[22..]), BinaryPrimitives.ReadSingleLittleEndian(src[26..]));
            _ext = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(src[30..]), BinaryPrimitives.ReadSingleLittleEndian(src[34..]), BinaryPrimitives.ReadSingleLittleEndian(src[38..]));
            Array.Clear(_present, 0, _ids);
            _listCount = 0;
        }
        var p = HeaderSize;
        for (var u = 0u; u < ups; u++)
        {
            if (!ReadVar(src, ref p, out var id) || id >= _ids || p + Rec > src.Length) return false;
            src.Slice(p, Rec).CopyTo(_state.AsSpan((int)id * Rec, Rec));
            p += Rec;
            if (!_present[id]) ListAdd((int)id);
        }
        for (var r = 0u; r < rems; r++)
        {
            if (!ReadVar(src, ref p, out var id) || id >= _ids) return false;
            if (_present[id]) ListRemove((int)id);
        }
        dst.EnsureCapacity(_listCount);
        for (var k = 0; k < _listCount; k++) Unpack(_state.AsSpan(_list[k] * Rec, Rec), dst, k);
        dst.Count = _listCount;
        dst.Version++;
        _seq = seq; _hasKey = true;
        LastUpserts = (int)ups; LastRemoves = (int)rems; LastKey = key;
        return true;
    }
}

/// <summary>
/// [PromptFlow Studio v16.58.0] 🔁 동적 NeRF-to-Splatting 하이브리드 스트림 변환기(Pure BCL · 무할당 변환/인코딩).
///   ▸ NeRF → 3DGS: 경계 격자 밀도 샘플 → α=1−e^(−σ·Δ) 임계 · 표면 셀(빈 이웃 인접) 선별 → 밀도 기울기 법선 정렬 원반 가우시안(접선 0.7Δ · 법선 0.25Δ) →
///     6축 방사 질의로 SH 1차 폐형 적합 → 예산 초과 시 임계 자동 상향.
///   ▸ 3DGS → NeRF: <see cref="GridRadianceField.FromSplats"/> 밀도/SH 격자(상호 변환).
///   ▸ 스트림: 안정 ID(셀 인덱스) <see cref="SplatStreamCodec"/> 키/델타 → <see cref="TeleportPacket"/> MTU 분할 + XOR FEC(v16.55 중계 호환).
///   ▸ 기준 렌더: NeRF 볼륨 레이마칭(조기 종료) — 변환 충실도(PSNR) 측정.
/// </summary>
public sealed class DynamicNeRfToSplattingConverter
{
    private float[] _sigma = Array.Empty<float>();
    private int[] _ids = Array.Empty<int>();
    private int _res;
    private readonly SplatStreamCodec _codec = new();
    private byte[] _enc = Array.Empty<byte>(), _packets = Array.Empty<byte>();
    private readonly int[] _lens = new int[TeleportPacket.MaxFragments + 1];
    private long _frames, _bytes, _packetsSent;
    private double _convertMs, _encodeMs;
    private int _lastCount, _lastBytes;
    private float _lastThreshold;
    private int _lastStride = 1;

    public event Action? OnChange;

    public SplatStreamCodec Codec => _codec;
    public ReadOnlySpan<int> Ids => _ids.AsSpan(0, _lastCount);
    public long Frames => Interlocked.Read(ref _frames);
    public long BytesStreamed => Interlocked.Read(ref _bytes);
    public long PacketsSent => Interlocked.Read(ref _packetsSent);
    public double LastConvertMs => Volatile.Read(ref _convertMs);
    public double LastEncodeMs => Volatile.Read(ref _encodeMs);
    public int LastGaussians => Volatile.Read(ref _lastCount);
    public int LastBytes => Volatile.Read(ref _lastBytes);
    public float LastThreshold => _lastThreshold;
    public int LastStride => _lastStride;
    public string? LastAction { get; private set; }

    private void Ensure(int res)
    {
        if (_res == res) return;
        _res = res;
        _sigma = new float[res * res * res];
        _codec.Reset(res * res * res);
    }

    /// <summary>NeRF → 가우시안(무할당 · 해상도 변경 시만 재할당). 반환 = 가우시안 수.</summary>
    public int Convert(IRadianceField field, GaussianScene dst, in ConvertOptions opt)
    {
        var t0 = Stopwatch.GetTimestamp();
        var res = Math.Clamp(opt.Resolution, 8, 128);
        Ensure(res);
        var (mn, mx) = field.Bounds;
        var cell = (mx - mn) / res;
        var dl = MathF.Min(cell.X, MathF.Min(cell.Y, cell.Z));
        var t = opt.Time;
        for (var z = 0; z < res; z++)
            for (var y = 0; y < res; y++)
                for (var x = 0; x < res; x++)
                {
                    var p = mn + (new Vector3(x, y, z) + new Vector3(0.5f)) * cell;
                    field.Query(p, Vector3.UnitZ, t, out var sg, out _);
                    _sigma[(z * res + y) * res + x] = sg;
                }
        dst.EnsureCapacity(Math.Min(opt.MaxGaussians, res * res * res));
        if (_ids.Length < dst.Capacity) _ids = new int[dst.Capacity];
        var thr = Math.Clamp(opt.AlphaThreshold, 0.01f, 0.99f);
        int count;
        var stride = 1;
        for (var attempt = 0; ; attempt++)
        {
            count = Emit(field, dst, res, mn, cell, dl, thr, opt, t, stride);
            if (count >= 0) break;
            if (attempt < 3) thr = MathF.Min(0.95f, thr + (1f - thr) * 0.35f);   // 예산 초과 → 임계 상향(3회)
            else stride++;                                                        // 이후 공간 해시 간격 솎아내기
        }
        _lastThreshold = thr;
        _lastStride = stride;
        Volatile.Write(ref _lastCount, count);
        Volatile.Write(ref _convertMs, (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
        return count;
    }

    private float AlphaAt(int x, int y, int z, int res, float dl)
    {
        if ((uint)x >= (uint)res || (uint)y >= (uint)res || (uint)z >= (uint)res) return 0f;
        return 1f - MathF.Exp(-_sigma[(z * res + y) * res + x] * dl);
    }

    private int Emit(IRadianceField field, GaussianScene dst, int res, Vector3 mn, Vector3 cell, float dl, float thr, in ConvertOptions opt, float t, int stride)
    {
        dst.Count = 0;
        Span<Vector3> dirs = stackalloc Vector3[6] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ };
        Span<Vector3> col = stackalloc Vector3[6];
        for (var z = 0; z < res; z++)
            for (var y = 0; y < res; y++)
                for (var x = 0; x < res; x++)
                {
                    var a = AlphaAt(x, y, z, res, dl);
                    if (a < thr) continue;
                    if (opt.SurfaceOnly && AlphaAt(x - 1, y, z, res, dl) >= thr && AlphaAt(x + 1, y, z, res, dl) >= thr && AlphaAt(x, y - 1, z, res, dl) >= thr
                        && AlphaAt(x, y + 1, z, res, dl) >= thr && AlphaAt(x, y, z - 1, res, dl) >= thr && AlphaAt(x, y, z + 1, res, dl) >= thr) continue;
                    if (stride > 1 && (uint)(x * 73856093 ^ y * 19349663 ^ z * 83492791) % (uint)stride != 0) continue;
                    if (dst.Count >= opt.MaxGaussians) return -1;
                    var p = mn + (new Vector3(x, y, z) + new Vector3(0.5f)) * cell;
                    var grad = new Vector3(AlphaAt(x + 1, y, z, res, dl) - AlphaAt(x - 1, y, z, res, dl), AlphaAt(x, y + 1, z, res, dl) - AlphaAt(x, y - 1, z, res, dl),
                        AlphaAt(x, y, z + 1, res, dl) - AlphaAt(x, y, z - 1, res, dl));
                    var gl = grad.Length();
                    var nrm = gl > 1e-4f ? -grad / gl : Vector3.UnitY;
                    // 법선 → 로컬 z 정렬 회전
                    var rot = FromTo(Vector3.UnitZ, nrm);
                    for (var k = 0; k < 6; k++) { field.Query(p, dirs[k], t, out _, out var c); col[k] = c; }
                    var avg = (col[0] + col[1] + col[2] + col[3] + col[4] + col[5]) / 6f;
                    var i = dst.Count;
                    dst.Add(p, rot, new Vector3(dl * 0.7f * MathF.Sqrt(stride), dl * 0.7f * MathF.Sqrt(stride), gl > 1e-4f ? dl * 0.25f : dl * 0.6f), MathF.Min(0.98f, 1f - MathF.Exp(-_sigma[(z * res + y) * res + x] * dl * 1.5f)), avg);
                    var o = i * GaussianScene.ShStride;
                    for (var ch = 0; ch < 3; ch++)
                    {
                        float px = Comp(col[0], ch), nx = Comp(col[1], ch), py = Comp(col[2], ch), ny = Comp(col[3], ch), pz = Comp(col[4], ch), nz = Comp(col[5], ch);
                        dst.Sh[o + 3 + ch] = -(py - ny) / (2f * GaussianScene.ShC1);
                        dst.Sh[o + 6 + ch] = (pz - nz) / (2f * GaussianScene.ShC1);
                        dst.Sh[o + 9 + ch] = -(px - nx) / (2f * GaussianScene.ShC1);
                    }
                    _ids[i] = (z * res + y) * res + x;
                }
        return dst.Count;
    }

    private static float Comp(Vector3 v, int c) => c == 0 ? v.X : c == 1 ? v.Y : v.Z;

    private static Quaternion FromTo(Vector3 a, Vector3 b)
    {
        var d = Vector3.Dot(a, b);
        if (d > 0.99999f) return Quaternion.Identity;
        if (d < -0.99999f) return new Quaternion(1f, 0f, 0f, 0f);
        var axis = Vector3.Cross(a, b);
        return Quaternion.Normalize(new Quaternion(axis.X, axis.Y, axis.Z, 1f + d));
    }

    /// <summary>변환 → 인코딩 → MTU 패킷(+FEC)(무할당 · 첫 호출/해상도 변경 시만 버퍼 확보). 반환 = 패킷 수.</summary>
    public int StreamFrame(IRadianceField field, GaussianScene work, in ConvertOptions opt, ushort stream, uint tsMicros, bool forceKey = false)
    {
        Convert(field, work, opt);
        var t0 = Stopwatch.GetTimestamp();
        var need = SplatStreamCodec.MaxEncodedSize(Math.Max(_res * _res * _res, Math.Max(work.Count, _codec.Live)));   // ID 공간 최악치 1회 확보
        if (_enc.Length < need) _enc = new byte[need];
        if (_packets.Length == 0) _packets = new byte[(TeleportPacket.MaxFragments + 1) * TeleportPacket.Mtu];
        var (mn, mx) = field.Bounds;
        var n = _codec.Encode(work, _ids.AsSpan(0, work.Count), mn, mx, _enc, forceKey);
        Volatile.Write(ref _lastBytes, n);
        var packets = 0;
        if (n > 0 && n <= TeleportPacket.MaxFragments * TeleportPacket.MaxPayload)
            packets = TeleportPacket.Packetize(_enc.AsSpan(0, n), stream, (uint)Interlocked.Read(ref _frames), _codec.LastKey, tsMicros, true, _packets, _lens);
        Volatile.Write(ref _encodeMs, (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
        Interlocked.Increment(ref _frames);
        Interlocked.Add(ref _bytes, n);
        Interlocked.Add(ref _packetsSent, packets);
        if ((Interlocked.Read(ref _frames) & 15) == 1) { LastAction = "🔁 NeRF→3DGS " + work.Count.ToString(CultureInfo.InvariantCulture) + " · " + n.ToString(CultureInfo.InvariantCulture) + "B"; Raise(); }
        return packets;
    }

    public ReadOnlySpan<byte> LastEncoded => _enc.AsSpan(0, Math.Max(0, LastBytes));
    public ReadOnlySpan<byte> Packet(int k) => _packets.AsSpan(k * TeleportPacket.Mtu, _lens[k]);

    /// <summary>NeRF 볼륨 렌더(레이마칭 · 투과율 조기 종료 · 무할당). 반환 = 평균 샘플 수.</summary>
    public static float RenderField(IRadianceField field, in SplatCamera cam, float time, Span<byte> rgb, Vector3 background, int steps = 96, Span<float> depth = default)
    {
        var (mn, mx) = field.Bounds;
        long samples = 0;
        for (var py = 0; py < cam.Height; py++)
            for (var px = 0; px < cam.Width; px++)
            {
                var dir = Vector3.Normalize(cam.Unproject(px + 0.5f, py + 0.5f, 1f) - cam.Position);
                // 경계 박스 교차(slab)
                var inv = new Vector3(1f / (MathF.Abs(dir.X) < 1e-8f ? 1e-8f : dir.X), 1f / (MathF.Abs(dir.Y) < 1e-8f ? 1e-8f : dir.Y), 1f / (MathF.Abs(dir.Z) < 1e-8f ? 1e-8f : dir.Z));
                var t1 = (mn - cam.Position) * inv; var t2 = (mx - cam.Position) * inv;
                var tmin = Vector3.Min(t1, t2); var tmax = Vector3.Max(t1, t2);
                float t0 = MathF.Max(0.01f, MathF.Max(tmin.X, MathF.Max(tmin.Y, tmin.Z))), tf = MathF.Min(tmax.X, MathF.Min(tmax.Y, tmax.Z));
                var T = 1f; var acc = Vector3.Zero; var dsum = 0f;
                if (tf > t0)
                {
                    var dt = (tf - t0) / steps;
                    for (var s = 0; s < steps && T > 1e-3f; s++)
                    {
                        var tt = t0 + (s + 0.5f) * dt;
                        field.Query(cam.Position + dir * tt, dir, time, out var sg, out var c);
                        samples++;
                        if (sg <= 0) continue;
                        var a = 1f - MathF.Exp(-sg * dt);
                        acc += c * (a * T);
                        dsum += Vector3.Dot(dir * tt, cam.Forward) * a * T;
                        T *= 1f - a;
                    }
                }
                var o = (py * cam.Width + px) * 3;
                var col = acc + background * T;
                rgb[o] = (byte)Math.Clamp((int)(col.X * 255f + 0.5f), 0, 255);
                rgb[o + 1] = (byte)Math.Clamp((int)(col.Y * 255f + 0.5f), 0, 255);
                rgb[o + 2] = (byte)Math.Clamp((int)(col.Z * 255f + 0.5f), 0, 255);
                if (!depth.IsEmpty) depth[py * cam.Width + px] = 1f - T > 0.5f ? dsum / (1f - T) : 0f;
            }
        return samples / (float)Math.Max(1, cam.Width * cam.Height);
    }

    internal void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Gs.Converter.OnChange", ex); }
    }
}
