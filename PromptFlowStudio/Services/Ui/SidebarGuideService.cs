using System;
using System.Collections.Generic;

namespace PromptFlowStudio.Services.Ui;

/// <summary>사이드바 메뉴 가이드 1개 — 단축키 · 요약 · 워크플로우 역할 · 권장 작업 순서.</summary>
public sealed record SidebarGuide(string Route, string Icon, string Title, string Shortcut, string Summary, string Role, string Order);

/// <summary>
/// [PromptFlowStudio] 사이드바 대화형 가이드 — 순수 BCL 오프라인 Authored Dictionary(외부 NuGet 0).
///   ▸ 각 메뉴의 <b>단축키 · 주요 기능 요약 · 워크플로우 내 역할 · 권장 작업 순서</b>를 담아 호버 시 <see cref="SidebarTooltip"/> 글래스 카드로 안내.
///   ▸ 라우트("" = 워크스페이스)로 조회.
/// </summary>
public sealed class SidebarGuideService
{
    private static readonly Dictionary<string, SidebarGuide> Guides = new(StringComparer.OrdinalIgnoreCase)
    {
        [""] = new("", "🎬", "워크스페이스", "Alt+0",
            "생성 워크플로우 랜딩 — 새 프로젝트 시작, 빠른 시작 템플릿, 최근 프로젝트.",
            "모든 작업의 시작점(허브)", "① 여기서 시작 → 프롬프트 또는 스토리보드로"),
        ["ig-loop"] = new("ig-loop", "🚀", "인스타그램 · 24/7 바이럴 오토메이션", "—",
            "인기 릴스 큐레이션 → 리믹스 → GPU 인코딩 → 골든아워 자동 발행 · 성과 기반 자가 튜닝 · 노출 급감 시 동결.",
            "인스타그램 파이프라인 관제(최상위)", "① 지역·자율 모드 설정 → Top Chart 원클릭 또는 24/7 자율 → 튜닝 차트 확인"),
        ["ig-reels"] = new("ig-reels", "📱", "인스타그램 · 릴스 바이럴 리믹스", "—",
            "3초 훅 분해 · 긴장 아크/더빙/세트 치환 리믹스 · SSIM 60% 오리지널리티 · 원작 크레딧 가드.",
            "리믹스 단계(v17.47)", "② 원본 대비 유사도와 크레딧 상태 확인"),
        ["ig-api"] = new("ig-api", "🔗", "인스타그램 · Graph API 연동", "—",
            "Meta 자격(장기 토큰) · 쿼터 · 4 MB 청크 업로드 · 인사이트/웹훅.",
            "발행·지표 연동(v17.48)", "③ 실 계정 자격 적용 후 실 API 모드 전환"),
        ["ig-transcode"] = new("ig-transcode", "🎞️", "인스타그램 · 9:16 트랜스코더", "—",
            "GPU H.264/AAC · fMP4 먹싱 · 8 Mbps 버짓 가드 · 로컬 웹훅 리스너.",
            "인코딩 단계(v17.49)", "④ 백엔드(GPU/CPU)와 비트레이트 확인"),
        ["workspace"] = new("workspace", "✍️", "프롬프트 입력", "—",
            "텍스트→영상(T2V)·이미지→영상(I2V) 단일 클립을 생성합니다.",
            "생성 파이프라인 1단계(입력)", "② 프롬프트 작성 → 부스팅 → 생성"),
        ["storyboard"] = new("storyboard", "🎞️", "스토리보드", "—",
            "여러 컷을 구성·생성·병합. 아기 테마 원클릭, 세이프존, 텐션 타임라인.",
            "생성 파이프라인 2단계(구성·연출)", "③ 테마/컷 구성 → 전체 생성 → 병합"),
        ["batch"] = new("batch", "🏭", "대량 양산", "—",
            "엑셀식 그리드/CSV로 수십 개 프로젝트를 일괄 등록·생성합니다.",
            "양산 파이프라인(스케일)", "선택: 대량 콘텐츠가 필요할 때"),
        ["analytics"] = new("analytics", "📊", "생산성 & 비용 대시보드", "—",
            "AI 호출 비용/토큰 · 예산 한도 · VRAM/RAM 텔레메트리 · 플랫폼/테마 효율 · CSV/JSON 리포트.",
            "운영 모니터링(비용 통제)", "양산 중 비용·자원 확인 또는 예산 한도 설정"),
        ["live-studio"] = new("live-studio", "📺", "24/7 AI 라이브 스튜디오", "—",
            "플레이리스트 무한 루프 RTMP/SRT 동시 송출 · 자동 재연결 · 실시간 채팅 AI 반응 · 아바타 립싱크/표정 오버레이.",
            "송출 파이프라인(라이브 운영)", "완성 영상을 24시간 라이브로 돌리며 채팅에 반응할 때"),
        ["node-studio"] = new("node-studio", "🧩", "비주얼 노드 플로우 스튜디오", "—",
            "Prompt ➔ ControlNet ➔ Wan ➔ 업스케일/후처리 노드 배선 · 레벨 병렬 실행 · 마스크/키프레임 캔버스 · 라이브 동기화 · 원격 GPU 오프로딩.",
            "고급 파이프라인 설계(노드 그래프)", "여러 단계를 시각적으로 조합해 반복 실행할 때"),
        ["brand-studio"] = new("brand-studio", "🛍️", "커머셜 브랜드 스튜디오", "—",
            "협찬 제품 누끼·조명 매칭 → 테마 장면 합성 · 로고/PPL/CTA 오버레이 · Gemini CM 카피 → 1-Click 광고 비디오.",
            "커머셜 출력(협찬·PPL)", "브랜드 협찬 광고 영상이 필요할 때"),
        ["camera-studio"] = new("camera-studio", "🎥", "스마트 카메라 & 모션", "—",
            "📡 RTSP/SRT/WebRTC 스트림 · FPS/지연 HUD · 프레임 캡처(I2V) + 🕹️ Wan 2.1 카메라 워크·스타일·모션 조합 → 원클릭 작업실 전송.",
            "생성 파이프라인 보조(실사 레퍼런스·연출 강화)", "현장 장면을 I2V 시작 프레임으로 쓰거나 프롬프트에 카메라 워크를 얹고 싶을 때"),
        ["queue"] = new("queue", "⏳", "렌더링 큐", "—",
            "생성/병합 진행률과 CPU·RAM·GPU 리소스를 실시간 감시합니다.",
            "생성 파이프라인 3단계(렌더 감시)", "④ 생성 시작 후 진행·리소스 확인"),
        ["gallery"] = new("gallery", "🖼️", "갤러리", "—",
            "완성 영상 자산 탐색·재생·메타 인스펙터·원클릭 스토리보드 복원.",
            "생성 파이프라인 4단계(완성·재활용)", "⑤ 결과 확인 → 필요 시 복원해 재편집"),
        // v5.8.2: AI 15초 컷다운 — 15~30초 원본을 15초 숏폼으로 자동 재단(신규 메뉴 · 갤러리 다음).
        ["ai-cutdown"] = new("ai-cutdown", "✂️", "AI 15초 컷다운", "—",
            "15~30초 원본 동영상을 훅/클라이맥스 보존 휴리스틱으로 15초 숏폼 1편으로 자동 재단합니다.",
            "생성 파이프라인 보조(숏폼 변환)", "완성 롱컷 원본 → 15초 숏폼이 필요할 때"),
        // v5.6.2: 구글 Flow 대체 코어 — 노드 오케스트레이터 정식 라우트.
        ["flow"] = new("flow", "🕹️", "노드 오케스트레이터", "—",
            "DAG 위상 정렬 기반 비동기 무한 노드 오케스트레이터 및 Wan 3.0 실행기 — 프롬프트→모션→Wan→프리뷰 체인·I2V 루프·롱폼 병합.",
            "구글 Flow 대체 코어(노드 파이프라인)", "노드 그래프 자유 구성 → ▶ 실행 / 🔄 N회 자동 체인"),
        ["user-settings"] = new("user-settings", "🎚️", "사용자 설정", "—",
            "이 PC의 테마·밀도·모션·알림 + 방송 스튜디오 모드 전환.",
            "환경 설정(개인)", "언제든: 화면 무드·표시 조정"),
        ["settings"] = new("settings", "⚙️", "관리자 설정", "—",
            "회원·라이선스·메일·AI 엔진·Wan/Gemini 키·설치 PC 운영 관리.",
            "시스템 운영(관리자)", "최초 1회: 키·엔진 등록 후 사용"),
        // v5.3.3~5.3.4: 모바일 시뮬레이터 호버 가이드(도메인 정화 — AI 프롬프트/씬/렌더큐/자이로 3D 구도).
        ["mobile-sim"] = new("mobile-sim", "📱", "모바일 시뮬레이터", "—",
            "PC 화면 우측에 3D 모바일 뷰포트를 띄워 AI 프롬프트·스토리보드 씬·렌더 큐·자이로 3D 구도를 미리봅니다.",
            "모바일 UX 미리보기(데스크톱)", "배포 전: 모바일 화면·터치 제어를 PC 에서 즉시 확인"),
        // v5.3.4: [원클릭 앱 제작] 중복 메뉴 제거 → 배포 가이드는 [관리자 설정] 항목으로 일원화(oneclick-deploy 항목 삭제).
    };

    /// <summary>라우트로 가이드 조회(없으면 null).</summary>
    public SidebarGuide? Get(string? route) => route is not null && Guides.TryGetValue(route, out var g) ? g : null;
}
