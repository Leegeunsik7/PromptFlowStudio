using System.Globalization;
using System.Text;
using System.Text.Json;
using PromptFlowStudio.Services.DigitalTwin;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Services.Accessibility;

/// <summary>프레임 비전 관측(무할당 산출 · 값형).</summary>
public readonly record struct FrameObservation(double TimeSec, float Luma, float Warmth, float Saturation, float Motion, float SkinRatio, float SkinX, float SkinY, bool SceneCut);

/// <summary>대사 공백 구간.</summary>
public readonly record struct DialogueGap(double Start, double End)
{
    public double Duration => End - Start;
}

/// <summary>화면 해설 세그먼트(구간 · 대본 · 근거 관측 · 출처).</summary>
public sealed record AdSegment(double Start, double End, string Text, string Source, int Observations)
{
    public double Duration => End - Start;
}

/// <summary>
/// [PromptFlow Studio v16.47.0] 🦯 시각장애인용 화면 해설(Audio Description) 자동 생성 엔진.
///   ▸ AI 비전(BCL · 무할당): 32×18 다운샘플 · 휘도/색온도/채도 · 그리드 차분 모션 · 피부 화소(트윈 판별식) 비율/무게중심(인물 위치) · 히스토그램 장면 전환.
///   ▸ 대사 공백 정밀 계산: 대사 큐 병합 → 가드(앞/뒤) 적용 → 최소 길이 이상 공백(Span 출력 · 무할당).
///   ▸ 내러티브 대본: 공백 구간 관측 요약 → 장면 전환 · 시간대/조명 · 인물 수/위치 · 동작 강도 · 감정(선택) 우선순위 문장 → 공백 길이에 맞춰 낭독 속도(초당 글자)로 절삭.
///     Gemini 재작성(JSON 스키마 · 길이 상한 · 데드라인 · 실패 시 BCL 폴백).
///   ▸ 오디오: AD 스템(TTS 음성 파일 배치 · 없으면 음절 포먼트 합성) · 메인 트랙 자동 더킹(어택/릴리스 · −dB) · 믹스 · 16-bit WAV — 더킹/믹스 무할당.
/// </summary>
public sealed class GenerativeAudioDescriptionEngine
{
    public const int GridW = 32, GridH = 18, GridN = GridW * GridH;
    public const double DefaultMinGap = 1.6, DefaultGuard = 0.25;
    public const float CharsPerSecond = 6.5f, CutThreshold = 0.42f;

    private readonly Func<string, CancellationToken, Task<string?>>? _llm;
    private readonly object _gate = new();
    private readonly float[] _grid = new float[GridN], _prevGrid = new float[GridN];
    private readonly int[] _hist = new int[16], _prevHist = new int[16];
    private readonly List<FrameObservation> _obs = new();
    private readonly Dictionary<double, string> _emotions = new();
    private List<AdSegment> _segments = new();
    private bool _hasPrev;
    private long _frames;

    public GenerativeAudioDescriptionEngine() : this((Func<string, CancellationToken, Task<string?>>?)null) { }

    public GenerativeAudioDescriptionEngine(IServiceProvider sp)
        : this(sp.GetService(typeof(GeminiClient)) is GeminiClient g ? async (p, ct) => { if (!g.Enabled) return null; var r = await g.GenerateAsync(p, null, 0.4, ct).ConfigureAwait(false); return r.Ok ? r.Text : null; } : null) { }

    public GenerativeAudioDescriptionEngine(Func<string, CancellationToken, Task<string?>>? llm) => _llm = llm;

    public event Action? OnChange;

    public double MinGap { get; set; } = DefaultMinGap;
    public double Guard { get; set; } = DefaultGuard;
    public float DuckDb { get; set; } = -12f;
    public bool UseLlm { get; set; } = true;
    public int LlmDeadlineMs { get; set; } = 6000;
    public bool LlmAvailable => _llm is not null;
    public long FramesAnalyzed => Interlocked.Read(ref _frames);
    public IReadOnlyList<FrameObservation> Observations { get { lock (_gate) return _obs.ToArray(); } }
    public IReadOnlyList<AdSegment> Segments { get { lock (_gate) return _segments; } }
    public string? LastAction { get; private set; }

    // ══════════════════════ 비전 분석(무할당) ══════════════════════

    public void Reset()
    {
        lock (_gate) { _obs.Clear(); _emotions.Clear(); _segments = new(); _hasPrev = false; }
    }

