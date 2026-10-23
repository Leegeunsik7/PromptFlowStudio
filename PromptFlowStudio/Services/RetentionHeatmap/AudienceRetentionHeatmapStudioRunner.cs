using System.Diagnostics;
using System.Globalization;
using PromptFlowStudio.Services.MatrixSyndication;
using PromptFlowStudio.Services.PostPublishSplitTest;

namespace PromptFlowStudio.Services.RetentionHeatmap;

/// <summary>
/// [PromptFlow Studio v17.98.0] 📉 시청자 유지율 히트맵 &amp; 리에디터 러너(HUD <c>ArhRun</c>).
///   ▸ 체인: 초 단위 유지율 곡선 → <see cref="AudienceRetentionDropoffDetectorEngine"/> 절벽/데드존 →
///     1 클릭 심폐소생 = <see cref="MicroPunchDynamicPacingReEditorEngine"/> 펀치 · FX · WSOLA 리패키징 + 출력 프레임 줌 렌더 →
///     <see cref="RetentionRescueRepublishGuard"/> 판정 → v17.93 A/B Variant-R · v17.81 매트릭스 · 로컬 대기열 이관.
///   ▸ 시뮬레이션 가상 시계 · 결정적 프리셋 곡선 · 절차 합성 발화 오디오(데드존 = 긴 무음 간격).
/// </summary>
public sealed class AudienceRetentionHeatmapStudioRunner : IDisposable
{
    public const int ClipMaxSec = 30, LogSize = 32, VideoW = 48, VideoH = 84, ClipIdBase = 9800;
    public const long SimEpoch = 1_800_000_000;

    /// <summary>프리셋 — 이름 · 길이(s) · 절벽(초, 낙폭) · 데드존(시작, 끝, 누수).</summary>
    public static readonly string[] PresetNames = { "🍳 30초 원팬 파스타", "🎮 보스전 하이라이트", "💄 데일리 메이크업" };
    private static readonly int[] PresetSec = { 30, 25, 28 };
    private static readonly float[][] PresetCliffs = { new[] { 7f, 0.12f, 23f, 0.08f }, new[] { 4f, 0.09f, 18f, 0.07f, 20f, 0.06f }, System.Array.Empty<float>() };
    private static readonly float[][] PresetDead = { new[] { 14f, 19f, 0.022f }, new[] { 9f, 14f, 0.02f }, System.Array.Empty<float>() };
    private static readonly float[] PresetBaseLeak = { 0.008f, 0.009f, 0.01f };

    private readonly IServiceProvider? _sp;
    private readonly string[] _log = new string[LogSize];
    private readonly float[] _curve = new float[ClipMaxSec + 1];
    private readonly float[] _src = new float[ClipMaxSec * MicroPunchDynamicPacingReEditorEngine.Fs];
    private readonly float[] _out = new float[ClipMaxSec * MicroPunchDynamicPacingReEditorEngine.Fs];
    private readonly uint[] _frame = new uint[VideoW * VideoH];
    private readonly uint[] _frameOut = new uint[VideoW * VideoH];
    private int _logHead, _logCount, _srcSamples;

    public AudienceRetentionHeatmapStudioRunner(IServiceProvider sp) : this() => _sp = sp;

    public AudienceRetentionHeatmapStudioRunner()
    {
        for (var y = 0; y < VideoH; y++)
            for (var x = 0; x < VideoW; x++)
                _frame[y * VideoW + x] = 0xFF000000u | ((uint)(x * 255 / VideoW) << 16) | ((uint)(y * 255 / VideoH) << 8) | 0x40u;
        ApplyPreset(0);
    }

    public event Action? OnChange;
    public AudienceRetentionDropoffDetectorEngine Detector { get; } = new();
    public MicroPunchDynamicPacingReEditorEngine ReEditor { get; } = new(ClipMaxSec);
    public RetentionRescueRepublishGuard Guard { get; } = new();
    public long SimNow { get; private set; } = SimEpoch;
    public int Preset { get; private set; }
    public int ClipId => ClipIdBase + Preset;
    public bool Rescued { get; private set; }
    public RescueTarget LastTarget { get; private set; }
    public long Rescues { get; private set; }
    public long RouteFailures { get; private set; }
    public int OutSamples { get; private set; }
    public int PunchedFrames { get; private set; }
    public float MaxFrameScale { get; private set; }
    public double LastRescueMs { get; private set; }
    public double MaxAnalyzeUs { get; private set; }
    public int LogCount => _logCount;
    public string? LastAction { get; private set; }
    /// <summary>임계 슬라이더(0.1 %p 단위 · 20~150).</summary>
    public int ThresholdTenths => (int)MathF.Round(Detector.Threshold * 1000f);
    public long ElapsedSec => SimNow - SimEpoch;
    public ReadOnlySpan<float> SourceAudio => _src.AsSpan(0, _srcSamples);
    public ReadOnlySpan<float> OutputAudio => _out.AsSpan(0, OutSamples);

