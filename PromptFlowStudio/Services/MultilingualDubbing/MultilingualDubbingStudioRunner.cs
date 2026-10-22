using System.Diagnostics;
using System.Globalization;
using System.Text;
using PromptFlowStudio.Services.GaussianFaceHand;
using PromptFlowStudio.Services.GlobalDubbing;
using PromptFlowStudio.Services.LiveDubbing;
using PromptFlowStudio.Services.Localization.Dubbing;

namespace PromptFlowStudio.Services.MultilingualDubbing;

/// <summary>
/// [PromptFlow Studio v17.82.0] 🗣️ AI 다국어 더빙 &amp; 립싱크 스튜디오 러너.
///   ▸ 준비(<see cref="Prepare"/>): 한국어 대본 → 원본 화자 PCM(컷 길이 기준) → 음색 추정 → v17.64 번역 음소(EN/ES/JA) → <see cref="MultilingualDubbingTimeStretchEngine"/> 합성 · 음색 이식 · WSOLA 컷 정합.
///   ▸ 재생(<see cref="Tick"/> 무할당 · 60 fps): 오디오 시계(스큐/점프 주입) → <see cref="AudioVisualPhaseDriftGuard"/> 립 시계 → 원본(Before) · 더빙(After) <see cref="NeuralVisemeLipSyncRetargeter"/> → v17.58 3DGS 얼굴 블렌드 → 비젬 스펙트로그램 링.
/// </summary>
public sealed class MultilingualDubbingStudioRunner : IDisposable
{
    public const string DemoScript = "안녕하세요 여러분. 오늘은 새로운 더빙 기능을 소개합니다. 함께 보시죠!";
    public const int TrackCount = 4, SpecCols = 48, LiveFps = 60, FaceSplats = 6_000, MaxGapMs = 400;
    public const float MinSkew = 0.97f, MaxSkew = 1.03f;
    public static readonly SpeakerTimbre OriginalSpeaker = new(150f, 1.08f);
    public static readonly DubLanguage[] TrackLangs = { DubLanguage.Ko, DubLanguage.En, DubLanguage.Es, DubLanguage.Ja };

    private readonly RealtimeMultilingualTranslationPhonemeEngine _mt = new();
    private readonly PhonemeEvent[] _ph = new PhonemeEvent[MultilingualDubbingTimeStretchEngine.MaxPhonemes];
    private readonly float[][] _pcm = new float[TrackCount][];
    private readonly DubTrackInfo[] _info = new DubTrackInfo[TrackCount];
    private readonly string[] _text = new string[TrackCount];
    private readonly byte[] _spec = new byte[SpecCols * NeuralVisemeLipSyncRetargeter.VisemeCount];
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _specHead, _selected = 1;
    private long _pendingJump;
    private float _skew = 1f;

    public MultilingualDubbingStudioRunner(IServiceProvider sp) : this() { }

    public MultilingualDubbingStudioRunner()
    {
        for (var i = 0; i < TrackCount; i++) { _pcm[i] = new float[MultilingualDubbingTimeStretchEngine.MaxSamples]; _text[i] = ""; }
    }

    public event Action? OnChange;
    public MultilingualDubbingTimeStretchEngine Dubber { get; } = new();
    public NeuralVisemeLipSyncRetargeter Before { get; } = new();
    public NeuralVisemeLipSyncRetargeter After { get; } = new();
    public AudioVisualPhaseDriftGuard Drift { get; } = new();
    public RealtimeGaussianFacialBlendshapeEngine Face { get; } = new(FaceSplats);
    public SpeakerTimbre Timbre { get; private set; }
    public bool Prepared { get; private set; }
    public bool Running => _loop is { IsCompleted: false };
    public string? LastAction { get; private set; }
    public long AudioPos { get; private set; }
    public long Ticks { get; private set; }
    public long Loops { get; private set; }
    public double PrepareMs { get; private set; }
    public int OriginalLength => _info[0].Length;
    public double ClipSec => (double)OriginalLength / MultilingualDubbingTimeStretchEngine.Fs;
    public ref readonly DubTrackInfo Info(int track) => ref _info[track];
    public string Text(int track) => _text[track];
    public ReadOnlySpan<float> Pcm(int track) => _pcm[track].AsSpan(0, _info[track].Length);
    public DubLanguage SelectedLang => TrackLangs[_selected];

