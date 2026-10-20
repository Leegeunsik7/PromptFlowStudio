using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.SpatialAudio;

public enum HrtfProfile : byte { Small = 0, Medium, Large, Custom }

/// <summary>
/// 개인화 HRIR 결선점(신경망 · SOFA 측정 등). 방위/고도(머리 기준 도) → 좌/우 HRIR(길이 = <see cref="NeuralBinauralHrtfSpatializer.HrirTaps"/>). 무할당 구현 필요.
/// </summary>
public interface INeuralHrtfModel
{
    bool TryGetHrir(float azimuthDeg, float elevationDeg, Span<float> left, Span<float> right);
}

/// <summary>
/// [PromptFlow Studio v16.76.0] 🎧 신경망 바이노럴 HRTF &amp; 공간화 통합 필터.
///   ▸ 파라메트릭 HRTF(Brown–Duda 구형 머리 모델): 귀별 Woodworth 지연(분수 지연선 · 선형 보간) + 머리 그림자 1극/1영 필터(α(θ) 귀축 각 · 분모 고정 → α 샘플 단위 보간 = 잡음 없는 전환) +
///     귀바퀴 고도 노치(6~10 kHz · 프로필 스케일) + 어깨 반사(저고도 지연 반사).
///   ▸ 머리 추적: 머리 회전(쿼터니언) 즉시 반영 — 블록 내 지연/그림자 파라미터 선형 보간(클릭 없음) · 블록 지연 0.
///   ▸ 초기 반사: 레이트레이서 탭(지연·도래 방향·대역 이득)을 음원 입력 이력에서 읽어 귀별 ITD·카디오이드 ILD 로 공간화.
///   ▸ Atmos 가상화: 7.1.4 베드 12채널을 고정 방향 가상 스피커로 바이노럴 렌더.
///   ▸ 결선점: <see cref="INeuralHrtfModel"/>(개인화 HRIR · FIR 컨볼루션) — 미결선 시 파라메트릭 모델.
/// </summary>
public sealed class NeuralBinauralHrtfSpatializer
{
    public const int MaxVoices = 48, HistoryLen = 8192, HrirTaps = 64, MaxTaps = 16;
    private const int HistMask = HistoryLen - 1;

    private readonly object _gate = new();
    private readonly int _fs;
    private readonly float[] _hist = new float[MaxVoices * HistoryLen];
    private readonly int[] _pos = new int[MaxVoices];
    private readonly float[] _dL = new float[MaxVoices], _dR = new float[MaxVoices], _aL = new float[MaxVoices], _aR = new float[MaxVoices];
    private readonly float[] _shL = new float[MaxVoices * 2], _shR = new float[MaxVoices * 2];   // 1극/1영 상태(x1, y1)
    private readonly float[] _nL = new float[MaxVoices * 4], _nR = new float[MaxVoices * 4];     // 노치 바이쿼드 상태
    private readonly float[] _nf = new float[MaxVoices], _sh = new float[MaxVoices];
    private readonly bool[] _init = new bool[MaxVoices];
    private readonly float[] _hrL = new float[HrirTaps], _hrR = new float[HrirTaps];
    private Quaternion _head = Quaternion.Identity, _headInv = Quaternion.Identity;
    private float _radius = 0.0875f, _pinna = 1f;
    private HrtfProfile _profile = HrtfProfile.Medium;
    private long _samples;

    public NeuralBinauralHrtfSpatializer(int sampleRate = 48000) => _fs = sampleRate;

    public INeuralHrtfModel? Model { get; set; }
    public int SampleRate => _fs;
    public HrtfProfile Profile => _profile;
    public float HeadRadius => _radius;
    public float PinnaScale => _pinna;
    public Quaternion Head => _head;
    public long SamplesProcessed => Interlocked.Read(ref _samples);
    public bool ShoulderReflection { get; set; } = true;

