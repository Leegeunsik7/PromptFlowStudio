using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Master;

/// <summary>v16.30.0 콘텐츠 정의 청크 1개(오프셋 · 길이 · SHA-256 hex).</summary>
public readonly record struct DeltaChunk(long Offset, int Length, string Hash);

/// <summary>v16.30.0 자산 매니페스트 — 버전(전체 해시) + 청크 목록.</summary>
public sealed record DeltaManifest(string AssetId, long Size, string FileHash, IReadOnlyList<DeltaChunk> Chunks, DateTimeOffset At);

/// <summary>v16.30.0 CDC 파라미터(최소/평균/최대 · 평균은 2의 거듭제곱).</summary>
public readonly record struct CdcParams(int Min, int Avg, int Max)
{
    public static CdcParams Video => new(256 * 1024, 1024 * 1024, 4 * 1024 * 1024);
    public static CdcParams Small => new(2 * 1024, 8 * 1024, 64 * 1024);
}

public sealed record DeltaSyncResult(bool Ok, string AssetId, int Chunks, int Transferred, int Deduplicated, long BytesTransferred, long BytesTotal, string Message)
{
    public double SavingRatio => BytesTotal > 0 ? 1 - (double)BytesTransferred / BytesTotal : 0;
}

/// <summary>v16.30.0 청크 저장소(클라우드/피어/로컬 캐시 공통 계약 · 콘텐츠 주소).</summary>
public interface IDeltaChunkStore
{
    string Name { get; }
    Task<HashSet<string>> HaveAsync(IReadOnlyCollection<string> hashes, CancellationToken ct);
    Task PutAsync(string hash, ReadOnlyMemory<byte> data, CancellationToken ct);
    Task<byte[]?> GetAsync(string hash, CancellationToken ct);
    Task PutManifestAsync(DeltaManifest manifest, CancellationToken ct);
    Task<DeltaManifest?> GetManifestAsync(string assetId, CancellationToken ct);
}

/// <summary>
/// [PromptFlowStudio] v16.30.0 ☁️ 유니버설 클라우드-네이티브 자산 델타 동기화 엔진 — 대용량 비디오/모델 자산의 변경 블록만 전송·릴레이.
///   ▸ 청킹: FastCDC 계열 Gear 롤링 해시(정규화 2단 마스크) — 파일을 ArrayPool 1MB 블록 체인(<see cref="ReadOnlySequence{T}"/> 다중 세그먼트)으로 읽고
///     <see cref="SequenceReader{T}"/> 로 경계 탐색(<see cref="FindCut"/> · 무할당) → 청크 해시는 세그먼트별 IncrementalHash 누적(복사 0).
///     삽입/삭제가 생겨도 이후 경계가 재동기화되어 변경 근처 청크만 달라짐.
///   ▸ 동기화: 매니페스트 비교 → 원격 HaveAsync 로 보유 청크 제외 → 누락분만 PutAsync(병렬 4) → 매니페스트 게시. 복원은 로컬 이전 버전 청크 재사용 + 누락분만 수신 · 해시 검증 · 임시 파일 원자 교체.
///   ▸ 저장소: <see cref="LocalFolderChunkStore"/>(NAS/피어 공유 폴더 · 2단 샤딩) · <see cref="HttpChunkStore"/>(REST · Bearer 는 제로 트러스트 쉴드 비밀 대여 · 429/5xx 백오프)
///     · <see cref="RelayAsync"/>(저장소 → 저장소 P2P 릴레이). 선택적 저장 암호화(수렴 AES-GCM: 키·nonce 를 청크 해시에서 HKDF 파생 → 중복 제거 유지).
/// </summary>
public sealed class CloudDeltaAssetSyncEngine
{
    public const int BlockSize = 1 << 20;
    public const int Parallelism = 4;

    private static readonly ulong[] Gear = BuildGear();

    private readonly ZeroTrustSecurityShieldService? _shield;
    private readonly object _gate = new();
    private long _bytesDone, _bytesTotal, _sent, _deduped;
    private int _busy;

