using System.Numerics;
using PromptFlowStudio.Services.SplatEnvironment;

namespace PromptFlowStudio.Services.BroadcastMcr;

/// <summary>
/// [PromptFlow Studio v17.66.0] 동적 3DGS 가상 PPL 앵커링 &amp; 브랜드 세이프티 가드(60 fps · 무할당).
///   ▸ 앵커: 토크쇼 데스크 상판(반경 0.46 m) 위 후보 25(현재 + 링 2 × 12) — 데스크 가장자리 · 아바타 접근 금지 여유 반영.
///   ▸ 가림률: 소품 원통 표면 64 표본점 중 화면 내 표본 → PGM 카메라 광선이 아바타 15 구(v17.56 골격 프록시 · 2~3 인)에 소품보다 먼저 맞는 비율(프레임 밖 = 노출 0 · 회피 대상 아님).
///   ▸ 충돌: 소품 경계 구(r = 0.11) ↔ 아바타 구 침투 깊이 &gt; 0 → 관통.
///   ▸ 회피: 가림률 &gt; <see cref="Threshold"/>(60 %) 또는 관통 프레임마다 즉시 후보 재탐색(최소 가림 · 관통 없음 · 현 위치 근접 가산) → 반감기 0.12 s 이동.
///     발동률 = 회피 발동 프레임 / 임계 초과 프레임(계약 100 %). 노출 = (1 − 가림률) · 화면 내 · 투영 크기 ≥ 6 px.
/// </summary>
public sealed class DynamicPplOcclusionBrandGuard
{
    public const int Samples = 64, Candidates = 25, MaxExtra = 8, MaxSpheres = 15 * UnifiedGaussianSceneCompositorEngine.MaxAvatars + MaxExtra;
    public const float Threshold = 0.6f, MinInFrame = 0.25f, BoundR = 0.11f, Clearance = 0.04f, HalfLife = 0.12f, MinPx = 6f;

    private readonly Vector3[] _sample = new Vector3[Samples], _cand = new Vector3[Candidates];
    private readonly Vector4[] _sph = new Vector4[MaxSpheres];
    private int _sphN;
    private Vector3 _pos, _target, _anchor;

    public DynamicPplOcclusionBrandGuard()
    {
        for (var k = 0; k < Samples; k++)
        {
            var a = MathF.Tau * (k % 16) / 16f; var y = UnifiedGaussianSceneCompositorEngine.PropHeight * (0.12f + 0.76f * (k / 16) / 3f);
            _sample[k] = new Vector3(UnifiedGaussianSceneCompositorEngine.PropRadius * MathF.Cos(a), y, UnifiedGaussianSceneCompositorEngine.PropRadius * MathF.Sin(a));
        }
        Anchor = UnifiedGaussianSceneCompositorEngine.DeskCenter + new Vector3(0.22f, 0f, 0.08f);
    }

    public bool Enabled { get; set; } = true;
    /// <summary>스폰서 지정 앵커(데스크 상판) — 회피 후 복귀 선호점.</summary>
    public Vector3 Anchor { get => _anchor; set { _anchor = ClampToDesk(value); _pos = _target = _anchor; } }
    public Vector3 Position => _pos;
    public Vector3 Target => _target;
    public float OcclusionRatio { get; private set; }
    public float PeakOcclusion { get; private set; }
    public float Exposure { get; private set; }
    /// <summary>화면 내 표본 비율(0 = PGM 프레임 밖 → 회피 대상 아님).</summary>
    public float InFrame { get; private set; }
    public float ScreenPx { get; private set; }
    public bool Penetrating { get; private set; }
    public long Frames { get; private set; }
    public long OverFrames { get; private set; }
    public long AvoidFrames { get; private set; }
    public long Relocations { get; private set; }
    public long Penetrations { get; private set; }
    public long Unresolved { get; private set; }
    public double ExposureSeconds { get; private set; }
    public double LastUs { get; private set; }
    public float AvoidRate => OverFrames == 0 ? 1f : (float)AvoidFrames / OverFrames;

    public void ResetMetrics() { Frames = OverFrames = AvoidFrames = Relocations = Penetrations = Unresolved = 0; PeakOcclusion = 0f; ExposureSeconds = 0; }

    private static Vector3 ClampToDesk(Vector3 p)
    {
        var c = UnifiedGaussianSceneCompositorEngine.DeskCenter;
        var d = new Vector2(p.X - c.X, p.Z - c.Z);
        var lim = UnifiedGaussianSceneCompositorEngine.DeskRadius - UnifiedGaussianSceneCompositorEngine.PropRadius - 0.02f;
        if (d.Length() > lim) d = Vector2.Normalize(d) * lim;
        return new Vector3(c.X + d.X, UnifiedGaussianSceneCompositorEngine.DeskTop, c.Z + d.Y);
    }

    /// <summary>아바타 구 수집(컴포지터 v17.56 골격 프록시) + 외부 가림 구(테스트 · 소품).</summary>
    public void Collect(UnifiedGaussianSceneCompositorEngine comp, ReadOnlySpan<Vector4> extra = default)
    {
        _sphN = 0;
        for (var a = 0; a < comp.Avatars; a++) foreach (var s in comp.Spheres(a)) if (_sphN < MaxSpheres) _sph[_sphN++] = s;
        foreach (var s in extra) if (_sphN < MaxSpheres) _sph[_sphN++] = s;
    }

