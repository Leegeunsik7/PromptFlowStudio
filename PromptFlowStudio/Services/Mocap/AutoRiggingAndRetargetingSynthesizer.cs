using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.Mocap;

/// <summary>캐릭터 리그(레스트 전역 위치 · 부모 기준 오프셋 · 스킨 가중치 4본/정점).</summary>
public sealed class CharacterRig
{
    public string Name = "rig";
    public readonly Vector3[] Rest = new Vector3[MocapSkeleton.Body];
    public readonly Vector3[] Offset = new Vector3[MocapSkeleton.Body];
    public Vector3 Min, Max;
    public Vector3[] Vertices = Array.Empty<Vector3>();
    public int[] BoneIndex = Array.Empty<int>();
    public float[] BoneWeight = Array.Empty<float>();

    public float Height => Max.Y - Min.Y;
    public float Ground => Min.Y;
    /// <summary>골반 높이(지면 기준) — 루트 이동 스케일.</summary>
    public float PelvisHeight => Rest[MocapSkeleton.Pelvis].Y - Min.Y;
    public float BoneLength(int j) => Offset[j].Length();

    public void FromRest()
    {
        for (var j = 0; j < MocapSkeleton.Body; j++)
        {
            var p = MocapSkeleton.Parent[j];
            Offset[j] = p < 0 ? Rest[j] : Rest[j] - Rest[p];
        }
    }
}

/// <summary>
/// [PromptFlow Studio v16.75.0] 🦴 자율 스켈레톤 오토 리깅 &amp; 모션 리타겟팅 합성기.
///   ▸ 오토 리깅(T-포즈 · Y-up · +Z 정면 메시 점군): 팔 끝/어깨 높이 · 겨드랑이 아래 몸통 폭 · 다리 사이 틈이 닫히는 가랑이 높이 · 다리 군집 · 발 앞/뒤꿈치 · 머리 꼭대기 랜드마크 →
///     해부학 비례로 24관절 배치(머리 크기 · 다리/팔 길이 · 체폭이 다른 이종 체형 대응) · 뼈 선분 거리 역4제곱 상위 4본 스킨 가중치 · LBS 스키닝.
///   ▸ 리타겟팅(무할당): 소스 3D 관절 → 전역 회전(골반·가슴 = 좌우축+상향축 프레임, 그 외 = 레스트 뼈 방향 최소 스윙) → 로컬 →
///     관절 한계(Hinge = 축 각도 클램프 · Ball = 스윙-트위스트 분해 원뿔/비틀림 클램프) → 표정·제스처 가산 회전 → 대상 뼈 길이 FK · 루트 이동은 골반 높이 비율 스케일.
/// </summary>
public sealed class AutoRiggingAndRetargetingSynthesizer
{
    private const int B = MocapSkeleton.Body;
    private readonly object _gate = new();
    private readonly Quaternion[] _srcGlobal = new Quaternion[B], _local = new Quaternion[B], _outGlobal = new Quaternion[B];
    private readonly Vector3[] _outPos = new Vector3[B];
    private CharacterRig _target = ReferenceRig(1f);
    private int _clamped;
    private long _frames;

    public event Action? OnChange;

    public CharacterRig Target { get => _target; set { lock (_gate) _target = value; Raise(); } }
    public bool EnforceLimits { get; set; } = true;
    public int ClampedJoints => _clamped;
    public long Frames => Interlocked.Read(ref _frames);
    public ReadOnlySpan<Quaternion> Local => _local;
    public ReadOnlySpan<Quaternion> Global => _outGlobal;
    public ReadOnlySpan<Vector3> Output => _outPos;
    public string? LastAction { get; private set; }

