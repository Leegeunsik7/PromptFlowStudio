using System.Numerics;
using PromptFlowStudio.Services.SkinOptics;

namespace PromptFlowStudio.Services.FluidWetness;

public enum DropletKind : byte { Sweat = 0, Rain, Tear }

/// <summary>
/// [PromptFlow Studio v17.19.0] 💦 미세 액적 접촉각 활주 &amp; 눈물·땀 궤적 합성기(<see cref="Update"/> 무할당 · 결정적 xorshift).
///   ▸ 액적(최대 512 · SoA): 얼굴 UV 위치 · 부피 µL · 속도(mm/s) · 종류(땀 · 비 · 눈물) · 상태(고정/활주).
///   ▸ 힘: 중력 접선 F_g = ρVg·sin α(피부 법선) vs 퍼미지 접촉각 이력 보유력 F_r = γ·2r·(cos θ_r − cos θ_a)(구형 캡 접촉 반경 r(V, θ)) ·
///     젖은 자리(피막 &gt; 5 µm) 이력 ½ → 흔적 추종 · 활주 a = (F_g − F_r)/m − c·v · 국소 피닝 무작위 편향(굽이치는 줄기).
///   ▸ 핵생성: 땀 = 피막 &gt; 45 µm 텍셀에 0.3 µL 비드 → 접촉 반경 내 피막 수확 성장 · 비 = 노출 텍셀 강수율 비례 2–14 µL · 눈물 = 안쪽 눈구석(요청 시 · 기쁨/긴장).
///   ▸ 병합: 접촉 반경 합 이내 → 부피 보존 · 운동량 가중 · 자취: 이동 mm 당 0.6% 부피를 피막으로 증착 + 1024 세그먼트 링버퍼(시각 줄기 · 3 s 감쇠) · 턱/가장자리 이탈 = 낙하 계수.
/// </summary>
public sealed class MicroDropletSlidingAdhesionSynthesizer
{
    public const int MaxDrops = 512, StreakRing = 1024, W = SeparableNeuralSkinSssEngine.W, H = SeparableNeuralSkinSssEngine.H;
    public const float Rho = 1000f, Gamma = 0.072f, G = 9.81f, DragC = 30f;

    private readonly SeparableNeuralSkinSssEngine _skin;
    private readonly MultiphaseSurfaceWetnessCapillaryEngine _wet;
    public readonly float[] U = new float[MaxDrops], V = new float[MaxDrops], Vol = new float[MaxDrops], Vx = new float[MaxDrops], Vy = new float[MaxDrops];
    public readonly DropletKind[] Kind = new DropletKind[MaxDrops];
    public readonly bool[] Alive = new bool[MaxDrops], Sliding = new bool[MaxDrops];
    private readonly float[] _rad = new float[MaxDrops];
    private readonly float[] _sx0 = new float[StreakRing], _sy0 = new float[StreakRing], _sx1 = new float[StreakRing], _sy1 = new float[StreakRing], _sAge = new float[StreakRing];
    private int _sHead, _sCount;
    private uint _rng = 0x1719u;
    private float _rainAcc, _tearAcc;

    public MicroDropletSlidingAdhesionSynthesizer(SeparableNeuralSkinSssEngine skin, MultiphaseSurfaceWetnessCapillaryEngine wet)
    {
        _skin = skin; _wet = wet;
    }

    public float AdvancingDeg { get; set; } = 95f;
    public float RecedingDeg { get; set; } = 65f;
    public float WetHysteresisFactor { get; set; } = 0.5f;
    public float SweatNucleationUm { get; set; } = 45f;
    public float TrailDeposit { get; set; } = 0.006f;
    public bool Tears { get; set; }
    public float TearRate { get; set; } = 0.6f;
    public int Count { get; private set; }
    public int SlidingCount { get; private set; }
    public long Spawned { get; private set; }
    public long Merges { get; private set; }
    public long Drips { get; private set; }
    public float MaxSpeedMm { get; private set; }
    public float TotalVolume { get; private set; }
    public double LastUpdateUs { get; private set; }
    public int StreakCount => _sCount;

    public void Streak(int back, out float x0, out float y0, out float x1, out float y1, out float age)
    {
        var k = (_sHead - 1 - back + 2 * StreakRing) % StreakRing;
        x0 = _sx0[k]; y0 = _sy0[k]; x1 = _sx1[k]; y1 = _sy1[k]; age = _sAge[k];
    }

    private float Rnd()
    {
        var x = _rng; x ^= x << 13; x ^= x >> 17; x ^= x << 5; _rng = x;
        return (x >> 8) * (1f / 16777216f);
    }

    // ── 물리 ────────────────────────────────────────────────

    /// <summary>구형 캡 접촉 반경(m · 부피 µL · 접촉각 °).</summary>
    public static float ContactRadius(float volUl, float thetaDeg)
    {
        var th = thetaDeg * MathF.PI / 180f;
        var c = MathF.Cos(th);
        var vol = volUl * 1e-9f;
        var R = MathF.Cbrt(3f * vol / (MathF.PI * (2f - 3f * c + c * c * c)));
        return R * MathF.Sin(th);
    }

