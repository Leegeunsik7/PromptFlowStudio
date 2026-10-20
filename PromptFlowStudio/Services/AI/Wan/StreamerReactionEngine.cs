using System;
using System.Diagnostics;
using System.Threading;

namespace PromptFlowStudio.Services.AI.Wan;

/// <summary>
/// [PromptFlow Studio v12.2.0-Phase3] 스트리머 리액션 이펙트 엔진 — <see cref="MultimodalAudioSyncEngine.OnFrameAnalyzed"/>
///   (진폭·도미넌트 주파수·비트)를 구독해 반응 강도(<see cref="ReactionAffect"/>)를 휴리스틱 분류하고, 10ms 고정 스텝으로
///   <see cref="ReactionOverlayPipeline"/>(파티클·뱃지·바운스)을 구동한다.
///   ▸ 오디오 스레드 → 스텝 스레드 전달은 락 없는 누적(Interlocked Add/Max) - 오디오 콜백이 렌더 스텝을 기다리지 않는다.
///   ▸ 10ms 는 "시뮬레이션 고정 스텝"이다. Windows 기본 타이머 해상도(약 15.6ms)에선 콜백 간격이 10ms 보다 길 수 있으므로
///     경과시간 누산기로 밀린 스텝을 따라잡아(최대 <see cref="MaxCatchUpMs"/>) 연출 시간축은 벽시계와 일치시킨다.
///   ▸ 핫패스(오디오 콜백·스텝·래스터화) 힙 할당 0. OnChange 는 250ms(25스텝) 또는 반응 단계 변화 시에만 발행(HUD 고빈도 억제).
///   ▸ <see cref="InteractiveReactionEngine"/>(OBS 이벤트→모션/LoRA)와는 입력원이 다른 별개 경로 - 기존 엔진 무변경(Additive-Only).
/// </summary>
public sealed class StreamerReactionEngine : IDisposable, IVirtualCamOverlay
{
    public const int TickMs = 10;
    public const double MaxCatchUpMs = 200;
    private const int NotifyEveryTicks = 25;
    private const double EwmaAlpha = 0.1;

    private static readonly string[] AffectLabels = { "😌 Calm", "🙂 Engaged", "🤩 Excited", "🔥 HYPE" };

    private readonly MultimodalAudioSyncEngine _audio;
    private readonly ReactionOverlayPipeline _pipeline;
    private readonly Func<long> _clock;
    private readonly object _stepGate = new();
    private readonly object _audioGate = new();
    private System.Threading.Timer? _timer;
    private int _inTick;
    private long _lastTimestamp;
    private double _accumMs;
    private double _ampEwma;

    // 오디오 스레드가 누적, 스텝 스레드가 Exchange 로 소비.
    private int _pendingSpawn;
    private int _pendingStrengthMilli;
    private int _pendingAffect;

    private long _ticks, _audioFrames, _impulses;
    private int _ticksSinceNotify;
    private ReactionAffect _lastNotifiedAffect;
    private bool _disposed;

    public event Action? OnChange;

    public StreamerReactionEngine(MultimodalAudioSyncEngine audio)
        : this(audio, new ReactionOverlayPipeline(), Stopwatch.GetTimestamp) { }

    /// <summary>테스트용: 파이프라인(시드/용량)과 시계 주입.</summary>
    internal StreamerReactionEngine(MultimodalAudioSyncEngine audio, ReactionOverlayPipeline pipeline, Func<long> clock)
    {
        _audio = audio;
        _pipeline = pipeline;
        _clock = clock;
        _audio.OnFrameAnalyzed += OnAudioFrame;
    }

