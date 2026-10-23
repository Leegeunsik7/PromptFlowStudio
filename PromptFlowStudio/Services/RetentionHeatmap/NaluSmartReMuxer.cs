using System.Diagnostics;

namespace PromptFlowStudio.Services.RetentionHeatmap;

public enum NalCodec : byte { H264 = 0, H265 = 1 }

/// <summary>Annex-B NAL 단위(시작 코드 포함 바이트 구간 — 연속 구간이 원본을 빈틈없이 분할).</summary>
public struct NalUnit
{
    public int Offset, Length, HeaderOffset;
    public byte Type;
    public bool Vcl, Key, ParamSet, FirstSlice;
}

/// <summary>접근 단위(AU = 1 픽처) — 디코딩 순서 · 90 kHz PTS/DTS.</summary>
public struct AccessUnit
{
    public int Offset, Length, FirstNal, NalCount;
    public bool Key, HasParams;
    public long Pts, Dts;
}

/// <summary>원본 시간(틱) → 출력 시간(틱) 단조 구간 선형 매핑의 절점.</summary>
public readonly record struct WarpKnot(long Src, long Out);

/// <summary>원본 구간(ms) — 리먹스 컷 리스트 항목.</summary>
public readonly record struct RemuxCut(int SrcStartMs, int SrcEndMs);

/// <summary>
/// [PromptFlow Studio v17.99.1] 🎞 NALU 스마트 무인코딩 리먹서(H.264 / H.265 Annex-B).
///   ▸ 파싱: 00 00 01 / 00 00 00 01 시작 코드 → NAL 헤더(H.264 1 B · H.265 2 B) → VCL/IRAP/파라미터 셋/AUD/SEI 분류 → AU 경계(first_mb_in_slice = 0 · first_slice_segment_in_pic_flag).
///   ▸ 컷: 시작은 직전 키 AU(GOP 시작)로 · 끝은 다음 키 AU 직전으로 스냅(닫힌 GOP 단위 스트림 복사 · 재인코딩 0) — 키 AU 에 SPS/PPS(/VPS) 없으면 직전 파라미터 셋 NAL 원문 주입.
///   ▸ 타임스탬프: 템포 맵(<see cref="WarpKnot"/>)으로 PTS/DTS 동일 재매핑 → 세그먼트 연결 오프셋 → DTS 엄격 증가 · PTS ≥ DTS 보정(보정 횟수 계측).
///   ▸ NAL 바이트는 원문 그대로 복사(무손실) — <see cref="VerifyLossless"/>가 출력 재파싱 후 NAL 단위 바이트 일치를 검증.
///   ▸ [v17.98.1] 줌 펀치(H.264): 펀치 GOP 의 SPS 를 <see cref="H264SpsCropRewriter"/>로 frame_cropping 비트 재작성(재인코딩 0) → 다음 GOP 에서 원 SPS 복원 — <see cref="ZeroTranscodePunchGops"/>.
///     H.265 · SPS 파싱 실패 · 크롭 비활성은 픽셀 변경을 스트림 복사로 표현 불가 → <see cref="ReencodeGops"/>로 정직 보고(인코더 없음 · 위장 금지).
///   ▸ 사전 할당 버퍼만 사용 — Parse/Remux/Verify 무할당.
/// </summary>
public sealed class NaluSmartReMuxer
{
    public const int Clock = 90_000;

    private readonly NalUnit[] _nal, _vNal;
    private readonly AccessUnit[] _au, _out;
    private readonly int[] _outSrcAu;
    private readonly int[] _emitted = new int[3];   // 출력에 이미 실린 파라미터 셋(슬롯 VPS/SPS/PPS → 원본 NAL 인덱스)
    private readonly H264SpsCropRewriter _crop = new();
    private readonly byte[] _spsBuf = new byte[H264SpsCropRewriter.MaxNal + 64];
    private int _nalN, _auN, _outN, _vNalN, _spsLen;
    private float _cropZoom;
    private bool _cropActive;

