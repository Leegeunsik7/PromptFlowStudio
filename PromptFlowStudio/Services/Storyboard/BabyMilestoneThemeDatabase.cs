using System.Collections.Generic;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>
/// [PromptFlowStudio] 아기 성장 마일스톤 독보적 감성 테마 백과사전(v2.9.0) — 순수 BCL Authored Data(<see cref="BabyThemeDatabase"/> 동일 스키마).
///   ▸ 탄생(Day 1)부터 1000일까지 아기 인생의 특별한 이정표를 <b>예술적 시네마틱 프롬프트</b>로 소장하는 마일스톤 전용 카테고리(<c>baby_milestone</c>).
///   ▸ 탄생·50일·백일·첫 이빨·200일·첫 걸음마·첫 말문·돌잔치·500일·1000일 첫 등원 — 각 4비트 감성 아크(도입→클로즈업→매직→피날레).
///   ▸ <see cref="BabyThemeDatabase"/> 가 카테고리로 병합 → 아기 테마 갤러리에서 바로 선택(엠보스 OFF · 아기 콘텐츠).
/// </summary>
public static class BabyMilestoneThemeDatabase
{
    /// <summary>마일스톤 카테고리(단일 · 10 테마).</summary>
    public static IReadOnlyList<BabyCategory> Categories { get; } = new List<BabyCategory>
    {
        new("baby_milestone", "🎀", "성장 마일스톤", "탄생~1000일 인생 이정표 감성 소장 시네마틱", new List<BabyThemeDef>
        {
            new("ms_birth", "baby_milestone", "👼", "탄생 · 첫 눈맞춤", "세상과의 첫 눈맞춤, 빛의 축복 👼", "신생아 베이비룸 · 천사 아우라", 1.0, new[]
            {
                new BabyBeat("세상에 온 걸 환영해 작은 기적아", "기적", "cinematic 50mm, soft heavenly window light, an ethereal newborn baby sleeping in a pristine white nursery, gentle golden angelic aura, dust motes floating, tender film grain", 24, "fade", "twinkle@C,sparkle@R25"),
                new BabyBeat("처음으로 눈을 맞춘 그 순간", "순간", "extreme close-up macro, the newborn slowly opening tiny eyes for the first time, catchlights sparkling, dewy skin, breathtaking soft focus, emotional", 46, "zoomin", "cute@C,ding@L20"),
                new BabyBeat("작은 손끝에 온 우주가 담겼어", "우주", "close-up of a newborn's tiny hand curling around a parent's finger, warm rim light, halo glow, cinematic shallow depth of field, pure love", 66, "slideleft", "pluck@C,sparkle@R25"),
                new BabyBeat("너의 첫 숨결, 우리의 새 시작", "시작", "wide serene shot of the newborn cradled in soft blankets bathed in radiant heavenly light, angel-wing bokeh, prestigious birth-announcement finale", 40, "hblur", "sparkle@C,twinkle@L20"),
            }),
            new("ms_day50", "baby_milestone", "🧚", "50일 · 베이비 요정", "50일의 베이비 요정 🧚", "플라워 보넷 · 숲속 요정 스튜디오", 1.0, new[]
            {
                new BabyBeat("어느새 오십 일, 요정이 되었네", "요정", "cinematic, dreamy forest-fairy studio set, a 50-day baby wearing a delicate flower bonnet, soft moss and fairy lights, enchanted pastel glow", 26, "fade", "twinkle@C,cute@R25"),
                new BabyBeat("꽃잎 사이로 방긋 웃어요", "방긋", "macro close-up of the baby's first gummy smile surrounded by drifting flower petals, magical bokeh, tender warm light", 50, "zoomin", "giggle@C,sparkle@L20"),
                new BabyBeat("작은 요정의 첫 마법이 반짝", "마법", "the baby reaching toward glowing floating fairy sparkles, whimsical enchanted forest, soft volumetric light beams", 70, "slideleft", "sparkle@C,ding@R25"),
                new BabyBeat("숲이 너를 축복하는 오십 일", "축복", "wide dreamy shot of the baby nestled in a flower-crown nest as fairy lights swirl, storybook fairytale finale, pastel magic", 42, "hblur", "twinkle@C,cute@L20"),
            }),
            new("ms_day100", "baby_milestone", "👑", "백일 · 꼬마 왕세자", "백일잔치 꼬마 왕세자 👑", "전통 한복 · 삼신상 고풍 어전 시네마틱", 1.0, new[]
            {
                new BabyBeat("백 일을 지켜낸 귀한 아가", "귀한", "cinematic, opulent traditional Korean royal hall, a 100-day baby in exquisite hanbok on silk cushions before a lavish samsin-sang feast table, warm lantern glow, regal grandeur", 28, "fade", "drumroll@C,ding@R25"),
                new BabyBeat("고운 한복에 담긴 백일의 소원", "소원", "close-up of the baby's hanbok embroidery and chubby cheeks, golden ornaments, candlelight, prestigious heirloom portrait, film grain", 52, "zoomin", "ding@C,sparkle@L20"),
                new BabyBeat("삼신할미도 미소 짓는 오늘", "미소", "the baby giggling amid stacked rainbow rice-cakes and fruit offerings, traditional court backdrop, auspicious red-and-gold palette", 72, "slideleft", "giggle@C,pop@R25"),
                new BabyBeat("백일의 축복, 만수무강하렴", "축복", "majestic wide shot of the little prince on a royal dais under silk canopies and hanging lanterns, dignified centennial finale", 44, "hblur", "drumroll@C,sparkle@R25"),
            }),
            new("ms_firsttooth", "baby_milestone", "🦷", "첫 이빨 · 뿅", "앙증맞은 첫 이빨 뿅 🦷", "치아 요정 마법 · 핑크 오로라", 1.05, new[]
            {
                new BabyBeat("어? 하얀 진주가 하나 뿅", "진주", "cinematic, soft pink aurora nursery, a baby grinning to reveal one tiny first tooth like a pearl, magical sparkle on the tooth, dreamy glow", 30, "fade", "pop@C,ding@R25"),
                new BabyBeat("치아 요정이 살짝 다녀갔대요", "요정", "whimsical tooth-fairy scene, glittering fairy dust swirling around the smiling baby, pastel pink magical light, storybook charm", 54, "zoomin", "twinkle@C,sparkle@L20"),
                new BabyBeat("깨물깨물 세상이 궁금해", "궁금", "playful close-up of the baby curiously chewing a soft toy, first tooth peeking, bright cheerful bokeh, adorable energy", 74, "slideleft", "giggle@C,boing@R25"),
                new BabyBeat("반짝이는 첫 이빨의 축하", "축하", "wide magical shot of the baby beaming under a shimmering pink aurora with floating sparkles, joyful milestone finale", 46, "hblur", "sparkle@C,cute@L20"),
            }),
            new("ms_day200", "baby_milestone", "🎂", "200일 · 혼자 앉기", "혼자 앉기 성공! 🎂", "미니 파티셰의 달콤한 케이크 성", 1.05, new[]
            {
                new BabyBeat("드디어 혼자 앉았어요 대단해", "대단", "cinematic, whimsical pastel cake-castle set, a 200-day baby sitting up proudly as a tiny patissier amid giant frosted cakes and macarons, sweet dreamy light", 30, "fade", "drumroll@C,pop@R25"),
                new BabyBeat("작은 손으로 크림을 콕", "크림", "close-up of the baby poking soft whipped cream with a delighted expression, sprinkles and candy bokeh, warm delicious glow", 54, "zoomin", "cute@C,ding@L20"),
                new BabyBeat("달콤한 성의 꼬마 파티셰", "파티셰", "the baby in a mini chef hat surrounded by a towering cake castle, playful confetti, vibrant candy-colored wonderland", 74, "slideleft", "giggle@C,sparkle@R25"),
                new BabyBeat("이백 일의 달콤한 승리", "승리", "wide joyful shot of the baby sitting atop a plush cake throne with balloons, celebratory sweet-kingdom finale", 46, "hblur", "drumroll@C,pop@R25"),
            }),
            new("ms_day300", "baby_milestone", "🦸", "300일 · 첫 걸음마", "첫 걸음마 위풍당당 아기 슈퍼맨 🦸", "도시 위를 나는 듯한 연출", 1.1, new[]
            {
                new BabyBeat("한 발 두 발 세상을 향해", "한 발", "cinematic, a 300-day baby in an adorable superhero cape taking wobbly first steps, heroic golden backlight, dynamic low angle, triumphant", 32, "fade", "drumroll@C,whoosh@R25"),
                new BabyBeat("작은 영웅이 하늘로 붕", "영웅", "epic shot of the caped baby appearing to soar above a glowing miniature city skyline at golden hour, comic-book heroic energy, cape fluttering", 58, "zoomin", "whoosh@C,ding@L20"),
                new BabyBeat("무엇도 두렵지 않은 발걸음", "발걸음", "determined close-up of the baby's tiny feet stepping forward with sparkles trailing, powerful shallow focus, inspiring", 78, "slideleft", "boing@C,sparkle@R25"),
                new BabyBeat("삼백 일, 세상은 너의 무대", "무대", "wide heroic shot of the baby superhero standing tall on a rooftop as city lights glitter below, victorious skyline finale", 48, "hblur", "drumroll@C,whoosh@R25"),
            }),
            new("ms_firstword", "baby_milestone", "💗", "첫 말문 · 엄마 아빠", "\"엄마 아빠\" 첫 고백 💗", "하트 팝업 · 브로드웨이 뮤지컬 무대", 1.05, new[]
            {
                new BabyBeat("떨리는 입술로 엄마 아빠", "엄마", "cinematic broadway musical stage, a baby center-spotlight speaking the first word as glowing heart pop-ups burst around, warm theatrical light, magical", 30, "fade", "ding@C,sparkle@R25"),
                new BabyBeat("그 한마디에 눈물이 핑", "한마디", "emotional close-up of the baby's mouth forming the word, parents' teary joyful reaction bokeh, tender spotlight, heartfelt", 54, "zoomin", "cute@C,pluck@L20"),
                new BabyBeat("무대 위로 쏟아지는 하트", "하트", "whimsical scene of floating glowing hearts and confetti raining over the delighted baby on stage, musical celebration, vibrant", 74, "slideleft", "pop@C,giggle@R25"),
                new BabyBeat("사랑한다는 첫 노래", "노래", "wide dazzling shot of the baby taking a tiny bow under a heart-shaped spotlight, curtains and sparkles, broadway finale", 46, "hblur", "drumroll@C,sparkle@L20"),
            }),
            new("ms_day365", "baby_milestone", "🎓", "돌잔치 · 돌잡이 전설", "돌잡이 전설 🎓", "마이크·청진기·판사봉을 한 손에 쥔 꼬마 천재", 1.05, new[]
            {
                new BabyBeat("일 년을 자란 우리 꼬마 천재", "천재", "cinematic, festive first-birthday doljanchi stage with rainbow rice-cakes, a 1-year baby in luxurious hanbok before the doljabi items, warm celebratory glow, grand", 30, "fade", "drumroll@C,ding@R25"),
                new BabyBeat("무엇을 잡을까 온 가족 두근", "두근", "close-up of the baby's hand hovering over a microphone, stethoscope, and gavel, suspenseful warm light, everyone watching, cinematic tension", 58, "zoomin", "suspense@C,pop@L20"),
                new BabyBeat("전설의 돌잡이, 다 잡아버렸네", "전설", "joyful shot of the beaming baby triumphantly grabbing mic, stethoscope and gavel all at once, confetti burst, legendary comedic energy", 80, "slideleft", "drumroll@C,pop@R25"),
                new BabyBeat("찬란한 첫 생일, 무한한 미래", "미래", "majestic wide shot of the little genius on a golden birthday throne under balloons and lights, radiant first-birthday finale", 48, "hblur", "sparkle@C,drumroll@R25"),
            }),
            new("ms_day500", "baby_milestone", "🧱", "500일 · 장난감 제국", "500일 장난감 제국의 국왕 🧱", "토이 킹덤 아키텍트 · 블록 성", 1.05, new[]
            {
                new BabyBeat("오백 일, 장난감 제국의 국왕", "국왕", "cinematic, vibrant toy-kingdom set, a 500-day baby ruling atop a colossal building-block castle surrounded by plush toys, playful royal glow, imaginative", 30, "fade", "drumroll@C,pop@R25"),
                new BabyBeat("블록 하나로 성을 쌓는 건축가", "건축가", "close-up of the baby stacking colorful blocks with focused delight, toy bricks glittering, bright cheerful bokeh, creative energy", 54, "zoomin", "pop@C,ding@L20"),
                new BabyBeat("웃음소리가 제국을 채워요", "웃음", "the giggling baby knocking over a tower as blocks and toys tumble playfully, joyful chaos, colorful confetti, adorable", 74, "slideleft", "giggle@C,boing@R25"),
                new BabyBeat("상상이 곧 왕국이 되는 나이", "왕국", "wide whimsical shot of the baby king on a block throne overlooking a sprawling toy metropolis, imaginative kingdom finale", 46, "hblur", "drumroll@C,sparkle@R25"),
            }),
            new("ms_day1000", "baby_milestone", "🎒", "1000일 · 첫 등원길", "1000일 인생 첫 등원길 🎒", "꼬마 스콜라 패셔니스타 슈퍼스타", 1.05, new[]
            {
                new BabyBeat("천 일을 자라 첫 등원하는 날", "천 일", "cinematic, bright morning street, a 1000-day toddler as a tiny scholar fashionista with a mini backpack striking a superstar pose, golden sunrise glow, chic", 30, "fade", "ding@C,sparkle@R25"),
                new BabyBeat("작은 어깨에 멘 큰 꿈", "큰 꿈", "close-up of the toddler adjusting the cute backpack strap with a confident grin, stylish outfit details, warm flattering light, endearing", 54, "zoomin", "cute@C,pluck@L20"),
                new BabyBeat("당당한 발걸음, 카메라 세례", "당당", "the toddler strutting like a runway superstar with sparkles and paparazzi flashes, playful fashion-editorial energy, vibrant", 76, "slideleft", "pop@C,whoosh@R25"),
                new BabyBeat("천 일의 성장, 빛나는 첫걸음", "성장", "wide heartwarming shot of the little scholar waving goodbye at the school gate bathed in radiant morning light, proud milestone finale", 48, "hblur", "drumroll@C,sparkle@L20"),
            }),
        }),
    };
}
