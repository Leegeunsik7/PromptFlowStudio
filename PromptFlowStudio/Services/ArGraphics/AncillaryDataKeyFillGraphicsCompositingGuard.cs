using System.Buffers.Binary;
using System.Numerics;
using PromptFlowStudio.Services.MasterControl;

namespace PromptFlowStudio.Services.ArGraphics;

public enum KeyFillFault : byte { None, FillExceedsKey, StraightAsPremultiplied }

/// <summary>
/// [PromptFlow Studio v17.06.0] Key/Fill 8비트 합성 SIMD 커널(스칼라 쌍둥이와 비트 동일).
/// div255(x) = (x + 128 + ((x + 128) &gt;&gt; 8)) &gt;&gt; 8 = round(x / 255) (x ≤ 65025).
/// 프리멀티: out = min(255, F + div255(B·(255 − K))) · 스트레이트: out = div255(F·K + B·(255 − K)).
/// </summary>
public static class KeyFillSimd
{
    public static int Div255(int x) => (x + 128 + ((x + 128) >> 8)) >> 8;

    private static Vector<ushort> Div255(Vector<ushort> x)
    {
        var t = x + new Vector<ushort>(128);
        return Vector.ShiftRightLogical(t + Vector.ShiftRightLogical(t, 8), 8);
    }

    public static void Premultiplied(ReadOnlySpan<byte> bg, ReadOnlySpan<byte> fill, ReadOnlySpan<byte> key, Span<byte> dst)
    {
        int i = 0, n = dst.Length;
        if (Vector.IsHardwareAccelerated && n >= Vector<byte>.Count)
        {
            var c255 = new Vector<ushort>(255);
            for (; i <= n - Vector<byte>.Count; i += Vector<byte>.Count)
            {
                Vector.Widen(new Vector<byte>(bg.Slice(i)), out var bl, out var bh);
                Vector.Widen(new Vector<byte>(fill.Slice(i)), out var fl, out var fh);
                Vector.Widen(new Vector<byte>(key.Slice(i)), out var kl, out var kh);
                var lo = Vector.Min(fl + Div255(bl * (c255 - kl)), c255);
                var hi = Vector.Min(fh + Div255(bh * (c255 - kh)), c255);
                Vector.Narrow(lo, hi).CopyTo(dst.Slice(i));
            }
        }
        for (; i < n; i++) dst[i] = (byte)Math.Min(255, fill[i] + Div255(bg[i] * (255 - key[i])));
    }

    public static void PremultipliedScalar(ReadOnlySpan<byte> bg, ReadOnlySpan<byte> fill, ReadOnlySpan<byte> key, Span<byte> dst)
    {
        for (var i = 0; i < dst.Length; i++) dst[i] = (byte)Math.Min(255, fill[i] + Div255(bg[i] * (255 - key[i])));
    }

    public static void Straight(ReadOnlySpan<byte> bg, ReadOnlySpan<byte> fill, ReadOnlySpan<byte> key, Span<byte> dst)
    {
        int i = 0, n = dst.Length;
        if (Vector.IsHardwareAccelerated && n >= Vector<byte>.Count)
        {
            var c255 = new Vector<ushort>(255);
            for (; i <= n - Vector<byte>.Count; i += Vector<byte>.Count)
            {
                Vector.Widen(new Vector<byte>(bg.Slice(i)), out var bl, out var bh);
                Vector.Widen(new Vector<byte>(fill.Slice(i)), out var fl, out var fh);
                Vector.Widen(new Vector<byte>(key.Slice(i)), out var kl, out var kh);
                Vector.Narrow(Div255(fl * kl + bl * (c255 - kl)), Div255(fh * kh + bh * (c255 - kh))).CopyTo(dst.Slice(i));
            }
        }
        for (; i < n; i++) dst[i] = (byte)Div255(fill[i] * key[i] + bg[i] * (255 - key[i]));
    }

    public static void StraightScalar(ReadOnlySpan<byte> bg, ReadOnlySpan<byte> fill, ReadOnlySpan<byte> key, Span<byte> dst)
    {
        for (var i = 0; i < dst.Length; i++) dst[i] = (byte)Div255(fill[i] * key[i] + bg[i] * (255 - key[i]));
    }

