using PromptFlowStudio.Services.StreamGateway;

namespace PromptFlowStudio.Services.SemanticChat;

/// <summary>v17.72.0 정규화 채팅 레코드(링 슬롯 메타 · 본문은 고정 char 슬롯).</summary>
public struct UnifiedChatRecord
{
    public long Seq, AtMs;
    public int Amount, UserHash;
    public ChatPlatform Platform;
    public ChatRole Role;
    public short UserLen, TextLen;
    public readonly bool Priority => Amount > 0 || (Role & (ChatRole.Moderator | ChatRole.Broadcaster)) != 0;
}

/// <summary>v17.72.0 병합 스트림 소비자(분석기 · 단일 소비 스레드 · 무할당).</summary>
public interface IUnifiedChatConsumer
{
    void Consume(in UnifiedChatRecord rec, ReadOnlySpan<char> user, ReadOnlySpan<char> text);
}

/// <summary>
/// [PromptFlow Studio v17.72.0] 🔀 크로스 플랫폼 통합 채팅 애그리게이터(다중 생산자 · 단일 소비자 · 무할당).
///   ▸ 입력: v17.62 게이트웨이(Twitch IRC · YouTube 폴링 · 치지직 WSS 파서) <see cref="IChatDispatchTarget"/> 탭 + 임의 소스 <see cref="Offer"/> — 소켓 스레드별 병렬 생산.
///   ▸ 정규화: 제어문자/제로폭 제거 · 연속 공백 1 개 · 길이 클램프(닉 32 · 본문 200) · 닉 FNV 해시 · 단조 시퀀스 · 플랫폼 태그.
///   ▸ 링: Vyukov 유계 MPMC 시퀀스 링(4096 슬롯 · Interlocked CAS 슬롯 예약 · 잠금 0) — 생산자 간 경합은 CAS 재시도만.
///   ▸ 배압 밸브(병렬): ① 100 ms × 10 버킷 수신률 &gt; <see cref="ValveRatePerSec"/>(1 000) → 일반 메시지 확률 셰딩(초과분 비율 · 우선 메시지 통과)
///     ② 링 점유 ≥ 75 % → 일반 메시지 테일 드롭 · 100 % → 우선도 드롭(계수). 후원 · 모더레이터 · 방송인 = 우선.
///   ▸ 소비: <see cref="Drain"/>(최대 건수 + µs 예산) — 렌더/분석 틱 스레드.
/// </summary>
public sealed class UnifiedCrossPlatformChatAggregator : IChatDispatchTarget
{
    public const int Capacity = 4096, Mask = Capacity - 1, UserCap = 32, TextCap = 200, SlotChars = UserCap + TextCap, Buckets = 10, BucketMs = 100, Platforms = 3;
    public const int ValveRatePerSec = 1_000;
    public const float HighWater = 0.75f;

    private readonly long[] _slotSeq = new long[Capacity];
    private readonly UnifiedChatRecord[] _rec = new UnifiedChatRecord[Capacity];
    private readonly char[] _chars = new char[Capacity * SlotChars];
    private readonly long[] _bStamp = new long[Buckets];
    private readonly int[] _bCount = new int[Buckets];
    private readonly long[] _byPlatform = new long[Platforms], _droppedBy = new long[Platforms];
    private long _enqueue, _dequeue, _seq, _offered, _accepted, _shed, _tail, _priDrop, _priority;
    private uint _rng = 0x1772u;

    public UnifiedCrossPlatformChatAggregator() { for (var i = 0; i < Capacity; i++) _slotSeq[i] = i; }

