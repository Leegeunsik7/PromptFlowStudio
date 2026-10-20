using System.Numerics;

namespace PromptFlowStudio.Services.Cinematography;

public enum ShotType : byte { None, ExtremeWide, Wide, Full, Medium, MediumCloseUp, CloseUp, ExtremeCloseUp }

/// <summary>구도 평가 결과(값 형식 · 무할당): 샷 타입 · 더치 · 화면 좌표 · 구성 점수(0..1) · 종합 0..100.</summary>
public readonly record struct FramingResult(
    ShotType Type, bool Dutch, bool Visible, Vector2 Eye, float HeadTop, float Feet, float HeadFraction, float BodyFraction, float FacingX,
    float Thirds, float Golden, float Headroom, float LeadRoom, float Horizon, float Balance, float Edge, float Score);

/// <summary>
/// [PromptFlow Studio v16.85.0] 📐 제너레이티브 샷 프레이밍 &amp; 미학 구도 평가기(무할당 · 값 결과).
///   ▸ 투영: 카메라 기저 + 수직 FOV · 화면비 → 눈/정수리/발/보조 피사체 정규 좌표(좌상 원점).
///   ▸ 분류: 머리(0.25 m) 화면 점유율 · 전신 점유율 → 익스트림 와이드 ~ 익스트림 클로즈업 · |롤| &gt; 8° 더치.
///   ▸ 점수: 삼등분 교차점 · 황금비(0.382/0.618) 근접(가우시안 σ = 0.08) · 샷별 헤드룸 허용 대역(눈선 정합) · 리드룸(시선 방향 앞 공간 ≈ 0.64) · 수평(우발 기울기 감점 · 의도 더치 허용) ·
///     보조 피사체 시각 무게 균형 · 프레임 경계 접선(발목/정수리 절단) 감점 → 가중 0..100.
///   ▸ 생성 제안: 시선 반대편 삼등분 교차점(ECU = 0.42) → 궤적 엔진 CompositionTarget 역조준.
/// </summary>
public sealed class GenerativeShotFramingEvaluator
{
    public const float HeadSize = 0.25f, DutchThresholdDeg = 8f, Sigma = 0.08f;
    public const float WGrid = 0.3f, WHeadroom = 0.2f, WLead = 0.2f, WHorizon = 0.1f, WBalance = 0.1f, WEdge = 0.1f;
    private const float Phi = 0.381966f;

    public long Evaluations { get; private set; }
    public FramingResult Last { get; private set; }

    public static string TypeName(ShotType t) => t switch
    {
        ShotType.ExtremeWide => "익스트림 와이드", ShotType.Wide => "와이드", ShotType.Full => "풀 샷", ShotType.Medium => "미디엄",
        ShotType.MediumCloseUp => "미디엄 클로즈업", ShotType.CloseUp => "클로즈업", ShotType.ExtremeCloseUp => "익스트림 클로즈업", _ => "피사체 없음",
    };

    /// <summary>샷 타입별 이상 헤드룸(프레임 상단 ~ 정수리 비).</summary>
    public static float IdealHeadroom(ShotType t) => t switch
    {
        ShotType.ExtremeCloseUp => -0.06f, ShotType.CloseUp => 0.03f, ShotType.MediumCloseUp => 0.06f, ShotType.Medium => 0.08f,
        ShotType.Full => 0.1f, ShotType.Wide => 0.2f, _ => 0.3f,
    };

    public static ShotType Classify(float headFrac, float bodyFrac)
    {
        if (headFrac >= 0.45f) return ShotType.ExtremeCloseUp;
        if (headFrac >= 0.2f) return ShotType.CloseUp;
        if (headFrac >= 0.11f) return ShotType.MediumCloseUp;
        if (bodyFrac >= 1.05f) return ShotType.Medium;
        if (bodyFrac >= 0.6f) return ShotType.Full;
        if (bodyFrac >= 0.3f) return ShotType.Wide;
        return ShotType.ExtremeWide;
    }

    private static float Gauss(float d, float s) => MathF.Exp(-(d * d) / (s * s));

    /// <summary>정규 화면 좌표(0..1 · 좌상) + 카메라 z.</summary>
    public static Vector3 ProjectNormalized(Vector3 p, in CameraPose cam)
    {
        var d = p - cam.Position;
        var z = Vector3.Dot(d, cam.Forward);
        if (z < 0.05f) return new Vector3(-1f, -1f, z);
        var tv = NeuralCameraTrajectoryEngine.SensorHeightMm * 0.5f / cam.FocalMm; var th = tv * NeuralCameraTrajectoryEngine.ImageAspect;
        return new Vector3(0.5f + Vector3.Dot(d, cam.Right) / (z * th) * 0.5f, 0.5f - Vector3.Dot(d, cam.Up) / (z * tv) * 0.5f, z);
    }

