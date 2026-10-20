using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PromptFlowStudio.Services.Media;   // KineticCaptionWord / FoleySpec / VideoTransition / TransitionSafety

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>아기 숏츠 테마 프리셋(갤러리 카드 + 6컷 완성형 데이터).</summary>
public sealed record BabyTheme(string Key, string Emoji, string Title, string Hook, string Example, double Speed, IReadOnlyList<BabyCut> Cuts)
{
    /// <summary>v16.7.0 시네마틱 메타 일괄(카메라·조명·렌즈·SFX·강도) — authored 데이터에서 산출 · 인스턴스 캐시.</summary>
    public ThemeCinematics Cinematics => ThemeCinematicsCatalog.For(this);
    /// <summary>v16.7.0 권장 카메라 무빙(Orbit·Dolly Zoom 등).</summary>
    public CameraMotion RecommendedCameraMotion => Cinematics.Camera;
    /// <summary>v16.7.0 권장 조명(GoldenHour·StudioSoft 등).</summary>
    public ThemeLighting RecommendedLighting => Cinematics.Lighting;
    /// <summary>v16.7.0 권장 효과음 키워드(최대 3).</summary>
    public IReadOnlyList<string> SuggestedSfx => Cinematics.Sfx;
    /// <summary>v16.7.0 최적 모션 강도(2~4).</summary>
    public int OptimalMotionIntensity => Cinematics.MotionIntensity;
}

/// <summary>프리셋의 컷 1개(authored) — 속마음 대사·골드 단어·Wan 프롬프트·긴장도·들어오는 전환·폴리 스펙.</summary>
public sealed record BabyCut(string Voice, string Gold, string Prompt, int Tension, string XfadeInto, int XfadeMs, IReadOnlyList<FoleySpec> Foley);

/// <summary>
/// [PromptFlowStudio] 아기 숏츠 원클릭 테마 프리셋 엔진 — 순수 BCL(외부 NuGet 0).
///   ▸ 10가지 아기 흥행 테마의 <b>완성형 6컷 스토리보드</b>를 코드 데이터로 보유: 컷별 한국어 속마음 대사·키네틱 골드 단어,
///     Wan 2.1 극사실 4K 영문 프롬프트(자연광/매크로/배속), xfade 스마트 트랜지션 + 3초 반전 무한 루프(매치컷),
///     Tension(0~100) + BGM Mute/Ducking, Gen-Foley 3D 공간 SFX 스펙(타입/시점/팬).
///   ▸ <see cref="ApplyPresetToStoryboard"/> — 사용자가 올린 아기 사진(프로젝트 앵커)의 특징을 유지하며(전 컷 i2v 앵커 바인딩) 선택 테마로
///     컷 카드를 즉시 채우고 4대 후처리 토글(스마트 트랜지션/키네틱 자막/감정선 BGM/Gen-Foley)을 자동 ON.
///   ▸ authored 데이터는 <see cref="StoryboardScene"/>에 저장 → 병합이 <b>Gemini 없이 오프라인</b>으로 키네틱/전환/텐션/폴리를 적용(1초 자동완성).
///   ▸ 키네틱 타임코드는 <see cref="BuildKineticWords"/>가 대사를 단어로 쪼개 균등 분배(골드 단어 highlight) — 형태소 분석기 불필요.
/// </summary>
public sealed class BabyThemePresetService
{
    private const string V = "720*1280";   // 세로 쇼츠 컷 해상도(Wan)

    /// <summary>갤러리에 노출할 10개 테마(순서 = 표시 순서).</summary>
    public IReadOnlyList<BabyTheme> Themes { get; } = Build();