    /// <summary>기준 골격 리그(균일 스케일 · 레그/암 배율). 메시 없음.</summary>
    public static CharacterRig ReferenceRig(float scale, float legScale = 1f, float armScale = 1f, float headScale = 1f)
    {
        var rig = new CharacterRig { Name = "reference" };
        Span<Vector3> off = stackalloc Vector3[B];
        for (var j = 0; j < B; j++)
        {
            var o = MocapSkeleton.RestOffset[j] * scale;
            if (j is MocapSkeleton.LKnee or MocapSkeleton.RKnee or MocapSkeleton.LAnkle or MocapSkeleton.RAnkle) o *= legScale;
            if (j is MocapSkeleton.LElbow or MocapSkeleton.RElbow or MocapSkeleton.LWrist or MocapSkeleton.RWrist or MocapSkeleton.LHand or MocapSkeleton.RHand) o *= armScale;
            if (j == MocapSkeleton.Head) o *= headScale;
            off[j] = o;
        }
        var legDelta = (0.41f + 0.42f) * scale * (legScale - 1f);
        off[0] = new Vector3(0, 0.95f * scale + legDelta, 0);
        MocapSkeleton.RestPositions(off, rig.Rest);
        rig.FromRest();
        rig.Min = new Vector3(-1, 0, -1); rig.Max = new Vector3(1, rig.Rest[MocapSkeleton.Head].Y + 0.12f * scale * headScale, 1);
        return rig;
    }

    // ══════════════════════ 오토 리깅(제어 평면) ══════════════════════

