using System.Numerics;
using System.Runtime.InteropServices;

namespace PromptFlowStudio.Services.FilmScore;

/// <summary>믹스 스템: 음악 5 + 폴리 + 대사.</summary>
public enum Stem : byte { Strings, Brass, Woodwinds, Percussion, Ambient, Foley, Dialogue }

public enum ScoreMode : byte { Major, Dorian, Minor, Phrygian }

public enum Instrument : byte { StringLong, StringShort, Brass, BrassStab, Flute, Timpani, Drum, Snare, Cymbal, Pad }

/// <summary>오디오 공용 SIMD 커널(Vector&lt;float&gt; · 꼬리 스칼라 — 스칼라 판과 비트 일치).</summary>
public static class AudioSimd
{
    private static readonly Vector<float> Index = MakeIndex();

    private static Vector<float> MakeIndex()
    {
        Span<float> s = stackalloc float[Vector<float>.Count];
        for (var i = 0; i < s.Length; i++) s[i] = i;
        return new Vector<float>(s);
    }

    private static int Body(int n) => Vector.IsHardwareAccelerated && n >= Vector<float>.Count ? n - n % Vector<float>.Count : 0;

    /// <summary>위상 램프: dst[i] = frac(start + inc·i).</summary>
    public static void PhaseRamp(Span<float> dst, float start, float inc)
    {
        var n = dst.Length; var m = Body(n);
        if (m > 0)
        {
            var d = MemoryMarshal.Cast<float, Vector<float>>(dst[..m]);
            Vector<float> vs = new(start), vi = new(inc), step = new((float)Vector<float>.Count), idx = Index;
            for (var k = 0; k < d.Length; k++) { var x = vs + vi * idx; d[k] = x - Vector.Floor(x); idx += step; }
        }
        for (var i = m; i < n; i++) { var x = start + inc * (float)i; dst[i] = x - MathF.Floor(x); }
    }

    public static void PhaseRampScalar(Span<float> dst, float start, float inc)
    {
        for (var i = 0; i < dst.Length; i++) { var x = start + inc * (float)i; dst[i] = x - MathF.Floor(x); }
    }

    /// <summary>sin(2πx) 근사(x ∈ [0,1) · 포물선 + 0.225 보정 · 최대 오차 ≈ 1e-3).</summary>
    public static void SinePoly(Span<float> x)
    {
        var n = x.Length; var m = Body(n);
        if (m > 0)
        {
            var d = MemoryMarshal.Cast<float, Vector<float>>(x[..m]);
            Vector<float> h = new(0.5f), a = new(8f), b = new(16f), p = new(0.225f);
            for (var k = 0; k < d.Length; k++)
            {
                var u = d[k] - h; var au = Vector.Abs(u);
                var y = a * u - b * u * au;
                y = p * (y * Vector.Abs(y) - y) + y;
                d[k] = -y;
            }
        }
        for (var i = m; i < n; i++) x[i] = SineScalar(x[i]);
    }

    public static float SineScalar(float x)
    {
        var u = x - 0.5f; var au = MathF.Abs(u);
        var y = 8f * u - 16f * u * au;
        y = 0.225f * (y * MathF.Abs(y) - y) + y;
        return -y;
    }

    public static void SinePolyScalar(Span<float> x)
    {
        for (var i = 0; i < x.Length; i++) x[i] = SineScalar(x[i]);
    }

    /// <summary>dst += a·s.</summary>
    public static void MulAdd(Span<float> dst, ReadOnlySpan<float> a, float s)
    {
        var n = dst.Length; var m = Body(n);
        if (m > 0)
        {
            var d = MemoryMarshal.Cast<float, Vector<float>>(dst[..m]); var va = MemoryMarshal.Cast<float, Vector<float>>(a[..m]);
            var vs = new Vector<float>(s);
            for (var k = 0; k < d.Length; k++) d[k] = d[k] + va[k] * vs;
        }
        for (var i = m; i < n; i++) dst[i] = dst[i] + a[i] * s;
    }

    public static void MulAddScalar(Span<float> dst, ReadOnlySpan<float> a, float s)
    {
        for (var i = 0; i < dst.Length; i++) dst[i] = dst[i] + a[i] * s;
    }

