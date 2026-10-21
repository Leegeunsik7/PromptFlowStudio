using PromptFlowStudio.Services.BeatSync;
using PromptFlowStudio.Services.Publisher;
using PromptFlowStudio.Services.StationMaster;

namespace PromptFlowStudio.Services.RetentionOptimizer;

public enum DropCause : byte { None, WeakHook, BoringExplainer, BgmOverDucked, SlowPacing, WeakEnding }
public enum DiagnosisVerdict : byte { Healthy, Underperforming, Stale, InsufficientData, Duplicate }

/// <summary>v17.75.0 클립 초 단위 구조 특징(패키징/EPG 기록 · 값 형식).</summary>
public struct ClipSecondFeature
{
    /// <summary>발화 속도(WPM · 0 = 무발화).</summary>
    public float SpeechWpm;
    /// <summary>BGM 더킹 감쇄(dB · 음수 · 0 = 없음).</summary>
    public float BgmDuckDb;
    /// <summary>직전 컷 이후 경과(s).</summary>
    public float SecSinceCut;
    /// <summary>모션 에너지 0~1.</summary>
    public float Motion;
}

/// <summary>v17.75.0 진단 1 건(링 보관 · 값 형식).</summary>
public struct RetentionDiagnosis
{
    public int ReelId;
    public SocialPlatform Platform;
    public DiagnosisVerdict Verdict;
    public DropCause Cause;
    public float Completion, HookDrop, DropPct, Severity;
    public int DropSec;
    public float SpeechDelta, CutDeltaSec, DuckDeltaDb, BgmEnergyDelta;
    public long AtUnix;
    public bool Applied;
}

/// <summary>
/// [PromptFlow Studio v17.75.0] 🩺 저성과 클립 자가 진단 &amp; EPG 피드백 세이프티 가드(<see cref="Diagnose"/> · <see cref="ApplyTo"/> 무할당).
///   ▸ 대상: 배포 3 h 이내 · 조회 ≥ 100 · 완청률(유지 곡선 마지막 값) &lt; 35 %.
///   ▸ 이탈 지점: 초당 낙폭 최대 지점(3 s 이후) · 훅 낙폭(0→3 s) &gt; 45 % = 훅 약함 우선. 원인 = 이탈 직전 2 s 구조 특징 평균:
///     더킹 ≤ −16 dB = 과도한 BGM 더킹 · WPM &lt; 135 또는 컷 없이 &gt; 4 s = 중반 지루한 설명 · 모션 &lt; 0.15 = 느린 페이싱 · 마지막 3 s = 엔딩 약함.
///   ▸ 보정(원인별 기본값): 설명 = 발화 +10 % · 컷 −0.5 s / 더킹 = 깊이 +3 dB(얕게) · BGM 에너지 +0.05 / 훅 = 발화 +5 % · 컷 −0.3 s / 페이싱 = 컷 −0.5 s · BGM +0.05 / 엔딩 = 발화 +5 %.
///   ▸ 세이프티: 릴·플랫폼당 1 회(중복 차단) · 적용 최소 간격 5 분 · 누적 한계(발화 +20 % · 컷 −1.5 s · 더킹 +6 dB · BGM +0.15) · 컷 ≥ 1 s · 더킹 −6~−24 dB 클램프.
///   ▸ 반영: v17.70 자가진화 적용값 <see cref="ClosedLoopViralFeedbackSelfEvolutionSynthesizer.Nudge"/>(발화 · BGM) · v17.53 사이드체인 더킹 깊이 · 방송국 컷 주기(<see cref="TargetCutIntervalSec"/>).
/// </summary>
public sealed class UnderperformingClipSelfDiagnosisGuard
{
    public const int Ring = 32, DedupeCap = 128;
    public const float CompletionFloor = 0.35f, HookDropLimit = 0.45f, OverDuckDb = -16f, SlowWpm = 135f, LongShotSec = 4f, LowMotion = 0.15f;
    public const float MaxSpeech = 0.20f, MinCutDelta = -1.5f, MaxDuck = 6f, MaxBgm = 0.15f, MinCutSec = 1f;
    public const long WindowSec = 3 * 3600, MinApplyGapSec = 300, MinViews = 100;

    private readonly RetentionDiagnosis[] _ring = new RetentionDiagnosis[Ring];
    private readonly long[] _seen = new long[DedupeCap];
    private int _head, _n, _seenN, _seenHead;
    private long _lastApplyUnix = long.MinValue / 2;

