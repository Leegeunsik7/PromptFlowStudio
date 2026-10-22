using System.Buffers.Binary;
using System.Diagnostics;

namespace PromptFlowStudio.Services.LiveRestream;

public enum RtmpPlatform : byte { YouTube, Twitch, Custom }
public enum RtmpTargetState : byte { Idle, Handshaking, Connecting, Publishing, Failed, Stopped }

/// <summary>v17.83.0 송출 엔드포인트 1 개 — 전송 계층 · 세션 상태 · 키프레임 게이트 · 송신 통계 · 서버 응답 처리(무할당).</summary>
public sealed class RtmpTarget : IRtmpMessageHandler
{
    internal readonly RtmpChunkReader Reader = new(256 * 1024);
    internal double ResultTxn = -1, ResultValue;
    internal bool PublishStarted, ConnectOk, PingPending;
    internal uint PingValue;

    public RtmpTarget(string name, RtmpPlatform platform, string app, string tcUrl, string streamKey, IRtmpTransport transport)
    {
        Name = name; Platform = platform; App = app; TcUrl = tcUrl; StreamKey = streamKey; Transport = transport;
    }

    public string Name { get; }
    public RtmpPlatform Platform { get; }
    public string App { get; }
    public string TcUrl { get; }
    public string StreamKey { get; }
    public IRtmpTransport Transport { get; }
    public RtmpTargetState State { get; internal set; }
    public bool Enabled { get; set; } = true;
    public uint StreamId { get; internal set; }
    public bool WaitingKeyframe { get; internal set; } = true;
    internal int SentSeqVersion = -1, SentAudioSeqVersion = -1;
    public long BytesSent { get; internal set; }
    public long VideoFrames { get; internal set; }
    public long AudioFrames { get; internal set; }
    public long Keyframes { get; internal set; }
    public long SeqHeaders { get; internal set; }
    public long Gated { get; internal set; }
    public long Drops { get; internal set; }
    public long Regates { get; internal set; }
    public long Connects { get; internal set; }
    public ulong PayloadHash { get; internal set; }
    public double ConnectMs { get; internal set; }
    public string? LastError { get; internal set; }

    public void OnMessage(byte type, uint streamId, uint timestamp, ReadOnlySpan<byte> payload)
    {
        if (type == RtmpWire.TypeUserCtl && payload.Length >= 6 && BinaryPrimitives.ReadUInt16BigEndian(payload) == 6)
        {
            PingPending = true; PingValue = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(2));
            return;
        }
        if (type != RtmpWire.TypeCommand) return;
        var r = new Amf0Reader(payload);
        if (!r.String(out var name)) return;
        r.Number(out var txn);
        if (name.SequenceEqual("_result"u8))
        {
            ResultTxn = txn;
            if (txn == ZeroAllocMultiTargetRtmpStreamerEngine.TxnConnect) { r.Skip(); ConnectOk = r.FindString("code"u8, out var c) && c.SequenceEqual("NetConnection.Connect.Success"u8); }
            else { r.Skip(); if (r.Number(out var v)) ResultValue = v; }
        }
        else if (name.SequenceEqual("onStatus"u8))
        {
            r.Skip();
            if (r.FindString("code"u8, out var code) && code.SequenceEqual("NetStream.Publish.Start"u8)) PublishStarted = true;
        }
        else if (name.SequenceEqual("_error"u8)) ResultTxn = -2;
    }
}

/// <summary>
/// [PromptFlow Studio v17.83.0] 무할당 RTMPS 멀티-타겟 동시 송출 엔진.
///   ▸ 세션: C0/C1 → S0/S1/S2 검증(S2 = C1 에코) → C2 → SetChunkSize 4096 → connect → releaseStream/FCPublish/createStream → publish → @setDataFrame onMetaData.
///   ▸ 비디오: Annex-B AU → NALU 분해(SPS/PPS 갱신 감지 → AVC 시퀀스 헤더 버전업 · AUD 제거) → FLV AVC 태그(AVCC 4 B 길이).
///   ▸ 오디오: ADTS 제거 → FLV AAC 태그(0xAF) · AAC-LC 48 kHz 2ch 시퀀스 헤더.
///   ▸ 제로-카피 멀티캐스트: 태그 바디 1 회 구성 → 청크 1 회 직렬화(스트림 id 동일 타겟 공유) → 동일 스팬을 N 개 전송 계층에 분기.
///     신규/재연결/드롭 타겟은 다음 IDR 까지 게이트(시퀀스 헤더 선송 후 합류) → 시청자 화면 깨짐 0.
/// </summary>
public sealed class ZeroAllocMultiTargetRtmpStreamerEngine
{
    public const int MaxTargets = 4, MaxAu = 1 << 20, MaxParam = 256;
    public const double TxnConnect = 1, TxnRelease = 2, TxnFcPublish = 3, TxnCreate = 4, TxnPublish = 5;

