using System.Diagnostics;
using System.Globalization;
using PromptFlowStudio.Services.SemanticChat;

namespace PromptFlowStudio.Services.ViralVoiceFx;

/// <summary>
/// [PromptFlow Studio v17.80.0] 🎙️ 바이럴 보이스 FX &amp; 감정 스튜디오 러너.
///   ▸ 체인: <see cref="RealtimeVoiceEmotionShifterEngine"/>(감정 피치/포먼트 · 50 ms 글라이드) → <see cref="ProceduralViralVoiceFxRackSynthesizer"/>(FX 4 종)
///     → <see cref="ZeroClippingFastLimiterGuard"/>(1 ms 룩어헤드 소프트 니 · 인터샘플 피크 차단) — 10 ms(480 샘플) 블록 무할당.
///   ▸ 데모: 절차적 성대 펄스 + 포먼트 공진 보컬 4 초 · v17.65 인라인 태그 대본 → 감정 큐 예약 · [excited] 진입 시 리버브 펀치 자동 트리거.
///   ▸ 라이브: 10 ms 틱 순환 처리 · v17.72 군중 감성 추종(선택) · FFT 512 스펙트로그램 링(48 열 × 24 로그 대역) · GR 이력.
/// </summary>
public sealed class ViralVoiceFxStudioRunner : IDisposable
{
    public const int Fs = 48_000, DemoSec = 4, Block = 480, SpecCols = 48, SpecBins = 24, FftN = 512, GrHist = 120, LiveSpecEvery = 4, Levels = 8;
    public const string DemoScript = "[excited] 와 이거 진짜 대박이에요 [whisper] 근데 이건 비밀인데요 [serious] 지금 바로 확인하세요 [funny] 따라 하면 인생 역전";

    private readonly SemanticChatWeatherStudioRunner? _chat;
    private readonly float[] _src = new float[DemoSec * Fs], _out = new float[DemoSec * Fs], _blk = new float[Block];
    private readonly float[] _tail = new float[FftN], _re = new float[FftN], _im = new float[FftN], _win = new float[FftN], _cos = new float[FftN / 2], _sin = new float[FftN / 2];
    private readonly int[] _rev = new int[FftN], _edge = new int[SpecBins + 1];
    private readonly float[] _spec = new float[SpecCols * SpecBins], _gr = new float[GrHist];
    private readonly object _gate = new();
    private int _tailPos, _specHead, _grHead, _livePos, _blockInCol;
    private long _lastEmotionChanges;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public ViralVoiceFxStudioRunner(IServiceProvider sp) : this(sp.GetService(typeof(SemanticChatWeatherStudioRunner)) as SemanticChatWeatherStudioRunner) { }

    public ViralVoiceFxStudioRunner(SemanticChatWeatherStudioRunner? chat = null)
    {
        _chat = chat;
        for (var i = 0; i < FftN; i++)
        {
            _win[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / FftN);
            var r = 0; for (int b = 1, v = i; b < FftN; b <<= 1, v >>= 1) r = (r << 1) | (v & 1);
            _rev[i] = r;
        }
        for (var i = 0; i < FftN / 2; i++) { _cos[i] = MathF.Cos(2f * MathF.PI * i / FftN); _sin[i] = -MathF.Sin(2f * MathF.PI * i / FftN); }
        for (var b = 0; b <= SpecBins; b++)
        {
            var f = 80.0 * Math.Pow(16000.0 / 80.0, (double)b / SpecBins);
            var bin = (int)Math.Round(f * FftN / Fs);
            _edge[b] = b == 0 ? Math.Max(1, bin) : Math.Max(_edge[b - 1] + 1, bin);
        }
        SynthesizeVoice(_src);
        Rack.Active = ViralVoiceFx.ReverbPunch;
    }

