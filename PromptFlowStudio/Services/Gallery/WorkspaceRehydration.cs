using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.Services.Ui;

namespace PromptFlowStudio.Services.Gallery;

/// <summary>작업실 역복원 스냅샷 — 프롬프트 · 카메라 모션 라벨 · 듀레이션 모드 · 화면비(<c>9:16</c>) · 사이즈 셀렉터 값(<c>720*1280</c>) · 시드.</summary>
public sealed record WorkspaceRehydrationSnapshot(
    string MediaPath, string Prompt, string CameraMotion, VideoDurationMode DurationMode,
    string AspectRatio, string WorkspaceSize, long Seed, bool HasMetadata);

/// <summary>
/// [PromptFlowStudio] 최근 미디어 → 작업실 파라미터 역복원(Re-hydration).
///   ▸ 소스: 미디어 옆 <c>{이름}.meta.json</c>(<see cref="GalleryMediaService"/> 스키마) — <c>durationMode</c>/<c>cameraMotion</c>/<c>aspectRatio</c> 확장 필드 우선,
///     없으면 <c>durationSec</c> · 파일명(<c>_10s</c>/<c>_15s</c>/<c>ambient</c>) · 프롬프트 내 카메라 토큰 · 해상도로 추정.
///   ▸ 메타 없음/손상 → 파일명 기반 기본 스냅샷(<see cref="WorkspaceRehydrationSnapshot.HasMetadata"/>=false) · 예외 없음.
///   ▸ <see cref="WriteIfMissing"/>: 단일 생성 산출물에 사이드카를 1회 기록(기존 메타는 덮어쓰지 않음).
/// </summary>
public static class WorkspaceRehydration
{
    public const string SizeLandscape = "1280*720";
    public const string SizePortrait = "720*1280";
    public const string SizeSquare = "960*960";

    /// <summary>미디어 경로의 사이드카 메타를 읽어 스냅샷 생성. 실패는 기본 스냅샷.</summary>
    public static WorkspaceRehydrationSnapshot Load(string mediaPath)
    {
        string? json = null;
        try
        {
            var meta = GalleryMediaService.MetaPathFor(mediaPath ?? "");
            if (File.Exists(meta)) json = File.ReadAllText(meta);
        }
        catch (Exception ex) { Program.Log("WorkspaceRehydration.Load", ex); }
        return FromJson(mediaPath ?? "", json);
    }

