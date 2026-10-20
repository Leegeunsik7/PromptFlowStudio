using System.Numerics;
using PromptFlowStudio.Services.Cinematography;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.DigitalHuman;

namespace PromptFlowStudio.Services.Localization.Dubbing;

/// <summary>대사별 더빙 지표(값 형식).</summary>
public readonly record struct DubLineMetrics(float Similarity, float ProsodyCorr, float Isochrony, float Extraction, float SyncMs, float Cps, SubtitleIssue Issues, SubtitlePlacement Placement);

/// <summary>
/// [PromptFlow Studio v16.89.0] 🌐 AI 글로벌 로컬라이제이션 &amp; 립싱크 스튜디오 러너(60 Hz = 오디오 블록 400 @ 24 kHz · <see cref="TickOnce"/> 재생 구간 무할당).
/// 대사 6줄 순환: 문화 적응(자막/발음) → 원본 합성/분석(음색 복제 · 운율) → 대상 언어 더빙 → 오디오 역추출 →
/// 재생 시계 동기 립싱크(v16.82 근육) → v16.85 구도 평가 → 자막 배치 가드. 줄 전환 시 준비(≈ 10 ms · 제어 평면).
/// </summary>
public sealed class NeuralLipSyncDubbingStudioRunner : IDisposable
{
    public const int Hz = 60, Block = NeuralVoiceCloningDubbingEngine.Block, WaveBins = 300;
    public const float Gap = 0.5f, Aspect = 16f / 9f;

    private readonly object _gate = new();
    private readonly NeuralVoiceCloningDubbingEngine _engine;
    private readonly GenerativeNeuralLipSyncSynthesizer _lip;
    private readonly CulturalLocalizationSubtitleGuard _guard;
    private readonly GenerativeShotFramingEvaluator _framing = new();
    private readonly LocalizedLine[] _lines = new LocalizedLine[GlobalLanguageCatalog.Lines];
    private readonly DubLineMetrics[] _metrics = new DubLineMetrics[GlobalLanguageCatalog.Lines];
    private readonly float[] _audio = new float[Block];
    private readonly float[] _waveMin = new float[WaveBins], _waveMax = new float[WaveBins];
    private int _waveHead;
    private CancellationTokenSource? _cts;
    private long _frame;
    private int _line = -1;
    private float _gapLeft;
    private FramingResult _fr;

    public NeuralLipSyncDubbingStudioRunner(IServiceProvider sp)
        : this((NeuralVoiceCloningDubbingEngine?)sp.GetService(typeof(NeuralVoiceCloningDubbingEngine)) ?? new NeuralVoiceCloningDubbingEngine(),
               (GenerativeNeuralLipSyncSynthesizer?)sp.GetService(typeof(GenerativeNeuralLipSyncSynthesizer)) ?? new GenerativeNeuralLipSyncSynthesizer(),
               (CulturalLocalizationSubtitleGuard?)sp.GetService(typeof(CulturalLocalizationSubtitleGuard)) ?? new CulturalLocalizationSubtitleGuard()) { }

    public NeuralLipSyncDubbingStudioRunner(NeuralVoiceCloningDubbingEngine engine, GenerativeNeuralLipSyncSynthesizer lip, CulturalLocalizationSubtitleGuard guard)
    {
        _engine = engine; _lip = lip; _guard = guard;
        for (var i = 0; i < _lines.Length; i++) _lines[i] = new LocalizedLine();
        SetLanguage(DubLanguage.En);
    }

    public event Action? OnChange;

    public NeuralVoiceCloningDubbingEngine Engine => _engine;
    public GenerativeNeuralLipSyncSynthesizer LipSync => _lip;
    public CulturalLocalizationSubtitleGuard Subtitles => _guard;
    public NeuralFacialMuscleSimulator Face => _lip.Face;
    public DubLanguage Language { get; private set; }
    public bool IsRunning => _cts is not null;
    public long Frame { get { lock (_gate) return _frame; } }
    public int Line => Math.Max(_line, 0);
    public bool Speaking => _gapLeft <= 0f && !_engine.Finished;
    public LocalizedLine Localized(int i) => _lines[i];
    public LocalizedLine Current => _lines[Line];
    public DubLineMetrics Metrics(int i) => _metrics[i];
    public FramingResult Framing => _fr;
    public float WaveMin(int back) => _waveMin[(_waveHead - 1 - back + 2 * WaveBins) % WaveBins];
    public float WaveMax(int back) => _waveMax[(_waveHead - 1 - back + 2 * WaveBins) % WaveBins];
    public double LastTickMs { get; private set; }
    public double LastPrepareMs { get; private set; }
    public string? LastAction { get; private set; }

    public static ShotScale ShotFor(int line) => line switch { 1 => ShotScale.CloseUp, 2 => ShotScale.CloseUp, 3 => ShotScale.MediumCloseUp, 4 => ShotScale.Wide, _ => ShotScale.Medium };

    /// <summary>대상 언어 전환(제어 평면): 6줄 문화 적응 · 첫 줄 준비.</summary>
    public void SetLanguage(DubLanguage lang)
    {
        lock (_gate)
        {
            Language = lang;
            for (var i = 0; i < _lines.Length; i++)
                _guard.Adapt(lang, i, 1.6f, _lines[i]);
            _line = -1; _gapLeft = 0f;
            NextLine();
        }
        var p = GlobalLanguageCatalog.Profile(lang);
        LastAction = "🌐 " + p.Name + " (" + p.Family + " · " + p.Branch + ")";
        Raise();
    }

