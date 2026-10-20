using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.Editorial;

/// <summary>몽타주 후보 클립(소스 · 소스 내 구간 · 강도 0..1).</summary>
public struct MontageClip
{
    public int Source, SrcIn, MaxLength;
    public float Intensity;
    public int Uses;
}

/// <summary>
/// [PromptFlow Studio v16.79.0] 🥁 신경망 리듬 몽타주 &amp; 서사 호흡 조율기.
/// BGM → 홉(512) 에너지 2대역(저역 1극 LPF 킥 · 전대역) 로그 플럭스 온셋 포락선(8 s 링) → 자기상관 템포(60~180 BPM · 120 BPM 로그 가우시안 사전 · 포물선 보간) →
/// 빗살 위상 탐색(최근 비트 시각) → 비트 그리드(초 · 영상 프레임 양자화). 씬 감정 곡선(액션 ↔ 드라마틱 슬로우) → 컷당 비트 수(1·2·4·8) → 목표 샷 길이.
/// 몽타주 조립: 비트 슬롯마다 곡선 강도에 가장 가까운 클립(직전 소스 회피 · 사용 횟수 벌점) → 타임라인 트랙. 오디오 처리/조립 무할당.
/// </summary>
public sealed class NeuralRhythmMontageSynthesizer
{
    public const int Hop = 512, EnvLen = 768, MinBpm = 60, MaxBpm = 180, MaxCurve = 64, MaxPool = 64;

    private readonly object _gate = new();
    private readonly int _fs;
    private readonly float[] _env = new float[EnvLen], _ac;
    private readonly float[] _curveT = new float[MaxCurve], _curveV = new float[MaxCurve];
    private readonly MontageClip[] _pool = new MontageClip[MaxPool];
    private int _envPos, _hopFill, _curveN, _poolN, _minLag, _maxLag;
    private long _hops;
    private float _lp, _hopELow, _hopEAll, _prevLow, _prevAll, _period = 0.5f, _lastBeatSec, _confidence;

    public NeuralRhythmMontageSynthesizer(int sampleRate = 48000)
    {
        _fs = sampleRate;
        var rate = (float)sampleRate / Hop;
        _minLag = (int)(rate * 60f / MaxBpm);
        _maxLag = (int)MathF.Ceiling(rate * 60f / MinBpm);
        _ac = new float[_maxLag + 2];
    }

    public int SampleRate => _fs;
    public float EnvelopeRate => (float)_fs / Hop;
    public float Bpm => 60f / _period;
    public float PeriodSec => _period;
    public float LastBeatSec => _lastBeatSec;
    public float TempoConfidence => _confidence;
    public long Hops => _hops;
    public float NowSec => _hops * (float)Hop / _fs;
    public int MinLag => _minLag;
    public int MaxLag => _maxLag;
    public float Autocorrelation(int lag) => (uint)lag < (uint)_ac.Length ? _ac[lag] : 0f;
    /// <summary>k 홉 전 온셋 포락선(0 = 최신).</summary>
    public float Envelope(int ago) => (uint)ago < EnvLen ? _env[(_envPos - 1 - ago + 2 * EnvLen) % EnvLen] : 0f;
    public int CurveCount => _curveN;
    public float CurveTime(int i) => _curveT[i];
    public float CurveValue(int i) => _curveV[i];
    public int PoolCount => _poolN;
    public ref MontageClip Pool(int i) => ref _pool[i];
    public long TempoUpdates { get; private set; }

    // ══════════════════════ 비트 추적 ══════════════════════

