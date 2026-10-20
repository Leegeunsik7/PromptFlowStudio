using System.Numerics;
using System.Runtime.InteropServices;

namespace PromptFlowStudio.Services.MotionMatching;

public enum BodyJoint : byte { Pelvis, Spine, Chest, Head, LHip, LKnee, LAnkle, LToe, RHip, RKnee, RAnkle, RToe, LShoulder, LElbow, LWrist, RShoulder, RElbow, RWrist }

/// <summary>이족 골격(18 관절 · 위치 기반 · 로컬 = 루트 지면 원점 · +Z 전방 · +X 오른쪽 · 질량 분포 Winter).</summary>
public static class BipedSkeleton
{
    public const int Count = 18;
    public const float Thigh = 0.45f, Shin = 0.44f, AnkleH = 0.08f, ToeFwd = 0.14f, ToeH = 0.02f, HipW = 0.1f, StandPelvis = 0.965f;
    public const float UpperArm = 0.29f, Forearm = 0.26f, ShoulderW = 0.19f, BodyMass = 70f;

    public static readonly sbyte[] Parent = { -1, 0, 1, 2, 0, 4, 5, 6, 0, 8, 9, 10, 2, 12, 13, 2, 15, 16 };
    public static readonly float FootLen = MathF.Sqrt(ToeFwd * ToeFwd + (AnkleH - ToeH) * (AnkleH - ToeH));
    private static readonly byte[] SegA = { 0, 1, 2, 3, 4, 5, 6, 8, 9, 10, 12, 13, 15, 16 };
    private static readonly byte[] SegB = { 0, 1, 2, 3, 5, 6, 7, 9, 10, 11, 13, 14, 16, 17 };
    private static readonly float[] SegM = { 0.142f, 0.139f, 0.216f, 0.081f, 0.1f, 0.0465f, 0.0145f, 0.1f, 0.0465f, 0.0145f, 0.028f, 0.022f, 0.028f, 0.022f };
    public static readonly float[] BoneLength = BuildLengths();
    /// <summary>분절(관절 A–B 중점 · 질량 비) — v17.35 중심 각운동량.</summary>
    public static ReadOnlySpan<byte> SegmentA => SegA;
    public static ReadOnlySpan<byte> SegmentB => SegB;
    public static ReadOnlySpan<float> SegmentMass => SegM;

    public static Vector3 Forward(float yaw) => new(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
    public static Vector3 Right(float yaw) => new(MathF.Cos(yaw), 0f, -MathF.Sin(yaw));
    public static Vector3 ToWorld(Vector3 l, Vector3 origin, float yaw) => origin + Right(yaw) * l.X + new Vector3(0f, l.Y, 0f) + Forward(yaw) * l.Z;
    public static Vector3 ToLocal(Vector3 w, Vector3 origin, float yaw) { var d = w - origin; return new(Vector3.Dot(d, Right(yaw)), d.Y, Vector3.Dot(d, Forward(yaw))); }
    public static float WrapPi(float a) { a %= MathF.Tau; return a > MathF.PI ? a - MathF.Tau : a < -MathF.PI ? a + MathF.Tau : a; }
    public static Vector3 Dir(Vector3 v, Vector3 fallback) { var l = v.Length(); return l > 1e-7f ? v / l : fallback; }

    /// <summary>무릎 최소 굴곡(°)에서의 고관절-발목 최대 도달 거리(코사인 법칙).</summary>
    public static float MaxReach(float minFlexDeg)
    {
        var c = MathF.Cos(minFlexDeg * MathF.PI / 180f);
        return MathF.Sqrt(Thigh * Thigh + Shin * Shin + 2f * Thigh * Shin * c);
    }

    /// <summary>해석적 2-본 IK(극 벡터 평면) → 무릎. 도달 불가 시 완전 신전 방향.</summary>
    public static Vector3 TwoBone(Vector3 root, Vector3 end, Vector3 pole, float l1, float l2)
    {
        var v = end - root;
        var len = v.Length();
        var axis = len > 1e-7f ? v / len : -Vector3.UnitY;
        var d = Math.Clamp(len, MathF.Abs(l1 - l2) + 1e-4f, l1 + l2 - 1e-5f);
        var a = (l1 * l1 - l2 * l2 + d * d) / (2f * d);
        var h = MathF.Sqrt(MathF.Max(0f, l1 * l1 - a * a));
        var pp = pole - axis * Vector3.Dot(pole, axis);
        if (pp.LengthSquared() < 1e-10f) pp = Vector3.Cross(axis, Vector3.UnitX);
        return root + axis * a + Vector3.Normalize(pp) * h;
    }

    public static Vector3 CenterOfMass(ReadOnlySpan<Vector3> p)
    {
        var c = Vector3.Zero;
        for (var i = 0; i < SegM.Length; i++) c += SegM[i] * 0.5f * (p[SegA[i]] + p[SegB[i]]);
        return c;
    }

    /// <summary>정지 자세(로컬).</summary>
    public static void Rest(Span<Vector3> p)
    {
        p[0] = new(0f, StandPelvis, 0f); p[1] = new(0f, StandPelvis + 0.18f, 0f); p[2] = new(0f, StandPelvis + 0.40f, 0f); p[3] = new(0f, StandPelvis + 0.65f, 0f);
        for (var s = 0; s < 2; s++)
        {
            var x = s == 0 ? -HipW : HipW; var b = s == 0 ? 4 : 8;
            p[b] = new(x, StandPelvis, 0f);
            p[b + 2] = new(x, AnkleH, 0f);
            p[b + 1] = TwoBone(p[b], p[b + 2], Vector3.UnitZ, Thigh, Shin);
            p[b + 3] = new(x, ToeH, ToeFwd);
            var sx = s == 0 ? -ShoulderW : ShoulderW; var a = s == 0 ? 12 : 15;
            p[a] = new(sx, StandPelvis + 0.49f, 0f);
            p[a + 1] = p[a] + new Vector3(0f, -UpperArm, 0f);
            p[a + 2] = p[a + 1] + new Vector3(0f, -Forearm * MathF.Cos(0.2f), Forearm * MathF.Sin(0.2f));
        }
    }

    private static float[] BuildLengths()
    {
        Span<Vector3> p = stackalloc Vector3[Count];
        Rest(p);
        var l = new float[Count];
        for (var j = 1; j < Count; j++) l[j] = (p[j] - p[Parent[j]]).Length();
        return l;
    }

    public static string Name(BodyJoint j) => j switch
    {
        BodyJoint.Pelvis => "골반", BodyJoint.Spine => "요추", BodyJoint.Chest => "흉추", BodyJoint.Head => "머리",
        BodyJoint.LHip => "L 고관절", BodyJoint.LKnee => "L 무릎", BodyJoint.LAnkle => "L 발목", BodyJoint.LToe => "L 발끝",
        BodyJoint.RHip => "R 고관절", BodyJoint.RKnee => "R 무릎", BodyJoint.RAnkle => "R 발목", BodyJoint.RToe => "R 발끝",
        BodyJoint.LShoulder => "L 어깨", BodyJoint.LElbow => "L 팔꿈치", BodyJoint.LWrist => "L 손목",
        BodyJoint.RShoulder => "R 어깨", BodyJoint.RElbow => "R 팔꿈치", _ => "R 손목",
    };
}

public readonly record struct MotionClipInfo(string Name, int Start, int Length, float SpeedFrom, float SpeedTo, float TurnRate);

/// <summary>
/// 모션 데이터베이스(절차 보행 합성 · 학습 모델 아님 — 실 모캡 DB 결선점).
///   ▸ 클립: 속도 7(0.5~5 m/s) × 회전율 7(±1.2 rad/s) · 정지 · 제자리 회전 · 가감속 → 60 fps 약 2만 프레임.
///   ▸ 보행 합성: 위상 누적(케이던스 · 듀티) → 접지 구간 중점 착지점(월드 고정) · 스윙 스무스스텝 + 들림 · ZMP 기준 경로(뒤꿈치→발끝 · 이중 지지 이전) 카트-테이블
///     경계값 문제(삼중대각 Thomas) 전신 CoM 궤적 ·
///     역진자 골반 높이(무릎 굴곡 ≥ 10° 도달 제약)
///     · 상체 전경/선회 기울기 · 반대 위상 팔 스윙 · 2-본 IK 무릎.
///   ▸ 특징 27D(루트 로컬): 발목 위치 6 · 발목 속도 6 · 골반 속도 3 · 미래 궤적(+20/40/60 프레임 = 1 s) 위치 6 · 방향 6 → 그룹 표준화 × 가중.
///   ▸ 검색 가속: 후보 특징 32 패딩 연속 배치 · AABB 2단(64 · 16 프레임) 하한 가지치기 → SIMD L2(Vector&lt;float&gt;) — 전수 검색과 결과 동일(정확).
/// </summary>
public sealed class MotionDatabase
{
    public const int Fps = 60, FeatDim = 27, Stride = 32, Small = 16, Big = 64, Tail = 20, Pad = 70, Count = BipedSkeleton.Count;
    public const float RunSpeed = 2.6f;
    public static readonly int[] Future = { 20, 40, 60 };
    private static readonly int[] GroupStart = { 0, 6, 12, 15, 21, 27 };
    private static readonly float[] GroupWeight = { 0.75f, 1f, 1f, 1f, 1.5f };
    private static readonly Lazy<MotionDatabase> SharedDb = new(() => new MotionDatabase(), LazyThreadSafetyMode.ExecutionAndPublication);

