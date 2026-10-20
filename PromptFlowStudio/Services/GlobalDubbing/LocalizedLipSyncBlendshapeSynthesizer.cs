using PromptFlowStudio.Services.DigitalHuman;
using PromptFlowStudio.Services.GaussianFaceHand;
using PromptFlowStudio.Services.LiveDubbing;
using PromptFlowStudio.Services.Localization.Dubbing;

namespace PromptFlowStudio.Services.GlobalDubbing;

/// <summary>
/// [PromptFlow Studio v17.64.0] 타깃 언어 기반 가우시안 립싱크 블렌드셰이프 합성기(60 fps · 무할당).
///   ▸ 오디오 마스터 시계: 레인 렌더 샘플 − 출력 지연 = 재생 중 음성 시각 → 비디오 표시 시각과 편차 추정(슬루 8 % · 40 ms 초과 즉시 재동기) → 잔여 편차 &lt; 50 ms.
///   ▸ 해당 시각의 v17.32 음소(이전 · 현재 · 혼합비 · 포락) → 16 음소 × 18 입/턱 ARKit 채널 표를 선형 보간 · 언어 조음 스타일(턱 · 원순 · 평순) 배율 · 감정 미소.
///   ▸ v17.58 52 블렌드셰이프 가우시안 델타 Blend → 입술 찢어짐 가드(<see cref="FacialSplatTearFingerIntersectionGuard"/>) 매 프레임 적용(언어 전환 무관).
/// </summary>
public sealed class LocalizedLipSyncBlendshapeSynthesizer
{
    public const int DefaultSplats = 6000, Hist = 240;
    public const float ResyncMs = 40f, BudgetMs = 50f, Slew = 0.08f, Attack = 0.75f, Release = 0.55f;

    public static readonly ArkitBlendshape[] Channels =
    {
        ArkitBlendshape.JawOpen, ArkitBlendshape.MouthClose, ArkitBlendshape.MouthFunnel, ArkitBlendshape.MouthPucker,
        ArkitBlendshape.MouthSmileLeft, ArkitBlendshape.MouthSmileRight, ArkitBlendshape.MouthStretchLeft, ArkitBlendshape.MouthStretchRight,
        ArkitBlendshape.MouthPressLeft, ArkitBlendshape.MouthPressRight, ArkitBlendshape.MouthRollLower, ArkitBlendshape.MouthRollUpper,
        ArkitBlendshape.MouthLowerDownLeft, ArkitBlendshape.MouthLowerDownRight, ArkitBlendshape.MouthUpperUpLeft, ArkitBlendshape.MouthUpperUpRight,
        ArkitBlendshape.TongueOut, ArkitBlendshape.JawForward,
    };
    public const int C = 18;

