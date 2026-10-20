using System.Numerics;
using PromptFlowStudio.Services.MarkerlessMocap;
using PromptFlowStudio.Services.Mocap;

namespace PromptFlowStudio.Services.DexterousHand;

public enum DigitJoint : byte { Mcp = 0, Pip, Dip }

/// <summary>
/// [PromptFlow Studio v17.13.0] 🦴 지간 자가 관통 &amp; 관절 탈구 방지 가드(<see cref="Process"/> 무할당 · µs 계측).
///   ▸ 관절 각 분해(손가락별 전파 프레임): MCP = 굴곡 + 외전(볼) · PIP/DIP = 굴곡(힌지) + 측방 편위. 굴곡 + = 손바닥 쪽 · 모두 레스트 대비.
///   ▸ ROM(°): 손가락 MCP 굴곡 [−35, 95] · 외전 ±22 · PIP [−8, 115] · DIP [−15, 85] / 엄지 CMC [−40, 70] · ±45 · MCP [−15, 70] · IP [−20, 85].
///     범위 이탈(과신전 등) → 한계로 클램프.
///   ▸ 탈구/비정상 비틀림: 힌지 측방 편위 &gt; 12°(엄지 15°) 또는 뼈 길이 비 ∉ [0.75, 1.3] → 해당 관절을 직전 유효 각도로 복귀.
///   ▸ 재구성: 보정 뼈 길이(유효 프레임 EMA) · MCP 고정 → 손끝까지 FK(뼈 길이 불변).
///   ▸ 지간 자가 충돌: 인접 손가락 쌍 4(엄지-검지 · 검지-중지 · 중지-약지 · 약지-소지) × 마디 캡슐 3×3 선분 최근접 &lt; 2r → 대칭 분리 + 뼈 길이 재정규화(≤ 8 패스).
/// </summary>
public sealed class InterDigitalSelfCollisionDislocationGuard
{
    private const int H = HandTopology.Hands, P = HandTopology.Points, F = HandTopology.Fingers;
    public const float PhalanxRadius = 0.006f, HingeLateralMax = 12f, ThumbLateralMax = 15f, LenLo = 0.75f, LenHi = 1.3f;

    // [finger][joint] 한계(°) — 손가락 0 = 엄지
    private static readonly float[] FlexLo = { -40, -15, -20, -35, -8, -15, -35, -8, -15, -35, -8, -15, -35, -8, -15 };
    private static readonly float[] FlexHi = { 70, 70, 85, 95, 115, 85, 95, 115, 85, 95, 115, 85, 95, 115, 85 };
    private static readonly float[] AbdMax = { 45, 22, 22, 22, 22 };

    private readonly float[] _restFlex = new float[H * F * 3], _restAbd = new float[H * F], _restLat = new float[H * F * 2];
    private readonly float[] _validFlex = new float[H * F * 3], _validAbd = new float[H * F];
    private readonly float[] _flex = new float[H * F * 3], _abd = new float[H * F], _lat = new float[H * F * 2], _flexRaw = new float[H * F * 3];
    private readonly float[] _len = new float[H * P];
    private readonly bool[] _hasValid = new bool[H], _calib = new bool[H], _clampedJ = new bool[H * F * 3], _revertedJ = new bool[H * F * 3];
    private readonly Vector3[] _out = new Vector3[H * P];
    private readonly int[] _pairs = { 0, 1, 1, 2, 2, 3, 3, 4 };

    public InterDigitalSelfCollisionDislocationGuard()
    {
        for (var h = 0; h < H; h++)
            for (var f = 0; f < F; f++)
            {
                var i = (h * F + f) * 3;
                _restFlex[i] = HandTopology.RestAngle(h, f, 0); _restFlex[i + 1] = HandTopology.RestAngle(h, f, 2); _restFlex[i + 2] = HandTopology.RestAngle(h, f, 4);
                _restAbd[h * F + f] = HandTopology.RestAngle(h, f, 1); _restLat[(h * F + f) * 2] = HandTopology.RestAngle(h, f, 3); _restLat[(h * F + f) * 2 + 1] = HandTopology.RestAngle(h, f, 5);
            }
    }

