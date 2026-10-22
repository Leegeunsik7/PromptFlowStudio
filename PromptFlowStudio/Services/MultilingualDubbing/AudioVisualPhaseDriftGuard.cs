namespace PromptFlowStudio.Services.MultilingualDubbing;

/// <summary>
/// [PromptFlow Studio v17.82.0] ⏱️ 오디오-비주얼 위상 드리프트 세이프티 가드(<see cref="Tick"/> 무할당 · µs 정수 시계).
///   ▸ 오디오 시계 = 재생 샘플 위치 × 10⁶ / 48 kHz · 립 시계 = 3DGS 립싱크 지오메트리 타임라인 — 드리프트 = 립 − 오디오(µs).
///   ▸ |드리프트| ≤ 15 ms: 비례 트림(보간 계수 1 − 0.1·d/Δt)으로 미세 가감속 — 끊김 없이 0 수렴.
///   ▸ |드리프트| &gt; 15 ms: 보간 계수 = (Δt − d)/Δt 로 해당 프레임 안에서 즉시 정렬(0~3× · 역행 없음 → 스터터 0). 범위 초과분은 다음 프레임 이월.
/// </summary>
public sealed class AudioVisualPhaseDriftGuard
{
    public const int Fs = 48_000;
    public const long ThresholdUs = 15_000, FrameUs = 16_667;
    public const float MaxRate = 3f, SoftGain = 0.1f;

    private long _resyncStart = -1;

    public bool Enabled { get; set; } = true;
    public long AudioClockUs { get; private set; }
    public long LipClockUs { get; private set; }
    public long DriftUs => LipClockUs - AudioClockUs;
    /// <summary>직전 프레임 보간 계수(립 시계 진행 / Δt).</summary>
    public float Rate { get; private set; } = 1f;
    public long Frames { get; private set; }
    public long Corrections { get; private set; }
    public long Carryovers { get; private set; }
    public long Regressions { get; private set; }
    public long MaxAbsDriftUs { get; private set; }
    public long MaxPostDriftUs { get; private set; }
    public long LastResyncFrames { get; private set; }
    public long LastPreDriftUs { get; private set; }

    public void Reset()
    {
        AudioClockUs = LipClockUs = 0; Rate = 1f; Frames = Corrections = Carryovers = Regressions = 0;
        MaxAbsDriftUs = MaxPostDriftUs = LastResyncFrames = LastPreDriftUs = 0; _resyncStart = -1;
    }

    public void ResetStats() { Corrections = Carryovers = Regressions = 0; MaxAbsDriftUs = MaxPostDriftUs = LastResyncFrames = 0; _resyncStart = -1; }

    /// <summary>프레임 틱 — 오디오 재생 샘플 위치 · 이번 프레임 Δt(µs) → 립 시계(µs).</summary>
    public long Tick(long audioSamples, long frameDtUs)
    {
        if (frameDtUs <= 0) frameDtUs = FrameUs;
        AudioClockUs = audioSamples * 1_000_000L / Fs;
        var prev = LipClockUs;
        var drift = prev + frameDtUs - AudioClockUs;
        LastPreDriftUs = drift;
        var abs = Math.Abs(drift);
        if (abs > MaxAbsDriftUs) MaxAbsDriftUs = abs;
        float rate;
        if (!Enabled) rate = 1f;
        else if (abs > ThresholdUs)
        {
            rate = (float)(frameDtUs - drift) / frameDtUs;
            if (_resyncStart < 0) { _resyncStart = Frames; Corrections++; }
            if (rate < 0f || rate > MaxRate) { rate = Math.Clamp(rate, 0f, MaxRate); Carryovers++; }
        }
        else rate = 1f - SoftGain * drift / frameDtUs;
        var step = (long)MathF.Round(frameDtUs * rate);
        if (Enabled && abs > ThresholdUs && rate > 0f && rate < MaxRate) step = AudioClockUs - prev;   // 정수 µs 정확 정렬
        LipClockUs = prev + step;
        if (LipClockUs < prev) { Regressions++; LipClockUs = prev; }
        Rate = (float)(LipClockUs - prev) / frameDtUs;
        Frames++;
        var post = Math.Abs(DriftUs);
        if (_resyncStart >= 0 && post <= ThresholdUs)
        {
            LastResyncFrames = Frames - _resyncStart;
            _resyncStart = -1;
            if (post > MaxPostDriftUs) MaxPostDriftUs = post;
        }
        return LipClockUs;
    }

    /// <summary>오디오 재시작(루프) 시 두 시계 동시 리셋.</summary>
    public void Rewind() { AudioClockUs = LipClockUs = 0; Rate = 1f; _resyncStart = -1; }
}
