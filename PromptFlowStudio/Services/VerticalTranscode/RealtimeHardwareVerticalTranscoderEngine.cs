namespace PromptFlowStudio.Services.VerticalTranscode;

public enum TranscodeBackend : byte { None, MediaFoundationHardware, MediaFoundationSoftware, ManagedBaseline }
public enum AudioBackend : byte { None, MediaFoundationAac, SilentAacFallback }

/// <summary>
/// [PromptFlow Studio v17.49.0] 🎞️ 실시간 9:16 H.264/AAC 트랜스코더 엔진(<see cref="EncodeFrame"/> 핫패스 GC 할당 0).
/// 비디오 백엔드 선택: ① Media Foundation 하드웨어 비동기 MFT(DXVA 계열 GPU 인코더) → ② Microsoft H.264 소프트웨어 MFT(CPU) →
/// ③ 관리 코드 <see cref="ManagedBaselineH264Encoder"/>(OS 미디어 스택 부재 · 모바일). 오디오: Microsoft AAC-LC MFT(128 kbps) → 무음 AAC 폴백.
/// 모든 출력은 <see cref="FragmentedMp4Muxer"/> 로 1 초(GOP) 조각 fMP4 에 먹싱 — 기본 출력은 4 MB 세그먼트 스트림(v17.48 분할 업로드 직결).
/// 프레임마다 <see cref="LocalHttpsWebhookBitrateBudgetGuard"/> 가 1 초 비트레이트 · 인코딩 지연을 판정해 QP(관리 코드) / 목표 비트레이트(MF)를 조정하고
/// 지연 누적 시 P_Skip 반복 프레임으로 드롭을 대체한다(관리 코드). MF 경로의 COM 샘플은 비관리 힙(GC 무관)이다.
/// </summary>
public sealed class RealtimeHardwareVerticalTranscoderEngine : IDisposable
{
    private readonly VideoSink _vs;
    private readonly AudioSink _as;
    private byte[] _frame = Array.Empty<byte>(), _pcm = Array.Empty<byte>();
    private readonly byte[] _aac = new byte[64];
    private ManagedBaselineH264Encoder? _managed;
    private MfH264Encoder? _mfv;
    private MfAacEncoder? _mfa;
    private FragmentedMp4Muxer? _mux;
    private Stream? _out;
    private long _frames, _audioSamples, _silentDue;
    private int _lastBytes;

    public RealtimeHardwareVerticalTranscoderEngine() { _vs = new VideoSink(this); _as = new AudioSink(this); }

