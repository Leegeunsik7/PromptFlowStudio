using System.Numerics;
using PromptFlowStudio.Services.GenerativeVfx;
using PromptFlowStudio.Services.HairCloth;
using PromptFlowStudio.Services.OpenUsd;

namespace PromptFlowStudio.Services.FilmScore;

public enum FoleyKind : byte { Footstep, Impact, Explosion, Splash, Whoosh }
public enum SurfaceKind : byte { Grass, Gravel, Sand, Snow, Stone, Wood, Water, Mud }

/// <summary>폴리 이벤트(값 형식): 장면 시각 · 종류 · 재질 · 에너지 0..1 · 월드 위치.</summary>
public readonly record struct FoleyEvent(double Time, FoleyKind Kind, SurfaceKind Surface, float Energy, Vector3 Position);

/// <summary>발 접지 검출(높이 임계 + 하강 → 정지 전이 = 힐 스트라이크 · 히스테리시스).</summary>
public struct FootContactDetector
{
    public float Threshold, Release;
    private float _prevY;
    private bool _down, _init;

    public FootContactDetector() : this(0.04f, 0.07f) { }

    public FootContactDetector(float threshold, float release) { Threshold = threshold; Release = release; _prevY = 0f; _down = false; _init = false; }

    /// <summary>발 높이(지면 기준 m) → 접지 순간이면 true · 충격 속도(m/s) 출력.</summary>
    public bool Update(float heightAboveGround, float dt, out float impactSpeed)
    {
        impactSpeed = 0f;
        if (!_init) { _prevY = heightAboveGround; _init = true; _down = heightAboveGround < Threshold; return false; }
        var vy = (heightAboveGround - _prevY) / MathF.Max(dt, 1e-5f);
        _prevY = heightAboveGround;
        if (!_down && heightAboveGround < Threshold) { _down = true; impactSpeed = MathF.Max(-vy, 0f); return true; }
        if (_down && heightAboveGround > Release) _down = false;
        return false;
    }
}

/// <summary>
/// [PromptFlow Studio v16.86.0] 👣 자율 폴리 &amp; 시공간 효과음 합성기(48 kHz · 블록 800 · 무할당 · 샘플 정확 동기화).
///   ▸ 발걸음: 힐 쿵(감쇠 사인 + 하향 글라이드) + 접촉 버스트(SVF 대역 통과 노이즈) + 재질 그레인(자갈/눈/모래 크런치) + 물 버블(Minnaert f = 3.26/r) — v16.72 바이옴 → 재질 · v16.84 습윤 → 물 혼합.
///   ▸ 의상 마찰: 연속 보이스 — 대역 노이즈(원단별 중심/Q · v16.83 FabricKind) × 사지 속도^1.5 · 가죽 크랙클.
///   ▸ VFX(v16.81 충돌 링): 지면 = 파편, 바디 = 펀치, 의상 = 휙, 유체 = 버블 스플래시 · 폭발: 하향 스윕 붐 + 저역 럼블 + 크랙클 그레인.
///   ▸ 시공간: 청취자 거리 → 전파 지연(343 m/s) · 공기 흡수 LPF · 1/r 감쇠 · 블록 내 시작 샘플 = (이벤트 시각 − 블록 시작) · fs → 동기 오차 계측.
/// </summary>
public sealed class AutonomousNeuralFoleySfxSynthesizer
{
    public const int SampleRate = NeuralCinematographicFilmScoreGenerator.SampleRate, Block = NeuralCinematographicFilmScoreGenerator.Block;
    public const int MaxVoices = 24, MaxPending = 64, LogSize = 32, MaxGrains = 12;
    public const float SpeedOfSound = 343f;

    private readonly struct SurfaceSpec
    {
        public readonly float Center, Q, Decay, Thud, Grains, Wet;
        public SurfaceSpec(float center, float q, float decay, float thud, float grains, float wet) { Center = center; Q = q; Decay = decay; Thud = thud; Grains = grains; Wet = wet; }
    }

