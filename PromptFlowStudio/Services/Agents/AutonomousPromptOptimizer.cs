using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PromptFlowStudio.Services.Timeline;   // MultiTrackTimelineState · TimelineTrackKind

namespace PromptFlowStudio.Services.Agents;

/// <summary>최적화 표본(불변) — 원본/압축 토큰 수 · 품질 점수 · 시각.</summary>
public sealed record OptimizationSample(int OriginalTokens, int OptimizedTokens, double QualityScore, long AtMs)
{
    public double CompressionRatio => OriginalTokens <= 0 ? 0 : 1.0 - (double)OptimizedTokens / OriginalTokens;
}

/// <summary>
/// [PromptFlow Studio v8.5.1] 자율 AI 프롬프트 최적화 엔진(관찰 가능 싱글턴 · 순수 BCL · 외부 NuGet 0).
///   ▸ <see cref="AgentSwarmOrchestrator"/>(결함/교정)·<see cref="MultiTrackTimelineState"/>(컷/프롬프트 커버리지) 실행 피드백으로 품질 점수를 산출.
///   ▸ <see cref="Compress"/> 는 마커/중복 태그를 <b>멱등</b> 제거해 토큰을 압축(한글 원문 보존) — 두 번 돌려도 같은 결과로 수렴.
///   ▸ <see cref="Optimize"/> 가 압축+피드백을 EWMA 로 누적해 자율 튜닝 지표(<see cref="AvgQuality"/>·<see cref="AvgCompression"/>)를 갱신.
///   ⚠ <see cref="OnChange"/> 는 호출(임의) 스레드에서 온다 — 컴포넌트는 InvokeAsync(StateHasChanged)로 마샬링.
/// </summary>
public sealed class AutonomousPromptOptimizer
{
    private const int LogCap = 48;
    private readonly object _gate = new();
    private readonly List<OptimizationSample> _log = new();
    private double _ewmaQuality;
    private double _ewmaCompression;

    public event Action? OnChange;
    private void Notify() => OnChange?.Invoke();

    public long SamplesObserved { get; private set; }
    public long TotalTokensSaved { get; private set; }
    public double AvgQuality => _ewmaQuality;
    public double AvgCompression => _ewmaCompression;
    public double LastCompressionRatio { get; private set; }
    public IReadOnlyList<OptimizationSample> Recent { get { lock (_gate) return _log.Take(20).ToList(); } }

    /// <summary>토큰 근사 카운트 — 공백/쉼표 분할 비어있지 않은 세그먼트 수.</summary>
    public static int CountTokens(string? prompt)
        => string.IsNullOrWhiteSpace(prompt) ? 0
           : prompt.Split(new[] { ' ', ',', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>
    /// 멱등 프롬프트 압축 — 쉼표 구분 태그를 트림·중복(대소문자 무시) 제거·공백 정규화. 한글 원문 태그는 순서 보존.
    /// </summary>
    public static string Compress(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return "";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>();
        foreach (var raw in prompt.Split(','))
        {
            var seg = CollapseSpaces(raw.Trim());
            if (seg.Length == 0) continue;
            if (seen.Add(seg)) kept.Add(seg);   // 최초 출현만 유지(멱등)
        }
        return string.Join(", ", kept);
    }

    private static string CollapseSpaces(string s)
    {
        var sb = new StringBuilder(s.Length);
        var prevSpace = false;
        foreach (var ch in s)
        {
            var isSpace = ch is ' ' or '\t' or '\n' or '\r';
            if (isSpace) { if (!prevSpace && sb.Length > 0) sb.Append(' '); prevSpace = true; }
            else { sb.Append(ch); prevSpace = false; }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 프롬프트를 압축하고 스웜/타임라인 피드백으로 품질을 산출해 표본을 누적한다. 반환=압축 프롬프트.
    /// </summary>
    public string Optimize(string? prompt, AgentSwarmOrchestrator? swarm = null, MultiTrackTimelineState? timeline = null)
    {
        var original = CountTokens(prompt);
        var compressed = Compress(prompt);
        var optimized = CountTokens(compressed);
        var quality = ScoreQuality(swarm, timeline);

        var sample = new OptimizationSample(original, optimized, quality, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        lock (_gate) { _log.Insert(0, sample); if (_log.Count > LogCap) _log.RemoveRange(LogCap, _log.Count - LogCap); }

        SamplesObserved++;
        TotalTokensSaved += Math.Max(0, original - optimized);
        LastCompressionRatio = sample.CompressionRatio;
        _ewmaQuality = _ewmaQuality <= 0 ? quality : 0.25 * quality + 0.75 * _ewmaQuality;
        _ewmaCompression = _ewmaCompression <= 0 ? sample.CompressionRatio : 0.25 * sample.CompressionRatio + 0.75 * _ewmaCompression;
        Notify();
        return compressed;
    }

    /// <summary>스웜 교정율 + 타임라인 프롬프트 커버리지로 품질 점수(0~1) 산출.</summary>
    private static double ScoreQuality(AgentSwarmOrchestrator? swarm, MultiTrackTimelineState? timeline)
    {
        // 스웜: 감지 대비 교정 비율(결함 없으면 만점).
        var swarmScore = 1.0;
        if (swarm is not null && swarm.DefectsFound > 0)
            swarmScore = Math.Clamp((double)swarm.DefectsResolved / swarm.DefectsFound, 0, 1);

        // 커버리지: 비디오 컷을 덮는 프롬프트 오버라이드 비율.
        var coverage = 1.0;
        if (timeline is not null)
        {
            var videos = timeline.ClipsFor(TimelineTrackKind.Video);
            var prompts = timeline.ClipsFor(TimelineTrackKind.Prompt);
            if (videos.Count > 0)
            {
                var covered = videos.Count(v => prompts.Any(p => p.StartSec < v.EndSec && p.EndSec > v.StartSec));
                coverage = (double)covered / videos.Count;
            }
        }
        return Math.Clamp(0.5 * swarmScore + 0.5 * coverage, 0, 1);
    }
}