    /// <summary>최근 로그(0 = 최신).</summary>
    public string Log(int i) => _log[(_logHead - 1 - i + LogSize * 2) % LogSize];

    // ══════════════════════ 조작(비핫패스) ══════════════════════

    public void SetPreset(int preset)
    {
        ApplyPreset(Math.Clamp(preset, 0, PresetNames.Length - 1));
        LastAction = "📼 프리셋: " + PresetNames[Preset] + " · 절벽 " + Id(Detector.SlumpCount) + " · 데드존 " + Id(Detector.DeadZoneCount);
        Push(LastAction);
        Raise();
    }

    public void SetThresholdTenths(int tenths)
    {
        Detector.Threshold = Math.Clamp(tenths, 20, 150) / 1000f;
        Analyze();
        LastAction = "🎚 절벽 임계 " + Pp(Detector.Threshold) + "%p · 절벽 " + Id(Detector.SlumpCount);
        Raise();
    }

    /// <summary>재분석 — 기존 리에디트 결과 무효화. 반환 = 절벽 수.</summary>
    public int Analyze()
    {
        var n = Detector.Analyze(_curve.AsSpan(0, PresetSec[Preset] + 1));
        if (Detector.LastAnalyzeUs > MaxAnalyzeUs) MaxAnalyzeUs = Detector.LastAnalyzeUs;
        Rescued = false;
        OutSamples = PunchedFrames = 0;
        MaxFrameScale = 1f;
        ReEditor.Reset();
        return n;
    }

    /// <summary>1 클릭 자동 심폐소생 — 계획 · 리패키징 · 프레임 렌더 · 가드 판정 · 이관. 반환 = 판정.</summary>
    public RescueVerdict Rescue()
    {
        if (Detector.Samples == 0) Analyze();
        var t0 = Stopwatch.GetTimestamp();
        ReEditor.Plan(Detector, SourceAudio);
        OutSamples = ReEditor.Repackage(SourceAudio, _out);
        RenderFrames();
        LastRescueMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        Rescued = true;
        Rescues++;
        var v = Guard.Evaluate(ClipId, Detector.SlumpCount, ReEditor.UpliftPp, ReEditor.PunchCount, ReEditor.SourceDurationMs, ReEditor.TempoCoveredMs, SimNow);
        LastTarget = RescueTarget.None;
        if (v == RescueVerdict.Approved)
        {
            LastTarget = Route();
            Guard.Commit(ClipId, LastTarget, ReEditor.UpliftPp, ReEditor.PredictedCompletion, SimNow);
            LastAction = "🫀 심폐소생 완료 · 펀치 " + Id(ReEditor.PunchCount) + " · 템포 " + Id(ReEditor.TempoCount) + " · 예상 +" + ReEditor.UpliftPp.ToString("0.0", CultureInfo.InvariantCulture) +
                         "%p → " + RetentionRescueRepublishGuard.TargetName(LastTarget) + " 이관";
        }
        else
            LastAction = "🛑 재투입 보류 — " + RetentionRescueRepublishGuard.VerdictName(v) + " · 예상 +" + ReEditor.UpliftPp.ToString("0.0", CultureInfo.InvariantCulture) + "%p";
        Push(LastAction);
        Raise();
        return v;
    }

    /// <summary>가상 시계 전진(시간) — 중복/일일 상한 해제 확인용.</summary>
    public void AdvanceHours(int hours)
    {
        SimNow += Math.Clamp(hours, 1, 48) * 3600L;
        LastAction = "⏩ +" + Id(hours) + "시간 · 24시간 내 이관 " + Id(Guard.CountSince(SimNow - RetentionRescueRepublishGuard.DaySec)) + "/" + Id(RetentionRescueRepublishGuard.DailyCap);
        Raise();
    }

    public void Reset()
    {
        Detector.Reset(); ReEditor.Reset(); Guard.Reset();
        SimNow = SimEpoch;
        Rescues = RouteFailures = 0;
        LastTarget = RescueTarget.None;
        LastRescueMs = MaxAnalyzeUs = 0;
        Array.Clear(_log);
        _logHead = _logCount = 0;
        ApplyPreset(Preset);
        LastAction = "♻ 초기화";
        Raise();
    }

    // ══════════════════════ 내부 ══════════════════════

    private void ApplyPreset(int p)
    {
        Preset = p;
        BuildCurve(p, _curve);
        _srcSamples = SynthesizeSpeech(p, _src);
        Analyze();
    }

    /// <summary>프리셋 곡선 — 0→1 s 훅 이탈 4 %p · 기본 누수 · 데드존 누수 · 절벽 낙폭.</summary>
    public static int BuildCurve(int p, Span<float> dst)
    {
        var sec = PresetSec[p];
        dst[0] = 1f;
        for (var s = 0; s < sec; s++)
        {
            var leak = s == 0 ? 0.04f : PresetBaseLeak[p];
            var dz = PresetDead[p];
            for (var i = 0; i + 2 < dz.Length; i += 3) if (s >= dz[i] && s < dz[i + 1]) leak = dz[i + 2];
            var cl = PresetCliffs[p];
            for (var i = 0; i < cl.Length; i += 2) if (s == (int)cl[i]) leak = cl[i + 1];
            dst[s + 1] = Math.Max(0f, dst[s] - leak);
        }
        return sec + 1;
    }