    private static readonly SurfaceSpec[] Surfaces =
    {
        new(1800f, 0.9f, 0.06f, 0.5f, 3f, 0f),   // 풀
        new(3200f, 1.2f, 0.09f, 0.35f, 10f, 0f), // 자갈
        new(2400f, 0.8f, 0.07f, 0.3f, 7f, 0f),   // 모래
        new(4200f, 1.6f, 0.12f, 0.25f, 12f, 0f), // 눈
        new(900f, 2.5f, 0.03f, 1f, 0f, 0f),      // 돌
        new(600f, 4f, 0.08f, 0.9f, 0f, 0f),      // 나무
        new(1500f, 0.7f, 0.1f, 0.3f, 0f, 1f),    // 물
        new(700f, 0.6f, 0.11f, 0.6f, 2f, 0.5f),  // 진흙
    };

    private readonly object _gate = new();
    // ── 보이스 SoA ──
    private readonly byte[] _kind = new byte[MaxVoices], _surf = new byte[MaxVoices];
    private readonly bool[] _active = new bool[MaxVoices], _live = new bool[MaxVoices];
    private readonly float[] _energy = new float[MaxVoices], _age = new float[MaxVoices], _gain = new float[MaxVoices], _air = new float[MaxVoices], _lp = new float[MaxVoices];
    private readonly float[] _ic1 = new float[MaxVoices], _ic2 = new float[MaxVoices], _ph = new float[MaxVoices], _ph2 = new float[MaxVoices];
    private readonly int[] _delay = new int[MaxVoices];
    private readonly uint[] _rng = new uint[MaxVoices];
    private readonly float[] _grainT = new float[MaxVoices * MaxGrains], _grainF = new float[MaxVoices * MaxGrains];
    private readonly Vector3[] _pos = new Vector3[MaxVoices];
    public readonly float[][] VoiceBuffer = new float[MaxVoices][];
    // ── 의상 마찰(연속) ──
    private readonly float[] _rustle = new float[Block];
    private float _rustleAmp, _rIc1, _rIc2, _crackle;
    private uint _rRng = 0xC0FFEEu;
    // ── 대기열 · 로그 ──
    private readonly FoleyEvent[] _pending = new FoleyEvent[MaxPending];
    private int _nPending;
    private readonly FoleyEvent[] _log = new FoleyEvent[LogSize];
    private readonly float[] _logErr = new float[LogSize];
    private readonly int[] _logStart = new int[LogSize];
    private int _logHead, _logCount;
    private double _blockTime;
    private float _lastVfxTime = -1f;
    private float _syncErrSum, _syncErrMax;
    private long _syncN;

    public AutonomousNeuralFoleySfxSynthesizer()
    {
        for (var v = 0; v < MaxVoices; v++) { VoiceBuffer[v] = new float[Block]; _rng[v] = 0x2468ACE1u + (uint)v * 7919u; }
    }

    public Vector3 Listener { get; set; }
    public bool PropagationDelay { get; set; } = true;
    public FabricKind Fabric { get; set; } = FabricKind.Cotton;
    public float RustleSpeed { get; private set; }
    public Vector3 RustlePosition { get; set; }
    public ReadOnlySpan<float> RustleBuffer => _rustle;
    public float RustleLevel => _rustleAmp;
    public float Wetness { get; set; }
    public double BlockTime => _blockTime;
    public long Triggered { get; private set; }
    public long Dropped { get; private set; }
    public long LateEvents { get; private set; }
    public int ActiveVoices { get; private set; }
    public int Pending => _nPending;
    public float MeanSyncErrorMs => _syncN > 0 ? _syncErrSum / _syncN : 0f;
    public float MaxSyncErrorMs => _syncErrMax;
    public double LastRenderMs { get; private set; }
    public int LogCount => _logCount;
    public FoleyEvent LogEvent(int back) => _log[(_logHead - 1 - back + 2 * LogSize) % LogSize];
    public float LogSyncError(int back) => _logErr[(_logHead - 1 - back + 2 * LogSize) % LogSize];
    public int LogStartSample(int back) => _logStart[(_logHead - 1 - back + 2 * LogSize) % LogSize];
    public bool IsActive(int v) => _active[v];
    /// <summary>이번 블록에 렌더된 보이스(마지막 블록 포함 · 믹서 소비용).</summary>
    public bool RenderedThisBlock(int v) => _live[v];
    public Vector3 VoicePosition(int v) => _pos[v];
    public FoleyKind VoiceKind(int v) => (FoleyKind)_kind[v];