    public Func<long> Clock { get; set; } = static () => Environment.TickCount64;
    public bool ValveEnabled { get; set; } = true;
    public long Offered => Interlocked.Read(ref _offered);
    public long Accepted => Interlocked.Read(ref _accepted);
    public long Shed => Interlocked.Read(ref _shed);
    public long TailDropped => Interlocked.Read(ref _tail);
    public long PriorityDropped => Interlocked.Read(ref _priDrop);
    public long PriorityAccepted => Interlocked.Read(ref _priority);
    public long Drained { get; private set; }
    public long ByPlatform(ChatPlatform p) => Interlocked.Read(ref _byPlatform[(int)p]);
    public long DroppedBy(ChatPlatform p) => Interlocked.Read(ref _droppedBy[(int)p]);
    public int Depth => (int)Math.Clamp(Interlocked.Read(ref _enqueue) - Interlocked.Read(ref _dequeue), 0, Capacity);
    public float Occupancy => Depth / (float)Capacity;
    public double LastDrainUs { get; private set; }
    public double MaxDrainUs { get; private set; }

    /// <summary>최근 1 s 수신률(msg/s · 버킷 합).</summary>
    public int RatePerSec(long nowMs)
    {
        var sum = 0; var cur = nowMs / BucketMs;
        for (var i = 0; i < Buckets; i++) if (cur - Volatile.Read(ref _bStamp[i]) < Buckets) sum += Volatile.Read(ref _bCount[i]);
        return sum;
    }

    public bool Shedding(long nowMs) => ValveEnabled && RatePerSec(nowMs) > ValveRatePerSec;

    public static int HashUser(ReadOnlySpan<char> u) { var h = 2166136261u; foreach (var c in u) { h ^= c; h *= 16777619; } return (int)h; }

    // ══════════════════════ 생산(임의 스레드 · 무할당) ══════════════════════
    public bool Deliver(ChatPlatform platform, ReadOnlySpan<char> user, ReadOnlySpan<char> text, int amount, ChatRole role, long nowMs) => Offer(platform, user, text, amount, role);

    public bool Offer(ChatPlatform platform, ReadOnlySpan<char> user, ReadOnlySpan<char> text, int amount = 0, ChatRole role = ChatRole.Viewer)
    {
        var now = Clock();
        Interlocked.Increment(ref _offered);
        var pri = amount > 0 || (role & (ChatRole.Moderator | ChatRole.Broadcaster)) != 0;
        // ① 수신률 버킷(원자 갱신 · 버킷 경계 리셋 경합은 근사 허용)
        var b = now / BucketMs; var bi = (int)(b % Buckets);
        if (Interlocked.Exchange(ref _bStamp[bi], b) != b) Interlocked.Exchange(ref _bCount[bi], 0);
        Interlocked.Increment(ref _bCount[bi]);
        if (!pri && ValveEnabled)
        {
            var rate = RatePerSec(now);
            if (rate > ValveRatePerSec)
            {
                // 초과분 비율만큼 확률 셰딩 → 통과율 ≈ Valve / rate
                var keep = (uint)((ulong)ValveRatePerSec * uint.MaxValue / (ulong)rate);
                if (NextRand() > keep) { Interlocked.Increment(ref _shed); Interlocked.Increment(ref _droppedBy[(int)platform]); return false; }
            }
        }
        // 링 점유 하이워터 — 일반 메시지는 우선 메시지 여유분(25 %) 보존을 위해 테일 드롭(밸브 설정과 무관)
        if (!pri && Depth >= Capacity * HighWater) { Interlocked.Increment(ref _tail); Interlocked.Increment(ref _droppedBy[(int)platform]); return false; }
        // ② Vyukov 슬롯 예약
        long pos;
        int slot;
        while (true)
        {
            pos = Interlocked.Read(ref _enqueue);
            slot = (int)(pos & Mask);
            var seq = Volatile.Read(ref _slotSeq[slot]);
            var dif = seq - pos;
            if (dif == 0) { if (Interlocked.CompareExchange(ref _enqueue, pos + 1, pos) == pos) break; }
            else if (dif < 0)
            {
                if (pri) Interlocked.Increment(ref _priDrop); else Interlocked.Increment(ref _tail);
                Interlocked.Increment(ref _droppedBy[(int)platform]);
                return false;
            }
        }
        ref var r = ref _rec[slot];
        var c = _chars.AsSpan(slot * SlotChars, SlotChars);
        r.UserLen = (short)Normalize(user, c[..UserCap]);
        r.TextLen = (short)Normalize(text, c.Slice(UserCap, TextCap));
        r.UserHash = HashUser(c[..r.UserLen]);
        r.Platform = platform; r.Role = role; r.Amount = amount; r.AtMs = now;
        r.Seq = Interlocked.Increment(ref _seq);
        Volatile.Write(ref _slotSeq[slot], pos + 1);
        Interlocked.Increment(ref _accepted); Interlocked.Increment(ref _byPlatform[(int)platform]);
        if (pri) Interlocked.Increment(ref _priority);
        return true;
    }

