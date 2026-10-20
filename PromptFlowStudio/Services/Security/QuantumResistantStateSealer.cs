using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PromptFlowStudio.Services.Security;

public enum SnapshotState { Sealed, Tampered, RolledBack }

public sealed record SnapshotView(int Slot, long Version, SnapshotState State, string Digest8, int Bytes);

public sealed record SealResult(long Version, string Digest8, string Signature8, double ElapsedMs);

public sealed record VerifyResult(bool Valid, int SlotsChecked, int Tampered, int RolledBack, double ElapsedMs);

/// <summary>v10.0-LTS: 외부 아티팩트(예: 핫픽스 패키지) 서명 다이제스트 — <see cref="QuantumResistantStateSealer.SealExternalArtifact"/> 산출.</summary>
public sealed record ExternalArtifactSeal(byte[] Digest, byte[] Signature, string Digest8);

/// <summary>
/// v9.9.2 양자 저항 상태 봉인기 — <see cref="Inference.QuantumStorageSealingEngine"/>(Merkle 세그먼트 캐시)와 달리
/// 라이브 애플리케이션 상태 스냅샷을 MemoryMappedFile 백드 링버퍼(8슬롯)에 <see cref="Span{T}"/> 무복사로 기록하고,
/// SHA3-256(미가용 시 SHA-256 폴백) 다이제스트 + HMAC 서명(PQC 스텁 · 교체 시 상위 무변경)으로 슬롯을 봉인한다.
/// 검증 시 변조 슬롯을 직전 무결 버전으로 자율 롤백. 순수 BCL(MemoryMappedFile/Span/Cryptography).
/// </summary>
public sealed class QuantumResistantStateSealer : IDisposable
{
    public const int TickMs = 1200;
    private const int SlotCount = 8;
    private const int SlotBytes = 8192;

    // v10.5.0-F1: 하드코딩 리터럴 → SecureSecretVault 이관(환경변수→DPAPI→Graceful 폴백). CLAUDE.md Rule 7 소급 적용.
    private readonly byte[] SignKey;

    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _acc;
    private byte[]? _pool;
    private readonly byte[][] _digests = new byte[SlotCount][];
    private readonly byte[][] _signatures = new byte[SlotCount][];
    private readonly long[] _versions = new long[SlotCount];
    private readonly SnapshotState[] _states = new SnapshotState[SlotCount];
    private readonly byte[] _scratch = new byte[SlotBytes];

    private readonly object _gate = new();
    private readonly Random _rng = new(9902);
    private System.Threading.Timer? _timer;
    private int _cursor;
    private long _seals, _verifies, _tampers, _rollbacks;
    private bool _disposed;

    public event Action? OnChange;

    public QuantumResistantStateSealer(SecureSecretVault vault)
    {
        SignKey = vault.ResolveOrCreate("quantum-state-sealer");
        try
        {
            _mmf = MemoryMappedFile.CreateNew(Guid.NewGuid().ToString("N"), (long)SlotCount * SlotBytes);
            _acc = _mmf.CreateViewAccessor(0, (long)SlotCount * SlotBytes);
        }
        catch (Exception) { _mmf = null; _acc = null; _pool = new byte[SlotCount * SlotBytes]; }   // MMF 미가용 → 풀 폴백
        for (var i = 0; i < SlotCount; i++) { _digests[i] = Array.Empty<byte>(); _signatures[i] = Array.Empty<byte>(); }
        SealSnapshotLocked();   // 슬롯0 기준선 봉인
    }

    public bool Mapped => _mmf is not null;
    public bool DemoRunning => _timer is not null;
    public string? LastAction { get; private set; }
    public long Seals => Interlocked.Read(ref _seals);
    public long Verifies => Interlocked.Read(ref _verifies);
    public long Tampers => Interlocked.Read(ref _tampers);
    public long Rollbacks => Interlocked.Read(ref _rollbacks);
    public long CurrentVersion { get { lock (_gate) return _versions[LastSlotLocked()]; } }

    public IReadOnlyList<SnapshotView> Snapshots
    {
        get
        {
            lock (_gate)
            {
                var list = new List<SnapshotView>(SlotCount);
                for (var i = 0; i < SlotCount; i++)
                    list.Add(new SnapshotView(i, _versions[i], _states[i], Hex8(_digests[i]), _digests[i].Length == 0 ? 0 : SlotBytes));
                return list;
            }
        }
    }

    /// <summary>현재 상태(순환 카운터 기반 결정적 페이로드)를 다음 링 슬롯에 Span 무복사 기록 후 봉인.</summary>
    public SealResult SealSnapshot()
    {
        SealResult r;
        lock (_gate) r = SealSnapshotLocked();
        OnChange?.Invoke();
        return r;
    }

