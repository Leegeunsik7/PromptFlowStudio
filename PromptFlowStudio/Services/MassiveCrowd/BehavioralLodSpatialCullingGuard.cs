using System.Diagnostics;

namespace PromptFlowStudio.Services.MassiveCrowd;

/// <summary>
/// [PromptFlow Studio v17.04.0] 🎚 행동 계층 LOD &amp; 공간 컬링 가드(무할당).
/// 카메라 FoV 원뿔 + 6 m 주변 인지 → 가시 판정 · 가시 에이전트 거리 히스토그램(256 버킷) → 상한(NearCap/MidCap)을 넘지 않는 버킷 경계로 근/중거리 반경 결정 →
/// 근거리 = 풀 본 모션 + 개별 ORCA 지능 · 중거리 = 그룹 플로킹(임포스터) · 원거리/시야 밖 = 스프라이트/밀도장(FarStride 분산 갱신).
/// 프레임 예산(16.6 ms) 피드백: 실측 92% 초과 → 근거리 상한 축소(최소 도달 시 중거리 상한 · 원거리 스트라이드) · 75% 미만 10 프레임 지속 → 역순 확대 · p95 추적.
/// </summary>
public sealed class BehavioralLodSpatialCullingGuard
{
    public const int Buckets = 256, History = 120, MinNearCap = 200, MaxNearCap = 20000, MinMidCap = 2000, MaxStride = 8;
    public const float NearDistMax = 30f, MidDistMax = 90f, PeripheralDist = 6f, BonesNear = 64, BonesMid = 12;
    public static readonly string[] LodNames = { "근거리 · 풀 본 + ORCA", "중거리 · 그룹 플로킹", "원거리 · 스프라이트/밀도장" };

    private readonly int[] _hist = new int[Buckets];
    private readonly float[] _dist = new float[MassiveAgenticCrowdSimulationEngine.MaxAgents];
    private readonly bool[] _vis = new bool[MassiveAgenticCrowdSimulationEngine.MaxAgents];
    private readonly float[] _ring = new float[History], _sorted = new float[History];
    private int _ringN, _ringHead, _calm;

    public float CameraX { get; private set; } = MassiveAgenticCrowdSimulationEngine.Cx - 150f;
    public float CameraZ { get; private set; } = MassiveAgenticCrowdSimulationEngine.Cz;
    public float CameraYaw { get; private set; }
    public float FovDeg { get; private set; } = 75f;
    public float BudgetMs { get; set; } = 16.6f;
    public bool Adaptive { get; set; } = true;
    public int NearCap { get; set; } = 3000;
    public int MidCap { get; set; } = 30000;
    public int FarStride { get; private set; } = 2;

    public int NearCount { get; private set; }
    public int MidCount { get; private set; }
    public int FarCount { get; private set; }
    public int VisibleCount { get; private set; }
    public int CulledCount { get; private set; }
    public float NearRadius { get; private set; }
    public float MidRadius { get; private set; }
    public long Overruns { get; private set; }
    public long Frames { get; private set; }
    public float LastFrameMs { get; private set; }
    public float P95FrameMs { get; private set; }
    public double LastAssignUs { get; private set; }
    /// <summary>렌더 비용 추정(본 수: 근 64 · 중 12 · 가시 원거리 스프라이트 1).</summary>
    public long BoneCost => (long)NearCount * (long)BonesNear + (long)MidCount * (long)BonesMid + Math.Max(0, VisibleCount - NearCount - MidCount);
    /// <summary>전원 풀 본이었을 때 대비 절감률.</summary>
    public float CostSavings(int total) => total == 0 ? 0f : 1f - BoneCost / (float)((long)total * (long)BonesNear);

    public void SetCamera(float x, float z, float yawRad, float fovDeg)
    {
        CameraX = x; CameraZ = z; CameraYaw = yawRad; FovDeg = Math.Clamp(fovDeg, 20f, 360f);
    }

