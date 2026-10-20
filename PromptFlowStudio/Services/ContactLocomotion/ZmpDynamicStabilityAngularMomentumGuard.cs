using System.Numerics;
using PromptFlowStudio.Services.MotionMatching;

namespace PromptFlowStudio.Services.ContactLocomotion;

/// <summary>
/// [PromptFlow Studio v17.35.0] ⚖ ZMP 95%+ 동적 안정성 &amp; 각운동량 보존 가드(<see cref="BeforeSolve"/> · <see cref="AfterPost"/> · <see cref="Observe"/> 무할당).
///   ▸ 진단(v17.34 계단 61 %): 측정 ZMP = c − h/(g + ÿ)·c̈ 의 수직 가속 항 — 뒷발 도달 하강의 즉시 해제(1 프레임 17 cm) · 루트 미리보기 9점 계단화 → ÿ ±25 m/s² ·
///     DB 자연 CoM 이 발 지지로 실현 불가한 ZMP 를 요구(평지 72 % · 계단 61 %).
///   ▸ 접지 창: 측정(SG 7점 = ±3 프레임 가속 창)과 같은 창 [t−3, t+3] — 과거 접지 링 + DB 미래 접지 → 창 전체 접지 발 직사각형(측정과 동일 치수 − 1.2 cm) 볼록 껍질
///     = 허용 ZMP 영역(창 내 모든 지지 다각형의 공통 부분 → 가속 가중 평균도 내부).
///   ▸ 수직 CoM: 2차 추종 필터(ω 12 · 속도 피드포워드 = 램프 무지연 · 자연 − 3 cm · 도달 한계) → 해석 수직 가속 = LIPM ω² 와 측정 분모 정합.
///   ▸ LIPM/DCM: ω² = (g + ÿ)/h · ξ = c + ċ/ω · 기준 = DB 자연 CoM r · p* = r − r̈/ω² + (1 + k)(ξ − ξr) · k = 5 → 허용 영역 투영 → 고정 p 해석해(cosh · sinh).
///   ▸ 해밀토니안 가드: 궤도 에너지 E = ½ċ² − ½ω²(c − p)² 계측 · DCM 편차 장벽(|ξ − ξr| &gt; 12 cm → 투영 비례 완화) = 발산(넘어짐) 대신 최소 이탈 복귀 ·
///     편차 → 계단 합성기 포착 스텝(다음 착지 이동).
///   ▸ CoM 실현: IK 골반 수평 보정(전 프레임 누적) + 최종 자세 잔차 강체 이동 · 무릎 2-본 재구성(발목 고정) → 측정 CoM = 목표(서브 mm).
///   ▸ 각운동량: 중심 각운동량 L = Σ mᵢ(rᵢ − c) × (vᵢ − ċ) · 팔 흔들림 = 하체/체간 요 각운동량 최소제곱 상쇄 이득(0.6 ~ 1.8 · 어깨 시상면 회전) ·
///     상체 전경 = ZMP 포화 부족분 중력 토크(20 % · ±5 cm)를 체간 피치 각가속으로 흡수(고관절 전략 · ±6° · 복원 ω 4) — 질량 이동은 CoM 실현이 상쇄.
/// </summary>
public sealed class ZmpDynamicStabilityAngularMomentumGuard
{
    public const int Win = 3, Series = 240, Seg = 14;
    public const float G = 9.81f, Margin = 0.012f, VertOmega = 12f, VertMargin = 0.03f, MaxCorr = 0.45f, ResetDev = 0.5f, DcmBound = 0.12f,
        MaxPitchDeg = 6f, PitchOmega = 4f, TorsoInertia = 4.5f, HipShare = 0.2f;
    private static readonly int[] Ankle = { 6, 10 }, Toe = { 7, 11 };

    private readonly byte[] _ctHist = new byte[Win];
    private readonly Vector3[] _segPrev = new Vector3[Seg];
    private readonly float[] _ratioS = new float[Series], _offS = new float[Series];
    private readonly bool[] _inWin = new bool[60];
    private Vector2 _c, _v, _rPrev, _rv, _ra, _rvPrev;
    private float _y, _yv, _yNPrev, _yNv;
    private Vector3 _corr, _comPrev;
    private bool _init, _segInit;
    private int _hist, _seriesHead, _seriesCount, _winHead, _winIn, _winN;
    private float _armGain = 1f, _armGainV, _pitch, _pitchV, _lbLa, _laLa, _lyRaw2, _lyAdj2;