    public event Action? OnChange;
    public RealtimeVoiceEmotionShifterEngine Engine { get; } = new();
    public ProceduralViralVoiceFxRackSynthesizer Rack { get; } = new();
    public ZeroClippingFastLimiterGuard Limiter { get; } = new();
    public VoiceEmotion Preset { get; private set; }
    public bool ChatLinked => _chat is not null;
    public bool FollowChat { get; set; }
    public bool Running => _loop is { IsCompleted: false };
    public bool DemoReady { get; private set; }
    public string? LastAction { get; private set; }
    public long Blocks { get; private set; }
    public double LastBlockUs { get; private set; }
    public double MaxBlockUs { get; private set; }
    public double AvgBlockUs { get; private set; }
    public float InputGain { get; set; } = 1f;
    public ReadOnlySpan<float> Output => _out;
    public ReadOnlySpan<float> Source => _src;
    /// <summary>블록 실시간 예산(10 ms) 대비 사용률.</summary>
    public double Load => AvgBlockUs / (Block * 1e6 / Fs);

    // ══════════════════════ 프리셋 · FX ══════════════════════
    public void SetPreset(VoiceEmotion e)
    {
        lock (_gate) { Engine.ClearCues(); Engine.SetEmotion(e); Preset = e; }
        LastAction = RealtimeVoiceEmotionShifterEngine.EmotionIcon(e) + " " + RealtimeVoiceEmotionShifterEngine.EmotionName(e) + " 프리셋 → 50 ms 글라이드";
        Raise();
    }

    public void ToggleFx(ViralVoiceFx fx)
    {
        lock (_gate) Rack.Toggle(fx);
        LastAction = ProceduralViralVoiceFxRackSynthesizer.FxIcon(fx) + " " + ProceduralViralVoiceFxRackSynthesizer.FxName(fx) + ((Rack.Active & fx) != 0 ? " ON" : " OFF");
        Raise();
    }

    public void Punch()
    {
        lock (_gate) { Rack.Active |= ViralVoiceFx.ReverbPunch; Rack.Punch(); }
        LastAction = "💥 리버브 펀치 — 1.2 s 테일";
        Raise();
    }

    /// <summary>v17.72 군중 감성 추종 — 지배 감성 비율 ≥ 0.3 이면 프리셋 전이 · 반환 = 전이 여부.</summary>
    public bool SyncFromChat()
    {
        if (_chat is null) return false;
        var v = _chat.Analyzer.Vector;
        if (v.Max < 0.3f) return false;
        var e = RealtimeVoiceEmotionShifterEngine.FromCrowd(v.Dominant);
        if (e == Preset) return false;
        SetPreset(e);
        return true;
    }

    // ══════════════════════ 처리 체인 ══════════════════════
    /// <summary>10 ms 블록 1 개: 감정 변조 → FX 랙 → 리미터(무할당).</summary>
    public void ProcessBlock(ReadOnlySpan<float> src, Span<float> dst, int specEvery)
    {
        var t0 = Stopwatch.GetTimestamp();
        var blk = _blk.AsSpan(0, Math.Min(Block, Math.Min(src.Length, dst.Length)));
        Engine.Process(src.Slice(0, blk.Length), blk);
        if (InputGain != 1f) for (var i = 0; i < blk.Length; i++) blk[i] *= InputGain;
        if (Engine.EmotionChanges != _lastEmotionChanges)
        {
            _lastEmotionChanges = Engine.EmotionChanges;
            if (Engine.Emotion == VoiceEmotion.Excited && (Rack.Active & ViralVoiceFx.ReverbPunch) != 0) Rack.Punch();
        }
        Rack.Process(blk);
        Limiter.Process(blk, dst.Slice(0, blk.Length));
        for (var i = 0; i < blk.Length; i++) { _tail[_tailPos] = dst[i]; if (++_tailPos == FftN) _tailPos = 0; }
        _gr[_grHead] = Limiter.BlockGainReductionDb;
        if (++_grHead == GrHist) _grHead = 0;
        if (++_blockInCol >= specEvery) { _blockInCol = 0; PushColumn(); }
        var us = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
        LastBlockUs = us;
        if (us > MaxBlockUs) MaxBlockUs = us;
        Blocks++;
        AvgBlockUs += (us - AvgBlockUs) / Math.Min(Blocks, 200);
    }

