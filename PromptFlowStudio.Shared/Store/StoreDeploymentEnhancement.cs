using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PromptFlowStudio.Shared.Cross;   // AppLanguage (v4.2 재사용)

namespace PromptFlowStudio.Shared.Store;

/// <summary>
/// [PromptFlow Studio v4.3.0] 자율 스토어 메타데이터 고도화(Task 5 · 순수 BCL).
///   ▸ 5개국(ko/en/de/zh/ja) 앱 스토어 등록 정보(키워드/설명/업데이트 내역) 자동 생성.
///   ▸ 개인정보 처리방침 HTML/Markdown 발급 + 기기 규격별 스크린샷 번들 계획/4K 스케일 인자.
/// </summary>
public static class StoreListingGenerator
{
    /// <summary>언어별 등록 정보 생성.</summary>
    public static StoreListing Generate(AppLanguage lang, string appName, string version)
    {
        return lang switch
        {
            AppLanguage.Ko => new("ko", appName, "AI 영상·현장 제어 올인원", "AI영상,프롬프트,숏츠,PLC,크로스플랫폼",
                $"{appName}은 프롬프트로 AI 영상을 대량 생성하고 현장 장비를 제어하는 올인원 스튜디오입니다. 아기·기업·자동차 테마 157종, 3D CAD, 텔레메트리 HUD를 지원합니다.",
                $"v{version}: 양대 스토어 배포 최적화 · 5개국어 · 성능 개선."),
            AppLanguage.En => new("en", appName, "All-in-one AI video & field studio", "AI video,prompt,shorts,PLC,cross-platform",
                $"{appName} mass-produces AI videos from prompts and controls field devices — 157 themes, 3D CAD, telemetry HUD, all in one studio.",
                $"v{version}: Store-ready builds, 5 languages, performance improvements."),
            AppLanguage.De => new("de", appName, "All-in-One KI-Video- & Field-Studio", "KI-Video,Prompt,Shorts,PLC,plattformübergreifend",
                $"{appName} erstellt KI-Videos aus Prompts und steuert Feldgeräte — 157 Themen, 3D-CAD, Telemetrie-HUD in einem Studio.",
                $"v{version}: Store-fertige Builds, 5 Sprachen, Leistungsverbesserungen."),
            AppLanguage.Zh => new("zh", appName, "一体化 AI 视频与现场控制", "AI视频,提示词,短视频,PLC,跨平台",
                $"{appName} 通过提示词批量生成 AI 视频并控制现场设备——157 种主题、3D CAD、遥测 HUD，一站式工作室。",
                $"v{version}：双商店发布优化，5 种语言，性能提升。"),
            _ => new("ja", appName, "オールインワン AI 動画・現場制御", "AI動画,プロンプト,ショート,PLC,クロスプラットフォーム",
                $"{appName} はプロンプトから AI 動画を量産し現場機器を制御するオールインワンスタジオ。157 テーマ、3D CAD、テレメトリ HUD 対応。",
                $"v{version}：ストア配信最適化、5 言語、パフォーマンス改善。"),
        };
    }

    /// <summary>전 언어 등록 정보.</summary>
    public static IReadOnlyList<StoreListing> GenerateAll(string appName, string version)
        => new[] { AppLanguage.Ko, AppLanguage.En, AppLanguage.De, AppLanguage.Zh, AppLanguage.Ja }
            .Select(l => Generate(l, appName, version)).ToList();
}

