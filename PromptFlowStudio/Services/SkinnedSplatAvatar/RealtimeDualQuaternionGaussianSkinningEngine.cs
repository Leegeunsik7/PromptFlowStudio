using System.Numerics;
using PromptFlowStudio.Services.NeuralRagdoll;
using PromptFlowStudio.Services.SplatEnvironment;
namespace PromptFlowStudio.Services.SkinnedSplatAvatar;

/// <summary>스플랫당 본 가중치(최대 4 본 · 20 B 고정 구조체). B0 = 소속 분절(드레싱 · 신장 · 자체 그림자 제외 기준).</summary>
public struct BoneWeights4
{
    public byte B0, B1, B2, B3;
    public float W0, W1, W2, W3;
}

/// <summary>
/// [PromptFlow Studio v17.57.0] 🧬 실시간 듀얼 쿼터니언 4D 가우시안 아바타 스키닝 엔진(<see cref="Skin"/> 무할당 · 60 fps).
/// 골격 = v17.37 래그돌 24 관절(v17.33 이족 18 관절은 <see cref="RagdollSkeleton.MapBiped"/> 매핑). 분절 b(부모 관절 → 관절 b)마다
/// 본 변환(회전 R_b · 바인드 피벗 → 현재 피벗 · 축 신장 s_b)을 단위 듀얼 쿼터니언 q̂_b = q_r + ε·½(t·q_r) 로 둔다.
/// ① 스칼라: 4 본 DQ 가중 합(대척 부호 = B0 실수부 기준 · 실수부 노름 정규화 = DLB) → 회전 q · 평행이동 t = 2(q_d q_r*) ·
///    소속 분절 축 신장(바인드 공간 S = I + (s−1)aaᵀ · μ, Σ ← SΣS) → SoA 기록.
/// ② Vector&lt;float&gt;: q → R(3×3) · μ' = Rμ + t · Σ' = R Σ Rᵀ(대칭 6 성분)를 장면 SoA 에 직접 기록(스칼라 꼬리 동일 수식).
/// ③ <see cref="VolumetricDeterminantClampingGuard"/>: det(Σ')/det(Σ₀) 이탈 클램프 · 비정상 값 복구 · MaxSigma 갱신.
/// 상주 워커 풀이 4 096 스플랫 청크를 나눠 처리한다. <see cref="UseDqs"/> = false 면 선형 블렌드 스키닝(LBS · 캔디 래퍼 비교용 · 스칼라).
/// </summary>
public sealed class RealtimeDualQuaternionGaussianSkinningEngine : IDisposable
{
    public const int MaxBones = 32, Chunk = 4096, StatN = 8;
    private readonly float[] _brx = new float[MaxBones], _bry = new float[MaxBones], _brz = new float[MaxBones], _brw = new float[MaxBones];
    private readonly float[] _bdx = new float[MaxBones], _bdy = new float[MaxBones], _bdz = new float[MaxBones], _bdw = new float[MaxBones];
    private readonly float[] _bs = new float[MaxBones];
    private readonly Vector3[] _bAxis = new Vector3[MaxBones], _bPivot = new Vector3[MaxBones];
    private readonly Quaternion[] _bRot = new Quaternion[MaxBones];
    private readonly Vector3[] _bT = new Vector3[MaxBones];
    private readonly SplatWorkerPool _pool;
    private readonly long[] _wStat;
    public readonly int Capacity;
    // 바인드(휴지) 데이터
    private readonly float[] _mx, _my, _mz, _c00, _c01, _c02, _c11, _c12, _c22, _det0, _sig0;
    private readonly BoneWeights4[] _w;
    // 단계 ① 결과(SoA)
    private readonly float[] _qx, _qy, _qz, _qw, _tx, _ty, _tz, _px, _py, _pz, _s00, _s01, _s02, _s11, _s12, _s22;
    private GaussianSplatScene? _scene;
    private VolumetricDeterminantClampingGuard? _guard;
    private int _start, _n;

