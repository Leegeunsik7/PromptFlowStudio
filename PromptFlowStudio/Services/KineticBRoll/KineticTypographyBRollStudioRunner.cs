using System.Globalization;
using PromptFlowStudio.Services.MotionTypography;
using PromptFlowStudio.Services.VisualHooks;

namespace PromptFlowStudio.Services.KineticBRoll;

/// <summary>
/// [PromptFlow Studio v17.78.0] 🎬 키네틱 자막 &amp; B-Roll 스튜디오 러너.
///   ▸ 대본 → <see cref="WordByWordKineticSubtitleEngine"/>(v17.52 음절 타이밍 · 3 단어 윈도우) + <see cref="DynamicBRollInsertSynthesizer"/>(키워드 → 1.5~2.5 s 인서트).
///   ▸ 프레임(216×384 · 60 fps 타임라인): 정적 배경(프레임 캐시 링 적중 시 복사) → 아바타(동적) → B-Roll 믹싱 → 단어 자막(스프라이트 풀) — <see cref="RenderFrame"/> 무할당.
///   ▸ <see cref="ZeroAllocRenderFrameCacheGuard"/> 가 래스터 절감률 · 16.7 ms 예산 · 저비용 모드를 관리. v17.77 비주얼 훅 러너가 있으면 세이프존 대상 플랫폼을 동기화.
/// </summary>
public sealed class KineticTypographyBRollStudioRunner : IDisposable
{
    public const int W = 216, H = 384, Fps = 60, StepFrames = 2, TickMs = 33;
    public const float FontPx = 26f;
    public const string DemoScript = "AI로 하루 만에 돈 버는 진짜 비밀. 자동차 한 대 값이 3억원 매출로 폭발했습니다! 스마트폰 하나면 누구나 충분합니다. 구독자 성장 비결도 지금 공개합니다. 끝까지 보고 바로 시작하세요.";
    private const ulong SceneKey = 0x5CE17780UL;

    private readonly KineticVisualHookStudioRunner? _hooks;
    private readonly uint[] _out = new uint[W * H];
    private readonly byte[] _rgba = new byte[W * H * 4];
    private readonly BRollHit[] _hits = new BRollHit[64];
    private readonly object _gate = new();
    private string? _png;
    private int _version, _pngVersion = -1, _hitCount;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public KineticTypographyBRollStudioRunner(IServiceProvider sp) : this(sp.GetService(typeof(KineticVisualHookStudioRunner)) as KineticVisualHookStudioRunner) { }

    public KineticTypographyBRollStudioRunner(KineticVisualHookStudioRunner? hooks = null)
    {
        _hooks = hooks;
        Cache.Configure(W, H);
        Subtitles = new WordByWordKineticSubtitleEngine(new SdfGlyphAtlas(), Cache) { SafeZone = SafeZone };
        BRoll.SafeZone = SafeZone;
        BRoll.Configure(W, H);
        BuildTimeline();
        RenderFrame(0);
    }

    public event Action? OnChange;
    public ZeroAllocRenderFrameCacheGuard Cache { get; } = new();
    public ReelsUiSafeZoneReframingGuard SafeZone { get; } = new();
    public WordByWordKineticSubtitleEngine Subtitles { get; }
    public DynamicBRollInsertSynthesizer BRoll { get; } = new();
    public bool HooksLinked => _hooks is not null;
    public string Script { get; private set; } = DemoScript;
    public int TotalFrames { get; private set; } = 1;
    public long Frame { get; private set; }
    public bool Running => _loop is { IsCompleted: false };
    public string? LastAction { get; private set; }
    public int LastInsert { get; private set; } = -1;
    public int LastWords { get; private set; }
    public double LastFrameUs { get; private set; }
    public long FramesRendered { get; private set; }
    public int HitCount => _hitCount;
    public ref readonly BRollHit Hit(int i) => ref _hits[i];
    public ReadOnlySpan<uint> Output => _out;
    public HormoziPreset Preset => Subtitles.Preset;

    // ══════════════════════ 타임라인 ══════════════════════
    public void BuildTimeline() => SetScript(Script);