    public bool RomGuard { get; set; } = true;
    public bool DislocationGuard { get; set; } = true;
    public bool CollisionGuard { get; set; } = true;
    public ReadOnlySpan<Vector3> Output => _out;
    public Vector3 Out(int k) => _out[k];
    /// <summary>레스트 대비 굴곡(°) — 가드 후.</summary>
    public float Flexion(int hand, int finger, DigitJoint j) => _flex[(hand * F + finger) * 3 + (int)j];
    public float RawFlexion(int hand, int finger, DigitJoint j) => _flexRaw[(hand * F + finger) * 3 + (int)j];
    public float Abduction(int hand, int finger) => _abd[hand * F + finger];
    public float LateralDeviation(int hand, int finger, int hinge) => _lat[(hand * F + finger) * 2 + hinge];
    public bool Clamped(int hand, int finger, DigitJoint j) => _clampedJ[(hand * F + finger) * 3 + (int)j];
    public bool Reverted(int hand, int finger, DigitJoint j) => _revertedJ[(hand * F + finger) * 3 + (int)j];
    public static float FlexMin(int finger, DigitJoint j) => FlexLo[finger * 3 + (int)j];
    public static float FlexMax(int finger, DigitJoint j) => FlexHi[finger * 3 + (int)j];
    public static float AbductionMax(int finger) => AbdMax[finger];
    /// <summary>손가락 굴곡률 0..1(세 관절 굴곡 합 / 최대 합).</summary>
    public float FlexionRatio(int hand, int finger)
    {
        float s = 0, m = 0;
        for (var j = 0; j < 3; j++) { s += MathF.Max(0f, _flex[(hand * F + finger) * 3 + j]); m += FlexHi[finger * 3 + j]; }
        return Math.Clamp(s / m, 0f, 1f);
    }
    public int RomClamps { get; private set; }
    public int Dislocations { get; private set; }
    public int Collisions { get; private set; }
    public float MaxCollisionMm { get; private set; }
    public float ResidualCollisionMm { get; private set; }
    public float MaxLateralDeg { get; private set; }
    public long RomClampTotal { get; private set; }
    public long DislocationTotal { get; private set; }
    public long CollisionTotal { get; private set; }
    public double LastGuardUs { get; private set; }

    public void Reset() { Array.Clear(_hasValid); Array.Clear(_calib); RomClampTotal = DislocationTotal = CollisionTotal = 0; }

    // ── 처리 ────────────────────────────────────────────────

