using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace PromptFlowStudio.Services.AI.Wan;

/// <summary>공유메모리 프레임 픽셀 포맷.</summary>
public enum VirtualCamPixelFormat { Rgba32 = 0, Nv12 = 1 }

/// <summary>가장 최근 브로드캐스트 프레임 메타(HUD 표시용 — 픽셀 바이트 자체는 포함하지 않음).</summary>
/// <summary>v13.0.0: <see cref="VirtualCamFrameInfo"/> 의 값 형식 대응(무할당 읽기 경로 전용).</summary>
public readonly record struct VirtualCamFrameHeader(long Sequence, int Width, int Height, VirtualCamPixelFormat Format, long TimestampTicks, int PayloadBytes);

public sealed record VirtualCamFrameInfo(long Sequence, int Width, int Height, VirtualCamPixelFormat Format, long TimestampTicks, int PayloadBytes);

/// <summary>v15.1.0: 스테이징 슬롯 메타(값 형식).</summary>
public readonly record struct StagedFrameInfo(long Sequence, int Width, int Height, VirtualCamPixelFormat Format, int Length);

/// <summary>
/// [v15.1.0 · WAN-OPT-3] Wan 렌더러(생산) ↔ 가상카메라 MMF 쓰기(소비) 락-프리 더블 버퍼링 핸드오프.
///   ▸ 생산측 back·소비측 front 두 버퍼에 교환용 middle 슬롯 1개를 더한 3슬롯 구성 — 순수 2슬롯은 소비자가 front 를 읽는 동안
///     생산자가 다음 back 을 확보하려면 대기가 불가피하다. 교환 슬롯 인덱스+Dirty 비트를 int 1개(<c>_state</c>)에 담아
///     <see cref="Interlocked.Exchange(ref int, int)"/> 1회로 스왑하므로 양측 모두 대기 0(블로킹·스핀·락 없음).
///   ▸ 소비자는 항상 "가장 최근 게시본"만 가져가며(소비 전 덮어씀 = 백프레셔 드롭으로 집계), 시퀀스는 단조 증가 — 역행 시
///     <see cref="StepRegressions"/> 증가(정상 동작에선 항상 0, 테스트로 잠금).
///   ▸ 단일 생산자·단일 소비자 계약을 CAS 소유 플래그로 강제 — 동시 2번째 호출은 대기 대신 즉시 false(<see cref="Contended"/>).
///   ▸ 슬롯은 해상도 상향 시에만 1회 성장 — 정상상태 할당 0.
/// </summary>
public sealed class WanFrameDoubleBuffer
{
    private const int IndexMask = 0x3;
    private const int DirtyBit = 0x4;
    private readonly byte[][] _slots = new byte[3][];
    private readonly StagedFrameInfo[] _info = new StagedFrameInfo[3];
    private int _state = 2;     // middle 슬롯 인덱스 | DirtyBit
    private int _back;          // 생산자 소유
    private int _front = 1;     // 소비자 소유
    private long _publishSeq;   // 생산자 소유
    private long _lastConsumedSeq;
    private int _producerBusy, _consumerBusy;
    private long _published, _consumed, _overwritten, _contended, _regressions;

    public WanFrameDoubleBuffer(int initialSlotBytes = 128 * 128 * 4)
    {
        for (var i = 0; i < 3; i++) _slots[i] = new byte[Math.Max(0, initialSlotBytes)];
    }

    public long Published => Interlocked.Read(ref _published);
    public long Consumed => Interlocked.Read(ref _consumed);
    /// <summary>소비 전 새 프레임으로 덮어써진 수(백프레셔 드롭).</summary>
    public long Overwritten => Interlocked.Read(ref _overwritten);
    public long Contended => Interlocked.Read(ref _contended);
    public long StepRegressions => Interlocked.Read(ref _regressions);
    public bool HasPending => (Volatile.Read(ref _state) & DirtyBit) != 0;