    public static MotionDatabase Shared => SharedDb.Value;

    private readonly float[] _pose, _feat, _rootVel, _yawRate;
    private readonly byte[] _contact;
    private readonly int[] _clipOf, _cand;
    private readonly MotionClipInfo[] _clips;
    private readonly float[] _mean = new float[FeatDim], _scale = new float[FeatDim];
    private readonly float[] _cf, _smin, _smax, _bmin, _bmax;
    private readonly int _nSmall, _nBig;

    public MotionDatabase()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var pose = new List<float>(1 << 21); var feat = new List<float>(1 << 20); var rv = new List<float>(1 << 16); var yr = new List<float>(1 << 15);
        var ct = new List<byte>(1 << 15); var clips = new List<MotionClipInfo>();
        float[] speeds = { 0.5f, 1.0f, 1.5f, 2.2f, 3.0f, 4.0f, 5.0f }, turns = { -1.2f, -0.6f, -0.25f, 0f, 0.25f, 0.6f, 1.2f };
        Generate("정지", 0f, 0f, 0f, 6f, pose, feat, rv, yr, ct, clips);
        Generate("제자리 좌회전", 0f, 0f, -1.5f, 4f, pose, feat, rv, yr, ct, clips);
        Generate("제자리 우회전", 0f, 0f, 1.5f, 4f, pose, feat, rv, yr, ct, clips);
        foreach (var v in speeds)
            foreach (var w in turns)
                Generate((v >= RunSpeed ? "달리기 " : "걷기 ") + v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + (w == 0f ? "" : w > 0 ? " 우선회" : " 좌선회"), v, v, w, 6f, pose, feat, rv, yr, ct, clips);
        Generate("가속 0.3→1.5", 0.3f, 1.5f, 0f, 4f, pose, feat, rv, yr, ct, clips);
        Generate("감속 1.5→0.3", 1.5f, 0.3f, 0f, 4f, pose, feat, rv, yr, ct, clips);
        Generate("가속 1.5→4.0", 1.5f, 4.0f, 0f, 4f, pose, feat, rv, yr, ct, clips);
        Generate("감속 4.0→1.5", 4.0f, 1.5f, 0f, 4f, pose, feat, rv, yr, ct, clips);

        _pose = pose.ToArray(); _rootVel = rv.ToArray(); _yawRate = yr.ToArray(); _contact = ct.ToArray(); _clips = clips.ToArray();
        Frames = _yawRate.Length;
        _clipOf = new int[Frames];
        for (var c = 0; c < _clips.Length; c++) for (var i = 0; i < _clips[c].Length; i++) _clipOf[_clips[c].Start + i] = c;

        // 그룹 표준화
        var raw = feat.ToArray();
        _feat = new float[Frames * Stride];
        Span<double> sum = stackalloc double[FeatDim], sq = stackalloc double[FeatDim];
        for (var f = 0; f < Frames; f++) for (var d = 0; d < FeatDim; d++) { var x = raw[f * FeatDim + d]; sum[d] += x; sq[d] += (double)x * x; }
        Span<float> std = stackalloc float[FeatDim];
        for (var d = 0; d < FeatDim; d++) { _mean[d] = (float)(sum[d] / Frames); std[d] = (float)Math.Sqrt(Math.Max(1e-12, sq[d] / Frames - (double)_mean[d] * _mean[d])); }
        for (var g = 0; g < GroupWeight.Length; g++)
        {
            var s = 0f;
            for (var d = GroupStart[g]; d < GroupStart[g + 1]; d++) s += std[d];
            s /= GroupStart[g + 1] - GroupStart[g];
            for (var d = GroupStart[g]; d < GroupStart[g + 1]; d++) _scale[d] = GroupWeight[g] / MathF.Max(s, 1e-4f);
        }
        for (var f = 0; f < Frames; f++) Normalize(raw.AsSpan(f * FeatDim, FeatDim), _feat.AsSpan(f * Stride, Stride), 0, FeatDim);

