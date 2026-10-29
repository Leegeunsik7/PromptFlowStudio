using System;
using System.Collections.Generic;
using System.IO;
using PromptFlowStudio.Services.Engines;   // CliPathResolver

namespace PromptFlowStudio.Services.Render;

/// <summary>ffmpeg 해결 결과 — 절대경로(null = 미설치) + 출처 라벨(배지용).</summary>
public sealed record FfmpegLocation(string? Path, string Source)
{
    public bool Found => !string.IsNullOrEmpty(Path);
}

/// <summary>
/// [PromptFlowStudio] 배포 동봉 ffmpeg 우선 탐색 — 순수 BCL(외부 NuGet 0).
///   탐색 순서(앱 폴더 우선 → 시스템):
///     ① <c>{BaseDirectory}\tools\ffmpeg\bin\ffmpeg.exe</c> ② <c>{BaseDirectory}\tools\ffmpeg.exe</c> ③ <c>{BaseDirectory}\ffmpeg.exe</c>
///     ④ <see cref="CliPathResolver"/>(Tools\ffmpeg 번들 · LocalAppData · 시스템 PATH).
///   배포 압축본에 <c>tools\</c> 로 동봉하면 외부 설치 없이 실물 합본/음성 믹싱이 동작한다.
/// </summary>
public static class FfmpegLocator
{
    private static readonly string ExeName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

    /// <summary>앱 폴더 기준 로컬 후보(우선순위 순).</summary>
    public static IReadOnlyList<string> LocalCandidates(string baseDir) => new[]
    {
        Path.Combine(baseDir, "tools", "ffmpeg", "bin", ExeName),
        Path.Combine(baseDir, "tools", ExeName),
        Path.Combine(baseDir, ExeName),
    };

    /// <summary>기본 탐색 — 앱 실행 폴더 기준.</summary>
    public static FfmpegLocation Locate() => Locate(AppContext.BaseDirectory, null);

    /// <param name="baseDir">로컬 후보 기준 폴더(테스트 주입).</param>
    /// <param name="systemFallback">로컬 부재 시 폴백(null 이면 <see cref="CliPathResolver"/>).</param>
    public static FfmpegLocation Locate(string baseDir, Func<string?>? systemFallback)
    {
        try
        {
            foreach (var c in LocalCandidates(baseDir))
                if (File.Exists(c)) return new(Path.GetFullPath(c), "로컬 tools");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }

        string? sys;
        try { sys = systemFallback is null ? CliPathResolver.GetExecutablePath("ffmpeg", "ffmpeg.exe") : systemFallback(); }
        catch (Exception ex) { Program.Log("FfmpegLocator", ex); sys = null; }
        return string.IsNullOrEmpty(sys) ? new(null, "미설치") : new(sys, "시스템 PATH");
    }
}
