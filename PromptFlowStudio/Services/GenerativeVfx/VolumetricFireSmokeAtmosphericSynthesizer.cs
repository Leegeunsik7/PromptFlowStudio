using System.Diagnostics;
using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.GenerativeVfx;

public enum VfxEventKind : byte { None = 0, Explosion, Chimney, Fog, Fire }

public enum VolumePreset : byte { ThinHaze = 0, DenseSmoke, ToxicSmoke, Steam }

/// <summary>절차 이미터(고정 슬롯 · 값 형식).</summary>
public struct VolumeEmitter
{
    public VfxEventKind Kind;
    public Vector3 Position;
    public float Radius, Density, Heat, Speed, Life, Age;
    public bool Alive => Kind != VfxEventKind.None && Age < Life;
}

/// <summary>
/// [PromptFlow Studio v16.81.0] 🔥 제너레이티브 볼류메트릭 화염 &amp; 대기 연기 합성기(N³ 격자 · 무할당 프레임).
/// 프롬프트/시나리오 이벤트(폭발 · 굴뚝 연기 · 안개 · 화염 · 한/영 키워드) → 이미터 슬롯(16) → 밀도/온도/속도 주입 →
/// 부력(αT − βρ) · 결정적 컬 노이즈 난류 · 야코비 압력 투영(비압축) · 세미-라그랑주 이류(삼선형) · 소산/냉각 · 연소 그을음 →
/// 방향광 투과율(Beer–Lambert 광선 행진 · 조기 종료) → 시점(−Z) 정사영 볼륨 렌더(산란 · 흑체 방출 · 시점 투과율).
/// </summary>
public sealed class VolumetricFireSmokeAtmosphericSynthesizer
{
    public const int MaxEmitters = 16;

    private float[] _d = Array.Empty<float>(), _t = Array.Empty<float>(), _u = Array.Empty<float>(), _v = Array.Empty<float>(), _w = Array.Empty<float>();
    private float[] _d0 = Array.Empty<float>(), _t0 = Array.Empty<float>(), _u0 = Array.Empty<float>(), _v0 = Array.Empty<float>(), _w0 = Array.Empty<float>();
    private float[] _p = Array.Empty<float>(), _div = Array.Empty<float>(), _light = Array.Empty<float>();
    private float[] _imgR = Array.Empty<float>(), _imgG = Array.Empty<float>(), _imgB = Array.Empty<float>(), _imgT = Array.Empty<float>();
    private readonly VolumeEmitter[] _em = new VolumeEmitter[MaxEmitters];
    private int _n;
    private float _cell, _time;

    public int N => _n;
    public Vector3 BoxMin { get; private set; } = new(-2f, 0f, -2f);
    public float BoxSize { get; private set; } = 4f;

    // ── 물성(프리셋) ──
    public VolumePreset Preset { get; private set; } = VolumePreset.DenseSmoke;
    /// <summary>소광 계수(1/m · 밀도 1 기준).</summary>
    public float Extinction { get; set; } = 6f;
    public float Albedo { get; set; } = 0.7f;
    public Vector3 SmokeTint { get; set; } = new(0.82f, 0.8f, 0.78f);
    public float Dissipation { get; set; } = 0.08f;
    public float Cooling { get; set; } = 0.9f;
    public float Buoyancy { get; set; } = 2.2f;
    public float Weight { get; set; } = 0.35f;
    public float Turbulence { get; set; } = 1.2f;
    public float AmbientFog { get; set; }
    public int PressureIterations { get; set; } = 12;
    public Vector3 LightDirection { get; set; } = Vector3.Normalize(new Vector3(-0.4f, -1f, -0.3f));
    public Vector3 LightColor { get; set; } = new(1f, 0.95f, 0.88f);

    // ── 계측 ──
    public long Steps { get; private set; }
    public long Events { get; private set; }
    public double LastStepMs { get; private set; }
    public double LastLightMs { get; private set; }
    public float TotalDensity { get; private set; }
    public float MaxTemperature { get; private set; }
    public float MeanTransmittance { get; private set; } = 1f;
    public float MaxDivergence { get; private set; }
    public int ActiveEmitters { get; private set; }
    public VfxEventKind LastEvent { get; private set; }
    public string? LastAction { get; private set; }