    public LocalHttpsWebhookBitrateBudgetGuard Guard { get; } = new();
    /// <summary>v17.62 압축 비트스트림 탭(무손실 롤링 링버퍼 등) — 먹싱과 동일 페이로드 복제 통지.</summary>
    public IEncodedStreamTap? Tap { get; set; }
    public TranscodeBackend Backend { get; private set; }
    public AudioBackend Audio { get; private set; }
    public string? FallbackReason { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Fps { get; private set; }
    public bool IsOpen => _mux is not null;
    public long Frames => _frames;
    public long EncodedFrames { get; private set; }
    public long KeyFrames { get; private set; }
    public long EncodedBytes { get; private set; }
    public long AudioFrames { get; private set; }
    public long MuxRejects { get; private set; }
    public double LastEncodeMs { get; private set; }
    public double MaxEncodeMs { get; private set; }
    public FragmentedMp4Muxer? Muxer => _mux;
    public ManagedBaselineH264Encoder? Managed => _managed;
    public static string BackendName(TranscodeBackend b) => b switch
    {
        TranscodeBackend.MediaFoundationHardware => "MF 하드웨어(GPU)",
        TranscodeBackend.MediaFoundationSoftware => "MF 소프트웨어(CPU)",
        TranscodeBackend.ManagedBaseline => "관리 코드 Baseline(폴백)",
        _ => "—",
    };
    public static string AudioName(AudioBackend a) => a switch { AudioBackend.MediaFoundationAac => "AAC-LC 128k(MF)", AudioBackend.SilentAacFallback => "무음 AAC(폴백)", _ => "—" };

    /// <summary>인코딩 세션 시작(비핫패스 · 할당 허용). allowMf = false 면 관리 코드 강제.</summary>
    public bool Open(Stream output, int width, int height, int fps, int targetKbps, bool preferHardware, bool allowMf)
    {
        Close();
        if (!output.CanSeek || !output.CanWrite) { FallbackReason = "탐색 가능한 출력 스트림 필요"; return false; }
        Width = width; Height = height; Fps = fps;
        Guard.Reset(fps, targetKbps, 30);
        FallbackReason = null;
        if (allowMf)
        {
            try
            {
                _mfv = MfH264Encoder.TryCreate(width, height, fps, Guard.MfKbps, preferHardware);
                if (_mfv is not null)
                {
                    Backend = _mfv.Kind == MfEncoderKind.Hardware ? TranscodeBackend.MediaFoundationHardware : TranscodeBackend.MediaFoundationSoftware;
                    if (preferHardware && _mfv.Kind != MfEncoderKind.Hardware) FallbackReason = _mfv.FailReason ?? "하드웨어 인코더 없음 → CPU";
                    _mfa = MfAacEncoder.TryCreate();
                }
                else FallbackReason = "Media Foundation 사용 불가 → 관리 코드";
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                PromptFlowStudio.Program.Log("Transcoder.Open", ex);
                _mfv?.Dispose(); _mfv = null; _mfa?.Dispose(); _mfa = null;
                FallbackReason = "Media Foundation 예외 → 관리 코드";
            }
        }
        if (_mfv is null)
        {
            _managed = new ManagedBaselineH264Encoder(width, height, fps) { Qp = Guard.Qp };
            Backend = TranscodeBackend.ManagedBaseline;
            if (_frame.Length < _managed.MaxFrameBytes) _frame = new byte[_managed.MaxFrameBytes];
        }
        Audio = _mfa is not null ? AudioBackend.MediaFoundationAac : AudioBackend.SilentAacFallback;
        var spf = 48000 / fps;
        if (_pcm.Length < spf * 4) _pcm = new byte[spf * 4];
        _out = output;
        _mux = new FragmentedMp4Muxer(output, width, height, fps);
        Tap?.OnSessionStart(width, height, fps);
        _frames = 0; _audioSamples = 0; _silentDue = 0; EncodedFrames = 0; KeyFrames = 0; EncodedBytes = 0; AudioFrames = 0; MuxRejects = 0; MaxEncodeMs = 0;
        return true;
    }

    /// <summary>프레임 1개 인코딩(NV12 · 스테레오 float) — 무할당.</summary>
    public void EncodeFrame(ReadOnlySpan<byte> y, ReadOnlySpan<byte> uv, ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (_mux is null) return;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _lastBytes = 0;
        var skipped = false;
        var dur = 10_000_000L / Fps;
        if (_managed is not null)
        {
            _managed.Qp = Guard.Qp;
            var key = _frames % Fps == 0 || !Guard.DropNext;
            skipped = !key;
            var n = key ? _managed.EncodeIdr(y, uv, _frame) : _managed.EncodeSkip(_frame);
            _vs.OnSample(_frame.AsSpan(0, n), _frames * dur);
        }
        else if (_mfv is not null)
        {
            if (Guard.MfKbps != _mfv.BitrateKbps) _mfv.SetBitrate(Guard.MfKbps);
            _mfv.Encode(y, uv, _frames * dur, dur, _vs);
        }
        EncodeAudio(left, right);
        _frames++;
        var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        LastEncodeMs = ms;
        if (ms > MaxEncodeMs && _frames > 2) MaxEncodeMs = ms;
        Guard.OnFrame(_lastBytes, ms, skipped);
    }

    private void EncodeAudio(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        var n = Math.Min(left.Length, right.Length);
        if (_mfa is not null)
        {
            for (var i = 0; i < n; i++)
            {
                var l = (short)Math.Clamp((int)(left[i] * 32767f), -32768, 32767); var r = (short)Math.Clamp((int)(right[i] * 32767f), -32768, 32767);
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(_pcm.AsSpan(i * 4), l);
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(_pcm.AsSpan(i * 4 + 2), r);
            }
            _mfa.Encode(_pcm.AsSpan(0, n * 4), _audioSamples * 10_000_000L / 48000, _as);
        }
        else
        {
            _silentDue += n;
            while (_silentDue >= FragmentedMp4Muxer.AacFrame)
            {
                _silentDue -= FragmentedMp4Muxer.AacFrame;
                var k = FragmentedMp4Muxer.WriteSilentAacFrame(_aac);
                _as.OnSample(_aac.AsSpan(0, k), 0);
            }
        }
        _audioSamples += n;
    }

    /// <summary>인코더 드레인 · 잔여 조각 · 길이 역기록 → 출력 스트림(위치 0) 반환. 세션 종료.</summary>
    public Stream? Finish()
    {
        if (_mux is null) return null;
        _mfv?.Finish(_vs);
        _mfa?.Finish(_as);
        _mux.Finish();
        var o = _out;
        if (o is not null) o.Position = 0;
        DisposeCodecs();
        _mux = null; _out = null;
        return o;
    }

    /// <summary>세션 폐기(출력 스트림 미반환 시 호출자가 처분).</summary>
    public void Close()
    {
        DisposeCodecs();
        _mux = null; _out = null;
    }

    private void DisposeCodecs()
    {
        _mfv?.Dispose(); _mfv = null;
        _mfa?.Dispose(); _mfa = null;
        _managed = null;
    }

    private void OnVideo(ReadOnlySpan<byte> annexB)
    {
        var key = false;
        var rest = annexB;
        while (FragmentedMp4Muxer.NextNal(ref rest, out var nal)) if (!nal.IsEmpty && (nal[0] & 31) == 5) { key = true; break; }
        if (_mux!.AddVideo(annexB, key)) { EncodedFrames++; if (key) KeyFrames++; } else MuxRejects++;
        Tap?.OnVideo(annexB, key);
        EncodedBytes += annexB.Length;
        _lastBytes += annexB.Length;
    }

    private void OnAudio(ReadOnlySpan<byte> raw)
    {
        if (_mux!.AddAudio(raw)) AudioFrames++; else MuxRejects++;
        Tap?.OnAudio(raw);
    }

    private sealed class VideoSink : IMfSampleSink
    {
        private readonly RealtimeHardwareVerticalTranscoderEngine _e;
        public VideoSink(RealtimeHardwareVerticalTranscoderEngine e) => _e = e;
        public void OnSample(ReadOnlySpan<byte> data, long time100ns) => _e.OnVideo(data);
    }

    private sealed class AudioSink : IMfSampleSink
    {
        private readonly RealtimeHardwareVerticalTranscoderEngine _e;
        public AudioSink(RealtimeHardwareVerticalTranscoderEngine e) => _e = e;
        public void OnSample(ReadOnlySpan<byte> data, long time100ns) => _e.OnAudio(data);
    }

    public void Dispose() => Close();
}
