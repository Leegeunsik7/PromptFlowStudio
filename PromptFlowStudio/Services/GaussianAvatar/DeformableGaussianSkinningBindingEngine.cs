using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using PromptFlowStudio.Services.AnatomicalTissue;
using PromptFlowStudio.Services.Garment;
using PromptFlowStudio.Services.StrandHair;

namespace PromptFlowStudio.Services.GaussianAvatar;

public enum GaussianSurface : byte { Muscle = 0, Garment, Hair }

public enum GaussianSimdPath : byte { Scalar = 0, V256 }

/// <summary>
/// [PromptFlow Studio v17.20.0] 🔮 실시간 4D 변형 가우시안 스키닝 &amp; 표면 바인딩 엔진.
/// v17.14 근육 피부 정점 · v17.16 의상 PBD 입자 · v17.15 헤어 가이드 노드를 단일 앵커 클라우드로 모으고,
/// 앵커마다 (자신 · 이웃 2점) 삼각 프레임의 휴지 → 현재 강체 변환을 듀얼 쿼터니언으로 만든다.
/// 가우시안(중심 · 회전 쿼터니언 · 스케일 = 공분산 RSSᵀRᵀ · 불투명도)은 앵커 4개에 DQS 블렌딩(반구 정렬)으로 바인딩되며,
/// 블렌딩(스칼라 gather) → 정규화 · 중심 변환 · 회전 합성(Vector256 SoA)의 2단계로 무할당 갱신한다. 경로 간 비트 동일.
/// </summary>
public sealed class DeformableGaussianSkinningBindingEngine
{
    public const int DefaultCount = 100352, Influences = 4;
    public const float MuscleShare = 0.55f, GarmentShare = 0.35f;

    public readonly int N, A, MuscleStart, GarmentStart, HairStart;
    // 앵커(점) 클라우드
    private readonly float[] _px, _py, _pz;
    private readonly int[] _nb, _nc;
    private readonly Vector3[] _restO;
    private readonly Quaternion[] _restQInv, _restQ;
    private readonly float[] _dq;                       // 앵커당 8: 실수부 xyzw · 이중부 xyzw
    // 가우시안 휴지 상태
    public readonly float[] RestX, RestY, RestZ, RestQx, RestQy, RestQz, RestQw;
    public readonly float[] ScaleX, ScaleY, ScaleZ, Opacity;
    public readonly int[] Bone;                         // N × 4
    public readonly float[] Weight;                     // N × 4
    public readonly GaussianSurface[] SurfaceOf;
    public readonly int[] HostPoint;                    // 소속 그룹 내 지역 인덱스(근육 정점 · 의상 입자 · 헤어 노드)
    // 변형 결과
    public readonly float[] X, Y, Z, Qx, Qy, Qz, Qw;
    public readonly float[] Sx, Sy, Sz, Sw;             // 스키닝 회전(정규화 실수부) — SH 시점 방향 정준화용
    // 블렌딩 스크래치(SoA)
    private readonly float[] _r0, _r1, _r2, _r3, _d0, _d1, _d2, _d3;

    private readonly NeuralAnatomicalMuscleFasciaEngine _muscle;
    private readonly MultiLayerGarmentPbdDynamicsEngine _garment;
    private readonly StrandHairPbdDynamicsEngine _hair;

