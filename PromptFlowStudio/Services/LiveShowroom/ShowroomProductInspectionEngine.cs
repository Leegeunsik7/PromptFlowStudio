using System.Numerics;
using PromptFlowStudio.Services.BroadcastMcr;
using PromptFlowStudio.Services.NeuralRagdoll;
using PromptFlowStudio.Services.SkinnedSplatAvatar;
using PromptFlowStudio.Services.SplatEnvironment;

namespace PromptFlowStudio.Services.LiveShowroom;

public enum ProductShape : byte { Perfume, Smartphone, Lipstick, Handbag, Sneaker }
public enum HoldPose : byte { Rest, Hold, Showcase, Clap, ThumbsUp }

/// <summary>v17.68.0 3DGS 상품 카탈로그 항목(가격 · 재고 · 형상 · 재질).</summary>
public sealed record ShowroomProduct(int Id, string Name, ProductShape Shape, long ListPrice, long SalePrice, int DisplayedPercent, int Stock, Vector3 Color, float Gloss, float Metallic)
{
    public int Splats { get; init; } = 20_000;
}

/// <summary>
/// [PromptFlow Studio v17.68.0] 3DGS 가상 상품 핸드헬드 &amp; 360° 인스펙션 엔진(60 fps · 무할당).
///   ▸ 모델: 형상별 절차 생성(향수병 · 스마트폰 · 립스틱 · 핸드백 · 스니커즈) 로컬 SoA(위치 · 법선 · 접선 · 크기 · 알베도) — 최대 5만 스플랫 · 형상 변경 시만 재생성.
///   ▸ 앵커: 호스트 v17.57 골격 R 손목(전역 회전) × 손바닥 오프셋 = 로컬 → 월드 변환 행렬. 팔 포즈(들기 · 쇼케이스 · 박수 · 엄지척)는 어깨/팔꿈치 로컬 회전 해석해(FromTo)로 컴포지터에 주입.
///   ▸ 쇼케이스: 상품을 가슴 앞으로 들어 yaw 0.9 rad/s · pitch 0.35 sin 절차 회전 → 매 프레임 카메라 기준 Blinn-Phong 광택(Gloss 지수 · 금속성 = 스페큘러 알베도 착색) · 프레넬 림 → v17.66 상품 슬롯 기록.
///   ▸ 쇼케이스 진입 시 v17.66 MCR 인서트 캠을 상품으로 재지정 → 상품 클로즈업 컷 요청(호출자 결선).
/// </summary>
public sealed class ShowroomProductInspectionEngine
{
    public const int MaxSplats = UnifiedGaussianSceneCompositorEngine.ProductSplats;
    public const float SpinRate = 0.9f, PalmOffset = 0.07f;

    private readonly Vector3[] _lp = new Vector3[MaxSplats], _ln = new Vector3[MaxSplats], _lt = new Vector3[MaxSplats], _ls = new Vector3[MaxSplats], _alb = new Vector3[MaxSplats];
    private int _n, _modelId = -1;
    private ShowroomProduct? _product;
    private float _yaw, _pitch, _t, _blend, _poseT;
    private Quaternion _pLS = Quaternion.Identity, _pLE = Quaternion.Identity, _pRS = Quaternion.Identity, _pRE = Quaternion.Identity;
    private Matrix4x4 _world = Matrix4x4.Identity;
    private Vector3 _center;

    public ShowroomProduct? Product => _product;
    public int SplatCount => _n;
    public HoldPose Pose { get; private set; } = HoldPose.Rest;
    public bool Showcase => Pose == HoldPose.Showcase;
    public int Avatar { get; set; }
    public Vector3 Center => _center;
    public float Yaw => _yaw;
    public float Pitch => _pitch;
    public long Frames { get; private set; }
    public long ModelBuilds { get; private set; }
    public float PeakSpecular { get; private set; }
    public float AnchorErrorMm { get; private set; }
    public double LastUs { get; private set; }
    public Matrix4x4 WorldMatrix => _world;

    public static string ShapeName(ProductShape s) => s switch { ProductShape.Perfume => "향수", ProductShape.Smartphone => "스마트폰", ProductShape.Lipstick => "립스틱", ProductShape.Handbag => "핸드백", _ => "스니커즈" };
    public static string PoseName(HoldPose p) => p switch { HoldPose.Hold => "들기", HoldPose.Showcase => "360° 쇼케이스", HoldPose.Clap => "박수", HoldPose.ThumbsUp => "엄지척", _ => "휴식" };

