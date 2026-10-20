using PromptFlowStudio.Services.MotionTypography;
using PromptFlowStudio.Services.VerticalTranscode;
namespace PromptFlowStudio.Services.InfluencerLive;

public enum ClipJobState : byte { Idle, Armed, Encoding }

/// <summary>v17.61.0 자동 생성 숏폼 클립(9:16 fMP4 · 4 MB 세그먼트 스트림 소유).</summary>
public sealed class LiveHighlightClip : IDisposable
{
    public int Id { get; init; }
    public long StartMs { get; init; }
    public long EndMs { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Frames { get; init; }
    public double DurationSec { get; init; }
    public float ChatRatio { get; init; }
    public float RmsRatio { get; init; }
    public float LikeRatio { get; init; }
    public float Score { get; init; }
    public string Caption { get; init; } = "";
    public TranscodeBackend Backend { get; init; }
    public DateTime CreatedUtc { get; init; }
    public SegmentedChunkStream? Media { get; internal set; }
    public long Bytes => Media?.Length ?? 0;
    public bool Published { get; internal set; }
    public void Dispose() { Media?.Dispose(); Media = null; }
}

/// <summary>
/// [PromptFlow Studio v17.61.0] ✂️ 라이브 인게이지먼트 스파이크 감지 &amp; 즉시 9:16 클리핑 합성기(<see cref="Tick"/> · <see cref="PushFrameRgb"/> · <see cref="EncodeStep"/> 무할당).
///   ▸ 계측: 1 s 버킷 링(120) — 채팅 속도(최근 10 s × 6 = 분당) · 좋아요 속도 · 음성 RMS(최근 3 s). 평소치 = τ 60 s EWMA(급증 구간은 갱신 동결 · 워밍업 20 s 단순 평균).
///   ▸ 감지: 채팅 속도 ≥ 평소 250 % 그리고 RMS ≥ 평소 250 % → 무장(Armed) · 3 s 포스트롤 후 클립 확정. 급증 시작(150 %) 기준 리드인 8 s → 길이 15~30 s.
///     세이프티 가드 레이드 중에는 감지 억제 · 해당 프레임은 오염 표시 → 클립 시작점은 마지막 오염 프레임 이후(15 s 미만이면 거부). 클립 간 쿨다운 20 s.
///   ▸ 롤링 버퍼: 36 s × 30 fps 프록시 NV12(108×192 · 9:16 피사체 추적 크롭) + 48 kHz 모노 링. 확정 시 클립 구간을 작업 버퍼로 스냅숏(이후 캡처와 독립).
///   ▸ 합성: 작업 버퍼 → 쌍선형 업스케일(기본 1080×1920) → v17.52 단어별 모션 자막 + 하이라이트 배지/진행 바(RGBA → NV12) → v17.49 트랜스코더(MF 하드웨어 → 관리 코드 폴백) fMP4
///     → <see cref="OnClip"/> 로 v17.51 옴니채널 배포 큐에 바이너리 직결.
/// </summary>
public sealed class LiveEngagementHighlightClippingSynthesizer : IDisposable
{
    public const int ProxyW = 108, ProxyH = 192, ProxyY = ProxyW * ProxyH, ProxyBytes = ProxyY * 3 / 2, CaptureFps = 30, RingSec = 36, RingFrames = RingSec * CaptureFps;
    public const int AudioRate = 48000, AudioRing = RingSec * AudioRate, Spf = AudioRate / CaptureFps, Buckets = 120, ShortChatSec = 10, ShortRmsSec = 3;
    public const int MinClipSec = 15, MaxClipSec = 30, PostRollMs = 3000, CooldownMs = 20_000, WarmupSec = 20, LeadInSec = 8, MaxClips = 12;
    public const float SpikeRatio = 2.5f, OnsetRatio = 1.5f, BaselineTauSec = 60f, ChatFloor = 6f, LikeFloor = 6f, RmsFloor = 0.01f;
    public const string DefaultCaption = "오늘 방송 최고의 순간! 이 반응 실화냐";
    public const int CaptionCap = 160;

