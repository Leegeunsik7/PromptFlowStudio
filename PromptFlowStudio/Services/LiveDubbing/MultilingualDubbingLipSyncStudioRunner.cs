using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Localization.Dubbing;
using PromptFlowStudio.Services.NeuralVoice;

namespace PromptFlowStudio.Services.LiveDubbing;

/// <summary>레인 지표 스냅숏(UI · HUD).</summary>
public readonly record struct DubLaneMetrics(
    DubLanguage Language, float Ratio, float DriftMs, float ProjectedDriftMs, float MaxDriftMs, float LatencyMs, float MaxLatencyMs,
    float Similarity, float Flicker, float CloneF0, float FormantScale, LipPhoneme Dominant, int Utterances, float PipelineMs);

/// <summary>
/// [PromptFlow Studio v17.32.0] 🗣️ AI 다국어 더빙 &amp; 립싱크 스튜디오 러너(60 Hz 전용 스레드 · 출력 시계 800 샘플/프레임).
/// 매 프레임 v17.18 액터 음성(한국어 원본) → 화자 임베딩 → 슬롯 시작 시 레인별 번역 지연 후 토큰 적재 → 가드 WSOLA 홉 당김(엔진 렌더 · 음색 적응)
/// → 립싱크 10 ms 키프레임 → 60 fps Hermite 평가(52 블렌드셰이프 입 영역 오버라이드) 순으로 처리한다.
/// </summary>
public sealed class MultilingualDubbingLipSyncStudioRunner : IDisposable
{
    public const int Hz = 60, Fs = RealtimeNeuralVoiceCloningDubbingEngine.Fs, TickSamples = Fs / Hz, MaxLanes = RealtimeNeuralVoiceCloningDubbingEngine.MaxLanes;
    public const int SeriesLen = 240, WaveBins = 240, Shapes = PhonemeStreamProceduralLipSyncSynthesizer.Shapes;
    public const float GapMs = 700f;

    public static readonly DubLanguage[] Choices = { DubLanguage.En, DubLanguage.Ja, DubLanguage.Zh, DubLanguage.Es, DubLanguage.Fr, DubLanguage.De };

    public static readonly string[] KoLines =
    {
        "안녕하세요 여러분, 오늘도 와 주셔서 감사합니다.",
        "이번 신곡은 여러분을 위해 준비했어요.",
        "다음 주에 다시 만나요.",
        "항상 응원해 주셔서 고마워요.",
    };

    // Choices 순서 × KoLines 순서(로마자 번역 토큰)
    private static readonly string[][] Lines =
    {
        new[] { "hello everyone, thank you for coming again today.", "i prepared this new song just for all of you.", "see you again next week.", "thank you so much for always supporting me." },
        new[] { "minasan konnichiwa, kyou mo kite kurete arigatou gozaimasu.", "kono shinkyoku wa minasan no tame ni junbi shimashita.", "mata raishuu aimashou.", "itsumo ouen shite kurete arigatou." },
        new[] { "da jia hao, xie xie ni men jin tian you lai le.", "zhe shou xin ge shi wei da jia zhun bei de.", "xia zhou zai jian.", "xie xie ni men yi zhi zhi chi wo." },
        new[] { "hola a todos, muchas gracias por venir otra vez hoy.", "prepare esta nueva cancion especialmente para todos ustedes.", "nos vemos otra vez la proxima semana.", "muchisimas gracias por apoyarme siempre." },
        new[] { "bonjour a tous, merci d'etre encore venus aujourd'hui.", "j'ai prepare cette nouvelle chanson pour vous tous.", "a la semaine prochaine.", "merci beaucoup de toujours me soutenir." },
        new[] { "hallo zusammen, danke dass ihr heute wieder da seid.", "diesen neuen song habe ich fuer euch alle vorbereitet.", "bis naechste woche.", "vielen dank, dass ihr mich immer unterstuetzt." },
    };

    private static readonly long[] SlotLen = BuildSlots();

