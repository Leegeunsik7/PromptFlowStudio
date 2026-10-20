using System.Numerics;

namespace PromptFlowStudio.Services.MotionMatching;

/// <summary>
/// [PromptFlow Studio v17.33.0] 🛡 발 미끄러짐 차단 &amp; 관절 과신전 방어 가드(<see cref="Sanitize"/> · <see cref="LockFeet"/> · <see cref="Post"/> 무할당).
///   ▸ 뼈 길이 복원: 관성 블렌딩(위치 오프셋) 후 부모→자식 순 방향 보존 · 기준 길이 재투영(전 · 후 오차 mm).
///   ▸ 팔꿈치 ROM: 후방 꺾임(굴곡 평면 법선 · 우측축 부호) → 상완-우측 평면 거울 반사 · 굴곡 &gt; 150° → 상완 쪽 회전 개방.
///   ▸ 접지 락: DB 접지 위상 ∧ 발목 지면 높이 &lt; 발목고 + 5 cm → 진입 시 월드 xz 고정(발끝 포함 · y 는 지면 정렬 추종) ·
///     위상 종료 · 도달 초과(최소 굴곡 도달 × 1.15 — IK 골반 하강 여유) → 해제 오프셋 임계 감쇠(반감기 60 ms)로 무팝 복귀.
///   ▸ 무릎: 굴곡 부호(극 벡터 기준 후방 = 음수) &lt; 4° → 과신전 · 150° 초과 · 각속도 &gt; 20 rad/s 급발산(접지 발은 굴곡 방향만) → 클램프 후 코사인 법칙 도달 거리로 발목 · 2-본 IK 무릎 재구성.
///   ▸ 지표: 접지 중 발목 수평 미끄럼(cm/s) · 락/해제 · 과신전 검출/교정/잔존 · 각속도 최대(전/후) · 뼈 길이 오차.
/// </summary>
public sealed class FootSkatingSuppressionJointHyperExtensionGuard
{
    public const int Count = BipedSkeleton.Count;
    public const float KneeMinFlexDeg = 4f, KneeMaxFlexDeg = 150f, ElbowMaxFlexDeg = 150f, MaxKneeOmega = 20f, LockHeight = 0.05f, ReleaseHalfLife = 0.06f, ReachSlack = 1.15f;
    public static readonly float MaxReach = BipedSkeleton.MaxReach(KneeMinFlexDeg);
    private const float MinFlex = KneeMinFlexDeg * MathF.PI / 180f, MaxFlex = KneeMaxFlexDeg * MathF.PI / 180f, ElbowMax = ElbowMaxFlexDeg * MathF.PI / 180f;

    private static readonly int[] Hip = { 4, 8 }, Knee = { 5, 9 }, Ankle = { 6, 10 }, Toe = { 7, 11 };
    private readonly bool[] _locked = new bool[2], _hasPrev = new bool[2], _hasFlex = new bool[2];
    private readonly Vector3[] _lock = new Vector3[2], _lockToe = new Vector3[2], _relX = new Vector3[2], _relV = new Vector3[2], _prevAnkle = new Vector3[2];
    private readonly float[] _prevFlex = new float[2], _flexDeg = new float[2];
    private double _skate, _contactTime;

    public bool Enabled { get; set; } = true;
    public bool IsLocked(int s) => _locked[s];
    public Vector3 LockPosition(int s) => _lock[s];
    public float KneeFlexDeg(int s) => _flexDeg[s];
    public long Locks { get; private set; }
    public long Releases { get; private set; }
    public long ReachReleases { get; private set; }
    public float SkatingCmPerSec => _contactTime > 0 ? (float)(100.0 * _skate / _contactTime) : 0f;
    public float RecentSkatingCmPerSec { get; private set; }
    public long HyperExtDetected { get; private set; }
    public long HyperExtClamped { get; private set; }
    public long HyperExtRemaining { get; private set; }
    public long FlexLimitClamps { get; private set; }
    public long OmegaClamps { get; private set; }
    public float MaxOmegaPre { get; private set; }
    public float MaxOmegaPost { get; private set; }
    public float BoneErrPreMm { get; private set; }
    public float MaxBoneErrPreMm { get; private set; }
    public float BoneErrPostMm { get; private set; }
    public float MaxBoneErrPostMm { get; private set; }
    public long ElbowFixes { get; private set; }

