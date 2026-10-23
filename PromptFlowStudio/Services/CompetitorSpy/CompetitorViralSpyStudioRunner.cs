using System.Diagnostics;
using System.Globalization;
using PromptFlowStudio.Services.OmniViral;

namespace PromptFlowStudio.Services.CompetitorSpy;

/// <summary>
/// [PromptFlow Studio v17.99.0] 🕵️ 경쟁 채널 바이럴 스파이 &amp; 역공학 러너(HUD <c>SpyRun</c>).
///   ▸ 체인: 벤치마크 채널 등록 → 최근 12 영상 메트릭 → <see cref="CompetitorViralOutlierSpikeDetectorEngine"/> 300 % 이상치 →
///     이상치 도입 훅 · 15 s 휘도/오디오 → <see cref="ViralHookAudiovisualReverseEngineer"/> 청사진 →
///     1 클릭 = <see cref="CounterViralScriptFormulaSafetyGuard"/> 합성/표절 방어 → v17.90 오케스트레이터 ①단계 입력(불가 시 로컬 대기열).
///   ▸ 외부 크롤러 없음 — 결정적 프리셋 채널 · 절차 합성 휘도/오디오 · 가상 시계.
/// </summary>
public sealed class CompetitorViralSpyStudioRunner : IDisposable
{
    public const int VideosPerChannel = 12, MaxChannels = 5, LogSize = 24, Fps = 30, Fs = 8000, ClipSec = 15;
    public const long SimEpoch = 1_800_000_000;

    /// <summary>프리셋 경쟁 채널 — 이름 · 평상 조회수 · 이상치(영상 인덱스, 배율) · 샷 길이(s) · 비트 간격(s).</summary>
    public static readonly string[] ChannelNames = { "🍳 쿡킹랩 키친", "💪 핏블리 짐", "💻 테크 언박서", "🎮 겜잘알 TV", "🧘 미니멀 데일리" };
    private static readonly float[] ChannelBaseViews = { 42_000f, 18_000f, 95_000f, 60_000f, 8_000f };
    private static readonly float[][] ChannelSpikes = { new[] { 3f, 6f, 8f, 4f }, new[] { 5f, 4.2f }, new[] { 1f, 8f }, new[] { 2f, 3.4f, 6f, 9f }, System.Array.Empty<float>() };
    private static readonly float[] ChannelShotSec = { 0.8f, 1.2f, 0.6f, 0.5f, 2.0f };
    private static readonly float[] ChannelBeatSec = { 0.5f, 0.75f, 0.4f, 0.35f, 1.5f };
    public static readonly string[] PersonaNames = { "꿀팁 언니", "냉철한 리뷰어", "웃긴 자취생" };

    private static readonly string[] HookPool =
    {
        "이거 모르면 절대 사지 마세요",
        "왜 다들 이 방법을 안 쓸까?",
        "아무도 안 알려준 숨겨진 비밀",
        "다들 맛없다고 했는데 반전이 있었습니다",
        "오늘은 기본 루틴을 정리했습니다",
        "이것 하나 바꿨더니 조회수가 터졌다",
        "경고: 이 실수 하면 후회합니다",
        "어떻게 3분 만에 끝낼 수 있을까?",
        "사실은 우리가 다 틀렸던 겁니다",
        "단 1개만 고르라면 이것",
        "주말 브이로그 모음",
        "위험하니 따라 하지 마세요",
    };

    private readonly IServiceProvider? _sp;
    private readonly string[] _log = new string[LogSize];
    private readonly int[] _registered = new int[MaxChannels];
    private readonly int[] _slotOutliers = new int[MaxChannels];
    private readonly float[] _slotTop = new float[MaxChannels];
    private readonly float[] _slotMean = new float[MaxChannels];
    private readonly float[] _views = new float[VideosPerChannel];
    private readonly float[] _age = new float[VideosPerChannel];
    private readonly float[] _watch = new float[VideosPerChannel];
    private readonly float[] _comments = new float[VideosPerChannel];
    private readonly float[] _luma = new float[ClipSec * Fps];
    private readonly float[] _audio = new float[ClipSec * Fs];
    private int _logHead, _logCount, _variantCursor;

    public CompetitorViralSpyStudioRunner(IServiceProvider sp) : this() => _sp = sp;

    public CompetitorViralSpyStudioRunner()
    {
        RegisterCore(0); RegisterCore(1); RegisterCore(3);
        SelectCore(0);
    }