    /// <summary>프레임(rgb24) 관측 → 기록. 워밍업(관측 리스트 용량) 후 무할당.</summary>
    public FrameObservation AnalyzeFrame(ReadOnlySpan<byte> rgb, int w, int h, double tSec)
    {
        if (rgb.Length < w * h * 3) throw new ArgumentException("프레임 크기 불일치");
        lock (_gate)
        {
            Array.Clear(_grid); Array.Clear(_hist);
            double lum = 0, warm = 0, sat = 0; long skin = 0; double sx = 0, sy = 0; long n = 0;
            var step = Math.Max(1, Math.Min(w, h) / 90);
            for (var y = 0; y < h; y += step)
            {
                var gy = y * GridH / h;
                for (var x = 0; x < w; x += step)
                {
                    var o = (y * w + x) * 3;
                    int r = rgb[o], g = rgb[o + 1], b = rgb[o + 2];
                    var l = 0.299f * r + 0.587f * g + 0.114f * b;
                    lum += l; warm += r - b;
                    int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
                    sat += mx == 0 ? 0 : (mx - mn) / (double)mx;
                    _grid[gy * GridW + x * GridW / w] += l;
                    _hist[(int)l >> 4]++;
                    if (AiDigitalTwinGeneratorEngine.IsSkin((byte)r, (byte)g, (byte)b)) { skin++; sx += x; sy += y; }
                    n++;
                }
            }
            var perCell = Math.Max(1, n / (float)GridN);
            float motion = 0, cut = 0;
            for (var i = 0; i < GridN; i++) _grid[i] /= perCell;
            if (_hasPrev)
            {
                for (var i = 0; i < GridN; i++) motion += MathF.Abs(_grid[i] - _prevGrid[i]);
                motion /= GridN * 255f;
                for (var i = 0; i < 16; i++) cut += Math.Abs(_hist[i] - _prevHist[i]);
                cut /= 2f * n;
            }
            Array.Copy(_grid, _prevGrid, GridN); Array.Copy(_hist, _prevHist, 16);
            var ob = new FrameObservation(tSec, (float)(lum / n), (float)(warm / n), (float)(sat / n), motion, skin / (float)n,
                skin == 0 ? -1 : (float)(sx / skin / w), skin == 0 ? -1 : (float)(sy / skin / h), _hasPrev && cut >= CutThreshold);
            _hasPrev = true;
            _obs.Add(ob);
            Interlocked.Increment(ref _frames);
            return ob;
        }
    }

    /// <summary>외부 표정 분석(예: v16.36 표정 분류) 결과 기록.</summary>
    public void ObserveEmotion(double tSec, string label) { lock (_gate) _emotions[Math.Round(tSec, 2)] = label; }

    // ══════════════════════ 대사 공백 ══════════════════════

    /// <summary>대사 큐(시작 순 아니어도 됨) → 공백(가드 · 최소 길이). 반환 = 기록 수. 큐 64개까지 무할당.</summary>
    public static int FindGaps(ReadOnlySpan<A11yCue> cues, double duration, double minGap, double guard, Span<DialogueGap> dst)
    {
        Span<double> s = stackalloc double[Math.Min(cues.Length, 64)];
        Span<double> e = stackalloc double[s.Length];
        var m = s.Length;
        for (var i = 0; i < m; i++) { s[i] = cues[i].Start; e[i] = cues[i].End; }
        for (var i = 1; i < m; i++)   // 삽입 정렬(시작 기준)
        {
            double ks = s[i], ke = e[i]; var k = i - 1;
            while (k >= 0 && s[k] > ks) { s[k + 1] = s[k]; e[k + 1] = e[k]; k--; }
            s[k + 1] = ks; e[k + 1] = ke;
        }
        var n = 0; double cursor = 0;
        for (var i = 0; i <= m && n < dst.Length; i++)
        {
            var nextStart = i < m ? s[i] : duration;
            var gs = cursor + (cursor > 0 ? guard : 0);
            var ge = nextStart - (i < m ? guard : 0);
            if (ge - gs >= minGap) dst[n++] = new DialogueGap(gs, ge);
            if (i < m) cursor = Math.Max(cursor, e[i]);
        }
        return n;
    }

    public static int MaxChars(double seconds) => (int)Math.Floor(seconds * CharsPerSecond);