    public RealtimeDualQuaternionGaussianSkinningEngine(int capacity, int workers = -1)
    {
        Capacity = Math.Max(capacity, 1);
        var wn = workers < 0 ? Math.Clamp(Environment.ProcessorCount - 1, 0, 7) : Math.Min(workers, 15);
        _pool = new SplatWorkerPool(wn, RunChunk, "PF.DqAvatar.W");
        _wStat = new long[(wn + 1) * StatN];
        float[] F() => new float[Capacity];
        _mx = F(); _my = F(); _mz = F(); _c00 = F(); _c01 = F(); _c02 = F(); _c11 = F(); _c12 = F(); _c22 = F(); _det0 = F(); _sig0 = F();
        _qx = F(); _qy = F(); _qz = F(); _qw = F(); _tx = F(); _ty = F(); _tz = F(); _px = F(); _py = F(); _pz = F();
        _s00 = F(); _s01 = F(); _s02 = F(); _s11 = F(); _s12 = F(); _s22 = F();
        _w = new BoneWeights4[Capacity];
        for (var b = 0; b < MaxBones; b++) { _brw[b] = 1f; _bs[b] = 1f; _bRot[b] = Quaternion.Identity; _bAxis[b] = Vector3.UnitY; }
    }

    public bool UseDqs { get; set; } = true;
    public bool UseSimd { get; set; } = Vector.IsHardwareAccelerated;
    public int Count => _n;
    public int Start => _start;
    public int Workers => _pool.Workers;
    public long Frames { get; private set; }
    public long FallbackBlends { get; private set; }
    public double LastSkinUs { get; private set; }
    public ref readonly BoneWeights4 Weights(int i) => ref _w[i];
    public float RestDet(int i) => _det0[i];
    public Vector3 RestMean(int i) => new(_mx[i], _my[i], _mz[i]);
    public Quaternion BoneRotation(int b) => _bRot[b];
    public Vector3 BonePivot(int b) => _bPivot[b];
    public float BoneStretch(int b) => _bs[b];
    /// <summary>스플랫 k 의 블렌드 회전(직전 DQS 프레임)으로 휴지 벡터 회전.</summary>
    public Vector3 RotateBlended(int k, Vector3 v) => Vector3.Transform(v, new Quaternion(_qx[k], _qy[k], _qz[k], _qw[k]));

    public static float Det(float a, float b, float c, float d, float e, float f) => a * (d * f - e * e) - b * (b * f - e * c) + c * (b * e - d * c);

    /// <summary>장면 슬롯 [start, start+count) 의 현재 값을 바인드 포즈로 고정(비핫패스).</summary>
    public void Bind(GaussianSplatScene scene, int start, int count, ReadOnlySpan<BoneWeights4> weights)
    {
        if (count > Capacity) throw new ArgumentOutOfRangeException(nameof(count));
        _scene = scene; _start = start; _n = count;
        for (var k = 0; k < count; k++)
        {
            var i = start + k;
            _mx[k] = scene.X[i]; _my[k] = scene.Y[i]; _mz[k] = scene.Z[i];
            _c00[k] = scene.C00[i]; _c01[k] = scene.C01[i]; _c02[k] = scene.C02[i]; _c11[k] = scene.C11[i]; _c12[k] = scene.C12[i]; _c22[k] = scene.C22[i];
            _det0[k] = Det(_c00[k], _c01[k], _c02[k], _c11[k], _c12[k], _c22[k]);
            _sig0[k] = scene.MaxSigma[i];
            var w = weights[k];
            var sum = w.W0 + w.W1 + w.W2 + w.W3;
            if (sum > 1e-6f) { w.W0 /= sum; w.W1 /= sum; w.W2 /= sum; w.W3 /= sum; } else { w.W0 = 1f; w.W1 = w.W2 = w.W3 = 0f; }
            _w[k] = w;
        }
    }

