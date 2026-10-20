using System.Numerics;
using System.Runtime.InteropServices;

namespace PromptFlowStudio.Services.MasterControl;

public enum SourceKind : byte { MoCo, Drone, Volumetric, Replay, Bars, Black }
public enum TransitionKind : byte { Cut, Mix, WipeH, WipeV, WipeCircle, WipeDiamond }
public enum ArenaEventKind : byte { Goal, Collision, Drama, Manual }

/// <summary>
/// [PromptFlow Studio v17.05.0] 스위처 SIMD 커널 — 8비트 정수 혼합(스칼라 쌍둥이와 비트 동일).
/// d = (a·(256−k) + b·k) &gt;&gt; 8 (k ∈ 0..256 · k = 0 → a, k = 256 → b 정확), 와이프 키 k = clamp((pos − 패턴)·4, 0, 256).
/// </summary>
public static class SwitchSimd
{
    public static void Mix(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> d, int k)
    {
        int i = 0, n = d.Length;
        if (Vector.IsHardwareAccelerated && n >= Vector<byte>.Count)
        {
            var vk = new Vector<ushort>((ushort)k);
            var vi = new Vector<ushort>((ushort)(256 - k));
            for (; i <= n - Vector<byte>.Count; i += Vector<byte>.Count)
            {
                Vector.Widen(new Vector<byte>(a.Slice(i)), out var al, out var ah);
                Vector.Widen(new Vector<byte>(b.Slice(i)), out var bl, out var bh);
                Vector.Narrow(Vector.ShiftRightLogical(al * vi + bl * vk, 8), Vector.ShiftRightLogical(ah * vi + bh * vk, 8)).CopyTo(d.Slice(i));
            }
        }
        for (; i < n; i++) d[i] = (byte)((a[i] * (256 - k) + b[i] * k) >> 8);
    }

    public static void MixScalar(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> d, int k)
    {
        for (var i = 0; i < d.Length; i++) d[i] = (byte)((a[i] * (256 - k) + b[i] * k) >> 8);
    }

    /// <summary>요소별 키(0..256) 혼합 — 와이프/키어.</summary>
    public static void MixKeyed(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<short> k, Span<byte> d)
    {
        int i = 0, n = d.Length;
        var ku = MemoryMarshal.Cast<short, ushort>(k);
        if (Vector.IsHardwareAccelerated && n >= Vector<byte>.Count)
        {
            var c = new Vector<ushort>(256);
            for (; i <= n - Vector<byte>.Count; i += Vector<byte>.Count)
            {
                Vector.Widen(new Vector<byte>(a.Slice(i)), out var al, out var ah);
                Vector.Widen(new Vector<byte>(b.Slice(i)), out var bl, out var bh);
                var kl = new Vector<ushort>(ku.Slice(i));
                var kh = new Vector<ushort>(ku.Slice(i + Vector<ushort>.Count));
                Vector.Narrow(Vector.ShiftRightLogical(al * (c - kl) + bl * kl, 8), Vector.ShiftRightLogical(ah * (c - kh) + bh * kh, 8)).CopyTo(d.Slice(i));
            }
        }
        for (; i < n; i++) d[i] = (byte)((a[i] * (256 - ku[i]) + b[i] * ku[i]) >> 8);
    }

    public static void MixKeyedScalar(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<short> k, Span<byte> d)
    {
        for (var i = 0; i < d.Length; i++) d[i] = (byte)((a[i] * (256 - k[i]) + b[i] * k[i]) >> 8);
    }

    /// <summary>와이프 패턴(0..1023) + 진행 위치 → 소프트 엣지(64 단계) 키.</summary>
    public static void WipeKey(ReadOnlySpan<short> pattern, Span<short> k, int pos)
    {
        int i = 0, n = k.Length;
        if (Vector.IsHardwareAccelerated && n >= Vector<short>.Count)
        {
            var vp = new Vector<short>((short)pos);
            var v4 = new Vector<short>(4);
            var vmax = new Vector<short>(256);
            for (; i <= n - Vector<short>.Count; i += Vector<short>.Count)
                Vector.Min(Vector.Max((vp - new Vector<short>(pattern.Slice(i))) * v4, Vector<short>.Zero), vmax).CopyTo(k.Slice(i));
        }
        for (; i < n; i++) k[i] = (short)Math.Clamp((pos - pattern[i]) * 4, 0, 256);
    }

    public static void WipeKeyScalar(ReadOnlySpan<short> pattern, Span<short> k, int pos)
    {
        for (var i = 0; i < k.Length; i++) k[i] = (short)Math.Clamp((pos - pattern[i]) * 4, 0, 256);
    }
}