    public bool Enabled { get; set; } = true;
    public bool AngularMomentumEnabled { get; set; } = true;
    public float TrackGain { get; set; } = 5f;
    /// <summary>IK 루트 지면 미리보기 편측 샘플(16 = 33점 · 단차 통과 지면 계단 1.7 → 0.5 cm).</summary>
    public int PreviewSamples { get; set; } = 16;
    public Vector2 TargetCom => _c;
    public Vector2 ComVelocity => _v;
    public float SmoothY => _y;
    public float VerticalAcc { get; private set; }
    public Vector2 Zmp { get; private set; }
    public Vector2 ZmpDesired { get; private set; }
    /// <summary>DCM 편차(동역학 − 자연 · 월드 xz) → 포착 스텝 착지 이동.</summary>
    public Vector2 CaptureOffset { get; private set; }
    public bool Saturated { get; private set; }
    public byte WindowFeet { get; private set; }
    public float Omega { get; private set; }
    public float OrbitalEnergy { get; private set; }
    public float ResidualMm { get; private set; }
    public float MaxResidualMm { get; private set; }
    public Vector3 Correction => _corr;
    public float ArmGain => _armGain;
    public float TorsoPitchDeg => _pitch * 57.29578f;
    public Vector3 AngularMomentum { get; private set; }
    /// <summary>요 각운동량 상쇄율(1 − rms(조정 팔) ÷ rms(DB 팔)).</summary>
    public float YawCancel => _lyRaw2 > 1e-9f ? 1f - MathF.Sqrt(_lyAdj2 / _lyRaw2) : 0f;
    public long Frames { get; private set; }
    public long SaturatedFrames { get; private set; }
    public long BarrierFrames { get; private set; }
    public long VertClamps { get; private set; }
    public long Resets { get; private set; }
    public long StairFrames { get; private set; }
    public long StairInside { get; private set; }
    public long FlatFrames { get; private set; }
    public long FlatInside { get; private set; }
    public long SingleSupportStair { get; private set; }
    public long SingleSupportStairInside { get; private set; }
    public float StairMaxOutsideCm { get; private set; }
    public float StairRatio => StairFrames > 0 ? StairInside / (float)StairFrames : 0f;
    public float FlatRatio => FlatFrames > 0 ? FlatInside / (float)FlatFrames : 0f;
    public float SingleSupportRatio => SingleSupportStair > 0 ? SingleSupportStairInside / (float)SingleSupportStair : 0f;
    /// <summary>최근 1 s ZMP 지지 내 비율.</summary>
    public float RecentRatio => _winN > 0 ? _winIn / (float)_winN : 0f;
    public double LastUs { get; private set; }
    public double MaxUs { get; private set; }
    public int SeriesCount => _seriesCount;
    public float RatioSeries(int i) => _ratioS[(_seriesHead - 1 - i + Series * 2) % Series];
    public float OffsetSeries(int i) => _offS[(_seriesHead - 1 - i + Series * 2) % Series];

    public void Reset() { _init = false; _segInit = false; _corr = Vector3.Zero; _hist = 0; _armGain = 1f; _armGainV = 0f; _pitch = _pitchV = 0f; Frames = 0; }

    public void ResetMetrics()
    {
        SaturatedFrames = BarrierFrames = Resets = VertClamps = StairFrames = StairInside = FlatFrames = FlatInside = SingleSupportStair = SingleSupportStairInside = 0;
        StairMaxOutsideCm = 0f; MaxResidualMm = 0f; MaxUs = 0; _winHead = _winIn = _winN = 0; _seriesCount = 0; _lyRaw2 = _lyAdj2 = 0f;
    }

    /// <summary>IK 해 전: 외부 CoM 제어 결선 · 전 프레임 누적 골반 보정 입력.</summary>
    public void BeforeSolve(TerrainAdaptiveBiomechanicsFullBodyIkSynthesizer ik)
    {
        ik.ComControl = Enabled;
        ik.PreviewSamples = Enabled ? PreviewSamples : 4;
        if (!Enabled) { _init = false; _corr = Vector3.Zero; ik.ComCorrection = Vector3.Zero; return; }
        ik.ComCorrection = _corr;
    }

    private static Vector2 XZ(Vector3 v) => new(v.X, v.Z);