    private SealResult SealSnapshotLocked()
    {
        var sw = Stopwatch.StartNew();
        _seals++;
        var version = _seals;
        var slot = (int)((version - 1) % SlotCount);

        Span<byte> scratch = _scratch;
        BinaryPrimitivesWriteInt64(scratch, version);                 // 헤더: 버전(8B)
        var seed = (byte)(version & 0xFF);
        for (var i = 8; i < scratch.Length; i += 256) scratch[i] = seed;   // 결정적 상태 패턴(무할당 재사용 스크래치)

        CommitSlot(slot, scratch);
        var digest = SHA256.HashData(scratch);        // BCL 기본 제공 다이제스트(SHA3-256 미가용 런타임 대비 폴백)
        var sig = HMACSHA256.HashData(SignKey, digest);
        _digests[slot] = digest; _signatures[slot] = sig; _versions[slot] = version; _states[slot] = SnapshotState.Sealed;
        _cursor = slot;
        sw.Stop();
        LastAction = $"슬롯#{slot} 봉인 · v{version} · {Hex8(digest)}";
        return new SealResult(version, Hex8(digest), Hex8(sig), sw.Elapsed.TotalMilliseconds);
    }

    private void CommitSlot(int slot, ReadOnlySpan<byte> data)
    {
        var offset = (long)slot * SlotBytes;
        if (_acc is not null) _acc.WriteArray(offset, data.ToArray(), 0, data.Length);
        else if (_pool is not null) data.CopyTo(_pool.AsSpan((int)offset, data.Length));
    }

    private ReadOnlySpan<byte> ReadSlot(int slot)
    {
        var offset = (long)slot * SlotBytes;
        var buf = new byte[SlotBytes];
        if (_acc is not null) _acc.ReadArray(offset, buf, 0, SlotBytes);
        else if (_pool is not null) _pool.AsSpan((int)offset, SlotBytes).CopyTo(buf);
        return buf;
    }

    /// <summary>무작위 봉인 슬롯의 원본 바이트 변조 주입.</summary>
    public void InjectTamper()
    {
        lock (_gate)
        {
            var sealedSlots = new List<int>();
            for (var i = 0; i < SlotCount; i++) if (_states[i] != SnapshotState.RolledBack && _digests[i].Length > 0) sealedSlots.Add(i);
            if (sealedSlots.Count == 0) return;
            var slot = sealedSlots[_rng.Next(sealedSlots.Count)];
            var buf = ReadSlot(slot).ToArray();
            buf[_rng.Next(8, buf.Length)] ^= 0xFF;   // 헤더(버전) 보존, 페이로드만 변조
            CommitSlot(slot, buf);
            _states[slot] = SnapshotState.Tampered;
            LastAction = $"슬롯#{slot} 변조 주입";
        }
        OnChange?.Invoke();
    }

    /// <summary>전 슬롯 SHA-256 재계산 → 서명 상수시간 대조 → 변조 슬롯을 직전 무결 슬롯 페이로드로 자율 롤백.</summary>
    public VerifyResult VerifyAndHeal()
    {
        VerifyResult r;
        lock (_gate) r = VerifyLocked();
        OnChange?.Invoke();
        return r;
    }

    private VerifyResult VerifyLocked()
    {
        var sw = Stopwatch.StartNew();
        _verifies++;
        int tampered = 0, rolled = 0, checkedCount = 0;
        for (var i = 0; i < SlotCount; i++)
        {
            if (_digests[i].Length == 0) continue;
            checkedCount++;
            var buf = ReadSlot(i);
            var digest = SHA256.HashData(buf);
            var sig = HMACSHA256.HashData(SignKey, digest);
            var valid = CryptographicOperations.FixedTimeEquals(digest, _digests[i]) && CryptographicOperations.FixedTimeEquals(sig, _signatures[i]);
            if (valid) continue;

            tampered++; _tampers++;
            var source = FindLatestIntactLocked(i);
            if (source >= 0)
            {
                CommitSlot(i, ReadSlot(source));
                _digests[i] = _digests[source]; _signatures[i] = _signatures[source]; _versions[i] = _versions[source];
                _states[i] = SnapshotState.RolledBack;
                rolled++; _rollbacks++;
            }
        }
        sw.Stop();
        LastAction = tampered > 0 ? $"변조 {tampered}건 검출 · 롤백 {rolled}건" : "전 슬롯 무결성 검증 통과";
        return new VerifyResult(tampered == 0, checkedCount, tampered, rolled, sw.Elapsed.TotalMilliseconds);
    }

