using System.Globalization;
using PromptFlowStudio.Services.LiveShowroom;
using PromptFlowStudio.Services.MotionTypography;
using PromptFlowStudio.Services.RetentionOptimizer;
using PromptFlowStudio.Services.RhythmAction;
using PromptFlowStudio.Services.SmartPublishing;

namespace PromptFlowStudio.Services.VisualHooks;

/// <summary>
/// [PromptFlow Studio v17.77.0] ⚡ 키네틱 비주얼 훅 &amp; 세이프존 스튜디오 러너.
///   ▸ 타임라인: v17.75 훅 분석(데모 PCM → 데드에어 절삭 · 자극 출력 시각) · v17.54 드롭/악센트 컷 · v17.76 SFX 큐(연결 시 스마트 배포 러너의 실제 큐) ·
///     v17.52 자막 키워드 → <see cref="KineticZoomPunchCutEngine"/> 펀치 · <see cref="AudioReactiveScreenShakeVignetteFilter"/> 셰이크 · <see cref="ProceduralVectorStickerOverlaySynthesizer"/> 스티커.
///   ▸ 프레임: 절차적 아바타 장면 → 스마트 피벗 줌 → 세이프존 클램프 자막(v17.74 하단 기본 위치 → 시프트) + 스티커 → 셰이크/비네트 → (옵션) 세이프존 가이드.
///     216×384(9:16) 사전 할당 3 버퍼 · <see cref="RenderFrame"/> 무할당 · 60 fps 타임라인(재생 30 Hz × 2 프레임).
/// </summary>
public sealed class KineticVisualHookStudioRunner : IDisposable
{
    public const int W = 216, H = 384, DurationMs = 6000, TotalFrames = DurationMs * KineticZoomPunchCutEngine.Fps / 1000, StepFrames = 2, TickMs = 33, HookFs = 16000;
    public const double BudgetUs = 1e6 / 60;
    public const string DemoCaption = "이거 진짜 대박 돈 버는 법 불 붙었다 끝까지 보면 사랑";
    /// <summary>v17.74 동적 자막 기본 위치(하단 1/3 · 세이프존 하단 22 % 침범).</summary>
    public static readonly HookBox CaptionRequested = new(0.08f, 0.80f, 0.76f, 0.09f);

    private static readonly RhythmCutEvent[] DemoCuts =
    {
        new() { PresentSec = 1.2, Reason = RhythmCutReason.Phrase },
        new() { PresentSec = 2.4, Reason = RhythmCutReason.Accent },
        new() { PresentSec = 3.6, Reason = RhythmCutReason.Drop },
        new() { PresentSec = 4.8, Reason = RhythmCutReason.Beat },
    };
    private static readonly PurchaseEvent[] DemoBuys = { new() { Qty = 1, AtTick = 1000 + 252 }, new() { Qty = 3, AtTick = 1000 + 318 } };

    private readonly SmartPublishingSchedulerStudioRunner? _sps;
    private readonly uint[] _base = new uint[W * H], _mid = new uint[W * H], _out = new uint[W * H];
    private readonly byte[] _rgba = new byte[W * H * 4];
    private readonly short[] _hookPcm = new short[HookFs * DurationMs / 1000];
    private readonly HookBox[] _boxes = new HookBox[1];
    private readonly string?[] _spritePng = new string?[ProceduralVectorStickerOverlaySynthesizer.Kinds];
    private readonly object _gate = new();
    private string? _png;
    private int _version, _pngVersion = -1;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public KineticVisualHookStudioRunner(IServiceProvider sp) : this(sp.GetService(typeof(SmartPublishingSchedulerStudioRunner)) as SmartPublishingSchedulerStudioRunner) { }

    public KineticVisualHookStudioRunner(SmartPublishingSchedulerStudioRunner? sps = null)
    {
        _sps = sps;
        Typo = new RealtimeWordByWordMotionTypographyEngine(new SdfGlyphAtlas());
        Stickers.Guard = Guard;
        Shake.Configure(W, H);
        Zoom.SetFace(new HookBox(0.36f, 0.22f, 0.28f, 0.17f));
        BuildTimeline();
        RenderFrame(0);
    }