    /// <summary>LOD 배정(무할당 · O(n)).</summary>
    public void Assign(MassiveAgenticCrowdSimulationEngine e)
    {
        var t0 = Stopwatch.GetTimestamp();
        var n = e.Count;
        var xs = e.Xs; var zs = e.Zs; var lods = e.Lods;
        float fx = MathF.Cos(CameraYaw), fz = MathF.Sin(CameraYaw), cosHalf = MathF.Cos(FovDeg * MathF.PI / 360f);
        var full = FovDeg >= 359.9f;
        Array.Clear(_hist);
        int vis = 0;
        const float bucketScale = Buckets / MidDistMax;
        for (var i = 0; i < n; i++)
        {
            float dx = xs[i] - CameraX, dz = zs[i] - CameraZ;
            var d = MathF.Sqrt(dx * dx + dz * dz);
            var v = full || d <= PeripheralDist || dx * fx + dz * fz >= cosHalf * d;
            _vis[i] = v; _dist[i] = d;
            if (!v) continue;
            vis++;
            if (d < MidDistMax) _hist[Math.Min(Buckets - 1, (int)(d * bucketScale))]++;
        }
        // 상한을 넘지 않는 마지막 완전 버킷 경계(배정도 같은 버킷 함수 → 상한 정확 보장)
        var nearMaxB = (int)(NearDistMax * bucketScale);
        int nearB = -1, midB = Buckets, cum = 0;
        for (var b = 0; b < Buckets; b++)
        {
            var next = cum + _hist[b];
            if (nearB < 0 && (next > NearCap || b >= nearMaxB)) nearB = b;
            if (next > NearCap + MidCap) { midB = b; break; }
            cum = next;
        }
        if (nearB < 0) nearB = nearMaxB;
        midB = Math.Max(midB, nearB);
        int near = 0, mid = 0, far = 0;
        for (var i = 0; i < n; i++)
        {
            var d = _dist[i];
            var b = d < MidDistMax ? Math.Min(Buckets - 1, (int)(d * bucketScale)) : Buckets;
            CrowdLod l;
            if (_vis[i] && b < nearB) { l = CrowdLod.Near; near++; }
            else if (_vis[i] && b < midB) { l = CrowdLod.Mid; mid++; }
            else { l = CrowdLod.Far; far++; }
            lods[i] = l;
        }
        var nearR = nearB / bucketScale; var midR = midB >= Buckets ? MidDistMax : midB / bucketScale;
        NearCount = near; MidCount = mid; FarCount = far; VisibleCount = vis; CulledCount = n - vis;
        NearRadius = nearR; MidRadius = midR;
        e.FarStride = FarStride;
        LastAssignUs = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
    }

    public bool IsVisible(int i) => _vis[i];
    public float Distance(int i) => _dist[i];

    /// <summary>실측 프레임 시간 보고 → 예산 피드백(상한 · 스트라이드).</summary>
    public void Report(float frameMs)
    {
        Frames++;
        LastFrameMs = frameMs;
        if (frameMs > BudgetMs) Overruns++;
        _ring[_ringHead] = frameMs;
        _ringHead = (_ringHead + 1) % History;
        if (_ringN < History) _ringN++;
        Array.Copy(_ring, _sorted, _ringN);
        Array.Sort(_sorted, 0, _ringN);
        P95FrameMs = _sorted[Math.Min(_ringN - 1, (int)(_ringN * 0.95f))];
        if (!Adaptive) return;
        // 히스테리시스 대역 75~92%: 초과 시 근거리 상한 → (최소 도달 후) 중거리 상한 · 스트라이드 순 축소, 여유 시 역순 확대
        if (frameMs > BudgetMs * 0.92f)
        {
            _calm = 0;
            if (NearCap > MinNearCap) NearCap = Math.Max(MinNearCap, (int)(NearCap * 0.8f));
            else { MidCap = Math.Max(MinMidCap, (int)(MidCap * 0.85f)); FarStride = Math.Min(MaxStride, FarStride + 1); }
        }
        else if (frameMs < BudgetMs * 0.75f && ++_calm >= 10)
        {
            _calm = 0;
            if (FarStride > 2) FarStride--;
            else if (MidCap < 30000) MidCap = Math.Min(MassiveAgenticCrowdSimulationEngine.MaxAgents, (int)(MidCap * 1.15f) + 200);
            else NearCap = Math.Min(MaxNearCap, (int)(NearCap * 1.1f) + 20);
        }
    }
}
