namespace PromptFlowStudio.Services.ColorScience;

/// <summary>
/// [PromptFlow Studio v17.10.0] 🎨 AI 라이브 DIT 컬러 사이언스 스튜디오 러너(23.976p · 24 Hz 전용 스레드 · 시뮬레이션 결정적).
/// 카메라 플레이트(ACEScg · 키 조명 차트 | LED 스필 차트 | LED 월 협대역 색상 스윕 | 하이라이트 글린트 | 그레이 램프) →
/// LED 스필 영역 메타메리즘 보정(SIMD K_ap1) → ACEScct → ASC CDL → 표시(사면체 3D LUT 33/65 또는 분석 경로: SIMD 709/2020 → 색역 가드 → ACES 2.0 톤스케일) →
/// 퍼레이드 · 벡터스코프 · False Color.
/// </summary>
public sealed class LiveDitColorStudioRunner : IDisposable
{
    public const int Hz = 24, SeriesLen = 240, W = 192, H = 108, N = W * H;
    public const int ChartH = 54, ChartW = 96, CellW = 16, CellH = 18, WallY = 54, WallH = 36, RampY = 90;

    private readonly object _gate = new();
    private readonly float[] _r = new float[N], _g = new float[N], _b = new float[N];
    private readonly float[] _dr = new float[N], _dg = new float[N], _db = new float[N];
    private readonly float[] _ms = new float[SeriesLen], _cdlUs = new float[SeriesLen], _ire = new float[SeriesLen], _oog = new float[SeriesLen];
    private readonly float[] _wall = new float[3 * 4];
    private readonly byte[] _bmp = new byte[54 + 3 * OnSetLiveDitGradingColorDecisionSynthesizer.WaveCols * OnSetLiveDitGradingColorDecisionSynthesizer.WaveBins * 3];
    private int _seriesHead, _seriesCount;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick, _frame;
    private float _time;

    public LiveDitColorStudioRunner(IServiceProvider sp)
        : this((RealtimeAcesColorTransformEngine?)sp.GetService(typeof(RealtimeAcesColorTransformEngine)) ?? new RealtimeAcesColorTransformEngine(),
               (OnSetLiveDitGradingColorDecisionSynthesizer?)sp.GetService(typeof(OnSetLiveDitGradingColorDecisionSynthesizer)) ?? new OnSetLiveDitGradingColorDecisionSynthesizer(),
               (LedSpectralMetamerismGamutClippingGuard?)sp.GetService(typeof(LedSpectralMetamerismGamutClippingGuard)) ?? new LedSpectralMetamerismGamutClippingGuard())
    {
    }

    public LiveDitColorStudioRunner(RealtimeAcesColorTransformEngine engine, OnSetLiveDitGradingColorDecisionSynthesizer synth, LedSpectralMetamerismGamutClippingGuard guard)
    {
        Engine = engine; Synth = synth; Guard = guard;
        CacheWall();
        Guard.ConfigureTarget(Engine.Matrix(AcesSpace.AcesCg, Engine.Monitor));
        LastAction = "📋 대기 · 플레이트 " + W + "×" + H + " ACEScg · 키 " + F0(Guard.KeyCct) + " K · LED " + F0(Guard.LedPeakR) + "/" + F0(Guard.LedPeakG) + "/" + F0(Guard.LedPeakB) + " nm";
    }

    public event Action? OnChange;

    public RealtimeAcesColorTransformEngine Engine { get; }
    public OnSetLiveDitGradingColorDecisionSynthesizer Synth { get; }
    public LedSpectralMetamerismGamutClippingGuard Guard { get; }
    public bool Motion { get; set; } = true;
    /// <summary>노출 보정(스톱).</summary>
    public float ExposureStops { get; set; }
    public bool FalseColor { get; set; }
    /// <summary>0 = 분석 경로 · 33 · 65 = 사면체 LUT.</summary>
    public int LutMode { get; private set; }
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public long Frame => _frame;
    public double LastTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public float MidGrayIre { get; private set; }
    public float SkinKeyIre { get; private set; }
    public float SkinLedIre { get; private set; }
    public float GlintPeak { get; private set; }
    public int SeriesCount => _seriesCount;
    public float FrameMsSeries(int i) => _ms[Idx(i)];
    public float CdlUsSeries(int i) => _cdlUs[Idx(i)];
    public float IreSeries(int i) => _ire[Idx(i)];
    public float OutOfGamutSeries(int i) => _oog[Idx(i)];
    private int Idx(int i) => (_seriesHead - 1 - i + SeriesLen * 2) % SeriesLen;
    public float Display(int c, int p) => c == 0 ? _dr[p] : c == 1 ? _dg[p] : _db[p];

