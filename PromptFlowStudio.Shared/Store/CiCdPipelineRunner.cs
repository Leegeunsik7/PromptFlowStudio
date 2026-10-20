using System;
using System.Globalization;
using System.Linq;

namespace PromptFlowStudio.Shared.Store;

/// <summary>
/// [PromptFlow Studio v4.4.0] AAB/IPA 실파일 검수 CI 게이트키퍼(Task 3 · 순수 BCL).
///   ▸ 파이프라인에서 AAB/IPA 를 파싱해 16KB 정렬·서명·Privacy 결함 발견 시 <b>Exit Code 1</b>로 파이프라인 중지.
///   ▸ 콘솔 <c>Main</c> 은 이 <see cref="Run"/> 를 그대로 반환한다(RCL 에 두어 단위 테스트 가능).
/// </summary>
public static class CiCdPipelineRunner
{
    /// <summary>
    /// 인자: <c>--aab &lt;path&gt;</c> <c>--ipa &lt;path&gt;</c> <c>--min-score &lt;N&gt;</c>(기본 90).
    ///   반환: 0=통과 · 1=결함(거절 위험/점수 미달) · 2=사용법 오류.
    /// </summary>
    public static int Run(string[] args, Action<string>? log = null)
    {
        log ??= Console.WriteLine;
        var aab = ArgVal(args, "--aab");
        var ipa = ArgVal(args, "--ipa");
        var minScore = int.TryParse(ArgVal(args, "--min-score"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : 90;

        if (aab is null && ipa is null)
        {
            log("[CI] 사용법: CiGate --aab <path.aab> --ipa <path.ipa> [--min-score 90]");
            return 2;
        }

        var totalFail = 0;
        var minSeen = 100;

        if (aab is not null)
        {
            var r = AndroidStoreCheckEngine.InspectAab(aab);
            PrintReport(log, "Google Play (AAB)", r);
            totalFail += r.FailCount; minSeen = Math.Min(minSeen, r.Score);
        }
        if (ipa is not null)
        {
            var r = IosStoreCheckEngine.InspectIpa(ipa, DateTimeOffset.Now);
            PrintReport(log, "App Store (IPA)", r);
            totalFail += r.FailCount; minSeen = Math.Min(minSeen, r.Score);
        }

        var ok = totalFail == 0 && minSeen >= minScore;
        log(ok
            ? $"[CI] ✅ PASS — 결함 0건 · 최저 점수 {minSeen} (기준 {minScore})"
            : $"[CI] ❌ FAIL — 결함 {totalFail}건 · 최저 점수 {minSeen} (기준 {minScore}) → 파이프라인 중지");
        return ok ? 0 : 1;
    }

    private static void PrintReport(Action<string> log, string label, StoreReport r)
    {
        log($"[CI] ── {label}: {r.Summary} (점수 {r.Score}) ──");
        foreach (var c in r.Checks)
            log($"[CI]   [{c.Severity.ToString().ToUpperInvariant()}] {c.Title} — {c.Detail}");
    }

    private static string? ArgVal(string[] args, string key)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}
