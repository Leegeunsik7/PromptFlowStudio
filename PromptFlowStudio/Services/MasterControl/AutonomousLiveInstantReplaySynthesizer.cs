namespace PromptFlowStudio.Services.MasterControl;

public enum ClipState : byte { Free, Recording, Ready, OnAir, Done }

public struct ReplayClip
{
    public int Id;
    public ArenaEventKind Kind;
    public int Channel;
    public long EventFrame, InFrame, OutFrame;
    public float Score, Speed;
    public ClipState State;
}

/// <summary>[PromptFlow Studio v17.05.0] SMPTE 12M 타임코드(60p NDF) — 무할당 서식/BCD(60p는 프레임 쌍 + 쌍 플래그 비트 7).</summary>
public static class SmpteTimecode
{
    public const int Length = 11;

    /// <summary>"HH:MM:SS:FF"(11자) 기록 · 반환 = 기록 길이.</summary>
    public static int Format(long frame, int fps, Span<char> dst)
    {
        if (dst.Length < Length) return 0;
        if (frame < 0) frame = 0;
        var ff = (int)(frame % fps); var ts = frame / fps;
        int ss = (int)(ts % 60), mm = (int)(ts / 60 % 60), hh = (int)(ts / 3600 % 24);
        Two(dst, 0, hh); dst[2] = ':'; Two(dst, 3, mm); dst[5] = ':'; Two(dst, 6, ss); dst[8] = ':'; Two(dst, 9, ff);
        return Length;
    }

    private static void Two(Span<char> d, int o, int v) { d[o] = (char)('0' + v / 10); d[o + 1] = (char)('0' + v % 10); }

    /// <summary>BCD 4 바이트(FF · SS · MM · HH).</summary>
    public static void ToBcd(long frame, int fps, Span<byte> bcd)
    {
        var ff = (int)(frame % fps); var ts = frame / fps;
        int ss = (int)(ts % 60), mm = (int)(ts / 60 % 60), hh = (int)(ts / 3600 % 24);
        var pair = fps > 30;
        var f = pair ? ff >> 1 : ff;
        bcd[0] = (byte)(f % 10 | f / 10 << 4 | (pair && (ff & 1) == 1 ? 0x80 : 0));
        bcd[1] = (byte)(ss % 10 | ss / 10 << 4);
        bcd[2] = (byte)(mm % 10 | mm / 10 << 4);
        bcd[3] = (byte)(hh % 10 | hh / 10 << 4);
    }

    public static long FromBcd(ReadOnlySpan<byte> bcd, int fps)
    {
        int f = (bcd[0] & 0xF) + (bcd[0] >> 4 & 0x3) * 10;
        if (fps > 30) f = f * 2 + (bcd[0] >> 7);
        int ss = (bcd[1] & 0xF) + (bcd[1] >> 4) * 10, mm = (bcd[2] & 0xF) + (bcd[2] >> 4) * 10, hh = (bcd[3] & 0xF) + (bcd[3] >> 4) * 10;
        return ((hh * 60L + mm) * 60 + ss) * fps + f;
    }
}

/// <summary>
/// [PromptFlow Studio v17.05.0] 🎬 AI 자율 라이브 인스턴트 리플레이 &amp; 슈퍼 슬로모션 합성기.
/// ISO 레코더 3 채널(CAM1 MoCo · DRN1 드론 · VOL1 볼류메트릭)을 핀 고정 순환 링버퍼(기본 240 프레임 = 4 초)에 상시 기록,
/// 경기 이벤트(골/충돌/결정적 찬스) 타임코드를 자동 감지해 프리롤/포스트롤 클립을 만들고(근접 이벤트 병합 · 채널 자동 선택),
/// 완성 클립을 송출 큐에 삽입. 재생은 블록 정합(8×8 · ±6 전역 탐색) 양방향 움직임 보상 보간 + 고정소수 쌍선형 표본 +
/// 가림(SAD 초과) 블록 선형 혼합 폴백으로 틱당 2 서브프레임(120 fps 출력 · 0.25× 시 유효 240 fps)을 합성. 전 경로 무할당.
/// </summary>
public sealed class AutonomousLiveInstantReplaySynthesizer
{
    public const int Channels = 3, MaxClips = 16, BlockSize = 8, SearchRange = 6, Hz = 60, SubFrames = 2;
    public const int OcclusionSad = 24;
    public static readonly int[] IsoInputs = { 0, 6, 10 };
    public static readonly string[] ChannelNames = { "ISO-A CAM1", "ISO-B DRN1", "ISO-C VOL1" };

