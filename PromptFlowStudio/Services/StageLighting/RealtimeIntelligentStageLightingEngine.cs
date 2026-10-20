using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PromptFlowStudio.Services.StageLighting;

public enum FixtureType : byte { MovingHead, Wash, Strobe, Hazer }
public enum LightRole : byte { Key, Fill, Rim, Spot, Wash, Strobe, Hazer }

/// <summary>가상 라이트 릭 장치(상태 · 에르미트 세그먼트 · 광학 매개변수).</summary>
public struct LightFixture
{
    public string Name;
    public FixtureType Type;
    public LightRole Role;
    /// <summary>DMX 시작 주소(1-기반).</summary>
    public int Address;
    /// <summary>조준 대상: 0/1 배우 · 2 두 배우 중간 · 3 화자 · −1 고정.</summary>
    public int Target;
    public Vector3 Position, Aim, Beam, Color;
    public float Pan, Tilt, PanVel, TiltVel, GoalPan, GoalTilt, GoalPanVel, GoalTiltVel;
    public float Zoom, Gobo, GoboRot, Focus, Frost, Cto, Shaft;
    internal float P0, T0, MP0, MT0, P1, T1, MP1, MT1, SegT, SegDur;
}

/// <summary>[v17.42.0] 외부 동적 포인트 라이트(파이로 스파크 클러스터 · 화염광) — 위치 · 색(최대 채널 1) · 광도 cd · 기여 파티클 수.</summary>
public struct DynamicPointLight
{
    public Vector3 Position, Color;
    public float Candela;
    public int Sparks;
}

/// <summary>
/// [PromptFlow Studio v17.41.0] 💡 실시간 스마트 무빙헤드 추적 조명 &amp; Art-Net DMX512 엔진(<see cref="Plan"/>/<see cref="Commit"/> 무할당).
///   ▸ 릭: 무빙헤드 7(전면 트러스 2 · 후면 2 · 사이드 붐 2 · 탑 스팟 1 · 16 ch) · RGBW 워시 4(사이클로라마 · 6 ch) · 스트로브 1 · 헤이저 1 = 유니버스 0 · 140 ch.
///   ▸ 역할 배정(v17.39 카메라 프러스텀 추적): 배우별 림 = 카메라 시선 정반대(dot −1) · 키 = 카메라 측 45°(dot 0.7) · 필 = 카메라 축(dot 0.95) — 그리디 + 히스테리시스 0.25.
///   ▸ 팬/틸트(540°/270°): 두 해(yaw+270, +t / yaw+90, −t) × 360° 래핑 중 시간 최적 해 → 3차 에르미트 스플라인(현재 속도 연속 · 목표 각속도 리드) → 300/220 °/s 상한.
///   ▸ DMX: 16-bit 팬/틸트(coarse/fine) · 디머 · 셔터 · RGBW · CTO · 줌 · 고보/회전 · 포커스/프로스트 → float 레벨 → <see cref="Encode"/>(Vector&lt;T&gt; 포화 양자화 · 스칼라 비트 동일).
///   ▸ 스트로브/플리커는 콘솔 소프트웨어 체이스(디머 채널)로만 구현 — 광학 가드가 모든 광량 변화를 관측(픽스처 내장 스트로브 미사용).
///   ▸ Art-Net: 44 Hz 누산기 · ArtDmx(OpCode 0x5000 · ProtVer 14 · 시퀀스 1..255) · 루프백 노드 검증 · 선택적 UDP(사전 직렬화 SocketAddress · SendTo 무할당).
/// </summary>
public sealed class RealtimeIntelligentStageLightingEngine : IDisposable
{
    public const int Slots = 512, Fixtures = 13, MovingHeads = 7, TopSpot = 6, MhChannels = 16, ArtHeader = 18, ArtDmxBytes = ArtHeader + Slots, ArtNetPort = 6454;
    public const float PanRange = 540f, TiltLimit = 135f, PanSpeed = 300f, TiltSpeed = 220f, ArtNetHz = 44f, HeadY = 1.6f;
    public const float MaxBeamCd = 60000f, RefLux = 3000f, DisplayNits = 200f, SubjectArea = 0.25f, Hysteresis = 0.25f;
    public const ushort OpDmx = 0x5000, ProtVer = 14;
    public const int PointLightSlots = 8;
    public static ReadOnlySpan<byte> ArtId => "Art-Net\0"u8;
    public static readonly string[] GoboNames = { "오픈", "블라인드", "나뭇잎", "그리드", "브레이크업" };

