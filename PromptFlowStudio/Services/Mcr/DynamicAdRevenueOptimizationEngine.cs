using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PromptFlowStudio.Services.Security;

namespace PromptFlowStudio.Services.Mcr;

[Flags]
public enum AdCategory : ushort { None = 0, Auto = 1, Beauty = 2, Gaming = 4, Finance = 8, Food = 16, Travel = 32, Tech = 64, Entertainment = 128, All = 0xFF }

/// <summary>DSP/애드 서버 입찰자 설정 — 국가 배수(Countries 순) · 언어 비트 · 카테고리 비트 · 변동성 · 입찰 하한 · VAST 래퍼.</summary>
public sealed class DspBidder
{
    public string Name { get; init; } = "";
    public string VastUrl { get; init; } = "";
    public double BaseCpm { get; init; }
    public float[] GeoMultiplier { get; init; } = Array.Empty<float>();
    public uint LanguageMask { get; init; } = uint.MaxValue;
    public AdCategory Categories { get; init; } = AdCategory.All;
    public double Volatility { get; init; } = 0.15;
    public double BidFloor { get; init; }
    /// <summary>원격 OpenRTB 엔드포인트(설정 시 실입찰 · 데드라인/서킷 브레이커).</summary>
    public string? OpenRtbUrl { get; init; }
}

public readonly record struct AuctionRequest(int Country, int Language, AdCategory Context, int Impressions, int BreakIndex);

/// <summary>2nd-price 경매 결과(값형) — 가격은 CPM(USD).</summary>
public readonly record struct AuctionResult(bool Filled, int Winner, double HighBid, double SecondBid, double ClearingCpm, double Floor, int Bids, long RevenueMicros);

public sealed record Stakeholder(string Name, int Bps);

public sealed record LedgerEntry(long Seq, string AssetId, string Country, string Bidder, double ClearingCpm, int Impressions, long RevenueMicros,
    IReadOnlyList<(string Name, long Micros)> Splits, string PrevHash, string Hash, DateTimeOffset Utc);

/// <summary>
/// [PromptFlow Studio v16.43.0] 💰 실시간 eCPM 오토스케일링 &amp; 수익 최적화기.
///   ▸ RTB: 입찰자별 입찰가 = 기본 CPM × 국가 배수 × (언어 일치 1 · 불일치 0.6) × (맥락 카테고리 일치 1.2) × (1 ± 변동성) → 2nd-price 경매(낙찰가 = max(차순위+0.01, 하한)) — 무할당.
///   ▸ 원격 DSP(OpenRTB 2.5 JSON) 150ms 데드라인 병렬 수집 · 스키마 검증(seatbid[0].bid[0].price) · 실패 시 지수 백오프 서킷 브레이커.
///   ▸ 하한가 오토스케일: 국가별 필레이트 EWMA → 목표 0.85 초과 시 +5% · 미만 시 -5%(0.5~40 USD 클램프).
///   ▸ 정산: 수익(마이크로 USD) → 지분권자 bps 정수 분배(나머지 1순위) · HMAC 체인 원장(canonical 집계 필드 포함 · FixedTimeEquals 검증) · VAST 4.0 래퍼.
/// </summary>
public sealed class DynamicAdRevenueOptimizationEngine : IDisposable
{
    public static readonly string[] Countries = { "KR", "US", "JP", "GB", "DE", "FR", "BR", "IN", "OT" };
    public static readonly string[] Languages = { "ko", "en", "ja", "de", "fr", "pt", "hi" };
    public const double TargetFill = 0.85, MinFloor = 0.5, MaxFloor = 40;
    public const string LedgerKeyName = "ad_ledger_hmac";

