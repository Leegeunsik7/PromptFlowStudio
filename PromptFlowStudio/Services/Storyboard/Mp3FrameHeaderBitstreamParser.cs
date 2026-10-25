using System.Buffers.Binary;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>[v18.2.4] MP3 비트스트림 분석 결과.</summary>
public enum Mp3ParseStatus : byte { None, Ok, Empty, NoSync, Unsupported }

/// <summary>[v18.2.4] MPEG 오디오 버전(헤더 2 비트).</summary>
public enum MpegVersion : byte { Mpeg25 = 0, Reserved = 1, Mpeg2 = 2, Mpeg1 = 3 }

/// <summary>[v18.2.4] 첫 프레임 메타 태그 — Xing(VBR) · Info(LAME CBR) · VBRI(Fraunhofer VBR).</summary>
public enum Mp3VbrTag : byte { None, Xing, Info, Vbri }

/// <summary>[v18.2.4] MPEG-1/2/2.5 Layer III 프레임 헤더 1 개(4 B 해석 결과).</summary>
public readonly struct Mp3FrameHeader
{
    public readonly MpegVersion Version;
    public readonly int BitrateKbps, SampleRate, Channels, FrameLength, SamplesPerFrame, SideInfoLength;
    public readonly bool Padding, Crc;

    public Mp3FrameHeader(MpegVersion version, int bitrateKbps, int sampleRate, int channels, int frameLength, int samplesPerFrame, int sideInfoLength, bool padding, bool crc)
    {
        Version = version; BitrateKbps = bitrateKbps; SampleRate = sampleRate; Channels = channels; FrameLength = frameLength;
        SamplesPerFrame = samplesPerFrame; SideInfoLength = sideInfoLength; Padding = padding; Crc = crc;
    }

    public bool IsValid => FrameLength > 0;
}

/// <summary>
/// [v18.2.4] MP3 스트림 실측 — 오디오 프레임 수(메타 태그 프레임 제외) · 총 샘플(LAME 지연/패딩 제거) · 재생 시간(100 ns 틱 정밀도).
///   <see cref="DataOffset"/>/<see cref="DataLength"/> = ID3v2 · ID3v1 · 메타 프레임을 뺀 순수 오디오 프레임 구간.
/// </summary>
public readonly struct Mp3StreamInfo
{
    public readonly Mp3ParseStatus Status;
    public readonly MpegVersion Version;
    public readonly Mp3VbrTag Tag;
    public readonly int SampleRate, Channels, SamplesPerFrame, Frames, HeaderFrames, AvgBitrateKbps, DataOffset, DataLength, EncoderDelay, EncoderPadding;
    public readonly bool IsVbr;
    public readonly long TotalSamples;

    public Mp3StreamInfo(Mp3ParseStatus status, MpegVersion version, Mp3VbrTag tag, int sampleRate, int channels, int samplesPerFrame, int frames, int headerFrames,
                         int avgBitrateKbps, int dataOffset, int dataLength, int encoderDelay, int encoderPadding, bool isVbr, long totalSamples)
    {
        Status = status; Version = version; Tag = tag; SampleRate = sampleRate; Channels = channels; SamplesPerFrame = samplesPerFrame;
        Frames = frames; HeaderFrames = headerFrames; AvgBitrateKbps = avgBitrateKbps; DataOffset = dataOffset; DataLength = dataLength;
        EncoderDelay = encoderDelay; EncoderPadding = encoderPadding; IsVbr = isVbr; TotalSamples = totalSamples;
    }

    public bool IsOk => Status == Mp3ParseStatus.Ok;
    /// <summary>재생 시간(샘플 → 100 ns 틱 절삭 · 1 ms 미만 오차 0).</summary>
    public TimeSpan Duration => SampleRate <= 0 ? TimeSpan.Zero : TimeSpan.FromTicks(TotalSamples * TimeSpan.TicksPerSecond / SampleRate);
    /// <summary>재생 시간 ms(반올림).</summary>
    public int DurationMs => SampleRate <= 0 ? 0 : (int)((TotalSamples * 1000 + SampleRate / 2) / SampleRate);
}

