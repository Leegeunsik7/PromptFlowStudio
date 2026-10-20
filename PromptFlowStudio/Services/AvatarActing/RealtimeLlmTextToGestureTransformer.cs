using System.Numerics;
using PromptFlowStudio.Services.DigitalHuman;
using PromptFlowStudio.Services.NarrativeTimeline;
namespace PromptFlowStudio.Services.AvatarActing;

public enum GestureClass : byte { Rest, Beat, DeicticSelf, DeicticOther, Inclusive, Refuse, Emphasis, Think }
public enum ActingPersona : byte { Calm, Exaggerated, TedSpeaker }
public enum HandShape : byte { Relaxed, Open, Point, Fist, Palm }

/// <summary>성향 프리셋 — 제스처 공간 배율 · 비트 감도 · 스프링 반감기 s · 척추 배율 · 무대 보행 m/s · 소프트맥스 온도.</summary>
public readonly record struct PersonaSpec(string Name, float Space, float BeatGain, float HalfLife, float SpineGain, float WalkSpeed, float Temperature, float FaceGain)
{
    public static PersonaSpec Of(ActingPersona p) => p switch
    {
        ActingPersona.Exaggerated => new("🎭 과장된", 1.25f, 1.4f, 0.07f, 1.5f, 0.35f, 0.6f, 1.3f),
        ActingPersona.TedSpeaker => new("🎤 TED 강연자", 1.1f, 1.0f, 0.10f, 1.0f, 0.75f, 0.8f, 1.0f),
        _ => new("🍵 차분한", 0.7f, 0.5f, 0.18f, 0.6f, 0f, 1.2f, 0.6f),
    };
}

/// <summary>토큰(어절) 스케줄 — 원문 링 위치 · 시각 · 계획 운율 · 분류 결과.</summary>
public struct GestureToken
{
    public int CharStart, CharLen, Line;
    public double OnsetS;
    public float DurS, PlanPitch, PlanEnergy, Valence, Arousal, Confidence;
    public GestureClass Class;
    public bool Inferred, LineEnd;
}

/// <summary>60 fps 방출 프레임 — 몸통(가슴) 로컬 손 목표(x 좌→우 · y 위 · z 전방) · 척추/머리 오프셋 · 손 모양 · 감정.</summary>
public struct GestureFrame
{
    public Vector3 LeftTarget, RightTarget;
    public float LeftWeight, RightWeight, SpineFlex, SpineTwist, HeadPitch, HeadYaw, HeadRoll, Shrug;
    public float WristTwistL, WristTwistR, ElbowHyper, Valence, Arousal, Intensity;
    public HandShape LeftShape, RightShape;
    public GestureClass Class;
    public int Token;
    public bool Speaking, Beat, Hallucinated;
}

public delegate void GestureFrameHandler(in GestureFrame frame);

/// <summary>
/// [PromptFlow Studio v17.60.0] 🗣 실시간 LLM 텍스트-투-제스처 트랜스포머(<see cref="Tick"/> 무할당 · 60 fps).
/// 대사(v17.40 각본 대사 토큰 · 내장 문장) → 고정 문자 링(4 096) · 토큰 링(256) 어절 분절 + 음절 수 기반 발화 시각 · 계획 운율(하강 억양 · 강조 상승 · 의문 상승 · 감탄 에너지).
/// 토큰 온셋마다 16 차원 특징(지시 자기/상대 · 포괄 · 거절 · 강조 · 의문 · 감탄 · 긍/부정 유인가 · 망설임 · 확장 · 피치 · 에너지 · 위치 · 길이)에 대해
/// 단일 헤드 셀프 어텐션(±6 토큰 창 · 상대 위치 바이어스 · 핵심어/피치 현저성 키) + 잔차 → 8 제스처 분류 소프트맥스 · 유인가(tanh) · 각성도(시그모이드) 헤드를 링 내부에서 추론(가중치 = 해석 가능한 수작업 고정 행렬 · 학습 모델 아님).
/// 실시간 운율(<see cref="PushProsody"/> · v17.32 더빙 F0/RMS) 의 에너지/피치 피크 → 비트 수동작 트리거. 매 프레임 손 목표 · 척추 굽힘 · ARKit 52 오프셋을 <see cref="OnFrame"/> 로 방출.
/// </summary>
public sealed class RealtimeLlmTextToGestureTransformer
{
    public const int D = 16, Classes = 8, CharRing = 4096, TokenRing = 256, Window = 6, K = 52;
    public const float SyllableS = 0.17f, TokenGapS = 0.06f, LineGapS = 0.7f, BeatCooldownS = 0.28f;
    private static readonly float[] Wc = BuildClassHead();
    private static readonly float[] Wv = { 0f, 0.1f, 0f, 0.3f, -0.9f, 0.2f, 0f, 0.25f, 1.6f, -1.7f, -0.2f, 0.4f, 0.1f, 0f, 0f, 0f };
    private static readonly float[] Wa = { -1.1f, 0.2f, 0.4f, 0.6f, 0.9f, 1.4f, 0.3f, 1.6f, 0.4f, 0.6f, -0.9f, 0.6f, 1.2f, 1.3f, 0f, 0.1f };
    private static readonly float[] Sal = { 0f, 1.2f, 1.2f, 1.1f, 1.3f, 1.0f, 0.4f, 0.6f, 0.5f, 0.5f, 0.7f, 0.5f, 0.8f, 0.4f, 0f, 0f };

