using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using PromptFlowStudio.Services.Storyboard;   // StoryboardProject · StoryboardScene · SmartThemeRecommender · ThemeCinematicsCatalog

namespace PromptFlowStudio.Services.Screenplay;

/// <summary>변환 옵션 — 세로 숏폼 · 립싱크(화면 속 인물 대사) · 나레이터 대사 분리 · 컷 최대 길이(초) · 언어 · 추가/교체 · 테마 매칭.</summary>
public sealed record ConvertOptions(bool Vertical = true, bool LipSync = true, double MaxCutSeconds = 6, string Language = "ko", bool Append = false, bool MatchThemes = true, int MaxCuts = 60);

/// <summary>컷별 음성 큐(TTS · 립싱크) — 화자 · 보이스 톤 · 시작(초) · 텍스트.</summary>
public sealed record VoiceCue(int CutIndex, string Speaker, string VoiceTone, double StartSec, string Text, bool LipSync);

public sealed record ConvertReport(int Scenes, int Cuts, int DialogueCues, int LipSyncCuts, int ThemeMatched, int CatalogSize, IReadOnlyList<VoiceCue> Cues, IReadOnlyList<string> ThemeKeys, string Message);

/// <summary>
/// [PromptFlow Studio v16.35.0] 대본 ➔ 스토리보드 1-Click 트랜스파일러.
///   ▸ 씬 → 컷 분할: 대사 단위로 묶되 컷 최대 길이(기본 6초) 초과 시 분할 · 대사 없는 씬은 지문 1컷.
///   ▸ 프롬프트: 등장 인물 외형(Look · 일관성) + 지문 + 장소/시간대 + 세계관 비주얼 모티프 + 테마 시네마틱(카메라·조명·렌즈 — 204+ 테마 카탈로그 TF-IDF 매칭) + 카메라 가이드.
///   ▸ 음성: 대사 → VoiceText · 화면 속 인물 대사 = 립싱크 · 나레이터 = 보이스오버 · 화자 보이스 톤 큐 · 프로젝트 톤/언어 = 주인공 기준.
///   ▸ 감정: 괄호 지시/긴장도 → FacialEmotion · PresetTension · 테마 네거티브 프롬프트.
///   ▸ 교체/추가 모드 · 컷 상한 · 결과 보고(큐/매칭 테마).
/// </summary>
public sealed class ScreenplayToStoryboardConverter
{
    private readonly SmartThemeRecommender? _themes;

    public ScreenplayToStoryboardConverter(IServiceProvider sp) : this(sp.GetService(typeof(SmartThemeRecommender)) as SmartThemeRecommender) { }

    public ScreenplayToStoryboardConverter(SmartThemeRecommender? themes) => _themes = themes;

    public event Action? OnChange;

    public int CatalogSize { get { try { return _themes?.AllThemes.Count ?? 0; } catch (Exception ex) { Program.Log("Screenplay.Catalog", ex); return 0; } } }
    public ConvertReport? LastReport { get; private set; }
    public string? LastAction { get; private set; }

    /// <summary>괄호 지시/긴장 → 미세 표정.</summary>
    public static FacialEmotion EmotionOf(string? parenthetical, int tension)
    {
        var p = parenthetical ?? "";
        if (p.Contains("웃", StringComparison.Ordinal) || p.Contains("laugh", StringComparison.OrdinalIgnoreCase) || p.Contains("확신", StringComparison.Ordinal)) return FacialEmotion.HappySmile;
        if (p.Contains("놀", StringComparison.Ordinal) || p.Contains("다급", StringComparison.Ordinal) || p.Contains("surpris", StringComparison.OrdinalIgnoreCase)) return FacialEmotion.Surprised;
        if (p.Contains("떨리", StringComparison.Ordinal) || p.Contains("울", StringComparison.Ordinal) || p.Contains("삐", StringComparison.Ordinal)) return FacialEmotion.Pout;
        if (p.Contains("궁금", StringComparison.Ordinal) || p.Contains("갸웃", StringComparison.Ordinal)) return FacialEmotion.Curious;
        return tension >= 85 ? FacialEmotion.Surprised : FacialEmotion.Neutral;
    }

    /// <summary>헤딩 "INT. 장소 - 시간" → (장소, 시간, 실외).</summary>
    public static (string Location, string Time, bool Exterior) ParseHeading(string heading)
    {
        var h = heading.Trim();
        var exterior = h.StartsWith("EXT", StringComparison.OrdinalIgnoreCase);
        var dot = h.IndexOf('.');
        var rest = dot >= 0 && dot < 8 ? h[(dot + 1)..].Trim() : h;
        var dash = rest.LastIndexOf(" - ", StringComparison.Ordinal);
        return dash > 0 ? (rest[..dash].Trim(), rest[(dash + 3)..].Trim(), exterior) : (rest, "", exterior);
    }

