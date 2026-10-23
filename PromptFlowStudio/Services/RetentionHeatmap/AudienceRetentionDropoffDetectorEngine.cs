namespace PromptFlowStudio.Services.RetentionHeatmap;

/// <summary>이탈 절벽 1 건(값 형식 · 영상 타임라인 ms · 10 ms 양자화) — 시작 · 최대 낙폭 창 시작 · 종료 · 1 s 창 최대 낙폭(비율).</summary>
public struct RetentionSlump
{
    public int OnsetMs, PeakMs, EndMs;
    public float Drop;
}

/// <summary>지루한 누수 구간(데드존) 1 건 — 초 단위 [Start, End) · 평균 초당 누수(비율).</summary>
public struct RetentionDeadZone
{
    public int StartSec, EndSec;
    public float LeakPerSec;
}

/// <summary>
/// [PromptFlow Studio v17.98.0] 📉 무할당 시청 지속률 곡선 &amp; 이탈 절벽 감지 엔진(<see cref="Analyze"/> 무할당).
///   ▸ 입력: 초 단위 유지율 시계열(<c>r[i]</c> = i 초 시점 0.0~1.0 · 최대 <see cref="MaxSec"/> 초) — 선형 보간으로 10 ms 틱 재표본.
///   ▸ 이탈 절벽: 1 s 슬라이딩 윈도 미분 <c>r(t) − r(t + 1 s)</c> ≥ 임계(기본 5 %p) 연속 구간 → 첫 틱 = 시작 · 최대 낙폭 틱 = 피크 · 마지막 틱 + 1 s = 종료.
///   ▸ 데드존: 절벽 미만이지만 초당 ≥ 1.5 %p 누수가 3 s 이상 이어지는 지루한 구간 → 템포 가속 후보.
///   ▸ 지표: 평균 시청 지속률(사다리꼴 적분) · 완청률(마지막 표본) · 숏폼 기준 곡선(1 − 0.35·(t/T)^0.6) 대비 상대 유지율 0~100.
/// </summary>
public sealed class AudienceRetentionDropoffDetectorEngine
{
    public const int MaxSec = 180, TickMs = 10, TicksPerSec = 1000 / TickMs, SlumpCap = 16, DeadZoneCap = 16, DeadMinSec = 3;
    public const float DefaultThreshold = 0.05f, DeadLeakPerSec = 0.015f, MinThreshold = 0.02f, MaxThreshold = 0.15f;

    private readonly float[] _r = new float[MaxSec + 1];
    private readonly RetentionSlump[] _slumps = new RetentionSlump[SlumpCap];
    private readonly RetentionDeadZone[] _dead = new RetentionDeadZone[DeadZoneCap];
    private int _n, _slumpN, _deadN;

    public float Threshold { get; set; } = DefaultThreshold;
    /// <summary>표본 수(= 길이 초 + 1).</summary>
    public int Samples => _n;
    public int DurationSec => _n > 1 ? _n - 1 : 0;
    public int SlumpCount => _slumpN;
    public int DeadZoneCount => _deadN;
    public ref readonly RetentionSlump Slump(int i) => ref _slumps[i];
    public ref readonly RetentionDeadZone DeadZone(int i) => ref _dead[i];
    public ReadOnlySpan<float> Curve => _r.AsSpan(0, _n);
    public float AverageRetention { get; private set; }
    public float Completion { get; private set; }
    public float BenchmarkAverage { get; private set; }
    /// <summary>기준 곡선 대비 상대 유지율(50 = 기준 동일 · 0~100 클램프).</summary>
    public float RelativeScore { get; private set; }
    public long Analyses { get; private set; }
    public long Truncated { get; private set; }
    public long DroppedSlumps { get; private set; }
    public double LastAnalyzeUs { get; private set; }

    /// <summary>10 ms 틱 t 의 선형 보간 유지율.</summary>
    public float At(int tick)
    {
        if (_n == 0) return 0f;
        if (tick <= 0) return _r[0];
        var s = tick / TicksPerSec;
        if (s >= _n - 1) return _r[_n - 1];
        return _r[s] + (_r[s + 1] - _r[s]) * (tick - s * TicksPerSec) * (1f / TicksPerSec);
    }

    /// <summary>1 s 윈도 낙폭(틱 t 시작) — 양수 = 이탈.</summary>
    public float WindowDrop(int tick) => At(tick) - At(tick + TicksPerSec);

    /// <summary>i 초 → i+1 초 낙폭(히트맵 셀).</summary>
    public float DropAt(int sec) => sec < 0 || sec + 1 >= _n ? 0f : _r[sec] - _r[sec + 1];