    /// <summary>최종 자세(무릎 가드 후): 접지 창 허용 영역 → 수직 평활 → LIPM/DCM → 각운동량 → CoM 실현.</summary>
    public void AfterPost(NeuralMotionMatchingStudioRunner r)
    {
        var ik = r.Ik; var e = r.Engine;
        var contact = e.Contact;
        if (!Enabled) { Push(contact); return; }
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        const float dt = NeuralMotionMatchingStudioRunner.Dt;
        var pose = ik.Pose;
        var right = BipedSkeleton.Right(e.Heading); var fwd = BipedSkeleton.Forward(e.Heading);
        var com = BipedSkeleton.CenterOfMass(pose);
        var rN = XZ(com) - XZ(_corr) * 0.86f;   // 자연 CoM(보정 제외 근사)

        // 접지 창 [t−3, t+3] 공통 발
        byte feet = contact;
        for (var i = 0; i < Math.Min(_hist, Win); i++) feet &= _ctHist[i];
        for (var k = 1; k <= Win; k++) feet &= e.FutureContact(k);
        if (feet == 0) feet = contact;
        WindowFeet = feet;

        // 허용 영역(발 직사각형 − 여유 → 볼록 껍질)
        Span<Vector2> pts = stackalloc Vector2[8];
        Span<Vector2> hull = stackalloc Vector2[9];
        var np = 0;
        var gy = 0f; var ng = 0;
        for (var s = 0; s < 2; s++)
        {
            if ((contact >> s & 1) != 0) { gy += ik.GroundY[s]; ng++; }
            if ((feet >> s & 1) == 0) continue;
            var a = pose[Ankle[s]]; var t = pose[Toe[s]];
            var ax = BipedSkeleton.Dir(new Vector3(t.X - a.X, 0f, t.Z - a.Z), fwd);
            var sd = new Vector3(ax.Z, 0f, -ax.X) * (0.045f - Margin);
            var heel = a - ax * (0.07f - Margin); var tip = t + ax * (0.03f - Margin);
            pts[np++] = new Vector2(heel.X + sd.X, heel.Z + sd.Z); pts[np++] = new Vector2(heel.X - sd.X, heel.Z - sd.Z);
            pts[np++] = new Vector2(tip.X + sd.X, tip.Z + sd.Z); pts[np++] = new Vector2(tip.X - sd.X, tip.Z - sd.Z);
        }
        var hn = np >= 3 ? TerrainAdaptiveBiomechanicsFullBodyIkSynthesizer.ConvexHull(pts[..np], hull) : 0;

        // 수직 CoM: 2차 추종 필터 ÿ = −ω²(y − g) − 2ω(ẏ − ġ) · 예상 수평 이동 후 도달 한계
        var yN = com.Y;
        if (!_init) { _y = yN; _yv = 0f; _yNPrev = yN; _yNv = 0f; }
        var yvPrev = _yv;
        _yNv += ((yN - _yNPrev) / dt - _yNv) * 0.25f; _yNPrev = yN;
        _yv += (-VertOmega * VertOmega * (_y - (yN - VertMargin)) - 2f * VertOmega * (_yv - _yNv)) * dt;
        _y += _yv * dt;
        var raise = float.MaxValue;
        var reach = FootSkatingSuppressionJointHyperExtensionGuard.MaxReach;
        var hs = new Vector3(_c.X + _v.X * dt - com.X, 0f, _c.Y + _v.Y * dt - com.Z) / 0.86f;
        for (var s = 0; s < 2; s++)
        {
            var v = pose[Ankle[s]] - pose[s == 0 ? 4 : 8] - hs;
            var xz2 = v.X * v.X + v.Z * v.Z;
            if (xz2 < reach * reach) raise = MathF.Min(raise, v.Y + MathF.Sqrt(reach * reach - xz2));
        }
        if (_y > yN + raise * 0.86f) { _y = yN + raise * 0.86f; _yv = MathF.Min(_yv, yvPrev); VertClamps++; }
        var ya = (_yv - yvPrev) / dt;
        VerticalAcc = ya;
        gy = ng > 0 ? gy / ng : _y - 0.9f;
        var h = MathF.Max(_y - gy, 0.4f);
        var w2 = MathF.Max(G + ya, 2f) / h; var w = MathF.Sqrt(w2);
        Omega = w;

        // 자연 CoM 미분(저역 통과) · 초기화/발산 재동기
        if (!_init) { _c = rN; _v = Vector2.Zero; _rPrev = rN; _rv = _rvPrev = _ra = Vector2.Zero; _init = true; }
        var rvRaw = (rN - _rPrev) / dt; _rPrev = rN;
        _rv += (rvRaw - _rv) * 0.35f;
        var raRaw = (_rv - _rvPrev) / dt; _rvPrev = _rv;
        _ra += (raRaw - _ra) * 0.3f;
        if (_ra.Length() > 8f) _ra *= 8f / _ra.Length();
        if (Frames == 0 || (_c - rN).Length() > ResetDev) { _c = rN; _v = _rv; Resets++; }

        // DCM 추종: 자연 ZMP + (1 + k)(ξ − ξr)
        var xi = _c + _v / w;
        CaptureOffset = xi - (rN + _rv / w);
        var pd = (contact & 3) == 0 ? _c : rN - _ra / w2 + (1f + TrackGain) * CaptureOffset;
        ZmpDesired = pd;
        var p = pd;
        Saturated = false;
        if (hn >= 3)
        {
            var d = TerrainAdaptiveBiomechanicsFullBodyIkSynthesizer.SignedDistance(hull[..hn], pd, out var closest);
            if (d > 0f)
            {
                p = closest; Saturated = true; SaturatedFrames++;
                // DCM 편차 장벽: |ξ − ξr| 이 경계 B 초과 시 투영 비례 완화(λ = (|e| − B)/B) — 발산 대신 최소 이탈 복귀
                var lam = Math.Clamp((CaptureOffset.Length() - DcmBound) / DcmBound, 0f, 1f);
                if (lam > 0f) { p += (pd - p) * lam; BarrierFrames++; }
            }
        }
        Zmp = p;

        // 고관절 전략: 포화 부족분(진행축) 중력 토크 → 체간 피치 각가속
        if (AngularMomentumEnabled)
        {
            var shortfall = Vector2.Dot(pd - p, new Vector2(fwd.X, fwd.Z));
            var acc = -HipShare * BipedSkeleton.BodyMass * G * Math.Clamp(shortfall, -0.05f, 0.05f) / TorsoInertia - PitchOmega * PitchOmega * _pitch - 2f * PitchOmega * _pitchV;
            _pitchV += acc * dt; _pitch += _pitchV * dt;
            var lim = MaxPitchDeg * MathF.PI / 180f;
            if (MathF.Abs(_pitch) > lim) { _pitch = MathF.Sign(_pitch) * lim; _pitchV = 0f; }
        }

        // LIPM 해석해(고정 p) · 궤도 에너지
        var ch = MathF.Cosh(w * dt); var sh = MathF.Sinh(w * dt);
        var x = _c - p;
        _c = p + x * ch + _v * (sh / w);
        _v = x * (w * sh) + _v * ch;
        OrbitalEnergy = 0.5f * _v.LengthSquared() - 0.5f * w2 * (_c - p).LengthSquared();

        // 각운동량(팔 요 상쇄 · 체간 피치) — 자연 CoM 산출 후 적용(질량 이동은 CoM 실현이 흡수)
        if (AngularMomentumEnabled) AngularStage(pose, right, dt);

        // CoM 실현(최종 자세 잔차 강체 이동 · 수직 포함)
        var shift = ik.ApplyComShift(new Vector3(_c.X, _y, _c.Y), true);
        _corr += new Vector3(shift.X, 0f, shift.Z);
        var cl = new Vector2(_corr.X, _corr.Z).Length();
        if (cl > MaxCorr) _corr *= MaxCorr / cl;
        var fin = BipedSkeleton.CenterOfMass(ik.Pose);
        ResidualMm = new Vector3(fin.X - _c.X, fin.Y - _y, fin.Z - _c.Y).Length() * 1000f;
        if (ResidualMm > MaxResidualMm && Frames > 10) MaxResidualMm = ResidualMm;
        Frames++;
        Push(contact);
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        LastUs = us; if (us > MaxUs) MaxUs = us;
    }

