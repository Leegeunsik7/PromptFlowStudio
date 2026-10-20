using System.Numerics;

namespace PromptFlowStudio.Services.NeuralCrowd;

public enum CrowdMood : byte { Calm = 0, Panic = 1, Excited = 2 }

public enum CrowdStimulusKind : byte { Gust = 0, Lightning = 1, Fracture = 2, Speech = 3 }

/// <summary>환경 자극 파면(원점 · 시각 · 전파 속도 · 강도 · 도달 반경).</summary>
public struct CrowdStimulus
{
    public CrowdStimulusKind Kind;
    public Vector2 Origin;
    public float Time, Speed, Strength, Range;
    public int Seq;
}

/// <summary>웅성거림 음향 매개변수 1 프레임(링버퍼 원소).</summary>
public struct MurmurFrame
{
    public float Loudness, Pitch, Voices, Scream, Rms;
}

/// <summary>
/// [PromptFlow Studio v17.29.0] 😱 사회적 감정 &amp; 환경 반응성 군집 행동 합성기.
/// v17.28 돌풍(머리 높이 풍속) · 낙뢰(뇌우 강도 기반 결정적 푸아송 발생) · v17.26 물리 파괴 충격파를 파면 링버퍼(32)로 전파해
/// 놀람/공포를 주입하고, 이웃(2 m) 공포 전염(이전 프레임 이중 버퍼 · 결정적) · 감쇠로 군중 심리를 갱신한다.
/// v17.12 메인 액터 시선 원뿔 + v17.18 발화 레벨로 주목도를 계산해 엔진 속도 배율/비상구/주목 가중을 쓴다.
/// 마지막으로 웅성거림(포먼트 공명 잡음 × 음절 포락 + 비명 부분음)을 48 kHz PCM 링버퍼에 무할당 절차 합성한다.
/// </summary>
public sealed class SocialEmotionEnvironmentalReactionSynthesizer
{
    public const int MaxAgents = RealtimeOrcaCrowdNavigationEngine.MaxAgents, G = RealtimeOrcaCrowdNavigationEngine.G;
    public const int StimulusCap = 32, MurmurLen = 256, SampleRate = 48000, PcmLen = 16384, Voices = 6;
    public const float ContagionRadius = 2f, ContagionRate = 1.6f, FearDecay = 0.08f, MoodOnset = 0.8f, SoundSpeed = 343f, GustThreshold = 11f;

    private readonly float[] _fear = new float[MaxAgents], _fearPrev = new float[MaxAgents], _surprise = new float[MaxAgents], _attention = new float[MaxAgents], _excite = new float[MaxAgents];
    private readonly uint[] _hitMask = new uint[MaxAgents];
    private readonly CrowdStimulus[] _stim = new CrowdStimulus[StimulusCap];
    private readonly MurmurFrame[] _murmur = new MurmurFrame[MurmurLen];
    private readonly float[] _pcm = new float[PcmLen];
    private readonly float[] _vFreq = new float[Voices], _vPhase = new float[Voices], _vY1 = new float[Voices], _vY2 = new float[Voices], _vRate = new float[Voices];
    private int _stimHead, _seq, _murHead, _murCount, _pcmHead;
    private uint _rng = 0x2545F491u, _noise = 0x1234567u;
    private float _time, _gustCooldown, _screamPhase, _loudSmooth;

    public SocialEmotionEnvironmentalReactionSynthesizer()
    {
        for (var v = 0; v < Voices; v++) { _vFreq[v] = 420f + 160f * v; _vRate[v] = 3.6f + 0.45f * v; _vPhase[v] = v * 0.37f; }
    }

    public CrowdMood Mood { get; private set; }
    public float MoodIntensity { get; private set; }
    public bool Enabled { get; set; } = true;
    /// <summary>v17.28 결선 입력: 머리 높이 풍속(m/s) · 강수(mm/h) · 뇌우 강도(0–1).</summary>
    public float AmbientWindMs { get; set; }
    public float PrecipMmH { get; set; }
    public float StormIntensity { get; set; }
    /// <summary>v17.12 메인 액터: 위치 · 시선(도 · 0 = +Y) · 원뿔 반각 · v17.18 발화 레벨(0–1).</summary>
    public Vector2 ActorPos { get; set; } = new(48f, 46f);
    public float GazeDeg { get; set; } = 225f;
    public float GazeHalfAngleDeg { get; set; } = 30f;
    public float SpeechLevel { get; set; }

