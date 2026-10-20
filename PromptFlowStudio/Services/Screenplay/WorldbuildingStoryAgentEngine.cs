using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Wan;   // GeminiClient

namespace PromptFlowStudio.Services.Screenplay;

[JsonConverter(typeof(JsonStringEnumConverter<StoryGenre>))]
public enum StoryGenre { Drama, Fantasy, SciFi, Romance, Thriller, Comedy, Family, Corporate }

[JsonConverter(typeof(JsonStringEnumConverter<CharacterRole>))]
public enum CharacterRole { Protagonist, Antagonist, Mentor, Ally, Foil, Narrator }

[JsonConverter(typeof(JsonStringEnumConverter<RelationKind>))]
public enum RelationKind { Ally, Rival, Family, Romance, Mentor, Enemy }

[JsonConverter(typeof(JsonStringEnumConverter<StoryAgent>))]
public enum StoryAgent { Loremaster, Writer, Director }

public sealed class WorldLore
{
    public string Title { get; set; } = "";
    public string Logline { get; set; } = "";
    public StoryGenre Genre { get; set; }
    public string Era { get; set; } = "";
    public string Setting { get; set; } = "";
    public string Tone { get; set; } = "";
    public string VisualMotif { get; set; } = "";
    public List<string> Rules { get; set; } = new();
    public List<string> Locations { get; set; } = new();
    public List<string> Factions { get; set; } = new();
    public List<string> Themes { get; set; } = new();
}

public sealed class CharacterProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public CharacterRole Role { get; set; }
    public string Archetype { get; set; } = "";
    public string Gender { get; set; } = "N";   // M/F/N
    public int Age { get; set; }
    public string Want { get; set; } = "";
    public string Need { get; set; } = "";
    public string Flaw { get; set; } = "";
    /// <summary>영상 프롬프트용 외형 묘사(영문 · 캐릭터 일관성).</summary>
    public string Look { get; set; } = "";
    /// <summary>감정 보이스 페르소나(mom/dad/pro · EmotionalVoiceSynthesizer).</summary>
    public string VoiceTone { get; set; } = "pro";
    /// <summary>막별 감정가(-1 절망 ~ 1 환희) — [1막, 2막 전반, 2막 후반, 3막].</summary>
    public List<float> EmotionArc { get; set; } = new();
}

public sealed class RelationshipEdge
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public RelationKind Kind { get; set; }
    /// <summary>-1(적대) ~ 1(유대).</summary>
    public float Strength { get; set; }
    public string Evolution { get; set; } = "";
}

public sealed record AgentTurn(StoryAgent Agent, int Round, string Text);

public sealed class WorldBible
{
    public string Id { get; set; } = "";
    public int SchemaVersion { get; set; } = 1;
    public string Premise { get; set; } = "";
    public string Source { get; set; } = "BCL-Agents";
    public WorldLore Lore { get; set; } = new();
    public List<CharacterProfile> Characters { get; set; } = new();
    public List<RelationshipEdge> Relationships { get; set; } = new();
    /// <summary>서사 아크 — 막별 요약(1막 · 2막 전반 · 2막 후반 · 3막).</summary>
    public List<string> Arc { get; set; } = new();
    public List<AgentTurn> Transcript { get; set; } = new();
    public int Rounds { get; set; }
    public double Consensus { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }

    public CharacterProfile? Find(string? id)
    {
        if (id is null) return null;
        foreach (var c in Characters) if (string.Equals(c.Id, id, StringComparison.Ordinal)) return c;
        return null;
    }

    public CharacterProfile? Protagonist => Characters.Find(static c => c.Role == CharacterRole.Protagonist);
}

public sealed record GraphNode(string Id, string Label, CharacterRole Role, float X, float Y, int Degree, float Centrality);

/// <summary>
/// [PromptFlow Studio v16.35.0] 멀티 에이전트 세계관 &amp; 캐릭터 서사 생성기.
///   ▸ BCL 에이전트 토론(결정적 시드): 세계관(Loremaster) 제안 → 작가(Writer) 인물/관계 → 감독(Director) 검수(시각 실현성 · 갈등 · 아크 변화폭 · 장소 수)
///     → 지적 사항 반영 수정 라운드(최대 3) · 합의도 = 통과 검사 비율 · 토론 기록 보존.
///   ▸ Gemini(설정 시) JSON 스키마 생성 → 즉시 검증 → 실패/부적합 시 BCL 에이전트 폴백(원인 기록).
///   ▸ 관계 그래프: 차수/가중 중심성 · 원형 레이아웃(주인공 중앙).
///   ▸ 영구 가이드: WorldBible JSON(System.Text.Json · 원자 교체) 저장/목록/로드 — 대본·스토리보드 변환이 공유.
/// </summary>
public sealed class WorldbuildingStoryAgentEngine
{
    public const int MaxRounds = 3;
    public const int MaxCharacters = 8;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly GeminiClient? _gemini;
    private readonly string _folder;
    private int _busy;

