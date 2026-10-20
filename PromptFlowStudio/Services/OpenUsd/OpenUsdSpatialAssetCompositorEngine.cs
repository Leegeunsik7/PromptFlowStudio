using System.Buffers.Binary;
using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Neural3d;
using PromptFlowStudio.Services.World;

namespace PromptFlowStudio.Services.OpenUsd;

public enum UsdFormat : byte { Unknown = 0, Usda, Usdc, Usdz, PixarCrate }

/// <summary>합성 스테이지 프림의 평가된 월드 상태(Evaluate 결과).</summary>
public readonly record struct UsdPrimState(int Index, UsdPrimType Type, float X, float Y, float Z, float Scale, bool Visible, bool Defined);

/// <summary>
/// [PromptFlow Studio v16.72.0] 🪐 유니버설 OpenUSD 공간 자산 파서 &amp; 씬 그래프 실시간 합성기.
///   ▸ 디코드: .usda(<see cref="UsdaReader"/>) · .usdc(PFS 크레이트 "PFSUSDC1" 리틀엔디언 SoA 섹션 + CRC32 — Pixar "PXR-USDC" 크레이트는 감지 후 <see cref="UsdError.UnsupportedCrate"/>) ·
///     .usdz(무압축 ZIP · 64바이트 정렬 검사 · 첫 .usda/.usdc 엔트리 = 기본 레이어 · CRC32 검증). 모두 바이트 스팬 직접 디코드(레이어 버퍼 재사용 = 무할당).
///   ▸ 합성: 레이어 스택(슬롯 0 = 가장 강함) · 약→강 순서 opinion 덮어쓰기(필드 마스크) · def 가 하나라도 있어야 정의 프림 · 레이어 뮤트.
///   ▸ 평가: 부모 체인 scale·translate 누적 · translate.timeSamples 선형 보간 · 가시성 상속 — 무할당.
///   ▸ 바인딩: 3DGS(<see cref="GaussianScene"/>, v16.58) 입자 ↔ USD · 4D 월드 모델(<see cref="AiGenerative4dWorldModelEngine"/>, v16.54) 강체 ↔ USD 양방향.
/// </summary>
public sealed class OpenUsdSpatialAssetCompositorEngine
{
    public const int MaxLayers = 8;
    public const int CrateHeader = 32;
    private static readonly uint[] CrcTable = BuildCrc();

    private readonly UsdLayer?[] _layers = new UsdLayer?[MaxLayers];
    private readonly bool[] _muted = new bool[MaxLayers];
    private readonly UsdLayer _stage = new("stage.flattened");
    private readonly float[] _wx = new float[UsdLayer.MaxPrims], _wy = new float[UsdLayer.MaxPrims], _wz = new float[UsdLayer.MaxPrims];
    private readonly float[] _ws = new float[UsdLayer.MaxPrims];
    private readonly bool[] _wvis = new bool[UsdLayer.MaxPrims];
    private long _composes, _evaluations, _binds;
    private float _time;

    public event Action? OnChange;

    public UsdLayer Stage => _stage;
    public float TimeCode => _time;
    public long Composes => Interlocked.Read(ref _composes);
    public long Evaluations => Interlocked.Read(ref _evaluations);
    public long Binds => Interlocked.Read(ref _binds);
    public int LastBoundGaussians { get; private set; }
    public UsdError LastError { get; private set; }
    public string? LastAction { get; private set; }

    public UsdLayer? Layer(int slot) => (uint)slot < MaxLayers ? _layers[slot] : null;
    public bool IsMuted(int slot) => (uint)slot < MaxLayers && _muted[slot];

    public int LayerCount
    {
        get { var n = 0; foreach (var l in _layers) if (l is not null) n++; return n; }
    }

    public static UsdFormat Detect(ReadOnlySpan<byte> data) =>
        data.StartsWith("#usda"u8) ? UsdFormat.Usda :
        data.StartsWith("PFSUSDC1"u8) ? UsdFormat.Usdc :
        data.StartsWith("PXR-USDC"u8) ? UsdFormat.PixarCrate :
        data.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(data) == 0x04034B50 ? UsdFormat.Usdz : UsdFormat.Unknown;

