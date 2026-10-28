using System;
using PromptFlowStudio.Services.Commercial;
using PromptFlowStudio.Services.Render;

namespace PromptFlowStudio.ViewModels;

/// <summary>
/// [PromptFlowStudio] 커머셜 브랜드 스튜디오 뷰모델 — 브랜드 킷 + 포맷 프리셋 → 커머셜 프롬프트 합성 · 렌더 큐 전송.

///   ▸ 포맷 변경 시 길이(6/15/30초)·기본 화면비가 프리셋으로 동기화 — 화면비는 이후 수동 토글 가능.

/// </summary>
public sealed class CommercialStudioViewModel
{
    private readonly CommercialBrandService _brand;
    private readonly IRenderQueueService _queue;
    private CommercialAdFormat _format = CommercialAdFormat.Feed15s;
    private Guid _exportedJobId;

    public CommercialStudioViewModel(CommercialBrandService brand, IRenderQueueService queue)
    {
        _brand = brand ?? throw new ArgumentNullException(nameof(brand));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    }

    public event Action? Changed;

    public string BrandName { get; set; } = "";
    public string LogoImagePath { get; set; } = "";
    /// <summary>제품(누끼) 사진 경로 — 렌더 작업 ReferenceImagePath(I2V 첫 프레임)로 전달.</summary>
    public string ProductImagePath { get; set; } = "";
    public string PrimaryColorHex { get; set; } = CommercialBrandService.DefaultColorHex;
    public WatermarkPlacement SelectedPlacement { get; set; } = WatermarkPlacement.BottomRight;
    public string DisclaimerText { get; set; } = CommercialBrandService.DefaultDisclaimer;
    public string ProductConcept { get; set; } = "";
    public string CtaText { get; set; } = "지금 구매하기";
    public string AspectRatio { get; set; } = CommercialBrandService.DefaultAspectFor(CommercialAdFormat.Feed15s);

    public CommercialAdFormat SelectedFormat
    {
        get => _format;
        set
        {
            if (_format == value) return;
            _format = value;
            AspectRatio = CommercialBrandService.DefaultAspectFor(value);
        }
    }

    public float TargetDurationSec => CommercialBrandService.DurationFor(_format);
    public bool IsVertical => !AspectRatio.StartsWith("16:9", StringComparison.Ordinal);
    public CommercialPromptPackage? LastPackage { get; private set; }
    public int DispatchedCount { get; private set; }
    public string? StatusMessage { get; private set; }
    /// <summary>직전 전송 성공 작업 — UI 토스트 · 렌더 큐 이동 안내.</summary>
    public Guid? LastExportedJobId => _exportedJobId == Guid.Empty ? null : _exportedJobId;

    public void ToggleAspect()
    {
        AspectRatio = IsVertical ? CommercialBrandService.Landscape : CommercialBrandService.Vertical;
        Raise();
    }

    public CommercialPromptPackage GenerateAdPrompt()
    {
        var kit = new BrandKitProfile(BrandName, LogoImagePath, SelectedPlacement, PrimaryColorHex, DisclaimerText);
        var spec = CommercialBrandService.Preset(_format, AspectRatio, CtaText);
        LastPackage = _brand.SynthesizeCommercialPrompt(ProductConcept, kit, spec, ProductImagePath);
        StatusMessage = "커머셜 프롬프트 합성 완료 · " + LastPackage.Overlay.Resolution;
        Raise();
        return LastPackage;
    }

    /// <summary>합성 결과를 렌더 큐에 Queued 로 적재 — 미합성 시 먼저 합성. 적재 성공 여부 반환.</summary>
    public bool ExportToRenderQueue()
    {
        // 이미 전송한 작업이면 새 JobId 로 재합성 — 큐의 중복 차단에 걸리지 않게
        var pkg = LastPackage is null || LastPackage.Job.JobId == _exportedJobId ? GenerateAdPrompt() : LastPackage;
        var added = _queue.Enqueue(new[] { pkg.Job });
        if (added > 0)
        {
            DispatchedCount += added;
            _exportedJobId = pkg.Job.JobId;
        }
        StatusMessage = added > 0
            ? "커머셜 렌더 큐 적재 (" + pkg.Overlay.AspectRatio + " · " + TargetDurationSec.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "s"
              + (pkg.Job.ReferenceImagePath is null ? ")" : " · 제품 I2V)")
            : "렌더 큐 적재 실패(중복 작업)";
        Raise();
        return added > 0;
    }

    private void Raise()
    {
        try { Changed?.Invoke(); } catch (Exception) { }
    }
}