    public WorldbuildingStoryAgentEngine(IServiceProvider sp)
        : this(sp.GetService(typeof(GeminiClient)) as GeminiClient, null) { }

    public WorldbuildingStoryAgentEngine(GeminiClient? gemini, string? folder)
    {
        _gemini = gemini;
        _folder = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", "Worldbuilding");
    }

    public event Action? OnChange;

    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public bool LlmAvailable => _gemini?.Enabled == true;
    public string Folder => _folder;
    public WorldBible? Current { get; private set; }
    public string? LastAction { get; private set; }

    public void SetCurrent(WorldBible? b) { Current = b; Raise(); }

    // ══════════════════════ 장르 · 시드 ══════════════════════

    private static readonly (StoryGenre G, string[] W)[] GenreWords =
    {
        (StoryGenre.Fantasy, new[] { "마법", "용", "왕국", "기사", "마녀", "fantasy", "dragon", "magic", "kingdom", "elf" }),
        (StoryGenre.SciFi, new[] { "우주", "로봇", "ai", "미래", "화성", "사이버", "space", "robot", "future", "cyber", "alien" }),
        (StoryGenre.Romance, new[] { "사랑", "연애", "첫사랑", "고백", "love", "romance", "date", "crush" }),
        (StoryGenre.Thriller, new[] { "살인", "추격", "범인", "음모", "실종", "murder", "chase", "conspiracy", "missing", "detective", "형사" }),
        (StoryGenre.Comedy, new[] { "웃긴", "코미디", "소동", "엉뚱", "comedy", "funny", "prank" }),
        (StoryGenre.Family, new[] { "아기", "엄마", "아빠", "가족", "육아", "baby", "mom", "dad", "family", "toddler" }),
        (StoryGenre.Corporate, new[] { "회사", "스타트업", "브랜드", "제품", "출시", "company", "startup", "brand", "product", "launch" }),
    };

    public static StoryGenre DetectGenre(string? premise)
    {
        var t = (premise ?? "").ToLowerInvariant();
        var best = StoryGenre.Drama; var bestScore = 0;
        foreach (var (g, words) in GenreWords)
        {
            var s = 0;
            foreach (var w in words) if (t.Contains(w, StringComparison.Ordinal)) s++;
            if (s > bestScore) { bestScore = s; best = g; }
        }
        return best;
    }

    public static int Seed(string? s)
    {
        unchecked { var h = 2166136261u; foreach (var c in s ?? "") { h ^= c; h *= 16777619u; } return (int)(h & 0x7FFFFFFF); }
    }

    private static T Pick<T>(IReadOnlyList<T> list, int seed, int salt) => list[(int)((uint)(seed * 31 + salt * 7919) % (uint)list.Count)];

    // ══════════════════════ 장르 템플릿 ══════════════════════

    private sealed record GenreKit(string Era, string Setting, string Tone, string Motif, string[] Rules, string[] Locations, string[] Factions, string[] Themes, string[] MaleNames, string[] FemaleNames, string Look);

