using System.Numerics;
using PromptFlowStudio.Services.WorldExpansion;

namespace PromptFlowStudio.Services.MotionMatching;

/// <summary>지형 높이장(수직 레이캐스트 = 높이 질의 · 구현체는 단일 스레드 사용).</summary>
public interface ITerrainHeightField
{
    float Height(float x, float z);
}

public enum TerrainKind : byte { Flat, Ramp, Stairs, Bumps, NeuralWorld }

/// <summary>절차 지형(평지 · 경사로 ±14° 삼각파 · 계단 피라미드 15 cm × 32 cm · 굴곡).</summary>
public sealed class ProceduralTerrainField : ITerrainHeightField
{
    public const float StepH = 0.15f, StepD = 0.32f, RampRise = 3f, RampRun = 12f;

    public ProceduralTerrainField(TerrainKind kind) => Kind = kind;

    public TerrainKind Kind { get; }

    public float Height(float x, float z)
    {
        switch (Kind)
        {
            case TerrainKind.Ramp:
            {
                var s = x - MathF.Floor(x / (2f * RampRun)) * 2f * RampRun;
                return RampRise * (s < RampRun ? s / RampRun : 2f - s / RampRun);
            }
            case TerrainKind.Stairs:
            {
                var i = (int)MathF.Floor(x / StepD);
                var m = ((i % 20) + 20) % 20;
                return (m <= 10 ? m : 20 - m) * StepH;
            }
            case TerrainKind.Bumps:
                return 0.22f * MathF.Sin(0.8f * x) * MathF.Cos(0.7f * z) + 0.12f * MathF.Sin(1.7f * x + 0.9f * z);
            default:
                return 0f;
        }
    }
}

/// <summary>v17.27 생성형 신경망 월드(해시 그리드 + MLP 높이) 어댑터 — 원점 기준 0 정규화.</summary>
public sealed class NeuralWorldTerrainField : ITerrainHeightField
{
    private readonly float _base;

    public NeuralWorldTerrainField(string prompt = "완만한 언덕과 숲길")
    {
        World = new GenerativeNeuralWorldExpansionEngine();
        World.SetPrompt(prompt);
        _base = World.Height(0f, 0f);
    }

    public GenerativeNeuralWorldExpansionEngine World { get; }

    public float Height(float x, float z) => World.Height(x, z) - _base;
}

public enum GroundSurface : byte { Dry, Wet, Snow, Mud }

/// <summary>지면 물성(침하 깊이 · 마찰 계수) — v17.28 적설/습윤 연동.</summary>
public readonly record struct SurfaceProfile(GroundSurface Kind, float SinkM, float Friction)
{
    public static SurfaceProfile Of(GroundSurface k, float snowCm = 15f) => k switch
    {
        GroundSurface.Wet => new(k, 0f, 0.45f),
        GroundSurface.Snow => new(k, Math.Clamp(snowCm * 0.004f, 0f, 0.1f), 0.28f),
        GroundSurface.Mud => new(k, 0.035f, 0.5f),
        _ => new(GroundSurface.Dry, 0f, 0.8f),
    };

    /// <summary>v17.28 미기후(평균 적설 cm · 평균 습윤도 0~1) → 지면 물성.</summary>
    public static SurfaceProfile FromWeather(float snowCm, float wetness) =>
        snowCm >= 1f ? Of(GroundSurface.Snow, snowCm) : wetness >= 0.7f ? Of(GroundSurface.Mud) : wetness >= 0.25f ? Of(GroundSurface.Wet) : Of(GroundSurface.Dry);

    public static string Name(GroundSurface k) => k switch { GroundSurface.Wet => "젖은 노면", GroundSurface.Snow => "적설", GroundSurface.Mud => "진흙", _ => "건조" };
}

/// <summary>
/// [PromptFlow Studio v17.33.0] 🦵 지형 적응형 전신 FABRIK &amp; ZMP 동적 균형 합성기(<see cref="Begin"/> · <see cref="Solve"/> · <see cref="UpdateDynamics"/> 무할당).
///   ▸ 지면 정렬: 발목 · 뒤꿈치 · 발끝 수직 레이캐스트(중앙 차분 법선) − 침하 → 지지 높이 max(발목, 뒤꿈치) + 애니 발목 높이 · 발끝 높이 → 발 피치(길이 보존).
///   ▸ 골반: 수직 min(좌/우 발 오프셋 · 도달 요구 하강) 임계 감쇠 50 ms + 잔여 도달 초과 즉시 클램프 · 수평 ZMP 균형 이동(이탈 300 ms 저역 통과 · 350 ms 스프링 · ±12 cm).
///   ▸ 전신 FABRIK: 골반 = 서브 베이스(휴리스틱 강성 6 + 접지 다리 1 · 스윙 0.25 가중 중심) · 다리 2 체인 후방/전방 패스 · 극 벡터(애니 무릎 방향) 회전 보정 · 허용 0.5 mm.
///   ▸ 척추: 진행 방향 경사 → 전경각 0.45·atan(기울기) 분배(요추 0.4 · 흉추 0.75 · 머리 1.0) → 단일 체인 FABRIK 길이 복원 · 팔 = 흉곽 강체.
///   ▸ ZMP(카트-테이블 · 50 ms 지연): CoM(Winter 분절 질량) 7점 Savitzky–Golay 중심 2차 미분 → zmp = c − (c_y − g_y)/(g + a_y)·a_xz · 지지 다각형(접지 발 직사각형 볼록 껍질) 부호 거리 ·
///     접지력 F = M(g + a_y) 를 발목 선분 투영 비로 좌/우 분배 · 필요 마찰 |M a_xz|/F vs 노면 μ.
/// </summary>
public sealed class TerrainAdaptiveBiomechanicsFullBodyIkSynthesizer
{
    public const int Count = BipedSkeleton.Count, MaxIter = 16, SpineIter = 6, Ring = 7;
    public const float Tol = 0.0005f, G = 9.81f, Eps = 0.04f, PelvisHalfLife = 0.12f, BalanceHalfLife = 0.35f, BalanceGain = 0.5f, MaxShift = 0.12f, DeadZone = 0.03f, PelvisStiffness = 6f;

