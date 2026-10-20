using System.Numerics;
using PromptFlowStudio.Services.MotionMatching;

namespace PromptFlowStudio.Services.ContactLocomotion;

/// <summary>계획 착지점(발목 월드 위치 · 방향 쿼터니언 · 진행축 거리 · 디딤판 안전 여부).</summary>
public readonly record struct Footstep(byte Side, Vector3 Position, Quaternion Rotation, float Along, bool EdgeSafe, float Cost);

/// <summary>
/// [PromptFlow Studio v17.35.0] 👣 실시간 3D 절차적 발 배치 &amp; 단차 접촉 탐색 엔진(<see cref="Scan"/> · <see cref="Plan"/> 무할당).
///   ▸ 볼류메트릭 스캔: 진행 방향 −0.25 ~ +2.75 m(전방 2 m 이상) × 3 레인(좌 · 중 · 우 10 cm) 2 cm 수직 레이캐스트 → 레인 최대 프로파일(Vector&lt;float&gt; Max) ·
///     인접 차 |Δh|(SIMD) &gt; 3 cm = 단차 모서리 → 챌판 높이(평균 |Δh|) · 디딤판 깊이(모서리 간격 평균) · 경사각 atan(챌판/디딤판) · 승/하강 부호.
///   ▸ 발 배치: 다음 4보(좌우 교대) — 1보 후보 = DB 예측 착지 ±12 cm(1 cm) · 2~4보 = 이전 + 공칭 보폭(속도 ÷ 2·케이던스) ±24 cm(1 cm) 격자 ·
///     비용 = 보폭 편차² + 1보 DB 편차² + 모서리 걸침(뒤꿈치 −7 ~ 발끝 +17 cm 구간 내 모서리) 벌점 → 층별 최소 비용 경로(휴리스틱 0 A* = 동적 계획 · 최적).
///   ▸ 출력: 착지 발목 = 루트 + 진행축 거리 + 좌우 골반 폭 · 지면 = 발 구간 최대 높이 · 방향 = yaw(진행) × pitch(발 구간 경사).
/// </summary>
public sealed class ProceduralFootstepPlacementEngine
{
    public const int Lanes = 3, Samples = 152, Steps = 4, MaxEdges = 48, First = 25, Cand = 49;
    public const float Back = 0.25f, Res = 0.02f, LaneOffset = 0.1f, EdgeMin = 0.03f, Heel = 0.07f, Toe = 0.17f, Margin = 0.015f, EdgePenalty = 6f, FirstSigma = 0.06f, StrideSigma = 0.12f;

    private readonly float[] _h = new float[Lanes * Samples], _prof = new float[Samples], _diff = new float[Samples];
    private readonly float[] _edgeS = new float[MaxEdges], _edgeDh = new float[MaxEdges];
    private readonly float[] _cost = new float[Steps * Cand], _pos = new float[Steps * Cand];
    private readonly short[] _back = new short[Steps * Cand];
    private readonly Footstep[] _plan = new Footstep[Steps];
    private Vector3 _origin;
    private float _heading;

    public int EdgeCount { get; private set; }
    public float EdgeAlong(int i) => _edgeS[i];
    public float EdgeRise(int i) => _edgeDh[i];
    public float RiserHeight { get; private set; }
    public float TreadDepth { get; private set; }
    public float SlopeDeg { get; private set; }
    /// <summary>+1 상승 · −1 하강 · 0 평탄(전방 1 m 순 높이 변화).</summary>
    public int Direction { get; private set; }
    public float StepLength { get; private set; }
    public int PlanCount { get; private set; }
    public ReadOnlySpan<Footstep> Planned => _plan.AsSpan(0, PlanCount);
    public Vector3 ScanOrigin => _origin;
    public float ScanHeading => _heading;
    public float ProfileAt(int i) => _prof[i];
    public double LastUs { get; private set; }
    public double MaxUs { get; private set; }
    public long Plans { get; private set; }
    public long SafePlans { get; private set; }
    public float SafeRatio => Plans > 0 ? SafePlans / (float)Plans : 1f;
    /// <summary>1보 계획 ↔ DB 예측 착지 진행축 편차(m).</summary>
    public float FirstDelta { get; private set; }

