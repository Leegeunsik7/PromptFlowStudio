using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.SpatialAudio;

public enum SoundKind : byte { Tone = 0, Voice, Wind, Rain, Birds, Water, Footsteps, Bgm, Crowd, Fire }

public enum SoundMotion : byte { Static = 0, Orbit, Flyby }

/// <summary>Atmos/ADM 스타일 객체 메타데이터 프레임(정규화 직교 좌표 −1..1 · 방 기준).</summary>
public readonly record struct ObjectMetadata(long Sample, int Object, float X, float Y, float Z, float Gain, float Distance);

/// <summary>
/// [PromptFlow Studio v16.76.0] 🌲 제너레이티브 3D 사운드스케이프 &amp; 객체 오디오 패닝 합성기.
///   ▸ 객체(32): 절차적 음원 10종(톤 · 성대 펄스+포먼트 음성 · 돌풍 바람 · 빗소리+물방울 · 새 처프 · 물결 · 발소리 · 코드 패드 BGM · 군중 · 모닥불) · 움직임(정지/궤도/통과) — 결정적 시드.
///   ▸ 전파: 객체별 지연선(거리/음속 · 분수 보간) → 도플러 · 전파 지연 · 1/r 감쇠 · 공기 흡음 · 폐색 셸빙(레이트레이서 대역 폐색 저/고역) — 블록 내 파라미터 선형 보간.
///   ▸ 패닝: 7.1.4(ITU-R BS.2051) 3D VBAP — 스피커 삼각망(상층 VOG 가상 스피커 분배) · 전력 정규화 · LFE 저역 송출 · Atmos 객체 메타데이터 스트림.
///   ▸ 잔향: 4선 FDN(하다마드 · 레이트레이서 RT60 저/고역 → 선별 감쇠·HF 감쇠) → 서라운드/상층 베드 + 바이노럴 L/R.
///   ▸ 프롬프트 프리셋(숲/비/도시/바다/카페/캠프파이어) → 객체 배치.
/// </summary>
public sealed class Generative3DSoundscapeSynthesizer
{
    public const int MaxObjects = 32, MaxBlock = 4096, Channels = 12, DelayLen = 16384, StateSize = 16, MetaCapacity = 1024;
    private const int DelayMask = DelayLen - 1;
    private static readonly int[] FdnLen = { 1427, 1693, 2003, 2309 };

    private readonly object _gate = new();
    private readonly int _fs;
    // 객체
    private readonly SoundKind[] _kind = new SoundKind[MaxObjects];
    private readonly SoundMotion[] _motion = new SoundMotion[MaxObjects];
    private readonly string[] _name = new string[MaxObjects];
    private readonly bool[] _active = new bool[MaxObjects];
    private readonly Vector3[] _pos = new Vector3[MaxObjects], _a = new Vector3[MaxObjects], _b = new Vector3[MaxObjects], _vel = new Vector3[MaxObjects];
    private readonly float[] _gain = new float[MaxObjects], _speed = new float[MaxObjects], _param = new float[MaxObjects], _phase0 = new float[MaxObjects];
    private readonly ulong[] _rng = new ulong[MaxObjects];
    private readonly float[] _st = new float[MaxObjects * StateSize];
    private readonly float[] _dl = new float[MaxObjects * DelayLen];
    private readonly int[] _dw = new int[MaxObjects];
    private readonly float[] _prevDelay = new float[MaxObjects], _prevAmp = new float[MaxObjects], _prevLo = new float[MaxObjects], _prevHi = new float[MaxObjects], _lp = new float[MaxObjects], _air = new float[MaxObjects];
    private readonly float[] _prevPan = new float[MaxObjects * Channels], _pan = new float[Channels];
    private readonly bool[] _init = new bool[MaxObjects];
    private readonly float[] _occLo = new float[MaxObjects], _occHi = new float[MaxObjects];
    private int _count;
    // 버퍼
    private readonly float[] _dry = new float[MaxBlock], _obj = new float[MaxObjects * MaxBlock], _bed = new float[Channels * MaxBlock], _send = new float[MaxBlock];
    private readonly float[] _revL = new float[MaxBlock], _revR = new float[MaxBlock], _lfeLp = new float[1];
    private readonly float[] _meter = new float[Channels];
    // VBAP
    private readonly int[] _tri = BuildTriangles();
    private readonly Matrix4x4[] _triInv;
    private static readonly Vector3[] SpeakerDir = BuildSpeakerDirs();
    // FDN
    private readonly float[] _fdn = new float[4 * 4096];
    private readonly int[] _fdnPos = new int[4];
    private readonly float[] _fdnLp = new float[4];
    private float _rtLo = 1.2f, _rtHi = 0.8f;
    // 메타데이터
    private readonly ObjectMetadata[] _meta = new ObjectMetadata[MetaCapacity];
    private long _metaSeq, _sampleClock;
    private Vector3 _roomMin = new(-5, 0, -4), _roomMax = new(5, 3.5f, 4);

