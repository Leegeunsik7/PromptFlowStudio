using System.Numerics;
using System.Runtime.InteropServices;
using PromptFlowStudio.Services.SemanticChat;

namespace PromptFlowStudio.Services.ViralVoiceFx;

public enum VoiceEmotion : byte { Neutral, Excited, Whisper, Serious, Funny }

/// <summary>감정 보이스 프로파일 — 피치/포먼트 배율(0.7~1.4×) · 숨소리 혼합(0~1) · 출력 이득.</summary>
public readonly record struct VoiceEmotionProfile(float Pitch, float Formant, float Breath, float Gain);

/// <summary>대본 인라인 태그 큐 — <see cref="TextIndex"/> = 태그 제거 본문 기준 문자 위치.</summary>
public struct VoiceEmotionCue
{
    public int TextIndex;
    public VoiceEmotion Emotion;
}

/// <summary>
/// [PromptFlow Studio v17.80.0] 🎙️ 실시간 음성 감정 피치 &amp; 포먼트 변조 엔진(<see cref="Process(ReadOnlySpan{float}, Span{float})"/> 무할당).
/// <b>SOLA(동기 중첩 가산)</b>: 48 kHz 입력 링 → 10 ms 마다 정규화 자기상관(NCCF · 80~500 Hz)으로 주기 T0 추정 →
///   분석 마크를 T0 단위로만 전진(파형 동기)시키고, 합성 마크를 T0 / 피치 간격으로 배치해 Hann 그레인을 중첩 가산 → F0 × 피치.
///   그레인 내부를 포먼트 배율 보폭으로 읽어 스펙트럼 포락선만 독립 스케일(피치와 분리) · 창 합 정규화로 이득 평탄.
/// <b>감정 태그</b>: v17.65 대본 인라인 태그(<c>[excited]</c>/<c>[whisper]</c>/<c>[serious]</c>/<c>[funny]</c> · 한글 별칭)를 샘플 시각 큐로 예약 →
///   50 ms 선형 글라이드로 피치/포먼트/숨소리/이득 전이(클릭 · 계단 0). v17.72 군중 감성(<see cref="CrowdEmotion"/>) 사상 제공.
/// 고정 지연 <see cref="Latency"/>(50 ms) — 그레인 최대 반폭 + 분석 선행분.
/// </summary>
public sealed class RealtimeVoiceEmotionShifterEngine
{
    public const int Fs = 48_000, MinT0 = 96, MaxT0 = 600, UnvoicedT0 = 240, GlideSamples = Fs / 20, Ring = 8192, RingMask = Ring - 1;
    public const float MinRatio = 0.7f, MaxRatio = 1.4f;
    public const int MaxHo = (int)(MaxT0 / MinRatio) + 2, MaxH = (int)(MaxHo * MaxRatio) + 2, Latency = 2400;
    public const int AnalysisWin = 512, PitchHop = 480, BlockMax = 1024, CueCap = 64, HannRes = 1024;
    private const float VoicedThreshold = 0.55f, OctaveTolerance = 0.88f;

    private static readonly float[] HannTab = BuildHann();
    private readonly float[] _in = new float[Ring], _acc = new float[Ring], _wsum = new float[Ring];
    private readonly float[] _an = new float[AnalysisWin + MaxT0 + 1];
    private readonly float[] _f32 = new float[BlockMax];
    private readonly long[] _cueAt = new long[CueCap];
    private readonly VoiceEmotion[] _cueEmo = new VoiceEmotion[CueCap];
    private long _n, _ana = long.MinValue, _lastEst = long.MinValue;
    private double _nextMark = MaxHo;
    private int _cueHead, _cueCount, _glideLeft;
    private VoiceEmotionProfile _from, _to;
    private uint _noise = 0x9E3779B9u;

    public RealtimeVoiceEmotionShifterEngine()
    {
        _to = _from = ProfileOf(VoiceEmotion.Neutral);
        Pitch = Formant = Gain = 1f;
    }

    public VoiceEmotion Emotion { get; private set; }
    public float Pitch { get; private set; }
    public float Formant { get; private set; }
    public float Breath { get; private set; }
    public float Gain { get; private set; }
    public bool Gliding => _glideLeft > 0;
    public int T0 { get; private set; } = UnvoicedT0;
    public bool Voiced { get; private set; }
    public long Grains { get; private set; }
    public long Glides { get; private set; }
    public long EmotionChanges { get; private set; }
    public long ClippedSamples { get; private set; }
    public long SamplesOut => Math.Max(0, _n - Latency);
    public int PendingCues => _cueCount - _cueHead;
    public double DetectedF0 => Voiced ? (double)Fs / T0 : 0;