    private readonly char[] _chars = new char[CharRing];
    private readonly GestureToken[] _tok = new GestureToken[TokenRing];
    private readonly float[] _feat = new float[TokenRing * D];
    private readonly float[] _face = new float[K];
    private long _charHead, _tokHead, _tokTail;
    private long _cur = -1;
    private double _clock, _schedEnd, _lastBeat = -10;
    private float _eMean = 0.05f, _eVar = 0.001f, _fMean = 140f, _liveE, _liveF0, _lastE;
    private int _lines;
    private GestureFrame _frame;
    private Vector3 _lT, _rT, _lV, _rV;
    private float _spine, _spineV, _beatEnv;
    private float _glitchT;

    public RealtimeLlmTextToGestureTransformer() { Persona = ActingPersona.TedSpeaker; }

    public event GestureFrameHandler? OnFrame;
    public ActingPersona Persona { get; set; }
    public PersonaSpec Spec => PersonaSpec.Of(Persona);
    /// <summary>LLM 환각 주입 — 몸통 관통 목표 · 손목 180° · 고개 180°(엑소시스트) · 팔꿈치 과신전 · 척추 90° 비틀기.</summary>
    public bool Hallucinate { get; set; }
    public ref readonly GestureFrame Current => ref _frame;
    public ReadOnlySpan<float> FaceOffsets => _face;
    public double Clock => _clock;
    public double ScheduleEndS => _schedEnd;
    public int Pending => (int)(_tokTail - _tokHead);
    public long TokensTotal => _tokTail;
    public long Inferences { get; private set; }
    public long Beats { get; private set; }
    public long Dropped { get; private set; }
    public long Hallucinations { get; private set; }
    public int Lines => _lines;
    public double LastInferUs { get; private set; }
    public double MaxInferUs { get; private set; }
    public long CurrentIndex => _cur;
    public bool Speaking => _cur >= 0 && _clock < _tok[(int)(_cur % TokenRing)].OnsetS + _tok[(int)(_cur % TokenRing)].DurS;
    public ref readonly GestureToken TokenAt(long index) => ref _tok[(int)(index % TokenRing)];
    public long FirstIndex => Math.Max(0, _tokTail - TokenRing);
    public ReadOnlySpan<char> TokenText(long index)
    {
        ref readonly var t = ref _tok[(int)(index % TokenRing)];
        var s = t.CharStart % CharRing;
        return s + t.CharLen <= CharRing ? _chars.AsSpan(s, t.CharLen) : ReadOnlySpan<char>.Empty;
    }
    public float Feature(long index, int d) => _feat[(int)(index % TokenRing) * D + d];

