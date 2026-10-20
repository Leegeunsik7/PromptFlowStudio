using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Metaverse;
using PromptFlowStudio.Services.Neuro;

namespace PromptFlowStudio.Services.Cinema;

public enum CinemaEmotion : byte { Joy = 0, Tension, Fear, Sadness, Curiosity, Awe }

public enum AudienceModality : byte { Bci = 0, Gaze, Text, Voice }

/// <summary>신경망 선택지 스코어러 결선점: features = 선택지별 <see cref="NeuralChoiceTreeEmotionEvaluator.Features"/> 개(행 우선) → logits.</summary>
public interface INeuralChoiceScorer
{
    void Score(ReadOnlySpan<float> features, int choices, Span<float> logits);
}

/// <summary>
/// [PromptFlow Studio v16.78.0] 🧠 신경망 선택지 트리 &amp; 멀티모달 감정 분기 평가기.
/// 모달리티 4종 → 6차원 감정 벡터(환희 · 긴장 · 공포 · 슬픔 · 호기심 · 경외): BCI(v16.56 EmotionState 매핑) · 시선(v16.61 XrGazeSample — 선택지 핫스팟 레이 히트 체류 ·
/// 사카드율 → 긴장 · 고정 → 호기심) · 텍스트/음성(어휘 사전 + 선택지 키워드 의도). 신뢰도 × 노후 감쇠 가중 융합 → EMA → 노드 설계 무드 대비 Jensen–Shannon 발산.
/// 선택지 특징 6종(무드 적합 · 시선 점유 · 투표 · 의도 · 사전확률 · 발산×적합) → 선형(또는 <see cref="INeuralChoiceScorer"/>) 로짓 → softmax 분기 확률. 무할당.
/// </summary>
public sealed class NeuralChoiceTreeEmotionEvaluator
{
    public const int Dim = 6, MaxChoices = 4, Modalities = 4, Features = 6;
    public static readonly string[] EmotionNames = { "환희", "긴장", "공포", "슬픔", "호기심", "경외" };
    public static readonly string[] ModalityNames = { "BCI", "시선", "텍스트", "음성" };
    /// <summary>선형 스코어 가중치(적합 · 시선 · 투표 · 의도 · 사전 · 발산×적합).</summary>
    public static readonly float[] LinearWeights = { 2.0f, 2.5f, 3.0f, 3.0f, 1.0f, 2.0f };

    private static readonly (string Word, CinemaEmotion E, float W)[] Lexicon =
    {
        ("무서", CinemaEmotion.Fear, 1f), ("소름", CinemaEmotion.Fear, 0.8f), ("공포", CinemaEmotion.Fear, 1f), ("scary", CinemaEmotion.Fear, 1f), ("도망", CinemaEmotion.Fear, 0.6f),
        ("긴장", CinemaEmotion.Tension, 1f), ("두근", CinemaEmotion.Tension, 0.7f), ("떨려", CinemaEmotion.Tension, 0.8f), ("빨리", CinemaEmotion.Tension, 0.6f), ("추격", CinemaEmotion.Tension, 0.8f), ("tense", CinemaEmotion.Tension, 1f),
        ("좋아", CinemaEmotion.Joy, 0.7f), ("최고", CinemaEmotion.Joy, 1f), ("ㅋㅋ", CinemaEmotion.Joy, 0.9f), ("웃", CinemaEmotion.Joy, 0.8f), ("구출", CinemaEmotion.Joy, 0.5f), ("haha", CinemaEmotion.Joy, 0.8f),
        ("슬퍼", CinemaEmotion.Sadness, 1f), ("눈물", CinemaEmotion.Sadness, 0.9f), ("ㅠ", CinemaEmotion.Sadness, 0.6f), ("희생", CinemaEmotion.Sadness, 0.8f), ("sad", CinemaEmotion.Sadness, 1f),
        ("궁금", CinemaEmotion.Curiosity, 1f), ("뭐지", CinemaEmotion.Curiosity, 0.8f), ("조사", CinemaEmotion.Curiosity, 0.7f), ("신호", CinemaEmotion.Curiosity, 0.5f), ("why", CinemaEmotion.Curiosity, 0.6f), ("진실", CinemaEmotion.Curiosity, 0.6f),
        ("대박", CinemaEmotion.Awe, 0.9f), ("와", CinemaEmotion.Awe, 0.5f), ("wow", CinemaEmotion.Awe, 0.9f), ("경이", CinemaEmotion.Awe, 1f), ("우주", CinemaEmotion.Awe, 0.6f),
    };

