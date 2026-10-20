using System.Numerics;
using System.Text;
using System.Text.Json;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.Twin;

public enum SimWeather : byte { Clear = 0, Overcast, Fog, Rain, Snow, Night }

public enum DatasetSplit : byte { Train = 0, Val, Test }

/// <summary>샘플 1장의 도메인 랜덤화 조건(조명 · 날씨 · 카메라 · 센서).</summary>
public struct SampleConditions
{
    public ulong Seed;
    public SimWeather Weather;
    public float SunElevationDeg, SunAzimuthDeg, SunIntensity, ColorTempK, Ambient, FogDensity, Precipitation;
    public float CamYawDeg, CamPitchDeg, CamDistance, FovDeg, Exposure, NoiseSigma, ObjectJitter;
}

/// <summary>랜덤화 범위. WeatherMask 비트 = <see cref="SimWeather"/>(0 = 전체).</summary>
public struct RandomizationRanges
{
    public byte WeatherMask;
    public float YawMin, YawMax, PitchMin, PitchMax, DistMin, DistMax, FovMin, FovMax, Jitter, NoiseMax;

    public static RandomizationRanges Default => new()
    {
        WeatherMask = 0, YawMin = 0, YawMax = 360, PitchMin = 12, PitchMax = 55, DistMin = 14, DistMax = 26, FovMin = 45, FovMax = 70, Jitter = 0.6f, NoiseMax = 0.03f,
    };
}

/// <summary>2D 바운딩 박스 라벨(가시 픽셀 기준 타이트 박스 · 아모달 대비 가림률 · 화면 경계 잘림 · 평균 깊이 m).</summary>
public readonly record struct BoxLabel(int Instance, byte Class, short X0, short Y0, short X1, short Y1, int Pixels, int AmodalPixels, float Occlusion, bool Truncated, float Depth);

public readonly record struct SyntheticSampleInfo(int Job, string JobName, int Index, DatasetSplit Split, SampleConditions Conditions, int Boxes);

/// <summary>데이터셋 출력 결선점(파일 · 원격 스토리지). 호출은 할당 가드 밖(제어 평면).</summary>
public interface ISyntheticDatasetSink
{
    void Write(in SyntheticSampleInfo info, ReadOnlySpan<byte> rgb, ReadOnlySpan<float> depth, ReadOnlySpan<byte> seg, ReadOnlySpan<BoxLabel> boxes);
}

/// <summary>Sim2Real 신경망 리파이너 결선점(합성 RGB → 실사 도메인). 무할당 구현 필요 · 라벨은 불변.</summary>
public interface ISim2RealRefiner
{
    void Refine(Span<byte> rgb, int width, int height, in SampleConditions c);
}

/// <summary>
/// [PromptFlow Studio v16.73.0] 🧪 신경망 합성 데이터 &amp; 멀티모달 데이터셋 제너레이터.
///   ▸ 도메인 랜덤화: 시드·인덱스 결정적 — 날씨 6종(맑음/흐림/안개/비/눈/야간) · 태양 고도/방위/세기/색온도 · 앰비언트 · 안개 밀도 · 강수 · 카메라 궤도(요/피치/거리/FOV) · 노출 · 센서 잡음 · 객체 위치/색 지터.
///   ▸ 렌더: 무할당 소프트웨어 레이캐스터(<see cref="W"/>×<see cref="H"/>) → RGB(감마) · Z 깊이(m) · 인스턴스 세그멘테이션(객체 i+1 · 바닥 255 · 하늘 0) — 그림자 · 안개 · 비/눈 오버레이 · 잡음은 RGB 에만(라벨 불변).
///   ▸ 라벨: 세그 마스크 타이트 박스 + 단일 객체 재레이캐스트 아모달 마스크 → 정확한 가림률 · 잘림 · 평균 깊이.
///   ▸ 큐: 렌더 작업(이름 · 시드 · 수량 · 범위) 순차 처리 · 80/10/10 해시 분할 · 라벨링 스펙트럼(클래스 · 날씨 · 가림률 · 분할) · 데이터셋 해시(결정성).
///   ▸ 결선점: <see cref="ISim2RealRefiner"/>(신경망 리파이너) · <see cref="ISyntheticDatasetSink"/>(<see cref="FolderDatasetSink"/> = BMP/PGM/JSONL).
/// </summary>
public sealed class NeuralSyntheticDataGeneratorEngine
{
    public const int W = 128, H = 96, MaxJobs = 16, MinPixels = 4, OcclusionBins = 5;
    public const byte FloorSeg = 255;
    private const float Deg = MathF.PI / 180f;

