using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.AI.Wan;

/// <summary>오디오 프레임 1건 분석 결과.</summary>
/// <remarks>v12.2.0: 값 형식(readonly record struct) - 프레임당 힙 할당 제거(속성 API 무변경).</remarks>
public readonly record struct AudioFrameFeatures(long FrameIndex, double Amplitude, double DominantFrequencyHz, bool BeatDetected, double ElapsedMs);

/// <summary>누적 관제 스냅샷.</summary>
public sealed record AudioSyncReport(long FramesAnalyzed, long BeatsDetected, long MotionInjections, double LastAmplitude, double LastFrequencyHz);

/// <summary>
/// [PromptFlow Studio v11.0.0] 멀티모달 오디오-비디오 동기화 엔진 - 16비트 PCM 모노 오디오 프레임을
///   Span 기반으로 float 로 변환(무할당)해 진폭(RMS)과 반복형 radix-2 FFT(Cooley-Tukey, 순수 BCL 스칼라 산술)로
///   도미넌트 주파수를 산출한다. 에너지 이동평균 대비 급증(비트) 감지 시 WanDynamicMotionController 에 실제
///   줌/팬 모션을 주입한다(계산 후 버려지는 죽은 값 금지 - F1 보간 버그에서 얻은 교훈을 그대로 적용).
///   FFT/특징 추출은 AnalyzeFrameOffloadedAsync 를 통해 HeterogeneousTaskScheduler 의 Secondary 디바이스
///   (iGPU/CPU 스레드풀) 경로로 오프로드 가능 - 내부적으로 동기 경로(AnalyzeFrame)를 그대로 재사용한다
///   (중복 구현 금지).
///   진폭->줌강도 / 주파수->팬방향 매핑은 결정적, 재현 가능한 명시적 규칙이다 - 실제 "음악적 느낌" 분석이 아니라
///   이 프로젝트 전역의 스텁 정합 기조와 동일한 수준의 자리표시자임을 명확히 한다.
/// </summary>
public sealed class MultimodalAudioSyncEngine
{
    private const int WindowSamples = 1024;
    private const double BeatEnergyRatio = 1.5;
    private const double EnergyEwmaAlpha = 0.2;

    private readonly WanDynamicMotionController _motion;
    private readonly HeterogeneousTaskScheduler _scheduler;
    private readonly object _gate = new();
    // v12.2.0: 이전의 인스턴스 공유 _re/_im 버퍼는 락 밖에서 쓰여 동기 AnalyzeFrame 과 AnalyzeFrameOffloadedAsync(Secondary
    //   스레드)가 겹치면 서로의 FFT 입력을 덮어썼다(결과 오염). 호출별 ArrayPool 대여로 교체 - 스레드 안전 + 안정상태 무할당.
    // 회전인자(twiddle) 사전 계산표: W_N^k = e^{-2πik/N}, k=0..N/2-1. 스테이지별 cos/sin 재계산·점화식 오차 누적 제거.
    private static readonly float[] TwCos = BuildTwiddle(cos: true);
    private static readonly float[] TwSin = BuildTwiddle(cos: false);
    private double _energyEwma;
    private long _framesAnalyzed, _beats, _injections;
    private double _lastAmplitude, _lastFreqHz;
    private long _actFrame;
    private bool _actBeat;
    private bool _actionIsFrame;
    private string? _lastAction;

    public event Action? OnChange;
    /// <summary>v12.2.0-Phase3: 프레임 분석 직후 특징값(struct)을 그대로 전달 - Report(레코드 할당) 재조회 없이 무할당 구독.</summary>
    public event Action<AudioFrameFeatures>? OnFrameAnalyzed;
    public string? LastAction
    {
        get
        {
            lock (_gate)
            {
                if (_actionIsFrame)
                {
                    _lastAction = "프레임 #" + _actFrame + " - Amp=" + _lastAmplitude.ToString("0.###") + " - " + _lastFreqHz.ToString("0") + "Hz" + (_actBeat ? " - BEAT" : "");
                    _actionIsFrame = false;
                }
                return _lastAction;
            }
        }
        private set { lock (_gate) { _lastAction = value; _actionIsFrame = false; } }
    }