    // 음소 × 채널: Jaw · Close · Funnel · Pucker · Smile(L,R) · Stretch(L,R) · Press(L,R) · RollLo · RollUp · LowerDown(L,R) · UpperUp(L,R) · Tongue · JawFwd
    private static readonly float[] Table =
    {
        0f,   0.05f, 0f,   0f,   0f,   0f,   0f,   0f,   0f,   0f,   0f,   0f,   0f,   0f,   0f,   0f,   0f,   0f,    // Rest
        0.62f, 0f,   0.05f, 0f,   0.10f, 0.10f, 0.05f, 0.05f, 0f,  0f,   0f,   0f,   0.30f, 0.30f, 0.15f, 0.15f, 0f,  0f,    // A
        0.38f, 0f,   0f,   0f,   0.32f, 0.32f, 0.22f, 0.22f, 0f,   0f,   0f,   0f,   0.22f, 0.22f, 0.12f, 0.12f, 0f,  0f,    // E
        0.18f, 0f,   0f,   0f,   0.45f, 0.45f, 0.30f, 0.30f, 0f,   0f,   0f,   0f,   0.10f, 0.10f, 0.08f, 0.08f, 0f,  0f,    // I
        0.42f, 0f,   0.55f, 0.35f, 0f,  0f,   0f,   0f,   0f,   0f,   0f,   0f,   0.12f, 0.12f, 0f,   0f,   0f,  0f,    // O
        0.15f, 0f,   0.35f, 0.70f, 0f,  0f,   0f,   0f,   0f,   0f,   0f,   0f,   0f,   0f,   0f,   0f,   0f,  0.05f, // U
        0f,   0.60f, 0f,   0f,   0f,   0f,   0f,   0f,   0.45f, 0.45f, 0.10f, 0.10f, 0f,  0f,   0f,   0f,   0f,  0f,    // P/B/M
        0.08f, 0.10f, 0f,  0f,   0f,   0f,   0.05f, 0.05f, 0f,  0f,   0.55f, 0f,   0f,   0f,   0.20f, 0.20f, 0f,  0f,    // F/V
        0.15f, 0f,   0f,   0f,   0f,   0f,   0.08f, 0.08f, 0f,  0f,   0f,   0f,   0.10f, 0.10f, 0.05f, 0.05f, 0.40f, 0f,  // Th
        0.25f, 0f,   0f,   0f,   0.10f, 0.10f, 0.08f, 0.08f, 0f, 0f,   0f,   0f,   0.15f, 0.15f, 0.05f, 0.05f, 0.15f, 0f,  // L
        0.08f, 0f,   0f,   0f,   0.25f, 0.25f, 0.30f, 0.30f, 0f,  0f,   0f,   0f,   0.05f, 0.05f, 0.10f, 0.10f, 0f,  0f,    // S/Z
        0.25f, 0f,   0f,   0f,   0.05f, 0.05f, 0.10f, 0.10f, 0f,  0f,   0f,   0f,   0.10f, 0.10f, 0f,   0f,   0f,  0f,    // K/G
        0.18f, 0f,   0f,   0f,   0.10f, 0.10f, 0.12f, 0.12f, 0f,  0f,   0f,   0f,   0.08f, 0.08f, 0.05f, 0.05f, 0.05f, 0f,  // T/D
        0.20f, 0f,   0.30f, 0.30f, 0f,  0f,   0f,   0f,   0f,   0f,   0f,   0f,   0.05f, 0.05f, 0f,   0f,   0f,  0f,    // R
        0.10f, 0f,   0.40f, 0.75f, 0f,  0f,   0f,   0f,   0f,   0f,   0f,   0f,   0f,   0f,   0f,   0f,   0f,  0.05f, // W
        0.12f, 0.15f, 0f,  0f,   0.08f, 0.08f, 0.05f, 0.05f, 0f, 0f,   0f,   0f,   0f,   0f,   0f,   0f,   0.05f, 0f,  // N
    };

    private readonly float[] _w = new float[C], _y = new float[C];
    private readonly float[] _driftS = new float[Hist], _apS = new float[Hist];
    private int _sHead, _sN;
    private double _offset;
    private bool _locked;
    private DubLanguage _lastLang = DubLanguage.Ko;

    public LocalizedLipSyncBlendshapeSynthesizer(int splats = DefaultSplats)
    {
        Face = new RealtimeGaussianFacialBlendshapeEngine(splats);
        Guard = new FacialSplatTearFingerIntersectionGuard(Face) { FingerEnabled = false };
    }

    public RealtimeGaussianFacialBlendshapeEngine Face { get; }
    public FacialSplatTearFingerIntersectionGuard Guard { get; }
    /// <summary>오디오 장치 출력 지연(ms) — 렌더 샘플 대비 실제 재생 시각 보정.</summary>
    public float OutputLatencyMs { get; set; } = 20f;
    public float Smile { get; set; }
    public LipPhoneme Current { get; private set; }
    public LipPhoneme Previous { get; private set; }
    public float Blend01 { get; private set; }
    public float Envelope { get; private set; }
    public float RawDriftMs { get; private set; }
    public float DriftMs { get; private set; }
    public float MaxDriftMs { get; private set; }
    public long Resyncs { get; private set; }
    public long LanguageSwitches { get; private set; }
    public long Frames { get; private set; }
    public double LastFrameUs { get; private set; }
    public double MaxFrameUs { get; private set; }
    public float Weight(int c) => _y[c];
    public int SeriesCount => _sN;
    private int Idx(int i) => (_sHead - 1 - i + 2 * Hist) % Hist;
    public float DriftSeries(int i) => _driftS[Idx(i)];
    public float ApertureSeries(int i) => _apS[Idx(i)];

