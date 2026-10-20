using System.Numerics;
using PromptFlowStudio.Services.MotionMatching;

namespace PromptFlowStudio.Services.ContactLocomotion;

/// <summary>
/// [PromptFlow Studio v17.35.0] 🪜 계단 승하강 &amp; 단차 파쿠르 로코모션 합성기(<see cref="BeforeLock"/> · <see cref="BeforeSolve"/> 무할당).
///   ▸ 위상: v17.33 모션 DB(약 1.9만 프레임) 현재 클립의 미래 접지 플래그 → 발별 착지/이륙까지 프레임 · 스윙 진행률 u = 체공 ÷ (체공 + 착지까지).
///   ▸ 착지 보정: 포착 스텝(ZMP 가드 DCM 편차 → 진행 ±30 · 횡 ±15 cm) 후 그 주변 디딤판 안전 착지 계획(±8 cm) · 스무스스텝(u) · 임계 감쇠 80 ms 관성 보간 ·
///     착지 프레임(락 직전)까지 유지 → 락 위치 = 보정 착지 · 착지 디딤판 재접지.
///   ▸ 스윙 궤적: 현재 → 착지 경로 6점 × 뒤꿈치/발목/발끝 레이캐스트 최대 높이 + 클리어런스(5 cm + 0.3·챌판)·sin(πu) → 높이 스프링 40 ms(턱 넘기 = 챌판 비례 상승).
///   ▸ 접지 궤적: 락 xz 재접지(발목 · 뒤꿈치 8 cm 최대 → 발끝 길이 보존 · ZMP 기준 평면 재설정) · 이륙 10 프레임 전부터 발끝 축 롤링(최대 28° · 이차 램프 = Toe-Off).
///   ▸ 계단 보행 속도: 보폭(v ÷ 2·케이던스) = 디딤판 깊이 → v = 1.6T ÷ (1 − 0.4T)(한 칸/한 걸음 · 32 cm → 0.59 m/s) · 감속 0.4 m/s² 레이트 제한.
///   ▸ 무릎 굴곡: 계단 웅크림 = 8 cm + 0.4·챌판(골반 하강 → 접지 무릎 굴곡 확보) · 접지 무릎 굴곡각 계측.
///   ▸ 계단 가중: 계단 진입/이탈 0↔1 임계 감쇠(150 ms) 관성 블렌딩 · 착지 발자국 링버퍼 16(UI 궤적).
/// </summary>
public sealed class StairTraversalParkourMotionSynthesizer
{
    public const int Trail = 16, Look = 60, ToeOffFrames = 10;
    public const float ToeOffDeg = 28f, ClearBase = 0.05f, ClearGain = 0.3f, DeltaHalfLife = 0.08f, SwingYHalfLife = 0.04f, BlendHalfLife = 0.15f, MaxDelta = 0.08f, MaxCapture = 0.3f, MaxCaptureLateral = 0.15f, SpeedRamp = 0.4f;
    private static readonly int[] Ankle = { 6, 10 };

    private readonly float[] _dl = new float[2], _dlV = new float[2], _dlat = new float[2], _dlatV = new float[2], _sy = new float[2], _syV = new float[2], _toeOff = new float[2];
    private readonly int[] _air = new int[2], _land = new int[2], _lift = new int[2];
    private readonly bool[] _syInit = new bool[2], _was = new bool[2];
    private readonly Vector3[] _trail = new Vector3[Trail];
    private readonly byte[] _trailSide = new byte[Trail];
    private int _trailHead, _trailCount;
    private float _w, _wv, _vLim = float.PositiveInfinity;

    public bool Enabled { get; set; } = true;
    public bool PlannerEnabled { get; set; } = true;
    public bool SpeedAdapt { get; set; } = true;
    public float CrouchBase { get; set; } = 0.08f;
    public float StairSpeed { get; private set; } = float.PositiveInfinity;
    /// <summary>동역학 DCM 편차(월드 xz · ZMP 동적 안정 가드 제공) → 포착 스텝.</summary>
    public Vector2 Capture { get; set; }
    public float StairWeight => _w;
    public int PlannedSide { get; private set; }
    public int PlannedFrames { get; private set; }
    public Vector3 Nominal { get; private set; }
    /// <summary>다음 착지 예측(DB 착지 + 계획 보정 · 월드 발목).</summary>
    public Vector3 NextLanding { get; private set; }
    public int LandFrames(int s) => _land[s];
    public int LiftFrames(int s) => _lift[s];
    public float SwingPhase(int s) => _air[s] + _land[s] > 0 ? _air[s] / (float)(_air[s] + _land[s]) : 0f;
    public float ToeOffNowDeg(int s) => _toeOff[s];
    public float AppliedDelta(int s) => _dl[s];
    public long Landings { get; private set; }
    public long FlatLandings { get; private set; }
    public float FlatLandingRatio => Landings > 0 ? FlatLandings / (float)Landings : 1f;
    public float MinToeClearanceCm { get; private set; } = float.MaxValue;
    public float KneeFlexStanceDeg { get; private set; }
    public float MaxKneeFlexStanceDeg { get; private set; }
    public float Crouch { get; private set; }
    public int TrailCount => _trailCount;
    public Vector3 TrailPoint(int i, out byte side) { var k = (_trailHead - 1 - i + Trail * 2) % Trail; side = _trailSide[k]; return _trail[k]; }

