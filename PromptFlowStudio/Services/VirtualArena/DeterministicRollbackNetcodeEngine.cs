using System.Diagnostics;
using System.Numerics;

namespace PromptFlowStudio.Services.VirtualArena;

/// <summary>[v17.03.0] 아레나 SIMD 커널 — 정수 적분(결정론) · 부동소수 적분(축약 FMA 없음) 모두 스칼라 경로와 비트 동일.</summary>
public static class ArenaSimd
{
    public static void AddInPlace(Span<int> pos, ReadOnlySpan<int> vel)
    {
        var n = pos.Length; var i = 0;
        if (Vector.IsHardwareAccelerated)
            for (var w = Vector<int>.Count; i <= n - w; i += w) (new Vector<int>(pos.Slice(i)) + new Vector<int>(vel.Slice(i))).CopyTo(pos.Slice(i));
        for (; i < n; i++) pos[i] += vel[i];
    }

    public static void AddInPlaceScalar(Span<int> pos, ReadOnlySpan<int> vel)
    {
        for (var i = 0; i < pos.Length; i++) pos[i] += vel[i];
    }

    public static void Integrate(Span<float> pos, ReadOnlySpan<float> vel, float dt)
    {
        var n = pos.Length; var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var vd = new Vector<float>(dt);
            for (var w = Vector<float>.Count; i <= n - w; i += w) (new Vector<float>(pos.Slice(i)) + new Vector<float>(vel.Slice(i)) * vd).CopyTo(pos.Slice(i));
        }
        for (; i < n; i++) pos[i] += vel[i] * dt;
    }

    public static void IntegrateScalar(Span<float> pos, ReadOnlySpan<float> vel, float dt)
    {
        for (var i = 0; i < pos.Length; i++) pos[i] += vel[i] * dt;
    }
}

/// <summary>
/// [PromptFlow Studio v17.03.0] 🏟️ GGPO형 결정론적 롤백 넷코드 엔진(무할당).
/// 상태 = 단일 int[](Q16.16 고정소수 · 정수 전용 물리 · 상태 내 xorshift RNG) → 프레임마다 순환 링버퍼(64)에 스냅샷 + FNV-1a 체크섬.
/// 원격 입력 미도착 프레임은 직전 확정 입력 반복으로 예측 실행 · 늦게 도착한 입력이 예측과 다르면 해당 프레임 스냅샷으로 되돌려 현재까지 고속 재시뮬레이션.
/// 예측 한도(12) 초과 시 정지(stall) · 입력 지연(0~4) · 확정 프레임 체크섬 비교로 디싱크 검출. 게임 = 4인 볼 아레나(대시 · 골 · 볼 48개).
/// </summary>
public sealed class DeterministicRollbackNetcodeEngine
{
    public const int MaxPlayers = 4, Balls = 48, Ring = 64, MaxPrediction = 12, MaxAhead = 32;
    public const int One = 1 << 16, Arena = 32 * One, PlayerR = One * 6 / 10, BallR = One * 35 / 100;
    public const int Accel = One / 48, MaxV = One / 5, MaxBallV = One / 2, Kick = One / 6, GoalHalf = 4 * One, DashCool = 45;
    public const byte Up = 1, Down = 2, Left = 4, Right = 8, Dash = 16;
    private const int HFrame = 0, HRng = 1, PX = 2, PY = PX + MaxPlayers, PVX = PY + MaxPlayers, PVY = PVX + MaxPlayers, PScore = PVY + MaxPlayers, PCool = PScore + MaxPlayers,
        BX = PCool + MaxPlayers, BY = BX + Balls, BVX = BY + Balls, BVY = BVX + Balls, BTouch = BVY + Balls;
    public const int StateSize = BTouch + Balls;

    private readonly int[] _s = new int[StateSize];
    private readonly int[] _ring = new int[Ring * StateSize];
    private readonly int[] _ringFrame = new int[Ring];
    private readonly uint[] _ringSum = new uint[Ring];
    private readonly byte[] _in = new byte[Ring * MaxPlayers], _used = new byte[Ring * MaxPlayers];
    private readonly int[] _recv = new int[MaxPlayers];
    private readonly byte[] _cur = new byte[MaxPlayers];
    private readonly long[] _depthHist = new long[MaxPrediction + 2];
    private int _rollbackTo = int.MaxValue, _inputDelay;