    /// <summary>7.1.4 채널 방향(머리 기준 방위 +오른쪽 · 고도). LFE = NaN.</summary>
    public static readonly (float Az, float El)[] BedLayout =
    {
        (-30, 0), (30, 0), (0, 0), (float.NaN, 0), (-90, 0), (90, 0), (-135, 0), (135, 0), (-45, 45), (45, 45), (-135, 45), (135, 45),
    };

    public static readonly string[] BedNames = { "L", "R", "C", "LFE", "Ls", "Rs", "Lb", "Rb", "Ltf", "Rtf", "Ltb", "Rtb" };

    public void SetProfile(HrtfProfile p, float customHeadRadius = 0.0875f, float customPinna = 1f)
    {
        lock (_gate)
        {
            _profile = p;
            (_radius, _pinna) = p switch
            {
                HrtfProfile.Small => (0.080f, 1.12f),
                HrtfProfile.Large => (0.095f, 0.9f),
                HrtfProfile.Custom => (Math.Clamp(customHeadRadius, 0.06f, 0.12f), Math.Clamp(customPinna, 0.7f, 1.4f)),
                _ => (0.0875f, 1f),
            };
        }
    }

    /// <summary>머리 회전(월드 → 머리). 머리 기준: 오른쪽 +X · 위 +Y · 정면 −Z.</summary>
    public void SetHeadOrientation(Quaternion q)
    {
        lock (_gate) { _head = Quaternion.Normalize(q); _headInv = Quaternion.Inverse(_head); }
    }

    public void SetHeadYawPitch(float yawDeg, float pitchDeg) =>
        SetHeadOrientation(Quaternion.CreateFromYawPitchRoll(yawDeg * MathF.PI / 180f, pitchDeg * MathF.PI / 180f, 0));

    public void ResetVoice(int v) { lock (_gate) { _init[v] = false; _hist.AsSpan(v * HistoryLen, HistoryLen).Clear(); } }

    /// <summary>월드 방향 → 머리 기준 방위(+오른쪽)/고도(도).</summary>
    public (float Az, float El) ToHead(Vector3 worldDir)
    {
        var d = Vector3.Normalize(Vector3.Transform(worldDir, _headInv));
        return (MathF.Atan2(d.X, -d.Z) * 180f / MathF.PI, MathF.Asin(Math.Clamp(d.Y, -1f, 1f)) * 180f / MathF.PI);
    }

    private static Vector3 FromAzEl(float az, float el)
    {
        var a = az * MathF.PI / 180f; var e = el * MathF.PI / 180f;
        return new Vector3(MathF.Sin(a) * MathF.Cos(e), MathF.Sin(e), -MathF.Cos(a) * MathF.Cos(e));
    }

    /// <summary>Woodworth 귀별 지연(s): 귀축과의 각 θ — θ &lt; 90° 면 −a/c·cosθ, 아니면 a/c·(θ − 90°). 양 귀 공통 오프셋 a/c 로 양수화.</summary>
    private float EarDelay(float theta) => (theta < MathF.PI / 2 ? -_radius / AcousticBands.SpeedOfSound * MathF.Cos(theta) : _radius / AcousticBands.SpeedOfSound * (theta - MathF.PI / 2)) + _radius / AcousticBands.SpeedOfSound;

    /// <summary>Brown–Duda α(θ): θ = 0 → 2(정면 귀 · 고역 +6 dB) · θ = 150° → 0.1(그림자).</summary>
    private static float ShadowAlpha(float theta)
    {
        const float aMin = 0.1f, thMin = 150f * MathF.PI / 180f;
        return 1 + aMin / 2 + (1 - aMin / 2) * MathF.Cos(theta / thMin * MathF.PI);
    }

    private float NotchFreq(float elDeg) => Math.Clamp(6500f + 3500f * (elDeg / 90f), 4000f, 11000f) * _pinna;

