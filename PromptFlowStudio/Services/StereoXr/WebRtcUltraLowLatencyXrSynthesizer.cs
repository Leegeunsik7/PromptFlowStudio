using System.Buffers.Binary;
using System.Numerics;

namespace PromptFlowStudio.Services.StereoXr;

/// <summary>HMD → 서버 데이터 채널 메시지: 6DoF 헤드 포즈 · 컨트롤러 · 시선(0..1).</summary>
public readonly record struct XrClientPose(uint Seq, long TimeUs, Vector3 Position, Quaternion Orientation, ushort Buttons, float Trigger, Vector2 Stick, Vector2 Gaze);

/// <summary>
/// [PromptFlow Studio v17.22.0] 📡 WebRTC 초저지연 WebXR 스트리밍 패킷 합성기(무할당 링버퍼 · 결정적 링크 모델).
///   ▸ 비디오(PT 96): 양안 프레임을 8×8 타일 레코드(RGB565 · 타일 VRS 레이트만큼 서브샘플)로 부호화 — 직전 전송 대비 변경 타일만 보내는 델타,
///     키프레임 주기/PLI 시 전체. RTP 12 B 헤더 + 1200 B MTU 패킷 분할 · 마커 비트 = 프레임 끝 · 첫 패킷에 렌더 포즈 헤더(ATW 입력).
///   ▸ 오디오(PT 97 · L16 48 kHz 스테레오 · 5 ms 패킷): v17.18 음성(모노)을 청자 머리 기준 우드워스 ITD + ILD 로 바이노럴 공간화.
///   ▸ 데이터 채널(64 B · FNV-1a 무결성): HMD 6DoF 포즈 · 버튼/트리거/스틱 · 시선 역수신 — 순서 역전/손상 메시지 폐기.
///   ▸ 링크: 업/다운 기저 지연 + 지터 + 주기적 Wi-Fi 스파이크(FIFO 헤드 오브 라인) · 손실 시 RTP 시퀀스 공백 → 프레임 폐기 + PLI 키프레임.
/// </summary>
public sealed class WebRtcUltraLowLatencyXrSynthesizer
{
    public const int Mtu = 1200, RtpHeader = 12, Slot = RtpHeader + Mtu, Ring = 512, PtVideo = 96, PtAudio = 97;
    public const int AudioFs = 48000, AudioFrames = 240, AudioHist = 128, PoseBytes = 64, PoseRing = 64, FrameHeader = 32, KeyInterval = 120;
    public const uint SsrcVideo = 0x50465856, SsrcAudio = 0x50465841;
    public const float HeadRadiusM = 0.0875f, SoundSpeed = 343f;

    private const int EN = StereoscopicFoveatedRenderingEngine.EN, EW = StereoscopicFoveatedRenderingEngine.EW, Tile = StereoscopicFoveatedRenderingEngine.Tile;
    private const int TX = StereoscopicFoveatedRenderingEngine.TX, Tiles = StereoscopicFoveatedRenderingEngine.Tiles;

    // 다운링크 패킷 링(서버 → HMD)
    private readonly byte[] _ring = new byte[Ring * Slot];
    private readonly int[] _len = new int[Ring];
    private readonly double[] _arrive = new double[Ring];
    private readonly bool[] _lost = new bool[Ring];
    private int _head, _tail, _count;
    private double _lastArrive;
    // 업링크 데이터 채널 링(HMD → 서버)
    private readonly byte[] _poseRing = new byte[PoseRing * PoseBytes];
    private readonly double[] _poseArrive = new double[PoseRing];
    private int _pHead, _pTail, _pCount;
    private double _lastPoseArrive;
    // 부호화기 상태
    private readonly ushort[] _sent = new ushort[2 * EN];
    private readonly byte[] _sentRate = new byte[2 * Tiles];
    private ushort _seqV, _seqA, _frameId;
    private int _sinceKey = int.MaxValue;
    private int _slot = -1, _off;
    // 오디오
    private readonly float[] _hist = new float[AudioHist];
    private readonly short[] _aBuf = new short[AudioFrames * 2];
    private int _histPos, _aFill;
    private uint _aTs;
    private float _itd, _gL = 0.7071f, _gR = 0.7071f;
    // 복호화기(HMD) 상태
    private readonly uint[] _decode = new uint[2 * EN], _front = new uint[2 * EN];
    private ushort _expV, _expA;
    private bool _haveV, _haveA, _inFrame, _broken;
    private ushort _curFrame;
    private Vector3 _curPos;
    private Quaternion _curQ = Quaternion.Identity;
    private double _curPoseMs;
    private uint _rng = 0x9E3779B9u;