    private readonly object _ring = new();
    private readonly int[] _chatB = new int[Buckets], _likeB = new int[Buckets];
    private readonly float[] _rmsB = new float[Buckets];
    private readonly float[] _velS = new float[Buckets], _rmsS = new float[Buckets], _baseS = new float[Buckets];
    private readonly List<LiveHighlightClip> _clips = new(MaxClips);
    private byte[]? _frames, _job;
    private float[]? _audio, _jobAudio;
    private long[]? _frameMs, _framePos;
    private bool[]? _taint;
    private long _frameSeq, _audioPos, _lastTaintSeq = -1, _nextCaptureMs = long.MinValue, _bucketStartMs = long.MinValue, _onsetMs = -1, _armMs, _cooldownUntil = long.MinValue;
    private int _bHead, _bN, _curChat, _curLikes, _curN;
    private double _curSq;
    private float _baseChat, _baseLike, _baseRms;
    private float _peakChat, _peakRms, _peakLike;
    // 작업
    private int _jobFrames, _jobDone, _clipId;
    private long _jobStartMs, _jobEndMs;
    private float _jobScore, _jobChat, _jobRms, _jobLike;
    private readonly char[] _cap = new char[CaptionCap], _jobCap = new char[CaptionCap];
    private int _capLen, _jobCapLen;
    private volatile ClipJobState _state;
    // 출력
    private readonly RealtimeHardwareVerticalTranscoderEngine _enc = new();
    private readonly byte[] _px = new byte[ProxyBytes];
    private byte[] _y = Array.Empty<byte>(), _uv = Array.Empty<byte>();
    private readonly float[] _l = new float[Spf], _r = new float[Spf];
    private int[] _xm = Array.Empty<int>(), _ym = Array.Empty<int>();
    private ushort[] _xw = Array.Empty<ushort>(), _yw = Array.Empty<ushort>();
    private RgbaOverlayLayer _layer = new(1, 1);
    private CrossPlatformUiSafeZoneGuard _safe = new();

    public LiveEngagementHighlightClippingSynthesizer()
    {
        Atlas = new SdfGlyphAtlas();
        Typo = new RealtimeWordByWordMotionTypographyEngine(Atlas) { Preset = TypoPreset.Pop };
        Configure(1080, 1920, 6000, true, true);
        SetCaption(DefaultCaption);
    }

    public event Action<LiveHighlightClip>? OnClip;
    public SdfGlyphAtlas Atlas { get; }
    public RealtimeWordByWordMotionTypographyEngine Typo { get; }
    public RealtimeHardwareVerticalTranscoderEngine Encoder => _enc;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Kbps { get; private set; }
    public bool AllowMediaFoundation { get; private set; }
    public bool PreferHardware { get; private set; }
    public bool Enabled { get; set; } = true;
    public ClipJobState State => _state;
    public string Caption => new(_cap, 0, _capLen);
    public float ChatVelocity { get; private set; }
    public float LikeVelocity { get; private set; }
    public float ShortRms { get; private set; }
    public float BaselineChat => _baseChat;
    public float BaselineRms => _baseRms;
    public float BaselineLike => _baseLike;
    public float ChatRatio { get; private set; }
    public float RmsRatio { get; private set; }
    public float LikeRatio { get; private set; }
    public bool Warm => _bN >= WarmupSec;
    public long FramesCaptured => _frameSeq;
    public long TaintedFrames { get; private set; }
    public long Spikes { get; private set; }
    public long SuppressedByRaid { get; private set; }
    public long Rejected { get; private set; }
    public long ClipsMade { get; private set; }
    public long Failures { get; private set; }
    public double Progress => _jobFrames > 0 ? (double)_jobDone / _jobFrames : 0;
    public double LastStepMs { get; private set; }
    public double LastSnapshotMs { get; private set; }
    public string? LastEvent { get; private set; }
    public int ClipCount { get { lock (_clips) return _clips.Count; } }
    public LiveHighlightClip? Clip(int i) { lock (_clips) return (uint)i < (uint)_clips.Count ? _clips[i] : null; }
    public LiveHighlightClip? FindClip(int id) { lock (_clips) { foreach (var c in _clips) if (c.Id == id) return c; return null; } }
    public int SeriesCount => _bN < Buckets ? _bN : Buckets;
    private int Bx(int i) => (_bHead - 1 - i + 2 * Buckets) % Buckets;
    public float VelocitySeries(int i) => _velS[Bx(i)];
    public float RmsSeries(int i) => _rmsS[Bx(i)];
    public float BaselineSeries(int i) => _baseS[Bx(i)];

