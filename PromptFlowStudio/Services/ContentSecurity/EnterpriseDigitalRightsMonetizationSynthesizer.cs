using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace PromptFlowStudio.Services.ContentSecurity;

public enum SettlementState : byte { Pending, Escrow, Settled }
public enum RateUnit : byte { PerPlay, PerUnit, RevenueShare, PerAiring }

/// <summary>원장 블록(고정 크기 · 선할당): 인덱스 · 이전 해시 · 배치 집계(수익/재생/불법/보유자 수) · 보유자별 지급 · 해시 · 서명 · 상태.</summary>
public sealed class LedgerBlock
{
    public long Index, SimTimeMs, Revenue, Plays, PiracyPlays, PiracyLoss;
    public int Holders;
    public readonly long[] Payout = new long[EnterpriseDigitalRightsMonetizationSynthesizer.MaxHolders];
    public readonly byte[] PrevHash = new byte[32], Hash = new byte[32], Signature = new byte[64];
    public SettlementState State;
}

/// <summary>
/// [PromptFlow Studio v16.90.0] 💰 엔터프라이즈 디지털 저작권 &amp; 라이선싱 수익화 합성기(계약 파싱 = 제어 평면 · 이벤트/정산 = 무할당 · 정수 밀리원).
///   ▸ 계약 DSL(한/영): <c>자산: 이름 | ID</c> · <c>지분: 보유자=%, …</c>(합 100 검증) · <c>회수: 보유자=금액</c>(워터폴 선회수) · <c>요율: 채널=금액 /재생|/건|매출%|/회</c> · <c>지역: KR, US …</c>.
///   ▸ 이벤트: 재생(채널 · 지역 · 디바이스) · 판매(총액) — 워터마크 역추적 유출 디바이스(차단 목록) 재생은 수익 제외 + 불법 손실 집계.
///   ▸ 정산 배치: 회수 우선(순서대로 100%) → 잔여 지분 비례 · 최대 잔여(Hamilton) 반올림 → 합계 정확 보존 · 보유자 잔액.
///   ▸ 분산 원장(스마트 계약 상태): 해시 체인(SHA-256 · 캐노니컬 = 집계 + 지급) + HMAC-SHA512 서명 · 확인 2블록 후 Escrow → Settled · 체인 검증(FixedTimeEquals) · 링 256.
/// </summary>
public sealed class EnterpriseDigitalRightsMonetizationSynthesizer
{
    public const int MaxHolders = 16, MaxChannels = 8, MaxTerritories = 16, LedgerSize = 256, MaxBlocked = 64, Confirmations = 2;
    public const long Milli = 1000;

    private readonly object _gate = new();
    private readonly byte[] _key;
    private readonly string[] _holder = new string[MaxHolders], _channel = new string[MaxChannels], _territory = new string[MaxTerritories];
    private readonly int[] _shareBp = new int[MaxHolders];
    private readonly long[] _recoupTarget = new long[MaxHolders], _recouped = new long[MaxHolders], _balance = new long[MaxHolders], _paidTotal = new long[MaxHolders];
    private readonly long[] _rate = new long[MaxChannels];
    private readonly RateUnit[] _unit = new RateUnit[MaxChannels];
    private readonly long[] _plays = new long[MaxChannels * MaxTerritories], _rev = new long[MaxChannels * MaxTerritories];
    private readonly long[] _batchPayout = new long[MaxHolders], _rem = new long[MaxHolders];
    private readonly ulong[] _blocked = new ulong[MaxBlocked];
    private readonly LedgerBlock[] _ledger = new LedgerBlock[LedgerSize];
    private readonly byte[] _canon = new byte[96 + MaxHolders * 8];
    private int _nHolder, _nChannel, _nTerritory, _nBlocked, _ledgerCount;
    private long _batchRevenue, _batchPlays, _batchPiracy, _batchLoss, _totalRevenue, _totalPaid;
    private readonly byte[] _tip = new byte[32];