    private static GenreKit Kit(StoryGenre g) => g switch
    {
        StoryGenre.Fantasy => new("고대 왕국력 812년", "부유섬이 떠 있는 에테르 대륙", "장엄하고 신비로운", "푸른 룬 빛", new[] { "마법은 기억을 대가로 쓴다", "용과의 계약은 혈통으로만 이어진다" },
            new[] { "부유섬 왕성", "안개 숲", "룬 도서관", "용의 협곡", "폐허 신전" }, new[] { "왕실 근위대", "룬 학파", "그림자 길드" }, new[] { "희생", "운명", "용기" },
            new[] { "카이렌", "도윤", "레온", "아르텐" }, new[] { "세라", "이엘", "미르", "하린" }, "fantasy costume, intricate embroidered cloak"),
        StoryGenre.SciFi => new("서기 2147년", "궤도 링 도시 네오 서울", "차갑고 긴장감 있는", "청록 홀로그램", new[] { "모든 시민은 신경 링크로 연결된다", "AI는 거짓말을 할 수 없다" },
            new[] { "궤도 링 하층 시장", "관제탑 브리지", "폐쇄된 연구 모듈", "무중력 정원", "화물 도크" }, new[] { "링 관리국", "자유 해커 연합", "넥서스 코퍼레이션" }, new[] { "인간성", "자유", "기억" },
            new[] { "제이", "현우", "타오", "레이" }, new[] { "노바", "서아", "미카", "유나" }, "sleek sci-fi suit with glowing seams"),
        StoryGenre.Romance => new("현재", "벚꽃이 지는 바닷가 소도시", "따뜻하고 설레는", "노을빛 골든아워", new[] { "마음은 말보다 행동으로 드러난다", "같은 계절은 두 번 오지 않는다" },
            new[] { "바닷가 카페", "오래된 서점", "기차역 플랫폼", "옥상 정원", "등대 언덕" }, new[] { "카페 단골들", "서점 가족" }, new[] { "용기", "타이밍", "성장" },
            new[] { "지훈", "태오", "민재", "선우" }, new[] { "수아", "하은", "윤서", "다인" }, "casual cozy knitwear, soft natural makeup"),
        StoryGenre.Thriller => new("현재", "비 내리는 항구 도시의 밤", "어둡고 숨막히는", "네온 반사광과 빗줄기", new[] { "모든 증거는 48시간 안에 사라진다", "누구도 완전히 믿을 수 없다" },
            new[] { "비 내리는 골목", "경찰서 취조실", "버려진 창고", "고층 펜트하우스", "항구 컨테이너 야적장" }, new[] { "강력계", "항만 카르텔", "언론사" }, new[] { "진실", "죄책감", "정의" },
            new[] { "강혁", "서진", "도경", "태석" }, new[] { "유진", "채원", "소희", "지민" }, "dark trench coat, wet hair, sharp eyes"),
        StoryGenre.Comedy => new("현재", "엉뚱한 사람들이 모인 동네 편의점", "유쾌하고 경쾌한", "선명한 원색 소품", new[] { "계획은 반드시 엇나간다", "진심은 결국 통한다" },
            new[] { "동네 편의점", "아파트 놀이터", "가족 단톡방 화면", "결혼식장", "동네 축제" }, new[] { "편의점 알바단", "아파트 부녀회" }, new[] { "우정", "엉뚱함", "화해" },
            new[] { "만수", "동철", "봉구", "재석" }, new[] { "말순", "보람", "나래", "은비" }, "colorful casual outfit, expressive face"),
        StoryGenre.Family => new("현재", "햇살 가득한 신혼집", "사랑스럽고 따뜻한", "부드러운 파스텔 햇살", new[] { "아기의 첫 순간은 다시 오지 않는다", "가족은 서로의 속도를 기다린다" },
            new[] { "아기방", "거실 소파", "동네 공원", "할머니 댁 마당", "욕실 물놀이" }, new[] { "우리 가족", "조부모님" }, new[] { "성장", "사랑", "처음" },
            new[] { "아빠 준호", "할아버지", "삼촌 민석" }, new[] { "엄마 지은", "아기 하린", "할머니" }, "cozy home clothes, warm soft lighting"),
        StoryGenre.Corporate => new("현재", "출시를 앞둔 스타트업 사무실", "몰입감 있고 희망적인", "브랜드 컬러 포인트 조명", new[] { "데모데이까지 남은 시간은 7일", "고객의 한 문장이 제품을 바꾼다" },
            new[] { "오픈 오피스", "회의실 화이트보드", "고객 인터뷰 카페", "런칭 무대", "새벽 옥상" }, new[] { "창업팀", "투자사", "경쟁사" }, new[] { "도전", "신뢰", "혁신" },
            new[] { "대표 민혁", "개발자 성훈", "투자자 정우" }, new[] { "디자이너 소연", "마케터 예린", "고객 미진" }, "smart casual office wear, clean modern style"),
        _ => new("현재", "변화의 기로에 선 작은 도시", "진솔하고 여운 있는", "흐린 날의 부드러운 확산광", new[] { "과거의 선택이 현재를 붙잡는다", "용서는 스스로에게서 시작된다" },
            new[] { "오래된 집", "병원 복도", "강변 산책로", "작업실", "버스 정류장" }, new[] { "가족", "동네 사람들" }, new[] { "용서", "상실", "회복" },
            new[] { "정우", "상현", "기태", "준서" }, new[] { "은재", "미영", "소라", "가윤" }, "natural everyday clothing, realistic"),
    };