    public static string KindName(FoleyKind k) => k switch { FoleyKind.Impact => "충돌", FoleyKind.Explosion => "폭발", FoleyKind.Splash => "스플래시", FoleyKind.Whoosh => "휙", _ => "발걸음" };
    public static string SurfaceName(SurfaceKind s) => s switch
    {
        SurfaceKind.Gravel => "자갈", SurfaceKind.Sand => "모래", SurfaceKind.Snow => "눈", SurfaceKind.Stone => "돌", SurfaceKind.Wood => "나무", SurfaceKind.Water => "물", SurfaceKind.Mud => "진흙", _ => "풀",
    };

    /// <summary>v16.72 바이옴 → 발걸음 재질.</summary>
    public static SurfaceKind FromBiome(Biome b) => b switch
    {
        Biome.Ocean => SurfaceKind.Water, Biome.Beach or Biome.Desert => SurfaceKind.Sand, Biome.Snow => SurfaceKind.Snow, Biome.Mountain => SurfaceKind.Stone,
        Biome.Taiga or Biome.Volcanic => SurfaceKind.Gravel, _ => SurfaceKind.Grass,
    };

    /// <summary>Minnaert 공명(Hz): f ≈ 3.26 / r(m).</summary>
    public static float MinnaertHz(float radiusM) => 3.26f / MathF.Max(radiusM, 1e-4f);

    /// <summary>이벤트 예약(장면 시각 기준 · 블록 내 샘플 정확 시작).</summary>
    public bool Trigger(in FoleyEvent e)
    {
        lock (_gate)
        {
            if (_nPending >= MaxPending) { Dropped++; return false; }
            _pending[_nPending++] = e;
            return true;
        }
    }

    /// <summary>v16.81 VFX 충돌 링 → 새 충돌 이벤트 수집(장면 시각 = 블록 시각 기준 정렬).</summary>
    public int IngestVfx(InteractiveVfxCollisionSplashGuard vfx, double sceneTimeOffset = 0)
    {
        var n = 0; var newest = _lastVfxTime;
        for (var k = 0; k < vfx.ImpactCount; k++)
        {
            var im = vfx.Impact(k);
            if (im.Time <= _lastVfxTime) break;
            newest = MathF.Max(newest, im.Time);
            var kind = im.Kind switch { ImpactKind.Fluid => FoleyKind.Splash, ImpactKind.Cloth => FoleyKind.Whoosh, _ => FoleyKind.Impact };
            if (Trigger(new FoleyEvent(im.Time + sceneTimeOffset, kind, SurfaceKind.Stone, Math.Clamp(im.Energy, 0f, 1f), new Vector3(im.X, im.Y, im.Z)))) n++;
        }
        _lastVfxTime = newest;
        return n;
    }

    /// <summary>의상 마찰 구동(사지 속도 m/s · 원단 = v16.83 FabricKind).</summary>
    public void SetRustle(float limbSpeed, FabricKind fabric) { RustleSpeed = MathF.Max(0f, limbSpeed); Fabric = fabric; }

    private static (float Center, float Q, float Gain, float Crackle) FabricSpec(FabricKind f) => f switch
    {
        FabricKind.Silk => (5200f, 1.2f, 0.6f, 0f), FabricKind.Denim => (1800f, 0.8f, 1.1f, 0f), FabricKind.Leather => (1200f, 1.5f, 0.9f, 0.6f),
        FabricKind.Wool => (2500f, 0.7f, 0.7f, 0f), FabricKind.Spandex => (3800f, 1f, 0.5f, 0f), _ => (3000f, 0.9f, 0.8f, 0f),
    };