    public NaluSmartReMuxer(int maxNals = 16_384, int maxAccessUnits = 4_096)
    {
        _nal = new NalUnit[maxNals]; _vNal = new NalUnit[maxNals];
        _au = new AccessUnit[maxAccessUnits]; _out = new AccessUnit[maxAccessUnits]; _outSrcAu = new int[maxAccessUnits];
    }

    public NalCodec Codec { get; private set; }
    public int NalCount => _nalN;
    public int AccessUnitCount => _auN;
    public int KeyCount { get; private set; }
    public long Truncated { get; private set; }
    public int LeadingBytes { get; private set; }
    public ref readonly NalUnit Nal(int i) => ref _nal[i];
    public ref readonly AccessUnit Au(int i) => ref _au[i];
    public int OutCount => _outN;
    public ref readonly AccessUnit OutAu(int i) => ref _out[i];
    public int OutSourceAu(int i) => _outSrcAu[i];
    public int OutBytes { get; private set; }
    public int CopiedGops { get; private set; }
    public int InjectedParamNals { get; private set; }
    public int ReencodeGops { get; private set; }
    /// <summary>SPS 크롭 재작성으로 처리된 펀치 GOP 수(재인코딩 0).</summary>
    public int ZeroTranscodePunchGops { get; private set; }
    /// <summary>출력에 실린 크롭 재작성 SPS NAL 수.</summary>
    public int CroppedSpsNals { get; private set; }
    /// <summary>SPS 크롭 재작성 1 회 최대 소요(µs).</summary>
    public double MaxCropUs { get; private set; }
    public H264SpsCropRewriter Cropper => _crop;
    public int TimestampRepairs { get; private set; }
    public long PaddingTicks { get; private set; }
    public long OutEndTicks { get; private set; }
    public double LastParseUs { get; private set; }
    public double LastRemuxUs { get; private set; }

    // ══════════════════════ NAL 분류 ══════════════════════

    public static byte TypeOf(NalCodec c, byte h0) => c == NalCodec.H264 ? (byte)(h0 & 0x1F) : (byte)((h0 >> 1) & 0x3F);
    public static bool IsVcl(NalCodec c, byte t) => c == NalCodec.H264 ? t is >= 1 and <= 5 : t <= 31;
    public static bool IsKey(NalCodec c, byte t) => c == NalCodec.H264 ? t == 5 : t is >= 16 and <= 23;
    public static bool IsParam(NalCodec c, byte t) => c == NalCodec.H264 ? t is 7 or 8 : t is 32 or 33 or 34;
    /// <summary>VCL 이후 등장 시 새 AU 를 여는 비 VCL(AUD · 파라미터 셋 · 접두 SEI · 예약 접두).</summary>
    public static bool OpensAu(NalCodec c, byte t) => c == NalCodec.H264 ? t is 6 or 7 or 8 or 9 or (>= 14 and <= 18) : t is 32 or 33 or 34 or 35 or 39 or (>= 41 and <= 44);
    public static int HeaderBytes(NalCodec c) => c == NalCodec.H264 ? 1 : 2;

    // ══════════════════════ 파싱 ══════════════════════

