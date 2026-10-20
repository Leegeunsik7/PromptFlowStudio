using System.Numerics;
using PromptFlowStudio.Services.Mocap;

namespace PromptFlowStudio.Services.MarkerlessMocap;

public enum MocapRigPreset : byte { DigitalHuman = 0, OpenUsdHero, OpenUsdStylized }

/// <summary>
/// [PromptFlow Studio v17.11.0] 🦿 생체역학 스켈레톤 리타게팅 &amp; 발 밀림 방지 합성기(<see cref="Retarget"/> 무할당).
///   ▸ 리타게팅: 소스 3D 관절 → 전역 회전(최소 스윙 · 골반/가슴 프레임) → 로컬 → 대상 리그 뼈 오프셋 FK · 루트 이동 = 골반 높이 비 스케일.
///     대상 = 디지털 휴먼(v16.82 · 1.78 m) · OpenUSD 캐릭터 리그(히어로 롱레그 · 스타일라이즈드 치비).
///   ▸ 접지 감지: 발목/발끝 지면 높이 + 수평 속도 · 히스테리시스(진입 3 cm · 0.4 m/s / 이탈 5 cm · 0.9 m/s) · 밀착도 0..1.
///   ▸ 발 고정: 접지 진입 시 발목 락(지면 투영) · 블렌드 인 50 ms / 아웃 200 ms · 도달 불가 시 골반 하강 · 엉덩이-무릎-발목 FABRIK(무릎 폴 = 원 무릎 평면 · 2본 정확 폐합) · 발끝 지면 클램프.
/// </summary>
public sealed class BiomechanicalSkeletonRetargetingSynthesizer
{
    private const int J = MocapSkeleton.Body;
    public const float EnterHeight = 0.03f, ExitHeight = 0.05f, EnterSpeed = 0.4f, ExitSpeed = 0.9f, BlendIn = 0.05f, BlendOut = 0.2f;

    private static readonly int[] HipJ = { MocapSkeleton.LHip, MocapSkeleton.RHip }, KneeJ = { MocapSkeleton.LKnee, MocapSkeleton.RKnee },
        AnkleJ = { MocapSkeleton.LAnkle, MocapSkeleton.RAnkle }, ToeJ = { MocapSkeleton.LFoot, MocapSkeleton.RFoot };

    private readonly Quaternion[] _g = new Quaternion[J], _local = new Quaternion[J], _rawG = new Quaternion[J];
    private readonly Vector3[] _raw = new Vector3[J], _out = new Vector3[J];
    private readonly Vector3[] _lock = new Vector3[2], _prevRawAnkle = new Vector3[2], _prevOutAnkle = new Vector3[2];
    private readonly bool[] _contact = new bool[2];
    private readonly float[] _w = new float[2], _conf = new float[2];
    private bool _hasPrev;
    private CharacterRig _rig = BuildRig(MocapRigPreset.DigitalHuman);

    public MocapRigPreset Preset { get; private set; } = MocapRigPreset.DigitalHuman;
    public CharacterRig Rig => _rig;
    public bool FootLock { get; set; } = true;
    public bool GroundClamp { get; set; } = true;

    public ReadOnlySpan<Vector3> Output => _out;
    public ReadOnlySpan<Vector3> RawOutput => _raw;
    public Vector3 Out(int j) => _out[j];
    public Vector3 Raw(int j) => _raw[j];
    public bool Contact(int side) => _contact[side];
    public float ContactConfidence(int side) => _conf[side];
    public float LockWeight(int side) => _w[side];
    public Vector3 LockPosition(int side) => _lock[side];
    public float FootSlideRawMmS { get; private set; }
    public float FootSlideMmS { get; private set; }
    public float GroundPenetrationRawMm { get; private set; }
    public float GroundPenetrationMm { get; private set; }
    public float PelvisDropMm { get; private set; }
    public float BoneLengthErrorMm { get; private set; }
    public int FabrikIterations { get; private set; }
    public double LastRetargetUs { get; private set; }
    public long Frames { get; private set; }
    public float AnkleGround => _rig.Rest[MocapSkeleton.LAnkle].Y;
    public float ToeGround => _rig.Rest[MocapSkeleton.LFoot].Y;

    public static string PresetName(MocapRigPreset p) => p switch
    {
        MocapRigPreset.OpenUsdHero => "OpenUSD 히어로 리그(롱레그)",
        MocapRigPreset.OpenUsdStylized => "OpenUSD 스타일라이즈드(치비)",
        _ => "디지털 휴먼 v16.82(1.78 m)",
    };

