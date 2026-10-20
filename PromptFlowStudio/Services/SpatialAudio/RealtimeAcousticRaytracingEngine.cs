using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Neural3d;
using PromptFlowStudio.Services.OpenUsd;

namespace PromptFlowStudio.Services.SpatialAudio;

/// <summary>옥타브 대역 8(125 Hz ~ 8 kHz + 25 kHz 초음파) · 공기 흡음(ISO 9613-1 · 20 °C · 50 %RH 근사 · 에너지 1/m).</summary>
public static class AcousticBands
{
    public const int Count = 8;
    public const float SpeedOfSound = 343f;
    public static readonly float[] Center = { 125, 250, 500, 1000, 2000, 4000, 8000, 25000 };
    public static readonly string[] Names = { "125", "250", "500", "1k", "2k", "4k", "8k", "25k" };
    public static readonly float[] AirAbsorption = { 9.2e-5f, 2.3e-4f, 4.4e-4f, 8.5e-4f, 2.2e-3f, 7.6e-3f, 2.7e-2f, 1.4e-1f };
}

/// <summary>대역별 흡음률 α(0..1) · 산란 계수 s(0 = 정반사 · 1 = 램버트).</summary>
public sealed class AcousticMaterial
{
    public required string Name { get; init; }
    public required float[] Absorption { get; init; }
    public float Scattering { get; init; } = 0.1f;

    public static readonly AcousticMaterial Concrete = new() { Name = "concrete", Absorption = new[] { 0.01f, 0.01f, 0.02f, 0.02f, 0.02f, 0.03f, 0.04f, 0.05f }, Scattering = 0.05f };
    public static readonly AcousticMaterial Brick = new() { Name = "brick", Absorption = new[] { 0.03f, 0.03f, 0.03f, 0.04f, 0.05f, 0.07f, 0.08f, 0.1f }, Scattering = 0.15f };
    public static readonly AcousticMaterial Wood = new() { Name = "wood", Absorption = new[] { 0.3f, 0.25f, 0.15f, 0.1f, 0.1f, 0.07f, 0.07f, 0.08f }, Scattering = 0.1f };
    public static readonly AcousticMaterial Carpet = new() { Name = "carpet", Absorption = new[] { 0.05f, 0.1f, 0.2f, 0.35f, 0.5f, 0.6f, 0.65f, 0.7f }, Scattering = 0.2f };
    public static readonly AcousticMaterial Curtain = new() { Name = "curtain", Absorption = new[] { 0.07f, 0.31f, 0.49f, 0.75f, 0.7f, 0.6f, 0.6f, 0.6f }, Scattering = 0.4f };
    public static readonly AcousticMaterial Glass = new() { Name = "glass", Absorption = new[] { 0.35f, 0.25f, 0.18f, 0.12f, 0.07f, 0.04f, 0.03f, 0.03f }, Scattering = 0.02f };
    public static readonly AcousticMaterial Foliage = new() { Name = "foliage", Absorption = new[] { 0.1f, 0.15f, 0.25f, 0.35f, 0.45f, 0.5f, 0.55f, 0.6f }, Scattering = 0.7f };
    public static readonly AcousticMaterial Grass = new() { Name = "grass", Absorption = new[] { 0.11f, 0.26f, 0.6f, 0.69f, 0.92f, 0.99f, 0.99f, 0.99f }, Scattering = 0.6f };
    public static readonly AcousticMaterial Open = new() { Name = "open", Absorption = new[] { 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f }, Scattering = 0f };

    public static readonly AcousticMaterial[] Presets = { Concrete, Brick, Wood, Carpet, Curtain, Glass, Foliage, Grass, Open };

    public static AcousticMaterial Uniform(string name, float alpha, float scattering = 0f) =>
        new() { Name = name, Absorption = new[] { alpha, alpha, alpha, alpha, alpha, alpha, alpha, alpha }, Scattering = scattering };

    /// <summary>이름 키워드 → 프리셋(없으면 콘크리트).</summary>
    public static AcousticMaterial ByKeyword(string name)
    {
        var n = name.ToLowerInvariant();
        foreach (var m in Presets) if (n.Contains(m.Name, StringComparison.Ordinal)) return m;
        if (n.Contains("tree", StringComparison.Ordinal) || n.Contains("bush", StringComparison.Ordinal)) return Foliage;
        if (n.Contains("sofa", StringComparison.Ordinal) || n.Contains("fabric", StringComparison.Ordinal)) return Curtain;
        if (n.Contains("window", StringComparison.Ordinal)) return Glass;
        return Concrete;
    }
}

/// <summary>초기 반사 탭(지연 s · 대역 진폭 이득 · 청취자 기준 도래 방향(월드) · 반사 차수).</summary>
public struct EarlyReflection
{
    public float Delay;
    public Vector3 Direction;
    public int Order;
    public float G0, G1, G2, G3, G4, G5, G6, G7;

    public readonly float Gain(int b) => b switch { 0 => G0, 1 => G1, 2 => G2, 3 => G3, 4 => G4, 5 => G5, 6 => G6, _ => G7 };

    public void SetGain(int b, float v)
    {
        switch (b) { case 0: G0 = v; break; case 1: G1 = v; break; case 2: G2 = v; break; case 3: G3 = v; break; case 4: G4 = v; break; case 5: G5 = v; break; case 6: G6 = v; break; default: G7 = v; break; }
    }
}

