using Microsoft.JSInterop;

namespace PromptFlowStudio.Services.Ui;

// =============================================================================
//  사용자 설정 중앙 저장소
//
//  ━━━ 왜 필요했나 ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//
//  환경 설정 화면의 Save() 는 `_saved = true` 만 하고 아무것도 저장하지
//  않고 있었다. 테마를 '라이트' 로 골라도 화면은 그대로였다. 값이 어디에도
//  안 남으니 다음에 열면 다시 기본값이었다.
//
//  ⚠ 설계 원칙: **읽기는 동기, 쓰기는 비동기.**
//    localStorage 접근은 JS 왕복이라 async 다. 그런데 렌더링 중에는 await 를
//    할 수 없다. 그래서 시작할 때 한 번 통째로 읽어 메모리에 두고, 화면은
//    메모리만 본다. 쓰기는 뒤에서 조용히 내보낸다.
//
//  ⚠ localStorage 는 이 PC 에만 남는다. 서버로 동기화되지 않고 감사 로그에도
//    안 남는다. 그래서 **안전에 영향을 주는 값은 여기 두지 않는다** —
//    위험 임계값 같은 것은 엣지 설정이어야 한다. 여기 있는 건 전부
//    '이 사람이 화면을 어떻게 보고 싶은가' 뿐이다.
//
//  ⚠ 이 앱은 단일 창 데스크톱이라 Singleton 이다. 웹으로 옮기면 Scoped 로
//    바꿔야 한다 — 아니면 모든 접속자가 같은 설정을 공유한다.
// =============================================================================
public sealed class UserPreferenceService
{
    /// <summary>키 이름. 문자열을 여기저기 흩어 두면 오타 하나로 설정이 사라진다.</summary>
    public static class Keys
    {
        public const string Theme = "ui.theme";                 // dark | light
        public const string Density = "ui.density";             // compact | comfortable
        public const string SoundAlert = "ui.sound";            // 1 | 0
        public const string HomePins = "home.pins";             // 구역ID,구역ID
        public const string VisionLayout = "vision.layout";     // 1|4|9|16
        public const string BioEmergencyOnly = "bio.emergency"; // 1 | 0
        public const string TableColumns = "table.cols";        // 화면별 접두 붙여 사용
        public const string RecentCommands = "cmd.recent";      // 명령ID,명령ID
        public const string RecentSearches = "search.recent";   // 검색어,검색어
        /// <summary>시뮬레이터 용어 모드. 1 = 전문가 용어, 0 = 쉬운 비유.</summary>
        public const string SimRawTerms = "sim.rawterms";        // 1 | 0

        /// <summary>스토리보드 미리보기 세이프존 플랫폼(마지막 선택 기억). None|YouTubeShorts|InstagramReels|TikTok. UI 표시용 뿐 — 안전 임계값 아님.</summary>
        public const string SafeZonePlatform = "sz.platform";     // SafePlatform enum 이름

        /// <summary>컷 카드 썸네일 세이프존 가이드 오버레이 표시 여부. UI 표시용 뿐.</summary>
        public const string CutCardSafeZone = "sz.cutcard";       // 1 | 0

        /// <summary>방송 스튜디오 시네마틱 테마(글래스모피즘·네온·펄스 LED). 문서 루트 <c>broadcast</c> 클래스로 적용. 기본 ON.</summary>
        public const string Broadcast = "ui.broadcast";           // 1 | 0

        /// <summary>사이드바만 밝게. 본문 테마와 <b>독립</b>이다.</summary>
        public const string SidebarLight = "ui.sidebar.light";    // 1 | 0

        /// <summary>
        /// 사용자가 직접 끈 모션.
        /// ⚠ OS 의 <c>prefers-reduced-motion</c> 과 <b>별개</b>다. OS 설정은
        ///   존중하되, 그것과 무관하게 끄고 싶은 사용자가 있다(시연 중 녹화 등).
        ///   둘 중 하나라도 켜지면 모션은 멈춘다.
        /// </summary>
        public const string MotionOff = "ui.motion.off";          // 1 | 0

        /// <summary>[v5.6.4] AI 품질/정체성 가드레일 사용 여부.</summary>
        public const string QualityGuardrail = "wan.guardrail";        // 1 | 0
        /// <summary>[v5.6.4] 가드레일 강도 프리셋(Soft|Balanced|Strict).</summary>
        public const string QualityPreset = "wan.guardrail.preset";    // enum 이름
        /// <summary>[v5.6.4] 커스텀 품질 키워드(비면 프리셋 코어 사용).</summary>
        public const string QualityCustom = "wan.guardrail.custom";    // 문자열
        /// <summary>[v5.7.3] 커스텀 FFmpeg 실행 파일 경로(비우면 시스템 PATH 탐색 · 데스크톱 실프레임 추출용).</summary>
        public const string FfmpegPath = "system.ffmpeg";              // 파일 경로(빈 문자열=PATH)
    }

    private const string Prefix = "sm.pref.";

