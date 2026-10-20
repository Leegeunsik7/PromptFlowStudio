using System.Numerics;
using PromptFlowStudio.Services.NeuralRagdoll;
using PromptFlowStudio.Services.SplatEnvironment;
namespace PromptFlowStudio.Services.SplatRelighting;

/// <summary>배우 6DoF 포즈 — 발 기준 루트 · 회전 · 키(m) · 보행 위상(rad).</summary>
public struct SplatActorPose
{
    public Vector3 Root;
    public Quaternion Rotation;
    public float Height, WalkPhase;
}

/// <summary>
/// [PromptFlow Studio v17.56.0] 🌓 실시간 3D 가우시안 스플랫 접지 그림자 투영 엔진(<see cref="ISplatRadianceModulator"/> · 무할당 · 60 fps).
/// <b>뎁스 맵</b>: v17.41 키 라이트 위치에서 배우(15 구 · 6DoF 포즈 · 보행 스윙) 경계 구에 맞춘 원근 256² 광원 공간 선형 깊이 —
/// 지면(y = 0) 광선 교차 + 구 전면 깊이(z − r·√(1 − q²))의 최솟값. 배우 전용 맵의 텍셀 사각형(+ 커널 여유) 밖 수용점은 즉시 통과.
/// <b>PCSS</b>: 블로커 탐색(반경 = 가드 상한) 평균 깊이 → 반음영 폭 = 광원 크기·(z_r − z_b)/z_b (원근 반음영 · 발 접지부는 날카롭게) →
/// PCF (2r+1)² 탭을 행 단위 Vector&lt;float&gt; 비교 · 마스크 합으로 계수(스칼라 꼬리). 바이어스는 <see cref="AdaptiveDepthBiasShadowGuard"/> 슬로프 공식.
/// <b>변조</b>: v17.55 래스터라이저 워커가 투영 통과 스플랫마다 호출 — 색 × (1 − 농도·(1 − 조도)) + 배우 근접 바운스(알베도 · 1.2 m 감쇠).
/// </summary>
public sealed class RealtimeSplatShadowCastingEngine : ISplatRadianceModulator
{
    public const int Res = 256, Spheres = 15, MaxWorkers = 16;
    public const byte ActorKind = 6, GroundKind = 1;
    private static readonly float Inf = float.PositiveInfinity;
    private readonly float[] _depth = new float[Res * Res], _actor = new float[Res * Res];
    private readonly Vector4[] _sph = new Vector4[Spheres];
    private readonly long[] _wTicks = new long[MaxWorkers], _wEval = new long[MaxWorkers], _wShadow = new long[MaxWorkers], _wSkip = new long[MaxWorkers];
    private readonly AdaptiveDepthBiasShadowGuard _guard;
    private GaussianSplatScene? _scene;
    private Vector3 _L, _F, _R, _U, _hip;
    private float _tan, _texK;
    private int _rx0, _ry0, _rx1, _ry1;

    public RealtimeSplatShadowCastingEngine(AdaptiveDepthBiasShadowGuard guard) { _guard = guard; LightPosition = new Vector3(2.6f, 4.2f, 2.2f); }

    public AdaptiveDepthBiasShadowGuard Guard => _guard;
    public bool Enabled { get; set; } = true;
    public bool UseSimd { get; set; } = Vector.IsHardwareAccelerated;
    /// <summary>그림자 농도(0..1).</summary>
    public float Opacity { get; set; } = 0.85f;
    /// <summary>반음영 부드러움(광원 크기 배율 0..3).</summary>
    public float Softness { get; set; } = 1f;
    public const float BaseLightSizeM = 0.35f;
    /// <summary>배우 → 환경 바운스(상호 반사) 강도.</summary>
    public float BleedStrength { get; set; } = 0.18f;
    public Vector3 ActorAlbedo { get; set; } = new(0.62f, 0.42f, 0.36f);
    public Vector3 LightPosition { get; set; }
    public ReadOnlySpan<float> DepthMap => _depth;
    public ReadOnlySpan<float> ActorDepthMap => _actor;
    public ReadOnlySpan<Vector4> ActorSpheres => _sph;
    public Vector3 ActorCenter => _hip;
    public int RectX0 => _rx0; public int RectY0 => _ry0; public int RectX1 => _rx1; public int RectY1 => _ry1;
    public float TanHalf => _tan;
    public double LastBuildUs { get; private set; }
    public double LastPcfUs { get; private set; }
    public float LastShadowMs { get; private set; }
    public long Evaluations { get; private set; }
    public long ShadowedSplats { get; private set; }
    public long SkippedSplats { get; private set; }
    public long Frames { get; private set; }

