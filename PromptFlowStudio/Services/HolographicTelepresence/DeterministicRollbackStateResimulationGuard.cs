using System.Numerics;

namespace PromptFlowStudio.Services.HolographicTelepresence;

/// <summary>원격 입력 1 프레임(테이블 평면 손 위치 mm 양자화 + 누름 플래그) — 결정적 비교 단위.</summary>
public struct RollbackInput
{
    public short X, Z;
    public byte Press;
    public readonly bool Same(in RollbackInput o) => X == o.X && Z == o.Z && Press == o.Press;
}

/// <summary>
/// 텔레프레전스 공용 결정적 시나리오(원탁 4좌석 · 손 궤적 · 공유 퍽 초기 배치) — 모든 피어가 같은 함수로 자신의 실제 입력을 생성한다.
/// </summary>
public static class TelepresenceScenario
{
    public const int Participants = 4, Bodies = 8;
    public const float TableY = 0.75f, TableR = 0.85f, SeatR = 0.95f, HeadY = 1.2f, HandR = 0.045f, BodyR = 0.06f;
    public static readonly string[] Names = { "나(로컬)", "서울", "베를린", "샌프란시스코" };
    private static readonly float[] Periods = { 3.1f, 3.7f, 4.3f, 2.9f };

    public static float SeatAngle(int p) => p * (MathF.PI * 0.5f);
    public static Vector3 Seat(int p) => new(SeatR * MathF.Sin(SeatAngle(p)), 0f, SeatR * MathF.Cos(SeatAngle(p)));
    /// <summary>아바타 정면(+z)이 테이블 중심을 향하는 y 회전.</summary>
    public static float SeatYaw(int p) => SeatAngle(p) + MathF.PI;

    public static Vector3 Hand(int p, long frame)
    {
        var t = frame / 60f;
        var T = Periods[p];
        var ph = 2f * MathF.PI * t / T + p;
        var r = 0.6f - 0.42f * (0.5f - 0.5f * MathF.Cos(ph));
        var a = SeatAngle(p) + 0.38f * MathF.Sin(2f * MathF.PI * t / (T * 1.7f) + p * 1.3f);
        var press = MathF.Sin(ph) > -0.25f;
        return new Vector3(r * MathF.Sin(a), press ? TableY + 0.03f : TableY + 0.15f, r * MathF.Cos(a));
    }

    public static RollbackInput Input(int p, long frame)
    {
        var h = Hand(p, frame);
        return new RollbackInput { X = (short)MathF.Round(h.X * 1000f), Z = (short)MathF.Round(h.Z * 1000f), Press = h.Y < TableY + 0.1f ? (byte)1 : (byte)0 };
    }

    public static void InitBodies(Span<float> x, Span<float> z, Span<float> vx, Span<float> vz)
    {
        for (var i = 0; i < Bodies; i++)
        {
            var a = i * (2f * MathF.PI / Bodies) + 0.2f;
            x[i] = 0.3f * MathF.Sin(a); z[i] = 0.3f * MathF.Cos(a); vx[i] = 0f; vz[i] = 0f;
        }
    }
}

/// <summary>
/// [PromptFlow Studio v17.25.0] ⏪ 확정론적 롤백 상태 재시뮬레이션 &amp; 지터 보상 가드(GGPO 방식 · <see cref="Advance"/> 무할당).
///   ▸ 원격 3 피어 · RTT 20–150 ms · 지터 · 손실 — 각 패킷은 최근 <see cref="Redundancy"/> 프레임 입력을 중복 실어 손실을 다음 패킷으로 복구.
///   ▸ 미도착 원격 입력은 마지막 확정 입력 반복으로 예측 → 실제 입력 도착 시 불일치 프레임 스냅샷으로 롤백 후 현재까지 전진 재시뮬레이션(최대 <see cref="MaxRollback"/> 프레임).
///   ▸ 예측 한계(현재 − 최소 확정 &gt; 10) 시 GGPO 처럼 프레임 정지(stall) · 벽시계 대비 지연 시 틱당 2 프레임 따라잡기.
///   ▸ 지연 0 오라클과 확정 프레임 체크섬(FNV-1a · float 비트) 비교 → 디싱크 0 검증(롤백 OFF 시 디싱크 발생 비교).
///   ▸ 롤백 보정 Δ 를 표시 오프셋으로 흡수 후 3차 에르미트 h00(s)=2s³−3s²+1 로 감쇠 → 아바타 손/공유 퍽 순간 워프 방어.
/// </summary>
public sealed class DeterministicRollbackStateResimulationGuard
{
    public const int Participants = TelepresenceScenario.Participants, Bodies = TelepresenceScenario.Bodies, Points = Bodies + Participants;
    public const int MaxRollback = 10, Redundancy = 8, Hist = 64, SnapRing = 16, MaxInFlight = 1024, MaxCatchUp = 2;
    public const float FrameMs = 1000f / 60f, Dt = 1f / 60f, HandK = 60f, BodyMass = 0.15f, Restitution = 0.8f, Drag = 0.985f, SnapM = 0.5f;