    private static readonly double TickUs = 1_000_000.0 / Stopwatch.Frequency;
    private readonly LightFixture[] _fx = new LightFixture[Fixtures];
    private readonly float[] _lv = new float[Slots];
    private readonly byte[] _dmx = new byte[Slots], _node = new byte[Slots], _packet = new byte[ArtDmxBytes];
    private readonly short[] _park = new short[Slots];
    private readonly float[] _raw = new float[Fixtures], _out = new float[Fixtures], _lum = new float[Fixtures], _red = new float[Fixtures], _lux0 = new float[Fixtures], _lux1 = new float[Fixtures];
    private readonly bool[] _used = new bool[MovingHeads];
    private readonly DynamicPointLight[] _pl = new DynamicPointLight[PointLightSlots];
    private float _strobePhase, _flickPhase, _artAcc;
    private byte _seq;
    private Socket? _sock;
    private SocketAddress? _addr;

    public RealtimeIntelligentStageLightingEngine()
    {
        Array.Fill(_park, (short)-1);
        var a = 1;
        Mh(0, "MH 전면 L", new Vector3(-2.5f, 5.5f, 3.5f), ref a);
        Mh(1, "MH 전면 R", new Vector3(2.5f, 5.5f, 3.5f), ref a);
        Mh(2, "MH 후면 L", new Vector3(-2.5f, 5.5f, -3.5f), ref a);
        Mh(3, "MH 후면 R", new Vector3(2.5f, 5.5f, -3.5f), ref a);
        Mh(4, "MH 붐 L", new Vector3(-5f, 4f, 0f), ref a);
        Mh(5, "MH 붐 R", new Vector3(5f, 4f, 0f), ref a);
        Mh(6, "MH 탑 스팟", new Vector3(0f, 6.5f, 0f), ref a);
        for (var i = 0; i < 4; i++)
        {
            ref var w = ref _fx[MovingHeads + i];
            w.Name = i switch { 0 => "워시 1", 1 => "워시 2", 2 => "워시 3", _ => "워시 4" };
            w.Type = FixtureType.Wash; w.Role = LightRole.Wash; w.Target = -1; w.Address = a; a += 6;
            w.Position = new Vector3(-3f + 2f * i, 6f, -4.5f); w.Beam = new Vector3(0f, -0.5f, -0.866f); w.Color = Vector3.One;
        }
        ref var s = ref _fx[11];
        s.Name = "스트로브"; s.Type = FixtureType.Strobe; s.Role = LightRole.Strobe; s.Target = -1; s.Address = a; a += 2;
        s.Position = new Vector3(0f, 6f, 2f); s.Beam = -Vector3.UnitY; s.Color = Vector3.One;
        ref var h = ref _fx[12];
        h.Name = "헤이저"; h.Type = FixtureType.Hazer; h.Role = LightRole.Hazer; h.Target = -1; h.Address = a; a += 2;
        h.Position = new Vector3(-5f, 0.3f, -4f); h.Beam = Vector3.UnitX;
        UsedChannels = a - 1;
    }

    private void Mh(int i, string name, Vector3 p, ref int addr)
    {
        ref var f = ref _fx[i];
        f.Name = name; f.Type = FixtureType.MovingHead; f.Role = i == TopSpot ? LightRole.Spot : LightRole.Fill; f.Target = i == TopSpot ? 3 : 2;
        f.Address = addr; addr += MhChannels;
        f.Position = p; f.Pan = f.P1 = f.GoalPan = 270f; f.Tilt = f.T1 = f.GoalTilt = 0f; f.SegDur = 1f; f.SegT = 1f;
        f.Beam = -Vector3.UnitY; f.Zoom = 20f; f.Color = Vector3.One; f.Focus = 0.5f;
    }

    public int UsedChannels { get; }
    public ref readonly LightFixture Fixture(int i) => ref _fx[i];
    public Vector3 Camera { get; private set; } = new(0f, 1.7f, 6f);
    public Vector3 Actor0 { get; private set; } = new(-1.2f, HeadY, 0f);
    public Vector3 Actor1 { get; private set; } = new(1.2f, HeadY, 0f);
    public bool UseSimd { get; set; } = true;
    public byte Dmx(int ch) => _dmx[ch];
    public byte NodeDmx(int ch) => _node[ch];
    public ReadOnlySpan<byte> DmxFrame => _dmx;
    public ReadOnlySpan<byte> NodeFrame => _node;
    public ReadOnlySpan<byte> LastPacket => _packet;
    public short Parked(int ch) => _park[ch];
    public int ParkedCount { get; private set; }
    public float RawLevel(int i) => _raw[i];
    public float OutLevel(int i) => _out[i];

