using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PromptFlowStudio.Services.VTuber;

namespace PromptFlowStudio.Services.Xr;

public enum AudienceInputKind : byte { Join, Leave, Lightstick, Cheer, Emote }

public enum AudienceEmote : byte { Heart, Clap, Jump, Wave, Fire, Cry }

/// <summary>관객 입력 — Lightstick: A/B/C = 가속도 xyz(g) · Cheer: A = 음량 0~1 · Join: A = 응원봉 색상(0~1).</summary>
public readonly record struct AudienceInput(uint UserId, AudienceInputKind Kind, AudienceEmote Emote = AudienceEmote.Heart, float A = 0, float B = 0, float C = 0);

/// <summary>
/// [PromptFlow Studio v16.40.0] 🙌 다자간 가상 관객 실시간 인터랙션 브릿지.
///   ▸ 입력: UDP 24B 바이너리("PFAU" · uid · kind · emote · xyz) · 채팅 명령(!하트/!박수/!점프/!함성 …) · 내장 군중 시뮬레이터.
///   ▸ 구역 8 × 좌석 256(구역 순환 배정) · 링 버퍼 수집(락 · 상한 초과 시 최고령 드롭) → 20Hz 집계(무할당):
///     응원봉 위상 동기도(Kuramoto 질서 변수) · 함성 dB · 이모트 감쇠 카운트 · 구역 열기 · 열지도 스펙트럼(구역 × 32 이력) · 하이프 지수.
///   ▸ 하이프 히스테리시스 돌파/하트 폭주 → v16.40.0 무대 오케스트레이터 폭죽·컨페티·워시 즉시 발화 · 관객 상태 JSON → XR 홀로그램 SSE.
/// </summary>
public sealed class MultiUserVirtualAudienceBridge : IDisposable
{
    public const int Sections = 8, SeatsPerSection = 256, Capacity = Sections * SeatsPerSection, History = 32, RingSize = 4096, PacketSize = 24, EmoteKinds = 6;
    public const int DefaultUdpPort = 17863;
    public const float HypeOn = 0.8f, HypeOff = 0.55f;
    public static readonly string[] EmoteIcons = { "💖", "👏", "🦘", "👋", "🔥", "😭" };

    private readonly VirtualConcertStageOrchestrator? _stage;
    private readonly VolumetricVideoHologramStreamer? _holo;
    private readonly object _gate = new();
    private readonly AudienceInput[] _ring = new AudienceInput[RingSize];
    private int _head, _count;
    private readonly Dictionary<uint, int> _seatOf = new(Capacity);
    private readonly uint[] _seatUser = new uint[Capacity];
    private readonly bool[] _occupied = new bool[Capacity];
    private readonly float[] _phase = new float[Capacity], _stickEnergy = new float[Capacity], _cheer = new float[Capacity], _stickHue = new float[Capacity];
    private readonly float[] _sectionHeat = new float[Sections];
    private readonly float[] _heat = new float[Sections * History];
    private readonly float[] _emotes = new float[EmoteKinds];
    private readonly ArrayBufferWriter<byte> _json = new(2048);
    private readonly Utf8JsonWriter _writer;
    private int _heatCol, _connected, _nextSeat, _burstEmote = -1;
    private float _cheerLevel, _sync, _hype;
    private bool _hypeLatched;
    private long _processed, _dropped, _triggers, _udpPackets, _ticks;
    private Socket? _udp;
    private CancellationTokenSource? _cts;
    private System.Threading.Timer? _timer;
    private long _lastRaise;
    private uint _rng = 0x2545F491u;
    private int _simCount, _ticking;
    private float _simHype;
    private double _simClock;

    public MultiUserVirtualAudienceBridge(IServiceProvider sp)
        : this(sp.GetService(typeof(VirtualConcertStageOrchestrator)) as VirtualConcertStageOrchestrator,
               sp.GetService(typeof(VolumetricVideoHologramStreamer)) as VolumetricVideoHologramStreamer) { }

    public MultiUserVirtualAudienceBridge(VirtualConcertStageOrchestrator? stage, VolumetricVideoHologramStreamer? holo)
    {
        _stage = stage; _holo = holo;
        _writer = new Utf8JsonWriter(_json);
    }

    public event Action? OnChange;

