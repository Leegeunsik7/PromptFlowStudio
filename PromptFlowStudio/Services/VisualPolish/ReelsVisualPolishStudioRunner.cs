using System.Globalization;
using PromptFlowStudio.Services.Autopilot;
using PromptFlowStudio.Services.BroadcastMcr;
using PromptFlowStudio.Services.LiveToReels;
using PromptFlowStudio.Services.Publisher;
using PromptFlowStudio.Services.SemanticChat;
using PromptFlowStudio.Services.Showrunner;
using PromptFlowStudio.Services.VerticalTranscode;

namespace PromptFlowStudio.Services.VisualPolish;

/// <summary>v17.74.0 폴리시 결과 1 건(릴 · 썸네일 · A/B · 동적 번-인 MP4).</summary>
public sealed class PolishedReel : IDisposable
{
    public int ReelId { get; init; }
    public GoldenFrameSnapshot Golden { get; } = new();
    public bool HasGolden { get; internal set; }
    public byte[]? Png { get; internal set; }
    public byte[]? PreviewBmp { get; internal set; }
    public AbVariantPair? Ab { get; internal set; }
    public SegmentedChunkStream? BurnedMp4 { get; internal set; }
    public MotionBurnResult Burn { get; internal set; }
    public double ThumbMs { get; internal set; }
    public string? Note { get; internal set; }
    public void Dispose() { BurnedMp4?.Dispose(); BurnedMp4 = null; }
}

/// <summary>
/// [PromptFlow Studio v17.74.0] 🎨 릴스 비주얼 폴리시 &amp; 썸네일 팩토리 러너.
///   ▸ 결선: <see cref="PeakEmotionFrameTracker"/> = v17.66 MCR 장면 레이어(60 Hz 무할당 · v17.67 피격/v17.72 열광 부스트).
///   ▸ <see cref="PolishAsync"/>(백그라운드): 릴(v17.69) 창 골든 프레임 → 3DGS 1080×1920 타일 썸네일 + 글로우 + 헤드라인(PNG) · A/B 메타 · 동적 모션 하드서브 번-인(디코드 가능 시) → 결과 링(8).
///   ▸ 플레이어: 번-인 MP4 디코드 → 프레임 미리보기 BMP(축소) · 시크.
/// </summary>
public sealed class ReelsVisualPolishStudioRunner : IDisposable
{
    public const int Ring = 8, PreviewW = 135, PreviewH = 240;

    private readonly BroadcastMcrStudioRunner? _mcr;
    private readonly LiveToReelsFactoryStudioRunner? _reels;
    private readonly OmniChannelPublisherStudioRunner? _pub;
    private readonly PolishedReel?[] _ring = new PolishedReel?[Ring];
    private readonly HardwareVideoDemuxFrameDecoder _player = new();
    private byte[] _py = Array.Empty<byte>(), _puv = Array.Empty<byte>(), _prgba = Array.Empty<byte>();
    private int _head, _n, _playerReel = -1;
    private Task? _task;

    public ReelsVisualPolishStudioRunner(IServiceProvider sp) : this(
        sp.GetService(typeof(BroadcastMcrStudioRunner)) as BroadcastMcrStudioRunner,
        sp.GetService(typeof(LiveToReelsFactoryStudioRunner)) as LiveToReelsFactoryStudioRunner,
        sp.GetService(typeof(OmniChannelPublisherStudioRunner)) as OmniChannelPublisherStudioRunner,
        sp.GetService(typeof(InteractiveShowrunnerStudioRunner)) as InteractiveShowrunnerStudioRunner,
        sp.GetService(typeof(SemanticChatWeatherStudioRunner)) as SemanticChatWeatherStudioRunner) { }

    public ReelsVisualPolishStudioRunner(BroadcastMcrStudioRunner? mcr = null, LiveToReelsFactoryStudioRunner? reels = null, OmniChannelPublisherStudioRunner? pub = null,
        InteractiveShowrunnerStudioRunner? showrunner = null, SemanticChatWeatherStudioRunner? chat = null)
    {
        _mcr = mcr; _reels = reels; _pub = pub;
        Tracker = new PeakEmotionFrameTracker(mcr);
        if (showrunner is not null) Tracker.HitCounter = () => showrunner.Spawner.Hits;
        if (chat is not null) Tracker.HypeProbe = () => chat.Vector.Hype;
        Thumbs = mcr is not null ? new PeakEmotion3dgsThumbnailSynthesizer(mcr.EnvSplats, mcr.BodySplats, mcr.FaceSplats) : new PeakEmotion3dgsThumbnailSynthesizer(60_000, 20_000, 4_000);
    }

