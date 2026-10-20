using System.Numerics;
using System.Runtime.InteropServices;
using PromptFlowStudio.Services.Mocap;

namespace PromptFlowStudio.Services.Continuity;

public enum CutKind : byte { Continuous, TimeJump, Neutral }

public enum ViolationKind : byte
{
    PropMoved, PropMissing, PropAppeared, LightDirection, LightColor, LightIntensity,
    WardrobeWetness, WardrobeWrinkle, WardrobeDamage, WardrobeSwap, WoundMissing, WoundMoved, WoundAppeared, Makeup,
    PoseMismatch, AxisCrossed, StoryBible,
}

/// <summary>연속성 위반(값 형식): 종류 · 심각도 0..1 · 대상(소품/캐릭터 인덱스) · 크기(m · ° · K 등) · 샷 번호.</summary>
public readonly record struct ContinuityViolation(ViolationKind Kind, float Severity, int Entity, float Magnitude, int Shot);

/// <summary>상처/메이크업 마크(관절 기준 국소 오프셋).</summary>
public struct BodyMark
{
    public byte Joint, Kind;
    public bool Present;
    public Vector3 Offset;
}

/// <summary>샷 경계 상태 스냅숏(선할당 · CopyFrom 무할당).</summary>
public sealed class ContinuitySnapshot
{
    public const int MaxProps = 32, MaxCharacters = 4, Joints = MocapSkeleton.Body, MaxMarks = 8;

    public readonly float[] PropX = new float[MaxProps], PropY = new float[MaxProps], PropZ = new float[MaxProps], PropYaw = new float[MaxProps];
    public readonly bool[] PropPresent = new bool[MaxProps], PropHandled = new bool[MaxProps];
    public readonly float[] JointX = new float[MaxCharacters * Joints], JointY = new float[MaxCharacters * Joints], JointZ = new float[MaxCharacters * Joints];
    public readonly bool[] CharPresent = new bool[MaxCharacters];
    public readonly float[] Wetness = new float[MaxCharacters], Wrinkle = new float[MaxCharacters], Damage = new float[MaxCharacters], Makeup = new float[MaxCharacters];
    public readonly int[] Outfit = new int[MaxCharacters];
    public readonly BodyMark[] Marks = new BodyMark[MaxCharacters * MaxMarks];
    public Vector3 KeyLight = Vector3.Normalize(new Vector3(0.4f, 0.8f, 0.3f)), Camera, CameraForward = Vector3.UnitZ;
    public float ColorTempK = 5600f, Intensity = 1f, Precipitation, StoryTimeHours;
    public int PropCount, Shot;

    public void CopyFrom(ContinuitySnapshot s)
    {
        s.PropX.AsSpan().CopyTo(PropX); s.PropY.AsSpan().CopyTo(PropY); s.PropZ.AsSpan().CopyTo(PropZ); s.PropYaw.AsSpan().CopyTo(PropYaw);
        s.PropPresent.AsSpan().CopyTo(PropPresent); s.PropHandled.AsSpan().CopyTo(PropHandled);
        s.JointX.AsSpan().CopyTo(JointX); s.JointY.AsSpan().CopyTo(JointY); s.JointZ.AsSpan().CopyTo(JointZ);
        s.CharPresent.AsSpan().CopyTo(CharPresent); s.Wetness.AsSpan().CopyTo(Wetness); s.Wrinkle.AsSpan().CopyTo(Wrinkle);
        s.Damage.AsSpan().CopyTo(Damage); s.Makeup.AsSpan().CopyTo(Makeup); s.Outfit.AsSpan().CopyTo(Outfit); s.Marks.AsSpan().CopyTo(Marks);
        KeyLight = s.KeyLight; Camera = s.Camera; CameraForward = s.CameraForward; ColorTempK = s.ColorTempK; Intensity = s.Intensity;
        Precipitation = s.Precipitation; StoryTimeHours = s.StoryTimeHours; PropCount = s.PropCount; Shot = s.Shot;
    }

    public Vector3 Prop(int i) => new(PropX[i], PropY[i], PropZ[i]);
    public void SetProp(int i, Vector3 p) { PropX[i] = p.X; PropY[i] = p.Y; PropZ[i] = p.Z; }
    public Vector3 Joint(int c, int j) => new(JointX[c * Joints + j], JointY[c * Joints + j], JointZ[c * Joints + j]);
    public void SetJoint(int c, int j, Vector3 p) { var k = c * Joints + j; JointX[k] = p.X; JointY[k] = p.Y; JointZ[k] = p.Z; }
    public ref BodyMark Mark(int c, int m) => ref Marks[c * MaxMarks + m];
}

