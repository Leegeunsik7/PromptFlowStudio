using System.Numerics;
using PromptFlowStudio.Services.AnatomicalTissue;
using PromptFlowStudio.Services.DigitalHuman;
using PromptFlowStudio.Services.StrandHair;

namespace PromptFlowStudio.Services.SkinOptics;

public enum VascularRegion : byte { CheekL = 0, CheekR, Nose, Forehead, EarL, EarR, Chin }

/// <summary>
/// [PromptFlow Studio v17.17.0] 🩸 미세 혈관 혈류 동역학 &amp; 감정 홍조 합성기(<see cref="Update"/> 무할당).
///   ▸ 감정 입력: v17.00 AGI 디렉터(정서가 · 각성 · 긴장) + v17.12 페이셜 ARKit 52(browDown · noseSneer → 분노 · smile · cheekSquint → 수줍음 · eyeWide · browInnerUp → 긴장) →
///     분노 · 수줍음 · 긴장 목표 → 1차 혈관 반응(확장 τ 2.5 s · 회복 τ 7 s — 홍조는 느리게 오르고 더 느리게 빠짐).
///   ▸ 심박: HR = 62 + 50·각성 + 25·긴장 + 18·분노(bpm) · PPG 파형 = 수축기 가우스 + 중복 절흔(dicrotic) 가우스 → 256 프레임 링버퍼 ·
///     영역별 맥파 전달 지연(턱 140 → 이마 175 ms) 보간 읽기 → 국소 맥동 혈액량 ±6%.
///   ▸ 혈액량 맵: 기저(입술 · 뺨 · 코 가산) + Σᵣ 영역 가우스 가중[r][t]·cᵣ(SIMD 누적 · v17.14 커널) · cᵣ = 홍조 + 맥동.
///   ▸ 산소 포화도 S: 기저 0.95 · 분노(과관류) → 0.97 · 긴장(혈관 수축 · 정체) → 0.88.
///   ▸ 알베도(비어–람베르트 RGB): ρ_c = 기저 × exp(−(멜라닌·μmel + V·L·(S·μHbO₂ + (1−S)·μHb))_c) → SSS 엔진 알베도 · 영역 홍반 지수 EI = 100·log₁₀(R/G).
/// </summary>
public sealed class MicroVascularHemodynamicsFlushingSynthesizer
{
    public const int Regions = 7, Ring = 256, N = SeparableNeuralSkinSssEngine.N;
    public const float PulseAmp = 0.06f, PathLen = 30f, TauRise = 2.5f, TauFall = 7f;
    public static readonly string[] RegionNames = { "뺨(좌)", "뺨(우)", "코끝", "이마", "귀(좌)", "귀(우)", "턱" };
    private static readonly Vector2[] Center = { new(0.29f, 0.56f), new(0.71f, 0.56f), new(0.5f, 0.53f), new(0.5f, 0.2f), new(0.055f, 0.48f), new(0.945f, 0.48f), new(0.5f, 0.86f) };
    private static readonly Vector2[] Sigma = { new(0.08f, 0.07f), new(0.08f, 0.07f), new(0.04f, 0.05f), new(0.16f, 0.06f), new(0.035f, 0.1f), new(0.035f, 0.1f), new(0.09f, 0.04f) };
    private static readonly float[] GainAnger = { 0.8f, 0.8f, 0.6f, 1f, 0.9f, 0.9f, 0.5f };
    private static readonly float[] GainShy = { 1f, 1f, 0.75f, 0.3f, 1f, 1f, 0.2f };
    private static readonly float[] TransitMs = { 150f, 150f, 155f, 175f, 165f, 165f, 140f };
    public static readonly Vector3 MuMel = new(0.12f, 0.22f, 0.4f), MuOxy = new(0.02f, 0.35f, 0.2f), MuDeoxy = new(0.15f, 0.42f, 0.28f), BaseAlbedo = new(0.95f, 0.92f, 0.88f);

    private readonly SeparableNeuralSkinSssEngine _skin;
    private readonly float[] _w = new float[Regions * N];
    private readonly float[] _c = new float[Regions], _flush = new float[Regions], _pulse = new float[Regions];
    private readonly float[] _base = new float[N], _vol = new float[N];
    private readonly float[] _ppg = new float[Ring], _hrRing = new float[Ring];
    private readonly float[] _erythema = new float[Regions], _regVol = new float[Regions];
    private int _head, _count;
    private float _phase;