    public Generative3DSoundscapeSynthesizer(int sampleRate = 48000)
    {
        _fs = sampleRate;
        _triInv = new Matrix4x4[_tri.Length / 3];
        for (var t = 0; t < _triInv.Length; t++)
        {
            var a = SpeakerDir[_tri[t * 3]]; var b = SpeakerDir[_tri[t * 3 + 1]]; var c = SpeakerDir[_tri[t * 3 + 2]];
            var m = new Matrix4x4(a.X, a.Y, a.Z, 0, b.X, b.Y, b.Z, 0, c.X, c.Y, c.Z, 0, 0, 0, 0, 1);
            Matrix4x4.Invert(m, out _triInv[t]);
        }
    }

    public event Action? OnChange;

    public int SampleRate => _fs;
    public int ObjectCount => _count;
    public float ReverbSend { get; set; } = 0.25f;
    public float LfeSend { get; set; } = 0.3f;
    public float MasterGain { get; set; } = 0.6f;
    public bool Doppler { get; set; } = true;
    public string? LastAction { get; private set; }
    public long MetadataCount => Interlocked.Read(ref _metaSeq);
    public string Name(int i) => _name[i];
    public SoundKind KindOf(int i) => _kind[i];
    public bool IsActive(int i) => _active[i];
    public Vector3 Position(int i) => _pos[i];
    public float Gain(int i) => _gain[i];
    public float Meter(int ch) => _meter[ch];
    public ReadOnlySpan<float> ObjectSignal(int i, int n) => _obj.AsSpan(i * MaxBlock, n);
    public ReadOnlySpan<float> Bed(int ch, int n) => _bed.AsSpan(ch * MaxBlock, n);
    public ReadOnlySpan<float> BedBuffer => _bed;
    public ReadOnlySpan<float> ReverbL(int n) => _revL.AsSpan(0, n);
    public ReadOnlySpan<float> ReverbR(int n) => _revR.AsSpan(0, n);
    /// <summary>객체의 직전 블록 끝 전파 지연(s).</summary>
    public float PropagationDelay(int i) => _prevDelay[i] / _fs;

    // ══════════════════════ 객체(제어 평면) ══════════════════════

    public int AddObject(string name, SoundKind kind, Vector3 position, float gain = 1f, float param = 0f, SoundMotion motion = SoundMotion.Static, Vector3 b = default, float speed = 0f)
    {
        lock (_gate)
        {
            var i = -1;
            for (var k = 0; k < _count; k++) if (!_active[k]) { i = k; break; }
            if (i < 0) { if (_count >= MaxObjects) return -1; i = _count++; }
            _name[i] = name; _kind[i] = kind; _active[i] = true; _pos[i] = position; _a[i] = position; _b[i] = b; _motion[i] = motion; _speed[i] = speed;
            _gain[i] = gain; _param[i] = param; _vel[i] = Vector3.Zero; _init[i] = false;
            _rng[i] = 0x5EED0000UL + (ulong)i * 0x9E3779B97F4A7C15UL + (ulong)kind;
            _phase0[i] = i * 0.37f;
            _st.AsSpan(i * StateSize, StateSize).Clear();
            _dl.AsSpan(i * DelayLen, DelayLen).Clear();
            _occLo[i] = _occHi[i] = 1;
            return i;
        }
    }

    public void RemoveObject(int i) { lock (_gate) _active[i] = false; }

    public void ClearObjects() { lock (_gate) { for (var i = 0; i < _count; i++) _active[i] = false; _count = 0; } }

    public void SetPosition(int i, Vector3 p) { lock (_gate) { _a[i] = p; if (_motion[i] == SoundMotion.Static) _pos[i] = p; } }

    public void SetGain(int i, float g) { lock (_gate) _gain[i] = MathF.Max(0, g); }

