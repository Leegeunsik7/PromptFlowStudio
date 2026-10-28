using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PromptFlowStudio.Services.Camera;

/// <summary>카메라 모션 강도 — 프롬프트 수식어 · 3D 프리뷰 진폭에 반영.</summary>
public enum MotionIntensity { Subtle, Normal, Dynamic }

/// <summary>스마트 카메라 → 작업실 1회 핸드오프 페이로드(프레임 바이트는 I2V 참조 버퍼로 별도 전달).</summary>
public sealed record CameraWorkspaceTransfer(string CameraMotion, MotionIntensity Intensity, double Speed);

/// <summary>
/// [PromptFlowStudio] 지능형 프롬프트 병합기 — 기존 프롬프트의 카메라 지시어(Pan/Orbit/Zoom/Tracking/Dolly/Wide shot…)를
///   감지·제거하고 새 카메라 모션 + 강도/속도 수식어를 첫 지시어 자리(없으면 끝)에 쉼표 구분으로 교체 삽입.
///   ▸ 수식어 자체도 "camera movement/motion" 을 포함 → 재병합 시 다시 교체(멱등). UI 클릭 경로 전용(핫패스 아님).
/// </summary>
public static class SmartCameraMerge
{
    public static readonly double[] Speeds = { 0.5, 1.0, 1.5 };

    private static readonly Regex CameraToken = new(
        @"\b(pan|pans|panning|orbit|orbits|orbiting|zoom|zooms|zooming|dolly|tracking|crane|tilt|tilting|trucking|aerial|drone|handheld|vertigo|push[- ]in|pull[- ]out|top-down view|bird'?s[- ]eye|camera (movement|motion|move|moves|moving|shake|speed)|(extreme |medium |long |establishing |wide )shot|wide[- ]angle|close[- ]?up)\b"
        + @"|카메라\s*(팬|패닝|줌|이동|무빙|워크)|패닝|줌\s*(인|아웃)|돌리\s*(줌|인|아웃|샷)|트래킹\s*샷|오빗|드론\s*(샷|부감)|와이드\s*샷",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>토큰(쉼표 구분 1조각)이 카메라 지시어인지.</summary>
    public static bool IsCameraToken(string token) => !string.IsNullOrWhiteSpace(token) && CameraToken.IsMatch(token);

    public static string IntensityModifier(MotionIntensity intensity) => intensity switch
    {
        MotionIntensity.Subtle => "subtle gentle camera movement",
        MotionIntensity.Dynamic => "dynamic energetic camera movement",
        _ => "steady camera movement",
    };

    /// <summary>속도 수식어 — 1.0x 는 생략(빈 문자열).</summary>
    public static string SpeedModifier(double speed)
        => speed < 0.75 ? "slow-paced camera motion" : speed > 1.25 ? "fast-paced camera motion" : "";

    public static string SpeedLabel(double speed) => speed.ToString("0.0", CultureInfo.InvariantCulture) + "x";

    public static string MergeCameraPrompt(string existingPrompt, string newCameraMotion, MotionIntensity intensity)
        => MergeCameraPrompt(existingPrompt, newCameraMotion, intensity, 1.0);

    /// <summary>기존 카메라 지시어 제거 + 중복 제거 후 새 모션·강도·속도 수식어 교체/병합. 새 모션이 비면 기존 유지.</summary>
    public static string MergeCameraPrompt(string? existingPrompt, string? newCameraMotion, MotionIntensity intensity, double speed)
    {
        var existing = existingPrompt ?? "";
        var incoming = Split(newCameraMotion);
        if (incoming.Count == 0) return existing.Trim();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var insert = new List<string>(incoming.Count + 2);
        foreach (var t in incoming) if (seen.Add(t)) insert.Add(t);
        var hasCamera = false;
        foreach (var t in insert) if (CameraToken.IsMatch(t)) { hasCamera = true; break; }
        if (hasCamera)
        {
            var mod = IntensityModifier(intensity);
            if (seen.Add(mod)) insert.Add(mod);
            var sp = SpeedModifier(speed);
            if (sp.Length > 0 && seen.Add(sp)) insert.Add(sp);
        }

        var kept = new List<string>();
        var at = -1;
        foreach (var t in Split(existing))
        {
            if (hasCamera && CameraToken.IsMatch(t)) { if (at < 0) at = kept.Count; continue; }
            if (!seen.Add(t)) continue;   // 새 토큰과 중복 · 기존 내 중복
            kept.Add(t);
        }
        if (at < 0) at = kept.Count;
        kept.InsertRange(at, insert);

        var sb = new StringBuilder(existing.Length + 64);
        for (var i = 0; i < kept.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(kept[i]);
        }
        return sb.ToString();
    }

    private static List<string> Split(string? s)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(s)) return list;
        foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (part.Length > 0) list.Add(part);
        return list;
    }
}