/// <summary>연속성 SIMD 커널(Vector&lt;float&gt; · 꼬리 스칼라 — 스칼라 판과 비트 일치).</summary>
public static class ContinuitySimd
{
    private static int Body(int n) => Vector.IsHardwareAccelerated && n >= Vector<float>.Count ? n - n % Vector<float>.Count : 0;

    /// <summary>dst = |a − b|²(3 성분 SoA).</summary>
    public static void DistanceSquared(ReadOnlySpan<float> ax, ReadOnlySpan<float> ay, ReadOnlySpan<float> az, ReadOnlySpan<float> bx, ReadOnlySpan<float> by, ReadOnlySpan<float> bz, Span<float> dst)
    {
        var n = dst.Length; var m = Body(n);
        if (m > 0)
        {
            var vax = MemoryMarshal.Cast<float, Vector<float>>(ax[..m]); var vay = MemoryMarshal.Cast<float, Vector<float>>(ay[..m]); var vaz = MemoryMarshal.Cast<float, Vector<float>>(az[..m]);
            var vbx = MemoryMarshal.Cast<float, Vector<float>>(bx[..m]); var vby = MemoryMarshal.Cast<float, Vector<float>>(by[..m]); var vbz = MemoryMarshal.Cast<float, Vector<float>>(bz[..m]);
            var d = MemoryMarshal.Cast<float, Vector<float>>(dst[..m]);
            for (var k = 0; k < d.Length; k++) { var x = vax[k] - vbx[k]; var y = vay[k] - vby[k]; var z = vaz[k] - vbz[k]; d[k] = x * x + y * y + z * z; }
        }
        for (var i = m; i < n; i++) { var x = ax[i] - bx[i]; var y = ay[i] - by[i]; var z = az[i] - bz[i]; dst[i] = x * x + y * y + z * z; }
    }

    public static void DistanceSquaredScalar(ReadOnlySpan<float> ax, ReadOnlySpan<float> ay, ReadOnlySpan<float> az, ReadOnlySpan<float> bx, ReadOnlySpan<float> by, ReadOnlySpan<float> bz, Span<float> dst)
    {
        for (var i = 0; i < dst.Length; i++) { var x = ax[i] - bx[i]; var y = ay[i] - by[i]; var z = az[i] - bz[i]; dst[i] = x * x + y * y + z * z; }
    }

    /// <summary>dst += (target − dst)·k(보정 완화).</summary>
    public static void Approach(Span<float> dst, ReadOnlySpan<float> target, float k)
    {
        var n = dst.Length; var m = Body(n);
        if (m > 0)
        {
            var d = MemoryMarshal.Cast<float, Vector<float>>(dst[..m]); var t = MemoryMarshal.Cast<float, Vector<float>>(target[..m]);
            var vk = new Vector<float>(k);
            for (var j = 0; j < d.Length; j++) d[j] = d[j] + (t[j] - d[j]) * vk;
        }
        for (var i = m; i < n; i++) dst[i] = dst[i] + (target[i] - dst[i]) * k;
    }

    public static void ApproachScalar(Span<float> dst, ReadOnlySpan<float> target, float k)
    {
        for (var i = 0; i < dst.Length; i++) dst[i] = dst[i] + (target[i] - dst[i]) * k;
    }
}

/// <summary>
/// [PromptFlow Studio v16.88.0] 📋 실시간 자율 스크립트 수퍼바이저 연속성 추적 엔진(무할당 검증 · 비동기 1슬롯 제출).
///   ▸ 샷 경계 비교(이전 샷 끝 ↔ 다음 샷 시작): 소품 위치(SoA SIMD 거리² · 취급 소품 면제) · 존재/소실 · 키 라이트 방향(태양 이동 15°/h 허용) · 색온도(Mired) · 강도(EV) ·
///     의상 젖음(강수 없이 증가 · 건조 속도 초과 감소) · 주름 · 손상(감소 = 불가) · 의상 교체 · 상처/메이크업 마크(소실 · 좌우 반전/이동 · 무사건 출현) ·
///     연속 액션 포즈(관절 SIMD 거리² · 매치 컷) · 180° 축(연속 컷 · 액션 라인 기준 카메라 측 반전).
///   ▸ 컷 종류: 연속(엄격) · 시간 경과(포즈 면제 · 허용치 완화) · 중립(축 재설정).
///   ▸ 점수: 위반 심각도 합 → 위반 스코어 0..100(1 − exp(−Σ)) · 연속성 점수 = 100 − 위반 · 위반 링 128 · 종류별 누계.
/// </summary>
public sealed class RealtimeScriptSupervisorContinuityEngine
{
    public const int MaxViolations = 128, KindCount = (int)ViolationKind.StoryBible + 1;
    private const int Jn = ContinuitySnapshot.MaxCharacters * ContinuitySnapshot.Joints;

