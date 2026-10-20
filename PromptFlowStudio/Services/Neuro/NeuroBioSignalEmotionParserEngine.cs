using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.Neuro;

public enum EegBand : byte { Theta, Alpha, Beta, Gamma }
public enum BioPacketKind : byte { Eeg = 1, Rr = 2, Gsr = 3 }

/// <summary>피험자 감정 상태(0..1 지수 + 생체 원시 지표). Fear = 공포/전율 지수.</summary>
public readonly record struct EmotionState(float Engagement, float Arousal, float Valence, float Fear, float Confidence,
    float HeartRate, float Rmssd, float GsrMicroSiemens, float Theta, float Alpha, float Beta, float Gamma, long TimestampMicros)
{
    public static readonly EmotionState Neutral = new(0.5f, 0.5f, 0.5f, 0.2f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0);

    /// <summary>지배 감정 라벨(리터럴 · 무할당).</summary>
    public string Dominant => Fear > 0.65f ? "공포/전율"
        : Arousal > 0.62f && Valence > 0.55f ? "환희/흥분"
        : Valence > 0.6f ? "평온/만족"
        : Engagement < 0.35f ? "이탈/지루함"
        : Valence < 0.4f ? "긴장/불안"
        : "몰입";
}

/// <summary>피험자별 스트림 상태(최초 수신 시 1회 할당 · 이후 재사용).</summary>
internal sealed class NeuroSubject
{
    public const int Channels = 4, WaveLen = 512, RrLen = 32, Bands = 4;
    public int DeviceId, ChannelCount = 4, SampleRate;
    public readonly float[] Coef = new float[Bands * 5];
    public readonly float[] Z = new float[Channels * Bands * 4];
    public readonly float[] Power = new float[Channels * Bands];
    public readonly float[] Dc = new float[Channels];
    public readonly float[] Wave = new float[Channels * WaveLen];
    public readonly float[] Rr = new float[RrLen];
    public int WavePos, Hop, RrPos, RrCount;
    public long Samples, Artifacts, LastMicros, LastGsrMicros;
    public float Hr, Rmssd, GsrFast, GsrSlow, ScrRate, Quality = 1f, PowerK;
    public bool Phasic, HasState;
    public EmotionState State = EmotionState.Neutral;
}

/// <summary>
/// [PromptFlow Studio v16.56.0] 🧠 실시간 AI 뇌파(EEG) &amp; 바이오 신호 감정 파서(Pure BCL · 무할당 스트림).
///   ▸ 입력: 바이너리 "PFBS" 패킷(EEG int16 0.1µV · RR u16 ms · GSR u16 0.01µS) · 텍스트 라인(EEG,dev,sr,v1..vN / RR,dev,ms / GSR,dev,µS) ·
///     UDP 수신 루프(SIO_UDP_CONNRESET off · 백오프) · BLE GATT/LSL(LCI) 브릿지는 <see cref="IngestBinary"/>/<see cref="IngestText"/> 결선점.
///   ▸ EEG: 채널별 DC 추적 → 150µV 아티팩트(눈깜빡임/근전도) 배제 → 4차(2단 바이쿼드) 대역통과 θ4–8 · α8–13 · β13–30 · γ30–45 → 0.5s EMA 대역 전력.
///   ▸ HRV: RR → 심박(EMA) · RMSSD(32 RR) · GSR: 토닉(느린 EMA)/페이직(빠른−느린) · SCR 발생률.
///   ▸ 지수(32샘플 홉): 몰입 β/(α+θ)(Pope) · 흥분 ln(β/α)+HR+GSR · 쾌적 전두 알파 비대칭 FAA(AF8−AF7)+HRV · 공포/전율 = 고흥분×저쾌적+SCR → 시그모이드 · EMA.
/// </summary>
public sealed class NeuroBioSignalEmotionParserEngine : IDisposable
{
    public const int MaxSubjects = 256, Channels = NeuroSubject.Channels, DefaultSampleRate = 256, HopSamples = 32, HeaderSize = 28;
    public const float ArtifactMicrovolts = 150f;
    private static readonly float[] BandLo = { 4f, 8f, 13f, 30f };
    private static readonly float[] BandHi = { 8f, 13f, 30f, 45f };