    public DeformableGaussianSkinningBindingEngine(NeuralAnatomicalMuscleFasciaEngine muscle, MultiLayerGarmentPbdDynamicsEngine garment, StrandHairPbdDynamicsEngine hair, int count = DefaultCount, uint seed = 0x9E3779B9u)
    {
        if (count <= 0 || count % 8 != 0) throw new ArgumentException("가우시안 수는 8 의 배수");
        _muscle = muscle; _garment = garment; _hair = hair;
        N = count;
        MuscleStart = 0; GarmentStart = NeuralAnatomicalMuscleFasciaEngine.V; HairStart = GarmentStart + garment.P;
        A = HairStart + hair.X.Length;
        _px = new float[A]; _py = new float[A]; _pz = new float[A];
        _nb = new int[A]; _nc = new int[A];
        _restO = new Vector3[A]; _restQInv = new Quaternion[A]; _restQ = new Quaternion[A];
        _dq = new float[A * 8];
        RestX = new float[N]; RestY = new float[N]; RestZ = new float[N]; RestQx = new float[N]; RestQy = new float[N]; RestQz = new float[N]; RestQw = new float[N];
        ScaleX = new float[N]; ScaleY = new float[N]; ScaleZ = new float[N]; Opacity = new float[N];
        Bone = new int[N * Influences]; Weight = new float[N * Influences];
        SurfaceOf = new GaussianSurface[N]; HostPoint = new int[N];
        X = new float[N]; Y = new float[N]; Z = new float[N]; Qx = new float[N]; Qy = new float[N]; Qz = new float[N]; Qw = new float[N];
        Sx = new float[N]; Sy = new float[N]; Sz = new float[N]; Sw = new float[N];
        _r0 = new float[N]; _r1 = new float[N]; _r2 = new float[N]; _r3 = new float[N]; _d0 = new float[N]; _d1 = new float[N]; _d2 = new float[N]; _d3 = new float[N];
        Path = Vector256.IsHardwareAccelerated && BitConverter.IsLittleEndian ? GaussianSimdPath.V256 : GaussianSimdPath.Scalar;
        BuildTopology();
        Gather();
        for (var a = 0; a < A; a++)
        {
            Frame(a, out var o, out var q, out var ok);
            if (!ok) q = Quaternion.Identity;
            _restO[a] = o; _restQ[a] = q; _restQInv[a] = Quaternion.Conjugate(q);
            var k = a * 8;
            _dq[k + 3] = 1f;
        }
        Bind(seed);
        Update();
    }

    public GaussianSimdPath Path { get; set; }
    public double LastGatherUs { get; private set; }
    public double LastAnchorUs { get; private set; }
    public double LastBlendUs { get; private set; }
    public double LastApplyUs { get; private set; }
    public double LastUpdateUs { get; private set; }
    public int DegenerateAnchors { get; private set; }
    public float MaxDisplacementMm { get; private set; }
    public long Updates { get; private set; }
    public int Count(GaussianSurface s) => s == GaussianSurface.Muscle ? _cnt0 : s == GaussianSurface.Garment ? _cnt1 : _cnt2;
    private int _cnt0, _cnt1, _cnt2;

    public Vector3 Center(int i) => new(X[i], Y[i], Z[i]);
    public Quaternion Rotation(int i) => new(Qx[i], Qy[i], Qz[i], Qw[i]);
    public Vector3 RestCenter(int i) => new(RestX[i], RestY[i], RestZ[i]);
    public Quaternion RestRotation(int i) => new(RestQx[i], RestQy[i], RestQz[i], RestQw[i]);
    public Vector3 AnchorPoint(int a) => new(_px[a], _py[a], _pz[a]);

    /// <summary>3D 공분산 Σ = R·S²·Rᵀ 상삼각 6 성분(xx · xy · xz · yy · yz · zz).</summary>
    public void Covariance(int i, Span<float> dst) => Covariance(Qx[i], Qy[i], Qz[i], Qw[i], ScaleX[i], ScaleY[i], ScaleZ[i], dst);

    public static void Covariance(float qx, float qy, float qz, float qw, float sx, float sy, float sz, Span<float> dst)
    {
        Covariance(qx, qy, qz, qw, sx, sy, sz, out dst[0], out dst[1], out dst[2], out dst[3], out dst[4], out dst[5]);
    }

    public static void Covariance(float qx, float qy, float qz, float qw, float sx, float sy, float sz, out float xx2, out float xy2, out float xz2, out float yy2, out float yz2, out float zz2)
    {
        float xx = qx * qx, yy = qy * qy, zz = qz * qz, xy = qx * qy, xz = qx * qz, yz = qy * qz, wx = qw * qx, wy = qw * qy, wz = qw * qz;
        float m00 = 1f - 2f * (yy + zz), m01 = 2f * (xy - wz), m02 = 2f * (xz + wy);
        float m10 = 2f * (xy + wz), m11 = 1f - 2f * (xx + zz), m12 = 2f * (yz - wx);
        float m20 = 2f * (xz - wy), m21 = 2f * (yz + wx), m22 = 1f - 2f * (xx + yy);
        float a = sx * sx, b = sy * sy, c = sz * sz;
        xx2 = m00 * m00 * a + m01 * m01 * b + m02 * m02 * c;
        xy2 = m00 * m10 * a + m01 * m11 * b + m02 * m12 * c;
        xz2 = m00 * m20 * a + m01 * m21 * b + m02 * m22 * c;
        yy2 = m10 * m10 * a + m11 * m11 * b + m12 * m12 * c;
        yz2 = m10 * m20 * a + m11 * m21 * b + m12 * m22 * c;
        zz2 = m20 * m20 * a + m21 * m21 * b + m22 * m22 * c;
    }