    /// <summary>폐색(대역 진폭 계수 — 저역 = 250 Hz · 고역 = 4 kHz).</summary>
    public void SetOcclusion(int i, float low, float high) { lock (_gate) { _occLo[i] = Math.Clamp(low, 0f, 1f); _occHi[i] = Math.Clamp(high, 0f, 1f); } }

    public void SetReverb(float rt60Low, float rt60High) { lock (_gate) { _rtLo = Math.Clamp(rt60Low, 0.05f, 12f); _rtHi = Math.Clamp(rt60High, 0.05f, 12f); } }

    public void SetRoomBounds(Vector3 min, Vector3 max) { lock (_gate) { _roomMin = min; _roomMax = max; } }

    /// <summary>프롬프트 → 사운드스케이프 프리셋. 반환 = 생성 객체 수.</summary>
    public int ApplyPrompt(string prompt)
    {
        var p = (prompt ?? "").ToLowerInvariant();
        ClearObjects();
        bool Has(params string[] k) { foreach (var w in k) if (p.Contains(w, StringComparison.Ordinal)) return true; return false; }
        if (Has("숲", "forest", "새", "bird"))
        {
            AddObject("새 1", SoundKind.Birds, new Vector3(-4, 4, -3), 0.5f, 0);
            AddObject("새 2", SoundKind.Birds, new Vector3(3, 5, -4), 0.4f, 1, SoundMotion.Orbit, new Vector3(0, 5, 0), 0.4f);
            AddObject("새 3", SoundKind.Birds, new Vector3(5, 3, 3), 0.35f, 2);
            AddObject("시냇물", SoundKind.Water, new Vector3(-3, 0, 4), 0.5f);
            AddObject("바람", SoundKind.Wind, new Vector3(0, 6, -6), 0.4f);
        }
        if (Has("비", "rain", "폭우", "storm"))
        {
            AddObject("빗소리 L", SoundKind.Rain, new Vector3(-4, 3, -2), 0.5f);
            AddObject("빗소리 R", SoundKind.Rain, new Vector3(4, 3, -2), 0.5f);
            AddObject("빗소리 후방", SoundKind.Rain, new Vector3(0, 3, 4), 0.4f);
            AddObject("바람", SoundKind.Wind, new Vector3(0, 6, -6), 0.35f);
        }
        if (Has("도시", "city", "거리", "street"))
        {
            AddObject("군중", SoundKind.Crowd, new Vector3(-3, 1.5f, -4), 0.5f);
            AddObject("발소리", SoundKind.Footsteps, new Vector3(-6, 0, 2), 0.6f, 0, SoundMotion.Flyby, new Vector3(6, 0, 2), 1.4f);
            AddObject("지나가는 차", SoundKind.Tone, new Vector3(-20, 0.5f, -6), 0.4f, 110, SoundMotion.Flyby, new Vector3(20, 0.5f, -6), 14f);
        }
        if (Has("바다", "ocean", "해변", "beach", "파도"))
        {
            AddObject("파도", SoundKind.Water, new Vector3(0, 0, -8), 0.8f, 1);
            AddObject("바람", SoundKind.Wind, new Vector3(-6, 4, -2), 0.4f);
            AddObject("갈매기", SoundKind.Birds, new Vector3(2, 6, -5), 0.35f, 3, SoundMotion.Orbit, new Vector3(0, 6, -3), 0.6f);
        }
        if (Has("카페", "cafe", "coffee"))
        {
            AddObject("손님들", SoundKind.Crowd, new Vector3(3, 1.2f, -2), 0.4f);
            AddObject("배경 음악", SoundKind.Bgm, new Vector3(-3, 2.5f, -3.5f), 0.35f);
        }
        if (Has("캠프", "camp", "모닥불", "fire"))
        {
            AddObject("모닥불", SoundKind.Fire, new Vector3(0, 0.3f, -2), 0.7f);
            AddObject("밤바람", SoundKind.Wind, new Vector3(5, 5, 0), 0.25f);
        }
        if (_count == 0) AddObject("배경 음악", SoundKind.Bgm, new Vector3(0, 2, -3), 0.4f);
        AddObject("내레이터", SoundKind.Voice, new Vector3(1.5f, 1.6f, -1.5f), 0.7f);
        LastAction = "🌲 사운드스케이프 · 객체 " + _count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Raise();
        return _count;
    }

    // ══════════════════════ VBAP ══════════════════════

