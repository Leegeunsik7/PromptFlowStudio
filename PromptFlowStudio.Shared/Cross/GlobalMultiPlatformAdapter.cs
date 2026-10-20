using System;
using System.Collections.Generic;

namespace PromptFlowStudio.Shared.Cross;

/// <summary>
/// [PromptFlow Studio v4.2] 글로벌 멀티플랫폼 어댑터(Task 3 · 순수 BCL · thread-safe).
///   ▸ OS 언어(ko/en/de/zh/ja) 자동 감지 → UI 문자열 + AI 프롬프트 톤 즉시 전환(관찰 가능).
///   ▸ 앱 미설치 글로벌 바이어용 <b>WebAssembly(PWA) 웹 폴백</b> 계획 제공.
/// </summary>
public sealed class GlobalMultiPlatformAdapter
{
    private readonly object _lock = new();
    private AppLanguage _current = AppLanguage.Ko;

    /// <summary>현재 언어(thread-safe).</summary>
    public AppLanguage Current { get { lock (_lock) return _current; } }
    /// <summary>언어 변경 통지.</summary>
    public event Action<AppLanguage>? OnChange;

    /// <summary>지원 언어 메타(언어/코드/자국어명/국기).</summary>
    public static readonly IReadOnlyList<(AppLanguage Lang, string Code, string Native, string Flag)> Languages = new[]
    {
        (AppLanguage.Ko, "ko", "한국어", "🇰🇷"),
        (AppLanguage.En, "en", "English", "🇺🇸"),
        (AppLanguage.De, "de", "Deutsch", "🇩🇪"),
        (AppLanguage.Zh, "zh", "中文", "🇨🇳"),
        (AppLanguage.Ja, "ja", "日本語", "🇯🇵"),
    };

    /// <summary>OS 컬처 이름("ko-KR" 등) → 앱 언어(2글자 접두 · 미지원 시 English 폴백).</summary>
    public static AppLanguage DetectLanguage(string? cultureName)
    {
        var c = (cultureName ?? "").Trim().ToLowerInvariant();
        if (c.Length >= 2)
        {
            var p = c.Substring(0, 2);
            foreach (var l in Languages) if (l.Code == p) return l.Lang;
        }
        return AppLanguage.En;
    }

    /// <summary>OS 컬처로 초기 언어를 설정한다.</summary>
    public void InitFromCulture(string? cultureName) => SetLanguage(DetectLanguage(cultureName));

    /// <summary>언어를 전환한다(변경 시 통지).</summary>
    public void SetLanguage(AppLanguage lang)
    {
        bool changed;
        lock (_lock) { changed = _current != lang; _current = lang; }
        if (changed) OnChange?.Invoke(lang);
    }

    /// <summary>현재 언어로 UI 문자열 조회.</summary>
    public string T(string key) => LocalizationCatalog.Get(Current, key);

    /// <summary>언어별 AI 프롬프트 톤(나레이션/카피 생성 지침).</summary>
    public static string PromptTone(AppLanguage lang) => lang switch
    {
        AppLanguage.Ko => "따뜻하고 감성적인 한국어 존댓말 톤, 공감 어휘 강조",
        AppLanguage.En => "warm, upbeat English marketing tone, concise and friendly",
        AppLanguage.De => "sachlich-präziser deutscher Ton, vertrauenswürdig und technisch",
        AppLanguage.Zh => "热情正式的中文语气，强调品质与信赖",
        _ => "丁寧で心温まる日本語トーン、共感と品質を重視",
    };

    /// <summary>PWA(WebAssembly) 웹 폴백 계획 — 앱 미설치 시 브라우저 응답 파이프라인.</summary>
    public static PwaFallbackPlan BuildPwaFallbackPlan(bool appInstalled)
        => new(!appInstalled, "wwwroot/manifest.webmanifest", "wwwroot/service-worker.js", "index.html",
            appInstalled ? "네이티브 앱 사용 중 — 폴백 비활성" : "앱 미설치 바이어: 브라우저에서 WASM PWA 로 즉시 응답(오프라인 캐시)");
}

/// <summary>i18n 문자열 카탈로그(ko/en/de/zh/ja · 미상 키/언어는 English → 키 폴백 · thread-safe 읽기 전용).</summary>
public static class LocalizationCatalog
{
    // key → [ko, en, de, zh, ja]
    private static readonly Dictionary<string, string[]> Table = new(StringComparer.Ordinal)
    {
        ["app.title"] = new[] { "프롬프트플로우 스튜디오", "PromptFlow Studio", "PromptFlow Studio", "提示流工作室", "プロンプトフロースタジオ" },
        ["deck.title"] = new[] { "크로스플랫폼 제어반", "Cross-Platform Console", "Plattformübergreifend", "跨平台控制台", "クロスプラットフォーム" },
        ["deck.crossrender"] = new[] { "iOS/Android 크로스 렌더링 시연", "Cross-render iOS/Android", "iOS/Android Cross-Rendering", "iOS/Android 交叉渲染演示", "iOS/Android クロスレンダリング" },
        ["qa.title"] = new[] { "시뮬레이터 QA 통과율", "Simulator QA Pass Rate", "Simulator-QA-Quote", "模拟器 QA 通过率", "シミュレータ QA 合格率" },
        ["qa.report"] = new[] { "시뮬레이션 리포트", "Simulation Report", "Simulationsbericht", "模拟报告", "シミュレーションレポート" },
        ["lang.label"] = new[] { "언어", "Language", "Sprache", "语言", "言語" },
        ["device.label"] = new[] { "기기 프리뷰", "Device Preview", "Gerätevorschau", "设备预览", "デバイスプレビュー" },
        ["pwa.label"] = new[] { "PWA 웹 폴백", "PWA Web Fallback", "PWA-Web-Fallback", "PWA 网页回退", "PWA ウェブフォールバック" },
    };

    private static int Idx(AppLanguage l) => (int)l;   // Ko=0..Ja=4

    /// <summary>문자열 조회(언어 없으면 English, 키 없으면 키 자체 반환).</summary>
    public static string Get(AppLanguage lang, string key)
    {
        if (Table.TryGetValue(key, out var vals))
        {
            var i = Idx(lang);
            if (i >= 0 && i < vals.Length && !string.IsNullOrEmpty(vals[i])) return vals[i];
            return vals.Length > 1 ? vals[1] : key;   // English 폴백
        }
        return key;
    }

    /// <summary>카탈로그 키 수.</summary>
    public static int KeyCount => Table.Count;
}
