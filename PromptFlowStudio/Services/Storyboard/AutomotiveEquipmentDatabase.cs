using System;
using System.Collections.Generic;
using System.Linq;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>
/// [PromptFlowStudio] 자동차 전문 장비 백과사전(v2.5.0 정밀 재구조화) — 순수 BCL Authored Data(외부 NuGet 0 · <see cref="BabyThemeDatabase"/> 동일 스키마).
///   ▸ 완성차·부품 제조 현장의 실무 검사/시험 장비를 <b>3대 라인업</b>으로 정밀 분류·프롬프트화:
///     ①시트(SEAT) 검사 라인 ②완성차 &amp; ADAS/센서 캘리브레이션 ③친환경·전동화·파워트레인·환경 챔버.
///   ▸ 각 장비 테마는 하이테크 시네마틱 3비트(장비 등장→검사 동작+HUD→PASS 판정) + 프로 나레이션.
///   ▸ 카테고리 키 <c>auto_*</c> → <see cref="VideoFormatFactory"/>가 3D 엠보싱 워터마크 + 텔레메트리 HUD 스타일을 자동 설정.
/// </summary>
public sealed class AutomotiveEquipmentDatabase
{
    /// <summary>UI 탭 순서.</summary>
    public IReadOnlyList<BabyCategory> Categories { get; } = Build();