    /// <summary>대본 교체(비핫패스) — 타이밍 · 청크 · 키워드 감지 · 인서트 재예약.</summary>
    public void SetScript(string script)
    {
        lock (_gate)
        {
            Script = string.IsNullOrWhiteSpace(script) ? DemoScript : script;
            if (_hooks is not null) SafeZone.Targets = _hooks.Guard.Targets;
            Subtitles.SetScript(Script, TypoLang.Ko, 200);
            Subtitles.Relayout(W, FontPx);
            _hitCount = BRoll.Detect(Script, _hits);
            BRoll.Clear();
            BRoll.ScheduleFromTimeline(Subtitles.Timeline);
            TotalFrames = Math.Max(1, (int)((Subtitles.Timeline.TotalMs + 600) * Fps / 1000));
            _version++;
        }
        LastAction = "🎬 타임라인 — 단어 " + Subtitles.WordCount.ToString(CultureInfo.InvariantCulture) + " · 청크 " + Subtitles.ChunkCount.ToString(CultureInfo.InvariantCulture)
            + " · 키워드 " + _hitCount.ToString(CultureInfo.InvariantCulture) + " · B-Roll " + BRoll.Count.ToString(CultureInfo.InvariantCulture) + (HooksLinked ? " (v17.77 세이프존 동기화)" : null);
        Raise();
    }

    public void SetPreset(HormoziPreset p)
    {
        Subtitles.Preset = p;
        RenderFrame(Frame);
        LastAction = "🎨 프리셋 → " + WordByWordKineticSubtitleEngine.PresetName(p);
        Raise();
    }

    // ══════════════════════ 프레임 ══════════════════════
    public static float FrameMs(long frame) => frame * 1000f / Fps;

    /// <summary>1 프레임 합성(무할당).</summary>
    public void RenderFrame(long frame)
    {
        lock (_gate)
        {
            Cache.BeginFrame();
            frame = Math.Clamp(frame, 0, TotalFrames - 1);
            var ms = FrameMs(frame);
            if (Cache.TryGetFrame(SceneKey, out var bg)) bg.CopyTo(_out);
            else
            {
                var dst = Cache.AcquireFrame(SceneKey);
                DrawBackground(dst);
                Cache.CommitFrame((long)W * H);
                dst.CopyTo(_out);
            }
            Cache.Spend(DrawAvatar(_out, frame, BRoll.Face));
            var shaded = BRoll.PixelsShaded;
            LastInsert = BRoll.Composite(_out, W, H, ms, Cache.Degraded);
            Cache.Spend(BRoll.PixelsShaded - shaded);
            LastWords = Subtitles.Render(_out, W, H, ms, Cache.Degraded);
            Frame = frame; FramesRendered++;
            LastFrameUs = Cache.EndFrame();
            _version++;
        }
    }

    public void Seek(long frame) { RenderFrame(frame); Raise(); }

    /// <summary>전체 타임라인 렌더 — 반환 = 예산 초과 프레임 수.</summary>
    public long RenderAll()
    {
        var before = Cache.OverBudget;
        for (var f = 0; f < TotalFrames; f++) RenderFrame(f);
        return Cache.OverBudget - before;
    }

    /// <summary>정적 배경(세트 그라디언트 + 바닥 그리드 + 조명 비네트) — 캐시 미스 시에만.</summary>
    private static void DrawBackground(Span<uint> dst)
    {
        for (var y = 0; y < H; y++)
        {
            var t = y / (float)H;
            var row = dst.Slice(y * W, W);
            for (var x = 0; x < W; x++)
            {
                var nx = x / (float)W - 0.5f; var ny = t - 0.35f;
                var light = MathF.Max(0f, 1f - (nx * nx * 2.2f + ny * ny * 1.6f));
                float r = 18 + 40 * light, g = 22 + 30 * light, b = 40 + 70 * light;
                if (t > 0.72f && ((x + (int)((t - 0.72f) * 300)) % 24 == 0 || (int)(t * 400) % 18 == 0)) { r += 18; g += 18; b += 28; }
                row[x] = 0xFF000000u | (uint)Math.Min(255f, r) << 16 | (uint)Math.Min(255f, g) << 8 | (uint)Math.Min(255f, b);
            }
        }
    }