    /// <summary>위치 <paramref name="basePos"/> 소품의 가림률 = 화면 내 표본 중 아바타에 가린 비율(무할당).</summary>
    public float Occlusion(Vector3 basePos, in SplatCamera cam, out float px) => Occlusion(basePos, cam, out px, out _);

    /// <param name="inFrame">화면 내 표본 비율.</param>
    public float Occlusion(Vector3 basePos, in SplatCamera cam, out float px, out float inFrame)
    {
        var e = cam.Position; var hits = 0; var vis = 0;
        float tanH = cam.TanHalfH, tanV = cam.TanHalfV;
        float umin = float.MaxValue, umax = float.MinValue;
        for (var k = 0; k < Samples; k++)
        {
            var p = basePos + _sample[k];
            var d = p - e;
            var z = Vector3.Dot(d, cam.Forward);
            if (z <= cam.Near) continue;
            var u = Vector3.Dot(d, cam.Right) / (z * tanH); var v = Vector3.Dot(d, cam.Up) / (z * tanV);
            if (MathF.Abs(u) > 1f || MathF.Abs(v) > 1f) continue;
            vis++;
            umin = MathF.Min(umin, u); umax = MathF.Max(umax, u);
            var len = d.Length(); var dir = d / len;
            for (var s = 0; s < _sphN; s++)
            {
                var c = new Vector3(_sph[s].X, _sph[s].Y, _sph[s].Z); var r = _sph[s].W;
                var oc = c - e; var tc = Vector3.Dot(oc, dir);
                if (tc <= 0f || tc - r >= len) continue;
                var d2 = oc.LengthSquared() - tc * tc;
                if (d2 < r * r) { hits++; break; }
            }
        }
        px = umax > umin ? (umax - umin) * 0.5f * UnifiedGaussianSceneCompositorEngine.W : 0f;
        inFrame = vis / (float)Samples;
        return vis > 0 ? hits / (float)vis : 0f;
    }

    public bool Penetrates(Vector3 basePos)
    {
        var c = basePos + new Vector3(0f, UnifiedGaussianSceneCompositorEngine.PropHeight * 0.5f, 0f);
        for (var s = 0; s < _sphN; s++)
        {
            var d = Vector3.Distance(c, new Vector3(_sph[s].X, _sph[s].Y, _sph[s].Z));
            if (d < BoundR + _sph[s].W + Clearance * 0.5f) return true;
        }
        return false;
    }

    /// <summary>프레임 — 가림/관통 판정 → 즉시 회피 재탐색 → 스프링 이동(무할당).</summary>
    public void Step(in SplatCamera cam, float dt)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Frames++;
        var occ = Occlusion(_pos, cam, out var px, out var inFrame);
        var pen = Penetrates(_pos);
        OcclusionRatio = occ; ScreenPx = px; Penetrating = pen; InFrame = inFrame;
        if (occ > PeakOcclusion) PeakOcclusion = occ;
        if (pen) Penetrations++;
        if (Enabled && (occ > Threshold && inFrame >= MinInFrame || pen))
        {
            OverFrames++;
            var best = Search(cam, out var bestOcc);
            if (Vector3.DistanceSquared(best, _target) > 1e-6f) { _target = best; Relocations++; }
            AvoidFrames++;
            if (bestOcc > Threshold) Unresolved++;
        }
        else if (Enabled && Vector3.DistanceSquared(_target, _anchor) > 1e-6f && Occlusion(_anchor, cam, out _) < Threshold * 0.5f && !Penetrates(_anchor)) _target = _anchor;
        var k = 1f - MathF.Exp(-dt * 0.693f / HalfLife);
        _pos += (_target - _pos) * k;
        Exposure = px >= MinPx ? inFrame * (1f - occ) : 0f;
        ExposureSeconds += Exposure * dt;
        LastUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private Vector3 Search(in SplatCamera cam, out float bestOcc)
    {
        var c = UnifiedGaussianSceneCompositorEngine.DeskCenter;
        _cand[0] = _anchor;
        for (var i = 0; i < 12; i++)
        {
            var a = MathF.Tau * i / 12f;
            _cand[1 + i] = ClampToDesk(c + new Vector3(0.18f * MathF.Cos(a), 0f, 0.18f * MathF.Sin(a)));
            _cand[13 + i] = ClampToDesk(c + new Vector3(0.36f * MathF.Cos(a), 0f, 0.36f * MathF.Sin(a)));
        }
        var best = _pos; var bestScore = float.MaxValue; bestOcc = 1f;
        for (var i = 0; i < Candidates; i++)
        {
            var p = _cand[i];
            var o = Occlusion(p, cam, out var px, out var inf);
            var score = o + (Penetrates(p) ? 2f : 0f) + 0.15f * Vector3.Distance(p, _pos) + (px < MinPx ? 0.3f : 0f) + 0.3f * (1f - inf) + (i == 0 ? -0.05f : 0f);
            if (score < bestScore) { bestScore = score; best = p; bestOcc = o; }
        }
        return best;
    }
}
