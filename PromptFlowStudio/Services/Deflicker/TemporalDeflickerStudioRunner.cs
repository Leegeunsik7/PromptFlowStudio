namespace PromptFlowStudio.Services.Deflicker;

/// <summary>
/// [PromptFlow Studio v17.07.0] ✨ 시공간 디플리커 &amp; 텍스처 피닝 스튜디오 러너(30 Hz 전용 스레드 · 시뮬레이션 결정적).
/// 생성형 클립 시뮬레이터(레이스 모자 · 재생성 떨림 · 모핑) → 디플리커 합성기(흐름 엔진 + 모핑 가드) → 원본/보정 지표 · 정답(무아티팩트) 대비 오차.
/// </summary>
public sealed class TemporalDeflickerStudioRunner : IDisposable
{
    public const int Hz = 30, SeriesLen = 240;

    private readonly object _gate = new();
    private readonly float[] _fin = new float[SeriesLen], _fout = new float[SeriesLen], _cons = new float[SeriesLen], _morph = new float[SeriesLen], _ms = new float[SeriesLen];
    private readonly float[] _cr, _cg, _cb;
    private readonly byte[] _bmp;
    private int _seriesHead, _seriesCount;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick, _t;

    public TemporalDeflickerStudioRunner(IServiceProvider sp)
        : this((SubPixelOpticalFlowTexturePinningEngine?)sp.GetService(typeof(SubPixelOpticalFlowTexturePinningEngine)) ?? new SubPixelOpticalFlowTexturePinningEngine(),
               (HighFrequencyDetailBlindDeflickerSynthesizer?)sp.GetService(typeof(HighFrequencyDetailBlindDeflickerSynthesizer)) ?? new HighFrequencyDetailBlindDeflickerSynthesizer(),
               (GenerativeMorphingSuppressionGuard?)sp.GetService(typeof(GenerativeMorphingSuppressionGuard)) ?? new GenerativeMorphingSuppressionGuard())
    {
    }

    public TemporalDeflickerStudioRunner(SubPixelOpticalFlowTexturePinningEngine flow, HighFrequencyDetailBlindDeflickerSynthesizer synth, GenerativeMorphingSuppressionGuard guard)
    {
        Flow = flow; Synth = synth; Guard = guard;
        Clip = new GenerativeClipSimulator(synth.Width, synth.Height);
        var n = synth.Width * synth.Height;
        _cr = new float[n]; _cg = new float[n]; _cb = new float[n];
        _bmp = new byte[54 + n * 3];
        LastAction = "📋 대기 · " + synth.Width + "×" + synth.Height + " 프록시 · 피닝 " + synth.Strength.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
    }

    public event Action? OnChange;

    public SubPixelOpticalFlowTexturePinningEngine Flow { get; }
    public HighFrequencyDetailBlindDeflickerSynthesizer Synth { get; }
    public GenerativeMorphingSuppressionGuard Guard { get; }
    public GenerativeClipSimulator Clip { get; }
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public long Time => _t;
    public double LastTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public float SplitPos { get; set; } = 0.5f;
    /// <summary>정답(무아티팩트) 대비 평균 절대 오차 — 저역(형태) 기준.</summary>
    public float ShapeErrorIn { get; private set; }
    public float ShapeErrorOut { get; private set; }
    public long MorphInjected { get; private set; }
    public int SeriesCount => _seriesCount;
    public float FlickerInSeries(int i) => _fin[Idx(i)];
    public float FlickerOutSeries(int i) => _fout[Idx(i)];
    public float ConsistencySeries(int i) => _cons[Idx(i)];
    public float MorphSeries(int i) => _morph[Idx(i)];
    public float FrameMsSeries(int i) => _ms[Idx(i)];
    private int Idx(int i) => (_seriesHead - 1 - i + SeriesLen * 2) % SeriesLen;

