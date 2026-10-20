using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PromptFlowStudio.Services.CognitiveCinematography;

/// <summary>스프링/CoC 커널 경로: 스칼라 · Vector256(비트 동일).</summary>
public enum CineSimdPath : byte { Scalar, V256 }

/// <summary>
/// [PromptFlow Studio v17.21.0] 🎥 실시간 인지 카메라 프레이밍 &amp; 시맨틱 블로킹 엔진(무할당 · 결정적).
///   ▸ 감정 강도(v17.00 디렉터 각성 0.55 + 긴장 0.45) → 초점 거리 24–135 mm(지수 1.3) · 프레임 높이 2.3 m(풀숏) → 0.42 m(클로즈업) · 긴장 → 로우 앵글/더치.
///   ▸ 삼분할: 피사체 시선(v17.12 안구 요 + 몸 방향)이 화면 우측이면 좌측 1/3 에 배치(리드 룸) · 눈높이 = 상단 1/3(헤드 룸) — 2 회 반복 조준 보정.
///   ▸ 궤적: 위치 3 · 조준점 3 · 초점 거리 · 더치 8 레인을 임계 감쇠 스프링-댐퍼(c = 2√k)로 60 Hz 적분 — Vector256 1 회 연산(스칼라와 비트 동일).
///   ▸ 가림 가드(<see cref="AutonomousFocusPullingOcclusionGuard"/>)의 미끄러짐 요 오프셋을 궤도 각에 합산.
/// </summary>
public sealed class CognitiveCameraFramingBlockingEngine
{
    public const int Lanes = 8;
    public const float SensorW = 36f, SensorH = 20.25f, MinFocal = 24f, MaxFocal = 135f, Third = 1f / 3f;
    public const float WideFrameM = 2.3f, CloseFrameM = 0.42f, ThreeQuarterDeg = 32f;

    private readonly float[] _x = new float[Lanes], _v = new float[Lanes], _t = new float[Lanes], _k = new float[Lanes], _c = new float[Lanes];
    private bool _primed;

    public CognitiveCameraFramingBlockingEngine()
    {
        ReadOnlySpan<float> k = stackalloc float[Lanes] { 7f, 7f, 7f, 16f, 16f, 16f, 5f, 3f };
        for (var i = 0; i < Lanes; i++) { _k[i] = k[i]; _c[i] = 2f * MathF.Sqrt(k[i]); }
        _x[6] = 50f;
        Basis();
    }

    public CineSimdPath Path { get; set; } = CineSimdPath.V256;
    public bool AutoFocal { get; set; } = true;
    public float ManualFocalMm { get; set; } = 50f;
    public bool AutoSubject { get; set; } = true;
    public int Subject { get; set; }
    public float DutchScale { get; set; } = 1f;
    /// <summary>가림 회피 중 — 위치 레인 강성 7 → 36(빠른 미끄러짐).</summary>
    public bool Urgent { get; set; }
    public float Intensity { get; private set; }
    public float TargetFocalMm { get; private set; } = 50f;
    public float FocalMm => _x[6];
    public float DutchDeg => _x[7];
    public Vector3 Position => new(_x[0], _x[1], _x[2]);
    public Vector3 Aim => new(_x[3], _x[4], _x[5]);
    public Vector3 TargetPosition => new(_t[0], _t[1], _t[2]);
    public Vector3 Forward { get; private set; }
    public Vector3 Right { get; private set; }
    public Vector3 Up { get; private set; }
    public float TanH { get; private set; }
    public float TanV { get; private set; }
    public float OrbitYawDeg { get; private set; }
    public float Distance { get; private set; }
    public bool GazeScreenRight { get; private set; }
    public float TargetX { get; private set; } = Third;
    public float TargetY { get; private set; } = Third;
    public float EyeX { get; private set; } = 0.5f;
    public float EyeY { get; private set; } = 0.5f;
    public float HeadTopY { get; private set; }
    public float FramingError { get; private set; }
    public float LeadRoom { get; private set; }
    public float Speed { get; private set; }
    public double LastFrameUs { get; private set; }
    public long Updates { get; private set; }

    public string ShotName => FocalMm switch { >= 105f => "ECU 익스트림 클로즈업", >= 75f => "CU 클로즈업", >= 50f => "MCU 미디엄 클로즈업", >= 35f => "MS 미디엄", _ => "WS 와이드" };

    public float Lane(int i) => _x[i];
    public float LaneVelocity(int i) => _v[i];
    public void SetLaneState(int i, float x, float v, float target) { _x[i] = x; _v[i] = v; _t[i] = target; }

    /// <summary>강도 → 초점 거리(mm).</summary>
    public static float FocalForIntensity(float intensity) => MinFocal + (MaxFocal - MinFocal) * MathF.Pow(Math.Clamp(intensity, 0f, 1f), 1.3f);

    /// <summary>1틱(무할당): 목표 산출 → 스프링 적분 → 기저 · 구도 계측.</summary>
    public void Update(CinematicBlockingSet set, float dt, float arousal, float tension, float dutchDeg, float slideYawDeg)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Intensity = Math.Clamp(0.55f * arousal + 0.45f * tension, 0f, 1f);
        TargetFocalMm = AutoFocal ? FocalForIntensity(Intensity) : Math.Clamp(ManualFocalMm, MinFocal, MaxFocal);
        var s = Math.Clamp(Subject, 0, CinematicBlockingSet.Actors - 1);
        var eye = set.Eye(s);
        var gaze = set.Gaze(s);
        var gh = Vector3.Normalize(new Vector3(gaze.X, 0f, gaze.Z) + new Vector3(1e-5f, 0f, 0f));