    public event Action? OnChange;
    public KineticZoomPunchCutEngine Zoom { get; } = new();
    public ReelsUiSafeZoneReframingGuard Guard { get; } = new();
    public ProceduralVectorStickerOverlaySynthesizer Stickers { get; } = new();
    public AudioReactiveScreenShakeVignetteFilter Shake { get; } = new();
    public ProceduralViralSoundFxSynthesizer Sfx { get; } = new();
    public ViralHookDeadAirAutoTrimmerEngine Hook { get; } = new();
    public RealtimeWordByWordMotionTypographyEngine Typo { get; }
    public bool SchedulerLinked => _sps is not null;
    /// <summary>마지막 타임라인이 v17.76 러너의 실제 SFX 큐를 사용했는지.</summary>
    public bool UsedLinkedSfx { get; private set; }
    public bool ShowSafeZone { get; set; } = true;
    public bool Running => _loop is { IsCompleted: false };
    public long Frame { get; private set; }
    public string? LastAction { get; private set; }
    public HookBox Caption => _boxes[0];
    public ReframeVerdict CaptionVerdict { get; private set; }
    public float LastScale { get; private set; } = 1f;
    public bool LastShake { get; private set; }
    public int LastStickers { get; private set; }
    public long FramesRendered { get; private set; }
    public long OverBudget { get; private set; }
    public double LastFrameUs { get; private set; }
    public double MaxFrameUs { get; private set; }
    public int ShakeEvents => Shake.Count;
    public ReadOnlySpan<uint> Output => _out;

    // ══════════════════════ 타임라인 ══════════════════════
    /// <summary>훅 · 컷 · SFX · 키워드 → 펀치/셰이크/스티커 재구성 + 자막 세이프존 리프레이밍.</summary>
    public void BuildTimeline()
    {
        lock (_gate)
        {
            Zoom.Clear(); Shake.Clear(); Stickers.Clear();
            Typo.SetCaption(DemoCaption, TypoLang.Ko, 300);
            var src = Sfx;
            UsedLinkedSfx = _sps is not null && _sps.Sfx.CueCount > 0;
            if (UsedLinkedSfx) src = _sps!.Sfx;
            else
            {
                Sfx.ClearCues();
                Sfx.CollectCaptions(Typo);
                Sfx.CollectCuts(DemoCuts);
                Sfx.CollectPurchases(DemoBuys, 1000, 60);
            }
            SynthesizeHookPcm(_hookPcm);
            Hook.ScanAudio(_hookPcm, HookFs);
            Hook.Plan();
            Zoom.CollectHook(Hook);
            Zoom.CollectCuts(DemoCuts);
            Zoom.CollectSfx(src);
            Zoom.CollectKeywords(Typo);
            Shake.CollectSfx(src);
            Stickers.CollectCaptions(Typo);
            Stickers.CollectSfx(src);
            _boxes[0] = CaptionRequested;
            CaptionVerdict = Guard.Clamp(ref _boxes[0]);
            _version++;
        }
        LastAction = "⚡ 타임라인 — 펀치 " + Zoom.Count.ToString(CultureInfo.InvariantCulture) + " · 셰이크 " + Shake.Count.ToString(CultureInfo.InvariantCulture)
            + " · 스티커 " + Stickers.Spawned.ToString(CultureInfo.InvariantCulture) + (UsedLinkedSfx ? " (v17.76 SFX 연동)" : null);
        Raise();
    }

    /// <summary>데모 훅 오디오(16 kHz): 0~0.3 s 발화 · 0.3~0.9 s 데드에어 · 0.9~1.1 s 자극 버스트 · 이후 발화.</summary>
    public static void SynthesizeHookPcm(Span<short> pcm)
    {
        for (var i = 0; i < pcm.Length; i++)
        {
            var t = (double)i / HookFs;
            double v;
            if (t >= 0.3 && t < 0.9) v = 0;
            else if (t >= 0.9 && t < 1.1) v = 0.8 * Math.Sin(2 * Math.PI * 180 * t);
            else v = 0.1 * Math.Sin(2 * Math.PI * (220 + 60 * Math.Sin(2 * Math.PI * 3 * t)) * t);
            pcm[i] = (short)(v * 32767);
        }
    }

    public void SetStrength(float strength)
    {
        Zoom.Strength = Math.Clamp(strength, 0f, 1f);
        BuildTimeline();
        RenderFrame(Frame);
    }

    public void SetPlatforms(ReelsPlatforms targets)
    {
        Guard.Targets = targets;
        BuildTimeline();
        RenderFrame(Frame);
        LastAction = "📐 세이프존 → " + ReelsUiSafeZoneReframingGuard.PlatformName(Guard.Targets) + " · 자막 " + VerdictName(CaptionVerdict);
        Raise();
    }

    public void ToggleSafeZone() { ShowSafeZone = !ShowSafeZone; RenderFrame(Frame); Raise(); }

