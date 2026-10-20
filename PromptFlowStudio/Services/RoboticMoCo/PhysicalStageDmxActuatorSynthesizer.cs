using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using PromptFlowStudio.Services.Atmosphere;
using PromptFlowStudio.Services.GenerativeVfx;

namespace PromptFlowStudio.Services.RoboticMoCo;

/// <summary>물리 무대 장치 종류(채널 풋프린트 고정).</summary>
public enum FixtureKind : byte { ParRgbw, MovingHead, Strobe, FogMachine, Turntable, WindFan, WorkLight, Co2Jet }

/// <summary>패치된 장치(유니버스 0..3 · 시작 주소 1..512).</summary>
public struct StageFixture
{
    public string Name;
    public FixtureKind Kind;
    public int Universe, Address;
    public Vector3 Position;
}

/// <summary>가상 → 물리 무대 큐(값 형식): 키/필/사이클로라마 색(선형 0..1) · 강도 · 포그 · 바람 · 번개 플래시 · 턴테이블.</summary>
public struct PhysicalStageCue
{
    public Vector3 KeyColor, FillColor, CycColor;
    public float KeyIntensity, FillIntensity, CycIntensity, Fog, Wind, Flash, TurntableRpm;
    public Vector3 AimTarget;

    public static PhysicalStageCue Default => new()
    {
        KeyColor = new Vector3(1f, 0.92f, 0.8f), FillColor = new Vector3(0.6f, 0.7f, 1f), CycColor = new Vector3(0.3f, 0.45f, 0.9f),
        KeyIntensity = 0.8f, FillIntensity = 0.35f, CycIntensity = 0.5f, AimTarget = new Vector3(2.3f, 1.4f, 0.2f),
    };
}

/// <summary>[PromptFlow Studio v17.01.0] DMX 채널 슬루 SIMD(Vector&lt;float&gt; · 스칼라 경로와 비트 동일).</summary>
public static class DmxSimd
{
    /// <summary>cur += clamp(target − cur, ±rate·dt).</summary>
    public static void Slew(Span<float> cur, ReadOnlySpan<float> target, ReadOnlySpan<float> rate, float dt)
    {
        var n = cur.Length; var i = 0;
        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            var m = n - n % Vector<float>.Count;
            Vector<float> vdt = new(dt);
            var c = MemoryMarshal.Cast<float, Vector<float>>(cur[..m]);
            var t = MemoryMarshal.Cast<float, Vector<float>>(target[..m]); var r = MemoryMarshal.Cast<float, Vector<float>>(rate[..m]);
            for (var k = 0; k < c.Length; k++) { var lim = r[k] * vdt; c[k] = c[k] + Vector.Min(Vector.Max(t[k] - c[k], -lim), lim); }
            i = m;
        }
        for (; i < n; i++) { var lim = rate[i] * dt; cur[i] = cur[i] + MathF.Min(MathF.Max(target[i] - cur[i], -lim), lim); }
    }

    public static void SlewScalar(Span<float> cur, ReadOnlySpan<float> target, ReadOnlySpan<float> rate, float dt)
    {
        for (var i = 0; i < cur.Length; i++) { var lim = rate[i] * dt; cur[i] = cur[i] + MathF.Min(MathF.Max(target[i] - cur[i], -lim), lim); }
    }
}

