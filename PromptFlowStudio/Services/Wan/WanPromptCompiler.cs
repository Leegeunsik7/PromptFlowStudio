using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PromptFlowStudio.Services.Mobile;   // SpatialPromptNode(공간 음성 구조화 데이터)

namespace PromptFlowStudio.Services.Wan;

// ════════════════════════════════════════════════════════════════════════════
//  [PromptFlow Studio v5.4.1 · Task1] Wan 3.0 프롬프트 컴파일러 — 순수 C# BCL(외부 SDK 0).
//   ▸ 모바일 모션-벡터(각도·속도·8방위)와 공간 음성 구조화 노드(SpatialPromptNode)를 합성,
//     Wan 3.0 카메라 모션 문법 + 시네마틱 프롬프트를 쉼표로 매끄럽게 연결한 최종 렌더 프롬프트를 반환.
//   ▸ 프론트엔드(MotionVectorPainter)와 백엔드(WanRenderClient)가 이 컴파일러를 단일 문법 원천으로 공유.
//   ⚠ 100% 결정적·순수 함수(테스트 대상) → 데스크톱 net8.0-windows 컴파일 정합.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>모션 해석 모드 — 카메라 이동 / 피사체 이동 / 줌·달리.</summary>
public enum MotionKind { Camera, Subject, Zoom }

/// <summary>8방위(+정지) — 화면 상향이 North.</summary>
public enum Direction8 { East, NorthEast, North, NorthWest, West, SouthWest, South, SouthEast, None }

/// <summary>
/// 모션 벡터 — 각도(도 · 수학상향 +) · 크기(0~1 정규화 · 프레임 대비) · 해석 모드.
///   프론트엔드(터치 화살표)가 생성, 컴파일러가 Wan 문법으로 변환.
/// </summary>
public sealed record MotionVector(double AngleDeg, double Magnitude, MotionKind Kind)
{
    /// <summary>dx/dy(화면 y 하향) + 모드로 생성(각도·정규화 크기 산출). refPx=정규화 기준 픽셀.</summary>
    public static MotionVector FromDelta(double dx, double dy, MotionKind kind, double refPx = 300)
    {
        var deg = Math.Atan2(-dy, dx) * 180.0 / Math.PI;   // y 반전(위=+)
        if (deg < 0) deg += 360;
        var len = Math.Sqrt(dx * dx + dy * dy);
        var mag = refPx <= 1 ? 0 : Math.Clamp(len / refPx, 0, 1);
        return new MotionVector(Safe(deg), Safe(mag), kind);
    }

    private static double Safe(double v) => double.IsNaN(v) || double.IsInfinity(v) ? 0 : v;
}

/// <summary>컴파일 결과 — 최종 Wan 프롬프트 · 모션 강도(0~100) · 모션 문구 목록 · 음성 주체.</summary>
public sealed record WanPromptBuild(string Prompt, int MotionIntensity, IReadOnlyList<string> MotionPhrases, string? VoiceSubject)
{
    /// <summary>빈 빌드(기본 프롬프트).</summary>
    public static WanPromptBuild Empty => new("cinematic video, smooth camera movement, ultra-detailed, 8k", 0, Array.Empty<string>(), null);
}

/// <summary>Wan 3.0 프롬프트 컴파일러(정적 · 순수).</summary>
/// <summary>[v5.6.4] 전역 품질 가드레일 강도 프리셋 — 경량/표준/최상위.</summary>
public enum QualityGuardrailPreset { Soft, Balanced, Strict }

public static class WanPromptCompiler
{
    /// <summary>단일 모션 벡터 → Wan 문법 문구.</summary>
    public static string PhraseFor(MotionVector mv)
    {
        if (mv is null || mv.Magnitude <= 0.001) return "";
        var dir = DirectionOf(mv.AngleDeg);
        var (h, v) = Compass(dir);                 // h:-1왼 0 1오른 · v:1위 0 -1아래
        var speed = SpeedWord(mv.Magnitude);

        return mv.Kind switch
        {
            MotionKind.Subject => "subject " + SubjectMove(h, v) + $" ({speed})",
            MotionKind.Zoom => ZoomMove(h, v, speed),
            _ => "camera " + CameraMove(h, v, PanDegrees(mv.Magnitude)) + $" ({speed})",
        };
    }

    /// <summary>여러 모션 벡터 → 중복 제거된 문구 목록(빈 문구 제외).</summary>
    public static IReadOnlyList<string> CompileMotion(IEnumerable<MotionVector>? motion)
    {
        if (motion is null) return Array.Empty<string>();
        var list = new List<string>();
        foreach (var mv in motion)
        {
            var p = PhraseFor(mv);
            if (!string.IsNullOrWhiteSpace(p) && !list.Contains(p)) list.Add(p);
        }
        return list;
    }