    // 광학 가드 입력(픽스처별 단위 디머당 화면 휘도 cd/m² · 포화 적색 휘도 · 배우 조도 lux) · 출력
    public Span<float> Raw => _raw;
    public Span<float> Out => _out;
    public ReadOnlySpan<float> Luminance => _lum;
    public ReadOnlySpan<float> RedLuminance => _red;
    public ReadOnlySpan<float> Lux0 => _lux0;
    public ReadOnlySpan<float> Lux1 => _lux1;

    // [v17.42.0] 파이로 동적 포인트 라이트(역제곱 배우 조도 · 무할당 고정 슬롯)
    public int PointLightCount { get; private set; }
    public ref readonly DynamicPointLight PointLight(int i) => ref _pl[i];
    public float PointLux0 { get; private set; }
    public float PointLux1 { get; private set; }

    public void SetPointLights(ReadOnlySpan<DynamicPointLight> lights)
    {
        var n = Math.Min(lights.Length, PointLightSlots);
        lights[..n].CopyTo(_pl);
        PointLightCount = n;
    }

    public long Frames { get; private set; }
    public long PacketsSent { get; private set; }
    public long UdpSent { get; private set; }
    public long TxErrors { get; private set; }
    public long BytesSent { get; private set; }
    public long NodeFrames { get; private set; }
    public long NodeRejected { get; private set; }
    public long RoleSwaps { get; private set; }
    public float MaxPanStep { get; private set; }
    public float MaxTiltStep { get; private set; }
    public double LastPlanUs { get; private set; }
    public double LastCommitUs { get; private set; }
    public string? ArtNetTarget { get; private set; }
    public bool ArtNetLive => _sock is not null;

    // ══════════════════════ 1) 계획(역할 · 조준 · 광량) ══════════════════════

    /// <summary>배우 무대 좌표(x, z) · v17.39 프로그램 카메라 · 화자 · 룩(v17.41 합성기) · 가드 승인 스트로브 Hz → 픽스처 상태 · 가드 입력.</summary>
    public void Plan(Vector2 a0, Vector2 a1, int program, int speaker, EmotionalVolumetricLightBeamSynthesizer look, float strobeHz, float dt)
    {
        var t0 = Stopwatch.GetTimestamp();
        Frames++;
        Actor0 = new Vector3(a0.X, HeadY, a0.Y); Actor1 = new Vector3(a1.X, HeadY, a1.Y);
        Camera = CameraFor(program, Actor0, Actor1);
        Array.Clear(_used);
        AssignRole(LightRole.Rim, 0, -1f);
        AssignRole(LightRole.Rim, 1, -1f);
        AssignRole(LightRole.Key, 0, 0.7f);
        AssignRole(LightRole.Key, 1, 0.7f);
        AssignRole(LightRole.Fill, 2, 0.95f);
        AssignRole(LightRole.Fill, 2, 0.95f);

        _flickPhase += dt * look.FlickerHz; _flickPhase -= MathF.Floor(_flickPhase);
        var flick = 1f - look.FlickerDepth * (0.5f + 0.5f * MathF.Sin(MathF.Tau * _flickPhase));
        if (strobeHz > 0f) { _strobePhase += dt * strobeHz; _strobePhase -= MathF.Floor(_strobePhase); } else _strobePhase = 0f;
        var strobeOn = strobeHz > 0f && _strobePhase < 0.5f;

        for (var i = 0; i < MovingHeads; i++)
        {
            ref var f = ref _fx[i];
            f.Aim = TargetPoint(f.Target, speaker);
            Track(ref f, dt);
            switch (f.Role)
            {
                case LightRole.Key: _raw[i] = look.KeyLevel; f.Color = look.KeyColor; f.Zoom = look.KeyZoom; break;
                case LightRole.Rim: _raw[i] = look.RimLevel; f.Color = look.RimColor; f.Zoom = look.RimZoom; break;
                case LightRole.Fill: _raw[i] = look.FillLevel; f.Color = look.FillColor; f.Zoom = look.FillZoom; break;
                default: _raw[i] = speaker >= 0 ? look.SpotLevel : look.SpotLevel * 0.4f; f.Color = look.KeyColor; f.Zoom = look.SpotZoom; break;
            }
            _raw[i] *= flick;
            f.Gobo = f.Role is LightRole.Rim or LightRole.Spot ? look.Gobo : 0;
            f.GoboRot = f.Gobo > 0 ? look.GoboRotDps : 0f;
            f.Cto = look.Cto; f.Focus = 0.35f + 0.3f * look.Tension; f.Frost = f.Role == LightRole.Fill ? 0.6f : 0f;
            f.Shaft = look.ShaftStrength(f.Beam, f.Position, Camera, 1f, 7f); // 단위 디머당 가시도(휘도 선형성 유지)
            Photometry(i, in f);
            _lum[i] += 12f * f.Shaft;
        }
        for (var i = MovingHeads; i < MovingHeads + 4; i++)
        {
            ref var w = ref _fx[i];
            _raw[i] = look.WashLevel * flick; w.Color = look.WashColor;
            _lux0[i] = _lux1[i] = 60f; _lum[i] = 10f;
            _red[i] = _lum[i] * Redness(w.Color);
        }
        ref var st = ref _fx[11];
        _raw[11] = strobeOn ? look.StrobeLevel : 0f; st.Color = Vector3.One;
        _lux0[11] = _lux1[11] = 900f; _lum[11] = 150f; _red[11] = 0f;
        _raw[12] = look.HazerLevel; _lux0[12] = _lux1[12] = _lum[12] = _red[12] = 0f;

        float pl0 = 0f, pl1 = 0f;
        for (var i = 0; i < PointLightCount; i++)
        {
            ref readonly var l = ref _pl[i];
            pl0 += l.Candela / MathF.Max(Vector3.DistanceSquared(l.Position, Actor0), 0.25f);
            pl1 += l.Candela / MathF.Max(Vector3.DistanceSquared(l.Position, Actor1), 0.25f);
        }
        PointLux0 = pl0; PointLux1 = pl1;

        for (var i = 0; i < Fixtures; i++)
        {
            var p = _park[DimmerChannel(i)];
            if (p >= 0) _raw[i] = p / 255f;
            _raw[i] = Math.Clamp(_raw[i], 0f, 1f);
        }
        LastPlanUs = (Stopwatch.GetTimestamp() - t0) * TickUs;
    }

