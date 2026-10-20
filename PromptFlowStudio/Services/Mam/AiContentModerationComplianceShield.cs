using System.Globalization;

namespace PromptFlowStudio.Services.Mam;

public enum ComplianceRegion : byte { US, KR, EU, UK, JP }

public enum ComplianceStatus : byte { Approved, Warning, Rejected }

public enum BrandSafetyTier : byte { Safe, Low, Medium, High, Floor }

[Flags]
public enum ComplianceIssue : ulong
{
    None = 0,
    UsObscenity = 1UL << 0, UsSafeHarbor = 1UL << 1, KrYouthHarmful = 1UL << 2, KrSmokingAlcohol = 1UL << 3, KrPpl = 1UL << 4, KrGambling = 1UL << 5,
    EuPplDisclosure = 1UL << 6, AiDisclosure = 1UL << 7, UkWatershed = 1UL << 8, JpViolence = 1UL << 9, HateSpeech = 1UL << 10, SelfHarm = 1UL << 11,
    SelfHarmSevere = 1UL << 12, MinorsSexualized = 1UL << 13, DeepfakeAbuse = 1UL << 14, CopyrightMatch = 1UL << 15, PoliticalDeepfake = 1UL << 16, Profanity = 1UL << 17,
}

/// <summary>콘텐츠 신호(값형 · 0~1 강도 · 메타 플래그) — 텍스트 분석/수동 입력/모델 결과 병합.</summary>
public struct ContentSignals
{
    public float Violence, Sexual, Horror, Drugs, Gambling, Hate, SelfHarm, Smoking, Alcohol, Weapons, Crime;
    public int ProfanityCount, ProductPlacements;
    public double DurationSec;
    public bool PplDisclosed, SyntheticMedia, SyntheticFace, RealPersonLikeness, ConsentOnFile, C2paSigned, AiLabelShown, MinorsPresent, Political;
    public ulong PHash, PHash2;
    public bool HasPHash;
    public MamAssetKind Kind;
}

public struct RegionVerdict
{
    public ComplianceRegion Region;
    public int RatingIndex;
    public ComplianceStatus Status;
    public ComplianceIssue Issues;
}

public struct ComplianceSummary
{
    public int BrandRisk;
    public BrandSafetyTier BrandTier;
    public int CopyrightIndex, CopyrightDistance;
    public ComplianceIssue Issues;
    public int Approved, Warned, Rejected;
}

public sealed record ComplianceReport(string AssetId, string Title, IReadOnlyList<(ComplianceRegion Region, string Rating, ComplianceStatus Status, IReadOnlyList<string> Issues)> Regions,
    int BrandRisk, BrandSafetyTier BrandTier, string? CopyrightOwner, IReadOnlyList<ComplianceRegion> ApprovedRegions, DateTimeOffset CheckedUtc);

/// <summary>
/// [PromptFlow Studio v16.42.0] 🛡 AI 글로벌 심의/저작권 컴플라이언스 쉴드.
///   ▸ 텍스트(대본 · 설명 · 자막) 렉시콘 스캔: 토큰 FNV-64 → 범주별 정렬 해시 배열 이진 탐색(한국어 어미/조사 1~2자 절삭) — 무할당.
///   ▸ 국가 규정: 미국 FCC(외설 차단 · 세이프하버 22~06시) · 한국 KCSC(청소년 유해 · 흡연/음주 미화 · 간접광고 · 사행성) · EU AVMSD/AI Act(PPL 고지 · 합성물 표시) · 영국 Ofcom(21시 워터셰드) · 일본 BPO(폭력).
///     공통 차단: 혐오 · 미성년 성적 대상화 · 동의 없는 실존 인물 딥페이크 · 정치 딥페이크(KR/EU) · 등록 저작물 지각 핑거프린트 일치(라이선스 제외).
///   ▸ 지역별 연령 등급(전체/12/15/19 · TV-G/PG/14/MA · 0/12/16/18 · U/PG/15/18 · G/PG12/R15+/R18+) · GARM 브랜드 안전 점수/티어 — Diagnose 는 Span 출력 무할당.
/// </summary>
public sealed class AiContentModerationComplianceShield
{
    public const int Regions = 5, CopyrightThreshold64 = 6, CopyrightThreshold128 = 14;
    public static readonly ComplianceRegion[] AllRegions = { ComplianceRegion.US, ComplianceRegion.KR, ComplianceRegion.EU, ComplianceRegion.UK, ComplianceRegion.JP };
    private static readonly string[][] RatingLabels =
    {
        new[] { "TV-G", "TV-PG", "TV-14", "TV-MA" }, new[] { "전체관람가", "12세", "15세", "청소년관람불가" }, new[] { "0+", "12+", "16+", "18+" },
        new[] { "U", "PG", "15", "18" }, new[] { "G", "PG12", "R15+", "R18+" },
    };

