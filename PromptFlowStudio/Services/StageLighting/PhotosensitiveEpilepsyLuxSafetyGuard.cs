namespace PromptFlowStudio.Services.StageLighting;

/// <summary>
/// [PromptFlow Studio v17.41.0] 🛡 광민성 발작(PSE) 방지 &amp; 조도 클리핑 광학 세이프티 가드(<see cref="Process"/> 무할당 · 60 Hz).
///   ▸ ITU-R BT.1702 플래시 정의: 휘도 극값 대비 ≥ 20 cd/m² 반대 방향 전이 쌍(어두운 쪽 &lt; 160 cd/m²) = 1 플래시 · 1 s 슬라이딩 창 3 플래시(전이 6) 초과 = 위반.
///     60 fps 샘플링 → 3~30 Hz(나이퀴스트) 대역 플리커 전부 관측. 포화 적색(R/(R+G+B) ≥ 0.8) 전이는 별도 검출기(160 조건 없음 · 더 엄격).
///   ▸ 억제: 창이 가득(전이 6)이면 다음 전이를 만들 휘도 변화를 극값 ±19.5 cd/m² 대역으로 투영 —
///     휘도가 디머에 선형이므로 out = prev + α(raw − prev)의 α 해석해(픽스처 간 비율 보존 · 같은 방향 진행은 허용).
///   ▸ 스트로브 요청 2.8 Hz 상한 클램프 · 피크 조도 소프트 니(1800 lux → 2500 lux 점근) · 어택 즉시/릴리스 τ 0.4 s(센서 화이트아웃 · 펌핑 방지).
///     최종 하드 상한 2500 lux — 단 PSE 억제 구간에선 휘도 대역 하한이 우선(발작 방지 &gt; 화이트아웃).
/// </summary>
public sealed class PhotosensitiveEpilepsyLuxSafetyGuard
{
    public const int Hz = 60, MaxTransitions = 6, Ring = 32, Series = 300;
    public const float FlashDelta = 20f, DarkLimit = 160f, Margin = 0.5f, MaxSafeStrobeHz = 2.8f, KneeLux = 1800f, MaxLux = 2500f, ReleaseS = 0.4f;

    /// <summary>전이 검출기(극값 추적 · 1 s 창 링).</summary>
    private sealed class Detector
    {
        public readonly long[] Ring = new long[PhotosensitiveEpilepsyLuxSafetyGuard.Ring];
        public readonly bool Dark;
        public int Dir, Head;
        public float Ext;
        public bool Init;
        public long Total;

        public Detector(bool dark) { Dark = dark; Array.Fill(Ring, long.MinValue / 2); }

        public void Reset() { Array.Fill(Ring, long.MinValue / 2); Dir = 0; Head = 0; Init = false; Ext = 0f; }

        public int Count(long frame)
        {
            var c = 0;
            for (var i = 0; i < Ring.Length; i++) if (Ring[i] > frame - Hz) c++;
            return c;
        }

        /// <summary>전이 발생 시 true.</summary>
        public bool Update(float l, long frame)
        {
            if (!Init) { Init = true; Ext = l; Dir = 0; return false; }
            var hit = false;
            if (Dir == 0)
            {
                if (l - Ext >= FlashDelta && (!Dark || Ext < DarkLimit)) { Dir = 1; hit = true; }
                else if (Ext - l >= FlashDelta && (!Dark || l < DarkLimit)) { Dir = -1; hit = true; }
                if (hit) Ext = l;
            }
            else if (Dir > 0)
            {
                if (l >= Ext) Ext = l;
                else if (Ext - l >= FlashDelta && (!Dark || l < DarkLimit)) { Dir = -1; Ext = l; hit = true; }
            }
            else
            {
                if (l <= Ext) Ext = l;
                else if (l - Ext >= FlashDelta && (!Dark || Ext < DarkLimit)) { Dir = 1; Ext = l; hit = true; }
            }
            if (hit) { Ring[Head] = frame; Head = (Head + 1) % Ring.Length; Total++; }
            return hit;
        }

        /// <summary>추가 전이를 만들지 않는 휘도 허용 구간.</summary>
        public void Bounds(out float lo, out float hi)
        {
            lo = float.NegativeInfinity; hi = float.PositiveInfinity;
            if (!Init) return;
            const float d = FlashDelta - Margin;
            if (Dir >= 0) lo = Dark ? MathF.Min(Ext - d, DarkLimit) : Ext - d;
            if (Dir <= 0 && (!Dark || Ext < DarkLimit)) hi = Ext + d;
        }
    }