/// <summary>
/// [PromptFlow Studio v17.01.0] 💡 물리 스테이지 DMX512 &amp; Art-Net 액추에이터 합성기(무할당 · 결정적).
///   ▸ 패치: 4 유니버스 × 512 채널 · 장치 64(RGBW 파 6ch · 무빙헤드 12ch(팬/틸트 16비트) · 스트로브 · 포그 · 턴테이블(16비트 속도) · 윈드 팬 · 워크 라이트 · CO2 제트).
///   ▸ 소스: v16.84 대기 조명(태양 직사 → 키 · 환경광 → 필 · 지평선 → 사이클로라마 · 안개 → 포그) · 미세 기상(번개 → 스트로브 · 풍속/돌풍 → 팬) · v16.81 VFX 충돌(에너지 → 플래시 · 폭발 → CO2 펄스).
///   ▸ 출력: 채널 목표 → SIMD 슬루(채널별 페이드율) → 8비트 양자화 → 안전 상태(E-Stop) 즉시 강제(포그/CO2/스트로브/턴테이블 0 · 워크 라이트 100%) →
///     Art-Net ArtDmx(18 B 헤더 · 시퀀스 1..255 · Net/SubUni 포트 주소) 변경 시 송신 + 0.8 s 킵얼라이브 → ArtSync 프레임 동기 · Enttec USB Pro 프레임 · DMX512 직렬 타이밍.
///   ▸ 루프백 노드: 패킷 검증(ID/OpCode/ProtVer/길이) → 유니버스 버퍼 → ArtSync 래치(프레임 동시 출력) · 시퀀스 결손 계측.
/// </summary>
public sealed class PhysicalStageDmxActuatorSynthesizer
{
    public const int Universes = 4, Slots = 512, Channels = Universes * Slots, MaxFixtures = 64;
    public const int ArtHeader = 18, ArtDmxBytes = ArtHeader + Slots, ArtSyncBytes = 14, EnttecBytes = 5 + 1 + Slots;
    public const ushort OpDmx = 0x5000, OpSync = 0x5200, ProtVer = 14;
    public const float KeepAliveS = 0.8f, BreakUs = 176f, MabUs = 12f, SlotUs = 44f;
    public static ReadOnlySpan<byte> ArtId => "Art-Net\0"u8;
    public static readonly string[] KindNames = { "RGBW 파", "무빙 헤드", "스트로브", "포그 머신", "턴테이블", "윈드 팬", "워크 라이트", "CO2 제트" };
    private static readonly double TickUs = 1e6 / Stopwatch.Frequency;

    private readonly object _gate = new();
    private readonly StageFixture[] _fx = new StageFixture[MaxFixtures];
    private readonly float[] _target = new float[Channels], _level = new float[Channels], _rate = new float[Channels];
    private readonly byte[] _dmx = new byte[Channels], _sent = new byte[Channels], _node = new byte[Channels], _latched = new byte[Channels];
    private readonly byte[] _packet = new byte[ArtDmxBytes], _sync = new byte[ArtSyncBytes];
    private readonly byte[] _seq = new byte[Universes], _nodeSeq = new byte[Universes];
    private readonly bool[] _patched = new bool[Universes];
    private readonly float[] _lastSend = new float[Universes];
    private readonly float[] _pan = new float[MaxFixtures], _tilt = new float[MaxFixtures];
    private readonly long[] _txPackets = new long[Universes];
    private int _fixtures;
    private float _flashEnv, _co2Timer, _lastVfxTime = float.NegativeInfinity;
    private PhysicalStageCue _cue = PhysicalStageCue.Default;
    private AtmosphericScatteringLightingEngine? _atmo;
    private DynamicMicroWeatherSimulatorEngine? _weather;
    private InteractiveVfxCollisionSplashGuard? _vfx;

    public PhysicalStageDmxActuatorSynthesizer()
    {
        _rate.AsSpan().Fill(1e9f);
        _sent.AsSpan().Fill(0xFF);
        _lastSend.AsSpan().Fill(-1e9f);
    }

    // ── 설정/상태 ──
    public bool SafeState { get; set; }
    public float GrandMaster { get; set; } = 1f;
    public int FixtureCount => _fixtures;
    public ref readonly StageFixture Fixture(int i) => ref _fx[i];
    public PhysicalStageCue Cue { get { lock (_gate) return _cue; } }
    public bool AtmosphereBound => _atmo is not null;
    public bool WeatherBound => _weather is not null;
    public bool VfxBound => _vfx is not null;
    // ── 계측 ──
    public long Frames { get; private set; }
    public long PacketsSent { get; private set; }
    public long Suppressed { get; private set; }
    public long KeepAlives { get; private set; }
    public long Syncs { get; private set; }
    public long BytesSent { get; private set; }
    public long Flashes { get; private set; }
    public long Co2Bursts { get; private set; }
    public long SafeFrames { get; private set; }
    public long NodeReceived { get; private set; }
    public long NodeRejected { get; private set; }
    public long NodeSeqGaps { get; private set; }
    public long NodeLatches { get; private set; }
    public double LastRenderUs { get; private set; }
    public float FlashEnvelope => _flashEnv;
    public bool Patched(int u) => _patched[u];
    public long UniversePackets(int u) => _txPackets[u];
    public byte Sequence(int u) => _seq[u];
    public byte Channel(int universe, int address) => _dmx[universe * Slots + address - 1];
    public byte NodeChannel(int universe, int address) => _latched[universe * Slots + address - 1];
    public ReadOnlySpan<byte> UniverseData(int u) => _dmx.AsSpan(u * Slots, Slots);
    public ReadOnlySpan<byte> NodeData(int u) => _latched.AsSpan(u * Slots, Slots);
    public ReadOnlySpan<byte> LastPacket => _packet;