    public static CharacterRig BuildRig(MocapRigPreset p)
    {
        var rig = p switch
        {
            MocapRigPreset.OpenUsdHero => AutoRiggingAndRetargetingSynthesizer.ReferenceRig(1.0f, 1.22f, 1.08f, 0.9f),
            MocapRigPreset.OpenUsdStylized => AutoRiggingAndRetargetingSynthesizer.ReferenceRig(0.62f, 0.7f, 0.85f, 1.8f),
            _ => AutoRiggingAndRetargetingSynthesizer.ReferenceRig(1.78f / MocapSkeleton.ReferenceHeight),
        };
        rig.Name = PresetName(p);
        return rig;
    }

    /// <summary>대상 리그 교체(제어 평면 · 접지 상태 초기화).</summary>
    public void SetPreset(MocapRigPreset p)
    {
        Preset = p;
        _rig = BuildRig(p);
        ResetContacts();
    }

    public void ResetContacts()
    {
        Array.Clear(_contact); Array.Clear(_w); Array.Clear(_conf);
        _hasPrev = false;
    }

    private static Vector3 Norm(Vector3 v, Vector3 fallback)
    {
        var l = v.Length();
        return l > 1e-9f ? v / l : fallback;
    }

    /// <summary>
    /// 2본 체인 FABRIK(a 고정) — 도달 불가 시 직선 신장 · 반복 후 폴 평면 정렬 + 코사인 법칙 폐합(뼈 길이 · 목표 정확). 반환 = 반복 수. 무할당.
    /// </summary>
    public static int Fabrik(ref Vector3 a, ref Vector3 b, ref Vector3 c, Vector3 target, float l0, float l1, Vector3 pole, float tol = 1e-4f, int maxIter = 8)
    {
        var root = a;
        var toT = target - root;
        if (toT.Length() >= l0 + l1)
        {
            var dir = Norm(toT, -Vector3.UnitY);
            b = root + dir * l0; c = b + dir * l1;
            return 0;
        }
        // 일직선 체인 특이점 회피: 무릎을 폴 방향으로 미리 굽힘
        var ax0 = Norm(toT, -Vector3.UnitY);
        var pr0 = pole - root;
        var pp0 = pr0 - ax0 * Vector3.Dot(pr0, ax0);
        if (pp0.LengthSquared() > 1e-10f) b += Vector3.Normalize(pp0) * (0.1f * l0);
        var it = 0;
        for (; it < maxIter; it++)
        {
            if (Vector3.Distance(c, target) < tol) break;
            c = target;
            b = c + Norm(b - c, Vector3.UnitY) * l1;
            b = root + Norm(b - root, -Vector3.UnitY) * l0;
            c = b + Norm(c - b, -Vector3.UnitY) * l1;
        }
        // 폴 평면 정렬 + 2본 정확 폐합(법선 근방 FABRIK 선형 수렴 꼬리 제거): |b−a| = l0 · |c−b| = l1 · c = target
        var dvec = target - root;
        var d = Math.Clamp(dvec.Length(), MathF.Abs(l0 - l1) + 1e-5f, l0 + l1 - 1e-5f);
        var axis = Norm(dvec, -Vector3.UnitY);
        var pr = pole - root;
        var pp = pr - axis * Vector3.Dot(pr, axis);
        if (pp.LengthSquared() < 1e-10f) { var rel = b - root; pp = rel - axis * Vector3.Dot(rel, axis); }
        var perpDir = Norm(pp, Vector3.UnitZ);
        var along = (l0 * l0 - l1 * l1 + d * d) / (2f * d);
        b = root + axis * along + perpDir * MathF.Sqrt(MathF.Max(0f, l0 * l0 - along * along));
        c = root + axis * d;
        return it;
    }

    /// <summary>소스 관절(실측 스케일 s) → 대상 리그 포즈 + 발 고정(무할당).</summary>
    public void Retarget(ReadOnlySpan<Vector3> src, float sourceScale, float dt)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var rig = _rig;
        AutoRiggingAndRetargetingSynthesizer.SolveGlobalRotations(src, _g);
        for (var j = 0; j < J; j++)
        {
            var p = MocapSkeleton.Parent[j];
            _local[j] = p < 0 ? _g[j] : Quaternion.Normalize(Quaternion.Inverse(_g[p]) * _g[j]);
        }
        var k = rig.PelvisHeight / MathF.Max(1e-3f, MocapSkeleton.RestOffset[0].Y * sourceScale);
        var s0 = src[MocapSkeleton.Pelvis];
        MocapSkeleton.ForwardKinematics(rig.Offset, _local, new Vector3(s0.X * k, rig.Ground + s0.Y * k, s0.Z * k), _raw, _rawG);
        _raw.AsSpan().CopyTo(_out);

