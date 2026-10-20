using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PromptFlowStudio.Services.HolographicTelepresence;

/// <summary>원격 햅틱 접촉 이벤트(손→퍽 · 손↔손 하이파이브).</summary>
public struct HapticTouchEvent
{
    public int From, Target;     // Target ≥ 0: 퍽 · &lt; 0: −1 − 상대 참가자(손)
    public Vector3 Position;
    public float Force, Gain;
    public double TimeMs, PlayoutMs;
    public bool Late;
}

/// <summary>참가자 1명 시선 상태.</summary>
public struct GazeState
{
    public Vector3 Head, Intended, Raw, Corrected;
    public int Target, RawTarget;
    public float CorrectionDeg, ErrorDeg;
    public bool Mutual;
}

/// <summary>
/// [PromptFlow Studio v17.25.0] 👁 다자간 공간 시선 · 오디오 · 햅틱 크로스모달 중재 합성기(<see cref="Step"/> 무할당 · 60 Hz 블록).
///   ▸ 시선: v17.22 HMD 6DoF 머리 위치 + v17.12 시선 벡터(원격 캡처/디스플레이 정합 오차 yaw/pitch 포함)를 공유 원탁 좌표에서 교차 → 25° 원뿔 내 최근접 참가자를 응시 대상으로 판정, 눈 위치로 재조준해 상호 아이컨택(i→j ∧ j→i) 복원.
///   ▸ 오디오(48 kHz · 800 샘플/블록): 원격 화자 성대 펄스 합성 → 로컬 청취자 머리 좌표 ITD(≤32 샘플)/ILD 등전력 패닝(v17.23 HRTF 축약) · 거리 감쇠.
///   ▸ AEC: 스피커→마이크 반향 경로(지연 20 + 96 탭) 를 NLMS 128 탭(Vector&lt;float&gt; 내적/갱신)으로 제거 · Geigel 이중 발화 시 적응 동결 · ERLE(dB) 측정.
///   ▸ 햅틱(v17.24): 손-퍽/손-손 접촉 시작 이벤트를 고정 링버퍼(64)에 지터 버퍼 재생 시각으로 적재 → 같은 타임스탬프에서 오디오 충격음(1.8 kHz)과 햅틱 버스트(250 Hz · 2.4 kHz 출력)를 샘플 정합 합성(교차 모달 스큐 측정).
/// </summary>
public sealed class MultiUserCrossModalInteractionSynthesizer
{
    public const int Participants = TelepresenceScenario.Participants, Bodies = TelepresenceScenario.Bodies;
    public const int Fs = 48000, Block = Fs / 60, HapticFs = 2400, HapticBlock = HapticFs / 60, EventRing = 64, MaxVoices = 16, RecentLen = 8;
    public const int AecTaps = 128, EchoDelay = 20, EchoLen = 96, ItdMax = 32, ItdRing = 64;
    public const float ConeDeg = 25f, RawContactDeg = 5f, JitterBufferMs = 60f, GeigelT = 0.5f, Mu = 0.5f;
    private static readonly float[] YawErrDeg = { 0f, 9f, -12f, 15f }, PitchErrDeg = { 0f, -6f, 5f, -8f }, F0 = { 120f, 165f, 210f, 105f };

    private struct Voice { public int Remain, HRemain, Age, HAge; public float Gain, Pan; }

    private readonly GazeState[] _gaze = new GazeState[Participants];
    private readonly Vector3[] _hands = new Vector3[Participants];
    private readonly Vector2[] _bodies = new Vector2[Bodies];
    private readonly bool[] _touch = new bool[Participants * (Bodies + Participants)];
    private readonly float[] _phase = new float[Participants], _env = new float[Participants];
    private readonly float[] _itd = new float[Participants * ItdRing];
    private int _itdPos;
    private readonly float[] _left = new float[Block], _right = new float[Block], _mic = new float[Block], _err = new float[Block], _near = new float[Block], _hap = new float[HapticBlock];
    private readonly float[] _echoH = new float[EchoLen], _farH = new float[2 * (EchoDelay + EchoLen)], _xh = new float[2 * AecTaps], _w = new float[AecTaps];
    private readonly float[] _farAbs = new float[AecTaps];
    private int _farPos, _xPos;
    private readonly HapticTouchEvent[] _ring = new HapticTouchEvent[EventRing], _recent = new HapticTouchEvent[RecentLen];
    private readonly bool[] _pending = new bool[EventRing];
    private int _rCount, _recentHead, _recentCount;
    private readonly Voice[] _voices = new Voice[MaxVoices];
    private uint _rng = 0x2545F491u;