        // 프레이밍 높이 · 거리(목표 초점 거리 기준 → 평형에서 위치와 화각 일치)
        var tanV = SensorH * 0.5f / TargetFocalMm;
        var frame = WideFrameM + (CloseFrameM - WideFrameM) * MathF.Pow(Intensity, 0.8f);
        var dist = frame * 0.5f / tanV;
        // 시선 기준 3/4 정면 + 가림 미끄러짐 오프셋
        var orbit = MathF.Atan2(gh.X, gh.Z) + (ThreeQuarterDeg + slideYawDeg) * (MathF.PI / 180f);
        OrbitYawDeg = orbit * (180f / MathF.PI);
        var height = eye.Y + 0.12f - 0.4f * tension;
        var cam = new Vector3(eye.X + MathF.Sin(orbit) * dist, height, eye.Z + MathF.Cos(orbit) * dist);
        // 삼분할 조준: 시선 쪽에 리드 룸 · 눈높이 상단 1/3 — 기저 2 회 반복
        var tanH = tanV * (SensorW / SensorH);
        var aim = eye;
        for (var it = 0; it < 2; it++)
        {
            var f = Vector3.Normalize(aim - cam);
            var r = Vector3.Normalize(Vector3.Cross(f, Vector3.UnitY));
            var u = Vector3.Cross(r, f);
            GazeScreenRight = Vector3.Dot(gh, r) >= 0f;
            TargetX = GazeScreenRight ? Third : 1f - Third;
            float nx = (TargetX - 0.5f) * 2f, ny = (0.5f - TargetY) * 2f;
            var d = Vector3.Dot(eye - cam, f);
            aim = eye - r * (nx * tanH * d) - u * (ny * tanV * d);
        }
        _t[0] = cam.X; _t[1] = cam.Y; _t[2] = cam.Z; _t[3] = aim.X; _t[4] = aim.Y; _t[5] = aim.Z; _t[6] = TargetFocalMm; _t[7] = dutchDeg * DutchScale;
        var kp = Urgent ? 36f : 7f;
        if (_k[0] != kp) { for (var i = 0; i < 3; i++) { _k[i] = kp; _c[i] = 2f * MathF.Sqrt(kp); } }
        if (!_primed) { for (var i = 0; i < Lanes; i++) { _x[i] = _t[i]; _v[i] = 0f; } _primed = true; }
        else if (Path == CineSimdPath.V256 && Vector256.IsHardwareAccelerated) SpringV256(dt);
        else SpringScalar(dt);
        Basis();
        Distance = Vector3.Distance(Position, eye);
        Speed = new Vector3(_v[0], _v[1], _v[2]).Length();

        Project(eye, out var ex, out var ey, out _);
        Project(set.HeadCenter(s) + Up * CinematicBlockingSet.HeadRadius, out _, out var hy, out _);
        EyeX = ex; EyeY = ey; HeadTopY = hy;
        FramingError = MathF.Sqrt((ex - TargetX) * (ex - TargetX) + (ey - TargetY) * (ey - TargetY));
        LeadRoom = GazeScreenRight ? 1f - ex : ex;
        Updates++;
        LastFrameUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>임계 감쇠 스프링(스칼라): v += (k(t − x) − c·v)·dt · x += v·dt.</summary>
    public void SpringScalar(float dt)
    {
        for (var i = 0; i < Lanes; i++)
        {
            var a = _k[i] * (_t[i] - _x[i]) - _c[i] * _v[i];
            var v = _v[i] + a * dt;
            _v[i] = v;
            _x[i] = _x[i] + v * dt;
        }
    }

    /// <summary>임계 감쇠 스프링(Vector256 8 레인 1 회 · 스칼라와 동일 연산 순서).</summary>
    public void SpringV256(float dt)
    {
        ref var x0 = ref MemoryMarshal.GetArrayDataReference(_x);
        ref var v0 = ref MemoryMarshal.GetArrayDataReference(_v);
        var x = Vector256.LoadUnsafe(ref x0);
        var v = Vector256.LoadUnsafe(ref v0);
        var t = Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(_t));
        var k = Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(_k));
        var c = Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(_c));
        var d = Vector256.Create(dt);
        var a = k * (t - x) - c * v;
        v = v + a * d;
        x = x + v * d;
        v.StoreUnsafe(ref v0);
        x.StoreUnsafe(ref x0);
    }

    private void Basis()
    {
        var f = Vector3.Normalize(Aim - Position);
        if (!float.IsFinite(f.X)) f = -Vector3.UnitZ;
        var r = Vector3.Normalize(Vector3.Cross(f, Vector3.UnitY));
        var u = Vector3.Cross(r, f);
        var dr = DutchDeg * (MathF.PI / 180f);
        float cs = MathF.Cos(dr), sn = MathF.Sin(dr);
        Forward = f; Right = r * cs + u * sn; Up = u * cs - r * sn;
        TanV = SensorH * 0.5f / MathF.Max(1f, FocalMm);
        TanH = TanV * (SensorW / SensorH);
    }

    /// <summary>월드 점 → 정규 화면(0..1, 좌상단 원점) · z 깊이.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Project(Vector3 p, out float sx, out float sy, out float z)
    {
        var q = p - Position;
        z = Vector3.Dot(q, Forward);
        var iz = 1f / MathF.Max(z, 1e-4f);
        sx = 0.5f + 0.5f * Vector3.Dot(q, Right) * iz / TanH;
        sy = 0.5f - 0.5f * Vector3.Dot(q, Up) * iz / TanV;
    }

    /// <summary>정규 화면 좌표 → 광선 방향.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector3 Ray(float sx, float sy) => Vector3.Normalize(Forward + Right * ((sx - 0.5f) * 2f * TanH) + Up * ((0.5f - sy) * 2f * TanV));
}