    private Dictionary<string, BabyThemeDef>? _index;
    private Dictionary<string, BabyThemeDef> Index => _index ??= Categories.SelectMany(c => c.Themes).ToDictionary(t => t.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>전체 장비 테마(평면).</summary>
    public IReadOnlyList<BabyThemeDef> AllThemes => Index.Values.ToList();

    /// <summary>키로 장비 테마 조회(없으면 null).</summary>
    public BabyThemeDef? Get(string? key) => !string.IsNullOrWhiteSpace(key) && Index.TryGetValue(key!, out var t) ? t : null;

    private static BabyBeat B(string v, string g, string shot, int t, string x, string sfx) => new(v, g, shot, t, x, sfx);

    // 장비 1개 = 3비트(등장→검사+HUD→PASS) 공통 아크. 프롬프트만 장비별 고유.
    private static BabyThemeDef Dev(string key, string cat, string emoji, string title, string hook, string example,
        string introShot, string inspectShot, string passShot, string sfx1, string sfx2, string sfx3, double speed = 1.0)
        => new(key, cat, emoji, title, hook, example, speed, new[]
        {
            B("정밀함은 검증에서 시작됩니다", "정밀", introShot, 30, "fade", sfx1),
            B("데이터가 실시간으로 판독됩니다", "판독", inspectShot, 58, "zoomin", sfx2),
            B("완벽한 품질 PASS 를 확인합니다", "PASS", passShot, 82, "slideleft", sfx3),
        });

    private static IReadOnlyList<BabyCategory> Build() => new List<BabyCategory>
    {
        // ═══ ① 시트(SEAT) 검사 라인 ═══
        new("auto_seat", "🪑", "시트 검사 라인", "슬라이드·전장·IMS·PODS·비전·안티핀치 검사", new List<BabyThemeDef>
        {
            Dev("seat_slide", "auto_seat", "↔️", "슬라이드 검사기", "시트 슬라이드 정밀 이송 검사 ↔️", "레일 이송력·소음·스트로크 자동 측정",
                "cinematic 35mm, cool factory light, a car seat mounted on a slide-rail test rig gliding smoothly, glowing force-measurement HUD, industrial precision",
                "macro 100mm, sharp light, an extreme close-up of the slide rail moving with glowing stroke and force graphs overlaid, real-time telemetry, oil-sheened detail",
                "wide clean light, a green PASS indicator illuminating over the seat rig as measurement gauges settle, confident quality-verified reveal",
                "ding@C,suspense@R25", "pop@L25,ding@R25", "sparkle@C,drumroll@R30"),
            Dev("seat_harness", "auto_seat", "🔌", "전장 검사기", "시트 전장 회로 무결점 검사 🔌", "전압·통전·단선 자동 판정",
                "35mm, cool electronics-lab glow, a seat wiring harness on an inspection bench with glowing circuit-node overlays, high-tech testing atmosphere",
                "macro, sharp light, a close-up of connector pins probed as voltage/continuity readouts flash on a HUD panel, glowing signal traces",
                "clean light, a green PASS lighting up across the harness diagram with all circuits verified, flawless electrical integrity reveal",
                "ding@C,suspense@R25", "pop@L25,ding@R25", "sparkle@C,drumroll@R30"),
            Dev("seat_ims", "auto_seat", "🧲", "IMS 검사기", "시트 메모리 시스템(IMS) 검사 🧲", "위치 메모리·모터 구동 정밀 검증",
                "35mm, cool light, a seat memory-system (IMS) test station with motorized seat adjusting to preset positions, glowing position-encoder HUD",
                "macro, sharp glow, a close-up of the memory motor driving with glowing angle/position telemetry graphs tracking each movement, precision detail",
                "clean light, a green PASS confirming perfect memory recall as position gauges lock in, reliable IMS quality reveal",
                "ding@C,boing@R25", "pop@L25,ding@R25", "sparkle@C,drumroll@R30"),
            Dev("seat_pods", "auto_seat", "🎯", "승객감지센서 검사기", "PODS·ODS·SBR 승객감지 검사 🎯", "착좌 하중·안전벨트 리마인더 판정",
                "35mm, cool light, an occupant-detection (PODS/ODS/SBR) test seat with weighted load cells and glowing pressure-map HUD, safety-critical testing",
                "macro, sharp light, a close-up of a pressure-sensor mat glowing with a real-time seated-load heatmap and SBR belt-reminder status, forensic detail",
                "clean light, a green PASS across the occupant-safety panel as detection thresholds verify, life-safety quality reveal",
                "suspense@C,ding@R25", "pop@L25,ding@R25", "sparkle@C,drumroll@R30"),
            Dev("seat_vision", "auto_seat", "👁️", "비전 검사기", "AI 비전 외관·조립 검사 👁️", "머신비전 결함·조립 상태 자동 검출",
                "35mm, cool light, a machine-vision inspection cell scanning a seat with glowing camera laser-lines and bounding-box detections, AI quality-vision",
                "macro, sharp glow, a close-up of the vision system highlighting stitching and clip alignment with glowing detection boxes and confidence scores, precise AI",
                "clean light, a green PASS as the vision panel confirms zero defects across the scanned seat, flawless AI-verified reveal",
                "ding@C,suspense@R25", "pop@L25,whoosh@R25", "sparkle@C,drumroll@R30"),
            Dev("seat_antipinch", "auto_seat", "✋", "안티핀치 검사기", "끼임 방지(Anti-pinch) 안전 검사 ✋", "이물 끼임 감지·역구동 판정",
                "35mm, tense cool light, an anti-pinch test rig with a test object placed in a closing seat mechanism, glowing force-threshold HUD, safety focus",
                "macro, sharp light, a close-up of the mechanism reversing instantly on contact as glowing force curves spike then drop, millisecond safety response",
                "clean light, a green PASS confirming the pinch-protection reversal within spec, dependable safety-mechanism reveal",
                "suspense@C,boing@R25", "boing@L25,pop@R25", "sparkle@C,drumroll@R30"),
            Dev("seat_powerharness", "auto_seat", "⚡", "파워 전장 검사기", "고전류 파워 전장 부하 검사 ⚡", "고전류 통전·발열·부하 자동 측정",
                "35mm, cool electronics glow, a high-current power-harness load tester with glowing current-flow and thermal overlays, heavy-duty electrical testing",
                "macro, sharp light, a close-up of power connectors under load as glowing amperage and temperature readouts climb within safe bands, precise monitoring",
                "clean light, a green PASS across the power-load panel with all thresholds held, robust electrical-endurance reveal",
                "ding@C,suspense@R25", "pop@L25,whoosh@R25", "sparkle@C,drumroll@R30"),
            Dev("seat_backlattice", "auto_seat", "🕸️", "백격자 검사기", "시트백 격자 강성·변형 검사 🕸️", "하중 인가 변형량·복원력 판정",
                "35mm, cool light, a seat-back lattice frame on a load-deflection tester with glowing stress-map overlay, structural quality inspection",
                "macro, sharp glow, a close-up of the lattice under applied load as glowing deformation vectors map the flex and recovery, engineered detail",
                "clean light, a green PASS confirming the back frame holds rigidity within spec, durable structural-quality reveal",
                "ding@C,suspense@R25", "boing@L25,ding@R25", "sparkle@C,drumroll@R30"),
            Dev("seat_headrest", "auto_seat", "💺", "헤드레스트 검사기", "헤드레스트 강도·높이 검사 💺", "인출력·높이 조절·강도 자동 판정",
                "35mm, cool light, a headrest strength-and-height test fixture with the headrest adjusting as glowing force and travel HUD tracks it, safety testing",
                "macro, sharp light, a close-up of the headrest posts under pull-force with glowing load and stroke telemetry graphs, precise measurement",
                "clean light, a green PASS across the headrest-safety panel with strength verified, occupant-safety quality reveal",
                "ding@C,boing@R25", "pop@L25,ding@R25", "sparkle@C,drumroll@R30"),
        }),

        // ═══ ② 완성차 & ADAS/센서 캘리브레이션 ═══
        new("auto_finish", "🚘", "완성차·캘리브레이션", "가진기·트랜스퍼·NCA·에이머·AVM/BSD/HUD/SCC 캘리브레이션", new List<BabyThemeDef>
        {
            Dev("fin_exciter", "auto_finish", "📳", "가진기(Exciter)", "차체 진동 내구 가진 시험 📳", "다축 진동·공진·내구 자동 측정",
                "cinematic 28mm, dramatic industrial light, a full vehicle on a multi-axis shaker (exciter) vibrating under test, glowing frequency-sweep HUD, powerful durability testing",
                "macro, sharp light, a close-up of a shaker actuator driving with glowing acceleration and resonance-spectrum graphs, high-energy precision",
                "wide light, a green PASS as the vibration-endurance panel confirms no failure, rugged durability-verified reveal",
                "drumroll@C,suspense@R25", "boing@L25,whoosh@R25", "sparkle@C,drumroll@R30", 1.15),
            Dev("fin_transfer", "auto_finish", "🦾", "트랜스퍼(Transfer)", "부품 자동 이송 트랜스퍼 🦾", "정밀 로봇 이송·위치 반복정도 검증",
                "28mm wide, cool factory glow, a robotic transfer system precisely moving a part between stations with glowing path-tracking overlays, automated precision",
                "35mm, sharp light, a close-up of the transfer gripper placing a part with glowing position-repeatability readouts locking in, exact motion",
                "wide light, a green PASS confirming flawless transfer accuracy across cycles, reliable automation reveal",
                "whoosh@C,ding@R25", "pop@L25,ding@R25", "sparkle@C,drumroll@R30", 1.15),
            Dev("fin_nca", "auto_finish", "🔧", "NCA", "완성차 NCA 종합 검사 🔧", "차량 종합 기능·정합 자동 검증",
                "24mm wide, cool assembly-line light, a finished vehicle entering an NCA final-inspection gate with glowing multi-system status HUD, comprehensive check",
                "35mm, sharp glow, a close-up of diagnostic connectors reading all vehicle systems as glowing OK indicators cascade across the panel, thorough validation",
                "wide light, a green PASS across the whole-vehicle inspection board, complete quality-assured reveal",
                "ding@C,suspense@R25", "pop@L25,ding@R25", "sparkle@C,drumroll@R30"),
            Dev("fin_wabot", "auto_finish", "🤖", "Wabot", "완성차 자동 검사 로봇 Wabot 🤖", "무인 로봇 자동 정밀 검사",
                "24mm wide, dramatic light, an autonomous inspection robot (Wabot) moving around a finished car scanning panels with glowing laser sweeps, futuristic automation",
                "35mm, sharp glow, a close-up of the robot arm probing a gap/flush point as glowing measurement values appear, precise robotic inspection",
                "wide light, a green PASS as Wabot completes its autonomous scan with all points in spec, next-gen quality reveal",
                "whoosh@C,ding@R25", "pop@L25,whoosh@R25", "sparkle@C,drumroll@R30"),
            Dev("fin_headlamp", "auto_finish", "💡", "헤드램프 에이머", "헤드램프 광축 정렬(에이머) 💡", "광축·광도·컷오프 자동 정렬",
                "35mm, dark aiming-lab glow, a headlamp aimer aligning a car's headlight beam onto a glowing calibration screen, luminous cut-off line, precise optics",
                "macro, sharp light, a close-up of the beam pattern on the aimer screen as glowing angle and intensity readouts fine-tune the aim, exact calibration",
                "clean light, a green PASS as the headlamp axis locks perfectly to spec, precise lighting-alignment reveal",
                "ding@C,suspense@R25", "pop@L25,ding@R25", "sparkle@C,drumroll@R30"),
            Dev("fin_avm", "auto_finish", "🅿️", "AVM Calibration", "어라운드뷰(AVM) 캘리브레이션 🅿️", "4채널 카메라 스티칭 정합 보정",
                "28mm wide, cool light, a car centered on an AVM calibration mat with checkerboard targets, glowing 360-surround stitching overlay forming, camera precision",
                "35mm, sharp glow, a close-up of the surround-view stitch seams aligning perfectly as glowing calibration markers lock, seamless image fusion",
                "wide light, a green PASS as the around-view image stitches flawlessly, precise camera-calibration reveal",
                "ding@C,whoosh@R25", "pop@L25,ding@R25", "sparkle@C,drumroll@R30"),
            Dev("fin_bsd", "auto_finish", "🚦", "BSD Calibration", "후측방 경보(BSD) 캘리브레이션 🚦", "레이더 감지각·거리 정밀 보정",
                "28mm wide, cool light, a car with rear-corner radar being calibrated against a glowing angular target array, detection-cone HUD, safety sensor tuning",
                "35mm, sharp glow, a close-up of the radar detection cone aligning to the target as glowing angle/range values converge, precise sensor calibration",
                "wide light, a green PASS as the blind-spot detection zone locks to spec, dependable BSD-calibration reveal",
                "suspense@C,ding@R25", "pop@L25,ding@R25", "sparkle@C,drumroll@R30"),
            Dev("fin_hud", "auto_finish", "🪟", "HUD Calibration", "헤드업 디스플레이(HUD) 캘리브레이션 🪟", "투영 위치·왜곡·휘도 보정",
                "35mm, dark cockpit glow, a windshield HUD projecting a glowing test grid being calibrated for position and distortion, luminous augmented display",
                "macro, sharp light, a close-up of the projected HUD image aligning as glowing distortion-correction and brightness values tune it, precise optics",
                "clean light, a green PASS as the HUD projection locks perfectly to the driver's eyebox, flawless display-calibration reveal",
                "ding@C,sparkle@R25", "pop@L25,ding@R25", "sparkle@C,drumroll@R30"),
            Dev("fin_scc", "auto_finish", "📡", "SCC/FCA/AEB Calibration", "지능형 주행(SCC/FCA/AEB) 캘리브레이션 📡", "전방 레이더·카메라 정합 보정",
                "28mm wide, cool light, a car's front radar and camera being calibrated to a glowing forward target board, detection-cone overlay, ADAS precision",
                "35mm, sharp glow, a close-up of the forward-sensor alignment as glowing distance/angle readouts converge onto the target, exact calibration",
                "wide light, a green PASS as the smart-cruise and emergency-braking sensors lock to spec, safety-critical calibration reveal",
                "suspense@C,ding@R25", "pop@L25,whoosh@R25", "sparkle@C,drumroll@R30"),
            Dev("fin_dvt", "auto_finish", "📋", "DVT", "설계 검증 시험(DVT) 📋", "설계 규격 종합 검증 시험",
                "35mm, cool engineering-lab light, a prototype vehicle under design-verification testing with glowing multi-parameter dashboards, rigorous R&D validation",
                "macro, sharp glow, a close-up of test instruments logging data as glowing spec-vs-actual graphs track compliance, meticulous verification",
                "wide light, a green PASS as the design-verification suite confirms all specifications met, engineering-validated reveal",
                "ding@C,drumroll@R25", "pop@L25,ding@R25", "sparkle@C,drumroll@R30"),
            Dev("fin_bsr", "auto_finish", "🔇", "BSR", "이음·잡음(BSR) 시험 🔇", "Buzz·Squeak·Rattle 소음 검출",
                "35mm, dark quiet-lab light, a vehicle interior under BSR (buzz-squeak-rattle) testing with glowing acoustic waveform overlays, sound-quality inspection",
                "macro, sharp glow, a close-up of a microphone array capturing cabin noise as glowing spectrum graphs isolate rattle frequencies, precise acoustics",
                "clean light, a green PASS as the BSR panel confirms a silent, refined cabin, premium sound-quality reveal",
                "suspense@C,ding@R25", "pop@L25,ding@R25", "sparkle@C,drumroll@R30"),
        }),

        // ═══ ③ 친환경·전동화·파워트레인·환경 챔버 ═══
        new("auto_ev", "🔋", "전동화·다이나모·챔버", "수소·배터리·다이나모·고저온/고도 환경 챔버", new List<BabyThemeDef>
        {
            Dev("ev_fuelcell", "auto_ev", "💧", "수소연료전지 시험장비", "수소연료전지(FC) 성능 시험 💧", "전류밀도·효율·수분 관리 측정",
                "cinematic 28mm, cool clean-energy lab glow, a hydrogen fuel-cell test stand with glowing gas-flow and current-density HUD, futuristic green-energy testing",
                "macro, sharp light, a close-up of a fuel-cell stack under load as glowing efficiency and humidity curves track performance, precise electrochemistry",
                "wide light, a green PASS as the fuel-cell performance panel confirms peak efficiency, clean-energy quality reveal",
                "ding@C,whoosh@R25", "pop@L25,ding@R25", "sparkle@C,drumroll@R30"),
            Dev("ev_electrolysis", "auto_ev", "⚗️", "수전해 시험장비", "그린수소 수전해 시험 ⚗️", "수전해 셀 효율·가스 순도 측정",
                "28mm, cool lab glow, a water-electrolysis test rig producing hydrogen with glowing gas-bubble and voltage-efficiency overlays, green-hydrogen innovation",
                "macro, sharp light, a close-up of the electrolysis cell as glowing purity and current-efficiency readouts track green-hydrogen output, precise measurement",
                "wide light, a green PASS as the electrolyzer confirms high-purity hydrogen output, sustainable-tech quality reveal",
                "ding@C,pop@R25", "pop@L25,ding@R25", "sparkle@C,drumroll@R30"),
            Dev("ev_stack", "auto_ev", "🧱", "스택 조립 솔루션", "연료전지 스택 정밀 조립 🧱", "셀 적층·체결력·기밀 자동 관리",
                "28mm wide, cool light, an automated fuel-cell stack assembly line precisely layering cells with glowing torque and alignment overlays, high-tech assembly",
                "macro, sharp glow, a close-up of stack cells compressing with glowing clamping-force and sealing telemetry, meticulous precision",
                "wide light, a green PASS as the assembled stack confirms perfect sealing and torque, flawless assembly reveal",
                "whoosh@C,ding@R25", "pop@L25,ding@R25", "sparkle@C,drumroll@R30"),
            Dev("ev_battery", "auto_ev", "🔋", "배터리 충·방전 시험기", "EV 배터리 충·방전 사이클 시험 🔋", "충방전 효율·발열·수명 측정",
                "28mm, cool light, an EV battery cycler charging and discharging cells with glowing state-of-charge and thermal HUD, high-power battery testing",
                "macro, sharp glow, a close-up of battery modules under cycling as glowing capacity and temperature curves track health, precise monitoring",
                "wide light, a green PASS as the battery endurance panel confirms cycle-life within spec, reliable energy-storage reveal",
                "ding@C,suspense@R25", "pop@L25,ding@R25", "sparkle@C,drumroll@R30"),
            Dev("ev_charger", "auto_ev", "🔌", "전기차 충전기 검사기", "EV 충전기 안전·성능 검사 🔌", "충전 전력·프로토콜·안전 판정",
                "28mm, cool light, an EV charger under test connected to a load bank with glowing power-delivery and protocol HUD, charging-safety inspection",
                "macro, sharp glow, a close-up of the charge connector as glowing voltage/current and handshake-protocol status verify, precise validation",
                "wide light, a green PASS as the charger confirms safe full-power delivery, dependable charging quality reveal",
                "ding@C,pop@R25", "pop@L25,ding@R25", "sparkle@C,drumroll@R30"),
            Dev("ev_enginedyno", "auto_ev", "🔩", "엔진 다이나모", "엔진 출력·토크 다이나모 시험 🔩", "출력·토크·연소 효율 측정",
                "cinematic 28mm, dramatic engine-cell light, an engine on a dynamometer roaring under load with glowing power-and-torque curve HUD, high-energy testing",
                "macro, sharp light, a close-up of the dyno coupling spinning as glowing RPM and torque telemetry climb, powerful precision",
                "wide light, a green PASS as the engine output panel confirms rated power, proven-performance reveal",
                "drumroll@C,suspense@R25", "boing@L25,whoosh@R25", "sparkle@C,drumroll@R30", 1.15),
            Dev("ev_ptdyno", "auto_ev", "⚙️", "파워트레인 다이나모", "파워트레인 통합 다이나모 시험 ⚙️", "구동계 통합 효율·응답 측정",
                "28mm, dramatic light, a full powertrain on an integrated dynamometer under load with glowing driveline efficiency HUD, comprehensive drivetrain testing",
                "macro, sharp glow, a close-up of the driveshaft spinning as glowing torque-transfer and response-time graphs track performance, precise detail",
                "wide light, a green PASS as the powertrain efficiency panel confirms optimal response, integrated-performance reveal",
                "drumroll@C,suspense@R25", "boing@L25,whoosh@R25", "sparkle@C,drumroll@R30", 1.15),
            Dev("ev_motordyno", "auto_ev", "🌀", "모터 다이나모", "구동 모터 다이나모 시험 🌀", "모터 효율·토크맵·발열 측정",
                "28mm, cool light, an EV drive motor on a dynamometer spinning under load with glowing efficiency-map and torque HUD, electric-powertrain testing",
                "macro, sharp glow, a close-up of the motor coupling as glowing RPM/torque and thermal telemetry map the efficiency, precise measurement",
                "wide light, a green PASS as the motor efficiency panel confirms peak performance, high-efficiency drive reveal",
                "whoosh@C,ding@R25", "boing@L25,ding@R25", "sparkle@C,drumroll@R30", 1.15),
            Dev("ev_dudyno", "auto_ev", "🧩", "DU 다이나모", "구동 유닛(DU) 통합 다이나모 🧩", "감속기+모터 통합 효율 측정",
                "28mm, cool light, an integrated drive-unit (motor + reducer) on a dynamometer under load with glowing combined-efficiency HUD, e-axle testing",
                "macro, sharp glow, a close-up of the drive unit as glowing gear-mesh and efficiency telemetry track the integrated performance, precise detail",
                "wide light, a green PASS as the drive-unit panel confirms integrated efficiency within spec, e-mobility quality reveal",
                "whoosh@C,ding@R25", "pop@L25,ding@R25", "sparkle@C,drumroll@R30", 1.15),
            Dev("ev_climate", "auto_ev", "🌡️", "실차 고저온 챔버", "실차 고저온 환경 시험 챔버 🌡️", "극한 온도 실차 성능 검증",
                "24mm wide, dramatic chamber glow, a whole car inside a climate chamber under extreme heat/cold with frost or heat-haze and glowing temperature HUD, harsh-environment testing",
                "35mm, sharp glow, a close-up of the car surface frosting/steaming as glowing temperature and system-status telemetry track it, environmental precision",
                "wide light, a green PASS as the vehicle confirms full function across temperature extremes, all-climate reliability reveal",
                "suspense@C,ding@R25", "whoosh@L25,ding@R25", "sparkle@C,drumroll@R30"),
            Dev("ev_altitude", "auto_ev", "⛰️", "고도 재현 환경 챔버", "고도 재현 저기압 환경 챔버 ⛰️", "고고도 저기압 성능 재현 시험",
                "24mm wide, eerie chamber glow, a vehicle in an altitude-simulation chamber under low pressure with glowing barometric and system HUD, extreme-condition testing",
                "35mm, sharp glow, a close-up of pressure gauges dropping as glowing altitude and engine-response telemetry adapt, precise simulation",
                "wide light, a green PASS as the vehicle confirms performance at simulated high altitude, extreme-environment reveal",
                "suspense@C,ding@R25", "whoosh@L25,ding@R25", "sparkle@C,drumroll@R30"),
            Dev("ev_valvebody", "auto_ev", "🧯", "Valve Body Tester", "변속기 밸브바디 시험기 🧯", "유압 회로·솔레노이드 정밀 검사",
                "35mm, cool hydraulics-lab glow, a transmission valve-body on a hydraulic tester with glowing oil-pressure circuit overlays, precise fluid-control inspection",
                "macro, sharp light, a close-up of solenoids actuating as glowing pressure-response curves verify each hydraulic channel, meticulous detail",
                "clean light, a green PASS as the valve-body panel confirms perfect hydraulic control, precise transmission-quality reveal",
                "ding@C,suspense@R25", "pop@L25,ding@R25", "sparkle@C,drumroll@R30"),
            Dev("ev_final", "auto_ev", "🏁", "Final Tester", "구동/변속 최종 검사(Final Tester) 🏁", "완성 유닛 종합 최종 판정",
                "28mm wide, dramatic light, a completed drive/transmission unit on a final test bench with glowing comprehensive status HUD, end-of-line validation",
                "macro, sharp glow, a close-up of the unit under final test as glowing multi-parameter checks cascade to OK, thorough verification",
                "wide light, a green PASS across the final-test board confirming ship-ready quality, end-of-line assurance reveal",
                "drumroll@C,ding@R25", "pop@L25,ding@R25", "sparkle@C,drumroll@R30"),
        }),
    };
}