        float ag = AnkleGround, tg = ToeGround;
        Span<Vector3> target = stackalloc Vector3[2];
        float penRaw = 0, slideRaw = 0, slideOut = 0;
        var contactN = 0;
        for (var side = 0; side < 2; side++)
        {
            Vector3 an = _raw[AnkleJ[side]], to = _raw[ToeJ[side]];
            var h = MathF.Min(an.Y - ag, to.Y - tg);
            penRaw = MathF.Max(penRaw, -MathF.Min(an.Y - ag, to.Y - rig.Ground));
            var hv = 0f;
            if (_hasPrev) { var d = an - _prevRawAnkle[side]; d.Y = 0; hv = d.Length() / dt; }
            _prevRawAnkle[side] = an;
            var was = _contact[side];
            var now = was ? !(h > ExitHeight || hv > ExitSpeed) : h < EnterHeight && hv < EnterSpeed;
            _conf[side] = Math.Clamp(1f - MathF.Max(0f, h) / ExitHeight, 0f, 1f) * Math.Clamp(1f - hv / ExitSpeed, 0f, 1f);
            if (now && !was) _lock[side] = new Vector3(an.X, MathF.Max(an.Y, ag), an.Z);
            _contact[side] = now;
            if (now) { slideRaw += hv; contactN++; }
            _w[side] = FootLock ? (now ? MathF.Min(1f, _w[side] + dt / BlendIn) : MathF.Max(0f, _w[side] - dt / BlendOut)) : 0f;
            var tgt = Vector3.Lerp(an, _lock[side], _w[side]);
            if (GroundClamp && tgt.Y < ag) tgt.Y = ag;
            target[side] = tgt;
        }

        // 도달 불가 → 골반 하강
        var dy = 0f;
        for (var side = 0; side < 2; side++)
        {
            var hip = _out[HipJ[side]];
            var len = (rig.BoneLength(KneeJ[side]) + rig.BoneLength(AnkleJ[side])) * 0.999f;
            float dx = hip.X - target[side].X, dz = hip.Z - target[side].Z;
            var dh2 = dx * dx + dz * dz;
            var vy = hip.Y - target[side].Y;
            if (vy * vy + dh2 > len * len) dy = MathF.Min(dy, target[side].Y + MathF.Sqrt(MathF.Max(0f, len * len - dh2)) - hip.Y);
        }
        if (dy < 0) for (var j = 0; j < J; j++) _out[j].Y += dy;
        PelvisDropMm = -dy * 1000f;

        var iters = 0;
        float pen = 0;
        for (var side = 0; side < 2; side++)
        {
            int hj = HipJ[side], kj = KneeJ[side], aj = AnkleJ[side], tj = ToeJ[side];
            var toeRel = _raw[tj] - _raw[aj];
            Vector3 a = _out[hj], b = _out[kj], c = _out[aj];
            var pole = _raw[kj] + new Vector3(0, dy, 0);
            iters += Fabrik(ref a, ref b, ref c, target[side], rig.BoneLength(kj), rig.BoneLength(aj), pole);
            _out[kj] = b; _out[aj] = c;
            var toe = c + toeRel;
            if (GroundClamp && toe.Y < rig.Ground)
            {
                // 발끝을 지면 위에 두고 발 길이 보존: 수평 성분 재계산
                var tl = toeRel.Length();
                var vy = rig.Ground - c.Y;
                var hd = new Vector3(toeRel.X, 0, toeRel.Z);
                toe = c + Norm(hd, Vector3.UnitZ) * MathF.Sqrt(MathF.Max(0f, tl * tl - vy * vy)) + new Vector3(0, vy, 0);
            }
            _out[tj] = toe;
            pen = MathF.Max(pen, -MathF.Min(c.Y - ag, toe.Y - rig.Ground));
            if (_contact[side] && _hasPrev) { var d = c - _prevOutAnkle[side]; d.Y = 0; slideOut += d.Length() / dt; }
            _prevOutAnkle[side] = c;
        }
        FabrikIterations = iters;
        GroundPenetrationRawMm = MathF.Max(0f, penRaw) * 1000f;
        GroundPenetrationMm = MathF.Max(0f, pen) * 1000f;
        if (contactN > 0)
        {
            FootSlideRawMmS += 0.1f * (slideRaw / contactN * 1000f - FootSlideRawMmS);
            FootSlideMmS += 0.1f * (slideOut / contactN * 1000f - FootSlideMmS);
        }

        var berr = 0f;
        for (var j = 1; j < J; j++)
            berr = MathF.Max(berr, MathF.Abs(Vector3.Distance(_out[j], _out[MocapSkeleton.Parent[j]]) - rig.BoneLength(j)));
        BoneLengthErrorMm = berr * 1000f;
        _hasPrev = true;
        Frames++;
        LastRetargetUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }
}