    private struct Packet { public int From; public long Frame; public double Arrive; }

    // 라이브 · 오라클 상태(x, z, vx, vz)
    private readonly float[] _s = new float[4 * Bodies], _o = new float[4 * Bodies];
    private readonly float[] _snap = new float[SnapRing * 4 * Bodies];
    private readonly long[] _snapFrame = new long[SnapRing];
    private readonly RollbackInput[] _true = new RollbackInput[Participants * Hist], _used = new RollbackInput[Participants * Hist];
    private readonly long[] _recvFrame = new long[Participants * Hist];
    private readonly ulong[] _simSum = new ulong[Hist], _oraSum = new ulong[Hist];
    private readonly long[] _confirmed = new long[Participants];
    private readonly RollbackInput[] _lastConf = new RollbackInput[Participants];
    private readonly Packet[] _fly = new Packet[MaxInFlight];
    private readonly float[] _rtt = { 0f, 40f, 90f, 140f }, _jit = { 0f, 8f, 8f, 8f }, _loss = { 0f, 0.02f, 0.02f, 0.02f };
    private readonly float[] _lagFrames = new float[Participants], _meanDelay = new float[Participants];
    private readonly long[] _sent = new long[Participants], _lostPk = new long[Participants], _arrived = new long[Participants];
    private readonly Vector2[] _tOld = new Vector2[Points], _tCorr = new Vector2[Points], _disp = new Vector2[Points], _off = new Vector2[Points];
    private readonly int[] _offAge = new int[Points];
    private int _fly_n;
    private long _current, _sendFrame, _verified = -1, _rollTo = long.MaxValue;
    private uint _rng = 0x9E3779B9u;
    private bool _dispInit;

    public DeterministicRollbackStateResimulationGuard() => Reset();

    public bool RollbackEnabled { get; set; } = true;
    public bool Smoothing { get; set; } = true;
    public int SmoothFrames { get; set; } = 6;
    public long CurrentFrame => _current;
    public long Rollbacks { get; private set; }
    public long ResimFrames { get; private set; }
    public long Stalls { get; private set; }
    public long Desyncs { get; private set; }
    public long Verified { get; private set; }
    public long Mispredictions { get; private set; }
    public long OverflowRollbacks { get; private set; }
    public int LastDepth { get; private set; }
    public int MaxDepth { get; private set; }
    public int LastResim { get; private set; }
    public float WarpMm { get; private set; }
    public float MaxWarpMm { get; private set; }
    public float CorrectionMm { get; private set; }
    public double LastAdvanceUs { get; private set; }
    public long VerifiedFrame => _verified;
    public long Confirmed(int p) => _confirmed[p];
    public float RttMs(int p) => _rtt[p];
    public float JitterMs(int p) => _jit[p];
    public float LossRate(int p) => _loss[p];
    public float OneWayMs(int p) => _rtt[p] * 0.5f;
    public float LagFrames(int p) => _lagFrames[p];
    public float MeasuredRttMs(int p) => _meanDelay[p] * 2f;
    public long Sent(int p) => _sent[p];
    public long PacketsLost(int p) => _lostPk[p];
    public long Arrived(int p) => _arrived[p];
    public int InFlight => _fly_n;
    public Vector2 BodyPosition(int i) => new(_s[i], _s[Bodies + i]);
    public Vector2 BodyVelocity(int i) => new(_s[2 * Bodies + i], _s[3 * Bodies + i]);
    public Vector2 OraclePosition(int i) => new(_o[i], _o[Bodies + i]);
    /// <summary>표시 좌표(에르미트 평활 · 0..7 퍽 · 8..11 참가자 손).</summary>
    public Vector2 Display(int k) => _disp[k];
    public Vector2 DisplayOffset(int k) => _off[k] * Hermite(Math.Min(1f, _offAge[k] / (float)Math.Max(1, SmoothFrames)));
    /// <summary>현재 시뮬레이션에 사용된(예측 포함) 손 입력.</summary>
    public RollbackInput UsedInput(int p) => _current > 0 ? _used[p * Hist + Slot(_current - 1)] : TelepresenceScenario.Input(p, 0);
    public bool Predicted(int p) => _current > 0 && _recvFrame[p * Hist + Slot(_current - 1)] != _current - 1;
    public ulong Checksum => Sum(_s);

