using PromptFlowStudio.Services.DigitalHuman;

namespace PromptFlowStudio.Services.NeuralVoice;

public enum Phone : byte { Rest = 0, Pause, A, E, I, O, U, M, N, P, B, T, F, S, H }

public enum SpeakerPreset : byte { Male = 0, Female, Child }

/// <summary>감정/생리 벡터(v17.17 혈류 합성기: 분노 · 수줍음 · 긴장 · 심박 + 정서가 → 기쁨).</summary>
public readonly record struct VoiceEmotion(float Anger, float Joy, float Tension, float Shyness, float Bpm);

/// <summary>음소 타임라인 이벤트(합성 샘플 시각 · 음소 · 파열 버스트 시각).</summary>
public readonly record struct PhoneEvent(long Sample, Phone Phone, long BurstSample);

/// <summary>
/// [PromptFlow Studio v17.18.0] 🎙️ 실시간 감정 적응형 음성 합성 &amp; 음색 모핑 엔진(<see cref="RenderChunk"/> 무할당).
///   ▸ 텍스트 → 음소(G2P · 한글 초/중/종성 분해 · 라틴 철자 규칙 · 문장부호 = 휴지 + 들숨) · 최대 512 음소(사전 할당 · 문자 순회 무할당).
///   ▸ 운율(절차 모델 · 학습 신경망 아님): 지속 = 기본 × 1/(1 + 0.35·분노 + 0.15·기쁨 + 0.25·긴장 − 0.15·수줍음) ·
///     F0 = 화자 × (1 + 0.35·분노 + 0.2·기쁨 + 0.15·긴장 + 0.05·수줍음) × 2^(음역·(강세 − 0.3·진행)/12) · 의문문 끝 상승 · 심박 주파수 미세 떨림 ·
///     폐 압력(에너지) · 개방률(분노 압착 ↓ · 수줍음 기식 ↑) · 지터(긴장 ↑) · 기식 잡음.
///   ▸ 음색 모핑: 화자 프리셋(남/여/아동: F0 · 성도 길이 구간 · 개방률 · 면적 배율) 1차 · 2차 보간 α.
///   ▸ 조음: 음소 → 성도 면적 함수(제어점 7 · 구간 보간) · 연구개 · 마찰/파열 위치 · 폐쇄 → 버스트 → 1 ms 제어율.
///   ▸ 스트리밍: 160 샘플(3.33 ms) 청크 × 성도 합성기 → 알고리즘 지연 = 청크 + 가드 룩어헤드(1 ms) &lt; 10 ms.
/// </summary>
public sealed class RealtimeNeuralVoiceSynthesisEngine
{
    public const int Fs = BiomechanicalVocalTractResonanceSynthesizer.Fs, Chunk = 160, MaxPhones = 512, Control = 48, ProsodyRing = 600, EventRing = 256;

    private static readonly float[][] Shapes = BuildShapes();
    private readonly Phone[] _phones = new Phone[MaxPhones];
    private readonly float[] _accent = new float[MaxPhones];
    private readonly PhoneEvent[] _events = new PhoneEvent[EventRing];
    private readonly float[] _f0Ring = new float[ProsodyRing], _enRing = new float[ProsodyRing];
    private readonly byte[] _phRing = new byte[ProsodyRing];
    private int _count, _cur, _evHead, _evCount, _prHead, _prCount;
    private long _sample, _phoneStart, _phoneLen, _burstAt;
    private bool _question, _finished = true;
    private float _f0Sm = 120f, _tremorPh;

    public RealtimeNeuralVoiceSynthesisEngine(BiomechanicalVocalTractResonanceSynthesizer tract)
    {
        Tract = tract;
        ApplyRest();
    }

