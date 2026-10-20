using System.Numerics;

namespace PromptFlowStudio.Services.CognitiveCinematography;

/// <summary>가림 대응: 끔 · 장애물 투명화 · 카메라 미끄러짐 · 자동(미끄러짐 우선 → 실패 시 투명화).</summary>
public enum OcclusionMode : byte { Off, Fade, Slide, Auto }

/// <summary>
/// [PromptFlow Studio v17.21.0] 🎯 자율 포커스 풀링 &amp; 카메라 시선 가림 방지 가드(무할당 · 결정적).
///   ▸ 포커스 풀링: 피사체(v17.12) 카메라 쪽 안구의 z 깊이를 목표로 1st AC 모델(임계 감쇠 k = 40 · 최대 4 m/s) 추종 — 피사체 전환 시 랙 포커스 집계.
///   ▸ 피사계 심도: 허용 착란원 0.03 mm · 과초점 H = f²/(N·c) + f · 근/원 한계 → 안구가 심도 내인지 판정 · 안구 CoC(px).
///   ▸ 시선 가림: 카메라 → 피사체 5 점(안구 · 정수리 · 좌/우 관자 · 가슴) 장애물 SDF 구체 추적(여유 0.04 m) → 가림률.
///   ▸ 미끄러짐: 궤도 ±10°…±70° 후보를 같은 SDF 로 검사해 최소 이동 무가림 위치 선택(히스테리시스 1.2 s 후 원위치 복귀 시도).
///   ▸ 투명화: 가린 장애물 알파 0.22 로 페이드(4 /s) · 해소 시 복원.
/// </summary>
public sealed class AutonomousFocusPullingOcclusionGuard
{
    public const int Samples = 5, MaxSteps = 64;
    public const float CocMm = 0.03f, Margin = 0.04f, CandidateMargin = 0.3f, TransitFadeS = 0.25f, FadeAlpha = 0.22f, FadeRate = 4f, PullK = 40f, PullMaxMs = 4f, SlideStepDeg = 10f, SlideMaxDeg = 70f, ReturnHoldS = 1.2f;

    private readonly Vector3[] _pts = new Vector3[Samples];
    private readonly bool[] _blockedPrim = new bool[CinematicBlockingSet.MaxPrims];
    private int _lastSubject = -1;
    private float _focusVel, _clearTime, _slideTarget, _occTime;

    public OcclusionMode Mode { get; set; } = OcclusionMode.Auto;
    public bool AutoFStop { get; set; } = true;
    public float ManualFStop { get; set; } = 2.8f;
    public bool AutoFocus { get; set; } = true;
    public float FStop { get; private set; } = 2.8f;
    public float FocusDistance { get; private set; } = 3f;
    public float SubjectDepth { get; private set; } = 3f;
    public float FocusErrorMm => MathF.Abs(FocusDistance - SubjectDepth) * 1000f;
    public float NearLimit { get; private set; }
    public float FarLimit { get; private set; }
    public float Hyperfocal { get; private set; }
    public bool EyeInFocus { get; private set; }
    public float EyeCocPx { get; private set; }
    public long Racks { get; private set; }
    public float Occlusion { get; private set; }
    /// <summary>투명화되지 않은(알파 &gt; 0.5) 장애물에 의한 가림률 — 실제 화면 가림.</summary>
    public float VisibleOcclusion { get; private set; }
    public float MinClearance { get; private set; }
    public int Blocker { get; private set; } = -1;
    public long OcclusionEvents { get; private set; }
    public long Slides { get; private set; }
    public long Fades { get; private set; }
    public float SlideYawDeg { get; private set; }
    public float SlideTargetDeg => _slideTarget;
    public bool Occluded => Occlusion > 0f;
    public int SdfSteps { get; private set; }
    public double LastGuardUs { get; private set; }