    public CloudDeltaAssetSyncEngine(ZeroTrustSecurityShieldService? shield = null) => _shield = shield;

    public event Action? OnChange;

    public CdcParams Params { get; set; } = CdcParams.Video;
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public double Progress { get { var t = Interlocked.Read(ref _bytesTotal); return t <= 0 ? 0 : Math.Min(1, (double)Interlocked.Read(ref _bytesDone) / t); } }
    public long TotalSentChunks => Interlocked.Read(ref _sent);
    public long TotalDedupedChunks => Interlocked.Read(ref _deduped);
    public string? LastAction { get; private set; }
    public DeltaSyncResult? LastResult { get; private set; }

    private static ulong[] BuildGear()
    {
        var g = new ulong[256];
        ulong x = 0x9E3779B97F4A7C15UL;
        for (var i = 0; i < 256; i++)
        {
            x ^= x << 13; x ^= x >> 7; x ^= x << 17;
            g[i] = x;
        }
        return g;
    }

    private static (ulong Small, ulong Large) Masks(int avg)
    {
        var bits = Math.Max(4, System.Numerics.BitOperations.Log2((uint)Math.Max(16, avg)));
        return ((1UL << (bits + 1)) - 1 << (63 - bits - 1), (1UL << (bits - 1)) - 1 << (63 - bits + 1));
    }

    // ══════════════════════ 청킹(무할당 핫패스) ══════════════════════

    /// <summary>
    /// 시퀀스 시작에서 다음 청크 경계 길이 반환 — <paramref name="isFinal"/>=false 이고 최대 길이 전 경계를 못 찾으면 -1(데이터 더 필요).
    /// 정규화 청킹: avg 이전에는 엄격 마스크, 이후 느슨 마스크.
    /// </summary>
    public static int FindCut(ReadOnlySequence<byte> seq, CdcParams p, bool isFinal)
    {
        var len = seq.Length;
        if (len <= p.Min) return isFinal ? (int)len : -1;
        var (small, large) = Masks(p.Avg);
        var reader = new SequenceReader<byte>(seq);
        reader.Advance(p.Min);
        ulong h = 0;
        var limit = (int)Math.Min(len, p.Max);
        var pos = p.Min;
        while (pos < limit && reader.TryRead(out var b))
        {
            h = (h << 1) + Gear[b];
            pos++;
            if ((h & (pos < p.Avg ? small : large)) == 0) return pos;
        }
        if (pos >= p.Max) return p.Max;
        return isFinal ? (int)len : -1;
    }

    /// <summary>메모리 버퍼 청킹(테스트/소형 자산) — 경계 목록.</summary>
    public static List<DeltaChunk> ChunkBuffer(ReadOnlyMemory<byte> data, CdcParams p)
    {
        var res = new List<DeltaChunk>();
        long off = 0;
        while (off < data.Length)
        {
            var seq = new ReadOnlySequence<byte>(data[(int)off..]);
            var cut = FindCut(seq, p, true);
            res.Add(new DeltaChunk(off, cut, Convert.ToHexString(SHA256.HashData(data.Span.Slice((int)off, cut)))));
            off += cut;
        }
        return res;
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public byte[] Array = System.Array.Empty<byte>();
        public int Start, Count;
        public void Set(byte[] arr, int start, int count, long running) { Array = arr; Start = start; Count = count; Memory = arr.AsMemory(start, count); RunningIndex = running; Next = null; }
        public void Link(Segment next) => Next = next;
    }