    public event Action? OnChange;
    public CompetitorViralOutlierSpikeDetectorEngine Detector { get; } = new();
    public ViralHookAudiovisualReverseEngineer Decompiler { get; } = new();
    public CounterViralScriptFormulaSafetyGuard Guard { get; } = new();
    public long SimNow { get; private set; } = SimEpoch;
    public int ChannelCount { get; private set; }
    public int SelectedSlot { get; private set; } = -1;
    public int SelectedOutlier { get; private set; } = -1;
    public int Persona { get; private set; }
    public string Keyword { get; private set; } = "에어프라이어 요리";
    public CounterTarget LastTarget { get; private set; }
    public long Scans { get; private set; }
    public long Counters { get; private set; }
    public long RouteFailures { get; private set; }
    public double LastPipelineMs { get; private set; }
    public string? LastAction { get; private set; }
    public int LogCount => _logCount;
    public int SimilarityPercent => (int)MathF.Round(Guard.SimilarityLimit * 100f);
    public ReadOnlySpan<float> Views => _views;
    public ReadOnlySpan<float> Luma => _luma;
    public ReadOnlySpan<float> Audio => _audio;
    public int ChannelPreset(int slot) => _registered[slot];
    public int SlotOutliers(int slot) => _slotOutliers[slot];
    public float SlotTopScore(int slot) => _slotTop[slot];
    public float SlotMeanViews(int slot) => _slotMean[slot];
    public bool IsRegistered(int preset) { for (var i = 0; i < ChannelCount; i++) if (_registered[i] == preset) return true; return false; }
    public string SelectedChannelName => SelectedSlot >= 0 ? ChannelNames[_registered[SelectedSlot]] : "—";
    public string OutlierHook(int k) => HookOf(SelectedPreset, Detector.Outlier(k).Index);
    public string? SelectedHook => SelectedOutlier >= 0 ? OutlierHook(SelectedOutlier) : null;
    private int SelectedPreset => SelectedSlot >= 0 ? _registered[SelectedSlot] : 0;

    /// <summary>최근 로그(0 = 최신).</summary>
    public string Log(int i) => _log[(_logHead - 1 - i + LogSize * 2) % LogSize];

    // ══════════════════════ 조작(비핫패스) ══════════════════════

    public void Register(int preset)
    {
        preset = Math.Clamp(preset, 0, ChannelNames.Length - 1);
        if (IsRegistered(preset)) LastAction = "ℹ 이미 등록된 채널: " + ChannelNames[preset];
        else if (ChannelCount >= MaxChannels) LastAction = "🛑 벤치마크 채널 상한 " + Id(MaxChannels);
        else
        {
            RegisterCore(preset);
            SelectCore(ChannelCount - 1);
            LastAction = "➕ 벤치마크 등록: " + ChannelNames[preset] + " · 이상치 " + Id(Detector.OutlierCount);
        }
        Push(LastAction);
        Raise();
    }

    public void Remove(int slot)
    {
        if ((uint)slot >= (uint)ChannelCount) return;
        var name = ChannelNames[_registered[slot]];
        for (var i = slot; i < ChannelCount - 1; i++)
        {
            _registered[i] = _registered[i + 1]; _slotOutliers[i] = _slotOutliers[i + 1];
            _slotTop[i] = _slotTop[i + 1]; _slotMean[i] = _slotMean[i + 1];
        }
        ChannelCount--;
        if (ChannelCount == 0) { SelectedSlot = SelectedOutlier = -1; Detector.Reset(); Decompiler.Reset(); }
        else SelectCore(Math.Min(slot, ChannelCount - 1));
        LastAction = "🗑 등록 해제: " + name;
        Push(LastAction);
        Raise();
    }

    public void Select(int slot)
    {
        if ((uint)slot >= (uint)ChannelCount) return;
        SelectCore(slot);
        LastAction = "🔭 스캔: " + SelectedChannelName + " · 이상치 " + Id(Detector.OutlierCount) + " · 평상 평균 " + Id((long)Detector.ChannelMeanViews) + "회";
        Push(LastAction);
        Raise();
    }

    public void SelectOutlierClip(int k)
    {
        if ((uint)k >= (uint)Detector.OutlierCount) return;
        DecompileCore(k);
        LastAction = "🧬 디컴파일: #" + Id(Detector.Outlier(k).Index + 1) + " " + ViralHookAudiovisualReverseEngineer.PatternNames[(int)Decompiler.Blueprint.Hook] +
                     " · 컷 " + Decompiler.Blueprint.CutsPerSec.ToString("0.0", CultureInfo.InvariantCulture) + "/s";
        Push(LastAction);
        Raise();
    }

    public void SetPersona(int persona)
    {
        Persona = Math.Clamp(persona, 0, PersonaNames.Length - 1);
        LastAction = "🎭 페르소나: " + PersonaNames[Persona];
        Raise();
    }

