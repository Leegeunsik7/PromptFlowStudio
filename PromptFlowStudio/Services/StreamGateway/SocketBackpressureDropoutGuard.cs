namespace PromptFlowStudio.Services.StreamGateway;

/// <summary>v17.62.0 라이브 채팅 플랫폼.</summary>
public enum ChatPlatform : byte { Twitch, YouTube, Chzzk }

/// <summary>v17.62.0 채팅 발신자 역할(배압 우선순위 판정).</summary>
[Flags]
public enum ChatRole : byte { Viewer = 0, Moderator = 1, Broadcaster = 2, Subscriber = 4 }

/// <summary>v17.62.0 수신 판정.</summary>
public enum IngressResult : byte { Queued, PriorityQueued, TailDropped, Shed, PriorityOverflow, Rejected }

/// <summary>v17.62.0 배압 큐 소비자 — v17.61 리액션 트리거 엔진 어댑터 등(무할당 구현). 반환 = 세이프티 통과(인게이지먼트 집계 대상).</summary>
public interface IChatDispatchTarget
{
    bool Deliver(ChatPlatform platform, ReadOnlySpan<char> user, ReadOnlySpan<char> text, int amount, ChatRole role, long nowMs);
}

/// <summary>
/// [PromptFlow Studio v17.62.0] 🧯 네트워크 소켓 지수 백오프 &amp; 고부하 배압 세이프티 가드(<see cref="Offer"/> · <see cref="Drain"/> 무할당 · 다중 생산자 / 단일 소비자).
///   ▸ 재접속: 플랫폼별 시도 k → 기저 min(2^k, 16) s × 지터 U(0.8, 1.2) → 16 s 상한 클램프(1 · 2 · 4 · 8 · 16 · 16 …) · 무한 재시도 · 첫 메시지 수신(건강) 시 k = 0.
///   ▸ 수신률: 100 ms × 10 버킷 슬라이딩 1 s 창 — 500 msg/s 초과 시 셰딩 진입(400 미만 해제 · 히스테리시스).
///   ▸ 이중 레인: 우선(후원 · 모더레이터 · 방송인 512) / 일반(1 024). 셰딩 중 일반 텍스트는 즉시 테일 드롭, 일반 레인 포화 시에도 테일 드롭 — 우선 레인은 항상 수용.
///   ▸ 소비: 렌더 틱이 <see cref="Drain"/>(우선 → 일반 · 최대 건수 + µs 예산) — 2 s 초과 일반 메시지는 지연 폐기. 레코드는 잠금 내 복사 후 잠금 밖에서 전달.
/// </summary>
public sealed class SocketBackpressureDropoutGuard
{
    public const int PriorityCap = 512, NormalCap = 1024, UserCap = 40, TextCap = 280, SlotChars = UserCap + TextCap, Buckets = 10, BucketMs = 100;
    public const int ShedEnterPerSec = 500, ShedExitPerSec = 400, StaleMs = 2000, MaxBackoffSec = 16;
    public const float JitterMin = 0.8f, JitterMax = 1.2f;

    private struct Rec { public long AtMs; public int Amount; public ChatPlatform Platform; public ChatRole Role; public short UserLen, TextLen; }

    private sealed class Lane
    {
        public readonly Rec[] R;
        public readonly char[] C;
        public int Head, Count;
        public Lane(int cap) { R = new Rec[cap]; C = new char[cap * SlotChars]; }
        public int Cap => R.Length;
    }

    private struct Backoff { public int Attempt; public long Reconnects, Disconnects; public double LastDelaySec; }

    private readonly object _lock = new();
    private readonly Lane _pri = new(PriorityCap), _norm = new(NormalCap);
    private readonly long[] _bStamp = new long[Buckets];
    private readonly int[] _bCount = new int[Buckets];
    private readonly Backoff[] _bo = new Backoff[3];
    private readonly long[] _offered = new long[3];
    private readonly char[] _u = new char[UserCap], _t = new char[TextCap];
    private uint _rng = 0x1762u;