    public int Count => _n;
    public ref readonly RetentionDiagnosis At(int i) => ref _ring[(_head - 1 - i + 2 * Ring) % Ring];
    public long Diagnosed { get; private set; }
    public long Underperformers { get; private set; }
    public long Applied { get; private set; }
    public long Throttled { get; private set; }
    public long Clamped { get; private set; }
    public long Duplicates { get; private set; }
    /// <summary>누적 보정(발화 배율 비율 · 컷 s · 더킹 dB · BGM 에너지).</summary>
    public float SpeechBias { get; private set; }
    public float CutBiasSec { get; private set; }
    public float DuckBiasDb { get; private set; }
    public float BgmBias { get; private set; }
    /// <summary>방송국 기준 컷 주기(s) — 러너가 갱신.</summary>
    public double BaseCutIntervalSec { get; set; } = 2.4;
    public double TargetCutIntervalSec => Math.Max(MinCutSec, BaseCutIntervalSec + CutBiasSec);
    public double LastDiagnoseUs { get; private set; }

    public static string CauseName(DropCause c) => c switch
    {
        DropCause.WeakHook => "훅 약함(첫 3초 이탈)", DropCause.BoringExplainer => "중반 지루한 설명", DropCause.BgmOverDucked => "과도한 BGM 더킹",
        DropCause.SlowPacing => "느린 페이싱", DropCause.WeakEnding => "엔딩 약함", _ => "—",
    };

    public static string VerdictName(DiagnosisVerdict v) => v switch
    {
        DiagnosisVerdict.Healthy => "정상", DiagnosisVerdict.Underperforming => "저성과", DiagnosisVerdict.Stale => "3h 경과", DiagnosisVerdict.InsufficientData => "표본 부족", _ => "중복",
    };

