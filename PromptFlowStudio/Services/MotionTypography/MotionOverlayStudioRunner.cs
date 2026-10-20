using System.Globalization;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.VerticalTranscode;
namespace PromptFlowStudio.Services.MotionTypography;

/// <summary>
/// [PromptFlow Studio v17.52.0] 🔤 9:16 모션 타이포그래피 &amp; 오버레이 스튜디오 러너(60 Hz 미리보기 · v17.49 <see cref="IVideoFrameOverlay"/> 구현).
/// 프레임 = 레이어 Begin → 오버레이(z0~3) → 자막(z4) → (인코딩 결선 시) NV12 합성. 미리보기는 그라데이션 플레이트 + 레이어를 1/4(270×480) BMP 로 축소한다.
/// 캡션 · 프리셋 · 폰트 · 플랫폼 마스크 변경은 비핫패스(글리프 준비 · 레이아웃 · 세이프 존 재계산).
/// </summary>
public sealed class MotionOverlayStudioRunner : IVideoFrameOverlay, IDisposable
{
    public const int Hz = 60, W = 1080, H = 1920, PW = 270, PH = 480, PreviewBytes = 54 + PW * PH * 3, Hist = 240;
    public static readonly string[] Fonts = { "Malgun Gothic", "Arial Black", "Impact" };
    public const string DefaultCaption = "이 영상 끝까지 보면 진짜 소름 돋는 반전이 나옵니다 저장하고 친구한테 공유하세요";
    private readonly object _gate = new();
    private readonly float[] _msS = new float[Hist];
    private readonly VerticalTranscoderStudioRunner? _trans;
    private int _head, _series;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;
    private float _t;

    public MotionOverlayStudioRunner(IServiceProvider sp) : this(sp.GetService(typeof(VerticalTranscoderStudioRunner)) as VerticalTranscoderStudioRunner) { }
    public MotionOverlayStudioRunner(VerticalTranscoderStudioRunner? trans = null)
    {
        _trans = trans;
        Atlas = new SdfGlyphAtlas();
        Typo = new RealtimeWordByWordMotionTypographyEngine(Atlas);
        Overlay = new EngagementBoostingOverlaySynthesizer(Atlas);
        Guard = new CrossPlatformUiSafeZoneGuard(W, H, CrossPlatformUiSafeZoneGuard.All);
        Layer = new RgbaOverlayLayer(W, H);
        SetCaption(DefaultCaption, TypoLang.Ko);
    }

    public event Action? OnChange;
    public SdfGlyphAtlas Atlas { get; }
    public RealtimeWordByWordMotionTypographyEngine Typo { get; }
    public EngagementBoostingOverlaySynthesizer Overlay { get; }
    public CrossPlatformUiSafeZoneGuard Guard { get; }
    public RgbaOverlayLayer Layer { get; }
    public string Caption { get; private set; } = DefaultCaption;
    public TypoLang Lang { get; private set; } = TypoLang.Ko;
    public float DurationMs => MathF.Max(Typo.TotalMs + 600, 3000);
    public float TimeMs => _t;
    public bool Attached => _trans is not null && ReferenceEquals(_trans.Overlay, this);
    public bool TranscoderLinked => _trans is not null;
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastFrameMs { get; private set; }
    public double MaxFrameMs { get; private set; }
    public long OverlayFrames { get; private set; }
    public string? LastAction { get; private set; }
    public int SeriesCount => _series;
    public float FrameMsSeries(int i) => _msS[(_head - 1 - i + 2 * Hist) % Hist];
    public static string PresetName(TypoPreset p) => p switch { TypoPreset.Pop => "Pop", TypoPreset.Typewriter => "Typewriter", _ => "Bounce" };

    // ══════════════════════ 제어(비핫패스) ══════════════════════
    public void SetCaption(string text, TypoLang lang)
    {
        lock (_gate)
        {
            Caption = text; Lang = lang;
            Typo.SetCaption(text, lang);
            Relayout();
            Overlay.SetCuts(stackalloc float[] { Typo.TotalMs * 0.5f, Typo.TotalMs });
            _t = 0;
        }
        LastAction = "✍ 캡션 " + Typo.WordCount.ToString(CultureInfo.InvariantCulture) + " 단어 · " + Typo.PageCount.ToString(CultureInfo.InvariantCulture) + " 페이지 · " + (Typo.TotalMs / 1000f).ToString("0.0", CultureInfo.InvariantCulture) + " s";
        Raise();
    }

    private void Relayout() => Typo.Layout((Guard.Safe.W - 2 * RealtimeWordByWordMotionTypographyEngine.OutlineEm * Typo.FontPx) / Typo.FontPx);