    public EnterpriseDigitalRightsMonetizationSynthesizer(byte[]? key = null)
    {
        _key = key is { Length: >= 16 } ? (byte[])key.Clone() : RandomNumberGenerator.GetBytes(32);
        for (var i = 0; i < LedgerSize; i++) _ledger[i] = new LedgerBlock();
    }

    public string AssetName { get; private set; } = "";
    public string AssetId { get; private set; } = "";
    public int HolderCount => _nHolder;
    public int ChannelCount => _nChannel;
    public int TerritoryCount => _nTerritory;
    public int ParseErrors { get; private set; }
    public string? ParseMessage { get; private set; }
    public long TotalRevenue => _totalRevenue;
    public long TotalPaid => _totalPaid;
    public long TotalPlays { get; private set; }
    public long PiracyPlays { get; private set; }
    public long PiracyLoss { get; private set; }
    public long LedgerHeight { get; private set; }
    public int LedgerCount => _ledgerCount;
    public long PendingRevenue => _batchRevenue;
    public double LastSettleMs { get; private set; }
    public string HolderName(int h) => _holder[h];
    public int ShareBasisPoints(int h) => _shareBp[h];
    public long RecoupTarget(int h) => _recoupTarget[h];
    public long Recouped(int h) => _recouped[h];
    public long Balance(int h) => _balance[h];
    public long PaidTotal(int h) => _paidTotal[h];
    public string ChannelName(int c) => _channel[c];
    public long Rate(int c) => _rate[c];
    public RateUnit Unit(int c) => _unit[c];
    public string TerritoryName(int t) => _territory[t];
    public long PlaysAt(int c, int t) => _plays[c * MaxTerritories + t];
    public long RevenueAt(int c, int t) => _rev[c * MaxTerritories + t];
    public int BlockedCount => _nBlocked;
    public LedgerBlock Block(int back) => _ledger[(int)((LedgerHeight - 1 - back) % LedgerSize + LedgerSize) % LedgerSize];

    public static string Won(long milli) => (milli / Milli).ToString("#,0", CultureInfo.InvariantCulture) + "원";
    public static string StateName(SettlementState s) => s switch { SettlementState.Escrow => "에스크로", SettlementState.Settled => "정산 완료", _ => "대기" };

    private static bool Is(string k, string a, string b) => k.Equals(a, StringComparison.OrdinalIgnoreCase) || k.Equals(b, StringComparison.OrdinalIgnoreCase);

