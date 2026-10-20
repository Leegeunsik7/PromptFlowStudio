using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PromptFlowStudio.Services.Account;   // LicenseService.DataAreaRoot()

namespace PromptFlowStudio.Services.Engines;

/// <summary>CLI 실행 파일이 해결된 출처(뱃지/진단용).</summary>
public enum CliSource
{
    /// <summary>어디서도 못 찾음(미설치).</summary>
    None,
    /// <summary>사용자가 지정한 커스텀 경로(설정 저장값).</summary>
    Custom,
    /// <summary>앱 실행 폴더 번들(<c>BaseDirectory\Tools\{engine}\</c>).</summary>
    Bundle,
    /// <summary>로컬 앱데이터 번들(<c>%LocalAppData%\PromptFlowStudio\Tools\{engine}\</c>).</summary>
    LocalAppData,
    /// <summary>시스템 PATH(기존 동작 · 사용자가 별도 설치).</summary>
    SystemPath,
}

/// <summary>CLI 경로 해결 결과 — 절대경로 + 출처.</summary>
public sealed record CliResolution(string? Path, CliSource Source)
{
    /// <summary>실행 파일을 찾았는지.</summary>
    public bool Found => !string.IsNullOrEmpty(Path);
    /// <summary>사용자 입력 없이 자동 해결됐는지(번들/로컬앱데이터/PATH).</summary>
    public bool IsAuto => Source is CliSource.Bundle or CliSource.LocalAppData or CliSource.SystemPath;
    public static readonly CliResolution NotFound = new(null, CliSource.None);
}

/// <summary>
/// [PromptFlowStudio] Zero-Config CLI 경로 자동 해결사 — 순수 BCL <see cref="System.IO"/>(외부 NuGet 0).
///   사용자가 [찾기]로 경로를 일일이 지정하지 않아도, 앱 실행 시 기본 번들 경로를 자동 탐색해 CLI 엔진을 즉시 사용 가능하게 한다.
///   ▸ <b>3단계 탐색 우선순위</b>:
///     ① 사용자 설정 저장값(커스텀 경로 · appsettings.override.json/engines.json 기록) — 파일 존재 또는 이름만이면 PATH.
///     ② 앱 실행 폴더 번들 — <c>AppDomain.CurrentDomain.BaseDirectory\Tools\{engineName}\</c>(하위 폴더 1단계까지 탐색).
///     ③ 로컬 앱데이터 — <c>%LocalAppData%\PromptFlowStudio\Tools\{engineName}\</c>.
///     (④ 폴백: 시스템 PATH — 기존 동작 보존.)
///   ▸ 어디서도 못 찾으면 null(<see cref="CliResolution.NotFound"/>) → 호출 엔진 기능만 Graceful 비활성(원본 파이프라인 정상).
///   ▸ Windows 는 확장자 없는 후보에 <c>.exe</c> 를 자동 부가해 함께 탐색.
/// </summary>
public static class CliPathResolver
{
    /// <summary>앱 번들 도구 폴더 — <c>{BaseDirectory}\Tools\{engineName}\</c>.</summary>
    public static string BundleToolsDir(string engineName) => Path.Combine(AppContext.BaseDirectory, "Tools", SafeName(engineName));

    /// <summary>로컬 앱데이터 도구 폴더 — <c>%LocalAppData%\PromptFlowStudio\Tools\{engineName}\</c>.</summary>
    public static string DataToolsDir(string engineName) => Path.Combine(LicenseService.DataAreaRoot(), "Tools", SafeName(engineName));

    /// <summary>
    /// 3단계(+PATH) 우선순위로 CLI 실행 파일을 해결한다. 출처(<see cref="CliSource"/>)까지 반환(뱃지용).
    /// </summary>
    /// <param name="engineName">엔진 폴더명(Tools 하위). 예: "RealEsrgan", "ffmpeg".</param>
    /// <param name="exeCandidates">가능한 실행 파일명들(확장자 유무 무관). 예: ["realesrgan-ncnn-vulkan"].</param>
    /// <param name="customPath">사용자 설정 저장값(있으면 최우선). null/빈값이면 건너뜀.</param>
    public static CliResolution Resolve(string engineName, IEnumerable<string>? exeCandidates, string? customPath)
    {
        var cands = Normalize(exeCandidates);

        // ① 사용자 설정 저장값(파일 존재 또는 이름만이면 PATH) — 최우선.
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            var c = ProcessRunner.Resolve(customPath);   // File.Exists 또는 PATH 조회
            if (c is not null) return new(c, CliSource.Custom);
            // 커스텀이 설정됐지만 못 찾음 → 번들로 폴백(Zero-Config 복원력).
        }