    private static Vector3 Dir(float az, float el)
    {
        var a = az * MathF.PI / 180f; var e = el * MathF.PI / 180f;
        return new Vector3(MathF.Sin(a) * MathF.Cos(e), MathF.Sin(e), -MathF.Cos(a) * MathF.Cos(e));
    }

    private static Vector3[] BuildSpeakerDirs()
    {
        var d = new Vector3[13];
        for (var c = 0; c < 12; c++) { var (az, el) = NeuralBinauralHrtfSpatializer.BedLayout[c]; d[c] = c == 3 ? Vector3.Zero : Dir(az, el); }
        d[12] = Vector3.UnitY;   // VOG(가상 천정)
        return d;
    }

    private static int[] BuildTriangles()
    {
        const int L = 0, R = 1, C = 2, Ls = 4, Rs = 5, Lb = 6, Rb = 7, Ltf = 8, Rtf = 9, Ltb = 10, Rtb = 11, V = 12;
        return new[]
        {
            C, R, Rtf,  C, Rtf, Ltf,  C, Ltf, L,
            R, Rs, Rtf,  Rs, Rtb, Rtf,  Rs, Rb, Rtb,
            Rb, Lb, Rtb,  Lb, Ltb, Rtb,
            Lb, Ls, Ltb,  Ls, Ltf, Ltb,  Ls, L, Ltf,
            V, Ltf, Rtf,  V, Rtf, Rtb,  V, Rtb, Ltb,  V, Ltb, Ltf,
        };
    }

    /// <summary>
    /// 3D VBAP(청취자 기준 방향 · 정면 −Z · 오른쪽 +X · 하반구는 수평으로 투영) → 12채널 전력 정규화 이득(LFE = 0). 무할당.
    /// </summary>
    public void Pan(Vector3 dir, Span<float> gains)
    {
        gains[..Channels].Clear();
        var d = dir;
        if (d.Y < 0) d.Y = 0;
        if (d.LengthSquared() < 1e-12f) d = -Vector3.UnitZ;
        d = Vector3.Normalize(d);
        var best = -1; float g1 = 0, g2 = 0, g3 = 0, bestMin = float.MinValue;
        for (var t = 0; t < _triInv.Length; t++)
        {
            var g = Vector3.Transform(d, _triInv[t]);   // d = g·[a;b;c] → g = d·M⁻¹
            var mn = MathF.Min(g.X, MathF.Min(g.Y, g.Z));
            if (mn > bestMin) { bestMin = mn; best = t; g1 = g.X; g2 = g.Y; g3 = g.Z; }
            if (mn >= -1e-5f) break;
        }
        g1 = MathF.Max(0, g1); g2 = MathF.Max(0, g2); g3 = MathF.Max(0, g3);
        Span<float> tmp = stackalloc float[13];
        tmp[_tri[best * 3]] += g1; tmp[_tri[best * 3 + 1]] += g2; tmp[_tri[best * 3 + 2]] += g3;
        if (tmp[12] > 0) { var share = tmp[12] * 0.5f; tmp[8] += share; tmp[9] += share; tmp[10] += share; tmp[11] += share; tmp[12] = 0; }
        float p = 0;
        for (var c = 0; c < 12; c++) p += tmp[c] * tmp[c];
        var norm = p > 0 ? 1f / MathF.Sqrt(p) : 0f;
        for (var c = 0; c < 12; c++) gains[c] = tmp[c] * norm;
    }

    // ══════════════════════ 음원 생성(무할당) ══════════════════════

    private float Rand(int i)
    {
        var x = _rng[i]; x ^= x << 13; x ^= x >> 7; x ^= x << 17; _rng[i] = x;
        return ((x * 0x2545F4914F6CDD1DUL) >> 40) * (1f / (1 << 24));
    }

    private float Noise(int i) => Rand(i) * 2 - 1;