    /// <summary>언어 조음 스타일 — 턱 · 원순(funnel/pucker) · 평순(smile/stretch) 배율.</summary>
    public static void Style(DubLanguage l, out float jaw, out float round, out float spread) => (jaw, round, spread) = l switch
    {
        DubLanguage.Ja => (0.80f, 0.85f, 0.90f),
        DubLanguage.Es => (1.10f, 1.00f, 1.00f),
        DubLanguage.Fr => (0.95f, 1.25f, 0.90f),
        DubLanguage.De => (1.05f, 1.10f, 1.00f),
        DubLanguage.Zh => (0.95f, 1.00f, 1.05f),
        DubLanguage.En => (1.00f, 1.10f, 1.10f),
        _ => (0.90f, 0.95f, 1.00f),
    };

    public void ResetSync() { _locked = false; _offset = 0; MaxDriftMs = 0; Resyncs = 0; DriftMs = RawDriftMs = 0; }

    /// <summary>비디오 프레임 1장 — 표시 시각(ms)에 대응하는 레인 음성 위치의 음소로 52 블렌드셰이프 · 가우시안 · 찢어짐 가드 갱신.</summary>
    public void Frame(RealtimeNeuralVoiceCloningDubbingEngine voice, int lane, DubLanguage lang, double videoMs)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (lang != _lastLang) { _lastLang = lang; LanguageSwitches++; }
        var clock = lane >= 0 ? voice.ClockOf(lane) : 0L;
        var audioMs = clock * 1000.0 / RealtimeNeuralVoiceCloningDubbingEngine.Fs - OutputLatencyMs;
        var raw = videoMs - audioMs;
        if (!_locked) { _offset = raw; _locked = true; }
        var err = raw - _offset;
        if (Math.Abs(err) > ResyncMs) { _offset = raw; Resyncs++; err = 0; }
        else _offset += err * Slew;
        RawDriftMs = (float)err;
        var d = (float)Math.Abs(raw - _offset);
        DriftMs = d; if (d > MaxDriftMs && Frames > 2) MaxDriftMs = d;

        LipPhoneme ph = LipPhoneme.Rest, prev = LipPhoneme.Rest; float blend = 1f, env = 0f;
        if (lane >= 0)
        {
            var pos = (long)((videoMs - _offset) * RealtimeNeuralVoiceCloningDubbingEngine.Fs / 1000.0);
            pos = Math.Clamp(pos, 0, Math.Max(0, clock - RealtimeNeuralVoiceCloningDubbingEngine.Hop));
            ph = voice.PhonemeAt(lane, pos, out prev, out blend, out env);
        }
        Current = ph; Previous = prev; Blend01 = blend; Envelope = env;
        Map(prev, ph, blend, env, lang, Smile, _w);
        for (var c = 0; c < C; c++)
        {
            var k = _w[c] > _y[c] ? Attack : Release;
            _y[c] += (_w[c] - _y[c]) * k;
            Face.SetWeight(Channels[c], _y[c]);
        }
        Face.Blend();
        Guard.ApplyFace(Face);
        Frames++;
        _driftS[_sHead] = d; _apS[_sHead] = Face.Aperture; _sHead = (_sHead + 1) % Hist; if (_sN < Hist) _sN++;
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        LastFrameUs = us; if (us > MaxFrameUs && Frames > 5) MaxFrameUs = us;
    }

    /// <summary>음소 쌍 선형 보간 → 언어 스타일 · 포락 · 감정 미소 → 채널 가중치(무할당).</summary>
    public static void Map(LipPhoneme prev, LipPhoneme cur, float blend, float env, DubLanguage lang, float smile, Span<float> w)
    {
        Style(lang, out var jaw, out var round, out var spread);
        var b = Math.Clamp(blend, 0f, 1f);
        var g = Math.Clamp(0.35f + 0.65f * MathF.Sqrt(Math.Clamp(env * 4f, 0f, 1f)), 0f, 1f);
        int pa = (int)prev * C, pb = (int)cur * C;
        for (var c = 0; c < C; c++)
        {
            var v = Table[pa + c] + (Table[pb + c] - Table[pa + c]) * b;
            v *= c switch { 0 or 12 or 13 => jaw, 2 or 3 => round, 4 or 5 or 6 or 7 => spread, _ => 1f };
            w[c] = c == 1 ? v : v * g;
        }
        var s = 0.25f * Math.Clamp(smile, 0f, 1f);
        w[4] += s; w[5] += s;
    }
}