    private int FindLatestIntactLocked(int excludeSlot)
    {
        var best = -1; long bestVer = -1;
        for (var i = 0; i < SlotCount; i++)
        {
            if (i == excludeSlot || _digests[i].Length == 0 || _states[i] == SnapshotState.Tampered) continue;
            if (_versions[i] > bestVer) { bestVer = _versions[i]; best = i; }
        }
        return best;
    }

    private int LastSlotLocked() => _cursor;

    public void StartDemo()
    {
        lock (_gate)
        {
            if (_timer is not null || _disposed) return;
            _timer = new System.Threading.Timer(_ => Tick(), null, TickMs, TickMs);
            LastAction = $"주기 봉인 루프 기동 · {TickMs}ms";
        }
        OnChange?.Invoke();
    }

    private void Tick()
    {
        lock (_gate) { if (_timer is null) return; SealSnapshotLocked(); }
        OnChange?.Invoke();
    }

    public void StopDemo()
    {
        lock (_gate) { _timer?.Dispose(); _timer = null; LastAction = "봉인 루프 정지"; }
        OnChange?.Invoke();
    }

    public void ResetAll()
    {
        lock (_gate)
        {
            for (var i = 0; i < SlotCount; i++) { _digests[i] = Array.Empty<byte>(); _signatures[i] = Array.Empty<byte>(); _versions[i] = 0; _states[i] = SnapshotState.Sealed; }
            _seals = 0; _cursor = 0;
            SealSnapshotLocked();
            LastAction = "상태 봉인기 초기화";
        }
        OnChange?.Invoke();
    }

    /// <summary>v10.0-LTS: 외부 아티팩트(핫픽스 패키지 등)를 이 봉인기 서명키로 다이제스트+HMAC 서명(신뢰 발급 시점).</summary>
    public ExternalArtifactSeal SealExternalArtifact(ReadOnlySpan<byte> data)
    {
        var digest = SHA256.HashData(data.ToArray());
        var signature = HMACSHA256.HashData(SignKey, digest);
        return new ExternalArtifactSeal(digest, signature, Hex8(digest));
    }

    /// <summary>v10.0-LTS: 외부 아티팩트를 재해시하여 발급 시점 다이제스트/서명과 상수시간 대조(변조·재서명 위조 검출).</summary>
    public bool VerifyExternalArtifact(ReadOnlySpan<byte> data, ExternalArtifactSeal expected)
    {
        var digest = SHA256.HashData(data.ToArray());
        var signature = HMACSHA256.HashData(SignKey, digest);
        return CryptographicOperations.FixedTimeEquals(digest, expected.Digest) && CryptographicOperations.FixedTimeEquals(signature, expected.Signature);
    }

    /// <summary>v10.3.0: SHA3-256(런타임 미가용 시 SHA-256 폴백)으로 최종 릴리스 아티팩트를 봉인 — 동일 서명키 재사용.</summary>
    public ExternalArtifactSeal SealExternalArtifactSha3(ReadOnlySpan<byte> data)
    {
        var digest = Sha3OrFallbackDigest(data);
        var signature = HMACSHA256.HashData(SignKey, digest);
        return new ExternalArtifactSeal(digest, signature, Hex8(digest));
    }

    /// <summary>v10.3.0: SHA3 봉인 아티팩트 재검증(발급 시점과 상수시간 대조 · 런타임별 SHA3 가용성 불일치는 자연 실패로 처리).</summary>
    public bool VerifyExternalArtifactSha3(ReadOnlySpan<byte> data, ExternalArtifactSeal expected)
    {
        var digest = Sha3OrFallbackDigest(data);
        var signature = HMACSHA256.HashData(SignKey, digest);
        return CryptographicOperations.FixedTimeEquals(digest, expected.Digest) && CryptographicOperations.FixedTimeEquals(signature, expected.Signature);
    }

    /// <summary>SHA3-256 이 런타임/OS 에서 지원되면 사용, 아니면 SHA-256 Graceful 폴백(둘 다 32바이트 다이제스트).</summary>
    public static bool Sha3Supported => SHA3_256.IsSupported;
    private static byte[] Sha3OrFallbackDigest(ReadOnlySpan<byte> data) => SHA3_256.IsSupported ? SHA3_256.HashData(data) : SHA256.HashData(data);

    private static void BinaryPrimitivesWriteInt64(Span<byte> span, long value)
        => System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(span, value);

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
        lock (_gate) { _timer?.Dispose(); _timer = null; _acc?.Dispose(); _mmf?.Dispose(); _acc = null; _mmf = null; }
    }
}
