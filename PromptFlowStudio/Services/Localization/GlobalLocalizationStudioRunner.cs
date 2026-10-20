using System.Diagnostics;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.Localization;

/// <summary>로컬라이제이션 1회 실행 결과(UI · 트랙은 프레임 단위).</summary>
public sealed record LocalizationRun(DubLanguage Target, int Line, AdaptationResult Adaptation, LipSyncReport LipSync, VoiceProfile Actor, VoiceProfile Cloned,
    float TimbreSimilarity, float F0ErrorPct, float Rt60ErrorPct, float BrightnessErrorPct, int Samples, int SourcePhonemes, int TargetPhonemes,
    float[] SourceMouth, float[] TargetMouth, float[] FinalMouth, float[] Envelope, float[] OffsetMs, float[] Scale, int[] Chosen, double TotalMs);

/// <summary>
/// [PromptFlow Studio v16.69.0] 🌐 글로벌 로컬라이제이션 &amp; 더빙 스튜디오 러너.
/// 파이프라인: 원 배우 레퍼런스(한국어 대사 · 잔향 공간) 분석 → 음색 전송 보정 → 문화 적응 → G2V · 등시성 정합 → 보이스 클로닝 합성 →
/// 클론 재분석(F0 · 음색 · 잔향 오차) → 립싱크 매칭(DTW + SIMD ROI 워프). 미리보기: 25fps 재생 루프(<see cref="TickOnce"/> 무할당 · 프레임 그리기+워프).
/// </summary>
public sealed class GlobalLocalizationStudioRunner : IDisposable
{
    public const int MaxSamples = NeuralVoiceCloningAcousticTransfer.SampleRate * 20;
    /// <summary>데모 원 배우 실제 파라미터(레퍼런스 생성용 · 분석기는 모름).</summary>
    public static readonly VoiceProfile ActorTruth = new(118, 5, 0.85f, 0.5f, 480, 0.3f, -20);

    private readonly object _gate = new();
    private readonly RealtimeNeuralAudioDubbingEngine _dub;
    private readonly MultiLanguageCulturalNuanceAdaptor _nuance;
    private readonly NeuralVoiceCloningAcousticTransfer _voice;
    private readonly float[] _audio = new float[MaxSamples], _refAudio = new float[MaxSamples];
    private readonly DubPhoneme[] _srcPh = new DubPhoneme[1024], _dstPh = new DubPhoneme[1024];
    private readonly float[] _timbreA = new float[NeuralVoiceCloningAcousticTransfer.Bands], _timbreC = new float[NeuralVoiceCloningAcousticTransfer.Bands];
    private readonly byte[] _preview = new byte[RealtimeNeuralAudioDubbingEngine.FrameW * RealtimeNeuralAudioDubbingEngine.FrameH * 4];
    private readonly byte[] _scratch = new byte[RealtimeNeuralAudioDubbingEngine.RoiW * RealtimeNeuralAudioDubbingEngine.RoiH * 4];
    private CancellationTokenSource? _cts;
    private VoiceProfile _actor;
    private bool _actorReady;
    private int _playhead, _busy;
    private long _frameSeq;
    private float _previewOpen;
    private LocalizationRun? _last;

    public GlobalLocalizationStudioRunner(IServiceProvider sp)
        : this((RealtimeNeuralAudioDubbingEngine?)sp.GetService(typeof(RealtimeNeuralAudioDubbingEngine)) ?? new RealtimeNeuralAudioDubbingEngine(),
               (MultiLanguageCulturalNuanceAdaptor?)sp.GetService(typeof(MultiLanguageCulturalNuanceAdaptor)) ?? new MultiLanguageCulturalNuanceAdaptor(),
               (NeuralVoiceCloningAcousticTransfer?)sp.GetService(typeof(NeuralVoiceCloningAcousticTransfer)) ?? new NeuralVoiceCloningAcousticTransfer()) { }

    public GlobalLocalizationStudioRunner(RealtimeNeuralAudioDubbingEngine dub, MultiLanguageCulturalNuanceAdaptor nuance, NeuralVoiceCloningAcousticTransfer voice)
    {
        _dub = dub; _nuance = nuance; _voice = voice;
    }

    public event Action? OnChange;

    public RealtimeNeuralAudioDubbingEngine Dubbing => _dub;
    public MultiLanguageCulturalNuanceAdaptor Nuance => _nuance;
    public NeuralVoiceCloningAcousticTransfer Voice => _voice;
    public LocalizationRun? Last => Volatile.Read(ref _last);
    public bool IsPlaying => _cts is not null;
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public int Playhead { get { lock (_gate) return _playhead; } }
    public float PreviewOpenness { get { lock (_gate) return _previewOpen; } }
    public long FrameSeq => Interlocked.Read(ref _frameSeq);
    public string? LastAction { get; private set; }

    /// <summary>원 배우 레퍼런스 생성 → 분석 → 음색 전송 보정(1회 캐시).</summary>
    public VoiceProfile EnsureActor()
    {
        lock (_gate)
        {
            if (_actorReady) return _actor;
            var n = _dub.ToPhonemes(MultiLanguageCulturalNuanceAdaptor.DemoScript[0].Text + " " + MultiLanguageCulturalNuanceAdaptor.DemoScript[1].Text, DubLanguage.Ko, _srcPh);
            var tone = _voice.Tone;
            _voice.Tone = new VoiceToneSettings();
            _voice.CalibrateTimbre(ActorTruth with { Wet = 0, Rt60Ms = 0 });   // 레퍼런스 녹음 자체의 음색(배우 성대·공간)
            var len = _voice.Synthesize(_srcPh.AsSpan(0, n), ActorTruth, _refAudio);
            _actor = _voice.Analyze(_refAudio.AsSpan(0, len), _timbreA);
            _voice.CalibrateTimbre(_actor);
            _voice.Tone = tone;
            _actorReady = true;
            return _actor;
        }
    }

