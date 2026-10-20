using System.Numerics;
using PromptFlowStudio.Services.NeuralRagdoll;
using PromptFlowStudio.Services.SkinnedSplatAvatar;
using PromptFlowStudio.Services.SplatEnvironment;
namespace PromptFlowStudio.Services.GaussianFaceHand;

public enum HandPreset : byte { Open, Fist, Rock, Paper, Scissors, Pinch, Grasp, Cross }

/// <summary>
/// [PromptFlow Studio v17.58.0] 🖐 양손 20 관절 정밀 핑거 DQS &amp; 자가 접촉 합성기(<see cref="Step"/> 무할당 · 60 fps).
/// 손당 10 본(엄지 · 검지 · 중지 · 약지 · 소지 × 기절골 · 중위절골) 을 v17.57 래그돌 손 분절(LWrist→LHand · RWrist→RHand = 수근/중수 손바닥 본)
/// 아래에 계층 결합(손바닥 R · 피벗 → MCP 굴곡/외전 → PIP 굴곡)하고, 손가락 캡슐 표면 10 000 가우시안을 4 본 가중치로
/// v17.57 <see cref="RealtimeDualQuaternionGaussianSkinningEngine"/>(22 본 · SIMD · 체적 가드) 로 스키닝한다.
/// 파지(핀치/그립) 시 엄지 말절 캡슐과 각 손가락 캡슐의 선분-선분 최근접 거리로 침투 깊이를 구해 반경 비 분할 접촉 평면을 세우고,
/// 평면을 넘은 표면 가우시안을 평면에 투영 + 법선 방향 공분산 압축(Σ ← PΣP · P = I − (1−κ)nnᵀ) 해 상호 압축 접촉면을 합성한다.
/// </summary>
public sealed class DexterousHandGaussianSkinningSynthesizer : IDisposable
{
    public const int Hands = 2, Fingers = 5, BonesPerHand = 10, FingerBones = 20, PalmBone = 20, DefaultCount = 10_000, MaxContacts = 16;
    public const float Kappa = 0.35f, ContactSlack = 0.0015f, Band = 0.003f;
    private static readonly float[] LenP = { 0.038f, 0.045f, 0.050f, 0.047f, 0.037f }, LenD = { 0.034f, 0.045f, 0.050f, 0.047f, 0.036f };
    private static readonly float[] Rad = { 0.0095f, 0.0085f, 0.0088f, 0.0083f, 0.0072f };
    private static readonly float[] Spread = { 0f, 0.08f, 0f, -0.06f, -0.14f }, Knuckle = { 0f, 0.029f, 0.0095f, -0.0095f, -0.027f }, KnuckleU = { 0f, 0f, 0.004f, 0f, -0.008f };
    private readonly RealtimeDualQuaternionGaussianSkinningEngine _skin;
    private readonly Vector3[] _p0 = new Vector3[FingerBones], _p1 = new Vector3[FingerBones], _flexAx = new Vector3[FingerBones], _abdAx = new Vector3[Hands * Fingers];
    private readonly Vector3[] _pn = new Vector3[Hands];
    private readonly float[] _rad = new float[FingerBones];
    private readonly float[] _flex = new float[FingerBones], _flexT = new float[FingerBones], _abd = new float[Hands * Fingers], _abdT = new float[Hands * Fingers];
    private readonly float[] _pinch = new float[Hands * 4];
    private readonly Vector3[] _ca = new Vector3[FingerBones], _cb = new Vector3[FingerBones];
    private readonly int[] _bLo = new int[FingerBones], _bHi = new int[FingerBones];
    private readonly byte[] _cA = new byte[MaxContacts], _cB = new byte[MaxContacts];
    private readonly bool[] _cOn = new bool[MaxContacts];
    private readonly Vector3[] _cP = new Vector3[MaxContacts], _cN = new Vector3[MaxContacts];
    private readonly float[] _cPen = new float[MaxContacts];
    private Vector3[] _restN = Array.Empty<Vector3>();
    private GaussianSplatScene? _scene;
    private int _start, _n;

