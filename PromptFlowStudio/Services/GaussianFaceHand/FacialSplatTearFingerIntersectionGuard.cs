using System.Numerics;
using PromptFlowStudio.Services.DigitalHuman;
using PromptFlowStudio.Services.SplatEnvironment;
namespace PromptFlowStudio.Services.GaussianFaceHand;

/// <summary>
/// [PromptFlow Studio v17.58.0] 🛡 구강/안구 스플랫 찢어짐 &amp; 손가락 관통 방어 가드(<see cref="ApplyFace"/> · <see cref="ApplyHands"/> 무할당).
/// ① 회전 공분산 폭주 · 유한성 복구(누적 ω 가 1.2 rad 초과 → 축 유지 클램프 · 비유한 μ/ω → 휴지).
/// ② 입술 이음매 쌍(입술 ↔ 인접 피부 · 입꼬리 윗/아랫입술, 휴지 5 mm 이내 최근접) 간격이 1.5 cm 를 넘으면 구멍 —
///    두 입자를 서로 끌어당기는 인력 보간(가우스-자이델 6 회 · 목표 0.98 × 임계) 으로 메운다.
/// ③ 내부 SDF 바운딩: 안구 = 구(중심 · 반경 12 mm) 밖 → 구면 투영, 치아/혀/구강 = 앵커 입술 표면 국소 평면 SDF
///    (휴지 깊이 d₀ 의 45 % 미만 · 혀는 TongueOut × 45 mm 허용) → 법선 반대로 밀어 넣기.
/// ④ 손가락: 인접 손가락 본 쌍 캡슐 근접 시 상대 캡슐 내부(부호 거리 &lt; −0.6 mm) 가우시안을 표면 법선으로 밀어냄(Repulsion),
///    접촉 평면(핀치) 을 넘은 가우시안은 평면 투영. 수정 후 재검사로 잔여 결함(ON 이면 0) 을 계측한다.
/// </summary>
public sealed class FacialSplatTearFingerIntersectionGuard
{
    public const float TearThreshold = 0.015f, PairRadius = 0.006f, Tol = 0.0006f, MinDepthFrac = 0.45f, TongueAllow = 0.045f, EyeTol = 0.0004f, MaxOmega = 1.2f;
    private readonly int[] _pa, _pb;
    private readonly int[] _anc;
    private readonly float[] _minDepth;
    private readonly int _intLo, _eyeLo, _eyeHi;
    private readonly byte[] _hA, _hB;

