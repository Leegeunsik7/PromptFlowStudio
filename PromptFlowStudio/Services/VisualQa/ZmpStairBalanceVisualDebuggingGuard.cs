using System.Numerics;
using PromptFlowStudio.Services.MotionMatching;

namespace PromptFlowStudio.Services.VisualQa;

/// <summary>
/// [PromptFlow Studio v17.34.0] 🦶 v17.33 계단 보행 ZMP 밸런스 튜닝 &amp; 시각 디버깅 가드(<see cref="Apply"/> · <see cref="Observe"/> · <see cref="DrawOverlay"/> 무할당).
///   ▸ 진단(v17.33 계단 26 %): 원인 분해 결과 ① 척추 전경각이 단차 순간 경사를 추종해 상체 수평 가속 2.2× ② 스윙 발 지면 높이가 챌판에서 순간이동 ③ 루트·골반이 단 주기 톱니를 추종해 수직 가속 1.6×
///     ④ 잔여 도달 하강 즉시 적용 → CoM 수직 스파이크 → ZMP 레버 h/(g + ÿ) 증폭.
///   ▸ 교정(IK 합성기 계단 적응 모드 결선): 진행 방향 단차 탐지(인접 10 cm 높이차 &gt; 4 cm) → 루트 지면 = ±45 cm 미리보기 평균(등가 경사로) ·
///     골반 고정 웅크림 14 cm + 강화 감쇠(반감기 0.2 s) · 스윙 발만 도달 하강 감쇠(접지 발은 즉시 = 관통 0) · 스윙 발끝 12 cm 선행 레이캐스트 + 지면 스프링 ·
///     전경각 = 미리보기 평균 경사 + 150 ms 스프링 · ZMP 기준 평면 = 접지 발 지면 · 카트-테이블 역산 동역학 필터(Δa = g/h·0.6·e · 복원 ω 3).
///   ▸ 시각 디버깅: 오프스크린 프레임에 지지 다각형(내부 녹 · 이탈 적) · CoM 수직 레이캐스트 선 · ZMP 점 · CoM→ZMP 선 · ZMP 궤적 꼬리 · 탑뷰 인셋 오버레이.
///   ▸ 지표: 계단/평지 구간별 ZMP 지지 내 비율 · 최대 이탈 · 계단 프레임 수.
/// </summary>
public sealed class ZmpStairBalanceVisualDebuggingGuard
{
    public const int Trail = 90;
    public const float Crouch = 0.14f, PelvisHalfLife = 0.2f, FilterGain = 0.6f, FilterOmega = 3f;

    private readonly Vector3[] _zTrail = new Vector3[Trail];
    private readonly bool[] _zIn = new bool[Trail];
    private readonly Vector2[] _hull = new Vector2[9];
    private int _trailHead, _trailCount;

    public bool Enabled { get; set; } = true;
    public bool OverlayEnabled { get; set; } = true;
    public long StairFrames { get; private set; }
    public long StairInside { get; private set; }
    public long FlatFrames { get; private set; }
    public long FlatInside { get; private set; }
    public float StairMaxOutside { get; private set; }
    public float StairRatio => StairFrames > 0 ? StairInside / (float)StairFrames : 0f;
    public float FlatRatio => FlatFrames > 0 ? FlatInside / (float)FlatFrames : 0f;

    public void ResetMetrics() { StairFrames = StairInside = FlatFrames = FlatInside = 0; StairMaxOutside = 0f; _trailCount = 0; }

    /// <summary>IK 합성기 계단 적응 · 동역학 필터 파라미터 결선(프레임 시작 전).</summary>
    public void Apply(TerrainAdaptiveBiomechanicsFullBodyIkSynthesizer ik)
    {
        ik.StairAdaptive = Enabled;
        ik.DynamicFilter = Enabled;
        if (!Enabled) return;
        ik.StairCrouch = Crouch;
        ik.StairPelvisHalfLife = PelvisHalfLife;
        ik.FilterGain = FilterGain;
        ik.FilterOmega = FilterOmega;
    }

    /// <summary>동역학 갱신 후 구간별 지표 · ZMP 궤적 기록.</summary>
    public void Observe(TerrainAdaptiveBiomechanicsFullBodyIkSynthesizer ik, ITerrainHeightField terrain, Vector3 root, float heading)
    {
        if (!ik.ZmpValid) return;
        var inside = ik.ZmpOffset <= 0.01f;
        // 계단 구간: 가드 OFF 에서도 같은 기준으로 비교(단차 탐지만 수행)
        var stairs = ik.OnStairs || ik.DetectSteps(terrain, root, heading) > ik.StairStepMin;
        if (stairs)
        {
            StairFrames++;
            if (inside) StairInside++;
            if (ik.ZmpOffset > StairMaxOutside) StairMaxOutside = ik.ZmpOffset;
        }
        else
        {
            FlatFrames++;
            if (inside) FlatInside++;
        }
        _zTrail[_trailHead] = ik.Zmp; _zIn[_trailHead] = inside;
        _trailHead = (_trailHead + 1) % Trail;
        if (_trailCount < Trail) _trailCount++;
    }