    private static readonly int[] Hip = { 4, 8 }, Knee = { 5, 9 }, Ankle = { 6, 10 }, Toe = { 7, 11 };
    private readonly Vector3[] _anim = new Vector3[Count], _out = new Vector3[Count];
    private readonly Vector3[] _ankleT = new Vector3[2], _toeT = new Vector3[2], _normal = new Vector3[2], _pole = new Vector3[2], _hipOff = new Vector3[2];
    private readonly float[] _ground = new float[2];
    private readonly Vector2[] _pts = new Vector2[8], _hull = new Vector2[9];
    private static readonly float[] Sg = { 5f, 0f, -3f, -4f, -3f, 0f, 5f };
    private readonly Vector3[] _comR = new Vector3[Ring], _ankR = new Vector3[Ring * 2], _toeR = new Vector3[Ring * 2];
    private readonly byte[] _ctR = new byte[Ring];
    private readonly float[] _gR = new float[Ring * 2], _gS = new float[2], _gV = new float[2];
    private readonly bool[] _gInit = new bool[2];
    private Vector3 _dc, _dv, _anc, _ancV, _pelOff, _pelOffV, _shift, _errLp, _origin, _acc, _zmp;
    private float _rootGround, _rootGroundV;
    private int _hullN, _comN, _ri;
    private float _dropS, _dropV, _leanS, _leanV;
    private bool _init;

    public bool Enabled { get; set; } = true;
    public bool BalanceEnabled { get; set; } = true;
    /// <summary>계단 적응(v17.34 ZMP 계단 가드가 제어): 단차 미리보기 루트 지면 · 스윙/접지 지면 스프링 · 골반 강화 감쇠 · 접지 발 ZMP 평면.</summary>
    public bool StairAdaptive { get; set; }
    public float StairStepMin { get; set; } = 0.04f;
    public float PreviewRadius { get; set; } = 0.45f;
    /// <summary>미리보기 반경 내 편측 샘플 수(기본 4 = 9점 · v17.35 가드 16 = 33점 → 단차 통과 시 지면 계단 1.7 → 0.5 cm).</summary>
    public int PreviewSamples { get; set; } = 4;
    public float StairPelvisHalfLife { get; set; } = 0.2f;
    public float SwingHalfLife { get; set; } = 0.05f;
    public float StanceHalfLife { get; set; } = 0.03f;
    public float StairCrouch { get; set; } = 0.1f;
    public float AnchorHalfLife { get; set; } = 0.12f;
    public bool StanceAnchoring { get; set; }
    /// <summary>ZMP 동역학 필터(카트-테이블 역산 CoM 보정).</summary>
    public bool DynamicFilter { get; set; }
    public float FilterGain { get; set; } = 0.5f;
    public float FilterOmega { get; set; } = 6f;
    public Vector3 FilterOffset => _dc;
    public float StairDropHalfLife { get; set; } = 0.15f;
    /// <summary>외부 CoM 제어(v17.35 ZMP 동적 안정 가드): 골반 수평 = <see cref="ComCorrection"/> 단독(균형 이동 · 앵커 · 동역학 필터 대체).</summary>
    public bool ComControl { get; set; }
    public Vector3 ComCorrection { get; set; }
    public Vector3 ComAcceleration => _acc;
    public bool OnStairs { get; private set; }
    public float StepAhead { get; private set; }
    public float Heading { get; private set; }
    public ReadOnlySpan<Vector3> AnimWorld => _anim;
    public Span<Vector3> Pose => _out;
    public Span<Vector3> AnkleTargets => _ankleT;
    public Span<Vector3> ToeTargets => _toeT;
    public ReadOnlySpan<float> GroundY => _ground;
    public ReadOnlySpan<Vector3> Poles => _pole;
    public Vector3 FootNormal(int s) => _normal[s];
    public Vector3 PelvisOffset { get; private set; }
    public Vector3 BalanceShift => _shift;
    public float ReachDrop { get; private set; }
    public float SpineLeanDeg { get; private set; }
    public int LastIterations { get; private set; }
    public float LastIkErrorMm { get; private set; }
    public double LastSolveUs { get; private set; }
    public double MaxSolveUs { get; private set; }
    public Vector3 CenterOfMass { get; private set; }
    public Vector3 Zmp => _zmp;
    public bool ZmpValid { get; private set; }
    /// <summary>ZMP ↔ 지지 다각형 부호 거리(m · 음수 = 내부).</summary>
    public float ZmpOffset { get; private set; }
    public float ForceL { get; private set; }
    public float ForceR { get; private set; }
    public float RequiredFriction { get; private set; }
    public long SupportFrames { get; private set; }
    public long ZmpInsideFrames { get; private set; }
    public long SlipRiskFrames { get; private set; }
    public float MaxZmpOutside { get; private set; }
    public float MaxPenetration { get; private set; }
    public float LastPenetration { get; private set; }
    public float FootAlignDeg { get; private set; }
    public float ZmpInsideRatio => SupportFrames > 0 ? ZmpInsideFrames / (float)SupportFrames : 1f;
    public int HullCount => _hullN;
    public Vector2 HullPoint(int i) => _hull[i];