    /// <param name="localPlayer">로컬 플레이어(−1 = 관전/권위 서버: 전원 원격 입력)</param>
    public DeterministicRollbackNetcodeEngine(int localPlayer = 0, int players = MaxPlayers)
    {
        LocalPlayer = localPlayer;
        Players = Math.Clamp(players, 1, MaxPlayers);
        Reset();
    }

    public int LocalPlayer { get; }
    public int Players { get; }
    public int Current { get; private set; }
    /// <summary>예측 허용 프레임 수(0 = 확정 입력만 실행 · 권위/동기 테스트 모드).</summary>
    public int PredictionLimit { get; set; } = MaxPrediction;
    public int InputDelay { get => _inputDelay; set => _inputDelay = Math.Clamp(value, 0, 4); }

    public long Rollbacks { get; private set; }
    public long ResimFrames { get; private set; }
    public long Mispredictions { get; private set; }
    public long Stalls { get; private set; }
    public long Advances { get; private set; }
    public int LastRollbackDepth { get; private set; }
    public int MaxRollbackDepth { get; private set; }
    public double LastAdvanceUs { get; private set; }
    public double MaxAdvanceUs { get; private set; }
    public long DepthCount(int d) => _depthHist[d];
    public int DepthBuckets => _depthHist.Length;

    public int ConfirmedFrame
    {
        get
        {
            var m = int.MaxValue;
            for (var p = 0; p < Players; p++) if (_recv[p] < m) m = _recv[p];
            return m;
        }
    }

    public int ReceivedFrame(int p) => _recv[p];
    public int PredictedFrames => Math.Max(0, Current - 1 - ConfirmedFrame);
    public bool CanAdvance => Current - ConfirmedFrame <= PredictionLimit;
    public uint CurrentChecksum => Checksum(_s);

    public float PlayerX(int p) => _s[PX + p] / (float)One;
    public float PlayerY(int p) => _s[PY + p] / (float)One;
    public float PlayerSpeed(int p) => MathF.Sqrt((float)_s[PVX + p] * _s[PVX + p] + (float)_s[PVY + p] * _s[PVY + p]) / One;
    public bool PlayerDashing(int p) => _s[PCool + p] > DashCool - 15;
    public int Score(int p) => _s[PScore + p];
    public float BallX(int b) => _s[BX + b] / (float)One;
    public float BallY(int b) => _s[BY + b] / (float)One;
    public int BallTouch(int b) => _s[BTouch + b];

    public void Reset()
    {
        Array.Clear(_s);
        Array.Fill(_ringFrame, -1);
        Array.Clear(_in); Array.Clear(_used); Array.Clear(_depthHist);
        _s[HRng] = 0x2545F491;
        for (var p = 0; p < MaxPlayers; p++)
        {
            _s[PX + p] = (p & 1) == 0 ? 6 * One : Arena - 6 * One;
            _s[PY + p] = (p & 2) == 0 ? 6 * One : Arena - 6 * One;
        }
        for (var b = 0; b < Balls; b++)
        {
            _s[BX + b] = 4 * One + (b % 8) * 3 * One;
            _s[BY + b] = 7 * One + (b / 8) * 3 * One;
            _s[BVX + b] = (int)(NextRng() % (uint)(MaxBallV / 2)) - MaxBallV / 4;
            _s[BVY + b] = (int)(NextRng() % (uint)(MaxBallV / 2)) - MaxBallV / 4;
            _s[BTouch + b] = -1;
        }
        Array.Fill(_recv, -1);
        Current = 0;
        _rollbackTo = int.MaxValue;
        Rollbacks = ResimFrames = Mispredictions = Stalls = Advances = 0;
        LastRollbackDepth = MaxRollbackDepth = 0;
        MaxAdvanceUs = 0;
    }

    private uint NextRng()
    {
        var r = (uint)_s[HRng];
        r ^= r << 13; r ^= r >> 17; r ^= r << 5;
        _s[HRng] = (int)r;
        return r;
    }

    public static uint Checksum(ReadOnlySpan<int> s)
    {
        var h = 2166136261u;
        for (var i = 0; i < s.Length; i++) { h ^= (uint)s[i]; h *= 16777619u; }
        return h;
    }

    // ── 입력 ──

    /// <summary>로컬 입력 → 프레임 Current + InputDelay(지연 증가 시 공백 프레임은 같은 입력으로 채움).</summary>
    public void AddLocalInput(byte input)
    {
        if (LocalPlayer < 0) return;
        var target = Current + _inputDelay;
        while (_recv[LocalPlayer] < target) Put(LocalPlayer, _recv[LocalPlayer] + 1, input);
    }