    // ══════════════════════ 감정 프로파일 · 태그 ══════════════════════
    public static VoiceEmotionProfile ProfileOf(VoiceEmotion e) => e switch
    {
        VoiceEmotion.Excited => new(1.18f, 1.06f, 0f, 1.08f),
        VoiceEmotion.Whisper => new(0.97f, 1.02f, 0.65f, 0.72f),
        VoiceEmotion.Serious => new(0.86f, 0.92f, 0f, 1f),
        VoiceEmotion.Funny => new(1.36f, 1.24f, 0f, 1.04f),
        _ => new(1f, 1f, 0f, 1f),
    };

    public static string EmotionName(VoiceEmotion e) => e switch
    {
        VoiceEmotion.Excited => "신남", VoiceEmotion.Whisper => "속삭임", VoiceEmotion.Serious => "진지", VoiceEmotion.Funny => "웃김", _ => "기본",
    };

    public static string EmotionIcon(VoiceEmotion e) => e switch
    {
        VoiceEmotion.Excited => "🔥", VoiceEmotion.Whisper => "🤫", VoiceEmotion.Serious => "🧐", VoiceEmotion.Funny => "🤪", _ => "🙂",
    };

    public static string TagOf(VoiceEmotion e) => e switch
    {
        VoiceEmotion.Excited => "[excited]", VoiceEmotion.Whisper => "[whisper]", VoiceEmotion.Serious => "[serious]", VoiceEmotion.Funny => "[funny]", _ => "[neutral]",
    };

    /// <summary>v17.72 군중 감성 → 보이스 감정(열광 → 신남 · 즐거움 → 웃김 · 슬픔 → 속삭임 · 분노 → 진지).</summary>
    public static VoiceEmotion FromCrowd(CrowdEmotion c) => c switch
    {
        CrowdEmotion.Hype => VoiceEmotion.Excited, CrowdEmotion.Joy => VoiceEmotion.Funny, CrowdEmotion.Sadness => VoiceEmotion.Whisper, _ => VoiceEmotion.Serious,
    };

    /// <summary>태그 본문(괄호 제외) → 감정 · 대소문자 무시 · 한글 별칭.</summary>
    public static bool TryParseTag(ReadOnlySpan<char> tag, out VoiceEmotion e)
    {
        tag = tag.Trim();
        if (tag.Equals("excited", StringComparison.OrdinalIgnoreCase) || tag.SequenceEqual("신남")) { e = VoiceEmotion.Excited; return true; }
        if (tag.Equals("whisper", StringComparison.OrdinalIgnoreCase) || tag.SequenceEqual("속삭임")) { e = VoiceEmotion.Whisper; return true; }
        if (tag.Equals("serious", StringComparison.OrdinalIgnoreCase) || tag.SequenceEqual("진지")) { e = VoiceEmotion.Serious; return true; }
        if (tag.Equals("funny", StringComparison.OrdinalIgnoreCase) || tag.SequenceEqual("웃김")) { e = VoiceEmotion.Funny; return true; }
        if (tag.Equals("neutral", StringComparison.OrdinalIgnoreCase) || tag.SequenceEqual("기본")) { e = VoiceEmotion.Neutral; return true; }
        e = VoiceEmotion.Neutral; return false;
    }

    /// <summary>대본 → 감정 큐(무할당) · 반환 = 큐 수. <paramref name="plainLength"/> = 인식 태그 제거 본문 길이(미인식 괄호는 본문 유지).</summary>
    public static int ParseScript(ReadOnlySpan<char> script, Span<VoiceEmotionCue> cues, out int plainLength)
    {
        int n = 0, plain = 0, i = 0;
        while (i < script.Length)
        {
            if (script[i] == '[')
            {
                var close = script.Slice(i + 1).IndexOf(']');
                if (close >= 0 && close <= 16 && TryParseTag(script.Slice(i + 1, close), out var e))
                {
                    if (n < cues.Length) { cues[n].TextIndex = plain; cues[n].Emotion = e; n++; }
                    i += close + 2;
                    continue;
                }
            }
            plain++; i++;
        }
        plainLength = plain;
        return n;
    }

    /// <summary>대본 태그를 출력 샘플 시각으로 예약(본문 낭독 속도 <paramref name="charsPerSec"/>) · 반환 = 예약 수.</summary>
    public int ScheduleScript(ReadOnlySpan<char> script, long startSample, double charsPerSec)
    {
        Span<VoiceEmotionCue> cues = stackalloc VoiceEmotionCue[CueCap];
        var n = ParseScript(script, cues, out _);
        var ok = 0;
        for (var k = 0; k < n; k++)
            if (Schedule(startSample + (long)(cues[k].TextIndex / Math.Max(1e-3, charsPerSec) * Fs), cues[k].Emotion)) ok++;
        return ok;
    }