    private readonly object _gate = new();
    private readonly byte[] _rgb = new byte[W * H * 3], _seg = new byte[W * H];
    private readonly float[] _depth = new float[W * H];
    private readonly SyntheticScene _work = new();
    private readonly BoxLabel[] _labels = new BoxLabel[SyntheticScene.MaxObjects];
    private readonly int[] _vis = new int[SyntheticScene.MaxObjects], _x0 = new int[SyntheticScene.MaxObjects], _y0 = new int[SyntheticScene.MaxObjects],
        _x1 = new int[SyntheticScene.MaxObjects], _y1 = new int[SyntheticScene.MaxObjects];
    private readonly float[] _dsum = new float[SyntheticScene.MaxObjects];
    private readonly long[] _classHist = new long[SyntheticScene.ClassNames.Length], _weatherHist = new long[6], _occHist = new long[OcclusionBins], _splitHist = new long[3];
    private readonly string[] _jobName = new string[MaxJobs];
    private readonly ulong[] _jobSeed = new ulong[MaxJobs];
    private readonly int[] _jobCount = new int[MaxJobs], _jobDone = new int[MaxJobs];
    private readonly RandomizationRanges[] _jobRanges = new RandomizationRanges[MaxJobs];
    private int _jobs, _labelCount, _lastJob = -1, _lastIndex;
    private Vector3 _eye, _fwd, _right, _up;
    private float _tanH;
    private SampleConditions _last;
    private DatasetSplit _lastSplit;
    private long _samples, _boxes, _renderTicks;
    private ulong _hash = 14695981039346656037UL;

    public event Action? OnChange;

    public ISyntheticDatasetSink? Sink { get; set; }
    public ISim2RealRefiner? Refiner { get; set; }
    /// <summary>카메라 주시점(null = 가시 객체 중심).</summary>
    public Vector3? Target { get; set; }

    public ReadOnlySpan<byte> Rgb => _rgb;
    public ReadOnlySpan<float> Depth => _depth;
    public ReadOnlySpan<byte> Segmentation => _seg;
    public ReadOnlySpan<BoxLabel> Labels => _labels.AsSpan(0, _labelCount);
    public SampleConditions LastConditions => _last;
    public DatasetSplit LastSplit => _lastSplit;
    public int LastJob => _lastJob;
    public int LastIndex => _lastIndex;
    public long Samples => Interlocked.Read(ref _samples);
    public long BoxesLabeled => Interlocked.Read(ref _boxes);
    public double LastRenderMs => _renderTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    public ulong DatasetHash => _hash;
    public int JobCount => _jobs;
    public string? LastAction { get; private set; }

    public long ClassCount(int cls) => _classHist[cls];
    public long WeatherCount(SimWeather w) => _weatherHist[(int)w];
    public long OcclusionCount(int bin) => _occHist[bin];
    public long SplitCount(DatasetSplit s) => _splitHist[(int)s];
    public string JobName(int j) => _jobName[j];
    public int JobTotal(int j) => _jobCount[j];
    public int JobDone(int j) => _jobDone[j];
    public ulong JobSeed(int j) => _jobSeed[j];
    public bool HasPending { get { lock (_gate) { for (var j = 0; j < _jobs; j++) if (_jobDone[j] < _jobCount[j]) return true; return false; } } }

    // ══════════════════════ 결정적 랜덤화 ══════════════════════

    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private struct Rng
    {
        private ulong _s;
        public Rng(ulong s) => _s = s == 0 ? 0x9E3779B97F4A7C15UL : s;
        public float Next() { _s += 0x9E3779B97F4A7C15UL; return (Mix(_s) >> 40) * (1f / (1 << 24)); }
        public float Range(float a, float b) => a + (b - a) * Next();
    }