    private const int CatViolence = 0, CatSexual = 1, CatHorror = 2, CatDrugs = 3, CatGambling = 4, CatHate = 5, CatSelfHarm = 6, CatSmoking = 7, CatAlcohol = 8, CatWeapons = 9, CatCrime = 10, CatProfanity = 11, Cats = 12;
    private static readonly ulong[][] Lexicon = BuildLexicon();

    private readonly object _gate = new();
    private ulong[] _cpHash = new ulong[64], _cpHash2 = new ulong[64];
    private MamAssetKind[] _cpKind = new MamAssetKind[64];
    private bool[] _cpLicensed = new bool[64];
    private readonly List<string> _cpOwner = new();
    private int _cpCount;
    private readonly Dictionary<string, ComplianceReport> _reports = new(StringComparer.Ordinal);

    public event Action? OnChange;
    public long Diagnoses { get; private set; }
    public int RegisteredWorks => _cpCount;
    public string? LastAction { get; private set; }

    private static ulong[][] BuildLexicon()
    {
        var words = new string[Cats][];
        words[CatViolence] = new[] { "살해", "살인", "피투성이", "학살", "고문", "폭행", "칼부림", "총격", "kill", "murder", "blood", "torture", "massacre", "stab", "shoot", "gore" };
        words[CatSexual] = new[] { "노출", "성행위", "음란", "베드신", "nude", "nudity", "sex", "explicit", "porn", "erotic" };
        words[CatHorror] = new[] { "귀신", "악령", "공포", "시체", "좀비", "ghost", "demon", "corpse", "zombie", "horror" };
        words[CatDrugs] = new[] { "마약", "필로폰", "대마", "코카인", "drug", "cocaine", "heroin", "meth", "cannabis" };
        words[CatGambling] = new[] { "도박", "카지노", "베팅", "토토", "gambling", "casino", "betting", "poker" };
        words[CatHate] = new[] { "혐오", "인종차별", "멸칭", "hate", "racist", "slur", "nazi", "genocide" };
        words[CatSelfHarm] = new[] { "자해", "자살", "투신", "suicide", "selfharm" };
        words[CatSmoking] = new[] { "담배", "흡연", "전자담배", "cigarette", "smoking", "vape" };
        words[CatAlcohol] = new[] { "음주", "소주", "폭음", "만취", "alcohol", "drunk", "beer", "whiskey" };
        words[CatWeapons] = new[] { "총", "권총", "소총", "폭탄", "gun", "rifle", "bomb", "grenade" };
        words[CatCrime] = new[] { "강도", "절도", "납치", "테러", "robbery", "theft", "kidnap", "terror" };
        words[CatProfanity] = new[] { "씨발", "개새끼", "병신", "fuck", "shit", "bitch", "damn" };
        var r = new ulong[Cats][];
        for (var c = 0; c < Cats; c++)
        {
            r[c] = new ulong[words[c].Length];
            for (var i = 0; i < words[c].Length; i++) r[c][i] = EnterpriseMamSmartIndexingEngine.Fnv64(words[c][i]);
            Array.Sort(r[c]);
        }
        return r;
    }