    public FacialSplatTearFingerIntersectionGuard(RealtimeGaussianFacialBlendshapeEngine face)
    {
        // ── 입술 이음매 쌍 ──
        var cand = new List<int>();
        for (var i = 0; i < face.Count; i++)
        {
            var r = face.Region(i);
            if (r is FaceRegion.LipUpper or FaceRegion.LipLower) { cand.Add(i); continue; }
            if (r is FaceRegion.Cheek or FaceRegion.Chin or FaceRegion.Nose)
            {
                RealtimeGaussianFacialBlendshapeEngine.UV(face.RestLocal(i), out var u, out var v);
                if (MathF.Abs(v - RealtimeGaussianFacialBlendshapeEngine.VMouth) < 0.16f && MathF.Abs(u) < 0.45f) cand.Add(i);
            }
        }
        var pa = new List<int>(); var pb = new List<int>();
        foreach (var i in cand)
        {
            var ri = face.Region(i);
            if (ri is not (FaceRegion.LipUpper or FaceRegion.LipLower)) continue;
            RealtimeGaussianFacialBlendshapeEngine.UV(face.RestLocal(i), out var ui, out _);
            var corner = MathF.Abs(ui) >= 0.25f;
            int bs = -1, bo = -1; float ds = PairRadius * PairRadius, dO = PairRadius * PairRadius;
            var xi = face.RestLocal(i);
            foreach (var j in cand)
            {
                if (j == i) continue;
                var rj = face.Region(j);
                var d2 = Vector3.DistanceSquared(xi, face.RestLocal(j));
                if (rj is FaceRegion.LipUpper or FaceRegion.LipLower) { if (corner && rj != ri && d2 < dO) { dO = d2; bo = j; } }
                else if (d2 < ds) { ds = d2; bs = j; }
            }
            if (bs >= 0) { pa.Add(i); pb.Add(bs); }
            if (bo >= 0 && i < bo) { pa.Add(i); pb.Add(bo); }
        }
        _pa = pa.ToArray(); _pb = pb.ToArray();
        // ── 내부 앵커(치아 · 혀 · 구강 → 입술 표면) ──
        _eyeLo = face.RegionStart(FaceRegion.EyeBall); _eyeHi = face.RegionEnd(FaceRegion.EyeBall);
        _intLo = face.RegionStart(FaceRegion.TeethUpper);
        var intHi = face.RegionEnd(FaceRegion.Cavity);
        _anc = new int[intHi - _intLo]; _minDepth = new float[intHi - _intLo];
        for (var i = _intLo; i < intHi; i++)
        {
            var x = face.RestLocal(i);
            RealtimeGaussianFacialBlendshapeEngine.UV(x, out _, out var v);
            var upper = face.Region(i) == FaceRegion.TeethUpper || face.Region(i) == FaceRegion.Cavity && v >= RealtimeGaussianFacialBlendshapeEngine.VMouth;
            var lo = face.RegionStart(upper ? FaceRegion.LipUpper : FaceRegion.LipLower); var hi = face.RegionEnd(upper ? FaceRegion.LipUpper : FaceRegion.LipLower);
            int best = lo; var bd = float.MaxValue;
            for (var j = lo; j < hi; j++)
            {
                var n = face.RestNormal(j); var dx = x - face.RestLocal(j);
                var lat = dx - n * Vector3.Dot(dx, n);
                if (lat.LengthSquared() < bd) { bd = lat.LengthSquared(); best = j; }
            }
            _anc[i - _intLo] = best;
            _minDepth[i - _intLo] = MinDepthFrac * Vector3.Dot(face.RestLocal(best) - x, face.RestNormal(best));
        }
        // ── 손가락 관통 감시 쌍(인접 손가락 × 마디 4 조합 + 엄지-검지 기절골) ──
        var ha = new List<byte>(); var hb = new List<byte>();
        for (var h = 0; h < DexterousHandGaussianSkinningSynthesizer.Hands; h++)
        {
            for (var f = 1; f < 4; f++)
                for (var s = 0; s < 2; s++)
                    for (var t = 0; t < 2; t++) { ha.Add((byte)DexterousHandGaussianSkinningSynthesizer.Bone(h, f, s)); hb.Add((byte)DexterousHandGaussianSkinningSynthesizer.Bone(h, f + 1, t)); }
            ha.Add((byte)DexterousHandGaussianSkinningSynthesizer.Bone(h, 0, 0)); hb.Add((byte)DexterousHandGaussianSkinningSynthesizer.Bone(h, 1, 0));
        }
        _hA = ha.ToArray(); _hB = hb.ToArray();
    }

    public bool FaceEnabled { get; set; } = true;
    public bool FingerEnabled { get; set; } = true;
    public int SeamPairs => _pa.Length;
    public int FingerPairs => _hA.Length;
    public int SeamA(int p) => _pa[p];
    public int SeamB(int p) => _pb[p];
    public long Frames { get; private set; }
    public long TearHoles { get; private set; }
    public int FrameTearHoles { get; private set; }
    public long TearResidual { get; private set; }
    public float FrameMaxGapMm { get; private set; }
    public float PeakGapMm { get; private set; }
    public long Protrusions { get; private set; }
    public int FrameProtrusions { get; private set; }
    public long ProtrusionResidual { get; private set; }
    public long CovFixes { get; private set; }
    public long FingerPenetrations { get; private set; }
    public int FrameFingerPenetrations { get; private set; }
    public long FingerResidual { get; private set; }
    public float FrameFingerDepthMm { get; private set; }
    public int PairsTested { get; private set; }
    public double LastFaceUs { get; private set; }
    public double LastHandUs { get; private set; }
    /// <summary>찢어짐 미터(0..1+ · 최대 이음매 간격 / 1.5 cm · 수정 전).</summary>
    public float TearMeter => FrameMaxGapMm / (TearThreshold * 1000f);
    public long Defects => TearResidual + ProtrusionResidual + FingerResidual;

    public void ResetMetrics() { Frames = TearHoles = TearResidual = Protrusions = ProtrusionResidual = CovFixes = FingerPenetrations = FingerResidual = 0; PeakGapMm = 0f; }

