using System.Diagnostics;
using System.Numerics;
using PromptFlowStudio.Services.SpatialAudio;

namespace PromptFlowStudio.Services.MassiveCrowd;

/// <summary>RBJ 바이쿼드(직접형 I · 상태 내장).</summary>
public struct CrowdBiquad
{
    public float B0, B1, B2, A1, A2, X1, X2, Y1, Y2;

    public static CrowdBiquad BandPass(float f, float q, float fs)
    {
        var w = MathF.Tau * f / fs; var al = MathF.Sin(w) / (2f * q); var a0 = 1f + al;
        return new CrowdBiquad { B0 = al / a0, B1 = 0f, B2 = -al / a0, A1 = -2f * MathF.Cos(w) / a0, A2 = (1f - al) / a0 };
    }

    public float Process(float x)
    {
        var y = B0 * x + B1 * X1 + B2 * X2 - A1 * Y1 - A2 * Y2;
        X2 = X1; X1 = x; Y2 = Y1; Y1 = y;
        return y;
    }
}

/// <summary>
/// [PromptFlow Studio v17.04.0] 📣 스타디움 군집 음향 &amp; 3D 함성 파동 합성기(48 kHz · 800 샘플/프레임 = 60 fps · 무할당).
/// 군중 → 16 섹터 집계(인원 · 흥분 · 공포 · 기립 · 홈 비율 · 무게중심) + 청취자 거리 Σ1/r² → 섹터 음압(1 m 인당 62 + 24·흥분 + 8·공포 dB 비간섭 합) →
/// 섹터 텍스처 합성: 함성(대역 잡음 + 저역 럼블 · 파도 기립 시 휘슬) · 박수(포아송 임펄스 → 1.8 kHz 공진 · 밀도 ↑ 시 가우스 근사) · 챈트(디튠 톱니 3성부 → 'O' 포먼트 · 8분 리듬 게이트) ·
/// 야유(실점 팀 · 'U' 포먼트) · 탄성(피격 'Oh' 숨소리) · 비명(공포 2.6 kHz) → v16.76 신경망 바이노럴 HRTF로 섹터 방향 공간화 → 블록 리미터 →
/// 옥타브 10밴드 음압 스펙트럼(dBFS → SPL 보정) · 피크/RMS 미터.
/// </summary>
public sealed class SwarmStadiumAcousticsSynthesizer
{
    public const int SampleRate = 48000, Block = 800, Sectors = MassiveAgenticCrowdSimulationEngine.Sectors, Bands = 10;
    public const float ListenerHeight = 12f, Ceiling = 0.89f;
    public static readonly float[] BandHz = { 31.5f, 63f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f };
    public static readonly string[] BandNames = { "31", "63", "125", "250", "500", "1k", "2k", "4k", "8k", "16k" };
    private static readonly byte[] ChantPattern = { 1, 1, 0, 1, 1, 1, 0, 0 };
    private const int FRoar = 0, FRumble = 1, FClap = 2, FCh1 = 3, FCh2 = 4, FBoo1 = 5, FBoo2 = 6, FScream = 7, PerSector = 8;

    private readonly NeuralBinauralHrtfSpatializer _hrtf;
    private readonly CrowdBiquad[] _f = new CrowdBiquad[Sectors * PerSector];
    private readonly CrowdBiquad[] _band = new CrowdBiquad[Bands];
    private readonly float[] _mono = new float[Block], _l = new float[Block], _r = new float[Block], _mix = new float[Block];
    private readonly float[] _invR2 = new float[Sectors], _spl = new float[Sectors];
    private readonly float[] _lRoar = new float[Sectors], _lClap = new float[Sectors], _lChant = new float[Sectors], _lBoo = new float[Sectors], _lGasp = new float[Sectors], _lScream = new float[Sectors], _lWhoosh = new float[Sectors];
    private readonly float[] _ph = new float[Sectors * 4], _pitch = new float[Sectors];
    private readonly float[] _bandDb = new float[Bands];
    private ulong _rng = 0x2545F4914F6CDD1DUL;
    private long _seenGoal, _seenHit, _seenCheer;
    private readonly float[] _env = new float[Sectors];
    private float _goalT, _hitT, _cheerT, _limGain = 1f;
    private int _goalTeam = -1;
    private double _clock;

    public SwarmStadiumAcousticsSynthesizer() : this(new NeuralBinauralHrtfSpatializer(SampleRate)) { }