    public event Action? OnChange;
    public PeakEmotionFrameTracker Tracker { get; }
    public PeakEmotion3dgsThumbnailSynthesizer Thumbs { get; }
    public DynamicMotionSubtitleBurnInSynthesizer Motion { get; } = new(new HardwareSubtitleBurnInEngine(540, 960));
    public AbTestingMetadataVariantGenerator Ab { get; } = new();
    public bool Attached { get; private set; }
    public bool Busy => _task is { IsCompleted: false };
    public long Polished { get; private set; }
    public string? LastAction { get; private set; }
    public int Count => _n;
    public PolishedReel? At(int i) => (uint)i < (uint)_n ? _ring[(_head - 1 - i + 2 * Ring) % Ring] : null;
    public PolishedReel? Find(int reelId) { for (var i = 0; i < _n; i++) if (At(i)?.ReelId == reelId) return At(i); return null; }
    public int PlayerFrames => _player.FrameCount;
    public int PlayerPosition => _player.Position;
    public VideoDecoderBackend PlayerBackend => _player.Backend;

    public void Attach()
    {
        if (_mcr is null || Attached) return;
        _mcr.AttachLayer(Tracker);
        Attached = true;
        LastAction = "🔗 MCR 감정 피크 추적 레이어 결선"; Raise();
    }

    public void Detach()
    {
        if (_mcr is null || !Attached) return;
        _mcr.DetachLayer(Tracker);
        Attached = false;
    }

    /// <summary>최신 릴(또는 지정 릴) 폴리시 — 백그라운드.</summary>
    public bool PolishLatest()
    {
        var r = _reels?.Reel(0);
        if (r is null) { LastAction = "⚠ 릴 없음 — v17.69 팩토리에서 릴 생성 필요"; Raise(); return false; }
        var start = r.Request.StartMs; var end = r.Request.EndMs;
        if (end <= start) { end = Tracker.Clock(); start = end - (long)(Math.Max(15, r.DurationSec) * 1000); }
        // 감지 시계(팩토리 = 자체 ms) ≠ 추적 시계 가능 → 창 = 최근 길이만큼
        var now = Tracker.Clock(); var len = Math.Max(15_000, end - start);
        return Start(r.Id, now - len, now, r.Media, ContextFor(r));
    }

    private ShortformContext ContextFor(FactoryReel r) => _pub?.ContextFor(r) ?? new ShortformContext { Caption = r.Caption, KeyLine = r.Caption, Cause = (int)r.Request.Cause, Peak = r.Request.Peak, DurationSec = r.DurationSec };

    public bool Start(int reelId, long startMs, long endMs, SegmentedChunkStream? media, ShortformContext ctx)
    {
        if (Busy) return false;
        _task = Task.Run(() => Polish(reelId, startMs, endMs, media, ctx));
        LastAction = "🎨 릴 #" + reelId.ToString(CultureInfo.InvariantCulture) + " 폴리시 시작"; Raise();
        return true;
    }

    public Task WaitAsync() => _task ?? Task.CompletedTask;

