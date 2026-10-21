using System.Globalization;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.InfluencerLive;
using PromptFlowStudio.Services.OmniChannel;
using PromptFlowStudio.Services.VerticalTranscode;
namespace PromptFlowStudio.Services.StreamGateway;

/// <summary>v17.62.0 무손실 1080p 클립(원본 비트스트림 리먹스 fMP4 · 세그먼트 스트림 소유).</summary>
public sealed class LosslessHighlightClip : IDisposable
{
    public int Id { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Fps { get; init; }
    public int Frames { get; init; }
    public int AudioFrames { get; init; }
    public int Fragments { get; init; }
    public long StartIndex { get; init; }
    public double DurationSec { get; init; }
    public double RemuxMs { get; init; }
    public bool Auto { get; init; }
    public DateTime CreatedUtc { get; init; }
    public SegmentedChunkStream? Media { get; internal set; }
    public long Bytes => Media?.Length ?? 0;
    public bool Published { get; internal set; }
    public void Dispose() { Media?.Dispose(); Media = null; }
}

/// <summary>[v17.62.0] 탐색 가능 · 쓰기 폐기 스트림 — 상시 송출 인코더 세션의 fMP4 먹서 출력 흡수(무할당).</summary>
public sealed class DiscardSeekableStream : Stream
{
    private long _len, _pos;
    public override bool CanRead => false;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => _len;
    public override long Position { get => _pos; set => _pos = Math.Max(0, value); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => 0;
    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _pos + offset, _ => _len + offset };
    public override void SetLength(long value) => _len = Math.Max(0, value);
    public override void Write(byte[] buffer, int offset, int count) => Advance(count);
    public override void Write(ReadOnlySpan<byte> buffer) => Advance(buffer.Length);
    private void Advance(int n) { _pos += n; if (_pos > _len) _len = _pos; }
}

/// <summary>
/// [PromptFlow Studio v17.62.0] 🛰️ 스트림 게이트웨이 &amp; 비트스트림 클리퍼 스튜디오 러너.
///   ▸ 송출 인코더 스레드: 1080×1920 60 fps 방송 패턴(NV12 · 톤) → v17.49 트랜스코더(MF 하드웨어 → 관리 코드 폴백) → 탭 → 32 MB 압축 링버퍼. v17.49 스튜디오 인코더에 탭 결선도 가능.
///   ▸ 60 Hz 틱: 배압 큐 소비(자체 v17.61 리액션 엔진 또는 v17.61 인플루언서 러너 결선) · 초당 세이프티 통과 채팅 → 평소 대비 250 % 스파이크 → 자동 클립 요청.
///   ▸ 클립 스레드: 링버퍼 무손실 리먹스(≤ 10 ms) → 세그먼트 스트림 → v17.51 옴니채널 배포 · 저장. 틱 연산 무할당.
/// </summary>
public sealed class StreamGatewayClipperStudioRunner : IDisposable
{
    public const int Hz = 60, Hist = 240, Feed = 32, MaxClips = 10, PreviewW = 135, PreviewH = 240, PreviewBytes = 54 + PreviewW * PreviewH * 3;
    public const int DrainMax = 64, CooldownMs = 20_000, WarmupSec = 10;
    public const double DrainBudgetUs = 2000;
    public const float SpikeRatio = 2.5f, SpikeFloor = 3f;