    public MultiUserCrossModalInteractionSynthesizer()
    {
        // 결정적 실내 반향 임펄스(감쇠 · Σ|h| ≈ 0.45 → Geigel 0.5 보장)
        var rng = 0x1234567u;
        var sum = 0f;
        for (var k = 0; k < EchoLen; k++)
        {
            rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
            var u = (rng >> 8) * (1f / 16777216f) * 2f - 1f;
            _echoH[k] = u * MathF.Exp(-k / 22f) * (k == 0 ? 3f : 1f);
            sum += MathF.Abs(_echoH[k]);
        }
        for (var k = 0; k < EchoLen; k++) _echoH[k] *= 0.45f / sum;
        for (var p = 0; p < Participants; p++) _gaze[p].Target = _gaze[p].RawTarget = -1;
    }

    public bool EyeContactCorrection { get; set; } = true;
    public bool AecEnabled { get; set; } = true;
    public bool HapticsEnabled { get; set; } = true;
    public bool UseSimd { get; set; } = true;
    public int Speaker { get; private set; }
    public int Addressee { get; private set; }
    public int MutualPairs { get; private set; }
    public float MeanCorrectionDeg { get; private set; }
    public float MeanErrorDeg { get; private set; }
    public float ErleDb { get; private set; }
    public bool DoubleTalk { get; private set; }
    public long DoubleTalkBlocks { get; private set; }
    public float EchoRms { get; private set; }
    public float ResidualRms { get; private set; }
    public float RmsLeft { get; private set; }
    public float RmsRight { get; private set; }
    public float HapticRms { get; private set; }
    public float HapticPeak { get; private set; }
    public long Events { get; private set; }
    public long LateEvents { get; private set; }
    public long DroppedEvents { get; private set; }
    public long PlayedEvents { get; private set; }
    public float MaxSkewMs { get; private set; }
    public int ActiveVoices { get; private set; }
    public long Blocks { get; private set; }
    public int PendingEvents => _rCount;
    public ref readonly GazeState Gaze(int p) => ref _gaze[p];
    public Vector3 HandPosition(int p) => _hands[p];
    public ReadOnlySpan<float> Left => _left;
    public ReadOnlySpan<float> Right => _right;
    public ReadOnlySpan<float> Mic => _mic;
    public ReadOnlySpan<float> Residual => _err;
    public ReadOnlySpan<float> Haptic => _hap;
    public float AecWeight(int k) => _w[k];
    public float EchoTap(int k) => _echoH[k];
    public int RecentCount => _recentCount;
    public ref readonly HapticTouchEvent Recent(int i) => ref _recent[(_recentHead - 1 - i + RecentLen * 2) % RecentLen];

    public static Vector3 HeadPosition(int p, double ms)
    {
        var s = TelepresenceScenario.Seat(p) * 0.9f;
        var t = (float)(ms / 1000.0);
        return new Vector3(s.X, TelepresenceScenario.HeadY + 0.01f * MathF.Sin(2f * MathF.PI * 0.25f * t + p), s.Z);
    }

