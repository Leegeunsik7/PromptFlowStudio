using System.Text;

namespace PromptFlowStudio.Services.Commerce;

public enum QrEcc : byte { Low, Medium }

/// <summary>QR 심볼(모듈 행렬 · 버전 · 오류 정정 · 마스크).</summary>
public sealed class QrCode
{
    public QrCode(int version, QrEcc ecc, int mask, bool[] modules)
    {
        Version = version; Ecc = ecc; Mask = mask; Modules = modules; Size = version * 4 + 17;
    }
    public int Version { get; }
    public QrEcc Ecc { get; }
    public int Mask { get; }
    public int Size { get; }
    public bool[] Modules { get; }
    public bool this[int x, int y] => Modules[y * Size + x];
}

/// <summary>
/// [PromptFlow Studio v16.48.0] 🔳 QR 코드 인코더(ISO/IEC 18004 · 순수 BCL).
///   ▸ 바이트 모드(UTF-8) · 버전 1~10 자동 선택 · 오류 정정 L/M · GF(256) 리드-솔로몬 · 블록 인터리브 · 기능 패턴(파인더/타이밍/정렬/다크 모듈) · BCH 포맷/버전 정보.
///   ▸ 마스크 8종 벌점(N1~N4) 최소 선택. 자기 검증용 판독기(언마스크 → 지그재그 판독 → 디인터리브 → 바이트 모드 파싱) 포함.
/// </summary>
public static class QrCodeEncoder
{
    public const int MinVersion = 1, MaxVersion = 10;

    // [버전-1] = (블록당 EC 코드워드, 그룹1 블록 수, 그룹1 데이터, 그룹2 블록 수, 그룹2 데이터)
    private static readonly (int Ec, int B1, int D1, int B2, int D2)[] LowTable =
    {
        (7, 1, 19, 0, 0), (10, 1, 34, 0, 0), (15, 1, 55, 0, 0), (20, 1, 80, 0, 0), (26, 1, 108, 0, 0),
        (18, 2, 68, 0, 0), (20, 2, 78, 0, 0), (24, 2, 97, 0, 0), (30, 2, 116, 0, 0), (18, 2, 68, 2, 69),
    };
    private static readonly (int Ec, int B1, int D1, int B2, int D2)[] MediumTable =
    {
        (10, 1, 16, 0, 0), (16, 1, 28, 0, 0), (26, 1, 44, 0, 0), (18, 2, 32, 0, 0), (24, 2, 43, 0, 0),
        (16, 4, 27, 0, 0), (18, 4, 31, 0, 0), (22, 2, 38, 2, 39), (22, 3, 36, 2, 37), (26, 4, 43, 1, 44),
    };
    private static readonly int[][] Align =
    {
        Array.Empty<int>(), new[] { 6, 18 }, new[] { 6, 22 }, new[] { 6, 26 }, new[] { 6, 30 }, new[] { 6, 34 },
        new[] { 6, 22, 38 }, new[] { 6, 24, 42 }, new[] { 6, 26, 46 }, new[] { 6, 28, 50 },
    };

    private static readonly byte[] Exp = new byte[512], Log = new byte[256];

