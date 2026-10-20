using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Shared.Deploy;

/// <summary>패키징 타깃.</summary>
public enum BuildTarget { WinExe, AndroidApk, IosIpa }

/// <summary>빌드 스텝 — 타깃/실행기(pwsh·bash)/스크립트/인자/산출물명.</summary>
public sealed record BuildStep(BuildTarget Target, string Runner, string Script, IReadOnlyList<string> Args, string OutputName);

/// <summary>패키징 계획 — 스텝 목록 + 출력 폴더 + 요약.</summary>
public sealed record DeploymentPlan(IReadOnlyList<BuildStep> Steps, string OutputDir, string Summary);

/// <summary>빌드 실행 결과.</summary>
public sealed record BuildRunResult(BuildTarget Target, bool Ok, string OutputPath, string Message);

/// <summary>패키징 실행 시임 — 데스크톱은 ps1/sh 체이닝, 그 외는 Graceful.</summary>
public interface IBuildLauncher
{
    bool IsSupported { get; }
    Task<BuildRunResult> RunAsync(BuildStep step, string outputDir, Action<string>? log = null, CancellationToken ct = default);
}

/// <summary>기본 런처(비데스크톱 · Graceful).</summary>
public sealed class DefaultBuildLauncher : IBuildLauncher
{
    public bool IsSupported => false;
    public Task<BuildRunResult> RunAsync(BuildStep step, string outputDir, Action<string>? log = null, CancellationToken ct = default)
        => Task.FromResult(new BuildRunResult(step.Target, false, "", "이 호스트에서는 패키징을 실행할 수 없습니다(데스크톱 전용)."));
}

/// <summary>
/// [PromptFlow Studio v4.4.0] 멀티플랫폼 원클릭 패키징 계획기(Task 4 · 순수 BCL).
///   ▸ [💻 Win EXE][🤖 Android APK][🍎 iOS IPA] 선택 타깃 → PowerShell(ps1)/Bash(sh) 스텝 체인 + <c>outputs/</c> 산출물명 산출.
/// </summary>
public static class DeploymentBuildPlanner
{
    /// <summary>기본 출력 폴더.</summary>
    public const string OutputDir = "outputs";
    private const string Ps1 = "scripts/package-all.ps1";
    private const string Sh = "scripts/package-all.sh";

    /// <summary>타깃 라벨/이모지.</summary>
    public static string Label(BuildTarget t) => t switch { BuildTarget.WinExe => "Win EXE", BuildTarget.AndroidApk => "Android APK", _ => "iOS IPA" };
    public static string Emoji(BuildTarget t) => t switch { BuildTarget.WinExe => "💻", BuildTarget.AndroidApk => "🤖", _ => "🍎" };

    /// <summary>산출물 파일명.</summary>
    public static string OutputName(BuildTarget t, string version) => t switch
    {
        BuildTarget.WinExe => $"PromptFlowStudio-v{version}-win-x64-setup.exe",
        BuildTarget.AndroidApk => $"PromptFlowStudio-v{version}-arm64-v8a.apk",
        _ => $"PromptFlowStudio-v{version}-ios.ipa",
    };

    /// <summary>선택 타깃 → 패키징 계획(Win/Android=pwsh · iOS=bash).</summary>
    public static DeploymentPlan Plan(IReadOnlyList<BuildTarget> targets, string version = "4.4.0", string? outputDir = null)
    {
        var dir = string.IsNullOrWhiteSpace(outputDir) ? OutputDir : outputDir!;
        var steps = new List<BuildStep>();
        foreach (var t in targets.Distinct())
        {
            var (runner, script) = t == BuildTarget.IosIpa ? ("bash", Sh) : ("pwsh", Ps1);
            var flag = t switch { BuildTarget.WinExe => "win", BuildTarget.AndroidApk => "android", _ => "ios" };
            var args = t == BuildTarget.IosIpa
                ? new[] { script, "--target", flag, "--version", version, "--out", dir }
                : new[] { script, "-Target", flag, "-Version", version, "-Out", dir };
            steps.Add(new BuildStep(t, runner, script, args, OutputName(t, version)));
        }
        var summary = steps.Count == 0 ? "선택된 타깃이 없습니다." : $"{steps.Count}개 타깃 패키징 → {dir}/ (" + string.Join(" · ", steps.Select(s => Emoji(s.Target) + Label(s.Target))) + ")";
        return new DeploymentPlan(steps, dir, summary);
    }
}