    private readonly object _gate = new();
    private readonly NeuroSubject?[] _subjects = new NeuroSubject?[MaxSubjects];
    private readonly int[] _map = new int[65536];
    private readonly byte[] _udpBuf = new byte[4096];
    private readonly char[] _udpChars = new char[4096];
    private int _count;
    private long _packets, _bad, _artifacts, _samples, _updates;
    private CancellationTokenSource? _udpCts;
    private Socket? _udp;

    public event Action? OnChange;

    public int SubjectCount => Volatile.Read(ref _count);
    public long Packets => Interlocked.Read(ref _packets);
    public long BadPackets => Interlocked.Read(ref _bad);
    public long Artifacts => Interlocked.Read(ref _artifacts);
    public long Samples => Interlocked.Read(ref _samples);
    public long EmotionUpdates => Interlocked.Read(ref _updates);
    public bool UdpListening => _udp is not null;
    public int UdpPort { get; private set; }
    public string? LastAction { get; private set; }

    /// <summary>디바이스 ID → 피험자 인덱스(없으면 생성 · 만석 -1).</summary>
    public int Resolve(int deviceId)
    {
        if ((uint)deviceId > 65535) return -1;
        var m = Volatile.Read(ref _map[deviceId]);
        if (m > 0) return m - 1;
        lock (_gate)
        {
            if (_map[deviceId] > 0) return _map[deviceId] - 1;
            if (_count >= MaxSubjects) return -1;
            var idx = _count;
            _subjects[idx] = new NeuroSubject { DeviceId = deviceId };
            Volatile.Write(ref _map[deviceId], idx + 1);
            Volatile.Write(ref _count, idx + 1);
            return idx;
        }
    }

    public int IndexOf(int deviceId) => (uint)deviceId <= 65535 ? Volatile.Read(ref _map[deviceId]) - 1 : -1;
    public int DeviceIdAt(int index) => (uint)index < (uint)SubjectCount ? _subjects[index]!.DeviceId : -1;

    public bool TryGetState(int index, out EmotionState s)
    {
        s = EmotionState.Neutral;
        if ((uint)index >= (uint)SubjectCount || _subjects[index] is not { HasState: true } sub) return false;
        lock (sub) s = sub.State;
        return true;
    }

    public EmotionState StateAt(int index) => TryGetState(index, out var s) ? s : EmotionState.Neutral;

    /// <summary>원시 파형(아티팩트 포함 · DC 제거) 최근 dst.Length 샘플 복사. 반환 = 복사 수.</summary>
    public int CopyWaveform(int index, int channel, Span<float> dst)
    {
        if ((uint)index >= (uint)SubjectCount || (uint)channel >= Channels || _subjects[index] is not { } s) return 0;
        lock (s)
        {
            var n = Math.Min(dst.Length, NeuroSubject.WaveLen);
            var w = s.Wave.AsSpan(channel * NeuroSubject.WaveLen, NeuroSubject.WaveLen);
            for (var i = 0; i < n; i++) dst[i] = w[(s.WavePos - n + i + NeuroSubject.WaveLen) % NeuroSubject.WaveLen];
            return n;
        }
    }

    /// <summary>대역 전력(µV²) 조회.</summary>
    public float BandPower(int index, int channel, EegBand band)
    {
        if ((uint)index >= (uint)SubjectCount || _subjects[index] is not { } s) return 0f;
        lock (s) return s.Power[channel * NeuroSubject.Bands + (int)band];
    }

