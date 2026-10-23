using System.Diagnostics;

namespace PromptFlowStudio.Services.RetentionHeatmap;

/// <summary>RBSP 비트 리더(MSB 우선 · Exp-Golomb).</summary>
public ref struct RbspBitReader
{
    private readonly ReadOnlySpan<byte> _s;
    public int Pos;
    public bool Overrun;

    public RbspBitReader(ReadOnlySpan<byte> s) { _s = s; Pos = 0; Overrun = false; }

    public int Bits => _s.Length * 8;

    public uint U1()
    {
        if (Pos >= _s.Length * 8) { Overrun = true; return 0; }
        var b = (uint)(_s[Pos >> 3] >> (7 - (Pos & 7))) & 1u;
        Pos++;
        return b;
    }

    public uint U(int n)
    {
        uint v = 0;
        for (var i = 0; i < n; i++) v = (v << 1) | U1();
        return v;
    }

    public uint Ue()
    {
        var lz = 0;
        while (U1() == 0) { if (++lz > 31 || Overrun) { Overrun = true; return 0; } }
        return lz == 0 ? 0 : (uint)((1UL << lz) - 1 + U(lz));
    }

    public int Se()
    {
        var k = Ue();
        return (k & 1) != 0 ? (int)((k + 1) >> 1) : -(int)(k >> 1);
    }
}

/// <summary>RBSP 비트 라이터(MSB 우선 · Exp-Golomb) — 고정 버퍼 · 초과 시 <see cref="Overrun"/>.</summary>
public ref struct RbspBitWriter
{
    private readonly Span<byte> _d;
    public int Pos;
    public bool Overrun;

    public RbspBitWriter(Span<byte> d) { _d = d; Pos = 0; Overrun = false; }

    public void U1(uint b)
    {
        if (Pos >= _d.Length * 8) { Overrun = true; return; }
        var i = Pos >> 3;
        if ((Pos & 7) == 0) _d[i] = 0;
        if ((b & 1) != 0) _d[i] |= (byte)(0x80 >> (Pos & 7));
        Pos++;
    }

    public void U(uint v, int n) { for (var i = n - 1; i >= 0; i--) U1(v >> i); }

    public void Ue(uint v)
    {
        var x = (ulong)v + 1;
        var len = 64 - System.Numerics.BitOperations.LeadingZeroCount(x);
        for (var i = 0; i < len - 1; i++) U1(0);
        for (var i = len - 1; i >= 0; i--) U1((uint)(x >> i));
    }

    public void Se(int v) => Ue(v <= 0 ? (uint)(-2L * v) : (uint)(2L * v - 1));

    /// <summary>rbsp_trailing_bits — 정지 비트 1 + 바이트 정렬 0. 반환 = 바이트 수.</summary>
    public int Trail()
    {
        U1(1);
        while ((Pos & 7) != 0) U1(0);
        return Pos >> 3;
    }
}

/// <summary>
/// [PromptFlow Studio v17.98.1] ✂ H.264 SPS 크롭 비트 재작성기 — 재인코딩 0 줌 펀치.
///   ▸ SPS NAL(시작 코드 포함) → 에뮬레이션 방지 바이트 제거 → profile/스케일링 리스트/POC/해상도 파싱 → frame_cropping_flag 비트 위치 확보.
///   ▸ 기존 표시 창(크롭 반영)을 중심 기준 1/zoom 로 축소한 오프셋(CropUnitX/Y 정렬)을 Exp-Golomb 로 재기록 · 이후 VUI 비트는 원문 그대로 이어 붙임.
///   ▸ 정지 비트 재정렬 → 에뮬레이션 방지 재삽입 → 시작 코드 · NAL 헤더 원문 유지.
///   ▸ 디코더는 같은 픽셀을 디코딩하고 표시 창만 좁아짐 → 플레이어가 표시 해상도로 확대 = 줌 펀치(픽셀 무변경 · GOP 경계 IDR 에서만 활성).
///   ▸ 사전 할당 스크래치만 사용 — <see cref="Rewrite"/> 무할당.
/// </summary>
public sealed class H264SpsCropRewriter
{
    public const int MaxNal = 1024;

    private readonly byte[] _rbsp = new byte[MaxNal];
    private readonly byte[] _wbits = new byte[MaxNal + 64];

    public int WidthPx { get; private set; }
    public int HeightPx { get; private set; }
    /// <summary>재작성 전 표시 창(크롭 반영) 크기.</summary>
    public int SrcVisibleW { get; private set; }
    public int SrcVisibleH { get; private set; }
    /// <summary>재작성 후 표시 창 크기.</summary>
    public int VisibleW { get; private set; }
    public int VisibleH { get; private set; }
    public int CropLeft { get; private set; }
    public int CropRight { get; private set; }
    public int CropTop { get; private set; }
    public int CropBottom { get; private set; }
    public long Rewrites { get; private set; }
    public long Failures { get; private set; }
    public double LastUs { get; private set; }

