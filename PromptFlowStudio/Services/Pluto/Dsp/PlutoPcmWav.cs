using System;
using System.Buffers.Binary;

namespace PromptFlowStudio.Services.Pluto.Dsp;

/// <summary>🎚️ WAV 헤더 메타(RIFF/WAVE · PCM 16/24/32 · float32 · EXTENSIBLE).</summary>
public readonly record struct PlutoPcmWavInfo(int Format, int Channels, int SampleRate, int BitsPerSample, int DataOffset, int DataLength)
{
    public int BytesPerSample => BitsPerSample / 8;
    public int BlockAlign => BytesPerSample * Channels;
    /// <summary>채널당 샘플(프레임) 수.</summary>
    public int Frames => BlockAlign <= 0 ? 0 : DataLength / BlockAlign;
}

/// <summary>
/// 🎚️ 플루토 DSP 전용 무할당 WAV 코덱 — 헤더 파싱 후 호출자 버퍼(<c>ArrayPool</c> 대여분)로 채널 디코드 · PCM16 인코드.
/// 비정상 청크 크기는 파일 끝으로 클램프(잘린 WAV 구제).
/// </summary>
public static class PlutoPcmWav
{
    public const int FormatPcm = 1, FormatFloat = 3, FormatExtensible = 0xFFFE;
    public const int HeaderBytes = 44;

    /// <summary>RIFF/WAVE 헤더 파싱 — 지원 포맷(PCM 16/24/32 · float32 · 1~8ch · 8k~384kHz)만 true.</summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> wav, out PlutoPcmWavInfo info)
    {
        info = default;
        if (wav.Length < 12 || !wav.Slice(0, 4).SequenceEqual("RIFF"u8) || !wav.Slice(8, 4).SequenceEqual("WAVE"u8)) return false;
        int fmt = 0, channels = 0, rate = 0, bits = 0, pos = 12;
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
                if (fmt == FormatExtensible && size >= 26) fmt = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(24));
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (channels is < 1 or > 8 || rate is < 8000 or > 384000) return false;
                var supported = (fmt == FormatPcm && bits is 16 or 24 or 32) || (fmt == FormatFloat && bits == 32);
                if (!supported) return false;
                info = new PlutoPcmWavInfo(fmt, channels, rate, bits, pos + 8, size);
                return info.Frames > 0;
            }
            pos += 8 + size + (size & 1);
        }
        return false;
    }

    /// <summary>채널 <paramref name="channel"/> 디코드 → <paramref name="dst"/>(길이 ≥ <see cref="PlutoPcmWavInfo.Frames"/>) · [−1, 1] float.</summary>
    public static void DecodeChannel(ReadOnlySpan<byte> wav, in PlutoPcmWavInfo info, int channel, Span<float> dst)
    {
        var data = wav.Slice(info.DataOffset, info.DataLength);
        int frames = info.Frames, bps = info.BytesPerSample, block = info.BlockAlign;
        var c = Math.Clamp(channel, 0, info.Channels - 1) * bps;
        for (var f = 0; f < frames; f++) dst[f] = Sample(data.Slice(f * block + c), info.Format, info.BitsPerSample);
    }

    /// <summary>전 채널 평균 다운믹스(모노) → <paramref name="dst"/>.</summary>
    public static void DecodeMono(ReadOnlySpan<byte> wav, in PlutoPcmWavInfo info, Span<float> dst)
    {
        var data = wav.Slice(info.DataOffset, info.DataLength);
        int frames = info.Frames, bps = info.BytesPerSample, block = info.BlockAlign, ch = info.Channels;
        var inv = 1f / ch;
        for (var f = 0; f < frames; f++)
        {
            var o = f * block;
            var acc = 0f;
            for (var c = 0; c < ch; c++) acc += Sample(data.Slice(o + c * bps), info.Format, info.BitsPerSample);
            dst[f] = acc * inv;
        }
    }

    /// <summary>float(−1~1) → 표준 44바이트 RIFF/WAVE PCM16(모노 = <paramref name="right"/> 무시). 스케일 32768 · 대칭 클램프.</summary>
    public static byte[] EncodePcm16(ReadOnlySpan<float> left, ReadOnlySpan<float> right, int channels, int sampleRate)
    {
        channels = channels >= 2 ? 2 : 1;
        var n = channels == 2 ? Math.Min(left.Length, right.Length) : left.Length;
        var dataBytes = n * channels * 2;
        var buf = new byte[HeaderBytes + dataBytes];
        var s = buf.AsSpan();
        "RIFF"u8.CopyTo(s);
        BinaryPrimitives.WriteInt32LittleEndian(s.Slice(4), 36 + dataBytes);
        "WAVE"u8.CopyTo(s.Slice(8));
        "fmt "u8.CopyTo(s.Slice(12));
        BinaryPrimitives.WriteInt32LittleEndian(s.Slice(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(s.Slice(20), FormatPcm);
        BinaryPrimitives.WriteInt16LittleEndian(s.Slice(22), (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(s.Slice(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(s.Slice(28), sampleRate * channels * 2);
        BinaryPrimitives.WriteInt16LittleEndian(s.Slice(32), (short)(channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(s.Slice(34), 16);
        "data"u8.CopyTo(s.Slice(36));
        BinaryPrimitives.WriteInt32LittleEndian(s.Slice(40), dataBytes);
        var p = HeaderBytes;
        for (var i = 0; i < n; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(s.Slice(p), ToPcm16(left[i])); p += 2;
            if (channels == 2) { BinaryPrimitives.WriteInt16LittleEndian(s.Slice(p), ToPcm16(right[i])); p += 2; }
        }
        return buf;
    }

    /// <summary>PCM16 양자화 값(인코드 결과와 동일한 float) — 마스터링 최종 검증용.</summary>
    public static float QuantizePcm16(float v) => ToPcm16(v) / 32768f;

    public static short ToPcm16(float v)
    {
        if (!float.IsFinite(v)) return 0;
        var q = MathF.Round(v * 32768f);
        return (short)Math.Clamp(q, -32767f, 32767f);
    }

    private static float Sample(ReadOnlySpan<byte> b, int fmt, int bits)
    {
        if (fmt == FormatFloat)
        {
            var f = BinaryPrimitives.ReadSingleLittleEndian(b);
            return float.IsFinite(f) ? f : 0f;
        }
        return bits switch
        {
            16 => BinaryPrimitives.ReadInt16LittleEndian(b) / 32768f,
            24 => ((b[0] | (b[1] << 8) | (b[2] << 16)) << 8 >> 8) / 8388608f,
            _ => BinaryPrimitives.ReadInt32LittleEndian(b) / 2147483648f,
        };
    }
}