    private void Generate(int i, Span<float> y, long clock)
    {
        var s = _st.AsSpan(i * StateSize, StateSize);
        var fs = (float)_fs;
        var dt = 1f / fs;
        switch (_kind[i])
        {
            case SoundKind.Tone:
            {
                var f = _param[i] > 0 ? _param[i] : 440f;
                for (var k = 0; k < y.Length; k++) { s[0] += MathF.Tau * f * dt; if (s[0] > MathF.Tau) s[0] -= MathF.Tau; y[k] = 0.5f * (MathF.Sin(s[0]) + 0.3f * MathF.Sin(2 * s[0])); }
                break;
            }
            case SoundKind.Voice:
            {
                // 성대 톱니(140 Hz · 비브라토) → 포먼트 2(모음 순환 · 음절 포락)
                for (var k = 0; k < y.Length; k++)
                {
                    var t = (clock + k) * dt + _phase0[i];
                    var f0 = 140f * (1 + 0.02f * MathF.Sin(MathF.Tau * 5.5f * t));
                    s[0] += f0 * dt; if (s[0] >= 1) s[0] -= 1;
                    var src = 2 * s[0] - 1;
                    var syl = (int)(t * 4); var vowel = (int)((uint)(syl * 2654435761u) % 5);
                    var (f1, f2) = vowel switch { 0 => (730f, 1090f), 1 => (530f, 1840f), 2 => (270f, 2290f), 3 => (570f, 840f), _ => (300f, 870f) };
                    var env = MathF.Max(0, MathF.Sin(MathF.PI * (t * 4 - syl))) * (syl % 7 == 6 ? 0 : 1);
                    var o1 = Resonator(s, 2, src, f1, 80f, fs); var o2 = Resonator(s, 6, src, f2, 120f, fs);
                    y[k] = 0.25f * env * (o1 + 0.6f * o2);
                }
                break;
            }
            case SoundKind.Wind:
                for (var k = 0; k < y.Length; k++)
                {
                    var t = (clock + k) * dt + _phase0[i];
                    var gust = 0.5f + 0.5f * MathF.Sin(MathF.Tau * 0.13f * t) * MathF.Sin(MathF.Tau * 0.051f * t + 1);
                    var cut = 200f + 900f * gust;
                    var a = 1 - MathF.Exp(-MathF.Tau * cut * dt);
                    s[0] += (Noise(i) - s[0]) * a; s[1] += (s[0] - s[1]) * a;
                    y[k] = 1.6f * s[1] * (0.3f + 0.7f * gust);
                }
                break;
            case SoundKind.Rain:
                for (var k = 0; k < y.Length; k++)
                {
                    var n = Noise(i);
                    s[0] += (n - s[0]) * 0.35f;
                    var hiss = n - s[0];
                    if (Rand(i) < 40f * dt) s[1] = 0.6f + 0.4f * Rand(i);
                    s[1] *= 0.993f;
                    s[2] += MathF.Tau * (2000f + 1500f * s[3]) * dt; if (Rand(i) < 40f * dt) s[3] = Rand(i);
                    y[k] = 0.18f * hiss + 0.25f * s[1] * MathF.Sin(s[2]);
                }
                break;
            case SoundKind.Birds:
                for (var k = 0; k < y.Length; k++)
                {
                    // s0 = 남은 처프 샘플 · s1 = 위상 · s2 = 처프 진행 · s3 = 시작 주파수
                    if (s[0] <= 0 && Rand(i) < 2.5f * dt) { s[0] = 0.07f * fs; s[2] = 0; s[3] = 2200f + 2500f * Rand(i) + 400f * _param[i]; }
                    if (s[0] > 0)
                    {
                        var prog = s[2] / (0.07f * fs);
                        var f = s[3] * (1 + 0.6f * prog) + 300f * MathF.Sin(MathF.Tau * 30f * prog);
                        s[1] += MathF.Tau * f * dt; if (s[1] > MathF.Tau) s[1] -= MathF.Tau;
                        y[k] = 0.4f * MathF.Sin(MathF.PI * prog) * MathF.Sin(s[1]);
                        s[0]--; s[2]++;
                    }
                    else y[k] = 0;
                }
                break;
            case SoundKind.Water:
                for (var k = 0; k < y.Length; k++)
                {
                    var t = (clock + k) * dt + _phase0[i];
                    var n = Noise(i);
                    s[0] += (n - s[0]) * 0.08f; s[1] += (s[0] - s[1]) * 0.08f;
                    var band = s[0] - s[1];
                    var swell = _param[i] >= 1 ? 0.25f + 0.75f * MathF.Pow(0.5f + 0.5f * MathF.Sin(MathF.Tau * 0.1f * t), 3) : 0.8f + 0.2f * MathF.Sin(MathF.Tau * 3.1f * t);
                    if (Rand(i) < 25f * dt) { s[2] = 0.3f; s[3] = 600f + 900f * Rand(i); }
                    s[2] *= 0.996f; s[4] += MathF.Tau * s[3] * (1 + 0.5f * (0.3f - s[2])) * dt;
                    y[k] = 2.2f * band * swell + 0.2f * s[2] * MathF.Sin(s[4]);
                }
                break;
            case SoundKind.Footsteps:
                for (var k = 0; k < y.Length; k++)
                {
                    var t = (clock + k) * dt + _phase0[i];
                    var step = t * 2f;
                    var fr = step - MathF.Floor(step);
                    var env = fr < 0.12f ? MathF.Exp(-fr * 45f) : 0f;
                    s[0] += MathF.Tau * 85f * dt;
                    y[k] = env * (0.6f * MathF.Sin(s[0]) + 0.25f * Noise(i));
                }
                break;
            case SoundKind.Bgm:
                for (var k = 0; k < y.Length; k++)
                {
                    var t = (clock + k) * dt;
                    var chord = (int)(t / 4f) & 3;
                    var root = chord switch { 0 => 220f, 1 => 174.61f, 2 => 261.63f, _ => 196f };
                    s[0] += MathF.Tau * root * dt; s[1] += MathF.Tau * root * 1.25992f * dt; s[2] += MathF.Tau * root * 1.49831f * dt;
                    if (s[0] > MathF.Tau) s[0] -= MathF.Tau;
                    if (s[1] > MathF.Tau) s[1] -= MathF.Tau;
                    if (s[2] > MathF.Tau) s[2] -= MathF.Tau;
                    var trem = 0.8f + 0.2f * MathF.Sin(MathF.Tau * 0.5f * t);
                    y[k] = 0.2f * trem * (MathF.Sin(s[0]) + 0.8f * MathF.Sin(s[1]) + 0.7f * MathF.Sin(s[2]));
                }
                break;
            case SoundKind.Crowd:
                for (var k = 0; k < y.Length; k++)
                {
                    var t = (clock + k) * dt + _phase0[i];
                    var n = Noise(i);
                    var f1 = 500f + 300f * MathF.Sin(MathF.Tau * 2.3f * t); var f2 = 1500f + 600f * MathF.Sin(MathF.Tau * 1.7f * t + 2);
                    var o = Resonator(s, 0, n, f1, 200f, fs) + 0.6f * Resonator(s, 4, n, f2, 300f, fs);
                    var mod = 0.6f + 0.4f * MathF.Sin(MathF.Tau * 3.3f * t) * MathF.Sin(MathF.Tau * 0.7f * t);
                    y[k] = 0.12f * o * mod;
                }
                break;
            default:   // Fire
                for (var k = 0; k < y.Length; k++)
                {
                    var n = Noise(i);
                    s[0] += (n - s[0]) * 0.01f;
                    if (Rand(i) < 18f * dt) s[1] = (0.5f + 0.5f * Rand(i)) * (Rand(i) < 0.5f ? 1 : -1);
                    s[1] *= 0.92f;
                    y[k] = 1.8f * s[0] + 0.5f * s[1];
                }
                break;
        }
    }