    /// <summary>모션 강도 집계(0~100) — 지배적(최대) 벡터 크기 기준.</summary>
    public static int AggregateIntensity(IEnumerable<MotionVector>? motion)
    {
        if (motion is null) return 0;
        var max = 0.0;
        foreach (var mv in motion) if (mv is not null && mv.Magnitude > max) max = mv.Magnitude;
        return max <= 0 ? 0 : Math.Clamp((int)Math.Round(max * 100), 5, 100);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  [v5.6.3 · Task3] 전역 코어 프롬프트 — 1:1 정체성 유지 + 초자연 고밀도 모션 가드레일.
    //   ▸ T2V/I2V/Concat 등 모든 생성 요청의 프롬프트 <b>말미에 자동 결합</b>(사용자 프롬프트는 원형 보존 · 훼손 0).
    //   ▸ 멱등: 이미 포함되어 있으면 재결합하지 않음(중복 차단).
    // ════════════════════════════════════════════════════════════════════════
    /// <summary>전역 정체성 보존 + 부드러운 모션 코어 접미사(Balanced 표준).</summary>
    public const string GlobalCorePrompt =
        "1:1 exact visual identity preservation for people, objects, and animals, seamless keypoint continuity, ultra-fluid natural motion, silky-smooth temporal transition, zero-flicker frame morphing, photorealistic consistency";

    // 프리셋별 코어 — Soft(경량 정체성)·Balanced(표준)·Strict(최상위).
    private const string SoftCore =
        "1:1 exact visual identity preservation for people, objects, and animals, natural smooth motion";
    private const string StrictCore =
        GlobalCorePrompt + ", flawless anatomical structure, temporally coherent in every frame, no warping, no identity drift, hyper-stable subject lock, cinema-grade high-density fluid motion";

    private const string CoreMarker = "exact visual identity preservation";

    /// <summary>프리셋(+커스텀 재정의) → 결합할 코어 구문.</summary>
    public static string BuildCore(QualityGuardrailPreset preset, string? custom)
    {
        if (!string.IsNullOrWhiteSpace(custom)) return custom!.Trim();   // 커스텀 키워드가 있으면 최우선(창작자 재정의)
        return preset switch
        {
            QualityGuardrailPreset.Soft => SoftCore,
            QualityGuardrailPreset.Strict => StrictCore,
            _ => GlobalCorePrompt,
        };
    }

    /// <summary>
    /// [v5.6.4] 사용자 설정(가드레일 On/Off · 프리셋 · 커스텀) 반영해 프롬프트 말미에 코어 결합.
    ///   ▸ <paramref name="enabled"/>=false → <b>원본 프롬프트 100% 통과</b>(시스템 개입 0 · 제약사항 2).
    ///   ▸ 멱등: 표준 마커가 이미 있으면 재결합하지 않음.
    /// </summary>
    public static string AppendCore(string? prompt, bool enabled, QualityGuardrailPreset preset, string? custom)
    {
        var p = (prompt ?? "").Trim();
        if (!enabled) return p;                                         // Off = 완전 통과(원본 제어권 보장)
        var core = BuildCore(preset, custom);
        if (p.Length == 0) return core;
        if (p.Contains(CoreMarker, StringComparison.OrdinalIgnoreCase)) return p;   // 표준 코어 중복 방지
        return p + ", " + core;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  [v5.7.5] AI 프롬프트 부스팅 BCL 폴백 — 원격 LLM 불가/오프라인 시 시네마틱 수식어를 1/60초 내 즉시 주입.
    // ════════════════════════════════════════════════════════════════════════
    private static readonly string[] CinematicQualities =
    {
        "shot on 35mm lens", "golden hour natural lighting", "hyper-realistic 8k resolution",
        "volumetric atmosphere", "highly detailed texture", "cinema-grade fluid motion",
    };

    /// <summary>
    /// [v5.7.5] 외부 LLM 오프라인 시 BCL 기반으로 프롬프트를 시네마틱 고밀도 문구로 증강(무재귀·멱등).
    ///   ▸ 이미 부스팅 마커("shot on 35mm")가 있으면 재부착하지 않음 · 빈 입력이면 기본 시네마틱 베이스.
    /// </summary>
    public static string EnhancePromptHeuristic(string? rawPrompt)
    {
        var p = (rawPrompt ?? "").Trim();
        if (string.IsNullOrWhiteSpace(p)) return "cinematic scene, highly detailed, masterwork, shot on 35mm lens";
        if (p.Contains("shot on 35mm", StringComparison.OrdinalIgnoreCase)) return p;   // 멱등(중복 부착 방지)
        return p + ", " + string.Join(", ", CinematicQualities.Take(3));
    }

    /// <summary>
    /// 공간 음성 노드 + 모션 벡터 → 최종 Wan 렌더 프롬프트 빌드.
    ///   음성 노드가 있으면 그 시네마틱 프롬프트를 베이스로, 없으면 subjectHint 로 기본 베이스를 만든 뒤
    ///   모션 문구를 쉼표로 매끄럽게 연결한다.
    /// </summary>
    public static WanPromptBuild Compile(SpatialPromptNode? voice, IEnumerable<MotionVector>? motion, string? subjectHint = null)
    {
        var phrases = CompileMotion(motion);
        var segments = new List<string>();

        var basePrompt = voice?.WanPrompt;
        var hasBase = !string.IsNullOrWhiteSpace(basePrompt);
        if (hasBase) segments.Add(basePrompt!.Trim());
        else
        {
            var subj = FirstNonEmpty(subjectHint, voice?.Subject, "cinematic scene");
            segments.Add($"cinematic video of {subj}");
        }

        segments.AddRange(phrases);
        if (phrases.Count == 0) segments.Add("smooth steady cinematic camera movement");
        if (!hasBase) segments.Add("volumetric lighting, ultra-detailed, sharp focus, 8k");   // 음성 베이스는 이미 8k 꼬리 포함(중복 방지)

        // v5.6.4: 코어는 렌더 디스패치(WanRenderClient.SubmitAsync)에서 사용자 설정 기반으로 단일 주입(여기선 원본 유지).
        var prompt = string.Join(", ", segments.Where(s => !string.IsNullOrWhiteSpace(s)));
        return new WanPromptBuild(prompt, AggregateIntensity(motion), phrases, voice?.Subject);
    }

    // ── 방위/문법 헬퍼 ──

    /// <summary>각도(도) → 8방위.</summary>
    public static Direction8 DirectionOf(double angleDeg)
    {
        var d = angleDeg % 360; if (d < 0) d += 360;
        var idx = (int)Math.Round(d / 45.0) % 8;
        return idx switch
        {
            0 => Direction8.East,
            1 => Direction8.NorthEast,
            2 => Direction8.North,
            3 => Direction8.NorthWest,
            4 => Direction8.West,
            5 => Direction8.SouthWest,
            6 => Direction8.South,
            _ => Direction8.SouthEast,
        };
    }

    private static (int H, int V) Compass(Direction8 d) => d switch
    {
        Direction8.East => (1, 0),
        Direction8.NorthEast => (1, 1),
        Direction8.North => (0, 1),
        Direction8.NorthWest => (-1, 1),
        Direction8.West => (-1, 0),
        Direction8.SouthWest => (-1, -1),
        Direction8.South => (0, -1),
        Direction8.SouthEast => (1, -1),
        _ => (0, 0),
    };

    private static string SpeedWord(double mag) => mag < 0.2 ? "slow" : mag < 0.5 ? "steady" : "fast";
    private static int PanDegrees(double mag) => mag < 0.2 ? 15 : mag < 0.5 ? 30 : 45;

    private static string CameraMove(int h, int v, int deg)
    {
        var pan = h == 1 ? $"pans right {deg}deg" : h == -1 ? $"pans left {deg}deg" : null;
        var tilt = v == 1 ? "tilts up" : v == -1 ? "tilts down" : null;
        if (pan is not null && tilt is not null) return $"{pan} and {tilt}";
        return pan ?? tilt ?? "holds steady";
    }

    private static string SubjectMove(int h, int v)
    {
        if (h != 0 && v != 0)   // 대각
        {
            var vv = v == 1 ? "up" : "down";
            var hh = h == 1 ? "right" : "left";
            return $"moving diagonally {vv}-{hh}";
        }
        if (h == 1) return "moving right";
        if (h == -1) return "moving left";
        if (v == 1) return "moving upward";
        if (v == -1) return "moving downward";
        return "holding position";
    }

    private static string ZoomMove(int h, int v, string speed)
    {
        // 세로 우선(줌/달리), 수평은 트럭.
        if (v == 1) return $"{speed} zoom in, dolly in";
        if (v == -1) return $"{speed} zoom out, dolly out";
        if (h == 1) return $"camera trucks right ({speed})";
        if (h == -1) return $"camera trucks left ({speed})";
        return $"static shot ({speed})";
    }

    private static string FirstNonEmpty(params string?[] vals)
    {
        foreach (var v in vals) if (!string.IsNullOrWhiteSpace(v)) return v!.Trim();
        return "cinematic scene";
    }
}