    public float UpBaseMs { get; set; } = 1.5f;
    public float DownBaseMs { get; set; } = 3f;
    public float JitterMs { get; set; } = 2f;
    public float PipelineMs { get; set; } = 5f;
    public float DecodeMs { get; set; } = 1.5f;
    public float LinkMbps { get; set; } = 200f;
    public float SpikeEverySec { get; set; } = 2.5f;
    public float SpikeDurMs { get; set; } = 150f;
    public float SpikeMs { get; set; } = 45f;
    public bool Spikes { get; set; } = true;
    public float LossRate { get; set; }
    public bool KeyframeRequested { get; private set; } = true;

    // 서버 통계
    public long BytesSent { get; private set; }
    public long VideoPackets { get; private set; }
    public long AudioPackets { get; private set; }
    public long KeyFrames { get; private set; }
    public long FramesSent { get; private set; }
    public int LastFrameBytes { get; private set; }
    public int LastFramePackets { get; private set; }
    public int LastTilesSent { get; private set; }
    public int LastTilesSkipped { get; private set; }
    public int MaxPacketBytes { get; private set; }
    public float BitrateMbps { get; private set; }
    public float LinkDelayMs { get; private set; }
    public long Overflows { get; private set; }
    public long LostPackets { get; private set; }
    public double LastEncodeUs { get; private set; }
    // 데이터 채널 통계
    public XrClientPose LatestPose { get; private set; }
    public bool HasPose { get; private set; }
    public double LatestPoseArriveMs { get; private set; }
    public long PosesReceived { get; private set; }
    public long StaleDrops { get; private set; }
    public long CorruptDrops { get; private set; }
    // 클라이언트 통계
    public ReadOnlySpan<uint> ClientFrame => _front;
    public bool ClientHasFrame { get; private set; }
    public ushort ClientFrameId { get; private set; }
    public Vector3 ClientRenderPosition { get; private set; }
    public Quaternion ClientRenderOrientation { get; private set; } = Quaternion.Identity;
    public double ClientPoseTimeMs { get; private set; }
    public double ClientFrameReadyMs { get; private set; }
    public long FramesDecoded { get; private set; }
    public long FramesCorrupted { get; private set; }
    public long AudioDecoded { get; private set; }
    public long AudioGaps { get; private set; }
    public float AudioLevelL { get; private set; }
    public float AudioLevelR { get; private set; }
    public float AudioItdUs => _itd * 1e6f / AudioFs;
    public int InFlight => _count;

    // ── 공용 유틸 ──────────────────────────────────────────

    public static ushort To565(uint argb) => (ushort)(((argb >> 19) & 0x1F) << 11 | ((argb >> 10) & 0x3F) << 5 | ((argb >> 3) & 0x1F));

    public static uint From565(ushort c)
    {
        uint r = (uint)(c >> 11) & 0x1F, g = (uint)(c >> 5) & 0x3F, b = (uint)c & 0x1F;
        return 0xFF000000u | ((r << 3) | (r >> 2)) << 16 | ((g << 2) | (g >> 4)) << 8 | ((b << 3) | (b >> 2));
    }

    private static uint Fnv(ReadOnlySpan<byte> s)
    {
        var h = 2166136261u;
        for (var i = 0; i < s.Length; i++) h = (h ^ s[i]) * 16777619u;
        return h;
    }

