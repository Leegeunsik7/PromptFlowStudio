namespace PromptFlowStudio.Services.Ui;

/// <summary>워크스페이스 탭 1개 — 제목 · 역할 1줄 · 소속 카탈로그 키(첫 키 = 탭 진입 기본 엔진).</summary>
public sealed record StudioWorkspaceTab(string Title, string Role, string[] Members);

/// <summary>슈퍼-워크스페이스 1개 — <c>ws:</c> 키 · 6대 그룹 · 개요 · 전용 통합 모달 키(null = 기존 모달 + 상단 워크스페이스 독) · 탭 · 작업 순서.</summary>
public sealed record StudioWorkspace(string Id, string Icon, string Title, int Group, string Overview, string? ModalKey, StudioWorkspaceTab[] Tabs, string[] Workflow);

/// <summary>기존 카탈로그 키 → [워크스페이스 · 탭 · 탭 내 엔진] 좌표(값 타입 · 무할당 전달).</summary>
public readonly record struct WorkspaceSubTabRoute(int Workspace, int Tab, int Member);

/// <summary>
/// [PromptFlow Studio v18.0.0] 175 분절 모달 → 32 엔터프라이즈 슈퍼-워크스페이스 통폐합 카탈로그.
/// 사이드바는 32 워크스페이스만 노출하고, 기존 175 키(Ctrl+K · 도움말 연관 메뉴 · 명령 팔레트)는 <see cref="ResolveWorkspaceSubTabRoute"/> 가
/// 워크스페이스 + 하위 탭 좌표로 순간이동시킨다. <see cref="StudioCatalog"/> 175 엔트리 · 백엔드 서비스는 그대로 보존(하위 탭으로 주입).
/// </summary>
public static class StudioWorkspaceCatalog
{
    public const string ShortsModalKey = "_shortsStudioWorkspace";
    public const string PublisherModalKey = "_socialPublisherWorkspace";
    public const string ViralGrowthModalKey = "_viralGrowthCommerceWorkspace";

    public static readonly string[] Groups =
    {
        "🎬 가상 프로덕션",
        "🎭 디지털 휴먼",
        "📺 방송 MCR",
        "🛍️ 라이브 커머스",
        "📸 소셜 & 바이럴",
        "⚙️ 시스템 & 코어",
    };