    /// <summary>포맷 자동 감지 디코드(무할당 — dst 재사용).</summary>
    public static bool Decode(ReadOnlySpan<byte> data, UsdLayer dst)
    {
        switch (Detect(data))
        {
            case UsdFormat.Usda: return UsdaReader.Parse(data, dst);
            case UsdFormat.Usdc: return DecodeUsdc(data, dst);
            case UsdFormat.Usdz: return DecodeUsdz(data, dst);
            case UsdFormat.PixarCrate: dst.Clear(); dst.LastError = UsdError.UnsupportedCrate; return false;
            default: dst.Clear(); dst.LastError = data.IsEmpty ? UsdError.Empty : UsdError.BadHeader; return false;
        }
    }

    // ══════════════════════ 레이어 스택 ══════════════════════

    public void SetLayer(int slot, UsdLayer? layer)
    {
        if ((uint)slot >= MaxLayers) throw new ArgumentOutOfRangeException(nameof(slot));
        _layers[slot] = layer;
        _muted[slot] = false;
        Compose();
        LastAction = layer is null ? "레이어 제거 · 슬롯 " + slot.ToString(System.Globalization.CultureInfo.InvariantCulture) : "레이어 장착 · " + layer.Identifier;
        Raise();
    }

    public void MuteLayer(int slot, bool muted)
    {
        if ((uint)slot >= MaxLayers || _muted[slot] == muted) return;
        _muted[slot] = muted;
        Compose();
        LastAction = (muted ? "🔇 뮤트 · " : "🔊 활성 · ") + (_layers[slot]?.Identifier ?? "-");
        Raise();
    }

    /// <summary>약(높은 슬롯) → 강(슬롯 0) 순서로 opinion 을 덮어써 평탄화 스테이지 생성(무할당).</summary>
    public bool Compose()
    {
        using var guard = ZeroGcRegionScope.Enter("Usd.Compose");
        _stage.Clear();
        var ok = true;
        for (var s = MaxLayers - 1; s >= 0; s--)
        {
            var l = _layers[s];
            if (l is null || _muted[s]) continue;
            for (var i = 0; i < l.Count; i++)
            {
                if (l.Spec[i] == (byte)UsdSpec.Class) continue;
                var d = _stage.AddPath(l.Path(i), (UsdSpec)l.Spec[i], (UsdPrimType)l.Type[i]);
                if (d < 0 || !l.OverlayInto(i, _stage, d)) { ok = false; _stage.LastError = UsdError.Capacity; }
            }
        }
        LastError = ok ? UsdError.None : UsdError.Capacity;
        Interlocked.Increment(ref _composes);
        Evaluate(_time);
        return ok;
    }

    /// <summary>시간 코드 평가: 부모 체인 누적 변환 · 시간 샘플 보간 · 가시성 상속(무할당).</summary>
    public void Evaluate(float timeCode)
    {
        using var guard = ZeroGcRegionScope.Enter("Usd.Evaluate");
        _time = timeCode;
        var st = _stage;
        for (var i = 0; i < st.Count; i++)
        {
            float lx = st.Tx[i], ly = st.Ty[i], lz = st.Tz[i];
            if (st.TsCount[i] > 0) st.SampleTranslate(i, timeCode, out lx, out ly, out lz);
            var ls = (st.Sx[i] + st.Sy[i] + st.Sz[i]) * (1f / 3f);
            var p = st.Parent[i];
            if (p >= 0 && p < i)
            {
                var ps = _ws[p];
                _wx[i] = _wx[p] + ps * lx; _wy[i] = _wy[p] + ps * ly; _wz[i] = _wz[p] + ps * lz;
                _ws[i] = ps * ls;
                _wvis[i] = _wvis[p] && st.Invisible[i] == 0;
            }
            else
            {
                _wx[i] = lx; _wy[i] = ly; _wz[i] = lz; _ws[i] = ls;
                _wvis[i] = st.Invisible[i] == 0;
            }
        }
        Interlocked.Increment(ref _evaluations);
    }

