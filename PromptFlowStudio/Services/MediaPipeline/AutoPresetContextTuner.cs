using System.Globalization;
using System.Text;
using PromptFlowStudio.Services.Storyboard;

namespace PromptFlowStudio.Services.MediaPipeline;

public enum ContentGenre : byte { General, Action, Horror, Romance, Comedy, Documentary, Nature, SciFi, Fantasy, Kids, Commercial, Sports, MusicVideo, Food }
public enum TimeOfDay : byte { Unspecified, Dawn, Day, GoldenHour, Night, Indoor }
public enum ShotSubject : byte { Scene, Person, Baby, Animal, Vehicle, Product, Landscape, Food }

/// <summary>오토 튜닝 결과 — 카메라/조명/FPS/템포/캐릭터 프롬프트 + 근거.</summary>
public sealed record AutoPreset(
    ContentGenre Genre, TimeOfDay Time, ShotSubject Subject, float Energy, float Valence,
    string CameraAngle, string CameraMove, int LensMm, string Lighting, int Fps, int TempoBpm, double ShotSeconds,
    string Size, bool Portrait, SceneMode Mode, FacialEmotion Emotion, double EmotionIntensity, int Tension,
    string CharacterPrompt, string StyleTags, string Negative, string TunedPrompt, float Confidence, IReadOnlyList<string> Reasons)
{
    public string Summary => CameraAngle + " · " + CameraMove + " · " + LensMm.ToString(CultureInfo.InvariantCulture) + "mm · " + Lighting + " · "
        + Fps.ToString(CultureInfo.InvariantCulture) + "fps · " + TempoBpm.ToString(CultureInfo.InvariantCulture) + "BPM";
}

/// <summary>
/// [PromptFlow Studio v16.57.0] ✨ 지능형 프롬프트 &amp; 스토리보드 파라미터 자율 최적화기(Pure BCL · 규칙 NLU + 이미지 통계 비전).
///   ▸ NLU: 한/영 어휘 가중 사전 → 장르(13) · 시간대 · 피사체 · 에너지(동작 강도) · 정서가(긍/부정) · 명시 지시(슬로모/드론/클로즈업/세로).
///   ▸ 비전: <see cref="ImageStats"/>(휘도 · 대비 · 채도 · 색온도 · 엣지 · 피부 · 명암) + 프로브(비율 · 해상도) → 시간대/조명/피사체/세로 구도 보정.
///   ▸ 결정: 장르 × 에너지 → 카메라 앵글·무브·렌즈 · 조명 · FPS(24/30/60/120 슬로모) · BGM 템포 · 컷 길이 · 표정 · 긴장도 · 캐릭터/스타일/네거티브 프롬프트.
///   ▸ 적용: <see cref="ApplyToScene"/> 는 사용자가 이미 지정한 값은 보존(빈 필드만 자동 채움) · 프롬프트 본문은 opt-in.
/// </summary>
public sealed class AutoPresetContextTuner
{
    private sealed record Lex(string[] Keys, ContentGenre Genre, float Energy, float Valence);