    /// <summary>피사계 심도 한계(m) — 초점 거리 mm · 조리개 · 초점 거리 m.</summary>
    public static void DepthOfField(float focalMm, float fStop, float focusM, out float near, out float far, out float hyperM)
    {
        var f = focalMm / 1000f;
        var h = f * f / (fStop * (CocMm / 1000f)) + f;
        hyperM = h;
        near = focusM * (h - f) / (h + focusM - 2f * f);
        far = focusM < h ? focusM * (h - f) / (h - focusM) : float.PositiveInfinity;
    }

    /// <summary>점 a → b 구간 장애물 SDF 구체 추적(가림 시 해당 프리미티브).</summary>
    public bool SegmentBlocked(CinematicBlockingSet set, Vector3 a, Vector3 b, out int which, out float clearance, float margin = Margin)
    {
        var d = b - a;
        var len = d.Length();
        d /= len;
        var t = 0f;
        clearance = float.PositiveInfinity; which = -1;
        for (var i = 0; i < MaxSteps && t < len; i++)
        {
            SdfSteps++;
            var s = set.ObstacleSdf(a + d * t, out var k);
            if (s < clearance) clearance = s;
            if (s < margin) { which = k; return true; }
            t += MathF.Max(s, 0.02f);
        }
        return false;
    }

    private bool PositionClear(CinematicBlockingSet set, Vector3 cam, int subject)
    {
        // 후보 검사는 안구 + 가슴 2 점 · 넓은 여유(0.3 m) — 동선 변화에도 재가림 지연
        return !SegmentBlocked(set, cam, set.Eye(subject), out _, out _, CandidateMargin) && !SegmentBlocked(set, cam, set.ActorPosition(subject) + new Vector3(0f, 1.25f, 0f), out _, out _, CandidateMargin);
    }

    private static Vector3 OrbitPosition(CognitiveCameraFramingBlockingEngine cam, Vector3 eye, float extraDeg)
    {
        var p = cam.TargetPosition;
        var rel = new Vector3(p.X - eye.X, 0f, p.Z - eye.Z);
        var a = extraDeg * (MathF.PI / 180f);
        float cs = MathF.Cos(a), sn = MathF.Sin(a);
        return new Vector3(eye.X + rel.X * cs + rel.Z * sn, p.Y, eye.Z - rel.X * sn + rel.Z * cs);
    }

    /// <summary>1틱(무할당): 가림 판정 → 미끄러짐/투명화 → 포커스 풀링 · 심도 계측.</summary>
    public void Process(CinematicBlockingSet set, CognitiveCameraFramingBlockingEngine cam, float dt, float directorFStop)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        SdfSteps = 0;
        var s = Math.Clamp(cam.Subject, 0, CinematicBlockingSet.Actors - 1);
        var c = cam.Position;
        var head = set.HeadCenter(s);
        _pts[0] = set.Eye(s);
        _pts[1] = head + new Vector3(0f, CinematicBlockingSet.HeadRadius, 0f);
        _pts[2] = head + cam.Right * (CinematicBlockingSet.HeadRadius * 0.9f);
        _pts[3] = head - cam.Right * (CinematicBlockingSet.HeadRadius * 0.9f);
        _pts[4] = set.ActorPosition(s) + new Vector3(0f, 1.25f, 0f);
        Array.Clear(_blockedPrim);
        var blocked = 0; var visible = 0;
        var minClr = float.PositiveInfinity;
        Blocker = -1;
        for (var i = 0; i < Samples; i++)
        {
            if (SegmentBlocked(set, c, _pts[i], out var k, out var clr)) { blocked++; _blockedPrim[k] = true; Blocker = k; if (set.Alpha(k) > 0.5f) visible++; }
            if (clr < minClr) minClr = clr;
        }
        var was = Occluded;
        Occlusion = blocked / (float)Samples;
        VisibleOcclusion = visible / (float)Samples;
        MinClearance = minClr;
        if (Occluded && !was) OcclusionEvents++;
        _occTime = Occluded ? _occTime + dt : 0f;
        cam.Urgent = Mode != OcclusionMode.Off && Mode != OcclusionMode.Fade && Occluded;

