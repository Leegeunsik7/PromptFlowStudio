namespace PromptFlowStudio.Services.OpenUsd;

public enum UsdSpec : byte { Def = 0, Over = 1, Class = 2 }

public enum UsdPrimType : byte { None = 0, Xform, Scope, Sphere, Cube, Points, DistantLight }

public enum UsdError : byte { None = 0, Empty, BadHeader, Syntax, Capacity, UnsupportedCrate, BadCrate, BadZip, ZipCompressed, NoLayerInZip, Checksum }

/// <summary>프림 필드 존재 비트(opinion 마스크) — 합성 시 강한 레이어의 비트만 덮어씀.</summary>
[Flags]
public enum UsdField : ushort
{
    None = 0, Translate = 1, Scale = 2, Color = 4, Radius = 8, Size = 16, Visibility = 32, Points = 64, TimeSamples = 128, Mass = 256, RigidBody = 512, Intensity = 1024,
}

/// <summary>
/// [PromptFlow Studio v16.72.0] OpenUSD 레이어(프림 스펙 SoA · 용량 선할당). 경로 = 절대 SdfPath("/World/Ball") · 문자 풀 · 오픈 어드레싱 경로 해시.
/// 부모 인덱스 &lt; 자식 인덱스 불변식(중첩 구문 · 크레이트 순서 보장). <see cref="Clear"/> 이후 재파싱은 무할당.
/// </summary>
public sealed class UsdLayer
{
    public const int MaxPrims = 512, MaxChars = 32 * 1024, MaxPointFloats = 3 * 8192, MaxSampleFloats = 4 * 2048;
    private const int HashSlots = 1024;

    public string Identifier;
    public int Count;
    internal int CharLen, PtLen, TsLen;
    internal readonly char[] Chars = new char[MaxChars];
    internal readonly int[] PathOff = new int[MaxPrims], PathLen = new int[MaxPrims], Parent = new int[MaxPrims];
    internal readonly byte[] Spec = new byte[MaxPrims], Type = new byte[MaxPrims], Invisible = new byte[MaxPrims], Rigid = new byte[MaxPrims];
    internal readonly ushort[] Fields = new ushort[MaxPrims];
    internal readonly float[] Tx = new float[MaxPrims], Ty = new float[MaxPrims], Tz = new float[MaxPrims];
    internal readonly float[] Sx = new float[MaxPrims], Sy = new float[MaxPrims], Sz = new float[MaxPrims];
    internal readonly float[] Cr = new float[MaxPrims], Cg = new float[MaxPrims], Cb = new float[MaxPrims];
    internal readonly float[] Radius = new float[MaxPrims], Size = new float[MaxPrims], Mass = new float[MaxPrims], Intensity = new float[MaxPrims];
    internal readonly int[] PtOff = new int[MaxPrims], PtCount = new int[MaxPrims], TsOff = new int[MaxPrims], TsCount = new int[MaxPrims];
    internal readonly float[] Pts = new float[MaxPointFloats];
    internal readonly float[] Ts = new float[MaxSampleFloats];
    private readonly int[] _slots = new int[HashSlots];

    public UsdLayer(string identifier = "anon.usda")
    {
        Identifier = identifier;
        Clear();
    }

    public UsdError LastError { get; internal set; }
    public int ErrorOffset { get; internal set; }
    public int PointCount => PtLen / 3;

    public void Clear()
    {
        Count = 0; CharLen = 0; PtLen = 0; TsLen = 0;
        LastError = UsdError.None; ErrorOffset = 0;
        _slots.AsSpan().Fill(-1);
    }

    /// <summary>외부 디코드(크레이트) 후 경로 해시 인덱스 재구성.</summary>
    internal void RebuildIndex()
    {
        _slots.AsSpan().Fill(-1);
        for (var i = 0; i < Count; i++)
        {
            var slot = (int)(HashPath(Path(i)) & (HashSlots - 1));
            while (_slots[slot] >= 0) slot = (slot + 1) & (HashSlots - 1);
            _slots[slot] = i;
        }
    }

    public ReadOnlySpan<char> Path(int i) => Chars.AsSpan(PathOff[i], PathLen[i]);

    public ReadOnlySpan<char> NameOf(int i)
    {
        var p = Path(i);
        return p[(p.LastIndexOf('/') + 1)..];
    }

    public int ParentOf(int i) => Parent[i];
    public UsdSpec SpecOf(int i) => (UsdSpec)Spec[i];
    public UsdPrimType TypeOf(int i) => (UsdPrimType)Type[i];
    public UsdField FieldsOf(int i) => (UsdField)Fields[i];
    public bool Has(int i, UsdField f) => (Fields[i] & (ushort)f) != 0;
    public (float X, float Y, float Z) Translate(int i) => (Tx[i], Ty[i], Tz[i]);
    public (float X, float Y, float Z) ScaleOf(int i) => (Sx[i], Sy[i], Sz[i]);
    public (float R, float G, float B) Color(int i) => (Cr[i], Cg[i], Cb[i]);
    public float RadiusOf(int i) => Radius[i];
    public float SizeOf(int i) => Size[i];
    public float MassOf(int i) => Mass[i];
    public bool IsInvisible(int i) => Invisible[i] != 0;
    public bool IsRigid(int i) => Rigid[i] != 0;
    public int SampleCount(int i) => TsCount[i];
    public ReadOnlySpan<float> PointsOf(int i) => Pts.AsSpan(PtOff[i], PtCount[i] * 3);

