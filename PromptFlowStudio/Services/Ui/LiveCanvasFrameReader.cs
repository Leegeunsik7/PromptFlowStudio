using System;
using System.Threading;
using PromptFlowStudio.Services.AI.Wan;

namespace PromptFlowStudio.Services.Ui;

/// <summary>MMF 최신 프레임 읽기 계약 — <see cref="LiveStreamVirtualCamPipe.TryReadLatestFrameRaw"/> 와 동일 시그니처(테스트 대역 주입용).</summary>
public delegate bool LiveFrameReadFn(Span<byte> destination, out VirtualCamFrameHeader header, out int bytesRead);

/// <summary>
/// [PromptFlowStudio] v15.4.1 🎬 라이브 캔버스 프레임 리더 — MMF 파이프의 최신 RGBA32 프레임을 시퀀스가 전진했을 때만
///   인스턴스 버퍼로 복사한다. 버퍼는 해상도가 바뀔 때만 재할당(정확한 길이 — JS interop 이 byte[] 를 그대로 Uint8Array 로 넘기도록),
///   같은 해상도의 연속 폴링은 0B. 그리기(JS interop)는 호출측 책임.
/// </summary>
public sealed class LiveCanvasFrameReader
{
    /// <summary>캔버스 표시 상한(512×512 RGBA) — 초과 프레임은 건너뛴다(1080p 원본을 매 틱 interop 으로 넘기지 않기 위함).</summary>
    public const int MaxCanvasBytes = 512 * 512 * 4;

    private readonly LiveFrameReadFn _read;
    private byte[] _scratch = Array.Empty<byte>();
    private byte[] _frame = Array.Empty<byte>();
    private long _lastSeq = -1;
    private long _delivered, _stale, _rejected;

    public LiveCanvasFrameReader(LiveFrameReadFn read) => _read = read;

    /// <summary>파이프 직결 생성자.</summary>
    public LiveCanvasFrameReader(LiveStreamVirtualCamPipe pipe) : this(pipe.TryReadLatestFrameRaw) { }

    /// <summary>마지막으로 전달된 프레임(정확한 길이 = Width×Height×4).</summary>
    public byte[] Frame => _frame;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public long LastSequence => _lastSeq;
    public long Delivered => Interlocked.Read(ref _delivered);
    public long Stale => Interlocked.Read(ref _stale);
    public long Rejected => Interlocked.Read(ref _rejected);

    /// <summary>새 프레임이 있으면 <see cref="Frame"/> 에 복사하고 true. 동일 시퀀스·비RGBA·상한 초과·읽기 실패는 false.</summary>
    public bool TryPoll()
    {
        if (_scratch.Length == 0) _scratch = new byte[MaxCanvasBytes];
        if (!_read(_scratch, out var h, out var n)) return false;
        if (h.Sequence == _lastSeq) { Interlocked.Increment(ref _stale); return false; }

        var need = (long)h.Width * h.Height * 4;
        if (h.Format != VirtualCamPixelFormat.Rgba32 || h.Width <= 0 || h.Height <= 0 || need > MaxCanvasBytes || n < need)
        {
            _lastSeq = h.Sequence;
            Interlocked.Increment(ref _rejected);
            return false;
        }

        if (_frame.Length != need) _frame = new byte[need];
        _scratch.AsSpan(0, (int)need).CopyTo(_frame);
        Width = h.Width;
        Height = h.Height;
        _lastSeq = h.Sequence;
        Interlocked.Increment(ref _delivered);
        return true;
    }
}