    /// <summary>BGM 모노 블록 처리(무할당). 0.25 s 마다 템포/위상 갱신.</summary>
    public void ProcessAudio(ReadOnlySpan<float> mono)
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Montage.Beat");
            var a = 1f - MathF.Exp(-2f * MathF.PI * 150f / _fs);
            for (var i = 0; i < mono.Length; i++)
            {
                var x = mono[i];
                _lp += (x - _lp) * a;
                _hopELow += _lp * _lp; _hopEAll += x * x;
                if (++_hopFill < Hop) continue;
                var lo = MathF.Log(1e-6f + _hopELow / Hop); var al = MathF.Log(1e-6f + _hopEAll / Hop);
                var flux = MathF.Max(0f, lo - _prevLow) * 1.5f + MathF.Max(0f, al - _prevAll);
                _prevLow = lo; _prevAll = al; _hopELow = _hopEAll = 0; _hopFill = 0;
                _env[_envPos] = flux; _envPos = (_envPos + 1) % EnvLen;
                _hops++;
                if (_hops % 24 == 0 && _hops >= 2 * _maxLag) UpdateTempo();
            }
        }
    }

    private void UpdateTempo()
    {
        // 평균 제거 자기상관
        float mean = 0;
        for (var i = 0; i < EnvLen; i++) mean += _env[i];
        mean /= EnvLen;
        float best = float.MinValue; var bl = _minLag;
        float r0 = 0;
        for (var i = 0; i < EnvLen; i++) { var v = Envelope(i) - mean; r0 += v * v; }
        for (var lag = _minLag; lag <= _maxLag; lag++)
        {
            float s = 0;
            for (var i = 0; i + lag < EnvLen; i++) s += (Envelope(i) - mean) * (Envelope(i + lag) - mean);
            s /= EnvLen - lag;
            var bpm = 60f * EnvelopeRate / lag;
            var prior = MathF.Exp(-0.5f * MathF.Pow(MathF.Log2(bpm / 120f) / 0.9f, 2f));
            _ac[lag] = s;
            var score = s * prior;
            if (score > best) { best = score; bl = lag; }
        }
        // 포물선 보간
        var p = (float)bl;
        if (bl > _minLag && bl < _maxLag)
        {
            float y0 = _ac[bl - 1], y1 = _ac[bl], y2 = _ac[bl + 1], den = y0 - 2 * y1 + y2;
            if (MathF.Abs(den) > 1e-9f) p += Math.Clamp(0.5f * (y0 - y2) / den, -0.5f, 0.5f);
        }
        _period = p / EnvelopeRate;
        _confidence = r0 > 1e-9f ? Math.Clamp(_ac[bl] * EnvLen / r0, 0f, 1f) : 0f;

        // 빗살 위상: φ ∈ [0, P) 홉 — 최근 4 비트 합 최대
        float bestPh = float.MinValue; var phase = 0;
        var P = (int)MathF.Round(p);
        for (var ph = 0; ph < P; ph++)
        {
            float s = 0;
            for (var k = 0; k < 6; k++)
            {
                var idx = ph + (int)MathF.Round(k * p);
                if (idx >= EnvLen) break;
                s += Envelope(idx) * (1f - 0.1f * k);
            }
            if (s > bestPh) { bestPh = s; phase = ph; }
        }
        // 최근 비트 시각(홉 끝 기준 · 온셋은 홉 중앙에서 감지되므로 반 홉 보정)
        _lastBeatSec = (_hops - phase) * (float)Hop / _fs - 0.5f * Hop / _fs;
        TempoUpdates++;
    }

    /// <summary>시각 t 에서 가장 가까운 비트까지의 부호 거리(초).</summary>
    public float BeatOffset(float t)
    {
        var x = (t - _lastBeatSec) / _period;
        return (x - MathF.Round(x)) * _period;
    }

    /// <summary>영상 프레임이 비트 프레임(가장 가까운 프레임)인지.</summary>
    public bool IsBeatFrame(long frame, int fps) => _confidence > 0.05f && MathF.Abs(BeatOffset(frame / (float)fps)) <= 0.5f / fps;

    /// <summary>t 이후 첫 비트 시각.</summary>
    public float NextBeat(float t)
    {
        var x = MathF.Ceiling((t - _lastBeatSec) / _period - 1e-4f);
        return _lastBeatSec + x * _period;
    }

    // ══════════════════════ 감정 곡선 · 호흡 ══════════════════════

    public void ClearCurve() { lock (_gate) _curveN = 0; }

    /// <summary>감정 강도 키(시간 오름차순 · 0 = 드라마틱 슬로우 · 1 = 액션).</summary>
    public void AddCurveKey(float t, float intensity)
    {
        lock (_gate)
        {
            if (_curveN >= MaxCurve) return;
            var i = _curveN++;
            while (i > 0 && _curveT[i - 1] > t) { _curveT[i] = _curveT[i - 1]; _curveV[i] = _curveV[i - 1]; i--; }
            _curveT[i] = t; _curveV[i] = Math.Clamp(intensity, 0f, 1f);
        }
    }

    /// <summary>선형 보간 감정 강도.</summary>
    public float Intensity(float t)
    {
        if (_curveN == 0) return 0.5f;
        if (t <= _curveT[0]) return _curveV[0];
        for (var i = 1; i < _curveN; i++)
            if (t < _curveT[i]) { var u = (t - _curveT[i - 1]) / MathF.Max(1e-4f, _curveT[i] - _curveT[i - 1]); return _curveV[i - 1] + (_curveV[i] - _curveV[i - 1]) * u; }
        return _curveV[_curveN - 1];
    }

    /// <summary>컷당 비트 수(강도 ≥0.7 → 1 · ≥0.45 → 2 · ≥0.2 → 4 · 그 외 8).</summary>
    public static int BeatsPerCut(float intensity) => intensity >= 0.7f ? 1 : intensity >= 0.45f ? 2 : intensity >= 0.2f ? 4 : 8;

    /// <summary>시각 t 의 목표 샷 길이(프레임).</summary>
    public int TargetShotFrames(float t, int fps) => Math.Max(fps / 2, (int)MathF.Round(BeatsPerCut(Intensity(t)) * _period * fps));

    // ══════════════════════ 몽타주 조립 ══════════════════════

    public void ClearPool() { lock (_gate) _poolN = 0; }

    public int AddPoolClip(int source, int srcIn, int maxLength, float intensity)
    {
        lock (_gate)
        {
            if (_poolN >= MaxPool) return -1;
            _pool[_poolN] = new MontageClip { Source = source, SrcIn = srcIn, MaxLength = maxLength, Intensity = Math.Clamp(intensity, 0f, 1f) };
            return _poolN++;
        }
    }

    /// <summary>
    /// [recStart, recEnd) 프레임 구간을 비트 슬롯으로 채워 track 에 추가(무할당). 슬롯 경계 = 비트 프레임 · 길이 = 컷당 비트 수.
    /// 클립 선택 = |강도 − 곡선| + 0.15·사용 횟수 + 직전 소스 벌점 1. 반환 = 추가 클립 수.
    /// </summary>
    public int Assemble(EditTimeline tl, int track, int recStart, int recEnd)
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Montage.Assemble");
            if (_poolN == 0 || recEnd <= recStart) return 0;
            for (var i = 0; i < _poolN; i++) _pool[i].Uses = 0;
            var fps = tl.Fps;
            var rec = recStart;
            // 첫 슬롯 = 시작 이후 첫 비트로 정렬(그 이전 구간은 첫 클립으로 채움)
            var firstBeat = (int)MathF.Round(NextBeat(recStart / (float)fps) * fps);
            var added = 0; var prevSrc = -1;
            while (rec < recEnd)
            {
                var t = rec / (float)fps;
                var target = Intensity(t);
                var beats = BeatsPerCut(target);
                var end = rec < firstBeat ? firstBeat : (int)MathF.Round(NextBeat(t + (beats - 0.5f) * _period) * fps);
                if (end <= rec) end = rec + Math.Max(1, (int)(_period * fps));
                end = Math.Min(end, recEnd);
                var best = -1; var bs = float.MaxValue;
                for (var i = 0; i < _poolN; i++)
                {
                    ref var c = ref _pool[i];
                    if (c.MaxLength < end - rec) continue;
                    var s = MathF.Abs(c.Intensity - target) + 0.15f * c.Uses + (c.Source == prevSrc ? 1f : 0f);
                    if (s < bs) { bs = s; best = i; }
                }
                if (best < 0) best = 0;
                ref var pick = ref _pool[best];
                var len = Math.Min(end - rec, pick.MaxLength);
                if (tl.Add(track, pick.Source, rec, len, CutReason.Montage, pick.SrcIn + (pick.Uses * 7) % Math.Max(1, pick.MaxLength - len + 1)) < 0) break;
                pick.Uses++; prevSrc = pick.Source; added++;
                rec += len;
            }
            return added;
        }
    }
}