    public int CalmCount { get; private set; }
    public int PanicCount { get; private set; }
    public int ExcitedCount { get; private set; }
    public float MeanFear { get; private set; }
    public float MeanAttention { get; private set; }
    public float MeanExcite { get; private set; }
    public float AttentionInCone { get; private set; }
    public float AttentionOutCone { get; private set; }
    public long StimuliTotal { get; private set; }
    public long Lightnings { get; private set; }
    public long Gusts { get; private set; }
    public long WaveHits { get; private set; }
    public int ActiveWaves { get; private set; }
    public float PcmRms { get; private set; }
    public float PcmPeak { get; private set; }
    public float SimTime => _time;
    public MurmurFrame LastMurmur => _murmur[(_murHead - 1 + MurmurLen) % MurmurLen];
    public int MurmurCount => _murCount;
    public MurmurFrame Murmur(int back) => _murmur[(_murHead - 1 - back + MurmurLen * 2) % MurmurLen];
    /// <summary>최근 PCM 표본(back = 0 이 최신).</summary>
    public float Pcm(int back) => _pcm[(_pcmHead - 1 - back + PcmLen * 2) % PcmLen];
    public float Fear(int i) => _fear[i];
    public float Attention(int i) => _attention[i];
    public float Excite(int i) => _excite[i];
    public float Surprise(int i) => _surprise[i];
    /// <summary>0 = 평온 · 1 = 패닉 · 2 = 흥분.</summary>
    public int StateOf(int i) => _fear[i] > 0.5f ? 1 : _excite[i] > 0.5f ? 2 : 0;
    public CrowdStimulus Stimulus(int k) => _stim[k];

    public void SetMood(CrowdMood m, float intensity) { Mood = m; MoodIntensity = Math.Clamp(intensity, 0f, 1f); }

    public void Reset()
    {
        Array.Clear(_fear); Array.Clear(_fearPrev); Array.Clear(_surprise); Array.Clear(_attention); Array.Clear(_excite); Array.Clear(_hitMask);
        Array.Clear(_stim); Array.Clear(_murmur); Array.Clear(_pcm);
        _stimHead = 0; _seq = 0; _murHead = 0; _murCount = 0; _pcmHead = 0; _time = 0f; _gustCooldown = 0f;
        StimuliTotal = 0; Lightnings = 0; Gusts = 0; WaveHits = 0;
    }

    /// <summary>자극 파면 등록(링 슬롯 재사용 시 해당 비트 일괄 해제).</summary>
    public void Push(CrowdStimulusKind kind, Vector2 origin, float strength)
    {
        var slot = _stimHead;
        _stimHead = (_stimHead + 1) % StimulusCap;
        var bit = ~(1u << slot);
        for (var i = 0; i < MaxAgents; i++) _hitMask[i] &= bit;
        float speed, range;
        switch (kind)
        {
            case CrowdStimulusKind.Lightning: speed = SoundSpeed; range = 90f; Lightnings++; break;   // 천둥 음파
            case CrowdStimulusKind.Fracture: speed = 120f; range = 40f; break;                       // 충격파(지면 + 소리 근사)
            case CrowdStimulusKind.Gust: speed = 14f; range = 70f; Gusts++; break;                   // 돌풍 전선
            default: speed = SoundSpeed; range = 30f; break;
        }
        _stim[slot] = new CrowdStimulus { Kind = kind, Origin = origin, Time = _time, Speed = speed, Strength = Math.Clamp(strength, 0f, 2f), Range = range, Seq = ++_seq };
        StimuliTotal++;
    }

    private float Rand() { _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5; return (_rng >> 8) * (1f / 16777216f); }

