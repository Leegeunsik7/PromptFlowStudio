namespace PromptFlowStudio.Services.RetentionHeatmap;

/// <summary>
/// [PromptFlow Studio v17.99.1] 🧪 구조 합성 Annex-B 비트스트림(H.264 / H.265) — 리먹서 회귀·소크·스튜디오 데모용.
///   ▸ 닫힌 GOP · B 프레임 재정렬(디코딩 순서 ≠ 표시 순서) · 다중 슬라이스 AU · AUD · 접두/접미 SEI · 파라미터 셋(첫 GOP 만 또는 매 GOP).
///   ▸ 페이로드는 결정적 의사 난수 + 에뮬레이션 방지 바이트(00 00 → 00 00 03) · RBSP 정지 비트로 끝남.
///   ▸ [v17.98.1] H.264 SPS = 규격 비트스트림(High · 1920×1080 크롭 · 스케일링 리스트 · VUI 타이밍) — SPS 크롭 줌 펀치 재작성 대상.
///   ▸ NAL 헤더/경계 구조(+ H.264 SPS)만 규격 준수 — 실제 디코딩 가능한 영상이 아님(UI 에서 "합성" 표기).
/// </summary>
public sealed class SyntheticAnnexBStream
{
    public SyntheticAnnexBStream(NalCodec codec, int frames, int gop, int bFrames, int fps, bool paramsEveryGop, uint seed)
    {
        Codec = codec;
        Fps = fps;
        FrameTicks = NaluSmartReMuxer.Clock / fps;
        frames = Math.Max(1, frames); gop = Math.Max(1, gop); bFrames = Math.Clamp(bFrames, 0, 3);
        var order = new int[frames];
        var d = 0;
        for (var s = 0; s < frames; s += gop)
        {
            var end = Math.Min(frames, s + gop);
            order[d++] = s;
            var i = s + 1;
            while (i < end)
            {
                var anchor = Math.Min(i + bFrames, end - 1);
                order[d++] = anchor;
                for (var j = i; j < anchor; j++) order[d++] = j;
                i = anchor + 1;
            }
        }
        var sps = codec == NalCodec.H264 ? BuildH264Sps(fps) : null;
        var buf = new List<byte>(frames * 400);
        Pts = new long[frames]; Dts = new long[frames];
        var rng = seed | 1u;
        for (var k = 0; k < frames; k++)
        {
            var f = order[k];
            var key = f % gop == 0;
            Pts[k] = (long)(f + bFrames) * FrameTicks;
            Dts[k] = (long)k * FrameTicks;
            Nal(buf, Header(codec, codec == NalCodec.H264 ? 9 : 35), 1, ref rng, aud: true);
            if (key && (paramsEveryGop || f == 0))
            {
                if (codec == NalCodec.H265) Nal(buf, Header(codec, 32), 24, ref rng);
                Nal(buf, Header(codec, codec == NalCodec.H264 ? 7 : 33), 20, ref rng, raw: sps);
                Nal(buf, Header(codec, codec == NalCodec.H264 ? 8 : 34), 6, ref rng);
            }
            if (key) Nal(buf, Header(codec, codec == NalCodec.H264 ? 6 : 39), 12, ref rng);
            var sliceType = key ? (codec == NalCodec.H264 ? 5 : 19) : 1;
            var slices = 1 + (int)(Next(ref rng) % 2);
            for (var sl = 0; sl < slices; sl++) Nal(buf, Header(codec, sliceType), (key ? 220 : 80) + (int)(Next(ref rng) % 120), ref rng, firstSlice: sl == 0, slice: true);
            if (codec == NalCodec.H265 && Next(ref rng) % 4 == 0) Nal(buf, Header(codec, 40), 8, ref rng);
        }
        Bytes = buf.ToArray();
        Frames = frames;
    }

    public NalCodec Codec { get; }
    public int Fps { get; }
    public long FrameTicks { get; }
    public int Frames { get; }
    public byte[] Bytes { get; }
    /// <summary>디코딩 순서 AU 별 90 kHz 타임스탬프(DTS ≤ PTS).</summary>
    public long[] Pts { get; }
    public long[] Dts { get; }