    // ══════════════════════ 배우 · 광원 ══════════════════════

    /// <summary>포즈 → 월드 구 15(머리 · 목 · 가슴 · 배 · 골반 · 상완/전완 ×2 · 허벅지/정강이 ×2 · 발 ×2) · 무할당.</summary>
    public static void BuildSpheres(in SplatActorPose pose, Span<Vector4> dst)
    {
        var h = pose.Height / 1.8f; var s = MathF.Sin(pose.WalkPhase);
        var q = pose.Rotation; var o = pose.Root;
        static Vector4 P(Vector3 o, Quaternion q, float x, float y, float z, float r, float h) { var p = o + Vector3.Transform(new Vector3(x, y, z) * h, q); return new Vector4(p, r * h); }
        dst[0] = P(o, q, 0f, 1.66f, 0.02f, 0.11f, h);
        dst[1] = P(o, q, 0f, 1.50f, 0f, 0.06f, h);
        dst[2] = P(o, q, 0f, 1.32f, 0f, 0.17f, h);
        dst[3] = P(o, q, 0f, 1.10f, 0f, 0.15f, h);
        dst[4] = P(o, q, 0f, 0.92f, 0f, 0.15f, h);
        dst[5] = P(o, q, -0.24f, 1.30f, -0.06f * s, 0.07f, h); dst[6] = P(o, q, 0.24f, 1.30f, 0.06f * s, 0.07f, h);
        dst[7] = P(o, q, -0.27f, 1.05f, -0.16f * s, 0.06f, h); dst[8] = P(o, q, 0.27f, 1.05f, 0.16f * s, 0.06f, h);
        dst[9] = P(o, q, -0.1f, 0.68f, 0.12f * s, 0.09f, h); dst[10] = P(o, q, 0.1f, 0.68f, -0.12f * s, 0.09f, h);
        dst[11] = P(o, q, -0.1f, 0.34f, 0.2f * s, 0.07f, h); dst[12] = P(o, q, 0.1f, 0.34f, -0.2f * s, 0.07f, h);
        dst[13] = P(o, q, -0.1f, 0.06f, 0.26f * s + 0.06f, 0.06f, h); dst[14] = P(o, q, 0.1f, 0.06f, -0.26f * s + 0.06f, 0.06f, h);
    }

    public void SetActor(in SplatActorPose pose)
    {
        BuildSpheres(pose, _sph);
        _hip = new Vector3(_sph[4].X, _sph[4].Y, _sph[4].Z);
        SkeletonDriven = false;
    }

    /// <summary>v17.63.1 골격 결선 여부 — true 면 15 구가 v17.57 DQS 아바타 관절에서 생성(간이 보행 프록시 아님).</summary>
    public bool SkeletonDriven { get; private set; }