    /// <summary>문장 우선순위 순으로 공백 길이 내 글자 수에 맞춤(문장 단위 · 첫 문장은 말줄임 절삭).</summary>
    public static string FitToGap(IReadOnlyList<string> sentences, double seconds)
    {
        var max = MaxChars(seconds);
        var sb = new StringBuilder();
        foreach (var s in sentences)
        {
            var add = sb.Length == 0 ? s.Length : s.Length + 1;
            if (sb.Length + add > max)
            {
                if (sb.Length == 0 && max >= 4) sb.Append(s.AsSpan(0, max - 1)).Append('…');
                break;
            }
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(s);
        }
        return sb.ToString();
    }

    /// <summary>구간 관측 → 묘사 문장 후보(우선순위 순).</summary>
    public List<string> Describe(double start, double end, out int used)
    {
        FrameObservation[] obs;
        lock (_gate) obs = _obs.ToArray();
        var list = new List<string>();
        int n = 0; bool cut = false; double lum = 0, warm = 0, sat = 0, skin = 0, motion = 0, sx = 0; int skinFrames = 0;
        foreach (var o in obs)
        {
            if (o.TimeSec < start - 1.0 || o.TimeSec > end) continue;
            n++; lum += o.Luma; warm += o.Warmth; sat += o.Saturation; skin += o.SkinRatio; motion += o.Motion;
            if (o.SkinX >= 0) { sx += o.SkinX; skinFrames++; }
            if (o.SceneCut && o.TimeSec >= start - 0.5) cut = true;
        }
        used = n;
        if (n == 0) { list.Add("화면이 이어집니다."); return list; }
        lum /= n; warm /= n; sat /= n; skin /= n; motion /= n;
        if (cut) list.Add("장면이 바뀝니다.");
        var light = lum < 55 ? "어두운" : lum > 170 ? "환하게 밝은" : "은은한 조명의";
        var tone = warm > 25 ? "따뜻한 색감" : warm < -20 ? "푸른빛이 도는" : sat < 0.12 ? "무채색" : "자연스러운 색감";
        var scene = light + " " + tone + " 화면입니다.";
        if (skin > 0.02 && skinFrames > 0)
        {
            var pos = sx / skinFrames < 0.38 ? "왼쪽" : sx / skinFrames > 0.62 ? "오른쪽" : "가운데";
            var act = motion > 0.06 ? "빠르게 움직입니다" : motion > 0.015 ? "천천히 움직입니다" : "가만히 서 있습니다";
            list.Add((skin > 0.12 ? "가까운 인물이 " : "인물이 ") + pos + "에서 " + act + ".");
        }
        else if (motion > 0.05) list.Add("무언가 빠르게 움직입니다.");
        list.Add(scene);   // 우선순위: 장면 전환 → (표정) → 인물/동작 → 배경 조명
        string? emo = null;
        lock (_gate) foreach (var kv in _emotions) if (kv.Key >= start - 1 && kv.Key <= end) { emo = kv.Value; break; }
        if (emo is not null) list.Insert(cut ? 1 : 0, "인물의 표정은 " + emo + ".");
        return list;
    }

    /// <summary>대사 큐 + 관측 → AD 세그먼트(BCL · 공백 내부 · 겹침 없음).</summary>
    public List<AdSegment> BuildScript(IReadOnlyList<A11yCue> cues, double duration)
    {
        var arr = new A11yCue[cues.Count];
        for (var i = 0; i < arr.Length; i++) arr[i] = cues[i];
        Span<DialogueGap> gaps = stackalloc DialogueGap[64];
        var n = FindGaps(arr, duration, MinGap, Guard, gaps);
        var segs = new List<AdSegment>(n);
        for (var i = 0; i < n; i++)
        {
            var g = gaps[i];
            var sentences = Describe(g.Start, g.End, out var used);
            var text = FitToGap(sentences, g.Duration);
            if (text.Length == 0) continue;
            var speak = Math.Min(g.Duration, Math.Max(0.6, text.Length / CharsPerSecond));
            segs.Add(new AdSegment(g.Start, g.Start + speak, text, "BCL", used));
        }
        lock (_gate) _segments = segs;
        LastAction = "🦯 화면 해설 " + segs.Count.ToString(CultureInfo.InvariantCulture) + "구간 · 공백 " + n.ToString(CultureInfo.InvariantCulture);
        Raise();
        return segs;
    }