    /// <summary>오프스크린 프레임 오버레이(3D 뷰포트 + 탑뷰 인셋) — 무할당.</summary>
    public void DrawOverlay(HeadlessCanvas c, in Camera3D cam, TerrainAdaptiveBiomechanicsFullBodyIkSynthesizer ik, Vector3 root, float heading, int insetX, int insetY, int insetSize)
    {
        if (!OverlayEnabled) return;
        var n = ik.HullCount;
        for (var i = 0; i < n; i++) _hull[i] = ik.HullPoint(i);
        var gy = ik.Zmp.Y + 0.005f;
        var inside = ik.ZmpOffset <= 0.01f;
        var polyCol = inside ? 0x5522C55Eu : 0x55EF4444u;
        // 지지 다각형(팬 분할 반투명)
        if (n >= 3)
        {
            cam.Project(new Vector3(_hull[0].X, gy, _hull[0].Y), out var x0, out var y0);
            for (var i = 1; i < n - 1; i++)
            {
                cam.Project(new Vector3(_hull[i].X, gy, _hull[i].Y), out var x1, out var y1);
                cam.Project(new Vector3(_hull[i + 1].X, gy, _hull[i + 1].Y), out var x2, out var y2);
                c.FillTriangle(x0, y0, x1, y1, x2, y2, polyCol, cam.Clip);
            }
            for (var i = 0; i < n; i++)
            {
                cam.Project(new Vector3(_hull[i].X, gy, _hull[i].Y), out var ax, out var ay);
                cam.Project(new Vector3(_hull[(i + 1) % n].X, gy, _hull[(i + 1) % n].Y), out var bx, out var by);
                c.Line(ax, ay, bx, by, inside ? 0xFF22C55Eu : 0xFFEF4444u, 2, cam.Clip);
            }
        }
        // CoM 수직 레이캐스트 · ZMP · CoM→ZMP
        var com = ik.CenterOfMass;
        cam.Project(com, out var cx, out var cy);
        cam.Project(new Vector3(com.X, gy, com.Z), out var gx, out var gyy);
        c.Line(cx, cy, gx, gyy, 0xFFF59E0Bu, 1, cam.Clip);
        c.FillCircle(cx, cy, 5, 0xFFF59E0Bu, cam.Clip);
        c.FillCircle(gx, gyy, 3, 0xFFFDE68Au, cam.Clip);
        cam.Project(ik.Zmp, out var zx, out var zy);
        c.Line(gx, gyy, zx, zy, 0xFFF87171u, 1, cam.Clip);
        for (var k = 1; k < _trailCount; k++)
        {
            var a = _zTrail[(_trailHead - k + Trail) % Trail]; var b = _zTrail[(_trailHead - k - 1 + Trail) % Trail];
            if (k + 1 > _trailCount) break;
            cam.Project(a, out var ax, out var ay); cam.Project(b, out var bx, out var by);
            c.Line(ax, ay, bx, by, _zIn[(_trailHead - k + Trail) % Trail] ? 0x9938BDF8u : 0x99F87171u, 1, cam.Clip);
        }
        c.FillCircle(zx, zy, 6, inside ? 0xFF38BDF8u : 0xFFEF4444u, cam.Clip);

        // 탑뷰 인셋(루트 중심 · 진행 방향 위 · 1 px = 0.4 cm)
        var clip = new PixelRect(insetX, insetY, insetSize, insetSize);
        c.FillRect(insetX, insetY, insetSize, insetSize, 0xFF0B1220u);
        c.StrokeRect(insetX, insetY, insetSize, insetSize, 0xFF334155u);
        float cxp = insetX + insetSize * 0.5f, cyp = insetY + insetSize * 0.5f, sc = insetSize / 1.2f;
        var right = BipedSkeleton.Right(heading); var fwd = BipedSkeleton.Forward(heading);
        if (n >= 3)
        {
            Top(_hull[0], root, right, fwd, cxp, cyp, sc, out var p0x, out var p0y);
            for (var i = 1; i < n - 1; i++)
            {
                Top(_hull[i], root, right, fwd, cxp, cyp, sc, out var p1x, out var p1y);
                Top(_hull[i + 1], root, right, fwd, cxp, cyp, sc, out var p2x, out var p2y);
                c.FillTriangle(p0x, p0y, p1x, p1y, p2x, p2y, polyCol, clip);
            }
        }
        Top(new Vector2(com.X, com.Z), root, right, fwd, cxp, cyp, sc, out var tcx, out var tcy);
        Top(new Vector2(ik.Zmp.X, ik.Zmp.Z), root, right, fwd, cxp, cyp, sc, out var tzx, out var tzy);
        c.Line(tcx, tcy, tzx, tzy, 0xFFF87171u, 1, clip);
        c.FillCircle(tcx, tcy, 4, 0xFFF59E0Bu, clip);
        c.FillCircle(tzx, tzy, 4, inside ? 0xFF38BDF8u : 0xFFEF4444u, clip);
        c.DrawText(insetX + 6, insetY + 6, inside ? "ZMP IN" : "ZMP OUT", inside ? 0xFF22C55Eu : 0xFFEF4444u, 2, clip);
        c.DrawText(insetX + 6, insetY + insetSize - 18, ik.OnStairs ? "STAIRS" : "FLAT", 0xFF94A3B8u, 2, clip);
    }

    private static void Top(Vector2 p, Vector3 root, Vector3 right, Vector3 fwd, float cx, float cy, float sc, out float x, out float y)
    {
        var d = new Vector3(p.X - root.X, 0f, p.Y - root.Z);
        x = cx + Vector3.Dot(d, right) * sc;
        y = cy - Vector3.Dot(d, fwd) * sc;
    }
}
