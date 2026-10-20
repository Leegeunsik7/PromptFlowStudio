using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Security;

namespace PromptFlowStudio.Services.Empire;

public enum SystemHealth : byte { Unknown, Healthy, Degraded, Down, Healing, Quarantined }

public enum RevenueStream : byte { Ads, Subscription, Donation, Commerce, Licensing }

public enum Currency : byte { Usd, Krw, Jpy, Eur, Gbp, Brl, Inr, Cny }

/// <summary>마일스톤 시스템 상태 슬롯.</summary>
public sealed class SystemSlot
{
    public string Version { get; init; } = "";
    public string Name { get; init; } = "";
    public double IntervalMs { get; init; } = 1000;
    public Func<bool>? Healer { get; set; }
    internal double LastBeat = double.NegativeInfinity, NextHealAt;
    internal int HealFails;
    public SystemHealth Health { get; internal set; }
    public long Beats { get; internal set; }
    public long Errors { get; internal set; }
    public long Heals { get; internal set; }
    public int HealFailures => HealFails;
}

/// <summary>메모리 하이퍼 관제 스냅샷(관리 힙 · 할당률 · GC 세대 수 · Zero-Alloc 위반).</summary>
public readonly record struct MemorySample(double TimeMs, long HeapBytes, double AllocBytesPerSec, int Gen0, int Gen1, int Gen2, long ZeroGcViolations, bool Alarm);

/// <summary>봉인된 정산서(집계 필드 · 지분 분배 · HMAC-SHA256).</summary>
public sealed record EmpireSettlement(long Seq, DateTimeOffset At, long TotalMicros, long Impressions, int Entries, IReadOnlyList<(string Party, long Micros)> Splits, string PrevHash, string Hash);

/// <summary>
/// [PromptFlow Studio v16.50.0] 📊 총괄 제국 실시간 수익 &amp; 텔레메트리 중앙 커맨드 매트릭스.
///   ▸ 50개 마일스톤 시스템(v16.0~v16.49) 하트비트 · 오류율 → Healthy/Degraded/Down · 자율 복구(Self-Healing: 힐러 호출 · 지수 백오프 · 연속 실패 3회 격리).
///   ▸ Zero-Alloc 메모리 하이퍼 관제: 관리 힙 · 스레드 무관 총 할당률 · GC 세대 카운트 · ZeroGcCounters 위반 · 링 버퍼 샘플 · 할당률 경보.
///   ▸ 글로벌 수익 매트릭스: 플랫폼 × 수익원(광고/구독/후원/커머스/라이선스) 마이크로 USD(Interlocked) · 통화 환산 · 플랫폼 eCPM · 분당 수익 링.
///   ▸ 정산: 지분권자 bps 정수 분배(나머지 1순위 · 손실 0) · HMAC 체인 봉인(canonical 집계 필드) · FixedTimeEquals 검증 · 키 = SecureSecretVault(환경변수/DPAPI).
///   ▸ Record/Beat/Tick 무할당.
/// </summary>
public sealed class ExecutiveEmpireTelemetryAndRevenueMatrix
{
    public const int Streams = 5, MinuteRing = 60, MemRing = 120, MaxHealFails = 3;
    public const string SettlementKeyName = "empire_settlement_hmac";

    private static readonly long[] MicrosPerUnit = { 1_000_000, 730, 6_700, 1_080_000, 1_270_000, 180_000, 12_000, 138_000 };   // 1 단위 → 마이크로 USD

    private readonly SystemSlot[] _systems;
    private readonly long[,] _revenue;
    private readonly long[] _impr;
    private readonly long[] _minute = new long[MinuteRing];
    private readonly MemorySample[] _mem = new MemorySample[MemRing];
    private readonly List<EmpireSettlement> _ledger = new();
    private readonly Func<byte[]> _key;
    private readonly ZeroGcCounters _zgc;
    private readonly object _gate = new();
    private int _memHead, _memCount, _minuteHead;
    private long _minuteEpoch = -1, _settledMicros, _heals, _quarantines, _entries;
    private long _lastAlloc = -1;
    private double _lastMemMs;