    private readonly object _gate = new(), _encGate = new(), _clipGate = new(), _prevGate = new();
    private readonly VirtualInfluencerLiveStudioRunner? _influencer;
    private readonly OmniChannelEngagementStudioRunner? _omni;
    private readonly VerticalTranscoderStudioRunner? _trans;
    private readonly ChatSpamToxicitySafetyGuard _ownSafety = new();
    private readonly OwnTarget _own;
    private readonly RealtimeHardwareVerticalTranscoderEngine _enc = new();
    private readonly List<LosslessHighlightClip> _clips = new(MaxClips);
    private readonly float[] _msS = new float[Hist], _rateS = new float[Hist], _dropS = new float[Hist];
    private readonly float[] _cleanS = new float[120];
    private readonly byte[] _preview = new byte[PreviewBytes];
    private readonly AutoResetEvent _clipEvt = new(false);
    private byte[] _y = Array.Empty<byte>(), _uv = Array.Empty<byte>();
    private float[] _l = Array.Empty<float>(), _r = Array.Empty<float>();
    private int _sHead, _sN, _cHead, _cN, _clipId;
    private long _secStart = -1, _lastClean, _cooldownUntil, _encFrames, _lastDrops;
    private float _baseClean;
    private double _phase, _clipReq;
    private bool _clipAuto;
    private Thread? _loop, _encLoop, _clipLoop;
    private volatile bool _running, _encoding;
    private long _tick;

    public StreamGatewayClipperStudioRunner(IServiceProvider sp) : this(
        sp.GetService(typeof(VirtualInfluencerLiveStudioRunner)) as VirtualInfluencerLiveStudioRunner,
        sp.GetService(typeof(OmniChannelEngagementStudioRunner)) as OmniChannelEngagementStudioRunner,
        sp.GetService(typeof(VerticalTranscoderStudioRunner)) as VerticalTranscoderStudioRunner) { }

    public StreamGatewayClipperStudioRunner(VirtualInfluencerLiveStudioRunner? influencer = null, OmniChannelEngagementStudioRunner? omni = null, VerticalTranscoderStudioRunner? trans = null)
    {
        _influencer = influencer; _omni = omni; _trans = trans;
        Guard = new SocketBackpressureDropoutGuard();
        Gateway = new MultiPlatformLiveChatGatewaySynthesizer(Guard);
        Chat = new RealtimeViewerChatReactionTriggerEngine(_ownSafety);
        _own = new OwnTarget(this);
        _enc.Tap = Ring;
        _preview[0] = (byte)'B';
    }

    public event Action? OnChange;
    /// <summary>게이트웨이 시계(ms) — 기본 <see cref="GatewayClock"/> · 테스트 가상 시간 주입.</summary>
    public Func<long> Clock { get; set; } = static () => GatewayClock.NowMs;
    public SocketBackpressureDropoutGuard Guard { get; }
    public MultiPlatformLiveChatGatewaySynthesizer Gateway { get; }
    public CompressedBitstreamRollingBufferEngine Ring { get; } = new();
    public RealtimeHardwareVerticalTranscoderEngine Encoder => _enc;
    /// <summary>자체 v17.61 리액션 엔진(인플루언서 러너 미결선 시 소비자).</summary>
    public RealtimeViewerChatReactionTriggerEngine Chat { get; }
    public bool InfluencerAvailable => _influencer is not null;
    public bool OmniLinked => _omni is not null;
    public bool TranscoderAvailable => _trans is not null;
    public bool LinkInfluencer { get; private set; }
    /// <summary>v17.67 세이프티 통과 채팅 커맨드 탭(쇼러너 `!drop` · `!confetti`) — 자체 소비 경로에서 전달.</summary>
    public IChatDispatchTarget? CommandTap { get; set; }
    /// <summary>v17.68 라이브 커머스 결제 탭(`!buy` · 후원 금액) — 세이프티 통과 채팅만.</summary>
    public IChatDispatchTarget? CommerceTap { get; set; }
    public bool TapTranscoder { get; private set; }
    public bool AutoClip { get; private set; } = true;
    public bool AutoPublish { get; private set; }
    public double ClipSeconds { get; private set; } = 20;
    public int Width { get; private set; } = 1080;
    public int Height { get; private set; } = 1920;
    public int Fps { get; private set; } = 60;
    public int Kbps { get; private set; } = 6000;
    public bool AllowMediaFoundation { get; private set; } = true;
    public bool IsRunning => _running;
    public bool IsEncoding => _encoding;
    public long EncodedFrames => Interlocked.Read(ref _encFrames);
    public long Tick => Interlocked.Read(ref _tick);
    public long NowMs => Tick * 1000 / Hz;
    public double LastTickMs { get; private set; }
    public double MaxTickMs { get; private set; }
    public long FramesOverBudget { get; private set; }
    public long Spikes { get; private set; }
    public float CleanPerSec { get; private set; }
    public float BaselineClean => _baseClean;
    public float SpikeLevel { get; private set; }
    public long Published { get; private set; }
    public string? LastAction { get; private set; }
    public int SeriesCount => _sN;
    private int Idx(int i) => (_sHead - 1 - i + 2 * Hist) % Hist;
    public float FrameMsSeries(int i) => _msS[Idx(i)];
    public float RateSeries(int i) => _rateS[Idx(i)];
    public float DropSeries(int i) => _dropS[Idx(i)];
    public int CleanCount => _cN;
    public float CleanSeries(int i) => _cleanS[(_cHead - 1 - i + 2 * _cleanS.Length) % _cleanS.Length];
    public int ClipCount { get { lock (_clips) return _clips.Count; } }
    public LosslessHighlightClip? Clip(int i) { lock (_clips) return (uint)i < (uint)_clips.Count ? _clips[i] : null; }
    public LosslessHighlightClip? FindClip(int id) { lock (_clips) { foreach (var c in _clips) if (c.Id == id) return c; return null; } }
    public int FeedCount => _own.Count;
    public ref readonly OwnTarget.Entry FeedAt(int i) => ref _own.At(i);
    public ReadOnlySpan<char> FeedUser(int i) => _own.User(i);
    public ReadOnlySpan<char> FeedText(int i) => _own.Text(i);