    // ══════════════════════ 진단(무할당) ══════════════════════
    /// <summary>초 단위 유지 곡선(0 s = 1.0 · 시청자 비율) + 구조 특징 → 판정 · 이탈 지점 · 원인 · 보정량. 링 기록 후 반환.</summary>
    public RetentionDiagnosis Diagnose(int reelId, SocialPlatform platform, ReadOnlySpan<float> retention, ReadOnlySpan<ClipSecondFeature> features, long views, long deployedAtUnix, long nowUnix)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var d = new RetentionDiagnosis { ReelId = reelId, Platform = platform, AtUnix = nowUnix, DropSec = -1 };
        Diagnosed++;
        var key = ((long)reelId << 8) | (byte)platform;
        if (nowUnix - deployedAtUnix > WindowSec) d.Verdict = DiagnosisVerdict.Stale;
        else if (views < MinViews || retention.Length < 4) d.Verdict = DiagnosisVerdict.InsufficientData;
        else
        {
            d.Completion = Math.Clamp(retention[^1], 0f, 1f);
            d.HookDrop = Math.Clamp(retention[0] - retention[Math.Min(3, retention.Length - 1)], 0f, 1f);
            if (d.Completion >= CompletionFloor) d.Verdict = DiagnosisVerdict.Healthy;
            else if (Seen(key)) { d.Verdict = DiagnosisVerdict.Duplicate; Duplicates++; }
            else
            {
                d.Verdict = DiagnosisVerdict.Underperforming; Underperformers++;
                Remember(key);
                var best = 0f; var at = -1;
                for (var s = 4; s < retention.Length; s++) { var drop = retention[s - 1] - retention[s]; if (drop > best) { best = drop; at = s; } }
                d.DropSec = at; d.DropPct = best;
                d.Severity = Math.Clamp((CompletionFloor - d.Completion) / CompletionFloor, 0f, 1f);
                if (d.HookDrop > HookDropLimit || at < 0) { d.Cause = DropCause.WeakHook; d.DropSec = Math.Min(3, retention.Length - 1); d.DropPct = d.HookDrop; }
                else d.Cause = Classify(features, at, retention.Length);
                switch (d.Cause)
                {
                    case DropCause.BoringExplainer: d.SpeechDelta = 0.10f; d.CutDeltaSec = -0.5f; break;
                    case DropCause.BgmOverDucked: d.DuckDeltaDb = 3f; d.BgmEnergyDelta = 0.05f; break;
                    case DropCause.WeakHook: d.SpeechDelta = 0.05f; d.CutDeltaSec = -0.3f; break;
                    case DropCause.SlowPacing: d.CutDeltaSec = -0.5f; d.BgmEnergyDelta = 0.05f; break;
                    case DropCause.WeakEnding: d.SpeechDelta = 0.05f; break;
                }
            }
        }
        _ring[_head] = d; _head = (_head + 1) % Ring; if (_n < Ring) _n++;
        LastDiagnoseUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        return d;
    }

    private static DropCause Classify(ReadOnlySpan<ClipSecondFeature> f, int at, int len)
    {
        if (at >= len - 3) return DropCause.WeakEnding;
        if (f.IsEmpty) return DropCause.BoringExplainer;
        int a = Math.Clamp(at - 2, 0, f.Length - 1), b = Math.Clamp(at, 0, f.Length - 1);
        float wpm = 0, duck = 0, shot = 0, motion = 0; var n = 0;
        for (var i = a; i <= b; i++) { wpm += f[i].SpeechWpm; duck += f[i].BgmDuckDb; shot = MathF.Max(shot, f[i].SecSinceCut); motion += f[i].Motion; n++; }
        wpm /= n; duck /= n; motion /= n;
        if (duck <= OverDuckDb) return DropCause.BgmOverDucked;
        if ((wpm > 0 && wpm < SlowWpm) || shot > LongShotSec) return DropCause.BoringExplainer;
        if (motion < LowMotion) return DropCause.SlowPacing;
        return DropCause.BoringExplainer;
    }

    private bool Seen(long key) { for (var i = 0; i < _seenN; i++) if (_seen[i] == key) return true; return false; }
    private void Remember(long key) { _seen[_seenHead] = key; _seenHead = (_seenHead + 1) % DedupeCap; if (_seenN < DedupeCap) _seenN++; }

    // ══════════════════════ 반영(무할당 · 세이프티) ══════════════════════
    /// <summary>최신 저성과 진단 → 누적 한계 클램프 → 자가진화 · 더킹 반영. false = 대상 없음/쿨다운.</summary>
    public bool ApplyTo(ClosedLoopViralFeedbackSelfEvolutionSynthesizer? evo, VoiceActivatedSidechainDuckingSynthesizer? ducker, long nowUnix)
    {
        if (_n == 0) return false;
        ref var d = ref _ring[(_head - 1 + Ring) % Ring];
        if (d.Verdict != DiagnosisVerdict.Underperforming || d.Applied) return false;
        if (nowUnix - _lastApplyUnix < MinApplyGapSec) { Throttled++; return false; }
        var sp = Math.Clamp(SpeechBias + d.SpeechDelta, -MaxSpeech, MaxSpeech) - SpeechBias;
        var cut = Math.Clamp(CutBiasSec + d.CutDeltaSec, MinCutDelta, 0f) - CutBiasSec;
        var duck = Math.Clamp(DuckBiasDb + d.DuckDeltaDb, 0f, MaxDuck) - DuckBiasDb;
        var bgm = Math.Clamp(BgmBias + d.BgmEnergyDelta, 0f, MaxBgm) - BgmBias;
        if (sp != d.SpeechDelta || cut != d.CutDeltaSec || duck != d.DuckDeltaDb || bgm != d.BgmEnergyDelta) Clamped++;
        SpeechBias += sp; CutBiasSec += cut; DuckBiasDb += duck; BgmBias += bgm;
        if (evo is not null)
        {
            if (sp != 0) evo.Nudge(EvoParam.SpeechRate, evo.SpeechRate * sp);
            if (bgm != 0) evo.Nudge(EvoParam.BgmEnergy, bgm);
        }
        if (ducker is not null && duck != 0) ducker.DepthDb = Math.Clamp(ducker.DepthDb + duck, -24f, -6f);
        d.Applied = true; _lastApplyUnix = nowUnix; Applied++;
        return true;
    }

    /// <summary>평균 시청(s) 기반 지수 생존 유지 곡선 추정(유지 곡선 API 부재 플랫폼 · 1 s 간격).</summary>
    public static int EstimateCurve(double avgViewSec, double durationSec, Span<float> dst)
    {
        var n = Math.Min(dst.Length, (int)Math.Ceiling(durationSec) + 1);
        var tau = Math.Max(0.5, avgViewSec);
        for (var s = 0; s < n; s++) dst[s] = (float)Math.Exp(-s / tau);
        return n;
    }

    /// <summary>비율 곡선(1 % 간격) → 초 단위 재표본(선형 · 0 s = 1.0).</summary>
    public static int ResampleToSeconds(ReadOnlySpan<float> ratioCurve, double durationSec, Span<float> dst)
    {
        var n = Math.Min(dst.Length, (int)Math.Ceiling(durationSec) + 1);
        if (ratioCurve.IsEmpty || n == 0) return 0;
        for (var s = 0; s < n; s++)
        {
            var r = Math.Min(1.0, s / durationSec) * ratioCurve.Length;   // 점 k = 비율 (k+1)/len
            if (r <= 1) { dst[s] = (float)(1 + (ratioCurve[0] - 1) * r); continue; }
            var i = Math.Min(ratioCurve.Length - 1, (int)r) - 1; var f = (float)(r - (i + 1));
            dst[s] = i + 1 < ratioCurve.Length ? ratioCurve[i] + (ratioCurve[i + 1] - ratioCurve[i]) * f : ratioCurve[^1];
        }
        return n;
    }
}
