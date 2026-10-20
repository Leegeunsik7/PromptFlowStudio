using System;
using System.Collections.Generic;
using System.Linq;

namespace PromptFlowStudio.Services.Ui;

/// <summary>FAQ/장애 조치 1건(질문 + 답변).</summary>
public sealed record MenuFaq(string Q, string A);

/// <summary>메뉴 전용 도움말 1개 — 핵심 기능 사용법 · 전문가 팁 · FAQ/장애 조치 3탭.</summary>
public sealed record MenuHelpTopic(string Key, string Icon, string Title,
    IReadOnlyList<string> Usage, IReadOnlyList<string> Tips, IReadOnlyList<MenuFaq> Faq);

/// <summary>
/// [PromptFlowStudio] 메뉴별 독자적 컨텍스트 도움말(Granular) — 순수 BCL 오프라인 Authored Dictionary(외부 NuGet 0).
///   ▸ 9개 메뉴(워크스페이스/프롬프트 입력/스토리보드/대량 양산/모션·카메라/렌더링 큐/갤러리/사용자 설정/관리자 설정)별로
///     <b>핵심 기능 사용법 · 전문가 팁 · FAQ/장애 조치</b>를 완전히 독립 제공 → <see cref="MenuHelpModal"/>(탭)이 표출.
/// </summary>
public sealed class GranularHelpService
{
    /// <summary>메뉴 키 상수(reference_data 규격).</summary>
    public static class Keys
    {
        public const string Workspace = "Workspace";
        public const string PromptInput = "PromptInput";
        public const string Storyboard = "Storyboard";
        public const string MassProduction = "MassProduction";
        public const string MotionCamera = "MotionCamera";
        public const string RenderQueue = "RenderQueue";
        public const string Gallery = "Gallery";
        public const string UserSettings = "UserSettings";
        public const string AdminSettings = "AdminSettings";
    }