    public void Reset() { Array.Clear(_gInit); OnStairs = false; _init = false; _pelOff = _pelOffV = _shift = _errLp = _acc = _anc = _ancV = _dc = _dv = Vector3.Zero; _comN = 0; _hullN = 0; ZmpValid = false; ResetMetrics(); }

    public void ResetMetrics()
    {
        SupportFrames = ZmpInsideFrames = SlipRiskFrames = 0; MaxZmpOutside = MaxPenetration = 0f; MaxSolveUs = 0;
    }

    /// <summary>수직 레이캐스트 → 높이 · 법선(중앙 차분).</summary>
    public static float Raycast(ITerrainHeightField t, float x, float z, out Vector3 n)
    {
        var h = t.Height(x, z);
        var hx = t.Height(x + Eps, z) - t.Height(x - Eps, z);
        var hz = t.Height(x, z + Eps) - t.Height(x, z - Eps);
        n = Vector3.Normalize(new Vector3(-hx / (2f * Eps), 1f, -hz / (2f * Eps)));
        return h;
    }

    private static void Spring(ref float x, ref float v, float goal, float hl, float dt)
    {
        var y = TrajectoryController.Damping(hl) * 0.5f;
        var j0 = x - goal; var j1 = v + j0 * y; var e = MathF.Exp(-y * dt);
        x = e * (j0 + j1 * dt) + goal; v = e * (v - j1 * y * dt);
    }

    private static void Spring(ref Vector3 x, ref Vector3 v, Vector3 goal, float hl, float dt)
    {
        var y = TrajectoryController.Damping(hl) * 0.5f;
        var j0 = x - goal; var j1 = v + j0 * y; var e = MathF.Exp(-y * dt);
        x = e * (j0 + j1 * dt) + goal; v = e * (v - j1 * y * dt);
    }

    /// <summary>1단계: 로컬 애니 → 월드(루트 지면 높이 스프링) · 발 지면 정렬 목표 · 극 벡터.</summary>
    public void Begin(ReadOnlySpan<Vector3> local, Vector3 rootPos, float heading, ITerrainHeightField terrain, in SurfaceProfile surface, float dt, byte contact = 3)
    {
        // 계단 적응: 진행 방향 단차 탐지 → 루트 지면 = 전후 미리보기 평균(계단 → 등가 경사로) · 골반 반감기 강화
        OnStairs = StairAdaptive && DetectSteps(terrain, rootPos, heading) > StairStepMin;
        var hr = (OnStairs ? PreviewGround(terrain, rootPos, heading) : terrain.Height(rootPos.X, rootPos.Z)) - surface.SinkM;
        if (!_init) { _rootGround = hr; _rootGroundV = 0f; _init = true; }
        else Spring(ref _rootGround, ref _rootGroundV, hr, 0.06f, dt);
        Heading = heading;
        _origin = new Vector3(rootPos.X, _rootGround, rootPos.Z);
        for (var j = 0; j < Count; j++) _anim[j] = BipedSkeleton.ToWorld(local[j], _origin, heading);
        var fallback = BipedSkeleton.Forward(heading);
        float align = 0f;
        for (var s = 0; s < 2; s++)
        {
            var a = _anim[Ankle[s]]; var t = _anim[Toe[s]];
            _pole[s] = BipedSkeleton.Dir(_anim[Knee[s]] - 0.5f * (_anim[Hip[s]] + a), fallback);
            if (!Enabled) { _ankleT[s] = a; _toeT[s] = t; _ground[s] = _rootGround; _normal[s] = Vector3.UnitY; continue; }
            var fwd = t - a; fwd.Y = 0f; fwd = BipedSkeleton.Dir(fwd, fallback);
            var heel = a - fwd * 0.06f;
            var ha = Raycast(terrain, a.X, a.Z, out var n) - surface.SinkM;
            var hh = terrain.Height(heel.X, heel.Z) - surface.SinkM;
            var ht = terrain.Height(t.X, t.Z) - surface.SinkM;
            var support = MathF.Max(ha, hh);
            if (StairAdaptive)
            {
                // 스윙 발: 발끝 전방 12 cm 선행 샘플(챌판 사전 상승) · 접지/스윙 공통 임계 감쇠(순간 단차 순간이동 → 연속 궤적)
                var swing = (contact >> s & 1) == 0;
                var goal = support;
                if (swing) goal = MathF.Max(goal, MathF.Max(ht, terrain.Height(t.X + fwd.X * 0.12f, t.Z + fwd.Z * 0.12f) - surface.SinkM));
                if (!_gInit[s]) { _gS[s] = goal; _gV[s] = 0f; _gInit[s] = true; }
                else Spring(ref _gS[s], ref _gV[s], goal, swing ? SwingHalfLife : StanceHalfLife, dt);
                support = MathF.Max(_gS[s], MathF.Max(ha, hh));
            }
            // 지면 하한 클램프(관성 스프링 오버슈트 · 뼈 길이 복원으로 발목고 미만 하강 방지)
            var ankleT = new Vector3(a.X, support + MathF.Max(local[Ankle[s]].Y, BipedSkeleton.AnkleH), a.Z);
            var toeT = new Vector3(t.X, MathF.Max(ht, support - 0.08f) + MathF.Max(local[Toe[s]].Y, BipedSkeleton.ToeH), t.Z);
            toeT = ankleT + BipedSkeleton.Dir(toeT - ankleT, fwd) * BipedSkeleton.FootLen;
            _ankleT[s] = ankleT; _toeT[s] = toeT; _ground[s] = support; _normal[s] = n;
            // 발 축 ↔ 지면 접평면 정렬 오차
            var fd = BipedSkeleton.Dir(toeT - ankleT - new Vector3(0f, BipedSkeleton.ToeH - BipedSkeleton.AnkleH, 0f), fwd);
            align = MathF.Max(align, MathF.Abs(MathF.Asin(Math.Clamp(Vector3.Dot(fd, n), -1f, 1f))) * 57.29578f);
        }
        FootAlignDeg = align;
    }