    private static bool TryAmount(string s, out long milli)
    {
        s = s.Replace(",", "", StringComparison.Ordinal).Replace("원", "", StringComparison.Ordinal).Trim();
        if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) && d >= 0) { milli = (long)decimal.Round(d * Milli); return true; }
        milli = 0; return false;
    }

    /// <summary>계약 파싱(제어 평면 · 상태 초기화). 반환 = 성공 여부(지분 합 100% · 보유자 ≥ 1 · 채널 ≥ 1).</summary>
    public bool Load(string? contract)
    {
        lock (_gate)
        {
            _nHolder = _nChannel = _nTerritory = 0; ParseErrors = 0; _nBlocked = 0;
            Array.Clear(_shareBp); Array.Clear(_recoupTarget); Array.Clear(_recouped); Array.Clear(_balance); Array.Clear(_paidTotal);
            Array.Clear(_plays); Array.Clear(_rev);
            _batchRevenue = _batchPlays = _batchPiracy = _batchLoss = _totalRevenue = _totalPaid = 0; TotalPlays = PiracyPlays = PiracyLoss = 0;
            LedgerHeight = 0; _ledgerCount = 0; Array.Clear(_tip);
            if (string.IsNullOrWhiteSpace(contract)) { ParseMessage = "⚠ 계약 비어 있음"; return false; }
            foreach (var raw in contract.Split('\n'))
            {
                var line = raw.Trim();
                var hash = line.IndexOf('#');
                if (hash >= 0) line = line[..hash].Trim();
                if (line.Length == 0) continue;
                var colon = line.IndexOf(':');
                if (colon < 0) { ParseErrors++; continue; }
                var k = line[..colon].Trim(); var body = line[(colon + 1)..].Trim();
                if (Is(k, "자산", "asset"))
                {
                    var bar = body.IndexOf('|');
                    AssetName = bar >= 0 ? body[..bar].Trim() : body; AssetId = bar >= 0 ? body[(bar + 1)..].Trim() : "";
                }
                else if (Is(k, "지분", "shares"))
                    foreach (var kv in body.Split(','))
                    {
                        var eq = kv.IndexOf('=');
                        if (eq < 0 || _nHolder >= MaxHolders || !decimal.TryParse(kv[(eq + 1)..].Replace("%", "", StringComparison.Ordinal).Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var pct)) { ParseErrors++; continue; }
                        _holder[_nHolder] = kv[..eq].Trim(); _shareBp[_nHolder] = (int)decimal.Round(pct * 100); _nHolder++;
                    }
                else if (Is(k, "회수", "recoup"))
                    foreach (var kv in body.Split(','))
                    {
                        var eq = kv.IndexOf('=');
                        var h = eq < 0 ? -1 : Array.IndexOf(_holder, kv[..eq].Trim(), 0, _nHolder);
                        if (h < 0 || !TryAmount(kv[(eq + 1)..], out var amt)) { ParseErrors++; continue; }
                        _recoupTarget[h] = amt;
                    }
                else if (Is(k, "요율", "rates"))
                    foreach (var kv in body.Split(','))
                    {
                        var eq = kv.IndexOf('=');
                        if (eq < 0 || _nChannel >= MaxChannels) { ParseErrors++; continue; }
                        var v = kv[(eq + 1)..].Trim();
                        RateUnit u; string num;
                        if (v.EndsWith("/재생", StringComparison.Ordinal) || v.EndsWith("/play", StringComparison.OrdinalIgnoreCase)) { u = RateUnit.PerPlay; num = v[..v.LastIndexOf('/')]; }
                        else if (v.EndsWith("/건", StringComparison.Ordinal) || v.EndsWith("/unit", StringComparison.OrdinalIgnoreCase)) { u = RateUnit.PerUnit; num = v[..v.LastIndexOf('/')]; }
                        else if (v.EndsWith("/회", StringComparison.Ordinal) || v.EndsWith("/airing", StringComparison.OrdinalIgnoreCase)) { u = RateUnit.PerAiring; num = v[..v.LastIndexOf('/')]; }
                        else if (v.EndsWith('%')) { u = RateUnit.RevenueShare; num = v[..^1]; }
                        else { ParseErrors++; continue; }
                        if (!TryAmount(num, out var r)) { ParseErrors++; continue; }
                        _channel[_nChannel] = kv[..eq].Trim(); _unit[_nChannel] = u; _rate[_nChannel] = u == RateUnit.RevenueShare ? r / 10 : r; _nChannel++; // 매출% = 0.01% 단위(r 밀리 → bp)
                    }
                else if (Is(k, "지역", "territories"))
                    foreach (var t in body.Split(','))
                    {
                        if (string.IsNullOrWhiteSpace(t) || _nTerritory >= MaxTerritories) continue;
                        _territory[_nTerritory++] = t.Trim();
                    }
                else ParseErrors++;
            }
            var sum = 0; for (var h = 0; h < _nHolder; h++) sum += _shareBp[h];
            if (_nTerritory == 0) _territory[_nTerritory++] = "GLOBAL";
            var ok = _nHolder > 0 && _nChannel > 0 && sum == 10000;
            ParseMessage = ok ? "📜 계약 로드 — 보유자 " + _nHolder.ToString(CultureInfo.InvariantCulture) + " · 채널 " + _nChannel.ToString(CultureInfo.InvariantCulture) + " · 지역 " + _nTerritory.ToString(CultureInfo.InvariantCulture)
                : "⚠ 계약 무효 — 지분 합 " + (sum / 100.0).ToString("0.##", CultureInfo.InvariantCulture) + "% (100% 필요) · 오류 " + ParseErrors.ToString(CultureInfo.InvariantCulture);
            Valid = ok;
            return ok;
        }
    }

    public bool Valid { get; private set; }

    /// <summary>유출 디바이스 차단(워터마크 역추적 ID · 무할당 · 상한 64).</summary>
    public bool BlockDevice(ulong payloadId)
    {
        lock (_gate)
        {
            for (var i = 0; i < _nBlocked; i++) if (_blocked[i] == payloadId) return false;
            if (_nBlocked >= MaxBlocked) return false;
            _blocked[_nBlocked++] = payloadId;
            return true;
        }
    }

    public bool IsBlocked(ulong payloadId)
    {
        for (var i = 0; i < _nBlocked; i++) if (_blocked[i] == payloadId) return true;
        return false;
    }

    /// <summary>재생 이벤트(무할당): 차단 디바이스 = 불법 손실.</summary>
    public void RecordPlay(int channel, int territory, ulong devicePayloadId, int count = 1)
    {
        lock (_gate)
        {
            if (!Valid || (uint)channel >= (uint)_nChannel || (uint)territory >= (uint)_nTerritory) return;
            var amt = _unit[channel] is RateUnit.PerPlay or RateUnit.PerUnit or RateUnit.PerAiring ? _rate[channel] * count : 0;
            if (IsBlocked(devicePayloadId)) { _batchPiracy += count; PiracyPlays += count; _batchLoss += amt; PiracyLoss += amt; return; }
            _plays[channel * MaxTerritories + territory] += count; _rev[channel * MaxTerritories + territory] += amt;
            _batchRevenue += amt; _batchPlays += count; TotalPlays += count;
        }
    }

    /// <summary>판매/매출(매출% 채널 = 총액 × bp/10000 · 그 외 = 총액 그대로 · 밀리원).</summary>
    public void RecordSale(int channel, int territory, long grossMilli)
    {
        lock (_gate)
        {
            if (!Valid || (uint)channel >= (uint)_nChannel || (uint)territory >= (uint)_nTerritory) return;
            var amt = _unit[channel] == RateUnit.RevenueShare ? grossMilli * _rate[channel] / 10000 : grossMilli;
            _rev[channel * MaxTerritories + territory] += amt; _batchRevenue += amt;
        }
    }

    /// <summary>워터폴 분배(무할당): 회수 순서대로 100% → 잔여 지분 비례(최대 잔여 반올림) · Σ = revenue 정확.</summary>
    public void Distribute(long revenue, Span<long> payout)
    {
        payout[.._nHolder].Clear();
        var left = revenue;
        for (var h = 0; h < _nHolder && left > 0; h++)
        {
            var need = _recoupTarget[h] - _recouped[h];
            if (need <= 0) continue;
            var take = Math.Min(need, left);
            payout[h] += take; _recouped[h] += take; left -= take;
        }
        if (left <= 0) return;
        long assigned = 0;
        for (var h = 0; h < _nHolder; h++)
        {
            var exact = (decimal)left * _shareBp[h];
            var floor = (long)(exact / 10000m);
            payout[h] += floor; assigned += floor; _rem[h] = (long)(exact - floor * 10000m);
        }
        var rest = left - assigned;
        while (rest > 0)
        {
            var best = 0; for (var h = 1; h < _nHolder; h++) if (_rem[h] > _rem[best]) best = h;
            payout[best]++; _rem[best] = -1; rest--;
        }
    }

    private int Canonical(LedgerBlock b)
    {
        var s = _canon.AsSpan();
        "PFSLEDGR"u8.CopyTo(s);
        BinaryPrimitives.WriteInt64LittleEndian(s[8..], b.Index);
        BinaryPrimitives.WriteInt64LittleEndian(s[16..], b.SimTimeMs);
        BinaryPrimitives.WriteInt64LittleEndian(s[24..], b.Revenue);
        BinaryPrimitives.WriteInt64LittleEndian(s[32..], b.Plays);
        BinaryPrimitives.WriteInt64LittleEndian(s[40..], b.PiracyPlays);
        BinaryPrimitives.WriteInt64LittleEndian(s[48..], b.PiracyLoss);
        BinaryPrimitives.WriteInt32LittleEndian(s[56..], b.Holders);
        long sum = 0; for (var h = 0; h < b.Holders; h++) sum += b.Payout[h];
        BinaryPrimitives.WriteInt64LittleEndian(s[60..], sum);   // 집계: 지급 합
        b.PrevHash.AsSpan().CopyTo(s[68..]);
        for (var h = 0; h < b.Holders; h++) BinaryPrimitives.WriteInt64LittleEndian(s[(100 + h * 8)..], b.Payout[h]);
        return 100 + b.Holders * 8;
    }

    /// <summary>배치 정산(무할당): 분배 → 블록 봉인(해시 체인 · 서명) → 확인 수 기반 상태 전이. 반환 = 블록 인덱스(−1 = 무효 계약).</summary>
    public long Settle(long simTimeMs)
    {
        lock (_gate)
        {
            if (!Valid) return -1;
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Distribute(_batchRevenue, _batchPayout);
            var b = _ledger[(int)(LedgerHeight % LedgerSize)];
            b.Index = LedgerHeight; b.SimTimeMs = simTimeMs; b.Revenue = _batchRevenue; b.Plays = _batchPlays; b.PiracyPlays = _batchPiracy; b.PiracyLoss = _batchLoss; b.Holders = _nHolder;
            Array.Clear(b.Payout);
            for (var h = 0; h < _nHolder; h++) { b.Payout[h] = _batchPayout[h]; _balance[h] += _batchPayout[h]; }
            _tip.AsSpan().CopyTo(b.PrevHash);
            var n = Canonical(b);
            SHA256.HashData(_canon.AsSpan(0, n), b.Hash);
            HMACSHA512.HashData(_key, b.Hash, b.Signature);
            b.Hash.AsSpan().CopyTo(_tip);
            b.State = SettlementState.Pending;
            _totalRevenue += _batchRevenue;
            _batchRevenue = _batchPlays = _batchPiracy = _batchLoss = 0;
            LedgerHeight++; if (_ledgerCount < LedgerSize) _ledgerCount++;
            // 확인: 이후 블록 수 ≥ 1 → 에스크로 · ≥ Confirmations → 정산(지급 확정)
            for (var back = 0; back < _ledgerCount; back++)
            {
                var x = Block(back);
                var conf = back;
                if (x.State == SettlementState.Pending && conf >= 1) x.State = SettlementState.Escrow;
                if (x.State == SettlementState.Escrow && conf >= Confirmations)
                {
                    x.State = SettlementState.Settled;
                    for (var h = 0; h < x.Holders; h++) { _paidTotal[h] += x.Payout[h]; _balance[h] -= x.Payout[h]; _totalPaid += x.Payout[h]; }
                }
                if (conf > Confirmations + 1) break;
            }
            LastSettleMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            return b.Index;
        }
    }

    /// <summary>원장 검증(링 내 블록 · 해시 재계산 + 이전 해시 연결 + 서명 · FixedTimeEquals). 반환 = 첫 불일치 블록 인덱스(−1 = 무결).</summary>
    public long VerifyLedger()
    {
        lock (_gate)
        {
            Span<byte> h = stackalloc byte[32]; Span<byte> sig = stackalloc byte[64];
            for (var back = _ledgerCount - 1; back >= 0; back--)
            {
                var b = Block(back);
                var n = Canonical(b);
                SHA256.HashData(_canon.AsSpan(0, n), h);
                if (!CryptographicOperations.FixedTimeEquals(h, b.Hash)) return b.Index;
                HMACSHA512.HashData(_key, b.Hash, sig);
                if (!CryptographicOperations.FixedTimeEquals(sig, b.Signature)) return b.Index;
                if (back < _ledgerCount - 1 && !CryptographicOperations.FixedTimeEquals(b.PrevHash, Block(back + 1).Hash)) return b.Index;
            }
            return -1;
        }
    }

    /// <summary>보유자 수령 합(잔액 + 지급 완료) — 보존 검증용.</summary>
    public long Entitled(int h) => _balance[h] + _paidTotal[h];
}