    private static float Redness(Vector3 c)
    {
        var s = c.X + c.Y + c.Z;
        return s < 1e-4f ? 0f : SmoothStep(0.7f, 0.8f, c.X / s);
    }

    /// <summary>배우별 조도(피크 칸델라 × 빔 감쇠 / d²) → 화면 휘도(피사체 화면 점유 25 % · 3000 lux = 200 cd/m²) · 포화 적색 휘도.</summary>
    private void Photometry(int i, in LightFixture f)
    {
        var half = 0.5f * f.Zoom * (MathF.PI / 180f);
        var cd = MathF.Min(MaxBeamCd * (144f / (f.Zoom * f.Zoom)), 4f * MaxBeamCd);
        _lux0[i] = Illum(in f, Actor0, half, cd);
        _lux1[i] = Illum(in f, Actor1, half, cd);
        _lum[i] = DisplayNits * SubjectArea * (_lux0[i] + _lux1[i]) * 0.5f / RefLux;
        _red[i] = _lum[i] * Redness(f.Color);
    }

    private static float Illum(in LightFixture f, Vector3 target, float half, float cd)
    {
        var d = target - f.Position;
        var d2 = MathF.Max(d.LengthSquared(), 0.25f);
        var c = Vector3.Dot(f.Beam, d / MathF.Sqrt(d2));
        return cd * SmoothStep(MathF.Cos(half * 1.6f), MathF.Cos(half * 0.6f), c) / d2;
    }

    private Vector3 TargetPoint(int target, int speaker)
    {
        var mid = 0.5f * (Actor0 + Actor1);
        return target switch
        {
            0 => Actor0,
            1 => Actor1,
            3 => speaker == 0 ? Actor0 : speaker == 1 ? Actor1 : mid,
            _ => new Vector3(mid.X, 1.3f, mid.Z),
        };
    }

