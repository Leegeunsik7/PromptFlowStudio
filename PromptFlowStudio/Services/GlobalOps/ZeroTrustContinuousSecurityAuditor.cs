using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PromptFlowStudio.Services.GlobalOps;

public enum ThreatKind : byte { KeyLeak, MemoryTamper, PacketMalformed, PacketForged, PacketReplay, PacketFlood }

public enum ThreatSeverity : byte { Low, Medium, High, Critical }

public enum PacketVerdict : byte { Accepted, Malformed, BadMac, Replay, Stale, RateLimited, Quarantined }

/// <summary>탐지 항목(값 형식 · 상세는 고정 코드 + 위치 — 원문 비노출).</summary>
public readonly record struct ThreatFinding(double TimeMs, ThreatKind Kind, ThreatSeverity Severity, int Source, int Code, int Offset);

/// <summary>
/// [PromptFlow Studio v16.52.0] 🔐 제로 트러스트 연속 보안 &amp; 취약점 감사기.
///   ▸ API 키 유출 스캔: 프리픽스 패턴(Google AIza · OpenAI sk- · Anthropic sk-ant- · AWS AKIA/ASIA · GitHub ghp_/github_pat_ · Slack xox · Stripe sk_live · PEM 개인키) + 고엔트로피 토큰(Shannon ≥ 4.0 · 32자+) — Span 스캔 무할당 · 원문 대신 마스킹 리댁션.
///   ▸ 메모리 변조 감시: 보호 영역 등록(바이트 버퍼 · HMAC-SHA256 기준선) → 주기 검증(stackalloc · FixedTimeEquals) → 변조 시 봉인 사본으로 자동 복원 + 격리 카운트.
///   ▸ 패킷 주입 방어: 헤더(매직 · 버전 · 길이) · HMAC-SHA256 truncated 16B · 64비트 슬라이딩 재생 윈도 · 타임스탬프 스큐 · 소스별 토큰 버킷 · 누적 위반 → 소스 격리(쿨다운 해제) — Inspect 무할당.
///   ▸ 지속 감사 Tick · 보안 점수(0~100) · 발견 링 버퍼.
/// </summary>
public sealed class ZeroTrustContinuousSecurityAuditor
{
    public const int MaxSources = 256, MaxRegions = 32, FindingCap = 512, HeaderSize = 20, MacSize = 16;
    public const uint Magic = 0x50465331;   // "PFS1"
    public const byte ProtoVersion = 1;
    public const string PacketKeyName = "zt_packet_hmac", MemoryKeyName = "zt_memory_hmac";

    private static readonly string[] KeyPrefixes = { "AIza", "sk-ant-", "sk-proj-", "sk-", "AKIA", "ASIA", "ghp_", "gho_", "github_pat_", "xoxb-", "xoxp-", "sk_live_", "rk_live_", "-----BEGIN PRIVATE KEY", "-----BEGIN RSA PRIVATE KEY", "-----BEGIN EC PRIVATE KEY" };
    private static readonly int[] PrefixMinTail = { 30, 40, 30, 32, 16, 16, 30, 30, 40, 20, 20, 20, 20, 0, 0, 0 };

    private readonly byte[] _packetKey, _memKey;
    private readonly ThreatFinding[] _findings = new ThreatFinding[FindingCap];
    private readonly ulong[] _replayMask = new ulong[MaxSources];
    private readonly ulong[] _replayTop = new ulong[MaxSources];
    private readonly double[] _tokens = new double[MaxSources], _lastRefill = new double[MaxSources], _quarantineUntil = new double[MaxSources];
    private readonly int[] _strikes = new int[MaxSources];
    private readonly byte[]?[] _regions = new byte[MaxRegions][], _sealed = new byte[MaxRegions][];
    private readonly byte[][] _regionMac = new byte[MaxRegions][];
    private readonly string?[] _regionNames = new string[MaxRegions];
    private readonly object _gate = new();
    private int _fHead, _fCount, _regionCount;
    private long _scanned, _leaks, _tamper, _restored, _packets, _accepted, _blocked, _quarantines;
    private double _nextMemCheck;

