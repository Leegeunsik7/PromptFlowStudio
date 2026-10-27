using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using Timer = System.Threading.Timer;
using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Live;
using PromptFlowStudio.Services.Render;

namespace PromptFlowStudio.ViewModels;

/// <summary>
/// [PromptFlowStudio] 24/7 AI 라이브 스튜디오 뷰모델 — 상품 입력 → 5단계 세일즈 피치 생성 · 1초 주기 가상 송출 · 세그먼트 렌더 큐 디스패치.
///   ▸ 송출 틱은 타이머 스레드에서 <see cref="Changed"/> 를 발생 — Blazor 는 InvokeAsync 마샬링.
///   ▸ <see cref="Segments"/> 는 <see cref="BuildPitchSequence"/>(UI 스레드)에서만 변경.
/// </summary>
public sealed class LiveStudioViewModel : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly LiveStudioHostService _host;
    private readonly IRenderQueueService _queue;
    private Timer? _timer;

    public LiveStudioViewModel(LiveStudioHostService host, IRenderQueueService queue)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    }

    public event Action? Changed;

    public string ProductName { get; set; } = "";
    public decimal Price { get; set; } = 39900m;
    public int DiscountRate { get; set; } = 30;
    /// <summary>셀링포인트 — 줄바꿈 또는 쉼표 구분.</summary>
    public string SellingPointsInput { get; set; } = "";
    public PromotionType PromotionType { get; set; } = PromotionType.FlashSale;

    public decimal SalePrice => LiveStudioHostService.SalePrice(Price, DiscountRate);
    public bool IsLiveStreaming { get; private set; }
    public LivePitchPhase? CurrentPhase { get; private set; }
    public LivePitchSegment? ActiveSegment { get; private set; }
    public long TickCount { get; private set; }
    public long CycleCount => _host.CycleCount;
    public int DispatchedCount { get; private set; }
    public string? StatusMessage { get; private set; }

    public ObservableCollection<LivePitchSegment> Segments { get; } = new();

    public bool BuildPitchSequence()
    {
        var points = new List<string>();
        foreach (var raw in (SellingPointsInput ?? "").Split(new[] { '\n', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            points.Add(raw);
        var spec = new LiveProductSpec(ProductName, Price, DiscountRate, points, PromotionType);
        var generated = _host.GeneratePitchSequence(spec);

        Segments.Clear();
        for (var i = 0; i < generated.Count; i++) Segments.Add(generated[i]);
        ActiveSegment = null;
        CurrentPhase = null;
        TickCount = 0;
        StatusMessage = "세일즈 피치 " + generated.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "단계 생성 완료";
        Raise();
        return generated.Count > 0;
    }

    public void ToggleLiveStream()
    {
        if (IsLiveStreaming)
        {
            Interlocked.Exchange(ref _timer, null)?.Dispose();
            IsLiveStreaming = false;
            StatusMessage = "송출 중단";
            Raise();
            return;
        }

        if (Segments.Count == 0) BuildPitchSequence();
        IsLiveStreaming = true;
        StatusMessage = "24/7 송출 중";
        Advance();
        var t = new Timer(OnTick, null, TickInterval, TickInterval);
        Interlocked.Exchange(ref _timer, t)?.Dispose();
    }

    /// <summary>릴레이 1스텝 진행 — 타이머 틱 또는 테스트에서 직접 호출.</summary>
    public void Advance()
    {
        var seg = _host.GetNextSegment();
        ActiveSegment = seg;
        CurrentPhase = seg.Phase;
        TickCount++;
        Raise();
    }

    /// <summary>세그먼트 VisualPrompt 를 렌더 큐에 즉시 적재 — 적재 성공 여부 반환.</summary>
    public bool SendToRenderQueue(LivePitchSegment? segment)
    {
        if (segment is null || string.IsNullOrWhiteSpace(segment.VisualPrompt)) return false;
        var job = new BatchRenderJob
        {
            Prompt = segment.VisualPrompt,
            Seed = segment.SegmentId.GetHashCode() & int.MaxValue,
        };
        var added = _queue.Enqueue(new[] { job });
        if (added > 0) DispatchedCount += added;
        StatusMessage = added > 0 ? "[" + segment.Phase + "] 렌더 큐 적재" : "렌더 큐 적재 실패";
        Raise();
        return added > 0;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _timer, null)?.Dispose();
        IsLiveStreaming = false;
    }

    private void OnTick(object? _)
    {
        try { if (IsLiveStreaming) Advance(); }
        catch (Exception ex) { Program.Log("LiveStudioViewModel.Tick", ex); }
    }

    private void Raise()
    {
        try { Changed?.Invoke(); } catch (Exception) { }
    }
}
