using System.Numerics;
using System.Runtime.Intrinsics;

namespace PromptFlowStudio.Services.SpatialAcoustics;

/// <summary>
/// [PromptFlow Studio v17.23.0] 48 kHz 파라메트릭 HRTF 필터 세트(Brown–Duda 구형 머리 그림자 1극/1영점 + 귓바퀴 5반사 · 64탭).
/// 방위 15° × 24 · 고도 −45°–90° 15° × 10 격자(ITD 분리형 — ITD 는 귀별 지연선 읽기로 적용해 보간 시 콤 필터가 없다)
/// + 수평 45° 가상 스피커 8개(ITD 내장형). 모든 필터는 시간 역순 저장(<see cref="AcousticKernels.Dot64Simd"/> 직결).
/// 결선점: 실측 SOFA HRIR 세트로 <see cref="Synthesize"/> 대체 시 상위 무변경.
/// </summary>
public sealed class HrtfFilterSet
{
    public const int Taps = AcousticKernels.Taps, AzCount = 24, ElCount = 10, AzStepDeg = 15, ElMinDeg = -45, ElStepDeg = 15, VirtualBins = 8, Base = 2;
    public const float HeadRadius = 0.0875f, SoundSpeed = 343f, Fs = 48000f;

    private static readonly float[] PinnaRho = { 0.5f, -1f, 0.5f, -0.25f, 0.25f };
    private static readonly float[] PinnaA = { 1f, 5f, 5f, 5f, 5f };
    private static readonly float[] PinnaB = { 2f, 4f, 7f, 11f, 13f };
    private static readonly float[] PinnaD = { 1f, 0.5f, 0.5f, 0.5f, 0.5f };

    private readonly float[] _set = new float[AzCount * ElCount * 2 * Taps];
    private readonly float[] _bins = new float[VirtualBins * 2 * Taps];

    public static HrtfFilterSet Shared { get; } = new();

    private HrtfFilterSet()
    {
        Span<float> l = stackalloc float[Taps];
        Span<float> r = stackalloc float[Taps];
        for (var e = 0; e < ElCount; e++)
            for (var a = 0; a < AzCount; a++)
            {
                Synthesize(a * AzStepDeg, ElMinDeg + e * ElStepDeg, false, l, r);
                StoreReversed(_set, (e * AzCount + a) * 2, l, r);
            }
        for (var b = 0; b < VirtualBins; b++)
        {
            Synthesize(b * 45f, 0f, true, l, r);
            StoreReversed(_bins, b * 2, l, r);
        }
    }

    private static void StoreReversed(float[] dst, int pair, ReadOnlySpan<float> l, ReadOnlySpan<float> r)
    {
        for (var k = 0; k < Taps; k++)
        {
            dst[pair * Taps + Taps - 1 - k] = l[k];
            dst[(pair + 1) * Taps + Taps - 1 - k] = r[k];
        }
    }

    /// <summary>우드워스 ITD(샘플 · + = 음원 우측 → 왼귀 지연). 방위 + = 우측(도).</summary>
    public static float ItdSamples(float azDeg, float elDeg)
    {
        var s = MathF.Sin(azDeg * (MathF.PI / 180f)) * MathF.Cos(elDeg * (MathF.PI / 180f));
        var lat = MathF.Asin(Math.Clamp(s, -1f, 1f));
        return HeadRadius / SoundSpeed * (lat + MathF.Sin(lat)) * Fs;
    }

    /// <summary>HRIR 쌍 합성(정순 · 구축 시 1회). embedItd = 원측 귀에 우드워스 ITD 를 분수 지연으로 내장.</summary>
    public static void Synthesize(float azDeg, float elDeg, bool embedItd, Span<float> left, Span<float> right)
    {
        var az = azDeg * (MathF.PI / 180f);
        var el = elDeg * (MathF.PI / 180f);
        var dir = new Vector3(MathF.Sin(az) * MathF.Cos(el), MathF.Sin(el), MathF.Cos(az) * MathF.Cos(el));
        var itd = embedItd ? MathF.Abs(ItdSamples(azDeg, elDeg)) : 0f;
        var right_ = MathF.Sin(az) >= 0f;
        Ear(dir, azDeg, elDeg, +1, embedItd && !right_ ? itd : 0f, right);
        Ear(dir, azDeg, elDeg, -1, embedItd && right_ ? itd : 0f, left);
    }