    /// <summary>키로 테마 조회(없으면 null).</summary>
    public BabyTheme? Get(string key) => Themes.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 선택 테마로 스토리보드를 자동 구성한다 — 앵커(아기 사진)는 유지하고 컷 6개를 authored 데이터로 교체, 4대 후처리 토글 ON.
    ///   기존 저장/열기와 100% 호환(모두 직렬화 가능한 필드). 실패 없이 항상 적용(빈 키는 무시).
    /// </summary>
    public bool ApplyPresetToStoryboard(StoryboardProject project, string themeKey)
    {
        var theme = Get(themeKey);
        if (project is null || theme is null) return false;

        // 컷 교체(앵커·제목류는 프로젝트 레벨에서 유지/설정).
        project.Scenes.Clear();
        var n = theme.Cuts.Count;
        for (var i = 0; i < n; i++)
        {
            var c = theme.Cuts[i];
            project.Scenes.Add(new StoryboardScene
            {
                // 앵커가 있으면 전 컷을 i2v 로(아기 특징 유지) — CharacterAnchorService 가 실제 참조 이미지를 바인딩.
                Mode = string.IsNullOrWhiteSpace(project.AnchorImagePath) ? SceneMode.TextToVideo : SceneMode.ImageToVideo,
                Prompt = c.Prompt,
                VoiceText = c.Voice,
                Size = V,
                PresetTension = Math.Clamp(c.Tension, 0, 100),
                PresetKineticWords = BuildKineticWords(c.Voice, c.Gold),
                PresetFoley = c.Foley.ToList(),
                // 이 컷으로 들어오는 전환(경계 xfade). cut0 은 루프(마지막→첫) 매치컷 메타.
                EnterTransition = new VideoTransition(TransitionSafety.SafeXfade(c.XfadeInto), Math.Clamp(c.XfadeMs, 200, 1500)),
            });
        }

        // 프로젝트 세팅 — 세로 쇼츠 + 배속 + 4대 후처리 토글 ON + 프리셋 메타.
        project.Title = theme.Emoji + " " + theme.Title;
        project.ExportResolution = "1080x1920";
        project.PlaybackSpeed = Math.Clamp(theme.Speed <= 0 ? 1.0 : theme.Speed, 0.5, 2.0);
        project.CoverTitle = theme.Hook;
        project.UseSmartTransitions = true;
        project.UseKineticCaptions = true;
        project.UseEmotionPacing = true;
        project.UseGenFoley = true;
        project.UseVisualSfxBadges = true;   // 음소거 시청 대응 시각 효과음 뱃지
        project.EnableAudioDucking = true;
        project.UseFadeEffect = false;   // 컷 전환은 xfade 가 담당
        project.IsBabyPreset = true;
        project.PresetKey = theme.Key;
        return true;
    }