    public void Reset() { Array.Clear(_dl); Array.Clear(_dlV); Array.Clear(_syInit); Array.Clear(_was); Array.Clear(_air); _w = _wv = 0f; _trailCount = 0; }

    public void ResetMetrics() { Landings = FlatLandings = 0; MinToeClearanceCm = float.MaxValue; MaxKneeFlexStanceDeg = 0f; }

    private static void Spring(ref float x, ref float v, float goal, float hl, float dt)
    {
        var y = TrajectoryController.Damping(hl) * 0.5f;
        var j0 = x - goal; var j1 = v + j0 * y; var e = MathF.Exp(-y * dt);
        x = e * (j0 + j1 * dt) + goal; v = e * (v - j1 * y * dt);
    }

    private static int Until(RealtimeNeuralMotionMatchingEngine e, int s, bool contact, int from)
    {
        for (var k = from; k <= Look; k++) if (((e.FutureContact(k) >> s & 1) != 0) == contact) return k;
        return Look;
    }

    /// <summary>DB 미래 k 프레임 발목(로컬) + 루트 속도/선회 외삽 → 예측 착지(월드).</summary>
    public static Vector3 PredictLanding(RealtimeNeuralMotionMatchingEngine e, int s, int k)
    {
        var t = k * NeuralMotionMatchingStudioRunner.Dt;
        var hm = e.Heading + 0.5f * e.YawRate * t; var h1 = e.Heading + e.YawRate * t;
        var origin = e.Position + (BipedSkeleton.Right(hm) * e.RootVelocity.X + BipedSkeleton.Forward(hm) * e.RootVelocity.Y) * t;
        return BipedSkeleton.ToWorld(e.FuturePose(k)[Ankle[s]], origin, h1);
    }