    public bool IsRunning => _timer is not null;
    public bool UdpActive => _udp is not null;
    public int UdpPort { get; private set; }
    public int Connected { get { lock (_gate) return _connected; } }
    public float CheerLevel => _cheerLevel;
    public float CheerDb => _cheerLevel <= 1e-4f ? -60f : MathF.Max(-60f, 20f * MathF.Log10(_cheerLevel));
    public float SyncRatio => _sync;
    public float Hype => _hype;
    public long Processed => Interlocked.Read(ref _processed);
    public long Dropped => Interlocked.Read(ref _dropped);
    public long Triggers => Interlocked.Read(ref _triggers);
    public long UdpPackets => Interlocked.Read(ref _udpPackets);
    public long Ticks => Interlocked.Read(ref _ticks);
    public int SimulatedAudience => _simCount;
    public bool AutoStageTriggers { get; set; } = true;
    public string? LastAction { get; private set; }

    public float EmoteRate(AudienceEmote e) { lock (_gate) return _emotes[(int)e]; }
    public float SectionHeat(int s) { lock (_gate) return (uint)s < Sections ? _sectionHeat[s] : 0f; }

    /// <summary>열지도(구역 × 이력, 가장 오래된 열 → 최신 열 순) 복사.</summary>
    public void CopyHeatmap(Span<float> dst)
    {
        lock (_gate)
        {
            for (var s = 0; s < Sections; s++)
                for (var h = 0; h < History; h++)
                {
                    var src = (_heatCol + h) % History;
                    var i = s * History + h;
                    if (i < dst.Length) dst[i] = _heat[s * History + src];
                }
        }
    }

    // ══════════════════════ 수집 ══════════════════════

    /// <summary>입력 적재(무할당 · 링 상한 초과 시 최고령 드롭).</summary>
    public void Post(in AudienceInput input)
    {
        lock (_gate)
        {
            if (_count == RingSize) { _head = (_head + 1) % RingSize; _count--; Interlocked.Increment(ref _dropped); }
            _ring[(_head + _count) % RingSize] = input;
            _count++;
        }
    }

    /// <summary>UDP 24B 패킷 디코딩: "PFAU" · uid u32 · kind u8 · emote u8 · pad 2 · A/B/C f32(LE).</summary>
    public static bool TryDecodePacket(ReadOnlySpan<byte> p, out AudienceInput input)
    {
        input = default;
        if (p.Length < PacketSize || !p[..4].SequenceEqual("PFAU"u8) || p[8] > (byte)AudienceInputKind.Emote || p[9] >= EmoteKinds) return false;
        input = new AudienceInput(BinaryPrimitives.ReadUInt32LittleEndian(p[4..]), (AudienceInputKind)p[8], (AudienceEmote)p[9],
            BinaryPrimitives.ReadSingleLittleEndian(p[12..]), BinaryPrimitives.ReadSingleLittleEndian(p[16..]), BinaryPrimitives.ReadSingleLittleEndian(p[20..]));
        return float.IsFinite(input.A) && float.IsFinite(input.B) && float.IsFinite(input.C);
    }

    public static void EncodePacket(in AudienceInput i, Span<byte> d)
    {
        "PFAU"u8.CopyTo(d);
        BinaryPrimitives.WriteUInt32LittleEndian(d[4..], i.UserId);
        d[8] = (byte)i.Kind; d[9] = (byte)i.Emote; d[10] = 0; d[11] = 0;
        BinaryPrimitives.WriteSingleLittleEndian(d[12..], i.A);
        BinaryPrimitives.WriteSingleLittleEndian(d[16..], i.B);
        BinaryPrimitives.WriteSingleLittleEndian(d[20..], i.C);
    }

    public static uint UserIdOf(string fanId)
    {
        var h = 2166136261u;
        foreach (var ch in fanId) { h ^= ch; h *= 16777619u; }
        return h == 0 ? 1u : h;
    }