    private static readonly Dictionary<string, MenuHelpTopic> Topics = new(StringComparer.OrdinalIgnoreCase)
    {
        [Keys.Workspace] = new(Keys.Workspace, "🎬", "워크스페이스",
            new[]{ "생성 워크플로우(프롬프트→스토리보드→모션→렌더링→완성)의 랜딩 대시보드입니다.", "[＋ 새 프로젝트]로 텍스트→영상 작업을 시작하고, 우측에서 빠른 시작 템플릿·최근 프로젝트를 엽니다." },
            new[]{ "상단 워크플로우 덱의 각 노드를 눌러 해당 단계로 바로 이동하세요.", "방송 스튜디오 모드에서는 LED 시그널·VU 미터가 상단에 표시됩니다." },
            new[]{ new MenuFaq("최근 프로젝트가 비어 있어요.", "스토리보드에서 만들고 💾 저장하면 최근 목록에 나타납니다."), }),
        [Keys.PromptInput] = new(Keys.PromptInput, "✍️", "프롬프트 입력",
            new[]{ "텍스트→영상(T2V) 또는 참조 이미지→영상(I2V)으로 단일 5초 클립을 생성합니다.", "프롬프트를 입력하고 [⚡ 부스팅]으로 시네마틱 태그를 덧붙인 뒤 생성하세요." },
            new[]{ "I2V는 이미지의 구도·인물을 유지합니다. 얼굴 일관성이 중요하면 I2V를 쓰세요.", "생성 결과가 마음에 들면 스토리보드로 이어 여러 컷으로 확장할 수 있습니다." },
            new[]{ new MenuFaq("생성이 시작되지 않아요.", "관리자 설정 › 영상(Wan)에서 API 키가 등록됐는지 확인하세요."),
                   new MenuFaq("이미지 업로드가 안 돼요.", "PNG/JPG/WebP만 지원합니다. 파일 크기와 형식을 확인하세요.") }),
        [Keys.Storyboard] = new(Keys.Storyboard, "🎞️", "스토리보드",
            new[]{ "여러 컷을 하나의 영상 흐름으로 구성·생성·병합합니다.", "[👶 아기 테마 백과사전]으로 6컷(숏츠)/16컷(롱폼)을 원클릭 자동 구성하거나, 컷을 직접 추가하세요.", "[✨ 전체 생성] → 완료 시 ffmpeg 자동 병합으로 최종 영상이 만들어집니다." },
            new[]{ "세이프존을 켜면 자막/뱃지가 플랫폼 UI를 피해 배치됩니다.", "긴장도 타임라인의 컷 점을 클릭하면 해당 컷으로 스크롤됩니다.", "후처리 스위치를 클릭하면 ON/OFF 연출 차이 가이드가 뜹니다." },
            new[]{ new MenuFaq("병합이 실패해요.", "ffmpeg.exe가 앱 폴더 또는 PATH에 있는지 확인하세요(없으면 병합 불가)."),
                   new MenuFaq("배속을 바꿔도 안 빨라져요.", "[⚡ 템포 배속] 체크박스가 켜져 있는지 확인하세요.") }),
        [Keys.MassProduction] = new(Keys.MassProduction, "🏭", "대량 양산",
            new[]{ "CSV/JSON 템플릿 또는 엑셀식 그리드로 수십 개 프로젝트를 한 번에 등록·생성합니다.", "그리드에 제목·대본을 입력하고 [일괄 큐 등록] 후 [배치 시작]으로 순차/병렬 생성하세요." },
            new[]{ "행별로 유효성(제목·대본 필수)이 실시간 검증됩니다 — 빨간 행은 등록에서 제외됩니다.", "공통 옵션(해상도·BGM·워터마크)은 배치 옵션에서 전 항목에 일괄 적용됩니다." },
            new[]{ new MenuFaq("CSV를 붙여넣으면 열이 깨져요.", "첫 열=제목, 둘째 열=대본 순서로, 쉼표가 포함된 셀은 큰따옴표로 감싸세요."),
                   new MenuFaq("일부 항목만 실패해요.", "실패 항목은 건너뛰고 계속 진행됩니다. 완료 후 실패 항목만 재시도하세요.") }),
        [Keys.MotionCamera] = new(Keys.MotionCamera, "🎥", "모션·카메라",
            new[]{ "Wan 2.1 카메라 워크(Pan/Tilt/Zoom/Orbit/Roll/Truck)와 스타일·모션 프리셋을 조합합니다.", "비주얼라이저에서 카메라 워크를 확인하고 [＋ 프롬프트에 추가]로 키워드를 반영하세요." },
            new[]{ "오빗은 인물/제품 소개에, 트럭·팬은 넓은 공간 소개에 어울립니다.", "과한 카메라 이동은 모션 왜곡을 유발할 수 있어 하나만 강조하는 것이 안전합니다." },
            new[]{ new MenuFaq("카메라 워크가 영상에 잘 반영되지 않아요.", "프롬프트 앞부분에 카메라 키워드를 배치하고, 배속을 낮춰 모션을 명확히 하세요.") }),
        [Keys.RenderQueue] = new(Keys.RenderQueue, "⏳", "렌더링 큐",
            new[]{ "진행 중·대기 중 생성/병합 작업의 진행률과 리소스(CPU/RAM/GPU)를 실시간 감시합니다.", "리소스 모니터에서 GPU·VRAM 여유를 확인한 뒤 대량 작업을 시작하세요." },
            new[]{ "nvidia-smi가 설치돼 있으면 GPU 사용률·VRAM이 실시간 표시됩니다(없으면 CPU 렌더 모드).", "VRAM이 가득 차면 4K 업스케일·병렬 생성 수를 줄이세요." },
            new[]{ new MenuFaq("GPU가 N/A로 나와요.", "NVIDIA 드라이버/CUDA와 nvidia-smi가 설치돼야 GPU 정보를 읽습니다. AMD/내장 GPU는 CPU 렌더 모드로 표시됩니다.") }),
        [Keys.Gallery] = new(Keys.Gallery, "🖼️", "갤러리",
            new[]{ "생성된 영상 자산을 탐색·재생·삭제하고, 메타 인스펙터로 프롬프트·폴리·프리셋을 조회합니다.", "썸네일을 클릭하면 미리보기와 함께 [메타 인스펙터]·[스토리보드로 복원]이 열립니다." },
            new[]{ "[🎞️ 스토리보드로 복원]으로 완성 영상의 원본 프로젝트를 1초 만에 다시 열어 재편집하세요.", "메타 인스펙터의 프롬프트를 복사해 다른 프로젝트에 재활용할 수 있습니다." },
            new[]{ new MenuFaq("복원 버튼이 비활성화돼 있어요.", "해당 영상의 원본 프로젝트(.json)가 저장돼 있어야 복원됩니다. 스토리보드에서 💾 저장했는지 확인하세요."),
                   new MenuFaq("영상이 목록에 안 보여요.", "[↻ 새로고침]을 누르거나, 병합이 완료됐는지 확인하세요.") }),
        [Keys.UserSettings] = new(Keys.UserSettings, "🎚️", "사용자 설정",
            new[]{ "이 PC의 화면 테마·밀도·모션·알림 등 표시 환경을 설정합니다(localStorage 저장).", "[스튜디오 모드]에서 표준 다크 ↔ 방송 스튜디오 테마를 미리 보고 즉시 전환하세요." },
            new[]{ "방송 스튜디오 모드는 전 메뉴에 글래스모피즘·네온·VU 미터를 적용합니다.", "설정은 이 PC에만 저장됩니다 — 다른 PC에는 동기화되지 않습니다." },
            new[]{ new MenuFaq("설정이 저장되지 않아요.", "브라우저 저장(localStorage)이 차단된 환경이면 이번 세션에만 적용됩니다. [💾 설정 저장]을 눌렀는지 확인하세요.") }),
        [Keys.AdminSettings] = new(Keys.AdminSettings, "⚙️", "관리자 설정",
            new[]{ "회원·라이선스·메일(SMTP)·AI 엔진·Wan/Gemini 키·설치 PC 등 운영 설정을 관리합니다(관리자 비밀번호 게이트).", "AI 엔진 탭에서 로컬 CLI(Real-ESRGAN/Whisper/LatentSync 등) 경로를 지정하고 연결 테스트하세요." },
            new[]{ "Wan/Gemini 키는 재시작 없이 즉시 반영됩니다(동적 설정).", "엔진이 미설치면 관련 기능 버튼이 자동 비활성화되고 안내 배너가 표시됩니다(Graceful)." },
            new[]{ new MenuFaq("관리자 설정에 못 들어가요.", "관리자 권한 계정으로 로그인해야 합니다. 최초 가입자는 자동으로 관리자입니다."),
                   new MenuFaq("연결 테스트가 실패해요.", "실행 파일 경로가 정확한지, --version/--help가 동작하는지 확인하세요.") }),
    };

    /// <summary>메뉴 키로 도움말 조회(없으면 null).</summary>
    public MenuHelpTopic? Get(string? key) => !string.IsNullOrWhiteSpace(key) && Topics.TryGetValue(key!, out var t) ? t : null;

    /// <summary>전체 메뉴 도움말.</summary>
    public IReadOnlyList<MenuHelpTopic> All => Topics.Values.ToList();
}