    /// <summary>x · g / 256(g ∈ 0..256) — 다운스트림 키어 게인.</summary>
    public static void Gain(ReadOnlySpan<byte> src, Span<byte> dst, int g)
    {
        int i = 0, n = dst.Length;
        if (Vector.IsHardwareAccelerated && n >= Vector<byte>.Count)
        {
            var vg = new Vector<ushort>((ushort)g);
            for (; i <= n - Vector<byte>.Count; i += Vector<byte>.Count)
            {
                Vector.Widen(new Vector<byte>(src.Slice(i)), out var l, out var h);
                Vector.Narrow(Vector.ShiftRightLogical(l * vg, 8), Vector.ShiftRightLogical(h * vg, 8)).CopyTo(dst.Slice(i));
            }
        }
        for (; i < n; i++) dst[i] = (byte)(src[i] * g >> 8);
    }

    /// <summary>방송 합법 레벨(16..235) 클립.</summary>
    public static void Legalize(Span<byte> d, byte lo, byte hi)
    {
        int i = 0, n = d.Length;
        if (Vector.IsHardwareAccelerated && n >= Vector<byte>.Count)
        {
            Vector<byte> vl = new(lo), vh = new(hi);
            for (; i <= n - Vector<byte>.Count; i += Vector<byte>.Count)
                Vector.Min(Vector.Max(new Vector<byte>(d.Slice(i)), vl), vh).CopyTo(d.Slice(i));
        }
        for (; i < n; i++) d[i] = Math.Clamp(d[i], lo, hi);
    }
}

/// <summary>
/// [PromptFlow Studio v17.06.0] SMPTE ST 2110-40(RFC 8331) 다중 ANC 패커/파서 — ATC 타임코드(DID 0x60/0x60 · 라인 9) ·
/// CEA-708 CDP(DID 0x61/0x01 · 라인 10 · cc_data 10 트리플 · CEA-608 필드 1 홀수 패리티) · FreeD 트래킹 메타데이터(DID 0x50/0x01 · 사용자 정의 2형 · 라인 11).
/// 10비트 워드 패리티(b8 = 짝수 패리티 · b9 = !b8) · 9비트 체크섬 · 32비트 정렬.
/// </summary>
public static class AncPacker
{
    public const int RtpHeader = 12, Header = 20, MaxPayload = 512;
    public const int AtcDid = 0x60, AtcSdid = 0x60, CdpDid = 0x61, CdpSdid = 0x01, FreeDDid = 0x50, FreeDSdid = 0x01;
    public const int CcCount = 10;

    public static int Word10(int v8)
    {
        var par = BitOperations.PopCount((uint)(v8 & 0xFF)) & 1;
        return v8 & 0xFF | par << 8 | (par ^ 1) << 9;
    }

    public static bool WordOk(int w) { var par = BitOperations.PopCount((uint)(w & 0xFF)) & 1; return (w >> 8 & 1) == par && (w >> 9 & 1) == (par ^ 1); }

    public static byte OddParity(int c) { c &= 0x7F; return (byte)((BitOperations.PopCount((uint)c) & 1) == 0 ? c | 0x80 : c); }

    public ref struct BitWriter
    {
        private readonly Span<byte> _d;
        public int Pos;
        public BitWriter(Span<byte> d) { _d = d; Pos = 0; }
        public void Put(int v, int n)
        {
            for (var i = n - 1; i >= 0; i--, Pos++)
            {
                var bi = Pos >> 3; var m = 0x80 >> (Pos & 7);
                _d[bi] = (byte)((v >> i & 1) != 0 ? _d[bi] | m : _d[bi] & ~m);
            }
        }
        public void Align32() { while ((Pos & 31) != 0) Put(0, 1); }
    }

