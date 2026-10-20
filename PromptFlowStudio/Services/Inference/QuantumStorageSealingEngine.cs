using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using PromptFlowStudio.Services.Security;   // SecureSecretVault(v10.5.0-F1)

namespace PromptFlowStudio.Services.Inference;

public enum SealSegmentState { Sealed, Tampered, Recovered }

public sealed record SealSegmentView(int Index, string Hash8, SealSegmentState State, int Bytes);

public sealed record SealScanReport(bool RootValid, string RootHash8, string Signature8, int Segments, int Tampered, int Recovered, long Bytes, double ElapsedMs);

/// <summary>
/// v9.7.2 양자 내성 스토리지 봉인 — 바이너리 캐시/모델 세그먼트를 SHA-256 Merkle Tree 로 봉인하고
/// 루트를 PQC 서명(HMAC-SHA256 스텁 · 교체 시 상위 무변경)으로 밀봉한다. 주기 스캔으로 세그먼트 변조를
/// 상수시간 대조 검출하고, 무결 복제본에서 오염 구획을 자율 복구 후 루트/서명을 재봉인한다.
/// 순수 BCL(System.Security.Cryptography).
/// </summary>
public sealed class QuantumStorageSealingEngine : IDisposable
{
    public const int TickMs = 1000;
    private const int SegCount = 12;
    private const int SegBytes = 4096;

    private sealed class Segment
    {
        public byte[] Data = Array.Empty<byte>();
        public byte[] Replica = Array.Empty<byte>();   // 봉인 시 저장한 무결 복제본(자율 복구 소스)
        public byte[] Hash = Array.Empty<byte>();
        public SealSegmentState State = SealSegmentState.Sealed;
    }

    // v10.5.0-F1: 하드코딩 리터럴 → SecureSecretVault 이관(환경변수→DPAPI→Graceful 폴백). CLAUDE.md Rule 7 소급 적용.
    private readonly byte[] SigningKey;

    private readonly object _gate = new();
    private readonly Segment[] _segments = new Segment[SegCount];
    private readonly Random _rng = new(9702);
    private System.Threading.Timer? _timer;
    private byte[] _root = Array.Empty<byte>();
    private byte[] _signature = Array.Empty<byte>();
    private SealScanReport? _last;
    private long _seals, _scans, _tampersDetected, _recoveries, _sigFails;
    private bool _disposed;

    public event Action? OnChange;

    public QuantumStorageSealingEngine(SecureSecretVault vault)
    {
        SigningKey = vault.ResolveOrCreate("quantum-storage-sealing-engine");
        for (var i = 0; i < _segments.Length; i++) _segments[i] = new Segment();
        SealAllLocked();
    }

    public bool DemoRunning => _timer is not null;
    public string? LastAction { get; private set; }
    public SealScanReport? LastReport => _last;
    public long Seals => Interlocked.Read(ref _seals);
    public long Scans => Interlocked.Read(ref _scans);
    public long TampersDetected => Interlocked.Read(ref _tampersDetected);
    public long Recoveries => Interlocked.Read(ref _recoveries);
    public long SignatureFailures => Interlocked.Read(ref _sigFails);
    public int SegmentCount => SegCount;

    public IReadOnlyList<SealSegmentView> Segments
    {
        get
        {
            lock (_gate)
            {
                var list = new List<SealSegmentView>(_segments.Length);
                for (var i = 0; i < _segments.Length; i++)
                {
                    var s = _segments[i];
                    list.Add(new SealSegmentView(i, Hex8(s.Hash), s.State, s.Data.Length));
                }
                return list;
            }
        }
    }

    /// <summary>전체 세그먼트를 무작위 바이트로 초기화하고 Merkle 봉인 + PQC 서명.</summary>
    public void SealAll()
    {
        lock (_gate) SealAllLocked();
        OnChange?.Invoke();
    }

    private void SealAllLocked()
    {
        foreach (var s in _segments)
        {
            s.Data = new byte[SegBytes];
            _rng.NextBytes(s.Data);
            s.Replica = (byte[])s.Data.Clone();
            s.Hash = SHA256.HashData(s.Data);
            s.State = SealSegmentState.Sealed;
        }
        RebuildRootLocked();
        _seals++;
        LastAction = $"{SegCount}세그먼트 Merkle 봉인 · 루트 {Hex8(_root)}";
    }