    private static void Configure(NeuroSubject s, int sampleRate)
    {
        s.SampleRate = sampleRate;
        s.PowerK = 1f - MathF.Exp(-1f / (sampleRate * 0.5f));
        for (var b = 0; b < NeuroSubject.Bands; b++)
        {
            var hi = MathF.Min(BandHi[b], sampleRate * 0.45f);
            var f0 = MathF.Sqrt(BandLo[b] * hi);
            var q = f0 / MathF.Max(0.5f, hi - BandLo[b]);
            var w0 = MathF.Tau * f0 / sampleRate;
            var al = MathF.Sin(w0) / (2f * q);
            var a0 = 1f + al;
            var c = b * 5;
            s.Coef[c] = al / a0; s.Coef[c + 1] = 0f; s.Coef[c + 2] = -al / a0;
            s.Coef[c + 3] = -2f * MathF.Cos(w0) / a0; s.Coef[c + 4] = (1f - al) / a0;
        }
        Array.Clear(s.Z); Array.Clear(s.Power); Array.Clear(s.Dc);
    }

    /// <summary>EEG 인터리브 샘플(µV · frames×channels) 수신(무할당).</summary>
    public bool IngestEeg(int deviceId, ReadOnlySpan<float> interleaved, int channels, int sampleRate, long tMicros)
    {
        var idx = Resolve(deviceId);
        if (idx < 0 || channels <= 0 || channels > Channels || sampleRate < 64 || sampleRate > 4096) { Interlocked.Increment(ref _bad); return false; }
        var s = _subjects[idx]!;
        lock (s)
        {
            if (s.SampleRate != sampleRate || s.ChannelCount != channels) { s.ChannelCount = channels; Configure(s, sampleRate); }
            var frames = interleaved.Length / channels;
            for (var f = 0; f < frames; f++) ProcessFrame(s, interleaved.Slice(f * channels, channels), tMicros);
        }
        return true;
    }

    private void ProcessFrame(NeuroSubject s, ReadOnlySpan<float> x, long t)
    {
        var n = x.Length;
        var artifact = false;
        Span<float> xa = stackalloc float[Channels];
        for (var c = 0; c < n; c++)
        {
            if (s.Samples < 64) s.Dc[c] += (x[c] - s.Dc[c]) * 0.2f;
            else s.Dc[c] += (x[c] - s.Dc[c]) * 0.002f;
            xa[c] = x[c] - s.Dc[c];
            s.Wave[c * NeuroSubject.WaveLen + s.WavePos] = xa[c];
            if (MathF.Abs(xa[c]) > ArtifactMicrovolts) artifact = true;
        }
        s.WavePos = (s.WavePos + 1) % NeuroSubject.WaveLen;
        s.Samples++;
        s.LastMicros = t;
        s.Quality += ((artifact ? 0f : 1f) - s.Quality) * 0.01f;
        if (artifact) { s.Artifacts++; Interlocked.Increment(ref _artifacts); }
        else
        {
            var k = s.PowerK;
            for (var c = 0; c < n; c++)
            {
                for (var b = 0; b < NeuroSubject.Bands; b++)
                {
                    var cf = b * 5;
                    var z = (c * NeuroSubject.Bands + b) * 4;
                    float b0 = s.Coef[cf], b2 = s.Coef[cf + 2], a1 = s.Coef[cf + 3], a2 = s.Coef[cf + 4];
                    var v = xa[c];
                    var y = b0 * v + s.Z[z];
                    s.Z[z] = -a1 * y + s.Z[z + 1];
                    s.Z[z + 1] = b2 * v - a2 * y;
                    var y2 = b0 * y + s.Z[z + 2];
                    s.Z[z + 2] = -a1 * y2 + s.Z[z + 3];
                    s.Z[z + 3] = b2 * y - a2 * y2;
                    ref var p = ref s.Power[c * NeuroSubject.Bands + b];
                    p += (y2 * y2 - p) * k;
                }
            }
        }
        Interlocked.Increment(ref _samples);
        if (++s.Hop >= HopSamples) { s.Hop = 0; UpdateEmotion(s, t); }
    }

    private static float Sig(float x) => 1f / (1f + MathF.Exp(-x));