    /// <summary>1 프레임(무할당): 환경 결선 → 파면 → 전염/감쇠 → 주목/흥분 → 엔진 가중 쓰기 → 웅성거림 합성.</summary>
    public void Step(RealtimeOrcaCrowdNavigationEngine e, float dt)
    {
        _time += dt;
        // 1) v17.28 환경 결선: 뇌우 낙뢰(푸아송 λ = 0.25·storm /s) · 돌풍(임계 초과 시 2.5 s 쿨다운)
        if (StormIntensity > 0.4f && Rand() < 0.25f * StormIntensity * dt)
            Push(CrowdStimulusKind.Lightning, new Vector2(4f + Rand() * (G - 8f), 4f + Rand() * (G - 8f)), 0.6f + 0.6f * StormIntensity);
        _gustCooldown -= dt;
        if (AmbientWindMs > GustThreshold && _gustCooldown <= 0f)
        {
            Push(CrowdStimulusKind.Gust, new Vector2(Rand() < 0.5f ? 0f : G, Rand() * G), Math.Clamp((AmbientWindMs - GustThreshold) / 15f, 0.1f, 1f));
            _gustCooldown = 2.5f;
        }

        var n = e.Count;
        Array.Copy(_fear, _fearPrev, n);
        float baseFear = Mood == CrowdMood.Panic ? MoodIntensity : 0f, baseExcite = Mood == CrowdMood.Excited ? MoodIntensity : 0f;
        var hurry = Math.Clamp(PrecipMmH / 25f, 0f, 1f);
        var gaze = GazeDeg * (MathF.PI / 180f);
        var gdir = new Vector2(MathF.Sin(gaze), MathF.Cos(gaze));
        var cosCone = MathF.Cos(GazeHalfAngleDeg * (MathF.PI / 180f));

        // 활성 파면 수
        var waves = 0;
        for (var k = 0; k < StimulusCap; k++)
        {
            ref var s = ref _stim[k];
            if (s.Seq != 0 && s.Speed * (_time - s.Time) <= s.Range) waves++;
        }
        ActiveWaves = waves;

        int calm = 0, panic = 0, excited = 0, act = 0, inC = 0, outC = 0;
        float sf = 0f, sa = 0f, se = 0f, ain = 0f, aout = 0f;
        var r2 = ContagionRadius * ContagionRadius;
        for (var i = 0; i < n; i++)
        {
            if (e.Active[i] == 0) continue;
            float x = e.PX[i], y = e.PY[i];
            if (!float.IsFinite(x) || !float.IsFinite(y)) continue;
            act++;
            var p = new Vector2(x, y);
            var fear = _fear[i]; var sur = _surprise[i] * MathF.Exp(-dt / 0.8f);

            // 2) 파면 통과 시 1 회 적용(감쇠 1/(1 + d/10))
            for (var k = 0; k < StimulusCap; k++)
            {
                ref var s = ref _stim[k];
                if (s.Seq == 0 || (_hitMask[i] & (1u << k)) != 0) continue;
                var d = Vector2.Distance(p, s.Origin);
                if (d > s.Range || s.Speed * (_time - s.Time) < d) continue;
                _hitMask[i] |= 1u << k;
                var fall = s.Strength / (1f + d / 10f);
                switch (s.Kind)
                {
                    case CrowdStimulusKind.Lightning: sur += 0.9f * fall; fear += 0.8f * fall; break;
                    case CrowdStimulusKind.Fracture: sur += 1.0f * fall; fear += 1.0f * fall; break;
                    case CrowdStimulusKind.Gust: sur += 0.35f * fall; fear += 0.08f * fall; break;
                    default: sur += 0.2f * fall; break;
                }
                WaveHits++;
            }

            // 3) 공포 전염(이전 프레임 이웃 평균·최대 혼합이 더 클 때만 · 2 m)
            int c = e.AgentCell[i], cx = c % G, cy = c / G;
            float nf = 0f, nmax = 0f; var nc = 0;
            for (var oy = -2; oy <= 2; oy++)
            {
                var yy = cy + oy;
                if ((uint)yy >= G) continue;
                for (var ox = -2; ox <= 2; ox++)
                {
                    var xx = cx + ox;
                    if ((uint)xx >= G) continue;
                    var cc = yy * G + xx;
                    for (var t = e.CellStart[cc]; t < e.CellStart[cc + 1]; t++)
                    {
                        var j = e.CellItems[t];
                        if (j == i) continue;
                        float dx = e.PX[j] - x, dy = e.PY[j] - y;
                        if (!(dx * dx + dy * dy < r2)) continue;
                        var fj = _fearPrev[j];
                        nf += fj; nc++;
                        if (fj > nmax) nmax = fj;
                    }
                }
            }
            if (nc > 0)
            {
                var mean = 0.5f * (nf / nc) + 0.5f * nmax;
                if (mean > fear) fear += (mean - fear) * MathF.Min(1f, ContagionRate * dt * MathF.Min(1f, nc / 3f));
            }
            fear += (baseFear - fear) * MathF.Min(1f, (baseFear > fear ? MoodOnset : FearDecay) * dt);   // 심리 트리거는 빠르게 · 해소는 느리게
            fear = Math.Clamp(fear, 0f, 1f);

            // 4) 주목: 시선 원뿔 × 근접 + 발화 가청(30 m)
            var ta = p - ActorPos;
            var da = ta.Length();
            var inCone = da > 1e-3f && Vector2.Dot(ta, gdir) >= cosCone * da;
            var prox = Math.Clamp(1f - da / 40f, 0f, 1f);
            var target = (inCone ? 0.65f * prox : 0f) + SpeechLevel * 0.8f * Math.Clamp(1f - da / 30f, 0f, 1f);
            target *= 1f - fear;
            var att = _attention[i] + (Math.Clamp(target, 0f, 1f) - _attention[i]) * MathF.Min(1f, 2.5f * dt);
            var ex = _excite[i] + (Math.Clamp(baseExcite + 0.5f * att * SpeechLevel, 0f, 1f) * (1f - fear) - _excite[i]) * MathF.Min(1f, 1.2f * dt);

            _fear[i] = fear; _surprise[i] = sur; _attention[i] = att; _excite[i] = ex;

            // 5) 엔진 가중(감정 → 행동)
            if (Enabled)
            {
                e.SpeedScale[i] = Math.Clamp(1f + 1.1f * fear + 0.35f * hurry + 0.4f * MathF.Min(1f, sur) - 0.6f * att * (1f - fear) + 0.2f * ex, 0.2f, 2.6f);
                e.ExitWeight[i] = Math.Clamp((fear - 0.35f) / 0.3f, 0f, 1f);
                e.AttractWeight[i] = Math.Clamp(ex * 0.9f, 0f, 1f) * (1f - fear);
            }
            else { e.SpeedScale[i] = 1f; e.ExitWeight[i] = 0f; e.AttractWeight[i] = 0f; }

            sf += fear; sa += att; se += ex;
            if (inCone) { ain += att; inC++; } else { aout += att; outC++; }
            if (fear > 0.5f) panic++; else if (ex > 0.5f) excited++; else calm++;
        }
        CalmCount = calm; PanicCount = panic; ExcitedCount = excited;
        MeanFear = act == 0 ? 0f : sf / act; MeanAttention = act == 0 ? 0f : sa / act; MeanExcite = act == 0 ? 0f : se / act;
        AttentionInCone = inC == 0 ? 0f : ain / inC; AttentionOutCone = outC == 0 ? 0f : aout / outC;

        SynthesizeMurmur(act, dt);
    }