    // ══════════════════════ 설정(비핫패스) ══════════════════════
    public bool Configure(int width, int height, int kbps, bool allowMf, bool preferHw)
    {
        if (_state == ClipJobState.Encoding) return false;
        width = Math.Max(16, width & ~1); height = Math.Max(16, height & ~1);
        if (width != Width || height != Height)
        {
            Width = width; Height = height;
            _y = new byte[width * height]; _uv = new byte[width * height / 2];
            (_xm, _xw) = Map(width, ProxyW); (_ym, _yw) = Map(height, ProxyH);
            _layer = new RgbaOverlayLayer(width, height);
            _safe = new CrossPlatformUiSafeZoneGuard(width, height, CrossPlatformUiSafeZoneGuard.All);
            Typo.FontPx = 92f * width / 1080f;
        }
        Kbps = Math.Clamp(kbps, 500, LocalHttpsWebhookBitrateBudgetGuard.CapKbps);
        AllowMediaFoundation = allowMf; PreferHardware = preferHw;
        return true;
    }

    private static (int[], ushort[]) Map(int dst, int src)
    {
        var m = new int[dst]; var w = new ushort[dst];
        for (var i = 0; i < dst; i++)
        {
            var f = Math.Clamp((i + 0.5f) * src / dst - 0.5f, 0f, src - 1.001f);
            m[i] = (int)f; w[i] = (ushort)((f - (int)f) * 256f);
        }
        return (m, w);
    }

    /// <summary>다음 클립 자막(유해 판정 통과 문장만 · 무할당 복사).</summary>
    public void SetCaption(ReadOnlySpan<char> text)
    {
        text = text.Trim();
        if (text.IsEmpty) return;
        lock (_ring) { _capLen = Math.Min(text.Length, CaptionCap); text[.._capLen].CopyTo(_cap); }
    }

    private void EnsureRing()
    {
        if (_frames is not null) return;
        _frames = new byte[RingFrames * ProxyBytes];
        _job = new byte[MaxClipSec * CaptureFps * ProxyBytes];
        _audio = new float[AudioRing];
        _jobAudio = new float[(MaxClipSec * CaptureFps + 1) * Spf];
        _frameMs = new long[RingFrames]; _framePos = new long[RingFrames]; _taint = new bool[RingFrames];
    }

    /// <summary>롤링 버퍼 사전 할당(비핫패스).</summary>
    public void Prepare() { lock (_ring) EnsureRing(); }

    public void Reset()
    {
        lock (_ring)
        {
            _frameSeq = 0; _audioPos = 0; _lastTaintSeq = -1; _nextCaptureMs = _bucketStartMs = _cooldownUntil = long.MinValue; _onsetMs = -1;
            _bHead = _bN = _curChat = _curLikes = _curN = 0; _curSq = 0; _baseChat = _baseLike = _baseRms = 0;
            ChatVelocity = LikeVelocity = ShortRms = ChatRatio = RmsRatio = LikeRatio = 0;
            Spikes = SuppressedByRaid = Rejected = TaintedFrames = 0;
            if (_state == ClipJobState.Armed) _state = ClipJobState.Idle;
        }
    }

    // ══════════════════════ 입력(무할당) ══════════════════════
    public void OnChat(int n = 1) => _curChat += n;
    public void OnLikes(int n) => _curLikes += n;

    public void PushAudio(ReadOnlySpan<float> mono)
    {
        lock (_ring)
        {
            EnsureRing();
            var a = _audio!;
            var p = (int)(_audioPos % AudioRing);
            foreach (var s0 in mono)
            {
                var s = float.IsFinite(s0) ? s0 : 0f;
                a[p] = s; _curSq += s * s; _curN++;
                if (++p == AudioRing) p = 0;
            }
            _audioPos += mono.Length;
        }
    }

    /// <summary>캡처 시각 도래 여부(30 fps).</summary>
    public bool CaptureDue(long nowMs) => nowMs >= _nextCaptureMs;

    /// <summary>프록시 NV12(108×192) 프레임 직접 공급.</summary>
    public void PushFrameNv12(ReadOnlySpan<byte> y, ReadOnlySpan<byte> uv, long nowMs, bool tainted)
    {
        lock (_ring)
        {
            var dst = BeginFrame(nowMs, tainted);
            y[..ProxyY].CopyTo(dst); uv[..(ProxyY / 2)].CopyTo(dst[ProxyY..]);
        }
    }