    private void UpdateEmotion(NeuroSubject s, long t)
    {
        const float eps = 1e-3f;
        var n = s.ChannelCount;
        float th = 0, al = 0, be = 0, ga = 0;
        for (var c = 0; c < n; c++)
        {
            var o = c * NeuroSubject.Bands;
            th += s.Power[o]; al += s.Power[o + 1]; be += s.Power[o + 2]; ga += s.Power[o + 3];
        }
        th /= n; al /= n; be /= n; ga /= n;
        if (al + be + th < eps) return;
        var eng = Sig(1.5f * (MathF.Log(be / (al + th + eps) + eps) + 0.7f));
        var hrTerm = s.Hr > 0 ? (s.Hr - 72f) / 15f : 0f;
        var gsrTerm = s.GsrSlow > 0 ? (s.GsrSlow - 4f) / 2f + 2f * (s.GsrFast - s.GsrSlow) : 0f;
        var aro = Sig(0.8f * MathF.Log((be + eps) / (al + eps)) + 0.6f * hrTerm + 0.5f * gsrTerm);
        var faa = n >= 3 ? MathF.Log(s.Power[2 * NeuroSubject.Bands + 1] + eps) - MathF.Log(s.Power[1 * NeuroSubject.Bands + 1] + eps) : 0f;
        var hrv = s.Rmssd > 0 ? (s.Rmssd - 35f) / 20f : 0f;
        var val = Sig(3f * faa + 0.3f * hrv);
        var fear = Sig(5f * (aro - 0.55f) + 4f * (0.45f - val) + 0.6f * MathF.Min(s.ScrRate / 4f, 2f));
        var conf = Math.Clamp(s.Quality, 0f, 1f) * (n / (float)Channels);
        var p = s.State;
        var a = s.HasState ? 0.25f : 1f;
        s.State = new EmotionState(p.Engagement + (eng - p.Engagement) * a, p.Arousal + (aro - p.Arousal) * a, p.Valence + (val - p.Valence) * a,
            p.Fear + (fear - p.Fear) * a, conf, s.Hr, s.Rmssd, s.GsrSlow, th, al, be, ga, t);
        s.HasState = true;
        Interlocked.Increment(ref _updates);
    }

    /// <summary>RR 간격(ms) 수신 → 심박 · RMSSD.</summary>
    public bool IngestRr(int deviceId, float rrMs, long tMicros)
    {
        var idx = Resolve(deviceId);
        if (idx < 0 || rrMs < 250f || rrMs > 2000f) { Interlocked.Increment(ref _bad); return false; }
        var s = _subjects[idx]!;
        lock (s)
        {
            s.Rr[s.RrPos] = rrMs;
            s.RrPos = (s.RrPos + 1) % NeuroSubject.RrLen;
            if (s.RrCount < NeuroSubject.RrLen) s.RrCount++;
            var hr = 60000f / rrMs;
            s.Hr = s.Hr <= 0 ? hr : s.Hr + (hr - s.Hr) * 0.2f;
            if (s.RrCount >= 3)
            {
                double sum = 0;
                for (var i = 1; i < s.RrCount; i++)
                {
                    var a = s.Rr[(s.RrPos - i + NeuroSubject.RrLen) % NeuroSubject.RrLen];
                    var b = s.Rr[(s.RrPos - i - 1 + NeuroSubject.RrLen) % NeuroSubject.RrLen];
                    sum += (a - b) * (a - b);
                }
                s.Rmssd = (float)Math.Sqrt(sum / (s.RrCount - 1));
            }
        }
        return true;
    }

    /// <summary>피부 전도도(µS) 수신 → 토닉/페이직 · SCR 발생률(30s 감쇠 계수).</summary>
    public bool IngestGsr(int deviceId, float microSiemens, long tMicros)
    {
        var idx = Resolve(deviceId);
        if (idx < 0 || microSiemens < 0f || microSiemens > 100f) { Interlocked.Increment(ref _bad); return false; }
        var s = _subjects[idx]!;
        lock (s)
        {
            if (s.GsrSlow <= 0) { s.GsrSlow = s.GsrFast = microSiemens; s.LastGsrMicros = tMicros; return true; }
            var dt = Math.Clamp((tMicros - s.LastGsrMicros) / 1e6f, 0.001f, 2f);
            s.LastGsrMicros = tMicros;
            s.GsrFast += (microSiemens - s.GsrFast) * (1f - MathF.Exp(-dt / 1f));
            s.GsrSlow += (microSiemens - s.GsrSlow) * (1f - MathF.Exp(-dt / 20f));
            s.ScrRate *= MathF.Exp(-dt / 30f);
            var phasic = s.GsrFast - s.GsrSlow > 0.05f;
            if (phasic && !s.Phasic) s.ScrRate += 1f;
            s.Phasic = phasic;
        }
        return true;
    }