    public SwarmStadiumAcousticsSynthesizer(NeuralBinauralHrtfSpatializer hrtf)
    {
        _hrtf = hrtf;
        for (var s = 0; s < Sectors; s++)
        {
            var o = s * PerSector;
            _f[o + FRoar] = CrowdBiquad.BandPass(650f, 0.8f, SampleRate);
            _f[o + FRumble] = CrowdBiquad.BandPass(220f, 0.9f, SampleRate);
            _f[o + FClap] = CrowdBiquad.BandPass(1800f, 1.8f, SampleRate);
            _f[o + FCh1] = CrowdBiquad.BandPass(450f, 5f, SampleRate);
            _f[o + FCh2] = CrowdBiquad.BandPass(800f, 6f, SampleRate);
            _f[o + FBoo1] = CrowdBiquad.BandPass(310f, 5f, SampleRate);
            _f[o + FBoo2] = CrowdBiquad.BandPass(700f, 6f, SampleRate);
            _f[o + FScream] = CrowdBiquad.BandPass(2600f, 3f, SampleRate);
            _pitch[s] = 140f + 25f * (s % 5) / 4f;
        }
        for (var b = 0; b < Bands; b++) _band[b] = CrowdBiquad.BandPass(BandHz[b], 1.41f, SampleRate);
        Array.Fill(_bandDb, -120f);
    }

    public bool ChantEnabled { get; set; }
    public float MasterGain { get; set; } = 1f;
    public long Blocks { get; private set; }
    public double LastRenderUs { get; private set; }
    public double SynthUs { get; private set; }
    public double SpatialUs { get; private set; }
    public float TotalSplDb { get; private set; }
    public float PeakDbfs { get; private set; } = -120f;
    public float RmsDbfs { get; private set; } = -120f;
    public float LimiterGainDb => 20f * MathF.Log10(MathF.Max(1e-6f, _limGain));
    public long ClipSamples { get; private set; }
    public float SectorSplDb(int s) => _spl[s];
    public float BandDbfs(int b) => _bandDb[b];
    /// <summary>밴드 음압(dB SPL) = 밴드 dBFS + (총 음압 − 전대역 RMS dBFS) 보정.</summary>
    public float BandSplDb(int b) => _bandDb[b] + (TotalSplDb - RmsDbfs);
    public float EnergyL { get; private set; }
    public float EnergyR { get; private set; }
    public bool GoalActive => _goalT > 0f;
    public bool GaspActive => _hitT > 0f;
    public float ChantLevel(int s) => _lChant[s];
    public float BooLevel(int s) => _lBoo[s];
    public float ClapLevel(int s) => _lClap[s];
    public ReadOnlySpan<float> Left => _l;
    public ReadOnlySpan<float> Right => _r;

