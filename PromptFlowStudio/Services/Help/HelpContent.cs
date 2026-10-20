using System.Collections.Generic;
using System.Linq;

namespace PromptFlowStudio.Services.Help;

// =============================================================================
//  화면별 도움말 컨텐츠 — PromptFlow Studio
//
//  ⚠ HelpContent.ForPath(route) 가 @page 라우트와 1:1 로 맞아야 상단바 제목과
//    도움말 드로어가 올바로 뜬다. 매핑이 없으면 드로어 _current=null 이 되어
//    전체 목록의 첫 항목(워크스페이스)이 위로 떠 <다른 화면 도움말>이 열린 것처럼 보인다.
//    현재 실제 라우트는 "", "batch", "analytics", "live-studio", "brand-studio", "node-studio", "user-settings", "settings" 다. 새 화면을 추가하면 여기에도 넣는다.
// =============================================================================

/// <summary>배지 색 톤 — modules.css 의 .b-* 클래스와 1:1.</summary>
public enum HelpTone
{
    /// <summary>정상 · 초록</summary>
    Safe,
    /// <summary>주의 · 주황</summary>
    Warn,
    /// <summary>위험 · 빨강</summary>
    Danger,
    /// <summary>보류 · 회색</summary>
    Unknown,
    /// <summary>정보 · 파랑(바이올렛)</summary>
    Info,
}

public sealed record HelpIndicator(HelpTone Tone, string Label, string Meaning);

public sealed record HelpTopic(
    string Route,
    string Icon,
    string Title,
    string Overview,
    string[] Roles,
    string[] Steps,
    HelpIndicator[] Indicators,
    string[] Protocol,
    string? Caveat = null)
{
    /// <summary>검색용 — 제목·요약·본문을 한 덩어리로.</summary>
    public string SearchBlob { get; } = string.Join(' ',
        new[] { Title, Overview, Caveat ?? "" }
            .Concat(Roles).Concat(Steps).Concat(Protocol)
            .Concat(Indicators.Select(i => i.Label + " " + i.Meaning))).ToLowerInvariant();

    public bool Matches(string q)
        => string.IsNullOrWhiteSpace(q) || SearchBlob.Contains(q.Trim().ToLowerInvariant());
}

public static class HelpContent
{
    /// <summary>현재 URL 경로 → 도움말. 못 찾으면 null. 쿼리·해시·끝슬래시·백슬래시·중복슬래시·대소문자·홈 별칭 정규화.</summary>
    public static HelpTopic? ForPath(string? relativePath)
    {
        var p = Normalize(relativePath);
        return All.FirstOrDefault(t => Normalize(t.Route) == p);
    }

