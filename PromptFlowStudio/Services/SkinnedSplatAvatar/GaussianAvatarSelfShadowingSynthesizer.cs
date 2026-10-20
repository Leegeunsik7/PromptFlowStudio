using System.Numerics;
using PromptFlowStudio.Services.NeuralRagdoll;
using PromptFlowStudio.Services.SplatEnvironment;
namespace PromptFlowStudio.Services.SkinnedSplatAvatar;

/// <summary>
/// [PromptFlow Studio v17.57.0] 🌗 아바타 셀프 섀도우 &amp; 내부 상호 차폐 합성기(<see cref="Compute"/> 무할당 · 워커 병렬 · <see cref="ISplatRadianceModulator"/>).
/// <b>계층 캡슐 프록시</b>: 분절 23 캡슐(현재 관절 · <see cref="RagdollSkeleton.BoneRadius"/>)을 몸통/머리 · 좌우 팔 · 좌우 다리 5 그룹 경계 구로 묶어
/// 스플랫 → 광원 광선을 그룹 구 → 캡슐 순으로 시험(자기 · 부모 · 자식 분절 제외). 광선–축 최근접 거리로 반음영 폭(0.3 r + 0.08·t)의 부드러운 차폐 계산.
/// <b>광원 공간 뎁스 맵</b>(128² 직교 · 스킨드 스플랫 중심 최소 깊이): 캡슐 계층이 후보로 판정한 스플랫만 3×3 PCF(바이어스 max(3.5 cm, 3 텍셀))로 정밀화 —
/// 열린 표면의 뎁스 아크네를 원천 차단. 최종 차폐 = max(캡슐, 뎁스) → 3 프레임 바이트 링버퍼 평균(시간 안정화).
/// 변조: 색 × (0.35 + 0.65·max(0, n·L)) × (1 − 농도·차폐) (n = DQS 블렌드 회전 법선).
/// </summary>
public sealed class GaussianAvatarSelfShadowingSynthesizer : ISplatRadianceModulator, IDisposable
{
    public const int Res = 128, History = 3, Segs = GaussianAvatarRig.Bones, Groups = 5, Chunk = 2048;
    private static readonly byte[] GroupOf = BuildGroups();
    private readonly float[] _depth = new float[Res * Res];
    private readonly Vector3[] _ca = new Vector3[Segs], _cb = new Vector3[Segs];
    private readonly Vector4[] _gs = new Vector4[Groups];
    private readonly byte[] _hist;
    private readonly float[] _occ;
    private readonly Vector3[] _restN;
    private readonly long[] _wStat;
    private readonly SplatWorkerPool _pool;
    private RealtimeDualQuaternionGaussianSkinningEngine? _eng;
    private GaussianSplatScene? _scene;
    private Vector3 _L, _R, _U, _center;
    private float _half, _texel;
    private int _slot, _start, _n, _frames;

    public GaussianAvatarSelfShadowingSynthesizer(int capacity, Vector3[] restNormals, int workers = -1)
    {
        _hist = new byte[History * capacity]; _occ = new float[capacity]; _restN = restNormals;
        var wn = workers < 0 ? Math.Clamp(Environment.ProcessorCount - 1, 0, 7) : Math.Min(workers, 15);
        _pool = new SplatWorkerPool(wn, RunChunk, "PF.DqShadow.W");
        _wStat = new long[(wn + 1) * 4];
    }

    private static byte[] BuildGroups()
    {
        var g = new byte[Segs];
        for (var b = 1; b < Segs; b++)
            g[b] = b switch { >= 7 and <= 10 => 1, >= 12 and <= 15 => 2, >= 17 and <= 19 => 3, >= 21 and <= 23 => 4, _ => 0 };
        return g;
    }

    public bool Enabled { get; set; } = true;
    public bool UseDepthRefine { get; set; } = true;
    public float Opacity { get; set; } = 0.75f;
    public float SoftAngle { get; set; } = 0.08f;
    public bool Shading { get; set; } = true;
    public Vector3 LightDir { get; set; } = Vector3.Normalize(new Vector3(-0.35f, 0.85f, 0.4f));
    public float Occlusion(int k) => _occ[k];
    public ReadOnlySpan<float> DepthMap => _depth;
    public long GroupTests { get; private set; }
    public long CapsuleTests { get; private set; }
    public long Candidates { get; private set; }
    public long Shadowed { get; private set; }
    public float MeanOcclusion { get; private set; }
    public double LastComputeUs { get; private set; }