/// <summary>
/// [PromptFlow Studio v17.05.0] 방송 소스용 아레나 경기(10 선수 + 공 · 결정적 xorshift) — 골/충돌/결정적 찬스 이벤트 링(64).
/// </summary>
public sealed class BroadcastArenaScene
{
    public const float FieldW = 100f, FieldH = 64f, GoalHalf = 4f;
    public const int Players = 10, Ball = Players, EventRing = 64;

    private readonly float[] _x = new float[Players + 1], _z = new float[Players + 1], _vx = new float[Players + 1], _vz = new float[Players + 1];
    private readonly long[] _evFrame = new long[EventRing];
    private readonly byte[] _evKind = new byte[EventRing];
    private readonly float[] _evX = new float[EventRing], _evZ = new float[EventRing], _evScore = new float[EventRing];
    private ulong _rng;
    private float _kickCd, _collCd, _dramaCd;

    public BroadcastArenaScene(ulong seed = 0x2545F4914F6CDD1DUL) => Reset(seed);

    public long Frame { get; private set; }
    public long EventCount { get; private set; }
    public int Score0 { get; private set; }
    public int Score1 { get; private set; }
    public float Excitement { get; private set; }
    public int LastTouch { get; private set; } = -1;
    public float X(int i) => _x[i];
    public float Z(int i) => _z[i];
    public float Vx(int i) => _vx[i];
    public float Vz(int i) => _vz[i];
    public static int Team(int i) => i < Players / 2 ? 0 : 1;

    public long EventFrame(long n) => _evFrame[n % EventRing];
    public ArenaEventKind EventKind(long n) => (ArenaEventKind)_evKind[n % EventRing];
    public float EventX(long n) => _evX[n % EventRing];
    public float EventZ(long n) => _evZ[n % EventRing];
    public float EventScore(long n) => _evScore[n % EventRing];

    public void Reset(ulong seed)
    {
        _rng = seed | 1; Frame = 0; EventCount = 0; Score0 = Score1 = 0; Excitement = 0;
        for (var i = 0; i < Players; i++) { HomeSpot(i, out _x[i], out _z[i]); _vx[i] = _vz[i] = 0; }
        Kickoff();
    }

    private void Kickoff() { _x[Ball] = FieldW / 2; _z[Ball] = FieldH / 2; _vx[Ball] = (Next01() - 0.5f) * 10f; _vz[Ball] = (Next01() - 0.5f) * 10f; _kickCd = 0.3f; }

    private static void HomeSpot(int i, out float x, out float z)
    {
        var t = Team(i); var k = i % (Players / 2);
        x = t == 0 ? 20f + (k % 3) * 14f : 80f - (k % 3) * 14f;
        z = 10f + k * 11f;
    }