    /// <summary>실효 줌 배율(원 표시 폭 ÷ 새 표시 폭).</summary>
    public float EffectiveZoom => VisibleW > 0 ? (float)SrcVisibleW / VisibleW : 1f;

    /// <summary>
    /// SPS NAL(Annex-B 시작 코드 포함) 크롭 재작성 → <paramref name="dst"/>. 반환 = 바이트 수(−1 = SPS 아님/파싱 실패/버퍼 부족).
    /// <paramref name="zoom"/> ≤ 1 이면 원 크롭 유지 재기록.
    /// </summary>
    public int Rewrite(ReadOnlySpan<byte> nal, float zoom, Span<byte> dst)
    {
        var t0 = Stopwatch.GetTimestamp();
        var n = RewriteCore(nal, zoom, dst);
        if (n < 0) Failures++; else Rewrites++;
        LastUs = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
        return n;
    }

    private int RewriteCore(ReadOnlySpan<byte> nal, float zoom, Span<byte> dst)
    {
        // 시작 코드 길이(00 00 01 / 00 00 00 01) · 헤더 = nal_unit_type 7
        int sc;
        if (nal.Length > 4 && nal[0] == 0 && nal[1] == 0 && nal[2] == 1) sc = 3;
        else if (nal.Length > 5 && nal[0] == 0 && nal[1] == 0 && nal[2] == 0 && nal[3] == 1) sc = 4;
        else return -1;
        if ((nal[sc] & 0x1F) != 7) return -1;
        var rl = Unescape(nal[(sc + 1)..], _rbsp);
        if (rl <= 3) return -1;
        var rbsp = _rbsp.AsSpan(0, rl);
        // 정지 비트 = 마지막 0 아닌 바이트의 최하위 1
        var lastNz = rl - 1;
        while (lastNz >= 0 && rbsp[lastNz] == 0) lastNz--;
        if (lastNz < 0) return -1;
        var stopBit = lastNz * 8 + 7 - System.Numerics.BitOperations.TrailingZeroCount(rbsp[lastNz]);

        var r = new RbspBitReader(rbsp);
        var profile = r.U(8);
        r.U(16);   // constraint_set 플래그 · level_idc
        r.Ue();    // seq_parameter_set_id
        uint chroma = 1;
        var separate = false;
        if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
        {
            chroma = r.Ue();
            if (chroma > 3) return -1;
            if (chroma == 3) separate = r.U1() != 0;
            r.Ue(); r.Ue();   // bit_depth_luma/chroma_minus8
            r.U1();           // qpprime_y_zero_transform_bypass_flag
            if (r.U1() != 0)  // seq_scaling_matrix_present_flag
            {
                var lists = chroma != 3 ? 8 : 12;
                for (var i = 0; i < lists; i++)
                    if (r.U1() != 0) SkipScalingList(ref r, i < 6 ? 16 : 64);
            }
        }
        r.Ue();   // log2_max_frame_num_minus4
        var pocType = r.Ue();
        if (pocType == 0) r.Ue();
        else if (pocType == 1)
        {
            r.U1(); r.Se(); r.Se();
            var cycle = r.Ue();
            if (cycle > 255) return -1;
            for (var i = 0u; i < cycle; i++) r.Se();
        }
        else if (pocType != 2) return -1;
        r.Ue();   // max_num_ref_frames
        r.U1();   // gaps_in_frame_num_value_allowed_flag
        var wMbs = r.Ue() + 1;
        var hMap = r.Ue() + 1;
        var frameMbsOnly = r.U1();
        if (frameMbsOnly == 0) r.U1();   // mb_adaptive_frame_field_flag
        r.U1();   // direct_8x8_inference_flag
        var cropPos = r.Pos;
        uint cl = 0, cr = 0, ct = 0, cb = 0;
        if (r.U1() != 0) { cl = r.Ue(); cr = r.Ue(); ct = r.Ue(); cb = r.Ue(); }
        var tailPos = r.Pos;
        if (r.Overrun || tailPos > stopBit || wMbs > 1024 || hMap > 1024) return -1;

        // CropUnit(7.4.2.1.1) — 단색/분리 평면 = 1 · 4:2:0 = 2×2 · 4:2:2 = 2×1 · 4:4:4 = 1×1, 세로 × (2 − frame_mbs_only)
        var arr = separate ? 0u : chroma;
        int ux = arr is 1 or 2 ? 2 : 1, uy = (arr == 1 ? 2 : 1) * (2 - (int)frameMbsOnly);
        var w = (int)wMbs * 16;
        var h = (int)hMap * 16 * (2 - (int)frameMbsOnly);
        var visW = w - ux * (int)(cl + cr);
        var visH = h - uy * (int)(ct + cb);
        if (visW <= 0 || visH <= 0) return -1;
        WidthPx = w; HeightPx = h; SrcVisibleW = visW; SrcVisibleH = visH;
        if (zoom > 1f)
        {
            var nw = Math.Max(ux, (int)(visW / zoom) / ux * ux);
            var nh = Math.Max(uy, (int)(visH / zoom) / uy * uy);
            int ex = (visW - nw) / ux, ey = (visH - nh) / uy;
            cl += (uint)(ex / 2); cr += (uint)(ex - ex / 2);
            ct += (uint)(ey / 2); cb += (uint)(ey - ey / 2);
        }
        CropLeft = (int)cl; CropRight = (int)cr; CropTop = (int)ct; CropBottom = (int)cb;
        VisibleW = w - ux * (int)(cl + cr); VisibleH = h - uy * (int)(ct + cb);

        // 재기록: [0, cropPos) 원문 · 크롭 필드 · [tailPos, stopBit) 원문(VUI) · 정지 비트
        var wr = new RbspBitWriter(_wbits);
        var rd = new RbspBitReader(rbsp);
        while (rd.Pos < cropPos) wr.U1(rd.U1());
        var crop = (cl | cr | ct | cb) != 0;
        wr.U1(crop ? 1u : 0u);
        if (crop) { wr.Ue(cl); wr.Ue(cr); wr.Ue(ct); wr.Ue(cb); }
        rd.Pos = tailPos;
        while (rd.Pos < stopBit) wr.U1(rd.U1());
        var wl = wr.Trail();
        if (wr.Overrun) return -1;

        // 시작 코드 · 헤더 원문 + 에뮬레이션 방지 재삽입
        var hdr = sc + 1;
        if (dst.Length < hdr) return -1;
        nal[..hdr].CopyTo(dst);
        var e = Escape(_wbits.AsSpan(0, wl), dst[hdr..]);
        return e < 0 ? -1 : hdr + e;
    }