    public ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _s;
        public int Pos;
        public BitReader(ReadOnlySpan<byte> s) { _s = s; Pos = 0; }
        public bool Has(int n) => Pos + n <= _s.Length * 8;
        public int Get(int n) { var v = 0; for (var i = 0; i < n; i++, Pos++) v = v << 1 | _s[Pos >> 3] >> (7 - (Pos & 7)) & 1; return v; }
        public void Align32() { Pos = (Pos + 31) & ~31; }
    }

    /// <summary>ANC 1개(헤더 + DID/SDID/DC + UDW + 체크섬 + 32비트 정렬).</summary>
    public static void Put(ref BitWriter w, int line, int did, int sdid, scoped ReadOnlySpan<byte> udw)
    {
        w.Put(0, 1); w.Put(line, 11); w.Put(0xFFF, 12); w.Put(0, 1); w.Put(0, 7);
        int a = Word10(did), b = Word10(sdid), c = Word10(udw.Length);
        w.Put(a, 10); w.Put(b, 10); w.Put(c, 10);
        var sum = (a & 0x1FF) + (b & 0x1FF) + (c & 0x1FF);
        for (var i = 0; i < udw.Length; i++) { var u = Word10(udw[i]); w.Put(u, 10); sum += u & 0x1FF; }
        sum &= 0x1FF;
        w.Put(sum | ((sum >> 8 & 1) ^ 1) << 9, 10);
        w.Align32();
    }

    /// <summary>CEA-708 CDP: 식별자 0x9669 · 길이 · 프레임률(60p = 8) · 플래그 · 시퀀스 · cc_data(0x72) · 푸터(0x74) · 체크섬(합 0).</summary>
    public static int BuildCdp(Span<byte> d, ushort seq, byte cc1, byte cc2)
    {
        var n = 0;
        d[n++] = 0x96; d[n++] = 0x69; d[n++] = 0;
        d[n++] = 0x8F; d[n++] = 0x43;
        d[n++] = (byte)(seq >> 8); d[n++] = (byte)seq;
        d[n++] = 0x72; d[n++] = 0xE0 | CcCount;
        d[n++] = 0xFC; d[n++] = cc1; d[n++] = cc2;
        for (var i = 1; i < CcCount; i++) { d[n++] = 0xFA; d[n++] = 0; d[n++] = 0; }
        d[n++] = 0x74; d[n++] = (byte)(seq >> 8); d[n++] = (byte)seq;
        d[2] = (byte)(n + 1);
        var s = 0;
        for (var i = 0; i < n; i++) s += d[i];
        d[n++] = (byte)(-s);
        return n;
    }

    /// <summary>CDP 검증 → 필드 1 CC 쌍(패리티 제거 전 원 바이트).</summary>
    public static bool ParseCdp(ReadOnlySpan<byte> d, out byte cc1, out byte cc2)
    {
        cc1 = cc2 = 0;
        if (d.Length < 11 || d[0] != 0x96 || d[1] != 0x69 || d[2] != d.Length) return false;
        var s = 0;
        for (var i = 0; i < d.Length; i++) s += d[i];
        if ((s & 0xFF) != 0 || d[7] != 0x72) return false;
        var cnt = d[8] & 0x1F;
        for (var k = 0; k < cnt; k++)
        {
            var o = 9 + k * 3;
            if (o + 2 >= d.Length) return false;
            if ((d[o] & 0x07) == 0x04) { cc1 = d[o + 1]; cc2 = d[o + 2]; return true; }   // cc_valid=1 · cc_type=00(608 필드 1)
        }
        return true;
    }
}

/// <summary>
/// [PromptFlow Studio v17.06.0] 🛡 SMPTE ST 2110-40 ANC &amp; Key/Fill 알파 합성 가드.
/// PGM 비디오 버스 위에 그래픽 Fill/Key를 프리멀티/스트레이트 모드로 SIMD 합성하면서
/// ① 프리멀티 규칙 위반(Fill &gt; Key) 감시 · 클램프, ② 스트레이트 Fill이 프리멀티로 들어온 모드 불일치(헤일로) 감지 · 자동 교정,
/// ③ 다운스트림 키어 게인(AUTO 페이드) · ④ 방송 합법 레벨(16–235) 리걸라이저 · ⑤ HUD 타이틀 세이프(90%) 검증,
/// ⑥ 자막(CEA-608 in 708 CDP) · ATC 타임코드 · FreeD 트래킹 메타데이터를 같은 RTP 타임스탬프의 ST 2110-40 패킷으로 패킹 →
/// 루프백 파싱으로 패리티/체크섬/프레임 동기 검증. 전 경로 무할당.
/// </summary>
public sealed class AncillaryDataKeyFillGraphicsCompositingGuard
{
    public const int CaptionMax = 48;

    private readonly byte[] _out, _fillG, _keyG, _fixed, _pkt = new byte[AncPacker.RtpHeader + AncPacker.Header + AncPacker.MaxPayload];
    private readonly byte[] _cdp = new byte[64];
    private readonly char[] _capTx = new char[CaptionMax], _capRx = new char[CaptionMax];
    private int _capTxN, _capTxPos = -1, _capRxN, _len;
    private ushort _seq, _cdpSeq;
    private float _gain = 1f;