    private float Next01()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17;
        return (_rng >> 40) / 16777216f;
    }

    /// <summary>하이라이트 이벤트 기록(수동 마크 포함).</summary>
    public void Emit(ArenaEventKind kind, float x, float z, float score)
    {
        var s = (int)(EventCount % EventRing);
        _evFrame[s] = Frame; _evKind[s] = (byte)kind; _evX[s] = x; _evZ[s] = z; _evScore[s] = score;
        EventCount++;
        Excitement = MathF.Min(1f, Excitement + score);
    }

    public void Step(float dt)
    {
        Frame++;
        Excitement *= MathF.Exp(-0.6f * dt);
        _kickCd -= dt; _collCd -= dt; _dramaCd -= dt;
        float bx = _x[Ball], bz = _z[Ball];
        // 팀별 최근접 선수 → 공 추격, 나머지 → 공 쪽으로 이동한 포메이션 지점.
        int c0 = -1, c1 = -1; float d0 = float.MaxValue, d1 = float.MaxValue;
        for (var i = 0; i < Players; i++)
        {
            var d = (_x[i] - bx) * (_x[i] - bx) + (_z[i] - bz) * (_z[i] - bz);
            if (Team(i) == 0) { if (d < d0) { d0 = d; c0 = i; } } else if (d < d1) { d1 = d; c1 = i; }
        }
        for (var i = 0; i < Players; i++)
        {
            float tx, tz, sp;
            if (i == c0 || i == c1) { tx = bx; tz = bz; sp = 7.5f; }
            else { HomeSpot(i, out tx, out tz); tx += (bx - FieldW / 2) * 0.35f; tz += (bz - tz) * 0.25f; sp = 4.5f; }
            float dx = tx - _x[i], dz = tz - _z[i], l = MathF.Sqrt(dx * dx + dz * dz);
            float wx = l > 0.3f ? dx / l * sp : 0, wz = l > 0.3f ? dz / l * sp : 0;
            _vx[i] += (wx - _vx[i]) * MathF.Min(1f, 4f * dt); _vz[i] += (wz - _vz[i]) * MathF.Min(1f, 4f * dt);
            _x[i] = Math.Clamp(_x[i] + _vx[i] * dt, 0f, FieldW); _z[i] = Math.Clamp(_z[i] + _vz[i] * dt, 0f, FieldH);
        }
        // 공 접촉 → 상대 골문 방향 패스/슛.
        if (_kickCd <= 0)
            for (var i = 0; i < Players; i++)
            {
                float dx = _x[i] - bx, dz = _z[i] - bz;
                if (dx * dx + dz * dz > 1.1f) continue;
                var gx = Team(i) == 0 ? FieldW : 0f;
                var dist = MathF.Abs(gx - bx);
                var shot = dist < 30f;
                var tz = FieldH / 2 + (Next01() - 0.5f) * (shot ? 13f : 40f);
                var tx = shot ? gx : bx + (gx - bx) * (0.3f + 0.3f * Next01());
                float kx = tx - bx, kz = tz - bz, kl = MathF.Max(0.1f, MathF.Sqrt(kx * kx + kz * kz));
                var v = shot ? 22f + 6f * Next01() : 11f + 6f * Next01();
                _vx[Ball] = kx / kl * v; _vz[Ball] = kz / kl * v;
                _kickCd = 0.5f; LastTouch = i;
                if (shot && dist < 24f && _dramaCd <= 0) { Emit(ArenaEventKind.Drama, bx, bz, 0.55f); _dramaCd = 2.5f; }
                break;
            }
        // 공 이동 · 마찰 · 사이드 반사 · 골.
        var f = MathF.Exp(-0.7f * dt);
        _vx[Ball] *= f; _vz[Ball] *= f;
        bx += _vx[Ball] * dt; bz += _vz[Ball] * dt;
        if (bz < 0) { bz = -bz; _vz[Ball] = -_vz[Ball]; } else if (bz > FieldH) { bz = 2 * FieldH - bz; _vz[Ball] = -_vz[Ball]; }
        if (bx < 0 || bx > FieldW)
        {
            if (MathF.Abs(bz - FieldH / 2) < GoalHalf)
            {
                if (bx > FieldW) Score0++; else Score1++;
                Emit(ArenaEventKind.Goal, bx < 0 ? 0f : FieldW, bz, 1f);
                Kickoff();
                return;
            }
            bx = bx < 0 ? -bx : 2 * FieldW - bx; _vx[Ball] = -_vx[Ball];
        }
        _x[Ball] = bx; _z[Ball] = bz;
        // 상대 선수 고속 충돌.
        if (_collCd <= 0)
            for (var i = 0; i < Players / 2; i++)
                for (var j = Players / 2; j < Players; j++)
                {
                    float dx = _x[i] - _x[j], dz = _z[i] - _z[j];
                    if (dx * dx + dz * dz > 1f) continue;
                    float rx = _vx[i] - _vx[j], rz = _vz[i] - _vz[j];
                    if (rx * rx + rz * rz < 36f) continue;
                    Emit(ArenaEventKind.Collision, 0.5f * (_x[i] + _x[j]), 0.5f * (_z[i] + _z[j]), 0.5f);
                    _vx[i] = -_vx[i]; _vz[i] = -_vz[i]; _vx[j] = -_vx[j]; _vz[j] = -_vz[j];
                    _collCd = 3f;
                    return;
                }
    }
}

/// <summary>
/// [PromptFlow Studio v17.05.0] 📺 실시간 방송급 비전 스위처 — 16 입력(MoCo 6 · 가상 드론 4 · 볼류메트릭 3 · 리플레이 · 컬러바 · 블랙).
/// 고정 메모리 맵(핀 고정 단일 byte[]: 입력 16 + PGM + PVW + 멀티뷰어 + 블랙) 위에서 PGM/PVW 버스 컷 · 디졸브 · 와이프(가로/세로/원/다이아몬드),
/// 자동 트랜지션(프레임 레이트) · T-바 수동 · FTB · 같은 틱 입력 → PGM 합성(지연 0 프레임) · 프레임 싱크(입력 결손 시 직전 프레임 유지) ·
/// AFV 프로그램 오디오(48 kHz 스테레오 · 800 샘플/프레임) · 4×4 멀티뷰어 · 탤리. 전 경로 무할당.
/// </summary>
public sealed class RealtimeBroadcastVisionSwitcherEngine
{
    public const int Inputs = 16, ReplayInput = 13, BarsInput = 14, BlackInput = 15, MvCols = 4;
    public const int SampleRate = 48000, AudioBlock = 800, WipeSoft = 64, WipeRange = 1023;
    public static readonly string[] Names = { "CAM1", "CAM2", "CAM3", "CAM4", "CAM5", "CAM6", "DRN1", "DRN2", "DRN3", "DRN4", "VOL1", "VOL2", "VOL3", "RPL", "BARS", "BLK" };