/// <summary>개인정보 처리방침 발급기(HTML/Markdown).</summary>
public static class PrivacyPolicyIssuer
{
    private static string Title(AppLanguage l) => l switch
    {
        AppLanguage.Ko => "개인정보 처리방침", AppLanguage.En => "Privacy Policy", AppLanguage.De => "Datenschutzrichtlinie",
        AppLanguage.Zh => "隐私政策", _ => "プライバシーポリシー",
    };
    private static string Body(AppLanguage l, string company) => l switch
    {
        AppLanguage.Ko => $"{company}는 앱 기능 제공에 필요한 최소한의 정보만 처리하며, 영상/오디오는 기기 내에서 처리되고 사용자 동의 없이 외부로 전송하지 않습니다.",
        AppLanguage.En => $"{company} processes only the minimum data required for app features. Media is processed on-device and never transmitted without consent.",
        AppLanguage.De => $"{company} verarbeitet nur die für die App-Funktionen erforderlichen Mindestdaten. Medien werden auf dem Gerät verarbeitet.",
        AppLanguage.Zh => $"{company} 仅处理应用功能所需的最少数据，媒体在设备端处理，未经同意不外传。",
        _ => $"{company} はアプリ機能に必要な最小限のデータのみ処理し、メディアは端末内で処理します。",
    };

    /// <summary>HTML 문서(인라인 스타일 · UTF-8).</summary>
    public static string BuildHtml(AppLanguage lang, string company, string contact)
        => $"<!doctype html><html lang=\"{lang.ToString().ToLowerInvariant()}\"><head><meta charset=\"utf-8\"><title>{Title(lang)}</title></head>" +
           $"<body style=\"font-family:system-ui,sans-serif;max-width:720px;margin:40px auto;line-height:1.7;color:#1a1a1a\">" +
           $"<h1>{Title(lang)}</h1><p>{Body(lang, company)}</p><p>Contact: {contact}</p>" +
           $"<p style=\"color:#888;font-size:13px\">Updated: {DateTime.Now:yyyy-MM-dd}</p></body></html>";

    /// <summary>Markdown 문서.</summary>
    public static string BuildMarkdown(AppLanguage lang, string company, string contact)
        => $"# {Title(lang)}\n\n{Body(lang, company)}\n\n- Contact: {contact}\n- Updated: {DateTime.Now:yyyy-MM-dd}\n";
}

/// <summary>기기 규격별 스크린샷 번들 계획기.</summary>
public static class ScreenshotBundlePlanner
{
    /// <summary>스토어 필수/권장 스크린샷 규격(4K급 해상도).</summary>
    public static readonly IReadOnlyList<DeviceScreenshotProfile> Profiles = new[]
    {
        new DeviceScreenshotProfile("iphone67", "6.7\" iPhone", StorePlatform.iOS, 1290, 2796, true),
        new DeviceScreenshotProfile("ipad129", "12.9\" iPad Pro", StorePlatform.iOS, 2048, 2732, true),
        new DeviceScreenshotProfile("android_phone", "Android Phone", StorePlatform.Android, 1080, 2400, true),
        new DeviceScreenshotProfile("android_tablet", "Android 10\" Tablet", StorePlatform.Android, 1600, 2560, false),
    };

    /// <summary>기기 × 로케일 스크린샷 촬영 계획(파일명 규격화).</summary>
    public static IReadOnlyList<ScreenshotShot> PlanBundle(IReadOnlyList<AppLanguage> langs)
    {
        var shots = new List<ScreenshotShot>();
        foreach (var d in Profiles)
            foreach (var l in langs)
            {
                var loc = l.ToString().ToLowerInvariant();
                shots.Add(new ScreenshotShot(d.Key, loc, d.W, d.H, $"{loc}/{d.Key}_{d.W}x{d.H}.png"));
            }
        return shots;
    }

    /// <summary>규격 해상도로 크롭·스케일하는 ffmpeg 인자(4K 소스 → 스토어 규격).</summary>
    public static IReadOnlyList<string> BuildScaleArgs(string src, string dst, int w, int h) => new[]
    {
        "-y", "-i", src,
        "-vf", string.Format(CultureInfo.InvariantCulture,
            "scale={0}:{1}:force_original_aspect_ratio=increase,crop={0}:{1}", w, h),
        "-frames:v", "1", dst,
    };
}