    public ReadOnlySpan<float> ImageR => _imgR;
    public ReadOnlySpan<float> ImageG => _imgG;
    public ReadOnlySpan<float> ImageB => _imgB;
    public ReadOnlySpan<float> ImageTransmittance => _imgT;
    public ReadOnlySpan<float> Density => _d;
    public ReadOnlySpan<float> Temperature => _t;
    public ReadOnlySpan<float> LightTransmittance => _light;
    public VolumeEmitter Emitter(int i) => _em[i];

    public VolumetricFireSmokeAtmosphericSynthesizer(int n = 32) => Configure(n, BoxMin, BoxSize);

    /// <summary>제어 평면: 해상도 · 월드 박스(버퍼 1회 할당).</summary>
    public void Configure(int n, Vector3 min, float size)
    {
        n = Math.Clamp(n, 8, 96);
        BoxMin = min; BoxSize = size; _cell = size / n;
        if (_n != n)
        {
            _n = n; var c = n * n * n;
            _d = new float[c]; _t = new float[c]; _u = new float[c]; _v = new float[c]; _w = new float[c];
            _d0 = new float[c]; _t0 = new float[c]; _u0 = new float[c]; _v0 = new float[c]; _w0 = new float[c];
            _p = new float[c]; _div = new float[c]; _light = new float[c];
            _imgR = new float[n * n]; _imgG = new float[n * n]; _imgB = new float[n * n]; _imgT = new float[n * n];
        }
        Clear();
    }

    public void Clear()
    {
        Array.Clear(_d); Array.Clear(_t); Array.Clear(_u); Array.Clear(_v); Array.Clear(_w); Array.Clear(_p);
        Array.Fill(_light, 1f); Array.Fill(_imgT, 1f); Array.Clear(_imgR); Array.Clear(_imgG); Array.Clear(_imgB);
        Array.Clear(_em); AmbientFog = 0; _time = 0; ActiveEmitters = 0; MeanTransmittance = 1f;
    }

    public void SetPreset(VolumePreset p)
    {
        Preset = p;
        (Extinction, Albedo, SmokeTint, Dissipation) = p switch
        {
            VolumePreset.ThinHaze => (1.5f, 0.95f, new Vector3(0.92f, 0.94f, 0.98f), 0.03f),
            VolumePreset.ToxicSmoke => (7f, 0.55f, new Vector3(0.55f, 0.8f, 0.35f), 0.06f),
            VolumePreset.Steam => (3.5f, 0.99f, new Vector3(0.97f, 0.97f, 0.99f), 0.35f),
            _ => (6f, 0.7f, new Vector3(0.82f, 0.8f, 0.78f), 0.08f),
        };
        LastAction = "🌫 프리셋 " + PresetName(p);
    }

    public static string PresetName(VolumePreset p) => p switch
    {
        VolumePreset.ThinHaze => "옅은 연무", VolumePreset.ToxicSmoke => "유독 연기", VolumePreset.Steam => "증기", _ => "짙은 연기",
    };

    public static string EventName(VfxEventKind k) => k switch
    {
        VfxEventKind.Explosion => "💥 폭발", VfxEventKind.Chimney => "🏭 굴뚝 연기", VfxEventKind.Fog => "🌫 안개", VfxEventKind.Fire => "🔥 화염", _ => "—",
    };