    public int Selected
    {
        get => _selected;
        set { lock (_gate) { _selected = Math.Clamp(value, 0, TrackCount - 1); After.Reset(); } LastAction = "🎚️ 트랙 → " + MultilingualDubbingTimeStretchEngine.LangName(TrackLangs[_selected]); Raise(); }
    }

    public float AudioSkew
    {
        get => _skew;
        set => _skew = float.IsFinite(value) ? Math.Clamp(value, MinSkew, MaxSkew) : 1f;
    }

    /// <summary>스펙트로그램 셀(0~255) — col 0 = 가장 오래된 열.</summary>
    public byte Spec(int col, int viseme) => _spec[(_specHead + col) % SpecCols * NeuralVisemeLipSyncRetargeter.VisemeCount + viseme];

    // ══════════════════════ 준비(비핫패스) ══════════════════════
    public void EnsurePrepared() { if (!Prepared) Prepare(); }

    public void Prepare()
    {
        var sw = Stopwatch.StartNew();
        lock (_gate)
        {
            // 원본(KO) — 컷 길이 기준
            var n = MultilingualDubbingTimeStretchEngine.KoreanPhonemes(DemoScript, _ph);
            var len = Dubber.Synthesize(_ph.AsSpan(0, n), DubLanguage.Ko, _pcm[0]);
            Dubber.ApplyTimbre(_pcm[0].AsSpan(0, len), OriginalSpeaker);
            _info[0] = new DubTrackInfo { Lang = DubLanguage.Ko, Phonemes = n, RawLength = len, Length = len, RequiredRatio = 1f, Ratio = 1f };
            _text[0] = DemoScript;
            Timbre = Dubber.AnalyzeTimbre(_pcm[0].AsSpan(0, len));

            // v17.64 번역 음소
            var sink = new TextSink();
            _mt.Reset();
            _mt.ActiveMask = 0;
            for (var i = 1; i < TrackCount; i++) _mt.ActiveMask |= 1 << RealtimeMultilingualTranslationPhonemeEngine.IndexOf(TrackLangs[i]);
            _mt.Sink = sink;
            _mt.Submit(DemoScript, 0);
            for (double t = 0; t < 60_000 && !(t > 0 && _mt.Idle); t += 20) _mt.Step(t);
            _mt.Sink = null;

            for (var i = 1; i < TrackCount; i++)
            {
                var l = TrackLangs[i];
                var cnt = CollectPhonemes(l);
                Dubber.RenderTrack(_ph.AsSpan(0, cnt), l, Timbre, len, _pcm[i], out _info[i]);
                _text[i] = sink.Get(l);
            }
            AudioPos = 0; Drift.Reset(); Before.Reset(); After.Reset(); Array.Clear(_spec);
            Prepared = true;
        }
        PrepareMs = sw.Elapsed.TotalMilliseconds;
        LastAction = "🗣️ 더빙 준비 — 컷 " + ClipSec.ToString("0.00", CultureInfo.InvariantCulture) + "s · 화자 F0 " + Timbre.F0Hz.ToString("0", CultureInfo.InvariantCulture) + "Hz · " + PrepareMs.ToString("0", CultureInfo.InvariantCulture) + "ms";
        Raise();
    }

    /// <summary>v17.64 음소 링(최신 우선) → 시간순 + 청크 간 휴지(≤ 400 ms) 복원.</summary>
    private int CollectPhonemes(DubLanguage l)
    {
        var total = (int)_mt.PhonemeCount(l);
        var n = 0; double end = -1;
        for (var i = total - 1; i >= 0 && n < _ph.Length - 1; i--)
        {
            ref readonly var e = ref _mt.Phoneme(l, i);
            if (end >= 0 && e.StartMs > end + 1)
            {
                _ph[n].Ph = LipPhoneme.Rest; _ph[n].DurMs = (float)Math.Min(MaxGapMs, e.StartMs - end); _ph[n].StartMs = end; n++;
            }
            _ph[n++] = e;
            end = e.StartMs + e.DurMs;
        }
        return n;
    }

    private sealed class TextSink : ITranslationChunkSink
    {
        private readonly StringBuilder[] _sb = { new(), new(), new() };
        private static int Slot(DubLanguage l) => l switch { DubLanguage.En => 0, DubLanguage.Es => 1, DubLanguage.Ja => 2, _ => -1 };
        public void OnSourceLine(ReadOnlySpan<char> korean) { }
        public void OnChunk(in TranslationChunk chunk, ReadOnlySpan<char> text)
        {
            var s = Slot(chunk.Lang);
            if (s < 0) return;
            if (_sb[s].Length > 0) _sb[s].Append(' ');
            _sb[s].Append(text);
        }
        public string Get(DubLanguage l) { var s = Slot(l); return s < 0 ? "" : _sb[s].ToString(); }
    }

