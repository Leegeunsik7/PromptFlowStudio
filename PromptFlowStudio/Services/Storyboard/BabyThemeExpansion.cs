using System.Collections.Generic;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>
/// [PromptFlowStudio] 아기 테마 백과사전 <b>대규모 확장</b>(v2.3.0) — 순수 BCL Authored Data(외부 NuGet 0).
///   ▸ 기존 10대 카테고리(50테마)에 상상력 확장 7대 신규 카테고리(간식/꿈나라/판타지/우주/명화/직업/동물, <b>52테마</b>)를 더해 총 <b>100+ 소분류</b>.
///   ▸ 각 테마는 compact 4-비트 미니 아크(등장→시도→반전→폭발) — <see cref="VideoFormatFactory"/>가 숏츠/롱폼으로 확장(Beats.Count 가변 허용).
///   ▸ <see cref="BabyThemeDatabase.Categories"/> 가 이 목록을 concat 해 UI 탭/조회에 노출(키 전역 고유).
/// </summary>
internal static class BabyThemeExpansion
{
    private static BabyBeat B(string v, string g, string shot, int t, string x, string sfx) => new(v, g, shot, t, x, sfx);

    /// <summary>신규 7대 카테고리(총 52테마).</summary>
    public static IReadOnlyList<BabyCategory> Categories { get; } = Build();