    /// <summary>
    /// 목록 구분자 — 유닛 세퍼레이터(U+001F).
    /// ⚠ 쉼표를 쓰면 안 된다. 최근 검색어에 쉼표가 들어가는 순간 한 항목이
    ///   둘로 쪼개진다. 사용자가 칠 수 없는 문자여야 한다.
    /// ⚠ 리터럴로 직접 넣지 않는다. 비출력 문자는 편집기·병합 도구를 지나며
    ///   조용히 사라진다. 반드시 이스케이프로 쓴다.
    /// </summary>
    private const char Sep = '\u001F';
    private const int RecentMax = 6;

    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private IJSRuntime? _js;

    /// <summary>설정이 바뀌면 알린다. 구독자는 UI 스레드로 올려 처리할 것.</summary>
    public event Action? OnChange;

    /// <summary>
    /// <see cref="OnChange"/> 의 다른 이름. 호출부가 의미를 분명히 하고 싶을 때 쓴다.
    /// ⚠ 같은 이벤트다. 별도 이벤트를 만들면 한쪽만 구독한 화면이 갱신되지 않는다.
    /// </summary>
    public event Action? OnPreferenceChanged
    {
        add => OnChange += value;
        remove => OnChange -= value;
    }

    // ── 화면 토글 (사이드바 위젯이 쓴다) ─────────────────────────────────────
    //  ⚠ 테마는 <문서 루트의 data-theme> 가 유일한 스위치다(app.css 토큰이
    //    :root[data-theme] 로 갈린다). 여기서 별도 클래스를 만들지 않는다 —
    //    진실이 둘이 되면 반드시 어긋난다.

    /// <summary>라이트 테마인가.</summary>
    public bool IsThemeLight
    {
        get => Get(Keys.Theme) == "light";
        set => Set(Keys.Theme, value ? "light" : "dark");
    }

    /// <summary>사이드바만 밝게 쓰는가.</summary>
    public bool IsSidebarLight
    {
        get => GetBool(Keys.SidebarLight);
        set => SetBool(Keys.SidebarLight, value);
    }

    /// <summary>사용자가 모션을 껐는가(OS 설정과 별개).</summary>
    public bool IsMotionDisabled
    {
        get => GetBool(Keys.MotionOff);
        set => SetBool(Keys.MotionOff, value);
    }

    /// <summary>방송 스튜디오 시네마틱 테마 켜짐(기본 ON).</summary>
    public bool IsBroadcast
    {
        get => GetBool(Keys.Broadcast);
        set => SetBool(Keys.Broadcast, value);
    }

    // ── [v5.6.4] AI 비디오 품질/정체성 가드레일 ─────────────────────────────
    /// <summary>전역 품질 가드레일 사용(기본 ON). Off 면 렌더 요청에 시스템 개입 0.</summary>
    public bool EnableGlobalQualityGuardrail
    {
        get => GetBool(Keys.QualityGuardrail);
        set => SetBool(Keys.QualityGuardrail, value);
    }

    /// <summary>가드레일 강도 프리셋(기본 Balanced).</summary>
    public PromptFlowStudio.Services.Wan.QualityGuardrailPreset QualityGuardrailPreset
    {
        get => Enum.TryParse<PromptFlowStudio.Services.Wan.QualityGuardrailPreset>(Get(Keys.QualityPreset), true, out var p)
             ? p : PromptFlowStudio.Services.Wan.QualityGuardrailPreset.Balanced;
        set => Set(Keys.QualityPreset, value.ToString());
    }

    /// <summary>커스텀 품질 키워드(비면 프리셋 코어). 지정 시 프리셋 대신 이 문구를 결합.</summary>
    public string CustomQualityPrompt
    {
        get => Get(Keys.QualityCustom);
        set => Set(Keys.QualityCustom, value ?? "");
    }

    /// <summary>[v5.7.3] 커스텀 FFmpeg 실행 파일 경로(비우면 시스템 PATH). FfmpegFrameExtractor 가 우선 참조.</summary>
    public string FfmpegBinaryPath
    {
        get => Get(Keys.FfmpegPath);
        set => Set(Keys.FfmpegPath, value ?? "");
    }

    public bool Loaded { get; private set; }

    /// <summary>기본값. Reset 과 '값 없음' 이 같은 표를 본다.</summary>
    private static readonly Dictionary<string, string> Defaults = new(StringComparer.Ordinal)
    {
        [Keys.Theme] = "dark",
        [Keys.Density] = "comfortable",
        [Keys.SoundAlert] = "0",
        [Keys.HomePins] = "",
        [Keys.VisionLayout] = "4",
        [Keys.BioEmergencyOnly] = "0",
        [Keys.RecentCommands] = "",
        [Keys.RecentSearches] = "",
        // ⚠ 기본은 <쉬운 비유>다. 처음 보는 사람이 압도되면 뒤 설명이 안 들린다.
        [Keys.SimRawTerms] = "0",
        [Keys.SidebarLight] = "0",
        [Keys.MotionOff] = "0",
        [Keys.SafeZonePlatform] = "None",
        [Keys.CutCardSafeZone] = "0",
        [Keys.Broadcast] = "1",
        [Keys.QualityGuardrail] = "1",       // 기본 ON — 정체성/모션 품질 보장
        [Keys.QualityPreset] = "Balanced",
        [Keys.QualityCustom] = "",
        [Keys.FfmpegPath] = "",
    };