    /// <summary>
    /// 스트림 청킹 — 1MB 풀 블록을 읽어 다중 세그먼트 시퀀스로 연결 → 경계마다 청크 콜백(해시 · 시퀀스 슬라이스). 소비가 끝난 블록은 즉시 풀 반환.
    /// </summary>
    public static async Task<List<DeltaChunk>> ChunkStreamAsync(Stream s, CdcParams p, Func<DeltaChunk, ReadOnlySequence<byte>, ValueTask>? onChunk, CancellationToken ct)
    {
        var chunks = new List<DeltaChunk>();
        var blocks = new List<(byte[] Buf, int Start, int Count)>();
        long consumedOffset = 0;
        var eof = false;
        var segs = new List<Segment>();
        try
        {
            while (true)
            {
                long avail = 0;
                foreach (var b in blocks) avail += b.Count;
                if (!eof && avail < p.Max)
                {
                    var buf = ArrayPool<byte>.Shared.Rent(BlockSize);
                    var n = 0;
                    while (n < BlockSize)
                    {
                        var r = await s.ReadAsync(buf.AsMemory(n, BlockSize - n), ct).ConfigureAwait(false);
                        if (r <= 0) { eof = true; break; }
                        n += r;
                    }
                    if (n > 0) blocks.Add((buf, 0, n)); else ArrayPool<byte>.Shared.Return(buf);
                    continue;
                }
                if (avail == 0) break;
                var seq = BuildSequence(blocks, segs);
                var cut = FindCut(seq, p, eof);
                if (cut < 0) continue;   // 데이터 더 필요(상단에서 추가 블록 읽기)
                var slice = seq.Slice(0, cut);
                string hash;
                using (var ih = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    foreach (var mem in slice) ih.AppendData(mem.Span);
                    hash = Convert.ToHexString(ih.GetHashAndReset());
                }
                var chunk = new DeltaChunk(consumedOffset, cut, hash);
                chunks.Add(chunk);
                if (onChunk is not null) await onChunk(chunk, slice).ConfigureAwait(false);
                consumedOffset += cut;
                var remaining = cut;
                while (remaining > 0)
                {
                    var (bb, st, cnt) = blocks[0];
                    if (cnt <= remaining) { remaining -= cnt; ArrayPool<byte>.Shared.Return(bb); blocks.RemoveAt(0); }
                    else { blocks[0] = (bb, st + remaining, cnt - remaining); remaining = 0; }
                }
            }
        }
        finally { foreach (var b in blocks) ArrayPool<byte>.Shared.Return(b.Buf); }
        return chunks;
    }

    private static ReadOnlySequence<byte> BuildSequence(List<(byte[] Buf, int Start, int Count)> blocks, List<Segment> pool)
    {
        while (pool.Count < blocks.Count) pool.Add(new Segment());
        long running = 0;
        for (var i = 0; i < blocks.Count; i++)
        {
            pool[i].Set(blocks[i].Buf, blocks[i].Start, blocks[i].Count, running);
            running += blocks[i].Count;
            if (i > 0) pool[i - 1].Link(pool[i]);
        }
        var first = pool[0]; var last = pool[blocks.Count - 1];
        return new ReadOnlySequence<byte>(first, 0, last, last.Count);
    }

