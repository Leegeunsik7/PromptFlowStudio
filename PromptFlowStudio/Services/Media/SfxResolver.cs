using System;
using System.IO;
using System.Linq;
using PromptFlowStudio.Services.Account;   // LicenseService.DataAreaRoot()

namespace PromptFlowStudio.Services.Media;

/// <summary>
/// [PromptFlowStudio] 스마트 SFX(컷 전환 효과음) 오디오 파일 해석 — 순수 BCL(외부 NuGet 0). <see cref="FontResolver"/> 와 동일 패턴.
///   ▸ 폴백 체인: ①데이터폴더 <c>%LocalAppData%\PromptFlowStudio\Sfx\</c>(사용자/시딩) → ②앱폴더 <c>Sfx\</c>(번들) → ③null(SFX 비활성).
///   ▸ 시딩: 첫 호출 시 데이터 Sfx 가 비어 있으면 앱폴더 Sfx 의 오디오를 복사(번들 효과음 독립성 · 사용자 교체 가능).
///   ▸ 선호도: 파일명에 whoosh/transition/swoosh/swipe/pop 이 든 파일을 우선(숏츠 전환음 관례) — 없으면 사전순 첫 파일.
///   ▸ Graceful: 폴더 부재/권한/IO 예외를 모두 흡수하고 null 을 돌려줘 병합 파이프라인이 SFX 없이 계속되게 한다.
/// </summary>
public static class SfxResolver
{
    private static readonly object _seedGate = new();
    private static bool _seeded;

    private static readonly string[] SfxExts = { ".mp3", ".wav", ".m4a", ".ogg", ".aac", ".flac" };

    // 숏츠 전환음으로 흔히 쓰는 키워드(우선순위 순).
    private static readonly string[] PreferKeywords = { "whoosh", "transition", "swoosh", "swipe", "pop", "swish" };

    private static string DataSfxDir => Path.Combine(LicenseService.DataAreaRoot(), "Sfx");
    private static string AppSfxDir  => Path.Combine(AppContext.BaseDirectory, "Sfx");

    /// <summary>컷 전환용 효과음 경로. 없으면 null(SFX 믹싱 생략). 데이터폴더 → 앱번들 순으로 탐색하며 선호 키워드 우선.</summary>
    public static string? FindTransitionSfx()
    {
        try
        {
            SeedIfNeeded();

            // ① 데이터 폴더(사용자/시딩).
            var fromData = PickFrom(DataSfxDir);
            if (fromData is not null) return fromData;

            // ② 앱 번들 폴더.
            var fromApp = PickFrom(AppSfxDir);
            if (fromApp is not null) return fromApp;
        }
        catch { /* 무시 → null(SFX 비활성) */ }
        return null;
    }

    /// <summary>
    /// 지정 타입(whoosh/pop/swoosh/ding/transition 등) 효과음을 우선 탐색 — 파일명에 타입 키워드가 든 파일을 찾고,
    ///   없으면 <see cref="FindTransitionSfx"/>(기본 전환음)로 폴백. 키네틱 정밀 SFX 트리거용. 파일 전무면 null(Graceful).
    /// </summary>
    public static string? FindByType(string? sfxType)
    {
        var kw = (sfxType ?? "").Trim().ToLowerInvariant();
        if (kw.Length == 0) return FindTransitionSfx();
        try
        {
            SeedIfNeeded();
            var hit = PickByKeyword(DataSfxDir, kw) ?? PickByKeyword(AppSfxDir, kw);
            return hit ?? FindTransitionSfx();
        }
        catch { return FindTransitionSfx(); }
    }

    private static string? PickByKeyword(string dir, string kw)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            return EnumerateSfx(dir).FirstOrDefault(f =>
                Path.GetFileNameWithoutExtension(f).ToLowerInvariant().Contains(kw));
        }
        catch { return null; }
    }

    /// <summary>데이터 Sfx 가 비어 있으면 앱 번들 Sfx 의 오디오를 복사(1회). 실패는 무시(Graceful).</summary>
    public static void SeedIfNeeded()
    {
        if (_seeded) return;
        lock (_seedGate)
        {
            if (_seeded) return;
            try
            {
                Directory.CreateDirectory(DataSfxDir);
                var hasData = EnumerateSfx(DataSfxDir).Any();
                if (!hasData && Directory.Exists(AppSfxDir))
                {
                    foreach (var f in EnumerateSfx(AppSfxDir))
                    {
                        try { File.Copy(f, Path.Combine(DataSfxDir, Path.GetFileName(f)), overwrite: false); }
                        catch { /* 개별 복사 실패 무시 */ }
                    }
                }
            }
            catch { /* 시딩 실패해도 앱번들/null 로 진행 */ }
            _seeded = true;
        }
    }

    // ── 내부 ──
    private static string? PickFrom(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            var files = EnumerateSfx(dir).ToList();
            if (files.Count == 0) return null;

            // 선호 키워드(whoosh 등)가 파일명에 든 파일 우선.
            foreach (var kw in PreferKeywords)
            {
                var hit = files.FirstOrDefault(f =>
                    Path.GetFileNameWithoutExtension(f).ToLowerInvariant().Contains(kw));
                if (hit is not null) return hit;
            }
            return files[0];
        }
        catch { return null; }
    }

    private static System.Collections.Generic.IEnumerable<string> EnumerateSfx(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir)
                .Where(f => SfxExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        }
        catch { return Array.Empty<string>(); }
    }
}