    public static readonly StudioWorkspace[] Workspaces =
    {
        Generic("ws:vpStage", "🎬", "가상 프로덕션 스테이지 & ICVFX", 0, "LED 월 · 젠록 · 카메라 트래킹 · 세트 확장 · 매팅 · 로보틱 MoCo 를 한 스테이지 관제로 묶습니다.",
            "_icvfxWall", "_gfWall", "_camTrack", "_setExt", "_matting", "_vpMaster", "_icvfx", "_roboMoco", "sb:_virtualOpen"),
        Generic("ws:neuralRender", "🧊", "3DGS 뉴럴 렌더링 & 월드", 0, "가우시안 스플래팅 · OpenUSD · 패스 트레이싱 · 4D 월드 · 비디오 디퓨전 · DIT 컬러를 하나의 렌더 파이프로 다룹니다.",
            "_gsEnv", "_gsShadow", "_usd", "_pathTrace", "_genWorld", "_gsplat", "_world4d", "_videoDiffusion", "_deflicker", "_ditColor", "sb:_spatialOpen"),
        Generic("ws:cinemaCamera", "🎥", "시네마토그래피 & AI 디렉터", 0, "구도 · 렌즈 · 자율 디렉터 · 리듬 액션 MV · 모션 타이포를 카메라 연출 한 곳에서 전환합니다.",
            "_cinematography", "_cinemato", "L_directorOpen", "L_cineDirectorOpen", "L_lensOpen", "_rhythmMv", "_motionType", "sb:_vfxOpen"),
        Generic("ws:stageFx", "💡", "무대 연출 & 물리 VFX", 0, "조명 · 파이로 · 기상 · 유체 습윤 · 뉴럴 물리 · 폴리 · 비트 싱크를 무대 효과 랙으로 결합합니다.",
            "L_stageLightOpen", "L_pyroOpen", "_genVfx", "_weather", "_genEnv", "_wetness", "_neuralPhys", "L_foleyOpen", "_beatSync"),

        Generic("ws:avatarBody", "🧬", "가우시안 아바타 & 피직스", 1, "4D 가우시안 아바타의 피부 · 근육 · 의상 · 헤어 · 얼굴/손을 탭으로 넘기며 한 캐릭터를 완성합니다.",
            "_gaussian", "_dqAvatar", "_digitalHuman", "_faceHand", "_skinSss", "_anatTissue", "_garment", "_clothHair", "_hairGroom", "_hairCloth"),
        Generic("ws:motionCapture", "🏃", "모션 캡처 & 생체역학", 1, "무마커 캡처 → 모션 매칭 → 접촉 로코모션 → 양손 조작 → 래그돌 복원을 한 동선으로 잇습니다.",
            "_markerlessMocap", "_mocap", "L_motionMatchOpen", "L_contactLocoOpen", "_dexHand", "L_dexManipOpen", "L_ragdollOpen", "sb:_twinOpen"),
        Generic("ws:voiceLocalization", "🗣️", "음성 · 더빙 & 로컬라이제이션", 1, "뉴럴 음성 · Audio2Face · 다국어 더빙 · 립싱크 · 실시간 통역 송출을 언어별 탭으로 정리합니다.",
            "_voice", "_facialPerf", "_lipSyncDub", "_localization", "L_liveDubOpen", "_globalDub", "sb:_dubbingOpen"),
        Generic("ws:avatarActing", "🎭", "아바타 연기 & 토크쇼", 1, "자율 연기 디렉팅 · 멀티 아바타 토크쇼 · 시맨틱 채팅 반응 · 아이돌 에이전시 운영을 묶습니다.",
            "_acting", "_talkshow", "_semanticWeather", "_agency"),

        Generic("ws:mcrMaster", "🎛️", "방송 주조정실 & 편성", 2, "24/7 편성 · MCR 컴포지터 · 비전 스위처 · SSAI · AR 그래픽 · 뉴스룸/스포츠 중계를 하나의 주조정실로 운용합니다.",
            "_bcastMcr", "_stationMaster", "_masterCtl", "_mcr", "_arGfx", "_live", "_newsroom", "_sports"),
        Generic("ws:showProduction", "🎪", "쇼러너 & 인터랙티브 시네마", 2, "AI 쇼러너 · 멀티에이전트 크루 · 군중 시뮬 · 인터랙티브 분기 시네마 · 프랜차이즈 유니버스를 제작 단위로 묶습니다.",
            "_showrunner", "_crewStudio", "_crowdSim", "_iCinema", "_cinema"),
        Generic("ws:scriptTimeline", "📜", "각본 · 편집 & 타임라인", 2, "시나리오 · 내러티브 타임라인 · 연속성 검사 · 자율 편집 · MV 안무를 각본→편집 순서로 배치합니다.",
            "sb:_screenplayOpen", "L_narrativeOpen", "_continuity", "_autoEdit", "sb:_mvOpen"),
        Generic("ws:audioPost", "🎧", "공간 음향 & 사운드 포스트", 2, "6DoF 음향 · 공간 오디오 · 영화 음악 · Dolby Atmos · 팟캐스트 믹스를 사운드 포스트 한 곳에서 다룹니다.",
            "_acoustics", "_spatialAudio", "_filmScore", "sb:_soundOpen", "_podcast"),

        Generic("ws:liveCommerce", "🛍️", "라이브 커머스 & 쇼퍼테인먼트", 3, "라이브 커머스 · 버추얼 커머스 · 인터랙티브 쇼핑 분기를 판매 동선 하나로 연결합니다.",
            "_commerce", "_virtualCommerce", "sb:_interactiveOpen"),
        Generic("ws:virtualLive", "📡", "버추얼 라이브 & 스트리밍", 3, "버추얼 라이브 · 인플루언서 · 게이밍 코스트림 · 스트림 게이트웨이 · RTMPS 리스트림 · 시청자 위젯 · 아카이브를 송출 체인으로 묶습니다.",
            "L_virtualLiveOpen", "_influencerLive", "_gaming", "_streamGateway", "_liveBroadcastRestream", "_liveAudienceEngagement", "_liveArchiveVault"),
        Generic("ws:mediaEmpire", "👑", "미디어 제국 & 콘텐츠 리퍼포징", 3, "옴니채널 미디어 제국 · 에듀테크 · VOD 아티클 리퍼포징 · 라이브-투-릴스 팩토리로 한 원본을 다매체로 회전시킵니다.",
            "_empire", "_liveToReels", "_vodRepurposing", "_edutech"),

        new("ws:shortsFactory", "🏭", "원스톱 숏폼 AI 팩토리", 4,
            "라이브/원본에서 킬러 클립을 잘라 9:16 으로 리프레임하고, 키네틱 자막 · 다국어 더빙 · 리텐션 줌 펀치 · 안전 BGM · 고CTR 썸네일 · fMP4 인출까지 화면을 닫지 않고 4 단계로 끝냅니다.",
            ShortsModalKey,
            new StudioWorkspaceTab[]
            {
                new("① 절삭 & 리프레임", "킬러클립 슬라이서가 클라이맥스 구간을 자르고 9:16 세이프존 프레임 가이드로 피사체 중심을 맞춥니다.", new[] { "_liveAutoClipper", "_visualHooks" }),
                new("② 자막 & 더빙", "호르모지 키네틱 자막을 입히고 원본 음색을 보존한 다국어 음성을 립싱크로 정합합니다.", new[] { "_brollSubtitles", "_multilingualDubbing" }),
                new("③ 리텐션 & 사운드", "이탈 절벽 앞 H.264 SPS 줌 펀치를 무트랜스코드 리먹싱하고 저작권 위험 음원을 로열티 프리 BGM 으로 치환합니다.", new[] { "_retentionHeatmapReEditor", "_copyrightCompliance" }),
                new("④ 인출 & 썸네일", "고CTR 썸네일 프레임을 뽑고 fMP4 하드웨어 익스포터로 업로드용 9:16 MP4 를 인출합니다.", new[] { "_viralPredictorExport", "_visualPolish" }),
            },
            new[] { "Step 1 · ① 탭에서 클립 구간 · 리프레임 중심을 맞추고 「✔ 절삭 확정」을 누릅니다.", "Step 2 · ② · ③ 탭으로 넘어가며 자막 프리셋 · 더빙 언어 · 줌 펀치 · BGM 치환을 확정합니다 — 앞 단계 값이 상단 컨텍스트로 그대로 이어집니다.", "Step 3 · ④ 탭에서 썸네일 위치를 고르고 「📦 인출 확정」으로 MP4 를 만든 뒤 소셜 퍼블리셔 워크스페이스로 배포합니다." }),
        new("ws:socialPublisher", "🚀", "소셜 퍼블리셔 매니저", 4,
            "OAuth2 계정 볼트에서 채널을 연결하고, 다채널 변형을 배포한 뒤 A/B 스플릿 레이스로 승자를 고르고 패자는 비지빌리티 게이트웨이로 비공개 · 삭제합니다.",
            PublisherModalKey,
            new StudioWorkspaceTab[]
            {
                new("① OAuth2 계정 볼트", "PKCE OAuth2 토큰을 AES-GCM 볼트에 보관하고 YouTube · Instagram · Graph API 계정을 연결합니다.", new[] { "_liveOAuthPublisher", "_graphApi", "_omniPublisher" }),
                new("② 다채널 변형 배포", "다계정 매트릭스 · 스마트 캘린더 · 옴니채널 댓글로 9:16/1:1 변형을 시간차 배포합니다.", new[] { "_matrixSyndication", "_smartPublish", "_omni" }),
                new("③ A/B 레이스 & 게이트웨이", "베타 사후 확률 A/B 스플릿 레이스 차트로 승자를 판정하고 패자를 플랫폼 API 로 비공개 · 삭제합니다.", new[] { "_postPublishSplitTest", "_platformVisibilityGateway" }),
            },
            new[] { "Step 1 · ① 탭에서 배포할 계정을 OAuth2 로 연결합니다.", "Step 2 · ② 탭에서 채널별 변형과 배포 시각을 정해 발행합니다.", "Step 3 · ③ 탭의 A/B 레이스 차트로 승자를 확인하고 패자를 게이트웨이로 회수합니다." }),
        new("ws:viralGrowth", "📈", "바이럴 그로스 룸", 4,
            "실시간 트렌드재킹 · 경쟁 채널 스파이 역공학 · 제휴 커머스 스마트 태거 · 골든아워 댓글 부스터를 한 전략 룸에서 엔진 칩으로 전환합니다.",
            ViralGrowthModalKey,
            new StudioWorkspaceTab[]
            {
                new("🧠 전략 룸", "트렌드 신호 → 경쟁 채널 청사진 → 제휴 링크 태깅 → 골든아워 댓글 부스트 순으로 성장 전략을 한 화면에서 실행합니다.",
                    new[] { "_socialTrendJackingRadar", "_competitorViralSpy", "_viralCommerceAffiliate", "_goldenHourEngagement" }),
            },
            new[] { "Step 1 · 트렌드재킹 칩에서 급상승 해시태그를 고릅니다.", "Step 2 · 경쟁 채널 스파이 칩으로 훅 · 컷 템포 청사진을 역공학하고 제휴 태거로 커머스 링크를 붙입니다.", "Step 3 · 골든아워 칩에서 업로드 직후 댓글 인게이지먼트를 부스트합니다." }),
        Generic("ws:viralAutomation", "🤖", "바이럴 오토메이션 & 리믹스 랩", 4, "옴니 바이럴 콕핏 · 제로-터치 오토파일럿 · 24/7 루프 · 릴스 리믹스 · 유지율 A/B · 보이스 FX · 하드웨어 트랜스코더를 자동화 랩으로 묶습니다.",
            "_omniViralMasterCockpit", "_autopilot", "_igLoop", "_reels", "_retentionAb", "_voiceFxStudio", "_vtc", "_realRunnerBatchSandbox", "sb:_mediaStudioOpen"),

        Generic("ws:intentHelp", "🧭", "인텐트 내비게이션 & 도움말", 5, "목적 문장 검색(Ctrl+K) · 명령 팔레트 · 사용자 도움말 · 시작 가이드로 원하는 기능을 바로 찾습니다.",
            "_intentNavigationHub", "@palette", "@help", "_helpModal"),
        Generic("ws:enterpriseMaster", "🏢", "엔터프라이즈 마스터 스위트", 5, "마스터 스위트 · 옴니모달 넥서스 · 멀티 엔진 시너지 · 팀 렌더 팜 · 마스터 파이프라인을 전사 관제로 묶습니다.",
            "_masterSuite", "_nexus", "_synergy", "sb:_masterOpen", "sb:_enterpriseOpen"),
        Generic("ws:twinSim", "♊", "디지털 트윈 & 합성 데이터", 5, "디지털 트윈 · 옴니버스 월드 · 신경망 SLAM 재구성으로 현실 공간을 합성 데이터로 옮깁니다.",
            "_twinData", "_twinWorld", "_spatialSlam"),
        Generic("ws:telepresence", "🔮", "홀로그래픽 텔레프레즌스", 5, "홀로그래픽 텔레프레즌스 · AGI 디렉터 · 볼류메트릭 텔레포테이션을 한 통화 세션으로 묶습니다.",
            "_holoTele", "_neuralTele", "_holo", "_teleport"),
        Generic("ws:spatialXr", "🥽", "공간 컴퓨팅 & XR", 5, "WebXR 스트리밍 · 공간 컴퓨팅 · 홀로그램 스테이지 · 6DoF 햅틱 · BCI 바이오피드백을 XR 스택으로 정리합니다.",
            "_spatialComputing", "_xrStream", "_spatialXr", "_xrStage", "_haptics", "_bci"),
        Generic("ws:edgeNetwork", "🌐", "에지 네트워크 & CDN", 5, "가상 아레나 롤백 넷코드 · 신경망 코덱 CDN · 에지 스웜 메시 · MAM 글로벌 배포를 전송 계층으로 묶습니다.",
            "_neuralCdn", "_edgeSwarm", "_arenaNet", "_mam"),
        Generic("ws:gpuCompute", "⚡", "GPU 스웜 & 하드웨어 가속", 5, "분산 GPU 스웜 · 하드웨어 가속 · 저사양 최적화 · 멀티 엔진 라우터로 연산 자원을 배분합니다.",
            "_gpuSwarm", "_hwAccel", "_lowSpec", "L_multiEngineOpen"),
        Generic("ws:reliability", "🛡️", "SLA · SRE & 무중단 운영", 5, "엔터프라이즈 SLA · 글로벌 SRE 관제 · 운용 총괄로 무중단 복구를 관리합니다.",
            "_enterpriseSla", "_sreGrid", "_globalOps"),
        Generic("ws:telemetry", "📊", "텔레메트리 & 자율 튜닝", 5, "실시간 텔레메트리 · 자율 튜닝 · DOM UI 자가 수정을 관측-조치 루프로 묶습니다.",
            "_liveTelemetry", "_selfHeal"),
        Generic("ws:qaVerify", "🔍", "QA & 클라우드 검증", 5, "클라우드 소스 검증 · 헤드리스 시각 QA 로 릴리스 전 회귀를 잡습니다.",
            "_cloudVerify", "L_visualQaOpen"),
        Generic("ws:security", "🏰", "보안 · 저작권 & 소버린 클라우드", 5, "콘텐츠 보안 · 소버린 양자 금고 · 시스템 하드닝을 보안 계층으로 정리합니다.",
            "_contentSecurity", "_sovereign", "_hardening"),
        Generic("ws:agentRelease", "🤖", "멀티 에이전트 연합 & 릴리스", 5, "멀티 에이전트 지식 그래프 연합과 엔터프라이즈 상용 릴리스를 배포 관문으로 묶습니다.",
            "_federation", "_release"),
        Generic("ws:accessibility", "🤟", "유니버설 접근성", 5, "자막 · 수어 · 음성 해설 등 배리어프리 산출물을 전 공정에 적용합니다.",
            "_a11y"),
    };