    public void SetStrength(float s) { lock (_gate) Synth.Strength = Math.Clamp(s, 0f, 1f); LastAction = "📌 피닝 강도 " + Synth.Strength.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture); Raise(); }
    public void SetTolerance(float px) { lock (_gate) Guard.Tolerance = Math.Clamp(px, 0.2f, 6f); Raise(); }
    public void SetArtifacts(float a) { lock (_gate) Clip.Artifacts = Math.Clamp(a, 0f, 2f); Raise(); }
    public void TogglePinning() { lock (_gate) Synth.Pinning = !Synth.Pinning; LastAction = Synth.Pinning ? "📌 텍스처 피닝 ON" : "⬜ 텍스처 피닝 OFF(원본 통과)"; Raise(); }
    public void ToggleGuard() { lock (_gate) Guard.Enabled = !Guard.Enabled; LastAction = Guard.Enabled ? "🛡 모핑 가드 ON" : "⬜ 모핑 가드 OFF"; Raise(); }
    public void ToggleLight() { lock (_gate) Synth.LightDeflicker = !Synth.LightDeflicker; Raise(); }

    public void InjectMorph()
    {
        lock (_gate) { Clip.ForcedMorphUntil = _t + 1 + Clip.MorphFrames; MorphInjected++; }
        LastAction = "🎭 모핑 환각 주입(챙 돌출 · 눈 변형 " + Clip.MorphFrames + " 프레임)";
        Raise();
    }

    public void Restart() { lock (_gate) { _t = 0; Synth.Reset(); Guard.Reset(); } LastAction = "⏮ 클립 처음부터"; Raise(); }

    /// <summary>1틱(무할당).</summary>
    public void Step()
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var t = _t++;
            Clip.Render(t, false, Synth.InputPlane(0), Synth.InputPlane(1), Synth.InputPlane(2));
            Synth.Process(Flow, Guard);
            Clip.Render(t, true, _cr, _cg, _cb);
            double ei = 0, eo = 0;
            int w = Synth.Width, n = _cr.Length;
            for (var y = 1; y < Synth.Height - 1; y++)
                for (var x = 1; x < w - 1; x++)
                {
                    var i = y * w + x;
                    // 3×3 평균(저역) 비교 — 질감 위상은 제외하고 형태만.
                    float c = 0, a = 0, o = 0;
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var k = i + dy * w + dx;
                            c += 0.2126f * _cr[k] + 0.7152f * _cg[k] + 0.0722f * _cb[k];
                            a += 0.2126f * Synth.In(0)[k] + 0.7152f * Synth.In(1)[k] + 0.0722f * Synth.In(2)[k];
                            o += 0.2126f * Synth.Out(0)[k] + 0.7152f * Synth.Out(1)[k] + 0.0722f * Synth.Out(2)[k];
                        }
                    ei += MathF.Abs(a - c) / 9f; eo += MathF.Abs(o - c) / 9f;
                }
            ShapeErrorIn = (float)(ei / n); ShapeErrorOut = (float)(eo / n);
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            _fin[_seriesHead] = Synth.FlickerIn; _fout[_seriesHead] = Synth.FlickerOut; _cons[_seriesHead] = Synth.Consistency; _morph[_seriesHead] = Guard.MaxScore; _ms[_seriesHead] = ms;
            _seriesHead = (_seriesHead + 1) % SeriesLen;
            if (_seriesCount < SeriesLen) _seriesCount++;
            Interlocked.Increment(ref _tick);
            LastTickMs = ms;
        }
    }

    /// <summary>UI 모니터 BMP(0 = 분할(좌 원본 · 우 보정) · 1 = 원본 · 2 = 보정 · 3 = 흐름(HSV) · 4 = 디테일 원본/보정 · 5 = 억제 마스크).</summary>
    public string MonitorBase64(int which)
    {
        lock (_gate)
        {
            int w = Synth.Width, h = Synth.Height, size = _bmp.Length;
            var d = _bmp.AsSpan();
            d.Slice(0, 54).Clear();
            d[0] = (byte)'B'; d[1] = (byte)'M';
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(2), size);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(10), 54);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(14), 40);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(18), w);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(22), -h);
            d[26] = 1; d[28] = 24;
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(34), size - 54);
            var split = (int)(SplitPos * w);
            for (int y = 0, o = 54; y < h; y++)
                for (var x = 0; x < w; x++, o += 3)
                {
                    var i = y * w + x;
                    float r, g, b;
                    switch (which)
                    {
                        case 3:
                        {
                            float fx = Flow.FullFlowX[i], fy = Flow.FullFlowY[i], m = Math.Clamp(MathF.Sqrt(fx * fx + fy * fy) / 3f, 0f, 1f), a = MathF.Atan2(fy, fx);
                            r = m * (0.5f + 0.5f * MathF.Cos(a)); g = m * (0.5f + 0.5f * MathF.Cos(a - 2.094f)); b = m * (0.5f + 0.5f * MathF.Cos(a + 2.094f));
                            var c = Flow.FullConfidence[i]; r = r * c + (1 - c) * 0.6f; g *= c; b *= c;
                            break;
                        }
                        case 4: r = g = b = 0.5f + 4f * (x < split ? Synth.DetailIn[i] : Synth.DetailOut[i]); break;
                        case 5: { var m = Guard.MaskFull[i]; var l = 0.3f * (0.2126f * Synth.Out(0)[i] + 0.7152f * Synth.Out(1)[i] + 0.0722f * Synth.Out(2)[i]); r = l + m; g = l; b = l + 0.3f * m; break; }
                        default:
                        {
                            var src = which == 1 || which == 0 && x < split;
                            r = src ? Synth.In(0)[i] : Synth.Out(0)[i]; g = src ? Synth.In(1)[i] : Synth.Out(1)[i]; b = src ? Synth.In(2)[i] : Synth.Out(2)[i];
                            if (which == 0 && x == split) { r = 1; g = 1; b = 0.3f; }
                            break;
                        }
                    }
                    d[o] = (byte)(Math.Clamp(b, 0f, 1f) * 255f); d[o + 1] = (byte)(Math.Clamp(g, 0f, 1f) * 255f); d[o + 2] = (byte)(Math.Clamp(r, 0f, 1f) * 255f);
                }
            return Convert.ToBase64String(_bmp);
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.Deflicker" };
            _loop.Start();
            LastAction = "▶ 디플리커 가동 · " + Hz + " fps · 서브픽셀 흐름 → 질감 피닝 → 모핑 가드";
        }
        Raise();
    }

    public void Stop()
    {
        Thread? t;
        lock (_gate) { if (!_running) return; _running = false; t = _loop; _loop = null; }
        t?.Join(3000);
        LastAction = "⏸ 디플리커 정지";
        Raise();
    }

    private void Loop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long n = 0;
        while (_running)
        {
            try { Step(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("Deflicker.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }   // 과부하 시 따라잡기 폭주 방지
            if (n % 3 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Deflicker.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