    private static void SkipScalingList(ref RbspBitReader r, int size)
    {
        int last = 8, next = 8;
        for (var j = 0; j < size; j++)
        {
            if (next != 0) next = (last + r.Se() + 256) % 256;
            last = next == 0 ? last : next;
        }
    }

    /// <summary>EBSP → RBSP(00 00 03 의 03 제거). 반환 = RBSP 바이트(−1 = 버퍼 부족).</summary>
    public static int Unescape(ReadOnlySpan<byte> ebsp, Span<byte> rbsp)
    {
        int n = 0, zeros = 0;
        foreach (var b in ebsp)
        {
            if (zeros >= 2 && b == 3) { zeros = 0; continue; }
            if (n == rbsp.Length) return -1;
            rbsp[n++] = b;
            zeros = b == 0 ? zeros + 1 : 0;
        }
        return n;
    }

    /// <summary>RBSP → EBSP(00 00 뒤 ≤ 03 앞에 03 삽입). 반환 = EBSP 바이트(−1 = 버퍼 부족).</summary>
    public static int Escape(ReadOnlySpan<byte> rbsp, Span<byte> ebsp)
    {
        int n = 0, zeros = 0;
        foreach (var b in rbsp)
        {
            if (zeros >= 2 && b <= 3)
            {
                if (n == ebsp.Length) return -1;
                ebsp[n++] = 3; zeros = 0;
            }
            if (n == ebsp.Length) return -1;
            ebsp[n++] = b;
            zeros = b == 0 ? zeros + 1 : 0;
        }
        return n;
    }

    /// <summary>재작성된 SPS 를 다시 파싱해 크롭 오프셋만 읽음(검증용 · 무할당). 반환 = 성공 여부.</summary>
    public bool TryReadCrop(ReadOnlySpan<byte> nal, out int visibleW, out int visibleH)
    {
        var saved = (WidthPx, HeightPx, SrcVisibleW, SrcVisibleH, VisibleW, VisibleH, CropLeft, CropRight, CropTop, CropBottom);
        SrcVisibleW = SrcVisibleH = 0;
        // 출력 없이 파싱만 — dst 길이 0 이면 헤더 복사 단계에서 −1 이지만 파싱 결과(SrcVisible*)는 갱신됨
        RewriteCore(nal, 1f, Span<byte>.Empty);
        visibleW = SrcVisibleW; visibleH = SrcVisibleH;
        var ok = visibleW > 0 && visibleH > 0;
        (WidthPx, HeightPx, SrcVisibleW, SrcVisibleH, VisibleW, VisibleH, CropLeft, CropRight, CropTop, CropBottom) = saved;
        return ok;
    }
}