        // 후보(클립 끝 Tail 제외) → 연속 배치 + AABB
        var cand = new List<int>(Frames);
        foreach (var c in _clips) for (var i = 0; i < c.Length - Tail; i++) cand.Add(c.Start + i);
        _cand = cand.ToArray();
        Candidates = _cand.Length;
        _cf = new float[Candidates * Stride];
        for (var i = 0; i < Candidates; i++) _feat.AsSpan(_cand[i] * Stride, Stride).CopyTo(_cf.AsSpan(i * Stride, Stride));
        _nSmall = (Candidates + Small - 1) / Small; _nBig = (Candidates + Big - 1) / Big;
        _smin = new float[_nSmall * Stride]; _smax = new float[_nSmall * Stride]; _bmin = new float[_nBig * Stride]; _bmax = new float[_nBig * Stride];
        Bounds(Small, _nSmall, _smin, _smax);
        Bounds(Big, _nBig, _bmin, _bmax);
        BuildMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    public int Frames { get; }
    public int Candidates { get; }
    public int ClipCount => _clips.Length;
    public double BuildMs { get; }
    public MotionClipInfo Clip(int c) => _clips[c];
    public int ClipOf(int f) => _clipOf[f];
    public ReadOnlySpan<Vector3> Pose(int f) => MemoryMarshal.Cast<float, Vector3>(_pose.AsSpan(f * Count * 3, Count * 3));
    public ReadOnlySpan<float> Features(int f) => _feat.AsSpan(f * Stride, Stride);
    public Vector2 RootVelocity(int f) => new(_rootVel[f * 2], _rootVel[f * 2 + 1]);
    public float YawRate(int f) => _yawRate[f];
    public byte Contact(int f) => _contact[f];
    public float RawFeature(int f, int d) => _feat[f * Stride + d] / _scale[d] + _mean[d];
    public bool IsLastOfClip(int f) { var c = _clips[_clipOf[f]]; return f >= c.Start + c.Length - 1; }
    public int IdleStart => _clips[0].Start;

    public void Normalize(ReadOnlySpan<float> raw, Span<float> dst, int from, int to)
    {
        for (var d = from; d < to; d++) dst[d] = (raw[d] - _mean[d]) * _scale[d];
        if (to == FeatDim) dst[FeatDim..Stride].Clear();
    }

    private void Bounds(int size, int n, float[] mn, float[] mx)
    {
        for (var b = 0; b < n; b++)
        {
            mn.AsSpan(b * Stride, Stride).Fill(float.MaxValue); mx.AsSpan(b * Stride, Stride).Fill(float.MinValue);
            var end = Math.Min(Candidates, (b + 1) * size);
            for (var i = b * size; i < end; i++)
                for (var d = 0; d < Stride; d++)
                {
                    var v = _cf[i * Stride + d];
                    if (v < mn[b * Stride + d]) mn[b * Stride + d] = v;
                    if (v > mx[b * Stride + d]) mx[b * Stride + d] = v;
                }
        }
    }

    /// <summary>SIMD 제곱 L2(32 패딩).</summary>
    public static float Distance(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var va = MemoryMarshal.Cast<float, Vector<float>>(a[..Stride]); var vb = MemoryMarshal.Cast<float, Vector<float>>(b[..Stride]);
        var acc = Vector<float>.Zero;
        for (var k = 0; k < va.Length; k++) { var d = va[k] - vb[k]; acc += d * d; }
        return Vector.Sum(acc);
    }

    /// <summary>AABB 하한(질의 → 상자 클램프 거리²).</summary>
    private static float LowerBound(ReadOnlySpan<float> q, ReadOnlySpan<float> mn, ReadOnlySpan<float> mx)
    {
        var vq = MemoryMarshal.Cast<float, Vector<float>>(q[..Stride]);
        var vn = MemoryMarshal.Cast<float, Vector<float>>(mn); var vx = MemoryMarshal.Cast<float, Vector<float>>(mx);
        var acc = Vector<float>.Zero;
        for (var k = 0; k < vq.Length; k++) { var d = vq[k] - Vector.Min(Vector.Max(vq[k], vn[k]), vx[k]); acc += d * d; }
        return Vector.Sum(acc);
    }

    /// <summary>가속 검색(AABB 2단 가지치기 → SIMD L2) — 반환 = DB 프레임 · 무할당.</summary>
    public int Search(ReadOnlySpan<float> q, out float cost, out int evaluated)
    {
        var best = float.MaxValue; var bi = -1; var ev = 0;
        for (var b = 0; b < _nBig; b++)
        {
            if (LowerBound(q, _bmin.AsSpan(b * Stride, Stride), _bmax.AsSpan(b * Stride, Stride)) >= best) continue;
            var s1 = Math.Min(_nSmall, (b + 1) * (Big / Small));
            for (var s = b * (Big / Small); s < s1; s++)
            {
                if (LowerBound(q, _smin.AsSpan(s * Stride, Stride), _smax.AsSpan(s * Stride, Stride)) >= best) continue;
                var e = Math.Min(Candidates, (s + 1) * Small);
                for (var i = s * Small; i < e; i++)
                {
                    var d = Distance(q, _cf.AsSpan(i * Stride, Stride));
                    ev++;
                    if (d < best) { best = d; bi = i; }
                }
            }
        }
        cost = best; evaluated = ev;
        return bi < 0 ? IdleStart : _cand[bi];
    }

    /// <summary>전수 검색(검증용 · 동일 거리 함수).</summary>
    public int SearchBruteForce(ReadOnlySpan<float> q, out float cost)
    {
        var best = float.MaxValue; var bi = 0;
        for (var i = 0; i < Candidates; i++)
        {
            var d = Distance(q, _cf.AsSpan(i * Stride, Stride));
            if (d < best) { best = d; bi = i; }
        }
        cost = best;
        return _cand[bi];
    }

    // ══════════════════════ 절차 보행 클립 합성(구축 시 1회) ══════════════════════

