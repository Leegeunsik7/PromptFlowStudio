using System.Numerics;
using PromptFlowStudio.Services.CognitiveCinematography;

namespace PromptFlowStudio.Services.SpatialAcoustics;

/// <summary>[PromptFlow Studio v17.23.0] 직접음 차폐 · 회절 판정 결과.</summary>
public struct OcclusionResult
{
    public bool Occluded;
    public int Occluder;
    public float DirectM;
    public float PathM;
    public float DeltaM;
    public float AttLowDb;
    public float AttHighDb;
    public Vector3 Edge;
}

/// <summary>
/// [PromptFlow Studio v17.23.0] 🛡️ 동적 도플러 효과 &amp; 회절/차폐 위상 방어 가드(무할당 · 슬롯별 상태).
///   ▸ 도플러: 경로 지연(샘플)을 블록 내 표본 단위 선형 램프로 보간 읽기 — 상대 속도에 따른 피치 시프트가 물리적으로 발생(비율 = 1 − Δd/Δn).
///     |Δd/Δn| &gt; <see cref="MaxSlew"/>(±25 %) 는 운동이 아닌 경로 점프로 간주.
///   ▸ 차폐/회절: 직접음 선분이 v17.21 세트 프리미티브(액터 · 기둥 · 상자)에 막히면 최소 SDF 지점에서 표면 밖 모서리를 잡아
///     S→모서리→L 회절 경로 길이 · 경로차 δ → Maekawa 감쇠(250 Hz 저역 · 4 kHz 고역 — 고역이 더 깎이고 저역은 돌아 들어온다).
///   ▸ 위상 가드: 경로 전환(직접 ↔ 회절) · 과대 지연 점프는 이전 헤드(이전 지연)를 Hann 페이드아웃하고 새 헤드를 상보 페이드인(20 ms),
///     1.5 dB 초과 이득 단계는 상승 코사인 윈도우(30 ms)로 스무딩 — 블록 경계 위상 튐 · 팝핑 노이즈 차단. <see cref="Enabled"/> = false 면 즉시 점프(비교용).
/// </summary>
public sealed class DopplerDiffractionOcclusionPhaseGuard
{
    public const int FadeSamples = 960, WindowSamples = 1440, Mask = Hrtf6DoFBinauralSpatialAudioSynthesizer.Mask;
    public const float MaxSlew = 0.25f, JumpDb = 1.5f, EdgeMargin = 0.05f, MaxAttDb = 24f, LowHz = 250f, HighHz = 4000f, SoundSpeed = 343f;

    private readonly int _slots;
    private readonly float[] _delay, _gLo, _gHi, _fromLo, _fromHi, _toLo, _toHi, _xDelay, _xLo, _xHi, _d0, _d1, _doppler;
    private readonly int[] _gPos, _gLen, _xLeft;
    private readonly bool[] _active, _diffracted, _gLin;

    public DopplerDiffractionOcclusionPhaseGuard(int slots)
    {
        _slots = slots;
        _delay = new float[slots]; _gLo = new float[slots]; _gHi = new float[slots];
        _fromLo = new float[slots]; _fromHi = new float[slots]; _toLo = new float[slots]; _toHi = new float[slots];
        _xDelay = new float[slots]; _xLo = new float[slots]; _xHi = new float[slots];
        _d0 = new float[slots]; _d1 = new float[slots]; _doppler = new float[slots];
        _gPos = new int[slots]; _gLen = new int[slots]; _xLeft = new int[slots];
        _active = new bool[slots]; _diffracted = new bool[slots]; _gLin = new bool[slots];
        Array.Fill(_doppler, 1f);
    }

    public bool Enabled { get; set; } = true;
    public int SlotCount => _slots;
    public long Crossfades { get; private set; }
    public long Windowed { get; private set; }
    public long Activations { get; private set; }
    public long SlewJumps { get; private set; }
    public long OcclusionToggles { get; private set; }
    public float Doppler(int slot) => _doppler[slot];
    public bool Active(int slot) => _active[slot];
    public float DelaySamples(int slot) => _delay[slot];
    public bool IsAudible(int slot) => _active[slot] || _xLeft[slot] > 0;