    /// <summary>
    /// 시작할 때 한 번 호출. 저장된 값을 전부 메모리로 끌어온다.
    ///
    /// <para>
    /// ⚠ 실패해도 예외를 던지지 않는다. 프라이빗 모드·사이트 데이터 차단에서
    ///   localStorage 접근 자체가 던지는데, 그것 때문에 앱이 안 뜨면 안 된다.
    ///   못 읽으면 전부 기본값으로 동작한다.
    /// </para>
    /// </summary>
    public async Task InitAsync(IJSRuntime js)
    {
        _js = js;
        foreach (var key in Defaults.Keys.ToList())
        {
            try
            {
                var v = await js.InvokeAsync<string?>("siteMaticStore.get", Prefix + key);
                if (v is not null) _cache[key] = v;
            }
            catch (Exception) { /* 무시 — 기본값으로 간다 */ }
        }
        Loaded = true;
        OnChange?.Invoke();
    }

    // ── 읽기 (동기) ──────────────────────────────────────────────────────────
    public string Get(string key)
        => _cache.TryGetValue(key, out var v) ? v
         : Defaults.TryGetValue(key, out var d) ? d : "";

    public bool GetBool(string key) => Get(key) == "1";

    public int GetInt(string key, int fallback)
        => int.TryParse(Get(key), out var v) ? v : fallback;

    public bool IsDefault(string key)
        => !_cache.ContainsKey(key) ||
           (Defaults.TryGetValue(key, out var d) && _cache[key] == d);

    /// <summary>기본값과 다른 항목 수 — '원복' 버튼에 배지로 띄운다.</summary>
    public int ChangedCount => Defaults.Keys.Count(k => !IsDefault(k));

    // ── 쓰기 (비동기 · 뒤에서) ───────────────────────────────────────────────
    public void Set(string key, string value)
    {
        if (Get(key) == value) return;      // 같은 값이면 알림도 저장도 하지 않는다
        _cache[key] = value;
        OnChange?.Invoke();
        _ = SaveAsync(key, value);
    }

    public void SetBool(string key, bool value) => Set(key, value ? "1" : "0");
    public void SetInt(string key, int value) => Set(key, value.ToString());

    private async Task SaveAsync(string key, string value)
    {
        if (_js is null) return;
        try { await _js.InvokeVoidAsync("siteMaticStore.set", Prefix + key, value); }
        catch (Exception) { /* 저장 실패해도 이번 세션은 메모리 값으로 동작한다 */ }
    }

    /// <summary>
    /// 전부 기본값으로.
    /// ⚠ '설정 꼬임' 은 실제로 자주 생긴다. 되돌릴 길이 없으면 사용자는
    ///   아무것도 건드리지 않게 되고, 그러면 편의 기능이 있으나 마나가 된다.
    /// </summary>
    public void Reset()
    {
        var keys = _cache.Keys.ToList();
        _cache.Clear();
        OnChange?.Invoke();
        _ = ResetAsync(keys);
    }

    private async Task ResetAsync(List<string> keys)
    {
        if (_js is null) return;
        foreach (var k in keys)
        {
            try { await _js.InvokeVoidAsync("siteMaticStore.remove", Prefix + k); }
            catch (Exception) { }
        }
    }

    // ── 최근 목록 ────────────────────────────────────────────────────────────
    public IReadOnlyList<string> GetRecent(string key)
        => Get(key).Split(Sep, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// 최근 목록 맨 앞에 넣는다. 이미 있으면 위로 끌어올린다.
    /// ⚠ 구분자로 쉼표를 쓰지 않는다. 검색어에 쉼표가 들어가면 항목이 쪼개진다.
    ///   출력 불가 문자(U+001F, Unit Separator)를 쓴다.
    /// </summary>
    public void PushRecent(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var list = GetRecent(key).Where(x => x != value).ToList();
        list.Insert(0, value);
        if (list.Count > RecentMax) list.RemoveRange(RecentMax, list.Count - RecentMax);
        Set(key, string.Join(Sep, list));
    }

    // ── 목록 값(핀 등) ───────────────────────────────────────────────────────
    public IReadOnlyList<string> GetList(string key)
        => Get(key).Split(Sep, StringSplitOptions.RemoveEmptyEntries);

    public bool InList(string key, string value) => GetList(key).Contains(value);

    public void ToggleInList(string key, string value)
    {
        var list = GetList(key).ToList();
        if (!list.Remove(value)) list.Add(value);
        Set(key, string.Join(Sep, list));
    }
}