    /// <summary>바이너리 PFBS 패킷 파싱(무할당). 헤더 28B: "PFBS" · ver · kind · channels · rsv · device u16 · sr u16 · seq u32 · t µs u64 · count u16 · rsv u16.</summary>
    public bool IngestBinary(ReadOnlySpan<byte> p)
    {
        if (p.Length < HeaderSize || !p[..4].SequenceEqual("PFBS"u8) || p[4] != 1) { Interlocked.Increment(ref _bad); return false; }
        var kind = (BioPacketKind)p[5];
        int ch = p[6], dev = BinaryPrimitives.ReadUInt16LittleEndian(p[8..]), sr = BinaryPrimitives.ReadUInt16LittleEndian(p[10..]);
        var t = BinaryPrimitives.ReadInt64LittleEndian(p[16..]);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(p[24..]);
        var body = p[HeaderSize..];
        switch (kind)
        {
            case BioPacketKind.Eeg:
                if (ch is < 1 or > Channels || body.Length < count * ch * 2 || count > 256) break;
                Span<float> frame = stackalloc float[Channels * 256];
                var f = frame[..(count * ch)];
                for (var i = 0; i < f.Length; i++) f[i] = BinaryPrimitives.ReadInt16LittleEndian(body[(i * 2)..]) * 0.1f;
                IngestEeg(dev, f, ch, sr, t);
                Interlocked.Increment(ref _packets);
                return true;
            case BioPacketKind.Rr:
                if (body.Length < count * 2) break;
                for (var i = 0; i < count; i++) IngestRr(dev, BinaryPrimitives.ReadUInt16LittleEndian(body[(i * 2)..]), t);
                Interlocked.Increment(ref _packets);
                return true;
            case BioPacketKind.Gsr:
                if (body.Length < count * 2) break;
                for (var i = 0; i < count; i++) IngestGsr(dev, BinaryPrimitives.ReadUInt16LittleEndian(body[(i * 2)..]) * 0.01f, t);
                Interlocked.Increment(ref _packets);
                return true;
        }
        Interlocked.Increment(ref _bad);
        return false;
    }