/// <summary>
/// [PromptFlow Studio v18.2.4] 🎧 무할당 MPEG Audio Layer III 프레임 헤더 비트스트림 파서 — 외부 코덱 0 · 순수 Span 해석.
///   ▸ <see cref="TryReadHeader"/>: 11 비트 싱크(0xFFE) · 버전 · Layer III · 비트레이트/샘플레이트 테이블 · 패딩 · 채널 모드 → 프레임 길이.
///   ▸ <see cref="Analyze"/>: ID3v2 건너뜀 → 연속 2 프레임 검증으로 첫 싱크 확정 → 프레임 길이 점프 순회(손상 구간 재동기화) · 첫 프레임 Xing/Info/VBRI · LAME 지연/패딩.
///   ▸ CBR · VBR 모두 순회 실측 프레임 수가 기준(태그 프레임 수는 <see cref="Mp3StreamInfo.HeaderFrames"/> 로 보고) · 힙 할당 0 B.
/// </summary>
public static class Mp3FrameHeaderBitstreamParser
{
    /// <summary>첫 싱크 탐색 창(ID3v2 이후 잡음 허용 범위).</summary>
    public const int SyncSearchWindow = 64 << 10;
    /// <summary>손상 구간 재동기화 탐색 창.</summary>
    public const int ResyncWindow = 8 << 10;