    /// <summary>v17.39 4 캠(마스터/OTS L/OTS R/액션) 위치 — 모두 관객 측(+z) 반평면(180° 라인 준수).</summary>
    public static Vector3 CameraFor(int program, Vector3 a0, Vector3 a1)
    {
        var mid = 0.5f * (a0 + a1);
        var ax = new Vector3(a1.X - a0.X, 0f, a1.Z - a0.Z);
        var len = ax.Length();
        ax = len > 1e-3f ? ax / len : Vector3.UnitX;
        var n = new Vector3(-ax.Z, 0f, ax.X);
        if (n.Z < 0f) n = -n;
        var p = program switch
        {
            1 => a1 + ax * 1.1f + n * 0.6f,
            2 => a0 - ax * 1.1f + n * 0.6f,
            3 => mid + n * 3f + ax * 2.5f,
            _ => mid + n * 5.5f,
        };
        return new Vector3(p.X, program == 3 ? 1.2f : 1.65f, p.Z);
    }

    private void AssignRole(LightRole role, int target, float want)
    {
        var t = TargetPoint(target, -1);
        var v = Flat(t - Camera);
        var best = -1; var bestS = float.NegativeInfinity;
        for (var i = 0; i < MovingHeads; i++)
        {
            if (i == TopSpot || _used[i]) continue;
            ref readonly var f = ref _fx[i];
            var s = -MathF.Abs(Vector3.Dot(Flat(t - f.Position), v) - want);
            if (f.Role == role && f.Target == target) s += Hysteresis;
            if (s > bestS) { bestS = s; best = i; }
        }
        if (best < 0) return;
        _used[best] = true;
        ref var b = ref _fx[best];
        if (b.Role != role || b.Target != target) { RoleSwaps++; b.Role = role; b.Target = target; }
    }

    private static Vector3 Flat(Vector3 d)
    {
        d.Y = 0f;
        var l = d.Length();
        return l > 1e-4f ? d / l : Vector3.UnitZ;
    }

    // ══════════════════════ 팬/틸트 · 에르미트 ══════════════════════

    /// <summary>(팬, 틸트) → 빔 방향(h = 팬 − 270° · 틸트 0 = 수직 하향).</summary>
    public static Vector3 Direction(float pan, float tilt)
    {
        var h = (pan - 270f) * (MathF.PI / 180f);
        var t = tilt * (MathF.PI / 180f);
        var st = MathF.Sin(t);
        return new Vector3(MathF.Sin(h) * st, -MathF.Cos(t), MathF.Cos(h) * st);
    }

    /// <summary>조준 해: 두 분기 × 360° 래핑 중 현재 자세에서 이동 시간 최소(분기 전환 0.05 s 벌점).</summary>
    public static void Solve(Vector3 fixture, Vector3 aim, float curPan, float curTilt, out float pan, out float tilt)
    {
        var d = aim - fixture;
        var len = MathF.Max(d.Length(), 1e-4f);
        var yaw = MathF.Atan2(d.X, d.Z) * (180f / MathF.PI);
        var t = MathF.Min(MathF.Acos(Math.Clamp(-d.Y / len, -1f, 1f)) * (180f / MathF.PI), TiltLimit);
        pan = curPan; tilt = curTilt;
        var best = float.PositiveInfinity;
        for (var sgn = 1; sgn >= -1; sgn -= 2)
        {
            var tl = sgn * t;
            var b = yaw + (sgn > 0 ? 270f : 90f);
            for (var k = -2; k <= 2; k++)
            {
                var p = b + 360f * k;
                if (p < 0f || p > PanRange) continue;
                var c = MathF.Max(MathF.Abs(p - curPan) / PanSpeed, MathF.Abs(tl - curTilt) / TiltSpeed);
                if (MathF.Abs(curTilt) > 1f && MathF.Sign(curTilt) != sgn) c += 0.05f;
                if (c < best) { best = c; pan = p; tilt = tl; }
            }
        }
    }