    private static string F0(float v) => v.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
    private static string F2(float v) => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>LED 원색 ACEScg(최대 성분 정규화) 캐시 — 월 콘텐츠 색상 스윕.</summary>
    private void CacheWall()
    {
        for (var j = 0; j < 3; j++)
        {
            Guard.LedPrimaryAcesCg(j, out var r, out var g, out var b);
            r = MathF.Max(0f, r); g = MathF.Max(0f, g); b = MathF.Max(0f, b);   // 월 콘텐츠는 AP1 로 전달(음수 클립)
            var m = MathF.Max(r, MathF.Max(g, b));
            _wall[j * 3] = r / m; _wall[j * 3 + 1] = g / m; _wall[j * 3 + 2] = b / m;
        }
        _wall[9] = _wall[0]; _wall[10] = _wall[1]; _wall[11] = _wall[2];
    }

    // ── 운영자 조작(비 핫패스 · LUT 재베이크 포함) ───────────

    private void Rebake()
    {
        if (LutMode > 0) Engine.BakeDisplayLut(LutMode, Guard.Params, Guard.GuardEnabled);
    }

    public void SetLutMode(int size)
    {
        lock (_gate) { LutMode = size >= 65 ? 65 : size >= 33 ? 33 : 0; Rebake(); }
        LastAction = LutMode == 0 ? "🧮 분석 경로(ACES 2.0 SDR 수식)" : "🧊 사면체 3D LUT " + LutMode + "³ 베이크 " + F2((float)Engine.LastBakeMs) + " ms";
        Raise();
    }

    public void SetMonitor(AcesSpace s)
    {
        lock (_gate) { Engine.SetMonitor(s); Guard.ConfigureTarget(Engine.Matrix(AcesSpace.AcesCg, Engine.Monitor)); Guard.ResetCounters(); Rebake(); }
        LastAction = s == AcesSpace.Rec2020 ? "🖥 모니터 Rec.2020(LED 월 광색역)" : "🖥 모니터 Rec.709(SDR)";
        Raise();
    }

    public void SetGuard(bool on)
    {
        lock (_gate) { Guard.GuardEnabled = on; Guard.ResetCounters(); Rebake(); }
        LastAction = on ? "🛡 색역 압축 가드 ON (ACES RGC 롤오프)" : "⬜ 색역 가드 OFF — 하드 클립";
        Raise();
    }

    public void SetThreshold(float s)
    {
        lock (_gate) { Guard.SetThresholdScale(s); Rebake(); }
        LastAction = "🎚 RGC 임계 × " + F2(Guard.ThresholdScale);
        Raise();
    }

    public void SetCorrection(bool on)
    {
        lock (_gate) Guard.CorrectionEnabled = on;
        LastAction = on ? "💡 메타메리즘 보정 ON · ΔE " + F2(Guard.DeltaEBefore) + " → " + F2(Guard.DeltaEAfter) : "💡 메타메리즘 보정 OFF";
        Raise();
    }

    public void SetDriveMode(bool cameraMatched)
    {
        lock (_gate) { Guard.SetDriveMode(cameraMatched); CacheWall(); }
        LastAction = (cameraMatched ? "📷 카메라 분광 역산 구동" : "👁 눈(XYZ) 기준 구동") + " · 월 화이트 ΔE " + F2(Guard.WallWhiteDeltaE);
        Raise();
    }

    public void SetKeyCct(float cct)
    {
        lock (_gate) { Guard.SetKeyCct(cct); CacheWall(); }
        LastAction = "🔆 키 조명 " + F0(Guard.KeyCct) + " K · ΔE " + F2(Guard.DeltaEBefore) + " → " + F2(Guard.DeltaEAfter);
        Raise();
    }

    public void SetWheel(int i, float x, float y, float master)
    {
        lock (_gate) Synth.SetWheel(i, x, y, master);
        ref readonly var c = ref Synth.Cdl;
        LastAction = "🎡 CDL S " + F2(c.SlopeR) + "/" + F2(c.SlopeG) + "/" + F2(c.SlopeB) + " O " + F2(c.OffsetR) + "/" + F2(c.OffsetG) + "/" + F2(c.OffsetB) + " P " + F2(c.PowerR) + "/" + F2(c.PowerG) + "/" + F2(c.PowerB);
        Raise();
    }