    /// <summary>dst += a·b.</summary>
    public static void MulAcc(Span<float> dst, ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var n = dst.Length; var m = Body(n);
        if (m > 0)
        {
            var d = MemoryMarshal.Cast<float, Vector<float>>(dst[..m]); var va = MemoryMarshal.Cast<float, Vector<float>>(a[..m]); var vb = MemoryMarshal.Cast<float, Vector<float>>(b[..m]);
            for (var k = 0; k < d.Length; k++) d[k] = d[k] + va[k] * vb[k];
        }
        for (var i = m; i < n; i++) dst[i] = dst[i] + a[i] * b[i];
    }

    public static void MulAccScalar(Span<float> dst, ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        for (var i = 0; i < dst.Length; i++) dst[i] = dst[i] + a[i] * b[i];
    }

    /// <summary>제곱합(RMS 계측).</summary>
    public static float SumSquares(ReadOnlySpan<float> a)
    {
        var n = a.Length; var m = Body(n); var acc = Vector<float>.Zero;
        if (m > 0)
        {
            var va = MemoryMarshal.Cast<float, Vector<float>>(a[..m]);
            for (var k = 0; k < va.Length; k++) acc += va[k] * va[k];
        }
        var s = Vector.Dot(acc, Vector<float>.One);
        for (var i = m; i < n; i++) s += a[i] * a[i];
        return s;
    }
}

/// <summary>
/// [PromptFlow Studio v16.86.0] 🎼 실시간 시네마틱 오케스트라 BGM 생성기(48 kHz · 블록 800 = 60 Hz · 무할당 · 결정적 RNG).
///   ▸ 감정선(정서가 · 각성 · 긴장) → 선법(장조/도리안/단조/프리지안 · 관계조 전조로 공통음 유지) · 진행(마디 단위) · 템포(마디 경계 램프) · 레이어 활성.
///   ▸ 편곡: 앰비언트 패드(근음+5도) · 현 4성부(최소 이동 성부 진행) · 저현 스피카토 오스티나토 · 목관 선율(강박 = 화성음 · 약박 = 음계 랜덤 워크) · 금관 스탭/스웰 · 팀파니/북/스네어/심벌.
///   ▸ 컷 전환: 다음 박(하드 컷 = 즉시)에 스팅어(심벌 + 팀파니 + 금관 히트) · 지연 계측.
///   ▸ 렌더: 보이스 40(SoA) · 배음 4 · 샘플 정확 노트 온(블록 내 오프셋) · 위상 램프/사인 근사/누적 SIMD · 노이즈(심벌/스네어/숨결) 1차 HPF · 스템 5 페이더.
/// </summary>
public sealed class NeuralCinematographicFilmScoreGenerator
{
    public const int SampleRate = 48000, Block = 800, MaxVoices = 40, Partials = 4, MusicStems = 5;
    private static readonly string[] NoteNames = { "C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B" };
    private static readonly int[][] Scales =
    {
        new[] { 0, 2, 4, 5, 7, 9, 11 }, new[] { 0, 2, 3, 5, 7, 9, 10 }, new[] { 0, 2, 3, 5, 7, 8, 10 }, new[] { 0, 1, 3, 5, 7, 8, 10 },
    };
    // 진행(음계 도수 0 기반 · 4마디 순환): 장조 I–V–vi–IV · 도리안 i–IV–i–VII · 단조 i–VI–III–VII · 프리지안 i–II–i–VII
    private static readonly int[][] Progressions = { new[] { 0, 4, 5, 3 }, new[] { 0, 3, 0, 6 }, new[] { 0, 5, 2, 6 }, new[] { 0, 1, 0, 6 } };

    private readonly struct InstSpec
    {
        public readonly float R0, R1, R2, R3, A0, A1, A2, A3, Attack, Release, Decay, Noise, Vibrato, Glide, Hp;
        public readonly Stem Stem;
        public InstSpec(Stem stem, float r0, float r1, float r2, float r3, float a0, float a1, float a2, float a3, float attack, float release, float decay, float noise, float vib, float glide, float hp)
        {
            Stem = stem; R0 = r0; R1 = r1; R2 = r2; R3 = r3; A0 = a0; A1 = a1; A2 = a2; A3 = a3; Attack = attack; Release = release; Decay = decay; Noise = noise; Vibrato = vib; Glide = glide; Hp = hp;
        }
    }