    private int Alloc()
    {
        for (var v = 0; v < MaxVoices; v++) if (!_active[v]) return v;
        int best = 0; var oldest = -1f;
        for (var v = 0; v < MaxVoices; v++) if (_age[v] > oldest) { oldest = _age[v]; best = v; }
        Dropped++;
        return best;
    }

    private void Start(in FoleyEvent e, int startSample, float intendedSample)
    {
        var v = Alloc();
        _active[v] = true; _kind[v] = (byte)e.Kind; _surf[v] = (byte)e.Surface; _energy[v] = Math.Clamp(e.Energy, 0f, 1f); _age[v] = 0f; _pos[v] = e.Position;
        _delay[v] = startSample; _ic1[v] = _ic2[v] = 0f; _ph[v] = 0f; _ph2[v] = 0f;
        var d = Vector3.Distance(e.Position, Listener);
        _gain[v] = 1f / MathF.Max(1f, d);
        // 공기 흡수: 거리 → 1차 LPF 계수(근거리 ≈ 1 · 1 km ≈ 0.1)
        _air[v] = MathF.Exp(-d / 450f) * 0.9f + 0.1f; _lp[v] = 0f;
        var r = _rng[v];
        for (var g = 0; g < MaxGrains; g++)
        {
            r ^= r << 13; r ^= r >> 17; r ^= r << 5;
            _grainT[v * MaxGrains + g] = (r & 0xFFFF) / 65535f;
            r ^= r << 13; r ^= r >> 17; r ^= r << 5;
            _grainF[v * MaxGrains + g] = (r & 0xFFFF) / 65535f;
        }
        _rng[v] = r;
        Triggered++;
        var err = MathF.Abs(startSample - intendedSample) * 1000f / SampleRate;
        _syncErrSum += err; _syncN++; _syncErrMax = MathF.Max(_syncErrMax, err);
        _log[_logHead] = e; _logErr[_logHead] = err; _logStart[_logHead] = startSample;
        _logHead = (_logHead + 1) % LogSize; if (_logCount < LogSize) _logCount++;
    }