    private readonly object _gate = new();
    private readonly List<DspBidder> _bidders = new();
    private double[] _remoteBid = new double[8];
    private long[] _breakerUntil = new long[8];
    private int[] _breakerFails = new int[8];
    private readonly double[] _floor = new double[Countries.Length], _fillEwma = new double[Countries.Length];
    private readonly long[] _revByCountry = new long[Countries.Length], _auctions = new long[Countries.Length], _fills = new long[Countries.Length];
    private long[] _revByBidder = new long[8], _winsByBidder = new long[8];
    private readonly List<LedgerEntry> _ledger = new();
    private readonly Func<byte[]> _ledgerKeySrc;
    private byte[]? _ledgerKey;
    private readonly HttpClient _http;
    private uint _rng;

    public DynamicAdRevenueOptimizationEngine(IServiceProvider sp)
        : this(sp.GetService(typeof(SecureSecretVault)) is SecureSecretVault v ? () => v.ResolveOrCreate(LedgerKeyName) : () => SHA256.HashData("pfs-ad-ledger-dev"u8), null, 0x2468ACE1u) { }

    public DynamicAdRevenueOptimizationEngine(Func<byte[]> ledgerKey, HttpMessageHandler? handler = null, uint seed = 0x2468ACE1u)
    {
        _ledgerKeySrc = ledgerKey;
        _rng = seed == 0 ? 1u : seed;
        _http = handler is null ? new HttpClient { Timeout = TimeSpan.FromSeconds(2) } : new HttpClient(handler, false) { Timeout = TimeSpan.FromSeconds(2) };
        for (var i = 0; i < Countries.Length; i++) { _floor[i] = 2.0; _fillEwma[i] = TargetFill; }
        Stakeholders = new[] { new Stakeholder("제작사", 5000), new Stakeholder("플랫폼", 3000), new Stakeholder("출연자", 1500), new Stakeholder("음원권자", 500) };
    }

    public event Action? OnChange;
    public IReadOnlyList<DspBidder> Bidders { get { lock (_gate) return _bidders.ToArray(); } }
    public IReadOnlyList<Stakeholder> Stakeholders { get; private set; }
    public IReadOnlyList<LedgerEntry> Ledger { get { lock (_gate) return _ledger.ToArray(); } }
    public int RemoteDeadlineMs { get; set; } = 150;
    public string? LastAction { get; private set; }
    public double FloorOf(int c) => _floor[c];
    public double FillRateOf(int c) => _fillEwma[c];
    public long RevenueMicrosOf(int c) => Interlocked.Read(ref _revByCountry[c]);
    public long AuctionsOf(int c) => Interlocked.Read(ref _auctions[c]);
    public long BidderRevenueMicros(int b) => (uint)b < (uint)_revByBidder.Length ? Interlocked.Read(ref _revByBidder[b]) : 0;
    public long BidderWins(int b) => (uint)b < (uint)_winsByBidder.Length ? Interlocked.Read(ref _winsByBidder[b]) : 0;
    public long TotalRevenueMicros { get { long s = 0; foreach (var r in _revByCountry) s += r; return s; } }

    public static int CountryIndex(ReadOnlySpan<char> cc)
    {
        for (var i = 0; i < Countries.Length - 1; i++) if (cc.Equals(Countries[i], StringComparison.OrdinalIgnoreCase)) return i;
        return Countries.Length - 1;
    }

    public static int LanguageIndex(ReadOnlySpan<char> l)
    {
        for (var i = 0; i < Languages.Length; i++) if (l.StartsWith(Languages[i], StringComparison.OrdinalIgnoreCase)) return i;
        return 1;
    }

    public static uint Lang(params string[] codes) { uint m = 0; foreach (var c in codes) m |= 1u << LanguageIndex(c); return m; }

    public void SetStakeholders(IReadOnlyList<Stakeholder> s)
    {
        var sum = 0; foreach (var x in s) sum += x.Bps;
        if (s.Count == 0 || sum != 10000) throw new ArgumentException("지분 합계는 10000 bps(100%) 여야 합니다.");
        Stakeholders = s;
    }