    /// <summary>접지 락 전: 위상 · 발 배치 계획 · 스윙 발 진행축 보정 + 장애물 클리어런스.</summary>
    public void BeforeLock(NeuralMotionMatchingStudioRunner r, ProceduralFootstepPlacementEngine planner)
    {
        var e = r.Engine; var ik = r.Ik; var t = r.TerrainField; const float dt = NeuralMotionMatchingStudioRunner.Dt;
        Spring(ref _w, ref _wv, Enabled && ik.OnStairs ? 1f : 0f, BlendHalfLife, dt);
        _w = Math.Clamp(_w, 0f, 1f);
        var contact = e.Contact;
        int ps = -1, pk = 0;
        for (var s = 0; s < 2; s++)
        {
            if ((contact >> s & 1) != 0)
            {
                _air[s] = 0;
                _lift[s] = Until(e, s, false, 1);
                _land[s] = _lift[s] >= Look ? Look : Until(e, s, true, _lift[s] + 1);
            }
            else
            {
                _air[s]++;
                _lift[s] = 0;
                _land[s] = Until(e, s, true, 1);
                if (ps < 0 || _land[s] < pk) { ps = s; pk = _land[s]; }
            }
        }
        if (ps < 0)
        {
            // 이중 지지: 먼저 이륙할 발의 다음 착지
            ps = _lift[0] <= _lift[1] ? 0 : 1; pk = _land[ps];
        }
        PlannedSide = ps; PlannedFrames = pk;
        var fwd = BipedSkeleton.Forward(e.Heading); var right = BipedSkeleton.Right(e.Heading);
        Nominal = PredictLanding(e, ps, pk);
        // 포착 스텝: 동역학 DCM 편차만큼 착지 이동(진행 ±15 · 횡 ±8 cm) → 그 주변 디딤판 안전 착지 계획
        var capA = Enabled ? Math.Clamp(Capture.X * fwd.X + Capture.Y * fwd.Z, -MaxCapture, MaxCapture) : 0f;
        var capL = Enabled ? Math.Clamp(Capture.X * right.X + Capture.Y * right.Z, -MaxCaptureLateral, MaxCaptureLateral) : 0f;
        var captured = Nominal + fwd * capA + right * capL;
        planner.Scan(t, e.Position, e.Heading);
        planner.Plan((byte)ps, captured, e.RootVelocity.Length());
        // 계단 보행 속도: 보폭(속도 ÷ 2·케이던스(0.8 + 0.2v)) = 디딤판 깊이 T → v = 1.6T ÷ (1 − 0.4T) · 한 칸/한 걸음(챌판 상승 1단)
        var stairsAhead = planner.EdgeCount >= 2 && planner.TreadDepth > 0.2f && planner.TreadDepth < 0.6f;
        StairSpeed = stairsAhead ? 1.6f * planner.TreadDepth / (1f - 0.4f * planner.TreadDepth) : float.PositiveInfinity;
        // 감속 레이트 제한(0.4 m/s²) — 자연 감속이 발 지지 한계를 넘는 ZMP 요구를 만들지 않도록
        var goalV = Enabled && SpeedAdapt ? StairSpeed : float.PositiveInfinity;
        if (float.IsPositiveInfinity(goalV)) _vLim = float.PositiveInfinity;
        else
        {
            if (float.IsPositiveInfinity(_vLim)) _vLim = MathF.Max(e.RootVelocity.Length(), goalV);
            _vLim = goalV < _vLim ? MathF.Max(goalV, _vLim - SpeedRamp * dt) : goalV;
        }
        r.SpeedLimit = _vLim;
        var goalA = capA + (Enabled && PlannerEnabled ? Math.Clamp(planner.FirstDelta, -MaxDelta, MaxDelta) * _w : 0f);
        NextLanding = Nominal + fwd * goalA + right * capL;
        if (!Enabled) return;

        var ankT = ik.AnkleTargets; var toeT = ik.ToeTargets;
        var sink = r.Profile.SinkM;
        var landing = planner.PlanCount > 0 ? planner.Planned[0].Position : Nominal;
        for (var s = 0; s < 2; s++)
        {
            if ((contact >> s & 1) != 0)
            {
                _syInit[s] = false;
                if (r.Guard.IsLocked(s) || (_dl[s] == 0f && _dlat[s] == 0f)) { _dl[s] = _dlV[s] = _dlat[s] = _dlatV[s] = 0f; continue; }
                // 착지 프레임(락 직전): 최종 보정 유지 → 락 위치 = 계획/포착 착지 · 착지 디딤판 재접지
                var o2 = fwd * _dl[s] + right * _dlat[s];
                ankT[s] += o2; toeT[s] += o2;
                var a2 = ankT[s];
                var g2 = MathF.Max(t.Height(a2.X, a2.Z), t.Height(a2.X - fwd.X * 0.08f, a2.Z - fwd.Z * 0.08f)) - sink;
                var d2 = g2 + BipedSkeleton.AnkleH - a2.Y;
                ankT[s].Y += d2; toeT[s].Y += d2;
                ik.SetGround(s, g2);
                continue;
            }
            Spring(ref _dl[s], ref _dlV[s], s == ps ? goalA : 0f, DeltaHalfLife, dt);
            Spring(ref _dlat[s], ref _dlatV[s], s == ps ? capL : 0f, DeltaHalfLife, dt);
            var u = SwingPhase(s);
            var su = u * u * (3f - 2f * u);
            var off = (fwd * _dl[s] + right * _dlat[s]) * su;
            ankT[s] += off; toeT[s] += off;
            // 보정 위치 지면 차(착지 디딤판) 반영
            var an = ankT[s];
            var gN = MathF.Max(t.Height(an.X, an.Z), t.Height(an.X - fwd.X * 0.08f, an.Z - fwd.Z * 0.08f)) - sink;
            var gShift = (gN - ik.GroundY[s]) * su;
            ankT[s].Y += gShift; toeT[s].Y += gShift;

            // 현재 → 착지 경로 최대 높이(뒤꿈치 · 발목 · 발끝)
            var a = ankT[s];
            var l = s == ps ? landing : a;
            var hmax = float.MinValue;
            for (var i = 0; i <= 5; i++)
            {
                var p = a + (l - a) * (i / 5f);
                hmax = MathF.Max(hmax, MathF.Max(t.Height(p.X, p.Z), MathF.Max(t.Height(p.X - fwd.X * 0.07f, p.Z - fwd.Z * 0.07f), t.Height(p.X + fwd.X * 0.17f, p.Z + fwd.Z * 0.17f))));
            }
            hmax -= sink;
            var clr = (ClearBase + ClearGain * planner.RiserHeight) * MathF.Sin(MathF.PI * Math.Clamp(u, 0f, 1f));
            var goal = a.Y + _w * MathF.Max(0f, hmax + BipedSkeleton.AnkleH + clr - a.Y);
            if (!_syInit[s]) { _sy[s] = a.Y; _syV[s] = 0f; _syInit[s] = true; }
            Spring(ref _sy[s], ref _syV[s], goal, SwingYHalfLife, dt);
            var y = MathF.Max(_sy[s], a.Y);
            var dy = y - a.Y;
            ankT[s].Y += dy; toeT[s].Y += dy;
            if (_w > 0.5f && u > 0.2f && u < 0.8f)
            {
                var tc = (toeT[s].Y - BipedSkeleton.ToeH - (t.Height(toeT[s].X, toeT[s].Z) - sink)) * 100f;
                if (tc < MinToeClearanceCm) MinToeClearanceCm = tc;
            }
        }
    }