    /// <summary>절차 발화 — 160 ms 유성 음절(3 배음) · 50 ms 간격 · 4 음절마다 140 ms · 데드존 = 음절 2 개마다 700 ms 무음.</summary>
    private static int SynthesizeSpeech(int p, float[] dst)
    {
        const int fs = MicroPunchDynamicPacingReEditorEngine.Fs;
        var n = PresetSec[p] * fs;
        Array.Clear(dst, 0, n);
        uint rng = 0xA5A5u + (uint)p;
        var pos = 0; var syl = 0;
        var dz = PresetDead[p];
        while (pos < n)
        {
            rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
            var f0 = 180f + rng % 80;
            var len = Math.Min(fs * 16 / 100, n - pos);
            for (var i = 0; i < len; i++)
            {
                var env = MathF.Sin(MathF.PI * i / len);
                var ph = 2f * MathF.PI * f0 * i / fs;
                dst[pos + i] = 0.3f * env * (MathF.Sin(ph) + 0.5f * MathF.Sin(2f * ph) + 0.25f * MathF.Sin(3f * ph));
            }
            pos += len;
            syl++;
            var sec = pos / (float)fs;
            var dead = false;
            for (var i = 0; i + 2 < dz.Length; i += 3) if (sec >= dz[i] && sec < dz[i + 1]) dead = true;
            pos += dead && syl % 2 == 0 ? fs * 7 / 10 : syl % 4 == 0 ? fs * 14 / 100 : fs / 20;
        }
        return n;
    }

    private void RenderFrames()
    {
        var frames = (long)ReEditor.OutDurationMs * MicroPunchDynamicPacingReEditorEngine.VideoFps / 1000;
        PunchedFrames = 0;
        MaxFrameScale = 1f;
        for (long f = 0; f < frames; f++)
        {
            var s = ReEditor.ApplyFrame(_frame, _frameOut, VideoW, VideoH, f);
            if (s > 1.0001f) PunchedFrames++;
            if (s > MaxFrameScale) MaxFrameScale = s;
        }
    }

    private RescueTarget Route()
    {
        var pst = _sp?.GetService(typeof(PostPublishSplitTestStudioRunner)) as PostPublishSplitTestStudioRunner;
        var matrix = _sp?.GetService(typeof(MatrixSyndicationStudioRunner)) as MatrixSyndicationStudioRunner;
        var target = RetentionRescueRepublishGuard.SelectTarget(pst is not null, pst?.Engine.Phase == SplitTestPhase.Tracking, matrix is not null);
        try
        {
            switch (target)
            {
                case RescueTarget.SplitTestVariantR:
                    pst!.Engine.Deploy(ClipId, "원본 · " + PresetNames[Preset], 0, 0, "🛟 Variant-R · " + PresetNames[Preset], 1, 0, pst.SimNow, 60);
                    break;
                case RescueTarget.MatrixSyndication:
                    matrix!.RouteNext();
                    break;
            }
        }
        catch (Exception ex)
        {
            RouteFailures++;
            PromptFlowStudio.Program.Log("RetentionHeatmap.Route", ex);
            target = RescueTarget.Queued;   // 외부 실패 → 로컬 대기열 폴백
        }
        return target;
    }

    private void Push(string line)
    {
        lock (_log)
        {
            _log[_logHead] = "[+" + Id(ElapsedSec) + "s] " + line;
            _logHead = (_logHead + 1) % LogSize;
            if (_logCount < LogSize) _logCount++;
        }
    }

    private static string Id(long v) => v.ToString(CultureInfo.InvariantCulture);

    public static string Pp(float ratio) => (ratio * 100f).ToString("0.0", CultureInfo.InvariantCulture);

    public string StatusLine() =>
        "절벽 " + Id(Detector.SlumpCount) + " · 데드존 " + Id(Detector.DeadZoneCount) + " · 평균 유지 " + Pp(Detector.AverageRetention) + "% · 완청 " + Pp(Detector.Completion) +
        "% · 상대 " + Detector.RelativeScore.ToString("0", CultureInfo.InvariantCulture) + " · 예상 +" + ReEditor.UpliftPp.ToString("0.0", CultureInfo.InvariantCulture) + "%p · 이관 " + Id(Guard.HandoffCount) +
        " · 분석 " + Detector.LastAnalyzeUs.ToString("0.0", CultureInfo.InvariantCulture) + "µs";

    private void Raise()
    {
        try { OnChange?.Invoke(); }
        catch (Exception ex) { PromptFlowStudio.Program.Log("RetentionHeatmap.OnChange", ex); }
    }

    public void Dispose() => OnChange = null;
}