    /// <summary>블록 계획: 목표 지연 · 대역 이득 · 회절 여부 → 도플러 램프 / 교차 페이드 / 윈도우 이득 전이 결정.</summary>
    public void Begin(int slot, bool valid, float delay, float gLo, float gHi, bool diffracted, int n)
    {
        if (!Enabled)
        {
            // 미가드: 블록 경계 즉시 점프(지연 · 이득 · 위상)
            _active[slot] = valid;
            _delay[slot] = delay; _d0[slot] = delay; _d1[slot] = delay;
            _gLo[slot] = valid ? gLo : 0f; _gHi[slot] = valid ? gHi : 0f;
            _gLen[slot] = 0; _xLeft[slot] = 0; _doppler[slot] = 1f;
            if (_diffracted[slot] != diffracted && valid) OcclusionToggles++;
            _diffracted[slot] = diffracted;
            return;
        }
        var tLo = valid ? gLo : 0f;
        var tHi = valid ? gHi : 0f;
        if (!_active[slot])
        {
            _d0[slot] = _d1[slot] = _delay[slot];
            _doppler[slot] = 1f;
            if (!valid) return;
            _active[slot] = true;
            _delay[slot] = delay; _d0[slot] = delay; _d1[slot] = delay;
            _gLo[slot] = 0f; _gHi[slot] = 0f;
            StartWindow(slot, 0f, 0f, tLo, tHi, FadeSamples, false);
            _diffracted[slot] = diffracted;
            Activations++;
            return;
        }
        var switched = valid && diffracted != _diffracted[slot];
        if (switched) OcclusionToggles++;
        _diffracted[slot] = diffracted;
        if (valid)
        {
            var dd = delay - _delay[slot];
            if (switched || MathF.Abs(dd) > MaxSlew * n)
            {
                // 위상 점프 방지: 이전 헤드(이전 지연 · 현재 이득) 페이드아웃 + 새 헤드(새 지연) 상보 페이드인
                _xDelay[slot] = _delay[slot];
                _xLo[slot] = CurrentLo(slot, 0); _xHi[slot] = CurrentHi(slot, 0);
                _xLeft[slot] = FadeSamples;
                _delay[slot] = delay; _d0[slot] = delay; _d1[slot] = delay;
                _doppler[slot] = 1f;
                StartWindow(slot, 0f, 0f, tLo, tHi, FadeSamples, false);
                Crossfades++;
                if (!switched) SlewJumps++;
                return;
            }
            _d0[slot] = _delay[slot]; _d1[slot] = delay;
            _doppler[slot] = 1f - dd / n;
        }
        else
        {
            _d0[slot] = _d1[slot] = _delay[slot];
            _doppler[slot] = 1f;
        }
        SetGainTarget(slot, tLo, tHi, n);
    }

    private void SetGainTarget(int slot, float tLo, float tHi, int n)
    {
        if (_gLen[slot] > 0 && _gPos[slot] < _gLen[slot] && !_gLin[slot])
        {
            // 윈도우 전이 진행 중 — 목적지 미세 변동은 목적지만 갱신(윈도우 유지)
            if (StepDb(tLo, _toLo[slot]) <= JumpDb && StepDb(tHi, _toHi[slot]) <= JumpDb) { _toLo[slot] = tLo; _toHi[slot] = tHi; return; }
        }
        var cl = CurrentLo(slot, 0); var ch = CurrentHi(slot, 0);
        if (StepDb(tLo, cl) > JumpDb || StepDb(tHi, ch) > JumpDb)
        {
            StartWindow(slot, cl, ch, tLo, tHi, WindowSamples, false);
            Windowed++;
        }
        else StartWindow(slot, cl, ch, tLo, tHi, n, true);
    }

    private static float StepDb(float a, float b) => MathF.Abs(20f * MathF.Log10((a + 1e-6f) / (b + 1e-6f)));