    /// <summary>입력 42 키포인트 → ROM 클램프 · 탈구 복귀 · 지간 충돌 분리(무할당).</summary>
    public void Process(ReadOnlySpan<Vector3> input, ReadOnlySpan<bool> valid)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        input.Slice(0, H * P).CopyTo(_out);
        int clamps = 0, disl = 0;
        var maxLat = 0f;
        Array.Clear(_clampedJ); Array.Clear(_revertedJ);
        Span<float> fl = stackalloc float[3];
        Span<bool> disloc = stackalloc bool[3];
        for (var h = 0; h < H; h++)
        {
            var o = h * P;
            if (!valid[o] || !valid[o + 5] || !valid[o + 9] || !valid[o + 17]) continue;
            if (!_calib[h])
            {
                var all = true;
                for (var p = 0; p < P; p++) if (!valid[o + p]) { all = false; break; }
                if (!all) continue;
                for (var p = 1; p < P; p++) _len[o + p] = Vector3.Distance(input[o + p], input[o + HandTopology.Parent[p]]);
                _calib[h] = true;
            }
            HandTopology.Frame(input, h, out var hx, out var hy, out _);
            for (var f = 0; f < F; f++)
            {
                var fb = o + HandTopology.Base(f);
                if (!valid[fb] || !valid[fb + 1] || !valid[fb + 2] || !valid[fb + 3]) continue;
                HandTopology.Decompose(input, o, h, f, out var fl0, out var ab, out var fl1, out var la1, out var fl2, out var la2);
                var fi = (h * F + f) * 3; var ai = h * F + f;
                fl0 -= _restFlex[fi]; fl1 -= _restFlex[fi + 1]; fl2 -= _restFlex[fi + 2];
                ab -= _restAbd[ai]; la1 -= _restLat[ai * 2]; la2 -= _restLat[ai * 2 + 1];
                _flexRaw[fi] = fl0; _flexRaw[fi + 1] = fl1; _flexRaw[fi + 2] = fl2;
                _lat[ai * 2] = la1; _lat[ai * 2 + 1] = la2;
                maxLat = MathF.Max(maxLat, MathF.Max(MathF.Abs(la1), MathF.Abs(la2)));
                var latMax = f == 0 ? ThumbLateralMax : HingeLateralMax;
                var b = o + HandTopology.Base(f);
                fl[0] = fl0; fl[1] = fl1; fl[2] = fl2;
                disloc[0] = false; disloc[1] = MathF.Abs(la1) > latMax; disloc[2] = MathF.Abs(la2) > latMax;
                for (var j = 0; j < 3; j++)
                {
                    var r = Vector3.Distance(input[b + j + 1], input[b + j]) / MathF.Max(1e-6f, _len[b + j + 1]);
                    if (r < LenLo || r > LenHi) disloc[j] = true;
                }
                var fingerBad = false;
                for (var j = 0; j < 3; j++)
                {
                    if (DislocationGuard && disloc[j] && _hasValid[h])
                    {
                        fl[j] = _validFlex[fi + j];
                        if (j == 0) ab = _validAbd[ai];
                        _revertedJ[fi + j] = true; disl++; fingerBad = true;
                        continue;
                    }
                    if (RomGuard)
                    {
                        var c = Math.Clamp(fl[j], FlexLo[f * 3 + j], FlexHi[f * 3 + j]);
                        if (c != fl[j]) { fl[j] = c; _clampedJ[fi + j] = true; clamps++; fingerBad = true; }
                    }
                }
                if (RomGuard && MathF.Abs(ab) > AbdMax[f]) { ab = Math.Clamp(ab, -AbdMax[f], AbdMax[f]); _clampedJ[fi] = true; clamps++; fingerBad = true; }
                _flex[fi] = fl[0]; _flex[fi + 1] = fl[1]; _flex[fi + 2] = fl[2]; _abd[ai] = ab;
                if (!fingerBad)
                {
                    _validFlex[fi] = fl[0]; _validFlex[fi + 1] = fl[1]; _validFlex[fi + 2] = fl[2]; _validAbd[ai] = ab;
                    for (var j = 1; j <= 3; j++) _len[b + j] += 0.05f * (Vector3.Distance(input[b + j], input[b + j - 1]) - _len[b + j]);
                    continue;
                }
                // 재구성: MCP 고정 → 가드 각 + 보정 길이
                var u = HandTopology.Dir(_out[b] - _out[o], hx);
                var y = HandTopology.Orth(hy, u, hy);
                var a = Vector3.Cross(y, u);
                var latTarget = 0f;
                for (var j = 0; j < 3; j++)
                {
                    float flexAbs = fl[j] + _restFlex[fi + j], side = j == 0 ? ab + _restAbd[ai] : latTarget + _restLat[ai * 2 + j - 1];
                    var v = HandTopology.Compose(u, y, a, flexAbs, side);
                    _out[b + j + 1] = _out[b + j] + v * _len[b + j + 1];
                    u = v; a = HandTopology.Orth(a, u, a); y = Vector3.Cross(u, a);
                }
            }
            _hasValid[h] = true;
        }
        RomClamps = clamps; Dislocations = disl; MaxLateralDeg = maxLat;
        RomClampTotal += clamps; DislocationTotal += disl;