/// <summary>
/// [PromptFlow Studio v16.76.0] 🎧 실시간 공간 음향 레이트레이싱 &amp; 벽면 잔향 반사 엔진.
///   ▸ 기하: 실내 슈박스(6면 재질) 또는 야외 지면 + 박스/구 장애물 — OpenUSD 합성 스테이지(Cube/Sphere · 프림 이름 키워드 재질) · 3DGS 가우시안(식생 구) 바인딩.
///   ▸ 확률적 음선 추적: 피보나치 구면 N 음선 · 구 SoA <see cref="Vector{T}"/> 레인 일괄 교차 · 대역별 흡음(1−α)·공기 흡음 · 산란 계수 확률 정반사/램버트 · 수신 구 통과 시 에너지/단면적 적립(1 ms 빈 × 8 대역).
///   ▸ 해석: Schroeder 역적분 EDC → 대역별 RT60(T20 회귀) · EDT · C50/C80 · D50 · 대역별 직접음.
///   ▸ 초기 반사: 이미지 소스 1·2차(슈박스 · 지면) — 지연 · 도래 방향 · 대역 이득 · 장애물 가림 검사.
///   ▸ 폐색/회절: 가시선 차단 시 장애물 모서리(박스 12 모서리 삼분 탐색 · 구 측지 우회) 최단 우회 경로차 δ → Maekawa 감쇠 10log(3+20N), N = 2δ/λ 대역별(저역일수록 회절 큼).
/// </summary>
public sealed class RealtimeAcousticRaytracingEngine
{
    public const int Bands = AcousticBands.Count, MaxSpheres = 256, MaxBoxes = 64, Bins = 2000, MaxReflections = 64, VisRays = 32, VisPoints = 8;
    public const float BinSeconds = 0.001f;
    private const float Eps = 1e-4f;

    private readonly object _gate = new();
    private readonly float[] _sx = new float[MaxSpheres], _sy = new float[MaxSpheres], _sz = new float[MaxSpheres], _sr2 = new float[MaxSpheres], _sr = new float[MaxSpheres];
    private readonly int[] _smat = new int[MaxSpheres];
    private int _ns;
    private readonly Vector3[] _bmin = new Vector3[MaxBoxes], _bmax = new Vector3[MaxBoxes];
    private readonly int[] _bmat = new int[MaxBoxes];
    private int _nb;
    private readonly List<AcousticMaterial> _mats = new();
    private float[] _alpha = Array.Empty<float>(), _scatter = Array.Empty<float>();
    private bool _room;
    private Vector3 _roomMin, _roomMax;
    private readonly int[] _wallMat = new int[6];
    private int _groundMat = -1;
    private readonly float[] _hist = new float[Bands * Bins], _edc = new float[Bands * Bins];
    private readonly float[] _rt60 = new float[Bands], _edt = new float[Bands], _direct = new float[Bands], _occl = new float[Bands], _e = new float[Bands];
    private readonly EarlyReflection[] _refl = new EarlyReflection[MaxReflections];
    private int _reflN;
    private readonly Vector3[] _vis = new Vector3[VisRays * VisPoints];
    private readonly int[] _visN = new int[VisRays];
    private Vector3 _src, _lis;
    private float _c50, _c80, _d50, _detour;
    private bool _los;
    private long _traces, _ticks, _segments;

    public RealtimeAcousticRaytracingEngine()
    {
        AddMaterial(AcousticMaterial.Concrete);
        Array.Fill(_sr2, -1f);
        SetRoom(new Vector3(-5, 0, -4), new Vector3(5, 3.5f, 4), AcousticMaterial.Concrete);
    }

    public int RayCount { get; set; } = 2048;
    public int MaxOrder { get; set; } = 60;
    public float ReceiverRadius { get; set; } = 0.35f;
    public bool AirAbsorption { get; set; } = true;
    public ulong Seed { get; set; } = 0x1676;

    public bool IsRoom => _room;
    public (Vector3 Min, Vector3 Max) Room => (_roomMin, _roomMax);
    public int SphereCount => _ns;
    public int BoxCount => _nb;
    public Vector3 Source => _src;
    public Vector3 Listener => _lis;
    public long Traces => Interlocked.Read(ref _traces);
    public long Segments => Interlocked.Read(ref _segments);
    public double LastMs => _ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    public static int SimdLanes => Vector<float>.Count;
    public bool LineOfSight => _los;
    /// <summary>회절 최단 우회 경로차 δ(m · 가시선이면 0).</summary>
    public float DetourMeters => _detour;
    public float C50 => _c50;
    public float C80 => _c80;
    public float D50 => _d50;
    public float Rt60(int band) => _rt60[band];
    public float Edt(int band) => _edt[band];
    /// <summary>직접음 진폭 이득(대역 · 거리 · 공기 · 폐색 포함).</summary>
    public float DirectGain(int band) => _direct[band];
    /// <summary>폐색 진폭 계수(1 = 가시선).</summary>
    public float Occlusion(int band) => _occl[band];
    public float Histogram(int band, int bin) => _hist[band * Bins + bin];
    public float EnergyDecayDb(int band, int bin) => _edc[band * Bins + bin];
    public int ReflectionCount => _reflN;
    public EarlyReflection Reflection(int i) => _refl[i];
    public int VisPointCount(int ray) => _visN[ray];
    public Vector3 VisPoint(int ray, int k) => _vis[ray * VisPoints + k];