    public static string ClassName(GestureClass c) => c switch
    {
        GestureClass.Beat => "🥁 비트", GestureClass.DeicticSelf => "🙋 자기 지시", GestureClass.DeicticOther => "👉 상대 지시",
        GestureClass.Inclusive => "🤗 포괄(팔 벌림)", GestureClass.Refuse => "✋ 거절", GestureClass.Emphasis => "✊ 강조",
        GestureClass.Think => "🤔 사고", _ => "· 휴지",
    };

    private static float[] BuildClassHead()
    {
        var w = new float[Classes * D];
        void S(GestureClass c, int d, float v) => w[(int)c * D + d] = v;
        S(GestureClass.Rest, 0, 0.6f); S(GestureClass.Rest, 12, -0.8f); S(GestureClass.Rest, 13, -0.8f);
        S(GestureClass.Beat, 0, 0.2f); S(GestureClass.Beat, 12, 1.2f); S(GestureClass.Beat, 13, 1.2f); S(GestureClass.Beat, 15, 0.3f);
        S(GestureClass.DeicticSelf, 1, 3.2f); S(GestureClass.DeicticSelf, 0, -0.6f);
        S(GestureClass.DeicticOther, 2, 3.2f); S(GestureClass.DeicticOther, 6, 0.6f); S(GestureClass.DeicticOther, 0, -0.6f);
        S(GestureClass.Inclusive, 3, 3.2f); S(GestureClass.Inclusive, 11, 1.4f); S(GestureClass.Inclusive, 8, 0.4f); S(GestureClass.Inclusive, 0, -0.6f);
        S(GestureClass.Refuse, 4, 3.4f); S(GestureClass.Refuse, 9, 0.6f); S(GestureClass.Refuse, 0, -0.6f);
        S(GestureClass.Emphasis, 5, 3.0f); S(GestureClass.Emphasis, 7, 1.2f); S(GestureClass.Emphasis, 13, 0.6f); S(GestureClass.Emphasis, 0, -0.7f);
        S(GestureClass.Think, 10, 3.2f); S(GestureClass.Think, 6, 0.5f); S(GestureClass.Think, 0, -0.6f);
        return w;
    }

    // ══════════════════════ 입력(비핫패스 허용 · 무할당) ══════════════════════
    public void Reset()
    {
        _charHead = _tokHead = _tokTail = 0; _cur = -1; _clock = 0; _schedEnd = 0; _lastBeat = -10; _lines = 0;
        _frame = default; _lT = _rT = _lV = _rV = Vector3.Zero; _spine = _spineV = 0f; _beatEnv = 0f;
        Inferences = Beats = Dropped = Hallucinations = 0; MaxInferUs = 0;
        Array.Clear(_face);
    }

    /// <summary>v17.61 선점 — 미발화 예약 토큰 폐기 · 일정 끝을 현재 시각으로(다음 Enqueue 가 0.1 s 뒤 시작 · 무할당).</summary>
    public void Interrupt()
    {
        var keep = Math.Max(_tokHead, _cur + 1);
        if (_tokTail > keep) _tokTail = keep;
        _schedEnd = _clock;
    }

    /// <summary>대사 한 줄 → 어절 토큰 링 · 발화 시각 · 계획 운율(무할당).</summary>
    public int Enqueue(ReadOnlySpan<char> line)
    {
        var start = Math.Max(_schedEnd, _clock + 0.1);
        var t = start; var added = 0; var n = line.Length;
        // 의문/감탄(문장 끝) — 계획 운율 상승
        var question = line.IndexOf('?') >= 0; var exclaim = line.IndexOf('!') >= 0;
        var count = 0; for (var i = 0; i < n;) { while (i < n && char.IsWhiteSpace(line[i])) i++; if (i >= n) break; while (i < n && !char.IsWhiteSpace(line[i])) i++; count++; }
        var k = 0;
        for (var i = 0; i < n;)
        {
            while (i < n && char.IsWhiteSpace(line[i])) i++;
            if (i >= n) break;
            var s = i; while (i < n && !char.IsWhiteSpace(line[i])) i++;
            var w = line[s..i];
            if (_tokTail - _tokHead >= TokenRing) { _tokHead++; Dropped++; }
            if (w.Length > CharRing / 4) w = w[..(CharRing / 4)];
            var cs = (int)(_charHead % CharRing);
            if (cs + w.Length > CharRing) { _charHead += CharRing - cs; cs = 0; }
            w.CopyTo(_chars.AsSpan(cs));
            ref var tk = ref _tok[(int)(_tokTail % TokenRing)];
            tk = default;
            tk.CharStart = (int)(_charHead % CharRing); tk.CharLen = w.Length; tk.Line = _lines;
            _charHead += w.Length;
            var syl = 0; foreach (var c in w) if (char.IsLetterOrDigit(c)) syl++;
            tk.OnsetS = t; tk.DurS = MathF.Max(1, syl) * SyllableS;
            var pos = count > 1 ? k / (float)(count - 1) : 0f;
            var emph = Lexicon(w, out _);
            tk.PlanPitch = 0.4f - 0.5f * pos + 0.9f * emph + (k == count - 1 && question ? 0.9f : 0f);
            tk.PlanEnergy = 0.5f + 0.6f * emph + (exclaim ? 0.35f : 0f) - 0.2f * pos;
            tk.LineEnd = k == count - 1;
            t += tk.DurS + TokenGapS;
            _tokTail++; added++; k++;
        }
        _schedEnd = t + LineGapS; _lines++;
        return added;
    }