    internal static ulong HashPath(ReadOnlySpan<char> s)
    {
        var h = 14695981039346656037UL;
        foreach (var c in s) { h ^= c; h *= 1099511628211UL; }
        return h;
    }

    public int Find(ReadOnlySpan<char> path)
    {
        var slot = (int)(HashPath(path) & (HashSlots - 1));
        for (var probe = 0; probe < HashSlots; probe++)
        {
            var idx = _slots[slot];
            if (idx < 0) return -1;
            if (Path(idx).SequenceEqual(path)) return idx;
            slot = (slot + 1) & (HashSlots - 1);
        }
        return -1;
    }

    /// <summary>프림 스펙 추가(경로 = 부모 경로 + "/" + 이름). 기존 경로면 그 인덱스(스펙 Def 승격만 반영). 실패 −1.</summary>
    public int AddPrim(int parent, ReadOnlySpan<char> name, UsdSpec spec, UsdPrimType type)
    {
        var parentLen = parent >= 0 ? PathLen[parent] : 0;
        var len = parentLen + 1 + name.Length;
        if (name.IsEmpty || CharLen + len > MaxChars) return -1;
        var dst = Chars.AsSpan(CharLen, len);
        if (parent >= 0) Chars.AsSpan(PathOff[parent], parentLen).CopyTo(dst);
        dst[parentLen] = '/';
        name.CopyTo(dst[(parentLen + 1)..]);
        return AddPath(dst, parent, spec, type, true);
    }

    /// <summary>절대 경로로 추가(부모는 기존 경로에서 탐색 — 없으면 루트 취급).</summary>
    public int AddPath(ReadOnlySpan<char> path, UsdSpec spec, UsdPrimType type)
    {
        if (path.Length < 2 || path[0] != '/' || CharLen + path.Length > MaxChars) return -1;
        var cut = path.LastIndexOf('/');
        var parent = cut > 0 ? Find(path[..cut]) : -1;
        var existing = Find(path);
        if (existing >= 0) { Promote(existing, spec, type); return existing; }
        var dst = Chars.AsSpan(CharLen, path.Length);
        path.CopyTo(dst);
        return AddPath(dst, parent, spec, type, true);
    }

    private int AddPath(ReadOnlySpan<char> pathInPool, int parent, UsdSpec spec, UsdPrimType type, bool pooled)
    {
        var existing = Find(pathInPool);
        if (existing >= 0) { Promote(existing, spec, type); return existing; }
        if (Count >= MaxPrims || !pooled) return -1;
        var i = Count++;
        PathOff[i] = CharLen; PathLen[i] = pathInPool.Length; CharLen += pathInPool.Length;
        Parent[i] = parent; Spec[i] = (byte)spec; Type[i] = (byte)type; Fields[i] = 0; Invisible[i] = 0; Rigid[i] = 0;
        Tx[i] = Ty[i] = Tz[i] = 0; Sx[i] = Sy[i] = Sz[i] = 1; Cr[i] = Cg[i] = Cb[i] = 0.8f;
        Radius[i] = 1; Size[i] = 2; Mass[i] = 0; Intensity[i] = 1;
        PtOff[i] = PtLen; PtCount[i] = 0; TsOff[i] = TsLen; TsCount[i] = 0;
        var slot = (int)(HashPath(pathInPool) & (HashSlots - 1));
        while (_slots[slot] >= 0) slot = (slot + 1) & (HashSlots - 1);
        _slots[slot] = i;
        return i;
    }

    private void Promote(int i, UsdSpec spec, UsdPrimType type)
    {
        if (spec == UsdSpec.Def) Spec[i] = (byte)UsdSpec.Def;
        if (type != UsdPrimType.None) Type[i] = (byte)type;
    }

    public void SetTranslate(int i, float x, float y, float z) { Tx[i] = x; Ty[i] = y; Tz[i] = z; Fields[i] |= (ushort)UsdField.Translate; }
    public void SetScale(int i, float x, float y, float z) { Sx[i] = x; Sy[i] = y; Sz[i] = z; Fields[i] |= (ushort)UsdField.Scale; }
    public void SetColor(int i, float r, float g, float b) { Cr[i] = r; Cg[i] = g; Cb[i] = b; Fields[i] |= (ushort)UsdField.Color; }
    public void SetRadius(int i, float r) { Radius[i] = r; Fields[i] |= (ushort)UsdField.Radius; }
    public void SetSize(int i, float s) { Size[i] = s; Fields[i] |= (ushort)UsdField.Size; }
    public void SetMass(int i, float m) { Mass[i] = m; Fields[i] |= (ushort)UsdField.Mass; }
    public void SetRigidBody(int i, bool on) { Rigid[i] = on ? (byte)1 : (byte)0; Fields[i] |= (ushort)UsdField.RigidBody; }
    public void SetIntensity(int i, float v) { Intensity[i] = v; Fields[i] |= (ushort)UsdField.Intensity; }
    public void SetVisibility(int i, bool visible) { Invisible[i] = visible ? (byte)0 : (byte)1; Fields[i] |= (ushort)UsdField.Visibility; }