    /// <summary>1블록(무할당): 대기열 → 시작 샘플 산출 · 보이스 합성 · 의상 마찰.</summary>
    public void RenderBlock(double blockStartTime)
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _blockTime = blockStartTime;
            var blockEnd = blockStartTime + (double)Block / SampleRate;
            for (var k = 0; k < _nPending;)
            {
                var e = _pending[k];
                var prop = PropagationDelay ? Vector3.Distance(e.Position, Listener) / SpeedOfSound : 0f;
                var at = e.Time + prop;
                if (at < blockEnd)
                {
                    var intended = (float)((at - blockStartTime) * SampleRate);
                    var start = (int)Math.Clamp(Math.Round(intended), 0, Block - 1);
                    if (intended < -0.5f) LateEvents++;
                    Start(e, start, intended);
                    _pending[k] = _pending[--_nPending];
                }
                else k++;
            }
            var act = 0;
            for (var v = 0; v < MaxVoices; v++)
            {
                _live[v] = _active[v];
                if (!_active[v]) continue;
                RenderVoice(v); act++;
            }
            ActiveVoices = act;
            RenderRustle();
            LastRenderMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
    }

    private float Noise(ref uint r)
    {
        r ^= r << 13; r ^= r >> 17; r ^= r << 5;
        return (r * (1f / uint.MaxValue)) * 2f - 1f;
    }

    /// <summary>Chamberlin SVF 대역 통과 1샘플.</summary>
    private static float Svf(float x, float f, float q, ref float lp, ref float bp)
    {
        var hp = x - lp - q * bp;
        bp += f * hp; lp += f * bp;
        return bp;
    }

    private void RenderVoice(int v)
    {
        var buf = VoiceBuffer[v].AsSpan(); buf.Clear();
        const float dt = 1f / SampleRate;
        var start = _delay[v]; _delay[v] = 0;
        var e = _energy[v]; var kind = (FoleyKind)_kind[v]; var sp = Surfaces[_surf[v]];
        float lp = _ic1[v], bp = _ic2[v], ph = _ph[v], ph2 = _ph2[v]; uint r = _rng[v];
        var age = _age[v];
        float maxAge;
        switch (kind)
        {
            case FoleyKind.Footstep:
                {
                    maxAge = 0.35f;
                    var wet = MathF.Max(sp.Wet, Wetness * 0.7f);
                    float fc = 2f * MathF.Sin(MathF.PI * MathF.Min(sp.Center * (0.8f + 0.4f * e), 9000f) / SampleRate), q = 1f / sp.Q;
                    for (var i = start; i < Block; i++)
                    {
                        var t = age; age += dt;
                        var thudF = 95f * (1f - 0.4f * MathF.Min(t / 0.04f, 1f));
                        ph += thudF * dt; ph -= MathF.Floor(ph);
                        var thud = sp.Thud * MathF.Exp(-t / 0.035f) * AudioSimd.SineScalar(ph);
                        var burst = Svf(Noise(ref r), fc, q, ref lp, ref bp) * MathF.Exp(-t / sp.Decay) * 1.2f;
                        var s = thud + burst;
                        if (sp.Grains > 0f)
                            for (var g = 0; g < (int)sp.Grains && g < MaxGrains; g++)
                            {
                                var gt = 0.008f + 0.09f * _grainT[v * MaxGrains + g];
                                var dg = t - gt;
                                if (dg >= 0f && dg < 0.004f) s += 0.35f * Noise(ref r) * MathF.Exp(-dg / 0.0008f);
                            }
                        if (wet > 0f)
                        {
                            // 버블(반경 1~4 mm) 상향 처프
                            var f = MinnaertHz(0.004f - 0.003f * _grainF[v * MaxGrains]) * (1f + 0.3f * t);
                            ph2 += f * dt; ph2 -= MathF.Floor(ph2);
                            var bt = t - 0.01f;
                            if (bt > 0f) s += wet * 0.4f * MathF.Exp(-bt / 0.03f) * AudioSimd.SineScalar(ph2);
                        }
                        buf[i] = s * (0.25f + 0.75f * e);
                    }
                    break;
                }
            case FoleyKind.Impact:
                {
                    maxAge = 0.5f;
                    float fc = 2f * MathF.Sin(MathF.PI * 1400f / SampleRate);
                    for (var i = start; i < Block; i++)
                    {
                        var t = age; age += dt;
                        ph += (70f + 50f * e) * dt; ph -= MathF.Floor(ph);
                        var s = MathF.Exp(-t / 0.08f) * AudioSimd.SineScalar(ph) + Svf(Noise(ref r), fc, 0.8f, ref lp, ref bp) * MathF.Exp(-t / 0.05f);
                        for (var g = 0; g < 6; g++)
                        {
                            var dg = t - (0.03f + 0.3f * _grainT[v * MaxGrains + g]);
                            if (dg >= 0f && dg < 0.006f) s += 0.3f * Noise(ref r) * MathF.Exp(-dg / 0.0015f);
                        }
                        buf[i] = s * e;
                    }
                    break;
                }
            case FoleyKind.Explosion:
                {
                    maxAge = 3f;
                    float fc = 2f * MathF.Sin(MathF.PI * 220f / SampleRate);
                    for (var i = start; i < Block; i++)
                    {
                        var t = age; age += dt;
                        ph += (30f + 60f * MathF.Exp(-t / 0.25f)) * dt; ph -= MathF.Floor(ph);
                        var boom = MathF.Exp(-t / 0.7f) * AudioSimd.SineScalar(ph) * 1.4f;
                        var rumble = Svf(Noise(ref r), fc, 0.5f, ref lp, ref bp) * MathF.Exp(-t / 1.1f) * 2.2f;
                        var crack = 0f;
                        for (var g = 0; g < MaxGrains; g++)
                        {
                            var dg = t - (0.02f + 1.2f * _grainT[v * MaxGrains + g] * _grainT[v * MaxGrains + g]);
                            if (dg >= 0f && dg < 0.01f) crack += 0.4f * Noise(ref r) * MathF.Exp(-dg / 0.002f);
                        }
                        buf[i] = (boom + rumble + crack * MathF.Exp(-t / 1.5f)) * e;
                    }
                    break;
                }
            case FoleyKind.Splash:
                {
                    maxAge = 0.6f;
                    float fc = 2f * MathF.Sin(MathF.PI * 2500f / SampleRate);
                    for (var i = start; i < Block; i++)
                    {
                        var t = age; age += dt;
                        var s = Svf(Noise(ref r), fc, 0.7f, ref lp, ref bp) * MathF.Exp(-t / 0.07f);
                        for (var g = 0; g < 8; g++)
                        {
                            var gt = 0.01f + 0.25f * _grainT[v * MaxGrains + g];
                            var dg = t - gt;
                            if (dg < 0f || dg > 0.05f) continue;
                            var f = MinnaertHz(0.0015f + 0.004f * _grainF[v * MaxGrains + g]) * (1f + 2f * dg);
                            s += 0.25f * MathF.Exp(-dg / 0.012f) * MathF.Sin(MathF.Tau * f * dg);
                        }
                        buf[i] = s * e;
                    }
                    break;
                }
            default:
                {
                    maxAge = 0.5f;
                    for (var i = start; i < Block; i++)
                    {
                        var t = age; age += dt;
                        var sweep = 400f + 3000f * MathF.Sin(MathF.PI * MathF.Min(t / 0.4f, 1f));
                        var fc = 2f * MathF.Sin(MathF.PI * sweep / SampleRate);
                        var env = MathF.Sin(MathF.PI * MathF.Min(t / 0.4f, 1f));
                        buf[i] = Svf(Noise(ref r), fc, 0.6f, ref lp, ref bp) * env * e * 0.8f;
                    }
                    break;
                }
        }
        // 거리 감쇠 + 공기 흡수 LPF
        float g1 = _gain[v], a = _air[v], z = _lp[v];
        for (var i = start; i < Block; i++) { z += a * (buf[i] - z); buf[i] = z * g1; }
        _lp[v] = z;
        _ic1[v] = lp; _ic2[v] = bp; _ph[v] = ph; _ph2[v] = ph2; _rng[v] = r; _age[v] = age;
        if (age > maxAge) _active[v] = false;
    }

    private void RenderRustle()
    {
        var (c, q, gain, crack) = FabricSpec(Fabric);
        var target = gain * 0.12f * MathF.Pow(MathF.Min(RustleSpeed, 4f), 1.5f);
        var fc = 2f * MathF.Sin(MathF.PI * c / SampleRate); var iq = 1f / q;
        var k = 1f - MathF.Exp(-1f / (0.05f * SampleRate));
        float amp = _rustleAmp, lp = _rIc1, bp = _rIc2, cr = _crackle; uint r = _rRng;
        for (var i = 0; i < Block; i++)
        {
            amp += (target - amp) * k;
            // 진폭 변조(직조 마찰 거칠기)
            var x = Noise(ref r) * (0.6f + 0.4f * MathF.Abs(Noise(ref r)));
            var s = Svf(x, fc, iq, ref lp, ref bp) * amp;
            if (crack > 0f)
            {
                if (Noise(ref r) > 1f - 0.002f * crack * MathF.Min(RustleSpeed, 3f)) cr = amp * 3f;
                s += cr * Noise(ref r); cr *= 0.93f;
            }
            _rustle[i] = s;
        }
        _rustleAmp = amp; _rIc1 = lp; _rIc2 = bp; _crackle = cr; _rRng = r;
    }
}