    private readonly object _gate = new();
    private readonly float[] _propD = new float[ContinuitySnapshot.MaxProps], _jointD = new float[Jn];
    private readonly ContinuityViolation[] _ring = new ContinuityViolation[MaxViolations];
    private readonly ContinuityViolation[] _last = new ContinuityViolation[MaxViolations];
    private readonly long[] _kindTotals = new long[KindCount];
    private readonly ContinuitySnapshot _pendA = new(), _pendB = new();
    private int _head, _count, _lastCount, _inFlight;
    private CutKind _pendCut;

    // ── 허용치 ──
    public float PropTolerance { get; set; } = 0.03f;
    public float PoseTolerance { get; set; } = 0.08f;
    public float LightToleranceDeg { get; set; } = 5f;
    public float SunRateDegPerHour { get; set; } = 15f;
    public float ColorTolMired { get; set; } = 12f;
    public float IntensityTolEv { get; set; } = 0.35f;
    public float DryRatePerHour { get; set; } = 0.35f;
    public float WrinkleTolerance { get; set; } = 0.2f;
    public float MarkTolerance { get; set; } = 0.03f;
    public float MakeupTolerance { get; set; } = 0.2f;

    // ── 계측 ──
    public float ViolationScore { get; private set; }
    public float ContinuityScore => 100f - ViolationScore;
    public int LastViolationCount => _lastCount;
    public long Verifications { get; private set; }
    public long AsyncCompleted { get; private set; }
    public long AsyncDropped { get; private set; }
    public double LastVerifyMs { get; private set; }
    public int RingCount => _count;
    public long KindTotal(ViolationKind k) => _kindTotals[(int)k];
    public ContinuityViolation LastViolation(int i) => _last[i];
    public ContinuityViolation History(int back) => _ring[(_head - 1 - back + 2 * MaxViolations) % MaxViolations];
    public event Action? OnVerified;

    public static string KindName(ViolationKind k) => k switch
    {
        ViolationKind.PropMoved => "소품 위치", ViolationKind.PropMissing => "소품 소실", ViolationKind.PropAppeared => "소품 출현",
        ViolationKind.LightDirection => "조명 방향", ViolationKind.LightColor => "조명 색온도", ViolationKind.LightIntensity => "조명 강도",
        ViolationKind.WardrobeWetness => "의상 젖음", ViolationKind.WardrobeWrinkle => "의상 주름", ViolationKind.WardrobeDamage => "의상 손상", ViolationKind.WardrobeSwap => "의상 교체",
        ViolationKind.WoundMissing => "상처 소실", ViolationKind.WoundMoved => "상처 위치", ViolationKind.WoundAppeared => "상처 출현", ViolationKind.Makeup => "메이크업",
        ViolationKind.PoseMismatch => "포즈 불일치", ViolationKind.AxisCrossed => "180° 축 위반", _ => "스토리 바이블",
    };

    public static int Category(ViolationKind k) => k switch
    {
        <= ViolationKind.PropAppeared => 0, <= ViolationKind.LightIntensity => 1, <= ViolationKind.WardrobeSwap => 2, <= ViolationKind.Makeup => 3, <= ViolationKind.AxisCrossed => 4, _ => 5,
    };

    public static float Mired(float k) => 1e6f / MathF.Max(k, 1000f);

    private void Add(ViolationKind kind, float sev, int entity, float mag, int shot)
    {
        if (_lastCount >= MaxViolations) return;
        var v = new ContinuityViolation(kind, Math.Clamp(sev, 0f, 1f), entity, mag, shot);
        _last[_lastCount++] = v;
        _ring[_head] = v; _head = (_head + 1) % MaxViolations; if (_count < MaxViolations) _count++;
        _kindTotals[(int)kind]++;
    }

    /// <summary>외부(스토리 바이블) 위반 합류 — 동일 점수 체계.</summary>
    public void Merge(ViolationKind kind, float severity, int entity, float magnitude, int shot)
    {
        lock (_gate)
        {
            Add(kind, severity, entity, magnitude, shot);
            Rescore();
        }
    }

    private void Rescore()
    {
        float s = 0f;
        for (var i = 0; i < _lastCount; i++) s += _last[i].Severity;
        ViolationScore = 100f * (1f - MathF.Exp(-s));
    }