    private static float Smooth(float x) { x = Math.Clamp(x, 0f, 1f); return x * x * (3f - 2f * x); }
    private static float Frac(double x) => (float)(x - Math.Floor(x));

    private static float Cadence(float v, float w) => v < 0.05f ? (MathF.Abs(w) < 0.05f ? 0f : 0.9f) : v < RunSpeed ? 0.8f + 0.2f * v : 1.3f + 0.08f * v;
    private static float Duty(float v, float w) => v < 0.05f ? (MathF.Abs(w) < 0.05f ? 1f : 0.6f) : v < RunSpeed ? Math.Clamp(0.62f - 0.05f * (v - 1f), 0.52f, 0.65f) : 0.32f;

    /// <summary>골반 높이: 기본 − 상하 진동 · 접지 발 도달 제약(무릎 ≥ 10°) → 열림 필터(최소 ±10 → Hann ±10 · 결과 ≤ 제약).</summary>
    private static void PelvisHeight(int m, float[] spd, float w, double[] ph, float[] duty, bool[] stance, Vector3[] world, Vector3[] pxz, float[] yaw, float reach, float[] pys)
    {
        const int R = 10;
        var raw = new float[m]; var lo = new float[m];
        for (var k = 0; k < m; k++)
        {
            var v = spd[k]; var moving = v >= 0.05f || MathF.Abs(w) >= 0.05f;
            var phase01 = Frac(ph[k]);
            var bob = !moving ? 0.004f * MathF.Sin(MathF.Tau * 0.25f * k / Fps) : (v >= RunSpeed ? 0.025f : 0.012f) * (1f + MathF.Cos(2f * MathF.Tau * (phase01 - duty[k] * 0.5f)));
            var y = BipedSkeleton.StandPelvis - (v >= RunSpeed ? 0.04f : 0f) - bob;
            for (var s = 0; s < 2; s++)
            {
                if (!stance[s * m + k]) continue;
                var an = world[k * Count + (s == 0 ? 6 : 10)];
                var hip = pxz[k] + BipedSkeleton.Right(yaw[k]) * (s == 0 ? -BipedSkeleton.HipW : BipedSkeleton.HipW);
                var dx = an.X - hip.X; var dz = an.Z - hip.Z; var xz2 = dx * dx + dz * dz;
                y = MathF.Min(y, xz2 < reach * reach ? an.Y + MathF.Sqrt(reach * reach - xz2) : an.Y);
            }
            raw[k] = y;
        }
        for (var k = 0; k < m; k++)
        {
            var mn = float.MaxValue;
            for (var j = -R; j <= R; j++) mn = MathF.Min(mn, raw[Math.Clamp(k + j, 0, m - 1)]);
            lo[k] = mn;
        }
        for (var k = 0; k < m; k++)
        {
            float acc = 0f, ws = 0f;
            for (var j = -R; j <= R; j++)
            {
                var hw = 0.5f + 0.5f * MathF.Cos(MathF.PI * j / (R + 1));
                acc += hw * lo[Math.Clamp(k + j, 0, m - 1)]; ws += hw;
            }
            pys[k] = acc / ws;
        }
    }

    /// <summary>프레임 k 월드 자세(상체 · 팔 · 다리) 구성 → 전신 CoM.</summary>
    private static Vector3 BuildFrame(int k, int m, float v0, float v1, float w, float seconds, float[] spd, float[] yaw, double[] ph, float[] footYaw, Vector3[] world, Vector3[] pxz, float[] pys, float reach)
    {
        var v = spd[k]; var y = yaw[k];
        var fwd = BipedSkeleton.Forward(y); var right = BipedSkeleton.Right(y);
        var pel = new Vector3(pxz[k].X, pys[k], pxz[k].Z);
        var lean = 0.035f * v + 0.03f * (v1 - v0) / seconds;
        var roll = Math.Clamp(0.5f * MathF.Atan(v * w / 9.81f), -0.3f, 0.3f);
        var up = Vector3.Normalize(right * MathF.Tan(roll) + Vector3.UnitY + fwd * MathF.Tan(lean));
        var b = k * Count;
        world[b] = pel; world[b + 1] = pel + up * 0.18f; world[b + 2] = pel + up * 0.40f; world[b + 3] = pel + up * 0.65f;
        var moving = v >= 0.05f || MathF.Abs(w) >= 0.05f;
        var runK = Smooth((v - 2.2f) / 0.8f);
        var amp = !moving ? 0.03f : 0.3f * MathF.Min(1f, v) * (1f - runK) + 0.55f * runK;
        var flex = (0.25f + 0.1f * MathF.Min(v, 2.2f)) * (1f - runK) + 1.4f * runK;
        var arm = amp * MathF.Sin(MathF.Tau * (float)ph[k]);
        for (var s = 0; s < 2; s++)
        {
            var side = s == 0 ? -1f : 1f;
            var hipJ = s == 0 ? 4 : 8;
            var hip = pel + right * (side * BipedSkeleton.HipW);
            var ankle = world[b + hipJ + 2];
            var d = ankle - hip;
            if (d.Length() > reach) { ankle = hip + Vector3.Normalize(d) * reach; world[b + hipJ + 2] = ankle; }
            var fy = footYaw[s * m + k];
            world[b + hipJ] = hip;
            world[b + hipJ + 1] = BipedSkeleton.TwoBone(hip, ankle, BipedSkeleton.Forward(fy), BipedSkeleton.Thigh, BipedSkeleton.Shin);
            world[b + hipJ + 3] = ankle + BipedSkeleton.Forward(fy) * BipedSkeleton.ToeFwd + new Vector3(0f, BipedSkeleton.ToeH - BipedSkeleton.AnkleH, 0f);
            var sh = s == 0 ? 12 : 15;
            var aa = s == 0 ? arm : -arm;
            var shoulder = world[b + 2] + up * 0.09f + right * (side * BipedSkeleton.ShoulderW);
            var elbow = shoulder + (-up * MathF.Cos(aa) + fwd * MathF.Sin(aa)) * BipedSkeleton.UpperArm;
            world[b + sh] = shoulder; world[b + sh + 1] = elbow;
            world[b + sh + 2] = elbow + (-up * MathF.Cos(aa + flex) + fwd * MathF.Sin(aa + flex)) * BipedSkeleton.Forearm;
        }
        return BipedSkeleton.CenterOfMass(world.AsSpan(b, Count));
    }