    /// <summary>채팅 명령 → 관객 입력(자동 입장). 인식 시 true.</summary>
    public bool PostChat(ChatMessage m)
    {
        var t = m.Text.AsSpan().Trim();
        if (t.Length < 2 || t[0] != '!') return false;
        var cmd = t[1..];
        var sp = cmd.IndexOf(' ');
        if (sp >= 0) cmd = cmd[..sp];
        var uid = UserIdOf(m.FanId.Length > 0 ? m.FanId : m.FanName);
        AudienceInput input;
        if (Is(cmd, "하트", "heart")) input = new(uid, AudienceInputKind.Emote, AudienceEmote.Heart);
        else if (Is(cmd, "박수", "clap")) input = new(uid, AudienceInputKind.Emote, AudienceEmote.Clap);
        else if (Is(cmd, "점프", "jump")) input = new(uid, AudienceInputKind.Emote, AudienceEmote.Jump);
        else if (Is(cmd, "손", "wave")) input = new(uid, AudienceInputKind.Emote, AudienceEmote.Wave);
        else if (Is(cmd, "불꽃", "fire")) input = new(uid, AudienceInputKind.Emote, AudienceEmote.Fire);
        else if (Is(cmd, "감동", "cry")) input = new(uid, AudienceInputKind.Emote, AudienceEmote.Cry);
        else if (Is(cmd, "함성", "cheer")) input = new(uid, AudienceInputKind.Cheer, A: 0.9f);
        else return false;
        Post(new AudienceInput(uid, AudienceInputKind.Join, A: (uid % 360) / 360f));
        Post(input);
        return true;

        static bool Is(ReadOnlySpan<char> c, string ko, string en)
            => c.Equals(ko, StringComparison.Ordinal) || c.Equals(en, StringComparison.OrdinalIgnoreCase);
    }

    // ══════════════════════ 집계(무할당) ══════════════════════

    private int SeatFor(uint uid, bool create)
    {
        if (_seatOf.TryGetValue(uid, out var seat)) return seat;
        if (!create || _connected >= Capacity) return -1;
        for (var probe = 0; probe < Capacity; probe++)
        {
            var s = _nextSeat;
            _nextSeat = (_nextSeat + SeatsPerSection + 1) % Capacity;
            if (_occupied[s]) continue;
            _occupied[s] = true; _seatUser[s] = uid; _seatOf[uid] = s;
            _phase[s] = 0; _stickEnergy[s] = 0; _cheer[s] = 0;
            _connected++;
            return s;
        }
        return -1;
    }

    private void Apply(in AudienceInput i)
    {
        switch (i.Kind)
        {
            case AudienceInputKind.Join:
                var js = SeatFor(i.UserId, true);
                if (js >= 0) _stickHue[js] = Math.Clamp(i.A, 0f, 1f);
                return;
            case AudienceInputKind.Leave:
                if (_seatOf.Remove(i.UserId, out var ls)) { _occupied[ls] = false; _connected--; }
                return;
        }
        var seat = SeatFor(i.UserId, true);
        if (seat < 0) return;
        var sec = seat / SeatsPerSection;
        switch (i.Kind)
        {
            case AudienceInputKind.Lightstick:
                var mag = MathF.Sqrt(i.A * i.A + i.B * i.B + i.C * i.C);
                _stickEnergy[seat] = Math.Clamp(mag / 2f, 0f, 1f);
                _phase[seat] = MathF.Atan2(i.B, i.A);
                _sectionHeat[sec] += 0.02f * _stickEnergy[seat];
                break;
            case AudienceInputKind.Cheer:
                _cheer[seat] = Math.Clamp(i.A, 0f, 1f);
                _sectionHeat[sec] += 0.03f * _cheer[seat];
                break;
            case AudienceInputKind.Emote:
                var e = (int)i.Emote;
                if ((uint)e < EmoteKinds) _emotes[e] += 1f;
                _sectionHeat[sec] += 0.05f;
                break;
        }
    }