    public bool Running => Volatile.Read(ref _timer) is not null;
    public long Ticks => Interlocked.Read(ref _ticks);
    public long AudioFramesObserved => Interlocked.Read(ref _audioFrames);
    public long ImpulsesQueued => Interlocked.Read(ref _impulses);
    public int Capacity => _pipeline.Capacity;
    /// <summary>v12.2.0-Phase4: 그릴 파티클이 있는지(무락 int 읽기 - 한 프레임 늦은 판정은 무해). false 면 가상카메라는 복사 없이 통과.</summary>
    public bool HasVisibleContent => _pipeline.ActiveParticles > 0;
    public ReactionOverlayFrame Snapshot { get { lock (_stepGate) return _pipeline.Snapshot; } }
    public long ParticlesSpawned { get { lock (_stepGate) return _pipeline.Spawned; } }
    public long ParticlesDropped { get { lock (_stepGate) return _pipeline.Dropped; } }
    public static string AffectLabel(ReactionAffect a) => AffectLabels[(int)a];

    /// <summary>진폭/비트 → 반응 강도 휴리스틱(결정적). loudRatio = 현재 진폭 / 진폭 EWMA.</summary>
    internal static ReactionAffect Classify(double amplitude, double loudRatio, bool beat)
    {
        if (beat && amplitude >= 0.35) return ReactionAffect.Hype;
        if (beat || (loudRatio >= 1.3 && amplitude >= 0.2)) return ReactionAffect.Excited;
        if (amplitude >= 0.08) return ReactionAffect.Engaged;
        return ReactionAffect.Calm;
    }

    // 오디오 분석 스레드(동기/오프로드 어느 쪽이든) - 무할당·스텝 비대기.
    private void OnAudioFrame(AudioFrameFeatures f)
    {
        Interlocked.Increment(ref _audioFrames);
        double ratio;
        lock (_audioGate)
        {
            ratio = f.Amplitude / Math.Max(_ampEwma, 1e-4);
            _ampEwma = _ampEwma <= 0 ? f.Amplitude : _ampEwma * (1 - EwmaAlpha) + f.Amplitude * EwmaAlpha;
        }
        var affect = Classify(f.Amplitude, ratio, f.BeatDetected);
        if (affect < ReactionAffect.Excited) return;   // 파티클 분출은 Excited 이상만(Engaged 이하는 자연 감쇠)

        var weight = affect == ReactionAffect.Hype ? 2 : 1;
        var spawn = (int)Math.Clamp(f.Amplitude * 120, 4, 96) * weight;
        var strengthMilli = (int)(Math.Clamp(f.Amplitude * 2, 0, 1) * 1000);
        Interlocked.Add(ref _pendingSpawn, spawn);
        InterlockedMax(ref _pendingStrengthMilli, strengthMilli);
        InterlockedMax(ref _pendingAffect, (int)affect);
        Interlocked.Increment(ref _impulses);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        var cur = Volatile.Read(ref target);
        while (value > cur)
        {
            var seen = Interlocked.CompareExchange(ref target, value, cur);
            if (seen == cur) return;
            cur = seen;
        }
    }

    /// <summary>경과시간만큼 10ms 고정 스텝을 진행(최대 <see cref="MaxCatchUpMs"/> 까지 따라잡기, 나머지는 다음 호출로 이월).
    ///   대기 중인 오디오 임펄스는 첫 스텝에서 1회 소비. 반환값 = 이번 호출에서 실행한 스텝 수.</summary>
    public int Advance(double elapsedMs)
    {
        if (elapsedMs <= 0) return 0;
        int steps = 0;
        bool notify;
        lock (_stepGate)
        {
            using var gcGuard = PromptFlowStudio.Services.Diagnostics.ZeroGcRegionScope.Enter("StreamerReaction.Advance");   // v12.3.0: 스텝 핫패스 할당 회귀 즉시 검출
            _accumMs = Math.Min(_accumMs + elapsedMs, MaxCatchUpMs);
            while (_accumMs >= TickMs)
            {
                _accumMs -= TickMs;
                var spawn = Interlocked.Exchange(ref _pendingSpawn, 0);
                var strength = Interlocked.Exchange(ref _pendingStrengthMilli, 0) / 1000f;
                var affect = (ReactionAffect)Interlocked.Exchange(ref _pendingAffect, 0);
                var impulse = new ReactionImpulse(spawn, strength, affect);
                _pipeline.Step(impulse);
                steps++;
            }
            _ticksSinceNotify += steps;
            var cur = _pipeline.Affect;
            notify = steps > 0 && (_ticksSinceNotify >= NotifyEveryTicks || cur != _lastNotifiedAffect);
            if (notify) { _ticksSinceNotify = 0; _lastNotifiedAffect = cur; }
        }
        if (steps > 0) Interlocked.Add(ref _ticks, steps);
        if (notify) OnChange?.Invoke();
        return steps;
    }