    private static IReadOnlyList<BabyCategory> Build() => new List<BabyCategory>
    {
        // ═══ 11. 간식 · 디저트 ═══
        new("dessert","🍬","간식·디저트","탕후루·마카롱·솜사탕 달콤 리액션", new List<BabyThemeDef>{
          new("tanghulu","dessert","🍡","탕후루 첫 입","와그작 소리 실화? 탕후루 장인 아기 😋","반짝이는 설탕코팅 첫 도전 (페르소나: 탕후루 소믈리에)",1.15, new[]{
            B("이 반짝이는 구슬은 무엇","반짝","Macro 85mm, warm neon-market glow, a baby eyeing a glossy candied-fruit skewer with sparkling wonder, sugar shell gleaming",32,"fade","pluck@C,ding@R25"),
            B("조심스레 와그작 깨물어","와그작","Extreme macro, warm light, baby teeth cracking the crystal sugar shell, tiny shards glinting, wide focused eyes",55,"zoomin","chomp@C,pop@R30"),
            B("어 안쪽은 새콤달콤 반전","반전","Macro 85mm, bright glow, a baby's face bursting into surprised delight at the tart-sweet fruit inside, juice on the lip",80,"slideleft","sparkle@C,boing@L30"),
            B("멈출 수 없어 폭풍 흡입","흡입","Macro 85mm, punchy sun, a giggling baby happily gnawing the whole skewer, sugar crumbs everywhere, blissful mess",90,"hblur","chomp@C,giggle@R30") }),
          new("macaron","dessert","🌈","마카롱 탑","알록달록 마카롱 탑 무너뜨린 아기 🌈","파스텔 탑 붕괴 사건 (페르소나: 마카롱 건축가)",1.0, new[]{
            B("파스텔 탑이 완벽하다","파스텔","50mm lens, soft pastel patisserie light, a baby admiring a tall stack of rainbow macarons, glossy shells, dreamy bokeh",30,"fade","cute@C,ding@R25"),
            B("맨 위 하나만 살짝","살짝","50mm macro, soft glow, a baby's finger delicately reaching for the top macaron, the tower wobbling precariously",52,"zoomin","suspense@C,pop@R25"),
            B("어 와르르 무너졌다","와르르","50mm lens, bright light, the macaron tower toppling in colorful chaos, shells rolling, a baby's shocked open mouth",78,"slideleft","crash@C,boing@L30,squeak@R25"),
            B("괜찮아 다 내 거지 헤헤","내 거","50mm lens, pastel glow, a giggling baby gathering scattered macarons into its arms, gleeful sugary heist",88,"hblur","giggle@C,sparkle@R30") }),
          new("jelly","dessert","🍮","젤리 퐁퐁","말랑 젤리에 놀란 아기 🍮","탱글탱글 젤리와의 씨름 (페르소나: 젤리 탐험가)",1.15, new[]{
            B("이 말랑한 건 살아있나","말랑","Macro 100mm, bright clean light, a baby poking a jiggly translucent jelly, it wobbling back, curious suspicion",33,"fade","boing@C,cute@R25"),
            B("눌러도 다시 튀어오르네","튀어","Macro 100mm, sparkling light, a baby repeatedly pressing the bouncing jelly, delighted disbelief, giggle building",56,"zoomin","boing@L30,pop@R25"),
            B("어 손에서 미끄덩 도망","미끄덩","Macro 100mm, bright glow, the slippery jelly squirting out of tiny hands, a baby's cross-eyed startle",76,"slideleft","squeak@C,boing@R30"),
            B("결국 한 입 꿀꺽 성공","꿀꺽","Macro 100mm, punchy light, a baby triumphantly gobbling the wobbly jelly, cheeks bulging, joyful squint",89,"hblur","chomp@C,giggle@R30") }),
          new("cottoncandy","dessert","☁️","솜사탕 구름","입에서 사라지는 마법에 홀린 아기 ☁️","달콤한 구름의 마법 (페르소나: 구름 사냥꾼)",1.0, new[]{
            B("분홍 구름을 잡았다","분홍","35mm lens, soft dreamy backlight, a baby holding a huge pink cotton-candy cloud bigger than its head, fluffy glow",31,"fade","pluck@C,sparkle@R25"),
            B("한 입 뜯어 입에 넣어","뜯어","35mm macro, warm glow, a baby pinching off a tuft and placing it on its tongue, wide anticipating eyes",54,"zoomin","suspense@C,pop@R25"),
            B("어 스르륵 사라졌어 마법","사라졌","35mm lens, sparkling light, a baby's astonished face as the candy melts to nothing, a magical vanishing, empty fingers",77,"slideleft","sparkle@C,boing@L30"),
            B("더 더 이 마법 최고야","마법","35mm lens, bright dreamy glow, a giggling baby stuffing fluffy clouds into its mouth, sticky pink cheeks, pure delight",90,"hblur","giggle@C,sparkle@R30") }),
          new("donut","dessert","🍩","도넛 서커스","도넛 구멍에 손가락 낀 아기 🍩","달콤한 링과의 한판 (페르소나: 도넛 곡예사)",1.15, new[]{
            B("가운데 구멍이 신기해","구멍","Macro 85mm, warm bakery light, a baby inspecting a glazed donut's hole with one eye, sprinkles glistening",32,"fade","cute@C,ding@R25"),
            B("손가락을 쏙 넣어본다","쏙","Macro 85mm, soft glow, a baby threading a chubby finger through the donut hole, proud discovery grin",55,"zoomin","pop@C,boing@R25"),
            B("어 안 빠져 낑낑 위기","낑낑","Macro 85mm, bright light, the donut stuck on a wiggling finger, a baby's comedic panic, tugging",78,"slideleft","squeak@C,boing@L30"),
            B("에라 그냥 먹어버리자","먹어","Macro 85mm, punchy glow, a baby solving it by eating the donut off its finger, glaze on the nose, triumphant",89,"hblur","chomp@C,giggle@R30") }),
          new("chocofountain","dessert","🍫","초코 분수","초코 분수에 손 담근 아기 🍫","흐르는 초콜릿의 유혹 (페르소나: 초코 마술사)",1.0, new[]{
            B("갈색 폭포가 흐른다","폭포","50mm lens, warm rich light, a baby mesmerized by a flowing chocolate fountain, glossy cascade, reflective eyes",31,"fade","suspense@C,cute@R25"),
            B("손가락을 살짝 담가","담가","50mm macro, warm glow, a baby dipping a finger into the chocolate stream, thick coating dripping, focused wonder",55,"zoomin","pop@C,ding@R25"),
            B("어 온 손이 초코 범벅","범벅","50mm lens, bright light, a baby staring at both chocolate-drenched hands in shock, drips everywhere, wide eyes",77,"slideleft","boing@C,squeak@R30"),
            B("맛있으니 다 괜찮아 헤헤","괜찮아","50mm lens, warm glow, a giggling baby licking chocolate off messy hands and cheeks, blissful sticky joy",89,"hblur","giggle@C,sparkle@R30") }),
          new("popcorn","dessert","🍿","팝콘 폭죽","펑펑 튀는 팝콘에 신난 아기 🍿","톡톡 터지는 팝콘 쇼 (페르소나: 팝콘 지휘자)",1.5, new[]{
            B("냄비에서 소리가 난다","소리","35mm lens, warm kitchen glow, a baby watching a popcorn pot with anticipation, ears perked, curious focus",33,"fade","suspense@C,cute@R25"),
            B("펑 팝콘이 튀어올라","펑","35mm, bright light, popcorn bursting into the air as a baby's eyes go huge with astonished delight, kernels flying",58,"zoomin","pop@L30,boing@R30"),
            B("우와 눈처럼 쏟아진다","쏟아","35mm lens, sparkling glow, a baby laughing under a shower of popping corn, catching pieces, joyful chaos",80,"slideleft","pop@C,sparkle@R25"),
            B("입 벌리고 받아먹기 성공","받아","35mm lens, punchy light, a baby gleefully catching popcorn in its open mouth, cheeks full, triumphant giggle",90,"hblur","chomp@C,giggle@R30") }),
          new("pudding","dessert","🍮","푸딩 지진","흔들리는 푸딩에 매혹된 아기 🍮","탱탱 푸딩 지진 실험 (페르소나: 푸딩 과학자)",1.0, new[]{
            B("노란 성이 흔들린다","흔들","Macro 100mm, soft caramel light, a baby watching a wobbling custard pudding with scientific fascination, glossy sheen",30,"fade","cute@C,boing@R25"),
            B("톡톡 지진을 일으켜","지진","Macro 100mm, warm glow, a baby tapping the plate to make the pudding quake, mesmerized by the jiggle",54,"zoomin","boing@C,pop@R25"),
            B("어 너무 세게 쳐서 풀썩","풀썩","Macro 100mm, bright light, the pudding collapsing into a caramel puddle, a baby's dramatic gasp of shock",77,"slideleft","crash@C,squeak@R30"),
            B("숟가락 없이 얼굴부터 다이빙","다이빙","Macro 100mm, punchy glow, a baby face-diving into the fallen pudding, caramel smeared ear to ear, blissful giggle",90,"hblur","splash@C,giggle@R30") }),
        }),

        // ═══ 12. 꿈나라 · 판타지 수면 ═══
        new("dreamland","🌙","꿈나라·판타지수면","구름 침대·은하수 자장가 몽환 서사", new List<BabyThemeDef>{
          new("cloudbed","dreamland","☁️","구름 침대 오디세이","구름 위에서 잠든 아기 ☁️","포근한 구름을 항해하는 꿈 (페르소나: 구름 항해사)",1.0, new[]{
            B("포근한 구름에 몸을 누인다","구름","50mm lens, dreamy soft pastel backlight, a baby sinking into a fluffy cloud-like bed, downy hair haloed, serene glow",26,"fade","cute@C,sparkle@R25"),
            B("구름이 두둥실 떠오른다","두둥실","50mm, ethereal glow, a baby gently drifting upward on a floating cloud, tiny smile forming, soft dream haze",40,"zoomin","whoosh@L20,pluck@R25"),
            B("별들 사이를 미끄러진다","미끄러","50mm lens, starry dream light, a baby serenely gliding past twinkling stars on the cloud, wonder-filled sleepy eyes",58,"slideleft","sparkle@C,ding@R25"),
            B("스르르 깊은 꿈에 닿는다","스르르","50mm lens, radiant soft glow, a peacefully sleeping baby cradled in the glowing cloud, blissful dream smile",30,"hblur","cute@C,sparkle@R25") }),
          new("galaxylullaby","dreamland","🌌","자장가 은하수 여행","은하수 자장가에 잠든 아기 🌌","별빛 자장가를 따라가는 여행 (페르소나: 은하수 여행자)",1.0, new[]{
            B("자장가가 은하로 이어진다","은하","35mm lens, deep cosmic blue dream light, a baby floating amid a swirling milky-way, star-sparkle in wide sleepy eyes",28,"fade","pluck@C,sparkle@R25"),
            B("별 하나가 손짓을 한다","손짓","35mm, glowing nebula light, a tiny star drifting close to a baby's outstretched hand, gentle shimmer, dreamy awe",42,"zoomin","ding@C,suspense@R25"),
            B("은하수 강을 헤엄친다","헤엄","35mm lens, luminous galaxy glow, a baby serenely swimming through a river of stars, trailing stardust, blissful",60,"slideleft","sparkle@C,whoosh@R25"),
            B("별빛 이불 덮고 새근새근","새근","35mm lens, soft radiant starlight, a peacefully sleeping baby wrapped in a blanket of stars, tender dream smile",30,"hblur","cute@C,sparkle@R25") }),
          new("pillowfairy","dreamland","🧚","베개 요정과의 만남","베개 요정을 만난 아기 🧚","꿈속 베개 요정과의 밤 (페르소나: 꿈의 손님)",1.0, new[]{
            B("베개에서 반짝임이 새어나온다","반짝","Macro 85mm, magical soft glow, a sleeping baby beside a pillow leaking golden fairy sparkles, dreamy shimmer",27,"fade","sparkle@C,pluck@R25"),
            B("작은 요정이 살포시 앉는다","살포시","Macro 85mm, warm fairy light, a tiny glowing fairy alighting on a baby's cheek, gossamer wings, gentle dream",42,"zoomin","ding@C,cute@R25"),
            B("요정이 좋은 꿈을 뿌린다","꿈","Macro 85mm, glittering glow, the fairy sprinkling dream-dust as the baby's face softens into a serene smile",58,"slideleft","sparkle@C,whoosh@R25"),
            B("행복한 꿈속으로 함께","함께","Macro 85mm, radiant soft light, a peacefully sleeping baby glowing with fairy magic, blissful dream, floating motes",30,"hblur","cute@C,sparkle@R25") }),
          new("starcandy","dreamland","⭐","별사탕 꿈","별사탕이 쏟아지는 꿈 ⭐","달콤한 별사탕 꿈나라 (페르소나: 별사탕 수집가)",1.15, new[]{
            B("하늘에서 별사탕이 내린다","별사탕","35mm lens, twinkling dream light, a baby gazing up as tiny star-shaped candies rain down softly, sparkling wonder",30,"fade","sparkle@C,cute@R25"),
            B("두 손 벌려 받아본다","받아","35mm, glowing glow, a baby cupping its hands to catch falling star-candies, delight blooming, shimmer trails",48,"zoomin","ding@C,pop@R25"),
            B("한 알 입에 넣으니 반짝","반짝","35mm lens, radiant light, a baby tasting a star-candy and glowing with sparkling joy, cheeks twinkling",64,"slideleft","sparkle@C,giggle@R25"),
            B("별사탕 이불 덮고 스르르","스르르","35mm lens, soft dream glow, a content sleepy baby nestled among glowing star-candies, drifting to sleep",30,"hblur","cute@C,sparkle@R25") }),
          new("moonswing","dreamland","🌙","달빛 그네","초승달 그네를 탄 아기 🌙","달에 걸린 그네를 타는 꿈 (페르소나: 달빛 몽상가)",1.0, new[]{
            B("초승달이 그네가 되었다","그네","50mm lens, silvery moonlight dream, a baby sitting on a crescent-moon swing high in a starry sky, gentle glow",28,"fade","pluck@C,sparkle@R25"),
            B("살랑살랑 달빛을 흔든다","살랑","50mm, soft lunar glow, a baby gently swinging on the moon, stars drifting past, a serene dreamy smile",44,"zoomin","whoosh@L20,ding@R25"),
            B("구름 사이로 높이 올라","높이","50mm lens, luminous glow, a baby swinging higher through soft clouds, wonder-wide sleepy eyes, floating hair",60,"slideleft","sparkle@C,whoosh@R25"),
            B("달을 안고 새근새근 잠든다","안고","50mm lens, radiant moonlight, a peacefully sleeping baby cradled on the crescent moon, tender dream glow",30,"hblur","cute@C,sparkle@R25") }),
          new("dreamtrain","dreamland","🚂","꿈 기차","꿈 기차를 탄 아기 🚂","밤하늘을 달리는 꿈 기차 (페르소나: 꿈 기관사)",1.15, new[]{
            B("반짝이는 기차가 도착했다","기차","24mm wide, magical night glow, a baby boarding a glowing dream-train under a starry sky, warm window light",29,"fade","ding@C,pluck@R25"),
            B("칙칙폭폭 별밤을 달린다","칙칙","24mm, luminous motion glow, a dream-train gliding across the night sky, a baby peering out with sleepy wonder",45,"zoomin","whoosh@L25,suspense@R25"),
            B("창밖에 꿈 풍경이 흐른다","풍경","24mm wide, dreamy glow, soft surreal candy-and-star scenery streaming past the train window, a baby's awed gaze",60,"slideleft","sparkle@C,ding@R25"),
            B("포근한 좌석에서 스르르","포근","24mm wide, soft warm glow, a content sleepy baby curling up in the cozy train seat, drifting into dreams",30,"hblur","cute@C,sparkle@R25") }),
        }),

        // ═══ 13. 판타지 · 마법 ═══
        new("fantasy","🪄","판타지·마법","마법사·용 기사·요정 판타지 서사", new List<BabyThemeDef>{
          new("wizard","fantasy","🧙","호그와트 최연소 마법사","마법 주문을 외운 아기 🪄","꼬마 마법사의 첫 주문 (페르소나: 최연소 마법사)",1.0, new[]{
            B("드디어 마법 지팡이를 받았다","지팡이","28mm low-angle, warm candlelit hall glow, a baby in a tiny wizard robe gripping a glowing wand with solemn destiny, floating sparks",34,"fade","pluck@C,drumroll@R25"),
            B("수리수리 첫 주문을 외운다","수리수리","28mm, magical glow, a baby waving the wand chanting, tongue out in focus, faint magic swirl gathering at the tip",55,"zoomin","suspense@C,ding@R25"),
            B("펑 예상 못한 마법 폭발","펑","28mm low-angle, bright burst light, a comedic puff of colorful magic exploding as the baby recoils wide-eyed, sparks raining",82,"slideleft","crash@C,boing@L30,sparkle@R25"),
            B("그래도 내가 최고 마법사지","최고","28mm low-angle, radiant glow, a baby posing triumphantly with the glowing wand, proud grin, magic sparkles orbiting",90,"hblur","sparkle@C,giggle@R30") }),
          new("dragonknight","fantasy","🐉","용을 길들이는 아기 기사","아기 용과 친구된 기사 🐉","꼬마 기사와 아기 용의 우정 (페르소나: 용의 기사)",1.0, new[]{
            B("작은 용이 나를 노려본다","용","28mm low-angle, epic warm light, a baby knight in foil armor facing a cute plush baby dragon, brave little stare, ember glow",34,"fade","suspense@C,drumroll@R25"),
            B("겁내지 마 친구가 되자","친구","28mm, warm glow, a baby cautiously extending a hand to the wary dragon, tension softening, a hopeful gaze",55,"zoomin","ding@C,cute@R25"),
            B("어 용이 재채기 불을 뿜어","재채기","28mm low-angle, bright flare, the dragon sneezing a harmless puff of glitter-fire, a baby knight's startled comic flinch",80,"slideleft","boing@C,pop@L30,squeak@R25"),
            B("이제 우리는 최고의 콤비","콤비","28mm low-angle, radiant glow, a baby knight hugging the giggling dragon, best-friend triumph, sparks of joy",89,"hblur","giggle@C,sparkle@R30") }),
          new("fairyforest","fantasy","🍄","아기 요정의 숲","반딧불 숲의 아기 요정 🧚","빛나는 숲속 요정의 하루 (페르소나: 숲의 요정)",1.0, new[]{
            B("반짝이는 숲이 깨어난다","숲","35mm lens, magical bioluminescent glow, a baby fairy with gossamer wings amid glowing mushrooms and fireflies, dewy wonder",32,"fade","pluck@C,sparkle@R25"),
            B("반딧불을 손에 담아본다","반딧불","35mm macro, warm firefly glow, a baby cupping a glowing firefly in tiny hands, its light pulsing, awed sparkle-eyes",52,"zoomin","ding@C,suspense@R25"),
            B("어 꽃들이 춤추며 반응해","춤","35mm lens, luminous glow, glowing flowers blooming and swaying around the delighted baby fairy, magical response",70,"slideleft","sparkle@C,whoosh@R25"),
            B("숲의 요정으로 훨훨 만세","훨훨","35mm lens, radiant magic glow, a baby fairy laughing amid a swirl of fireflies and petals, wings shimmering",88,"hblur","giggle@C,sparkle@R30") }),
          new("potion","fantasy","⚗️","마법 물약 연금술사","보글보글 물약 만드는 아기 ⚗️","꼬마 연금술사의 실험실 (페르소나: 물약 연금술사)",1.15, new[]{
            B("신비한 물약을 조제한다","물약","Macro 85mm, moody alchemist glow, a baby in a tiny robe stirring a bubbling glowing potion, colored vapor curling, focused frown",33,"fade","suspense@C,ding@R25"),
            B("반짝이 가루를 톡톡 넣어","톡톡","Macro 85mm, sparkling glow, a baby sprinkling glittering powder into the cauldron, the brew fizzing brighter, intent eyes",55,"zoomin","pop@C,sparkle@R25"),
            B("펑 물약이 무지개로 폭발","무지개","Macro 85mm, bright rainbow burst, the potion erupting in harmless rainbow bubbles over a startled giggling baby",80,"slideleft","crash@C,boing@L30,sparkle@R25"),
            B("완벽한 마법 물약 완성","완성","Macro 85mm, radiant glow, a baby proudly holding up the glowing rainbow potion, triumphant alchemist grin",89,"hblur","sparkle@C,giggle@R30") }),
          new("unicorn","fantasy","🦄","유니콘 친구","무지개 유니콘 탄 아기 🦄","무지개 갈기 유니콘과 질주 (페르소나: 유니콘 라이더)",1.15, new[]{
            B("무지개 유니콘이 나타났다","유니콘","35mm lens, dreamy pastel glow, a baby meeting a fluffy rainbow-maned unicorn, sparkle horn glowing, awed delight",33,"fade","pluck@C,ding@R25"),
            B("갈기를 잡고 올라탄다","올라","35mm, magical glow, a baby climbing onto the unicorn's soft back, gripping the rainbow mane, giddy anticipation",55,"zoomin","boing@C,sparkle@R25"),
            B("무지개 길을 따라 질주","질주","35mm lens, luminous rainbow light, a baby riding the galloping unicorn across a rainbow, hair streaming, pure exhilaration",80,"slideleft","whoosh@C,sparkle@R30"),
            B("최고의 친구와 함께 만세","친구","35mm lens, radiant glow, a baby hugging the unicorn's neck with a joyful grin, sparkles swirling, best-friend magic",90,"hblur","giggle@C,sparkle@R30") }),
          new("witchbroom","fantasy","🧹","마녀 빗자루 비행","빗자루 타고 나는 아기 마녀 🧹","꼬마 마녀의 첫 비행 (페르소나: 견습 마녀)",1.15, new[]{
            B("빗자루야 날아오르렴","빗자루","28mm wide, moody magic glow, a baby in a witch hat straddling a floating broom, determined little scowl, swirling mist",34,"fade","suspense@C,drumroll@R25"),
            B("두둥실 살짝 떠오른다","두둥실","28mm, glowing light, the broom lifting a hovering baby witch a foot off the ground, wobbly balance, thrilled eyes",56,"zoomin","whoosh@L25,boing@R25"),
            B("어 빙글빙글 통제 불능","빙글","28mm wide, dynamic glow, the broom spinning the baby in comic loops, hat flying, a delighted-panicked squeal",80,"slideleft","whoosh@C,squeak@R30"),
            B("완벽 착지 견습 마녀 만세","착지","28mm wide, radiant glow, a baby witch landing with arms up in triumph, hat askew, sparkling victory grin",89,"hblur","sparkle@C,giggle@R30") }),
          new("crystalball","fantasy","🔮","수정 구슬 예언가","수정 구슬 들여다보는 아기 🔮","미래를 점치는 꼬마 예언가 (페르소나: 수정구 점술사)",1.0, new[]{
            B("신비한 구슬이 빛난다","구슬","Macro 100mm, mystic purple glow, a baby gazing into a swirling glowing crystal ball, fortune-teller gravity, reflected sparks",30,"fade","suspense@C,ding@R25"),
            B("구슬 속에 무언가 보인다","보인다","Macro 100mm, glowing haze, a baby leaning close as dreamy images swirl inside the ball, wide fascinated eyes",50,"zoomin","suspense@C,pop@R25"),
            B("어 구슬이 내 얼굴을 비춰","비춰","Macro 100mm, bright glow, the crystal ball reflecting the baby's own funny face, a startled cross-eyed double-take",70,"slideleft","boing@C,squeak@R25"),
            B("대박 밝은 미래가 보여 만세","미래","Macro 100mm, radiant glow, a baby hoisting the glowing crystal ball with a prophetic grin, magical sparkles",88,"hblur","sparkle@C,giggle@R30") }),
          new("magiccarpet","fantasy","🧞","마법 양탄자","양탄자 타고 나는 아기 🧞","하늘을 나는 양탄자 모험 (페르소나: 양탄자 모험가)",1.15, new[]{
            B("양탄자가 스르륵 떠오른다","양탄자","24mm wide, warm arabian-night glow, a baby sitting cross-legged on a floating ornate carpet, wondrous wide eyes, starry sky",33,"fade","pluck@C,whoosh@R25"),
            B("도시 위로 높이 날아오른다","날아","24mm, luminous glow, the magic carpet soaring over a glowing dream-city at night, a baby gripping the edge with thrill",56,"zoomin","whoosh@L25,ding@R25"),
            B("어 양탄자가 기우뚱 흔들","기우뚱","24mm wide, dynamic glow, the carpet tilting sharply as a baby slides and grabs on, a comic wide-eyed gasp",80,"slideleft","whoosh@C,squeak@R30"),
            B("별을 향해 훨훨 모험 만세","모험","24mm wide, radiant starlight, a baby flying the carpet toward glowing stars, arms wide with joy, wind and sparkle",89,"hblur","sparkle@C,giggle@R30") }),
        }),

        // ═══ 14. 우주 · SF ═══
        new("space","🚀","우주·SF","아스트로넛·타임머신·로봇 미래 서사", new List<BabyThemeDef>{
          new("astronaut","space","👨‍🚀","달나라 첫 발자국 아스트로넛","달에 첫 발 디딘 아기 🌕","달 표면을 탐험하는 꼬마 우주인 (페르소나: 달 탐험 대장)",1.0, new[]{
            B("드디어 달에 착륙했다","착륙","24mm wide, stark lunar light with earthrise, a baby in a puffy astronaut suit stepping onto the moon, low-gravity wonder, star field",34,"fade","suspense@C,drumroll@R25"),
            B("통 통 낮은 중력이 신기해","통통","24mm, cosmic glow, a baby bouncing in slow floaty low-gravity hops across moon dust, arms out, delighted amazement",55,"zoomin","boing@L30,whoosh@R25"),
            B("어 헬멧에 달먼지가 묻었다","달먼지","24mm wide, bright lunar flare, a baby examining moon dust on its glove cross-eyed, a comic curious tumble",78,"slideleft","pop@C,boing@R25"),
            B("지구를 향해 손 흔들며 만세","만세","24mm wide, radiant earthrise glow, a baby planting a tiny flag and waving at earth in triumph, weightless joy",90,"hblur","sparkle@C,giggle@R30") }),
          new("timemachine","space","⏰","타임머신 탄 아기","시간을 넘나드는 아기 ⏳","과거와 미래를 오가는 시간 여행 (페르소나: 시간 여행자)",1.15, new[]{
            B("반짝이는 타임머신에 탄다","타임머신","35mm lens, glowing sci-fi light, a baby climbing into a retro glowing time-machine pod, blinking dials, curious thrill",33,"fade","suspense@C,ding@R25"),
            B("버튼을 꾹 눌러 출발","출발","35mm, electric glow, a baby mashing a big glowing button, energy swirling around the pod, wide anticipating eyes",55,"zoomin","pop@C,whoosh@R30"),
            B("어 공룡 시대에 도착했네","공룡","35mm lens, prehistoric warm glow, the pod materializing in a jungle as a friendly cartoon dinosaur peeks in, a comic gasp",80,"slideleft","boing@C,crash@L30,squeak@R25"),
            B("미래로 다시 슝 대모험 만세","대모험","35mm lens, radiant neon glow, a baby cheering as the time-machine zooms into a bright future city, sparkles streaking",89,"hblur","whoosh@C,giggle@R30") }),
          new("robotics","space","🤖","미래 도시 아기 로봇 공학자","로봇을 조립하는 아기 🤖","꼬마 로봇 공학자의 발명 (페르소나: 로봇 공학자)",1.15, new[]{
            B("나만의 로봇을 발명한다","발명","35mm lens, cool neon lab glow, a baby engineer surrounded by glowing robot parts, focused inventor frown, holographic sparks",33,"fade","pluck@C,ding@R25"),
            B("부품을 척척 조립한다","조립","35mm, electric glow, a baby snapping glowing robot parts together with proud focus, tongue out, blinking lights",55,"zoomin","pop@C,boing@R25"),
            B("어 로봇이 갑자기 춤을 춰","춤","35mm lens, bright neon flare, the little robot booting up and dancing wildly as a baby recoils in delighted surprise",80,"slideleft","boing@C,pop@L30,sparkle@R25"),
            B("최고의 발명품 완성 만세","발명품","35mm lens, radiant glow, a baby hugging its dancing robot with a triumphant grin, neon confetti, future-city skyline",89,"hblur","giggle@C,sparkle@R30") }),
          new("alienfriend","space","👽","외계인 친구","외계인과 인사한 아기 👽","우주에서 온 새 친구 (페르소나: 우주 외교관)",1.0, new[]{
            B("우주선에서 초록 친구가 내려온다","친구","28mm wide, eerie glowing green light, a baby facing a cute round alien descending from a saucer, awed cautious stare",33,"fade","suspense@C,ding@R25"),
            B("안녕 우리 친구 할까","안녕","28mm, soft glow, a baby offering a toy in friendship to the wide-eyed alien, gentle hopeful smile, tension easing",54,"zoomin","cute@C,pop@R25"),
            B("어 외계어로 삑삑 대답해","삑삑","28mm wide, bright flare, the alien beeping funny alien sounds as a baby giggles and tries to imitate, comic delight",76,"slideleft","boing@C,squeak@R30"),
            B("우주 최고의 친구 탄생 만세","탄생","28mm wide, radiant glow, a baby and alien high-fiving as the saucer glows warmly, sparkling intergalactic friendship",89,"hblur","giggle@C,sparkle@R30") }),
          new("spacepirate","space","🏴‍☠️","우주 해적 선장","별을 훔치는 아기 해적 🏴‍☠️","은하를 누비는 꼬마 해적 (페르소나: 우주 해적 선장)",1.15, new[]{
            B("우주 해적선의 선장이다","선장","28mm low-angle, dramatic starship glow, a baby in an eyepatch commanding a toy spaceship helm, fierce captain scowl, star field",34,"fade","suspense@C,drumroll@R25"),
            B("반짝이는 별 보물을 노린다","보물","28mm, glowing light, a baby steering toward a glittering star-treasure, greedy gleaming eyes, engines humming",55,"zoomin","ding@C,whoosh@R25"),
            B("어 운석 소나기 위기다","운석","28mm low-angle, bright chaos flare, the ship dodging tumbling meteors as a baby grips the wheel with a wide-eyed yelp",80,"slideleft","crash@C,boing@L30,squeak@R25"),
            B("별 보물 획득 해적 만세","획득","28mm low-angle, radiant glow, a baby captain hoisting a glowing star-treasure overhead in victory, gleeful pirate grin",90,"hblur","sparkle@C,giggle@R30") }),
          new("constellation","space","✨","별자리 항해사","별자리를 잇는 아기 ✨","밤하늘 별자리를 그리는 항해 (페르소나: 별자리 항해사)",1.0, new[]{
            B("밤하늘 별들이 나를 부른다","별들","35mm lens, deep starry glow, a baby navigator gazing up at a sky full of stars, awe-wide eyes, cosmic reflection",30,"fade","pluck@C,sparkle@R25"),
            B("손끝으로 별을 잇는다","잇는다","35mm, glowing light, a baby tracing a finger between stars, glowing constellation lines forming, focused wonder",50,"zoomin","ding@C,suspense@R25"),
            B("우와 곰 별자리가 완성됐다","완성","35mm lens, luminous glow, a full glowing bear constellation lighting up as a baby gasps with sparkling delight",70,"slideleft","sparkle@C,pop@R25"),
            B("온 하늘에 별그림 만세","별그림","35mm lens, radiant starlight, a baby laughing under a sky filled with self-drawn glowing constellations, arms wide",88,"hblur","giggle@C,sparkle@R30") }),
          new("marsrover","space","🔴","화성 탐사 대원","화성을 탐사하는 아기 🔴","붉은 행성의 꼬마 탐사 대원 (페르소나: 화성 탐사 대원)",1.15, new[]{
            B("붉은 행성에 도착했다","화성","24mm wide, rusty red-planet light, a baby explorer in a suit surveying a red martian desert, dusty wind, adventurous awe",33,"fade","suspense@C,drumroll@R25"),
            B("작은 탐사 로버를 몬다","로버","24mm, warm dusty glow, a baby driving a tiny toy rover across red sand, focused mission face, wheels kicking dust",55,"zoomin","whoosh@L25,boing@R25"),
            B("어 신기한 화성 돌을 발견","돌","24mm wide, bright flare, a baby picking up a glowing martian rock in astonishment, cross-eyed inspection, comic wonder",78,"slideleft","ding@C,pop@R25"),
            B("화성 정복 탐사 완료 만세","정복","24mm wide, radiant red glow, a baby planting a flag on mars and cheering with arms up, dust sparkling in the sun",90,"hblur","sparkle@C,giggle@R30") }),
          new("ufo","space","🛸","UFO 조종사","UFO를 조종하는 아기 🛸","반짝이는 UFO를 모는 조종사 (페르소나: UFO 조종사)",1.15, new[]{
            B("빛나는 UFO에 탑승한다","UFO","28mm wide, glowing neon saucer light, a baby settling into a glowing UFO cockpit full of blinking buttons, thrilled focus",33,"fade","suspense@C,ding@R25"),
            B("두둥실 살포시 떠오른다","두둥실","28mm, electric glow, the saucer lifting smoothly as a baby's eyes widen with weightless delight, humming lights",55,"zoomin","whoosh@L25,boing@R25"),
            B("어 빙글빙글 회전 폭주","회전","28mm wide, dynamic flare, the UFO spinning wildly through the sky as a baby grips the controls with a comic squeal",80,"slideleft","whoosh@C,squeak@R30"),
            B("완벽 착륙 조종사 만세","착륙","28mm wide, radiant glow, a baby landing the glowing UFO gently and pumping a fist, sparkling triumphant grin",89,"hblur","sparkle@C,giggle@R30") }),
        }),

        // ═══ 15. 명화 · 예술 패러디 ═══
        new("art","🎨","명화·예술","별밤·모나리자·천지창조 명화 패러디", new List<BabyThemeDef>{
          new("vangogh","art","🌌","별이 빛나는 밤 속 아기","별밤 명화 속 아기 🌌","반 고흐 그림 속으로 (페르소나: 별밤의 몽상가)",1.0, new[]{
            B("소용돌이 별밤 속에 서다","별밤","35mm lens, swirling van-gogh painterly glow, a baby standing inside a living Starry Night, thick swirling brushstroke sky, awed gaze",30,"fade","pluck@C,sparkle@R25"),
            B("붓질이 살아 움직인다","붓질","35mm, painterly motion glow, swirling impasto stars flowing around a mesmerized baby, textured painted air, wide eyes",48,"zoomin","whoosh@L20,ding@R25"),
            B("별 하나를 손으로 만져","만져","35mm lens, glowing paint light, a baby touching a swirling painted star that ripples like wet paint, sparkling wonder",64,"slideleft","sparkle@C,pop@R25"),
            B("명화 속 주인공 만세","주인공","35mm lens, radiant painterly glow, a baby laughing amid the swirling Starry Night, arms wide, paint-stars dancing",86,"hblur","giggle@C,sparkle@R30") }),
          new("monalisa","art","🖼️","모나리자의 아기 미소","모나리자 패러디 아기 🖼️","신비한 미소의 아기 (페르소나: 미소의 명화)",1.0, new[]{
            B("고전 초상화처럼 앉는다","초상","85mm lens, soft renaissance chiaroscuro, a baby posed like the Mona Lisa in a gilded frame, folded hands, mysterious calm, oil-paint glow",30,"fade","cute@C,ding@R25"),
            B("신비로운 미소를 지어본다","미소","85mm macro, warm painterly light, a baby attempting an enigmatic half-smile, one brow subtly lifting, painterly skin glow",48,"zoomin","suspense@C,pop@R25"),
            B("어 참다못해 씨익 웃음","씨익","85mm lens, soft glow, the mysterious pose cracking into a helpless gummy grin, dignity lost, painterly charm",66,"slideleft","boing@C,giggle@R25"),
            B("세계 최고의 명화 미소 만세","명화","85mm lens, radiant renaissance glow, a baby beaming a joyful grin in the gilded frame, timeless masterpiece charm",86,"hblur","sparkle@C,giggle@R30") }),
          new("creation","art","🙌","천지창조 아기 버전","손가락 맞닿는 천지창조 🙌","미켈란젤로 패러디 (페르소나: 천지창조의 손)",1.0, new[]{
            B("두 손가락이 서서히 다가온다","다가","50mm lens, epic sistine-ceiling glow, two hands reaching toward each other fresco-style, a baby finger and a parent finger, divine warm light",28,"fade","suspense@C,drumroll@R25"),
            B("조금만 더 닿을 듯 말 듯","말 듯","50mm macro, dramatic glow, the baby fingertip inching closer with breathless anticipation, painterly clouds behind",50,"zoomin","suspense@C,ding@R25"),
            B("톡 마침내 맞닿는 순간","톡","50mm lens, radiant burst, the fingertips touching with a spark of light, a baby's face lighting up in wonder, fresco glow",76,"slideleft","ding@C,sparkle@L30,pop@R25"),
            B("천지가 창조되었다 만세","창조","50mm lens, glorious golden glow, a baby giggling triumphantly as light radiates from the touch, masterpiece grandeur",88,"hblur","sparkle@C,giggle@R30") }),
          new("scream","art","😱","절규하는 아기","뭉크 절규 패러디 아기 😱","물결치는 하늘 아래 절규 (페르소나: 절규의 주인공)",1.15, new[]{
            B("물결치는 하늘이 이상해","물결","35mm lens, swirling munch painterly glow, a baby on a bridge under a wavy orange-red sky, uneasy wide eyes, expressionist texture",32,"fade","suspense@C,ding@R25"),
            B("양손을 볼에 갖다 댄다","볼에","35mm macro, painterly light, a baby placing both hands on its cheeks in the iconic pose, an O-shaped mouth forming, comic gravity",52,"zoomin","boing@C,squeak@R25"),
            B("으아 세상 억울한 절규","으아","35mm lens, dramatic swirling glow, a baby unleashing a tiny theatrical scream-face, the painted sky rippling in response",78,"slideleft","squeak@C,boing@L30,pop@R25"),
            B("사실 배고팠던 거야 헤헤","배고팠","35mm lens, warm painterly glow, the scream melting into a sheepish giggle as the baby reaches for a snack, comic relief",84,"hblur","giggle@C,sparkle@R30") }),
          new("pearl","art","💎","진주 귀걸이 아기","진주 귀걸이 소녀 패러디 💎","베르메르 명화 속 아기 (페르소나: 진주의 명화)",1.0, new[]{
            B("어둠 속 한 줄기 빛을 받는다","빛","85mm lens, vermeer soft window chiaroscuro, a baby posed like Girl with a Pearl Earring, turban and pearl, luminous dark backdrop",29,"fade","cute@C,ding@R25"),
            B("살포시 뒤돌아 바라본다","뒤돌아","85mm macro, soft glow, a baby turning to gaze over its shoulder with painterly innocence, a glinting pearl, moist lips sheen",48,"zoomin","suspense@C,pop@R25"),
            B("어 진주를 잡아당겨 봐","잡아","85mm lens, warm glow, a baby curiously tugging the giant pearl earring, cross-eyed focus, breaking the elegant pose",66,"slideleft","boing@C,squeak@R25"),
            B("명화의 아기 모델 만세","모델","85mm lens, radiant vermeer glow, a baby flashing a proud grin in the classic pose, pearl swinging, masterpiece charm",86,"hblur","sparkle@C,giggle@R30") }),
          new("klimt","art","💛","황금빛 키스 아기","클림트 키스 패러디 아기 💛","황금 문양 속 포옹 (페르소나: 황금빛 명화)",1.0, new[]{
            B("황금빛 문양에 둘러싸인다","황금","50mm lens, klimt golden mosaic glow, a baby wrapped in a shimmering gold-patterned blanket like The Kiss, ornate radiant texture",29,"fade","pluck@C,sparkle@R25"),
            B("포근한 포옹을 받는다","포옹","50mm macro, warm gold light, a baby nestling into a tender gilded embrace, cheeks glowing, decorative gold swirls",48,"zoomin","cute@C,ding@R25"),
            B("어 반짝이가 코에 붙었다","반짝이","50mm lens, glittering glow, a fleck of gold glitter on the baby's nose triggering a cross-eyed giggle, ornate sparkle",64,"slideleft","boing@C,pop@R25"),
            B("황금빛 사랑의 명화 만세","사랑","50mm lens, radiant gold glow, a baby beaming amid swirling gold patterns, a masterpiece of warmth, glittering joy",86,"hblur","sparkle@C,giggle@R30") }),
          new("sunflower","art","🌻","해바라기 아기","고흐 해바라기 속 아기 🌻","노란 해바라기 정원 (페르소나: 해바라기 화가)",1.15, new[]{
            B("노란 해바라기 밭에 서다","해바라기","35mm lens, van-gogh warm yellow impasto glow, a baby amid towering painterly sunflowers, thick golden brushstrokes, sunny awe",31,"fade","cute@C,ding@R25"),
            B("커다란 꽃송이를 올려다봐","올려","35mm, painterly glow, a baby gazing up at a giant sunflower nodding down toward it, textured petals, wide wonder",50,"zoomin","suspense@C,pop@R25"),
            B("어 꽃가루에 에취 재채기","에취","35mm lens, bright golden flare, a baby sneezing adorably in a puff of pollen, sunflowers swaying, comic pause",68,"slideleft","boing@C,squeak@R25"),
            B("해바라기 화가 만세","화가","35mm lens, radiant yellow glow, a baby laughing surrounded by glowing painterly sunflowers, arms wide, masterpiece joy",86,"hblur","giggle@C,sparkle@R30") }),
        }),

        // ═══ 16. 직업 · 역할극 ═══
        new("jobs","👔","직업·역할극","셰프·디자이너·카레이서 직업 체험", new List<BabyThemeDef>{
          new("chef","jobs","👨‍🍳","아기 셰프의 쿠킹 클래스","요리하는 아기 셰프 👨‍🍳","꼬마 셰프의 좌충우돌 쿠킹 (페르소나: 미슐랭 아기 셰프)",1.15, new[]{
            B("오늘의 특선 요리를 시작한다","특선","35mm lens, warm kitchen spotlight, a baby in a chef hat surveying ingredients with culinary authority, gleaming utensils, focused frown",33,"fade","pluck@C,ding@R25"),
            B("휘휘 반죽을 섞는다","휘휘","35mm, bright glow, a baby vigorously whisking a bowl, batter splattering, flour on the nose, absorbed concentration",55,"zoomin","boing@C,pop@R25"),
            B("어 밀가루가 펑 폭발했다","펑","35mm lens, flour-cloud burst, a puff of flour exploding over a startled baby chef, white-dusted face, comic freeze",80,"slideleft","crash@C,boing@L30,squeak@R25"),
            B("완성 미슐랭 셰프 만세","완성","35mm lens, radiant glow, a baby proudly presenting a messy but adorable dish, flour-covered triumphant grin",89,"hblur","sparkle@C,giggle@R30") }),
          new("designer","jobs","👗","꼬마 패션쇼 디자이너","런웨이 워킹하는 아기 👗","꼬마 디자이너의 패션쇼 (페르소나: 런웨이 디자이너)",1.15, new[]{
            B("나만의 컬렉션을 선보인다","컬렉션","50mm lens, glossy runway spotlight, a baby designer in stylish outfit striking a pose at a mini runway, flashing lights, chic confidence",33,"fade","drumroll@C,ding@R25"),
            B("도도하게 런웨이를 걷는다","런웨이","50mm, dramatic light, a baby strutting the runway with exaggerated fashion attitude, tiny hand on hip, camera flashes popping",55,"zoomin","pop@L25,boing@R25"),
            B("어 옷자락 밟고 휘청","휘청","50mm lens, bright glow, a baby tripping on its long stylish scarf with a dramatic wobble, wide-eyed comic recovery",78,"slideleft","squeak@C,boing@L30"),
            B("완벽한 피날레 포즈 만세","피날레","50mm lens, radiant spotlight, a baby striking a fierce finale pose amid camera flashes, confident designer grin, confetti",89,"hblur","sparkle@C,giggle@R30") }),
          new("racer","jobs","🏎️","꼬마 카레이서","붕붕 질주하는 아기 레이서 🏎️","꼬마 카레이서의 패스트 드라이빙 (페르소나: F1 아기 레이서)",1.5, new[]{
            B("출발선에 선다 엔진 부릉","출발","28mm low-angle, dramatic pit light, a baby in a racing helmet gripping a toy car wheel, fierce game face, revving anticipation",34,"fade","suspense@C,drumroll@R25"),
            B("초록불 붕 전속력 출발","붕","28mm, motion-blur speed light, a baby zooming forward in a push-car, hair whipping back, gleeful determination, streaking bokeh",58,"zoomin","whoosh@L35,boing@R25"),
            B("어 급커브 드리프트 위기","드리프트","28mm low-angle, dynamic flare, the push-car skidding sideways around a curve as a baby leans hard with a thrilled squeal",80,"slideleft","whoosh@C,squeak@R30"),
            B("체커기 통과 우승 만세","우승","28mm low-angle, radiant glow, a baby crossing the finish line with arms up in victory, checkered flags, champion grin",90,"hblur","sparkle@C,giggle@R30") }),
          new("firefighter","jobs","🚒","꼬마 소방관","불끄러 출동한 아기 소방관 🚒","용감한 꼬마 소방관 출동 (페르소나: 용감한 소방관)",1.15, new[]{
            B("출동 벨이 울린다 긴급","출동","28mm low-angle, red-alert glow, a baby in a firefighter helmet snapping to attention at an alarm, brave determined face, siren light",34,"fade","suspense@C,ding@R25"),
            B("호스를 잡고 물을 뿜는다","호스","28mm, dynamic glow, a baby aiming a toy hose, a burst of water spraying forward, focused heroic effort, droplets flying",56,"zoomin","splash@C,whoosh@R25"),
            B("어 물이 나한테 튀었다","튀었","28mm low-angle, bright flare, the hose backfiring a splash onto the baby firefighter, a soaked comic double-take",78,"slideleft","splash@C,squeak@R30"),
            B("불 껐다 영웅 소방관 만세","영웅","28mm low-angle, radiant glow, a baby striking a heroic pose after saving the day, helmet gleaming, proud triumphant grin",90,"hblur","sparkle@C,giggle@R30") }),
          new("doctor","jobs","🩺","꼬마 의사 선생님","청진기 든 아기 의사 🩺","인형을 진료하는 꼬마 의사 (페르소나: 꼬마 명의)",1.0, new[]{
            B("환자를 진료할 시간이다","진료","50mm lens, clean clinic light, a baby in a doctor coat with a stethoscope examining a plush patient, serious professional frown",32,"fade","cute@C,ding@R25"),
            B("청진기를 인형에 대본다","청진기","50mm macro, soft glow, a baby pressing the stethoscope to a teddy's chest, listening with intense focus, tongue out",52,"zoomin","suspense@C,pop@R25"),
            B("어 청진기를 자기 입에 넣어","입에","50mm lens, bright light, a baby absent-mindedly putting the stethoscope in its own mouth, cross-eyed comic confusion",70,"slideleft","boing@C,squeak@R25"),
            B("진료 완료 명의 만세","명의","50mm lens, radiant glow, a baby proudly bandaging the teddy and beaming, a tiny toy syringe raised, triumphant doctor grin",86,"hblur","sparkle@C,giggle@R30") }),
          new("conductor","jobs","🎼","꼬마 지휘자 마에스트로","오케스트라 지휘하는 아기 🎼","열정의 꼬마 마에스트로 (페르소나: 오케스트라 지휘자)",1.15, new[]{
            B("지휘봉을 들고 무대에 선다","지휘봉","50mm lens, dramatic concert-hall glow, a baby maestro raising a tiny baton before an unseen orchestra, solemn artistic gravity, spotlight",33,"fade","drumroll@C,ding@R25"),
            B("팔을 크게 휘저어 지휘한다","휘저어","50mm, warm stage light, a baby waving the baton with grand passionate sweeps, hair bouncing, absorbed in the music",56,"zoomin","ding@L25,sparkle@R25"),
            B("어 너무 신나서 지휘봉 날아가","날아","50mm lens, bright glow, the baton flying out of a baby's over-enthusiastic hand, wide-eyed comic follow, a gasp",78,"slideleft","whoosh@C,boing@L30,squeak@R25"),
            B("대성황 마에스트로 만세","마에스트로","50mm lens, radiant glow, a baby taking a proud bow to applause, arms flung wide, triumphant maestro grin, confetti",89,"hblur","clap@C,giggle@R30") }),
          new("pilot","jobs","✈️","꼬마 파일럿 기장","비행기 조종하는 아기 ✈️","하늘을 나는 꼬마 기장 (페르소나: 베테랑 기장)",1.15, new[]{
            B("이륙 준비 완료 기장이다","이륙","28mm wide, cockpit glow, a baby in a pilot cap gripping a toy yoke, blinking dashboard lights, confident captain face, sky ahead",33,"fade","suspense@C,ding@R25"),
            B("슝 하늘로 날아오른다","슝","28mm, bright sky glow, a baby steering a toy plane up into fluffy clouds, wind in the hair, exhilarated grin, streaking light",56,"zoomin","whoosh@L25,boing@R25"),
            B("어 난기류 덜컹 흔들","덜컹","28mm wide, dynamic flare, the plane bouncing through turbulence as a baby grips the controls with a thrilled wobble-squeal",78,"slideleft","boing@C,squeak@R30"),
            B("완벽 착륙 기장 만세","착륙","28mm wide, radiant glow, a baby landing the plane smoothly and saluting proudly, sunny runway, triumphant captain grin",89,"hblur","sparkle@C,giggle@R30") }),
          new("scientist","jobs","🔬","꼬마 과학자","실험하는 아기 과학자 🔬","호기심 폭발 꼬마 과학자 (페르소나: 괴짜 과학자)",1.15, new[]{
            B("위대한 실험을 시작한다","실험","Macro 85mm, cool lab glow, a baby in goggles and a lab coat peering at bubbling beakers with mad-scientist glee, glowing liquids",33,"fade","suspense@C,ding@R25"),
            B("두 액체를 조심조심 섞어","섞어","Macro 85mm, glowing light, a baby carefully pouring one glowing liquid into another, tongue out, intense curious focus",55,"zoomin","pop@C,suspense@R25"),
            B("펑 무지개 거품 대폭발","폭발","Macro 85mm, bright rainbow burst, a fizzy rainbow foam eruption billowing over a wide-eyed delighted baby scientist",80,"slideleft","crash@C,boing@L30,sparkle@R25"),
            B("대발견 과학자 만세","대발견","Macro 85mm, radiant glow, a goggled baby cheering amid rainbow bubbles with a eureka grin, foam on the nose, triumphant",89,"hblur","sparkle@C,giggle@R30") }),
        }),

        // ═══ 17. 자연 · 동물 교감 ═══
        new("animals","🦁","자연·동물교감","아기 사자·다람쥐·인어 동물 판타지", new List<BabyThemeDef>{
          new("lioncub","animals","🦁","아기 사자와의 비밀 친구","아기 사자와 교감한 아기 🦁","초원의 아기 사자 절친 (페르소나: 초원의 친구)",1.0, new[]{
            B("초원에서 아기 사자를 만난다","사자","50mm lens, golden savanna glow, a baby meeting a fluffy lion cub nose to nose in tall grass, mutual curious wonder, warm dust light",32,"fade","cute@C,ding@R25"),
            B("살며시 갈기를 쓰다듬어","쓰다듬","50mm macro, warm light, a baby gently patting the cub's soft mane, the cub leaning in, a tender trusting moment",52,"zoomin","suspense@C,pop@R25"),
            B("어 사자가 앙 장난을 쳐","앙","50mm lens, bright glow, the cub playfully nibbling the baby's sleeve as it giggles and tumbles, playful chaos, savanna bokeh",72,"slideleft","boing@C,giggle@R30"),
            B("초원 최고의 절친 만세","절친","50mm lens, radiant golden glow, a baby and lion cub cuddling cheek to cheek in the grass, best-friend warmth, sunny sparkle",88,"hblur","sparkle@C,giggle@R30") }),
          new("squirrel","animals","🐿️","숲속 다람쥐 오케스트라","다람쥐들과 합주한 아기 🐿️","도토리 리듬 다람쥐 밴드 (페르소나: 숲속 지휘자)",1.15, new[]{
            B("숲속 다람쥐들이 모여든다","다람쥐","35mm lens, dappled forest glow, a baby surrounded by curious squirrels holding acorns like instruments, whimsical woodland light",31,"fade","pluck@C,cute@R25"),
            B("도토리로 톡톡 박자를 맞춰","톡톡","35mm, warm dapples, a baby tapping acorns in rhythm as squirrels join with tiny taps, a building woodland beat, focused joy",52,"zoomin","pop@L25,ding@R25"),
            B("어 다람쥐가 도토리를 훔쳐","훔쳐","35mm lens, bright glow, a cheeky squirrel snatching an acorn and darting off as a baby gives comic chase, giggling",70,"slideleft","whoosh@C,boing@R25"),
            B("숲속 대합주 만세","합주","35mm lens, radiant forest glow, a baby conducting a joyful squirrel orchestra amid falling leaves, arms wide, woodland harmony",88,"hblur","sparkle@C,giggle@R30") }),
          new("mermaid","animals","🧜","바다속 인어 아기","바닷속 인어가 된 아기 🧜","반짝이는 바닷속 인어 모험 (페르소나: 바다의 인어)",1.0, new[]{
            B("반짝이는 바닷속을 헤엄친다","바닷속","35mm lens, dreamy underwater blue glow, a baby mermaid with a shimmering tail gliding through a coral reef, sun rays and bubbles",30,"fade","whoosh@C,sparkle@R25"),
            B("물고기 친구들이 인사해","물고기","35mm, luminous aqua light, colorful fish swirling around a delighted baby mermaid, gentle currents, wonder-wide eyes",50,"zoomin","ding@C,pop@R25"),
            B("어 반짝이는 진주를 발견","진주","35mm lens, glowing light, a baby mermaid discovering a glowing pearl in a clam, sparkling awe, drifting bubbles",70,"slideleft","sparkle@C,boing@R25"),
            B("바다의 인어 공주 만세","공주","35mm lens, radiant underwater glow, a baby mermaid twirling joyfully amid fish and pearls, tail shimmering, blissful",88,"hblur","giggle@C,sparkle@R30") }),
          new("butterfly","animals","🦋","나비 정원의 아기","나비들과 노는 아기 🦋","꽃과 나비의 정원 (페르소나: 나비 요정)",1.0, new[]{
            B("꽃 정원에 나비가 가득하다","나비","50mm lens, soft sunlit garden glow, a baby amid a cloud of colorful butterflies over blooming flowers, dreamy bokeh, wonder",30,"fade","pluck@C,sparkle@R25"),
            B("한 마리가 손끝에 앉는다","손끝","50mm macro, warm glow, a butterfly delicately landing on a baby's outstretched fingertip, breath held, sparkling awe",50,"zoomin","ding@C,suspense@R25"),
            B("어 나비가 코에 앉아 간지러","코","50mm lens, bright glow, a butterfly perching on the baby's nose triggering a ticklish cross-eyed giggle, petals drifting",70,"slideleft","boing@C,giggle@R25"),
            B("나비 정원의 요정 만세","요정","50mm lens, radiant garden glow, a baby laughing surrounded by swirling butterflies and petals, arms wide, magical joy",88,"hblur","sparkle@C,giggle@R30") }),
          new("penguinice","animals","🐧","펭귄 빙하 친구","펭귄들과 미끄럼 탄 아기 🐧","남극 펭귄들과의 우정 (페르소나: 남극 탐험 친구)",1.15, new[]{
            B("빙하 위 펭귄들을 만난다","펭귄","24mm wide, crisp icy blue glow, a bundled baby meeting a huddle of waddling penguins on glittering ice, frosty breath, delight",32,"fade","cute@C,ding@R25"),
            B("함께 뒤뚱뒤뚱 걸어본다","뒤뚱","24mm, bright ice light, a baby waddling in a line with the penguins, mimicking their walk, joyful synchronized wobble",52,"zoomin","boing@L30,pop@R25"),
            B("어 얼음 미끄럼틀 슝","슝","24mm wide, sparkling flare, a baby belly-sliding down an ice slope beside sliding penguins, snow spraying, exhilarated squeal",72,"slideleft","whoosh@C,giggle@R30"),
            B("남극 최고의 친구들 만세","친구들","24mm wide, radiant icy glow, a baby cheering with a huddle of penguins under sparkling snow, arms up, best-friend joy",88,"hblur","sparkle@C,giggle@R30") }),
          new("elephant","animals","🐘","아기 코끼리 목욕","코끼리 코 샤워 받는 아기 🐘","아기 코끼리와의 물놀이 (페르소나: 정글 물놀이 친구)",1.15, new[]{
            B("정글에서 아기 코끼리를 만난다","코끼리","24mm wide, warm jungle glow, a baby meeting a friendly baby elephant by a river, mutual curious trunks-and-hands, dappled light",32,"fade","cute@C,ding@R25"),
            B("코끼리가 코로 물을 빨아들여","빨아","24mm, bright glow, the baby elephant sucking river water into its trunk as a baby watches with fascinated anticipation",52,"zoomin","suspense@C,pop@R25"),
            B("어 첨벙 물 샤워 세례","첨벙","24mm wide, sparkling splash flare, the elephant spraying a gentle shower over a shrieking-with-laughter soaked baby, droplets flying",78,"slideleft","splash@C,giggle@R30"),
            B("정글 물놀이 친구 만세","물놀이","24mm wide, radiant glow, a drenched giggling baby hugging the baby elephant's trunk, splashing together, best-friend warmth",89,"hblur","sparkle@C,giggle@R30") }),
          new("panda","animals","🐼","판다 대나무 친구","판다랑 대나무 먹는 아기 🐼","뒹굴뒹굴 판다와의 하루 (페르소나: 판다 절친)",1.0, new[]{
            B("대나무 숲에서 판다를 만난다","판다","50mm lens, soft misty bamboo glow, a baby meeting a fluffy baby panda munching bamboo, mutual round-eyed curiosity, green haze",31,"fade","cute@C,ding@R25"),
            B("대나무를 같이 아작아작","아작","50mm macro, warm glow, a baby and panda gnawing opposite ends of a bamboo stalk, comic tug, focused chubby cheeks",52,"zoomin","chomp@C,pop@R25"),
            B("어 판다가 데굴 넘어졌다","데굴","50mm lens, bright glow, the clumsy panda rolling over backward as a baby bursts into delighted giggles, bamboo scattering",72,"slideleft","boing@C,giggle@R30"),
            B("판다랑 뒹굴뒹굴 절친 만세","뒹굴","50mm lens, radiant misty glow, a baby and panda tumbling and cuddling in the bamboo, best-friend warmth, soft sparkle",88,"hblur","sparkle@C,giggle@R30") }),
        }),
    };
}
