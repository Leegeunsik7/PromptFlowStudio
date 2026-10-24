using System.Diagnostics;

namespace PromptFlowStudio.Services.RetentionHeatmap;

/// <summary>SPS 적합성 결함 분류(첫 결함만 보고).</summary>
public enum RbspConformanceFault : byte
{
    None = 0,
    NotSps,             // 시작 코드/헤더 이상 · nal_unit_type ≠ 7
    ForbiddenBit,       // forbidden_zero_bit = 1
    StartCodeEmulation, // 바이트 정렬 위치 00 00 00 / 00 00 01 / 00 00 02 (에뮬레이션 방지 누락)
    BadEscape,          // 00 00 03 뒤 바이트 > 03
    TrailingZero,       // NAL 마지막 바이트 0x00
    Syntax,             // 필드 범위 초과 · 비트 고갈
    TrailingBits,       // 구문 끝 ≠ rbsp_stop_one_bit 위치
    CropMisaligned,     // 크롭 픽셀 오프셋이 CropUnit 배수 아님 / 표시 창 크로마 정렬 위반
    CropOverflow,       // 크롭 ≥ 디코딩 프레임
    BufferTooSmall,
}

/// <summary>SPS 디코더급 파싱 결과(무할당 값 구조체).</summary>
public struct SpsConformanceReport
{
    public RbspConformanceFault Fault;
    public int Profile, Level, SpsId, ChromaFormatIdc, BitDepthLuma, BitDepthChroma, PocType, MaxRefFrames;
    public int WidthPx, HeightPx, CropUnitX, CropUnitY, CropLeft, CropRight, CropTop, CropBottom, VisibleW, VisibleH;
    public int EbspBytes, RbspBytes, EmulationPreventionBytes, RbspBits;
    public bool FrameMbsOnly, Vui, Timing, Hrd, BitstreamRestriction;
    public uint NumUnitsInTick, TimeScale;
    public readonly bool Conformant => Fault == RbspConformanceFault.None;
}

/// <summary>
/// [PromptFlow Studio v17.98.2] 🛡 H.264 SPS RBSP 적합성 안전 엔진 — 실 플레이어(VLC · WMF · AVPlayer · ExoPlayer) 파싱 크래시 원천 방어.
///   ▸ EBSP 바이트 계층: 바이트 정렬 00 00 00/01/02 금지 · 00 00 03 뒤 ≤ 03 · 마지막 바이트 ≠ 00 · forbidden_zero_bit = 0(ISO/IEC 14496-10 7.4.1).
///   ▸ RBSP 구문 계층: profile → 스케일링 리스트 → POC → 해상도 → frame_cropping → VUI(HRD · bitstream_restriction 포함) 전 필드 파싱 →
///     구문 종료 비트 = rbsp_stop_one_bit 위치 정확 일치(비트 시프팅 손상 검출).
///   ▸ 크롭 계층: CropUnitX/Y(4:2:0 = 2 × 2 · 필드 = ×2) 배수 · 표시 창 &gt; 0 · 4:2:0 표시 창 짝수 정렬.
///   ▸ <see cref="Harden"/> = EBSP → RBSP → 에뮬레이션 방지 바이트 재주입(누락분 삽입 · 기존분 보존) · <see cref="AlignCrop"/> = 픽셀 크롭 → CropUnit 정렬.
///   ▸ 사전 할당 스크래치만 사용 — Validate/Harden 무할당.
/// </summary>
public sealed class BitstreamRbspConformanceSafetyEngine
{
    private readonly byte[] _rbsp = new byte[H264SpsCropRewriter.MaxNal];
    private SpsConformanceReport _last;

    public ref readonly SpsConformanceReport Last => ref _last;
    public long Validations { get; private set; }
    public long Rejects { get; private set; }
    public long Hardened { get; private set; }
    public double LastUs { get; private set; }
    public double MaxUs { get; private set; }

    /// <summary>SPS NAL(Annex-B 시작 코드 포함 또는 NAL 헤더부터) 검증. 반환 = 첫 결함(None = 적합).</summary>
    public RbspConformanceFault Validate(ReadOnlySpan<byte> spsNal)
    {
        var t0 = Stopwatch.GetTimestamp();
        _last = default;
        var f = ValidateCore(spsNal, ref _last);
        _last.Fault = f;
        Validations++;
        if (f != RbspConformanceFault.None) Rejects++;
        LastUs = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
        if (LastUs > MaxUs) MaxUs = LastUs;
        return f;
    }