    /// <summary>points 배열 설정 — 프림의 포인트는 풀 끝에 연속 저장(같은 프림 재설정 시 새 구간).</summary>
    public bool SetPoints(int i, ReadOnlySpan<float> xyz)
    {
        if (PtLen + xyz.Length > MaxPointFloats) return false;
        xyz.CopyTo(Pts.AsSpan(PtLen));
        PtOff[i] = PtLen; PtCount[i] = xyz.Length / 3; PtLen += xyz.Length - xyz.Length % 3;
        Fields[i] |= (ushort)UsdField.Points;
        return true;
    }

    internal bool BeginPoints(int i) { PtOff[i] = PtLen; PtCount[i] = 0; Fields[i] |= (ushort)UsdField.Points; return true; }

    internal bool AppendPoint(int i, float x, float y, float z)
    {
        if (PtLen + 3 > MaxPointFloats) return false;
        Pts[PtLen] = x; Pts[PtLen + 1] = y; Pts[PtLen + 2] = z; PtLen += 3; PtCount[i]++;
        return true;
    }

    internal void BeginSamples(int i) { TsOff[i] = TsLen; TsCount[i] = 0; Fields[i] |= (ushort)UsdField.TimeSamples; }

    /// <summary>translate 시간 샘플(시간 오름차순 입력 가정).</summary>
    public bool AddTranslateSample(int i, float time, float x, float y, float z)
    {
        if ((Fields[i] & (ushort)UsdField.TimeSamples) == 0 || TsOff[i] + TsCount[i] * 4 != TsLen) BeginSamples(i);
        if (TsLen + 4 > MaxSampleFloats) return false;
        Ts[TsLen] = time; Ts[TsLen + 1] = x; Ts[TsLen + 2] = y; Ts[TsLen + 3] = z; TsLen += 4; TsCount[i]++;
        return true;
    }

    /// <summary>시간 샘플 선형 보간(양 끝 클램프). 샘플 없으면 false.</summary>
    public bool SampleTranslate(int i, float time, out float x, out float y, out float z)
    {
        x = y = z = 0;
        var n = TsCount[i];
        if (n == 0) return false;
        var o = TsOff[i];
        if (time <= Ts[o] || n == 1) { x = Ts[o + 1]; y = Ts[o + 2]; z = Ts[o + 3]; return true; }
        for (var k = 1; k < n; k++)
        {
            var b = o + k * 4;
            if (time > Ts[b] && k < n - 1) continue;
            var a = b - 4;
            var span = Ts[b] - Ts[a];
            var t = span > 1e-9f ? Math.Clamp((time - Ts[a]) / span, 0f, 1f) : 1f;
            x = Ts[a + 1] + (Ts[b + 1] - Ts[a + 1]) * t;
            y = Ts[a + 2] + (Ts[b + 2] - Ts[a + 2]) * t;
            z = Ts[a + 3] + (Ts[b + 3] - Ts[a + 3]) * t;
            return true;
        }
        return true;
    }

    /// <summary>프림 i 의 opinion 을 dst 의 프림 d 위에 덮어씀(마스크된 필드만 · 합성용).</summary>
    internal bool OverlayInto(int i, UsdLayer dst, int d)
    {
        var f = Fields[i];
        if ((f & (ushort)UsdField.Translate) != 0) dst.SetTranslate(d, Tx[i], Ty[i], Tz[i]);
        if ((f & (ushort)UsdField.Scale) != 0) dst.SetScale(d, Sx[i], Sy[i], Sz[i]);
        if ((f & (ushort)UsdField.Color) != 0) dst.SetColor(d, Cr[i], Cg[i], Cb[i]);
        if ((f & (ushort)UsdField.Radius) != 0) dst.SetRadius(d, Radius[i]);
        if ((f & (ushort)UsdField.Size) != 0) dst.SetSize(d, Size[i]);
        if ((f & (ushort)UsdField.Mass) != 0) dst.SetMass(d, Mass[i]);
        if ((f & (ushort)UsdField.RigidBody) != 0) dst.SetRigidBody(d, Rigid[i] != 0);
        if ((f & (ushort)UsdField.Intensity) != 0) dst.SetIntensity(d, Intensity[i]);
        if ((f & (ushort)UsdField.Visibility) != 0) dst.SetVisibility(d, Invisible[i] == 0);
        if ((f & (ushort)UsdField.Points) != 0 && !dst.SetPoints(d, PointsOf(i))) return false;
        if ((f & (ushort)UsdField.TimeSamples) != 0)
        {
            dst.BeginSamples(d);
            for (var k = 0; k < TsCount[i]; k++)
            {
                var o = TsOff[i] + k * 4;
                if (!dst.AddTranslateSample(d, Ts[o], Ts[o + 1], Ts[o + 2], Ts[o + 3])) return false;
            }
        }
        return true;
    }
}