    /// <summary>생산측: back 슬롯에 복사 후 middle 과 원자 교환(게시). 대기 없음.</summary>
    public bool TryPublish(ReadOnlySpan<byte> data, int width, int height, VirtualCamPixelFormat format, out bool overwroteUnconsumed)
    {
        overwroteUnconsumed = false;
        if (Interlocked.CompareExchange(ref _producerBusy, 1, 0) != 0) { Interlocked.Increment(ref _contended); return false; }
        try
        {
            var b = _back;
            if (_slots[b].Length < data.Length) _slots[b] = new byte[data.Length];
            data.CopyTo(_slots[b]);
            _info[b] = new StagedFrameInfo(++_publishSeq, width, height, format, data.Length);
            // Exchange 는 전체 펜스 — 슬롯·메타 기록이 게시보다 먼저 보인다.
            var old = Interlocked.Exchange(ref _state, b | DirtyBit);
            _back = old & IndexMask;
            overwroteUnconsumed = (old & DirtyBit) != 0;
            if (overwroteUnconsumed) Interlocked.Increment(ref _overwritten);
            Interlocked.Increment(ref _published);
            return true;
        }
        finally { Volatile.Write(ref _producerBusy, 0); }
    }

    /// <summary>소비측: 새 게시본이 있으면 front 와 원자 교환 후 그 슬롯을 노출. true 면 반드시 <see cref="EndConsume"/> 호출
    ///   (그 전까지 front 슬롯은 소비자 독점 — 생산자는 다른 두 슬롯만 사용).</summary>
    public bool TryBeginConsume(out ReadOnlySpan<byte> frame, out StagedFrameInfo info)
    {
        frame = default; info = default;
        if (Interlocked.CompareExchange(ref _consumerBusy, 1, 0) != 0) { Interlocked.Increment(ref _contended); return false; }
        if ((Volatile.Read(ref _state) & DirtyBit) == 0) { Volatile.Write(ref _consumerBusy, 0); return false; }
        var old = Interlocked.Exchange(ref _state, _front);
        _front = old & IndexMask;
        info = _info[_front];
        if (info.Sequence <= _lastConsumedSeq) Interlocked.Increment(ref _regressions);
        _lastConsumedSeq = info.Sequence;
        frame = _slots[_front].AsSpan(0, info.Length);
        Interlocked.Increment(ref _consumed);
        return true;
    }

    public void EndConsume() => Volatile.Write(ref _consumerBusy, 0);
}

/// <summary>
/// [PromptFlow Studio v11.1.0] Win32 공유메모리(MemoryMappedFile+Mutex) 가상 카메라 프레임 브로드캐스터.
///   ▸ ⚠ 정직한 스코프: 이것은 NDI 프로토콜(NewTek/Vizrt 독점 네트워크 프로토콜 — 별도 SDK 필요·외부 NuGet 없이는
///     재구현 불가)이 아니다. 실제로는 "Win32 Shared Memory 가상 카메라 파이프"(지시서 요구사항 본문)이며,
///     OBS 자체 VirtualCam 드라이버(서명된 DirectShow/Media Foundation 커널 인접 컴포넌트)와 직접 연동하려면
///     OBS 쪽에 대응 리더 플러그인이 별도로 필요하다 — 이 클래스는 그 프로듀서(브로드캐스터) 측만 구현한다.
///   ▸ 와이어 포맷(36바이트 헤더 + 페이로드, C-ABI 명세는 <see cref="VirtualCamPipeNativeSpec"/> 참고): Magic(uint4)·
///     Width(int4)·Height(int4)·FrameIndex(long8)·Timestamp(long8)·PixelFormat(int4)·PayloadBytes(int4) —
///     리틀엔디안 고정, <see cref="BinaryPrimitives"/> 로 기록.
///   ▸ Zero-Alloc: 헤더는 stackalloc, 페이로드는 호출측이 준 <see cref="ReadOnlySpan{T}"/> 를 재사용 스트림에
///     그대로 씀(중간 배열 할당 없음). 상한(<see cref="MaxPayloadBytes"/>, 1920×1080 RGBA)을 넘는 프레임은
///     예외 없이 Graceful 드롭(카운터만 증가).
///   ▸ Mutex 타임아웃(리더가 오래 점유) 시에도 Graceful 드롭 — 라이터가 블로킹되어 상위 파이프라인을 막지 않는다.
///     <see cref="AbandonedMutexException"/>(이전 프로세스 비정상 종료)은 자가치유로 그대로 계속 진행.
/// </summary>
public sealed class LiveStreamVirtualCamPipe : IDisposable
{
    public const string DefaultMapName = "Local\\PromptFlowStudio_VirtualCam_Frame_v1";
    public const string DefaultMutexName = "Local\\PromptFlowStudio_VirtualCam_Mutex_v1";
    /// <summary>고정 매직 넘버(리틀엔디안 4바이트 메모리상 ASCII "PFSV") - 헤더 최선두, C/C++ 리더가 파싱 전 포맷 식별/검증용.</summary>
    public const uint MagicValue = 0x56534650u;
    internal const int HeaderBytes = 36;   // Magic(4)+Width(4)+Height(4)+FrameIndex(8)+Timestamp(8)+PixelFormat(4)+PayloadBytes(4)
    public const int MaxPayloadBytes = 1920 * 1080 * 4;   // 1080p RGBA 상한(≈7.9MB)

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewStream _stream;
    private readonly Mutex _mutex;
    private readonly object _gate = new();
    private long _sequence;
    private long _framesSubmitted, _framesDropped, _mutexTimeouts;
    private double _lastElapsedMs;
    // v12.2.0 PERF-2: 임계구역에선 값 메타만 기록 - VirtualCamFrameInfo 레코드/LastAction 문자열은 조회 시 지연 생성(60FPS 프레임당 할당 2건 제거).
    private readonly record struct FrameMeta(long Seq, int Width, int Height, VirtualCamPixelFormat Format, long Ts, int Bytes, bool Abandoned);
    private FrameMeta _meta;
    private bool _hasMeta;
    private VirtualCamFrameInfo? _infoCache;
    private bool _actionIsFrame;
    private string? _lastAction;
    private bool _disposed;