    /// <summary>RGBA32 프레임에 현재 오버레이를 가산 합성(가상카메라 송출 직전 결선용). 그린 파티클 수 반환.</summary>
    public int RenderOverlayInto(Span<byte> rgba, int width, int height)
    {
        lock (_stepGate) return _pipeline.RenderInto(rgba, width, height);
    }

    public int CopyParticles(Span<OverlayParticle> destination)
    {
        lock (_stepGate) return _pipeline.CopyParticles(destination);
    }

    /// <summary>10ms 주기 타이머 기동(실제 콜백 간격은 OS 타이머 해상도에 따름 - 누산기가 보정).</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Volatile.Read(ref _timer) is not null) return;
        _lastTimestamp = _clock();
        var t = new System.Threading.Timer(OnTimer, null, TickMs, TickMs);
        if (Interlocked.CompareExchange(ref _timer, t, null) is not null) t.Dispose();
        else PromptFlowStudio.Services.Diagnostics.GcLatencySession.Acquire();   // v12.3.0: 루프 가동 동안 블로킹 gen2 억제
        OnChange?.Invoke();
    }

    public void Stop()
    {
        var t = Interlocked.Exchange(ref _timer, null);
        if (t is null) return;
        t.Dispose();
        PromptFlowStudio.Services.Diagnostics.GcLatencySession.Release();
        OnChange?.Invoke();
    }

    private void OnTimer(object? _)
    {
        if (Interlocked.CompareExchange(ref _inTick, 1, 0) != 0) return;   // 느린 틱 중첩 방지
        try
        {
            var now = _clock();
            var elapsed = (now - _lastTimestamp) * 1000.0 / Stopwatch.Frequency;
            _lastTimestamp = now;
            Advance(elapsed);
        }
        catch (Exception ex) { Program.Log("StreamerReaction.Tick", ex); }
        finally { Volatile.Write(ref _inTick, 0); }
    }

    /// <summary>수동 트리거(HUD 데모·테스트) - 오디오 없이 지정 강도의 임펄스를 대기열에 적재.</summary>
    public void TriggerManual(ReactionAffect affect, double amplitude)
    {
        var spawn = (int)Math.Clamp(amplitude * 120, 4, 96) * (affect == ReactionAffect.Hype ? 2 : 1);
        Interlocked.Add(ref _pendingSpawn, spawn);
        InterlockedMax(ref _pendingStrengthMilli, (int)(Math.Clamp(amplitude * 2, 0, 1) * 1000));
        InterlockedMax(ref _pendingAffect, (int)affect);
        Interlocked.Increment(ref _impulses);
    }

    public void ResetAll()
    {
        lock (_stepGate) { _pipeline.Reset(); _accumMs = 0; _ticksSinceNotify = 0; _lastNotifiedAffect = ReactionAffect.Calm; }
        lock (_audioGate) _ampEwma = 0;
        Interlocked.Exchange(ref _pendingSpawn, 0);
        Interlocked.Exchange(ref _pendingStrengthMilli, 0);
        Interlocked.Exchange(ref _pendingAffect, 0);
        Interlocked.Exchange(ref _ticks, 0);
        Interlocked.Exchange(ref _audioFrames, 0);
        Interlocked.Exchange(ref _impulses, 0);
        OnChange?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _audio.OnFrameAnalyzed -= OnAudioFrame;
        Stop();
    }
}
