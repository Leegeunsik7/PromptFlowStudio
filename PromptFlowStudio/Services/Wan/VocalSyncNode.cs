using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PromptFlowStudio.Services.Wan;

/// <summary>컷 다이내믹 등급 — 구간 평균 강도로 판정(고조/중간/잔잔).</summary>
public enum VocalDynamic { Calm, Mid, Energetic }

/// <summary>
/// [PromptFlow Studio v6.0.2] 오디오 동기화 컷 1개 — 타임코드 구간 + 평균 강도 + 립싱크 프롬프트 수식어.
/// </summary>
public sealed record VocalSyncCut(int Index, double StartSec, double EndSec, double Intensity, VocalDynamic Dynamic, string LipSyncModifier)
{
    /// <summary>구간 길이(초).</summary>
    public double LengthSec => Math.Max(0, EndSec - StartSec);

    /// <summary>"0:03–0:05" 형태 타임코드 라벨(로캘 독립).</summary>
    public string TimecodeLabel => $"{Mmss(StartSec)}–{Mmss(EndSec)}";

    private static string Mmss(double s)
    {
        if (s < 0 || double.IsNaN(s)) s = 0;
        var m = (int)(s / 60);
        var sec = (int)Math.Round(s - m * 60);
        if (sec == 60) { m++; sec = 0; }
        return m.ToString(CultureInfo.InvariantCulture) + ":" + sec.ToString("00", CultureInfo.InvariantCulture);
    }
}

/// <summary>보컬 싱크 계획 — 트랙 + N개 컷 + 컷별 프롬프트 수식어. FlowExecutionPipeline 컷 오버라이드로 결선된다.</summary>
public sealed record VocalSyncPlan(AudioTrackInfo Track, IReadOnlyList<VocalSyncCut> Cuts)
{
    public int CutCount => Cuts.Count;

    /// <summary>비어 있는(오디오 없음) 계획.</summary>
    public static VocalSyncPlan Empty { get; } = new(AudioTrackInfo.Empty, Array.Empty<VocalSyncCut>());
}

/// <summary>
/// [PromptFlow Studio v6.0.2] Audio-to-Video 보컬 립싱크 노드 — 순수 C# BCL(외부 SDK/JS 0 · 결정적).
///   ▸ <see cref="Plan"/>: 오디오 길이를 N개 컷으로 균등 분할, 각 컷의 평균 볼륨 강도로 다이내믹 등급을 매겨 립싱크 수식어를 배정.
///   ▸ <see cref="AppendLipSync"/>: 특정 컷 수식어를 프롬프트 말미에 <b>멱등</b> 결합(중복 주입 차단 · insight 3).
///   ▸ <see cref="BuildCutPrompts"/>: 기저 프롬프트에 컷별 수식어를 얹은 컷 오버라이드 목록 → RunShortsChainAsync(cutPrompts) 직결.
/// </summary>
public static class VocalSyncNode
{
    /// <summary>립싱크 수식어 공통 마커(멱등 판정용) — 이 문구가 이미 있으면 재주입하지 않는다.</summary>
    public const string LipSyncMarker = "lip-sync";

    // 등급별 립싱크 프롬프트 수식어(카메라 근접도 + 표정 다이내믹).
    private const string EnergeticMod = "(close-up singing, dynamic lip-sync, energetic expression, rhythmic head motion)";
    private const string MidMod = "(medium shot, natural lip-sync, expressive singing, subtle sway)";
    private const string CalmMod = "(wide calm shot, soft lip-sync, gentle expression, slow breathing)";

    // 등급 임계(구간 평균 강도 0~1).
    private const double EnergeticThreshold = 0.62;
    private const double CalmThreshold = 0.32;

    /// <summary>
    /// 오디오 트랙을 <paramref name="cutCount"/> 개 컷으로 균등 분할하고 컷별 강도·다이내믹·립싱크 수식어를 배정한다.
    ///   트랙이 비어 있으면 <see cref="VocalSyncPlan.Empty"/>. cutCount 는 [1, 8]로 클램프(concat 용량).
    /// </summary>
    public static VocalSyncPlan Plan(AudioTrackInfo? track, int cutCount)
    {
        if (track is null || track.DurationSec <= 0 || track.Buckets == 0) return VocalSyncPlan.Empty;
        var n = Math.Clamp(cutCount, 1, FlowConcatCapacity);
        var seg = track.DurationSec / n;

        var cuts = new List<VocalSyncCut>(n);
        for (var i = 0; i < n; i++)
        {
            var start = i * seg;
            var end = i == n - 1 ? track.DurationSec : (i + 1) * seg;   // 마지막 컷은 끝까지(부동소수 잔차 흡수)
            var intensity = track.AverageIntensityBetween(start, end);
            var dyn = DynamicFor(intensity);
            cuts.Add(new VocalSyncCut(i + 1, start, end, intensity, dyn, ModifierFor(dyn)));
        }
        return new VocalSyncPlan(track, cuts);
    }