    /// <summary>
    /// v17.63.1 — v17.57 DQS 아바타 24 관절(<see cref="RagdollJoint"/>) → 동일 15 구 계층(본 중점 · 프록시 반경 유지) · 무할당.
    /// 관절은 골반 XZ 기준으로 원점화 후 배우 포즈(루트 · 회전)로 배치 — 아바타 실제 골격 실루엣이 그림자에 투영된다.
    /// </summary>
    public static void BuildSpheresFromSkeleton(ReadOnlySpan<Vector3> j, in SplatActorPose pose, Span<Vector4> dst)
    {
        var pel = j[(int)RagdollJoint.Pelvis]; var org = new Vector3(pel.X, 0f, pel.Z);
        var q = pose.Rotation; var o = pose.Root;
        static Vector4 S(Vector3 a, Vector3 b, float r, Vector3 o, Vector3 org, Quaternion q) => new(o + Vector3.Transform((a + b) * 0.5f - org, q), r);
        dst[0] = S(j[(int)RagdollJoint.Head], j[(int)RagdollJoint.HeadTop], 0.11f, o, org, q);
        dst[1] = S(j[(int)RagdollJoint.Neck], j[(int)RagdollJoint.Neck], 0.06f, o, org, q);
        dst[2] = S(j[(int)RagdollJoint.Chest], j[(int)RagdollJoint.Chest], 0.17f, o, org, q);
        dst[3] = S(j[(int)RagdollJoint.Spine], j[(int)RagdollJoint.Chest], 0.15f, o, org, q);
        dst[4] = S(j[(int)RagdollJoint.Pelvis], j[(int)RagdollJoint.Pelvis], 0.15f, o, org, q);
        dst[5] = S(j[(int)RagdollJoint.LShoulder], j[(int)RagdollJoint.LElbow], 0.07f, o, org, q); dst[6] = S(j[(int)RagdollJoint.RShoulder], j[(int)RagdollJoint.RElbow], 0.07f, o, org, q);
        dst[7] = S(j[(int)RagdollJoint.LElbow], j[(int)RagdollJoint.LWrist], 0.06f, o, org, q); dst[8] = S(j[(int)RagdollJoint.RElbow], j[(int)RagdollJoint.RWrist], 0.06f, o, org, q);
        dst[9] = S(j[(int)RagdollJoint.LHip], j[(int)RagdollJoint.LKnee], 0.09f, o, org, q); dst[10] = S(j[(int)RagdollJoint.RHip], j[(int)RagdollJoint.RKnee], 0.09f, o, org, q);
        dst[11] = S(j[(int)RagdollJoint.LKnee], j[(int)RagdollJoint.LAnkle], 0.07f, o, org, q); dst[12] = S(j[(int)RagdollJoint.RKnee], j[(int)RagdollJoint.RAnkle], 0.07f, o, org, q);
        dst[13] = S(j[(int)RagdollJoint.LAnkle], j[(int)RagdollJoint.LToe], 0.06f, o, org, q); dst[14] = S(j[(int)RagdollJoint.RAnkle], j[(int)RagdollJoint.RToe], 0.06f, o, org, q);
    }

    /// <summary>골격 구동 배우 — 관절 수 부족 · 비유한 좌표면 프록시 포즈로 폴백(false).</summary>
    public bool SetActorSkeleton(ReadOnlySpan<Vector3> joints, in SplatActorPose pose)
    {
        if (joints.Length < RagdollSkeleton.Count) { SetActor(pose); return false; }
        for (var i = 0; i < RagdollSkeleton.Count; i++)
            if (!float.IsFinite(joints[i].X) || !float.IsFinite(joints[i].Y) || !float.IsFinite(joints[i].Z)) { SetActor(pose); return false; }
        BuildSpheresFromSkeleton(joints, pose, _sph);
        _hip = new Vector3(_sph[4].X, _sph[4].Y, _sph[4].Z);
        SkeletonDriven = true;
        return true;
    }