    /// <summary>20Hz 집계: 링 드레인 → 감쇠 · 동기도 · 함성 · 하이프 · 열지도 열 기록 · 무대 트리거. 반환 = 처리 입력 수.</summary>
    public int Tick(double dtSec)
    {
        int processed;
        StageFx trigger = StageFx.Wash;
        var fire = false;
        lock (_gate)
        {
            if (_simCount > 0) SimulateStep(dtSec);
            processed = _count;
            while (_count > 0)
            {
                Apply(in _ring[_head]);
                _head = (_head + 1) % RingSize;
                _count--;
            }
            var decay = MathF.Exp(-(float)dtSec / 1.5f);
            var fast = MathF.Exp(-(float)dtSec / 0.4f);
            double sx = 0, sy = 0, cheerSum = 0;
            var sticks = 0;
            for (var s = 0; s < Capacity; s++)
            {
                if (!_occupied[s]) continue;
                _cheer[s] *= fast;
                cheerSum += _cheer[s];
                if (_stickEnergy[s] > 0.05f) { sx += MathF.Cos(_phase[s]); sy += MathF.Sin(_phase[s]); sticks++; }
                _stickEnergy[s] *= decay;
            }
            _sync = sticks == 0 ? 0f : (float)(Math.Sqrt(sx * sx + sy * sy) / sticks);
            _cheerLevel = _connected == 0 ? 0f : (float)Math.Min(1.0, cheerSum / _connected * 1.4);
            var emoteTotal = 0f;
            var top = -1; var topV = 0f;
            for (var e = 0; e < EmoteKinds; e++)
            {
                _emotes[e] *= decay;
                emoteTotal += _emotes[e];
                if (_emotes[e] > topV) { topV = _emotes[e]; top = e; }
            }
            var emoteNorm = _connected == 0 ? 0f : Math.Min(1f, emoteTotal / Math.Max(4f, _connected * 0.25f));
            var stickShare = _connected == 0 ? 0f : Math.Min(1f, sticks / (float)_connected);
            var target = 0.4f * _cheerLevel + 0.35f * _sync * stickShare + 0.25f * emoteNorm;
            _hype += (target - _hype) * Math.Min(1f, (float)dtSec * 3f);
            for (var s = 0; s < Sections; s++)
            {
                _sectionHeat[s] = Math.Clamp(_sectionHeat[s] * decay, 0f, 1f);
                _heat[s * History + _heatCol] = _sectionHeat[s];
            }
            _heatCol = (_heatCol + 1) % History;

            if (!_hypeLatched && _hype >= HypeOn) { _hypeLatched = true; fire = true; trigger = StageFx.Fireworks; }
            else if (_hypeLatched && _hype <= HypeOff) _hypeLatched = false;
            _burstEmote = top >= 0 && topV >= Math.Max(6f, _connected * 0.1f) ? top : -1;
            if (!fire && _burstEmote == (int)AudienceEmote.Heart && _emotes[0] >= Math.Max(12f, _connected * 0.3f)) { fire = true; trigger = StageFx.Confetti; _emotes[0] *= 0.3f; }
        }
        if (processed > 0) Interlocked.Add(ref _processed, processed);
        Interlocked.Increment(ref _ticks);
        if (fire && AutoStageTriggers && _stage is not null)
        {
            _stage.Fire(trigger);
            if (trigger == StageFx.Fireworks) _stage.Fire(StageFx.Confetti, 0.8f);
            Interlocked.Increment(ref _triggers);
            LastAction = trigger == StageFx.Fireworks ? "🎆 관객 하이프 돌파 → 폭죽 · 컨페티" : "💖 하트 폭주 → 컨페티";
        }
        if (_holo is { IsRunning: true } h) h.PublishState(SerializeState());
        Raise(fire);
        return processed;
    }

    /// <summary>관객 상태 JSON(재사용 버퍼 · 무할당 · 집계 스레드 전용).</summary>
    public ReadOnlySpan<byte> SerializeState()
    {
        _json.ResetWrittenCount();
        _writer.Reset(_json);
        _writer.WriteStartObject();
        _writer.WriteString("type"u8, "crowd"u8);
        lock (_gate)
        {
            _writer.WriteNumber("n"u8, _connected);
            _writer.WriteNumber("hype"u8, MathF.Round(_hype * 100f) / 100f);
            _writer.WriteNumber("sync"u8, MathF.Round(_sync * 100f) / 100f);
            _writer.WriteNumber("cheer"u8, MathF.Round(_cheerLevel * 100f) / 100f);
            if (_burstEmote >= 0) _writer.WriteString("burst"u8, EmoteIcons[_burstEmote]);
            _writer.WriteStartArray("heat"u8);
            for (var s = 0; s < Sections; s++) _writer.WriteNumberValue(MathF.Round(_sectionHeat[s] * 100f) / 100f);
            _writer.WriteEndArray();
        }
        _writer.WriteEndObject();
        _writer.Flush();
        return _json.WrittenSpan;
    }

    // ══════════════════════ 시뮬레이터 ══════════════════════

    /// <summary>군중 시뮬레이터(count 명 · hype 0~1 → 동기 응원봉 · 함성 · 이모트 빈도). count 0 = 해제.</summary>
    public void Simulate(int count, double hype)
    {
        lock (_gate)
        {
            _simCount = Math.Clamp(count, 0, Capacity);
            _simHype = (float)Math.Clamp(hype, 0, 1);
            for (var i = 0; i < _simCount; i++) SeatFor(0x5000_0000u + (uint)i, true);
            for (var i = _simCount; i < Capacity; i++)
                if (_seatOf.Remove(0x5000_0000u + (uint)i, out var s)) { _occupied[s] = false; _connected--; }
        }
        LastAction = count > 0 ? "🧪 가상 관객 " + count.ToString(CultureInfo.InvariantCulture) + "명 · 열기 " + (hype * 100).ToString("0", CultureInfo.InvariantCulture) + "%" : "🧪 시뮬레이터 해제";
        Raise(true);
    }

