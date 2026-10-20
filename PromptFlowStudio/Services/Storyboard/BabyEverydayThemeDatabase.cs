using System;
using System.Collections.Generic;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>
/// [PromptFlowStudio] v16.6.0 실생활 밀착형 아기 테마 — 놀이터·키즈카페·놀이공원·일상 나들이 4대 카테고리(<see cref="BabyThemeDatabase"/> 동일 스키마).
///   ▸ 각 비트 Shot 에 <b>전진 모션 벡터</b>(forward/upward/toward camera 등 물리적으로 맞는 방향)를 authored → Wan 2.1 I2V 역재생 오류 억제.
///   ▸ 카테고리별 네거티브(<see cref="NegativeFor"/>)는 <see cref="MotionVectorPromptEngine.ReverseGuardNegative"/> 를 결합해
///     <see cref="VideoFormatFactory"/> 가 컷 생성 시 <see cref="StoryboardScene.NegativePrompt"/> 로 결선한다.
///   ▸ <see cref="SearchTagsOf"/> — 카테고리명·동의어(볼풀/그네/회전목마/유모차 등)를 검색 인덱스에 합류(<see cref="ThemeSearchFilterService"/>).
///   순수 BCL Authored Data · 모바일 링크.
/// </summary>
public static class BabyEverydayThemeDatabase
{
    public const string Playground = "playground", KidsCafe = "kidscafe", AmusementPark = "amusement", Everyday = "everyday";

    // 카테고리 공통 네거티브(안전·물리 일관성) — 역방향 방지 키워드는 NegativeFor 에서 결합.
    private const string BaseNegative = "blurry, low quality, watermark, distorted face, deformed hands, extra limbs, unsafe fall, injury";

    private static readonly string PlaygroundNegative = MotionVectorPromptEngine.AppendReverseGuard(
        BaseNegative + ", sliding upward, swing moving unnaturally, sand flowing upward, bubbles shrinking into wand");
    private static readonly string KidsCafeNegative = MotionVectorPromptEngine.AppendReverseGuard(
        BaseNegative + ", balls flying back into pit, floating in mid-air, crowded strangers' faces");
    private static readonly string AmusementNegative = MotionVectorPromptEngine.AppendReverseGuard(
        BaseNegative + ", carousel spinning backwards, train rolling backwards, balloon sinking, unsafe standing on ride");
    private static readonly string EverydayNegative = MotionVectorPromptEngine.AppendReverseGuard(
        BaseNegative + ", stroller rolling backwards, water flowing upward, dog walking backwards, baby standing in cart");

    // 검색 동의어 — 카테고리명 + 대표 놀이 키워드(제목/샷에 없는 구어 표현 보강).
    private const string PlaygroundTags = "놀이터 🎠 playground 미끄럼틀 그네 시소 모래 모래놀이 비눗방울 버블 정글짐 짚라인 흔들말 터널 뺑뺑이 분필 암벽 평균대 그물";
    private const string KidsCafeTags = "키즈카페 🎈 kids cafe 볼풀 볼풀장 편백 편백칩 트램폴린 방방 소꿉놀이 주방놀이 낚시 역할놀이 요리사 의사 병원놀이 슬라임 촉감놀이 키네틱 블록 붕붕카 버블쇼 물감";
    private const string AmusementTags = "놀이공원 🎡 테마파크 amusement 회전목마 꼬마기차 기차 솜사탕 풍선 퍼레이드 야간 커피잔 관람차 범퍼카 캐릭터 불꽃놀이 롤러코스터 보트 거울 열기구";
    private const string EverydayTags = "일상 야외 나들이 🌿 유모차 산책 마트 카트 분수 바닥분수 물놀이 강아지 반려견 물웅덩이 비 낮잠 과일 피크닉 소풍 자전거 아쿠아리움 딸기 농장 도서관 그림책 버스 세차";

    /// <summary>카테고리 키 → 네거티브 프롬프트(역방향 방지 포함). 이 모듈 소속이 아니면 null.</summary>
    public static string? NegativeFor(string? categoryKey) => categoryKey switch
    {
        Playground => PlaygroundNegative,
        KidsCafe => KidsCafeNegative,
        AmusementPark => AmusementNegative,
        Everyday => EverydayNegative,
        _ => null,
    };

    /// <summary>카테고리 키 → 검색 태그(카테고리명·동의어). 이 모듈 소속이 아니면 빈 문자열.</summary>
    public static string SearchTagsOf(string? categoryKey) => categoryKey switch
    {
        Playground => PlaygroundTags,
        KidsCafe => KidsCafeTags,
        AmusementPark => AmusementTags,
        Everyday => EverydayTags,
        _ => "",
    };

    private static BabyBeat B(string v, string g, string shot, int t, string x, string sfx) => new(v, g, shot, t, x, sfx);

