using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PromptFlowStudio.Services.Neural3d;

namespace PromptFlowStudio.Services.ContentSecurity;

public enum WmBand : byte { Low, Mid, High }

/// <summary>워터마크 페이로드: 디바이스 24 · 사용자 24 · 세션 16 비트 = 64 비트 ID(+ CRC-16 = 80 비트).</summary>
public readonly record struct WatermarkPayload(uint Device, uint User, ushort Session)
{
    public ulong Id => ((ulong)(Device & 0xFFFFFF) << 40) | ((ulong)(User & 0xFFFFFF) << 16) | Session;
    public static WatermarkPayload FromId(ulong id) => new((uint)(id >> 40) & 0xFFFFFF, (uint)(id >> 16) & 0xFFFFFF, (ushort)id);
}

/// <summary>검출 결과(값 형식): 성공(CRC) · 페이로드 · 신뢰도(평균 |soft|) · 비트 오류율(기대 페이로드 대비 · 미지정 = −1).</summary>
public readonly record struct WatermarkDetection(bool Valid, WatermarkPayload Payload, float Confidence, float BitErrorRate, int Slots);

/// <summary>워터마크 SIMD 커널(Vector&lt;float&gt; · 꼬리 스칼라 — 스칼라 판과 비트 일치(레인 누적 순서 재현)).</summary>
public static class WmSimd
{
    private static int Body(int n) => Vector.IsHardwareAccelerated && n >= Vector<float>.Count ? n - n % Vector<float>.Count : 0;

    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var n = a.Length; var m = Body(n); var acc = Vector<float>.Zero;
        if (m > 0)
        {
            var va = MemoryMarshal.Cast<float, Vector<float>>(a[..m]); var vb = MemoryMarshal.Cast<float, Vector<float>>(b[..m]);
            for (var k = 0; k < va.Length; k++) acc += va[k] * vb[k];
        }
        var s = 0f;
        for (var l = 0; l < Vector<float>.Count; l++) s += acc[l];
        for (var i = m; i < n; i++) s += a[i] * b[i];
        return s;
    }

    public static float DotScalar(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var n = a.Length; var m = Body(n); var w = Vector<float>.Count;
        Span<float> lanes = stackalloc float[w]; lanes.Clear();
        for (var i = 0; i < m; i++) lanes[i % w] += a[i] * b[i];
        var s = 0f;
        for (var l = 0; l < w; l++) s += lanes[l];
        for (var i = m; i < n; i++) s += a[i] * b[i];
        return s;
    }

    /// <summary>dst += a·s.</summary>
    public static void MulAdd(Span<float> dst, ReadOnlySpan<float> a, float s)
    {
        var n = dst.Length; var m = Body(n);
        if (m > 0)
        {
            var d = MemoryMarshal.Cast<float, Vector<float>>(dst[..m]); var va = MemoryMarshal.Cast<float, Vector<float>>(a[..m]);
            var vs = new Vector<float>(s);
            for (var k = 0; k < d.Length; k++) d[k] = d[k] + va[k] * vs;
        }
        for (var i = m; i < n; i++) dst[i] = dst[i] + a[i] * s;
    }

    public static void MulAddScalar(Span<float> dst, ReadOnlySpan<float> a, float s)
    {
        for (var i = 0; i < dst.Length; i++) dst[i] = dst[i] + a[i] * s;
    }
}