    public CharacterRig AutoRig(ReadOnlySpan<Vector3> v, string name)
    {
        if (v.Length < 64) throw new ArgumentException("정점이 너무 적습니다(≥64).", nameof(v));
        Vector3 mn = new(float.MaxValue), mx = new(float.MinValue);
        foreach (var p in v) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
        var H = mx.Y - mn.Y; var g = mn.Y;
        float tipL = mx.X, tipR = mn.X;
        // 팔 높이(양 팔 끝 15% 구간 평균)
        double ay = 0, az = 0; var an = 0;
        foreach (var p in v) if (p.X > tipL * 0.85f || p.X < tipR * 0.85f) { ay += p.Y; az += p.Z; an++; }
        var armY = (float)(ay / Math.Max(1, an)); var armZ = (float)(az / Math.Max(1, an));
        // 겨드랑이 아래 몸통 반폭
        var torsoHalf = 0f;
        foreach (var p in v) if (p.Y > armY - 0.14f * H && p.Y < armY - 0.07f * H) torsoHalf = MathF.Max(torsoHalf, MathF.Abs(p.X));
        var shoulderX = torsoHalf + 0.03f * H;   // 어깨 관절은 몸통 외곽보다 약간 바깥(삼각근 내부)
        // 가랑이: 중앙 열(|x| < 0.008H)이 처음 채워지는 높이
        var crotch = g + 0.45f * H;
        for (var y = g + 0.15f * H; y < armY; y += 0.005f * H)
        {
            var hit = false;
            foreach (var p in v) if (MathF.Abs(p.X) < 0.008f * H && MathF.Abs(p.Y - y) < 0.006f * H) { hit = true; break; }
            if (hit) { crotch = y; break; }
        }
        // 다리 군집
        double lx = 0, rx = 0, lz = 0; int ln = 0, rn = 0;
        foreach (var p in v)
            if (p.Y > crotch - 0.10f * H && p.Y < crotch - 0.03f * H)
            {
                if (p.X > 0) { lx += p.X; ln++; } else { rx += p.X; rn++; }
                lz += p.Z;
            }
        var hipXL = ln > 0 ? (float)(lx / ln) : 0.05f * H; var hipXR = rn > 0 ? (float)(rx / rn) : -0.05f * H;
        var legZ = ln + rn > 0 ? (float)(lz / (ln + rn)) : 0f;
        // 발: 바닥 6% 구간 좌/우 앞(z 최대)·뒤(z 최소)
        float heelL = float.MaxValue, toeL = float.MinValue, heelR = float.MaxValue, toeR = float.MinValue;
        double fxL = 0, fxR = 0; int fnL = 0, fnR = 0;
        foreach (var p in v)
            if (p.Y < g + 0.06f * H)
            {
                if (p.X > 0) { heelL = MathF.Min(heelL, p.Z); toeL = MathF.Max(toeL, p.Z); fxL += p.X; fnL++; }
                else { heelR = MathF.Min(heelR, p.Z); toeR = MathF.Max(toeR, p.Z); fxR += p.X; fnR++; }
            }
        var footXL = fnL > 0 ? (float)(fxL / fnL) : hipXL; var footXR = fnR > 0 ? (float)(fxR / fnR) : hipXR;
        if (fnL == 0) { heelL = legZ; toeL = legZ + 0.08f * H; }
        if (fnR == 0) { heelR = legZ; toeR = legZ + 0.08f * H; }

        var rig = new CharacterRig { Name = name, Min = mn, Max = mx, Vertices = v.ToArray() };
        var r = rig.Rest;
        var hipY = crotch + 0.035f * H;
        r[MocapSkeleton.LHip] = new Vector3(hipXL, hipY, legZ);
        r[MocapSkeleton.RHip] = new Vector3(hipXR, hipY, legZ);
        r[MocapSkeleton.Pelvis] = new Vector3(0, hipY + 0.035f * H, legZ);
        r[MocapSkeleton.LAnkle] = new Vector3(footXL, g + 0.035f * H, heelL + 0.025f * H);
        r[MocapSkeleton.RAnkle] = new Vector3(footXR, g + 0.035f * H, heelR + 0.025f * H);
        r[MocapSkeleton.LFoot] = new Vector3(footXL, g, toeL - 0.025f * H);
        r[MocapSkeleton.RFoot] = new Vector3(footXR, g, toeR - 0.025f * H);
        r[MocapSkeleton.LKnee] = Vector3.Lerp(r[MocapSkeleton.LHip], r[MocapSkeleton.LAnkle], 0.5f);
        r[MocapSkeleton.RKnee] = Vector3.Lerp(r[MocapSkeleton.RHip], r[MocapSkeleton.RAnkle], 0.5f);
        var neck = new Vector3(0, armY + (mx.Y - armY) * 0.2f, armZ);
        r[MocapSkeleton.Neck] = neck;
        r[MocapSkeleton.Head] = new Vector3(0, armY + (mx.Y - armY) * 0.55f, armZ);
        var pel = r[MocapSkeleton.Pelvis];
        r[MocapSkeleton.Spine1] = Vector3.Lerp(pel, neck, 0.2f);
        r[MocapSkeleton.Spine2] = Vector3.Lerp(pel, neck, 0.45f);
        r[MocapSkeleton.Spine3] = Vector3.Lerp(pel, neck, 0.7f);
        var s3 = r[MocapSkeleton.Spine3];
        var collarY = s3.Y + (armY - s3.Y) * 0.85f;
        r[MocapSkeleton.LCollar] = new Vector3(0.39f * shoulderX, collarY, armZ);
        r[MocapSkeleton.RCollar] = new Vector3(-0.39f * shoulderX, collarY, armZ);
        for (var side = 0; side < 2; side++)
        {
            var sgn = side == 0 ? 1f : -1f; var tip = side == 0 ? tipL : tipR;
            var sh = new Vector3(sgn * shoulderX, armY, armZ);
            var wr = new Vector3(sgn * shoulderX + (tip - sgn * shoulderX) * 0.81f, armY, armZ);
            r[side == 0 ? MocapSkeleton.LShoulder : MocapSkeleton.RShoulder] = sh;
            r[side == 0 ? MocapSkeleton.LWrist : MocapSkeleton.RWrist] = wr;
            r[side == 0 ? MocapSkeleton.LElbow : MocapSkeleton.RElbow] = Vector3.Lerp(sh, wr, 0.52f);
            r[side == 0 ? MocapSkeleton.LHand : MocapSkeleton.RHand] = new Vector3(sgn * shoulderX + (tip - sgn * shoulderX) * 0.91f, armY, armZ);
        }
        rig.FromRest();
        ComputeSkinWeights(rig);
        LastAction = "🦴 오토 리깅 · " + name + " · 키 " + H.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "m · 정점 " + v.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Raise();
        return rig;
    }

    private static float SegmentDistance(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a; var l2 = ab.LengthSquared();
        var t = l2 > 1e-12f ? Math.Clamp(Vector3.Dot(p - a, ab) / l2, 0f, 1f) : 0f;
        return Vector3.Distance(p, a + ab * t);
    }