    public float Volume => _room ? (_roomMax.X - _roomMin.X) * (_roomMax.Y - _roomMin.Y) * (_roomMax.Z - _roomMin.Z) : 0;

    // ══════════════════════ 장면(제어 평면) ══════════════════════

    public int AddMaterial(AcousticMaterial m)
    {
        lock (_gate)
        {
            var i = _mats.IndexOf(m);
            if (i >= 0) return i;
            if (m.Absorption.Length != Bands) throw new ArgumentException("흡음률은 8 대역이어야 합니다.", nameof(m));
            _mats.Add(m);
            _alpha = new float[_mats.Count * Bands]; _scatter = new float[_mats.Count];
            for (var k = 0; k < _mats.Count; k++)
            {
                for (var b = 0; b < Bands; b++) _alpha[k * Bands + b] = Math.Clamp(_mats[k].Absorption[b], 0f, 1f);
                _scatter[k] = Math.Clamp(_mats[k].Scattering, 0f, 1f);
            }
            return _mats.Count - 1;
        }
    }

    public AcousticMaterial Material(int i) => _mats[i];

    /// <summary>슈박스 실내(벽 순서 −X, +X, 바닥, 천장, −Z, +Z).</summary>
    public void SetRoom(Vector3 min, Vector3 max, AcousticMaterial all, AcousticMaterial? floor = null, AcousticMaterial? ceiling = null)
    {
        lock (_gate)
        {
            _room = true; _groundMat = -1;
            _roomMin = Vector3.Min(min, max); _roomMax = Vector3.Max(min, max);
            var a = AddMaterial(all);
            for (var w = 0; w < 6; w++) _wallMat[w] = a;
            if (floor is not null) _wallMat[2] = AddMaterial(floor);
            if (ceiling is not null) _wallMat[3] = AddMaterial(ceiling);
        }
    }

    public void SetWall(int wall, AcousticMaterial m) { lock (_gate) _wallMat[wall] = AddMaterial(m); }

    /// <summary>야외(지면 y = 0 만 · null = 자유 음장).</summary>
    public void SetOutdoor(AcousticMaterial? ground)
    {
        lock (_gate) { _room = false; _groundMat = ground is null ? -1 : AddMaterial(ground); }
    }

    public void ClearObstacles()
    {
        lock (_gate) { _ns = 0; _nb = 0; Array.Fill(_sr2, -1f); }
    }

    public int AddSphere(Vector3 c, float r, AcousticMaterial m)
    {
        var mat = AddMaterial(m);
        lock (_gate)
        {
            if (_ns >= MaxSpheres) return -1;
            var i = _ns++;
            _sx[i] = c.X; _sy[i] = c.Y; _sz[i] = c.Z; _sr[i] = MathF.Max(1e-3f, r); _sr2[i] = _sr[i] * _sr[i]; _smat[i] = mat;
            return i;
        }
    }

    public int AddBox(Vector3 min, Vector3 max, AcousticMaterial m)
    {
        var mat = AddMaterial(m);
        lock (_gate)
        {
            if (_nb >= MaxBoxes) return -1;
            var i = _nb++;
            _bmin[i] = Vector3.Min(min, max); _bmax[i] = Vector3.Max(min, max); _bmat[i] = mat;
            return i;
        }
    }

    /// <summary>USD 합성 스테이지 → 장애물(정의 · 가시 Cube = 박스 · Sphere = 구 · 재질 = 프림 이름 키워드). 반환 = 추가 수.</summary>
    public int BuildFromUsd(OpenUsdSpatialAssetCompositorEngine comp)
    {
        var st = comp.Stage;
        var n = 0;
        for (var i = 0; i < st.Count; i++)
        {
            var s = comp.State(i);
            if (!s.Defined || !s.Visible) continue;
            var c = new Vector3(s.X, s.Y, s.Z);
            var mat = AcousticMaterial.ByKeyword(new string(st.NameOf(i)));
            if (s.Type == UsdPrimType.Cube) { var h = new Vector3(st.SizeOf(i) * 0.5f * s.Scale); if (AddBox(c - h, c + h, mat) >= 0) n++; }
            else if (s.Type == UsdPrimType.Sphere && AddSphere(c, st.RadiusOf(i) * s.Scale, mat) >= 0) n++;
        }
        return n;
    }

    /// <summary>3DGS 가우시안 → 식생 산란 구(반경 = 2σ · 불투명도 ≥ minOpacity). 반환 = 추가 수.</summary>
    public int AddGaussians(GaussianScene gs, float minOpacity = 0.3f, int max = 128)
    {
        var n = 0;
        for (var i = 0; i < gs.Count && n < max; i++)
        {
            if (gs.Opacity[i] < minOpacity) continue;
            var s = gs.ScaleOf(i);
            if (AddSphere(gs.Position(i), 2f * (s.X + s.Y + s.Z) / 3f, AcousticMaterial.Foliage) < 0) break;
            n++;
        }
        return n;
    }

    // ══════════════════════ 교차 ══════════════════════