    private readonly object _gate = new();
    private readonly AudioVisualSyncJitterCompensationGuard[] _guards = new AudioVisualSyncJitterCompensationGuard[MaxLanes];
    private readonly PhonemeStreamProceduralLipSyncSynthesizer[] _lips = new PhonemeStreamProceduralLipSyncSynthesizer[MaxLanes];
    private readonly int[] _laneChoice = new int[MaxLanes];
    private readonly bool[] _selected = new bool[6];
    private readonly float[] _w = new float[MaxLanes * Shapes];
    private readonly float[] _src = new float[TickSamples], _hop = new float[RealtimeNeuralVoiceCloningDubbingEngine.Hop];
    private readonly long[] _relAt = new long[MaxLanes];
    private readonly bool[] _relPending = new bool[MaxLanes];
    private readonly float[] _wMin = new float[WaveBins], _wMax = new float[WaveBins];
    private readonly byte[] _wPh = new byte[WaveBins];
    private readonly float[] _simS = new float[SeriesLen], _driftS = new float[SeriesLen], _flickS = new float[SeriesLen], _msS = new float[SeriesLen];
    private int _waveHead, _waveCount, _seriesHead, _seriesCount, _line = -1;
    private long _slotStart, _nextSlot;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;

    public MultilingualDubbingLipSyncStudioRunner(IServiceProvider sp) : this() { }

    public MultilingualDubbingLipSyncStudioRunner()
    {
        Voice = new RealtimeNeuralVoiceSynthesisEngine(new BiomechanicalVocalTractResonanceSynthesizer()) { Loop = false, Primary = SpeakerPreset.Male, Morph = 0f };
        Engine = new RealtimeNeuralVoiceCloningDubbingEngine();
        for (var i = 0; i < MaxLanes; i++) { _guards[i] = new AudioVisualSyncJitterCompensationGuard(); _lips[i] = new PhonemeStreamProceduralLipSyncSynthesizer(); }
        _selected[0] = _selected[1] = _selected[2] = _selected[3] = true;
        ApplyLanes();
        LastAction = "📋 대기 · 레인 " + LaneCount + " · 48 kHz · 10 ms 음소 홉 · 60 fps 립싱크 · 지연 예산 " + RealtimeNeuralVoiceCloningDubbingEngine.BudgetMs.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " ms";
    }

    public event Action? OnChange;

    public RealtimeNeuralVoiceSynthesisEngine Voice { get; }
    public RealtimeNeuralVoiceCloningDubbingEngine Engine { get; }
    public int LaneCount { get; private set; }
    public int Focus { get; private set; }
    public bool GuardEnabled { get; private set; } = true;
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double NowMs => Tick * 1000.0 / Hz;
    public int Line => Math.Max(_line, 0);
    public double LastTickMs { get; private set; }
    public double MaxTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public int SeriesCount => _seriesCount;
    public int WaveCount => _waveCount;
    private int Idx(int i) => (_seriesHead - 1 - i + SeriesLen * 2) % SeriesLen;
    public float SimilaritySeries(int i) => _simS[Idx(i)];
    public float DriftSeries(int i) => _driftS[Idx(i)];
    public float FlickerSeries(int i) => _flickS[Idx(i)];
    public float TickMsSeries(int i) => _msS[Idx(i)];
    private int W(int i) => (_waveHead - 1 - i + WaveBins * 2) % WaveBins;
    public float WaveMin(int back) => _wMin[W(back)];
    public float WaveMax(int back) => _wMax[W(back)];
    public LipPhoneme WavePhoneme(int back) => (LipPhoneme)_wPh[W(back)];

    public bool IsSelected(int choice) => _selected[choice];
    public DubLanguage LaneLanguage(int lane) => Choices[_laneChoice[lane]];
    public string LaneText(int lane) => Lines[_laneChoice[lane]][Line];
    public AudioVisualSyncJitterCompensationGuard Guard(int lane) => _guards[lane];
    public PhonemeStreamProceduralLipSyncSynthesizer Lip(int lane) => _lips[lane];
    public float Blendshape(int lane, int k) => _w[lane * Shapes + k];

    private static long[] BuildSlots()
    {
        var s = new long[KoLines.Length];
        for (var i = 0; i < s.Length; i++) s[i] = RealtimeNeuralVoiceCloningDubbingEngine.Plan(DubLanguage.Ko, KoLines[i]);
        return s;
    }

    public static long SlotSamples(int line) => SlotLen[line];
    public static long PlannedSamples(int choice, int line) => RealtimeNeuralVoiceCloningDubbingEngine.Plan(Choices[choice], Lines[choice][line]);

    public static string LangName(DubLanguage l) => l switch
    {
        DubLanguage.En => "🇺🇸 영어", DubLanguage.Ja => "🇯🇵 일본어", DubLanguage.Zh => "🇨🇳 중국어", DubLanguage.Es => "🇪🇸 스페인어",
        DubLanguage.Fr => "🇫🇷 프랑스어", DubLanguage.De => "🇩🇪 독일어", DubLanguage.Ko => "🇰🇷 한국어", _ => l.ToString(),
    };