    /// <summary>v17.40 각본 시퀀서의 대사(Dialogue) 토큰 전량 적재.</summary>
    public int EnqueueScript(RealtimeScriptToScreenTimelineSequencerEngine seq)
    {
        var n = 0;
        for (var i = 0; i < seq.TokenCount; i++) if (seq.Token(i).Kind == ScriptTokenKind.Dialogue) { Enqueue(seq.Text(i)); n++; }
        return n;
    }

    /// <summary>실시간 운율 표본(F0 Hz · RMS) — v17.32 더빙 레인 또는 외부 TTS.</summary>
    public void PushProsody(float f0Hz, float energy)
    {
        _liveF0 = float.IsFinite(f0Hz) ? f0Hz : 0f;
        _liveE = float.IsFinite(energy) ? MathF.Max(energy, 0f) : 0f;
    }

    /// <summary>핵심어 사전 강조도(0..1) · 특징 비트 마스크.</summary>
    public static float Lexicon(ReadOnlySpan<char> w, out int mask)
    {
        mask = 0;
        if (Starts(w, "나는") || Starts(w, "나도") || Starts(w, "나를") || Starts(w, "내가") || Starts(w, "저는") || Starts(w, "제가") || Starts(w, "저도") || Starts(w, "저를") || Starts(w, "저의")) mask |= 1 << 1;
        if (Has(w, "너") || Has(w, "당신") || Has(w, "그대") || Has(w, "네가") || Has(w, "여기")) mask |= 1 << 2;
        if (Has(w, "모두") || Has(w, "우리") || Has(w, "여러분") || Has(w, "함께") || Has(w, "다같이")) mask |= 1 << 3;
        if (Has(w, "거절") || Has(w, "아니") || Has(w, "싫") || Has(w, "안돼") || Has(w, "반대") || Has(w, "그만") || Has(w, "못")) mask |= 1 << 4;
        if (Has(w, "정말") || Has(w, "절대") || Has(w, "반드시") || Has(w, "꼭") || Has(w, "진짜") || Has(w, "가장") || Has(w, "매우")) mask |= 1 << 5;
        if (w.IndexOf('?') >= 0) mask |= 1 << 6;
        if (w.IndexOf('!') >= 0) mask |= 1 << 7;
        if (Has(w, "좋") || Has(w, "행복") || Has(w, "기쁘") || Has(w, "감사") || Has(w, "사랑") || Has(w, "멋") || Has(w, "놀라운") || Has(w, "최고") || Has(w, "희망")) mask |= 1 << 8;
        if (Has(w, "슬프") || Has(w, "화가") || Has(w, "무섭") || Has(w, "나쁘") || Has(w, "걱정") || Has(w, "실패") || Has(w, "두렵")) mask |= 1 << 9;
        if (Has(w, "음") || Has(w, "글쎄") || Has(w, "아마") || Has(w, "생각") || Has(w, "모르")) mask |= 1 << 10;
        if (Has(w, "크") || Has(w, "넓") || Has(w, "모든") || Has(w, "전부") || Has(w, "세계") || Has(w, "미래") || Has(w, "세상")) mask |= 1 << 11;
        var e = 0f;
        if ((mask & (1 << 5)) != 0) e += 0.8f;
        if ((mask & (1 << 7)) != 0) e += 0.4f;
        if ((mask & ((1 << 1) | (1 << 2) | (1 << 3) | (1 << 4))) != 0) e += 0.35f;
        return MathF.Min(e, 1f);
    }

