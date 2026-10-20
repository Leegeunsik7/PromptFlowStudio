using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Neural3d;
using PromptFlowStudio.Services.OpenUsd;

namespace PromptFlowStudio.Services.GenerativeVfx;

/// <summary>표면 물성: 반발 · 마찰 · 흡수(젖음 이득) · 비산 문턱(m/s).</summary>
public readonly record struct VfxSurfaceMaterial(string Name, float Restitution, float Friction, float Absorption, float SplashSpeed)
{
    public static readonly VfxSurfaceMaterial Rock = new("암석", 0.35f, 0.25f, 0.15f, 1.6f);
    public static readonly VfxSurfaceMaterial Soil = new("흙", 0.12f, 0.55f, 0.75f, 2.2f);
    public static readonly VfxSurfaceMaterial Skin = new("캐릭터(3DGS)", 0.2f, 0.4f, 0.35f, 1.4f);
}

/// <summary>
/// [PromptFlow Studio v16.81.0] 🛡 인터랙티브 물성 충돌 &amp; 자산 표면 비산 가드(무할당).
/// OpenUSD 지형(<see cref="IHeightField"/> · 유한차분 법선 · 흡수율 → 물성 보간) + 캐릭터 3DGS 프록시 구(스플랫 → stride 샘플 · 바운딩 구 조기 탈락) 충돌 →
/// 위치 투영 · 법선 반발/접선 마찰 · 충돌 이벤트 링 · 비산(물방울 링 4096 · 탄도 → 착지 시 젖음) · 파편(링 512 · 바운스/스핀) ·
/// 젖음 텍스처(64² 지형 맵 · 증발) + 프록시별 젖음 → 3DGS SH DC 어둡게(젖은 표면 피드백).
/// </summary>
public sealed class InteractiveVfxCollisionSplashGuard
{
    public const int WetN = 64, MaxDroplets = 4096, MaxDebris = 512, MaxProxies = 96, MaxImpacts = 64;

    private readonly float[] _wet = new float[WetN * WetN];
    private readonly float[] _dx = new float[MaxDroplets], _dy = new float[MaxDroplets], _dz = new float[MaxDroplets], _dvx = new float[MaxDroplets], _dvy = new float[MaxDroplets], _dvz = new float[MaxDroplets], _dl = new float[MaxDroplets];
    private readonly Vector3[] _bp = new Vector3[MaxDebris], _bv = new Vector3[MaxDebris], _bs = new Vector3[MaxDebris];
    private readonly float[] _bl = new float[MaxDebris], _bsize = new float[MaxDebris];
    private readonly Vector3[] _proxyC = new Vector3[MaxProxies];
    private readonly float[] _proxyR = new float[MaxProxies], _proxyWet = new float[MaxProxies];
    private readonly ImpactEvent[] _impacts = new ImpactEvent[MaxImpacts];
    private int[] _splatProxy = Array.Empty<int>();
    private float[] _baseDc = Array.Empty<float>();
    private GaussianScene? _scene;
    private int _dropHead, _debrisHead, _proxies, _impactHead, _impactCount;
    private Vector3 _boundC; private float _boundR;
    private ulong _rng = 0x5A1A_5481UL;
    private float _time;

    public IHeightField? Terrain { get; set; }
    public Vector2 TerrainMin { get; set; } = new(-2f, -2f);
    public Vector2 TerrainMax { get; set; } = new(2f, 2f);
    public VfxSurfaceMaterial HardMaterial { get; set; } = VfxSurfaceMaterial.Rock;
    public VfxSurfaceMaterial SoftMaterial { get; set; } = VfxSurfaceMaterial.Soil;
    public VfxSurfaceMaterial ProxyMaterial { get; set; } = VfxSurfaceMaterial.Skin;
    public float DryingPerSec { get; set; } = 0.035f;
    public float DropletsPerSplash { get; set; } = 6f;
    public float Gravity { get; set; } = -9.81f;

