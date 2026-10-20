using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using PromptFlowStudio.Services.DigitalTwin;

namespace PromptFlowStudio.Services.MediaPipeline;

public enum MediaKind : byte { Unknown, Image, RawImage, Video, Audio }
public enum MediaFormat : byte { Unknown, Png, Jpeg, WebP, Bmp, Gif, Tiff, Dng, CameraRaw, Heic, Mp4, Mov, WebM, Avi, Wav, Mp3 }

/// <summary>헤더 프로브 결과(메모리 맵 · 전체 로드 없음).</summary>
public readonly record struct MediaProbe(MediaFormat Format, MediaKind Kind, long Bytes, int Width, int Height, double DurationSec, double Fps, string Codec, int BitDepth, bool HasAlpha)
{
    public static MediaProbe Unknown(long bytes) => new(MediaFormat.Unknown, MediaKind.Unknown, bytes, 0, 0, 0, 0, "", 0, false);
    public bool IsPortrait => Height > Width && Width > 0;
    public double Megapixels => Width * (double)Height / 1e6;
}

/// <summary>다운샘플 그리드 기반 이미지 통계(0..1) — 오토 튜너 비전 입력.</summary>
public readonly record struct ImageStats(float MeanLuma, float Contrast, float Saturation, float Warmth, float EdgeDensity, float SkinRatio, float DarkRatio, float BrightRatio, uint Dominant, int GridW, int GridH)
{
    public static readonly ImageStats None = new(0.5f, 0.2f, 0.3f, 0f, 0.1f, 0f, 0f, 0f, 0x808080, 0, 0);
    public bool Valid => GridW > 0;
}

public sealed record MediaIngestResult(bool Ok, string? Path, string? ProxyPath, MediaProbe Probe, ImageStats Stats, string Sha256, long Bytes, long Chunks, double Seconds, string Message)
{
    public double MegabytesPerSecond => Seconds > 0 ? Bytes / 1048576.0 / Seconds : 0;
    /// <summary>외부 업로드/생성에 쓸 경로(프록시 우선).</summary>
    public string? EffectivePath => ProxyPath ?? Path;
}

/// <summary>
/// 메모리 맵 미디어 리더 — 파일 전체를 가상 주소에 매핑하고 고정 스크래치(64KB)로 임의 위치 읽기(무할당 · 500MB+/8GB 파일도 RSS 상주 없이 페이지 단위 접근).
/// </summary>
public sealed class MappedMediaReader : IDisposable
{
    public const int ScratchSize = 64 * 1024;
    private readonly MemoryMappedFile? _mmf;
    private readonly MemoryMappedViewAccessor? _acc;
    private readonly byte[] _scratch = new byte[ScratchSize];

    public MappedMediaReader(string path)
    {
        Length = new FileInfo(path).Length;
        if (Length == 0) return;
        _mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _acc = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
    }

    public long Length { get; }

    /// <summary>pos 부터 dst 길이만큼(파일 끝 절단) 복사. 반환 = 바이트 수(무할당).</summary>
    public int Read(long pos, Span<byte> dst)
    {
        if (_acc is null || pos < 0 || pos >= Length) return 0;
        var total = (int)Math.Min(dst.Length, Length - pos);
        var done = 0;
        while (done < total)
        {
            var n = Math.Min(ScratchSize, total - done);
            _acc.ReadArray(pos + done, _scratch, 0, n);
            _scratch.AsSpan(0, n).CopyTo(dst[done..]);
            done += n;
        }
        return total;
    }

    public uint U32BE(long pos) { Span<byte> b = stackalloc byte[4]; return Read(pos, b) == 4 ? BinaryPrimitives.ReadUInt32BigEndian(b) : 0; }
    public ulong U64BE(long pos) { Span<byte> b = stackalloc byte[8]; return Read(pos, b) == 8 ? BinaryPrimitives.ReadUInt64BigEndian(b) : 0; }
    public ushort U16BE(long pos) { Span<byte> b = stackalloc byte[2]; return Read(pos, b) == 2 ? BinaryPrimitives.ReadUInt16BigEndian(b) : (ushort)0; }
    public uint U32(long pos, bool le) { Span<byte> b = stackalloc byte[4]; return Read(pos, b) == 4 ? (le ? BinaryPrimitives.ReadUInt32LittleEndian(b) : BinaryPrimitives.ReadUInt32BigEndian(b)) : 0; }
    public ushort U16(long pos, bool le) { Span<byte> b = stackalloc byte[2]; return Read(pos, b) == 2 ? (le ? BinaryPrimitives.ReadUInt16LittleEndian(b) : BinaryPrimitives.ReadUInt16BigEndian(b)) : (ushort)0; }
    public byte U8(long pos) { Span<byte> b = stackalloc byte[1]; return Read(pos, b) == 1 ? b[0] : (byte)0; }

    /// <summary>표본 지문(머리/중간/꼬리 64KB FNV-1a · 크기 혼합) — 중복 업로드 감지(무할당).</summary>
    public ulong Fingerprint()
    {
        var h = 14695981039346656037UL ^ (ulong)Length;
        Span<long> at = stackalloc long[3] { 0, Math.Max(0, Length / 2 - ScratchSize / 2), Math.Max(0, Length - ScratchSize) };
        for (var k = 0; k < 3; k++)
        {
            var n = (int)Math.Min(ScratchSize, Math.Max(0, Length - at[k]));
            if (n <= 0 || _acc is null) continue;
            _acc.ReadArray(at[k], _scratch, 0, n);
            for (var i = 0; i < n; i++) { h ^= _scratch[i]; h *= 1099511628211UL; }
        }
        return h;
    }

    public void Dispose() { _acc?.Dispose(); _mmf?.Dispose(); }
}

