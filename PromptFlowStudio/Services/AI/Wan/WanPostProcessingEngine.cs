using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Media;      // AutonomousNeuralTilingUpscaler(업스케일러 재사용 — 중복 구현 금지)
using PromptFlowStudio.Services.Inference;  // DirectMlNativeAdapter(경량 P/Invoke DirectML 가용성 프로브)

namespace PromptFlowStudio.Services.AI.Wan;

/// <summary>후처리 프레임 1건 처리 결과. v12.2.0: 값 형식(readonly record struct) - 60FPS 프레임당 힙 할당 제거(속성 API 무변경).</summary>
public readonly record struct PostProcessFrameResult(long FrameIndex, bool Interpolated, UpscaleTarget Factor, double ElapsedMs);

/// <summary>누적 관제 스냅샷 — HUD 바인딩 전용.</summary>
public sealed record PostProcessReport(long FramesIn, long Interpolated, long Upscaled, UpscaleTarget CurrentFactor, bool DirectMlCapable, double LastElapsedMs);

/// <summary>
/// [PromptFlow Studio v10.6.0] Wan 2.1 후처리 파이프라인 — 저사양 생성 결과를 프레임 보간(Spatial-Temporal
///   Interpolation) + 업스케일링으로 체감 품질을 끌어올린다.
///   ▸ 업스케일은 <see cref="AutonomousNeuralTilingUpscaler"/>(v9.8.1, MMF 타일 아레나)를 그대로 재사용한다
///     (중복 암호/텐서 구현 금지 — 이 프로젝트 전역 관례).
///   ▸ <see cref="DirectMlNativeAdapter.IsDirectMlCapable"/>(디바이스 미생성 경량 D3D12 프로브, 순수 BCL P/Invoke)로
///     이 호스트의 DirectX12/DirectML 가속 가용 여부만 판정해 관제에 노출한다 — 실제 텐서 연산 자체는 여전히
///     BCL 결정적 시뮬레이션이다(스텁 결선점: 향후 실 DirectML 백엔드 도입 시 <see cref="InterpolateInto"/> 내부와
///     <see cref="AutonomousNeuralTilingUpscaler"/> 내부만 교체하면 이 상위 클래스는 무변경).
///   ▸ Span&lt;byte&gt; 고정 아레나 프레임 링(오프로딩) — 안정상태 힙 할당 0(재사용 스크래치 복사만 수행).
///   ▸ <see cref="LivePipelineAutoTuner.CurrentQuant"/> 를 구독해 지속 리소스 압박(Int8 다운그레이드) 시 업스케일
///     목표를 8K→4K 로 자율 하향, 여유 회복(Fp16) 시 재상향한다("VRAM 피크 시 업스케일 팩터 자율 보정").
/// </summary>
public sealed class WanPostProcessingEngine : IDisposable
{
    private const int FrameBytes = 64 * 1024;
    private const int RingCapacity = 4;   // 보간엔 직전 1프레임만 필요 — 여유 있게 4슬롯 오프로딩