    /// <summary>접지 락 후: 접지 발 재접지 · 발끝 롤링 · 계단 웅크림 · 착지 발자국.</summary>
    public void BeforeSolve(NeuralMotionMatchingStudioRunner r, ProceduralFootstepPlacementEngine planner)
    {
        var e = r.Engine; var ik = r.Ik; var t = r.TerrainField;
        var contact = e.Contact; var sink = r.Profile.SinkM;
        var fwd = BipedSkeleton.Forward(e.Heading); var right = BipedSkeleton.Right(e.Heading);
        var ankT = ik.AnkleTargets; var toeT = ik.ToeTargets;
        Crouch = Math.Clamp(CrouchBase + 0.4f * planner.RiserHeight, 0.08f, 0.3f);
        if (Enabled && ik.OnStairs) ik.StairCrouch = Crouch;
        for (var s = 0; s < 2; s++)
        {
            _toeOff[s] = 0f;
            if (!Enabled || (contact >> s & 1) == 0 || !r.Guard.IsLocked(s)) { _was[s] = false; continue; }
            var a = ankT[s]; var tt = toeT[s];
            var ax = BipedSkeleton.Dir(new Vector3(tt.X - a.X, 0f, tt.Z - a.Z), fwd);
            // 지지 = 발목 · 뒤꿈치(8 cm · 무릎 가드 발목 보정 여유) 최대
            var support = MathF.Max(t.Height(a.X, a.Z), t.Height(a.X - ax.X * 0.08f, a.Z - ax.Z * 0.08f)) - sink;
            if (!_was[s])
            {
                Landings++;
                var hh = t.Height(a.X - ax.X * 0.07f, a.Z - ax.Z * 0.07f); var ha = t.Height(a.X, a.Z); var ht = t.Height(a.X + ax.X * 0.17f, a.Z + ax.Z * 0.17f);
                if (MathF.Max(hh, MathF.Max(ha, ht)) - MathF.Min(hh, MathF.Min(ha, ht)) < 0.01f) FlatLandings++;
                _trail[_trailHead] = new Vector3(a.X, support, a.Z); _trailSide[_trailHead] = (byte)s;
                _trailHead = (_trailHead + 1) % Trail;
                if (_trailCount < Trail) _trailCount++;
                _was[s] = true;
            }
            a.Y = support + BipedSkeleton.AnkleH;
            var ty = MathF.Max(t.Height(tt.X, tt.Z) - sink, support - 0.08f) + BipedSkeleton.ToeH;
            tt = a + BipedSkeleton.Dir(new Vector3(tt.X, ty, tt.Z) - a, ax) * BipedSkeleton.FootLen;
            // 발끝 롤링(이륙 전 · 발끝 축 회전 → 뒤꿈치 상승)
            var kl = _lift[s];
            if (kl <= ToeOffFrames)
            {
                var k = 1f - kl / (ToeOffFrames + 1f);
                var th = ToeOffDeg * MathF.PI / 180f * k * k;
                a = tt + Vector3.Transform(a - tt, Quaternion.CreateFromAxisAngle(right, th));
                _toeOff[s] = th * 57.29578f;
            }
            ankT[s] = a; toeT[s] = tt;
            ik.SetGround(s, support);
        }
    }

    /// <summary>최종 자세 후: 접지 무릎 굴곡 계측.</summary>
    public void Observe(NeuralMotionMatchingStudioRunner r)
    {
        var c = r.Engine.Contact; var k = 0f; var n = 0;
        for (var s = 0; s < 2; s++) if ((c >> s & 1) != 0) { k += r.Guard.KneeFlexDeg(s); n++; }
        KneeFlexStanceDeg = n > 0 ? k / n : 0f;
        if (r.Ik.OnStairs && KneeFlexStanceDeg > MaxKneeFlexStanceDeg) MaxKneeFlexStanceDeg = KneeFlexStanceDeg;
    }
}