    /// <summary>Gemini 재작성(구간별 · JSON {"lines":[...]} · 각 줄 공백 길이 상한 · 실패 시 BCL 유지).</summary>
    public async Task<List<AdSegment>> RefineAsync(List<AdSegment> segs, string? context, CancellationToken ct = default)
    {
        if (_llm is null || !UseLlm || segs.Count == 0) return segs;
        var sb = new StringBuilder("You write Korean audio description (화면 해설) for blind viewers. Present tense, objective, no interpretation, no dialogue. Reply ONLY JSON {\"lines\":[\"...\"]} with exactly ")
            .Append(segs.Count.ToString(CultureInfo.InvariantCulture)).Append(" lines, line i must be at most the given character limit.\n");
        if (!string.IsNullOrWhiteSpace(context)) sb.Append("Program context: ").Append(context).Append('\n');
        for (var i = 0; i < segs.Count; i++)
            sb.Append(i.ToString(CultureInfo.InvariantCulture)).Append(") limit ").Append(MaxChars(GapOf(segs[i])).ToString(CultureInfo.InvariantCulture)).Append(" chars · observed: ").Append(segs[i].Text).Append('\n');
        using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
        d.CancelAfter(LlmDeadlineMs);
        try
        {
            var text = await _llm(sb.ToString(), d.Token).ConfigureAwait(false);
            if (!TryParseLines(text, segs.Count, out var lines)) return segs;
            var res = new List<AdSegment>(segs.Count);
            for (var i = 0; i < segs.Count; i++)
            {
                var max = MaxChars(GapOf(segs[i]));
                var l = lines[i].Length <= max ? lines[i] : segs[i].Text;
                var speak = Math.Min(GapOf(segs[i]), Math.Max(0.6, l.Length / CharsPerSecond));
                res.Add(segs[i] with { End = segs[i].Start + speak, Text = l, Source = l == segs[i].Text ? segs[i].Source : "Gemini" });
            }
            lock (_gate) _segments = res;
            LastAction = "🤖 Gemini 화면 해설 재작성";
            Raise();
            return res;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return segs; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException) { PromptFlowStudio.Program.Log("AD.Llm", ex); return segs; }
    }

    private double GapOf(AdSegment s) => Math.Max(s.Duration, s.Text.Length / CharsPerSecond);