    public AncillaryDataKeyFillGraphicsCompositingGuard(int width = 256, int height = 144)
    {
        Width = width; Height = height;
        var n = width * height * 4;
        _out = new byte[n]; _fillG = new byte[n]; _keyG = new byte[n]; _fixed = new byte[n];
    }

    public int Width { get; }
    public int Height { get; }
    public FillMode Expected { get; set; } = FillMode.Premultiplied;
    public bool AutoCorrect { get; set; } = true;
    public bool BroadcastSafe { get; set; } = true;
    public bool KeyerOn { get; set; } = true;
    public int AutoFrames { get; set; } = 20;
    public float Gain => _gain;
    /// <summary>모드 불일치 판정 임계 — Fill이 Key를 8레벨 넘게 초과한 픽셀 수(진짜 프리멀티 소스는 반올림 ±1~2 수준).</summary>
    public int MismatchPixels { get; set; } = 16;
    public KeyFillFault LastFault { get; private set; }
    public long Frames { get; private set; }
    public long Violations { get; private set; }
    public long ClampedPixels { get; private set; }
    public long MismatchFrames { get; private set; }
    public long Corrected { get; private set; }
    public long IllegalLevels { get; private set; }
    public long TitleSafeViolations { get; private set; }
    public int LastViolations { get; private set; }
    public double CompositeUs { get; private set; }
    public ReadOnlySpan<byte> Output => _out;
    public ReadOnlySpan<byte> KeyOut => _keyG;
    public ReadOnlySpan<byte> FillOut => _fillG;
    public ReadOnlySpan<byte> LastAncPacket => _pkt.AsSpan(0, _len);

    // ANC 수신 측(루프백)
    public long AncPackets { get; private set; }
    public long AncErrors { get; private set; }
    public long SyncErrors { get; private set; }
    public long CaptionPairs { get; private set; }
    public long LastTimecode { get; private set; } = -1;
    public FreeDPose RxPose { get; private set; }
    public long RxPoses { get; private set; }
    public int AncCount { get; private set; }
    public int AncBytes => _len;
    public ReadOnlySpan<char> RxCaption => _capRx.AsSpan(0, _capRxN);

    public void Take() => KeyerOn = !KeyerOn;

    /// <summary>
    /// 1 프레임 합성: 게인 램프 → 위반 검사(프리멀티) → 모드 불일치 자동 교정 또는 클램프 → SIMD 합성 → 리걸라이저.
    /// </summary>
    public void Composite(ReadOnlySpan<byte> pgm, ReadOnlySpan<byte> fill, ReadOnlySpan<byte> key, FillMode mode)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Frames++;
        var step = 1f / Math.Max(1, AutoFrames);
        _gain = KeyerOn ? MathF.Min(1f, _gain + step) : MathF.Max(0f, _gain - step);
        var g = (int)MathF.Round(_gain * 256f);
        LastFault = KeyFillFault.None;
        var effective = mode;
        ReadOnlySpan<byte> f = fill;
        if (mode == FillMode.Premultiplied)
        {
            int bad = 0, sig = 0;
            for (var o = 0; o < key.Length; o += 4)
            {
                var k = key[o];
                if (k == 255) continue;
                var mx = Math.Max(fill[o], Math.Max(fill[o + 1], fill[o + 2]));
                if (mx <= k) continue;
                bad++;
                if (mx > k + 8) sig++;   // 반올림 오차가 아닌 큰 초과 = 스트레이트 색값 증거
            }
            LastViolations = bad; Violations += bad;
            if (bad > 0)
            {
                if (AutoCorrect && sig >= MismatchPixels)
                {
                    LastFault = KeyFillFault.StraightAsPremultiplied; MismatchFrames++; Corrected++;
                    effective = FillMode.Straight;   // 스트레이트로 해석(헤일로 제거)
                }
                else
                {
                    LastFault = KeyFillFault.FillExceedsKey;
                    for (var i = 0; i < fill.Length; i++) { var k = key[i & ~3]; var v = fill[i]; _fixed[i] = v > k ? k : v; }
                    ClampedPixels += bad;
                    f = _fixed;
                }
            }
        }
        else LastViolations = 0;
        // 게인: 프리멀티 = Fill·Key 동일 스케일 · 스트레이트 = Key만.
        if (effective == FillMode.Premultiplied) KeyFillSimd.Gain(f, _fillG, g); else f.CopyTo(_fillG);
        KeyFillSimd.Gain(key, _keyG, g);
        if (effective == FillMode.Premultiplied) KeyFillSimd.Premultiplied(pgm, _fillG, _keyG, _out);
        else KeyFillSimd.Straight(pgm, _fillG, _keyG, _out);
        var illegal = 0;
        for (var o = 0; o < _out.Length; o += 4)
            if (_out[o] < 16 || _out[o] > 235 || _out[o + 1] < 16 || _out[o + 1] > 235 || _out[o + 2] < 16 || _out[o + 2] > 235) illegal++;
        IllegalLevels += illegal;
        if (BroadcastSafe) KeyFillSimd.Legalize(_out, 16, 235);
        for (var o = 3; o < _out.Length; o += 4) _out[o] = 255;
        CompositeUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>HUD 사각형 타이틀 세이프(가장자리 5%) 검증 → 위반 수.</summary>
    public int CheckTitleSafe(OnAirArTelestrationSportsTelemetrySynthesizer s)
    {
        int mx = Width / 20, my = Height / 20, bad = 0;
        for (var i = 0; i < s.HudCount; i++)
        {
            var r = s.Hud(i);
            if (r.X < mx || r.Y < my || r.X + r.W > Width - mx || r.Y + r.H > Height - my) bad++;
        }
        TitleSafeViolations += bad;
        return bad;
    }

