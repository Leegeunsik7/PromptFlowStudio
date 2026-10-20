namespace PromptFlowStudio.Shared.Hud;

/// <summary>HUD 캔버스 그리기 모드 — JS 모듈 hud-canvas.js 의 MODE 상수와 1:1.</summary>
public enum HudCanvasMode
{
    Bars = 0,
    Wave = 1,
    Sparkline = 2,
}

/// <summary>
/// HudCanvas 1개가 소유하는 독립 상태 — DI/서비스 의존 0, 다른 Blazor 프로젝트에 파일 그대로 이식 가능.
/// 생산자(워커/타이머 스레드)가 Write*/Push 로 고정 바이트 프레임(0..255)을 갱신하고, 소비자(UI)가 버전 비교 후 TryCopy 로 가져간다.
/// ⚠ 핫패스 무할당 — 모든 버퍼는 생성자에서 1회 확보, 쓰기/복사는 lock 안 Span 복사만.
/// </summary>
public sealed class HudFrameBuffer
{
    private readonly byte[] _frame;
    private readonly object _gate = new();
    private int _head;      // Sparkline 링 쓰기 위치(가장 오래된 샘플)
    private long _version;

    public HudFrameBuffer(int capacity)
    {
        if (capacity is < 2 or > 4096) throw new ArgumentOutOfRangeException(nameof(capacity));
        _frame = new byte[capacity];
    }

    public int Capacity => _frame.Length;
    public long Version => Interlocked.Read(ref _version);

    /// <summary>바이트 레벨(0..255) 프레임 통째 교체 — 길이가 다르면 앞쪽만 복사하고 나머지는 0.</summary>
    public void Write(ReadOnlySpan<byte> levels)
    {
        lock (_gate)
        {
            int n = Math.Min(levels.Length, _frame.Length);
            levels[..n].CopyTo(_frame);
            if (n < _frame.Length) Array.Clear(_frame, n, _frame.Length - n);
            _head = 0;
            _version++;
        }
    }

    /// <summary>PCM(-1..1) → 128 중심 8-bit 양자화, 용량에 맞춰 구간 최대 절댓값 부호 유지 다운샘플(피크 보존).</summary>
    public void WriteSamples(ReadOnlySpan<float> pcm)
    {
        if (pcm.IsEmpty) return;
        lock (_gate)
        {
            int cap = _frame.Length;
            for (int i = 0; i < cap; i++)
            {
                int s0 = (int)((long)i * pcm.Length / cap);
                int s1 = Math.Max(s0 + 1, (int)((long)(i + 1) * pcm.Length / cap));
                float pick = 0f;
                for (int s = s0; s < s1 && s < pcm.Length; s++)
                    if (MathF.Abs(pcm[s]) > MathF.Abs(pick)) pick = pcm[s];
                _frame[i] = Quantize(pick);
            }
            _head = 0;
            _version++;
        }
    }

    /// <summary>스칼라(0..1) 1개를 링에 추가 — Sparkline 텔레메트리(프레임 시간·부하 등).</summary>
    public void Push(float unit)
    {
        lock (_gate)
        {
            _frame[_head] = (byte)(Math.Clamp(unit, 0f, 1f) * 255f + 0.5f);
            _head = _head + 1 == _frame.Length ? 0 : _head + 1;
            _version++;
        }
    }

    /// <summary>버전이 <paramref name="seenVersion"/> 이후 바뀌었으면 시간순으로 펼쳐 복사 후 true. dst 길이는 Capacity 이상.</summary>
    public bool TryCopy(Span<byte> dst, ref long seenVersion)
    {
        lock (_gate)
        {
            if (_version == seenVersion) return false;
            int tail = _frame.Length - _head;
            _frame.AsSpan(_head, tail).CopyTo(dst);
            _frame.AsSpan(0, _head).CopyTo(dst[tail..]);
            seenVersion = _version;
            return true;
        }
    }

    private static byte Quantize(float v) => (byte)Math.Clamp((int)(v * 127f + 128.5f), 0, 255);
}