    private readonly object _gate = new();
    private readonly float[] _mod = new float[Modalities * Dim], _conf = new float[Modalities], _last = new float[Modalities];
    private readonly float[] _fused = new float[Dim], _target = new float[Dim];
    private readonly Vector3[] _hot = new Vector3[MaxChoices];
    private readonly float[] _radius = new float[MaxChoices], _mood = new float[MaxChoices * Dim], _prior = new float[MaxChoices];
    private readonly float[] _dwell = new float[MaxChoices], _votes = new float[MaxChoices], _intent = new float[MaxChoices];
    private readonly float[] _feat = new float[MaxChoices * Features], _logit = new float[MaxChoices], _prob = new float[MaxChoices];
    private readonly bool[] _eligible = new bool[MaxChoices];
    private readonly string[]?[] _keywords = new string[]?[MaxChoices];
    private int _choices;
    private float _lastT, _lastGazeT = -1f, _saccade, _fixT;
    private Vector3 _lastDir;
    private long _samples;

    public NeuralChoiceTreeEmotionEvaluator()
    {
        for (var m = 0; m < Modalities; m++) _last[m] = -1e9f;
        for (var d = 0; d < Dim; d++) _fused[d] = 0.2f;
    }

    public INeuralChoiceScorer? Model { get; set; }
    public int ChoiceCount => _choices;
    public ReadOnlySpan<float> Fused => _fused;
    public float Divergence { get; private set; }
    public float Probability(int i) => (uint)i < (uint)_choices ? _prob[i] : 0f;
    public float GazeShare(int i) => Share(_dwell, i);
    public float VoteShare(int i) => Share(_votes, i);
    public float IntentShare(int i) => Share(_intent, i);
    public float Votes(int i) => _votes[i];
    /// <summary>선택지 의도 원시 합(6 s 감쇠 · 음성 1.5 / 텍스트 1).</summary>
    public float IntentSum
    {
        get
        {
            float s = 0;
            for (var k = 0; k < _choices; k++) s += _intent[k];
            return s;
        }
    }
    public float ModalityConfidence(AudienceModality m) => _conf[(int)m];
    public float ModalityValue(AudienceModality m, CinemaEmotion e) => _mod[(int)m * Dim + (int)e];
    public float SaccadeRate => _saccade;
    public long Samples => Interlocked.Read(ref _samples);
    public int GazeHit { get; private set; } = -1;
    public bool IsEligible(int i) => (uint)i < (uint)_choices && _eligible[i];

    public CinemaEmotion Dominant
    {
        get
        {
            var b = 0;
            for (var d = 1; d < Dim; d++) if (_fused[d] > _fused[b]) b = d;
            return (CinemaEmotion)b;
        }
    }

    private float Share(float[] a, int i)
    {
        if ((uint)i >= (uint)_choices) return 0f;
        float s = 0;
        for (var k = 0; k < _choices; k++) s += a[k];
        return s > 1e-6f ? a[i] / s : 0f;
    }

    // ══════════════════════ 선택지 컨텍스트 ══════════════════════

    /// <summary>노드 진입: 선택지 수 설정 + 체류/투표/의도 초기화.</summary>
    public void BeginChoices(int count)
    {
        lock (_gate)
        {
            _choices = Math.Clamp(count, 0, MaxChoices);
            Array.Clear(_dwell); Array.Clear(_votes); Array.Clear(_intent); Array.Clear(_prob); Array.Clear(_eligible);
            GazeHit = -1;
        }
    }

    public void SetChoice(int i, Vector3 hotspot, float radius, ReadOnlySpan<float> mood, float prior, string[]? keywords, bool eligible)
    {
        lock (_gate)
        {
            if ((uint)i >= MaxChoices) return;
            _hot[i] = hotspot; _radius[i] = radius; _prior[i] = prior; _keywords[i] = keywords; _eligible[i] = eligible;
            mood[..Dim].CopyTo(_mood.AsSpan(i * Dim, Dim));
        }
    }

    public Vector3 Hotspot(int i) => _hot[i];

    // ══════════════════════ 모달리티 입력 ══════════════════════

    /// <summary>BCI 감정 상태 → 6차원(정서가/각성 원형 모델 매핑).</summary>
    public void PushBci(in EmotionState e, float t)
    {
        lock (_gate)
        {
            var v = _mod.AsSpan((int)AudienceModality.Bci * Dim, Dim);
            float val = e.Valence, ar = e.Arousal, en = e.Engagement;
            v[(int)CinemaEmotion.Joy] = Math.Clamp((val - 0.5f) * 2f, 0f, 1f) * (0.5f + 0.5f * ar);
            v[(int)CinemaEmotion.Tension] = Math.Clamp(ar * (1.2f - val), 0f, 1f);
            v[(int)CinemaEmotion.Fear] = Math.Clamp(e.Fear, 0f, 1f);
            v[(int)CinemaEmotion.Sadness] = Math.Clamp((0.5f - val) * 2f, 0f, 1f) * (1f - 0.6f * ar);
            v[(int)CinemaEmotion.Curiosity] = Math.Clamp(en * (1f - e.Fear) * (0.6f + 0.4f * ar), 0f, 1f);
            v[(int)CinemaEmotion.Awe] = Math.Clamp(ar * val * en * 1.5f, 0f, 1f);
            _conf[(int)AudienceModality.Bci] = MathF.Max(0.5f, e.Confidence);
            _last[(int)AudienceModality.Bci] = t;
            _samples++;
        }
    }