    /// <summary>동기 검증(무할당): prev = 이전 샷 끝 · cur = 다음 샷 시작.</summary>
    public int Verify(ContinuitySnapshot prev, ContinuitySnapshot cur, CutKind cut)
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _lastCount = 0;
            var shot = cur.Shot;
            var gap = MathF.Max(0f, cur.StoryTimeHours - prev.StoryTimeHours);
            var relax = cut == CutKind.Continuous ? 1f : 1.5f;

            // ── 소품 ──
            var np = Math.Max(prev.PropCount, cur.PropCount);
            ContinuitySimd.DistanceSquared(prev.PropX.AsSpan(0, np), prev.PropY.AsSpan(0, np), prev.PropZ.AsSpan(0, np), cur.PropX.AsSpan(0, np), cur.PropY.AsSpan(0, np), cur.PropZ.AsSpan(0, np), _propD.AsSpan(0, np));
            var pt2 = PropTolerance * relax; pt2 *= pt2;
            for (var i = 0; i < np; i++)
            {
                bool a = prev.PropPresent[i], b = cur.PropPresent[i];
                if (a && !b) { if (!prev.PropHandled[i]) Add(ViolationKind.PropMissing, 0.5f, i, 0f, shot); continue; }
                if (!a && b) { if (!cur.PropHandled[i]) Add(ViolationKind.PropAppeared, 0.4f, i, 0f, shot); continue; }
                if (!a || cur.PropHandled[i] || prev.PropHandled[i]) continue;
                if (_propD[i] > pt2) { var d = MathF.Sqrt(_propD[i]); Add(ViolationKind.PropMoved, MathF.Min(1f, 0.15f + d * 1.5f), i, d, shot); }
                else
                {
                    var dyaw = MathF.Abs(MathF.IEEERemainder(cur.PropYaw[i] - prev.PropYaw[i], 360f));
                    if (dyaw > 10f * relax) Add(ViolationKind.PropMoved, MathF.Min(1f, dyaw / 90f), i, dyaw, shot);
                }
            }