    public void SetLink(int p, float rttMs, float jitterMs, float loss)
    {
        if (p <= 0 || p >= Participants) return;
        _rtt[p] = Math.Clamp(rttMs, 0f, 400f); _jit[p] = Math.Clamp(jitterMs, 0f, 100f); _loss[p] = Math.Clamp(loss, 0f, 0.5f);
    }

    public void Reset()
    {
        TelepresenceScenario.InitBodies(_s.AsSpan(0, Bodies), _s.AsSpan(Bodies, Bodies), _s.AsSpan(2 * Bodies, Bodies), _s.AsSpan(3 * Bodies, Bodies));
        _s.AsSpan().CopyTo(_o);
        Array.Fill(_recvFrame, -1L); Array.Fill(_snapFrame, -1L);
        Array.Clear(_confirmed); Array.Clear(_simSum); Array.Clear(_oraSum);
        for (var p = 0; p < Participants; p++) { _confirmed[p] = -1; _lastConf[p] = TelepresenceScenario.Input(p, 0); }
        Array.Clear(_lagFrames); Array.Clear(_meanDelay); Array.Clear(_sent); Array.Clear(_lostPk); Array.Clear(_arrived);
        Array.Clear(_off); Array.Clear(_offAge);
        _fly_n = 0; _current = 0; _sendFrame = 0; _verified = -1; _rollTo = long.MaxValue; _rng = 0x9E3779B9u; _dispInit = false;
        Rollbacks = ResimFrames = Stalls = Desyncs = Verified = Mispredictions = OverflowRollbacks = 0;
        LastDepth = MaxDepth = LastResim = 0; WarpMm = MaxWarpMm = CorrectionMm = 0f;
    }

    /// <summary>벽시계 nowMs 까지 1 틱: 원격 송신/수신 → 롤백 재시뮬레이션 → 전진(≤2) → 확정 검증 → 에르미트 표시.</summary>
    public void Advance(double nowMs)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        // 1) 원격 피어 송신(각자 벽시계 프레임에 자기 입력 + 중복 7 프레임)
        while (_sendFrame * (double)FrameMs <= nowMs)
        {
            for (var p = 1; p < Participants; p++)
            {
                _sent[p]++;
                if (Rand() < _loss[p]) { _lostPk[p]++; continue; }
                if (_fly_n >= MaxInFlight) { _lostPk[p]++; continue; }
                _fly[_fly_n++] = new Packet { From = p, Frame = _sendFrame, Arrive = _sendFrame * (double)FrameMs + _rtt[p] * 0.5 + _jit[p] * Rand() };
            }
            _sendFrame++;
        }
        // 2) 수신 · 실제 입력 기록 · 불일치 프레임 탐지
        var target = (long)(nowMs / FrameMs);
        for (var k = 0; k < _fly_n;)
        {
            if (_fly[k].Arrive > nowMs) { k++; continue; }
            Deliver(_fly[k], nowMs);
            _fly[k] = _fly[--_fly_n];
        }
        // 3) 롤백 · 전진 재시뮬레이션(보정 Δ 측정용 이전 표시 목표)
        Targets(_tOld);
        var rolled = false;
        LastResim = 0;
        if (RollbackEnabled && _rollTo < _current)
        {
            var depth = (int)(_current - _rollTo);
            if (depth > MaxRollback) { OverflowRollbacks++; _rollTo = _current - MaxRollback; depth = MaxRollback; }
            Restore(_rollTo);
            for (var f = _rollTo; f < _current; f++) Simulate(f);
            Rollbacks++; ResimFrames += depth; LastDepth = depth; LastResim = depth;
            if (depth > MaxDepth) MaxDepth = depth;
            rolled = true;
        }
        _rollTo = long.MaxValue;
        var corr = 0f;
        if (rolled)
        {
            for (var k = 0; k < Points; k++)
            {
                var d = Target(k) - _tOld[k];
                var dl = d.Length();
                if (dl > corr) corr = dl;
                if (!Smoothing || dl < 1e-5f) continue;
                var cur = DisplayOffset(k) - d;
                _off[k] = cur.Length() > SnapM ? Vector2.Zero : cur;
                _offAge[k] = 0;
            }
        }
        CorrectionMm = corr * 1000f;
        Targets(_tCorr);
        // 4) 전진(예측 한계 정지 · 따라잡기)
        var minConf = long.MaxValue;
        for (var p = 1; p < Participants; p++) if (_confirmed[p] < minConf) minConf = _confirmed[p];
        for (var n = 0; n < MaxCatchUp && _current <= target; n++)
        {
            if (_current - minConf > MaxRollback) { Stalls++; break; }
            Oracle(_current);
            Simulate(_current);
            _current++;
        }
        // 5) 확정 프레임 결정성 검증(오라클 체크섬)
        var conf = Math.Min(minConf, _current - 1);
        while (_verified < conf)
        {
            var f = _verified + 1;
            if (f <= _current - Hist) { _verified = f; continue; }
            if (_simSum[Slot(f)] != _oraSum[Slot(f)]) Desyncs++;
            Verified++;
            _verified = f;
        }
        for (var p = 1; p < Participants; p++) _lagFrames[p] = _current - 1 - _confirmed[p];
        // 6) 표시(에르미트 오프셋 감쇠) · 워프 = 표시 이동 − 보정 후 자연 이동(롤백 보정이 화면에 새어 나온 순간 점프)
        var warp = 0f;
        for (var k = 0; k < Points; k++)
        {
            if (!Smoothing) _off[k] = Vector2.Zero;
            _offAge[k]++;
            var tn = Target(k);
            var nd = tn + DisplayOffset(k);
            if (_dispInit)
            {
                var w = (nd - _disp[k] - (tn - _tCorr[k])).Length();
                if (w > warp) warp = w;
            }
            _disp[k] = nd;
        }
        _dispInit = true;
        WarpMm = warp * 1000f;
        if (WarpMm > MaxWarpMm) MaxWarpMm = WarpMm;
        LastAdvanceUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    public void ResetMetrics()
    {
        Rollbacks = ResimFrames = Stalls = Desyncs = Verified = Mispredictions = OverflowRollbacks = 0;
        MaxDepth = 0; MaxWarpMm = 0f;
    }