    private readonly Detector _lum = new(true), _redDet = new(false);
    private readonly float[] _prev;
    private readonly float[] _frS = new float[Series], _luxS = new float[Series], _lS = new float[Series];
    private float _gain = 1f;
    private long _frame;
    private int _sHead, _sCount;

    public PhotosensitiveEpilepsyLuxSafetyGuard() : this(RealtimeIntelligentStageLightingEngine.Fixtures) { }

    public PhotosensitiveEpilepsyLuxSafetyGuard(int channels) => _prev = new float[channels];

    public bool Enabled { get; set; } = true;
    public long Frames => _frame;
    public float Luminance { get; private set; }
    public float RedLuminance { get; private set; }
    public float Lux { get; private set; }
    public float RawLux { get; private set; }
    public float PeakLux { get; private set; }
    public float PeakRawLux { get; private set; }
    public float LuxGain => _gain;
    public float FlashRate { get; private set; }
    public float RedFlashRate { get; private set; }
    public float MaxFlashRate { get; private set; }
    public float MaxRedFlashRate { get; private set; }
    public float LastAlpha { get; private set; } = 1f;
    public long Transitions => _lum.Total;
    public long RedTransitions => _redDet.Total;
    /// <summary>1 s 창 3 플래시 초과(BT.1702 일반 플래시 실패) 발생 프레임 수.</summary>
    public long Violations { get; private set; }
    public long RedViolations { get; private set; }
    public long SuppressedFrames { get; private set; }
    public long Projections { get; private set; }
    public long StrobeClamps { get; private set; }
    public long LuxLimitedFrames { get; private set; }
    public double LastProcessUs { get; private set; }

    public int SeriesCount => _sCount;
    public float FlashRateSeries(int i) => _frS[Idx(i)];
    public float LuxSeries(int i) => _luxS[Idx(i)];
    public float LuminanceSeries(int i) => _lS[Idx(i)];
    private int Idx(int i) => (_sHead - 1 - i + 2 * Series) % Series;

    /// <summary>스트로브 요청 주파수 → 안전 상한(가드 OFF 시 그대로).</summary>
    public float ClampStrobeHz(float hz)
    {
        if (!Enabled || hz <= MaxSafeStrobeHz) return hz;
        StrobeClamps++;
        return MaxSafeStrobeHz;
    }

    /// <summary>
    /// raw(디머 0..1) · 단위 디머당 화면 휘도/포화 적색 휘도(cd/m²) · 배우 0/1 조도(lux) → output(가드 승인 디머).
    /// </summary>
    public void Process(ReadOnlySpan<float> raw, ReadOnlySpan<float> lum, ReadOnlySpan<float> red, ReadOnlySpan<float> lux0, ReadOnlySpan<float> lux1, Span<float> output, float dt)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var n = Math.Min(raw.Length, _prev.Length);
        _frame++;

        // 1) 피크 조도 소프트 니(어택 즉시 · 릴리스 τ)
        var rl = MathF.Max(Dot(raw, lux0, n, 1f), Dot(raw, lux1, n, 1f));
        RawLux = rl; PeakRawLux = MathF.Max(PeakRawLux, rl);
        var s = 1f;
        if (Enabled && rl > KneeLux)
        {
            var over = rl - KneeLux;
            s = (KneeLux + over / (1f + over / (MaxLux - KneeLux))) / rl;
        }
        if (!Enabled) _gain = 1f;
        else if (s < _gain) _gain = s;
        else _gain += (s - _gain) * (1f - MathF.Exp(-dt / ReleaseS));
        if (_gain < 0.999f) LuxLimitedFrames++;