    /// <summary>Annex-B 파싱 → NAL · AU 목록. 반환 = AU 수. 첫 시작 코드 이전 바이트(비 Annex-B 잔여)는 제외하고 <see cref="LeadingBytes"/>로 보고.</summary>
    public int Parse(ReadOnlySpan<byte> s, NalCodec codec)
    {
        var t0 = Stopwatch.GetTimestamp();
        Codec = codec;
        _nalN = ScanNals(s, codec, _nal, out var trunc);
        LeadingBytes = _nalN > 0 ? _nal[0].Offset : s.Length;
        Truncated += trunc;
        _auN = 0; KeyCount = 0;
        var curHasVcl = false;
        for (var i = 0; i < _nalN; i++)
        {
            ref readonly var u = ref _nal[i];
            var open = _auN == 0 || (curHasVcl && (OpensAu(codec, u.Type) || (u.Vcl && u.FirstSlice)));
            if (open)
            {
                if (_auN == _au.Length) { Truncated++; _nalN = i; break; }
                ref var a = ref _au[_auN++];
                a = default;
                a.Offset = u.Offset;
                a.FirstNal = i;
                curHasVcl = false;
            }
            ref var cur = ref _au[_auN - 1];
            cur.NalCount++;
            cur.Length = u.Offset + u.Length - cur.Offset;
            if (u.Vcl) curHasVcl = true;
            if (u.Key && !cur.Key) { cur.Key = true; KeyCount++; }
            if (u.ParamSet) cur.HasParams = true;
        }
        LastParseUs = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
        return _auN;
    }

    private static int ScanNals(ReadOnlySpan<byte> s, NalCodec codec, NalUnit[] dst, out int truncated)
    {
        truncated = 0;
        var n = 0;
        var hb = HeaderBytes(codec);
        var i = 0;
        while (true)
        {
            var rel = s[i..].IndexOf((ReadOnlySpan<byte>)[0, 0, 1]);
            if (rel < 0) break;
            var p = i + rel;
            var start = p > 0 && s[p - 1] == 0 ? p - 1 : p;
            if (n > 0) dst[n - 1].Length = start - dst[n - 1].Offset;
            if (n == dst.Length) { truncated = 1; break; }
            ref var u = ref dst[n++];
            u = default;
            u.Offset = start;
            u.HeaderOffset = p + 3;
            if (u.HeaderOffset + hb <= s.Length)
            {
                u.Type = TypeOf(codec, s[u.HeaderOffset]);
                u.Vcl = IsVcl(codec, u.Type);
                u.Key = u.Vcl && IsKey(codec, u.Type);
                u.ParamSet = IsParam(codec, u.Type);
                var fb = u.HeaderOffset + hb;
                u.FirstSlice = u.Vcl && fb < s.Length && (s[fb] & 0x80) != 0;
            }
            i = p + 3;
        }
        if (truncated == 0 && n > 0) dst[n - 1].Length = s.Length - dst[n - 1].Offset;
        return n;
    }

    /// <summary>AU 타임스탬프 지정(컨테이너 제공 · 디코딩 순서 · 90 kHz).</summary>
    public void SetTimestamps(ReadOnlySpan<long> pts, ReadOnlySpan<long> dts)
    {
        if (pts.Length < _auN || dts.Length < _auN) throw new ArgumentException("timestamp count < access units");
        for (var i = 0; i < _auN; i++) { _au[i].Pts = pts[i]; _au[i].Dts = dts[i]; }
    }

    // ══════════════════════ 템포 맵 ══════════════════════

    /// <summary>단조 구간 선형 매핑 — 절점 범위 밖은 기울기 1 외삽.</summary>
    public static long Warp(ReadOnlySpan<WarpKnot> k, long src)
    {
        if (k.Length == 0) return src;
        if (src <= k[0].Src) return k[0].Out + (src - k[0].Src);
        if (src >= k[^1].Src) return k[^1].Out + (src - k[^1].Src);
        int lo = 0, hi = k.Length - 1;
        while (hi - lo > 1) { var m = (lo + hi) >> 1; if (k[m].Src <= src) lo = m; else hi = m; }
        var ds = k[hi].Src - k[lo].Src;
        return ds <= 0 ? k[hi].Out : k[lo].Out + (long)Math.Round((double)(src - k[lo].Src) * (k[hi].Out - k[lo].Out) / ds);
    }

    public static bool IsMonotonic(ReadOnlySpan<WarpKnot> k)
    {
        for (var i = 1; i < k.Length; i++) if (k[i].Src < k[i - 1].Src || k[i].Out < k[i - 1].Out) return false;
        return true;
    }

    // ══════════════════════ 리먹스 ══════════════════════