    public int AddBidder(DspBidder b)
    {
        if (b.GeoMultiplier.Length != Countries.Length) throw new ArgumentException("GeoMultiplier 길이 = 국가 수");
        lock (_gate)
        {
            _bidders.Add(b);
            var n = _bidders.Count;
            if (_remoteBid.Length < n)
            {
                Array.Resize(ref _remoteBid, n * 2); Array.Resize(ref _breakerUntil, n * 2); Array.Resize(ref _breakerFails, n * 2);
                Array.Resize(ref _revByBidder, n * 2); Array.Resize(ref _winsByBidder, n * 2);
            }
            return n - 1;
        }
    }

    /// <summary>데모 DSP 4종(글로벌 · 아시아 · 유럽 · 게임 특화).</summary>
    public void SeedDemoBidders()
    {
        if (_bidders.Count > 0) return;
        AddBidder(new DspBidder { Name = "GlobalDSP", VastUrl = "https://ads.globaldsp.example/vast", BaseCpm = 6.0, GeoMultiplier = new[] { 1.1f, 1.6f, 1.2f, 1.3f, 1.2f, 1.1f, 0.6f, 0.4f, 0.5f }, Volatility = 0.2 });
        AddBidder(new DspBidder { Name = "AsiaReach", VastUrl = "https://vast.asiareach.example/w", BaseCpm = 7.0, GeoMultiplier = new[] { 1.5f, 0.5f, 1.4f, 0.4f, 0.4f, 0.4f, 0.3f, 0.8f, 0.4f }, LanguageMask = Lang("ko", "ja"), Categories = AdCategory.Beauty | AdCategory.Food | AdCategory.Entertainment });
        AddBidder(new DspBidder { Name = "EuroVideo", VastUrl = "https://euro-video.example/vast4", BaseCpm = 6.5, GeoMultiplier = new[] { 0.3f, 0.8f, 0.3f, 1.4f, 1.5f, 1.4f, 0.4f, 0.2f, 0.6f }, LanguageMask = Lang("en", "de", "fr"), Categories = AdCategory.Auto | AdCategory.Finance | AdCategory.Travel });
        AddBidder(new DspBidder { Name = "GameAds", VastUrl = "https://gameads.example/vast", BaseCpm = 5.0, GeoMultiplier = new[] { 1.3f, 1.3f, 1.2f, 1.0f, 1.0f, 1.0f, 1.0f, 0.9f, 0.8f }, Categories = AdCategory.Gaming | AdCategory.Tech, BidFloor = 3 });
        LastAction = "🏦 데모 DSP 4종 등록";
        Raise();
    }

    // ══════════════════════ 경매(무할당) ══════════════════════