    private readonly byte[] _map;
    private readonly short[][] _patterns = new short[4][];
    private readonly short[] _key;
    private readonly float[] _camX = new float[Inputs], _camZ = new float[Inputs], _camS = new float[Inputs], _camR = new float[Inputs], _phase = new float[Inputs];
    private readonly long[] _lastFrame = new long[Inputs];
    private readonly float[] _audio = new float[AudioBlock * 2];
    private ulong _noise = 0x853C49E6748FEA9BUL;
    private float _t, _ftb;
    private bool _auto, _tbarFlip, _ftbOn;
    private long _frame;

    public RealtimeBroadcastVisionSwitcherEngine(int width = 128, int height = 72)
    {
        if (width % 8 != 0 || height % 4 != 0) throw new ArgumentException("width % 8 == 0, height % 4 == 0");
        Width = width; Height = height; FrameBytes = width * height * 4;
        _map = GC.AllocateArray<byte>(FrameBytes * (Inputs + 4), pinned: true);
        _key = new short[FrameBytes];
        for (var p = 0; p < 4; p++) _patterns[p] = BuildPattern((TransitionKind)(p + 2));
        for (var i = 0; i < Inputs; i++)
        {
            Kind[i] = i < 6 ? SourceKind.MoCo : i < 10 ? SourceKind.Drone : i < 13 ? SourceKind.Volumetric : i == ReplayInput ? SourceKind.Replay : i == BarsInput ? SourceKind.Bars : SourceKind.Black;
            _camX[i] = BroadcastArenaScene.FieldW / 2; _camZ[i] = BroadcastArenaScene.FieldH / 2; _camS[i] = 0.6f;
        }
        for (var i = 0; i < Inputs + 4; i++) FillBlack(_map.AsSpan(i * FrameBytes, FrameBytes));
        RenderBars(Input(BarsInput), Width, Height);
        Pgm = 0; Pvw = 6;
    }

    public int Width { get; }
    public int Height { get; }
    public int FrameBytes { get; }
    public SourceKind[] Kind { get; } = new SourceKind[Inputs];
    /// <summary>입력 신호 결손 모사(프레임 미도착 → 프레임 싱크가 직전 프레임 유지).</summary>
    public bool[] InputLost { get; } = new bool[Inputs];
    /// <summary>입력 블랙 모사(렌즈 캡 · 인코더 블랙).</summary>
    public bool[] InputBlack { get; } = new bool[Inputs];
    public int Pgm { get; private set; }
    public int Pvw { get; private set; }
    public TransitionKind Transition { get; set; } = TransitionKind.Mix;
    public int RateFrames { get; set; } = 30;
    public bool InTransition { get; private set; }
    public float Progress => _t;
    public bool TBarFlipped => _tbarFlip;
    public bool FadeToBlack => _ftbOn;
    public float FtbLevel => _ftb;
    public long Frame => _frame;
    public long Cuts { get; private set; }
    public long Transitions { get; private set; }
    public long EmergencyCuts { get; private set; }
    public double RenderUs { get; private set; }
    public double CompositeUs { get; private set; }
    public double MultiviewUs { get; private set; }
    public float ProgramPeakDbfs { get; private set; } = -120f;
    /// <summary>입력 → PGM 지연(프레임) — 같은 틱 합성.</summary>
    public int LatencyFrames => 0;

    public Span<byte> Input(int i) => _map.AsSpan(i * FrameBytes, FrameBytes);
    public Span<byte> ProgramBus => _map.AsSpan(Inputs * FrameBytes, FrameBytes);
    public Span<byte> PreviewBus => _map.AsSpan((Inputs + 1) * FrameBytes, FrameBytes);
    public Span<byte> Multiviewer => _map.AsSpan((Inputs + 2) * FrameBytes, FrameBytes);
    private ReadOnlySpan<byte> Black => _map.AsSpan((Inputs + 3) * FrameBytes, FrameBytes);
    public ReadOnlySpan<float> ProgramAudio => _audio;
    public int MemoryMapBytes => _map.Length;
    public long InputAgeFrames(int i) => _frame - _lastFrame[i];