    private float Next01()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17;
        return (_rng >> 40) / 16777216f;
    }

    private float White() => Next01() * 2f - 1f;

    /// <summary>1인 1 m 음향 출력(dB) — 웅성 62 → 열광 86 · 공포 비명 +8.</summary>
    public static float PersonDb(float arousal, float fear) => 62f + 24f * arousal + 8f * fear;

    /// <summary>1 블록(800 샘플) 합성 → Left/Right(무할당).</summary>
    public void Render(MassiveAgenticCrowdSimulationEngine e, float listenerX, float listenerZ, float listenerYaw)
    {
        var t0 = Stopwatch.GetTimestamp();
        const float dt = Block / (float)SampleRate;
        // ── 이벤트 ──
        if (e.GoalEvents != _seenGoal) { _seenGoal = e.GoalEvents; _goalT = 4f; _goalTeam = e.LastGoalTeam; }
        if (e.HitEvents != _seenHit) { _seenHit = e.HitEvents; _hitT = 1.2f; }
        if (e.CheerEvents != _seenCheer) { _seenCheer = e.CheerEvents; _cheerT = 2.5f; }
        // ── 청취자 거리 Σ1/r² ──
        Array.Clear(_invR2);
        var xs = e.Xs; var zs = e.Zs; var sec = e.SectorIndex;
        const float h2 = ListenerHeight * ListenerHeight;
        for (var i = 0; i < xs.Length; i++)
        {
            float dx = xs[i] - listenerX, dz = zs[i] - listenerZ;
            _invR2[sec[i]] += 1f / MathF.Max(4f, dx * dx + dz * dz + h2);
        }
        // ── 섹터 음압 · 텍스처 레벨 ──
        double total = 0;
        float fx = MathF.Cos(listenerYaw), fz = MathF.Sin(listenerYaw);
        Array.Clear(_l); Array.Clear(_r);
        var beat = (int)(_clock * 4.0) % ChantPattern.Length;   // 120 BPM 8분 음표
        var gateTarget = ChantPattern[beat] == 1 && (_clock * 4.0 % 1.0) < 0.7 ? 1f : 0f;
        var k = MathF.Min(1f, dt * 6f);
        long synth = 0, spatial = 0;
        for (var s = 0; s < Sectors; s++)
        {
            var n = e.SectorCount(s);
            if (n <= 0f) { _spl[s] = 0f; continue; }
            float a = e.SectorArousal(s), f = e.SectorFear(s), st = e.SectorStanding(s), home = e.SectorHomeShare(s);
            var intensity = MathF.Pow(10f, PersonDb(a, f) / 10f) * _invR2[s];
            _spl[s] = 10f * MathF.Log10(MathF.Max(1e-12f, intensity));
            total += intensity;
            var scorer = _goalTeam == 0 ? home : 1f - home;
            var goal = _goalT > 0f ? MathF.Min(1f, _goalT / 2f) : 0f;
            _lRoar[s] += ((0.25f + 0.75f * a) * (1f + goal * scorer) - _lRoar[s]) * k;
            var clapFrac = Math.Clamp((a - 0.35f) / 0.4f, 0f, 1f) + goal * scorer + (_cheerT > 0f ? 0.6f : 0f);
            _lClap[s] += (MathF.Min(1.5f, clapFrac) - _lClap[s]) * k;
            var chant = (ChantEnabled || a > 0.72f) && f < 0.3f ? MathF.Min(1f, a) * MathF.Max(home, 1f - home) : 0f;
            _lChant[s] += (chant - _lChant[s]) * k;
            _lBoo[s] += ((goal > 0f ? goal * (1f - scorer) : 0f) - _lBoo[s]) * k;
            _lGasp[s] += ((_hitT > 0f ? MathF.Min(1f, _hitT) * (0.4f + 0.6f * f) : 0f) - _lGasp[s]) * k;
            _lScream[s] += (f * f - _lScream[s]) * k;
            _lWhoosh[s] += (st - _lWhoosh[s]) * MathF.Min(1f, dt * 12f);
            var ts0 = Stopwatch.GetTimestamp();
            Synthesize(s, n, gateTarget);
            var ts1 = Stopwatch.GetTimestamp();
            synth += ts1 - ts0;
            // 공간화(머리 기준: +X 오른쪽 · −Z 전방)
            float dx = e.SectorX(s) - listenerX, dz = e.SectorZ(s) - listenerZ;
            float fwd = dx * fx + dz * fz, right = -dx * fz + dz * fx;
            var dir = new Vector3(right, ListenerHeight * 0.4f, -fwd);
            if (dir.LengthSquared() < 1e-6f) dir = -Vector3.UnitZ;
            var amp = MathF.Min(1.5f, MathF.Pow(10f, (_spl[s] - 112f) / 20f)) * MasterGain;
            _hrtf.ProcessVoice(s, _mono, dir, amp, _l, _r);
            spatial += Stopwatch.GetTimestamp() - ts1;
        }
        SynthUs = synth * 1e6 / Stopwatch.Frequency; SpatialUs = spatial * 1e6 / Stopwatch.Frequency;
        TotalSplDb = total > 0 ? (float)(10.0 * Math.Log10(total)) : 0f;
        // ── 리미터 · 미터 ──
        var peak = 0f;
        for (var i = 0; i < Block; i++) peak = MathF.Max(peak, MathF.Max(MathF.Abs(_l[i]), MathF.Abs(_r[i])));
        var desired = peak > Ceiling ? Ceiling / peak : 1f;
        _limGain = desired < _limGain ? desired : MathF.Min(desired, _limGain * 1.03f + 0.002f);
        double el = 0, er = 0, em = 0;
        for (var i = 0; i < Block; i++)
        {
            var l = _l[i] * _limGain; var r = _r[i] * _limGain;
            if (l > 1f || l < -1f || r > 1f || r < -1f) ClipSamples++;
            l = Math.Clamp(l, -1f, 1f); r = Math.Clamp(r, -1f, 1f);
            _l[i] = l; _r[i] = r;
            _mix[i] = 0.5f * (l + r);
            el += l * l; er += r * r; em += _mix[i] * _mix[i];
        }
        EnergyL = (float)(el / Block); EnergyR = (float)(er / Block);
        var pk = 0f;
        for (var i = 0; i < Block; i++) pk = MathF.Max(pk, MathF.Max(MathF.Abs(_l[i]), MathF.Abs(_r[i])));
        PeakDbfs = 20f * MathF.Log10(MathF.Max(1e-6f, pk));
        RmsDbfs = 10f * MathF.Log10(MathF.Max(1e-12f, (float)(em / Block)));
        for (var b = 0; b < Bands; b++)
        {
            ref var bq = ref _band[b];
            double acc = 0;
            for (var i = 0; i < Block; i++) { var y = bq.Process(_mix[i]); acc += y * y; }
            var db = 10f * MathF.Log10(MathF.Max(1e-12f, (float)(acc / Block)));
            _bandDb[b] += (db - _bandDb[b]) * 0.35f;
        }
        if (_goalT > 0f) _goalT -= dt;
        if (_hitT > 0f) _hitT -= dt;
        if (_cheerT > 0f) _cheerT -= dt;
        _clock += dt;
        Blocks++;
        LastRenderUs = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
    }

    /// <summary>섹터 텍스처 → _mono(섹터 내부 정규화 · 음압은 공간화 이득으로).</summary>
    private void Synthesize(int s, float people, float gateTarget)
    {
        var o = s * PerSector;
        float roar = _lRoar[s] * (1f + 0.8f * _lWhoosh[s]), clap = _lClap[s], chant = _lChant[s], boo = _lBoo[s], gasp = _lGasp[s], scream = _lScream[s];
        var clapRate = people * 0.5f * clap * 3f;              // 박수 인원 × 3 Hz (섹터 대표 · 밀도 상한 근사)
        var lambda = clapRate / SampleRate;
        var lamSq = MathF.Sqrt(MathF.Min(lambda, 64f));
        var p0 = _pitch[s];
        float inc0 = p0 / SampleRate, inc1 = p0 * 1.011f / SampleRate, inc2 = p0 * 0.987f / SampleRate, incB = 112f / SampleRate;
        var ph = s * 4;
        for (var i = 0; i < Block; i++)
        {
            var w = White();
            var v = roar * (5.5f * _f[o + FRoar].Process(w) + 3.5f * _f[o + FRumble].Process(w));
            if (clap > 1e-3f)
            {
                float exc;
                if (lambda < 0.3f) exc = Next01() < lambda ? White() * 4f : 0f;
                else exc = White() * 1.7f * lamSq / MathF.Max(1f, lamSq);
                v += clap * 2.2f * _f[o + FClap].Process(exc);
            }
            if (chant > 1e-3f || boo > 1e-3f)
            {
                _env[s] += (gateTarget - _env[s]) * 0.0015f;
                _ph[ph] += inc0; if (_ph[ph] >= 1f) _ph[ph] -= 1f;
                _ph[ph + 1] += inc1; if (_ph[ph + 1] >= 1f) _ph[ph + 1] -= 1f;
                _ph[ph + 2] += inc2; if (_ph[ph + 2] >= 1f) _ph[ph + 2] -= 1f;
                var saw = (_ph[ph] + _ph[ph + 1] + _ph[ph + 2]) * (2f / 3f) - 1f + 0.15f * w;
                if (chant > 1e-3f) v += chant * _env[s] * 2.4f * (_f[o + FCh1].Process(saw) + 0.6f * _f[o + FCh2].Process(saw));
                if (boo > 1e-3f)
                {
                    _ph[ph + 3] += incB * (1f + 0.02f * MathF.Sin(_ph[ph] * MathF.Tau)); if (_ph[ph + 3] >= 1f) _ph[ph + 3] -= 1f;
                    var sb = 2f * _ph[ph + 3] - 1f + 0.2f * w;
                    v += boo * 2.6f * (_f[o + FBoo1].Process(sb) + 0.5f * _f[o + FBoo2].Process(sb));
                }
            }
            if (gasp > 1e-3f) v += gasp * 4f * _f[o + FBoo1].Process(w);
            if (scream > 1e-3f) v += scream * 3f * _f[o + FScream].Process(w * (1f + MathF.Sin(_ph[ph] * MathF.Tau * 3f)));
            _mono[i] = v * 0.35f;
        }
    }
}
