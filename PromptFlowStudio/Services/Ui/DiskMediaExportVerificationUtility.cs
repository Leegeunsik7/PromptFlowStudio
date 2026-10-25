using System.Buffers.Binary;

namespace PromptFlowStudio.Services.Ui;

public enum DiskExportStatus : byte { None, Verified, NoOutput, WriteFailed, Mp4Invalid, PngInvalid, RoundTripMismatch, Cancelled }

/// <summary>디스크 fMP4 최상위 박스 스트리밍 판정 — 헤더만 시크 판독(전체 적재 없음).</summary>
public struct Mp4DiskReport
{
    public long FileBytes, BoxBytes, MoovOffset, FirstMoofOffset, FirstMdatOffset;
    public int TopBoxes, Moov, Moof, Mdat, MoofMdatPairs;
    public uint MajorBrand;
    public bool FtypFirst, Contiguous, MoovBeforeMoof;
    public readonly bool Valid => FtypFirst && Contiguous && MoovBeforeMoof && Moov == 1 && Moof > 0 && Mdat >= Moof && MoofMdatPairs == Moof && BoxBytes == FileBytes;
}

/// <summary>디스크 PNG 청크 스트리밍 판정 — 매직 · IHDR 선두 · 전 청크 CRC · IEND 말단.</summary>
public struct PngDiskReport
{
    public long FileBytes;
    public int Chunks, Idat, Width, Height;
    public bool Signature, IhdrFirst, CrcOk, IendLast;
    public readonly bool Valid => Signature && IhdrFirst && CrcOk && IendLast && Idat > 0 && Width > 0 && Height > 0;
}

/// <summary>
/// [PromptFlow Studio v18.0.8] 💾 실 디스크 익스포트 검증기 — <see cref="ShortsFactoryOneClickRenderOrchestrator"/> 산출 fMP4 · PNG 썸네일을
/// <see cref="Path.GetTempPath"/> 하위 전용 디렉터리에 비동기 <see cref="FileStream"/> 로 기록 → 다시 스트림으로 판독해 바이너리 정합성을 검증한다.
///   ▸ MP4: 첫 8 B ftyp 시그니처 · moov/moof/mdat 연속 오프셋(largesize · size=0 EOF 확장 지원) · Σ박스 = 파일 크기 · moof→mdat 쌍.
///   ▸ PNG: 매직 0x89504E470D0A1A0A · IHDR 선두 · 전 청크 CRC-32 스트리밍 · IEND(길이 0) 말단 = 파일 끝.
///   ▸ 왕복: 디스크 판독 바이트 = 메모리 원본(청크 비교). 판독 버퍼는 인스턴스 1 회 할당 재사용.
///   ▸ <see cref="Dispose"/> = 생성 파일 · 디렉터리 전량 삭제(Zero-Disk-Leak) · 삭제 실패는 격리 후 <see cref="LeakedFiles"/> 집계.
/// </summary>
public sealed class DiskMediaExportVerificationUtility : IDisposable, IAsyncDisposable
{
    public const int ChunkBytes = 64 * 1024;
    private static readonly uint[] CrcTable = BuildCrcTable();
    private readonly byte[] _buf = new byte[ChunkBytes];
    private readonly byte[] _hdr = new byte[16];
    private int _disposed;

    public string Directory { get; }
    public string VideoPath { get; }
    public string ThumbnailPath { get; }
    public DiskExportStatus Status { get; private set; }
    public Mp4DiskReport Mp4 { get; private set; }
    public PngDiskReport Png { get; private set; }
    public long BytesWritten { get; private set; }
    public int FilesWritten { get; private set; }
    public int FilesDeleted { get; private set; }
    public int LeakedFiles { get; private set; }
    public long Exports { get; private set; }
    public string? LastError { get; private set; }