    private static void Ear(Vector3 dir, float azDeg, float elDeg, int side, float extraDelay, Span<float> h)
    {
        h.Clear();
        // 귓바퀴 반사(Brown–Duda · 44.1 kHz 표 → 48 kHz 환산)
        var earAz = azDeg - side * 90f;
        while (earAz > 180f) earAz -= 360f;
        while (earAz < -180f) earAz += 360f;
        var theta = Math.Clamp(earAz, -90f, 90f) * (MathF.PI / 180f);
        var phi = (90f - elDeg) * (MathF.PI / 180f);
        Span<float> x = stackalloc float[Taps];
        Put(x, Base + extraDelay, 1f);
        for (var k = 0; k < PinnaRho.Length; k++)
        {
            var tau = (PinnaA[k] * MathF.Cos(theta * 0.5f) * MathF.Sin(PinnaD[k] * phi) + PinnaB[k]) * (48f / 44.1f);
            Put(x, Base + extraDelay + MathF.Max(0.5f, tau), PinnaRho[k]);
        }
        // 머리 그림자: H(s) = (1 + α s τ)/(1 + s τ), τ = a/2c → 쌍선형 변환
        var cosT = Math.Clamp(Vector3.Dot(dir, new Vector3(side, 0f, 0f)), -1f, 1f);
        var thetaDeg = MathF.Acos(cosT) * (180f / MathF.PI);
        const float aMin = 0.1f, thMin = 150f;
        var alpha = (1f + aMin * 0.5f) + (1f - aMin * 0.5f) * MathF.Cos(thetaDeg / thMin * MathF.PI);
        var kk = 2f * Fs * HeadRadius / (2f * SoundSpeed);
        float b0 = 1f + alpha * kk, b1 = 1f - alpha * kk, a0 = 1f + kk, a1 = 1f - kk;
        float xp = 0f, yp = 0f;
        for (var n = 0; n < Taps; n++)
        {
            var y = (b0 * x[n] + b1 * xp - a1 * yp) / a0;
            xp = x[n]; yp = y;
            h[n] = y;
        }
        // 꼬리 16탭 반 Hann 절단
        for (var n = Taps - 16; n < Taps; n++) h[n] *= 0.5f + 0.5f * MathF.Cos(MathF.PI * (n - (Taps - 16) + 1) / 17f);
    }

    private static void Put(Span<float> x, float pos, float v)
    {
        var i = (int)MathF.Floor(pos);
        var f = pos - i;
        if (i >= 0 && i < x.Length) x[i] += v * (1f - f);
        if (i + 1 >= 0 && i + 1 < x.Length) x[i + 1] += v * f;
    }

    /// <summary>방위/고도 쌍선형 보간 필터(시간 역순 · 무할당).</summary>
    public void Interpolate(float azDeg, float elDeg, Span<float> left, Span<float> right)
    {
        var a = azDeg / AzStepDeg;
        a -= MathF.Floor(a / AzCount) * AzCount;
        var a0 = Math.Min((int)a, AzCount - 1);
        var fa = a - a0;
        var a1 = (a0 + 1) % AzCount;
        var e = Math.Clamp((elDeg - ElMinDeg) / ElStepDeg, 0f, ElCount - 1);
        var e0 = Math.Min((int)e, ElCount - 1);
        var fe = e - e0;
        var e1 = Math.Min(e0 + 1, ElCount - 1);
        float w00 = (1f - fa) * (1f - fe), w10 = fa * (1f - fe), w01 = (1f - fa) * fe, w11 = fa * fe;
        int p00 = (e0 * AzCount + a0) * 2 * Taps, p10 = (e0 * AzCount + a1) * 2 * Taps, p01 = (e1 * AzCount + a0) * 2 * Taps, p11 = (e1 * AzCount + a1) * 2 * Taps;
        for (var k = 0; k < Taps; k++)
        {
            left[k] = w00 * _set[p00 + k] + w10 * _set[p10 + k] + w01 * _set[p01 + k] + w11 * _set[p11 + k];
            right[k] = w00 * _set[p00 + Taps + k] + w10 * _set[p10 + Taps + k] + w01 * _set[p01 + Taps + k] + w11 * _set[p11 + Taps + k];
        }
    }