    /// <summary>
    /// 라우트 정규화(강화) — 폴백 오탐을 원천 차단하기 위해 모든 표기 변형을 하나로 모은다.
    ///   ▸ 앞뒤 공백 · 쿼리(?)/해시(#) 제거 · 역슬래시→'/' · 중복 슬래시 축약 · 앞뒤 '/' 제거 · 소문자(Invariant).
    ///   ▸ 홈 별칭("home"/"index") → "" (워크스페이스). ⚠ "workspace"(프롬프트 입력)는 별개 라우트라 건드리지 않는다.
    /// </summary>
    internal static string Normalize(string? path)
    {
        var s = (path ?? "").Trim().Replace('\\', '/');
        var cut = s.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0) s = s[..cut];
        while (s.Contains("//")) s = s.Replace("//", "/");
        s = s.Trim().Trim('/').ToLowerInvariant();
        return s is "home" or "index" ? "" : s;
    }

    /// <summary>이 라우트에 전용 도움말 토픽이 있는지(테스트/오딧용) — false 면 드로어가 폴백한다.</summary>
    public static bool HasTopic(string? route) => ForPath(route) is not null;

    public static readonly IReadOnlyList<HelpTopic> All = new HelpTopic[]
    {
        // ── 워크스페이스(홈) ──────────────────────────────────────────────────
        new(
            Route: "",
            Icon: "🎬",
            Title: "스튜디오 워크스페이스",
            Overview: "프롬프트 입력에서 최종 영상까지, 생성 워크플로우가 한 흐름으로 이어지는 작업 공간입니다. 현재는 파이프라인 골격만 구성되어 있고 오른쪽 작업 영역은 <작업 전> 상태입니다.",
            Roles: new[]
            {
                "크리에이터 — 프롬프트·레퍼런스를 넣고 생성 파이프라인을 돌립니다.",
                "관리자 — 팀 계정·라이선스·메일 발송 설정을 관리합니다(관리자 설정).",
                "권한 — 로그인한 모든 사용자가 워크스페이스를 볼 수 있습니다.",
            },
            Steps: new[]
            {
                "가운데 **워크플로우 흐름도**에서 프롬프트 → 스토리보드 → 모션·카메라 → 렌더 → 영상 단계가 어떻게 이어지는지 확인합니다.",
                "오른쪽 **작업 영역**은 아직 비어 있습니다(작업 전). 생성 기능이 연결되면 이 자리에 결과가 흐릅니다.",
                "왼쪽 사이드바에서 **사용자 설정**·**관리자 설정**으로 이동해 이 PC와 계정 환경을 먼저 갖춥니다.",
            },
            Indicators: new[]
            {
                new HelpIndicator(HelpTone.Info,    "프롬프트 입력",   "생성의 시작점 — 텍스트·이미지 레퍼런스."),
                new HelpIndicator(HelpTone.Info,    "스토리보드",      "장면 구성과 컷을 정리하는 중간 단계."),
                new HelpIndicator(HelpTone.Info,    "모션·카메라",     "움직임과 카메라 워크를 지정하는 단계."),
                new HelpIndicator(HelpTone.Safe,    "최종 영상",       "렌더링을 거쳐 완성된 결과물."),
                new HelpIndicator(HelpTone.Unknown, "작업 전",         "아직 생성 파이프라인이 연결되지 않은 상태입니다."),
            },
            Protocol: new[]
            {
                "생성 기능이 연결되기 전에는 이 화면은 워크플로우 구조를 보여 주는 안내 화면입니다.",
                "계정·라이선스·메일 설정은 지금도 모두 동작합니다 — 관리자 설정에서 구성하십시오.",
            },
            Caveat: "오른쪽 작업 영역이 비어 있는 것은 오류가 아니라 **의도된 <작업 전> 상태**입니다."
        ),

        // ── 프롬프트 입력(단일 생성) ──────────────────────────────────────────
        new(
            Route: "workspace",
            Icon: "✍️",
            Title: "프롬프트 입력 (단일 생성)",
            Overview: "텍스트→영상(T2V) 또는 참조 이미지→영상(I2V)으로 단일 5초 클립을 생성하는 입력 화면입니다.",
            Roles: new[]
            {
                "크리에이터 — 프롬프트/레퍼런스로 한 컷을 빠르게 생성·시험합니다.",
                "권한 — 로그인한 모든 사용자가 사용할 수 있습니다.",
            },
            Steps: new[]
            {
                "**프롬프트**를 입력하고 [⚡ 부스팅]으로 시네마틱 품질 태그를 덧붙입니다.",
                "인물/구도 일관성이 중요하면 참조 이미지를 올려 **I2V**로 생성합니다.",
                "결과가 좋으면 **스토리보드**로 이어 여러 컷으로 확장합니다.",
            },
            Indicators: new[]
            {
                new HelpIndicator(HelpTone.Info, "T2V",        "텍스트만으로 영상 생성."),
                new HelpIndicator(HelpTone.Info, "I2V",        "참조 이미지의 구도·인물 유지."),
                new HelpIndicator(HelpTone.Safe, "부스팅",      "시네마틱 품질 태그 자동 결합."),
                new HelpIndicator(HelpTone.Warn, "엔진 미구성", "Wan API 키가 없으면 생성이 시작되지 않습니다."),
            },
            Protocol: new[]
            {
                "생성이 시작되지 않으면 관리자 설정 › 영상(Wan)에서 API 키를 확인하세요.",
                "이미지 업로드는 PNG/JPG/WebP만 지원합니다.",
            },
            Caveat: "단일 클립은 **5초 고정**입니다(Wan 2.1). 더 긴 영상은 스토리보드에서 여러 컷을 병합하세요."
        ),

        // ── 스토리보드 ────────────────────────────────────────────────────────
        new(
            Route: "storyboard",
            Icon: "🎞️",
            Title: "스토리보드",
            Overview: "여러 컷을 하나의 영상 흐름으로 구성·생성·병합합니다. 아기 테마 원클릭, 8대 플랫폼 세이프존, 무료 결함 자동 보정(Auto-Pilot)을 지원합니다.",
            Roles: new[]
            {
                "크리에이터 — 컷 구성·연출·병합을 담당합니다.",
                "권한 — 로그인한 모든 사용자가 사용할 수 있습니다.",
            },
            Steps: new[]
            {
                "[👶 아기 테마 백과사전]으로 6컷(숏츠)/16컷(롱폼)을 원클릭 구성하거나 컷을 직접 추가합니다.",
                "**세이프존**(8대 플랫폼)·**Auto-Pilot**(무료 결함 보정)·후처리 스위치를 설정합니다.",
                "[✨ 전체 생성] → 완료 시 ffmpeg 자동 병합으로 최종 영상이 만들어집니다.",
            },
            Indicators: new[]
            {
                new HelpIndicator(HelpTone.Info, "아기 테마",   "완성형 6/16컷 원클릭 구성."),
                new HelpIndicator(HelpTone.Info, "세이프존",    "8대 플랫폼 UI 가림 회피 자막/뱃지 배치."),
                new HelpIndicator(HelpTone.Safe, "Auto-Pilot",  "깜빡임·모션·계조 자동 보정(기본 ON)."),
                new HelpIndicator(HelpTone.Warn, "병합 실패",   "ffmpeg.exe가 없으면 병합이 불가합니다."),
            },
            Protocol: new[]
            {
                "병합이 실패하면 ffmpeg.exe가 앱 폴더/PATH에 있는지 확인하세요.",
                "배속이 적용되지 않으면 [⚡ 템포 배속] 체크박스를 확인하세요.",
            },
            Caveat: "Auto-Pilot은 기본 ON이라 무료 생성물이 자동 후처리됩니다(끄면 기존 파이프라인 그대로)."
        ),

        // ── 대량 양산 배치 팩토리 ──────────────────────────────────────────────
        new(
            Route: "batch",
            Icon: "🏭",
            Title: "대량 양산 배치 팩토리",
            Overview: "CSV·엑셀식 그리드로 수십 편의 숏츠를 한 번에 대기열에 올리고, 백그라운드에서 한 편씩 자동으로 양산합니다(대본 분석 → 컷 생성/병합 → 커버 → 메타데이터).",
            Roles: new[]
            {
                "크리에이터 — 제목·대본을 대량 입력해 한 번에 여러 편을 양산합니다.",
                "권한 — 로그인한 모든 사용자가 사용할 수 있습니다(생성 엔진 구성 필요).",
            },
            Steps: new[]
            {
                "**① 대량 입력** — 엑셀식 그리드에 제목·대본을 입력하거나 CSV/JSON 을 붙여넣고 [일괄 큐 등록]을 누릅니다.",
                "**② 공통 양산 옵션** — 해상도·BGM·워터마크·무료 결함 자동 보정(Auto-Pilot) 등 전 항목에 일괄 적용될 옵션을 설정합니다.",
                "**③ 대기열** — [배치 시작]으로 순차/병렬 양산을 시작하면 한 편씩 진행률과 상태가 표시됩니다.",
            },
            Indicators: new[]
            {
                new HelpIndicator(HelpTone.Info, "대량 입력",      "제목·대본 그리드 또는 CSV/JSON — 행별 유효성 실시간 검증."),
                new HelpIndicator(HelpTone.Info, "공통 양산 옵션",  "해상도·BGM·워터마크·무료 결함 자동 보정을 전 항목 일괄 적용."),
                new HelpIndicator(HelpTone.Safe, "대기열",          "백그라운드 순차/병렬 양산 — 실패 항목은 건너뛰고 계속 진행."),
                new HelpIndicator(HelpTone.Warn, "엔진 미구성",     "Wan API 키/엔진이 없으면 양산이 시작되지 않습니다."),
            },
            Protocol: new[]
            {
                "빨간(무효) 행은 등록에서 제외됩니다 — 제목과 대본(2자 이상)이 모두 있어야 유효합니다.",
                "일부 항목이 실패해도 배치는 멈추지 않고 다음 편을 계속 양산합니다(완료 후 실패분만 재시도).",
            },
            Caveat: "CSV 는 **첫 열=제목, 둘째 열=대본** 순서이며, 쉼표가 포함된 셀은 큰따옴표로 감싸세요."
        ),

        // ── v16.18.0 생산성 & 비용 대시보드 ────────────────────────────────────
        new(
            Route: "analytics",
            Icon: "📊",
            Title: "생산성 & 비용 대시보드",
            Overview: "Wan 2.1·Gemini·ElevenLabs 호출 비용과 토큰을 요금표 기반으로 실시간 집계하고, 일간/월간 예산 한도 도달 시 신규 영상 생성을 자동 일시정지합니다. VRAM·RAM·CPU 텔레메트리와 컷당 렌더 시간도 함께 보여 줍니다.",
            Roles: new[]
            {
                "운영자 — 예산 한도를 설정하고 비용·자원 상태를 감시합니다.",
                "크리에이터 — 플랫폼/테마별 생산 비용 효율을 비교합니다.",
            },
            Steps: new[]
            {
                "**① 예산 한도** — 일간/월간 슬라이더로 한도를 정합니다(0 = 무제한).",
                "**② 텔레메트리** — CPU·RAM·VRAM 게이지와 OOM 징후, 컷당 평균 렌더 시간을 확인합니다.",
                "**③ 리포트** — [CSV/JSON 리포트]로 원장과 생산 기록을 내보냅니다(문서 › PromptFlowStudio › Reports).",
            },
            Indicators: new[]
            {
                new HelpIndicator(HelpTone.Safe, "예산 정상",   "한도의 80% 미만."),
                new HelpIndicator(HelpTone.Warn, "예산 80%",    "한도 임박 — 계속 생성은 가능."),
                new HelpIndicator(HelpTone.Warn, "한도 도달",   "신규 Wan 제출 거부 · 배치 큐 자동 일시정지(한도 상향 시 자동 재개)."),
                new HelpIndicator(HelpTone.Warn, "OOM 위험",    "메모리 여유 8% 미만 — 배치 동시성이 자동으로 1 로 줄어듭니다."),
            },
            Protocol: new[]
            {
                "비용은 제출 성공·응답 사용량 기준 추정치입니다 — 실제 청구서와 다를 수 있으니 [요금표]에서 단가를 맞추세요.",
                "진행 중인 작업은 한도 도달 시에도 끝까지 진행되며, 새 작업만 멈춥니다.",
            },
            Caveat: "원장은 %LocalAppData% › PromptFlowStudio › Analytics › cost_ledger.json 에 저장됩니다."
        ),

        // ── v16.25.0 24/7 AI 라이브 스튜디오 ────────────────────────────────────
        new(
            Route: "live-studio",
            Icon: "📺",
            Title: "24/7 AI 라이브 스튜디오",
            Overview: "완성 영상 플레이리스트를 끊김 없이 무한 루프로 YouTube Live·치지직·TikTok·Twitch·RTMP/SRT 에 동시 송출하고, 시청자 채팅을 실시간으로 걸러 AI 아바타가 목소리·표정·자막으로 반응합니다.",
            Roles: new[]
            {
                "운영자 — 송출 대상/키와 플레이리스트를 관리하고 송출 상태를 감시합니다.",
                "크리에이터 — 아바타 페르소나와 응답 방식(Gemini/템플릿·음성)을 정합니다.",
            },
            Steps: new[]
            {
                "**① 송출 대상** — 플랫폼을 체크하고 서버 주소·스트림 키를 입력합니다(키는 저장되지 않음).",
                "**② 플레이리스트** — 영상 파일/폴더를 추가합니다. 모든 영상은 송출 해상도로 정규화되어 하나의 스트림으로 반복됩니다.",
                "**③ 송출 시작** — 비정상 종료 시 자동 재연결(1→60초 백오프)되며 AI 반응도 함께 가동됩니다.",
                "**④ 채팅** — YouTube videoId 로 채팅을 연결하거나 테스트 채팅으로 반응을 확인합니다.",
            },
            Indicators: new[]
            {
                new HelpIndicator(HelpTone.Safe, "● LIVE",     "ffmpeg 송출 중 — fps/kbps/speed 가 1초마다 갱신됩니다."),
                new HelpIndicator(HelpTone.Warn, "재연결 중",  "송출이 끊겨 백오프 후 자동 재시작합니다(연속 20회 실패 시 중단)."),
                new HelpIndicator(HelpTone.Warn, "필터",       "링크·금칙어·반복 문자·중복 채팅은 응답하지 않습니다."),
            },
            Protocol: new[]
            {
                "채팅 연결은 환경변수 PFS_YOUTUBE_API_KEY 가 필요합니다. 치지직/TikTok 채팅은 테스트 입력과 같은 경로(Ingest)로 브리지합니다.",
                "AI 응답 실패/지연(8초) 시 즉시 템플릿 응답으로 대체됩니다.",
            },
            Caveat: "플레이리스트 영상에는 오디오 트랙이 있어야 합니다. 송출 권리가 있는 콘텐츠만 송출하세요."
        ),

        // ── v16.21.0 비주얼 노드 플로우 스튜디오 ──────────────────────────────
        new(
            Route: "node-studio",
            Icon: "🧩",
            Title: "비주얼 노드 플로우 스튜디오",
            Overview: "프롬프트 ➔ ControlNet ➔ Wan 생성기 ➔ 업스케일러/후처리 ➔ 출력 노드를 무한 캔버스에서 배선하고, 같은 단계의 분기를 병렬로 실행합니다. 영상 위에 브러시로 마스크를 그려 키프레임 변환을 주고, 데스크톱 상태를 모바일/웹에 실시간 동기화하거나 원격 GPU 워커로 실행을 넘길 수 있습니다.",
            Roles: new[]
            {
                "크리에이터 — 노드를 조합해 생성·후처리 파이프라인을 설계합니다.",
                "운영자 — 라이브 동기화와 원격 GPU 오프로딩을 관리합니다.",
            },
            Steps: new[]
            {
                "**① 배선** — 팔레트로 노드를 추가하고 출력 점(오른쪽) → 입력 점(왼쪽)을 차례로 클릭해 연결합니다. 빈 곳 드래그 = 이동, 휠 = 확대/축소.",
                "**② 마스크** — 아래 캔버스에서 영상을 불러와 브러시로 영역을 칠하고 [◆ 키프레임 설정]/[🎯 박스 추적]으로 움직임을 줍니다. [💾 마스크 내보내기]하면 ControlNet 노드에 inpaint 마스크로 결선됩니다.",
                "**③ 실행** — [▶ 그래프 실행]. 원격 GPU 가 구성돼 있으면 오프로딩을 켜 클라우드로 실행합니다(실패 시 로컬).",
            },
            Indicators: new[]
            {
                new HelpIndicator(HelpTone.Safe, "완료(초록)",   "노드 실행 성공 — 연결선도 초록으로 바뀝니다."),
                new HelpIndicator(HelpTone.Warn, "건너뜀",       "상위 노드가 실패해 실행하지 않았습니다(다른 분기는 계속)."),
                new HelpIndicator(HelpTone.Info, "라이브 N명",   "접속한 모바일/웹 클라이언트 수 — 토큰 포함 주소로만 접속됩니다."),
            },
            Protocol: new[]
            {
                "ControlNet 노드는 Wan 제출 시 참조 이미지(i2v)와 구조 가이드 문구로 변환됩니다(네이티브 ControlNet 가중치 미지원 백엔드).",
                "업스케일 엔진/ffmpeg 가 없으면 해당 노드는 원본을 그대로 통과시킵니다.",
            },
            Caveat: "라이브 동기화는 기본 localhost 바인딩입니다. 원격 GPU 는 환경변수 PFS_GPU_WORKER_ENDPOINT / PFS_GPU_WORKER_KEY 로 구성합니다."
        ),

        // ── v16.19.0 커머셜 브랜드 스튜디오 ─────────────────────────────────
        new(
            Route: "brand-studio",
            Icon: "🛍️",
            Title: "커머셜 브랜드 스튜디오",
            Overview: "아동용품·브랜드 협찬 제품 사진을 자동 누끼(알파 마스크)와 장면 조명 매칭으로 테마 영상에 합성하고, 로고 워터마크·PPL 고지·CTA 를 세이프존에 배치해 광고 비디오를 1-Click 으로 출력합니다. Gemini 로 15초/30초 CM 카피·징글·BGM 페어링도 생성합니다.",
            Roles: new[]
            {
                "마케터 — 협찬 캠페인의 카피와 CTA 를 구성합니다.",
                "크리에이터 — 테마 장면에 제품을 자연스럽게 배치해 광고 영상을 양산합니다.",
            },
            Steps: new[]
            {
                "**① 자산** — 제품 사진·로고·테마 장면 영상(MP4)을 올리고 테마를 고르면 조명·배치가 추천됩니다.",
                "**② 카피** — 제품명·특징을 입력하고 [CM 카피 · 징글 생성]으로 대본과 BGM 페어링을 만듭니다.",
                "**③ 출력** — 해상도·배치·CTA 를 정하고 [1-Click 광고 비디오 출력]을 누릅니다(미디어 폴더 › _brand).",
            },
            Indicators: new[]
            {
                new HelpIndicator(HelpTone.Safe, "배경 키잉",     "흰/단색 배경 사진은 테두리와 연결된 배경만 제거 — 흰 제품 내부는 보존됩니다."),
                new HelpIndicator(HelpTone.Info, "조명 매칭",     "장면 프레임(없으면 테마 조명 프로필) 색감에 맞춰 제품 색온도·밝기를 보정합니다."),
                new HelpIndicator(HelpTone.Warn, "표시광고 점검", "최고·100%·치료 등 표현은 실증 없으면 위반 소지 — 점검 메모를 확인하세요."),
            },
            Protocol: new[]
            {
                "협찬/PPL 영상은 「유료 광고 포함」 고지를 유지하세요(기본 켜짐).",
                "일부 장면 합성이 실패해도 나머지 장면은 계속 출력됩니다.",
            },
            Caveat: "투명 PNG 제품 사진이면 원본 알파를 그대로 사용합니다. 배경과 제품 색이 비슷하면 누끼 품질이 떨어질 수 있습니다."
        ),

        // ── 모션·카메라 ───────────────────────────────────────────────────────
        new(
            Route: "motion",
            Icon: "🎥",
            Title: "모션·카메라",
            Overview: "Wan 2.1 카메라 워크(Pan/Tilt/Zoom/Orbit/Roll/Truck)와 스타일·모션 프리셋을 조합해 연출을 강화합니다.",
            Roles: new[]
            {
                "크리에이터 — 카메라 워크·모션 프리셋을 프롬프트에 반영합니다.",
                "권한 — 로그인한 모든 사용자가 사용할 수 있습니다.",
            },
            Steps: new[]
            {
                "비주얼라이저에서 카메라 워크의 움직임을 미리 확인합니다.",
                "[＋ 프롬프트에 추가]로 카메라 키워드를 프롬프트에 반영합니다.",
                "워크스페이스/스토리보드로 돌아가 생성합니다.",
            },
            Indicators: new[]
            {
                new HelpIndicator(HelpTone.Info, "오빗",     "인물/제품 소개에 적합."),
                new HelpIndicator(HelpTone.Info, "트럭·팬",  "넓은 공간 소개에 적합."),
                new HelpIndicator(HelpTone.Warn, "과한 이동", "카메라 이동이 과하면 모션 왜곡이 생깁니다."),
            },
            Protocol: new[]
            {
                "카메라 키워드는 프롬프트 앞부분에 배치하면 더 잘 반영됩니다.",
                "모션이 뭉개지면 배속을 낮추거나 GPU 60fps 보간(Auto-Pilot)을 켜세요.",
            },
            Caveat: "하나의 카메라 워크만 강조하는 것이 왜곡 없이 가장 안전합니다."
        ),

        // ── 렌더링 큐 ─────────────────────────────────────────────────────────
        new(
            Route: "queue",
            Icon: "⏳",
            Title: "렌더링 큐",
            Overview: "진행 중·대기 중 생성/병합 작업의 진행률과 리소스(CPU/RAM/GPU)를 실시간 감시합니다.",
            Roles: new[]
            {
                "크리에이터 — 생성 진행·리소스 여유를 확인합니다.",
                "권한 — 로그인한 모든 사용자가 사용할 수 있습니다.",
            },
            Steps: new[]
            {
                "리소스 모니터에서 GPU·VRAM 여유를 확인한 뒤 대량 작업을 시작합니다.",
                "진행 단계(생성 → 다운로드 → 병합)와 진행률을 실시간으로 확인합니다.",
            },
            Indicators: new[]
            {
                new HelpIndicator(HelpTone.Info, "GPU 감시",  "nvidia-smi로 사용률·VRAM 실시간 표시."),
                new HelpIndicator(HelpTone.Safe, "CPU 렌더",  "GPU가 없으면 CPU 렌더 모드로 표시."),
                new HelpIndicator(HelpTone.Warn, "VRAM 포화", "가득 차면 4K/병렬 생성 수를 줄이세요."),
            },
            Protocol: new[]
            {
                "GPU가 N/A면 NVIDIA 드라이버/nvidia-smi 설치를 확인하세요(AMD/내장은 CPU 모드).",
                "GPU 60fps 모션 보간은 NVENC 지원 시 자동 가속됩니다.",
            },
            Caveat: "리소스 수치는 **감시용**이며 안전 임계값이 아닙니다."
        ),

        // ── 갤러리 ────────────────────────────────────────────────────────────
        new(
            Route: "gallery",
            Icon: "🖼️",
            Title: "갤러리",
            Overview: "완성 영상 자산을 탐색·재생·삭제하고, 메타 인스펙터로 프롬프트·폴리·프리셋을 조회하며 원클릭으로 스토리보드에 복원합니다.",
            Roles: new[]
            {
                "크리에이터 — 결과물을 확인·재활용합니다.",
                "권한 — 로그인한 모든 사용자가 사용할 수 있습니다.",
            },
            Steps: new[]
            {
                "썸네일을 클릭하면 미리보기와 [메타 인스펙터]·[스토리보드로 복원]이 열립니다.",
                "[🎞️ 스토리보드로 복원]으로 원본 프로젝트를 1초 만에 다시 열어 재편집합니다.",
                "[📦 OSMU 내보내기]로 8대 플랫폼 메타데이터·태그를 한 번에 뽑습니다.",
            },
            Indicators: new[]
            {
                new HelpIndicator(HelpTone.Info, "메타 인스펙터", "완성 영상 → 원본 프롬프트/폴리 조회."),
                new HelpIndicator(HelpTone.Safe, "원클릭 복원",   "원본 .json이 있으면 1초 재편집."),
                new HelpIndicator(HelpTone.Info, "OSMU",         "8대 플랫폼 메타데이터/태그 1-Click."),
            },
            Protocol: new[]
            {
                "복원 버튼이 비활성화되면 원본 프로젝트(.json)가 저장됐는지 확인하세요.",
                "영상이 안 보이면 [↻ 새로고침]을 누르거나 병합 완료를 확인하세요.",
            },
            Caveat: "복원은 원본 프로젝트가 저장돼 있을 때만 가능합니다."
        ),

        // ── 사용자 설정 ───────────────────────────────────────────────────────
        new(
            Route: "user-settings",
            Icon: "🎚️",
            Title: "사용자 설정",
            Overview: "이 PC의 화면 환경(테마·밀도·모션 등)을 조정합니다. 관리자 비밀번호가 필요 없으며 이 PC에만 저장됩니다.",
            Roles: new[]
            {
                "모든 사용자 — 자기 작업 환경을 자유롭게 바꿀 수 있습니다.",
                "권한 — 별도 권한이 필요 없습니다.",
            },
            Steps: new[]
            {
                "**테마**(다크/라이트)와 **밀도**를 취향에 맞게 고릅니다.",
                "설정은 즉시 반영되고 이 PC에 저장됩니다.",
            },
            Indicators: new[]
            {
                new HelpIndicator(HelpTone.Info, "이 PC 저장", "설정은 서버가 아니라 이 PC의 브라우저 저장소에 남습니다."),
                new HelpIndicator(HelpTone.Safe, "즉시 반영",  "저장 버튼 없이 바로 적용됩니다."),
            },
            Protocol: new[]
            {
                "공용 PC라면 로그아웃 후에도 화면 설정은 남습니다 — 계정 정보와는 무관합니다.",
            },
            Caveat: "여기 설정은 **이 PC 전용**입니다. 다른 PC에서 로그인하면 그 PC의 설정을 따릅니다."
        ),

        // ── 관리자 설정 ───────────────────────────────────────────────────────
        new(
            Route: "settings",
            Icon: "⚙️",
            Title: "관리자 설정",
            Overview: "회원·권한, 메일(SMTP) 발송, 설치 PC 정보, 라이선스, 관리자 명부를 관리합니다. 진입 시 관리자 비밀번호가 필요합니다.",
            Roles: new[]
            {
                "관리자(Admin) — 회원 승인·권한 변경·라이선스 발급을 담당합니다.",
                "권한 — Admin 계정만 진입할 수 있습니다(테스트 계정 불가).",
            },
            Steps: new[]
            {
                "**회원·권한** 탭에서 가입 회원을 승인하고 역할(Admin/Operator/Viewer)을 지정합니다.",
                "**메일(SMTP)** 탭에서 발송 서버를 설정하면 가입 통보·OTP 메일이 나갑니다. [테스트 발송]으로 먼저 확인하십시오.",
                "**라이선스** 탭에서 발급 목록을 관리합니다. 회원별 발급은 [회원·권한] 탭의 회원 행에서 합니다.",
            },
            Indicators: new[]
            {
                new HelpIndicator(HelpTone.Info,   "회원·권한",   "가입 승인·역할(RBAC)·라이선스 키 부여."),
                new HelpIndicator(HelpTone.Info,   "메일(SMTP)",  "발송 설정은 이 PC에 암호화되어 저장됩니다."),
                new HelpIndicator(HelpTone.Warn,   "관리자 비밀번호", "이 화면 진입에는 Admin 계정 비밀번호가 필요합니다."),
                new HelpIndicator(HelpTone.Safe,   "설치 PC 정보", "배포 대상 PC의 하드웨어·OS 정보가 자동 수집됩니다."),
            },
            Protocol: new[]
            {
                "SMTP 비밀번호는 이 PC에 기기·사용자 바인딩 암호화로 저장됩니다 — 다른 PC로 파일을 복사해도 열리지 않습니다.",
                "라이선스 서명 키(license_signing.dat)가 있는 PC에서만 새 라이선스를 발급할 수 있습니다.",
            },
            Caveat: "관리자 비밀번호는 **Admin 계정의 로그인 비밀번호**와 같습니다. 별도 비밀번호가 아닙니다."
        ),
    };
}