    public DexterousHandGaussianSkinningSynthesizer(int capacity = DefaultCount, int workers = -1)
    {
        _skin = new RealtimeDualQuaternionGaussianSkinningEngine(capacity, workers);
        BuildRest();
        var c = 0;
        for (var h = 0; h < Hands; h++)
            for (var f = 1; f < Fingers; f++)
                for (var s = 0; s < 2; s++) { _cA[c] = (byte)Bone(h, 0, 1); _cB[c] = (byte)Bone(h, f, s); c++; }
        for (var h = 0; h < Hands; h++) SolvePinch(h);
        SetPreset(HandPreset.Open, true);
    }

    public RealtimeDualQuaternionGaussianSkinningEngine Skinning => _skin;
    public VolumetricDeterminantClampingGuard VolumeGuard { get; } = new();
    public HandPreset Preset { get; private set; }
    public bool ContactEnabled { get; set; } = true;
    public float Smoothing { get; set; } = 14f;
    public int Count => _n;
    public int Start => _start;
    public int ActiveContacts { get; private set; }
    public int ContactSplats { get; private set; }
    public float PeakPenetrationMm { get; private set; }
    public float FramePenetrationMm { get; private set; }
    public long ContactFrames { get; private set; }
    public double LastStepUs { get; private set; }
    public static int Bone(int hand, int finger, int seg) => hand * BonesPerHand + finger * 2 + seg;
    public static int WristJoint(int hand) => hand == 0 ? (int)RagdollJoint.LWrist : (int)RagdollJoint.RWrist;
    public static int HandJoint(int hand) => hand == 0 ? (int)RagdollJoint.LHand : (int)RagdollJoint.RHand;
    public int BoneStart(int b) => _bLo[b];
    public int BoneEnd(int b) => _bHi[b];
    public float Radius(int b) => _rad[b];
    public Vector3 CapsuleA(int b) => _ca[b];
    public Vector3 CapsuleB(int b) => _cb[b];
    public Vector3 RestTip(int b) => _p1[b];
    public float Flex(int b) => _flex[b];
    public bool ContactOn(int c) => _cOn[c];
    public int ContactBoneA(int c) => _cA[c];
    public int ContactBoneB(int c) => _cB[c];
    public Vector3 ContactPoint(int c) => _cP[c];
    public Vector3 ContactNormal(int c) => _cN[c];
    public Vector3 RestNormal(int k) => _restN[k];

    public static string PresetName(HandPreset p) => p switch
    {
        HandPreset.Open => "🖐 펴기", HandPreset.Fist => "✊ 주먹", HandPreset.Rock => "✊ 바위", HandPreset.Paper => "🖐 보",
        HandPreset.Scissors => "✌ 가위", HandPreset.Pinch => "🤏 핀치", HandPreset.Grasp => "🫳 그립", _ => "🤞 교차(관통 주입)",
    };

    // ══════════════════════ 휴지 골격(비핫패스) ══════════════════════
    private void BuildRest()
    {
        var rest = RagdollSkeleton.Rest;
        var side = Vector3.Normalize(rest[(int)RagdollJoint.RHip] - rest[(int)RagdollJoint.LHip]);
        var up = Vector3.Normalize(rest[(int)RagdollJoint.Chest] - rest[(int)RagdollJoint.Pelvis]);
        var fwd = Vector3.Normalize(Vector3.Cross(side, up));
        for (var h = 0; h < Hands; h++)
        {
            Vector3 w = rest[WristJoint(h)], hj = rest[HandJoint(h)];
            var u = Vector3.Normalize(hj - w);
            var t = fwd - u * Vector3.Dot(fwd, u); t = t.LengthSquared() > 1e-8f ? Vector3.Normalize(t) : Vector3.Normalize(Vector3.Cross(u, up));
            var pn = Vector3.Normalize(Vector3.Cross(u, t));
            if (Vector3.Dot(pn, rest[(int)RagdollJoint.Chest] - w) < 0f) pn = -pn;
            _pn[h] = pn;
            for (var f = 0; f < Fingers; f++)
            {
                Vector3 mcp, d;
                if (f == 0) { mcp = w + u * 0.025f + t * 0.03f; d = Vector3.Normalize(u * 0.7f + t * 0.7f + pn * 0.25f); }
                else { mcp = hj - u * (0.005f - KnuckleU[f]) + t * Knuckle[f]; d = Vector3.Normalize(u + t * Spread[f]); }
                int bp = Bone(h, f, 0), bd = Bone(h, f, 1);
                _p0[bp] = mcp; _p1[bp] = mcp + d * LenP[f]; _p0[bd] = _p1[bp]; _p1[bd] = _p1[bp] + d * LenD[f];
                _rad[bp] = Rad[f]; _rad[bd] = Rad[f] * 0.9f;
                var curl = f == 0 ? Vector3.Normalize(pn - t * 0.8f) : pn;
                var ax = Vector3.Cross(d, curl); ax = ax.LengthSquared() > 1e-8f ? Vector3.Normalize(ax) : Vector3.Normalize(Vector3.Cross(d, up));
                _flexAx[bp] = ax; _flexAx[bd] = ax;
                var spreadDir = f is 1 or 2 || f == 0 ? t : -t;
                _abdAx[h * Fingers + f] = Vector3.Dot(Vector3.Cross(pn, d), spreadDir) >= 0f ? pn : -pn;
            }
        }
    }

