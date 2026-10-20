using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PromptFlowStudio.Services.Sound;

/// <summary>결정적 xorshift32 난수(값형 · 무할당).</summary>
public struct XorShift32
{
    private uint _s;

    public XorShift32(int seed)
    {
        _s = unchecked((uint)seed * 2654435761u ^ 0x9E3779B9u);
        if (_s == 0) _s = 1;
    }

    /// <summary>-1~1.</summary>
    public float Next()
    {
        var x = _s;
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;
        _s = x;
        return (x & 0xFFFFFF) / (float)0x7FFFFF - 1f;
    }

    public float Next01() => (Next() + 1f) * 0.5f;
}

/// <summary>RBJ 쿡북 바이쿼드(Direct Form II Transposed · 상태 포함 값형 — 지역 변수로 보유해 호출).</summary>
public struct Biquad
{
    public float B0, B1, B2, A1, A2;
    private float _z1, _z2;

    public float Process(float x)
    {
        var y = B0 * x + _z1;
        _z1 = B1 * x - A1 * y + _z2;
        _z2 = B2 * x - A2 * y;
        return y;
    }

    public void Reset() { _z1 = 0; _z2 = 0; }

    /// <summary>계수 교체(스윕) 시 이전 필터 상태 승계 — 클릭 노이즈 억제.</summary>
    public void CopyStateFrom(in Biquad other) { _z1 = other._z1; _z2 = other._z2; }

    private static Biquad Make(double b0, double b1, double b2, double a0, double a1, double a2)
        => new() { B0 = (float)(b0 / a0), B1 = (float)(b1 / a0), B2 = (float)(b2 / a0), A1 = (float)(a1 / a0), A2 = (float)(a2 / a0) };

    private static (double Cos, double Alpha) W(double fc, double q, int rate)
    {
        var w = 2 * Math.PI * Math.Clamp(fc, 1, rate * 0.49) / rate;
        return (Math.Cos(w), Math.Sin(w) / (2 * Math.Max(1e-3, q)));
    }

    public static Biquad LowPass(double fc, double q, int rate) { var (c, a) = W(fc, q, rate); return Make((1 - c) / 2, 1 - c, (1 - c) / 2, 1 + a, -2 * c, 1 - a); }
    public static Biquad HighPass(double fc, double q, int rate) { var (c, a) = W(fc, q, rate); return Make((1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + a, -2 * c, 1 - a); }
    public static Biquad BandPass(double fc, double q, int rate) { var (c, a) = W(fc, q, rate); return Make(a, 0, -a, 1 + a, -2 * c, 1 - a); }

    public static Biquad Peaking(double fc, double q, double db, int rate)
    {
        var (c, a) = W(fc, q, rate);
        var A = Math.Pow(10, db / 40);
        return Make(1 + a * A, -2 * c, 1 - a * A, 1 + a / A, -2 * c, 1 - a / A);
    }

    public static Biquad LowShelf(double fc, double q, double db, int rate)
    {
        var (c, a) = W(fc, q, rate);
        var A = Math.Pow(10, db / 40); var s = 2 * Math.Sqrt(A) * a;
        return Make(A * ((A + 1) - (A - 1) * c + s), 2 * A * ((A - 1) - (A + 1) * c), A * ((A + 1) - (A - 1) * c - s),
            (A + 1) + (A - 1) * c + s, -2 * ((A - 1) + (A + 1) * c), (A + 1) + (A - 1) * c - s);
    }

    public static Biquad HighShelf(double fc, double q, double db, int rate)
    {
        var (c, a) = W(fc, q, rate);
        var A = Math.Pow(10, db / 40); var s = 2 * Math.Sqrt(A) * a;
        return Make(A * ((A + 1) + (A - 1) * c + s), -2 * A * ((A - 1) + (A + 1) * c), A * ((A + 1) + (A - 1) * c - s),
            (A + 1) - (A - 1) * c + s, 2 * ((A - 1) - (A + 1) * c), (A + 1) - (A - 1) * c - s);
    }
}

/// <summary>radix-2 복소 FFT 플랜(트위들 테이블 1회 · 변환 무할당).</summary>
public sealed class FftPlan
{
    private readonly float[] _cos, _sin;