/// <summary>PNG IDAT 연결 스트림(메모리 맵 위 · 청크 경계 투명) — ZLibStream 입력.</summary>
internal sealed class PngIdatStream : Stream
{
    private readonly MappedMediaReader _r;
    private long _pos, _left;

    public PngIdatStream(MappedMediaReader r, long firstChunk) { _r = r; _pos = firstChunk; Advance(); }

    private void Advance()
    {
        while (_pos + 8 <= _r.Length)
        {
            var len = _r.U32BE(_pos);
            var type = _r.U32BE(_pos + 4);
            if (type == 0x49444154u) { _left = len; _pos += 8; return; }   // IDAT
            if (type == 0x49454E44u) break;                                 // IEND
            _pos += 12 + (long)len;
        }
        _left = 0; _pos = _r.Length;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        while (_left == 0)
        {
            if (_pos >= _r.Length) return 0;
            _pos += 4;   // CRC
            Advance();
            if (_pos >= _r.Length && _left == 0) return 0;
        }
        var n = _r.Read(_pos, buffer[..(int)Math.Min(buffer.Length, _left)]);
        if (n <= 0) return 0;
        _pos += n; _left -= n;
        return n;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// [PromptFlow Studio v16.57.0] 📦 고용량 미디어 스트리밍 &amp; 메모리 보호 파이프라인(Pure BCL).
///   ▸ 수신: 12MB/32MB 상한 해제(기본 64GB · 디스크 여유 검사) → 1MB 풀 버퍼 청크 루프(파일 크기 무관 상수 메모리 · 청크당 무할당) · 증분 SHA-256 · 사전 할당 ·
///     .part 임시 파일 → 원자 이동 · 실패/취소 시 잔여물 정리 · 진행률 콜백.
///   ▸ 분석: 메모리 맵(<see cref="MappedMediaReader"/>) 헤더 프로브 — PNG/JPEG/WebP/BMP/GIF/TIFF/DNG·CR2·NEF·ARW/HEIC · MP4/MOV(mvhd·tkhd·mdhd·stsd·stts)/WebM/AVI · WAV/MP3.
///   ▸ 디코딩/압축: PNG(IDAT 스트림 → zlib → 행 필터 복원 · 행 버퍼 2개)/BMP 순수 스트리밍 박스 다운샘플 · JPEG 등은 Windows GDI+ 축소 폴백 →
///     이미지 통계(휘도/대비/채도/색온도/엣지/피부/명암 비율/지배색) + 업로드 한도 초과 · 4K 초과 시 프록시 PNG(장변 2048) 자동 생성.
/// </summary>
public sealed class UnlimitedMediaStreamBufferPipeline
{
    public const int ChunkSize = 1 << 20, StatsGrid = 64, ProxyLongSide = 2048;
    public const long DefaultMaxBytes = 64L << 30, LegacyLimitBytes = 12L << 20, UploadLimitBytes = 32L << 20, DiskMarginBytes = 256L << 20;

    private long _files, _bytes, _chunks, _proxies, _failures;
    private double _lastMBps;

    public event Action? OnChange;

    public long MaxIngestBytes { get; set; } = DefaultMaxBytes;
    public long FilesIngested => Interlocked.Read(ref _files);
    public long BytesIngested => Interlocked.Read(ref _bytes);
    public long ChunksProcessed => Interlocked.Read(ref _chunks);
    public long ProxiesGenerated => Interlocked.Read(ref _proxies);
    public long Failures => Interlocked.Read(ref _failures);
    public double LastMegabytesPerSecond => Volatile.Read(ref _lastMBps);
    public MediaIngestResult? Last { get; private set; }
    public string? LastAction { get; private set; }

    /// <summary>수신 허용 여부 사전 검사(크기 상한 · 디스크 여유). 통과 시 null.</summary>
    public string? Precheck(long declaredBytes, string destDir)
    {
        if (declaredBytes > MaxIngestBytes) return "파일이 수신 상한(" + FormatBytes(MaxIngestBytes) + ")을 초과합니다.";
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(destDir));
            if (!string.IsNullOrEmpty(root))
            {
                var free = new DriveInfo(root).AvailableFreeSpace;
                if (declaredBytes > 0 && free - declaredBytes < DiskMarginBytes) return "디스크 여유 공간 부족(" + FormatBytes(free) + " 남음).";
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { }
        return null;
    }

    public static string FormatBytes(long b) => b >= 1L << 30 ? (b / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + "GB"
        : b >= 1L << 20 ? (b / (double)(1L << 20)).ToString("0.0", CultureInfo.InvariantCulture) + "MB"
        : (b / 1024.0).ToString("0", CultureInfo.InvariantCulture) + "KB";

    private static string TargetPath(string destDir, string fileName)
    {
        Directory.CreateDirectory(destDir);
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(ext) || ext.Length > 6) ext = ".bin";
        return Path.Combine(destDir, Guid.NewGuid().ToString("N") + ext.ToLowerInvariant());
    }

    private static FileStream OpenTarget(string part, long declared)
        => new(part, new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None, BufferSize = 0, Options = FileOptions.SequentialScan, PreallocationSize = Math.Max(0, declared) });

    /// <summary>청크 1개 처리(무할당) — 해시 누적 + 기록.</summary>
    private static void ProcessChunk(IncrementalHash hash, Stream dst, ReadOnlySpan<byte> chunk)
    {
        hash.AppendData(chunk);
        dst.Write(chunk);
    }

    /// <summary>동기 스트림 수신(테스트/로컬 파일). 메모리 사용 = 1MB 풀 버퍼 상수.</summary>
    public MediaIngestResult Ingest(Stream src, string destDir, string fileName, long declaredBytes = -1, Action<long, long>? progress = null, CancellationToken ct = default)
    {
        var pre = Precheck(declaredBytes, destDir);
        if (pre is not null) return Fail(pre);
        var path = TargetPath(destDir, fileName);
        var part = path + ".part";
        var buf = ArrayPool<byte>.Shared.Rent(ChunkSize);
        var sw = Stopwatch.StartNew();
        long total = 0, chunks = 0;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var dst = OpenTarget(part, declaredBytes))
            {
                int n;
                while ((n = ReadFull(src, buf.AsSpan(0, ChunkSize))) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    total += n;
                    if (total > MaxIngestBytes) throw new InvalidDataException("수신 상한 초과");
                    ProcessChunk(hash, dst, buf.AsSpan(0, n));
                    chunks++;
                    if ((chunks & 7) == 0) progress?.Invoke(total, declaredBytes);
                }
                dst.SetLength(total);
            }
            File.Move(part, path, true);
            progress?.Invoke(total, declaredBytes);
            return Complete(path, total, chunks, sw.Elapsed.TotalSeconds, Convert.ToHexString(hash.GetHashAndReset()));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or UnauthorizedAccessException)
        {
            TryDelete(part);
            return Fail(ex is OperationCanceledException ? "수신 취소" : "수신 실패: " + ex.Message);
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    /// <summary>비동기 스트림 수신(Blazor IBrowserFile.OpenReadStream 등).</summary>
    public async Task<MediaIngestResult> IngestAsync(Stream src, string destDir, string fileName, long declaredBytes = -1, Action<long, long>? progress = null, CancellationToken ct = default)
    {
        var pre = Precheck(declaredBytes, destDir);
        if (pre is not null) return Fail(pre);
        var path = TargetPath(destDir, fileName);
        var part = path + ".part";
        var buf = ArrayPool<byte>.Shared.Rent(ChunkSize);
        var sw = Stopwatch.StartNew();
        long total = 0, chunks = 0;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var dst = OpenTarget(part, declaredBytes))
            {
                while (true)
                {
                    var filled = 0;
                    while (filled < ChunkSize)
                    {
                        var r = await src.ReadAsync(buf.AsMemory(filled, ChunkSize - filled), ct).ConfigureAwait(false);
                        if (r <= 0) break;
                        filled += r;
                    }
                    if (filled == 0) break;
                    total += filled;
                    if (total > MaxIngestBytes) throw new InvalidDataException("수신 상한 초과");
                    hash.AppendData(buf, 0, filled);
                    await dst.WriteAsync(buf.AsMemory(0, filled), ct).ConfigureAwait(false);
                    chunks++;
                    if ((chunks & 7) == 0) progress?.Invoke(total, declaredBytes);
                    if (filled < ChunkSize) break;
                }
                dst.SetLength(total);
            }
            File.Move(part, path, true);
            progress?.Invoke(total, declaredBytes);
            return await Task.Run(() => Complete(path, total, chunks, sw.Elapsed.TotalSeconds, Convert.ToHexString(hash.GetHashAndReset())), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or UnauthorizedAccessException)
        {
            TryDelete(part);
            return Fail(ex is OperationCanceledException ? "수신 취소" : "수신 실패: " + ex.Message);
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    /// <summary>스트림 수신 코어 벤치용(무할당 · 테스트) — 해시/기록 없이 청크 루프만.</summary>
    public static long DrainChunks(Stream src, Span<byte> buffer, Stream sink, IncrementalHash hash)
    {
        long total = 0;
        int n;
        while ((n = ReadFull(src, buffer)) > 0) { ProcessChunk(hash, sink, buffer[..n]); total += n; }
        return total;
    }

    private static int ReadFull(Stream s, Span<byte> buf)
    {
        var filled = 0;
        while (filled < buf.Length)
        {
            var r = s.Read(buf[filled..]);
            if (r <= 0) break;
            filled += r;
        }
        return filled;
    }

    private MediaIngestResult Complete(string path, long total, long chunks, double secs, string sha)
    {
        var probe = Probe(path);
        var stats = ImageStats.None;
        string? proxy = null;
        var msg = "수신 완료 · " + FormatBytes(total);
        if (probe.Kind == MediaKind.Image)
        {
            var needProxy = total > UploadLimitBytes || Math.Max(probe.Width, probe.Height) > 4096 || probe.Format is MediaFormat.Bmp or MediaFormat.Tiff or MediaFormat.Gif;
            var res = AnalyzeImage(path, probe, needProxy ? ProxyLongSide : 0, out stats);
            if (res is not null && needProxy)
            {
                proxy = Path.ChangeExtension(path, null) + "_proxy.png";
                PromptFlowStudio.Services.Cad.CadWireframeOverlayEngine.WritePngRgb(proxy, res.Value.W, res.Value.H, res.Value.Rgb);
                Interlocked.Increment(ref _proxies);
                msg += " · 프록시 " + res.Value.W.ToString(CultureInfo.InvariantCulture) + "×" + res.Value.H.ToString(CultureInfo.InvariantCulture);
            }
            else if (!stats.Valid) msg += " · 픽셀 디코딩 미지원(헤더 분석만)";
        }
        else if (probe.Kind == MediaKind.RawImage) msg += " · RAW 헤더 분석(현상 미지원)";
        else if (probe.Kind == MediaKind.Video) msg += " · " + probe.Codec + " " + probe.DurationSec.ToString("0.0", CultureInfo.InvariantCulture) + "s";
        Interlocked.Increment(ref _files);
        Interlocked.Add(ref _bytes, total);
        Interlocked.Add(ref _chunks, chunks);
        var r = new MediaIngestResult(true, path, proxy, probe, stats, sha, total, chunks, secs, msg);
        Volatile.Write(ref _lastMBps, r.MegabytesPerSecond);
        Last = r;
        LastAction = "📦 " + msg;
        Raise();
        return r;
    }

    private MediaIngestResult Fail(string message)
    {
        Interlocked.Increment(ref _failures);
        LastAction = "⚠ " + message;
        Raise();
        return new MediaIngestResult(false, null, null, MediaProbe.Unknown(0), ImageStats.None, "", 0, 0, 0, message);
    }

    private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    // ══════════════════════ 포맷 스니핑 · 헤더 프로브 ══════════════════════

    public static MediaFormat Sniff(ReadOnlySpan<byte> h, string? ext = null)
    {
        if (h.Length >= 8 && h[..8].SequenceEqual(stackalloc byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return MediaFormat.Png;
        if (h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return MediaFormat.Jpeg;
        if (h.Length >= 12 && h[..4].SequenceEqual("RIFF"u8))
        {
            if (h[8..12].SequenceEqual("WEBP"u8)) return MediaFormat.WebP;
            if (h[8..12].SequenceEqual("AVI "u8)) return MediaFormat.Avi;
            if (h[8..12].SequenceEqual("WAVE"u8)) return MediaFormat.Wav;
        }
        if (h.Length >= 2 && h[0] == (byte)'B' && h[1] == (byte)'M') return MediaFormat.Bmp;
        if (h.Length >= 4 && h[..4].SequenceEqual("GIF8"u8)) return MediaFormat.Gif;
        if (h.Length >= 4 && (h[..4].SequenceEqual("II*\0"u8) || h[..4].SequenceEqual("MM\0*"u8)))
        {
            var e = (ext ?? "").ToLowerInvariant();
            return e == ".dng" ? MediaFormat.Dng : e is ".cr2" or ".nef" or ".arw" or ".orf" or ".rw2" or ".pef" ? MediaFormat.CameraRaw : MediaFormat.Tiff;
        }
        if (h.Length >= 4 && h[0] == 0x1A && h[1] == 0x45 && h[2] == 0xDF && h[3] == 0xA3) return MediaFormat.WebM;
        if (h.Length >= 12 && h[4..8].SequenceEqual("ftyp"u8))
        {
            var brand = h[8..12];
            if (brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) || brand.SequenceEqual("mif1"u8) || brand.SequenceEqual("avif"u8)) return MediaFormat.Heic;
            return brand.SequenceEqual("qt  "u8) ? MediaFormat.Mov : MediaFormat.Mp4;
        }
        if (h.Length >= 8 && (h[4..8].SequenceEqual("moov"u8) || h[4..8].SequenceEqual("mdat"u8) || h[4..8].SequenceEqual("wide"u8) || h[4..8].SequenceEqual("free"u8))) return MediaFormat.Mov;
        if (h.Length >= 3 && (h[..3].SequenceEqual("ID3"u8) || (h[0] == 0xFF && (h[1] & 0xE0) == 0xE0))) return MediaFormat.Mp3;
        return MediaFormat.Unknown;
    }

    public static MediaKind KindOf(MediaFormat f) => f switch
    {
        MediaFormat.Png or MediaFormat.Jpeg or MediaFormat.WebP or MediaFormat.Bmp or MediaFormat.Gif or MediaFormat.Tiff or MediaFormat.Heic => MediaKind.Image,
        MediaFormat.Dng or MediaFormat.CameraRaw => MediaKind.RawImage,
        MediaFormat.Mp4 or MediaFormat.Mov or MediaFormat.WebM or MediaFormat.Avi => MediaKind.Video,
        MediaFormat.Wav or MediaFormat.Mp3 => MediaKind.Audio,
        _ => MediaKind.Unknown,
    };

    /// <summary>메모리 맵 헤더 프로브(파일 크기 무관 · 헤더/박스만 접근).</summary>
    public static MediaProbe Probe(string path)
    {
        try
        {
            using var r = new MappedMediaReader(path);
            return Probe(r, Path.GetExtension(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return MediaProbe.Unknown(0); }
    }

    public static MediaProbe Probe(MappedMediaReader r, string? ext)
    {
        Span<byte> h = stackalloc byte[32];
        var hn = r.Read(0, h);
        var f = Sniff(h[..hn], ext);
        var kind = KindOf(f);
        var p = new MediaProbe(f, kind, r.Length, 0, 0, 0, 0, "", 8, false);
        return f switch
        {
            MediaFormat.Png => p with { Width = (int)r.U32BE(16), Height = (int)r.U32BE(20), BitDepth = r.U8(24), HasAlpha = r.U8(25) is 4 or 6, Codec = "deflate" },
            MediaFormat.Jpeg => ProbeJpeg(r, p),
            MediaFormat.Bmp => p with { Width = (int)r.U32(18, true), Height = Math.Abs((int)r.U32(22, true)), BitDepth = r.U16(28, true), HasAlpha = r.U16(28, true) == 32, Codec = "bmp" },
            MediaFormat.Gif => p with { Width = r.U16(6, true), Height = r.U16(8, true), Codec = "lzw" },
            MediaFormat.WebP => ProbeWebP(r, p),
            MediaFormat.Tiff or MediaFormat.Dng or MediaFormat.CameraRaw => ProbeTiff(r, p),
            MediaFormat.Mp4 or MediaFormat.Mov => ProbeIsoBmff(r, p),
            MediaFormat.Avi => ProbeAvi(r, p),
            MediaFormat.Wav => ProbeWav(r, p),
            MediaFormat.Heic => p with { Codec = "hevc" },
            MediaFormat.WebM => p with { Codec = "matroska" },
            _ => p,
        };
    }

    private static MediaProbe ProbeJpeg(MappedMediaReader r, MediaProbe p)
    {
        long pos = 2;
        for (var guard = 0; guard < 4096 && pos + 4 < r.Length; guard++)
        {
            if (r.U8(pos) != 0xFF) { pos++; continue; }
            var m = r.U8(pos + 1);
            if (m == 0xFF) { pos++; continue; }
            if (m is 0xD8 or 0x01 || m is >= 0xD0 and <= 0xD7) { pos += 2; continue; }
            if (m == 0xD9 || m == 0xDA) break;
            var len = r.U16BE(pos + 2);
            if (m is >= 0xC0 and <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC)
                return p with { Height = r.U16BE(pos + 5), Width = r.U16BE(pos + 7), BitDepth = r.U8(pos + 4), Codec = m == 0xC2 ? "jpeg-progressive" : "jpeg" };
            pos += 2 + len;
        }
        return p with { Codec = "jpeg" };
    }

    private static MediaProbe ProbeWebP(MappedMediaReader r, MediaProbe p)
    {
        var tag = r.U32BE(12);
        if (tag == 0x56503820u) return p with { Width = r.U16(26, true) & 0x3FFF, Height = r.U16(28, true) & 0x3FFF, Codec = "vp8" };   // "VP8 "
        if (tag == 0x5650384Cu)                                                                                                         // "VP8L"
        {
            var b = r.U32(21, true);
            return p with { Width = (int)(b & 0x3FFF) + 1, Height = (int)((b >> 14) & 0x3FFF) + 1, HasAlpha = ((b >> 28) & 1) != 0, Codec = "vp8l" };
        }
        if (tag == 0x56503858u)                                                                                                         // "VP8X"
            return p with { Width = (int)((r.U32(24, true) & 0xFFFFFF) + 1), Height = (int)((r.U32(27, true) & 0xFFFFFF) + 1), HasAlpha = (r.U8(20) & 0x10) != 0, Codec = "vp8x" };
        return p with { Codec = "webp" };
    }

    private static MediaProbe ProbeTiff(MappedMediaReader r, MediaProbe p)
    {
        var le = r.U8(0) == (byte)'I';
        var ifd = r.U32(4, le);
        if (ifd == 0 || ifd + 2 > r.Length) return p;
        int n = r.U16(ifd, le), w = 0, h = 0, bits = 8;
        var dng = false;
        for (var i = 0; i < Math.Min(n, 512); i++)
        {
            var e = ifd + 2 + i * 12L;
            var tag = r.U16(e, le);
            var type = r.U16(e + 2, le);
            var val = type == 3 ? r.U16(e + 8, le) : (int)r.U32(e + 8, le);
            if (tag == 256) w = val;
            else if (tag == 257) h = val;
            else if (tag == 258) bits = type == 3 && r.U32(e + 4, le) == 1 ? val : bits;
            else if (tag == 50706) dng = true;
        }
        var f = dng ? MediaFormat.Dng : p.Format;
        return p with { Format = f, Kind = KindOf(f), Width = w, Height = h, BitDepth = bits, Codec = dng ? "dng" : f == MediaFormat.CameraRaw ? "raw" : "tiff" };
    }

    private static MediaProbe ProbeWav(MappedMediaReader r, MediaProbe p)
    {
        long pos = 12;
        int rate = 0, byteRate = 0, bits = 16, ch = 0;
        for (var g = 0; g < 64 && pos + 8 <= r.Length; g++)
        {
            var id = r.U32BE(pos);
            var size = r.U32(pos + 4, true);
            if (id == 0x666D7420u) { ch = r.U16(pos + 10, true); rate = (int)r.U32(pos + 12, true); byteRate = (int)r.U32(pos + 16, true); bits = r.U16(pos + 22, true); }
            else if (id == 0x64617461u && byteRate > 0) return p with { DurationSec = size / (double)byteRate, BitDepth = bits, Codec = "pcm " + ch.ToString(CultureInfo.InvariantCulture) + "ch " + rate.ToString(CultureInfo.InvariantCulture) + "Hz" };
            pos += 8 + size + (size & 1);
        }
        return p with { Codec = "pcm" };
    }

    private static MediaProbe ProbeAvi(MappedMediaReader r, MediaProbe p)
    {
        Span<byte> win = stackalloc byte[4096];
        var n = r.Read(0, win);
        var at = win[..n].IndexOf("avih"u8);
        if (at < 0) return p with { Codec = "avi" };
        var d = at + 8L;
        var usPerFrame = r.U32(d, true);
        var frames = r.U32(d + 16, true);
        var fps = usPerFrame > 0 ? 1e6 / usPerFrame : 0;
        return p with { Width = (int)r.U32(d + 32, true), Height = (int)r.U32(d + 36, true), Fps = fps, DurationSec = fps > 0 ? frames / fps : 0, Codec = "avi" };
    }

    /// <summary>ISO BMFF(MP4/MOV) 박스 워크 — moov 위치(앞/뒤) 무관 · mdat 본문 미접근.</summary>
    private static MediaProbe ProbeIsoBmff(MappedMediaReader r, MediaProbe p)
    {
        var st = new IsoState();
        WalkBoxes(r, 0, r.Length, 0, ref st);
        var fps = st.VideoTimescale > 0 && st.SttsDelta > 0 ? st.VideoTimescale / (double)st.SttsDelta : 0;
        var dur = st.MovieTimescale > 0 ? st.MovieDuration / (double)st.MovieTimescale : 0;
        return p with { Width = st.Width, Height = st.Height, DurationSec = dur, Fps = Math.Round(fps, 3), Codec = st.Codec ?? (p.Format == MediaFormat.Mov ? "quicktime" : "mp4") };
    }

    private struct IsoState
    {
        public long MovieTimescale, MovieDuration, VideoTimescale, TrackTimescale, SttsDelta;
        public int Width, Height, TrackW, TrackH;
        public bool InVideo;
        public string? Codec;
    }

    private static void WalkBoxes(MappedMediaReader r, long start, long end, int depth, ref IsoState st)
    {
        var pos = start;
        for (var guard = 0; guard < 4096 && pos + 8 <= end; guard++)
        {
            long size = r.U32BE(pos);
            var type = r.U32BE(pos + 4);
            long hdr = 8;
            if (size == 1) { size = (long)r.U64BE(pos + 8); hdr = 16; }
            else if (size == 0) size = end - pos;
            if (size < hdr || pos + size > end) break;
            var body = pos + hdr;
            switch (type)
            {
                case 0x6D6F6F76u: case 0x6D646961u: case 0x6D696E66u: case 0x7374626Cu:   // moov · mdia · minf · stbl
                    WalkBoxes(r, body, pos + size, depth + 1, ref st);
                    break;
                case 0x7472616Bu:                                                         // trak
                    st.InVideo = false; st.TrackW = st.TrackH = 0;
                    WalkBoxes(r, body, pos + size, depth + 1, ref st);
                    break;
                case 0x6D766864u:                                                         // mvhd
                    if (r.U8(body) == 1) { st.MovieTimescale = r.U32BE(body + 20); st.MovieDuration = (long)r.U64BE(body + 24); }
                    else { st.MovieTimescale = r.U32BE(body + 12); st.MovieDuration = r.U32BE(body + 16); }
                    break;
                case 0x746B6864u:                                                         // tkhd — 끝 8B = width/height 16.16
                    st.TrackW = (int)(r.U32BE(pos + size - 8) >> 16); st.TrackH = (int)(r.U32BE(pos + size - 4) >> 16);
                    break;
                case 0x6D646864u:                                                         // mdhd
                    st.TrackTimescale = r.U8(body) == 1 ? r.U32BE(body + 20) : r.U32BE(body + 12);
                    break;
                case 0x68646C72u:                                                         // hdlr
                    if (r.U32BE(body + 8) == 0x76696465u)                                 // 'vide'
                    {
                        st.InVideo = true; st.Width = st.TrackW; st.Height = st.TrackH; st.VideoTimescale = st.TrackTimescale;
                    }
                    break;
                case 0x73747364u:                                                         // stsd → 첫 엔트리 fourcc
                    if (st.InVideo)
                    {
                        var fourcc = r.U32BE(body + 12);
                        Span<byte> cc = [(byte)(fourcc >> 24), (byte)(fourcc >> 16), (byte)(fourcc >> 8), (byte)fourcc];
                        st.Codec = cc[0] == (byte)'a' && cc[1] == (byte)'v' && cc[2] == (byte)'c' ? "h264"
                            : (cc[0] == (byte)'h' && cc[1] == (byte)'v' && cc[2] == (byte)'c') || (cc[0] == (byte)'h' && cc[1] == (byte)'e' && cc[2] == (byte)'v') ? "hevc"
                            : cc[0] == (byte)'a' && cc[1] == (byte)'v' && cc[2] == (byte)'0' ? "av1"
                            : cc[0] == (byte)'v' && cc[1] == (byte)'p' && cc[2] == (byte)'0' ? "vp9"
                            : cc[0] == (byte)'a' && cc[1] == (byte)'p' && cc[2] == (byte)'c' ? "prores" : "video";
                    }
                    break;
                case 0x73747473u:                                                         // stts 첫 엔트리 delta
                    if (st.InVideo && r.U32BE(body + 4) > 0) st.SttsDelta = r.U32BE(body + 12);
                    break;
            }
            pos += size;
        }
    }

    // ══════════════════════ 스트리밍 디코딩 · 통계 · 프록시 ══════════════════════

    /// <summary>픽셀 분석(+선택 프록시). proxyLongSide=0 이면 통계 그리드만. 반환 = 프록시 RGB(null = 디코딩 불가 또는 미요청).</summary>
    public static (byte[] Rgb, int W, int H)? AnalyzeImage(string path, MediaProbe probe, int proxyLongSide, out ImageStats stats)
    {
        stats = ImageStats.None;
        if (probe.Width <= 0 || probe.Height <= 0) return null;
        int gw, gh;
        var scale = Math.Min(1.0, StatsGrid / (double)Math.Max(probe.Width, probe.Height));
        gw = Math.Max(1, (int)Math.Round(probe.Width * scale)); gh = Math.Max(1, (int)Math.Round(probe.Height * scale));
        int pw = 0, ph = 0;
        if (proxyLongSide > 0)
        {
            var ps = Math.Min(1.0, proxyLongSide / (double)Math.Max(probe.Width, probe.Height));
            pw = Math.Max(1, (int)Math.Round(probe.Width * ps)); ph = Math.Max(1, (int)Math.Round(probe.Height * ps));
        }
        var acc = new BoxAccumulator(probe.Width, probe.Height, gw, gh, pw, ph);
        try
        {
            var ok = probe.Format switch
            {
                MediaFormat.Png => DecodePng(path, probe, acc),
                MediaFormat.Bmp => DecodeBmp(path, probe, acc),
                _ => DecodeGdi(path, acc),
            };
            if (!ok) return null;
            stats = acc.Stats();
            return pw > 0 ? (acc.ProxyRgb(), pw, ph) : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or OverflowException or IndexOutOfRangeException)
        {
            PromptFlowStudio.Program.Log("MediaPipeline.Decode", ex);
            return null;
        }
        finally { acc.Dispose(); }
    }

    /// <summary>원본 해상도 행 스트림 → 통계 그리드 + 프록시 그리드 박스 누적(풀 버퍼 · 행 단위).</summary>
    internal sealed class BoxAccumulator : IDisposable
    {
        private readonly int _w, _h, _gw, _gh, _pw, _ph;
        private readonly long[] _g;
        private readonly int[]? _p;
        private readonly int[] _gx, _px;

        public BoxAccumulator(int w, int h, int gw, int gh, int pw, int ph)
        {
            _w = w; _h = h; _gw = gw; _gh = gh; _pw = pw; _ph = ph;
            _g = new long[gw * gh * 4];
            _p = pw > 0 ? ArrayPool<int>.Shared.Rent(pw * ph * 4) : null;
            _p?.AsSpan(0, pw * ph * 4).Clear();
            _gx = ArrayPool<int>.Shared.Rent(w);
            _px = ArrayPool<int>.Shared.Rent(w);
            for (var x = 0; x < w; x++) { _gx[x] = (int)((long)x * gw / w); _px[x] = pw > 0 ? (int)((long)x * pw / w) : 0; }
        }

        /// <summary>RGB24 행 1개 누적(무할당).</summary>
        public void Row(int y, ReadOnlySpan<byte> rgb)
        {
            var gyo = (int)((long)y * _gh / _h) * _gw;
            var pyo = _pw > 0 ? (int)((long)y * _ph / _h) * _pw : 0;
            for (var x = 0; x < _w; x++)
            {
                int r = rgb[x * 3], g = rgb[x * 3 + 1], b = rgb[x * 3 + 2];
                var gi = (gyo + _gx[x]) * 4;
                _g[gi] += r; _g[gi + 1] += g; _g[gi + 2] += b; _g[gi + 3]++;
                if (_p is not null)
                {
                    var pi = (pyo + _px[x]) * 4;
                    _p[pi] += r; _p[pi + 1] += g; _p[pi + 2] += b; _p[pi + 3]++;
                }
            }
        }

        public byte[] ProxyRgb()
        {
            var o = new byte[_pw * _ph * 3];
            for (var i = 0; i < _pw * _ph; i++)
            {
                var c = Math.Max(1, _p![i * 4 + 3]);
                o[i * 3] = (byte)(_p[i * 4] / c); o[i * 3 + 1] = (byte)(_p[i * 4 + 1] / c); o[i * 3 + 2] = (byte)(_p[i * 4 + 2] / c);
            }
            return o;
        }

        public ImageStats Stats()
        {
            var n = _gw * _gh;
            Span<float> luma = n <= 4096 ? stackalloc float[n] : new float[n];
            Span<int> hist = stackalloc int[512];
            double sl = 0, sl2 = 0, sat = 0, warm = 0;
            int skin = 0, dark = 0, bright = 0;
            for (var i = 0; i < n; i++)
            {
                var c = Math.Max(1, _g[i * 4 + 3]);
                byte r = (byte)(_g[i * 4] / c), g = (byte)(_g[i * 4 + 1] / c), b = (byte)(_g[i * 4 + 2] / c);
                var l = (0.299f * r + 0.587f * g + 0.114f * b) / 255f;
                luma[i] = l; sl += l; sl2 += l * l;
                int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
                sat += mx == 0 ? 0 : (mx - mn) / (double)mx;
                warm += (r - b) / 255.0;
                if (AiDigitalTwinGeneratorEngine.IsSkin(r, g, b)) skin++;
                if (l < 0.2f) dark++; else if (l > 0.8f) bright++;
                hist[((r >> 5) << 6) | ((g >> 5) << 3) | (b >> 5)]++;
            }
            int edges = 0, pairs = 0;
            for (var y = 0; y < _gh; y++)
                for (var x = 0; x < _gw; x++)
                {
                    var i = y * _gw + x;
                    if (x + 1 < _gw) { pairs++; if (MathF.Abs(luma[i] - luma[i + 1]) > 0.09f) edges++; }
                    if (y + 1 < _gh) { pairs++; if (MathF.Abs(luma[i] - luma[i + _gw]) > 0.09f) edges++; }
                }
            var best = 0;
            for (var k = 1; k < 512; k++) if (hist[k] > hist[best]) best = k;
            uint dom = ((uint)(((best >> 6) & 7) * 32 + 16) << 16) | ((uint)(((best >> 3) & 7) * 32 + 16) << 8) | (uint)((best & 7) * 32 + 16);
            var mean = sl / n;
            return new ImageStats((float)mean, (float)Math.Sqrt(Math.Max(0, sl2 / n - mean * mean)), (float)(sat / n), (float)(warm / n),
                pairs == 0 ? 0 : edges / (float)pairs, skin / (float)n, dark / (float)n, bright / (float)n, dom, _gw, _gh);
        }

        public void Dispose()
        {
            if (_p is not null) ArrayPool<int>.Shared.Return(_p);
            ArrayPool<int>.Shared.Return(_gx);
            ArrayPool<int>.Shared.Return(_px);
        }
    }

    private static bool DecodePng(string path, MediaProbe probe, BoxAccumulator acc)
    {
        using var r = new MappedMediaReader(path);
        int w = probe.Width, h = probe.Height, depth = r.U8(24), ct = r.U8(25), interlace = r.U8(28);
        if (interlace != 0 || depth is not (8 or 16) || ct is not (0 or 2 or 3 or 4 or 6)) return false;
        var ch = ct switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        var bpp = ch * depth / 8;
        var stride = w * bpp;
        Span<byte> pal = stackalloc byte[768];
        long pos = 8, firstData = -1;
        while (pos + 8 <= r.Length)
        {
            var len = r.U32BE(pos);
            var type = r.U32BE(pos + 4);
            if (type == 0x504C5445u) r.Read(pos + 8, pal[..(int)Math.Min(768, len)]);       // PLTE
            if (type == 0x49444154u) { firstData = pos; break; }
            pos += 12 + (long)len;
        }
        if (firstData < 0) return false;
        var prev = ArrayPool<byte>.Shared.Rent(stride + 1);
        var cur = ArrayPool<byte>.Shared.Rent(stride + 1);
        var rgb = ArrayPool<byte>.Shared.Rent(w * 3);
        try
        {
            Array.Clear(prev, 0, stride + 1);
            using var z = new ZLibStream(new PngIdatStream(r, firstData), CompressionMode.Decompress);
            for (var y = 0; y < h; y++)
            {
                if (ReadFull(z, cur.AsSpan(0, stride + 1)) != stride + 1) return y > h / 2;
                var f = cur[0];
                var c = cur.AsSpan(1, stride); var p = prev.AsSpan(1, stride);
                for (var i = 0; i < stride; i++)
                {
                    int a = i >= bpp ? c[i - bpp] : 0, b = p[i], d = i >= bpp ? p[i - bpp] : 0;
                    c[i] = f switch
                    {
                        1 => (byte)(c[i] + a),
                        2 => (byte)(c[i] + b),
                        3 => (byte)(c[i] + ((a + b) >> 1)),
                        4 => (byte)(c[i] + Paeth(a, b, d)),
                        _ => c[i],
                    };
                }
                var step = depth / 8;
                for (var x = 0; x < w; x++)
                {
                    var o = x * bpp;
                    byte rr, gg, bb;
                    if (ct == 3) { var k = c[o] * 3; rr = pal[k]; gg = pal[k + 1]; bb = pal[k + 2]; }
                    else if (ch <= 2) { rr = gg = bb = c[o]; }
                    else { rr = c[o]; gg = c[o + step]; bb = c[o + 2 * step]; }
                    rgb[x * 3] = rr; rgb[x * 3 + 1] = gg; rgb[x * 3 + 2] = bb;
                }
                acc.Row(y, rgb.AsSpan(0, w * 3));
                (prev, cur) = (cur, prev);
            }
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(prev); ArrayPool<byte>.Shared.Return(cur); ArrayPool<byte>.Shared.Return(rgb);
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static bool DecodeBmp(string path, MediaProbe probe, BoxAccumulator acc)
    {
        using var r = new MappedMediaReader(path);
        var bits = r.U16(28, true);
        if (bits is not (24 or 32) || r.U32(30, true) is not (0 or 3)) return false;
        var off = r.U32(10, true);
        var bottomUp = (int)r.U32(22, true) > 0;
        int w = probe.Width, h = probe.Height, bpp = bits / 8, stride = (w * bpp + 3) & ~3;
        var row = ArrayPool<byte>.Shared.Rent(stride);
        var rgb = ArrayPool<byte>.Shared.Rent(w * 3);
        try
        {
            for (var y = 0; y < h; y++)
            {
                var src = bottomUp ? h - 1 - y : y;
                if (r.Read(off + (long)src * stride, row.AsSpan(0, stride)) < w * bpp) return false;
                for (var x = 0; x < w; x++) { rgb[x * 3] = row[x * bpp + 2]; rgb[x * 3 + 1] = row[x * bpp + 1]; rgb[x * 3 + 2] = row[x * bpp]; }
                acc.Row(y, rgb.AsSpan(0, w * 3));
            }
            return true;
        }
        finally { ArrayPool<byte>.Shared.Return(row); ArrayPool<byte>.Shared.Return(rgb); }
    }

    /// <summary>JPEG/GIF/TIFF 등 — Windows GDI+ 디코드(원본 해상도 · 행 단위 누적). 모바일은 미지원(false).</summary>
    private static bool DecodeGdi(string path, BoxAccumulator acc)
    {
#if WINDOWS
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        using var img = System.Drawing.Image.FromStream(fs, false, false);
        using var bmp = new System.Drawing.Bitmap(img);
        int w = bmp.Width, h = bmp.Height;
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        var row = ArrayPool<byte>.Shared.Rent(Math.Abs(data.Stride));
        var rgb = ArrayPool<byte>.Shared.Rent(w * 3);
        try
        {
            for (var y = 0; y < h; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, w * 3);
                for (var x = 0; x < w; x++) { rgb[x * 3] = row[x * 3 + 2]; rgb[x * 3 + 1] = row[x * 3 + 1]; rgb[x * 3 + 2] = row[x * 3]; }
                acc.Row(y, rgb.AsSpan(0, w * 3));
            }
            return true;
        }
        finally
        {
            bmp.UnlockBits(data);
            ArrayPool<byte>.Shared.Return(row); ArrayPool<byte>.Shared.Return(rgb);
        }
#else
        _ = path; _ = acc;
        return false;
#endif
    }

    /// <summary>기존 파일(로컬 경로)에 대해 업로드 한도 초과 시 프록시 생성 — 앵커/외부 업로드 경로 공용. 반환 = 사용할 경로.</summary>
    public static string EnsureUploadProxy(string path, long uploadLimitBytes = UploadLimitBytes)
    {
        try
        {
            var len = new FileInfo(path).Length;
            var probe = Probe(path);
            if (probe.Kind != MediaKind.Image || (len <= uploadLimitBytes && Math.Max(probe.Width, probe.Height) <= 4096)) return path;
            var proxy = Path.ChangeExtension(path, null) + "_proxy.png";
            if (File.Exists(proxy)) return proxy;
            var res = AnalyzeImage(path, probe, ProxyLongSide, out _);
            if (res is null) return path;
            PromptFlowStudio.Services.Cad.CadWireframeOverlayEngine.WritePngRgb(proxy, res.Value.W, res.Value.H, res.Value.Rgb);
            return proxy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return path; }
    }

    internal void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("MediaPipeline.OnChange", ex); }
    }
}