    public ReadOnlySpan<float> BinLeft(int b) => _bins.AsSpan(b * 2 * Taps, Taps);
    public ReadOnlySpan<float> BinRight(int b) => _bins.AsSpan((b * 2 + 1) * Taps, Taps);

    /// <summary>필터 쌍 에너지 비 ILD(dB · + = 오른귀 큼).</summary>
    public static float IldDb(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        float el = 1e-12f, er = 1e-12f;
        for (var k = 0; k < left.Length; k++) { el += left[k] * left[k]; er += right[k] * right[k]; }
        return 10f * MathF.Log10(er / el);
    }
}

/// <summary>[PromptFlow Studio v17.23.0] 후기 잔향 4선 FDN(하다마드 · 저/고 2대역 감쇠 = 광선 추적 RT60 결선 · 무할당).</summary>
public sealed class FeedbackDelayReverb
{
    private static readonly int[] Lengths = { 1499, 1877, 2243, 2647 };
    private readonly float[][] _line = new float[4][];
    private readonly int[] _pos = new int[4];
    private readonly float[] _lp = new float[4], _gLo = new float[4], _gHi = new float[4];
    private readonly float _c;
    private float _norm = 1f;

    public FeedbackDelayReverb()
    {
        for (var i = 0; i < 4; i++) _line[i] = new float[Lengths[i]];
        _c = 1f - MathF.Exp(-2f * MathF.PI * 700f / Hrtf6DoFBinauralSpatialAudioSynthesizer.Fs);
        Configure(0.6f, 0.4f);
    }

    public float Rt60Low { get; private set; }
    public float Rt60High { get; private set; }

    /// <summary>선별 감쇠 이득 g = 10^(−3·L/(Fs·RT60)) · 꼬리 에너지 정규화.</summary>
    public void Configure(float rt60Low, float rt60High)
    {
        rt60Low = float.IsFinite(rt60Low) ? Math.Clamp(rt60Low, 0.05f, 8f) : 0.6f;
        rt60High = float.IsFinite(rt60High) ? Math.Clamp(rt60High, 0.05f, 8f) : 0.4f;
        Rt60Low = rt60Low; Rt60High = rt60High;
        var acc = 0f;
        for (var i = 0; i < 4; i++)
        {
            _gLo[i] = MathF.Pow(10f, -3f * Lengths[i] / (Hrtf6DoFBinauralSpatialAudioSynthesizer.Fs * rt60Low));
            _gHi[i] = MathF.Pow(10f, -3f * Lengths[i] / (Hrtf6DoFBinauralSpatialAudioSynthesizer.Fs * rt60High));
            var g = 0.5f * (_gLo[i] + _gHi[i]);
            acc += 1f / (1f - g * g);
        }
        _norm = 1f / MathF.Sqrt(0.5f * acc / 4f);
    }

    public void Clear()
    {
        for (var i = 0; i < 4; i++) { Array.Clear(_line[i]); _lp[i] = 0f; }
    }

    public void Process(ReadOnlySpan<float> in0, ReadOnlySpan<float> in1, float level, Span<float> outL, Span<float> outR)
    {
        var gain = Math.Clamp(level, 0f, 2f) * _norm;
        var n = outL.Length;
        for (var i = 0; i < n; i++)
        {
            var x = (in0[i] + in1[i]) * gain;
            float y0 = _line[0][_pos[0]], y1 = _line[1][_pos[1]], y2 = _line[2][_pos[2]], y3 = _line[3][_pos[3]];
            _lp[0] += _c * (y0 - _lp[0]); _lp[1] += _c * (y1 - _lp[1]); _lp[2] += _c * (y2 - _lp[2]); _lp[3] += _c * (y3 - _lp[3]);
            var d0 = _gHi[0] * (y0 - _lp[0]) + _gLo[0] * _lp[0];
            var d1 = _gHi[1] * (y1 - _lp[1]) + _gLo[1] * _lp[1];
            var d2 = _gHi[2] * (y2 - _lp[2]) + _gLo[2] * _lp[2];
            var d3 = _gHi[3] * (y3 - _lp[3]) + _gLo[3] * _lp[3];
            _line[0][_pos[0]] = 0.5f * (d0 + d1 + d2 + d3) + x;
            _line[1][_pos[1]] = 0.5f * (d0 - d1 + d2 - d3) - x;
            _line[2][_pos[2]] = 0.5f * (d0 + d1 - d2 - d3) + x;
            _line[3][_pos[3]] = 0.5f * (d0 - d1 - d2 + d3) - x;
            outL[i] += 0.5f * (y0 - y2);
            outR[i] += 0.5f * (y1 - y3);
            for (var k = 0; k < 4; k++) { var p = _pos[k] + 1; _pos[k] = p == Lengths[k] ? 0 : p; }
        }
    }
}