    public MicroVascularHemodynamicsFlushingSynthesizer(SeparableNeuralSkinSssEngine skin)
    {
        _skin = skin;
        for (var y = 0; y < SeparableNeuralSkinSssEngine.H; y++)
            for (var x = 0; x < SeparableNeuralSkinSssEngine.W; x++)
            {
                var i = SeparableNeuralSkinSssEngine.Index(x, y);
                float u = (x + 0.5f) / SeparableNeuralSkinSssEngine.W, v = (y + 0.5f) / SeparableNeuralSkinSssEngine.H;
                var m = skin.Mask[i];
                for (var r = 0; r < Regions; r++)
                {
                    float du = (u - Center[r].X) / Sigma[r].X, dv = (v - Center[r].Y) / Sigma[r].Y;
                    _w[r * N + i] = m * MathF.Exp(-0.5f * (du * du + dv * dv));
                }
                var reg = skin.RegionOf[i];
                _base[i] = m * (reg == SkinRegion.Lips ? 0.07f : reg == SkinRegion.Finger ? 0.035f : reg == SkinRegion.Ear ? 0.03f : 0.022f);
            }
        Path = skin.Path;
        Update(0f);
    }

    public HairSimdPath Path { get; set; }
    public float Valence { get; private set; }
    public float Arousal { get; private set; }
    public float DirectorTension { get; private set; }
    public float AngerTarget { get; private set; }
    public float ShyTarget { get; private set; }
    public float TensionTarget { get; private set; }
    public float Anger { get; private set; }
    public float Shyness { get; private set; }
    public float Tension { get; private set; }
    /// <summary>수동 심박(0 = 감정 연동).</summary>
    public float BpmOverride { get; set; }
    public float Bpm { get; private set; } = 62f;
    public float FlushGain { get; set; } = 0.045f;
    public float Melanin { get; set; } = 1f;
    public float Saturation { get; private set; } = 0.95f;
    public float Ppg { get; private set; }
    public int Beats { get; private set; }
    public double LastUpdateUs { get; private set; }
    public float Volume(int i) => _vol[i];
    public float RegionVolume(int r) => _regVol[r];
    public float RegionErythema(int r) => _erythema[r];
    public float RegionFlush(int r) => _flush[r];
    public float RegionPulse(int r) => _pulse[r];
    public float RegionWeight(int r, int i) => _w[r * N + i];
    public int RingCount => _count;
    public float PpgSample(int back) => _ppg[(_head - 1 - back + 2 * Ring) % Ring];
    public float HeartRateSample(int back) => _hrRing[(_head - 1 - back + 2 * Ring) % Ring];

    /// <summary>PPG 파형(위상 0..1): 수축기 피크 + 중복 절흔 후 반사파.</summary>
    public static float PpgWave(float f)
    {
        float a = (f - 0.15f) / 0.06f, b = (f - 0.42f) / 0.09f;
        return MathF.Exp(-a * a) + 0.35f * MathF.Exp(-b * b);
    }

    /// <summary>감정 입력(디렉터 정서 + 페이셜 ARKit 52 · 빈 범위 허용).</summary>
    public void SetEmotion(float valence, float arousal, float tension, ReadOnlySpan<float> arkit)
    {
        Valence = valence; Arousal = arousal; DirectorTension = tension;
        float brow = 0, sneer = 0, smile = 0, squint = 0, wide = 0, inner = 0;
        if (arkit.Length >= NeuralFacialMuscleSimulator.ShapeCount)
        {
            brow = 0.5f * (arkit[(int)ArkitBlendshape.BrowDownLeft] + arkit[(int)ArkitBlendshape.BrowDownRight]);
            sneer = 0.5f * (arkit[(int)ArkitBlendshape.NoseSneerLeft] + arkit[(int)ArkitBlendshape.NoseSneerRight]);
            smile = 0.5f * (arkit[(int)ArkitBlendshape.MouthSmileLeft] + arkit[(int)ArkitBlendshape.MouthSmileRight]);
            squint = 0.5f * (arkit[(int)ArkitBlendshape.CheekSquintLeft] + arkit[(int)ArkitBlendshape.CheekSquintRight]);
            wide = 0.5f * (arkit[(int)ArkitBlendshape.EyeWideLeft] + arkit[(int)ArkitBlendshape.EyeWideRight]);
            inner = arkit[(int)ArkitBlendshape.BrowInnerUp];
        }
        AngerTarget = Math.Clamp(1.6f * arousal * MathF.Max(0f, -valence) + 0.6f * brow + 0.4f * sneer, 0f, 1f);
        ShyTarget = Math.Clamp((0.9f * MathF.Max(0f, valence) * (1f - 0.6f * arousal) + 0.5f * smile + 0.4f * squint) * (1f - 0.5f * AngerTarget), 0f, 1f);
        TensionTarget = Math.Clamp(1.2f * tension * (1f - 0.5f * MathF.Max(0f, valence)) + 0.5f * wide + 0.3f * inner, 0f, 1f);
    }