    /// <summary>퍼미지 보유력(N).</summary>
    public static float Retention(float volUl, float advDeg, float recDeg, float hysteresisScale)
    {
        var r = ContactRadius(volUl, 0.5f * (advDeg + recDeg));
        return Gamma * 2f * r * (MathF.Cos(recDeg * MathF.PI / 180f) - MathF.Cos(advDeg * MathF.PI / 180f)) * hysteresisScale;
    }

    /// <summary>중력 접선력(N · 경사 sin α).</summary>
    public static float GravityTangential(float volUl, float sinAlpha) => Rho * volUl * 1e-9f * G * sinAlpha;

    public int Spawn(float u, float v, float volUl, DropletKind kind)
    {
        for (var i = 0; i < MaxDrops; i++)
        {
            if (Alive[i]) continue;
            Alive[i] = true; U[i] = u; V[i] = v; Vol[i] = volUl; Vx[i] = Vy[i] = 0f; Kind[i] = kind; Sliding[i] = false;
            Spawned++;
            return i;
        }
        return -1;
    }

    public void Clear() { Array.Clear(Alive); _sCount = 0; }

    private bool OnSkin(float u, float v, out int i)
    {
        int x = (int)u, y = (int)v;
        i = -1;
        if ((uint)x >= W || (uint)y >= H) return false;
        i = SeparableNeuralSkinSssEngine.Index(x, y);
        return _skin.Mask[i] > 0f && _skin.RegionOf[i] != SkinRegion.Finger;
    }