    private float Rand() { _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5; return (_rng & 0xFFFFFF) / 16777216f; }

    private void SimulateStep(double dt)
    {
        _simClock += dt;
        var bpm = _stage is { Bpm: > 0 } st ? st.Bpm : 128.0;
        var beatPhase = (float)(_simClock * bpm / 60.0 * Math.Tau);
        var h = _simHype;
        for (var i = 0; i < _simCount; i++)
        {
            if (!_seatOf.TryGetValue(0x5000_0000u + (uint)i, out var s)) continue;
            var jitter = (Rand() - 0.5f) * MathF.Tau * (1f - h);
            _phase[s] = beatPhase + jitter;
            _stickEnergy[s] = 0.4f + 0.6f * h * Rand();
            _sectionHeat[s / SeatsPerSection] += 0.004f * h;
            if (Rand() < h * 0.5f) _cheer[s] = Math.Max(_cheer[s], h * (0.6f + 0.4f * Rand()));
            if (Rand() < h * dt * 0.6f) _emotes[(int)(Rand() * EmoteKinds) % EmoteKinds] += 1f;
        }
    }

    // ══════════════════════ 수명 ══════════════════════

    /// <summary>20Hz 집계 타이머 + (선택) UDP 수신 시작. udpPort 0 = 임의 포트 · -1 = UDP 미사용.</summary>
    public void Start(int udpPort = DefaultUdpPort)
    {
        if (_timer is not null) return;
        if (udpPort >= 0) StartUdp(udpPort);
        _timer = new System.Threading.Timer(_ =>
        {
            if (Interlocked.Exchange(ref _ticking, 1) != 0) return;
            try { Tick(0.05); }
            catch (Exception ex) { Program.Log("Audience.Tick", ex); }
            finally { Volatile.Write(ref _ticking, 0); }
        }, null, 50, 50);
        LastAction = "🙌 관객 브릿지 ON" + (UdpActive ? " · UDP " + UdpPort.ToString(CultureInfo.InvariantCulture) : "");
        Raise(true);
    }

    private void StartUdp(int port)
    {
        try
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            if (OperatingSystem.IsWindows()) { try { s.IOControl(unchecked((int)0x9800000C), new byte[4], null); } catch (Exception ex) { Program.Log("Audience.ConnReset", ex); } }
            s.Bind(new IPEndPoint(IPAddress.Loopback, port));
            UdpPort = ((IPEndPoint)s.LocalEndPoint!).Port;
            _udp = s;
            var cts = new CancellationTokenSource();
            _cts = cts;
            _ = ReceiveLoopAsync(s, cts.Token);
        }
        catch (Exception ex) { Program.Log("Audience.Udp", ex); LastAction = "⚠ UDP 바인딩 실패 · 채팅/시뮬 전용"; }
    }

    private async Task ReceiveLoopAsync(Socket s, CancellationToken ct)
    {
        var buf = new byte[512];
        while (!ct.IsCancellationRequested)
        {
            int n;
            try { n = await s.ReceiveAsync(buf.AsMemory(), SocketFlags.None, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { break; }
            catch (SocketException) { try { await Task.Delay(50, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; } continue; }
            for (var o = 0; o + PacketSize <= n; o += PacketSize)
                if (TryDecodePacket(buf.AsSpan(o, PacketSize), out var input)) { Post(in input); Interlocked.Increment(ref _udpPackets); }
        }
    }

    public void Stop()
    {
        var t = Interlocked.Exchange(ref _timer, null);
        if (t is null) return;
        t.Dispose();
        var cts = Interlocked.Exchange(ref _cts, null);
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        cts?.Dispose();
        Interlocked.Exchange(ref _udp, null)?.Dispose();
        LastAction = "⏹ 관객 브릿지 OFF";
        Raise(true);
    }

    private void Raise(bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now - Interlocked.Read(ref _lastRaise) < 250) return;
        Interlocked.Exchange(ref _lastRaise, now);
        try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("Audience.OnChange", ex); }
    }

    public void Dispose() { Stop(); _writer.Dispose(); }
}