    public static float AngleDeg(Vector3 a, Vector3 b) => MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1f, 1f)) * (180f / MathF.PI);

    /// <summary>방향 벡터에 방위/고도 오차(도) 적용.</summary>
    public static Vector3 Perturb(Vector3 d, float yawDeg, float pitchDeg)
    {
        d = Vector3.Normalize(d);
        var az = MathF.Atan2(d.X, d.Z) + yawDeg * (MathF.PI / 180f);
        var el = MathF.Asin(Math.Clamp(d.Y, -1f, 1f)) + pitchDeg * (MathF.PI / 180f);
        var c = MathF.Cos(el);
        return new Vector3(c * MathF.Sin(az), MathF.Sin(el), c * MathF.Cos(az));
    }

    /// <summary>60 Hz 블록 1회: 시선 교차/보정 → 접촉 이벤트 적재 → 오디오 공간화 + AEC → 이벤트 재생(오디오/햅틱 정합).</summary>
    public void Step(double blockMs, ReadOnlySpan<Vector3> hands, ReadOnlySpan<Vector2> bodies, ReadOnlySpan<float> oneWayMs)
    {
        for (var p = 0; p < Participants; p++) _hands[p] = hands[p];
        for (var k = 0; k < Bodies; k++) _bodies[k] = bodies[k];
        UpdateGaze(blockMs);
        DetectContacts(blockMs, oneWayMs);
        RenderAudio(blockMs);
        Blocks++;
    }

    private void UpdateGaze(double ms)
    {
        var t = (float)(ms / 1000.0);
        Speaker = (int)(t / 3f) % Participants;
        Addressee = (Speaker + 1 + (int)(t / 1.5f) % (Participants - 1)) % Participants;
        for (var p = 0; p < Participants; p++) _gaze[p].Head = HeadPosition(p, ms);
        for (var p = 0; p < Participants; p++)
        {
            ref var g = ref _gaze[p];
            var tgt = p == Speaker ? Addressee : Speaker;
            g.Intended = Vector3.Normalize(_gaze[tgt].Head - g.Head);
            var sacc = 0.8f * MathF.Sin(2f * MathF.PI * 1.7f * t + 2.1f * p);
            g.Raw = Perturb(g.Intended, YawErrDeg[p] + sacc, PitchErrDeg[p]);
            // 공유 좌표 교차: 원뿔 내 최근접 눈 위치
            int best = -1, rawBest = -1;
            float bestA = ConeDeg;
            for (var q = 0; q < Participants; q++)
            {
                if (q == p) continue;
                var a = AngleDeg(g.Raw, _gaze[q].Head - g.Head);
                if (a < bestA) { bestA = a; best = q; }
                if (a < RawContactDeg) rawBest = q;
            }
            g.RawTarget = rawBest;
            if (EyeContactCorrection && best >= 0)
            {
                g.Corrected = Vector3.Normalize(_gaze[best].Head - g.Head);
                g.Target = best;
            }
            else { g.Corrected = g.Raw; g.Target = rawBest; }
            g.CorrectionDeg = AngleDeg(g.Raw, g.Corrected);
            g.ErrorDeg = AngleDeg(g.Corrected, g.Intended);
        }
        var mutual = 0;
        float sc = 0f, se = 0f;
        for (var p = 0; p < Participants; p++)
        {
            ref var g = ref _gaze[p];
            g.Mutual = g.Target >= 0 && _gaze[g.Target].Target == p;
            if (g.Mutual && p < g.Target) mutual++;
            sc += g.CorrectionDeg; se += g.ErrorDeg;
        }
        MutualPairs = mutual;
        MeanCorrectionDeg = sc / Participants;
        MeanErrorDeg = se / Participants;
    }

    private void DetectContacts(double ms, ReadOnlySpan<float> oneWayMs)
    {
        const int stride = Bodies + Participants;
        const float rhb = TelepresenceScenario.HandR + TelepresenceScenario.BodyR;
        for (var p = 0; p < Participants; p++)
        {
            var h = _hands[p];
            var press = h.Y < TelepresenceScenario.TableY + 0.1f;
            for (var k = 0; k < Bodies; k++)
            {
                var d = Vector2.Distance(new Vector2(h.X, h.Z), _bodies[k]);
                var on = press && d < rhb;
                ref var was = ref _touch[p * stride + k];
                if (on && !was) Enqueue(p, k, new Vector3(_bodies[k].X, TelepresenceScenario.TableY, _bodies[k].Y), DeterministicRollbackStateResimulationGuard.HandK * (rhb - d), ms, oneWayMs[p]);
                was = on;
            }
            for (var q = p + 1; q < Participants; q++)
            {
                var d = Vector3.Distance(h, _hands[q]);
                var on = d < 0.1f;
                ref var was = ref _touch[p * stride + Bodies + q];
                if (on && !was) Enqueue(p, -1 - q, (h + _hands[q]) * 0.5f, 40f * (0.1f - d) + 1f, ms, MathF.Max(oneWayMs[p], oneWayMs[q]));
                was = on;
            }
        }
    }

    private void Enqueue(int from, int target, Vector3 pos, float force, double ms, float oneWay)
    {
        Events++;
        if (_rCount >= EventRing) { DroppedEvents++; return; }
        var local = from == 0;   // 로컬 손이 당사자 → 즉시 재생
        var arrive = ms + oneWay;
        var play = ms + JitterBufferMs;
        var late = !local && arrive > play;
        if (late) { LateEvents++; play = arrive; }
        var gain = local ? 1f : MathF.Exp(-Vector3.Distance(pos, _hands[0]) / 0.35f);
        var e = new HapticTouchEvent { From = from, Target = target, Position = pos, Force = force, Gain = gain, TimeMs = ms, PlayoutMs = local ? ms : play, Late = late };
        var slot = 0;
        while (_pending[slot]) slot++;
        _ring[slot] = e;
        _pending[slot] = true;
        _rCount++;
        _recent[_recentHead] = e;
        _recentHead = (_recentHead + 1) % RecentLen;
        if (_recentCount < RecentLen) _recentCount++;
    }

    private void RenderAudio(double ms)
    {
        var t0 = (float)(ms / 1000.0);
        var head = _gaze[0].Head;
        var fwd = Vector3.Normalize(new Vector3(-head.X, 0f, -head.Z));
        var right = new Vector3(-fwd.Z, 0f, fwd.X);
        Span<float> gl = stackalloc float[Participants];
        Span<float> gr = stackalloc float[Participants];
        Span<int> dly = stackalloc int[Participants];
        Span<float> pan = stackalloc float[Participants];
        for (var p = 1; p < Participants; p++)
        {
            var rel = _gaze[p].Head - head;
            var dist = MathF.Max(rel.Length(), 0.5f);
            var x = Vector3.Dot(Vector3.Normalize(rel), right);
            pan[p] = Math.Clamp(x, -1f, 1f);
            gl[p] = MathF.Sqrt(0.5f * (1f - pan[p])) / dist;
            gr[p] = MathF.Sqrt(0.5f * (1f + pan[p])) / dist;
            dly[p] = (int)MathF.Round(MathF.Abs(pan[p]) * ItdMax);
        }
        // 이벤트 → 보이스(같은 재생 시각에서 오디오/햅틱 온셋 산출)
        var blockEnd = ms + 1000.0 / 60.0;
        for (var slot = 0; slot < EventRing && _rCount > 0; slot++)
        {
            if (!_pending[slot] || _ring[slot].PlayoutMs >= blockEnd) continue;
            ref readonly var e = ref _ring[slot];
            var off = Math.Max(0.0, e.PlayoutMs - ms);
            var aOn = (int)(off * Fs / 1000.0);
            var hOn = (int)(off * HapticFs / 1000.0);
            var skew = (float)Math.Abs(aOn * 1000.0 / Fs - hOn * 1000.0 / HapticFs);
            if (skew > MaxSkewMs) MaxSkewMs = skew;
            for (var v = 0; v < MaxVoices; v++)
            {
                if (_voices[v].Remain > 0 || _voices[v].HRemain > 0) continue;
                var rel = e.Position - head;
                _voices[v] = new Voice { Remain = Fs / 40 + aOn, HRemain = HapticFs / 16 + hOn, Age = -aOn, HAge = -hOn, Gain = MathF.Min(1f, 0.15f + 0.05f * e.Force) * e.Gain, Pan = Math.Clamp(Vector3.Dot(Vector3.Normalize(rel + new Vector3(1e-4f, 0f, 0f)), right), -1f, 1f) };
                break;
            }
            PlayedEvents++;
            _pending[slot] = false;
            _rCount--;
        }
        // 원격 화자 합성 + 공간화
        double el = 0, er = 0, echoP = 0, resP = 0, farP = 0;
        var dtAny = false;
        var spk = Speaker;
        for (var i = 0; i < Block; i++)
        {
            var t = t0 + i * (1f / Fs);
            var l = 0f; var r = 0f; var near = 0f;
            for (var p = 0; p < Participants; p++)
            {
                var targetEnv = p == spk ? 0.35f * (0.55f + 0.45f * MathF.Sin(2f * MathF.PI * 4f * t + p)) : 0f;
                _env[p] += 0.002f * (targetEnv - _env[p]);
                var f0 = F0[p] * (1f + 0.02f * MathF.Sin(2f * MathF.PI * 5f * t));
                _phase[p] += f0 / Fs;
                if (_phase[p] >= 1f) _phase[p] -= 1f;
                var ph = _phase[p];
                var src = _env[p] * (MathF.Sin(2f * MathF.PI * ph) + 0.5f * MathF.Sin(4f * MathF.PI * ph) + 0.25f * MathF.Sin(6f * MathF.PI * ph));
                if (p == 0) { near = src; continue; }
                var ring = p * ItdRing;
                _itd[ring + _itdPos] = src;
                var delayed = _itd[ring + ((_itdPos - dly[p] + ItdRing) & (ItdRing - 1))];
                l += gl[p] * (pan[p] > 0f ? delayed : src);
                r += gr[p] * (pan[p] < 0f ? delayed : src);
            }
            _itdPos = (_itdPos + 1) & (ItdRing - 1);
            // 충격음 보이스
            for (var v = 0; v < MaxVoices; v++)
            {
                ref var vo = ref _voices[v];
                if (vo.Remain <= 0) continue;
                vo.Remain--;
                var age = vo.Age++;
                if (age < 0) continue;
                var s = vo.Gain * MathF.Exp(-age / (0.006f * Fs)) * MathF.Sin(2f * MathF.PI * 1800f * age / Fs);
                l += s * MathF.Sqrt(0.5f * (1f - vo.Pan));
                r += s * MathF.Sqrt(0.5f * (1f + vo.Pan));
            }
            _left[i] = l; _right[i] = r;
            el += l * l; er += r * r;
            // 스피커 → 마이크 반향
            var far = 0.5f * (l + r);
            const int fl = EchoDelay + EchoLen;
            _farH[_farPos] = far; _farH[_farPos + fl] = far;
            _farPos = (_farPos + 1) % fl;
            var echo = 0f;
            for (var k = 0; k < EchoLen; k++) echo += _echoH[k] * _farH[_farPos + fl - 1 - EchoDelay - k];
            _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
            var noise = ((_rng >> 8) * (1f / 16777216f) - 0.5f) * 2e-4f;
            var mic = near + echo + noise;
            _near[i] = near; _mic[i] = mic;
            // NLMS
            _xh[_xPos] = far; _xh[_xPos + AecTaps] = far;
            _farAbs[_xPos] = MathF.Abs(far);
            _xPos = (_xPos + 1) % AecTaps;
            var win = _xh.AsSpan(_xPos, AecTaps);
            float e;
            if (AecEnabled)
            {
                var y = Dot(_w, win);
                e = mic - y;
                var maxFar = 0f;
                for (var k = 0; k < AecTaps; k++) if (_farAbs[k] > maxFar) maxFar = _farAbs[k];
                if (MathF.Abs(mic) > GeigelT * maxFar + 1e-3f) dtAny = true;   // Geigel: 반향 상한 초과 → 근단 발화
                else
                {
                    var pw = Dot(win, win) + 1e-4f;
                    Axpy(Mu * e / pw, win, _w);
                }
            }
            else e = mic;
            _err[i] = e;
            farP += far * far;
            echoP += echo * echo;
            var res = e - near;
            resP += res * res;
        }
        RmsLeft = (float)Math.Sqrt(el / Block);
        RmsRight = (float)Math.Sqrt(er / Block);
        EchoRms = (float)Math.Sqrt(echoP / Block);
        ResidualRms = (float)Math.Sqrt(resP / Block);
        DoubleTalk = dtAny;
        if (dtAny) DoubleTalkBlocks++;
        else if (farP > 1e-4)
        {
            var erle = 10f * MathF.Log10((float)((echoP + 1e-12) / (resP + 1e-12)));
            ErleDb += 0.1f * (erle - ErleDb);
        }
        // 햅틱 2.4 kHz
        double hp = 0;
        var pk = 0f;
        var active = 0;
        for (var i = 0; i < HapticBlock; i++)
        {
            var s = 0f;
            for (var v = 0; v < MaxVoices; v++)
            {
                ref var vo = ref _voices[v];
                if (vo.HRemain <= 0) continue;
                vo.HRemain--;
                var age = vo.HAge++;
                if (age < 0 || !HapticsEnabled) continue;
                s += vo.Gain * MathF.Exp(-age / (0.025f * HapticFs)) * MathF.Sin(2f * MathF.PI * 250f * age / HapticFs);
            }
            _hap[i] = s;
            hp += s * s;
            if (MathF.Abs(s) > pk) pk = MathF.Abs(s);
        }
        for (var v = 0; v < MaxVoices; v++) if (_voices[v].Remain > 0 || _voices[v].HRemain > 0) active++;
        ActiveVoices = active;
        HapticRms = (float)Math.Sqrt(hp / HapticBlock);
        if (pk > HapticPeak) HapticPeak = pk;
    }

    public void ResetMetrics()
    {
        Events = LateEvents = DroppedEvents = PlayedEvents = DoubleTalkBlocks = 0;
        MaxSkewMs = HapticPeak = 0f;
    }

    private float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var i = 0;
        var s = 0f;
        var w = Vector<float>.Count;
        if (UseSimd && Vector.IsHardwareAccelerated && a.Length >= w)
        {
            ref var ra = ref MemoryMarshal.GetReference(a);
            ref var rb = ref MemoryMarshal.GetReference(b);
            var acc = Vector<float>.Zero;
            for (; i <= a.Length - w; i += w) acc += Unsafe.ReadUnaligned<Vector<float>>(ref Unsafe.As<float, byte>(ref Unsafe.Add(ref ra, i))) * Unsafe.ReadUnaligned<Vector<float>>(ref Unsafe.As<float, byte>(ref Unsafe.Add(ref rb, i)));
            s = Vector.Dot(acc, Vector<float>.One);
        }
        for (; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    private void Axpy(float k, ReadOnlySpan<float> x, Span<float> y)
    {
        var i = 0;
        var w = Vector<float>.Count;
        if (UseSimd && Vector.IsHardwareAccelerated && x.Length >= w)
        {
            ref var rx = ref MemoryMarshal.GetReference(x);
            ref var ry = ref MemoryMarshal.GetReference(y);
            var vk = new Vector<float>(k);
            for (; i <= x.Length - w; i += w)
            {
                ref var dst = ref Unsafe.As<float, byte>(ref Unsafe.Add(ref ry, i));
                Unsafe.WriteUnaligned(ref dst, Unsafe.ReadUnaligned<Vector<float>>(ref dst) + vk * Unsafe.ReadUnaligned<Vector<float>>(ref Unsafe.As<float, byte>(ref Unsafe.Add(ref rx, i))));
            }
        }
        for (; i < x.Length; i++) y[i] += k * x[i];
    }
}