    /// <summary>
    /// 컷 리스트를 GOP 스냅 스트림 복사로 연결 + 템포 맵 재타이밍. 반환 = 출력 바이트 수(−1 = 출력 버퍼 부족).
    /// <paramref name="frameTicks"/> = 원본 1 프레임 길이(세그먼트 연결 간격). <paramref name="punchSrcMs"/> = 줌 펀치 시각.
    /// <paramref name="cropZoom"/> &gt; 1 = H.264 펀치 GOP SPS 크롭 재작성(재인코딩 0) · ≤ 1 = 펀치 GOP 를 재인코딩 필요로 집계만.
    /// </summary>
    public int Remux(ReadOnlySpan<byte> src, ReadOnlySpan<RemuxCut> cuts, ReadOnlySpan<WarpKnot> warp, long frameTicks, Span<byte> dst, ReadOnlySpan<int> punchSrcMs = default, float cropZoom = 0f)
    {
        if (!IsMonotonic(warp)) throw new ArgumentException("warp knots must be monotonic", nameof(warp));
        var t0 = Stopwatch.GetTimestamp();
        _outN = 0; OutBytes = 0; CopiedGops = 0; InjectedParamNals = 0; ReencodeGops = 0; TimestampRepairs = 0; PaddingTicks = 0; OutEndTicks = 0;
        ZeroTranscodePunchGops = 0; CroppedSpsNals = 0; MaxCropUs = 0;
        _cropZoom = cropZoom; _cropActive = false;
        long offset = 0, prevDts = long.MinValue;
        var w = 0;
        _emitted[0] = _emitted[1] = _emitted[2] = -1;
        foreach (var cut in cuts)
        {
            long cs = (long)cut.SrcStartMs * Clock / 1000, ce = (long)cut.SrcEndMs * Clock / 1000;
            if (ce <= cs) continue;
            var a0 = GopStartFor(cs);
            var a1 = GopEndFor(ce);
            if (a0 < 0 || a1 <= a0) continue;
            long minPts = long.MaxValue, maxPts = long.MinValue, minDts = long.MaxValue;
            for (var a = a0; a < a1; a++) { minPts = Math.Min(minPts, _au[a].Pts); maxPts = Math.Max(maxPts, _au[a].Pts); minDts = Math.Min(minDts, _au[a].Dts); }
            PaddingTicks += Math.Max(0, cs - minPts) + Math.Max(0, maxPts + frameTicks - ce);
            var baseOut = Warp(warp, minPts);
            // 첫 세그먼트: 재정렬 지연(PTS − DTS)만큼 올려 첫 DTS = 0(음수 DTS 금지) · 이후 세그먼트: 직전 최대 PTS + 1 프레임에서 연속
            if (_outN == 0) offset = Math.Max(0, baseOut - Warp(warp, minDts));
            long segMax = long.MinValue;
            for (var a = a0; a < a1; a++)
            {
                ref readonly var au = ref _au[a];
                var crop = false;
                var spsIn = -1;
                if (au.Key)
                {
                    CopiedGops++;
                    spsIn = au.HasParams ? SpsIn(au) : -1;
                    if (CountsPunch(au, a, punchSrcMs))
                    {
                        // 줌 펀치 GOP — H.264 면 SPS 크롭 재작성(재인코딩 0) · 불가하면 재인코딩 필요로 정직 보고
                        var spsSrc = spsIn >= 0 ? spsIn : LatestSps(au.FirstNal);
                        crop = cropZoom > 1f && Codec == NalCodec.H264 && spsSrc >= 0 && CropSps(src, spsSrc) > 0;
                        if (crop) ZeroTranscodePunchGops++; else ReencodeGops++;
                        if (crop && spsIn < 0)
                        {
                            // AU 에 SPS 없음 → 크롭 SPS 를 AU 앞에 주입(IDR 에서 활성)
                            if (w + _spsLen > dst.Length) return -1;
                            _spsBuf.AsSpan(0, _spsLen).CopyTo(dst[w..]);
                            w += _spsLen;
                            CroppedSpsNals++;
                        }
                    }
                    var restore = !crop && _cropActive && spsIn < 0;
                    if ((!au.HasParams && a == a0) || restore)
                    {
                        // 세그먼트 첫 GOP — 파라미터 셋 미포함이면 직전 SPS/PPS(/VPS) 원문 주입(출력에 동일 셋이 이미 있으면 생략)
                        // 크롭 GOP 직후 — 출력 SPS 슬롯이 크롭본이므로 원 SPS 복원 주입
                        var inj = InjectParams(src, au.FirstNal, dst[w..], skipSps: crop);
                        if (inj < 0) return -1;
                        w += inj;
                    }
                    _cropActive = crop;
                }
                if (_outN == _out.Length) return -1;
                var auStart = w;
                if (crop && spsIn >= 0)
                {
                    // AU 내 SPS 를 크롭 SPS 로 제자리 치환 · 나머지 NAL 원문
                    for (var k = au.FirstNal; k < au.FirstNal + au.NalCount; k++)
                    {
                        ref readonly var u = ref _nal[k];
                        var len = k == spsIn ? _spsLen : u.Length;
                        if (w + len > dst.Length) return -1;
                        if (k == spsIn) { _spsBuf.AsSpan(0, len).CopyTo(dst[w..]); CroppedSpsNals++; }
                        else src.Slice(u.Offset, len).CopyTo(dst[w..]);
                        w += len;
                    }
                }
                else
                {
                    if (w + au.Length > dst.Length) return -1;
                    src.Slice(au.Offset, au.Length).CopyTo(dst[w..]);
                    w += au.Length;
                }
                if (au.HasParams)
                    for (var k = au.FirstNal; k < au.FirstNal + au.NalCount; k++)
                        if (_nal[k].ParamSet) _emitted[Slot(_nal[k].Type)] = k;
                if (crop) _emitted[1] = -2;   // 출력 SPS 슬롯 = 크롭본(원본 NAL 과 불일치)
                ref var o = ref _out[_outN];
                o = au;
                o.Offset = auStart;
                o.Length = w - auStart;
                o.Pts = Warp(warp, au.Pts) - baseOut + offset;
                o.Dts = Warp(warp, au.Dts) - baseOut + offset;
                if (prevDts != long.MinValue && o.Dts <= prevDts) { o.Dts = prevDts + 1; TimestampRepairs++; }
                if (o.Pts < o.Dts) { o.Pts = o.Dts; TimestampRepairs++; }
                prevDts = o.Dts;
                _outSrcAu[_outN++] = a;
                if (o.Pts > segMax) segMax = o.Pts;
            }
            var lastOutFrame = Math.Max(1, Warp(warp, maxPts + frameTicks) - Warp(warp, maxPts));
            offset = segMax + lastOutFrame;
            OutEndTicks = offset;
        }
        OutBytes = w;
        LastRemuxUs = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
        return w;
    }