    private readonly RtmpTarget[] _t = new RtmpTarget[MaxTargets];
    private readonly byte[] _tag = new byte[MaxAu + 64];
    private readonly byte[] _chunk = new byte[RtmpWire.MaxChunked(MaxAu + 64, RtmpWire.OutChunk)];
    private readonly byte[] _ctl = new byte[8 * 1024];
    private readonly byte[] _rx = new byte[16 * 1024];
    private readonly byte[] _sps = new byte[MaxParam], _pps = new byte[MaxParam];
    private readonly byte[] _seqTag = new byte[2 * MaxParam + 32];
    private readonly byte[] _aacSeq = new byte[8];
    private int _n, _spsLen, _ppsLen, _seqLen, _aacSeqLen, _seqVersion = -1, _audioSeqVersion = -1, _chunkLen;
    private uint _chunkSid = uint.MaxValue, _seed = 0xC0FFEE11u;

    public int Count => _n;
    public RtmpTarget Target(int i) => _t[i];
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public int Fps { get; set; } = 30;
    public int AudioSampleRateIndex { get; set; } = 3;
    public int AudioChannels { get; set; } = 2;
    public long Messages { get; private set; }
    public long ChunkBuilds { get; private set; }
    public long Fanouts { get; private set; }
    public long PanicCount { get; private set; }
    public int SeqVersion => _seqVersion;
    public double LastMuxUs { get; private set; }
    public double MaxMuxUs { get; private set; }
    public int PublishingCount { get { var c = 0; for (var i = 0; i < _n; i++) if (_t[i].State == RtmpTargetState.Publishing) c++; return c; } }

    public int AddTarget(RtmpTarget t)
    {
        if (_n >= MaxTargets) throw new InvalidOperationException("타겟 상한 " + MaxTargets);
        _t[_n] = t;
        return _n++;
    }

    // ══════════════════════ 세션 수립(비핫패스 · 타임아웃) ══════════════════════