    public void Reset()
    {
        Array.Clear(_locked); Array.Clear(_hasPrev); Array.Clear(_hasFlex); Array.Clear(_relX); Array.Clear(_relV);
        ResetMetrics();
    }

    public void ResetMetrics()
    {
        _skate = _contactTime = 0; RecentSkatingCmPerSec = 0f;
        Locks = Releases = ReachReleases = HyperExtDetected = HyperExtClamped = HyperExtRemaining = FlexLimitClamps = OmegaClamps = ElbowFixes = 0;
        MaxOmegaPre = MaxOmegaPost = MaxBoneErrPreMm = MaxBoneErrPostMm = 0f;
    }

    /// <summary>로컬 자세 뼈 길이 복원 · 팔꿈치 ROM(가드 OFF = 측정만).</summary>
    public void Sanitize(Span<Vector3> local)
    {
        var err = BoneError(local);
        BoneErrPreMm = err * 1000f;
        if (BoneErrPreMm > MaxBoneErrPreMm) MaxBoneErrPreMm = BoneErrPreMm;
        if (!Enabled) return;
        for (var j = 1; j < Count; j++)
        {
            var p = BipedSkeleton.Parent[j];
            local[j] = local[p] + BipedSkeleton.Dir(local[j] - local[p], -Vector3.UnitY) * BipedSkeleton.BoneLength[j];
        }
        for (var s = 0; s < 2; s++)
        {
            var sh = s == 0 ? 12 : 15;
            var u = local[sh + 1] - local[sh]; var f = local[sh + 2] - local[sh + 1];
            var n = Vector3.Cross(u, f);
            if (Vector3.Dot(n, Vector3.UnitX) > 1e-5f)
            {
                var pn = BipedSkeleton.Dir(Vector3.Cross(u, Vector3.UnitX), Vector3.UnitZ);
                f -= 2f * Vector3.Dot(f, pn) * pn;
                ElbowFixes++;
            }
            var ang = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(u), BipedSkeleton.Dir(f, -Vector3.UnitY)), -1f, 1f));
            if (ang > ElbowMax)
            {
                var axis = BipedSkeleton.Dir(Vector3.Cross(u, f), Vector3.UnitX);
                f = Vector3.Transform(f, Quaternion.CreateFromAxisAngle(axis, -(ang - ElbowMax)));
                ElbowFixes++;
            }
            local[sh + 2] = local[sh + 1] + f;
        }
    }

    public static float BoneError(ReadOnlySpan<Vector3> p)
    {
        var err = 0f;
        for (var j = 1; j < Count; j++) err = MathF.Max(err, MathF.Abs((p[j] - p[BipedSkeleton.Parent[j]]).Length() - BipedSkeleton.BoneLength[j]));
        return err;
    }

    private static void Decay(ref Vector3 x, ref Vector3 v, float hl, float dt)
    {
        var y = TrajectoryController.Damping(hl) * 0.5f;
        var j1 = v + x * y; var e = MathF.Exp(-y * dt);
        x = e * (x + j1 * dt); v = e * (v - j1 * y * dt);
    }

    /// <summary>접지 락 · 해제 블렌딩 → 발목/발끝 목표 수정(IK 입력).</summary>
    public void LockFeet(Span<Vector3> ankleT, Span<Vector3> toeT, ReadOnlySpan<float> groundY, ReadOnlySpan<Vector3> animWorld, byte contact, float dt)
    {
        for (var s = 0; s < 2; s++)
        {
            Decay(ref _relX[s], ref _relV[s], ReleaseHalfLife, dt);
            if (!Enabled) { _locked[s] = false; _relX[s] = _relV[s] = Vector3.Zero; continue; }
            var c = (contact >> s & 1) != 0 && ankleT[s].Y - groundY[s] < BipedSkeleton.AnkleH + LockHeight;
            var a0 = ankleT[s]; var t0 = toeT[s];
            if (c && !_locked[s])
            {
                _locked[s] = true; Locks++;
                _lock[s] = a0 + _relX[s]; _lockToe[s] = t0 + _relX[s];
                _relX[s] = _relV[s] = Vector3.Zero;
            }
            if (_locked[s])
            {
                var a = new Vector3(_lock[s].X, a0.Y, _lock[s].Z);
                var t = new Vector3(_lockToe[s].X, t0.Y, _lockToe[s].Z);
                var reach = (a - animWorld[Hip[s]]).Length();
                if (!c || reach > MaxReach * ReachSlack)
                {
                    _locked[s] = false; Releases++;
                    if (c) ReachReleases++;
                    _relX[s] = a - a0; _relV[s] = Vector3.Zero;
                }
                ankleT[s] = a; toeT[s] = t;
            }
            else { ankleT[s] = a0 + _relX[s]; toeT[s] = t0 + _relX[s]; }
        }
    }

    /// <summary>최종 자세: 무릎 과신전 · ROM · 각속도 클램프 → 재구성 · 미끄럼/뼈 길이 측정.</summary>
    public void Post(Span<Vector3> w, ReadOnlySpan<Vector3> poles, byte contact, float dt)
    {
        for (var s = 0; s < 2; s++)
        {
            var hip = w[Hip[s]]; var knee = w[Knee[s]]; var ankle = w[Ankle[s]];
            var flex = MathF.PI - MathF.Acos(Math.Clamp(Vector3.Dot(BipedSkeleton.Dir(hip - knee, Vector3.UnitY), BipedSkeleton.Dir(ankle - knee, -Vector3.UnitY)), -1f, 1f));
            var axis = BipedSkeleton.Dir(ankle - hip, -Vector3.UnitY);
            var perp = knee - hip - axis * Vector3.Dot(knee - hip, axis);
            if (perp.LengthSquared() > 1e-10f && Vector3.Dot(perp, poles[s]) < 0f) flex = -flex;
            var hyper = flex < MinFlex - 1e-4f;
            if (hyper) HyperExtDetected++;
            var omega = _hasFlex[s] ? MathF.Abs(flex - _prevFlex[s]) / dt : 0f;
            if (omega > MaxOmegaPre) MaxOmegaPre = omega;
            var target = flex;
            if (Enabled)
            {
                target = Math.Clamp(target, MinFlex, MaxFlex);
                if (target != flex && !hyper) FlexLimitClamps++;
                if (_hasFlex[s])
                {
                    // 각속도 제한 — 접지 발은 굴곡(발 들림) 방향만 허용: 신전 방향 제한은 발목을 지면 아래로 밀어 관통 유발
                    var lim = MaxKneeOmega * dt;
                    var d = target - _prevFlex[s];
                    if (MathF.Abs(d) > lim)
                    {
                        var limited = Math.Clamp(_prevFlex[s] + MathF.Sign(d) * lim, MinFlex, MaxFlex);
                        if ((contact >> s & 1) == 0 || limited > target) { target = limited; OmegaClamps++; }
                    }
                }
                if (MathF.Abs(target - flex) > 1e-5f)
                {
                    var reach = MathF.Sqrt(BipedSkeleton.Thigh * BipedSkeleton.Thigh + BipedSkeleton.Shin * BipedSkeleton.Shin + 2f * BipedSkeleton.Thigh * BipedSkeleton.Shin * MathF.Cos(target));
                    var na = hip + axis * reach;
                    var nk = BipedSkeleton.TwoBone(hip, na, poles[s], BipedSkeleton.Thigh, BipedSkeleton.Shin);
                    var shift = na - ankle;
                    w[Knee[s]] = nk; w[Ankle[s]] = na; w[Toe[s]] += shift;
                    if (hyper) HyperExtClamped++;
                }
            }
            if (target < MinFlex - 1e-3f) HyperExtRemaining++;
            var post = _hasFlex[s] ? MathF.Abs(target - _prevFlex[s]) / dt : 0f;
            if (post > MaxOmegaPost) MaxOmegaPost = post;
            _prevFlex[s] = target; _hasFlex[s] = true;
            _flexDeg[s] = target * 57.29578f;
        }

        var frame = 0f; var ct = 0f;
        for (var s = 0; s < 2; s++)
        {
            var a = w[Ankle[s]];
            if ((contact >> s & 1) != 0 && _hasPrev[s])
            {
                var d = new Vector2(a.X - _prevAnkle[s].X, a.Z - _prevAnkle[s].Z).Length();
                _skate += d; _contactTime += dt;
                frame += d; ct += dt;
            }
            _prevAnkle[s] = a; _hasPrev[s] = true;
        }
        if (ct > 0f) RecentSkatingCmPerSec += 0.05f * (100f * frame / ct - RecentSkatingCmPerSec);

        var be = BoneError(w);
        BoneErrPostMm = be * 1000f;
        if (BoneErrPostMm > MaxBoneErrPostMm) MaxBoneErrPostMm = BoneErrPostMm;
    }
}