    /// <summary>광원 원근 뎁스 맵 생성(무할당) — 배우 경계 구 × 1.25 에 화각 맞춤.</summary>
    public void BuildDepthMap()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Frames++;
        // 배우 경계 구
        var c = Vector3.Zero; for (var i = 0; i < Spheres; i++) c += new Vector3(_sph[i].X, _sph[i].Y, _sph[i].Z);
        c /= Spheres;
        var br = 0f; for (var i = 0; i < Spheres; i++) br = MathF.Max(br, Vector3.Distance(c, new Vector3(_sph[i].X, _sph[i].Y, _sph[i].Z)) + _sph[i].W);
        _L = LightPosition;
        _F = Vector3.Normalize(c - _L);
        _R = Vector3.Normalize(Vector3.Cross(_F, MathF.Abs(_F.Y) > 0.98f ? Vector3.UnitX : Vector3.UnitY));
        _U = Vector3.Cross(_R, _F);
        var dist = MathF.Max(Vector3.Distance(c, _L), br * 1.5f);
        _tan = br * 1.25f / MathF.Sqrt(MathF.Max(dist * dist - br * br, 1e-4f));
        _texK = 2f * _tan / Res;
        // 지면 광선 교차
        var half = Res * 0.5f;
        for (var y = 0; y < Res; y++)
        {
            var ny = 1f - (y + 0.5f) / half;
            var row = y * Res;
            for (var x = 0; x < Res; x++)
            {
                var nx = (x + 0.5f) / half - 1f;
                var dy = _F.Y + _R.Y * nx * _tan + _U.Y * ny * _tan;
                _depth[row + x] = dy < -1e-5f ? -_L.Y / dy : Inf;   // 방향의 F 성분 = 1 → t = 선형 깊이
            }
        }
        Array.Fill(_actor, Inf);
        int rx0 = Res, ry0 = Res, rx1 = -1, ry1 = -1;
        for (var i = 0; i < Spheres; i++)
        {
            var sp = _sph[i]; var cc = new Vector3(sp.X, sp.Y, sp.Z) - _L;
            var z = Vector3.Dot(cc, _F);
            if (z <= sp.W) continue;
            var cu = (Vector3.Dot(cc, _R) / (z * _tan) + 1f) * half - 0.5f; var cv = (1f - Vector3.Dot(cc, _U) / (z * _tan)) * half - 0.5f;
            var rt = sp.W / (z * _tan) * half;
            int x0 = Math.Max(0, (int)MathF.Floor(cu - rt)), x1 = Math.Min(Res - 1, (int)MathF.Ceiling(cu + rt));
            int y0 = Math.Max(0, (int)MathF.Floor(cv - rt)), y1 = Math.Min(Res - 1, (int)MathF.Ceiling(cv + rt));
            for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++)
                {
                    float du = x - cu, dv = y - cv;
                    var q2 = (du * du + dv * dv) / MathF.Max(rt * rt, 1e-6f);
                    if (q2 > 1f) continue;
                    var d = z - sp.W * MathF.Sqrt(1f - q2);
                    var k = y * Res + x;
                    if (d < _actor[k]) _actor[k] = d;
                    if (d < _depth[k]) _depth[k] = d;
                    if (x < rx0) rx0 = x; if (x > rx1) rx1 = x; if (y < ry0) ry0 = y; if (y > ry1) ry1 = y;
                }
        }
        _rx0 = rx0; _ry0 = ry0; _rx1 = rx1; _ry1 = ry1;
        LastBuildUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>광원 텍셀 좌표(u, v · 텍셀 단위 · 중심 = 정수) → 월드 광선 방향(F 성분 1 · 정규화 안 함).</summary>
    public Vector3 TexelDirection(float u, float v)
    {
        var half = Res * 0.5f;
        float nx = (u + 0.5f) / half - 1f, ny = 1f - (v + 0.5f) / half;
        return _F + _R * (nx * _tan) + _U * (ny * _tan);
    }

    // ══════════════════════ 평가(무할당 · 워커 공유 읽기 전용) ══════════════════════

    /// <summary>수용점 p(법선 n) 조도 1 = 비춤 · 0 = 완전 그림자. radius = 사용한 PCF 반경(배우 사각형 밖이면 0).</summary>
    public float EvaluateShadow(Vector3 p, Vector3 n, out int radius)
    {
        radius = 0;
        var d = p - _L;
        var z = Vector3.Dot(d, _F);
        if (!(z > 1e-3f) || _rx1 < 0) return 1f;
        var half = Res * 0.5f;
        var u = (Vector3.Dot(d, _R) / (z * _tan) + 1f) * half - 0.5f; var v = (1f - Vector3.Dot(d, _U) / (z * _tan)) * half - 0.5f;
        var cap = _guard.RadiusCap;
        var tx = (int)MathF.Round(u); var ty = (int)MathF.Round(v);
        if (tx < _rx0 - cap - 2 || tx > _rx1 + cap + 2 || ty < _ry0 - cap - 2 || ty > _ry1 + cap + 2) return 1f;
        var texelM = z * _texK;
        var cosT = Vector3.Dot(n, Vector3.Normalize(-d));
        // 블로커 탐색(결합 맵 · 상한 반경 — 바이어스 부족 시 지면 자기 블로커 = 아크네)
        var bias0 = _guard.Bias(cosT, texelM, cap);
        float bsum = 0; var bn = 0;
        for (var y = Math.Max(0, ty - cap); y <= Math.Min(Res - 1, ty + cap); y++)
        {
            var row = y * Res;
            for (var x = Math.Max(0, tx - cap); x <= Math.Min(Res - 1, tx + cap); x++)
            {
                var a = _depth[row + x];
                if (a < z - bias0) { bsum += a; bn++; }
            }
        }
        if (bn == 0) return 1f;
        var zb = bsum / bn;
        var pen = BaseLightSizeM * MathF.Max(Softness, 0f) * (z - zb) / MathF.Max(zb, 1e-3f) / texelM;
        var r = Math.Clamp((int)MathF.Round(pen), 1, cap);
        radius = r;
        var zref = z - _guard.Bias(cosT, texelM, r);
        int lit = 0, total = 0;
        var xs = Math.Max(0, tx - r); var xe = Math.Min(Res - 1, tx + r);
        var w = xe - xs + 1;
        var vz = new Vector<float>(zref);
        for (var y = Math.Max(0, ty - r); y <= Math.Min(Res - 1, ty + r); y++)
        {
            var row = _depth.AsSpan(y * Res + xs, w);
            var k = 0;
            if (UseSimd && Vector.IsHardwareAccelerated)
            {
                var vc = Vector<float>.Count;
                for (; k <= w - vc; k += vc) lit -= Vector.Sum(Vector.GreaterThanOrEqual(new Vector<float>(row.Slice(k, vc)), vz));
            }
            for (; k < w; k++) if (row[k] >= zref) lit++;
            total += w;
        }
        return total > 0 ? (float)lit / total : 1f;
    }

    public void BeginFrame(GaussianSplatScene scene, in SplatCamera cam, int workers)
    {
        _scene = scene;
        var n = Math.Min(workers, MaxWorkers);
        Array.Clear(_wTicks, 0, n); Array.Clear(_wEval, 0, n); Array.Clear(_wShadow, 0, n); Array.Clear(_wSkip, 0, n);
    }

    public Vector3 Modulate(int worker, int splat, Vector3 position, Vector3 color)
    {
        var s = _scene;
        if (!Enabled || s is null || worker >= MaxWorkers) return color;
        var kind = s.Kind[splat];
        if (kind == ActorKind) return color;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var nrm = kind == GroundKind ? Vector3.UnitY : Vector3.Normalize(_L - position);
        var lit = EvaluateShadow(position, nrm, out var r);
        var outc = color * (1f - Opacity * (1f - lit));
        // 배우 → 환경 상호 반사(바운스 · 근접 감쇠 · 비춤 영역만)
        var d2 = Vector3.DistanceSquared(position, _hip);
        if (BleedStrength > 0f && d2 < 1.44f) outc += ActorAlbedo * (BleedStrength * (1f - d2 / 1.44f) * lit);
        _wTicks[worker] += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        if (r == 0 && lit >= 1f) _wSkip[worker]++; else _wEval[worker]++;
        if (lit < 0.99f) _wShadow[worker]++;
        return outc;
    }

    public void EndFrame()
    {
        long tmax = 0, ev = 0, sh = 0, sk = 0;
        for (var w = 0; w < MaxWorkers; w++) { if (_wTicks[w] > tmax) tmax = _wTicks[w]; ev += _wEval[w]; sh += _wShadow[w]; sk += _wSkip[w]; }
        LastPcfUs = tmax * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        Evaluations = ev; ShadowedSplats = sh; SkippedSplats = sk;
        LastShadowMs = (float)((LastBuildUs + LastPcfUs) / 1000.0);
        _guard.Observe(LastShadowMs);
    }

    /// <summary>뎁스 맵 미니 프리뷰(128² · 8 비트 그레이 BMP · 배우 = 주황 강조) — dst 길이 = 54 + 128·128·3.</summary>
    public void WriteDepthBmp(byte[] dst)
    {
        const int N = 128, Row = N * 3;
        if (dst.Length < 54 + Row * N) return;
        dst.AsSpan(0, 54).Clear();
        dst[0] = (byte)'B'; dst[1] = (byte)'M';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst.AsSpan(2), 54 + Row * N);
        dst[10] = 54; dst[14] = 40;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst.AsSpan(18), N);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst.AsSpan(22), N);
        dst[26] = 1; dst[28] = 24;
        float zmin = Inf, zmax = 0;
        for (var i = 0; i < _depth.Length; i++) { var z = _depth[i]; if (z < Inf) { if (z < zmin) zmin = z; if (z > zmax) zmax = z; } }
        var span = MathF.Max(zmax - zmin, 1e-3f);
        for (var y = 0; y < N; y++)
        {
            var o = 54 + (N - 1 - y) * Row;
            for (var x = 0; x < N; x++)
            {
                var k = (y * 2) * Res + x * 2;
                var z = _depth[k];
                var g = z < Inf ? (byte)(255 - (int)(220 * (z - zmin) / span)) : (byte)0;
                var actor = _actor[k] < Inf;
                dst[o + x * 3] = actor ? (byte)(g / 4) : g; dst[o + x * 3 + 1] = actor ? (byte)(g * 3 / 5) : g; dst[o + x * 3 + 2] = g;
            }
        }
    }
}