    public DiskMediaExportVerificationUtility(string? root = null)
    {
        Directory = Path.Combine(root ?? Path.GetTempPath(), "pfs_export_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        VideoPath = Path.Combine(Directory, "short_9x16.mp4");
        ThumbnailPath = Path.Combine(Directory, "thumb_9x16.png");
    }

    public Task<DiskExportStatus> ExportAndVerifyAsync(ShortsFactoryOneClickRenderOrchestrator o, CancellationToken ct = default)
        => ExportAndVerifyAsync(o.Output, o.ThumbnailPng, ct);

    public async Task<DiskExportStatus> ExportAndVerifyAsync(ReadOnlyMemory<byte> mp4, ReadOnlyMemory<byte> png, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        LastError = null;
        if (mp4.IsEmpty || png.IsEmpty) return Status = DiskExportStatus.NoOutput;
        try
        {
            await WriteAsync(VideoPath, mp4, ct).ConfigureAwait(false);
            await WriteAsync(ThumbnailPath, png, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return Status = DiskExportStatus.Cancelled; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LastError = ex.Message; return Status = DiskExportStatus.WriteFailed; }

        try
        {
            using (var fs = OpenRead(VideoPath))
            {
                var ok = VerifyMp4(fs, out var m); Mp4 = m;
                if (!ok) return Status = DiskExportStatus.Mp4Invalid;
                fs.Position = 0;
                if (!SameBytes(fs, mp4.Span)) return Status = DiskExportStatus.RoundTripMismatch;
            }
            using (var fs = OpenRead(ThumbnailPath))
            {
                var ok = VerifyPng(fs, out var p); Png = p;
                if (!ok) return Status = DiskExportStatus.PngInvalid;
                fs.Position = 0;
                if (!SameBytes(fs, png.Span)) return Status = DiskExportStatus.RoundTripMismatch;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LastError = ex.Message; return Status = DiskExportStatus.WriteFailed; }
        Exports++;
        return Status = DiskExportStatus.Verified;
    }

    private async Task WriteAsync(string path, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var fresh = !File.Exists(path);
        await using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, ChunkBytes, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await fs.WriteAsync(data, ct).ConfigureAwait(false);
            await fs.FlushAsync(ct).ConfigureAwait(false);
        }
        BytesWritten += data.Length;
        if (fresh) FilesWritten++;
    }

    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);

    /// <summary>ISO-BMFF 최상위 박스 체인 판독 — 박스 헤더(8 B · largesize 16 B)만 읽고 본문은 시크.</summary>
    public bool VerifyMp4(Stream s, out Mp4DiskReport r)
    {
        r = default;
        r.FileBytes = s.Length;
        if (r.FileBytes < 8) return false;
        long p = 0; uint prev = 0; r.Contiguous = true;
        while (p + 8 <= r.FileBytes)
        {
            s.Position = p;
            s.ReadExactly(_hdr.AsSpan(0, 8));
            long size = BinaryPrimitives.ReadUInt32BigEndian(_hdr);
            var type = BinaryPrimitives.ReadUInt32BigEndian(_hdr.AsSpan(4));
            if (size == 1)
            {
                if (p + 16 > r.FileBytes) { r.Contiguous = false; break; }
                s.ReadExactly(_hdr.AsSpan(8, 8));
                size = BinaryPrimitives.ReadInt64BigEndian(_hdr.AsSpan(8));
                if (size < 16) { r.Contiguous = false; break; }
            }
            else if (size == 0) size = r.FileBytes - p;
            if (size < 8 || p + size > r.FileBytes) { r.Contiguous = false; break; }

            r.TopBoxes++;
            if (type == Ftyp)
            {
                r.FtypFirst = p == 0 && r.TopBoxes == 1;
                if (size >= 12) { s.ReadExactly(_hdr.AsSpan(0, 4)); r.MajorBrand = BinaryPrimitives.ReadUInt32BigEndian(_hdr); }
            }
            else if (type == Moov) { if (r.Moov++ == 0) r.MoovOffset = p; }
            else if (type == Moof) { if (r.Moof++ == 0) { r.FirstMoofOffset = p; r.MoovBeforeMoof = r.Moov > 0; } }
            else if (type == Mdat)
            {
                if (r.Mdat++ == 0) r.FirstMdatOffset = p;
                if (prev == Moof) r.MoofMdatPairs++;
            }
            prev = type;
            p += size;
        }
        r.BoxBytes = p;
        if (p != r.FileBytes) r.Contiguous = false;
        return r.Valid;
    }

    /// <summary>PNG 청크 체인 판독 — 청크 본문을 <see cref="ChunkBytes"/> 단위로 흘려 CRC-32 누적.</summary>
    public bool VerifyPng(Stream s, out PngDiskReport r)
    {
        r = default;
        r.FileBytes = s.Length;
        if (r.FileBytes < 8 + 25 + 12) return false;
        s.Position = 0;
        s.ReadExactly(_hdr.AsSpan(0, 8));
        r.Signature = BinaryPrimitives.ReadUInt64BigEndian(_hdr) == PngMagic;
        if (!r.Signature) return false;
        long p = 8; r.CrcOk = true;
        while (p + 12 <= r.FileBytes)
        {
            s.ReadExactly(_hdr.AsSpan(0, 8));
            var len = BinaryPrimitives.ReadInt32BigEndian(_hdr);
            var type = BinaryPrimitives.ReadUInt32BigEndian(_hdr.AsSpan(4));
            if (len < 0 || p + 12 + len > r.FileBytes) { r.CrcOk = false; return false; }
            var crc = Update(0xFFFFFFFFu, _hdr.AsSpan(4, 4));
            var first = r.Chunks++ == 0;
            for (var left = len; left > 0;)
            {
                var n = Math.Min(left, _buf.Length);
                s.ReadExactly(_buf.AsSpan(0, n));
                if (first && type == Ihdr && n >= 8) { r.Width = BinaryPrimitives.ReadInt32BigEndian(_buf); r.Height = BinaryPrimitives.ReadInt32BigEndian(_buf.AsSpan(4)); }
                crc = Update(crc, _buf.AsSpan(0, n));
                left -= n;
            }
            s.ReadExactly(_hdr.AsSpan(8, 4));
            if (BinaryPrimitives.ReadUInt32BigEndian(_hdr.AsSpan(8)) != ~crc) r.CrcOk = false;
            if (first) r.IhdrFirst = type == Ihdr && len == 13;
            else if (type == Idat) r.Idat++;
            p += 12 + len;
            if (type == Iend) { r.IendLast = len == 0 && p == r.FileBytes; break; }
        }
        return r.Valid;
    }

    private bool SameBytes(Stream s, ReadOnlySpan<byte> src)
    {
        if (s.Length != src.Length) return false;
        for (var off = 0; off < src.Length;)
        {
            var n = Math.Min(src.Length - off, _buf.Length);
            s.ReadExactly(_buf.AsSpan(0, n));
            if (!_buf.AsSpan(0, n).SequenceEqual(src.Slice(off, n))) return false;
            off += n;
        }
        return true;
    }

    /// <summary>생성 파일 · 전용 디렉터리 삭제 — 실패는 격리하고 잔존 파일 수만 기록.</summary>
    public void Cleanup()
    {
        var leaked = 0;
        leaked += Delete(VideoPath);
        leaked += Delete(ThumbnailPath);
        try { if (System.IO.Directory.Exists(Directory) && System.IO.Directory.GetFileSystemEntries(Directory).Length == 0) System.IO.Directory.Delete(Directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LastError = ex.Message; }
        LeakedFiles = leaked;
    }

    private int Delete(string path)
    {
        try
        {
            if (!File.Exists(path)) return 0;
            File.Delete(path);
            FilesDeleted++;
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LastError = ex.Message; return 1; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Cleanup();
    }

    public ValueTask DisposeAsync() { Dispose(); return default; }

    private const ulong PngMagic = 0x89504E470D0A1A0AUL;
    private const uint Ftyp = 0x66747970, Moov = 0x6D6F6F76, Moof = 0x6D6F6F66, Mdat = 0x6D646174;
    private const uint Ihdr = 0x49484452, Idat = 0x49444154, Iend = 0x49454E44;

    private static uint Update(uint c, ReadOnlySpan<byte> d)
    {
        var t = CrcTable;
        for (var i = 0; i < d.Length; i++) c = t[(c ^ d[i]) & 0xFF] ^ (c >> 8);
        return c;
    }

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
}