    private void Generate(string name, float v0, float v1, float w, float seconds, List<float> pose, List<float> feat, List<float> rv, List<float> yr, List<byte> ct, List<MotionClipInfo> clips)
    {
        var n = (int)(seconds * Fps); var m = n + 2 * Pad;
        var pos = new Vector3[m]; var yaw = new float[m]; var ph = new double[m]; var spd = new float[m]; var duty = new float[m];
        Vector3 p = Vector3.Zero; float h = 0f; double phase = 0.0;
        for (var k = 0; k < m; k++)
        {
            var t = (k - Pad) / (float)Fps;
            var v = v0 + (v1 - v0) * Smooth(t / seconds);
            spd[k] = v; pos[k] = p; yaw[k] = h; ph[k] = phase; duty[k] = Duty(v, w);
            p += BipedSkeleton.Forward(h) * (v / Fps); h += w / Fps; phase += Cadence(v, w) / Fps;
        }
        var world = new Vector3[m * Count];
        var footYaw = new float[2 * m];
        var stance = new bool[2 * m];
        var stS = new int[2 * m]; var stE = new int[2 * m];
        for (var s = 0; s < 2; s++)
        {
            var off = s == 0 ? 0.0 : 0.5;
            for (var k = 0; k < m; k++) stance[s * m + k] = duty[k] >= 0.999f || Frac(ph[k] + off) < duty[k];
            // 접지 구간 → 착지점(중점 · 월드 고정)
            var runS = new List<int>(); var runE = new List<int>();
            for (var k = 0; k < m; k++)
            {
                if (!stance[s * m + k]) continue;
                if (k == 0 || !stance[s * m + k - 1]) runS.Add(k);
                if (k == m - 1 || !stance[s * m + k + 1]) runE.Add(k);
            }
            for (var r = 0; r < runS.Count; r++) for (var k = runS[r]; k <= runE[r]; k++) { stS[s * m + k] = runS[r]; stE[s * m + k] = runE[r]; }
            var plant = new Vector3[runS.Count]; var plantYaw = new float[runS.Count];
            for (var r = 0; r < runS.Count; r++)
            {
                var mid = (runS[r] + runE[r]) / 2;
                var lat = spd[mid] >= RunSpeed ? 0.07f : BipedSkeleton.HipW;
                plant[r] = pos[mid] + BipedSkeleton.Right(yaw[mid]) * (s == 0 ? -lat : lat);
                plantYaw[r] = yaw[mid];
            }
            var ri = 0;
            for (var k = 0; k < m; k++)
            {
                while (ri < runS.Count && runE[ri] < k) ri++;
                Vector3 a; float fy;
                if (runS.Count == 0) { a = pos[k] + BipedSkeleton.Right(yaw[k]) * (s == 0 ? -BipedSkeleton.HipW : BipedSkeleton.HipW); a.Y = BipedSkeleton.AnkleH; fy = yaw[k]; }
                else if (ri < runS.Count && runS[ri] <= k) { a = plant[ri]; a.Y = BipedSkeleton.AnkleH; fy = plantYaw[ri]; }
                else if (ri == 0) { a = plant[0]; a.Y = BipedSkeleton.AnkleH; fy = plantYaw[0]; }
                else if (ri >= runS.Count) { a = plant[^1]; a.Y = BipedSkeleton.AnkleH; fy = plantYaw[^1]; }
                else
                {
                    int e0 = runE[ri - 1], s1 = runS[ri];
                    var u = (k - e0) / (float)(s1 - e0);
                    var su = Smooth(u);
                    a = Vector3.Lerp(plant[ri - 1], plant[ri], su);
                    var lift = spd[k] >= RunSpeed ? 0.16f : spd[k] < 0.05f ? 0.06f : 0.11f;
                    a.Y = BipedSkeleton.AnkleH + lift * MathF.Sin(MathF.PI * u);
                    fy = plantYaw[ri - 1] + BipedSkeleton.WrapPi(plantYaw[ri] - plantYaw[ri - 1]) * su;
                }
                world[k * Count + (s == 0 ? 6 : 10)] = a;
                footYaw[s * m + k] = fy;
            }
        }

        // 골반: 측방 스웨이 → 높이(도달 제약 열림 필터) → 프레임 구성 → 전신 CoM 을 ZMP 기준 경로의 카트-테이블 해로 보정(3회 반복)
        var reach = BipedSkeleton.MaxReach(10f);
        var pxz = new Vector3[m]; var pys = new float[m]; var com = new Vector3[m];
        for (var k = 0; k < m; k++)
        {
            var moving = spd[k] >= 0.05f || MathF.Abs(w) >= 0.05f;
            var sway = moving ? -0.02f * MathF.Cos(MathF.Tau * (Frac(ph[k]) - duty[k] * 0.5f)) : 0f;
            pxz[k] = pos[k] + BipedSkeleton.Right(yaw[k]) * sway;
        }
        PelvisHeight(m, spd, w, ph, duty, stance, world, pxz, yaw, reach, pys);
        for (var k = 0; k < m; k++) com[k] = BuildFrame(k, m, v0, v1, w, seconds, spd, yaw, ph, footYaw, world, pxz, pys, reach);
        // ZMP 기준 경로: 단일 지지 = 뒤꿈치→발끝 롤(−3 → +10 cm) · 이중 지지 = 후행 → 선행 발 스무스 이전(장기 정지 = 양발 중점) · 비행 = 탄도(ẍ = 0)
        var pref = new Vector2[m]; var flight = new bool[m];
        Vector2 Roll(int sf, int k)
        {
            var an = world[k * Count + (sf == 0 ? 6 : 10)];
            var fwF = BipedSkeleton.Forward(footYaw[sf * m + k]);
            var len = Math.Max(1, stE[sf * m + k] - stS[sf * m + k]);
            var u = (k - stS[sf * m + k]) / (float)len;
            var p = an + fwF * (-0.03f + 0.13f * u);
            return new Vector2(p.X, p.Z);
        }
        for (var k = 0; k < m;)
        {
            bool l = stance[k], r = stance[m + k];
            if (l != r) { pref[k] = Roll(l ? 0 : 1, k); k++; continue; }
            if (!l) { flight[k] = true; k++; continue; }
            var a0 = k;
            while (k < m && stance[k] && stance[m + k]) k++;
            var b0 = k - 1;
            var lead = stS[a0] > stS[m + a0] ? 0 : 1;   // 늦게 착지한 발 = 선행
            for (var q = a0; q <= b0; q++)
            {
                if (b0 - a0 > 30) { var pa = Roll(0, q); var pb = Roll(1, q); pref[q] = 0.5f * (pa + pb); continue; }
                var t = Smooth((q - a0 + 0.5f) / (b0 - a0 + 1));
                pref[q] = Vector2.Lerp(Roll(1 - lead, q), Roll(lead, q), t);
            }
        }
        // 카트-테이블 경계값 문제: p_k = x_k − h_k/((g + ÿ_k)Δt²)·(x_{k+1} − 2x_k + x_{k−1}) → 삼중대각(Thomas) · 양끝 = 현 CoM · 3회 반복
        var xs = new Vector2[m]; var cp = new float[m]; var dp = new Vector2[m];
        for (var iter = 0; iter < 3; iter++)
        {
            xs[0] = new Vector2(com[0].X, com[0].Z); xs[m - 1] = new Vector2(com[m - 1].X, com[m - 1].Z);
            // 전진 소거(행 k: −c x_{k−1} + (1 + 2c) x_k − c x_{k+1} = p_k · 비행: −x_{k−1} + 2x_k − x_{k+1} = 0)
            cp[0] = 0f; dp[0] = xs[0];
            for (var k = 1; k < m - 1; k++)
            {
                float lo, di, up; Vector2 rhs;
                if (flight[k]) { lo = -1f; di = 2f; up = -1f; rhs = Vector2.Zero; }
                else
                {
                    var yacc = (com[k + 1].Y - 2f * com[k].Y + com[k - 1].Y) * Fps * Fps;
                    var c = com[k].Y / (MathF.Max(9.81f + yacc, 2f) / (Fps * Fps));
                    lo = -c; di = 1f + 2f * c; up = -c; rhs = pref[k];
                }
                var den = di - lo * cp[k - 1];
                cp[k] = up / den;
                dp[k] = (rhs - lo * dp[k - 1]) / den;
            }
            for (var k = m - 2; k >= 1; k--) xs[k] = dp[k] - cp[k] * xs[k + 1];
            for (var k = 1; k < m - 1; k++) pxz[k] += new Vector3(xs[k].X - com[k].X, 0f, xs[k].Y - com[k].Z) / 0.82f;
            PelvisHeight(m, spd, w, ph, duty, stance, world, pxz, yaw, reach, pys);
            for (var k = 0; k < m; k++) com[k] = BuildFrame(k, m, v0, v1, w, seconds, spd, yaw, ph, footYaw, world, pxz, pys, reach);
        }

        // 코어 구간 → 로컬 자세 · 루트 속도 · 접지 · 특징
        var start = yr.Count;
        Span<float> f = stackalloc float[FeatDim];
        for (var k = Pad; k < Pad + n; k++)
        {
            var origin = new Vector3(pos[k].X, 0f, pos[k].Z); var y = yaw[k];
            var right = BipedSkeleton.Right(y); var fwd = BipedSkeleton.Forward(y);
            for (var j = 0; j < Count; j++) { var l = BipedSkeleton.ToLocal(world[k * Count + j], origin, y); pose.Add(l.X); pose.Add(l.Y); pose.Add(l.Z); }
            var vel = (pos[k + 1] - pos[k - 1]) * (Fps * 0.5f);
            rv.Add(Vector3.Dot(vel, right)); rv.Add(Vector3.Dot(vel, fwd));
            yr.Add(w);
            ct.Add((byte)((stance[k] ? 1 : 0) | (stance[m + k] ? 2 : 0)));
            for (var s = 0; s < 2; s++)
            {
                var aj = s == 0 ? 6 : 10;
                var l = BipedSkeleton.ToLocal(world[k * Count + aj], origin, y);
                f[s * 3] = l.X; f[s * 3 + 1] = l.Y; f[s * 3 + 2] = l.Z;
                var av = (world[(k + 1) * Count + aj] - world[(k - 1) * Count + aj]) * (Fps * 0.5f);
                f[6 + s * 3] = Vector3.Dot(av, right); f[7 + s * 3] = av.Y; f[8 + s * 3] = Vector3.Dot(av, fwd);
            }
            var pv = (world[(k + 1) * Count] - world[(k - 1) * Count]) * (Fps * 0.5f);
            f[12] = Vector3.Dot(pv, right); f[13] = pv.Y; f[14] = Vector3.Dot(pv, fwd);
            for (var i = 0; i < 3; i++)
            {
                var q = k + Future[i];
                var l = BipedSkeleton.ToLocal(pos[q], origin, y);
                f[15 + i * 2] = l.X; f[16 + i * 2] = l.Z;
                var da = yaw[q] - y;
                f[21 + i * 2] = MathF.Sin(da); f[22 + i * 2] = MathF.Cos(da);
            }
            for (var d = 0; d < FeatDim; d++) feat.Add(f[d]);
        }
        clips.Add(new MotionClipInfo(name, start, n, v0, v1, w));
    }
}

/// <summary>
/// 궤적 컨트롤러(임계 감쇠 스프링 · Holden 2020 · 폐형 1 s 예측 · 무할당).
/// </summary>
public sealed class TrajectoryController
{
    public Vector3 Position, Velocity, Acceleration;
    public float Yaw, YawVelocity;
    public Vector3 DesiredVelocity { get; private set; }
    public float DesiredYaw { get; private set; }
    public float HalfLife { get; set; } = 0.27f;
    public float YawHalfLife { get; set; } = 0.24f;