    /// <summary>시작 코드 길이(00 00 01 = 3 · 00 00 00 01 = 4 · 없음 = 0).</summary>
    public static int StartCodeLength(ReadOnlySpan<byte> nal)
    {
        if (nal.Length > 3 && nal[0] == 0 && nal[1] == 0 && nal[2] == 1) return 3;
        if (nal.Length > 4 && nal[0] == 0 && nal[1] == 0 && nal[2] == 0 && nal[3] == 1) return 4;
        return 0;
    }

    /// <summary>
    /// EBSP 바이트 계층 검사(헤더 이후 페이로드). 반환 = 결함 · <paramref name="prevention"/> = 에뮬레이션 방지 바이트 수.
    /// </summary>
    public static RbspConformanceFault CheckEmulation(ReadOnlySpan<byte> ebsp, out int prevention)
    {
        prevention = 0;
        if (ebsp.Length > 0 && ebsp[^1] == 0) return RbspConformanceFault.TrailingZero;
        var zeros = 0;
        for (var i = 0; i < ebsp.Length; i++)
        {
            var b = ebsp[i];
            if (zeros >= 2)
            {
                if (b < 3) return RbspConformanceFault.StartCodeEmulation;
                if (b == 3)
                {
                    if (i + 1 < ebsp.Length && ebsp[i + 1] > 3) return RbspConformanceFault.BadEscape;
                    prevention++; zeros = 0; continue;
                }
            }
            zeros = b == 0 ? zeros + 1 : 0;
        }
        return RbspConformanceFault.None;
    }

    /// <summary>
    /// 에뮬레이션 방지 바이트 재주입 — 입력 NAL(시작 코드 · 헤더 원문 유지) EBSP 를 RBSP 로 복원 후 재이스케이프 → <paramref name="dst"/>.
    /// 누락된 03 은 삽입 · 정상 03 은 보존(동일 위치 재생성). 반환 = 바이트 수(−1 = 버퍼 부족/빈 NAL).
    /// </summary>
    public int Harden(ReadOnlySpan<byte> nal, Span<byte> dst)
    {
        var hdr = StartCodeLength(nal) + 1;
        if (nal.Length <= hdr || dst.Length < hdr) return -1;
        var rl = H264SpsCropRewriter.Unescape(nal[hdr..], _rbsp);
        if (rl < 0) return -1;
        nal[..hdr].CopyTo(dst);
        var e = H264SpsCropRewriter.Escape(_rbsp.AsSpan(0, rl), dst[hdr..]);
        if (e < 0) return -1;
        Hardened++;
        return hdr + e;
    }

    /// <summary>RBSP → EBSP 에뮬레이션 방지 무할당 인젝터(<see cref="H264SpsCropRewriter.Escape"/> 위임). 반환 = EBSP 바이트(−1 = 버퍼 부족).</summary>
    public static int InjectEmulationPrevention(ReadOnlySpan<byte> rbsp, Span<byte> ebsp) => H264SpsCropRewriter.Escape(rbsp, ebsp);

    /// <summary>CropUnit(7.4.2.1.1) — 단색/분리 평면 1×1 · 4:2:0 2×2 · 4:2:2 2×1 · 4:4:4 1×1, 세로 × (2 − frame_mbs_only).</summary>
    public static void CropUnits(int chromaFormatIdc, bool separatePlanes, bool frameMbsOnly, out int ux, out int uy)
    {
        var arr = separatePlanes ? 0 : chromaFormatIdc;
        ux = arr is 1 or 2 ? 2 : 1;
        uy = (arr == 1 ? 2 : 1) * (frameMbsOnly ? 1 : 2);
    }

    /// <summary>
    /// 픽셀 크롭 → CropUnit 배수 정렬(내림 · 표시 창이 원 요청보다 작아지지 않음). 입력은 픽셀, 출력은 정렬된 픽셀.
    /// 반환 = 보정이 일어났는지.
    /// </summary>
    public static bool AlignCrop(int chromaFormatIdc, bool frameMbsOnly, ref int leftPx, ref int rightPx, ref int topPx, ref int bottomPx)
    {
        CropUnits(chromaFormatIdc, false, frameMbsOnly, out var ux, out var uy);
        int l = leftPx / ux * ux, r = rightPx / ux * ux, t = topPx / uy * uy, b = bottomPx / uy * uy;
        var changed = l != leftPx || r != rightPx || t != topPx || b != bottomPx;
        leftPx = Math.Max(0, l); rightPx = Math.Max(0, r); topPx = Math.Max(0, t); bottomPx = Math.Max(0, b);
        return changed;
    }