    private readonly byte[] _ring, _lumaA, _lumaB, _sub;
    private readonly long[] _ringFrame;
    private readonly sbyte[] _mvx, _mvy;
    private readonly int[] _mvSad;
    private readonly ReplayClip[] _clips = new ReplayClip[MaxClips];
    private readonly int[] _queue = new int[MaxClips];
    private int _qHead, _qCount, _nextSlot, _nextId = 1, _onAir = -1, _meCh = -1;
    private long _meFrame = -1, _seenEvents;
    private double _play;

    public AutonomousLiveInstantReplaySynthesizer(int width = 128, int height = 72, int ringFrames = 240)
    {
        Width = width; Height = height; FrameBytes = width * height * 4; RingFrames = ringFrames;
        BlocksX = width / BlockSize; BlocksY = Math.Max(1, height / BlockSize);
        _ring = GC.AllocateArray<byte>(Channels * ringFrames * FrameBytes, pinned: true);
        _ringFrame = new long[Channels * ringFrames];
        Array.Fill(_ringFrame, -1L);
        _lumaA = new byte[width * height]; _lumaB = new byte[width * height];
        _sub = new byte[FrameBytes];
        _mvx = new sbyte[BlocksX * BlocksY]; _mvy = new sbyte[BlocksX * BlocksY]; _mvSad = new int[BlocksX * BlocksY];
    }

    public int Width { get; }
    public int Height { get; }
    public int FrameBytes { get; }
    public int RingFrames { get; }
    public int BlocksX { get; }
    public int BlocksY { get; }
    public int PreRollFrames { get; set; } = 90;
    public int PostRollFrames { get; set; } = 30;
    public float Speed { get; set; } = 0.25f;
    public float AutoThreshold { get; set; } = 0.5f;
    public bool AutoMark { get; set; } = true;
    public long LastFrame { get; private set; } = -1;
    public long RecordedFrames { get; private set; }
    public long SynthesizedFrames { get; private set; }
    public long InterpolatedFrames { get; private set; }
    public long MotionEstimations { get; private set; }
    public long OccludedBlocks { get; private set; }
    public long ClipsMarked { get; private set; }
    public long ClipsAired { get; private set; }
    public long MergedEvents { get; private set; }
    public double LastSynthUs { get; private set; }
    public double LastMeUs { get; private set; }
    public float MeanMotion { get; private set; }
    public int OutputFps => Hz * SubFrames;
    public float EffectiveFps => Hz / Math.Max(0.05f, Speed);
    public bool IsOnAir => _onAir >= 0;
    public int QueueCount => _qCount;
    public double Playhead => _play;
    public ReplayClip OnAirClip => _onAir >= 0 ? _clips[_onAir] : default;
    public ReplayClip Clip(int slot) => _clips[slot];
    public int QueuedSlot(int k) => _queue[(_qHead + k) % MaxClips];
    public int MvX(int block) => _mvx[block];
    public int MvY(int block) => _mvy[block];
    public int MvSad(int block) => _mvSad[block];
    /// <summary>직전 틱 첫 서브프레임(120 fps 출력의 홀수 프레임).</summary>
    public ReadOnlySpan<byte> SubFrame => _sub;

    /// <summary>링에 남아 있는 가장 오래된 프레임.</summary>
    public long OldestFrame => LastFrame < 0 ? -1 : Math.Max(0, LastFrame - RingFrames + 1);

