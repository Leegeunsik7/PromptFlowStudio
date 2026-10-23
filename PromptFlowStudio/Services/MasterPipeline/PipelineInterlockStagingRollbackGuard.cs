using System.Buffers;

namespace PromptFlowStudio.Services.MasterPipeline;

/// <summary>v17.92.0 인터락 차단 사유.</summary>
public enum InterlockReason : byte { None, ContentIdRisk, ExportFailed, StageFailed, Exception, Cancelled }

/// <summary>v17.92.0 스테이지 인터락 — 하류 실행 차단 신호(엔진이 잡아 롤백으로 전환).</summary>
public sealed class PipelineInterlockException : Exception
{
    public PipelineInterlockException(InterlockReason reason, string message) : base(message) => Reason = reason;
    public InterlockReason Reason { get; }
}

/// <summary>
/// [PromptFlow Studio v17.92.0] 🧯 파이프라인 인터락 &amp; 스테이징 롤백 세이프티 가드.
///   ▸ 스테이징 리스: 상류 스테이지 산출물 버퍼를 <see cref="ArrayPool{T}.Shared"/> 에서 대여해 고정 슬롯(16)에 등록 — 재실행 시 재대여(워밍업 후 무할당).
///   ▸ 트립: 예외 · Content ID 위험도 초과 등 → <see cref="Trip"/> 즉시 래치 → 엔진은 다음 스테이지 진입 전에 <see cref="IsTripped"/> 로 하류 차단.
///   ▸ 롤백: 모든 슬롯을 <see cref="Interlocked.Exchange{T}(ref T, T)"/> 로 원자 회수 → 풀 반환(중복 반환 0) · 회수 바이트/건수 누적 → OOM 방어.
/// </summary>
public sealed class PipelineInterlockStagingRollbackGuard
{
    public const int MaxLeases = 16;

    private readonly byte[]?[] _bytes = new byte[]?[MaxLeases];
    private readonly float[]?[] _floats = new float[]?[MaxLeases];
    private readonly char[]?[] _chars = new char[]?[MaxLeases];
    private int _tripped, _tripStage = -1;
    private long _outstandingBytes;

    public bool IsTripped => Volatile.Read(ref _tripped) != 0;
    public int TripStage => Volatile.Read(ref _tripStage);
    public InterlockReason Reason { get; private set; }
    public string? TripMessage { get; private set; }
    public long Trips { get; private set; }
    public long Rollbacks { get; private set; }
    public long ReclaimedBytes { get; private set; }
    public long ReclaimedLeases { get; private set; }
    public long TotalLeases { get; private set; }
    public long OutstandingBytes => Interlocked.Read(ref _outstandingBytes);

    public int OutstandingLeases
    {
        get
        {
            var n = 0;
            for (var i = 0; i < MaxLeases; i++)
            {
                if (Volatile.Read(ref _bytes[i]) is not null) n++;
                if (Volatile.Read(ref _floats[i]) is not null) n++;
                if (Volatile.Read(ref _chars[i]) is not null) n++;
            }
            return n;
        }
    }

    // ══════════════════════ 스테이징 리스 ══════════════════════

    public byte[] LeaseBytes(int minLength) => Lease(_bytes, minLength, 1);
    public float[] LeaseFloats(int minLength) => Lease(_floats, minLength, sizeof(float));
    public char[] LeaseChars(int minLength) => Lease(_chars, minLength, sizeof(char));

    private T[] Lease<T>(T[]?[] slots, int minLength, int unit)
    {
        if (IsTripped) throw new PipelineInterlockException(InterlockReason.StageFailed, "인터락 트립 상태 — 스테이징 대여 차단");
        var a = ArrayPool<T>.Shared.Rent(Math.Max(1, minLength));
        for (var i = 0; i < slots.Length; i++)
        {
            if (Interlocked.CompareExchange(ref slots[i], a, null) is not null) continue;
            Interlocked.Add(ref _outstandingBytes, (long)a.Length * unit);
            TotalLeases++;
            return a;
        }
        ArrayPool<T>.Shared.Return(a);
        throw new PipelineInterlockException(InterlockReason.StageFailed, "스테이징 슬롯 포화(" + MaxLeases + ")");
    }

    // ══════════════════════ 인터락 ══════════════════════

    /// <summary>트립 래치(최초 1 회만 사유 기록) — 이후 하류 스테이지 진입 · 신규 대여 차단.</summary>
    public bool Trip(int stage, InterlockReason reason, string? message)
    {
        if (Interlocked.CompareExchange(ref _tripped, 1, 0) != 0) return false;
        Volatile.Write(ref _tripStage, stage);
        Reason = reason;
        TripMessage = message;
        Trips++;
        return true;
    }

    /// <summary>원자 롤백 — 모든 스테이징 버퍼 회수 · 회수 바이트 반환(트립 래치는 유지).</summary>
    public long Rollback()
    {
        var bytes = ReturnAll(_bytes, 1) + ReturnAll(_floats, sizeof(float)) + ReturnAll(_chars, sizeof(char));
        Rollbacks++;
        ReclaimedBytes += bytes;
        return bytes;
    }

    /// <summary>정상 종료 회수(롤백 카운트 없음) — 다음 실행 전 · Dispose.</summary>
    public long ReleaseAll() => ReturnAll(_bytes, 1) + ReturnAll(_floats, sizeof(float)) + ReturnAll(_chars, sizeof(char));

    /// <summary>다음 실행 무장 — 스테이징 회수 + 래치 해제.</summary>
    public void Arm()
    {
        ReleaseAll();
        Reason = InterlockReason.None;
        TripMessage = null;
        Volatile.Write(ref _tripStage, -1);
        Volatile.Write(ref _tripped, 0);
    }

    private long ReturnAll<T>(T[]?[] slots, int unit)
    {
        long bytes = 0;
        for (var i = 0; i < slots.Length; i++)
        {
            var a = Interlocked.Exchange(ref slots[i], null);
            if (a is null) continue;
            bytes += (long)a.Length * unit;
            ReclaimedLeases++;
            ArrayPool<T>.Shared.Return(a);
        }
        Interlocked.Add(ref _outstandingBytes, -bytes);
        return bytes;
    }
}