    static QrCodeEncoder()
    {
        var x = 1;
        for (var i = 0; i < 255; i++) { Exp[i] = (byte)x; Log[x] = (byte)i; x <<= 1; if (x >= 256) x ^= 0x11D; }
        for (var i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
    }

    public static byte GfMul(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];

    private static (int Ec, int B1, int D1, int B2, int D2) Spec(int v, QrEcc e) => (e == QrEcc.Low ? LowTable : MediumTable)[v - 1];

    public static int DataCodewords(int v, QrEcc e) { var s = Spec(v, e); return s.B1 * s.D1 + s.B2 * s.D2; }

    /// <summary>바이트 모드 최대 바이트 수(문자 수 지시자 8비트 · 버전 1~9 / 16비트 · 10).</summary>
    public static int ByteCapacity(int v, QrEcc e) => (DataCodewords(v, e) * 8 - 4 - (v < 10 ? 8 : 16)) / 8;

    // ══════════════════════ 리드-솔로몬 ══════════════════════

    public static byte[] RsDivisor(int degree)
    {
        var r = new byte[degree];
        r[degree - 1] = 1;
        byte root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < r.Length; j++)
            {
                r[j] = GfMul(r[j], root);
                if (j + 1 < r.Length) r[j] ^= r[j + 1];
            }
            root = GfMul(root, 2);
        }
        return r;
    }

    public static byte[] RsRemainder(ReadOnlySpan<byte> data, byte[] divisor)
    {
        var r = new byte[divisor.Length];
        foreach (var b in data)
        {
            var f = (byte)(b ^ r[0]);
            Array.Copy(r, 1, r, 0, r.Length - 1);
            r[^1] = 0;
            for (var i = 0; i < r.Length; i++) r[i] ^= GfMul(divisor[i], f);
        }
        return r;
    }

    /// <summary>블록(데이터+EC) 신드롬 전부 0 여부 — 무결성 검증.</summary>
    public static bool SyndromesZero(ReadOnlySpan<byte> block, int ecLen)
    {
        for (var i = 0; i < ecLen; i++)
        {
            var x = Exp[i];
            byte s = 0;
            foreach (var c in block) s = (byte)(GfMul(s, x) ^ c);
            if (s != 0) return false;
        }
        return true;
    }

    // ══════════════════════ 인코딩 ══════════════════════

    public static QrCode Encode(string text, QrEcc ecc = QrEcc.Medium, int forceMask = -1)
    {
        var data = Encoding.UTF8.GetBytes(text);
        var v = MinVersion;
        while (v <= MaxVersion && ByteCapacity(v, ecc) < data.Length) v++;
        if (v > MaxVersion) throw new ArgumentException("QR 용량 초과(" + data.Length + "바이트 · 최대 " + ByteCapacity(MaxVersion, ecc) + ")");
        var cw = BuildCodewords(data, v, ecc);
        var size = v * 4 + 17;
        var func = new bool[size * size];
        var mods = new bool[size * size];
        DrawFunction(mods, func, size, v);
        PlaceData(mods, func, size, cw);
        var best = forceMask; var bestPenalty = int.MaxValue;
        if (best < 0)
            for (var m = 0; m < 8; m++)
            {
                ApplyMask(mods, func, size, m);
                DrawFormat(mods, func, size, ecc, m);
                var p = Penalty(mods, size);
                if (p < bestPenalty) { bestPenalty = p; best = m; }
                ApplyMask(mods, func, size, m);
            }
        ApplyMask(mods, func, size, best);
        DrawFormat(mods, func, size, ecc, best);
        return new QrCode(v, ecc, best, mods);
    }

    private static byte[] BuildCodewords(byte[] data, int v, QrEcc ecc)
    {
        var cap = DataCodewords(v, ecc);
        var bits = new List<bool>(cap * 8);
        void Put(int val, int n) { for (var i = n - 1; i >= 0; i--) bits.Add(((val >> i) & 1) != 0); }
        Put(0b0100, 4);
        Put(data.Length, v < 10 ? 8 : 16);
        foreach (var b in data) Put(b, 8);
        Put(0, Math.Min(4, cap * 8 - bits.Count));
        while (bits.Count % 8 != 0) bits.Add(false);
        var dc = new byte[cap];
        var n = bits.Count / 8;
        for (var i = 0; i < n; i++) { var b = 0; for (var k = 0; k < 8; k++) b = b << 1 | (bits[i * 8 + k] ? 1 : 0); dc[i] = (byte)b; }
        for (var i = n; i < cap; i++) dc[i] = (byte)((i - n) % 2 == 0 ? 0xEC : 0x11);

        var s = Spec(v, ecc);
        var blocks = s.B1 + s.B2;
        var div = RsDivisor(s.Ec);
        var dataBlocks = new byte[blocks][]; var ecBlocks = new byte[blocks][];
        var off = 0;
        for (var b = 0; b < blocks; b++)
        {
            var len = b < s.B1 ? s.D1 : s.D2;
            dataBlocks[b] = dc.AsSpan(off, len).ToArray(); off += len;
            ecBlocks[b] = RsRemainder(dataBlocks[b], div);
        }
        var res = new List<byte>(cap + blocks * s.Ec);
        var maxD = Math.Max(s.D1, s.D2);
        for (var i = 0; i < maxD; i++) for (var b = 0; b < blocks; b++) if (i < dataBlocks[b].Length) res.Add(dataBlocks[b][i]);
        for (var i = 0; i < s.Ec; i++) for (var b = 0; b < blocks; b++) res.Add(ecBlocks[b][i]);
        return res.ToArray();
    }

    private static void Set(bool[] m, bool[] f, int size, int x, int y, bool dark) { m[y * size + x] = dark; f[y * size + x] = true; }

    private static void DrawFunction(bool[] m, bool[] f, int size, int v)
    {
        for (var i = 0; i < size; i++) { Set(m, f, size, 6, i, i % 2 == 0); Set(m, f, size, i, 6, i % 2 == 0); }
        Finder(m, f, size, 3, 3); Finder(m, f, size, size - 4, 3); Finder(m, f, size, 3, size - 4);
        var a = Align[v - 1];
        for (var i = 0; i < a.Length; i++)
            for (var j = 0; j < a.Length; j++)
            {
                if (i == 0 && j == 0 || i == 0 && j == a.Length - 1 || i == a.Length - 1 && j == 0) continue;
                for (var dy = -2; dy <= 2; dy++) for (var dx = -2; dx <= 2; dx++) Set(m, f, size, a[i] + dx, a[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }
        DrawFormat(m, f, size, QrEcc.Medium, 0);   // 예약(이후 덮어씀)
        if (v >= 7)
        {
            var rem = v;
            for (var i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            var bits = v << 12 | rem;
            for (var i = 0; i < 18; i++)
            {
                var bit = ((bits >> i) & 1) != 0;
                int a1 = size - 11 + i % 3, b1 = i / 3;
                Set(m, f, size, a1, b1, bit); Set(m, f, size, b1, a1, bit);
            }
        }
    }

    private static void Finder(bool[] m, bool[] f, int size, int cx, int cy)
    {
        for (var dy = -4; dy <= 4; dy++)
            for (var dx = -4; dx <= 4; dx++)
            {
                int x = cx + dx, y = cy + dy;
                if ((uint)x >= (uint)size || (uint)y >= (uint)size) continue;
                var d = Math.Max(Math.Abs(dx), Math.Abs(dy));
                Set(m, f, size, x, y, d != 2 && d != 4);
            }
    }

    public static int FormatBits(QrEcc ecc, int mask)
    {
        var data = (ecc == QrEcc.Low ? 1 : 0) << 3 | mask;
        var rem = data;
        for (var i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        return (data << 10 | rem) ^ 0x5412;
    }

    private static void DrawFormat(bool[] m, bool[] f, int size, QrEcc ecc, int mask)
    {
        var bits = FormatBits(ecc, mask);
        bool B(int i) => ((bits >> i) & 1) != 0;
        for (var i = 0; i <= 5; i++) Set(m, f, size, 8, i, B(i));
        Set(m, f, size, 8, 7, B(6)); Set(m, f, size, 8, 8, B(7)); Set(m, f, size, 7, 8, B(8));
        for (var i = 9; i < 15; i++) Set(m, f, size, 14 - i, 8, B(i));
        for (var i = 0; i < 8; i++) Set(m, f, size, size - 1 - i, 8, B(i));
        for (var i = 8; i < 15; i++) Set(m, f, size, 8, size - 15 + i, B(i));
        Set(m, f, size, 8, size - 8, true);
    }

    /// <summary>지그재그 데이터 모듈 좌표 순회(우하단 → 2열 단위 · 6열 건너뜀).</summary>
    private static IEnumerable<(int X, int Y)> DataOrder(bool[] f, int size)
    {
        for (var right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (var vert = 0; vert < size; vert++)
                for (var j = 0; j < 2; j++)
                {
                    var x = right - j;
                    var upward = ((right + 1) & 2) == 0;
                    var y = upward ? size - 1 - vert : vert;
                    if (!f[y * size + x]) yield return (x, y);
                }
        }
    }

    private static void PlaceData(bool[] m, bool[] f, int size, byte[] cw)
    {
        var i = 0;
        foreach (var (x, y) in DataOrder(f, size))
        {
            if (i < cw.Length * 8) m[y * size + x] = ((cw[i >> 3] >> (7 - (i & 7))) & 1) != 0;
            i++;
        }
    }

    public static bool MaskBit(int mask, int x, int y) => mask switch
    {
        0 => (x + y) % 2 == 0, 1 => y % 2 == 0, 2 => x % 3 == 0, 3 => (x + y) % 3 == 0, 4 => (x / 3 + y / 2) % 2 == 0,
        5 => x * y % 2 + x * y % 3 == 0, 6 => (x * y % 2 + x * y % 3) % 2 == 0, _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
    };

    private static void ApplyMask(bool[] m, bool[] f, int size, int mask)
    {
        for (var y = 0; y < size; y++) for (var x = 0; x < size; x++) if (!f[y * size + x] && MaskBit(mask, x, y)) m[y * size + x] = !m[y * size + x];
    }

    public static int Penalty(bool[] m, int size)
    {
        var p = 0;
        for (var pass = 0; pass < 2; pass++)
            for (var a = 0; a < size; a++)
            {
                var run = 1;
                for (var b = 1; b < size; b++)
                {
                    bool cur = pass == 0 ? m[a * size + b] : m[b * size + a], prev = pass == 0 ? m[a * size + b - 1] : m[(b - 1) * size + a];
                    if (cur == prev) { run++; if (run == 5) p += 3; else if (run > 5) p++; }
                    else run = 1;
                }
                for (var b = 0; b + 10 < size; b++)
                {
                    bool At(int k) => pass == 0 ? m[a * size + b + k] : m[(b + k) * size + a];
                    var core = At(4) && !At(5) && At(6) && At(7) && At(8) && !At(9) && At(10);
                    var core2 = At(0) && !At(1) && At(2) && At(3) && At(4) && !At(5) && At(6);
                    if (core && !At(0) && !At(1) && !At(2) && !At(3)) p += 40;
                    if (core2 && !At(7) && !At(8) && !At(9) && !At(10)) p += 40;
                }
            }
        var dark = 0;
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                if (m[y * size + x]) dark++;
                if (x + 1 < size && y + 1 < size) { var c = m[y * size + x]; if (m[y * size + x + 1] == c && m[(y + 1) * size + x] == c && m[(y + 1) * size + x + 1] == c) p += 3; }
            }
        var total = size * size;
        p += Math.Abs(dark * 20 - total * 10) / total * 10;
        return p;
    }

    // ══════════════════════ 자기 검증 판독 ══════════════════════

    /// <summary>판독: 포맷 정보 → 마스크 해제 → 코드워드 → RS 신드롬 검증 → 바이트 모드 파싱. 실패 시 null.</summary>
    public static string? Decode(QrCode q)
    {
        var size = q.Size; var v = q.Version;
        var bits = 0;
        for (var i = 0; i <= 5; i++) if (q[8, i]) bits |= 1 << i;
        if (q[8, 7]) bits |= 1 << 6; if (q[8, 8]) bits |= 1 << 7; if (q[7, 8]) bits |= 1 << 8;
        for (var i = 9; i < 15; i++) if (q[14 - i, 8]) bits |= 1 << i;
        int mask = -1; var ecc = QrEcc.Medium;
        for (var e = 0; e < 2 && mask < 0; e++) for (var k = 0; k < 8; k++) if (FormatBits((QrEcc)e, k) == bits) { mask = k; ecc = (QrEcc)e; break; }
        if (mask < 0) return null;
        var func = new bool[size * size]; var tmp = new bool[size * size];
        DrawFunction(tmp, func, size, v);
        var cwCount = DataCodewords(v, ecc) + (Spec(v, ecc).B1 + Spec(v, ecc).B2) * Spec(v, ecc).Ec;
        var cw = new byte[cwCount];
        var i2 = 0;
        foreach (var (x, y) in DataOrder(func, size))
        {
            if (i2 >= cwCount * 8) break;
            var bit = q[x, y] ^ MaskBit(mask, x, y);
            if (bit) cw[i2 >> 3] |= (byte)(0x80 >> (i2 & 7));
            i2++;
        }
        var s = Spec(v, ecc);
        var blocks = s.B1 + s.B2;
        var blk = new List<byte>[blocks];
        for (var b = 0; b < blocks; b++) blk[b] = new List<byte>();
        var p = 0;
        var maxD = Math.Max(s.D1, s.D2);
        for (var i = 0; i < maxD; i++) for (var b = 0; b < blocks; b++) if (i < (b < s.B1 ? s.D1 : s.D2)) blk[b].Add(cw[p++]);
        var data = new List<byte>();
        foreach (var l in blk) data.AddRange(l);
        for (var i = 0; i < s.Ec; i++) for (var b = 0; b < blocks; b++) blk[b].Add(cw[p++]);
        foreach (var l in blk) if (!SyndromesZero(l.ToArray(), s.Ec)) return null;
        var bp = 0;
        int Read(int n) { var r = 0; for (var k = 0; k < n; k++, bp++) r = r << 1 | ((data[bp >> 3] >> (7 - (bp & 7))) & 1); return r; }
        if (Read(4) != 0b0100) return null;
        var len = Read(v < 10 ? 8 : 16);
        if (len * 8 + bp > data.Count * 8) return null;
        var bytes = new byte[len];
        for (var k = 0; k < len; k++) bytes[k] = (byte)Read(8);
        return Encoding.UTF8.GetString(bytes);
    }
}
