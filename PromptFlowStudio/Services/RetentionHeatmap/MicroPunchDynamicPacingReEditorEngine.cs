using PromptFlowStudio.Services.MultilingualDubbing;
using PromptFlowStudio.Services.SmartPublishing;
using PromptFlowStudio.Services.VisualHooks;

namespace PromptFlowStudio.Services.RetentionHeatmap;

/// <summary>마이크로 펀치 1 건 — 원본 타임라인 펀치 시각(절벽 0.3 s 전) · 대상 절벽 시작 · 피크 배율.</summary>
public struct PunchEdit
{
    public int AtMs, SlumpOnsetMs;
    public float Scale;
}

/// <summary>국소 템포 가속 구간 — 원본/출력 타임라인 ms · 배속 · 구간 내 데드에어 ms.</summary>
public struct TempoSegment
{
    public int SrcStartMs, SrcEndMs, OutStartMs, OutEndMs, DeadAirMs;
    public float Rate;
}

/// <summary>
/// [PromptFlow Studio v17.98.0] 🫀 이탈 구간 마이크로 펀치 &amp; 템포 가속 리에디터(<see cref="Plan"/> · <see cref="Repackage"/> · <see cref="ApplyFrame"/> 무할당).
///   ▸ 펀치: 이탈 절벽 시작 <see cref="PunchLeadMs"/> 전 → v17.77 <see cref="KineticZoomPunchCutEngine"/> 1.15× 줌 펀치 컷 + v17.76 <see cref="ProceduralViralSoundFxSynthesizer"/> 임팩트 각성 FX 큐.
///   ▸ 템포: 데드존(지루한 누수 구간) 오디오를 10 ms RMS 프레임으로 스캔해 무음 ≥ 300 ms 데드에어 비율 → 1.05×~1.15× 배속 →
///     v17.82 <see cref="MultilingualDubbingTimeStretchEngine.Stretch"/> WSOLA(배율 = 1 / 배속) 로 피치 보존 압축.
///   ▸ 리패키징: 단일 재사용 작업 버퍼(원본 복사 → SFX 믹스 → 구간별 복사/WSOLA) · 출력 → 원본 시간 사상으로 비디오 프레임 재타이밍 후 줌 적용.
///   ▸ [v17.98.1] 비트스트림: <see cref="RemuxInto"/> = 템포 구간 → 90 kHz 워프 절점(WSOLA 출력 시간 맵과 동일) · 펀치 시각 → <see cref="NaluSmartReMuxer"/> GOP 스트림 복사 +
///     H.264 SPS 크롭 재작성(<see cref="PunchScale"/> 줌 · 재인코딩 0) · PTS/DTS 재타이밍(무할당).
///   ▸ 예상 유지율: 절벽 초 낙폭 × (1 − 0.45) · 템포 구간 낙폭 × (1 − 0.35 × (배속 − 1) / 0.15) 시뮬레이션 곡선.
/// </summary>
public sealed class MicroPunchDynamicPacingReEditorEngine
{
    public const int Fs = MultilingualDubbingTimeStretchEngine.Fs, FrameMs = 10, FrameSamples = Fs / 1000 * FrameMs, PunchLeadMs = 300, EditCap = 16, DeadAirMinMs = 300, VideoFps = KineticZoomPunchCutEngine.Fps;
    public const float PunchScale = 1.15f, MinRate = 1.05f, MaxRate = 1.15f, SilenceRms = 0.01f, PunchRecovery = 0.45f, TempoRecovery = 0.35f;

    private readonly float[] _work;
    private readonly float[] _pred = new float[AudienceRetentionDropoffDetectorEngine.MaxSec + 1];
    private readonly PunchEdit[] _punch = new PunchEdit[EditCap];
    private readonly TempoSegment[] _tempo = new TempoSegment[EditCap];
    private readonly WarpKnot[] _knots = new WarpKnot[EditCap * 2 + 2];
    private readonly int[] _punchSrcMs = new int[EditCap];
    private readonly RemuxCut[] _cut = new RemuxCut[1];
    private int _pn, _tn, _predN, _srcMs, _kn;

    public MicroPunchDynamicPacingReEditorEngine(int maxSec = 60)
    {
        MaxSec = Math.Clamp(maxSec, 1, AudienceRetentionDropoffDetectorEngine.MaxSec);
        _work = new float[MaxSec * Fs];
    }