    public long Offered { get; private set; }
    public long Queued { get; private set; }
    public long PriorityQueued { get; private set; }
    public long TailDropped { get; private set; }
    public long ShedDropped { get; private set; }
    public long PriorityOverflow { get; private set; }
    public long StaleDropped { get; private set; }
    public long Drained { get; private set; }
    public long DeliveredClean { get; private set; }
    public long DeliveryFaults { get; private set; }
    public int RatePerSec { get; private set; }
    public int PeakRatePerSec { get; private set; }
    public bool Shedding { get; private set; }
    public long ShedEpisodes { get; private set; }
    public double LastDrainUs { get; private set; }
    public double MaxDrainUs { get; private set; }
    public int LastDrainCount { get; private set; }
    /// <summary>재접속 지연 배율(테스트 · 시뮬레이션 가속 — 1 = 실시간).</summary>
    public double BackoffScale { get; set; } = 1;
    public int PriorityDepth { get { lock (_lock) return _pri.Count; } }
    public int NormalDepth { get { lock (_lock) return _norm.Count; } }
    public long OfferedBy(ChatPlatform p) => _offered[(int)p];
    public int Attempt(ChatPlatform p) => _bo[(int)p].Attempt;
    public long Reconnects(ChatPlatform p) => _bo[(int)p].Reconnects;
    public long Disconnects(ChatPlatform p) => _bo[(int)p].Disconnects;
    public double LastBackoffSec(ChatPlatform p) => _bo[(int)p].LastDelaySec;

    public static string ResultName(IngressResult r) => r switch
    {
        IngressResult.PriorityQueued => "⭐ 우선",
        IngressResult.TailDropped => "🧯 테일 드롭",
        IngressResult.Shed => "🌊 셰딩",
        IngressResult.PriorityOverflow => "⚠ 우선 포화",
        IngressResult.Rejected => "⛔ 거부",
        _ => "✅ 큐",
    };

    public void Reset()
    {
        lock (_lock)
        {
            _pri.Head = _pri.Count = _norm.Head = _norm.Count = 0;
            Array.Clear(_bStamp); Array.Clear(_bCount); Array.Clear(_bo); Array.Clear(_offered);
            Offered = Queued = PriorityQueued = TailDropped = ShedDropped = PriorityOverflow = StaleDropped = Drained = DeliveredClean = DeliveryFaults = ShedEpisodes = 0;
            RatePerSec = PeakRatePerSec = 0; Shedding = false; MaxDrainUs = 0;
        }
    }

    // ══════════════════════ 재접속 백오프 ══════════════════════
    /// <summary>연결 끊김 통지 → 다음 재접속까지 대기 시간(배율 적용 · 지터 · 16 s 상한).</summary>
    public TimeSpan OnDisconnected(ChatPlatform p)
    {
        lock (_lock)
        {
            ref var b = ref _bo[(int)p];
            var baseSec = Math.Min(MaxBackoffSec, 1 << Math.Min(b.Attempt, 4));
            var j = JitterMin + (JitterMax - JitterMin) * ((NextRng() >> 8) * (1f / 16777216f));
            var d = Math.Min(MaxBackoffSec, baseSec * j);
            b.Attempt = Math.Min(b.Attempt + 1, 30); b.Disconnects++; b.LastDelaySec = d;
            return TimeSpan.FromSeconds(d * BackoffScale);
        }
    }

    /// <summary>순수 계산(테스트 · UI) — 시도 k 의 기저 지연(s).</summary>
    public static int BaseDelaySec(int attempt) => Math.Min(MaxBackoffSec, 1 << Math.Min(Math.Max(attempt, 0), 4));

    public void OnConnected(ChatPlatform p) { lock (_lock) _bo[(int)p].Reconnects++; }

    /// <summary>첫 정상 메시지 수신 — 시도 횟수 초기화.</summary>
    public void OnHealthy(ChatPlatform p) { if (_bo[(int)p].Attempt != 0) lock (_lock) _bo[(int)p].Attempt = 0; }

    private uint NextRng() { _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5; return _rng; }