    private static ushort Header(NalCodec c, int type) => c == NalCodec.H264 ? (ushort)(0x60 | type) : (ushort)((type << 9) | 1);   // H.265: layer 0 · tid+1 = 1

    private static uint Next(ref uint r) { r ^= r << 13; r ^= r >> 17; r ^= r << 5; return r; }

    /// <summary>H.264 SPS EBSP(헤더 제외) — High@4.0 · 4:2:0 · 120×68 MB(1920×1088) 하단 크롭 8 → 1080 · 4×4 인트라 Y 스케일링 리스트 · POC 0 · VUI(SAR 1:1 · 타이밍 fps).</summary>
    public static byte[] BuildH264Sps(int fps)
    {
        Span<byte> r = stackalloc byte[64];
        var w = new RbspBitWriter(r);
        w.U(100, 8); w.U(0, 8); w.U(40, 8);   // profile_idc · constraint · level_idc
        w.Ue(0);                               // seq_parameter_set_id
        w.Ue(1); w.Ue(0); w.Ue(0); w.U1(0);    // chroma_format_idc 4:2:0 · bit_depth 8/8 · qpprime
        w.U1(1);                               // seq_scaling_matrix_present_flag
        for (var i = 0; i < 8; i++)
        {
            w.U1(i == 0 ? 1u : 0u);
            if (i == 0) { w.Se(8); for (var j = 1; j < 16; j++) w.Se(j % 3 == 0 ? 1 : 0); }
        }
        w.Ue(0); w.Ue(0); w.Ue(2);             // log2_max_frame_num−4 · poc_type 0 · log2_max_poc_lsb−4
        w.Ue(4); w.U1(0);                      // max_num_ref_frames · gaps
        w.Ue(119); w.Ue(67);                   // 1920 × 1088
        w.U1(1); w.U1(1);                      // frame_mbs_only · direct_8x8
        w.U1(1); w.Ue(0); w.Ue(0); w.Ue(0); w.Ue(4);   // 하단 4 × CropUnitY(2) = 8 → 1080
        w.U1(1);                               // vui_parameters_present_flag
        w.U1(1); w.U(1, 8);                    // aspect_ratio_idc 1:1
        w.U1(0); w.U1(0); w.U1(0);             // overscan · video_signal · chroma_loc
        w.U1(1); w.U(1, 32); w.U((uint)(fps * 2), 32); w.U1(1);   // timing_info(num_units_in_tick 1 · time_scale 2·fps · fixed)
        w.U1(0); w.U1(0); w.U1(0); w.U1(0);    // nal_hrd · vcl_hrd · pic_struct · bitstream_restriction
        var n = w.Trail();
        Span<byte> e = stackalloc byte[96];
        var en = H264SpsCropRewriter.Escape(r[..n], e);
        return e[..en].ToArray();
    }

    private static void Nal(List<byte> b, ushort header, int payload, ref uint rng, bool aud = false, bool firstSlice = false, bool slice = false, byte[]? raw = null)
    {
        // 4 바이트 / 3 바이트 시작 코드 혼용(첫 NAL 은 4 바이트)
        if (b.Count == 0 || Next(ref rng) % 2 == 0) b.Add(0);
        b.Add(0); b.Add(0); b.Add(1);
        if (header > 0xFF) { b.Add((byte)(header >> 8)); b.Add((byte)header); } else b.Add((byte)header);
        if (aud) { b.Add(0x10); return; }   // primary_pic_type/pic_type + 정지 비트
        if (raw != null) { b.AddRange(raw); return; }
        int zeros = 0;
        for (var i = 0; i < payload; i++)
        {
            byte v;
            if (slice && i == 0) v = firstSlice ? (byte)(0x80 | (Next(ref rng) & 0x7F)) : (byte)(0x40 | (Next(ref rng) & 0x3F));
            else v = (byte)Next(ref rng);
            if (i == payload - 1) v = 0x80;   // rbsp_stop_one_bit
            if (zeros >= 2 && v <= 3) { b.Add(3); zeros = 0; }
            b.Add(v);
            zeros = v == 0 ? zeros + 1 : 0;
        }
    }
}