    /// <summary>자체 소비자 — v17.61 엔진 Accept + UI 피드 링(무할당).</summary>
    public sealed class OwnTarget : IChatDispatchTarget
    {
        public struct Entry { public ChatPlatform Platform; public ChatRole Role; public ChatSafetyVerdict Verdict; public int Amount, UserLen, TextLen; }
        private readonly StreamGatewayClipperStudioRunner _r;
        private readonly Entry[] _e = new Entry[Feed];
        private readonly char[] _c = new char[Feed * SocketBackpressureDropoutGuard.SlotChars];
        private int _head, _n;
        internal OwnTarget(StreamGatewayClipperStudioRunner r) => _r = r;
        public int Count => _n;
        private int S(int i) => (_head - 1 - i + 2 * Feed) % Feed;
        public ref readonly Entry At(int i) => ref _e[S(i)];
        public ReadOnlySpan<char> User(int i) => _c.AsSpan(S(i) * SocketBackpressureDropoutGuard.SlotChars, _e[S(i)].UserLen);
        public ReadOnlySpan<char> Text(int i) => _c.AsSpan(S(i) * SocketBackpressureDropoutGuard.SlotChars + SocketBackpressureDropoutGuard.UserCap, _e[S(i)].TextLen);

        public bool Deliver(ChatPlatform platform, ReadOnlySpan<char> user, ReadOnlySpan<char> text, int amount, ChatRole role, long nowMs)
        {
            var v = _r.Chat.Accept(user, text, amount, _r.NowMs);
            ref var e = ref _e[_head];
            e.Platform = platform; e.Role = role; e.Verdict = v; e.Amount = amount;
            e.UserLen = Math.Min(user.Length, SocketBackpressureDropoutGuard.UserCap); e.TextLen = Math.Min(text.Length, SocketBackpressureDropoutGuard.TextCap);
            var c = _c.AsSpan(_head * SocketBackpressureDropoutGuard.SlotChars, SocketBackpressureDropoutGuard.SlotChars);
            user[..e.UserLen].CopyTo(c); text[..e.TextLen].CopyTo(c[SocketBackpressureDropoutGuard.UserCap..]);
            _head = (_head + 1) % Feed; if (_n < Feed) _n++;
            if (v == ChatSafetyVerdict.Clean && text.Length > 1 && text[0] == '!') { _r.CommandTap?.Deliver(platform, user, text, amount, role, nowMs); _r.CommerceTap?.Deliver(platform, user, text, amount, role, nowMs); }
            return v == ChatSafetyVerdict.Clean;
        }
    }