            // ── 조명 ──
            var ang = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(prev.KeyLight), Vector3.Normalize(cur.KeyLight)), -1f, 1f)) * 180f / MathF.PI;
            var allowAng = LightToleranceDeg + (cut == CutKind.TimeJump ? SunRateDegPerHour * gap : 0f);
            if (ang > allowAng) Add(ViolationKind.LightDirection, MathF.Min(1f, (ang - allowAng) / 30f + 0.2f), -1, ang, shot);
            var dm = MathF.Abs(Mired(cur.ColorTempK) - Mired(prev.ColorTempK));
            var allowM = ColorTolMired * relax + (cut == CutKind.TimeJump ? 20f * gap : 0f);
            if (dm > allowM) Add(ViolationKind.LightColor, MathF.Min(1f, (dm - allowM) / 60f + 0.2f), -1, cur.ColorTempK - prev.ColorTempK, shot);
            var dev = MathF.Abs(MathF.Log2(MathF.Max(cur.Intensity, 1e-4f) / MathF.Max(prev.Intensity, 1e-4f)));
            var allowEv = IntensityTolEv * relax + (cut == CutKind.TimeJump ? 0.5f * gap : 0f);
            if (dev > allowEv) Add(ViolationKind.LightIntensity, MathF.Min(1f, (dev - allowEv) / 2f + 0.15f), -1, dev, shot);

            // ── 캐릭터: 의상 · 마크 · 메이크업 ──
            for (var c = 0; c < ContinuitySnapshot.MaxCharacters; c++)
            {
                if (!prev.CharPresent[c] || !cur.CharPresent[c]) continue;
                var swapped = prev.Outfit[c] != cur.Outfit[c];
                if (swapped && (cut == CutKind.Continuous || gap < 0.5f)) Add(ViolationKind.WardrobeSwap, 0.7f, c, cur.Outfit[c], shot);
                if (!swapped)
                {
                    var dw = cur.Wetness[c] - prev.Wetness[c];
                    var raining = cur.Precipitation > 0.1f || prev.Precipitation > 0.1f;
                    if (dw > 0.1f * relax && !raining) Add(ViolationKind.WardrobeWetness, MathF.Min(1f, dw + 0.2f), c, dw, shot);
                    else if (-dw > 0.1f * relax + DryRatePerHour * gap) Add(ViolationKind.WardrobeWetness, MathF.Min(1f, -dw + 0.2f), c, dw, shot);
                    var dr = MathF.Abs(cur.Wrinkle[c] - prev.Wrinkle[c]);
                    if (cut == CutKind.Continuous && dr > WrinkleTolerance) Add(ViolationKind.WardrobeWrinkle, MathF.Min(1f, dr), c, dr, shot);
                    var dd = cur.Damage[c] - prev.Damage[c];
                    if (dd < -0.05f) Add(ViolationKind.WardrobeDamage, MathF.Min(1f, -dd + 0.3f), c, dd, shot);
                }
                var mk = MathF.Abs(cur.Makeup[c] - prev.Makeup[c]);
                if (mk > MakeupTolerance * relax && gap < 2f) Add(ViolationKind.Makeup, MathF.Min(1f, mk), c, mk, shot);
                for (var m = 0; m < ContinuitySnapshot.MaxMarks; m++)
                {
                    ref readonly var a = ref prev.Mark(c, m); ref readonly var b = ref cur.Mark(c, m);
                    if (a.Present && !b.Present) { if (gap < 72f) Add(ViolationKind.WoundMissing, 0.8f, c * ContinuitySnapshot.MaxMarks + m, 0f, shot); }
                    else if (!a.Present && b.Present) Add(ViolationKind.WoundAppeared, 0.6f, c * ContinuitySnapshot.MaxMarks + m, 0f, shot);
                    else if (a.Present)
                    {
                        var d = a.Joint != b.Joint ? 1f : Vector3.Distance(a.Offset, b.Offset);
                        if (d > MarkTolerance) Add(ViolationKind.WoundMoved, MathF.Min(1f, 0.4f + d * 4f), c * ContinuitySnapshot.MaxMarks + m, d, shot);
                    }
                }
            }

            // ── 연속 액션 포즈(매치 컷) ──
            if (cut == CutKind.Continuous)
            {
                ContinuitySimd.DistanceSquared(prev.JointX, prev.JointY, prev.JointZ, cur.JointX, cur.JointY, cur.JointZ, _jointD);
                var t2 = PoseTolerance * PoseTolerance;
                for (var c = 0; c < ContinuitySnapshot.MaxCharacters; c++)
                {
                    if (!prev.CharPresent[c] || !cur.CharPresent[c]) continue;
                    float worst = 0f; var bad = 0;
                    for (var j = 0; j < ContinuitySnapshot.Joints; j++)
                    {
                        var d = _jointD[c * ContinuitySnapshot.Joints + j];
                        if (d > t2) bad++;
                        worst = MathF.Max(worst, d);
                    }
                    if (bad > 0) Add(ViolationKind.PoseMismatch, MathF.Min(1f, 0.2f + MathF.Sqrt(worst) * 2f), c, MathF.Sqrt(worst), shot);
                }
            }

            // ── 180° 축 ──
            if (cut == CutKind.Continuous && prev.CharPresent[0] && prev.CharPresent[1] && cur.CharPresent[0] && cur.CharPresent[1])
            {
                var sa = AxisSide(prev); var sb = AxisSide(cur);
                if (sa * sb < 0f) Add(ViolationKind.AxisCrossed, 0.6f, -1, 0f, shot);
            }

            Rescore();
            Verifications++;
            LastVerifyMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            return _lastCount;
        }
    }

    /// <summary>액션 라인(캐릭터 0 → 1 골반) 기준 카메라 측 부호.</summary>
    public static float AxisSide(ContinuitySnapshot s)
    {
        var a = s.Joint(0, MocapSkeleton.Pelvis); var b = s.Joint(1, MocapSkeleton.Pelvis);
        var line = b - a; var toCam = s.Camera - a;
        return line.X * toCam.Z - line.Z * toCam.X;
    }

    /// <summary>비동기 제출(1슬롯 백프레셔 · 스냅숏 내부 복사 무할당 · 진행 중이면 드롭).</summary>
    public bool Submit(ContinuitySnapshot prev, ContinuitySnapshot cur, CutKind cut)
    {
        if (Interlocked.Exchange(ref _inFlight, 1) != 0) { AsyncDropped++; return false; }
        _pendA.CopyFrom(prev); _pendB.CopyFrom(cur); _pendCut = cut;
        _ = Task.Run(RunPending);
        return true;
    }

    public bool Busy => Volatile.Read(ref _inFlight) != 0;

    private void RunPending()
    {
        try
        {
            Verify(_pendA, _pendB, _pendCut);
            AsyncCompleted++;
            try { OnVerified?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Continuity.OnVerified", ex); }
        }
        catch (Exception ex) { PromptFlowStudio.Program.Log("Continuity.Verify", ex); }
        finally { Volatile.Write(ref _inFlight, 0); }
    }
}