    /// <summary>무작위 세그먼트 1바이트 변조 주입.</summary>
    public void InjectTamper()
    {
        lock (_gate)
        {
            var i = _rng.Next(_segments.Length);
            var s = _segments[i];
            if (s.Data.Length == 0) return;
            s.Data[_rng.Next(s.Data.Length)] ^= 0xFF;
            s.State = SealSegmentState.Tampered;
            LastAction = $"세그먼트 #{i} 변조 주입";
        }
        OnChange?.Invoke();
    }

    /// <summary>무결성 스캔 → 변조 검출 → 복제본에서 자율 복구 → 재봉인.</summary>
    public SealScanReport Scan()
    {
        SealScanReport rep;
        lock (_gate) rep = ScanLocked();
        OnChange?.Invoke();
        return rep;
    }

    private SealScanReport ScanLocked()
    {
        var sw = Stopwatch.StartNew();
        _scans++;
        int tampered = 0, recovered = 0;
        long bytes = 0;
        foreach (var s in _segments)
        {
            bytes += s.Data.Length;
            var h = SHA256.HashData(s.Data);
            if (!CryptographicOperations.FixedTimeEquals(h, s.Hash))
            {
                tampered++; _tampersDetected++;
                s.Data = (byte[])s.Replica.Clone();     // 자율 세그먼트 복구
                s.State = SealSegmentState.Recovered;
                recovered++; _recoveries++;
            }
        }
        var freshRoot = ComputeRootLocked();
        var expectedSig = Sign(freshRoot);
        var rootValid = CryptographicOperations.FixedTimeEquals(freshRoot, _root) && CryptographicOperations.FixedTimeEquals(expectedSig, _signature);
        if (recovered > 0)
        {
            foreach (var s in _segments) s.Hash = SHA256.HashData(s.Data);
            RebuildRootLocked();
            rootValid = true;   // 복구 후 재봉인
        }
        if (!rootValid) _sigFails++;
        sw.Stop();
        _last = new SealScanReport(rootValid, Hex8(_root), Hex8(_signature), SegCount, tampered, recovered, bytes, sw.Elapsed.TotalMilliseconds);
        LastAction = tampered > 0 ? $"변조 {tampered}건 검출 · 복구 {recovered}건 재봉인" : "무결성 스캔 통과";
        return _last;
    }

    public void StartDemo()
    {
        lock (_gate)
        {
            if (_timer is not null || _disposed) return;
            _timer = new System.Threading.Timer(_ => ScanTick(), null, TickMs, TickMs);
            LastAction = $"주기 봉인 스캔 기동 · {TickMs}ms";
        }
        OnChange?.Invoke();
    }

    private void ScanTick()
    {
        lock (_gate) { if (_timer is null) return; ScanLocked(); }
        OnChange?.Invoke();
    }

    public void StopDemo()
    {
        lock (_gate) { _timer?.Dispose(); _timer = null; LastAction = "스캔 정지"; }
        OnChange?.Invoke();
    }

    public void ResetAll()
    {
        lock (_gate) { SealAllLocked(); _last = null; LastAction = "스토리지 봉인 초기화"; }
        OnChange?.Invoke();
    }

    private void RebuildRootLocked()
    {
        _root = ComputeRootLocked();
        _signature = Sign(_root);
    }

    private byte[] ComputeRootLocked()
    {
        var level = new List<byte[]>(_segments.Length);
        foreach (var s in _segments) level.Add(s.Hash);
        if (level.Count == 0) return Array.Empty<byte>();
        while (level.Count > 1)
        {
            var next = new List<byte[]>((level.Count + 1) / 2);
            for (var i = 0; i < level.Count; i += 2)
            {
                var left = level[i];
                var right = i + 1 < level.Count ? level[i + 1] : level[i];   // 홀수면 자기복제
                var buf = new byte[left.Length + right.Length];
                Buffer.BlockCopy(left, 0, buf, 0, left.Length);
                Buffer.BlockCopy(right, 0, buf, left.Length, right.Length);
                next.Add(SHA256.HashData(buf));
            }
            level = next;
        }
        return level[0];
    }

    private byte[] Sign(byte[] root) => root.Length == 0 ? Array.Empty<byte>() : HMACSHA256.HashData(SigningKey, root);

    private static string Hex8(byte[] h)
    {
        if (h.Length == 0) return "--------";
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
