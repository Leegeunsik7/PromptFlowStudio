using System;
using System.Collections.Generic;
using System.Linq;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>
/// 테마 컷 1개의 compact authoring 비트 — 대규모 테마를 유지보수 가능하게 쓰기 위한 최소 단위.
///   V=속마음 대사(페르소나 어조), G=골드 강조 단어, Shot=시각 묘사(렌즈·조명·질감 authored · 프롬프트 조립용),
///   T=긴장도, X=들어오는 xfade, Sfx=폴리 토큰(콤마 다중 · <c>type</c> 또는 <c>type@pan</c> 예: <c>chomp@L30</c>).
///   <see cref="VideoFormatFactory"/>가 포맷(숏츠/롱폼)에 맞춰 이 비트를 완성형 <see cref="StoryboardScene"/>로 확장한다.
/// </summary>
public sealed record BabyBeat(string V, string G, string Shot, int T, string X, string Sfx);

/// <summary>소분류 테마 정의 — 메타 + 6비트(숏츠 기본). 롱폼은 팩토리가 이 비트를 확장.</summary>
public sealed record BabyThemeDef(string Key, string CategoryKey, string Emoji, string Title, string Hook, string Example, double ShortSpeed, IReadOnlyList<BabyBeat> Beats)
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

/// <summary>10대 대분류 카테고리 — 소분류 테마 목록 보유.</summary>
public sealed record BabyCategory(string Key, string Emoji, string Name, string Desc, IReadOnlyList<BabyThemeDef> Themes);

/// <summary>
/// [PromptFlowStudio] 아기 테마 백과사전 — 순수 BCL 인메모리 계층형 데이터(외부 DB/NuGet 0 · Authored Data 패턴).
///   ▸ 10대 대분류(음식/수면/신체/감정/야외/일상/코스튬/가족/장난/특별한날) × 소분류 5+ 테마 = 50+ 완성형 테마.
///   ▸ v16.10.0: 음식·수면·일상 +6 · 야외 +4 · 특별한 날(시즌/이벤트) +12 = 84 테마(신규는 4-비트 미니 아크).
///   ▸ v1.2.1 정밀 가공: 테마마다 <b>고유 렌즈·조명·질감·페르소나 어조·시그니처 3D 폴리</b>를 부여해 컷 바이 컷 중복을 제거.
///   ▸ 각 테마는 compact 6-비트(<see cref="BabyBeat"/>) — <see cref="VideoFormatFactory"/>가 숏츠(6컷 루프) / 롱폼(15~20컷 서사)로 확장.
///   ▸ 조회: <see cref="Categories"/>(탭), <see cref="Get"/>(키), <see cref="AllThemes"/>.
/// </summary>
public sealed class BabyThemeDatabase
{
    /// <summary>UI 탭 순서 = 기본 10대 카테고리 + v2.3.0 확장 7대 카테고리(간식/꿈나라/판타지/우주/명화/직업/동물) + 마일스톤 + 실생활 4대 = 총 200+ 소분류(v16.10.0).</summary>
    public IReadOnlyList<BabyCategory> Categories => _categories;

    /// <summary>기본(내장) 카테고리 — 커스텀 병합과 무관한 authored 백과사전.</summary>
    public IReadOnlyList<BabyCategory> BuiltInCategories { get; } = Build().Concat(BabyThemeExpansion.Categories).Concat(BabyMilestoneThemeDatabase.Categories)
        .Concat(BabyEverydayThemeDatabase.Categories).ToList();   // v16.6.0 놀이터·키즈카페·놀이공원·일상 나들이

    private volatile IReadOnlyList<BabyCategory> _categories;
    private volatile Dictionary<string, BabyThemeDef>? _index;
    private Dictionary<string, BabyThemeDef> Index => _index ??= Categories.SelectMany(c => c.Themes).ToDictionary(t => t.Key, StringComparer.OrdinalIgnoreCase);

    public BabyThemeDatabase() => _categories = BuiltInCategories;

    /// <summary>
    /// v16.8.0 커스텀 카테고리 동적 병합 — 내장 카테고리 뒤에 붙인 새 목록으로 원자 교체(키 충돌 시 예외 · 호출부가 사전 검증).
    ///   빈 목록이면 내장만 남긴다. 이전 스냅숏을 읽는 렌더는 그대로 안전(불변 목록 교체).
    /// </summary>
    public void SetCustomCategories(IReadOnlyList<BabyCategory>? custom)
    {
        var merged = custom is { Count: > 0 } ? BuiltInCategories.Concat(custom.Where(c => c.Themes.Count > 0)).ToList() : (IReadOnlyList<BabyCategory>)BuiltInCategories;
        var index = merged.SelectMany(c => c.Themes).ToDictionary(t => t.Key, StringComparer.OrdinalIgnoreCase);   // 중복 키면 여기서 throw(교체 전)
        _categories = merged;
        _index = index;
    }

    /// <summary>내장 테마 키 여부(커스텀 키 충돌 검사용).</summary>
    public bool IsBuiltInKey(string? key) => !string.IsNullOrWhiteSpace(key) && BuiltInKeys.Contains(key!);

    /// <summary>v16.13.0 내장 키 집합(지연 1회 구축) — 가져오기/재명명 루프의 테마 전수 선형 탐색 제거.</summary>
    private HashSet<string>? _builtInKeys;
    private HashSet<string> BuiltInKeys => _builtInKeys ??= new HashSet<string>(BuiltInCategories.SelectMany(c => c.Themes).Select(t => t.Key), StringComparer.OrdinalIgnoreCase);

    /// <summary>전체 테마(평면).</summary>
    public IReadOnlyList<BabyThemeDef> AllThemes => Index.Values.ToList();

    /// <summary>키로 테마 조회(없으면 null).</summary>
    public BabyThemeDef? Get(string? key) => !string.IsNullOrWhiteSpace(key) && Index.TryGetValue(key!, out var t) ? t : null;

    /// <summary>테마가 속한 카테고리 조회.</summary>
    public BabyCategory? CategoryOf(string themeKey) => Categories.FirstOrDefault(c => c.Themes.Any(t => string.Equals(t.Key, themeKey, StringComparison.OrdinalIgnoreCase)));

    // 비트 헬퍼(가독성).
    private static BabyBeat B(string v, string g, string shot, int t, string x, string sfx) => new(v, g, shot, t, x, sfx);