    // ── 자막 송신(CEA-608 · 프레임당 2자) ──────────────────

    public void SetCaption(ReadOnlySpan<char> text)
    {
        var n = Math.Min(text.Length, CaptionMax);
        for (var i = 0; i < n; i++) { var c = text[i]; _capTx[i] = c >= ' ' && c < 127 ? c : '?'; }
        _capTxN = n; _capTxPos = -1;   // −1 = EDM(표시 메모리 소거) 제어 쌍 먼저
    }

    private void NextCc(out byte cc1, out byte cc2)
    {
        if (_capTxPos < 0) { cc1 = AncPacker.OddParity(0x14); cc2 = AncPacker.OddParity(0x2C); _capTxPos = 0; return; }
        if (_capTxPos >= _capTxN) { cc1 = cc2 = 0x80; return; }
        cc1 = AncPacker.OddParity(_capTx[_capTxPos++]);
        cc2 = _capTxPos < _capTxN ? AncPacker.OddParity(_capTx[_capTxPos++]) : (byte)0x80;
    }

    public bool CaptionSent => _capTxPos >= _capTxN;

    /// <summary>프레임 ANC 패킷 생성(RTP ts = 비디오 프레임 ts) — ATC + CDP + FreeD.</summary>
    public int Pack(long frame, ReadOnlySpan<byte> freeD)
    {
        var p = _pkt.AsSpan();
        p.Clear();
        p[0] = 0x80; p[1] = 0x80 | 100;
        BinaryPrimitives.WriteUInt16BigEndian(p.Slice(2), _seq);
        BinaryPrimitives.WriteUInt32BigEndian(p.Slice(4), (uint)(frame * 1500));
        BinaryPrimitives.WriteUInt32BigEndian(p.Slice(8), 0x2110_0040);
        BinaryPrimitives.WriteUInt16BigEndian(p.Slice(12), (ushort)(_seq >> 16));
        _seq++;
        var count = freeD.Length >= FreeD.PacketBytes ? 3 : 2;
        p[16] = (byte)count;
        var w = new AncPacker.BitWriter(p.Slice(AncPacker.RtpHeader + 8));
        Span<byte> bcd = stackalloc byte[4];
        SmpteTimecode.ToBcd(frame, 60, bcd);
        Span<byte> atc = stackalloc byte[16];
        for (var i = 0; i < 16; i++) atc[i] = (byte)(i < 8 ? ((bcd[i >> 1] >> ((i & 1) * 4)) & 0xF) << 4 : 0);
        AncPacker.Put(ref w, 9, AncPacker.AtcDid, AncPacker.AtcSdid, atc);
        NextCc(out var cc1, out var cc2);
        var cl = AncPacker.BuildCdp(_cdp, _cdpSeq++, cc1, cc2);
        AncPacker.Put(ref w, 10, AncPacker.CdpDid, AncPacker.CdpSdid, _cdp.AsSpan(0, cl));
        if (count == 3) AncPacker.Put(ref w, 11, AncPacker.FreeDDid, AncPacker.FreeDSdid, freeD.Slice(0, FreeD.PacketBytes));
        var bytes = w.Pos / 8;
        BinaryPrimitives.WriteUInt16BigEndian(p.Slice(14), (ushort)bytes);
        _len = AncPacker.RtpHeader + 8 + bytes;
        AncCount = count;
        return _len;
    }