    // Layer III 비트레이트(kbps ÷ 8 · byte 테이블 = 어셈블리 데이터 직참조 0 B) — MPEG-1 · MPEG-2/2.5 · 인덱스 0(free) · 15(bad) 는 거부
    private static ReadOnlySpan<byte> BitrateV1 => new byte[] { 0, 4, 5, 6, 7, 8, 10, 12, 14, 16, 20, 24, 28, 32, 40, 0 };
    private static ReadOnlySpan<byte> BitrateV2 => new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 18, 20, 0 };

    /// <summary>4 B 헤더 해석 — Layer III · 비트레이트 1~14 · 샘플레이트 0~2 만 유효(0 B).</summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> b, out Mp3FrameHeader h)
    {
        h = default;
        if (b.Length < 4 || b[0] != 0xFF || (b[1] & 0xE0) != 0xE0) return false;
        var ver = (MpegVersion)((b[1] >> 3) & 3);
        if (ver == MpegVersion.Reserved || ((b[1] >> 1) & 3) != 1) return false;   // Layer III = 01
        var bi = b[2] >> 4;
        var si = (b[2] >> 2) & 3;
        if (bi == 0 || bi == 15 || si == 3) return false;
        var v1 = ver == MpegVersion.Mpeg1;
        var kbps = (v1 ? BitrateV1[bi] : BitrateV2[bi]) * 8;
        var rate = (si == 0 ? 44100 : si == 1 ? 48000 : 32000) >> (v1 ? 0 : ver == MpegVersion.Mpeg2 ? 1 : 2);
        var pad = (b[2] & 2) != 0;
        var ch = (b[3] >> 6) == 3 ? 1 : 2;
        var spf = v1 ? 1152 : 576;
        var len = (spf / 8) * kbps * 1000 / rate + (pad ? 1 : 0);
        var side = v1 ? (ch == 1 ? 17 : 32) : (ch == 1 ? 9 : 17);
        h = new Mp3FrameHeader(ver, kbps, rate, ch, len, spf, side, pad, (b[1] & 1) == 0);
        return true;
    }

    /// <summary>ID3v2 태그 전체 길이(헤더 10 B + 동기안전 크기 + 꼬리말) · 없으면 0.</summary>
    public static int Id3v2Length(ReadOnlySpan<byte> b)
    {
        if (b.Length < 10 || b[0] != (byte)'I' || b[1] != (byte)'D' || b[2] != (byte)'3') return 0;
        if (((b[6] | b[7] | b[8] | b[9]) & 0x80) != 0) return 0;
        var size = (b[6] << 21) | (b[7] << 14) | (b[8] << 7) | b[9];
        return 10 + size + ((b[5] & 0x10) != 0 ? 10 : 0);
    }

    /// <summary>MP3 판별(파일 머리 4 B 이상) — ID3v2 또는 유효 Layer III 헤더.</summary>
    public static bool LooksLikeMp3(ReadOnlySpan<byte> head) =>
        head.Length >= 3 && head[0] == (byte)'I' && head[1] == (byte)'D' && head[2] == (byte)'3' || TryReadHeader(head, out _);

    /// <summary>전체 버퍼 분석(0 B) — 첫 싱크 확정 · 프레임 점프 순회 · 메타 태그/LAME 지연 반영.</summary>
    public static Mp3StreamInfo Analyze(ReadOnlySpan<byte> mp3)
    {
        if (mp3.IsEmpty) return Fail(Mp3ParseStatus.Empty);
        var end = mp3.Length;
        if (end >= 128 && mp3[end - 128] == (byte)'T' && mp3[end - 127] == (byte)'A' && mp3[end - 126] == (byte)'G') end -= 128;   // ID3v1
        var start = Math.Min(Id3v2Length(mp3), end);
        var first = FindSync(mp3[..end], start, Math.Min(end, start + SyncSearchWindow));
        if (first < 0) return Fail(Mp3ParseStatus.NoSync);
        TryReadHeader(mp3[first..], out var h0);

        // 첫 프레임 메타 태그(Xing/Info/VBRI) — 오디오 프레임 아님
        var tag = ReadTag(mp3.Slice(first, Math.Min(h0.FrameLength, end - first)), h0, out var tagFrames, out var delay, out var padding);
        var pos = first;
        if (tag != Mp3VbrTag.None) pos += h0.FrameLength;
        var dataOffset = pos;

        int frames = 0, minKbps = int.MaxValue, maxKbps = 0;
        long bits = 0;
        var dataEnd = pos;
        while (pos + 4 <= end)
        {
            if (!TryReadHeader(mp3[pos..], out var h) || h.SampleRate != h0.SampleRate || h.Version != h0.Version)
            {
                var next = FindSync(mp3[..end], pos + 1, Math.Min(end, pos + ResyncWindow));
                if (next < 0) break;
                pos = next;
                continue;
            }
            if (pos + h.FrameLength > end) break;   // 잘린 마지막 프레임 제외
            frames++;
            bits += h.BitrateKbps;
            if (h.BitrateKbps < minKbps) minKbps = h.BitrateKbps;
            if (h.BitrateKbps > maxKbps) maxKbps = h.BitrateKbps;
            pos += h.FrameLength;
            dataEnd = pos;
        }
        if (frames == 0) return Fail(Mp3ParseStatus.NoSync);
        var total = (long)frames * h0.SamplesPerFrame;
        if (delay + padding < total) total -= delay + padding;
        else delay = padding = 0;
        return new Mp3StreamInfo(Mp3ParseStatus.Ok, h0.Version, tag, h0.SampleRate, h0.Channels, h0.SamplesPerFrame, frames, tagFrames,
                                 (int)(bits / frames), dataOffset, dataEnd - dataOffset, delay, padding, minKbps != maxKbps, total);
    }

    /// <summary>[from, limit) 에서 다음 프레임 헤더도 유효(또는 정확히 끝)인 첫 싱크 오프셋 · 없으면 -1.</summary>
    private static int FindSync(ReadOnlySpan<byte> b, int from, int limit)
    {
        for (var i = from; i + 4 <= limit; i++)
        {
            if (b[i] != 0xFF || !TryReadHeader(b[i..], out var h)) continue;
            var n = i + h.FrameLength;
            if (n == b.Length) return i;
            if (n + 4 <= b.Length && TryReadHeader(b[n..], out var h2) && h2.SampleRate == h.SampleRate && h2.Version == h.Version) return i;
        }
        return -1;
    }

    /// <summary>첫 프레임의 Xing/Info(사이드 정보 뒤) · VBRI(헤더 + 32 B) 태그 · LAME 지연/패딩(12 비트 × 2).</summary>
    private static Mp3VbrTag ReadTag(ReadOnlySpan<byte> f, in Mp3FrameHeader h, out int frames, out int delay, out int padding)
    {
        frames = delay = padding = 0;
        var x = 4 + (h.Crc ? 2 : 0) + h.SideInfoLength;
        if (x + 8 <= f.Length)
        {
            var id = f.Slice(x, 4);
            var tag = id.SequenceEqual("Xing"u8) ? Mp3VbrTag.Xing : id.SequenceEqual("Info"u8) ? Mp3VbrTag.Info : Mp3VbrTag.None;
            if (tag != Mp3VbrTag.None)
            {
                var flags = BinaryPrimitives.ReadUInt32BigEndian(f[(x + 4)..]);
                var p = x + 8;
                if ((flags & 1) != 0 && p + 4 <= f.Length) { frames = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(f[p..])); p += 4; }
                if ((flags & 2) != 0) p += 4;
                if ((flags & 4) != 0) p += 100;
                if ((flags & 8) != 0) p += 4;
                if (p + 24 <= f.Length && f.Slice(p, 4).SequenceEqual("LAME"u8))
                {
                    delay = (f[p + 21] << 4) | (f[p + 22] >> 4);
                    padding = ((f[p + 22] & 0x0F) << 8) | f[p + 23];
                }
                return tag;
            }
        }
        if (36 + 18 <= f.Length && f.Slice(36, 4).SequenceEqual("VBRI"u8))
        {
            frames = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(f[50..]));
            return Mp3VbrTag.Vbri;
        }
        return Mp3VbrTag.None;
    }

    private static Mp3StreamInfo Fail(Mp3ParseStatus st) => new(st, default, default, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, 0);
}
