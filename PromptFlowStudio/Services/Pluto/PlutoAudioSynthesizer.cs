using System;
using System.Buffers.Binary;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>
/// 🔔 플루토 절차적 SFX 합성기 — 외부 에셋 0 · 48kHz/16-bit/스테레오 PCM WAV(표준 44바이트 RIFF 헤더).
/// ADSR 엔벨로프 + 사인 배음 + 1-pole 저역 통과 노이즈 버스트. 결정론(고정 시드) · -1 dBFS 피크 정규화.
/// </summary>
public static class PlutoAudioSynthesizer
{
    public const int SampleRate = 48000;
    public const short Channels = 2;
    public const short BitsPerSample = 16;
    public const int WavHeaderBytes = 44;
    /// <summary>피크 정규화 목표(-1 dBFS) — 큐 볼륨·amix 합산 전 클리핑 여유.</summary>
    public const float PeakTarget = 0.891f;

    public const int ShutterClickMs = 150, ChimeSparkleMs = 800, BedtimeToneMs = 1200;

    public static int DurationMs(PlutoSfxType t) => t switch
    {
        PlutoSfxType.ShutterClick => ShutterClickMs,
        PlutoSfxType.ChimeSparkle => ChimeSparkleMs,
        _ => BedtimeToneMs,
    };

    public static byte[] Generate(PlutoSfxType t) => t switch
    {
        PlutoSfxType.ShutterClick => GenerateShutterClickWav(),
        PlutoSfxType.ChimeSparkle => GenerateChimeSparkleWav(),
        _ => GenerateBedtimeToneWav(),
    };