/// <summary>
/// [PromptFlow Studio v17.23.0] 🎧 6DoF HRTF 바이노럴 공간 음향 합성기(48 kHz · 블록 800 = 60 Hz 틱 · 무할당).
///   ▸ 입력 음원 2개(v17.18 음성 · 환경음)를 700 Hz 1극 크로스오버로 저/고 2대역 링(16384)에 기록 — 경로별 대역 이득(재질 흡음 · 차폐)을 읽기 시 적용.
///   ▸ 직접음 + 1차 반사(음원당 7경로): v17.22 HMD 헤드 포즈 역회전으로 머리 기준 방위/고도 → 귀별 지연(경로 지연 ± ITD/2 · 블록 내 선형 램프)
///     + ITD 분리 HRIR 쌍선형 보간 64탭 SIMD 컨볼루션(필터 변경 블록은 이전/새 필터 출력 교차 페이드).
///   ▸ 2차 반사(음원당 30경로): 머리 기준 방위로 수평 가상 스피커 8개에 정전력 패닝(블록 램프) → ITD 내장 HRIR 컨볼루션.
///   ▸ 후기 잔향: 광선 추적 RT60(저/고) · 후기 레벨로 구성한 FDN.
///   ▸ 경로 지연 · 이득 램프 · 위상 점프 방지는 <see cref="DopplerDiffractionOcclusionPhaseGuard"/> 가 담당.
/// </summary>
public sealed class Hrtf6DoFBinauralSpatialAudioSynthesizer
{
    public const int Fs = 48000, Block = 800, Ring = 16384, Mask = Ring - 1, Sources = 2, Slots = RealtimeAcousticRayTracingEngine.PathSlots;
    public const int HrtfSlots = 1 + RealtimeAcousticRayTracingEngine.Planes, Taps = AcousticKernels.Taps, Hist = Taps - 1, Bins = HrtfFilterSet.VirtualBins, ScopeLen = 512;
    public const float CrossoverHz = 700f, MasterGain = 0.7f, SoundSpeed = 343f;

    private readonly float[][] _lo = new float[Sources][], _hi = new float[Sources][];
    private readonly float[] _lp = new float[Sources];
    private readonly float[][] _earIn = new float[Sources * HrtfSlots * 2][];
    private readonly float[][] _filt = new float[Sources * HrtfSlots * 2][];
    private readonly float[][] _filtPrev = new float[Sources * HrtfSlots * 2][];
    private readonly float[] _keyAz = new float[Sources * HrtfSlots], _keyEl = new float[Sources * HrtfSlots], _ild = new float[Sources * HrtfSlots];
    private readonly bool[] _keyHrtf = new bool[Sources * HrtfSlots], _hasFilter = new bool[Sources * HrtfSlots];
    private readonly float[] _itdPrev = new float[Sources * HrtfSlots];
    private readonly float[][] _binIn = new float[Bins][];
    private readonly float[] _pan = new float[Sources * Slots * Bins], _panNew = new float[Bins];
    private readonly float[] _az = new float[Sources * Slots], _el = new float[Sources * Slots];
    private readonly float[] _tmp = new float[Block];
    private readonly float[] _outL = new float[Block], _outR = new float[Block];
    private readonly float[] _scopeL = new float[ScopeLen], _scopeR = new float[ScopeLen];
    private readonly float _c;
    private int _scopeHead;
    private long _clock;