    /// <summary>파일 → 매니페스트(전체 SHA-256 동시 계산).</summary>
    public async Task<DeltaManifest> BuildManifestAsync(string path, string assetId, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, true);
        using var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var chunks = await ChunkStreamAsync(fs, Params, (c, data) => { foreach (var m in data) whole.AppendData(m.Span); return ValueTask.CompletedTask; }, ct).ConfigureAwait(false);
        return new DeltaManifest(assetId, fs.Length, Convert.ToHexString(whole.GetHashAndReset()), chunks, DateTimeOffset.UtcNow);
    }

    /// <summary>두 매니페스트 차이 — 새 버전에 필요하지만 이전 버전에 없는 청크 해시.</summary>
    public static HashSet<string> Diff(DeltaManifest? previous, DeltaManifest current)
    {
        var have = new HashSet<string>(StringComparer.Ordinal);
        if (previous is not null) foreach (var c in previous.Chunks) have.Add(c.Hash);
        var need = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in current.Chunks) if (!have.Contains(c.Hash)) need.Add(c.Hash);
        return need;
    }

    public static bool IsSafeAssetId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 96) return false;
        foreach (var ch in id) if (!(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.')) return false;
        return id[0] != '.';
    }

    // ══════════════════════ 업로드 / 복원 / 릴레이 ══════════════════════

    /// <summary>
    /// 업로드 — 매니페스트 생성 → 원격 보유 조회 → 누락 청크만 전송(파일 2차 패스 · 병렬 4) → 매니페스트 게시. 쉴드 인가(sync:write).
    /// </summary>
    public async Task<DeltaSyncResult> UploadAsync(string path, string assetId, IDeltaChunkStore store, string? sessionToken = null, CancellationToken ct = default)
    {
        if (!IsSafeAssetId(assetId)) return Fail(assetId, "자산 ID 형식 오류");
        if (_shield is not null && !_shield.Authorize(sessionToken, "sync:" + assetId, "write").Allowed) return Fail(assetId, "제로 트러스트 인가 거부");
        if (Interlocked.Exchange(ref _busy, 1) == 1) return Fail(assetId, "다른 동기화 진행 중");
        try
        {
            LastAction = "🧮 청킹 · " + assetId;
            Raise();
            var manifest = await BuildManifestAsync(path, assetId, ct).ConfigureAwait(false);
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in manifest.Chunks) unique.Add(c.Hash);
            var have = await store.HaveAsync(unique, ct).ConfigureAwait(false);
            var need = new HashSet<string>(StringComparer.Ordinal);
            long needBytes = 0;
            foreach (var c in manifest.Chunks) if (!have.Contains(c.Hash) && need.Add(c.Hash)) needBytes += c.Length;
            Interlocked.Exchange(ref _bytesTotal, Math.Max(1, needBytes));
            Interlocked.Exchange(ref _bytesDone, 0);
            LastAction = "⬆ 델타 전송 · " + need.Count.ToString(CultureInfo.InvariantCulture) + "/" + unique.Count.ToString(CultureInfo.InvariantCulture) + " 청크";
            Raise();
            long sentBytes = 0; var sent = 0;
            if (need.Count > 0)
            {
                using var gate = new SemaphoreSlim(Parallelism);
                var tasks = new List<Task>();
                await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, true);
                var queued = new HashSet<string>(StringComparer.Ordinal);
                foreach (var c in manifest.Chunks)
                {
                    if (!need.Contains(c.Hash) || !queued.Add(c.Hash)) continue;
                    var buf = new byte[c.Length];
                    fs.Position = c.Offset;
                    var n = 0;
                    while (n < c.Length) { var r = await fs.ReadAsync(buf.AsMemory(n), ct).ConfigureAwait(false); if (r <= 0) break; n += r; }
                    if (n != c.Length) return Fail(assetId, "파일이 청킹 중 변경되었습니다.");
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    var chunk = c;
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await store.PutAsync(chunk.Hash, buf, ct).ConfigureAwait(false);
                            Interlocked.Add(ref _bytesDone, chunk.Length);
                            Interlocked.Add(ref sentBytes, chunk.Length);
                            Interlocked.Increment(ref sent);
                            Raise();
                        }
                        finally { gate.Release(); }
                    }, ct));
                }
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            await store.PutManifestAsync(manifest, ct).ConfigureAwait(false);
            Interlocked.Add(ref _sent, sent);
            Interlocked.Add(ref _deduped, manifest.Chunks.Count - sent);
            var res = new DeltaSyncResult(true, assetId, manifest.Chunks.Count, sent, manifest.Chunks.Count - sent, sentBytes, manifest.Size,
                "업로드 완료 · 전송 " + FormatBytes(sentBytes) + " / " + FormatBytes(manifest.Size));
            LastResult = res;
            LastAction = "✅ " + res.Message;
            return res;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Fail(assetId, "취소됨"); }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException) { Program.Log("DeltaSync.Upload", ex); return Fail(assetId, "업로드 실패: " + ex.Message); }
        finally { Volatile.Write(ref _busy, 0); Raise(); }
    }

    /// <summary>
    /// 복원 — 원격 매니페스트 → 로컬 기존 파일(있으면) 청킹해 재사용 맵 → 누락 청크만 수신(해시 검증) → 순서대로 임시 파일 작성 → 전체 해시 확인 → 원자 교체.
    /// </summary>
    public async Task<DeltaSyncResult> DownloadAsync(string assetId, IDeltaChunkStore store, string destination, string? sessionToken = null, CancellationToken ct = default)
    {
        if (!IsSafeAssetId(assetId)) return Fail(assetId, "자산 ID 형식 오류");
        if (_shield is not null && !_shield.Authorize(sessionToken, "sync:" + assetId, "read").Allowed) return Fail(assetId, "제로 트러스트 인가 거부");
        if (Interlocked.Exchange(ref _busy, 1) == 1) return Fail(assetId, "다른 동기화 진행 중");
        var tmp = destination + ".delta.tmp";
        try
        {
            var m = await store.GetManifestAsync(assetId, ct).ConfigureAwait(false);
            if (m is null) return Fail(assetId, "원격 매니페스트 없음");
            var local = new Dictionary<string, (long Off, int Len)>(StringComparer.Ordinal);
            if (File.Exists(destination))
            {
                await using var lf = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read, 1, true);
                foreach (var c in await ChunkStreamAsync(lf, Params, null, ct).ConfigureAwait(false)) local.TryAdd(c.Hash, (c.Offset, c.Length));
            }
            long need = 0;
            foreach (var c in m.Chunks) if (!local.ContainsKey(c.Hash)) need += c.Length;
            Interlocked.Exchange(ref _bytesTotal, Math.Max(1, need));
            Interlocked.Exchange(ref _bytesDone, 0);
            long recvBytes = 0; var recv = 0;
            var fetched = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            using var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var outFs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            await using (var src = File.Exists(destination) ? new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true) : null)
            {
                foreach (var c in m.Chunks)
                {
                    byte[] data;
                    if (local.TryGetValue(c.Hash, out var loc) && src is not null)
                    {
                        data = ArrayPool<byte>.Shared.Rent(loc.Len);
                        try
                        {
                            src.Position = loc.Off;
                            var n = 0;
                            while (n < loc.Len) { var r = await src.ReadAsync(data.AsMemory(n, loc.Len - n), ct).ConfigureAwait(false); if (r <= 0) break; n += r; }
                            whole.AppendData(data, 0, loc.Len);
                            await outFs.WriteAsync(data.AsMemory(0, loc.Len), ct).ConfigureAwait(false);
                        }
                        finally { ArrayPool<byte>.Shared.Return(data); }
                        continue;
                    }
                    if (!fetched.TryGetValue(c.Hash, out var got))
                    {
                        got = await store.GetAsync(c.Hash, ct).ConfigureAwait(false) ?? throw new IOException("청크 누락: " + c.Hash[..12]);
                        if (got.Length != c.Length || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(got), Convert.FromHexString(c.Hash))) throw new IOException("청크 무결성 실패: " + c.Hash[..12]);
                        fetched[c.Hash] = got;
                        recv++; recvBytes += got.Length;
                        Interlocked.Add(ref _bytesDone, got.Length);
                        Raise();
                    }
                    whole.AppendData(got);
                    await outFs.WriteAsync(got, ct).ConfigureAwait(false);
                }
            }
            if (!string.Equals(Convert.ToHexString(whole.GetHashAndReset()), m.FileHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("전체 해시 불일치");
            File.Move(tmp, destination, true);
            var res = new DeltaSyncResult(true, assetId, m.Chunks.Count, recv, m.Chunks.Count - recv, recvBytes, m.Size, "복원 완료 · 수신 " + FormatBytes(recvBytes) + " / " + FormatBytes(m.Size));
            LastResult = res;
            LastAction = "✅ " + res.Message;
            return res;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { TryDelete(tmp); return Fail(assetId, "취소됨"); }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException or FormatException) { TryDelete(tmp); Program.Log("DeltaSync.Download", ex); return Fail(assetId, "복원 실패: " + ex.Message); }
        finally { Volatile.Write(ref _busy, 0); Raise(); }
    }

    /// <summary>P2P/클라우드 릴레이 — 원본 저장소 매니페스트 기준으로 대상에 없는 청크만 복사 후 매니페스트 게시.</summary>
    public async Task<DeltaSyncResult> RelayAsync(string assetId, IDeltaChunkStore from, IDeltaChunkStore to, CancellationToken ct = default)
    {
        var m = await from.GetManifestAsync(assetId, ct).ConfigureAwait(false);
        if (m is null) return Fail(assetId, "원본 매니페스트 없음");
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in m.Chunks) unique.Add(c.Hash);
        var have = await to.HaveAsync(unique, ct).ConfigureAwait(false);
        long bytes = 0; var moved = 0;
        foreach (var h in unique)
        {
            if (have.Contains(h)) continue;
            var data = await from.GetAsync(h, ct).ConfigureAwait(false);
            if (data is null || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(data), Convert.FromHexString(h))) return Fail(assetId, "릴레이 청크 무결성 실패");
            await to.PutAsync(h, data, ct).ConfigureAwait(false);
            moved++; bytes += data.Length;
        }
        await to.PutManifestAsync(m, ct).ConfigureAwait(false);
        var res = new DeltaSyncResult(true, assetId, m.Chunks.Count, moved, unique.Count - moved, bytes, m.Size, "릴레이 " + from.Name + " → " + to.Name + " · " + FormatBytes(bytes));
        LastResult = res;
        LastAction = "🔁 " + res.Message;
        Raise();
        return res;
    }

    private DeltaSyncResult Fail(string id, string msg)
    {
        var r = new DeltaSyncResult(false, id, 0, 0, 0, 0, 0, msg);
        LastResult = r;
        LastAction = "⚠ " + msg;
        Raise();
        return r;
    }

    public static string FormatBytes(long b) => b >= 1L << 30 ? (b / (double)(1L << 30)).ToString("0.00", CultureInfo.InvariantCulture) + " GB"
        : b >= 1L << 20 ? (b / (double)(1L << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
        : b >= 1024 ? (b / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB" : b.ToString(CultureInfo.InvariantCulture) + " B";

    private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    // ══════════════════════ 매니페스트 직렬화 · 수렴 암호화 ══════════════════════

    public static string SerializeManifest(DeltaManifest m)
    {
        using var ms = new MemoryStream(256 + m.Chunks.Count * 96);
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("asset", m.AssetId); w.WriteNumber("size", m.Size); w.WriteString("hash", m.FileHash);
            w.WriteString("at", m.At.ToString("O", CultureInfo.InvariantCulture));
            w.WriteStartArray("chunks");
            foreach (var c in m.Chunks) { w.WriteStartArray(); w.WriteNumberValue(c.Offset); w.WriteNumberValue(c.Length); w.WriteStringValue(c.Hash); w.WriteEndArray(); }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    /// <summary>매니페스트 파싱 + 검증(오프셋 연속 · 길이 합 = size · 해시 형식).</summary>
    public static DeltaManifest? ParseManifest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var asset = r.GetProperty("asset").GetString();
            var size = r.GetProperty("size").GetInt64();
            var hash = r.GetProperty("hash").GetString();
            if (!IsSafeAssetId(asset) || hash is null || hash.Length != 64 || size < 0) return null;
            var list = new List<DeltaChunk>();
            long expect = 0;
            foreach (var c in r.GetProperty("chunks").EnumerateArray())
            {
                var off = c[0].GetInt64(); var len = c[1].GetInt32(); var h = c[2].GetString();
                if (off != expect || len <= 0 || h is null || h.Length != 64) return null;
                list.Add(new DeltaChunk(off, len, h));
                expect += len;
            }
            if (expect != size) return null;
            return new DeltaManifest(asset!, size, hash, list, DateTimeOffset.TryParse(r.GetProperty("at").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException or FormatException) { return null; }
    }

    /// <summary>수렴 암호화 — key/nonce = HKDF(테넌트 키, 청크 해시) → 같은 평문 = 같은 암호문(중복 제거 유지).</summary>
    public static byte[] ConvergentEncrypt(ReadOnlySpan<byte> tenantKey, string hash, ReadOnlySpan<byte> plain)
    {
        Span<byte> km = stackalloc byte[44];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, tenantKey, km, Encoding.ASCII.GetBytes(hash), "pfs-delta-ce-v1"u8);
        var o = new byte[16 + plain.Length];
        using (var g = new AesGcmCompat(km[..32], 16)) g.Encrypt(km.Slice(32, 12), plain, o.AsSpan(16), o.AsSpan(0, 16));
        CryptographicOperations.ZeroMemory(km);
        return o;
    }

    public static byte[]? ConvergentDecrypt(ReadOnlySpan<byte> tenantKey, string hash, ReadOnlySpan<byte> blob)
    {
        if (blob.Length < 16) return null;
        Span<byte> km = stackalloc byte[44];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, tenantKey, km, Encoding.ASCII.GetBytes(hash), "pfs-delta-ce-v1"u8);
        var p = new byte[blob.Length - 16];
        try { using var g = new AesGcmCompat(km[..32], 16); g.Decrypt(km.Slice(32, 12), blob[16..], blob[..16], p); return p; }
        catch (CryptographicException) { return null; }
        finally { CryptographicOperations.ZeroMemory(km); }
    }

    private void Raise()
    {
        var h = OnChange;
        if (h is null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action)d)(); } catch (Exception ex) { Program.Log("DeltaSync.OnChange", ex); }
        }
    }
}

