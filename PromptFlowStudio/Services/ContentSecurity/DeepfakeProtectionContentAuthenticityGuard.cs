using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace PromptFlowStudio.Services.ContentSecurity;

public enum AuthVerdict : byte { Authentic, Recompressed, Tampered, Unmarked, SignatureInvalid }
public enum ThreatAxis : byte { Recompression, LocalTamper, Synthetic, WatermarkLoss, FingerprintDrift, Signature }

/// <summary>
/// 봉인 매니페스트(인코딩 시점 · 선할당): 프레임 번호 · 페이로드 ID · 타일 지문(64비트) · 전역 지문 · 집계(타일/블록/평균 휘도) · HMAC-SHA512 서명.
/// </summary>
public sealed class FrameManifest
{
    public const int MaxTiles = 256;
    public long Frame;
    public ulong PayloadId, GlobalHash;
    public int Tiles, Blocks, Width, Height;
    public float MeanLuma;
    public readonly ulong[] TileHash = new ulong[MaxTiles];
    public readonly byte[] Signature = new byte[64];
    public bool Sealed;

    public void CopyFrom(FrameManifest m)
    {
        Frame = m.Frame; PayloadId = m.PayloadId; GlobalHash = m.GlobalHash; Tiles = m.Tiles; Blocks = m.Blocks; Width = m.Width; Height = m.Height; MeanLuma = m.MeanLuma;
        m.TileHash.AsSpan().CopyTo(TileHash); m.Signature.AsSpan().CopyTo(Signature); Sealed = m.Sealed;
    }
}

/// <summary>
/// [PromptFlow Studio v16.90.0] 🕵 딥페이크 방지 &amp; 콘텐츠 진위 검증 가드(무할당 검증 · 학습 모델 미사용 · 서명 = HMAC-SHA512 FixedTimeEquals).
///   ▸ 봉인: 타일(4×3 블록 = 32×24 px) 지각 지문 64비트(블록 DC 대 타일 평균 · 저주파 AC 부호) + 전역 8×8 평균 지문 → 캐노니컬 페이로드(집계 필드 포함) HMAC-SHA512.
///   ▸ 검증: ① 매니페스트 서명 ② 워터마크 프레임 검출(ID · 신뢰도) ③ 타일별 워터마크 슬롯 일치율(기대 부호 · 프레임 중앙값 대비) ④ 타일 지문 해밍 거리
///     ⑤ 재압축 = 블록 경계 단차비 상승 + 전 타일 균일 일치율 하락 ⑥ 합성 흔적 = 체커보드(−1)^{x+y} · 2× 최근접 업샘플 계단 에너지.
///   ▸ 판정: 서명 불일치 → SignatureInvalid · 워터마크/지문 모두 불일치 → Unmarked · 국소 타일 이상(일치율 급락 ∧ 지문 변화) → Tampered(영역 맵 · 오버레이) · 균일 열화 → Recompressed · 그 외 Authentic.
///   ▸ 역추적: 잔존 워터마크 ID → 디바이스/사용자(유출 경로) · 레이더 6축(0..1).
/// </summary>
public sealed class DeepfakeProtectionContentAuthenticityGuard
{
    public const int TileBx = 4, TileBy = 3, Axes = 6;
    private readonly object _gate = new();
    private readonly byte[] _key;
    private readonly NeuralInvisibleWatermarkEncoderEngine _wm;
    private readonly byte[] _canon = new byte[64 + FrameManifest.MaxTiles * 8];
    private readonly float[] _slotSoft = new float[4096];
    private readonly float[] _tileAgree = new float[FrameManifest.MaxTiles], _tileSynth = new float[FrameManifest.MaxTiles], _sorted = new float[FrameManifest.MaxTiles];
    private readonly int[] _tileHam = new int[FrameManifest.MaxTiles];
    private readonly ulong[] _hashScratch = new ulong[FrameManifest.MaxTiles];
    private readonly float[] _blockDc = new float[4096], _radar = new float[Axes];
    public readonly byte[] TamperMask = new byte[FrameManifest.MaxTiles];
    private int _tx, _ty;

    public DeepfakeProtectionContentAuthenticityGuard(NeuralInvisibleWatermarkEncoderEngine wm, byte[]? key = null)
    {
        _wm = wm;
        _key = key is { Length: >= 16 } ? (byte[])key.Clone() : RandomNumberGenerator.GetBytes(32);
    }