    private static readonly Dictionary<string, WorkspaceSubTabRoute> Routes = BuildRoutes(out _mapped, out _duplicates);
    private static readonly Dictionary<string, string> Labels = BuildLabels();
    private static int _mapped, _duplicates;

    /// <summary>라우트 표에 등록된 기존 카탈로그 키 수(175 기대 · 워크스페이스 <c>ws:</c> 키 제외).</summary>
    public static int MappedKeyCount => _mapped;
    /// <summary>두 워크스페이스에 중복 배치된 키 수(0 이어야 정상).</summary>
    public static int DuplicateKeyCount => _duplicates;

    /// <summary>
    /// 기존 모달 키(또는 <c>ws:</c> 워크스페이스 키) → [워크스페이스 · 탭 · 엔진] — Dictionary 조회만 하는 0 바이트 경로.
    /// 미등록 키는 false(호출 측이 기존 단독 모달로 폴백).
    /// </summary>
    public static bool ResolveWorkspaceSubTabRoute(string? key, out WorkspaceSubTabRoute route)
    {
        if (key is not null && Routes.TryGetValue(key, out route)) return true;
        route = default;
        return false;
    }

    /// <summary>좌표 → 기존 카탈로그 키(범위 밖은 탭 첫 엔진으로 클램프).</summary>
    public static string MemberKey(in WorkspaceSubTabRoute r)
    {
        var tabs = Workspaces[r.Workspace].Tabs;
        var m = tabs[(uint)r.Tab < (uint)tabs.Length ? r.Tab : 0].Members;
        return m[(uint)r.Member < (uint)m.Length ? r.Member : 0];
    }