/// <summary>로컬/NAS/피어 공유 폴더 저장소 — chunks/ab/cd/{hash} · manifests/{asset}.json · 선택적 수렴 암호화.</summary>
public sealed class LocalFolderChunkStore : IDeltaChunkStore
{
    private readonly string _root;
    private readonly byte[]? _key;

    public LocalFolderChunkStore(string root, string? name = null, byte[]? encryptionKey = null)
    {
        _root = root;
        _key = encryptionKey;
        Name = name ?? Path.GetFileName(root.TrimEnd('\\', '/'));
    }

    public string Name { get; }

    private string ChunkPath(string h) => Path.Combine(_root, "chunks", h[..2], h.Substring(2, 2), h);

    private static bool IsHash(string h)
    {
        if (h.Length != 64) return false;
        foreach (var ch in h) if (!char.IsAsciiHexDigit(ch)) return false;
        return true;
    }

    public Task<HashSet<string>> HaveAsync(IReadOnlyCollection<string> hashes, CancellationToken ct)
    {
        var res = new HashSet<string>(StringComparer.Ordinal);
        foreach (var h in hashes) if (IsHash(h) && File.Exists(ChunkPath(h))) res.Add(h);
        return Task.FromResult(res);
    }

    public async Task PutAsync(string hash, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (!IsHash(hash)) throw new IOException("잘못된 해시");
        var p = ChunkPath(hash);
        if (File.Exists(p)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var payload = _key is null ? data.ToArray() : CloudDeltaAssetSyncEngine.ConvergentEncrypt(_key, hash, data.Span);
        var tmp = p + "." + Guid.NewGuid().ToString("N")[..6] + ".tmp";
        await File.WriteAllBytesAsync(tmp, payload, ct).ConfigureAwait(false);
        try { File.Move(tmp, p, false); } catch (IOException) { File.Delete(tmp); }   // 동시 업로드 경합 = 이미 존재
    }

    public async Task<byte[]?> GetAsync(string hash, CancellationToken ct)
    {
        if (!IsHash(hash)) return null;
        var p = ChunkPath(hash);
        if (!File.Exists(p)) return null;
        var raw = await File.ReadAllBytesAsync(p, ct).ConfigureAwait(false);
        return _key is null ? raw : CloudDeltaAssetSyncEngine.ConvergentDecrypt(_key, hash, raw);
    }

    public async Task PutManifestAsync(DeltaManifest manifest, CancellationToken ct)
    {
        var dir = Path.Combine(_root, "manifests");
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, manifest.AssetId + ".json.tmp");
        await File.WriteAllTextAsync(tmp, CloudDeltaAssetSyncEngine.SerializeManifest(manifest), ct).ConfigureAwait(false);
        File.Move(tmp, Path.Combine(dir, manifest.AssetId + ".json"), true);
    }