    // ══════════════════════ 수신(무할당 · 네트워크 스레드) ══════════════════════
    public IngressResult Offer(ChatPlatform p, ReadOnlySpan<char> user, ReadOnlySpan<char> text, int amount, ChatRole role, long nowMs)
    {
        if (user.IsEmpty) return IngressResult.Rejected;
        var priority = amount > 0 || (role & (ChatRole.Moderator | ChatRole.Broadcaster)) != 0;
        lock (_lock)
        {
            Offered++; _offered[(int)p]++;
            CountRate(nowMs);
            Lane lane;
            if (priority)
            {
                if (_pri.Count >= _pri.Cap) { PriorityOverflow++; return IngressResult.PriorityOverflow; }
                lane = _pri;
            }
            else
            {
                if (Shedding) { ShedDropped++; return IngressResult.Shed; }
                if (_norm.Count >= _norm.Cap) { TailDropped++; return IngressResult.TailDropped; }
                lane = _norm;
            }
            var i = (lane.Head + lane.Count) % lane.Cap;
            ref var r = ref lane.R[i];
            r.AtMs = nowMs; r.Amount = amount; r.Platform = p; r.Role = role;
            var ul = Math.Min(user.Length, UserCap); var tl = Math.Min(text.Length, TextCap);
            r.UserLen = (short)ul; r.TextLen = (short)tl;
            var c = lane.C.AsSpan(i * SlotChars, SlotChars);
            user[..ul].CopyTo(c); text[..tl].CopyTo(c[UserCap..]);
            lane.Count++;
            if (priority) { PriorityQueued++; return IngressResult.PriorityQueued; }
            Queued++;
            return IngressResult.Queued;
        }
    }

    private void CountRate(long now)
    {
        var slot = now / BucketMs;
        var k = (int)(slot % Buckets);
        if (_bStamp[k] != slot) { _bStamp[k] = slot; _bCount[k] = 0; }
        _bCount[k]++;
        RefreshRate(slot);
    }

    private void RefreshRate(long slot)
    {
        var sum = 0;
        for (var i = 0; i < Buckets; i++) if (slot - _bStamp[i] < Buckets) sum += _bCount[i];
        RatePerSec = sum;
        if (sum > PeakRatePerSec) PeakRatePerSec = sum;
        if (!Shedding && sum > ShedEnterPerSec) { Shedding = true; ShedEpisodes++; }
        else if (Shedding && sum < ShedExitPerSec) Shedding = false;
    }

    // ══════════════════════ 소비(무할당 · 렌더 틱) ══════════════════════
    /// <summary>우선 → 일반 순 최대 maxMessages 건 · budgetUs 예산 내 전달. 반환 = 전달 건수.</summary>
    public int Drain(IChatDispatchTarget target, long nowMs, int maxMessages, double budgetUs)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var limit = (long)(budgetUs * System.Diagnostics.Stopwatch.Frequency / 1e6);
        var n = 0;
        lock (_lock) RefreshRate(nowMs / BucketMs);
        while (n < maxMessages && System.Diagnostics.Stopwatch.GetTimestamp() - t0 < limit)
        {
            Rec r;
            lock (_lock)
            {
                Lane lane;
                if (_pri.Count > 0) lane = _pri;
                else
                {
                    while (_norm.Count > 0 && nowMs - _norm.R[_norm.Head].AtMs > StaleMs) { Pop(_norm); StaleDropped++; }
                    if (_norm.Count == 0) break;
                    lane = _norm;
                }
                r = lane.R[lane.Head];
                var c = lane.C.AsSpan(lane.Head * SlotChars, SlotChars);
                c[..r.UserLen].CopyTo(_u); c.Slice(UserCap, r.TextLen).CopyTo(_t);
                Pop(lane);
                Drained++;
            }
            try { if (target.Deliver(r.Platform, _u.AsSpan(0, r.UserLen), _t.AsSpan(0, r.TextLen), r.Amount, r.Role, nowMs)) DeliveredClean++; }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { DeliveryFaults++; PromptFlowStudio.Program.Log("Gateway.Deliver", ex); }
            n++;
        }
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        LastDrainUs = us; LastDrainCount = n; if (us > MaxDrainUs) MaxDrainUs = us;
        return n;
    }

    private static void Pop(Lane l) { l.Head = (l.Head + 1) % l.Cap; l.Count--; }
}