    /// <summary>대사를 단어로 쪼개 컷 구간 [0.3s, ~clipMs-0.3s]에 균등 분배, 골드 단어는 highlight=true. 밀리초 키네틱 타임코드 즉시 생성.</summary>
    public static List<KineticCaptionWord> BuildKineticWords(string? voice, string? gold, int clipMs = 4600)
    {
        var words = (voice ?? "").Split(new[] { ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var list = new List<KineticCaptionWord>();
        if (words.Length == 0) return list;

        var startAt = 300;
        var endAt = Math.Max(startAt + 500, clipMs - 300);
        double slot = (endAt - startAt) / (double)words.Length;
        var goldClean = Clean(gold);

        for (var i = 0; i < words.Length; i++)
        {
            var s = startAt + (int)Math.Round(i * slot);
            var e = s + (int)Math.Round(slot * 0.94);
            if (e <= s) e = s + 220;
            var hl = goldClean.Length > 0 && Clean(words[i]).Contains(goldClean, StringComparison.Ordinal);
            list.Add(new KineticCaptionWord(words[i], s, e, hl));
        }
        return list;
    }

    private static string Clean(string? w)
    {
        if (string.IsNullOrEmpty(w)) return "";
        var sb = new StringBuilder(w.Length);
        foreach (var c in w) if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    // ── SFX 스펙 팩토리(가독성) ──
    private static FoleySpec Fx(string type, int ms, double pan) => new(type, ms, pan);
    private static IReadOnlyList<FoleySpec> Sfx(params FoleySpec[] f) => f;

    // ── 10개 테마 데이터 ──
    private static IReadOnlyList<BabyTheme> Build() => new List<BabyTheme>
    {
        // 1. 레몬 시식가
        new("lemon","🍋","레몬 시식가",
            "0.1초 뒤, 이 진지한 표정이 와르르 무너집니다 😂","생애 첫 레몬을 시식하는 아기 미식가 — 신맛의 배신 → 이상한 중독 → 루프.",1.0,
            new List<BabyCut>{
              new("흠 오늘의 시식 메뉴는 이것인가","시식","Bright natural window sunlight, extreme macro close-up of a chubby-cheeked baby holding up a glistening yellow lemon slice, furrowed serious brow, curious eyes inspecting it like a tiny food critic, soft skin texture, catchlights, shallow depth of field, 4K, photorealistic",30,"fade",450, Sfx(Fx("pluck",200,0),Fx("cute",1600,0))),
              new("겉은 상큼해 보이는군 일단 한 입","한 입","Macro zoom on a baby's tiny lips and tongue cautiously touching a bright lemon slice, glistening juice, natural sunlight, soft-focus background, chubby cheeks, hyper-detailed skin, 4K, warm, anticipation, photorealistic",45,"zoomin",400, Sfx(Fx("suspense",1200,0))),
              new("이건 배신이야 세상에 이런 맛이","배신","Extreme close-up, a baby's entire face scrunching in sour shock, eyes squeezed shut, lips puckered tight, cheeks quivering, a tiny tear sparkle, bright natural light, subtle fast-motion shiver, hyper-detailed, 4K, comedic, photorealistic",80,"slideleft",300, Sfx(Fx("crash",120,0),Fx("boing",320,0.3),Fx("squeak",700,-0.2))),
              new("그런데 왜 자꾸 생각나지 한 번만 더","자꾸","Close-up, a baby's face melting from a sour pucker into a delighted drooly grin, sparkling wide eyes, one chubby hand reaching back toward the lemon, warm golden window light, soft bokeh, 4K, joyful, photorealistic",72,"hblur",420, Sfx(Fx("giggle",300,0),Fx("sparkle",1500,0.4))),
              new("인정 오늘부터 넌 내 최애다","최애","Fast-motion, a giggling baby happily gnawing and chomping a lemon slice, juice dripping, messy joyful chaos, bright natural sunlight, vivid colors, playful energy, 4K, photorealistic, comedic",90,"slideleft",300, Sfx(Fx("chomp",200,0),Fx("boing",900,0.3),Fx("pop",1500,-0.3))),
              new("자 다음 메뉴는 무엇이려나","다음","Bright natural window sunlight, extreme macro of the same baby holding up a fresh lemon slice, furrowed serious brow, curious inspecting eyes, identical framing and lighting to the opening shot for a seamless loop, 4K, photorealistic",30,"fade",450, Sfx(Fx("pluck",300,0),Fx("cute",1500,0))),
            }),

        // 2. 낮잠 거부
        new("naptime","😪","낮잠 거부",
            "절대 안 자겠다던 아기의 최후 3초 😴","졸림 vs 자존심 — 필사적으로 눈 부릅뜨다 결국 픽 쓰러지는 무한 루프.",1.0,
            new List<BabyCut>{
              new("나는 절대 자지 않아 잠 따위","절대","Bright soft nursery daylight, close-up of a determined baby with wide alert eyes forcing itself to stay awake, tiny fists clenched, chubby cheeks, natural window light, hyper-detailed, 4K, photorealistic",35,"fade",450, Sfx(Fx("cute",300,0),Fx("pop",1400,0.2))),
              new("어른들은 모른다 밤은 짧다는 걸","짧다는","Macro close-up of a baby's big blinking eyes fighting to stay open, long eyelashes, subtle slow blink, warm afternoon sunlight, soft focus, 4K, tender, photorealistic",42,"zoomin",400, Sfx(Fx("suspense",900,0))),
              new("잠깐 눈만 감았다 뜬 거야 진짜","진짜","Extreme close-up, a baby's eyelids drooping heavily then snapping back open in denial, tiny eyebrow raise, natural light, comedic timing, hyper-detailed, 4K, photorealistic",55,"slideleft",320, Sfx(Fx("boing",200,0.3),Fx("ding",700,-0.2))),
              new("고개가 왜 자꾸 무거워지지 이상하네","무거워","Close-up of a baby's head slowly nodding forward then jerking back up, sleepy wobble, soft nursery daylight, gentle fast-motion, 4K, endearing, photorealistic",60,"hblur",420, Sfx(Fx("boing",300,0),Fx("cute",1200,0.3))),
              new("아니 나는 아직 쌩쌩 그대로 픽","픽","Fast-motion, a baby finally collapsing sideways into a soft pillow, instantly asleep mid-motion, peaceful face, bright natural light, comedic sudden drop, 4K, photorealistic",85,"slideleft",300, Sfx(Fx("whoosh",150,0.4),Fx("crash",500,0),Fx("ding",900,0))),
              new("음 아직 안 졸린데 눈만 잠깐","눈만","Bright soft nursery daylight, close-up of the same baby jolting awake with wide determined eyes again, tiny fists clenched, identical framing to the opening for a seamless loop, 4K, photorealistic",35,"fade",450, Sfx(Fx("cute",300,0),Fx("pop",1300,0.2))),
            }),

        // 3. 아기 명탐정 (CSI)
        new("csi","🕵️","아기 명탐정",
            "사라진 쿠키의 진실… 유력 용의자는 본인 🍪","쿠키 도난 사건을 수사하는 진지한 아기 형사 — 범인은 거울 속에.",1.0,
            new List<BabyCut>{
              new("사건 발생 쿠키 한 조각이 사라졌다","사건","Bright natural light, cinematic close-up of a baby with an intensely serious detective face examining an empty cookie plate, furrowed brow, dramatic side light, chubby cheeks, 4K, photorealistic, film-noir mood but bright",40,"fade",450, Sfx(Fx("suspense",200,0),Fx("ding",1300,0))),
              new("현장에 남은 결정적 단서 이 부스러기","단서","Extreme macro of a baby's tiny finger pointing at cookie crumbs on the table, sharp focus, natural sunlight, investigative mood, hyper-detailed, 4K, photorealistic",48,"zoomin",400, Sfx(Fx("ding",300,0),Fx("suspense",1000,0.2))),
              new("범인은 반드시 이 방 안에 있다","범인","Close-up of a baby slowly turning its head with a squinting suspicious glare, dramatic natural light, tension, chubby cheeks, hyper-detailed, 4K, comedic-serious, photorealistic",62,"slideleft",320, Sfx(Fx("drumroll",200,0),Fx("suspense",900,0))),
              new("잠깐 거울 속 저 용의자는 누구","용의자","Close-up of a baby staring at its own reflection in a mirror with a shocked realization, wide eyes, bright natural light, comedic twist, 4K, photorealistic",70,"hblur",420, Sfx(Fx("boing",250,0.3),Fx("ding",600,-0.3))),
              new("입가에 묻은 이 부스러기 자백해라","자백","Extreme close-up, macro on cookie crumbs around a guilty grinning baby's mouth, caught red-handed, bright sunlight, comedic reveal, fast-motion, 4K, photorealistic",88,"slideleft",300, Sfx(Fx("crash",150,0),Fx("giggle",600,0.2),Fx("pop",1100,-0.3))),
              new("사건 종결 다음 사건은 또 어디서","다음","Bright natural light, cinematic close-up of the same baby resuming a serious detective face scanning the room, identical framing to the opening for a seamless loop, 4K, photorealistic",40,"fade",450, Sfx(Fx("suspense",250,0),Fx("ding",1300,0))),
            }),

        // 4. 2배속 펭귄 걸음마
        new("penguin","🐧","펭귄 걸음마",
            "2배속으로 보면 완벽한 펭귄입니다 🐧","아장아장 첫 걸음이 2배속에선 뒤뚱뒤뚱 펭귄 행진 — 넘어지고 또 도전.",2.0,
            new List<BabyCut>{
              new("드디어 오늘 나는 두 발로 선다","두 발","Bright natural sunlight, wide shot of a baby in a puffy outfit standing up wobbling on two feet like a tiny penguin, arms out for balance, soft home background, 4K, adorable, photorealistic",40,"fade",450, Sfx(Fx("cute",300,0),Fx("drumroll",1200,0))),
              new("한 발 그리고 또 한 발 뒤뚱","뒤뚱","Fast-motion, a baby waddling forward with stiff little steps, arms flapping for balance, penguin-like wobble, bright natural light, comedic 2x speed, 4K, photorealistic",55,"zoomin",380, Sfx(Fx("boing",200,-0.4),Fx("boing",500,0.4),Fx("pop",900,0))),
              new("속도를 올린다 아무도 못 막아","속도","Fast-motion, a baby waddling faster across the room, chubby legs pumping, joyful determined face, motion blur, bright sunlight, comedic energy, 4K, photorealistic",70,"slideleft",300, Sfx(Fx("whoosh",150,-0.5),Fx("boing",600,0.3))),
              new("어 어 균형이 흔들 위기다","위기","Close-up of a baby's arms flailing wildly to keep balance, wobbling dramatically, wide surprised eyes, natural light, comedic tension, fast-motion, 4K, photorealistic",78,"hblur",400, Sfx(Fx("suspense",120,0),Fx("boing",400,0.2))),
              new("결국 엉덩방아 하지만 괜찮아 콰당","콰당","Fast-motion, a baby plopping onto its padded bottom then immediately laughing, bounce, bright natural light, comedic crash, 4K, joyful, photorealistic",90,"slideleft",300, Sfx(Fx("crash",180,0),Fx("boing",420,0.3),Fx("giggle",800,0))),
              new("자 다시 도전 두 발로 일어선다","다시","Bright natural sunlight, wide shot of the same baby standing back up wobbling on two feet with arms out, identical framing to the opening for a seamless loop, 4K, photorealistic",40,"fade",450, Sfx(Fx("cute",300,0),Fx("drumroll",1100,0))),
            }),

        // 5. 첫 이유식 먹방
        new("mukbang","🍚","이유식 먹방",
            "생애 첫 이유식… 표정이 다 말해줍니다 🥄","한 스푼에 우주가 열리는 아기 먹방 — 츄릅, 감탄, 폭풍 흡입.",1.0,
            new List<BabyCut>{
              new("드디어 첫 이유식 이게 밥이라는 것","첫","Bright natural window light, close-up of a baby in a high chair staring at a spoon of baby food with sparkling curious eyes, bib on, chubby cheeks, warm cozy kitchen, 4K, photorealistic",32,"fade",450, Sfx(Fx("cute",300,0),Fx("ding",1300,0))),
              new("일단 신중하게 한 스푼 츄릅","츄릅","Macro zoom on a baby's mouth taking a spoonful of food, tiny lips closing around the spoon, soft focus, natural sunlight, hyper-detailed, 4K, warm, photorealistic",48,"zoomin",400, Sfx(Fx("suspense",800,0))),
              new("오 이것은 신세계 맛이 열렸다","신세계","Close-up of a baby's eyes going wide with amazement mid-bite, eyebrows raised, pure wonder, bright natural light, comedic delight, 4K, photorealistic",68,"slideleft",320, Sfx(Fx("ding",150,0),Fx("sparkle",500,0.3),Fx("pop",900,-0.2))),
              new("더 더 빨리 스푼을 내놔라","더 더","Close-up of a baby leaning forward mouth wide open demanding more, hands grabbing toward the spoon, natural light, comedic urgency, fast-motion, 4K, photorealistic",80,"hblur",400, Sfx(Fx("boing",200,0.2),Fx("giggle",700,0))),
              new("폭풍 흡입 온 얼굴이 이유식 범벅","범벅","Fast-motion, a giggling baby happily eating with food smeared all over its face and hands, joyful mess, bright natural sunlight, vivid, 4K, comedic, photorealistic",90,"slideleft",300, Sfx(Fx("chomp",180,0),Fx("boing",700,0.3),Fx("giggle",1200,-0.3))),
              new("자 다음 한 스푼은 어떤 맛일까","다음","Bright natural window light, close-up of the same baby in the high chair staring at a fresh spoonful with sparkling eyes, identical framing to the opening for a seamless loop, 4K, photorealistic",32,"fade",450, Sfx(Fx("cute",300,0),Fx("ding",1200,0))),
            }),

        // 6. 아기 헬스장
        new("gym","💪","아기 헬스장",
            "오늘도 터미타임 3세트… 프로의 자세 💪","엎드려 버티기를 헬스 운동처럼 진지하게 하는 아기 보디빌더.",1.0,
            new List<BabyCut>{
              new("운동은 매일 오늘의 목표 터미타임","목표","Bright natural gym-lit daylight, close-up of a determined baby lying on its tummy lifting its head like a serious workout, focused face, chubby arms pushing up, soft playmat, 4K, photorealistic",38,"fade",450, Sfx(Fx("cute",300,0),Fx("drumroll",1200,0))),
              new("자 1세트 고개 들고 버틴다","버틴다","Close-up of a baby straining to lift its head and chest during tummy time, tiny muscles working, focused grunt face, natural light, hyper-detailed, 4K, comedic-serious, photorealistic",50,"zoomin",400, Sfx(Fx("suspense",200,0),Fx("boing",900,0.2))),
              new("부들부들 이게 근성이다 조금만 더","근성","Extreme close-up of a baby's trembling arms shaking with effort, quivering, determined eyes, bright natural light, comedic strain, fast-motion, 4K, photorealistic",68,"slideleft",320, Sfx(Fx("boing",150,-0.3),Fx("squeak",600,0.2))),
              new("어 팔에 힘이 스르륵 위기 상황","위기","Close-up of a baby's arms wobbling and slowly giving out, dramatic effort, wide eyes, natural light, comedic tension, 4K, photorealistic",76,"hblur",400, Sfx(Fx("suspense",120,0),Fx("boing",450,0.3))),
              new("풀썩 하지만 완벽한 마무리 자세","완벽","Fast-motion, a baby flopping face-down onto the soft mat then popping its head up proudly, satisfied grin, bright natural light, comedic, 4K, photorealistic",88,"slideleft",300, Sfx(Fx("crash",180,0),Fx("giggle",600,0),Fx("pop",1000,-0.3))),
              new("좋아 다음 세트 다시 엎드린다","다음","Bright natural daylight, close-up of the same determined baby getting back into tummy-time position lifting its head, identical framing to the opening for a seamless loop, 4K, photorealistic",38,"fade",450, Sfx(Fx("cute",300,0),Fx("drumroll",1100,0))),
            }),

        // 7. 아기 CEO
        new("ceo","👔","아기 CEO",
            "긴급 이사회 소집… 안건은 '간식' 📈","화상회의 중인 진지한 아기 대표 — 중대 결정을 내린다.",1.0,
            new List<BabyCut>{
              new("긴급 회의를 소집한다 안건은 중대하다","긴급","Bright natural office daylight, cinematic close-up of a baby sitting upright with a tiny serious executive expression facing a laptop, dramatic soft light, chubby cheeks, 4K, photorealistic, comedic-serious",40,"fade",450, Sfx(Fx("ding",250,0),Fx("suspense",1300,0))),
              new("자료를 검토했다 결론은 명확해","결론","Close-up of a baby staring intensely at papers, furrowed brow of concentration, tapping a tiny hand on the desk, natural light, hyper-detailed, 4K, photorealistic",50,"zoomin",400, Sfx(Fx("ding",300,0),Fx("suspense",900,0.2))),
              new("반대 의견 있나 없으면 통과다 쾅","통과","Close-up of a baby slamming a tiny fist on the desk with a decisive commanding face, bright natural light, comedic authority, fast-motion, 4K, photorealistic",68,"slideleft",320, Sfx(Fx("crash",120,0),Fx("drumroll",500,0),Fx("ding",900,-0.2))),
              new("오늘의 결정 간식 예산을 두 배로","두 배","Close-up of a baby breaking into a triumphant grin, sparkling eyes, sense of a big win, natural light, comedic joy, 4K, photorealistic",75,"hblur",400, Sfx(Fx("sparkle",200,0.3),Fx("giggle",700,0))),
              new("회의는 끝 이제 실무는 낮잠이다 픽","실무","Fast-motion, a baby suddenly slumping face-first onto the desk fast asleep, papers scattering, bright natural light, comedic drop, 4K, photorealistic",88,"slideleft",300, Sfx(Fx("whoosh",150,0.3),Fx("crash",450,0),Fx("ding",900,0))),
              new("자 다음 안건 회의를 재개한다","다음","Bright natural office daylight, cinematic close-up of the same baby snapping back upright with a serious executive face at the laptop, identical framing to the opening for a seamless loop, 4K, photorealistic",40,"fade",450, Sfx(Fx("ding",250,0),Fx("suspense",1200,0))),
            }),

        // 8. 목욕탕 대참사
        new("bath","🛁","목욕탕 대참사",
            "평화롭던 목욕 시간… 3초 뒤 물바다 🌊","진지하게 목욕하던 아기가 물장구 스위치가 켜지는 순간.",1.0,
            new List<BabyCut>{
              new("목욕 시간 오늘은 조용히 씻겠다","조용히","Bright natural bathroom daylight, close-up of a baby sitting calmly in a warm bubble bath with a serene face, glistening water droplets, soft steam, chubby cheeks, 4K, photorealistic",32,"fade",450, Sfx(Fx("cute",300,0),Fx("splash",1300,0))),
              new("거품이 참 많군 흥미로운 물질이야","거품","Macro close-up of a baby curiously poking a mountain of white bubbles, tiny finger, sparkling water, natural light, hyper-detailed, 4K, warm, photorealistic",44,"zoomin",400, Sfx(Fx("pop",300,0),Fx("suspense",900,0.2))),
              new("잠깐 이 물을 때리면 어떻게 될까","때리면","Close-up of a baby raising both hands over the water with a mischievous spark in its eyes, anticipation, bright natural light, comedic, 4K, photorealistic",64,"slideleft",320, Sfx(Fx("suspense",150,0),Fx("ding",600,0))),
              new("첨벙 온 세상이 물바다가 됐다","첨벙","Fast-motion, a baby joyfully smacking the bath water sending huge splashes everywhere, water droplets frozen in sunlight, pure chaos, bright natural light, 4K, comedic, photorealistic",90,"hblur",380, Sfx(Fx("splash",120,0),Fx("crash",300,0.3),Fx("giggle",800,-0.3))),
              new("나는 즐겁다 엄마 표정은 잘 모르겠고","즐겁다","Fast-motion, a giggling soaked baby splashing wildly with a huge delighted grin, bubbles flying, bright natural sunlight, joyful mess, 4K, photorealistic",85,"slideleft",300, Sfx(Fx("splash",200,0.2),Fx("boing",600,-0.3),Fx("giggle",1100,0))),
              new("자 다시 조용히 씻어볼까 얌전","다시","Bright natural bathroom daylight, close-up of the same baby sitting calmly again in the bubble bath with a serene face, identical framing to the opening for a seamless loop, 4K, photorealistic",32,"fade",450, Sfx(Fx("cute",300,0),Fx("splash",1200,0))),
            }),

        // 9. 까꿍 반전
        new("peekaboo","🙈","까꿍 반전",
            "까꿍… 다음 표정을 아무도 예측 못 함 😲","손 뒤에 숨었다 나타나는 반전의 미학, 무한 까꿍 루프.",1.15,
            new List<BabyCut>{
              new("나는 지금부터 완벽하게 사라진다","사라진다","Bright natural daylight, close-up of a baby covering its own face with two chubby hands, only tiny fingers peeking, soft home background, warm light, 4K, adorable, photorealistic",34,"fade",450, Sfx(Fx("cute",300,0),Fx("suspense",1200,0))),
              new("아무도 나를 볼 수 없지 후후","아무도","Close-up of a baby hiding behind its hands, fingers slightly parting to peek out, mischievous eyes, natural light, hyper-detailed, 4K, photorealistic",44,"zoomin",380, Sfx(Fx("suspense",300,0),Fx("ding",900,0.2))),
              new("자 카운트 셋 둘 하나 까꿍","까꿍","Close-up of a baby suddenly throwing its hands open revealing a huge surprised smile, wide sparkling eyes, bright natural light, comedic pop, fast-motion, 4K, photorealistic",82,"slideleft",280, Sfx(Fx("pop",100,0),Fx("boing",280,0.3),Fx("giggle",600,0))),
              new("어때 놀랐지 반응이 아주 좋아","놀랐지","Close-up of a baby laughing with pure delight at its own reveal, scrunched happy eyes, bright sunlight, joyful, 4K, photorealistic",76,"hblur",400, Sfx(Fx("giggle",200,0),Fx("sparkle",700,0.3))),
              new("한 번 더 이번엔 진짜 안 보인다","한 번 더","Fast-motion, a giggling baby quickly covering its face again for another round, playful energy, bright natural light, 4K, comedic, photorealistic",70,"slideleft",300, Sfx(Fx("whoosh",120,-0.3),Fx("cute",500,0.2))),
              new("나는 지금부터 완벽하게 사라진다 또","완벽하게","Bright natural daylight, close-up of the same baby covering its face with two hands again with peeking fingers, identical framing to the opening for a seamless loop, 4K, photorealistic",34,"fade",450, Sfx(Fx("cute",300,0),Fx("suspense",1100,0))),
            }),

        // 10. 생애 첫 눈
        new("snow","❄️","생애 첫 눈",
            "생애 처음 본 눈… 아기의 철학이 시작된다 ❄️","하늘에서 내리는 하얀 것의 정체를 탐구하는 아기 철학자.",1.0,
            new List<BabyCut>{
              new("저 하늘에서 하얀 것이 내려온다","하얀","Bright crisp winter sunlight, close-up of a bundled-up baby looking up in awe at falling snowflakes, wide sparkling eyes, rosy cheeks, soft bokeh snow, 4K, magical, photorealistic",34,"fade",450, Sfx(Fx("sparkle",300,0),Fx("cute",1400,0))),
              new("이것의 정체를 반드시 밝혀내겠다","정체","Macro close-up of a single snowflake landing on a baby's tiny outstretched mitten, ultra-detailed ice crystal, natural winter light, hyper-detailed, 4K, photorealistic",44,"zoomin",400, Sfx(Fx("sparkle",250,0.2),Fx("suspense",900,0))),
              new("잡았다 그런데 손에서 사라졌어 마법","마법","Close-up of a baby staring in shock at its empty mitten where the snowflake melted, puzzled wide eyes, bright winter light, comedic wonder, 4K, photorealistic",64,"slideleft",320, Sfx(Fx("boing",200,0.2),Fx("ding",600,-0.2))),
              new("이 차가운 감촉 나쁘지 않은데 좋아","좋아","Close-up of a baby breaking into a delighted giggle as snowflakes touch its face, joyful scrunch, sparkling winter sunlight, warm breath fog, 4K, photorealistic",74,"hblur",420, Sfx(Fx("giggle",200,0),Fx("sparkle",700,0.3))),
              new("온몸으로 맞이한다 첫 눈 만세","만세","Fast-motion, a bundled baby joyfully waving its arms in the falling snow, laughing, snowflakes swirling in bright sunlight, pure joy, 4K, comedic, photorealistic",88,"slideleft",300, Sfx(Fx("whoosh",150,-0.4),Fx("giggle",600,0),Fx("sparkle",1100,0.4))),
              new("자 다음 눈송이는 어디에 있을까","다음","Bright crisp winter sunlight, close-up of the same bundled baby looking up in awe at falling snow again, identical framing to the opening for a seamless loop, 4K, photorealistic",34,"fade",450, Sfx(Fx("sparkle",300,0),Fx("cute",1300,0))),
            }),
    };
}