    public ExecutiveEmpireTelemetryAndRevenueMatrix(IServiceProvider sp)
        : this(sp.GetService(typeof(SecureSecretVault)) is SecureSecretVault v ? () => v.ResolveOrCreate(SettlementKeyName) : () => SHA256.HashData("pfs-empire-settlement-dev"u8),
               OmnichannelGlobalBroadcastDispatcher.Catalog().Length, null) { }

    public ExecutiveEmpireTelemetryAndRevenueMatrix(Func<byte[]> key, int platforms, ZeroGcCounters? counters = null)
    {
        _key = key;
        _zgc = counters ?? ZeroGcCounters.Global;
        _systems = MilestoneCatalog();
        _revenue = new long[platforms, Streams];
        _impr = new long[platforms];
    }

    public event Action? OnChange;

    public double AllocAlarmBytesPerSec { get; set; } = 64L * 1024 * 1024;
    public double DegradedFactor { get; set; } = 2.5;
    public double DownFactor { get; set; } = 6;
    public double HealBackoffMs { get; set; } = 1000;
    public List<(string Party, int Bps)> Stakeholders { get; } = new() { ("크리에이터", 5500), ("플랫폼 운영", 2500), ("IP 라이선서", 1200), ("AI 인프라", 800) };
    public int SystemCount => _systems.Length;
    public SystemSlot System(int i) => _systems[i];
    public int PlatformCount => _impr.Length;
    public long Heals => Interlocked.Read(ref _heals);
    public long Quarantines => Interlocked.Read(ref _quarantines);
    public long Entries => Interlocked.Read(ref _entries);
    public long SettledMicros => Interlocked.Read(ref _settledMicros);
    public IReadOnlyList<EmpireSettlement> Ledger { get { lock (_gate) return _ledger.ToArray(); } }
    public string? LastAction { get; private set; }

    public int CountHealth(SystemHealth h) { var n = 0; foreach (var s in _systems) if (s.Health == h) n++; return n; }

    // ══════════════════════ 시스템 헬스 · 자율 복구 ══════════════════════

    /// <summary>하트비트(정상/오류). 무할당.</summary>
    public void Beat(int sys, double nowMs, bool ok = true)
    {
        if ((uint)sys >= (uint)_systems.Length) return;
        var s = _systems[sys];
        if (s.Health == SystemHealth.Quarantined) return;
        s.Beats++;
        if (!ok) { s.Errors++; return; }
        s.LastBeat = nowMs;
    }

    /// <summary>격리 해제(운영자 수동).</summary>
    public void Release(int sys) { var s = _systems[sys]; s.HealFails = 0; s.Health = SystemHealth.Unknown; s.LastBeat = double.NegativeInfinity; Raise(); }

    /// <summary>헬스 평가 + 자율 복구. 반환 = 상태 변화 수. 힐러 외 무할당.</summary>
    public int Evaluate(double nowMs)
    {
        var changed = 0;
        foreach (var s in _systems)
        {
            if (s.Health == SystemHealth.Quarantined) continue;
            var gap = nowMs - s.LastBeat;
            var errRate = s.Beats > 0 ? s.Errors / (double)s.Beats : 0;
            var h = double.IsNegativeInfinity(s.LastBeat) ? SystemHealth.Unknown
                : gap > s.IntervalMs * DownFactor ? SystemHealth.Down
                : gap > s.IntervalMs * DegradedFactor || errRate > 0.2 ? SystemHealth.Degraded : SystemHealth.Healthy;
            if (h == SystemHealth.Down && nowMs >= s.NextHealAt)
            {
                s.Health = SystemHealth.Healing;
                var ok = false;
                try { ok = s.Healer?.Invoke() ?? false; }
                catch (Exception ex) { PromptFlowStudio.Program.Log("Empire.Heal", ex); }
                if (ok)
                {
                    s.Heals++; s.HealFails = 0; s.LastBeat = nowMs; s.Errors = 0; s.Beats = 0; h = SystemHealth.Healthy;
                    Interlocked.Increment(ref _heals);
                    LastAction = "🩹 자율 복구 · " + s.Version + " " + s.Name;
                }
                else if (++s.HealFails >= MaxHealFails)
                {
                    h = SystemHealth.Quarantined;
                    Interlocked.Increment(ref _quarantines);
                    LastAction = "⛔ 격리 · " + s.Version + " " + s.Name;
                }
                else { s.NextHealAt = nowMs + HealBackoffMs * (1 << s.HealFails); h = SystemHealth.Down; }
            }
            if (h != s.Health) { s.Health = h; changed++; }
        }
        if (changed > 0) Raise();
        return changed;
    }

