using System.Diagnostics;

namespace PromptFlowStudio.Services.CompetitorSpy;

/// <summary>도입 3 초 훅 유형 — 값 = 해부도 인덱스.</summary>
public enum HookPattern : byte { Statement, Question, Reversal, FearWarning, Curiosity }

/// <summary>바이럴 포뮬러 청사진(값 형식) — 훅 해부 · 컷 템포 · 오디오 다이내믹스.</summary>
public struct ViralFormulaBlueprint
{
    public HookPattern Hook;
    public float HookConfidence;
    public int HookChars, CutCount, PeakCount;
    public float DurationSec, CutsPerSec, AvgShotSec, PeakDispersion, PeakDensityPerSec, MeanPeak;
    public ulong Fingerprint;
}

/// <summary>
/// [PromptFlow Studio v17.99.0] 🧬 바이럴 훅 &amp; 시청각 편집 패턴 역공학 디컴파일러(<see cref="Decompile"/> 무할당).
///   ▸ 훅: 도입 3 초 자막(최대 <see cref="HookMaxChars"/> 자)을 규칙 기반 구문 분석 — 유형별 키워드 히트(대소문자 무시 Span 검색) + 물음표 가중 →
///     최다 득점 유형(동점 = 공포/경고 &gt; 반전 &gt; 호기심 &gt; 의문), 0 점 = 평서형. 신뢰도 = 1위 득점 / 전체 득점.
///   ▸ 컷 템포: 프레임 평균 휘도 시계열 |Δ| ≥ <see cref="CutDelta"/> 이고 직전 컷과 2 프레임 이상 떨어지면 컷 1 회 → 초당 컷 · 평균 샷 길이.
///   ▸ 오디오: 100 ms 창 피크 절댓값 → 평균 · 변동계수(분산도) · 평균 + 1σ 초과 피크 밀도(초당 비트 히트).
/// </summary>
public sealed class ViralHookAudiovisualReverseEngineer
{
    public const int HookMaxChars = 48, PatternCount = 5, PeakWindowMs = 100, MinCutGapFrames = 2;
    public const float CutDelta = 0.12f;

    public static readonly string[] PatternNames = { "평서형", "의문형", "반전형", "공포/경고형", "호기심 유발형" };
    public static readonly string[] PatternIcons = { "💬", "❓", "🔄", "⚠️", "🔍" };

    private static readonly string[][] Keywords =
    {
        System.Array.Empty<string>(),
        new[] { "왜", "어떻게", "일까", "뭘까", "인가요", "나요", "할까", "why", "how" },
        new[] { "하지만", "그런데", "반전", "사실은", "알고 보니", "오히려", "아니었", "but" },
        new[] { "절대", "경고", "하지 마", "위험", "조심", "망하", "후회", "never", "stop" },
        new[] { "비밀", "아무도", "모르는", "숨겨진", "이것", "단 1", "공개", "secret" },
    };
    /// <summary>동점 우선순위(앞이 우선).</summary>
    private static readonly HookPattern[] Priority = { HookPattern.FearWarning, HookPattern.Reversal, HookPattern.Curiosity, HookPattern.Question };

    private readonly int[] _hits = new int[PatternCount];
    private ViralFormulaBlueprint _bp;

    public ref readonly ViralFormulaBlueprint Blueprint => ref _bp;
    /// <summary>유형별 득점(해부도 막대).</summary>
    public int Hits(HookPattern p) => _hits[(int)p];
    public long Decompiles { get; private set; }
    public double LastDecompileUs { get; private set; }