    /// <summary>시드 + 인덱스 → 샘플 조건(결정적 · 무할당).</summary>
    public static void Randomize(ulong seed, int index, in RandomizationRanges r, out SampleConditions c)
    {
        var s = Mix(seed ^ (ulong)(uint)index * 0x9E3779B97F4A7C15UL ^ 0x1673UL);
        var g = new Rng(s);
        c = default;
        c.Seed = s;
        var mask = r.WeatherMask == 0 ? 0x3F : r.WeatherMask & 0x3F;
        var bits = System.Numerics.BitOperations.PopCount((uint)mask);
        var pick = Math.Min(bits - 1, (int)(g.Next() * bits));
        var w = 0;
        for (var k = 0; k < 6; k++) if ((mask & (1 << k)) != 0 && pick-- == 0) { w = k; break; }
        c.Weather = (SimWeather)w;
        c.SunAzimuthDeg = g.Range(0, 360);
        switch (c.Weather)
        {
            case SimWeather.Clear: c.SunElevationDeg = g.Range(20, 70); c.SunIntensity = g.Range(0.9f, 1.2f); c.ColorTempK = g.Range(4500, 6500); c.Ambient = g.Range(0.15f, 0.25f); c.FogDensity = g.Range(0, 0.004f); break;
            case SimWeather.Overcast: c.SunElevationDeg = g.Range(30, 70); c.SunIntensity = g.Range(0.25f, 0.4f); c.ColorTempK = g.Range(6500, 7500); c.Ambient = g.Range(0.35f, 0.5f); c.FogDensity = g.Range(0.004f, 0.01f); break;
            case SimWeather.Fog: c.SunElevationDeg = g.Range(10, 50); c.SunIntensity = g.Range(0.3f, 0.5f); c.ColorTempK = g.Range(6500, 7000); c.Ambient = g.Range(0.35f, 0.45f); c.FogDensity = g.Range(0.05f, 0.12f); break;
            case SimWeather.Rain: c.SunElevationDeg = g.Range(30, 60); c.SunIntensity = g.Range(0.25f, 0.4f); c.ColorTempK = g.Range(6500, 8000); c.Ambient = g.Range(0.3f, 0.4f); c.FogDensity = g.Range(0.01f, 0.03f); c.Precipitation = g.Range(0.04f, 0.1f); break;
            case SimWeather.Snow: c.SunElevationDeg = g.Range(20, 50); c.SunIntensity = g.Range(0.4f, 0.6f); c.ColorTempK = g.Range(7000, 9000); c.Ambient = g.Range(0.45f, 0.6f); c.FogDensity = g.Range(0.01f, 0.03f); c.Precipitation = g.Range(0.03f, 0.08f); break;
            default: c.SunElevationDeg = g.Range(5, 20); c.SunIntensity = g.Range(0.05f, 0.1f); c.ColorTempK = g.Range(2500, 3500); c.Ambient = g.Range(0.03f, 0.06f); c.FogDensity = g.Range(0, 0.01f); break;
        }
        c.CamYawDeg = g.Range(r.YawMin, r.YawMax);
        c.CamPitchDeg = g.Range(r.PitchMin, r.PitchMax);
        c.CamDistance = g.Range(r.DistMin, r.DistMax);
        c.FovDeg = g.Range(r.FovMin, r.FovMax);
        c.Exposure = g.Range(0.85f, 1.15f);
        c.NoiseSigma = g.Range(0, r.NoiseMax);
        c.ObjectJitter = r.Jitter;
    }

    /// <summary>해시 분할 80/10/10(시드·인덱스 결정적).</summary>
    public static DatasetSplit SplitOf(ulong seed, int index)
    {
        var h = Mix(seed * 0xD1B54A32D192ED03UL ^ (ulong)(uint)index) % 100;
        return h < 80 ? DatasetSplit.Train : h < 90 ? DatasetSplit.Val : DatasetSplit.Test;
    }

    public static Vector3 KelvinToRgb(float k)
    {
        if (k < 6500) return Vector3.Lerp(new Vector3(1f, 0.56f, 0.25f), Vector3.One, Math.Clamp((k - 2500) / 4000f, 0f, 1f));
        return Vector3.Lerp(Vector3.One, new Vector3(0.75f, 0.85f, 1f), Math.Clamp((k - 6500) / 3500f, 0f, 1f));
    }