    public bool TryGetFrame(int ch, long frame, out ReadOnlySpan<byte> f)
    {
        f = default;
        if ((uint)ch >= Channels || frame < 0) return false;
        var slot = ch * RingFrames + (int)(frame % RingFrames);
        if (_ringFrame[slot] != frame) return false;
        f = _ring.AsSpan(slot * FrameBytes, FrameBytes);
        return true;
    }

    /// <summary>ISO 채널 기록(틱당 1회 · 프레임 = 스위처 프레임 번호) → 포스트롤 완료 클립을 송출 큐로.</summary>
    public void Record(RealtimeBroadcastVisionSwitcherEngine sw, long frame)
    {
        for (var c = 0; c < Channels; c++)
        {
            var slot = c * RingFrames + (int)(frame % RingFrames);
            if (_onAir >= 0 && _ringFrame[slot] >= _clips[_onAir].InFrame && _ringFrame[slot] <= _clips[_onAir].OutFrame && _clips[_onAir].Channel == c) continue;   // 송출 중 클립 보호
            sw.Input(IsoInputs[c]).CopyTo(_ring.AsSpan(slot * FrameBytes, FrameBytes));
            _ringFrame[slot] = frame;
        }
        LastFrame = frame; RecordedFrames++;
        for (var i = 0; i < MaxClips; i++)
        {
            ref var c = ref _clips[i];
            if (c.State != ClipState.Recording || frame < c.OutFrame) continue;
            c.State = ClipState.Ready;
            if (_qCount < MaxClips) { _queue[(_qHead + _qCount) % MaxClips] = i; _qCount++; }
        }
    }

    /// <summary>경기 이벤트 링 폴링 → 임계 이상 자동 마크.</summary>
    public void Observe(BroadcastArenaScene scene)
    {
        if (scene.EventCount - _seenEvents > BroadcastArenaScene.EventRing) _seenEvents = scene.EventCount - BroadcastArenaScene.EventRing;
        for (; _seenEvents < scene.EventCount; _seenEvents++)
        {
            var sc = scene.EventScore(_seenEvents);
            if (AutoMark && sc >= AutoThreshold) Mark(scene.EventKind(_seenEvents), scene.EventFrame(_seenEvents), sc);
        }
    }

    /// <summary>하이라이트 마크 → 클립(프리롤/포스트롤) 생성 또는 근접 녹화 중 클립에 병합. 반환 = 클립 슬롯(−1 = 불가).</summary>
    public int Mark(ArenaEventKind kind, long eventFrame, float score)
    {
        for (var i = 0; i < MaxClips; i++)
        {
            ref var c = ref _clips[i];
            if (c.State != ClipState.Recording || eventFrame > c.OutFrame + 15) continue;
            c.OutFrame = Math.Min(Math.Max(c.OutFrame, eventFrame + PostRollFrames), c.InFrame + RingFrames - 2);
            if (score > c.Score) { c.Score = score; c.Kind = kind; c.Channel = ChannelFor(kind); }
            MergedEvents++;
            return i;
        }
        for (var k = 0; k < MaxClips; k++)
        {
            var i = (_nextSlot + k) % MaxClips;
            var st = _clips[i].State;
            if (st == ClipState.OnAir || st == ClipState.Recording || st == ClipState.Ready) continue;
            var inF = Math.Max(eventFrame - PreRollFrames, Math.Max(0, OldestFrame + 2));
            _clips[i] = new ReplayClip
            {
                Id = _nextId++, Kind = kind, Channel = ChannelFor(kind), EventFrame = eventFrame,
                InFrame = inF, OutFrame = Math.Min(eventFrame + PostRollFrames, inF + RingFrames - 2), Score = score, Speed = Speed, State = ClipState.Recording,
            };
            _nextSlot = (i + 1) % MaxClips;
            ClipsMarked++;
            return i;
        }
        return -1;
    }

