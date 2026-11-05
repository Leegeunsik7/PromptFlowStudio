using System;
using System.Globalization;
using System.IO;

namespace PromptFlowStudio.Services.Render;

/// <summary>사전 점검 결과 — 여유/요구 바이트 · 거부 사유(통과 시 null).</summary>
public sealed record PreFlightCheckResult(bool Ok, long FreeBytes, long RequiredBytes, string? Message)
{
    public long FreeMb => FreeBytes / (1024 * 1024);
    public long RequiredMb => RequiredBytes / (1024 * 1024);
}

/// <summary>
/// 🛡️ 사전 리소스 센티넬 — 4K 업스케일·60fps 보간·듀얼 분할 같은 대용량(1~3GB 임시) 인코딩 착수 전 디스크 여유 공간 Fail-Fast 점검.
///   ▸ 여유 &lt; 요구 → 즉시 거부(<c>Insufficient disk space: {free}MB available, {required}MB required.</c>) — 장시간 처리 후 IOException 중단 방지.
///   ▸ 여유 공간 조회 실패(권한·미지원 경로) → 통과(기존 동작 보존 · 실제 쓰기 실패는 하위 폴백이 처리).
///   ▸ <see cref="FreeSpaceProvider"/> 주입으로 테스트에서 저용량 시뮬레이션.
/// </summary>
public sealed class PreFlightResourceSentinel
{
    public const long DefaultRequiredBytes = 5L * 1024 * 1024 * 1024;

    /// <summary>디렉터리 → 가용 바이트(null = 조회 불가). 기본 = <see cref="DriveInfo.AvailableFreeSpace"/>.</summary>
    public Func<string, long?> FreeSpaceProvider { get; set; } = QueryFreeSpace;

    public PreFlightCheckResult CheckDiskSpace(string targetDirectory, long requiredBytes = DefaultRequiredBytes)
    {
        if (requiredBytes < 0) requiredBytes = 0;
        if (string.IsNullOrWhiteSpace(targetDirectory)) return new(true, -1, requiredBytes, null);
        long? free;
        try { free = FreeSpaceProvider(targetDirectory); }
        catch (Exception ex) { Program.Log("PreFlight.FreeSpace", ex); free = null; }
        if (free is not { } f) return new(true, -1, requiredBytes, null);
        if (f >= requiredBytes) return new(true, f, requiredBytes, null);
        var r = new PreFlightCheckResult(false, f, requiredBytes, null);
        return r with { Message = BuildMessage(r.FreeMb, r.RequiredMb) };
    }

    public static string BuildMessage(long freeMb, long requiredMb)
        => "Insufficient disk space: " + freeMb.ToString(CultureInfo.InvariantCulture) + "MB available, "
           + requiredMb.ToString(CultureInfo.InvariantCulture) + "MB required.";

    public static long? QueryFreeSpace(string directory)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            if (string.IsNullOrEmpty(root)) return null;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