    private void Track(ref LightFixture f, float dt)
    {
        Solve(f.Position, f.Aim, f.Pan, f.Tilt, out var gp, out var gt);
        var jump = MathF.Abs(gp - f.GoalPan) > 30f || MathF.Abs(gt - f.GoalTilt) > 30f;
        var vp = jump ? 0f : (gp - f.GoalPan) / dt;
        var vt = jump ? 0f : (gt - f.GoalTilt) / dt;
        f.GoalPanVel = Math.Clamp(f.GoalPanVel + (vp - f.GoalPanVel) * 0.25f, -0.5f * PanSpeed, 0.5f * PanSpeed);
        f.GoalTiltVel = Math.Clamp(f.GoalTiltVel + (vt - f.GoalTiltVel) * 0.25f, -0.5f * TiltSpeed, 0.5f * TiltSpeed);
        if (jump) { f.GoalPanVel = 0f; f.GoalTiltVel = 0f; }
        f.GoalPan = gp; f.GoalTilt = gt;
        if (MathF.Abs(gp - f.P1) > 0.25f || MathF.Abs(gt - f.T1) > 0.25f || f.SegT >= f.SegDur)
        {
            var dur = Math.Clamp(MathF.Max(MathF.Abs(gp - f.Pan) / PanSpeed, MathF.Abs(gt - f.Tilt) / TiltSpeed) * 1.8f + 0.12f, 0.15f, 3f);
            f.P0 = f.Pan; f.T0 = f.Tilt; f.MP0 = f.PanVel * dur; f.MT0 = f.TiltVel * dur;
            f.P1 = gp; f.T1 = gt; f.MP1 = f.GoalPanVel * dur; f.MT1 = f.GoalTiltVel * dur;
            f.SegT = 0f; f.SegDur = dur;
        }
        f.SegT = MathF.Min(f.SegT + dt, f.SegDur);
        var s = f.SegT / f.SegDur;
        var np = Hermite(f.P0, f.MP0, f.P1, f.MP1, s);
        var nt = Hermite(f.T0, f.MT0, f.T1, f.MT1, s);
        var dp = Math.Clamp(np - f.Pan, -PanSpeed * dt, PanSpeed * dt);
        var dtl = Math.Clamp(nt - f.Tilt, -TiltSpeed * dt, TiltSpeed * dt);
        var pan = Math.Clamp(f.Pan + dp, 0f, PanRange);
        var tilt = Math.Clamp(f.Tilt + dtl, -TiltLimit, TiltLimit);
        MaxPanStep = MathF.Max(MaxPanStep, MathF.Abs(pan - f.Pan));
        MaxTiltStep = MathF.Max(MaxTiltStep, MathF.Abs(tilt - f.Tilt));
        f.PanVel = (pan - f.Pan) / dt; f.TiltVel = (tilt - f.Tilt) / dt;
        f.Pan = pan; f.Tilt = tilt;
        f.Beam = Direction(pan, tilt);
    }

    /// <summary>3차 에르미트(h00 p0 + h10 m0 + h01 p1 + h11 m1).</summary>
    public static float Hermite(float p0, float m0, float p1, float m1, float s)
    {
        var s2 = s * s; var s3 = s2 * s;
        return (2f * s3 - 3f * s2 + 1f) * p0 + (s3 - 2f * s2 + s) * m0 + (-2f * s3 + 3f * s2) * p1 + (s3 - s2) * m1;
    }

    /// <summary>빔 축과 대상 방향의 각도 오차(°).</summary>
    public float AimErrorDeg(int i)
    {
        ref readonly var f = ref _fx[i];
        var d = f.Aim - f.Position;
        var c = Vector3.Dot(f.Beam, d / MathF.Max(d.Length(), 1e-4f));
        return MathF.Acos(Math.Clamp(c, -1f, 1f)) * (180f / MathF.PI);
    }