    /// <summary>1회 로컬라이제이션 파이프라인(동기 · 제어 평면).</summary>
    public LocalizationRun Localize(int line, DubLanguage target)
    {
        var t0 = Stopwatch.GetTimestamp();
        var actor = EnsureActor();
        LocalizationRun run;
        lock (_gate)
        {
            var (text, visual) = MultiLanguageCulturalNuanceAdaptor.DemoScript[Math.Clamp(line, 0, MultiLanguageCulturalNuanceAdaptor.DemoScript.Length - 1)];
            var ns = _dub.ToPhonemes(text, DubLanguage.Ko, _srcPh);
            var srcMs = RealtimeNeuralAudioDubbingEngine.DurationMs(_srcPh.AsSpan(0, ns));
            var srcMouth = new float[RealtimeNeuralAudioDubbingEngine.MaxFrames];
            var nsf = RealtimeNeuralAudioDubbingEngine.MouthTrack(_srcPh.AsSpan(0, ns), srcMouth);
            var adaptation = _nuance.Adapt(text, target, visual, srcMs);
            var dubText = adaptation.Adapted.StartsWith("[MT", StringComparison.Ordinal) ? text : adaptation.Adapted;
            var nt = _dub.ToPhonemes(dubText, target, _dstPh);
            var iso = RealtimeNeuralAudioDubbingEngine.FitIsochrony(_dstPh.AsSpan(0, nt), srcMs);
            var dstMouth = new float[RealtimeNeuralAudioDubbingEngine.MaxFrames];
            var ntf = RealtimeNeuralAudioDubbingEngine.MouthTrack(_dstPh.AsSpan(0, nt), dstMouth);
            var samples = _voice.Synthesize(_dstPh.AsSpan(0, nt), actor, _audio);
            var env = new float[ntf];
            var speech = Math.Min(samples, ntf * NeuralVoiceCloningAcousticTransfer.SampleRate / RealtimeNeuralAudioDubbingEngine.Fps);
            RealtimeNeuralAudioDubbingEngine.AudioEnvelope(_audio.AsSpan(0, speech), NeuralVoiceCloningAcousticTransfer.SampleRate, env);
            var clone = _voice.Analyze(_audio.AsSpan(0, samples), _timbreC);
            var chosen = new int[ntf]; var scale = new float[ntf]; var off = new float[ntf]; var fin = new float[ntf];
            var rep = _dub.Match(srcMouth.AsSpan(0, nsf), dstMouth.AsSpan(0, ntf), env, chosen, scale, off, fin, iso);
            var tone = _voice.Tone;
            var expectF0 = actor.F0Hz * MathF.Pow(2, tone.PitchShiftSemis / 12f);
            run = new LocalizationRun(target, line, adaptation, rep, actor, clone,
                NeuralVoiceCloningAcousticTransfer.TimbreSimilarity(_timbreA, _timbreC),
                MathF.Abs(clone.F0Hz - expectF0) / expectF0 * 100,
                actor.Rt60Ms > 0 ? MathF.Abs(clone.Rt60Ms - actor.Rt60Ms) / actor.Rt60Ms * 100 : 0,
                MathF.Abs(clone.Brightness - actor.Brightness) / actor.Brightness * 100,
                samples, ns, nt, srcMouth[..nsf], dstMouth[..ntf], fin, env, off, scale, chosen,
                (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
            _playhead = 0;
        }
        Volatile.Write(ref _last, run);
        _dub.Announce("🎬 " + RealtimeNeuralAudioDubbingEngine.LanguageLabel(target) + " 더빙 · 립싱크 상관 " + run.LipSync.BeforeCorrelation.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
            + " → " + run.LipSync.AfterCorrelation.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
        LastAction = _dub.LastAction;
        Raise();
        return run;
    }

    public async Task<LocalizationRun?> LocalizeAsync(int line, DubLanguage target)
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0) return null;
        try { return await Task.Run(() => Localize(line, target)).ConfigureAwait(false); }
        finally { Volatile.Write(ref _busy, 0); Raise(); }
    }

    /// <summary>재생 1프레임(무할당) — 선택 원 프레임 그리기 → ROI 워프 → 측정.</summary>
    public float TickOnce()
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Localization.Runner.Tick");
            var r = _last;
            if (r is null || r.Chosen.Length == 0) return 0;
            var f = _playhead % r.Chosen.Length;
            RealtimeNeuralAudioDubbingEngine.DrawFace(_preview, r.SourceMouth[r.Chosen[f]]);
            RealtimeNeuralAudioDubbingEngine.WarpMouth(_preview, r.Scale[f], _scratch);
            _previewOpen = RealtimeNeuralAudioDubbingEngine.MeasureOpenness(_preview);
            _playhead = f + 1;
            Interlocked.Increment(ref _frameSeq);
            return _previewOpen;
        }
    }

    public void Play()
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(() => LoopAsync(cts.Token));
        LastAction = "▶ 립싱크 미리보기 재생";
        Raise();
    }

    public void Stop()
    {
        var c = Interlocked.Exchange(ref _cts, null);
        if (c is null) return;
        c.Cancel(); c.Dispose();
        LastAction = "⏸ 정지";
        Raise();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / RealtimeNeuralAudioDubbingEngine.Fps));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { TickOnce(); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("Localization.Tick", ex); }
                if ((FrameSeq & 1) == 0) Raise();
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