    // decay > 0 = 타악 엔벨로프(어택 후 지수 감쇠 · 게이트 무시)
    private static readonly InstSpec[] Specs =
    {
        new(Stem.Strings, 1f, 2f, 3f, 4f, 1f, 0.45f, 0.28f, 0.15f, 0.35f, 0.6f, 0f, 0f, 0.004f, 0f, 0f),
        new(Stem.Strings, 1f, 2f, 3f, 4f, 1f, 0.5f, 0.3f, 0.18f, 0.004f, 0.08f, 0.16f, 0f, 0f, 0f, 0f),
        new(Stem.Brass, 1f, 2f, 3f, 4f, 1f, 0.7f, 0.5f, 0.35f, 0.08f, 0.3f, 0f, 0f, 0.002f, 0f, 0f),
        new(Stem.Brass, 1f, 2f, 3f, 4f, 1f, 0.75f, 0.55f, 0.4f, 0.015f, 0.2f, 0.4f, 0f, 0f, 0f, 0f),
        new(Stem.Woodwinds, 1f, 2f, 3f, 4f, 1f, 0.15f, 0.06f, 0f, 0.07f, 0.15f, 0f, 0.03f, 0.006f, 0f, 0.95f),
        new(Stem.Percussion, 1f, 1.5f, 1.99f, 2.44f, 1f, 0.5f, 0.35f, 0.2f, 0.003f, 0.2f, 0.9f, 0.05f, 0f, -0.03f, 0.9f),
        new(Stem.Percussion, 1f, 1.6f, 2.3f, 3.1f, 1f, 0.3f, 0.1f, 0.05f, 0.002f, 0.1f, 0.25f, 0.08f, 0f, -2.5f, 0.9f),
        new(Stem.Percussion, 1f, 1.5f, 0f, 0f, 0.3f, 0.15f, 0f, 0f, 0.001f, 0.05f, 0.14f, 1f, 0f, 0f, 0.6f),
        new(Stem.Percussion, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0.004f, 0.3f, 1.8f, 1f, 0f, 0f, 0.15f),
        new(Stem.Ambient, 1f, 2f, 3f, 0.5f, 1f, 0.3f, 0.12f, 0.25f, 2.0f, 2.5f, 0f, 0f, 0.002f, 0f, 0f),
    };

    private readonly object _gate = new();
    // ── 보이스 SoA ──
    private readonly byte[] _vInst = new byte[MaxVoices], _vState = new byte[MaxVoices]; // state: 0 비어 있음 · 1 게이트 · 2 릴리스 · 3 타악
    private readonly float[] _vFreq = new float[MaxVoices], _vAmp = new float[MaxVoices], _vEnv = new float[MaxVoices], _vAge = new float[MaxVoices];
    private readonly float[] _vPh = new float[MaxVoices * Partials], _vHp = new float[MaxVoices], _vPrev = new float[MaxVoices], _vSwell = new float[MaxVoices];
    private readonly int[] _vDelay = new int[MaxVoices], _vMidi = new int[MaxVoices];
    private readonly uint[] _vRng = new uint[MaxVoices];
    // ── 블록 버퍼 ──
    private readonly float[] _tmp = new float[Block], _voice = new float[Block], _env = new float[Block];
    private readonly float[][] _stems = new float[MusicStems][];
    private readonly float[] _stemRms = new float[MusicStems], _stemPeak = new float[MusicStems];
    public readonly float[] StemGain = { 1f, 1f, 1f, 1f, 1f };
    private readonly int[] _chordMidi = new int[4], _prevChord = new int[4];
    private ulong _rng;
    private double _beat, _sampleClock;
    private float _bpm = 80f, _bpmTarget = 80f, _bpmFrom = 80f, _valence = 0.3f, _arousal = 0.3f, _tension = 0.2f, _actionTempo;
    private int _root = 50, _barIndex = -1, _step = -1, _melodyDeg = 9, _chordDeg;
    private ScoreMode _mode = ScoreMode.Major;
    private bool _stingerPending, _stingerHard;
    private double _cutSample;

    public NeuralCinematographicFilmScoreGenerator(ulong seed = 0x1686_5C0E)
    {
        _rng = seed | 1;
        for (var s = 0; s < MusicStems; s++) _stems[s] = new float[Block];
        for (var v = 0; v < MaxVoices; v++) _vRng[v] = (uint)(0x9E3779B9u * (v + 1));
    }