    private void StartWindow(int slot, float fl, float fh, float tl, float th, int len, bool linear)
    {
        _fromLo[slot] = fl; _fromHi[slot] = fh; _toLo[slot] = tl; _toHi[slot] = th;
        _gPos[slot] = 0; _gLen[slot] = len; _gLin[slot] = linear;
    }

    private float Shape(int slot, int i)
    {
        var u = MathF.Min(1f, (_gPos[slot] + i) / (float)_gLen[slot]);
        return _gLin[slot] ? u : 0.5f - 0.5f * MathF.Cos(MathF.PI * u);
    }

    /// <summary>블록 내 i 번째 표본 시점 저역 이득.</summary>
    public float CurrentLo(int slot, int i) => _gLen[slot] == 0 ? _gLo[slot] : _fromLo[slot] + (_toLo[slot] - _fromLo[slot]) * Shape(slot, i);
    public float CurrentHi(int slot, int i) => _gLen[slot] == 0 ? _gHi[slot] : _fromHi[slot] + (_toHi[slot] - _fromHi[slot]) * Shape(slot, i);

    /// <summary>
    /// 슬롯 신호 읽기(상태 불변 — 귀별로 2회 호출 가능). 지연 = 블록 램프(도플러) + 귀 오프셋 램프(ITD/2),
    /// 링은 선형 분수 지연 보간 · 저/고 대역 이득 적용 · 교차 페이드 중이면 이전 헤드를 Hann 가중으로 합산.
    /// </summary>
    public void Read(int slot, ReadOnlySpan<float> lo, ReadOnlySpan<float> hi, long blockStart, float ear0, float ear1, Span<float> dst)
    {
        var n = dst.Length;
        var invN = 1f / n;
        float d0 = _d0[slot], d1 = _d1[slot];
        var xLeft = _xLeft[slot];
        float xd = _xDelay[slot], xl = _xLo[slot], xh = _xHi[slot];
        for (var i = 0; i < n; i++)
        {
            var u = (i + 1) * invN;
            var ear = ear0 + (ear1 - ear0) * u;
            var d = d0 + (d1 - d0) * u + ear;
            var v = Tap(lo, hi, blockStart + i, d, CurrentLo(slot, i + 1), CurrentHi(slot, i + 1));
            if (i < xLeft)
            {
                var r = xLeft - i;
                var w = 0.5f - 0.5f * MathF.Cos(MathF.PI * r / FadeSamples);
                v += w * Tap(lo, hi, blockStart + i, xd + ear, xl, xh);
            }
            dst[i] = v;
        }
    }

    private static float Tap(ReadOnlySpan<float> lo, ReadOnlySpan<float> hi, long at, float delay, float gl, float gh)
    {
        if (delay < 0f) delay = 0f;
        var df = MathF.Floor(delay);
        var f = delay - df;
        var idx = at - (long)df;
        var i0 = (int)(idx & Mask); var i1 = (int)((idx - 1) & Mask);
        var l = lo[i0] + (lo[i1] - lo[i0]) * f;
        var h = hi[i0] + (hi[i1] - hi[i0]) * f;
        return l * gl + h * gh;
    }

    /// <summary>블록 커밋: 지연 = 램프 끝 · 이득 윈도우 전진 · 교차 페이드 잔여 감소 · 페이드아웃 완료 시 비활성.</summary>
    public void End(int slot, int n)
    {
        _delay[slot] = _d1[slot];
        if (_gLen[slot] > 0)
        {
            _gPos[slot] += n;
            if (_gPos[slot] >= _gLen[slot])
            {
                _gLo[slot] = _toLo[slot]; _gHi[slot] = _toHi[slot];
                _gLen[slot] = 0; _gPos[slot] = 0;
            }
        }
        _xLeft[slot] = Math.Max(0, _xLeft[slot] - n);
        if (Enabled && _active[slot] && _gLen[slot] == 0 && _gLo[slot] == 0f && _gHi[slot] == 0f) _active[slot] = false;
    }