    public static Vector3 SkyColor(SimWeather w) => w switch
    {
        SimWeather.Clear => new(0.55f, 0.72f, 0.95f),
        SimWeather.Overcast => new(0.62f, 0.64f, 0.68f),
        SimWeather.Fog => new(0.70f, 0.72f, 0.74f),
        SimWeather.Rain => new(0.45f, 0.48f, 0.52f),
        SimWeather.Snow => new(0.80f, 0.82f, 0.86f),
        _ => new(0.02f, 0.03f, 0.07f),
    };

    // ══════════════════════ 렌더 · 라벨(무할당) ══════════════════════

    /// <summary>단일 샘플 생성(통계·큐 미반영 · 미리보기/검증용). 반환 = 라벨 수.</summary>
    public int Generate(SyntheticScene source, ulong seed, int index, in RandomizationRanges ranges)
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Synth.Generate");
            Randomize(seed, index, ranges, out var c);
            return RenderLocked(source, c);
        }
    }

    /// <summary>지정 조건으로 렌더(객체 지터 포함). 반환 = 라벨 수.</summary>
    public int Render(SyntheticScene source, in SampleConditions c)
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Synth.Render");
            return RenderLocked(source, c);
        }
    }

    private int RenderLocked(SyntheticScene source, in SampleConditions c)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        PrepareScene(source, c);
        Shade(_work, c);
        Refiner?.Refine(_rgb, W, H, c);
        _labelCount = ExtractLabels(_work);
        _last = c;
        _renderTicks = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        return _labelCount;
    }

    private void PrepareScene(SyntheticScene source, in SampleConditions c)
    {
        _work.CopyFrom(source);
        if (c.ObjectJitter <= 0) return;
        var g = new Rng(c.Seed ^ 0xA5A5A5A5UL);
        for (var i = 0; i < _work.Count; i++)
        {
            _work.Px[i] += (g.Next() - 0.5f) * 2 * c.ObjectJitter;
            _work.Pz[i] += (g.Next() - 0.5f) * 2 * c.ObjectJitter;
            var k = g.Range(0.8f, 1.2f);
            _work.R[i] = MathF.Min(1, _work.R[i] * k); _work.G[i] = MathF.Min(1, _work.G[i] * k); _work.B[i] = MathF.Min(1, _work.B[i] * k);
        }
    }

    private Vector3 PixelRay(int x, int y)
    {
        var aspect = (float)W / H;
        var nx = ((x + 0.5f) / W * 2 - 1) * _tanH * aspect;
        var ny = (1 - (y + 0.5f) / H * 2) * _tanH;
        return Vector3.Normalize(_fwd + _right * nx + _up * ny);
    }

    private void SetupCamera(SyntheticScene s, in SampleConditions c)
    {
        Vector3 target;
        if (Target is { } tg) target = tg;
        else
        {
            Vector3 sum = default; var n = 0;
            for (var i = 0; i < s.Count; i++) if (!s.Hidden[i]) { sum += s.Position(i); n++; }
            target = n > 0 ? sum / n : Vector3.Zero;
        }
        float yaw = c.CamYawDeg * Deg, pitch = c.CamPitchDeg * Deg;
        _eye = target + c.CamDistance * new Vector3(MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch), MathF.Cos(pitch) * MathF.Cos(yaw));
        _fwd = Vector3.Normalize(target - _eye);
        _right = Vector3.Normalize(Vector3.Cross(_fwd, Vector3.UnitY));
        _up = Vector3.Cross(_right, _fwd);
        _tanH = MathF.Tan(Math.Clamp(c.FovDeg, 10f, 120f) * Deg * 0.5f);
    }

    private void Shade(SyntheticScene s, in SampleConditions c)
    {
        SetupCamera(s, c);
        float el = c.SunElevationDeg * Deg, az = c.SunAzimuthDeg * Deg;
        var sun = Vector3.Normalize(new Vector3(MathF.Cos(el) * MathF.Cos(az), MathF.Sin(el), MathF.Cos(el) * MathF.Sin(az)));
        var sunCol = KelvinToRgb(c.ColorTempK) * c.SunIntensity;
        var sky = SkyColor(c.Weather);
        var skyLit = c.Weather == SimWeather.Night ? sky : sky * MathF.Max(0.4f, c.SunIntensity + c.Ambient);
        var noiseSeed = c.Seed ^ 0x5EEDUL;
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var d = PixelRay(x, y);
                var p = y * W + x;
                Vector3 col;
                if (s.Raycast(_eye, d, 1000f, out var t, out var hit, out var n))
                {
                    var hp = _eye + d * t;
                    Vector3 baseCol;
                    if (hit >= 0) baseCol = s.Color(hit);
                    else { var chk = (((int)MathF.Floor(hp.X) + (int)MathF.Floor(hp.Z)) & 1) == 0 ? 0.55f : 0.45f; baseCol = new Vector3(chk); }
                    var ndl = MathF.Max(0, Vector3.Dot(n, sun));
                    if (ndl > 0 && s.Occluded(hp + n * 1e-3f, sun, 1000f)) ndl = 0;
                    col = baseCol * (sunCol * ndl + sky * c.Ambient);
                    var fog = 1f - MathF.Exp(-c.FogDensity * t);
                    col = Vector3.Lerp(col, skyLit, fog);
                    _depth[p] = t * Vector3.Dot(d, _fwd);
                    _seg[p] = hit >= 0 ? (byte)(hit + 1) : FloorSeg;
                }
                else
                {
                    col = skyLit * (0.85f + 0.15f * MathF.Max(0, d.Y));
                    _depth[p] = 0;
                    _seg[p] = 0;
                }
                if (c.Precipitation > 0)
                {
                    if (c.Weather == SimWeather.Rain && Hash01(noiseSeed, x, y / 5) < c.Precipitation) col = Vector3.Lerp(col, new Vector3(0.75f, 0.78f, 0.85f), 0.5f);
                    else if (c.Weather == SimWeather.Snow && Hash01(noiseSeed ^ 0x51, x, y) < c.Precipitation * 0.5f) col = new Vector3(0.95f);
                }
                if (c.NoiseSigma > 0) col += new Vector3((Hash01(noiseSeed ^ 0x77, x, y) - 0.5f) * 2 * c.NoiseSigma);
                col *= c.Exposure;
                var o = p * 3;
                _rgb[o] = (byte)(MathF.Sqrt(Math.Clamp(col.X, 0f, 1f)) * 255f + 0.5f);
                _rgb[o + 1] = (byte)(MathF.Sqrt(Math.Clamp(col.Y, 0f, 1f)) * 255f + 0.5f);
                _rgb[o + 2] = (byte)(MathF.Sqrt(Math.Clamp(col.Z, 0f, 1f)) * 255f + 0.5f);
            }
    }

    private static float Hash01(ulong seed, int x, int y) =>
        (Mix(seed ^ (ulong)(uint)x * 0x9E3779B97F4A7C15UL ^ (ulong)(uint)y * 0xC2B2AE3D27D4EB4FUL) >> 40) * (1f / (1 << 24));

    private bool Project(Vector3 p, out float sx, out float sy)
    {
        var rel = p - _eye;
        var z = Vector3.Dot(rel, _fwd);
        sx = sy = 0;
        if (z <= 1e-3f) return false;
        var aspect = (float)W / H;
        sx = (Vector3.Dot(rel, _right) / (z * _tanH * aspect) + 1) * 0.5f * W;
        sy = (1 - Vector3.Dot(rel, _up) / (z * _tanH)) * 0.5f * H;
        return true;
    }

    private int ExtractLabels(SyntheticScene s)
    {
        var n = s.Count;
        for (var i = 0; i < n; i++) { _vis[i] = 0; _x0[i] = int.MaxValue; _y0[i] = int.MaxValue; _x1[i] = -1; _y1[i] = -1; _dsum[i] = 0; }
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var id = _seg[y * W + x];
                if (id == 0 || id == FloorSeg) continue;
                var i = id - 1;
                _vis[i]++; _dsum[i] += _depth[y * W + x];
                if (x < _x0[i]) _x0[i] = x;
                if (x > _x1[i]) _x1[i] = x;
                if (y < _y0[i]) _y0[i] = y;
                if (y > _y1[i]) _y1[i] = y;
            }
        var count = 0;
        for (var i = 0; i < n; i++)
        {
            if (s.Hidden[i] || _vis[i] < MinPixels) continue;
            // 아모달 범위: AABB 8모서리 투영(카메라 뒤 모서리가 있으면 전체 화면)
            var c = s.Position(i); var h = s.Half(i);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            var full = false;
            for (var k = 0; k < 8 && !full; k++)
            {
                var corner = c + new Vector3((k & 1) != 0 ? h.X : -h.X, (k & 2) != 0 ? h.Y : -h.Y, (k & 4) != 0 ? h.Z : -h.Z);
                if (!Project(corner, out var sx, out var sy)) { full = true; break; }
                minX = MathF.Min(minX, sx); maxX = MathF.Max(maxX, sx); minY = MathF.Min(minY, sy); maxY = MathF.Max(maxY, sy);
            }
            int rx0 = full ? 0 : Math.Clamp((int)MathF.Floor(minX) - 1, 0, W - 1), rx1 = full ? W - 1 : Math.Clamp((int)MathF.Ceiling(maxX) + 1, 0, W - 1);
            int ry0 = full ? 0 : Math.Clamp((int)MathF.Floor(minY) - 1, 0, H - 1), ry1 = full ? H - 1 : Math.Clamp((int)MathF.Ceiling(maxY) + 1, 0, H - 1);
            var amodal = 0;
            var truncated = false;
            for (var y = ry0; y <= ry1; y++)
                for (var x = rx0; x <= rx1; x++)
                {
                    if (!s.IntersectOne(i, _eye, PixelRay(x, y), 1000f, out _)) continue;
                    amodal++;
                    if (x == 0 || y == 0 || x == W - 1 || y == H - 1) truncated = true;
                }
            amodal = Math.Max(amodal, _vis[i]);
            var occ = 1f - (float)_vis[i] / amodal;
            _labels[count++] = new BoxLabel(i + 1, s.Class[i], (short)_x0[i], (short)_y0[i], (short)_x1[i], (short)_y1[i], _vis[i], amodal, occ, truncated, _dsum[i] / _vis[i]);
        }
        return count;
    }

    // ══════════════════════ 렌더 큐 ══════════════════════

    /// <summary>렌더 작업 등록(완료된 작업 슬롯 재사용). 반환 = 작업 ID(−1 = 큐 가득).</summary>
    public int Enqueue(string name, int count, ulong seed, in RandomizationRanges ranges)
    {
        int j;
        lock (_gate)
        {
            j = -1;
            for (var k = 0; k < _jobs; k++) if (_jobDone[k] >= _jobCount[k]) { j = k; break; }
            if (j < 0)
            {
                if (_jobs >= MaxJobs) return -1;
                j = _jobs++;
            }
            _jobName[j] = string.IsNullOrWhiteSpace(name) ? "job" + j.ToString(System.Globalization.CultureInfo.InvariantCulture) : name;
            _jobSeed[j] = seed; _jobCount[j] = Math.Max(1, count); _jobDone[j] = 0; _jobRanges[j] = ranges;
            LastAction = "📥 작업 등록 · " + _jobName[j] + " × " + _jobCount[j].ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        Raise();
        return j;
    }

    /// <summary>큐에서 최대 maxSamples 장 생성 → 통계·해시 갱신 → 싱크 기록(가드 밖). 반환 = 생성 수.</summary>
    public int ProcessNext(SyntheticScene source, int maxSamples = 1)
    {
        var made = 0;
        lock (_gate)
        {
            for (var k = 0; k < maxSamples; k++)
            {
                var j = -1;
                for (var q = 0; q < _jobs; q++) if (_jobDone[q] < _jobCount[q]) { j = q; break; }
                if (j < 0) break;
                var idx = _jobDone[j];
                int labels;
                SampleConditions c;
                using (ZeroGcRegionScope.Enter("Synth.Process"))
                {
                    Randomize(_jobSeed[j], idx, _jobRanges[j], out c);
                    labels = RenderLocked(source, c);
                    var split = SplitOf(_jobSeed[j], idx);
                    _lastSplit = split; _lastJob = j; _lastIndex = idx;
                    _splitHist[(int)split]++;
                    _weatherHist[(int)c.Weather]++;
                    for (var b = 0; b < labels; b++)
                    {
                        ref readonly var l = ref _labels[b];
                        _classHist[l.Class < _classHist.Length ? l.Class : 0]++;
                        _occHist[Math.Min(OcclusionBins - 1, (int)(l.Occlusion * OcclusionBins))]++;
                        _hash = (_hash ^ (uint)l.Instance ^ (ulong)l.Class << 8 ^ (ulong)(ushort)l.X0 << 16) * 1099511628211UL;
                        _hash = (_hash ^ (ulong)(ushort)l.Y0 ^ (ulong)(ushort)l.X1 << 16 ^ (ulong)(ushort)l.Y1 << 32 ^ (ulong)(uint)l.Pixels << 40) * 1099511628211UL;
                    }
                    _hash = (_hash ^ (ulong)c.Weather ^ (ulong)labels << 8) * 1099511628211UL;
                    _jobDone[j]++;
                    _samples++; _boxes += labels;
                }
                if (Sink is { } sink)
                {
                    try { sink.Write(new SyntheticSampleInfo(j, _jobName[j], idx, _lastSplit, c, labels), _rgb, _depth, _seg, _labels.AsSpan(0, labels)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { PromptFlowStudio.Program.Log("Synth.Sink", ex); }
                }
                made++;
            }
        }
        return made;
    }

    public void ResetStatistics()
    {
        lock (_gate)
        {
            Array.Clear(_classHist); Array.Clear(_weatherHist); Array.Clear(_occHist); Array.Clear(_splitHist);
            _samples = 0; _boxes = 0; _hash = 14695981039346656037UL;
        }
        Raise();
    }

    // ══════════════════════ 미리보기(제어 평면) ══════════════════════

    /// <summary>채널 → 32bpp 하향식 BMP(0 = RGB · 1 = 깊이(근=밝음) · 2 = 세그 팔레트).</summary>
    public byte[] SnapshotBmp(int channel)
    {
        const int N = W * H * 4;
        var bmp = new byte[54 + N];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 54);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), W);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), -H);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28), 32);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(34), N);
        lock (_gate)
        {
            var maxD = 1f;
            if (channel == 1) foreach (var d in _depth) if (d > maxD) maxD = d;
            for (var p = 0; p < W * H; p++)
            {
                var o = 54 + p * 4;
                byte r, g, b;
                if (channel == 1)
                {
                    var v = _depth[p] <= 0 ? (byte)0 : (byte)(255 * (1 - _depth[p] / maxD * 0.85f));
                    r = g = b = v;
                }
                else if (channel == 2)
                {
                    var id = _seg[p];
                    if (id == 0) { r = 10; g = 12; b = 24; }
                    else if (id == FloorSeg) { r = 50; g = 55; b = 60; }
                    else { var h = Mix((ulong)id * 7919); r = (byte)(80 + (h & 0x7F)); g = (byte)(80 + (h >> 8 & 0x7F)); b = (byte)(80 + (h >> 16 & 0x7F)); }
                }
                else { r = _rgb[p * 3]; g = _rgb[p * 3 + 1]; b = _rgb[p * 3 + 2]; }
                bmp[o] = b; bmp[o + 1] = g; bmp[o + 2] = r; bmp[o + 3] = 255;
            }
        }
        return bmp;
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Synth.OnChange", ex); }
    }
}