/// <summary>
/// [PromptFlow Studio v16.90.0] 💧 비시각/비청각 워터마킹 인코더 엔진(무할당 프레임 · 키 의존 · 학습 모델 미사용).
///   ▸ 페이로드: 64비트 ID(디바이스/사용자/세션) + CRC-16/CCITT = 80비트 → 키 PN 백색화 → 키 순열 슬롯 반복(소프트 다수결).
///   ▸ 비디오(DCT 도메인): 8×8 블록 · 정규직교 DCT 기저 2개(대역 저/중/고) 투영 = 계수(SIMD 내적) → 디더 QIM(Δ, d = ±Δ/4) → Δc·기저 공간 가산(SIMD) ·
///     지각 마스킹 Δ = 강도 · 베버(블록 평균 휘도 → 이득 불변) · (1 + 마스킹 · σ_AC) — σ_AC 는 내장 계수를 제외한 AC 에너지(Parseval · 삽입 불변 → 디코더 동일 Δ). 휘도 = RGB 동일 가산.
///   ▸ 오디오(위상): 프레임 1024 · 빈 40~103 쌍(기준 k · 데이터 k+1) 차분 위상 부호(진폭 보존 · 최소 섭동 = 여유각 θm 경계까지만 회전 · 강도 = θm · 최소 진폭 바닥) · 직교 DFT 투영/재합성(SIMD) · 7.1.4 12채널(LFE 제외) 동시 · 검출 = 채널 합산.
///   ▸ 3DGS: SH DC(휘도 채널 0) 디더 QIM(키 순열 스플랫) — v16.58 GaussianScene.
///   ▸ 지표: PSNR · 오디오 SNR · 검출 신뢰도/BER.
/// </summary>
public sealed class NeuralInvisibleWatermarkEncoderEngine
{
    public const int B = 8, BlockPixels = 64, PayloadBits = 80, AudioN = 1024, AudioBin0 = 40, AudioPairs = 32, Channels = 12, Lfe = 3;
    private static readonly (int U, int V)[][] BandCoeffs = { new[] { (1, 0), (0, 1) }, new[] { (2, 1), (1, 2) }, new[] { (3, 2), (2, 3) } };
    private static readonly float[][] Basis = BuildBasis();
    private static readonly float[] AudioCos = new float[2 * AudioPairs * AudioN], AudioSin = new float[2 * AudioPairs * AudioN];

    private readonly object _gate = new();
    private readonly byte[] _key;
    private readonly ulong _pnSeed, _permSeed;
    private readonly sbyte[] _bits = new sbyte[PayloadBits], _pn = new sbyte[PayloadBits];
    private readonly float[] _soft = new float[PayloadBits];
    private readonly int[] _softN = new int[PayloadBits];
    private readonly float[] _blk = new float[BlockPixels];
    private int[] _slotBit = Array.Empty<int>();
    private int _w, _h, _bx, _by;
    private WatermarkPayload _payload;

    static NeuralInvisibleWatermarkEncoderEngine()
    {
        for (var p = 0; p < 2 * AudioPairs; p++)
        {
            var k = AudioBin0 + p;
            for (var n = 0; n < AudioN; n++)
            {
                var a = MathF.Tau * k * n / AudioN;
                AudioCos[p * AudioN + n] = MathF.Cos(a); AudioSin[p * AudioN + n] = MathF.Sin(a);
            }
        }
    }

    /// <summary>key = 워터마크 비밀(≥ 16 B). null → 환경변수 PROMPTFLOW_WM_KEY(Base64) → 없으면 프로세스 임시 키(재시작 시 검출 불가).</summary>
    public NeuralInvisibleWatermarkEncoderEngine(byte[]? key = null)
    {
        var eph = false;
        _key = key is { Length: >= 16 } ? (byte[])key.Clone() : LoadKey(out eph);
        EphemeralKey = eph;
        Span<byte> h = stackalloc byte[32];
        HMACSHA256.HashData(_key, "pfs-wm-v1690"u8, h);
        _pnSeed = BinaryPrimitives.ReadUInt64LittleEndian(h) | 1; _permSeed = BinaryPrimitives.ReadUInt64LittleEndian(h[8..]) | 1;
        var s = _pnSeed;
        for (var i = 0; i < PayloadBits; i++) _pn[i] = (sbyte)((Next(ref s) & 1) == 0 ? 1 : -1);
        SetPayload(new WatermarkPayload(0x0A0B0C, 0x123456, 0x0001));
        Span<byte> fp = stackalloc byte[32];
        SHA256.HashData(_key, fp);
        KeyFingerprint = Convert.ToHexString(fp[..4]);
    }

    private static byte[] LoadKey(out bool ephemeral)
    {
        var env = Environment.GetEnvironmentVariable("PROMPTFLOW_WM_KEY");
        if (!string.IsNullOrWhiteSpace(env))
        {
            try { var k = Convert.FromBase64String(env.Trim()); if (k.Length >= 16) { ephemeral = false; return k; } }
            catch (FormatException) { }
        }
        ephemeral = true;
        return RandomNumberGenerator.GetBytes(32);
    }

    public bool EphemeralKey { get; }
    /// <summary>키 지문(SHA-256 앞 4 B · 표시용 · 키 자체 비노출).</summary>
    public string KeyFingerprint { get; }

    // ── 설정 ──
    public WmBand Band { get; set; } = WmBand.Mid;
    public float Strength { get; set; } = 0.1f;
    public float Masking { get; set; } = 0.6f;
    public float AudioStrength { get; set; } = 0.12f;
    public float SplatStep { get; set; } = 0.04f;
    public WatermarkPayload Payload => _payload;