    /// <summary>4대 실생활 카테고리(58 테마 · v16.10.0 카테고리당 +10).</summary>
    public static IReadOnlyList<BabyCategory> Categories { get; } = new List<BabyCategory>
    {
        // ═══ 🎠 놀이터 ═══
        new(Playground, "🎠", "놀이터", "미끄럼틀·그네·정글짐·짚라인·모래성·비눗방울 바깥 놀이", new List<BabyThemeDef>
        {
            new("pg_slide", Playground, "🛝", "미끄럼틀 탈출", "꼭대기에서 망설이던 아기, 3초 뒤 슝 🛝", "미끄럼틀 정상 정복 → 시원한 하강 (페르소나: 탈출 전문가)", 1.1, new[]
            {
                B("작전명 미끄럼틀 탈출 개시", "탈출", "24mm wide, bright afternoon sun, a toddler sitting at the top of a colorful playground slide gripping the rails, determined squint, safe padded rubber ground below, toddler leaning forward to start",30,"fade","drumroll@C,cute@R20"),
                B("하나 둘 셋 출발한다", "출발", "low-angle 35mm, sunny playground, the toddler pushing off and sliding forward down the slide toward the camera, hair fluttering backward in the wind, gravity-correct downward motion",62,"zoomin","whoosh@L30,boing@R25"),
                B("슝 바람보다 빠르다", "슝", "35mm tracking, dappled sun, the toddler gliding down to the bottom of the slide feet first, arms up in joy, natural forward momentum, mouth open in a delighted shriek",86,"slideleft","whoosh@C,giggle@R30"),
                B("한 번 더 한 번만 더", "한 번 더", "24mm wide, warm golden light, the toddler landing softly at the slide bottom and pointing back at the ladder with a huge grin, ready to climb again",44,"hblur","giggle@C,sparkle@R25"),
            }),
            new("pg_swing", Playground, "🪁", "그네 높이 타기", "하늘까지 닿을 것 같은 그네 💨", "앞으로 쭉, 하늘로 쭉 (페르소나: 꼬마 비행사)", 1.0, new[]
            {
                B("오늘의 비행 준비 완료", "비행", "50mm, soft morning light, a baby safely buckled in a bucket toddler swing at a park, tiny hands on the chains, excited wide eyes, parent's hand gently on the seat",28,"fade","cute@C,pluck@R20"),
                B("앞으로 쭉 하늘로 쭉", "하늘로", "low-angle 35mm, blue sky, the swing arcing forward and upward toward the camera in a smooth natural pendulum motion, baby's legs kicking forward, cheeks pink",64,"zoomin","whoosh@L30,giggle@R25"),
                B("바람이 간지러워 꺄르르", "꺄르르", "35mm, bright sun flare, the baby at the forward peak of the swing laughing with eyes squeezed shut, hair lifting softly in the breeze, continuous pendulum motion",84,"slideleft","giggle@C,sparkle@R30"),
                B("비행 성공 착륙합니다", "착륙", "50mm, golden afternoon light, the swing slowing to a gentle stop while the baby claps and beams up at the parent, serene park background",40,"hblur","clap@C,ding@R25"),
            }),
            new("pg_seesaw", Playground, "⚖️", "시소 쿵쾅", "쿵! 하고 올라가는 순간의 표정 😆", "엄마랑 시소 대결 (페르소나: 저울의 왕)", 1.05, new[]
            {
                B("자 누가 더 무거운지 대결", "대결", "35mm, sunny playground, a toddler holding the handle on one end of a low seesaw, parent kneeling at the other end, playful challenge in the eyes",32,"fade","drumroll@C,cute@R20"),
                B("쿵 하고 내려간다", "쿵", "35mm, bright light, the toddler's side of the seesaw lowering gently to the ground with a soft bump, knees bending naturally, surprised open mouth",58,"zoomin","boing@C,squeak@R30"),
                B("이번엔 하늘로 슝", "하늘로", "low-angle 35mm, blue sky, the seesaw lifting the toddler upward in a smooth arc, legs dangling, delighted squeal, physically consistent lever motion",82,"slideleft","whoosh@L25,giggle@R30"),
                B("쿵쾅쿵쾅 멈출 수 없어", "쿵쾅", "35mm wide, golden light, the toddler and parent laughing together as the seesaw rocks steadily up and down, warm family moment",46,"hblur","giggle@C,clap@R25"),
            }),
            new("pg_sandcastle", Playground, "🏰", "모래성 쌓기", "꼬마 건축가의 모래성 대작 🏖️", "삽질 → 완공 → 와르르 (페르소나: 모래 건축가)", 1.0, new[]
            {
                B("오늘의 건축 현장에 도착", "건축", "macro 85mm, warm afternoon sun, a toddler in a sunhat crouching in a sandbox with a tiny red shovel and bucket, sand on chubby knees, focused frown",30,"fade","pluck@C,cute@R20"),
                B("한 삽 한 삽 정성껏", "정성", "macro 85mm, soft sun, the toddler scooping sand into the bucket and patting it down, sand grains falling downward naturally from the shovel, tongue poking out",50,"zoomin","pop@L25,suspense@C"),
                B("짜잔 모래성 완공", "완공", "50mm, bright light, the toddler lifting the bucket to reveal a perfect sand tower, eyes wide with pride, arms raised in triumph",76,"slideleft","ding@C,sparkle@R30"),
                B("와르르 그래도 행복해", "와르르", "50mm, golden light, the sand tower crumbling downward as the toddler giggles and pats the pile, sand dust drifting in the sunlight",58,"hblur","crash@C,giggle@R25"),
            }),
            new("pg_bubbles", Playground, "🫧", "비눗방울 잡기", "잡힐 듯 말 듯 비눗방울 사냥 🫧", "무지개 방울 사냥꾼 (페르소나: 방울 헌터)", 1.0, new[]
            {
                B("하늘에서 보석이 내려온다", "보석", "85mm, backlit golden hour, dozens of iridescent soap bubbles drifting outward from a bubble wand toward a toddler on the grass, rainbow sheen, wonder-struck face",28,"fade","twinkle@C,cute@R20"),
                B("살금살금 다가간다", "살금살금", "85mm, soft backlight, the toddler toddling forward with arms outstretched toward a floating bubble, bubbles drifting slowly with the breeze",50,"zoomin","suspense@C,pluck@R20"),
                B("톡 잡았다", "톡", "macro 100mm, sparkling light, a bubble popping on the toddler's fingertip into a fine mist, the toddler's eyes flying wide in astonishment",80,"slideleft","pop@C,squeak@R30"),
                B("더 많이 더 많이", "더 많이", "50mm wide, golden haze, the toddler spinning happily amid a cloud of bubbles rising and drifting away, pure joy, dreamy bokeh",44,"hblur","giggle@C,twinkle@R25"),
            }),
            // v16.10.0 확장
            new("pg_junglegym", Playground, "🧗", "정글짐 탐험", "정글짐 꼭대기 = 아기의 에베레스트 🏔️", "한 칸 한 칸 정상 정복 (페르소나: 정글 탐험대장)", 1.05, new[]
            {
                B("정글 탐험대 출동", "출동", "24mm wide, bright afternoon sun, a toddler in a striped t-shirt standing at the base of a colorful low jungle gym, gripping the first bar with both hands, determined explorer face, soft rubber ground",30,"fade","drumroll@C,cute@R20"),
                B("한 칸씩 위로 위로", "위로", "low-angle 35mm, blue sky, the toddler climbing upward one rung at a time, knees lifting naturally, tiny hands wrapping around the bars, parent's hand spotting nearby",56,"zoomin","boing@L25,suspense@C"),
                B("정상이 코앞이다", "정상", "crane up 35mm, sunny daylight, the camera rising with the toddler as it pulls itself onto the top platform, cheeks flushed, physically grounded climbing motion",78,"slideleft","whoosh@C,ding@R25"),
                B("에베레스트 정복 만세", "만세", "24mm wide, golden light, the toddler standing safely at the top railing waving both arms toward the camera, playground spread out below, triumphant grin",52,"hblur","clap@C,sparkle@R25"),
            }),
            new("pg_zipline", Playground, "🚡", "짚라인 비행", "꼬마 짚라인, 슈웅 하늘을 난다 🦅", "안전벨트 매고 하늘 비행 (페르소나: 꼬마 독수리)", 1.15, new[]
            {
                B("비행 준비 완료", "비행", "35mm, bright morning sunlight, a toddler wearing a small helmet sitting in a padded child zipline seat, hands on the rope, parent checking the safety strap, nervous excited eyes",32,"fade","drumroll@C,suspense@R20"),
                B("출발 하나 둘 셋", "출발", "tracking 35mm, sunny daylight, the zipline seat gliding forward along the cable toward the camera, the toddler's hair streaming backward in the wind, smooth forward motion",68,"zoomin","whoosh@L30,giggle@R25"),
                B("나는 독수리다", "독수리", "aerial drone shot, blue sky, the toddler swooping forward over the green playground on the zipline, legs dangling, mouth open in a joyful shriek",88,"slideleft","whoosh@C,giggle@R30"),
                B("착륙 또 타고 싶어", "또", "50mm, golden afternoon light, the seat slowing to a gentle stop at the end platform, the toddler clapping and pointing back toward the start, beaming",46,"hblur","clap@C,ding@R25"),
            }),
            new("pg_springrider", Playground, "🐴", "스프링 흔들말", "흔들흔들 스프링 말 타고 달려라 🐎", "꼬마 카우보이의 질주 (페르소나: 꼬마 카우보이)", 1.0, new[]
            {
                B("내 애마를 소개합니다", "애마", "50mm, soft morning light, a baby in a tiny cowboy hat sitting on a bright yellow spring rocking horse in a playground, holding the handles, proud little smile",28,"fade","cute@C,pluck@R20"),
                B("이랴 앞으로 달려", "이랴", "35mm, sunny daylight, the baby rocking forward and back on the spring horse, the spring bending naturally, hat bouncing on the head, delighted squeal",62,"zoomin","boing@C,giggle@R25"),
                B("바람을 가르는 질주", "질주", "bouncing handheld 35mm, bright light, the baby leaning forward into the motion with wide eyes, cheeks wobbling, steady spring physics",80,"slideleft","boing@L25,whoosh@R25"),
                B("오늘의 경주 우승", "우승", "50mm, golden light, the spring horse settling to a stop as the baby raises one fist in victory, hat askew, a gummy grin",42,"hblur","clap@C,sparkle@R25"),
            }),
            new("pg_tunnel", Playground, "🕳️", "터널 기어가기", "터널 끝에서 까꿍! 👀", "알록달록 터널 탐험 (페르소나: 꼬마 동굴 탐험가)", 1.0, new[]
            {
                B("저 동굴 속엔 뭐가 있을까", "동굴", "35mm, bright daylight, a baby on hands and knees peering into the entrance of a bright red plastic crawl tunnel, light glowing at the far end, curious face",30,"fade","suspense@C,cute@R20"),
                B("살금살금 앞으로", "살금살금", "tracking 24mm inside the tunnel, soft diffused red glow, the baby crawling forward toward the camera, knees and palms moving naturally, determined expression",52,"zoomin","pop@L20,suspense@C"),
                B("빛이 보인다", "빛", "24mm, backlit sunny exit, the baby emerging headfirst from the tunnel exit into the sunlight, squinting happily, hair fluffy with static",74,"slideleft","ding@C,sparkle@R25"),
                B("까꿍 여기 있지롱", "까꿍", "50mm, bright light, the baby sitting at the tunnel mouth giggling at the parent with a peekaboo face, both hands up",56,"hblur","giggle@C,cute@R25"),
            }),
            new("pg_spinner", Playground, "🌀", "뺑뺑이 회전놀이", "빙글빙글 세상이 돈다 🌀", "회전 원판 위 꼬마 우주비행 (페르소나: 회전 마스터)", 1.1, new[]
            {
                B("회전 비행 준비", "회전", "35mm, sunny playground, a toddler sitting cross-legged in the center of a low merry-go-round spinner, holding the bars, parent's hand on the rim, excited eyes",30,"fade","drumroll@C,cute@R20"),
                B("빙글빙글 출발", "빙글빙글", "orbit 35mm, bright daylight, the spinner turning slowly and steadily, the toddler's hair fanning outward, consistent circular motion in one direction",62,"zoomin","whoosh@C,giggle@R25"),
                B("세상이 빙글빙글 돈다", "세상", "360 orbit shot, blue sky, the toddler laughing with head tipped back as the playground blurs gently behind, smooth forward rotation",84,"slideleft","whoosh@L25,giggle@R30"),
                B("어질어질 그래도 또", "또", "50mm, golden light, the spinner coming to a gentle stop, the toddler wobbling happily and holding up one finger for one more turn",46,"hblur","boing@C,giggle@R25"),
            }),
            new("pg_sandwater", Playground, "🏞️", "모래 물길 만들기", "꼬마 토목기사의 물길 공사 🚧", "모래에 강을 만드는 여름 공사 (페르소나: 꼬마 토목기사)", 1.0, new[]
            {
                B("오늘의 공사 계획", "공사", "macro 85mm, bright afternoon sun, a toddler in a sunhat kneeling in a sandbox digging a winding channel with a toy shovel, sand grains glistening",28,"fade","pluck@C,cute@R20"),
                B("물을 부어볼까", "물", "macro 85mm, sunny daylight, the toddler tipping a small watering can, water pouring downward into the sand channel and flowing forward along the groove, gravity-correct flow",54,"zoomin","splash@C,suspense@R20"),
                B("강이 생겼다", "강", "bird's-eye 35mm, bright light, the water snaking downhill through the sand river, the toddler pointing at it in amazement, wet sand darkening naturally",76,"slideleft","ding@C,sparkle@R25"),
                B("토목기사 공사 완료", "완료", "50mm, golden light, the toddler patting a sand dam with muddy hands and grinning proudly at the camera",44,"hblur","clap@C,giggle@R25"),
            }),
            new("pg_chalk", Playground, "🖍️", "분필 그림 그리기", "바닥 전체가 아기의 도화지 🌈", "보도블록 위 꼬마 화가 (페르소나: 길바닥 피카소)", 1.0, new[]
            {
                B("오늘의 캔버스는 바닥", "캔버스", "bird's-eye 35mm, bright morning daylight, a toddler squatting on a smooth playground pavement holding a fat pink sidewalk chalk, colorful chalk sticks scattered around",26,"fade","pluck@C,cute@R20"),
                B("쓱쓱 해님을 그려요", "해님", "macro 85mm, sunny light, the toddler's chubby hand dragging the chalk forward in a wobbly circle, chalk dust powdering the fingertips",48,"zoomin","pop@L20,suspense@C"),
                B("짜잔 무지개 완성", "무지개", "crane up 24mm, bright daylight, the camera rising to reveal a big rainbow and sun drawn on the ground around the beaming toddler",74,"slideleft","ding@C,sparkle@R30"),
                B("내 손도 무지개색", "손", "50mm, golden light, the toddler holding up chalk-dusted rainbow palms toward the camera with a proud giggle",50,"hblur","giggle@C,cute@R25"),
            }),
            new("pg_climbwall", Playground, "🧱", "꼬마 암벽 등반", "작은 손으로 꽉! 꼬마 클라이머 🧗", "낮은 볼더링 벽 정복기 (페르소나: 꼬마 클라이머)", 1.05, new[]
            {
                B("저 벽을 오르겠다", "벽", "35mm, bright afternoon sun, a toddler looking up at a short colorful climbing wall with chunky handholds, safety mat below, rolling up imaginary sleeves",30,"fade","drumroll@C,suspense@R20"),
                B("손 하나 발 하나", "하나", "low-angle 35mm, sunny daylight, the toddler reaching upward for a green handhold and stepping a foot onto a yellow hold, natural upward climbing motion",58,"zoomin","boing@L20,suspense@C"),
                B("영차 거의 다 왔어", "영차", "crane up 35mm, blue sky, the camera rising alongside the toddler pulling upward toward the top hold, tongue poking out in concentration",80,"slideleft","whoosh@C,ding@R25"),
                B("등반 성공 꼬마 클라이머", "성공", "50mm, golden light, the toddler perched safely at the top ledge with the parent's hands nearby, clapping proudly",50,"hblur","clap@C,sparkle@R25"),
            }),
            new("pg_balance", Playground, "🤹", "평균대 건너기", "후들후들 한 발 한 발 균형왕 ⚖️", "낮은 평균대 위 서커스 (페르소나: 꼬마 곡예사)", 1.0, new[]
            {
                B("곡예사 등장", "곡예사", "35mm, bright daylight, a toddler standing at the start of a low wooden balance beam, arms stretched out wide like airplane wings, parent holding one fingertip",30,"fade","drumroll@C,cute@R20"),
                B("한 발 한 발 앞으로", "앞으로", "tracking 35mm, sunny light, the toddler stepping forward along the beam one careful foot at a time, arms wobbling for balance, natural forward walking",56,"zoomin","suspense@C,pluck@R20"),
                B("후들 그래도 안 떨어져", "후들", "50mm, dappled sun, the toddler wobbling mid-beam then steadying with a determined squint, realistic body weight shift",72,"slideleft","boing@C,squeak@R25"),
                B("도착 균형왕 탄생", "균형왕", "35mm wide, golden light, the toddler hopping off the end of the beam onto the soft ground and throwing arms up in triumph",54,"hblur","clap@C,giggle@R25"),
            }),
            new("pg_nethammock", Playground, "🕸️", "그물 해먹 출렁", "출렁출렁 그물 위 꺄르르 🕸️", "거대 그물 해먹 탐험 (페르소나: 거미줄 탐험가)", 1.0, new[]
            {
                B("거대한 거미줄 발견", "거미줄", "24mm wide, soft morning sun, a toddler lying on a large rope net hammock strung between posts, fingers poking through the net holes, curious smile",26,"fade","cute@C,pluck@R20"),
                B("출렁출렁 파도 같아", "출렁출렁", "35mm, dappled daylight, the net swaying gently up and down under the toddler as the parent rocks it, the toddler rolling slightly with natural sag physics",56,"zoomin","boing@C,giggle@R25"),
                B("하늘이 보여요", "하늘", "bird's-eye 35mm, blue sky, the toddler spread like a starfish on the net looking straight up at the clouds, giggling, hair spread out",70,"slideleft","sparkle@C,giggle@R25"),
                B("거미줄 탐험 완료", "완료", "50mm, golden light, the toddler crawling forward across the net toward the parent's open arms, contented grin",40,"hblur","cute@C,twinkle@R25"),
            }),
        }),

        // ═══ 🎈 키즈카페 ═══
        new(KidsCafe, "🎈", "키즈카페", "볼풀·트램폴린·역할놀이·슬라임·붕붕카 실내 놀이", new List<BabyThemeDef>
        {
            new("kc_ballpit", KidsCafe, "🟡", "볼풀장 다이빙", "볼풀 속으로 풍덩! 사라진 아기 🎈", "알록달록 볼 바다 다이빙 (페르소나: 볼풀 다이버)", 1.1, new[]
            {
                B("저 알록달록 바다가 나를 부른다", "바다", "24mm wide, bright soft indoor lighting, a toddler standing at the edge of a ball pit filled with pastel plastic balls, eyes sparkling, clean bright kids cafe interior",30,"fade","cute@C,drumroll@R25"),
                B("하나 둘 셋 풍덩", "풍덩", "35mm, soft LED light, the toddler leaning forward and flopping belly-first into the ball pit, balls scattering outward and rolling away naturally",66,"zoomin","splash@C,boing@R30"),
                B("어디 갔게 여기 있지", "여기", "50mm, bright light, only the toddler's giggling face and two hands poking up out of the colorful balls, a ball balanced on the head",78,"slideleft","pop@C,giggle@R30"),
                B("볼풀 다이버 임무 완수", "임무", "35mm wide, cheerful light, the toddler throwing a handful of balls forward into the air with both arms, balls arcing and falling back down, huge grin",50,"hblur","giggle@C,sparkle@R25"),
            }),
            new("kc_cypress", KidsCafe, "🌲", "편백칩 놀이", "편백 향 가득 숲속 모래놀이 🌲", "편백칩 산을 쌓고 뿌리는 힐링 놀이 (페르소나: 숲의 정령)", 1.0, new[]
            {
                B("숲 냄새가 솔솔 나는 곳", "숲", "50mm, warm wooden interior light, a toddler sitting in a pool of pale hinoki cypress wood chips, sniffing a handful with closed eyes, cozy natural texture",26,"fade","pluck@C,cute@R20"),
                B("한 줌 가득 퍼올려서", "한 줌", "macro 85mm, soft warm light, the toddler scooping wood chips with both hands and letting them fall downward between the fingers, chips tumbling naturally",48,"zoomin","pop@L20,suspense@C"),
                B("편백 눈이 내려요", "눈", "50mm, golden diffused light, the toddler tossing wood chips upward and watching them sprinkle down like snow, mouth open in wonder",76,"slideleft","sparkle@C,giggle@R30"),
                B("숲의 정령 힐링 완료", "힐링", "35mm wide, warm cozy glow, the toddler lying back blissfully in the wood chips making a chip angel, peaceful smile",38,"hblur","twinkle@C,cute@R25"),
            }),
            new("kc_trampoline", KidsCafe, "🤸", "트램폴린 방방 점프", "방방 위 아기 = 통통 튀는 공 🤸", "방방 점프 챌린지 (페르소나: 꼬마 우주인)", 1.15, new[]
            {
                B("오늘은 달나라에 간다", "달나라", "35mm, bright indoor light, a toddler standing in the middle of a netted kids trampoline holding a parent's hands, knees bending in preparation",32,"fade","drumroll@C,cute@R20"),
                B("방방 위로 위로", "위로", "low-angle 35mm, soft LED light, the toddler bouncing upward off the trampoline mat, hair flying up, the mat stretching naturally beneath the feet",70,"zoomin","boing@C,whoosh@R25"),
                B("무중력 꺄르르", "무중력", "35mm, bright light, the toddler at the top of a small bounce with arms spread wide, ecstatic open-mouth laugh, then falling naturally back down",86,"slideleft","boing@L25,giggle@R30"),
                B("착지 성공 한 번 더", "착지", "50mm, cheerful light, the toddler plopping softly onto the bouncy mat on the bottom, rolling with giggles, safe netted enclosure",48,"hblur","giggle@C,clap@R25"),
            }),
            new("kc_kitchen", KidsCafe, "🍳", "주방 놀이 소꿉장난", "꼬마 셰프의 오늘의 메뉴 🍳", "장난감 주방 요리쇼 (페르소나: 꼬마 셰프)", 1.0, new[]
            {
                B("셰프 출근했습니다", "셰프", "50mm, warm pastel light, a toddler in a tiny chef apron standing at a wooden toy kitchen, wooden pans and felt vegetables, serious chef expression",28,"fade","ding@C,cute@R20"),
                B("보글보글 맛있게", "보글보글", "macro 85mm, soft light, the toddler stirring a toy pot with a wooden spoon in steady circles, felt carrot inside, concentrated pout",48,"zoomin","pluck@C,pop@R25"),
                B("자 맛보세요 손님", "맛보세요", "50mm, warm light, the toddler holding a toy spoon forward toward the camera offering a taste, proud beaming smile",70,"slideleft","ding@C,giggle@R25"),
                B("오늘도 별 다섯 개", "별 다섯", "35mm wide, cozy light, the toddler clapping proudly behind a toy table full of plastic dishes, chef hat askew",44,"hblur","clap@C,sparkle@R30"),
            }),
            new("kc_fishing", KidsCafe, "🎣", "낚시왕 아기", "자석 낚싯대로 대어 낚기 🐟", "실내 낚시터 월척 도전 (페르소나: 낚시왕)", 1.0, new[]
            {
                B("오늘은 월척의 날이다", "월척", "50mm, bright soft light, a toddler sitting by a shallow toy fishing pool holding a tiny magnetic fishing rod, colorful plastic fish floating, focused eyes",28,"fade","suspense@C,cute@R20"),
                B("조용히 조용히 기다려", "조용히", "macro 85mm, soft light, the magnet tip of the rod dipping down toward a floating toy fish, tiny ripples spreading outward, toddler holding breath",52,"zoomin","suspense@C,pluck@R20"),
                B("잡았다 대어다", "잡았다", "50mm, bright light, the toddler lifting the rod upward with a toy fish attached by the magnet, water droplets falling down, eyes wide with triumph",84,"slideleft","splash@C,ding@R30"),
                B("낚시왕 등극", "낚시왕", "35mm wide, cheerful light, the toddler hugging a pile of caught toy fish with a proud toothy grin",46,"hblur","clap@C,sparkle@R25"),
            }),
            // v16.10.0 확장
            new("kc_cookplay", KidsCafe, "👩‍🍳", "꼬마 요리사 역할놀이", "주방놀이 셰프의 특제 요리 🍳", "장난감 주방 요리 대회 (페르소나: 꼬마 셰프)", 1.0, new[]
            {
                B("셰프 출근했습니다", "셰프", "50mm, soft LED kids cafe lighting, a toddler in a tiny white chef hat and apron standing at a pastel toy kitchen, holding a wooden spatula, serious chef face",28,"fade","ding@C,cute@R20"),
                B("달달 볶아볼까", "볶아", "macro 85mm, bright soft light, the toddler stirring felt vegetables in a toy frying pan, wrist moving in natural circles, tongue poking out in focus",52,"zoomin","pop@L20,suspense@C"),
                B("특제 요리 완성", "완성", "50mm, pastel studio light, the toddler lifting a toy plate forward toward the camera with both hands, proud wide eyes",74,"slideleft","ding@C,sparkle@R25"),
                B("맛있게 드세요", "드세요", "35mm, soft diffused light, the toddler feeding a pretend bite to the parent and giggling as the parent fakes a delighted chew",50,"hblur","giggle@C,chomp@R25"),
            }),
            new("kc_doctorplay", KidsCafe, "🩺", "꼬마 의사 역할놀이", "청진기 대고 진지하게… 어디가 아프세요? 🩺", "곰인형 환자 진료 (페르소나: 꼬마 원장님)", 1.0, new[]
            {
                B("원장님 진료 시작", "진료", "50mm, soft studio lighting, a toddler wearing a tiny white doctor coat with a toy stethoscope around the neck, a teddy bear patient lying on a toy bed",28,"fade","ding@C,cute@R20"),
                B("쿵쿵 심장 소리 들어볼게요", "쿵쿵", "macro 85mm, pastel soft light, the toddler pressing the toy stethoscope to the teddy bear's chest, eyebrows furrowed in professional concentration",50,"zoomin","suspense@C,pluck@R20"),
                B("주사 따끔 참아요", "따끔", "50mm, bright soft light, the toddler gently tapping a toy syringe on the bear's arm and patting its head to comfort it, caring expression",66,"slideleft","pop@C,squeak@R25"),
                B("다 나았어요 퇴원", "퇴원", "35mm, cheerful LED light, the toddler hugging the bandaged teddy bear tightly and beaming at the camera",52,"hblur","clap@C,sparkle@R25"),
            }),
            new("kc_slime", KidsCafe, "🟢", "슬라임 촉감놀이", "쭈욱~ 늘어나는 슬라임에 눈이 휘둥그레 🟢", "말랑말랑 촉감 실험실 (페르소나: 꼬마 과학자)", 1.0, new[]
            {
                B("이 말랑한 건 뭐지", "말랑", "macro 100mm, soft diffused studio light, a toddler poking a glossy mint-green slime blob on a tray with one fingertip, curious open mouth",28,"fade","pop@C,cute@R20"),
                B("쭈욱 늘려보자", "쭈욱", "extreme macro, bright softbox, the toddler pulling the slime apart with both hands, the slime stretching and sagging downward naturally with gravity, glossy strands",58,"zoomin","squeak@C,suspense@R20"),
                B("뽁 소리가 났다", "뽁", "macro 85mm, pastel light, the toddler squishing the slime in a fist, little air bubbles popping, eyes flying wide in surprise",80,"slideleft","pop@C,giggle@R30"),
                B("촉감 실험 대성공", "대성공", "50mm, soft studio light, the toddler holding up slime-covered hands toward the camera with a delighted belly laugh",50,"hblur","giggle@C,sparkle@R25"),
            }),
            new("kc_kinetic", KidsCafe, "🏖️", "키네틱 모래 놀이", "흘러내리는 모래가 신기해 ✨", "실내 매직 샌드 공방 (페르소나: 모래 마법사)", 1.0, new[]
            {
                B("마법의 모래 등장", "마법", "macro 85mm, soft LED light, a toddler sitting at a low table with a tray of pastel blue kinetic sand, mold cups and a tiny rolling pin beside",26,"fade","twinkle@C,cute@R20"),
                B("꾹꾹 눌러서 쏙", "꾹꾹", "macro 85mm, bright soft light, the toddler pressing a star mold into the sand and lifting it upward to reveal a crisp star shape",50,"zoomin","pop@C,suspense@R20"),
                B("사르르 녹아내려요", "사르르", "extreme macro, diffused studio light, the sand star slowly slumping and flowing downward under its own weight, the toddler watching with a gasp",72,"slideleft","sparkle@C,squeak@R25"),
                B("모래 마법 완성", "완성", "50mm, pastel light, the toddler proudly patting a lumpy sand castle and grinning at the camera",44,"hblur","clap@C,giggle@R25"),
            }),
            new("kc_blocks", KidsCafe, "🧱", "대형 블록 성 쌓기", "내 키보다 큰 성을 쌓았다 🏰", "소프트 블록 건축가 (페르소나: 꼬마 건축왕)", 1.05, new[]
            {
                B("건축왕 현장 도착", "건축왕", "24mm wide, bright soft indoor lighting, a toddler surrounded by big colorful foam building blocks on a padded floor, hugging one block like a treasure",28,"fade","drumroll@C,cute@R20"),
                B("하나 위에 또 하나", "하나", "35mm, soft LED light, the toddler lifting a big red foam block upward and placing it on top of a growing tower, arms stretching high",56,"zoomin","pop@L20,suspense@C"),
                B("내 키보다 높아", "높아", "crane up 24mm, bright light, the camera rising to show the block tower taller than the toddler, the toddler looking up in awe",76,"slideleft","ding@C,sparkle@R25"),
                B("와르르 그래도 재밌어", "와르르", "35mm wide, cheerful light, the foam tower toppling downward softly around the giggling toddler, blocks bouncing on the mat",70,"hblur","crash@C,giggle@R30"),
            }),
            new("kc_ridecar", KidsCafe, "🏎️", "붕붕카 레이싱", "부릉부릉 키즈카페 F1 출전 🏁", "실내 트랙 붕붕카 경주 (페르소나: 꼬마 레이서)", 1.15, new[]
            {
                B("레이서 출전 준비", "레이서", "35mm, bright LED lighting, a toddler sitting in a red ride-on toy car at a painted indoor race track start line, hands on the steering wheel, focused grin",30,"fade","drumroll@C,suspense@R20"),
                B("부릉부릉 출발", "출발", "tracking 35mm, bright soft light, the toddler pushing the toy car forward with both feet, wheels rolling forward along the track, determined face",64,"zoomin","whoosh@L30,boing@R20"),
                B("코너를 돌아간다", "코너", "low-angle tracking, neon accent lights, the toddler steering the car around a gentle curve of the track, leaning into the turn, natural forward momentum",82,"slideleft","whoosh@C,giggle@R25"),
                B("결승선 통과 우승", "우승", "50mm, bright light, the toddler crossing the checkered finish line and raising both arms, parent cheering in soft focus",50,"hblur","clap@C,sparkle@R25"),
            }),
            new("kc_mart", KidsCafe, "🏪", "마트 계산대 놀이", "삑! 바코드 찍는 꼬마 점원 🛍️", "장난감 마트 운영기 (페르소나: 꼬마 사장님)", 1.0, new[]
            {
                B("어서오세요 꼬마 마트", "마트", "50mm, soft pastel lighting, a toddler standing behind a toy cash register in a miniature play supermarket, colorful toy groceries on the shelves",28,"fade","ding@C,cute@R20"),
                B("삑 바코드 찍어요", "삑", "macro 85mm, bright soft light, the toddler sliding a toy milk carton across the scanner, pressing a big register button with a fingertip",52,"zoomin","ding@C,pop@R20"),
                B("영수증 나왔습니다", "영수증", "50mm, LED light, the toddler handing a paper slip forward to the parent with a very serious shopkeeper face",68,"slideleft","pluck@C,squeak@R25"),
                B("오늘 장사 대박", "대박", "35mm, cheerful studio light, the toddler clapping behind the register and hugging a toy apple, proud grin",48,"hblur","clap@C,giggle@R25"),
            }),
            new("kc_bubbleshow", KidsCafe, "🫧", "버블 쇼 관람", "거대 비눗방울 속에 아기가 쏙 🫧", "버블 아티스트 공연 (페르소나: 버블 왕자님)", 1.0, new[]
            {
                B("버블 쇼가 시작됐어", "버블 쇼", "35mm, soft blue LED stage lighting, a toddler sitting cross-legged on a mat watching a performer lift a giant bubble wand, eyes wide",28,"fade","twinkle@C,suspense@R20"),
                B("거대한 방울이 내려와", "방울", "macro 85mm, pastel backlight, a huge iridescent bubble wall rising slowly upward around the toddler standing in a ring, rainbow film shimmering",58,"zoomin","sparkle@C,pop@R20"),
                B("방울 속에 내가 있어", "방울 속", "orbit 35mm, glittering stage light, the toddler standing inside a giant bubble dome, gently touching the shimmering wall with one finger, awestruck",82,"slideleft","twinkle@C,giggle@R25"),
                B("톡 터지고 꺄르르", "꺄르르", "50mm, soft light, the bubble popping into fine mist around the toddler who bursts into giggles and claps",60,"hblur","pop@C,clap@R25"),
            }),
            new("kc_fingerpaint", KidsCafe, "🎨", "핑거페인팅 놀이", "손바닥 도장 쾅! 꼬마 화가 🖐️", "물감 범벅 미술 시간 (페르소나: 꼬마 화가)", 1.0, new[]
            {
                B("오늘의 작품을 그린다", "작품", "50mm, bright studio softbox, a toddler in a clear smock sitting before a big white paper, small pots of red, yellow and blue washable paint in front",26,"fade","pluck@C,cute@R20"),
                B("손바닥에 물감 듬뿍", "듬뿍", "macro 85mm, soft light, the toddler dipping a chubby palm into yellow paint, paint coating the fingers, a curious squint",50,"zoomin","squeak@C,suspense@R20"),
                B("쾅 손도장 완성", "쾅", "bird's-eye 35mm, bright light, the toddler pressing the painted hand down onto the paper and lifting it to reveal a bright handprint sun",74,"slideleft","pop@C,ding@R25"),
                B("얼굴까지 알록달록", "알록달록", "50mm, pastel studio light, the toddler holding up the painting with paint smudges on the cheeks and nose, giggling proudly",56,"hblur","giggle@C,sparkle@R25"),
            }),
            new("kc_ballblaster", KidsCafe, "🎯", "공 대포 발사", "슈웅! 볼 블라스터 명중 🎯", "에어 볼 대포 사격왕 (페르소나: 꼬마 포병)", 1.1, new[]
            {
                B("포병 위치로", "포병", "35mm, bright soft indoor lighting, a toddler standing at a colorful air-powered ball blaster in a kids cafe play structure, loading a soft yellow ball",30,"fade","drumroll@C,suspense@R20"),
                B("발사 준비 완료", "발사", "macro 85mm, LED light, the toddler's small hands pushing the ball into the tube and gripping the trigger handle, concentrated frown",50,"zoomin","suspense@C,pluck@R20"),
                B("슈웅 명중이다", "명중", "tracking 35mm, bright light, the soft ball shooting forward out of the tube in a gentle arc and bouncing off a target, the toddler gasping in delight",84,"slideleft","whoosh@C,boing@R30"),
                B("사격왕 등극", "사격왕", "50mm, cheerful light, the toddler jumping up and down with both arms raised as soft balls rain down gently from above",58,"hblur","giggle@C,clap@R25"),
            }),
        }),

        // ═══ 🎡 놀이공원 ═══
        new(AmusementPark, "🎡", "놀이공원", "회전목마·관람차·범퍼카·야간 퍼레이드·불꽃놀이 테마파크 나들이", new List<BabyThemeDef>
        {
            new("ap_carousel", AmusementPark, "🎠", "회전목마 손흔들기", "회전목마 위에서 엄마 찾아 손 흔들기 👋", "반짝이는 회전목마 왕자님/공주님 (페르소나: 꼬마 기사)", 1.0, new[]
            {
                B("반짝이는 말을 타고 출발", "출발", "50mm, warm twinkling carousel lights, a toddler safely seated on a painted carousel horse with a parent standing beside, golden mirrored canopy, excited grin",30,"fade","twinkle@C,cute@R20"),
                B("빙글빙글 앞으로", "빙글빙글", "35mm, golden bokeh, the carousel rotating steadily forward, the horse gently rising and falling, the toddler holding the brass pole, consistent forward rotation",58,"zoomin","pluck@C,whoosh@R25"),
                B("엄마 여기 나 여기 있어", "여기", "50mm, warm light, the toddler spotting the camera and waving a tiny hand enthusiastically while gliding past, sparkling eyes",82,"slideleft","giggle@C,sparkle@R30"),
                B("동화 속 기사님 완성", "기사님", "wide 35mm, magical dusk glow, the whole carousel glowing with lights as the toddler rides by beaming, fairytale atmosphere",44,"hblur","twinkle@C,ding@R25"),
            }),
            new("ap_minitrain", AmusementPark, "🚂", "꼬마기차 여행", "칙칙폭폭 꼬마기차 기관사 출동 🚂", "공원 한 바퀴 기차 여행 (페르소나: 꼬마 기관사)", 1.0, new[]
            {
                B("기관사 탑승 완료", "기관사", "35mm, bright sunny park, a toddler in a striped conductor cap seated in an open mini train car with a parent, safety bar down, proud smile",28,"fade","ding@C,cute@R20"),
                B("칙칙폭폭 출발", "출발", "tracking 35mm, sunny day, the mini train moving forward along the track past flowerbeds, scenery passing from front to back, the toddler leaning forward excitedly",60,"zoomin","whoosh@C,pluck@R25"),
                B("터널이다 꺄아", "터널", "35mm, playful light contrast, the train heading forward into a small colorful tunnel, the toddler squealing with hands over cheeks",80,"slideleft","suspense@L20,giggle@R30"),
                B("한 바퀴 여행 끝", "여행", "50mm, golden light, the train arriving at the station as the toddler waves goodbye to the train with a happy sigh",40,"hblur","ding@C,clap@R25"),
            }),
            new("ap_treats", AmusementPark, "🍭", "솜사탕·풍선 득템", "구름 한 입, 풍선 하나 득템 🎈", "솜사탕과 풍선을 손에 넣은 날 (페르소나: 득템 요정)", 1.0, new[]
            {
                B("저 분홍 구름은 뭐지", "구름", "85mm, bright pastel park, a toddler staring up wide-eyed at a giant pink cotton candy held by a parent, a heart balloon floating above",30,"fade","cute@C,twinkle@R20"),
                B("한 입 사르르", "사르르", "macro 100mm, soft light, the toddler taking a tiny bite of cotton candy that melts on the lips, sticky pink fluff on the nose, blissful closed eyes",56,"zoomin","chomp@C,sparkle@R25"),
                B("풍선도 내 거야", "풍선", "50mm, sunny day, a balloon string tied safely around the toddler's wrist, the balloon floating upward and bobbing in the breeze, toddler looking up delighted",78,"slideleft","pop@C,giggle@R30"),
                B("오늘의 득템 완료", "득템", "35mm wide, golden light, the toddler walking forward proudly holding cotton candy with the balloon floating overhead, carnival bokeh",46,"hblur","sparkle@C,ding@R25"),
            }),
            new("ap_parade", AmusementPark, "🎉", "퍼레이드 신기한 눈빛", "퍼레이드에 넋 나간 아기 눈빛 ✨", "반짝반짝 퍼레이드 관람 (페르소나: 꼬마 관객)", 1.0, new[]
            {
                B("저기 뭐가 온다", "온다", "50mm, festive dusk lights, a toddler sitting on a parent's shoulders, eyes fixed on colorful parade floats approaching forward in the distance",30,"fade","drumroll@C,cute@R20"),
                B("우와 반짝반짝", "반짝반짝", "85mm, sparkling light reflections, extreme close-up of the toddler's wide amazed eyes reflecting dancing parade lights, mouth agape",60,"zoomin","twinkle@C,sparkle@R25"),
                B("나도 같이 춤출래", "춤출래", "50mm, vibrant festive light, the toddler bouncing and clapping along to the music as the floats pass forward from left to right, confetti falling down",84,"slideleft","clap@C,giggle@R30"),
                B("꿈 같은 밤이야", "꿈", "wide 35mm, magical night glow, the toddler resting a sleepy head on the parent with a content smile as the parade lights fade into the distance",40,"hblur","twinkle@C,cute@R25"),
            }),
            // v16.10.0 확장
            new("ap_nightparade", AmusementPark, "🌃", "야간 퍼레이드 관람", "반짝이는 밤 퍼레이드에 넋 나간 아기 ✨", "빛의 행렬을 처음 본 밤 (페르소나: 꼬마 관객)", 1.0, new[]
            {
                B("밤인데 길이 반짝여", "반짝", "50mm, night with twinkling string lights, a baby sitting on a parent's shoulders at the edge of a theme park street, wearing glowing mouse-ear headband, waiting",28,"fade","twinkle@C,suspense@R20"),
                B("빛의 행렬이 다가온다", "행렬", "tracking 35mm, neon festive parade lights, an illuminated parade float rolling forward along the street toward the camera, the baby's face lit in shifting colors",60,"zoomin","drumroll@C,sparkle@R25"),
                B("우와 손 흔들어 주세요", "손", "85mm, colorful neon bokeh, the baby waving both hands excitedly at the passing float, mouth open in amazement, reflections dancing in the eyes",82,"slideleft","clap@C,giggle@R25"),
                B("꿈같은 밤 마무리", "꿈", "wide 35mm, night fireworks glow, the sleepy baby resting a cheek on the parent's head as the last float passes, a dreamy smile",36,"hblur","twinkle@C,cute@R25"),
            }),
            new("ap_teacup", AmusementPark, "☕", "커피잔 회전", "빙글빙글 찻잔 속 아기 ☕", "핑크 찻잔 회전 놀이기구 (페르소나: 찻잔 공주님)", 1.05, new[]
            {
                B("찻잔에 탑승", "탑승", "35mm, bright pastel daylight, a baby sitting safely between parents inside a giant pink teacup ride, gripping the center wheel, curious eyes",30,"fade","cute@C,ding@R20"),
                B("빙글빙글 돌아간다", "빙글빙글", "orbit 35mm, sunny daylight, the teacup turning slowly and steadily on the platform, the baby's hair lifting softly outward, consistent circular motion",64,"zoomin","whoosh@C,giggle@R25"),
                B("세상이 회오리처럼", "회오리", "360 orbit, bright festive colors, the baby laughing with head tipped back as pastel teacups swirl past in the background, smooth rotation",84,"slideleft","whoosh@L25,giggle@R30"),
                B("어지러워도 또 탈래", "또", "50mm, golden afternoon light, the teacup slowing to a stop, the baby wobbling happily and clapping, cheeks rosy",44,"hblur","clap@C,boing@R25"),
            }),
            new("ap_ferris", AmusementPark, "🎡", "대관람차 하늘 구경", "꼭대기에서 바라본 세상은? 🎡", "하늘 끝까지 올라가는 관람차 (페르소나: 하늘 여행자)", 1.0, new[]
            {
                B("동그란 집이 올라간다", "올라간다", "35mm, golden hour, a baby sitting on a parent's lap inside a ferris wheel gondola, palms pressed to the window glass, curious wide eyes",28,"fade","cute@C,suspense@R20"),
                B("점점 하늘로", "하늘로", "crane up shot, sunset sky, the gondola rising steadily upward along the wheel, the theme park shrinking below, the baby's face glowing in warm light",56,"zoomin","whoosh@C,twinkle@R20"),
                B("꼭대기다 우와", "꼭대기", "aerial drone shot, golden sunset, the gondola at the top of the giant wheel with the whole park spread out, the baby pointing out the window in awe",80,"slideleft","sparkle@C,giggle@R25"),
                B("하늘 여행 완료", "완료", "50mm, dusk glow, the baby yawning contentedly against the parent's chest as the gondola descends smoothly",34,"hblur","twinkle@C,cute@R25"),
            }),
            new("ap_bumper", AmusementPark, "🚗", "꼬마 범퍼카", "쿵! 부딪혀도 꺄르르 🚗", "유아 범퍼카 첫 운전 (페르소나: 꼬마 드라이버)", 1.15, new[]
            {
                B("드라이버 착석", "드라이버", "35mm, neon festive arena lights, a toddler sitting beside a parent in a small padded bumper car, hands on the steering wheel, determined grin",30,"fade","drumroll@C,cute@R20"),
                B("부릉 앞으로 출발", "출발", "tracking 35mm, colorful neon light, the bumper car gliding forward across the smooth floor, the toddler steering with both hands, hair bouncing",62,"zoomin","whoosh@C,giggle@R20"),
                B("쿵 살짝 부딪혔다", "쿵", "50mm, bright arena light, the bumper car gently bumping a padded rail and rebounding softly, the toddler bursting into surprised laughter",84,"slideleft","boing@C,giggle@R30"),
                B("최고의 드라이버", "최고", "50mm, festive neon glow, the car coasting to a stop as the toddler throws both arms up and cheers",50,"hblur","clap@C,sparkle@R25"),
            }),
            new("ap_mascot", AmusementPark, "🐻", "캐릭터 인형 만남", "커다란 곰돌이와 첫 포옹 🧸", "인형탈 친구와 첫 인사 (페르소나: 꼬마 팬클럽 회장)", 1.0, new[]
            {
                B("저 커다란 곰은 누구", "곰", "50mm, bright sunny daylight, a toddler standing in a theme park plaza staring up at a giant friendly bear mascot costume, half shy half excited",28,"fade","suspense@C,cute@R20"),
                B("조심조심 다가가기", "조심조심", "tracking 35mm, dappled light, the toddler walking forward slowly toward the mascot holding the parent's hand, the mascot kneeling down and waving",52,"zoomin","pluck@C,suspense@R20"),
                B("하이파이브 성공", "하이파이브", "85mm, bright light, the toddler giving the mascot's big fluffy paw a high five, eyes lighting up, a gasp of delight",76,"slideleft","clap@C,ding@R25"),
                B("꼬옥 안아주기", "꼬옥", "50mm, golden light, the toddler wrapping arms around the mascot's fluffy belly in a big hug, cheek pressed into the fur",56,"hblur","cute@C,sparkle@R25"),
            }),
            new("ap_fireworks", AmusementPark, "🎆", "첫 불꽃놀이", "펑! 밤하늘 꽃에 놀란 아기 🎆", "성 위로 터지는 불꽃 축제 (페르소나: 밤하늘 관측자)", 1.0, new[]
            {
                B("밤하늘이 조용해", "밤하늘", "50mm, blue-hour night sky, a baby wearing soft ear-protection muffs sitting on a parent's lap facing a lit fairytale castle, waiting quietly",26,"fade","suspense@C,twinkle@R20"),
                B("펑 하늘에 꽃이 폈다", "펑", "wide 35mm, night with festive fireworks, the first firework bursting upward into a golden flower above the castle, light flashing on the baby's surprised face",66,"zoomin","crash@C,sparkle@R25"),
                B("알록달록 별비가 내려", "별비", "85mm, neon firework glow, sparkling embers falling slowly downward across the sky reflected in the baby's wide eyes, mouth open in wonder",84,"slideleft","sparkle@C,twinkle@R25"),
                B("박수 짝짝 최고야", "박수", "50mm, warm night glow, the baby clapping excitedly as the finale lights up the castle, nestled safely in the parent's arms",56,"hblur","clap@C,giggle@R25"),
            }),
            new("ap_minicoaster", AmusementPark, "🎢", "꼬마 롤러코스터", "생애 첫 롤러코스터 리액션 🎢", "유아용 미니 코스터 도전 (페르소나: 꼬마 스릴러)", 1.15, new[]
            {
                B("안전바 내리고 출발 준비", "준비", "35mm, sunny daylight, a toddler buckled beside a parent in the front car of a small kiddie roller coaster shaped like a caterpillar, lap bar down, nervous excited eyes",32,"fade","drumroll@C,suspense@R20"),
                B("오르막 두근두근", "두근두근", "low-angle 35mm, bright blue sky, the coaster car climbing slowly upward along a gentle hill, the toddler gripping the bar and peeking forward",56,"zoomin","suspense@C,pluck@R20"),
                B("슈웅 내려간다", "슈웅", "tracking 24mm, bright sunlight, the coaster swooping forward down the small slope, the toddler's hair flying backward, a joyful shriek",88,"slideleft","whoosh@C,giggle@R30"),
                B("한 번만 더 타자", "한 번만", "50mm, golden light, the coaster rolling into the station, the toddler laughing and pointing back at the track",48,"hblur","clap@C,giggle@R25"),
            }),
            new("ap_boatride", AmusementPark, "🚣", "꼬마 보트 탐험", "둥실둥실 물 위의 꼬마 선장 ⛵", "인형 나라 보트 투어 (페르소나: 꼬마 선장)", 1.0, new[]
            {
                B("선장 탑승 완료", "선장", "35mm, soft festive lighting, a baby in a tiny sailor hat sitting in a small boat ride beside a parent, gentle water channel ahead, curious eyes",26,"fade","cute@C,pluck@R20"),
                B("둥실둥실 앞으로", "둥실둥실", "tracking 35mm, twinkling lights, the baby's boat floating forward smoothly along the water channel, the baby gripping the rim, gentle ripples spreading outward behind it, natural water physics",52,"zoomin","splash@C,twinkle@R20"),
                B("인형들이 노래해요", "노래", "50mm, colorful festive light, animated dolls singing along the canal walls, the baby bouncing happily and pointing at them",74,"slideleft","sparkle@C,giggle@R25"),
                B("항해 성공 박수", "항해", "50mm, warm light, the boat drifting into the exit dock as the baby claps with both hands, sailor hat askew",44,"hblur","clap@C,cute@R25"),
            }),
            new("ap_mirrorhouse", AmusementPark, "🪞", "거울의 집", "거울 속 아기가 열 명?! 🪞", "요술 거울 미로 탐험 (페르소나: 꼬마 미스터리 탐정)", 1.0, new[]
            {
                B("반짝이는 미로 입구", "미로", "35mm, soft neon festive lighting, a toddler holding a parent's hand at the entrance of a mirror maze, many reflections stretching into the distance",28,"fade","suspense@C,twinkle@R20"),
                B("어 내가 여러 명", "여러 명", "50mm, cool LED glow, the toddler staring at a dozen mirrored copies of itself, tilting its head as all the reflections tilt in sync, consistent reflections",56,"zoomin","ding@C,squeak@R20"),
                B("요술 거울에 길쭉길쭉", "길쭉", "fisheye lens, bright light, the toddler giggling at a funhouse mirror that stretches its reflection tall and thin, hands on cheeks",80,"slideleft","boing@C,giggle@R30"),
                B("탐정 미로 탈출", "탈출", "tracking 35mm, warm light, the toddler walking forward out of the maze exit into the sunlight with a triumphant grin",46,"hblur","clap@C,sparkle@R25"),
            }),
            new("ap_hotair", AmusementPark, "🎈", "꼬마 열기구 회전", "둥둥 떠오르는 열기구 놀이기구 🎈", "풍선 바구니 하늘 산책 (페르소나: 꼬마 탐험가)", 1.0, new[]
            {
                B("바구니에 쏙", "바구니", "35mm, bright morning daylight, a baby sitting in a wicker-basket ride car under a colorful striped hot-air balloon canopy, parent beside, curious eyes",26,"fade","cute@C,pluck@R20"),
                B("둥실 떠오른다", "둥실", "crane up shot, blue sky, the balloon ride lifting slowly upward on its central arm and beginning a gentle circular glide, the baby gazing down",54,"zoomin","whoosh@C,twinkle@R20"),
                B("하늘을 빙글 산책", "산책", "aerial drone shot, sunny daylight, the balloon cars circling smoothly above the park, the baby waving both hands at the people below",76,"slideleft","sparkle@C,giggle@R25"),
                B("사뿐히 착륙", "착륙", "50mm, golden light, the basket descending gently to the platform, the baby clapping and leaning on the parent with a happy sigh",40,"hblur","clap@C,cute@R25"),
            }),
        }),

        // ═══ 🌿 일상·야외 ═══
        new(Everyday, "🌿", "일상·야외", "유모차 나들이·물웅덩이·피크닉·아쿠아리움·반려견 낮잠", new List<BabyThemeDef>
        {
            new("ev_stroller", Everyday, "🌞", "유모차 햇살 나들이", "햇살 맞으며 유모차 드라이브 ☀️", "공원 산책길 유모차 투어 (페르소나: 꼬마 VIP)", 1.0, new[]
            {
                B("오늘의 드라이브 시작", "드라이브", "50mm, soft morning sunlight, a baby reclining in a stroller with a sunshade, tree-lined park path ahead, curious bright eyes",26,"fade","pluck@C,cute@R20"),
                B("살랑살랑 바람이 좋아", "살랑살랑", "tracking 35mm, dappled sunlight through leaves, the stroller rolling forward smoothly along the path, the baby's hair stirring in the breeze, gentle forward motion",48,"zoomin","whoosh@L20,twinkle@R25"),
                B("저 새는 뭐야", "새", "85mm, bright light, the baby pointing a tiny finger at a bird on a branch, delighted gasp, sun flares through the trees",70,"slideleft","ding@C,giggle@R25"),
                B("햇살 VIP 투어 완료", "투어", "wide 35mm, golden hour, the baby dozing peacefully in the stroller as it rolls forward along the sunlit path, serene ending",36,"hblur","twinkle@C,cute@R20"),
            }),
            new("ev_cart", Everyday, "🛒", "마트 카트 탑승", "마트 카트 = 아기 전용 리무진 🛒", "장보기 동행 쇼핑왕 (페르소나: 쇼핑왕)", 1.05, new[]
            {
                B("리무진 탑승 완료", "리무진", "35mm, bright supermarket lighting, a baby safely seated and buckled in a shopping cart child seat, colorful produce aisles behind, excited kicking legs",30,"fade","ding@C,cute@R20"),
                B("출발 쇼핑 원정", "원정", "tracking 35mm, bright light, the cart rolling forward down the aisle, shelves passing from front to back, the baby gripping the handle and leaning forward",58,"zoomin","whoosh@C,pluck@R25"),
                B("이거 이거 담아주세요", "담아", "50mm, warm light, the baby reaching forward toward a bright red apple, eyes huge, mouth in an eager O",80,"slideleft","pop@C,giggle@R30"),
                B("오늘의 쇼핑왕", "쇼핑왕", "35mm, cheerful light, the baby hugging a loaf of bread proudly in the cart seat with a toothy grin",46,"hblur","clap@C,sparkle@R25"),
            }),
            new("ev_fountain", Everyday, "⛲", "바닥 분수대 물놀이", "바닥에서 물이 솟아오르면? 꺄악 💦", "여름날 바닥분수 첫 체험 (페르소나: 물방울 탐험가)", 1.1, new[]
            {
                B("바닥에서 뭔가 나온다", "바닥", "24mm wide, bright summer sun, a toddler in a swimsuit and sunhat standing on a wet splash pad, peering curiously at the small fountain holes",30,"fade","suspense@C,cute@R20"),
                B("슈욱 물기둥이다", "물기둥", "low-angle 35mm, sparkling sunlight, jets of water shooting upward from the ground around the toddler and falling back down naturally, the toddler jumping in surprise",72,"zoomin","splash@C,whoosh@R25"),
                B("차가워 근데 재밌어", "재밌어", "50mm, glittering backlight, the toddler laughing with water droplets falling down over the face and shoulders, hands reaching into the rising spray",84,"slideleft","splash@L25,giggle@R30"),
                B("여름 정복 완료", "여름", "wide 35mm, golden summer light, the drenched toddler running forward through the fountains toward the parent with a beaming smile, rainbow mist",48,"hblur","sparkle@C,clap@R25"),
            }),
            new("ev_puppy", Everyday, "🐶", "강아지와의 산책", "강아지랑 아기, 세상 제일 귀여운 산책 🐾", "털친구와 첫 산책 (페르소나: 꼬마 산책 대장)", 1.0, new[]
            {
                B("털친구랑 산책 가자", "털친구", "50mm, soft afternoon sun, a toddler and a gentle golden retriever puppy side by side at the start of a grassy park path, toddler holding the parent's hand",28,"fade","cute@C,pluck@R20"),
                B("같이 앞으로 총총", "총총", "tracking 35mm, dappled light, the toddler and the puppy walking forward together along the path, the puppy's tail wagging, natural forward walking gait for both",54,"zoomin","pop@L20,giggle@R25"),
                B("간지러워 뽀뽀 공격", "뽀뽀", "85mm, warm backlight, the puppy gently licking the toddler's cheek, the toddler squeezing eyes shut in a giggle, soft fur detail",82,"slideleft","giggle@C,squeak@R30"),
                B("우리는 단짝 친구", "단짝", "wide 35mm, golden hour, the toddler sitting on the grass hugging the puppy as the sun sets behind them, heartwarming finale",40,"hblur","twinkle@C,cute@R25"),
            }),
            // v16.10.0 확장
            new("ev_puddle", Everyday, "🌧️", "물웅덩이 첨벙첨벙", "장화 신고 첨벙! 비 온 뒤 최고의 놀이 💦", "물웅덩이 점프 챔피언 (페르소나: 첨벙 챔피언)", 1.1, new[]
            {
                B("물웅덩이를 발견했다", "발견", "35mm, soft overcast daylight after rain, a toddler in a yellow raincoat and red rubber boots standing at the edge of a big muddy puddle on a park path, mischievous grin",30,"fade","suspense@C,cute@R20"),
                B("하나 둘 셋 점프", "점프", "low-angle 35mm, overcast light, the toddler jumping forward and landing with both boots in the puddle, water splashing outward and falling back down naturally",76,"zoomin","splash@C,boing@R25"),
                B("첨벙첨벙 멈출 수 없어", "첨벙첨벙", "tracking 35mm, wet reflective ground, the toddler stomping forward through the puddle, droplets flying in arcs, a joyful shriek",88,"slideleft","splash@L25,giggle@R30"),
                B("흠뻑 젖어도 행복해", "행복", "50mm, soft daylight breaking through clouds, the muddy toddler grinning at the camera with drops dripping off the raincoat hood",46,"hblur","giggle@C,sparkle@R25"),
            }),
            new("ev_dognap", Everyday, "🐕", "반려견과의 낮잠", "강아지 품에서 새근새근 꿀잠 💤", "털 이불 덮고 함께 꿈나라 (페르소나: 꿀잠 콤비)", 0.95, new[]
            {
                B("털친구 옆에 누웠어", "털친구", "50mm, soft afternoon window light, a baby lying on a cozy blanket next to a calm golden retriever on a living room floor, tiny hand resting on the dog's fur",24,"fade","cute@C,pluck@R20"),
                B("눈꺼풀이 무거워져", "무거워", "macro 85mm, warm diffused light, the baby's eyelids slowly drooping as the dog's chest rises and falls gently, subtle breathing motion",36,"zoomin","twinkle@C,suspense@R20"),
                B("새근새근 꿈나라", "꿈나라", "bird's-eye 35mm, cozy warm glow, the baby and the dog curled up together fast asleep, the dog's tail slowly sweeping once, peaceful",30,"slideleft","twinkle@C,cute@R20"),
                B("우리는 꿀잠 콤비", "콤비", "50mm, golden window light, the baby stirring awake and smiling sleepily as the dog licks its hand softly",40,"hblur","giggle@C,sparkle@R25"),
            }),
            new("ev_martfruit", Everyday, "🍎", "마트 과일 코너 탐험", "알록달록 과일 앞에서 눈이 반짝 🍊", "카트 타고 과일 사냥 (페르소나: 꼬마 바이어)", 1.0, new[]
            {
                B("과일 나라에 도착", "과일", "35mm, bright supermarket lighting, a baby buckled in a shopping cart seat rolling up to a colorful fruit display of oranges, apples and bananas, eyes sparkling",28,"fade","ding@C,cute@R20"),
                B("이 동그란 건 뭐야", "동그란", "macro 85mm, bright light, the baby reaching forward and touching a bright orange, little fingers feeling the bumpy peel, curious frown",50,"zoomin","pop@C,suspense@R20"),
                B("냄새도 맡아볼래", "냄새", "85mm, soft bright light, the baby holding a small apple up to its nose and sniffing, eyes closing in delight",66,"slideleft","sparkle@C,giggle@R25"),
                B("이거 담아 주세요", "담아", "tracking 35mm, bright light, the cart rolling forward down the aisle as the baby hugs the apple proudly in the seat",48,"hblur","clap@C,cute@R25"),
            }),
            new("ev_picnic", Everyday, "🧺", "공원 피크닉", "돗자리 위 꼬마 미식가의 소풍 🧺", "봄날 잔디밭 도시락 소풍 (페르소나: 소풍 대장)", 1.0, new[]
            {
                B("소풍 출발", "소풍", "24mm wide, dappled morning sunlight, a toddler sitting on a red checkered picnic blanket in a green park with a wicker basket, sunhat on, excited smile",26,"fade","cute@C,pluck@R20"),
                B("도시락 열어볼까", "도시락", "macro 85mm, bright daylight, the toddler lifting the lid of a lunchbox to reveal tiny rice balls and fruit, gasping happily",50,"zoomin","ding@C,pop@R20"),
                B("냠냠 꿀맛이야", "꿀맛", "85mm, dappled sun through leaves, the toddler biting into a little rice ball, cheeks puffing, leaves swaying gently overhead",66,"slideleft","chomp@C,giggle@R25"),
                B("잔디밭 데굴데굴", "데굴데굴", "35mm wide, golden afternoon light, the toddler rolling forward across the grass giggling as the parent laughs nearby",58,"hblur","giggle@C,sparkle@R25"),
            }),
            new("ev_bike", Everyday, "🚲", "아빠 자전거 뒷자리", "바람 가르는 자전거 데이트 🚲", "강변 자전거 라이딩 (페르소나: 꼬마 라이더)", 1.05, new[]
            {
                B("헬멧 쓰고 준비 완료", "헬멧", "50mm, soft morning sunlight, a toddler wearing a small helmet buckled into a child seat on the back of a parent's bicycle, riverside path ahead",28,"fade","ding@C,cute@R20"),
                B("페달 밟고 출발", "출발", "tracking 35mm, bright daylight, the bicycle rolling forward smoothly along the riverside path, the toddler's cheeks wobbling, wheels turning in the correct direction",60,"zoomin","whoosh@C,giggle@R20"),
                B("바람이 시원해", "바람", "low-angle tracking, sunny blue sky, the toddler spreading arms wide behind the parent, wind lifting the hair under the helmet, river sparkling",80,"slideleft","whoosh@L25,giggle@R30"),
                B("자전거 데이트 끝", "데이트", "50mm, golden hour, the bicycle stopping under a tree as the toddler hugs the parent's back with a happy smile",42,"hblur","clap@C,twinkle@R25"),
            }),
            new("ev_aquarium", Everyday, "🐠", "아쿠아리움 나들이", "유리 너머 물고기와 눈맞춤 🐟", "바닷속 터널 첫 탐험 (페르소나: 꼬마 해양학자)", 1.0, new[]
            {
                B("파란 세상에 왔어", "파란", "35mm, cool blue aquarium glow, a toddler standing in front of a huge glass tank, colorful fish swimming past, palms pressed to the glass",28,"fade","twinkle@C,suspense@R20"),
                B("물고기야 안녕", "안녕", "macro 85mm, soft blue diffused light, a small orange fish swimming forward right up to the glass where the toddler's finger points, eyes meeting",52,"zoomin","pop@C,cute@R20"),
                B("머리 위로 상어가", "상어", "tracking 24mm, blue underwater tunnel light, the toddler walking forward through the glass tunnel as a gentle shark glides overhead, mouth wide open",80,"slideleft","whoosh@C,giggle@R25"),
                B("꼬마 해양학자 탄생", "해양학자", "50mm, shimmering blue light, the toddler turning to the camera with a huge grin, light ripples dancing across the face",48,"hblur","clap@C,sparkle@R25"),
            }),
            new("ev_strawberry", Everyday, "🍓", "딸기 농장 체험", "내 손으로 딴 첫 딸기 🍓", "비닐하우스 딸기 따기 (페르소나: 꼬마 농부)", 1.0, new[]
            {
                B("빨간 보석이 주렁주렁", "보석", "50mm, bright diffused greenhouse daylight, a toddler in a tiny straw hat crouching beside rows of strawberry plants, ripe red berries hanging",26,"fade","cute@C,pluck@R20"),
                B("조심조심 똑 따기", "똑", "macro 100mm, soft bright light, the toddler's small fingers gently pulling a ripe strawberry off the stem, leaves bending naturally",52,"zoomin","pop@C,suspense@R20"),
                B("한 입 앙 달콤해", "달콤", "macro 85mm, bright light, the toddler biting the strawberry, red juice on the lips, eyes squeezing shut in delight",76,"slideleft","chomp@C,sparkle@R25"),
                B("바구니 가득 수확", "수확", "35mm, golden light through the greenhouse, the toddler holding up a small basket full of strawberries toward the camera, proud grin",50,"hblur","clap@C,giggle@R25"),
            }),
            new("ev_library", Everyday, "📚", "도서관 그림책 시간", "그림책 넘기며 쉿! 꼬마 독서가 📖", "어린이 도서관 첫 방문 (페르소나: 꼬마 독서왕)", 0.95, new[]
            {
                B("책 나라에 왔어요", "책 나라", "35mm, cozy warm library light, a toddler sitting on a round reading cushion surrounded by low shelves of colorful picture books, finger on lips",24,"fade","pluck@C,cute@R20"),
                B("한 장 한 장 넘겨요", "넘겨요", "macro 85mm, soft amber reading lamp, the toddler turning a thick board-book page with both hands, pictures of animals appearing",42,"zoomin","pop@C,suspense@R20"),
                B("어 사자가 나왔다", "사자", "50mm, warm cozy glow, the toddler pointing at a lion picture and roaring silently with a big open mouth, eyes sparkling",62,"slideleft","ding@C,giggle@R25"),
                B("책을 꼭 안고 쿨쿨", "쿨쿨", "50mm, warm tungsten light, the toddler dozing off against the cushion hugging the picture book, a peaceful smile",30,"hblur","twinkle@C,cute@R20"),
            }),
            new("ev_bus", Everyday, "🚌", "첫 버스 타기", "창밖 풍경에 눈을 못 떼는 아기 🚌", "시내버스 첫 탑승기 (페르소나: 꼬마 승객)", 1.0, new[]
            {
                B("커다란 버스가 왔다", "버스", "35mm, bright morning daylight, a toddler holding a parent's hand at a bus stop, a big green city bus pulling up, wide amazed eyes",28,"fade","ding@C,suspense@R20"),
                B("삑 카드 찍고 탑승", "탑승", "50mm, bright interior light, the toddler stepping forward up the bus stairs and tapping the card reader with the parent's help, proud smile",52,"zoomin","ding@C,pop@R20"),
                B("창밖이 휙휙 지나가", "휙휙", "50mm, sunny window light, the toddler kneeling on a seat with nose to the window as the city scenery passes, the bus moving forward smoothly",74,"slideleft","whoosh@C,giggle@R25"),
                B("정류장 도착 띵동", "띵동", "35mm, golden light, the toddler pressing the stop button with a happy squeal as the bus slows down",50,"hblur","ding@C,clap@R25"),
            }),
            new("ev_carwash", Everyday, "🫧", "자동 세차 터널", "거품 폭풍 속 꼬마 탐험 🚙", "카시트에서 본 세차장 쇼 (페르소나: 꼬마 우주비행사)", 1.0, new[]
            {
                B("거대한 동굴로 들어간다", "동굴", "35mm, soft LED tunnel light, a toddler buckled in a car seat looking out the side window as the car rolls forward into an automatic car wash, curious face",28,"fade","suspense@C,cute@R20"),
                B("거품이 쏟아진다", "거품", "50mm, colorful neon car wash lights, pink and blue foam sliding downward over the window glass, the toddler pressing palms to the glass in wonder",60,"zoomin","splash@C,pop@R20"),
                B("빙글빙글 솔이 춤춰요", "솔", "85mm, festive neon glow, giant spinning brushes sweeping across the car, the toddler squealing and kicking its feet happily",82,"slideleft","whoosh@C,giggle@R30"),
                B("반짝반짝 출구 탈출", "출구", "35mm, bright daylight, the car rolling forward out into the sunshine, the toddler clapping as water droplets sparkle on the window",46,"hblur","clap@C,sparkle@R25"),
            }),
        }),
    };
}