    public float HammingThreshold { get; set; } = 10f;
    public float AgreementDrop { get; set; } = 0.45f;

    // ── 결과 ──
    public AuthVerdict Verdict { get; private set; }
    public WatermarkDetection Detection { get; private set; }
    public float FrameAgreement { get; private set; }
    public float Blockiness { get; private set; }
    public float SyntheticScore { get; private set; }
    public int TamperedTiles { get; private set; }
    public int TilesX => _tx;
    public int TilesY => _ty;
    public float TileAgreement(int t) => _tileAgree[t];
    public int TileHamming(int t) => _tileHam[t];
    public float TileSynthetic(int t) => _tileSynth[t];
    public float Radar(ThreatAxis a) => _radar[(int)a];
    public long Verifications { get; private set; }
    public long Seals { get; private set; }
    public double LastVerifyMs { get; private set; }
    public double LastSealMs { get; private set; }

    public static string VerdictName(AuthVerdict v) => v switch
    {
        AuthVerdict.Recompressed => "재압축(정상 배포 열화)", AuthVerdict.Tampered => "국소 위변조(딥페이크 의심)", AuthVerdict.Unmarked => "워터마크 없음(무단 복제/합성)",
        AuthVerdict.SignatureInvalid => "매니페스트 서명 불일치", _ => "진본",
    };

    public static string AxisName(ThreatAxis a) => a switch
    {
        ThreatAxis.Recompression => "재압축", ThreatAxis.LocalTamper => "국소 변조", ThreatAxis.Synthetic => "합성 흔적", ThreatAxis.WatermarkLoss => "워터마크 손실",
        ThreatAxis.FingerprintDrift => "지문 이탈", _ => "서명",
    };

    private static float Luma(ReadOnlySpan<float> rgb, int i) => 0.299f * rgb[i] + 0.587f * rgb[i + 1] + 0.114f * rgb[i + 2];

    /// <summary>블록 DC 평균(블록 수 ≤ 4096) · 반환 = 프레임 평균 휘도.</summary>
    private float BlockMeans(ReadOnlySpan<float> rgb, int w, int bx, int by)
    {
        double total = 0;
        for (var j = 0; j < by; j++)
            for (var i = 0; i < bx; i++)
            {
                var s = 0f;
                for (var y = 0; y < 8; y++) { var row = ((j * 8 + y) * w + i * 8) * 3; for (var x = 0; x < 8; x++) s += Luma(rgb, row + x * 3); }
                _blockDc[j * bx + i] = s / 64f; total += s / 64f;
            }
        return (float)(total / Math.Max(1, bx * by));
    }

    /// <summary>타일 지각 지문: 블록 DC &gt; 타일 평균(12비트) · 수평/수직 1차 차분 부호(블록 쌍 · 최대 34비트) · 평균 휘도 4비트 양자화.</summary>
    private void TileHashes(int bx, int by, Span<ulong> dst)
    {
        for (var ty = 0; ty < _ty; ty++)
            for (var tx = 0; tx < _tx; tx++)
            {
                float mean = 0f;
                for (var j = 0; j < TileBy; j++) for (var i = 0; i < TileBx; i++) mean += _blockDc[(ty * TileBy + j) * bx + tx * TileBx + i];
                mean /= TileBx * TileBy;
                ulong h = 0; var bit = 0;
                for (var j = 0; j < TileBy; j++)
                    for (var i = 0; i < TileBx; i++)
                    {
                        var v = _blockDc[(ty * TileBy + j) * bx + tx * TileBx + i];
                        if (v > mean) h |= 1UL << bit; bit++;
                        if (i + 1 < TileBx) { if (_blockDc[(ty * TileBy + j) * bx + tx * TileBx + i + 1] > v + 0.004f) h |= 1UL << bit; bit++; }
                        if (j + 1 < TileBy) { if (_blockDc[(ty * TileBy + j + 1) * bx + tx * TileBx + i] > v + 0.004f) h |= 1UL << bit; bit++; }
                    }
                var q = (ulong)Math.Clamp((int)(mean * 15.99f), 0, 15);
                h |= (q ^ (q >> 1)) << 56; // 그레이 코드(인접 휘도 1비트 차)
                dst[ty * _tx + tx] = h;
            }
    }