    public void Reset(Vector3 p, float yaw) { Position = p; Velocity = Acceleration = Vector3.Zero; Yaw = DesiredYaw = yaw; YawVelocity = 0f; DesiredVelocity = Vector3.Zero; }

    public void SetDesired(Vector3 velocity, float yaw) { velocity.Y = 0f; DesiredVelocity = velocity; DesiredYaw = yaw; }

    public static float Damping(float halfLife) => 4f * 0.6931472f / (halfLife + 1e-5f);

    public static void SpringCharacter(ref Vector3 x, ref Vector3 v, ref Vector3 a, Vector3 goal, float halfLife, float dt)
    {
        var y = Damping(halfLife) * 0.5f;
        var j0 = v - goal; var j1 = a + j0 * y; var e = MathF.Exp(-y * dt);
        x = e * (-j1 / (y * y) + (-j0 - j1 * dt) / y) + j1 / (y * y) + j0 / y + goal * dt + x;
        v = e * (j0 + j1 * dt) + goal;
        a = e * (a - j1 * y * dt);
    }

    public static void SpringAngle(ref float x, ref float v, float goal, float halfLife, float dt)
    {
        var y = Damping(halfLife) * 0.5f;
        var j0 = BipedSkeleton.WrapPi(x - goal); var j1 = v + j0 * y; var e = MathF.Exp(-y * dt);
        x = BipedSkeleton.WrapPi(e * (j0 + j1 * dt) + goal);
        v = e * (v - j1 * y * dt);
    }