    public static float Hermite(float s) => 1f - s * s * (3f - 2f * s);

    private void Deliver(in Packet pk, double nowMs)
    {
        var p = pk.From;
        _arrived[p]++;
        var delay = (float)(nowMs - pk.Frame * (double)FrameMs);
        _meanDelay[p] += 0.05f * (delay - _meanDelay[p]);
        var lo = Math.Max(0L, Math.Max(pk.Frame - Redundancy + 1, _current - Hist + MaxRollback + 2));
        for (var fr = lo; fr <= pk.Frame; fr++)
        {
            var i = p * Hist + Slot(fr);
            if (_recvFrame[i] == fr) continue;
            if (fr >= _current + Hist - MaxRollback - 2) break;   // 너무 앞선 프레임(링 여유 밖)
            var tin = TelepresenceScenario.Input(p, fr);
            _true[i] = tin;
            _recvFrame[i] = fr;
            if (fr < _current && !_used[i].Same(tin))
            {
                Mispredictions++;
                if (fr < _rollTo) _rollTo = fr;
            }
        }
        while (_recvFrame[p * Hist + Slot(_confirmed[p] + 1)] == _confirmed[p] + 1)
        {
            _confirmed[p]++;
            _lastConf[p] = _true[p * Hist + Slot(_confirmed[p])];
        }
    }

    private void Simulate(long f)
    {
        var sl = (int)(f & (SnapRing - 1));
        _s.AsSpan().CopyTo(_snap.AsSpan(sl * 4 * Bodies, 4 * Bodies));
        _snapFrame[sl] = f;
        Span<RollbackInput> inp = stackalloc RollbackInput[Participants];
        for (var p = 0; p < Participants; p++)
        {
            var i = p * Hist + Slot(f);
            if (p == 0)
            {
                if (_recvFrame[i] != f) { _true[i] = TelepresenceScenario.Input(0, f); _recvFrame[i] = f; }
                if (_confirmed[0] < f) { _confirmed[0] = f; _lastConf[0] = _true[i]; }
                inp[p] = _true[i];
            }
            else inp[p] = _recvFrame[i] == f ? _true[i] : _lastConf[p];
            _used[i] = inp[p];
        }
        StepPhysics(_s, inp);
        _simSum[Slot(f)] = Sum(_s);
    }