    public FftPlan(int n)
    {
        if (n < 2 || (n & (n - 1)) != 0) throw new ArgumentException("2의 거듭제곱 길이만 지원");
        N = n;
        _cos = new float[n / 2]; _sin = new float[n / 2];
        for (var k = 0; k < n / 2; k++) { _cos[k] = (float)Math.Cos(2 * Math.PI * k / n); _sin[k] = (float)Math.Sin(2 * Math.PI * k / n); }
    }

    public int N { get; }

    public void Transform(Span<float> re, Span<float> im, bool inverse)
    {
        var n = N;
        if (re.Length < n || im.Length < n) throw new ArgumentException("버퍼 크기 불일치");
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        var sign = inverse ? 1f : -1f;
        for (var len = 2; len <= n; len <<= 1)
        {
            var half = len >> 1;
            var step = n / len;
            for (var i = 0; i < n; i += len)
            {
                for (var k = 0; k < half; k++)
                {
                    float wr = _cos[k * step], wi = sign * _sin[k * step];
                    int a = i + k, b = a + half;
                    var xr = re[b] * wr - im[b] * wi;
                    var xi = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - xr; im[b] = im[a] - xi;
                    re[a] += xr; im[a] += xi;
                }
            }
        }
        if (inverse)
        {
            var inv = 1f / n;
            for (var i = 0; i < n; i++) { re[i] *= inv; im[i] *= inv; }
        }
    }
}

/// <summary>오디오 공용 유틸 — 라우드니스(BS.1770-4) · 리미터 · 팬 · WAV 입출력(float32 · EXTENSIBLE · 추가 청크) · 파형 피크.</summary>
public static class SoundDsp
{
    public static float DbToGain(float db) => MathF.Pow(10f, db / 20f);
    public static float GainToDb(float g) => 20f * MathF.Log10(MathF.Max(g, 1e-9f));

    /// <summary>등전력 팬(-1 좌 ~ 1 우).</summary>
    public static (float L, float R) Pan(float pan)
    {
        var a = (Math.Clamp(pan, -1f, 1f) + 1f) * (MathF.PI / 4f);
        return (MathF.Cos(a), MathF.Sin(a));
    }

    public static float Smoothstep(float e0, float e1, float x)
    {
        var t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3 - 2 * t);
    }

    /// <summary>BS.1770-4 통합 라우드니스(LUFS · K-가중 · 400ms 블록 75% 겹침 · 절대 -70 / 상대 -10 게이트). 무음 -70.</summary>
    public static double MeasureLufs(ReadOnlySpan<float> l, ReadOnlySpan<float> r, int rate)
    {
        var n = Math.Min(l.Length, r.Length);
        var step = rate / 10;
        if (n < step * 4 || rate <= 0) return -70;
        var segs = n / step;
        var seg = new double[segs];
        var shL = Biquad.HighShelf(1681.974450955533, 0.7071752369554196, 3.999843853973347, rate);
        var hpL = Biquad.HighPass(38.13547087602444, 0.5003270373238773, rate);
        var shR = shL; var hpR = hpL;
        for (var s = 0; s < segs; s++)
        {
            double acc = 0;
            var end = (s + 1) * step;
            for (var i = s * step; i < end; i++)
            {
                var a = hpL.Process(shL.Process(l[i]));
                var b = hpR.Process(shR.Process(r[i]));
                acc += (double)a * a + (double)b * b;
            }
            seg[s] = acc;
        }
        var blocks = segs - 3;
        var z = new double[blocks];
        double sumAbs = 0; var nAbs = 0;
        for (var j = 0; j < blocks; j++)
        {
            z[j] = (seg[j] + seg[j + 1] + seg[j + 2] + seg[j + 3]) / (4.0 * step);
            if (-0.691 + 10 * Math.Log10(z[j] + 1e-20) > -70) { sumAbs += z[j]; nAbs++; }
        }
        if (nAbs == 0) return -70;
        var rel = -0.691 + 10 * Math.Log10(sumAbs / nAbs) - 10;
        double sum = 0; var cnt = 0;
        for (var j = 0; j < blocks; j++)
        {
            var lj = -0.691 + 10 * Math.Log10(z[j] + 1e-20);
            if (lj > -70 && lj > rel) { sum += z[j]; cnt++; }
        }
        return cnt == 0 ? -70 : Math.Max(-70, -0.691 + 10 * Math.Log10(sum / cnt));
    }

