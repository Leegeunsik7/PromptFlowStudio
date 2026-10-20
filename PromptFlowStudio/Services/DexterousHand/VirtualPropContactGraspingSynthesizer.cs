using System.Numerics;

namespace PromptFlowStudio.Services.DexterousHand;

public enum PropKind : byte { Sword = 0, PistolGrip, Hammer, Ball }

public enum SdfPrimitive : byte { None = 0, Capsule, RoundBox, Sphere }

/// <summary>소품 로컬 SDF 원시(캡슐 A–B · 라운드 박스 중심 A 반폭 B · 구 중심 A) · R = 반경/라운딩.</summary>
public readonly record struct PropShape(SdfPrimitive Kind, Vector3 A, Vector3 B, float R);

/// <summary>
/// [PromptFlow Studio v17.13.0] 🗡️ 동적 가상 소품 파지 &amp; 접촉 물리 합성기(<see cref="Process"/> 무할당).
///   ▸ 소품 = OpenUSD 프림(경로 · 로컬 Z = 손잡이 축) · 해석 SDF 원시 합성(최소 거리) · 법선 = SDF 중앙 차분 기울기.
///   ▸ 접촉 매니폴드: 손 42 키포인트 + 마디 중점 30 의 SDF 거리 − 지골 반경 &lt; 2 mm → 접촉점(표면 투영) · 법선 · 관통 깊이 · 접촉력 F = k·관통(측정 의도 그립) + 예압.
///   ▸ 관통 락: 관통 키포인트를 법선 방향 표면 밖으로 투영 · 스티키 락(소품 로컬 좌표 고정 · 이탈 8 mm 또는 미끄럼 12 mm 초과 시 재설정) → 손가락 체인 뼈 길이 재정규화 ↔ 재투영(5 패스) → 최종 투영.
///   ▸ 파지 상태기: 접촉 손가락 ≥ 3 → 소품을 손 좌표계에 부착(상대 변환 고정 · 손 이동/회전 추종) · 접촉 손가락 &lt; 2 가 150 ms 지속 → 해제(손 속도 인계 · 중력 낙하 → 거치대).
///   ▸ 파지 품질 = 1 − |Σ Fᵢ nᵢ| / Σ Fᵢ(힘 평형 근사) · 접촉력 히트맵(손잡이 원통 좌표 32 × 16 · 가우스 스플랫 · 감쇠).
/// </summary>
public sealed class VirtualPropContactGraspingSynthesizer
{
    public const int J = HandTopology.Keypoints, Mid = HandTopology.Hands * 15, HeatU = 32, HeatV = 16, MaxShapes = 3;
    public const float FingerRadius = 0.006f, ContactTol = 0.002f, LockRelease = 0.008f, SlipLimit = 0.012f, Stiffness = 900f, Preload = 0.4f, Gravity = 9.81f;

    private readonly PropShape[] _shapes = new PropShape[MaxShapes];
    private readonly Vector3[] _out = new Vector3[J], _lockLocal = new Vector3[J], _cPoint = new Vector3[J], _cNormal = new Vector3[J];
    private readonly bool[] _contact = new bool[J], _locked = new bool[J];
    private readonly float[] _force = new float[J], _depth = new float[J], _len = new float[J];
    private readonly float[] _heat = new float[HeatU * HeatV];
    private readonly float[] _fingerForce = new float[HandTopology.Hands * HandTopology.Fingers];
    private Quaternion _attachRot = Quaternion.Identity;
    private Vector3 _attachPos, _vel;
    private float _releaseTimer;

    public VirtualPropContactGraspingSynthesizer() => SetProp(PropKind.Sword);

