using System;
using System.Collections.Generic;
using System.Linq;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>
/// [PromptFlowStudio] B2B 기업/장비 홍보 테마 백과사전 — 순수 BCL Authored Data(외부 NuGet 0 · <see cref="BabyThemeDatabase"/>와 동일 스키마 재사용).
///   ▸ 아기 테마와 <b>전환 가능한</b> [🏢 B2B 기업/장비 홍보] 탭: 기업 브랜딩 · 중장비/산업현장 · 스마트팩토리/IT · 제품/디바이스.
///   ▸ <see cref="BabyThemeDef"/>/<see cref="BabyCategory"/> 타입을 그대로 써 <see cref="ExpandedThemeGallery"/>·<see cref="VideoFormatFactory"/>가 코드 변경 없이 재사용.
///   ▸ 각 테마는 시네마틱 4-비트(도입→디테일→임팩트→클로징) — 프로 나레이션 + 하이테크 연출 프롬프트(아기 페르소나 아님).
/// </summary>
public sealed class CorporateThemeDatabase
{
    /// <summary>UI 탭 순서.</summary>
    public IReadOnlyList<BabyCategory> Categories { get; } = Build();

    private Dictionary<string, BabyThemeDef>? _index;
    private Dictionary<string, BabyThemeDef> Index => _index ??= Categories.SelectMany(c => c.Themes).ToDictionary(t => t.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>전체 테마(평면).</summary>
    public IReadOnlyList<BabyThemeDef> AllThemes => Index.Values.ToList();

    /// <summary>키로 테마 조회(없으면 null).</summary>
    public BabyThemeDef? Get(string? key) => !string.IsNullOrWhiteSpace(key) && Index.TryGetValue(key!, out var t) ? t : null;

    private static BabyBeat B(string v, string g, string shot, int t, string x, string sfx) => new(v, g, shot, t, x, sfx);

    private static IReadOnlyList<BabyCategory> Build() => new List<BabyCategory>
    {
        // ═══ B1. 기업 브랜딩 ═══
        new("corp_brand","🏢","기업 브랜딩","비전·파트너십·CEO 메세지 시네마틱", new List<BabyThemeDef>{
          new("vision","corp_brand","🎯","신뢰의 비전 시네마틱","브랜드 신뢰를 담은 오프닝 🎯","기업의 비전과 신뢰를 그리는 브랜드 필름 (톤: 웅장·신뢰)",1.0, new[]{
            B("신뢰는 하루아침에 만들어지지 않습니다","신뢰","Cinematic 35mm anamorphic, dawn golden light, a sweeping aerial establishing shot of a modern glass headquarters, volumetric sun rays, corporate grandeur",30,"fade","drumroll@C,sparkle@R25"),
            B("우리는 매 순간 최선을 다합니다","최선","50mm lens, clean rim light, a slow dolly across focused professionals collaborating in a bright modern office, shallow depth, premium tone",50,"zoomin","ding@C,whoosh@R25"),
            B("함께라면 더 멀리 나아갑니다","함께","24mm wide, dramatic backlight, a low-angle hero shot of a diverse team walking forward in unity, lens flare, inspiring momentum",70,"slideleft","whoosh@C,sparkle@R25"),
            B("당신의 신뢰에 보답하겠습니다","보답","Cinematic wide, warm golden hour, a majestic pull-back revealing the company logo glowing on the building at dusk, confident brand finale",60,"hblur","sparkle@C,ding@R30") }),
          new("partnership","corp_brand","🤝","글로벌 파트너십 하이라이트","전 세계를 잇는 파트너십 🌐","글로벌 협력과 확장을 보여주는 하이라이트 (톤: 역동·글로벌)",1.0, new[]{
            B("세계는 하나로 연결됩니다","연결","24mm wide, cool blue tech glow, a rotating 3D globe with glowing connection arcs between cities, data particles, sleek futuristic tone",34,"fade","suspense@C,ding@R25"),
            B("국경을 넘어 함께 성장합니다","성장","35mm lens, bright corporate light, a fast montage of international handshakes and city skylines, dynamic push-ins, energetic pace",55,"zoomin","whoosh@L25,pop@R25"),
            B("혁신은 협력에서 시작됩니다","협력","50mm lens, clean studio light, a sweeping shot of a global team on a video wall of live feeds, glowing network lines, unified vision",72,"slideleft","ding@C,sparkle@R25"),
            B("더 큰 미래를 함께 그립니다","미래","Cinematic wide, radiant sunrise, a triumphant aerial of connected global landmarks lighting up in sequence, inspiring worldwide finale",62,"hblur","sparkle@C,drumroll@R30") }),
          new("ceomessage","corp_brand","🎙️","CEO 메세지 리포트","리더의 비전을 전하는 메세지 🎙️","신뢰감 있는 CEO 메세지 리포트 (톤: 진중·리더십)",1.0, new[]{
            B("변화의 시대 우리의 답은 명확합니다","명확","85mm lens, soft executive key light, a dignified medium shot of a leader addressing the camera in a modern office, bokeh city view, gravitas",30,"fade","drumroll@C,ding@R25"),
            B("우리는 고객 가치를 최우선합니다","가치","50mm lens, clean light, an elegant dolly past infographic holograms of growth charts, crisp data visualization, confident clarity",50,"zoomin","ding@L25,pop@R25"),
            B("도전을 두려워하지 않습니다","도전","35mm lens, dramatic contrast, a bold low-angle of the leadership team overlooking a city skyline at dusk, determined resolve",70,"slideleft","whoosh@C,suspense@R25"),
            B("우리의 약속을 지켜가겠습니다","약속","Cinematic wide, warm golden light, a sincere close-up of the leader with the brand promise text emerging, trustworthy report finale",58,"hblur","sparkle@C,ding@R30") }),
        }),

        // ═══ B2. 중장비 · 산업 현장 ═══
        new("corp_heavy","🏗️","중장비·산업현장","굴착기·토목·건설 파워 액션", new List<BabyThemeDef>{
          new("excavator","corp_heavy","🚜","굴착기 하이테크 액션","압도적인 굴착기 파워 시연 🚜","중장비의 정밀함과 힘을 담은 액션 (톤: 파워·하이테크)",1.15, new[]{
            B("압도적인 힘이 현장을 지배합니다","힘","24mm wide low-angle, dramatic industrial light, a massive hydraulic excavator rearing its arm against a dusty sky, raw power, backlit haze",34,"fade","drumroll@C,suspense@R25"),
            B("밀리미터 단위의 정밀 제어","정밀","50mm lens, hard side light, an extreme close-up of a hydraulic arm joint moving with precise oil-sheened articulation, mechanical detail",55,"zoomin","ding@C,pop@R25"),
            B("거대한 토사를 단숨에 들어올립니다","단숨에","24mm wide, dynamic dust flare, the excavator bucket scooping and lifting a huge load of earth in one powerful sweep, debris flying",82,"slideleft","crash@C,whoosh@L30,boing@R25"),
            B("현장의 신뢰 그 이름값을 합니다","신뢰","Cinematic wide, golden dusk, a heroic silhouette of the excavator against a completed worksite, dust settling, powerful brand finale",60,"hblur","sparkle@C,drumroll@R30") }),
          new("crane","corp_heavy","🏗️","초고층 빌딩 건설","하늘을 향한 크레인의 위엄 🏗️","초고층 건설 현장의 압도적 스케일 (톤: 스케일·위엄)",1.0, new[]{
            B("하늘을 향해 도시가 자랍니다","자랍니다","14mm ultra-wide, epic blue-sky light, a towering tower-crane lifting a steel beam high above a rising skyscraper, vast vertical scale",34,"fade","suspense@C,drumroll@R25"),
            B("정교한 계획이 현실이 됩니다","현실","35mm lens, crisp daylight, a time-lapse feel of steel beams locking precisely into place, sparks and precision, engineering mastery",55,"zoomin","ding@L25,pop@R25"),
            B("거대한 구조물이 완성됩니다","완성","24mm wide, dramatic sunset, a sweeping crane shot revealing the near-complete skyscraper glowing against the city, awe-inspiring reveal",80,"slideleft","whoosh@C,crash@L30,sparkle@R25"),
            B("우리가 도시의 스카이라인을 만듭니다","스카이라인","Cinematic aerial, golden hour, a majestic flyover of the finished tower crowning the skyline, triumphant construction finale",62,"hblur","sparkle@C,drumroll@R30") }),
          new("civil","corp_heavy","🛣️","압도적인 토목 공사 시연","대규모 토목의 웅장한 시연 🛣️","도로·교량 대규모 토목 시연 (톤: 웅장·대규모)",1.15, new[]{
            B("불가능을 가능으로 바꿉니다","불가능","24mm wide, harsh industrial sun, a vast panorama of a mega civil-engineering site with fleets of heavy machinery, dust and scale, epic breadth",34,"fade","drumroll@C,suspense@R25"),
            B("치밀한 설계가 대지를 다집니다","다집니다","35mm lens, dramatic side light, a powerful shot of a road roller and pavers laying a highway in perfect formation, precise coordination",55,"zoomin","whoosh@L25,ding@R25"),
            B("거대한 교량이 강을 가로지릅니다","가로","14mm ultra-wide, cinematic dusk, a sweeping reveal of a massive new bridge spanning a river, headlights streaming, monumental achievement",80,"slideleft","whoosh@C,crash@L30,sparkle@R25"),
            B("우리는 미래의 길을 놓습니다","길","Cinematic aerial, golden light, a majestic flyover of the completed infrastructure network glowing at dusk, grand civil-works finale",60,"hblur","sparkle@C,drumroll@R30") }),
        }),

        // ═══ B3. 스마트 팩토리 · IT ═══
        new("corp_smart","🏭","스마트팩토리·IT","반도체·AI 자동화·솔루션 데모", new List<BabyThemeDef>{
          new("cleanroom","corp_smart","🔬","첨단 반도체 클린룸 로봇","무결점 반도체 클린룸 공정 🔬","초정밀 반도체 클린룸의 미래 공정 (톤: 첨단·무결점)",1.0, new[]{
            B("나노미터의 세계에 오신 것을 환영합니다","나노","Macro 100mm, sterile cyan clean-room glow, an extreme close-up of a silicon wafer with rainbow diffraction, robotic arms in soft focus, futuristic purity",32,"fade","suspense@C,ding@R25"),
            B("무결점을 향한 초정밀 공정","초정밀","50mm lens, crisp tech light, a smooth dolly along robotic arms placing chips with flawless precision, glowing status LEDs, immaculate detail",52,"zoomin","ding@L25,pop@R25"),
            B("데이터가 실시간으로 흐릅니다","실시간","35mm lens, blue data glow, a sweeping shot of holographic process data overlaying the automated line, flowing particles, high-tech oversight",72,"slideleft","whoosh@C,sparkle@R25"),
            B("완벽한 품질 그것이 기준입니다","기준","Cinematic wide, clean radiant light, a majestic reveal of the entire automated fab glowing in sterile blue, precision-tech finale",58,"hblur","sparkle@C,drumroll@R30") }),
          new("automation","corp_smart","🤖","AI 자동화 공정 레이저 오버레이","AI가 지휘하는 자동화 라인 🤖","레이저 오버레이 AI 자동화 공정 (톤: 지능·역동)",1.15, new[]{
            B("생각하는 공장이 가동됩니다","생각","24mm wide, moody neon factory glow, a sweeping shot of robotic arms working in synchrony with glowing AR laser overlays tracking each motion",34,"fade","suspense@C,whoosh@R25"),
            B("인공지능이 모든 흐름을 최적화합니다","최적화","35mm lens, dynamic tech light, fast push-ins on conveyor lines with scanning laser grids and floating AI data annotations, intelligent flow",56,"zoomin","ding@L25,pop@R25"),
            B("한 치의 오차도 없이 스스로 판단합니다","판단","50mm lens, dramatic glow, a close-up of a robotic gripper adjusting in real time as red scan-lines confirm alignment, autonomous precision",78,"slideleft","whoosh@C,ding@L30,sparkle@R25"),
            B("미래의 제조 지금 시작됩니다","미래","Cinematic wide, radiant neon glow, a majestic reveal of the fully autonomous smart factory pulsing with light, futuristic finale",60,"hblur","sparkle@C,drumroll@R30") }),
          new("solution","corp_smart","💡","미래 솔루션 데모","혁신 솔루션을 보여주는 데모 💡","IT 솔루션의 가치를 담은 데모 (톤: 혁신·명료)",1.0, new[]{
            B("복잡함을 단순함으로 바꿉니다","단순","35mm lens, clean minimalist light, an elegant floating UI dashboard materializing in a bright modern space, sleek holographic interface, clarity",32,"fade","ding@C,pop@R25"),
            B("데이터가 인사이트가 됩니다","인사이트","50mm lens, cool glow, a smooth dolly through rising 3D data charts and glowing insight nodes, information becoming clear, premium visualization",52,"zoomin","whoosh@L25,sparkle@R25"),
            B("한 번의 클릭으로 모든 것이 연결됩니다","연결","24mm wide, bright tech light, a dynamic shot of interconnected devices lighting up in a chain reaction across a seamless ecosystem, effortless flow",72,"slideleft","ding@C,pop@R25"),
            B("당신의 비즈니스를 가속합니다","가속","Cinematic wide, radiant glow, a confident reveal of the solution logo with an upward growth graph soaring, innovative solution finale",58,"hblur","sparkle@C,drumroll@R30") }),
        }),

        // ═══ B4. 제품 · 디바이스 ═══
        new("corp_product","📦","제품·디바이스","매크로·턴테이블·분해조립 쇼케이스", new List<BabyThemeDef>{
          new("macro","corp_product","🔎","프리미엄 매크로 렌즈 질감 샷","제품 질감을 극대화한 매크로 🔎","프리미엄 소재의 질감을 담은 매크로 (톤: 고급·정밀)",1.0, new[]{
            B("디테일이 곧 품격입니다","디테일","Extreme macro 100mm probe, dramatic studio rim light, an ultra-close glide over a premium product's brushed-metal texture, glinting micro-detail, luxury sheen",30,"fade","suspense@C,ding@R25"),
            B("소재의 완성도를 느껴보십시오","완성도","Macro 100mm, soft gradient light, a slow reveal of stitched leather and polished glass surfaces, tactile richness, premium material story",50,"zoomin","ding@L25,pop@R25"),
            B("빛이 닿는 모든 각도가 완벽합니다","완벽","Macro 100mm, rotating specular highlights, light sweeping across a curved metallic edge creating a flowing gleam, flawless craftsmanship",70,"slideleft","sparkle@C,whoosh@R25"),
            B("장인의 손끝에서 완성됩니다","장인","Cinematic macro, warm key light, an elegant final beauty shot of the premium product glowing on a dark reflective surface, luxury finale",58,"hblur","sparkle@C,ding@R30") }),
          new("turntable","corp_product","🔄","360도 턴테이블 쇼케이스","완벽한 360도 제품 쇼케이스 🔄","360도 회전 프리미엄 쇼케이스 (톤: 세련·완결)",1.0, new[]{
            B("모든 각도가 자신 있습니다","자신","50mm lens, gradient studio backdrop, a product on a slowly rotating turntable with clean rim lighting, premium showroom feel, smooth spin",30,"fade","ding@C,whoosh@R25"),
            B("빛과 그림자가 실루엣을 그립니다","실루엣","50mm lens, dramatic key-and-fill, the rotating product casting elegant moving shadows, sculpted form, sophisticated lighting design",50,"zoomin","suspense@C,pop@R25"),
            B("작은 디테일까지 놓치지 않습니다","놓치지","Macro insert, crisp light, a close-up push into a precise button and seam as the turntable continues, meticulous product detail",70,"slideleft","ding@C,sparkle@R25"),
            B("완벽한 한 바퀴 제품의 모든 것","모든 것","Cinematic wide, radiant studio glow, the product completing its rotation with the brand name appearing, polished showcase finale",58,"hblur","sparkle@C,drumroll@R30") }),
          new("teardown","corp_product","⚙️","분해 조립 그래픽","정교한 분해 조립 익스플로디드 뷰 ⚙️","제품 내부 구조 익스플로디드 뷰 (톤: 정교·테크)",1.15, new[]{
            B("겉이 아닌 속을 보여드립니다","속","35mm lens, clean tech-blue light, a premium device beginning to separate into a floating exploded view, parts drifting apart, precise engineering",34,"fade","suspense@C,whoosh@R25"),
            B("모든 부품이 완벽히 맞물립니다","맞물","50mm lens, crisp glow, an animated close-up of internal components rotating and aligning with glowing connection lines, intricate assembly",56,"zoomin","ding@L25,pop@R25"),
            B("정교함이 성능을 완성합니다","성능","24mm wide, dynamic light, the exploded parts flowing back together in a satisfying reassembly, mechanical harmony, high-tech precision",78,"slideleft","whoosh@C,ding@L30,sparkle@R25"),
            B("보이지 않는 곳까지 완벽합니다","완벽","Cinematic wide, radiant glow, the fully reassembled product glowing with an internal-schematic overlay fading out, engineering finale",60,"hblur","sparkle@C,drumroll@R30") }),
        }),
    };
}