    public Hrtf6DoFBinauralSpatialAudioSynthesizer()
    {
        for (var s = 0; s < Sources; s++) { _lo[s] = new float[Ring]; _hi[s] = new float[Ring]; }
        for (var i = 0; i < _earIn.Length; i++) { _earIn[i] = new float[Hist + Block]; _filt[i] = new float[Taps]; _filtPrev[i] = new float[Taps]; }
        for (var b = 0; b < Bins; b++) _binIn[b] = new float[Hist + Block];
        _c = 1f - MathF.Exp(-2f * MathF.PI * CrossoverHz / Fs);
        Array.Fill(_itdPrev, float.NaN);
    }

    public DopplerDiffractionOcclusionPhaseGuard Guard { get; } = new(Sources * Slots);
    public FeedbackDelayReverb Late { get; } = new();
    public HrtfFilterSet Hrtf => HrtfFilterSet.Shared;
    public bool HrtfEnabled { get; set; } = true;
    public bool LateEnabled { get; set; } = true;
    public bool UseSimd { get; set; } = Vector128.IsHardwareAccelerated;
    public float AzimuthDeg { get; private set; }
    public float ElevationDeg { get; private set; }
    public float ItdUs { get; private set; }
    public float IldDb { get; private set; }
    public float PeakL { get; private set; }
    public float PeakR { get; private set; }
    public float RmsL { get; private set; }
    public float RmsR { get; private set; }
    public long Clips { get; private set; }
    public long Blocks { get; private set; }
    public int AudiblePaths { get; private set; }
    public double LastRenderUs { get; private set; }
    public double MaxRenderUs { get; private set; }
    public ReadOnlySpan<float> OutputLeft => _outL;
    public ReadOnlySpan<float> OutputRight => _outR;
    public float PathAzimuth(int slot) => _az[slot];
    public float PathElevation(int slot) => _el[slot];
    public float ScopeL(int i) => _scopeL[(_scopeHead + i) % ScopeLen];
    public float ScopeR(int i) => _scopeR[(_scopeHead + i) % ScopeLen];