    // ══════════════════════ 바인딩 ══════════════════════

    private void BuildTopology()
    {
        const int segs = NeuralAnatomicalMuscleFasciaEngine.Segs, rings = NeuralAnatomicalMuscleFasciaEngine.Rings;
        for (var v = 0; v < NeuralAnatomicalMuscleFasciaEngine.V; v++)
        {
            int t = v / NeuralAnatomicalMuscleFasciaEngine.PerTube, r = v / segs % rings, s = v % segs;
            _nb[v] = NeuralAnatomicalMuscleFasciaEngine.Vertex(t, r, (s + 1) % segs);
            _nc[v] = NeuralAnatomicalMuscleFasciaEngine.Vertex(t, r < rings - 1 ? r + 1 : r - 1, s);
        }
        var p = _garment.P;
        for (var i = 0; i < p; i++) { _nb[GarmentStart + i] = -1; _nc[GarmentStart + i] = -1; }
        for (var e = 0; e < _garment.EA.Length; e++)
        {
            int a = _garment.EA[e], b = _garment.EB[e];
            if (a == b || (uint)a >= (uint)p || (uint)b >= (uint)p) continue;
            Link(GarmentStart + a, GarmentStart + b);
            Link(GarmentStart + b, GarmentStart + a);
        }
        for (var i = 0; i < p; i++)
        {
            var a = GarmentStart + i;
            if (_nb[a] < 0) _nb[a] = GarmentStart + (i + 1) % p;
            if (_nc[a] < 0 || _nc[a] == _nb[a]) _nc[a] = GarmentStart + (i + 2) % p;
        }
        int g = _hair.G, n = StrandHairPbdDynamicsEngine.N;
        for (var k = 0; k < n; k++)
            for (var j = 0; j < g; j++)
            {
                var a = HairStart + k * g + j;
                _nb[a] = HairStart + (k < n - 1 ? k + 1 : k - 1) * g + j;
                _nc[a] = HairStart + k * g + (j + 1) % g;
            }
    }

    private void Link(int a, int b)
    {
        if (_nb[a] < 0) _nb[a] = b;
        else if (_nc[a] < 0 && _nb[a] != b) _nc[a] = b;
    }

    private static uint Next(ref uint s) { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return s; }
    private static float U01(ref uint s) => (Next(ref s) >> 8) * (1f / 16777216f);