    private int GopStartFor(long cs)
    {
        // 표시 시각 ≤ cs 인 마지막 키 AU(디코딩 순서) — 없으면 첫 키 AU
        var best = -1;
        for (var a = 0; a < _auN; a++)
        {
            if (!_au[a].Key) continue;
            if (_au[a].Pts <= cs || best < 0) best = a;
            if (_au[a].Pts > cs) break;
        }
        return best;
    }

    private int GopEndFor(long ce)
    {
        // 표시 시각 ≥ ce 인 첫 키 AU 직전(배타) — 없으면 끝
        for (var a = 0; a < _auN; a++) if (_au[a].Key && _au[a].Pts >= ce) return a;
        return _auN;
    }

    private bool CountsPunch(in AccessUnit key, int a, ReadOnlySpan<int> punchSrcMs)
    {
        if (punchSrcMs.IsEmpty) return false;
        var end = long.MaxValue;
        for (var b = a + 1; b < _auN; b++) if (_au[b].Key) { end = _au[b].Pts; break; }
        foreach (var ms in punchSrcMs)
        {
            var t = (long)ms * Clock / 1000;
            if (t >= key.Pts && t < end) return true;
        }
        return false;
    }

    private int SpsIn(in AccessUnit au)
    {
        for (var k = au.FirstNal; k < au.FirstNal + au.NalCount; k++) if (_nal[k].ParamSet && IsSps(_nal[k].Type)) return k;
        return -1;
    }