    public async Task<DeltaManifest?> GetManifestAsync(string assetId, CancellationToken ct)
    {
        if (!CloudDeltaAssetSyncEngine.IsSafeAssetId(assetId)) return null;
        var p = Path.Combine(_root, "manifests", assetId + ".json");
        return File.Exists(p) ? CloudDeltaAssetSyncEngine.ParseManifest(await File.ReadAllTextAsync(p, ct).ConfigureAwait(false)) : null;
    }
}

/// <summary>
/// HTTP 청크 저장소 — POST {base}/chunks/have(JSON 배열) · PUT/GET {base}/chunks/{hash} · PUT/GET {base}/manifests/{asset}.
/// https(또는 loopback http)만 · Bearer 토큰 공급자(쉴드 비밀 대여) · 429/5xx 지수 백오프 · 응답 해시는 엔진이 검증.
/// </summary>
public sealed class HttpChunkStore : IDeltaChunkStore
{
    private readonly HttpClient _http;
    private readonly string _base;
    private readonly Func<string?> _token;

    public HttpChunkStore(string baseUrl, Func<string?> token, HttpMessageHandler? handler = null)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) || !(u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback)))
            throw new ArgumentException("https(또는 로컬 http) 주소만 허용", nameof(baseUrl));
        _base = baseUrl.TrimEnd('/');
        _token = token;
        _http = handler is null ? new HttpClient { Timeout = TimeSpan.FromSeconds(60) } : new HttpClient(handler, false) { Timeout = TimeSpan.FromSeconds(60) };
        Name = u.Host;
    }

    public string Name { get; }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var req = build();
            var t = _token();
            if (!string.IsNullOrEmpty(t)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t);
            var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            req.Dispose();
            if ((int)resp.StatusCode is 429 or >= 500 && attempt < 3) { resp.Dispose(); await Task.Delay(TimeSpan.FromMilliseconds(250 << attempt), ct).ConfigureAwait(false); continue; }
            return resp;
        }
    }

    public async Task<HashSet<string>> HaveAsync(IReadOnlyCollection<string> hashes, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) { w.WriteStartArray(); foreach (var h in hashes) w.WriteStringValue(h); w.WriteEndArray(); }
        var body = ms.ToArray();
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, _base + "/chunks/have") { Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } } }, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var res = new HashSet<string>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new HttpRequestException("have 응답 스키마 불일치");
        foreach (var e in doc.RootElement.EnumerateArray()) if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: 64 } h) res.Add(h);
        return res;
    }

    public async Task PutAsync(string hash, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var arr = data.ToArray();
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Put, _base + "/chunks/" + hash) { Content = new ByteArrayContent(arr) }, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<byte[]?> GetAsync(string hash, CancellationToken ct)
    {
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, _base + "/chunks/" + hash), ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    public async Task PutManifestAsync(DeltaManifest manifest, CancellationToken ct)
    {
        var json = CloudDeltaAssetSyncEngine.SerializeManifest(manifest);
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Put, _base + "/manifests/" + manifest.AssetId) { Content = new StringContent(json, Encoding.UTF8, "application/json") }, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<DeltaManifest?> GetManifestAsync(string assetId, CancellationToken ct)
    {
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, _base + "/manifests/" + assetId), ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return CloudDeltaAssetSyncEngine.ParseManifest(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }
}