    /// <summary>원격 입력(프레임 순서대로만 수락 · 중복/공백/과도 선행 거부).</summary>
    public bool AddRemoteInput(int p, int frame, byte input)
    {
        if ((uint)p >= (uint)Players || p == LocalPlayer) return false;
        if (frame != _recv[p] + 1 || frame >= Current + MaxAhead) return false;
        Put(p, frame, input);
        return true;
    }

    private void Put(int p, int f, byte input)
    {
        var k = f % Ring * MaxPlayers + p;
        _in[k] = input;
        _recv[p] = f;
        if (f < Current && _used[k] != input)
        {
            Mispredictions++;
            if (f < _rollbackTo) _rollbackTo = f;
        }
    }

    public byte ConfirmedInput(int p, int f) => f <= _recv[p] ? _in[f % Ring * MaxPlayers + p] : (byte)0;

    /// <summary>확정 입력 또는 예측(직전 확정 입력 반복).</summary>
    private byte InputFor(int p, int f)
    {
        if (f <= _recv[p]) return _in[f % Ring * MaxPlayers + p];
        var r = _recv[p];
        return r < 0 ? (byte)0 : _in[r % Ring * MaxPlayers + p];
    }

    // ── 진행 · 롤백 ──

    /// <summary>대기 롤백 적용(되감기 → 고속 재시뮬) 후 1 프레임 진행. 예측 한도 초과 시 false(stall).</summary>
    public bool Advance()
    {
        var t0 = Stopwatch.GetTimestamp();
        LastRollbackDepth = 0;
        if (_rollbackTo < Current)
        {
            var from = _rollbackTo;
            var depth = Current - from;
            Load(from);
            for (var f = from; f < Current; f++) SimFrame(f);
            Rollbacks++; ResimFrames += depth; LastRollbackDepth = depth;
            if (depth > MaxRollbackDepth) MaxRollbackDepth = depth;
        }
        _rollbackTo = int.MaxValue;
        var ok = CanAdvance;
        if (ok)
        {
            SimFrame(Current);
            Current++;
            Advances++;
            _depthHist[Math.Min(LastRollbackDepth, _depthHist.Length - 1)]++;
        }
        else Stalls++;
        LastAdvanceUs = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
        if (LastAdvanceUs > MaxAdvanceUs) MaxAdvanceUs = LastAdvanceUs;
        return ok;
    }

    private void SimFrame(int f)
    {
        var slot = f % Ring;
        _s[HFrame] = f;
        _s.AsSpan().CopyTo(_ring.AsSpan(slot * StateSize, StateSize));
        _ringFrame[slot] = f;
        _ringSum[slot] = Checksum(_s);
        for (var p = 0; p < Players; p++)
        {
            var i = InputFor(p, f);
            _cur[p] = i;
            _used[slot * MaxPlayers + p] = i;
        }
        Simulate();
    }

    private void Load(int f)
    {
        var slot = f % Ring;
        if (_ringFrame[slot] != f) throw new InvalidOperationException("롤백 스냅샷 링 초과");
        _ring.AsSpan(slot * StateSize, StateSize).CopyTo(_s);
    }

    /// <summary>프레임 f 시작 시점 체크섬(링 보존 시). final = f 이전 입력 전원 확정(더 이상 롤백 불가).</summary>
    public bool TryChecksum(int f, out uint sum, out bool final)
    {
        var slot = f % Ring;
        sum = 0; final = false;
        if (f < 0 || f >= Current || _ringFrame[slot] != f) return false;
        sum = _ringSum[slot];
        final = f - 1 <= ConfirmedFrame && _rollbackTo > f - 1;
        return true;
    }

    // ── 결정론 물리(정수 전용) ──