    private static string TimeEnglish(string t) => t switch
    {
        "밤" or "NIGHT" => "at night", "아침" or "MORNING" => "in the early morning", "해질녘" or "DUSK" => "at golden dusk", "낮" or "DAY" => "in daylight", _ => "",
    };

    /// <summary>대본 → 컷 목록(프로젝트 미변경). 보고서 동반.</summary>
    public (List<StoryboardScene> Cuts, ConvertReport Report) Convert(ScreenplayDoc doc, WorldBible? bible, ConvertOptions? options = null)
    {
        var o = options ?? new ConvertOptions();
        var cuts = new List<StoryboardScene>(Math.Min(o.MaxCuts, doc.Scenes.Count * 2));
        var cues = new List<VoiceCue>();
        var themeKeys = new List<string>();
        int dialogueCues = 0, lipCuts = 0, matched = 0;
        var maxCut = Math.Clamp(o.MaxCutSeconds, 2, 15);
        double clock = 0;
        foreach (var s in doc.Scenes)
        {
            if (cuts.Count >= o.MaxCuts) break;
            var (loc, time, exterior) = ParseHeading(s.Heading);
            var action = new StringBuilder();
            string? camera = null;
            var dialogues = new List<ScriptElement>();
            foreach (var e in s.Elements)
            {
                switch (e.Kind)
                {
                    case ScriptElementKind.Action: if (action.Length > 0) action.Append(' '); action.Append(e.Text.TrimEnd('.')); break;
                    case ScriptElementKind.Camera: camera ??= e.Text; break;
                    case ScriptElementKind.Dialogue: if (!string.IsNullOrWhiteSpace(e.Text)) dialogues.Add(e); break;
                }
            }
            // 테마 시네마틱 매칭
            ThemeCinematics? cine = null;
            if (o.MatchThemes && _themes is not null)
            {
                try
                {
                    var q = action + " " + loc + " " + (bible?.Lore.Genre.ToString() ?? "") + " " + (bible?.Lore.Tone ?? "");
                    var recs = _themes.Recommend(q, 1);
                    if (recs.Count > 0) { cine = recs[0].Theme.Cinematics; themeKeys.Add(recs[0].Theme.Key); matched++; }
                }
                catch (Exception ex) { Program.Log("Screenplay.Theme", ex); }
            }
            // 대사 → 컷 묶음(길이 상한)
            var groups = new List<List<ScriptElement>>();
            if (dialogues.Count == 0) groups.Add(new List<ScriptElement>());
            else
            {
                var cur = new List<ScriptElement>(); double sec = 0;
                foreach (var d in dialogues)
                {
                    var ds = SpeechSeconds(d.Text);
                    if (cur.Count > 0 && sec + ds > maxCut) { groups.Add(cur); cur = new List<ScriptElement>(); sec = 0; }
                    cur.Add(d); sec += ds;
                }
                if (cur.Count > 0) groups.Add(cur);
            }
            var perCut = Math.Max(2, s.Seconds / groups.Count);
            for (var gi = 0; gi < groups.Count && cuts.Count < o.MaxCuts; gi++)
            {
                var group = groups[gi];
                var speakers = new List<CharacterProfile>();
                if (bible is not null)
                    foreach (var d in group)
                        if (bible.Characters.Find(c => c.Name == d.Character) is { } ch && !speakers.Contains(ch)) speakers.Add(ch);
                var onScreen = speakers.Count > 0 && speakers.Exists(static c => c.Role != CharacterRole.Narrator);
                var prompt = BuildPrompt(bible, speakers, action.ToString(), loc, time, exterior, camera, cine, gi);
                var voice = new StringBuilder();
                var emotion = FacialEmotion.Neutral;
                var t0 = clock;
                foreach (var d in group)
                {
                    if (voice.Length > 0) voice.Append(' ');
                    voice.Append(d.Text);
                    var sp = bible?.Characters.Find(c => c.Name == d.Character);
                    var lip = o.LipSync && sp is not null && sp.Role != CharacterRole.Narrator;
                    cues.Add(new VoiceCue(cuts.Count, d.Character ?? "", sp?.VoiceTone ?? "pro", Math.Round(t0, 2), d.Text, lip));
                    t0 += SpeechSeconds(d.Text);
                    dialogueCues++;
                    if (emotion == FacialEmotion.Neutral) emotion = EmotionOf(d.Parenthetical, s.Tension);
                }
                var cut = new StoryboardScene
                {
                    Prompt = prompt,
                    VoiceText = voice.ToString(),
                    Size = o.Vertical ? "720*1280" : "1280*720",
                    UseLipSync = o.LipSync && onScreen && voice.Length > 0,
                    Emotion = speakers.Count > 0 ? emotion : FacialEmotion.Neutral,
                    PresetTension = Math.Clamp(s.Tension, 0, 100),
                    NegativePrompt = cine?.Negative,
                };
                if (cut.UseLipSync) lipCuts++;
                cuts.Add(cut);
                clock += perCut;
            }
        }
        var report = new ConvertReport(doc.Scenes.Count, cuts.Count, dialogueCues, lipCuts, matched, CatalogSize, cues, themeKeys,
            "씬 " + doc.Scenes.Count.ToString(CultureInfo.InvariantCulture) + " → 컷 " + cuts.Count.ToString(CultureInfo.InvariantCulture) + " · 대사 큐 " + dialogueCues.ToString(CultureInfo.InvariantCulture)
            + " · 립싱크 " + lipCuts.ToString(CultureInfo.InvariantCulture) + " · 테마 매칭 " + matched.ToString(CultureInfo.InvariantCulture) + "/" + CatalogSize.ToString(CultureInfo.InvariantCulture));
        return (cuts, report);
    }