    /// <summary>탤리: 2 = PGM(빨강) · 1 = PVW(초록) · 3 = 트랜지션 중 양쪽 송출.</summary>
    public int Tally(int i) => i != Pgm && i != Pvw ? 0 : InTransition ? 3 : i == Pgm ? 2 : 1;

    private short[] BuildPattern(TransitionKind kind)
    {
        var p = new short[FrameBytes];
        float cx = (Width - 1) * 0.5f, cy = (Height - 1) * 0.5f, rmax = MathF.Sqrt(cx * cx + cy * cy);
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                var v = kind switch
                {
                    TransitionKind.WipeH => x / (float)(Width - 1),
                    TransitionKind.WipeV => y / (float)(Height - 1),
                    TransitionKind.WipeCircle => MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / rmax,
                    _ => 0.5f * (MathF.Abs(x - cx) / cx + MathF.Abs(y - cy) / cy),
                };
                var s = (short)Math.Clamp((int)(v * WipeRange + 0.5f), 0, WipeRange);
                var o = (y * Width + x) * 4;
                p[o] = p[o + 1] = p[o + 2] = p[o + 3] = s;
            }
        return p;
    }

    private static void FillBlack(Span<byte> f)
    {
        f.Clear();
        for (var i = 3; i < f.Length; i += 4) f[i] = 255;
    }

    /// <summary>SMPTE 75% 컬러바(7 바 + 하단 PLUGE).</summary>
    public static void RenderBars(Span<byte> f, int width, int height)
    {
        ReadOnlySpan<byte> bars = [191, 191, 191, 191, 191, 0, 0, 191, 191, 0, 191, 0, 191, 0, 191, 191, 0, 0, 0, 0, 191];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                int r, g, b;
                if (y < height * 3 / 4) { var k = x * 7 / width * 3; r = bars[k]; g = bars[k + 1]; b = bars[k + 2]; }
                else { var v = x < width / 4 ? 16 : x < width / 2 ? 235 : x < width * 3 / 4 ? 4 : 24; r = g = b = v; }
                f[o] = (byte)r; f[o + 1] = (byte)g; f[o + 2] = (byte)b; f[o + 3] = 255;
            }
    }

    // ── 버스 제어 ─────────────────────────────────────────────

    public void SetPreview(int i) { if ((uint)i < Inputs && !InTransition) Pvw = i; }

    /// <summary>PGM 직접 선택(핫 컷) — 이전 PGM은 PVW로.</summary>
    public void SetProgram(int i)
    {
        if ((uint)i >= Inputs || i == Pgm) return;
        Abort();
        Pgm = i; Cuts++;
    }

    /// <summary>CUT — PGM ↔ PVW 교환(다음 합성부터 즉시 송출 · 프레임 경계).</summary>
    public void Cut()
    {
        Abort();
        (Pgm, Pvw) = (Pvw, Pgm);
        Cuts++;
    }

    /// <summary>AUTO — 선택 트랜지션을 RateFrames 동안 진행.</summary>
    public void Auto()
    {
        if (Transition == TransitionKind.Cut) { Cut(); return; }
        if (InTransition) { _auto = true; return; }
        InTransition = true; _auto = true; _t = 0;
    }

    /// <summary>T-바 수동 위치(0..1 · 끝 도달 시 완료 후 방향 반전).</summary>
    public void SetTBar(float pos)
    {
        pos = Math.Clamp(pos, 0f, 1f);
        var t = _tbarFlip ? 1f - pos : pos;
        if (Transition == TransitionKind.Cut) { if (t >= 1f) { Cut(); _tbarFlip = !_tbarFlip; } return; }
        _auto = false;
        if (t <= 0f) { InTransition = false; _t = 0; return; }
        InTransition = true; _t = t;
        if (t >= 1f) { Complete(); _tbarFlip = !_tbarFlip; }
    }

    public void ToggleFtb() => _ftbOn = !_ftbOn;

    /// <summary>세이프 가드 비상 컷 — 진행 중 트랜지션 중단, 다음 합성부터 백업 소스 송출.</summary>
    public void EmergencyCut(int backup)
    {
        if ((uint)backup >= Inputs) return;
        Abort();
        if (Pvw == backup) Pvw = Pgm;
        Pgm = backup;
        EmergencyCuts++;
    }

    private void Abort() { InTransition = false; _auto = false; _t = 0; }

    private void Complete()
    {
        (Pgm, Pvw) = (Pvw, Pgm);
        InTransition = false; _auto = false; _t = 0;
        Transitions++;
    }

    // ── 소스 렌더(카메라 피드) ───────────────────────────────

    /// <summary>카메라 리그 갱신(MoCo = 공 추적 모션 컨트롤 · 드론 = 궤도 · 볼류메트릭 = 선수 자유 시점).</summary>
    public void UpdateCameras(BroadcastArenaScene s, float dt)
    {
        float bx = s.X(BroadcastArenaScene.Ball), bz = s.Z(BroadcastArenaScene.Ball);
        var a = MathF.Min(1f, 3f * dt);
        for (var i = 0; i < 6; i++)
        {
            var rigX = 10f + i * 16f;
            var tx = rigX + (bx - rigX) * (0.55f + 0.08f * i);
            _camX[i] += (tx - _camX[i]) * a; _camZ[i] += (bz - _camZ[i]) * a;
            _camS[i] = 0.12f + 0.05f * (i % 3); _camR[i] = 0;
        }
        var t = s.Frame * dt;
        for (var i = 6; i < 10; i++)
        {
            var ang = t * (0.05f + 0.02f * (i - 6)) + (i - 6) * MathF.PI * 0.5f;
            _camX[i] = BroadcastArenaScene.FieldW / 2 + 12f * MathF.Cos(ang); _camZ[i] = BroadcastArenaScene.FieldH / 2 + 8f * MathF.Sin(ang);
            _camS[i] = 0.55f + 0.1f * (i - 6); _camR[i] = ang * 0.25f;
        }
        for (var i = 10; i < 13; i++)
        {
            var p = i == 10 ? (s.LastTouch >= 0 ? s.LastTouch : 0) : i == 11 ? 2 : 7;
            _camX[i] += (s.X(p) - _camX[i]) * a; _camZ[i] += (s.Z(p) - _camZ[i]) * a;
            float vx = s.Vx(p), vz = s.Vz(p);
            if (vx * vx + vz * vz > 1f) { var h = MathF.Atan2(vz, vx); var d = h - _camR[i]; d -= MathF.Round(d / (2 * MathF.PI)) * 2 * MathF.PI; _camR[i] += d * a * 0.3f; }
            _camS[i] = 0.07f;
        }
    }

    /// <summary>13 카메라 입력 렌더(결손 입력은 건너뜀 → 프레임 싱크 유지).</summary>
    public void RenderSources(BroadcastArenaScene s)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _frame++;
        for (var i = 0; i < ReplayInput; i++)
        {
            if (InputLost[i]) continue;
            if (InputBlack[i]) FillBlack(Input(i));
            else RenderView(s, i, Input(i));
            _lastFrame[i] = _frame;
        }
        _lastFrame[BarsInput] = _lastFrame[BlackInput] = _frame;
        if (!InputLost[ReplayInput]) _lastFrame[ReplayInput] = _frame;
        RenderUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private void RenderView(BroadcastArenaScene s, int cam, Span<byte> f)
    {
        float sc = _camS[cam], cr = MathF.Cos(_camR[cam]), sr = MathF.Sin(_camR[cam]), cx = _camX[cam], cz = _camZ[cam];
        var vol = Kind[cam] == SourceKind.Volumetric;
        var lw = MathF.Max(0.25f, sc * 0.8f);
        float hw = Width * 0.5f, hh = Height * 0.5f;
        for (var y = 0; y < Height; y++)
        {
            var dy = (y - hh) * sc;
            float wx = cx - hw * sc * cr - dy * sr, wz = cz - hw * sc * sr + dy * cr;
            float ax = sc * cr, az = sc * sr;
            var o = y * Width * 4;
            for (var x = 0; x < Width; x++, wx += ax, wz += az, o += 4)
            {
                int r, g, b;
                if (wx >= 0 && wx <= BroadcastArenaScene.FieldW && wz >= 0 && wz <= BroadcastArenaScene.FieldH)
                {
                    var stripe = ((int)(wx / 6f) & 1) == 0;
                    r = stripe ? 40 : 34; g = stripe ? 142 : 126; b = stripe ? 60 : 52;
                    float ccx = wx - BroadcastArenaScene.FieldW / 2, ccz = wz - BroadcastArenaScene.FieldH / 2;
                    if (wx < lw || wx > BroadcastArenaScene.FieldW - lw || wz < lw || wz > BroadcastArenaScene.FieldH - lw || MathF.Abs(ccx) < lw * 0.5f ||
                        MathF.Abs(MathF.Sqrt(ccx * ccx + ccz * ccz) - 9.15f) < lw * 0.5f) { r = g = b = 228; }
                }
                else
                {
                    var hsh = (uint)((int)(wx * 2.3f) * 73856093 ^ (int)(wz * 2.3f) * 19349663);
                    var v = 26 + (int)(hsh >> 27);
                    r = v + 6; g = v; b = v + 14;
                    if ((wx < 0 && wx > -2f || wx > BroadcastArenaScene.FieldW && wx < BroadcastArenaScene.FieldW + 2f) && MathF.Abs(wz - BroadcastArenaScene.FieldH / 2) < BroadcastArenaScene.GoalHalf) { r = g = b = 210; }
                }
                f[o] = (byte)r; f[o + 1] = (byte)g; f[o + 2] = (byte)b; f[o + 3] = 255;
            }
        }
        for (var p = 0; p <= BroadcastArenaScene.Players; p++)
        {
            float dx = s.X(p) - cx, dz = s.Z(p) - cz;
            float u = (dx * cr + dz * sr) / sc + hw, v = (-dx * sr + dz * cr) / sc + hh;
            var rad = MathF.Max(1f, (p == BroadcastArenaScene.Ball ? 0.4f : 0.7f) / sc);
            if (u < -rad || v < -rad || u > Width + rad || v > Height + rad) continue;
            int r, g, b;
            if (p == BroadcastArenaScene.Ball) { r = 255; g = 236; b = 120; }
            else if (BroadcastArenaScene.Team(p) == 0) { r = 222; g = 44; b = 44; }
            else { r = 44; g = 86; b = 230; }
            int x0 = Math.Max(0, (int)(u - rad)), x1 = Math.Min(Width - 1, (int)(u + rad)), y0 = Math.Max(0, (int)(v - rad)), y1 = Math.Min(Height - 1, (int)(v + rad));
            var r2 = rad * rad;
            for (var yy = y0; yy <= y1; yy++)
                for (var xx = x0; xx <= x1; xx++)
                {
                    float ex = xx + 0.5f - u, ey = yy + 0.5f - v;
                    if (ex * ex + ey * ey > r2) continue;
                    var o = (yy * Width + xx) * 4;
                    f[o] = (byte)r; f[o + 1] = (byte)g; f[o + 2] = (byte)b;
                }
        }
        if (!vol) return;
        // 볼류메트릭 룩: 보셀 양자화 + 냉색 톤 + 깊이 비네트.
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                var o = (y * Width + x) * 4;
                float ex = (x - hw) / hw, ey = (y - hh) / hh;
                var vig = 1f - 0.35f * (ex * ex + ey * ey);
                f[o] = (byte)((int)(f[o] * 0.7f * vig) & 0xF0);
                f[o + 1] = (byte)((int)(f[o + 1] * 0.9f * vig) & 0xF0);
                f[o + 2] = (byte)Math.Min(255, (int)(f[o + 2] * 1.15f * vig + 20) & 0xF0);
            }
    }

    // ── 합성 ────────────────────────────────────────────────

    /// <summary>1 프레임 합성(무할당): 자동 트랜지션 진행 → PGM(컷/믹스/와이프 + FTB) · PVW · 멀티뷰어 · AFV 오디오.</summary>
    public void Composite(float excitement = 0f)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (InTransition && _auto)
        {
            _t += 1f / Math.Max(1, RateFrames);
            if (_t >= 0.9999f) Complete();
        }
        var a = Input(Pgm);
        var dst = ProgramBus;
        if (!InTransition) a.CopyTo(dst);
        else if (Transition == TransitionKind.Mix || Transition == TransitionKind.Cut) SwitchSimd.Mix(a, Input(Pvw), dst, (int)(_t * 256f + 0.5f));
        else
        {
            SwitchSimd.WipeKey(_patterns[(int)Transition - 2], _key, (int)(_t * (WipeRange + WipeSoft)));
            SwitchSimd.MixKeyed(a, Input(Pvw), _key, dst);
        }
        _ftb = _ftbOn ? MathF.Min(1f, _ftb + 1f / 30f) : MathF.Max(0f, _ftb - 1f / 30f);
        if (_ftb > 0) SwitchSimd.Mix(dst, Black, dst, (int)(_ftb * 256f + 0.5f));
        Input(Pvw).CopyTo(PreviewBus);
        CompositeUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        RenderAudio(excitement);
        var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        BuildMultiviewer();
        MultiviewUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t1) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>4×4 멀티뷰어 — 입력별 4×4 박스 평균 축소 타일.</summary>
    private void BuildMultiviewer()
    {
        int tw = Width / MvCols, th = Height / MvCols;
        var mv = Multiviewer;
        for (var i = 0; i < Inputs; i++)
        {
            ReadOnlySpan<byte> src = Input(i);
            int ox = i % MvCols * tw, oy = i / MvCols * th;
            for (var y = 0; y < th; y++)
                for (var x = 0; x < tw; x++)
                {
                    int r = 0, g = 0, b = 0;
                    for (var yy = 0; yy < MvCols; yy++)
                    {
                        var so = ((y * MvCols + yy) * Width + x * MvCols) * 4;
                        for (var xx = 0; xx < MvCols; xx++, so += 4) { r += src[so]; g += src[so + 1]; b += src[so + 2]; }
                    }
                    var d = ((oy + y) * Width + ox + x) * 4;
                    mv[d] = (byte)(r >> 4); mv[d + 1] = (byte)(g >> 4); mv[d + 2] = (byte)(b >> 4); mv[d + 3] = 255;
                }
        }
    }

    /// <summary>AFV 프로그램 오디오: 소스별 톤(−20 dBFS) + 경기장 앰비언스, 트랜지션 진행률로 크로스페이드, FTB 동반 페이드.</summary>
    private void RenderAudio(float excitement)
    {
        var t = InTransition ? _t : 0f;
        var g = 1f - _ftb;
        float fa = ToneHz(Pgm), fb = ToneHz(Pvw);
        float pa = _phase[Pgm], pb = _phase[Pvw], da = 2 * MathF.PI * fa / SampleRate, db = 2 * MathF.PI * fb / SampleRate;
        float amb = 0.02f + 0.08f * excitement, peak = 0;
        bool silentA = Pgm == BlackInput, silentB = Pvw == BlackInput;
        for (var n = 0; n < AudioBlock; n++)
        {
            _noise ^= _noise << 13; _noise ^= _noise >> 7; _noise ^= _noise << 17;
            var nz = ((_noise >> 40) / 16777216f - 0.5f) * 2f * amb;
            var sa = silentA ? 0f : 0.1f * MathF.Sin(pa);
            var sb = silentB ? 0f : 0.1f * MathF.Sin(pb);
            var s = ((1f - t) * sa + t * sb + nz) * g;
            _audio[2 * n] = s; _audio[2 * n + 1] = s * 0.96f;
            pa += da; pb += db;
            if (pa > 2 * MathF.PI) pa -= 2 * MathF.PI;
            if (pb > 2 * MathF.PI) pb -= 2 * MathF.PI;
            var m = MathF.Abs(s); if (m > peak) peak = m;
        }
        _phase[Pgm] = pa; if (Pvw != Pgm) _phase[Pvw] = pb;
        ProgramPeakDbfs = peak > 1e-6f ? 20f * MathF.Log10(peak) : -120f;
    }

    /// <summary>소스 오디오 톤(컬러바 = 1 kHz 라인업 톤).</summary>
    public static float ToneHz(int input) => input == BarsInput ? 1000f : 220f * (1f + input * 0.125f);

    /// <summary>입력 평균 휘도(BT.709 근사 · 4 픽셀 간격 표본 · 0..255).</summary>
    public int MeanLuma(int input) => MeanLuma(Input(input));

    public static int MeanLuma(ReadOnlySpan<byte> f)
    {
        long acc = 0; var n = 0;
        for (var o = 0; o + 3 < f.Length; o += 16) { acc += (54 * f[o] + 183 * f[o + 1] + 19 * f[o + 2]) >> 8; n++; }
        return n == 0 ? 0 : (int)(acc / n);
    }

    // ── 모니터 출력(UI) ─────────────────────────────────────

    public int BmpBytes => 54 + Width * Height * 3;

    /// <summary>RGBA 프레임 → 24비트 BMP(하향식) — UI 모니터용.</summary>
    public void WriteBmp(ReadOnlySpan<byte> rgba, Span<byte> dst)
    {
        var size = BmpBytes;
        dst.Slice(0, 54).Clear();
        dst[0] = (byte)'B'; dst[1] = (byte)'M';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(2), size);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(10), 54);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(14), 40);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(18), Width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(22), -Height);
        dst[26] = 1; dst[28] = 24;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(34), size - 54);
        var d = 54;
        for (var o = 0; o < Width * Height * 4; o += 4, d += 3) { dst[d] = rgba[o + 2]; dst[d + 1] = rgba[o + 1]; dst[d + 2] = rgba[o]; }
    }
}