    public ZeroTrustContinuousSecurityAuditor(IServiceProvider sp)
        : this(sp.GetService(typeof(Security.SecureSecretVault)) is Security.SecureSecretVault v ? v.ResolveOrCreate(PacketKeyName) : SHA256.HashData("pfs-zt-packet-dev"u8),
               sp.GetService(typeof(Security.SecureSecretVault)) is Security.SecureSecretVault v2 ? v2.ResolveOrCreate(MemoryKeyName) : SHA256.HashData("pfs-zt-memory-dev"u8)) { }

    public ZeroTrustContinuousSecurityAuditor(byte[] packetKey, byte[] memoryKey)
    {
        _packetKey = packetKey; _memKey = memoryKey;
        Array.Fill(_tokens, -1);
    }

    public event Action? OnChange;
    public event Action<ThreatFinding>? OnThreat;

    public double RatePerSec { get; set; } = 200;
    public double Burst { get; set; } = 400;
    public double MaxSkewMs { get; set; } = 30000;
    public int QuarantineStrikes { get; set; } = 5;
    public double QuarantineMs { get; set; } = 60000;
    public double MemoryCheckEveryMs { get; set; } = 1000;
    public double EntropyThreshold { get; set; } = 4.0;
    public long Scanned => Interlocked.Read(ref _scanned);
    public long Leaks => Interlocked.Read(ref _leaks);
    public long TamperDetected => Interlocked.Read(ref _tamper);
    public long Restored => Interlocked.Read(ref _restored);
    public long Packets => Interlocked.Read(ref _packets);
    public long Accepted => Interlocked.Read(ref _accepted);
    public long Blocked => Interlocked.Read(ref _blocked);
    public long Quarantines => Interlocked.Read(ref _quarantines);
    public int RegionCount => _regionCount;
    public string? RegionName(int i) => _regionNames[i];
    public bool IsQuarantined(int source, double nowMs) => (uint)source < MaxSources && nowMs < _quarantineUntil[source];
    public int QuarantinedSources(double nowMs) { var n = 0; for (var i = 0; i < MaxSources; i++) if (nowMs < _quarantineUntil[i]) n++; return n; }
    public string? LastAction { get; private set; }

    /// <summary>보안 점수 0~100(최근 위협 심각도 가중 감점).</summary>
    public int Score(double nowMs, double windowMs = 300000)
    {
        lock (_gate)
        {
            var s = 100.0;
            for (var i = 0; i < _fCount; i++)
            {
                var f = _findings[(_fHead - 1 - i + FindingCap) % FindingCap];
                if (nowMs - f.TimeMs > windowMs) break;
                s -= f.Severity switch { ThreatSeverity.Critical => 15, ThreatSeverity.High => 6, ThreatSeverity.Medium => 2, _ => 0.5 };
            }
            return (int)Math.Max(0, s);
        }
    }

    public int CopyFindings(Span<ThreatFinding> dst)
    {
        lock (_gate)
        {
            var n = Math.Min(dst.Length, _fCount);
            for (var i = 0; i < n; i++) dst[i] = _findings[(_fHead - 1 - i + FindingCap) % FindingCap];
            return n;
        }
    }

    private void Report(ThreatFinding f)
    {
        lock (_gate) { _findings[_fHead] = f; _fHead = (_fHead + 1) % FindingCap; if (_fCount < FindingCap) _fCount++; }
        try { OnThreat?.Invoke(f); } catch (Exception ex) { PromptFlowStudio.Program.Log("ZeroTrust.OnThreat", ex); }
    }

    // ══════════════════════ API 키 유출 ══════════════════════

    /// <summary>텍스트 스캔 → 탐지 수(위치 · 패턴 코드 기록). 무할당.</summary>
    public int ScanForSecrets(ReadOnlySpan<char> text, double nowMs, int source = -1)
    {
        Interlocked.Increment(ref _scanned);
        var hits = 0;
        var i = 0;
        while (i < text.Length)
        {
            if (!IsTokenChar(text[i]) && text[i] != '-') { i++; continue; }
            var start = i;
            while (i < text.Length && (IsTokenChar(text[i]) || text[i] == '-' && i - start < 40)) i++;
            var tok = text[start..i];
            var code = FindPrefix(text, start, i, out var at, out var consumed);
            if (code >= 0) { hits++; Report(new ThreatFinding(nowMs, ThreatKind.KeyLeak, code >= 13 ? ThreatSeverity.Critical : ThreatSeverity.High, source, code, at)); i = Math.Max(i, at + consumed); continue; }
            if (tok.Length >= 32 && HasMixedClasses(tok) && ShannonEntropy(tok) >= EntropyThreshold) { hits++; Report(new ThreatFinding(nowMs, ThreatKind.KeyLeak, ThreatSeverity.Medium, source, 100, start)); }
        }
        if (hits > 0) { Interlocked.Add(ref _leaks, hits); LastAction = "🔑 키 유출 의심 " + hits.ToString(CultureInfo.InvariantCulture) + "건"; Raise(); }
        return hits;
    }

