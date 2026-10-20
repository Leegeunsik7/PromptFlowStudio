using System;
using System.Text;
using PromptFlowStudio.Services.Ui;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>v16.8.0 트랜스파일 대상 비디오 AI 엔진.</summary>
public enum VideoEngineTarget { Wan21, LumaDreamMachine, RunwayGen3 }

/// <summary>v16.8.0 트랜스파일 결과 — 엔진 · 프롬프트 · 네거티브(미지원 엔진은 null) · 길이 상한 절단/네거티브 폐기 여부.</summary>
public sealed record TranspiledPrompt(VideoEngineTarget Engine, string Prompt, string? Negative, bool Truncated, bool NegativeDropped);

/// <summary>
/// [PromptFlowStudio] v16.8.0 🔀 멀티 엔진 프롬프트 트랜스파일러 — 테마 프롬프트를 엔진별 문법/길이 제약에 맞춰 변환(순수 BCL).
///   ▸ <b>Wan 2.1</b>: 쉼표 태그 나열 · 카메라 토큰(<see cref="WanCameraStyleStudio.Cameras"/> 스니펫) + 조명 구문 꼬리 결합 · 네거티브 지원.
///   ▸ <b>Luma Dream Machine</b>: 자연어 문장형("Camera …. Scene …. Lighting …") · 네거티브 미지원 → 폐기 플래그.
///   ▸ <b>Runway Gen-3</b>: 권장 구조 "[카메라 무빙]: [장면]. [디테일]." · 네거티브 미지원 → 폐기 플래그.
///   ▸ 입력에 이미 들어 있는 Wan 카메라 토큰·품질 꼬리(4K/8k 태그)는 제거 후 엔진 문법으로 재기술(중복·충돌 방지).
///   ▸ 길이 상한은 각 엔진 공개 한도보다 <b>보수적인 내부 상수</b> — 초과 시 쉼표/공백 경계에서 절단.
/// </summary>
public static class MultiEnginePromptTranspiler
{
    /// <summary>엔진별 프롬프트 글자 상한(보수적 내부값).</summary>
    public const int WanMaxChars = 800, LumaMaxChars = 1000, RunwayMaxChars = 500, WanNegativeMaxChars = 500;

    private static readonly string[] QualityTails =
    {
        ", 4K UHD, photorealistic, hyper-detailed", ", ultra-detailed, sharp focus, 8k", "4K UHD", "hyper-detailed", "8k",
    };

    /// <summary>엔진 표시 라벨.</summary>
    public static string Label(VideoEngineTarget e) => e switch
    {
        VideoEngineTarget.Wan21 => "Wan 2.1",
        VideoEngineTarget.LumaDreamMachine => "Luma Dream Machine",
        _ => "Runway Gen-3",
    };

    /// <summary>엔진별 프롬프트 글자 상한.</summary>
    public static int MaxChars(VideoEngineTarget e) => e switch
    {
        VideoEngineTarget.Wan21 => WanMaxChars,
        VideoEngineTarget.LumaDreamMachine => LumaMaxChars,
        _ => RunwayMaxChars,
    };

    /// <summary>테마 컷 → 엔진별 프롬프트(메타가 없으면 카메라/조명 구문 생략).</summary>
    public static TranspiledPrompt Transpile(string? scene, ThemeCinematics? cine, VideoEngineTarget engine, string? negative = null)
    {
        var core = Clean(scene);
        string prompt;
        switch (engine)
        {
            case VideoEngineTarget.Wan21:
            {
                var sb = new StringBuilder(core.Length + 96).Append(core);
                if (cine is not null)
                {
                    AppendTag(sb, cine.CameraChip.Snippet);
                    AppendTag(sb, cine.LightingSnippet);
                    AppendTag(sb, WanMotionTag(cine.MotionIntensity));
                }
                prompt = sb.ToString();
                var neg = (negative ?? cine?.Negative ?? "").Trim();
                var (p, t) = Fit(prompt, WanMaxChars);
                var (n, _) = Fit(neg, WanNegativeMaxChars);
                return new(engine, p, n.Length == 0 ? null : n, t, false);
            }
            case VideoEngineTarget.LumaDreamMachine:
            {
                var sb = new StringBuilder(core.Length + 120);
                if (cine is not null) sb.Append("Camera ").Append(LumaCamera(cine.Camera)).Append('.');
                if (core.Length > 0) Space(sb).Append(Sentence(core));
                if (cine is not null)
                    Space(sb).Append(Capitalize(cine.LightingSnippet)).Append(", ").Append(MotionWords(cine.MotionIntensity)).Append('.');
                prompt = sb.ToString();
                break;
            }
            default:
            {
                var sb = new StringBuilder(core.Length + 120);
                if (cine is not null) sb.Append(RunwayCamera(cine.Camera)).Append(core.Length > 0 ? ": " : ".");   // v16.13.0: 빈 장면이면 "샷:" 고아 구두점 대신 마침표
                sb.Append(Sentence(core));
                if (cine is not null)
                    Space(sb).Append(Capitalize(cine.LightingSnippet)).Append(". ").Append(Capitalize(MotionWords(cine.MotionIntensity))).Append('.');
                prompt = sb.ToString();
                break;
            }
        }
        var (fit, truncated) = Fit(prompt, MaxChars(engine));
        var dropped = !string.IsNullOrWhiteSpace(negative ?? cine?.Negative);
        return new(engine, fit, null, truncated, dropped);
    }