    private uint NextRand()
    {
        uint x, y;
        do { x = Volatile.Read(ref _rng); y = x ^ (x << 13); y ^= y >> 17; y ^= y << 5; } while (Interlocked.CompareExchange(ref _rng, y, x) != x);
        return y;
    }

    /// <summary>정규화 복사 — 제어문자 · 제로폭(U+200B~200F · FEFF) 제거 · 공백류 → 단일 공백 · 앞뒤 공백 제거.</summary>
    public static int Normalize(ReadOnlySpan<char> s, Span<char> d)
    {
        var n = 0; var space = true;
        for (var i = 0; i < s.Length && n < d.Length; i++)
        {
            var ch = s[i];
            if (ch is >= '​' and <= '‏' or '﻿' or '⁠') continue;
            if (char.IsWhiteSpace(ch) || char.IsControl(ch)) { if (!space) { d[n++] = ' '; space = true; } continue; }
            if (char.IsHighSurrogate(ch) && n + 1 >= d.Length) break;   // 서로게이트 쌍 분할 방지
            d[n++] = ch; space = false;
        }
        if (n > 0 && d[n - 1] == ' ') n--;
        return n;
    }

    // ══════════════════════ 소비(단일 스레드 · 무할당) ══════════════════════
    public int Drain(IUnifiedChatConsumer consumer, int maxMessages, double budgetUs)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var limit = (long)(budgetUs * System.Diagnostics.Stopwatch.Frequency / 1e6);
        var n = 0;
        while (n < maxMessages)
        {
            var pos = _dequeue;
            var slot = (int)(pos & Mask);
            if (Volatile.Read(ref _slotSeq[slot]) != pos + 1) break;
            var c = _chars.AsSpan(slot * SlotChars, SlotChars);
            ref readonly var r = ref _rec[slot];
            try { consumer.Consume(in r, c[..r.UserLen], c.Slice(UserCap, r.TextLen)); }
            catch (Exception ex) { PromptFlowStudio.Program.Log("ChatAggregator.Consume", ex); }
            Volatile.Write(ref _slotSeq[slot], pos + Capacity);
            Interlocked.Exchange(ref _dequeue, pos + 1);
            n++;
            if ((n & 7) == 0 && System.Diagnostics.Stopwatch.GetTimestamp() - t0 > limit) break;   // 8 건마다 예산 확인
        }
        Drained += n;
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        LastDrainUs = us; if (us > MaxDrainUs && n > 0) MaxDrainUs = us;
        return n;
    }

    public void ResetMetrics()
    {
        Interlocked.Exchange(ref _offered, 0); Interlocked.Exchange(ref _accepted, 0); Interlocked.Exchange(ref _shed, 0); Interlocked.Exchange(ref _tail, 0);
        Interlocked.Exchange(ref _priDrop, 0); Interlocked.Exchange(ref _priority, 0);
        for (var i = 0; i < Platforms; i++) { Interlocked.Exchange(ref _byPlatform[i], 0); Interlocked.Exchange(ref _droppedBy[i], 0); }
        Drained = 0; MaxDrainUs = 0;
    }
}