    public static int Footprint(FixtureKind k) => k switch
    {
        FixtureKind.ParRgbw => 6, FixtureKind.MovingHead => 12, FixtureKind.Strobe => 2, FixtureKind.FogMachine => 2,
        FixtureKind.Turntable => 4, _ => 1,
    };

    /// <summary>DMX512 직렬 프레임 시간(µs): 브레이크 + MAB + (시작 코드 + 슬롯) × 44 µs.</summary>
    public static float FrameTimeUs(int slots) => BreakUs + MabUs + SlotUs * (1 + slots);
    public static float MaxRefreshHz(int slots) => 1e6f / FrameTimeUs(slots);

    public void Bind(AtmosphericScatteringLightingEngine? atmo, DynamicMicroWeatherSimulatorEngine? weather, InteractiveVfxCollisionSplashGuard? vfx)
    {
        lock (_gate) { _atmo = atmo; _weather = weather; _vfx = vfx; }
    }

    /// <summary>장치 패치(겹침/범위 초과 = −1).</summary>
    public int Patch(string name, FixtureKind kind, int universe, int address, Vector3 position)
    {
        lock (_gate)
        {
            var fp = Footprint(kind);
            if (_fixtures >= MaxFixtures || (uint)universe >= Universes || address < 1 || address + fp - 1 > Slots) return -1;
            for (var i = 0; i < _fixtures; i++)
            {
                ref readonly var o = ref _fx[i];
                if (o.Universe == universe && address < o.Address + Footprint(o.Kind) && o.Address < address + fp) return -1;
            }
            var id = _fixtures++;
            _fx[id] = new StageFixture { Name = name, Kind = kind, Universe = universe, Address = address, Position = position };
            _patched[universe] = true;
            var b = universe * Slots + address - 1;
            // 채널 페이드율(단위/s): 강도/색 = 0.25 s 전 범위 · 팬 = 1 s · 스트로브/밸브/16비트/방향 = 즉시
            switch (kind)
            {
                case FixtureKind.ParRgbw: for (var c = 0; c < 5; c++) _rate[b + c] = 1020f; break;
                case FixtureKind.MovingHead: for (var c = 4; c < 9; c++) _rate[b + c] = 1020f; _rate[b + 9] = 510f; break;
                case FixtureKind.WindFan: _rate[b] = 255f; break;
                case FixtureKind.WorkLight: _rate[b] = 1020f; break;
                case FixtureKind.FogMachine: _rate[b + 1] = 255f; break;
            }
            return id;
        }
    }

    public void SetCue(in PhysicalStageCue cue) { lock (_gate) _cue = cue; }

    /// <summary>VFX 플래시 펄스(에너지 0..1 · 폭발 = CO2 0.3 s).</summary>
    public void Pulse(float energy, bool explosion)
    {
        lock (_gate)
        {
            _flashEnv = MathF.Max(_flashEnv, Math.Clamp(energy, 0f, 1f)); Flashes++;
            if (explosion && !SafeState) { _co2Timer = 0.3f; Co2Bursts++; }
        }
    }

