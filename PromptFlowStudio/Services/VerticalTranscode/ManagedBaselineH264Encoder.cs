using System.Numerics;
namespace PromptFlowStudio.Services.VerticalTranscode;

/// <summary>RBSP 비트 기록기(무할당 · 호출자 버퍼).</summary>
public ref struct H264BitWriter
{
    private readonly Span<byte> _d;
    private int _byte;
    private ulong _acc;
    private int _bits;

    public H264BitWriter(Span<byte> dst) { _d = dst; _byte = 0; _acc = 0; _bits = 0; }
    public int BitPosition => _byte * 8 + _bits;

    public void Bits(uint v, int n)
    {
        if (n == 0) return;
        _acc = _acc << n | (v & (n == 32 ? 0xFFFFFFFFu : (1u << n) - 1));
        _bits += n;
        while (_bits >= 8) { _bits -= 8; _d[_byte++] = (byte)(_acc >> _bits); }
    }

    public void Bit(bool b) => Bits(b ? 1u : 0u, 1);

    public void Ue(uint v)
    {
        var x = v + 1;
        var len = 32 - BitOperations.LeadingZeroCount(x);
        Bits(0, len - 1);
        Bits(x, len);
    }

    public void Se(int v) => Ue(v > 0 ? (uint)(2 * v - 1) : (uint)(-2 * v));

    /// <summary>rbsp_trailing_bits — 바이트 길이 반환.</summary>
    public int Trailing()
    {
        Bits(1, 1);
        if (_bits > 0) Bits(0, 8 - _bits);
        return _byte;
    }
}