    /// <summary>스테레오 링크 피크 리미터(즉시 어택 · 릴리즈 ms) → 출력 피크(dBFS). 무할당.</summary>
    public static float Limit(Span<float> l, Span<float> r, int rate, float ceilingDb, float releaseMs = 80)
    {
        var ceil = DbToGain(ceilingDb);
        var rel = MathF.Exp(-1f / (MathF.Max(1f, releaseMs) * 0.001f * rate));
        var env = 0f; var peak = 0f;
        var n = Math.Min(l.Length, r.Length);
        for (var i = 0; i < n; i++)
        {
            var a = MathF.Max(MathF.Abs(l[i]), MathF.Abs(r[i]));
            env = a > env ? a : env * rel + a * (1 - rel);
            var g = env > ceil ? ceil / env : 1f;
            l[i] = Math.Clamp(l[i] * g, -ceil, ceil); r[i] = Math.Clamp(r[i] * g, -ceil, ceil);
            peak = MathF.Max(peak, MathF.Max(MathF.Abs(l[i]), MathF.Abs(r[i])));
        }
        return GainToDb(peak);
    }

    public static float PeakDb(ReadOnlySpan<float> x)
    {
        var p = 0f;
        for (var i = 0; i < x.Length; i++) p = MathF.Max(p, MathF.Abs(x[i]));
        return GainToDb(p);
    }

    public static double Energy(ReadOnlySpan<float> x)
    {
        double e = 0;
        for (var i = 0; i < x.Length; i++) e += (double)x[i] * x[i];
        return e;
    }

    /// <summary>파형 피크(버킷별 |x| 최대) — UI 타임라인.</summary>
    public static float[] Peaks(ReadOnlySpan<float> x, int buckets)
    {
        buckets = Math.Clamp(buckets, 1, 4096);
        var p = new float[buckets];
        if (x.IsEmpty) return p;
        for (var b = 0; b < buckets; b++)
        {
            int s = (int)((long)x.Length * b / buckets), e = (int)((long)x.Length * (b + 1) / buckets);
            var m = 0f;
            for (var i = s; i < e; i++) m = MathF.Max(m, MathF.Abs(x[i]));
            p[b] = m;
        }
        return p;
    }

    /// <summary>선형 보간 리샘플(오프라인).</summary>
    public static float[] Resample(ReadOnlySpan<float> x, int srcRate, int dstRate)
    {
        if (srcRate == dstRate || x.IsEmpty) return x.ToArray();
        var n = (int)((long)x.Length * dstRate / srcRate);
        var y = new float[n];
        var ratio = (double)srcRate / dstRate;
        for (var i = 0; i < n; i++)
        {
            var pos = i * ratio; var k = (int)pos; var f = (float)(pos - k);
            y[i] = k + 1 < x.Length ? x[k] + (x[k + 1] - x[k]) * f : x[Math.Min(k, x.Length - 1)];
        }
        return y;
    }

    // ══════════════════════ WAV ══════════════════════

    private static readonly byte[] FloatSubFormat = { 0x03, 0, 0, 0, 0, 0, 0x10, 0, 0x80, 0, 0, 0xAA, 0, 0x38, 0x9B, 0x71 };