    private static float[] BuildTwiddle(bool cos)
    {
        var t = new float[WindowSamples / 2];
        for (var k = 0; k < t.Length; k++)
        {
            var ang = -2.0 * Math.PI * k / WindowSamples;
            t[k] = (float)(cos ? Math.Cos(ang) : Math.Sin(ang));
        }
        return t;
    }
    public long FramesAnalyzed => Interlocked.Read(ref _framesAnalyzed);
    public long BeatsDetected => Interlocked.Read(ref _beats);
    public long MotionInjections => Interlocked.Read(ref _injections);
    public AudioSyncReport Report { get { lock (_gate) return new AudioSyncReport(FramesAnalyzed, BeatsDetected, MotionInjections, _lastAmplitude, _lastFreqHz); } }

    public MultimodalAudioSyncEngine(WanDynamicMotionController motion, HeterogeneousTaskScheduler scheduler)
    {
        _motion = motion;
        _scheduler = scheduler;
    }

    /// <summary>16비트 PCM 모노 바이트 프레임 1건 분석 - Span 무할당 변환+RMS+FFT 도미넌트 주파수, 비트 판정 시
    ///   WanDynamicMotionController 에 줌/팬 모션을 실제로 주입한다.</summary>
    public AudioFrameFeatures AnalyzeFrame(ReadOnlySpan<byte> pcm16Mono, int sampleRateHz)
    {
        var t0 = Stopwatch.GetTimestamp();   // v12.2.0: Stopwatch 인스턴스 할당 제거
        var n = Math.Min(pcm16Mono.Length / 2, WindowSamples);

        var reArr = ArrayPool<float>.Shared.Rent(WindowSamples);
        var imArr = ArrayPool<float>.Shared.Rent(WindowSamples);
        double amplitude, freqHz;
        try
        {
            var re = reArr.AsSpan(0, WindowSamples);
            var im = imArr.AsSpan(0, WindowSamples);

            // 리틀엔디안 호스트는 바이트→short 를 복사 없이 재해석(Zero-Copy), 빅엔디안은 기존 수동 조립 경로.
            double sumSq = 0;
            if (BitConverter.IsLittleEndian)
            {
                var samples = MemoryMarshal.Cast<byte, short>(pcm16Mono[..(n * 2)]);
                for (var i = 0; i < samples.Length; i++)
                {
                    var f = samples[i] / 32768f;
                    re[i] = f;
                    sumSq += (double)f * f;
                }
            }
            else
            {
                for (var i = 0; i < n; i++)
                {
                    var f = (short)(pcm16Mono[i * 2] | (pcm16Mono[i * 2 + 1] << 8)) / 32768f;
                    re[i] = f;
                    sumSq += (double)f * f;
                }
            }
            re[n..].Clear();
            im.Clear();

            amplitude = n > 0 ? Math.Sqrt(sumSq / n) : 0.0;

            FftInPlace(re, im);
            var dominantBin = DominantBin(re, im, WindowSamples / 2);
            freqHz = dominantBin * (double)sampleRateHz / WindowSamples;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(reArr);
            ArrayPool<float>.Shared.Return(imArr);
        }

        bool beat;
        lock (_gate)
        {
            beat = _energyEwma > 0 && amplitude > _energyEwma * BeatEnergyRatio;
            _energyEwma = _energyEwma <= 0 ? amplitude : _energyEwma * (1 - EnergyEwmaAlpha) + amplitude * EnergyEwmaAlpha;
        }

        if (beat) InjectMotionForBeat(amplitude, freqHz);

        var frameIndex = Interlocked.Increment(ref _framesAnalyzed);
        if (beat) Interlocked.Increment(ref _beats);
        var elapsedMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        lock (_gate)
        {
            _lastAmplitude = amplitude;
            _lastFreqHz = freqHz;
            _actFrame = frameIndex; _actBeat = beat; _actionIsFrame = true;   // 문자열은 LastAction 조회 시 생성
        }
        var view = new AudioFrameFeatures(frameIndex, amplitude, freqHz, beat, elapsedMs);
        OnFrameAnalyzed?.Invoke(view);   // v12.2.0-Phase3: 값 형식 그대로 전달(무할당) - StreamerReactionEngine 구독점
        OnChange?.Invoke();
        return view;
    }