    /// <summary>v17.55 래스터라이저 선형 RGB(+투과율 배경) → 피사체 X 추적 9:16 크롭 → 톤 → BT.601 NV12 프록시.</summary>
    public void PushFrameRgb(ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b, ReadOnlySpan<float> trans, System.Numerics.Vector3 background, int srcW, int srcH, float centerX, long nowMs, bool tainted)
    {
        lock (_ring)
        {
            var dst = BeginFrame(nowMs, tainted);
            var cropW = Math.Min(srcW, srcH * 9 / 16);
            var x0 = Math.Clamp((int)(centerX - cropW * 0.5f), 0, srcW - cropW);
            var hasT = trans.Length >= srcW * srcH;
            for (var y = 0; y < ProxyH; y++)
            {
                var sy = Math.Min(srcH - 1, y * srcH / ProxyH);
                for (var x = 0; x < ProxyW; x++)
                {
                    var p = sy * srcW + x0 + Math.Min(cropW - 1, x * cropW / ProxyW);
                    var t = hasT ? trans[p] : 0f;
                    int R = Tone(r[p] + t * background.X), G = Tone(g[p] + t * background.Y), B = Tone(b[p] + t * background.Z);
                    dst[y * ProxyW + x] = (byte)Math.Clamp((66 * R + 129 * G + 25 * B + 128 >> 8) + 16, 0, 255);
                    if ((x & 1) == 0 && (y & 1) == 0)
                    {
                        var o = ProxyY + (y >> 1) * ProxyW + x;
                        dst[o] = (byte)Math.Clamp((-38 * R - 74 * G + 112 * B + 128 >> 8) + 128, 0, 255);
                        dst[o + 1] = (byte)Math.Clamp((112 * R - 94 * G - 18 * B + 128 >> 8) + 128, 0, 255);
                    }
                }
            }
        }
    }

    private static int Tone(float v) { v = MathF.Max(v, 0f); return Math.Clamp((int)(MathF.Sqrt(v / (1f + v * 0.1f)) * 255f + 0.5f), 0, 255); }

    private Span<byte> BeginFrame(long nowMs, bool tainted)
    {
        EnsureRing();
        var s = (int)(_frameSeq % RingFrames);
        _frameMs![s] = nowMs; _framePos![s] = _audioPos; _taint![s] = tainted;
        if (tainted) { _lastTaintSeq = _frameSeq; TaintedFrames++; }
        _frameSeq++;
        _nextCaptureMs = (_nextCaptureMs == long.MinValue || nowMs - _nextCaptureMs > 200 ? nowMs : _nextCaptureMs) + 1000 / CaptureFps;
        return _frames.AsSpan(s * ProxyBytes, ProxyBytes);
    }

    // ══════════════════════ 감지(무할당 · 스냅숏 시 34 MB memcpy) ══════════════════════
    /// <summary>틱 — 1 s 버킷 롤 · 비율 · 무장/확정. 반환 = 이번 틱에 클립 작업 시작.</summary>
    public bool Tick(long nowMs, bool raidActive)
    {
        if (_bucketStartMs == long.MinValue) _bucketStartMs = nowMs;
        var started = false;
        while (nowMs - _bucketStartMs >= 1000)
        {
            _bucketStartMs += 1000;
            RollBucket(raidActive);
        }
        if (!Enabled) return false;
        if (_state == ClipJobState.Armed)
        {
            _peakChat = MathF.Max(_peakChat, ChatRatio); _peakRms = MathF.Max(_peakRms, RmsRatio); _peakLike = MathF.Max(_peakLike, LikeRatio);
            if (nowMs - _armMs >= PostRollMs) started = Commit(nowMs);
        }
        return started;
    }

