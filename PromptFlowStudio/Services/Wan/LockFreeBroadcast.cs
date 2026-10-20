using System;
using System.Threading;
using System.Threading.Channels;

namespace PromptFlowStudio.Services.Wan;

/// <summary>Key Vault 변경 신호 종류.</summary>
public enum VaultSignalKind : byte { KeysChanged, Persisted }

/// <summary>Key Vault 신호(값 형식 · 박싱 0) — KeysChanged 는 <see cref="Provider"/>, Persisted 는 <see cref="Ok"/>.</summary>
public readonly record struct VaultSignal(VaultSignalKind Kind, string? Provider, bool Ok);

/// <summary>
/// [PromptFlowStudio] v15.15.0 Lock-Free 발행-구독 허브.
///   ▸ 동기 핸들러: 카피온라이트 <c>Action&lt;T&gt;[]</c> 스냅샷(CAS 교체) — 발행 순회 0B · 구독자 예외 격리(<c>GetInvocationList</c> 할당 제거).
///   ▸ 비동기 구독: 구독자별 <see cref="Channel"/> BoundedChannel(DropOldest · SingleReader) — 발행자는 <c>TryWrite</c> 만 하고 즉시 반환(UI 블로킹 0).
///     느린 소비자는 오래된 신호부터 버려 최신 상태로 수렴(백프레셔 대기 금지). 구독 토큰 취소 시 자동 해제 + <c>Complete</c>.
/// </summary>
public sealed class LockFreeBroadcast<T>
{
    private Action<T>[] _handlers = Array.Empty<Action<T>>();
    private ChannelWriter<T>[] _writers = Array.Empty<ChannelWriter<T>>();
    private long _faults;

    /// <summary>격리된 구독자 예외 누적.</summary>
    public long Faults => Interlocked.Read(ref _faults);

    /// <summary>현재 비동기(Channel) 구독자 수.</summary>
    public int ChannelSubscribers => Volatile.Read(ref _writers).Length;

    public void Add(Action<T>? handler)
    {
        if (handler is not null) Swap(ref _handlers, handler, add: true);
    }

    public void Remove(Action<T>? handler)
    {
        if (handler is not null) Swap(ref _handlers, handler, add: false);
    }

    /// <summary>비동기 구독 — <paramref name="ct"/> 취소 시 해제(리더 완료). 용량 초과 시 가장 오래된 신호부터 드롭.</summary>
    public ChannelReader<T> Subscribe(CancellationToken ct, int capacity = 64)
    {
        var ch = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,   // 발행 스레드에서 소비자 연속 실행 금지
        });
        Swap(ref _writers, ch.Writer, add: true);
        if (ct.CanBeCanceled)
        {
            ct.Register(static s =>
            {
                var (hub, w) = ((LockFreeBroadcast<T>, ChannelWriter<T>))s!;
                hub.Unsubscribe(w);
            }, (this, ch.Writer));
        }
        return ch.Reader;
    }

    private void Unsubscribe(ChannelWriter<T> writer)
    {
        Swap(ref _writers, writer, add: false);
        writer.TryComplete();
    }

    /// <summary>발행(0B · 구독자 없으면 즉시 반환). 동기 핸들러 예외는 격리 후 <see cref="Faults"/> 증가.</summary>
    public void Publish(T value)
    {
        var hs = Volatile.Read(ref _handlers);
        for (int i = 0; i < hs.Length; i++)
        {
            try { hs[i](value); }
            catch { Interlocked.Increment(ref _faults); }
        }
        var ws = Volatile.Read(ref _writers);
        for (int i = 0; i < ws.Length; i++) ws[i].TryWrite(value);
    }

    // 카피온라이트 CAS 교체 — 추가는 꼬리, 제거는 마지막 일치 1개(event 의 -= 의미와 동일).
    private static void Swap<TItem>(ref TItem[] field, TItem item, bool add) where TItem : class
    {
        var spin = new SpinWait();
        while (true)
        {
            var cur = Volatile.Read(ref field);
            TItem[] next;
            if (add)
            {
                next = new TItem[cur.Length + 1];
                Array.Copy(cur, next, cur.Length);
                next[cur.Length] = item;
            }
            else
            {
                int at = -1;
                for (int i = cur.Length - 1; i >= 0; i--)
                    if (Equals(cur[i], item)) { at = i; break; }
                if (at < 0) return;
                if (cur.Length == 1) next = Array.Empty<TItem>();
                else
                {
                    next = new TItem[cur.Length - 1];
                    Array.Copy(cur, 0, next, 0, at);
                    Array.Copy(cur, at + 1, next, at, cur.Length - at - 1);
                }
            }
            if (ReferenceEquals(Interlocked.CompareExchange(ref field, next, cur), cur)) return;
            spin.SpinOnce();
        }
    }
}