    private void Push(byte contact)
    {
        for (var i = Win - 1; i > 0; i--) _ctHist[i] = _ctHist[i - 1];
        _ctHist[0] = contact;
        if (_hist < Win) _hist++;
    }

    /// <summary>중심 각운동량 · 팔 요 상쇄 이득(최소제곱) · 체간 피치 적용.</summary>
    private void AngularStage(Span<Vector3> pose, Vector3 right, float dt)
    {
        var segA = BipedSkeleton.SegmentA; var segB = BipedSkeleton.SegmentB; var segM = BipedSkeleton.SegmentMass;
        var com = BipedSkeleton.CenterOfMass(pose);
        if (!_segInit)
        {
            for (var i = 0; i < Seg; i++) _segPrev[i] = 0.5f * (pose[segA[i]] + pose[segB[i]]);
            _comPrev = com; _segInit = true;
        }
        var vc = (com - _comPrev) / dt;
        Vector3 lb = Vector3.Zero, la = Vector3.Zero;
        for (var i = 0; i < Seg; i++)
        {
            var m = 0.5f * (pose[segA[i]] + pose[segB[i]]);
            var v = (m - _segPrev[i]) / dt - vc;
            var l = BipedSkeleton.BodyMass * segM[i] * Vector3.Cross(m - com, v);
            if (segA[i] >= 12) la += l; else lb += l;
            _segPrev[i] = m;
        }
        _comPrev = com;
        AngularMomentum = lb + la;
        // 팔 요 상쇄: L_y(g) ≈ Lb + g·La(DB 원 자세) → g* = −E[Lb·La] ÷ E[La²](지수 이동 평균 · 0.6 ~ 1.8 · 2차 평활)
        _lbLa += (lb.Y * la.Y - _lbLa) * 0.01f;
        _laLa += (la.Y * la.Y - _laLa) * 0.01f;
        var gStar = _laLa > 1e-6f ? Math.Clamp(-_lbLa / _laLa, 0.6f, 1.8f) : 1f;
        _armGainV += ((gStar - _armGain) * 4f - _armGainV * 4f) * dt;
        _armGain = Math.Clamp(_armGain + _armGainV * dt, 0.6f, 1.8f);
        var raw = lb.Y + la.Y; var adj = lb.Y + _armGain * la.Y;
        _lyRaw2 += (raw * raw - _lyRaw2) * 0.01f;
        _lyAdj2 += (adj * adj - _lyAdj2) * 0.01f;

        // 팔: 어깨 기준 시상면 스윙각 × 이득
        var fw = Vector3.Cross(right, Vector3.UnitY);
        for (var s = 0; s < 2; s++)
        {
            var sh = s == 0 ? 12 : 15;
            var u = pose[sh + 1] - pose[sh];
            var ang = MathF.Atan2(Vector3.Dot(u, fw), -u.Y);
            var q = Quaternion.CreateFromAxisAngle(right, -(_armGain - 1f) * ang);
            pose[sh + 1] = pose[sh] + Vector3.Transform(pose[sh + 1] - pose[sh], q);
            pose[sh + 2] = pose[sh] + Vector3.Transform(pose[sh + 2] - pose[sh], q);
        }
        // 체간 피치(골반 축)
        if (MathF.Abs(_pitch) > 1e-5f)
        {
            var q = Quaternion.CreateFromAxisAngle(right, _pitch);
            var pel = pose[0];
            for (var j = 1; j < 4; j++) pose[j] = pel + Vector3.Transform(pose[j] - pel, q);
            for (var j = 12; j < BipedSkeleton.Count; j++) pose[j] = pel + Vector3.Transform(pose[j] - pel, q);
        }
    }

