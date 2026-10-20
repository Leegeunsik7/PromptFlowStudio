using PromptFlowStudio.Services.Sound;

namespace PromptFlowStudio.Services.AudioTelemetry;

/// <summary>[v17.29.0] 오디오 텔레메트리 1프레임 스냅샷(값 타입 · 박싱 0).</summary>
public readonly struct AudioTelemetrySnapshot
{
    public readonly long Frame;
    public readonly float Rms;
    public readonly float Peak;
    public readonly float RmsDb;
    public readonly int ZeroCrossings;
    public readonly bool Clipping;

    public AudioTelemetrySnapshot(long frame, float rms, float peak, float rmsDb, int zeroCrossings, bool clipping)
    {
        Frame = frame; Rms = rms; Peak = peak; RmsDb = rmsDb; ZeroCrossings = zeroCrossings; Clipping = clipping;
    }
}

/// <summary>
/// [PromptFlow Studio v17.29.0] 🎚 실시간 오디오/텔레메트리 모니터 뼈대.
/// 호스트(캡처·합성기)가 <see cref="Push"/> 로 모노 PCM 을 링 버퍼에 넣으면, 타이머 틱(기본 100 ms ≤ 250 ms)마다
/// RMS·피크·영교차와 Hann 창 FFT 로그 밴드 스펙트럼(8-bit, 0 = <see cref="FloorDb"/> dBFS · 255 = 0 dBFS)을 계산한다.
///   · DSP 는 레포 내 검증 자산(<see cref="FftPlan"/> · <see cref="SoundDsp"/>) 재사용. 밴드 규약은 시너지 공용 SoundStack.Core.SpectrumFrameBuilder 와 동일
///     → 공용 ZeroAlloc.Dsp 모듈화 시 <see cref="Analyze"/> 내부만 교체(결선점).
///   · 입력이 없으면 <see cref="SyntheticTone"/> 로 결정적 테스트 톤을 주입(데모/회귀용).
///   ⚠ 무할당: 링·창·FFT·밴드 테이블은 생성자 1회 할당. <see cref="Analyze"/> 힙 할당 0. 결선점: 실제 캡처 소스(WASAPI/AudioRecord) → <see cref="Push"/>.
/// </summary>
public sealed class RealtimeAudioTelemetryMonitor : IDisposable
{
    public const int SampleRate = 48000, FftSize = 2048, Bands = 64, TickMs = 100;
    public const float ClipThreshold = 0.999f, FloorDb = -100f, MinHz = 20f;

    private readonly object _gate = new();
    private readonly float[] _ring = new float[FftSize];
    private readonly float[] _pcm = new float[FftSize];
    private readonly float[] _hann = new float[FftSize];
    private readonly float[] _re = new float[FftSize];
    private readonly float[] _im = new float[FftSize];
    private readonly int[] _bandLo = new int[Bands];
    private readonly int[] _bandHi = new int[Bands];
    private readonly byte[] _bands = new byte[Bands];
    private readonly FftPlan _fft = new(FftSize);
    private int _head, _filled, _pcmCount;
    private double _tonePhase;
    private long _frame;
    private System.Threading.Timer? _timer;
    private volatile bool _running;

    public RealtimeAudioTelemetryMonitor(IServiceProvider sp) : this() { }

    public RealtimeAudioTelemetryMonitor()
    {
        for (int i = 0; i < FftSize; i++) _hann[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / FftSize);
        double binHz = (double)SampleRate / FftSize, maxHz = SampleRate / 2.0;
        double ratio = Math.Pow(maxHz / MinHz, 1.0 / Bands);
        int half = FftSize / 2;
        for (int b = 0; b < Bands; b++)
        {
            double lo = MinHz * Math.Pow(ratio, b), hi = lo * ratio;
            int kLo = (int)Math.Ceiling(lo / binHz), kHi = (int)Math.Floor(hi / binHz - 1e-9);
            if (kHi < kLo) kLo = kHi = (int)Math.Round(Math.Sqrt(lo * hi) / binHz); // bin 보다 좁은 저역 밴드 → 중심 bin
            _bandLo[b] = Math.Clamp(kLo, 1, half);
            _bandHi[b] = Math.Clamp(kHi, 1, half);
        }
    }

    public event Action? OnChange;