        var wantFade = false;
        if (Mode == OcclusionMode.Slide || Mode == OcclusionMode.Auto)
        {
            // 카메라 목표는 평활 미끄러짐 각 기준 → 확정 목표까지 남은 오프셋
            var pending = _slideTarget - SlideYawDeg;
            if (Occluded)
            {
                _clearTime = 0f;
                // 확정 목표가 이미 무가림이면 이동 대기, 아니면 최소 이동 무가림 후보 탐색
                var found = PositionClear(set, OrbitPosition(cam, _pts[0], pending), s);
                for (var step = SlideStepDeg; step <= SlideMaxDeg && !found; step += SlideStepDeg)
                {
                    for (var sg = -1; sg <= 1 && !found; sg += 2)
                    {
                        var off = pending + sg * step;
                        if (MathF.Abs(SlideYawDeg + off) > SlideMaxDeg * 1.5f) continue;
                        if (PositionClear(set, OrbitPosition(cam, _pts[0], off), s)) { _slideTarget = SlideYawDeg + off; found = true; Slides++; }
                    }
                }
                // 자동: 무가림 후보 없음 또는 미끄러지는 동안(0.25 s 초과) 임시 투명화
                if (Mode == OcclusionMode.Auto && (!found || _occTime > TransitFadeS)) wantFade = true;
            }
            else
            {
                _clearTime += dt;
                if (_clearTime > ReturnHoldS && _slideTarget != 0f)
                {
                    var back = -MathF.Sign(_slideTarget) * MathF.Min(SlideStepDeg, MathF.Abs(_slideTarget));
                    if (PositionClear(set, OrbitPosition(cam, _pts[0], pending + back), s)) _slideTarget += back;
                    _clearTime = 0f;
                }
            }
        }
        else { _slideTarget = 0f; }
        if (Mode == OcclusionMode.Fade && Occluded) wantFade = true;
        // 미끄러짐 각의 평활(1차 · 6 /s) — 카메라 스프링이 추가 평활
        SlideYawDeg += (_slideTarget - SlideYawDeg) * MathF.Min(1f, 6f * dt);
        for (var k = set.ObstacleStart; k < set.PrimCount; k++)
        {
            var target = wantFade && _blockedPrim[k] ? FadeAlpha : 1f;
            var a = set.Alpha(k);
            if (target < a && a >= 0.999f) Fades++;
            set.SetAlpha(k, a + Math.Clamp(target - a, -FadeRate * dt, FadeRate * dt));
        }

        // ── 포커스 풀링 ──
        FStop = AutoFStop ? Math.Clamp(directorFStop, 1.4f, 16f) : Math.Clamp(ManualFStop, 1.4f, 16f);
        // 카메라 쪽 안구 = 얼굴 중심 대비 카메라 방향 오프셋(z 깊이 최소)
        var eyeDepth = Vector3.Dot(_pts[0] - c, cam.Forward);
        SubjectDepth = MathF.Max(0.3f, eyeDepth);
        if (_lastSubject >= 0 && _lastSubject != s) Racks++;
        _lastSubject = s;
        if (AutoFocus)
        {
            var a = PullK * (SubjectDepth - FocusDistance) - 2f * MathF.Sqrt(PullK) * _focusVel;
            _focusVel = Math.Clamp(_focusVel + a * dt, -PullMaxMs, PullMaxMs);
            FocusDistance = MathF.Max(0.3f, FocusDistance + _focusVel * dt);
        }
        DepthOfField(cam.FocalMm, FStop, FocusDistance, out var near, out var far, out var hyper);
        NearLimit = near; FarLimit = far; Hyperfocal = hyper;
        EyeInFocus = SubjectDepth >= near && SubjectDepth <= far;
        EyeCocPx = VolumetricRayMarchingCinematicDoFSynthesizer.CocRadiusPx(cam.FocalMm, FStop, FocusDistance, SubjectDepth);
        LastGuardUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    public void SetManualFocus(float m) { AutoFocus = false; FocusDistance = Math.Clamp(m, 0.3f, 30f); _focusVel = 0f; }
}