    /// <summary>float32 WAV 헤더(fmt[EXTENSIBLE] + 추가 청크 + data 헤더) — 데이터는 호출자가 이어서 기록.</summary>
    public static void WriteWavHeader(Stream s, int channels, int rate, long frames, uint channelMask, IReadOnlyList<(string Id, byte[] Data)>? chunks = null)
    {
        var ext = channels > 2 || channelMask != 0;
        var fmtLen = ext ? 40 : 16;
        var dataBytes = frames * channels * 4;
        long extra = 0;
        if (chunks is not null) foreach (var c in chunks) extra += 8 + c.Data.Length + (c.Data.Length & 1);
        var riff = 4 + 8 + fmtLen + extra + 8 + dataBytes;
        if (riff > uint.MaxValue) throw new InvalidOperationException("WAV 4GB 초과");
        Span<byte> h = stackalloc byte[60];
        "RIFF"u8.CopyTo(h); BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(4), (uint)riff); "WAVE"u8.CopyTo(h.Slice(8));
        "fmt "u8.CopyTo(h.Slice(12)); BinaryPrimitives.WriteInt32LittleEndian(h.Slice(16), fmtLen);
        BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(20), ext ? (ushort)0xFFFE : (ushort)3);
        BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(22), (ushort)channels);
        BinaryPrimitives.WriteInt32LittleEndian(h.Slice(24), rate);
        BinaryPrimitives.WriteInt32LittleEndian(h.Slice(28), rate * channels * 4);
        BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(32), (ushort)(channels * 4));
        BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(34), 32);
        var len = 36;
        if (ext)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(36), 22);
            BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(38), 32);
            BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(40), channelMask);
            FloatSubFormat.CopyTo(h.Slice(44));
            len = 60;
        }
        s.Write(h.Slice(0, len));
        if (chunks is not null)
        {
            Span<byte> ch = stackalloc byte[8];
            foreach (var c in chunks)
            {
                Encoding.ASCII.GetBytes(c.Id.AsSpan(0, 4), ch);
                BinaryPrimitives.WriteInt32LittleEndian(ch.Slice(4), c.Data.Length);
                s.Write(ch);
                s.Write(c.Data);
                if ((c.Data.Length & 1) != 0) s.WriteByte(0);
            }
        }
        Span<byte> d = stackalloc byte[8];
        "data"u8.CopyTo(d); BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(4), (uint)dataBytes);
        s.Write(d);
    }

    public static void WriteFloats(Stream s, ReadOnlySpan<float> interleaved) => s.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(interleaved));

    /// <summary>스테레오 float32 WAV(원자 교체).</summary>
    public static void WriteStereoWav(string path, ReadOnlySpan<float> l, ReadOnlySpan<float> r, int rate)
    {
        var n = Math.Min(l.Length, r.Length);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            WriteWavHeader(fs, 2, rate, n, 0);
            Span<float> blk = stackalloc float[512];
            for (var i = 0; i < n; i += 256)
            {
                var m = Math.Min(256, n - i);
                for (var k = 0; k < m; k++) { blk[2 * k] = l[i + k]; blk[2 * k + 1] = r[i + k]; }
                WriteFloats(fs, blk.Slice(0, m * 2));
            }
        }
        File.Move(tmp, path, true);
    }

    /// <summary>WAV(PCM 16/24/32 · float32 · EXTENSIBLE) → 좌/우(모노는 복제). 실패 false.</summary>
    public static bool TryReadWav(ReadOnlySpan<byte> wav, out float[] left, out float[] right, out int rate, out int channels)
    {
        left = right = Array.Empty<float>(); rate = 0; channels = 0;
        if (wav.Length < 12 || !wav.Slice(0, 4).SequenceEqual("RIFF"u8) || !wav.Slice(8, 4).SequenceEqual("WAVE"u8)) return false;
        int fmt = 0, bits = 0, pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var id = wav.Slice(pos, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(wav.Slice(pos + 4));
            if (size < 0 || pos + 8 + (long)size > wav.Length) size = wav.Length - pos - 8;
            var body = wav.Slice(pos + 8, size);
            if (id.SequenceEqual("fmt "u8) && size >= 16)
            {
                fmt = BinaryPrimitives.ReadUInt16LittleEndian(body);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(14));
                if (fmt == 0xFFFE && size >= 26) fmt = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(24));
            }
            else if (id.SequenceEqual("data"u8) && channels > 0)
            {
                var bps = bits / 8;
                if (bps is < 2 or > 4 || (fmt == 3 && bits != 32) || (fmt != 1 && fmt != 3)) return false;
                var frames = size / (bps * channels);
                left = new float[frames]; right = channels > 1 ? new float[frames] : left;
                for (var f = 0; f < frames; f++)
                {
                    var o = f * bps * channels;
                    left[f] = Sample(body.Slice(o), fmt, bits);
                    if (channels > 1) right[f] = Sample(body.Slice(o + bps), fmt, bits);
                }
                return true;
            }
            pos += 8 + size + (size & 1);
        }
        return false;
    }

    private static float Sample(ReadOnlySpan<byte> b, int fmt, int bits) => (fmt, bits) switch
    {
        (3, 32) => BinaryPrimitives.ReadSingleLittleEndian(b),
        (1, 16) => BinaryPrimitives.ReadInt16LittleEndian(b) / 32768f,
        (1, 24) => ((b[0] | (b[1] << 8) | (b[2] << 16)) << 8 >> 8) / 8388608f,
        (1, 32) => BinaryPrimitives.ReadInt32LittleEndian(b) / 2147483648f,
        _ => 0f,
    };
}