    // ── 입력 ──
    public float Valence => _valence;
    public float Arousal => _arousal;
    public float Tension => _tension;
    public float ActionTempo => _actionTempo;

    // ── 상태/계측 ──
    public float Bpm => _bpm;
    public ScoreMode Mode => _mode;
    public int KeyRoot => _root;
    public int ChordDegree => _chordDeg;
    public int Bar => Math.Max(_barIndex, 0);
    public double BeatPosition => _beat;
    public int ActiveVoices { get; private set; }
    public long Stingers { get; private set; }
    public float LastCutLatencyMs { get; private set; }
    public long NotesOn { get; private set; }
    public long VoiceSteals { get; private set; }
    public double LastRenderMs { get; private set; }
    public long Blocks { get; private set; }
    public bool LayerStrings => _arousal > 0.1f;
    public bool LayerWoodwinds => _arousal > 0.2f && _arousal < 0.8f;
    public bool LayerBrass => _arousal > 0.55f || _tension > 0.65f;
    public bool LayerPercussion => _arousal > 0.35f;
    public bool LayerOstinato => _arousal > 0.6f;
    public ReadOnlySpan<float> StemBuffer(Stem s) => _stems[(int)s];
    public float StemRms(Stem s) => _stemRms[(int)s];
    public float StemPeak(Stem s) => _stemPeak[(int)s];
    public ReadOnlySpan<int> ChordNotes => _chordMidi;
    public int VoiceMidi(int v) => _vState[v] == 0 ? -1 : _vMidi[v];
    public Instrument VoiceInstrument(int v) => (Instrument)_vInst[v];
    /// <summary>게이트 유지(릴리스 꼬리 아님) 또는 방금 시작(≤ 0.1 s) 보이스.</summary>
    public bool VoiceCurrent(int v) => _vState[v] == 1 || (_vState[v] == 3 && _vAge[v] <= 0.1f);

    public static string ModeName(ScoreMode m) => m switch { ScoreMode.Dorian => "도리안", ScoreMode.Minor => "단조", ScoreMode.Phrygian => "프리지안", _ => "장조" };
    public static string NoteName(int midi) => NoteNames[((midi % 12) + 12) % 12];
    public static ReadOnlySpan<int> ScaleOf(ScoreMode m) => Scales[(int)m];
    public string ChordName
    {
        get
        {
            var sc = Scales[(int)_mode];
            var third = (sc[(_chordDeg + 2) % 7] - sc[_chordDeg] + 12) % 12;
            return NoteName(_root + sc[_chordDeg]) + (third == 3 ? "m" : "");
        }
    }
    public static float MidiToHz(int midi) => 440f * MathF.Pow(2f, (midi - 69) / 12f);

    /// <summary>서사 감정선 큐(정서가 −1..1 · 각성 0..1 · 긴장 0..1 · 액션 템포 BPM 0 = 자동).</summary>
    public void SetCue(float valence, float arousal, float tension, float actionTempo = 0f)
    {
        lock (_gate)
        {
            _valence = Math.Clamp(valence, -1f, 1f); _arousal = Math.Clamp(arousal, 0f, 1f); _tension = Math.Clamp(tension, 0f, 1f);
            _actionTempo = actionTempo <= 0f ? 0f : Math.Clamp(actionTempo, 50f, 180f);
        }
    }

    /// <summary>컷 전환 → 스팅어(하드 컷 = 다음 블록 즉시 · 기본 = 다음 박).</summary>
    public void Cut(bool hard = false) { lock (_gate) { _stingerPending = true; _stingerHard |= hard; _cutSample = _sampleClock; } }

    private float Next01()
    {
        _rng ^= _rng >> 12; _rng ^= _rng << 25; _rng ^= _rng >> 27;
        return ((_rng * 0x2545F4914F6CDD1DUL) >> 40) * (1f / (1 << 24));
    }

    private ScoreMode ChooseMode()
    {
        if (_valence > 0.15f) return ScoreMode.Major;
        if (_valence > -0.15f) return ScoreMode.Dorian;
        return _tension > 0.7f ? ScoreMode.Phrygian : ScoreMode.Minor;
    }

    /// <summary>음계 도수(옥타브 넘침 허용) → MIDI.</summary>
    private int DegreeToMidi(int deg, int baseOct)
    {
        var sc = Scales[(int)_mode];
        var o = (int)MathF.Floor(deg / 7f);
        return _root + baseOct * 12 + o * 12 + sc[deg - o * 7];
    }