    // ══════════════════════ 재생 틱(무할당 핫패스) ══════════════════════
    /// <summary>프레임 1개 — 오디오 전진(스큐 · 점프) → 드리프트 가드 → Before/After 비젬 → 3DGS 블렌드 → 스펙트로그램.</summary>
    public void Tick(long frameDtUs)
    {
        lock (_gate)
        {
            if (!Prepared) return;
            var len = OriginalLength;
            AudioPos += (long)(frameDtUs * (MultilingualDubbingTimeStretchEngine.Fs / 1e6) * _skew) + _pendingJump;
            _pendingJump = 0;
            if (AudioPos < 0) AudioPos = 0;
            if (AudioPos >= len) { AudioPos = 0; Drift.Rewind(); Before.Reset(); After.Reset(); Loops++; }
            var lip = Drift.Tick(AudioPos, frameDtUs);
            var at = (int)(lip * MultilingualDubbingTimeStretchEngine.Fs / 1_000_000L);
            Before.Process(_pcm[0].AsSpan(0, len), at, Drift.Rate);
            After.Process(_pcm[_selected].AsSpan(0, len), at, Drift.Rate);
            After.Apply(Face);
            Face.Blend();
            var col = _specHead * NeuralVisemeLipSyncRetargeter.VisemeCount;
            var v = After.Visemes;
            for (var i = 0; i < v.Length; i++) _spec[col + i] = (byte)Math.Clamp((int)(v[i] * 255f), 0, 255);
            _specHead = (_specHead + 1) % SpecCols;
            Ticks++;
        }
    }

    /// <summary>오디오 재생 위치 점프(±ms) 주입 — 다음 틱에 반영(언더런 복구/시크 시뮬레이션).</summary>
    public void InjectAudioJump(int ms)
    {
        lock (_gate) _pendingJump += ms * (long)MultilingualDubbingTimeStretchEngine.Fs / 1000;
        LastAction = "🧪 오디오 점프 " + (ms > 0 ? "+" : "") + ms.ToString(CultureInfo.InvariantCulture) + "ms 주입";
        Raise();
    }

    public void SetGuard(bool on)
    {
        Drift.Enabled = on;
        LastAction = on ? "🛡️ 드리프트 가드 ON" : "⚠️ 드리프트 가드 OFF(보정 없음)";
        Raise();
    }

    public void Start()
    {
        if (Running) return;
        EnsurePrepared();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew(); long last = 0, lastRaise = 0;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var now = sw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
                    var dt = last == 0 ? AudioVisualPhaseDriftGuard.FrameUs : Math.Clamp(now - last, 1_000L, 100_000L);
                    last = now;
                    Tick(dt);
                    if (now - lastRaise >= 250_000) { lastRaise = now; Raise(); }
                    await Task.Delay(1000 / LiveFps, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { PromptFlowStudio.Program.Log("MultilingualDubbing.Loop", ex); }
        }, ct);
        LastAction = "▶ 립싱크 재생 — " + MultilingualDubbingTimeStretchEngine.LangName(SelectedLang);
        Raise();
    }

    public void Stop()
    {
        var cts = _cts; var loop = _loop;
        if (cts is null) return;
        cts.Cancel();
        try { loop?.Wait(1000); } catch (AggregateException) { }
        cts.Dispose(); _cts = null; _loop = null;
        LastAction = "⏸ 재생 정지";
        Raise();
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("MultilingualDubbing.OnChange", ex); }
    }

    public string Summary() =>
        !Prepared ? "더빙 미준비" :
        "컷 " + ClipSec.ToString("0.00", CultureInfo.InvariantCulture) + "s · WSOLA EN " + _info[1].Ratio.ToString("0.00", CultureInfo.InvariantCulture) + "× ES " + _info[2].Ratio.ToString("0.00", CultureInfo.InvariantCulture) +
        "× JA " + _info[3].Ratio.ToString("0.00", CultureInfo.InvariantCulture) + "× · 드리프트 " + (Drift.DriftUs / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + "ms · 보정 " +
        Drift.Corrections.ToString(CultureInfo.InvariantCulture) + " · 틱 " + Ticks.ToString(CultureInfo.InvariantCulture);

    public void Dispose()
    {
        Stop();
        OnChange = null;
    }
}