    /// <summary>
    /// 사용자가 조정한 임의 컷 경계(내부 분할 지점 · 0~1 프랙션)로 계획을 만든다 — 파형 마커 드래그 결과 반영.
    ///   <paramref name="interiorFractions"/> 는 (0,1) 사이 값들(정렬·중복/근접 제거). 컷 수 = 경계 수 + 1(최대 8).
    /// </summary>
    public static VocalSyncPlan PlanFromBoundaries(AudioTrackInfo? track, IReadOnlyList<double>? interiorFractions)
    {
        if (track is null || track.DurationSec <= 0 || track.Buckets == 0) return VocalSyncPlan.Empty;

        // 유효 경계만: (0,1) 범위, 오름차순, 인접 간격 최소 2% 확보(빈 컷 방지).
        var fr = (interiorFractions ?? Array.Empty<double>())
            .Where(f => f > 0.001 && f < 0.999).OrderBy(f => f).ToList();
        var bounds = new List<double>();
        foreach (var f in fr)
        {
            if (bounds.Count >= FlowConcatCapacity - 1) break;   // 컷 수 상한(=8)
            if (bounds.Count == 0 || f - bounds[^1] >= 0.02) bounds.Add(f);
        }

        var edges = new List<double> { 0.0 };
        edges.AddRange(bounds);
        edges.Add(1.0);

        var cuts = new List<VocalSyncCut>(edges.Count - 1);
        for (var i = 0; i < edges.Count - 1; i++)
        {
            var start = edges[i] * track.DurationSec;
            var end = edges[i + 1] * track.DurationSec;
            var intensity = track.AverageIntensityBetween(start, end);
            var dyn = DynamicFor(intensity);
            cuts.Add(new VocalSyncCut(i + 1, start, end, intensity, dyn, ModifierFor(dyn)));
        }
        return new VocalSyncPlan(track, cuts);
    }

    /// <summary>강도(0~1) → 다이내믹 등급.</summary>
    public static VocalDynamic DynamicFor(double intensity)
        => intensity >= EnergeticThreshold ? VocalDynamic.Energetic
         : intensity <= CalmThreshold ? VocalDynamic.Calm
         : VocalDynamic.Mid;

    /// <summary>다이내믹 등급 → 립싱크 프롬프트 수식어.</summary>
    public static string ModifierFor(VocalDynamic dyn) => dyn switch
    {
        VocalDynamic.Energetic => EnergeticMod,
        VocalDynamic.Calm => CalmMod,
        _ => MidMod,
    };

    /// <summary>
    /// 프롬프트 말미에 립싱크 수식어를 멱등 결합한다. 이미 립싱크 마커가 있으면 원본 그대로 반환(중복 차단 · insight 3).
    /// </summary>
    public static string AppendLipSync(string? prompt, string? modifier)
    {
        var p = (prompt ?? "").Trim();
        var m = (modifier ?? "").Trim();
        if (m.Length == 0) return p;
        if (p.Contains(LipSyncMarker, StringComparison.OrdinalIgnoreCase)) return p;   // 멱등
        return p.Length == 0 ? m : p + ", " + m;
    }

    /// <summary>
    /// 기저 프롬프트에 컷별 립싱크 수식어를 얹은 컷 오버라이드 목록 — RunShortsChainAsync 의 cutPrompts 로 그대로 전달한다.
    /// </summary>
    public static IReadOnlyList<string> BuildCutPrompts(string? basePrompt, VocalSyncPlan plan)
    {
        if (plan is null || plan.CutCount == 0) return Array.Empty<string>();
        var baseP = (basePrompt ?? "").Trim();
        return plan.Cuts.Select(c => AppendLipSync(baseP, c.LipSyncModifier)).ToList();
    }

    /// <summary>계획 요약(대표 다이내믹) → 생성 닥 초안에 부착할 단일 립싱크 수식어(가장 지배적 등급 기준).</summary>
    public static string DominantModifier(VocalSyncPlan plan)
    {
        if (plan is null || plan.CutCount == 0) return "";
        var dominant = plan.Cuts.GroupBy(c => c.Dynamic)
                                .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key)
                                .First().Key;
        return ModifierFor(dominant);
    }

    // FlowExecutionPipeline.ConcatInputs(=8)와 동일 용량 상한. 순환 참조 없이 상수만 미러링(결정적).
    private const int FlowConcatCapacity = 8;
}