    private void RollBucket(bool raid)
    {
        var i = _bHead;
        _chatB[i] = _curChat; _likeB[i] = _curLikes; _rmsB[i] = _curN > 0 ? (float)Math.Sqrt(_curSq / _curN) : 0f;
        _curChat = _curLikes = 0; _curSq = 0; _curN = 0;
        _bHead = (_bHead + 1) % Buckets; _bN++;
        int chat = 0, likes = 0; double sq = 0;
        var nc = Math.Min(_bN, ShortChatSec);
        for (var k = 0; k < nc; k++) { var j = (_bHead - 1 - k + Buckets) % Buckets; chat += _chatB[j]; likes += _likeB[j]; }
        var nr = Math.Min(_bN, ShortRmsSec);
        for (var k = 0; k < nr; k++) { var v = _rmsB[(_bHead - 1 - k + Buckets) % Buckets]; sq += v * v; }
        ChatVelocity = chat * 60f / nc; LikeVelocity = likes * 60f / nc; ShortRms = (float)Math.Sqrt(sq / nr);
        // 평소치(EWMA · 워밍업 평균 · 급증 동결)
        var perMinChat = _chatB[i] * 60f; var perMinLike = _likeB[i] * 60f; var rms = _rmsB[i];
        if (_bN <= WarmupSec)
        {
            var w = 1f / _bN;
            _baseChat += (perMinChat - _baseChat) * w; _baseLike += (perMinLike - _baseLike) * w; _baseRms += (rms - _baseRms) * w;
        }
        ChatRatio = ChatVelocity / MathF.Max(_baseChat, ChatFloor);
        LikeRatio = LikeVelocity / MathF.Max(_baseLike, LikeFloor);
        RmsRatio = ShortRms / MathF.Max(_baseRms, RmsFloor);
        var surging = ChatRatio >= OnsetRatio || RmsRatio >= OnsetRatio;
        if (_bN > WarmupSec && !surging && !raid)
        {
            var a = 1f - MathF.Exp(-1f / BaselineTauSec);
            _baseChat += (perMinChat - _baseChat) * a; _baseLike += (perMinLike - _baseLike) * a; _baseRms += (rms - _baseRms) * a;
        }
        if (ChatRatio >= OnsetRatio) { if (_onsetMs < 0) _onsetMs = _bucketStartMs - 1000L * ShortChatSec / 2; } else if (_state != ClipJobState.Armed) _onsetMs = -1;
        _velS[i] = ChatVelocity; _rmsS[i] = ShortRms; _baseS[i] = _baseChat;
        // 스파이크
        if (!Enabled || _state != ClipJobState.Idle || !Warm || _bucketStartMs < _cooldownUntil) return;
        if (ChatRatio >= SpikeRatio && RmsRatio >= SpikeRatio)
        {
            if (raid) { SuppressedByRaid++; return; }
            Spikes++;
            _state = ClipJobState.Armed; _armMs = _bucketStartMs;
            _peakChat = ChatRatio; _peakRms = RmsRatio; _peakLike = LikeRatio;
            if (_onsetMs < 0) _onsetMs = _bucketStartMs;
            LastEvent = "🔥 스파이크 감지 — 포스트롤 3 s";
        }
    }

    private bool Commit(long nowMs)
    {
        lock (_ring)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _cooldownUntil = nowMs + CooldownMs;
            var end = _frameSeq;                         // 배타
            var wantSec = Math.Clamp((nowMs - _onsetMs) / 1000.0 + LeadInSec, MinClipSec, MaxClipSec);
            var start = end - (long)Math.Round(wantSec * CaptureFps);
            var oldest = Math.Max(0, end - RingFrames + CaptureFps);
            start = Math.Max(start, Math.Max(oldest, _lastTaintSeq + 1));
            var n = (int)(end - start);
            _onsetMs = -1;
            if (n < MinClipSec * CaptureFps || _frames is null)
            {
                Rejected++; _state = ClipJobState.Idle;
                LastEvent = _lastTaintSeq >= end - MinClipSec * CaptureFps ? "🛡 레이드 오염 구간 — 클립 거부" : "⚠ 버퍼 부족 — 클립 거부";
                return false;
            }
            for (var k = 0; k < n; k++) _frames.AsSpan((int)((start + k) % RingFrames) * ProxyBytes, ProxyBytes).CopyTo(_job.AsSpan(k * ProxyBytes));
            var a0 = _framePos![(int)(start % RingFrames)];
            var need = n * Spf;
            var avail = (int)Math.Clamp(_audioPos - a0, 0, Math.Min(need, AudioRing));
            for (var k = 0; k < avail; k++) _jobAudio![k] = _audio![(int)((a0 + k) % AudioRing)];
            _jobAudio.AsSpan(avail, need - avail).Clear();
            _jobFrames = n; _jobDone = 0;
            _jobStartMs = _frameMs![(int)(start % RingFrames)]; _jobEndMs = _frameMs[(int)((end - 1) % RingFrames)];
            _jobChat = _peakChat; _jobRms = _peakRms; _jobLike = _peakLike;
            _jobScore = (MathF.Min(_peakChat, 10f) + MathF.Min(_peakRms, 10f) + MathF.Min(_peakLike, 10f) * 0.5f) / 2.5f * 10f;
            _cap.AsSpan(0, _capLen).CopyTo(_jobCap); _jobCapLen = _capLen;
            LastSnapshotMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _state = ClipJobState.Encoding;
            LastEvent = "✂ 클립 확정 " + (n / (double)CaptureFps).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s — 9:16 합성 시작";
            return true;
        }
    }