    // ── 계측 ──
    public long Contacts { get; private set; }
    public long TerrainContacts { get; private set; }
    public long ProxyContacts { get; private set; }
    public long Splashes { get; private set; }
    public long DropletsSpawned { get; private set; }
    public long DebrisSpawned { get; private set; }
    public long Penetrations { get; private set; }
    public float MaxImpactSpeed { get; private set; }
    public float WetCoverage { get; private set; }
    public int ProxyCount => _proxies;
    public Vector3 ProxyCenter(int i) => _proxyC[i];
    public float ProxyRadius(int i) => _proxyR[i];
    public float ProxyWetness(int i) => _proxyWet[i];
    public string? LastAction { get; private set; }

    public float Wetness(int x, int z) => _wet[z * WetN + x];
    public ReadOnlySpan<float> WetMap => _wet;
    public int ImpactCount => _impactCount;
    public ImpactEvent Impact(int back) => _impacts[(_impactHead - 1 - back + MaxImpacts * 2) % MaxImpacts];

    public int DropletCount { get { var n = 0; for (var i = 0; i < MaxDroplets; i++) if (_dl[i] > 0f) n++; return n; } }
    public bool DropletAlive(int i) => _dl[i] > 0f;
    public Vector3 Droplet(int i) => new(_dx[i], _dy[i], _dz[i]);
    public int DebrisCount { get { var n = 0; for (var i = 0; i < MaxDebris; i++) if (_bl[i] > 0f) n++; return n; } }
    public bool DebrisAlive(int i) => _bl[i] > 0f;
    public Vector3 DebrisPosition(int i) => _bp[i];
    public float DebrisSize(int i) => _bsize[i];

    public void Reset()
    {
        Array.Clear(_wet); Array.Clear(_dl); Array.Clear(_bl); Array.Clear(_proxyWet);
        _impactCount = _impactHead = 0; _time = 0;
        Contacts = TerrainContacts = ProxyContacts = Splashes = DropletsSpawned = DebrisSpawned = Penetrations = 0; MaxImpactSpeed = 0; WetCoverage = 0;
        RestoreSplats();
    }

    /// <summary>제어 평면: 캐릭터 3DGS 씬 → 프록시 구(stride 샘플 · 반경 = 최대 스케일 × 2.5) · 원본 SH DC 보관.</summary>
    public void BindSplatProxies(GaussianScene scene, int maxProxies = MaxProxies)
    {
        _scene = scene;
        var n = scene.Count;
        maxProxies = Math.Clamp(maxProxies, 1, MaxProxies);
        _proxies = Math.Min(n, maxProxies);
        if (_splatProxy.Length < n) { _splatProxy = new int[n]; _baseDc = new float[n * 3]; }
        if (_proxies == 0) { _boundR = 0; return; }
        var stride = Math.Max(1, n / _proxies);
        for (var p = 0; p < _proxies; p++)
        {
            var i = Math.Min(n - 1, p * stride);
            _proxyC[p] = scene.Position(i);
            var s = scene.ScaleOf(i);
            _proxyR[p] = MathF.Max(0.04f, MathF.Max(s.X, MathF.Max(s.Y, s.Z)) * 2.5f);
        }
        Vector3 mn = new(float.MaxValue), mx = new(float.MinValue);
        for (var p = 0; p < _proxies; p++) { mn = Vector3.Min(mn, _proxyC[p] - new Vector3(_proxyR[p])); mx = Vector3.Max(mx, _proxyC[p] + new Vector3(_proxyR[p])); }
        _boundC = (mn + mx) * 0.5f; _boundR = (mx - mn).Length() * 0.5f;
        for (var i = 0; i < n; i++)
        {
            var p0 = scene.Position(i); var best = 0; var bd = float.MaxValue;
            for (var p = 0; p < _proxies; p++) { var d = Vector3.DistanceSquared(p0, _proxyC[p]); if (d < bd) { bd = d; best = p; } }
            _splatProxy[i] = best;
            _baseDc[i * 3] = scene.Sh[i * GaussianScene.ShStride]; _baseDc[i * 3 + 1] = scene.Sh[i * GaussianScene.ShStride + 1]; _baseDc[i * 3 + 2] = scene.Sh[i * GaussianScene.ShStride + 2];
        }
        LastAction = "🧍 3DGS 프록시 " + _proxies.ToString(System.Globalization.CultureInfo.InvariantCulture) + "개 결합";
    }

