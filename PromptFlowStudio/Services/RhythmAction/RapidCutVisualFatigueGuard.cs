namespace PromptFlowStudio.Services.RhythmAction;

/// <summary>MV 카메라 뱅크 — 와이드(축 수직) · 미디엄 · 클로즈업 · 오빗(짐벌 자유 궤도 — 180° 선을 넘을 수 있음).</summary>
public enum MvCam : byte { Wide, Medium, CloseUp, Orbit }

public enum RhythmCutVerdict : byte { Approved, AxisClamped, TooSoon, RateLimited, SameShot }

/// <summary>
/// [PromptFlow Studio v17.54.0] 🛡 초고속 컷 시각 피로도 &amp; 공간 방향 상실 방어 가드(<see cref="Review"/> · <see cref="Commit"/> 무할당).
/// <b>최소 유지</b>: 직전 컷 이후 0.35 s(≈ 21 프레임 @60) 미만 전환은 어떤 사유(드롭 · 악센트 포함)든 TooSoon 으로 차단한다.
/// <b>피로도</b>: 최근 3 s 컷 수가 상한(기본 7 ≈ 2.3 컷/s) 이상이면 악센트 · 드롭이 아닌 컷을 RateLimited 로 보류한다.
/// <b>180° 축</b>: 방위각(축 수직 = 0° · 선 = ±90°)이 선 근처(±82°)를 넘는 대상 카메라는 선 대칭 반사 후 ±82° 로 클램프(AxisClamped · 승인)하고,
/// 방송 중인 오빗 카메라도 <see cref="ConstrainOnAir"/> 로 선 앞에서 정지시킨다. 방송 지표(유지 위반 · 선 넘김)는 가드 ON/OFF 와 무관하게 계측한다.
/// </summary>
public sealed class RapidCutVisualFatigueGuard
{
    public const int Cams = 4, Ring = 64;
    public const float MinHoldFloorS = 0.35f, LineDeg = 90f, NearLineDeg = 8f, WindowS = 3f;
    private readonly double[] _t = new double[Ring];
    private int _head, _n;
    private double _last = double.NegativeInfinity;
    private float _hold = MinHoldFloorS;

    public bool Enabled { get; set; } = true;
    /// <summary>최소 샷 유지(s) — 0.35 s 미만으로는 내려가지 않는다.</summary>
    public float MinHoldS { get => _hold; set => _hold = MathF.Max(MinHoldFloorS, value); }
    public int MaxCutsPerWindow { get; set; } = 7;
    public long Reviews { get; private set; }
    public long Approvals { get; private set; }
    public long TooSoonRejects { get; private set; }
    public long RateRejects { get; private set; }
    public long AxisClamps { get; private set; }
    public long OrbitHolds { get; private set; }
    public long Commits { get; private set; }
    /// <summary>방송된 컷 중 직전 컷과 0.35 s 미만 간격(가드 ON 이면 0 이어야 함).</summary>
    public long OnAirHoldViolations { get; private set; }
    public long OnAirAxisCuts { get; private set; }
    public long OnAirAxisFrames { get; private set; }
    public double MinGapSeen { get; private set; } = double.PositiveInfinity;
    public double LastGap { get; private set; }
    public float CutsPerSecond { get; private set; }
    /// <summary>피로도 지수(1 = 상한 컷 빈도).</summary>
    public float FatigueIndex => CutsPerSecond * WindowS / MathF.Max(MaxCutsPerWindow, 1);
    public RhythmCutVerdict LastVerdict { get; private set; }

    public static string VerdictName(RhythmCutVerdict v) => v switch
    {
        RhythmCutVerdict.Approved => "승인", RhythmCutVerdict.AxisClamped => "축 클램프", RhythmCutVerdict.TooSoon => "유지 미달",
        RhythmCutVerdict.RateLimited => "피로도 보류", _ => "동일 샷",
    };

    public void ResetMetrics()
    {
        Reviews = Approvals = TooSoonRejects = RateRejects = AxisClamps = OrbitHolds = Commits = OnAirHoldViolations = OnAirAxisCuts = OnAirAxisFrames = 0;
        MinGapSeen = double.PositiveInfinity; LastGap = 0; CutsPerSecond = 0;
    }

    public void Reset() { ResetMetrics(); Array.Clear(_t); _head = _n = 0; _last = double.NegativeInfinity; }

    public static bool Crosses(float azDeg) => MathF.Abs(azDeg) > LineDeg;

    /// <summary>선 너머(|az| &gt; 90°)면 선 대칭 반사 → ±(90 − 8)° 클램프(부호 = 카메라 측 유지).</summary>
    public static float ClampToSide(float azDeg)
    {
        var a = MathF.Abs(azDeg);
        if (a > 180f) a = 360f - a;
        if (a > LineDeg) a = 180f - a;
        return MathF.CopySign(MathF.Min(a, LineDeg - NearLineDeg), azDeg);
    }

    public int CutsSince(double t)
    {
        var c = 0;
        for (var i = 0; i < _n; i++) if (_t[i] > t) c++;
        return c;
    }

    /// <summary>컷 심사(무할당) — 승인/축 클램프면 호출자가 <see cref="Commit"/>. azDeg 는 클램프 시 교정된다.</summary>
    public RhythmCutVerdict Review(double now, int from, int to, ref float azDeg, bool accent)
    {
        Reviews++;
        RhythmCutVerdict v;
        if (to == from) v = RhythmCutVerdict.SameShot;
        else if (!Enabled) v = RhythmCutVerdict.Approved;
        else if (now - _last < _hold - 1e-9) { v = RhythmCutVerdict.TooSoon; TooSoonRejects++; }
        else if (!accent && CutsSince(now - WindowS) >= MaxCutsPerWindow) { v = RhythmCutVerdict.RateLimited; RateRejects++; }
        else if (MathF.Abs(azDeg) > LineDeg - NearLineDeg) { azDeg = ClampToSide(azDeg); v = RhythmCutVerdict.AxisClamped; AxisClamps++; }
        else v = RhythmCutVerdict.Approved;
        if (v is RhythmCutVerdict.Approved or RhythmCutVerdict.AxisClamped) Approvals++;
        LastVerdict = v;
        return v;
    }

    public void Commit(double now, int to, float azDeg)
    {
        if (_n > 0)
        {
            var gap = now - _last;
            LastGap = gap;
            if (gap < MinGapSeen) MinGapSeen = gap;
            if (gap < MinHoldFloorS - 1e-9) OnAirHoldViolations++;
        }
        if (Crosses(azDeg)) OnAirAxisCuts++;
        _t[_head] = now; _head = (_head + 1) % Ring; if (_n < Ring) _n++;
        _last = now; Commits++;
        CutsPerSecond = CutsSince(now - WindowS) / WindowS;
    }

    /// <summary>방송 중 카메라 방위 관측(프레임) — 가드 ON 이면 선 앞에서 정지시킨 방위를 돌려준다.</summary>
    public float ConstrainOnAir(float azDeg)
    {
        if (Enabled && MathF.Abs(azDeg) > LineDeg - NearLineDeg) { azDeg = ClampToSide(azDeg); OrbitHolds++; }
        if (Crosses(azDeg)) OnAirAxisFrames++;
        return azDeg;
    }

    /// <summary>프레임 시계로 컷 빈도 갱신(컷이 없을 때 감쇠 표시용).</summary>
    public void Observe(double now) => CutsPerSecond = CutsSince(now - WindowS) / WindowS;
}