    // ══════════════════════ 프레임 ══════════════════════
    /// <summary>1 프레임 합성(무할당).</summary>
    public void RenderFrame(long frame)
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            frame = Math.Clamp(frame, 0, TotalFrames - 1);
            DrawScene(_base, frame, Zoom.Face);
            LastScale = Zoom.Apply(_base, _mid, W, H, frame);
            DrawCaption(_mid, frame);
            LastStickers = Stickers.Render(_mid, W, H, frame);
            LastShake = Shake.Apply(_mid, _out, W, H, frame);
            if (ShowSafeZone) DrawSafeZone(_out, Guard.SafeRect);
            Frame = frame; FramesRendered++;
            LastFrameUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
            if (LastFrameUs > MaxFrameUs) MaxFrameUs = LastFrameUs;
            if (LastFrameUs > BudgetUs) OverBudget++;
            _version++;
        }
    }

    public void Seek(long frame) { RenderFrame(frame); Raise(); }

    /// <summary>절차적 9:16 장면 — 그라디언트 + 이동 사선 + 아바타(어깨 · 목 · 얼굴 · 머리 · 눈 · 입 립싱크).</summary>
    private static void DrawScene(Span<uint> dst, long frame, in HookBox face)
    {
        for (var y = 0; y < H; y++)
        {
            var t = y / (float)H;
            var r = (uint)(64 - 48 * t); var g = (uint)(28 - 14 * t); var b = (uint)(120 - 80 * t);
            var row = dst.Slice(y * W, W);
            for (var x = 0; x < W; x++)
            {
                var stripe = ((x + y + (int)(frame * 2)) / 18 & 1) == 0 ? 10u : 0u;
                row[x] = 0xFF000000u | (r + stripe) << 16 | (g + stripe) << 8 | (b + stripe);
            }
        }
        var fcx = face.Cx * W; var fcy = face.Cy * H; var frx = face.W * W * 0.5f; var fry = face.H * H * 0.5f;
        FillEllipse(dst, W * 0.5f, fcy + fry * 3.4f, W * 0.36f, fry * 1.9f, 0xFF1D4ED8u);
        FillEllipse(dst, fcx, fcy + fry * 1.1f, frx * 0.38f, fry * 0.6f, 0xFFE0AC8Cu);
        FillEllipse(dst, fcx, fcy, frx, fry, 0xFFF5C8A8u);
        FillEllipse(dst, fcx, fcy - fry * 0.62f, frx * 1.04f, fry * 0.48f, 0xFF2A1E1Eu);
        FillEllipse(dst, fcx - frx * 0.36f, fcy + fry * 0.02f, frx * 0.1f, fry * 0.08f, 0xFF111111u);
        FillEllipse(dst, fcx + frx * 0.36f, fcy + fry * 0.02f, frx * 0.1f, fry * 0.08f, 0xFF111111u);
        var open = 0.05f + 0.13f * MathF.Abs(MathF.Sin(frame * 0.45f));
        FillEllipse(dst, fcx, fcy + fry * 0.48f, frx * 0.28f, fry * open, 0xFF7F1D1Du);
    }

    private static void FillEllipse(Span<uint> dst, float cx, float cy, float rx, float ry, uint color)
    {
        if (rx <= 0 || ry <= 0) return;
        var y0 = Math.Max(0, (int)(cy - ry)); var y1 = Math.Min(H - 1, (int)(cy + ry));
        for (var y = y0; y <= y1; y++)
        {
            var dy = (y + 0.5f - cy) / ry; var k = 1f - dy * dy;
            if (k <= 0) continue;
            var hw = rx * MathF.Sqrt(k);
            var x0 = Math.Max(0, (int)(cx - hw + 0.5f)); var x1 = Math.Min(W - 1, (int)(cx + hw - 0.5f));
            if (x1 >= x0) dst.Slice(y * W + x0, x1 - x0 + 1).Fill(color);
        }
    }

    /// <summary>세이프존 보정 자막 박스 — 반투명 배경 + 단어 블록(현재 발화 단어 = 노랑 하이라이트).</summary>
    private void DrawCaption(Span<uint> dst, long frame)
    {
        ReelsUiSafeZoneReframingGuard.ToPixels(in _boxes[0], W, H, out var x0, out var y0, out var x1, out var y1);
        for (var y = y0; y < y1; y++)
        {
            var row = dst.Slice(y * W, W);
            for (var x = x0; x < x1; x++) row[x] = ProceduralVectorStickerOverlaySynthesizer.Blend(row[x], 0xFF000000u, 150);
        }
        var ms = frame * 1000.0 / KineticZoomPunchCutEngine.Fps;
        int px = x0 + 4, py = y0 + 4; const int Row = 11, Bh = 7;
        for (var i = 0; i < Typo.WordCount; i++)
        {
            var bw = Math.Max(6, Typo.WordText(i).Length * 6);
            if (px + bw > x1 - 4) { px = x0 + 4; py += Row; }
            if (py + Bh > y1) break;
            ref readonly var wd = ref Typo.Word(i);
            var color = ms >= wd.StartMs && ms < wd.EndMs ? 0xFFFDE047u : ms >= wd.EndMs ? 0xFFF8FAFCu : 0xFF64748Bu;
            for (var y = py; y < py + Bh; y++) dst.Slice(y * W + px, bw).Fill(color);
            px += bw + 4;
        }
    }

    /// <summary>세이프존 가이드 — 금지 마진 적색 틴트 + 경계 점선.</summary>
    private static void DrawSafeZone(Span<uint> dst, in HookBox safe)
    {
        ReelsUiSafeZoneReframingGuard.ToPixels(in safe, W, H, out var sx0, out var sy0, out var sx1, out var sy1);
        for (var y = 0; y < H; y++)
        {
            var row = dst.Slice(y * W, W);
            var inY = y >= sy0 && y < sy1;
            for (var x = 0; x < W; x++)
            {
                if (inY && x >= sx0 && x < sx1)
                {
                    var edge = (y == sy0 || y == sy1 - 1) && (x >> 2 & 1) == 0 || (x == sx0 || x == sx1 - 1) && (y >> 2 & 1) == 0;
                    if (edge) row[x] = 0xFFF8FAFCu;
                    continue;
                }
                row[x] = ProceduralVectorStickerOverlaySynthesizer.Blend(row[x], 0xFFEF4444u, 70);
            }
        }
    }

    /// <summary>전체 타임라인 렌더 벤치(무할당) — 반환 = 예산 초과 프레임 수.</summary>
    public long RenderAll()
    {
        var before = OverBudget;
        for (var f = 0; f < TotalFrames; f++) RenderFrame(f);
        return OverBudget - before;
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
                catch (Exception ex) { LastAction = "⚠ " + ex.Message; PromptFlowStudio.Program.Log("VisualHooks.Loop", ex); }
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
            _png = Png(_rgba, W, H);
            _pngVersion = _version;
            return _png;
        }
    }

    /// <summary>스티커 스프라이트 PNG(알파 · 1 회 캐시).</summary>
    public string StickerDataUri(StickerKind k)
    {
        var cached = _spritePng[(int)k];
        if (cached is not null) return cached;
        const int S = ProceduralVectorStickerOverlaySynthesizer.Size;
        var spr = Stickers.Sprite(k);
        var rgba = new byte[S * S * 4];
        for (var i = 0; i < spr.Length; i++)
        {
            var p = spr[i]; var o = i * 4;
            rgba[o] = (byte)(p >> 16); rgba[o + 1] = (byte)(p >> 8); rgba[o + 2] = (byte)p; rgba[o + 3] = (byte)(p >> 24);
        }
        return _spritePng[(int)k] = Png(rgba, S, S);
    }

    private static string Png(byte[] rgba, int w, int h)
    {
        using var ms = new MemoryStream(w * h);
        PromptFlowStudio.Services.Media.BabyPngWriter.WriteRgba(ms, w, h, rgba);
        return "data:image/png;base64," + Convert.ToBase64String(ms.GetBuffer(), 0, (int)ms.Length);
    }

    public static string VerdictName(ReframeVerdict v) => v switch { ReframeVerdict.Inside => "안전", ReframeVerdict.Shifted => "시프트 보정", _ => "축소 보정" };

    public static double FrameMs(long frame) => frame * 1000.0 / KineticZoomPunchCutEngine.Fps;

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("VisualHooks.OnChange", ex); }
    }

    public void Dispose()
    {
        Stop();
        OnChange = null;
    }

    public string Summary() =>
        "펀치 " + Zoom.Count.ToString(CultureInfo.InvariantCulture) + " · 셰이크 " + Shake.Count.ToString(CultureInfo.InvariantCulture) + " · 스티커 " + Stickers.Spawned.ToString(CultureInfo.InvariantCulture)
        + " · 세이프존 시프트 " + Guard.Shifts.ToString(CultureInfo.InvariantCulture) + " · 프레임 " + LastFrameUs.ToString("0", CultureInfo.InvariantCulture) + " µs";
}
