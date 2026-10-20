using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PromptFlowStudio.Services.Production;

public enum SyncTier : byte { Local, Edge, Cloud }

public enum SyncAssetKind : byte { Storyboard, World4D, HotPatch }

/// <summary>OTA1 델타 패킷 헤더(16B · LE) — 매직 · 자산 · 블록 · 길이 · 목표 버전.</summary>
public readonly record struct OtaPacketHeader(ushort Asset, ushort Block, ushort Length, long Version)
{
    public const int Size = 16;
    public const uint Magic = 0x3141544F; // "OTA1"

    public void Write(Span<byte> dst)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(dst, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[4..], Asset);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[6..], Block);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[8..], Length);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[10..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(dst[12..], (int)Version);
    }

    /// <summary>매직 · 페이로드 길이 검증 후 파싱(무할당).</summary>
    public static bool TryRead(ReadOnlySpan<byte> src, out OtaPacketHeader h)
    {
        h = default;
        if (src.Length < Size || BinaryPrimitives.ReadUInt32LittleEndian(src) != Magic) return false;
        var len = BinaryPrimitives.ReadUInt16LittleEndian(src[8..]);
        if (len > GlobalCloudEdgeOtaSyncEngine.BlockSize || src.Length < Size + len) return false;
        h = new OtaPacketHeader(BinaryPrimitives.ReadUInt16LittleEndian(src[4..]), BinaryPrimitives.ReadUInt16LittleEndian(src[6..]), len,
            BinaryPrimitives.ReadInt32LittleEndian(src[12..]));
        return true;
    }
}

/// <summary>자산·티어 전송 상태 스냅샷(UI).</summary>
public readonly record struct SyncReplicaStatus(SyncTier Tier, long Version, bool Transferring, SyncTier From, float Progress, bool Paused);

/// <summary>
/// [PromptFlow Studio v16.66.0] 🚀 글로벌 클라우드-에지 자율 동기화 &amp; 무단절 OTA 파이프라인.
/// 3티어(로컬 · 에지 · 클라우드) × 자산(스토리보드 · 4D 월드 파라미터 · AI 핫패치) 복제본 — 각 복제본은 A/B 슬롯(활성/섀도).
/// <see cref="Tick"/>: 버전이 더 높은 최저 지연 이웃을 소스로 선택 → 256B 블록 매니페스트 비교 → 변경 블록만 OTA1 패킷 인코드/디코드로 섀도 슬롯에 적용 ·
/// 링크별 바이트 예산(대역폭 × dt) · 링크 지연 경과 후 SHA-256 검증 → 활성 슬롯 원자 교체(독자는 항상 완전한 버전만 관측 = 무단절).
/// 검증 실패 → 롤백(섀도 폐기 · 재전송) · 링크 단절 → 커서 보존 일시정지(Resume) · 핫패치 우선. 틱 무할당.
/// </summary>
public sealed class GlobalCloudEdgeOtaSyncEngine
{
    public const int Tiers = 3, MaxAssets = 16, BlockSize = 256, MaxAssetBytes = 64 * 1024, ManifestEntryBytes = 8;

    private sealed class Asset
    {
        public string Name = "";
        public SyncAssetKind Kind;
        public int Length;
        public readonly byte[][] Slots = new byte[Tiers * 2][];
        public readonly int[] Active = new int[Tiers];
        public readonly long[] Version = new long[Tiers];
        public readonly int[] From = { -1, -1, -1 };
        public readonly int[] Cursor = new int[Tiers];
        public readonly long[] Target = new long[Tiers];
        public readonly double[] ArriveMs = new double[Tiers];
        public double EditMs;
        public bool Converged = true;
    }

    private readonly object _gate = new();
    private readonly Asset[] _assets = new Asset[MaxAssets];
    private readonly double[] _latency = new double[Tiers * Tiers];
    private readonly double[] _mbps = new double[Tiers * Tiers];
    private readonly bool[] _up = new bool[Tiers * Tiers];
    private readonly double[] _budget = new double[Tiers * Tiers];
    private readonly byte[] _wire = new byte[OtaPacketHeader.Size + BlockSize];
    private int _count;
    private double _clock;
    private long _bytesSent, _fullBytes, _packets, _skipped, _syncs, _rollbacks, _restarts, _pausedTicks, _converges, _badPackets;
    private double _lastConvergeMs = double.NaN;
    private bool _corruptNext;