    // ── 계측 ──
    public float LastPsnr { get; private set; }
    public float LastAudioSnrDb { get; private set; }
    public float MeanDelta { get; private set; }
    public long FramesEncoded { get; private set; }
    public long AudioFramesEncoded { get; private set; }
    public double LastEncodeMs { get; private set; }
    public double LastDetectMs { get; private set; }
    public int Width => _w;
    public int Height => _h;
    public int BlocksX => _bx;
    public int BlocksY => _by;
    public int SlotCount => _slotBit.Length;

    private static ulong Next(ref ulong s) { s ^= s >> 12; s ^= s << 25; s ^= s >> 27; return s * 0x2545F4914F6CDD1DUL; }

    private static float[][] BuildBasis()
    {
        var b = new float[64][];
        for (var u = 0; u < 8; u++)
            for (var v = 0; v < 8; v++)
            {
                var a = new float[64];
                float au = u == 0 ? MathF.Sqrt(1f / 8f) : MathF.Sqrt(2f / 8f), av = v == 0 ? MathF.Sqrt(1f / 8f) : MathF.Sqrt(2f / 8f);
                for (var y = 0; y < 8; y++)
                    for (var x = 0; x < 8; x++)
                        a[y * 8 + x] = au * av * MathF.Cos((2 * x + 1) * u * MathF.PI / 16f) * MathF.Cos((2 * y + 1) * v * MathF.PI / 16f);
                b[v * 8 + u] = a;
            }
        return b;
    }

    /// <summary>DCT 기저(u = 가로 주파수 · v = 세로).</summary>
    public static ReadOnlySpan<float> BasisOf(int u, int v) => Basis[v * 8 + u];

