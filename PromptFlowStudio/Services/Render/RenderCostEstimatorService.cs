using System;

namespace PromptFlowStudio.Services.Render;

/// <summary>비용 추산 요청 — 해상도("WxH") · 컷 길이(초) · 배치 작업 수 · FPS.</summary>
public sealed record CostEstimateRequest(string Resolution, float DurationSec, int JobCount, int Fps = RenderCostEstimatorService.DefaultFps);

/// <summary>비용 추산 결과 — 총 프레임 · 피크 VRAM(GB, 작업 1건 기준) · 총 렌더 시간(초) · 총 크레딧.</summary>
public sealed record CostEstimateResult(int TotalFrames, double EstimatedVramGb, double EstimatedRenderTimeSec, int EstimatedCredits);

/// <summary>
/// [PromptFlowStudio] 3번 워크플로우 【대량 양산 &amp; 비용】 렌더 자원/비용 사전 추산기.
///   ▸ 해상도 높이로 프로파일(480p/720p/1080p/4K) 선택 — VRAM · 프레임 가중치 · 5초당 크레딧.
///   ▸ 0 이하/NaN 입력은 안전 기본값(5초 · 1건 · 24fps)으로 클램프, 상한으로 오버플로 차단.
/// </summary>
public sealed class RenderCostEstimatorService
{
    public const int DefaultFps = 24;
    public const float DefaultDurationSec = 5.0f;
    public const string DefaultResolution = "1280x720";
    public const int MaxFps = 120;
    public const float MaxDurationSec = 600f;
    public const int MaxJobCount = 10_000;

    /// <summary>프레임당 기준 렌더 시간(초, 720p 가중치 1.0).</summary>
    public const double BaseSecPerFrame = 0.5;

    private readonly struct Profile
    {
        public readonly double VramGb, FrameWeight;
        public readonly int CreditsPer5Sec;
        public Profile(double vramGb, double frameWeight, int creditsPer5Sec) { VramGb = vramGb; FrameWeight = frameWeight; CreditsPer5Sec = creditsPer5Sec; }
    }

    private static readonly Profile P480 = new(4.2, 0.6, 6);
    private static readonly Profile P720 = new(6.5, 1.0, 10);
    private static readonly Profile P1080 = new(11.2, 2.25, 22);
    private static readonly Profile P2160 = new(24.0, 9.0, 80);

    public CostEstimateResult Estimate(CostEstimateRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);

        var duration = float.IsFinite(req.DurationSec) && req.DurationSec > 0f ? Math.Min(req.DurationSec, MaxDurationSec) : DefaultDurationSec;
        var jobs = req.JobCount > 0 ? Math.Min(req.JobCount, MaxJobCount) : 1;
        var fps = req.Fps > 0 ? Math.Min(req.Fps, MaxFps) : DefaultFps;
        var p = ResolveProfile(req.Resolution);

        var framesPerJob = (int)Math.Round(duration * fps, MidpointRounding.AwayFromZero);
        var totalFrames = framesPerJob * jobs;
        var renderSec = totalFrames * BaseSecPerFrame * p.FrameWeight;
        var creditsPerJob = (int)Math.Ceiling(p.CreditsPer5Sec * (duration / DefaultDurationSec));

        return new CostEstimateResult(totalFrames, p.VramGb, renderSec, creditsPerJob * jobs);
    }

    /// <summary>"WxH" 의 짧은 변(세로 기준)으로 프로파일 선택 — 파싱 실패 시 720p.</summary>
    private static Profile ResolveProfile(string? resolution)
    {
        if (!TryParseShortSide(resolution, out var side)) return P720;
        if (side <= 480) return P480;
        if (side <= 720) return P720;
        if (side <= 1080) return P1080;
        return P2160;
    }

    private static bool TryParseShortSide(string? resolution, out int side)
    {
        side = 0;
        if (string.IsNullOrWhiteSpace(resolution)) return false;
        var span = resolution.AsSpan().Trim();
        var x = span.IndexOfAny('x', 'X');
        if (x <= 0 || x >= span.Length - 1) return false;
        if (!int.TryParse(span[..x], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var w) ||
            !int.TryParse(span[(x + 1)..], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var h) ||
            w <= 0 || h <= 0) return false;
        side = Math.Min(w, h);
        return true;
    }
}