    /// <summary>프록시 이동(캐릭터 애니메이션 · 무할당).</summary>
    public void TranslateProxies(Vector3 delta)
    {
        for (var p = 0; p < _proxies; p++) _proxyC[p] += delta;
        _boundC += delta;
    }

    /// <summary>핫패스: 유체 입자 충돌(엔진 서브스텝마다 호출 · 무할당).</summary>
    public void Resolve(NeuralParticleFluidDynamicsEngine e, float dt)
    {
        using var guard = ZeroGcRegionScope.Enter("VfxGuard.Resolve");
        _time += dt;
        var n = e.Count; var h = e.H;
        var terrain = Terrain;
        var bound2 = (_boundR + h) * (_boundR + h);
        for (var i = 0; i < n; i++)
        {
            if (e.Phase[i] != (byte)FluidPhase.Liquid) continue;
            var p = new Vector3(e.X[i], e.Y[i], e.Z[i]);
            var v = new Vector3(e.VX[i], e.VY[i], e.VZ[i]);
            var hit = false;

            if (terrain is not null)
            {
                var gy = terrain.HeightAt(p.X, p.Z);
                if (p.Y < gy)
                {
                    if (gy - p.Y > h) Penetrations++;
                    var nrm = TerrainNormal(terrain, p.X, p.Z, h);
                    var a = Math.Clamp(terrain.AbsorptionAt(p.X, p.Z), 0f, 1f);
                    var mat = Blend(HardMaterial, SoftMaterial, a);
                    p.Y = gy + h * 0.01f;
                    v = Bounce(v, nrm, mat, p, ImpactKind.Ground, out var spd);
                    if (spd > 0f) { Wet(p.X, p.Z, mat.Absorption * MathF.Min(1f, spd) * 0.02f); TerrainContacts++; }
                    hit = true;
                }
            }

            if (_proxies > 0 && Vector3.DistanceSquared(p, _boundC) < bound2)
            {
                for (var k = 0; k < _proxies; k++)
                {
                    var d = p - _proxyC[k]; var rr = _proxyR[k] + h * 0.25f;
                    var d2 = d.LengthSquared();
                    if (d2 >= rr * rr) continue;
                    var len = MathF.Sqrt(d2);
                    var nrm = len > 1e-6f ? d / len : Vector3.UnitY;
                    p = _proxyC[k] + nrm * rr;
                    v = Bounce(v, nrm, ProxyMaterial, p, ImpactKind.Body, out var spd);
                    if (spd > 0f) { _proxyWet[k] = MathF.Min(1f, _proxyWet[k] + ProxyMaterial.Absorption * MathF.Min(1f, spd) * 0.03f); ProxyContacts++; }
                    hit = true;
                }
            }

            if (!hit) continue;
            e.X[i] = p.X; e.Y[i] = p.Y; e.Z[i] = p.Z; e.VX[i] = v.X; e.VY[i] = v.Y; e.VZ[i] = v.Z;
        }
    }

    private Vector3 Bounce(Vector3 v, Vector3 nrm, in VfxSurfaceMaterial mat, Vector3 p, ImpactKind kind, out float impactSpeed)
    {
        var vn = Vector3.Dot(v, nrm);
        impactSpeed = 0f;
        if (vn >= 0f) return v;
        impactSpeed = -vn;
        Contacts++;
        var vt = v - vn * nrm;
        var o = vt * (1f - mat.Friction) - vn * mat.Restitution * nrm;
        if (impactSpeed > MaxImpactSpeed) MaxImpactSpeed = impactSpeed;
        if (impactSpeed > mat.SplashSpeed)
        {
            Splashes++;
            var energy = 0.5f * impactSpeed * impactSpeed;
            _impacts[_impactHead] = new ImpactEvent(_time, p.X, p.Y, p.Z, energy, kind);
            _impactHead = (_impactHead + 1) % MaxImpacts; if (_impactCount < MaxImpacts) _impactCount++;
            var cnt = (int)MathF.Min(24f, DropletsPerSplash * (impactSpeed / mat.SplashSpeed - 0.5f));
            for (var k = 0; k < cnt; k++) SpawnDroplet(p + nrm * 0.01f, Crown(nrm, impactSpeed * 0.45f) + vt * 0.5f);
        }
        return o;
    }