    public void SetSaturation(float s) { lock (_gate) Synth.SetSaturation(s); Raise(); }
    public void SetBypass(bool on) { lock (_gate) Synth.Bypass = on; LastAction = on ? "⏭ CDL 바이패스" : "🎨 CDL 적용"; Raise(); }
    public void ResetGrade() { lock (_gate) Synth.Reset(); LastAction = "↺ 그레이드 초기화(CDL 항등)"; Raise(); }

    // ── 1 틱 ────────────────────────────────────────────────

    /// <summary>1틱(무할당).</summary>
    public void Step()
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            ++_frame;
            if (Motion) _time += 1f / Hz;
            Synthesize();
            if (Guard.CorrectionEnabled)
            {
                var k = Guard.AcesCorrection;
                for (var y = 0; y < ChartH; y++)
                {
                    var o = y * W + ChartW;
                    AcesSimd.Apply(k, _r.AsSpan(o, ChartW), _g.AsSpan(o, ChartW), _b.AsSpan(o, ChartW));
                }
            }
            for (var i = 0; i < N; i++)
            {
                _dr[i] = AcesColorMath.AcesCctEncode(_r[i]); _dg[i] = AcesColorMath.AcesCctEncode(_g[i]); _db[i] = AcesColorMath.AcesCctEncode(_b[i]);
            }
            Synth.ApplyCdl(_dr, _dg, _db);
            if (LutMode > 0 && Engine.LutReady) Engine.ApplyLut(_dr, _dg, _db);
            else Engine.DisplayAnalytic(_dr, _dg, _db, Guard);
            Synth.Analyze(_dr, _dg, _db, W, H);
            MidGrayIre = Luma(3 * CellW + CellW / 2, 2 * CellH + CellH / 2);
            SkinKeyIre = Luma(CellW + CellW / 2, CellH / 2);
            SkinLedIre = Luma(ChartW + CellW + CellW / 2, CellH / 2);
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            _ms[_seriesHead] = ms; _cdlUs[_seriesHead] = (float)Synth.LastCdlUs; _ire[_seriesHead] = Synth.AvgIre; _oog[_seriesHead] = Guard.LastOutOfGamut;
            _seriesHead = (_seriesHead + 1) % SeriesLen;
            if (_seriesCount < SeriesLen) _seriesCount++;
            Interlocked.Increment(ref _tick);
            LastTickMs = ms;
        }
    }

    private float Luma(int x, int y)
    {
        var p = y * W + x;
        return 100f * (0.2126f * Math.Clamp(_dr[p], 0f, 1f) + 0.7152f * Math.Clamp(_dg[p], 0f, 1f) + 0.0722f * Math.Clamp(_db[p], 0f, 1f));
    }

    /// <summary>플레이트 합성(ACEScg 선형 · 무할당).</summary>
    private void Synthesize()
    {
        var t = _time;
        var exp = MathF.Pow(2f, ExposureStops + 0.12f * MathF.Sin(0.7f * t));
        float gx = 128f + 64f * (0.5f + 0.5f * MathF.Sin(0.45f * t)), gy = WallY + WallH * (0.35f + 0.25f * MathF.Sin(0.8f * t));
        GlintPeak = 24f * exp;
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var p = y * W + x;
                float r, g, b;
                if (y < ChartH)
                {
                    var cx = x % ChartW;
                    int col = cx / CellW, row = y / CellH;
                    if (cx % CellW == 0 || y % CellH == 0) { r = g = b = 0.012f; }
                    else
                    {
                        var pi = row * 6 + col;
                        if (x < ChartW) { r = Guard.PatchRef(pi, 0); g = Guard.PatchRef(pi, 1); b = Guard.PatchRef(pi, 2); }
                        else { r = Guard.PatchLed(pi, 0); g = Guard.PatchLed(pi, 1); b = Guard.PatchLed(pi, 2); }
                    }
                }
                else if (y < RampY)
                {
                    var v = (y - WallY) / (float)(WallH - 1);
                    if (x < 128)
                    {
                        var h = x * 3f / 128f;
                        var j = (int)h; var f = h - j;
                        float pr = _wall[j * 3] + f * (_wall[j * 3 + 3] - _wall[j * 3]), pg = _wall[j * 3 + 1] + f * (_wall[j * 3 + 4] - _wall[j * 3 + 1]), pb = _wall[j * 3 + 2] + f * (_wall[j * 3 + 5] - _wall[j * 3 + 2]);
                        const float wl = 0.35f;
                        r = wl + v * (0.8f * pr - wl); g = wl + v * (0.8f * pg - wl); b = wl + v * (0.8f * pb - wl);
                    }
                    else
                    {
                        float dx = x - gx, dy = y - gy;
                        var gl = 24f * MathF.Exp(-(dx * dx + dy * dy) / 18f);
                        var sky = 0.55f - 0.3f * v;
                        r = 0.32f * sky + gl; g = 0.48f * sky + gl; b = 0.95f * sky + gl * 0.9f;
                    }
                }
                else
                {
                    var lin = MathF.Pow(2f, -8f + 12f * x / (W - 1f));
                    r = g = b = lin;
                }
                _r[p] = r * exp; _g[p] = g * exp; _b[p] = b * exp;
            }
    }

    // ── 모니터 ──────────────────────────────────────────────

    private static byte B8(float v) => (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);

    private int Header(int w, int h)
    {
        var size = 54 + w * h * 3;
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
        return size;
    }

    private static void ZoneColor(byte z, ref float r, ref float g, ref float b)
    {
        switch ((FalseColorZone)z)
        {
            case FalseColorZone.Crush: r = 0.55f; g = 0f; b = 0.75f; break;
            case FalseColorZone.Under: r = 0.1f; g = 0.35f; b = 1f; break;
            case FalseColorZone.MidGray: r = 0.2f; g = 0.85f; b = 0.25f; break;
            case FalseColorZone.Skin: r = 1f; g = 0.55f; b = 0.75f; break;
            case FalseColorZone.NearClip: r = 1f; g = 0.95f; b = 0.1f; break;
            case FalseColorZone.Clip: r = 1f; g = 0.1f; b = 0.1f; break;
            default: { var l = (0.2126f * r + 0.7152f * g + 0.0722f * b) * 0.85f; r = g = b = l; break; }
        }
    }

    /// <summary>UI 모니터 BMP(0 표시 · 1 표시 + False Color · 2 RGB 퍼레이드 288×128 · 3 벡터스코프 128²).</summary>
    public string MonitorBase64(int which)
    {
        lock (_gate)
        {
            const int wc = OnSetLiveDitGradingColorDecisionSynthesizer.WaveCols, wb = OnSetLiveDitGradingColorDecisionSynthesizer.WaveBins, vb = OnSetLiveDitGradingColorDecisionSynthesizer.VecBins;
            const float per = OnSetLiveDitGradingColorDecisionSynthesizer.Persist;
            int w, h;
            switch (which)
            {
                case 2: w = 3 * wc; h = wb; break;
                case 3: w = vb; h = vb; break;
                default: w = W; h = H; break;
            }
            var size = Header(w, h);
            var d = _bmp.AsSpan();
            var zones = Synth.Width == W;
            for (int p = 0, o = 54; p < w * h; p++, o += 3)
            {
                float r, g, b;
                int x = p % w, y = p / w;
                switch (which)
                {
                    case 2:
                        {
                            int ch = x / wc, col = x % wc;
                            var v = 1f - MathF.Exp(-Synth.WaveSum(ch, col, wb - 1 - y) / (per * 2.5f));
                            r = ch == 0 ? v : v * 0.15f; g = ch == 1 ? v : v * 0.15f; b = ch == 2 ? v : v * 0.15f;
                            if ((wb - 1 - y) % 32 == 0) { r += 0.12f; g += 0.12f; b += 0.12f; }
                            break;
                        }
                    case 3:
                        {
                            var v = 1f - MathF.Exp(-Synth.VecSum(x, vb - 1 - y) / (per * 1.2f));
                            r = 0.35f * v; g = v; b = 0.55f * v;
                            break;
                        }
                    default:
                        r = _dr[p]; g = _dg[p]; b = _db[p];
                        if (which == 1 && zones) ZoneColor(Synth.Zone(p), ref r, ref g, ref b);
                        break;
                }
                d[o] = B8(b); d[o + 1] = B8(g); d[o + 2] = B8(r);
            }
            return Convert.ToBase64String(_bmp, 0, size);
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.LiveDitColor" };
            _loop.Start();
            LastAction = "▶ DIT 가동 · 23.976p · 플레이트 → 메타메리즘 보정 → ACEScct CDL → ACES 2.0 표시 → 스코프";
        }
        Raise();
    }

    public void Stop()
    {
        Thread? t;
        lock (_gate) { if (!_running) return; _running = false; t = _loop; _loop = null; }
        t?.Join(3000);
        LastAction = "⏸ DIT 정지";
        Raise();
    }

    private void Loop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long n = 0;
        while (_running)
        {
            try { Step(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("LiveDitColor.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }   // 과부하 시 따라잡기 폭주 방지
            if (n % 3 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("LiveDitColor.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