    /// <summary>
    /// 음원 1개(보이스 v) 공간화 → outL/outR 누적. dir = 청취자 → 음원 월드 방향 · taps = 초기 반사(지연은 직접음 대비 상대 = tap.Delay − directDelay). 무할당.
    /// </summary>
    public void ProcessVoice(int v, ReadOnlySpan<float> mono, Vector3 dir, float gain, Span<float> outL, Span<float> outR,
        ReadOnlySpan<EarlyReflection> taps = default, float directDelay = 0, float reflectionGain = 1f)
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Hrtf.Voice");
            var n = mono.Length;
            var hb = v * HistoryLen;
            var p0 = _pos[v];
            for (var i = 0; i < n; i++) _hist[hb + ((p0 + i) & HistMask)] = mono[i];
            _pos[v] = (p0 + n) & HistMask;

            var hd = Vector3.Normalize(Vector3.Transform(dir, _headInv));
            var thR = MathF.Acos(Math.Clamp(hd.X, -1f, 1f)); var thL = MathF.Acos(Math.Clamp(-hd.X, -1f, 1f));
            var el = MathF.Asin(Math.Clamp(hd.Y, -1f, 1f)) * 180f / MathF.PI;
            var tL = EarDelay(thL) * _fs; var tR = EarDelay(thR) * _fs;
            var aL = ShadowAlpha(thL); var aR = ShadowAlpha(thR);
            var nf = NotchFreq(el);
            if (!_init[v]) { _dL[v] = tL; _dR[v] = tR; _aL[v] = aL; _aR[v] = aR; _nf[v] = nf; _init[v] = true; Array.Clear(_shL, v * 2, 2); Array.Clear(_shR, v * 2, 2); Array.Clear(_nL, v * 4, 4); Array.Clear(_nR, v * 4, 4); }

            if (Model is { } model && model.TryGetHrir(MathF.Atan2(hd.X, -hd.Z) * 180f / MathF.PI, el, _hrL, _hrR))
            {
                for (var i = 0; i < n; i++)
                {
                    float l = 0, r = 0;
                    var idx = p0 + i;
                    for (var k = 0; k < HrirTaps; k++) { var x = _hist[hb + ((idx - k) & HistMask)]; l += x * _hrL[k]; r += x * _hrR[k]; }
                    outL[i] += l * gain; outR[i] += r * gain;
                }
            }
            else
            {
                var beta = 2 * AcousticBands.SpeedOfSound / _radius;
                var K = 2f * _fs;
                var a0 = K + beta; var a1 = (beta - K) / a0;
                // 노치 바이쿼드(블록 단위 갱신 · 주파수 보간은 블록 시작/끝 평균)
                var fN = 0.5f * (_nf[v] + nf);
                var w0 = MathF.Tau * fN / _fs; var cw = MathF.Cos(w0); var alpha = MathF.Sin(w0) / (2 * 2.5f);
                var depth = 0.35f + 0.25f * Math.Clamp(1 - MathF.Abs(el) / 60f, 0f, 1f);   // 노치 깊이(정면 고도에서 깊음)
                var nb0 = 1 + alpha * depth; var nb1 = -2 * cw; var nb2 = 1 - alpha * depth; var na0 = 1 + alpha; var na1 = -2 * cw; var na2 = 1 - alpha;
                nb0 /= na0; nb1 /= na0; nb2 /= na0; na1 /= na0; na2 /= na0;
                var inv = 1f / n;
                var shoulder = ShoulderReflection ? 0.25f * Math.Clamp(1 - (el + 30f) / 90f, 0f, 1f) : 0f;
                var sDelay = (0.0012f + 0.0006f * MathF.Max(0, el) / 90f) * _fs;
                for (var i = 0; i < n; i++)
                {
                    var f = (i + 1) * inv;
                    var dl = _dL[v] + (tL - _dL[v]) * f; var dr = _dR[v] + (tR - _dR[v]) * f;
                    var al = _aL[v] + (aL - _aL[v]) * f; var ar = _aR[v] + (aR - _aR[v]) * f;
                    var idx = p0 + i;
                    var xl = Read(hb, idx - dl) + shoulder * Read(hb, idx - dl - sDelay);
                    var xr = Read(hb, idx - dr) + shoulder * Read(hb, idx - dr - sDelay);
                    var yl = Shadow(_shL, v, xl, al, beta, K, a0, a1);
                    var yr = Shadow(_shR, v, xr, ar, beta, K, a0, a1);
                    yl = Biquad(_nL, v, yl, nb0, nb1, nb2, na1, na2);
                    yr = Biquad(_nR, v, yr, nb0, nb1, nb2, na1, na2);
                    outL[i] += yl * gain; outR[i] += yr * gain;
                }
            }
            _dL[v] = tL; _dR[v] = tR; _aL[v] = aL; _aR[v] = aR; _nf[v] = nf;