    /// <summary>뼈 선분(관절 → 주 자식, 말단은 관절 점) 거리 역4제곱 상위 4본 · 정규화.</summary>
    public static void ComputeSkinWeights(CharacterRig rig)
    {
        var n = rig.Vertices.Length;
        rig.BoneIndex = new int[n * 4]; rig.BoneWeight = new float[n * 4];
        Span<float> best = stackalloc float[4]; Span<int> bi = stackalloc int[4];
        for (var i = 0; i < n; i++)
        {
            best.Fill(float.MaxValue); bi.Fill(0);
            var p = rig.Vertices[i];
            for (var j = 0; j < B; j++)
            {
                var c = MocapSkeleton.PrimaryChild[j];
                var d = c >= 0 ? SegmentDistance(p, rig.Rest[j], rig.Rest[c]) : Vector3.Distance(p, rig.Rest[j]);
                for (var k = 0; k < 4; k++)
                {
                    if (d >= best[k]) continue;
                    for (var m = 3; m > k; m--) { best[m] = best[m - 1]; bi[m] = bi[m - 1]; }
                    best[k] = d; bi[k] = j;
                    break;
                }
            }
            float sum = 0;
            for (var k = 0; k < 4; k++) { var w = 1f / MathF.Pow(best[k] + 0.01f, 4); rig.BoneWeight[i * 4 + k] = w; rig.BoneIndex[i * 4 + k] = bi[k]; sum += w; }
            for (var k = 0; k < 4; k++) rig.BoneWeight[i * 4 + k] /= sum;
        }
    }

    /// <summary>LBS: v' = Σ wᵦ (posᵦ + Gᵦ·(v − restᵦ)). 무할당.</summary>
    public static void Skin(CharacterRig rig, ReadOnlySpan<Vector3> pos, ReadOnlySpan<Quaternion> global, Span<Vector3> dst)
    {
        for (var i = 0; i < rig.Vertices.Length; i++)
        {
            var v = rig.Vertices[i];
            var acc = Vector3.Zero;
            for (var k = 0; k < 4; k++)
            {
                var b = rig.BoneIndex[i * 4 + k];
                acc += rig.BoneWeight[i * 4 + k] * (pos[b] + Vector3.Transform(v - rig.Rest[b], global[b]));
            }
            dst[i] = acc;
        }
    }

    // ══════════════════════ 리타겟팅(무할당) ══════════════════════