    public event Action? OnChange;
    public string? LastAction
    {
        get
        {
            lock (_gate)
            {
                if (_actionIsFrame)
                {
                    var m = _meta;
                    _lastAction = $"프레임 #{m.Seq} - {m.Width}x{m.Height} {m.Format} - {m.Bytes:N0}B - {_lastElapsedMs:0.###}ms" + (m.Abandoned ? " (Abandoned Mutex 복구 후 덮어쓰기)" : "");
                    _actionIsFrame = false;
                }
                return _lastAction;
            }
        }
        private set { lock (_gate) { _lastAction = value; _actionIsFrame = false; } }
    }
    public long FramesSubmitted => Interlocked.Read(ref _framesSubmitted);
    public long FramesDropped => Interlocked.Read(ref _framesDropped);
    public long MutexTimeouts => Interlocked.Read(ref _mutexTimeouts);
    public VirtualCamFrameInfo? LastFrameInfo
    {
        get
        {
            lock (_gate)
            {
                if (!_hasMeta) return null;
                var m = _meta;
                return _infoCache ??= new VirtualCamFrameInfo(m.Seq, m.Width, m.Height, m.Format, m.Ts, m.Bytes);
            }
        }
    }
    public double LastElapsedMs { get { lock (_gate) return _lastElapsedMs; } }
    /// <summary>throughput 기반 순간 추정치(fps) — 실 카메라 주사율 측정이 아니라 SubmitFrame 소요시간의 역수(정직 표기).</summary>
    public double EstimatedFps { get { lock (_gate) return _lastElapsedMs > 0 ? Math.Round(1000.0 / _lastElapsedMs, 1) : 0.0; } }
    /// <summary>공유메모리 페이로드 점유율(%) — 최근 프레임 바이트 / <see cref="MaxPayloadBytes"/>.</summary>
    public double OccupancyPct { get { lock (_gate) return _hasMeta ? Math.Round(_meta.Bytes * 100.0 / MaxPayloadBytes, 1) : 0.0; } }

    /// <summary>v12.2.0: AbandonedMutexException(이전 소유 스레드/프로세스가 해제 없이 종료)을 감지·복구한 횟수.</summary>
    public long AbandonedRecoveries => Interlocked.Read(ref _abandonedRecoveries);
    private long _abandonedRecoveries;

    private readonly TimeSpan _acquireTimeout = TimeSpan.FromMilliseconds(50);

    /// <summary>테스트 전용: Mutex 취득 한도 주입. Abandoned 판정은 소유 OS 스레드가 실제 종료된 시점에 커널이 내리므로(Thread.Join 반환과
    ///   비동기), 부하 시 50ms 한도가 먼저 만료되는 경합 없이 "Abandoned 신호로 대기가 풀리는" 경로를 결정적으로 검증하기 위함.</summary>
    internal LiveStreamVirtualCamPipe(string mapName, string mutexName, TimeSpan acquireTimeout) : this(mapName, mutexName)
        => _acquireTimeout = acquireTimeout;