    /// <summary>동역학 측정 후: 계단/평지 · 단일 지지 ZMP 지지 내 비율 · 최근 1 s 시계열.</summary>
    public void Observe(TerrainAdaptiveBiomechanicsFullBodyIkSynthesizer ik, ITerrainHeightField terrain, Vector3 root, float heading, byte contact)
    {
        if (!ik.ZmpValid) return;
        var inside = ik.ZmpOffset <= 0.01f;
        var stairs = ik.OnStairs || ik.DetectSteps(terrain, root, heading) > ik.StairStepMin;
        if (stairs)
        {
            StairFrames++; if (inside) StairInside++;
            if (ik.ZmpOffset * 100f > StairMaxOutsideCm) StairMaxOutsideCm = ik.ZmpOffset * 100f;
            if ((contact & 3) != 3) { SingleSupportStair++; if (inside) SingleSupportStairInside++; }
        }
        else { FlatFrames++; if (inside) FlatInside++; }
        if (_winN == _inWin.Length) { if (_inWin[_winHead]) _winIn--; } else _winN++;
        _inWin[_winHead] = inside; if (inside) _winIn++;
        _winHead = (_winHead + 1) % _inWin.Length;
        _ratioS[_seriesHead] = RecentRatio; _offS[_seriesHead] = ik.ZmpOffset;
        _seriesHead = (_seriesHead + 1) % Series;
        if (_seriesCount < Series) _seriesCount++;
    }
}