    /// <summary>동기 폴리시(백그라운드 스레드 · 회귀) — 골든 프레임 → 썸네일 · A/B · 동적 번-인.</summary>
    public PolishedReel Polish(int reelId, long startMs, long endMs, SegmentedChunkStream? media, ShortformContext ctx)
    {
        var p = new PolishedReel { ReelId = reelId };
        p.HasGolden = Tracker.TryGolden(startMs, endMs, p.Golden);
        var mood = AbTestingMetadataVariantGenerator.MoodOf(p.HasGolden ? p.Golden : null);
        p.Ab = Ab.Generate(reelId, ctx, mood);
        if (p.HasGolden)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            lock (Thumbs)
            {
                Thumbs.Render(p.Golden, Headline(p.Ab.A.Title));
                using var ms = new MemoryStream(512 * 1024);
                Thumbs.WritePng(ms);
                p.Png = ms.ToArray();
                p.PreviewBmp = new byte[PeakEmotion3dgsThumbnailSynthesizer.PreviewBytes(PreviewW, PreviewH)];
                Thumbs.WritePreviewBmp(p.PreviewBmp, PreviewW, PreviewH);
            }
            p.ThumbMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }
        else p.Note = "골든 프레임 없음(MCR 추적 기록 부재)";
        if (media is not null)
        {
            var outS = new SegmentedChunkStream();
            lock (Motion)
            {
                var r = Motion.Burn(media, (p.Ab.A.Title + ". " + p.Ab.A.Description).AsSpan(), outS);
                p.Burn = r;
                if (r.Ok) { outS.Position = 0; p.BurnedMp4 = outS; } else { outS.Dispose(); p.Note = (p.Note is null ? "" : p.Note + " · ") + "동적 번-인 불가: " + r.Error; }
            }
        }
        lock (_ring) { _ring[_head]?.Dispose(); _ring[_head] = p; _head = (_head + 1) % Ring; if (_n < Ring) _n++; }
        Polished++;
        LastAction = "✅ 릴 #" + reelId.ToString(CultureInfo.InvariantCulture) + " 폴리시 · 썸네일 " + (p.Png is null ? "—" : (p.Png.Length / 1024).ToString(CultureInfo.InvariantCulture) + " KB")
            + " · 번-인 " + (p.Burn.Ok ? p.Burn.Frames.ToString(CultureInfo.InvariantCulture) + " f(" + p.Burn.Decoder + ")" : "—");
        Raise();
        return p;
    }

    private static string Headline(string title) { var t = title.Replace("#Shorts", "", StringComparison.OrdinalIgnoreCase).Trim(); return t.Length > 24 ? t[..24] : t; }

    /// <summary>샘플 클립(실 H.264 · 움직이는 그라데이션/MCR PGM) — 데모 · 회귀.</summary>
    public SegmentedChunkStream CreateSampleClip(double seconds = 3, int width = 270, int height = 480, int fps = 30, bool allowMf = false)
    {
        var enc = new HardwareSubtitleBurnInEngine(width, height) { Enabled = false };
        IBurnInFrameSource src = new GradientFrameSource();
        if (_mcr is { } m && m.Mcr.VideoFrames > 0) { var ps = new McrProgramFrameSource(); ps.Capture(m.Mcr.ProgramOut); src = ps; }
        var s = new SegmentedChunkStream();
        enc.BurnReel(src, seconds, fps, s, 3000, allowMf);
        s.Position = 0;
        return s;
    }

    /// <summary>A/B 선택 → v17.71 초안 반영.</summary>
    public void Choose(int reelId, AbVariant v)
    {
        var p = Find(reelId);
        if (p?.Ab is not { } ab) return;
        Ab.Choose(ab, v);
        var m = ab.Get(v);
        if (_pub?.FindDraft(reelId) is not null) _pub.EditDraft(reelId, m.Title, m.Description, string.Join(' ', m.Tags.AsSpan(0, m.TagCount).ToArray()));
        LastAction = "🧪 릴 #" + reelId.ToString(CultureInfo.InvariantCulture) + " 변형 " + v + " 확정"; Raise();
    }

    // ══════════════════════ 플레이어(UI) ══════════════════════
    /// <summary>번-인 MP4 프레임 i → 미리보기 BMP(축소 · 비핫패스).</summary>
    public bool PlayerFrame(int reelId, int frame, byte[] bmp)
    {
        var p = Find(reelId);
        if (p?.BurnedMp4 is not { } mp4) return false;
        lock (_player)
        {
            if (_playerReel != reelId) { if (!_player.Open(mp4)) return false; _playerReel = reelId; }
            int w = _player.Width, h = _player.Height;
            if (_py.Length != w * h) { _py = new byte[w * h]; _puv = new byte[w * h / 2]; _prgba = new byte[w * h * 4]; }
            frame = Math.Clamp(frame, 0, _player.FrameCount - 1);
            if (frame == _player.Position) { if (!_player.NextFrame(_py, _puv)) return false; }
            else if (!_player.Seek(frame, _py, _puv) || !_player.NextFrame(_py, _puv)) return false;
            HardwareVideoDemuxFrameDecoder.ToRgba32(_py, _puv, w, h, _prgba);
            WriteBmp(_prgba, w, h, bmp, PreviewW, PreviewH);
        }
        return true;
    }

    public static void WriteBmp(ReadOnlySpan<byte> rgba, int w, int h, byte[] dst, int ow, int oh)
    {
        var row = ow * 3; var pad = (4 - row % 4) % 4;
        dst.AsSpan(0, 54).Clear();
        dst[0] = (byte)'B'; dst[1] = (byte)'M';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst.AsSpan(2), 54 + (row + pad) * oh);
        dst[10] = 54; dst[14] = 40;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst.AsSpan(18), ow); System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst.AsSpan(22), oh);
        dst[26] = 1; dst[28] = 24;
        for (var y = 0; y < oh; y++)
        {
            var o = 54 + (oh - 1 - y) * (row + pad); var sy = Math.Min(h - 1, y * h / oh);
            for (var x = 0; x < ow; x++) { var p = (sy * w + Math.Min(w - 1, x * w / ow)) * 4; dst[o + x * 3] = rgba[p + 2]; dst[o + x * 3 + 1] = rgba[p + 1]; dst[o + x * 3 + 2] = rgba[p]; }
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("VisualPolish.OnChange", ex); }
    }

    public void Dispose()
    {
        Detach();
        try { _task?.Wait(5_000); } catch (AggregateException) { }
        lock (_ring) { foreach (var p in _ring) p?.Dispose(); Array.Clear(_ring); _n = 0; }
        _player.Dispose();
        Thumbs.Dispose();
        OnChange = null;
    }

    public string Summary() =>
        "피크 " + Tracker.BestScore.ToString("0.00", CultureInfo.InvariantCulture) + " · 폴리시 " + Polished.ToString(CultureInfo.InvariantCulture) + " · A/B " + Ab.Generated.ToString(CultureInfo.InvariantCulture)
        + " · 번-인 " + (Motion.Last.Ok ? Motion.Last.MedianFrameMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms/f" : "—");
}
