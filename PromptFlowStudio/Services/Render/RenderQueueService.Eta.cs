using System;
using System.Globalization;
using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Export;

namespace PromptFlowStudio.Services.Render;

/// <summary>
/// ⏱️ 동적 큐 처리량 · ETA 추정 — 최근 완료 작업의 인코딩 소요(ms)를 해상도 가중치로 정규화한 롤링 평균(기준 1080p60 = 1.0×)으로
/// 남은 작업(대기 + 진행 중 잔여 %) 소요를 추정. 표본 없음 = null. 고정 링 버퍼(할당 없음).
/// </summary>
public sealed partial class RenderQueueService
{
    /// <summary>롤링 평균 표본 수.</summary>
    public const int EtaSampleWindow = 8;
    /// <summary>4K · 32:9(초광폭) 가중치.</summary>
    public const double WeightUltra = 1.8;
    /// <summary>1080p60 가로 기준 가중치.</summary>
    public const double WeightBaseline = 1.0;
    /// <summary>9:16 세로 · 1080p30 · 720p 이하 가중치.</summary>
    public const double WeightLight = 0.7;
    /// <summary>KST(UTC+9) — 장 시작(09:00 KST) 대비 완료 시각 표시.</summary>
    public static readonly TimeSpan KstOffset = TimeSpan.FromHours(9);

    private readonly double[] _etaSamples = new double[EtaSampleWindow];   // 기준(1.0×) 환산 초/작업
    private int _etaCount, _etaNext;

    /// <summary>ETA 기준 시계(테스트 주입).</summary>
    public Func<DateTimeOffset> EtaClock { get; set; } = static () => DateTimeOffset.Now;

    /// <summary>
    /// 해상도 프로파일 가중치 — <c>WxH</c>/<c>4K</c>/<c>1080p</c>/<c>720p</c> 표기 지원.
    /// 4K(장변 ≥ 3840) 또는 32:9(종횡비 ≥ 3) = 1.8× · 세로(9:16) = 0.7× · 1080p 가로 = 1.0×(<paramref name="fps"/> ≤ 30 이면 0.7×) · 그 이하 = 0.7×.
    /// </summary>
    public static double ResolutionWeight(string? resolution, int? fps = null)
    {
        var s = (resolution ?? "").Trim();
        if (s.Length == 0) return WeightBaseline;
        if (s.Contains("4K", StringComparison.OrdinalIgnoreCase) || s.Contains("2160", StringComparison.Ordinal) || s.Contains("32:9", StringComparison.Ordinal)) return WeightUltra;
        if (s.Contains("9:16", StringComparison.Ordinal)) return WeightLight;
        int w = 0, h = 0;
        var x = s.IndexOfAny(['x', 'X', '×']);
        if (x > 0)
        {
            var end = x + 1;
            while (end < s.Length && char.IsAsciiDigit(s[end])) end++;
            _ = int.TryParse(s.AsSpan(0, x).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out w);
            _ = int.TryParse(s.AsSpan(x + 1, end - x - 1), NumberStyles.None, CultureInfo.InvariantCulture, out h);
        }
        else if (s.EndsWith('p') || s.EndsWith('P'))
        {
            if (int.TryParse(s.AsSpan(0, s.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out h)) w = h * 16 / 9;
        }
        if (w <= 0 || h <= 0) return WeightBaseline;
        var longSide = Math.Max(w, h);
        if (longSide >= 3840 || (double)w / h >= 3.0) return WeightUltra;
        if (h > w) return WeightLight;
        if (longSide >= 1920) return fps is > 0 and <= 30 ? WeightLight : WeightBaseline;
        return WeightLight;
    }

    private static int? FpsOf(BatchRenderJob job)
        => job.Metadata is { } m && m.TryGetValue("fps", out var f) && int.TryParse(f, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>작업 가중치(해상도 + 메타 <c>fps</c>).</summary>
    public static double JobWeight(BatchRenderJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return ResolutionWeight(job.Resolution, FpsOf(job));
    }

    /// <summary>처리량 표본 기록 — 실측 소요(ms)를 가중치로 나눠 기준(1.0×) 초/작업으로 환산. 무효 값 무시.</summary>
    public void RecordThroughputSample(long elapsedMs, double weight)
    {
        if (elapsedMs <= 0 || !(weight > 0) || !double.IsFinite(weight)) return;
        lock (_gate)
        {
            _etaSamples[_etaNext] = elapsedMs / 1000.0 / weight;
            _etaNext = (_etaNext + 1) % EtaSampleWindow;
            if (_etaCount < EtaSampleWindow) _etaCount++;
        }
    }

    /// <summary>⚡ 인코더 텔레메트리(<c>encodeElapsedMs</c>) 표본 기록.</summary>
    public void RecordThroughputSample(EncoderTelemetry? telemetry, string? resolution)
    {
        if (telemetry is null) return;
        RecordThroughputSample(telemetry.EncodeElapsedMs, ResolutionWeight(resolution));
    }

    /// <summary>롤링 평균 기준 초/작업(1080p60 환산 · 표본 없음 = null).</summary>
    public double? BaselineSecondsPerJob
    {
        get
        {
            lock (_gate)
            {
                if (_etaCount == 0) return null;
                var sum = 0.0;
                for (var i = 0; i < _etaCount; i++) sum += _etaSamples[i];
                return sum / _etaCount;
            }
        }
    }

    /// <summary>⏱️ 남은 예상 소요 — 대기 작업 가중 합 + 진행 중 작업 잔여 비율. 표본 없음/남은 작업 없음 = null.</summary>
    public TimeSpan? EstimatedRemainingTime
    {
        get
        {
            lock (_gate)
            {
                if (_etaCount == 0) return null;
                var sum = 0.0;
                for (var i = 0; i < _etaCount; i++) sum += _etaSamples[i];
                var baseline = sum / _etaCount;
                var total = 0.0;
                var any = false;
                for (var i = 0; i < Queue.Count; i++)
                {
                    var j = Queue[i];
                    if (j.Status == BatchJobStatus.Queued) { total += baseline * JobWeight(j); any = true; }
                    else if (j.Status == BatchJobStatus.Processing)
                    {
                        total += baseline * JobWeight(j) * (1.0 - Math.Clamp(j.ProgressPercent, 0, 100) / 100.0);
                        any = true;
                    }
                }
                return any ? TimeSpan.FromSeconds(Math.Max(0, total)) : null;
            }
        }
    }

    /// <summary>⏱️ 예상 완료 시각(<see cref="EtaClock"/> + 남은 소요 · 산출 불가 = null).</summary>
    public DateTimeOffset? EstimatedCompletionTime => EstimatedRemainingTime is { } r ? EtaClock() + r : null;

    /// <summary>UI 라벨 — <c>⏱️ ETA: ~{mm:ss} (Est. {HH:mm:ss} KST)</c>(분은 60 초과 시 누적 표기).</summary>
    public static string FormatEta(TimeSpan remaining, DateTimeOffset completion)
    {
        var secs = (long)Math.Ceiling(Math.Max(0, remaining.TotalSeconds));
        var kst = completion.ToOffset(KstOffset);
        return "⏱️ ETA: ~" + (secs / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (secs % 60).ToString("00", CultureInfo.InvariantCulture)
            + " (Est. " + kst.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " KST)";
    }
}