    /// <summary>사이드카 JSON(없으면 null) → 스냅샷. 순수 함수(테스트 대상).</summary>
    public static WorkspaceRehydrationSnapshot FromJson(string mediaPath, string? json)
    {
        string prompt = "", camera = "", aspect = "", resolution = "", mode = "";
        double durationSec = 0;
        long seed = 0;
        var has = false;
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                if (r.ValueKind == JsonValueKind.Object)
                {
                    has = true;
                    prompt = Str(r, "prompt");
                    camera = Str(r, "cameraMotion");
                    aspect = Str(r, "aspectRatio");
                    resolution = Str(r, "resolution");
                    mode = Str(r, "durationMode");
                    if (r.TryGetProperty("seed", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var sv)) seed = sv;
                    if (r.TryGetProperty("durationSec", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out var dv)) durationSec = dv;
                    if (r.TryGetProperty("metadata", out var m) && m.ValueKind == JsonValueKind.Object && mode.Length == 0)
                        mode = Str(m, "durationMode");
                }
            }
            catch (JsonException) { has = false; }
        }

        var name = Path.GetFileNameWithoutExtension(mediaPath ?? "") ?? "";
        var duration = ResolveDuration(mode, durationSec, name);
        if (camera.Length == 0) camera = ResolveCameraMotion(prompt);
        var size = ResolveWorkspaceSize(resolution, aspect);
        if (aspect.Length == 0) aspect = AspectOfSize(size);
        return new WorkspaceRehydrationSnapshot(mediaPath ?? "", prompt, camera, duration, aspect, size, seed, has);
    }

    /// <summary>프롬프트 내 Wan 카메라 토큰 → 라벨(<c>Orbit</c> 등). 없으면 빈 문자열.</summary>
    public static string ResolveCameraMotion(string? prompt)
    {
        var i = WanCameraStyleStudio.ActiveIndex(prompt, WanCameraStyleStudio.Cameras);
        return i >= 0 ? WanCameraStyleStudio.Cameras[i].Label : "";
    }

    /// <summary>명시 모드(enum 이름 · <c>10s</c>/<c>15s</c>/<c>15s-ambient</c>) → 길이(초) → 파일명 토큰 순으로 결정. 기본 5s.</summary>
    public static VideoDurationMode ResolveDuration(string? explicitMode, double durationSec, string? fileName)
    {
        var m = (explicitMode ?? "").Trim();
        if (m.Length > 0)
        {
            if (Enum.TryParse<VideoDurationMode>(m, true, out var e) && Enum.IsDefined(e)) return e;
            if (m.Contains("ambient", StringComparison.OrdinalIgnoreCase)) return VideoDurationMode.Sec15_AmbientSlow;
            if (m.StartsWith("15", StringComparison.Ordinal)) return VideoDurationMode.Sec15_Triple;
            if (m.StartsWith("10", StringComparison.Ordinal)) return VideoDurationMode.Sec10_Dual;
            if (m.StartsWith("5", StringComparison.Ordinal)) return VideoDurationMode.Sec5_Single;
        }
        var n = fileName ?? "";
        if (n.Contains("ambient", StringComparison.OrdinalIgnoreCase)) return VideoDurationMode.Sec15_AmbientSlow;
        if (durationSec >= 12.5 || n.EndsWith("_15s", StringComparison.OrdinalIgnoreCase)) return VideoDurationMode.Sec15_Triple;
        if (durationSec >= 7.5 || n.EndsWith("_10s", StringComparison.OrdinalIgnoreCase)) return VideoDurationMode.Sec10_Dual;
        return VideoDurationMode.Sec5_Single;
    }

    /// <summary>해상도(<c>720x1280</c>) 또는 화면비(<c>9:16</c>) → 작업실 사이즈 셀렉터 값 3종 중 하나. 기본 가로.</summary>
    public static string ResolveWorkspaceSize(string? resolution, string? aspectRatio)
    {
        if (GalleryMediaService.ParseResolution(resolution) is { } d)
        {
            var exact = d.W.ToString(CultureInfo.InvariantCulture) + "*" + d.H.ToString(CultureInfo.InvariantCulture);
            if (exact is SizeLandscape or SizePortrait or SizeSquare) return exact;
            return GalleryMediaService.Classify(d.W, d.H) switch
            {
                AspectCategory.Vertical9x16 => SizePortrait,
                AspectCategory.Square1x1 => SizeSquare,
                _ => SizeLandscape,
            };
        }
        return (aspectRatio ?? "").Trim() switch
        {
            "9:16" => SizePortrait,
            "1:1" => SizeSquare,
            _ => SizeLandscape,
        };
    }

    public static string AspectOfSize(string size) => size switch
    {
        SizePortrait => "9:16",
        SizeSquare => "1:1",
        _ => "16:9",
    };

    public static string ModeToken(VideoDurationMode mode) => mode switch
    {
        VideoDurationMode.Sec10_Dual => "10s",
        VideoDurationMode.Sec15_Triple => "15s",
        VideoDurationMode.Sec15_AmbientSlow => "15s-ambient",
        _ => "5s",
    };

    public static int SecondsOf(VideoDurationMode mode) => mode switch
    {
        VideoDurationMode.Sec10_Dual => 10,
        VideoDurationMode.Sec15_Triple or VideoDurationMode.Sec15_AmbientSlow => 15,
        _ => 5,
    };

    /// <summary>단일 생성 산출물 사이드카 1회 기록(갤러리 스키마 + 역복원 확장 필드). 이미 있으면 false · 실패 격리.</summary>
    public static bool WriteIfMissing(string mediaPath, string? prompt, VideoDurationMode mode, string? workspaceSize, long seed)
    {
        if (string.IsNullOrWhiteSpace(mediaPath)) return false;
        try
        {
            var metaPath = GalleryMediaService.MetaPathFor(mediaPath);
            if (File.Exists(metaPath) || !File.Exists(mediaPath)) return false;
            var size = ResolveWorkspaceSize(workspaceSize, null);
            var json = JsonSerializer.Serialize(new
            {
                title = Path.GetFileNameWithoutExtension(mediaPath),
                prompt = prompt ?? "",
                seed,
                resolution = size.Replace('*', 'x'),
                createdAt = DateTimeOffset.Now,
                durationSec = SecondsOf(mode),
                durationMode = mode.ToString(),
                cameraMotion = ResolveCameraMotion(prompt),
                aspectRatio = AspectOfSize(size),
            });
            File.WriteAllText(metaPath, json);
            return true;
        }
        catch (Exception ex) { Program.Log("WorkspaceRehydration.Write", ex); return false; }
    }

    private static string Str(JsonElement r, string name)
        => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";
}