    public void SetKeyword(string? keyword)
    {
        Keyword = (keyword ?? "").Trim();
        if (Keyword.Length > 40) Keyword = Keyword[..40];
        LastAction = "🎯 타깃 키워드: " + (Keyword.Length == 0 ? "(비어 있음)" : Keyword);
        Raise();
    }

    public void SetSimilarityPercent(int percent)
    {
        Guard.SimilarityLimit = Math.Clamp(percent, 20, 60) / 100f;
        LastAction = "🛡 표절 임계 " + Id(SimilarityPercent) + "%";
        Raise();
    }

    /// <summary>1 클릭 대항 기획안 생성 + 파이프라인 전송. 반환 = 판정.</summary>
    public CounterVerdict GenerateAndDispatch()
    {
        if (SelectedOutlier < 0 && Detector.OutlierCount > 0) DecompileCore(0);
        LastTarget = CounterTarget.None;
        if (SelectedOutlier < 0)
        {
            LastAction = "🛑 이상치 없음 — 디컴파일할 클립이 없습니다";
            Push(LastAction);
            Raise();
            return CounterVerdict.EmptyInput;
        }
        var t0 = Stopwatch.GetTimestamp();
        var v = Guard.Compose(in Decompiler.Blueprint, PersonaNames[Persona], Keyword, SelectedHook, _variantCursor++);
        LastPipelineMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        Counters++;
        if (v == CounterVerdict.Approved)
        {
            LastTarget = Route();
            Guard.Commit(LastTarget, Detector.Outlier(SelectedOutlier).Index, SimNow);
            LastAction = "🚀 대항 기획안 전송 → " + CounterViralScriptFormulaSafetyGuard.TargetName(LastTarget) + " · 유사도 " + Pct(Guard.LastSimilarity) + "% · 재합성 " + Id(Guard.LastAttempts - 1);
        }
        else
            LastAction = "🛑 전송 보류 — " + CounterViralScriptFormulaSafetyGuard.VerdictName(v) + (v == CounterVerdict.Plagiarism ? " · 유사도 " + Pct(Guard.LastSimilarity) + "%" : null);
        SimNow += 600;
        Push(LastAction);
        Raise();
        return v;
    }

    public void Reset()
    {
        Detector.Reset(); Decompiler.Reset(); Guard.Reset();
        SimNow = SimEpoch;
        Scans = Counters = RouteFailures = 0;
        LastPipelineMs = 0;
        LastTarget = CounterTarget.None;
        Persona = 0;
        Keyword = "에어프라이어 요리";
        _variantCursor = 0;
        ChannelCount = 0;
        Array.Clear(_log);
        _logHead = _logCount = 0;
        RegisterCore(0); RegisterCore(1); RegisterCore(3);
        SelectCore(0);
        LastAction = "♻ 초기화";
        Raise();
    }

    // ══════════════════════ 내부 ══════════════════════

    private void RegisterCore(int preset)
    {
        var slot = ChannelCount++;
        _registered[slot] = preset;
        LoadMetrics(preset);
        _slotOutliers[slot] = Detector.Analyze(_views, _age, _watch, _comments);
        _slotTop[slot] = Detector.TopScore;
        _slotMean[slot] = Detector.ChannelMeanViews;
        Scans++;
    }

    private void SelectCore(int slot)
    {
        SelectedSlot = slot;
        LoadMetrics(_registered[slot]);
        Detector.Analyze(_views, _age, _watch, _comments);
        _slotOutliers[slot] = Detector.OutlierCount;
        _slotTop[slot] = Detector.TopScore;
        _slotMean[slot] = Detector.ChannelMeanViews;
        Scans++;
        SelectedOutlier = -1;
        Decompiler.Reset();
        if (Detector.OutlierCount > 0) DecompileCore(0);
    }

    private void DecompileCore(int k)
    {
        SelectedOutlier = k;
        var idx = Detector.Outlier(k).Index;
        SynthesizeClip(SelectedPreset, idx, _luma, _audio);
        Decompiler.Decompile(HookOf(SelectedPreset, idx), _luma, Fps, _audio, Fs);
    }