    public UsdPrimState State(int i) =>
        new(i, (UsdPrimType)_stage.Type[i], _wx[i], _wy[i], _wz[i], _ws[i], _wvis[i], _stage.Spec[i] == (byte)UsdSpec.Def);

    public int FindPrim(ReadOnlySpan<char> path) => _stage.Find(path);

    // ══════════════════════ 3DGS 바인딩 ══════════════════════

    /// <summary>
    /// 정의·가시 프림 → 가우시안(구 = 반지름/2 표준편차 · 큐브 = size/4 · Points = 포인트별 2cm × 스케일). 용량 충분하면 무할당. 반환 = 가우시안 수.
    /// </summary>
    public int BindToGaussians(GaussianScene dst, float opacity = 0.9f)
    {
        using var guard = ZeroGcRegionScope.Enter("Usd.BindGaussians");
        dst.Count = 0;
        var st = _stage;
        for (var i = 0; i < st.Count; i++)
        {
            if (st.Spec[i] != (byte)UsdSpec.Def || !_wvis[i]) continue;
            var rgb = new Vector3(st.Cr[i], st.Cg[i], st.Cb[i]);
            var s = _ws[i];
            switch ((UsdPrimType)st.Type[i])
            {
                case UsdPrimType.Sphere:
                    dst.Add(new Vector3(_wx[i], _wy[i], _wz[i]), Quaternion.Identity, new Vector3(st.Radius[i] * s * 0.5f), opacity, rgb);
                    break;
                case UsdPrimType.Cube:
                    dst.Add(new Vector3(_wx[i], _wy[i], _wz[i]), Quaternion.Identity, new Vector3(st.Size[i] * s * 0.25f), opacity, rgb);
                    break;
                case UsdPrimType.Points:
                    var pts = st.PointsOf(i);
                    for (var k = 0; k + 2 < pts.Length; k += 3)
                        dst.Add(new Vector3(_wx[i] + s * pts[k], _wy[i] + s * pts[k + 1], _wz[i] + s * pts[k + 2]), Quaternion.Identity, new Vector3(0.02f * s), opacity, rgb);
                    break;
            }
        }
        dst.Version++;
        LastBoundGaussians = dst.Count;
        Interlocked.Increment(ref _binds);
        return dst.Count;
    }

    /// <summary>3DGS 입자 → USD Points 프림(경로 /Splats/Cloud · 색 = 평균 기저색). 반환 = 포인트 수.</summary>
    public static int ImportGaussians(GaussianScene src, UsdLayer dst, int maxPoints = 4096)
    {
        dst.Clear();
        var root = dst.AddPrim(-1, "Splats", UsdSpec.Def, UsdPrimType.Xform);
        var cloud = dst.AddPrim(root, "Cloud", UsdSpec.Def, UsdPrimType.Points);
        var n = Math.Min(src.Count, Math.Min(maxPoints, UsdLayer.MaxPointFloats / 3));
        dst.BeginPoints(cloud);
        Vector3 acc = default;
        for (var i = 0; i < n; i++)
        {
            var p = src.Position(i);
            dst.AppendPoint(cloud, p.X, p.Y, p.Z);
            acc += src.BaseColor(i);
        }
        if (n > 0) { acc /= n; dst.SetColor(cloud, acc.X, acc.Y, acc.Z); }
        return n;
    }

    // ══════════════════════ 4D 월드 바인딩 ══════════════════════

    /// <summary>정의·가시 Sphere/Cube → 4D 월드 강체(rigidBodyEnabled=false 또는 질량 0 = 정적). 제어 평면(이름 문자열 할당). 반환 = 생성 수.</summary>
    public int BindToWorld(AiGenerative4dWorldModelEngine world, bool reset = true)
    {
        if (reset) world.Reset();
        var st = _stage;
        var n = 0;
        for (var i = 0; i < st.Count; i++)
        {
            if (st.Spec[i] != (byte)UsdSpec.Def || !_wvis[i]) continue;
            var type = (UsdPrimType)st.Type[i];
            if (type is not (UsdPrimType.Sphere or UsdPrimType.Cube)) continue;
            var dynamicBody = st.Rigid[i] != 0 && st.Mass[i] > 0;
            var mass = dynamicBody ? st.Mass[i] : 0;
            var name = new string(st.NameOf(i));
            var label = new string(st.Path(i));
            var half = type == UsdPrimType.Sphere ? st.Radius[i] * _ws[i] : st.Size[i] * 0.5f * _ws[i];
            var idx = world.Spawn(name, label, type == UsdPrimType.Sphere ? WorldShape.Sphere : WorldShape.Box,
                _wx[i], _wy[i], _wz[i], half, half, half, mass, 0.4f, 0.5f, st.Cr[i], st.Cg[i], st.Cb[i]);
            if (idx < 0) break;
            n++;
        }
        LastAction = "4D 월드 바인딩 · 강체 " + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return n;
    }