    public GlobalCloudEdgeOtaSyncEngine()
    {
        SetLink(SyncTier.Local, SyncTier.Edge, 6, 200);
        SetLink(SyncTier.Edge, SyncTier.Cloud, 35, 80);
        SetLink(SyncTier.Local, SyncTier.Cloud, 90, 20);
    }

    public event Action? OnChange;

    public int AssetCount { get { lock (_gate) return _count; } }
    public double ClockMs { get { lock (_gate) return _clock; } }
    public long BytesSent { get { lock (_gate) return _bytesSent; } }
    public long FullBytesEquivalent { get { lock (_gate) return _fullBytes; } }
    public long Packets { get { lock (_gate) return _packets; } }
    public long SkippedBlocks { get { lock (_gate) return _skipped; } }
    public long Syncs { get { lock (_gate) return _syncs; } }
    public long Rollbacks { get { lock (_gate) return _rollbacks; } }
    public long Restarts { get { lock (_gate) return _restarts; } }
    public long PausedTicks { get { lock (_gate) return _pausedTicks; } }
    public long Converges { get { lock (_gate) return _converges; } }
    public long BadPackets { get { lock (_gate) return _badPackets; } }
    public double LastConvergeMs { get { lock (_gate) return _lastConvergeMs; } }
    public string? LastAction { get; private set; }

    /// <summary>델타 효율(전송 바이트 / 전체 복제 바이트 · 낮을수록 좋음).</summary>
    public float DeltaRatio { get { lock (_gate) return _fullBytes > 0 ? (float)_bytesSent / _fullBytes : 0f; } }

    public void SetLink(SyncTier a, SyncTier b, double latencyMs, double mbps)
    {
        lock (_gate)
        {
            int i = (int)a * Tiers + (int)b, j = (int)b * Tiers + (int)a;
            _latency[i] = _latency[j] = Math.Max(0.1, latencyMs);
            _mbps[i] = _mbps[j] = Math.Max(0.01, mbps);
            _up[i] = _up[j] = true;
        }
    }

    public void SetLinkUp(SyncTier a, SyncTier b, bool up)
    {
        lock (_gate) _up[(int)a * Tiers + (int)b] = _up[(int)b * Tiers + (int)a] = up;
        LastAction = (up ? "🔗 링크 복구 " : "✂ 링크 단절 ") + TierLabel(a) + " ↔ " + TierLabel(b);
        Raise();
    }

    public bool IsLinkUp(SyncTier a, SyncTier b) { lock (_gate) return _up[(int)a * Tiers + (int)b]; }
    public double LatencyOf(SyncTier a, SyncTier b) { lock (_gate) return _latency[(int)a * Tiers + (int)b]; }
    public double MbpsOf(SyncTier a, SyncTier b) { lock (_gate) return _mbps[(int)a * Tiers + (int)b]; }

    /// <summary>다음 1패킷 전송 중 1바이트 변조(검증 → 롤백 경로 시험).</summary>
    public void CorruptNextPacket()
    {
        lock (_gate) _corruptNext = true;
        LastAction = "☣ 다음 OTA 패킷 변조 주입";
        Raise();
    }

    /// <summary>자산 등록 — 전 티어 동일 초기본(버전 1 · 수렴 상태).</summary>
    public int Register(string name, SyncAssetKind kind, ReadOnlySpan<byte> initial)
    {
        if (initial.Length is 0 or > MaxAssetBytes) throw new ArgumentOutOfRangeException(nameof(initial));
        lock (_gate)
        {
            if (_count >= MaxAssets) throw new InvalidOperationException("자산 상한 초과");
            var a = new Asset { Name = name, Kind = kind, Length = initial.Length };
            for (var s = 0; s < a.Slots.Length; s++) a.Slots[s] = new byte[initial.Length];
            for (var t = 0; t < Tiers; t++) { initial.CopyTo(a.Slots[t * 2]); a.Version[t] = 1; }
            _assets[_count] = a;
            return _count++;
        }
    }