    /// <summary>오디오 FFT/특징 추출을 HeterogeneousTaskScheduler 의 Secondary 디바이스(iGPU/CPU 스레드풀)로 오프로드.
    ///   async 경계 제약으로 배열을 받는다(WanPostProcessingEngine 과 동일 사유) - 내부에서는 기존 동기
    ///   AnalyzeFrame(Zero-Alloc Span 경로)을 그대로 재사용한다(중복 구현 금지).</summary>
    public Task<AudioFrameFeatures> AnalyzeFrameOffloadedAsync(byte[] pcm16Mono, int sampleRateHz, CancellationToken ct = default)
        => _scheduler.RunOnSecondaryAsync(_ => Task.FromResult(AnalyzeFrame(pcm16Mono, sampleRateHz)), "AudioFftFeatureExtraction", ct);

    private void InjectMotionForBeat(double amplitude, double freqHz)
    {
        var zoomMag = Math.Clamp(amplitude * 2.0, 0, 1);
        _motion.SetZoom(ZoomDirection.In, zoomMag);
        var panDir = freqHz >= 1000 ? PanDirection.Right : freqHz >= 300 ? PanDirection.Up : PanDirection.Left;
        var panMag = Math.Clamp(freqHz / 4000.0, 0, 1);
        _motion.SetPan(panDir, panMag);
        _motion.InjectNext();
        Interlocked.Increment(ref _injections);
    }

    private static void FftInPlace(Span<float> re, Span<float> im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        // v12.2.0: 스테이지 len 의 k번째 회전인자 = W_N^{k·N/len} → 사전 계산표 조회(스테이지마다 Math.Cos/Sin 호출·점화식 제거).
        for (var len = 2; len <= n; len <<= 1)
        {
            var half = len / 2;
            var step = n / len;
            for (var i = 0; i < n; i += len)
            {
                for (var k = 0; k < half; k++)
                {
                    var wRe = TwCos[k * step];
                    var wIm = TwSin[k * step];
                    var uRe = re[i + k]; var uIm = im[i + k];
                    var xRe = re[i + k + half]; var xIm = im[i + k + half];
                    var vRe = xRe * wRe - xIm * wIm;
                    var vIm = xRe * wIm + xIm * wRe;
                    re[i + k] = uRe + vRe; im[i + k] = uIm + vIm;
                    re[i + k + half] = uRe - vRe; im[i + k + half] = uIm - vIm;
                }
            }
        }
    }

    private static int DominantBin(ReadOnlySpan<float> re, ReadOnlySpan<float> im, int halfN)
    {
        var bestBin = 1;
        var bestMag = 0f;
        for (var k = 1; k < halfN; k++)
        {
            var mag = re[k] * re[k] + im[k] * im[k];
            if (mag > bestMag) { bestMag = mag; bestBin = k; }
        }
        return bestBin;
    }

    public void ResetAll()
    {
        lock (_gate) { _energyEwma = 0; _lastAmplitude = 0; _lastFreqHz = 0; LastAction = "오디오 동기화 엔진 초기화"; }
        Interlocked.Exchange(ref _framesAnalyzed, 0);
        Interlocked.Exchange(ref _beats, 0);
        Interlocked.Exchange(ref _injections, 0);
        OnChange?.Invoke();
    }
}