    private void Bind(uint seed)
    {
        var s = seed == 0 ? 1u : seed;
        int nm = (int)(N * MuscleShare), ng = (int)(N * GarmentShare);
        for (var i = 0; i < N; i++)
        {
            GaussianSurface surf; int a, local;
            if (i < nm) { surf = GaussianSurface.Muscle; local = (int)(Next(ref s) % (uint)NeuralAnatomicalMuscleFasciaEngine.V); a = MuscleStart + local; _cnt0++; }
            else if (i < nm + ng) { surf = GaussianSurface.Garment; local = (int)(Next(ref s) % (uint)_garment.P); a = GarmentStart + local; _cnt1++; }
            else { surf = GaussianSurface.Hair; local = (int)(Next(ref s) % (uint)_hair.X.Length); a = HairStart + local; _cnt2++; }
            SurfaceOf[i] = surf; HostPoint[i] = local;
            int b = _nb[a], c = _nc[a];
            var o = _restO[a];
            var e1 = _restO[b] - o; var e2 = _restO[c] - o;
            float l1 = e1.Length(), l2 = e2.Length();
            float u = U01(ref s), v = U01(ref s);              // 앵커 → 이웃 b/c 사각 패치 전체(링 간 공백 방지)
            var rq = _restQ[a];
            var nrm = Vector3.Transform(Vector3.UnitZ, rq);
            Vector3 pos; Quaternion q; float sx, sy, sz, op;
            if (surf == GaussianSurface.Hair)
            {
                pos = o + e1 * u + e2 * (v * 0.1f);
                q = rq;                                      // 국소 x = 스트랜드 방향
                sx = Math.Clamp(l1 * 0.35f, 0.002f, 0.012f); sy = 0.0009f; sz = 0.0006f; op = 0.45f + 0.35f * U01(ref s);
            }
            else
            {
                pos = o + e1 * u + e2 * v + nrm * ((U01(ref s) - 0.5f) * 0.0015f);
                var spin = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, U01(ref s) * MathF.PI);
                q = Quaternion.Normalize(Quaternion.Concatenate(spin, rq));   // 국소 z = 표면 법선(서펠)
                // 표면 밀도 기반 σ(≈ 4 m² / 9 만 → 간격 ≈ 7 mm · σ ≈ 반간격) — 메시 간격(최대 수 cm)에 비례시키면 픽셀당 수십 겹 과중첩
                var span = MathF.Max(0.003f, MathF.Min(0.008f, 0.25f * (l1 + l2)));
                sx = span * (0.45f + 0.3f * U01(ref s)); sy = span * (0.45f + 0.3f * U01(ref s)); sz = 0.12f * MathF.Min(sx, sy);
                op = surf == GaussianSurface.Muscle ? 0.75f + 0.2f * U01(ref s) : 0.65f + 0.3f * U01(ref s);
            }
            RestX[i] = pos.X; RestY[i] = pos.Y; RestZ[i] = pos.Z;
            RestQx[i] = q.X; RestQy[i] = q.Y; RestQz[i] = q.Z; RestQw[i] = q.W;
            ScaleX[i] = sx; ScaleY[i] = sy; ScaleZ[i] = sz; Opacity[i] = op;
            // 영향 앵커: 자신 · 이웃 b · 이웃 c · b 의 이웃 — 휴지 거리 역가중
            var k4 = i * Influences;
            Bone[k4] = a; Bone[k4 + 1] = b; Bone[k4 + 2] = c; Bone[k4 + 3] = _nb[b] == a ? _nc[b] : _nb[b];
            var sum = 0f;
            for (var k = 0; k < Influences; k++)
            {
                var w = 1f / (Vector3.Distance(pos, _restO[Bone[k4 + k]]) + 0.004f);
                Weight[k4 + k] = w; sum += w;
            }
            for (var k = 0; k < Influences; k++) Weight[k4 + k] /= sum;
        }
    }

    // ══════════════════════ 프레임 갱신 ══════════════════════

    private void Gather()
    {
        _muscle.Px.AsSpan().CopyTo(_px.AsSpan(MuscleStart)); _muscle.Py.AsSpan().CopyTo(_py.AsSpan(MuscleStart)); _muscle.Pz.AsSpan().CopyTo(_pz.AsSpan(MuscleStart));
        var p = _garment.P;
        _garment.X.AsSpan(0, p).CopyTo(_px.AsSpan(GarmentStart)); _garment.Y.AsSpan(0, p).CopyTo(_py.AsSpan(GarmentStart)); _garment.Z.AsSpan(0, p).CopyTo(_pz.AsSpan(GarmentStart));
        _hair.X.AsSpan().CopyTo(_px.AsSpan(HairStart)); _hair.Y.AsSpan().CopyTo(_py.AsSpan(HairStart)); _hair.Z.AsSpan().CopyTo(_pz.AsSpan(HairStart));
    }

    /// <summary>외부 소스 대신 앵커 클라우드 위치를 직접 덮어쓴다(검증 · 외부 리그 결선).</summary>
    public void SetAnchor(int a, Vector3 p) { _px[a] = p.X; _py[a] = p.Y; _pz[a] = p.Z; }

    private void Frame(int a, out Vector3 o, out Quaternion q, out bool ok)
    {
        int b = _nb[a], c = _nc[a];
        o = new Vector3(_px[a], _py[a], _pz[a]);
        var e1 = new Vector3(_px[b], _py[b], _pz[b]) - o;
        var e2 = new Vector3(_px[c], _py[c], _pz[c]) - o;
        var l1 = e1.Length();
        var zc = Vector3.Cross(e1, e2);
        var lz = zc.Length();
        if (l1 < 1e-7f || lz < 1e-10f || !float.IsFinite(lz)) { q = Quaternion.Identity; ok = false; return; }
        var x = e1 / l1; var z = zc / lz; var y = Vector3.Cross(z, x);
        var m = new Matrix4x4(x.X, x.Y, x.Z, 0f, y.X, y.Y, y.Z, 0f, z.X, z.Y, z.Z, 0f, 0f, 0f, 0f, 1f);
        q = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
        ok = true;
    }

    /// <summary>소스 수집 → 앵커 DQ → 가우시안 DQS(무할당).</summary>
    public void Update() => Update(true);

    public void Update(bool gather)
    {
        var f = System.Diagnostics.Stopwatch.Frequency / 1e6;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (gather) Gather();
        var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        var deg = 0;
        for (var a = 0; a < A; a++)
        {
            Frame(a, out var o, out var qc, out var ok);
            var k = a * 8;
            if (!ok) { deg++; continue; }                   // 퇴화 프레임 → 직전 DQ 유지
            var r = Quaternion.Concatenate(_restQInv[a], qc);
            var prev = new Quaternion(_dq[k], _dq[k + 1], _dq[k + 2], _dq[k + 3]);
            if (Quaternion.Dot(prev, r) < 0f) r = -r;        // 시간적 반구 연속성
            var t = o - Vector3.Transform(_restO[a], r);
            _dq[k] = r.X; _dq[k + 1] = r.Y; _dq[k + 2] = r.Z; _dq[k + 3] = r.W;
            _dq[k + 4] = 0.5f * (t.X * r.W + t.Y * r.Z - t.Z * r.Y);
            _dq[k + 5] = 0.5f * (-t.X * r.Z + t.Y * r.W + t.Z * r.X);
            _dq[k + 6] = 0.5f * (t.X * r.Y - t.Y * r.X + t.Z * r.W);
            _dq[k + 7] = -0.5f * (t.X * r.X + t.Y * r.Y + t.Z * r.Z);
        }
        DegenerateAnchors = deg;
        var t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        Blend();
        var t3 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (Path == GaussianSimdPath.V256 && BitConverter.IsLittleEndian) ApplyV256(); else ApplyScalar(0);
        var t4 = System.Diagnostics.Stopwatch.GetTimestamp();
        var maxD = 0f;
        for (var i = 0; i < N; i += 97)
        {
            float dx = X[i] - RestX[i], dy = Y[i] - RestY[i], dz = Z[i] - RestZ[i];
            var d = dx * dx + dy * dy + dz * dz;
            if (d > maxD) maxD = d;
        }
        MaxDisplacementMm = MathF.Sqrt(maxD) * 1000f;
        LastGatherUs = (t1 - t0) / f; LastAnchorUs = (t2 - t1) / f; LastBlendUs = (t3 - t2) / f; LastApplyUs = (t4 - t3) / f; LastUpdateUs = (t4 - t0) / f;
        Updates++;
    }

    /// <summary>단계 1 — 앵커 DQ gather · 반구 정렬 가중합(스칼라).</summary>
    private void Blend()
    {
        var dq = _dq; var bone = Bone; var wt = Weight;
        for (var i = 0; i < N; i++)
        {
            var k4 = i * Influences;
            var j0 = bone[k4] * 8;
            float p0 = dq[j0], p1 = dq[j0 + 1], p2 = dq[j0 + 2], p3 = dq[j0 + 3];
            float r0 = 0f, r1 = 0f, r2 = 0f, r3 = 0f, d0 = 0f, d1 = 0f, d2 = 0f, d3 = 0f;
            for (var k = 0; k < Influences; k++)
            {
                var j = bone[k4 + k] * 8;
                var w = wt[k4 + k];
                if (p0 * dq[j] + p1 * dq[j + 1] + p2 * dq[j + 2] + p3 * dq[j + 3] < 0f) w = -w;
                r0 += w * dq[j]; r1 += w * dq[j + 1]; r2 += w * dq[j + 2]; r3 += w * dq[j + 3];
                d0 += w * dq[j + 4]; d1 += w * dq[j + 5]; d2 += w * dq[j + 6]; d3 += w * dq[j + 7];
            }
            _r0[i] = r0; _r1[i] = r1; _r2[i] = r2; _r3[i] = r3; _d0[i] = d0; _d1[i] = d1; _d2[i] = d2; _d3[i] = d3;
        }
    }

    /// <summary>단계 2 스칼라 — 정규화 · t = 2·d·r* · p' = r·p·r* + t · q' = r·q₀.</summary>
    private void ApplyScalar(int from)
    {
        for (var i = from; i < N; i++)
        {
            float rx = _r0[i], ry = _r1[i], rz = _r2[i], rw = _r3[i];
            var inv = 1f / MathF.Sqrt(rx * rx + ry * ry + rz * rz + rw * rw);
            rx *= inv; ry *= inv; rz *= inv; rw *= inv;
            float dx = _d0[i] * inv, dy = _d1[i] * inv, dz = _d2[i] * inv, dw = _d3[i] * inv;
            var tx = 2f * (dx * rw - dw * rx + dz * ry - dy * rz);
            var ty = 2f * (dy * rw - dw * ry + dx * rz - dz * rx);
            var tz = 2f * (dz * rw - dw * rz + dy * rx - dx * ry);
            float vx = RestX[i], vy = RestY[i], vz = RestZ[i];
            var cx = ry * vz - rz * vy + rw * vx;
            var cy = rz * vx - rx * vz + rw * vy;
            var cz = rx * vy - ry * vx + rw * vz;
            X[i] = vx + 2f * (ry * cz - rz * cy) + tx;
            Y[i] = vy + 2f * (rz * cx - rx * cz) + ty;
            Z[i] = vz + 2f * (rx * cy - ry * cx) + tz;
            float qx = RestQx[i], qy = RestQy[i], qz = RestQz[i], qw = RestQw[i];
            Qx[i] = rw * qx + rx * qw + ry * qz - rz * qy;
            Qy[i] = rw * qy - rx * qz + ry * qw + rz * qx;
            Qz[i] = rw * qz + rx * qy - ry * qx + rz * qw;
            Qw[i] = rw * qw - rx * qx - ry * qy - rz * qz;
            Sx[i] = rx; Sy[i] = ry; Sz[i] = rz; Sw[i] = rw;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> L(float[] a, int i) => Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(a), (nuint)i);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void S(float[] a, int i, Vector256<float> v) => v.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(a), (nuint)i);

    /// <summary>단계 2 Vector256 — <see cref="ApplyScalar"/> 와 연산 순서 동일(IEEE 단일 · 비트 동일).</summary>
    private void ApplyV256()
    {
        var one = Vector256.Create(1f); var two = Vector256.Create(2f);
        var i = 0;
        for (; i + 8 <= N; i += 8)
        {
            Vector256<float> rx = L(_r0, i), ry = L(_r1, i), rz = L(_r2, i), rw = L(_r3, i);
            var inv = one / Vector256.Sqrt(rx * rx + ry * ry + rz * rz + rw * rw);
            rx *= inv; ry *= inv; rz *= inv; rw *= inv;
            Vector256<float> dx = L(_d0, i) * inv, dy = L(_d1, i) * inv, dz = L(_d2, i) * inv, dw = L(_d3, i) * inv;
            var tx = two * (dx * rw - dw * rx + dz * ry - dy * rz);
            var ty = two * (dy * rw - dw * ry + dx * rz - dz * rx);
            var tz = two * (dz * rw - dw * rz + dy * rx - dx * ry);
            Vector256<float> vx = L(RestX, i), vy = L(RestY, i), vz = L(RestZ, i);
            var cx = ry * vz - rz * vy + rw * vx;
            var cy = rz * vx - rx * vz + rw * vy;
            var cz = rx * vy - ry * vx + rw * vz;
            S(X, i, vx + two * (ry * cz - rz * cy) + tx);
            S(Y, i, vy + two * (rz * cx - rx * cz) + ty);
            S(Z, i, vz + two * (rx * cy - ry * cx) + tz);
            Vector256<float> qx = L(RestQx, i), qy = L(RestQy, i), qz = L(RestQz, i), qw = L(RestQw, i);
            S(Qx, i, rw * qx + rx * qw + ry * qz - rz * qy);
            S(Qy, i, rw * qy - rx * qz + ry * qw + rz * qx);
            S(Qz, i, rw * qz + rx * qy - ry * qx + rz * qw);
            S(Qw, i, rw * qw - rx * qx - ry * qy - rz * qz);
            S(Sx, i, rx); S(Sy, i, ry); S(Sz, i, rz); S(Sw, i, rw);
        }
        if (i < N) ApplyScalar(i);
    }
}