    /// <summary>
    /// 1블록 렌더(무할당). paths = 음원별 37 슬롯 × 2(<see cref="RealtimeAcousticRayTracingEngine.ComputePaths"/> + 차폐 적용).
    /// headQ 는 v17.22 HMD 헤드 회전(Yaw/Pitch/Roll), 로컬 +Z 정면 · +X 우측.
    /// </summary>
    public void Render(ReadOnlySpan<float> mono0, ReadOnlySpan<float> mono1, Vector3 headPos, Quaternion headQ, ReadOnlySpan<AcousticPath> paths, float rt60Low, float rt60High, float lateLevel)
    {
        if (mono0.Length < Block || mono1.Length < Block || paths.Length < Sources * Slots) throw new ArgumentException("블록/경로 길이 부족");
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        // 1) 입력 → 저/고 분리 링
        for (var s = 0; s < Sources; s++)
        {
            var m = s == 0 ? mono0 : mono1;
            var lo = _lo[s]; var hi = _hi[s]; var lp = _lp[s];
            for (var i = 0; i < Block; i++)
            {
                var x = m[i];
                lp += _c * (x - lp);
                var w = (int)((_clock + i) & Mask);
                lo[w] = lp; hi[w] = x - lp;
            }
            _lp[s] = lp;
        }
        Array.Clear(_outL); Array.Clear(_outR);
        for (var i = 0; i < _earIn.Length; i++) _earIn[i].AsSpan(Block, Hist).CopyTo(_earIn[i]);
        for (var b = 0; b < Bins; b++) { _binIn[b].AsSpan(Block, Hist).CopyTo(_binIn[b]); _binIn[b].AsSpan(Hist, Block).Clear(); }

        var inv = Quaternion.Conjugate(headQ);
        var audible = 0;
        Span<float> nl = stackalloc float[Taps];
        Span<float> nr = stackalloc float[Taps];
        for (var s = 0; s < Sources; s++)
        {
            var lo = _lo[s]; var hi = _hi[s];
            for (var j = 0; j < Slots; j++)
            {
                var slot = s * Slots + j;
                var p = paths[slot];
                Guard.Begin(slot, p.Valid, p.LengthM / SoundSpeed * Fs, p.GainLow, p.GainHigh, p.Diffracted, Block);
                if (!Guard.IsAudible(slot))
                {
                    if (j < HrtfSlots)
                    {
                        // 무음 슬롯: 컨볼루션 이력 비움(재활성 시 묵은 꼬리 0) · ITD 램프 재시작
                        var cq = s * HrtfSlots + j;
                        _earIn[cq * 2].AsSpan().Clear(); _earIn[cq * 2 + 1].AsSpan().Clear();
                        _itdPrev[cq] = float.NaN;
                    }
                    else _pan.AsSpan(slot * Bins, Bins).Clear();
                    Guard.End(slot, Block);
                    continue;
                }
                audible++;
                var local = Vector3.Transform(p.Via - headPos, inv);
                var ll = local.Length();
                var dir = ll > 1e-5f ? local / ll : Vector3.UnitZ;
                var az = MathF.Atan2(dir.X, dir.Z) * (180f / MathF.PI);
                var el = MathF.Asin(Math.Clamp(dir.Y, -1f, 1f)) * (180f / MathF.PI);
                _az[slot] = az; _el[slot] = el;
                if (j < HrtfSlots)
                {
                    var ch = s * HrtfSlots + j;
                    var itd = HrtfEnabled ? HrtfFilterSet.ItdSamples(az, el) : 0f;
                    var ip = float.IsNaN(_itdPrev[ch]) ? itd : _itdPrev[ch];
                    _itdPrev[ch] = itd;
                    var inL = _earIn[ch * 2]; var inR = _earIn[ch * 2 + 1];
                    Guard.Read(slot, lo, hi, _clock, 0.5f * ip, 0.5f * itd, inL.AsSpan(Hist, Block));
                    Guard.Read(slot, lo, hi, _clock, -0.5f * ip, -0.5f * itd, inR.AsSpan(Hist, Block));
                    var xfade = UpdateFilter(ch, az, el, nl, nr);
                    Convolve(inL, _filt[ch * 2], _filtPrev[ch * 2], xfade, _outL);
                    Convolve(inR, _filt[ch * 2 + 1], _filtPrev[ch * 2 + 1], xfade, _outR);
                    if (slot == 0)
                    {
                        AzimuthDeg = az; ElevationDeg = el;
                        ItdUs = itd / Fs * 1e6f;
                        IldDb = _ild[ch];
                    }
                }
                else
                {
                    Guard.Read(slot, lo, hi, _clock, 0f, 0f, _tmp);
                    PanBins(slot, az);
                }
                Guard.End(slot, Block);
            }
        }
        for (var b = 0; b < Bins; b++)
        {
            ConvolveFixed(_binIn[b], Hrtf.BinLeft(b), _outL);
            ConvolveFixed(_binIn[b], Hrtf.BinRight(b), _outR);
        }
        if (LateEnabled)
        {
            Late.Configure(rt60Low, rt60High);
            Late.Process(mono0, mono1, lateLevel, _outL, _outR);
        }
        // 마스터 · 클립 · 계측
        float pl = 0f, pr = 0f, el2 = 0f, er2 = 0f;
        for (var i = 0; i < Block; i++)
        {
            var l = _outL[i] * MasterGain; var r = _outR[i] * MasterGain;
            if (l > 1f || l < -1f || r > 1f || r < -1f) Clips++;
            l = Math.Clamp(l, -1f, 1f); r = Math.Clamp(r, -1f, 1f);
            _outL[i] = l; _outR[i] = r;
            var al = MathF.Abs(l); var ar = MathF.Abs(r);
            if (al > pl) pl = al;
            if (ar > pr) pr = ar;
            el2 += l * l; er2 += r * r;
        }
        PeakL = pl; PeakR = pr; RmsL = MathF.Sqrt(el2 / Block); RmsR = MathF.Sqrt(er2 / Block);
        const int step = Block / 64;
        for (var i = 0; i < Block; i += step)
        {
            _scopeL[_scopeHead] = _outL[i]; _scopeR[_scopeHead] = _outR[i];
            _scopeHead = (_scopeHead + 1) % ScopeLen;
        }
        AudiblePaths = audible;
        _clock += Block;
        Blocks++;
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        LastRenderUs = us;
        if (us > MaxRenderUs) MaxRenderUs = us;
    }