    private double NextGauss()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
        var a = (_rng & 0xFFFFFF) / 16777216.0;
        _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
        var b = (_rng & 0xFFFFFF) / 16777216.0;
        return (a + b - 1.0) * 1.7;   // 근사 정규(-1.7~1.7)
    }

    /// <summary>입찰가(CPM) — 결정적 부분(변동성 제외).</summary>
    public static double BaseBid(DspBidder b, in AuctionRequest r)
    {
        var lang = ((b.LanguageMask >> r.Language) & 1) != 0 ? 1.0 : 0.6;
        var ctx = (b.Categories & r.Context) != 0 ? 1.2 : 1.0;
        return b.BaseCpm * b.GeoMultiplier[r.Country] * lang * ctx;
    }

    /// <summary>2nd-price 경매 + 하한 오토스케일 + 집계(무할당).</summary>
    public AuctionResult RunAuction(in AuctionRequest r)
    {
        lock (_gate)
        {
            var c = (uint)r.Country < (uint)Countries.Length ? r.Country : Countries.Length - 1;
            var floor = _floor[c];
            int best = -1; double high = 0, second = 0; var bids = 0;
            for (var i = 0; i < _bidders.Count; i++)
            {
                var b = _bidders[i];
                double bid;
                if (b.OpenRtbUrl is not null) { bid = _remoteBid[i]; if (bid <= 0) continue; }
                else bid = Math.Max(0, BaseBid(b, r) * (1 + b.Volatility * NextGauss()));
                if (bid < b.BidFloor || bid <= 0) continue;
                bids++;
                if (bid > high) { second = high; high = bid; best = i; }
                else if (bid > second) second = bid;
            }
            var filled = best >= 0 && high >= floor;
            var clearing = filled ? Math.Min(high, Math.Max(second + 0.01, floor)) : 0;
            var rev = filled ? (long)Math.Round(clearing * r.Impressions * 1000) : 0;   // CPM×노출/1000 → 마이크로 USD
            _fillEwma[c] = _fillEwma[c] * 0.9 + (filled ? 0.1 : 0);
            _floor[c] = Math.Clamp(_fillEwma[c] > TargetFill ? floor * 1.05 : floor * 0.95, MinFloor, MaxFloor);
            _auctions[c]++;
            if (filled)
            {
                _fills[c]++;
                _revByCountry[c] += rev;
                _revByBidder[best] += rev;
                _winsByBidder[best]++;
            }
            return new AuctionResult(filled, best, high, second, Math.Round(clearing, 4), floor, bids, rev);
        }
    }

    /// <summary>원격 OpenRTB 입찰 병렬 수집(데드라인 · 스키마 검증 · 서킷 브레이커) 후 경매.</summary>
    public async Task<AuctionResult> RunAuctionWithRemoteAsync(AuctionRequest r, CancellationToken ct = default)
    {
        DspBidder[] bs;
        lock (_gate) bs = _bidders.ToArray();
        var tasks = new List<Task>();
        var now = Environment.TickCount64;
        for (var i = 0; i < bs.Length; i++)
        {
            if (bs[i].OpenRtbUrl is null) continue;
            _remoteBid[i] = 0;
            if (now < Volatile.Read(ref _breakerUntil[i])) continue;
            tasks.Add(FetchRemoteAsync(i, bs[i], r, ct));
        }
        if (tasks.Count > 0) await Task.WhenAll(tasks).ConfigureAwait(false);
        return RunAuction(r);
    }

    private async Task FetchRemoteAsync(int i, DspBidder b, AuctionRequest r, CancellationToken ct)
    {
        using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
        d.CancelAfter(RemoteDeadlineMs);
        try
        {
            var body = "{\"id\":\"" + Guid.NewGuid().ToString("N") + "\",\"imp\":[{\"id\":\"1\",\"video\":{\"mimes\":[\"video/mp4\"],\"minduration\":5,\"maxduration\":30},\"bidfloor\":"
                     + _floor[r.Country].ToString("0.00", CultureInfo.InvariantCulture) + "}],\"device\":{\"geo\":{\"country\":\"" + Countries[r.Country] + "\"},\"language\":\"" + Languages[r.Language] + "\"},\"tmax\":" + RemoteDeadlineMs.ToString(CultureInfo.InvariantCulture) + "}";
            using var req = new HttpRequestMessage(HttpMethod.Post, b.OpenRtbUrl) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("x-openrtb-version", "2.5");
            using var resp = await _http.SendAsync(req, d.Token).ConfigureAwait(false);
            if ((int)resp.StatusCode == 204) { Succeed(i); return; }
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(d.Token).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("seatbid", out var sb) || sb.ValueKind != JsonValueKind.Array || sb.GetArrayLength() == 0
                || !sb[0].TryGetProperty("bid", out var bid) || bid.ValueKind != JsonValueKind.Array || bid.GetArrayLength() == 0
                || !bid[0].TryGetProperty("price", out var price) || price.ValueKind != JsonValueKind.Number || !price.TryGetDouble(out var p) || p < 0 || p > 1000)
                throw new FormatException("OpenRTB 스키마 불일치");
            _remoteBid[i] = p;
            Succeed(i);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or FormatException)
        {
            if (ct.IsCancellationRequested) return;
            var fails = Interlocked.Increment(ref _breakerFails[i]);
            Volatile.Write(ref _breakerUntil[i], Environment.TickCount64 + Math.Min(60_000, 250L << Math.Min(fails, 8)));
        }

        void Succeed(int k) => Volatile.Write(ref _breakerFails[k], 0);
    }

    public bool BreakerOpen(int bidder) => Environment.TickCount64 < Volatile.Read(ref _breakerUntil[bidder]);

    // ══════════════════════ 정산 · 원장 ══════════════════════

    /// <summary>bps 정수 분배(합 = 총액 · 나머지 1순위). dst 길이 ≥ 지분권자 수 — 무할당.</summary>
    public static void Split(long micros, IReadOnlyList<Stakeholder> holders, Span<long> dst)
    {
        long used = 0;
        for (var i = 0; i < holders.Count; i++) { dst[i] = micros * holders[i].Bps / 10000; used += dst[i]; }
        if (holders.Count > 0) dst[0] += micros - used;
    }

    private byte[] LedgerKey => _ledgerKey ??= _ledgerKeySrc();

    private static string Canonical(long seq, string asset, string country, string bidder, double cpm, int impr, long rev, IReadOnlyList<(string Name, long Micros)> splits, string prev)
    {
        var sb = new StringBuilder("v1|").Append(seq.ToString(CultureInfo.InvariantCulture)).Append('|').Append(asset.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(asset)
            .Append('|').Append(country).Append('|').Append(bidder.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(bidder)
            .Append('|').Append(cpm.ToString("0.0000", CultureInfo.InvariantCulture)).Append('|').Append(impr.ToString(CultureInfo.InvariantCulture))
            .Append('|').Append(rev.ToString(CultureInfo.InvariantCulture)).Append('|').Append(splits.Count.ToString(CultureInfo.InvariantCulture));
        long sum = 0;
        foreach (var (n, m) in splits) { sb.Append('|').Append(n.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(n).Append('=').Append(m.ToString(CultureInfo.InvariantCulture)); sum += m; }
        return sb.Append("|sum=").Append(sum.ToString(CultureInfo.InvariantCulture)).Append('|').Append(prev).ToString();
    }

    /// <summary>낙찰 결과 → 지분 정산 원장 기록(HMAC 체인).</summary>
    public LedgerEntry? Settle(string assetId, in AuctionRequest r, in AuctionResult res)
    {
        if (!res.Filled) return null;
        var holders = Stakeholders;
        Span<long> parts = stackalloc long[Math.Max(1, holders.Count)];
        Split(res.RevenueMicros, holders, parts);
        var splits = new List<(string, long)>(holders.Count);
        for (var i = 0; i < holders.Count; i++) splits.Add((holders[i].Name, parts[i]));
        lock (_gate)
        {
            var seq = _ledger.Count + 1L;
            var prev = _ledger.Count == 0 ? "genesis" : _ledger[^1].Hash;
            var bidder = _bidders[res.Winner].Name;
            var country = Countries[r.Country];
            var hash = Convert.ToHexString(HMACSHA256.HashData(LedgerKey, Encoding.UTF8.GetBytes(Canonical(seq, assetId, country, bidder, res.ClearingCpm, r.Impressions, res.RevenueMicros, splits, prev)))).ToLowerInvariant();
            var e = new LedgerEntry(seq, assetId, country, bidder, res.ClearingCpm, r.Impressions, res.RevenueMicros, splits, prev, hash, DateTimeOffset.UtcNow);
            _ledger.Add(e);
            if (_ledger.Count > 100_000) _ledger.RemoveRange(0, 10_000);
            return e;
        }
    }

    /// <summary>원장 무결성 검증(체인 · HMAC · FixedTimeEquals). 반환 = 첫 위반 인덱스(-1 = 정상).</summary>
    public int VerifyLedger(IReadOnlyList<LedgerEntry>? entries = null)
    {
        var list = entries ?? Ledger;
        for (var i = 0; i < list.Count; i++)
        {
            var e = list[i];
            if (i > 0 && e.PrevHash != list[i - 1].Hash) return i;
            long sum = 0; foreach (var (_, m) in e.Splits) sum += m;
            if (sum != e.RevenueMicros) return i;
            var expect = HMACSHA256.HashData(LedgerKey, Encoding.UTF8.GetBytes(Canonical(e.Seq, e.AssetId, e.Country, e.Bidder, e.ClearingCpm, e.Impressions, e.RevenueMicros, e.Splits, e.PrevHash)));
            byte[] got;
            try { got = Convert.FromHexString(e.Hash); } catch (FormatException) { return i; }
            if (got.Length != expect.Length || !CryptographicOperations.FixedTimeEquals(got, expect)) return i;
        }
        return -1;
    }

    /// <summary>지분권자별 누적(마이크로 USD).</summary>
    public IReadOnlyList<(string Name, long Micros)> SettlementTotals()
    {
        var d = new Dictionary<string, long>(StringComparer.Ordinal);
        lock (_gate) foreach (var e in _ledger) foreach (var (n, m) in e.Splits) d[n] = d.GetValueOrDefault(n) + m;
        var l = new List<(string, long)>();
        foreach (var kv in d) l.Add((kv.Key, kv.Value));
        l.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return l;
    }

    /// <summary>VAST 4.0 Wrapper(낙찰가 매크로 · 노출 비콘).</summary>
    public string VastWrapper(int winner, in AuctionResult res, string impressionBase, string adId)
    {
        var b = _bidders[winner];
        var price = res.ClearingCpm.ToString("0.0000", CultureInfo.InvariantCulture);
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<VAST version=\"4.0\" xmlns=\"http://www.iab.com/VAST\">\n  <Ad id=\"" + Esc(adId) + "\">\n    <Wrapper>\n      <AdSystem>PromptFlow SSAI</AdSystem>\n"
             + "      <VASTAdTagURI><![CDATA[" + b.VastUrl + (b.VastUrl.Contains('?') ? "&" : "?") + "price=" + price + "&cur=USD]]></VASTAdTagURI>\n"
             + "      <Impression><![CDATA[" + impressionBase + "?ad=" + Uri.EscapeDataString(adId) + "&dsp=" + Uri.EscapeDataString(b.Name) + "&cpm=" + price + "]]></Impression>\n"
             + "      <Pricing model=\"CPM\" currency=\"USD\">" + price + "</Pricing>\n      <Creatives/>\n    </Wrapper>\n  </Ad>\n</VAST>\n";

        static string Esc(string s) => s.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;");
    }

    /// <summary>브레이크 1회 전 국가 시뮬레이션(국가별 시청자 → 경매 · 정산). 반환 = 총 수익 마이크로.</summary>
    public long SimulateBreak(string assetId, ReadOnlySpan<int> viewersByCountry, AdCategory context, int breakIndex = 0)
    {
        long total = 0;
        for (var c = 0; c < Countries.Length && c < viewersByCountry.Length; c++)
        {
            if (viewersByCountry[c] <= 0) continue;
            var lang = c switch { 0 => 0, 2 => 2, 4 => 3, 5 => 4, 6 => 5, 7 => 6, _ => 1 };
            var req = new AuctionRequest(c, lang, context, viewersByCountry[c], breakIndex);
            var res = RunAuction(req);
            if (res.Filled) { Settle(assetId, req, res); total += res.RevenueMicros; }
        }
        LastAction = "💰 브레이크 정산 · $" + (total / 1e6).ToString("0.00", CultureInfo.InvariantCulture);
        Raise();
        return total;
    }

    public static AdCategory ContextFor(Cinema.SceneMood mood) => mood switch
    {
        Cinema.SceneMood.Action => AdCategory.Gaming | AdCategory.Auto | AdCategory.Tech,
        Cinema.SceneMood.Warm => AdCategory.Food | AdCategory.Travel | AdCategory.Beauty,
        Cinema.SceneMood.Mystery or Cinema.SceneMood.Tense => AdCategory.Entertainment | AdCategory.Tech,
        Cinema.SceneMood.Melancholy => AdCategory.Finance | AdCategory.Travel,
        _ => AdCategory.Entertainment,
    };

    private void Raise() { try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("AdRevenue.OnChange", ex); } }

    public void Dispose() => _http.Dispose();
}