    /// <summary>Ctrl+K · 도움말 표기용 「워크스페이스 ▸ 탭」 — 초기화 때 고정한 문자열을 돌려준다(무할당).</summary>
    public static string RouteLabel(string? key) => key is not null && Labels.TryGetValue(key, out var s) ? s : "";

    public static int IndexOfModal(string? modalKey)
    {
        for (var i = 0; i < Workspaces.Length; i++) if (Workspaces[i].ModalKey == modalKey && modalKey is not null) return i;
        return -1;
    }

    public static int CountIn(int group) { var n = 0; foreach (var w in Workspaces) if (w.Group == group) n++; return n; }

    public static int MemberCount(int ws) { var n = 0; foreach (var t in Workspaces[ws].Tabs) n += t.Members.Length; return n; }

    /// <summary>사이드바 검색 — 워크스페이스 제목 · 탭 제목 · 소속 175 기능 제목 중 하나라도 부분 일치(공백 질의 = 전량).</summary>
    public static bool Matches(int ws, string? query) => FirstMatch(ws, query, out _);

    public static int CountMatches(int group, string? query)
    {
        var n = 0;
        for (var i = 0; i < Workspaces.Length; i++) if (Workspaces[i].Group == group && Matches(i, query)) n++;
        return n;
    }

    /// <summary>검색어에 처음 걸린 하위 탭 좌표 — 사이드바 검색 결과 클릭 시 해당 세부 기능 탭으로 직행.</summary>
    public static bool FirstMatch(int ws, string? query, out WorkspaceSubTabRoute route)
    {
        route = new WorkspaceSubTabRoute(ws, 0, 0);
        var q = query.AsSpan().Trim();
        var w = Workspaces[ws];
        if (q.IsEmpty || w.Title.AsSpan().Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        for (var t = 0; t < w.Tabs.Length; t++)
        {
            var tab = w.Tabs[t];
            if (tab.Title.AsSpan().Contains(q, StringComparison.OrdinalIgnoreCase)) { route = new WorkspaceSubTabRoute(ws, t, 0); return true; }
            for (var m = 0; m < tab.Members.Length; m++)
                if (TitleOf(tab.Members[m]).AsSpan().Contains(q, StringComparison.OrdinalIgnoreCase)) { route = new WorkspaceSubTabRoute(ws, t, m); return true; }
        }
        return false;
    }

    /// <summary>카탈로그 제목(미등록 키는 키 그대로).</summary>
    public static string TitleOf(string key)
    {
        foreach (var e in StudioCatalog.Entries) if (e.Key == key) return e.Title;
        return key;
    }

    public static string IconOf(string key)
    {
        foreach (var e in StudioCatalog.Entries) if (e.Key == key) return e.Icon;
        return "•";
    }

    /// <summary>기존 모달 + 독 워크스페이스 — 기존 엔트리 1 개 = 탭 1 개(역할 = 엔트리 도움말 개요).</summary>
    private static StudioWorkspace Generic(string id, string icon, string title, int group, string overview, params string[] members)
    {
        var tabs = new StudioWorkspaceTab[members.Length];
        for (var i = 0; i < members.Length; i++)
            tabs[i] = new StudioWorkspaceTab(TitleOf(members[i]), StudioCatalogHelpRegistry.Find(members[i])?.RoleSummary ?? TitleOf(members[i]), new[] { members[i] });
        var flow = new[]
        {
            "Step 1 · 사이드바 「" + title + "」을(를) 열면 「" + tabs[0].Title + "」 탭으로 시작합니다.",
            members.Length > 1 ? "Step 2 · 상단 워크스페이스 탭으로 「" + tabs[1].Title + "」 등 " + members.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 개 세부 기능을 화면 이동 없이 전환합니다."
                               : "Step 2 · 화면의 핵심 조작으로 결과를 만들고 HUD 지표로 확인합니다.",
            "Step 3 · Ctrl+K 에 세부 기능 이름을 입력하면 이 워크스페이스의 해당 탭으로 바로 들어옵니다.",
        };
        return new StudioWorkspace(id, icon, title, group, overview, null, tabs, flow);
    }

    private static Dictionary<string, WorkspaceSubTabRoute> BuildRoutes(out int mapped, out int duplicates)
    {
        var d = new Dictionary<string, WorkspaceSubTabRoute>(256, StringComparer.Ordinal);
        mapped = duplicates = 0;
        for (var w = 0; w < Workspaces.Length; w++)
        {
            d[Workspaces[w].Id] = new WorkspaceSubTabRoute(w, 0, 0);
            var tabs = Workspaces[w].Tabs;
            for (var t = 0; t < tabs.Length; t++)
                for (var m = 0; m < tabs[t].Members.Length; m++)
                {
                    if (d.TryAdd(tabs[t].Members[m], new WorkspaceSubTabRoute(w, t, m))) mapped++;
                    else duplicates++;
                }
        }
        return d;
    }

    private static Dictionary<string, string> BuildLabels()
    {
        var d = new Dictionary<string, string>(Routes.Count, StringComparer.Ordinal);
        foreach (var kv in Routes)
        {
            var w = Workspaces[kv.Value.Workspace];
            d[kv.Key] = w.Icon + " " + w.Title + " ▸ " + w.Tabs[kv.Value.Tab].Title;
        }
        return d;
    }
}