    private static readonly Lex[] Genres =
    {
        new(new[] { "액션", "추격", "폭발", "전투", "싸움", "질주", "action", "chase", "explosion", "fight", "battle" }, ContentGenre.Action, 0.9f, 0f),
        new(new[] { "공포", "호러", "귀신", "유령", "괴물", "어둠 속", "horror", "ghost", "creepy", "monster", "haunted" }, ContentGenre.Horror, 0.6f, -0.8f),
        new(new[] { "로맨스", "사랑", "연인", "키스", "데이트", "romance", "love", "couple", "kiss", "wedding", "웨딩" }, ContentGenre.Romance, 0.3f, 0.8f),
        new(new[] { "코미디", "웃긴", "개그", "유쾌", "funny", "comedy", "silly", "hilarious" }, ContentGenre.Comedy, 0.6f, 0.9f),
        new(new[] { "다큐", "인터뷰", "기록", "documentary", "interview", "reportage" }, ContentGenre.Documentary, 0.2f, 0.1f),
        new(new[] { "숲", "바다", "산", "폭포", "초원", "자연", "forest", "ocean", "mountain", "waterfall", "nature", "meadow" }, ContentGenre.Nature, 0.2f, 0.6f),
        new(new[] { "우주", "사이버", "네온", "로봇", "미래", "sci-fi", "cyberpunk", "neon", "robot", "spaceship", "futuristic" }, ContentGenre.SciFi, 0.6f, 0.2f),
        new(new[] { "판타지", "마법", "용", "요정", "성", "fantasy", "magic", "dragon", "fairy", "castle", "wizard" }, ContentGenre.Fantasy, 0.5f, 0.5f),
        new(new[] { "아기", "아이", "어린이", "동화", "baby", "toddler", "kid", "child", "nursery" }, ContentGenre.Kids, 0.3f, 0.9f),
        new(new[] { "광고", "제품", "브랜드", "패키지", "commercial", "product", "brand", "advert", "packshot" }, ContentGenre.Commercial, 0.4f, 0.6f),
        new(new[] { "축구", "농구", "경기", "선수", "스포츠", "soccer", "basketball", "sports", "athlete", "match", "stadium" }, ContentGenre.Sports, 0.9f, 0.4f),
        new(new[] { "뮤직비디오", "댄스", "춤", "콘서트", "무대", "music video", "dance", "concert", "stage", "k-pop", "케이팝" }, ContentGenre.MusicVideo, 0.8f, 0.6f),
        new(new[] { "음식", "요리", "디저트", "커피", "레스토랑", "food", "dish", "dessert", "coffee", "cooking", "restaurant" }, ContentGenre.Food, 0.2f, 0.7f),
    };

    private static readonly (string[] Keys, TimeOfDay T)[] Times =
    {
        (new[] { "새벽", "아침 안개", "dawn", "sunrise", "일출" }, TimeOfDay.Dawn),
        (new[] { "노을", "석양", "황혼", "골든아워", "sunset", "golden hour", "dusk" }, TimeOfDay.GoldenHour),
        (new[] { "밤", "야경", "한밤", "달빛", "night", "midnight", "moonlight" }, TimeOfDay.Night),
        (new[] { "실내", "방 안", "카페", "사무실", "스튜디오", "indoor", "room", "office", "studio", "cafe" }, TimeOfDay.Indoor),
        (new[] { "한낮", "정오", "맑은 날", "noon", "daylight", "sunny" }, TimeOfDay.Day),
    };

    private static readonly (string[] Keys, ShotSubject S)[] Subjects =
    {
        (new[] { "아기", "baby", "toddler", "infant" }, ShotSubject.Baby),
        (new[] { "강아지", "고양이", "동물", "새", "말", "dog", "cat", "animal", "bird", "horse", "puppy" }, ShotSubject.Animal),
        (new[] { "자동차", "차량", "오토바이", "기차", "car", "vehicle", "motorcycle", "train", "truck" }, ShotSubject.Vehicle),
        (new[] { "제품", "병", "시계", "화장품", "product", "bottle", "watch", "sneaker", "cosmetic" }, ShotSubject.Product),
        (new[] { "음식", "요리", "디저트", "food", "dish", "dessert", "burger", "sushi" }, ShotSubject.Food),
        (new[] { "남자", "여자", "소녀", "소년", "사람", "인물", "주인공", "선수", "athlete", "player", "man", "woman", "girl", "boy", "person", "character", "portrait" }, ShotSubject.Person),
        (new[] { "풍경", "도시", "산", "바다", "landscape", "city", "skyline", "mountain", "ocean" }, ShotSubject.Landscape),
    };

    private static readonly string[] EnergyUp = { "빠르게", "달리", "뛰", "폭발", "격렬", "fast", "running", "rush", "explode", "intense", "dynamic" };
    private static readonly string[] EnergyDown = { "천천히", "고요", "잔잔", "평화", "slow", "calm", "quiet", "peaceful", "gentle", "serene" };
    private static readonly string[] Positive = { "행복", "웃", "즐거", "따뜻", "희망", "happy", "smile", "joy", "warm", "hope", "cheerful" };
    private static readonly string[] Negative = { "슬픔", "눈물", "외로", "분노", "절망", "sad", "tears", "lonely", "angry", "despair", "grief" };