        // ② 앱 실행 폴더 번들.
        var bundle = SearchDir(BundleToolsDir(engineName), cands);
        if (bundle is not null) return new(bundle, CliSource.Bundle);

        // ③ 로컬 앱데이터 번들.
        var lad = SearchDir(DataToolsDir(engineName), cands);
        if (lad is not null) return new(lad, CliSource.LocalAppData);

        // ④ 시스템 PATH(기존 동작 보존).
        if (cands.Count > 0)
        {
            var onPath = ProcessRunner.Resolve(null, cands.ToArray());
            if (onPath is not null) return new(onPath, CliSource.SystemPath);
        }

        return CliResolution.NotFound;
    }

    /// <summary>
    /// [요구사항 시그니처] 엔진명 + 실행파일명으로 유효한 절대경로를 반환(3단계 탐색). 없으면 null.
    ///   <paramref name="customPath"/> 지정 시 1단계(커스텀)로 사용.
    /// </summary>
    public static string? GetExecutablePath(string engineName, string exeFileName, string? customPath = null)
        => Resolve(engineName, new[] { exeFileName }, customPath).Path;

    /// <summary>번들(②③)에서만 찾은 경로 — UI 의 '기본 번들 경로' 연한 표시(placeholder)용. 커스텀/PATH 제외.</summary>
    public static string? GetBundlePath(string engineName, IEnumerable<string>? exeCandidates)
    {
        var cands = Normalize(exeCandidates);
        return SearchDir(BundleToolsDir(engineName), cands) ?? SearchDir(DataToolsDir(engineName), cands);
    }

    // ── 내부 ──

    /// <summary>후보 실행 파일명 정규화 — 공백/중복 제거 + Windows 는 확장자 없는 후보에 .exe 부가.</summary>
    private static List<string> Normalize(IEnumerable<string>? raw)
    {
        var list = new List<string>();
        foreach (var r in raw ?? Enumerable.Empty<string>())
        {
            var s = (r ?? "").Trim().Trim('"');
            if (s.Length == 0) continue;
            // 커스텀 경로가 후보로 섞여 들어와도 파일명만 취한다(번들 폴더 안 탐색용).
            var name = Path.GetFileName(s);
            if (name.Length == 0) name = s;
            AddUnique(list, name);
            if (OperatingSystem.IsWindows() && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                AddUnique(list, name + ".exe");
        }
        return list;
    }

    private static void AddUnique(List<string> list, string v)
    {
        if (!list.Contains(v, StringComparer.OrdinalIgnoreCase)) list.Add(v);
    }

    /// <summary>디렉터리(+하위 1단계)에서 후보 실행 파일을 찾는다. 없으면 null.</summary>
    private static string? SearchDir(string dir, IReadOnlyList<string> cands)
    {
        try
        {
            if (cands.Count == 0 || !Directory.Exists(dir)) return null;

            // 폴더 직속.
            foreach (var c in cands)
            {
                var p = Path.Combine(dir, c);
                if (File.Exists(p)) return Path.GetFullPath(p);
            }
            // 하위 폴더 1단계(도구가 버전 폴더에 들어있는 경우 대비).
            foreach (var sub in SafeSubdirs(dir))
                foreach (var c in cands)
                {
                    var p = Path.Combine(sub, c);
                    if (File.Exists(p)) return Path.GetFullPath(p);
                }
        }
        catch { /* 접근 실패 → null */ }
        return null;
    }

    private static IEnumerable<string> SafeSubdirs(string dir)
    {
        try { return Directory.EnumerateDirectories(dir); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>엔진명을 안전한 폴더명으로(경로 금지문자 치환).</summary>
    private static string SafeName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "engine";
        foreach (var ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch, '_');
        return s.Trim();
    }
}
