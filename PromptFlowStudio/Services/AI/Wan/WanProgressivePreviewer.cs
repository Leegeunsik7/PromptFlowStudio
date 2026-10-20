using System;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Wan;   // WanSampleFactory(플레이스홀더 프리뷰 DataURL)

namespace PromptFlowStudio.Services.AI.Wan;

/// <summary>디노이징 스텝 1건 발행 결과.</summary>
public sealed record LatentStepView(int Step, int TotalSteps, string Digest8, bool Mapped, double ElapsedMs);

/// <summary>
/// [PromptFlow Studio v10.6.0] Wan 2.1 프로그레시브 Latent 프리뷰어 — 디노이징 스텝마다 중간 잠재 프레임을
///   <see cref="MemoryMappedFile"/> 기반 공유메모리 프레임 파이프(4슬롯 링, <see cref="AutonomousNeuralTilingUpscaler"/>
///   와 동일한 MMF-우선·풀-폴백 관례)에 무할당으로 적재한 뒤, BlazorWebView 바인딩용 짧은 지문(Digest8)과
///   플레이스홀더 미리보기 URL을 노출한다.
///   ▸ <see cref="PublishStep"/> 자체(파이프 쓰기 + FNV-1a 지문)는 스칼라 연산/고정 아레나 복사뿐이라 안정상태
///     힙 할당 0. 문자열 할당은 <see cref="GetLatestPreviewDataUrl"/>(UI 바인딩 경계)에서 스텝당 최대 1회만
///     발생 — Blazor 렌더링이 근본적으로 문자열(src) 바인딩이라 이 경계 자체는 회피 불가능하므로 정직하게 구분한다.
/// </summary>
public sealed class WanProgressivePreviewer : IDisposable
{
    private const int SlotBytes = 64 * 1024;
    private const int SlotCount = 4;

    private readonly HeterogeneousTaskScheduler _scheduler;
    private readonly object _gate = new();
    private readonly byte[] _scratch = new byte[SlotBytes];
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _acc;
    private byte[]? _pool;
    private int _head;
    private int _totalSteps = 20;
    private int _lastStep = -1;
    private string? _lastDigest8;
    private string? _cachedPreviewUrl;
    private long _steps, _fallbacks;
    private bool _disposed;

    public event Action? OnChange;
    public string? LastAction { get; private set; }
    public bool Mapped => _mmf is not null;
    public long Steps => Interlocked.Read(ref _steps);
    public long Fallbacks => Interlocked.Read(ref _fallbacks);
    public int TotalSteps { get { lock (_gate) return _totalSteps; } }
    public int LastStep { get { lock (_gate) return _lastStep; } }

    public WanProgressivePreviewer(HeterogeneousTaskScheduler scheduler)
    {
        _scheduler = scheduler;
        Reallocate();
    }

    /// <summary>v10.6.0-F2: <see cref="PublishStep"/> 을 <see cref="HeterogeneousTaskScheduler"/> 의 Secondary
    ///   디바이스 경로로 비동기 오프로드 실행 — 마이그레이션(MMF 왕복) 모사 후 기존 동기 경로를 재사용한다.
    ///   ⚠ async 경계 제약으로 <paramref name="latentFrame"/> 은 배열로 받는다(§WanPostProcessingEngine 과 동일 사유).</summary>
    public async Task<LatentStepView> PublishStepOffloadedAsync(int step, int totalSteps, byte[] latentFrame, CancellationToken ct = default)
    {
        LatentStepView view = default!;
        await _scheduler.RunOnSecondaryAsync(_ =>
        {
            _scheduler.SimulateMigration(latentFrame);
            view = PublishStep(step, totalSteps, latentFrame);
            return Task.CompletedTask;
        }, "WanPreviewStep", ct).ConfigureAwait(false);
        return view;
    }