    /// <summary>원천 → 힘/활주 → 병합 → 자취/낙하(무할당). eyeTears = 눈물 유발(0..1).</summary>
    public void Update(float dt, float rainMmH, float tearDrive)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        // ① 핵생성 — 비
        _rainAcc += rainMmH * 0.35f * dt;
        while (_rainAcc >= 1f)
        {
            _rainAcc -= 1f;
            for (var tries = 0; tries < 8; tries++)
            {
                float u = Rnd() * W, v = Rnd() * H;
                if (!OnSkin(u, v, out var i) || _wet.Exposure(i) < Rnd()) continue;
                Spawn(u, v, 2f + 12f * Rnd() * Rnd(), DropletKind.Rain);
                break;
            }
        }
        // 땀 비드(피막 과포화 텍셀 · 격자 표본)
        for (var y = 2; y < H - 2; y += 4)
            for (var x = 2; x < W - 2; x += 4)
            {
                var i = SeparableNeuralSkinSssEngine.Index(x, y);
                if (_skin.Mask[i] <= 0f || _wet.Film(i) < SweatNucleationUm) continue;
                if (Rnd() > 0.15f) continue;
                var got = _wet.Harvest(x, y, 1, 0.5f) * MultiphaseSurfaceWetnessCapillaryEngine.TexelMmX * MultiphaseSurfaceWetnessCapillaryEngine.TexelMmY * 1e-3f;
                Spawn(x + Rnd(), y + Rnd(), MathF.Max(0.3f, got), DropletKind.Sweat);
            }
        // 눈물(안쪽 눈구석)
        if (Tears || tearDrive > 0f)
        {
            _tearAcc += (Tears ? TearRate : 0f) * dt + tearDrive * TearRate * dt;
            while (_tearAcc >= 1f)
            {
                _tearAcc -= 1f;
                var side = Rnd() < 0.5f ? -1f : 1f;
                Spawn(W * (0.5f + side * 0.105f) + Rnd() * 2f - 1f, H * 0.405f, 8f + 10f * Rnd(), DropletKind.Tear);
            }
        }
        // ② 힘 · 활주
        var hysWet = WetHysteresisFactor;
        float vmax = 0, tot = 0; int alive = 0, sliding = 0;
        for (var d = 0; d < MaxDrops; d++)
        {
            if (!Alive[d]) continue;
            if (!OnSkin(U[d], V[d], out var i)) { Alive[d] = false; Drips++; continue; }
            // 수확 성장(땀 · 비) — 접촉 반경 내 피막
            var r = ContactRadius(Vol[d], 0.5f * (AdvancingDeg + RecedingDeg)) * 1000f;   // mm
            var film = _wet.Film(i);
            if (film > 8f) Vol[d] += _wet.Harvest((int)U[d], (int)V[d], Math.Max(0, (int)(r / MultiphaseSurfaceWetnessCapillaryEngine.TexelMmX)), 0.08f) * MultiphaseSurfaceWetnessCapillaryEngine.TexelMmX * MultiphaseSurfaceWetnessCapillaryEngine.TexelMmY * 1e-3f;
            // 경사: 중력의 피부 접선 성분(UV x = +X mm · y = −Y mm)
            var n = new Vector3(_skin.NX[i], _skin.NY[i], _skin.NZ[i]);
            var gt = new Vector3(0f, -1f, 0f) - n * -n.Y;
            var sinA = gt.Length();
            var fg = GravityTangential(Vol[d], sinA);
            var fr = Retention(Vol[d], AdvancingDeg, RecedingDeg, film > 5f ? hysWet : 1f);
            var m = Rho * Vol[d] * 1e-9f;
            float ax = 0f, ay = 0f;
            if (fg > fr || Sliding[d])
            {
                var net = (fg - fr) / m * 1000f;   // mm/s²
                if (sinA > 1e-4f) { ax = net * gt.X / sinA; ay = net * -gt.Y / sinA; }
                ax += (Rnd() - 0.5f) * 400f;       // 국소 피닝 편향
            }
            Vx[d] += (ax - DragC * Vx[d]) * dt; Vy[d] += (ay - DragC * Vy[d]) * dt;
            var sp = MathF.Sqrt(Vx[d] * Vx[d] + Vy[d] * Vy[d]);
            Sliding[d] = fg > fr * 0.85f && sp > 0.5f || fg > fr;
            if (!Sliding[d]) { Vx[d] *= 0.5f; Vy[d] *= 0.5f; sp *= 0.5f; }
            if (sp > vmax) vmax = sp;
            // 이동(mm → 텍셀) · 자취 증착
            var dxm = Vx[d] * dt; var dym = Vy[d] * dt;
            var dist = MathF.Sqrt(dxm * dxm + dym * dym);
            if (dist > 1e-4f)
            {
                float u0 = U[d], v0 = V[d];
                U[d] += dxm / MultiphaseSurfaceWetnessCapillaryEngine.TexelMmX; V[d] += dym / MultiphaseSurfaceWetnessCapillaryEngine.TexelMmY;
                var dep = MathF.Min(Vol[d] * 0.5f, Vol[d] * TrailDeposit * dist);
                Vol[d] -= dep;
                _wet.AddFilm((int)U[d], (int)V[d], dep * 1000f / (MultiphaseSurfaceWetnessCapillaryEngine.TexelMmX * MultiphaseSurfaceWetnessCapillaryEngine.TexelMmY));
                if (dist > 0.05f)
                {
                    _sx0[_sHead] = u0; _sy0[_sHead] = v0; _sx1[_sHead] = U[d]; _sy1[_sHead] = V[d]; _sAge[_sHead] = 0f;
                    _sHead = (_sHead + 1) % StreakRing;
                    if (_sCount < StreakRing) _sCount++;
                }
            }
            // 증발(소형)
            Vol[d] -= 0.02f * dt * (1f - _wet.Humidity);
            if (Vol[d] < 0.05f) { _wet.AddFilm((int)U[d], (int)V[d], MathF.Max(0f, Vol[d]) * 1000f / (MultiphaseSurfaceWetnessCapillaryEngine.TexelMmX * MultiphaseSurfaceWetnessCapillaryEngine.TexelMmY)); Alive[d] = false; continue; }
            alive++; if (Sliding[d]) sliding++;
            tot += Vol[d];
        }
        // ③ 병합(O(n²) · 접촉 반경 합 · 반경 사전 계산)
        for (var d = 0; d < MaxDrops; d++) _rad[d] = Alive[d] ? ContactRadius(Vol[d], 80f) * 1000f : 0f;
        for (var a = 0; a < MaxDrops; a++)
        {
            if (!Alive[a]) continue;
            var ra = _rad[a];
            for (var b = a + 1; b < MaxDrops; b++)
            {
                if (!Alive[b]) continue;
                var dx = (U[a] - U[b]) * MultiphaseSurfaceWetnessCapillaryEngine.TexelMmX; var dy = (V[a] - V[b]) * MultiphaseSurfaceWetnessCapillaryEngine.TexelMmY;
                var rb = _rad[b];
                if (dx * dx + dy * dy > (ra + rb) * (ra + rb)) continue;
                var vt = Vol[a] + Vol[b];
                U[a] = (U[a] * Vol[a] + U[b] * Vol[b]) / vt; V[a] = (V[a] * Vol[a] + V[b] * Vol[b]) / vt;
                Vx[a] = (Vx[a] * Vol[a] + Vx[b] * Vol[b]) / vt; Vy[a] = (Vy[a] * Vol[a] + Vy[b] * Vol[b]) / vt;
                Vol[a] = vt; Alive[b] = false; Merges++; alive--;
                ra = _rad[a] = ContactRadius(Vol[a], 80f) * 1000f;
            }
        }
        for (var k = 0; k < _sCount; k++) { var j = (_sHead - 1 - k + 2 * StreakRing) % StreakRing; _sAge[j] += dt; }
        Count = alive; SlidingCount = sliding; MaxSpeedMm = vmax; TotalVolume = tot;
        LastUpdateUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }
}