    public void Reset()
    {
        Array.Clear(_active); Array.Clear(_gLo); Array.Clear(_gHi); Array.Clear(_gLen); Array.Clear(_gPos); Array.Clear(_xLeft); Array.Clear(_diffracted);
        Array.Fill(_doppler, 1f);
    }

    /// <summary>Maekawa 삽입 손실(dB) — 프레넬 수 N = 2δf/c, N ≤ −0.19 이면 0, 상한 24 dB.</summary>
    public static float Maekawa(float deltaM, float hz)
    {
        var nf = 2f * deltaM * hz / SoundSpeed;
        return nf <= -0.19f ? 0f : MathF.Min(MaxAttDb, 10f * MathF.Log10(3f + 20f * nf));
    }

    /// <summary>
    /// 직접음 차폐 · 회절 판정(무할당). ownerExclude = 발화 액터(자기 머리 · 몸통은 자기 목소리를 가리지 않음, −1 = 없음).
    /// 최근접 차폐 프리미티브의 선분 위 최소 SDF 지점에서 LOS 수직 방향으로 표면 밖(+5 cm) 모서리 → S→모서리→L.
    /// </summary>
    public static bool EvaluateDirect(CinematicBlockingSet set, Vector3 src, Vector3 lis, int ownerExclude, out OcclusionResult r)
    {
        r = default;
        var dv = lis - src;
        var len = dv.Length();
        r.DirectM = len;
        r.PathM = len;
        if (len < 1e-3f) return false;
        var dir = dv / len;
        var best = len - 0.05f;
        var k0 = -1;
        for (var k = 0; k < set.PrimCount; k++)
        {
            if (ownerExclude >= 0 && set.Owner(k) == ownerExclude) continue;
            var t = set.Intersect(k, src, dir);
            if (t < best) { best = t; k0 = k; }
        }
        if (k0 < 0) return false;
        var minS = float.MaxValue;
        var pm = src;
        for (var i = 1; i < 32; i++)
        {
            var p = src + dir * (len * i / 32f);
            var s = set.Sdf(k0, p);
            if (s < minS) { minS = s; pm = p; }
        }
        var nrm = set.Normal(k0, pm);
        nrm -= dir * Vector3.Dot(nrm, dir);
        if (!(nrm.LengthSquared() > 1e-6f))
        {
            nrm = Vector3.Cross(dir, Vector3.UnitY);
            if (!(nrm.LengthSquared() > 1e-6f)) nrm = Vector3.UnitX;
        }
        nrm = Vector3.Normalize(nrm);
        var push = MathF.Max(0f, -minS) + EdgeMargin;
        var edge = pm + nrm * push;
        // 모서리가 아직 프리미티브 내부면 바깥으로 2배씩 밀어냄(최대 4회)
        for (var it = 0; it < 4 && set.Sdf(k0, edge) < 0f; it++) { push *= 2f; edge = pm + nrm * push; }
        var path = Vector3.Distance(src, edge) + Vector3.Distance(edge, lis);
        r.Occluded = true;
        r.Occluder = k0;
        r.Edge = edge;
        r.PathM = path;
        r.DeltaM = MathF.Max(0f, path - len);
        r.AttLowDb = Maekawa(r.DeltaM, LowHz);
        r.AttHighDb = Maekawa(r.DeltaM, HighHz);
        return true;
    }

    /// <summary>판정 결과를 직접음 경로에 적용 — 회절 경로 길이 · 모서리 도래 방향 · 거리비 × 대역별 Maekawa 감쇠.</summary>
    public static void Apply(ref AcousticPath p, in OcclusionResult r)
    {
        if (!r.Occluded) { p.Diffracted = false; return; }
        var scale = p.LengthM / MathF.Max(r.PathM, p.LengthM);
        p.LengthM = MathF.Max(r.PathM, p.LengthM);
        p.Via = r.Edge;
        p.Diffracted = true;
        p.GainLow *= scale * MathF.Pow(10f, -r.AttLowDb / 20f);
        p.GainHigh *= scale * MathF.Pow(10f, -r.AttHighDb / 20f);
    }
}
