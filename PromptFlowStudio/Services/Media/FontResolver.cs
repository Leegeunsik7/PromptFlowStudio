using System;
using System.IO;
using System.Linq;
using PromptFlowStudio.Services.Account;   // LicenseService.DataAreaRoot()

namespace PromptFlowStudio.Services.Media;

/// <summary>
/// [PromptFlowStudio] drawtext 번인 폰트 해석 — 순수 BCL(외부 NuGet 0). VideoMergeService/ShortsCoverService 공유.
///   ▸ 폴백 체인: ①데이터폴더 <c>%LocalAppData%\PromptFlowStudio\Fonts\</c>(사용자/시딩) → ②앱폴더 <c>Fonts\</c>(번들) →
///     ③시스템 한글 폰트(malgun 등) → ④null(drawtext 기본 폰트=라틴). 오프라인/한글폰트 부재 환경 대비.
///   ▸ 시딩: 첫 호출 시 데이터 Fonts 가 비어 있으면 앱폴더 Fonts 의 폰트를 복사(번들 폰트 독립성).
///   ▸ 경로 이스케이프: drawtext 는 인자 내 콜론을 옵션 구분자로 보므로 절대경로의 드라이브 콜론을 <c>C\:/…</c> 로 이스케이프(문자 치환 → 로캘 무관).
/// </summary>
public static class FontResolver
{
    private static readonly object _seedGate = new();
    private static bool _seeded;

    private static readonly string[] FontExts = { ".ttf", ".ttc", ".otf" };

    private static string DataFontsDir => Path.Combine(LicenseService.DataAreaRoot(), "Fonts");
    private static string AppFontsDir  => Path.Combine(AppContext.BaseDirectory, "Fonts");

    // 시스템 한글 폰트 후보(일반/굵게).
    private static readonly string[] SystemRegular =
    {
        @"C:\Windows\Fonts\malgun.ttf", @"C:\Windows\Fonts\NanumGothic.ttf",
        @"C:\Windows\Fonts\gulim.ttc", @"C:\Windows\Fonts\batang.ttc",
    };
    private static readonly string[] SystemBold =
    {
        @"C:\Windows\Fonts\malgunbd.ttf", @"C:\Windows\Fonts\NanumGothicBold.ttf",
        @"C:\Windows\Fonts\malgun.ttf", @"C:\Windows\Fonts\NanumGothic.ttf",
        @"C:\Windows\Fonts\gulim.ttc", @"C:\Windows\Fonts\batang.ttc",
    };

    /// <summary>번인용 한글 폰트 경로. 없으면 null(drawtext 기본 폰트). <paramref name="bold"/>=굵은 폰트 우선(커버용).</summary>
    public static string? FindKoreanFont(bool bold = false)
    {
        try
        {
            SeedIfNeeded();

            // ① 데이터 폴더(사용자/시딩) — 굵게 우선이면 이름에 bd/bold 포함 파일 먼저.
            var fromData = PickFrom(DataFontsDir, bold);
            if (fromData is not null) return fromData;

            // ② 앱 번들 폴더.
            var fromApp = PickFrom(AppFontsDir, bold);
            if (fromApp is not null) return fromApp;

            // ③ 시스템 한글 폰트.
            foreach (var c in (bold ? SystemBold : SystemRegular))
                if (SafeExists(c)) return c;
        }
        catch { /* 무시 → null */ }
        return null;
    }

    /// <summary>데이터 Fonts 가 비어 있으면 앱 번들 Fonts 의 폰트를 복사(1회). 실패는 무시(Graceful).</summary>
    public static void SeedIfNeeded()
    {
        if (_seeded) return;
        lock (_seedGate)
        {
            if (_seeded) return;
            try
            {
                Directory.CreateDirectory(DataFontsDir);
                var hasData = EnumerateFonts(DataFontsDir).Any();
                if (!hasData && Directory.Exists(AppFontsDir))
                {
                    foreach (var f in EnumerateFonts(AppFontsDir))
                    {
                        try { File.Copy(f, Path.Combine(DataFontsDir, Path.GetFileName(f)), overwrite: false); }
                        catch { /* 개별 복사 실패 무시 */ }
                    }
                }
            }
            catch { /* 시딩 실패해도 시스템/기본 폰트로 진행 */ }
            _seeded = true;
        }
    }

    /// <summary>drawtext 의 fontfile/textfile 절대경로 이스케이프(역슬래시→'/', 콜론→'\:'). 문자 치환이라 InvariantCulture 무관.</summary>
    public static string EscapeDrawPath(string p) => (p ?? "").Replace('\\', '/').Replace(":", "\\:");

    // ── 내부 ──
    private static string? PickFrom(string dir, bool bold)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            var fonts = EnumerateFonts(dir).ToList();
            if (fonts.Count == 0) return null;
            if (bold)
            {
                var b = fonts.FirstOrDefault(f =>
                {
                    var n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                    return n.Contains("bold") || n.EndsWith("bd") || n.Contains("-b");
                });
                if (b is not null) return b;
            }
            return fonts[0];
        }
        catch { return null; }
    }

    private static System.Collections.Generic.IEnumerable<string> EnumerateFonts(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir)
                .Where(f => FontExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        }
        catch { return Array.Empty<string>(); }
    }

    private static bool SafeExists(string p)
    { try { return File.Exists(p); } catch { return false; } }
}