    // ══════════════════════ 제어(비핫패스) ══════════════════════
    public void ConnectSimulated(ChatPlatform p) { Gateway.ConnectSimulated(p); LastAction = "🧪 " + MultiPlatformLiveChatGatewaySynthesizer.PlatformName(p) + " 시뮬레이션 소켓 연결"; Raise(); }
    public void ConnectSimulatedAll() { for (var i = 0; i < 3; i++) Gateway.ConnectSimulated((ChatPlatform)i); LastAction = "🧪 3 플랫폼 시뮬레이션 소켓 연결"; Raise(); }
    public void Disconnect(ChatPlatform p) { Gateway.Disconnect(p); LastAction = "⏏ " + MultiPlatformLiveChatGatewaySynthesizer.PlatformName(p) + " 연결 해제"; Raise(); }

    /// <summary>실서비스 연결(Twitch 채널 · YouTube videoId · 치지직 channelId). 해석 실패 시 시뮬레이션 폴백.</summary>
    public async Task<bool> ConnectLiveAsync(ChatPlatform p, string id, bool fallbackToSim = true)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var ok = true;
        try
        {
            using var to = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            switch (p)
            {
                case ChatPlatform.Twitch: Gateway.ConnectTwitch(id); break;
                case ChatPlatform.YouTube: ok = await Gateway.ConnectYouTubeVideoAsync(id, to.Token).ConfigureAwait(false); break;
                default: ok = await Gateway.ConnectChzzkChannelAsync(id, to.Token).ConfigureAwait(false); break;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            PromptFlowStudio.Program.Log("Gateway.ConnectLive", ex);
            ok = false;
        }
        if (!ok && fallbackToSim) Gateway.ConnectSimulated(p);
        LastAction = ok ? "🛰 " + MultiPlatformLiveChatGatewaySynthesizer.PlatformName(p) + " 실서비스 연결 · " + id : "⚠ " + MultiPlatformLiveChatGatewaySynthesizer.PlatformName(p) + " 해석 실패(키/채널) → " + (fallbackToSim ? "시뮬레이션 폴백" : "중단");
        Raise();
        return ok;
    }

    public void StartRaid(double seconds = 8) { Gateway.Traffic.StartRaid(seconds); LastAction = "🌊 레이드 폭주 " + (Gateway.Traffic.RaidRatePerSec * 3).ToString("0", CultureInfo.InvariantCulture) + " msg/s · " + seconds.ToString("0", CultureInfo.InvariantCulture) + " s"; Raise(); }
    public void SetFlaky(bool on) { Gateway.Traffic.Flaky = on; LastAction = on ? "📶 불안정 네트워크(순단 · 접속 거부) ON" : "📶 안정 네트워크"; Raise(); }
    public void ForceDrop(ChatPlatform p) { Gateway.Traffic.ForceDrop(p); LastAction = "✂ " + MultiPlatformLiveChatGatewaySynthesizer.PlatformName(p) + " 소켓 강제 순단"; Raise(); }
    public void SetTrafficRate(float perPlatform) { Gateway.Traffic.RatePerSec = Math.Clamp(perPlatform, 0f, 200f); Raise(); }
    public void SetAutoClip(bool on) { AutoClip = on; LastAction = on ? "✂ 스파이크 자동 무손실 클립 ON" : "✂ 자동 클립 OFF"; Raise(); }
    public void SetClipSeconds(double s) { ClipSeconds = Math.Clamp(s, 3, CompressedBitstreamRollingBufferEngine.MaxClipSec); Raise(); }
    public void SetAutoPublish(bool on) { if (on && _omni is null) return; AutoPublish = on; LastAction = on ? "🚀 클립 즉시 v17.51 옴니채널 배포" : "🚀 수동 배포"; Raise(); }