    /// <summary>백과사전 테마의 n번째 컷 → 엔진별 프롬프트(테마 메타 자동 적용).</summary>
    public static TranspiledPrompt TranspileTheme(BabyThemeDef theme, int cutIndex, VideoEngineTarget engine)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var shot = theme.Beats.Count == 0 ? "" : theme.Beats[Math.Clamp(cutIndex, 0, theme.Beats.Count - 1)].Shot;
        var cine = theme.Cinematics;
        return Transpile(shot, cine, engine, cine.Negative ?? BabyEverydayThemeDatabase.NegativeFor(theme.CategoryKey));
    }

    // ── 엔진 문법 ──

    private static string LumaCamera(CameraMotion c) => c switch
    {
        CameraMotion.Orbit => "orbits slowly around the subject",
        CameraMotion.FpvDrone => "flies forward like an FPV drone",
        CameraMotion.CraneUp => "cranes up to reveal the scene",
        CameraMotion.DollyZoom => "performs a dolly zoom on the subject",
        CameraMotion.SmoothPan => "pans smoothly across the scene",
        _ => "tilts and rolls dynamically",
    };

    private static string RunwayCamera(CameraMotion c) => c switch
    {
        CameraMotion.Orbit => "Orbiting shot",
        CameraMotion.FpvDrone => "FPV drone shot",
        CameraMotion.CraneUp => "Crane up shot",
        CameraMotion.DollyZoom => "Dolly zoom shot",
        CameraMotion.SmoothPan => "Smooth tracking pan",
        _ => "Dutch tilt roll shot",
    };

    private static string WanMotionTag(int intensity) => intensity switch
    {
        <= 2 => "gentle subtle motion",
        3 => "natural fluid motion",
        _ => "dynamic energetic motion",
    };

    private static string MotionWords(int intensity) => intensity switch
    {
        <= 2 => "gentle subtle movement",
        3 => "natural fluid movement",
        _ => "dynamic energetic movement",
    };

    // ── 텍스트 유틸 ──

    /// <summary>Wan 카메라 토큰·품질 꼬리 제거 + 공백/쉼표 정리.</summary>
    internal static string Clean(string? scene)
    {
        var s = (scene ?? "").Trim();
        if (s.Length == 0) return s;
        foreach (var chip in WanCameraStyleStudio.Cameras) s = s.Replace(chip.Snippet, "", StringComparison.Ordinal);
        foreach (var tail in QualityTails) s = s.Replace(tail, "", StringComparison.OrdinalIgnoreCase);
        var sb = new StringBuilder(s.Length);
        var lastComma = true;
        foreach (var raw in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Length == 0) continue;
            if (!lastComma) sb.Append(", ");
            sb.Append(raw);
            lastComma = false;
        }
        return sb.ToString();
    }

    private static void AppendTag(StringBuilder sb, string tag)
    {
        if (tag.Length == 0) return;
        if (sb.Length > 0) sb.Append(", ");
        sb.Append(tag);
    }

    /// <summary>v16.13.0 문장 경계 공백 — 앞 내용이 있을 때만(빈 장면에서 선두/이중 공백 방지).</summary>
    private static StringBuilder Space(StringBuilder sb) => sb.Length > 0 ? sb.Append(' ') : sb;

    private static string Sentence(string s)
    {
        if (s.Length == 0) return "";
        var c = Capitalize(s);
        return c[^1] is '.' or '!' or '?' ? c : c + ".";
    }

    private static string Capitalize(string s) => s.Length == 0 || !char.IsLower(s[0]) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>상한 이내면 그대로, 초과면 마지막 쉼표/공백 경계에서 절단.</summary>
    internal static (string Text, bool Truncated) Fit(string s, int max)
    {
        if (s.Length <= max) return (s, false);
        var cut = s.AsSpan(0, max);
        var b = cut.LastIndexOfAny(',', ' ');
        if (b > max / 2) cut = cut[..b];
        return (cut.TrimEnd(", ".AsSpan()).ToString(), true);
    }
}