    private static ulong GlobalHash(ReadOnlySpan<float> dc, int bx, int by)
    {
        Span<float> g = stackalloc float[64];
        for (var gy = 0; gy < 8; gy++)
            for (var gx = 0; gx < 8; gx++)
            {
                int x0 = gx * bx / 8, x1 = Math.Max(x0 + 1, (gx + 1) * bx / 8), y0 = gy * by / 8, y1 = Math.Max(y0 + 1, (gy + 1) * by / 8);
                float s = 0f; var n = 0;
                for (var y = y0; y < y1; y++) for (var x = x0; x < x1; x++) { s += dc[y * bx + x]; n++; }
                g[gy * 8 + gx] = s / n;
            }
        var m = 0f; for (var i = 0; i < 64; i++) m += g[i]; m /= 64f;
        ulong h = 0; for (var i = 0; i < 64; i++) if (g[i] > m) h |= 1UL << i;
        return h;
    }

    private int Canonical(FrameManifest m)
    {
        var s = _canon.AsSpan();
        "PFSv1690"u8.CopyTo(s);
        BinaryPrimitives.WriteInt64LittleEndian(s[8..], m.Frame);
        BinaryPrimitives.WriteUInt64LittleEndian(s[16..], m.PayloadId);
        BinaryPrimitives.WriteUInt64LittleEndian(s[24..], m.GlobalHash);
        BinaryPrimitives.WriteInt32LittleEndian(s[32..], m.Tiles);
        BinaryPrimitives.WriteInt32LittleEndian(s[36..], m.Blocks);
        BinaryPrimitives.WriteInt32LittleEndian(s[40..], m.Width);
        BinaryPrimitives.WriteInt32LittleEndian(s[44..], m.Height);
        BinaryPrimitives.WriteSingleLittleEndian(s[48..], m.MeanLuma);
        ulong xr = 0; for (var t = 0; t < m.Tiles; t++) xr ^= m.TileHash[t];
        BinaryPrimitives.WriteUInt64LittleEndian(s[52..], xr);   // 집계: 타일 지문 XOR
        BinaryPrimitives.WriteInt32LittleEndian(s[60..], m.Tiles * 64);
        for (var t = 0; t < m.Tiles; t++) BinaryPrimitives.WriteUInt64LittleEndian(s[(64 + t * 8)..], m.TileHash[t]);
        return 64 + m.Tiles * 8;
    }

    private void Geometry(int w, int h)
    {
        var bx = w / 8; var by = h / 8;
        _tx = Math.Max(1, bx / TileBx); _ty = Math.Max(1, by / TileBy);
        if (_tx * _ty > FrameManifest.MaxTiles) throw new ArgumentException("frame too large for tile manifest");
    }