    /// <summary>결선 엔진 → 큐(대기 조명은 LUT 구축 이후만 · 미결선 항목은 현 큐 유지).</summary>
    public void PullFromEngines()
    {
        lock (_gate)
        {
            if (_atmo is { LutBuilds: > 0 } at)
            {
                var sun = at.SunIrradiance; var amb = at.AmbientIrradiance;
                _cue.KeyColor = Chroma(sun); _cue.KeyIntensity = Math.Clamp(MathF.Sqrt(at.GroundLux / AtmosphericScatteringLightingEngine.SunLux), 0f, 1f);
                _cue.FillColor = Chroma(amb); _cue.FillIntensity = Math.Clamp(0.15f + 0.6f * (1f - at.NightFactor), 0f, 1f) * 0.6f;
                _cue.CycColor = Chroma(at.HorizonColor); _cue.CycIntensity = Math.Clamp(0.2f + 0.8f * (1f - at.NightFactor), 0f, 1f);
                _cue.Fog = Math.Clamp(at.FogDensity / 0.05f, 0f, 1f);
            }
            if (_weather is { } we)
            {
                var s = we.State;
                _cue.Wind = Math.Clamp((s.WindSpeed + we.Gust) / 15f, 0f, 1f);
                _cue.Flash = we.FlashIntensity;
                _cue.Fog = MathF.Max(_cue.Fog, Math.Clamp(s.Fog, 0f, 1f));
            }
            if (_vfx is { } vfx)
            {
                var newest = _lastVfxTime;
                for (var k = 0; k < vfx.ImpactCount; k++)
                {
                    var im = vfx.Impact(k);
                    if (im.Time <= _lastVfxTime) break;
                    newest = MathF.Max(newest, im.Time);
                    _flashEnv = MathF.Max(_flashEnv, Math.Clamp(im.Energy, 0f, 1f)); Flashes++;
                    if (im.Energy > 0.6f && !SafeState) { _co2Timer = 0.3f; Co2Bursts++; }
                }
                _lastVfxTime = newest;
            }
        }
    }

    private static Vector3 Chroma(Vector3 c)
    {
        var m = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        return m <= 1e-12f ? Vector3.Zero : c / m;
    }

    private static float Gamma(float linear) => 255f * MathF.Pow(Math.Clamp(linear, 0f, 1f), 1f / 2.2f);