    private static bool Has(ReadOnlySpan<char> w, string k) => w.IndexOf(k.AsSpan(), StringComparison.Ordinal) >= 0;
    private static bool Starts(ReadOnlySpan<char> w, string k) => w.StartsWith(k.AsSpan(), StringComparison.Ordinal);

    // ══════════════════════ 프레임(무할당) ══════════════════════
    public void Tick(float dt)
    {
        if (!(dt > 0f) || !float.IsFinite(dt)) return;
        _clock += dt;
        // 온셋 도래 토큰 → 추론
        while (_cur + 1 < _tokTail && _tok[(int)((_cur + 1) % TokenRing)].OnsetS <= _clock)
        {
            _cur++;
            if (_cur < _tokHead) _cur = _tokHead;
            Infer(_cur);
        }
        while (_tokHead < _tokTail && _tokHead < _cur - TokenRing / 2) _tokHead++;
        Compose(dt);
        OnFrame?.Invoke(in _frame);
    }

    private void Features(long idx, Span<float> x)
    {
        ref readonly var t = ref _tok[(int)(idx % TokenRing)];
        var w = TokenText(idx);
        Lexicon(w, out var m);
        x.Clear();
        x[0] = 1f;
        for (var d = 1; d <= 11; d++) if ((m & (1 << d)) != 0) x[d] = 1f;
        x[12] = Math.Clamp(t.PlanPitch, -1f, 2f);
        x[13] = Math.Clamp(t.PlanEnergy, 0f, 2f);
        var lineStart = idx; while (lineStart > _tokHead && lineStart > idx - 32 && _tok[(int)((lineStart - 1) % TokenRing)].Line == t.Line) lineStart--;
        x[14] = MathF.Min((idx - lineStart) / 8f, 1f);
        x[15] = MathF.Min(w.Length / 6f, 1f);
    }