    // ══════════════════════ 안면(로컬 누적 SoA · 무할당) ══════════════════════
    public void ApplyFace(RealtimeGaussianFacialBlendshapeEngine face)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        float[] X = face.X, Y = face.Y, Z = face.Z;
        float[] Wx = face.Omega(0), Wy = face.Omega(1), Wz = face.Omega(2);
        // ① 회전 공분산 · 유한성
        long cf = 0;
        for (var i = 0; i < face.Count; i++)
        {
            var th = Wx[i] * Wx[i] + Wy[i] * Wy[i] + Wz[i] * Wz[i];
            var finite = float.IsFinite(X[i] + Y[i] + Z[i]) && float.IsFinite(th);
            if (finite && th <= MaxOmega * MaxOmega) continue;
            cf++;
            if (!FaceEnabled) continue;
            if (!finite) { var r = face.RestLocal(i); X[i] = r.X; Y[i] = r.Y; Z[i] = r.Z; Wx[i] = Wy[i] = Wz[i] = 0f; continue; }
            var s = MaxOmega / MathF.Sqrt(th);
            Wx[i] *= s; Wy[i] *= s; Wz[i] *= s;
        }
        CovFixes += cf;
        // ② 입술 이음매 구멍
        int holes = 0; var maxGap = 0f; const float T2 = TearThreshold * TearThreshold;
        for (var p = 0; p < _pa.Length; p++)
        {
            int i = _pa[p], j = _pb[p];
            float dx = X[j] - X[i], dy = Y[j] - Y[i], dz = Z[j] - Z[i];
            var d2 = dx * dx + dy * dy + dz * dz;
            if (d2 > maxGap) maxGap = d2;
            if (d2 > T2) holes++;
        }
        FrameMaxGapMm = MathF.Sqrt(maxGap) * 1000f;
        if (FrameMaxGapMm > PeakGapMm) PeakGapMm = FrameMaxGapMm;
        FrameTearHoles = holes; TearHoles += holes;
        if (holes > 0 && FaceEnabled)
        {
            const float target = TearThreshold * 0.98f;
            for (var it = 0; it < 6; it++)
            {
                var moved = false;
                for (var p = 0; p < _pa.Length; p++)
                {
                    int i = _pa[p], j = _pb[p];
                    float dx = X[j] - X[i], dy = Y[j] - Y[i], dz = Z[j] - Z[i];
                    var d = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (d <= target) continue;
                    var k = 0.5f * (d - target) / d;
                    X[i] += dx * k; Y[i] += dy * k; Z[i] += dz * k; X[j] -= dx * k; Y[j] -= dy * k; Z[j] -= dz * k;
                    moved = true;
                }
                if (!moved) break;
            }
        }
        long res = 0;
        for (var p = 0; p < _pa.Length; p++)
        {
            int i = _pa[p], j = _pb[p];
            float dx = X[j] - X[i], dy = Y[j] - Y[i], dz = Z[j] - Z[i];
            if (dx * dx + dy * dy + dz * dz > T2 * 1.0001f) res++;
        }
        TearResidual += res;
        // ③ 내부 SDF
        int prot = 0; long pres = 0;
        for (var i = _eyeLo; i < _eyeHi; i++)
        {
            var c = face.EyeCenter(face.RestLocal(i).X < 0f ? 0 : 1);
            float dx = X[i] - c.X, dy = Y[i] - c.Y, dz = Z[i] - c.Z;
            var d = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            if (d <= RealtimeGaussianFacialBlendshapeEngine.EyeRadius + EyeTol) continue;
            prot++;
            if (!FaceEnabled) { pres++; continue; }
            var s = RealtimeGaussianFacialBlendshapeEngine.EyeRadius / d;
            X[i] = c.X + dx * s; Y[i] = c.Y + dy * s; Z[i] = c.Z + dz * s;
        }
        var tongueLo = face.RegionStart(FaceRegion.Tongue); var tongueHi = face.RegionEnd(FaceRegion.Tongue);
        var allow = MathF.Max(face.Weight(ArkitBlendshape.TongueOut), 0f) * TongueAllow;
        for (var k = 0; k < _anc.Length; k++)
        {
            var i = _intLo + k; var a = _anc[k];
            var n = face.RestNormal(a);
            var min = _minDepth[k] - (i >= tongueLo && i < tongueHi ? allow : 0f);
            var depth = (X[a] - X[i]) * n.X + (Y[a] - Y[i]) * n.Y + (Z[a] - Z[i]) * n.Z;
            if (depth >= min - 1e-5f) continue;
            prot++;
            if (!FaceEnabled) { pres++; continue; }
            var push = min - depth;
            X[i] -= n.X * push; Y[i] -= n.Y * push; Z[i] -= n.Z * push;
        }
        FrameProtrusions = prot; Protrusions += prot; ProtrusionResidual += pres;
        Frames++;
        LastFaceUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    // ══════════════════════ 손가락(장면 월드 SoA · 무할당) ══════════════════════
    public void ApplyHands(DexterousHandGaussianSkinningSynthesizer hands, GaussianSplatScene sc)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int pen = 0, tested = 0; var deep = 0f;
        for (var pass = 0; pass < 3; pass++)
        {
            var found = 0;
            for (var p = 0; p < _hA.Length; p++)
            {
                int a = _hA[p], b = _hB[p];
                DexterousHandGaussianSkinningSynthesizer.ClosestSegSeg(hands.CapsuleA(a), hands.CapsuleB(a), hands.CapsuleA(b), hands.CapsuleB(b), out var c1, out var c2);
                if (Vector3.Distance(c1, c2) > hands.Radius(a) + hands.Radius(b) + 0.002f) continue;
                if (pass == 0) tested++;
                found += Repel(hands, sc, a, b, pass == 0, ref deep) + Repel(hands, sc, b, a, pass == 0, ref deep);
            }
            for (var c = 0; c < DexterousHandGaussianSkinningSynthesizer.MaxContacts; c++)
            {
                if (!hands.ContactOn(c)) continue;
                found += Plane(hands, sc, hands.ContactBoneA(c), hands.ContactPoint(c), hands.ContactNormal(c)) + Plane(hands, sc, hands.ContactBoneB(c), hands.ContactPoint(c), -hands.ContactNormal(c));
            }
            if (pass == 0) pen = found;
            if (!FingerEnabled || found == 0) { if (pass > 0 || !FingerEnabled) FingerResidual += found; break; }
            if (pass == 2) { FingerResidual += Count(hands, sc); break; }
        }
        PairsTested = tested; FrameFingerPenetrations = pen; FingerPenetrations += pen; FrameFingerDepthMm = deep * 1000f;
        LastHandUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private int Repel(DexterousHandGaussianSkinningSynthesizer hands, GaussianSplatScene sc, int own, int other, bool measure, ref float deep)
    {
        Vector3 qa = hands.CapsuleA(other), qb = hands.CapsuleB(other); var r = hands.Radius(other);
        var o = hands.Start; var cnt = 0;
        for (var k = hands.BoneStart(own); k < hands.BoneEnd(own); k++)
        {
            var i = o + k;
            var x = new Vector3(sc.X[i], sc.Y[i], sc.Z[i]);
            var q = DexterousHandGaussianSkinningSynthesizer.ClosestOnSeg(qa, qb, x);
            var dv = x - q; var d = dv.Length();
            var sd = d - r;
            if (sd >= -Tol) continue;
            cnt++;
            if (measure && -sd > deep) deep = -sd;
            if (!FingerEnabled) continue;
            var n = d > 1e-7f ? dv / d : Vector3.Normalize(Vector3.Cross(qb - qa, Vector3.UnitY) + new Vector3(1e-4f, 0f, 0f));
            x = q + n * (r + 0.0002f);
            sc.X[i] = x.X; sc.Y[i] = x.Y; sc.Z[i] = x.Z;
        }
        return cnt;
    }