    /// <summary>동적 아바타(어깨 · 얼굴 · 머리 · 눈 깜빡임 · 립싱크) — 반환 = 칠한 픽셀 수.</summary>
    private static long DrawAvatar(Span<uint> dst, long frame, in HookBox face)
    {
        var fcx = face.Cx * W; var fcy = face.Cy * H; var frx = face.W * W * 0.5f; var fry = face.H * H * 0.5f;
        long n = 0;
        n += FillEllipse(dst, W * 0.5f, fcy + fry * 3.4f, W * 0.38f, fry * 1.9f, 0xFF7C3AEDu);
        n += FillEllipse(dst, fcx, fcy + fry * 1.1f, frx * 0.38f, fry * 0.6f, 0xFFE0AC8Cu);
        n += FillEllipse(dst, fcx, fcy, frx, fry, 0xFFF5C8A8u);
        n += FillEllipse(dst, fcx, fcy - fry * 0.62f, frx * 1.04f, fry * 0.48f, 0xFF3F2A1Eu);
        var blink = frame % 150 < 6 ? 0.02f : 0.08f;
        n += FillEllipse(dst, fcx - frx * 0.36f, fcy + fry * 0.02f, frx * 0.1f, fry * blink, 0xFF111111u);
        n += FillEllipse(dst, fcx + frx * 0.36f, fcy + fry * 0.02f, frx * 0.1f, fry * blink, 0xFF111111u);
        var open = 0.05f + 0.13f * MathF.Abs(MathF.Sin(frame * 0.45f));
        n += FillEllipse(dst, fcx, fcy + fry * 0.48f, frx * 0.28f, fry * open, 0xFF7F1D1Du);
        return n;
    }

    private static long FillEllipse(Span<uint> dst, float cx, float cy, float rx, float ry, uint color)
    {
        if (rx <= 0 || ry <= 0) return 0;
        long n = 0;
        var y0 = Math.Max(0, (int)(cy - ry)); var y1 = Math.Min(H - 1, (int)(cy + ry));
        for (var y = y0; y <= y1; y++)
        {
            var dy = (y + 0.5f - cy) / ry; var k = 1f - dy * dy;
            if (k <= 0) continue;
            var hw = rx * MathF.Sqrt(k);
            var x0 = Math.Max(0, (int)(cx - hw + 0.5f)); var x1 = Math.Min(W - 1, (int)(cx + hw - 0.5f));
            if (x1 >= x0) { dst.Slice(y * W + x0, x1 - x0 + 1).Fill(color); n += x1 - x0 + 1; }
        }
        return n;
    }

    // ══════════════════════ 재생 ══════════════════════
    public void Start()
    {
        if (Running) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(async () =>
        {
            var tick = 0;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    RenderFrame((Frame + StepFrames) % TotalFrames);
                    if (++tick % 3 == 0) Raise();
                }
                catch (Exception ex) { LastAction = "⚠ " + ex.Message; PromptFlowStudio.Program.Log("KineticBRoll.Loop", ex); }
                try { await Task.Delay(TickMs, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }, ct);
        LastAction = "▶ 재생(60 fps 타임라인 · 30 Hz 미리보기)"; Raise();
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loop?.Wait(5_000); } catch (AggregateException) { }
        _cts?.Dispose(); _cts = null; _loop = null;
        Raise();
    }

    // ══════════════════════ UI 보조 ══════════════════════
    /// <summary>현재 프레임 PNG 데이터 URI(버전 캐시 · UI 전용 할당).</summary>
    public string PreviewDataUri()
    {
        lock (_gate)
        {
            if (_png is not null && _pngVersion == _version) return _png;
            for (var i = 0; i < _out.Length; i++)
            {
                var p = _out[i]; var o = i * 4;
                _rgba[o] = (byte)(p >> 16); _rgba[o + 1] = (byte)(p >> 8); _rgba[o + 2] = (byte)p; _rgba[o + 3] = 255;
            }
            using var ms = new MemoryStream(W * H);
            PromptFlowStudio.Services.Media.BabyPngWriter.WriteRgba(ms, W, H, _rgba);
            _png = "data:image/png;base64," + Convert.ToBase64String(ms.GetBuffer(), 0, (int)ms.Length);
            _pngVersion = _version;
            return _png;
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("KineticBRoll.OnChange", ex); }
    }

    public void Dispose()
    {
        Stop();
        OnChange = null;
    }

    public string Summary() =>
        "단어 " + Subtitles.WordCount.ToString(CultureInfo.InvariantCulture) + " · B-Roll " + BRoll.Count.ToString(CultureInfo.InvariantCulture)
        + " · 캐시 절감 " + (Cache.Savings * 100).ToString("0", CultureInfo.InvariantCulture) + "% · 스프라이트 적중 " + Cache.SpriteHits.ToString(CultureInfo.InvariantCulture)
        + " · 프레임 " + LastFrameUs.ToString("0", CultureInfo.InvariantCulture) + " µs" + (Cache.Degraded ? " · 저비용 모드" : null);
}