    /// <summary>구도 평가(무할당): 피사체 발 위치 · 신장 · 시선 방향 · 보조 피사체(선택).</summary>
    public FramingResult Evaluate(in CameraPose cam, Vector3 feet, float height, Vector3 facing, Vector3 secondary, bool hasSecondary)
    {
        Evaluations++;
        var eyeW = feet + Vector3.UnitY * (height * 0.92f);
        var topW = feet + Vector3.UnitY * height;
        var e = ProjectNormalized(eyeW, cam); var tp = ProjectNormalized(topW, cam); var ft = ProjectNormalized(feet, cam);
        var chinW = topW - Vector3.UnitY * HeadSize; var ch = ProjectNormalized(chinW, cam);
        var visible = e.Z > 0.05f && e.X >= 0f && e.X <= 1f && e.Y >= 0f && e.Y <= 1f;
        var roll = cam.RollDeg;
        var dutch = MathF.Abs(roll) > DutchThresholdDeg;
        if (!visible)
        {
            var none = new FramingResult(ShotType.None, dutch, false, new Vector2(e.X, e.Y), tp.Y, ft.Y, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f);
            Last = none;
            return none;
        }
        var headFrac = MathF.Max(0f, ch.Y - tp.Y); var bodyFrac = MathF.Max(0f, ft.Y - tp.Y);
        var type = Classify(headFrac, bodyFrac);

        // 삼등분 · 황금비 교차점 근접
        var ex = e.X; var ey = e.Y;
        float dT = float.MaxValue, dG = float.MaxValue;
        for (var i = 1; i <= 2; i++)
            for (var j = 1; j <= 2; j++)
            {
                dT = MathF.Min(dT, Vector2.Distance(new Vector2(ex, ey), new Vector2(i / 3f, j / 3f)));
                dG = MathF.Min(dG, Vector2.Distance(new Vector2(ex, ey), new Vector2(i == 1 ? Phi : 1f - Phi, j == 1 ? Phi : 1f - Phi)));
            }
        float thirds = Gauss(dT, Sigma), golden = Gauss(dG, Sigma);
        var grid = 0.7f * MathF.Max(thirds, golden) + 0.3f * MathF.Min(thirds, golden);

        // 헤드룸: 정수리 위 공간 · 비-ECU 머리 절단 강한 감점
        // 허용 대역 [이상치 − 0.04, max(이상치, 눈선 1/3 − 눈~정수리) + 0.02] 내 1 · 밖은 가우시안 감쇠(삼등분 눈선과 정합)
        var hr = tp.Y; var ideal = IdealHeadroom(type);
        float lo = ideal - 0.04f, hi = MathF.Max(ideal, 1f / 3f - (ey - hr)) + 0.02f;
        var headroom = hr < lo ? Gauss(lo - hr, 0.06f) : hr > hi ? Gauss(hr - hi, 0.06f) : 1f;
        if (hr < 0f && type != ShotType.ExtremeCloseUp) headroom *= 0.2f;

        // 리드룸: 시선 화면 성분 · 앞 공간 0.64 이상치 · 정면 응시는 중앙 허용
        var fx = Vector3.Dot(Vector3.Normalize(new Vector3(facing.X, 0f, facing.Z) + new Vector3(1e-6f, 0f, 0f)), cam.Right);
        var lead = fx >= 0f ? 1f - ex : ex;
        var leadScore = Gauss(lead - 0.64f, 0.12f);
        var centerScore = Gauss(ex - 0.5f, 0.15f);
        var w = Math.Clamp(MathF.Abs(fx) * 1.5f, 0f, 1f);
        var leadRoom = centerScore + (leadScore - centerScore) * w;

        // 수평: 1° 이내 = 1 · 1~8° 우발 기울기 → 0.3 · 더치 = 0.85(양식화)
        var ar = MathF.Abs(roll);
        var horizon = ar <= 1f ? 1f : ar <= DutchThresholdDeg ? 1f - 0.7f * (ar - 1f) / (DutchThresholdDeg - 1f) : 0.85f;

        // 균형: 보조 피사체 포함 시각 무게 중심 수평 중앙 근접
        var balance = 1f;
        if (hasSecondary)
        {
            var s2 = ProjectNormalized(secondary, cam);
            if (s2.Z > 0.05f && s2.X >= 0f && s2.X <= 1f)
            {
                var wm = 0.6f; var cx = ex * wm + s2.X * (1f - wm);
                balance = 1f - Math.Clamp(MathF.Abs(cx - 0.5f) * 2.5f, 0f, 1f);
            }
            else balance = 0.6f;
        }

        // 경계 접선: 발목/정수리가 프레임 경계 ±3% 에 걸리면 감점
        var edge = 1f;
        if (MathF.Abs(ft.Y - 1f) < 0.03f) edge -= 0.5f;
        if (MathF.Abs(tp.Y) < 0.015f) edge -= 0.3f;
        if (ex < 0.06f || ex > 0.94f) edge -= 0.4f;
        edge = MathF.Max(edge, 0f);

        var score = 100f * (WGrid * grid + WHeadroom * headroom + WLead * leadRoom + WHorizon * horizon + WBalance * balance + WEdge * edge)
                    / (WGrid + WHeadroom + WLead + WHorizon + WBalance + WEdge);
        var r = new FramingResult(type, dutch, true, new Vector2(ex, ey), tp.Y, ft.Y, headFrac, bodyFrac, fx, thirds, golden, headroom, leadRoom, horizon, balance, edge, score);
        Last = r;
        return r;
    }

    /// <summary>생성 제안: 시선 반대편 삼등분 교차점(눈 높이 = 위 1/3 · ECU 0.42 · 정면 응시 = 중앙).</summary>
    public static Vector2 SuggestEyeTarget(in FramingResult r)
    {
        if (!r.Visible) return new Vector2(0.5f, 0.4f);
        var y = r.Type == ShotType.ExtremeCloseUp ? 0.42f : 1f / 3f;
        if (MathF.Abs(r.FacingX) < 0.2f) return new Vector2(0.5f, y);
        return new Vector2(r.FacingX > 0f ? 1f / 3f : 2f / 3f, y);
    }
}