    // ══════════════════════ 텍스트 분석(무할당) ══════════════════════

    /// <summary>텍스트 렉시콘 스캔 → 신호 누적(범주 강도 = 1 - e^(-히트/2) 최댓값 병합 · 욕설 카운트 가산). 반환 = 총 히트.</summary>
    public static int AnalyzeText(ReadOnlySpan<char> text, ref ContentSignals s)
    {
        Span<int> hits = stackalloc int[Cats];
        hits.Clear();
        var total = 0;
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && !char.IsLetterOrDigit(text[i])) i++;
            var st = i;
            while (i < text.Length && char.IsLetterOrDigit(text[i])) i++;
            if (i <= st) continue;
            var w = text[st..i];
            for (var cut = 0; cut <= 2 && w.Length - cut >= 1; cut++)
            {
                if (cut > 0 && (w.Length - cut < 1 || !IsHangul(w[^1]))) break;
                var h = EnterpriseMamSmartIndexingEngine.Fnv64(w[..(w.Length - cut)]);
                var matched = false;
                for (var c = 0; c < Cats; c++)
                    if (Array.BinarySearch(Lexicon[c], h) >= 0) { hits[c]++; total++; matched = true; }
                if (matched) break;
            }
        }
        Merge(ref s.Violence, hits[CatViolence]); Merge(ref s.Sexual, hits[CatSexual]); Merge(ref s.Horror, hits[CatHorror]); Merge(ref s.Drugs, hits[CatDrugs]);
        Merge(ref s.Gambling, hits[CatGambling]); Merge(ref s.Hate, hits[CatHate]); Merge(ref s.SelfHarm, hits[CatSelfHarm]); Merge(ref s.Smoking, hits[CatSmoking]);
        Merge(ref s.Alcohol, hits[CatAlcohol]); Merge(ref s.Weapons, hits[CatWeapons]); Merge(ref s.Crime, hits[CatCrime]);
        s.ProfanityCount += hits[CatProfanity];
        return total;

        static void Merge(ref float f, int n) { if (n > 0) f = MathF.Max(f, 1f - MathF.Exp(-n / 2f)); }
        static bool IsHangul(char ch) => ch is >= '가' and <= '힣';
    }

    // ══════════════════════ 저작권 핑거프린트 ══════════════════════

    public void RegisterProtectedWork(string owner, MamAssetKind kind, ulong phash, ulong phash2 = 0, bool licensed = false)
    {
        lock (_gate)
        {
            if (_cpCount == _cpHash.Length)
            {
                Array.Resize(ref _cpHash, _cpCount * 2); Array.Resize(ref _cpHash2, _cpCount * 2);
                Array.Resize(ref _cpKind, _cpCount * 2); Array.Resize(ref _cpLicensed, _cpCount * 2);
            }
            _cpHash[_cpCount] = phash; _cpHash2[_cpCount] = phash2; _cpKind[_cpCount] = kind; _cpLicensed[_cpCount] = licensed;
            _cpOwner.Add(owner);
            _cpCount++;
        }
    }

    public string? OwnerOf(int index) { lock (_gate) return (uint)index < (uint)_cpCount ? _cpOwner[index] : null; }

    /// <summary>최근접 등록 저작물(라이선스 포함) — 임계 이내면 인덱스. 무할당.</summary>
    public int MatchCopyright(in ContentSignals s, out int distance, out bool licensed)
    {
        distance = int.MaxValue; licensed = false;
        if (!s.HasPHash) return -1;
        var best = -1;
        lock (_gate)
        {
            for (var i = 0; i < _cpCount; i++)
            {
                if (_cpKind[i] != s.Kind) continue;
                var two = s.Kind == MamAssetKind.Audio;
                var d = EnterpriseMamSmartIndexingEngine.Hamming(s.PHash, _cpHash[i]) + (two ? EnterpriseMamSmartIndexingEngine.Hamming(s.PHash2, _cpHash2[i]) : 0);
                if (d < distance && d <= (two ? CopyrightThreshold128 : CopyrightThreshold64)) { distance = d; best = i; licensed = _cpLicensed[i]; }
            }
        }
        return best;
    }

    // ══════════════════════ 진단(무할당) ══════════════════════

    public static int Level(float v) => v < 0.15f ? 0 : v < 0.4f ? 1 : v < 0.7f ? 2 : 3;

    public static int ProfanityLevel(int count, double durationSec)
    {
        if (count <= 0) return 0;
        var perMin = count / Math.Max(1.0, durationSec / 60.0);
        return perMin > 5 ? 3 : perMin > 2 ? 2 : 1;
    }

    /// <summary>지역 5개 판정 + 요약(브랜드 안전 · 저작권 · 집계). verdicts 길이 ≥ 5.</summary>
    public void Diagnose(in ContentSignals s, Span<RegionVerdict> verdicts, out ComplianceSummary sum)
    {
        sum = default;
        sum.CopyrightIndex = MatchCopyright(in s, out var dist, out var licensed);
        sum.CopyrightDistance = sum.CopyrightIndex >= 0 ? dist : -1;

        var maturity = Math.Max(Math.Max(Level(s.Violence), Level(s.Sexual * 1.2f)), Math.Max(Level(s.Horror * 0.8f), Math.Max(Level(s.Drugs), Level(s.SelfHarm))));
        maturity = Math.Max(maturity, Math.Max(ProfanityLevel(s.ProfanityCount, s.DurationSec), Level(s.Weapons * 0.7f)));

        var global = ComplianceIssue.None;
        var globalReject = ComplianceIssue.None;
        if (s.Hate >= 0.3f) globalReject |= ComplianceIssue.HateSpeech;
        if (s.MinorsPresent && s.Sexual > 0.2f) globalReject |= ComplianceIssue.MinorsSexualized;
        if (s.SyntheticFace && s.RealPersonLikeness && !s.ConsentOnFile) globalReject |= ComplianceIssue.DeepfakeAbuse;
        if (sum.CopyrightIndex >= 0 && !licensed) globalReject |= ComplianceIssue.CopyrightMatch;
        if (s.SelfHarm >= 0.7f) globalReject |= ComplianceIssue.SelfHarmSevere;
        else if (s.SelfHarm >= 0.4f) global |= ComplianceIssue.SelfHarm;
        if (s.ProfanityCount > 0) global |= ComplianceIssue.Profanity;
        var undisclosedAi = s.SyntheticMedia && !(s.C2paSigned || s.AiLabelShown);
        var politicalFake = s.SyntheticMedia && s.Political && (s.SyntheticFace || s.RealPersonLikeness);

        var n = Math.Min(verdicts.Length, Regions);
        for (var r = 0; r < n; r++)
        {
            ref var v = ref verdicts[r];
            v.Region = (ComplianceRegion)r;
            v.RatingIndex = maturity;
            var warn = global;
            var reject = globalReject;
            switch (v.Region)
            {
                case ComplianceRegion.US:
                    if (s.Sexual >= 0.85f) reject |= ComplianceIssue.UsObscenity;
                    if (maturity >= 3 || ProfanityLevel(s.ProfanityCount, s.DurationSec) >= 2) warn |= ComplianceIssue.UsSafeHarbor;
                    if (undisclosedAi) warn |= ComplianceIssue.AiDisclosure;
                    break;
                case ComplianceRegion.KR:
                    if (maturity >= 3) warn |= ComplianceIssue.KrYouthHarmful;
                    if (s.Smoking >= 0.4f || s.Alcohol >= 0.4f) warn |= ComplianceIssue.KrSmokingAlcohol;
                    if (s.ProductPlacements > 3 || (s.ProductPlacements > 0 && !s.PplDisclosed)) warn |= ComplianceIssue.KrPpl;
                    if (s.Gambling >= 0.4f) warn |= ComplianceIssue.KrGambling;
                    if (undisclosedAi) warn |= ComplianceIssue.AiDisclosure;
                    if (politicalFake) reject |= ComplianceIssue.PoliticalDeepfake;
                    break;
                case ComplianceRegion.EU:
                    if (s.ProductPlacements > 0 && !s.PplDisclosed) warn |= ComplianceIssue.EuPplDisclosure;
                    if (undisclosedAi) reject |= ComplianceIssue.AiDisclosure;
                    if (politicalFake) reject |= ComplianceIssue.PoliticalDeepfake;
                    break;
                case ComplianceRegion.UK:
                    if (maturity >= 2) warn |= ComplianceIssue.UkWatershed;
                    if (undisclosedAi) warn |= ComplianceIssue.AiDisclosure;
                    break;
                case ComplianceRegion.JP:
                    if (s.Violence >= 0.6f) warn |= ComplianceIssue.JpViolence;
                    break;
            }
            v.Issues = warn | reject;
            v.Status = reject != 0 ? ComplianceStatus.Rejected : (warn & ~ComplianceIssue.Profanity) != 0 ? ComplianceStatus.Warning : ComplianceStatus.Approved;
            sum.Issues |= v.Issues;
            if (v.Status == ComplianceStatus.Approved) sum.Approved++; else if (v.Status == ComplianceStatus.Warning) sum.Warned++; else sum.Rejected++;
        }

        // GARM 브랜드 안전(가중 합 · 바닥 범주는 Floor)
        var risk = s.Violence * 18 + s.Sexual * 22 + s.Hate * 30 + s.Drugs * 14 + s.Weapons * 10 + s.Crime * 10 + s.Horror * 6 + s.Gambling * 8 + s.SelfHarm * 20
                 + Math.Min(10, s.ProfanityCount * 2) + (s.Smoking + s.Alcohol) * 4 + (sum.CopyrightIndex >= 0 && !licensed ? 15 : 0) + (undisclosedAi ? 6 : 0);
        sum.BrandRisk = Math.Clamp((int)MathF.Round(risk), 0, 100);
        sum.BrandTier = (globalReject & (ComplianceIssue.HateSpeech | ComplianceIssue.MinorsSexualized | ComplianceIssue.DeepfakeAbuse)) != 0 ? BrandSafetyTier.Floor
            : sum.BrandRisk >= 60 ? BrandSafetyTier.High : sum.BrandRisk >= 30 ? BrandSafetyTier.Medium : sum.BrandRisk >= 10 ? BrandSafetyTier.Low : BrandSafetyTier.Safe;
        lock (_gate) Diagnoses++;
    }

    // ══════════════════════ 리포트(UI · 배포 게이트) ══════════════════════

    public static ContentSignals SignalsFor(MamAsset a)
    {
        var s = new ContentSignals { Kind = a.Kind, DurationSec = a.DurationSec, SyntheticMedia = a.Synthetic, C2paSigned = a.C2paSigned, PHash = a.PHash, PHash2 = a.PHash2, HasPHash = a.HasPHash };
        AnalyzeText(a.Title, ref s);
        AnalyzeText(a.Description, ref s);
        foreach (var t in a.Tags) AnalyzeText(t, ref s);
        return s;
    }

    public ComplianceReport Check(MamAsset a, ContentSignals? overrides = null)
    {
        var s = overrides ?? SignalsFor(a);
        Span<RegionVerdict> v = stackalloc RegionVerdict[Regions];
        Diagnose(in s, v, out var sum);
        var regions = new List<(ComplianceRegion, string, ComplianceStatus, IReadOnlyList<string>)>(Regions);
        var approved = new List<ComplianceRegion>();
        for (var r = 0; r < Regions; r++)
        {
            regions.Add((v[r].Region, RatingLabel(v[r].Region, v[r].RatingIndex), v[r].Status, IssueTexts(v[r].Issues)));
            if (v[r].Status != ComplianceStatus.Rejected) approved.Add(v[r].Region);
        }
        var report = new ComplianceReport(a.Id, a.Title, regions, sum.BrandRisk, sum.BrandTier, sum.CopyrightIndex >= 0 ? OwnerOf(sum.CopyrightIndex) : null, approved, DateTimeOffset.UtcNow);
        lock (_gate) _reports[a.Id] = report;
        LastAction = "🛡 심의 · " + a.Title + " · 승인 " + sum.Approved.ToString(CultureInfo.InvariantCulture) + " / 경고 " + sum.Warned.ToString(CultureInfo.InvariantCulture) + " / 반려 " + sum.Rejected.ToString(CultureInfo.InvariantCulture);
        try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("Compliance.OnChange", ex); }
        return report;
    }

    public ComplianceReport? ReportFor(string assetId) { lock (_gate) return _reports.TryGetValue(assetId, out var r) ? r : null; }

    public static string RatingLabel(ComplianceRegion r, int idx) => RatingLabels[(int)r][Math.Clamp(idx, 0, 3)];

    public static string RegionLabel(ComplianceRegion r) => r switch
    {
        ComplianceRegion.US => "🇺🇸 미국 FCC", ComplianceRegion.KR => "🇰🇷 한국 KCSC", ComplianceRegion.EU => "🇪🇺 EU AVMSD/AI Act", ComplianceRegion.UK => "🇬🇧 영국 Ofcom", _ => "🇯🇵 일본 BPO",
    };

    public static List<string> IssueTexts(ComplianceIssue i)
    {
        var l = new List<string>();
        void A(ComplianceIssue f, string t) { if ((i & f) != 0) l.Add(t); }
        A(ComplianceIssue.UsObscenity, "⛔ 외설(FCC 방송 금지)");
        A(ComplianceIssue.UsSafeHarbor, "⚠ 세이프하버(22~06시) 편성만 허용");
        A(ComplianceIssue.KrYouthHarmful, "⚠ 청소년 유해 — 19세 표기 · 22시 이후 편성");
        A(ComplianceIssue.KrSmokingAlcohol, "⚠ 흡연/음주 미화 제한(방송심의규정)");
        A(ComplianceIssue.KrPpl, "⚠ 간접광고 고지/횟수 규정");
        A(ComplianceIssue.KrGambling, "⚠ 사행성 조장 우려");
        A(ComplianceIssue.EuPplDisclosure, "⚠ PPL 고지 의무(AVMSD Art.11)");
        A(ComplianceIssue.AiDisclosure, "⚠ AI 합성물 표시 필요(C2PA/라벨)");
        A(ComplianceIssue.UkWatershed, "⚠ 21시 워터셰드 이후 편성");
        A(ComplianceIssue.JpViolence, "⚠ 폭력 묘사 배려(BPO)");
        A(ComplianceIssue.HateSpeech, "⛔ 혐오 표현");
        A(ComplianceIssue.SelfHarm, "⚠ 자해/자살 묘사 — 상담 안내 필요");
        A(ComplianceIssue.SelfHarmSevere, "⛔ 자해/자살 구체 묘사");
        A(ComplianceIssue.MinorsSexualized, "⛔ 미성년자 성적 대상화");
        A(ComplianceIssue.DeepfakeAbuse, "⛔ 동의 없는 실존 인물 딥페이크");
        A(ComplianceIssue.CopyrightMatch, "⛔ 등록 저작물 핑거프린트 일치");
        A(ComplianceIssue.PoliticalDeepfake, "⛔ 정치 딥페이크(선거법)");
        A(ComplianceIssue.Profanity, "ℹ 비속어 포함");
        return l;
    }
}