    /// <summary>프레임 차폐 계산(무할당) — joints = 현재 관절 24(월드).</summary>
    public void Compute(RealtimeDualQuaternionGaussianSkinningEngine eng, GaussianSplatScene scene, ReadOnlySpan<Vector3> joints)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _eng = eng; _scene = scene; _start = eng.Start; _n = eng.Count;
        _L = Vector3.Normalize(LightDir);
        _R = Vector3.Normalize(Vector3.Cross(MathF.Abs(_L.Y) < 0.95f ? Vector3.UnitY : Vector3.UnitX, _L)); _U = Vector3.Cross(_L, _R);
        // 캡슐 · 그룹 경계 구
        for (var g = 0; g < Groups; g++) _gs[g] = new Vector4(0f, 0f, 0f, -1f);
        Span<Vector3> gsum = stackalloc Vector3[Groups]; Span<int> gcnt = stackalloc int[Groups];
        for (var b = 1; b < Segs; b++) { _ca[b] = joints[RagdollSkeleton.Parent[b]]; _cb[b] = joints[b]; gsum[GroupOf[b]] += _ca[b] + _cb[b]; gcnt[GroupOf[b]] += 2; }
        _center = Vector3.Zero;
        for (var g = 0; g < Groups; g++) { var c = gsum[g] / Math.Max(gcnt[g], 1); _gs[g] = new Vector4(c, 0f); _center += c; }
        _center /= Groups;
        var extent = 0f;
        for (var b = 1; b < Segs; b++)
        {
            var g = GroupOf[b]; var c = new Vector3(_gs[g].X, _gs[g].Y, _gs[g].Z); var r = RagdollSkeleton.BoneRadius[b];
            var e = MathF.Max(Vector3.Distance(c, _ca[b]), Vector3.Distance(c, _cb[b])) + r;
            if (e > _gs[g].W) _gs[g].W = e;
            extent = MathF.Max(extent, MathF.Max(Vector3.Distance(_center, _ca[b]), Vector3.Distance(_center, _cb[b])) + r);
        }
        // 광원 공간 뎁스 맵(스킨드 스플랫 중심 · 최소 깊이 = 광원에 가장 가까움)
        _half = extent * 1.05f; _texel = 2f * _half / Res;
        Array.Fill(_depth, float.PositiveInfinity);
        float[] X = scene.X, Y = scene.Y, Z = scene.Z;
        var inv = 1f / _texel;
        for (var k = 0; k < _n; k++)
        {
            var i = _start + k;
            var d = new Vector3(X[i] - _center.X, Y[i] - _center.Y, Z[i] - _center.Z);
            var u = (int)((Vector3.Dot(d, _R) + _half) * inv); var v = (int)((Vector3.Dot(d, _U) + _half) * inv);
            if ((uint)u >= Res || (uint)v >= Res) continue;
            var z = -Vector3.Dot(d, _L);
            ref var t = ref _depth[v * Res + u];
            if (z < t) t = z;
        }
        Array.Clear(_wStat);
        _slot = _frames % History;
        if (Enabled) _pool.Run(0, (_n + Chunk - 1) / Chunk, 1);
        else Array.Clear(_occ, 0, _n);
        _frames++;
        long gt = 0, ct = 0, cand = 0, sh = 0;
        for (var w = 0; w <= _pool.Workers; w++) { var o = w * 4; gt += _wStat[o]; ct += _wStat[o + 1]; cand += _wStat[o + 2]; sh += _wStat[o + 3]; }
        GroupTests = gt; CapsuleTests = ct; Candidates = cand; Shadowed = sh;
        var sum = 0f; for (var k = 0; k < _n; k++) sum += _occ[k];
        MeanOcclusion = _n > 0 ? sum / _n : 0f;
        LastComputeUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private void RunChunk(int job, int w, int cs, int ce)
    {
        long gt = 0, ct = 0, cand = 0, sh = 0;
        var sc = _scene!; var eng = _eng!;
        var L = _L;
        var hist = Math.Min(_frames + 1, History);
        for (var c = cs; c < ce; c++)
        {
            int s = c * Chunk, e = Math.Min(s + Chunk, _n);
            for (var k = s; k < e; k++)
            {
                var i = _start + k;
                var p = new Vector3(sc.X[i], sc.Y[i], sc.Z[i]);
                var own = eng.Weights(k).B0;
                var par = RagdollSkeleton.Parent[own];
                var o = p + L * 0.012f;
                var occ = 0f;
                for (var g = 0; g < Groups; g++)
                {
                    gt++;
                    var gc = new Vector3(_gs[g].X, _gs[g].Y, _gs[g].Z); var gr = _gs[g].W;
                    // 광선(o + L t, t ≥ 0) – 구 교차(반음영 여유 포함)
                    var oc = o - gc; var bb = Vector3.Dot(oc, L); var cc = oc.LengthSquared() - (gr + 0.15f) * (gr + 0.15f);
                    if (cc > 0f && bb > 0f) continue;
                    if (bb * bb - cc < 0f) continue;
                    for (var b = 1; b < Segs; b++)
                    {
                        if (GroupOf[b] != g || b == own || b == par || RagdollSkeleton.Parent[b] == own) continue;
                        ct++;
                        var v = CapsuleOcclusion(o, L, _ca[b], _cb[b], RagdollSkeleton.BoneRadius[b]);
                        if (v > occ) occ = v;
                    }
                }
                if (occ > 0f)
                {
                    cand++;
                    if (UseDepthRefine)
                    {
                        var d = p - _center;
                        var inv = 1f / _texel;
                        var u = (int)((Vector3.Dot(d, _R) + _half) * inv); var v = (int)((Vector3.Dot(d, _U) + _half) * inv);
                        var z = -Vector3.Dot(d, L) - MathF.Max(0.035f, 3f * _texel);
                        int hit = 0, tot = 0;
                        for (var dv = -1; dv <= 1; dv++)
                            for (var du = -1; du <= 1; du++)
                            {
                                int uu = u + du, vv = v + dv;
                                if ((uint)uu >= Res || (uint)vv >= Res) continue;
                                tot++; if (_depth[vv * Res + uu] < z) hit++;
                            }
                        if (tot > 0) occ = MathF.Max(occ, (float)hit / tot);
                    }
                }
                _hist[_slot * _occ.Length + k] = (byte)(Math.Clamp(occ, 0f, 1f) * 255f + 0.5f);
                var sum = 0;
                for (var h = 0; h < hist; h++) sum += _hist[h * _occ.Length + k];
                var avg = sum / (255f * hist);
                _occ[k] = avg;
                if (avg > 0.2f) sh++;
            }
        }
        var so = w * 4;
        _wStat[so] += gt; _wStat[so + 1] += ct; _wStat[so + 2] += cand; _wStat[so + 3] += sh;
    }