    public BiomechanicalVocalTractResonanceSynthesizer Tract { get; }
    public VoiceEmotion Emotion { get; set; } = new(0f, 0.2f, 0f, 0f, 70f);
    public SpeakerPreset Primary { get; set; } = SpeakerPreset.Male;
    public SpeakerPreset Secondary { get; set; } = SpeakerPreset.Female;
    /// <summary>음색 모핑 α(0 = 1차 · 1 = 2차).</summary>
    public float Morph { get; set; }
    public float SpeedScale { get; set; } = 1f;
    public float PitchScale { get; set; } = 1f;
    public bool Loop { get; set; } = true;
    /// <summary>파열 버스트 잡음 이득.</summary>
    public float BurstGain { get; set; } = 0.07f;
    public int PhoneCount => _count;
    public int CurrentIndex => _cur;
    public Phone Current => _count > 0 && _cur < _count ? _phones[_cur] : Phone.Rest;
    public Phone PhoneAt(int i) => _phones[i];
    public bool Finished => _finished;
    public long SampleClock => _sample;
    public float CurrentF0 { get; private set; }
    public float CurrentEnergy { get; private set; }
    public float RateFactor { get; private set; } = 1f;
    public float BaseF0 { get; private set; }
    public double LastChunkUs { get; private set; }
    public double MaxChunkUs { get; private set; }
    public float ChunkMs => 1000f * Chunk / Fs;
    public long LinesSpoken { get; private set; }
    public int ProsodyCount => _prCount;
    private int Pr(int back) => (_prHead - 1 - back + 2 * ProsodyRing) % ProsodyRing;
    public float F0Sample(int back) => _f0Ring[Pr(back)];
    public float EnergySample(int back) => _enRing[Pr(back)];
    public Phone PhoneSample(int back) => (Phone)_phRing[Pr(back)];
    public int EventCount => _evCount;
    public PhoneEvent Event(int back) => _events[(_evHead - 1 - back + 2 * EventRing) % EventRing];
    /// <summary>음소 이벤트 수신(가드 · 무할당 대리자 아님 — 폴링).</summary>
    public long EventsTotal { get; private set; }

    public static Viseme VisemeOf(Phone p) => p switch
    {
        Phone.A => Viseme.A, Phone.E => Viseme.E, Phone.I => Viseme.I, Phone.O => Viseme.O, Phone.U => Viseme.U,
        Phone.M or Phone.P or Phone.B => Viseme.M, Phone.F => Viseme.F, Phone.N or Phone.T or Phone.S => Viseme.E, _ => Viseme.Rest,
    };

    public static string Name(Phone p) => p switch { Phone.Pause => "‖", Phone.Rest => "·", _ => p.ToString() };

    // ══════════════════════ G2P(제어 평면 · 무할당 순회) ══════════════════════

    private void Add(Phone p, float accent = 0f)
    {
        if (_count >= MaxPhones) return;
        _phones[_count] = p; _accent[_count] = accent; _count++;
    }