    /// <summary>2극 공진기(대역폭 bw Hz) — 상태 s[o..o+1].</summary>
    private static float Resonator(Span<float> s, int o, float x, float f, float bw, float fs)
    {
        var r = MathF.Exp(-MathF.PI * bw / fs);
        var a1 = -2 * r * MathF.Cos(MathF.Tau * f / fs); var a2 = r * r;
        var yv = (1 - r) * x - a1 * s[o] - a2 * s[o + 1];
        s[o + 1] = s[o]; s[o] = yv;
        return yv;
    }

    private Vector3 MotionAt(int i, float t)
    {
        switch (_motion[i])
        {
            case SoundMotion.Orbit:
            {
                var c = _b[i]; var r0 = _a[i] - c; var rad = new Vector2(r0.X, r0.Z).Length();
                var ang = MathF.Atan2(r0.Z, r0.X) + _speed[i] * t;
                return new Vector3(c.X + rad * MathF.Cos(ang), _a[i].Y, c.Z + rad * MathF.Sin(ang));
            }
            case SoundMotion.Flyby:
            {
                var len = Vector3.Distance(_a[i], _b[i]);
                if (len < 1e-4f || _speed[i] <= 0) return _a[i];
                var period = 2 * len / _speed[i];
                var ph = (t % period) / period;
                var f = ph < 0.5f ? ph * 2 : 2 - ph * 2;
                return Vector3.Lerp(_a[i], _b[i], f);
            }
            default: return _a[i];
        }
    }