    /// <summary>토큰 [start, end) 내 프리픽스 탐색 — 토큰 시작 또는 구분자(= : / +) 직후 위치만 시도. 무할당.</summary>
    private static int FindPrefix(ReadOnlySpan<char> text, int start, int end, out int at, out int consumed)
    {
        for (var j = start; j < end; j++)
        {
            if (j > start && text[j - 1] is not ('=' or ':' or '/' or '+')) continue;
            var code = MatchPrefix(text[j..], out consumed);
            if (code >= 0) { at = j; return code; }
        }
        at = start; consumed = 0;
        return -1;
    }

    private static bool IsTokenChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '+' or '/' or '=';

    /// <summary>프리픽스 매칭: 반환 = 패턴 코드(-1 없음), consumed = 키 길이.</summary>
    public static int MatchPrefix(ReadOnlySpan<char> s, out int consumed)
    {
        consumed = 0;
        for (var p = 0; p < KeyPrefixes.Length; p++)
        {
            var pre = KeyPrefixes[p];
            if (!s.StartsWith(pre, StringComparison.Ordinal)) continue;
            if (pre[0] == '-') { consumed = pre.Length; return p; }
            var tail = 0;
            while (pre.Length + tail < s.Length && (char.IsAsciiLetterOrDigit(s[pre.Length + tail]) || s[pre.Length + tail] is '_' or '-')) tail++;
            if (tail >= PrefixMinTail[p]) { consumed = pre.Length + tail; return p; }
        }
        return -1;
    }

    public static string PatternLabel(int code) => code switch
    {
        0 => "Google API 키", 1 => "Anthropic 키", 2 or 3 => "OpenAI 키", 4 or 5 => "AWS 액세스 키", 6 or 7 or 8 => "GitHub 토큰", 9 or 10 => "Slack 토큰", 11 or 12 => "Stripe 라이브 키",
        13 or 14 or 15 => "PEM 개인키", 100 => "고엔트로피 시크릿", _ => "알 수 없음",
    };

    private static bool HasMixedClasses(ReadOnlySpan<char> t)
    {
        bool up = false, lo = false, di = false;
        foreach (var c in t) { if (char.IsAsciiLetterUpper(c)) up = true; else if (char.IsAsciiLetterLower(c)) lo = true; else if (char.IsAsciiDigit(c)) di = true; }
        return (up ? 1 : 0) + (lo ? 1 : 0) + (di ? 1 : 0) >= 3;
    }

    /// <summary>Shannon 엔트로피(bit/char) — ASCII 히스토그램 stackalloc.</summary>
    public static double ShannonEntropy(ReadOnlySpan<char> t)
    {
        if (t.IsEmpty) return 0;
        Span<int> h = stackalloc int[128];
        h.Clear();
        foreach (var c in t) h[c & 127]++;
        double e = 0;
        foreach (var n in h) if (n > 0) { var p = n / (double)t.Length; e -= p * Math.Log2(p); }
        return e;
    }

    /// <summary>탐지된 키를 마스킹한 사본(앞 4자 유지 · 나머지 '*').</summary>
    public string Redact(string text)
    {
        var buf = text.ToCharArray();
        var i = 0;
        while (i < buf.Length)
        {
            if (!IsTokenChar(buf[i]) && buf[i] != '-') { i++; continue; }
            var start = i;
            while (i < buf.Length && (IsTokenChar(buf[i]) || buf[i] == '-' && i - start < 40)) i++;
            var code = FindPrefix(text, start, i, out var at, out var consumed);
            if (code >= 0) { for (var k = at + 4; k < at + consumed && k < buf.Length; k++) buf[k] = '*'; i = Math.Max(i, at + consumed); continue; }
            var len = i - start;
            if (len >= 32 && HasMixedClasses(text.AsSpan(start, len)) && ShannonEntropy(text.AsSpan(start, len)) >= EntropyThreshold)
                for (var k = start + 4; k < start + len; k++) buf[k] = '*';
        }
        return new string(buf);
    }