    /// <summary>본 b 변환: x' = rot·(S_b(x) − restPivot) + curPivot · S_b = 축 신장(restAxis · stretch).</summary>
    public void SetBone(int b, Quaternion rot, Vector3 restPivot, Vector3 curPivot, float stretch = 1f, Vector3 restAxis = default)
    {
        if ((uint)b >= MaxBones) return;
        var n = rot.Length();
        rot = n > 1e-8f && float.IsFinite(n) ? rot * (1f / n) : Quaternion.Identity;
        var t = curPivot - Vector3.Transform(restPivot, rot);
        _bRot[b] = rot; _bT[b] = t; _bPivot[b] = restPivot;
        _bs[b] = float.IsFinite(stretch) ? stretch : 1f;
        if (restAxis.LengthSquared() > 1e-10f) _bAxis[b] = Vector3.Normalize(restAxis);
        // q_d = ½ (t, 0) q_r
        var d = new Quaternion(t, 0f) * rot * 0.5f;
        _brx[b] = rot.X; _bry[b] = rot.Y; _brz[b] = rot.Z; _brw[b] = rot.W;
        _bdx[b] = d.X; _bdy[b] = d.Y; _bdz[b] = d.Z; _bdw[b] = d.W;
    }

    /// <summary>프레임 스키닝(무할당) — 결과는 바인드된 장면 슬롯에 기록.</summary>
    public void Skin(VolumetricDeterminantClampingGuard guard)
    {
        if (_scene is null || _n == 0) return;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _guard = guard;
        guard.BeginFrame(_pool.Workers + 1);
        Array.Clear(_wStat);
        _pool.Run(0, (_n + Chunk - 1) / Chunk, 1);
        long fb = 0;
        for (var w = 0; w <= _pool.Workers; w++) fb += _wStat[w * StatN];
        FallbackBlends = fb;
        guard.EndFrame();
        Frames++;
        LastSkinUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private void RunChunk(int job, int w, int cs, int ce)
    {
        for (var c = cs; c < ce; c++)
        {
            int s = c * Chunk, e = Math.Min(s + Chunk, _n);
            if (UseDqs) { Blend(w, s, e); Transform(s, e); }
            else Lbs(s, e);
            _guard!.Apply(w, _scene!, _start, s, e, _det0, _c00, _c01, _c02, _c11, _c12, _c22, _sig0);
        }
    }

    /// <summary>바인드 공간 축 신장(B0 분절) — 결과 μ_s · Σ_s 를 단계 SoA 에 기록.</summary>
    private void Stretch(int k, int b0)
    {
        float px = _mx[k], py = _my[k], pz = _mz[k];
        float a00 = _c00[k], a01 = _c01[k], a02 = _c02[k], a11 = _c11[k], a12 = _c12[k], a22 = _c22[k];
        var kk = _bs[b0] - 1f;
        if (MathF.Abs(kk) > 1e-5f)
        {
            var a = _bAxis[b0]; var pv = _bPivot[b0];
            var along = (px - pv.X) * a.X + (py - pv.Y) * a.Y + (pz - pv.Z) * a.Z;
            px += kk * along * a.X; py += kk * along * a.Y; pz += kk * along * a.Z;
            // SΣS = Σ + k(a vᵀ + v aᵀ) + k² α a aᵀ · v = Σa · α = aᵀΣa
            float vx = a00 * a.X + a01 * a.Y + a02 * a.Z, vy = a01 * a.X + a11 * a.Y + a12 * a.Z, vz = a02 * a.X + a12 * a.Y + a22 * a.Z;
            var al = a.X * vx + a.Y * vy + a.Z * vz; var k2 = kk * kk * al;
            a00 += kk * 2f * a.X * vx + k2 * a.X * a.X; a01 += kk * (a.X * vy + vx * a.Y) + k2 * a.X * a.Y; a02 += kk * (a.X * vz + vx * a.Z) + k2 * a.X * a.Z;
            a11 += kk * 2f * a.Y * vy + k2 * a.Y * a.Y; a12 += kk * (a.Y * vz + vy * a.Z) + k2 * a.Y * a.Z; a22 += kk * 2f * a.Z * vz + k2 * a.Z * a.Z;
        }
        _px[k] = px; _py[k] = py; _pz[k] = pz;
        _s00[k] = a00; _s01[k] = a01; _s02[k] = a02; _s11[k] = a11; _s12[k] = a12; _s22[k] = a22;
    }

    /// <summary>① DQ 가중 합 · 정규화 · 평행이동 추출(스칼라).</summary>
    private void Blend(int w, int s, int e)
    {
        long fb = 0;
        for (var k = s; k < e; k++)
        {
            ref readonly var wt = ref _w[k];
            int b0 = wt.B0, b1 = wt.B1, b2 = wt.B2, b3 = wt.B3;
            float rx0 = _brx[b0], ry0 = _bry[b0], rz0 = _brz[b0], rw0 = _brw[b0];
            float w0 = wt.W0;
            float rx = rx0 * w0, ry = ry0 * w0, rz = rz0 * w0, rw = rw0 * w0;
            float dx = _bdx[b0] * w0, dy = _bdy[b0] * w0, dz = _bdz[b0] * w0, dw = _bdw[b0] * w0;
            Acc(b1, wt.W1, rx0, ry0, rz0, rw0, ref rx, ref ry, ref rz, ref rw, ref dx, ref dy, ref dz, ref dw);
            Acc(b2, wt.W2, rx0, ry0, rz0, rw0, ref rx, ref ry, ref rz, ref rw, ref dx, ref dy, ref dz, ref dw);
            Acc(b3, wt.W3, rx0, ry0, rz0, rw0, ref rx, ref ry, ref rz, ref rw, ref dx, ref dy, ref dz, ref dw);
            var n2 = rx * rx + ry * ry + rz * rz + rw * rw;
            if (!(n2 > 1e-12f) || !float.IsFinite(n2))
            {
                fb++;
                rx = rx0; ry = ry0; rz = rz0; rw = rw0; dx = _bdx[b0]; dy = _bdy[b0]; dz = _bdz[b0]; dw = _bdw[b0]; n2 = 1f;
            }
            var inv = 1f / MathF.Sqrt(n2);
            rx *= inv; ry *= inv; rz *= inv; rw *= inv; dx *= inv; dy *= inv; dz *= inv; dw *= inv;
            // t = 2 · (q_d q_r*) 벡터부
            _tx[k] = 2f * (-dw * rx + dx * rw - dy * rz + dz * ry);
            _ty[k] = 2f * (-dw * ry + dy * rw - dz * rx + dx * rz);
            _tz[k] = 2f * (-dw * rz + dz * rw - dx * ry + dy * rx);
            _qx[k] = rx; _qy[k] = ry; _qz[k] = rz; _qw[k] = rw;
            Stretch(k, b0);
        }
        _wStat[w * StatN] += fb;
    }

    private void Acc(int b, float wk, float rx0, float ry0, float rz0, float rw0, ref float rx, ref float ry, ref float rz, ref float rw, ref float dx, ref float dy, ref float dz, ref float dw)
    {
        if (wk <= 0f) return;
        // 대척 정렬: B0 실수부와 내적 음수면 부호 반전(최단 경로 블렌드)
        if (_brx[b] * rx0 + _bry[b] * ry0 + _brz[b] * rz0 + _brw[b] * rw0 < 0f) wk = -wk;
        rx += _brx[b] * wk; ry += _bry[b] * wk; rz += _brz[b] * wk; rw += _brw[b] * wk;
        dx += _bdx[b] * wk; dy += _bdy[b] * wk; dz += _bdz[b] * wk; dw += _bdw[b] * wk;
    }

    /// <summary>② q → R · μ' = Rμ + t · Σ' = RΣRᵀ(Vector&lt;float&gt; · 스칼라 꼬리 동일 수식).</summary>
    private void Transform(int s, int e)
    {
        var sc = _scene!; var o = _start;
        var k = s;
        if (UseSimd && Vector.IsHardwareAccelerated)
        {
            var vc = Vector<float>.Count;
            var one = Vector<float>.One; var two = new Vector<float>(2f);
            for (; k <= e - vc; k += vc)
            {
                var x = new Vector<float>(_qx, k); var y = new Vector<float>(_qy, k); var z = new Vector<float>(_qz, k); var w = new Vector<float>(_qw, k);
                var r00 = one - two * (y * y + z * z); var r01 = two * (x * y - w * z); var r02 = two * (x * z + w * y);
                var r10 = two * (x * y + w * z); var r11 = one - two * (x * x + z * z); var r12 = two * (y * z - w * x);
                var r20 = two * (x * z - w * y); var r21 = two * (y * z + w * x); var r22 = one - two * (x * x + y * y);
                var px = new Vector<float>(_px, k); var py = new Vector<float>(_py, k); var pz = new Vector<float>(_pz, k);
                (r00 * px + r01 * py + r02 * pz + new Vector<float>(_tx, k)).CopyTo(sc.X, o + k);
                (r10 * px + r11 * py + r12 * pz + new Vector<float>(_ty, k)).CopyTo(sc.Y, o + k);
                (r20 * px + r21 * py + r22 * pz + new Vector<float>(_tz, k)).CopyTo(sc.Z, o + k);
                var a = new Vector<float>(_s00, k); var b = new Vector<float>(_s01, k); var c = new Vector<float>(_s02, k);
                var d = new Vector<float>(_s11, k); var f = new Vector<float>(_s12, k); var g = new Vector<float>(_s22, k);
                // M = R Σ
                var m00 = r00 * a + r01 * b + r02 * c; var m01 = r00 * b + r01 * d + r02 * f; var m02 = r00 * c + r01 * f + r02 * g;
                var m10 = r10 * a + r11 * b + r12 * c; var m11 = r10 * b + r11 * d + r12 * f; var m12 = r10 * c + r11 * f + r12 * g;
                var m20 = r20 * a + r21 * b + r22 * c; var m21 = r20 * b + r21 * d + r22 * f; var m22 = r20 * c + r21 * f + r22 * g;
                (m00 * r00 + m01 * r01 + m02 * r02).CopyTo(sc.C00, o + k);
                (m00 * r10 + m01 * r11 + m02 * r12).CopyTo(sc.C01, o + k);
                (m00 * r20 + m01 * r21 + m02 * r22).CopyTo(sc.C02, o + k);
                (m10 * r10 + m11 * r11 + m12 * r12).CopyTo(sc.C11, o + k);
                (m10 * r20 + m11 * r21 + m12 * r22).CopyTo(sc.C12, o + k);
                (m20 * r20 + m21 * r21 + m22 * r22).CopyTo(sc.C22, o + k);
            }
        }
        for (; k < e; k++)
        {
            float x = _qx[k], y = _qy[k], z = _qz[k], w = _qw[k];
            float r00 = 1f - 2f * (y * y + z * z), r01 = 2f * (x * y - w * z), r02 = 2f * (x * z + w * y);
            float r10 = 2f * (x * y + w * z), r11 = 1f - 2f * (x * x + z * z), r12 = 2f * (y * z - w * x);
            float r20 = 2f * (x * z - w * y), r21 = 2f * (y * z + w * x), r22 = 1f - 2f * (x * x + y * y);
            float px = _px[k], py = _py[k], pz = _pz[k];
            sc.X[o + k] = r00 * px + r01 * py + r02 * pz + _tx[k];
            sc.Y[o + k] = r10 * px + r11 * py + r12 * pz + _ty[k];
            sc.Z[o + k] = r20 * px + r21 * py + r22 * pz + _tz[k];
            float a = _s00[k], b = _s01[k], c = _s02[k], d = _s11[k], f = _s12[k], g = _s22[k];
            float m00 = r00 * a + r01 * b + r02 * c, m01 = r00 * b + r01 * d + r02 * f, m02 = r00 * c + r01 * f + r02 * g;
            float m10 = r10 * a + r11 * b + r12 * c, m11 = r10 * b + r11 * d + r12 * f, m12 = r10 * c + r11 * f + r12 * g;
            float m20 = r20 * a + r21 * b + r22 * c, m21 = r20 * b + r21 * d + r22 * f, m22 = r20 * c + r21 * f + r22 * g;
            sc.C00[o + k] = m00 * r00 + m01 * r01 + m02 * r02;
            sc.C01[o + k] = m00 * r10 + m01 * r11 + m02 * r12;
            sc.C02[o + k] = m00 * r20 + m01 * r21 + m02 * r22;
            sc.C11[o + k] = m10 * r10 + m11 * r11 + m12 * r12;
            sc.C12[o + k] = m10 * r20 + m11 * r21 + m12 * r22;
            sc.C22[o + k] = m20 * r20 + m21 * r21 + m22 * r22;
        }
    }

    /// <summary>LBS(비교용 · 스칼라) — 행렬 선형 블렌드: μ' = Σw(R_k μ + t_k) · Σ' = M Σ Mᵀ(M = Σ w R_k).</summary>
    private void Lbs(int s, int e)
    {
        var sc = _scene!; var o = _start;
        Span<int> bs = stackalloc int[4]; Span<float> ws = stackalloc float[4];
        for (var k = s; k < e; k++)
        {
            ref readonly var wt = ref _w[k];
            Stretch(k, wt.B0);
            bs[0] = wt.B0; bs[1] = wt.B1; bs[2] = wt.B2; bs[3] = wt.B3; ws[0] = wt.W0; ws[1] = wt.W1; ws[2] = wt.W2; ws[3] = wt.W3;
            var M = new Matrix4x4(); var t = Vector3.Zero;
            for (var j = 0; j < 4; j++)
            {
                if (ws[j] <= 0f) continue;
                var R = Matrix4x4.CreateFromQuaternion(_bRot[bs[j]]);
                M.M11 += R.M11 * ws[j]; M.M12 += R.M12 * ws[j]; M.M13 += R.M13 * ws[j];
                M.M21 += R.M21 * ws[j]; M.M22 += R.M22 * ws[j]; M.M23 += R.M23 * ws[j];
                M.M31 += R.M31 * ws[j]; M.M32 += R.M32 * ws[j]; M.M33 += R.M33 * ws[j];
                t += _bT[bs[j]] * ws[j];
            }
            // System.Numerics 행 벡터 규약: v' = v·M → 열 형식 R = Mᵀ
            float r00 = M.M11, r01 = M.M21, r02 = M.M31, r10 = M.M12, r11 = M.M22, r12 = M.M32, r20 = M.M13, r21 = M.M23, r22 = M.M33;
            float px = _px[k], py = _py[k], pz = _pz[k];
            sc.X[o + k] = r00 * px + r01 * py + r02 * pz + t.X;
            sc.Y[o + k] = r10 * px + r11 * py + r12 * pz + t.Y;
            sc.Z[o + k] = r20 * px + r21 * py + r22 * pz + t.Z;
            float a = _s00[k], b = _s01[k], c = _s02[k], d = _s11[k], f = _s12[k], g = _s22[k];
            float m00 = r00 * a + r01 * b + r02 * c, m01 = r00 * b + r01 * d + r02 * f, m02 = r00 * c + r01 * f + r02 * g;
            float m10 = r10 * a + r11 * b + r12 * c, m11 = r10 * b + r11 * d + r12 * f, m12 = r10 * c + r11 * f + r12 * g;
            float m20 = r20 * a + r21 * b + r22 * c, m21 = r20 * b + r21 * d + r22 * f, m22 = r20 * c + r21 * f + r22 * g;
            sc.C00[o + k] = m00 * r00 + m01 * r01 + m02 * r02;
            sc.C01[o + k] = m00 * r10 + m01 * r11 + m02 * r12;
            sc.C02[o + k] = m00 * r20 + m01 * r21 + m02 * r22;
            sc.C11[o + k] = m10 * r10 + m11 * r11 + m12 * r12;
            sc.C12[o + k] = m10 * r20 + m11 * r21 + m12 * r22;
            sc.C22[o + k] = m20 * r20 + m21 * r21 + m22 * r22;
        }
    }

    public void Dispose() => _pool.Dispose();
}