    public void SetLinkInfluencer(bool on)
    {
        if (on && _influencer is null) return;
        LinkInfluencer = on;
        if (_influencer is not null) _influencer.ExternalIngress = on ? Guard : null;
        LastAction = on ? "🔗 v17.61 인플루언서 리액션 엔진 결선(아바타 반응)" : "🔗 자체 리액션 엔진";
        Raise();
    }

    public bool SetTapTranscoder(bool on)
    {
        if (_trans is null) return false;
        if (on) StopEncoder();
        _trans.Engine.Tap = on ? Ring : null;
        TapTranscoder = on;
        LastAction = on ? "🔗 v17.49 스튜디오 인코더 출력 탭 → 링버퍼" : "🔗 v17.49 탭 해제";
        Raise();
        return true;
    }

    public bool ConfigureEncoder(int width, int height, int fps, int kbps, bool allowMf)
    {
        if (_encoding) { LastAction = "⚠ 송출 중 — 정지 후 변경"; Raise(); return false; }
        Width = Math.Max(16, width & ~1); Height = Math.Max(16, height & ~1); Fps = Math.Clamp(fps, 10, 120);
        Kbps = Math.Clamp(kbps, 500, LocalHttpsWebhookBitrateBudgetGuard.CapKbps); AllowMediaFoundation = allowMf;
        LastAction = "⚙ 송출 " + Width.ToString(CultureInfo.InvariantCulture) + "×" + Height.ToString(CultureInfo.InvariantCulture) + " · " + Fps.ToString(CultureInfo.InvariantCulture) + " fps · " + Kbps.ToString(CultureInfo.InvariantCulture) + " kbps";
        Raise();
        return true;
    }

    public bool StartEncoder()
    {
        lock (_encGate)
        {
            if (_encoding) return true;
            if (TapTranscoder && _trans is not null) { _trans.Engine.Tap = null; TapTranscoder = false; }
            _y = new byte[Width * Height]; _uv = new byte[Width * Height / 2];
            _l = new float[48000 / Fps]; _r = new float[48000 / Fps];
            if (!_enc.Open(new DiscardSeekableStream(), Width, Height, Fps, Kbps, true, AllowMediaFoundation)) { LastAction = "⚠ 인코더 열기 실패: " + _enc.FallbackReason; Raise(); return false; }
            _encoding = true; Interlocked.Exchange(ref _encFrames, 0);
            _encLoop = new Thread(EncodeLoop) { IsBackground = true, Name = "PF.GatewayEncoder" };
            _encLoop.Start();
        }
        LastAction = "🎥 송출 인코더 · " + RealtimeHardwareVerticalTranscoderEngine.BackendName(_enc.Backend) + (_enc.FallbackReason is { } f ? " (" + f + ")" : "");
        Raise();
        return true;
    }

    public void StopEncoder()
    {
        Thread? t;
        lock (_encGate) { if (!_encoding) return; _encoding = false; t = _encLoop; _encLoop = null; }
        t?.Join(3000);
        lock (_encGate) _enc.Close();
        LastAction = "⏹ 송출 인코더 정지";
        Raise();
    }

    /// <summary>송출 프레임 1 개 합성 · 인코딩(테스트 · 인코더 스레드).</summary>
    public void EncodeOne()
    {
        lock (_encGate)
        {
            if (!_enc.IsOpen) return;
            var f = Interlocked.Increment(ref _encFrames);
            Pattern(f);
            _enc.EncodeFrame(_y, _uv, _l, _r);
            if (f % 6 == 1) Preview();
        }
    }