/// <summary>
/// 폴더 데이터셋 싱크: images/*.bmp(24bpp) · depth/*.pgm(16bit mm 빅엔디언) · seg/*.pgm(8bit 인스턴스) · labels.jsonl(샘플당 1줄 · System.Text.Json).
/// </summary>
public sealed class FolderDatasetSink : ISyntheticDatasetSink, IDisposable
{
    private readonly StreamWriter _jsonl;
    private readonly string _root;

    public FolderDatasetSink(string root)
    {
        _root = root;
        Directory.CreateDirectory(Path.Combine(root, "images"));
        Directory.CreateDirectory(Path.Combine(root, "depth"));
        Directory.CreateDirectory(Path.Combine(root, "seg"));
        _jsonl = new StreamWriter(Path.Combine(root, "labels.jsonl"), append: true, new UTF8Encoding(false));
    }

    public string Root => _root;
    public int Written { get; private set; }

    public void Write(in SyntheticSampleInfo info, ReadOnlySpan<byte> rgb, ReadOnlySpan<float> depth, ReadOnlySpan<byte> seg, ReadOnlySpan<BoxLabel> boxes)
    {
        const int W = NeuralSyntheticDataGeneratorEngine.W, H = NeuralSyntheticDataGeneratorEngine.H;
        var stem = info.Job.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + "_" + info.Index.ToString("000000", System.Globalization.CultureInfo.InvariantCulture);
        var bmp = new byte[54 + W * H * 3];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 54);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), W);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), -H);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28), 24);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(34), W * H * 3);
        for (var p = 0; p < W * H; p++) { bmp[54 + p * 3] = rgb[p * 3 + 2]; bmp[55 + p * 3] = rgb[p * 3 + 1]; bmp[56 + p * 3] = rgb[p * 3]; }
        File.WriteAllBytes(Path.Combine(_root, "images", stem + ".bmp"), bmp);

        var header = Encoding.ASCII.GetBytes("P5\n" + W.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + H.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n65535\n");
        var pgm = new byte[header.Length + W * H * 2];
        header.CopyTo(pgm, 0);
        for (var p = 0; p < W * H; p++)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(pgm.AsSpan(header.Length + p * 2), (ushort)Math.Clamp(depth[p] * 1000f, 0f, 65535f));
        File.WriteAllBytes(Path.Combine(_root, "depth", stem + ".pgm"), pgm);

        var sh = Encoding.ASCII.GetBytes("P5\n" + W.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + H.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n255\n");
        var sp = new byte[sh.Length + W * H];
        sh.CopyTo(sp, 0);
        seg.CopyTo(sp.AsSpan(sh.Length));
        File.WriteAllBytes(Path.Combine(_root, "seg", stem + ".pgm"), sp);

        using var ms = new MemoryStream();
        using (var j = new Utf8JsonWriter(ms))
        {
            var c = info.Conditions;
            j.WriteStartObject();
            j.WriteString("image", "images/" + stem + ".bmp");
            j.WriteString("depth", "depth/" + stem + ".pgm");
            j.WriteString("seg", "seg/" + stem + ".pgm");
            j.WriteString("job", info.JobName);
            j.WriteNumber("index", info.Index);
            j.WriteString("split", info.Split.ToString().ToLowerInvariant());
            j.WriteString("weather", c.Weather.ToString().ToLowerInvariant());
            j.WriteNumber("sun_elevation", c.SunElevationDeg);
            j.WriteNumber("sun_intensity", c.SunIntensity);
            j.WriteNumber("color_temp_k", c.ColorTempK);
            j.WriteNumber("fog", c.FogDensity);
            j.WriteNumber("cam_yaw", c.CamYawDeg);
            j.WriteNumber("cam_pitch", c.CamPitchDeg);
            j.WriteNumber("cam_distance", c.CamDistance);
            j.WriteNumber("fov", c.FovDeg);
            j.WriteStartArray("boxes");
            foreach (var b in boxes)
            {
                j.WriteStartObject();
                j.WriteNumber("instance", b.Instance);
                j.WriteString("class", b.Class < SyntheticScene.ClassNames.Length ? SyntheticScene.ClassNames[b.Class] : "unknown");
                j.WriteStartArray("bbox"); j.WriteNumberValue(b.X0); j.WriteNumberValue(b.Y0); j.WriteNumberValue(b.X1); j.WriteNumberValue(b.Y1); j.WriteEndArray();
                j.WriteNumber("pixels", b.Pixels);
                j.WriteNumber("occlusion", MathF.Round(b.Occlusion, 4));
                j.WriteBoolean("truncated", b.Truncated);
                j.WriteNumber("depth_m", MathF.Round(b.Depth, 3));
                j.WriteEndObject();
            }
            j.WriteEndArray();
            j.WriteEndObject();
        }
        _jsonl.WriteLine(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
        _jsonl.Flush();
        Written++;
    }

    public void Dispose() => _jsonl.Dispose();
}