    private void Simulate()
    {
        var s = _s;
        for (var p = 0; p < Players; p++)
        {
            int inp = _cur[p];
            var ax = ((inp & Right) != 0 ? Accel : 0) - ((inp & Left) != 0 ? Accel : 0);
            var ay = ((inp & Up) != 0 ? Accel : 0) - ((inp & Down) != 0 ? Accel : 0);
            var vx = s[PVX + p] + ax; var vy = s[PVY + p] + ay;
            if ((inp & Dash) != 0 && s[PCool + p] == 0 && (ax | ay) != 0) { vx += ax * 12; vy += ay * 12; s[PCool + p] = DashCool; }
            if (s[PCool + p] > 0) s[PCool + p]--;
            vx -= vx >> 4; vy -= vy >> 4;
            var lim = s[PCool + p] > DashCool - 15 ? MaxV * 3 : MaxV;
            vx = Math.Clamp(vx, -lim, lim); vy = Math.Clamp(vy, -lim, lim);
            var x = s[PX + p] + vx; var y = s[PY + p] + vy;
            if (x < PlayerR) { x = PlayerR; vx = 0; } else if (x > Arena - PlayerR) { x = Arena - PlayerR; vx = 0; }
            if (y < PlayerR) { y = PlayerR; vy = 0; } else if (y > Arena - PlayerR) { y = Arena - PlayerR; vy = 0; }
            s[PX + p] = x; s[PY + p] = y; s[PVX + p] = vx; s[PVY + p] = vy;
        }
        ArenaSimd.AddInPlace(s.AsSpan(BX, Balls), s.AsSpan(BVX, Balls));
        ArenaSimd.AddInPlace(s.AsSpan(BY, Balls), s.AsSpan(BVY, Balls));
        const int r = PlayerR + BallR;
        for (var b = 0; b < Balls; b++)
        {
            int x = s[BX + b], y = s[BY + b], vx = s[BVX + b], vy = s[BVY + b];
            var inGoal = Math.Abs(y - Arena / 2) < GoalHalf;
            if ((x < BallR || x > Arena - BallR) && inGoal) { Goal(b); continue; }
            if (x < BallR) { x = 2 * BallR - x; vx = -vx; } else if (x > Arena - BallR) { x = 2 * (Arena - BallR) - x; vx = -vx; }
            if (y < BallR) { y = 2 * BallR - y; vy = -vy; } else if (y > Arena - BallR) { y = 2 * (Arena - BallR) - y; vy = -vy; }
            vx -= vx >> 7; vy -= vy >> 7;
            for (var p = 0; p < Players; p++)
            {
                var dx = x - s[PX + p]; var dy = y - s[PY + p];
                if (dx <= -r || dx >= r || dy <= -r || dy >= r) continue;
                var d2 = (long)dx * dx + (long)dy * dy;
                if (d2 >= (long)r * r) continue;
                var d = Math.Max(1, ISqrt(d2));
                var ov = r - d;
                x += (int)((long)dx * ov / d); y += (int)((long)dy * ov / d);
                vx += (int)((long)dx * Kick / d) + (s[PVX + p] >> 1);
                vy += (int)((long)dy * Kick / d) + (s[PVY + p] >> 1);
                s[BTouch + b] = p;
            }
            s[BX + b] = Math.Clamp(x, BallR, Arena - BallR); s[BY + b] = Math.Clamp(y, BallR, Arena - BallR);
            s[BVX + b] = Math.Clamp(vx, -MaxBallV, MaxBallV); s[BVY + b] = Math.Clamp(vy, -MaxBallV, MaxBallV);
        }
    }

    private void Goal(int b)
    {
        var t = _s[BTouch + b];
        if (t >= 0) _s[PScore + t]++;
        _s[BX + b] = Arena / 2;
        _s[BY + b] = Arena / 2 + (int)(NextRng() % (uint)(8 * One)) - 4 * One;
        _s[BVX + b] = (int)(NextRng() % (uint)MaxBallV) - MaxBallV / 2;
        _s[BVY + b] = (int)(NextRng() % (uint)MaxBallV) - MaxBallV / 2;
        _s[BTouch + b] = -1;
    }

    /// <summary>정확 정수 제곱근(IEEE sqrt 근사 + 정수 보정 · 플랫폼 무관).</summary>
    public static int ISqrt(long v)
    {
        if (v <= 0) return 0;
        var x = (long)Math.Sqrt(v);
        while (x * x > v) x--;
        while ((x + 1) * (x + 1) <= v) x++;
        return (int)x;
    }

    /// <summary>결정적 봇 입력(플레이어 · 프레임 → 8방향 + 간헐 대시 · 20 프레임 세그먼트).</summary>
    public static byte Bot(int p, int f)
    {
        var k = f + p * 17;
        var h = (uint)(p * 7919) ^ (uint)(k / 20) * 2654435761u;
        h ^= h >> 16; h *= 0x7feb352du; h ^= h >> 15; h *= 0x846ca68bu; h ^= h >> 16;
        var b = (h % 9) switch
        {
            0 => Up, 1 => (byte)(Up | Right), 2 => Right, 3 => (byte)(Down | Right), 4 => Down,
            5 => (byte)(Down | Left), 6 => Left, 7 => (byte)(Up | Left), _ => (byte)0,
        };
        if ((h >> 8) % 5 == 0 && k % 20 < 2) b |= Dash;
        return b;
    }
}