    private void Oracle(long f)
    {
        Span<RollbackInput> inp = stackalloc RollbackInput[Participants];
        for (var p = 0; p < Participants; p++) inp[p] = TelepresenceScenario.Input(p, f);
        StepPhysics(_o, inp);
        _oraSum[Slot(f)] = Sum(_o);
    }

    private void Restore(long f)
    {
        var sl = (int)(f & (SnapRing - 1));
        if (_snapFrame[sl] != f) return;
        _snap.AsSpan(sl * 4 * Bodies, 4 * Bodies).CopyTo(_s);
    }

    /// <summary>결정적 2D 퍽 물리(고정 순서 · float): 손 스프링 밀기 → 퍽 쌍 충돌 → 테이블 테두리 반사 → 감쇠 · 반암시 오일러.</summary>
    public static void StepPhysics(Span<float> s, ReadOnlySpan<RollbackInput> inp)
    {
        const int B = Bodies;
        const float R = TelepresenceScenario.BodyR, Rh = TelepresenceScenario.HandR + TelepresenceScenario.BodyR, Wall = TelepresenceScenario.TableR - TelepresenceScenario.BodyR;
        for (var i = 0; i < B; i++)
        {
            float fx = 0f, fz = 0f;
            for (var p = 0; p < inp.Length; p++)
            {
                if (inp[p].Press == 0) continue;
                var dx = s[i] - inp[p].X * 0.001f;
                var dz = s[B + i] - inp[p].Z * 0.001f;
                var d2 = dx * dx + dz * dz;
                if (d2 >= Rh * Rh || d2 < 1e-12f) continue;
                var d = MathF.Sqrt(d2);
                var f = HandK * (Rh - d) / d;
                fx += f * dx; fz += f * dz;
            }
            s[2 * B + i] += fx / BodyMass * Dt;
            s[3 * B + i] += fz / BodyMass * Dt;
        }
        for (var i = 0; i < B; i++)
            for (var j = i + 1; j < B; j++)
            {
                var dx = s[j] - s[i];
                var dz = s[B + j] - s[B + i];
                var d2 = dx * dx + dz * dz;
                if (d2 >= 4f * R * R || d2 < 1e-12f) continue;
                var d = MathF.Sqrt(d2);
                var nx = dx / d; var nz = dz / d;
                var pen = 0.5f * (2f * R - d);
                s[i] -= nx * pen; s[B + i] -= nz * pen; s[j] += nx * pen; s[B + j] += nz * pen;
                var vn = (s[2 * B + j] - s[2 * B + i]) * nx + (s[3 * B + j] - s[3 * B + i]) * nz;
                if (vn >= 0f) continue;
                var imp = 0.5f * (1f + Restitution) * vn;
                s[2 * B + i] += imp * nx; s[3 * B + i] += imp * nz; s[2 * B + j] -= imp * nx; s[3 * B + j] -= imp * nz;
            }
        for (var i = 0; i < B; i++)
        {
            var x = s[i]; var z = s[B + i];
            var r = MathF.Sqrt(x * x + z * z);
            if (r > Wall)
            {
                var nx = x / r; var nz = z / r;
                s[i] = nx * Wall; s[B + i] = nz * Wall;
                var vn = s[2 * B + i] * nx + s[3 * B + i] * nz;
                if (vn > 0f) { s[2 * B + i] -= 1.6f * vn * nx; s[3 * B + i] -= 1.6f * vn * nz; }
            }
            s[2 * B + i] *= Drag; s[3 * B + i] *= Drag;
            s[i] += s[2 * B + i] * Dt; s[B + i] += s[3 * B + i] * Dt;
        }
    }

    /// <summary>FNV-1a 64(상태 float 비트).</summary>
    public static ulong Sum(ReadOnlySpan<float> s)
    {
        var h = 14695981039346656037UL;
        for (var i = 0; i < s.Length; i++)
        {
            var b = (uint)BitConverter.SingleToInt32Bits(s[i]);
            for (var k = 0; k < 4; k++) { h ^= (b >> (8 * k)) & 0xFF; h *= 1099511628211UL; }
        }
        return h;
    }

    private Vector2 Target(int k)
    {
        if (k < Bodies) return new Vector2(_s[k], _s[Bodies + k]);
        var u = UsedInput(k - Bodies);
        return new Vector2(u.X * 0.001f, u.Z * 0.001f);
    }

    private void Targets(Vector2[] dst)
    {
        for (var k = 0; k < Points; k++) dst[k] = Target(k);
    }

    private static int Slot(long f) => (int)(f & (Hist - 1));

    private float Rand()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
        return (_rng >> 8) * (1f / 16777216f);
    }
}