    // ══════════════════════ 모델(비핫패스 · 형상 변경 시만) ══════════════════════
    public void SetProduct(ShowroomProduct? p)
    {
        _product = p;
        if (p is null) { _n = 0; _modelId = -1; return; }
        if (p.Id == _modelId) return;
        Build(p);
        _modelId = p.Id; ModelBuilds++;
    }

    private void Build(ShowroomProduct p)
    {
        var n = Math.Clamp(p.Splats, 1000, MaxSplats);
        var rng = 0xC0FFEEu + (uint)p.Id * 7919u;
        float U() { rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5; return (rng & 0xFFFFFF) / 16777216f; }
        var accent = new Vector3(0.95f, 0.85f, 0.55f);
        for (var k = 0; k < n; k++)
        {
            Vector3 pos, nrm; var col = p.Color; var s = 0.0028f;
            switch (p.Shape)
            {
                case ProductShape.Perfume:
                {
                    // 몸체 6×9×3.5 cm 박스 + 캡 원통(r 1.4 · h 2.5)
                    if (k < n * 4 / 5) BoxPoint(new Vector3(0.03f, 0.045f, 0.0175f), U, out pos, out nrm);
                    else { CylPoint(0.014f, 0.025f, U, out pos, out nrm); pos.Y += 0.045f; col = accent; }
                    break;
                }
                case ProductShape.Smartphone:
                {
                    BoxPoint(new Vector3(0.036f, 0.075f, 0.004f), U, out pos, out nrm);
                    if (nrm.Z > 0.9f && MathF.Abs(pos.X) < 0.033f && MathF.Abs(pos.Y) < 0.07f) col = new Vector3(0.03f, 0.04f, 0.08f) + 0.15f * new Vector3(0.2f, 0.4f, 0.9f) * (0.5f + 0.5f * pos.Y / 0.07f);
                    s = 0.0022f;
                    break;
                }
                case ProductShape.Lipstick:
                {
                    if (k < n / 2) { CylPoint(0.0105f, 0.05f, U, out pos, out nrm); col = accent; }
                    else { CylPoint(0.0075f, 0.025f, U, out pos, out nrm); pos.Y += 0.05f; if (pos.Y > 0.07f) pos.Y -= (pos.Y - 0.07f) * (0.6f + 0.4f * MathF.Abs(pos.X) / 0.0075f); }
                    s = 0.0018f;
                    break;
                }
                case ProductShape.Handbag:
                {
                    if (k < n * 4 / 5) BoxPoint(new Vector3(0.12f, 0.08f, 0.05f), U, out pos, out nrm);
                    else
                    {
                        var a = MathF.PI * U(); var r = 0.007f; var b = MathF.Tau * U();
                        var c = new Vector3(0.07f * MathF.Cos(a), 0.08f + 0.07f * MathF.Sin(a), 0f);
                        var tdir = new Vector3(-MathF.Sin(a), MathF.Cos(a), 0f); var side = Vector3.Cross(tdir, Vector3.UnitZ);
                        nrm = MathF.Cos(b) * side + MathF.Sin(b) * Vector3.UnitZ; pos = c + nrm * r; col = accent;
                    }
                    s = 0.0035f;
                    break;
                }
                default:
                {
                    // 스니커즈: 밑창(납작 박스) + 갑피(반타원체)
                    if (k < n / 3) { BoxPoint(new Vector3(0.05f, 0.012f, 0.14f), U, out pos, out nrm); col = new Vector3(0.95f, 0.95f, 0.95f); }
                    else
                    {
                        var u = MathF.Tau * U(); var v = MathF.Acos(U());
                        nrm = new Vector3(MathF.Sin(v) * MathF.Cos(u), MathF.Cos(v), MathF.Sin(v) * MathF.Sin(u));
                        pos = new Vector3(nrm.X * 0.045f, 0.012f + nrm.Y * 0.06f, nrm.Z * 0.13f);
                        nrm = Vector3.Normalize(new Vector3(nrm.X / 0.045f, nrm.Y / 0.06f, nrm.Z / 0.13f));
                    }
                    s = 0.0035f;
                    break;
                }
            }
            var t = Vector3.Normalize(Vector3.Cross(nrm, MathF.Abs(nrm.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY));
            _lp[k] = pos; _ln[k] = nrm; _lt[k] = t; _ls[k] = new Vector3(s, s, s * 0.25f); _alb[k] = col * (0.92f + 0.08f * U());
        }
        _n = n;

        static void BoxPoint(Vector3 h, Func<float> u, out Vector3 p, out Vector3 n)
        {
            float ax = h.Y * h.Z, ay = h.X * h.Z, az = h.X * h.Y, tot = ax + ay + az;
            var r = u() * tot; var sg = u() < 0.5f ? -1f : 1f;
            float a = u() * 2f - 1f, b = u() * 2f - 1f;
            if (r < ax) { p = new Vector3(sg * h.X, a * h.Y, b * h.Z); n = new Vector3(sg, 0f, 0f); }
            else if (r < ax + ay) { p = new Vector3(a * h.X, sg * h.Y, b * h.Z); n = new Vector3(0f, sg, 0f); }
            else { p = new Vector3(a * h.X, b * h.Y, sg * h.Z); n = new Vector3(0f, 0f, sg); }
        }

        static void CylPoint(float r, float h, Func<float> u, out Vector3 p, out Vector3 n)
        {
            var a = MathF.Tau * u();
            if (u() < 0.15f) { var rr = r * MathF.Sqrt(u()); p = new Vector3(rr * MathF.Cos(a), h, rr * MathF.Sin(a)); n = Vector3.UnitY; return; }
            p = new Vector3(r * MathF.Cos(a), h * u(), r * MathF.Sin(a)); n = new Vector3(MathF.Cos(a), 0f, MathF.Sin(a));
        }
    }

    // ══════════════════════ 포즈(무할당) ══════════════════════
    public void SetPose(HoldPose p) { if (p != Pose) { Pose = p; _poseT = 0f; } }

    /// <summary>어깨 → 팔꿈치 · 팔꿈치 → 손목 목표 방향(몸 기준) → 로컬 회전 해석해.</summary>
    private static void Solve(Vector3 restUpper, Vector3 restFore, Vector3 upper, Vector3 fore, out Quaternion sh, out Quaternion el)
    {
        sh = GaussianAvatarRig.FromTo(Vector3.Normalize(restUpper), Vector3.Normalize(upper));
        var f = Vector3.Transform(Vector3.Normalize(fore), Quaternion.Inverse(sh));
        el = GaussianAvatarRig.FromTo(Vector3.Normalize(restFore), f);
    }

    private void Arms(UnifiedGaussianSceneCompositorEngine comp, float dt)
    {
        var rest = RagdollSkeleton.Rest;
        Vector3 F = comp.BodyForward, Up = comp.BodyUp, R = comp.BodyRight;
        var lU = rest[(int)RagdollJoint.LElbow] - rest[(int)RagdollJoint.LShoulder]; var lF = rest[(int)RagdollJoint.LWrist] - rest[(int)RagdollJoint.LElbow];
        var rU = rest[(int)RagdollJoint.RElbow] - rest[(int)RagdollJoint.RShoulder]; var rF = rest[(int)RagdollJoint.RWrist] - rest[(int)RagdollJoint.RElbow];
        // 오른쪽(+R) 이 R 어깨 쪽인지 휴식 자세로 판정
        var rSide = Vector3.Dot(rest[(int)RagdollJoint.RShoulder] - rest[(int)RagdollJoint.LShoulder], R) < 0f ? -1f : 1f;
        var side = R * rSide;
        Vector3 du = -Up, df = -Up;   // 오른팔 기본(내림)
        Vector3 lu = -Up, lf = -Up;
        _poseT += dt;
        switch (Pose)
        {
            case HoldPose.Hold: du = -Up * 0.92f + F * 0.25f + side * 0.1f; df = F * 0.9f + Up * 0.15f - side * 0.25f; break;
            case HoldPose.Showcase: du = -Up * 0.75f + F * 0.55f + side * 0.05f; df = F * 0.55f + Up * 0.75f - side * 0.45f; break;
            case HoldPose.Clap:
            {
                var c = 0.5f + 0.5f * MathF.Sin(_poseT * MathF.Tau * 2.5f);
                du = -Up * 0.8f + F * 0.5f; df = F * 0.7f + Up * 0.45f - side * (0.35f + 0.25f * c);
                lu = -Up * 0.8f + F * 0.5f; lf = F * 0.7f + Up * 0.45f + side * (0.35f + 0.25f * c);
                break;
            }
            case HoldPose.ThumbsUp: du = -Up * 0.92f + F * 0.25f + side * 0.1f; df = F * 0.9f + Up * 0.15f - side * 0.25f; lu = -Up * 0.55f + F * 0.6f - side * 0.25f; lf = Up * 0.95f + F * 0.25f; break;
        }
        Solve(lU, lF, lu, lf, out var ls, out var le);
        Solve(rU, rF, du, df, out var rs, out var re);
        var k = 1f - MathF.Exp(-dt / 0.12f);
        _pLS = Quaternion.Slerp(_pLS, ls, k); _pLE = Quaternion.Slerp(_pLE, le, k); _pRS = Quaternion.Slerp(_pRS, rs, k); _pRE = Quaternion.Slerp(_pRE, re, k);
        comp.SetArmPose(Avatar, _pLS, _pLE, _pRS, _pRE);
    }

    /// <summary>컴포지터 갱신 전 — 팔 포즈 주입(무할당).</summary>
    public void PreUpdate(UnifiedGaussianSceneCompositorEngine comp, float dt) => Arms(comp, dt);

    // ══════════════════════ 앵커 · 광택 기록(컴포지터 갱신 후 · 무할당) ══════════════════════
    public void Write(UnifiedGaussianSceneCompositorEngine comp, in SplatCamera cam, float dt)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Frames++;
        var sc = comp.Scene; var start = comp.ProductStart;
        var p = _product;
        var visible = p is not null && _n > 0 && Pose is HoldPose.Hold or HoldPose.Showcase or HoldPose.ThumbsUp;
        _blend += ((visible ? 1f : 0f) - _blend) * (1f - MathF.Exp(-dt / 0.1f));
        if (!visible && _blend < 0.02f)
        {
            if (_n > 0 && sc.Opacity[start] > 0f) for (var k = 0; k < MaxSplats; k++) sc.Opacity[start + k] = 0f;
            LastUs = Us(t0); return;
        }
        _t += dt;
        if (Pose == HoldPose.Showcase) { _yaw += SpinRate * dt; _pitch = 0.35f * MathF.Sin(_t * 0.7f); }
        else { _yaw *= MathF.Exp(-dt / 0.4f); _pitch *= MathF.Exp(-dt / 0.4f); }
        // 손바닥 앵커: R 손목 전역 회전 · 손목 → 손 방향으로 오프셋
        var j = comp.Joints(Avatar);
        var wrist = j[(int)RagdollJoint.RWrist]; var hand = j[(int)RagdollJoint.RHand];
        var rot = comp.JointRotation(Avatar, (int)RagdollJoint.RWrist);
        var along = hand - wrist; var al = along.Length(); along = al > 1e-5f ? along / al : -Vector3.UnitY;
        var palm = hand + along * PalmOffset;
        var spin = Quaternion.CreateFromYawPitchRoll(_yaw, _pitch, 0f);
        // 쇼케이스 = 월드 기준 절차 회전(손목 흔들림과 무관한 360° 턴테이블) · 들기 = 손목 전역 회전에 결합
        var orient = Pose == HoldPose.Showcase ? spin : rot * spin;
        _world = Matrix4x4.CreateFromQuaternion(orient) * Matrix4x4.CreateTranslation(palm);
        _center = palm;
        AnchorErrorMm = Vector3.Distance(Vector3.Transform(Vector3.Zero, _world), palm) * 1000f;
        // 광택(Blinn-Phong · 프레넬)
        var gloss = MathF.Max(4f, p!.Gloss); var metal = Math.Clamp(p.Metallic, 0f, 1f);
        var light = Vector3.Normalize(new Vector3(-0.4f, 0.8f, 0.45f));
        var eye = cam.Position; var op = 0.95f * _blend;
        var peak = 0f;
        for (var k = 0; k < _n; k++)
        {
            var wp = Vector3.Transform(_lp[k], _world);
            var wn = Vector3.Normalize(Vector3.TransformNormal(_ln[k], _world));
            var wt = Vector3.Normalize(Vector3.TransformNormal(_lt[k], _world));
            var v = Vector3.Normalize(eye - wp);
            var h = Vector3.Normalize(v + light);
            var ndl = MathF.Max(0f, Vector3.Dot(wn, light));
            var spec = MathF.Pow(MathF.Max(0f, Vector3.Dot(wn, h)), gloss) * (gloss + 8f) / 32f;
            var fres = MathF.Pow(1f - MathF.Max(0f, Vector3.Dot(wn, v)), 4f);
            var a = _alb[k];
            var col = a * (0.35f + 0.65f * ndl) * (1f - 0.6f * metal) + Vector3.Lerp(Vector3.One, a, metal) * (spec * 0.9f) + new Vector3(0.25f) * fres;
            if (spec > peak) peak = spec;
            sc.Write(start + k, wp, wt, Vector3.Cross(wn, wt), wn, _ls[k], op, Vector3.Min(col, new Vector3(1.5f)), 9);
        }
        if (_n < MaxSplats && sc.Opacity[start + _n] > 0f) for (var k = _n; k < MaxSplats; k++) sc.Opacity[start + k] = 0f;
        PeakSpecular = peak;
        LastUs = Us(t0);
    }

    private static double Us(long t0) => (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
}