    private static IReadOnlyList<BabyCategory> Build() => new List<BabyCategory>
    {
        // ═══ 1. 음식 / 먹방 ═══
        new("food","🍽️","음식·먹방","첫 시식·이유식·스파게티·촉감 먹방 리액션", new List<BabyThemeDef>{
          new("lemon","food","🍋","레몬 시식가","0.1초 뒤 진지한 표정이 무너집니다 😂","첫 레몬의 신맛 배신 → 이상한 중독 (페르소나: 고독한 미식가)",1.0, new[]{
            B("흠 오늘의 시식 메뉴는 이것","시식","Macro 85mm lens, warm golden-hour backlight, a chubby-cheeked baby holding up a translucent lemon slice with a solemn food-critic frown, downy cheek fuzz glowing, a faint drool bead on the lower lip",30,"fade","pluck@C,cute@R20"),
            B("겉은 상큼해 보이는군 한 입","한 입","Extreme macro 100mm, soft golden rim light, a baby's tiny lips and pink tongue cautiously touching the glistening lemon, a thin thread of saliva stretching, citrus mist",45,"zoomin","suspense@C"),
            B("이건 명백한 배신이야","배신","Macro 85mm, harsh sunlit catchlight, a baby's whole face collapsing into a sour pucker, eyes clamped shut, cheeks quivering, a single tear sparkle at the lash line",82,"slideleft","crash@C,boing@L30,squeak@R40"),
            B("그런데 왜 자꾸 손이 가지","자꾸","Macro 85mm, warm bokeh, the sour scowl melting into a drooly delighted grin, one pudgy hand creeping back toward the lemon, glossy lower lip",72,"hblur","giggle@L20,sparkle@R30"),
            B("인정 오늘부터 넌 내 최애","최애","Macro 85mm, bright punchy sun, a giggling baby gnawing the lemon rind, citrus juice running down the chin and wrist, blissful squint",90,"slideleft","chomp@C,pop@R35"),
            B("자 다음 시식 메뉴는","다음","Macro 85mm lens, warm golden-hour backlight, the same chubby-cheeked baby lifting a fresh lemon slice with the identical solemn food-critic frown, matched framing for a seamless loop",30,"fade","pluck@C,cute@R20") }),
          new("weaning","food","🍚","이유식 먹방","첫 이유식… 표정이 다 말해줍니다 🥄","한 스푼에 우주가 열리는 먹방 (페르소나: 미슐랭 평가단)",1.0, new[]{
            B("본 평가단이 신메뉴를 심사한다","심사","100mm probe macro, soft overhead kitchen softbox, a bib-wearing baby eyeing a spoon of orange puree like a Michelin judge, gleaming spoon reflection, sparkling focused eyes",32,"fade","ding@C,cute@R20"),
            B("일단 한 스푼 신중하게","한 스푼","Extreme macro, soft top light, baby lips closing around a laden spoon, puree smearing the upper lip, a shiny drool string, intense concentration",48,"zoomin","ding@L25,suspense@C"),
            B("오 이건 별 다섯 개짜리","별 다섯","100mm macro, bright softbox, baby's eyes flying wide in gourmet amazement mid-bite, eyebrows lifting, a puree droplet on the nose tip",68,"slideleft","ding@C,sparkle@R30,pop@L20"),
            B("더 더 빨리 내오세요","더 더","100mm macro, soft light, baby lunging forward mouth agape demanding more, chubby hands grabbing the spoon, puree freckles across both cheeks",80,"hblur","boing@L30,giggle@R25"),
            B("온 얼굴이 평점 만점 범벅","범벅","Macro, bright airy light, a giggling baby with puree smeared ear to ear, spoon clutched like a trophy, joyful drooly grin",90,"slideleft","chomp@C,giggle@R35"),
            B("자 다음 코스를 내오세요","다음","100mm probe macro, soft overhead kitchen softbox, the same bib-wearing baby eyeing a fresh spoonful with the identical judging stare, matched framing for a seamless loop",32,"fade","ding@C,cute@R20") }),
          new("icecream","food","🍦","첫 아이스크림","차가움에 놀란 아기의 3초 후 😳","차가움 충격 → 황홀경 (페르소나: 얼음왕국 탐험가)",1.0, new[]{
            B("이 차가운 흰 산은 무엇","흰 산","Anamorphic 40mm, cool blue-hour window light with warm rim, a baby squinting up at a tall soft-serve cone, frosty vapor curling, wide curious eyes",30,"fade","pluck@C,cute@L20"),
            B("정찰 삼아 한 번 핥아본다","정찰","Anamorphic macro, cold cyan glow, a baby's pink tongue inching toward the frozen swirl, a chill shiver on the cheeks, glistening lip",45,"zoomin","suspense@C"),
            B("으 차가워 이건 함정이다","차가워","Anamorphic 40mm, harsh cold catchlight, a baby recoiling with a full-body brain-freeze shiver, scrunched nose, mouth an O of shock",78,"slideleft","crash@C,squeak@R40"),
            B("근데 달콤해 다시 접근","달콤","Anamorphic macro, warm-cool mixed light, the shocked face thawing into a blissful lick, creamy mustache forming, half-closed happy eyes",74,"hblur","sparkle@L20,giggle@R30"),
            B("정복 완료 멈출 수 없어","정복","Anamorphic 40mm, bright cold sparkle, a baby eagerly lapping the melting cone, ice cream dripping over the fist, gleeful mess",88,"slideleft","chomp@C,pop@R30"),
            B("다음 빙산을 정찰한다","다음","Anamorphic 40mm, cool blue-hour window light with warm rim, the same baby squinting up at a fresh cone with the identical wary awe, matched framing for a seamless loop",30,"fade","pluck@C,cute@L20") }),
          new("watermelon","food","🍉","수박 탐험대","수박에 파묻힌 아기 실화 🍉","수박 속으로 다이빙하는 여름 먹방 (페르소나: 여름 정글 탐험대)",1.0, new[]{
            B("빨간 정글이 나를 부른다","정글","Ultra-wide 20mm, bright dappled summer sun, a baby facing a giant watermelon slice taller than its head, glossy seeds like treasure, adventurous grin",32,"fade","cute@C,ding@R25"),
            B("탐험대장 얼굴부터 투입","투입","Ultra-wide macro, hot sunlight, a baby plunging its whole face into the juicy red flesh, cheeks squishing, juice spraying in droplets",52,"zoomin","boing@L25,splash@R30"),
            B("입안 가득 여름이 터졌다","여름","Ultra-wide 20mm, sparkling sun, cheeks bulging with watermelon, pink juice streaming down the neck, seeds stuck to the chin, ecstatic squint",72,"slideleft","chomp@C,splash@L30"),
            B("어 이 까만 씨앗은 뭐지","씨앗","Ultra-wide macro, dappled light, a baby cross-eyed inspecting a black seed stuck to a fingertip, puzzled pout, glossy juicy lips",68,"hblur","suspense@C,pop@R25"),
            B("여름 정복 만세","정복","Ultra-wide 20mm, brilliant sun flare, a giggling baby drenched in watermelon juice raising sticky arms in triumph, pure summer joy",88,"slideleft","giggle@C,sparkle@R35"),
            B("다음 정글을 탐험한다","다음","Ultra-wide 20mm, bright dappled summer sun, the same baby facing a fresh giant watermelon slice with the identical explorer grin, matched framing for a seamless loop",32,"fade","cute@C,ding@R25") }),
          new("noodle","food","🍜","국수 흡입기","후루룩 소리 실화? 국수 장인 아기 😋","국수 흡입 챌린지 (페르소나: 면치기 장인)",1.15, new[]{
            B("면발의 장인이 강림했다","장인","Tilt-shift 50mm, warm tungsten diner glow, a baby solemnly regarding a bowl of long soft noodles like a ramen master, steam rising, focused brow",30,"fade","pluck@C,cute@R20"),
            B("후루룩 이게 진정한 흡입","후루룩","Tilt-shift macro, warm amber light, a baby attempting to slurp one long noodle, lips pursed, the noodle whipping upward, broth droplets flying",48,"zoomin","whoosh@L30,suspense@C"),
            B("어 면이 얼굴에 착 붙었다","착","Tilt-shift 50mm, warm glow, a single noodle draped across a baby's astonished face over one eye, mouth agape, comedic freeze",72,"slideleft","boing@C,squeak@R35"),
            B("의외로 이 스타일 마음에 들어","스타일","Tilt-shift macro, cozy tungsten, the baby laughing as noodles dangle from both cheeks like whiskers, broth speckles on the nose",74,"hblur","giggle@L20,pop@R25"),
            B("폭풍 흡입 개시","흡입","Tilt-shift 50mm, warm diner light, a baby rapidly slurping a fistful of noodles, broth splattering, blissful messy chaos",90,"slideleft","chomp@C,sparkle@R30"),
            B("자 다음 면을 삶아오게","다음","Tilt-shift 50mm, warm tungsten diner glow, the same baby solemnly regarding a fresh bowl of noodles with the identical master's focus, matched framing for a seamless loop",30,"fade","pluck@C,cute@R20") }),
          // v16.10.0 확장
          new("fd_spaghetti","food","🍝","스파게티 먹방","온 얼굴이 토마토 소스 범벅 🍝","면발과의 전쟁 → 소스 범벅 승리 (페르소나: 파스타 마에스트로)",1.05, new[]{
            B("오늘의 메뉴는 파스타","파스타","Macro 85mm, warm golden kitchen light, a baby in a high chair with a bib facing a small bowl of short spaghetti in tomato sauce, fork clutched in a tiny fist, solemn chef face",30,"fade","pluck@C,cute@R20"),
            B("후루룩 한 가닥 도전","후루룩","Extreme macro, soft golden light, the baby slurping a single long noodle, the noodle sliding upward into the mouth, sauce dotting the cheeks",56,"zoomin","whoosh@C,suspense@R20"),
            B("손으로 먹는 게 진리","진리","Macro 85mm, warm light, the baby grabbing a fistful of spaghetti and stuffing it in, sauce smearing ear to ear, delighted squint",82,"slideleft","chomp@C,giggle@R30"),
            B("파스타 마에스트로 완식","완식","50mm, bright golden light, the sauce-covered baby holding up the empty bowl with a proud orange grin",60,"hblur","clap@C,sparkle@R25") }),
          new("fd_purreplay","food","🥣","이유식 촉감놀이","숟가락 대신 손바닥으로 퍼먹기 🖐️","단호박 이유식 촉감 실험 (페르소나: 꼬마 조각가)",1.0, new[]{
            B("이 말랑한 노란 건 뭘까","노란","100mm macro, soft overhead kitchen softbox, a baby in a high chair poking a smooth mound of yellow pumpkin puree on the tray with one finger",28,"fade","pop@C,cute@R20"),
            B("손바닥으로 철퍽","철퍽","Extreme macro, bright softbox, the baby slapping both palms into the puree, the puree squishing outward between the fingers naturally",62,"zoomin","splash@C,squeak@R25"),
            B("쭉쭉 그림을 그려요","그림","Bird's-eye 35mm, soft studio light, the baby smearing puree across the tray in swirls like finger painting, tongue poking out",74,"slideleft","pluck@C,giggle@R25"),
            B("결국 손가락 쪽쪽","쪽쪽","Macro 85mm, warm diffused light, the baby sucking puree off its fingers with blissful closed eyes, cheeks dotted yellow",52,"hblur","chomp@C,sparkle@R25") }),
          new("fd_corn","food","🌽","옥수수 하모니카","옥수수 하모니카 연주 실화 🌽","찐 옥수수 갉아먹기 챌린지 (페르소나: 옥수수 연주자)",1.0, new[]{
            B("황금빛 악기를 받았다","악기","50mm, bright summer daylight, a toddler holding a short cob of steamed yellow corn with both hands, steam curling softly, curious eyes",28,"fade","pluck@C,cute@R20"),
            B("앙 한 줄 갉아먹기","갉아","Macro 85mm, sunny light, the toddler gnawing along a row of kernels from left to right like playing a harmonica, kernels popping",58,"zoomin","chomp@C,pop@R20"),
            B("이 사이에 낀 옥수수","낀","Extreme close-up, bright daylight, the toddler grinning with a corn kernel stuck to the chin and front teeth, comic wide eyes",76,"slideleft","boing@C,giggle@R30"),
            B("연주회 대성공","연주회","50mm, golden afternoon light, the toddler holding up the half-eaten cob like a trophy, kernels on the cheeks",50,"hblur","clap@C,sparkle@R25") }),
          new("fd_banana","food","🍌","바나나 첫 쥐기","미끌미끌 바나나와의 사투 🍌","혼자 먹기 첫 도전 (페르소나: 바나나 사냥꾼)",1.0, new[]{
            B("노란 막대를 발견했다","막대","Macro 85mm, warm golden morning light, a baby in a high chair holding a peeled banana half like a microphone, studying it intently",26,"fade","ding@C,cute@R20"),
            B("꽉 쥐었더니 쭉","쭉","Extreme macro, soft golden light, the baby squeezing the banana too hard, soft banana squishing out between the chubby fingers, surprised face",64,"zoomin","squeak@C,boing@R25"),
            B("그래도 맛은 최고","최고","Macro 85mm, warm light, the baby licking mashed banana off its fist with sparkling eyes, a sticky smile",72,"slideleft","chomp@C,giggle@R25"),
            B("바나나 사냥 성공","성공","50mm, bright golden light, the banana-smeared baby waving the empty peel triumphantly",50,"hblur","clap@C,sparkle@R25") }),
          new("fd_yogurt","food","🥄","요거트 셀프 먹기","숟가락은 입으로… 요거트는 이마로 🥄","혼자 숟가락질 첫 도전 (페르소나: 숟가락 수련생)",1.0, new[]{
            B("오늘은 혼자 먹겠다","혼자","Macro 85mm, soft overhead kitchen softbox, a toddler gripping a small spoon over a cup of white yogurt, determined frown",28,"fade","drumroll@C,cute@R20"),
            B("푹 떠서 천천히","천천히","Extreme macro, bright softbox, the toddler scooping yogurt and lifting the wobbly spoon upward toward the mouth, a drip falling downward",54,"zoomin","suspense@C,pluck@R20"),
            B("어 볼에 먹었다","볼에","Macro 85mm, soft light, the spoon missing and landing on the cheek, a yogurt smear across the face, a puzzled blink",74,"slideleft","boing@C,squeak@R25"),
            B("두 번째는 성공","성공","50mm, bright soft light, the toddler finally getting the spoon in the mouth and beaming with a white yogurt mustache",60,"hblur","clap@C,giggle@R25") }),
          new("fd_tangerine","food","🍊","귤 까기 도전","작은 손으로 귤 까기 대작전 🍊","겨울 귤 껍질 챌린지 (페르소나: 귤 장인)",1.0, new[]{
            B("동그란 주황 공이다","주황","Macro 85mm, cozy warm tungsten winter light, a toddler sitting on a heated floor blanket holding a small tangerine with both hands",26,"fade","pluck@C,cute@R20"),
            B("손톱으로 콕콕","콕콕","Extreme macro, warm amber light, the toddler's tiny fingers picking at the peel, a citrus mist spraying faintly, intense focus",50,"zoomin","suspense@C,pop@R20"),
            B("껍질이 쭉 벗겨졌다","벗겨","Macro 85mm, cozy glow, a big piece of peel coming away in one strip, the toddler gasping with pride",72,"slideleft","ding@C,sparkle@R25"),
            B("새콤달콤 한 조각","새콤","50mm, warm tungsten light, the toddler popping a tangerine segment in the mouth and scrunching up the face at the sour-sweet burst",64,"hblur","chomp@C,giggle@R25") }),
        }),

        // ═══ 2. 수면 / 행동 ═══
        new("sleep","😴","수면·행동","낮잠·기상·잠투정·잠버릇 리얼", new List<BabyThemeDef>{
          new("naptime","sleep","😪","낮잠 거부","절대 안 잔다던 아기의 최후 3초 😴","졸림 vs 자존심 → 픽 (페르소나: 불면의 반항아)",1.0, new[]{
            B("나는 절대로 잠들지 않아","절대","35mm lens, soft overcast nursery light, a defiant baby forcing wide alert eyes open, tiny fists clenched, jaw set, downy hair mussed",35,"fade","cute@C,pop@R25"),
            B("어른들은 밤이 짧다는 걸 몰라","짧다","35mm macro, gentle grey daylight, a baby's heavy eyelids fighting to stay open, long lashes trembling, a slow reluctant blink",42,"zoomin","suspense@C"),
            B("눈만 잠깐 감았다 뜬 거야","진짜","35mm lens, soft light, eyelids drooping then snapping back open in stubborn denial, one eyebrow twitching up",55,"slideleft","boing@L25,ding@R30"),
            B("고개가 왜 이렇게 무겁지","무거워","35mm, overcast glow, a baby's head nodding forward then jerking upright, a sleepy wobble, cheeks going slack",60,"hblur","boing@C,cute@R25"),
            B("나는 아직 쌩쌩한데 픽","픽","35mm lens, soft nursery light, a baby toppling sideways into a pillow instantly asleep mid-motion, serene surrender, one arm flung out",85,"slideleft","whoosh@L35,crash@C,ding@R20"),
            B("음 안 졸린데 눈만 잠깐","눈만","35mm lens, soft overcast nursery light, the same baby jolting awake with wide defiant eyes and clenched fists, matched framing for a seamless loop",35,"fade","cute@C,pop@R25") }),
          new("dawn","sleep","🌅","새벽 기상 요정","해도 안 떴는데 눈 뜬 아기 😇","새벽 5시 기상 요정의 하루 시작 (페르소나: 새벽의 요정)",1.0, new[]{
            B("벌써 아침 냄새가 난다","아침","24mm wide, cool pre-dawn blue with warm nightlight glow, a wide-awake baby blinking brightly in a dim crib, tousled hair, mischievous alertness",28,"fade","pluck@C,cute@R20"),
            B("온 세상이 아직 자고 있네","세상","24mm, blue-hour softness, a baby peeking over the crib rail at sleeping parents, tiny fingers gripping the bars, conspiratorial eyes",40,"zoomin","suspense@C"),
            B("깨어난 요정은 나뿐이다","요정","24mm wide, dawn glow, a baby breaking into a radiant mischievous grin in the half-light, dimples deep, eyes sparkling",58,"slideleft","ding@C,boing@R30"),
            B("자 놀이 시간을 선포한다","놀이","24mm, warming light, a baby clapping tiny hands with gleeful energy, bouncing on the mattress, hair flopping",70,"hblur","clap@C,giggle@R25"),
            B("모두 일어나요 요정의 명령","명령","24mm wide, brightening dawn, a baby babbling loudly with arms thrown up to wake the house, joyful commanding face",85,"slideleft","pop@L25,sparkle@R30"),
            B("좋은 아침이야 다시 시작","아침","24mm wide, cool pre-dawn blue with warm nightlight glow, the same wide-awake baby blinking brightly over the crib rail, matched framing for a seamless loop",28,"fade","pluck@C,cute@R20") }),
          new("wakeup","sleep","🥱","자다 깬 멍뭉이","자다 깬 아기 = 세상 억울한 표정 😪","단잠에서 깬 멍한 표정 모음 (페르소나: 억울한 잠수부)",1.0, new[]{
            B("여긴 어디 나는 누구","어디","50mm lens, dim warm nightlight, a baby surfacing from sleep with a dazed cross-eyed stare, one cheek creased from the pillow, tufted hair",30,"fade","cute@C,suspense@R20"),
            B("내가 대체 왜 깨어난 거지","왜","50mm macro, soft amber, a baby rubbing sleepy eyes with balled fists, an eye-crust fleck, a wobbling disoriented head",42,"zoomin","cute@C"),
            B("세상 억울해 죽겠네","억울","50mm lens, warm dim glow, a baby's face folding into a deeply wronged pout, lower lip jutting, brows knotted in grievance",60,"slideleft","boing@L25,squeak@R35"),
            B("에라 모르겠다 다시 잠수","잠수","50mm, cozy shadow, a baby flopping face-first back into the blanket, arms splayed, surrendering to sleep",55,"hblur","whoosh@L30,ding@R20"),
            B("아니 잠깐 이제 놀고 싶다","놀고","50mm lens, warm light, a baby suddenly bolting upright wide awake with a delighted grin, bed-head wild, eyes bright",78,"slideleft","pop@C,giggle@R30"),
            B("음 아직 졸린데 여긴 어디","졸린","50mm lens, dim warm nightlight, the same baby surfacing from sleep with the identical dazed cross-eyed stare, matched framing for a seamless loop",30,"fade","cute@C,suspense@R20") }),
          new("fort","sleep","🏰","이불 요새 건설","이불 속으로 사라진 아기 🏰","이불 요새를 짓는 건축가 아기 (페르소나: 요새 건축가)",1.15, new[]{
            B("나만의 요새를 설계한다","요새","Fisheye 8mm, soft blanket-filtered glow, a baby architect surveying a mountain of pastel blankets, tiny hands on hips, plotting eyes, bulged wide-angle world",30,"fade","pluck@C,drumroll@R25"),
            B("이 이불을 이렇게 끌어당겨","당겨","Fisheye 8mm, warm diffusion, a baby heaving a huge blanket overhead with grunting effort, fabric ballooning, cheeks puffed",48,"zoomin","whoosh@L30,boing@R25"),
            B("완벽한 은신처가 완성됐다","완벽","Fisheye 8mm, cozy filtered light, a baby peeking proudly from a blanket cave, only a grinning face and two fists visible",68,"slideleft","ding@C,sparkle@R30"),
            B("아무도 나를 찾을 수 없지","아무도","Fisheye 8mm, soft glow, a baby hiding under blankets with only sparkling eyes showing, a muffled giggle, fabric folds rounding the frame",72,"hblur","giggle@C,pop@R25"),
            B("어 요새가 무너졌다 콰르르","무너졌","Fisheye 8mm, bright diffusion, the blanket fort collapsing over a shrieking-with-laughter baby, an avalanche of soft folds",88,"slideleft","crash@C,boing@L30,giggle@R25"),
            B("자 다시 요새를 설계한다","다시","Fisheye 8mm, soft blanket-filtered glow, the same baby architect surveying the blanket mountain with hands on hips, matched framing for a seamless loop",30,"fade","pluck@C,drumroll@R25") }),
          new("sleeptalk","sleep","💤","잠꼬대 통역","아기 잠꼬대 자막 실화 ㅋㅋ 😴","무슨 꿈일까 잠꼬대 통역기 (페르소나: 꿈 통역사)",1.0, new[]{
            B("쿨쿨 깊은 꿈속을 헤맨다","꿈속","100mm macro, cool moonlight-blue nightlight, a peacefully sleeping baby, tiny chest rising, parted lips, a shimmering drool bead at the corner",25,"fade","cute@C,pluck@R20"),
            B("자막 음냐 맘마 더 주세요","맘마","100mm macro, blue glow, a baby mumbling in sleep, lips smacking softly, a fleeting frown then smile, a dream-twitching cheek",40,"zoomin","suspense@C"),
            B("자막 싫어 그건 안 먹어","싫어","100mm macro, moonlit, a baby scowling in a dream, nose scrunching, a tiny fist clenching by the cheek, comedic sleep-face",58,"slideleft","boing@L20,ding@R30"),
            B("자막 헤헤 그건 내 거야","내 거","100mm macro, soft blue, a sudden dreamy sleep-smile spreading, a dimple flickering, a contented sigh fogging the cool air",68,"hblur","giggle@C,sparkle@R25"),
            B("자막 우다다 잡히지 않아","우다다","100mm macro, moonlight, tiny legs kicking under the blanket as if running in a dream, blissful twitching, wild bed-hair",80,"slideleft","whoosh@L30,pop@R25"),
            B("쿨쿨 다시 꿈속으로","쿨쿨","100mm macro, cool moonlight-blue nightlight, the same peacefully sleeping baby with parted lips and a drool bead, matched framing for a seamless loop",25,"fade","cute@C,pluck@R20") }),
          // v16.10.0 확장 — 다양한 잠버릇
          new("sl_starfish","sleep","⭐","불가사리 대자 잠","침대 전체를 점령한 아기 ⭐","대자로 뻗은 잠버릇 (페르소나: 침대 정복자)",0.95, new[]{
            B("오늘도 침대는 내 것","침대","Bird's-eye 35mm, soft moonlight through curtains, a baby sleeping spread out like a starfish across the middle of a big bed, arms and legs flung wide",24,"fade","twinkle@C,cute@R20"),
            B("새근새근 영역 확장","확장","Bird's-eye 35mm, cool blue night light, the baby slowly stretching one leg further out in sleep, subtle breathing, the parents squeezed at the edges",36,"zoomin","suspense@C,pluck@R20"),
            B("발차기 한 방","발차기","50mm, moonlit, the sleeping baby's foot gently landing on the parent's cheek, the parent's comic sleepy grimace",58,"slideleft","boing@C,squeak@R25"),
            B("평화로운 정복자의 밤","평화","Bird's-eye 35mm, soft moonlight, the baby smiling in deep sleep, still spread wide as a starfish, peaceful night",28,"hblur","twinkle@C,cute@R20") }),
          new("sl_frog","sleep","🐸","엉덩이 들고 자기","개구리 자세로 꿀잠 🐸","엉덩이 하늘 향한 잠버릇 (페르소나: 꿈속 개구리)",0.95, new[]{
            B("이 자세가 제일 편해","자세","50mm, soft moonlight nursery, a baby sleeping face-down with knees tucked under and bottom raised high in a frog pose, cheek squished on the mattress",24,"fade","cute@C,twinkle@R20"),
            B("엉덩이가 들썩들썩","들썩","Macro 85mm, cool blue night light, the baby's raised bottom rising and falling gently with each breath, subtle breathing motion",36,"zoomin","pluck@C,suspense@R20"),
            B("쩝쩝 꿈속 간식","쩝쩝","Macro 85mm, moonlit, the sleeping baby smacking its lips in a dream, a tiny smile, cheek still squished",50,"slideleft","chomp@C,giggle@R20"),
            B("꿈속 개구리의 꿀잠","꿀잠","50mm, soft moonlight, the baby sighing deeply and settling further into the frog pose, peaceful night",26,"hblur","twinkle@C,cute@R20") }),
          new("sl_clock","sleep","🕐","360도 회전 잠버릇","자고 일어나면 방향이 바뀌어 있다 🔄","시계바늘처럼 도는 잠버릇 (페르소나: 꿈속 시계바늘)",1.0, new[]{
            B("분명 똑바로 잤는데","똑바로","Bird's-eye 35mm, soft moonlight, a baby tucked in neatly with head on the pillow at the top of the crib, peaceful sleeping face",24,"fade","cute@C,twinkle@R20"),
            B("새벽 두 시 반 바퀴","반 바퀴","Bird's-eye time-lapse, cool blue night light, the sleeping baby slowly rotating sideways across the mattress, blanket bunching, one consistent rotation direction",48,"zoomin","whoosh@C,suspense@R20"),
            B("발이 베개 위에 있다","베개","Bird's-eye 35mm, dawn light, the baby now lying completely sideways with feet resting on the pillow, still fast asleep",66,"slideleft","boing@C,squeak@R25"),
            B("아침 기상 개운해","기상","50mm, bright morning window light, the baby waking up upside down and grinning at the camera with messy hair",52,"hblur","giggle@C,sparkle@R25") }),
          new("sl_milkdrunk","sleep","🍼","분유 먹다 꿀잠","젖병 물고 스르륵 밀크 드렁크 🍼","배부름의 행복한 기절 (페르소나: 밀크 소믈리에)",0.9, new[]{
            B("오늘의 밀크 시음","시음","Macro 85mm, cozy warm tungsten lamp, a baby cradled in a parent's arm drinking from a bottle, tiny hands holding the bottle, focused eyes",26,"fade","cute@C,pluck@R20"),
            B("눈이 점점 감긴다","감긴다","Extreme macro, warm amber glow, the baby's eyelids slowly drooping as the sucking slows down, a milk droplet at the lip corner",36,"zoomin","twinkle@C,suspense@R20"),
            B("스르륵 밀크 드렁크","드렁크","Macro 85mm, cozy warm light, the bottle slipping gently from the lips as the baby falls asleep with mouth open, arms going limp, blissful face",42,"slideleft","twinkle@C,cute@R20"),
            B("배부른 천사의 미소","미소","50mm, soft candle-warm glow, the sleeping baby breaking into a tiny dreamy smile, milk-drunk and content",28,"hblur","sparkle@C,cute@R20") }),
          new("sl_carseat","sleep","🚗","카시트 꾸벅꾸벅","출발 5분 만에 기절하는 아기 🚗","카시트 수면 마법 (페르소나: 드라이브 수면왕)",0.95, new[]{
            B("나 하나도 안 졸려","안 졸려","50mm, soft afternoon sunlight through car window, a toddler buckled in a car seat insisting wide-eyed, a toy in hand",28,"fade","cute@C,suspense@R20"),
            B("눈꺼풀이 천근만근","천근만근","Macro 85mm, dappled light flickering through the window, the toddler's eyelids drooping slowly, head beginning to nod forward",40,"zoomin","twinkle@C,pluck@R20"),
            B("꾸벅 꾸벅 결국","꾸벅","50mm, warm golden light, the toddler's head nodding gently to one side against the car seat wing, the toy slipping from the hand",50,"slideleft","boing@C,twinkle@R20"),
            B("드라이브 수면왕 등극","수면왕","50mm, golden hour, the toddler fast asleep with mouth open, sunlight warming the cheek as trees pass outside",28,"hblur","twinkle@C,cute@R20") }),
          new("sl_dadchest","sleep","👨","아빠 배 위 낮잠","아빠 배 위가 세상에서 제일 편해 💤","오르락내리락 숨결 침대 (페르소나: 아빠 배 전세 계약자)",0.9, new[]{
            B("오늘의 침대는 아빠","아빠","50mm, soft overcast window light, a baby lying tummy-down on a sleeping father's chest on a sofa, cheek pressed against the shirt",24,"fade","cute@C,pluck@R20"),
            B("오르락내리락 숨결","숨결","Macro 85mm, diffused daylight, the baby rising and falling gently with the father's breathing, subtle breathing motion, eyes half closed",34,"zoomin","twinkle@C,suspense@R20"),
            B("둘이 똑같이 입 벌리고","똑같이","50mm, soft light, father and baby both asleep with identical open mouths, a comedic matching expression",54,"slideleft","giggle@C,squeak@R20"),
            B("세상 제일 편한 낮잠","낮잠","35mm, warm golden afternoon light, the father's hand resting on the sleeping baby's back, peaceful",26,"hblur","twinkle@C,cute@R20") }),
        }),

        // ═══ 3. 신체 / 운동 ═══
        new("body","🤸","신체·운동","걸음마·뒤집기·발달 챌린지", new List<BabyThemeDef>{
          new("penguin","body","🐧","펭귄 걸음마","2배속으로 보면 완벽한 펭귄 🐧","아장아장 펭귄 행진 (페르소나: 펭귄 행진 대장)",2.0, new[]{
            B("드디어 두 발로 기립한다","두 발","24mm low-angle, bright natural daylight, a baby in a puffy suit wobbling upright like a tiny penguin, arms stiff for balance, jelly thighs trembling",40,"fade","cute@C,drumroll@R25"),
            B("한 발 또 한 발 뒤뚱뒤뚱","뒤뚱","24mm low-angle, crisp light, a baby waddling forward with stiff penguin steps, arms flapping, diaper bottom swaying side to side",55,"zoomin","boing@L35,boing@R35,pop@C"),
            B("속도를 올린다 아무도 못 막아","속도","24mm low-angle, bright sun, a baby waddling faster across the floor, chubby legs pumping, motion blur, a gleeful determined face",70,"slideleft","whoosh@L40,boing@R30"),
            B("어어 균형이 흔들린다","위기","24mm low-angle, natural light, a baby's arms windmilling wildly to keep balance, eyes huge, teetering on tiptoe",78,"hblur","suspense@C,boing@R25"),
            B("엉덩방아 콰당 하지만 무사","콰당","24mm low-angle, bright daylight, a baby plopping onto its padded bottom with a bounce then bursting into laughter, legs in the air",90,"slideleft","crash@C,boing@L30,giggle@R25"),
            B("자 다시 두 발로 기립","다시","24mm low-angle, bright natural daylight, the same puffy-suited baby wobbling upright with arms out like a penguin, matched framing for a seamless loop",40,"fade","cute@C,drumroll@R25") }),
          new("gym","body","💪","아기 헬스장","오늘도 터미타임 3세트 프로의 자세 💪","엎드려 버티기 보디빌더 아기 (페르소나: 근성의 보디빌더)",1.0, new[]{
            B("오늘의 목표 터미타임 삼세트","목표","35mm lens, hard dramatic side key light, a baby lying prone lifting its head like a bodybuilder, sweat-sheen on the brow, jaw clenched, playmat texture",38,"fade","cute@C,drumroll@R25"),
            B("일세트 고개 들고 버틴다","버틴다","35mm macro, sharp side light, a baby straining to lift head and chest, neck muscles taut, a grunting focused scowl",50,"zoomin","suspense@L20,boing@R25"),
            B("부들부들 이것이 근성이다","근성","35mm lens, hard shadow, a baby's arms trembling violently under effort, a bead of sweat rolling, veiny determination, a quivering lip",68,"slideleft","boing@L30,squeak@R35"),
            B("팔에 힘이 스르륵 위기다","위기","35mm, dramatic light, a baby's arms buckling and slowly giving out, wide panicked eyes, a wobble to one side",76,"hblur","suspense@C,boing@R25"),
            B("풀썩 완벽한 마무리 자세","완벽","35mm lens, gym key light, a baby flopping face-down then popping its head up with a proud triumphant grin, flushed cheeks",88,"slideleft","crash@C,giggle@L20,pop@R30"),
            B("좋아 다음 세트 다시 엎드려","다음","35mm lens, hard dramatic side key light, the same baby getting back into the prone lift position with a game face, matched framing for a seamless loop",38,"fade","cute@C,drumroll@R25") }),
          new("rollover","body","🔄","뒤집기 챌린지","뒤집기 3일째 도전 결말은 😤","첫 뒤집기 대성공 프로젝트 (페르소나: 뒤집기 도전자)",1.15, new[]{
            B("오늘은 반드시 성공한다","성공","Top-down 50mm, soft even daylight, a baby lying on its back with a fierce determined face, fists pumped, playmat below, rosy cheeks",35,"fade","cute@C,drumroll@R25"),
            B("영차 온몸에 힘을 준다","영차","Top-down 50mm, gentle light, a baby straining sideways to flip, red-faced effort, one leg hoisted, tongue poking out",52,"zoomin","suspense@L20,boing@R25"),
            B("거의 다 왔어 조금만 더","거의","Top-down 50mm, soft glow, a baby stuck half-rolled onto its side, legs bicycling, grunting with effort, a cheek squished to the mat",70,"slideleft","boing@C,squeak@R30"),
            B("어 다시 원래대로 굴러갔네","다시","Top-down 50mm, daylight, a baby flopping back flat with a frustrated pout, arms splayed, an exasperated huff",64,"hblur","boing@L25,ding@R25"),
            B("드디어 뒤집었다 만세","드디어","Top-down 50mm, bright soft light, a baby finally flipping onto its tummy then beaming up with pride, arms spread like a champion",90,"slideleft","sparkle@C,giggle@L20,clap@R30"),
            B("한 번 더 도전한다","한 번","Top-down 50mm, soft even daylight, the same baby on its back with the identical determined face and pumped fists, matched framing for a seamless loop",35,"fade","cute@C,drumroll@R25") }),
          new("stairs","body","🪜","계단 등반가","계단 앞에서 눈빛이 달라진 아기 🧗","한 칸 한 칸 도전하는 등반가 (페르소나: 정상 정복 등반가)",1.0, new[]{
            B("저 위가 나를 부른다","위","20mm wide low-angle, warm hallway light, a baby gazing up a towering staircase with mountaineer ambition, a tiny hand reaching, dramatic scale",32,"fade","cute@C,suspense@R25"),
            B("첫 발판에 손을 건다","첫 발판","20mm wide, warm glow, tiny fingers gripping the first stair edge, knuckles whitening, a determined heave upward",48,"zoomin","boing@L25,suspense@R20"),
            B("영차 한 칸을 정복했다","영차","20mm wide low-angle, warm light, a baby hauling itself up one step with whole-body effort, a leg swinging high, triumphant strain",68,"slideleft","suspense@C,boing@R30"),
            B("높다 조금 무섭기도 하고","무섭","20mm wide, warm hallway, a baby freezing mid-climb with a nervous wide-eyed glance downward, gripping tight, a gulp",74,"hblur","suspense@C,squeak@R25"),
            B("정상 정복 등반가 만세","정복","20mm wide low-angle, warm glow, a baby reaching the top step and flinging both arms up in victory, a conqueror's grin",88,"slideleft","sparkle@C,giggle@L20,pop@R30"),
            B("자 다음 봉우리를 오른다","다음","20mm wide low-angle, warm hallway light, the same baby gazing up the staircase with the identical climber's ambition, matched framing for a seamless loop",32,"fade","cute@C,suspense@R25") }),
          new("gymball","body","⚽","짐볼 챔피언","짐볼 위 아기 균형 실화 ⚽","짐볼 위에서 통통 챔피언 (페르소나: 균형의 서커스)",1.0, new[]{
            B("이 거대한 공에 오른다","공","35mm lens, bright airy studio light, a baby perched atop a big exercise ball like a circus act, arms out, wobbling with cautious wonder, a bouncy highlight",34,"fade","cute@C,boing@R25"),
            B("통통 반동을 시험한다","통통","35mm, clean light, a baby gently bouncing on the ball, cheeks jiggling with each rebound, wide delighted eyes",52,"zoomin","boing@C,pop@R30"),
            B("어 균형이 사르르 무너져","균형","35mm lens, airy glow, a baby wobbling precariously on the ball, arms flailing for balance, a nervous open-mouth grin",70,"slideleft","suspense@C,boing@R30"),
            B("재밌잖아 더 높이 통통","더 높이","35mm, bright studio, a baby laughing and bouncing higher, hair lifting on each rebound, pure exhilaration",78,"hblur","giggle@L20,boing@R30"),
            B("최고의 점프 착지 성공","점프","35mm lens, bright light, a baby bouncing high with a huge open grin, arms spread, mid-air joy frozen",90,"slideleft","boing@C,sparkle@L20,giggle@R30"),
            B("한 번 더 공에 오른다","한 번","35mm lens, bright airy studio light, the same baby perched atop the exercise ball with arms out and cautious wonder, matched framing for a seamless loop",34,"fade","cute@C,boing@R25") }),
        }),

        // ═══ 4. 감정 / 소통 ═══
        new("emotion","🥰","감정·소통","까꿍·삐짐·하이파이브 리액션", new List<BabyThemeDef>{
          new("peekaboo","emotion","🙈","까꿍 반전","다음 표정을 아무도 예측 못 함 😲","숨었다 나타나는 반전의 미학 (페르소나: 반전 매직 마술사)",1.15, new[]{
            B("지금부터 완벽하게 사라진다","사라진다","Macro 85mm, soft ring-light catchlight, a baby magician covering its face with two pudgy hands, fingers slightly splayed, a ring-glint in the peeking eyes",34,"fade","cute@C,suspense@R25"),
            B("아무도 나를 볼 수 없지 후후","아무도","Macro 85mm, ring glow, a baby peeking through a gap in its fingers, a mischievous glint, holding a theatrical pause",44,"zoomin","suspense@C,ding@R25"),
            B("셋 둘 하나 까꿍","까꿍","Macro 85mm, bright ring catchlight, a baby flinging its hands open to reveal a giant surprised smile, eyes exploding wide, a comedic pop",82,"slideleft","pop@C,boing@L30,giggle@R25"),
            B("어때 놀랐지 반응 최고","놀랐지","Macro 85mm, soft ring light, a baby laughing in delight at its own reveal, scrunched happy eyes, dimples deep",76,"hblur","giggle@C,sparkle@R30"),
            B("한 번 더 이번엔 진짜야","한 번 더","Macro 85mm, ring glow, a baby quickly clapping its hands back over its face for another round, playful anticipation",70,"slideleft","whoosh@L30,cute@R25"),
            B("지금부터 완벽하게 사라진다","완벽하게","Macro 85mm, soft ring-light catchlight, the same baby covering its face with splayed fingers and peeking eyes, matched framing for a seamless loop",34,"fade","cute@C,suspense@R25") }),
          new("sulk","emotion","😤","삐짐 3단 콤보","삐진 아기 달래기 난이도 최상 😤","삐짐 → 외면 → 못 이기는 척 (페르소나: 삐짐 협상가)",1.0, new[]{
            B("흥 나 지금 단단히 삐졌어","삐졌","50mm lens, moody soft window side-light, a baby turning away with a dramatic pout, puffed cheeks, one eye glinting under the shadow",35,"fade","suspense@C,ding@R25"),
            B("불러도 절대 안 돌아봐","절대","50mm, low soft light, a baby folding its arms and staring at the wall, lower lip trembling, chin tucked in defiance",50,"zoomin","boing@L25"),
            B("살짝 봤지만 못 본 척","못 본 척","50mm lens, moody glow, a baby stealing a quick sideways peek then snapping its head back away, betrayed by a twitching smile",65,"slideleft","boing@C,squeak@R30"),
            B("흥 그래도 나는 화났어","흥","50mm, soft window, a baby fighting back a grin, cheeks quivering with the effort to stay mad, arms still crossed",72,"hblur","suspense@L20,pop@R25"),
            B("못 이기는 척 헤헤 풀렸다","헤헤","50mm lens, warming light, a baby finally cracking into a helpless giggle, arms unfolding, forgiveness in the sparkling eyes",84,"slideleft","giggle@C,sparkle@R30"),
            B("자 다시 삐질 준비 완료","삐질","50mm lens, moody soft window side-light, the same baby turning away with the identical dramatic pout and puffed cheeks, matched framing for a seamless loop",35,"fade","suspense@C,ding@R25") }),
          new("highfive","emotion","🙏","첫 하이파이브","드디어 손바닥이 마주친 순간 🙌","하이파이브 성공까지의 여정 (페르소나: 하이파이브 스나이퍼)",1.0, new[]{
            B("저 손바닥 목표물을 포착했다","목표물","35mm lens, bright clean daylight, a baby eyeing a raised open palm like a sniper acquiring a target, one eye squinting, a tiny hand cocked",32,"fade","cute@C,ding@R25"),
            B("살짝 조준하고 접근","조준","35mm macro, crisp light, a baby's chubby hand inching toward the palm with intense aim, tongue poking out in concentration",48,"zoomin","suspense@C"),
            B("어 빗나갔다 허공을 쳤어","빗나","35mm lens, clean light, a baby's hand swiping past the target through empty air, a puzzled wide-eyed double-take",64,"slideleft","boing@L25,squeak@R30"),
            B("다시 정확히 재조준한다","정확","35mm, bright glow, a baby recalibrating with fierce focus, palm hovering, holding a dramatic drumroll pause",72,"hblur","suspense@C,drumroll@R25"),
            B("짝 명중 미션 성공","명중","35mm lens, bright light, a tiny palm smacking the target palm dead-center, a triumphant grin exploding, a sparkle of contact",88,"slideleft","clap@C,sparkle@L20,giggle@R30"),
            B("자 다음 목표물을 포착","다음","35mm lens, bright clean daylight, the same baby eyeing a fresh raised palm with the identical sniper squint, matched framing for a seamless loop",32,"fade","cute@C,ding@R25") }),
          new("kiss","emotion","💋","뽀뽀 세례","아기 뽀뽀 공격 심장 폭격 💕","쪽쪽 뽀뽀 세례 퍼붓기 (페르소나: 사랑 폭격기)",1.0, new[]{
            B("사랑을 전할 준비가 됐다","사랑","100mm macro, warm rosy light, a baby leaning in with puckered glossy lips and adoring half-closed eyes, a tiny drool shine, blush on the cheeks",34,"fade","cute@C,sparkle@R25"),
            B("일단 쪽 한 방 발사","쪽","100mm macro, soft warm glow, a baby planting a big wet kiss right on the lens, lips squished flat, a smudge of drool",55,"zoomin","kiss@C,pop@R25"),
            B("또 쪽 또 쪽 연발이다","연발","100mm macro, rosy light, a baby delivering rapid-fire kisses, cheeks bouncing, joyful energy, glistening lips",70,"slideleft","kiss@L30,giggle@R30"),
            B("멈출 수 없는 사랑의 폭격","폭격","100mm macro, warm glow, a baby giggling between machine-gun kisses, a drooly grin, eyes squeezed with affection",78,"hblur","giggle@C,sparkle@R25"),
            B("사랑해 최종 뽀뽀 폭탄 투하","폭탄","100mm macro, bright rosy light, a baby showering kisses everywhere with total abandon, pure love overflowing, sparkling eyes",86,"slideleft","kiss@C,pop@L20,sparkle@R30"),
            B("자 다시 사랑을 장전한다","다시","100mm macro, warm rosy light, the same baby leaning in with puckered glossy lips and adoring eyes, matched framing for a seamless loop",34,"fade","cute@C,sparkle@R25") }),
          new("mood","emotion","🎭","눈치 게임","1초마다 표정 바뀌는 아기 🎭","웃다 울다 표정 롤러코스터 (페르소나: 표정 롤러코스터 배우)",1.15, new[]{
            B("지금 내 기분은 아주 좋아","좋아","50mm lens, neutral soft light, a baby method-actor beaming a bright pure smile, eyes crinkled, cheeks glowing",36,"fade","cute@C,ding@R25"),
            B("어 갑자기 확 시무룩","시무룩","50mm, even light, the smile instantly crashing into a downturned sulk, lip pushed out, a cloud passing over the face",55,"zoomin","suspense@C,boing@R25"),
            B("아니 다시 방긋 반전","방긋","50mm lens, soft glow, the sulk flipping back to a radiant grin in a blink, sunshine returning, dimples popping",70,"slideleft","pop@C,giggle@R30"),
            B("살짝 삐질까 말까 간 봐","간 봐","50mm, neutral light, a baby testing a playful half-frown, side-eyeing for a reaction, lips twitching between moods",66,"hblur","boing@L20,squeak@R25"),
            B("결국 까르르 대폭발","까르르","50mm lens, bright soft light, a baby erupting into uncontrollable belly giggles, head tipping back, joyful chaos",86,"slideleft","giggle@C,sparkle@R30"),
            B("자 다시 기분은 아주 좋아","다시","50mm lens, neutral soft light, the same baby beaming the identical bright pure smile with crinkled eyes, matched framing for a seamless loop",36,"fade","cute@C,ding@R25") }),
        }),

        // ═══ 5. 야외 / 자연 ═══
        new("outdoor","🌿","야외·자연","첫 눈·바다·낙엽 첫 경험", new List<BabyThemeDef>{
          new("snow","outdoor","❄️","생애 첫 눈","처음 본 눈… 철학이 시작된다 ❄️","하얀 것의 정체를 탐구 (페르소나: 눈의 철학자)",1.0, new[]{
            B("하늘이 하얀 질문을 던진다","하얀","Macro 85mm, crisp overcast winter light with drifting sparkle, a bundled baby philosopher gazing up at falling snowflakes, lashes dusted white, breath fogging",34,"fade","sparkle@C,cute@R25"),
            B("이것의 정체를 밝혀내겠다","정체","Macro 85mm, cool light, a single crystalline snowflake landing on a baby's outstretched mitten, an ultra-detailed ice lattice, wondering eyes",44,"zoomin","sparkle@L20,suspense@C"),
            B("잡았는데 손에서 사라졌어","사라졌","Macro 85mm, winter glow, a baby staring shocked at an empty wet mitten where the flake vanished, philosophical bewilderment",64,"slideleft","boing@C,ding@R25"),
            B("이 차가운 감촉 나쁘지 않아","감촉","Macro 85mm, sparkling overcast, a baby breaking into a delighted giggle as snow kisses its rosy cheeks, a warm breath cloud",74,"hblur","giggle@L20,sparkle@R30"),
            B("온몸으로 첫 눈을 맞이한다","만세","Macro 85mm, bright winter sparkle, a bundled baby flinging its arms up amid swirling snow, pure laughing joy, flakes on the hood",88,"slideleft","whoosh@L35,giggle@C,sparkle@R30"),
            B("자 다음 눈송이는 어디에","다음","Macro 85mm, crisp overcast winter light with drifting sparkle, the same bundled baby gazing up at the snow with dusted lashes, matched framing for a seamless loop",34,"fade","sparkle@C,cute@R25") }),
          new("sea","outdoor","🌊","생애 첫 바다","파도 앞 아기의 첫 반응 🌊","드넓은 바다와의 첫 만남 (페르소나: 파도 정복자)",1.0, new[]{
            B("저 거대한 물은 대체 뭐지","거대한 물","Ultra-wide 14mm, bright coastal midday sun, a tiny baby facing the vast glittering ocean, wind lifting wisps of hair, awestruck wide eyes, a huge sky",32,"fade","cute@C,suspense@R25"),
            B("용감하게 발끝을 담근다","발끝","Ultra-wide macro, sparkling sunlight, baby toes touching the frothy edge of a wave, sand clinging between them, a cautious curl",48,"zoomin","splash@C,suspense@R20"),
            B("으 차가워 깜짝이야","차가워","Ultra-wide 14mm, bright sun, a baby flinching back as a cold wave rushes over its feet, arms flying up, a shocked squeal face",72,"slideleft","splash@C,squeak@R35"),
            B("어 이거 은근 재밌잖아","재밌","Ultra-wide 14mm, coastal glow, a baby laughing and stomping in the shallow surf, water beads flying, a sunlit spray halo",78,"hblur","splash@L30,giggle@R25"),
            B("파도랑 놀자 정복 완료","정복","Ultra-wide 14mm, brilliant sun flare, a baby joyfully splashing the waves with both hands, drenched and delighted, the ocean horizon behind",88,"slideleft","splash@C,boing@L20,giggle@R30"),
            B("자 다음 파도를 정복한다","다음","Ultra-wide 14mm, bright coastal midday sun, the same tiny baby facing the vast ocean with awestruck wide eyes, matched framing for a seamless loop",32,"fade","cute@C,suspense@R25") }),
          new("leaves","outdoor","🍂","낙엽 밟기","바스락 소리에 중독된 아기 🍂","가을 낙엽 바스락 챌린지 (페르소나: 바스락 수집가)",1.15, new[]{
            B("바닥이 온통 알록달록하다","알록","35mm lens, warm autumn golden light, a baby standing amid a carpet of red and gold leaves, one leaf stuck to a mitten, curious wonder, backlit foliage",32,"fade","pluck@C,cute@R25"),
            B("한 발 조심스레 밟아본다","밟아","35mm macro, golden glow, a tiny boot pressing onto a crisp curled leaf, edges crackling, anticipation on the peering face",48,"zoomin","suspense@C"),
            B("바스락 이 소리에 중독됐다","바스락","35mm lens, autumn light, a baby lighting up at the crunch underfoot, wide sparkling eyes, stomping again eagerly",68,"slideleft","pop@C,sparkle@R30"),
            B("낙엽을 하늘로 뿌린다","뿌려","35mm, warm sun, a baby tossing a double handful of leaves overhead, foliage raining down, a gleeful open-mouth laugh",76,"hblur","whoosh@L30,giggle@R25"),
            B("낙엽 비를 맞으며 만세","만세","35mm lens, golden hour, a baby laughing under a shower of falling autumn leaves, arms wide, leaves in the hair, pure joy",88,"slideleft","sparkle@C,giggle@L20,boing@R25"),
            B("자 또 바스락 밟으러 간다","또","35mm lens, warm autumn golden light, the same baby standing in the leaf carpet with a leaf on its mitten, curious, matched framing for a seamless loop",32,"fade","pluck@C,cute@R25") }),
          new("bubble","outdoor","🫧","비눗방울 추격","비눗방울 잡으려는 아기 실화 🫧","반짝이는 방울과의 추격전 (페르소나: 방울 추격자)",1.15, new[]{
            B("반짝이는 것이 떠다닌다","반짝","50mm lens, soft sunlit backlight with rainbow refractions, a baby's eyes tracking floating soap bubbles, shimmering spheres, mouth open in wonder",32,"fade","sparkle@C,cute@R25"),
            B("살금살금 손을 뻗는다","뻗는다","50mm macro, iridescent glow, a baby reaching a single tentative finger toward a wobbling rainbow bubble, breath held",50,"zoomin","suspense@C,ding@R25"),
            B("어 톡 터져버렸다","터졌","50mm lens, sunlit sparkle, a bubble popping on the baby's fingertip in a fine mist, a startled blink, a droplet on the nose",68,"slideleft","pop@C,squeak@R30"),
            B("이번엔 반드시 잡는다","잡는다","50mm, soft light, a baby toddling determinedly after a cluster of drifting bubbles, arms outstretched, a focused chase",76,"hblur","boing@L25,whoosh@R30"),
            B("방울 잔치 한가운데 만세","잔치","50mm lens, glittering backlight, a baby laughing surrounded by dozens of rainbow bubbles, popping them with both hands, radiant",88,"slideleft","pop@C,sparkle@L20,giggle@R30"),
            B("자 다음 방울을 쫓는다","다음","50mm lens, soft sunlit backlight with rainbow refractions, the same baby tracking floating bubbles with wonder, matched framing for a seamless loop",32,"fade","sparkle@C,cute@R25") }),
          new("grass","outdoor","🌾","강아지풀 간지럼","강아지풀에 무장해제된 아기 🌾","간지럼 참기 챌린지 실패기 (페르소나: 간지럼 저항군)",1.0, new[]{
            B("이 풀은 참 보드랍구나","보드랍","100mm macro, dappled meadow sunlight, a baby curiously fingering a soft foxtail grass, fuzzy seed head, downy cheeks glowing, bokeh greenery",30,"fade","pluck@C,cute@R25"),
            B("볼에 살짝 대보면 어떨까","볼에","100mm macro, warm dapples, the foxtail brushing a baby's plump cheek, fine fuzz tickling the skin, an anticipatory twitch",48,"zoomin","suspense@C"),
            B("큭 간지러워 참아야 해","간지러","100mm macro, sunlit, a baby scrunching up mid-tickle, shoulders hunching, a giggle bursting through pressed lips",70,"slideleft","giggle@C,boing@R25"),
            B("안 웃을 거야 절대로 안 웃어","절대","100mm macro, dappled light, a baby fighting a laugh with all its might, cheeks trembling, lips clamped, losing badly",66,"hblur","suspense@L20,squeak@R25"),
            B("결국 빵 터졌다 항복","빵","100mm macro, bright meadow sun, a baby exploding into helpless belly laughter, head thrown back, tears of joy at the lashes",88,"slideleft","giggle@C,sparkle@L20,pop@R30"),
            B("자 한 번 더 간지럼 도전","한 번","100mm macro, dappled meadow sunlight, the same baby fingering the soft foxtail grass with curious downy cheeks, matched framing for a seamless loop",30,"fade","pluck@C,cute@R25") }),
          // v16.10.0 확장
          new("od_dandelion","outdoor","🌼","민들레 씨앗 후","후~ 불면 하늘로 날아가요 🌬️","민들레 홀씨 날리기 (페르소나: 바람의 요정)",1.0, new[]{
            B("하얀 솜방울 발견","솜방울","100mm macro, backlit golden hour, a toddler crouching in a meadow holding a white dandelion puffball, fluffy seeds glowing in the sun",26,"fade","twinkle@C,cute@R20"),
            B("볼 빵빵 후","후","Extreme macro, golden backlight, the toddler puffing up both cheeks and blowing at the dandelion, tiny lips pursed",52,"zoomin","whoosh@C,suspense@R20"),
            B("씨앗이 날아간다","날아간다","85mm, sun flare, the dandelion seeds drifting forward and upward on the breeze, the toddler watching with mouth open in wonder",76,"slideleft","sparkle@C,twinkle@R25"),
            B("바람의 요정 완성","요정","50mm, golden meadow light, the toddler spinning happily in the grass holding the bare stem, seeds floating away in the distance",50,"hblur","giggle@C,sparkle@R25") }),
          new("od_tadpole","outdoor","🐸","개울 올챙이 관찰","꼬물꼬물 올챙이와 첫 만남 🐸","시냇가 꼬마 생물학자 (페르소나: 개울 탐험가)",1.0, new[]{
            B("개울에 뭔가 꼬물꼬물","꼬물꼬물","35mm, dappled forest sunlight, a toddler in rain boots squatting at the edge of a shallow clear stream, peering into the water",28,"fade","suspense@C,pluck@R20"),
            B("투명한 컵으로 쏙","컵","Macro 85mm, sunny dappled light, the toddler scooping water into a clear plastic cup, tiny tadpoles swimming inside",54,"zoomin","splash@C,pop@R20"),
            B("눈앞에서 헤엄친다","헤엄","Extreme macro, bright daylight, the toddler's huge eye seen through the cup as tadpoles wriggle forward, an astonished gasp",74,"slideleft","ding@C,sparkle@R25"),
            B("안녕 다시 집으로","안녕","50mm, golden light, the toddler gently pouring the tadpoles back into the stream and waving goodbye",42,"hblur","cute@C,twinkle@R25") }),
          new("od_firefly","outdoor","✨","반딧불이 밤","밤하늘 별이 내려온 줄 알았어 ✨","여름밤 반딧불이 숲 (페르소나: 꼬마 별지기)",0.95, new[]{
            B("깜깜한 숲에 왔어요","숲","35mm, deep blue night with soft moonlight, a toddler in pajamas holding a parent's hand at the edge of a summer forest, fireflies starting to twinkle",24,"fade","suspense@C,twinkle@R20"),
            B("반짝 별이 날아다녀","반짝","85mm, night with twinkling firefly glow, tiny lights drifting slowly through the air around the toddler, reflected in wide eyes",54,"zoomin","twinkle@C,sparkle@R20"),
            B("손바닥에 살포시","살포시","Extreme macro, moonlit, a single glowing firefly landing gently on the toddler's open palm, the toddler holding its breath",70,"slideleft","sparkle@C,suspense@R20"),
            B("별지기의 소원","소원","50mm, starlight and firefly glow, the toddler watching the firefly fly upward into the night sky with a dreamy smile",34,"hblur","twinkle@C,cute@R20") }),
          new("od_kite","outdoor","🪁","첫 연날리기","하늘 높이 날아라 꼬마 연 🪁","들판 연날리기 대회 (페르소나: 바람 조종사)",1.05, new[]{
            B("내 연이 하늘로 간다","연","24mm wide, bright windy daylight, a toddler on a grassy hill holding the spool of a small fish-shaped kite, parent kneeling behind",28,"fade","drumroll@C,cute@R20"),
            B("달려라 바람 타고","달려라","tracking 35mm, sunny blue sky, the toddler running forward across the field as the kite lifts upward behind on the wind, string tightening",68,"zoomin","whoosh@C,giggle@R25"),
            B("높이 높이 날았다","높이","low-angle 35mm, bright blue sky, the kite soaring high with ribbons fluttering, the toddler looking up in amazement, mouth open",82,"slideleft","whoosh@L25,sparkle@R25"),
            B("바람 조종사 성공","성공","50mm, golden afternoon light, the toddler proudly holding the string as the kite dances steadily in the sky",50,"hblur","clap@C,giggle@R25") }),
        }),

        // ═══ 6. 일상 / 목욕 ═══
        new("daily","🛁","일상·목욕","목욕·거품·오리 인형·양치 대소동", new List<BabyThemeDef>{
          new("bath","daily","💦","목욕 대참사","3초 뒤 온 집안 물바다 🌊","물장구 스위치 켜진 순간 (페르소나: 물장구 파괴자)",1.0, new[]{
            B("오늘은 우아하게 목욕하겠다","우아","24mm wide, bright bathroom light with dancing water caustics, a baby sitting serenely in a bubble bath, glistening droplets, a foam crown, angelic calm",32,"fade","cute@C,splash@R25"),
            B("거품이 참 흥미로운 물질이군","거품","24mm macro, caustic sparkle, a baby poking a mountain of white foam with one finger, bubbles clinging, a studious frown",44,"zoomin","pop@C,suspense@R20"),
            B("이 물을 때리면 어찌 될까","때리면","24mm wide, bright light, a baby raising both hands over the water with a mischievous spark igniting in its eyes, held breath",64,"slideleft","suspense@C,ding@R25"),
            B("첨벙 온 욕실이 물바다다","첨벙","24mm wide, water caustics flying, a baby smacking the bath in a giant double splash, droplets frozen in the light, gleeful destruction",90,"hblur","splash@C,crash@L30,giggle@R25"),
            B("나는 즐겁다 엄마 표정은 몰라","즐겁다","24mm wide, bright glow, a soaked giggling baby splashing wildly, foam and water everywhere, a huge delighted grin",85,"slideleft","splash@L25,boing@C,giggle@R30"),
            B("자 다시 우아하게 목욕","다시","24mm wide, bright bathroom light with dancing water caustics, the same baby sitting serenely in the bubble bath with a foam crown, matched framing for a seamless loop",32,"fade","cute@C,splash@R25") }),
          new("lotion","daily","🧴","로션 도망자","로션만 보면 도망가는 아기 🏃","보습 크림과의 추격전 (페르소나: 로션 대도)",1.5, new[]{
            B("로션이다 대도는 튄다","로션","35mm handheld, soft warm light, a baby spotting the lotion pump and freezing like a cornered cat burglar, eyes darting, muscles coiled",34,"fade","suspense@C,ding@R25"),
            B("작전상 후퇴 전속력으로","후퇴","35mm handheld, motion-blurred warm light, a baby crawling away at top speed from the lotion, a giggling escape, blanket trailing",55,"zoomin","whoosh@L35,boing@R25"),
            B("잡혔다 으악 체포됐어","으악","35mm, soft light, a baby caught mid-escape and gently scooped up, a dramatic betrayed face, legs still pedaling air",72,"slideleft","squeak@C,boing@R30"),
            B("차갑고 미끌 소름이 돋아","미끌","35mm handheld, warm glow, a baby shivering at the cool slippery lotion on its cheek, an exaggerated goosebump grimace",70,"hblur","suspense@L20,pop@R25"),
            B("근데 이 부드러움 의외로 좋아","부드러움","35mm, cozy light, a baby melting into contentment as the soft cream massage soothes, half-closed eyes, a tiny satisfied sigh",78,"slideleft","cute@C,sparkle@R25"),
            B("자 다시 도망칠 준비","도망","35mm handheld, soft warm light, the same baby spotting the lotion pump and freezing like a cornered burglar, matched framing for a seamless loop",34,"fade","suspense@C,ding@R25") }),
          new("brush","daily","🪥","양치 전쟁","입 꾹 다문 아기 협상의 기술 🦷","칫솔 vs 아기 신경전 (페르소나: 양치 협상가)",1.15, new[]{
            B("입은 협상 대상이 아니다","협상","Macro 85mm, clean cool bathroom light, a baby clamping its mouth shut against an approaching toothbrush, cheeks bulging, a defiant negotiator's glare",35,"fade","suspense@C,ding@R25"),
            B("칫솔은 나의 적이다","적","Macro 85mm, crisp light, a baby eyeing the toothbrush with deep suspicion, lips sealed in a tight line, one brow arched",50,"zoomin","boing@L25"),
            B("어 딸기맛 이건 반칙인데","딸기맛","Macro 85mm, cool glow, a baby's eyes widening in surprised delight at the strawberry paste flavor, tongue sampling cautiously",68,"slideleft","ding@C,pop@R25"),
            B("생각보다 나쁘지 않군","나쁘지","Macro 85mm, clean light, a baby tentatively allowing the brushing, foam gathering at the lips, softening resistance",72,"hblur","suspense@L20,cute@R25"),
            B("치카치카 이제 신난다","치카","Macro 85mm, bright cool light, a baby happily brushing with foam frothing everywhere, giggling around the brush, sparkling teeth",84,"slideleft","giggle@C,sparkle@R30"),
            B("자 다시 입을 꾹 다문다","다시","Macro 85mm, clean cool bathroom light, the same baby clamping its mouth shut against the toothbrush with a defiant glare, matched framing for a seamless loop",35,"fade","suspense@C,ding@R25") }),
          new("diaper","daily","👶","기저귀 탈출","기저귀 갈기 = 로데오 경기 🤠","가만히 안 있는 아기와의 사투 (페르소나: 기저귀 로데오 선수)",1.5, new[]{
            B("기저귀 로데오 개막을 알린다","로데오","40mm lens, soft nursery light, a baby wriggling energetically on the changing mat like a rodeo bronco, legs kicking, gleeful defiance, a pastel blanket",38,"fade","boing@C,cute@R25"),
            B("데굴데굴 회피 기동","데굴","40mm, gentle glow, a baby rolling away mid-change with a giggling escape, chubby limbs flailing, a half-off diaper",58,"zoomin","whoosh@L30,giggle@R25"),
            B("두 발로 강력하게 저항","저항","40mm lens, soft light, a baby bicycle-kicking its little legs in playful resistance, toes splayed, a triumphant squeal",74,"slideleft","boing@C,squeak@R30"),
            B("잡았다 싶었지 또 도망","또 도망","40mm, nursery glow, a baby squirming free again the instant it is held, a slippery wriggle, a cheeky over-the-shoulder grin",76,"hblur","boing@L25,pop@R25"),
            B("결국 항복 헤헤 성공","항복","40mm lens, soft light, a baby finally going limp with a mischievous surrendering grin, a fresh diaper on, a victorious parent unseen",82,"slideleft","giggle@C,sparkle@R30"),
            B("자 다시 로데오 개막","다시","40mm lens, soft nursery light, the same baby wriggling on the changing mat like a rodeo bronco with kicking legs, matched framing for a seamless loop",38,"fade","boing@C,cute@R25") }),
          new("laundry","daily","🧺","빨래 방해꾼","갠 빨래 위로 다이빙하는 아기 🧺","빨래 개기 훼방 전문가 (페르소나: 빨래 방해 요원)",1.15, new[]{
            B("저 각진 옷 산이 수상하다","옷 산","20mm wide, bright airy daylight, a baby eyeing a neat pile of folded laundry like a saboteur casing a target, a plotting grin, sunlit fabric",32,"fade","cute@C,ding@R25"),
            B("정상까지 기어오른다","정상","20mm wide, clean light, a baby clambering onto the soft laundry stack, hands sinking into towels, a determined ascent",50,"zoomin","boing@L25,suspense@R20"),
            B("폭신 다이빙 입수","다이빙","20mm wide, bright glow, a baby belly-flopping face-first into the folded clothes, fabric puffing up around it, muffled glee",72,"slideleft","boing@C,pop@R25"),
            B("옷을 사방으로 흩뿌린다","흩뿌려","20mm wide, airy light, a baby flinging folded clothes into the air with both arms, garments raining, a mischievous cackle",76,"hblur","whoosh@L30,giggle@R25"),
            B("빨래 눈사태 한복판 만세","눈사태","20mm wide, bright daylight, a baby laughing buried in an avalanche of scattered laundry, a sock on its head, arms up",88,"slideleft","crash@C,giggle@L20,sparkle@R30"),
            B("자 다시 옷 산을 노린다","다시","20mm wide, bright airy daylight, the same baby eyeing a neat laundry pile with a plotting saboteur grin, matched framing for a seamless loop",32,"fade","cute@C,ding@R25") }),
          // v16.10.0 확장 — 목욕·일상
          new("dl_foambeard","daily","🧔","거품 수염 놀이","거품 수염 붙인 꼬마 할아버지 🛁","욕조 속 거품 분장쇼 (페르소나: 거품 산타)",1.0, new[]{
            B("거품 산이 생겼다","거품","24mm wide, bright bathroom light with water caustics, a baby sitting in a bubble bath scooping up a big handful of white foam, curious eyes",28,"fade","pop@C,cute@R20"),
            B("턱에 붙이면 수염","수염","Macro 85mm, bright softbox bathroom light, the baby patting foam onto its chin to make a fluffy white beard, foam clinging naturally",56,"zoomin","squeak@C,suspense@R20"),
            B("호호호 거품 산타","산타","50mm, bright light, the foam-bearded baby giggling with a foam crown on its head, bubbles drifting upward and popping",78,"slideleft","giggle@C,sparkle@R30"),
            B("후 불면 사라져요","후","Macro 85mm, sparkling caustics, the baby blowing the foam beard off, foam flecks floating away, a delighted squeal",60,"hblur","pop@C,giggle@R25") }),
          new("dl_duck","daily","🦆","오리 인형 목욕","삑삑 오리 친구와 목욕 파티 🦆","러버덕 함대 사령관 (페르소나: 오리 함장)",1.0, new[]{
            B("오리 함대 집합","함대","24mm wide, bright bathroom daylight, a baby sitting in a shallow tub with a fleet of yellow rubber ducks floating around, eyes wide",26,"fade","cute@C,squeak@R20"),
            B("삑삑 꾹 눌러봐","삑삑","Macro 85mm, bright softbox, the baby squeezing a rubber duck with both hands, a little water squirting out of its beak, surprised blink",56,"zoomin","squeak@C,splash@R20"),
            B("둥둥 앞으로 출항","출항","50mm, sparkling water caustics, the baby pushing a duck forward across the water, ripples spreading outward naturally, giggling",72,"slideleft","splash@C,giggle@R25"),
            B("오리 함장 임무 완료","함장","50mm, bright light, the baby hugging three rubber ducks to its chest with a proud grin, water droplets on the cheeks",48,"hblur","clap@C,sparkle@R25") }),
          new("dl_hairwash","daily","🚿","머리 감기 대작전","샴푸 모자 쓰고 눈 꼭 감아 🚿","머리 감기 공포 극복기 (페르소나: 용감한 샴푸 전사)",1.05, new[]{
            B("머리 감기 시간이다","머리","35mm, bright bathroom light, a toddler in the tub wearing a soft shampoo visor cap, eyes squeezed shut in anticipation, fists clenched",32,"fade","suspense@C,drumroll@R20"),
            B("거품 뽀글뽀글","뽀글","Macro 85mm, soft diffused bathroom light, the parent's hands lathering shampoo bubbles on the toddler's hair, foam piling up into a funny mohawk",54,"zoomin","pop@C,squeak@R20"),
            B("물이 쏴아 내려간다","쏴아","50mm, bright light, warm water pouring downward over the visor and flowing off the edges, the toddler peeking one eye open bravely",72,"slideleft","splash@C,suspense@R25"),
            B("해냈다 샴푸 전사","해냈다","50mm, bright softbox, the toddler throwing up both arms with wet slicked hair and a huge proud grin",62,"hblur","clap@C,sparkle@R25") }),
          new("dl_towelcape","daily","🦸","수건 망토 탈출","목욕 후 알몸 탈출 슈퍼히어로 🦸","수건 망토 달고 도망 (페르소나: 목욕 탈출 히어로)",1.15, new[]{
            B("목욕 끝 탈출 준비","탈출","35mm, warm cozy bathroom light, a toddler wrapped in a hooded animal towel standing on a bath mat, a mischievous twinkle in the eyes",32,"fade","suspense@C,cute@R20"),
            B("수건 망토 장착","망토","50mm, warm light, the towel slipping back to hang like a cape from the hood, the toddler striking a superhero pose",56,"zoomin","whoosh@C,ding@R20"),
            B("슈퍼히어로 출동","출동","tracking 35mm, cozy warm hallway light, the toddler running forward down the hallway with the towel cape fluttering behind, natural forward running",84,"slideleft","whoosh@C,giggle@R30"),
            B("잡혔다 로션 타임","잡혔다","50mm, warm tungsten glow, the parent scooping up the giggling toddler in the towel, a happy defeated grin",54,"hblur","giggle@C,boing@R25") }),
          new("dl_nails","daily","✂️","손톱 깎기 미션","쉿… 자는 동안 손톱 깎기 작전 ✂️","잠든 아기 손톱 깎기 첩보전 (페르소나: 잠든 VIP)",0.95, new[]{
            B("작전명 손톱 정리","작전","Macro 85mm, soft dim lamp light, a sleeping baby's tiny hand resting on a blanket, a parent holding baby nail clippers carefully nearby",28,"fade","suspense@C,pluck@R20"),
            B("살금살금 첫 번째","첫 번째","Extreme macro, warm amber lamp, the clippers gently trimming a tiny fingernail, the baby's fingers twitching slightly in sleep",44,"zoomin","suspense@C,pop@R20"),
            B("어 눈을 떴다","떴다","Macro 85mm, cozy warm light, the baby's eyes fluttering open mid-mission, the parent freezing in comic panic",64,"slideleft","ding@C,squeak@R25"),
            B("다시 쿨쿨 작전 성공","성공","50mm, soft tungsten glow, the baby yawning and drifting back to sleep as the parent silently celebrates",32,"hblur","twinkle@C,cute@R20") }),
          new("dl_dressing","daily","👕","혼자 옷 입기","팔이 어디로 들어가지? 🤔","셀프 옷 입기 첫 도전 (페르소나: 꼬마 패셔니스타)",1.0, new[]{
            B("오늘은 혼자 입을래","혼자","35mm, bright morning daylight bedroom, a toddler sitting on the floor holding a striped t-shirt up, determined face",28,"fade","drumroll@C,cute@R20"),
            B("머리가 안 나와요","머리","50mm, bright light, the toddler stuck with the shirt over its head, arms waving, only a tuft of hair poking out of the neck hole",62,"zoomin","boing@C,squeak@R25"),
            B("영차 쏙 나왔다","쏙","50mm, sunny morning light, the toddler's head popping out of the neck hole with messy hair and a surprised grin",76,"slideleft","pop@C,giggle@R30"),
            B("거꾸로 입어도 멋져","멋져","35mm, bright daylight, the toddler proudly posing with the shirt on backwards and one arm through the neck hole",56,"hblur","clap@C,sparkle@R25") }),
        }),

        // ═══ 7. 코스튬 / 역할극 ═══
        new("costume","🦖","코스튬·역할극","공룡·사자·산타 변신놀이", new List<BabyThemeDef>{
          new("dino","costume","🦖","아기 공룡","공룡 옷 입은 아기 어흥 🦖","꼬마 공룡의 위엄 넘치는 하루 (페르소나: 꼬마 폭군 공룡)",1.0, new[]{
            B("쥐라기의 폭군이 강림했다","폭군","28mm low-angle dramatic, warm spotlight, a baby in a green dino costume rearing up with a tiny tyrant scowl, felt spikes lit, a plush tail curled",34,"fade","cute@C,drumroll@R25"),
            B("어흥 대륙을 뒤흔든다","어흥","28mm low, warm key light, a baby opening wide for a mighty roar that comes out an adorable squeak, tiny teeth, a dramatic shadow",55,"zoomin","boing@C,pop@R30"),
            B("쿵쿵 지축을 밟으며 걷는다","쿵쿵","28mm low-angle, spotlight, a baby stomping around like a fierce little dinosaur, the costume tail swinging, thunderous tiny steps",70,"slideleft","crash@L30,boing@R30"),
            B("어 꼬리에 걸려 넘어질 뻔","꼬리","28mm low, warm glow, a baby tripping over its own costume tail with a startled flail, wide eyes, a comedic recovery",74,"hblur","squeak@C,boing@R25"),
            B("그래도 내가 최강 공룡이다","최강","28mm low-angle, bright spotlight, a baby posing triumphantly in the dino suit, chest puffed, a victorious toothy grin, proud spikes",86,"slideleft","drumroll@L20,sparkle@C,giggle@R30"),
            B("자 다시 쥐라기가 열린다","다시","28mm low-angle dramatic, warm spotlight, the same baby in the dino costume rearing up with a tiny tyrant scowl, matched framing for a seamless loop",34,"fade","cute@C,drumroll@R25") }),
          new("lion","costume","🦁","아기 사자","갈기 두른 아기 밀림의 왕 🦁","작지만 용맹한 정글의 왕 (페르소나: 정글 애송이 왕)",1.0, new[]{
            B("밀림의 어린 왕이 나가신다","왕","50mm lens, golden savanna warm light, a baby in a fluffy lion-mane hood wearing a regal little frown, the fuzzy mane glowing, a dignified pose",34,"fade","cute@C,drumroll@R25"),
            B("어흥 사자의 포효다","어흥","50mm, warm glow, a baby attempting a fierce roar that emerges as a cute squeak, tongue out, the mane framing a round face",56,"zoomin","boing@C,squeak@R30"),
            B("이 갈기가 멋지지 않나","갈기","50mm lens, golden light, a baby proudly shaking the fluffy mane, fuzz rippling, chin lifted with royal vanity",68,"slideleft","sparkle@L20,pop@R25"),
            B("어 갈기가 눈을 찔러","눈","50mm, warm sun, the mane flopping into the baby's eyes, a flustered blink and paw-swat, comedic fluster",72,"hblur","boing@C,squeak@R25"),
            B("역시 정글의 왕은 나야","왕","50mm lens, bright savanna glow, a baby posing majestically in the lion costume, arms regal, a beaming little-king grin",86,"slideleft","drumroll@L20,giggle@C,sparkle@R30"),
            B("자 다시 왕이 나가신다","다시","50mm lens, golden savanna warm light, the same baby in the lion-mane hood with a regal little frown, matched framing for a seamless loop",34,"fade","cute@C,drumroll@R25") }),
          new("pumpkin","costume","🎃","호박 변신","호박 옷 입고 데굴데굴 🎃","동그란 호박이 된 아기 (페르소나: 굴러가는 호박)",1.15, new[]{
            B("나는 동그란 호박이 되었다","호박","Fisheye lens, warm autumn orange light, a baby in a round pumpkin costume posing cutely, plump orange curves bulging the frame, a leafy hat",34,"fade","cute@C,ding@R25"),
            B("데굴 굴러보면 어떨까","데굴","Fisheye, warm glow, a baby attempting to roll in the round bulky costume, tipping to one side, a curved-world distortion",55,"zoomin","boing@C,whoosh@R30"),
            B("어 뒤집혀서 못 일어나","못 일어나","Fisheye lens, orange light, a baby stuck on its back like an upturned turtle in the pumpkin shell, limbs wiggling helplessly",72,"slideleft","squeak@C,boing@R30"),
            B("도와줘 헤헤 이것도 재밌어","도와줘","Fisheye, warm sun, a baby giggling while stranded on its back, reaching up cutely, the round costume rocking",70,"hblur","giggle@C,pop@R25"),
            B("호박 대성공 다시 앉았다","성공","Fisheye lens, bright orange glow, a baby righting itself and sitting up proudly in the pumpkin suit, a triumphant round grin",84,"slideleft","sparkle@L20,giggle@R30"),
            B("자 다시 데굴 굴러본다","다시","Fisheye lens, warm autumn orange light, the same baby in the round pumpkin costume posing cutely with a leafy hat, matched framing for a seamless loop",34,"fade","cute@C,ding@R25") }),
          new("rabbit","costume","🐰","토끼 점프","토끼 귀 쫑긋 폴짝폴짝 🐰","깡총깡총 아기 토끼 (페르소나: 깡총 점퍼)",1.5, new[]{
            B("나는야 깡총 토끼","토끼","35mm lens, soft spring pastel light, a baby in fluffy bunny ears with a twitching-nose expression, floppy ears backlit, pastel meadow bokeh",34,"fade","cute@C,drumroll@R25"),
            B("귀가 쫑긋 움직인다","쫑긋","35mm, gentle glow, the bunny ears bouncing as the baby bobs its head, a whisker-twitch nose, an adorable spring vibe",52,"zoomin","boing@C,pop@R30"),
            B("폴짝 한 번 뛰어본다","폴짝","35mm lens, pastel light, a baby attempting a little bunny hop, both feet leaving the ground, ears flying up, a joyful launch",72,"slideleft","boing@L30,whoosh@R30"),
            B("어 착지하다 넘어질 뻔","뻔","35mm, soft sun, a baby wobbling on landing with a cute stumble-recovery, ears askew, a surprised grin",74,"hblur","squeak@C,boing@R25"),
            B("깡총깡총 신나서 연속 점프","깡총","35mm lens, bright pastel glow, a baby hopping repeatedly in bunny ears with pure glee, mid-air joy, meadow sparkle",86,"slideleft","boing@C,giggle@L20,sparkle@R30"),
            B("자 다시 폴짝 준비","다시","35mm lens, soft spring pastel light, the same baby in bunny ears with a twitching-nose expression, matched framing for a seamless loop",34,"fade","cute@C,drumroll@R25") }),
          new("santa","costume","🎅","아기 산타","산타 모자 쓴 아기 선물 배달 🎅","꼬마 산타의 선물 대소동 (페르소나: 꼬마 산타 배달부)",1.0, new[]{
            B("메리 크리스마스 배달 왔어요","배달","50mm lens, warm firelight with fairy-light bokeh, a baby in a tiny Santa hat holding a small wrapped gift with cheerful importance, a glowing hat pom",34,"fade","cute@C,ding@R25"),
            B("이 선물을 신중히 배달한다","선물","50mm, cozy glow, a baby clutching the gift with solemn duty, bokeh lights dancing behind, careful little steps implied",52,"zoomin","suspense@C,drumroll@R25"),
            B("어 포장을 뜯어버렸네","뜯네","50mm lens, firelight, a baby curiously tearing the gift wrap open instead of delivering it, paper flying, guilty delight",70,"slideleft","pop@C,boing@R25"),
            B("이건 아무래도 내 거야","내 거","50mm, warm bokeh, a baby hugging the unwrapped gift possessively, a cheeky over-the-shoulder grin, the hat tilting",74,"hblur","giggle@C,squeak@R25"),
            B("산타는 바로 나야 만세","산타","50mm lens, bright festive glow, a baby celebrating in the Santa hat with arms up, fairy lights sparkling, pure holiday joy",86,"slideleft","sparkle@L20,giggle@C,ding@R30"),
            B("자 다음 선물 배달 갑니다","다음","50mm lens, warm firelight with fairy-light bokeh, the same baby in a Santa hat holding a fresh wrapped gift with cheerful importance, matched framing for a seamless loop",34,"fade","cute@C,ding@R25") }),
        }),

        // ═══ 8. 가족 / 관계 ═══
        new("family","👨‍👩‍👧","가족·관계","아빠껌딱지·형아·반려견", new List<BabyThemeDef>{
          new("dad","family","👨","아빠 껌딱지","아빠만 보면 돌변하는 아기 🥹","아빠 바라기 꼬마의 하루 (페르소나: 아빠 바라기)",1.0, new[]{
            B("아빠가 어디 갔지 찾아야 해","아빠","35mm lens, warm homey light, a baby scanning the room with a slightly worried searching face, lower lip pushing out, cozy living-room bokeh",32,"fade","cute@C,suspense@R25"),
            B("저기 아빠다 발견","발견","35mm, warm glow, a baby's face detonating with joy on spotting dad, eyes flashing bright, arms already lifting",55,"zoomin","ding@C,sparkle@R25"),
            B("두 팔 벌리고 전속력 돌진","돌진","35mm lens, homey light, a baby toddling full-speed toward dad with both arms flung wide, joyful abandon, motion blur",74,"slideleft","whoosh@L30,boing@R25"),
            B("딱 안겼다 세상 편안해","안겼다","35mm, warm cozy glow, a baby snuggling deep into a warm hug, a cheek mashed against a shoulder, a blissful sigh",78,"hblur","giggle@C,cute@R25"),
            B("아빠가 세상에서 제일 좋아","제일","35mm lens, bright warm light, a baby laughing with delight while being lifted high overhead, weightless joy, sparkling eyes",88,"slideleft","sparkle@L20,giggle@C,pop@R30"),
            B("자 다시 아빠를 찾는다","다시","35mm lens, warm homey light, the same baby scanning the room with a worried searching face and pushed-out lip, matched framing for a seamless loop",32,"fade","cute@C,suspense@R25") }),
          new("sibling","family","🧒","형아 따라쟁이","형아 하는 거 다 따라하는 아기 👦","완벽 카피 따라쟁이 동생 (페르소나: 카피캣 동생)",1.15, new[]{
            B("형아가 하는 걸 관찰한다","관찰","50mm lens, bright playroom light, a baby intently studying an older sibling out of frame, brow furrowed in concentration, colorful toy bokeh",32,"fade","cute@C,ding@R25"),
            B("나도 똑같이 따라 할래","따라","50mm, clean light, a baby mimicking the sibling's exact pose, tongue out in focus, a slightly clumsy copy",52,"zoomin","boing@L25,pop@R25"),
            B("어렵지만 포기는 없다","포기","50mm lens, bright glow, a baby struggling to reproduce a tricky move, wobbling determinedly, a red-cheeked effort",70,"slideleft","suspense@C,boing@R25"),
            B("어 실패 이상하다","실패","50mm, playroom light, a baby fumbling the copy with a puzzled pout, head tilting, looking to the sibling for the trick",66,"hblur","squeak@L20,boing@R25"),
            B("드디어 성공 형아 봤지","성공","50mm lens, bright light, a baby nailing the copied move and beaming with proud accomplishment, clapping for itself",86,"slideleft","sparkle@C,giggle@L20,clap@R30"),
            B("자 다음 동작도 관찰","다음","50mm lens, bright playroom light, the same baby intently studying the older sibling with a furrowed focused brow, matched framing for a seamless loop",32,"fade","cute@C,ding@R25") }),
          new("grandma","family","👵","할머니 반가움","할머니 오시면 세상 신난 아기 🥰","할머니 사랑 독차지 손주 (페르소나: 할머니 독차지)",1.0, new[]{
            B("초인종 소리 심상치 않다","초인종","40mm lens, warm nostalgic golden light, a baby perking up alertly at a doorbell sound, ears almost twitching, hopeful wide eyes, a cozy home",32,"fade","ding@C,suspense@R25"),
            B("할머니다 세상에 우와","할머니","40mm, golden glow, a baby's whole face bursting with joy at seeing grandma, arms shooting up, radiant recognition",58,"zoomin","sparkle@C,ding@R25"),
            B("품으로 파고들어 독차지","독차지","40mm lens, warm light, a baby burrowing deep into grandma's soft embrace, a cheek pressed to the cardigan, a blissful cling",74,"slideleft","cute@C,giggle@R25"),
            B("어쩐지 용돈 냄새가 나","용돈","40mm, nostalgic glow, a baby flashing a cheeky knowing grin, side-eyeing with playful mischief, a deep dimple",72,"hblur","pop@L20,giggle@R25"),
            B("할머니 정말 사랑해요","사랑","40mm lens, bright warm light, a baby showering grandma with hugs and giggles, tiny hands patting her cheeks, overflowing love",88,"slideleft","kiss@C,sparkle@L20,giggle@R30"),
            B("자 다시 초인종을 기다린다","다시","40mm lens, warm nostalgic golden light, the same baby perking up alertly at the doorbell with hopeful wide eyes, matched framing for a seamless loop",32,"fade","ding@C,suspense@R25") }),
          new("twins","family","👯","쌍둥이 티키타카","쌍둥이 텔레파시 실화 👶👶","말없이 통하는 쌍둥이 케미 (페르소나: 텔레파시 듀오)",1.15, new[]{
            B("우리는 텔레파시 쌍둥이","쌍둥이","35mm lens, soft even light, symmetrical framing of two identical babies side by side with mirrored expressions, matching outfits, uncanny sync",34,"fade","cute@C,ding@R25"),
            B("동시에 같은 곳을 본다","동시","35mm, balanced glow, both babies snapping their heads to look the same way at the exact same instant, mirror-perfect",52,"zoomin","boing@L25,pop@R25"),
            B("텔레파시 교신 시작","교신","35mm lens, soft symmetrical light, the twins exchanging a knowing sideways glance with tiny matching grins, silent understanding",70,"slideleft","ding@C,sparkle@R25"),
            B("어 같은 장난감을 노렸네","노렸","35mm, even light, both babies reaching for the identical toy at once, a gentle playful tug-of-war, a comedic standoff",76,"hblur","boing@L25,squeak@R25"),
            B("결국 같이 까르르 합창","합창","35mm lens, bright even glow, the twins bursting into perfectly synchronized giggles, heads tipping together, a joyful duet",88,"slideleft","giggle@C,sparkle@L20,pop@R30"),
            B("자 다시 텔레파시 개시","다시","35mm lens, soft even light, the same two identical babies side by side with mirrored expressions, symmetrical, matched framing for a seamless loop",34,"fade","cute@C,ding@R25") }),
          new("puppy","family","🐶","반려견 절친","강아지랑 찐친된 아기 🐶🍼","댕댕이와 아기의 우정 (페르소나: 댕댕이 절친)",1.0, new[]{
            B("멍멍이 안녕 우리 친구 할까","멍멍","Macro 85mm, warm floor-level light, a baby meeting a gentle puppy nose to nose, both curious, soft fur and downy cheeks catching low sun",32,"fade","cute@C,ding@R25"),
            B("코가 촉촉하고 간지러워","코","Macro 85mm, warm glow, a puppy sniffing the baby's outstretched hand, a wet nose glistening, a ticklish anticipatory smile",50,"zoomin","suspense@C,pop@R25"),
            B("어 핥였다 깜짝 간지럼","핥였","Macro 85mm, floor light, a baby erupting in a surprised giggle as the puppy licks its cheek, scrunched delighted eyes",72,"slideleft","boing@L25,giggle@R30"),
            B("같이 데굴데굴 놀자","데굴","Macro 85mm, warm light, a baby and puppy tumbling and rolling together on the floor, fur and giggles tangling, pure play",76,"hblur","whoosh@L30,giggle@R25"),
            B("우리는 이제 찐친 만세","찐친","Macro 85mm, bright warm glow, a baby and puppy cuddling cheek to cheek, matching contented eyes, best-friend warmth",88,"slideleft","sparkle@C,giggle@L20,cute@R30"),
            B("자 다시 멍멍이랑 논다","다시","Macro 85mm, warm floor-level light, the same baby meeting the puppy nose to nose with mutual curiosity, matched framing for a seamless loop",32,"fade","cute@C,ding@R25") }),
        }),

        // ═══ 9. 사고뭉치 / 장난 ═══
        new("mischief","😈","사고뭉치·장난","휴지·서랍·리모컨 대참사", new List<BabyThemeDef>{
          new("csi","mischief","🕵️","아기 명탐정","사라진 쿠키의 진실 용의자는 본인 🍪","쿠키 도난 사건 수사반 (페르소나: 쿠키 수사반 형사)",1.0, new[]{
            B("사건 발생 쿠키가 사라졌다","사건","Anamorphic 40mm, bright film-noir side key light, a baby detective with an intensely grave face examining an empty cookie plate, hard cheek shadow, oval bokeh",40,"fade","suspense@C,ding@R25"),
            B("결정적 단서 이 부스러기","단서","Anamorphic macro, dramatic light, a baby's tiny finger pointing at cookie crumbs on the table, forensic focus, a glinting eye",48,"zoomin","ding@C,suspense@R20"),
            B("범인은 이 방 안에 있다","범인","Anamorphic 40mm, noir key light, a baby slowly panning a squinting suspicious glare across the room, tension mounting, jaw set",62,"slideleft","drumroll@L20,suspense@R25"),
            B("잠깐 거울 속 저 용의자는","용의자","Anamorphic 40mm, side light, a baby staring shocked at its own reflection in a mirror, a dawning realization, a comedic twist",70,"hblur","boing@C,ding@R25"),
            B("입가의 증거 자백하시지","자백","Anamorphic macro, bright key, macro on cookie crumbs ringing a guilty grinning mouth, caught red-handed, a smug reveal",88,"slideleft","crash@L20,giggle@C,pop@R30"),
            B("사건 종결 다음 사건은","다음","Anamorphic 40mm, bright film-noir side key light, the same baby detective resuming a grave face scanning the room, matched framing for a seamless loop",40,"fade","suspense@C,ding@R25") }),
          new("tissue","mischief","🧻","휴지 대방출","휴지 한 통 순삭한 아기 🧻","휴지 뽑기 무한 루프의 마법 (페르소나: 휴지 대방출 도둑)",1.5, new[]{
            B("이 하얀 게 계속 나온다","하얀","20mm wide, bright clean light, a baby discovering a tissue box and pulling one sheet out with dawning wonder, crisp white paper, plotting eyes",34,"fade","cute@C,pop@R25"),
            B("신기해 계속 나오네","신기","20mm wide, bright glow, a baby amazed as tissues keep endlessly emerging, a growing pile, delighted disbelief",52,"zoomin","pop@C,ding@R25"),
            B("더 빨리 총력 방출","방출","20mm wide, bright light, a baby rapidly yanking tissues out two-fisted with pure glee, paper flurrying everywhere",74,"slideleft","whoosh@L30,pop@R30"),
            B("온 방이 하얗게 뒤덮였다","하얗","20mm wide, airy light, a baby surrounded by a mountain of pulled tissues, buried to the waist, gleeful chaos",78,"hblur","boing@L25,giggle@R25"),
            B("눈처럼 흩뿌리며 만세","만세","20mm wide, bright glow, a baby tossing handfuls of tissues into the air like snow, laughing in the paper blizzard",88,"slideleft","whoosh@L20,sparkle@C,giggle@R30"),
            B("자 다시 휴지를 뽑는다","다시","20mm wide, bright clean light, the same baby pulling a tissue from the box with plotting wonder, matched framing for a seamless loop",34,"fade","cute@C,pop@R25") }),
          new("drawer","mischief","🗄️","서랍 털이범","서랍만 열면 다 꺼내는 아기 🗄️","수납장 대참사 전문가 (페르소나: 서랍 털이범)",1.15, new[]{
            B("이 서랍이 몹시 수상하다","서랍","35mm lens, warm indoor light, a baby eyeing a low drawer like a safecracker sizing up a vault, mischievous curiosity, cozy shadows",34,"fade","cute@C,suspense@R25"),
            B("영차 잠금을 해제한다","해제","35mm, warm glow, a baby tugging the drawer open with grunting two-handed effort, it sliding free with a jolt",50,"zoomin","boing@L25,pop@R25"),
            B("보물이 가득 대박이다","보물","35mm lens, warm light, a baby's eyes sparkling with greed at the drawer's contents, hands hovering over the loot, a gleeful gasp",68,"slideleft","ding@C,sparkle@R25"),
            B("하나씩 다 꺼내주지","꺼내","35mm, cozy glow, a baby methodically extracting items one by one, focused thievery, a growing floor pile",76,"hblur","pop@L25,boing@R25"),
            B("바닥에 전부 쏟아 만세","쏟아","35mm lens, warm light, a baby surrounded by the entire drawer's scattered contents, arms raised in heist triumph, cackling",88,"slideleft","crash@C,giggle@L20,pop@R30"),
            B("자 다음 서랍을 노린다","다음","35mm lens, warm indoor light, the same baby eyeing a low drawer like a safecracker with mischievous curiosity, matched framing for a seamless loop",34,"fade","cute@C,suspense@R25") }),
          new("makeup","mischief","💄","화장품 아티스트","엄마 화장품으로 예술한 아기 💄","얼굴이 캔버스가 된 예술가 (페르소나: 얼굴 캔버스 아티스트)",1.0, new[]{
            B("예쁜 색깔들을 발견했다","색깔","Macro 100mm, soft vanity ring light, a baby discovering mom's colorful makeup with wide artistic wonder, glossy tubes reflecting, ring-catch eyes",34,"fade","cute@C,ding@R25"),
            B("얼굴 캔버스에 발라본다","캔버스","Macro 100mm, ring glow, a baby dabbing bright lipstick onto its own cheek, a focused artist's frown, a bold red smear",52,"zoomin","suspense@C,pop@R25"),
            B("여기저기 예술혼을 불태워","예술혼","Macro 100mm, soft light, a baby smearing color all over its face with abandon, streaks everywhere, absorbed creation",72,"slideleft","boing@L20,sparkle@R25"),
            B("어 거울 속 내가 깜짝이야","깜짝","Macro 100mm, ring light, a baby catching its own painted face in a mirror and freezing in comic shock, wide eyes",74,"hblur","boing@C,squeak@R25"),
            B("완벽한 작품이 탄생했다","작품","Macro 100mm, bright ring glow, a baby proudly presenting its messy makeup masterpiece face, a triumphant colorful grin",86,"slideleft","sparkle@L20,giggle@C,pop@R30"),
            B("자 다시 붓을 든다","다시","Macro 100mm, soft vanity ring light, the same baby discovering the colorful makeup with wide artistic wonder, matched framing for a seamless loop",34,"fade","cute@C,ding@R25") }),
          new("remote","mischief","📺","리모컨 도둑","리모컨만 보면 홀리는 아기 📺","금지된 리모컨 쟁탈전 (페르소나: 리모컨 대도)",1.15, new[]{
            B("저 금단의 리모컨을 원해","리모컨","50mm lens, cozy living room with flickering TV glow, a baby staring longingly at a forbidden remote on the sofa, coveting eyes, a blue-warm light mix",34,"fade","suspense@C,ding@R25"),
            B("몰래 살금살금 접근한다","살금","50mm, low TV glow, a baby stealthily creeping toward the remote like a master thief, an exaggerated tiptoe crawl, held breath",52,"zoomin","suspense@L20,boing@R20"),
            B("잡았다 완벽한 절도 성공","성공","50mm lens, warm glow, a baby snatching the remote with a triumphant heist grin, clutching the prize to its chest",70,"slideleft","ding@C,pop@R25"),
            B("버튼을 마구 눌러본다","버튼","50mm, TV flicker, a baby curiously mashing buttons, channels strobing across its fascinated face, chaotic delight",74,"hblur","pop@L25,boing@R25"),
            B("결국 입에 넣어 만세","입에","50mm lens, cozy glow, a baby victoriously gnawing on the stolen remote, drool on the buttons, a cheeky conqueror grin",84,"slideleft","boing@C,giggle@L20,squeak@R30"),
            B("자 다시 리모컨을 노린다","다시","50mm lens, cozy living room with flickering TV glow, the same baby staring longingly at the forbidden remote, matched framing for a seamless loop",34,"fade","suspense@C,ding@R25") }),
        }),

        // ═══ 10. 특별한 날 ═══
        new("special","🎂","특별한 날","첫 생일·크리스마스·돌잡이·할로윈·설날·벚꽃·물총", new List<BabyThemeDef>{
          new("cake","special","🎂","첫 생일 케이크","돌잡이 케이크 스매시 실화 🎂","케이크에 얼굴부터 다이빙 (페르소나: 케이크 스매셔)",1.0, new[]{
            B("이 거대한 케이크는 무엇","케이크","35mm lens, warm party bokeh with candlelight, a baby facing a giant first-birthday cake in awe, frosting glistening, a tiny candle flame reflected in wide eyes",34,"fade","cute@C,drumroll@R25"),
            B("손가락으로 살짝 콕 찍어","콕","35mm macro, warm glow, a baby poking the frosting with one curious finger, a soft dent, cream clinging to the fingertip",50,"zoomin","suspense@C,pop@R25"),
            B("달다 이건 더 먹어야 해","달다","35mm lens, candlelit, a baby's eyes lighting up at the sweet taste, both hands now diving toward the cake, greedy delight",72,"slideleft","ding@C,sparkle@R25"),
            B("얼굴부터 케이크에 다이빙","다이빙","35mm lens, party bokeh, a baby face-planting straight into the birthday cake, frosting splattering outward, a gleeful smash",90,"hblur","splash@C,boing@L30,giggle@R25"),
            B("온 얼굴 크림 범벅 만세","범벅","35mm lens, warm glow, a baby covered head to chin in frosting, laughing with total joy, cake chunks everywhere, a candle askew",86,"slideleft","giggle@C,sparkle@L20,pop@R30"),
            B("자 다음 케이크를 노린다","다음","35mm lens, warm party bokeh with candlelight, the same baby facing a fresh giant birthday cake in candlelit awe, matched framing for a seamless loop",34,"fade","cute@C,drumroll@R25") }),
          new("christmas","special","🎄","첫 크리스마스","트리 불빛에 홀린 아기 🎄","반짝이는 트리와 첫 만남 (페르소나: 트리 불빛 홀린 아이)",1.0, new[]{
            B("반짝이는 나무가 나를 홀린다","반짝","50mm lens, warm Christmas tree fairy-light bokeh, a baby gazing up at a glowing tree utterly mesmerized, colored lights sparkling in wide eyes",32,"fade","sparkle@C,cute@R25"),
            B("이 불빛을 만져보고 싶어","불빛","50mm macro, glowing bokeh, a baby reaching a tentative finger toward the twinkling string lights, a warm halo on the cheek",50,"zoomin","ding@C,suspense@R20"),
            B("오너먼트가 정말 예쁘다","오너먼트","50mm lens, festive glow, a baby fascinated by a shiny round ornament, its curved reflection warping the tiny face, a sparkling gaze",68,"slideleft","pop@C,sparkle@R25"),
            B("어 저 아래 선물이 있네","선물","50mm, warm bokeh, a baby spotting wrapped gifts beneath the tree, eyes going huge with excitement, leaning toward them",76,"hblur","ding@L20,boing@R25"),
            B("크리스마스 최고 만세","만세","50mm lens, bright fairy-light glow, a baby laughing with joy surrounded by tree lights, arms up, magic reflected everywhere",88,"slideleft","sparkle@C,giggle@L20,ding@R30"),
            B("자 다음 장식을 바라본다","다음","50mm lens, warm Christmas tree fairy-light bokeh, the same baby gazing up at the glowing tree utterly mesmerized, matched framing for a seamless loop",32,"fade","sparkle@C,cute@R25") }),
          new("doljabi","special","🔮","돌잡이 예언","무엇을 잡을까 운명의 순간 🔮","돌잡이로 미래를 점치는 순간 (페르소나: 운명의 예언자)",1.0, new[]{
            B("운명의 물건들이 놓였다","운명","40mm lens, bright ceremonial light, a baby in hanbok facing a row of doljabi objects with fortune-teller gravity, silky fabric sheen, a focused destiny stare",34,"fade","drumroll@C,suspense@R25"),
            B("무엇을 잡을지 고민한다","고민","40mm, clean light, a baby's hand hovering indecisively over the objects, fingers wavering, the whole room holding its breath",50,"zoomin","suspense@C,ding@R25"),
            B("두구두구 긴장의 순간","두구두구","40mm lens, ceremonial glow, a dramatic pause as everyone watches the baby deliberate, tension thick, a tiny furrowed brow",66,"slideleft","drumroll@C,suspense@R25"),
            B("어 다 잡아버렸네","다","40mm, bright light, a baby scooping up every object at once with both arms, a greedy triumphant grab, comedic overreach",78,"hblur","boing@L25,pop@R25"),
            B("대박 미래가 열렸다 만세","대박","40mm lens, bright ceremonial glow, a baby hoisting the armful of grabbed objects overhead in triumph, a prophetic grin, a confetti feel",88,"slideleft","sparkle@C,giggle@L20,ding@R30"),
            B("자 다시 운명을 고른다","다시","40mm lens, bright ceremonial light, the same hanbok baby facing the doljabi objects with fortune-teller gravity, matched framing for a seamless loop",34,"fade","drumroll@C,suspense@R25") }),
          new("trip","special","✈️","첫 가족여행","캐리어 위 아기 여행 준비 완료 🧳","설레는 첫 여행길 풍경 (페르소나: 꼬마 여행자)",1.0, new[]{
            B("우리 지금 어디로 가는 걸까","어디","Ultra-wide 16mm, bright travel daylight, a baby perched on a suitcase with wanderlust eyes, boarding scenery behind, wind-tousled hair, a big adventurous sky",32,"fade","cute@C,ding@R25"),
            B("창밖 풍경이 신기해","신기","Ultra-wide 16mm, bright window light, a baby pressing its nose to a window as scenery streaks past, a foggy nose-print, a dazzled gaze",50,"zoomin","suspense@C,pop@R25"),
            B("어 우리 구름 위에 있어","구름","Ultra-wide 16mm, luminous sky light, a baby amazed peering out at clouds below, eyes enormous and sparkling, tiny palms on the glass",70,"slideleft","ding@C,sparkle@R25"),
            B("새로운 냄새가 가득해","냄새","Ultra-wide 16mm, bright airy light, a baby sniffing the air curiously at a new place, nose wrinkling, taking it all in",68,"hblur","cute@C,pop@R25"),
            B("여행은 최고야 만세","여행","Ultra-wide 16mm, brilliant scenic light, a baby laughing with joy in a beautiful travel landscape, arms wide to the view, wind and sun",88,"slideleft","sparkle@C,giggle@L20,whoosh@R30"),
            B("자 다음 여행지는 어디","다음","Ultra-wide 16mm, bright travel daylight, the same baby perched on a suitcase with wanderlust eyes and wind-tousled hair, matched framing for a seamless loop",32,"fade","cute@C,ding@R25") }),
          new("hundred","special","💯","100일의 기적","100일 만에 이만큼 자란 아기 💯","백일의 성장 감동 스토리 (페르소나: 100일의 기적)",1.0, new[]{
            B("나 벌써 백일이 되었어요","백일","Macro 85mm, soft dreamy backlight, a milestone close-up of a 100-day-old baby bathed in gentle glow, downy hair haloed, tiny fingers curled, serene",30,"fade","cute@C,sparkle@R25"),
            B("이만큼이나 자랐답니다","자랐","Macro 85mm, warm backlight, a baby proudly showing off a new little skill, wobbly but determined, soft-focus tenderness",48,"zoomin","ding@C,drumroll@R25"),
            B("방긋 웃을 줄도 알아요","방긋","Macro 85mm, dreamy glow, a baby breaking into an adorable practiced smile, a gummy grin, eyes crescenting, a dimple emerging",66,"slideleft","sparkle@C,giggle@R25"),
            B("손가락도 꼭 쥘 수 있죠","꼭","Macro 85mm, soft light, a baby's tiny hand tightly gripping a parent's finger, a tender milestone, warm skin-contact detail",74,"hblur","cute@C,ding@R25"),
            B("사랑 가득한 백일의 기적","기적","Macro 85mm, radiant soft backlight, a baby surrounded by warm family love, glowing with joy, a heartfelt milestone moment",86,"slideleft","sparkle@C,giggle@L20,clap@R30"),
            B("자 다음 백일도 함께해요","다음","Macro 85mm, soft dreamy backlight, the same 100-day-old baby bathed in gentle glow with a haloed downy head, matched framing for a seamless loop",30,"fade","cute@C,sparkle@R25") }),
          // v16.10.0 확장 — 시즌·이벤트
          new("sp_halloween","special","🎃","할로윈 호박마차","호박 마차 탄 꼬마 공주님 🎃","할로윈 코스튬 퍼레이드 (페르소나: 호박 왕국 공주)",1.0, new[]{
            B("오늘 밤 나는 공주님","공주님","35mm, warm amber lantern light, a toddler in a sparkly orange pumpkin princess costume sitting in a pumpkin-shaped wagon decorated with jack-o'-lanterns",30,"fade","twinkle@C,cute@R20"),
            B("호박 마차 출발","출발","tracking 35mm, night with glowing lantern light, the pumpkin wagon rolling forward along a leaf-covered sidewalk as the parent pulls it, the toddler waving",58,"zoomin","whoosh@C,giggle@R20"),
            B("트릭 오어 트릿","트릭","50mm, festive orange porch lights, the toddler holding out a little pumpkin bucket at a doorway, candy dropping in, eyes shining",78,"slideleft","ding@C,sparkle@R25"),
            B("사탕 가득 퍼레이드 끝","사탕","50mm, warm candle glow, the toddler hugging the candy bucket sleepily in the wagon under the moon",44,"hblur","twinkle@C,cute@R25") }),
          new("sp_xmastree","special","🎄","크리스마스 트리 꾸미기","꼬마 손으로 꾸민 우리 집 트리 🎄","오너먼트 걸기 첫 도전 (페르소나: 꼬마 장식가)",1.0, new[]{
            B("트리를 꾸며볼까","트리","50mm, cozy warm fireplace light with twinkling fairy lights, a toddler in a red knit sweater holding a shiny gold ornament in front of a bare Christmas tree",28,"fade","sparkle@C,cute@R20"),
            B("영차 가지에 쏙","가지","Macro 85mm, warm twinkling bokeh, the toddler reaching upward and hooking the ornament onto a low branch, the branch bending slightly under its weight",54,"zoomin","ding@C,suspense@R20"),
            B("아빠 별을 달아요","별","crane up 35mm, festive glow, a parent lifting the toddler upward to place a glowing star on top of the tree, tiny arms stretching",80,"slideleft","whoosh@C,sparkle@R25"),
            B("반짝 불이 켜졌다","불","wide 35mm, twinkling festive lights, the tree lighting up all at once and the toddler clapping with sparkling eyes",70,"hblur","clap@C,twinkle@R25") }),
          new("sp_cherry","special","🌸","봄날 벚꽃 구경","벚꽃비 맞으며 첫 봄 나들이 🌸","분홍 꽃잎 터널 산책 (페르소나: 봄의 요정)",1.0, new[]{
            B("분홍 구름 아래","분홍","35mm, soft pastel morning sunlight, a baby in a stroller beneath a tunnel of blooming cherry blossom trees, petals drifting down, wide eyes",26,"fade","twinkle@C,cute@R20"),
            B("꽃잎이 내려와요","꽃잎","85mm, backlit golden light, pink petals falling slowly downward around the baby, one landing on its nose, a cross-eyed giggle",54,"zoomin","sparkle@C,giggle@R20"),
            B("두 손 가득 꽃잎","가득","Macro 85mm, dappled pastel light, the baby opening chubby hands to catch the floating petals, petals settling naturally on the palms",70,"slideleft","pop@C,twinkle@R25"),
            B("봄의 요정 탄생","요정","tracking 35mm, golden spring light, the baby's stroller rolling forward along the blossom path as petals swirl gently in the breeze, the baby reaching up with a happy smile",40,"hblur","sparkle@C,cute@R25") }),
          new("sp_watergun","special","🔫","한여름 물총놀이","물총 한 방에 꺄르르 🌊","마당 물총 대전 (페르소나: 여름 물총 사령관)",1.15, new[]{
            B("물총 장전 완료","장전","35mm, bright midsummer sunlight, a toddler in a swimsuit and sunglasses holding a small colorful water pistol in a backyard, determined grin",30,"fade","drumroll@C,suspense@R20"),
            B("발사 쏴아","발사","tracking 35mm, sunny daylight, the toddler squeezing the trigger, a stream of water arcing forward and falling naturally onto the laughing parent",74,"zoomin","splash@C,whoosh@R25"),
            B("반격이다 으악","반격","50mm, bright sun flare, the parent spraying back gently, water droplets sparkling around the shrieking giggling toddler",88,"slideleft","splash@L25,giggle@R30"),
            B("여름 사령관 승리","승리","50mm, golden afternoon light, the drenched toddler raising the water gun in victory, a rainbow forming in the spray",56,"hblur","clap@C,sparkle@R25") }),
          new("sp_seollal","special","🧧","설날 첫 세배","한복 입고 새해 복 많이 받으세요 🙇","생애 첫 세배와 세뱃돈 (페르소나: 꼬마 복주머니)",1.0, new[]{
            B("고운 한복 입었어요","한복","50mm, warm cozy tungsten light in a traditional room, a toddler in a colorful hanbok with a bokjumeoni pouch standing before seated grandparents",28,"fade","pluck@C,cute@R20"),
            B("새해 복 많이 받으세요","새해","35mm, warm soft light, the toddler bowing forward clumsily with hands on the floor, bottom up, the grandparents laughing",56,"zoomin","ding@C,giggle@R20"),
            B("세뱃돈 받았다","세뱃돈","Macro 85mm, warm amber light, the grandmother placing a crisp envelope into the toddler's tiny hands, eyes going wide",74,"slideleft","sparkle@C,ding@R25"),
            B("복주머니 가득 행복","행복","50mm, cozy warm glow, the toddler hugging the grandmother as the family claps around them",50,"hblur","clap@C,cute@R25") }),
          new("sp_chuseok","special","🌕","추석 송편 빚기","꼬마 손으로 빚은 반달 송편 🌕","추석 가족 송편 공방 (페르소나: 꼬마 떡 장인)",1.0, new[]{
            B("오늘은 송편 만드는 날","송편","Macro 85mm, cozy warm tungsten kitchen light, a toddler sitting at a low table with family, holding a small ball of pastel rice dough",26,"fade","pluck@C,cute@R20"),
            B("꾹꾹 눌러 소를 넣고","꾹꾹","Extreme macro, warm amber light, the toddler's tiny thumbs pressing a hollow into the dough and adding sweet sesame filling",48,"zoomin","suspense@C,pop@R20"),
            B("어 모양이 이상해","이상해","Macro 85mm, warm light, the toddler holding up a lumpy misshapen songpyeon next to grandma's perfect half-moon one, a puzzled pout",70,"slideleft","boing@C,giggle@R25"),
            B("보름달처럼 환하게","보름달","50mm, night with bright full moon through the window, the family laughing as the toddler proudly eats its own lumpy rice cake",44,"hblur","chomp@C,twinkle@R25") }),
          new("sp_childrensday","special","🎁","어린이날 선물","선물 상자 앞에서 두근두근 🎁","5월 5일 주인공의 하루 (페르소나: 오늘의 주인공)",1.05, new[]{
            B("오늘은 내 날이야","내 날","35mm, bright sunny spring daylight, a toddler in a party hat facing a big wrapped gift box with a ribbon, bouncing on tiptoes",30,"fade","drumroll@C,cute@R20"),
            B("리본을 쭉 당겨라","리본","Macro 85mm, bright light, the toddler pulling the ribbon, wrapping paper tearing open, eyes fixed on the box",58,"zoomin","suspense@C,pop@R20"),
            B("짜잔 장난감 기차다","기차","crane up 35mm, sunny daylight, the camera rising as the toddler lifts a toy train out of the box overhead with a shriek of joy",84,"slideleft","ding@C,sparkle@R30"),
            B("세상에서 제일 행복해","행복","50mm, golden afternoon light, the toddler hugging the parent tightly with the toy train in hand",52,"hblur","clap@C,giggle@R25") }),
          new("sp_easter","special","🥚","부활절 달걀 찾기","풀숲 속 알록달록 달걀 사냥 🥚","봄 정원 보물찾기 (페르소나: 꼬마 보물 사냥꾼)",1.0, new[]{
            B("보물 사냥 시작","보물","35mm, bright morning daylight in a spring garden, a toddler in bunny ears holding a small wicker basket, scanning the grass",28,"fade","drumroll@C,suspense@R20"),
            B("저기 분홍 달걀","분홍","tracking 35mm, dappled sunlight, the toddler toddling forward toward a pastel pink egg hidden under a bush, pointing excitedly",56,"zoomin","ding@C,pop@R20"),
            B("찾았다 내 보물","찾았다","Macro 85mm, bright light, the toddler lifting the painted egg with both hands and gasping, eyes huge",76,"slideleft","sparkle@C,giggle@R25"),
            B("바구니 가득 달걀","가득","50mm, golden spring light, the toddler showing off a basket full of colorful eggs to the camera with a proud grin",50,"hblur","clap@C,cute@R25") }),
          new("sp_parentsday","special","💐","어버이날 카네이션","고사리손으로 달아드린 첫 카네이션 💐","감사의 꽃 선물 (페르소나: 꼬마 효자)",0.95, new[]{
            B("엄마 아빠 선물이에요","선물","50mm, soft warm morning light, a toddler holding a red paper carnation with both hands behind its back, shy smile",26,"fade","cute@C,pluck@R20"),
            B("가슴에 꽃을 달아드려요","꽃","Macro 85mm, warm diffused light, the toddler's small fingers carefully pinning the carnation onto the parent's shirt, tongue poking out",48,"zoomin","suspense@C,ding@R20"),
            B("사랑해요 뽀뽀","사랑해요","85mm, golden backlight, the toddler giving the parent a big kiss on the cheek, the parent's eyes welling up",68,"slideleft","sparkle@C,cute@R25"),
            B("고마워 우리 아기","고마워","50mm, warm soft glow, the family hugging tightly together, the carnation glowing in the sunlight",40,"hblur","twinkle@C,clap@R25") }),
          new("sp_newyear","special","🎉","새해 카운트다운","3, 2, 1… 새해다! 🎉","생애 첫 제야의 종 (페르소나: 꼬마 카운트다운 MC)",1.0, new[]{
            B("곧 새해가 온대요","새해","50mm, night with twinkling festive lights, a toddler in pajamas and a sparkly party hat sitting on the sofa by a window overlooking the city",28,"fade","suspense@C,twinkle@R20"),
            B("삼 이 일","삼 이 일","85mm, festive neon glow from the TV, the toddler counting on its fingers with the family, eyes wide, holding a little noisemaker",60,"zoomin","drumroll@C,ding@R20"),
            B("펑 새해다","펑","wide 35mm, night with fireworks bursting outside the window, the toddler blowing the noisemaker and throwing confetti upward",86,"slideleft","crash@C,sparkle@R30"),
            B("새해 첫 꿈나라","꿈나라","50mm, cozy warm lamp light, the toddler falling asleep in the parent's arms still wearing the party hat",30,"hblur","twinkle@C,cute@R20") }),
          new("sp_snowman","special","⛄","첫 눈사람 만들기","데굴데굴 굴려 만든 꼬마 눈사람 ⛄","겨울 마당 눈사람 공방 (페르소나: 꼬마 눈사람 장인)",1.0, new[]{
            B("하얀 마당에 나왔어","마당","24mm wide, bright overcast winter daylight, a toddler in a puffy snowsuit and mittens standing in a snowy backyard, breath puffing",28,"fade","cute@C,pluck@R20"),
            B("데굴데굴 굴려요","데굴데굴","tracking 35mm, soft overcast light, the toddler pushing a snowball forward across the snow, the ball growing bigger as it rolls",58,"zoomin","whoosh@C,suspense@R20"),
            B("당근 코 콕","코","Macro 85mm, bright winter light, the toddler pressing a carrot nose into the small snowman's face, mittens dusted with snow",74,"slideleft","pop@C,ding@R25"),
            B("눈사람 친구 완성","완성","50mm, golden winter sunset, the toddler hugging the lopsided little snowman, cheeks rosy, snowflakes drifting down",50,"hblur","clap@C,sparkle@R25") }),
          new("sp_valentine","special","🍫","발렌타인 초콜릿","초콜릿 선물보다 먼저 먹어버린 아기 🍫","하트 초콜릿 배달 작전 (페르소나: 꼬마 큐피드)",1.0, new[]{
            B("하트 선물 배달 준비","하트","Macro 85mm, soft pastel studio light, a toddler in a tiny cupid wing costume holding a heart-shaped chocolate box with both hands",28,"fade","cute@C,pluck@R20"),
            B("한 개만 맛볼까","한 개","Extreme macro, warm diffused light, the toddler sneaking a chocolate out of the box and nibbling it, chocolate smudging the lips",56,"zoomin","suspense@C,chomp@R20"),
            B("앗 들켰다","들켰다","50mm, soft light, the toddler freezing with a chocolate-covered grin as the parent walks in, comic guilty eyes",76,"slideleft","squeak@C,boing@R25"),
            B("그래도 사랑해요","사랑해요","50mm, warm golden light, the toddler offering the half-empty box with a chocolate kiss on the parent's cheek",52,"hblur","giggle@C,sparkle@R25") }),
        }),
    };
}