    private float Rand()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
        return (_rng >> 8) * (1f / 16777216f);
    }

    /// <summary>현재 다운링크 프레임 지연(ms) = 기저 + 지터 + 스파이크.</summary>
    private float DownDelay(double nowMs)
    {
        var d = DownBaseMs + JitterMs * Rand();
        if (Spikes && SpikeEverySec > 0f && nowMs % (SpikeEverySec * 1000.0) < SpikeDurMs) d += SpikeMs;
        LinkDelayMs = d;
        return d;
    }

    // ── 데이터 채널(포즈 · 컨트롤러) ──────────────────────────

    public static int WritePose(Span<byte> dst, in XrClientPose p)
    {
        dst[..PoseBytes].Clear();
        dst[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(dst[2..], p.Seq);
        BinaryPrimitives.WriteInt64LittleEndian(dst[6..], p.TimeUs);
        BinaryPrimitives.WriteSingleLittleEndian(dst[14..], p.Position.X); BinaryPrimitives.WriteSingleLittleEndian(dst[18..], p.Position.Y); BinaryPrimitives.WriteSingleLittleEndian(dst[22..], p.Position.Z);
        BinaryPrimitives.WriteSingleLittleEndian(dst[26..], p.Orientation.X); BinaryPrimitives.WriteSingleLittleEndian(dst[30..], p.Orientation.Y);
        BinaryPrimitives.WriteSingleLittleEndian(dst[34..], p.Orientation.Z); BinaryPrimitives.WriteSingleLittleEndian(dst[38..], p.Orientation.W);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[42..], p.Buttons);
        BinaryPrimitives.WriteSingleLittleEndian(dst[44..], p.Trigger);
        BinaryPrimitives.WriteSingleLittleEndian(dst[48..], p.Stick.X); BinaryPrimitives.WriteSingleLittleEndian(dst[52..], p.Stick.Y);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[56..], (ushort)(Math.Clamp(p.Gaze.X, 0f, 1f) * 65535f + 0.5f));
        BinaryPrimitives.WriteUInt16LittleEndian(dst[58..], (ushort)(Math.Clamp(p.Gaze.Y, 0f, 1f) * 65535f + 0.5f));
        BinaryPrimitives.WriteUInt32LittleEndian(dst[60..], Fnv(dst[..60]));
        return PoseBytes;
    }

    public static bool TryReadPose(ReadOnlySpan<byte> src, out XrClientPose p)
    {
        p = default;
        if (src.Length < PoseBytes || src[0] != 1 || BinaryPrimitives.ReadUInt32LittleEndian(src[60..]) != Fnv(src[..60])) return false;
        p = new XrClientPose(
            BinaryPrimitives.ReadUInt32LittleEndian(src[2..]), BinaryPrimitives.ReadInt64LittleEndian(src[6..]),
            new Vector3(BinaryPrimitives.ReadSingleLittleEndian(src[14..]), BinaryPrimitives.ReadSingleLittleEndian(src[18..]), BinaryPrimitives.ReadSingleLittleEndian(src[22..])),
            new Quaternion(BinaryPrimitives.ReadSingleLittleEndian(src[26..]), BinaryPrimitives.ReadSingleLittleEndian(src[30..]), BinaryPrimitives.ReadSingleLittleEndian(src[34..]), BinaryPrimitives.ReadSingleLittleEndian(src[38..])),
            BinaryPrimitives.ReadUInt16LittleEndian(src[42..]), BinaryPrimitives.ReadSingleLittleEndian(src[44..]),
            new Vector2(BinaryPrimitives.ReadSingleLittleEndian(src[48..]), BinaryPrimitives.ReadSingleLittleEndian(src[52..])),
            new Vector2(BinaryPrimitives.ReadUInt16LittleEndian(src[56..]) / 65535f, BinaryPrimitives.ReadUInt16LittleEndian(src[58..]) / 65535f));
        return true;
    }

    /// <summary>서버 수신 처리: 손상 · 순서 역전(오래된 seq) 폐기.</summary>
    public bool ReceiveControl(ReadOnlySpan<byte> msg, double nowMs)
    {
        if (!TryReadPose(msg, out var p)) { CorruptDrops++; return false; }
        if (HasPose && p.Seq <= LatestPose.Seq) { StaleDrops++; return false; }
        LatestPose = p; HasPose = true; LatestPoseArriveMs = nowMs; PosesReceived++;
        return true;
    }

    /// <summary>HMD 측 송신: 업링크 지연(기저 + 지터 30%) 후 서버 도착.</summary>
    public void SendPose(in XrClientPose p, double nowMs)
    {
        if (_pCount == PoseRing) { _pTail = (_pTail + 1) % PoseRing; _pCount--; Overflows++; }
        WritePose(_poseRing.AsSpan(_pHead * PoseBytes, PoseBytes), p);
        var a = Math.Max(_lastPoseArrive, nowMs + UpBaseMs + JitterMs * 0.3f * Rand());
        _poseArrive[_pHead] = a; _lastPoseArrive = a;
        _pHead = (_pHead + 1) % PoseRing; _pCount++;
    }

    /// <summary>서버 측: 도착 시각이 지난 데이터 채널 메시지 수신.</summary>
    public void PumpServer(double nowMs)
    {
        while (_pCount > 0 && _poseArrive[_pTail] <= nowMs)
        {
            ReceiveControl(_poseRing.AsSpan(_pTail * PoseBytes, PoseBytes), _poseArrive[_pTail]);
            _pTail = (_pTail + 1) % PoseRing; _pCount--;
        }
    }

    // ── RTP 패킷 ───────────────────────────────────────────

    private void Begin(int pt, ushort seq, uint ts, uint ssrc)
    {
        if (_count == Ring) { _tail = (_tail + 1) % Ring; _count--; Overflows++; }
        _slot = _head;
        var s = _ring.AsSpan(_slot * Slot, RtpHeader);
        s[0] = 0x80; s[1] = (byte)pt;
        BinaryPrimitives.WriteUInt16BigEndian(s[2..], seq);
        BinaryPrimitives.WriteUInt32BigEndian(s[4..], ts);
        BinaryPrimitives.WriteUInt32BigEndian(s[8..], ssrc);
        _off = RtpHeader;
    }

    private void Commit(bool marker, double arriveMs)
    {
        var b = _slot * Slot;
        if (marker) _ring[b + 1] |= 0x80;
        _len[_slot] = _off;
        var a = Math.Max(_lastArrive, arriveMs + _off * 8.0 / (LinkMbps * 1000.0));
        _arrive[_slot] = a; _lastArrive = a;
        var lost = LossRate > 0f && Rand() < LossRate;
        _lost[_slot] = lost;
        if (lost) LostPackets++;
        BytesSent += _off;
        if (_off > MaxPacketBytes) MaxPacketBytes = _off;
        _head = (_head + 1) % Ring; _count++;
        _slot = -1;
    }

    private Span<byte> Body => _ring.AsSpan(_slot * Slot + _off, Slot - _off);

    private void BeginVideo(uint ts, bool first, bool key)
    {
        Begin(PtVideo, _seqV++, ts, SsrcVideo);
        var s = Body;
        BinaryPrimitives.WriteUInt16BigEndian(s, _frameId);
        s[2] = (byte)((first ? 1 : 0) | (key ? 2 : 0)); s[3] = 0;
        _off += 4;
    }

    /// <summary>
    /// 양안 프레임 부호화 · 패킷화 · 송신(무할당). 렌더 포즈(위치 · 회전 · 샘플 시각)는 첫 패킷 헤더로 전달된다.
    /// </summary>
    public void SendFrame(ReadOnlySpan<uint> px, ReadOnlySpan<byte> rates, Vector3 renderPos, Quaternion renderQ, double poseTimeMs, double nowMs)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var key = KeyframeRequested || _sinceKey >= KeyInterval;
        if (key) { KeyframeRequested = false; _sinceKey = 0; KeyFrames++; } else _sinceKey++;
        var ts = (uint)(long)(poseTimeMs * 90.0);
        var arrive = nowMs + PipelineMs + DownDelay(nowMs);
        long before = BytesSent;
        int packets = 1, sentTiles = 0, skipped = 0;
        BeginVideo(ts, true, key);
        var h = Body;
        BinaryPrimitives.WriteSingleBigEndian(h, renderPos.X); BinaryPrimitives.WriteSingleBigEndian(h[4..], renderPos.Y); BinaryPrimitives.WriteSingleBigEndian(h[8..], renderPos.Z);
        BinaryPrimitives.WriteSingleBigEndian(h[12..], renderQ.X); BinaryPrimitives.WriteSingleBigEndian(h[16..], renderQ.Y);
        BinaryPrimitives.WriteSingleBigEndian(h[20..], renderQ.Z); BinaryPrimitives.WriteSingleBigEndian(h[24..], renderQ.W);
        BinaryPrimitives.WriteSingleBigEndian(h[28..], (float)(poseTimeMs - Math.Floor(poseTimeMs / 1000.0) * 1000.0));
        _off += FrameHeader;
        for (var e = 0; e < 2; e++)
        for (var t = 0; t < Tiles; t++)
        {
            var id = e * Tiles + t;
            var rate = rates[id];
            var x0 = (t % TX) * Tile; var y0 = (t / TX) * Tile; var o = e * EN;
            var changed = key || rate != _sentRate[id];
            if (!changed)
            {
                for (var y = y0; y < y0 + Tile && !changed; y += rate)
                for (var x = x0; x < x0 + Tile; x += rate)
                    if (To565(px[o + y * EW + x]) != _sent[o + y * EW + x]) { changed = true; break; }
            }
            if (!changed) { skipped++; continue; }
            var n = Tile / rate;
            var need = 3 + n * n * 2;
            if (_off + need > Slot) { Commit(false, arrive); BeginVideo(ts, false, key); packets++; }
            var s = Body;
            BinaryPrimitives.WriteUInt16BigEndian(s, (ushort)id); s[2] = rate;
            var w = 3;
            for (var y = y0; y < y0 + Tile; y += rate)
            for (var x = x0; x < x0 + Tile; x += rate)
            {
                var c = To565(px[o + y * EW + x]);
                BinaryPrimitives.WriteUInt16BigEndian(s[w..], c); w += 2;
                for (var yy = y; yy < y + rate; yy++)
                for (var xx = x; xx < x + rate; xx++) _sent[o + yy * EW + xx] = c;
            }
            _off += w;
            _sentRate[id] = rate;
            sentTiles++;
        }
        Commit(true, arrive);
        _frameId++;
        FramesSent++; VideoPackets += packets;
        LastFrameBytes = (int)(BytesSent - before); LastFramePackets = packets; LastTilesSent = sentTiles; LastTilesSkipped = skipped;
        BitrateMbps += (LastFrameBytes * 8f * 60f / 1e6f - BitrateMbps) * 0.1f;
        LastEncodeUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    // ── 바이노럴 오디오 ─────────────────────────────────────

    /// <summary>방위각(라디안 · 우측 +) → 귀 이득 · ITD(샘플, + = 좌측 지연). 우드워스 구형 머리 모델.</summary>
    public static void Binaural(float azimuth, out float gL, out float gR, out float itdSamples)
    {
        var s = MathF.Sin(azimuth);
        var lat = MathF.Asin(Math.Clamp(s, -1f, 1f));                 // 전후 폴딩(±90°)
        itdSamples = HeadRadiusM / SoundSpeed * (lat + MathF.Sin(lat)) * AudioFs;
        gL = MathF.Sqrt(0.5f * (1f - 0.7f * s));
        gR = MathF.Sqrt(0.5f * (1f + 0.7f * s));
    }

    /// <summary>모노 음원 → 청자 머리 기준 바이노럴 L16 패킷(5 ms) 송신(무할당).</summary>
    public void SendAudio(ReadOnlySpan<float> mono, Vector3 srcPos, Vector3 headPos, Quaternion headQ, double nowMs)
    {
        var rel = Vector3.Transform(srcPos - headPos, Quaternion.Conjugate(headQ));
        var dist = rel.Length();
        Binaural(MathF.Atan2(rel.X, rel.Z), out var gL, out var gR, out var itd);
        var att = 1f / MathF.Max(1f, dist);
        gL *= att; gR *= att;
        float pL = _gL, pR = _gR, pI = _itd;
        var n = mono.Length;
        for (var i = 0; i < n; i++)
        {
            var k = (i + 1f) / n;                                       // 블록 내 선형 램프(지퍼 노이즈 방지)
            var cL = pL + (gL - pL) * k; var cR = pR + (gR - pR) * k; var cI = pI + (itd - pI) * k;
            _hist[_histPos] = mono[i];
            var dl = MathF.Max(0f, cI); var dr = MathF.Max(0f, -cI);
            var l = Delayed(dl) * cL; var r = Delayed(dr) * cR;
            _histPos = (_histPos + 1) % AudioHist;
            _aBuf[_aFill * 2] = (short)Math.Clamp((int)(l * 32767f), -32768, 32767);
            _aBuf[_aFill * 2 + 1] = (short)Math.Clamp((int)(r * 32767f), -32768, 32767);
            if (++_aFill == AudioFrames)
            {
                Begin(PtAudio, _seqA++, _aTs, SsrcAudio);
                var s = Body;
                for (var j = 0; j < AudioFrames * 2; j++) BinaryPrimitives.WriteInt16BigEndian(s[(j * 2)..], _aBuf[j]);
                _off += AudioFrames * 4;
                Commit(false, nowMs + 2f + LinkDelayMs);
                _aTs += AudioFrames; _aFill = 0; AudioPackets++;
            }
        }
        _gL = gL; _gR = gR; _itd = itd;
    }

    private float Delayed(float d)
    {
        var i = (int)d; var f = d - i;
        var a = _hist[(_histPos - i + AudioHist) % AudioHist];
        var b = _hist[(_histPos - i - 1 + AudioHist) % AudioHist];
        return a + (b - a) * f;
    }

    // ── HMD 복호화 ─────────────────────────────────────────

    /// <summary>클라이언트: 도착(+복호 시간)이 지난 패킷 소비 · 완결 프레임을 프런트 버퍼로 교체. 반환 = 새 프레임 수.</summary>
    public int PumpClient(double nowMs)
    {
        var frames = 0;
        while (_count > 0 && _arrive[_tail] + DecodeMs <= nowMs)
        {
            var slot = _tail;
            _tail = (_tail + 1) % Ring; _count--;
            if (_lost[slot]) continue;
            var p = _ring.AsSpan(slot * Slot, _len[slot]);
            var seq = BinaryPrimitives.ReadUInt16BigEndian(p[2..]);
            if (p[1] == PtAudio)
            {
                if (_haveA && seq != _expA) AudioGaps++;
                _haveA = true; _expA = (ushort)(seq + 1);
                DecodeAudio(p[RtpHeader..]);
                continue;
            }
            if (_haveV && seq != _expV) _broken = true;     // 시퀀스 공백 = 손실
            _haveV = true; _expV = (ushort)(seq + 1);
            if (DecodeVideo(p, _arrive[slot] + DecodeMs)) frames++;
        }
        return frames;
    }

    private bool DecodeVideo(ReadOnlySpan<byte> p, double readyMs)
    {
        var marker = (p[1] & 0x80) != 0;
        var ts = BinaryPrimitives.ReadUInt32BigEndian(p[4..]);
        var b = p[RtpHeader..];
        var fid = BinaryPrimitives.ReadUInt16BigEndian(b);
        var first = (b[2] & 1) != 0;
        var r = 4;
        if (first)
        {
            if (_inFrame) { FramesCorrupted++; KeyframeRequested = true; }   // 직전 프레임 마커 유실
            _inFrame = true; _broken = false; _curFrame = fid;
            _curPos = new Vector3(BinaryPrimitives.ReadSingleBigEndian(b[r..]), BinaryPrimitives.ReadSingleBigEndian(b[(r + 4)..]), BinaryPrimitives.ReadSingleBigEndian(b[(r + 8)..]));
            _curQ = new Quaternion(BinaryPrimitives.ReadSingleBigEndian(b[(r + 12)..]), BinaryPrimitives.ReadSingleBigEndian(b[(r + 16)..]), BinaryPrimitives.ReadSingleBigEndian(b[(r + 20)..]), BinaryPrimitives.ReadSingleBigEndian(b[(r + 24)..]));
            _curPoseMs = ts / 90.0;
            r += FrameHeader;
        }
        else if (!_inFrame || fid != _curFrame) { if (marker) { FramesCorrupted++; KeyframeRequested = true; } return false; }   // 첫 패킷 유실
        while (r + 3 <= b.Length)
        {
            var id = BinaryPrimitives.ReadUInt16BigEndian(b[r..]);
            var rate = b[r + 2];
            r += 3;
            if (id >= 2 * Tiles || rate is not (1 or 2 or 4)) { _broken = true; break; }
            var e = id / Tiles; var t = id % Tiles;
            var x0 = (t % TX) * Tile; var y0 = (t / TX) * Tile; var o = e * EN;
            for (var y = y0; y < y0 + Tile; y += rate)
            for (var x = x0; x < x0 + Tile; x += rate)
            {
                var c = From565(BinaryPrimitives.ReadUInt16BigEndian(b[r..])); r += 2;
                for (var yy = y; yy < y + rate; yy++)
                for (var xx = x; xx < x + rate; xx++) _decode[o + yy * EW + xx] = c;
            }
        }
        if (!marker) return false;
        _inFrame = false;
        if (_broken) { _broken = false; FramesCorrupted++; KeyframeRequested = true; return false; }
        _decode.AsSpan().CopyTo(_front);
        ClientHasFrame = true; ClientFrameId = fid; ClientRenderPosition = _curPos; ClientRenderOrientation = _curQ;
        ClientPoseTimeMs = _curPoseMs; ClientFrameReadyMs = readyMs; FramesDecoded++;
        return true;
    }

    private void DecodeAudio(ReadOnlySpan<byte> b)
    {
        double sl = 0, sr = 0;
        var n = Math.Min(AudioFrames, b.Length / 4);
        for (var i = 0; i < n; i++)
        {
            float l = BinaryPrimitives.ReadInt16BigEndian(b[(i * 4)..]) / 32768f, r = BinaryPrimitives.ReadInt16BigEndian(b[(i * 4 + 2)..]) / 32768f;
            sl += l * l; sr += r * r;
        }
        AudioLevelL = (float)Math.Sqrt(sl / Math.Max(1, n)); AudioLevelR = (float)Math.Sqrt(sr / Math.Max(1, n));
        AudioDecoded++;
    }
}