    /// <summary>손바닥 프레임 기준 손가락 FK(휴지 공간) — 말절 끝점.</summary>
    private Vector3 RestFrameTip(int h, int f, float th1, float th2, float abd)
    {
        int bp = Bone(h, f, 0), bd = Bone(h, f, 1);
        var lp = Quaternion.CreateFromAxisAngle(_abdAx[h * Fingers + f], abd) * Quaternion.CreateFromAxisAngle(_flexAx[bp], th1);
        var ld = lp * Quaternion.CreateFromAxisAngle(_flexAx[bd], th2);
        var pip = _p0[bp] + Vector3.Transform(_p1[bp] - _p0[bp], lp);
        return pip + Vector3.Transform(_p1[bd] - _p0[bd], ld);
    }

    /// <summary>핀치 각 탐색(격자 · 비핫패스): 엄지/검지 말절 끝 거리 = 반경 합 × 0.55(살짝 압축) · 최소 굴곡.</summary>
    private void SolvePinch(int h)
    {
        var target = (_rad[Bone(h, 0, 1)] + _rad[Bone(h, 1, 1)]) * 0.55f;
        var best = float.MaxValue;
        for (var a = 0; a <= 12; a++)
            for (var b = 0; b <= 12; b++)
            {
                var tt = RestFrameTip(h, 0, a * 0.1f, b * 0.1f, 0f);
                for (var c = 0; c <= 14; c++)
                    for (var d = 0; d <= 16; d++)
                    {
                        var ti = RestFrameTip(h, 1, c * 0.1f, d * 0.1f, 0f);
                        var cost = MathF.Abs(Vector3.Distance(tt, ti) - target) + 0.002f * (a + b + c + d) * 0.1f;
                        if (cost < best) { best = cost; _pinch[h * 4] = a * 0.1f; _pinch[h * 4 + 1] = b * 0.1f; _pinch[h * 4 + 2] = c * 0.1f; _pinch[h * 4 + 3] = d * 0.1f; }
                    }
            }
    }

    public void SetPreset(HandPreset p, bool snap = false)
    {
        Preset = p;
        for (var h = 0; h < Hands; h++)
            for (var f = 0; f < Fingers; f++)
            {
                float m, q, ab = 0f;
                var curled = f == 0 ? (0.6f, 0.8f) : (1.45f, 1.6f);
                (m, q) = p switch
                {
                    HandPreset.Fist or HandPreset.Rock => curled,
                    HandPreset.Paper => (0f, 0f),
                    HandPreset.Scissors => f is 1 or 2 ? (0.05f, 0.05f) : curled,
                    HandPreset.Pinch => f <= 1 ? (_pinch[h * 4 + f * 2], _pinch[h * 4 + f * 2 + 1]) : (0.3f, 0.4f),
                    HandPreset.Grasp => f == 0 ? (0.5f, 0.4f) : (0.7f, 0.8f),
                    HandPreset.Cross => f == 0 ? (0.2f, 0.2f) : (0.12f, 0.1f),
                    _ => (0.05f, 0.08f),
                };
                if (p == HandPreset.Paper) ab = 0.18f;
                else if (p == HandPreset.Scissors && f is 1 or 2) ab = 0.22f;
                else if (p == HandPreset.Cross) ab = f switch { 1 => -0.30f, 2 => -0.38f, 3 => -0.30f, _ => 0f };
                _flexT[Bone(h, f, 0)] = m; _flexT[Bone(h, f, 1)] = q; _abdT[h * Fingers + f] = ab;
            }
        if (snap) { _flexT.AsSpan().CopyTo(_flex); _abdT.AsSpan().CopyTo(_abd); }
    }