    // ══════════════════════ 메모리 변조 ══════════════════════

    /// <summary>보호 영역 등록(라이브 버퍼 참조 + 봉인 사본 + HMAC 기준선). 반환 = 영역 인덱스.</summary>
    public int Protect(string name, byte[] live)
    {
        lock (_gate)
        {
            if (_regionCount >= MaxRegions) throw new InvalidOperationException("보호 영역 상한(32) 초과");
            var i = _regionCount++;
            _regionNames[i] = name; _regions[i] = live; _sealed[i] = (byte[])live.Clone();
            _regionMac[i] = HMACSHA256.HashData(_memKey, live);
            return i;
        }
    }

    /// <summary>합법적 갱신 후 재봉인.</summary>
    public void Reseal(int region)
    {
        lock (_gate) { var b = _regions[region]!; _sealed[region] = (byte[])b.Clone(); _regionMac[region] = HMACSHA256.HashData(_memKey, b); }
    }

    /// <summary>전 영역 검증 → 변조 영역 복원. 반환 = 변조 수. 무할당.</summary>
    public int VerifyMemory(double nowMs)
    {
        var bad = 0;
        Span<byte> mac = stackalloc byte[32];
        lock (_gate)
        {
            for (var i = 0; i < _regionCount; i++)
            {
                var live = _regions[i]!;
                HMACSHA256.HashData(_memKey, live, mac);
                if (CryptographicOperations.FixedTimeEquals(mac, _regionMac[i])) continue;
                bad++;
                _sealed[i]!.AsSpan(0, Math.Min(live.Length, _sealed[i]!.Length)).CopyTo(live);
                Interlocked.Increment(ref _tamper); Interlocked.Increment(ref _restored);
                Report(new ThreatFinding(nowMs, ThreatKind.MemoryTamper, ThreatSeverity.Critical, -1, i, 0));
            }
        }
        if (bad > 0) { LastAction = "🧬 메모리 변조 " + bad.ToString(CultureInfo.InvariantCulture) + "영역 → 봉인 사본 복원"; Raise(); }
        return bad;
    }

    // ══════════════════════ 패킷 검사 ══════════════════════

    /// <summary>패킷 서명(헤더 + 페이로드 + MAC16). 레이아웃: magic(4) ver(1) flags(1) source(2) seq(8) len(4) payload mac(16).</summary>
    public int Seal(Span<byte> dst, ushort source, ulong seq, ReadOnlySpan<byte> payload, uint flags = 0)
    {
        var total = HeaderSize + payload.Length + MacSize;
        if (dst.Length < total) throw new ArgumentException("버퍼 부족", nameof(dst));
        BinaryPrimitives.WriteUInt32BigEndian(dst, Magic);
        dst[4] = ProtoVersion; dst[5] = (byte)flags;
        BinaryPrimitives.WriteUInt16BigEndian(dst[6..], source);
        BinaryPrimitives.WriteUInt64BigEndian(dst[8..], seq);
        BinaryPrimitives.WriteUInt32BigEndian(dst[16..], (uint)payload.Length);
        payload.CopyTo(dst[HeaderSize..]);
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(_packetKey, dst[..(HeaderSize + payload.Length)], mac);
        mac[..MacSize].CopyTo(dst[(HeaderSize + payload.Length)..]);
        return total;
    }