    /// <summary>웅성거림: 화자 수 ∝ 활성 수 · 피치 ↑ 공포 · 크기 ↑ 흥분/공포 · 주목 시 잦아듦 → 프레임 매개변수 링 + PCM.</summary>
    private void SynthesizeMurmur(int active, float dt)
    {
        var voices = MathF.Min(Voices, 1f + active / 150f);
        var loud = Math.Clamp(0.12f + 0.08f * MathF.Log(1f + active / 50f) + 0.45f * MeanFear + 0.3f * MeanExcite - 0.25f * MeanAttention * (1f - MeanFear), 0f, 1f);
        _loudSmooth += (loud - _loudSmooth) * MathF.Min(1f, 6f * dt);
        var pitch = 1f + 0.6f * MeanFear + 0.2f * MeanExcite;
        var scream = Math.Clamp((MeanFear - 0.3f) / 0.5f, 0f, 1f);

        var samples = Math.Min(PcmLen, (int)(SampleRate * dt + 0.5f));
        float sum2 = 0f, peak = 0f;
        for (var k = 0; k < samples; k++)
        {
            float acc = 0f;
            for (var v = 0; v < Voices; v++)
            {
                if (v >= voices) break;
                // 음절 포락(3.6–5.9 Hz) × 포먼트 2 극 공명 잡음
                _vPhase[v] += _vRate[v] * pitch / SampleRate;
                if (_vPhase[v] >= 1f) _vPhase[v] -= 1f;
                var env = MathF.Max(0f, MathF.Sin(_vPhase[v] * MathF.Tau));
                _noise ^= _noise << 13; _noise ^= _noise >> 17; _noise ^= _noise << 5;
                var wn = (_noise >> 8) * (2f / 16777216f) - 1f;
                var w0 = MathF.Tau * _vFreq[v] * pitch / SampleRate;
                const float rr = 0.985f;
                var y = wn * (1f - rr) + 2f * rr * MathF.Cos(w0) * _vY1[v] - rr * rr * _vY2[v];
                _vY2[v] = _vY1[v]; _vY1[v] = y;
                acc += y * env;
            }
            if (scream > 0f)
            {
                _screamPhase += (950f + 250f * MathF.Sin(_time * 7f)) * pitch / SampleRate;
                if (_screamPhase >= 1f) _screamPhase -= 1f;
                acc += 0.25f * scream * MathF.Sin(_screamPhase * MathF.Tau);
            }
            var s = MathF.Tanh(acc * 2.5f * _loudSmooth);
            _pcm[_pcmHead] = s;
            _pcmHead = (_pcmHead + 1) % PcmLen;
            sum2 += s * s;
            var a = MathF.Abs(s);
            if (a > peak) peak = a;
        }
        PcmRms = samples == 0 ? 0f : MathF.Sqrt(sum2 / samples);
        PcmPeak = peak;
        _murmur[_murHead] = new MurmurFrame { Loudness = _loudSmooth, Pitch = pitch, Voices = voices, Scream = scream, Rms = PcmRms };
        _murHead = (_murHead + 1) % MurmurLen;
        if (_murCount < MurmurLen) _murCount++;
    }
}