    private static float SmoothStep(float a, float b, float x)
    {
        var t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    // ══════════════════════ 2) 커밋(DMX 인코딩 · Art-Net) ══════════════════════

    /// <summary>가드 출력(<see cref="Out"/>) → 512 ch 레벨 → 바이트 인코딩 → 44 Hz ArtDmx.</summary>
    public void Commit(float dt)
    {
        var t0 = Stopwatch.GetTimestamp();
        for (var i = 0; i < Fixtures; i++)
        {
            ref readonly var f = ref _fx[i];
            var b = f.Address - 1;
            var o = Math.Clamp(_out[i], 0f, 1f);
            switch (f.Type)
            {
                case FixtureType.MovingHead:
                {
                    Write16(b, f.Pan / PanRange);
                    Write16(b + 2, (f.Tilt + TiltLimit) / (2f * TiltLimit));
                    _lv[b + 4] = o; _lv[b + 5] = 32f / 255f;
                    Rgbw(b + 6, f.Color);
                    _lv[b + 10] = f.Cto;
                    _lv[b + 11] = Math.Clamp((f.Zoom - 5f) / 45f, 0f, 1f);
                    _lv[b + 12] = f.Gobo * 32f / 255f;
                    _lv[b + 13] = Math.Clamp(0.5f + f.GoboRot / 400f, 0f, 1f);
                    _lv[b + 14] = f.Focus; _lv[b + 15] = f.Frost;
                    break;
                }
                case FixtureType.Wash:
                    _lv[b] = o; Rgbw(b + 1, f.Color); _lv[b + 5] = 0f;
                    break;
                case FixtureType.Strobe:
                    _lv[b] = o; _lv[b + 1] = 0f;
                    break;
                default:
                    _lv[b] = o; _lv[b + 1] = o > 0.01f ? 0.6f : 0f;
                    break;
            }
        }
        if (ParkedCount > 0)
            for (var c = 0; c < Slots; c++) if (_park[c] >= 0 && !IsIntensity(c)) _lv[c] = _park[c] / 255f;
        if (UseSimd) Encode(_lv, _dmx); else EncodeScalar(_lv, _dmx);

        _artAcc += dt;
        const float period = 1f / ArtNetHz;
        if (_artAcc >= period)
        {
            _artAcc -= period;
            if (_artAcc > period) _artAcc = 0f;
            var len = WriteArtDmx(0, _packet);
            PacketsSent++; BytesSent += len;
            NodeReceive(_packet.AsSpan(0, len));
            if (_sock is { } s && _addr is { } addr)
            {
                try { s.SendTo(_packet.AsSpan(0, len), SocketFlags.None, addr); UdpSent++; }
                catch (SocketException) { TxErrors++; }
                catch (ObjectDisposedException) { TxErrors++; }
            }
        }
        LastCommitUs = (Stopwatch.GetTimestamp() - t0) * TickUs;
    }

    private void Write16(int ch, float norm)
    {
        var v = (int)(Math.Clamp(norm, 0f, 1f) * 65535f + 0.5f);
        _lv[ch] = (v >> 8) / 255f; _lv[ch + 1] = (v & 0xFF) / 255f;
    }

    private void Rgbw(int ch, Vector3 c)
    {
        var w = MathF.Min(c.X, MathF.Min(c.Y, c.Z));
        _lv[ch] = Math.Clamp(c.X - w, 0f, 1f); _lv[ch + 1] = Math.Clamp(c.Y - w, 0f, 1f); _lv[ch + 2] = Math.Clamp(c.Z - w, 0f, 1f); _lv[ch + 3] = Math.Clamp(w, 0f, 1f);
    }

    /// <summary>광량 채널(디머) 여부 — 파킹 시 가드 앞단(Raw)에 주입.</summary>
    public bool IsIntensity(int ch)
    {
        for (var i = 0; i < Fixtures; i++) if (DimmerChannel(i) == ch) return true;
        return false;
    }

    /// <summary>픽스처 디머 채널(0-기반 · 무빙헤드 = 시작 + 4).</summary>
    public int DimmerChannel(int i) => _fx[i].Address - 1 + (_fx[i].Type == FixtureType.MovingHead ? 4 : 0);

    /// <summary>채널 파킹(0..255) · −1 = 해제.</summary>
    public void Park(int ch, int value)
    {
        if ((uint)ch >= Slots) return;
        var was = _park[ch] >= 0;
        _park[ch] = (short)(value < 0 ? -1 : Math.Min(value, 255));
        if (was && value < 0) ParkedCount--;
        else if (!was && value >= 0) ParkedCount++;
    }

    public void ReleaseAll() { Array.Fill(_park, (short)-1); ParkedCount = 0; }

    /// <summary>float 레벨(유한값 계약) → DMX 바이트(×255 + 0.5 · [0, 255] 포화 · 절삭). Vector&lt;T&gt; 4×float → 2×ushort → byte 협소화 · 꼬리 스칼라.</summary>
    public static void Encode(ReadOnlySpan<float> src, Span<byte> dst)
    {
        var n = Math.Min(src.Length, dst.Length);
        var i = 0;
        if (Vector.IsHardwareAccelerated && BitConverter.IsLittleEndian)
        {
            var w = Vector<float>.Count;
            var step = w * 4;
            ref var s0 = ref MemoryMarshal.GetReference(src);
            ref var d0 = ref MemoryMarshal.GetReference(dst);
            for (; i <= n - step; i += step)
            {
                var q0 = Quant(Load(ref s0, i));
                var q1 = Quant(Load(ref s0, i + w));
                var q2 = Quant(Load(ref s0, i + 2 * w));
                var q3 = Quant(Load(ref s0, i + 3 * w));
                var lo = Vector.Narrow(q0, q1);
                var hi = Vector.Narrow(q2, q3);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref d0, i), Vector.Narrow(lo, hi));
            }
        }
        for (; i < n; i++) dst[i] = Quant(src[i]);
    }

    public static void EncodeScalar(ReadOnlySpan<float> src, Span<byte> dst)
    {
        var n = Math.Min(src.Length, dst.Length);
        for (var i = 0; i < n; i++) dst[i] = Quant(src[i]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Quant(float v)
    {
        var x = v * 255f + 0.5f;
        x = x > 0f ? x : 0f;
        x = x < 255f ? x : 255f;
        return (byte)(int)x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector<float> Load(ref float s0, int i) => Unsafe.ReadUnaligned<Vector<float>>(ref Unsafe.As<float, byte>(ref Unsafe.Add(ref s0, i)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector<uint> Quant(Vector<float> v)
    {
        var x = v * new Vector<float>(255f) + new Vector<float>(0.5f);
        x = Vector.ConditionalSelect(Vector.GreaterThan(x, Vector<float>.Zero), x, Vector<float>.Zero);
        x = Vector.ConditionalSelect(Vector.LessThan(x, new Vector<float>(255f)), x, new Vector<float>(255f));
        return Vector.AsVectorUInt32(Vector.ConvertToInt32(x));
    }

    /// <summary>ArtDmx(Art-Net\0 · OpCode 0x5000 LE · ProtVer 14 BE · 시퀀스 1..255 · Physical · SubUni · Net · 길이 512 BE · 데이터).</summary>
    public int WriteArtDmx(int universe, Span<byte> dst)
    {
        if (dst.Length < ArtDmxBytes) return 0;
        ArtId.CopyTo(dst);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[8..], OpDmx);
        BinaryPrimitives.WriteUInt16BigEndian(dst[10..], ProtVer);
        _seq = (byte)(_seq == 255 ? 1 : _seq + 1);
        dst[12] = _seq; dst[13] = 0;
        dst[14] = (byte)(universe & 0xFF); dst[15] = (byte)((universe >> 8) & 0x7F);
        BinaryPrimitives.WriteUInt16BigEndian(dst[16..], Slots);
        _dmx.AsSpan().CopyTo(dst[ArtHeader..]);
        return ArtDmxBytes;
    }

    /// <summary>루프백 Art-Net 노드(가상 콘솔 수신부): 헤더 · OpCode · 버전 · 길이 검증 → 유니버스 버퍼.</summary>
    public bool NodeReceive(ReadOnlySpan<byte> p)
    {
        if (p.Length < ArtHeader || !p[..8].SequenceEqual(ArtId) || BinaryPrimitives.ReadUInt16LittleEndian(p[8..]) != OpDmx || BinaryPrimitives.ReadUInt16BigEndian(p[10..]) < ProtVer)
        {
            NodeRejected++;
            return false;
        }
        var len = BinaryPrimitives.ReadUInt16BigEndian(p[16..]);
        if (len is < 2 or > Slots || p.Length < ArtHeader + len) { NodeRejected++; return false; }
        p.Slice(ArtHeader, len).CopyTo(_node);
        NodeFrames++;
        return true;
    }

    // ══════════════════════ UDP(비핫패스 제어) ══════════════════════

    /// <summary>실물 콘솔/노드로 Art-Net UDP 송출 개시(IPv4/IPv6 리터럴 · 브로드캐스트 허용).</summary>
    public bool EnableArtNet(string host, int port = ArtNetPort)
    {
        if (!IPAddress.TryParse(host, out var ip) || port is <= 0 or > 65535) return false;
        DisableArtNet();
        var s = new Socket(ip.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            if (OperatingSystem.IsWindows()) s.IOControl(unchecked((int)0x9800000C), new byte[4], null);
        }
        catch (SocketException) { }
        catch (PlatformNotSupportedException) { }
        if (ip.AddressFamily == AddressFamily.InterNetwork) s.EnableBroadcast = true;
        _addr = new IPEndPoint(ip, port).Serialize();
        _sock = s;
        ArtNetTarget = host + ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    public void DisableArtNet()
    {
        var s = Interlocked.Exchange(ref _sock, null);
        _addr = null; ArtNetTarget = null;
        s?.Dispose();
    }

    public void ResetMetrics() { PacketsSent = UdpSent = TxErrors = BytesSent = NodeFrames = NodeRejected = RoleSwaps = 0; MaxPanStep = MaxTiltStep = 0f; }

    public void Dispose() => DisableArtNet();
}