    /// <summary>혈관 반응 → 심박/PPG 링버퍼 → 혈액량 · 포화도 · 알베도(SSS 엔진 기록 · 무할당).</summary>
    public void Update(float dt)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (dt > 0f)
        {
            float kr = 1f - MathF.Exp(-dt / TauRise), kf = 1f - MathF.Exp(-dt / TauFall);
            Anger += (AngerTarget - Anger) * (AngerTarget > Anger ? kr : kf);
            Shyness += (ShyTarget - Shyness) * (ShyTarget > Shyness ? kr : kf);
            Tension += (TensionTarget - Tension) * (TensionTarget > Tension ? kr : kf);
        }
        Bpm = BpmOverride > 0f ? BpmOverride : 62f + 50f * Arousal + 25f * Tension + 18f * Anger;
        var prev = _phase;
        _phase += Bpm / 60f * dt;
        if ((int)_phase != (int)prev) Beats++;
        var f = _phase - MathF.Floor(_phase);
        Ppg = PpgWave(f);
        _ppg[_head] = Ppg; _hrRing[_head] = Bpm;
        _head = (_head + 1) % Ring;
        if (_count < Ring) _count++;
        // 영역 계수: 홍조(확장) − 긴장(수축) + 맥동(지연)
        var constrict = 1f - 0.55f * Tension;
        for (var r = 0; r < Regions; r++)
        {
            _flush[r] = FlushGain * (Anger * GainAnger[r] + Shyness * GainShy[r]) - 0.012f * Tension;
            var d = TransitMs[r] * 60f / 1000f;
            var di = (int)d; var df = d - di;
            float a = PpgSample(Math.Min(di, _count - 1)), b = PpgSample(Math.Min(di + 1, _count - 1));
            _pulse[r] = 0.03f * PulseAmp * ((a + (b - a) * df) - 0.45f) * constrict;
            _c[r] = _flush[r] + _pulse[r];
        }
        NeuralAnatomicalMuscleFasciaEngine.Accumulate((TissueSimdPath)(byte)Path, _w, _c, Regions, N, _vol);
        Saturation = Math.Clamp(0.95f + 0.02f * Anger - 0.07f * Tension, 0.8f, 0.99f);
        var mu = Saturation * MuOxy + (1f - Saturation) * MuDeoxy;
        var mel = Melanin * MuMel;
        var sk = _skin;
        for (var i = 0; i < N; i++)
        {
            var v = MathF.Max(0f, _base[i] * constrict + _vol[i]);
            _vol[i] = v;
            var a = mel + v * PathLen * mu;
            sk.AlbedoR[i] = BaseAlbedo.X * MathF.Exp(-a.X); sk.AlbedoG[i] = BaseAlbedo.Y * MathF.Exp(-a.Y); sk.AlbedoB[i] = BaseAlbedo.Z * MathF.Exp(-a.Z);
        }
        for (var r = 0; r < Regions; r++)
        {
            double sw = 0, sv = 0, sR = 0, sG = 0;
            for (var i = 0; i < N; i++)
            {
                var w = _w[r * N + i];
                if (w < 0.3f) continue;
                sw += w; sv += w * _vol[i]; sR += w * sk.AlbedoR[i]; sG += w * sk.AlbedoG[i];
            }
            _regVol[r] = sw > 0 ? (float)(sv / sw) : 0f;
            _erythema[r] = sw > 0 ? 100f * MathF.Log10((float)(sR / Math.Max(1e-9, sG))) : 0f;
        }
        LastUpdateUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }
}