    public static bool TryParseLines(string? text, int expected, out List<string> lines)
    {
        lines = new();
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = GeminiClient.StripFence(text);
        var a = s.IndexOf('{'); var b = s.LastIndexOf('}');
        if (a < 0 || b <= a) return false;
        try
        {
            using var doc = JsonDocument.Parse(s[a..(b + 1)]);
            if (!doc.RootElement.TryGetProperty("lines", out var arr) || arr.ValueKind != JsonValueKind.Array) return false;
            foreach (var l in arr.EnumerateArray())
            {
                if (l.ValueKind != JsonValueKind.String) return false;
                var t = l.GetString()!.Trim();
                if (t.Length == 0) return false;
                lines.Add(t);
            }
            return lines.Count == expected;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>세그먼트가 대사(가드 포함)와 겹치는지 검사.</summary>
    public static bool OverlapsDialogue(IReadOnlyList<AdSegment> segs, IReadOnlyList<A11yCue> cues)
    {
        foreach (var s in segs) foreach (var c in cues) if (s.Start < c.End && c.Start < s.End) return true;
        return false;
    }

    // ══════════════════════ 오디오 스템 · 더킹 ══════════════════════

    /// <summary>AD 스템(mono float): 세그먼트별 음성 PCM(선택 · 16-bit mono WAV) 배치 · 없으면 음절 포먼트 합성.</summary>
    public static float[] SynthesizeStem(IReadOnlyList<AdSegment> segs, double duration, int sampleRate, Func<AdSegment, float[]?>? voice = null)
    {
        var stem = new float[(int)Math.Ceiling(duration * sampleRate)];
        foreach (var s in segs)
        {
            var at = (int)(s.Start * sampleRate);
            var maxLen = (int)(s.Duration * sampleRate);
            var pcm = voice?.Invoke(s);
            if (pcm is not null)
            {
                for (var i = 0; i < pcm.Length && i < maxLen && at + i < stem.Length; i++) stem[at + i] = pcm[i];
                continue;
            }
            var syll = Math.Max(1, s.Text.Length);
            var per = maxLen / (double)syll;
            for (var i = 0; i < maxLen && at + i < stem.Length; i++)
            {
                var k = (int)(i / per);
                var local = (i - k * per) / per;
                var env = (float)Math.Sin(Math.PI * Math.Clamp(local, 0, 1));
                var f0 = 180 + 25 * Math.Sin(k * 1.7);
                var ch = s.Text[Math.Min(k, s.Text.Length - 1)];
                var formant = char.IsWhiteSpace(ch) ? 0 : 600 + (ch % 7) * 120;
                var tt = i / (double)sampleRate;
                stem[at + i] = char.IsWhiteSpace(ch) ? 0 : 0.25f * env * (float)(Math.Sin(2 * Math.PI * f0 * tt) * 0.6 + Math.Sin(2 * Math.PI * formant * tt) * 0.4);
            }
        }
        return stem;
    }

    /// <summary>더킹 게인 엔벨로프(세그먼트 구간 −dB · 어택/릴리스 램프)를 메인 트랙에 제자리 적용. 세그먼트 64개까지 무할당.</summary>
    public static void ApplyDucking(Span<float> main, ReadOnlySpan<(double Start, double End)> regions, int sampleRate, float duckDb, double attack = 0.2, double release = 0.35)
    {
        var floor = MathF.Pow(10, duckDb / 20f);
        var aS = Math.Max(1, (int)(attack * sampleRate)); var rS = Math.Max(1, (int)(release * sampleRate));
        foreach (var (start, end) in regions)
        {
            int s = (int)(start * sampleRate), e = (int)(end * sampleRate);
            int from = Math.Max(0, s - aS), to = Math.Min(main.Length, e + rS);
            for (var i = from; i < to; i++)
            {
                float g;
                if (i < s) g = 1 - (1 - floor) * ((i - (s - aS)) / (float)aS);
                else if (i < e) g = floor;
                else g = floor + (1 - floor) * ((i - e) / (float)rS);
                main[i] *= g;
            }
        }
    }

    public static void Mix(ReadOnlySpan<float> main, ReadOnlySpan<float> ad, Span<float> dst)
    {
        var n = Math.Min(dst.Length, Math.Max(main.Length, ad.Length));
        for (var i = 0; i < n; i++)
        {
            var v = (i < main.Length ? main[i] : 0) + (i < ad.Length ? ad[i] : 0);
            dst[i] = Math.Clamp(v, -1f, 1f);
        }
    }

    public static float Rms(ReadOnlySpan<float> s)
    {
        if (s.IsEmpty) return 0;
        double a = 0;
        foreach (var v in s) a += v * v;
        return (float)Math.Sqrt(a / s.Length);
    }

    public static byte[] ToWav16(ReadOnlySpan<float> mono, int sampleRate)
    {
        var data = mono.Length * 2;
        var b = new byte[44 + data];
        void W32(int o, int v) => BitConverter.TryWriteBytes(b.AsSpan(o, 4), v);
        void W16(int o, short v) => BitConverter.TryWriteBytes(b.AsSpan(o, 2), v);
        "RIFF"u8.CopyTo(b); W32(4, 36 + data); "WAVE"u8.CopyTo(b.AsSpan(8)); "fmt "u8.CopyTo(b.AsSpan(12));
        W32(16, 16); W16(20, 1); W16(22, 1); W32(24, sampleRate); W32(28, sampleRate * 2); W16(32, 2); W16(34, 16);
        "data"u8.CopyTo(b.AsSpan(36)); W32(40, data);
        for (var i = 0; i < mono.Length; i++) W16(44 + i * 2, (short)Math.Round(Math.Clamp(mono[i], -1f, 1f) * 32767));
        return b;
    }

    /// <summary>WebVTT 화면 해설 트랙(kind="descriptions").</summary>
    public static string ToWebVtt(IReadOnlyList<AdSegment> segs)
    {
        var sb = new StringBuilder("WEBVTT\nKind: descriptions\nLanguage: ko\n\n");
        for (var i = 0; i < segs.Count; i++)
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append('\n').Append(VttTime(segs[i].Start)).Append(" --> ").Append(VttTime(segs[i].End)).Append('\n').Append(segs[i].Text).Append("\n\n");
        return sb.ToString();
    }

    public static string VttTime(double s)
    {
        var t = TimeSpan.FromMilliseconds(Math.Round(Math.Max(0, s) * 1000));
        return ((int)t.TotalHours).ToString("00", CultureInfo.InvariantCulture) + ":" + t.Minutes.ToString("00", CultureInfo.InvariantCulture) + ":" + t.Seconds.ToString("00", CultureInfo.InvariantCulture) + "." + t.Milliseconds.ToString("000", CultureInfo.InvariantCulture);
    }

    private void Raise() { try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("AD.OnChange", ex); } }
}
