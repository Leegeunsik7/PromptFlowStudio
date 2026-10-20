using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PromptFlowStudio.Services.Telemetry;

public enum AuditResult { Allow, Deny, Error }

public sealed record AuditSpokeView(string Id, long Allow, long Deny, long Error, DateTimeOffset LastSeen);

public sealed record AuditEntryView(long Seq, string Spoke, string Action, string Resource, AuditResult Result, string Hash8, string PrevHash8, DateTimeOffset At);

public sealed record AuditChainReport(bool Valid, long Entries, long BreakAtSeq, double ElapsedMs);

/// <summary>
/// v10.0.1 엔터프라이즈 감사 텔레메트리 집계기 — 다수 분산 스포크(모바일/엣지/워커)가 제출하는 감사 이벤트를
/// 허브가 SHA-256 해시체인(각 항목이 직전 항목 해시를 포함)으로 실시간 집계·불변화하고, 스포크별 Allow/Deny/Error
/// 롤업을 무할당 카운터로 유지한다. <see cref="Integration.TelemetryAggregator"/>(RPS/GPU 수치 시계열)와 달리
/// "누가·무엇을·언제·결과"의 변조 불가 감사 원장을 다룬다. 순수 BCL(SHA-256).
/// </summary>
public sealed class EnterpriseAuditTelemetryAggregator : IDisposable
{
    public const int Capacity = 256;
    public const int TickMs = 800;

    private sealed class Spoke { public long Allow, Deny, Error; public DateTimeOffset LastSeen; }

    private readonly object _gate = new();
    private readonly Dictionary<string, Spoke> _spokes = new(StringComparer.Ordinal);
    private readonly AuditEntryView[] _ring = new AuditEntryView[Capacity];
    private readonly Random _rng = new(10001);
    private System.Threading.Timer? _timer;
    private int _head, _count;
    private long _seq;
    private byte[] _lastHash = Array.Empty<byte>();
    private long _submitted, _tamperFlags;
    private bool _disposed;

    public event Action? OnChange;

    public bool DemoRunning => _timer is not null;
    public string? LastAction { get; private set; }
    public long Submitted => Interlocked.Read(ref _submitted);
    public long TamperFlags => Interlocked.Read(ref _tamperFlags);
    public int SpokeCount { get { lock (_gate) return _spokes.Count; } }

    public IReadOnlyList<AuditSpokeView> Spokes
    {
        get
        {
            lock (_gate)
            {
                var list = new List<AuditSpokeView>(_spokes.Count);
                foreach (var (id, s) in _spokes) list.Add(new AuditSpokeView(id, s.Allow, s.Deny, s.Error, s.LastSeen));
                return list;
            }
        }
    }

    /// <summary>최신순 감사 원장 엔트리.</summary>
    public IReadOnlyList<AuditEntryView> RecentEntries
    {
        get
        {
            lock (_gate)
            {
                var list = new List<AuditEntryView>(_count);
                for (var i = 0; i < _count; i++) list.Add(_ring[(_head - 1 - i + Capacity) % Capacity]);
                return list;
            }
        }
    }

    /// <summary>스포크로부터 감사 이벤트 1건 제출 — 해시체인에 봉인하고 스포크 롤업 갱신(무할당 상태 전이).</summary>
    public AuditEntryView Submit(string spokeId, string action, string resource, AuditResult result)
    {
        AuditEntryView entry;
        lock (_gate) entry = SubmitLocked(spokeId, action, resource, result);
        OnChange?.Invoke();
        return entry;
    }

    private AuditEntryView SubmitLocked(string spokeId, string action, string resource, AuditResult result)
    {
        _seq++; _submitted++;
        if (!_spokes.TryGetValue(spokeId, out var s)) { s = new Spoke(); _spokes[spokeId] = s; }
        s.LastSeen = DateTimeOffset.UtcNow;
        switch (result) { case AuditResult.Allow: s.Allow++; break; case AuditResult.Deny: s.Deny++; break; default: s.Error++; break; }

        // v10.4.0 Zero-Alloc(CLAUDE.md 규칙6): 문자열 보간+GetBytes(string) 대신 풀링 버퍼에 UTF8 바이트 직결 기록.
        // prevHash 도 Hex8 왕복(bytes→hex문자열→utf8bytes) 없이 원본 32바이트를 그대로 체인 입력에 포함(왕복 낭비 제거).
        var prevHashDisplay = Hex8(_lastHash);   // 출력 레코드 표시용(엔트리당 1회 · 체인 입력과는 무관)
        var hash = HashChainLinkZeroAlloc(_seq, spokeId, action, resource, result, _lastHash);
        var entry = new AuditEntryView(_seq, spokeId, action, resource, result, Hex8(hash), prevHashDisplay, DateTimeOffset.UtcNow);
        _lastHash = hash;

        _ring[_head] = entry; _head = (_head + 1) % Capacity; if (_count < Capacity) _count++;
        LastAction = $"#{_seq} {spokeId} {action}→{result}";
        return entry;
    }