    public int CopyMetrics(Span<DubLaneMetrics> dst)
    {
        lock (_gate)
        {
            var n = Math.Min(dst.Length, LaneCount);
            for (var l = 0; l < n; l++)
            {
                var g = _guards[l];
                var lang = LaneLanguage(l);
                dst[l] = new DubLaneMetrics(lang, g.Ratio, g.LastDriftMs, g.ProjectedDriftMs, g.MaxAbsDriftMs, g.LastLatencyMs, g.MaxLatencyMs,
                    Engine.Similarity(l), g.FlickerIndex, Engine.CloneF0(l), Engine.FormantScale(l), _lips[l].Dominant, g.UtterancesMeasured,
                    RealtimeNeuralVoiceCloningDubbingEngine.PipelineLatencyMs(lang, g.Enabled));
            }
            return n;
        }
    }

    public float AverageSimilarity()
    {
        lock (_gate)
        {
            var s = 0f;
            for (var l = 0; l < LaneCount; l++) s += Engine.Similarity(l);
            return LaneCount > 0 ? s / LaneCount : 0f;
        }
    }

    public float MaxDriftMs()
    {
        lock (_gate)
        {
            var m = 0f;
            for (var l = 0; l < LaneCount; l++) m = MathF.Max(m, _guards[l].MaxAbsDriftMs);
            return m;
        }
    }

    public float MaxFlicker()
    {
        lock (_gate)
        {
            var m = 0f;
            for (var l = 0; l < LaneCount; l++) m = MathF.Max(m, _guards[l].FlickerIndex);
            return m;
        }
    }

    // ══════════════════════ 제어 ══════════════════════

    public bool ToggleLanguage(int choice)
    {
        lock (_gate)
        {
            if (_selected[choice]) { if (LaneCount <= 1) return false; _selected[choice] = false; }
            else { if (LaneCount >= MaxLanes) return false; _selected[choice] = true; }
            ApplyLanes();
        }
        LastAction = "🌐 타깃 언어 " + LaneCount + "개 · 레인 재구성(파이프라인 재시작)";
        Raise();
        return true;
    }

    private void ApplyLanes()
    {
        var n = 0;
        for (var c = 0; c < Choices.Length && n < MaxLanes; c++)
            if (_selected[c]) _laneChoice[n++] = c;
        LaneCount = n;
        for (var l = 0; l < MaxLanes; l++)
        {
            Engine.ResetLane(l); _guards[l].Reset(); _lips[l].Reset(); _relPending[l] = false;
            _guards[l].Enabled = GuardEnabled;
        }
        var now = Tick * TickSamples;
        for (var l = 0; l < MaxLanes; l++)
        {
            // 가드 출력 시계를 러너 시계에 정렬(무음 선행)
            while (_guards[l].OutputClock < now) _guards[l].NextHop(Engine, l, _hop);
        }
        _nextSlot = now;
        _line = -1;
        Array.Clear(_w);
        if (Focus >= LaneCount) Focus = 0;
    }

    public void SetFocus(int lane) { lock (_gate) Focus = Math.Clamp(lane, 0, Math.Max(0, LaneCount - 1)); LastAction = "🎯 모니터 레인 → " + LangName(LaneLanguage(Focus)); Raise(); }

    public void SetGuard(bool on)
    {
        lock (_gate) { GuardEnabled = on; for (var l = 0; l < MaxLanes; l++) _guards[l].Enabled = on; }
        LastAction = on ? "🛡 가드 ON(WSOLA 피치 보존 신축 · 3차 Hermite 스무딩 · A/V 지연 보상 30 ms)" : "⛔ 가드 OFF(α 1 고정 · 최근접 홀드 → 드리프트 누적 · 입 떨림 비교)";
        Raise();
    }

    public void SetCloning(bool on)
    {
        lock (_gate) Engine.CloneEnabled = on;
        LastAction = on ? "🧬 음색 클로닝 ON(d-vector 폐루프 · F0/포먼트/기식 적응)" : "🧬 클로닝 OFF(기본 성인 남성 톤 120 Hz 고정)";
        Raise();
    }