    /// <summary>CRC-16/CCITT-FALSE.</summary>
    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++) crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }

    /// <summary>페이로드 설정(제어 평면 · 비트 = ±1 · 백색화 전).</summary>
    public void SetPayload(in WatermarkPayload p)
    {
        lock (_gate)
        {
            _payload = p;
            Span<byte> buf = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(buf, p.Id);
            var crc = Crc16(buf);
            for (var i = 0; i < 64; i++) _bits[i] = (sbyte)(((p.Id >> (63 - i)) & 1) == 1 ? 1 : -1);
            for (var i = 0; i < 16; i++) _bits[64 + i] = (sbyte)(((crc >> (15 - i)) & 1) == 1 ? 1 : -1);
        }
    }

    /// <summary>프레임 기하(제어 평면 · 변경 시만 할당): 슬롯 = 블록 × 2 계수 → 키 순열 비트.</summary>
    public void Configure(int w, int h)
    {
        lock (_gate)
        {
            if (w == _w && h == _h && _slotBit.Length > 0) return;
            _w = w; _h = h; _bx = w / B; _by = h / B;
            var n = _bx * _by * 2;
            _slotBit = new int[n];
            for (var i = 0; i < n; i++) _slotBit[i] = i % PayloadBits;
            var s = _permSeed;
            for (var i = n - 1; i > 0; i--) { var j = (int)(Next(ref s) % (ulong)(i + 1)); (_slotBit[i], _slotBit[j]) = (_slotBit[j], _slotBit[i]); }
        }
    }

    /// <summary>슬롯 → 페이로드 비트 인덱스.</summary>
    public int SlotBit(int slot) => _slotBit[slot];

    /// <summary>슬롯의 기대 부호(페이로드 × PN · ±1).</summary>
    public int ExpectedSign(int slot) => _bits[_slotBit[slot]] * _pn[_slotBit[slot]];

    // ══════════════════════ 비디오 ══════════════════════

    private void LoadBlock(ReadOnlySpan<float> rgb, int bx, int by)
    {
        for (var y = 0; y < B; y++)
        {
            var row = ((by * B + y) * _w + bx * B) * 3;
            for (var x = 0; x < B; x++) { var i = row + x * 3; _blk[y * B + x] = 0.299f * rgb[i] + 0.587f * rgb[i + 1] + 0.114f * rgb[i + 2]; }
        }
    }

    /// <summary>블록 QIM 스텝(내장 계수 제외 AC 에너지 기반 마스킹).</summary>
    private float Step(ReadOnlySpan<float> blk, float c0, float c1)
    {
        var mean = 0f; for (var i = 0; i < BlockPixels; i++) mean += blk[i];
        var dc = mean / 8f; // 정규직교 DC 계수 = Σ/8
        var e = WmSimd.Dot(blk, blk) - dc * dc - c0 * c0 - c1 * c1;
        var sigma = MathF.Sqrt(MathF.Max(e, 0f) / 62f);
        // 베버 정규화(블록 평균 휘도 비례 → 밝기 이득 불변) × 질감 마스킹
        var lum = mean / BlockPixels;
        return Strength * ((lum + 0.05f) / 0.55f) * (1f + Masking * MathF.Min(sigma * 10f, 3f));
    }

    private static float Qim(float c, float delta, int sign)
    {
        var d = sign > 0 ? delta * 0.25f : -delta * 0.25f;
        return delta * MathF.Round((c - d) / delta) + d;
    }

    /// <summary>소프트 판정 −1..1(+ = 비트 1 격자 근접).</summary>
    public static float QimSoft(float c, float delta)
    {
        var d1 = MathF.Abs(c - Qim(c, delta, 1)); var d0 = MathF.Abs(c - Qim(c, delta, -1));
        return Math.Clamp((d0 - d1) / (delta * 0.5f), -1f, 1f);
    }

    /// <summary>프레임 인코딩(제자리 · RGB 교차 0..1 · 무할당).</summary>
    public void EncodeFrame(Span<float> rgb, int w, int h)
    {
        Configure(w, h);
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var (b0, b1) = (BandCoeffs[(int)Band][0], BandCoeffs[(int)Band][1]);
            var basis0 = Basis[b0.V * 8 + b0.U]; var basis1 = Basis[b1.V * 8 + b1.U];
            Span<float> dlt = stackalloc float[BlockPixels];
            double mse = 0, dsum = 0;
            for (var by = 0; by < _by; by++)
                for (var bx = 0; bx < _bx; bx++)
                {
                    LoadBlock(rgb, bx, by);
                    var c0 = WmSimd.Dot(_blk, basis0); var c1 = WmSimd.Dot(_blk, basis1);
                    var delta = Step(_blk, c0, c1);
                    var slot = (by * _bx + bx) * 2;
                    var n0 = Qim(c0, delta, ExpectedSign(slot)); var n1 = Qim(c1, delta, ExpectedSign(slot + 1));
                    dlt.Clear();
                    WmSimd.MulAdd(dlt, basis0, n0 - c0); WmSimd.MulAdd(dlt, basis1, n1 - c1);
                    dsum += MathF.Abs(n0 - c0) + MathF.Abs(n1 - c1);
                    for (var y = 0; y < B; y++)
                    {
                        var row = ((by * B + y) * w + bx * B) * 3;
                        for (var x = 0; x < B; x++)
                        {
                            var d = dlt[y * B + x]; var i = row + x * 3;
                            for (var ch = 0; ch < 3; ch++)
                            {
                                var o = rgb[i + ch]; var v = Math.Clamp(o + d, 0f, 1f);
                                rgb[i + ch] = v; mse += (v - o) * (v - o);
                            }
                        }
                    }
                }
            mse /= Math.Max(1, _bx * _by * BlockPixels * 3);
            LastPsnr = mse > 1e-14 ? (float)(10.0 * Math.Log10(1.0 / mse)) : 99f;
            MeanDelta = (float)(dsum / Math.Max(1, _bx * _by * 2));
            FramesEncoded++;
            LastEncodeMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
    }

    /// <summary>슬롯 소프트 값(블록 단위 · soft 배열 길이 = 슬롯 수 · 무할당) — 위변조 국소 판정용.</summary>
    public void SlotSoft(ReadOnlySpan<float> rgb, int w, int h, Span<float> soft)
    {
        Configure(w, h);
        lock (_gate)
        {
            var (b0, b1) = (BandCoeffs[(int)Band][0], BandCoeffs[(int)Band][1]);
            var basis0 = Basis[b0.V * 8 + b0.U]; var basis1 = Basis[b1.V * 8 + b1.U];
            for (var by = 0; by < _by; by++)
                for (var bx = 0; bx < _bx; bx++)
                {
                    LoadBlock(rgb, bx, by);
                    var c0 = WmSimd.Dot(_blk, basis0); var c1 = WmSimd.Dot(_blk, basis1);
                    var delta = Step(_blk, c0, c1);
                    var slot = (by * _bx + bx) * 2;
                    soft[slot] = QimSoft(c0, delta) * _pn[_slotBit[slot]];
                    soft[slot + 1] = QimSoft(c1, delta) * _pn[_slotBit[slot + 1]];
                }
        }
    }

    private readonly float[] _slotScratch = new float[4096];

    /// <summary>프레임 검출(무할당 · 슬롯 ≤ 4096): 소프트 다수결 → 디백색화 → CRC.</summary>
    public WatermarkDetection DetectFrame(ReadOnlySpan<float> rgb, int w, int h, WatermarkPayload? expected = null)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Configure(w, h);
        var n = _slotBit.Length;
        var soft = n <= _slotScratch.Length ? _slotScratch.AsSpan(0, n) : new float[n];
        SlotSoft(rgb, w, h, soft);
        lock (_gate)
        {
            Array.Clear(_soft); Array.Clear(_softN);
            for (var s = 0; s < n; s++) { _soft[_slotBit[s]] += soft[s]; _softN[_slotBit[s]]++; }
            var r = Decide(n, expected);
            LastDetectMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            return r;
        }
    }

    /// <summary>누적 소프트(_soft = 디백색화 완료값 합) → 비트 · CRC · 신뢰도.</summary>
    private WatermarkDetection Decide(int slots, WatermarkPayload? expected)
    {
        ulong id = 0; ushort crc = 0; float conf = 0f;
        for (var i = 0; i < PayloadBits; i++)
        {
            var v = _softN[i] > 0 ? _soft[i] / _softN[i] : 0f;
            conf += MathF.Abs(v);
            var bit = v > 0f ? 1UL : 0UL;
            if (i < 64) id |= bit << (63 - i); else crc |= (ushort)(bit << (79 - i));
        }
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buf, id);
        var valid = Crc16(buf) == crc;
        var ber = -1f;
        if (expected is { } e)
        {
            var errs = 0;
            for (var i = 0; i < 64; i++) if ((((e.Id >> (63 - i)) & 1) == 1) != (((id >> (63 - i)) & 1) == 1)) errs++;
            ber = errs / 64f;
        }
        return new WatermarkDetection(valid, WatermarkPayload.FromId(id), conf / PayloadBits, ber, slots);
    }

    // ══════════════════════ 오디오(위상) ══════════════════════

    /// <summary>
    /// 7.1.4 베드 1 프레임(채널 c = bed[c·stride .. +1024]) 위상 인코딩(제자리 · 무할당).
    /// 프레임 번호 frame → 32 비트 구간 = (frame mod 3) · 32 … (80 비트 순환).
    /// </summary>
    public void EncodeAudio(Span<float> bed, int stride, long frame)
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            double sig = 0, noise = 0;
            var baseBit = (int)(frame % 3) * AudioPairs;
            for (var c = 0; c < Channels; c++)
            {
                if (c == Lfe) continue;
                var x = bed.Slice(c * stride, AudioN);
                var rms = MathF.Sqrt(WmSimd.Dot(x, x) / AudioN);
                if (rms < 1e-5f) continue;
                sig += rms * rms * AudioN;
                var floor = rms * 0.02f * AudioN / 2f;
                for (var p = 0; p < AudioPairs; p++)
                {
                    var bi = (baseBit + p) % PayloadBits;
                    var sign = _bits[bi] * _pn[bi];
                    int pr = 2 * p, pd = 2 * p + 1;
                    var cr = AudioCos.AsSpan(pr * AudioN, AudioN); var sr = AudioSin.AsSpan(pr * AudioN, AudioN);
                    var cd = AudioCos.AsSpan(pd * AudioN, AudioN); var sd = AudioSin.AsSpan(pd * AudioN, AudioN);
                    float rRe = WmSimd.Dot(x, cr), rIm = -WmSimd.Dot(x, sr);
                    float dRe = WmSimd.Dot(x, cd), dIm = -WmSimd.Dot(x, sd);
                    var mag = MathF.Max(MathF.Sqrt(dRe * dRe + dIm * dIm), floor);
                    var phiR = MathF.Atan2(rIm, rRe); var phiD = MathF.Atan2(dIm, dRe);
                    // 최소 섭동: 상대 위상 rel 이 부호 영역 [θm, π − θm](또는 음수 대칭) 안이면 유지 · 밖이면 가장 가까운 경계로만 회전
                    var thm = Math.Clamp(AudioStrength, 0.02f, 1f) * MathF.PI * 0.5f;
                    var rel = MathF.IEEERemainder(phiD - phiR, MathF.Tau);
                    var sp = sign > 0 ? rel : -rel;
                    if (sp >= thm && sp <= MathF.PI - thm && mag > floor * 1.01f) continue;
                    var tgt = sp < thm && sp > -MathF.PI * 0.5f ? thm : sp > MathF.PI - thm || sp <= -MathF.PI * 0.5f ? MathF.PI - thm : sp;
                    var phi = phiR + (sign > 0 ? tgt : -tgt);
                    float nRe = mag * MathF.Cos(phi), nIm = mag * MathF.Sin(phi);
                    float aRe = nRe - dRe, aIm = nIm - dIm;
                    // x += (2/N)·Re(ΔX·e^{jωn}) = (2/N)(ΔRe·cos − ΔIm·sin)
                    WmSimd.MulAdd(x, cd, 2f * aRe / AudioN);
                    WmSimd.MulAdd(x, sd, -2f * aIm / AudioN);
                    noise += 2.0 * (aRe * aRe + aIm * aIm) / AudioN;
                }
            }
            LastAudioSnrDb = noise > 1e-20 ? (float)(10.0 * Math.Log10(sig / noise)) : 120f;
            AudioFramesEncoded++;
            LastEncodeMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
    }

    /// <summary>오디오 누적 검출 시작/누적/판정(프레임 3개 이상 · 채널 합산 · 무할당).</summary>
    public void ResetAudioDetect() { lock (_gate) { Array.Clear(_aSoft); Array.Clear(_aN); } }

    private readonly float[] _aSoft = new float[PayloadBits];
    private readonly int[] _aN = new int[PayloadBits];

    public void AccumulateAudio(ReadOnlySpan<float> bed, int stride, long frame)
    {
        lock (_gate)
        {
            var baseBit = (int)(frame % 3) * AudioPairs;
            for (var p = 0; p < AudioPairs; p++)
            {
                var bi = (baseBit + p) % PayloadBits;
                float s = 0f;
                for (var c = 0; c < Channels; c++)
                {
                    if (c == Lfe) continue;
                    var x = bed.Slice(c * stride, AudioN);
                    int pr = 2 * p, pd = 2 * p + 1;
                    float rRe = WmSimd.Dot(x, AudioCos.AsSpan(pr * AudioN, AudioN)), rIm = -WmSimd.Dot(x, AudioSin.AsSpan(pr * AudioN, AudioN));
                    float dRe = WmSimd.Dot(x, AudioCos.AsSpan(pd * AudioN, AudioN)), dIm = -WmSimd.Dot(x, AudioSin.AsSpan(pd * AudioN, AudioN));
                    // sin(φd − φr) = Im(D·conj(R)) / |D||R|
                    var im = dIm * rRe - dRe * rIm; var mm = MathF.Sqrt((dRe * dRe + dIm * dIm) * (rRe * rRe + rIm * rIm));
                    if (mm > 1e-9f) s += im / mm;
                }
                _aSoft[bi] += s * _pn[bi]; _aN[bi] += Channels - 1;
            }
        }
    }

    public WatermarkDetection DecideAudio(WatermarkPayload? expected = null)
    {
        lock (_gate)
        {
            _aSoft.AsSpan().CopyTo(_soft); _aN.AsSpan().CopyTo(_softN);
            var slots = 0; foreach (var v in _aN) slots += v;
            return Decide(slots, expected);
        }
    }

    // ══════════════════════ 3DGS ══════════════════════

    /// <summary>스플랫 SH DC 채널 0 디더 QIM(키 순열 · 무할당 · 반환 = 내장 수).</summary>
    public int EncodeSplats(GaussianScene scene)
    {
        lock (_gate)
        {
            var s = _permSeed ^ 0x5A5A;
            for (var i = 0; i < scene.Count; i++)
            {
                var bi = (int)(Next(ref s) % PayloadBits);
                ref var dc = ref scene.Sh[i * GaussianScene.ShStride];
                dc = Qim(dc, SplatStep, _bits[bi] * _pn[bi]);
            }
            scene.Version++;
            return scene.Count;
        }
    }

    public WatermarkDetection DetectSplats(GaussianScene scene, WatermarkPayload? expected = null)
    {
        lock (_gate)
        {
            Array.Clear(_soft); Array.Clear(_softN);
            var s = _permSeed ^ 0x5A5A;
            for (var i = 0; i < scene.Count; i++)
            {
                var bi = (int)(Next(ref s) % PayloadBits);
                _soft[bi] += QimSoft(scene.Sh[i * GaussianScene.ShStride], SplatStep) * _pn[bi]; _softN[bi]++;
            }
            return Decide(scene.Count, expected);
        }
    }
}