    private bool Closest(Vector3 o, Vector3 d, float tMax, out float t, out Vector3 n, out int mat, bool includeBoundary)
    {
        t = tMax; n = default; mat = -1;
        var vc = Vector<float>.Count;
        var padded = (_ns + vc - 1) / vc * vc;
        if (padded > 0)
        {
            Vector<float> ox = new(o.X), oy = new(o.Y), oz = new(o.Z), dx = new(d.X), dy = new(d.Y), dz = new(d.Z), eps = new(Eps);
            for (var i = 0; i < padded; i += vc)
            {
                var ocx = ox - new Vector<float>(_sx, i); var ocy = oy - new Vector<float>(_sy, i); var ocz = oz - new Vector<float>(_sz, i);
                var b = ocx * dx + ocy * dy + ocz * dz;
                var disc = b * b - (ocx * ocx + ocy * ocy + ocz * ocz - new Vector<float>(_sr2, i));
                var hm = Vector.GreaterThan(disc, Vector<float>.Zero);
                if (Vector.EqualsAll(hm, Vector<int>.Zero)) continue;
                var sq = Vector.SquareRoot(Vector.Max(disc, Vector<float>.Zero));
                var t0 = -b - sq;
                var valid = Vector.BitwiseAnd(hm, Vector.BitwiseAnd(Vector.GreaterThan(t0, eps), Vector.LessThan(t0, new Vector<float>(t))));
                if (Vector.EqualsAll(valid, Vector<int>.Zero)) continue;
                for (var l = 0; l < vc; l++)
                    if (valid[l] != 0 && t0[l] < t)
                    {
                        t = t0[l]; mat = _smat[i + l];
                        n = Vector3.Normalize(o + d * t - new Vector3(_sx[i + l], _sy[i + l], _sz[i + l]));
                    }
            }
        }
        for (var i = 0; i < _nb; i++)
        {
            if (!SlabEnter(_bmin[i], _bmax[i], o, d, t, out var tb, out var axis, out var sgn)) continue;
            t = tb; mat = _bmat[i];
            n = axis == 0 ? new Vector3(sgn, 0, 0) : axis == 1 ? new Vector3(0, sgn, 0) : new Vector3(0, 0, sgn);
        }
        if (!includeBoundary) return mat >= 0;
        if (_room)
        {
            var tx = d.X > 0 ? (_roomMax.X - o.X) / d.X : d.X < 0 ? (_roomMin.X - o.X) / d.X : float.MaxValue;
            var ty = d.Y > 0 ? (_roomMax.Y - o.Y) / d.Y : d.Y < 0 ? (_roomMin.Y - o.Y) / d.Y : float.MaxValue;
            var tz = d.Z > 0 ? (_roomMax.Z - o.Z) / d.Z : d.Z < 0 ? (_roomMin.Z - o.Z) / d.Z : float.MaxValue;
            var tw = MathF.Min(tx, MathF.Min(ty, tz));
            if (tw < t && tw > 0)
            {
                t = tw;
                if (tw == tx) { n = new Vector3(d.X > 0 ? -1 : 1, 0, 0); mat = _wallMat[d.X > 0 ? 1 : 0]; }
                else if (tw == ty) { n = new Vector3(0, d.Y > 0 ? -1 : 1, 0); mat = _wallMat[d.Y > 0 ? 3 : 2]; }
                else { n = new Vector3(0, 0, d.Z > 0 ? -1 : 1); mat = _wallMat[d.Z > 0 ? 5 : 4]; }
            }
        }
        else if (_groundMat >= 0 && d.Y < -1e-7f)
        {
            var tg = -o.Y / d.Y;
            if (tg > Eps && tg < t) { t = tg; n = Vector3.UnitY; mat = _groundMat; }
        }
        return mat >= 0;
    }

    private static float Comp(Vector3 v, int a) => a == 0 ? v.X : a == 1 ? v.Y : v.Z;

    private static bool SlabEnter(Vector3 mn, Vector3 mx, Vector3 o, Vector3 d, float tMax, out float t, out int axis, out float sgn)
    {
        float t0 = float.NegativeInfinity, t1 = float.PositiveInfinity;
        axis = 0; sgn = 0; t = 0;
        for (var a = 0; a < 3; a++)
        {
            float oa = Comp(o, a), da = Comp(d, a), lo = Comp(mn, a), hi = Comp(mx, a);
            if (MathF.Abs(da) < 1e-9f) { if (oa < lo || oa > hi) return false; continue; }
            var inv = 1f / da;
            float ta = (lo - oa) * inv, tb = (hi - oa) * inv, s = -1;
            if (ta > tb) { (ta, tb) = (tb, ta); s = 1; }
            if (ta > t0) { t0 = ta; axis = a; sgn = s; }
            t1 = MathF.Min(t1, tb);
            if (t0 > t1) return false;
        }
        if (t0 <= Eps || t0 >= tMax) return false;   // 내부 출발/뒤쪽 = 무시
        t = t0;
        return true;
    }

    /// <summary>a→b 선분이 장애물(박스/구)에 막히는가(경계 제외).</summary>
    public bool Blocked(Vector3 a, Vector3 b)
    {
        var d = b - a; var len = d.Length();
        if (len < 1e-6f) return false;
        return Closest(a, d / len, len - Eps, out _, out _, out _, false);
    }

    // ══════════════════════ 추적(무할당) ══════════════════════

    private struct Rng
    {
        private ulong _s;
        public Rng(ulong s) => _s = s;
        public float Next()
        {
            _s += 0x9E3779B97F4A7C15UL;
            var z = _s;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL; z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL; z ^= z >> 31;
            return (z >> 40) * (1f / (1 << 24));
        }
    }