    public void SetPreset(TypoPreset p) { lock (_gate) Typo.Preset = p; LastAction = "🎞 프리셋 " + PresetName(p); Raise(); }
    public void SetFont(string font)
    {
        lock (_gate) { Atlas.SetFont(font); Typo.SetCaption(Caption, Lang); Overlay.SetVote("다음 편은?", "A 도시", "B 바다", Overlay.VoteShareA); Relayout(); }
        LastAction = "🔠 폰트 " + font;
        Raise();
    }
    public void SetFontPx(float px) { lock (_gate) { Typo.FontPx = Math.Clamp(px, 40f, 140f); Relayout(); } Raise(); }
    public void SetPlatforms(int mask) { lock (_gate) { Guard.Configure(W, H, mask); Relayout(); } LastAction = "🛡 세이프 존 " + Safe(); Raise(); }
    public void SetProgress(bool on) { lock (_gate) Overlay.Progress = on; Raise(); }
    public void SetVote(bool on) { lock (_gate) Overlay.Vote = on; Raise(); }
    public void SetBadge(bool on) { lock (_gate) Overlay.Badge = on; Raise(); }
    public void SetTransitions(bool on) { lock (_gate) Overlay.Transitions = on; Raise(); }
    public void Seek(float ms) { lock (_gate) _t = Math.Clamp(ms, 0, DurationMs); Raise(); }

    /// <summary>v17.49 트랜스코더 녹화 프레임에 결선/해제.</summary>
    public bool Attach(bool on)
    {
        if (_trans is null) return false;
        _trans.Overlay = on ? this : null;
        LastAction = on ? "🔗 v17.49 인코딩 직전 합성 ON" : "🔗 인코딩 결선 해제";
        Raise();
        return true;
    }

    public string Safe() { var s = Guard.Safe; return s.X.ToString(CultureInfo.InvariantCulture) + "," + s.Y.ToString(CultureInfo.InvariantCulture) + " " + s.W.ToString(CultureInfo.InvariantCulture) + "×" + s.H.ToString(CultureInfo.InvariantCulture); }

    // ══════════════════════ 프레임(무할당) ══════════════════════
    /// <summary>tMs 시각의 전 레이어 렌더.</summary>
    public void RenderFrame(float tMs)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Layer.Begin();
        Overlay.Render(tMs, DurationMs, Layer, Guard);
        Typo.Render(tMs, Layer, Guard);
        var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        LastFrameMs = ms;
        if (ms > MaxFrameMs && _tick > 5) MaxFrameMs = ms;
        _msS[_head] = (float)ms; _head = (_head + 1) % Hist;
        if (_series < Hist) _series++;
    }

    /// <summary><see cref="IVideoFrameOverlay"/> — 인코딩 프레임(타임라인 = 프레임/fps 루프)에 합성(무할당).</summary>
    public void Apply(Span<byte> y, Span<byte> uv, int width, int height, long frameIndex, int fps)
    {
        lock (_gate)
        {
            if (width != W || height != H) return;
            RenderFrame((float)(frameIndex * 1000.0 / fps % DurationMs));
            Layer.CompositeNv12(y, uv, width);
            OverlayFrames++;
        }
    }

    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("MotionOverlay.Step");
            Interlocked.Increment(ref _tick);
            _t += 1000f / Hz;
            if (_t > DurationMs) _t = 0;
            RenderFrame(_t);
        }
    }

    /// <summary>미리보기 BMP(1/4 · 하단부터 BGR) — 그라데이션 플레이트 위 레이어 합성(2×2 박스 평균).</summary>
    public void WritePreviewBmp(Span<byte> dst)
    {
        dst[..54].Clear();
        dst[0] = (byte)'B'; dst[1] = (byte)'M';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst[2..], PreviewBytes);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst[10..], 54);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst[14..], 40);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst[18..], PW);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst[22..], PH);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(dst[26..], 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(dst[28..], 24);
        lock (_gate)
        {
            var px = Layer.Pixels;
            for (var py = 0; py < PH; py++)
            {
                var d = 54 + py * PW * 3;
                var sy = (PH - 1 - py) * 4;
                for (var pxx = 0; pxx < PW; pxx++)
                {
                    float r = 0, g = 0, b = 0, a = 0;
                    for (var k = 0; k < 4; k++)
                    {
                        var o = ((sy + (k >> 1) * 2) * W + pxx * 4 + (k & 1) * 2) * 4;
                        r += px[o]; g += px[o + 1]; b += px[o + 2]; a += px[o + 3];
                    }
                    var fa = a / 1020f;
                    var v = (float)(PH - 1 - py) / PH;
                    float br = 0.12f + 0.25f * v, bg = 0.1f + 0.1f * v, bb = 0.25f + 0.2f * (1 - v);
                    dst[d + pxx * 3] = (byte)Math.Clamp(bb * (1 - fa) * 255f + b / 4f, 0f, 255f);
                    dst[d + pxx * 3 + 1] = (byte)Math.Clamp(bg * (1 - fa) * 255f + g / 4f, 0f, 255f);
                    dst[d + pxx * 3 + 2] = (byte)Math.Clamp(br * (1 - fa) * 255f + r / 4f, 0f, 255f);
                }
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.MotionOverlay" };
            _loop.Start();
        }
        LastAction = "▶ 미리보기 60 Hz";
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("MotionOverlay.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }
            if (n % 6 == 0) Raise();
        }
    }
    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("MotionOverlay.Runner.OnChange", ex); }
    }
    public void Dispose() { Stop(); if (Attached) _trans!.Overlay = null; }
}