    /// <summary>대사 설정(문자 순회 · 할당 없음) → 처음부터 발화.</summary>
    public void SetText(ReadOnlySpan<char> text)
    {
        _count = 0; _question = false;
        Add(Phone.Rest);
        var vowelIndex = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c >= '가' && c <= '힣')
            {
                var s = c - 0xAC00; int cho = s / (21 * 28), jung = s / 28 % 21, jong = s % 28;
                var ini = Choseong(cho);
                if (ini != Phone.Rest) Add(ini);
                Add(Jungseong(jung), vowelIndex++ == 1 ? 1f : 0.3f);
                var fin = Jongseong(jong);
                if (fin != Phone.Rest) Add(fin);
                continue;
            }
            var l = char.ToLowerInvariant(c);
            var p = l switch
            {
                'a' => Phone.A, 'e' => Phone.E, 'i' or 'y' => Phone.I, 'o' => Phone.O, 'u' or 'w' => Phone.U,
                'm' => Phone.M, 'n' or 'l' or 'r' => Phone.N, 'p' => Phone.P, 'b' => Phone.B,
                't' or 'd' or 'k' or 'g' or 'c' or 'q' => Phone.T, 'f' or 'v' => Phone.F, 's' or 'z' or 'x' or 'j' => Phone.S, 'h' => Phone.H,
                ' ' => Phone.Rest, '.' or ',' or '!' or '?' or ';' or ':' or '…' => Phone.Pause, _ => (Phone)255,
            };
            if ((byte)p == 255) continue;
            if (l == '?') _question = true;
            if (p is Phone.A or Phone.E or Phone.I or Phone.O or Phone.U) { Add(p, vowelIndex++ == 1 ? 1f : 0.3f); continue; }
            if (p == Phone.Rest && _count > 0 && _phones[_count - 1] is Phone.Rest or Phone.Pause) continue;
            Add(p);
        }
        Add(Phone.Pause);
        _cur = 0; _finished = _count == 0;
        BeginPhone();
    }

    private static Phone Choseong(int c) => c switch
    {
        0 or 1 or 15 => Phone.T, 2 => Phone.N, 3 or 4 or 16 => Phone.T, 5 => Phone.N, 6 => Phone.M, 7 => Phone.B, 8 or 17 => Phone.P,
        9 or 10 or 12 or 13 or 14 => Phone.S, 18 => Phone.H, _ => Phone.Rest,
    };

    private static Phone Jungseong(int j) => j switch
    {
        0 or 2 or 9 => Phone.A, 1 or 3 or 5 or 7 or 10 or 11 or 15 => Phone.E, 4 or 6 or 8 or 12 or 14 => Phone.O,
        13 or 17 or 18 => Phone.U, _ => Phone.I,
    };

    private static Phone Jongseong(int j) => j switch { 4 or 8 or 21 => Phone.N, 16 => Phone.M, 1 or 7 or 19 or 20 => Phone.T, 17 => Phone.P, _ => Phone.Rest };

    // ══════════════════════ 조음 형상 ══════════════════════

    private static float[] Shape(params float[] pts) => pts;

    /// <summary>음소 면적 제어점(위치 0..1 → 면적 cm² 쌍 · 성문 → 입술).</summary>
    private static float[][] BuildShapes()
    {
        var s = new float[15][];
        var neutral = Shape(0f, 2f, 0.3f, 3f, 0.7f, 3f, 1f, 3f);
        s[(int)Phone.Rest] = neutral; s[(int)Phone.Pause] = neutral; s[(int)Phone.H] = neutral;
        s[(int)Phone.A] = Shape(0f, 1.6f, 0.2f, 0.8f, 0.4f, 0.9f, 0.55f, 3f, 0.7f, 6f, 0.9f, 7f, 1f, 5f);
        s[(int)Phone.I] = Shape(0f, 2f, 0.15f, 6f, 0.4f, 8f, 0.6f, 3f, 0.75f, 0.4f, 0.88f, 0.6f, 1f, 2.5f);
        s[(int)Phone.U] = Shape(0f, 2f, 0.2f, 5f, 0.45f, 3f, 0.62f, 0.7f, 0.78f, 3f, 0.92f, 4f, 1f, 0.35f);
        s[(int)Phone.E] = Shape(0f, 2f, 0.2f, 4f, 0.45f, 5f, 0.7f, 1.6f, 0.85f, 2f, 1f, 3.5f);
        s[(int)Phone.O] = Shape(0f, 2f, 0.2f, 1.5f, 0.45f, 2f, 0.62f, 3f, 0.8f, 5f, 0.95f, 2f, 1f, 1.1f);
        s[(int)Phone.M] = Shape(0f, 2f, 0.4f, 3f, 0.8f, 3f, 0.9f, 2f, 0.94f, 0.0004f, 1f, 0.0004f);
        s[(int)Phone.P] = s[(int)Phone.M]; s[(int)Phone.B] = s[(int)Phone.M];
        s[(int)Phone.N] = Shape(0f, 2f, 0.4f, 3f, 0.78f, 2.5f, 0.83f, 0.0004f, 0.88f, 0.0004f, 0.93f, 2f, 1f, 3f);
        s[(int)Phone.T] = s[(int)Phone.N];
        s[(int)Phone.F] = Shape(0f, 2f, 0.4f, 3f, 0.8f, 3f, 0.92f, 2.5f, 0.96f, 0.12f, 1f, 0.12f);
        s[(int)Phone.S] = Shape(0f, 2f, 0.4f, 3f, 0.8f, 2.5f, 0.86f, 0.09f, 0.91f, 0.09f, 0.96f, 1.5f, 1f, 1.5f);
        return s;
    }

    /// <summary>제어점 → 구간 면적(선형 보간 · 화자 배율).</summary>
    public static void Fill(Phone p, Span<float> dst, int n, float scale)
    {
        var pts = Shapes[(int)p];
        for (var i = 0; i < n; i++)
        {
            var x = (i + 0.5f) / n;
            var a = pts[^1];
            for (var k = 2; k < pts.Length; k += 2)
                if (x <= pts[k]) { var t = (x - pts[k - 2]) / (pts[k] - pts[k - 2]); a = pts[k - 1] + (pts[k + 1] - pts[k - 1]) * t; break; }
            dst[i] = MathF.Max(0.0003f, a * (a < 0.05f ? 1f : scale));
        }
    }

    // ══════════════════════ 화자 · 운율 ══════════════════════

    private static void Preset(SpeakerPreset p, out float f0, out float sections, out float oq, out float areaScale)
    {
        switch (p)
        {
            case SpeakerPreset.Female: f0 = 205f; sections = 41f; oq = 0.66f; areaScale = 0.8f; break;
            case SpeakerPreset.Child: f0 = 285f; sections = 33f; oq = 0.7f; areaScale = 0.55f; break;
            default: f0 = 115f; sections = 48f; oq = 0.58f; areaScale = 1f; break;
        }
    }

    private void Speaker(out float f0, out int sections, out float oq, out float areaScale)
    {
        Preset(Primary, out var f1, out var s1, out var o1, out var a1);
        Preset(Secondary, out var f2, out var s2, out var o2, out var a2);
        var m = Math.Clamp(Morph, 0f, 1f);
        f0 = f1 * MathF.Pow(f2 / f1, m); sections = (int)MathF.Round(s1 + (s2 - s1) * m); oq = o1 + (o2 - o1) * m; areaScale = a1 + (a2 - a1) * m;
    }

    private static float BaseMs(Phone p) => p switch
    {
        Phone.A or Phone.E or Phone.I or Phone.O or Phone.U => 115f, Phone.M or Phone.N => 70f, Phone.P or Phone.B or Phone.T => 85f,
        Phone.F or Phone.S => 95f, Phone.H => 60f, Phone.Pause => 260f, _ => 45f,
    };

    private void BeginPhone()
    {
        if (_cur >= _count) return;
        var e = Emotion;
        RateFactor = Math.Clamp(1f + 0.35f * e.Anger + 0.15f * e.Joy + 0.25f * e.Tension - 0.15f * e.Shyness, 0.6f, 1.8f) * MathF.Max(0.3f, SpeedScale);
        var p = _phones[_cur];
        _phoneLen = (long)(BaseMs(p) / RateFactor * Fs / 1000f);
        _phoneStart = _sample;
        _burstAt = p is Phone.P or Phone.B or Phone.T ? _sample + _phoneLen * 70 / 100 : -1;
        _events[_evHead] = new PhoneEvent(_sample, p, _burstAt);
        _evHead = (_evHead + 1) % EventRing;
        if (_evCount < EventRing) _evCount++;
        EventsTotal++;
        Speaker(out _, out var sections, out _, out var areaScale);
        Tract.Sections = sections;
        Fill(p, Tract.TargetArea, sections, areaScale);
        Tract.TargetVelum = p is Phone.M or Phone.N ? 0.7f : 0f;
        Tract.FricationSection = p switch
        {
            Phone.F => sections - 2, Phone.S => (int)(sections * 0.9f), Phone.P or Phone.B => sections - 2, Phone.T => (int)(sections * 0.86f), _ => -1,
        };
    }

    private void ApplyRest()
    {
        Speaker(out _, out var sections, out _, out var areaScale);
        Tract.Sections = sections;
        Fill(Phone.Rest, Tract.TargetArea, sections, areaScale);
        Fill(Phone.Rest, Tract.Area, sections, areaScale);
        Tract.TargetVelum = 0f;
        Tract.Voicing = 0f;
    }

    /// <summary>1 ms 제어: 음소 진행 · 운율 · 성문/마찰/버스트 제어.</summary>
    private void ControlStep()
    {
        if (!_finished && _sample - _phoneStart >= _phoneLen)
        {
            _cur++;
            if (_cur >= _count)
            {
                LinesSpoken++;
                if (Loop) { _cur = 0; BeginPhone(); }
                else { _finished = true; ApplyRest(); }
            }
            else BeginPhone();
        }
        var e = Emotion;
        Speaker(out var f0, out _, out var oq, out _);
        BaseF0 = f0 * (1f + 0.35f * e.Anger + 0.2f * e.Joy + 0.15f * e.Tension + 0.05f * e.Shyness) * PitchScale;
        var range = 3f + 5f * e.Anger + 5f * e.Joy - 1.5f * e.Tension;
        var p = _finished ? Phone.Rest : _phones[_cur];
        var prog = _count > 1 ? _cur / (float)(_count - 1) : 0f;
        var local = _phoneLen > 0 ? Math.Clamp((_sample - _phoneStart) / (float)_phoneLen, 0f, 1f) : 0f;
        var accent = _finished ? 0f : _accent[_cur] * MathF.Sin(MathF.PI * local);
        var semis = range * (accent - 0.3f * prog);
        if (_question && prog > 0.8f) semis += 6f * (prog - 0.8f) / 0.2f;
        _tremorPh += e.Bpm / 60f * Control / Fs;
        var tremor = 1f + (0.002f + 0.006f * e.Tension) * MathF.Sin(MathF.Tau * _tremorPh);
        var target = BaseF0 * MathF.Pow(2f, semis / 12f) * tremor;
        _f0Sm += (target - _f0Sm) * 0.15f;
        CurrentF0 = _f0Sm;
        var voiced = p is Phone.A or Phone.E or Phone.I or Phone.O or Phone.U or Phone.M or Phone.N or Phone.B;
        var energy = (1f + 0.6f * e.Anger + 0.2f * e.Joy - 0.3f * e.Shyness) * (p == Phone.Pause || p == Phone.Rest ? 0.2f : 1f);
        CurrentEnergy = energy;
        var t = Tract;
        t.F0 = _f0Sm;
        t.LungPressure = energy;
        t.OpenQuotient = Math.Clamp(oq - 0.15f * e.Anger + 0.12f * e.Shyness, 0.35f, 0.85f);
        t.Jitter = 0.004f + 0.02f * e.Tension;
        t.Shimmer = 0.02f + 0.05f * e.Tension;
        t.Aspiration = 0.02f + 0.1f * e.Shyness + 0.03f * e.Tension + (p == Phone.H ? 3f : 0f) + (p == Phone.P && _burstAt >= 0 && _sample >= _burstAt ? 1.5f : 0f);
        var vTarget = voiced ? 1f : 0f;
        if (p == Phone.B && _burstAt >= 0 && _sample < _burstAt) vTarget = 0.35f;   // 폐쇄 중 유성 버즈
        t.Voicing += (vTarget - t.Voicing) * 0.35f;
        t.FricationAmp = p switch { Phone.F => 0.012f, Phone.S => 0.025f, _ => 0f };
        // 파열: 폐쇄 → 버스트(개방 · 15 ms 감쇠 잡음)
        t.BurstAmp = 0f;
        if (_burstAt >= 0 && _sample >= _burstAt)
        {
            var since = (_sample - _burstAt) / (float)Fs;
            t.BurstAmp = BurstGain * MathF.Exp(-since / 0.012f);
            Speaker(out _, out var sections, out _, out var areaScale);
            Fill(Phone.Rest, t.TargetArea, sections, areaScale);   // 개방
            if (since < 0.002f) for (var i = (int)(sections * 0.8f); i < sections; i++) t.Area[i] = MathF.Max(t.Area[i], 1.2f);
        }
        t.Breath = p == Phone.Pause ? (local < 0.55f ? 1f : -1f) : 0f;
        _f0Ring[_prHead] = voiced ? _f0Sm : 0f; _enRing[_prHead] = energy; _phRing[_prHead] = (byte)p;
        _prHead = (_prHead + 1) % ProsodyRing;
        if (_prCount < ProsodyRing) _prCount++;
    }

    /// <summary>청크 합성(제어 1 ms · 성도 블록 · 무할당). dst 길이 = <see cref="Chunk"/> 권장.</summary>
    public void RenderChunk(Span<float> dst)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var done = 0;
        while (done < dst.Length)
        {
            var off = (int)(_sample % Control);
            if (off == 0) ControlStep();
            var n = Math.Min(dst.Length - done, Control - off);
            Tract.Process(dst.Slice(done, n));
            _sample += n; done += n;
        }
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        LastChunkUs = us;
        if (_sample > Chunk * 4 && us > MaxChunkUs) MaxChunkUs = us;
    }

    public void ResetTiming() => MaxChunkUs = 0;
}