    private int LatestSps(int beforeNal)
    {
        for (var i = beforeNal - 1; i >= 0; i--) if (_nal[i].ParamSet && IsSps(_nal[i].Type)) return i;
        return -1;
    }

    /// <summary>원본 SPS NAL → 크롭 재작성(스크래치 버퍼). 반환 = 바이트 수(−1 = 실패).</summary>
    private int CropSps(ReadOnlySpan<byte> src, int idx)
    {
        ref readonly var u = ref _nal[idx];
        _spsLen = _crop.Rewrite(src.Slice(u.Offset, u.Length), _cropZoom, _spsBuf);
        if (_crop.LastUs > MaxCropUs) MaxCropUs = _crop.LastUs;
        return _spsLen;
    }

    private int InjectParams(ReadOnlySpan<byte> src, int beforeNal, Span<byte> dst, bool skipSps = false)
    {
        // 타입별 최근 파라미터 셋(H.264: SPS 7 · PPS 8 / H.265: VPS 32 · SPS 33 · PPS 34)
        int i0 = -1, i1 = -1, i2 = -1;
        var h264 = Codec == NalCodec.H264;
        for (var i = beforeNal - 1; i >= 0 && ((!h264 && i0 < 0) || i1 < 0 || i2 < 0); i--)
        {
            ref readonly var u = ref _nal[i];
            if (!u.ParamSet) continue;
            var slot = Slot(u.Type);
            if (slot == 0 && i0 < 0) i0 = i; else if (slot == 1 && i1 < 0) i1 = i; else if (slot == 2 && i2 < 0) i2 = i;
        }
        var w = 0;
        Span<int> order = [i0, i1, i2];
        for (var slot = 0; slot < 3; slot++)
        {
            var idx = order[slot];
            if (idx < 0 || (skipSps && slot == 1)) continue;
            ref readonly var u = ref _nal[idx];
            var e = _emitted[slot];
            if (e == idx || (e >= 0 && src.Slice(_nal[e].Offset, _nal[e].Length).SequenceEqual(src.Slice(u.Offset, u.Length)))) continue;
            var len = u.Length;
            if (w + len > dst.Length) return -1;
            src.Slice(u.Offset, len).CopyTo(dst[w..]);
            w += len;
            _emitted[slot] = idx;
            InjectedParamNals++;
        }
        return w;
    }

    // ══════════════════════ 검증 ══════════════════════

