using System.Globalization;
using System.Text;
using System.Text.Json;
using PromptFlowStudio.Services.Media;
using PromptFlowStudio.Services.Storyboard;
using PromptFlowStudio.Services.VTuber;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Services.Newsroom;

public enum AnchorRole : byte { Anchor, Reporter }

public sealed record AnchorLine(int Index, AnchorRole Role, string Speaker, string Text, FacialEmotion Emotion, double Start, double Duration);

public sealed record AnchorScript(string StoryTitle, NewsGrade Grade, FactVerdict Verdict, int Score, IReadOnlyList<AnchorLine> Lines, string Source, double TotalSeconds);

/// <summary>
/// [PromptFlow Studio v16.44.0] 🎙 듀얼 AI 앵커 코호스트 &amp; 자율 대본 텔레프롬프터.
///   ▸ 대본: 메인 앵커 오프닝(등급별 멘트) → 현장 리포터 연결 → 리포터 리포트(요약 2문장) → 앵커 후속 질문(분야별) → 리포터 답변(팩트체크 판정 고지 · 미확인/논란/허위 경고 필수) → 앵커 클로징.
///     허위 판정 + BlockFalse = 팩트체크 정정 코너로 전환. Gemini(설정 시) 대화체 재작성(JSON lines 스키마 검증 · 고지 누락 시 BCL 고지 재삽입 · 데드라인 · 폴백).
///   ▸ 텔레프롬프터: 한국어 발화 속도(초당 글자) 기반 타이밍 · LineAt(t)/진행률 무할당 이진 탐색.
///   ▸ 라이브 진행: TTS(앵커 Cloud · 리포터 Bright 톤) → v16.38.0 브릿지 PrepareSpeechAsync(v16.20.0 오디오 립싱크) → Speak · 화자 하단 자막(그래픽 엔진) · CTS 취소.
/// </summary>
public sealed class DualAiNewsAnchorGenerator
{
    public const double CharsPerSecond = 7.0, MinLineSec = 1.2, GapSec = 0.35;

    private readonly Func<string, CancellationToken, Task<string?>>? _llm;
    private readonly LiveVTuberBroadcastingBridge? _bridge;
    private readonly AiVoiceoverTtsService? _tts;
    private readonly NewsMotionGraphicsOverlayEngine? _gfx;
    private CancellationTokenSource? _cts;
    private int _current = -1;
    private long _performed;

    public DualAiNewsAnchorGenerator(IServiceProvider sp)
        : this(sp.GetService(typeof(GeminiClient)) is GeminiClient g ? async (p, ct) => { if (!g.Enabled) return null; var r = await g.GenerateAsync(p, null, 0.6, ct).ConfigureAwait(false); return r.Ok ? r.Text : null; } : null,
               sp.GetService(typeof(LiveVTuberBroadcastingBridge)) as LiveVTuberBroadcastingBridge, sp.GetService(typeof(AiVoiceoverTtsService)) as AiVoiceoverTtsService,
               sp.GetService(typeof(NewsMotionGraphicsOverlayEngine)) as NewsMotionGraphicsOverlayEngine) { }

    public DualAiNewsAnchorGenerator(Func<string, CancellationToken, Task<string?>>? llm, LiveVTuberBroadcastingBridge? bridge = null, AiVoiceoverTtsService? tts = null, NewsMotionGraphicsOverlayEngine? gfx = null)
    {
        _llm = llm; _bridge = bridge; _tts = tts; _gfx = gfx;
    }

    public event Action? OnChange;
    public string AnchorName { get; set; } = "한서진";
    public string ReporterName { get; set; } = "윤태오";
    public string StylePrompt { get; set; } = "신뢰감 있는 공영방송 9시 뉴스 톤. 간결한 문장, 사실과 추측을 명확히 구분, 과장 금지.";
    public bool BlockFalse { get; set; } = true;
    public bool UseLlm { get; set; } = true;
    public bool UseTts { get; set; } = true;
    public int LlmDeadlineMs { get; set; } = 6000;
    public bool LlmAvailable => _llm is not null;
    public AnchorScript? Current { get; private set; }
    public int CurrentLine => Volatile.Read(ref _current);
    public bool OnAir => _cts is not null;
    public long Performed => Interlocked.Read(ref _performed);
    public string? LastAction { get; private set; }

    // ══════════════════════ BCL 대본 ══════════════════════

    public static string Opening(NewsGrade g) => g switch
    {
        NewsGrade.Breaking => "속보입니다.", NewsGrade.Urgent => "긴급 소식 전해드립니다.", NewsGrade.Exclusive => "단독 보도입니다.", _ => "다음 소식입니다.",
    };