    /// <summary>머리 기준 방향 HRIR 갱신 — 0.05° 이상 변하면 이전 필터 보존 후 재보간(교차 페이드 대상).</summary>
    private bool UpdateFilter(int ch, float az, float el, Span<float> nl, Span<float> nr)
    {
        var mode = HrtfEnabled;
        if (_hasFilter[ch] && _keyHrtf[ch] == mode && MathF.Abs(DeltaAz(az, _keyAz[ch])) < 0.05f && MathF.Abs(el - _keyEl[ch]) < 0.05f) return false;
        var fl = _filt[ch * 2]; var fr = _filt[ch * 2 + 1];
        if (mode) Hrtf.Interpolate(az, el, nl, nr);
        else
        {
            // 비교용 정전력 패닝(ITD · 스펙트럼 단서 없음)
            nl.Clear(); nr.Clear();
            var sn = MathF.Sin(az * (MathF.PI / 180f));
            nl[Taps - 1 - HrtfFilterSet.Base] = MathF.Sqrt(0.5f * (1f - sn));
            nr[Taps - 1 - HrtfFilterSet.Base] = MathF.Sqrt(0.5f * (1f + sn));
        }
        var had = _hasFilter[ch];
        if (had) { fl.AsSpan().CopyTo(_filtPrev[ch * 2]); fr.AsSpan().CopyTo(_filtPrev[ch * 2 + 1]); }
        nl.CopyTo(fl); nr.CopyTo(fr);
        _keyAz[ch] = az; _keyEl[ch] = el; _keyHrtf[ch] = mode; _hasFilter[ch] = true;
        _ild[ch] = HrtfFilterSet.IldDb(fl, fr);
        return had;
    }

    private static float DeltaAz(float a, float b)
    {
        var d = a - b;
        while (d > 180f) d -= 360f;
        while (d < -180f) d += 360f;
        return d;
    }

    /// <summary>2차 반사 → 머리 기준 방위 정전력 패닝(인접 가상 스피커 2개) · 블록 선형 램프 누적.</summary>
    private void PanBins(int slot, float az)
    {
        var f = az / 45f;
        f -= MathF.Floor(f / Bins) * Bins;
        var b0 = Math.Min((int)f, Bins - 1);
        var fr = f - b0;
        var b1 = (b0 + 1) % Bins;
        Array.Clear(_panNew);
        _panNew[b0] = MathF.Cos(fr * MathF.PI * 0.5f);
        _panNew[b1] += MathF.Sin(fr * MathF.PI * 0.5f);
        var o = slot * Bins;
        var invN = 1f / Block;
        for (var b = 0; b < Bins; b++)
        {
            var g0 = _pan[o + b]; var g1 = _panNew[b];
            if (g0 == 0f && g1 == 0f) continue;
            var dst = _binIn[b];
            var dg = (g1 - g0) * invN;
            for (var i = 0; i < Block; i++) dst[Hist + i] += _tmp[i] * (g0 + dg * (i + 1));
            _pan[o + b] = g1;
        }
    }

    private float Dot(ReadOnlySpan<float> x, ReadOnlySpan<float> h) => UseSimd ? AcousticKernels.Dot64Simd(x, h) : AcousticKernels.Dot64Scalar(x, h);

    private void Convolve(float[] input, float[] cur, float[] prev, bool xfade, float[] output)
    {
        var x = input.AsSpan();
        if (!xfade)
        {
            for (var i = 0; i < Block; i++) output[i] += Dot(x.Slice(i, Taps), cur);
            return;
        }
        const float invN = 1f / Block;
        for (var i = 0; i < Block; i++)
        {
            var w = (i + 0.5f) * invN;
            var xs = x.Slice(i, Taps);
            output[i] += Dot(xs, cur) * w + Dot(xs, prev) * (1f - w);
        }
    }

    private void ConvolveFixed(float[] input, ReadOnlySpan<float> h, float[] output)
    {
        var x = input.AsSpan();
        for (var i = 0; i < Block; i++) output[i] += Dot(x.Slice(i, Taps), h);
    }
}