    /// <summary>패킷 검사(격리 → 레이트 → 형식 → MAC → 스큐 → 재생). 무할당.</summary>
    public PacketVerdict Inspect(ReadOnlySpan<byte> pkt, double nowMs, double sentAtMs = double.NaN)
    {
        Interlocked.Increment(ref _packets);
        var src = pkt.Length >= 8 ? BinaryPrimitives.ReadUInt16BigEndian(pkt[6..]) % MaxSources : 0;
        if (nowMs < _quarantineUntil[src]) { Interlocked.Increment(ref _blocked); return PacketVerdict.Quarantined; }
        lock (_gate)
        {
            if (_tokens[src] < 0) { _tokens[src] = Burst; _lastRefill[src] = nowMs; }
            _tokens[src] = Math.Min(Burst, _tokens[src] + (nowMs - _lastRefill[src]) / 1000 * RatePerSec);
            _lastRefill[src] = nowMs;
            if (_tokens[src] < 1) return Fail(src, PacketVerdict.RateLimited, ThreatKind.PacketFlood, ThreatSeverity.Medium, nowMs);
            _tokens[src] -= 1;
        }
        if (pkt.Length < HeaderSize + MacSize || BinaryPrimitives.ReadUInt32BigEndian(pkt) != Magic || pkt[4] != ProtoVersion)
            return Fail(src, PacketVerdict.Malformed, ThreatKind.PacketMalformed, ThreatSeverity.Medium, nowMs);
        var len = BinaryPrimitives.ReadUInt32BigEndian(pkt[16..]);
        if (len > 1 << 20 || HeaderSize + (long)len + MacSize != pkt.Length) return Fail(src, PacketVerdict.Malformed, ThreatKind.PacketMalformed, ThreatSeverity.Medium, nowMs);
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(_packetKey, pkt[..(HeaderSize + (int)len)], mac);
        if (!CryptographicOperations.FixedTimeEquals(mac[..MacSize], pkt[(HeaderSize + (int)len)..])) return Fail(src, PacketVerdict.BadMac, ThreatKind.PacketForged, ThreatSeverity.High, nowMs);
        if (!double.IsNaN(sentAtMs) && Math.Abs(nowMs - sentAtMs) > MaxSkewMs) return Fail(src, PacketVerdict.Stale, ThreatKind.PacketReplay, ThreatSeverity.Medium, nowMs);
        var seq = BinaryPrimitives.ReadUInt64BigEndian(pkt[8..]);
        lock (_gate)
        {
            var top = _replayTop[src];
            if (seq > top)
            {
                var shift = seq - top;
                _replayMask[src] = shift >= 64 ? 1UL : (_replayMask[src] << (int)shift) | 1UL;
                _replayTop[src] = seq;
            }
            else
            {
                var off = top - seq;
                if (off >= 64 || (_replayMask[src] & (1UL << (int)off)) != 0) return Fail(src, PacketVerdict.Replay, ThreatKind.PacketReplay, ThreatSeverity.High, nowMs);
                _replayMask[src] |= 1UL << (int)off;
            }
        }
        Interlocked.Increment(ref _accepted);
        return PacketVerdict.Accepted;
    }

    private PacketVerdict Fail(int src, PacketVerdict v, ThreatKind kind, ThreatSeverity sev, double nowMs)
    {
        Interlocked.Increment(ref _blocked);
        Report(new ThreatFinding(nowMs, kind, sev, src, (int)v, 0));
        var q = false;
        lock (_gate)
        {
            if (++_strikes[src] >= QuarantineStrikes) { _quarantineUntil[src] = nowMs + QuarantineMs; _strikes[src] = 0; q = true; }
        }
        if (q) { Interlocked.Increment(ref _quarantines); LastAction = "⛔ 소스 #" + src.ToString(CultureInfo.InvariantCulture) + " 격리(" + VerdictLabel(v) + ")"; Raise(); }
        return v;
    }

    public void ReleaseSource(int src) { lock (_gate) { _quarantineUntil[src] = 0; _strikes[src] = 0; } Raise(); }

    // ══════════════════════ 지속 감사 ══════════════════════

    /// <summary>주기 틱: 메모리 검증(주기) · 격리 만료 정리. 반환 = 이번 틱 변조 수.</summary>
    public int Tick(double nowMs)
    {
        if (nowMs < _nextMemCheck) return 0;
        _nextMemCheck = nowMs + MemoryCheckEveryMs;
        return VerifyMemory(nowMs);
    }

    public static string VerdictLabel(PacketVerdict v) => v switch
    {
        PacketVerdict.Accepted => "수락", PacketVerdict.Malformed => "형식 오류", PacketVerdict.BadMac => "위조 MAC", PacketVerdict.Replay => "재생", PacketVerdict.Stale => "시각 스큐",
        PacketVerdict.RateLimited => "폭주", _ => "격리",
    };

    public static string KindLabel(ThreatKind k) => k switch
    {
        ThreatKind.KeyLeak => "🔑 키 유출", ThreatKind.MemoryTamper => "🧬 메모리 변조", ThreatKind.PacketMalformed => "📦 비정상 패킷", ThreatKind.PacketForged => "🪪 위조 패킷",
        ThreatKind.PacketReplay => "🔁 재생 공격", _ => "🌊 패킷 폭주",
    };

    private void Raise() { try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("ZeroTrust.OnChange", ex); } }
}