    // ══════════════════════ BCL 에이전트 토론 ══════════════════════

    /// <summary>프리미스 → 세계관 바이블(결정적 · 동일 입력 = 동일 결과).</summary>
    public static WorldBible Debate(string premise, int characterCount = 4, StoryGenre? genre = null)
    {
        var p = (premise ?? "").Trim();
        if (p.Length == 0) p = "평범한 주인공이 예상치 못한 기회를 만나 성장하는 이야기";
        var seed = Seed(p);
        var g = genre ?? DetectGenre(p);
        var kit = Kit(g);
        var n = Math.Clamp(characterCount, 2, MaxCharacters);
        var b = new WorldBible { Id = "wb_" + seed.ToString("x8", CultureInfo.InvariantCulture), Premise = p, CreatedUtc = DateTimeOffset.UtcNow };

        // ── 라운드 1: 세계관 제안 ──
        var lore = b.Lore;
        lore.Genre = g; lore.Era = kit.Era; lore.Setting = kit.Setting; lore.Tone = kit.Tone; lore.VisualMotif = kit.Motif;
        lore.Rules.AddRange(kit.Rules);
        var locCount = 3 + seed % 3;
        for (var i = 0; i < locCount && i < kit.Locations.Length; i++) lore.Locations.Add(kit.Locations[(seed / 7 + i) % kit.Locations.Length]);
        lore.Factions.AddRange(kit.Factions);
        lore.Themes.AddRange(kit.Themes);
        lore.Title = TitleFrom(p, kit, seed);
        lore.Logline = Logline(p, kit);
        b.Transcript.Add(new(StoryAgent.Loremaster, 1, "세계관 제안 — " + lore.Setting + " · " + lore.Era + " · 규칙 " + lore.Rules.Count.ToString(CultureInfo.InvariantCulture) + "개 · 장소 " + lore.Locations.Count.ToString(CultureInfo.InvariantCulture) + "곳"));

        // ── 라운드 1: 작가 인물/관계 ──
        CharacterRole[] roles = { CharacterRole.Protagonist, CharacterRole.Mentor, CharacterRole.Ally, CharacterRole.Foil, CharacterRole.Ally, CharacterRole.Ally, CharacterRole.Foil, CharacterRole.Narrator };
        if (seed % 2 == 0 && n >= 3) roles[2] = CharacterRole.Antagonist;   // 의도적 결함 가능성: 짝수 시드만 적대자 포함 → 감독 검수 경로 양쪽 검증
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < n; i++)
        {
            var female = ((seed >> i) & 1) == 0;
            var pool = female ? kit.FemaleNames : kit.MaleNames;
            var name = Pick(pool, seed, i);
            for (var k = 1; used.Contains(name) && k < 8; k++) name = Pick(pool, seed, i + k * 13);
            if (!used.Add(name)) { name += " " + (i + 1).ToString(CultureInfo.InvariantCulture); used.Add(name); }
            b.Characters.Add(MakeCharacter("c" + (i + 1).ToString(CultureInfo.InvariantCulture), name, roles[i], female ? "F" : "M", kit, seed, i, p));
        }
        var pro = b.Characters[0];
        for (var i = 1; i < b.Characters.Count; i++)
        {
            var c = b.Characters[i];
            var (kind, s) = c.Role switch
            {
                CharacterRole.Mentor => (RelationKind.Mentor, 0.7f),
                CharacterRole.Antagonist => (RelationKind.Enemy, -0.8f),
                CharacterRole.Foil => (RelationKind.Rival, -0.3f),
                CharacterRole.Narrator => (RelationKind.Ally, 0.2f),
                _ => (g == StoryGenre.Romance && i == 2 ? RelationKind.Romance : g == StoryGenre.Family ? RelationKind.Family : RelationKind.Ally, 0.5f),
            };
            b.Relationships.Add(new RelationshipEdge { From = pro.Id, To = c.Id, Kind = kind, Strength = s, Evolution = Evolution(kind) });
        }
        if (b.Characters.Count >= 4) b.Relationships.Add(new RelationshipEdge { From = b.Characters[2].Id, To = b.Characters[3].Id, Kind = RelationKind.Rival, Strength = -0.2f, Evolution = "경쟁 → 공동 목표 앞에서 협력" });
        b.Transcript.Add(new(StoryAgent.Writer, 1, "인물 " + b.Characters.Count.ToString(CultureInfo.InvariantCulture) + "명 · 관계 " + b.Relationships.Count.ToString(CultureInfo.InvariantCulture) + "개 제안 — 주인공 " + pro.Name + "(" + pro.Want + ")"));