    public void Update(float dt)
    {
        SpringCharacter(ref Position, ref Velocity, ref Acceleration, DesiredVelocity, HalfLife, dt);
        SpringAngle(ref Yaw, ref YawVelocity, DesiredYaw, YawHalfLife, dt);
    }

    /// <summary>t 초 뒤 위치 · 방향(폐형 — 현 상태 불변).</summary>
    public void Predict(float t, out Vector3 pos, out float yaw)
    {
        var x = Position; var v = Velocity; var a = Acceleration; var r = Yaw; var rv = YawVelocity;
        SpringCharacter(ref x, ref v, ref a, DesiredVelocity, HalfLife, t);
        SpringAngle(ref r, ref rv, DesiredYaw, YawHalfLife, t);
        pos = x; yaw = r;
    }
}

/// <summary>
/// [PromptFlow Studio v17.33.0] 🏃 실시간 관성 모션 매칭 &amp; 궤적 예측 엔진(60 fps · <see cref="Step"/> 무할당).
///   ▸ 질의 = 현재 재생 프레임 자세 특징(발목 위치/속도 · 골반 속도) ‖ 컨트롤러 1 s 예측 궤적(+0.33/0.67/1.0 s 위치 · 방향 — 캐릭터 로컬) → DB 표준화.
///   ▸ 검색: 6 프레임(0.1 s) 주기 · 입력 급변 · 클립 끝 → AABB 2단 가지치기 SIMD L2 · 동일 클립 인접(±12) 전환 억제 · 비용 개선 &gt; 0.02 시 전환.
///   ▸ 관성 블렌딩(Inertialization): 전환 시 출력-목적 자세 위치/속도 오프셋 → 임계 감쇠 감쇠 스프링(반감기 0.1 s) · 루트 속도/회전율 동일 → 위치·속도 C¹ 연속.
///   ▸ 루트: DB 루트 모션 적분 우선(발 고정 보존) → 시뮬레이션으로 약동기(반감기 0.6 s) · 시뮬레이션은 캐릭터 0.3 m 이내로 클램프.
/// </summary>
public sealed class RealtimeNeuralMotionMatchingEngine
{
    public const int Count = BipedSkeleton.Count, SearchInterval = 6, Adjacent = 12, ArcPoints = 12;
    public const float Dt = 1f / MotionDatabase.Fps, MinImprovement = 0.02f, SyncHalfLife = 0.6f, MaxSimDistance = 0.3f;

    private readonly MotionDatabase _db;
    private readonly Vector3[] _pose = new Vector3[Count], _prev = new Vector3[Count], _prev2 = new Vector3[Count], _offX = new Vector3[Count], _offV = new Vector3[Count];
    private readonly float[] _q = new float[MotionDatabase.Stride], _raw = new float[MotionDatabase.FeatDim];
    private Vector2 _offRv, _offRvV;
    private float _offYr, _offYrV, _pruneSum;
    private int _cur, _since;
    private Vector3 _lastDesired;
    private long _frames;

    public RealtimeNeuralMotionMatchingEngine(MotionDatabase? db = null)
    {
        _db = db ?? MotionDatabase.Shared;
        Reset(Vector3.Zero, 0f);
    }

    public MotionDatabase Database => _db;
    public TrajectoryController Controller { get; } = new();
    public bool Inertialize { get; set; } = true;
    public float HalfLife { get; set; } = 0.1f;
    public Vector3 Position { get; private set; }
    public float Heading { get; private set; }
    public ReadOnlySpan<Vector3> Pose => _pose;
    public Vector2 RootVelocity { get; private set; }
    public float YawRate { get; private set; }
    public byte Contact { get; private set; }
    public int CurrentFrame => _cur;
    public int CurrentClip => _db.ClipOf(_cur);
    public float LastCost { get; private set; }
    public float CurrentCost { get; private set; }
    public double LastSearchUs { get; private set; }
    public double MaxSearchUs { get; private set; }
    public long Searches { get; private set; }
    public long Transitions { get; private set; }
    public int LastEvaluated { get; private set; }
    public float PruneRatio => Searches > 0 ? _pruneSum / Searches : 0f;
    /// <summary>프레임 간 관절 2차 차분 최대(m) — 전환 불연속(팝) 지표.</summary>
    public float LastJerk { get; private set; }
    public float MaxJerk { get; private set; }
    public float OffsetMagnitude { get; private set; }

    public void Reset(Vector3 pos, float heading)
    {
        _cur = _db.IdleStart;
        _db.Pose(_cur).CopyTo(_pose); _pose.AsSpan().CopyTo(_prev); _pose.AsSpan().CopyTo(_prev2);
        Array.Clear(_offX); Array.Clear(_offV); _offRv = _offRvV = Vector2.Zero; _offYr = _offYrV = 0f;
        Position = pos; Heading = heading; RootVelocity = Vector2.Zero; YawRate = 0f; Contact = _db.Contact(_cur);
        Controller.Reset(pos, heading);
        _since = 0; _frames = 0; _lastDesired = Vector3.Zero;
    }

    public void ResetMetrics() { MaxSearchUs = 0; Searches = Transitions = 0; _pruneSum = 0f; MaxJerk = 0f; }

    /// <summary>질의 특징(표준화) 구성 — 현재 프레임 자세 ‖ 예측 궤적.</summary>
    public void BuildQuery(Span<float> q)
    {
        _db.Features(_cur)[..15].CopyTo(q);
        var right = BipedSkeleton.Right(Heading); var fwd = BipedSkeleton.Forward(Heading);
        for (var i = 0; i < 3; i++)
        {
            Controller.Predict(MotionDatabase.Future[i] * Dt, out var p, out var y);
            var d = p - Controller.Position;
            _raw[15 + i * 2] = Vector3.Dot(d, right); _raw[16 + i * 2] = Vector3.Dot(d, fwd);
            var a = y - Heading;
            _raw[21 + i * 2] = MathF.Sin(a); _raw[22 + i * 2] = MathF.Cos(a);
        }
        _db.Normalize(_raw, q, 15, MotionDatabase.FeatDim);
    }