    public PropKind Kind { get; private set; }
    public string PrimPath { get; private set; } = "/World/Props/Sword";
    public string PropName { get; private set; } = "검";
    public int ShapeCount { get; private set; }
    public PropShape Shape(int i) => _shapes[i];
    public float HeatZMin { get; private set; }
    public float HeatZMax { get; private set; }
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; } = Quaternion.Identity;
    public Vector3 RestPosition { get; set; }
    public Quaternion RestRotation { get; set; } = Quaternion.Identity;
    public bool ContactLock { get; set; } = true;
    public bool AutoAttach { get; set; } = true;
    public int GraspHand { get; set; } = 1;
    public bool Attached { get; private set; }
    public bool Falling { get; private set; }
    public ReadOnlySpan<Vector3> Output => _out;
    public Vector3 Out(int k) => _out[k];
    public bool Contact(int k) => _contact[k];
    public float Force(int k) => _force[k];
    public Vector3 ContactPoint(int k) => _cPoint[k];
    public Vector3 ContactNormal(int k) => _cNormal[k];
    public float FingerForce(int hand, int finger) => _fingerForce[hand * HandTopology.Fingers + finger];
    public float Heat(int u, int v) => _heat[v * HeatU + u];
    public float HeatMax { get; private set; }
    public int Contacts { get; private set; }
    public int ContactFingers { get; private set; }
    public bool ThumbOpposition { get; private set; }
    public float TotalForceN { get; private set; }
    public float GraspQuality { get; private set; }
    public float RawPenetrationMm { get; private set; }
    public float ResidualPenetrationMm { get; private set; }
    public float BoneLengthErrorMm { get; private set; }
    public int Slips { get; private set; }
    public long Grasps { get; private set; }
    public long Releases { get; private set; }
    public double LastContactUs { get; private set; }

    public static string Name(PropKind k) => k switch { PropKind.PistolGrip => "권총 그립", PropKind.Hammer => "망치", PropKind.Ball => "공", _ => "검" };

    /// <summary>소품 교체(제어 평면 · 접촉/부착 초기화).</summary>
    public void SetProp(PropKind k)
    {
        Kind = k;
        Array.Clear(_shapes);
        switch (k)
        {
            case PropKind.PistolGrip:
                _shapes[0] = new PropShape(SdfPrimitive.RoundBox, Vector3.Zero, new Vector3(0.012f, 0.02f, 0.05f), 0.006f);
                _shapes[1] = new PropShape(SdfPrimitive.RoundBox, new Vector3(0.09f, 0.0f, 0.07f), new Vector3(0.1f, 0.012f, 0.016f), 0.003f);
                ShapeCount = 2; HeatZMin = -0.06f; HeatZMax = 0.06f; PrimPath = "/World/Props/PistolGrip"; break;
            case PropKind.Hammer:
                _shapes[0] = new PropShape(SdfPrimitive.Capsule, new Vector3(0, 0, -0.12f), new Vector3(0, 0, 0.12f), 0.016f);
                _shapes[1] = new PropShape(SdfPrimitive.RoundBox, new Vector3(0, 0, 0.15f), new Vector3(0.06f, 0.018f, 0.018f), 0.003f);
                ShapeCount = 2; HeatZMin = -0.12f; HeatZMax = 0.12f; PrimPath = "/World/Props/Hammer"; break;
            case PropKind.Ball:
                _shapes[0] = new PropShape(SdfPrimitive.Sphere, Vector3.Zero, Vector3.Zero, 0.04f);
                ShapeCount = 1; HeatZMin = -0.045f; HeatZMax = 0.045f; PrimPath = "/World/Props/Ball"; break;
            default:
                _shapes[0] = new PropShape(SdfPrimitive.Capsule, new Vector3(0, 0, -0.07f), new Vector3(0, 0, 0.07f), 0.017f);
                _shapes[1] = new PropShape(SdfPrimitive.RoundBox, new Vector3(0, 0, 0.085f), new Vector3(0.05f, 0.012f, 0.008f), 0.002f);
                _shapes[2] = new PropShape(SdfPrimitive.RoundBox, new Vector3(0, 0, 0.45f), new Vector3(0.022f, 0.003f, 0.36f), 0.001f);
                ShapeCount = 3; HeatZMin = -0.08f; HeatZMax = 0.08f; PrimPath = "/World/Props/Sword"; break;
        }
        PropName = Name(k);
        Detach(false);
        Array.Clear(_heat);
        Position = RestPosition; Rotation = RestRotation;
    }

    private void Detach(bool count)
    {
        if (Attached && count) Releases++;
        Attached = false;
        Array.Clear(_locked);
        _releaseTimer = 0;
    }

    // ── SDF ─────────────────────────────────────────────────

    public static float ShapeDistance(in PropShape s, Vector3 p)
    {
        switch (s.Kind)
        {
            case SdfPrimitive.Capsule:
            {
                var ab = s.B - s.A;
                var t = Math.Clamp(Vector3.Dot(p - s.A, ab) / MathF.Max(1e-12f, ab.LengthSquared()), 0f, 1f);
                return Vector3.Distance(p, s.A + ab * t) - s.R;
            }
            case SdfPrimitive.RoundBox:
            {
                var q = Vector3.Abs(p - s.A) - s.B;
                return Vector3.Max(q, Vector3.Zero).Length() + MathF.Min(MathF.Max(q.X, MathF.Max(q.Y, q.Z)), 0f) - s.R;
            }
            case SdfPrimitive.Sphere: return Vector3.Distance(p, s.A) - s.R;
            default: return float.MaxValue;
        }
    }

    /// <summary>소품 로컬 SDF(m).</summary>
    public float Sdf(Vector3 local)
    {
        var d = float.MaxValue;
        for (var i = 0; i < ShapeCount; i++) d = MathF.Min(d, ShapeDistance(_shapes[i], local));
        return d;
    }

    public Vector3 SdfNormal(Vector3 local)
    {
        const float e = 1e-4f;
        var n = new Vector3(Sdf(local + new Vector3(e, 0, 0)) - Sdf(local - new Vector3(e, 0, 0)),
                            Sdf(local + new Vector3(0, e, 0)) - Sdf(local - new Vector3(0, e, 0)),
                            Sdf(local + new Vector3(0, 0, e)) - Sdf(local - new Vector3(0, 0, e)));
        var l = n.Length();
        return l > 1e-12f ? n / l : Vector3.UnitY;
    }

    public Vector3 ToLocal(Vector3 w) => Vector3.Transform(w - Position, Quaternion.Conjugate(Rotation));
    public Vector3 ToWorld(Vector3 l) => Position + Vector3.Transform(l, Rotation);

    /// <summary>월드 점 → 지골 반경 고려 부호 거리(m).</summary>
    public float Distance(Vector3 world) => Sdf(ToLocal(world)) - FingerRadius;

    // ── 손 좌표계 회전(오른손 좌표계 보장) ───────────────────

    private static Quaternion HandRotation(ReadOnlySpan<Vector3> p, int hand, out Vector3 origin)
    {
        HandTopology.Frame(p, hand, out var x, out _, out var z);
        var y = Vector3.Cross(z, x);
        origin = p[hand * HandTopology.Points];
        var m = new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    /// <summary>손 좌표계 기준 소품 배치(파지 위치 · 로컬 오프셋 · 소품 Z = 손 측방).</summary>
    public static void GripPose(ReadOnlySpan<Vector3> hand42, int hand, Vector3 localOffset, out Vector3 pos, out Quaternion rot)
    {
        rot = HandRotation(hand42, hand, out var o);
        HandTopology.Frame(hand42, hand, out var x, out var y, out var z);
        pos = o + x * localOffset.X + y * localOffset.Y + z * localOffset.Z;
    }

    // ── 처리 ────────────────────────────────────────────────

    /// <summary>입력 42(가드 출력) → 소품 자세 갱신 → 접촉 매니폴드 · 관통 락 · 파지 상태 · 히트맵(무할당).</summary>
    public void Process(ReadOnlySpan<Vector3> input, ReadOnlySpan<bool> valid, float dt)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        input.Slice(0, J).CopyTo(_out);
        for (var k = 0; k < J; k++)
        {
            var pp = HandTopology.Parent[k % HandTopology.Points];
            _len[k] = pp < 0 ? 0f : Vector3.Distance(input[k], input[k - k % HandTopology.Points + pp]);
        }
        var gh = GraspHand;
        var handOk = valid[gh * HandTopology.Points] && valid[gh * HandTopology.Points + 5] && valid[gh * HandTopology.Points + 9] && valid[gh * HandTopology.Points + 17];

        // ① 소품 자세: 부착 = 손 추종 · 해제 = 중력 낙하 → 거치대
        if (Attached && handOk)
        {
            var hr = HandRotation(input, gh, out var ho);
            Rotation = Quaternion.Normalize(hr * _attachRot);
            var np = ho + Vector3.Transform(_attachPos, hr);
            if (dt > 0) _vel = (np - Position) / dt;
            Position = np;
        }
        else if (!Attached && Falling)
        {
            _vel.Y -= Gravity * dt;
            Position += _vel * dt;
            Rotation = Quaternion.Normalize(Quaternion.Slerp(Rotation, RestRotation, 1f - MathF.Exp(-dt / 0.2f)));
            if (Position.Y <= RestPosition.Y)
            {
                Position = RestPosition; Rotation = RestRotation; _vel = Vector3.Zero; Falling = false;
            }
        }

        // ② 원시 관통(측정) · 접촉 판정
        var rawPen = 0f;
        Array.Clear(_force);
        for (var k = 0; k < J; k++)
        {
            _contact[k] = false;
            if (!valid[k] || k % HandTopology.Points == 0) { _locked[k] = false; continue; }
            var lp = ToLocal(input[k]);
            var d = Sdf(lp) - FingerRadius;
            _depth[k] = d;
            rawPen = MathF.Max(rawPen, -d);
            if (d >= ContactTol) { _locked[k] = false; continue; }
            _contact[k] = true;
            _force[k] = Preload + Stiffness * MathF.Max(0f, -d);
        }
        RawPenetrationMm = rawPen * 1000f;

        // ③ 관통 락: 투영 + 스티키 → 체인 재정규화 ↔ 재투영
        var slips = 0;
        for (var pass = 0; pass < 5; pass++)
        {
            for (var k = 0; k < J; k++)
            {
                if (!_contact[k]) continue;
                var lp = ToLocal(_out[k]);
                var d = Sdf(lp) - FingerRadius;
                var n = SdfNormal(lp);
                var surf = d < 0 ? lp - n * d : lp;
                if (pass == 0 && ContactLock)
                {
                    if (_locked[k])
                    {
                        if (Vector3.Distance(surf, _lockLocal[k]) > SlipLimit || d > LockRelease) { _lockLocal[k] = surf; slips++; }
                        else surf = _lockLocal[k];
                    }
                    else { _lockLocal[k] = surf; _locked[k] = true; }
                }
                _out[k] = ToWorld(surf);
                _cPoint[k] = ToWorld(surf - n * FingerRadius);
                _cNormal[k] = Vector3.Normalize(Vector3.Transform(n, Rotation));
            }
            // 마디 중점(원위 관절 이동)
            for (var h = 0; h < HandTopology.Hands; h++)
                for (var p = 1; p < HandTopology.Points; p++)
                {
                    int k = h * HandTopology.Points + p, pk = h * HandTopology.Points + HandTopology.Parent[p];
                    if (HandTopology.Parent[p] == 0 || !valid[k]) continue;
                    var mid = 0.5f * (_out[k] + _out[pk]);
                    var lm = ToLocal(mid);
                    var d = Sdf(lm) - FingerRadius;
                    if (d >= 0) continue;
                    var nw = Vector3.Transform(SdfNormal(lm), Rotation);
                    _out[k] -= nw * (2f * d);
                    _contact[k] = true;
                    _force[k] = MathF.Max(_force[k], Preload + Stiffness * -d);
                    _cNormal[k] = Vector3.Normalize(nw);
                    _cPoint[k] = _out[k] - nw * FingerRadius;
                }
            // 뼈 길이 재정규화(손목 → 손끝)
            for (var h = 0; h < HandTopology.Hands; h++)
                for (var p = 1; p < HandTopology.Points; p++)
                {
                    var pp = HandTopology.Parent[p];
                    if (pp == 0) continue;
                    int k = h * HandTopology.Points + p, pk = h * HandTopology.Points + pp;
                    var dv = _out[k] - _out[pk];
                    var l = dv.Length();
                    if (l > 1e-6f) _out[k] = _out[pk] + dv * (_len[k] / l);
                }
        }
        Slips = slips;
        // 최종 투영(재정규화 없음 · 잔여 관통 0 우선 · 뼈 길이 오차는 지표로 보고)
        for (var k = 0; k < J; k++)
        {
            if (!valid[k] || k % HandTopology.Points == 0) continue;
            var lp = ToLocal(_out[k]);
            var d = Sdf(lp) - FingerRadius;
            if (d < 0) _out[k] = ToWorld(lp - SdfNormal(lp) * d);
        }

        // ④ 지표 · 파지 상태
        float res = 0, berr = 0, tot = 0;
        var net = Vector3.Zero;
        var cn = 0;
        Array.Clear(_fingerForce);
        for (var k = 0; k < J; k++)
        {
            var p = k % HandTopology.Points;
            if (valid[k] && p != 0) res = MathF.Max(res, -Distance(_out[k]));
            if (p != 0 && valid[k]) berr = MathF.Max(berr, MathF.Abs(Vector3.Distance(_out[k], _out[k - p + HandTopology.Parent[p]]) - _len[k]));
            if (!_contact[k]) continue;
            cn++;
            tot += _force[k];
            net += _force[k] * _cNormal[k];
            _fingerForce[(k / HandTopology.Points) * HandTopology.Fingers + HandTopology.FingerOf(p)] += _force[k];
        }
        ResidualPenetrationMm = MathF.Max(0f, res) * 1000f;
        BoneLengthErrorMm = berr * 1000f;
        Contacts = cn; TotalForceN = tot;
        GraspQuality = tot > 1e-6f ? 1f - net.Length() / tot : 0f;
        var fingers = 0;
        for (var f = 0; f < HandTopology.Fingers; f++) if (_fingerForce[gh * HandTopology.Fingers + f] > 0) fingers++;
        ContactFingers = fingers;
        ThumbOpposition = _fingerForce[gh * HandTopology.Fingers] > 0 && fingers >= 2;
        if (AutoAttach && handOk)
        {
            if (!Attached && fingers >= 3)
            {
                var hr = HandRotation(input, gh, out var ho);
                var inv = Quaternion.Conjugate(hr);
                _attachRot = Quaternion.Normalize(inv * Rotation);
                _attachPos = Vector3.Transform(Position - ho, inv);
                Attached = true; Falling = false; Grasps++; _releaseTimer = 0;
            }
            else if (Attached)
            {
                _releaseTimer = fingers < 2 ? _releaseTimer + dt : 0f;
                if (_releaseTimer > 0.15f) { Detach(true); Falling = true; }
            }
        }

        // ⑤ 히트맵(손잡이 원통 좌표)
        var hm = 0f;
        for (var i = 0; i < _heat.Length; i++) { _heat[i] *= 0.92f; hm = MathF.Max(hm, _heat[i]); }
        var cx = _shapes[0].Kind == SdfPrimitive.Capsule ? 0.5f * (_shapes[0].A + _shapes[0].B) : _shapes[0].A;
        for (var k = 0; k < J; k++)
        {
            if (!_contact[k]) continue;
            var lp = ToLocal(_cPoint[k]) - cx;
            var u = (MathF.Atan2(lp.Y, lp.X) / MathF.Tau + 0.5f) * HeatU;
            var v = (lp.Z + cx.Z - HeatZMin) / (HeatZMax - HeatZMin) * HeatV;
            if (v < -1 || v > HeatV + 1) continue;
            for (var dv = -2; dv <= 2; dv++)
                for (var du = -2; du <= 2; du++)
                {
                    int iu = ((int)MathF.Floor(u) + du + HeatU) % HeatU, iv = (int)MathF.Floor(v) + dv;
                    if (iv < 0 || iv >= HeatV) continue;
                    float ddu = iu + 0.5f - u, ddv = iv + 0.5f - v;
                    if (ddu > HeatU / 2) ddu -= HeatU; else if (ddu < -HeatU / 2) ddu += HeatU;
                    var add = 0.08f * _force[k] * MathF.Exp(-(ddu * ddu + ddv * ddv) / 1.5f);
                    var hv = _heat[iv * HeatU + iu] += add;
                    hm = MathF.Max(hm, hv);
                }
        }
        HeatMax = hm;
        LastContactUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    public void ResetProp()
    {
        Detach(false);
        Falling = false; _vel = Vector3.Zero;
        Position = RestPosition; Rotation = RestRotation;
    }
}