        // ── 감독 검수 ↔ 수정 라운드 ──
        var round = 1;
        List<string> issues;
        while (true)
        {
            issues = Review(b);
            var total = 5;
            b.Consensus = Math.Round((total - Math.Min(total, issues.Count)) / (double)total, 2);
            b.Transcript.Add(new(StoryAgent.Director, round, issues.Count == 0 ? "검수 통과 — 시각 실현성·갈등·아크 모두 충족" : "지적 " + issues.Count.ToString(CultureInfo.InvariantCulture) + "건: " + string.Join(" / ", issues)));
            if (issues.Count == 0 || round >= MaxRounds) break;
            round++;
            Revise(b, issues, kit, seed);
            b.Transcript.Add(new(StoryAgent.Writer, round, "수정 반영 — " + string.Join(", ", issues)));
        }
        b.Rounds = round;
        b.Arc.AddRange(new[]
        {
            "1막 — " + pro.Name + "은(는) " + lore.Locations[0] + "에서 '" + pro.Want + "'을(를) 원하지만 " + pro.Flaw + " 때문에 머뭇거린다.",
            "2막 전반 — " + lore.Rules[0] + "는 사실이 드러나며 판이 커지고, 동료와의 관계가 시험대에 오른다.",
            "2막 후반 — 가장 큰 상실의 순간, " + pro.Name + "은(는) 진짜 필요한 것이 '" + pro.Need + "'임을 깨닫는다.",
            "3막 — " + lore.Locations[^1] + "에서의 최후 선택으로 " + lore.Themes[0] + "을(를) 증명한다.",
        });
        return b;
    }

    private static CharacterProfile MakeCharacter(string id, string name, CharacterRole role, string gender, GenreKit kit, int seed, int i, string premise)
    {
        string[] wants = { "인정받는 것", "잃어버린 것을 되찾는 것", "진실을 밝히는 것", "자유를 얻는 것", "사랑하는 사람을 지키는 것" };
        string[] needs = { "스스로를 믿는 용기", "타인에게 기대는 법", "과거를 놓아주는 것", "솔직해지는 것", "책임을 받아들이는 것" };
        string[] flaws = { "두려움", "고집", "지나친 자존심", "불신", "조급함" };
        string[] ages = { "teen", "young adult", "adult", "middle-aged", "elderly" };
        var age = role switch { CharacterRole.Mentor => 58, CharacterRole.Narrator => 40, _ => 19 + (seed / (i + 3)) % 22 };
        var ageWord = ages[Math.Clamp(age / 15 - 1, 0, 4)];
        var arc = role switch
        {
            CharacterRole.Protagonist => new List<float> { -0.2f, 0.3f, -0.7f, 0.8f },
            CharacterRole.Antagonist => new List<float> { 0.6f, 0.7f, 0.4f, -0.6f },
            CharacterRole.Mentor => new List<float> { 0.3f, 0.2f, -0.4f, 0.5f },
            _ => new List<float> { 0.1f, 0.4f, -0.3f, 0.6f },
        };
        return new CharacterProfile
        {
            Id = id, Name = name, Role = role, Gender = gender, Age = age,
            Archetype = role switch
            {
                CharacterRole.Protagonist => "영웅", CharacterRole.Antagonist => "그림자", CharacterRole.Mentor => "현자",
                CharacterRole.Foil => "거울", CharacterRole.Narrator => "관찰자", _ => "동반자",
            },
            Want = role == CharacterRole.Protagonist && premise.Length > 0 ? Pick(wants, seed, i) : Pick(wants, seed, i + 3),
            Need = Pick(needs, seed, i), Flaw = Pick(flaws, seed, i + 1),
            Look = (gender == "F" ? "a " + ageWord + " woman" : "a " + ageWord + " man") + ", " + kit.Look,
            VoiceTone = role is CharacterRole.Narrator or CharacterRole.Mentor ? "pro" : gender == "F" ? "mom" : "dad",
            EmotionArc = arc,
        };
    }

    private static string Evolution(RelationKind k) => k switch
    {
        RelationKind.Mentor => "가르침 → 갈등 → 스승의 희생으로 계승",
        RelationKind.Enemy => "위협 → 정면 충돌 → 최종 대결",
        RelationKind.Rival => "경쟁 → 서로를 인정",
        RelationKind.Romance => "어색함 → 설렘 → 위기 → 고백",
        RelationKind.Family => "당연함 → 소중함의 재발견",
        _ => "신뢰 → 시험 → 더 깊은 유대",
    };

    private static string TitleFrom(string premise, GenreKit kit, int seed)
    {
        var words = premise.Split(new[] { ' ', ',', '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);
        var key = words.Length > 0 ? words[seed % words.Length] : kit.Themes[0];
        if (key.Length > 12) key = key[..12];
        return key + "의 " + kit.Themes[seed % kit.Themes.Length];
    }

    private static string Logline(string premise, GenreKit kit)
    {
        var p = premise.Length > 80 ? premise[..80] + "…" : premise;
        return kit.Setting + "에서, " + p.TrimEnd('.') + " — " + kit.Themes[0] + "에 관한 이야기.";
    }

    /// <summary>감독 검수(시각 실현성 · 갈등 · 아크 · 관계 연결 · 외형) → 지적 목록(빈 목록 = 통과).</summary>
    public static List<string> Review(WorldBible b)
    {
        var issues = new List<string>(5);
        if (b.Lore.Locations.Count is < 2 or > 5) issues.Add("장소 수 2~5 필요(촬영 실현성)");
        var conflict = false;
        foreach (var e in b.Relationships) if (e.Strength <= -0.5f) { conflict = true; break; }
        if (!conflict) issues.Add("중심 갈등(적대 관계) 부족");
        var pro = b.Protagonist;
        if (pro is null || pro.EmotionArc.Count < 4) issues.Add("주인공 감정 아크 누락");
        else
        {
            float min = 1, max = -1;
            foreach (var v in pro.EmotionArc) { min = MathF.Min(min, v); max = MathF.Max(max, v); }
            if (max - min < 1.2f) issues.Add("주인공 감정 변화폭 부족(≥1.2)");
        }
        var linked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in b.Relationships) { linked.Add(e.From); linked.Add(e.To); }
        foreach (var c in b.Characters) if (!linked.Contains(c.Id)) { issues.Add("고립 인물: " + c.Name); break; }
        foreach (var c in b.Characters) if (string.IsNullOrWhiteSpace(c.Look)) { issues.Add("외형 묘사 누락: " + c.Name); break; }
        return issues;
    }

    private static void Revise(WorldBible b, List<string> issues, GenreKit kit, int seed)
    {
        foreach (var issue in issues)
        {
            if (issue.StartsWith("장소", StringComparison.Ordinal))
            {
                while (b.Lore.Locations.Count > 5) b.Lore.Locations.RemoveAt(b.Lore.Locations.Count - 1);
                for (var i = 0; b.Lore.Locations.Count < 2 && i < kit.Locations.Length; i++) if (!b.Lore.Locations.Contains(kit.Locations[i])) b.Lore.Locations.Add(kit.Locations[i]);
            }
            else if (issue.StartsWith("중심 갈등", StringComparison.Ordinal))
            {
                var pro = b.Protagonist!;
                CharacterProfile? target = null;
                foreach (var c in b.Characters) if (c.Role == CharacterRole.Foil) { target = c; break; }
                if (target is null)
                {
                    var name = Pick(kit.MaleNames, seed, 99);
                    target = MakeCharacter("c" + (b.Characters.Count + 1).ToString(CultureInfo.InvariantCulture), name + " (적)", CharacterRole.Antagonist, "M", kit, seed, 9, b.Premise);
                    if (b.Characters.Count < MaxCharacters) b.Characters.Add(target);
                }
                target.Role = CharacterRole.Antagonist; target.Archetype = "그림자";
                target.EmotionArc = new List<float> { 0.6f, 0.7f, 0.4f, -0.6f };
                var edge = b.Relationships.Find(e => e.From == pro.Id && e.To == target.Id);
                if (edge is null) b.Relationships.Add(edge = new RelationshipEdge { From = pro.Id, To = target.Id });
                edge.Kind = RelationKind.Enemy; edge.Strength = -0.8f; edge.Evolution = Evolution(RelationKind.Enemy);
            }
            else if (issue.StartsWith("주인공", StringComparison.Ordinal) && b.Protagonist is { } pr) pr.EmotionArc = new List<float> { -0.2f, 0.3f, -0.7f, 0.8f };
            else if (issue.StartsWith("고립", StringComparison.Ordinal) && b.Protagonist is { } p2)
            {
                foreach (var c in b.Characters)
                {
                    if (c.Id == p2.Id || b.Relationships.Exists(e => e.From == c.Id || e.To == c.Id)) continue;
                    b.Relationships.Add(new RelationshipEdge { From = p2.Id, To = c.Id, Kind = RelationKind.Ally, Strength = 0.4f, Evolution = Evolution(RelationKind.Ally) });
                }
            }
            else if (issue.StartsWith("외형", StringComparison.Ordinal))
                foreach (var c in b.Characters) if (string.IsNullOrWhiteSpace(c.Look)) c.Look = "a person, " + kit.Look;
        }
    }

    // ══════════════════════ 그래프 ══════════════════════

    /// <summary>원형 레이아웃(주인공 중앙 · 정규화 -1~1) + 차수 · |강도| 가중 중심성(0~1).</summary>
    public static List<GraphNode> LayoutGraph(WorldBible b)
    {
        var deg = new Dictionary<string, int>(StringComparer.Ordinal);
        var w = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var e in b.Relationships)
        {
            deg[e.From] = deg.GetValueOrDefault(e.From) + 1; deg[e.To] = deg.GetValueOrDefault(e.To) + 1;
            w[e.From] = w.GetValueOrDefault(e.From) + MathF.Abs(e.Strength); w[e.To] = w.GetValueOrDefault(e.To) + MathF.Abs(e.Strength);
        }
        var maxW = 1e-6f;
        foreach (var v in w.Values) maxW = MathF.Max(maxW, v);
        var nodes = new List<GraphNode>(b.Characters.Count);
        var others = b.Characters.Count - 1;
        var k = 0;
        foreach (var c in b.Characters)
        {
            float x = 0, y = 0;
            if (c.Role != CharacterRole.Protagonist || nodes.Exists(static n => n.Role == CharacterRole.Protagonist))
            {
                var a = 2 * MathF.PI * k / Math.Max(1, others) - MathF.PI / 2;
                x = 0.8f * MathF.Cos(a); y = 0.8f * MathF.Sin(a); k++;
            }
            nodes.Add(new(c.Id, c.Name, c.Role, x, y, deg.GetValueOrDefault(c.Id), w.GetValueOrDefault(c.Id) / maxW));
        }
        return nodes;
    }

    // ══════════════════════ LLM ══════════════════════

    public static string BuildLlmInstruction(string premise, int characters) =>
        "You are a writers' room of three agents (Loremaster, Writer, Director) debating a story world. Premise: \"" + premise.Replace("\"", "'", StringComparison.Ordinal) + "\".\n" +
        "Return ONLY JSON: {\"lore\":{\"title\",\"logline\",\"genre\"(Drama|Fantasy|SciFi|Romance|Thriller|Comedy|Family|Corporate),\"era\",\"setting\",\"tone\",\"visualMotif\",\"rules\":[],\"locations\":[2-5],\"factions\":[],\"themes\":[]}," +
        "\"characters\":[{\"id\",\"name\",\"role\"(Protagonist|Antagonist|Mentor|Ally|Foil|Narrator),\"archetype\",\"gender\"(M|F|N),\"age\",\"want\",\"need\",\"flaw\",\"look\"(English visual description),\"voiceTone\"(mom|dad|pro),\"emotionArc\":[4 floats -1..1]}] (" +
        characters.ToString(CultureInfo.InvariantCulture) + " characters, exactly one Protagonist)," +
        "\"relationships\":[{\"from\",\"to\",\"kind\"(Ally|Rival|Family|Romance|Mentor|Enemy),\"strength\"(-1..1),\"evolution\"}],\"arc\":[4 strings],\"transcript\":[{\"agent\",\"round\",\"text\"}]}. Write narrative fields in Korean.";

    /// <summary>LLM JSON → 바이블(스키마/값 검증 실패 null).</summary>
    public static WorldBible? ParseLlmJson(string? text, string premise)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            var b = JsonSerializer.Deserialize<WorldBible>(GeminiClient.StripFence(text), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (b is null || string.IsNullOrWhiteSpace(b.Lore.Title) || b.Characters.Count is < 2 or > MaxCharacters || b.Lore.Locations.Count == 0) return null;
            var pros = 0;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in b.Characters)
            {
                if (string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Name) || !ids.Add(c.Id)) return null;
                if (c.Role == CharacterRole.Protagonist) pros++;
                if (c.VoiceTone is not ("mom" or "dad" or "pro")) c.VoiceTone = "pro";
                for (var i = 0; i < c.EmotionArc.Count; i++) c.EmotionArc[i] = Math.Clamp(c.EmotionArc[i], -1f, 1f);
            }
            if (pros != 1) return null;
            b.Relationships.RemoveAll(e => !ids.Contains(e.From) || !ids.Contains(e.To) || e.From == e.To);
            foreach (var e in b.Relationships) e.Strength = Math.Clamp(e.Strength, -1f, 1f);
            b.Premise = premise; b.Source = "Gemini"; b.Id = "wb_" + Seed(premise).ToString("x8", CultureInfo.InvariantCulture); b.CreatedUtc = DateTimeOffset.UtcNow;
            b.Rounds = Math.Max(1, b.Rounds);
            var issues = Review(b);
            b.Consensus = Math.Round((5 - Math.Min(5, issues.Count)) / 5.0, 2);
            return b;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>LLM 우선(설정 시) → 검증 실패/오류 시 BCL 에이전트 토론 폴백.</summary>
    public async Task<WorldBible> BuildAsync(string premise, int characters = 4, bool useLlm = true, CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _busy, 1);
        LastAction = "🌍 세계관 토론 중";
        Raise();
        try
        {
            WorldBible? b = null;
            string? note = null;
            if (useLlm && _gemini is { Enabled: true } g)
            {
                using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
                d.CancelAfter(TimeSpan.FromSeconds(60));
                try
                {
                    var r = await g.GenerateAsync(BuildLlmInstruction(premise, characters), null, 0.8, d.Token).ConfigureAwait(false);
                    b = r.Ok ? ParseLlmJson(r.Text, premise) : null;
                    if (b is null) note = r.Ok ? "LLM 스키마 불일치 → BCL 폴백" : "LLM 실패(" + r.Message + ") → BCL 폴백";
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { note = "LLM 시간 초과 → BCL 폴백"; }
            }
            b ??= Debate(premise, characters);
            if (note is not null) b.Transcript.Add(new(StoryAgent.Director, 0, note));
            Current = b;
            LastAction = "🌍 " + b.Lore.Title + " · 인물 " + b.Characters.Count.ToString(CultureInfo.InvariantCulture) + " · 합의 " + (b.Consensus * 100).ToString("0", CultureInfo.InvariantCulture) + "% · " + b.Source;
            return b;
        }
        finally { Volatile.Write(ref _busy, 0); Raise(); }
    }

    // ══════════════════════ 영구 저장 ══════════════════════

    public static string Serialize(WorldBible b) => JsonSerializer.Serialize(b, Json);

    public static WorldBible? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<WorldBible>(json, Json); }
        catch (JsonException) { return null; }
    }

    public string Save(WorldBible b)
    {
        Directory.CreateDirectory(_folder);
        var safe = new StringBuilder(b.Id.Length);
        foreach (var ch in b.Id) safe.Append(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_');
        var path = Path.Combine(_folder, (safe.Length == 0 ? "world" : safe.ToString()) + ".json");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Serialize(b), new UTF8Encoding(false));
        File.Move(tmp, path, true);
        LastAction = "💾 세계관 저장 · " + Path.GetFileName(path);
        Raise();
        return path;
    }

    public IReadOnlyList<string> List()
    {
        if (!Directory.Exists(_folder)) return Array.Empty<string>();
        var files = Directory.GetFiles(_folder, "*.json");
        Array.Sort(files, StringComparer.Ordinal);
        return files;
    }

    public WorldBible? Load(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(Path.GetFullPath(_folder), StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return null;
            var b = Deserialize(File.ReadAllText(full));
            if (b is not null) { Current = b; LastAction = "📂 세계관 로드 · " + b.Lore.Title; Raise(); }
            return b;
        }
        catch (Exception ex) { Program.Log("World.Load", ex); return null; }
    }

    private void Raise()
    {
        var h = OnChange;
        if (h is null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action)d)(); } catch (Exception ex) { Program.Log("World.OnChange", ex); }
        }
    }
}