    /// <summary>프리셋 채널 메트릭 — 평상 ±20 % 결정적 잡음 · 이상치 배율 · 업로드 2 일 간격 · 이상치는 완청/댓글 상승.</summary>
    public static void BuildMetrics(int preset, Span<float> views, Span<float> ageHours, Span<float> watch, Span<float> comments)
    {
        uint rng = 0x9E3779B9u ^ (uint)(preset * 7919 + 1);
        var spikes = ChannelSpikes[preset];
        for (var i = 0; i < VideosPerChannel; i++)
        {
            var factor = 0.8f + 0.4f * Next(ref rng);
            var hot = false;
            for (var s = 0; s + 1 < spikes.Length; s += 2) if ((int)spikes[s] == i) { factor = spikes[s + 1]; hot = true; }
            views[i] = MathF.Round(ChannelBaseViews[preset] * factor);
            ageHours[i] = 24f * (2 * i + 1);
            watch[i] = (hot ? 0.62f : 0.38f) + 0.15f * Next(ref rng);
            comments[i] = MathF.Round(views[i] * ((hot ? 0.006f : 0.002f) + 0.002f * Next(ref rng)));
        }
    }

    /// <summary>15 s 이상치 클립 — 채널 샷 길이마다 휘도 점프(컷) · 비트 간격마다 감쇠 타격음 + 저레벨 베드.</summary>
    public static void SynthesizeClip(int preset, int video, Span<float> luma, Span<float> audio)
    {
        uint rng = 0xC0FFEEu ^ (uint)(preset * 131 + video);
        var shot = Math.Max(1, (int)(ChannelShotSec[preset] * Fps));
        var level = 0.5f;
        for (var f = 0; f < luma.Length; f++)
        {
            if (f > 0 && f % shot == 0)
            {
                var l = 0.15f + 0.7f * Next(ref rng);
                level = MathF.Abs(l - level) < 0.2f ? (l + 0.35f) % 1f : l;   // 컷 경계 휘도 점프 ≥ 0.15 보장
            }
            luma[f] = level + 0.01f * (Next(ref rng) - 0.5f);
        }
        var beat = Math.Max(1, (int)(ChannelBeatSec[preset] * Fs));
        for (var i = 0; i < audio.Length; i++)
        {
            var t = i % beat;
            var hit = t < Fs / 20 ? 0.85f * (1f - t / (Fs / 20f)) : 0f;
            audio[i] = 0.12f * MathF.Sin(2f * MathF.PI * 140f * i / Fs) + hit * MathF.Sin(2f * MathF.PI * 60f * i / Fs);
        }
    }

    public static string HookOf(int preset, int video) => HookPool[(preset * 5 + video) % HookPool.Length];

    private void LoadMetrics(int preset) => BuildMetrics(preset, _views, _age, _watch, _comments);

    private CounterTarget Route()
    {
        var ovg = _sp?.GetService(typeof(OmniViralGrowthStudioRunner)) as OmniViralGrowthStudioRunner;
        if (ovg is null) return CounterTarget.Queued;
        try
        {
            return ovg.SubmitStageOneInput((uint)Guard.LastFingerprint, PersonaNames[Persona] + " · " + Keyword) ? CounterTarget.OrchestratorStage1 : CounterTarget.Queued;
        }
        catch (Exception ex)
        {
            RouteFailures++;
            PromptFlowStudio.Program.Log("CompetitorSpy.Route", ex);
            return CounterTarget.Queued;   // 외부 실패 → 로컬 대기열 폴백
        }
    }

    private static float Next(ref uint s)
    {
        s ^= s << 13; s ^= s >> 17; s ^= s << 5;
        return (s & 0xFFFFFF) / 16777216f;
    }

    private void Push(string line)
    {
        lock (_log)
        {
            _log[_logHead] = "[+" + Id((SimNow - SimEpoch) / 60) + "m] " + line;
            _logHead = (_logHead + 1) % LogSize;
            if (_logCount < LogSize) _logCount++;
        }
    }

    private static string Id(long v) => v.ToString(CultureInfo.InvariantCulture);

    public static string Pct(float ratio) => (ratio * 100f).ToString("0", CultureInfo.InvariantCulture);

    public string StatusLine() =>
        "채널 " + Id(ChannelCount) + " · 이상치 " + Id(Detector.OutlierCount) + " · 최고 지수 " + Detector.TopScore.ToString("0", CultureInfo.InvariantCulture) +
        " · 훅 " + ViralHookAudiovisualReverseEngineer.PatternNames[(int)Decompiler.Blueprint.Hook] + " · 승인 " + Id(Guard.Approved) + " · 차단 " + Id(Guard.Blocked) +
        " · 전송 " + Id(Guard.DispatchCount) + " · 스캔 " + Detector.LastAnalyzeUs.ToString("0.0", CultureInfo.InvariantCulture) + "µs";

    private void Raise()
    {
        try { OnChange?.Invoke(); }
        catch (Exception ex) { PromptFlowStudio.Program.Log("CompetitorSpy.OnChange", ex); }
    }

    public void Dispose() => OnChange = null;
}