        // 2) 플래시 대역 투영(α)
        var a = 1f;
        if (Enabled)
        {
            if (_lum.Count(_frame) >= MaxTransitions) a = MathF.Min(a, Alpha(_lum, Dot(_prev, lum, n, 1f), Dot(raw, lum, n, _gain)));
            if (_redDet.Count(_frame) >= MaxTransitions) a = MathF.Min(a, Alpha(_redDet, Dot(_prev, red, n, 1f), Dot(raw, red, n, _gain)));
        }
        LastAlpha = a;
        if (a < 0.9999f) SuppressedFrames++;
        for (var i = 0; i < n; i++)
        {
            var v = _prev[i] + a * (raw[i] * _gain - _prev[i]);
            v = float.IsFinite(v) ? Math.Clamp(v, 0f, 1f) : 0f;
            output[i] = v; _prev[i] = v;
        }
        if (Enabled)
        {
            // 가중치(빔 방향 · 샤프트) 프레임 간 변화로 prev 가 대역 밖이면 α 로 닿지 못함 → 전체 비례 스케일로 대역 투영
            if (_lum.Count(_frame) >= MaxTransitions) Project(_lum, lum, output, n);
            if (_redDet.Count(_frame) >= MaxTransitions) Project(_redDet, red, output, n);
            // 최종 조도 상한(가중치 변화로 prev 혼합이 넘친 경우) — PSE 대역 하한 아래로는 내리지 않음(발작 방지 우선)
            var lxo = MathF.Max(Dot(output, lux0, n, 1f), Dot(output, lux1, n, 1f));
            if (lxo > MaxLux)
            {
                var k = MaxLux / lxo;
                if (_lum.Count(_frame) >= MaxTransitions)
                {
                    _lum.Bounds(out var lo, out _);
                    var l0 = Dot(output, lum, n, 1f);
                    if (float.IsFinite(lo) && l0 > 1e-6f) k = MathF.Max(k, MathF.Min(1f, lo / l0));
                }
                for (var i = 0; i < n; i++) { output[i] *= k; _prev[i] = output[i]; }
                LuxLimitedFrames++;
            }
        }

        // 3) 결과 휘도 분석(BT.1702)
        var l = Luminance = Dot(output, lum, n, 1f);
        var r = RedLuminance = Dot(output, red, n, 1f);
        var lx = Lux = MathF.Max(Dot(output, lux0, n, 1f), Dot(output, lux1, n, 1f));
        PeakLux = MathF.Max(PeakLux, lx);
        var hitL = _lum.Update(l, _frame);
        var hitR = _redDet.Update(r, _frame);
        var cl = _lum.Count(_frame); var cr = _redDet.Count(_frame);
        if (hitL && cl > MaxTransitions) Violations++;
        if (hitR && cr > MaxTransitions) RedViolations++;
        FlashRate = 0.5f * cl; RedFlashRate = 0.5f * cr;
        MaxFlashRate = MathF.Max(MaxFlashRate, FlashRate);
        MaxRedFlashRate = MathF.Max(MaxRedFlashRate, RedFlashRate);
        _frS[_sHead] = FlashRate; _luxS[_sHead] = lx; _lS[_sHead] = l;
        _sHead = (_sHead + 1) % Series;
        if (_sCount < Series) _sCount++;
        LastProcessUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    private void Project(Detector d, ReadOnlySpan<float> w, Span<float> output, int n)
    {
        var l = Dot(output, w, n, 1f);
        d.Bounds(out var lo, out var hi);
        var target = Math.Clamp(l, lo, hi);
        if (target == l || l < 1e-6f) return;
        var k = target / l;
        for (var i = 0; i < n; i++) { var v = Math.Clamp(output[i] * k, 0f, 1f); output[i] = v; _prev[i] = v; }
        Projections++;
    }

    private static float Alpha(Detector d, float prev, float next)
    {
        d.Bounds(out var lo, out var hi);
        var target = Math.Clamp(next, lo, hi);
        if (target == next) return 1f;
        var delta = next - prev;
        if (MathF.Abs(delta) < 1e-6f) return 1f;
        return Math.Clamp((target - prev) / delta, 0f, 1f);
    }

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> w, int n, float k)
    {
        var s = 0f;
        for (var i = 0; i < n; i++) s += a[i] * k * w[i];
        return s;
    }

    public void ResetMetrics()
    {
        Violations = RedViolations = SuppressedFrames = StrobeClamps = LuxLimitedFrames = 0;
        MaxFlashRate = MaxRedFlashRate = 0f; PeakLux = PeakRawLux = 0f;
    }

    /// <summary>검출기 · 이력 초기화(장면 전환 · 가드 토글 시).</summary>
    public void Reset()
    {
        _lum.Reset(); _redDet.Reset();
        Array.Clear(_prev);
        _gain = 1f;
        ResetMetrics();
    }
}