    /// <summary>ANC 패킷 수신/검증(패리티 · 체크섬 · CDP · FreeD) → 자막 재조립 · 메타데이터 프레임 동기 확인.</summary>
    public bool Receive(ReadOnlySpan<byte> p, long videoFrame)
    {
        if (p.Length < AncPacker.RtpHeader + 8 || (p[1] & 0x7F) != 100) { AncErrors++; return false; }
        long frame = BinaryPrimitives.ReadUInt32BigEndian(p.Slice(4)) / 1500;
        if (frame != videoFrame) SyncErrors++;
        int count = p[16], len = BinaryPrimitives.ReadUInt16BigEndian(p.Slice(14));
        if (AncPacker.RtpHeader + 8 + len > p.Length) { AncErrors++; return false; }
        var r = new AncPacker.BitReader(p.Slice(AncPacker.RtpHeader + 8, len));
        Span<byte> udw = stackalloc byte[255];
        Span<byte> bcd = stackalloc byte[4];
        var ok = true;
        for (var k = 0; k < count && ok; k++)
        {
            if (!r.Has(62)) { ok = false; break; }
            r.Get(32);
            int did = r.Get(10), sdid = r.Get(10), dc = r.Get(10);
            if (!AncPacker.WordOk(did) || !AncPacker.WordOk(sdid) || !AncPacker.WordOk(dc)) { ok = false; break; }
            var n = dc & 0xFF;
            if (!r.Has(n * 10 + 10)) { ok = false; break; }
            var sum = (did & 0x1FF) + (sdid & 0x1FF) + (dc & 0x1FF);
            for (var i = 0; i < n; i++)
            {
                var u = r.Get(10);
                if (!AncPacker.WordOk(u)) ok = false;
                sum += u & 0x1FF; udw[i] = (byte)u;
            }
            var cs = r.Get(10); sum &= 0x1FF;
            if ((cs & 0x1FF) != sum || (cs >> 9 & 1) != ((sum >> 8 & 1) ^ 1)) ok = false;
            r.Align32();
            if (!ok) break;
            int d = did & 0xFF, s = sdid & 0xFF;
            var data = udw.Slice(0, n);
            if (d == AncPacker.AtcDid && s == AncPacker.AtcSdid && n == 16)
            {
                bcd.Clear();
                for (var i = 0; i < 8; i++) bcd[i >> 1] |= (byte)((data[i] >> 4 & 0xF) << ((i & 1) * 4));
                LastTimecode = SmpteTimecode.FromBcd(bcd, 60);
            }
            else if (d == AncPacker.CdpDid && s == AncPacker.CdpSdid)
            {
                if (!AncPacker.ParseCdp(data, out var c1, out var c2)) { ok = false; break; }
                Cc(c1, c2);
            }
            else if (d == AncPacker.FreeDDid && s == AncPacker.FreeDSdid)
            {
                if (!FreeD.Decode(data, out var pose)) { ok = false; break; }
                RxPose = pose; RxPoses++;
            }
        }
        if (!ok) { AncErrors++; return false; }
        AncPackets++;
        return true;
    }

    private void Cc(byte c1, byte c2)
    {
        if (c1 == 0 && c2 == 0) return;
        if (BitOperations.PopCount(c1) % 2 == 0 || BitOperations.PopCount(c2) % 2 == 0) { AncErrors++; return; }
        int a = c1 & 0x7F, b = c2 & 0x7F;
        if (a == 0x14 && b == 0x2C) { _capRxN = 0; CaptionPairs++; return; }
        if (a == 0 && b == 0) return;
        if (a >= 0x20 && _capRxN < CaptionMax) _capRx[_capRxN++] = (char)a;
        if (b >= 0x20 && _capRxN < CaptionMax) _capRx[_capRxN++] = (char)b;
        CaptionPairs++;
    }
}