    /// <summary>
    /// 제어 평면: 프롬프트/시나리오 문장 → 이벤트(한/영 키워드 · 대소문자 무시 · 여러 개 가능). 강도 수식어(거대/huge · 약한/small) 반영. 반환 = 발동 수.
    /// </summary>
    public int TriggerFromPrompt(string? prompt, Vector3 focus)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return 0;
        var s = prompt.AsSpan();
        var mul = Has(s, "거대") || Has(s, "huge") || Has(s, "massive") || Has(s, "대폭발") ? 1.8f : Has(s, "약한") || Has(s, "small") || Has(s, "작은") ? 0.5f : 1f;
        var n = 0;
        if (Has(s, "폭발") || Has(s, "explo") || Has(s, "blast") || Has(s, "boom")) { Trigger(VfxEventKind.Explosion, focus, mul); n++; }
        if (Has(s, "굴뚝") || Has(s, "chimney") || Has(s, "smokestack") || Has(s, "연기") || Has(s, "smoke")) { Trigger(VfxEventKind.Chimney, focus + new Vector3(0.8f, 0f, -0.6f), mul); n++; }
        if (Has(s, "안개") || Has(s, "fog") || Has(s, "mist") || Has(s, "연무") || Has(s, "haze")) { Trigger(VfxEventKind.Fog, focus, mul); n++; }
        if (Has(s, "화염") || Has(s, "불길") || Has(s, "fire") || Has(s, "flame") || Has(s, "불꽃") || Has(s, "모닥불")) { Trigger(VfxEventKind.Fire, focus, mul); n++; }
        return n;
    }

    private static bool Has(ReadOnlySpan<char> s, string k) => s.IndexOf(k.AsSpan(), StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>이벤트 → 이미터 슬롯(빈 슬롯/가장 오래된 슬롯 재사용 · 무할당).</summary>
    public void Trigger(VfxEventKind kind, Vector3 at, float intensity = 1f)
    {
        if (kind == VfxEventKind.None) return;
        intensity = Math.Clamp(intensity, 0.1f, 3f);
        var slot = 0; var oldest = -1f;
        for (var i = 0; i < MaxEmitters; i++)
        {
            if (!_em[i].Alive) { slot = i; oldest = float.MaxValue; break; }
            if (_em[i].Age > oldest) { oldest = _em[i].Age; slot = i; }
        }
        var floorY = BoxMin.Y + _cell * 1.5f;
        _em[slot] = kind switch
        {
            VfxEventKind.Explosion => new VolumeEmitter { Kind = kind, Position = new Vector3(at.X, MathF.Max(at.Y, floorY + 0.3f), at.Z), Radius = 0.35f * intensity, Density = 9f * intensity, Heat = 14f * intensity, Speed = 6f * intensity, Life = 0.25f },
            VfxEventKind.Chimney => new VolumeEmitter { Kind = kind, Position = new Vector3(at.X, floorY + 0.6f, at.Z), Radius = 0.14f, Density = 2.2f * intensity, Heat = 1.6f, Speed = 1.4f, Life = 1e9f },
            VfxEventKind.Fog => new VolumeEmitter { Kind = kind, Position = new Vector3(at.X, floorY, at.Z), Radius = BoxSize, Density = 0.12f * intensity, Heat = 0f, Speed = 0f, Life = 1e9f },
            _ => new VolumeEmitter { Kind = VfxEventKind.Fire, Position = new Vector3(at.X, floorY, at.Z), Radius = 0.22f * intensity, Density = 0.5f, Heat = 7f * intensity, Speed = 1.8f, Life = 1e9f },
        };
        Events++; LastEvent = kind;
        LastAction = EventName(kind) + " · 강도 " + intensity.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
    }

    public void StopContinuous() { for (var i = 0; i < MaxEmitters; i++) if (_em[i].Life > 1e8f) _em[i].Life = _em[i].Age; LastAction = "⏹ 지속 이미터 정지"; }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private int Ix(int x, int y, int z) => (z * _n + y) * _n + x;

    /// <summary>핫패스: 1 스텝(무할당).</summary>
    public void Step(float dt)
    {
        using var guard = ZeroGcRegionScope.Enter("Volume.Step");
        var t0 = Stopwatch.GetTimestamp();
        _time += dt;
        Inject(dt);
        Forces(dt);
        Project();
        Advect(dt);
        var t1 = Stopwatch.GetTimestamp();
        ComputeLight();
        LastLightMs = Stopwatch.GetElapsedTime(t1).TotalMilliseconds;
        Render();
        Steps++;
        LastStepMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    private void Inject(float dt)
    {
        var n = _n; var active = 0;
        for (var e = 0; e < MaxEmitters; e++)
        {
            ref var em = ref _em[e];
            if (!em.Alive) continue;
            active++;
            em.Age += dt;
            if (em.Kind == VfxEventKind.Fog)
            {
                // 저층 안개: 바닥 1/4 높이에 지수 감쇠 밀도
                for (var z = 0; z < n; z++)
                    for (var y = 0; y < n / 4; y++)
                    {
                        var add = em.Density * dt * MathF.Exp(-y * 0.45f);
                        for (var x = 0; x < n; x++) { var i = Ix(x, y, z); _d[i] = MathF.Min(_d[i] + add, 0.6f); }
                    }
                continue;
            }
            var c = (em.Position - BoxMin) / _cell;
            var r = em.Radius / _cell; var rc = (int)MathF.Ceiling(r);
            var gain = em.Kind == VfxEventKind.Explosion ? 1f / MathF.Max(em.Life, dt) * dt : dt;
            int x0 = Math.Max(0, (int)c.X - rc), x1 = Math.Min(n - 1, (int)c.X + rc), y0 = Math.Max(0, (int)c.Y - rc), y1 = Math.Min(n - 1, (int)c.Y + rc), z0 = Math.Max(0, (int)c.Z - rc), z1 = Math.Min(n - 1, (int)c.Z + rc);
            for (var z = z0; z <= z1; z++)
                for (var y = y0; y <= y1; y++)
                    for (var x = x0; x <= x1; x++)
                    {
                        var d = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) - c;
                        var q = d.Length() / MathF.Max(r, 0.5f);
                        if (q >= 1f) continue;
                        var f = (1f - q * q) * gain;
                        var i = Ix(x, y, z);
                        _d[i] += em.Density * f; _t[i] += em.Heat * f;
                        if (em.Kind == VfxEventKind.Explosion)
                        {
                            var dir = q > 1e-3f ? d / (q * MathF.Max(r, 0.5f)) : Vector3.UnitY;
                            _u[i] += dir.X * em.Speed * f * 4f; _v[i] += (dir.Y + 0.6f) * em.Speed * f * 4f; _w[i] += dir.Z * em.Speed * f * 4f;
                        }
                        else _v[i] = MathF.Max(_v[i], em.Speed * (1f - q));
                    }
        }
        ActiveEmitters = active;
    }

    private void Forces(float dt)
    {
        var n = _n; var tt = _time;
        for (var z = 0; z < n; z++)
            for (var y = 0; y < n; y++)
                for (var x = 0; x < n; x++)
                {
                    var i = Ix(x, y, z);
                    var d = _d[i]; var t = _t[i];
                    _v[i] += (Buoyancy * t - Weight * d) * dt;
                    if (d + t < 1e-4f) continue;
                    // 결정적 컬 노이즈(해석적 sin 포텐셜의 회전) — 연기/화염 결 난류
                    float px = x * 0.37f, py = y * 0.29f - tt * 1.3f, pz = z * 0.41f;
                    var cx = MathF.Cos(py + 1.7f) - MathF.Cos(pz + 0.3f);
                    var cy = MathF.Cos(pz + 2.1f) - MathF.Cos(px + 0.9f);
                    var cz = MathF.Cos(px + 0.4f) - MathF.Cos(py + 2.6f);
                    var k = Turbulence * dt * MathF.Min(1f, d + 0.3f * t);
                    _u[i] += cx * k; _v[i] += cy * k * 0.5f; _w[i] += cz * k;
                }
    }

    /// <summary>야코비 압력 투영(경계 = 벽 · 노이만).</summary>
    private void Project()
    {
        var n = _n; var maxDiv = 0f;
        for (var z = 0; z < n; z++)
            for (var y = 0; y < n; y++)
                for (var x = 0; x < n; x++)
                {
                    var i = Ix(x, y, z);
                    var du = (x < n - 1 ? _u[i + 1] : 0f) - (x > 0 ? _u[i - 1] : 0f);
                    var dv = (y < n - 1 ? _v[i + n] : 0f) - (y > 0 ? _v[i - n] : 0f);
                    var dw = (z < n - 1 ? _w[i + n * n] : 0f) - (z > 0 ? _w[i - n * n] : 0f);
                    var div = 0.5f * (du + dv + dw);
                    _div[i] = div;
                    if (MathF.Abs(div) > maxDiv) maxDiv = MathF.Abs(div);
                }
        MaxDivergence = maxDiv;
        Array.Clear(_p);
        var it = Math.Clamp(PressureIterations, 0, 64);
        for (var k = 0; k < it; k++)
        {
            // 적-흑 가우스-자이델(제자리 · 추가 버퍼 없음)
            for (var color = 0; color < 2; color++)
                for (var z = 0; z < n; z++)
                    for (var y = 0; y < n; y++)
                        for (var x = (y + z + color) & 1; x < n; x += 2)
                        {
                            var i = Ix(x, y, z);
                            float s = 0f; var cnt = 0;
                            if (x > 0) { s += _p[i - 1]; cnt++; }
                            if (x < n - 1) { s += _p[i + 1]; cnt++; }
                            if (y > 0) { s += _p[i - n]; cnt++; }
                            if (y < n - 1) { s += _p[i + n]; cnt++; }
                            if (z > 0) { s += _p[i - n * n]; cnt++; }
                            if (z < n - 1) { s += _p[i + n * n]; cnt++; }
                            _p[i] = (s - _div[i]) / cnt;
                        }
        }
        for (var z = 0; z < n; z++)
            for (var y = 0; y < n; y++)
                for (var x = 0; x < n; x++)
                {
                    var i = Ix(x, y, z);
                    _u[i] -= 0.5f * ((x < n - 1 ? _p[i + 1] : _p[i]) - (x > 0 ? _p[i - 1] : _p[i]));
                    _v[i] -= 0.5f * ((y < n - 1 ? _p[i + n] : _p[i]) - (y > 0 ? _p[i - n] : _p[i]));
                    _w[i] -= 0.5f * ((z < n - 1 ? _p[i + n * n] : _p[i]) - (z > 0 ? _p[i - n * n] : _p[i]));
                    if (x == 0 && _u[i] < 0 || x == n - 1 && _u[i] > 0) _u[i] = 0;
                    if (y == 0 && _v[i] < 0 || y == n - 1 && _v[i] > 0) _v[i] = 0;
                    if (z == 0 && _w[i] < 0 || z == n - 1 && _w[i] > 0) _w[i] = 0;
                }
    }

    /// <summary>스칼라 필드 측정용 발산(투영 후 · 내부 셀 최대 |∇·u|).</summary>
    public float MeasureDivergence()
    {
        var n = _n; var m = 0f;
        for (var z = 1; z < n - 1; z++)
            for (var y = 1; y < n - 1; y++)
                for (var x = 1; x < n - 1; x++)
                {
                    var i = Ix(x, y, z);
                    var div = 0.5f * (_u[i + 1] - _u[i - 1] + _v[i + n] - _v[i - n] + _w[i + n * n] - _w[i - n * n]);
                    if (MathF.Abs(div) > m) m = MathF.Abs(div);
                }
        return m;
    }

    private void Advect(float dt)
    {
        var n = _n;
        _d.AsSpan().CopyTo(_d0); _t.AsSpan().CopyTo(_t0); _u.AsSpan().CopyTo(_u0); _v.AsSpan().CopyTo(_v0); _w.AsSpan().CopyTo(_w0);
        var s = dt / _cell;
        var keepD = MathF.Exp(-Dissipation * dt); var keepT = MathF.Exp(-Cooling * dt);
        var fog = AmbientFog;
        float total = 0f, maxT = 0f;
        for (var z = 0; z < n; z++)
            for (var y = 0; y < n; y++)
                for (var x = 0; x < n; x++)
                {
                    var i = Ix(x, y, z);
                    float bx = x - _u0[i] * s, by = y - _v0[i] * s, bz = z - _w0[i] * s;
                    var t = Sample(_t0, bx, by, bz) * keepT;
                    // 연소: 고온부 그을음 생성
                    var soot = t > 2f ? (t - 2f) * 0.04f * dt : 0f;
                    var d = Sample(_d0, bx, by, bz) * keepD + soot + fog * dt;
                    _d[i] = d; _t[i] = t;
                    _u[i] = Sample(_u0, bx, by, bz) * 0.995f; _v[i] = Sample(_v0, bx, by, bz) * 0.995f; _w[i] = Sample(_w0, bx, by, bz) * 0.995f;
                    total += d; if (t > maxT) maxT = t;
                }
        TotalDensity = total * _cell * _cell * _cell;
        MaxTemperature = maxT;
    }

    private float Sample(float[] f, float x, float y, float z)
    {
        var n = _n;
        x = Math.Clamp(x, 0f, n - 1.001f); y = Math.Clamp(y, 0f, n - 1.001f); z = Math.Clamp(z, 0f, n - 1.001f);
        int x0 = (int)x, y0 = (int)y, z0 = (int)z;
        float tx = x - x0, ty = y - y0, tz = z - z0;
        var i = Ix(x0, y0, z0);
        float c00 = f[i] + (f[i + 1] - f[i]) * tx, c10 = f[i + n] + (f[i + n + 1] - f[i + n]) * tx;
        var j = i + n * n;
        float c01 = f[j] + (f[j + 1] - f[j]) * tx, c11 = f[j + n] + (f[j + n + 1] - f[j + n]) * tx;
        var c0 = c00 + (c10 - c00) * ty; var c1 = c01 + (c11 - c01) * ty;
        return c0 + (c1 - c0) * tz;
    }

    /// <summary>월드 위치 밀도(경로 추적 매질 결선 · 박스 밖 = 0).</summary>
    public float SampleDensity(Vector3 world)
    {
        var g = (world - BoxMin) / _cell - new Vector3(0.5f);
        if (g.X < 0 || g.Y < 0 || g.Z < 0 || g.X > _n - 1 || g.Y > _n - 1 || g.Z > _n - 1) return 0f;
        return Sample(_d, g.X, g.Y, g.Z);
    }

    /// <summary>방향광 투과율: 셀마다 광원 쪽으로 1셀 간격 행진 · T = exp(−σ Σρ Δ) · T &lt; 0.005 조기 종료.</summary>
    private void ComputeLight()
    {
        var n = _n; var l = -Vector3.Normalize(LightDirection); var sig = Extinction * _cell;
        for (var z = 0; z < n; z++)
            for (var y = 0; y < n; y++)
                for (var x = 0; x < n; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f, pz = z + 0.5f, tau = 0f;
                    for (var k = 0; k < n * 2; k++)
                    {
                        px += l.X; py += l.Y; pz += l.Z;
                        int ix = (int)px, iy = (int)py, iz = (int)pz;
                        if ((uint)ix >= (uint)n || (uint)iy >= (uint)n || (uint)iz >= (uint)n || px < 0 || py < 0 || pz < 0) break;
                        tau += _d[Ix(ix, iy, iz)] * sig;
                        if (tau > 5.3f) break;
                    }
                    _light[Ix(x, y, z)] = MathF.Exp(-tau);
                }
    }

    /// <summary>흑체 근사 방출색(온도 단위 → RGB).</summary>
    public static Vector3 Blackbody(float t)
    {
        if (t <= 1.5f) return Vector3.Zero;
        var k = MathF.Min((t - 1.5f) / 8f, 1.5f);
        return new Vector3(MathF.Min(k * 3.2f, 6f), MathF.Min(k * k * 1.8f, 4f), MathF.Min(k * k * k * 0.9f, 2.5f));
    }

    /// <summary>정사영 볼륨 렌더(+Z → −Z 시점 · 전→후 합성).</summary>
    private void Render()
    {
        var n = _n; var sig = Extinction * _cell; var lc = LightColor * Albedo; var tint = SmokeTint;
        double tsum = 0;
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
            {
                float T = 1f, r = 0f, g = 0f, b = 0f;
                for (var z = n - 1; z >= 0; z--)
                {
                    var i = Ix(x, y, z);
                    var d = _d[i]; var t = _t[i];
                    if (d < 1e-5f && t < 1.5f) continue;
                    var a = 1f - MathF.Exp(-d * sig);
                    var sc = _light[i] * a;
                    var em = Blackbody(t) * (0.08f * _cell * 32f);
                    r += T * (sc * lc.X * tint.X + em.X); g += T * (sc * lc.Y * tint.Y + em.Y); b += T * (sc * lc.Z * tint.Z + em.Z);
                    T *= 1f - a;
                    if (T < 0.003f) { T = 0f; break; }
                }
                var o = (n - 1 - y) * n + x;
                _imgR[o] = r; _imgG[o] = g; _imgB[o] = b; _imgT[o] = T;
                tsum += T;
            }
        MeanTransmittance = (float)(tsum / (n * n));
    }
}