    /// <summary>단일 헤드 셀프 어텐션(±창 · 링 내부) → 분류/유인가/각성도(무할당 · stackalloc).</summary>
    private void Infer(long idx)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        long lo = Math.Max(_tokHead, idx - Window), hi = Math.Min(_tokTail - 1, idx + Window);
        var n = (int)(hi - lo + 1);
        Span<float> xs = stackalloc float[(2 * Window + 1) * D];
        Span<float> sc = stackalloc float[2 * Window + 1];
        for (var j = 0; j < n; j++)
        {
            Features(lo + j, xs.Slice(j * D, D));
            xs.Slice(j * D, D).CopyTo(_feat.AsSpan((int)((lo + j) % TokenRing) * D, D));
        }
        var qi = (int)(idx - lo);
        var q = xs.Slice(qi * D, D);
        var line = _tok[(int)(idx % TokenRing)].Line;
        // 질의 = 현재 토큰 의미 특징 · 키 = 현저성 가중 특징 · 상대 위치 바이어스 · 다른 줄 마스크
        var max = float.NegativeInfinity;
        for (var j = 0; j < n; j++)
        {
            var k = xs.Slice(j * D, D);
            var s = 0f;
            for (var d = 0; d < D; d++) s += (0.3f + q[d]) * Sal[d] * k[d];
            s = s * 0.25f - 0.35f * MathF.Abs(j - qi) + (j == qi ? 1.5f : 0f);
            if (_tok[(int)((lo + j) % TokenRing)].Line != line) s -= 8f;
            sc[j] = s; if (s > max) max = s;
        }
        var sum = 0f;
        for (var j = 0; j < n; j++) { sc[j] = MathF.Exp(sc[j] - max); sum += sc[j]; }
        Span<float> h = stackalloc float[D];
        for (var d = 0; d < D; d++)
        {
            var c = 0f;
            for (var j = 0; j < n; j++) c += sc[j] * xs[j * D + d];
            h[d] = q[d] + 0.5f * c / sum;
        }
        // 헤드
        var temp = Spec.Temperature;
        Span<float> lg = stackalloc float[Classes];
        var lm = float.NegativeInfinity; var arg = 0;
        for (var c = 0; c < Classes; c++)
        {
            var s = 0f;
            for (var d = 0; d < D; d++) s += Wc[c * D + d] * h[d];
            lg[c] = s / temp; if (lg[c] > lm) { lm = lg[c]; arg = c; }
        }
        var ls = 0f; for (var c = 0; c < Classes; c++) ls += MathF.Exp(lg[c] - lm);
        float v = 0f, a = 0f;
        for (var d = 0; d < D; d++) { v += Wv[d] * h[d]; a += Wa[d] * h[d]; }
        ref var tk = ref _tok[(int)(idx % TokenRing)];
        tk.Class = (GestureClass)arg; tk.Confidence = 1f / ls;
        tk.Valence = MathF.Tanh(v); tk.Arousal = 1f / (1f + MathF.Exp(-a));
        tk.Inferred = true;
        Inferences++;
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        LastInferUs = us; if (us > MaxInferUs) MaxInferUs = us;
    }

    /// <summary>현재 토큰 분류 → 제스처 단계(준비 · 스트로크 · 유지 · 복귀) 손 목표 · 스프링 평활 · 비트 · 표정 오프셋.</summary>
    private void Compose(float dt)
    {
        var p = Spec;
        ref var f = ref _frame;
        var speaking = Speaking;
        GestureClass cls = GestureClass.Rest; float inten = 0f, val = 0f, aro = 0.3f; var ph = 1f;
        if (_cur >= 0)
        {
            ref readonly var tk = ref _tok[(int)(_cur % TokenRing)];
            ph = (float)((_clock - tk.OnsetS) / MathF.Max(tk.DurS, 1e-3f));
            val = tk.Valence; aro = tk.Arousal;
            if (speaking || ph < 1.6f) { cls = tk.Class; inten = MathF.Min(1f, 0.55f + 0.6f * tk.Arousal) * (speaking ? 1f : MathF.Max(0f, 1f - (ph - 1f) / 0.6f)); }
        }
        // 실시간 운율 비트(에너지 z ≥ 1.2 상승 에지 · 쿨다운)
        var e = _liveE > 0f ? _liveE : speaking && _cur >= 0 ? _tok[(int)(_cur % TokenRing)].PlanEnergy * (0.6f + 0.4f * MathF.Sin(MathF.PI * Math.Clamp(ph, 0f, 1f))) * 0.1f : 0f;
        var a = 1f - MathF.Exp(-dt / 1.5f);
        var dv = e - _eMean; _eMean += dv * a; _eVar += (dv * dv - _eVar) * a;
        if (_liveF0 > 0f) _fMean += (_liveF0 - _fMean) * a;
        var z = dv / MathF.Sqrt(MathF.Max(_eVar, 1e-8f));
        var pitchUp = _liveF0 <= 0f || _liveF0 > _fMean;
        var beat = speaking && z > 1.2f / p.BeatGain && e > _lastE && pitchUp && _clock - _lastBeat > BeatCooldownS;
        _lastE = e;
        if (beat) { _lastBeat = _clock; Beats++; _beatEnv = 1f; }
        _beatEnv = MathF.Max(0f, _beatEnv - dt / 0.22f);
        // 목표(가슴 로컬)
        var s = p.Space;
        Vector3 lt = new(-0.22f, -0.50f, 0.10f), rt = new(0.22f, -0.50f, 0.10f);
        HandShape ls = HandShape.Relaxed, rs = HandShape.Relaxed;
        float spine = 0f, yaw = 0f, pitch = 0f, roll = 0f, shrug = 0f, lw = 0.25f, rw = 0.25f;
        var stroke = MathF.Sin(MathF.PI * Math.Clamp(ph * 1.4f, 0f, 1f) * 0.5f);
        switch (cls)
        {
            case GestureClass.Beat:
                rt = new Vector3(0.20f, -0.26f, 0.32f) * s; rs = HandShape.Open; rw = 0.9f; lw = 0.4f; break;
            case GestureClass.DeicticSelf:
                rt = new Vector3(0.04f, -0.05f, 0.14f); rs = HandShape.Palm; rw = 1f; spine = -0.05f; pitch = 0.1f; break;
            case GestureClass.DeicticOther:
                rt = new Vector3(0.22f, -0.04f, 0.50f) * s; rs = HandShape.Point; rw = 1f; spine = 0.08f; pitch = -0.05f; break;
            case GestureClass.Inclusive:
                lt = new Vector3(-0.46f, -0.12f, 0.30f) * s; rt = new Vector3(0.46f, -0.12f, 0.30f) * s; ls = rs = HandShape.Open; lw = rw = 1f; spine = -0.06f; pitch = -0.08f; break;
            case GestureClass.Refuse:
                rt = new Vector3(0.10f, -0.06f, 0.46f) * s; rs = HandShape.Palm; rw = 1f; spine = -0.15f; yaw = 0.35f * MathF.Sin((float)_clock * 14f) * stroke; break;
            case GestureClass.Emphasis:
                lt = new Vector3(-0.20f, -0.16f, 0.36f) * s; rt = new Vector3(0.20f, -0.16f, 0.36f) * s; ls = rs = HandShape.Fist; lw = rw = 1f; spine = 0.22f; pitch = 0.18f * stroke; break;
            case GestureClass.Think:
                rt = new Vector3(0.05f, 0.17f, 0.13f); rs = HandShape.Fist; rw = 1f; lt = new Vector3(-0.10f, -0.22f, 0.16f); lw = 0.7f; pitch = -0.12f; roll = 0.12f; shrug = 0.3f; break;
        }
        var w = inten * stroke;
        lt = Vector3.Lerp(new Vector3(-0.22f, -0.50f, 0.10f), lt, w); rt = Vector3.Lerp(new Vector3(0.22f, -0.50f, 0.10f), rt, w);
        rt.Y -= 0.07f * _beatEnv * p.BeatGain; rt.Z += 0.02f * _beatEnv;
        pitch += 0.08f * _beatEnv * p.BeatGain;
        Spring(ref _lT, ref _lV, lt, p.HalfLife, dt); Spring(ref _rT, ref _rV, rt, p.HalfLife, dt);
        Spring1(ref _spine, ref _spineV, spine * w * p.SpineGain * (0.6f + 0.6f * aro), p.HalfLife * 1.5f, dt);
        f.LeftTarget = _lT; f.RightTarget = _rT;
        f.LeftWeight = 0.15f + (lw - 0.15f) * w; f.RightWeight = MathF.Max(0.15f + (rw - 0.15f) * w, 0.6f * _beatEnv);
        f.SpineFlex = _spine; f.SpineTwist = 0.12f * (rt.X + lt.X) * w;
        f.HeadPitch = pitch * MathF.Max(w, _beatEnv); f.HeadYaw = yaw; f.HeadRoll = roll * w; f.Shrug = shrug * w;
        f.WristTwistL = f.WristTwistR = 0f; f.ElbowHyper = 0f;
        f.LeftShape = w > 0.3f ? ls : HandShape.Relaxed; f.RightShape = w > 0.3f || _beatEnv > 0.3f ? (rs == HandShape.Relaxed ? HandShape.Open : rs) : HandShape.Relaxed;
        f.Valence = val; f.Arousal = aro; f.Intensity = w; f.Class = cls; f.Token = (int)_cur; f.Speaking = speaking; f.Beat = beat; f.Hallucinated = false;
        // LLM 환각(1.5 s 주기 0.5 s)
        if (Hallucinate)
        {
            _glitchT += dt;
            if (_glitchT % 1.5f < 0.5f)
            {
                var k = (int)(_glitchT / 1.5f) % 4;
                if (k == 0) { f.HeadYaw = MathF.PI; f.HeadPitch = -1.6f; }
                else if (k == 1) { f.WristTwistR = MathF.PI; f.WristTwistL = -MathF.PI; f.ElbowHyper = 0.9f; }
                else if (k == 2) { f.RightTarget = new Vector3(-0.15f, -0.05f, -0.45f); f.LeftTarget = new Vector3(0.6f, 0.9f, -0.5f); f.RightWeight = f.LeftWeight = 1f; f.SpineTwist = 1.6f; }
                else { f.SpineFlex = 1.8f; f.HeadRoll = 2.4f; f.ElbowHyper = 1.2f; f.Shrug = 3f; }
                f.Hallucinated = true; Hallucinations++;
            }
        }
        else _glitchT = 0f;
        WriteFace(in f, p.FaceGain);
    }

    private static void Spring(ref Vector3 x, ref Vector3 v, Vector3 g, float hl, float dt)
    {
        var y = 1.3862944f / (hl + 1e-5f);
        var j0 = x - g; var j1 = v + j0 * y; var ex = MathF.Exp(-y * dt);
        x = ex * (j0 + j1 * dt) + g; v = ex * (v - j1 * y * dt);
    }

    private static void Spring1(ref float x, ref float v, float g, float hl, float dt)
    {
        var y = 1.3862944f / (hl + 1e-5f);
        var j0 = x - g; var j1 = v + j0 * y; var ex = MathF.Exp(-y * dt);
        x = ex * (j0 + j1 * dt) + g; v = ex * (v - j1 * y * dt);
    }

    /// <summary>유인가/각성도/제스처 → ARKit 52 기저 오프셋(0..1).</summary>
    private void WriteFace(in GestureFrame f, float gain)
    {
        var fc = _face; Array.Clear(fc);
        float pos = MathF.Max(f.Valence, 0f) * gain, neg = MathF.Max(-f.Valence, 0f) * gain, ar = Math.Clamp((f.Arousal - 0.4f) * 1.6f, 0f, 1f) * gain;
        void S(ArkitBlendshape k, float v) { ref var x = ref fc[(int)k]; x = Math.Clamp(MathF.Max(x, v), 0f, 1f); }
        S(ArkitBlendshape.MouthSmileLeft, 0.7f * pos); S(ArkitBlendshape.MouthSmileRight, 0.7f * pos);
        S(ArkitBlendshape.CheekSquintLeft, 0.4f * pos); S(ArkitBlendshape.CheekSquintRight, 0.4f * pos);
        S(ArkitBlendshape.MouthFrownLeft, 0.6f * neg); S(ArkitBlendshape.MouthFrownRight, 0.6f * neg);
        S(ArkitBlendshape.BrowDownLeft, 0.5f * neg); S(ArkitBlendshape.BrowDownRight, 0.5f * neg);
        S(ArkitBlendshape.BrowInnerUp, 0.5f * ar); S(ArkitBlendshape.EyeWideLeft, 0.4f * ar); S(ArkitBlendshape.EyeWideRight, 0.4f * ar);
        switch (f.Class)
        {
            case GestureClass.Refuse: S(ArkitBlendshape.BrowDownLeft, 0.6f * f.Intensity); S(ArkitBlendshape.BrowDownRight, 0.6f * f.Intensity); S(ArkitBlendshape.MouthPressLeft, 0.5f * f.Intensity); S(ArkitBlendshape.MouthPressRight, 0.5f * f.Intensity); break;
            case GestureClass.Think: S(ArkitBlendshape.BrowInnerUp, 0.6f * f.Intensity); S(ArkitBlendshape.EyeLookUpLeft, 0.5f * f.Intensity); S(ArkitBlendshape.EyeLookUpRight, 0.5f * f.Intensity); S(ArkitBlendshape.MouthPucker, 0.3f * f.Intensity); break;
            case GestureClass.Emphasis: S(ArkitBlendshape.BrowOuterUpLeft, 0.5f * f.Intensity); S(ArkitBlendshape.BrowOuterUpRight, 0.5f * f.Intensity); break;
            case GestureClass.Inclusive: S(ArkitBlendshape.MouthSmileLeft, 0.4f * f.Intensity); S(ArkitBlendshape.MouthSmileRight, 0.4f * f.Intensity); break;
        }
    }
}