    public void SetSourceSpeaker(SpeakerPreset p)
    {
        lock (_gate) { Voice.Primary = p; Voice.Secondary = p; Engine.ResetCloning(); }
        LastAction = "🎙 원본 화자 → " + SpeakerName(p) + " · 임베딩 재추출";
        Raise();
    }

    public static string SpeakerName(SpeakerPreset p) => p switch { SpeakerPreset.Female => "여성", SpeakerPreset.Child => "아동", _ => "남성" };

    public void ResetMetrics()
    {
        lock (_gate) { for (var l = 0; l < MaxLanes; l++) _guards[l].ResetMetrics(); Engine.ResetTiming(); MaxTickMs = 0; }
        LastAction = "↺ 지표 초기화";
        Raise();
    }

    // ══════════════════════ 프레임(무할당) ══════════════════════

    private void BeginLine(long now)
    {
        _line = (_line + 1) % KoLines.Length;
        _slotStart = now;
        Voice.SetText(KoLines[_line]);
        for (var l = 0; l < LaneCount; l++)
        {
            _relAt[l] = now + (long)(RealtimeNeuralVoiceCloningDubbingEngine.TranslationLatencyMs(LaneLanguage(l)) * Fs / 1000f);
            _relPending[l] = true;
        }
        _nextSlot = now + SlotLen[_line] + (long)(GapMs * Fs / 1000f);
    }

    /// <summary>1 프레임: 원본 음성 → 임베딩 → 번역 토큰 적재 → WSOLA 홉 → 립 키프레임 → 60 fps 블렌드셰이프.</summary>
    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("LiveDubbing.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var tick = Interlocked.Increment(ref _tick);
            var outEnd = tick * TickSamples;
            var outStart = outEnd - TickSamples;
            if (outStart >= _nextSlot) BeginLine(outStart);
            for (var l = 0; l < LaneCount; l++)
            {
                if (!_relPending[l] || outStart < _relAt[l]) continue;
                _relPending[l] = false;
                var mt = _relAt[l] - _slotStart;
                Engine.Enqueue(l, LaneLanguage(l), Lines[_laneChoice[l]][_line]);
                _guards[l].ExpectUtterance(_slotStart, _slotStart + mt + SlotLen[_line]);
            }

            const int chunk = RealtimeNeuralVoiceSynthesisEngine.Chunk;
            for (var c = 0; c + chunk <= TickSamples; c += chunk) Voice.RenderChunk(_src.AsSpan(c, chunk));
            Engine.PushSource(_src);

            var tMs = outEnd * 1000.0 / Fs;
            for (var l = 0; l < LaneCount; l++)
            {
                var g = _guards[l];
                var lip = _lips[l];
                while (g.OutputClock < outEnd)
                {
                    g.NextHop(Engine, l, _hop);
                    lip.PushHop(_hop, Engine, l, g.LastInputPos);
                    if (l == Focus)
                    {
                        float mn = 0f, mx = 0f;
                        for (var i = 0; i < _hop.Length; i++) { var v = _hop[i]; if (v < mn) mn = v; if (v > mx) mx = v; }
                        _wMin[_waveHead] = mn; _wMax[_waveHead] = mx; _wPh[_waveHead] = (byte)lip.Dominant;
                        _waveHead = (_waveHead + 1) % WaveBins;
                        if (_waveCount < WaveBins) _waveCount++;
                    }
                }
                lip.Evaluate(tMs, _w.AsSpan(l * Shapes, Shapes), g);
            }

            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            var h = _seriesHead;
            var fg = _guards[Focus];
            _simS[h] = Engine.Similarity(Focus); _driftS[h] = MathF.Abs(fg.ProjectedDriftMs); _flickS[h] = fg.FlickerIndex; _msS[h] = ms;
            _seriesHead = (h + 1) % SeriesLen;
            if (_seriesCount < SeriesLen) _seriesCount++;
            LastTickMs = ms;
            if (ms > MaxTickMs && tick > 10) MaxTickMs = ms;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.LiveDubbing" };
            _loop.Start();
            LastAction = "▶ 동시 더빙 · 레인 " + LaneCount + " · 60 Hz · WSOLA · Hermite 립싱크";
        }
        Raise();
    }

    public void Stop()
    {
        Thread? t;
        lock (_gate) { if (!_running) return; _running = false; t = _loop; _loop = null; }
        t?.Join(3000);
        LastAction = "⏸ 정지";
        Raise();
    }

    private void Loop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long n = 0;
        while (_running)
        {
            try { Step(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("LiveDubbing.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("LiveDubbing.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