    /// <summary>초 셀이 데드존에 속하는지.</summary>
    public bool IsDead(int sec)
    {
        for (var i = 0; i < _deadN; i++) if (sec >= _dead[i].StartSec && sec < _dead[i].EndSec) return true;
        return false;
    }

    /// <summary>히트맵 강도 0..1 — 1 s 낙폭 / (임계 × 3).</summary>
    public float Heat(int sec) => Math.Clamp(DropAt(sec) / (Threshold * 3f), 0f, 1f);

    public void Reset()
    {
        _n = _slumpN = _deadN = 0;
        AverageRetention = Completion = BenchmarkAverage = RelativeScore = 0f;
        Analyses = Truncated = DroppedSlumps = 0;
        LastAnalyzeUs = 0;
        Threshold = DefaultThreshold;
    }

    /// <summary>곡선 스캔(무할당) — 반환 = 이탈 절벽 수.</summary>
    public int Analyze(ReadOnlySpan<float> retention)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var n = retention.Length;
        if (n > MaxSec + 1) { n = MaxSec + 1; Truncated++; }
        var prev = 1f;
        for (var i = 0; i < n; i++)
        {
            var v = retention[i];
            v = float.IsNaN(v) ? prev : Math.Clamp(v, 0f, 1f);
            _r[i] = prev = v;
        }
        _n = n;
        _slumpN = _deadN = 0;
        Analyses++;
        if (n < 2)
        {
            AverageRetention = Completion = n == 1 ? _r[0] : 0f;
            BenchmarkAverage = RelativeScore = 0f;
            Stamp(t0);
            return 0;
        }

        // ── 이탈 절벽: 10 ms 틱 1 s 윈도 미분 ──
        var thr = Math.Clamp(Threshold, MinThreshold, MaxThreshold);
        var lastTick = (n - 1) * TicksPerSec - TicksPerSec;
        var inSlump = false;
        var cur = default(RetentionSlump);
        for (var t = 0; t <= lastTick; t++)
        {
            var d = WindowDrop(t);
            if (d >= thr - 1e-6f)
            {
                if (!inSlump) { inSlump = true; cur = new RetentionSlump { OnsetMs = t * TickMs, PeakMs = t * TickMs, Drop = d }; }
                else if (d > cur.Drop) { cur.Drop = d; cur.PeakMs = t * TickMs; }
                cur.EndMs = (t + TicksPerSec) * TickMs;
            }
            else if (inSlump) { inSlump = false; PushSlump(in cur); }
        }
        if (inSlump) PushSlump(in cur);

        // ── 데드존: 초당 누수 [1.5 %p, 임계) ≥ 3 s 연속 ──
        var runStart = -1;
        var leakSum = 0f;
        for (var s = 0; s <= n - 1; s++)
        {
            var leak = s < n - 1 ? _r[s] - _r[s + 1] : -1f;
            var dull = leak >= DeadLeakPerSec && leak < thr;
            if (dull)
            {
                if (runStart < 0) { runStart = s; leakSum = 0f; }
                leakSum += leak;
                continue;
            }
            if (runStart >= 0 && s - runStart >= DeadMinSec && _deadN < DeadZoneCap)
                _dead[_deadN++] = new RetentionDeadZone { StartSec = runStart, EndSec = s, LeakPerSec = leakSum / (s - runStart) };
            runStart = -1;
        }

        // ── 지표 ──
        double area = 0, bench = 0;
        var dur = n - 1;
        for (var s = 0; s < dur; s++)
        {
            area += (_r[s] + _r[s + 1]) * 0.5;
            bench += (Benchmark(s, dur) + Benchmark(s + 1, dur)) * 0.5;
        }
        AverageRetention = (float)(area / dur);
        BenchmarkAverage = (float)(bench / dur);
        Completion = _r[n - 1];
        RelativeScore = Math.Clamp(50f + 50f * (AverageRetention - BenchmarkAverage) / Math.Max(1e-3f, BenchmarkAverage), 0f, 100f);
        Stamp(t0);
        return _slumpN;
    }

    /// <summary>숏폼 기준 유지율 곡선(1 − 0.35·(t/T)^0.6).</summary>
    public static float Benchmark(int sec, int durationSec) =>
        durationSec <= 0 ? 1f : 1f - 0.35f * MathF.Pow(Math.Clamp(sec / (float)durationSec, 0f, 1f), 0.6f);

    private void PushSlump(in RetentionSlump s)
    {
        if (_slumpN == SlumpCap) { DroppedSlumps++; return; }
        _slumps[_slumpN++] = s;
    }

    private void Stamp(long t0) => LastAnalyzeUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
}