/// <summary>
/// [PromptFlow Studio v17.49.0] 🎞️ 관리 코드 H.264 Constrained Baseline 인코더 — OS 미디어 스택(Media Foundation) 부재 시 최종 소프트웨어 폴백(무할당).
/// IDR 프레임: 전 매크로블록 Intra16x16(DC 예측) · 루마 DC 4×4 하다마드 + 크로마 DC 2×2 하다마드만 CAVLC 로 부호화(AC 미전송 · cbp 루마 0) ·
/// 디블로킹 비활성(disable_deblocking_filter_idc = 1) → 인코더 재구성 = 디코더 출력(비트 동일). 반복 프레임: P_Skip 전체(mb_skip_run).
/// 루마 DC 계수 수는 <see cref="MaxLumaDcCoeffs"/>(기본 8)로 상한 — coeff_token 사용 범위를 검증된 표 영역으로 제한한다.
/// 출력은 Annex-B(00 00 00 01) · IDR 앞에 SPS/PPS 동봉 · 에뮬레이션 방지 바이트 삽입.
/// </summary>
public sealed class ManagedBaselineH264Encoder
{
    public const int MinQp = 10, MaxQp = 51;
    // CAVLC 표(ISO/IEC 14496-10 표 9-5 · 9-7 · 9-8 · 9-9 · 9-10) — nC 0..1 열 · 크로마 DC(nC = −1) 열.
    private static ReadOnlySpan<byte> CtLen => new byte[] { 1, 0, 0, 0, 6, 2, 0, 0, 8, 6, 3, 0, 9, 8, 7, 5, 10, 9, 8, 6, 11, 10, 9, 7, 13, 11, 10, 8, 13, 13, 11, 9, 13, 13, 13, 10 };
    private static ReadOnlySpan<byte> CtBits => new byte[] { 1, 0, 0, 0, 5, 1, 0, 0, 7, 4, 1, 0, 7, 6, 5, 3, 7, 6, 5, 3, 7, 6, 5, 4, 15, 6, 5, 4, 11, 14, 5, 4, 8, 10, 13, 4 };
    private static ReadOnlySpan<byte> CdcLen => new byte[] { 2, 0, 0, 0, 6, 1, 0, 0, 6, 6, 3, 0, 6, 7, 7, 6, 6, 8, 8, 7 };
    private static ReadOnlySpan<byte> CdcBits => new byte[] { 1, 0, 0, 0, 7, 1, 0, 0, 4, 6, 1, 0, 3, 3, 2, 5, 2, 3, 2, 0 };
    private static ReadOnlySpan<byte> TzLen => new byte[]
    {
        1, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 9,
        3, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 6, 6, 6, 6, 0,
        4, 3, 3, 3, 4, 4, 3, 3, 4, 5, 5, 6, 5, 6, 0, 0,
        5, 3, 4, 4, 3, 3, 3, 4, 3, 4, 5, 5, 5, 0, 0, 0,
        4, 4, 4, 3, 3, 3, 3, 3, 4, 5, 4, 5, 0, 0, 0, 0,
        6, 5, 3, 3, 3, 3, 3, 3, 4, 3, 6, 0, 0, 0, 0, 0,
        6, 5, 3, 3, 3, 2, 3, 4, 3, 6, 0, 0, 0, 0, 0, 0,
        6, 4, 5, 3, 2, 2, 3, 3, 6, 0, 0, 0, 0, 0, 0, 0,
    };
    private static ReadOnlySpan<byte> TzBits => new byte[]
    {
        1, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 1,
        7, 6, 5, 4, 3, 5, 4, 3, 2, 3, 2, 3, 2, 1, 0, 0,
        5, 7, 6, 5, 4, 3, 4, 3, 2, 3, 2, 1, 1, 0, 0, 0,
        3, 7, 5, 4, 6, 5, 4, 3, 3, 2, 2, 1, 0, 0, 0, 0,
        5, 4, 3, 7, 6, 5, 4, 3, 2, 1, 1, 0, 0, 0, 0, 0,
        1, 1, 7, 6, 5, 4, 3, 2, 1, 1, 0, 0, 0, 0, 0, 0,
        1, 1, 5, 4, 3, 3, 2, 1, 1, 0, 0, 0, 0, 0, 0, 0,
        1, 1, 1, 3, 3, 2, 2, 1, 0, 0, 0, 0, 0, 0, 0, 0,
    };
    private static ReadOnlySpan<byte> CTzLen => new byte[] { 1, 2, 3, 3, 1, 2, 2, 0, 1, 1, 0, 0 };
    private static ReadOnlySpan<byte> CTzBits => new byte[] { 1, 1, 1, 0, 1, 1, 0, 0, 1, 0, 0, 0 };
    private static ReadOnlySpan<byte> RunLen => new byte[]
    {
        1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 2, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        2, 2, 2, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        2, 2, 2, 3, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        2, 2, 3, 3, 3, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        2, 3, 3, 3, 3, 3, 3, 0, 0, 0, 0, 0, 0, 0, 0,
        3, 3, 3, 3, 3, 3, 3, 4, 5, 6, 7, 8, 9, 10, 11,
    };
    private static ReadOnlySpan<byte> RunBits => new byte[]
    {
        1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        3, 2, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        3, 2, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        3, 2, 3, 2, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        3, 0, 1, 3, 2, 5, 4, 0, 0, 0, 0, 0, 0, 0, 0,
        7, 6, 5, 4, 3, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    };
    /// <summary>4×4 지그재그 스캔 → 래스터(행 × 4 + 열).</summary>
    private static ReadOnlySpan<byte> Zig => new byte[] { 0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15 };
    private static ReadOnlySpan<byte> QpC => new byte[] { 29, 30, 31, 32, 32, 33, 34, 34, 35, 35, 36, 36, 37, 37, 37, 38, 38, 38, 39, 39, 39, 39 };
    private static readonly ushort[] Mf0 = { 13107, 11916, 10082, 9362, 8192, 7282 };
    private static ReadOnlySpan<byte> V0 => new byte[] { 10, 11, 13, 14, 16, 18 };

    private readonly byte[] _recY, _recU, _recV, _rbsp;
    private int _idrId, _frameNum;