    // ══════════════════════ 블록 처리(무할당) ══════════════════════

    /// <summary>
    /// n 샘플 처리: 객체 생성 → 움직임 → 전파(도플러·감쇠·폐색) → 객체 신호(<see cref="ObjectSignal"/>) · 7.1.4 베드 · 잔향 L/R · 메타데이터. 반환 = 처리 샘플 수.
    /// </summary>
    public int ProcessBlock(Vector3 listener, int n)
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Soundscape.Block");
            n = Math.Clamp(n, 1, MaxBlock);
            var fs = (float)_fs;
            _bed.AsSpan().Clear();
            _send.AsSpan(0, n).Clear();
            var clock = _sampleClock;
            var tEnd = (clock + n) / fs;
            var c = AcousticBands.SpeedOfSound;
            for (var i = 0; i < _count; i++)
            {
                var dst = _obj.AsSpan(i * MaxBlock, n);
                if (!_active[i]) { dst.Clear(); continue; }
                var dry = _dry.AsSpan(0, n);
                Generate(i, dry, clock);
                var newPos = MotionAt(i, tEnd);
                var blockDt = n / fs;
                _vel[i] = _init[i] ? (newPos - _pos[i]) / blockDt : Vector3.Zero;
                _pos[i] = newPos;
                var rel = newPos - listener;
                var dist = MathF.Max(rel.Length(), 0.25f);
                var delay = MathF.Min(Doppler ? dist / c * fs : 0f, DelayLen - 4);
                var amp = _gain[i] / MathF.Max(dist, 1f);
                var lo = _occLo[i]; var hi = _occHi[i] * MathF.Exp(-0.5f * AcousticBands.AirAbsorption[5] * dist);
                if (!_init[i]) { _prevDelay[i] = delay; _prevAmp[i] = amp; _prevLo[i] = lo; _prevHi[i] = hi; }
                // 지연선 기록 → 가변 지연 읽기(도플러)
                var db = i * DelayLen; var w = _dw[i];
                for (var k = 0; k < n; k++) _dl[db + ((w + k) & DelayMask)] = dry[k];
                var lpA = 1 - MathF.Exp(-MathF.Tau * 1000f / fs);
                var inv = 1f / n;
                for (var k = 0; k < n; k++)
                {
                    var f = (k + 1) * inv;
                    var d = _prevDelay[i] + (delay - _prevDelay[i]) * f;
                    var rp = w + k - d;
                    var i0 = (int)MathF.Floor(rp); var fr = rp - i0;
                    var x = _dl[db + (i0 & DelayMask)] * (1 - fr) + _dl[db + ((i0 + 1) & DelayMask)] * fr;
                    _lp[i] += (x - _lp[i]) * lpA;
                    var g = _prevAmp[i] + (amp - _prevAmp[i]) * f;
                    var gl = _prevLo[i] + (lo - _prevLo[i]) * f; var gh = _prevHi[i] + (hi - _prevHi[i]) * f;
                    dst[k] = g * (_lp[i] * gl + (x - _lp[i]) * gh);
                }
                _dw[i] = (w + n) & DelayMask;
                _prevDelay[i] = delay; _prevAmp[i] = amp; _prevLo[i] = lo; _prevHi[i] = hi;
                // 패닝(블록 내 이득 보간)
                Pan(rel, _pan);
                var pp = _prevPan.AsSpan(i * Channels, Channels);
                if (!_init[i]) _pan.AsSpan().CopyTo(pp);
                for (var ch = 0; ch < Channels; ch++)
                {
                    if (ch == 3) continue;
                    var g0 = pp[ch]; var g1 = _pan[ch];
                    if (g0 == 0 && g1 == 0) continue;
                    var bo = ch * MaxBlock;
                    for (var k = 0; k < n; k++) _bed[bo + k] += dst[k] * (g0 + (g1 - g0) * (k + 1) * inv);
                }
                _pan.AsSpan().CopyTo(pp);
                for (var k = 0; k < n; k++) _send[k] += dst[k];
                _init[i] = true;
                // 메타데이터(ADM 직교 좌표 정규화)
                var span = _roomMax - _roomMin;
                var nx = Math.Clamp((newPos.X - _roomMin.X) / MathF.Max(span.X, 1e-3f) * 2 - 1, -1f, 1f);
                var ny = Math.Clamp((newPos.Z - _roomMin.Z) / MathF.Max(span.Z, 1e-3f) * -2 + 1, -1f, 1f);
                var nz = Math.Clamp((newPos.Y - _roomMin.Y) / MathF.Max(span.Y, 1e-3f), 0f, 1f);
                _meta[_metaSeq % MetaCapacity] = new ObjectMetadata(clock, i, nx, ny, nz, amp, dist);
                _metaSeq++;
            }
            // LFE: 합 저역(120 Hz)
            var la = 1 - MathF.Exp(-MathF.Tau * 120f / fs);
            var lfe = _bed.AsSpan(3 * MaxBlock, n);
            for (var k = 0; k < n; k++) { _lfeLp[0] += (_send[k] - _lfeLp[0]) * la; lfe[k] = _lfeLp[0] * LfeSend; }
            // 잔향 FDN
            Reverb(n);
            for (var k = 0; k < n; k++)
            {
                var l = _revL[k]; var r = _revR[k];
                _bed[4 * MaxBlock + k] += l * 0.7f; _bed[5 * MaxBlock + k] += r * 0.7f;
                _bed[6 * MaxBlock + k] += l * 0.6f; _bed[7 * MaxBlock + k] += r * 0.6f;
                _bed[10 * MaxBlock + k] += l * 0.4f; _bed[11 * MaxBlock + k] += r * 0.4f;
            }
            var mg = MasterGain;
            for (var ch = 0; ch < Channels; ch++)
            {
                var b = _bed.AsSpan(ch * MaxBlock, n);
                double acc = 0;
                for (var k = 0; k < n; k++) { b[k] *= mg; acc += b[k] * b[k]; }
                _meter[ch] = (float)Math.Sqrt(acc / n);
            }
            _sampleClock = clock + n;
            return n;
        }
    }

    private void Reverb(int n)
    {
        var fs = (float)_fs;
        Span<float> g = stackalloc float[4]; Span<float> hfA = stackalloc float[4]; Span<float> o = stackalloc float[4];
        for (var j = 0; j < 4; j++)
        {
            var len = FdnLen[j] * fs / 48000f;
            g[j] = MathF.Pow(10, -3 * len / (fs * _rtLo));
            var gHi = MathF.Pow(10, -3 * len / (fs * _rtHi));
            hfA[j] = Math.Clamp(1 - gHi / MathF.Max(g[j], 1e-6f), 0f, 0.95f);   // 루프 내 1극 저역통과(고역 추가 감쇠)
        }
        var send = ReverbSend;
        for (var k = 0; k < n; k++)
        {
            for (var j = 0; j < 4; j++)
            {
                var len = (int)(FdnLen[j] * fs / 48000f);
                o[j] = _fdn[j * 4096 + ((_fdnPos[j] - len) & 4095)];
                _fdnLp[j] += (o[j] - _fdnLp[j]) * (1 - hfA[j]);
                o[j] = _fdnLp[j] * g[j];
            }
            // 하다마드 4×4 / 2
            float h0 = (o[0] + o[1] + o[2] + o[3]) * 0.5f, h1 = (o[0] - o[1] + o[2] - o[3]) * 0.5f, h2 = (o[0] + o[1] - o[2] - o[3]) * 0.5f, h3 = (o[0] - o[1] - o[2] + o[3]) * 0.5f;
            var x = _send[k] * send;
            _fdn[0 * 4096 + (_fdnPos[0] & 4095)] = x + h0; _fdn[1 * 4096 + (_fdnPos[1] & 4095)] = x + h1;
            _fdn[2 * 4096 + (_fdnPos[2] & 4095)] = x + h2; _fdn[3 * 4096 + (_fdnPos[3] & 4095)] = x + h3;
            for (var j = 0; j < 4; j++) _fdnPos[j]++;
            _revL[k] = (o[0] + o[2]) * 0.5f; _revR[k] = (o[1] + o[3]) * 0.5f;
        }
    }

    /// <summary>최근 메타데이터(최신 우선) 복사.</summary>
    public int CopyMetadata(Span<ObjectMetadata> dst)
    {
        lock (_gate)
        {
            var n = (int)Math.Min(Math.Min(_metaSeq, MetaCapacity), dst.Length);
            for (var k = 0; k < n; k++) dst[k] = _meta[(_metaSeq - 1 - k) % MetaCapacity];
            return n;
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Soundscape.OnChange", ex); }
    }
}