    private static int ChannelFor(ArenaEventKind k) => k switch { ArenaEventKind.Goal => 0, ArenaEventKind.Collision => 2, ArenaEventKind.Drama => 1, _ => 0 };

    /// <summary>송출 큐 선두 클립 온에어(다른 클립 송출 중이면 거부).</summary>
    public bool TakeNext()
    {
        if (_onAir >= 0 || _qCount == 0) return false;
        var i = _queue[_qHead]; _qHead = (_qHead + 1) % MaxClips; _qCount--;
        ref var c = ref _clips[i];
        if (c.State != ClipState.Ready || c.InFrame < OldestFrame) { c.State = ClipState.Done; return false; }
        c.State = ClipState.OnAir; _onAir = i; _play = c.InFrame;
        return true;
    }

    public void Abort()
    {
        if (_onAir < 0) return;
        _clips[_onAir].State = ClipState.Done; _onAir = -1;
    }

    /// <summary>
    /// 1 틱 재생(무할당): 서브프레임 2장 합성(_sub ← t, dst ← t + Δ/2) 후 재생 헤드 전진. 반환 = 이번 틱 송출 여부(클립 종료 시 false).
    /// </summary>
    public bool Render(Span<byte> dst)
    {
        if (_onAir < 0) return false;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        ref var c = ref _clips[_onAir];
        var half = c.Speed / SubFrames;
        Synthesize(c.Channel, _play, _sub);
        Synthesize(c.Channel, Math.Min(_play + half, c.OutFrame), dst);
        _play += c.Speed;
        LastSynthUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        if (_play >= c.OutFrame) { c.State = ClipState.Done; _onAir = -1; ClipsAired++; }
        return true;
    }

    /// <summary>링 위치(분수 프레임) → 프레임(정수 위치 = 복사 · 분수 = 움직임 보상 보간).</summary>
    public void Synthesize(int ch, double pos, Span<byte> dst)
    {
        var i0 = (long)Math.Floor(pos);
        var phi = (float)(pos - i0);
        if (!TryGetFrame(ch, i0, out var a)) return;   // 링 밖 → 직전 출력 유지
        SynthesizedFrames++;
        if (phi < 1f / 512 || !TryGetFrame(ch, i0 + 1, out var b)) { a.CopyTo(dst); return; }
        if (_meCh != ch || _meFrame != i0) { EstimateMotion(a, b); _meCh = ch; _meFrame = i0; }
        Interpolate(a, b, phi, dst);
    }

    private void Luma(ReadOnlySpan<byte> f, byte[] y)
    {
        for (int p = 0, o = 0; p < y.Length; p++, o += 4) y[p] = (byte)((54 * f[o] + 183 * f[o + 1] + 19 * f[o + 2]) >> 8);
    }