    public int MaxSec { get; }
    public KineticZoomPunchCutEngine Zoom { get; } = new() { Strength = 1f };
    public ProceduralViralSoundFxSynthesizer Sfx { get; } = new();
    public MultilingualDubbingTimeStretchEngine Wsola { get; } = new();
    public int PunchCount => _pn;
    public int TempoCount => _tn;
    public ref readonly PunchEdit Punch(int i) => ref _punch[i];
    public ref readonly TempoSegment Tempo(int i) => ref _tempo[i];
    public ReadOnlySpan<float> PredictedCurve => _pred.AsSpan(0, _predN);
    public int SourceDurationMs => _srcMs;
    public int OutDurationMs => OutMsAt(_srcMs);
    public int TempoCoveredMs { get; private set; }
    public int DeadAirMs { get; private set; }
    public float PredictedAverage { get; private set; }
    public float PredictedCompletion { get; private set; }
    /// <summary>예상 평균 유지율 상승(%p).</summary>
    public float UpliftPp { get; private set; }
    public long Plans { get; private set; }
    public long Repackages { get; private set; }
    public long FramesRendered { get; private set; }
    public int LastOutSamples { get; private set; }
    public double LastPlanUs { get; private set; }
    public double LastRepackageUs { get; private set; }
    public long Remuxes { get; private set; }
    public double LastRemuxUs { get; private set; }
    /// <summary>최근 <see cref="RemuxInto"/> 워프 절점(90 kHz · 원본 PTS 원점 기준).</summary>
    public ReadOnlySpan<WarpKnot> Knots => _knots.AsSpan(0, _kn);