    public LiveStreamVirtualCamPipe(string? mapName = null, string? mutexName = null)
    {
        // v12.2.0: 생성 도중 실패(권한/용량/이름 충돌) 시 앞서 연 커널 핸들을 즉시 반환 - 이전엔 필드 대입 전 예외로 MMF 가 GC 종료자까지 잔류.
        MemoryMappedFile? mmf = null;
        MemoryMappedViewStream? stream = null;
        try
        {
            mmf = MemoryMappedFile.CreateOrOpen(mapName ?? DefaultMapName, HeaderBytes + MaxPayloadBytes, MemoryMappedFileAccess.ReadWrite);
            stream = mmf.CreateViewStream(0, HeaderBytes + MaxPayloadBytes, MemoryMappedFileAccess.ReadWrite);
            _mutex = new Mutex(false, mutexName ?? DefaultMutexName);
        }
        catch
        {
            stream?.Dispose();
            mmf?.Dispose();
            throw;
        }
        _mmf = mmf;
        _stream = stream;
    }

    /// <summary>50ms 한도로 Mutex 취득. Abandoned 는 소유권이 이미 이 스레드로 넘어온 상태(=취득 성공)이므로 true + abandoned 플래그.
    ///   Dispose 이후 호출은 false(예외 대신 Graceful 드롭).</summary>
    private bool TryAcquire(out bool abandoned)
    {
        abandoned = false;
        if (Volatile.Read(ref _disposed)) return false;
        try { return _mutex.WaitOne(_acquireTimeout); }
        catch (AbandonedMutexException)
        {
            abandoned = true;
            Interlocked.Increment(ref _abandonedRecoveries);
            return true;
        }
        catch (ObjectDisposedException) { return false; }
    }

    // 헤더 Magic 만 0 으로 지워 "프레임 없음" 상태로 되돌린다(Mutex 보유 중에만 호출).
    private void InvalidateHeaderLocked()
    {
        Span<byte> zero = stackalloc byte[4];
        zero.Clear();
        _stream.Position = 0;
        _stream.Write(zero);
    }

    // ── v12.2.0-Phase4: 리액션 오버레이 종단간 결속 ─────────────────────────────────────────────
    private IVirtualCamOverlay? _overlay;
    private long _overlayComposited;

    /// <summary>오버레이가 실제로 합성되어 송출된 프레임 수.</summary>
    public long OverlayFramesComposited => Interlocked.Read(ref _overlayComposited);
    public bool OverlayAttached => Volatile.Read(ref _overlay) is not null;

    /// <summary>송출 직전 합성할 오버레이 연결(null = 해제). 참조 교체 1회(Volatile) - 송출 스레드와 락 경합 없음.</summary>
    public void AttachOverlay(IVirtualCamOverlay? overlay) => Volatile.Write(ref _overlay, overlay);

    /// <summary>프레임 1건 브로드캐스트. 성공 true, 상한 초과/Mutex 타임아웃은 false(예외 없이 드롭 + 카운터).
    ///   v12.2.0-Phase4: 오버레이가 연결돼 있고 그릴 내용이 있을 때만(RGBA32·정확한 w*h*4 길이) ArrayPool 스크래치에 원본을 복사해
    ///   파티클을 가산 합성한 뒤 송출한다 - 그 외 전부(미연결/빈 오버레이/NV12)는 기존 경로 그대로(참조 1회 읽기 + 분기만, 복사 0).
    ///   합성은 Mutex 취득 이전에 끝나므로 공유메모리 임계구역 길이는 오버레이 유무와 무관.</summary>
    public bool SubmitFrame(ReadOnlySpan<byte> pixelData, int width, int height, VirtualCamPixelFormat format)
    {
        var overlay = Volatile.Read(ref _overlay);
        if (overlay is null || format != VirtualCamPixelFormat.Rgba32 || width <= 0 || height <= 0
            || pixelData.Length != width * height * 4 || pixelData.Length > MaxPayloadBytes || !overlay.HasVisibleContent)
            return SubmitCore(pixelData, width, height, format);   // Passthrough fast-path

        var scratch = ArrayPool<byte>.Shared.Rent(pixelData.Length);
        try
        {
            var composed = scratch.AsSpan(0, pixelData.Length);
            pixelData.CopyTo(composed);
            try
            {
                if (overlay.RenderOverlayInto(composed, width, height) > 0) Interlocked.Increment(ref _overlayComposited);
            }
            catch (Exception ex)
            {
                // 오버레이 결함이 송출을 막지 않게 - 원본 프레임으로 즉시 폴백.
                Program.Log("VirtualCam.Overlay", ex);
                return SubmitCore(pixelData, width, height, format);
            }
            return SubmitCore(composed, width, height, format);
        }
        finally { ArrayPool<byte>.Shared.Return(scratch); }
    }