    /// <summary>손가락 굴곡 직접 지정(손 · 손가락 · MCP/PIP 라디안).</summary>
    public void SetFinger(int hand, int finger, float mcp, float pip, float abd = 0f)
    {
        _flexT[Bone(hand, finger, 0)] = mcp; _flexT[Bone(hand, finger, 1)] = pip; _abdT[hand * Fingers + finger] = abd;
    }

    /// <summary>손가락 캡슐 표면 스플랫 바인드(비핫패스) — 본 순 연속 구간.</summary>
    public int Bind(GaussianSplatScene scene, int start, int target, uint seed = 0x4A11Du)
    {
        target = Math.Min(target, _skin.Capacity);
        var weights = new BoneWeights4[target]; _restN = new Vector3[target];
        Span<float> area = stackalloc float[FingerBones];
        var total = 0f;
        for (var b = 0; b < FingerBones; b++)
        {
            var L = Vector3.Distance(_p0[b], _p1[b]); var r = _rad[b];
            area[b] = 2f * MathF.PI * r * L + ((b & 1) == 1 ? 2f * MathF.PI * r * r : 0f); total += area[b];
        }
        var rng = seed == 0 ? 1u : seed;
        float R() { rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5; return (rng >> 8) * (1f / 16777216f); }
        var n = 0;
        var skinCol = new Vector3(0.80f, 0.60f, 0.50f); var nail = new Vector3(0.90f, 0.74f, 0.70f);
        for (var b = 0; b < FingerBones; b++)
        {
            _bLo[b] = n;
            var cnt = b == FingerBones - 1 ? target - n : (int)(target * area[b] / total);
            Vector3 A = _p0[b], B = _p1[b]; var axis = B - A; var L = axis.Length(); axis /= L;
            var r = _rad[b]; var distal = (b & 1) == 1;
            var t1 = Vector3.Normalize(Vector3.Cross(MathF.Abs(axis.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX, axis)); var t2 = Vector3.Cross(axis, t1);
            var disc = MathF.Sqrt(area[b] / MathF.Max(cnt, 1)) * 0.8f;
            var capFrac = distal ? 2f * MathF.PI * r * r / area[b] : 0f;
            var h = b / BonesPerHand;
            for (var k = 0; k < cnt && n < target; k++)
            {
                float u1 = R(), u2 = R(), u3 = R();
                Vector3 p, nrm; float t;
                if (u3 < capFrac)
                {
                    var z = u1; var a = MathF.Tau * u2; var rr = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
                    nrm = t1 * (rr * MathF.Cos(a)) + t2 * (rr * MathF.Sin(a)) + axis * z; p = B + nrm * r; t = 1f;
                }
                else { var a = MathF.Tau * u2; nrm = t1 * MathF.Cos(a) + t2 * MathF.Sin(a); t = u1; p = A + axis * (L * t) + nrm * r; }
                var ta = Vector3.Normalize(Vector3.Cross(MathF.Abs(nrm.Y) < 0.95f ? Vector3.UnitY : Vector3.UnitX, nrm)); var tb = Vector3.Cross(nrm, ta);
                var col = distal && t > 0.8f && Vector3.Dot(nrm, _pn[h]) < -0.5f ? nail : skinCol;
                scene.Write(start + n, p, ta, tb, nrm, new Vector3(disc, disc, disc * 0.2f), 0.97f, col, 6);
                _restN[n] = nrm;
                var w = new BoneWeights4 { B0 = (byte)b, W0 = 1f };
                if (!distal)
                {
                    var wp = 0.5f * Sm(1f - t / 0.25f); var wc = 0.5f * Sm((t - 0.75f) / 0.25f);
                    w.W0 = 1f - wp - wc; w.B1 = (byte)(PalmBone + h); w.W1 = wp; w.B2 = (byte)(b + 1); w.W2 = wc;
                }
                else { var wp = 0.5f * Sm(1f - t / 0.25f); w.W0 = 1f - wp; w.B1 = (byte)(b - 1); w.W1 = wp; }
                weights[n] = w;
                n++;
            }
            _bHi[b] = n;
        }
        _scene = scene; _start = start; _n = n;
        _skin.Bind(scene, start, n, weights);
        return n;
    }

    private static float Sm(float x) { x = Math.Clamp(x, 0f, 1f); return x * x * (3f - 2f * x); }

    // ══════════════════════ 프레임(무할당) ══════════════════════
    /// <summary>손바닥(v17.57 손 분절 본) → 손가락 FK → DQS 스키닝 → 자가 접촉 합성.</summary>
    public void Step(RealtimeDualQuaternionGaussianSkinningEngine body, ReadOnlySpan<Vector3> joints, float dt)
    {
        if (_scene is null) return;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var a = 1f - MathF.Exp(-dt * Smoothing);
        for (var i = 0; i < FingerBones; i++) _flex[i] += (_flexT[i] - _flex[i]) * a;
        for (var i = 0; i < _abd.Length; i++) _abd[i] += (_abdT[i] - _abd[i]) * a;
        var rest = RagdollSkeleton.Rest;
        for (var h = 0; h < Hands; h++)
        {
            int wj = WristJoint(h), hj = HandJoint(h);
            var rp = body.BoneRotation(hj); var w0 = rest[wj]; var w1 = joints[wj];
            _skin.SetBone(PalmBone + h, rp, w0, w1);
            for (var f = 0; f < Fingers; f++)
            {
                int bp = Bone(h, f, 0), bd = Bone(h, f, 1);
                var lp = Quaternion.CreateFromAxisAngle(_abdAx[h * Fingers + f], _abd[h * Fingers + f]) * Quaternion.CreateFromAxisAngle(_flexAx[bp], _flex[bp]);
                var gp = rp * lp;
                var mcp = Vector3.Transform(_p0[bp] - w0, rp) + w1;
                _skin.SetBone(bp, gp, _p0[bp], mcp);
                var pip = mcp + Vector3.Transform(_p1[bp] - _p0[bp], gp);
                var gd = gp * Quaternion.CreateFromAxisAngle(_flexAx[bd], _flex[bd]);
                _skin.SetBone(bd, gd, _p0[bd], pip);
                _ca[bp] = mcp; _cb[bp] = pip; _ca[bd] = pip; _cb[bd] = pip + Vector3.Transform(_p1[bd] - _p0[bd], gd);
            }
        }
        _skin.Skin(VolumeGuard);
        SynthesizeContacts();
        LastStepUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>선분 [p0,p1] · [q0,q1] 최근접 점 쌍.</summary>
    public static void ClosestSegSeg(Vector3 p0, Vector3 p1, Vector3 q0, Vector3 q1, out Vector3 c1, out Vector3 c2)
    {
        Vector3 d1 = p1 - p0, d2 = q1 - q0, r = p0 - q0;
        float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
        float s, t;
        if (a <= 1e-12f && e <= 1e-12f) { c1 = p0; c2 = q0; return; }
        if (a <= 1e-12f) { s = 0f; t = Math.Clamp(f / e, 0f, 1f); }
        else
        {
            var c = Vector3.Dot(d1, r);
            if (e <= 1e-12f) { t = 0f; s = Math.Clamp(-c / a, 0f, 1f); }
            else
            {
                var b = Vector3.Dot(d1, d2); var den = a * e - b * b;
                s = den > 1e-12f ? Math.Clamp((b * f - c * e) / den, 0f, 1f) : 0f;
                t = (b * s + f) / e;
                if (t < 0f) { t = 0f; s = Math.Clamp(-c / a, 0f, 1f); }
                else if (t > 1f) { t = 1f; s = Math.Clamp((b - c) / a, 0f, 1f); }
            }
        }
        c1 = p0 + d1 * s; c2 = q0 + d2 * t;
    }

    public static Vector3 ClosestOnSeg(Vector3 a, Vector3 b, Vector3 x)
    {
        var d = b - a; var l2 = d.LengthSquared();
        return l2 <= 1e-12f ? a : a + d * Math.Clamp(Vector3.Dot(x - a, d) / l2, 0f, 1f);
    }

    /// <summary>엄지 말절 × 손가락 캡슐 접촉 → 반경 비 분할 평면 투영 + 법선 공분산 압축.</summary>
    private void SynthesizeContacts()
    {
        var sc = _scene!;
        int act = 0, touched = 0; var peak = 0f;
        for (var c = 0; c < MaxContacts; c++)
        {
            _cOn[c] = false;
            if (!ContactEnabled) continue;
            int A = _cA[c], B = _cB[c];
            ClosestSegSeg(_ca[A], _cb[A], _ca[B], _cb[B], out var c1, out var c2);
            float rA = _rad[A], rB = _rad[B];
            var dv = c2 - c1; var d = dv.Length();
            var pen = rA + rB - d;
            if (pen <= -ContactSlack) continue;
            var nrm = d > 1e-6f ? dv / d : _pn[A / BonesPerHand];
            var pc = c1 + nrm * (rA - MathF.Max(pen, 0f) * rA / (rA + rB));
            _cOn[c] = true; _cP[c] = pc; _cN[c] = nrm; _cPen[c] = pen;
            act++; if (pen > peak) peak = pen;
            var lim = 2.5f * (rA + rB);
            touched += Flatten(sc, A, pc, nrm, lim) + Flatten(sc, B, pc, -nrm, lim);
        }
        ActiveContacts = act; ContactSplats = touched;
        FramePenetrationMm = peak * 1000f;
        if (FramePenetrationMm > PeakPenetrationMm) PeakPenetrationMm = FramePenetrationMm;
        if (act > 0) ContactFrames++;
    }

    private int Flatten(GaussianSplatScene sc, int bone, Vector3 pc, Vector3 n, float lim)
    {
        var cnt = 0; var lim2 = lim * lim;
        for (var k = _bLo[bone]; k < _bHi[bone]; k++)
        {
            var i = _start + k;
            var x = new Vector3(sc.X[i], sc.Y[i], sc.Z[i]);
            var s = Vector3.Dot(x - pc, n);
            if (s <= -Band) continue;
            var lat = x - pc - n * s;
            if (lat.LengthSquared() > lim2) continue;
            if (s > 0f) { x -= n * s; sc.X[i] = x.X; sc.Y[i] = x.Y; sc.Z[i] = x.Z; }
            CompressAlong(sc, i, n, Kappa);
            cnt++;
        }
        return cnt;
    }

    /// <summary>Σ ← PΣP · P = I − (1−κ)nnᵀ(법선 방향 압축 · 대칭 보존).</summary>
    public static void CompressAlong(GaussianSplatScene sc, int i, Vector3 n, float kappa)
    {
        float a = sc.C00[i], b = sc.C01[i], c = sc.C02[i], d = sc.C11[i], f = sc.C12[i], g = sc.C22[i];
        var q = 1f - kappa;
        // v = Σn · α = nᵀΣn · PΣP = Σ − q(n vᵀ + v nᵀ) + q² α n nᵀ
        float vx = a * n.X + b * n.Y + c * n.Z, vy = b * n.X + d * n.Y + f * n.Z, vz = c * n.X + f * n.Y + g * n.Z;
        var al = n.X * vx + n.Y * vy + n.Z * vz; var q2 = q * q * al;
        sc.C00[i] = a - 2f * q * n.X * vx + q2 * n.X * n.X;
        sc.C01[i] = b - q * (n.X * vy + vx * n.Y) + q2 * n.X * n.Y;
        sc.C02[i] = c - q * (n.X * vz + vx * n.Z) + q2 * n.X * n.Z;
        sc.C11[i] = d - 2f * q * n.Y * vy + q2 * n.Y * n.Y;
        sc.C12[i] = f - q * (n.Y * vz + vy * n.Z) + q2 * n.Y * n.Z;
        sc.C22[i] = g - 2f * q * n.Z * vz + q2 * n.Z * n.Z;
        var tr = sc.C00[i] + sc.C11[i] + sc.C22[i];
        sc.MaxSigma[i] = tr > 0f ? MathF.Sqrt(tr) : sc.MaxSigma[i];
    }

    public void Dispose() => _skin.Dispose();
}