    private void Pattern(long f)
    {
        int w = Width, h = Height;
        var y = _y.AsSpan();
        // 8 컬러 바(세로 띠) + 이동 그라데이션 + 이동 박스 · 하단 채팅 속도 막대
        var shift = (int)(f * 6 % h);
        for (var row = 0; row < h; row++)
        {
            var line = y.Slice(row * w, w);
            var band = (byte)(40 + ((row + shift) * 160 / h));
            line[..(w / 2)].Fill(band);
            line[(w / 2)..].Fill((byte)(255 - band));
        }
        var bx = (int)((f * 9) % Math.Max(1, w - w / 5)); var by = h / 3; var bs = w / 5;
        for (var row = by; row < Math.Min(h, by + bs); row++) y.Slice(row * w + bx, bs).Fill(235);
        var bar = Math.Clamp((int)(w * Math.Min(1f, Guard.RatePerSec / 1000f)), 1, w);
        for (var row = h - h / 24; row < h; row++) y.Slice(row * w, bar).Fill(220);
        var uv = _uv.AsSpan();
        var hue = (byte)(128 + 60 * Math.Sin(f * 0.02));
        for (var row = 0; row < h / 2; row++)
        {
            var line = uv.Slice(row * w, w);
            for (var x = 0; x < w; x += 2) { line[x] = x < w / 2 ? hue : (byte)(256 - hue); line[x + 1] = (byte)(row < h / 4 ? 150 : 110); }
        }
        var dphi = 2 * Math.PI * 440 / 48000.0;
        for (var i = 0; i < _l.Length; i++) { _phase += dphi; if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI; var v = 0.05f * (float)Math.Sin(_phase); _l[i] = v; _r[i] = v; }
    }