    private static Vector3 Lambert(Vector3 n, float u1, float u2)
    {
        var s = n.Z >= 0 ? 1f : -1f; var a = -1f / (s + n.Z); var bb = n.X * n.Y * a;
        var t = new Vector3(1 + s * n.X * n.X * a, s * bb, -s * n.X);
        var b = new Vector3(bb, s + n.Y * n.Y * a, -n.Y);
        var r = MathF.Sqrt(u1); var phi = MathF.Tau * u2;
        return Vector3.Normalize(t * (r * MathF.Cos(phi)) + b * (r * MathF.Sin(phi)) + n * MathF.Sqrt(MathF.Max(0, 1 - u1)));
    }

    /// <summary>음원·청취자에 대해 음선 추적 + 해석 + 초기 반사 + 폐색. 무할당.</summary>
    public void Trace(Vector3 source, Vector3 listener)
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Acoustic.Trace");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _src = source; _lis = listener;
            Array.Clear(_hist);
            Array.Clear(_visN);
            var n = Math.Max(16, RayCount);
            var r = ReceiverRadius; var r2 = r * r;
            var area = MathF.PI * r2;
            var c = AcousticBands.SpeedOfSound;
            var rng = new Rng(Seed);
            var rot = Quaternion.CreateFromYawPitchRoll(rng.Next() * MathF.Tau, rng.Next() * MathF.Tau, rng.Next() * MathF.Tau);
            var e = _e;
            long segs = 0;
            for (var i = 0; i < n; i++)
            {
                // 피보나치 구면(전역 회전으로 격자 편향 제거)
                var z = 1 - 2 * (i + 0.5f) / n; var rr = MathF.Sqrt(MathF.Max(0, 1 - z * z)); var ph = i * 2.399963f;
                var d = Vector3.Transform(new Vector3(rr * MathF.Cos(ph), z, rr * MathF.Sin(ph)), rot);
                var o = source;
                var dist = 0f;
                for (var b = 0; b < Bands; b++) e[b] = 1f / n;
                if (i < VisRays) { _vis[i * VisPoints] = o; _visN[i] = 1; }
                for (var order = 0; order < MaxOrder; order++)
                {
                    var hit = Closest(o, d, 1e4f, out var t, out var nrm, out var mat, true);
                    segs++;
                    if (order > 0)
                    {
                        // 수신 구 통과(직접음은 해석적으로 별도 처리)
                        var v = listener - o;
                        var tca = Vector3.Dot(v, d);
                        if (tca > 0 && tca < (hit ? t : 1e4f))
                        {
                            var dd = v.LengthSquared() - tca * tca;
                            if (dd < r2)
                            {
                                var bin = (int)((dist + tca) / c / BinSeconds);
                                if ((uint)bin < Bins)
                                    for (var bnd = 0; bnd < Bands; bnd++)
                                        _hist[bnd * Bins + bin] += e[bnd] * (AirAbsorption ? MathF.Exp(-AcousticBands.AirAbsorption[bnd] * tca) : 1f) / area;
                            }
                        }
                    }
                    if (!hit) break;
                    var p = o + d * t;
                    dist += t;
                    var maxE = 0f;
                    for (var bnd = 0; bnd < Bands; bnd++)
                    {
                        var att = (1 - _alpha[mat * Bands + bnd]) * (AirAbsorption ? MathF.Exp(-AcousticBands.AirAbsorption[bnd] * t) : 1f);
                        e[bnd] *= att;
                        if (e[bnd] > maxE) maxE = e[bnd];
                    }
                    if (i < VisRays && _visN[i] < VisPoints) _vis[i * VisPoints + _visN[i]++] = p;
                    if (maxE < 1e-9f / n || dist / c > Bins * BinSeconds) break;
                    if (Vector3.Dot(d, nrm) > 0) nrm = -nrm;
                    d = rng.Next() < _scatter[mat] ? Lambert(nrm, rng.Next(), rng.Next()) : Vector3.Reflect(d, nrm);
                    o = p + nrm * 1e-3f;
                }
            }
            _segments += segs;
            Direct();
            Analyze();
            ImageSources();
            _traces++;
            _ticks = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        }
    }

    private void Direct()
    {
        var d = Vector3.Distance(_src, _lis);
        _los = !Blocked(_src, _lis);
        _detour = _los ? 0 : Detour(_src, _lis);
        var c = AcousticBands.SpeedOfSound;
        for (var b = 0; b < Bands; b++)
        {
            float occ = 1;
            if (!_los)
            {
                var lambda = c / AcousticBands.Center[b];
                var nF = 2 * _detour / lambda;
                var att = MathF.Min(24f, 10 * MathF.Log10(3 + 20 * nF));
                occ = MathF.Pow(10, -att / 20f);
            }
            _occl[b] = occ;
            var amp = 1f / MathF.Max(d, 0.1f) * (AirAbsorption ? MathF.Exp(-0.5f * AcousticBands.AirAbsorption[b] * d) : 1f) * occ;
            _direct[b] = amp;
            var bin = (int)(d / c / BinSeconds);
            if ((uint)bin < Bins) _hist[b * Bins + bin] += amp * amp / (4 * MathF.PI);
        }
    }

    /// <summary>임의 음원-청취자 폐색(추적 없이 · 대역 b 의 Maekawa 진폭 계수). 객체별 실시간 폐색용 · 무할당.</summary>
    public float OcclusionBetween(Vector3 s, Vector3 l, int band)
    {
        lock (_gate)
        {
            if (!Blocked(s, l)) return 1f;
            var nF = 2 * Detour(s, l) / (AcousticBands.SpeedOfSound / AcousticBands.Center[band]);
            return MathF.Pow(10, -MathF.Min(24f, 10 * MathF.Log10(3 + 20 * nF)) / 20f);
        }
    }

    /// <summary>가시선을 막는 장애물의 최단 우회 경로차(박스 모서리 · 구 측지).</summary>
    private float Detour(Vector3 s, Vector3 l)
    {
        var direct = Vector3.Distance(s, l);
        var best = float.MaxValue;
        Span<Vector3> corners = stackalloc Vector3[8];
        for (var i = 0; i < _nb; i++)
        {
            if (!SegmentHitsBox(s, l, _bmin[i], _bmax[i])) continue;
            var mn = _bmin[i] - new Vector3(1e-3f); var mx = _bmax[i] + new Vector3(1e-3f);
            for (var k = 0; k < 8; k++) corners[k] = new Vector3((k & 1) != 0 ? mx.X : mn.X, (k & 2) != 0 ? mx.Y : mn.Y, (k & 4) != 0 ? mx.Z : mn.Z);
            for (var a = 0; a < 8; a++)
                for (var bit = 1; bit < 8; bit <<= 1)
                {
                    if ((a & bit) != 0) continue;
                    var p = corners[a]; var q = corners[a | bit];
                    float lo = 0, hi = 1;
                    for (var it = 0; it < 24; it++)
                    {
                        float m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
                        var f1 = Vector3.Distance(s, Vector3.Lerp(p, q, m1)) + Vector3.Distance(Vector3.Lerp(p, q, m1), l);
                        var f2 = Vector3.Distance(s, Vector3.Lerp(p, q, m2)) + Vector3.Distance(Vector3.Lerp(p, q, m2), l);
                        if (f1 < f2) hi = m2; else lo = m1;
                    }
                    var ep = Vector3.Lerp(p, q, (lo + hi) * 0.5f);
                    if (_room ? ep.Y < _roomMin.Y || ep.Y > _roomMax.Y : _groundMat >= 0 && ep.Y < 0) continue;   // 바닥/천장 밖 경로 불가
                    if (SegmentHitsBox(s, ep, _bmin[i], _bmax[i]) || SegmentHitsBox(ep, l, _bmin[i], _bmax[i])) continue;
                    var len = Vector3.Distance(s, ep) + Vector3.Distance(ep, l);
                    if (len < best) best = len;
                }
            // 두꺼운 장애물: 같은 면의 평행 모서리 2개를 감싸는 경로(중첩 삼분 탐색)
            if (best == float.MaxValue)
                for (var a2 = 0; a2 < 8; a2++)
                    for (var bit = 1; bit < 8; bit <<= 1)
                    {
                        if ((a2 & bit) != 0) continue;
                        for (var other = 1; other < 8; other <<= 1)
                        {
                            if (other == bit) continue;
                            var c2 = a2 ^ other;
                            Vector3 p1 = corners[a2], q1 = corners[a2 | bit], p2 = corners[c2], q2 = corners[c2 | bit];
                            float lo1 = 0, hi1 = 1;
                            float bestT2 = 0.5f;
                            for (var it = 0; it < 18; it++)
                            {
                                float m1 = lo1 + (hi1 - lo1) / 3, m2 = hi1 - (hi1 - lo1) / 3;
                                var f1 = TwoEdge(s, l, Vector3.Lerp(p1, q1, m1), p2, q2, out _);
                                var f2 = TwoEdge(s, l, Vector3.Lerp(p1, q1, m2), p2, q2, out _);
                                if (f1 < f2) hi1 = m2; else lo1 = m1;
                            }
                            var e1 = Vector3.Lerp(p1, q1, (lo1 + hi1) * 0.5f);
                            var len = TwoEdge(s, l, e1, p2, q2, out bestT2);
                            var e2 = Vector3.Lerp(p2, q2, bestT2);
                            if (_room ? e1.Y < _roomMin.Y || e1.Y > _roomMax.Y || e2.Y < _roomMin.Y || e2.Y > _roomMax.Y : _groundMat >= 0 && (e1.Y < 0 || e2.Y < 0)) continue;
                            if (SegmentHitsBox(s, e1, _bmin[i], _bmax[i]) || SegmentHitsBox(e1, e2, _bmin[i], _bmax[i]) || SegmentHitsBox(e2, l, _bmin[i], _bmax[i])) continue;
                            if (len < best) best = len;
                        }
                    }
            // 최후: 모서리 꼭짓점 2개 경유(상한 근사)
            if (best == float.MaxValue)
                for (var a2 = 0; a2 < 8; a2++)
                {
                    if (_room ? corners[a2].Y < _roomMin.Y || corners[a2].Y > _roomMax.Y : _groundMat >= 0 && corners[a2].Y < 0) continue;
                    if (SegmentHitsBox(s, corners[a2], _bmin[i], _bmax[i])) continue;
                    for (var b2 = 0; b2 < 8; b2++)
                    {
                        if (b2 == a2 || (_room ? corners[b2].Y < _roomMin.Y || corners[b2].Y > _roomMax.Y : _groundMat >= 0 && corners[b2].Y < 0)) continue;
                        if (SegmentHitsBox(corners[a2], corners[b2], _bmin[i], _bmax[i]) || SegmentHitsBox(corners[b2], l, _bmin[i], _bmax[i])) continue;
                        var len = Vector3.Distance(s, corners[a2]) + Vector3.Distance(corners[a2], corners[b2]) + Vector3.Distance(corners[b2], l);
                        if (len < best) best = len;
                    }
                }
        }
        for (var i = 0; i < _ns; i++)
        {
            var cc = new Vector3(_sx[i], _sy[i], _sz[i]); var R = _sr[i];
            var a = Vector3.Distance(s, cc); var b = Vector3.Distance(l, cc);
            if (a <= R || b <= R) continue;
            var sl = l - s; var tt = Math.Clamp(Vector3.Dot(cc - s, sl) / sl.LengthSquared(), 0f, 1f);
            if (Vector3.Distance(s + sl * tt, cc) >= R) continue;
            var ang = MathF.Acos(Math.Clamp(Vector3.Dot(s - cc, l - cc) / (a * b), -1f, 1f));
            var arc = ang - MathF.Acos(R / a) - MathF.Acos(R / b);
            var len = MathF.Sqrt(a * a - R * R) + MathF.Sqrt(b * b - R * R) + R * MathF.Max(0, arc);
            if (len < best) best = len;
        }
        return best == float.MaxValue ? 0 : MathF.Max(0, best - direct);
    }

    /// <summary>s → e1 → e2(p2..q2 위 삼분 탐색) → l 최단 길이.</summary>
    private static float TwoEdge(Vector3 s, Vector3 l, Vector3 e1, Vector3 p2, Vector3 q2, out float t2)
    {
        float lo = 0, hi = 1;
        for (var it = 0; it < 18; it++)
        {
            float m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
            var f1 = Vector3.Distance(e1, Vector3.Lerp(p2, q2, m1)) + Vector3.Distance(Vector3.Lerp(p2, q2, m1), l);
            var f2 = Vector3.Distance(e1, Vector3.Lerp(p2, q2, m2)) + Vector3.Distance(Vector3.Lerp(p2, q2, m2), l);
            if (f1 < f2) hi = m2; else lo = m1;
        }
        t2 = (lo + hi) * 0.5f;
        var e2 = Vector3.Lerp(p2, q2, t2);
        return Vector3.Distance(s, e1) + Vector3.Distance(e1, e2) + Vector3.Distance(e2, l);
    }

    private static bool SegmentHitsBox(Vector3 a, Vector3 b, Vector3 mn, Vector3 mx)
    {
        var d = b - a;
        float t0 = 0, t1 = 1;
        for (var ax = 0; ax < 3; ax++)
        {
            float o = Comp(a, ax), dd = Comp(d, ax), lo = Comp(mn, ax), hi = Comp(mx, ax);
            if (MathF.Abs(dd) < 1e-9f) { if (o <= lo || o >= hi) return false; continue; }
            var ta = (lo - o) / dd; var tb = (hi - o) / dd;
            if (ta > tb) (ta, tb) = (tb, ta);
            t0 = MathF.Max(t0, ta); t1 = MathF.Min(t1, tb);
            if (t0 >= t1 - 1e-6f) return false;
        }
        return true;
    }

    private void Analyze()
    {
        float early50 = 0, late50 = 0, early80 = 0, late80 = 0;
        for (var b = 0; b < Bands; b++)
        {
            var o = b * Bins;
            var acc = 0.0;
            for (var k = Bins - 1; k >= 0; k--) { acc += _hist[o + k]; _edc[o + k] = (float)acc; }
            var total = _edc[o];
            for (var k = 0; k < Bins; k++) _edc[o + k] = total > 0 && _edc[o + k] > 0 ? 10 * MathF.Log10(_edc[o + k] / total) : -200f;
            _rt60[b] = Fit(o, -5, -25) is var s20 && s20 < 0 ? -60f / s20 : Fit(o, -5, -15) is var s10 && s10 < 0 ? -60f / s10 : 0;
            _edt[b] = Fit(o, 0, -10) is var se && se < 0 ? -60f / se : 0;
            if (b is 2 or 3)
            {
                for (var k = 0; k < Bins; k++)
                {
                    var v = _hist[o + k];
                    if (k < 50) early50 += v; else late50 += v;
                    if (k < 80) early80 += v; else late80 += v;
                }
            }
        }
        _c50 = late50 > 0 ? 10 * MathF.Log10(early50 / late50) : 60f;
        _c80 = late80 > 0 ? 10 * MathF.Log10(early80 / late80) : 60f;
        _d50 = early50 + late50 > 0 ? early50 / (early50 + late50) : 1f;
    }

    /// <summary>EDC 구간 [hi, lo] dB 선형 회귀 기울기(dB/s · 데이터 부족 0).</summary>
    private float Fit(int o, float hiDb, float loDb)
    {
        double sx = 0, sy = 0, sxx = 0, sxy = 0; var n = 0;
        for (var k = 0; k < Bins; k++)
        {
            var v = _edc[o + k];
            if (v > hiDb) continue;
            if (v < loDb) break;
            var x = k * BinSeconds;
            sx += x; sy += v; sxx += x * x; sxy += x * v; n++;
        }
        if (n < 5) return 0;
        var den = n * sxx - sx * sx;
        return den <= 0 ? 0 : (float)((n * sxy - sx * sy) / den);
    }

    /// <summary>이미지 소스 1·2차(슈박스 6면 · 야외 지면) — 장애물 가림 검사 · 대역 이득 · 도래 방향.</summary>
    private void ImageSources()
    {
        _reflN = 0;
        if (!_room)
        {
            if (_groundMat < 0) return;
            var img = new Vector3(_src.X, -_src.Y, _src.Z);
            var tHit = _lis.Y / MathF.Max(1e-6f, _lis.Y - img.Y);
            var pt = _lis + (img - _lis) * tHit;
            if (Blocked(_src, pt) || Blocked(pt, _lis)) return;
            AddReflection(img, 1, _groundMat, -1);
            return;
        }
        for (var w = 0; w < 6; w++)
        {
            var i1 = Mirror(_src, w);
            if (VisibleImage(i1, w, -1)) AddReflection(i1, 1, _wallMat[w], -1);
            for (var w2 = 0; w2 < 6; w2++)
            {
                if (w2 == w) continue;
                var i2 = Mirror(i1, w2);
                if (VisibleImage(i2, w2, w)) AddReflection(i2, 2, _wallMat[w2], _wallMat[w]);
            }
        }
        // 지연 오름차순
        for (var i = 1; i < _reflN; i++)
        {
            var x = _refl[i]; var j = i - 1;
            while (j >= 0 && _refl[j].Delay > x.Delay) { _refl[j + 1] = _refl[j]; j--; }
            _refl[j + 1] = x;
        }
    }

    private Vector3 Mirror(Vector3 p, int wall) => wall switch
    {
        0 => new Vector3(2 * _roomMin.X - p.X, p.Y, p.Z), 1 => new Vector3(2 * _roomMax.X - p.X, p.Y, p.Z),
        2 => new Vector3(p.X, 2 * _roomMin.Y - p.Y, p.Z), 3 => new Vector3(p.X, 2 * _roomMax.Y - p.Y, p.Z),
        4 => new Vector3(p.X, p.Y, 2 * _roomMin.Z - p.Z), _ => new Vector3(p.X, p.Y, 2 * _roomMax.Z - p.Z),
    };

    /// <summary>청취자 → 이미지 경로가 마지막 벽(w)에서 반사되고(2차는 이전 벽 경유) 장애물에 막히지 않는가.</summary>
    private bool VisibleImage(Vector3 img, int w, int prevWall)
    {
        var p2 = WallHit(_lis, img, w);
        if (p2 is not { } q2 || Blocked(_lis, q2)) return false;
        if (prevWall < 0) return !Blocked(q2, _src);
        var img1 = Mirror(_src, prevWall);
        var p1 = WallHit(q2, img1, prevWall);
        return p1 is { } q1 && !Blocked(q2, q1) && !Blocked(q1, _src);
    }

    private Vector3? WallHit(Vector3 from, Vector3 to, int w)
    {
        var axis = w / 2; var plane = (w & 1) == 0 ? Comp(_roomMin, axis) : Comp(_roomMax, axis);
        var a = Comp(from, axis); var b = Comp(to, axis);
        if (MathF.Abs(b - a) < 1e-9f) return null;
        var t = (plane - a) / (b - a);
        if (t <= 0 || t >= 1) return null;
        var p = from + (to - from) * t;
        var e = 1e-3f;
        if (p.X < _roomMin.X - e || p.X > _roomMax.X + e || p.Y < _roomMin.Y - e || p.Y > _roomMax.Y + e || p.Z < _roomMin.Z - e || p.Z > _roomMax.Z + e) return null;
        return p;
    }

    private void AddReflection(Vector3 img, int order, int mat1, int mat2)
    {
        if (_reflN >= MaxReflections) return;
        var d = Vector3.Distance(img, _lis);
        var r = new EarlyReflection { Delay = d / AcousticBands.SpeedOfSound, Direction = Vector3.Normalize(img - _lis), Order = order };
        for (var b = 0; b < Bands; b++)
        {
            var refl = MathF.Sqrt(1 - _alpha[mat1 * Bands + b]) * (mat2 >= 0 ? MathF.Sqrt(1 - _alpha[mat2 * Bands + b]) : 1f);
            r.SetGain(b, refl / MathF.Max(d, 0.1f) * (AirAbsorption ? MathF.Exp(-0.5f * AcousticBands.AirAbsorption[b] * d) : 1f));
        }
        _refl[_reflN++] = r;
    }

    /// <summary>Eyring 잔향 시간(검증 기준 · 공기 흡음 제외 · 슈박스 평균 흡음).</summary>
    public float EyringRt60(int band)
    {
        if (!_room) return 0;
        var s = _roomMax - _roomMin;
        Span<float> area = stackalloc float[] { s.Y * s.Z, s.Y * s.Z, s.X * s.Z, s.X * s.Z, s.X * s.Y, s.X * s.Y };
        float total = 0, absorb = 0;
        for (var w = 0; w < 6; w++) { total += area[w]; absorb += area[w] * _alpha[_wallMat[w] * Bands + band]; }
        var am = absorb / total;
        return 0.161f * (s.X * s.Y * s.Z) / (-total * MathF.Log(1 - MathF.Min(am, 0.999f)));
    }
}