    /// <summary>한국어 ≈ 초당 5음절 · 영어 ≈ 초당 2.5단어(최소 1.2초).</summary>
    public static double SpeechSeconds(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        int hangul = 0, words = 1;
        foreach (var c in text)
        {
            if (c >= '가' && c <= '힣') hangul++;
            else if (c == ' ') words++;
        }
        return Math.Max(1.2, hangul > 0 ? hangul / 5.0 : words / 2.5);
    }

    private static string BuildPrompt(WorldBible? bible, List<CharacterProfile> speakers, string action, string loc, string time, bool exterior, string? camera, ThemeCinematics? cine, int part)
    {
        var sb = new StringBuilder(320);
        if (speakers.Count > 0)
        {
            for (var i = 0; i < speakers.Count && i < 2; i++)
            {
                if (i > 0) sb.Append(" and ");
                sb.Append(speakers[i].Look);
            }
            sb.Append(", ");
        }
        else if (bible?.Protagonist is { } pro && part == 0) sb.Append(pro.Look).Append(", ");
        sb.Append(action.Length > 160 ? action[..160] : action);
        sb.Append(", ").Append(exterior ? "exterior " : "interior ").Append(loc);
        var te = TimeEnglish(time);
        if (te.Length > 0) sb.Append(' ').Append(te);
        if (bible is not null && bible.Lore.VisualMotif.Length > 0) sb.Append(", motif: ").Append(bible.Lore.VisualMotif);
        if (cine is not null) sb.Append(", ").Append(ThemeCinematicsCatalog.LightingSnippet(cine.Lighting)).Append(", ").Append(cine.Lens);
        if (!string.IsNullOrWhiteSpace(camera)) sb.Append(", ").Append(camera);
        sb.Append(", cinematic, highly detailed");
        if (part > 0) sb.Append(", reverse angle");
        return sb.ToString();
    }

    /// <summary>프로젝트에 적용(교체/추가) — 제목·세로 해상도·주인공 톤/언어 설정. 반환 = 보고서.</summary>
    public ConvertReport Apply(StoryboardProject project, ScreenplayDoc doc, WorldBible? bible, ConvertOptions? options = null)
    {
        var o = options ?? new ConvertOptions();
        var (cuts, report) = Convert(doc, bible, o);
        if (!o.Append) project.Scenes.Clear();
        foreach (var c in cuts) project.Scenes.Add(c);
        if (!string.IsNullOrWhiteSpace(doc.Title) && !o.Append) project.Title = "📜 " + doc.Title;
        if (o.Vertical) project.ExportResolution = "1080x1920";
        if (bible?.Protagonist is { } pro) project.VoiceTone = pro.VoiceTone;
        project.VoiceLanguage = o.Language is "ko" or "en" or "de" or "zh" or "ja" ? o.Language : "ko";
        if (string.IsNullOrWhiteSpace(project.CoverTitle) && bible is not null) project.CoverTitle = bible.Lore.Title;
        LastReport = report;
        LastAction = "🎬 " + report.Message;
        Raise();
        return report;
    }

    private void Raise()
    {
        var h = OnChange;
        if (h is null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action)d)(); } catch (Exception ex) { Program.Log("Screenplay.OnChange", ex); }
        }
    }
}