    /// <summary>워터마크 내장 직후 봉인(무할당).</summary>
    public void Seal(ReadOnlySpan<float> rgb, int w, int h, long frame, FrameManifest dst)
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Geometry(w, h);
            int bx = w / 8, by = h / 8;
            dst.Frame = frame; dst.PayloadId = _wm.Payload.Id; dst.Width = w; dst.Height = h; dst.Blocks = bx * by; dst.Tiles = _tx * _ty;
            dst.MeanLuma = BlockMeans(rgb, w, bx, by);
            TileHashes(bx, by, dst.TileHash);
            dst.GlobalHash = GlobalHash(_blockDc, bx, by);
            var n = Canonical(dst);
            HMACSHA512.HashData(_key, _canon.AsSpan(0, n), dst.Signature);
            dst.Sealed = true;
            Seals++;
            LastSealMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
    }

    public bool VerifySignature(FrameManifest m)
    {
        lock (_gate)
        {
            if (!m.Sealed) return false;
            var n = Canonical(m);
            Span<byte> sig = stackalloc byte[64];
            HMACSHA512.HashData(_key, _canon.AsSpan(0, n), sig);
            return CryptographicOperations.FixedTimeEquals(sig, m.Signature);
        }
    }

    /// <summary>블록 경계 단차비: 경계(8k−1 ↔ 8k) 평균 |Δ| ÷ 내부 평균 |Δ|(재압축 ↑).</summary>
    private static float BlockinessOf(ReadOnlySpan<float> rgb, int w, int h)
    {
        double eb = 0, ei = 0; long nb = 0, ni = 0;
        for (var y = 0; y < h; y += 2)
            for (var x = 1; x < w; x++)
            {
                var d = MathF.Abs(Luma(rgb, (y * w + x) * 3) - Luma(rgb, (y * w + x - 1) * 3));
                if ((x & 7) == 0) { eb += d; nb++; } else { ei += d; ni++; }
            }
        return ni > 0 && ei > 1e-9 ? (float)((eb / nb) / (ei / ni)) : 1f;
    }

    /// <summary>타일 합성 흔적: |Σ(−1)^{x+y}Y| + 2×2 계단(최근접 업샘플) 에너지 비.</summary>
    private float SyntheticOf(ReadOnlySpan<float> rgb, int w, int x0, int y0, int tw, int th)
    {
        double cb = 0, e1 = 0, e2 = 0;
        for (var y = y0; y < y0 + th; y++)
            for (var x = x0; x < x0 + tw; x++)
            {
                var v = Luma(rgb, (y * w + x) * 3);
                cb += ((x + y) & 1) == 0 ? v : -v;
                if (x + 1 < x0 + tw)
                {
                    var d = MathF.Abs(Luma(rgb, (y * w + x + 1) * 3) - v);
                    if ((x & 1) == 0) e1 += d; else e2 += d; // 짝 쌍 내부 vs 쌍 경계
                }
            }
        var n = tw * th;
        var checker = (float)(Math.Abs(cb) / n) * 40f;
        var stair = e2 > 1e-9 ? (float)Math.Clamp(1.0 - e1 / e2, 0.0, 1.0) : 0f;
        return Math.Clamp(MathF.Max(checker, stair), 0f, 1f);
    }

    /// <summary>프레임 검증(무할당): 서명 → 워터마크 → 타일 일치율/지문/합성 → 판정 · 레이더 · 오버레이 마스크.</summary>
    public AuthVerdict Verify(ReadOnlySpan<float> rgb, int w, int h, FrameManifest manifest)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var sigOk = VerifySignature(manifest);
        var expected = WatermarkPayload.FromId(manifest.PayloadId);
        var det = _wm.DetectFrame(rgb, w, h, expected);
        var slots = _wm.SlotCount;
        _wm.SlotSoft(rgb, w, h, _slotSoft.AsSpan(0, Math.Min(slots, _slotSoft.Length)));
        lock (_gate)
        {
            Geometry(w, h);
            int bx = w / 8, by = h / 8, tiles = _tx * _ty;
            Detection = det;
            // 기대 부호(봉인 페이로드) 기준 슬롯 일치(soft · 부호) → 타일 평균
            Array.Clear(_tileAgree, 0, tiles);
            double fa = 0;
            // 봉인 페이로드 80비트 부호(ID 64 + CRC-16) — SlotSoft 는 PN 디백색화 완료값
            Span<sbyte> want = stackalloc sbyte[NeuralInvisibleWatermarkEncoderEngine.PayloadBits];
            Span<byte> idb = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(idb, manifest.PayloadId);
            var crc = NeuralInvisibleWatermarkEncoderEngine.Crc16(idb);
            for (var i = 0; i < 64; i++) want[i] = (sbyte)(((manifest.PayloadId >> (63 - i)) & 1) == 1 ? 1 : -1);
            for (var i = 0; i < 16; i++) want[64 + i] = (sbyte)(((crc >> (15 - i)) & 1) == 1 ? 1 : -1);
            for (var ty = 0; ty < _ty; ty++)
                for (var tx = 0; tx < _tx; tx++)
                {
                    float a = 0f; var n = 0;
                    for (var j = 0; j < TileBy; j++)
                        for (var i = 0; i < TileBx; i++)
                        {
                            var b = (ty * TileBy + j) * bx + tx * TileBx + i;
                            for (var k = 0; k < 2; k++) { var s = b * 2 + k; if (s >= slots) continue; a += _slotSoft[s] * want[_wm.SlotBit(s)]; n++; }
                        }
                    _tileAgree[ty * _tx + tx] = n > 0 ? a / n : 0f;
                    fa += _tileAgree[ty * _tx + tx];
                }
            FrameAgreement = (float)(fa / tiles);
            BlockMeans(rgb, w, bx, by);
            TileHashes(bx, by, _hashScratch);
            var gh = GlobalHash(_blockDc, bx, by);
            var gHam = BitOperations.PopCount(gh ^ manifest.GlobalHash);
            float hamSum = 0f, synMax = 0f;
            for (var t = 0; t < tiles; t++)
            {
                _tileHam[t] = BitOperations.PopCount(_hashScratch[t] ^ manifest.TileHash[t]);
                hamSum += _tileHam[t];
                var tx = t % _tx; var ty = t / _tx;
                _tileSynth[t] = SyntheticOf(rgb, w, tx * TileBx * 8, ty * TileBy * 8, TileBx * 8, TileBy * 8);
                synMax = MathF.Max(synMax, _tileSynth[t]);
            }
            // 중앙값 일치율(견고 기준)
            _tileAgree.AsSpan(0, tiles).CopyTo(_sorted); _sorted.AsSpan(0, tiles).Sort();
            var med = _sorted[tiles / 2];
            var tampered = 0;
            for (var t = 0; t < tiles; t++)
            {
                var drop = med - _tileAgree[t];
                // 재압축은 평탄/암부 타일에서 일치율 하락 + 소폭 지문 변화(≤ 6) → 지문 0.8·T 이상 동반 시에만 변조
                var bad = (drop > AgreementDrop && _tileHam[t] >= HammingThreshold * 0.8f) || (_tileHam[t] >= HammingThreshold * 1.2f && drop > AgreementDrop * 0.45f) || (_tileSynth[t] > 0.6f && drop > AgreementDrop * 0.45f);
                TamperMask[t] = (byte)(bad ? 1 : 0);
                if (bad) tampered++;
            }
            TamperedTiles = tampered;
            Blockiness = BlockinessOf(rgb, w, h);
            SyntheticScore = synMax;
            var meanHam = hamSum / tiles;
            AuthVerdict v;
            if (!sigOk) v = AuthVerdict.SignatureInvalid;
            else if (med < 0.15f && gHam > 12) v = AuthVerdict.Unmarked;
            else if (tampered > 0 && tampered <= tiles / 2) v = AuthVerdict.Tampered;
            else if (med < 0.15f) v = AuthVerdict.Unmarked;
            else if (med < 0.85f || Blockiness > 1.25f) v = AuthVerdict.Recompressed;
            else v = AuthVerdict.Authentic;
            Verdict = v;
            _radar[(int)ThreatAxis.Recompression] = Math.Clamp((1f - med) * 1.2f + MathF.Max(0f, Blockiness - 1f), 0f, 1f);
            _radar[(int)ThreatAxis.LocalTamper] = Math.Clamp(tampered / MathF.Max(1f, tiles * 0.15f), 0f, 1f);
            _radar[(int)ThreatAxis.Synthetic] = synMax;
            _radar[(int)ThreatAxis.WatermarkLoss] = Math.Clamp(1f - det.Confidence, 0f, 1f);
            _radar[(int)ThreatAxis.FingerprintDrift] = Math.Clamp(meanHam / 20f + gHam / 64f, 0f, 1f);
            _radar[(int)ThreatAxis.Signature] = sigOk ? 0f : 1f;
            Verifications++;
            LastVerifyMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            return v;
        }
    }

    /// <summary>위변조 오버레이(제자리 · 무할당): 변조 타일 적색 블렌드 + 테두리.</summary>
    public void Overlay(Span<float> rgb, int w, int h)
    {
        lock (_gate)
        {
            for (var t = 0; t < _tx * _ty; t++)
            {
                if (TamperMask[t] == 0) continue;
                int x0 = (t % _tx) * TileBx * 8, y0 = (t / _tx) * TileBy * 8;
                for (var y = y0; y < Math.Min(h, y0 + TileBy * 8); y++)
                    for (var x = x0; x < Math.Min(w, x0 + TileBx * 8); x++)
                    {
                        var i = (y * w + x) * 3;
                        var edge = x == x0 || y == y0 || x == x0 + TileBx * 8 - 1 || y == y0 + TileBy * 8 - 1;
                        var a = edge ? 1f : 0.35f;
                        rgb[i] += (1f - rgb[i]) * a; rgb[i + 1] *= 1f - a * 0.8f; rgb[i + 2] *= 1f - a * 0.8f;
                    }
            }
        }
    }
}