        // 지간 자가 충돌
        var depth0 = 0f;
        var cols = 0;
        for (var h = 0; h < H; h++) depth0 = MathF.Max(depth0, Collide(h, false, ref cols));
        MaxCollisionMm = depth0 * 1000f;
        if (CollisionGuard && depth0 > 0)
            for (var pass = 0; pass < 8; pass++)
            {
                var d = 0f; var n = 0;
                for (var h = 0; h < H; h++) d = MathF.Max(d, Collide(h, true, ref n));
                if (d <= 0) break;
            }
        var tmp = 0;
        var res = 0f;
        for (var h = 0; h < H; h++) res = MathF.Max(res, Collide(h, false, ref tmp));
        ResidualCollisionMm = res * 1000f;
        Collisions = cols;
        CollisionTotal += cols > 0 ? 1 : 0;
        LastGuardUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private float Radius(int h) => _calib[h] ? PhalanxRadius * Math.Clamp(_len[h * P + 9] / 0.0902f, 0.5f, 2f) : PhalanxRadius;

    /// <summary>손가락 원위 이동(자손 동반) 후 체인 길이 재정규화.</summary>
    private void Shift(int h, int f, int fromJoint, Vector3 delta)
    {
        var b = h * P + HandTopology.Base(f);
        for (var j = fromJoint; j <= 3; j++) _out[b + j] += delta;
        for (var j = 1; j <= 3; j++)
        {
            var d = _out[b + j] - _out[b + j - 1];
            var l = d.Length();
            if (l < 1e-6f) continue;
            var fix = _out[b + j - 1] + d * (_len[b + j] / l) - _out[b + j];
            for (var k = j; k <= 3; k++) _out[b + k] += fix;
        }
    }

    private float Collide(int h, bool resolve, ref int count)
    {
        var r2 = 2f * Radius(h);
        var o = h * P;
        var max = 0f;
        HandTopology.Frame(_out, h, out _, out _, out var hz);
        for (var pi = 0; pi < _pairs.Length; pi += 2)
        {
            int fa = _pairs[pi], fb = _pairs[pi + 1];
            int ba = o + HandTopology.Base(fa), bb = o + HandTopology.Base(fb);
            for (var sa = 0; sa < 3; sa++)
                for (var sb = 0; sb < 3; sb++)
                {
                    if (fa == 0 && sa == 0 && sb == 0) continue;   // 엄지 CMC ↔ 검지 MCP 근위는 손바닥 연부
                    var d2 = KinematicJointLimitBonePenetrationGuard.SegmentSegmentClosest(_out[ba + sa], _out[ba + sa + 1], _out[bb + sb], _out[bb + sb + 1], out var s, out var t);
                    var d = MathF.Sqrt(d2);
                    if (d >= r2) continue;
                    var pen = r2 - d;
                    max = MathF.Max(max, pen);
                    if (!resolve) { count++; continue; }
                    var pa = Vector3.Lerp(_out[ba + sa], _out[ba + sa + 1], s); var pb = Vector3.Lerp(_out[bb + sb], _out[bb + sb + 1], t);
                    var sep = pa - pb;
                    // 손가락 a 는 b 보다 엄지 쪽(+z). 교차(측방 성분 음수)·퇴화 시 측방으로 교차 깊이까지 분리
                    var shz = Vector3.Dot(sep, hz);
                    if (sep.LengthSquared() < 1e-10f || shz < 0) { sep = hz; pen = r2 - MathF.Min(0f, shz); }
                    else sep = HandTopology.Dir(sep, hz);
                    var push = (pen + 0.0005f) * 0.5f;
                    Shift(h, fa, sa + 1, sep * (push / MathF.Max(0.35f, s)));
                    Shift(h, fb, sb + 1, -sep * (push / MathF.Max(0.35f, t)));
                }
        }
        return max;
    }
}