    private void Preview()
    {
        lock (_prevGate)
        {
            var d = _preview;
            d.AsSpan(0, 54).Clear();
            d[0] = (byte)'B'; d[1] = (byte)'M';
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(2), PreviewBytes);
            d[10] = 54; d[14] = 40;
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(18), PreviewW);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(22), PreviewH);
            d[26] = 1; d[28] = 24;
            int w = Width, h = Height;
            for (var py = 0; py < PreviewH; py++)
            {
                var sy = py * h / PreviewH; var o = 54 + (PreviewH - 1 - py) * PreviewW * 3;
                for (var px = 0; px < PreviewW; px++)
                {
                    var sx = px * w / PreviewW;
                    int Y = _y[sy * w + sx] - 16, U = _uv[(sy >> 1) * w + (sx & ~1)] - 128, V = _uv[(sy >> 1) * w + (sx & ~1) + 1] - 128;
                    d[o + px * 3] = (byte)Math.Clamp((298 * Y + 516 * U + 128) >> 8, 0, 255);
                    d[o + px * 3 + 1] = (byte)Math.Clamp((298 * Y - 100 * U - 208 * V + 128) >> 8, 0, 255);
                    d[o + px * 3 + 2] = (byte)Math.Clamp((298 * Y + 409 * V + 128) >> 8, 0, 255);
                }
            }
        }
    }

    public void WritePreviewBmp(byte[] dst) { lock (_prevGate) _preview.AsSpan(0, Math.Min(dst.Length, PreviewBytes)).CopyTo(dst); }

    // ══════════════════════ 클립(클립 스레드 · 비핫패스 머티리얼라이즈) ══════════════════════
    public void RequestClip(double seconds) { _clipAuto = false; _clipReq = seconds; _clipEvt.Set(); }

    /// <summary>즉시 무손실 클립(호출 스레드) — 리먹스 ≤ 10 ms · 이후 세그먼트 스트림 복사.</summary>
    public LosslessHighlightClip? ClipNow(double seconds, bool auto = false)
    {
        LosslessHighlightClip clip;
        lock (_clipGate)
        {
            var n = Ring.Extract(seconds);
            if (n == 0) { LastAction = "⚠ 클립 실패: " + Ring.LastFailure; Raise(); return null; }
            var media = new SegmentedChunkStream();
            media.Write(Ring.LastClip);
            media.Position = 0;
            clip = new LosslessHighlightClip
            {
                Id = ++_clipId, Width = Ring.Width, Height = Ring.Height, Fps = Ring.Fps, Frames = Ring.LastClipFrames, AudioFrames = Ring.LastClipAudio, Fragments = Ring.LastClipFragments,
                StartIndex = Ring.LastClipStart, DurationSec = Ring.LastClipSec, RemuxMs = Ring.LastRemuxMs, Auto = auto, CreatedUtc = DateTime.UtcNow, Media = media,
            };
            lock (_clips)
            {
                if (_clips.Count >= MaxClips) { _clips[^1].Dispose(); _clips.RemoveAt(_clips.Count - 1); }
                _clips.Insert(0, clip);
            }
        }
        LastAction = (auto ? "🔥 스파이크 자동 클립 #" : "✂ 무손실 클립 #") + clip.Id.ToString(CultureInfo.InvariantCulture) + " · " + clip.Width.ToString(CultureInfo.InvariantCulture) + "×" + clip.Height.ToString(CultureInfo.InvariantCulture)
            + " · " + clip.DurationSec.ToString("0.0", CultureInfo.InvariantCulture) + " s · 리먹스 " + clip.RemuxMs.ToString("0.00", CultureInfo.InvariantCulture) + " ms";
        if (AutoPublish) PublishClip(clip.Id);
        Raise();
        return clip;
    }

    public bool PublishClip(int id)
    {
        var c = FindClip(id);
        if (c?.Media is not { } m || _omni is null) { LastAction = _omni is null ? "⚠ v17.51 옴니채널 미연동" : "⚠ 클립 없음"; Raise(); return false; }
        var copy = new SegmentedChunkStream();
        var buf = new byte[81920]; long pos = 0; int k;
        while ((k = m.ReadAt(pos, buf)) > 0) { copy.Write(buf.AsSpan(0, k)); pos += k; }
        copy.Position = 0;
        var ok = _omni.PublishMaster(copy);
        if (ok) { c.Published = true; Published++; }
        LastAction = ok ? "🚀 무손실 클립 #" + id.ToString(CultureInfo.InvariantCulture) + " → Shorts/Reels/TikTok" : "⚠ 배포 실패: " + _omni.LastAction;
        Raise();
        return ok;
    }

    public string? SaveClip(int id, string? path = null)
    {
        var c = FindClip(id);
        if (c?.Media is not { } m) return null;
        try
        {
            path ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "PromptFlow", "LosslessClips", "lossless_" + c.CreatedUtc.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + id.ToString(CultureInfo.InvariantCulture) + ".mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var fs = File.Create(path);
            var buf = new byte[81920]; long pos = 0; int k;
            while ((k = m.ReadAt(pos, buf)) > 0) { fs.Write(buf, 0, k); pos += k; }
            LastAction = "💾 " + path; Raise();
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            PromptFlowStudio.Program.Log("Gateway.SaveClip", ex);
            LastAction = "⚠ 저장 실패: " + ex.Message; Raise();
            return null;
        }
    }

    public void RemoveClip(int id) { lock (_clips) for (var i = 0; i < _clips.Count; i++) if (_clips[i].Id == id) { _clips[i].Dispose(); _clips.RemoveAt(i); break; } Raise(); }

    // ══════════════════════ 60 Hz 틱(무할당) ══════════════════════
    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("Gateway.Step");
            Compute();
        }
    }

    private void Compute()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _tick);
        var g = Clock();
        if (!(LinkInfluencer && _influencer is { IsRunning: true }))
        {
            Guard.Drain(_own, g, DrainMax, DrainBudgetUs);
            Chat.Dispatch(NowMs, null);
        }
        // 1 s 롤 — 세이프티 통과 채팅 속도 · 스파이크
        if (_secStart < 0) { _secStart = g; _lastClean = Guard.DeliveredClean; }
        if (g - _secStart >= 1000)
        {
            _secStart += 1000; if (g - _secStart > 3000) _secStart = g;
            var clean = (float)(Guard.DeliveredClean - _lastClean); _lastClean = Guard.DeliveredClean;
            CleanPerSec = clean;
            _cleanS[_cHead] = clean; _cHead = (_cHead + 1) % _cleanS.Length; if (_cN < _cleanS.Length) _cN++;
            var level = clean / MathF.Max(_baseClean, SpikeFloor);
            SpikeLevel = level;
            if (_cN <= WarmupSec) _baseClean += (clean - _baseClean) / _cN;
            else if (level < 1.5f && !Guard.Shedding) _baseClean += (clean - _baseClean) * (1f - MathF.Exp(-1f / 60f));
            if (_cN > WarmupSec && level >= SpikeRatio && !Guard.Shedding && g >= _cooldownUntil)
            {
                Spikes++; _cooldownUntil = g + CooldownMs;
                if (AutoClip) { _clipAuto = true; _clipReq = ClipSeconds; _clipEvt.Set(); }
            }
        }
        var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        LastTickMs = ms; if (ms > MaxTickMs && _tick > 5) MaxTickMs = ms;
        if (ms > 1000.0 / Hz) FramesOverBudget++;
        var drops = Guard.TailDropped + Guard.ShedDropped + Guard.StaleDropped;
        _msS[_sHead] = (float)ms; _rateS[_sHead] = Guard.RatePerSec; _dropS[_sHead] = drops - _lastDrops; _lastDrops = drops;
        _sHead = (_sHead + 1) % Hist; if (_sN < Hist) _sN++;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.Gateway" };
            _clipLoop = new Thread(ClipLoop) { IsBackground = true, Name = "PF.GatewayClip", Priority = ThreadPriority.BelowNormal };
            _loop.Start(); _clipLoop.Start();
        }
        LastAction = "▶ 게이트웨이 60 Hz · 배압 소비 · 스파이크 감시";
        Raise();
    }

    public void Stop()
    {
        Thread? t, c;
        lock (_gate) { if (!_running) return; _running = false; t = _loop; c = _clipLoop; _loop = _clipLoop = null; }
        _clipEvt.Set();
        t?.Join(3000); c?.Join(3000);
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("Gateway.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }
            if (n % 6 == 0) Raise();
        }
    }

    private void EncodeLoop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long n = 0;
        while (_encoding)
        {
            try { EncodeOne(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or System.Runtime.InteropServices.COMException) { PromptFlowStudio.Program.Log("Gateway.Encode", ex); }
            n++;
            var wait = n * 1000.0 / Fps - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }
        }
    }

    private void ClipLoop()
    {
        while (_running)
        {
            _clipEvt.WaitOne(250);
            var s = _clipReq;
            if (s <= 0) continue;
            _clipReq = 0;
            try { ClipNow(s, _clipAuto); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException) { PromptFlowStudio.Program.Log("Gateway.Clip", ex); }
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Gateway.OnChange", ex); }
    }

    public void Dispose()
    {
        Stop();
        StopEncoder();
        Gateway.Dispose();
        if (_influencer is not null && ReferenceEquals(_influencer.ExternalIngress, Guard)) _influencer.ExternalIngress = null;
        if (_trans is not null && ReferenceEquals(_trans.Engine.Tap, Ring)) _trans.Engine.Tap = null;
        _enc.Dispose();
        lock (_clips) { foreach (var c in _clips) c.Dispose(); _clips.Clear(); }
        _clipEvt.Dispose();
    }
}