    /// <summary>📸 ~0.15초 — 포커스 틱(2.4kHz 핑 + 밝은 노이즈) → 55ms 후 셔터(저역 통과 노이즈 + 170Hz 썸프).</summary>
    public static byte[] GenerateShutterClickWav()
    {
        var n = Samples(ShutterClickMs);
        var l = new float[n];
        var r = new float[n];
        uint seed = 0x5EED_C11Cu;
        float lpBright = 0, lpBody = 0;
        var shutterAt = Samples(55);
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)SampleRate;
            var noise = NextNoise(ref seed);
            // 포커스 틱 — 4ms 밝은 노이즈 + 감쇠 핑.
            lpBright += 0.55f * (noise - lpBright);
            var focus = (float)(Adsr(t, 0.0005, 0.004, 0.0, 0.001, 0.006) * (noise - lpBright) * 0.6
                              + Math.Exp(-t / 0.006) * Math.Sin(2 * Math.PI * 2400 * t) * 0.35);
            var s = focus;
            if (i >= shutterAt)
            {
                var ts = (i - shutterAt) / (double)SampleRate;
                lpBody += 0.18f * (noise - lpBody);   // 부드러운 저역 통과 노이즈(기계식 셔터 몸체)
                var env = Adsr(ts, 0.001, 0.02, 0.15, 0.05, 0.095);
                s += (float)(env * lpBody * 1.6 + Math.Exp(-ts / 0.018) * Math.Sin(2 * Math.PI * 170 * ts) * 0.45);
            }
            l[i] = s;
            r[i] = s * 0.92f + lpBright * 0.04f;   // 미세 스테레오 폭
        }
        return Finish(l, r);
    }

    /// <summary>✨ ~0.8초 — 오르골 차임 아르페지오(E6 · G#6 · B6 · E7) · 비조화 배음 · 좌우 패닝.</summary>
    public static byte[] GenerateChimeSparkleWav()
    {
        var n = Samples(ChimeSparkleMs);
        var l = new float[n];
        var r = new float[n];
        ReadOnlySpan<double> freq = stackalloc double[] { 1318.51, 1661.22, 1975.53, 2637.02 };
        ReadOnlySpan<double> start = stackalloc double[] { 0.0, 0.07, 0.14, 0.22 };
        ReadOnlySpan<double> pan = stackalloc double[] { 0.35, 0.65, 0.45, 0.58 };
        var total = ChimeSparkleMs / 1000.0;
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)SampleRate;
            double sl = 0, sr = 0;
            for (var k = 0; k < freq.Length; k++)
            {
                var tk = t - start[k];
                if (tk < 0) continue;
                var w = 2 * Math.PI * freq[k] * tk;
                // 오르골 빗살 — 기음 + 비조화 배음(×2.76, ×5.40) 빠른 감쇠.
                var v = Math.Sin(w) * Math.Exp(-tk / 0.28)
                      + 0.32 * Math.Sin(w * 2.76) * Math.Exp(-tk / 0.09)
                      + 0.12 * Math.Sin(w * 5.40) * Math.Exp(-tk / 0.04);
                v *= Adsr(tk, 0.002, 0.05, 0.85, 0.0, total) * (k == freq.Length - 1 ? 0.6 : 1.0);
                sl += v * (1 - pan[k]);
                sr += v * pan[k];
            }
            var rel = Release(t, total, 0.08);
            l[i] = (float)(sl * rel);
            r[i] = (float)(sr * rel);
        }
        return Finish(l, r);
    }

    /// <summary>🌙 ~1.2초 — 따뜻한 잠자리 벨(C5 · 서브 옥타브 · 2/3배음) · 좌우 0.7Hz 디튠 코러스 · 긴 릴리스.</summary>
    public static byte[] GenerateBedtimeToneWav()
    {
        var n = Samples(BedtimeToneMs);
        var l = new float[n];
        var r = new float[n];
        const double f0 = 523.25;
        var total = BedtimeToneMs / 1000.0;
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)SampleRate;
            var env = Adsr(t, 0.015, 0.35, 0.45, 0.25, total);
            var decay = Math.Exp(-t / 0.75);
            l[i] = (float)(env * decay * Bell(t, f0 - 0.35));
            r[i] = (float)(env * decay * Bell(t, f0 + 0.35));
        }
        return Finish(l, r);

        static double Bell(double t, double f)
        {
            var w = 2 * Math.PI * f * t;
            return Math.Sin(w) + 0.28 * Math.Sin(w * 0.5) + 0.38 * Math.Sin(w * 2.0) * Math.Exp(-t / 0.4) + 0.14 * Math.Sin(w * 3.01) * Math.Exp(-t / 0.22);
        }
    }

    /// <summary>
    /// 📈 ADSR 엔벨로프(초) — attack 선형 상승 → decay 지수 하강(sustain 수렴) → 끝에서 <paramref name="release"/> 동안 선형 0.
    /// </summary>
    public static double Adsr(double t, double attack, double decay, double sustain, double release, double total)
    {
        if (t < 0 || t >= total) return 0;
        double v;
        if (t < attack) v = attack <= 0 ? 1 : t / attack;
        else
        {
            var td = t - attack;
            v = decay <= 0 ? sustain : sustain + (1 - sustain) * Math.Exp(-td / decay);
        }
        return v * Release(t, total, release);
    }

    private static double Release(double t, double total, double release)
    {
        if (release <= 0) return 1;
        var rs = total - release;
        return t <= rs ? 1 : Math.Max(0, (total - t) / release);
    }

    private static int Samples(int ms) => (int)((long)SampleRate * ms / 1000);

    /// <summary>xorshift32 백색 노이즈(−1~1) — 결정론.</summary>
    private static float NextNoise(ref uint s)
    {
        s ^= s << 13; s ^= s >> 17; s ^= s << 5;
        return (s & 0xFFFFFF) / 8388607.5f - 1f;
    }

    /// <summary>DC 제거 → 피크 −1 dBFS 정규화 → 끝 2ms 클릭 방지 페이드 → PCM16 WAV.</summary>
    private static byte[] Finish(float[] l, float[] r)
    {
        var n = l.Length;
        double meanL = 0, meanR = 0;
        for (var i = 0; i < n; i++) { meanL += l[i]; meanR += r[i]; }
        meanL /= n; meanR /= n;
        float peak = 0;
        for (var i = 0; i < n; i++)
        {
            l[i] -= (float)meanL; r[i] -= (float)meanR;
            var a = Math.Max(Math.Abs(l[i]), Math.Abs(r[i]));
            if (a > peak) peak = a;
        }
        var gain = peak > 1e-6f ? PeakTarget / peak : 0f;
        var tail = Math.Min(n, Samples(2));
        for (var i = 0; i < n; i++)
        {
            var g = gain;
            if (i >= n - tail) g *= (n - i) / (float)tail;
            l[i] *= g; r[i] *= g;
        }
        return EncodePcm16Wav(l, r);
    }

    /// <summary>스테레오 float(−1~1) → 표준 44바이트 RIFF/WAVE PCM16 바이트.</summary>
    public static byte[] EncodePcm16Wav(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        var n = Math.Min(left.Length, right.Length);
        var dataBytes = n * Channels * (BitsPerSample / 8);
        var buf = new byte[WavHeaderBytes + dataBytes];
        var s = buf.AsSpan();
        "RIFF"u8.CopyTo(s);
        BinaryPrimitives.WriteInt32LittleEndian(s.Slice(4), 36 + dataBytes);
        "WAVE"u8.CopyTo(s.Slice(8));
        "fmt "u8.CopyTo(s.Slice(12));
        BinaryPrimitives.WriteInt32LittleEndian(s.Slice(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(s.Slice(20), 1);   // PCM
        BinaryPrimitives.WriteInt16LittleEndian(s.Slice(22), Channels);
        BinaryPrimitives.WriteInt32LittleEndian(s.Slice(24), SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(s.Slice(28), SampleRate * Channels * (BitsPerSample / 8));
        BinaryPrimitives.WriteInt16LittleEndian(s.Slice(32), (short)(Channels * (BitsPerSample / 8)));
        BinaryPrimitives.WriteInt16LittleEndian(s.Slice(34), BitsPerSample);
        "data"u8.CopyTo(s.Slice(36));
        BinaryPrimitives.WriteInt32LittleEndian(s.Slice(40), dataBytes);
        var p = WavHeaderBytes;
        for (var i = 0; i < n; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(s.Slice(p), ToPcm16(left[i])); p += 2;
            BinaryPrimitives.WriteInt16LittleEndian(s.Slice(p), ToPcm16(right[i])); p += 2;
        }
        return buf;
    }

    private static short ToPcm16(float v)
    {
        if (float.IsNaN(v)) return 0;
        var c = Math.Clamp(v, -1f, 1f);
        return (short)Math.Round(c * 32767f);
    }
}