    public void ResetMetrics() { Plans = SafePlans = 0; MaxUs = 0; }

    public static float Along(int i) => -Back + i * Res;

    /// <summary>전방 볼류메트릭 스캔(3 레인 · 2 cm) → 프로파일 · 모서리 · 챌판/디딤판/경사.</summary>
    public void Scan(ITerrainHeightField t, Vector3 origin, float heading)
    {
        _origin = origin; _heading = heading;
        var f = BipedSkeleton.Forward(heading); var r = BipedSkeleton.Right(heading);
        for (var l = 0; l < Lanes; l++)
        {
            var lo = (l - 1) * LaneOffset;
            var row = _h.AsSpan(l * Samples, Samples);
            for (var i = 0; i < Samples; i++)
            {
                var s = Along(i);
                row[i] = t.Height(origin.X + f.X * s + r.X * lo, origin.Z + f.Z * s + r.Z * lo);
            }
        }
        // 레인 최대(SIMD) → 보수적 프로파일
        var w = Vector<float>.Count;
        var i0 = 0;
        for (; i0 + w <= Samples; i0 += w)
        {
            var m = Vector.Max(new Vector<float>(_h, i0), Vector.Max(new Vector<float>(_h, Samples + i0), new Vector<float>(_h, 2 * Samples + i0)));
            m.CopyTo(_prof, i0);
        }
        for (; i0 < Samples; i0++) _prof[i0] = MathF.Max(_h[i0], MathF.Max(_h[Samples + i0], _h[2 * Samples + i0]));
        // 인접 차 |Δh|(SIMD)
        i0 = 0;
        for (; i0 + w < Samples; i0 += w) Vector.Abs(new Vector<float>(_prof, i0 + 1) - new Vector<float>(_prof, i0)).CopyTo(_diff, i0);
        for (; i0 < Samples - 1; i0++) _diff[i0] = MathF.Abs(_prof[i0 + 1] - _prof[i0]);
        _diff[Samples - 1] = 0f;

        var n = 0; var rise = 0f; var gap = 0f; var gaps = 0;
        for (var i = 0; i < Samples - 1 && n < MaxEdges; i++)
        {
            if (_diff[i] <= EdgeMin) continue;
            var s = Along(i) + Res * 0.5f;
            if (n > 0 && s - _edgeS[n - 1] < 0.08f) continue;   // 경사 연속 차분 → 단일 모서리
            if (n > 0) { gap += s - _edgeS[n - 1]; gaps++; }
            _edgeS[n] = s; _edgeDh[n] = _prof[i + 1] - _prof[i]; rise += _diff[i]; n++;
        }
        EdgeCount = n;
        RiserHeight = n > 0 ? rise / n : 0f;
        TreadDepth = gaps > 0 ? gap / gaps : 0f;
        SlopeDeg = TreadDepth > 0f ? MathF.Atan(RiserHeight / TreadDepth) * 57.29578f : 0f;
        var ahead = HeightAt(1f) - HeightAt(0f);
        Direction = ahead > EdgeMin ? 1 : ahead < -EdgeMin ? -1 : 0;
    }

    /// <summary>프로파일 높이(진행축 거리 · 선형 보간 · 범위 밖 클램프).</summary>
    public float HeightAt(float s)
    {
        var x = Math.Clamp((s + Back) / Res, 0f, Samples - 1.001f);
        var i = (int)x; var u = x - i;
        return _prof[i] + (_prof[i + 1] - _prof[i]) * u;
    }