    private static void WriteHeader(Span<byte> d, BioPacketKind kind, int channels, ushort device, int sampleRate, uint seq, long t, int count)
    {
        "PFBS"u8.CopyTo(d);
        d[4] = 1; d[5] = (byte)kind; d[6] = (byte)channels; d[7] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(d[8..], device);
        BinaryPrimitives.WriteUInt16LittleEndian(d[10..], (ushort)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(d[12..], seq);
        BinaryPrimitives.WriteInt64LittleEndian(d[16..], t);
        BinaryPrimitives.WriteUInt16LittleEndian(d[24..], (ushort)count);
        BinaryPrimitives.WriteUInt16LittleEndian(d[26..], 0);
    }

    /// <summary>EEG 패킷 직렬화(디바이스/시뮬 · 무할당). 반환 = 바이트 수.</summary>
    public static int WriteEegPacket(Span<byte> d, ushort device, uint seq, long t, int sampleRate, ReadOnlySpan<float> interleaved, int channels)
    {
        var frames = interleaved.Length / channels;
        var len = HeaderSize + frames * channels * 2;
        if (d.Length < len || frames > 256) return 0;
        WriteHeader(d, BioPacketKind.Eeg, channels, device, sampleRate, seq, t, frames);
        for (var i = 0; i < frames * channels; i++)
            BinaryPrimitives.WriteInt16LittleEndian(d[(HeaderSize + i * 2)..], (short)Math.Clamp((int)MathF.Round(interleaved[i] * 10f), short.MinValue, short.MaxValue));
        return len;
    }

    public static int WriteScalarPacket(Span<byte> d, BioPacketKind kind, ushort device, uint seq, long t, float value)
    {
        if (d.Length < HeaderSize + 2 || kind == BioPacketKind.Eeg) return 0;
        WriteHeader(d, kind, 0, device, 0, seq, t, 1);
        var raw = kind == BioPacketKind.Gsr ? value * 100f : value;
        BinaryPrimitives.WriteUInt16LittleEndian(d[HeaderSize..], (ushort)Math.Clamp((int)MathF.Round(raw), 0, 65535));
        return HeaderSize + 2;
    }

    /// <summary>텍스트 라인 파싱(무할당 · Span) — "EEG,dev,sr,v1,..,vN" · "RR,dev,ms" · "GSR,dev,µS".</summary>
    public bool IngestText(ReadOnlySpan<char> line)
    {
        line = line.Trim();
        var comma = line.IndexOf(',');
        if (comma <= 0) { Interlocked.Increment(ref _bad); return false; }
        var tag = line[..comma];
        var rest = line[(comma + 1)..];
        if (!NextInt(ref rest, out var dev)) { Interlocked.Increment(ref _bad); return false; }
        var t = (long)(Stopwatch.GetTimestamp() * 1_000_000.0 / Stopwatch.Frequency);
        if (tag.Equals("EEG", StringComparison.OrdinalIgnoreCase))
        {
            if (!NextInt(ref rest, out var sr)) { Interlocked.Increment(ref _bad); return false; }
            Span<float> v = stackalloc float[Channels];
            var n = 0;
            while (n < Channels && NextFloat(ref rest, out var f)) v[n++] = f;
            if (n == 0) { Interlocked.Increment(ref _bad); return false; }
            if (!IngestEeg(dev, v[..n], n, sr, t)) return false;
        }
        else if (tag.Equals("RR", StringComparison.OrdinalIgnoreCase) && NextFloat(ref rest, out var rr)) { if (!IngestRr(dev, rr, t)) return false; }
        else if (tag.Equals("GSR", StringComparison.OrdinalIgnoreCase) && NextFloat(ref rest, out var g)) { if (!IngestGsr(dev, g, t)) return false; }
        else { Interlocked.Increment(ref _bad); return false; }
        Interlocked.Increment(ref _packets);
        return true;
    }

    private static ReadOnlySpan<char> NextField(ref ReadOnlySpan<char> rest)
    {
        var c = rest.IndexOf(',');
        var f = c < 0 ? rest : rest[..c];
        rest = c < 0 ? ReadOnlySpan<char>.Empty : rest[(c + 1)..];
        return f.Trim();
    }

    private static bool NextInt(ref ReadOnlySpan<char> rest, out int v)
        => int.TryParse(NextField(ref rest), NumberStyles.Integer, CultureInfo.InvariantCulture, out v);

    private static bool NextFloat(ref ReadOnlySpan<char> rest, out float v)
    {
        v = 0;
        return !rest.IsEmpty && float.TryParse(NextField(ref rest), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    /// <summary>UDP 수신 시작(바이너리 PFBS 또는 줄바꿈 텍스트). 루프백 기본 · LAN 헤드셋은 any=true.</summary>
    public void StartUdp(int port, bool any = false)
    {
        if (_udp is not null) return;
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                try { s.IOControl(unchecked((int)0x9800000C), new byte[4], null); } catch (SocketException) { } catch (PlatformNotSupportedException) { }
            }
            s.Bind(new IPEndPoint(any ? IPAddress.Any : IPAddress.Loopback, port));
        }
        catch (SocketException ex) { s.Dispose(); LastAction = "⚠ UDP 바인드 실패 · " + ex.SocketErrorCode; Raise(); return; }
        _udp = s;
        UdpPort = ((IPEndPoint)s.LocalEndPoint!).Port;
        var cts = new CancellationTokenSource();
        _udpCts = cts;
        _ = Task.Run(() => UdpLoopAsync(s, cts.Token));
        LastAction = "📶 UDP 수신 :" + UdpPort.ToString(CultureInfo.InvariantCulture);
        Raise();
    }

    private async Task UdpLoopAsync(Socket s, CancellationToken ct)
    {
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await s.ReceiveFromAsync(_udpBuf, SocketFlags.None, any, ct).ConfigureAwait(false);
                HandleDatagram(r.ReceivedBytes);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                PromptFlowStudio.Program.Log("Neuro.Udp", ex);
                try { await Task.Delay(50, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private void HandleDatagram(int n)
    {
        var d = _udpBuf.AsSpan(0, n);
        if (n >= 4 && d[..4].SequenceEqual("PFBS"u8)) { IngestBinary(d); return; }
        var chars = Encoding.UTF8.GetChars(d, _udpChars);
        ReadOnlySpan<char> text = _udpChars.AsSpan(0, chars);
        while (!text.IsEmpty)
        {
            var nl = text.IndexOf('\n');
            var line = nl < 0 ? text : text[..nl];
            text = nl < 0 ? ReadOnlySpan<char>.Empty : text[(nl + 1)..];
            if (!line.Trim().IsEmpty) IngestText(line);
        }
    }

    public void StopUdp()
    {
        var c = Interlocked.Exchange(ref _udpCts, null);
        var s = Interlocked.Exchange(ref _udp, null);
        if (c is null && s is null) return;
        c?.Cancel(); s?.Dispose(); c?.Dispose();
        LastAction = "📴 UDP 수신 정지";
        Raise();
    }

    internal void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Neuro.Parser.OnChange", ex); }
    }

    public void Dispose() => StopUdp();
}

/// <summary>
/// 내장 생체 신호 시뮬레이터(데모/테스트) — 목표 감정(몰입 · 흥분 · 쾌적 · 공포) → 대역별 진폭 · 전두 알파 비대칭 · 심박/HRV · GSR/SCR · 눈깜빡임 아티팩트를
/// PFBS 패킷으로 직렬화해 파서에 투입(실 디바이스와 동일 경로 · 무할당).
/// </summary>
public sealed class NeuroSignalSimulator
{
    private const int MaxSubjects = NeuroBioSignalEmotionParserEngine.MaxSubjects, Ch = 4, Sr = NeuroBioSignalEmotionParserEngine.DefaultSampleRate;
    private static readonly float[] Freq = { 6f, 10f, 20f, 38f };
    private readonly float[] _target = new float[MaxSubjects * 4];
    private readonly double[] _phase = new double[MaxSubjects * 4];
    private readonly double[] _carry = new double[MaxSubjects];
    private readonly double[] _nextBeat = new double[MaxSubjects];
    private readonly double[] _nextGsr = new double[MaxSubjects];
    private readonly float[] _scrPulse = new float[MaxSubjects];
    private readonly int[] _blink = new int[MaxSubjects];
    private readonly ushort[] _device = new ushort[MaxSubjects];
    private readonly float[] _frame = new float[Ch * 256];
    private readonly byte[] _pkt = new byte[NeuroBioSignalEmotionParserEngine.HeaderSize + Ch * 256 * 2];
    private uint _rng = 0x2545F491u, _seq;
    private int _count;
    private double _clock;

    public float BlinkProbability { get; set; } = 0.0004f;
    public int Count => _count;

    public void SetCount(int n, ushort baseDevice = 1000)
    {
        _count = Math.Clamp(n, 0, MaxSubjects);
        for (var i = 0; i < _count; i++)
        {
            _device[i] = (ushort)(baseDevice + i);
            if (_target[i * 4] == 0 && _target[i * 4 + 1] == 0) SetTarget(i, 0.5f, 0.4f, 0.5f, 0.2f);
        }
    }

    public ushort Device(int i) => _device[i];

    public void SetTarget(int i, float engagement, float arousal, float valence, float fear)
    {
        _target[i * 4] = Math.Clamp(engagement, 0f, 1f); _target[i * 4 + 1] = Math.Clamp(arousal, 0f, 1f);
        _target[i * 4 + 2] = Math.Clamp(valence, 0f, 1f); _target[i * 4 + 3] = Math.Clamp(fear, 0f, 1f);
    }

    private float Rand() { var x = _rng; x ^= x << 13; x ^= x >> 17; x ^= x << 5; _rng = x; return (x & 0xFFFFFF) / 16777216f; }
    private float Gauss() => (Rand() + Rand() + Rand() + Rand() - 2f) * 1.732f;

    /// <summary>dt 초 진행 → 피험자별 EEG/RR/GSR 패킷을 파서에 투입(무할당).</summary>
    public void Step(NeuroBioSignalEmotionParserEngine engine, double dtSec)
    {
        _clock += dtSec;
        var t = (long)(_clock * 1e6);
        for (var i = 0; i < _count; i++)
        {
            float eng = _target[i * 4], aro = _target[i * 4 + 1], val = _target[i * 4 + 2], fear = _target[i * 4 + 3];
            float aTheta = 2f + 6f * (1f - eng), aAlpha = 4f + 9f * (1f - aro), aBeta = 3f + 9f * eng * (0.6f + 0.6f * aro), aGamma = 1f + 3f * fear;
            var asym = 0.35f * (2f * val - 1f);
            _carry[i] += dtSec * Sr;
            var frames = Math.Min(256, (int)_carry[i]);
            _carry[i] -= frames;
            for (var f = 0; f < frames; f++)
            {
                var o = i * 4;
                for (var b = 0; b < 4; b++)
                {
                    _phase[o + b] += Math.Tau * Freq[b] / Sr;
                    if (_phase[o + b] > Math.Tau) _phase[o + b] -= Math.Tau;
                }
                if (_blink[i] <= 0 && Rand() < BlinkProbability) _blink[i] = 25;
                var blink = _blink[i] > 0 ? 260f : 0f;
                if (_blink[i] > 0) _blink[i]--;
                for (var c = 0; c < Ch; c++)
                {
                    var alphaAmp = aAlpha * (c == 2 ? 1f + asym : c == 1 ? 1f - asym : 1f);
                    var ph = c * 0.3;
                    var v = aTheta * (float)Math.Sin(_phase[o] + ph) + alphaAmp * (float)Math.Sin(_phase[o + 1] + ph)
                        + aBeta * (float)Math.Sin(_phase[o + 2] + ph) + aGamma * (float)Math.Sin(_phase[o + 3] + ph) + Gauss() * 2f + 15f;
                    if (c is 1 or 2) v += blink;
                    _frame[f * Ch + c] = v;
                }
            }
            if (frames > 0)
            {
                var n = NeuroBioSignalEmotionParserEngine.WriteEegPacket(_pkt, _device[i], _seq++, t, Sr, _frame.AsSpan(0, frames * Ch), Ch);
                engine.IngestBinary(_pkt.AsSpan(0, n));
            }
            if (_clock >= _nextBeat[i])
            {
                var hr = 62f + 38f * aro + 12f * fear;
                var rr = 60000f / hr + Gauss() * (45f * (1f - aro) + 8f);
                _nextBeat[i] = _clock + rr / 1000.0;
                engine.IngestBinary(_pkt.AsSpan(0, NeuroBioSignalEmotionParserEngine.WriteScalarPacket(_pkt, BioPacketKind.Rr, _device[i], _seq++, t, rr)));
            }
            if (_clock >= _nextGsr[i])
            {
                _nextGsr[i] = _clock + 0.1;
                if (fear > 0.55f && Rand() < 0.04f * fear) _scrPulse[i] = 0.6f;
                _scrPulse[i] *= 0.93f;
                var gsr = 2f + 4f * aro + _scrPulse[i] + Gauss() * 0.01f;
                engine.IngestBinary(_pkt.AsSpan(0, NeuroBioSignalEmotionParserEngine.WriteScalarPacket(_pkt, BioPacketKind.Gsr, _device[i], _seq++, t, gsr)));
            }
        }
    }
}