    /// <summary>데모 대본 4 초 전체 처리(태그 큐 · 자동 펀치) → 스펙트로그램 48 열.</summary>
    public int ProcessDemo()
    {
        int cues;
        lock (_gate)
        {
            ResetChain();
            Span<VoiceEmotionCue> tmp = stackalloc VoiceEmotionCue[RealtimeVoiceEmotionShifterEngine.CueCap];
            RealtimeVoiceEmotionShifterEngine.ParseScript(DemoScript, tmp, out var plain);
            cues = Engine.ScheduleScript(DemoScript, 0, plain / (DemoSec * 0.92));
            var blocks = _src.Length / Block;
            var every = Math.Max(1, blocks / SpecCols);
            for (var b = 0; b + Block <= _src.Length; b += Block) ProcessBlock(_src.AsSpan(b, Block), _out.AsSpan(b, Block), every);
            Preset = Engine.Emotion;
            DemoReady = true;
        }
        LastAction = "🎬 데모 대본 " + cues.ToString(CultureInfo.InvariantCulture) + " 태그 처리 · 펀치 " + Rack.Punches.ToString(CultureInfo.InvariantCulture)
            + " · GR 최대 " + Limiter.MaxGainReductionDb.ToString("0.0", CultureInfo.InvariantCulture) + " dB · 블록 " + AvgBlockUs.ToString("0", CultureInfo.InvariantCulture) + " µs";
        Raise();
        return cues;
    }

    private void ResetChain()
    {
        Engine.Reset(); Rack.Reset(); Limiter.Reset();
        Array.Clear(_tail); Array.Clear(_spec); Array.Clear(_gr); Array.Clear(_out);
        _tailPos = _specHead = _grHead = _blockInCol = 0; _lastEmotionChanges = 0;
        Blocks = 0; AvgBlockUs = MaxBlockUs = LastBlockUs = 0;
    }

    /// <summary>라이브 1 틱(10 ms) — 데모 원음 순환 처리.</summary>
    public void LiveTick()
    {
        lock (_gate)
        {
            if (_livePos + Block > _src.Length) _livePos = 0;
            ProcessBlock(_src.AsSpan(_livePos, Block), _out.AsSpan(_livePos, Block), LiveSpecEvery);
            _livePos += Block;
        }
    }