    /// <summary>출력 샘플 시각 감정 전이 예약(시각 비감소 · 상한 <see cref="CueCap"/>).</summary>
    public bool Schedule(long atSample, VoiceEmotion e)
    {
        if (_cueHead == _cueCount) _cueHead = _cueCount = 0;
        if (_cueCount >= CueCap) return false;
        if (_cueCount > _cueHead && atSample < _cueAt[_cueCount - 1]) atSample = _cueAt[_cueCount - 1];
        _cueAt[_cueCount] = atSample; _cueEmo[_cueCount] = e; _cueCount++;
        return true;
    }

    public void ClearCues() => _cueHead = _cueCount = 0;

    /// <summary>즉시 감정 전이 — 현재 값에서 50 ms 선형 글라이드.</summary>
    public void SetEmotion(VoiceEmotion e)
    {
        _from = new VoiceEmotionProfile(Pitch, Formant, Breath, Gain);
        _to = Clamp(ProfileOf(e));
        _glideLeft = GlideSamples;
        if (e != Emotion) EmotionChanges++;
        Emotion = e;
        Glides++;
    }

    /// <summary>사용자 정의 목표(배율 0.7~1.4 클램프) — 글라이드 경유.</summary>
    public void SetTarget(VoiceEmotionProfile p)
    {
        _from = new VoiceEmotionProfile(Pitch, Formant, Breath, Gain);
        _to = Clamp(p);
        _glideLeft = GlideSamples;
        Glides++;
    }

    private static VoiceEmotionProfile Clamp(VoiceEmotionProfile p) =>
        new(Math.Clamp(p.Pitch, MinRatio, MaxRatio), Math.Clamp(p.Formant, MinRatio, MaxRatio), Math.Clamp(p.Breath, 0f, 1f), Math.Clamp(p.Gain, 0f, 2f));

    public void Reset()
    {
        Array.Clear(_in); Array.Clear(_acc); Array.Clear(_wsum);
        _n = 0; _ana = _lastEst = long.MinValue; _nextMark = MaxHo;
        _cueHead = _cueCount = _glideLeft = 0;
        _to = _from = ProfileOf(VoiceEmotion.Neutral);
        Pitch = Formant = Gain = 1f; Breath = 0f; Emotion = VoiceEmotion.Neutral;
        T0 = UnvoicedT0; Voiced = false; Grains = Glides = EmotionChanges = ClippedSamples = 0;
    }

    // ══════════════════════ 처리 ══════════════════════
    /// <summary>float 블록 처리(제자리 가능 · 고정 지연 <see cref="Latency"/>).</summary>
    public void Process(ReadOnlySpan<float> src, Span<float> dst)
    {
        var len = Math.Min(src.Length, dst.Length);
        for (var i = 0; i < len; i++) dst[i] = Step(src[i]);
    }

    /// <summary>48 kHz 16-bit PCM 블록 처리(반올림 · 포화 계수 <see cref="ClippedSamples"/>).</summary>
    public void Process(ReadOnlySpan<short> src, Span<short> dst)
    {
        var len = Math.Min(src.Length, dst.Length);
        for (var off = 0; off < len; off += BlockMax)
        {
            var m = Math.Min(BlockMax, len - off);
            var buf = _f32.AsSpan(0, m);
            for (var i = 0; i < m; i++) buf[i] = src[off + i] * (1f / 32768f);
            Process(buf, buf);
            for (var i = 0; i < m; i++)
            {
                var v = MathF.Round(buf[i] * 32768f);
                if (v > 32767f) { v = 32767f; ClippedSamples++; } else if (v < -32768f) { v = -32768f; ClippedSamples++; }
                dst[off + i] = (short)v;
            }
        }
    }

    private float Step(float x)
    {
        _in[_n & RingMask] = x;
        var tOut = _n - Latency;
        _n++;
        if (tOut < 0) return 0f;

        while (_cueHead < _cueCount && _cueAt[_cueHead] <= tOut) SetEmotion(_cueEmo[_cueHead++]);
        if (_glideLeft > 0)
        {
            _glideLeft--;
            var t = 1f - (float)_glideLeft / GlideSamples;
            Pitch = _from.Pitch + (_to.Pitch - _from.Pitch) * t;
            Formant = _from.Formant + (_to.Formant - _from.Formant) * t;
            Breath = _from.Breath + (_to.Breath - _from.Breath) * t;
            Gain = _from.Gain + (_to.Gain - _from.Gain) * t;
        }

        while (_nextMark <= tOut + MaxHo + 1) PlaceGrain((long)Math.Round(_nextMark));

        var idx = (int)(tOut & RingMask);
        var w = _wsum[idx];
        var y = w > 1e-3f ? _acc[idx] / MathF.Max(w, 0.5f) : 0f;
        _acc[idx] = 0f; _wsum[idx] = 0f;
        if (Breath > 0f)
        {
            _noise = _noise * 1664525u + 1013904223u;
            var r = (int)(_noise >> 8) * (1f / 8388608f) - 1f;
            y = y * (1f - Breath) + y * r * Breath * 1.732f;
        }
        return y * Gain;
    }