    public void SetFormal(bool formal) { _guard.FormalRegister = formal; SetLanguage(Language); }
    public void SetIdiomAdaptation(bool on) { _guard.AdaptIdioms = on; SetLanguage(Language); }
    public void SetSourceSpeaker(VoiceTimbre v) { _engine.SourceSpeaker = v; SetLanguage(Language); }

    /// <summary>카메라: 줄별 샷(원점 인물 · 신장 1.75 m) — 3번 줄 = 얼굴이 화면 하단(자막 상단 이동 시험).</summary>
    private CameraPose CameraFor(int line)
    {
        var eye = new Vector3(0f, 1.61f, 0f);
        float dist = line switch { 1 => 1.0f, 2 => 0.55f, 3 => 1.4f, 4 => 6f, _ => 2.2f };
        var pos = new Vector3(0.15f, 1.6f, -dist);
        var aim = eye;
        if (line == 3) aim = eye + new Vector3(0f, 0.32f, 0f);
        var f = Vector3.Normalize(aim - pos);
        return new CameraPose { Position = pos, Orientation = NeuralCameraTrajectoryEngine.LookRotation(f, 0f), FocalMm = 32f, FocusDistance = dist, FStop = 2.8f };
    }

    private void NextLine()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _line = (_line + 1) % GlobalLanguageCatalog.Lines;
        var ko = _lines[_line];
        // 원본 로마자(토큰 = 한국어 적응표)
        var srcRoman = KoRoman(_line);
        _engine.PrepareSource(srcRoman, GlobalLanguageCatalog.Emotions[_line], GlobalLanguageCatalog.IsQuestion[_line]);
        _engine.PrepareDub(ko.Roman, Language, GlobalLanguageCatalog.IsQuestion[_line]);
        CulturalLocalizationSubtitleGuard.UpdateTiming(ko, Language, _engine.Plan.Duration);
        var ext = _lip.ExtractFromAudio(_engine.DubAudio, _engine.Plan, _engine.Clone.Vtf);
        _fr = _framing.Evaluate(CameraFor(_line), Vector3.Zero, 1.75f, -Vector3.UnitZ, default, false);
        var prof = GlobalLanguageCatalog.Profile(Language);
        var lines = ko.Line2.Length > 0 ? 2 : 1;
        var issues = ko.Issues & ~(SubtitleIssue.FaceOverlap | SubtitleIssue.Relocated);
        issues |= _guard.Place(_fr, lines, Math.Max(ko.Line1.Length, ko.Line2.Length), CulturalLocalizationSubtitleGuard.IsCjk(prof.Script), Aspect);
        ko.Issues = issues;
        _metrics[_line] = new DubLineMetrics(_engine.TimbreSimilarity, _engine.ProsodyCorrelation, _engine.IsochronyError, ext, _lip.SyncErrorMs, ko.Cps, issues, _guard.Placement);
        _engine.Rewind();
        _gapLeft = Gap;
        LastPrepareMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    private string KoRoman(int line)
    {
        var tpl = GlobalLanguageCatalog.Text[0][line].Roman;
        if (_koRoman[line] is { } cached) return cached;
        var r = tpl.Replace("{easy}", GlobalLanguageCatalog.Idioms[0][0].AdaptedRoman, StringComparison.Ordinal)
                   .Replace("{funny}", GlobalLanguageCatalog.Idioms[1][0].AdaptedRoman, StringComparison.Ordinal)
                   .Replace("{thanks}", GlobalLanguageCatalog.Thanks[0].CasualRoman, StringComparison.Ordinal);
        _koRoman[line] = r;
        return r;
    }

    private readonly string?[] _koRoman = new string?[GlobalLanguageCatalog.Lines];

    /// <summary>1틱(재생 구간 무할당): 립싱크(블록 시작 시각) → 오디오 블록 → 파형 · 줄 종료 → 다음 줄 준비.</summary>
    public void TickOnce()
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Localization.Tick");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            const float dt = 1f / Hz;
            if (_gapLeft > 0f)
            {
                _gapLeft -= dt;
                if (_gapLeft <= 0f) _lip.BeginLine();
                _lip.Step(_engine.Plan, -1f, dt);
                Wave(0f, 0f);
            }
            else if (!_engine.Finished)
            {
                _lip.Step(_engine.Plan, _engine.PlaybackTime, dt);
                _engine.RenderBlock(_audio);
                float mn = 0f, mx = 0f;
                for (var i = 0; i < Block; i++) { mn = MathF.Min(mn, _audio[i]); mx = MathF.Max(mx, _audio[i]); }
                Wave(mn, mx);
            }
            else
            {
                var lag = _lip.EndLine();
                _metrics[_line] = _metrics[_line] with { SyncMs = float.IsNaN(lag) ? 0f : lag };
                NextLine();
            }
            _frame++;
            LastTickMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
    }

    private void Wave(float mn, float mx)
    {
        _waveMin[_waveHead] = mn; _waveMax[_waveHead] = mx;
        _waveHead = (_waveHead + 1) % WaveBins;
    }

    public void Start()
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(() => LoopAsync(cts.Token));
        LastAction = "▶ 더빙 재생";
        Raise();
    }

    public void Stop()
    {
        var c = Interlocked.Exchange(ref _cts, null);
        if (c is null) return;
        c.Cancel(); c.Dispose();
        Raise();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / Hz));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { TickOnce(); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("Localization.Tick", ex); }
                if ((Frame & 7) == 0) Raise();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Localization.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