    public static string FollowUp(string category) => category switch
    {
        "재난" or "사고" => "추가 피해 우려는 없습니까?",
        "경제" => "시장에 미칠 영향은 어떻게 전망됩니까?",
        "연예" => "당사자 측의 공식 입장은 확인됐습니까?",
        "정치" => "정치권 반응은 어떻습니까?",
        _ => "앞으로 어떤 점을 주목해야 할까요?",
    };

    public static string Disclosure(FactVerdict v, int sources) => v switch
    {
        FactVerdict.Verified => "현재 " + sources.ToString(CultureInfo.InvariantCulture) + "개 매체와 공식 발표를 통해 교차 확인된 내용입니다.",
        FactVerdict.Likely => "복수 매체가 보도하고 있지만 공식 확인은 아직 진행 중입니다.",
        FactVerdict.Unverified => "아직 공식적으로 확인되지 않은 내용으로, 추가 확인이 필요합니다.",
        FactVerdict.Disputed => "보도마다 수치와 내용이 엇갈리고 있어 신중한 해석이 필요합니다.",
        _ => "이 내용은 사실과 다른 것으로 확인되고 있습니다. 허위 정보 확산에 유의해 주시기 바랍니다.",
    };

    /// <summary>문장 분할(마침표/물음표/느낌표 · '다.' 경계) — 최대 max 문장.</summary>
    public static List<string> Sentences(string text, int max)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            sb.Append(ch);
            if (ch is '.' or '?' or '!' or '。')
            {
                var s = sb.ToString().Trim();
                if (s.Length > 1) list.Add(s);
                sb.Clear();
                if (list.Count >= max) return list;
            }
        }
        var rest = sb.ToString().Trim();
        if (rest.Length > 1 && list.Count < max) list.Add(rest.EndsWith('.') ? rest : rest + ".");
        return list;
    }

    public static string StripTitle(string title)
    {
        var t = title.Trim();
        foreach (var p in new[] { "[속보]", "[단독]", "[긴급]", "속보", "단독", "Breaking:", "BREAKING:" })
            if (t.StartsWith(p, StringComparison.OrdinalIgnoreCase)) { t = t[p.Length..].Trim(); break; }
        return t;
    }

    public AnchorScript BuildTemplate(NewsStory st)
    {
        var lead = st.Lead;
        var title = StripTitle(lead.Title);
        var loc = lead.Location ?? FindLocation(st);
        var raw = new List<(AnchorRole, string, FacialEmotion)>();
        if (st.Verdict == FactVerdict.False && BlockFalse)
        {
            raw.Add((AnchorRole.Anchor, "팩트체크 코너입니다. 온라인에서 '" + title + "'라는 소식이 빠르게 퍼지고 있는데요.", FacialEmotion.Curious));
            raw.Add((AnchorRole.Reporter, "네, 확인 결과 " + Disclosure(FactVerdict.False, st.IndependentSources), FacialEmotion.Neutral));
            var deny = st.Items.Find(i => RealtimeNewsFeedFactCheckEngine.ContainsAny(i.Title + " " + i.Summary, RealtimeNewsFeedFactCheckEngine.NegationWords));
            if (deny is not null) raw.Add((AnchorRole.Reporter, deny.Source + " 보도에 따르면 " + (Sentences(deny.Summary, 1).FirstOrDefault() ?? StripTitle(deny.Title) + "."), FacialEmotion.Neutral));
            raw.Add((AnchorRole.Anchor, "확인되지 않은 정보의 공유는 자제해 주시기 바랍니다.", FacialEmotion.Neutral));
        }
        else
        {
            raw.Add((AnchorRole.Anchor, Opening(st.Grade) + " " + title + ".", st.Grade is NewsGrade.Breaking or NewsGrade.Urgent ? FacialEmotion.Surprised : FacialEmotion.Neutral));
            raw.Add((AnchorRole.Anchor, (loc is null ? "" : loc + " ") + "현장에 나가 있는 " + ReporterName + " 기자 연결합니다. " + ReporterName + " 기자, 현재 상황 어떻습니까?", FacialEmotion.Curious));
            var body = Sentences(lead.Summary, 2);
            raw.Add((AnchorRole.Reporter, "네, " + (loc is null ? "취재 현장" : loc) + "입니다. " + (body.Count > 0 ? string.Join(" ", body) : title + " 소식입니다."), FacialEmotion.Neutral));
            raw.Add((AnchorRole.Anchor, FollowUp(lead.Category), FacialEmotion.Curious));
            raw.Add((AnchorRole.Reporter, Disclosure(st.Verdict, st.IndependentSources), st.Verdict >= FactVerdict.Disputed ? FacialEmotion.Pout : FacialEmotion.Neutral));
            raw.Add((AnchorRole.Anchor, ReporterName + " 기자 수고했습니다. 새로운 내용이 들어오는 대로 다시 전해드리겠습니다.", FacialEmotion.HappySmile));
        }
        return Assemble(st, raw, "BCL");
    }

    private static string? FindLocation(NewsStory st)
    {
        foreach (var i in st.Items) if (!string.IsNullOrWhiteSpace(i.Location)) return i.Location;
        return null;
    }

    private AnchorScript Assemble(NewsStory st, List<(AnchorRole Role, string Text, FacialEmotion Emo)> raw, string source)
    {
        var lines = new List<AnchorLine>(raw.Count);
        double t = 0;
        for (var i = 0; i < raw.Count; i++)
        {
            var d = Math.Max(MinLineSec, raw[i].Text.Length / CharsPerSecond);
            lines.Add(new AnchorLine(i, raw[i].Role, raw[i].Role == AnchorRole.Anchor ? AnchorName : ReporterName, raw[i].Text, raw[i].Emo, Math.Round(t, 3), Math.Round(d, 3)));
            t += d + GapSec;
        }
        var s = new AnchorScript(st.Lead.Title, st.Grade, st.Verdict, st.Score, lines, source, Math.Round(Math.Max(0, t - GapSec), 3));
        Current = s;
        LastAction = "📝 앵커 대본 " + lines.Count.ToString(CultureInfo.InvariantCulture) + "줄 · " + source + " · " + st.Lead.Title;
        Raise();
        return s;
    }

    /// <summary>대본 생성(Gemini 재작성 → 스키마 검증 → 고지 보증 · 실패 시 BCL).</summary>
    public async Task<AnchorScript> GenerateAsync(NewsStory st, CancellationToken ct = default)
    {
        var bcl = BuildTemplate(st);
        if (_llm is null || !UseLlm) return bcl;
        var sb = new StringBuilder("You write live Korean TV news dialogue between main anchor (").Append(AnchorName).Append(") and field reporter (").Append(ReporterName).Append(").\nStyle: ").Append(StylePrompt)
            .Append("\nReturn ONLY JSON {\"lines\":[{\"role\":\"anchor|reporter\",\"text\":\"...\"}]} with 4-8 lines, each <= 120 Korean chars. Never invent facts beyond the reports. Must state verification status: ")
            .Append(Disclosure(st.Verdict, st.IndependentSources)).Append("\nGrade: ").Append(RealtimeNewsFeedFactCheckEngine.GradeLabel(st.Grade)).Append(" · Fact score ").Append(st.Score.ToString(CultureInfo.InvariantCulture)).Append("%\nReports:\n");
        foreach (var it in st.Items.Take(6)) sb.Append("- ").Append(it.Source).Append(": ").Append(it.Title).Append(" — ").Append(it.Summary.Length > 240 ? it.Summary[..240] : it.Summary).Append('\n');
        using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
        d.CancelAfter(LlmDeadlineMs);
        try
        {
            var text = await _llm(sb.ToString(), d.Token).ConfigureAwait(false);
            if (!TryParseLines(text, out var parsed)) return bcl;
            var disclosure = Disclosure(st.Verdict, st.IndependentSources);
            var hasNotice = st.Verdict <= FactVerdict.Likely || parsed.Exists(p => p.Text.Contains("확인", StringComparison.Ordinal) || p.Text.Contains("허위", StringComparison.Ordinal));
            if (!hasNotice) parsed.Insert(Math.Max(0, parsed.Count - 1), (AnchorRole.Reporter, disclosure, FacialEmotion.Neutral));
            return Assemble(st, parsed, "Gemini");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return bcl; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException) { Program.Log("Anchor.Llm", ex); return bcl; }
    }

    public static bool TryParseLines(string? text, out List<(AnchorRole Role, string Text, FacialEmotion Emo)> lines)
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
                if (l.ValueKind != JsonValueKind.Object || !l.TryGetProperty("role", out var r) || r.ValueKind != JsonValueKind.String || !l.TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String) return false;
                var role = r.GetString()!.Trim().ToLowerInvariant() switch { "anchor" => AnchorRole.Anchor, "reporter" => AnchorRole.Reporter, _ => (AnchorRole)255 };
                var tx = t.GetString()!.Trim();
                if ((byte)role == 255 || tx.Length == 0) return false;
                if (tx.Length > 200) tx = tx[..200];
                lines.Add((role, tx, FacialEmotion.Neutral));
            }
            return lines.Count is >= 3 and <= 12 && lines.Exists(x => x.Role == AnchorRole.Anchor) && lines.Exists(x => x.Role == AnchorRole.Reporter);
        }
        catch (JsonException) { return false; }
    }

    /// <summary>편집기 텍스트("앵커: …" / "리포터: …" 줄) → 대본 재구성.</summary>
    public AnchorScript ApplyEdit(NewsStory st, string edited)
    {
        var raw = new List<(AnchorRole, string, FacialEmotion)>();
        foreach (var line in edited.Replace("\r", "").Split('\n'))
        {
            var c = line.IndexOf(':');
            if (c <= 0) continue;
            var who = line[..c].Trim();
            var text = line[(c + 1)..].Trim();
            if (text.Length == 0) continue;
            var role = who.StartsWith("리포터", StringComparison.Ordinal) || who.Equals(ReporterName, StringComparison.Ordinal) || who.Equals("reporter", StringComparison.OrdinalIgnoreCase) ? AnchorRole.Reporter : AnchorRole.Anchor;
            raw.Add((role, text, FacialEmotion.Neutral));
        }
        return Assemble(st, raw, "편집");
    }

    public static string ToEditorText(AnchorScript s)
    {
        var sb = new StringBuilder();
        foreach (var l in s.Lines) sb.Append(l.Role == AnchorRole.Anchor ? "앵커" : "리포터").Append(": ").Append(l.Text).Append('\n');
        return sb.ToString();
    }

    // ══════════════════════ 텔레프롬프터(무할당) ══════════════════════

    /// <summary>시각 t 의 대본 줄(갭 구간은 직전 줄 · 시작 전 -1).</summary>
    public static int LineAt(IReadOnlyList<AnchorLine> lines, double t)
    {
        int lo = 0, hi = lines.Count - 1, ans = -1;
        while (lo <= hi)
        {
            var m = (lo + hi) >> 1;
            if (lines[m].Start <= t) { ans = m; lo = m + 1; } else hi = m - 1;
        }
        return ans;
    }

    /// <summary>줄 내 진행률 0~1 및 현재 발화 글자 위치.</summary>
    public static float Progress(AnchorLine l, double t, out int charIndex)
    {
        var p = (float)Math.Clamp((t - l.Start) / Math.Max(0.001, l.Duration), 0, 1);
        charIndex = (int)(p * l.Text.Length);
        return p;
    }

    // ══════════════════════ 라이브 진행 ══════════════════════

    /// <summary>대본 라이브 진행(TTS → 립싱크 브릿지 Speak · 화자 하단 자막 · 줄 길이만큼 대기). 취소 시 즉시 중단.</summary>
    public async Task<int> PerformAsync(AnchorScript script, CancellationToken ct = default)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var prev = Interlocked.Exchange(ref _cts, cts);
        if (prev is not null) { try { prev.Cancel(); } catch (ObjectDisposedException) { } }
        var done = 0;
        LastAction = "🔴 ON AIR · " + script.StoryTitle;
        Raise();
        try
        {
            for (var i = 0; i < script.Lines.Count; i++)
            {
                cts.Token.ThrowIfCancellationRequested();
                var l = script.Lines[i];
                Volatile.Write(ref _current, i);
                _gfx?.ShowSpeaker(l.Speaker, l.Role == AnchorRole.Anchor ? "앵커" : "현장 기자");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                if (_bridge is not null)
                {
                    string? voice = null;
                    if (UseTts && _tts is not null)
                    {
                        using var d = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                        d.CancelAfter(TimeSpan.FromSeconds(8));
                        try { voice = await _tts.SynthesizeAsync(l.Text, l.Role == AnchorRole.Anchor ? NarrationTone.Cloud : NarrationTone.Bright, 1.05, ct: d.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (!cts.IsCancellationRequested) { voice = null; }
                        catch (Exception ex) when (ex is HttpRequestException or IOException) { Program.Log("Anchor.Tts", ex); }
                    }
                    var perf = await _bridge.PrepareSpeechAsync(l.Text, l.Emotion, voice, cts.Token).ConfigureAwait(false);
                    _bridge.Speak(perf);
                    var wait = Math.Max(perf.Duration, l.Duration) + GapSec - sw.Elapsed.TotalSeconds;
                    if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait), cts.Token).ConfigureAwait(false);
                }
                else
                {
                    var wait = l.Duration + GapSec - sw.Elapsed.TotalSeconds;
                    if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait), cts.Token).ConfigureAwait(false);
                }
                done++;
                Raise();
            }
            Interlocked.Increment(ref _performed);
            LastAction = "✅ 방송 완료 · " + script.StoryTitle;
        }
        catch (OperationCanceledException) { LastAction = "⏹ 방송 중단"; }
        finally
        {
            Volatile.Write(ref _current, -1);
            if (Interlocked.CompareExchange(ref _cts, null, cts) == cts) cts.Dispose();
            Raise();
        }
        return done;
    }

    public void StopPerformance()
    {
        var c = Volatile.Read(ref _cts);
        if (c is null) return;
        try { c.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void Raise() { try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("Anchor.OnChange", ex); } }
}