    public ManagedBaselineH264Encoder(int width, int height, int fps)
    {
        if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0) throw new ArgumentException("even positive size required");
        Width = width; Height = height; Fps = fps;
        MbW = (width + 15) >> 4; MbH = (height + 15) >> 4;
        PadW = MbW * 16; PadH = MbH * 16;
        _recY = new byte[PadW * PadH]; _recU = new byte[PadW * PadH / 4]; _recV = new byte[PadW * PadH / 4];
        _rbsp = new byte[MbW * MbH * 160 + 256];
    }

    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }
    public int MbW { get; }
    public int MbH { get; }
    public int PadW { get; }
    public int PadH { get; }
    public int Qp { get; set; } = 30;
    public int MaxLumaDcCoeffs { get; set; } = 8;
    public long IdrFrames { get; private set; }
    public long SkipFrames { get; private set; }
    /// <summary>디코더 출력과 비트 동일한 재구성 루마(PadW 보폭).</summary>
    public ReadOnlySpan<byte> ReconY => _recY;
    public ReadOnlySpan<byte> ReconU => _recU;
    public ReadOnlySpan<byte> ReconV => _recV;
    /// <summary>프레임당 최대 출력 바이트(호출자 버퍼 크기).</summary>
    public int MaxFrameBytes => _rbsp.Length * 3 / 2 + 512;
    public static int ChromaQp(int qp) => qp < 30 ? qp : QpC[Math.Min(qp, 51) - 30];

    // ══════════════════════ 파라미터 세트 ══════════════════════
    public int WriteSps(Span<byte> rbsp)
    {
        var w = new H264BitWriter(rbsp);
        w.Bits(66, 8); w.Bits(0xC0, 8); w.Bits(Fps > 30 ? 42u : 40u, 8);
        w.Ue(0); w.Ue(0); w.Ue(2); w.Ue(1); w.Bit(false);
        w.Ue((uint)(MbW - 1)); w.Ue((uint)(MbH - 1));
        w.Bit(true); w.Bit(true);
        var crop = PadW != Width || PadH != Height;
        w.Bit(crop);
        if (crop) { w.Ue(0); w.Ue((uint)((PadW - Width) / 2)); w.Ue(0); w.Ue((uint)((PadH - Height) / 2)); }
        w.Bit(true); // vui
        w.Bit(false); w.Bit(false);
        w.Bit(true); w.Bits(5, 3); w.Bit(false); w.Bit(true); w.Bits(1, 8); w.Bits(1, 8); w.Bits(1, 8);
        w.Bit(false);
        w.Bit(true); w.Bits(1, 32); w.Bits((uint)(2 * Fps), 32); w.Bit(true);
        w.Bit(false); w.Bit(false); w.Bit(false); w.Bit(false);
        return w.Trailing();
    }

    public static int WritePps(Span<byte> rbsp)
    {
        var w = new H264BitWriter(rbsp);
        w.Ue(0); w.Ue(0); w.Bit(false); w.Bit(false); w.Ue(0); w.Ue(0); w.Ue(0); w.Bit(false); w.Bits(0, 2);
        w.Se(0); w.Se(0); w.Se(0);
        w.Bit(true); w.Bit(false); w.Bit(false);
        return w.Trailing();
    }

    /// <summary>Annex-B NAL 기록(시작 코드 · 헤더 · 에뮬레이션 방지).</summary>
    public static int WriteNal(Span<byte> dst, int refIdc, int type, ReadOnlySpan<byte> rbsp)
    {
        dst[0] = 0; dst[1] = 0; dst[2] = 0; dst[3] = 1; dst[4] = (byte)(refIdc << 5 | type);
        var n = 5; var zeros = 0;
        foreach (var b in rbsp)
        {
            if (zeros >= 2 && b <= 3) { dst[n++] = 3; zeros = 0; }
            dst[n++] = b;
            zeros = b == 0 ? zeros + 1 : 0;
        }
        return n;
    }

    // ══════════════════════ 프레임 ══════════════════════
    /// <summary>NV12(Y 보폭 = Width, UV 인터리브) → SPS · PPS · IDR(Annex-B). 길이 반환.</summary>
    public int EncodeIdr(ReadOnlySpan<byte> y, ReadOnlySpan<byte> uv, Span<byte> dst)
    {
        var qp = Math.Clamp(Qp, MinQp, MaxQp);
        var n = 0;
        n += WriteNal(dst, 3, 7, _rbsp.AsSpan(0, WriteSps(_rbsp)));
        n += WriteNal(dst[n..], 3, 8, _rbsp.AsSpan(0, WritePps(_rbsp)));
        var w = new H264BitWriter(_rbsp);
        w.Ue(0); w.Ue(7); w.Ue(0); w.Bits(0, 4); w.Ue((uint)(_idrId & 1));
        w.Bit(false); w.Bit(false);
        w.Se(qp - 26); w.Ue(1);
        var qpc = ChromaQp(qp);
        for (var my = 0; my < MbH; my++)
            for (var mx = 0; mx < MbW; mx++)
                EncodeMb(ref w, y, uv, mx, my, qp, qpc);
        n += WriteNal(dst[n..], 3, 5, _rbsp.AsSpan(0, w.Trailing()));
        _idrId++; _frameNum = 0; IdrFrames++;
        return n;
    }

    /// <summary>직전 참조 프레임 반복(P 슬라이스 · 전 MB 스킵). 길이 반환.</summary>
    public int EncodeSkip(Span<byte> dst)
    {
        _frameNum = (_frameNum + 1) & 15;
        var w = new H264BitWriter(_rbsp);
        w.Ue(0); w.Ue(5); w.Ue(0); w.Bits((uint)_frameNum, 4);
        w.Bit(false); w.Bit(false); w.Bit(false);
        w.Se(Math.Clamp(Qp, MinQp, MaxQp) - 26); w.Ue(1);
        w.Ue((uint)(MbW * MbH));
        SkipFrames++;
        return WriteNal(dst, 2, 1, _rbsp.AsSpan(0, w.Trailing()));
    }

    private void EncodeMb(ref H264BitWriter w, ReadOnlySpan<byte> sy, ReadOnlySpan<byte> suv, int mx, int my, int qp, int qpc)
    {
        Span<int> dc = stackalloc int[16];
        Span<int> lev = stackalloc int[16];
        Span<int> cdc = stackalloc int[8];
        int x0 = mx * 16, y0 = my * 16;
        bool top = my > 0, left = mx > 0;
        // ── 루마 Intra16x16 DC 예측 ──
        int sum = 0, pred;
        if (top) for (var i = 0; i < 16; i++) sum += _recY[(y0 - 1) * PadW + x0 + i];
        if (left) for (var i = 0; i < 16; i++) sum += _recY[(y0 + i) * PadW + x0 - 1];
        pred = top && left ? (sum + 16) >> 5 : top || left ? (sum + 8) >> 4 : 128;
        for (var by = 0; by < 4; by++)
            for (var bx = 0; bx < 4; bx++)
            {
                var s = 0;
                for (var j = 0; j < 4; j++)
                {
                    var yy = Math.Min(y0 + by * 4 + j, Height - 1);
                    var row = yy * Width;
                    for (var i = 0; i < 4; i++) s += sy[row + Math.Min(x0 + bx * 4 + i, Width - 1)] - pred;
                }
                dc[by * 4 + bx] = s;
            }
        Hadamard4(dc);
        var qbits = 15 + qp / 6; var mf = Mf0[qp % 6]; var f = (1 << (qbits + 1)) / 3;
        for (var i = 0; i < 16; i++)
        {
            var v = dc[i] / 2;
            var a = (int)(((long)Math.Abs(v) * mf + f) >> (qbits + 1));
            lev[i] = Math.Clamp(v < 0 ? -a : a, -2000, 2000);
        }
        // 지그재그 상한 — 고주파부터 제거
        var kept = 0;
        for (var k = 0; k < 16; k++) { var p = Zig[k]; if (lev[p] != 0 && ++kept > MaxLumaDcCoeffs) lev[p] = 0; }
        // 재구성(8.5.10)
        lev.CopyTo(dc);
        Hadamard4(dc);
        var ls = 16 * V0[qp % 6];
        for (var i = 0; i < 16; i++)
            dc[i] = qp >= 36 ? (dc[i] * ls) << (qp / 6 - 6) : (dc[i] * ls + (1 << (5 - qp / 6))) >> (6 - qp / 6);
        for (var by = 0; by < 4; by++)
            for (var bx = 0; bx < 4; bx++)
            {
                var r = (dc[by * 4 + bx] + 32) >> 6;
                var v = (byte)Math.Clamp(pred + r, 0, 255);
                for (var j = 0; j < 4; j++) _recY.AsSpan((y0 + by * 4 + j) * PadW + x0 + bx * 4, 4).Fill(v);
            }
        // ── 크로마 DC ──
        var cw = PadW / 2; int cx0 = mx * 8, cy0 = my * 8, sw = Width / 2, sh = Height / 2;
        Span<int> cpred = stackalloc int[8];
        var cbp = 0;
        var cmf = Mf0[qpc % 6]; var cqb = 15 + qpc / 6; var cf = (1 << (cqb + 1)) / 3;
        for (var c = 0; c < 2; c++)
        {
            var rec = c == 0 ? _recU : _recV;
            for (var b = 0; b < 4; b++)
            {
                int bx = b & 1, by = b >> 1, st = 0, sl = 0;
                if (top) for (var i = 0; i < 4; i++) st += rec[(cy0 - 1) * cw + cx0 + bx * 4 + i];
                if (left) for (var i = 0; i < 4; i++) sl += rec[(cy0 + by * 4 + i) * cw + cx0 - 1];
                int p;
                if (bx == by) p = top && left ? (st + sl + 4) >> 3 : left ? (sl + 2) >> 2 : top ? (st + 2) >> 2 : 128;
                else if (bx == 1) p = top ? (st + 2) >> 2 : left ? (sl + 2) >> 2 : 128;
                else p = left ? (sl + 2) >> 2 : top ? (st + 2) >> 2 : 128;
                cpred[c * 4 + b] = p;
                var s = 0;
                for (var j = 0; j < 4; j++)
                {
                    var yy = Math.Min(cy0 + by * 4 + j, sh - 1);
                    for (var i = 0; i < 4; i++) s += suv[yy * Width + 2 * Math.Min(cx0 + bx * 4 + i, sw - 1) + c] - p;
                }
                cdc[c * 4 + b] = s;
            }
            var q = cdc.Slice(c * 4, 4);
            int a0 = q[0] + q[1] + q[2] + q[3], a1 = q[0] - q[1] + q[2] - q[3], a2 = q[0] + q[1] - q[2] - q[3], a3 = q[0] - q[1] - q[2] + q[3];
            q[0] = a0; q[1] = a1; q[2] = a2; q[3] = a3;
            for (var i = 0; i < 4; i++)
            {
                var a = (int)(((long)Math.Abs(q[i]) * cmf + cf) >> (cqb + 1));
                q[i] = Math.Clamp(q[i] < 0 ? -a : a, -2000, 2000);
                if (q[i] != 0) cbp = 1;
            }
        }
        // ── 매크로블록 계층 ──
        w.Ue((uint)(3 + 4 * cbp)); w.Ue(0); w.Se(0);
        Span<int> scan = stackalloc int[16];
        for (var k = 0; k < 16; k++) scan[k] = lev[Zig[k]];
        Residual(ref w, scan, 16, false);
        if (cbp != 0) { Residual(ref w, cdc[..4], 4, true); Residual(ref w, cdc[4..], 4, true); }
        // 크로마 재구성(8.5.11.2)
        var cls = 16 * V0[qpc % 6];
        for (var c = 0; c < 2; c++)
        {
            var rec = c == 0 ? _recU : _recV;
            var q = cdc.Slice(c * 4, 4);
            int f0 = q[0] + q[1] + q[2] + q[3], f1 = q[0] - q[1] + q[2] - q[3], f2 = q[0] + q[1] - q[2] - q[3], f3 = q[0] - q[1] - q[2] + q[3];
            for (var b = 0; b < 4; b++)
            {
                var fv = b == 0 ? f0 : b == 1 ? f1 : b == 2 ? f2 : f3;
                var d = ((fv * cls) << (qpc / 6)) >> 5;
                var v = (byte)Math.Clamp(cpred[c * 4 + b] + ((d + 32) >> 6), 0, 255);
                int bx = b & 1, by = b >> 1;
                for (var j = 0; j < 4; j++) rec.AsSpan((cy0 + by * 4 + j) * cw + cx0 + bx * 4, 4).Fill(v);
            }
        }
    }

    private static void Hadamard4(Span<int> m)
    {
        Span<int> t = stackalloc int[16];
        for (var r = 0; r < 4; r++)
        {
            int a = m[r * 4], b = m[r * 4 + 1], c = m[r * 4 + 2], d = m[r * 4 + 3];
            t[r * 4] = a + b + c + d; t[r * 4 + 1] = a + b - c - d; t[r * 4 + 2] = a - b - c + d; t[r * 4 + 3] = a - b + c - d;
        }
        for (var col = 0; col < 4; col++)
        {
            int a = t[col], b = t[4 + col], c = t[8 + col], d = t[12 + col];
            m[col] = a + b + c + d; m[4 + col] = a + b - c - d; m[8 + col] = a - b - c + d; m[12 + col] = a - b + c - d;
        }
    }

    /// <summary>residual_block_cavlc — 루마(nC = 0) 또는 크로마 DC(nC = −1).</summary>
    private static void Residual(ref H264BitWriter w, scoped ReadOnlySpan<int> c, int maxCoeff, bool chromaDc)
    {
        Span<int> lv = stackalloc int[16];
        Span<int> pos = stackalloc int[16];
        var tc = 0;
        for (var k = c.Length - 1; k >= 0; k--) if (c[k] != 0) { lv[tc] = c[k]; pos[tc] = k; tc++; }
        var t1 = 0;
        while (t1 < tc && t1 < 3 && Math.Abs(lv[t1]) == 1) t1++;
        if (chromaDc) w.Bits(CdcBits[tc * 4 + t1], CdcLen[tc * 4 + t1]);
        else w.Bits(CtBits[tc * 4 + t1], CtLen[tc * 4 + t1]);
        if (tc == 0) return;
        for (var i = 0; i < t1; i++) w.Bit(lv[i] < 0);
        var sl = tc > 10 && t1 < 3 ? 1 : 0;
        for (var i = t1; i < tc; i++)
        {
            var l = lv[i];
            var code = l > 0 ? 2 * l - 2 : -2 * l - 1;
            if (i == t1 && t1 < 3) code -= 2;
            int prefix, suffix, size;
            if (sl == 0)
            {
                if (code < 14) { prefix = code; suffix = 0; size = 0; }
                else if (code < 30) { prefix = 14; suffix = code - 14; size = 4; }
                else { prefix = 15; suffix = code - 30; size = 12; }
            }
            else if (code < 15 << sl) { prefix = code >> sl; suffix = code & ((1 << sl) - 1); size = sl; }
            else { prefix = 15; suffix = code - (15 << sl); size = 12; }
            w.Bits(1, prefix + 1);
            w.Bits((uint)suffix, size);
            if (sl == 0) sl = 1;
            if (Math.Abs(l) > 3 << (sl - 1) && sl < 6) sl++;
        }
        var tz = pos[0] + 1 - tc;
        if (tc < maxCoeff)
        {
            if (chromaDc) w.Bits(CTzBits[(tc - 1) * 4 + tz], CTzLen[(tc - 1) * 4 + tz]);
            else w.Bits(TzBits[(tc - 1) * 16 + tz], TzLen[(tc - 1) * 16 + tz]);
        }
        var left = tz;
        for (var i = 0; i < tc - 1 && left > 0; i++)
        {
            var run = pos[i] - pos[i + 1] - 1;
            var row = Math.Min(left, 7) - 1;
            w.Bits(RunBits[row * 15 + run], RunLen[row * 15 + run]);
            left -= run;
        }
    }
}