    public string NameOf(int asset) { lock (_gate) return _assets[asset].Name; }
    public SyncAssetKind KindOf(int asset) { lock (_gate) return _assets[asset].Kind; }
    public int LengthOf(int asset) { lock (_gate) return _assets[asset].Length; }
    public long VersionOf(int asset, SyncTier tier) { lock (_gate) return _assets[asset].Version[(int)tier]; }
    public bool IsConverged(int asset) { lock (_gate) return _assets[asset].Converged; }

    /// <summary>활성 슬롯 복사(독자 관점 — 항상 검증된 완전한 버전).</summary>
    public int Read(int asset, SyncTier tier, Span<byte> dst)
    {
        lock (_gate)
        {
            var a = _assets[asset];
            var src = a.Slots[(int)tier * 2 + a.Active[(int)tier]].AsSpan(0, a.Length);
            var n = Math.Min(dst.Length, src.Length);
            src[..n].CopyTo(dst);
            return n;
        }
    }

    /// <summary>티어 원본 편집(부분 바이트) — 최고 버전 +1 · 해당 티어 수신 전송 취소(최신 편집 우선).</summary>
    public long Edit(int asset, SyncTier tier, int offset, ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            var a = _assets[asset];
            var t = (int)tier;
            if ((uint)offset >= (uint)a.Length) return a.Version[t];
            var n = Math.Min(data.Length, a.Length - offset);
            data[..n].CopyTo(a.Slots[t * 2 + a.Active[t]].AsSpan(offset));
            long max = 0;
            for (var i = 0; i < Tiers; i++) max = Math.Max(max, a.Version[i]);
            a.Version[t] = max + 1;
            a.From[t] = -1;
            if (a.Converged) a.EditMs = _clock;
            a.Converged = false;
            return a.Version[t];
        }
    }

    /// <summary>1틱(무할당) — 링크 예산 분배 → 우선순위(핫패치 → 4D → 스토리보드) 전송 → 지연 경과 후 검증·교체.</summary>
    public void Tick(double dtMs)
    {
        lock (_gate)
        {
            _clock += dtMs;
            // 토큰 버킷 — 저대역 링크도 패킷 1개 이상 누적 가능(상한: 2틱분 또는 2패킷)
            for (var i = 0; i < _budget.Length; i++)
            {
                var add = _mbps[i] * 125.0 * dtMs;
                _budget[i] = _up[i] ? Math.Min(_budget[i] + add, Math.Max(add * 2, 2.0 * (OtaPacketHeader.Size + BlockSize))) : 0;
            }
            for (var k = (int)SyncAssetKind.HotPatch; k >= 0; k--)
                for (var id = 0; id < _count; id++)
                    if ((int)_assets[id].Kind == k) TickAsset(id, _assets[id]);
        }
    }

    private void TickAsset(int id, Asset a)
    {
        var blocks = (a.Length + BlockSize - 1) / BlockSize;
        for (var d = 0; d < Tiers; d++)
        {
            if (a.From[d] < 0 && !StartTransfer(a, d)) continue;
            var s = a.From[d];
            var link = s * Tiers + d;
            if (!_up[link]) { _pausedTicks++; continue; }   // Resume: 커서 보존
            var src = a.Slots[s * 2 + a.Active[s]];
            var shadow = a.Slots[d * 2 + (1 - a.Active[d])];
            while (a.Cursor[d] < blocks && _budget[link] >= ManifestEntryBytes)
            {
                var off = a.Cursor[d] * BlockSize;
                var len = Math.Min(BlockSize, a.Length - off);
                if (src.AsSpan(off, len).SequenceEqual(shadow.AsSpan(off, len)))
                {
                    _budget[link] -= ManifestEntryBytes;   // 블록 해시 매니페스트 비교 비용
                    _skipped++; a.Cursor[d]++;
                    continue;
                }
                if (_budget[link] < OtaPacketHeader.Size + len) break;
                _budget[link] -= OtaPacketHeader.Size + len;
                SendBlock(id, a.Target[d], a.Cursor[d], src.AsSpan(off, len), shadow);
                a.Cursor[d]++;
                a.ArriveMs[d] = _clock + _latency[link];
            }
            if (a.Cursor[d] < blocks || _clock < a.ArriveMs[d]) continue;
            Commit(a, d, s, src, shadow);
        }
    }

    private bool StartTransfer(Asset a, int d)
    {
        var best = -1;
        for (var s = 0; s < Tiers; s++)
        {
            if (s == d || a.Version[s] <= a.Version[d] || !_up[s * Tiers + d]) continue;
            if (best < 0 || a.Version[s] > a.Version[best] || (a.Version[s] == a.Version[best] && _latency[s * Tiers + d] < _latency[best * Tiers + d])) best = s;
        }
        if (best < 0) return false;
        a.From[d] = best;
        a.Target[d] = a.Version[best];
        a.Cursor[d] = 0;
        a.ArriveMs[d] = _clock + _latency[best * Tiers + d];
        a.Slots[d * 2 + a.Active[d]].AsSpan(0, a.Length).CopyTo(a.Slots[d * 2 + 1 - a.Active[d]]);
        return true;
    }

    /// <summary>OTA1 인코드 → (선로) → 디코드 → 섀도 적용.</summary>
    private void SendBlock(int id, long version, int block, ReadOnlySpan<byte> payload, byte[] shadow)
    {
        new OtaPacketHeader((ushort)id, (ushort)block, (ushort)payload.Length, version).Write(_wire);
        payload.CopyTo(_wire.AsSpan(OtaPacketHeader.Size));
        if (_corruptNext) { _wire[OtaPacketHeader.Size] ^= 0x5A; _corruptNext = false; }
        _bytesSent += OtaPacketHeader.Size + payload.Length;
        _packets++;
        var pkt = _wire.AsSpan(0, OtaPacketHeader.Size + payload.Length);
        if (!OtaPacketHeader.TryRead(pkt, out var h)) { _badPackets++; return; }
        pkt.Slice(OtaPacketHeader.Size, h.Length).CopyTo(shadow.AsSpan(h.Block * BlockSize));
    }

    private void Commit(Asset a, int d, int s, byte[] src, byte[] shadow)
    {
        if (a.Version[s] != a.Target[d])
        {
            // 소스가 전송 중 갱신 — 섀도 유지한 채 재비교(변경 블록만 추가 전송)
            a.Target[d] = a.Version[s]; a.Cursor[d] = 0; _restarts++;
            return;
        }
        Span<byte> h1 = stackalloc byte[32];
        Span<byte> h2 = stackalloc byte[32];
        SHA256.HashData(src.AsSpan(0, a.Length), h1);
        SHA256.HashData(shadow.AsSpan(0, a.Length), h2);
        if (!CryptographicOperations.FixedTimeEquals(h1, h2))
        {
            _rollbacks++;
            a.Slots[d * 2 + a.Active[d]].AsSpan(0, a.Length).CopyTo(shadow);   // 섀도 폐기 → 재전송
            a.Cursor[d] = 0;
            return;
        }
        a.Active[d] = 1 - a.Active[d];   // 원자 교체
        a.Version[d] = a.Target[d];
        a.From[d] = -1;
        _syncs++;
        _fullBytes += a.Length;
        var v = a.Version[0];
        for (var t = 1; t < Tiers; t++) if (a.Version[t] != v) return;
        if (!a.Converged) { a.Converged = true; _converges++; _lastConvergeMs = _clock - a.EditMs; }
    }

    public SyncReplicaStatus StatusOf(int asset, SyncTier tier)
    {
        lock (_gate)
        {
            var a = _assets[asset]; var d = (int)tier;
            var from = a.From[d];
            var blocks = (a.Length + BlockSize - 1) / BlockSize;
            return new SyncReplicaStatus(tier, a.Version[d], from >= 0, from >= 0 ? (SyncTier)from : tier,
                from >= 0 ? a.Cursor[d] / (float)blocks : 1f, from >= 0 && !_up[from * Tiers + d]);
        }
    }

    public static string TierLabel(SyncTier t) => t switch { SyncTier.Local => "로컬", SyncTier.Edge => "에지", _ => "클라우드" };
    public static string KindLabel(SyncAssetKind k) => k switch { SyncAssetKind.Storyboard => "스토리보드", SyncAssetKind.World4D => "4D 월드", _ => "AI 핫패치" };

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Production.Sync.OnChange", ex); }
    }
}