    /// <summary>2단계: 골반 오프셋 · 균형 이동 · 전신 FABRIK · 척추 굴곡(무할당).</summary>
    public void Solve(ITerrainHeightField terrain, in SurfaceProfile surface, byte contact, float maxReach, float dt)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!Enabled)
        {
            _anim.AsSpan().CopyTo(_out);
            PelvisOffset = Vector3.Zero; LastIterations = 0; LastIkErrorMm = 0f; ReachDrop = 0f; SpineLeanDeg = 0f;
            return;
        }
        // 최종 목표(가드 락 xz 반영) 지면 하한 재적용 — 락 위치가 다른 계단 단 위일 때 관통 방지
        for (var s = 0; s < 2; s++)
        {
            var a = _ankleT[s]; var t = _toeT[s];
            var ax = BipedSkeleton.Dir(new Vector3(t.X - a.X, 0f, t.Z - a.Z), BipedSkeleton.Forward(Heading));
            var sup = MathF.Max(terrain.Height(a.X, a.Z), terrain.Height(a.X - ax.X * 0.06f, a.Z - ax.Z * 0.06f)) - surface.SinkM;
            if (a.Y < sup + BipedSkeleton.AnkleH) { var dy = sup + BipedSkeleton.AnkleH - a.Y; _ankleT[s].Y += dy; _toeT[s].Y += dy; }
            var st = terrain.Height(_toeT[s].X, _toeT[s].Z) - surface.SinkM + BipedSkeleton.ToeH;
            if (_toeT[s].Y < st) _toeT[s] = _ankleT[s] + BipedSkeleton.Dir(new Vector3(_toeT[s].X, st, _toeT[s].Z) - _ankleT[s], ax) * BipedSkeleton.FootLen;
            _ground[s] = MathF.Max(_ground[s], sup);
        }
        var pelA = _anim[0];
        // 골반 수직: min(좌/우 발 오프셋, 도달 요구 하강) → 임계 감쇠(50 ms) · 잔여 도달 초과만 즉시 클램프
        // 계단: 루트가 이미 등가 경사로를 추종 → 발 오프셋(단 주기 톱니) 대신 고정 웅크림 + 도달 요구만(수직 가속 억제)
        var offY = OnStairs ? -StairCrouch : MathF.Min(_ankleT[0].Y - _anim[Ankle[0]].Y, _ankleT[1].Y - _anim[Ankle[1]].Y);
        var basePel = pelA + new Vector3(_pelOff.X, 0f, _pelOff.Z);
        for (var s = 0; s < 2; s++)
        {
            _hipOff[s] = _anim[Hip[s]] - pelA;
            offY = MathF.Min(offY, ReachLimit(_ankleT[s] - (basePel + _hipOff[s]), maxReach * 0.995f));
        }
        offY = Math.Clamp(offY, -0.45f, 0.3f);
        if (!BalanceEnabled) _shift = Vector3.Zero;
        Spring(ref _pelOff.Y, ref _pelOffV.Y, offY, OnStairs ? StairPelvisHalfLife : PelvisHalfLife, dt);
        // 접지 앵커: 락된 접지 발 ↔ 애니 발목 수평 편차 평균만큼 골반 이동(DB 의 발-CoM 관계 보존 → 지지 다각형 정합)
        var anchor = Vector3.Zero; var na = 0;
        if (StanceAnchoring)
            for (var s = 0; s < 2; s++)
                if ((contact >> s & 1) != 0) { var d = _ankleT[s] - _anim[Ankle[s]]; anchor += new Vector3(d.X, 0f, d.Z); na++; }
        if (na > 0) anchor /= na;
        Spring(ref _anc.X, ref _ancV.X, anchor.X, AnchorHalfLife, dt);
        Spring(ref _anc.Z, ref _ancV.Z, anchor.Z, AnchorHalfLife, dt);
        Spring(ref _pelOff.X, ref _pelOffV.X, _shift.X, BalanceHalfLife, dt);
        Spring(ref _pelOff.Z, ref _pelOffV.Z, _shift.Z, BalanceHalfLife, dt);
        var pel = ComControl ? pelA + new Vector3(ComCorrection.X, _pelOff.Y, ComCorrection.Z)
            : pelA + _pelOff + new Vector3(_anc.X, 0f, _anc.Z) + (DynamicFilter ? _dc / 0.82f : Vector3.Zero);
        float drop = 0f, dropSwing = 0f;
        for (var s = 0; s < 2; s++)
        {
            var need = ReachLimit(_ankleT[s] - (pel + _hipOff[s]), maxReach);
            if ((OnStairs || ComControl) && (contact >> s & 1) == 0) dropSwing = MathF.Min(dropSwing, need);
            else drop = MathF.Min(drop, need);
        }
        // 계단: 스윙 발 도달 하강만 임계 감쇠(공중 발 미도달 허용 · 수직 가속 스파이크 차단) — 접지 발 요구는 즉시(관통 방지)
        if (ComControl)
        {
            // 외부 CoM 제어: 하강 해제(이륙 · 도달 회복)도 임계 감쇠 — 접지 요구가 더 깊으면 즉시(관통 방지) · 높이 불연속 → 수직 가속 스파이크 차단
            Spring(ref _dropS, ref _dropV, MathF.Min(drop, dropSwing), StairDropHalfLife, dt);
            if (drop < _dropS) { _dropS = drop; _dropV = MathF.Min(_dropV, 0f); }
            drop = MathF.Min(0f, _dropS);
        }
        else if (OnStairs) { Spring(ref _dropS, ref _dropV, dropSwing, StairDropHalfLife, dt); drop = MathF.Min(drop, MathF.Min(0f, _dropS)); }
        else { _dropS = 0f; _dropV = 0f; }
        drop = MathF.Max(drop, -0.35f);
        pel.Y += drop; ReachDrop = drop;

        // 전신 FABRIK(골반 서브 베이스 · 다리 2 체인)
        var delta = pel - pelA;
        Span<Vector3> hip = stackalloc Vector3[2], knee = stackalloc Vector3[2], ank = stackalloc Vector3[2];
        Span<float> w = stackalloc float[2];
        for (var s = 0; s < 2; s++)
        {
            hip[s] = pel + _hipOff[s]; knee[s] = _anim[Knee[s]] + delta; ank[s] = _anim[Ankle[s]] + delta;
            w[s] = (contact >> s & 1) != 0 ? 1f : 0.25f;
        }
        var heur = pel;
        var it = 0; var err = 0f;
        for (; it < MaxIter; it++)
        {
            var sum = heur * PelvisStiffness; var ws = PelvisStiffness;
            for (var s = 0; s < 2; s++)
            {
                var a = _ankleT[s];
                var k = a + BipedSkeleton.Dir(knee[s] - a, _pole[s]) * BipedSkeleton.Shin;
                var h = k + BipedSkeleton.Dir(hip[s] - k, Vector3.UnitY) * BipedSkeleton.Thigh;
                knee[s] = k;
                sum += (h - _hipOff[s]) * w[s]; ws += w[s];
            }
            pel = sum / ws;
            err = 0f;
            for (var s = 0; s < 2; s++)
            {
                hip[s] = pel + _hipOff[s];
                knee[s] = hip[s] + BipedSkeleton.Dir(knee[s] - hip[s], _pole[s]) * BipedSkeleton.Thigh;
                ank[s] = knee[s] + BipedSkeleton.Dir(_ankleT[s] - knee[s], -Vector3.UnitY) * BipedSkeleton.Shin;
                knee[s] = PoleFix(hip[s], knee[s], ank[s], _pole[s]);
                err = MathF.Max(err, (ank[s] - _ankleT[s]).Length());
            }
            if (err < Tol) { it++; break; }
        }
        LastIterations = it; LastIkErrorMm = err * 1000f;
        PelvisOffset = pel - pelA;
        _out[0] = pel;
        for (var s = 0; s < 2; s++)
        {
            _out[Hip[s]] = hip[s]; _out[Knee[s]] = knee[s]; _out[Ankle[s]] = ank[s];
            _out[Toe[s]] = ank[s] + BipedSkeleton.Dir(_toeT[s] - _ankleT[s], BipedSkeleton.Forward(Heading)) * BipedSkeleton.FootLen;
        }

        // 척추 굴곡: 진행 방향 경사 → 전경 분배 → 단일 체인 FABRIK
        var fwd = BipedSkeleton.Forward(Heading);
        var slope = (terrain.Height(_origin.X + fwd.X * 0.35f, _origin.Z + fwd.Z * 0.35f) - terrain.Height(_origin.X - fwd.X * 0.35f, _origin.Z - fwd.Z * 0.35f)) / 0.7f;
        var lean = 0.45f * MathF.Atan(slope);
        if (StairAdaptive)
        {
            // 계단: 단차 순간 경사(±∞ 근사) 대신 미리보기 평균 경사 + 전경각 임계 감쇠(150 ms) — 상체 수평 가속 억제
            var gp = PreviewGround(terrain, _origin + fwd * 0.35f, Heading); var gm = PreviewGround(terrain, _origin - fwd * 0.35f, Heading);
            Spring(ref _leanS, ref _leanV, 0.45f * MathF.Atan((gp - gm) / 0.7f), 0.15f, dt);
            lean = _leanS;
        }
        SpineLeanDeg = lean * 57.29578f;
        var right = BipedSkeleton.Right(Heading);
        Span<Vector3> ch = stackalloc Vector3[4];
        ch[0] = pel;
        ch[1] = pel + Vector3.Transform(_anim[1] - pelA, Quaternion.CreateFromAxisAngle(right, lean * 0.4f));
        ch[2] = pel + Vector3.Transform(_anim[2] - pelA, Quaternion.CreateFromAxisAngle(right, lean * 0.75f));
        var headT = pel + Vector3.Transform(_anim[3] - pelA, Quaternion.CreateFromAxisAngle(right, lean));
        ch[3] = headT;
        for (var i = 0; i < SpineIter; i++)
        {
            ch[3] = headT;
            for (var j = 2; j >= 1; j--) ch[j] = ch[j + 1] + BipedSkeleton.Dir(ch[j] - ch[j + 1], -Vector3.UnitY) * BipedSkeleton.BoneLength[j + 1];
            ch[0] = pel;
            for (var j = 1; j <= 3; j++) ch[j] = ch[j - 1] + BipedSkeleton.Dir(ch[j] - ch[j - 1], Vector3.UnitY) * BipedSkeleton.BoneLength[j];
        }
        _out[1] = ch[1]; _out[2] = ch[2]; _out[3] = ch[3];
        var rq = Quaternion.CreateFromAxisAngle(right, lean * 0.75f);
        for (var j = 12; j < Count; j++) _out[j] = ch[2] + Vector3.Transform(_anim[j] - _anim[2], rq);
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        LastSolveUs = us; if (us > MaxSolveUs) MaxSolveUs = us;
    }

    /// <summary>발 지면 높이 재설정(접지 락 xz 재정렬 후 — ZMP 기준 평면 · 관통 하한).</summary>
    public void SetGround(int s, float y) => _ground[s] = y;

    /// <summary>
    /// 최종 자세 CoM 잔차 보정(무할당): 골반 · 체간 · 팔 · 고관절 강체 평행 이동 → 2-본 IK 무릎 재구성(발목 고정 · 도달 초과 시 상체 하강) 반복.
    /// vertical = 목표 높이까지 수직 포함. 반환 = 누적 골반 이동.
    /// </summary>
    public Vector3 ApplyComShift(Vector3 target, bool vertical = false, int iterations = 4)
    {
        var total = Vector3.Zero;
        var reach = BipedSkeleton.Thigh + BipedSkeleton.Shin - 0.002f;
        for (var it = 0; it < iterations; it++)
        {
            var c = BipedSkeleton.CenterOfMass(_out);
            var e = new Vector3(target.X - c.X, vertical ? target.Y - c.Y : 0f, target.Z - c.Z);
            if (e.LengthSquared() < 1e-8f) break;
            var d = e / 0.86f;
            // 도달 한계: 수평 이동 후 고관절 상승 허용치(초과 시 하강 · 최대 5 cm)
            var dy = d.Y;
            for (var s = 0; s < 2; s++)
            {
                var v = _out[Ankle[s]] - (_out[Hip[s]] + new Vector3(d.X, 0f, d.Z));
                var xz2 = v.X * v.X + v.Z * v.Z;
                if (xz2 < reach * reach) dy = MathF.Min(dy, v.Y + MathF.Sqrt(reach * reach - xz2));
            }
            d.Y = MathF.Max(dy, -0.15f);
            for (var j = 0; j < 4; j++) _out[j] += d;
            for (var j = 12; j < Count; j++) _out[j] += d;
            for (var s = 0; s < 2; s++)
            {
                _out[Hip[s]] += d;
                _out[Knee[s]] = BipedSkeleton.TwoBone(_out[Hip[s]], _out[Ankle[s]], _pole[s], BipedSkeleton.Thigh, BipedSkeleton.Shin);
            }
            total += d;
        }
        return total;
    }

    /// <summary>진행 방향 ±미리보기 반경 10 cm 간격 인접 높이차 최대(단차 탐지).</summary>
    public float DetectSteps(ITerrainHeightField t, Vector3 root, float heading)
    {
        var f = BipedSkeleton.Forward(heading);
        var n = (int)(PreviewRadius / 0.1f);
        var prev = t.Height(root.X - f.X * n * 0.1f, root.Z - f.Z * n * 0.1f);
        var mx = 0f;
        for (var i = -n + 1; i <= n; i++)
        {
            var h = t.Height(root.X + f.X * i * 0.1f, root.Z + f.Z * i * 0.1f);
            mx = MathF.Max(mx, MathF.Abs(h - prev));
            prev = h;
        }
        StepAhead = mx;
        return mx;
    }

    /// <summary>진행 방향 ±반경 9점 평균 지면(계단 → 등가 경사로).</summary>
    public float PreviewGround(ITerrainHeightField t, Vector3 root, float heading)
    {
        var f = BipedSkeleton.Forward(heading);
        var acc = 0f;
        var n = Math.Max(PreviewSamples, 1);
        for (var i = -n; i <= n; i++) { var d = PreviewRadius * i / n; acc += t.Height(root.X + f.X * d, root.Z + f.Z * d); }
        return acc / (2 * n + 1);
    }

    /// <summary>고관절→목표 벡터가 도달 거리 안에 들도록 필요한 골반 수직 이동(≤ 0 · 수평 초과 시 0 = IK/가드 처리).</summary>
    private static float ReachLimit(Vector3 v, float reach)
    {
        var xz2 = v.X * v.X + v.Z * v.Z;
        return xz2 < reach * reach ? MathF.Min(0f, v.Y + MathF.Sqrt(reach * reach - xz2)) : 0f;
    }

    /// <summary>무릎을 고관절-발목 축 주위로 회전해 극 벡터 평면에 정렬(길이 보존).</summary>
    public static Vector3 PoleFix(Vector3 hip, Vector3 knee, Vector3 ankle, Vector3 pole)
    {
        var axis = BipedSkeleton.Dir(ankle - hip, -Vector3.UnitY);
        var r = knee - hip;
        var along = Vector3.Dot(r, axis);
        var perp = r - axis * along;
        var pl = perp.Length();
        if (pl < 1e-6f) return knee;
        var pd = pole - axis * Vector3.Dot(pole, axis);
        if (pd.LengthSquared() < 1e-10f) return knee;
        return hip + axis * along + Vector3.Normalize(pd) * pl;
    }

    /// <summary>3단계(최종 자세 후): CoM · ZMP(3 프레임 지연 SG 미분) · 지지 다각형 · 접지력 · 마찰 · 관통 · 다음 프레임 균형 이동.</summary>
    public void UpdateDynamics(ITerrainHeightField terrain, in SurfaceProfile surface, byte contact, float dt)
    {
        var com = BipedSkeleton.CenterOfMass(_out);
        CenterOfMass = com;
        _ri = (_ri + 1) % Ring;
        _comR[_ri] = com; _ctR[_ri] = contact; _gR[_ri * 2] = _ground[0]; _gR[_ri * 2 + 1] = _ground[1];
        for (var s = 0; s < 2; s++) { _ankR[_ri * 2 + s] = _out[Ankle[s]]; _toeR[_ri * 2 + s] = _out[Toe[s]]; }
        if (_comN < Ring) _comN++;

        // 관통(현재 프레임 · 접지 발 발목 기준)
        var pen = 0f;
        for (var s = 0; s < 2; s++)
        {
            if ((contact >> s & 1) == 0) continue;
            var a = _out[Ankle[s]];
            var fwd = BipedSkeleton.Dir(new Vector3(_out[Toe[s]].X - a.X, 0f, _out[Toe[s]].Z - a.Z), BipedSkeleton.Forward(Heading));
            var g = MathF.Max(terrain.Height(a.X, a.Z), terrain.Height(a.X - fwd.X * 0.06f, a.Z - fwd.Z * 0.06f)) - surface.SinkM;
            pen = MathF.Max(pen, g + BipedSkeleton.AnkleH - 0.01f - a.Y);
        }
        LastPenetration = MathF.Max(0f, pen);
        if (LastPenetration > MaxPenetration) MaxPenetration = LastPenetration;

        if (_comN < Ring)
        {
            ZmpValid = false; _hullN = 0; ZmpOffset = 0f; ForceL = ForceR = 0f; RequiredFriction = 0f;
            return;
        }
        // 중심 프레임(t − 3) · Savitzky–Golay 7점 2차 미분 [5 0 −3 −4 −3 0 5]/42
        var mid = (_ri - 3 + Ring) % Ring;
        var acc = Vector3.Zero;
        for (var i = 0; i < Ring; i++) acc += Sg[i] * _comR[(_ri - 6 + i + Ring) % Ring];
        _acc = acc / (42f * dt * dt);
        var cm = _comR[mid]; var ct = _ctR[mid];

        // 지지 다각형(접지 발 직사각형 → 볼록 껍질)
        var np = 0;
        for (var s = 0; s < 2; s++)
        {
            if ((ct >> s & 1) == 0) continue;
            var a = _ankR[mid * 2 + s]; var t = _toeR[mid * 2 + s];
            var ax = BipedSkeleton.Dir(new Vector3(t.X - a.X, 0f, t.Z - a.Z), BipedSkeleton.Forward(Heading));
            var sd = new Vector3(ax.Z, 0f, -ax.X) * 0.045f;
            var heel = a - ax * 0.07f; var tip = t + ax * 0.03f;
            _pts[np++] = new Vector2(heel.X + sd.X, heel.Z + sd.Z); _pts[np++] = new Vector2(heel.X - sd.X, heel.Z - sd.Z);
            _pts[np++] = new Vector2(tip.X + sd.X, tip.Z + sd.Z); _pts[np++] = new Vector2(tip.X - sd.X, tip.Z - sd.Z);
        }
        _hullN = np >= 3 ? ConvexHull(_pts.AsSpan(0, np), _hull) : 0;
        // ZMP 기준 평면: 계단 적응 = 접지 발 지면 평균(CoM 아래 단차 불연속 배제) · 기본 = CoM 직하 지면
        float gy;
        if (StairAdaptive && (ct & 3) != 0)
        {
            gy = (ct & 3) == 3 ? 0.5f * (_gR[mid * 2] + _gR[mid * 2 + 1]) : _gR[mid * 2 + ((ct & 1) != 0 ? 0 : 1)];
        }
        else gy = terrain.Height(cm.X, cm.Z) - surface.SinkM;
        var denom = MathF.Max(G + _acc.Y, 0.2f * G);
        var zx = cm.X - (cm.Y - gy) / denom * _acc.X;
        var zz = cm.Z - (cm.Y - gy) / denom * _acc.Z;
        _zmp = new Vector3(zx, gy, zz);
        ZmpValid = _hullN >= 3;
        var f = ZmpValid ? BipedSkeleton.BodyMass * denom : 0f;
        if (ZmpValid)
        {
            var d = SignedDistance(_hull.AsSpan(0, _hullN), new Vector2(zx, zz), out var closest);
            ZmpOffset = d;
            SupportFrames++;
            if (d <= 0.01f) ZmpInsideFrames++;
            if (d > MaxZmpOutside) MaxZmpOutside = d;
            // 균형 이동(저대역): 사역 3 cm 초과 이탈 벡터를 300 ms 저역 통과 → CoM 반대 이동 목표(빠른 이동 자체가 가속 → ZMP 역이동 유발 방지)
            var errV = Vector3.Zero;
            if (BalanceEnabled && d > DeadZone)
            {
                var e = new Vector2(zx, zz) - closest;
                var el = e.Length();
                var dir = el > 1e-6f ? (d > 0f ? e : -e) / el : Vector2.Zero;   // 바깥 방향 단위 벡터
                errV = new Vector3(dir.X, 0f, dir.Y) * MathF.Min(d - DeadZone, 0.3f);
            }
            _errLp += (errV - _errLp) * (dt / 0.3f);
            // 동역학 필터(카트-테이블 역산): 이탈 e 만큼 ZMP 를 되돌리는 보정 가속 Δa = (g/h)·Kz·e → CoM 보정 이중 적분(임계 감쇠 복원)
            if (DynamicFilter && BalanceEnabled)
            {
                var ev = d > 0f ? new Vector3(zx - closest.X, 0f, zz - closest.Y) : Vector3.Zero;
                var hgt = MathF.Max(cm.Y - gy, 0.3f);
                var da = ev * (G / hgt * FilterGain) - _dc * (FilterOmega * FilterOmega) - _dv * (2f * FilterOmega);
                _dv += da * dt; _dc += _dv * dt;
                var dl = new Vector2(_dc.X, _dc.Z).Length();
                if (dl > MaxShift) { _dc *= MaxShift / dl; _dv *= 0.5f; }
            }
            // 접지력 분배(발목 선분 투영 비)
            if ((ct & 3) == 3)
            {
                var l = _ankR[mid * 2]; var r = _ankR[mid * 2 + 1];
                var lr = new Vector2(r.X - l.X, r.Z - l.Z);
                var tt = Math.Clamp(Vector2.Dot(new Vector2(zx - l.X, zz - l.Z), lr) / MathF.Max(lr.LengthSquared(), 1e-6f), 0f, 1f);
                ForceR = tt * f; ForceL = f - ForceR;
            }
            else if ((ct & 1) != 0) { ForceL = f; ForceR = 0f; }
            else { ForceR = f; ForceL = 0f; }
            RequiredFriction = BipedSkeleton.BodyMass * MathF.Sqrt(_acc.X * _acc.X + _acc.Z * _acc.Z) / MathF.Max(f, 1f);
            if (RequiredFriction > surface.Friction) SlipRiskFrames++;
        }
        else
        {
            ZmpOffset = 0f; ForceL = ForceR = 0f; RequiredFriction = 0f;
            _errLp -= _errLp * (dt / 0.3f);
            if (DynamicFilter) { var da = -_dc * (FilterOmega * FilterOmega) - _dv * (2f * FilterOmega); _dv += da * dt; _dc += _dv * dt; }
        }
        _shift = BalanceEnabled ? -_errLp * BalanceGain : Vector3.Zero;
        var sl = new Vector2(_shift.X, _shift.Z).Length();
        if (sl > MaxShift) _shift *= MaxShift / sl;
    }

    private static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    /// <summary>모노톤 체인 볼록 껍질(CCW · 삽입 정렬 · 점 ≤ 8).</summary>
    public static int ConvexHull(Span<Vector2> p, Span<Vector2> hull)
    {
        for (var i = 1; i < p.Length; i++)
        {
            var v = p[i]; var j = i - 1;
            while (j >= 0 && (p[j].X > v.X || (p[j].X == v.X && p[j].Y > v.Y))) { p[j + 1] = p[j]; j--; }
            p[j + 1] = v;
        }
        var k = 0;
        for (var i = 0; i < p.Length; i++)
        {
            while (k >= 2 && Cross(hull[k - 2], hull[k - 1], p[i]) <= 0f) k--;
            hull[k++] = p[i];
        }
        for (int i = p.Length - 2, t = k + 1; i >= 0; i--)
        {
            while (k >= t && Cross(hull[k - 2], hull[k - 1], p[i]) <= 0f) k--;
            hull[k++] = p[i];
        }
        return Math.Max(0, k - 1);
    }

    /// <summary>CCW 다각형 부호 거리(내부 음수) + 최근접 경계점(내부여도 경계).</summary>
    public static float SignedDistance(ReadOnlySpan<Vector2> poly, Vector2 q, out Vector2 closest)
    {
        var inside = true; var best = float.MaxValue; closest = q;
        for (var i = 0; i < poly.Length; i++)
        {
            var a = poly[i]; var b = poly[(i + 1) % poly.Length];
            if (Cross(a, b, q) < 0f) inside = false;
            var ab = b - a;
            var t = Math.Clamp(Vector2.Dot(q - a, ab) / MathF.Max(ab.LengthSquared(), 1e-12f), 0f, 1f);
            var c = a + ab * t;
            var d = (q - c).Length();
            if (d < best) { best = d; closest = c; }
        }
        return inside ? -best : best;
    }
}