    // ══════════════════════ 보이스 ══════════════════════

    private int Alloc()
    {
        for (var v = 0; v < MaxVoices; v++) if (_vState[v] == 0) return v;
        // 스틸: 가장 조용한 릴리스/타악 보이스
        int best = -1; var bestEnv = float.MaxValue;
        for (var v = 0; v < MaxVoices; v++) if (_vState[v] != 1 && _vEnv[v] < bestEnv) { bestEnv = _vEnv[v]; best = v; }
        if (best < 0) for (var v = 0; v < MaxVoices; v++) if (_vEnv[v] < bestEnv) { bestEnv = _vEnv[v]; best = v; }
        VoiceSteals++;
        return best;
    }

    private int NoteOn(Instrument inst, int midi, float amp, int offset, float swell = 0f)
    {
        var v = Alloc();
        _vInst[v] = (byte)inst; _vMidi[v] = midi; _vFreq[v] = inst == Instrument.Cymbal ? 1f : MidiToHz(midi);
        _vAmp[v] = amp; _vEnv[v] = 0f; _vAge[v] = 0f; _vDelay[v] = Math.Clamp(offset, 0, Block - 1); _vSwell[v] = swell;
        _vState[v] = (byte)(Specs[(int)inst].Decay > 0f ? 3 : 1);
        for (var p = 0; p < Partials; p++) _vPh[v * Partials + p] = Next01();
        _vHp[v] = 0f; _vPrev[v] = 0f;
        NotesOn++;
        return v;
    }

    private void ReleaseWhere(Instrument inst)
    {
        for (var v = 0; v < MaxVoices; v++) if (_vState[v] == 1 && _vInst[v] == (byte)inst) _vState[v] = 2;
    }

    /// <summary>보이스 1블록 렌더(엔벨로프 = 샘플 스칼라 재귀 · 배음/누적 SIMD).</summary>
    private void RenderVoice(int v)
    {
        ref readonly var sp = ref Specs[_vInst[v]];
        const float dt = 1f / SampleRate;
        var delay = _vDelay[v]; _vDelay[v] = 0;
        var e = _vEnv[v]; var st = _vState[v]; var age = _vAge[v];
        float attStep = dt / MathF.Max(sp.Attack + _vSwell[v], 1e-4f), relK = MathF.Exp(-dt / sp.Release), decK = sp.Decay > 0f ? MathF.Exp(-dt / sp.Decay) : 1f;
        var tgt = _vAmp[v];
        for (var i = 0; i < delay; i++) _env[i] = 0f;
        for (var i = delay; i < Block; i++)
        {
            if (st == 2) e *= relK;
            else if (age < sp.Attack + _vSwell[v]) e = MathF.Min(tgt, e + tgt * attStep);
            else if (st == 3) e *= decK;
            else e = tgt;
            age += dt;
            _env[i] = e;
        }
        _vEnv[v] = e; _vAge[v] = age;
        if ((st == 2 || st == 3) && e < 1e-4f) { _vState[v] = 0; }

        var f = _vFreq[v];
        if (sp.Glide != 0f) { f *= MathF.Exp(sp.Glide * Block * dt); _vFreq[v] = MathF.Max(f, 20f); }
        var vib = sp.Vibrato > 0f ? 1f + sp.Vibrato * MathF.Sin(MathF.Tau * 5.3f * (float)(_sampleClock * dt) + v) * MathF.Min(1f, age * 2f) : 1f;
        var buf = _voice.AsSpan(); buf.Clear();
        if (sp.A0 > 0f) Partial(v, 0, f * sp.R0 * vib, sp.A0, buf);
        if (sp.A1 > 0f) Partial(v, 1, f * sp.R1 * vib, sp.A1, buf);
        if (sp.A2 > 0f) Partial(v, 2, f * sp.R2 * vib, sp.A2, buf);
        if (sp.A3 > 0f) Partial(v, 3, f * sp.R3 * vib, sp.A3, buf);
        if (sp.Noise > 0f)
        {
            uint r = _vRng[v]; float hp = _vHp[v], prev = _vPrev[v], a = sp.Hp;
            for (var i = 0; i < Block; i++)
            {
                r ^= r << 13; r ^= r >> 17; r ^= r << 5;
                var x = (r * (1f / uint.MaxValue)) * 2f - 1f;
                hp = a * (hp + x - prev); prev = x;
                buf[i] += hp * sp.Noise;
            }
            _vRng[v] = r; _vHp[v] = hp; _vPrev[v] = prev;
        }
        AudioSimd.MulAcc(_stems[(int)sp.Stem], buf, _env);
    }