    /// <summary>시선 샘플 → 핫스팟 체류(3 s 감쇠) · 사카드율(&gt;100°/s) · 고정 시간.</summary>
    public int PushGaze(in XrGazeSample g, float t)
    {
        lock (_gate)
        {
            if (g.Confidence < 0.3f || g.Direction.LengthSquared() < 1e-8f) return GazeHit;
            var dir = Vector3.Normalize(g.Direction);
            var dt = _lastGazeT >= 0 ? Math.Clamp(t - _lastGazeT, 0f, 0.2f) : 0f;
            _lastGazeT = t;
            if (dt > 0)
            {
                var ang = MathF.Acos(Math.Clamp(Vector3.Dot(dir, _lastDir), -1f, 1f)) * (180f / MathF.PI) / dt;
                var sac = ang > 100f ? 1f / dt : 0f;
                var a = 1f - MathF.Exp(-dt / 1f);
                _saccade += (sac - _saccade) * a;
                _fixT = ang > 100f ? 0f : _fixT + dt;
                var decay = MathF.Exp(-dt / 3f);
                for (var k = 0; k < _choices; k++) _dwell[k] *= decay;
            }
            _lastDir = dir;
            // 레이 · 구 핫스팟(가장 가까운 히트)
            var hit = -1; var best = float.MaxValue;
            for (var k = 0; k < _choices; k++)
            {
                if (!_eligible[k]) continue;
                var oc = _hot[k] - g.Origin; var tc = Vector3.Dot(oc, dir);
                if (tc <= 0) continue;
                var d2 = oc.LengthSquared() - tc * tc;
                if (d2 <= _radius[k] * _radius[k] && tc < best) { best = tc; hit = k; }
            }
            if (hit >= 0) _dwell[hit] += dt * g.Confidence;
            GazeHit = hit;
            var v = _mod.AsSpan((int)AudienceModality.Gaze * Dim, Dim);
            v.Clear();
            v[(int)CinemaEmotion.Tension] = Math.Clamp(_saccade / 3f, 0f, 1f);
            v[(int)CinemaEmotion.Curiosity] = Math.Clamp(_fixT / 1.5f, 0f, 1f) * (hit >= 0 ? 1f : 0.5f);
            _conf[(int)AudienceModality.Gaze] = 0.4f * g.Confidence;
            _last[(int)AudienceModality.Gaze] = t;
            _samples++;
            return hit;
        }
    }

    /// <summary>텍스트/음성(STT) 반응 → 어휘 감정 + 선택지 키워드 의도(음성 ×1.5). 무할당(스팬 서수 대소문자 무시 검색).</summary>
    public void PushText(ReadOnlySpan<char> text, bool voice, float t)
    {
        lock (_gate)
        {
            var m = voice ? AudienceModality.Voice : AudienceModality.Text;
            var v = _mod.AsSpan((int)m * Dim, Dim);
            Span<float> acc = stackalloc float[Dim];
            var any = ScoreLexicon(text, acc);
            if (any)
            {
                acc.CopyTo(v);
                _conf[(int)m] = voice ? 0.7f : 0.6f;
                _last[(int)m] = t;
            }
            for (var k = 0; k < _choices; k++)
            {
                var kw = _keywords[k];
                if (kw is null || !_eligible[k]) continue;
                for (var w = 0; w < kw.Length; w++)
                    if (text.Contains(kw[w].AsSpan(), StringComparison.OrdinalIgnoreCase)) { _intent[k] += voice ? 1.5f : 1f; break; }
            }
            _samples++;
        }
    }

    /// <summary>어휘 사전 감정 점수(최대 1 정규화). 반환 = 적중 여부.</summary>
    public static bool ScoreLexicon(ReadOnlySpan<char> text, Span<float> dst)
    {
        dst[..Dim].Clear();
        var any = false;
        for (var i = 0; i < Lexicon.Length; i++)
        {
            if (!text.Contains(Lexicon[i].Word.AsSpan(), StringComparison.OrdinalIgnoreCase)) continue;
            dst[(int)Lexicon[i].E] += Lexicon[i].W; any = true;
        }
        if (!any) return false;
        float mx = 0;
        for (var d = 0; d < Dim; d++) mx = MathF.Max(mx, dst[d]);
        for (var d = 0; d < Dim; d++) dst[d] /= mx;
        return true;
    }