    private void Reallocate()
    {
        _acc?.Dispose(); _mmf?.Dispose(); _acc = null; _mmf = null; _pool = null;
        var bytes = (long)SlotCount * SlotBytes;
        try
        {
            _mmf = MemoryMappedFile.CreateNew(Guid.NewGuid().ToString("N"), bytes);
            _acc = _mmf.CreateViewAccessor(0, bytes);
        }
        catch (Exception) { _pool = new byte[bytes]; Interlocked.Increment(ref _fallbacks); }   // MMF 미가용 → 풀 폴백(Graceful)
        LastAction = $"프리뷰 파이프 재할당 · {SlotCount}슬롯 · {(Mapped ? "MMF" : "Pool")}";
    }

    /// <summary>디노이징 스텝 1건 발행 — latentFrame 은 호출 직후 복사 완료라 호출측이 즉시 재사용해도 안전.</summary>
    public LatentStepView PublishStep(int step, int totalSteps, ReadOnlySpan<byte> latentFrame)
    {
        var sw = Stopwatch.StartNew();
        string digest8;
        bool mapped;
        lock (_gate)
        {
            _totalSteps = Math.Max(1, totalSteps);
            var take = Math.Min(latentFrame.Length, SlotBytes);
            latentFrame[..take].CopyTo(_scratch);
            if (take < SlotBytes) _scratch.AsSpan(take).Clear();   // 잔여 구간 정리(직전 스텝 잔상 방지)

            var offset = (long)_head * SlotBytes;
            if (_acc is not null) _acc.WriteArray(offset, _scratch, 0, SlotBytes);
            else if (_pool is not null) _scratch.CopyTo(_pool.AsSpan((int)offset, SlotBytes));
            _head = (_head + 1) % SlotCount;

            digest8 = QuickDigest8(_scratch.AsSpan(0, take));
            mapped = Mapped;
            _lastStep = step; _lastDigest8 = digest8; _cachedPreviewUrl = null;   // 새 스텝 → 캐시 무효화
            LastAction = $"스텝 {step}/{_totalSteps} 발행 · {digest8} · {(mapped ? "MMF" : "Pool")}";
        }
        Interlocked.Increment(ref _steps);
        sw.Stop();
        var view = new LatentStepView(step, totalSteps, digest8, mapped, sw.Elapsed.TotalMilliseconds);
        OnChange?.Invoke();
        return view;
    }

    // Zero-Alloc 짧은 지문(암호 강도 불요 — HUD 표시 전용): FNV-1a 32비트, stackalloc 로 8hex 직접 구성(박싱/보간 없음).
    private static string QuickDigest8(ReadOnlySpan<byte> data)
    {
        var h = 2166136261u;
        for (var i = 0; i < data.Length; i++) { h ^= data[i]; h *= 16777619u; }
        Span<char> buf = stackalloc char[8];
        for (var i = 0; i < 8; i++)
        {
            var nibble = (int)((h >> ((7 - i) * 4)) & 0xF);
            buf[i] = (char)(nibble < 10 ? '0' + nibble : 'a' + nibble - 10);
        }
        return new string(buf);
    }

    /// <summary>UI 바인딩용 미리보기 데이터URL(SVG 플레이스홀더 — 실 픽셀 디코딩 없음, 지문 표시 전용).
    ///   ⚠ 여기서만 문자열 할당 발생(Blazor 바인딩 경계) — 같은 스텝 재호출 시 캐시 재사용(스텝당 최대 1회).</summary>
    public string? GetLatestPreviewDataUrl()
    {
        lock (_gate)
        {
            if (_lastDigest8 is null) return null;
            _cachedPreviewUrl ??= WanSampleFactory.FrameDataUrl($"Latent Step {_lastStep}/{_totalSteps}", "#a855f7", _lastDigest8);
            return _cachedPreviewUrl;
        }
    }

    public void ResetAll()
    {
        lock (_gate) { _lastStep = -1; _lastDigest8 = null; _cachedPreviewUrl = null; Reallocate(); }
        Interlocked.Exchange(ref _steps, 0); Interlocked.Exchange(ref _fallbacks, 0);
        OnChange?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate) { _acc?.Dispose(); _mmf?.Dispose(); _acc = null; _mmf = null; }
    }
}