    /// <summary>
    /// 리에디트 결과를 Annex-B 비트스트림에 적용 — 템포 맵 절점 · 펀치 시각(원점 보정) → <see cref="NaluSmartReMuxer.Remux"/>
    /// (펀치 GOP = SPS 크롭 <see cref="PunchScale"/>× · 재인코딩 0). <paramref name="mux"/>는 Parse/SetTimestamps 완료 상태. 반환 = 출력 바이트(−1 = 버퍼 부족).
    /// </summary>
    public int RemuxInto(NaluSmartReMuxer mux, ReadOnlySpan<byte> src, long originTicks, long frameTicks, Span<byte> dst)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var originMs = (int)((originTicks * 1000 + NaluSmartReMuxer.Clock - 1) / NaluSmartReMuxer.Clock);
        BuildKnots(originTicks);
        for (var i = 0; i < _pn; i++) _punchSrcMs[i] = _punch[i].AtMs + originMs;
        _cut[0] = new RemuxCut(originMs, originMs + _srcMs);
        var w = mux.Remux(src, _cut, _knots.AsSpan(0, _kn), frameTicks, dst, _punchSrcMs.AsSpan(0, _pn), PunchScale);
        Remuxes++;
        LastRemuxUs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
        return w;
    }

    /// <summary>템포 구간 → 리먹서 절점(90 kHz · 원본 PTS 원점 기준) — 비단조 절점 폐기.</summary>
    private void BuildKnots(long origin)
    {
        const long k = NaluSmartReMuxer.Clock / 1000;
        _kn = 0;
        _knots[_kn++] = new WarpKnot(origin, origin + OutMsAt(0) * k);
        for (var i = 0; i < _tn && _kn + 2 < _knots.Length; i++)
        {
            ref readonly var t = ref _tempo[i];
            AddKnot(origin + t.SrcStartMs * k, origin + t.OutStartMs * k);
            AddKnot(origin + t.SrcEndMs * k, origin + t.OutEndMs * k);
        }
        AddKnot(origin + _srcMs * k, origin + OutDurationMs * k);
    }

    private void AddKnot(long src, long outT)
    {
        if (src <= _knots[_kn - 1].Src || outT < _knots[_kn - 1].Out) return;
        _knots[_kn++] = new WarpKnot(src, outT);
    }

    /// <summary>줌 엔진 가중치 — 강도 1.0 에서 피크 배율 = <see cref="PunchScale"/>.</summary>
    public static float PunchWeight => (PunchScale - KineticZoomPunchCutEngine.MinScale) / (KineticZoomPunchCutEngine.MaxScale - KineticZoomPunchCutEngine.MinScale);

    public void Reset()
    {
        Zoom.Clear(); Sfx.ClearCues();
        _pn = _tn = _predN = _srcMs = 0;
        TempoCoveredMs = DeadAirMs = LastOutSamples = 0;
        PredictedAverage = PredictedCompletion = UpliftPp = 0f;
        Plans = Repackages = FramesRendered = 0;
        LastPlanUs = LastRepackageUs = 0;
    }

    /// <summary>
    /// 편집 계획(무할당) — 절벽별 펀치/FX · 데드존별 템포 · 시간 사상 · 예상 곡선. 반환 = 펀치 + 템포 편집 수.
    /// [v18.0.2] <paramref name="punchAtMs"/>(클립 원점 ms · 오름차순)가 있으면 절벽 유도 대신 그 시각에 펀치(숏폼 팩토리 ③ 타임코드 동기화).
    /// </summary>
    public int Plan(AudienceRetentionDropoffDetectorEngine det, ReadOnlySpan<float> audio, ReadOnlySpan<int> punchAtMs = default)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Zoom.Clear(); Sfx.ClearCues();
        _pn = _tn = 0;
        TempoCoveredMs = DeadAirMs = 0;
        _srcMs = Math.Min(det.DurationSec, MaxSec) * 1000;
        var w = PunchWeight;

        if (punchAtMs.Length > 0)
        {
            foreach (var at in punchAtMs)
            {
                if (_pn == EditCap || at >= _srcMs) break;
                if (at >= 0) AddPunchEdit(at, at + PunchLeadMs, w);
            }
        }
        else
        {
            for (var i = 0; i < det.SlumpCount && _pn < EditCap; i++)
            {
                ref readonly var s = ref det.Slump(i);
                if (s.OnsetMs >= _srcMs) break;
                AddPunchEdit(Math.Max(0, s.OnsetMs - PunchLeadMs), s.OnsetMs, w);
            }
        }

        var removed = 0;   // 누적 압축 샘플
        for (var i = 0; i < det.DeadZoneCount && _tn < EditCap; i++)
        {
            ref readonly var z = ref det.DeadZone(i);
            var a = Math.Min(z.StartSec * 1000, _srcMs);
            var b = Math.Min(z.EndSec * 1000, _srcMs);
            if (b - a < 1000) continue;
            var dead = DeadAirIn(audio, a, b);
            var frac = Math.Clamp(dead / (float)(b - a) / 0.5f, 0f, 1f);
            var rate = MinRate + (MaxRate - MinRate) * frac;
            var srcLen = (b - a) * (Fs / 1000);
            var outLen = (int)(srcLen * (1f / rate));
            var outA = a * (Fs / 1000) - removed;
            removed += srcLen - outLen;
            _tempo[_tn++] = new TempoSegment
            {
                SrcStartMs = a, SrcEndMs = b, Rate = rate, DeadAirMs = dead,
                OutStartMs = outA / (Fs / 1000), OutEndMs = (outA + outLen) / (Fs / 1000),
            };
            TempoCoveredMs += b - a;
            DeadAirMs += dead;
        }

        Predict(det);
        Plans++;
        LastPlanUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        return _pn + _tn;
    }

    private void AddPunchEdit(int at, int onset, float w)
    {
        Zoom.AddPunch(at, w, ZoomPunchSource.Manual);
        Sfx.AddCue(at, ViralSfxKind.Impact);
        _punch[_pn++] = new PunchEdit { AtMs = at, SlumpOnsetMs = onset, Scale = KineticZoomPunchCutEngine.ScaleFor(w * Zoom.Strength) };
    }

    /// <summary>오디오 리패키징(무할당) — 작업 버퍼 복사 → 임팩트 FX 믹스 → 구간 복사/WSOLA. 반환 = 출력 샘플 수.</summary>
    public int Repackage(ReadOnlySpan<float> src, Span<float> dst)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var n = Math.Min(src.Length, _work.Length);
        var work = _work.AsSpan(0, n);
        src[..n].CopyTo(work);
        Sfx.Mix(work, 1, 0);

        int pos = 0, outPos = 0;
        for (var i = 0; i < _tn && outPos < dst.Length; i++)
        {
            ref readonly var seg = ref _tempo[i];
            var a = Math.Min(seg.SrcStartMs * (Fs / 1000), n);
            var b = Math.Min(seg.SrcEndMs * (Fs / 1000), n);
            outPos += CopyClamped(work[pos..a], dst[outPos..]);
            var seg0 = work[a..b];
            var o = Wsola.Stretch(seg0, dst[outPos..], 1f / seg.Rate);
            // 첫 합성 홉 = 분석 프레임 0 의 상승 Hann 단독 구간 → 원본 그대로(진폭 딥 제거)
            seg0[..Math.Min(Math.Min(MultilingualDubbingTimeStretchEngine.HopS, o), seg0.Length)].CopyTo(dst[outPos..]);
            outPos += o;
            pos = b;
        }
        if (pos < n && outPos < dst.Length) outPos += CopyClamped(work[pos..n], dst[outPos..]);

        LastOutSamples = outPos;
        Repackages++;
        LastRepackageUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        return outPos;
    }

    /// <summary>출력 프레임 → 원본 프레임 재타이밍 후 줌 펀치 적용(무할당). 반환 = 적용 배율.</summary>
    public float ApplyFrame(ReadOnlySpan<uint> src, Span<uint> dst, int w, int h, long outFrame)
    {
        FramesRendered++;
        return Zoom.Apply(src, dst, w, h, SourceFrameAt(outFrame));
    }

    public long SourceFrameAt(long outFrame) => KineticZoomPunchCutEngine.FrameOf(SourceMsAt((int)(outFrame * 1000 / VideoFps)), VideoFps);

    /// <summary>출력 ms → 원본 ms(템포 구간 = 배속 선형 · 그 외 = 이동).</summary>
    public int SourceMsAt(int outMs)
    {
        int lastSrc = 0, lastOut = 0;
        for (var i = 0; i < _tn; i++)
        {
            ref readonly var s = ref _tempo[i];
            if (outMs < s.OutStartMs) return lastSrc + (outMs - lastOut);
            if (outMs < s.OutEndMs) return s.SrcStartMs + (int)((outMs - s.OutStartMs) * s.Rate);
            lastSrc = s.SrcEndMs; lastOut = s.OutEndMs;
        }
        return lastSrc + (outMs - lastOut);
    }

    /// <summary>원본 ms → 출력 ms.</summary>
    public int OutMsAt(int srcMs)
    {
        int lastSrc = 0, lastOut = 0;
        for (var i = 0; i < _tn; i++)
        {
            ref readonly var s = ref _tempo[i];
            if (srcMs < s.SrcStartMs) return lastOut + (srcMs - lastSrc);
            if (srcMs < s.SrcEndMs) return s.OutStartMs + (int)((srcMs - s.SrcStartMs) / s.Rate);
            lastSrc = s.SrcEndMs; lastOut = s.OutEndMs;
        }
        return lastOut + (srcMs - lastSrc);
    }

    /// <summary>[a, b) ms 구간 데드에어(10 ms RMS &lt; −40 dBFS 연속 ≥ 300 ms) 합계 ms.</summary>
    public static int DeadAirIn(ReadOnlySpan<float> audio, int aMs, int bMs)
    {
        var total = 0; var run = 0;
        for (var t = aMs; t + FrameMs <= bMs; t += FrameMs)
        {
            var s0 = t * (Fs / 1000);
            var silent = true;
            if (s0 + FrameSamples <= audio.Length)
            {
                var e = 0f;
                var fr = audio.Slice(s0, FrameSamples);
                for (var i = 0; i < fr.Length; i++) e += fr[i] * fr[i];
                silent = e / FrameSamples < SilenceRms * SilenceRms;
            }
            if (silent) { run += FrameMs; continue; }
            if (run >= DeadAirMinMs) total += run;
            run = 0;
        }
        if (run >= DeadAirMinMs) total += run;
        return total;
    }

    private void Predict(AudienceRetentionDropoffDetectorEngine det)
    {
        var curve = det.Curve;
        var n = Math.Min(curve.Length, _srcMs / 1000 + 1);
        _predN = n;
        if (n == 0) { PredictedAverage = PredictedCompletion = UpliftPp = 0f; return; }
        _pred[0] = curve[0];
        double area = 0, orig = 0;
        for (var s = 0; s + 1 < n; s++)
        {
            var drop = curve[s] - curve[s + 1];
            if (drop > 0f)
            {
                int c0 = s * 1000, c1 = c0 + 1000;
                for (var i = 0; i < det.SlumpCount; i++)
                {
                    ref readonly var sl = ref det.Slump(i);
                    if (sl.OnsetMs < c1 && sl.EndMs > c0) { drop *= 1f - PunchRecovery; break; }
                }
                for (var i = 0; i < _tn; i++)
                    if (_tempo[i].SrcStartMs < c1 && _tempo[i].SrcEndMs > c0) { drop *= 1f - TempoRecovery * (_tempo[i].Rate - 1f) / (MaxRate - 1f); break; }
            }
            _pred[s + 1] = Math.Clamp(_pred[s] - drop, 0f, 1f);
            area += (_pred[s] + _pred[s + 1]) * 0.5;
            orig += (curve[s] + curve[s + 1]) * 0.5;
        }
        var dur = Math.Max(1, n - 1);
        PredictedAverage = n > 1 ? (float)(area / dur) : _pred[0];
        PredictedCompletion = _pred[n - 1];
        UpliftPp = n > 1 ? (float)((area - orig) / dur * 100.0) : 0f;
    }

    private static int CopyClamped(ReadOnlySpan<float> src, Span<float> dst)
    {
        var k = Math.Min(src.Length, dst.Length);
        src[..k].CopyTo(dst);
        return k;
    }
}