    public void PushVote(int choice, float weight = 1f)
    {
        lock (_gate) { if ((uint)choice < (uint)_choices && _eligible[choice]) _votes[choice] += weight; _samples++; }
    }

    // ══════════════════════ 융합 · 분기 확률 ══════════════════════

    /// <summary>융합(신뢰도 × 4 s 노후 감쇠 → 0.8 s EMA) · 발산 · 선택지 확률(무할당).</summary>
    public void Update(float t, ReadOnlySpan<float> nodeMood)
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Cinema.Evaluate");
            var dt = Math.Clamp(t - _lastT, 0f, 1f);
            _lastT = t;
            float sw = 0;
            _target.AsSpan().Clear();
            for (var m = 0; m < Modalities; m++)
            {
                var age = t - _last[m];
                if (age > 30f) continue;
                var w = _conf[m] * MathF.Exp(-MathF.Max(0f, age) / 4f);
                sw += w;
                for (var d = 0; d < Dim; d++) _target[d] += w * _mod[m * Dim + d];
            }
            if (sw > 1e-4f)
            {
                var a = 1f - MathF.Exp(-dt / 0.8f);
                for (var d = 0; d < Dim; d++) _fused[d] += (_target[d] / sw - _fused[d]) * a;
            }
            var di = MathF.Exp(-dt / 6f);
            for (var k = 0; k < _choices; k++) _intent[k] *= di;
            Divergence = nodeMood.Length >= Dim ? JensenShannon(_fused, nodeMood) : 0f;

            if (_choices == 0) return;
            float sd = 0, sv = 0, si = 0;
            for (var k = 0; k < _choices; k++) { sd += _dwell[k]; sv += _votes[k]; si += _intent[k]; }
            for (var k = 0; k < _choices; k++)
            {
                var f = _feat.AsSpan(k * Features, Features);
                var fit = Cosine(_fused, _mood.AsSpan(k * Dim, Dim));
                f[0] = fit;
                f[1] = sd > 1e-4f ? _dwell[k] / sd : 0f;
                f[2] = sv > 1e-4f ? _votes[k] / sv : 0f;
                f[3] = si > 1e-4f ? _intent[k] / si : 0f;
                f[4] = _prior[k];
                f[5] = Divergence * fit;
            }
            var logits = _logit.AsSpan(0, _choices);
            if (Model is not null) Model.Score(_feat.AsSpan(0, _choices * Features), _choices, logits);
            else
                for (var k = 0; k < _choices; k++)
                {
                    float z = 0;
                    for (var j = 0; j < Features; j++) z += LinearWeights[j] * _feat[k * Features + j];
                    logits[k] = z;
                }
            Softmax(logits, _eligible, _prob.AsSpan(0, _choices));
        }
    }

    /// <summary>심층 노드용 사전 로짓(현재 융합 감정 적합 + 사전확률 · 실시간 신호 없음).</summary>
    public float PriorLogit(ReadOnlySpan<float> mood, float prior) => LinearWeights[0] * Cosine(_fused, mood) + LinearWeights[4] * prior;

    public float Feature(int choice, int j) => _feat[choice * Features + j];

    public static void Softmax(ReadOnlySpan<float> logits, ReadOnlySpan<bool> eligible, Span<float> dst)
    {
        var mx = float.MinValue;
        for (var k = 0; k < logits.Length; k++) if (eligible[k]) mx = MathF.Max(mx, logits[k]);
        float s = 0;
        for (var k = 0; k < logits.Length; k++) { dst[k] = eligible[k] ? MathF.Exp(logits[k] - mx) : 0f; s += dst[k]; }
        if (s <= 0) return;
        for (var k = 0; k < logits.Length; k++) dst[k] /= s;
    }

    public static float Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float d = 0, na = 0, nb = 0;
        for (var i = 0; i < Dim; i++) { d += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na > 1e-9f && nb > 1e-9f ? d / MathF.Sqrt(na * nb) : 0f;
    }

    /// <summary>Jensen–Shannon 발산(log2 · 0..1).</summary>
    public static float JensenShannon(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float sa = 0, sb = 0;
        for (var i = 0; i < Dim; i++) { sa += MathF.Max(0f, a[i]) + 1e-4f; sb += MathF.Max(0f, b[i]) + 1e-4f; }
        float js = 0;
        for (var i = 0; i < Dim; i++)
        {
            var p = (MathF.Max(0f, a[i]) + 1e-4f) / sa; var q = (MathF.Max(0f, b[i]) + 1e-4f) / sb; var m = 0.5f * (p + q);
            js += 0.5f * (p * MathF.Log2(p / m) + q * MathF.Log2(q / m));
        }
        return Math.Clamp(js, 0f, 1f);
    }
}
