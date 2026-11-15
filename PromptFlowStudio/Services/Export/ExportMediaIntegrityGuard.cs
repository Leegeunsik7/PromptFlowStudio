using System;
using System.Buffers.Binary;
using System.IO;

namespace PromptFlowStudio.Services.Export;

/// <summary>
/// 🛡️ 내보내기 직전 MP4 무결성 가드(Pure BCL · <see cref="FileStream"/> 전용 · 자식 프로세스 0 → Desktop/iOS 공용).
///   ▸ 파일 존재 · 최소 <see cref="MinBytes"/> 바이트 · ISO BMFF 최상위 박스 체인 순회.
///   ▸ 첫 박스 <c>ftyp</c> 필수 · <c>moov</c> 존재 필수(faststart 아님 → mdat 뒤 moov 허용) · 박스가 파일 끝을 넘으면 잘림 판정.
/// </summary>
public static class ExportMediaIntegrityGuard
{
    public const int MinBytes = 1024;
    /// <summary>비정상 파일 무한 순회 방지 상한.</summary>
    public const int MaxTopLevelBoxes = 4096;

    private const uint Ftyp = 0x66747970;   // 'ftyp'
    private const uint Moov = 0x6D6F6F76;   // 'moov'

    /// <summary>ISO BMFF 계열 확장자(.mp4 · .m4v · .mov) — 가드 적용 대상.</summary>
    public static bool IsIsoBmffPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(path);
        return ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || ext.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".mov", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>검증 실패 → 한국어 진단 <see cref="InvalidOperationException"/>.</summary>
    public static void EnsureValidMp4(string filePath)
    {
        if (!TryValidateMp4(filePath, out var error)) throw new InvalidOperationException(error);
    }

    /// <summary>예외 없는 검증(사유 반환).</summary>
    public static bool TryValidateMp4(string? filePath, out string error)
    {
        var name = string.IsNullOrWhiteSpace(filePath) ? "(경로 없음)" : Path.GetFileName(filePath);
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            error = "🛡️ 영상 무결성 검사 실패: 파일을 찾을 수 없습니다 — " + name;
            return false;
        }
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.None);
            var length = fs.Length;
            if (length < MinBytes)
            {
                error = "🛡️ 영상 무결성 검사 실패: 파일이 너무 작습니다(" + length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " B < " + MinBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + " B) — 렌더가 중단되었거나 잘린 파일입니다: " + name;
                return false;
            }

            Span<byte> head = stackalloc byte[16];
            long pos = 0;
            bool sawFtyp = false, sawMoov = false;
            for (var n = 0; n < MaxTopLevelBoxes && pos < length; n++)
            {
                if (length - pos < 8) { error = Truncated(name, "박스 헤더가 잘렸습니다"); return false; }
                fs.Position = pos;
                if (!ReadExactly(fs, head[..8])) { error = Truncated(name, "박스 헤더를 읽을 수 없습니다"); return false; }
                long size = BinaryPrimitives.ReadUInt32BigEndian(head);
                var type = BinaryPrimitives.ReadUInt32BigEndian(head[4..]);
                var headerLen = 8L;
                if (size == 1)
                {
                    if (length - pos < 16 || !ReadExactly(fs, head.Slice(8, 8))) { error = Truncated(name, "64비트 박스 크기가 잘렸습니다"); return false; }
                    var large = BinaryPrimitives.ReadUInt64BigEndian(head[8..]);
                    if (large > long.MaxValue) { error = Corrupt(name, "박스 크기가 비정상입니다"); return false; }
                    size = (long)large;
                    headerLen = 16;
                }
                else if (size == 0) size = length - pos;   // 파일 끝까지

                if (n == 0 && type != Ftyp) { error = Corrupt(name, "ftyp 헤더가 없습니다(MP4/ISO BMFF 형식 아님)"); return false; }
                if (size < headerLen) { error = Corrupt(name, "박스 크기가 헤더보다 작습니다"); return false; }
                if (size > length - pos) { error = Truncated(name, "'" + FourCc(type) + "' 박스가 파일 끝을 넘습니다"); return false; }

                if (type == Ftyp) sawFtyp = true;
                else if (type == Moov) sawMoov = true;
                pos += size;
            }

            if (!sawFtyp) { error = Corrupt(name, "ftyp 헤더가 없습니다"); return false; }
            if (!sawMoov) { error = Truncated(name, "moov(메타데이터) 박스가 없습니다 — 렌더가 완료되지 않았습니다"); return false; }
            error = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = "🛡️ 영상 무결성 검사 실패: 파일을 읽을 수 없습니다 — " + name + " (" + ex.Message + ")";
            return false;
        }
    }

    private static bool ReadExactly(FileStream fs, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var r = fs.Read(buffer[read..]);
            if (r <= 0) return false;
            read += r;
        }
        return true;
    }

    private static string FourCc(uint type)
    {
        Span<char> c = stackalloc char[4];
        for (var i = 0; i < 4; i++)
        {
            var b = (byte)(type >> (24 - i * 8));
            c[i] = b is >= 0x20 and < 0x7F ? (char)b : '?';
        }
        return new string(c);
    }

    private static string Truncated(string name, string detail) => "🛡️ 영상 무결성 검사 실패: 잘린 MP4 — " + detail + ": " + name;
    private static string Corrupt(string name, string detail) => "🛡️ 영상 무결성 검사 실패: 손상된 MP4 — " + detail + ": " + name;

    /// <summary>테스트/더미용 최소 유효 MP4 바이트(<c>ftyp</c>(isom) + <c>moov</c> + <c>free</c> 패딩) — <paramref name="totalBytes"/> ≥ <see cref="MinBytes"/>.</summary>
    public static byte[] BuildMinimalMp4(int totalBytes = MinBytes)
    {
        if (totalBytes < 40) totalBytes = 40;
        var buf = new byte[totalBytes];
        var s = buf.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(s, 16); BinaryPrimitives.WriteUInt32BigEndian(s[4..], Ftyp);
        "isom"u8.CopyTo(s[8..]); BinaryPrimitives.WriteUInt32BigEndian(s[12..], 0x200);
        BinaryPrimitives.WriteUInt32BigEndian(s[16..], 8); BinaryPrimitives.WriteUInt32BigEndian(s[20..], Moov);
        BinaryPrimitives.WriteUInt32BigEndian(s[24..], (uint)(totalBytes - 24)); "free"u8.CopyTo(s[28..]);
        return buf;
    }
}