    public bool IsRunning => _running;
    public bool SyntheticTone { get; set; } = true;
    public float ToneHz { get; set; } = 1000f;
    public float ToneAmp { get; set; } = 0.5f;
    public AudioTelemetrySnapshot Last { get; private set; }
    public long ClipEvents { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>최근 밴드 레벨(0–255, 저역→고역). UI 는 읽기 전용으로만 사용.</summary>
    public ReadOnlySpan<byte> BandLevels => _bands;

    /// <summary>밴드 b 의 기하 중심 주파수(Hz).</summary>
    public static double BandCenterHz(int b) => MinHz * Math.Pow(SampleRate / 2.0 / MinHz, (b + 0.5) / Bands);

    public void Start()
    {
        if (_running) return;
        _running = true;
        _timer = new System.Threading.Timer(static s => ((RealtimeAudioTelemetryMonitor)s!).OnTick(), this, 0, TickMs);
        Raise();
    }

    public void Stop()
    {
        _running = false;
        _timer?.Dispose();
        _timer = null;
        Raise();
    }

    /// <summary>모노 PCM(−1..1)을 링 버퍼에 기록. 캡처 콜백 스레드에서 호출 가능(힙 할당 0).</summary>
    public void Push(ReadOnlySpan<float> mono)
    {
        lock (_gate) Write(mono);
    }

    /// <summary>v17.30: 마지막 Analyze 의 시간순 PCM 을 dst 로 복사(파형 HUD용, 힙 할당 0). 반환 = 복사 샘플 수.</summary>
    public int CopyLastPcm(Span<float> dst)
    {
        lock (_gate)
        {
            int n = Math.Min(_pcmCount, dst.Length);
            new ReadOnlySpan<float>(_pcm, _pcmCount - n, n).CopyTo(dst);
            return n;
        }
    }

    /// <summary>링 버퍼를 시간순으로 펼쳐 지표·스펙트럼을 갱신한다(힙 할당 0).</summary>
    public AudioTelemetrySnapshot Analyze()
    {
        lock (_gate)
        {
            int n = _filled, pad = FftSize - n;
            int start = (_head - n + FftSize) % FftSize;
            for (int i = 0; i < n; i++) _pcm[i] = _ring[(start + i) % FftSize];
            ReadOnlySpan<float> pcm = new(_pcm, 0, n);
            _pcmCount = n;

            float rms = n > 0 ? (float)Math.Sqrt(SoundDsp.Energy(pcm) / n) : 0f;
            float peak = 0f;
            int zc = 0;
            for (int i = 0; i < n; i++)
            {
                float a = MathF.Abs(pcm[i]);
                if (a > peak) peak = a;
                if (i > 0 && (pcm[i] >= 0f) != (pcm[i - 1] >= 0f)) zc++;
            }
            BuildBands(pcm, pad);

            float db = rms > 1e-10f ? SoundDsp.GainToDb(rms) : FloorDb;
            bool clip = peak >= ClipThreshold;
            if (clip) ClipEvents++;
            var snap = new AudioTelemetrySnapshot(++_frame, rms, peak, db, zc, clip);
            Last = snap;
            return snap;
        }
    }

    private void BuildBands(ReadOnlySpan<float> pcm, int pad)
    {
        for (int i = 0; i < pad; i++) { _re[i] = 0f; _im[i] = 0f; }
        for (int i = 0; i < pcm.Length; i++) { _re[pad + i] = pcm[i] * _hann[pad + i]; _im[pad + i] = 0f; }
        _fft.Transform(_re, _im, inverse: false);

        const float ampScale = 4f / FftSize, span = -FloorDb;
        for (int b = 0; b < Bands; b++)
        {
            float best = 0f;
            for (int k = _bandLo[b]; k <= _bandHi[b]; k++)
            {
                float p = _re[k] * _re[k] + _im[k] * _im[k];
                if (p > best) best = p;
            }
            float db = best > 0f ? 20f * MathF.Log10(MathF.Sqrt(best) * ampScale) : FloorDb;
            float v = (db - FloorDb) / span * 255f;
            _bands[b] = v <= 0f ? (byte)0 : v >= 255f ? (byte)255 : (byte)v;
        }
    }

    private void OnTick()
    {
        if (!_running) return;
        try
        {
            if (SyntheticTone) InjectTone(SampleRate * TickMs / 1000);
            Analyze();
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message; // 타이머 예외 격리
        }
        Raise();
    }

    /// <summary>결정적 정현파를 링 버퍼에 주입(스택 청크 · 힙 할당 0).</summary>
    public void InjectTone(int samples)
    {
        Span<float> chunk = stackalloc float[256];
        double step = 2.0 * Math.PI * ToneHz / SampleRate;
        float amp = ToneAmp;
        lock (_gate)
        {
            while (samples > 0)
            {
                int len = Math.Min(samples, chunk.Length);
                for (int i = 0; i < len; i++)
                {
                    chunk[i] = amp * (float)Math.Sin(_tonePhase);
                    _tonePhase += step;
                }
                if (_tonePhase > 2.0 * Math.PI * 1024) _tonePhase %= 2.0 * Math.PI;
                Write(chunk.Slice(0, len));
                samples -= len;
            }
        }
    }

    private void Write(ReadOnlySpan<float> mono)
    {
        if (mono.Length > FftSize) mono = mono.Slice(mono.Length - FftSize);
        for (int i = 0; i < mono.Length; i++)
        {
            _ring[_head] = mono[i];
            _head = (_head + 1) % FftSize;
        }
        _filled = Math.Min(FftSize, _filled + mono.Length);
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch { /* 구독자 예외 격리 */ }
    }

    public void Dispose()
    {
        Stop();
        OnChange = null;
    }
}