    /// <summary>훅 유형 분류(무할당) — 득점은 <see cref="Hits"/> 에 남는다.</summary>
    public HookPattern ClassifyHook(ReadOnlySpan<char> hook, out float confidence)
    {
        Array.Clear(_hits);
        if (hook.Length > HookMaxChars) hook = hook[..HookMaxChars];
        var total = 0;
        for (var p = 1; p < PatternCount; p++)
        {
            var kw = Keywords[p];
            for (var k = 0; k < kw.Length; k++)
                if (hook.Contains(kw[k].AsSpan(), StringComparison.OrdinalIgnoreCase)) _hits[p]++;
        }
        for (var i = 0; i < hook.Length; i++) if (hook[i] == '?' || hook[i] == '？') { _hits[(int)HookPattern.Question] += 2; break; }
        for (var p = 1; p < PatternCount; p++) total += _hits[p];
        if (total == 0) { confidence = 0f; _hits[0] = 1; return HookPattern.Statement; }
        var best = Priority[0];
        for (var i = 1; i < Priority.Length; i++) if (_hits[(int)Priority[i]] > _hits[(int)best]) best = Priority[i];
        confidence = _hits[(int)best] / (float)total;
        return best;
    }

    /// <summary>컷 검출(무할당) — 반환 = 컷 수.</summary>
    public static int CountCuts(ReadOnlySpan<float> luma)
    {
        var cuts = 0;
        var last = -MinCutGapFrames;
        for (var i = 1; i < luma.Length; i++)
        {
            if (MathF.Abs(luma[i] - luma[i - 1]) < CutDelta || i - last < MinCutGapFrames) continue;
            cuts++;
            last = i;
        }
        return cuts;
    }

    /// <summary>훅 · 컷 · 오디오 피크를 하나의 청사진으로 디컴파일(무할당).</summary>
    public ref readonly ViralFormulaBlueprint Decompile(ReadOnlySpan<char> hook, ReadOnlySpan<float> luma, int fps, ReadOnlySpan<float> audio, int fs)
    {
        var t0 = Stopwatch.GetTimestamp();
        Decompiles++;
        _bp = default;
        _bp.Hook = ClassifyHook(hook, out _bp.HookConfidence);
        _bp.HookChars = Math.Min(hook.Length, HookMaxChars);

        fps = Math.Max(1, fps);
        _bp.DurationSec = luma.Length / (float)fps;
        _bp.CutCount = CountCuts(luma);
        _bp.CutsPerSec = _bp.DurationSec > 0f ? _bp.CutCount / _bp.DurationSec : 0f;
        _bp.AvgShotSec = _bp.DurationSec / (_bp.CutCount + 1);

        var win = Math.Max(1, fs * PeakWindowMs / 1000);
        var windows = audio.Length / win;
        if (windows > 0)
        {
            double sum = 0, sq = 0;
            for (var w = 0; w < windows; w++)
            {
                var pk = Peak(audio.Slice(w * win, win));
                sum += pk; sq += (double)pk * pk;
            }
            var mean = sum / windows;
            var sd = Math.Sqrt(Math.Max(0, sq / windows - mean * mean));
            _bp.MeanPeak = (float)mean;
            _bp.PeakDispersion = mean > 1e-6 ? (float)(sd / mean) : 0f;
            var hi = mean + sd;
            for (var w = 0; w < windows; w++) if (Peak(audio.Slice(w * win, win)) > hi) _bp.PeakCount++;
            _bp.PeakDensityPerSec = _bp.PeakCount / (windows * PeakWindowMs / 1000f);
        }

        var h = 0xCBF29CE484222325UL;
        h = Mix(h, (long)_bp.Hook); h = Mix(h, _bp.CutCount); h = Mix(h, _bp.PeakCount); h = Mix(h, (long)(_bp.PeakDispersion * 1000f));
        _bp.Fingerprint = h;
        LastDecompileUs = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
        return ref _bp;
    }

    public void Reset()
    {
        Array.Clear(_hits);
        _bp = default;
        Decompiles = 0;
        LastDecompileUs = 0;
    }

    private static float Peak(ReadOnlySpan<float> s)
    {
        var pk = 0f;
        for (var i = 0; i < s.Length; i++) { var a = MathF.Abs(s[i]); if (a > pk) pk = a; }
        return pk;
    }

    private static ulong Mix(ulong h, long v) => (h ^ (ulong)v) * 0x100000001B3UL;
}