    /// <summary>합성 마크 s 에 그레인 1 개 중첩 가산(분석 마크 = T0 동기 전진 · 포먼트 보폭 판독).</summary>
    private void PlaceGrain(long s)
    {
        if (s - _lastEst >= PitchHop || _lastEst == long.MinValue) { EstimateT0(s); _lastEst = s; }
        var t0 = T0;
        var p = Pitch; var f = Formant;
        var spacing = t0 / (double)p;
        var ho = Math.Max(t0 / (double)f, spacing);
        if (ho > MaxHo) ho = MaxHo;
        if (ho * f > MaxH) ho = MaxH / (double)f;

        if (_ana == long.MinValue || _ana < s - 2L * MaxT0 || _ana > s + MaxT0 / 2) _ana = s;
        while (_ana + t0 <= s + t0 / 2) _ana += t0;

        var hoI = (int)ho;
        var inv = HannRes / ho;
        for (var k = -hoI; k <= hoI; k++)
        {
            var u = (int)(Math.Abs(k) * inv);
            if (u >= HannRes) continue;
            var w = HannTab[u];
            var pos = _ana + k * (double)f;
            var ip = (long)Math.Floor(pos);
            var fr = (float)(pos - ip);
            var a = _in[ip & RingMask];
            var b = _in[(ip + 1) & RingMask];
            var o = (int)((s + k) & RingMask);
            _acc[o] += (a + (b - a) * fr) * w;
            _wsum[o] += w;
        }
        Grains++;
        _nextMark = s + spacing;
    }

    private void EstimateT0(long center)
    {
        var start = center - (AnalysisWin + MaxT0) / 2;
        for (var i = 0; i < _an.Length; i++) _an[i] = _in[(start + i) & RingMask];
        var tau = EstimatePeriod(_an);
        Voiced = tau > 0;
        T0 = tau > 0 ? tau : UnvoicedT0;
    }

    /// <summary>NCCF 주기 추정(길이 ≥ <see cref="AnalysisWin"/>+<see cref="MaxT0"/>+1 · 무할당) — 반환 = 주기(샘플) · 무성/무음 = 0.</summary>
    public static int EstimatePeriod(ReadOnlySpan<float> x)
    {
        if (x.Length < AnalysisWin + MaxT0 + 1) return 0;
        var a = x.Slice(0, AnalysisWin);
        var e0 = Dot(a, a);
        if (e0 < 1e-6f * AnalysisWin) return 0;
        var el = Dot(x.Slice(MinT0, AnalysisWin), x.Slice(MinT0, AnalysisWin));
        Span<float> r = stackalloc float[MaxT0 - MinT0 + 1];
        var best = -1f;
        for (var tau = MinT0; tau <= MaxT0; tau++)
        {
            var c = Dot(a, x.Slice(tau, AnalysisWin));
            var v = c / MathF.Sqrt(e0 * MathF.Max(el, 1e-12f));
            r[tau - MinT0] = v;
            if (v > best) best = v;
            var lo = x[tau]; var hi = x[tau + AnalysisWin];
            el += hi * hi - lo * lo;
        }
        if (best < VoicedThreshold) return 0;
        var limit = best * OctaveTolerance;
        for (var k = 1; k < r.Length - 1; k++)
            if (r[k] >= limit && r[k] >= r[k - 1] && r[k] >= r[k + 1]) return k + MinT0;
        return 0;
    }

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var s = 0f; var i = 0;
        if (Vector.IsHardwareAccelerated && a.Length >= Vector<float>.Count)
        {
            var va = MemoryMarshal.Cast<float, Vector<float>>(a);
            var vb = MemoryMarshal.Cast<float, Vector<float>>(b.Slice(0, a.Length));
            var acc = Vector<float>.Zero;
            for (var k = 0; k < va.Length; k++) acc += va[k] * vb[k];
            s = Vector.Dot(acc, Vector<float>.One);
            i = va.Length * Vector<float>.Count;
        }
        for (; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    private static float[] BuildHann()
    {
        var t = new float[HannRes];
        for (var i = 0; i < HannRes; i++) t[i] = 0.5f + 0.5f * MathF.Cos(MathF.PI * i / HannRes);
        return t;
    }
}