    /// <summary>1프레임(무할당): 큐 → 채널 목표 → SIMD 슬루 → 양자화 → 안전 강제 → Art-Net(변경/킵얼라이브) → ArtSync → 노드 래치.</summary>
    public void Render(float dt, float now)
    {
        lock (_gate)
        {
            var t0 = Stopwatch.GetTimestamp();
            Frames++;
            _flashEnv = MathF.Max(0f, _flashEnv - dt / 0.15f);
            _co2Timer = MathF.Max(0f, _co2Timer - dt);
            var flash = MathF.Max(_flashEnv, _cue.Flash);
            var gm = Math.Clamp(GrandMaster, 0f, 1f);
            for (var i = 0; i < _fixtures; i++)
            {
                ref readonly var f = ref _fx[i];
                var b = f.Universe * Slots + f.Address - 1;
                switch (f.Kind)
                {
                    case FixtureKind.ParRgbw:
                    {
                        var cyc = f.Position.Z < -1.5f;   // 무대 뒤 = 사이클로라마
                        var col = cyc ? _cue.CycColor : _cue.FillColor; var inten = (cyc ? _cue.CycIntensity : _cue.FillIntensity) * gm;
                        inten = MathF.Max(inten, flash * gm);
                        var w = MathF.Min(col.X, MathF.Min(col.Y, col.Z));
                        _target[b] = Gamma(inten); _target[b + 1] = Gamma(col.X - w + flash); _target[b + 2] = Gamma(col.Y - w + flash);
                        _target[b + 3] = Gamma(col.Z - w + flash); _target[b + 4] = Gamma(w + flash); _target[b + 5] = 0f;
                        break;
                    }
                    case FixtureKind.MovingHead:
                    {
                        var d = _cue.AimTarget - f.Position;
                        var len = d.Length();
                        if (len > 1e-4f)
                        {
                            d /= len;
                            var pan = MathF.Atan2(d.X, d.Z) * 180f / MathF.PI;            // −180..180
                            var tilt = MathF.Acos(Math.Clamp(-d.Y, -1f, 1f)) * 180f / MathF.PI; // 0 = 직하
                            _pan[i] = Math.Clamp((pan + 270f) / 540f, 0f, 1f);
                            _tilt[i] = Math.Clamp((tilt + 135f) / 270f, 0f, 1f);
                        }
                        var p16 = (ushort)(_pan[i] * 65535f + 0.5f); var t16 = (ushort)(_tilt[i] * 65535f + 0.5f);
                        _target[b] = p16 >> 8; _target[b + 1] = p16 & 0xFF; _target[b + 2] = t16 >> 8; _target[b + 3] = t16 & 0xFF;
                        var col = _cue.KeyColor;
                        var w = MathF.Min(col.X, MathF.Min(col.Y, col.Z));
                        _target[b + 4] = Gamma(_cue.KeyIntensity * gm);
                        _target[b + 5] = Gamma(col.X - w); _target[b + 6] = Gamma(col.Y - w); _target[b + 7] = Gamma(col.Z - w); _target[b + 8] = Gamma(w);
                        _target[b + 9] = 40f + 160f * Math.Clamp(1f - len / 8f, 0f, 1f);   // 줌(가까울수록 넓게)
                        _target[b + 10] = 255f; _target[b + 11] = 0f;                       // 셔터 열림 · 최고 속도
                        break;
                    }
                    case FixtureKind.Strobe:
                        _target[b] = 255f * Math.Clamp(flash, 0f, 1f) * gm; _target[b + 1] = flash > 0.01f ? 255f : 0f;
                        break;
                    case FixtureKind.FogMachine:
                        _target[b] = 255f * Math.Clamp(_cue.Fog, 0f, 1f); _target[b + 1] = _cue.Fog > 0.01f ? 160f : 0f;
                        break;
                    case FixtureKind.Turntable:
                    {
                        var rpm = Math.Clamp(_cue.TurntableRpm, -10f, 10f);
                        var s16 = (ushort)(MathF.Abs(rpm) / 10f * 65535f + 0.5f);
                        _target[b] = s16 >> 8; _target[b + 1] = s16 & 0xFF; _target[b + 2] = rpm < 0f ? 255f : 0f; _target[b + 3] = rpm == 0f ? 255f : 0f;
                        break;
                    }
                    case FixtureKind.WindFan: _target[b] = 255f * Math.Clamp(_cue.Wind, 0f, 1f); break;
                    case FixtureKind.WorkLight: _target[b] = 0f; break;
                    case FixtureKind.Co2Jet: _target[b] = _co2Timer > 0f ? 255f : 0f; break;
                }
            }
            DmxSimd.Slew(_level, _target, _rate, dt);
            for (var i = 0; i < Channels; i++) { var v = _level[i]; _dmx[i] = (byte)(v <= 0f ? 0 : v >= 255f ? 255 : (int)(v + 0.5f)); }
            if (SafeState) { ApplySafeState(); SafeFrames++; }
            // ── Art-Net 송신(변경 또는 킵얼라이브) + ArtSync ──
            var any = false;
            for (var u = 0; u < Universes; u++)
            {
                if (!_patched[u]) continue;
                var cur = _dmx.AsSpan(u * Slots, Slots);
                var changed = !cur.SequenceEqual(_sent.AsSpan(u * Slots, Slots));
                var keep = now - _lastSend[u] >= KeepAliveS;
                if (!changed && !keep) { Suppressed++; continue; }
                if (!changed) KeepAlives++;
                var len = WriteArtDmx(u, _packet);
                cur.CopyTo(_sent.AsSpan(u * Slots, Slots));
                _lastSend[u] = now; PacketsSent++; _txPackets[u]++; BytesSent += len; any = true;
                NodeReceive(_packet.AsSpan(0, len));
            }
            if (any)
            {
                var sl = WriteArtSync(_sync);
                BytesSent += sl; Syncs++;
                NodeReceive(_sync.AsSpan(0, sl));
            }
            LastRenderUs = (Stopwatch.GetTimestamp() - t0) * TickUs;
        }
    }