    /// <summary>4D 월드 현재 상태 → USD 레이어(/World/{이름} · 구/큐브 · 질량/강체 플래그 · 색).</summary>
    public static int ImportWorld(AiGenerative4dWorldModelEngine world, UsdLayer dst)
    {
        dst.Clear();
        var root = dst.AddPrim(-1, "World", UsdSpec.Def, UsdPrimType.Xform);
        Span<char> buf = stackalloc char[64];
        var n = 0;
        for (var i = 0; i < world.ObjectCount; i++)
        {
            var nm = world.Name(i).AsSpan();
            var len = 0;
            foreach (var c in nm) if (len < buf.Length && (char.IsAsciiLetterOrDigit(c) || c == '_')) buf[len++] = c;
            if (len == 0 || char.IsAsciiDigit(buf[0])) { if (len < buf.Length) { buf[..len].CopyTo(buf[1..]); buf[0] = '_'; len++; } }
            var sphere = world.Shape(i) == WorldShape.Sphere;
            var p = dst.AddPrim(root, buf[..len], UsdSpec.Def, sphere ? UsdPrimType.Sphere : UsdPrimType.Cube);
            if (p < 0) break;
            var (x, y, z) = world.Position(i);
            var (hx, _, _) = world.Half(i);
            var (r, g, b) = world.Color(i);
            dst.SetTranslate(p, x, y, z);
            if (sphere) dst.SetRadius(p, hx); else dst.SetSize(p, hx * 2);
            dst.SetColor(p, r, g, b);
            dst.SetMass(p, world.Mass(i));
            dst.SetRigidBody(p, !world.IsStatic(i));
            if (world.IsHidden(i)) dst.SetVisibility(p, false);
            n++;
        }
        return n;
    }

    // ══════════════════════ USDC(PFS 크레이트) ══════════════════════

    public static int CrateSize(UsdLayer l) =>
        CrateHeader + l.Count * (4 * 3 + 2 + 4 + 4 * 13 + 4 * 4) + l.CharLen * 2 + l.PtLen * 4 + l.TsLen * 4 + 4;