    private readonly AutonomousNeuralTilingUpscaler _upscaler;
    private readonly LivePipelineAutoTuner _tuner;
    private readonly HeterogeneousTaskScheduler _scheduler;
    private readonly WanDynamicMotionController _motion;
    private readonly LoRAHotSwapManager _lora;
    private readonly object _gate = new();
    private readonly byte[] _arena = new byte[FrameBytes * RingCapacity];
    // v12.2.0 PERF-2: 보간 결과 이중 버퍼 + 버퍼별 seqlock 버전(홀수=기록 중). 라이터는 back 버퍼에만 쓰고 front 인덱스를
    //   Volatile 게시 → 리더(CopyLastInterpolatedFrameTo, VirtualCam 60FPS 블릿)는 _gate 를 잡지 않고 복사 후 버전 재확인.
    //   이전엔 64KB 복사 전 구간을 _gate 로 잡아 SubmitFrame(보간+지문 64KB 2패스)과 상호 대기했다.
    private readonly byte[][] _blend = { new byte[FrameBytes], new byte[FrameBytes] };
    private readonly int[] _blendVersion = new int[2];
    private int _frontBlend = -1;
    private int _head;
    private int _filled;
    private WanQuantMode _lastObservedQuant = WanQuantMode.Fp16;
    // v12.2.0 PERF-1: 지문/LastAction 은 원시값만 기록하고 문자열은 조회 시 지연 생성(프레임당 string 2건 할당 제거).
    private uint _lastDigest;
    private bool _hasDigest;
    private string? _digestCache;
    private long _actFrame;
    private bool _actInterp;
    private UpscaleTarget _actFactor;
    private double _actMs;
    private bool _actionIsFrame;
    private string? _lastAction;
    private long _framesIn, _interpolated, _upscaled;
    private double _lastElapsedMs;
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
                    _lastAction = $"프레임 #{_actFrame} · {(_actInterp ? $"보간({DigestLocked()})+" : "")}업스케일 ×{(_actFactor == UpscaleTarget.Uhd8K ? 4 : 2)} · {_actMs:0.##}ms";
                    _actionIsFrame = false;
                }
                return _lastAction;
            }
        }
        private set { lock (_gate) { _lastAction = value; _actionIsFrame = false; } }
    }
    public bool DirectMlCapable { get; } = DirectMlNativeAdapter.IsDirectMlCapable();
    public long FramesIn => Interlocked.Read(ref _framesIn);
    public long Interpolated => Interlocked.Read(ref _interpolated);
    public long Upscaled => Interlocked.Read(ref _upscaled);
    public UpscaleTarget CurrentFactor => _upscaler.Target;
    /// <summary>가장 최근 보간 프레임의 짧은 지문(HUD 표시/검증용) — 보간이 아직 없었으면 null.</summary>
    public string? LastInterpolatedDigest8 { get { lock (_gate) return DigestLocked(); } }

    private string? DigestLocked() => _hasDigest ? _digestCache ??= FormatDigest8(_lastDigest) : null;

    /// <summary>v11.1.0: 가장 최근 보간 프레임을 대상 버퍼로 복사(락 보호 - torn read 방지, Zero-Alloc 호출측 재사용
    ///   버퍼 전제). 아직 보간 이력이 없으면 0. ⚠ FrameBytes(64KB)는 정확히 128×128 RGBA(128*128*4=65536)와
    ///   일치하도록 잡은 값 - 실 카메라 해상도가 아니라 이 파이프라인 전역의 "동일 추상화 수준 자리표시자" 관례를
    ///   그대로 유지하면서도(스텁), LiveStreamVirtualCamPipe 결선점에 의미 있는 고정 해상도를 제공한다.</summary>
    public int CopyLastInterpolatedFrameTo(Span<byte> destination)
    {
        var n = Math.Min(FrameBytes, destination.Length);
        // 낙관적 무락 경로: 버전 짝수 확인 → 복사 → 전체 펜스 → 버전 불변이면 torn 아님. 라이터가 같은 버퍼를 덮는 경합(2프레임
        //   연속 게시 도중)일 때만 재시도, 그래도 실패하면 _gate 폴백(라이터는 _gate 안에서만 버퍼를 쓰므로 그 안에선 안정).
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var idx = Volatile.Read(ref _frontBlend);
            if (idx < 0) return 0;
            var v1 = Volatile.Read(ref _blendVersion[idx]);
            if ((v1 & 1) != 0) { Thread.SpinWait(8); continue; }
            _blend[idx].AsSpan(0, n).CopyTo(destination);
            Interlocked.MemoryBarrier();
            if (Volatile.Read(ref _blendVersion[idx]) == v1) return n;
        }
        lock (_gate)
        {
            var idx = _frontBlend;
            if (idx < 0) return 0;
            _blend[idx].AsSpan(0, n).CopyTo(destination);
            return n;
        }
    }

    public PostProcessReport Report { get { lock (_gate) return new PostProcessReport(FramesIn, Interpolated, Upscaled, CurrentFactor, DirectMlCapable, _lastElapsedMs); } }

    public WanPostProcessingEngine(AutonomousNeuralTilingUpscaler upscaler, LivePipelineAutoTuner tuner, HeterogeneousTaskScheduler scheduler,
        WanDynamicMotionController motion, LoRAHotSwapManager lora)
    {
        _upscaler = upscaler;
        _tuner = tuner;
        _scheduler = scheduler;
        _motion = motion;
        _lora = lora;
        _tuner.OnChange += OnTunerChanged;
    }

    /// <summary>v10.6.0-F3: 현재 Denoising Step의 모션 벡터를 프레임에 실제 반영(주입)한 뒤 <see cref="SubmitFrame"/>
    ///   을 실행 — 진행 중인 LoRA 핫스왑이 있으면 먼저 짧게 대기(메인 샘플링 루프 Pause 모사)한다.
    ///   ⚠ <see cref="DirectMlWanConnector"/> 의 검증된 <see cref="System.Collections.Generic.IAsyncEnumerable{T}"/>
    ///   타일 루프는 안전을 위해 무변경 유지 — 이 메서드가 "모션 주입/핫스왑 Pause"의 실질 결선점이다.</summary>
    public async Task<PostProcessFrameResult> SubmitFrameWithMotionAsync(byte[] frame, CancellationToken ct = default)
    {
        await _lora.WaitIfPausedAsync(ct).ConfigureAwait(false);
        var vector = _motion.InjectNext();
        ApplyMotionInPlace(frame, vector);
        return SubmitFrame(frame);
    }

    // Spatial 모션 주입 스텁 — 실 워핑/아핀변환이 아니라 팬/줌/롤 크기에 비례한 결정적 바이트 변형(동일 추상화 수준의
    // 시뮬레이션 · InterpolateInto 의 "결정적 대용" 관례와 동일 정합성 기조). 인덱스 혼합 XOR 이라 무할당.
    //   v12.2.0: (byte)i 는 256 주기이므로 key^j(j=0..255) 패턴을 stackalloc 1회 구성 후 256B 청크마다 SIMD XOR - 결과 비트 동일.
    internal static void ApplyMotionInPlace(Span<byte> frame, WanMotionVector v)
    {
        var key = (byte)(1 + (int)(v.PanMagnitude * 50) + (int)(v.ZoomMagnitude * 30) + (int)(Math.Abs(v.RollDegrees)));
        Span<byte> pattern = stackalloc byte[256];
        for (var j = 0; j < 256; j++) pattern[j] = (byte)(key ^ j);
        for (var off = 0; off < frame.Length; off += 256)
        {
            var chunk = frame.Slice(off, Math.Min(256, frame.Length - off));
            XorInPlace(chunk, pattern[..chunk.Length]);
        }
    }

    private static void XorInPlace(Span<byte> dst, ReadOnlySpan<byte> src)
    {
        var i = 0;
        if (Vector128.IsHardwareAccelerated && dst.Length >= Vector128<byte>.Count)
        {
            ref var d = ref MemoryMarshal.GetReference(dst);
            ref var s = ref MemoryMarshal.GetReference(src);
            var last = dst.Length - Vector128<byte>.Count;
            for (; i <= last; i += Vector128<byte>.Count)
                (Vector128.LoadUnsafe(ref d, (nuint)i) ^ Vector128.LoadUnsafe(ref s, (nuint)i)).StoreUnsafe(ref d, (nuint)i);
        }
        for (; i < dst.Length; i++) dst[i] ^= src[i];
    }

    /// <summary>v10.6.0-F2: <see cref="SubmitFrame"/> 을 <see cref="HeterogeneousTaskScheduler"/> 의 Secondary
    ///   디바이스 경로로 비동기 오프로드 실행 — 먼저 마이그레이션(MMF 왕복)을 모사한 뒤 기존 동기 무할당 경로를
    ///   그대로 재사용한다(중복 구현 금지). ⚠ async 경계는 <see cref="Span{T}"/> 를 넘길 수 없어 <paramref name="frame"/>
    ///   은 배열로 받는다 — 이 한 지점만 Zero-Alloc 예외(언어 자체의 근본 제약)이고, SubmitFrame 내부 경로는 무변경.</summary>
    public async Task<PostProcessFrameResult> SubmitFrameOffloadedAsync(byte[] frame, CancellationToken ct = default)
    {
        PostProcessFrameResult? result = null;
        await _scheduler.RunOnSecondaryAsync(_ =>
        {
            _scheduler.SimulateMigration(frame);
            result = SubmitFrame(frame);
            return Task.CompletedTask;
        }, "WanPostProcess", ct).ConfigureAwait(false);
        return result.GetValueOrDefault();
    }

    /// <summary>Wan 출력 프레임 1건 제출 — 링에 무할당 오프로딩 후 보간+업스케일 파이프라인을 실행.</summary>
    public PostProcessFrameResult SubmitFrame(ReadOnlySpan<byte> frame)
    {
        var t0 = Stopwatch.GetTimestamp();   // v12.2.0: Stopwatch 인스턴스 할당 제거
        bool interpolated;
        lock (_gate)
        {
            interpolated = _filled > 0;
            if (interpolated)
            {
                var back = _frontBlend == 0 ? 1 : 0;
                var buf = _blend[back];
                Interlocked.Increment(ref _blendVersion[back]);   // 홀수: 기록 시작(리더 재시도 신호)
                InterpolateInto(PrevSlotSpan(), frame, buf);        // 직전↔신규 사이 중간 프레임 합성(무할당 · SIMD)
                _lastDigest = Fnv1a32(buf);                         // 결과를 관측 가능하게 지문화(계산 후 버려지는 죽은 값 방지)
                _hasDigest = true;
                _digestCache = null;
                Interlocked.Increment(ref _blendVersion[back]);   // 짝수: 기록 완료
                Volatile.Write(ref _frontBlend, back);             // 게시
            }
            PushLocked(frame);
        }
        if (interpolated) Interlocked.Increment(ref _interpolated);
        var frameNo = Interlocked.Increment(ref _framesIn);

        _upscaler.UpscaleHotPath();   // 재사용(중복 구현 금지) — 이 프레임 도착과 상관된 1회 업스케일 패스(리포트 지연 생성 경로)
        Interlocked.Increment(ref _upscaled);
        var elapsedMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

        UpscaleTarget factor;
        lock (_gate)
        {
            _lastElapsedMs = elapsedMs;
            factor = _upscaler.Target;
            _actFrame = frameNo; _actInterp = interpolated; _actFactor = factor; _actMs = elapsedMs;
            _actionIsFrame = true;   // 문자열은 LastAction 조회 시 생성
        }
        var result = new PostProcessFrameResult(frameNo, interpolated, factor, elapsedMs);
        OnChange?.Invoke();
        return result;
    }

    private ReadOnlySpan<byte> PrevSlotSpan()
    {
        var prevIndex = (_head - 1 + RingCapacity) % RingCapacity;
        return _arena.AsSpan(prevIndex * FrameBytes, FrameBytes);
    }

    private void PushLocked(ReadOnlySpan<byte> frame)
    {
        var slot = _arena.AsSpan(_head * FrameBytes, FrameBytes);
        var take = Math.Min(frame.Length, FrameBytes);
        frame[..take].CopyTo(slot);
        if (take < FrameBytes) slot[take..].Clear();
        _head = (_head + 1) % RingCapacity;
        if (_filled < RingCapacity) _filled++;
    }

    /// <summary>Spatial-Temporal Interpolation 스텁 — 바이트 가중평균으로 중간 프레임을 합성(재사용 스크래치, 무할당).
    ///   ⚠ 실 신경망 광류(optical-flow) 보간이 아니라 동일 추상화 수준(결정적 시뮬레이션)의 자리표시자다 —
    ///   AutonomousNeuralTilingUpscaler 의 "결정적 패턴 대용" 관례와 동일한 정합성 기조.</summary>
    //   v12.2.0: floor((a+b)/2) == (a&b) + ((a^b)>>1) 항등식으로 byte 오버플로 없이 16B 단위 SIMD 처리 - 스칼라와 비트 동일.
    internal static void InterpolateInto(ReadOnlySpan<byte> prev, ReadOnlySpan<byte> curr, Span<byte> dest)
    {
        var n = Math.Min(Math.Min(prev.Length, curr.Length), dest.Length);
        var i = 0;
        if (Vector128.IsHardwareAccelerated && n >= Vector128<byte>.Count)
        {
            ref var p = ref MemoryMarshal.GetReference(prev);
            ref var c = ref MemoryMarshal.GetReference(curr);
            ref var d = ref MemoryMarshal.GetReference(dest);
            var last = n - Vector128<byte>.Count;
            for (; i <= last; i += Vector128<byte>.Count)
            {
                var a = Vector128.LoadUnsafe(ref p, (nuint)i);
                var b = Vector128.LoadUnsafe(ref c, (nuint)i);
                ((a & b) + Vector128.ShiftRightLogical(a ^ b, 1)).StoreUnsafe(ref d, (nuint)i);
            }
        }
        for (; i < n; i++) dest[i] = (byte)((prev[i] + curr[i]) >> 1);
    }

    /// <summary>v15.1.0 WAN-OPT-2: 시간축 가중 보간(Temporal Motion Interpolation) 커널 — dest = round(prev·(256−w) + curr·w) / 256.
    ///   w = 0 이면 prev, 256 이면 curr 와 비트 동일. 저 FPS 키프레임 사이를 60FPS 위상(<see cref="ComputePhase256"/>)으로 채운다.
    ///   ▸ Vector256(AVX2 32B) → Vector128(SSE2/AdvSimd 16B) → 스칼라 꼬리. byte→ushort Widen 후 곱셈 — 최대 255·256+128 = 65408 &lt; 65536
    ///     이라 ushort 오버플로 없이 스칼라 참조식과 비트 동일. 원소별 연산이라 dest 가 prev/curr 와 완전 동일 위치 별칭이어도 안전(무할당).</summary>
    public static void BlendWeightedInto(ReadOnlySpan<byte> prev, ReadOnlySpan<byte> curr, Span<byte> dest, int weight256)
    {
        var n = Math.Min(Math.Min(prev.Length, curr.Length), dest.Length);
        var t = Math.Clamp(weight256, 0, 256);
        var i = 0;
        ref var p = ref MemoryMarshal.GetReference(prev);
        ref var c = ref MemoryMarshal.GetReference(curr);
        ref var d = ref MemoryMarshal.GetReference(dest);
        if (Vector256.IsHardwareAccelerated && n >= Vector256<byte>.Count)
        {
            var wc = Vector256.Create((ushort)t);
            var wp = Vector256.Create((ushort)(256 - t));
            var round = Vector256.Create((ushort)128);
            var last = n - Vector256<byte>.Count;
            for (; i <= last; i += Vector256<byte>.Count)
            {
                var (pl, ph) = Vector256.Widen(Vector256.LoadUnsafe(ref p, (nuint)i));
                var (cl, ch) = Vector256.Widen(Vector256.LoadUnsafe(ref c, (nuint)i));
                var lo = Vector256.ShiftRightLogical(pl * wp + cl * wc + round, 8);
                var hi = Vector256.ShiftRightLogical(ph * wp + ch * wc + round, 8);
                Vector256.Narrow(lo, hi).StoreUnsafe(ref d, (nuint)i);
            }
        }
        if (Vector128.IsHardwareAccelerated && n - i >= Vector128<byte>.Count)
        {
            var wc = Vector128.Create((ushort)t);
            var wp = Vector128.Create((ushort)(256 - t));
            var round = Vector128.Create((ushort)128);
            var last = n - Vector128<byte>.Count;
            for (; i <= last; i += Vector128<byte>.Count)
            {
                var (pl, ph) = Vector128.Widen(Vector128.LoadUnsafe(ref p, (nuint)i));
                var (cl, ch) = Vector128.Widen(Vector128.LoadUnsafe(ref c, (nuint)i));
                var lo = Vector128.ShiftRightLogical(pl * wp + cl * wc + round, 8);
                var hi = Vector128.ShiftRightLogical(ph * wp + ch * wc + round, 8);
                Vector128.Narrow(lo, hi).StoreUnsafe(ref d, (nuint)i);
            }
        }
        for (; i < n; i++) dest[i] = (byte)((prev[i] * (256 - t) + curr[i] * t + 128) >> 8);
    }

    /// <summary>v15.1.0: 모션 스무딩(시간축 EMA) — accum ← accum·(256−w) + curr·w. 키프레임 간 미세 떨림(flicker) 억제용, 무할당 제자리 연산.</summary>
    public static void SmoothTemporalInPlace(Span<byte> accum, ReadOnlySpan<byte> curr, int weight256)
        => BlendWeightedInto(accum, curr, accum, weight256);

    /// <summary>v15.1.0: 직전 키프레임 도착 후 경과/키프레임 간격 → 보간 위상(0~256). 간격 미확정(≤0)이면 256(최신 키프레임 그대로).</summary>
    public static int ComputePhase256(long elapsedTicks, long intervalTicks)
        => intervalTicks <= 0 ? 256 : (int)Math.Clamp(elapsedTicks * 256 / intervalTicks, 0L, 256L);

    // Zero-Alloc 짧은 지문(암호 강도 불요 — HUD 표시/검증 전용): FNV-1a 32비트. v12.2.0: 해시(핫패스)와 hex 문자열화(조회 시)를 분리.
    private static uint Fnv1a32(ReadOnlySpan<byte> data)
    {
        var h = 2166136261u;
        for (var i = 0; i < data.Length; i++) { h ^= data[i]; h *= 16777619u; }
        return h;
    }

    private static string FormatDigest8(uint h)
    {
        Span<char> buf = stackalloc char[8];
        for (var i = 0; i < 8; i++)
        {
            var nibble = (int)((h >> ((7 - i) * 4)) & 0xF);
            buf[i] = (char)(nibble < 10 ? '0' + nibble : 'a' + nibble - 10);
        }
        return new string(buf);
    }

    // v10.6.0: LivePipelineAutoTuner 결선 — Int8(지속 리소스 압박)이면 8K→4K 하향, Fp16(여유) 복귀 시 재상향.
    private void OnTunerChanged()
    {
        var quant = _tuner.CurrentQuant;
        if (quant == _lastObservedQuant) return;
        _lastObservedQuant = quant;
        if (quant == WanQuantMode.Int8 && _upscaler.Target == UpscaleTarget.Uhd8K)
        {
            _upscaler.SetTarget(UpscaleTarget.Uhd4K);
            lock (_gate) LastAction = "튜너 Int8 다운그레이드 감지 — 업스케일 8K→4K 자율 하향";
            OnChange?.Invoke();
        }
        else if (quant == WanQuantMode.Fp16 && _upscaler.Target == UpscaleTarget.Uhd4K)
        {
            _upscaler.SetTarget(UpscaleTarget.Uhd8K);
            lock (_gate) LastAction = "튜너 Fp16 복귀 감지 — 업스케일 4K→8K 자율 재상향";
            OnChange?.Invoke();
        }
    }

    public void ResetAll()
    {
        lock (_gate) { _head = 0; _filled = 0; _lastElapsedMs = 0; _hasDigest = false; _digestCache = null; Volatile.Write(ref _frontBlend, -1); LastAction = "후처리 엔진 초기화"; }
        Interlocked.Exchange(ref _framesIn, 0); Interlocked.Exchange(ref _interpolated, 0); Interlocked.Exchange(ref _upscaled, 0);
        OnChange?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tuner.OnChange -= OnTunerChanged;
    }
}