    private bool SubmitCore(ReadOnlySpan<byte> pixelData, int width, int height, VirtualCamPixelFormat format)
    {
        if (pixelData.Length > MaxPayloadBytes)
        {
            Interlocked.Increment(ref _framesDropped);
            lock (_gate) LastAction = $"드롭 - 상한({MaxPayloadBytes:N0}B) 초과: {pixelData.Length:N0}B";
            OnChange?.Invoke();
            return false;
        }

        var acquired = TryAcquire(out var abandoned);

        if (!acquired)
        {
            Interlocked.Increment(ref _mutexTimeouts);
            Interlocked.Increment(ref _framesDropped);
            lock (_gate) LastAction = "드롭 - Mutex 대기 타임아웃(리더 점유 중)";
            OnChange?.Invoke();
            return false;
        }

        long seq;
        long ts;
        double elapsedMs;
        try
        {
            using var gcGuard = PromptFlowStudio.Services.Diagnostics.ZeroGcRegionScope.Enter("VirtualCam.SubmitCore");   // v12.3.0: MMF 블릿 임계구역 할당 회귀 검출
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();   // v12.2.0: Stopwatch 인스턴스 할당 제거
            seq = Interlocked.Increment(ref _sequence);
            ts = DateTimeOffset.UtcNow.Ticks;

            Span<byte> header = stackalloc byte[HeaderBytes];
            BinaryPrimitives.WriteUInt32LittleEndian(header[0..4], MagicValue);
            BinaryPrimitives.WriteInt32LittleEndian(header[4..8], width);
            BinaryPrimitives.WriteInt32LittleEndian(header[8..12], height);
            BinaryPrimitives.WriteInt64LittleEndian(header[12..20], seq);
            BinaryPrimitives.WriteInt64LittleEndian(header[20..28], ts);
            BinaryPrimitives.WriteInt32LittleEndian(header[28..32], (int)format);
            BinaryPrimitives.WriteInt32LittleEndian(header[32..36], pixelData.Length);

            // v12.2.0: Magic 을 마지막에 기록(커밋 마커) - 쓰기 도중 프로세스가 죽어 Mutex 가 Abandoned 되더라도 반쪽 프레임은
            //   Magic=0 으로 남아 리더가 절대 유효 프레임으로 오인하지 않는다(이전엔 헤더를 먼저 완결 기록해 찢어진 페이로드 노출 가능).
            BinaryPrimitives.WriteUInt32LittleEndian(header[0..4], 0u);
            _stream.Position = 0;
            _stream.Write(header);
            _stream.Write(pixelData);
            _stream.Position = 0;
            Span<byte> magic = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(magic, MagicValue);
            _stream.Write(magic);
            // v12.2.0: _stream.Flush() 제거 - 페이지파일 기반 공유메모리는 프로세스 간 같은 물리 페이지라 FlushViewOfFile(디스크 기록용)이
            //   불필요했다(프레임마다 7.9MB 뷰 대상 syscall). 가시성은 ReleaseMutex 의 전체 메모리 배리어가 보장한다.
            elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
        finally { _mutex.ReleaseMutex(); }

        Interlocked.Increment(ref _framesSubmitted);
        lock (_gate)   // 값 필드 7개 대입뿐인 최소 임계구역(할당·포맷팅 없음)
        {
            _meta = new FrameMeta(seq, width, height, format, ts, pixelData.Length, abandoned);
            _hasMeta = true;
            _infoCache = null;
            _lastElapsedMs = elapsedMs;
            _actionIsFrame = true;
        }
        OnChange?.Invoke();
        return true;
    }

    // ── v15.1.0 WAN-OPT-3: 렌더러↔MMF 쓰기 스레드 격리(락-프리 스테이징 + 전용 송출 펌프) ─────────────────────
    private readonly WanFrameDoubleBuffer _staging = new();
    private WanQualitySpeedGovernor? _governor;
    private readonly ManualResetEventSlim _pumpSignal = new(false);
    private readonly ManualResetEventSlim _pumpStopEvent = new(false);
    private Thread? _pumpThread;
    private int _pumpFps;
    private int _pumpTickBusy;
    // 페이스드(보간) 모드 키프레임 상태 — 펌프 소비자 전용(_pumpTickBusy 로 단일 실행 보장).
    private byte[] _keyPrev = Array.Empty<byte>(), _keyCurr = Array.Empty<byte>(), _pumpOut = Array.Empty<byte>();
    private StagedFrameInfo _keyPrevInfo, _keyCurrInfo;
    private long _keyArrival, _keyInterval, _keyCount;
    private bool _finalEmitted;
    private long _interpolatedEmits;

    public WanFrameDoubleBuffer Staging => _staging;
    public bool PumpRunning => Volatile.Read(ref _pumpThread) is not null;
    /// <summary>페이스드 펌프가 키프레임 사이 위상 보간 프레임을 송출한 수.</summary>
    public long InterpolatedEmits => Interlocked.Read(ref _interpolatedEmits);

    /// <summary>스테이징 드롭/덮어씀을 백프레셔 신호로 거버너에 보고(null = 해제).</summary>
    public void AttachGovernor(WanQualitySpeedGovernor? governor) => Volatile.Write(ref _governor, governor);

    /// <summary>생산측(Wan 렌더러 스레드): 스테이징에 게시만 하고 즉시 반환 — Mutex·MMF 대기 0. 실제 송출은 펌프 스레드가 수행.</summary>
    public bool StageFrame(ReadOnlySpan<byte> pixelData, int width, int height, VirtualCamPixelFormat format)
    {
        if (Volatile.Read(ref _disposed) || pixelData.Length > MaxPayloadBytes)
        {
            Interlocked.Increment(ref _framesDropped);
            return false;
        }
        var ok = _staging.TryPublish(pixelData, width, height, format, out var overwrote);
        Volatile.Read(ref _governor)?.RecordFrame(!ok || overwrote);
        if (ok) { try { _pumpSignal.Set(); } catch (ObjectDisposedException) { /* Dispose 경합 - 무시 */ } }
        return ok;
    }

    /// <summary>소비측 1회(패스스루): 최신 스테이징 프레임을 그대로 MMF 로 송출. 새 프레임이 없으면 false.</summary>
    public bool PumpStagedFrame()
    {
        if (!_staging.TryBeginConsume(out var frame, out var info)) return false;
        try { return SubmitFrame(frame, info.Width, info.Height, info.Format); }
        finally { _staging.EndConsume(); }
    }

    /// <summary>소비측 1틱(페이스드): 새 키프레임이 있으면 흡수한 뒤, 직전↔최신 키프레임을 경과 위상으로 SIMD 보간
    ///   (<see cref="WanPostProcessingEngine.BlendWeightedInto"/>)해 송출. 위상 256(최신 그대로)은 키프레임당 1회만 송출하고 이후 홀드.
    ///   <paramref name="nowTimestamp"/> 는 <see cref="Stopwatch.GetTimestamp"/> 단위(테스트 결정성 위해 주입).</summary>
    internal bool PumpPacedTick(long nowTimestamp)
    {
        if (Interlocked.CompareExchange(ref _pumpTickBusy, 1, 0) != 0) return false;
        try
        {
            if (_staging.TryBeginConsume(out var frame, out var info))
            {
                try
                {
                    (_keyPrev, _keyCurr) = (_keyCurr, _keyPrev);
                    _keyPrevInfo = _keyCurrInfo;
                    if (_keyCurr.Length < frame.Length) _keyCurr = new byte[frame.Length];
                    frame.CopyTo(_keyCurr);
                    _keyCurrInfo = info;
                }
                finally { _staging.EndConsume(); }
                if (_keyCount > 0)
                {
                    var d = nowTimestamp - _keyArrival;
                    _keyInterval = _keyInterval == 0 ? d : (_keyInterval * 3 + d) / 4;
                }
                _keyArrival = nowTimestamp;
                _keyCount++;
                _finalEmitted = false;
            }
            if (_keyCount == 0 || _finalEmitted) return false;

            var cur = _keyCurrInfo;
            var p = _keyPrevInfo;
            var blendable = _keyCount >= 2 && p.Length == cur.Length && p.Width == cur.Width && p.Height == cur.Height && p.Format == cur.Format;
            var phase = blendable ? WanPostProcessingEngine.ComputePhase256(nowTimestamp - _keyArrival, _keyInterval) : 256;
            if (phase >= 256)
            {
                _finalEmitted = true;
                return SubmitFrame(_keyCurr.AsSpan(0, cur.Length), cur.Width, cur.Height, cur.Format);
            }
            if (_pumpOut.Length < cur.Length) _pumpOut = new byte[cur.Length];
            var outSpan = _pumpOut.AsSpan(0, cur.Length);
            WanPostProcessingEngine.BlendWeightedInto(_keyPrev.AsSpan(0, cur.Length), _keyCurr.AsSpan(0, cur.Length), outSpan, phase);
            Interlocked.Increment(ref _interpolatedEmits);
            return SubmitFrame(outSpan, cur.Width, cur.Height, cur.Format);
        }
        finally { Volatile.Write(ref _pumpTickBusy, 0); }
    }

    /// <summary>전용 송출 스레드 기동. targetFps ≤ 0: 게시 신호 구동 패스스루, &gt; 0: 고정 주기(예: 60) 위상 보간 송출.
    ///   이미 실행 중이거나 Dispose 이후면 false.</summary>
    public bool StartPump(int targetFps = 0)
    {
        if (Volatile.Read(ref _disposed)) return false;
        var t = new Thread(PumpLoop) { IsBackground = true, Name = "VirtualCam.Pump" };
        if (Interlocked.CompareExchange(ref _pumpThread, t, null) is not null) return false;
        _pumpFps = Math.Clamp(targetFps, 0, 240);
        _pumpStopEvent.Reset();
        t.Start();
        return true;
    }

    public void StopPump()
    {
        var t = Interlocked.Exchange(ref _pumpThread, null);
        if (t is null) return;
        _pumpStopEvent.Set();
        _pumpSignal.Set();
        if (t != Thread.CurrentThread) t.Join(TimeSpan.FromSeconds(1));
    }

    private void PumpLoop()
    {
        try { PumpLoopCore(); }
        catch (ObjectDisposedException) { /* Join 한도 초과 후 Dispose 경합 - 스레드 조용히 종료 */ }
    }

    private void PumpLoopCore()
    {
        var fps = _pumpFps;
        if (fps <= 0)
        {
            while (!_pumpStopEvent.IsSet)
            {
                _pumpSignal.Wait(100);
                _pumpSignal.Reset();   // Reset 후 소비 — 그 사이 게시는 HasPending 으로 이번 회차에 흡수(신호 유실 없음)
                if (_pumpStopEvent.IsSet) break;
                try { PumpStagedFrame(); } catch (Exception ex) { Program.Log("VirtualCam.Pump", ex); }
            }
            return;
        }

        var period = Stopwatch.Frequency / fps;
        var next = Stopwatch.GetTimestamp() + period;
        while (!_pumpStopEvent.IsSet)
        {
            var now = Stopwatch.GetTimestamp();
            var waitMs = (int)((next - now) * 1000 / Stopwatch.Frequency);
            if (waitMs > 0 && _pumpStopEvent.Wait(waitMs)) break;
            now = Stopwatch.GetTimestamp();
            next += period;
            if (next <= now) next = now + period;   // 지연 누적 시 버스트 따라잡기 금지
            try { PumpPacedTick(now); } catch (Exception ex) { Program.Log("VirtualCam.Pump", ex); }
        }
    }

    /// <summary>독립 리더(다른 프로세스/테스트)가 현재 공유메모리 내용을 읽는다 — SubmitFrame 과 동일 Mutex 로 보호.</summary>
    public bool TryReadLatestFrame(Span<byte> destination, out VirtualCamFrameInfo? info, out int bytesRead)
    {
        info = null;
        if (!TryReadLatestFrameRaw(destination, out var h, out bytesRead)) return false;
        info = new VirtualCamFrameInfo(h.Sequence, h.Width, h.Height, h.Format, h.TimestampTicks, h.PayloadBytes);
        return true;
    }

    /// <summary>v13.0.0: 값 형식 헤더로 반환하는 무할당 읽기(1FPS 비전 샘플러 등 상시 폴링용) - 의미·락 규약은 <see cref="TryReadLatestFrame"/> 과 동일.</summary>
    public bool TryReadLatestFrameRaw(Span<byte> destination, out VirtualCamFrameHeader header, out int bytesRead)
    {
        header = default; bytesRead = 0;
        // v12.2.0: 리더 경로는 AbandonedMutexException 을 처리하지 않아 이전 소유자 비정상 종료 시 예외가 그대로 전파됐다.
        if (!TryAcquire(out var abandoned)) return false;
        try
        {
            if (abandoned)
            {
                // 이전 소유자가 쓰기 도중 죽었을 수 있음 - 현재 내용을 신뢰하지 않고 무효화(다음 SubmitFrame 이 새로 커밋).
                InvalidateHeaderLocked();
                lock (_gate) LastAction = "Abandoned Mutex 복구 - 공유메모리 헤더 무효화(찢어진 프레임 차단)";
                OnChange?.Invoke();
                return false;
            }

            Span<byte> hdr = stackalloc byte[HeaderBytes];
            _stream.Position = 0;
            var read = _stream.Read(hdr);
            if (read < HeaderBytes) return false;

            var magic = BinaryPrimitives.ReadUInt32LittleEndian(hdr[0..4]);
            if (magic != MagicValue) return false;   // 아직 프레임 없음(전부 0) 또는 손상 - 안전하게 거부

            var width = BinaryPrimitives.ReadInt32LittleEndian(hdr[4..8]);
            var height = BinaryPrimitives.ReadInt32LittleEndian(hdr[8..12]);
            var seq = BinaryPrimitives.ReadInt64LittleEndian(hdr[12..20]);
            var ts = BinaryPrimitives.ReadInt64LittleEndian(hdr[20..28]);
            var format = (VirtualCamPixelFormat)BinaryPrimitives.ReadInt32LittleEndian(hdr[28..32]);
            var payloadLen = BinaryPrimitives.ReadInt32LittleEndian(hdr[32..36]);
            if (payloadLen <= 0 || payloadLen > MaxPayloadBytes) return false;

            var take = Math.Min(payloadLen, destination.Length);
            bytesRead = _stream.Read(destination[..take]);
            header = new VirtualCamFrameHeader(seq, width, height, format, ts, payloadLen);
            return true;
        }
        finally { _mutex.ReleaseMutex(); }
    }

    public void ResetCounters()
    {
        lock (_gate) { _hasMeta = false; _infoCache = null; _lastElapsedMs = 0; LastAction = "카운터 초기화"; }
        Interlocked.Exchange(ref _framesSubmitted, 0);
        Interlocked.Exchange(ref _framesDropped, 0);
        Interlocked.Exchange(ref _mutexTimeouts, 0);
        OnChange?.Invoke();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposedFlag, 1) != 0) return;   // v12.2.0: 동시 Dispose 이중 해제 차단
        Volatile.Write(ref _disposed, true);
        StopPump();   // v15.1.0: 송출 스레드 먼저 정지(뷰 해제 후 접근 차단)
        _pumpSignal.Dispose();
        _pumpStopEvent.Dispose();
        // 진행 중 Submit/Read 가 Mutex 를 쥐고 있으면 그 구간이 끝날 때까지(최대 1초) 기다린 뒤 뷰/매핑 해제 - 사용 중 해제로 인한 AccessViolation/ODE 차단.
        var held = false;
        try { held = _mutex.WaitOne(TimeSpan.FromSeconds(1)); }
        catch (AbandonedMutexException) { held = true; }
        catch (ObjectDisposedException) { /* 무시 */ }
        try { _stream.Dispose(); } catch { /* 무시 */ }
        try { _mmf.Dispose(); } catch { /* 무시 */ }
        if (held) { try { _mutex.ReleaseMutex(); } catch { /* 무시 */ } }
        _mutex.Dispose();
    }
    private int _disposedFlag;
}