    /// <summary>레이어 → PFS 크레이트(리틀엔디언 SoA 섹션 + CRC32). 반환 = 바이트 수(공간 부족 −1).</summary>
    public static int EncodeUsdc(UsdLayer l, Span<byte> dst)
    {
        var size = CrateSize(l);
        if (dst.Length < size) return -1;
        "PFSUSDC1"u8.CopyTo(dst);
        BinaryPrimitives.WriteInt32LittleEndian(dst[8..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(dst[12..], l.Count);
        BinaryPrimitives.WriteInt32LittleEndian(dst[16..], l.CharLen);
        BinaryPrimitives.WriteInt32LittleEndian(dst[20..], l.PtLen);
        BinaryPrimitives.WriteInt32LittleEndian(dst[24..], l.TsLen);
        BinaryPrimitives.WriteInt32LittleEndian(dst[28..], size);
        var o = CrateHeader;
        for (var i = 0; i < l.Count; i++)
        {
            W(dst, ref o, l.PathOff[i]); W(dst, ref o, l.PathLen[i]); W(dst, ref o, l.Parent[i]);
            dst[o++] = l.Spec[i]; dst[o++] = l.Type[i];
            BinaryPrimitives.WriteUInt16LittleEndian(dst[o..], l.Fields[i]); o += 2;
            dst[o++] = l.Invisible[i]; dst[o++] = l.Rigid[i];
            F(dst, ref o, l.Tx[i]); F(dst, ref o, l.Ty[i]); F(dst, ref o, l.Tz[i]);
            F(dst, ref o, l.Sx[i]); F(dst, ref o, l.Sy[i]); F(dst, ref o, l.Sz[i]);
            F(dst, ref o, l.Cr[i]); F(dst, ref o, l.Cg[i]); F(dst, ref o, l.Cb[i]);
            F(dst, ref o, l.Radius[i]); F(dst, ref o, l.Size[i]); F(dst, ref o, l.Mass[i]); F(dst, ref o, l.Intensity[i]);
            W(dst, ref o, l.PtOff[i]); W(dst, ref o, l.PtCount[i]); W(dst, ref o, l.TsOff[i]); W(dst, ref o, l.TsCount[i]);
        }
        for (var i = 0; i < l.CharLen; i++) { BinaryPrimitives.WriteUInt16LittleEndian(dst[o..], l.Chars[i]); o += 2; }
        for (var i = 0; i < l.PtLen; i++) F(dst, ref o, l.Pts[i]);
        for (var i = 0; i < l.TsLen; i++) F(dst, ref o, l.Ts[i]);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[o..], Crc32(dst[..o])); o += 4;
        return o;
    }

    /// <summary>PFS 크레이트 → 레이어(경계·CRC·불변식 검증 · 무할당).</summary>
    public static bool DecodeUsdc(ReadOnlySpan<byte> src, UsdLayer dst)
    {
        dst.Clear();
        if (src.Length < CrateHeader + 4 || !src.StartsWith("PFSUSDC1"u8)) return Bad(dst, UsdError.BadHeader);
        int count = R(src, 12), chars = R(src, 16), pts = R(src, 20), ts = R(src, 24), size = R(src, 28);
        if (R(src, 8) != 1 || (uint)count > UsdLayer.MaxPrims || (uint)chars > UsdLayer.MaxChars || (uint)pts > UsdLayer.MaxPointFloats
            || (uint)ts > UsdLayer.MaxSampleFloats || size > src.Length || size < CrateHeader + 4) return Bad(dst, UsdError.BadCrate);
        if (BinaryPrimitives.ReadUInt32LittleEndian(src[(size - 4)..]) != Crc32(src[..(size - 4)])) return Bad(dst, UsdError.Checksum);
        dst.Count = count; dst.CharLen = chars; dst.PtLen = pts; dst.TsLen = ts;
        if (size != CrateSize(dst)) { dst.Clear(); return Bad(dst, UsdError.BadCrate); }
        var o = CrateHeader;
        for (var i = 0; i < count; i++)
        {
            dst.PathOff[i] = Ri(src, ref o); dst.PathLen[i] = Ri(src, ref o); dst.Parent[i] = Ri(src, ref o);
            dst.Spec[i] = src[o++]; dst.Type[i] = src[o++];
            dst.Fields[i] = BinaryPrimitives.ReadUInt16LittleEndian(src[o..]); o += 2;
            dst.Invisible[i] = src[o++]; dst.Rigid[i] = src[o++];
            dst.Tx[i] = Rf(src, ref o); dst.Ty[i] = Rf(src, ref o); dst.Tz[i] = Rf(src, ref o);
            dst.Sx[i] = Rf(src, ref o); dst.Sy[i] = Rf(src, ref o); dst.Sz[i] = Rf(src, ref o);
            dst.Cr[i] = Rf(src, ref o); dst.Cg[i] = Rf(src, ref o); dst.Cb[i] = Rf(src, ref o);
            dst.Radius[i] = Rf(src, ref o); dst.Size[i] = Rf(src, ref o); dst.Mass[i] = Rf(src, ref o); dst.Intensity[i] = Rf(src, ref o);
            dst.PtOff[i] = Ri(src, ref o); dst.PtCount[i] = Ri(src, ref o); dst.TsOff[i] = Ri(src, ref o); dst.TsCount[i] = Ri(src, ref o);
        }
        for (var i = 0; i < chars; i++) { dst.Chars[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(src[o..]); o += 2; }
        for (var i = 0; i < pts; i++) dst.Pts[i] = Rf(src, ref o);
        for (var i = 0; i < ts; i++) dst.Ts[i] = Rf(src, ref o);
        // 불변식: 경로·포인트·샘플 범위, 부모 < 자식
        for (var i = 0; i < count; i++)
        {
            if ((uint)dst.PathOff[i] > (uint)chars || (uint)dst.PathLen[i] > (uint)(chars - dst.PathOff[i]) || dst.PathLen[i] < 2
                || dst.Parent[i] >= i || dst.Parent[i] < -1
                || (uint)dst.PtOff[i] > (uint)pts || (uint)dst.PtCount[i] > (uint)((pts - dst.PtOff[i]) / 3)
                || (uint)dst.TsOff[i] > (uint)ts || (uint)dst.TsCount[i] > (uint)((ts - dst.TsOff[i]) / 4))
            { dst.Clear(); return Bad(dst, UsdError.BadCrate); }
        }
        dst.RebuildIndex();
        return true;
    }

    // ══════════════════════ USDZ(무압축 ZIP) ══════════════════════

    /// <summary>USDZ 패키지 디코드 — 첫 .usda/.usdc 엔트리를 기본 레이어로(압축 엔트리 거부 · CRC 검증 · 64B 정렬 여부 <paramref name="aligned"/>).</summary>
    public static bool DecodeUsdz(ReadOnlySpan<byte> zip, UsdLayer dst) => DecodeUsdz(zip, dst, out _);

    public static bool DecodeUsdz(ReadOnlySpan<byte> zip, UsdLayer dst, out bool aligned)
    {
        aligned = false;
        var o = 0;
        while (o + 30 <= zip.Length && BinaryPrimitives.ReadUInt32LittleEndian(zip[o..]) == 0x04034B50)
        {
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(zip[(o + 6)..]);
            var method = BinaryPrimitives.ReadUInt16LittleEndian(zip[(o + 8)..]);
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(zip[(o + 14)..]);
            var csize = BinaryPrimitives.ReadInt32LittleEndian(zip[(o + 18)..]);
            var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(zip[(o + 26)..]);
            var extraLen = BinaryPrimitives.ReadUInt16LittleEndian(zip[(o + 28)..]);
            var data = o + 30 + nameLen + extraLen;
            if ((flags & 8) != 0 || csize < 0 || data + (long)csize > zip.Length) { dst.Clear(); return Bad(dst, UsdError.BadZip); }
            var name = zip.Slice(o + 30, nameLen);
            var isLayer = name.EndsWith(".usda"u8) || name.EndsWith(".usdc"u8);
            if (isLayer)
            {
                if (method != 0) { dst.Clear(); return Bad(dst, UsdError.ZipCompressed); }
                var payload = zip.Slice(data, csize);
                if (Crc32(payload) != crc) { dst.Clear(); return Bad(dst, UsdError.Checksum); }
                aligned = data % 64 == 0;
                return name.EndsWith(".usda"u8) ? UsdaReader.Parse(payload, dst) : DecodeUsdc(payload, dst);
            }
            o = data + csize;
        }
        dst.Clear();
        return Bad(dst, o == 0 ? UsdError.BadZip : UsdError.NoLayerInZip);
    }

    /// <summary>단일 레이어 USDZ 패키징(무압축 · 데이터 64B 정렬 패딩 · 중앙 디렉터리 + EOCD). 반환 = 바이트 수(공간 부족 −1).</summary>
    public static int WriteUsdz(ReadOnlySpan<byte> entryName, ReadOnlySpan<byte> payload, Span<byte> dst)
    {
        var headerEnd = 30 + entryName.Length + 4;            // +4 = 패딩 extra 헤더(id 0x1986, size)
        var pad = (64 - headerEnd % 64) % 64;
        var dataStart = headerEnd + pad;
        var cd = dataStart + payload.Length;
        var total = cd + 46 + entryName.Length + 22;
        if (dst.Length < total) return -1;
        dst[..total].Clear();
        var crc = Crc32(payload);
        BinaryPrimitives.WriteUInt32LittleEndian(dst, 0x04034B50);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[4..], 10);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[14..], crc);
        BinaryPrimitives.WriteInt32LittleEndian(dst[18..], payload.Length);
        BinaryPrimitives.WriteInt32LittleEndian(dst[22..], payload.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[26..], (ushort)entryName.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[28..], (ushort)(4 + pad));
        entryName.CopyTo(dst[30..]);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[(30 + entryName.Length)..], 0x1986);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[(32 + entryName.Length)..], (ushort)pad);
        payload.CopyTo(dst[dataStart..]);
        var c = dst[cd..];
        BinaryPrimitives.WriteUInt32LittleEndian(c, 0x02014B50);
        BinaryPrimitives.WriteUInt16LittleEndian(c[4..], 10);
        BinaryPrimitives.WriteUInt16LittleEndian(c[6..], 10);
        BinaryPrimitives.WriteUInt32LittleEndian(c[16..], crc);
        BinaryPrimitives.WriteInt32LittleEndian(c[20..], payload.Length);
        BinaryPrimitives.WriteInt32LittleEndian(c[24..], payload.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(c[28..], (ushort)entryName.Length);
        entryName.CopyTo(c[46..]);
        var e = dst[(cd + 46 + entryName.Length)..];
        BinaryPrimitives.WriteUInt32LittleEndian(e, 0x06054B50);
        BinaryPrimitives.WriteUInt16LittleEndian(e[8..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(e[10..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(e[12..], 46 + entryName.Length);
        BinaryPrimitives.WriteInt32LittleEndian(e[16..], cd);
        return total;
    }

    // ══════════════════════ USDA 쓰기 ══════════════════════

    /// <summary>레이어 → USDA 텍스트(UTF-8 · 중첩 프림 · InvariantCulture 수치). 반환 = 바이트 수(공간 부족 −1).</summary>
    public static int WriteUsda(UsdLayer l, Span<byte> dst)
    {
        var w = new Utf8Writer(dst);
        w.Raw("#usda 1.0\n(\n    doc = \"PromptFlow Studio v16.72.0\"\n    upAxis = \"Y\"\n)\n");
        for (var i = 0; i < l.Count; i++) if (l.Parent[i] < 0) WritePrim(l, i, 0, ref w);
        return w.Overflow ? -1 : w.Length;
    }

    private static void WritePrim(UsdLayer l, int i, int depth, ref Utf8Writer w)
    {
        w.Indent(depth);
        w.Raw(l.Spec[i] switch { (byte)UsdSpec.Over => "over ", (byte)UsdSpec.Class => "class ", _ => "def " });
        var type = (UsdPrimType)l.Type[i];
        if (type != UsdPrimType.None) { w.Raw(type switch { UsdPrimType.Xform => "Xform", UsdPrimType.Scope => "Scope", UsdPrimType.Sphere => "Sphere", UsdPrimType.Cube => "Cube", UsdPrimType.Points => "Points", _ => "DistantLight" }); w.Raw(" "); }
        w.Raw("\""); w.Chars(l.NameOf(i)); w.Raw("\"\n");
        w.Indent(depth); w.Raw("{\n");
        var f = (UsdField)l.Fields[i];
        var d = depth + 1;
        if ((f & UsdField.Translate) != 0) { w.Indent(d); w.Raw("double3 xformOp:translate = "); w.Vec(l.Tx[i], l.Ty[i], l.Tz[i]); w.Raw("\n"); }
        if ((f & UsdField.Scale) != 0) { w.Indent(d); w.Raw("float3 xformOp:scale = "); w.Vec(l.Sx[i], l.Sy[i], l.Sz[i]); w.Raw("\n"); }
        if ((f & UsdField.Color) != 0) { w.Indent(d); w.Raw("color3f[] primvars:displayColor = ["); w.Vec(l.Cr[i], l.Cg[i], l.Cb[i]); w.Raw("]\n"); }
        if ((f & UsdField.Radius) != 0) { w.Indent(d); w.Raw("double radius = "); w.Num(l.Radius[i]); w.Raw("\n"); }
        if ((f & UsdField.Size) != 0) { w.Indent(d); w.Raw("double size = "); w.Num(l.Size[i]); w.Raw("\n"); }
        if ((f & UsdField.Mass) != 0) { w.Indent(d); w.Raw("float physics:mass = "); w.Num(l.Mass[i]); w.Raw("\n"); }
        if ((f & UsdField.RigidBody) != 0) { w.Indent(d); w.Raw(l.Rigid[i] != 0 ? "bool physics:rigidBodyEnabled = true\n" : "bool physics:rigidBodyEnabled = false\n"); }
        if ((f & UsdField.Intensity) != 0) { w.Indent(d); w.Raw("float inputs:intensity = "); w.Num(l.Intensity[i]); w.Raw("\n"); }
        if ((f & UsdField.Visibility) != 0) { w.Indent(d); w.Raw(l.Invisible[i] != 0 ? "token visibility = \"invisible\"\n" : "token visibility = \"inherited\"\n"); }
        if ((f & UsdField.Points) != 0)
        {
            w.Indent(d); w.Raw("point3f[] points = [");
            var p = l.PointsOf(i);
            for (var k = 0; k + 2 < p.Length; k += 3) { if (k > 0) w.Raw(", "); w.Vec(p[k], p[k + 1], p[k + 2]); }
            w.Raw("]\n");
        }
        if ((f & UsdField.TimeSamples) != 0)
        {
            w.Indent(d); w.Raw("double3 xformOp:translate.timeSamples = {\n");
            for (var k = 0; k < l.TsCount[i]; k++)
            {
                var o = l.TsOff[i] + k * 4;
                w.Indent(d + 1); w.Num(l.Ts[o]); w.Raw(": "); w.Vec(l.Ts[o + 1], l.Ts[o + 2], l.Ts[o + 3]); w.Raw(",\n");
            }
            w.Indent(d); w.Raw("}\n");
        }
        for (var c = i + 1; c < l.Count; c++) if (l.Parent[c] == i) WritePrim(l, c, depth + 1, ref w);
        w.Indent(depth); w.Raw("}\n");
    }

    private ref struct Utf8Writer
    {
        private readonly Span<byte> _d;
        public int Length;
        public bool Overflow;

        public Utf8Writer(Span<byte> d) { _d = d; Length = 0; Overflow = false; }

        public void Raw(string s)
        {
            foreach (var c in s) Byte((byte)c);
        }

        public void Chars(ReadOnlySpan<char> s) { foreach (var c in s) Byte(c < 128 ? (byte)c : (byte)'_'); }

        private void Byte(byte b)
        {
            if (Length >= _d.Length) { Overflow = true; return; }
            _d[Length++] = b;
        }

        public void Indent(int depth) { for (var i = 0; i < depth * 4; i++) Byte((byte)' '); }

        public void Num(float v)
        {
            if (Length < _d.Length && v.TryFormat(_d[Length..], out var n, "0.######", System.Globalization.CultureInfo.InvariantCulture)) Length += n;
            else Overflow = true;
        }

        public void Vec(float x, float y, float z) { Byte((byte)'('); Num(x); Raw(", "); Num(y); Raw(", "); Num(z); Byte((byte)')'); }
    }

    // ══════════════════════ 유틸 ══════════════════════

    private static bool Bad(UsdLayer l, UsdError e) { l.LastError = e; return false; }
    private static void W(Span<byte> d, ref int o, int v) { BinaryPrimitives.WriteInt32LittleEndian(d[o..], v); o += 4; }
    private static void F(Span<byte> d, ref int o, float v) { BinaryPrimitives.WriteSingleLittleEndian(d[o..], v); o += 4; }
    private static int R(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadInt32LittleEndian(s[o..]);
    private static int Ri(ReadOnlySpan<byte> s, ref int o) { var v = BinaryPrimitives.ReadInt32LittleEndian(s[o..]); o += 4; return v; }
    private static float Rf(ReadOnlySpan<byte> s, ref int o) { var v = BinaryPrimitives.ReadSingleLittleEndian(s[o..]); o += 4; return v; }

    private static uint[] BuildCrc()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Usd.Compositor.OnChange", ex); }
    }
}