    // ══════════════════════ 메모리 하이퍼 관제 ══════════════════════

    /// <summary>메모리 샘플(관리 힙 · 총 할당률 · GC 세대 · 위반). 무할당.</summary>
    public MemorySample SampleMemory(double nowMs)
    {
        var alloc = GC.GetTotalAllocatedBytes(true);
        var rate = _lastAlloc < 0 || nowMs <= _lastMemMs ? 0 : (alloc - _lastAlloc) / ((nowMs - _lastMemMs) / 1000);
        _lastAlloc = alloc; _lastMemMs = nowMs;
        var m = new MemorySample(nowMs, GC.GetTotalMemory(false), rate, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), _zgc.Violations, rate > AllocAlarmBytesPerSec);
        lock (_gate) { _mem[_memHead] = m; _memHead = (_memHead + 1) % MemRing; if (_memCount < MemRing) _memCount++; }
        return m;
    }

    /// <summary>최근 메모리 샘플(최신 우선).</summary>
    public int CopyMemory(Span<MemorySample> dst)
    {
        lock (_gate)
        {
            var n = Math.Min(dst.Length, _memCount);
            for (var i = 0; i < n; i++) dst[i] = _mem[(_memHead - 1 - i + MemRing) % MemRing];
            return n;
        }
    }

    public MemorySample LastMemory { get { lock (_gate) return _memCount == 0 ? default : _mem[(_memHead - 1 + MemRing) % MemRing]; } }

    // ══════════════════════ 수익 매트릭스 ══════════════════════

    public static long ToMicrosUsd(decimal amount, Currency c) => (long)Math.Round(amount * MicrosPerUnit[(int)c], MidpointRounding.AwayFromZero);

    /// <summary>수익 기록(마이크로 USD). 무할당.</summary>
    public void Record(int platform, RevenueStream stream, long micros, long impressions, double nowMs)
    {
        if ((uint)platform >= (uint)_impr.Length || micros < 0) return;
        Interlocked.Add(ref _revenue[platform, (int)stream], micros);
        if (impressions > 0) Interlocked.Add(ref _impr[platform], impressions);
        Interlocked.Increment(ref _entries);
        var minute = (long)(nowMs / 60000);
        lock (_gate)
        {
            if (minute != _minuteEpoch)
            {
                var steps = _minuteEpoch < 0 ? MinuteRing : Math.Min(MinuteRing, minute - _minuteEpoch);
                for (var k = 0; k < steps; k++) { _minuteHead = (_minuteHead + 1) % MinuteRing; _minute[_minuteHead] = 0; }
                _minuteEpoch = minute;
            }
            _minute[_minuteHead] += micros;
        }
    }

    public void Record(int platform, RevenueStream stream, decimal amount, Currency c, long impressions, double nowMs) => Record(platform, stream, ToMicrosUsd(amount, c), impressions, nowMs);

    public long Revenue(int platform, RevenueStream s) => Interlocked.Read(ref _revenue[platform, (int)s]);

    public long PlatformRevenue(int platform) { long t = 0; for (var s = 0; s < Streams; s++) t += Interlocked.Read(ref _revenue[platform, s]); return t; }

    public long StreamRevenue(RevenueStream s) { long t = 0; for (var p = 0; p < _impr.Length; p++) t += Interlocked.Read(ref _revenue[p, (int)s]); return t; }

    public long TotalRevenue { get { long t = 0; for (var p = 0; p < _impr.Length; p++) t += PlatformRevenue(p); return t; } }

    public long Impressions(int platform) => Interlocked.Read(ref _impr[platform]);

    public long TotalImpressions { get { long t = 0; for (var p = 0; p < _impr.Length; p++) t += Interlocked.Read(ref _impr[p]); return t; } }

    /// <summary>플랫폼 광고 eCPM(USD) = 광고 수익 / 노출 × 1000.</summary>
    public double Ecpm(int platform) { var i = Impressions(platform); return i <= 0 ? 0 : Revenue(platform, RevenueStream.Ads) / 1e6 / i * 1000; }

    public double GlobalEcpm { get { var i = TotalImpressions; return i <= 0 ? 0 : StreamRevenue(RevenueStream.Ads) / 1e6 / i * 1000; } }

    /// <summary>최근 분당 수익(최신 우선, 마이크로 USD).</summary>
    public int CopyMinutes(Span<long> dst)
    {
        lock (_gate)
        {
            var n = Math.Min(dst.Length, MinuteRing);
            for (var i = 0; i < n; i++) dst[i] = _minute[(_minuteHead - i + MinuteRing) % MinuteRing];
            return n;
        }
    }

    // ══════════════════════ 정산 ══════════════════════

    /// <summary>bps 정수 분배 — 합계 보존(나머지는 1순위).</summary>
    public static long[] Split(long total, IReadOnlyList<(string Party, int Bps)> parties)
    {
        var bpsSum = 0;
        foreach (var p in parties) bpsSum += p.Bps;
        if (bpsSum != 10000) throw new InvalidOperationException("지분 합계는 10000bps여야 합니다.");
        var res = new long[parties.Count];
        long used = 0;
        for (var i = 0; i < res.Length; i++) { res[i] = total / 10000 * parties[i].Bps + total % 10000 * parties[i].Bps / 10000; used += res[i]; }
        if (res.Length > 0) res[0] += total - used;
        return res;
    }

    public static string Canonical(long seq, DateTimeOffset at, long total, long impressions, int entries, IReadOnlyList<(string Party, long Micros)> splits, string prev)
    {
        var sb = new StringBuilder("pfs-empire-settlement/v1|");
        sb.Append(seq.ToString(CultureInfo.InvariantCulture)).Append('|').Append(at.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)).Append('|')
          .Append(total.ToString(CultureInfo.InvariantCulture)).Append('|').Append(impressions.ToString(CultureInfo.InvariantCulture)).Append('|')
          .Append(entries.ToString(CultureInfo.InvariantCulture)).Append('|').Append(splits.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var (p, m) in splits) sb.Append('|').Append(p.Replace("|", "/", StringComparison.Ordinal)).Append('=').Append(m.ToString(CultureInfo.InvariantCulture));
        return sb.Append('|').Append(prev).ToString();
    }

    /// <summary>미정산 누적분 봉인 정산(HMAC 체인). 미정산 0이면 null.</summary>
    public EmpireSettlement? Settle(DateTimeOffset at)
    {
        var total = TotalRevenue;
        var pending = total - Interlocked.Read(ref _settledMicros);
        if (pending <= 0) return null;
        var parts = Split(pending, Stakeholders);
        var splits = new (string, long)[parts.Length];
        for (var i = 0; i < parts.Length; i++) splits[i] = (Stakeholders[i].Party, parts[i]);
        EmpireSettlement s;
        lock (_gate)
        {
            var seq = _ledger.Count + 1L;
            var prev = _ledger.Count > 0 ? _ledger[^1].Hash : "genesis";
            var impr = TotalImpressions; var entries = (int)Math.Min(int.MaxValue, Entries);
            var hash = Convert.ToHexString(HMACSHA256.HashData(_key(), Encoding.UTF8.GetBytes(Canonical(seq, at, pending, impr, entries, splits, prev)))).ToLowerInvariant();
            s = new EmpireSettlement(seq, at, pending, impr, entries, splits, prev, hash);
            _ledger.Add(s);
            Interlocked.Add(ref _settledMicros, pending);
        }
        LastAction = "🧾 정산 #" + s.Seq.ToString(CultureInfo.InvariantCulture) + " · " + Usd(pending);
        Raise();
        return s;
    }

    /// <summary>원장 검증(체인 · 합계 보존 · HMAC FixedTimeEquals). 반환 = 첫 위반 인덱스(-1 정상).</summary>
    public int VerifyLedger(IReadOnlyList<EmpireSettlement> ledger)
    {
        var key = _key();
        var prev = "genesis";
        for (var i = 0; i < ledger.Count; i++)
        {
            var e = ledger[i];
            if (e.PrevHash != prev || e.Seq != i + 1) return i;
            long sum = 0;
            foreach (var (_, m) in e.Splits) sum += m;
            if (sum != e.TotalMicros) return i;
            var expect = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Canonical(e.Seq, e.At, e.TotalMicros, e.Impressions, e.Entries, e.Splits, e.PrevHash)));
            byte[] got;
            try { got = Convert.FromHexString(e.Hash); } catch (FormatException) { return i; }
            if (got.Length != expect.Length || !CryptographicOperations.FixedTimeEquals(got, expect)) return i;
            prev = e.Hash;
        }
        return -1;
    }

    public static string Usd(long micros) => "$" + (micros / 1e6).ToString("#,0.00", CultureInfo.InvariantCulture);

    public static string HealthLabel(SystemHealth h) => h switch
    {
        SystemHealth.Healthy => "정상", SystemHealth.Degraded => "저하", SystemHealth.Down => "다운", SystemHealth.Healing => "복구 중", SystemHealth.Quarantined => "격리", _ => "미확인",
    };

    public static string StreamLabel(RevenueStream s) => s switch
    {
        RevenueStream.Ads => "광고", RevenueStream.Subscription => "구독", RevenueStream.Donation => "후원", RevenueStream.Commerce => "커머스", _ => "라이선스",
    };

    /// <summary>v16.0~v16.49 마일스톤 시스템 50종.</summary>
    public static SystemSlot[] MilestoneCatalog()
    {
        string[] names =
        {
            "스토리보드 코어", "장면 생성 파이프라인", "프롬프트 엔진", "이미지 생성 결선", "영상 생성 결선", "자막 엔진", "썸네일 생성기", "테마 스마트 매처", "커스텀 테마 저작", "BGM 매칭",
            "장면 편집기", "음성 합성 결선", "트랜지션·심리스 병합", "캐릭터 일관성", "동화 나레이션 TTS", "OSMU 재구성", "API 쿼터·동시성 거버너", "자동 QA", "비용·예산 차단", "협찬 제품 합성",
            "오디오 립싱크", "비주얼 노드 그래프", "얼굴 정합성", "바이럴 훅 변형", "다국어 보이스 더빙", "아바타 퍼펫티어", "인터랙티브 전환 분석", "클라우드 렌더팜", "성과 피드백 루프", "프롬프트 트랜스파일러",
            "마스터 스튜디오", "3D 가우시안 스플래팅", "시네마틱 카메라", "AI 폴리 사운드", "뉴럴 VFX 리라이팅", "세계관 스토리 에이전트", "디지털 트윈", "AI 음악", "V-Tuber 페르소나", "게임 비전 이벤트",
            "볼류메트릭 홀로그램", "시네마틱 유니버스", "엔터프라이즈 MAM", "서버사이드 광고 삽입", "AI 뉴스룸", "스포츠 멀티캠", "에듀테크 퀴즈", "유니버설 접근성", "라이브 커머스", "AI 팟캐스트·토크쇼",
        };
        var res = new SystemSlot[names.Length];
        for (var i = 0; i < names.Length; i++) res[i] = new SystemSlot { Version = "v16." + i.ToString(CultureInfo.InvariantCulture), Name = names[i], IntervalMs = 1000 };
        return res;
    }

    private void Raise() { try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Empire.OnChange", ex); } }
}