            // 초기 반사 탭
            var tn = Math.Min(taps.Length, MaxTaps);
            for (var k = 0; k < tn; k++)
            {
                var tap = taps[k];
                var g = 0.25f * (tap.G2 + tap.G3 + tap.G4 + tap.G5) * reflectionGain;
                if (g <= 1e-6f) continue;
                var td = Vector3.Normalize(Vector3.Transform(tap.Direction, _headInv));
                var rel = MathF.Max(0, tap.Delay - directDelay) * _fs;
                var gl = g * (0.55f - 0.45f * td.X); var gr = g * (0.55f + 0.45f * td.X);
                var dlt = rel + EarDelay(MathF.Acos(Math.Clamp(-td.X, -1f, 1f))) * _fs;
                var drt = rel + EarDelay(MathF.Acos(Math.Clamp(td.X, -1f, 1f))) * _fs;
                if (dlt >= HistoryLen - 2 || drt >= HistoryLen - 2) continue;
                for (var i = 0; i < n; i++)
                {
                    var idx = p0 + i;
                    outL[i] += Read(hb, idx - dlt) * gl;
                    outR[i] += Read(hb, idx - drt) * gr;
                }
            }
            _samples += n;
        }
    }

    private float Read(int hb, float pos)
    {
        var i0 = (int)MathF.Floor(pos); var fr = pos - i0;
        return _hist[hb + (i0 & HistMask)] * (1 - fr) + _hist[hb + ((i0 + 1) & HistMask)] * fr;
    }

    /// <summary>H(z) = ((αK+β) + (β−αK)z⁻¹) / ((K+β) + (β−K)z⁻¹) — 분모 고정이라 α 샘플 보간에 안정.</summary>
    private static float Shadow(float[] st, int v, float x, float alpha, float beta, float K, float a0, float a1)
    {
        var o = v * 2;
        var y = ((alpha * K + beta) * x + (beta - alpha * K) * st[o]) / a0 - a1 * st[o + 1];
        st[o] = x; st[o + 1] = y;
        return y;
    }

    private static float Biquad(float[] st, int v, float x, float b0, float b1, float b2, float a1, float a2)
    {
        var o = v * 4;
        var y = b0 * x + b1 * st[o] + b2 * st[o + 1] - a1 * st[o + 2] - a2 * st[o + 3];
        st[o + 1] = st[o]; st[o] = x; st[o + 3] = st[o + 2]; st[o + 2] = y;
        return y;
    }

    /// <summary>7.1.4 베드(채널 c 의 n 샘플 = bed[c·stride ..]) → 바이노럴. 보이스 MaxVoices−12 … 사용. LFE 는 양 귀 동일. 무할당.</summary>
    public void VirtualizeBed(ReadOnlySpan<float> bed, int stride, int n, Span<float> outL, Span<float> outR)
    {
        for (var c = 0; c < 12; c++)
        {
            var ch = bed.Slice(c * stride, n);
            if (c == 3) { for (var i = 0; i < n; i++) { outL[i] += ch[i] * 0.5f; outR[i] += ch[i] * 0.5f; } continue; }
            var (az, el) = BedLayout[c];
            ProcessVoice(MaxVoices - 12 + c, ch, Vector3.Transform(FromAzEl(az, el), _head), 1f, outL[..n], outR[..n]);
        }
    }
}