    public void Start()
    {
        if (Running) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            long done = 0, lastRaise = 0;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var owed = sw.ElapsedMilliseconds / 10 - done;
                    if (owed > 5) { done += owed - 5; owed = 5; }
                    for (var k = 0; k < owed; k++) { LiveTick(); done++; }
                    if (sw.ElapsedMilliseconds - lastRaise >= 250)
                    {
                        lastRaise = sw.ElapsedMilliseconds;
                        if (FollowChat) SyncFromChat();
                        Raise();
                    }
                }
                catch (Exception ex) { LastAction = "⚠ " + ex.Message; PromptFlowStudio.Program.Log("ViralVoiceFx.Loop", ex); }
                try { await Task.Delay(10, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }, ct);
        LastAction = "▶ 실시간 보이스 FX 모니터링(10 ms 블록)";
        Raise();
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loop?.Wait(5_000); } catch (AggregateException) { }
        _cts?.Dispose(); _cts = null; _loop = null;
        Raise();
    }

    // ══════════════════════ 스펙트로그램 · GR 이력 ══════════════════════
    private void PushColumn()
    {
        for (var i = 0; i < FftN; i++) { _re[i] = _tail[(_tailPos + i) & (FftN - 1)] * _win[i]; _im[i] = 0f; }
        Fft(_re, _im);
        var col = _specHead * SpecBins;
        for (var b = 0; b < SpecBins; b++)
        {
            double p = 0; var a = _edge[b]; var e = Math.Min(FftN / 2, _edge[b + 1]);
            for (var k = a; k < e; k++) p += _re[k] * _re[k] + _im[k] * _im[k];
            p /= Math.Max(1, e - a);
            var db = 10 * Math.Log10(p + 1e-12);
            _spec[col + b] = (float)Math.Clamp((db + 50) / 90, 0, 1);
        }
        if (++_specHead == SpecCols) _specHead = 0;
    }

    private void Fft(float[] re, float[] im)
    {
        for (var i = 0; i < FftN; i++)
        {
            var j = _rev[i];
            if (j > i) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (var len = 2; len <= FftN; len <<= 1)
        {
            var half = len >> 1; var step = FftN / len;
            for (var i = 0; i < FftN; i += len)
                for (var k = 0; k < half; k++)
                {
                    var c = _cos[k * step]; var s = _sin[k * step];
                    var xr = re[i + k + half]; var xi = im[i + k + half];
                    var tr = xr * c - xi * s; var ti = xr * s + xi * c;
                    re[i + k + half] = re[i + k] - tr; im[i + k + half] = im[i + k] - ti;
                    re[i + k] += tr; im[i + k] += ti;
                }
        }
    }

    /// <summary>시간순 열 c(0 = 가장 오래됨) · 대역 b(0 = 80 Hz) 정규화 세기 0~1.</summary>
    public float Spec(int c, int b) => _spec[(_specHead + c) % SpecCols * SpecBins + b];
    public int SpecLevel(int c, int b) => Math.Min(Levels - 1, (int)(Spec(c, b) * Levels));
    public int BandHz(int b) => _edge[b] * Fs / FftN;
    /// <summary>시간순 GR 이력(0 = 가장 오래됨 · dB).</summary>
    public float GrAt(int i) => _gr[(_grHead + i) % GrHist];

    // ══════════════════════ 데모 보컬 ══════════════════════
    /// <summary>절차적 보컬(톱니 성대 펄스 F0 135~175 Hz + 700/1220/2600 Hz 포먼트 공진 + 3.5 Hz 음절 포락 · 피크 0.4).</summary>
    public static void SynthesizeVoice(Span<float> dst)
    {
        Span<float> fq = stackalloc float[] { 700f, 1220f, 2600f };
        Span<float> bw = stackalloc float[] { 90f, 110f, 160f };
        Span<float> amp = stackalloc float[] { 1f, 0.6f, 0.25f };
        Span<float> a1 = stackalloc float[3], a2 = stackalloc float[3], g = stackalloc float[3], y1 = stackalloc float[3], y2 = stackalloc float[3];
        for (var k = 0; k < 3; k++)
        {
            var r = MathF.Exp(-MathF.PI * bw[k] / Fs);
            a1[k] = 2f * r * MathF.Cos(2f * MathF.PI * fq[k] / Fs); a2[k] = -r * r; g[k] = (1f - r) * amp[k];
        }
        double ph = 0; var peak = 1e-6f;
        for (var i = 0; i < dst.Length; i++)
        {
            var t = (double)i / Fs;
            var f0 = 155 + 20 * Math.Sin(2 * Math.PI * 0.7 * t);
            ph += f0 / Fs; if (ph >= 1) ph -= 1;
            var pulse = (float)(1 - 2 * ph);
            var env = (float)Math.Pow(Math.Max(0, Math.Sin(2 * Math.PI * 3.5 * t)), 0.6);
            var s = 0f;
            for (var k = 0; k < 3; k++)
            {
                var y = g[k] * pulse + a1[k] * y1[k] + a2[k] * y2[k];
                y2[k] = y1[k]; y1[k] = y; s += y;
            }
            s *= env;
            dst[i] = s;
            if (MathF.Abs(s) > peak) peak = MathF.Abs(s);
        }
        var scale = 0.4f / peak;
        for (var i = 0; i < dst.Length; i++) dst[i] *= scale;
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("ViralVoiceFx.OnChange", ex); }
    }

    public void Dispose()
    {
        Stop();
        OnChange = null;
    }

    public string Summary() =>
        RealtimeVoiceEmotionShifterEngine.EmotionName(Engine.Emotion) + " · 피치 ×" + Engine.Pitch.ToString("0.00", CultureInfo.InvariantCulture) + " · 포먼트 ×" + Engine.Formant.ToString("0.00", CultureInfo.InvariantCulture)
        + " · FX " + Rack.ActiveCount.ToString(CultureInfo.InvariantCulture) + " · GR " + Limiter.GainReductionDb.ToString("0.0", CultureInfo.InvariantCulture) + " dB · 클램프 " + Limiter.HardClamps.ToString(CultureInfo.InvariantCulture);
}