    /// <summary>
    /// 무손실 검증 — 출력 재파싱 → 출력 AU 별 NAL 수·바이트가 원본 AU 와 일치하는지(주입 파라미터 셋은 원본 NAL 과 바이트 일치) 확인.
    /// 반환 = 불일치 수(0 = 무손실). 각 세그먼트 첫 VCL 이 키이며 파라미터 셋이 선행하는지도 함께 검증.
    /// </summary>
    public int VerifyLossless(ReadOnlySpan<byte> src, ReadOnlySpan<byte> remuxed)
    {
        var bad = 0;
        var vn = ScanNals(remuxed, Codec, _vNal, out var tr);
        _vNalN = vn;
        if (tr != 0) bad++;
        int outAu = 0, k = 0;
        bool haveSps = false, havePps = false;
        for (var i = 0; i < vn; i++)
        {
            ref readonly var v = ref _vNal[i];
            var bytes = remuxed.Slice(v.Offset, v.Length);
            if (v.ParamSet) { if (IsSps(v.Type)) haveSps = true; else if (IsPps(v.Type)) havePps = true; }
            if (outAu < _outN && v.Offset >= _out[outAu].Offset && v.Offset < _out[outAu].Offset + _out[outAu].Length)
            {
                // AU 소속 NAL — 원본 AU 의 동일 순번 NAL 과 바이트 비교
                ref readonly var sa = ref _au[_outSrcAu[outAu]];
                ref readonly var s = ref _nal[sa.FirstNal + k];
                if (!bytes.SequenceEqual(src.Slice(s.Offset, s.Length)) && !(s.ParamSet && IsSps(s.Type) && IsCropOf(src, sa.FirstNal + k, bytes))) bad++;
                // 세그먼트 첫 AU 의 첫 VCL = 키 · SPS/PPS 선행 필수(독립 디코딩 가능)
                if (v.Vcl && k == FirstVclIndex(sa) && IsSegmentStart(outAu) && (!v.Key || !haveSps || !havePps)) bad++;
                if (++k >= sa.NalCount) { k = 0; outAu++; }
            }
            else if (!v.ParamSet || !(MatchesAnySourceParam(src, bytes) || MatchesCroppedSourceSps(src, bytes))) bad++;   // 주입 NAL = 원본 파라미터 셋 원문 또는 그 크롭 재작성본
        }
        if (outAu != _outN || k != 0) bad++;
        return bad;
    }

    /// <summary>재파싱된 출력 NAL 수(<see cref="VerifyLossless"/> 이후).</summary>
    public int VerifiedNalCount => _vNalN;

    /// <summary>파라미터 셋 슬롯 — 0 = VPS(H.265) · 1 = SPS · 2 = PPS.</summary>
    private int Slot(byte t) => Codec == NalCodec.H264 ? t - 6 : t - 32;

    private bool IsSps(byte t) => Codec == NalCodec.H264 ? t == 7 : t == 33;
    private bool IsPps(byte t) => Codec == NalCodec.H264 ? t == 8 : t == 34;

    private int FirstVclIndex(in AccessUnit a)
    {
        for (var k = 0; k < a.NalCount; k++) if (_nal[a.FirstNal + k].Vcl) return k;
        return -1;
    }

    /// <summary>세그먼트 시작 = 출력 첫 AU 또는 원본 인덱스가 불연속이 되는 지점.</summary>
    private bool IsSegmentStart(int outAu) => outAu == 0 || _outSrcAu[outAu - 1] != _outSrcAu[outAu] - 1;

    /// <summary>출력 NAL 이 원본 SPS(idx) 크롭 재작성본과 바이트 일치하는지(크롭 비활성이면 거짓).</summary>
    private bool IsCropOf(ReadOnlySpan<byte> src, int idx, ReadOnlySpan<byte> bytes)
    {
        if (_cropZoom <= 1f || Codec != NalCodec.H264) return false;
        ref readonly var u = ref _nal[idx];
        var n = _crop.Rewrite(src.Slice(u.Offset, u.Length), _cropZoom, _spsBuf);
        return n == bytes.Length && _spsBuf.AsSpan(0, n).SequenceEqual(bytes);
    }

    private bool MatchesCroppedSourceSps(ReadOnlySpan<byte> src, ReadOnlySpan<byte> bytes)
    {
        if (_cropZoom <= 1f || Codec != NalCodec.H264) return false;
        for (var i = 0; i < _nalN; i++)
            if (_nal[i].ParamSet && IsSps(_nal[i].Type) && IsCropOf(src, i, bytes)) return true;
        return false;
    }

    private bool MatchesAnySourceParam(ReadOnlySpan<byte> src, ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i < _nalN; i++)
        {
            ref readonly var u = ref _nal[i];
            if (u.ParamSet && u.Length == bytes.Length && src.Slice(u.Offset, u.Length).SequenceEqual(bytes)) return true;
        }
        return false;
    }
}