    /// <summary>핸드셰이크 → connect → createStream → publish → 메타데이터. 실패 시 Failed + 사유.</summary>
    public bool Connect(int i, int kbps, int audioKbps, int timeoutMs = 5000)
    {
        var t = _t[i];
        var sw = Stopwatch.StartNew();
        t.State = RtmpTargetState.Handshaking; t.LastError = null;
        t.Reader.Reset(); t.ResultTxn = -1; t.PublishStarted = t.ConnectOk = false;
        t.WaitingKeyframe = true; t.SentSeqVersion = t.SentAudioSeqVersion = -1;
        try
        {
            Span<byte> hs = stackalloc byte[1 + RtmpWire.HandshakeSize];
            RtmpWire.WriteC0C1(hs, 0, ref _seed);
            var c1 = _ctl.AsSpan(0, RtmpWire.HandshakeSize);
            hs.Slice(1).CopyTo(c1);
            if (t.Transport.Send(hs) <= 0) return Fail(t, "C0/C1 송신 실패");
            var s = _rx.AsSpan(0, 1 + RtmpWire.HandshakeSize * 2);
            var got = 0;
            while (got < s.Length)
            {
                var n = t.Transport.Receive(s.Slice(got));
                if (n > 0) { got += n; continue; }
                if (!t.Transport.Connected || sw.ElapsedMilliseconds > timeoutMs) return Fail(t, "S0/S1/S2 타임아웃");
                Thread.Sleep(2);
            }
            if (s[0] != RtmpWire.Version) return Fail(t, "S0 버전 불일치");
            if (!s.Slice(1 + RtmpWire.HandshakeSize + 8, RtmpWire.HandshakeSize - 8).SequenceEqual(c1.Slice(8))) return Fail(t, "S2 에코 불일치");
            if (t.Transport.Send(s.Slice(1, RtmpWire.HandshakeSize)) <= 0) return Fail(t, "C2 송신 실패");

            t.State = RtmpTargetState.Connecting;
            var o = _ctl.AsSpan();
            var p = RtmpWire.WriteControl(o, RtmpWire.TypeSetChunk, RtmpWire.OutChunk, RtmpWire.DefaultChunk);
            Span<byte> cmd = stackalloc byte[1024];
            var w = new Amf0Writer(cmd);
            w.Str("connect"u8); w.Num(TxnConnect);
            w.ObjStart(); w.Prop("app"u8, t.App.AsSpan()); w.Prop("type"u8, "nonprivate"u8); w.Prop("flashVer"u8, "FMLE/3.0 (compatible; PromptFlowStudio)"u8); w.Prop("tcUrl"u8, t.TcUrl.AsSpan()); w.ObjEnd();
            p += RtmpWire.WriteMessage(o.Slice(p), RtmpWire.CsidCommand, 0, RtmpWire.TypeCommand, 0, w.Written, RtmpWire.OutChunk);
            if (t.Transport.Send(o.Slice(0, p)) <= 0 || !Await(t, TxnConnect, sw, timeoutMs) || !t.ConnectOk) return Fail(t, "connect 거절/타임아웃");

            p = Command(o, "releaseStream"u8, TxnRelease, t.StreamKey, 0);
            p += Command(o.Slice(p), "FCPublish"u8, TxnFcPublish, t.StreamKey, 0);
            w = new Amf0Writer(cmd); w.Str("createStream"u8); w.Num(TxnCreate); w.Null();
            p += RtmpWire.WriteMessage(o.Slice(p), RtmpWire.CsidCommand, 0, RtmpWire.TypeCommand, 0, w.Written, RtmpWire.OutChunk);
            if (t.Transport.Send(o.Slice(0, p)) <= 0 || !Await(t, TxnCreate, sw, timeoutMs)) return Fail(t, "createStream 타임아웃");
            t.StreamId = (uint)t.ResultValue;

            w = new Amf0Writer(cmd); w.Str("publish"u8); w.Num(TxnPublish); w.Null(); w.Str(t.StreamKey.AsSpan()); w.Str("live"u8);
            p = RtmpWire.WriteMessage(o, RtmpWire.CsidStream, 0, RtmpWire.TypeCommand, t.StreamId, w.Written, RtmpWire.OutChunk);
            if (t.Transport.Send(o.Slice(0, p)) <= 0) return Fail(t, "publish 송신 실패");
            while (!t.PublishStarted)
            {
                if (!Pump(t) && (!t.Transport.Connected || sw.ElapsedMilliseconds > timeoutMs)) return Fail(t, "NetStream.Publish.Start 타임아웃");
                if (!t.PublishStarted) Thread.Sleep(1);
            }

            w = new Amf0Writer(cmd);
            w.Str("@setDataFrame"u8); w.Str("onMetaData"u8);
            w.EcmaStart(8);
            w.Prop("width"u8, Width); w.Prop("height"u8, Height); w.Prop("framerate"u8, Fps); w.Prop("videocodecid"u8, 7);
            w.Prop("videodatarate"u8, kbps); w.Prop("audiocodecid"u8, 10); w.Prop("audiodatarate"u8, audioKbps); w.Prop("encoder"u8, "PromptFlowStudio v17.83 RTMPS"u8);
            w.ObjEnd();
            p = RtmpWire.WriteMessage(o, RtmpWire.CsidStream, 0, RtmpWire.TypeData, t.StreamId, w.Written, RtmpWire.OutChunk);
            if (t.Transport.Send(o.Slice(0, p)) <= 0) return Fail(t, "메타데이터 송신 실패");
            t.State = RtmpTargetState.Publishing;
            t.Connects++;
            t.ConnectMs = sw.Elapsed.TotalMilliseconds;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException)
        {
            return Fail(t, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static int Command(Span<byte> o, ReadOnlySpan<byte> name, double txn, string key, uint sid)
    {
        Span<byte> cmd = stackalloc byte[512];
        var w = new Amf0Writer(cmd);
        w.Str(name); w.Num(txn); w.Null(); w.Str(key.AsSpan());
        return RtmpWire.WriteMessage(o, RtmpWire.CsidCommand, 0, RtmpWire.TypeCommand, sid, w.Written, RtmpWire.OutChunk);
    }

    private bool Await(RtmpTarget t, double txn, Stopwatch sw, int timeoutMs)
    {
        while (t.ResultTxn != txn)
        {
            if (t.ResultTxn == -2) return false;
            if (!Pump(t) && (!t.Transport.Connected || sw.ElapsedMilliseconds > timeoutMs)) return false;
            if (t.ResultTxn != txn) Thread.Sleep(1);
        }
        return true;
    }

    /// <summary>서버 → 클라이언트 수신 소화(Ping → Pong 응답). 반환 = 수신 발생 여부.</summary>
    private bool Pump(RtmpTarget t)
    {
        var any = false;
        int n;
        while ((n = t.Transport.Receive(_rx)) > 0)
        {
            any = true;
            if (!t.Reader.Feed(_rx.AsSpan(0, n), t)) throw new InvalidOperationException("RTMP 청크 파싱 오류");
        }
        if (t.PingPending)
        {
            t.PingPending = false;
            Span<byte> pong = stackalloc byte[32];
            var m = RtmpWire.WriteUserControl(pong, 7, t.PingValue, RtmpWire.OutChunk);
            t.Transport.Send(pong.Slice(0, m));
        }
        return any;
    }

    private static bool Fail(RtmpTarget t, string why)
    {
        t.State = RtmpTargetState.Failed; t.LastError = why;
        return false;
    }

    /// <summary>정상 종료 — FCUnpublish + deleteStream 후 전송 닫기.</summary>
    public void Disconnect(int i)
    {
        var t = _t[i];
        if (t.State == RtmpTargetState.Publishing)
        {
            var o = _ctl.AsSpan();
            var p = Command(o, "FCUnpublish"u8, 6, t.StreamKey, 0);
            Span<byte> cmd = stackalloc byte[64];
            var w = new Amf0Writer(cmd); w.Str("deleteStream"u8); w.Num(7); w.Null(); w.Num(t.StreamId);
            p += RtmpWire.WriteMessage(o.Slice(p), RtmpWire.CsidCommand, 0, RtmpWire.TypeCommand, 0, w.Written, RtmpWire.OutChunk);
            t.Transport.Send(o.Slice(0, p));
        }
        t.Transport.Close();
        t.State = RtmpTargetState.Stopped;
    }

    /// <summary>긴급 송출 중단 — 모든 전송 즉시 절단(언퍼블리시 협상 생략).</summary>
    public int Panic()
    {
        var cut = 0;
        for (var i = 0; i < _n; i++)
        {
            var t = _t[i];
            if (t.State is RtmpTargetState.Publishing or RtmpTargetState.Connecting or RtmpTargetState.Handshaking) cut++;
            t.Transport.Close();
            t.State = RtmpTargetState.Stopped;
        }
        PanicCount++;
        return cut;
    }

    // ══════════════════════ 핫패스(무할당) ══════════════════════

    /// <summary>Annex-B 액세스 유닛 1 개 송출. 반환 = IDR 여부.</summary>
    public bool WriteVideo(ReadOnlySpan<byte> au, uint dtsMs, int ctsMs = 0)
    {
        var t0 = Stopwatch.GetTimestamp();
        var tag = _tag.AsSpan();
        var p = 5; var idr = false; var sps = false; var pps = false; var pos = 0;
        while (RtmpWire.NextNal(au, ref pos, out var nal))
        {
            if (nal.Length == 0) continue;
            var type = nal[0] & 0x1F;
            if (type == 7)
            {
                if (nal.Length >= 4 && nal.Length <= MaxParam && !nal.SequenceEqual(_sps.AsSpan(0, _spsLen))) { nal.CopyTo(_sps); _spsLen = nal.Length; sps = true; }
                continue;
            }
            if (type == 8)
            {
                if (nal.Length <= MaxParam && !nal.SequenceEqual(_pps.AsSpan(0, _ppsLen))) { nal.CopyTo(_pps); _ppsLen = nal.Length; pps = true; }
                continue;
            }
            if (type == 9) continue;
            if (type == 5) idr = true;
            if (p + 4 + nal.Length > tag.Length) break;
            BinaryPrimitives.WriteUInt32BigEndian(tag.Slice(p), (uint)nal.Length);
            nal.CopyTo(tag.Slice(p + 4));
            p += 4 + nal.Length;
        }
        if ((sps || pps) && _spsLen >= 4 && _ppsLen > 0)
        {
            _seqLen = RtmpWire.WriteAvcSequenceHeader(_seqTag, _sps.AsSpan(0, _spsLen), _pps.AsSpan(0, _ppsLen));
            _seqVersion++;
        }
        if (p == 5) return false;
        RtmpWire.WriteAvcNaluHeader(tag, idr, ctsMs);
        var body = tag.Slice(0, p);
        var hash = RtmpWire.Fnv(body);
        _chunkSid = uint.MaxValue;
        for (var i = 0; i < _n; i++)
        {
            var t = _t[i];
            if (t.State != RtmpTargetState.Publishing || !t.Enabled) continue;
            if (t.WaitingKeyframe)
            {
                if (!idr || _seqVersion < 0) { t.Gated++; continue; }
                t.WaitingKeyframe = false;
            }
            if (idr && t.SentSeqVersion != _seqVersion)
            {
                if (!SendRaw(t, RtmpWire.CsidVideo, dtsMs, RtmpWire.TypeVideo, _seqTag.AsSpan(0, _seqLen))) continue;
                t.SentSeqVersion = _seqVersion; t.SeqHeaders++;
            }
            if (!SendShared(t, RtmpWire.CsidVideo, dtsMs, RtmpWire.TypeVideo, body, hash)) continue;
            t.VideoFrames++;
            if (idr) t.Keyframes++;
        }
        Messages++;
        var us = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
        LastMuxUs = us; if (us > MaxMuxUs) MaxMuxUs = us;
        return idr;
    }

    /// <summary>AAC 프레임(ADTS 허용) 1 개 송출 — 비디오 키프레임 합류 이후 타겟만(A/V 시작 정렬).</summary>
    public void WriteAudio(ReadOnlySpan<byte> aac, uint tsMs)
    {
        if (_audioSeqVersion < 0)
        {
            _aacSeqLen = RtmpWire.WriteAacSequenceHeader(_aacSeq, AudioSampleRateIndex, AudioChannels);
            _audioSeqVersion = 0;
        }
        var raw = RtmpWire.StripAdts(aac);
        if (raw.Length == 0 || raw.Length + 2 > _tag.Length) return;
        var tag = _tag.AsSpan();
        tag[0] = 0xAF; tag[1] = 1;
        raw.CopyTo(tag.Slice(2));
        var body = tag.Slice(0, raw.Length + 2);
        var hash = RtmpWire.Fnv(body);
        _chunkSid = uint.MaxValue;
        for (var i = 0; i < _n; i++)
        {
            var t = _t[i];
            if (t.State != RtmpTargetState.Publishing || !t.Enabled || t.WaitingKeyframe) continue;
            if (t.SentAudioSeqVersion != _audioSeqVersion)
            {
                if (!SendRaw(t, RtmpWire.CsidAudio, tsMs, RtmpWire.TypeAudio, _aacSeq.AsSpan(0, _aacSeqLen))) continue;
                t.SentAudioSeqVersion = _audioSeqVersion; t.SeqHeaders++;
            }
            if (SendShared(t, RtmpWire.CsidAudio, tsMs, RtmpWire.TypeAudio, body, hash)) t.AudioFrames++;
        }
        Messages++;
    }

    /// <summary>모든 타겟 서버 응답 소화(Ping/Pong · 끊김 감지).</summary>
    public void Service()
    {
        for (var i = 0; i < _n; i++)
        {
            var t = _t[i];
            if (t.State != RtmpTargetState.Publishing) continue;
            if (!t.Transport.Connected) { Fail(t, "전송 끊김"); continue; }
            try { Pump(t); }
            catch (InvalidOperationException ex) { Fail(t, ex.Message); }
        }
    }

    /// <summary>공유 청크 버퍼(스트림 id 별 1 회 직렬화) → 타겟 전송. 거절/끊김 시 다음 IDR 까지 재게이트.</summary>
    private bool SendShared(RtmpTarget t, int csid, uint ts, byte type, ReadOnlySpan<byte> body, ulong hash)
    {
        if (_chunkSid != t.StreamId)
        {
            _chunkLen = RtmpWire.WriteMessage(_chunk, csid, ts, type, t.StreamId, body, RtmpWire.OutChunk);
            _chunkSid = t.StreamId;
            ChunkBuilds++;
        }
        else Fanouts++;
        var r = t.Transport.Send(_chunk.AsSpan(0, _chunkLen));
        if (r <= 0) { Drop(t, r); return false; }
        t.BytesSent += r;
        t.PayloadHash = (t.PayloadHash ^ hash) * 1099511628211UL;
        return true;
    }

    private bool SendRaw(RtmpTarget t, int csid, uint ts, byte type, ReadOnlySpan<byte> body)
    {
        var o = _ctl.AsSpan();
        var n = RtmpWire.WriteMessage(o, csid, ts, type, t.StreamId, body, RtmpWire.OutChunk);
        var r = t.Transport.Send(o.Slice(0, n));
        if (r <= 0) { Drop(t, r); return false; }
        t.BytesSent += r;
        t.PayloadHash = RtmpWire.Chain(t.PayloadHash, body);
        return true;
    }

    private static void Drop(RtmpTarget t, int r)
    {
        t.Drops++;
        if (!t.WaitingKeyframe) { t.WaitingKeyframe = true; t.Regates++; }
        t.SentSeqVersion = t.SentAudioSeqVersion = -1;
        if (r < 0) Fail(t, "전송 끊김");
    }
}