    /// <summary>발 구간(뒤꿈치 −7 ~ 발끝 +17 cm · 여유 1.5 cm)에 모서리 존재 여부.</summary>
    public bool Straddles(float s)
    {
        for (var i = 0; i < EdgeCount; i++)
        {
            var e = _edgeS[i];
            if (e > s - Heel - Margin && e < s + Toe + Margin) return true;
        }
        return false;
    }

    private float NodeCost(float s) => Straddles(s) ? EdgePenalty : 0f;

    /// <summary>
    /// 다음 4보 계획(스캔 후 호출). side = 1보 발(0 좌 · 1 우) · nominal = DB 예측 1보 착지(월드) · speed = 루트 속도.
    /// </summary>
    public void Plan(byte side, Vector3 nominal, float speed)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var f = BipedSkeleton.Forward(_heading); var r = BipedSkeleton.Right(_heading);
        var d0 = nominal - _origin;
        var s0 = Vector3.Dot(d0, f); var lat0 = Vector3.Dot(d0, r);
        var cad = speed < MotionDatabase.RunSpeed ? 0.8f + 0.2f * speed : 1.3f + 0.08f * speed;
        var len = speed > 0.05f ? speed / (2f * cad) : 0f;
        StepLength = len;
        var steps = len > 0.05f ? Steps : 1;

        // 1층: DB 예측 착지 ±12 cm
        for (var i = 0; i < First; i++)
        {
            var s = s0 + (i - First / 2) * 0.01f;
            _pos[i] = s;
            var dv = (s - s0) / FirstSigma;
            _cost[i] = dv * dv + NodeCost(s);
            _back[i] = -1;
        }
        // 2~4층: 이전 층 최적 전이(보폭 편차²) — 층 중심 = 1보 + (k−1)·보폭
        for (var k = 1; k < steps; k++)
        {
            var nPrev = k == 1 ? First : Cand;
            var center = s0 + k * len;
            for (var i = 0; i < Cand; i++)
            {
                var s = center + (i - Cand / 2) * 0.01f;
                var node = NodeCost(s);
                float best = float.MaxValue; short arg = 0;
                for (var j = 0; j < nPrev; j++)
                {
                    var dv = (s - _pos[(k - 1) * Cand + j] - len) / StrideSigma;
                    var c = _cost[(k - 1) * Cand + j] + dv * dv;
                    if (c < best) { best = c; arg = (short)j; }
                }
                _pos[k * Cand + i] = s; _cost[k * Cand + i] = best + node; _back[k * Cand + i] = arg;
            }
        }
        // 역추적
        var last = steps - 1; var nl = last == 0 ? First : Cand;
        int bi = 0; var bc = float.MaxValue;
        for (var i = 0; i < nl; i++) if (_cost[last * Cand + i] < bc) { bc = _cost[last * Cand + i]; bi = i; }
        for (var k = last; k >= 0; k--)
        {
            var s = _pos[k * Cand + bi];
            var sd = (byte)((side + k) & 1);
            var lat = k == 0 ? lat0 : (sd == 0 ? -BipedSkeleton.HipW : BipedSkeleton.HipW);
            var heel = HeightAt(s - Heel); var toe = HeightAt(s + Toe - 0.03f);
            var ground = MathF.Max(HeightAt(s), MathF.Max(heel, toe));
            var pitch = MathF.Atan2(toe - heel, Heel + Toe - 0.03f);
            var p = _origin + f * s + r * lat;
            p.Y = ground + BipedSkeleton.AnkleH;
            var safe = !Straddles(s);
            _plan[k] = new Footstep(sd, p, Quaternion.CreateFromYawPitchRoll(_heading, -pitch, 0f), s, safe, _cost[k * Cand + bi]);
            if (k > 0) bi = _back[k * Cand + bi];
        }
        PlanCount = steps;
        FirstDelta = _plan[0].Along - s0;
        Plans++;
        if (_plan[0].EdgeSafe) SafePlans++;
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        LastUs = us; if (us > MaxUs) MaxUs = us;
    }
}