    private void Partial(int v, int p, float hz, float amp, Span<float> buf)
    {
        if (hz >= SampleRate * 0.45f) return;
        var inc = hz / SampleRate; ref var ph = ref _vPh[v * Partials + p];
        AudioSimd.PhaseRamp(_tmp, ph, inc);
        AudioSimd.SinePoly(_tmp);
        AudioSimd.MulAdd(buf, _tmp, amp);
        var nx = ph + inc * Block; ph = nx - MathF.Floor(nx);
    }

    // ══════════════════════ 편곡 ══════════════════════

    /// <summary>현 4성부: 새 화음 구성음(근·3·5·7/8) → 이전 성부 최근접 옥타브(최소 이동).</summary>
    private void VoiceLead(int deg)
    {
        var sc = Scales[(int)_mode];
        Span<int> pcs = stackalloc int[4];
        pcs[0] = sc[deg % 7]; pcs[1] = sc[(deg + 2) % 7]; pcs[2] = sc[(deg + 4) % 7]; pcs[3] = _arousal < 0.35f ? sc[(deg + 6) % 7] : sc[deg % 7];
        _chordMidi.CopyTo(_prevChord, 0);
        for (var k = 0; k < 4; k++)
        {
            var target = _prevChord[k] == 0 ? 55 + k * 5 : _prevChord[k];
            var best = 0; var bd = int.MaxValue;
            for (var o = 3; o <= 6; o++)
            {
                var m = _root % 12 + pcs[k] + o * 12;
                var d = Math.Abs(m - target);
                if (d < bd && m >= 48 + k * 4 && m <= 76 + k * 2) { bd = d; best = m; }
            }
            _chordMidi[k] = best;
        }
    }

    private void OnBar(int offset)
    {
        _barIndex++;
        var mode = ChooseMode();
        if (mode != _mode)
        {
            // 관계조 전조(장 ↔ 단: 근음 ±3) → 공통음 유지
            var wasMajor = _mode == ScoreMode.Major; var isMajor = mode == ScoreMode.Major;
            if (wasMajor && !isMajor) _root -= 3; else if (!wasMajor && isMajor) _root += 3;
            if (_root < 45) _root += 12; else if (_root > 56) _root -= 12;
            _mode = mode;
        }
        _bpmFrom = _bpm;
        _bpmTarget = _actionTempo > 0f ? _actionTempo : 60f + 80f * _arousal + 20f * _tension;
        var prog = Progressions[(int)_mode];
        var slow = _arousal < 0.3f ? 2 : 1;
        if (_barIndex % slow == 0)
        {
            _chordDeg = prog[(_barIndex / slow) % prog.Length];
            VoiceLead(_chordDeg);
            ReleaseWhere(Instrument.Pad); ReleaseWhere(Instrument.StringLong);
            var padRoot = DegreeToMidi(_chordDeg, -1);
            NoteOn(Instrument.Pad, padRoot, 0.10f, offset);
            NoteOn(Instrument.Pad, padRoot + 7, 0.07f, offset);
            if (LayerStrings)
                for (var k = 0; k < 4; k++) NoteOn(Instrument.StringLong, _chordMidi[k], (0.05f + 0.05f * _arousal) * (k == 0 ? 1.2f : 1f), offset);
            if (_tension > 0.65f) NoteOn(Instrument.Brass, _chordMidi[0] - 12, 0.12f * _tension, offset, 60f / _bpm * 3f);
        }
    }