    /// <summary>E-Stop 안전 상태: 위험 액추에이터 즉시 0(슬루 우회) · 워크 라이트 100% · 레벨도 동기화(해제 시 페이드 인).</summary>
    private void ApplySafeState()
    {
        for (var i = 0; i < _fixtures; i++)
        {
            ref readonly var f = ref _fx[i];
            var b = f.Universe * Slots + f.Address - 1;
            switch (f.Kind)
            {
                case FixtureKind.FogMachine: Force(b, 0); Force(b + 1, 0); break;
                case FixtureKind.Co2Jet: Force(b, 0); _co2Timer = 0f; break;
                case FixtureKind.Strobe: Force(b, 0); Force(b + 1, 0); break;
                case FixtureKind.Turntable: Force(b, 0); Force(b + 1, 0); Force(b + 3, 255); break;
                case FixtureKind.WindFan: Force(b, 0); break;
                case FixtureKind.WorkLight: Force(b, 255); break;
            }
        }
    }

    private void Force(int ch, byte v) { _dmx[ch] = v; _level[ch] = v; }

    /// <summary>ArtDmx(OpCode 0x5000 LE · ProtVer 14 BE · 시퀀스 1..255 · Physical · SubUni · Net · 길이 BE).</summary>
    public int WriteArtDmx(int universe, Span<byte> dst)
    {
        if (dst.Length < ArtDmxBytes) return 0;
        ArtId.CopyTo(dst);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[8..], OpDmx);
        BinaryPrimitives.WriteUInt16BigEndian(dst[10..], ProtVer);
        var s = (byte)(_seq[universe] == 255 ? 1 : _seq[universe] + 1);
        _seq[universe] = s;
        dst[12] = s; dst[13] = (byte)universe;
        dst[14] = (byte)(universe & 0xFF); dst[15] = 0;   // SubUni(하위 8비트) · Net(상위 7비트)
        BinaryPrimitives.WriteUInt16BigEndian(dst[16..], Slots);
        _dmx.AsSpan(universe * Slots, Slots).CopyTo(dst[ArtHeader..]);
        return ArtDmxBytes;
    }

    public static int WriteArtSync(Span<byte> dst)
    {
        if (dst.Length < ArtSyncBytes) return 0;
        ArtId.CopyTo(dst);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[8..], OpSync);
        BinaryPrimitives.WriteUInt16BigEndian(dst[10..], ProtVer);
        dst[12] = 0; dst[13] = 0;
        return ArtSyncBytes;
    }

    /// <summary>Enttec DMX USB Pro 프레임(0x7E · 라벨 6 · 길이 LE · 시작 코드 0 + 512 슬롯 · 0xE7).</summary>
    public int WriteEnttecFrame(int universe, Span<byte> dst)
    {
        if (dst.Length < EnttecBytes) return 0;
        dst[0] = 0x7E; dst[1] = 6;
        BinaryPrimitives.WriteUInt16LittleEndian(dst[2..], Slots + 1);
        dst[4] = 0;
        _dmx.AsSpan(universe * Slots, Slots).CopyTo(dst[5..]);
        dst[5 + Slots] = 0xE7;
        return EnttecBytes;
    }

    /// <summary>루프백 Art-Net 노드: 검증 → 수신 버퍼 → ArtSync 시 래치.</summary>
    public bool NodeReceive(ReadOnlySpan<byte> p)
    {
        if (p.Length < 12 || !p[..8].SequenceEqual(ArtId) || BinaryPrimitives.ReadUInt16BigEndian(p[10..]) < ProtVer) { NodeRejected++; return false; }
        var op = BinaryPrimitives.ReadUInt16LittleEndian(p[8..]);
        if (op == OpSync)
        {
            _node.AsSpan().CopyTo(_latched); NodeLatches++;
            return true;
        }
        if (op != OpDmx || p.Length < ArtHeader) { NodeRejected++; return false; }
        int len = BinaryPrimitives.ReadUInt16BigEndian(p[16..]);
        int uni = p[14] | ((p[15] & 0x7F) << 8);
        if (len < 2 || len > Slots || (len & 1) != 0 || p.Length != ArtHeader + len || uni >= Universes) { NodeRejected++; return false; }
        var seq = p[12];
        if (seq != 0 && _nodeSeq[uni] != 0 && seq != (byte)(_nodeSeq[uni] == 255 ? 1 : _nodeSeq[uni] + 1)) NodeSeqGaps++;
        _nodeSeq[uni] = seq;
        p.Slice(ArtHeader, len).CopyTo(_node.AsSpan(uni * Slots, len));
        NodeReceived++;
        return true;
    }
}