    public void Step()
    {
        Controller.Update(Dt);
        var next = _cur + 1;
        var atEnd = _db.IsLastOfClip(_cur);
        _since++;
        var changed = (Controller.DesiredVelocity - _lastDesired).LengthSquared() > 0.16f;
        if (_since >= SearchInterval || changed || atEnd)
        {
            BuildQuery(_q);
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var best = _db.Search(_q, out var cost, out var ev);
            var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
            LastSearchUs = us; if (us > MaxSearchUs) MaxSearchUs = us;
            Searches++; LastEvaluated = ev; _pruneSum += 1f - ev / (float)_db.Candidates;
            LastCost = cost;
            CurrentCost = atEnd ? float.PositiveInfinity : MotionDatabase.Distance(_q, _db.Features(next));
            _since = 0; _lastDesired = Controller.DesiredVelocity;
            var adjacent = !atEnd && _db.ClipOf(best) == _db.ClipOf(next) && Math.Abs(best - next) < Adjacent;
            if (!adjacent && (atEnd || cost < CurrentCost - MinImprovement)) { Transition(best); next = best; }
        }
        Advance(next);
    }

    private void Transition(int dst)
    {
        Transitions++;
        if (!Inertialize) { Array.Clear(_offX); Array.Clear(_offV); _offRv = _offRvV = Vector2.Zero; _offYr = _offYrV = 0f; return; }
        // 후보는 클립 끝 Tail 제외 → dst + 1 은 항상 같은 클립(전진 차분 속도)
        var dp = _db.Pose(dst);
        var dn = _db.Pose(dst + 1);
        for (var j = 0; j < Count; j++)
        {
            _offX[j] = _pose[j] - dp[j];
            _offV[j] = (_pose[j] - _prev[j]) / Dt - (dn[j] - dp[j]) / Dt;
        }
        _offRv = RootVelocity - _db.RootVelocity(dst); _offRvV = Vector2.Zero;
        _offYr = YawRate - _db.YawRate(dst); _offYrV = 0f;
    }

    private static void Decay(ref Vector3 x, ref Vector3 v, float hl, float dt)
    {
        var y = TrajectoryController.Damping(hl) * 0.5f;
        var j1 = v + x * y; var e = MathF.Exp(-y * dt);
        x = e * (x + j1 * dt); v = e * (v - j1 * y * dt);
    }

    private static void Decay(ref Vector2 x, ref Vector2 v, float hl, float dt)
    {
        var y = TrajectoryController.Damping(hl) * 0.5f;
        var j1 = v + x * y; var e = MathF.Exp(-y * dt);
        x = e * (x + j1 * dt); v = e * (v - j1 * y * dt);
    }

    private static void Decay(ref float x, ref float v, float hl, float dt)
    {
        var y = TrajectoryController.Damping(hl) * 0.5f;
        var j1 = v + x * y; var e = MathF.Exp(-y * dt);
        x = e * (x + j1 * dt); v = e * (v - j1 * y * dt);
    }

    private void Advance(int frame)
    {
        _cur = frame;
        _prev.AsSpan().CopyTo(_prev2);
        _pose.AsSpan().CopyTo(_prev);
        var dp = _db.Pose(_cur);
        float om = 0f;
        for (var j = 0; j < Count; j++)
        {
            if (Inertialize) Decay(ref _offX[j], ref _offV[j], HalfLife, Dt);
            _pose[j] = dp[j] + _offX[j];
            om = MathF.Max(om, _offX[j].Length());
        }
        OffsetMagnitude = om;
        if (Inertialize) { Decay(ref _offRv, ref _offRvV, HalfLife, Dt); Decay(ref _offYr, ref _offYrV, HalfLife, Dt); }
        RootVelocity = _db.RootVelocity(_cur) + _offRv;
        YawRate = _db.YawRate(_cur) + _offYr;
        Contact = _db.Contact(_cur);
        _frames++;
        if (_frames > 2)
        {
            var jerk = 0f;
            for (var j = 0; j < Count; j++) jerk = MathF.Max(jerk, (_pose[j] - 2f * _prev[j] + _prev2[j]).Length());
            LastJerk = jerk;
            if (jerk > MaxJerk) MaxJerk = jerk;
        }

        // 루트 모션 적분 → 시뮬레이션 동기
        var h = Heading + YawRate * Dt;
        var p = Position + (BipedSkeleton.Right(h) * RootVelocity.X + BipedSkeleton.Forward(h) * RootVelocity.Y) * Dt;
        var k = 1f - MathF.Exp(-Dt * 0.6931472f / SyncHalfLife);
        var sp = Controller.Position;
        p += new Vector3(sp.X - p.X, 0f, sp.Z - p.Z) * k;
        h = BipedSkeleton.WrapPi(h + BipedSkeleton.WrapPi(Controller.Yaw - h) * k);
        var d = new Vector3(sp.X - p.X, 0f, sp.Z - p.Z);
        var dl = d.Length();
        if (dl > MaxSimDistance) Controller.Position = new Vector3(p.X, sp.Y, p.Z) + d / dl * MaxSimDistance;
        Position = p; Heading = h;
    }

    /// <summary>현재 매칭 클립의 k 프레임 후 접지 플래그(클립 끝 고정 · 전환 전까지의 예측 — v17.35 접지 창 · 발 배치).</summary>
    public byte FutureContact(int k)
    {
        var c = _db.Clip(_db.ClipOf(_cur));
        return _db.Contact(Math.Min(_cur + Math.Max(k, 0), c.Start + c.Length - 1));
    }

    /// <summary>현재 매칭 클립의 k 프레임 후 로컬 자세(클립 끝 고정).</summary>
    public ReadOnlySpan<Vector3> FuturePose(int k)
    {
        var c = _db.Clip(_db.ClipOf(_cur));
        return _db.Pose(Math.Min(_cur + Math.Max(k, 0), c.Start + c.Length - 1));
    }

    /// <summary>컨트롤러 예측 궤적 호(월드 · y = 0) — UI.</summary>
    public void PredictArc(Span<Vector3> dst)
    {
        for (var i = 0; i < dst.Length; i++)
        {
            Controller.Predict((i + 1) / (float)dst.Length, out var p, out _);
            dst[i] = p;
        }
    }

    /// <summary>현재 매칭 프레임의 DB 미래 궤적(월드 3점) — UI.</summary>
    public void MatchedFuture(Span<Vector3> dst)
    {
        var origin = new Vector3(Position.X, 0f, Position.Z);
        for (var i = 0; i < 3; i++)
            dst[i] = BipedSkeleton.ToWorld(new Vector3(_db.RawFeature(_cur, 15 + i * 2), 0f, _db.RawFeature(_cur, 16 + i * 2)), origin, Heading);
    }
}