    private long _tunes;

    public event Action? OnChange;

    public long Tunes => Interlocked.Read(ref _tunes);
    public AutoPreset? Last { get; private set; }
    public string? LastAction { get; private set; }

    private static int Hits(string text, string[] keys)
    {
        var n = 0;
        foreach (var k in keys) if (text.Contains(k, StringComparison.OrdinalIgnoreCase)) n++;
        return n;
    }

    /// <summary>텍스트 프롬프트(+선택 이미지 통계/프로브) → 오토 프리셋.</summary>
    public AutoPreset Tune(string? prompt, ImageStats? image = null, MediaProbe? probe = null)
    {
        var text = (prompt ?? "").Trim();
        var reasons = new List<string>();
        var genre = ContentGenre.General;
        float bestScore = 0, energy = 0.45f, valence = 0.2f;
        foreach (var g in Genres)
        {
            var h = Hits(text, g.Keys);
            if (h > bestScore) { bestScore = h; genre = g.Genre; energy = g.Energy; valence = g.Valence; }
        }
        if (genre != ContentGenre.General) reasons.Add("장르 " + GenreLabel(genre) + "(키워드 " + bestScore.ToString(CultureInfo.InvariantCulture) + ")");

        var time = TimeOfDay.Unspecified;
        foreach (var (keys, t) in Times) if (Hits(text, keys) > 0) { time = t; break; }
        var subject = ShotSubject.Scene;
        foreach (var (keys, s) in Subjects) if (Hits(text, keys) > 0) { subject = s; break; }

        var up = Hits(text, EnergyUp); var down = Hits(text, EnergyDown);
        energy = Math.Clamp(energy + 0.15f * up - 0.15f * down, 0f, 1f);
        var pos = Hits(text, Positive); var neg = Hits(text, Negative);
        valence = Math.Clamp(valence + 0.3f * pos - 0.3f * neg, -1f, 1f);
        if (up + down > 0) reasons.Add("에너지 " + energy.ToString("0.00", CultureInfo.InvariantCulture));
        if (pos + neg > 0) reasons.Add("정서가 " + valence.ToString("0.00", CultureInfo.InvariantCulture));

        var slowMo = Hits(text, new[] { "슬로모", "슬로우", "slow motion", "slow-mo", "slowmo" }) > 0;
        var drone = Hits(text, new[] { "드론", "항공", "aerial", "drone", "bird's-eye" }) > 0;
        var close = Hits(text, new[] { "클로즈업", "근접", "close-up", "closeup", "macro", "매크로" }) > 0;
        var vertical = Hits(text, new[] { "세로", "쇼츠", "릴스", "vertical", "shorts", "reels", "9:16" }) > 0;

        var portrait = vertical;
        var conf = text.Length == 0 ? 0.2f : Math.Min(1f, 0.35f + 0.15f * (bestScore + (time != TimeOfDay.Unspecified ? 1 : 0) + (subject != ShotSubject.Scene ? 1 : 0)));
        if (image is { Valid: true } st)
        {
            conf = Math.Min(1f, conf + 0.25f);
            if (time == TimeOfDay.Unspecified)
            {
                time = st.MeanLuma < 0.22f ? TimeOfDay.Night : st.Warmth > 0.12f && st.MeanLuma < 0.6f ? TimeOfDay.GoldenHour : st.MeanLuma > 0.55f ? TimeOfDay.Day : TimeOfDay.Indoor;
                reasons.Add("이미지 시간대 추정 " + TimeLabel(time) + "(휘도 " + st.MeanLuma.ToString("0.00", CultureInfo.InvariantCulture) + " · 색온도 " + st.Warmth.ToString("0.00", CultureInfo.InvariantCulture) + ")");
            }
            if (subject == ShotSubject.Scene && st.SkinRatio > 0.12f) { subject = ShotSubject.Person; reasons.Add("피부 비율 " + (st.SkinRatio * 100).ToString("0", CultureInfo.InvariantCulture) + "% → 인물"); }
            if (subject == ShotSubject.Scene && st.EdgeDensity < 0.12f && st.Saturation > 0.25f) subject = ShotSubject.Landscape;
            energy = Math.Clamp(energy + (st.EdgeDensity - 0.2f) * 0.6f + (st.Contrast - 0.2f) * 0.5f, 0f, 1f);
            valence = Math.Clamp(valence + (st.MeanLuma - 0.45f) * 0.6f + (st.Saturation - 0.3f) * 0.4f, -1f, 1f);
        }
        if (probe is { Width: > 0 } pr)
        {
            if (pr.IsPortrait && !vertical) { portrait = true; reasons.Add("세로 원본(" + pr.Width.ToString(CultureInfo.InvariantCulture) + "×" + pr.Height.ToString(CultureInfo.InvariantCulture) + ") → 9:16"); }
            if (pr.Kind == MediaKind.Video && pr.Fps >= 50) { slowMo = slowMo || pr.Fps >= 100; reasons.Add("원본 " + pr.Fps.ToString("0", CultureInfo.InvariantCulture) + "fps 유지"); }
        }

        // ── 카메라 ──
        string angle, move; int lens;
        if (drone || genre is ContentGenre.Nature && subject == ShotSubject.Landscape) { angle = "aerial wide shot"; move = "slow drone push-in"; lens = 24; }
        else if (close || subject is ShotSubject.Product or ShotSubject.Food) { angle = "close-up"; move = genre == ContentGenre.Commercial ? "slow orbit" : "gentle dolly-in"; lens = subject == ShotSubject.Food ? 100 : 85; }
        else if (genre == ContentGenre.Horror) { angle = "low angle"; move = "slow creeping push-in"; lens = 28; }
        else if (genre is ContentGenre.Action or ContentGenre.Sports) { angle = "dynamic tracking shot"; move = "handheld tracking"; lens = 35; }
        else if (genre == ContentGenre.MusicVideo) { angle = "medium shot"; move = "orbit with whip pans"; lens = 35; }
        else if (subject is ShotSubject.Person or ShotSubject.Baby) { angle = "eye-level medium close-up"; move = energy > 0.6f ? "steadicam follow" : "slow dolly-in"; lens = 50; }
        else if (genre == ContentGenre.Documentary) { angle = "eye-level medium shot"; move = "static tripod"; lens = 35; }
        else { angle = "wide establishing shot"; move = energy > 0.6f ? "fast crane up" : "slow pan"; lens = 28; }

        // ── 조명 ──
        var lighting = (genre, time) switch
        {
            (ContentGenre.Horror, _) => "low-key chiaroscuro, cold moonlight rim",
            (ContentGenre.SciFi, _) => "neon cyan-magenta practicals, volumetric haze",
            (_, TimeOfDay.GoldenHour) => "warm golden hour backlight, soft flare",
            (_, TimeOfDay.Night) => "moody night lighting, motivated practicals",
            (_, TimeOfDay.Dawn) => "soft blue-hour dawn light, mist",
            (_, TimeOfDay.Indoor) => "soft window key light, warm practicals",
            (ContentGenre.Commercial or ContentGenre.Food, _) => "clean high-key softbox, specular highlights",
            (ContentGenre.Kids, _) => "bright soft daylight, pastel fill",
            _ => valence < -0.3f ? "overcast diffuse light, desaturated" : "natural soft daylight",
        };

        var fps = slowMo ? 120 : genre is ContentGenre.Sports or ContentGenre.Action ? 60 : genre is ContentGenre.Documentary or ContentGenre.Commercial ? 30 : 24;
        if (probe is { Kind: MediaKind.Video, Fps: >= 50 } vp && !slowMo) fps = Math.Max(fps, (int)Math.Round(vp.Fps));
        var tempo = (int)Math.Round(70 + 80 * energy + (genre == ContentGenre.MusicVideo ? 10 : 0) - (genre == ContentGenre.Horror ? 10 : 0));
        var shot = Math.Round(Math.Clamp(6.0 - 3.5 * energy, 2.5, 6.0), 1);
        var size = portrait ? "720*1280" : "1280*720";
        var mode = image is { Valid: true } || probe is { Kind: MediaKind.Image } ? SceneMode.ImageToVideo : SceneMode.TextToVideo;
        var emotion = genre == ContentGenre.Horror ? FacialEmotion.Surprised
            : valence > 0.6f && energy > 0.6f ? FacialEmotion.Giggle
            : valence > 0.3f ? FacialEmotion.HappySmile
            : valence < -0.3f ? FacialEmotion.Pout
            : genre is ContentGenre.SciFi or ContentGenre.Fantasy or ContentGenre.Documentary ? FacialEmotion.Curious : FacialEmotion.Neutral;
        var intensity = Math.Round(Math.Clamp(0.4 + 0.5 * Math.Abs(valence) * 0.6 + 0.3 * energy, 0.3, 1.0), 2);
        var tension = (int)Math.Round(Math.Clamp(energy * 70 + (valence < 0 ? -valence * 30 : 0), 0, 100));

        var character = subject switch
        {
            ShotSubject.Baby => "adorable baby with consistent facial features, soft skin texture, natural expression",
            ShotSubject.Person => "consistent main character, detailed facial features, natural skin texture, expressive eyes",
            ShotSubject.Animal => "realistic animal with detailed fur, consistent markings",
            ShotSubject.Product => "hero product, crisp edges, accurate materials and logo placement",
            ShotSubject.Food => "appetizing food, glistening textures, steam",
            ShotSubject.Vehicle => "detailed vehicle, reflective paint, accurate proportions",
            _ => "",
        };
        var style = GenreStyle(genre);
        var negative = "blurry, low resolution, watermark, text artifacts, deformed hands, extra fingers, flicker, jitter, warped face"
            + (subject is ShotSubject.Person or ShotSubject.Baby ? ", identity drift, inconsistent face" : "")
            + (genre == ContentGenre.Kids ? ", scary, violent" : "");

        var sb = new StringBuilder(text.TrimEnd(',', ' ', '.'));
        if (sb.Length > 0) sb.Append(", ");
        sb.Append(angle).Append(", ").Append(move).Append(", ").Append(lens.ToString(CultureInfo.InvariantCulture)).Append("mm lens, ").Append(lighting);
        if (character.Length > 0) sb.Append(", ").Append(character);
        if (style.Length > 0) sb.Append(", ").Append(style);
        if (slowMo) sb.Append(", slow motion");

        reasons.Add("카메라 " + angle + " · " + move);
        reasons.Add("FPS " + fps.ToString(CultureInfo.InvariantCulture) + " · 템포 " + tempo.ToString(CultureInfo.InvariantCulture) + "BPM · 컷 " + shot.ToString("0.0", CultureInfo.InvariantCulture) + "s");

        var preset = new AutoPreset(genre, time, subject, energy, valence, angle, move, lens, lighting, fps, tempo, shot, size, portrait, mode, emotion, intensity, tension,
            character, style, negative, sb.ToString(), conf, reasons);
        Interlocked.Increment(ref _tunes);
        Last = preset;
        LastAction = "✨ 자동 셋팅 · " + GenreLabel(genre) + " · " + preset.Summary;
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("AutoTuner.OnChange", ex); }
        return preset;
    }

    /// <summary>미디어 수신 결과 기반 튜닝(이미지 통계 · 프로브 결합).</summary>
    public AutoPreset Tune(string? prompt, MediaIngestResult media)
        => Tune(prompt, media.Stats.Valid ? media.Stats : null, media.Probe);

    private static string GenreStyle(ContentGenre g) => g switch
    {
        ContentGenre.Action => "high-energy cinematic, motion blur, anamorphic",
        ContentGenre.Horror => "eerie atmosphere, desaturated teal, film grain",
        ContentGenre.Romance => "dreamy shallow depth of field, soft bokeh, warm tones",
        ContentGenre.Comedy => "bright saturated colors, playful framing",
        ContentGenre.Documentary => "naturalistic, observational, true-to-life color",
        ContentGenre.Nature => "pristine nature cinematography, rich greens, crisp detail",
        ContentGenre.SciFi => "futuristic cinematic, high contrast, chrome reflections",
        ContentGenre.Fantasy => "epic fantasy, ethereal glow, painterly detail",
        ContentGenre.Kids => "storybook warmth, pastel palette, gentle",
        ContentGenre.Commercial => "premium commercial look, clean background, studio quality",
        ContentGenre.Sports => "broadcast sports look, crisp motion, stadium atmosphere",
        ContentGenre.MusicVideo => "stylized music video, rhythmic cuts, vivid color grade",
        ContentGenre.Food => "food cinematography, macro detail, warm appetizing tones",
        _ => "cinematic, highly detailed, smooth camera movement",
    };

    public static string GenreLabel(ContentGenre g) => g switch
    {
        ContentGenre.Action => "액션", ContentGenre.Horror => "호러", ContentGenre.Romance => "로맨스", ContentGenre.Comedy => "코미디",
        ContentGenre.Documentary => "다큐", ContentGenre.Nature => "자연", ContentGenre.SciFi => "SF", ContentGenre.Fantasy => "판타지",
        ContentGenre.Kids => "키즈", ContentGenre.Commercial => "광고", ContentGenre.Sports => "스포츠", ContentGenre.MusicVideo => "뮤직비디오",
        ContentGenre.Food => "푸드", _ => "일반",
    };

    public static string TimeLabel(TimeOfDay t) => t switch
    {
        TimeOfDay.Dawn => "새벽", TimeOfDay.Day => "낮", TimeOfDay.GoldenHour => "골든아워", TimeOfDay.Night => "밤", TimeOfDay.Indoor => "실내", _ => "미지정",
    };

    /// <summary>
    /// 컷에 자동 셋팅 적용 — 사용자 지정 값은 보존. replacePrompt=true 일 때만 프롬프트 본문을 튜닝 프롬프트로 교체.
    /// 반환 = 변경된 필드 수.
    /// </summary>
    public static int ApplyToScene(StoryboardScene scene, AutoPreset p, bool replacePrompt = false)
    {
        var n = 0;
        if (scene.Size is "1280*720" or "" && p.Size != scene.Size) { scene.Size = p.Size; n++; }
        if (string.IsNullOrWhiteSpace(scene.NegativePrompt)) { scene.NegativePrompt = p.Negative; n++; }
        if (scene.Emotion == FacialEmotion.Neutral && p.Emotion != FacialEmotion.Neutral) { scene.Emotion = p.Emotion; scene.EmotionIntensity = p.EmotionIntensity; n++; }
        if (scene.PresetTension is null) { scene.PresetTension = p.Tension; n++; }
        if (p.Mode == SceneMode.ImageToVideo && scene.Mode != SceneMode.ImageToVideo && !string.IsNullOrEmpty(scene.ImagePath)) { scene.Mode = SceneMode.ImageToVideo; n++; }
        if (replacePrompt && !string.Equals(scene.Prompt, p.TunedPrompt, StringComparison.Ordinal)) { scene.Prompt = p.TunedPrompt; n++; }
        return n;
    }

    /// <summary>프로젝트 단위 자동 셋팅(세로 해상도 · 품질 기본값) — 사용자 값 보존. 반환 = 변경 수.</summary>
    public static int ApplyToProject(StoryboardProject project, AutoPreset p)
    {
        var n = 0;
        if (p.Portrait && project.ExportResolution == "1280x720") { project.ExportResolution = "720x1280"; n++; }
        if (!project.EnableAudioDucking) { project.EnableAudioDucking = true; n++; }
        return n;
    }
}