    /// <summary>전체 원장 순회 재해시 → 체인 연속성(prevHash 링크) 검증. 불일치 시 최초 단절 시퀀스 보고.</summary>
    // v10.4.0 Zero-Alloc 체인 입력 구성 — ArrayPool 임차 버퍼에 seq/필드/prevHash(원본 바이트)를 직접 기록 후 즉시 해시,
    // 중간 문자열(보간·Hex8 왕복) 0. resultTag 는 순수 ASCII 리터럴이라 char 길이=UTF8 바이트 수로 안전 근사.
    private static byte[] HashChainLinkZeroAlloc(long seq, string spokeId, string action, string resource, AuditResult result, byte[] prevHash)
    {
        var resultTag = ResultTag(result);
        var need = 20 + Encoding.UTF8.GetByteCount(spokeId) + Encoding.UTF8.GetByteCount(action) + Encoding.UTF8.GetByteCount(resource)
                 + resultTag.Length + prevHash.Length + 8;
        var rented = ArrayPool<byte>.Shared.Rent(need);
        try
        {
            Span<byte> span = rented;
            var pos = 0;
            Utf8Formatter.TryFormat(seq, span[pos..], out var w1); pos += w1;
            span[pos++] = (byte)'|';
            pos += Encoding.UTF8.GetBytes(spokeId, span[pos..]);
            span[pos++] = (byte)'|';
            pos += Encoding.UTF8.GetBytes(action, span[pos..]);
            span[pos++] = (byte)'|';
            pos += Encoding.UTF8.GetBytes(resource, span[pos..]);
            span[pos++] = (byte)'|';
            pos += Encoding.UTF8.GetBytes(resultTag, span[pos..]);
            span[pos++] = (byte)'|';
            prevHash.CopyTo(span[pos..]); pos += prevHash.Length;
            return SHA256.HashData(span[..pos]);
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    private static string ResultTag(AuditResult r) => r switch
    {
        AuditResult.Allow => "ALLOW",
        AuditResult.Deny => "DENY",
        _ => "ERROR",
    };

    public AuditChainReport VerifyChain()
    {
        AuditChainReport rep;
        lock (_gate) rep = VerifyChainLocked();
        OnChange?.Invoke();
        return rep;
    }

    private AuditChainReport VerifyChainLocked()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ordered = new List<AuditEntryView>(_count);
        for (var i = _count - 1; i >= 0; i--) ordered.Add(_ring[(_head - 1 - i + Capacity) % Capacity]);
        string prev = Hex8(Array.Empty<byte>());
        foreach (var e in ordered)
        {
            if (!string.Equals(e.PrevHash8, prev, StringComparison.Ordinal))
            {
                sw.Stop();
                _tamperFlags++;
                LastAction = $"체인 단절 검출 · #{e.Seq}";
                return new AuditChainReport(false, ordered.Count, e.Seq, sw.Elapsed.TotalMilliseconds);
            }
            prev = e.Hash8;
        }
        sw.Stop();
        LastAction = $"체인 검증 통과 · {ordered.Count}건";
        return new AuditChainReport(true, ordered.Count, 0, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>임의 링 슬롯의 해시를 직접 훼손(체인 불일치 유발) — 검증 데모용.</summary>
    public void InjectChainTamper()
    {
        lock (_gate)
        {
            if (_count == 0) return;
            var idx = (_head - 1 - _rng.Next(_count) + Capacity) % Capacity;
            var e = _ring[idx];
            _ring[idx] = e with { Hash8 = "deadbeef" };
            LastAction = $"#{e.Seq} 원장 해시 변조 주입";
        }
        OnChange?.Invoke();
    }

    public void StartDemo()
    {
        lock (_gate)
        {
            if (_timer is not null || _disposed) return;
            _timer = new System.Threading.Timer(_ => Tick(), null, 0, TickMs);
            LastAction = $"스포크 시뮬레이션 기동 · {TickMs}ms";
        }
        OnChange?.Invoke();
    }

    private void Tick()
    {
        lock (_gate)
        {
            if (_timer is null) return;
            var spoke = $"spoke-{_rng.Next(1, 5)}";
            var result = _rng.NextDouble() < 0.75 ? AuditResult.Allow : _rng.NextDouble() < 0.5 ? AuditResult.Deny : AuditResult.Error;
            SubmitLocked(spoke, DemoActions[_rng.Next(DemoActions.Length)], "resource://render-job", result);
        }
        OnChange?.Invoke();
    }

    private static readonly string[] DemoActions = { "render.submit", "gallery.write", "config.update", "session.login" };

    public void StopDemo()
    {
        lock (_gate) { _timer?.Dispose(); _timer = null; LastAction = "스포크 시뮬레이션 정지"; }
        OnChange?.Invoke();
    }

    public void ResetAll()
    {
        lock (_gate)
        {
            _spokes.Clear(); Array.Clear(_ring); _head = _count = 0; _seq = 0; _lastHash = Array.Empty<byte>();
            LastAction = "감사 집계기 초기화";
        }
        OnChange?.Invoke();
    }

    private static string Hex8(byte[] h)
    {
        if (h.Length == 0) return "00000000";
        var sb = new StringBuilder(8);
        for (var i = 0; i < 4 && i < h.Length; i++) sb.Append(h[i].ToString("x2"));
        return sb.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate) { _timer?.Dispose(); _timer = null; }
    }
}