    public static Quaternion FromTo(Vector3 a, Vector3 b)
    {
        var d = Vector3.Dot(a, b);
        if (d > 0.999999f) return Quaternion.Identity;
        if (d < -0.999999f)
        {
            var axis = Vector3.Cross(Vector3.UnitX, a);
            if (axis.LengthSquared() < 1e-6f) axis = Vector3.Cross(Vector3.UnitY, a);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        var c = Vector3.Cross(a, b);
        return Quaternion.Normalize(new Quaternion(c, 1 + d));
    }

    private static Quaternion Frame(Vector3 x, Vector3 yRaw)
    {
        x = Vector3.Normalize(x);
        var z = Vector3.Normalize(Vector3.Cross(x, yRaw));
        var y = Vector3.Cross(z, x);
        var m = new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    /// <summary>3D 관절 → 전역 회전(기준 레스트 대비). 골반/가슴 = 프레임 · 그 외 = 최소 스윙.</summary>
    public static void SolveGlobalRotations(ReadOnlySpan<Vector3> pos, Span<Quaternion> global)
    {
        for (var j = 0; j < B; j++)
        {
            if (j == MocapSkeleton.Pelvis) { global[j] = Frame(pos[MocapSkeleton.LHip] - pos[MocapSkeleton.RHip], pos[MocapSkeleton.Spine1] - pos[j]); continue; }
            if (j == MocapSkeleton.Spine3) { global[j] = Frame(pos[MocapSkeleton.LShoulder] - pos[MocapSkeleton.RShoulder], pos[MocapSkeleton.Neck] - pos[j]); continue; }
            var parentG = global[MocapSkeleton.Parent[j]];
            var c = MocapSkeleton.PrimaryChild[j];
            if (c < 0) { global[j] = parentG; continue; }
            var obs = pos[c] - pos[j];
            if (obs.LengthSquared() < 1e-10f) { global[j] = parentG; continue; }
            var cur = Vector3.Transform(Vector3.Normalize(MocapSkeleton.RestOffset[c]), parentG);
            global[j] = Quaternion.Normalize(FromTo(cur, Vector3.Normalize(obs)) * parentG);
        }
    }

    /// <summary>로컬 회전 관절 한계 적용. 반환 = 클램프됨.</summary>
    public static Quaternion ClampLocal(int j, Quaternion q, out bool clamped)
    {
        clamped = false;
        var lim = MocapSkeleton.Limits[j];
        if (q.W < 0) q = -q;
        switch (lim.Kind)
        {
            case JointLimitKind.Hinge:
            {
                var axis = lim.Axis;
                var proj = Vector3.Dot(new Vector3(q.X, q.Y, q.Z), axis);
                var ang = 2 * MathF.Atan2(proj, q.W) * (180f / MathF.PI);
                var c = Math.Clamp(ang, lim.MinDeg, lim.MaxDeg);
                var swing = MathF.Abs(2 * MathF.Acos(Math.Clamp(MathF.Sqrt(q.W * q.W + proj * proj), 0f, 1f))) * (180f / MathF.PI);
                clamped = c != ang || swing > 5f;
                return Quaternion.CreateFromAxisAngle(axis, c * (MathF.PI / 180f));
            }
            case JointLimitKind.Ball:
            {
                var c = MocapSkeleton.PrimaryChild[j];
                var t = Vector3.Normalize(c >= 0 ? MocapSkeleton.RestOffset[c] : MocapSkeleton.RestOffset[j]);
                var v = new Vector3(q.X, q.Y, q.Z);
                var p = t * Vector3.Dot(v, t);
                var twist = new Quaternion(p, q.W);
                twist = twist.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(twist);
                var swing = q * Quaternion.Inverse(twist);
                if (swing.W < 0) swing = -swing;
                var sAng = 2 * MathF.Acos(Math.Clamp(swing.W, -1f, 1f)) * (180f / MathF.PI);
                if (sAng > lim.SwingDeg) { swing = Quaternion.Slerp(Quaternion.Identity, swing, lim.SwingDeg / sAng); clamped = true; }
                var tAng = 2 * MathF.Atan2(Vector3.Dot(new Vector3(twist.X, twist.Y, twist.Z), t), twist.W) * (180f / MathF.PI);
                if (tAng > 180) tAng -= 360; else if (tAng < -180) tAng += 360;
                if (MathF.Abs(tAng) > lim.TwistDeg) { twist = Quaternion.CreateFromAxisAngle(t, Math.Clamp(tAng, -lim.TwistDeg, lim.TwistDeg) * (MathF.PI / 180f)); clamped = true; }
                return Quaternion.Normalize(swing * twist);
            }
            default: return q;
        }
    }

    /// <summary>
    /// 소스 24관절 → 대상 리그 포즈. sourcePelvisHeight = 소스 골반 레스트 높이(루트 이동 스케일) · additive = 로컬 가산 회전(길이 0 이면 없음). 무할당.
    /// </summary>
    public void Retarget(ReadOnlySpan<Vector3> src, float sourcePelvisHeight, ReadOnlySpan<Quaternion> additive, Span<Vector3> outPos)
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Mocap.Retarget");
            SolveGlobalRotations(src, _srcGlobal);
            var clamped = 0;
            for (var j = 0; j < B; j++)
            {
                var p = MocapSkeleton.Parent[j];
                var l = p < 0 ? _srcGlobal[j] : Quaternion.Normalize(Quaternion.Inverse(_srcGlobal[p]) * _srcGlobal[j]);
                if (EnforceLimits && p >= 0) { l = ClampLocal(j, l, out var c); if (c) clamped++; }
                if (additive.Length == B) l = Quaternion.Normalize(l * additive[j]);
                _local[j] = l;
            }
            var t = _target;
            var k = t.PelvisHeight / MathF.Max(1e-3f, sourcePelvisHeight);
            var s0 = src[0];
            var root = new Vector3(s0.X * k, t.Ground + s0.Y * k, s0.Z * k);
            MocapSkeleton.ForwardKinematics(t.Offset, _local, root, _outPos, _outGlobal);
            _outPos.AsSpan().CopyTo(outPos);
            _clamped = clamped;
            _frames++;
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Mocap.Retarget.OnChange", ex); }
    }
}