    private Vector3 Crown(Vector3 nrm, float speed)
    {
        var t = Vector3.Normalize(Vector3.Cross(nrm, MathF.Abs(nrm.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY)); var b = Vector3.Cross(nrm, t);
        var a = Rand() * MathF.Tau; var spread = 0.6f + 0.6f * Rand();
        return (nrm * (0.8f + 0.5f * Rand()) + (t * MathF.Cos(a) + b * MathF.Sin(a)) * spread) * speed;
    }

    private void SpawnDroplet(Vector3 p, Vector3 v)
    {
        var i = _dropHead; _dropHead = (_dropHead + 1) % MaxDroplets;
        _dx[i] = p.X; _dy[i] = p.Y; _dz[i] = p.Z; _dvx[i] = v.X; _dvy[i] = v.Y; _dvz[i] = v.Z; _dl[i] = 2.5f;
        DropletsSpawned++;
    }

    /// <summary>파편 분산(폭발 · 강한 충돌): 반구 방사 · 스핀.</summary>
    public void SpawnDebris(Vector3 center, int count, float speed)
    {
        for (var k = 0; k < count; k++)
        {
            var i = _debrisHead; _debrisHead = (_debrisHead + 1) % MaxDebris;
            var u = Rand() * 2f - 1f; var a = Rand() * MathF.Tau; var s = MathF.Sqrt(1f - u * u);
            var dir = new Vector3(s * MathF.Cos(a), MathF.Abs(u) * 0.8f + 0.2f, s * MathF.Sin(a));
            _bp[i] = center; _bv[i] = Vector3.Normalize(dir) * speed * (0.5f + Rand());
            _bs[i] = new Vector3(Rand() - 0.5f, Rand() - 0.5f, Rand() - 0.5f) * 20f;
            _bl[i] = 4f + 2f * Rand(); _bsize[i] = 0.02f + 0.06f * Rand();
            DebrisSpawned++;
        }
    }

    /// <summary>핫패스: 2차 입자(물방울 · 파편) 탄도 + 착지 · 젖음 증발 · 3DGS 젖음 피드백(무할당).</summary>
    public void StepSecondary(float dt)
    {
        using var guard = ZeroGcRegionScope.Enter("VfxGuard.Secondary");
        var terrain = Terrain;
        for (var i = 0; i < MaxDroplets; i++)
        {
            if (_dl[i] <= 0f) continue;
            _dl[i] -= dt;
            _dvy[i] += Gravity * dt;
            _dx[i] += _dvx[i] * dt; _dy[i] += _dvy[i] * dt; _dz[i] += _dvz[i] * dt;
            var gy = terrain?.HeightAt(_dx[i], _dz[i]) ?? 0f;
            if (_dy[i] <= gy)
            {
                var a = terrain is null ? 0.5f : Math.Clamp(terrain.AbsorptionAt(_dx[i], _dz[i]), 0f, 1f);
                Wet(_dx[i], _dz[i], 0.03f * (0.4f + a));
                _dl[i] = 0f;
            }
            else if (_proxies > 0)
            {
                var p = new Vector3(_dx[i], _dy[i], _dz[i]);
                for (var k = 0; k < _proxies; k++)
                {
                    if (Vector3.DistanceSquared(p, _proxyC[k]) >= _proxyR[k] * _proxyR[k]) continue;
                    _proxyWet[k] = MathF.Min(1f, _proxyWet[k] + 0.02f * ProxyMaterial.Absorption);
                    _dl[i] = 0f; break;
                }
            }
        }
        for (var i = 0; i < MaxDebris; i++)
        {
            if (_bl[i] <= 0f) continue;
            _bl[i] -= dt;
            _bv[i].Y += Gravity * dt;
            _bp[i] += _bv[i] * dt;
            var gy = terrain?.HeightAt(_bp[i].X, _bp[i].Z) ?? 0f;
            if (_bp[i].Y < gy + _bsize[i])
            {
                _bp[i].Y = gy + _bsize[i];
                if (_bv[i].Y < 0f) _bv[i] = new Vector3(_bv[i].X * 0.6f, -_bv[i].Y * HardMaterial.Restitution, _bv[i].Z * 0.6f);
                _bs[i] *= 0.8f;
            }
        }

        // 젖음 증발 · 커버리지
        var dry = DryingPerSec * dt; var wetCells = 0;
        for (var i = 0; i < _wet.Length; i++)
        {
            var w = _wet[i] - dry * (0.3f + _wet[i]);
            _wet[i] = w > 0f ? w : 0f;
            if (_wet[i] > 0.1f) wetCells++;
        }
        WetCoverage = wetCells / (float)_wet.Length;
        for (var k = 0; k < _proxies; k++) { var w = _proxyWet[k] - dry * 0.5f; _proxyWet[k] = w > 0f ? w : 0f; }
        ApplyWetnessToSplats();
    }

    /// <summary>젖은 표면 피드백: 프록시 젖음 → 결합 스플랫 기본색 최대 40% 어둡게(원본 DC 기준 · 누적 없음).</summary>
    public void ApplyWetnessToSplats()
    {
        var s = _scene; if (s is null) return;
        var n = Math.Min(s.Count, _splatProxy.Length);
        for (var i = 0; i < n; i++)
        {
            var f = 1f - 0.4f * _proxyWet[_splatProxy[i]];
            var o = i * GaussianScene.ShStride;
            // DC → 색(0.5 + C0·dc) 을 f 배 → 다시 DC
            for (var c = 0; c < 3; c++) s.Sh[o + c] = ((0.5f + GaussianScene.ShC0 * _baseDc[i * 3 + c]) * f - 0.5f) / GaussianScene.ShC0;
        }
        s.Version++;
    }

    private void RestoreSplats()
    {
        var s = _scene; if (s is null) return;
        var n = Math.Min(s.Count, _splatProxy.Length);
        for (var i = 0; i < n; i++) for (var c = 0; c < 3; c++) s.Sh[i * GaussianScene.ShStride + c] = _baseDc[i * 3 + c];
        s.Version++;
    }

    private void Wet(float x, float z, float amount)
    {
        var u = (x - TerrainMin.X) / (TerrainMax.X - TerrainMin.X); var w = (z - TerrainMin.Y) / (TerrainMax.Y - TerrainMin.Y);
        if (u < 0f || u >= 1f || w < 0f || w >= 1f) return;
        var cx = (int)(u * WetN); var cz = (int)(w * WetN);
        // 3×3 가중 스플랫
        for (var dz = -1; dz <= 1; dz++)
            for (var dx = -1; dx <= 1; dx++)
            {
                int ix = cx + dx, iz = cz + dz;
                if ((uint)ix >= WetN || (uint)iz >= WetN) continue;
                var k = dx == 0 && dz == 0 ? 1f : (dx == 0 || dz == 0 ? 0.5f : 0.25f);
                var c = iz * WetN + ix;
                _wet[c] = MathF.Min(1f, _wet[c] + amount * k);
            }
    }

    public static Vector3 TerrainNormal(IHeightField t, float x, float z, float eps)
    {
        var hx = t.HeightAt(x + eps, z) - t.HeightAt(x - eps, z);
        var hz = t.HeightAt(x, z + eps) - t.HeightAt(x, z - eps);
        return Vector3.Normalize(new Vector3(-hx, 2f * eps, -hz));
    }

    private static VfxSurfaceMaterial Blend(in VfxSurfaceMaterial a, in VfxSurfaceMaterial b, float t) =>
        new(t < 0.5f ? a.Name : b.Name, a.Restitution + (b.Restitution - a.Restitution) * t, a.Friction + (b.Friction - a.Friction) * t,
            a.Absorption + (b.Absorption - a.Absorption) * t, a.SplashSpeed + (b.SplashSpeed - a.SplashSpeed) * t);

    private float Rand()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17;
        return (_rng >> 40) / (float)(1 << 24);
    }
}