    /// <summary>8분음 그리드 이벤트(마디 내 0..7).</summary>
    private void OnStep(int step, int offset)
    {
        if (step == 0) OnBar(offset);
        var a = _arousal;
        var strong = step == 0 || step == 4;
        if (LayerPercussion)
        {
            if (step == 0) NoteOn(Instrument.Timpani, DegreeToMidi(_chordDeg, -2), 0.35f + 0.35f * a, offset);
            if (a > 0.6f && (step & 1) == 0) NoteOn(Instrument.Drum, 40, 0.3f + 0.3f * a, offset);
            if (a > 0.75f && (step == 2 || step == 6)) NoteOn(Instrument.Snare, 62, 0.25f * a, offset);
        }
        if (LayerOstinato) NoteOn(Instrument.StringShort, DegreeToMidi(_chordDeg, -1) + ((step & 1) == 1 && step % 4 == 3 ? 7 : 0), 0.08f + 0.06f * a, offset);
        if (LayerBrass && a > 0.55f && strong)
        {
            for (var k = 0; k < 3; k++) NoteOn(Instrument.BrassStab, _chordMidi[k] - 12 + (k == 0 ? 0 : 12), 0.06f + 0.08f * a, offset);
        }
        if (LayerWoodwinds)
        {
            var density = 0.25f + 0.6f * a;
            if (strong || Next01() < density)
            {
                ReleaseWhere(Instrument.Flute);
                if (strong)
                {
                    // 강박: 현재 선율 도수에서 가장 가까운 화성음
                    var best = _melodyDeg; var bd = int.MaxValue;
                    for (var d = _melodyDeg - 3; d <= _melodyDeg + 3; d++)
                    {
                        var r = ((d - _chordDeg) % 7 + 7) % 7;
                        if ((r == 0 || r == 2 || r == 4) && Math.Abs(d - _melodyDeg) < bd) { bd = Math.Abs(d - _melodyDeg); best = d; }
                    }
                    _melodyDeg = best;
                }
                else _melodyDeg += Next01() < 0.5f ? -1 : (Next01() < 0.3f ? 2 : 1);
                _melodyDeg = Math.Clamp(_melodyDeg, 7, 18);
                NoteOn(Instrument.Flute, DegreeToMidi(_melodyDeg, 1), 0.07f + 0.04f * a, offset);
            }
        }
    }

    private void Stinger(int offset)
    {
        NoteOn(Instrument.Cymbal, 0, 0.35f, offset);
        NoteOn(Instrument.Timpani, DegreeToMidi(_chordDeg, -2), 0.8f, offset);
        for (var k = 0; k < 3; k++) NoteOn(Instrument.BrassStab, _chordMidi[k], 0.12f, offset);
        Stingers++;
        LastCutLatencyMs = (float)((_sampleClock + offset - _cutSample) * 1000.0 / SampleRate);
        _stingerPending = false; _stingerHard = false;
    }

    /// <summary>1블록(800 샘플) 렌더(무할당): 그리드 이벤트(샘플 정확) → 보이스 → 스템 페이더 · 계측.</summary>
    public void RenderBlock()
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            for (var s = 0; s < MusicStems; s++) _stems[s].AsSpan().Clear();
            // 템포 램프(마디 진행률) → 8분음 경계 탐색
            var barPhase = (float)(_beat / 4.0 - Math.Floor(_beat / 4.0));
            _bpm = _bpmFrom + (_bpmTarget - _bpmFrom) * barPhase;
            var beatsPerSample = _bpm / 60.0 / SampleRate;
            if (_stingerPending && _stingerHard) Stinger(0);
            var b0 = _beat; var b1 = _beat + beatsPerSample * Block;
            var firstStep = (long)Math.Ceiling(b0 * 2.0 - 1e-9);
            for (var st = firstStep; st < b1 * 2.0; st++)
            {
                var off = (int)Math.Round((st * 0.5 - b0) / beatsPerSample);
                var step = (int)(st % 8);
                if (st != _step) { _step = (int)st; OnStep(step, off); if (_stingerPending && (st & 1) == 0) Stinger(off); }
            }
            _beat = b1;
            var active = 0;
            for (var v = 0; v < MaxVoices; v++) if (_vState[v] != 0) { RenderVoice(v); active++; }
            ActiveVoices = active;
            for (var s = 0; s < MusicStems; s++)
            {
                var g = StemGain[s];
                var buf = _stems[s].AsSpan();
                if (g != 1f) for (var i = 0; i < Block; i++) buf[i] *= g;
                _stemRms[s] = MathF.Sqrt(AudioSimd.SumSquares(buf) / Block);
                var pk = 0f; for (var i = 0; i < Block; i++) pk = MathF.Max(pk, MathF.Abs(buf[i]));
                _stemPeak[s] = pk;
            }
            _sampleClock += Block;
            Blocks++;
            LastRenderMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
    }
}