    private int Plane(DexterousHandGaussianSkinningSynthesizer hands, GaussianSplatScene sc, int bone, Vector3 pc, Vector3 n)
    {
        var o = hands.Start; var cnt = 0;
        for (var k = hands.BoneStart(bone); k < hands.BoneEnd(bone); k++)
        {
            var i = o + k;
            var s = (sc.X[i] - pc.X) * n.X + (sc.Y[i] - pc.Y) * n.Y + (sc.Z[i] - pc.Z) * n.Z;
            if (s <= Tol) continue;
            cnt++;
            if (!FingerEnabled) continue;
            sc.X[i] -= n.X * s; sc.Y[i] -= n.Y * s; sc.Z[i] -= n.Z * s;
        }
        return cnt;
    }

    private int Count(DexterousHandGaussianSkinningSynthesizer hands, GaussianSplatScene sc)
    {
        var en = FingerEnabled; FingerEnabled = false;
        var dummy = 0f; var n = 0;
        for (var p = 0; p < _hA.Length; p++) n += Repel(hands, sc, _hA[p], _hB[p], false, ref dummy) + Repel(hands, sc, _hB[p], _hA[p], false, ref dummy);
        for (var c = 0; c < DexterousHandGaussianSkinningSynthesizer.MaxContacts; c++)
            if (hands.ContactOn(c)) n += Plane(hands, sc, hands.ContactBoneA(c), hands.ContactPoint(c), hands.ContactNormal(c)) + Plane(hands, sc, hands.ContactBoneB(c), hands.ContactPoint(c), -hands.ContactNormal(c));
        FingerEnabled = en;
        return n;
    }
}