    /// <summary>A → B 블록 정합(전역 탐색 · 영벡터 우선 · 조기 종료). mv: A(x) ≈ B(x + mv).</summary>
    public void EstimateMotion(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Luma(a, _lumaA); Luma(b, _lumaB);
        long mag = 0;
        for (var by = 0; by < BlocksY; by++)
            for (var bx = 0; bx < BlocksX; bx++)
            {
                int x0 = bx * BlockSize, y0 = by * BlockSize;
                var best = Sad(x0, y0, 0, 0, int.MaxValue); int bdx = 0, bdy = 0;
                for (var dy = -SearchRange; dy <= SearchRange; dy++)
                {
                    if (y0 + dy < 0 || y0 + dy + BlockSize > Height) continue;
                    for (var dx = -SearchRange; dx <= SearchRange; dx++)
                    {
                        if ((dx | dy) == 0 || x0 + dx < 0 || x0 + dx + BlockSize > Width) continue;
                        var s = Sad(x0, y0, dx, dy, best);
                        if (s < best || s == best && dx * dx + dy * dy < bdx * bdx + bdy * bdy) { best = s; bdx = dx; bdy = dy; }
                    }
                }
                var k = by * BlocksX + bx;
                _mvx[k] = (sbyte)bdx; _mvy[k] = (sbyte)bdy; _mvSad[k] = best;
                mag += Math.Abs(bdx) + Math.Abs(bdy);
            }
        MeanMotion = mag / (float)(BlocksX * BlocksY);
        MotionEstimations++;
        LastMeUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private int Sad(int x0, int y0, int dx, int dy, int limit)
    {
        var s = 0;
        for (var y = 0; y < BlockSize; y++)
        {
            int ra = (y0 + y) * Width + x0, rb = (y0 + y + dy) * Width + x0 + dx;
            for (var x = 0; x < BlockSize; x++) s += Math.Abs(_lumaA[ra + x] - _lumaB[rb + x]);
            if (s > limit) return s;
        }
        return s;
    }

    /// <summary>
    /// 양방향 움직임 보상 보간: out(x) = (1−φ)·A(x − φ·mv) + φ·B(x + (1−φ)·mv) (쌍선형 8비트 고정소수).
    /// SAD/픽셀 &gt; OcclusionSad 블록(가림/정합 실패)은 같은 위치 선형 혼합으로 폴백.
    /// </summary>
    public void Interpolate(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, float phi, Span<byte> dst)
    {
        var w = Math.Clamp((int)(phi * 256f + 0.5f), 0, 256);
        long occ = 0;
        for (var y = 0; y < Height; y++)
        {
            var by = Math.Min(BlocksY - 1, y / BlockSize);
            for (var bx = 0; bx < BlocksX; bx++)
            {
                var k = by * BlocksX + bx;
                var x0 = bx * BlockSize;
                var o = (y * Width + x0) * 4;
                if (_mvSad[k] > OcclusionSad * BlockSize * BlockSize)
                {
                    SwitchSimd.MixScalar(a.Slice(o, BlockSize * 4), b.Slice(o, BlockSize * 4), dst.Slice(o, BlockSize * 4), w);
                    if (y % BlockSize == 0) occ++;
                    continue;
                }
                int ax = (int)MathF.Round(-phi * _mvx[k] * 256f), ay = (int)MathF.Round(-phi * _mvy[k] * 256f);
                int bxo = (int)MathF.Round((1f - phi) * _mvx[k] * 256f), byo = (int)MathF.Round((1f - phi) * _mvy[k] * 256f);
                for (var x = x0; x < x0 + BlockSize; x++, o += 4)
                    for (var ch = 0; ch < 3; ch++)
                    {
                        var va = Bilinear(a, (x << 8) + ax, (y << 8) + ay, ch);
                        var vb = Bilinear(b, (x << 8) + bxo, (y << 8) + byo, ch);
                        dst[o + ch] = (byte)((va * (256 - w) + vb * w + 128) >> 8);
                    }
            }
        }
        for (var o = 3; o < dst.Length; o += 4) dst[o] = 255;
        OccludedBlocks += occ;
        InterpolatedFrames++;
    }

    private int Bilinear(ReadOnlySpan<byte> f, int fx, int fy, int ch)
    {
        int ix = fx >> 8, iy = fy >> 8, ux = fx & 255, uy = fy & 255;
        int x0 = Math.Clamp(ix, 0, Width - 1), x1 = Math.Clamp(ix + 1, 0, Width - 1), y0 = Math.Clamp(iy, 0, Height - 1), y1 = Math.Clamp(iy + 1, 0, Height - 1);
        int p00 = f[(y0 * Width + x0) * 4 + ch], p10 = f[(y0 * Width + x1) * 4 + ch], p01 = f[(y1 * Width + x0) * 4 + ch], p11 = f[(y1 * Width + x1) * 4 + ch];
        var top = p00 * (256 - ux) + p10 * ux;
        var bot = p01 * (256 - ux) + p11 * ux;
        return (top * (256 - uy) + bot * uy + 32768) >> 16;
    }
}