    /// <summary>광선–캡슐 부드러운 차폐(0..1) — 최근접 거리 d · 광선 거리 t · 반음영 폭 w = 0.3r + SoftAngle·t.</summary>
    private float CapsuleOcclusion(Vector3 o, Vector3 L, Vector3 a, Vector3 b, float r)
    {
        var ab = b - a; var ao = o - a;
        float abab = Vector3.Dot(ab, ab), abL = Vector3.Dot(ab, L), aoab = Vector3.Dot(ao, ab), aoL = Vector3.Dot(ao, L);
        var den = abab - abL * abL;
        float s, t;
        if (den > 1e-8f) { s = Math.Clamp((aoab - abL * aoL) / den, 0f, 1f); }
        else s = 0f;
        t = MathF.Max(0f, Vector3.Dot(a + ab * s - o, L));
        if (abab > 1e-10f) s = Math.Clamp(Vector3.Dot(o + L * t - a, ab) / abab, 0f, 1f);
        var q = o + L * t; var c = a + ab * s;
        if (Vector3.Dot(c - o, L) <= 0f) return 0f;
        var dist = Vector3.Distance(q, c);
        var w = 0.3f * r + SoftAngle * t;
        return Math.Clamp((r + w - dist) / (2f * w), 0f, 1f);
    }

    // ══════════════════════ ISplatRadianceModulator ══════════════════════
    public void BeginFrame(GaussianSplatScene scene, in SplatCamera cam, int workers) { }

    public Vector3 Modulate(int worker, int splat, Vector3 position, Vector3 color)
    {
        var k = splat - _start;
        if ((uint)k >= (uint)_n || _eng is null) return color;
        var f = 1f;
        if (Shading) { var nrm = _eng.RotateBlended(k, _restN[k]); f = 0.35f + 0.65f * MathF.Max(0f, Vector3.Dot(nrm, _L)); }
        return color * (f * (1f - Opacity * _occ[k]));
    }

    public void EndFrame() { }

    public void Dispose() => _pool.Dispose();
}