    /// <summary>테스트 · 수동 — 현재 버퍼 최근 seconds 를 즉시 클립 확정.</summary>
    public bool ClipNow(long nowMs, double seconds)
    {
        if (_state != ClipJobState.Idle) return false;
        _onsetMs = nowMs - (long)((Math.Clamp(seconds, MinClipSec, MaxClipSec) - LeadInSec) * 1000);
        _peakChat = ChatRatio; _peakRms = RmsRatio; _peakLike = LikeRatio;
        _state = ClipJobState.Armed;
        return Commit(nowMs);
    }

    // ══════════════════════ 합성(전용 스레드 · 프레임 무할당) ══════════════════════
    /// <summary>작업 버퍼 프레임 최대 maxFrames 개 합성 · 인코딩. 완료 시 클립 등록 · <see cref="OnClip"/>.</summary>
    public int EncodeStep(int maxFrames)
    {
        if (_state != ClipJobState.Encoding) return 0;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_jobDone == 0 && !_enc.IsOpen && !OpenJob()) return 0;
        var done = 0;
        while (done < maxFrames && _jobDone < _jobFrames)
        {
            _job.AsSpan(_jobDone * ProxyBytes, ProxyBytes).CopyTo(_px);
            Upscale();
            RenderOverlay(_jobDone);
            _layer.CompositeNv12(_y, _uv, Width);
            _jobAudio.AsSpan(_jobDone * Spf, Spf).CopyTo(_l);
            _l.CopyTo(_r.AsSpan());
            _enc.EncodeFrame(_y, _uv, _l, _r);
            _jobDone++; done++;
        }
        LastStepMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (_jobDone >= _jobFrames) FinishJob();
        return done;
    }

    private bool OpenJob()
    {
        if (!_enc.Open(new SegmentedChunkStream(), Width, Height, CaptureFps, Kbps, PreferHardware, AllowMediaFoundation))
        {
            Failures++; _state = ClipJobState.Idle; LastEvent = "⚠ 인코더 열기 실패: " + _enc.FallbackReason;
            return false;
        }
        Typo.SetCaption(_jobCap.AsSpan(0, _jobCapLen), TypoLang.Ko, 300f, 60f);
        Typo.Layout((_safe.Safe.W - 2 * RealtimeWordByWordMotionTypographyEngine.OutlineEm * Typo.FontPx) / Typo.FontPx);
        Atlas.Ensure("LIVE HIGHLIGHT");
        return true;
    }

    private void Upscale()
    {
        int w = Width, h = Height;
        var px = _px; var y = _y; var uv = _uv;
        for (var oy = 0; oy < h; oy++)
        {
            int sy = _ym[oy], wy = _yw[oy], r0 = sy * ProxyW, r1 = Math.Min(sy + 1, ProxyH - 1) * ProxyW, o = oy * w;
            for (var ox = 0; ox < w; ox++)
            {
                int sx = _xm[ox], wx = _xw[ox], sx1 = Math.Min(sx + 1, ProxyW - 1);
                var top = px[r0 + sx] * (256 - wx) + px[r0 + sx1] * wx;
                var bot = px[r1 + sx] * (256 - wx) + px[r1 + sx1] * wx;
                y[o + ox] = (byte)((top * (256 - wy) + bot * wy + 32768) >> 16);
            }
        }
        for (var oy = 0; oy < h / 2; oy++)
        {
            var sy = Math.Min(ProxyH / 2 - 1, _ym[oy * 2] >> 1);
            var src = ProxyY + sy * ProxyW; var o = oy * w;
            for (var ox = 0; ox < w; ox += 2)
            {
                var sx = Math.Min(ProxyW / 2 - 1, _xm[ox] >> 1) * 2;
                uv[o + ox] = px[src + sx]; uv[o + ox + 1] = px[src + sx + 1];
            }
        }
    }

    private void RenderOverlay(int frame)
    {
        _layer.Begin();
        var tMs = frame * 1000f / CaptureFps;
        var loop = Typo.TotalMs + 1500f;
        Typo.Render(loop > 0 ? tMs % loop : tMs, _layer, _safe);
        var s = _safe.Safe;
        var px = MathF.Max(14f, 40f * Width / 1080f);
        var badge = new PxRect(s.X, s.Y, (int)(px * 8.6f), (int)(px * 1.6f));
        _layer.FillRoundRect(in badge, px * 0.4f, 0.94f, 0.16f, 0.24f, 0.92f);
        SdfText.Draw(Atlas, _layer, "LIVE HIGHLIGHT", s.X + px * 0.5f, s.Y + px * 1.2f, px, 1f, 1f, 1f);
        var bh = Math.Max(4, Height / 240);
        var bar = new PxRect(0, Height - bh, Math.Max(1, (int)((long)Width * (frame + 1) / _jobFrames)), bh);
        _layer.FillRoundRect(in bar, 0f, 1f, 0.84f, 0f, 0.95f);
    }

    private void FinishJob()
    {
        var media = _enc.Finish() as SegmentedChunkStream;
        if (media is null || media.Length == 0)
        {
            media?.Dispose(); Failures++; _state = ClipJobState.Idle; LastEvent = "⚠ 클립 먹싱 실패";
            return;
        }
        var clip = new LiveHighlightClip
        {
            Id = ++_clipId, StartMs = _jobStartMs, EndMs = _jobEndMs, Width = Width, Height = Height, Frames = _jobFrames,
            DurationSec = _jobFrames / (double)CaptureFps, ChatRatio = _jobChat, RmsRatio = _jobRms, LikeRatio = _jobLike, Score = _jobScore,
            Caption = new string(_jobCap, 0, _jobCapLen), Backend = _enc.Backend, CreatedUtc = DateTime.UtcNow, Media = media,
        };
        lock (_clips)
        {
            if (_clips.Count >= MaxClips) { _clips[^1].Dispose(); _clips.RemoveAt(_clips.Count - 1); }
            _clips.Insert(0, clip);
        }
        ClipsMade++;
        _state = ClipJobState.Idle;
        LastEvent = "🎞 클립 #" + clip.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 생성 · " + (clip.Bytes / 1048576.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " MB";
        try { OnClip?.Invoke(clip); } catch (Exception ex) { PromptFlowStudio.Program.Log("Clip.OnClip", ex); }
    }

    /// <summary>진행 중 작업 취소.</summary>
    public void Cancel()
    {
        if (_state == ClipJobState.Encoding) { _enc.Finish()?.Dispose(); }
        _state = ClipJobState.Idle; _jobFrames = _jobDone = 0;
    }

    /// <summary>클립 바이너리 복제(배포 큐 소유권 이전용 · 비핫패스).</summary>
    public static SegmentedChunkStream? CloneMedia(LiveHighlightClip clip)
    {
        var src = clip.Media;
        if (src is null) return null;
        var dst = new SegmentedChunkStream();
        var buf = new byte[81920];
        long pos = 0; int k;
        while ((k = src.ReadAt(pos, buf)) > 0) { dst.Write(buf.AsSpan(0, k)); pos += k; }
        dst.Position = 0;
        return dst;
    }

    public bool RemoveClip(int id)
    {
        lock (_clips)
        {
            for (var i = 0; i < _clips.Count; i++) if (_clips[i].Id == id) { _clips[i].Dispose(); _clips.RemoveAt(i); return true; }
        }
        return false;
    }

    public void Dispose()
    {
        Cancel();
        lock (_clips) { foreach (var c in _clips) c.Dispose(); _clips.Clear(); }
        _enc.Dispose();
    }
}