    private RbspConformanceFault ValidateCore(ReadOnlySpan<byte> nal, ref SpsConformanceReport rep)
    {
        var sc = StartCodeLength(nal);
        if (nal.Length < sc + 4) return RbspConformanceFault.NotSps;
        var h = nal[sc];
        if ((h & 0x80) != 0) return RbspConformanceFault.ForbiddenBit;
        if ((h & 0x1F) != 7) return RbspConformanceFault.NotSps;
        var ebsp = nal[(sc + 1)..];
        rep.EbspBytes = ebsp.Length;
        var ef = CheckEmulation(ebsp, out rep.EmulationPreventionBytes);
        if (ef != RbspConformanceFault.None) return ef;
        if (ebsp.Length > _rbsp.Length) return RbspConformanceFault.BufferTooSmall;
        var rl = H264SpsCropRewriter.Unescape(ebsp, _rbsp);
        if (rl < 0) return RbspConformanceFault.BufferTooSmall;
        rep.RbspBytes = rl;
        var rbsp = _rbsp.AsSpan(0, rl);
        var lastNz = rl - 1;
        while (lastNz >= 0 && rbsp[lastNz] == 0) lastNz--;
        if (lastNz < 0) return RbspConformanceFault.TrailingBits;
        var stopBit = lastNz * 8 + 7 - System.Numerics.BitOperations.TrailingZeroCount(rbsp[lastNz]);
        rep.RbspBits = stopBit;

        var r = new RbspBitReader(rbsp);
        rep.Profile = (int)r.U(8);
        r.U(8);   // constraint_set0..5 + reserved_zero_2bits
        rep.Level = (int)r.U(8);
        rep.SpsId = (int)r.Ue();
        if (rep.SpsId > 31) return RbspConformanceFault.Syntax;
        rep.ChromaFormatIdc = 1; rep.BitDepthLuma = rep.BitDepthChroma = 8;
        var separate = false;
        if (rep.Profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
        {
            rep.ChromaFormatIdc = (int)r.Ue();
            if (rep.ChromaFormatIdc > 3) return RbspConformanceFault.Syntax;
            if (rep.ChromaFormatIdc == 3) separate = r.U1() != 0;
            var bl = r.Ue(); var bc = r.Ue();
            if (bl > 6 || bc > 6) return RbspConformanceFault.Syntax;
            rep.BitDepthLuma = 8 + (int)bl; rep.BitDepthChroma = 8 + (int)bc;
            r.U1();   // qpprime_y_zero_transform_bypass_flag
            if (r.U1() != 0)
            {
                var lists = rep.ChromaFormatIdc != 3 ? 8 : 12;
                for (var i = 0; i < lists; i++)
                    if (r.U1() != 0 && !SkipScalingList(ref r, i < 6 ? 16 : 64)) return RbspConformanceFault.Syntax;
            }
        }
        if (r.Ue() > 12) return RbspConformanceFault.Syntax;   // log2_max_frame_num_minus4
        rep.PocType = (int)r.Ue();
        if (rep.PocType == 0) { if (r.Ue() > 12) return RbspConformanceFault.Syntax; }
        else if (rep.PocType == 1)
        {
            r.U1(); r.Se(); r.Se();
            var cycle = r.Ue();
            if (cycle > 255) return RbspConformanceFault.Syntax;
            for (var i = 0u; i < cycle; i++) r.Se();
        }
        else if (rep.PocType != 2) return RbspConformanceFault.Syntax;
        rep.MaxRefFrames = (int)r.Ue();
        if (rep.MaxRefFrames > 16) return RbspConformanceFault.Syntax;
        r.U1();   // gaps_in_frame_num_value_allowed_flag
        var wMbs = r.Ue() + 1;
        var hMap = r.Ue() + 1;
        if (wMbs > 1024 || hMap > 1024) return RbspConformanceFault.Syntax;
        rep.FrameMbsOnly = r.U1() != 0;
        if (!rep.FrameMbsOnly) r.U1();
        r.U1();   // direct_8x8_inference_flag
        uint cl = 0, cr = 0, ct = 0, cb = 0;
        if (r.U1() != 0) { cl = r.Ue(); cr = r.Ue(); ct = r.Ue(); cb = r.Ue(); }
        rep.Vui = r.U1() != 0;
        if (rep.Vui && !ParseVui(ref r, ref rep)) return RbspConformanceFault.Syntax;
        if (r.Overrun) return RbspConformanceFault.Syntax;
        if (r.Pos != stopBit) return RbspConformanceFault.TrailingBits;

        CropUnits(rep.ChromaFormatIdc, separate, rep.FrameMbsOnly, out var ux, out var uy);
        rep.CropUnitX = ux; rep.CropUnitY = uy;
        rep.WidthPx = (int)wMbs * 16;
        rep.HeightPx = (int)hMap * 16 * (rep.FrameMbsOnly ? 1 : 2);
        rep.CropLeft = (int)cl * ux; rep.CropRight = (int)cr * ux; rep.CropTop = (int)ct * uy; rep.CropBottom = (int)cb * uy;
        if ((long)ux * (cl + cr) >= rep.WidthPx || (long)uy * (ct + cb) >= rep.HeightPx) return RbspConformanceFault.CropOverflow;
        rep.VisibleW = rep.WidthPx - rep.CropLeft - rep.CropRight;
        rep.VisibleH = rep.HeightPx - rep.CropTop - rep.CropBottom;
        // 4:2:0 — 크롭 오프셋 · 표시 창 모두 크로마 2 × 2 격자 정렬(크로마 평면 반 픽셀 위상 방지)
        if (rep.CropLeft % ux != 0 || rep.CropRight % ux != 0 || rep.CropTop % uy != 0 || rep.CropBottom % uy != 0) return RbspConformanceFault.CropMisaligned;
        if (rep.ChromaFormatIdc == 1 && !separate && ((rep.VisibleW & 1) != 0 || (rep.VisibleH & 1) != 0)) return RbspConformanceFault.CropMisaligned;
        return RbspConformanceFault.None;
    }

    private static bool ParseVui(ref RbspBitReader r, ref SpsConformanceReport rep)
    {
        if (r.U1() != 0 && r.U(8) == 255) { r.U(16); r.U(16); }   // aspect_ratio_idc · Extended_SAR
        if (r.U1() != 0) r.U1();                                  // overscan
        if (r.U1() != 0) { r.U(3); r.U1(); if (r.U1() != 0) { r.U(8); r.U(8); r.U(8); } }   // video_signal_type · colour_description
        if (r.U1() != 0 && (r.Ue() > 5 || r.Ue() > 5)) return false;   // chroma_loc_info
        rep.Timing = r.U1() != 0;
        if (rep.Timing)
        {
            rep.NumUnitsInTick = r.U(32); rep.TimeScale = r.U(32); r.U1();
            if (rep.NumUnitsInTick == 0 || rep.TimeScale == 0) return false;
        }
        var nal = r.U1() != 0;
        if (nal && !SkipHrd(ref r)) return false;
        var vcl = r.U1() != 0;
        if (vcl && !SkipHrd(ref r)) return false;
        rep.Hrd = nal || vcl;
        if (rep.Hrd) r.U1();   // low_delay_hrd_flag
        r.U1();                // pic_struct_present_flag
        rep.BitstreamRestriction = r.U1() != 0;
        if (rep.BitstreamRestriction)
        {
            r.U1();
            if (r.Ue() > 16) return false;   // max_bytes_per_pic_denom
            if (r.Ue() > 16) return false;   // max_bits_per_mb_denom
            if (r.Ue() > 16) return false;   // log2_max_mv_length_horizontal
            if (r.Ue() > 16) return false;   // log2_max_mv_length_vertical
            var reorder = r.Ue(); var dpb = r.Ue();
            if (reorder > dpb || dpb > 16) return false;
        }
        return !r.Overrun;
    }

    private static bool SkipHrd(ref RbspBitReader r)
    {
        var cpb = r.Ue();
        if (cpb > 31) return false;
        r.U(4); r.U(4);
        for (var i = 0u; i <= cpb; i++) { r.Ue(); r.Ue(); r.U1(); }
        r.U(5); r.U(5); r.U(5); r.U(5);
        return !r.Overrun;
    }

    private static bool SkipScalingList(ref RbspBitReader r, int size)
    {
        int last = 8, next = 8;
        for (var j = 0; j < size; j++)
        {
            if (next != 0)
            {
                var d = r.Se();
                if (d < -128 || d > 127) return false;
                next = (last + d + 256) % 256;
            }
            last = next == 0 ? last : next;
        }
        return !r.Overrun;
    }
}
